using Relic.Core.Install;
using Relic.Core.Launch;
using Relic.Core.Server;
using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.App;

/// <summary>
/// The UI half of the checks that precede a launch from the desktop shortcut: the questions put to
/// the user — whether a version whose client is not known to start on this OS should be launched
/// anyway, whether the game starts with Relic's file or with a file of the player's own in its place,
/// and what to do when the server check is not "all good". The decision and the actual agent
/// probe (with retries) live in Core (<see cref="LaunchSupport"/>, <see cref="GamePatcher"/>,
/// <see cref="ServerProbe"/>), where the spikes can cover them; only the dialogs stay here.
/// </summary>
internal static class PlayPreflight
{
    /// <summary>The answer to <see cref="AskOwnFiles"/>: <see cref="OwnFileChoices.Keep"/> or
    /// <see cref="OwnFileChoices.Replace"/>, and whether "Don't ask again for this version" was ticked —
    /// the caller then stores it.</summary>
    public readonly record struct OwnFilesAnswer(string Choice, bool Remember);

    /// <summary>
    /// The game folder of <paramref name="versionId"/> holds a file of the player's own in the place of one
    /// Relic ships as a replacement (<paramref name="files"/>, <see cref="GamePatcher.OwnFiles"/>): which one
    /// does the game start with? A TaskDialog with "Use Relic's file" / "Keep mine" — the default, which Esc
    /// and closing the dialog answer too — and "Don't ask again for this version". The texts are the page's
    /// question. Asked by the caller only while the version's stored choice is "ask". Never throws and never
    /// stops a launch: a dialog that cannot be shown answers "keep". Must be called on the UI thread of
    /// <paramref name="owner"/>; <paramref name="status"/> (the splash's line) is told an answer is awaited.
    /// </summary>
    public static OwnFilesAnswer AskOwnFiles(IWin32Window? owner, string versionId,
        IReadOnlyList<GamePatcher.Replacement> files, Action<string>? status = null)
    {
        status?.Invoke(L.T("shell.splash.waitingConfirm"));
        string names = string.Join(", ", files.Select(f => f.Dst));
        string reasons = string.Join(" ", files.Select(f => f.Reason).Where(r => r.Length > 0));
        string text = L.T("confirm.ownFiles.found", new { id = versionId, files = names })
            + (reasons.Length > 0 ? "\n\n" + L.T("confirm.ownFiles.reason", new { reasons }) : "")
            + "\n\n" + L.T("confirm.ownFiles.choice", new { kept = string.Join(", ", files.Select(f => f.Kept)) });
        Log.Info($"pre-launch question ({versionId}): the player's own {names}");
        var use = new TaskDialogButton(L.T("confirm.ownFiles.yes"));
        var keep = new TaskDialogButton(L.T("confirm.ownFiles.no"));
        var page = new TaskDialogPage
        {
            Caption = L.T("shell.preflight.ownFilesTitle", new { version = versionId }),
            Heading = L.T("confirm.ownFiles.title", new { id = versionId }),
            Text = text,
            Icon = TaskDialogIcon.Warning,
            Buttons = { use, keep },
            DefaultButton = keep,
            Verification = new TaskDialogVerificationCheckBox(L.T("confirm.ownFiles.dontAsk")),
            // Where a "Don't ask again" can be taken back.
            Footnote = new TaskDialogFootnote(L.T("confirm.ownFiles.dontAskSub")),
            AllowCancel = true,
        };
        // As with the other questions before a launch: it must not wait unseen under another window.
        page.Created += (_, _) => DialogFront.PromoteCurrentThreadDialogs();
        try
        {
            var clicked = owner is null ? TaskDialog.ShowDialog(page) : TaskDialog.ShowDialog(owner, page);
            var answer = new OwnFilesAnswer(clicked == use ? OwnFileChoices.Replace : OwnFileChoices.Keep,
                page.Verification?.Checked == true);
            Log.Info($"pre-launch question ({versionId}): the player's own files -> {answer.Choice}{(answer.Remember ? " (don't ask again)" : "")}");
            return answer;
        }
        catch (Exception ex)
        {
            // TaskDialog needs comctl32 v6 (visual styles); without it nothing is asked and the player's file stays.
            Log.Error("the question about the player's own game files could not be shown — the player's file stays", ex);
            return new OwnFilesAnswer(OwnFileChoices.Keep, false);
        }
    }

    /// <summary>
    /// true = go on to the server check. Asks only when this PC runs Windows 11 and the build's payload
    /// manifest says the version's client is not known to start there through Relic's launch path
    /// (<see cref="LaunchSupport"/>): a warning whose default answer is No. Yes goes on with the normal
    /// launch for this OS. Must be called on the UI thread of <paramref name="owner"/> — the dialog is
    /// modal to it. <paramref name="status"/> (the splash's line) is told that an answer is awaited, only
    /// when the question is asked.
    /// </summary>
    public static bool ConfirmWindows11(IWin32Window? owner, string versionId, Action<string>? status = null)
    {
        if (!LaunchSupport.AskOnWindows11(LaunchSupport.Windows11Unsupported(versionId), WindowsInfo.Detect().IsWindows11))
            return true;
        status?.Invoke(L.T("shell.splash.waitingConfirm"));
        string title = L.T("shell.preflight.windows11Title", new { version = versionId });
        Log.Info($"pre-launch question ({versionId}): {title}");
        using var front = DialogFront.WatchAndPromote();
        return MessageBox.Show(owner, L.T("shell.preflight.windows11Body", new { version = versionId }), title,
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    /// <summary>Mode-aware probe (admin: token /status; player: public status, then the SDK port) —
    /// the shortcut process has no Backend, so the state is loaded here.</summary>
    public static Task<ServerProbeResult> CheckAsync(string versionId, Action<string>? status = null)
        => ServerProbe.CheckAsync(Relic.Core.State.RelicState.Load(), versionId, status);

    /// <summary>
    /// true = start the game. Asks nothing when everything is fine. Must be called on the UI thread
    /// of <paramref name="owner"/> — the dialog is modal to it.
    /// </summary>
    public static bool Confirm(IWin32Window? owner, string versionId, ServerProbeResult r)
    {
        if (r.AllGood) return true;
        // The title follows the truth of each branch, as in the app: "unknown" must not become
        // "stopped" in the one line most people read. A version the server does not have comes first:
        // "stopped ... until the server is started" would send the player to wait for a start that
        // cannot happen (the wording stays neutral — the stack may be being extracted right now).
        string body, title;
        if (r.Hosted == false)
        {
            body = r.HostedVersions.Length > 0
                ? L.T("shell.preflight.absentBody", new { version = versionId, hosted = r.HostedVersions })
                : L.T("shell.preflight.absentBodyNone", new { version = versionId });
            title = L.T("shell.preflight.absentTitle", new { version = versionId });
        }
        else if (r.Unlisted)
        {
            // The agent answered and does not know this version: neither "stopped" (it could not start
            // it) nor "could not be checked" (it was) — the text says what the agent said.
            body = L.T("shell.preflight.unlistedBody", new { version = versionId });
            title = L.T("shell.preflight.unlistedTitle", new { version = versionId });
        }
        else if (r.Up == true && r.Degraded.Length == 0 && r.Loading)
        {
            // Every service runs and nobody gets in yet: the game server is still loading its data.
            body = L.T("shell.preflight.loadingBody");
            title = L.T("shell.preflight.loadingTitle");
        }
        else
        {
            // The public snapshot does not name the dead services: its marker is not a list to show.
            body = r.Up == true
                ? r.DegradedUnlisted
                    ? L.T("shell.preflight.degradedBodyNoList")
                    : L.T("shell.preflight.degradedBody", new { degraded = r.Degraded })
                : r.Up == false
                    ? L.T("shell.preflight.downBody", new { version = versionId })
                    : L.T("shell.preflight.unknownBody");
            title = r.Up == true ? L.T("shell.preflight.degradedTitle")
                : r.Up == false ? L.T("shell.preflight.downTitle") : L.T("shell.preflight.unknownTitle");
        }
        Log.Info($"pre-launch question ({versionId}): {title}");
        // As with the consent dialogs: the question must not wait unseen under something else.
        using var front = DialogFront.WatchAndPromote();
        return MessageBox.Show(owner, body, title, MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}
