using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Relic.Core.Util;

/// <summary>
/// DNS TXT lookups for server-address discovery (a DNS name can publish its game/agent ports as
/// <c>_relic.&lt;host&gt; IN TXT "v=relic1 port=21000 agent=18080"</c>, Minecraft-style).
/// Primary path is the OS resolver (<c>dnsapi!DnsQuery_W</c> — honours the system cache, VPN/NRPT
/// split DNS and DoH settings); fallback is DNS-over-HTTPS JSON (Google, Cloudflare). Pure parsing
/// helpers are public so the spike can cover them without network.
/// </summary>
public static class DnsTxt
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>All TXT records of <paramref name="name"/> (each record's character-strings joined).
    /// Empty on NXDOMAIN/no records/any failure — never throws.</summary>
    public static async Task<IReadOnlyList<string>> QueryAsync(string name, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        name = (name ?? "").Trim().TrimEnd('.');
        if (name.Length == 0 || name.Length > 253) return Array.Empty<string>();
        var budget = timeout ?? DefaultTimeout;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var sys = Task.Run(() => QueryWindows(name), ct);
                var done = await Task.WhenAny(sys, Task.Delay(budget, ct)).ConfigureAwait(false);
                if (done == sys && sys.Result is { Count: > 0 } r) return r;
                if (done == sys && sys.Result is { Count: 0 } && _lastWindowsStatus is 0 or 9003 or 9501)
                    return Array.Empty<string>(); // authoritative "no such record" — do not ask DoH
            }
            catch { /* fall through to DoH */ }
        }

        foreach (var endpoint in new[] { "https://dns.google/resolve", "https://cloudflare-dns.com/dns-query" })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(budget);
                var recs = await DohTxtAsync(endpoint, name, cts.Token).ConfigureAwait(false);
                if (recs is not null) return recs;
            }
            catch { /* next */ }
        }
        return Array.Empty<string>();
    }

    /// <summary>Parse one TXT record of whitespace/semicolon separated <c>key=value</c> tokens
    /// (keys lower-cased, later duplicates win). Tokens without '=' are ignored.</summary>
    public static IReadOnlyDictionary<string, string> ParseKv(string record)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tok in (record ?? "").Split(new[] { ' ', '\t', ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = tok.IndexOf('=');
            if (eq <= 0) continue;
            string k = tok[..eq].Trim().ToLowerInvariant();
            string v = tok[(eq + 1)..].Trim().Trim('"');
            if (k.Length > 0) d[k] = v;
        }
        return d;
    }

    public readonly record struct RelicTxt(int? Port, int? AgentPort);

    /// <summary>Pick the Relic record out of a TXT set: the first record with <c>v=relic1</c>, else
    /// the first record carrying a valid <c>port=</c>. Ports outside 1-65535 are ignored.</summary>
    public static RelicTxt? PickRelic(IEnumerable<string> records)
    {
        RelicTxt? fallback = null;
        foreach (var rec in records)
        {
            var kv = ParseKv(rec);
            int? port = kv.TryGetValue("port", out var ps) && int.TryParse(ps, out int p) && p is > 0 and < 65536 ? p : null;
            int? agent = kv.TryGetValue("agent", out var ags) && int.TryParse(ags, out int a) && a is > 0 and < 65536 ? a : null;
            if (kv.TryGetValue("v", out var v) && string.Equals(v, "relic1", StringComparison.OrdinalIgnoreCase))
                return new RelicTxt(port, agent);
            if (port is not null && fallback is null) fallback = new RelicTxt(port, agent);
        }
        return fallback;
    }

    // ── DoH ──

    /// <summary>Extract the TXT strings from a DoH JSON answer (type 16). Returns null when the
    /// answer has no TXT records. Quoted segments ("a" "b") are unescaped and concatenated.</summary>
    public static IReadOnlyList<string>? ParseDohTxt(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<string>();
        foreach (var a in answers.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) continue;
            if (!a.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.Number || t.GetInt32() != 16) continue;
            if (!a.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.String) continue;
            string s = UnquoteTxt(d.GetString() ?? "");
            if (s.Length > 0) list.Add(s);
        }
        return list.Count == 0 ? null : list;
    }

    /// <summary>DoH presents TXT data as zone-file text: one or more quoted, backslash-escaped
    /// character-strings. Join them into the record's logical string.</summary>
    public static string UnquoteTxt(string data)
    {
        data = data.Trim();
        if (data.Length == 0 || data[0] != '"') return data;
        var sb = new StringBuilder();
        bool inQ = false;
        for (int i = 0; i < data.Length; i++)
        {
            char c = data[i];
            if (!inQ)
            {
                if (c == '"') inQ = true;
                continue;
            }
            if (c == '\\' && i + 1 < data.Length) { sb.Append(data[++i]); continue; }
            if (c == '"') { inQ = false; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static async Task<IReadOnlyList<string>?> DohTxtAsync(string endpoint, string name, CancellationToken ct)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = DefaultTimeout };
        http.MaxResponseContentBufferSize = 64 * 1024;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?name={Uri.EscapeDataString(name)}&type=TXT");
        req.Headers.Accept.ParseAdd("application/dns-json");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseDohTxt(body) ?? Array.Empty<string>();
    }

    // ── Windows resolver (dnsapi.dll) ──

    private const ushort DNS_TYPE_TEXT = 16;
    private const uint DNS_QUERY_STANDARD = 0;
    private const int DnsFreeRecordList = 1;
    [ThreadStatic] private static int _lastWindowsStatus;

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int DnsQuery_W(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsRecordListFree(IntPtr list, int freeType);

    /// <summary>Synchronous OS lookup. Empty on no records/failure. Windows only.</summary>
    public static IReadOnlyList<string> QueryWindows(string name)
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;
        IntPtr results = IntPtr.Zero;
        try
        {
            int status = DnsQuery_W(name, DNS_TYPE_TEXT, DNS_QUERY_STANDARD, IntPtr.Zero, out results, IntPtr.Zero);
            _lastWindowsStatus = status;
            if (status != 0 || results == IntPtr.Zero) return list;
            int ptr = IntPtr.Size;
            // DNS_RECORDW: pNext, pName, WORD wType, WORD wDataLength, DWORD Flags, DWORD dwTtl,
            // DWORD dwReserved, then the data union. On x64 the union starts at 32, on x86 at 24.
            int offType = 2 * ptr;
            int offData = ptr == 8 ? 32 : 24;
            for (IntPtr rec = results; rec != IntPtr.Zero; rec = Marshal.ReadIntPtr(rec))
            {
                ushort type = (ushort)Marshal.ReadInt16(rec, offType);
                if (type != DNS_TYPE_TEXT) continue;
                // DNS_TXT_DATAW { DWORD dwStringCount; PWSTR pStringArray[1]; } — the array is
                // pointer-aligned right after the count.
                int count = Marshal.ReadInt32(rec, offData);
                int offArr = offData + (ptr == 8 ? 8 : 4);
                var sb = new StringBuilder();
                for (int i = 0; i < count && i < 64; i++)
                {
                    IntPtr ps = Marshal.ReadIntPtr(rec, offArr + i * ptr);
                    if (ps != IntPtr.Zero) sb.Append(Marshal.PtrToStringUni(ps));
                }
                if (sb.Length > 0) list.Add(sb.ToString());
            }
            return list;
        }
        catch
        {
            return list;
        }
        finally
        {
            if (results != IntPtr.Zero) { try { DnsRecordListFree(results, DnsFreeRecordList); } catch { } }
        }
    }
}
