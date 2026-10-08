using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Relic.Core.Util;

namespace Relic.Core.Download;

public readonly record struct DownloadProgress(long Received, long? Total)
{
    public double? Fraction => Total is > 0 ? (double)Received / Total!.Value : null;
}

/// <summary>
/// The server answered 2xx but with a web page instead of the file — a Google Drive quota/virus-scan
/// interstitial, a captive portal, an expired share. A distinct type because the caller must react by
/// switching SOURCE, not by retrying the same url: a retry gets the same page, forever.
/// </summary>
public sealed class SourceServedPageException : Exception
{
    /// <summary>True when the page is specifically Drive's daily-quota refusal, which no retry and no
    /// alternative Drive url can get around — only another host, or waiting out the day.</summary>
    public bool IsQuota { get; }

    public SourceServedPageException(string message, bool isQuota) : base(message) => IsQuota = isQuota;
}

/// <summary>Result of a small range-probe used to validate a source without downloading it.</summary>
public sealed record RangeProbe(int Status, string? ContentType, long? Total, byte[] Head, bool SupportsRange)
{
    public bool LooksLikeZip => Head.Length >= 2 && Head[0] == 0x50 && Head[1] == 0x4B; // "PK"
    public bool IsHtml =>
        (ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Head.Length > 0 && (char)Head[0] == '<');
}

/// <summary>
/// Resumable HTTP downloader for the official CDN (plain public resumable GETs) and Google Drive
/// (one-shot confirm-token URL, with an HTML-interstitial fallback). Large files require a 64-bit
/// process; ZIP64 extraction is handled elsewhere.
/// </summary>
public static class Downloader
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Relic/1.0";

    /// <summary>
    /// No bytes for this long on an open connection = the connection is dead, whatever the socket says.
    /// Nothing else bounds a read: <see cref="HttpClient.Timeout"/> is infinite on purpose (a 30 GB
    /// transfer has no sane total), so without this watchdog a silently-dropped connection would hang the
    /// install forever and never reach the retry or the source fallback. It also bounds the connect
    /// and header wait — deliberately instead of <c>SocketsHttpHandler.ConnectTimeout</c>, which
    /// surfaces as a <see cref="TaskCanceledException"/> that every layer above reads as the USER's
    /// cancel (no resume, no retry, no fallback). The OS connect timeout (~21 s)
    /// still applies underneath and arrives as an <see cref="HttpRequestException"/>, which is retried.
    /// </summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(75);

    /// <summary>How many reconnects IN A ROW without meaningful progress one call may make after a stall
    /// or a mid-body network error before giving up. A reconnect that then delivers at least
    /// <see cref="ProgressResetBytes"/> resets the streak: a link that drops every few minutes but
    /// resumes fine is healthy, not dead, and must not fail at the 7th drop of a 45-minute transfer.</summary>
    public static int MaxResumes { get; set; } = 6;

    /// <summary>Bytes a reconnect must deliver for the resume streak to count as progress (and reset).</summary>
    public static long ProgressResetBytes { get; set; } = 8 << 20;

    /// <summary>
    /// Reconnects allowed when the attempt delivered NOTHING (dead at connect/TLS/headers). One, not
    /// <see cref="MaxResumes"/>: seven 75 s waits against a host that never answers would be ~9 minutes
    /// of "0 B / 15 GB" before the caller's own retry and source fallback get a say. A PINNED edge that
    /// delivers nothing gets none at all — the caller re-races in ~2 s, which beats any wait.
    /// </summary>
    public static int MaxNoProgressResumes { get; set; } = 1;

    /// <summary>Pause before a reconnect, so a flapping link is not hammered. Doubles on every
    /// consecutive connect-level failure (2, 4, 8, 16, 16… s) while <see cref="NoProgressWindow"/> lasts.</summary>
    public static TimeSpan ResumeDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a link may be DOWN (reconnects that fail at once — no route, reset, DNS gone: a Wi-Fi
    /// roam, a router reboot, a VPN flap) before the call gives up. A failure that fails in milliseconds
    /// is not a stall that consumed 75 s, so it gets time rather than a count: with the doubling delay
    /// that is ~5 reconnects inside the window. Pinned edges still surface at once (the caller re-races).
    /// </summary>
    public static TimeSpan NoProgressWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Bound on <see cref="ProbeAsync"/>: a source that accepts the connection and then never
    /// answers must become a source failure (→ next host), not an install that sits there forever.</summary>
    public static TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <param name="pinIp">Connect every request for <paramref name="pinHost"/> to this address instead
    /// of whatever DNS answers (see <see cref="EdgeRace"/>). TLS still negotiates with — and validates
    /// the certificate for — the real host name; only the TCP endpoint is chosen by us. Requests to any
    /// other host (a redirect target) resolve normally.</param>
    public static HttpClient CreateClient(IPAddress? pinIp = null, string? pinHost = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.None, // never gzip a zip
            // Bypass any system proxy: never route a multi-GB download through Fiddler (RAM) or a stale proxy.
            UseProxy = false,
            Proxy = null,
            // No ConnectTimeout here — see StallTimeout.
        };
        if (pinIp is not null && !string.IsNullOrEmpty(pinHost))
        {
            handler.ConnectCallback = async (ctx, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    if (string.Equals(ctx.DnsEndPoint.Host, pinHost, StringComparison.OrdinalIgnoreCase))
                        await socket.ConnectAsync(new IPEndPoint(pinIp, ctx.DnsEndPoint.Port), ct);
                    else
                        await socket.ConnectAsync(ctx.DnsEndPoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    /// <summary>Google Drive one-shot direct-download URL (works for large files in a single request).</summary>
    public static string DriveDirectUrl(string fileId) =>
        $"https://drive.usercontent.google.com/download?id={fileId}&export=download&confirm=t";

    /// <summary>Extract a Drive file id from any of the common Drive URL shapes.</summary>
    public static string? DriveFileId(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"(?:/d/|[?&]id=)([A-Za-z0-9_-]{20,})");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Fetch just the first <paramref name="maxBytes"/> bytes via a Range request, to validate a source.</summary>
    public static async Task<RangeProbe> ProbeAsync(string url, int maxBytes = 1024, CancellationToken ct = default)
    {
        // Bounded: a source that accepts the TCP connection and then never answers is a SOURCE failure
        // (next host), surfaced as an IOException — never as a cancel, which the caller would read as
        // the user's, and never as a hang, which would have no way out at all.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            using var http = CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, maxBytes - 1);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            long? total = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength;
            string? ctype = resp.Content.Headers.ContentType?.MediaType;

            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[maxBytes];
            int read = 0, n;
            while (read < buf.Length && (n = await s.ReadAsync(buf.AsMemory(read), cts.Token)) > 0) read += n;

            return new RangeProbe((int)resp.StatusCode, ctype, total,
                buf.AsSpan(0, read).ToArray(), resp.StatusCode == HttpStatusCode.PartialContent);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException(L.T("core.download.probeTimeout", new { seconds = ProbeTimeout.TotalSeconds.ToString("0") }), ex);
        }
    }

    /// <summary>
    /// Download <paramref name="url"/> to <paramref name="dest"/>, resuming if a partial file exists
    /// (Range + append; falls back to a full restart if the server ignores Range).
    /// </summary>
    /// <param name="expectZip">Refuse a fresh response whose first bytes are not "PK". Opt-in because
    /// this downloader is not zip-only — the spike drives it with arbitrary bytes — but every real
    /// caller downloads an archive and wants the bad response caught BEFORE anything is written.</param>
    /// <param name="startFresh">Ignore whatever is already at <paramref name="dest"/> and download the
    /// whole file again. This is how a caller switches HOST without destroying the old copy up front:
    /// the existing bytes are not resumed (they came from a different copy and must never be spliced),
    /// but they are also not deleted — the destination is opened, and therefore truncated, only after
    /// the new host's response has passed the page/zip sniff below. A host that answers with a quota
    /// page or a 403 leaves the previous partial exactly where it was, so a failed fallback costs
    /// nothing instead of costing the whole transfer.</param>
    /// <param name="edge">Pin every connection to this edge (see <see cref="EdgeRace.PickAsync"/>).</param>
    /// <remarks>
    /// Mid-transfer failures are handled HERE, not by the caller: a stall (no bytes for
    /// <see cref="StallTimeout"/>), a connection dropped by the network, or a body that ended short of
    /// its Content-Length reconnects and resumes with a Range request from the bytes already on disk —
    /// up to <see cref="MaxResumes"/> times in a row without progress (<see cref="MaxNoProgressResumes"/>
    /// when the attempt delivered nothing at all, and not even once on a pinned edge, whose death the
    /// caller answers with a new race). Only then does the error surface, always as an
    /// <see cref="IOException"/>/<see cref="HttpRequestException"/>, never as a cancel. A refusal (error
    /// page, 4xx/5xx) is never retried here — that is the caller's source-fallback decision.
    /// </remarks>
    public static async Task DownloadResumableAsync(
        string url, string dest, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default,
        bool expectZip = false, bool startFresh = false, EdgeChoice? edge = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var http = CreateClient(edge?.Ip, edge?.Host);

        // Once THIS call has truncated and written the destination, the bytes on disk are this host's
        // and every later reconnect must resume them — even when the caller asked for a fresh start.
        bool fresh = startFresh;
        int streak = 0;           // reconnects in a row without ProgressResetBytes of progress
        int zeroStreak = 0;       // reconnects in a row that delivered nothing at all
        long zeroSince = 0;       // when the current zero-delivery streak began (NoProgressWindow)
        bool rangeIgnored = false; // the host answered 200 to a Range request: it cannot be resumed

        for (int attempt = 0; ; attempt++)
        {
            long existing = !fresh && File.Exists(dest) ? new FileInfo(dest).Length : 0;
            long attemptTick = Environment.TickCount64;

            // Stall watchdog: a linked token that fires when no byte has arrived for StallTimeout.
            // Covers the connect and header wait as well as the body — all can hang on a half-dead link.
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            long lastActivity = Environment.TickCount64;
            int stalledFlag = 0;
            var watchdog = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(1000, stallCts.Token);
                        if (Environment.TickCount64 - Volatile.Read(ref lastActivity) > StallTimeout.TotalMilliseconds)
                        {
                            Volatile.Write(ref stalledFlag, 1);
                            stallCts.Cancel();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            });
            bool Stalled() => Volatile.Read(ref stalledFlag) == 1;

            long received = existing, attemptStart = existing; // what this attempt delivered = received - attemptStart
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stallCts.Token);
                Volatile.Write(ref lastActivity, Environment.TickCount64);

                if (existing > 0 && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Range starts at/after EOF. If the local file already holds the whole remote file,
                    // the download is simply complete — without this, a finished zip fails every retry.
                    long? full = resp.Content.Headers.ContentRange?.Length; // "bytes */<len>"
                    if (full is null || existing == full.Value)
                    {
                        progress?.Report(new DownloadProgress(existing, full ?? existing));
                        return;
                    }
                    if (existing > full.Value && attempt == 0)
                    {
                        // Local file is LARGER than the remote one (poisoned/mismatched cache) — restart clean.
                        File.Delete(dest);
                        continue;
                    }
                    resp.EnsureSuccessStatusCode(); // throws with the 416
                }

                bool append = existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                if (existing > 0 && resp.StatusCode == HttpStatusCode.OK)
                {
                    // Server ignored Range → the documented restart from zero. Remember it: such a host
                    // cannot be RESUMED, so a later failure must surface instead of truncating and
                    // re-downloading from zero again and again — a sawtooth nobody would ever see end.
                    existing = 0;
                    rangeIgnored = true;
                    Log.Info("download: the server ignores Range — restarting from zero (no further resumes on this host)");
                }
                resp.EnsureSuccessStatusCode();

                long? total = append
                    ? resp.Content.Headers.ContentRange?.Length
                    : resp.Content.Headers.ContentLength;
                received = append ? existing : 0;
                attemptStart = received;

                await using var stream = await resp.Content.ReadAsStreamAsync(stallCts.Token);
                var buf = new byte[1 << 20];

                // Sniff the first bytes BEFORE the destination is opened. A 200 carrying Drive's
                // "Quota exceeded" page would otherwise be written with FileMode.Create — truncating a
                // complete or half-complete multi-GB archive to a 2 KB web page, hours of download
                // destroyed by an error the server reported as success. Nothing is opened for writing
                // until we know the body is the file we asked for.
                int head = 0, r;
                while (head < buf.Length && head < 512 && (r = await stream.ReadAsync(buf.AsMemory(head, Math.Min(512, buf.Length) - head), stallCts.Token)) > 0)
                    head += r;
                Volatile.Write(ref lastActivity, Environment.TickCount64);

                // A text/html body is never the file, on any request — this one is unconditional.
                if (resp.Content.Headers.ContentType?.MediaType
                        ?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false)
                {
                    string text = System.Text.Encoding.UTF8.GetString(buf, 0, head);
                    bool quota = text.Contains("quota", StringComparison.OrdinalIgnoreCase);
                    throw new SourceServedPageException(
                        quota ? L.T("core.download.page.quota") : L.T("core.download.page.generic"),
                        quota);
                }
                // Only on a FRESH transfer: a resumed chunk starts mid-file, where "PK" means nothing.
                // This also covers an HTML page served with a lying content type.
                if (expectZip && !append && !(head >= 2 && buf[0] == 0x50 && buf[1] == 0x4B))
                    throw new SourceServedPageException(L.T("core.download.page.notZip"), isQuota: false);

                await using var fs = new FileStream(
                    dest, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
                    1 << 20, useAsync: true);
                fresh = false; // from here on the bytes on disk are this host's — resume them, never recreate

                progress?.Report(new DownloadProgress(received, total));
                if (head > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, head), stallCts.Token);
                    received += head;
                    progress?.Report(new DownloadProgress(received, total));
                }
                int n;
                while ((n = await stream.ReadAsync(buf, stallCts.Token)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), stallCts.Token);
                    received += n;
                    Volatile.Write(ref lastActivity, Environment.TickCount64);
                    progress?.Report(new DownloadProgress(received, total));
                }
                // The body ended. If the server told us how long it is and we are short, the connection
                // was cut cleanly (an edge going away, a NAT timeout): resume, do not hand the caller a
                // truncated file as if it were complete.
                if (total is > 0 && received < total.Value)
                {
                    if (!MayResume(received - attemptStart, Environment.TickCount64 - attemptTick, edge is not null, rangeIgnored,
                                   ref streak, ref zeroStreak, ref zeroSince, out var wait))
                        throw new IOException(rangeIgnored
                            ? L.T("core.download.closedNoResume", new { received, total })
                            : L.T("core.download.closedRepeatedly", new { count = streak, received, total }));
                    Log.Info($"download: the connection closed at {received}/{total} B — resume {streak}/{MaxResumes} via Range");
                    await Task.Delay(wait, ct);
                    continue;
                }
                return;
            }
            catch (Exception ex) when (IsResumable(ex, Stalled(), ct))
            {
                if (!MayResume(received - attemptStart, Environment.TickCount64 - attemptTick, edge is not null, rangeIgnored,
                               ref streak, ref zeroStreak, ref zeroSince, out var wait))
                {
                    // Out of reconnects. Surface it as what it is — a network failure — never as a
                    // cancel: the caller keys its same-host retry, its source fallback and its UI on
                    // the exception type, and a cancel means "the user stopped this" to all of them.
                    if (ex is OperationCanceledException)
                        throw new IOException(Stalled()
                            ? L.T("core.download.stalledRepeatedly", new { seconds = StallTimeout.TotalSeconds.ToString("0"), count = streak })
                            : L.T("core.download.connectFailed", new { error = FirstLine(ex.InnerException?.Message ?? ex.Message) }), ex);
                    if (rangeIgnored)
                        throw new IOException(
                            L.T("core.download.droppedNoResume", new { error = FirstLine(ex.Message) }), ex);
                    throw;
                }
                Log.Info(Stalled()
                    ? $"download: no bytes for {StallTimeout.TotalSeconds:0}s — reconnecting and resuming {streak}/{MaxResumes} via Range"
                    : $"download: the connection dropped ({ex.GetType().Name}: {FirstLine(ex.Message)}) — resume {streak}/{MaxResumes} via Range in {wait.TotalSeconds:0.#}s");
                await Task.Delay(wait, ct);
                continue;
            }
            finally
            {
                stallCts.Cancel();
                try { await watchdog; } catch { }
            }
        }
    }

    /// <summary>
    /// The resume policy, in one place. <paramref name="delivered"/> is what the failed attempt put on
    /// disk; the streaks are advanced here. Returns false when the call must give up.
    /// </summary>
    private static bool MayResume(long delivered, long attemptMs, bool pinned, bool rangeIgnored,
        ref int streak, ref int zeroStreak, ref long zeroSince, out TimeSpan delay)
    {
        delay = ResumeDelay;
        // A host that ignored Range cannot be resumed: every reconnect would truncate and restart from
        // zero. The documented single restart has already happened; the next failure surfaces.
        if (rangeIgnored) return false;
        if (delivered >= ProgressResetBytes) { streak = 0; zeroStreak = 0; }
        streak++;
        if (delivered > 0)
        {
            zeroStreak = 0;
            return streak <= MaxResumes;
        }
        // Nothing delivered by this attempt.
        if (zeroStreak == 0) zeroSince = Environment.TickCount64;
        zeroStreak++;
        if (pinned) return false; // the pin is the suspect — the caller races another edge in ~2 s
        bool diedFast = attemptMs < StallTimeout.TotalMilliseconds / 2;
        if (!diedFast) return zeroStreak <= MaxNoProgressResumes; // a full silent stall: one more try
        // Failed at once (link down, reset, no route): give the link time to come back, backing off.
        delay = TimeSpan.FromMilliseconds(Math.Min(16_000, ResumeDelay.TotalMilliseconds * (1 << Math.Min(zeroStreak - 1, 3))));
        return Environment.TickCount64 - zeroSince < NoProgressWindow.TotalMilliseconds && zeroStreak <= 10;
    }

    /// <summary>
    /// Is this a network-level failure worth reconnecting for — as opposed to a refusal (error page,
    /// 4xx/5xx status, zip sniff) or the user's own cancel, which must surface?
    /// </summary>
    private static bool IsResumable(Exception ex, bool stalled, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        // A cancel that is not the user's: our own stall watchdog, or a timeout the handler reports as
        // a cancel (SocketsHttpHandler does that for ConnectTimeout — not set here, but never
        // mistake it for the user).
        if (ex is OperationCanceledException) return stalled || ex.InnerException is TimeoutException;
        if (ex is SourceServedPageException) return false;
        if (ex is HttpRequestException { StatusCode: not null }) return false; // a status the server chose
        if (ex is IOException io && (io.HResult & 0xFFFF) is 0x27 or 0x70) return false; // disk full
        return ex is IOException or HttpRequestException or SocketException;
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOfAny(['\r', '\n']);
        return nl > 0 ? s[..nl] : s;
    }
}
