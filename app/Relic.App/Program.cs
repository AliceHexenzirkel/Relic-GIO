using System.Runtime.InteropServices;
using Relic.Core.Fiddler;
using Relic.Core.Install;
using Relic.Core.Play;
using Relic.Core.Server;
using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.App;

internal static class Program
{
    /// <summary>Set by the play process of a desktop shortcut started while the launcher runs, once it stored
    /// a version's answer about the player's own game files in the state file: the launcher reads it in
    /// (<see cref="MainForm.OwnFilesChangedCrossThread"/>) instead of saving its older copy over it.</summary>
    private const string OwnFilesSignalName = @"Local\Relic.OwnFilesChanged";

    [STAThread]
    private static void Main(string[] args)
    {
        // Log everything, and turn any unhandled crash into a logged message + a dialog
        // (so a crash at start becomes a readable error with a log path).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fatal("ThreadException (UI)", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("UnobservedTaskException", e.Exception); e.SetObserved(); };

        // User-facing strings before anything that could show one (Fatal included): the saved
        // language is read from state.json; anything wrong there means English.
        InitLanguage();

        Log.Info($"Relic starting. args=[{string.Join(' ', args)}] cwd={Environment.CurrentDirectory}");
        // Before any window — otherwise the shell has already given us the identity of the shortcut
        // that launched us, and the game shortcut carries the Genshin icon.
        Interop.DeclareAppIdentity();
        ApplicationConfiguration.Initialize();

        // Explain the Windows root-certificate consent dialog before it appears, whichever path
        // triggers it (install, play, the Settings button, or the --play shortcut process below).
        // Wired here rather than in Core so Relic.Core keeps no UI dependency.
        FiddlerAutomation.BeforeCertConsent = CertConsentDialog.Show;

        // Desktop shortcut launches "Relic.exe --play <version>": run the orchestrated play session.
        // The server check is NOT done here but under the splash, on both paths below — see
        // PlayPreflight: placed before any window it would not be allowed to take long, and a single
        // short attempt on a machine that has just booted misses a server that is working fine.
        string? playVersion = args.Length >= 2 && args[0] == "--play" ? args[1] : null;

        // Called by the Inno UNINSTALLER, before it removes {app}. Also ahead of the single-instance
        // gate: the tray instance normally holds that mutex, and the cleanup's first job is to kill it.
        if (args.Length >= 2 && args[0] == "--uninstall-probe")
        {
            RunUninstallProbe(args[1]);
            return;
        }
        if (args.Length >= 1 && args[0] == "--uninstall-cleanup")
        {
            RunUninstallCleanup(args);
            return;
        }

        // Single instance: with autostart a hidden --tray instance is always running, and a second
        // Backend would clobber state.json with its own stale RelicState. A second launch just asks
        // the existing instance to show its window, then exits.
        using var instanceMutex = new Mutex(true, @"Local\Relic.Launcher", out bool createdNew);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Relic.ShowWindow");
        using var ownFilesSignal = new EventWaitHandle(false, EventResetMode.AutoReset, OwnFilesSignalName);
        if (!createdNew)
        {
            // The launcher is already running (tray or open window): the play session goes into this
            // process — a second Backend would overwrite state.json with its own stale state.
            if (playVersion is not null) { RunPlay(playVersion, ownFilesSignal, showSignal); return; }
            // A background start (the autostart Run entry, or the RunOnce logon-recovery hook that
            // Startup arms while a private profile is loaded — both may fire at the same logon) has
            // nothing to add to a running instance: its recovery loop is already armed. Signalling
            // would pop the window in the user's face right after logon.
            if (args.Contains("--tray"))
            {
                Log.Info("Relic is already running — a background (--tray) start has nothing to do, exiting");
                return;
            }
            Log.Info("Relic is already running — signalling the existing instance to show its window, then exiting");
            showSignal.Set();
            return;
        }

        try
        {
            // A shortcut launch with the launcher closed STARTS it — in the background, minimized to
            // the tray. A session process on its own would have no icon at all: the session would do its
            // job (restore the profile when the game closes, close the game if Fiddler closes), but
            // none of that would be visible anywhere and there would be nothing to open.
            bool startHidden = args.Contains("--tray") || playVersion is not null;
            var form = new MainForm(startHidden, playVersion);
            var showWatcher = new Thread(() =>
            {
                while (showSignal.WaitOne())
                    form.ShowFromTrayCrossThread();
            }) { IsBackground = true, Name = "Relic.ShowWindowWatcher" };
            showWatcher.Start();
            var ownFilesWatcher = new Thread(() =>
            {
                while (ownFilesSignal.WaitOne())
                    form.OwnFilesChangedCrossThread();
            }) { IsBackground = true, Name = "Relic.OwnFilesWatcher" };
            ownFilesWatcher.Start();
            Application.Run(form);
        }
        catch (Exception ex)
        {
            Fatal("Application.Run", ex);
        }
    }

    /// <summary>Select the UI language from the saved settings. Never throws: a missing or corrupt
    /// state.json simply means English, which <see cref="L"/> falls back to anyway.</summary>
    private static void InitLanguage()
    {
        string code = "en";
        try { code = RelicState.Load().Settings.Language; }
        catch (Exception ex) { Log.Error("language init (non-fatal)", ex); }
        L.Init(code);
    }

    /// <summary>
    /// The play session run STANDALONE, in its own process: the path for a shortcut pressed while the
    /// launcher is already running. When the launcher is NOT running, the shortcut starts it in the
    /// background instead and the session goes through <c>Backend.StartPlayFromShortcut</c> — see Main.
    /// <paramref name="ownFilesSignal"/> tells that launcher when this process stored an answer about the
    /// player's own game files (<see cref="AskOwnFilesOnSplash"/>); <paramref name="showSignal"/> brings up
    /// its window when a balloon of this process is clicked (<see cref="PlayTray"/>).
    /// </summary>
    private static void RunPlay(string versionId, EventWaitHandle ownFilesSignal, EventWaitHandle showSignal)
    {
        var state = RelicState.Load();
        L.Init(state.Settings.Language);
        var session = new PlaySession(state) { StateReadAtStart = true };
        session.Log += m => Log.Info($"[play] {m}");

        // "Starting the game" splash: without it the desktop shortcut would show NOTHING while
        // Fiddler + the profile are being prepared (tens of seconds) — it would simply look broken. The
        // session starts only at Shown, so no event can arrive before the window; the splash closes
        // when the client has actually been seen running, and the session continues in the background
        // (waits for the game to close and restores the profile). The message loop is this process's own
        // tray (PlayTray), started with the splash as its first window and running on once the splash has
        // closed: this process has no window then, and what the session end says — the game has closed,
        // then what the session end did — reaches the player as balloons of its own tray icon. The loop
        // ends once the session's work is done, and the process with it.
        Exception? error = null;
        Task? work = null;
        // Same hint rule as Backend.EnhancementsHintFor — this path has no Backend (the launcher is
        // already running in another process), only the state it just loaded.
        bool eeHint = state.Settings.Enhancements && Relic.Core.Launch.Enhancements.ShippedFor(versionId);
        using (var splash = new PlaySplashForm(versionId, eeHint, state))
        using (var tray = new PlayTray(splash, () => showSignal.Set()))
        {
            session.Log += splash.SetStatusSafe;
            session.GameAppeared += splash.CloseSafe;
            // The moment the game is seen gone, before the settle window: there is no page to say it on.
            session.GameClosed += tray.Notify;
            splash.Shown += (_, _) =>
            {
                tray.WorkStarted();
                work = Task.Run(async () =>
                {
                    try
                    {
                        // A version whose client is not known to start on this OS through Relic's launch
                        // path is asked about first, before the server is.
                        if (!ConfirmWindows11OnSplash(splash, versionId)) return;
                        // Then a file of the player's own in the place of one Relic ships, while the version's
                        // stored choice is "ask" — the answer goes into the launch, it never stops one.
                        session.OwnFilesAnswer = AskOwnFilesOnSplash(splash, state, versionId, ownFilesSignal);
                        // The server check happens NOW, under the splash — not silently before it. That
                        // way it may retry (see PlayPreflight) without the shortcut looking dead, and on
                        // the happy path the user sees no extra dialog.
                        var pre = await PlayPreflight.CheckAsync(versionId, splash.SetStatusSafe);
                        if (!ConfirmOnSplash(splash, versionId, pre)) return;
                        await session.PlayAsync(versionId);
                        // What the session end did — the Fiddler and the profile decision — in words.
                        if (session.Result is { } result) tray.Notify(result);
                    }
                    catch (Exception ex) { error = ex; }
                    finally
                    {
                        splash.CloseSafe(); // error, or a game that never appeared — it closes either way
                        // The loop ends: at once after an error (its dialog follows), else once the last
                        // balloon had its time on screen.
                        tray.WorkDone(waitForBalloons: error is null);
                    }
                });
            };
            Application.Run(tray);
        }
        try { work?.GetAwaiter().GetResult(); } catch { /* error is already captured above */ }
        if (error is not null)
        {
            Log.Error("RunPlay", error);
            // Fiddler closed by the user is not a launch error — it gets its own title, so the user
            // understands at once that Relic closed the game on purpose, not that it crashed.
            MessageBox.Show(error.Message,
                error is FiddlerClosedException ? L.T("shell.play.closedTitle") : "Relic",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>The Windows 11 question of <see cref="PlayPreflight.ConfirmWindows11"/>, raised on the
    /// splash's thread from the worker thread, like <see cref="ConfirmOnSplash"/> — which also says why.</summary>
    private static bool ConfirmWindows11OnSplash(PlaySplashForm splash, string versionId)
    {
        try
        {
            bool go = (bool)splash.Invoke(() => PlayPreflight.ConfirmWindows11(splash, versionId, splash.SetStatusSafe));
            if (!go) Log.Info($"shortcut launch cancelled by user ({versionId})");
            return go;
        }
        catch (Exception ex)
        {
            // The splash vanished under us — do not start the game silently without the user's answer.
            Log.Error("pre-launch confirmation", ex);
            return false;
        }
    }

    /// <summary>
    /// The question of <see cref="PlayPreflight.AskOwnFiles"/> in the standalone play process: the game
    /// folder is looked at here, on the worker thread, and the dialog is raised on the splash's thread. It is
    /// asked only while the version's stored choice is "ask" and the folder holds a file of the player's own
    /// in the place of one Relic ships. "Don't ask again" goes into this process's own state — a save the
    /// session makes later keeps it — and into the state FILE, read again for it (<see cref="RelicState.RememberOwnFilesChoice"/>:
    /// the launcher running beside this process holds its own copy and saves it whole), and the launcher is
    /// told to read it in. null = nothing asked: the stored choice applies; a question that fails asks nothing.
    /// </summary>
    private static string? AskOwnFilesOnSplash(PlaySplashForm splash, RelicState state, string versionId,
        EventWaitHandle ownFilesSignal)
    {
        try
        {
            var inst = state.FindInstalled(versionId);
            if (inst is null || state.Settings.OwnFilesChoiceFor(versionId) != OwnFileChoices.Ask) return null;
            var files = GamePatcher.OwnFiles(inst.Id, inst.GameDir, withInjectables: state.Settings.Enhancements);
            if (files.Count == 0) return null;
            var answer = (PlayPreflight.OwnFilesAnswer)splash.Invoke(() => PlayPreflight.AskOwnFiles(splash, versionId, files, splash.SetStatusSafe));
            if (answer.Remember)
            {
                // A store that fails costs the "don't ask again", never this launch's answer.
                try
                {
                    state.Settings.SetOwnFilesChoice(versionId, answer.Choice);
                    if (RelicState.RememberOwnFilesChoice(versionId, answer.Choice)) ownFilesSignal.Set();
                    else Log.Info($"shortcut launch: the answer about the player's own game files of {versionId} could not be stored in state.json — it holds for this launch");
                }
                catch (Exception ex) { Log.Error("shortcut launch: storing the answer about the player's own game files", ex); }
            }
            return answer.Choice;
        }
        catch (Exception ex)
        {
            Log.Error("shortcut launch: the question about the player's own game files", ex);
            return null;
        }
    }

    /// <summary>The pre-launch question, raised on the splash's thread (the dialog is modal to it, so
    /// it has to come from there) from the worker thread that did the check.</summary>
    private static bool ConfirmOnSplash(PlaySplashForm splash, string versionId, ServerProbeResult pre)
    {
        if (pre.AllGood) return true;
        splash.SetStatusSafe(L.T("shell.splash.waitingConfirm"));
        try
        {
            bool go = (bool)splash.Invoke(() => PlayPreflight.Confirm(splash, versionId, pre));
            if (!go) Log.Info($"shortcut launch cancelled by user ({versionId})");
            return go;
        }
        catch (Exception ex)
        {
            // The splash vanished under us — do not start the game silently without the user's answer.
            Log.Error("pre-launch confirmation", ex);
            return false;
        }
    }

    /// <summary>
    /// The message loop of the standalone play process (<see cref="RunPlay"/>) and its own notification-area icon. The
    /// splash is the loop's first window, and the loop runs on once it has closed — the game has appeared, the session
    /// goes on in the background —, so what the session end says (the game has closed, then what the session end did)
    /// reaches the player as balloons of this icon, as the launcher's own tray says it for a session the launcher runs.
    /// The icon appears with the first balloon. The loop ends once the splash has closed and the session's work is done:
    /// at once when no balloon was shown or the work failed (its dialog follows), else once the last balloon has had its
    /// time on screen — the icon stays while its balloon may show. A click on a balloon or on the icon brings up the
    /// launcher's window.
    /// </summary>
    private sealed class PlayTray : ApplicationContext
    {
        private const uint SpiGetMessageDuration = 0x2016;

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref uint value, uint update);

        private readonly NotifyIcon _icon;
        // A window of this loop's thread: what the session raises on its own threads is handed to the loop through it.
        private readonly Control _loop;
        private readonly Action _openLauncher;
        private System.Windows.Forms.Timer? _linger;
        private bool _workStarted, _splashClosed, _workDone, _waitForBalloons = true, _ended, _removed;
        private long _lastBalloonAt = -1;

        /// <param name="splash">The loop's first window: shown when the loop starts.</param>
        /// <param name="openLauncher">Brings up the running launcher's window.</param>
        public PlayTray(Form splash, Action openLauncher) : base(splash)
        {
            _openLauncher = openLauncher;
            _loop = new Control();
            _ = _loop.Handle; // made now, on the thread the loop runs on
            // The launcher window's class, not this context's MainForm (the splash).
            _icon = new NotifyIcon { Icon = global::Relic.App.MainForm.TrayIcon(), Text = "Relic", Visible = false };
            _icon.BalloonTipClicked += (_, _) => OpenLauncher();
            _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenLauncher(); };
        }

        /// <summary>The session's work has started (the splash's Shown, on the loop's thread): from here the loop waits
        /// for its end.</summary>
        public void WorkStarted() => _workStarted = true;

        /// <summary>A notice of the session — raised on one of its threads, never waited for: shown on the loop's.</summary>
        public void Notify(SessionNotice notice) => OnLoop(() => Show(notice));

        /// <summary>The session's work is done (its finally). <paramref name="waitForBalloons"/>: keep the icon while
        /// its last balloon is on screen; false when the work failed and its dialog follows.</summary>
        public void WorkDone(bool waitForBalloons) => OnLoop(() =>
        {
            _workDone = true;
            _waitForBalloons = waitForBalloons;
            MaybeEnd();
        });

        /// <summary>The splash has closed: the session goes on in the background, and so does the loop — unless the
        /// session's work never started.</summary>
        protected override void OnMainFormClosed(object? sender, EventArgs e)
        {
            _splashClosed = true;
            if (!_workStarted) End();
            else MaybeEnd();
        }

        // The icon goes the moment the loop ends; the timer and the window that hand the loop its work go with the
        // context (ExitThread may run inside a callback of either).
        protected override void ExitThreadCore()
        {
            RemoveIcon();
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                RemoveIcon();
                _linger?.Dispose();
                _loop.Dispose();
            }
            base.Dispose(disposing);
        }

        private void OnLoop(Action action)
        {
            try
            {
                if (_loop.IsHandleCreated && !_loop.IsDisposed) _loop.BeginInvoke(action);
            }
            catch (Exception ex) { Log.Error("play: a notice could not reach this process's tray (non-fatal)", ex); }
        }

        private void Show(SessionNotice notice)
        {
            if (_ended || string.IsNullOrWhiteSpace(notice.Text)) return;
            try
            {
                _icon.Visible = true;
                _icon.ShowBalloonTip(5000, "Relic", notice.Text, global::Relic.App.MainForm.NoticeIcon(notice));
                _lastBalloonAt = Environment.TickCount64;
            }
            catch (Exception ex) { Log.Error($"play: the {notice.Kind} notice could not be shown in the tray (non-fatal)", ex); }
        }

        private void MaybeEnd()
        {
            if (_ended || !_splashClosed || !_workDone || _linger is not null) return;
            long left = _waitForBalloons && _lastBalloonAt >= 0 ? _lastBalloonAt + OnScreenMs() - Environment.TickCount64 : 0;
            if (left <= 0)
            {
                End();
                return;
            }
            _linger = new System.Windows.Forms.Timer { Interval = (int)Math.Min(left, int.MaxValue) };
            _linger.Tick += (_, _) => End();
            _linger.Start();
        }

        private void OpenLauncher()
        {
            try { _openLauncher(); }
            catch (Exception ex) { Log.Error("play: bringing up the launcher's window (non-fatal)", ex); }
            // Clicked once the session's work is done: the launcher's window is up, the balloons have nothing left to say.
            if (_splashClosed && _workDone) End();
        }

        private void End()
        {
            if (_ended) return;
            _ended = true;
            ExitThread();
        }

        private void RemoveIcon()
        {
            if (_removed) return;
            _removed = true;
            _linger?.Stop();
            _icon.Visible = false;
            _icon.Dispose();
        }

        /// <summary>How long a balloon stays on screen, in milliseconds: the system's time for notifications (its
        /// accessibility setting, 5 s unless changed; at most a minute here), and a margin for it to come and go.</summary>
        private static long OnScreenMs()
        {
            uint seconds = 5;
            try
            {
                if (!SystemParametersInfo(SpiGetMessageDuration, 0, ref seconds, 0)) seconds = 5;
            }
            catch (Exception) { seconds = 5; }
            return Math.Clamp((long)seconds, 5, 60) * 1000 + 2000;
        }
    }

    /// <summary>
    /// "Relic.exe --uninstall-probe &lt;file&gt;" — write the key=value snapshot the uninstaller's
    /// options page is built from (how many GB of games, is Fiddler installed). Exit 0 on success so
    /// the Pascal side can tell a real answer from a build too old to know this switch.
    /// </summary>
    private static void RunUninstallProbe(string path)
    {
        try
        {
            Uninstaller.WriteProbe(path);
        }
        catch (Exception ex)
        {
            Log.Error("uninstall probe", ex);
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// "Relic.exe --uninstall-cleanup [--games] [--keep-fiddler]" — remove everything Relic wrote
    /// OUTSIDE {app}, which is all the Inno uninstaller ever owned. Exit code 0 = clean, 1 = finished
    /// but something had to stay (the uninstaller shows the report). Never throws: a failure here
    /// must not abort the uninstall itself.
    /// </summary>
    private static void RunUninstallCleanup(string[] args)
    {
        // The report strings come from the language saved in state.json — read it BEFORE the
        // cleanup deletes that file.
        InitLanguage();
        string report = Path.Combine(Path.GetTempPath(), "relic-uninstall.log");
        try
        {
            var opt = new CleanupOptions(
                RemoveGames: args.Contains("--games", StringComparer.OrdinalIgnoreCase),
                RemoveFiddler: !args.Contains("--keep-fiddler", StringComparer.OrdinalIgnoreCase));
            Log.Info($"uninstall cleanup: games={opt.RemoveGames} fiddler={opt.RemoveFiddler}");

            var r = Uninstaller.Run(opt);
            // Written to %TEMP%, not to the log: %LOCALAPPDATA%\Relic\logs is one of the things that
            // just got deleted, and recreating it would undo the cleanup.
            File.WriteAllText(report,
                L.T("shell.uninstall.reportTitle", new { time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }) + $"\r\n\r\n{r.Text}");
            Environment.ExitCode = r.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(report, L.T("shell.uninstall.cleanupFailed") + $"\r\n{ex}"); } catch { /* ignore */ }
            Environment.ExitCode = 1;
        }
    }

    private static void Fatal(string where, Exception? ex)
    {
        Log.Error($"FATAL {where}", ex ?? new Exception("unknown"));
        try
        {
            MessageBox.Show(
                L.T("shell.fatal.body", new { message = ex?.Message, logFile = Log.LogFile }),
                L.T("shell.fatal.title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* ignore */ }
    }
}
