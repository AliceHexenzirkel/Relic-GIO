using System.Net;
using Relic.Core.Util;

namespace Relic.Core.Server;

/// <summary>
/// The one thing a player types: <c>host</c>, <c>host:port</c>, <c>[v6]:port</c> (a scheme or trailing
/// slash is tolerated and stripped). The game port defaults to 21000 and the agent port to 18080;
/// a DNS name may publish both in a TXT record (<see cref="DnsTxt"/>), which is consulted only when
/// no explicit port was given — an explicit <c>host:port</c> is never overridden.
/// </summary>
public sealed record ServerAddress(string Host, int? Port)
{
    public const int DefaultGamePort = 21000;
    public const int DefaultAgentPort = 18080;

    /// <summary>Where the effective port came from — shown in the UI ("port 21000 (from TXT record)").</summary>
    public const string SourceExplicit = "explicit", SourceTxt = "txt", SourceDefault = "default";

    /// <summary>A resolved address. <paramref name="AgentPortFromTxt"/> is non-null ONLY when a TXT
    /// record actually published an <c>agent=</c> port — callers must not infer that from AgentPort,
    /// which is filled with the default when nothing published one (a record carrying only <c>port=</c>,
    /// or a record that published 18080, are otherwise indistinguishable).</summary>
    public readonly record struct Resolved(string Host, int Port, int AgentPort, string Source, int? AgentPortFromTxt = null);

    public bool IsIpLiteral => IPAddress.TryParse(Host, out _);

    /// <summary>Host as it must appear inside a URL / Fiddler rule: IPv6 literals bracketed.</summary>
    public string HostForUrl => IPAddress.TryParse(Host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? "[" + Host + "]" : Host;

    public static bool TryParse(string? input, out ServerAddress? address, out string? error)
    {
        address = null; error = null;
        string s = (input ?? "").Trim();
        if (s.Length == 0) { error = "empty"; return false; }
        // scheme / path noise: "http://host:21000/" -> "host:21000"
        int schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0) s = s[(schemeIdx + 3)..];
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        s = s.Trim();
        if (s.Length == 0) { error = "empty"; return false; }

        string host; int? port = null;
        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close < 0) { error = "unclosed [ipv6]"; return false; }
            host = s[1..close];
            string rest = s[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':') || !TryPort(rest[1..], out port)) { error = "bad port"; return false; }
            }
            if (!IPAddress.TryParse(host, out var v6) || v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            { error = "bad ipv6"; return false; }
        }
        else if (s.Count(c => c == ':') > 1)
        {
            // bare IPv6 literal without brackets: no port possible
            if (!IPAddress.TryParse(s, out var v6) || v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            { error = "bad host"; return false; }
            host = s;
        }
        else
        {
            int colon = s.IndexOf(':');
            if (colon >= 0)
            {
                host = s[..colon];
                if (!TryPort(s[(colon + 1)..], out port)) { error = "bad port"; return false; }
            }
            else host = s;
            host = host.Trim().TrimEnd('.');
            if (host.Length == 0 || host.Length > 253 || host.Any(char.IsWhiteSpace)) { error = "bad host"; return false; }
            if (!IPAddress.TryParse(host, out _) && !Uri.CheckHostName(host).Equals(UriHostNameType.Dns)) { error = "bad host"; return false; }
        }
        address = new ServerAddress(host, port);
        return true;
    }

    private static bool TryPort(string s, out int? port)
    {
        port = null;
        if (int.TryParse(s.Trim(), out int p) && p is > 0 and < 65536) { port = p; return true; }
        return false;
    }

    /// <summary>Effective ports: explicit → as typed; IP literal → defaults; DNS name → TXT
    /// (<c>_relic.&lt;host&gt;</c>, then <c>&lt;host&gt;</c>) within the timeout, else defaults.</summary>
    public async Task<Resolved> ResolveAsync(CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (Port is int p) return new Resolved(Host, p, DefaultAgentPort, SourceExplicit);
        if (IsIpLiteral) return new Resolved(Host, DefaultGamePort, DefaultAgentPort, SourceDefault);
        try
        {
            foreach (var name in new[] { "_relic." + Host, Host })
            {
                var recs = await DnsTxt.QueryAsync(name, ct, timeout).ConfigureAwait(false);
                var pick = DnsTxt.PickRelic(recs);
                if (pick is { } r && (r.Port is not null || r.AgentPort is not null))
                    return new Resolved(Host, r.Port ?? DefaultGamePort, r.AgentPort ?? DefaultAgentPort, SourceTxt, r.AgentPort);
            }
        }
        catch { /* defaults */ }
        return new Resolved(Host, DefaultGamePort, DefaultAgentPort, SourceDefault);
    }

    public override string ToString() => Port is int p ? $"{HostForUrl}:{p}" : Host;
}
