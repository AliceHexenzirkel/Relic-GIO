namespace Relic.Core.Play;

/// <summary>What of THIS launcher holds — or is about to take — the play-session gate
/// (<see cref="PlaySession.SessionGateName"/>).</summary>
public enum GateHolder
{
    /// <summary>Nothing of this launcher: a gate found held is held by another Relic process.</summary>
    None,
    /// <summary>A play session (the PLAY button, or the desktop shortcut's session run in the launcher).</summary>
    PlaySession,
    /// <summary>An install, an import or a voice download (the install job).</summary>
    InstallJob,
    /// <summary>The removal of a version.</summary>
    Removal,
    /// <summary>Settings' "Apply Relic's game files to every installed version".</summary>
    GameFiles,
    /// <summary>A recovery pass of this launcher (its start, the recovery loop's, Restore, the exit), counted where it
    /// holds the gate.</summary>
    RecoveryPass,
}

/// <summary>
/// Who holds the play-session gate, in the words the log uses. Every holder in the launcher takes the gate with a
/// zero timeout, so the recovery loop leaves the gate alone while one of them is at work (a pass of its own would
/// only make that job fail on the gate), and a pass that finds the gate held names the holder instead of guessing.
/// </summary>
public static class SessionGate
{
    /// <summary>The words for a holder outside this launcher: the play process of a desktop shortcut pressed while
    /// the launcher runs, the uninstaller's cleanup, the dev-only test runner.</summary>
    public const string OtherProcess = "another Relic process (a desktop-shortcut play session, the uninstaller or the dev test runner)";

    /// <summary>
    /// The holder of this launcher from its own flags. A recovery pass comes first: it is counted while it really
    /// holds the gate, where every other holder sets its flag a moment BEFORE it asks for the gate (a session or a job
    /// may be waiting for that very pass). Then a play session: an install asked for during one holds the install
    /// guard for a moment before it fails on the gate. The install guard is shared by the install job, a version
    /// removal and "Apply Relic's game files": <paramref name="filesApplying"/> names the last, a cancellable install
    /// job (<paramref name="installJob"/>) the first, and the guard alone the removal.
    /// </summary>
    public static GateHolder InProcess(bool playInProgress, bool installBusy, bool filesApplying, bool installJob,
        bool recoveryPass = false)
    {
        if (recoveryPass) return GateHolder.RecoveryPass;
        if (playInProgress) return GateHolder.PlaySession;
        if (installBusy)
        {
            if (filesApplying) return GateHolder.GameFiles;
            return installJob ? GateHolder.InstallJob : GateHolder.Removal;
        }
        return GateHolder.None;
    }

    /// <summary>The holder as a noun (<see cref="OtherProcess"/> for <see cref="GateHolder.None"/>).</summary>
    public static string Describe(GateHolder holder) => holder switch
    {
        GateHolder.PlaySession => "a play session in this launcher",
        GateHolder.InstallJob => "an install, an import or a voice download in this launcher",
        GateHolder.Removal => "a version removal in this launcher",
        GateHolder.GameFiles => "Apply Relic's game files in this launcher",
        GateHolder.RecoveryPass => "a recovery pass of this launcher",
        _ => OtherProcess,
    };

    /// <summary>What "the session gate is …" says for <paramref name="holder"/>. Another process and a recovery pass
    /// of this launcher are certain holders; a holder known by its flag may also be about to take the gate, while
    /// another process holds it.</summary>
    public static string HeldBy(GateHolder holder) => holder switch
    {
        GateHolder.None or GateHolder.RecoveryPass => "held by " + Describe(holder),
        _ => "held (or about to be taken) by " + Describe(holder),
    };

    /// <summary>Whether <paramref name="holder"/> puts the official profile back itself when it is done: a play
    /// session, a version removal (of the version whose profile is loaded) and a recovery pass do; an install, an
    /// import, a voice download and Apply Relic's game files never touch the profile.</summary>
    public static bool PutsProfileBack(GateHolder holder) =>
        holder is GateHolder.PlaySession or GateHolder.Removal or GateHolder.RecoveryPass;
}
