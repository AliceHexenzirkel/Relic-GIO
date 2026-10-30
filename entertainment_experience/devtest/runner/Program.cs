using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relic.Core.Fiddler;
using Relic.Core.Install;
using Relic.Core.Isolation;
using Relic.Core.Play;
using Relic.Core.State;
using CoreLog = Relic.Core.Util.Log;

namespace Relic.DevTest;

/// <summary>
/// The dev-only headless runner of the DevTest bed. <c>play</c> runs one session of an installed version through
/// Relic's own <see cref="PlaySession"/> — the session gate, the profile swap, Fiddler, the payload, the injected
/// launch and the post-exit decision, exactly as PLAY does them — with a DevTest build of the enhancements DLL taken
/// from a staged copy of the repository's payload, and puts the release DLL back afterwards. It opens no window and
/// asks nothing: whatever PLAY would have to ask is refused before anything changes.
/// </summary>
internal static class Program
{
    public const int ExitOk = 0;
    public const int ExitSessionFailed = 1;
    public const int ExitRefused = 2;
    public const int ExitDevDllLeft = 3;
    public const int ExitWindowNotRestored = 4;
    public const int ExitSlotLoaded = 5;
    public const int ExitFiddlerLeft = 6;
    public const int ExitUsage = 64;

    private const string RunnerMutexName = @"Local\Relic.DevTestRunner";

    /// <summary>The prefix of the variables a scenario keeps its secrets in (the agent token, a test account's
    /// password): none of them is the runner's business.</summary>
    public const string SecretPrefix = "RELIC_DEVTEST_";

    /// <summary>How long the work after a session waits for the session gate: Relic's own recovery takes it for a
    /// few seconds at a time while a session's profile is still loaded.</summary>
    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(60);

    private static readonly string[] PlayOptions =
        { "version", "dll", "expect-host", "own-files", "window", "config", "log", "result", "stage", "payload", "restore-from" };
    private static readonly string[] RestoreOptions = { "version", "log", "result", "restore-from" };
    private static readonly string[] RulesOptions = { "expect-host", "log", "result" };

    private const string Usage = """
        DevTestRunner -- the dev-only runner of the DevTest bed (never part of a release)

          DevTestRunner play --version <v> --dll <DevTest CLibrary.dll> --expect-host <host>
                             [--own-files keep|replace] [--window WxH|keep] [--config <cfg block.json>]
                             [--log <file>] [--result <file>] [--stage <folder>] [--payload <folder>]
                             [--restore-from <payload folder>]
              one session of <v> through Relic's PlaySession with the DevTest DLL, then the release DLL and the
              slot's screen values back. Defaults: --own-files keep, --window 1600x900, --stage D:\relic-devtest\stage
          DevTestRunner check  <the arguments of play>     the preflight alone: nothing is changed
          DevTestRunner restore --version <v> [--restore-from <payload folder>] [--log <file>] [--result <file>]
              the release DLL and the slot's screen values back (what a session that could not finish left)
          DevTestRunner restore-window --version <v> [--log <file>] [--result <file>]
              the slot's screen values back, once Relic restored the live profile
          DevTestRunner fiddler-rules --expect-host <host> [--log <file>] [--result <file>]
              Fiddler's redirect rules (CustomRules.js) rendered for Relic's server, which must be <host>; only
              while Fiddler is closed and its root certificate already trusted: no consent dialog, nothing else
              changed (Relic's next PLAY renders them again for whatever server it then points at)
          DevTestRunner --tray      (the logon recovery, when no installed Relic was found to name) starts the
                                    installed Relic --tray and exits
          DevTestRunner selftest    the runner's own mechanics on temp files: no game, no profile, no state

        Exit codes: 0 done, 1 the session failed, 2 refused (nothing changed), 3 the release DLL is not back in the
        game folder, 4 the screen values could not be put back, 5 the session's profile is still loaded (a client
        was running when it ended: Relic's recovery puts the live profile back -- then restore-window, when the
        screen values wait for it), 6 Fiddler is still running after the session, 64 usage.
        """;

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    private static async Task<int> Main(string[] args)
    {
        Paths.RunnerDir = AppContext.BaseDirectory;
        bool tray = args.Contains("--tray");
        if (tray) FreeConsole();   // started by Windows at a logon: the console window it was given goes at once
        DropSecrets();
        if (tray) return Tray();
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        string command = args[0];
        Options o;
        try
        {
            o = command switch
            {
                "play" or "check" => Options.Parse(args.Skip(1).ToList(), PlayOptions),
                "restore" => Options.Parse(args.Skip(1).ToList(), RestoreOptions),
                "restore-window" => Options.Parse(args.Skip(1).ToList(), RestoreOptions.Where(k => k != "restore-from").ToArray()),
                "fiddler-rules" => Options.Parse(args.Skip(1).ToList(), RulesOptions),
                "selftest" => Options.Parse(args.Skip(1).ToList(), Array.Empty<string>()),
                _ => throw new UsageException($"unknown command '{command}'"),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine(Usage);
            return ExitUsage;
        }
        if (command == "selftest") return SelfTest.Run();

        using var log = new RunLog(o.Get("log"));
        var result = new JsonObject
        {
            ["command"] = command,
            ["version"] = o.Get("version"),
            ["startedAt"] = DateTimeOffset.Now.ToString("o"),
        };
        int exit;
        try
        {
            exit = command switch
            {
                "play" => await Play(o, log, result, checkOnly: false),
                "check" => await Play(o, log, result, checkOnly: true),
                "restore" => Restore(o, log, result, dll: true),
                "fiddler-rules" => FiddlerRules(o, log, result),
                _ => Restore(o, log, result, dll: false),
            };
        }
        catch (UsageException ex)
        {
            log.Line($"error: {ex.Message}");
            Console.Error.WriteLine(Usage);
            exit = ExitUsage;
        }
        catch (Refusal r)
        {
            log.Line("refused -- nothing was changed:");
            foreach (string p in r.Problems) log.Line("  - " + p);
            result["problems"] = new JsonArray(r.Problems.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            exit = ExitRefused;
        }
        catch (Exception ex)
        {
            log.Line($"error: {ex}");
            result["error"] = ex.Message;
            exit = ExitSessionFailed;
        }
        result["exit"] = exit;
        result["endedAt"] = DateTimeOffset.Now.ToString("o");
        WriteResult(o.Get("result"), result, log);
        log.Line($"exit {exit}");
        return exit;
    }

    /// <summary>The scenario's secrets leave this process's environment before anything starts: neither launcher.exe,
    /// the game, nor Fiddler inherit them (nor a crash dump of any of them). Returns how many went.</summary>
    public static int DropSecrets()
    {
        var names = Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(k => k.ToString() ?? "")
            .Where(k => k.StartsWith(SecretPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (string name in names) Environment.SetEnvironmentVariable(name, null);
        return names.Count;
    }

    /// <summary>The worst of what is left, in this order: a DevTest DLL in the game folder, screen values that could
    /// not be put back, the session's profile still loaded (the owner's screen values waiting for it, or not),
    /// Fiddler still running, a failed session.</summary>
    public static int ExitCodeFor(bool devDllLeft, bool windowFailed, bool slotLoaded, bool fiddlerLeft, bool sessionFailed) =>
        devDllLeft ? ExitDevDllLeft
        : windowFailed ? ExitWindowNotRestored
        : slotLoaded ? ExitSlotLoaded
        : fiddlerLeft ? ExitFiddlerLeft
        : sessionFailed ? ExitSessionFailed
        : ExitOk;

    private static async Task<int> Play(Options o, RunLog log, JsonObject result, bool checkOnly)
    {
        using var single = new Mutex(true, RunnerMutexName, out bool created);
        if (!created) throw new Refusal("another DevTestRunner is at work -- one at a time");

        var plan = Preflight.Run(o);
        result["dll"] = plan.DllPath;
        result["dllSha256"] = plan.DllSha256;
        result["gameDir"] = plan.Inst.GameDir;
        result["profile"] = plan.Inst.ProfileId;
        result["stage"] = plan.StageDir;
        result["releaseFrom"] = plan.Release.From;
        result["releasePayload"] = plan.Release.Root;
        result["releaseSha256"] = plan.Release.Entry.Sha256;
        result["window"] = plan.Window?.ToString() ?? "keep";
        result["ownFiles"] = plan.OwnFiles;
        result["notes"] = new JsonArray(plan.Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        log.Line($"preflight: {plan.Version} in {plan.Inst.GameDir} (slot {plan.Inst.ProfileId}), server {plan.ExpectHost}");
        log.Line($"preflight: DevTest DLL {plan.DllPath} (sha256 {plan.DllSha256})");
        log.Line($"preflight: release DLL from {plan.Release.From} ({plan.Release.Root}, sha256 {plan.Release.Entry.Sha256})");
        log.Line($"preflight: window {plan.Window?.ToString() ?? "kept as the slot has it"}, own files: {plan.OwnFiles}");
        foreach (string n in plan.Notes) log.Line("note: " + n);
        if (checkOnly)
        {
            log.Line("check: the preflight passed -- nothing was changed");
            return ExitOk;
        }

        // A Ctrl+C in the runner's console must not end it while the slot is loaded: the session ends when the game
        // is closed, and only then are the profile, Fiddler, the DLL and the screen values put back.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            log.Line("Ctrl+C ignored: close the game (or send the quit command) -- the runner then puts everything back");
        };

        // The stage folder first: nothing of the game or of Relic changes before it is complete.
        string staged = Stage.Build(plan.StageDir, plan.SourcePayload, plan.Version, plan.SourceEntry,
            plan.DllPath, plan.DllSha256, log);
        Stage.Activate(plan.StageDir);
        // The system proxy as it is before Fiddler makes itself the proxy: what goes back if Fiddler is killed.
        ProxyGuard.Snapshot(plan.StageDir, log, result);
        var injectables = GamePatcher.Injectables(plan.Version, plan.Inst.GameDir);
        if (!injectables.Any(i => i.Shipped && string.Equals(i.Dst, plan.SourceEntry.Dst, StringComparison.OrdinalIgnoreCase)))
            throw new Refusal("the staged payload does not hand the enhancements DLL to the session");
        result["stagedSha256"] = staged;
        log.Line($"stage: the session applies {GamePatcher.PayloadRoot()}");

        // The screen values. A refusal here comes before any edit of them: nothing was changed.
        var (windowApplied, windowPrepFailed) = WindowBefore(plan, log, result);

        // From here on something may have changed: nothing ends the run early any more. Whatever happens, what the
        // session left is put back below, and the exit code says what is still left.
        bool sessionFailed = false, sessionStarted = false;
        try
        {
            if (windowPrepFailed)
            {
                sessionFailed = true;
                result["sessionOk"] = false;
                result["sessionError"] = "the screen values could not be prepared -- the session was not started";
            }
            else
            {
                SeedConfig(plan, log, result);

                CoreLog.Info($"DevTestRunner: a DevTest session of {plan.Version} (DLL sha256 {staged}) -- dev only");
                var session = new PlaySession(plan.State) { OwnFilesAnswer = plan.OwnFiles };
                bool appeared = false;
                session.Log += m => log.Line("session: " + m);
                session.GameAppeared += () =>
                {
                    // Written at once: a scenario runner waiting for the DLL learns here whether launcher.exe injected
                    // it, and stops waiting when it did not.
                    appeared = true;
                    log.Line($"session: the game client is up (enhancements injected: {session.EnhancementsActive})");
                    result["phase"] = "gameUp";
                    result["enhancementsActive"] = session.EnhancementsActive;
                    PointRecoveryAtLauncher(log, result);
                    WriteResult(o.Get("result"), result, log);
                };
                var started = DateTimeOffset.Now;
                sessionStarted = true;
                try
                {
                    await session.PlayAsync(plan.Version);
                    result["sessionOk"] = appeared;
                    if (!appeared)
                    {
                        sessionFailed = true;
                        result["sessionError"] = "the game client was never seen running";
                    }
                }
                catch (Exception ex)
                {
                    sessionFailed = true;
                    result["sessionOk"] = false;
                    result["sessionError"] = ex.Message;
                    log.Line($"session: failed -- {ex.Message}");
                    CoreLog.Error("DevTestRunner: the session failed", ex);
                }
                result["enhancementsActive"] = session.EnhancementsActive;
                result["sessionSeconds"] = Math.Round((DateTimeOffset.Now - started).TotalSeconds, 1);
            }
        }
        catch (Exception ex)
        {
            sessionFailed = true;
            string why = ex is Refusal r ? string.Join("; ", r.Problems) : ex.Message;
            result["sessionOk"] = false;
            result["sessionError"] = why;
            log.Line($"session: not started -- {why}");
        }

        var left = AfterSession(plan.Version, plan.Inst, plan.Release, windowApplied, sessionStarted, log, result);
        if (!ProxyGuard.RepairIfHijacked(plan.StageDir, log, result)) left.FiddlerLeft = true;
        CoreLog.Info($"DevTestRunner: the DevTest session of {plan.Version} is over (release DLL back: {!left.DevDllLeft})");
        return ExitCodeFor(left.DevDllLeft, left.WindowFailed, left.SlotLoaded, left.FiddlerLeft, sessionFailed);
    }

    /// <summary>What a session leaves, found and put back.</summary>
    private sealed class Left
    {
        public bool DevDllLeft, WindowFailed, SlotLoaded, FiddlerLeft;
    }

    /// <summary>
    /// What a session left, put back under the session gate — no PLAY of the launcher and no recovery pass writes the
    /// game folder or the slot meanwhile — each part on its own, so that one that fails does not keep the next from
    /// running: the release DLL back in the game folder, the owner's screen values back in the slot's export, then
    /// what is still loaded (the session's profile, Fiddler). A gate another session holds is that session's: nothing
    /// is written then, and what is there is only reported.
    /// </summary>
    private static Left AfterSession(string version, InstalledVersion inst, ReleaseSource? release, bool windowApplied,
        bool sessionStarted, RunLog log, JsonObject result)
    {
        var left = new Left();
        string copy = Paths.SlotReg(inst.ProfileId) + WindowMode.CopySuffix;
        Semaphore? gate = null;
        bool held = false;
        try
        {
            gate = new Semaphore(1, 1, PlaySession.SessionGateName);
            held = gate.WaitOne(GateWait);
        }
        catch (Exception ex)
        {
            log.Line($"after the session: the session gate could not be taken -- {ex.Message}");
        }
        try
        {
            if (!held)
            {
                string why = gate is null ? "the session gate could not be taken"
                    : "another play session holds the session gate (Relic's PLAY, a desktop shortcut)";
                log.Line($"after the session: {why} -- the game folder and the profile may be another session's: nothing is written");
                result["afterSession"] = why;
            }
            if (release is not null)
            {
                try
                {
                    left.DevDllLeft = !RestoreRelease(version, inst, release, held, log, result);
                }
                catch (Exception ex)
                {
                    left.DevDllLeft = true;
                    log.Line($"restore: the game folder's DLL could not be checked -- {ex.Message}; run: DevTestRunner restore --version {version}");
                    result["releaseDll"] = new JsonObject { ["restored"] = false, ["why"] = ex.Message };
                }
            }
            if (windowApplied || File.Exists(copy))
            {
                try
                {
                    (left.WindowFailed, left.SlotLoaded) = WindowAfter(version, inst, held, log, result);
                }
                catch (Exception ex)
                {
                    left.WindowFailed = true;
                    log.Line($"window: the owner's screen values could not be put back -- {ex.Message}; the copy {copy} keeps them");
                    result["windowRestored"] = false;
                    result["windowError"] = ex.Message;
                }
            }
            if (sessionStarted)
            {
                try
                {
                    var (loaded, fiddler) = StillLoaded(held, log, result);
                    left.SlotLoaded |= loaded;
                    left.FiddlerLeft = fiddler;
                }
                catch (Exception ex)
                {
                    log.Line($"after the session: what is still loaded could not be told -- {ex.Message}");
                    result["afterSession"] = ex.Message;
                }
            }
        }
        finally
        {
            if (held) gate!.Release();
            gate?.Dispose();
        }
        return left;
    }

    /// <summary>What the session left loaded: its profile, when a client was still running as its settle window
    /// ended (Relic's recovery puts the live profile back once none runs), and Fiddler. (loaded, fiddler).</summary>
    private static (bool Loaded, bool Fiddler) StillLoaded(bool held, RunLog log, JsonObject result)
    {
        if (!held)
        {
            result["liveProfileBack"] = null;
            result["fiddlerRunning"] = null;
            return (false, false);
        }
        var profiles = ProfileStore.ForGenshin();
        bool live = profiles.LiveIsActive();
        string active = profiles.IsTornLoad() ? "a swap cut halfway" : profiles.ActiveId();
        bool fiddler = FiddlerAutomation.IsRunning;
        result["liveProfileBack"] = live;
        result["activeProfile"] = active;
        result["fiddlerRunning"] = fiddler;
        if (!live)
            log.Line($"profile: '{active}' is still loaded (a client was running when the session ended) -- Relic's recovery puts the live "
                + "profile back once none runs: the installed Relic does (its tray instance within seconds, or its next start), or run: DevTestRunner --tray");
        if (fiddler)
            log.Line(live
                ? "fiddler: still running after the session, which did not stop it -- it is the system proxy while it runs: close it"
                : "fiddler: still running -- Relic's recovery stops it together with the profile restore");
        return (!live, fiddler);
    }

    /// <summary>The owner's screen values from an earlier run back first, then this run's window — under the
    /// session gate, so no swap of another process saves or loads the slot meanwhile. (applied, failed).</summary>
    private static (bool Applied, bool Failed) WindowBefore(Plan plan, RunLog log, JsonObject result)
    {
        if (!plan.WindowLeftover && plan.Window is null) return (false, false);
        using var gate = new Semaphore(1, 1, PlaySession.SessionGateName);
        if (!gate.WaitOne(TimeSpan.FromSeconds(30)))
            throw new Refusal("a play session started meanwhile -- the screen values were not changed");
        try
        {
            if (!ProfileStore.ForGenshin().LiveIsActive())
                throw new Refusal("a private profile was loaded meanwhile -- the screen values were not changed");
            if (plan.WindowLeftover)
            {
                string? was = WindowMode.Restore(plan.SlotReg);
                log.Line($"window: the owner's screen values from an earlier run are back (the slot held {was})");
            }
            if (plan.Window is null) return (false, false);
            string before = WindowMode.Apply(plan.SlotReg, plan.Window);
            log.Line($"window: {plan.Window} windowed for this session (the slot held {before}; a copy is kept beside it)");
            result["windowApplied"] = true;
            return (true, false);
        }
        catch (Exception ex) when (ex is not Refusal)
        {
            log.Line($"window: the screen values could not be prepared -- {ex.Message}");
            result["windowError"] = ex.Message;
            return (false, true);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The owner's screen values back into the slot's export, once the slot is saved and no longer loaded.
    /// The caller holds the session gate when <paramref name="held"/>; without it the slot may be another session's,
    /// and nothing is written. (failed, deferred).</summary>
    private static (bool Failed, bool Deferred) WindowAfter(string version, InstalledVersion inst, bool held, RunLog log, JsonObject result)
    {
        string reg = Paths.SlotReg(inst.ProfileId);
        string copy = reg + WindowMode.CopySuffix;
        if (!File.Exists(copy))
        {
            log.Line($"window: nothing to put back (no {Path.GetFileName(copy)} in the slot of {version})");
            result["windowRestored"] = true;
            return (false, false);
        }
        var profiles = ProfileStore.ForGenshin();
        if (!held || profiles.IsTornLoad() || string.Equals(profiles.ActiveId(), inst.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            string why = held ? "the slot is still loaded (a client was still running when the session ended)"
                : "another play session is in progress";
            log.Line($"window: {why} -- the slot's screen values and {Path.GetFileName(copy)} stay; once Relic has put the live "
                + $"profile back (and that session is over), run: DevTestRunner restore-window --version {version}");
            result["windowRestored"] = "deferred";
            return (false, true);
        }
        try
        {
            string? was = WindowMode.Restore(reg);
            log.Line($"window: the owner's screen values are back in the slot (the session left {was})");
            result["windowRestored"] = true;
            return (false, false);
        }
        catch (Exception ex)
        {
            log.Line($"window: the owner's screen values could not be put back -- {ex.Message}; the copy {copy} keeps them");
            result["windowRestored"] = false;
            result["windowError"] = ex.Message;
            return (true, false);
        }
    }

    /// <summary>The scenario's config block as the dev build's own config file (<c>cfg-devtest.json</c> beside the
    /// DLL), written while no client runs. Without <c>--config</c> the file is left as the last dev session left it.</summary>
    private static void SeedConfig(Plan plan, RunLog log, JsonObject result)
    {
        if (plan.ConfigFile is null)
        {
            log.Line("config: no --config -- cfg-devtest.json is left as it is");
            return;
        }
        var node = JsonNode.Parse(File.ReadAllText(plan.ConfigFile)) as JsonObject
            ?? throw new Refusal($"--config {plan.ConfigFile}: not a JSON object");
        JsonObject doc = node.ContainsKey("profiles")
            ? node
            : new JsonObject { ["current_profile"] = "default", ["profiles"] = new JsonObject { ["default"] = node } };
        if (ProcessGuard.AnyRunning())
            throw new Refusal("a game client started meanwhile -- cfg-devtest.json was not written");
        Directory.CreateDirectory(plan.ModDir);
        string dst = Path.Combine(plan.ModDir, "cfg-devtest.json");
        Files.WriteAtomic(dst, new UTF8Encoding(false).GetBytes(doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        log.Line($"config: {dst} written from {plan.ConfigFile}");
        result["config"] = dst;
    }

    /// <summary>The logon recovery the session armed when it loaded the slot names the program that loaded it — this
    /// one, a console program in a build folder. While the slot is loaded it names the installed launcher instead,
    /// which is what that recovery starts anyway (<c>Relic.exe --tray</c>): no console window at the next logon, and
    /// no reliance on the build folder being there. Relic removes the value once live is back, whatever it names.</summary>
    private static void PointRecoveryAtLauncher(RunLog log, JsonObject result)
    {
        try
        {
            if (!Startup.LogonRecoveryArmed()) return;
            string? root = InstalledLauncher.Root();
            if (root is null)
            {
                log.Line("recovery: no installed Relic was found -- the logon recovery names this runner, whose --tray starts Relic");
                return;
            }
            string exe = Path.Combine(root, "Relic.exe");
            Startup.ArmLogonRecovery(exe);
            result["logonRecovery"] = exe;
            log.Line($"recovery: while the slot is loaded the logon recovery starts {exe} --tray");
        }
        catch (Exception ex)
        {
            log.Line($"recovery: the logon recovery still names this runner ({ex.Message})");
        }
    }

    /// <summary>The release DLL back in the game folder: the plan of the release payload applied (read, never written),
    /// then the game folder's DLL proven to be the release one. A folder whose game runs, or that another session holds
    /// (<paramref name="write"/> false), is not written: what it holds is reported. True when it is the release DLL.</summary>
    private static bool RestoreRelease(string version, InstalledVersion inst, ReleaseSource release, bool write, RunLog log, JsonObject result)
    {
        if (release.Entry.Synthetic) return RemoveDevDll(version, inst, release, write, log, result);
        string dst = Path.Combine(inst.GameDir, release.Entry.Dst.Replace('/', Path.DirectorySeparatorChar));
        var outcome = new JsonObject { ["from"] = release.From, ["payload"] = release.Root, ["expected"] = release.Entry.Sha256 };
        result["releaseDll"] = outcome;
        string? notNow = !write ? "another play session holds the session gate"
            : ProcessGuard.AnyRunning() ? "a game client was still running" : null;
        if (notNow is not null)
        {
            bool already = File.Exists(dst) && Files.SameHash(Files.Sha256(dst), release.Entry.Sha256) && !DevBuild.CarriesMarker(dst);
            outcome["restored"] = already;
            outcome["why"] = notNow;
            log.Line(already
                ? $"restore: {notNow} -- the game folder is not touched; its DLL is the release one"
                : $"restore: {notNow} -- the game folder is not touched now; once its game is closed run: DevTestRunner restore --version {version}");
            return already;
        }
        try
        {
            var report = GamePatcher.Apply(version, inst.GameDir, m => log.Line("restore: " + m),
                payloadRoot: release.Root, withInjectables: true);
            log.Line($"restore: {report.Summary}");
        }
        catch (Exception ex)
        {
            log.Line($"restore: the release payload could not be applied -- {ex.Message}");
            outcome["why"] = ex.Message;
        }
        bool there = File.Exists(dst);
        string? sha = there ? Files.Sha256(dst) : null;
        bool marker = there && DevBuild.CarriesMarker(dst);
        bool ok = sha is not null && Files.SameHash(sha, release.Entry.Sha256) && !marker;
        outcome["sha256"] = sha;
        outcome["marker"] = marker;
        outcome["restored"] = ok;
        log.Line(ok
            ? $"restore: {dst} is the release DLL again (sha256 {sha})"
            : $"restore: {dst} is NOT the release DLL (sha256 {sha ?? "none"}{(marker ? ", a DevTest build" : "")}) -- run: DevTestRunner restore --version {version}");
        return ok;
    }

    /// <summary>The release state of a version whose payload ships no enhancements DLL: no DevTest DLL in the game folder.
    /// Only a file that carries the DevTest marker is removed — any other file at that place is not the runner's and stays.
    /// A folder whose game runs, or that another session holds, is not written. True when no DevTest DLL is left there.</summary>
    internal static bool RemoveDevDll(string version, InstalledVersion inst, ReleaseSource release, bool write, RunLog log, JsonObject result,
        bool? clientRunning = null)
    {
        string dst = Path.Combine(inst.GameDir, release.Entry.Dst.Replace('/', Path.DirectorySeparatorChar));
        var outcome = new JsonObject { ["from"] = release.From, ["payload"] = release.Root, ["shipped"] = false };
        result["releaseDll"] = outcome;
        bool ours = File.Exists(dst) && DevBuild.CarriesMarker(dst);
        string? notNow = !write ? "another play session holds the session gate"
            : (clientRunning ?? ProcessGuard.AnyRunning()) ? "a game client was still running" : null;
        if (notNow is not null)
        {
            outcome["restored"] = !ours;
            outcome["why"] = notNow;
            log.Line(ours
                ? $"restore: {notNow} -- the DevTest DLL stays in {dst} for now; once its game is closed run: DevTestRunner restore --version {version}"
                : $"restore: {notNow} -- the game folder holds no DevTest DLL");
            return !ours;
        }
        if (ours)
        {
            try { File.Delete(dst); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Line($"restore: the DevTest DLL could not be removed from {dst} -- {ex.Message}");
                outcome["why"] = ex.Message;
            }
        }
        bool left = File.Exists(dst) && DevBuild.CarriesMarker(dst);
        outcome["removed"] = ours && !left;
        outcome["restored"] = !left;
        log.Line(left
            ? $"restore: {dst} is still the DevTest DLL -- run: DevTestRunner restore --version {version}"
            : ours ? $"restore: the DevTest DLL is out of {dst} -- the payload ships no enhancements DLL for {version}"
                   : $"restore: {dst} holds no DevTest DLL -- the payload ships no enhancements DLL for {version}");
        return !left;
    }

    /// <summary><c>restore</c> (the release DLL, then the screen values) and <c>restore-window</c> (the screen values).</summary>
    private static int Restore(Options o, RunLog log, JsonObject result, bool dll)
    {
        using var single = new Mutex(true, RunnerMutexName, out bool created);
        if (!created) throw new Refusal("another DevTestRunner is at work -- one at a time");
        string version = o.Require("version").Trim();
        var (_, inst) = Preflight.Installed(version);
        ReleaseSource? release = null;
        if (dll)
        {
            var problems = new List<string>();
            var notes = new List<string>();
            string? repo = Paths.RepoRoot();
            release = Preflight.FindRelease(o.Get("restore-from"), repo is null ? null : Path.Combine(repo, "payload"),
                version, problems, notes);
            foreach (string n in notes) log.Line("note: " + n);
            if (release is null) throw new Refusal(problems);
        }
        var left = AfterSession(version, inst, release, windowApplied: true, sessionStarted: false, log, result);
        bool proxyOk = ProxyGuard.RepairIfHijacked(Paths.DefaultStage, log, result);
        return ExitCodeFor(left.DevDllLeft, left.WindowFailed, left.SlotLoaded, !proxyOk, false);
    }

    /// <summary><c>fiddler-rules</c>: Fiddler's redirect rules for the server Relic points at, so a session's preflight
    /// finds Fiddler ready. Refused unless Relic's server is the expected one, Fiddler is installed and closed and its
    /// root certificate is already trusted — so nothing here can open a consent dialog or touch a running proxy.</summary>
    private static int FiddlerRules(Options o, RunLog log, JsonObject result)
    {
        string host = o.Require("expect-host").Trim();
        var state = RelicState.TryRead()
            ?? throw new Refusal($"Relic's state file ({RelicState.DefaultPath}) cannot be read");
        var s = state.Settings;
        var problems = new List<string>();
        if (!string.Equals(s.ServerHost, host, StringComparison.OrdinalIgnoreCase))
            problems.Add($"Relic points at '{s.ServerHost}', not at '{host}' -- point it at the test server first");
        if (!FiddlerAutomation.IsInstalled)
            problems.Add("Fiddler is not installed -- play once from Relic so that it sets Fiddler up");
        else if (!FiddlerAutomation.IsRootCertUsable())
            problems.Add("Fiddler's root certificate is not trusted -- play once from Relic (the consent is the owner's)");
        if (FiddlerAutomation.IsRunning)
            problems.Add("Fiddler is running -- its rules change only while it is closed");
        if (problems.Count > 0) throw new Refusal(problems);
        FiddlerAutomation.WriteCustomRules(s.ServerHost, s.ServerPort);
        var ready = FiddlerAutomation.CheckReady(s.ServerHost, s.ServerPort);
        log.Line($"Fiddler's rules written for {s.ServerHost}:{s.ServerPort} -- ready: {ready.AllGood}");
        result["fiddlerReady"] = ready.AllGood;
        return ready.AllGood ? ExitOk : ExitSessionFailed;
    }

    /// <summary>The logon recovery names this program when no installed launcher was found to name while the slot was
    /// loaded. The recovery itself is the installed launcher's: its background start restores the live profile.</summary>
    private static int Tray()
    {
        string? root = InstalledLauncher.Root();
        if (root is null)
        {
            CoreLog.Error("DevTestRunner --tray: no installed Relic was found -- start Relic by hand: its recovery puts the live profile back");
            return ExitSessionFailed;
        }
        string exe = Path.Combine(root, "Relic.exe");
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--tray") { UseShellExecute = true, WorkingDirectory = root })?.Dispose();
            CoreLog.Info($"DevTestRunner --tray: started {exe} --tray for the logon recovery");
            return ExitOk;
        }
        catch (Exception ex)
        {
            CoreLog.Error($"DevTestRunner --tray: {exe} could not be started", ex);
            return ExitSessionFailed;
        }
    }

    private static void WriteResult(string? path, JsonObject result, RunLog log)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            Files.WriteAtomic(full, new UTF8Encoding(false).GetBytes(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        }
        catch (Exception ex)
        {
            log.Line($"the result file {path} could not be written: {ex.Message}");
        }
    }
}
