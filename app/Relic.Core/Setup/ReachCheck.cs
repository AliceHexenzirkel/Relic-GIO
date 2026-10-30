using Relic.Core.Server;
using Relic.Core.Util;

namespace Relic.Core.Setup;

/// <summary>What the reach check found: the game's login port (TCP 21000, <see cref="SdkReachable"/> with the
/// probe's own word in <see cref="SdkDetail"/>) and the agent's port (<see cref="AgentReachable"/>), the verdict
/// (<c>ok</c> — both answer, <c>partial</c> — the login port only, <c>fail</c> — otherwise), whether this PC is
/// inside the server's network as the page judged it, and the text key that says it.</summary>
public sealed record ReachResult(bool SdkReachable, string SdkDetail, bool AgentReachable, string Verdict, bool Inside, string TextKey);

/// <summary>
/// The reach check of a server players on the Internet are to join: from this PC, TCP to the game's login port
/// and to the agent at the server's public address. TCP only — whether the game's UDP port 21081 gets through is
/// not tested, and every text says so. No third-party service is asked.
/// </summary>
public static class ReachCheck
{
    /// <summary>The game's login port, the one a player's client connects to first.</summary>
    public const int LoginPort = 21000;

    /// <summary>ok / partial / fail from the two answers. Pure.</summary>
    public static string Verdict(bool sdkReachable, bool agentReachable) =>
        sdkReachable && agentReachable ? "ok" : sdkReachable ? "partial" : "fail";

    /// <summary>The text that says a verdict: <c>qs.reach.partial</c>, or <c>qs.reach.ok</c> / <c>qs.reach.fail</c>
    /// with <c>Inside</c> (this PC is in the server's network: the check goes through the router's NAT loopback)
    /// or <c>Outside</c>. Pure.</summary>
    public static string TextKey(string verdict, bool inside) => verdict switch
    {
        "ok" => inside ? "qs.reach.okInside" : "qs.reach.okOutside",
        "partial" => "qs.reach.partial",
        _ => inside ? "qs.reach.failInside" : "qs.reach.failOutside",
    };

    /// <summary>Spikes only: replaces the two probes — (address, port) → reachable, detail.</summary>
    public static Func<string, int, Task<(bool Reachable, string Detail)>>? ProbeOverride { get; set; }

    /// <summary>
    /// Probe <paramref name="address"/>: the login port through <see cref="ServerProbe.ProbeSdkAsync"/> (6 s) and
    /// <c>http://&lt;address&gt;:&lt;agentPort&gt;/health</c> (5 s), neither through the system proxy. Never throws for an
    /// address that does not answer; <paramref name="inside"/> comes from the page. The log line names the outcome alone,
    /// never the address: for a server on this PC reached from the Internet it is this PC's own public address.
    /// </summary>
    public static async Task<ReachResult> RunAsync(string address, int agentPort, bool inside, CancellationToken ct = default)
    {
        string host = (address ?? "").Trim();
        bool sdk; string detail; bool agent;
        if (ProbeOverride is { } probe)
        {
            (sdk, detail) = await probe(host, LoginPort).ConfigureAwait(false);
            (agent, _) = await probe(host, agentPort).ConfigureAwait(false);
        }
        else
        {
            var sdkTask = ServerProbe.ProbeSdkAsync(host, LoginPort, TimeSpan.FromSeconds(6), ct);
            var agentTask = HealthAsync(host, agentPort, ct);
            var s = await sdkTask.ConfigureAwait(false);
            sdk = s.Reachable == true;
            detail = s.Detail;
            agent = await agentTask.ConfigureAwait(false);
        }
        string verdict = Verdict(sdk, agent);
        Log.Info($"reach check: login port {(sdk ? "answers" : "silent")} ({detail}), agent port {agentPort} {(agent ? "answers" : "silent")} -> {verdict}");
        return new ReachResult(sdk, detail, agent, verdict, inside, TextKey(verdict, inside));
    }

    /// <summary>Does the agent answer its /health at the address? 5 s, no proxy, never throws.</summary>
    private static async Task<bool> HealthAsync(string host, int port, CancellationToken ct)
    {
        if (port is <= 0 or > 65535) return false;
        string h = host.Contains(':') && !host.StartsWith('[') ? "[" + host + "]" : host;
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
            string body = await http.GetStringAsync($"http://{h}:{port}/health", ct).ConfigureAwait(false);
            return body.Contains("gio-agent", StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }
}
