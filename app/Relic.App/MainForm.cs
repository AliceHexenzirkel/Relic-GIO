using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Relic.Core.Play;
using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.App;

/// <summary>
/// Borderless native host. The whole UI (including the title bar) is HTML in the WebView2, served from
/// the local ui/ folder via a virtual host. Closing the window hides Relic to the tray so it keeps
/// running in the background; the tray menu really quits it and toggles run-at-startup.
/// </summary>
public sealed class MainForm : Form
{
    // The HTML UI is a fixed canvas of 1188×752 CSS pixels (ui/relic.css #app, overflow:hidden).
    // The native window must always give it exactly that many CSS pixels — see ApplyScale.
    private const int BaseWidth = 1188;
    private const int BaseHeight = 752;

    private readonly WebView2 _web = new();
    private readonly bool _startHidden;
    /// <summary>The version requested by the desktop shortcut ("Relic.exe --play &lt;version&gt;")
    /// when the launch runs in this process. null = a normal launcher start.</summary>
    private readonly string? _shortcutPlay;
    private Backend? _backend;
    private NotifyIcon? _tray;
    private bool _reallyExit;
    private bool _trayHintShown;
    /// <summary>Someone already asked for the window (a second launch signalled it, or the shortcut
    /// launch was cancelled). A background start must not hide it again when it finishes loading —
    /// that hide comes at the end of Load, i.e. long after we showed it.</summary>
    private bool _shownOnRequest;
    private double _uiScale = 1.0;      // physical px per CSS px for the current monitor
    private double _lastZoomEstimate;   // last dpi-based ZoomFactor estimate actually applied
    private int _zoomFixGeneration;
    private bool _wasMinimized;
    /// <summary>True once ApplyScale has actually run for real (window not minimized). A background
    /// start creates the window minimized, so the first real sizing — and the centring — happen only
    /// when it is brought back from the tray.</summary>
    private bool _scaledOnce;
    /// <summary>True while the window lives in the tray (X, Alt+F4, a background start). The web UI
    /// reads it at boot (app.init) and follows the "window.visibility" event afterwards, so the
    /// start-screen music/video pause while nobody can see the window — otherwise the launcher keeps
    /// playing its theme from the tray.</summary>
    public bool IsHiddenToTray { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Process-lifetime: shared by the window and the tray, so neither can dispose it out from under
    // the other. Loaded once — the file sits next to the exe and never changes while we run.
    private static readonly Icon AppIcon = LoadAppIcon();

    public MainForm(bool startHidden = false, string? shortcutPlayVersion = null)
    {
        _startHidden = startHidden;
        _shortcutPlay = shortcutPlayVersion;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        // Placeholder pre-Load size; ApplyScale recomputes it for the monitor's DPI on Load.
        ClientSize = new Size(BaseWidth, BaseHeight);
        BackColor = Color.FromArgb(0x0f, 0x13, 0x20);
        Text = "Relic";
        // <ApplicationIcon> only stamps the exe (Explorer, file dialogs); the taskbar button and the
        // alt-tab entry come from Form.Icon. WinForms re-applies it whenever the handle is recreated,
        // so it survives HideToTray's ShowInTaskbar toggle.
        Icon = AppIcon;
        // Background start (autostart "--tray" or a game launch from the shortcut): the window is
        // CREATED minimized so nothing shows on screen while WebView2 initialises — several seconds
        // on first start, right in front of the player who just clicked the shortcut. HideToTray, at
        // the end of Load, then hides it completely; on its own it is not enough, and a Hide() moved
        // into the synchronous part of Load does not hold (Application.Run shows the window
        // a moment later anyway, and a Hide() posted via BeginInvoke still leaves a flicker). Both
        // properties are set before the handle exists, so they cost no handle recreation.
        ShowInTaskbar = !startHidden;
        if (startHidden) WindowState = FormWindowState.Minimized;
        IsHiddenToTray = startHidden;

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);
        Load += OnLoadAsync;
        FormClosing += OnFormClosing;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>FormBorderStyle.None strips WS_MINIMIZEBOX, and without that style Windows ignores
    /// the click on the app's taskbar button (it only activates, never minimizes/restores). Add the
    /// style back at the Win32 level — the window stays visually borderless because WS_CAPTION is
    /// still absent, but the taskbar button behaves like on a normal window again.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000; // WS_MINIMIZEBOX
            return cp;
        }
    }

    // ── window controls invoked from the web title bar ──
    public void MinimizeWindow() => BeginInvoke(() => WindowState = FormWindowState.Minimized);
    public void CloseWindow() => BeginInvoke(HideToTray);          // X → background, not exit
    public void DragWindow() => Interop.DragWindow(Handle);

    // ── DPI / display-scale handling ──
    // The page is a fixed 1188×752 CSS-px layout. WebView2 multiplies its rendering by the OS
    // display scale, so a 1188-physical-px window on a 125% laptop gives the page only ~950 CSS px
    // and the bottom + the window buttons get clipped. Fix: scale the physical window by the DPI
    // factor (shrinking to fit small work areas, e.g. 1366×768), and set the WebView zoom so the
    // page always measures exactly 1188×752 CSS px.

    private void ApplyScale(bool recenter)
    {
        if (WindowState == FormWindowState.Minimized) return;   // OnResize re-applies on restore
        var work = Screen.FromHandle(Handle).WorkingArea;
        double dpi = DeviceDpi / 96.0;
        double s = Math.Min(dpi, Math.Min(work.Width / (double)BaseWidth, work.Height / (double)BaseHeight));
        var size = new Size((int)Math.Round(BaseWidth * s), (int)Math.Round(BaseHeight * s));
        _uiScale = s;
        _scaledOnce = true;
        MinimumSize = size;      // before ClientSize, so shrinking is not clamped
        ClientSize = size;       // borderless → window size == client size
        if (recenter)
            Location = new Point(work.Left + (work.Width - size.Width) / 2,
                                 work.Top + (work.Height - size.Height) / 2);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_web.CoreWebView2 is null) return;   // first call comes before init; OnLoadAsync re-applies
        double estimate = _uiScale / (DeviceDpi / 96.0);
        // Hard-reset to the dpi-based estimate only when it actually changed (real DPI/size change).
        // Resetting on every trigger (drag end, restore, system events) would discard FineTune's
        // measured correction — a visible zoom snap on machines with Windows text scaling > 100%.
        if (Math.Abs(estimate - _lastZoomEstimate) > 0.001)
        {
            _lastZoomEstimate = estimate;
            try { _web.ZoomFactor = estimate; }
            catch (Exception ex) { Log.Error("ApplyZoom (non-fatal)", ex); }
        }
        _ = FineTuneZoomAsync();
    }

    /// <summary>If the window ended up entirely outside every screen (e.g. a bad restore
    /// position), center it back on the nearest monitor's work area.</summary>
    private void EnsureOnScreen()
    {
        var work = Screen.FromHandle(Handle).WorkingArea;   // nearest monitor for off-screen windows
        if (Bounds.IntersectsWith(work)) return;
        Location = new Point(work.Left + (work.Width - Width) / 2,
                             work.Top + (work.Height - Height) / 2);
    }

    /// <summary>
    /// Measured correction: WebView2's rasterization scale can include Windows text scaling on top
    /// of the monitor DPI, so the dpi-based estimate can be off. Nudge ZoomFactor until the page
    /// really measures 1188 CSS px wide.
    /// </summary>
    private async Task FineTuneZoomAsync()
    {
        int gen = ++_zoomFixGeneration;
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var core = _web.CoreWebView2;
                if (core is null || gen != _zoomFixGeneration) return;
                if (WindowState != FormWindowState.Normal) return;  // minimized viewport is ~160px — would corrupt the zoom
                string raw = await core.ExecuteScriptAsync("window.innerWidth");
                if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double innerW) || innerW <= 0)
                    return;
                if (Math.Abs(innerW - BaseWidth) <= 1 || gen != _zoomFixGeneration) return;
                _web.ZoomFactor *= innerW / BaseWidth;
                await Task.Delay(100);
            }
        }
        catch (Exception ex) { Log.Error("FineTuneZoom (non-fatal)", ex); }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Cancel must be set BEFORE the base call — Form.OnDpiChanged checks it internally and
        // would otherwise apply its own SuggestedRectangle rescale first (double-resize flicker).
        e.Cancel = true;
        base.OnDpiChanged(e);
        // While minimized the suggested rect describes the iconic "parking" area (~-32000,-32000);
        // setting Location would be recorded as the restore position and the window would come back
        // off-screen. Skip — OnResize re-derives everything on restore.
        if (WindowState == FormWindowState.Minimized) return;
        // Base no longer moves the window, so follow the OS-suggested position ourselves (it keeps
        // the window under the cursor mid-drag), then size for our fixed canvas.
        Location = e.SuggestedRectangle.Location;
        ApplyScale(recenter: false);
    }

    // Fires when a drag between same-DPI monitors ends (no WM_DPICHANGED then): re-fit to the new
    // monitor's work area, e.g. growing back to full size after leaving a 1366×768 screen.
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        const int WM_EXITSIZEMOVE = 0x0232;
        if (m.Msg == WM_EXITSIZEMOVE) ApplyScale(recenter: false);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        bool minimized = WindowState == FormWindowState.Minimized;
        // DPI/display changes that arrive while minimized are skipped (degenerate 160-px viewport
        // would corrupt the zoom) — re-derive everything on restore instead.
        if (!minimized && _wasMinimized)
        {
            ApplyScale(recenter: false);
            EnsureOnScreen();
        }
        _wasMinimized = minimized;
    }

    // Resolution / text-size / work-area changes fire no WM_DPICHANGED; re-fit on system events.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => ReapplyScale();
    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) => ReapplyScale();

    private void ReapplyScale()
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => ApplyScale(recenter: false)); }
        catch { /* window torn down mid-event */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        base.Dispose(disposing);
    }

    /// <summary>Show a native folder picker (install location, local game folder). Returns null if cancelled.</summary>
    public string? PickFolder(string? initial, string? title = null)
    {
        string? result = null;
        void Show()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = string.IsNullOrWhiteSpace(title) ? L.T("shell.pickFolder.installLocation") : title,
                UseDescriptionForTitle = true,
            };
            if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dlg.SelectedPath = initial;
            if (dlg.ShowDialog(this) == DialogResult.OK) result = dlg.SelectedPath;
        }
        if (InvokeRequired) Invoke(Show); else Show();
        return result;
    }

    /// <summary>Show a native file picker (e.g. an SSH private key). Returns null if cancelled.</summary>
    public string? PickFile(string? title = null, string? filter = null, string? initialDir = null)
    {
        string? result = null;
        void Show()
        {
            using var dlg = new OpenFileDialog
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Relic" : title,
                Filter = string.IsNullOrWhiteSpace(filter) ? "All files|*.*" : filter,
                CheckFileExists = true,
                Multiselect = false,
            };
            if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir)) dlg.InitialDirectory = initialDir;
            if (dlg.ShowDialog(this) == DialogResult.OK) result = dlg.FileName;
        }
        if (InvokeRequired) Invoke(Show); else Show();
        return result;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_reallyExit) return;                       // tray "Exit completely"
        if (e.CloseReason == CloseReason.WindowsShutDown)
        {
            _tray?.Dispose();                          // let Windows take us down
            return;
        }
        // Any other close (web X, Alt+F4, taskbar/Task Manager close) → keep running in the tray.
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        IsHiddenToTray = true;
        PostJson(new { @event = "window.visibility", data = new { hidden = true } });
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowBalloonTip(3000, "Relic", L.T("shell.tray.hint"), ToolTipIcon.Info);
        }
    }

    private void ShowFromTray()
    {
        _shownOnRequest = true;
        // Read BEFORE restoring: leaving the minimized state goes through OnResize, which gets to
        // call ApplyScale first (without recentring) and would clear the flag under us.
        bool firstScale = !_scaledOnce;
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        IsHiddenToTray = false;
        PostJson(new { @event = "window.visibility", data = new { hidden = false } });
        // Started in the background, the window has lived minimized until now, and ApplyScale bails
        // out early while minimized — rightly so: a page loaded into a minimized window reports
        // a wrong `window.innerWidth`. The first true sizing for the current monitor
        // (and the only centring) therefore happens only here. Later opens do not recentre: the
        // window stays where the user left it.
        if (firstScale) ApplyScale(recenter: true);
        Activate();
        BringToFront();
    }

    /// <summary>Thread-safe "bring the window back", used when a second Relic launch signals this
    /// (single) instance instead of starting its own.</summary>
    public void ShowFromTrayCrossThread()
    {
        try
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)ShowFromTray);
        }
        catch (Exception ex) { Log.Error("ShowFromTrayCrossThread (non-fatal)", ex); }
    }

    /// <summary>Set the moment the user confirms: the exit pumps messages while the recovery pass
    /// runs, so a queued click or rpc could otherwise re-enter this and start a second pass.</summary>
    private bool _exiting;

    private void ReallyExit()
    {
        if (_exiting) return;
        // BEFORE the confirmation, not after: that call shows a dialog and then pumps messages while
        // the recovery pass runs, and both are re-entry points for a queued click.
        _exiting = true;
        if (!ConfirmExitDuringSession()) { _exiting = false; return; }
        _reallyExit = true;
        _tray?.Dispose();
        Close();
        Application.Exit();
    }

    /// <summary>
    /// The guard on the tray "Exit completely": the session's restore (settle window → stop Fiddler
    /// → put the live profile back) runs INSIDE this process, and so does the recovery loop that
    /// finishes a deferred restore. Exiting mid-session, or while a deferred restore is pending,
    /// would leave the private profile loaded and Fiddler as the system proxy until the next Relic
    /// start — an official client started meanwhile would run on the private profile and be redirected to
    /// the private server. So while a session runs here, or the live profile is not back in place,
    /// ask first; "No" (the default) keeps Relic in the tray. On "Yes" with no session in this process
    /// one synchronous recovery pass runs before the exit (it defers by itself if a client is running
    /// — the next start then finishes the job). The dialog is promoted like the consent
    /// dialogs: the window is usually hidden in the tray, and the game may be full screen.
    /// </summary>
    private bool ConfirmExitDuringSession()
    {
        bool playing = _backend?.PlayInProgress == true;
        bool liveActive = true;
        try { liveActive = Relic.Core.Isolation.ProfileStore.ForGenshin().LiveIsActive(); }
        catch (Exception ex) { Log.Error("exit guard: reading the profile state (non-fatal)", ex); }
        if (!playing && liveActive) return true;

        DialogResult answer;
        using (DialogFront.WatchAndPromote())
        {
            answer = MessageBox.Show(
                L.T("shell.tray.exitWhilePlaying.body"), L.T("shell.tray.exitWhilePlaying.title"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        }
        if (answer != DialogResult.Yes)
        {
            Log.Info($"exit completely declined (playInProgress={playing} liveActive={liveActive}) — staying in the tray");
            return false;
        }
        Log.Info($"exit completely confirmed by the user (playInProgress={playing} liveActive={liveActive})");
        if (!playing)
        {
            // The pass itself runs on the POOL and the UI thread keeps pumping while it does. It
            // waits out a 5-15 s settle window (and up to 20 s for a pass already in flight), and
            // running that on the UI thread would leave a process with no tray icon, no window and no
            // repaints — Windows paints it "not responding" and the user sees a launcher that hung
            // on exit. The tray still goes first so a second click cannot start a second pass.
            _tray?.Dispose();
            Cursor.Current = Cursors.WaitCursor;
            var pass = Task.Run(() =>
            {
                try { _backend?.RecoverBeforeExit(); }
                catch (Exception ex) { Log.Error("exit: recovery pass failed (non-fatal)", ex); }
            });
            // Bounded: a recovery that will not finish must never keep the process alive. What it
            // did not manage is done at the next start (or by the RunOnce logon hook).
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!pass.Wait(50) && DateTime.UtcNow < deadline) Application.DoEvents();
            if (!pass.IsCompleted) Log.Info("exit: the recovery pass did not finish in 45 s — leaving it to the next start");
            Cursor.Current = Cursors.Default;
        }
        return true;
    }

    private void SetupTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(L.T("shell.tray.open"), null, (_, _) => ShowFromTray());

        var startupItem = new ToolStripMenuItem(L.T("shell.tray.startWithWindows")) { CheckOnClick = true };
        try { startupItem.Checked = Startup.IsEnabled(); } catch { }
        startupItem.Click += (_, _) =>
        {
            try { Startup.Set(startupItem.Checked, Environment.ProcessPath ?? ""); }
            catch (Exception ex) { Log.Error("Startup toggle", ex); startupItem.Checked = !startupItem.Checked; }
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("shell.tray.exit"), null, (_, _) => ReallyExit());

        // The shell draws NotifyIcon at the small-icon size (16 px at 100%, 20 at 125%); this ctor
        // picks that frame out of the multi-resolution .ico instead of squashing the 32 px one,
        // which is the whole reason the small frames are drawn by hand in build/make_icon.py.
        Icon trayIcon = AppIcon;
        try { trayIcon = new Icon(AppIcon, SystemInformation.SmallIconSize); }
        catch (Exception ex) { Log.Error("tray icon resize (non-fatal)", ex); }

        _tray = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "Relic",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    /// <summary>The brand icon, read from the exe's own resource first, then the loose file, then
    /// the drawn fallback.</summary>
    private static Icon LoadAppIcon()
    {
        // The exe ALWAYS carries the icon (<ApplicationIcon> embeds it as the Win32 resource), while
        // the loose assets\relic.ico does not survive a single-file publish — so the embedded copy is
        // the only source that works in both a dev build and a ship build.
        try
        {
            if (Environment.ProcessPath is string exe && Icon.ExtractAssociatedIcon(exe) is Icon ico)
                return ico;
        }
        catch (Exception ex) { Log.Error("LoadAppIcon from exe (non-fatal)", ex); }
        try
        {
            // Fully qualified: WinForms still exposes a Shortcut enum, so importing the namespace
            // would make the bare name ambiguous.
            if (Relic.Core.Install.Shortcut.AppIconPath is string path) return new Icon(path);
        }
        catch (Exception ex) { Log.Error("LoadAppIcon from file (non-fatal)", ex); }
        return MakeIcon();
    }

    /// <summary>Fallback gold diamond drawn in code — only reached if the .ico did not ship.</summary>
    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var pts = new[] { new Point(16, 2), new Point(30, 16), new Point(16, 30), new Point(2, 16) };
            using var fill = new SolidBrush(Color.FromArgb(0xc8, 0xa0, 0x4f));
            using var pen = new Pen(Color.FromArgb(0xe6, 0xcf, 0x93), 2);
            g.FillPolygon(fill, pts);
            g.DrawPolygon(pen, pts);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// The desktop-shortcut launch, run inside the launcher: the main window stays hidden in the
    /// tray — hence the notification-area icon for the whole session, and hence something left to
    /// open once the game has closed. The only visible surface is the "Starting the game" splash,
    /// shown non-modally: the message loop belongs to the main window, and a second Application.Run
    /// on the same thread is not possible.
    /// </summary>
    private async void StartShortcutPlay(string versionId)
    {
        var splash = new PlaySplashForm(versionId, _backend!.EnhancementsHintFor(versionId));
        splash.Show();
        try
        {
            // The server check runs under the splash, not before it: that way it may retry (see
            // PlayPreflight) without the shortcut looking dead, and on the happy path the user sees
            // no extra dialog. The continuation returns to the UI thread, so Confirm may raise the
            // modal dialog over the splash from here.
            var pre = await PlayPreflight.CheckAsync(versionId, splash.SetStatusSafe);
            if (splash.IsDisposed) return;
            if (!pre.AllGood)
            {
                splash.SetStatusSafe(L.T("shell.splash.waitingConfirm"));
                if (!PlayPreflight.Confirm(splash, versionId, pre))
                {
                    // The game does not start. We do not leave behind just a tray icon: the user
                    // said "no" because something is wrong with the server, and the window is
                    // exactly the place they can start it from.
                    Log.Info($"shortcut launch cancelled by user ({versionId})");
                    splash.Close();
                    ShowFromTray();
                    return;
                }
            }
            splash.SetStatusSafe(L.T("shell.splash.preparing"));
            _backend!.StartPlayFromShortcut(versionId,
                log: splash.SetStatusSafe,
                appeared: splash.CloseSafe,   // the client appeared — from here on they watch the game
                finished: ex =>
                {
                    splash.CloseSafe();       // error, or a game that never appeared at all
                    if (ex is not null) ShowShortcutPlayError(ex);
                });
        }
        catch (Exception ex)
        {
            // async void: an exception escaping here would go straight to the global crash handler.
            Log.Error("shortcut launch", ex);
            splash.CloseSafe();
            ShowShortcutPlayError(ex);
        }
    }

    /// <summary>The error of a shortcut launch, shown as a dialog: the launcher window is hidden in
    /// the tray, so a toast in the web UI would be seen by nobody. Promoted into the topmost band for
    /// the same reason as the consent dialogs — the game may already be full screen, and a
    /// MessageBox left underneath would look like a launch that froze.</summary>
    private void ShowShortcutPlayError(Exception ex)
    {
        void Show()
        {
            // Fiddler closed by the user is not a launch error — it gets its own title, so the user
            // understands at once that Relic closed the game on purpose, not that it crashed.
            using var front = DialogFront.WatchAndPromote();
            MessageBox.Show(ex.Message,
                ex is FiddlerClosedException ? L.T("shell.play.closedTitle") : "Relic",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        try
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Show);
            else Show();
        }
        catch (Exception e) { Log.Error("shortcut launch error dialog", e); }
    }

    /// <summary>Serialize and post a message to the web layer (thread-safe, never throws).</summary>
    public void PostJson(object payload)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;

        // Serialization is thread-safe and can run on any thread.
        string json;
        try { json = JsonSerializer.Serialize(payload, JsonOpts); }
        catch (Exception ex) { Log.Error("PostJson serialize", ex); return; }

        // CoreWebView2 may ONLY be touched on the UI thread, so read it INSIDE the marshaled call,
        // never before. (IsHandleCreated / InvokeRequired are safe to read from any thread.)
        void Post()
        {
            try
            {
                var core = _web.CoreWebView2;
                core?.PostWebMessageAsJson(json);
            }
            catch (Exception ex) { Log.Error("PostWebMessageAsJson", ex); }
        }
        try
        {
            if (InvokeRequired) BeginInvoke((Action)Post);
            else Post();
        }
        catch (Exception ex) { Log.Error("PostJson invoke", ex); }
    }

    private async void OnLoadAsync(object? sender, EventArgs e)
    {
        try
        {
            SetupTray();
            ApplyScale(recenter: true);          // size the window for this monitor's DPI

            // The Backend is built BEFORE WebView2, not after: the desktop-shortcut launch runs
            // in this process, and a WebView2 that fails to start must not mean the game no longer
            // starts at all. Backend needs no browser — PostJson is a no-op while CoreWebView2 is null.
            _backend = new Backend(this);
            if (_shortcutPlay is string shortcutVersion) StartShortcutPlay(shortcutVersion);

            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relic", "WebView2");
            Directory.CreateDirectory(userData);

            // Autoplay: the start screen plays its music without a click (the mute pill remembers the
            // user's choice); a muted+loop video autoplays regardless, this flag is for the audio.
            var envOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, userData, envOptions);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;     // docked F12 would skew the zoom self-measurement
#endif
            core.Settings.IsZoomControlEnabled = false;   // Ctrl+wheel zoom would clip the fixed layout

            ApplyZoom();                                  // CoreWebView2 exists now
            core.NavigationCompleted += (_, _) => _ = FineTuneZoomAsync();

            core.WebMessageReceived += (_, ev) =>
                _backend!.OnMessage(ev.TryGetWebMessageAsString() ?? ev.WebMessageAsJson);

            core.ProcessFailed += (_, ev) =>
            {
                Log.Error($"WebView2 ProcessFailed: {ev.ProcessFailedKind} reason={ev.Reason}");
                try { core.Reload(); } catch { /* ignore */ }
            };

            core.SetVirtualHostNameToFolderMapping(
                "relic.app", Path.Combine(AppContext.BaseDirectory, "ui"),
                CoreWebView2HostResourceAccessKind.Allow);

            string? devScreen = Environment.GetEnvironmentVariable("RELIC_DEV_SCREEN");
            string url = "https://relic.app/index.html" + (string.IsNullOrEmpty(devScreen) ? "" : "#" + devScreen);
            core.Navigate(url);

            // Dev aid: RELIC_DEV_AUTOINSTALL=1.6 auto-starts that install a few seconds after load
            // (used to test the install path without clicking through the wizard).
            string? devInstall = Environment.GetEnvironmentVariable("RELIC_DEV_AUTOINSTALL");
            if (!string.IsNullOrEmpty(devInstall))
            {
                var t = new System.Windows.Forms.Timer { Interval = 3000 };
                t.Tick += (_, _) =>
                {
                    t.Stop(); t.Dispose();
                    PostJson(new { @event = "dev.autoinstall", data = new { versionId = devInstall } });
                };
                t.Start();
            }

            // …but not over a window requested in the meantime: WebView2 takes seconds to load, and
            // in that span a second launch (or a cancelled shortcut launch) may already have brought
            // it to the front — hiding it here would blow it back into the tray under the user's hand.
            if (_startHidden && !_shownOnRequest) HideToTray();
        }
        catch (Exception ex)
        {
            Log.Error("MainForm load", ex);
            // With a play session started from the shortcut, a modal over the game that is just
            // launching would do more harm than good: the session does not depend on WebView2 (which
            // is why the Backend is built before it) and carries on. Say it in the tray, not up front.
            if (_shortcutPlay is not null)
            {
                _tray?.ShowBalloonTip(6000, "Relic",
                    L.T("shell.ui.failedButPlaying"),
                    ToolTipIcon.Warning);
                return;
            }
            MessageBox.Show(this, ex.ToString(), L.T("shell.ui.startupErrorTitle"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
