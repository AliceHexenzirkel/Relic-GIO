using Relic.Core.Isolation;
using Relic.Core.Util;

namespace Relic.Core.Play;

/// <summary>The session's clients and the steps the post-exit decision acts through (<see cref="SessionEnd.FinishAsync"/>):
/// the real ones in a play session, recorders in the session spike. The settle window is not one of them: its length is
/// <see cref="SessionEnd.FinishAsync"/>'s own, so no caller can hand the decision a shorter window.</summary>
public sealed class SessionEndSteps
{
    /// <summary>The game folders a client of the session runs from (a client is attributed by its full path).</summary>
    public required IReadOnlyList<string> OurGameDirs { get; init; }

    /// <summary>The pids of the clients that ran before the launch: a client whose path cannot be read counts as the
    /// session's own only when it is not one of them (null: no snapshot — such a client cannot be attributed).</summary>
    public IReadOnlyCollection<int>? PreExistingPids { get; init; }

    /// <summary>The image name the clients run under, without ".exe": the game's, or the session spike's stand-ins'
    /// (a stand-in called GenshinImpact.exe would be a client to every Relic on the same PC).</summary>
    public string ProcessName { get; init; } = ProcessGuard.ClientProcessName;

    /// <summary>Stop Fiddler (it must not keep redirecting a client once ours is gone).</summary>
    public required Action StopFiddler { get; init; }

    /// <summary>Save the session's profile into its slot and load the official one.</summary>
    public required Action RestoreLive { get; init; }

    /// <summary>The official client started on the still-loaded profile: snapshot the slot and mark it tainted.</summary>
    public required Action Taint { get; init; }

    /// <summary>relic.log.</summary>
    public Action<string> Diag { get; init; } = m => Log.Info(m);

    /// <summary>The Fiddler this session started (null: it reused one that was running).</summary>
    public int? OwnedFiddlerPid { get; init; }
}

/// <summary>
/// The end of a play session once its client is gone: ONE sustained settle window over the session's clients
/// (<see cref="ProcessGuard.SettleWindowFor"/>: 5 s, 15 s when the client was never seen — chosen here, never by the
/// caller), then the post-exit decision (<see cref="PlaySession.PostExitDecision"/>) — Fiddler and the profile are
/// SEPARATE decisions, and Fiddler is never stopped before the window has ended —, then what it says
/// (<see cref="SessionNotice"/>).
/// </summary>
public static class SessionEnd
{
    /// <summary>The notice of the moment the session's client is seen gone, before the settle window: what the
    /// session is about to do. <paramref name="fiddler"/>: the session runs Fiddler.</summary>
    public static SessionNotice Closing(string versionId, bool fiddler) =>
        new(SessionNotice.KindClosing, fiddler ? L.T("core.play.closing") : L.T("core.play.closingNoFiddler"))
        {
            VersionId = versionId,
            Appeared = true,
        };

    /// <summary>
    /// The post-exit step. <paramref name="fiddler"/>: the session runs Fiddler, so a decision to stop it applies.
    /// <paramref name="clientSeen"/>: the session saw its client. <paramref name="clientNeverAppeared"/>: the wait for
    /// the client ended without one (the longer window, and the result says the game was not detected).
    /// <paramref name="say"/>: the session's own lines (the user-facing log). Returns what the step did.
    /// </summary>
    public static async Task<SessionNotice> FinishAsync(SessionEndSteps steps, string versionId, bool fiddler,
        bool clientSeen, bool clientNeverAppeared, Action<string>? say)
    {
        var settle = await ProcessGuard.ObserveSustainedAsync(steps.OurGameDirs, steps.PreExistingPids,
            ProcessGuard.SettleWindowFor(clientNeverAppeared), steps.ProcessName);
        var decision = PlaySession.PostExitDecision(settle.SawOurs, settle.SawForeign, settle.SawUnknown);
        steps.Diag($"post-exit: ours={settle.SawOurs} foreign={settle.SawForeign} unknown={settle.SawUnknown} -> stopFiddler={decision.StopFiddler} restore={decision.RestoreProfile} ({decision.Reason})");
        if (!decision.RestoreProfile)
        {
            // Record what actually blocked us, so a future deferral is diagnosable from
            // relic.log instead of guessable.
            foreach (var g in ProcessGuard.Running(steps.ProcessName))
                steps.Diag($"profile restore deferred: {steps.ProcessName}.exe pid={g.Pid} path='{g.Path}'");
        }

        bool fiddlerStopped = false;
        if (fiddler && decision.StopFiddler)
        {
            say?.Invoke(L.T("core.play.stoppingFiddler"));
            steps.Diag(steps.OwnedFiddlerPid is int pid ? $"stopping Fiddler (own pid {pid})" : "stopping Fiddler (reused instance)");
            steps.StopFiddler();
            fiddlerStopped = true;
        }

        if (decision.RestoreProfile)
        {
            say?.Invoke(L.T("core.play.savingProfile"));
            steps.RestoreLive();
            say?.Invoke(L.T("core.play.liveRestored"));
            return Outcome(SessionNotice.KindRestored,
                fiddler ? L.T("core.play.endedRestored") : L.T("core.play.endedRestoredNoFiddler"),
                versionId, fiddlerStopped, restored: true, clientSeen, clientNeverAppeared);
        }
        if (decision.StopFiddler)
        {
            steps.Taint();
            say?.Invoke(L.T("core.play.deferredForeign"));
            return Outcome(SessionNotice.KindDeferredForeign, L.T("core.play.endedDeferredForeign"),
                versionId, fiddlerStopped, restored: false, clientSeen, clientNeverAppeared);
        }
        say?.Invoke(L.T("core.play.deferredOwn"));
        return Outcome(SessionNotice.KindDeferredOwn, L.T("core.play.endedDeferredOwn"),
            versionId, fiddlerStopped, restored: false, clientSeen, clientNeverAppeared);
    }

    /// <summary>The result notice; a session whose client never appeared says so first.</summary>
    private static SessionNotice Outcome(string kind, string text, string versionId, bool fiddlerStopped, bool restored,
        bool clientSeen, bool clientNeverAppeared) =>
        new(kind, clientNeverAppeared ? L.T("core.play.gameNotDetected") + " " + text : text)
        {
            VersionId = versionId,
            FiddlerStopped = fiddlerStopped,
            Restored = restored,
            Appeared = clientSeen,
        };
}
