using System.Text.Json;
using Relic.Core.Util;

namespace Relic.Core.Server;

/// <summary>One job the Server page runs after an install, in order: Repoint = POST /server/relocate {dir, mode:
/// repoint} first; Fetch = the package download (after the repoint when both).</summary>
public sealed record FetchStep(string Version, string Dir, bool Repoint, bool Fetch);

/// <summary>A download an install chose that the Server page is NOT handed, and why (<paramref name="Message"/>, a
/// sentence for the install log and the finished overlay: what keeps it back and what to do).</summary>
public sealed record FetchHeld(string Version, string Message);

/// <summary>What an install hands the Server page: the jobs to run one after the other (<paramref name="Steps"/>,
/// in the plan's version order), the downloads held back (<paramref name="Held"/>) and the lines for the install
/// log that need no action (<paramref name="Notes"/>).</summary>
public sealed record FetchChain(IReadOnlyList<FetchStep> Steps, IReadOnlyList<FetchHeld> Held, IReadOnlyList<string> Notes);

/// <summary>
/// What follows an agent install: the downloads it chose, and the folders a running agent does not know yet,
/// decided from what the agent that answers AFTER the install says. A re-install on this PC over an agent that is
/// running does not restart it (that would kill a job it may be running): the process keeps the version table and
/// the token it read when it started, until it is restarted. So a download of a version that process has no
/// folder for is a repoint first — by the Server page, which follows the relocate job and then the fetch —, a
/// version that process has in ANOTHER folder waits for a restart, and nothing is ever repointed that the agent
/// already has a folder for. Over SSH the installer restarts the agent, which then knows the folders the box keeps.
/// </summary>
public static class InstallFollowUp
{
    /// <summary>The jobs an install hands the Server page, decided from what the agent that answers after it says
    /// (its /status) -- never repointing a version that agent already has a folder for. Pure; never throws.</summary>
    /// <param name="plan">The plan as the install left it (stored folders in place: LocalAgent.Install /
    /// AgentDeployer.Install change it).</param>
    /// <param name="downloads">The versions that really need the download (the install's answer: a download whose
    /// folder already holds a stack is not one).</param>
    /// <param name="status">The /status answer of the agent after the install; null when it was not asked or did
    /// not answer.</param>
    /// <param name="sameProcess">The agent that answers is the process that ran before the install (on this PC: the
    /// start handed back the pid it had) — it has not read the configuration just written.</param>
    /// <param name="tokenChanged">The install wrote another token than the one that process runs with.</param>
    /// <param name="statusError">Why /status could not be read; "" when it was not asked.</param>
    /// <param name="pageRunsChain">The page that started the install runs chains (a repoint, then a fetch);
    /// one that does not queues the downloads alone.</param>
    /// <param name="fetching">The versions a download job of that agent runs for, as its jobs list names them
    /// (<see cref="RunningFetches"/>) — asked when /status names another job as busy
    /// (<see cref="BusyMayHideFetch"/>); null when it was not asked.</param>
    public static FetchChain Decide(AgentInstallPlan plan, IReadOnlyList<string> downloads, JsonElement? status,
        bool sameProcess, bool tokenChanged, string statusError, bool pageRunsChain, IReadOnlyCollection<string>? fetching = null)
    {
        var steps = new List<FetchStep>();
        var held = new List<FetchHeld>();
        var notes = new List<string>();
        try
        {
            var versions = Member(status, "versions");
            var busy = Member(status, "busy");
            bool FetchRuns(string v) =>
                (busy is { ValueKind: JsonValueKind.Object } && Text(Member(busy, "kind")) == "fetch" && Text(Member(busy, "version")) == v)
                || (fetching?.Contains(v) ?? false);
            foreach (var v in plan.Versions)
            {
                string dir = plan.DirFor(v);
                var e = Member(versions, v);
                bool isObject = e is { ValueKind: JsonValueKind.Object };
                string agentDir = Text(Member(e, "dir"));
                bool configured = Member(e, "configured") switch
                {
                    { ValueKind: JsonValueKind.True } => true,
                    { ValueKind: JsonValueKind.False } => false,
                    _ => agentDir.Length > 0,
                };
                if (!downloads.Contains(v))
                {
                    // A folder the form named that the agent which kept running does not know: the page points the
                    // agent at it, so the version is usable without a restart (nothing is downloaded or moved).
                    if (sameProcess && !tokenChanged && IsRepointCandidate(plan, v) && isObject && !configured && pageRunsChain)
                    {
                        steps.Add(new FetchStep(v, dir, Repoint: true, Fetch: false));
                        notes.Add(L.T("core.deploy.chainRepointOnly", new { version = v, dir }));
                    }
                    continue;
                }
                if (sameProcess && tokenChanged)
                {
                    held.Add(new FetchHeld(v, L.T("core.deploy.chainRestartToken", new { version = v })));
                    continue;
                }
                if (status is null)
                {
                    held.Add(new FetchHeld(v, statusError.Length > 0
                        ? L.T("core.deploy.chainNoStatus", new { version = v, error = statusError })
                        : L.T("core.deploy.chainNoHealth", new { version = v })));
                    continue;
                }
                if (!isObject)
                {
                    held.Add(new FetchHeld(v, L.T("core.deploy.chainUnknownVersion", new { version = v })));
                    continue;
                }
                if (FetchRuns(v))
                {
                    notes.Add(L.T("core.deploy.chainAlreadyRunning", new { version = v }));
                    continue;
                }
                if (Member(e, "present") is { ValueKind: JsonValueKind.True })
                {
                    notes.Add(L.T("core.deploy.chainPresent", new { version = v, dir = agentDir }));
                    continue;
                }
                if (!configured)
                {
                    if (!sameProcess) held.Add(new FetchHeld(v, L.T("core.deploy.chainNotConfigured", new { version = v })));
                    else if (!pageRunsChain) held.Add(new FetchHeld(v, L.T("core.deploy.chainNeedsRestart", new { version = v, dir })));
                    else
                    {
                        steps.Add(new FetchStep(v, dir, Repoint: true, Fetch: true));
                        notes.Add(L.T("core.deploy.chainRepoint", new { version = v, dir }));
                    }
                    continue;
                }
                bool sameFolder = AgentInstallPlan.SameFolder(agentDir, dir, plan.IsWindows);
                // The process that kept running holds another folder for the version than the configuration
                // names now: repointing it would undo what the configuration says, downloading would fill the
                // old folder. Only a restart makes the two one.
                if (!sameFolder && sameProcess)
                {
                    held.Add(new FetchHeld(v, L.T("core.deploy.chainRestartFirst", new { version = v, running = agentDir, dir })));
                    continue;
                }
                string problem = Text(Member(Member(e, "fetch"), "problem"));
                if (problem.Trim().Length > 0)
                {
                    held.Add(new FetchHeld(v, L.T("core.deploy.chainRefused", new { version = v, problem })));
                    continue;
                }
                steps.Add(new FetchStep(v, agentDir, Repoint: false, Fetch: true));
                // Another folder after a fresh start: the installer kept the folder the box already uses.
                if (!sameFolder) notes.Add(L.T("core.deploy.chainKeptDir", new { version = v, dir = agentDir }));
            }
        }
        catch (Exception ex)
        {
            // Nothing above should throw; if it does, no download is handed on blindly.
            Log.Error("install follow-up: the agent's answer could not be read", ex);
            steps.Clear();
            notes.Clear();
            held.Clear();
            foreach (var v in plan.Versions.Where(downloads.Contains))
                held.Add(new FetchHeld(v, L.T("core.deploy.chainNoStatus", new { version = v, error = ex.Message })));
        }
        return new FetchChain(steps, held, notes);
    }

    /// <summary>Whether the follow-up needs the /status of the agent after the install (<see cref="Decide"/>
    /// reads it): there is a download to decide, or a folder a process that kept running could be pointed at —
    /// and the launcher can still ask that process (its token did not change).</summary>
    public static bool NeedsStatus(AgentInstallPlan plan, IReadOnlyList<string> downloads, bool sameProcess, bool tokenChanged) =>
        !(sameProcess && tokenChanged)
        && (plan.Versions.Any(downloads.Contains) || (sameProcess && plan.Versions.Any(v => IsRepointCandidate(plan, v))));

    /// <summary>Whether a download may run on the agent although its /status does not name one: <c>busy</c> names
    /// the job the agent started LAST, and a download that is in its long phase lets the agent's own jobs (a
    /// service revived, the Spiral Abyss calendar, the advertised address) start beside it — while
    /// <c>busy</c> names such a job, only the agent's jobs list (<see cref="RunningFetches"/>) tells. False when
    /// nothing is busy or busy is a download (it names its version itself), and for an answer of another shape.
    /// Pure; never throws.</summary>
    public static bool BusyMayHideFetch(JsonElement? status)
    {
        var busy = Member(status, "busy");
        return busy is { ValueKind: JsonValueKind.Object } && Text(Member(busy, "kind")) != "fetch";
    }

    /// <summary>The versions a download job of the agent runs for, read off its jobs list as
    /// <see cref="AgentClient.JobsAsync"/> hands it on (<c>{jobs: [{kind, version, state}]}</c>): every job of kind
    /// <c>fetch</c> whose state is <c>running</c>. Empty for an answer of another shape. Pure; never throws.</summary>
    public static IReadOnlyList<string> RunningFetches(JsonElement? jobs)
    {
        var found = new List<string>();
        if (Member(jobs, "jobs") is not { ValueKind: JsonValueKind.Array } list) return found;
        foreach (var job in list.EnumerateArray())
        {
            string version = Text(Member(job, "version"));
            if (Text(Member(job, "kind")) == "fetch" && Text(Member(job, "state")) == "running" && version.Length > 0
                && !found.Contains(version))
                found.Add(version);
        }
        return found;
    }

    /// <summary>A version a running agent may be pointed at without a download: its package is in the folder the
    /// form named (I have it — or a download whose folder already holds a stack), and that folder is not one the
    /// configuration already stored.</summary>
    private static bool IsRepointCandidate(AgentInstallPlan plan, string v) =>
        plan.ChoiceFor(v) is StackChoice.Have or StackChoice.Download
        && !string.IsNullOrWhiteSpace(plan.DirFor(v)) && !plan.FolderIsStored(v);

    /// <summary>The member <paramref name="name"/> of an object; null when <paramref name="e"/> is no object or lacks it.</summary>
    private static JsonElement? Member(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) ? v : null;

    /// <summary>A string member's text; "" for anything that is not a string.</summary>
    private static string Text(JsonElement? e) => e is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";
}
