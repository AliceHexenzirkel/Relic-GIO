using Relic.Core.Install;
using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.App;

/// <summary>
/// The small "Starting the game" window shown while a desktop-shortcut launch ("Relic.exe --play")
/// prepares the session. Without it the shortcut shows NOTHING for the whole Fiddler +
/// profile-swap + launch stretch (tens of seconds, a minute+ on first run), which reads as "the
/// shortcut is broken". It streams the session's own log lines and closes the moment the game
/// client is actually sighted (or the session ends without one — error path included).
/// </summary>
public sealed class PlaySplashForm : Form
{
    private readonly Label _status;
    private volatile bool _closeRequested;

    /// <param name="enhancementsHint">Show the "press F1 in game" line: the in-game enhancements are
    /// on and this build ships them for the version (Backend.EnhancementsHintFor).</param>
    /// <param name="state">The launcher state, for the server's last word on the in-game account
    /// (created through Relic / none on this server / the catalogue fallback); loaded from disk when
    /// the caller has none at hand — a read, never a save, so it cannot clobber the live one.</param>
    public PlaySplashForm(string versionId, bool enhancementsHint = false, RelicState? state = null)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        // 96 = the base the coordinates below are designed at; with AutoScaleMode.Dpi WinForms then
        // scales ClientSize and the children's Bounds together with the fonts — otherwise the text
        // would grow on a 150% laptop while its boxes stayed in 96-DPI pixels (clipped text).
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        // A shortcut launch never goes through the launcher, so this window is the only place the
        // player can still learn which account to log in with — hence it shows the login data too.
        // The SERVER's word wins (an account created through Relic, or "none on this server" for a
        // stack prepared without the default save); the catalogue only fills in before the server ever
        // answered. An unknown or empty account adds nothing to the window, not even an empty row.
        // The password is never shown as an example value: "any" is the shipped default — this window
        // has no server status, so it cannot tell whether the admin turned verification on.
        string account;
        try { account = GameAccounts.ForVersion(versionId, state ?? RelicState.Load()).Account; }
        catch (Exception ex) { Log.Error("splash: reading the account (non-fatal)", ex); account = ""; }
        // One optional row each for the login hint and the enhancements hint, stacked in that order.
        int loginRowY = 186;
        int eeRowY = account.Length > 0 ? 212 : 186;
        int height = 200 + (account.Length > 0 ? 32 : 0) + (enhancementsHint ? 26 : 0);
        ClientSize = new Size(460, height);
        BackColor = Color.FromArgb(0x0f, 0x13, 0x20);
        // No TopMost: the session may raise the Windows certificate-approval dialog — DialogFront
        // lifts that into the topmost band itself, but a topmost splash would compete with it in
        // the same band and the launch could look stuck. The normal activation from OnShown is
        // enough for it to be seen.
        ShowInTaskbar = true;
        Text = L.T("shell.splash.windowTitle", new { version = versionId });
        try
        {
            if (Environment.ProcessPath is string exe && Icon.ExtractAssociatedIcon(exe) is Icon ico)
                Icon = ico;
        }
        catch (Exception ex) { Log.Error("splash icon (non-fatal)", ex); }

        var gold = Color.FromArgb(0xe6, 0xcf, 0x93);
        var goldDim = Color.FromArgb(0xc8, 0xa0, 0x4f);

        var title = new Label
        {
            Text = "R E L I C",
            Font = new Font("Segoe UI", 21f, FontStyle.Bold),
            ForeColor = gold,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(0, 26, 460, 42),
        };
        var subtitle = new Label
        {
            Text = L.T("shell.splash.subtitle", new { version = versionId }),
            Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(0xc7, 0xcd, 0xda),
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(0, 72, 460, 26),
        };
        _status = new Label
        {
            Text = L.T("shell.splash.preparing"),
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(0x8a, 0xa0, 0xc8),
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
            Bounds = new Rectangle(18, 108, 424, 34),
        };
        var bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 28,
            Bounds = new Rectangle(60, 156, 340, 8),
        };
        Controls.AddRange(new Control[] { title, subtitle, _status, bar });
        if (account.Length > 0)
        {
            Controls.Add(new Label
            {
                Text = L.T("shell.splash.loginHint", new { account }),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = goldDim,
                BackColor = Color.Transparent,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true,
                Bounds = new Rectangle(18, loginRowY, 424, 26),
            });
        }
        if (enhancementsHint)
        {
            Controls.Add(new Label
            {
                Text = L.T("shell.splash.eeHint"),
                Font = new Font("Segoe UI", 9f),
                ForeColor = goldDim,
                BackColor = Color.Transparent,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true,
                Bounds = new Rectangle(18, eeRowY, 424, 22),
            });
        }

        // Thin gold border — the borderless window would otherwise melt into any dark background.
        Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(120, goldDim));
            e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        };
    }

    /// <summary>Thread-safe status update — the play session logs from a worker thread.</summary>
    public void SetStatusSafe(string text)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => { if (!IsDisposed) _status.Text = text; });
        }
        catch (Exception) { /* the window is closing right now — the message no longer matters */ }
    }

    /// <summary>Thread-safe close, callable before the handle exists (the session task starts only
    /// on Shown, but a belt-and-braces flag keeps a lost race from stranding the splash open).</summary>
    public void CloseSafe()
    {
        _closeRequested = true;
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => { if (!IsDisposed) Close(); });
        }
        catch (Exception) { /* already closed */ }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_closeRequested) { Close(); return; }
        Activate(); // in front at start, without TopMost
    }
}
