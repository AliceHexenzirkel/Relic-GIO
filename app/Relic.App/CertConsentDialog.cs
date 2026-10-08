using Relic.Core.Util;

namespace Relic.App;

/// <summary>
/// The notice shown immediately before Windows' own "Security Warning" for the Fiddler root
/// certificate. The Windows dialog is in the system language, talks about a certificate called
/// DO_NOT_TRUST_FiddlerRoot and warns that trusting it is dangerous — everything about it invites a
/// scared "No", after which nothing works and the user has no idea why. This says, in plain words
/// and before the fact, exactly which window is coming and which button to press.
///
/// It is NOT a way around the consent: the answer is still the user's, and it is still Windows that
/// records it. See <see cref="Relic.Core.Fiddler.FiddlerAutomation.BeforeCertConsent"/> for why the
/// prompt is kept rather than suppressed.
/// </summary>
internal static class CertConsentDialog
{
    private static string Heading => L.T("shell.cert.heading");

    private static string Body => L.T("shell.cert.body");

    /// <summary>
    /// Show it and return only after the user has read it. Runs on its own STA thread so it works
    /// identically from every caller — the install task, a play session, the Settings button, and the
    /// separate "Relic.exe --play" process, which has no message loop at all.
    /// </summary>
    public static void Show()
    {
        var t = new Thread(ShowCore) { Name = "Relic.CertConsentNotice", IsBackground = false };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    /// <summary>Working area of the screen the launcher is on, so the notice lands on the same
    /// display the user is looking at. The "--play" shortcut runs in its own process with no window
    /// of its own, and there the primary screen is the only sensible answer.</summary>
    private static Rectangle OwnerScreenArea()
    {
        try
        {
            IntPtr h = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (h != IntPtr.Zero) return Screen.FromHandle(h).WorkingArea;
        }
        catch (Exception ex) { Log.Error("choosing the screen for the certificate notice (non-fatal)", ex); }
        return (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
    }

    private static void ShowCore()
    {
        // An invisible TopMost owner. It does two jobs: both TaskDialog and MessageBox centre
        // themselves on their owner (positioning), and on current Windows the owned dialog comes up
        // with the owner's WS_EX_TOPMOST already set (Win11 26200). That inheritance is
        // undocumented behaviour though, and at install time this notice can end up hiding
        // behind the launcher anyway — so the dialog ALSO promotes itself into the topmost band the
        // moment it exists (page.Created → DialogFront) instead of betting on the owner alone.
        //
        // It is parked at the CENTRE of the screen, not off-desktop. Both TaskDialog and MessageBox
        // centre themselves on their owner, so an owner at (-32000,-32000) gets the notice clamped
        // into the top-left corner of the display while the Windows warning it explains opens in the
        // middle — the two read as unrelated windows, which defeats the whole point.
        var area = OwnerScreenArea();
        using var owner = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2),
            Size = new Size(1, 1),
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            TopMost = true,
        };

        try
        {
            owner.Show();

            var page = new TaskDialogPage
            {
                Caption = "Relic",
                Heading = Heading,
                Text = Body,
                Icon = TaskDialogIcon.ShieldBlueBar,
                Buttons = { new TaskDialogButton(L.T("shell.cert.button")) },
                AllowCancel = false,
            };
            // The instant the native dialog exists, lift it into the topmost band — the watcher in
            // TrustRootCertConsented would catch it too, but only on its next poll, and this event
            // runs on the dialog's own thread so there is no gap at all.
            page.Created += (_, _) => DialogFront.PromoteCurrentThreadDialogs();
            TaskDialog.ShowDialog(owner, page);
        }
        catch (Exception ex)
        {
            // TaskDialog needs comctl32 v6 (visual styles). If anything about that is off, the plain
            // MessageBox still gets the message across — this must never be the thing that fails.
            Log.Error("TaskDialog for the certificate notice failed — falling back to MessageBox", ex);
            try
            {
                MessageBox.Show(owner, Heading + "\r\n\r\n" + Body, "Relic",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex2) { Log.Error("MessageBox for the certificate notice (non-fatal)", ex2); }
        }
        finally
        {
            try { owner.Close(); } catch { /* going down anyway */ }
        }
    }
}
