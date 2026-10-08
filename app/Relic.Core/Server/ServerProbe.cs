using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Relic.Core.Util;

namespace Relic.Core.Server;

/// <summary>What we know about the server before the game starts. <see cref="Up"/> null means "could
/// not find out", NOT "stopped" — the two must never be conflated in the text shown to the player.</summary>
public sealed record ServerProbeResult(bool? Up, string Degraded)
{
    /// <summary>The server is running AND every critical service is up — the only case in which the
    /// game starts without asking anything.</summary>
    public bool AllGood => Up == true && Degraded.Length == 0;

    /// <summary>Which path answered: "agent" (token /status), "public" (/public/status), "sdk" (game
    /// port probe), "none" (nothing configured).</summary>
    public string Source { get; init; } = "agent";

    /// <summary>Does the server have this version at all? false = the agent lists it with
    /// <c>present: false</c> (no stack on the box — "stopped" would promise a start that cannot happen;
    /// it can be transient while an admin extracts a stack, so the text stays neutral). true = listed
    /// and not marked absent (an older agent without the key counts as present). null = unknown: a
    /// version the agent does not list, or the SDK probe, which cannot tell versions apart.</summary>
    public bool? Hosted { get; init; }

    /// <summary>Comma-joined ids of the versions the agent lists as present (<c>present != false</c>) —
    /// what the server does have, for the "not on this server" text. Empty when unknown or none.</summary>
    public string HostedVersions { get; init; } = "";

    /// <summary>Up but not healthy, without the names of the dead services: the public snapshot only
    /// says <c>healthy: false</c>. <see cref="Degraded"/> then holds a non-empty marker (so
    /// <see cref="AllGood"/> stays false) that is not a service list and must not be shown as one.</summary>
    public bool DegradedUnlisted { get; init; }
}

/// <summary>
/// The question "is version X's server running?", put to the agent, WITH RETRIES.
///
/// The retries are there because the probe can run
/// at the coldest possible moment — the first process after a boot, on the first
/// run after the install, with the whole HTTP stack still un-JITted, nothing in the DNS cache and,
/// often enough, the network still coming to its senses a few seconds after login. A single attempt
/// then reports "the server status could not be checked" against a server that is
/// working perfectly, while the next launch — after the launcher has warmed everything up — passes
/// without a word. In the normal
/// case the answer comes on the first attempt and the retries cost nothing.
/// </summary>
public static class ServerProbe
{
    /// <summary>How long a single attempt may take. A box that is really off never answers the SYN at
    /// all, and Windows would retry on its own for ~21 s — this cap is what gets us out of there.</summary>
    public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(4);
    public const int DefaultAttempts = 3;
    public static readonly TimeSpan DefaultRetryPause = TimeSpan.FromMilliseconds(750);

    /// <summary>Never throws: any failure becomes "unknown" (Up = null).</summary>
    public static async Task<ServerProbeResult> CheckAsync(
        ServerConfig? cfg, string versionId, Action<string>? status = null,
        TimeSpan? attemptTimeout = null, int attempts = DefaultAttempts, TimeSpan? retryPause = null)
    {
        if (cfg is null)
        {
            Log.Info("server probe skipped: no server configured in the build");
            return new ServerProbeResult(true, "");
        }
        var perAttempt = attemptTimeout ?? DefaultAttemptTimeout;
        var pause = retryPause ?? DefaultRetryPause;
        if (attempts < 1) attempts = 1;

        status?.Invoke(L.T("core.probe.checking"));
        var sw = Stopwatch.StartNew();
        string why = "";
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (await TryOnceAsync(cfg, versionId, perAttempt) is ServerProbeResult got)
                {
                    Log.Info($"server probe {versionId}: up={got.Up?.ToString() ?? "unknown"} " +
                             $"hosted={got.Hosted?.ToString() ?? "unknown"} " +
                             $"servicesDown='{got.Degraded}' ({attempt} attempts, {sw.ElapsedMilliseconds} ms)");
                    return got;
                }
                why = $"no answer within {perAttempt.TotalSeconds:0.#} s";
            }
            catch (Exception ex)
            {
                why = ex.Message;
            }
            if (attempt == attempts) break;
            Log.Info($"server probe {versionId}: attempt {attempt} failed ({why}) — retrying");
            status?.Invoke(L.T("core.probe.retrying"));
            await Task.Delay(pause);
        }
        Log.Info($"server probe {versionId}: UNKNOWN after {attempts} attempts " +
                 $"in {sw.ElapsedMilliseconds} ms — last: {why}");
        return new ServerProbeResult(null, "");
    }

    /// <summary>One attempt. null = timed out; exception = failed; otherwise the agent's answer.</summary>
    private static async Task<ServerProbeResult?> TryOnceAsync(ServerConfig cfg, string versionId, TimeSpan timeout)
    {
        // No `using` on cts: after a timeout the abandoned task still holds its token, and a Dispose
        // here would pull the source out from under it.
        var cts = new CancellationTokenSource(timeout);
        var work = Task.Run(async () =>
        {
            await using var a = new AgentClient(cfg);
            await a.ConnectAsync(cts.Token);
            return Parse(await a.StatusAsync(cts.Token), versionId);
        });
        // A race, not just the token: in ssh mode ConnectAsync enters Renci's `_ssh.Connect()`, which
        // is synchronous, has its own 15 s timeout and cannot be cancelled — our cap has to sit above
        // it, not inside it.
        if (await Task.WhenAny(work, Task.Delay(timeout)) != work)
        {
            cts.Cancel();
            // Observe the abandoned task's failure, otherwise it becomes an UnobservedTaskException.
            _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }
        return await work;
    }

    /// <summary>
    /// The MODE-AWARE variant: Server Admin mode with a token asks the agent's /status;
    /// Player mode asks the agent's token-less /public/status (when the admin enabled it) and,
    /// failing that, probes the game's SDK port directly — so a player needs nothing but a server
    /// address. <see cref="ServerProbeResult.Source"/> says which path answered.
    /// </summary>
    public static async Task<ServerProbeResult> CheckAsync(State.RelicState state, string versionId, Action<string>? status = null)
    {
        var s = state.Settings;
        bool admin = string.Equals(s.Mode, "admin", StringComparison.OrdinalIgnoreCase);
        // The same derivation the app uses for every admin call: the CONFIGURED host, with the token
        // stored for it (or the baked one) — never the host remembered inside server.json.
        var cfg = ServerConfig.Effective(ServerConfigStore.Load(), s.ServerHost, s.AgentPort);
        if (admin && cfg is not null)
        {
            var r = await CheckAsync(cfg, versionId, status);
            if (r.Up is not null) return r with { Source = "agent" };
        }
        if (string.IsNullOrWhiteSpace(s.ServerHost))
        {
            Log.Info("server probe skipped: no server address configured");
            return new ServerProbeResult(true, "") { Source = "none" };
        }
        status?.Invoke(L.T("core.probe.checking"));
        var sw = Stopwatch.StartNew();
        try
        {
            using var pub = new AgentPublicClient(s.ServerHost, s.AgentPort, TimeSpan.FromSeconds(5));
            var st = await pub.StatusAsync();
            var r = ParsePublic(st, versionId);
            Log.Info($"server probe {versionId} (public): up={r.Up?.ToString() ?? "unknown"} hosted={r.Hosted?.ToString() ?? "unknown"} " +
                     $"degraded='{r.Degraded}' ({sw.ElapsedMilliseconds} ms)");
            return r;
        }
        catch (Exception ex)
        {
            Log.Info($"server probe {versionId}: public status unavailable ({ex.Message}) — probing the SDK port");
        }
        var sdk = await ProbeSdkAsync(s.ServerHost, s.ServerPort, DefaultAttemptTimeout);
        Log.Info($"server probe {versionId} (sdk {s.ServerHost}:{s.ServerPort}): reachable={sdk.Reachable?.ToString() ?? "unknown"} {sdk.Detail} ({sw.ElapsedMilliseconds} ms)");
        return new ServerProbeResult(sdk.Reachable, sdk.Reachable == true && sdk.Detail != "ok" ? sdk.Detail : "") { Source = "sdk" };
    }

    /// <summary>Read a /public/status answer for one version (same shape rules as <see cref="Parse"/>).
    /// A version the snapshot does not list is unknown, never absent: only an explicit
    /// <c>present: false</c> says the server does not have it. <c>statusUnknown</c> (docker
    /// could not answer on the box) makes every <c>up: false</c> in the snapshot fabricated, so nothing
    /// is asserted about running — but <c>present</c> is a filesystem fact on the agent, independent of
    /// docker, so it is read first (the same order as <see cref="Parse"/>).</summary>
    public static ServerProbeResult ParsePublic(JsonElement st, string versionId)
    {
        string hosted = HostedList(st);
        if (!TryVersion(st, versionId, out var v))
            return new ServerProbeResult(null, "") { Source = "public", HostedVersions = hosted };
        if (IsAbsent(v))
            return new ServerProbeResult(false, "") { Source = "public", Hosted = false, HostedVersions = hosted };
        if (st.TryGetProperty("statusUnknown", out var su) && su.ValueKind == JsonValueKind.True)
            return new ServerProbeResult(null, "") { Source = "public", Hosted = true, HostedVersions = hosted };
        bool up = v.TryGetProperty("up", out var u) && u.ValueKind == JsonValueKind.True;
        if (!up) return new ServerProbeResult(false, "") { Source = "public", Hosted = true, HostedVersions = hosted };
        bool healthy = !v.TryGetProperty("healthy", out var h) || h.ValueKind != JsonValueKind.False;
        return new ServerProbeResult(true, healthy ? "" : "services")
            { Source = "public", Hosted = true, HostedVersions = hosted, DegradedUnlisted = !healthy };
    }

    /// <summary>versions[versionId] as an object, when the answer lists it.</summary>
    private static bool TryVersion(JsonElement st, string versionId, out JsonElement v)
    {
        v = default;
        return st.ValueKind == JsonValueKind.Object
            && st.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Object
            && vs.TryGetProperty(versionId, out v) && v.ValueKind == JsonValueKind.Object;
    }

    /// <summary>Only a JSON <c>false</c> is absent — an older agent sends no <c>present</c> at all.</summary>
    private static bool IsAbsent(JsonElement v) =>
        v.TryGetProperty("present", out var p) && p.ValueKind == JsonValueKind.False;

    /// <summary>The ids the answer lists as present, comma-joined in the agent's order.</summary>
    private static string HostedList(JsonElement st)
    {
        if (!(st.ValueKind == JsonValueKind.Object
              && st.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Object))
            return "";
        return string.Join(", ", vs.EnumerateObject()
            .Where(e => e.Value.ValueKind == JsonValueKind.Object && !IsAbsent(e.Value))
            .Select(e => e.Name));
    }

    public readonly record struct SdkProbe(bool? Reachable, string Detail);

    /// <summary>Token-less reachability check of the game's SDK/dispatch port: the vendor SDK proxies
    /// /query_region_list to dispatch and returns plain base64 text. 200 + body = sdk AND dispatch up;
    /// 5xx = sdk up but dispatch down (still reachable); connection refused = down; timeout = unknown.
    /// Never through the system proxy (Fiddler may be running).</summary>
    public static async Task<SdkProbe> ProbeSdkAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        string h = host.Contains(':') && !host.StartsWith('[') ? "[" + host + "]" : host;
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = timeout }) { Timeout = timeout };
            using var resp = await http.GetAsync($"http://{h}:{port}/query_region_list?version=OSRELWin1.6.0&lang=2&platform=3&binary=1&time=0", ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return new SdkProbe(body.Trim().Length > 0, body.Trim().Length > 0 ? "ok" : "empty");
            if ((int)resp.StatusCode >= 500) return new SdkProbe(true, "dispatch down");
            return new SdkProbe(true, $"http {(int)resp.StatusCode}");
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException se)
        {
            return se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused
                ? new SdkProbe(false, "refused") : new SdkProbe(null, se.SocketErrorCode.ToString());
        }
        catch (TaskCanceledException) { return new SdkProbe(null, "timeout"); }
        catch (Exception ex) { return new SdkProbe(null, ex.GetType().Name); }
    }

    /// <summary>Reading the /status answer for one version. Public because it is the pure half of
    /// the mechanism and the spike checks it against synthetic payloads.</summary>
    public static ServerProbeResult Parse(JsonElement st, string versionId)
    {
        string hosted = HostedList(st);
        bool listed = TryVersion(st, versionId, out var v);
        // present:false is checked BEFORE the docker error: it is a filesystem fact on the agent (no
        // compose file for the version), true whether or not docker answered.
        if (listed && IsAbsent(v))
            return new ServerProbeResult(false, "") { Hosted = false, HostedVersions = hosted };
        bool? hostedHere = listed ? true : null;
        // A top-level "error" = docker compose ls failed on the box: everything shows up:false without
        // it being true, so nothing can be asserted. The agent did answer, though — not worth a retry.
        if (st.ValueKind == JsonValueKind.Object && st.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            return new ServerProbeResult(null, "") { Hosted = hostedHere, HostedVersions = hosted };
        if (!(listed && v.TryGetProperty("up", out var u) && u.ValueKind == JsonValueKind.True))
            return new ServerProbeResult(false, "") { Hosted = hostedHere, HostedVersions = hosted };
        // "up" with fallen services is exactly the white-screen scenario — the client
        // connects, but the dead gameserver leaves it on an empty screen. The agent's watchdog revives
        // them on its own in ~2 minutes; until then it is not a clean "yes".
        string dead = "";
        if (v.TryGetProperty("servicesDown", out var sd) && sd.ValueKind == JsonValueKind.Array)
            dead = string.Join(", ", sd.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s)));
        return new ServerProbeResult(true, dead) { Hosted = true, HostedVersions = hosted };
    }
}
