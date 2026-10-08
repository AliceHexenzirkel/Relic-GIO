using Relic.Core.Fiddler;
using Relic.Core.Play;
using Relic.Core.Server;
using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.App;

internal static class Program
{
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
        if (!createdNew)
        {
            // The launcher is already running (tray or open window): its icon is already there, so
            // the play session goes into this process — a second Backend would
            // overwrite state.json with its own stale state.
            if (playVersion is not null) { RunPlay(playVersion); return; }
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
    /// </summary>
    private static void RunPlay(string versionId)
    {
        var state = RelicState.Load();
        L.Init(state.Settings.Language);
        var session = new PlaySession(state);
        session.Log += m => Log.Info($"[play] {m}");

        // "Starting the game" splash: without it the desktop shortcut would show NOTHING while
        // Fiddler + the profile are being prepared (tens of seconds) — it would simply look broken. The
        // session starts only at Shown, so no event can arrive before the window; the splash closes
        // when the client has actually been seen running, and the session continues in the background
        // (waits for the game to close and restores the profile) — which is why it is awaited once
        // more below.
        Exception? error = null;
        Task? work = null;
        // Same hint rule as Backend.EnhancementsHintFor — this path has no Backend (the launcher is
        // already running in another process), only the state it just loaded.
        bool eeHint = state.Settings.Enhancements && Relic.Core.Launch.Enhancements.ShippedFor(versionId);
        using (var splash = new PlaySplashForm(versionId, eeHint, state))
        {
            session.Log += splash.SetStatusSafe;
            session.GameAppeared += splash.CloseSafe;
            splash.Shown += (_, _) => work = Task.Run(async () =>
            {
                try
                {
                    // The server check happens NOW, under the splash — not silently before it. That
                    // way it may retry (see PlayPreflight) without the shortcut looking dead, and on
                    // the happy path the user sees no extra dialog.
                    var pre = await PlayPreflight.CheckAsync(versionId, splash.SetStatusSafe);
                    if (!ConfirmOnSplash(splash, versionId, pre)) return;
                    await session.PlayAsync(versionId);
                }
                catch (Exception ex) { error = ex; }
                finally { splash.CloseSafe(); } // error, or a game that never appeared — it closes either way
            });
            Application.Run(splash);
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
