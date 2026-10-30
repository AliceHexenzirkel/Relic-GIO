using System.Net;
using System.Net.Sockets;
using Relic.Core.Util;

namespace Relic.Core.Setup;

/// <summary>What one address service answered: its address (<see cref="Service"/>, the https URL asked) and the
/// first line of its answer, "" when it gave none.</summary>
public sealed record ServiceAnswer(string Service, string Answer);

/// <summary>The public address learned from the address services: <see cref="Address"/> when two of them agree on
/// one a player outside could be handed, else null and <see cref="Problem"/> says why. <see cref="Answers"/> lists
/// what each service said, in the order they were asked.</summary>
public sealed record PublicIpResult(string? Address, IReadOnlyList<ServiceAnswer> Answers, string? Problem);

/// <summary>
/// The public IPv4 address of a server, as Quick Start learns it: from two address services that agree (asked on
/// the server itself over SSH, or from this PC for a server that runs here), held to the agent's own rule for an
/// address it may advertise (<c>advertisable_ip</c> in gio_agent.py).
/// </summary>
public static class PublicAddress
{
    /// <summary>Spikes only: the documentation ranges (RFC 5737: 192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24)
    /// count as public, as the agent's selftest counts them through its own seam. Off = they are not public,
    /// which is what they are.</summary>
    public static bool DocumentationRangesArePublic { get; set; }

    private static readonly (uint Net, int Bits)[] NotGlobal =
    {
        // The IANA special-purpose ranges Python's ipaddress counts as not global (is_private), the four the
        // agent refuses outright included; 100.64.0.0/10 (CGNAT) is neither private nor global there.
        (Ip(0, 0, 0, 0), 8), (Ip(10, 0, 0, 0), 8), (Ip(127, 0, 0, 0), 8), (Ip(169, 254, 0, 0), 16),
        (Ip(172, 16, 0, 0), 12), (Ip(192, 0, 0, 0), 24), (Ip(192, 0, 0, 170), 31), (Ip(192, 168, 0, 0), 16),
        (Ip(198, 18, 0, 0), 15), (Ip(240, 0, 0, 0), 4), (Ip(255, 255, 255, 255), 32), (Ip(100, 64, 0, 0), 10),
    };

    private static readonly (uint Net, int Bits)[] Documentation =
    {
        (Ip(192, 0, 2, 0), 24), (Ip(198, 51, 100, 0), 24), (Ip(203, 0, 113, 0), 24),
    };

    /// <summary>The two hosts of 192.0.0.0/24 that are global all the same (the registry's exceptions).</summary>
    private static readonly uint[] GlobalExceptions = { Ip(192, 0, 0, 9), Ip(192, 0, 0, 10) };

    private static readonly (uint Net, int Bits)[] Rfc1918 =
    {
        (Ip(10, 0, 0, 0), 8), (Ip(172, 16, 0, 0), 12), (Ip(192, 168, 0, 0), 16),
    };

    private static uint Ip(int a, int b, int c, int d) => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | (uint)d;

    private static bool In(uint ip, (uint Net, int Bits) range)
    {
        uint mask = range.Bits == 0 ? 0u : uint.MaxValue << (32 - range.Bits);
        return (ip & mask) == (range.Net & mask);
    }

    /// <summary>An IPv4 address written in full ("192.0.2.10" — no shorthand such as "192.0.2", no leading
    /// zeros), as a number; null for anything else. Pure.</summary>
    public static uint? ParseIpv4(string? text)
    {
        string s = (text ?? "").Trim();
        if (!IPAddress.TryParse(s, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != s) return null;
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    /// <summary>
    /// The agent's <c>advertisable_ip(addr, allow_private)</c>: an IPv4 address a player outside could be handed —
    /// a public one, or a private RFC 1918 one when <paramref name="allowPrivate"/>. Never an IPv6 address, 0/8,
    /// loopback, link-local, multicast or reserved, and never CGNAT 100.64/10 (the provider's side of a carrier
    /// NAT, which is neither public nor private). The documentation ranges are not public
    /// (<see cref="DocumentationRangesArePublic"/>). Pure.
    /// </summary>
    public static bool IsAdvertisable(string? address, bool allowPrivate)
    {
        if (ParseIpv4(address) is not { } ip) return false;
        if (In(ip, (Ip(0, 0, 0, 0), 8)) || In(ip, (Ip(127, 0, 0, 0), 8)) || In(ip, (Ip(169, 254, 0, 0), 16))
            || In(ip, (Ip(224, 0, 0, 0), 4)) || In(ip, (Ip(240, 0, 0, 0), 4)))
            return false;
        return IsGlobal(ip) || (allowPrivate && Rfc1918.Any(r => In(ip, r)));
    }

    private static bool IsGlobal(uint ip)
    {
        if (Documentation.Any(r => In(ip, r))) return DocumentationRangesArePublic;
        if (GlobalExceptions.Contains(ip)) return true;
        return !NotGlobal.Any(r => In(ip, r));
    }

    /// <summary>The service's name as the screen shows it: the host of its URL.</summary>
    public static string ServiceName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : (url ?? "").Trim();

    /// <summary>
    /// The address two services give alike: the first address, in the order the answers came, that at least two
    /// answers name (each answer read as its first line, an IPv4 address written in full) and that
    /// <see cref="IsAdvertisable"/> passes without private addresses. Otherwise no address and the reason: two
    /// services agree on an address that is not public (<c>core.setup.publicIp.notPublic</c>), they disagree
    /// (<c>core.setup.publicIp.disagree</c>), or fewer than two gave an address at all
    /// (<c>core.setup.publicIp.none</c>, with <paramref name="errors"/> — what went wrong asking — in its text).
    /// Pure.
    /// </summary>
    public static PublicIpResult Agree(IReadOnlyList<ServiceAnswer> answers, IReadOnlyList<string>? errors = null)
    {
        var list = answers ?? Array.Empty<ServiceAnswer>();
        var addresses = list.Select(a => FirstLine(a.Answer)).Select(a => ParseIpv4(a) is null ? null : a).ToList();
        var counts = addresses.Where(a => a is not null).GroupBy(a => a!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var agreed = addresses.Where(a => a is not null && counts[a] >= 2).Select(a => a!).Distinct(StringComparer.Ordinal).ToList();
        foreach (var a in agreed)
            if (IsAdvertisable(a, allowPrivate: false)) return new PublicIpResult(a, list, null);
        if (agreed.Count > 0)
            return new PublicIpResult(null, list, L.T("core.setup.publicIp.notPublic", new { address = agreed[0] }));
        int valid = addresses.Count(a => a is not null);
        if (valid >= 2)
        {
            string said = string.Join(", ", list.Where(a => ParseIpv4(FirstLine(a.Answer)) is not null)
                .Select(a => ServiceName(a.Service) + ": " + FirstLine(a.Answer)));
            return new PublicIpResult(null, list, L.T("core.setup.publicIp.disagree", new { answers = said }));
        }
        var why = (errors ?? Array.Empty<string>()).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        if (why.Count == 0)
            why = list.Where(a => FirstLine(a.Answer).Length > 0 && ParseIpv4(FirstLine(a.Answer)) is null)
                .Select(a => ServiceName(a.Service) + ": " + FirstLine(a.Answer)).ToList();
        return new PublicIpResult(null, list, L.T("core.setup.publicIp.none", new { error = why.Count > 0 ? string.Join("; ", why) : "-" }));
    }

    private static string FirstLine(string? text)
    {
        string s = (text ?? "").Trim();
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0) s = s[..nl].Trim();
        return s.Length > 64 ? s[..64] : s;
    }

    /// <summary>
    /// This PC's public address, asked of every service in <paramref name="services"/> at once (https, never through
    /// the system proxy — Fiddler may be it —, 6 s each) and decided by <see cref="Agree"/>. <paramref name="fetch"/>
    /// replaces the request (spikes). Never throws for a service that fails: its answer is "" and its error goes
    /// into the reason.
    /// </summary>
    public static async Task<PublicIpResult> LookupAsync(IReadOnlyList<string> services,
        Func<string, CancellationToken, Task<string>>? fetch = null, CancellationToken ct = default)
    {
        var urls = (services ?? Array.Empty<string>()).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (urls.Count == 0)
            return new PublicIpResult(null, Array.Empty<ServiceAnswer>(), L.T("core.setup.publicIp.none", new { error = "-" }));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(6) };
        fetch ??= async (url, token) =>
        {
            using var resp = await http.GetAsync(url, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        };
        var errors = new string[urls.Count];
        var tasks = urls.Select(async (url, i) =>
        {
            try { return FirstLine(await fetch(url, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors[i] = ServiceName(url) + ": " + (ex is OperationCanceledException ? "timeout" : ex.Message);
                return "";
            }
        }).ToList();
        var said = await Task.WhenAll(tasks).ConfigureAwait(false);
        var answers = urls.Select((u, i) => new ServiceAnswer(u, said[i])).ToList();
        var result = Agree(answers, errors.Where(e => e is not null).Select(e => e!).ToList());
        // The outcome alone: this PC's address and the services' answers stay out of relic.log, which users attach to
        // a request for help.
        Log.Info("public address from this PC: " + (result.Address is not null ? "two services agree" : "not learned")
            + " (" + string.Join(", ", answers.Select(a => ServiceName(a.Service) + (a.Answer.Length > 0 ? " answered" : " gave no answer"))) + ")");
        return result;
    }
}
