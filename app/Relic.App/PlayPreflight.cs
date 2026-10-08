using Relic.Core.Server;
using Relic.Core.Util;

namespace Relic.App;

/// <summary>
/// The UI half of the check that precedes a launch from the desktop shortcut: the question put to
/// the user when the answer is not "all good". The actual agent probe (with retries) lives in
/// <see cref="ServerProbe"/>, in Core, where the spike can cover it; only the dialog stays here.
/// </summary>
internal static class PlayPreflight
{
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
