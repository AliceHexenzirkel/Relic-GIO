namespace Relic.Core.Play;

/// <summary>
/// What a play session or the recovery loop tells the person at the moments that matter, in words (<see cref="Text"/>,
/// through the i18n layer) and as facts: the game has closed and the post-exit step begins
/// (<see cref="KindClosing"/>); what that step did (<see cref="KindRestored"/>, <see cref="KindDeferredOwn"/>,
/// <see cref="KindDeferredForeign"/>); and the recovery loop putting the official profile back after a restore
/// was deferred (<see cref="KindRecovered"/>, <see cref="KindRecoveredDiscarded"/>, <see cref="KindRepaired"/>).
/// The page receives it as the data of play.closing / play.done / profile.changed, the tray as a balloon.
/// </summary>
/// <param name="Kind">One of the <c>Kind…</c> constants.</param>
/// <param name="Text">The sentence to show, in the selected language.</param>
public sealed record SessionNotice(string Kind, string Text)
{
    /// <summary>The session's client is seen gone: the settle window, then Fiddler, then the profile.</summary>
    public const string KindClosing = "closing";

    /// <summary>The session end stopped Fiddler and put the official profile back.</summary>
    public const string KindRestored = "restored";

    /// <summary>The session's own client (or one that cannot be attributed) still runs: Fiddler stays on and the
    /// restore waits for it to close.</summary>
    public const string KindDeferredOwn = "deferredOwn";

    /// <summary>Only another client (the official one) runs: Fiddler is stopped, the restore waits for it to close.</summary>
    public const string KindDeferredForeign = "deferredForeign";

    /// <summary>A pass of the recovery loop put the official profile back once every client had closed.</summary>
    public const string KindRecovered = "recovered";

    /// <summary>As <see cref="KindRecovered"/>, without exporting the loaded profile: the official client had run
    /// on it (a tainted slot keeps its earlier snapshot).</summary>
    public const string KindRecoveredDiscarded = "recoveredDiscarded";

    /// <summary>A pass of the recovery loop repaired an interrupted profile swap and loaded the official profile.</summary>
    public const string KindRepaired = "repaired";

    /// <summary>The version of the session (null for a notice of the recovery loop).</summary>
    public string? VersionId { get; init; }

    /// <summary>The profile slot that was put away (Restored / Discarded by the recovery loop), else null.</summary>
    public string? Profile { get; init; }

    /// <summary>Fiddler was stopped (session end), or does not run once the profile is back (recovery loop).</summary>
    public bool FiddlerStopped { get; init; }

    /// <summary>The official profile is back in place.</summary>
    public bool Restored { get; init; }

    /// <summary>Whether the session saw its client at all; null for a notice of the recovery loop.</summary>
    public bool? Appeared { get; init; }
}
