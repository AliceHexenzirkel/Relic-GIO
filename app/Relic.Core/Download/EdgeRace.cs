using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Relic.Core.Util;

namespace Relic.Core.Download;

/// <summary>One edge that took part in a race: what it delivered inside the probe window.</summary>
public sealed record EdgeProbe(IPAddress Ip, long Bytes, double Seconds, int Status, string? Error)
{
    public double MiBps => Seconds > 0 ? Bytes / Seconds / 1048576.0 : 0;
    public override string ToString() =>
        Error is null ? $"{Ip} {MiBps:0.0} MiB/s" : $"{Ip} ✗ {Error}";
}

/// <summary>The edge a host's download should be pinned to, chosen by measuring, not by trusting DNS.</summary>
public sealed record EdgeChoice(string Host, IPAddress Ip, double MiBps, IReadOnlyList<EdgeProbe> All, IReadOnlyList<string> Sources);

/// <summary>
/// Client-side CDN steering. <c>autopatchhk.yuanshen.com</c> is a multi-CDN name: successive DNS
/// answers rotate between Akamai (which can be an ISP-embedded node),
/// CloudFront and Cloudflare (which can be many times slower from the same line). A single connection
/// gets whichever edge the resolver happened to hand out at connect time — which is why the same
/// code can be fast one day and many times slower the next. So instead of one lookup, gather every edge the
/// name can resolve to (system resolver + several public DoH resolvers + plain UDP resolvers + every
/// CNAME target seen, re-resolved + what won last time), pull a few MiB from each at once, and pin the
/// transfer to the one that actually delivers. The cache is a hint only: a stale IP just loses or
/// fails the probe.
/// </summary>
public static class EdgeRace
{
    /// <summary>How long one DNS lookup (system, DoH or UDP) may take before it is simply ignored.</summary>
    public static TimeSpan ResolveTimeout { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Wall-clock window of the race; measured from the moment all probes are started.</summary>
    public static TimeSpan ProbeWindow { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Cap per probe: an edge that delivers this much before the window ends is done (and fast).</summary>
    public static int ProbeBytes { get; set; } = 4 << 20;
    /// <summary>At most this many edges are raced. The CDN name resolves to 13–15 addresses once every
    /// CNAME target is followed (eight of them Akamai nodes in five different /16s); probing them all
    /// would cost 15 × ProbeBytes per attempt and make the probes compete with each other for the
    /// line. One address per /16 keeps the diversity (different CDNs, and Akamai's ISP-embedded
    /// nodes vs its backbone ones, never share a /16) at a bounded cost, and the candidates are
    /// interleaved across CDN families before the cap so no family can crowd the others out.</summary>
    public static int MaxRaced { get; set; } = 10;
    /// <summary>Where the learned edges live (per host: ips, cname targets, last winner).</summary>
    public static string CacheFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relic", "edges.json");
    /// <summary>Public DNS-over-HTTPS JSON endpoints. Each is queried independently; any failure is ignored.
    /// Deliberately more than one provider: they sit behind different caches and hand out different CDNs.
    /// (Quad9's :5053 JSON endpoint and OpenDNS's do not answer — don't add them.)</summary>
    public static IReadOnlyList<string> DohEndpoints { get; set; } =
    [
        "https://dns.google/resolve",
        "https://dns.google/resolve", // asked twice on purpose: its answers rotate between CDNs
        "https://cloudflare-dns.com/dns-query",
        "https://dns.adguard-dns.com/resolve",
        "https://dns.nextdns.io/dns-query",
    ];
    /// <summary>Plain DNS-over-UDP resolvers asked directly (port 53), bypassing the Windows cache — the
    /// system resolver answers the same thing for the whole TTL, these answer what the steering says NOW.
    /// Blocked UDP just means a silent miss.</summary>
    public static IReadOnlyList<string> UdpResolvers { get; set; } = ["8.8.8.8", "1.1.1.1", "9.9.9.9"];

    private const int MaxNamesPerHost = 8;       // cname targets re-resolved per host (bounds the round-2 fan-out)
    private const int MaxCachedIps = 12;
    private const long MaxDohBody = 64 * 1024;   // a DoH answer is a few hundred bytes; anything bigger is not one

    private sealed class Memory
    {
        public List<string> Ips { get; set; } = [];
        public List<string> Cnames { get; set; } = [];
        public string? Winner { get; set; }
        public DateTime At { get; set; }
    }

    /// <summary>What one lookup produced: addresses, cname targets, and where it came from.</summary>
    private sealed record Resolution(HashSet<IPAddress> Ips, HashSet<string> Cnames, List<string> Sources);

    /// <summary>
    /// Every distinct IPv4 edge <paramref name="host"/> can resolve to right now, plus the ones learned
    /// before — ordered for the race: last winner first, then the families (the host's own answers, then
    /// each CNAME target's) interleaved one address at a time, then the cache. Never throws; never takes
    /// longer than roughly two <see cref="ResolveTimeout"/>s.
    /// </summary>
    public static async Task<(IReadOnlyList<IPAddress> Ips, IReadOnlyList<string> Cnames, IReadOnlyList<string> Sources)>
        ResolveCandidatesAsync(string host, CancellationToken ct)
    {
        var sources = new List<string>();
        var cnames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mem = LoadMemory(host);

        // Round 1: the host itself, through every resolver at once — twice. The steering answers
        // differently from one query to the next even on the same resolver (one can answer
        // CloudFront, CloudFront, Akamai to three consecutive lookups), so a second pass
        // a moment later is the cheapest way to see a CDN family the first pass missed — and on a
        // fresh machine, before the cache knows that family's CNAME, it is the only way.
        var byName = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        for (int pass = 0; pass < 2; pass++)
        {
            var r1 = await ResolveManyAsync([host], ct);
            Merge(byName, r1, cnames, sources);
        }

        // Round 2: every CNAME target ever seen for this host (now or cached), resolved directly. This is
        // what makes the race see ALL the CDNs: the top-level steering answers one of them per query,
        // but "a1881.w27.akamai.net" always resolves to Akamai, whatever the steering says today.
        var names = new List<string>();
        foreach (var c in cnames) if (!c.Equals(host, StringComparison.OrdinalIgnoreCase) && !names.Contains(c, StringComparer.OrdinalIgnoreCase)) names.Add(c);
        if (mem is not null)
            foreach (var c in mem.Cnames)
                if (!c.Equals(host, StringComparison.OrdinalIgnoreCase) && !names.Contains(c, StringComparer.OrdinalIgnoreCase)) names.Add(c);
        if (names.Count > 0)
        {
            var r2 = await ResolveManyAsync(names.Take(MaxNamesPerHost).ToList(), ct);
            Merge(byName, r2, cnames, sources);
        }

        // Interleave the families so the MaxRaced cap cannot be filled by one CDN's many nodes while
        // another CDN's only address sits at the end of the list: host answers first, then round-robin.
        var ordered = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();
        var families = new List<List<IPAddress>>();
        if (byName.TryGetValue(host, out var own)) families.Add(own);
        foreach (var n in names) if (byName.TryGetValue(n, out var l)) families.Add(l);
        for (int i = 0; families.Any(f => i < f.Count); i++)
            foreach (var f in families)
                if (i < f.Count && seen.Add(f[i])) ordered.Add(f[i]);

        // Last time's winner goes FIRST — but only when today's resolution still names it. Prune keeps
        // one address per /16, so a head-of-list winner that is no longer in rotation would claim the
        // slot of a live sibling in the same /16 (an ISP-embedded Akamai pair of addresses is
        // exactly such a pair) and the race would never see the fast edge. A winner nobody resolves
        // today is still raced — after the families, where it can only add, never shadow.
        IPAddress? winner = mem?.Winner is not null && IPAddress.TryParse(mem.Winner, out var w)
                            && w.AddressFamily == AddressFamily.InterNetwork ? w : null;
        if (winner is not null)
        {
            int at = ordered.FindIndex(ip => ip.Equals(winner));
            if (at > 0) { ordered.RemoveAt(at); ordered.Insert(0, winner); }
            else if (at < 0 && seen.Add(winner)) { ordered.Add(winner); sources.Add("cache:winner"); }
        }

        // Learned edges last: they are re-measured like everything else, and a stale one just loses.
        if (mem is not null)
            foreach (var s in mem.Ips)
                if (IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && seen.Add(ip))
                { ordered.Add(ip); sources.Add("cache"); }

        return (ordered, cnames.ToList(), sources);
    }

    private static void Merge(Dictionary<string, List<IPAddress>> byName, Dictionary<string, Resolution> found,
        HashSet<string> cnames, List<string> sources)
    {
        foreach (var (name, r) in found)
        {
            if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
            foreach (var ip in r.Ips) if (!list.Contains(ip)) list.Add(ip);
            cnames.UnionWith(r.Cnames);
            sources.AddRange(r.Sources);
        }
    }

    /// <summary>Resolve each name through every resolver at once; per-name results, in resolver order.</summary>
    private static async Task<Dictionary<string, Resolution>> ResolveManyAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        var tasks = new List<(string Name, Task<Resolution?> Task)>();
        foreach (var n in names)
        {
            tasks.Add((n, SystemResolveAsync(n, ct)));
            foreach (var ep in DohEndpoints) tasks.Add((n, DohResolveAsync(ep, n, ct)));
            foreach (var srv in UdpResolvers) tasks.Add((n, UdpResolveAsync(srv, n, ct)));
        }
        await Task.WhenAll(tasks.Select(t => t.Task));
        var res = new Dictionary<string, Resolution>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, task) in tasks)
        {
            var r = task.Result;
            if (r is null) continue;
            if (!res.TryGetValue(name, out var acc))
                res[name] = acc = new Resolution([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), []);
            acc.Ips.UnionWith(r.Ips); acc.Cnames.UnionWith(r.Cnames); acc.Sources.AddRange(r.Sources);
        }
        return res;
    }

    private static async Task<Resolution?> SystemResolveAsync(string name, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ResolveTimeout);
            var addrs = await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, cts.Token);
            var set = new HashSet<IPAddress>(addrs);
            return set.Count == 0 ? null : new Resolution(set, new HashSet<string>(StringComparer.OrdinalIgnoreCase), [$"system({name})"]);
        }
        catch { return null; }
    }

    /// <summary>One DoH JSON query (RFC 8484's JSON flavour as served by Google/Cloudflare/AdGuard/NextDNS).
    /// The whole answer is read under the same token and size cap — a resolver that sends headers and then
    /// stalls must not hang the race (a synchronous body parse would, and would ignore the user's cancel).</summary>
    private static async Task<Resolution?> DohResolveAsync(string endpoint, string name, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ResolveTimeout);
            using var http = Downloader.CreateClient();
            http.Timeout = ResolveTimeout;
            http.MaxResponseContentBufferSize = MaxDohBody;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?name={Uri.EscapeDataString(name)}&type=A");
            req.Headers.Accept.ParseAdd("application/dns-json");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            string body = await resp.Content.ReadAsStringAsync(cts.Token);
            var (ips, cnames) = ParseDohAnswer(body);
            if (ips.Count == 0 && cnames.Count == 0) return null;
            return new Resolution(ips, cnames, [$"doh:{new Uri(endpoint).Host}({name})"]);
        }
        catch { return null; }
    }

    /// <summary>One plain DNS query over UDP (RFC 1035): A records and CNAMEs from the answer section.</summary>
    private static async Task<Resolution?> UdpResolveAsync(string server, string name, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ResolveTimeout);
            ushort id = (ushort)Random.Shared.Next(1, 65535);
            var q = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }; // RD=1, QDCOUNT=1
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var b = System.Text.Encoding.ASCII.GetBytes(label);
                if (b.Length is 0 or > 63) return null;
                q.Add((byte)b.Length); q.AddRange(b);
            }
            q.AddRange([0, 0, 1, 0, 1]); // root, QTYPE A, QCLASS IN
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(IPAddress.Parse(server), 53);
            await udp.SendAsync(q.ToArray(), cts.Token);
            var p = (await udp.ReceiveAsync(cts.Token)).Buffer;
            if (p.Length < 12 || p[0] != (byte)(id >> 8) || p[1] != (byte)id || (p[3] & 0x0F) != 0) return null;
            int qd = (p[4] << 8) | p[5], an = (p[6] << 8) | p[7], pos = 12;
            for (int i = 0; i < qd; i++) pos = SkipName(p, pos) + 4;
            var ips = new HashSet<IPAddress>();
            var cnames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < an && pos < p.Length; i++)
            {
                pos = SkipName(p, pos);
                if (pos + 10 > p.Length) break;
                int type = (p[pos] << 8) | p[pos + 1];
                int rdlen = (p[pos + 8] << 8) | p[pos + 9];
                pos += 10;
                if (pos + rdlen > p.Length) break;
                if (type == 1 && rdlen == 4) ips.Add(new IPAddress(p.AsSpan(pos, 4)));
                else if (type == 5) { string c = ReadName(p, pos); if (c.Length > 0) cnames.Add(c); }
                pos += rdlen;
            }
            if (ips.Count == 0 && cnames.Count == 0) return null;
            return new Resolution(ips, cnames, [$"udp:{server}({name})"]);
        }
        catch { return null; }
    }

    private static int SkipName(byte[] p, int pos)
    {
        while (pos < p.Length)
        {
            int len = p[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2;
            pos += 1 + len;
        }
        return pos;
    }

    private static string ReadName(byte[] p, int pos)
    {
        var sb = new System.Text.StringBuilder();
        int hops = 0;
        while (pos < p.Length && hops < 32)
        {
            int len = p[pos];
            if (len == 0) break;
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= p.Length) break;
                pos = ((len & 0x3F) << 8) | p[pos + 1];
                hops++;
                continue;
            }
            if (pos + 1 + len > p.Length) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(System.Text.Encoding.ASCII.GetString(p, pos + 1, len));
            pos += 1 + len;
        }
        return sb.ToString();
    }

    /// <summary>Parse a DoH JSON answer: type 1 = A, type 5 = CNAME. Public for the download spike.</summary>
    public static (HashSet<IPAddress> Ips, HashSet<string> Cnames) ParseDohAnswer(string json)
    {
        var ips = new HashSet<IPAddress>();
        var cnames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("Answer", out var answers) && answers.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in answers.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                if (!a.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.Number
                    || !a.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.String) continue;
                string data = d.GetString()!.Trim().TrimEnd('.');
                switch (t.GetInt32())
                {
                    case 1 when IPAddress.TryParse(data, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork:
                        ips.Add(ip); break;
                    case 5 when data.Length > 0:
                        cnames.Add(data); break;
                }
            }
        }
        return (ips, cnames);
    }

    /// <summary>
    /// Race every edge of <paramref name="url"/>'s host and return the one to pin to — or null when
    /// there is nothing to choose (fewer than two edges, or none that delivers), in which case the
    /// caller connects the ordinary way. Costs at most <see cref="ProbeBytes"/> per edge.
    /// </summary>
    /// <param name="candidates">Override the resolved edge list (spikes). Null = resolve.</param>
    /// <param name="exclude">Edges known dead (a pin that stopped answering mid-transfer): not raced.</param>
    public static async Task<EdgeChoice?> PickAsync(string url, CancellationToken ct,
        IReadOnlyList<IPAddress>? candidates = null, IEnumerable<IPAddress>? exclude = null)
    {
        var uri = new Uri(url);
        string host = uri.Host;
        IReadOnlyList<IPAddress> ips;
        IReadOnlyList<string> cnames = [];
        IReadOnlyList<string> sources = [];
        if (candidates is not null) ips = candidates;
        else
        {
            if (IPAddress.TryParse(host, out _)) return null; // a literal IP has no edges to choose from
            (ips, cnames, sources) = await ResolveCandidatesAsync(host, ct);
        }
        if (exclude is not null)
        {
            var dead = new HashSet<IPAddress>(exclude);
            ips = ips.Where(ip => !dead.Contains(ip)).ToList();
        }
        if (ips.Count < 2)
        {
            if (candidates is null) Log.Info($"edge {host}: a single DNS answer ({string.Join(",", ips)}) — no race");
            return null;
        }
        int found = ips.Count;
        ips = Prune(ips);

        var clock = Stopwatch.StartNew();
        var probes = await Task.WhenAll(ips.Select(ip => ProbeOneAsync(url, host, ip, clock, ct)));
        // Rank by throughput, not bytes: two edges that both hit ProbeBytes before the window ends tie
        // on bytes, and the one that got there sooner is the faster one.
        // An edge still streaming when the window closes is scored on
        // what it managed in the whole window, which is exactly what the user would get from it.
        var ranked = probes.Where(p => p.Error is null && p.Bytes > 0)
            .OrderByDescending(p => p.MiBps).ThenByDescending(p => p.Bytes).ToList();

        string detail = string.Join(" · ", probes.Select(p => p.ToString()));
        if (ranked.Count == 0)
        {
            Log.Info($"edge {host}: no edge delivered within the {ProbeWindow.TotalSeconds:0.#}s probe ({detail}) — ordinary connection");
            return null;
        }
        var best = ranked[0];
        var choice = new EdgeChoice(host, best.Ip, best.MiBps, probes, sources);
        Log.Info($"edge {host}: {detail} → {best.Ip} ({best.MiBps:0.0} MiB/s in the probe; " +
                 $"{ips.Count} edges probed out of {found} found in {sources.Count} answers)");
        if (candidates is null) SaveMemory(host, ips, cnames, best.Ip);
        return choice;
    }

    /// <summary>Keep the list's order (winner/system first) but at most one address per /16 and at most
    /// <see cref="MaxRaced"/> in total. Public for the download spike.</summary>
    public static IReadOnlyList<IPAddress> Prune(IReadOnlyList<IPAddress> ips)
    {
        var seen = new HashSet<uint>();
        var kept = new List<IPAddress>();
        foreach (var ip in ips)
        {
            var b = ip.GetAddressBytes();
            if (b.Length != 4) continue;
            uint slash16 = (uint)(b[0] << 8 | b[1]);
            if (!seen.Add(slash16)) continue;
            kept.Add(ip);
            if (kept.Count >= MaxRaced) break;
        }
        return kept.Count >= 2 ? kept : ips.Take(Math.Max(2, MaxRaced)).ToList();
    }

    private static async Task<EdgeProbe> ProbeOneAsync(string url, string host, IPAddress ip, Stopwatch clock, CancellationToken ct)
    {
        long bytes = 0; int status = 0;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeWindow);
            using var http = Downloader.CreateClient(pinIp: ip, pinHost: host);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, ProbeBytes - 1);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            status = (int)resp.StatusCode;
            if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                return new EdgeProbe(ip, 0, clock.Elapsed.TotalSeconds, status, $"status {status}");
            if (resp.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false)
                return new EdgeProbe(ip, 0, clock.Elapsed.TotalSeconds, status, "html page");
            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[64 << 10];
            int n;
            while (bytes < ProbeBytes && (n = await s.ReadAsync(buf, cts.Token)) > 0) bytes += n;
            return new EdgeProbe(ip, bytes, clock.Elapsed.TotalSeconds, status, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Window over: whatever arrived is the measurement. Zero bytes = this edge never got going.
            return new EdgeProbe(ip, bytes, clock.Elapsed.TotalSeconds, status, bytes > 0 ? null : "no answer within the window");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EdgeProbe(ip, bytes, clock.Elapsed.TotalSeconds, status, Short(ex));
        }
    }

    private static string Short(Exception ex)
    {
        var e = ex; while (e.InnerException is not null && e is HttpRequestException or AggregateException) e = e.InnerException;
        string m = e.Message.Trim();
        int nl = m.IndexOfAny(['\r', '\n']); if (nl > 0) m = m[..nl];
        return m.Length > 80 ? m[..77] + "..." : m;
    }

    // ── learned edges ──

    private static Dictionary<string, Memory> LoadAll()
    {
        try
        {
            if (!File.Exists(CacheFile)) return new(StringComparer.OrdinalIgnoreCase);
            var d = JsonSerializer.Deserialize<Dictionary<string, Memory>>(File.ReadAllText(CacheFile));
            return d is null ? new(StringComparer.OrdinalIgnoreCase) : new(d, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static Memory? LoadMemory(string host) => LoadAll().TryGetValue(host, out var m) ? m : null;

    private static void SaveMemory(string host, IReadOnlyList<IPAddress> ips, IReadOnlyList<string> cnames, IPAddress winner)
    {
        try
        {
            var all = LoadAll();
            all.TryGetValue(host, out var old);
            var keep = new List<string> { winner.ToString() };
            foreach (var ip in ips) if (!keep.Contains(ip.ToString())) keep.Add(ip.ToString());
            if (old is not null) foreach (var s in old.Ips) if (!keep.Contains(s)) keep.Add(s);
            var names = new List<string>(cnames);
            if (old is not null) foreach (var c in old.Cnames) if (!names.Contains(c, StringComparer.OrdinalIgnoreCase)) names.Add(c);
            all[host] = new Memory
            {
                Ips = keep.Take(MaxCachedIps).ToList(),
                Cnames = names.Take(MaxNamesPerHost).ToList(),
                Winner = winner.ToString(),
                At = DateTime.UtcNow,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            string tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, CacheFile, overwrite: true);
        }
        catch (Exception ex) { Log.Error("edge: could not save the edge cache (non-fatal)", ex); }
    }
}
