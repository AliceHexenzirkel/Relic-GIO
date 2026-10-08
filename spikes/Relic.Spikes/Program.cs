using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Relic.Core.Download;
using Relic.Core.Fiddler;
using Relic.Core.Install;
using Relic.Core.Isolation;
using Relic.Core.Launch;
using Relic.Core.Server;
using Relic.Core.State;
using Relic.Core.Util;

// Spike test runner. Each spike validates one risky mechanism in isolation.
//   usage: Relic.Spikes [isolation|launch|download|fiddler|commands|agent|extract|state|patch|accounts|server|all]
string spike = args.Length > 0 ? args[0].ToLowerInvariant() : "isolation";

return spike switch
{
    "isolation" => IsolationSpike.Run(),
    "launch" => LaunchSpike.Run(),
    "download" => DownloadSpike.Run(),
    "fiddler" => FiddlerSpike.Run(),
    "commands" => CommandsSpike.Run(),
    "agent" => AgentSpike.Run(),
    "extract" => ExtractSpike.Run(),
    "state" => StateSpike.Run(),
    "patch" => PatchSpike.Run(),
    "accounts" => AccountsSpike.Run(),
    "server" => ServerSpike.Run(),
    "deploy" => DeploySpike.Run(),   // opt-in live test (needs RELIC_SSH_* env) — never part of "all"
    "localagent" => LocalAgentSpike.Run(), // opt-in live test (needs RELIC_LOCAL_DIR16) — never part of "all"
    "agentupdate" => AgentUpdateSpike.Run(), // opt-in live test (the real agent on a spare port; needs python, takes a minute) — never part of "all"
    "all" => new[]
    {
        IsolationSpike.Run(), LaunchSpike.Run(), DownloadSpike.Run(),
        FiddlerSpike.Run(), CommandsSpike.Run(), AgentSpike.Run(), ExtractSpike.Run(),
        StateSpike.Run(), PatchSpike.Run(), AccountsSpike.Run(), ServerSpike.Run(),
    }.Max(),
    _ => Fail($"unknown spike '{spike}'"),
};

static int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }

/// <summary>
/// Proves the registry + LocalLow profile-swap round-trips losslessly, purges stale values, and
/// starts fresh profiles clean — all against a SYNTHETIC key (HKCU\Software\RelicSpikeTest\...)
/// and temp folders, so no real Genshin data is touched.
/// </summary>
static class IsolationSpike
{
    public static int Run()
    {
        Console.WriteLine("== isolation spike: registry + LocalLow profile swap (synthetic key) ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        string tmp = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relic", "_spiketest");
        const string regRoot = @"HKCU\Software\RelicSpikeTest";
        string regKey = regRoot + @"\Genshin Impact";
        string localLow = Path.Combine(tmp, "LocalLow_active");
        string store = Path.Combine(tmp, "profiles");

        // clean slate
        Proc.Run("reg", "delete", regRoot, "/f");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(localLow);

        try
        {
            var ps = new ProfileStore(regKey, localLow, store);

            Check("marker: fresh store defaults to live", ps.ActiveId() == "live" && !ps.IsTornLoad());

            // --- state A: "live" ---
            SetReg(regKey, "token", "AAA");
            SetReg(regKey, "data", "live-data");
            File.WriteAllText(Path.Combine(localLow, "save.txt"), "LIVE");
            ps.SaveProfile("live");

            // --- state B: "priv28" ---
            SetReg(regKey, "token", "BBB");
            SetReg(regKey, "data", "priv-data");
            File.WriteAllText(Path.Combine(localLow, "save.txt"), "PRIV");
            ps.SaveProfile("priv28");

            // load "live" back
            ps.LoadProfile("live");
            Check("registry token restored to live (AAA)", RegValue(regKey, "token") == "AAA");
            Check("registry data restored to live", RegValue(regKey, "data") == "live-data");
            Check("LocalLow restored to LIVE", ReadOrNull(Path.Combine(localLow, "save.txt")) == "LIVE");

            // swap live -> priv28
            ps.SwapTo("priv28", "live");
            Check("registry token swapped to priv (BBB)", RegValue(regKey, "token") == "BBB");
            Check("registry data swapped to priv", RegValue(regKey, "data") == "priv-data");
            Check("LocalLow swapped to PRIV", ReadOrNull(Path.Combine(localLow, "save.txt")) == "PRIV");
            Check("marker: active id tracks the loaded profile (priv28)", ps.ActiveId() == "priv28");

            // stale-value purge: junk added under priv must NOT survive a restore of live
            SetReg(regKey, "junk", "XXX");
            ps.SwapTo("live", "priv28");
            Check("stale value purged on restore (junk gone)", RegValue(regKey, "junk") is null);
            Check("token is live again (AAA) after purge", RegValue(regKey, "token") == "AAA");

            // fresh (never-saved) profile starts clean
            ps.SwapTo("priv16", "live");
            Check("fresh profile: empty registry key", RegValue(regKey, "token") is null);
            Check("fresh profile: empty LocalLow", !File.Exists(Path.Combine(localLow, "save.txt")));

            // returning to live still has the live data (round-trip integrity across many swaps)
            ps.SwapTo("live", "priv16");
            Check("round-trip: live token intact after 5 swaps", RegValue(regKey, "token") == "AAA");
            Check("round-trip: live save intact after 5 swaps", ReadOrNull(Path.Combine(localLow, "save.txt")) == "LIVE");
            Check("marker: live is the clean active profile after round-trip", ps.LiveIsActive());

            // --- torn-swap recovery: a crash mid-LoadProfile leaves "loading:<id>" + a disk mix ---
            File.WriteAllText(Path.Combine(store, "active"), "loading:priv28");
            SetReg(regKey, "torn-junk", "ZZZ"); // simulate a partial import over live's data
            Check("torn: detected from the loading marker", ps.IsTornLoad());
            Check("torn: ActiveId still resolves the target (priv28)", ps.ActiveId() == "priv28");
            Check("torn: LiveIsActive is false while torn", !ps.LiveIsActive());
            Check("torn: repair ran", ps.RepairIfTorn());
            Check("torn: repair discarded the junk (torn-junk gone)", RegValue(regKey, "torn-junk") is null);
            Check("torn: repair restored live token (AAA)", RegValue(regKey, "token") == "AAA");
            Check("torn: repair restored live LocalLow", ReadOrNull(Path.Combine(localLow, "save.txt")) == "LIVE");
            Check("torn: live active again after repair", ps.LiveIsActive());
            Check("torn: repair is a no-op when clean", !ps.RepairIfTorn());

            // --- taint: the official client ran on a loaded private profile → live is restored WITHOUT
            // re-exporting the slot, which keeps its pre-taint snapshot (ProfileStore.MarkTainted) ---
            string save = Path.Combine(localLow, "save.txt");
            ps.SwapTo("priv28", "live");                    // priv28 loaded: token BBB, save PRIV
            Check("taint: a clean slot is not tainted", !ps.IsTainted() && !ps.IsTainted("priv28"));
            SetReg(regKey, "token", "OFFICIAL");             // the official client logs in on the loaded key
            File.WriteAllText(save, "OFFICIAL");
            ps.MarkTainted("priv28");
            Check("taint: the active slot reports tainted (no-arg and by id)", ps.IsTainted() && ps.IsTainted("priv28"));
            Check("taint: live can never be tainted", Throws(() => ps.MarkTainted("live")));
            string discarded = ps.RestoreLiveDiscarding();
            Check("taint: RestoreLiveDiscarding names the discarded slot", discarded == "priv28");
            Check("taint: live token back (AAA) without an export of the mix", RegValue(regKey, "token") == "AAA");
            Check("taint: live LocalLow back", ReadOrNull(save) == "LIVE");
            Check("taint: marker is clean live", ps.LiveIsActive());
            Check("taint: cleared once live is loaded", !ps.IsTainted("priv28") && !ps.IsTainted());
            ps.SwapTo("priv28", "live");
            Check("taint: the slot kept its pre-taint registry snapshot (BBB, not OFFICIAL)", RegValue(regKey, "token") == "BBB");
            Check("taint: the slot kept its pre-taint LocalLow snapshot (PRIV)", ReadOrNull(save) == "PRIV");
            Check("taint: a fresh load of the slot is not tainted", !ps.IsTainted());
            // Belt and braces: a tainted ACTIVE slot is never exported through SwapTo either — the
            // path a "--play" session of another version takes while the tainted slot is loaded.
            SetReg(regKey, "token", "OFFICIAL2");
            ps.MarkTainted("priv28");
            ps.SwapTo("live", "priv28");
            Check("taint: SwapTo skips the export of a tainted slot (live AAA)", RegValue(regKey, "token") == "AAA");
            Check("taint: SwapTo dropped the taint", !ps.IsTainted("priv28"));
            ps.SwapTo("priv28", "live");
            Check("taint: slot still holds BBB after the SwapTo path", RegValue(regKey, "token") == "BBB");
            // Same id + taint is NOT the usual no-op: the slot is reloaded from its snapshot.
            SetReg(regKey, "token", "OFFICIAL3");
            ps.MarkTainted("priv28");
            ps.SwapTo("priv28", "priv28");
            Check("taint: same-id swap of a tainted slot reloads the snapshot (BBB)", RegValue(regKey, "token") == "BBB" && !ps.IsTainted());
            // Once the taint is gone the normal export works again (nothing sticks).
            SetReg(regKey, "token", "BBB2");
            ps.SwapTo("live", "priv28");
            ps.SwapTo("priv28", "live");
            Check("taint: normal export resumes after the taint is gone (BBB2)", RegValue(regKey, "token") == "BBB2");
            ps.SwapTo("live", "priv28");
            Check("taint: live intact at the end (AAA)", RegValue(regKey, "token") == "AAA" && ps.LiveIsActive());

            // --- logon recovery hook: the RunOnce value exists only while a private profile is loaded.
            // The very delegate ForGenshin wires, against a synthetic RunOnce key. ---
            Startup.RunOnceKeyOverride = regRoot + @"\RunOnce";
            var hooked = new ProfileStore(regKey, localLow, store) { Loaded = ProfileStore.LogonRecoveryHook };
            Check("runonce: not armed while live is loaded", !Startup.LogonRecoveryArmed());
            hooked.SwapTo("priv28", "live");
            Check("runonce: armed after a private profile is loaded", Startup.LogonRecoveryArmed());
            string? hookValue = RegValue(regRoot + @"\RunOnce", "RelicRecover");
            Check("runonce: value is \"<exe>\" --tray", hookValue is not null && hookValue.EndsWith("\" --tray", StringComparison.Ordinal)
                && hookValue.StartsWith("\"", StringComparison.Ordinal) && hookValue.Contains(".exe", StringComparison.OrdinalIgnoreCase));
            hooked.SwapTo("live", "priv28");
            Check("runonce: removed after live is loaded", !Startup.LogonRecoveryArmed());
            hooked.SwapTo("priv16", "live");
            Check("runonce: armed again for another private profile", Startup.LogonRecoveryArmed());
            File.WriteAllText(Path.Combine(store, "active"), "loading:priv16"); // a crash mid-load...
            Check("runonce: the torn repair (LoadProfile live) disarms it too", hooked.RepairIfTorn() && !Startup.LogonRecoveryArmed());
            Check("runonce: RestoreLiveDiscarding path disarms it", RunHookDiscard(hooked) && !Startup.LogonRecoveryArmed());
        }
        finally
        {
            Startup.RunOnceKeyOverride = null;
            Proc.Run("reg", "delete", regRoot, "/f");
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { /* best effort */ }
        }

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"isolation spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    static void SetReg(string key, string name, string val)
    {
        var r = Proc.Run("reg", "add", key, "/v", name, "/t", "REG_SZ", "/d", val, "/f");
        if (!r.Ok) throw new InvalidOperationException($"reg add failed: {r.Err}");
    }

    static string? RegValue(string key, string name)
    {
        var r = Proc.Run("reg", "query", key, "/v", name);
        if (!r.Ok) return null;
        foreach (var line in r.Out.Split('\n'))
        {
            int idx = line.IndexOf("REG_SZ", StringComparison.Ordinal);
            if (idx >= 0 && line.Contains(name, StringComparison.Ordinal))
                return line[(idx + "REG_SZ".Length)..].Trim();
        }
        return null;
    }

    static string? ReadOrNull(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    static bool Throws(Action a)
    {
        try { a(); return false; } catch { return true; }
    }

    /// <summary>Load a private profile through the hooked store, taint it, restore live discarding —
    /// true when the hook saw the private load (armed) before the discarding restore ran.</summary>
    static bool RunHookDiscard(ProfileStore hooked)
    {
        hooked.SwapTo("priv28", "live");
        bool armed = Startup.LogonRecoveryArmed();
        hooked.MarkTainted("priv28");
        return hooked.RestoreLiveDiscarding() == "priv28" && armed && hooked.LiveIsActive();
    }
}

/// <summary>
/// Validates the launch mechanics that do NOT need the real game: OS-based method selection, and the
/// risky "find the process by FULL path and wait for its exit" pattern used after an indirect launch
/// (the Win11 launcher.exe exits immediately). Uses a harmless copy of cmd.exe as a stand-in that
/// runs for ~5s, discarding the start handle so only path-matching can find it.
/// </summary>
static class LaunchSpike
{
    public static int Run()
    {
        Console.WriteLine("== launch spike: OS detection + find-by-path + wait-for-exit ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        var rel = WindowsInfo.Detect();
        Console.WriteLine($"  detected {rel} -> default method = {(rel.IsWindows11 ? "InjectedLauncher" : "DirectExe")}");
        Check("OS build looks sane (>= 10240)", rel.Build >= 10240);

        // --- method selection (pure logic; no real files needed beyond the injector pair existing) ---
        string tmp = Path.Combine(Path.GetTempPath(), "relic_launchspike_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        string gameDir = Path.Combine(tmp, "game");
        Directory.CreateDirectory(gameDir);
        string sleeper = Path.Combine(gameDir, "Sleeper.exe");
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        File.Copy(cmd, sleeper, overwrite: true);
        // fake GenshinImpact.exe + injector pair so ChooseMethod has real files to test CanInject
        File.Copy(cmd, Path.Combine(gameDir, "GenshinImpact.exe"), overwrite: true);
        string launcherExe = Path.Combine(tmp, "launcher.exe"); File.Copy(cmd, launcherExe, true);
        string dll = Path.Combine(tmp, "mhynot2.dll"); File.WriteAllText(dll, "stub");

        try
        {
            var withInjector = new GameInstall(gameDir, launcherExe, dll);
            var noInjector = new GameInstall(gameDir);
            Check("Win11/forceInjected + injector present -> InjectedLauncher",
                GameLauncher.ChooseMethod(withInjector, forceInjected: true) == LaunchMethod.InjectedLauncher);
            Check("forceInjected but injector missing -> falls back to DirectExe",
                GameLauncher.ChooseMethod(noInjector, forceInjected: true) == LaunchMethod.DirectExe);
            Check("forceInjected=false -> DirectExe regardless of injector",
                GameLauncher.ChooseMethod(withInjector, forceInjected: false) == LaunchMethod.DirectExe);

            // --- in-game enhancements: extra DLLs force the injector on Windows 10 too ---
            string eeDll = Path.Combine(tmp, "ee", "CLibrary.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(eeDll)!);
            File.WriteAllText(eeDll, "stub");
            var withExtras = new GameInstall(gameDir, launcherExe, dll) { ExtraDlls = new[] { eeDll } };
            Check("extra DLLs + pair present, forceInjected=false (Win10) -> InjectedLauncher",
                GameLauncher.ChooseMethod(withExtras, forceInjected: false) == LaunchMethod.InjectedLauncher);
            Check("NeedsInjector: the extras alone want the injector, the bare pair on Win10 does not",
                GameLauncher.NeedsInjector(withExtras, forceInjected: false)
                && !GameLauncher.NeedsInjector(withInjector, forceInjected: false)
                && GameLauncher.NeedsInjector(withInjector, forceInjected: true));
            var extrasNoPair = new GameInstall(gameDir) { ExtraDlls = new[] { eeDll } };
            Check("extra DLLs but no pair -> DirectExe (the mod is dropped, not the launch)",
                GameLauncher.ChooseMethod(extrasNoPair, forceInjected: false) == LaunchMethod.DirectExe);
            Check("InjectorMissing stays OS-only: false on Win10 even with extras wanted",
                !GameLauncher.InjectorMissing(extrasNoPair, forceInjected: false));
            Check("InjectorMissing: true on Win11 without the pair (unchanged)",
                GameLauncher.InjectorMissing(extrasNoPair, forceInjected: true));
            var lsi = GameLauncher.LauncherStartInfo(withExtras);
            Check("launcher argv = [gameDir, mhynot2.dll, extra...] — mhynot2 FIRST",
                lsi.ArgumentList.SequenceEqual(new[] { gameDir, dll, eeDll }));
            Check("launcher argv without extras is exactly the two-argv Win11 call",
                GameLauncher.LauncherStartInfo(withInjector).ArgumentList.SequenceEqual(new[] { gameDir, dll }));
            Check("launcher start info: no shell, no window, stdout captured, cwd = game dir",
                !lsi.UseShellExecute && lsi.CreateNoWindow && lsi.RedirectStandardOutput
                && lsi.WorkingDirectory == gameDir && lsi.FileName == launcherExe);

            // --- Enhancements.Resolve / ShippedFor against a synthetic payload root ---
            string eePayload = Path.Combine(tmp, "payload");
            string eeSrc = Path.Combine(eePayload, "2.8", "ee", "CLibrary.dll.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(eeSrc)!);
            File.WriteAllText(eeSrc, "EE-STUB");
            File.WriteAllText(Path.Combine(eePayload, "manifest.json"), """
            { "common": [], "versions": { "2.8": [
                { "src": "2.8/ee/CLibrary.dll.bin", "dst": "ayy/anime/ee/CLibrary.dll", "sha256": "00", "inject": true } ] } }
            """);
            string eeGameDir = Path.Combine(tmp, "eegame");
            string eeDst = Path.Combine(eeGameDir, "ayy", "anime", "ee", "CLibrary.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(eeDst)!);
            File.WriteAllText(eeDst, "EE-STUB");
            var r1 = Enhancements.Resolve("2.8", eeGameDir, enabled: true, payloadRoot: eePayload);
            Check("resolve: enabled + shipped + present -> 1 dll (absolute dst path), 0 missing, Shipped",
                r1.Enabled && r1.Shipped && r1.Dlls.Count == 1 && r1.Missing.Count == 0
                && string.Equals(r1.Dlls[0], eeDst, StringComparison.OrdinalIgnoreCase));
            File.Delete(eeDst);
            var r2 = Enhancements.Resolve("2.8", eeGameDir, true, eePayload);
            Check("resolve: DLL gone from the game dir (antivirus) -> 0 dlls, 1 missing, still Shipped",
                r2.Enabled && r2.Shipped && r2.Dlls.Count == 0 && r2.Missing.Count == 1);
            var r3 = Enhancements.Resolve("2.8", eeGameDir, false, eePayload);
            Check("resolve: disabled -> Enabled false, both lists empty",
                !r3.Enabled && r3.Dlls.Count == 0 && r3.Missing.Count == 0);
            Check("ShippedFor: 2.8 yes, 1.6 no",
                Enhancements.ShippedFor("2.8", eePayload) && !Enhancements.ShippedFor("1.6", eePayload));
            string noPayload = Path.Combine(tmp, "nopayload"); Directory.CreateDirectory(noPayload);
            Check("absent manifest -> no throw, nothing shipped, nothing to inject",
                !Enhancements.ShippedFor("2.8", noPayload)
                && Enhancements.Resolve("2.8", eeGameDir, true, noPayload) is { Shipped: false, Dlls.Count: 0, Missing.Count: 0 });

            // --- launcher.exe exit != 0: the direct fallback happens ONLY when the injector was
            // wanted for the extra DLLs alone. Stand-ins: where.exe exits non-zero at once whether it
            // plays "launcher.exe" (unknown patterns) or "GenshinImpact.exe" (no arguments) — nothing
            // lingers, and no real client is ever started.
            string fbDir = Path.Combine(tmp, "fallback"); Directory.CreateDirectory(fbDir);
            string where = Path.Combine(Environment.SystemDirectory, "where.exe");
            string fbGame = Path.Combine(fbDir, "GenshinImpact.exe"); File.Copy(where, fbGame, true);
            string fbLauncher = Path.Combine(fbDir, "launcher.exe"); File.Copy(where, fbLauncher, true);
            string fbDll = Path.Combine(fbDir, "mhynot2.dll"); File.WriteAllText(fbDll, "stub");
            var fbInstall = new GameInstall(fbDir, fbLauncher, fbDll) { ExtraDlls = new[] { eeDll } };
            var hWin10 = GameLauncher.Launch(fbInstall, forceInjected: false);
            Check("launcher exit != 0, injector wanted for the extras only -> fell back to DirectExe with a handle",
                hWin10.Method == LaunchMethod.DirectExe && hWin10.Direct is not null);
            hWin10.Direct?.WaitForExit(3000); hWin10.Direct?.Dispose();
            var hWin11 = GameLauncher.Launch(fbInstall, forceInjected: true);
            Check("launcher exit != 0 on Win11 -> stays InjectedLauncher, no direct start (as before)",
                hWin11.Method == LaunchMethod.InjectedLauncher && hWin11.Direct is null);

            // --- indirect launch + find-by-path + wait-for-exit ---
            var psi = new System.Diagnostics.ProcessStartInfo(sleeper)
            {
                Arguments = "/c ping -n 6 127.0.0.1 >nul", // ~5s, then exits
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var started = System.Diagnostics.Process.Start(psi)!;
            int realPid = started.Id;
            started.Dispose(); // discard the handle: only path-matching may find it now

            int? foundPid = GameProcessWatcher.FindByPath("Sleeper", sleeper);
            Check("find-by-path locates the running stand-in", foundPid == realPid);

            string decoy = Path.Combine(tmp, "elsewhere", "Sleeper.exe");
            Check("find-by-path rejects a different path (discrimination)",
                GameProcessWatcher.FindByPath("Sleeper", decoy) is null);

            long t0 = Environment.TickCount64;
            bool appeared = GameProcessWatcher
                .WaitForAppearThenExitAsync("Sleeper", sleeper, TimeSpan.FromSeconds(15))
                .GetAwaiter().GetResult();
            long elapsed = Environment.TickCount64 - t0;
            Check("wait returned after appear+exit", appeared);
            Check($"actually waited for exit (~5s, got {elapsed}ms)", elapsed is >= 3000 and <= 20000);
            Check("process is gone after wait", GameProcessWatcher.FindByPath("Sleeper", sleeper) is null);

            // --- post-exit decision table ---
            // ours running -> keep Fiddler, defer restore; only foreign -> stop Fiddler, defer restore;
            // nothing -> stop + restore; unattributable counts as ours.
            var d1 = Relic.Core.Play.PlaySession.PostExitDecision(sawOurs: true, sawForeign: true, sawUnknown: false);
            Check("decision: own client running -> keep Fiddler + defer restore", !d1.StopFiddler && !d1.RestoreProfile);
            var d2 = Relic.Core.Play.PlaySession.PostExitDecision(sawOurs: false, sawForeign: true, sawUnknown: false);
            Check("decision: foreign client only -> STOP Fiddler + defer restore", d2.StopFiddler && !d2.RestoreProfile);
            var d3 = Relic.Core.Play.PlaySession.PostExitDecision(sawOurs: false, sawForeign: false, sawUnknown: false);
            Check("decision: nothing running -> stop Fiddler + restore live", d3.StopFiddler && d3.RestoreProfile);
            var d4 = Relic.Core.Play.PlaySession.PostExitDecision(sawOurs: false, sawForeign: false, sawUnknown: true);
            Check("decision: unattributable client -> conservative (keep Fiddler, defer)", !d4.StopFiddler && !d4.RestoreProfile);

            // --- Classify(): ours vs foreign by game-dir prefix on real processes ---
            // GenshinImpact.exe is the only name Classify looks at, so stand-ins must carry that name.
            string oursDir = Path.Combine(tmp, "ours"); Directory.CreateDirectory(oursDir);
            string foreignDir = Path.Combine(tmp, "foreign"); Directory.CreateDirectory(foreignDir);
            string oursExe = Path.Combine(oursDir, "GenshinImpact.exe"); File.Copy(cmd, oursExe, true);
            string foreignExe = Path.Combine(foreignDir, "GenshinImpact.exe"); File.Copy(cmd, foreignExe, true);
            var baseline = ProcessGuard.Classify(new[] { oursDir });
            var pOurs = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(oursExe) { Arguments = "/c ping -n 4 127.0.0.1 >nul", UseShellExecute = false, CreateNoWindow = true })!;
            var pForeign = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(foreignExe) { Arguments = "/c ping -n 4 127.0.0.1 >nul", UseShellExecute = false, CreateNoWindow = true })!;
            try
            {
                Thread.Sleep(400);
                var c = ProcessGuard.Classify(new[] { oursDir });
                Check($"classify: one ours + one foreign (got ours={c.Ours - baseline.Ours} foreign={c.Foreign - baseline.Foreign})",
                    c.Ours - baseline.Ours == 1 && c.Foreign - baseline.Foreign == 1);
                var cNone = ProcessGuard.Classify(new[] { Path.Combine(tmp, "elsewhere") });
                Check("classify: no registered dir matches -> both foreign", cNone.Ours - baseline.Ours == 0 && cNone.Foreign - baseline.Foreign == 2);
                var settle = ProcessGuard.ObserveSustainedAsync(new[] { oursDir }, null, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                Check("observe: returns early with sawOurs while our stand-in runs", settle.SawOurs);
            }
            finally
            {
                try { pOurs.Kill(); } catch { }
                try { pForeign.Kill(); } catch { }
                pOurs.WaitForExit(3000); pForeign.WaitForExit(3000);
                pOurs.Dispose(); pForeign.Dispose();
            }
            Thread.Sleep(300);
            var after = ProcessGuard.ObserveSustainedAsync(new[] { oursDir }, null, TimeSpan.FromMilliseconds(800)).GetAwaiter().GetResult();
            Check("observe: after both exit, a short window sees nothing (unless a real client runs)",
                !after.SawOurs && (!after.SawForeign || baseline.Foreign > 0));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"launch spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }
}

/// <summary>
/// Server-address mechanics without network: the address parser (host[:port], [v6]:port, scheme
/// noise), the TXT-record key/value parser, the DoH TXT answer parser, and the baked default-server
/// list decoding. Everything a player types on the start screen goes through these.
/// </summary>
static class ServerSpike
{
    public static int Run()
    {
        Console.WriteLine("== server spike: address parsing + TXT discovery + baked defaults ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        // --- ServerAddress.TryParse ---
        Check("host only", ServerAddress.TryParse("game.example.com", out var a1, out _) && a1!.Host == "game.example.com" && a1.Port is null);
        Check("host:port", ServerAddress.TryParse("game.example.com:21000", out var a2, out _) && a2!.Host == "game.example.com" && a2.Port == 21000);
        Check("ipv4:port", ServerAddress.TryParse("192.0.2.10:2100", out var a3, out _) && a3!.Host == "192.0.2.10" && a3.Port == 2100 && a3.IsIpLiteral);
        Check("[ipv6]:port", ServerAddress.TryParse("[2001:db8::1]:21000", out var a4, out _) && a4!.Host == "2001:db8::1" && a4.Port == 21000 && a4.HostForUrl == "[2001:db8::1]");
        Check("bare ipv6 literal (no port)", ServerAddress.TryParse("2001:db8::1", out var a5, out _) && a5!.Host == "2001:db8::1" && a5.Port is null);
        Check("scheme + trailing slash stripped", ServerAddress.TryParse("http://game.example.com:21000/", out var a6, out _) && a6!.Host == "game.example.com" && a6.Port == 21000);
        Check("whitespace trimmed", ServerAddress.TryParse("  game.example.com  ", out var a7, out _) && a7!.Host == "game.example.com");
        Check("empty rejected", !ServerAddress.TryParse("   ", out _, out var e1) && e1 == "empty");
        Check("bad port rejected", !ServerAddress.TryParse("game.example.com:99999", out _, out _));
        Check("bad port (text) rejected", !ServerAddress.TryParse("game.example.com:abc", out _, out _));
        Check("space inside host rejected", !ServerAddress.TryParse("game example.com", out _, out _));
        Check("unclosed bracket rejected", !ServerAddress.TryParse("[2001:db8::1", out _, out _));
        Check("ToString round-trips host:port", a2!.ToString() == "game.example.com:21000" && a1!.ToString() == "game.example.com");
        var ipRes = a3!.ResolveAsync().GetAwaiter().GetResult();
        Check("resolve: explicit port -> explicit source, default agent port", ipRes.Port == 2100 && ipRes.AgentPort == ServerAddress.DefaultAgentPort && ipRes.Source == ServerAddress.SourceExplicit);
        var ipOnly = ServerAddress.TryParse("192.0.2.10", out var a8, out _) ? a8!.ResolveAsync().GetAwaiter().GetResult() : default;
        Check("resolve: IP literal without port -> defaults, no TXT", ipOnly.Port == ServerAddress.DefaultGamePort && ipOnly.Source == ServerAddress.SourceDefault);

        // --- DnsTxt parsing ---
        var kv = DnsTxt.ParseKv("v=relic1 port=21000 agent=18080");
        Check("kv: three tokens", kv["v"] == "relic1" && kv["port"] == "21000" && kv["agent"] == "18080");
        var kv2 = DnsTxt.ParseKv("PORT=21001; Agent=\"18081\" junk novalue=");
        Check("kv: case-insensitive keys, ; separator, quotes stripped, junk ignored", kv2["port"] == "21001" && kv2["agent"] == "18081" && !kv2.ContainsKey("junk") && kv2["novalue"] == "");
        var pick = DnsTxt.PickRelic(new[] { "google-site-verification=abc", "v=relic1 port=21000 agent=18080", "v=relic1 port=1" });
        Check("pick: first v=relic1 record wins", pick is { Port: 21000, AgentPort: 18080 });
        var pick2 = DnsTxt.PickRelic(new[] { "spf1 -all", "port=22000" });
        Check("pick: falls back to a record with port= when no v=relic1", pick2 is { Port: 22000, AgentPort: null });
        Check("pick: nothing usable -> null", DnsTxt.PickRelic(new[] { "v=spf1 -all", "port=70000" }) is null);
        Check("unquote: joins quoted character-strings and unescapes", DnsTxt.UnquoteTxt("\"v=relic1 \" \"port=21000 \\\"x\\\"\"") == "v=relic1 port=21000 \"x\"");
        Check("unquote: plain text passes through", DnsTxt.UnquoteTxt("port=21000") == "port=21000");
        var doh = DnsTxt.ParseDohTxt("{\"Status\":0,\"Answer\":[{\"name\":\"_relic.x.\",\"type\":16,\"TTL\":300,\"data\":\"\\\"v=relic1 port=21000\\\"\"},{\"name\":\"x\",\"type\":1,\"data\":\"192.0.2.1\"}]}");
        Check("doh: TXT records extracted, A records ignored", doh is { Count: 1 } && doh[0] == "v=relic1 port=21000");
        Check("doh: no Answer -> null", DnsTxt.ParseDohTxt("{\"Status\":3}") is null);

        // --- baked default servers (base64 JSON) ---
        string json = "[{\"label\":\"Demo\",\"host\":\"game.example.com:21000\"},{\"label\":\"\",\"host\":\"192.0.2.10\"},{\"label\":\"x;y=z\",\"host\":\"[2001:db8::1]:21000\"}]";
        var servers = BuildServers.Decode(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
        Check("defaults: three entries decoded", servers.Count == 3);
        Check("defaults: label/host preserved incl. ';' and '='", servers[2].Label == "x;y=z" && servers[2].Host == "[2001:db8::1]:21000");
        Check("defaults: blank label -> host as label", servers[1].Label == "192.0.2.10");
        Check("defaults: invalid base64 -> empty", BuildServers.Decode("not base64!").Count == 0);
        Check("defaults: empty -> empty", BuildServers.Decode("").Count == 0);

        // --- ServerConfig.Effective: the CONFIGURED host always wins over the host inside server.json
        // (otherwise changing the address on the start screen would change only the label) ---
        bool baked = !string.IsNullOrWhiteSpace(BuildDefaults.AgentToken);
        var storedDirect = new ServerConfig("old.example.com", 22, "root", "", "tokOLD", 18090, "direct");
        var storedSsh = new ServerConfig("Box.Example.com", 2222, "admin", "pw", "tokSSH", 18080, "ssh");
        var sameHost = ServerConfig.Effective(storedDirect, "OLD.example.com.", 18091);
        Check("effective: same host -> stored token, host as configured, agent port from settings",
            sameHost is { AgentToken: "tokOLD", Host: "OLD.example.com.", AgentPort: 18091, Mode: "direct" });
        var otherHost = ServerConfig.Effective(storedDirect, "new.example.com", 18080);
        Check("effective: other host -> stored token NOT reused" + (baked ? " (baked token applies)" : " (no token -> null)"),
            baked ? otherHost is { AgentToken: var bt, Host: "new.example.com" } && bt == BuildDefaults.AgentToken : otherHost is null);
        Check("effective: blank host -> null", ServerConfig.Effective(storedDirect, "  ", 18080) is null);
        var sshEff = ServerConfig.Effective(storedSsh, "box.example.com", 18085);
        Check("effective: ssh override keeps its SSH details + remote agent port",
            sshEff is { Mode: "ssh", User: "admin", Port: 2222, Password: "pw", AgentPort: 18080, AgentToken: "tokSSH", Host: "box.example.com" });
        Check("effective: no file -> baked token or null",
            baked ? ServerConfig.Effective(null, "any.example.com", 18080) is { AgentToken: var t2 } && t2 == BuildDefaults.AgentToken
                  : ServerConfig.Effective(null, "any.example.com", 18080) is null);
        Check("sameHost: case/trailing-dot insensitive, null-safe",
            ServerConfig.SameHost("Game.Example.COM.", "game.example.com") && !ServerConfig.SameHost("a.example.com", "b.example.com") && !ServerConfig.SameHost(null, ""));
        Check("tokenScope: stored host / baked 'any' / empty",
            (baked ? ServerConfig.TokenScope(storedDirect) == "any" : ServerConfig.TokenScope(storedDirect) == "old.example.com")
            && (baked || ServerConfig.TokenScope(null) == ""));

        // --- the open key: the bearer the agent hands out while its admin lets everyone administer, saved
        // like a token but flagged — the flag has to survive the one place that rebuilds the record, must
        // never be lent to another host or to the baked token, and is no saved token for the start screen ---
        var storedOpen = new ServerConfig("open.example.com", 22, "root", "", "keyOPEN", 18085, "direct", OpenKey: true);
        var openEff = ServerConfig.Effective(storedOpen, "Open.Example.com", 18085);
        Check("effective: an open-key record for the same host keeps its flag (and its key as the bearer)",
            openEff is { AgentToken: "keyOPEN", OpenKey: true, Mode: "direct", Host: "Open.Example.com", AgentPort: 18085 });
        Check("effective: a token record is never flagged", sameHost is { OpenKey: false } && (sshEff is null || !sshEff.OpenKey));
        Check("effective: another host -> the open key is NOT reused" + (baked ? " (the baked token applies, unflagged)" : " (no token -> null)"),
            ServerConfig.Effective(storedOpen, "other.example.com", 18080) is null or { OpenKey: false });
        Check("effective: a flagged record without a bearer lends no key" + (baked ? " (the baked token applies, unflagged)" : " (no token -> null)"),
            ServerConfig.Effective(storedOpen with { AgentToken = "" }, "open.example.com", 18085) is null or { OpenKey: false });
        Check("tokenScope: an open-key record is no saved token for the start screen" + (baked ? " (baked: 'any' regardless)" : ""),
            ServerConfig.TokenScope(storedOpen) == (baked ? "any" : ""));
        // The record is what ServerConfigStore writes and reads (System.Text.Json, no DPAPI needed to prove it):
        // a file saved without the field holds a token, like a file without Mode holds a tunnel.
        var legacyRecord = JsonSerializer.Deserialize<ServerConfig>(
            """{"Host":"old.example.com","Port":22,"User":"root","Password":"","AgentToken":"tokOLD","AgentPort":18090,"Mode":"direct"}""");
        Check("record: a server.json without OpenKey reads as a token (false), the rest as written",
            legacyRecord is { OpenKey: false, AgentToken: "tokOLD", Mode: "direct", AgentPort: 18090, Host: "old.example.com" });
        var roundTrip = JsonSerializer.Deserialize<ServerConfig>(JsonSerializer.SerializeToUtf8Bytes(storedOpen));
        Check("record: OpenKey survives the Save/Load round trip",
            roundTrip is { OpenKey: true, AgentToken: "keyOPEN", Host: "open.example.com", AgentPort: 18085 }
            && JsonSerializer.Deserialize<ServerConfig>(JsonSerializer.SerializeToUtf8Bytes(storedDirect)) is { OpenKey: false });


        // ── agent-port provenance (Resolved.AgentPortFromTxt) ──
        // The decision it feeds: "keep the configured agent port unless the resolution actually LEARNED
        // one". Inferring that from AgentPort != 18080 would silently discard a TXT record publishing
        // agent=18080, so the provenance has to be carried, not guessed.
        Check("resolve: explicit game port learns NO agent port", ipRes.AgentPortFromTxt is null);
        Check("resolve: IP literal learns NO agent port", ipOnly.AgentPortFromTxt is null);
        Check("resolve: default(Resolved) has no provenance", default(ServerAddress.Resolved).AgentPortFromTxt is null);
        Check("txt: a record publishing agent=18080 is distinguishable from none",
            DnsTxt.PickRelic(new[] { "v=relic1 port=21000 agent=18080" }) is { AgentPort: 18080 }
            && DnsTxt.PickRelic(new[] { "v=relic1 port=21000" }) is { AgentPort: null });

        // ── install plan: the config the box actually receives ──
        AgentInstallPlan Plan() => new()
        {
            Target = AgentInstallPlan.TargetLinux, Dir16 = "/home/raf/1.6_live",
            Listen = "0.0.0.0:18080", Token = new string('a', 32), Region = "dev_docker",
        };
        string Line(string cfg, string key) => cfg.Split('\n').FirstOrDefault(l => l.StartsWith(key + "=")) ?? "";

        var okPlan = Plan();
        Check("plan: a clean plan validates", okPlan.Validate().Count == 0);
        Check("plan: the token is written BYTE-FOR-BYTE (no silent rewrite)",
            Line(okPlan.RenderConfig(), "GIO_AGENT_TOKEN") == "GIO_AGENT_TOKEN=" + okPlan.Token);
        Check("plan: a blank stack dir is still emitted, SET AND EMPTY",
            okPlan.ToEnvironment().TryGetValue("GIO_DIR_28", out var d28) && d28 == "");
        Check("plan: provision/txt-fix modes are OMITTED when blank (the box keeps its own)",
            !okPlan.ToEnvironment().ContainsKey("GIO_PROVISION_MODE")
            && !okPlan.ToEnvironment().ContainsKey("GIO_TXT_FIXES_MODE"));

        var named = Plan(); named.ServerName = "Raf's \"test\" box";
        string namedCfg = named.RenderConfig();
        Check("plan: an apostrophe in the server name is stripped, not left to span lines",
            Line(namedCfg, "GIO_SERVER_NAME") == "GIO_SERVER_NAME=Rafs test box");
        Check("plan: every other key survives the quoted server name",
            namedCfg.Contains("GIO_STATE_PATH=") || namedCfg.Contains("GIO_AGENT_REGION=dev_docker"));

        // DDNS host: a plain DNS name, never an IP, never together with an advertised IP.
        var hosted = Plan(); hosted.AdvertisedHost = "game.example.com";
        Check("plan: a DDNS host validates and is written as GIO_ADVERTISED_HOST",
            hosted.Validate().Count == 0 && Line(hosted.RenderConfig(), "GIO_ADVERTISED_HOST") == "GIO_ADVERTISED_HOST=game.example.com");
        Check("plan: no DDNS host is still emitted, SET AND EMPTY (the installer keeps the box's stored one)",
            okPlan.ToEnvironment().TryGetValue("GIO_ADVERTISED_HOST", out var advHost) && advHost == "");
        foreach (var badHost in new[] { "198.51.100.171", "2001:db8::1", "game example.com", "game.example.com/x", "http://game.example.com", "a'b.example.com" })
        {
            var p3 = Plan(); p3.AdvertisedHost = badHost;
            Check($"plan: DDNS host \"{badHost}\" is refused", p3.Validate().Count > 0);
        }
        var bothAdv = Plan(); bothAdv.AdvertisedHost = "game.example.com"; bothAdv.AdvertisedIp = "198.51.100.171";
        Check("plan: an advertised IP AND a DDNS host together are refused (the agent would ignore the IP)",
            bothAdv.Validate().Count > 0);

        var quotedToken = Plan(); quotedToken.Token = "abc'def" + new string('a', 20);
        Check("plan: a quoted token is REFUSED (stripping it would 401 against our own agent)",
            quotedToken.Validate().Count > 0);
        var quotedDir = Plan(); quotedDir.Dir16 = "/home/raf/it's_live";
        Check("plan: a quoted stack dir is REFUSED", quotedDir.Validate().Count > 0);

        // Listen must spell out host AND port: the agent's parse_listen reads a bare "18080" as
        // 127.0.0.1:18080, so it would bind loopback while Relic saved a direct config for the LAN host.
        foreach (var bad in new[] { "18080", "0.0.0.0", ":18080" })
        {
            var p2 = Plan(); p2.Listen = bad;
            Check($"plan: listen \"{bad}\" is refused (agent would bind loopback)", p2.Validate().Count > 0);
        }
        Check("plan: probe authority for a wildcard bind dials loopback", Plan().ProbeAuthority == "127.0.0.1:18080");
        var specific = Plan(); specific.Listen = "192.0.2.10:19000";
        Check("plan: probe authority for a specific bind dials that address",
            specific.ProbeAuthority == "192.0.2.10:19000" && specific.ListenPort == 19000);

        // ── stack download marks: a version marked "download it" is not checked on disk, and
        // the mark travels to the INSTALLER only (GIO_FETCH_*) — never into the agent config, or an
        // upgrade run would re-mark a version that is long present. ──
        var fetch = Plan(); fetch.Fetch16 = true;
        Check("plan: a fetch-marked version validates", fetch.Validate().Count == 0);
        Check("plan: the mark travels as GIO_FETCH_16=y in the installer environment (and only for the marked version)",
            fetch.ToEnvironment().TryGetValue("GIO_FETCH_16", out var f16) && f16 == "y" && !fetch.ToEnvironment().ContainsKey("GIO_FETCH_28"));
        Check("plan: GIO_FETCH_* never reaches the agent config", !fetch.RenderConfig().Contains("GIO_FETCH_"));
        Check("plan: a fetch-marked version is skipped by the stack check (its folder is expected absent)",
            fetch.MissingStackEntries(_ => false).Count == 0);
        Check("plan: FetchVersions names the marked version", fetch.FetchVersions().SequenceEqual(new[] { "1.6" }));
        fetch.Dir28 = "/home/2.8_live";
        var missingUnmarked = fetch.MissingStackEntries(_ => false);
        Check("plan: an unmarked version is still checked", missingUnmarked.Count == 1 && missingUnmarked[0] == "2.8: /home/2.8_live");
        var fetchNoDir = Plan(); fetchNoDir.Fetch28 = true; fetchNoDir.Dir28 = "";
        Check("plan: a fetch mark without a folder is refused (fetchNeedsDir)",
            fetchNoDir.Validate().Contains(L.T("core.deploy.fetchNeedsDir", new { version = "2.8" })));
        Check("plan: a fetch mark without a folder is not a fetch version and emits no GIO_FETCH_ key",
            fetchNoDir.FetchVersions().Count == 0 && !fetchNoDir.ToEnvironment().ContainsKey("GIO_FETCH_28"));
        Check("plan: no mark = no GIO_FETCH_ key at all", !okPlan.ToEnvironment().Keys.Any(k => k.StartsWith("GIO_FETCH_", StringComparison.Ordinal)));

        // ── MUIP sign key: written byte-for-byte, SET AND EMPTY when blank (the installer keeps
        // the box's stored one; the agent picks a random key), refused when the agent would ignore it. ──
        var keyed = Plan(); keyed.MuipKey = "Chosen_Key-12345";
        Check("plan: a MUIP key validates and is written as GIO_MUIP_KEY byte-for-byte",
            keyed.Validate().Count == 0 && Line(keyed.RenderConfig(), "GIO_MUIP_KEY") == "GIO_MUIP_KEY=Chosen_Key-12345");
        Check("plan: no MUIP key is still emitted, SET AND EMPTY",
            okPlan.ToEnvironment().TryGetValue("GIO_MUIP_KEY", out var mk) && mk == "");
        Check("plan: GIO_MUIP_KEY follows GIO_AGENT_REGION in the config (the installers' heredoc order)",
            keyed.RenderConfig().IndexOf("GIO_AGENT_REGION=", StringComparison.Ordinal) < keyed.RenderConfig().IndexOf("GIO_MUIP_KEY=", StringComparison.Ordinal));
        foreach (var badKey in new[] { "short1", "has space 12345", "quote'key12345", "back\\slash12345", new string('a', 129) })
        {
            var pk = Plan(); pk.MuipKey = badKey;
            Check($"plan: MUIP key \"{(badKey.Length > 20 ? badKey[..20] + "…" : badKey)}\" is refused (the agent's SECRET_VALUE_RE would drop it)",
                pk.Validate().Contains(L.T("core.deploy.badMuipKey")));
        }
        var longKey = Plan(); longKey.MuipKey = new string('x', 128);
        Check("plan: a 128-character MUIP key is accepted", longKey.Validate().Count == 0);

        // ── the two stack secrets: the MySQL root password and the Flask secret key. Written
        // byte-for-byte when set and — unlike GIO_MUIP_KEY — OMITTED when blank, so the box keeps the value it
        // stores on both targets (install_agent.sh for a key its environment does not name,
        // LocalAgent.MergeConfig for a key the form does not render); refused with ONE fixed text each, so a
        // message can never quote the value. ──
        var secret = Plan(); secret.MysqlPassword = "Root_Pw-12345"; secret.FlaskKey = "Flask_Key-12345";
        string secretCfg = secret.RenderConfig();
        Check("plan: a MySQL root password and a Flask key validate and are written byte-for-byte",
            secret.Validate().Count == 0
            && Line(secretCfg, "GIO_MYSQL_ROOT_PASSWORD") == "GIO_MYSQL_ROOT_PASSWORD=Root_Pw-12345"
            && Line(secretCfg, "GIO_FLASK_SECRET_KEY") == "GIO_FLASK_SECRET_KEY=Flask_Key-12345");
        var blankSecrets = Plan(); blankSecrets.MysqlPassword = "  "; blankSecrets.FlaskKey = "\t";
        Check("plan: blank (or only blanks) = both keys OMITTED from the config and the installer environment (the box keeps its stored values)",
            new[] { okPlan, blankSecrets }.All(bp => bp.Validate().Count == 0
                && !bp.RenderConfig().Contains("GIO_MYSQL_ROOT_PASSWORD") && !bp.RenderConfig().Contains("GIO_FLASK_SECRET_KEY")
                && !bp.ToEnvironment().ContainsKey("GIO_MYSQL_ROOT_PASSWORD") && !bp.ToEnvironment().ContainsKey("GIO_FLASK_SECRET_KEY")));
        Check("plan: a set value reaches install_agent.sh through the installer environment",
            secret.ToEnvironment().TryGetValue("GIO_MYSQL_ROOT_PASSWORD", out var envRoot) && envRoot == "Root_Pw-12345"
            && secret.ToEnvironment().TryGetValue("GIO_FLASK_SECRET_KEY", out var envFlask) && envFlask == "Flask_Key-12345");
        Check("plan: GIO_MYSQL_ROOT_PASSWORD, then GIO_FLASK_SECRET_KEY, come right after GIO_MUIP_KEY (the installers' heredoc order)",
            secretCfg.Split('\n').Select(cl => cl.Split('=')[0]).SkipWhile(ck => ck != "GIO_MUIP_KEY").Take(3)
                .SequenceEqual(new[] { "GIO_MUIP_KEY", "GIO_MYSQL_ROOT_PASSWORD", "GIO_FLASK_SECRET_KEY" }));
        var padded = Plan(); padded.MysqlPassword = "  Root_Pw-12345 "; padded.FlaskKey = " Flask_Key-12345\n";
        Check("plan: surrounding blanks (a pasted line end too) are trimmed, never written",
            padded.Validate().Count == 0 && padded.RenderConfig() == secretCfg);
        Check("plan: the two refusals have their own, different en.json texts, without a placeholder",
            L.Has("core.deploy.badMysqlPassword") && L.Has("core.deploy.badFlaskKey")
            && L.T("core.deploy.badMysqlPassword") != L.T("core.deploy.badFlaskKey")
            && !L.T("core.deploy.badMysqlPassword").Contains('{') && !L.T("core.deploy.badFlaskKey").Contains('{'));
        foreach (var badSecret in new[] { "short1", "has space 12345", "quote'key12345", "back\\slash12345", new string('a', 129) })
        {
            var badRoot = Plan(); badRoot.MysqlPassword = badSecret;
            var badFlask = Plan(); badFlask.FlaskKey = badSecret;
            Check($"plan: \"{(badSecret.Length > 20 ? badSecret[..20] + "…" : badSecret)}\" is refused as a MySQL root password and as a Flask key, each with exactly its ONE fixed text (the value is never quoted)",
                badRoot.Validate().SequenceEqual(new[] { L.T("core.deploy.badMysqlPassword") })
                && badFlask.Validate().SequenceEqual(new[] { L.T("core.deploy.badFlaskKey") }));
        }
        // The root password has its own, stricter rule: the vendor stack renders it UNQUOTED into
        // docker-compose.yml, where a value that starts with a digit or '-' is read as a number or a date
        // ("01234567" → 342391) — the first character must be a letter. The Flask key lands in quoted JSON
        // and keeps the general rule.
        foreach (var notLetterFirst in new[] { "01234567", "2026-10-01", "-abcdefgh", "_abcdefgh" })
        {
            var badRoot = Plan(); badRoot.MysqlPassword = notLetterFirst;
            var okFlask = Plan(); okFlask.FlaskKey = notLetterFirst;
            Check($"plan: \"{notLetterFirst}\" is refused as a MySQL root password (it must start with a letter) and accepted as a Flask key",
                badRoot.Validate().SequenceEqual(new[] { L.T("core.deploy.badMysqlPassword") }) && okFlask.Validate().Count == 0);
        }
        var edgeLong = Plan(); edgeLong.MysqlPassword = "a" + new string('9', 127); edgeLong.FlaskKey = new string('9', 128);
        var edgeShort = Plan(); edgeShort.MysqlPassword = "a2345678"; edgeShort.FlaskKey = "12345678";
        Check("plan: the bounds are accepted for both — 128 and 8 characters", edgeLong.Validate().Count == 0 && edgeShort.Validate().Count == 0);

        // ── "Keep .env settings" (GIO_KEEP_ENV), a TRI-STATE of the plan. ON = the line GIO_KEEP_ENV=1, and
        // the bind IP + the three configured secrets left out ENTIRELY: the agent applies none of them while
        // the option is on, and a key that is not sent is one the box keeps on both targets
        // (install_agent.sh for a key its environment lacks, LocalAgent.MergeConfig for a key the form does
        // not render) — written empty, GIO_BIND_IP and GIO_MUIP_KEY would replace the stored line on
        // Windows. OFF = GIO_KEEP_ENV=0 and everything else as without the option. NOT STATED = no line at
        // all, so the box keeps its stored choice. ──
        AgentInstallPlan Stated(bool? keepEnv)
        {
            var stated = Plan();
            stated.BindIp = "192.0.2.10"; stated.MuipKey = "Chosen_Key-12345";
            stated.MysqlPassword = "Root_Pw-12345"; stated.FlaskKey = "Flask_Key-12345";
            stated.KeepEnv = keepEnv;
            return stated;
        }
        string[] withheld = { "GIO_BIND_IP", "GIO_MUIP_KEY", "GIO_MYSQL_ROOT_PASSWORD", "GIO_FLASK_SECRET_KEY" };
        string unstatedCfg = Stated(null).RenderConfig(), offCfg = Stated(false).RenderConfig(), onCfg = Stated(true).RenderConfig();
        Check("plan: Keep .env settings not stated (the default) = no GIO_KEEP_ENV line, the bind IP and the three secrets written",
            okPlan.KeepEnv is null && !okPlan.RenderConfig().Contains("GIO_KEEP_ENV") && !unstatedCfg.Contains("GIO_KEEP_ENV")
            && Line(unstatedCfg, "GIO_BIND_IP") == "GIO_BIND_IP=192.0.2.10" && Line(unstatedCfg, "GIO_MUIP_KEY") == "GIO_MUIP_KEY=Chosen_Key-12345"
            && Line(unstatedCfg, "GIO_MYSQL_ROOT_PASSWORD") == "GIO_MYSQL_ROOT_PASSWORD=Root_Pw-12345"
            && Line(unstatedCfg, "GIO_FLASK_SECRET_KEY") == "GIO_FLASK_SECRET_KEY=Flask_Key-12345");
        Check("plan: Keep .env settings off = the line GIO_KEEP_ENV=0, every other line exactly as when it is not stated",
            Line(offCfg, "GIO_KEEP_ENV") == "GIO_KEEP_ENV=0" && offCfg.Replace("GIO_KEEP_ENV=0\n", "") == unstatedCfg);
        Check("plan: Keep .env settings on = the line GIO_KEEP_ENV=1, and GIO_BIND_IP, GIO_MUIP_KEY, GIO_MYSQL_ROOT_PASSWORD and GIO_FLASK_SECRET_KEY left out ENTIRELY — typed values included",
            Line(onCfg, "GIO_KEEP_ENV") == "GIO_KEEP_ENV=1" && withheld.All(wk => !onCfg.Contains(wk)));
        Check("plan: Keep .env settings on changes no other line (the token, the listen address, the folders, the advertised address, the MUIP host are still sent)",
            onCfg.Replace("GIO_KEEP_ENV=1\n", "") == string.Concat(unstatedCfg.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(cl => !withheld.Any(wk => cl.StartsWith(wk + "=", StringComparison.Ordinal))).Select(cl => cl + "\n")));
        var onBlank = Plan(); onBlank.KeepEnv = true;
        Check("plan: Keep .env settings on with blank fields — GIO_BIND_IP and GIO_MUIP_KEY are not even written SET AND EMPTY",
            onBlank.Validate().Count == 0 && withheld.All(wk => !onBlank.RenderConfig().Contains(wk)));
        var onEnv = Stated(true).ToEnvironment(); var offEnv = Stated(false).ToEnvironment(); var unstatedEnv = Stated(null).ToEnvironment();
        Check("plan: the installer environment follows — GIO_KEEP_ENV=1 without the four keys, GIO_KEEP_ENV=0 with them, no GIO_KEEP_ENV when not stated",
            onEnv.TryGetValue("GIO_KEEP_ENV", out var envKeepOn) && envKeepOn == "1" && withheld.All(wk => !onEnv.ContainsKey(wk))
            && offEnv.TryGetValue("GIO_KEEP_ENV", out var envKeepOff) && envKeepOff == "0" && withheld.All(offEnv.ContainsKey)
            && !unstatedEnv.ContainsKey("GIO_KEEP_ENV") && withheld.All(unstatedEnv.ContainsKey));
        Check("plan: with Keep .env settings on the installer environment is the unstated one minus the four keys plus GIO_KEEP_ENV — nothing else moves",
            onEnv.Count == unstatedEnv.Count - withheld.Length + 1
            && unstatedEnv.Where(ue => !withheld.Contains(ue.Key)).All(ue => onEnv.TryGetValue(ue.Key, out var sent) && sent == ue.Value));
        // A field that is not sent cannot refuse the plan: with the option on the form hides the four fields,
        // and a value left behind in one of them is not part of what gets installed. Everything that IS sent
        // is still checked.
        AgentInstallPlan Leftover(bool? keepEnv)
        {
            var left = Plan();
            left.BindIp = "192.0.2"; left.MuipKey = "short1"; left.MysqlPassword = "01234567"; left.FlaskKey = "has space 12345";
            left.KeepEnv = keepEnv;
            return left;
        }
        Check("plan: with Keep .env settings on, a bad value left in one of the four fields never refuses the plan (it is not sent); off or not stated each is refused",
            Leftover(true).Validate().Count == 0 && withheld.All(wk => !Leftover(true).RenderConfig().Contains(wk))
            && new bool?[] { false, null }.All(choice => Leftover(choice).Validate().SequenceEqual(new[]
            {
                L.T("core.deploy.badBindIp", new { ip = "192.0.2" }), L.T("core.deploy.badMuipKey"),
                L.T("core.deploy.badMysqlPassword"), L.T("core.deploy.badFlaskKey"),
            })));
        var onShortToken = Stated(true); onShortToken.Token = "short";
        Check("plan: Keep .env settings on still validates what is sent (a short token is refused)",
            onShortToken.Validate().SequenceEqual(new[] { L.T("core.deploy.tokenTooShort") }));

        // ── what the SSH install keeps off the admin's screen and out of relic.log (AgentDeployer) ──
        // The secrets a plan carries are blanked by VALUE, longest first. A line that assigns one of the
        // secret keys is kept out of the live log, and blanked in the failure tail, whatever its value: one
        // the box already stores (its form field left blank) is not known to the launcher.
        var leaky = Plan(); leaky.MuipKey = "abcdefgh"; leaky.MysqlPassword = " abcdefgh1234 "; leaky.FlaskKey = "Flask_Key-12345";
        string[] installSecrets = AgentDeployer.SecretsOf(leaky);
        Check("deploy: the redaction list is every non-empty secret of the plan, trimmed, LONGEST first",
            installSecrets.SequenceEqual(new[] { leaky.Token, "Flask_Key-12345", "abcdefgh1234", "abcdefgh" })
            && AgentDeployer.SecretsOf(okPlan).SequenceEqual(new[] { okPlan.Token }));
        // The list is what install.env carries, nothing more. With "Keep .env settings" on the three are not
        // sent — and not validated: LeaksSecret hides every line that CONTAINS a listed value, so a short
        // leftover in a field the form hides would silence most of the install log.
        var unsent = Stated(true); unsent.MuipKey = "a"; unsent.FlaskKey = "e";
        Check("deploy: with Keep .env settings on the redaction list is the token alone — a value that is not sent can never hide an installer line",
            AgentDeployer.SecretsOf(Stated(true)).SequenceEqual(new[] { okPlan.Token })
            && unsent.Validate().Count == 0 && AgentDeployer.SecretsOf(unsent).SequenceEqual(new[] { okPlan.Token })
            && !AgentDeployer.LeaksSecret("  1.6 dir : /home/raf/1.6_live", AgentDeployer.SecretsOf(unsent))
            && AgentDeployer.SecretsOf(Stated(false)).Length == 4);
        Check("deploy: a secret that begins a longer one never leaves the longer one's tail readable",
            AgentDeployer.Redact("root abcdefgh1234 / muip abcdefgh / flask Flask_Key-12345", installSecrets) == "root *** / muip *** / flask ***");
        string[] assigning =
        {
            "GIO_AGENT_TOKEN=Stored_Token-1234567890", "  kept    : GIO_MUIP_KEY=Stored_Key-12345 (not managed by this script)",
            "GIO_MYSQL_ROOT_PASSWORD=Stored_Root-123", "declare -x GIO_FLASK_SECRET_KEY=\"Stored_Flask-123\"",
        };
        Check("deploy: a line that assigns a secret key never reaches the live log, and its value is blanked in the failure tail — a value the plan does not know too",
            assigning.All(al => AgentDeployer.LeaksSecret(al, installSecrets))
            && AgentDeployer.Redact(string.Join("\r\n", assigning), installSecrets) == string.Join("\r\n",
                "GIO_AGENT_TOKEN=***", "  kept    : GIO_MUIP_KEY=***", "GIO_MYSQL_ROOT_PASSWORD=***", "declare -x GIO_FLASK_SECRET_KEY=***"));
        // The first sample is the summary line install_agent.sh prints when an upgrade run replaces the
        // box's token — the one warning that every other launcher of that server has to be given the new
        // token. It names the token twice and carries no value, so it has to come through both filters as
        // it is: the "Agent token" / "generated token:" rules are for the two lines that DO print one. A
        // copy of the script's text, held to the script itself further down.
        const string tokenChangesLine = "  token   : CHANGES -- every launcher that uses the old token must be given the new one";
        Check("deploy: the installer's summary and its refusal (no value, a fingerprint at most) still reach the admin, untouched",
            new[]
            {
                tokenChangesLine,
                "  MUIP key: chosen (fingerprint 0a1b2c3d)",
                "  MySQL pw: set in the config -- used when a stack still on the vendor's default is prepared",
                "  Flask   : random at the next Prepare server",
                "  .env    : kept as it is -- the agent replaces none of the vendor's published passwords and does not rewrite OUTER_IP (GIO_KEEP_ENV=1)",
                "  MySQL pw: the stack's own password is kept (the one in the config is not applied)",
                "  kept    : HTTPS_PROXY (not managed by this script; value unchanged)",
                "GIO_MYSQL_ROOT_PASSWORD must be 8-128 characters: letters, digits, '_' or '-', starting with a letter -- fix it in /etc/gio-agent/config (or the environment) and run this again.",
            }.All(sl => !AgentDeployer.LeaksSecret(sl, installSecrets) && AgentDeployer.Redact(sl, installSecrets) == sl));

        // ── the agent config on THIS PC, parsed like the agent parses it (LocalAgent.ReadConfig), and the
        // uninstall on a temp root — never the developer's real %LOCALAPPDATA%\Relic\agent. ──
        string tmpRoot = Path.Combine(Path.GetTempPath(), "relic-server-spike-" + Guid.NewGuid().ToString("n")[..8]);
        const int spikePort = 18977;   // nothing listens here: Stop(port) must find nothing to kill
        bool autostartBefore = LocalAgent.AutostartEnabled;
        LocalAgent.RootOverride = tmpRoot;
        try
        {
            Directory.CreateDirectory(tmpRoot);
            Check("localagent: RootOverride redirects every path", LocalAgent.ConfigPath == Path.Combine(tmpRoot, "config") && LocalAgent.Root == tmpRoot);
            Check("localagent: no config file -> ReadConfig() is null", LocalAgent.ReadConfig() is null);
            File.WriteAllText(LocalAgent.ConfigPath,
                "\uFEFF# Relic GIO agent configuration\r\nGIO_AGENT_TOKEN=\"abcdefghijklmnopqrstuvwxyz012345\"\r\nGIO_AGENT_LISTEN=0.0.0.0:18099\r\n"
                + "  GIO_BIND_IP = 192.0.2.10 \r\nGIO_AGENT_TOKEN=other\r\nGIO_DIR_16='C:\\stacks\\1.6_live'\r\nnot a pair\r\n1BAD=x\r\nGIO_DIR_28=\r\n",
                new UTF8Encoding(true));
            var lc = LocalAgent.ReadConfig();
            Check("localagent: BOM + CRLF tolerated, quotes stripped, FIRST occurrence wins (the agent's apply_config_file)",
                lc is not null && lc.Token == "abcdefghijklmnopqrstuvwxyz012345");
            Check("localagent: listen port parsed; a wildcard bind is dialled on the bind IP",
                lc is { ListenPort: 18099, ListenHost: "0.0.0.0", Host: "192.0.2.10", BindIp: "192.0.2.10" });
            Check("localagent: blanks around '=' tolerated, single quotes stripped, non-pairs and bad keys dropped",
                lc is not null && lc.Dir16 == "C:\\stacks\\1.6_live" && lc.Dir28 == "" && !lc.All.ContainsKey("1BAD") && lc.All.Count == 5);
            File.WriteAllText(LocalAgent.ConfigPath, "GIO_AGENT_LISTEN=127.0.0.1:18099\nGIO_BIND_IP=192.0.2.10\n");
            Check("localagent: a loopback bind is dialled on 127.0.0.1, never the bind IP the agent did not bind",
                LocalAgent.ReadConfig()!.Host == "127.0.0.1");
            File.WriteAllText(LocalAgent.ConfigPath, "GIO_AGENT_LISTEN=192.0.2.10:18099\nGIO_BIND_IP=192.0.2.10\n");
            Check("localagent: a bind on the bind IP itself is dialled there", LocalAgent.ReadConfig()!.Host == "192.0.2.10");
            File.WriteAllText(LocalAgent.ConfigPath, "GIO_AGENT_LISTEN=0.0.0.0:18099\n");
            Check("localagent: no bind IP -> loopback", LocalAgent.ReadConfig()!.Host == "127.0.0.1");
            File.WriteAllText(LocalAgent.ConfigPath, "GIO_AGENT_TOKEN=x\n");
            Check("localagent: no listen line -> the agent's own default (127.0.0.1:18080)",
                LocalAgent.ReadConfig() is { ListenPort: 18080, ListenHost: "127.0.0.1", Host: "127.0.0.1" });
            Check("localagent: HostFor is the one rule both the install and 'enter' apply",
                LocalAgentConfig.HostFor("192.0.2.10", "::") == "192.0.2.10" && LocalAgentConfig.HostFor("192.0.2.10", "127.0.0.1") == "127.0.0.1"
                && LocalAgentConfig.HostFor("", "0.0.0.0") == "127.0.0.1" && LocalAgentConfig.HostFor("192.0.2.10", "192.0.2.11") == "127.0.0.1");
            // With "Keep .env settings" on the agent applies no bind IP, so the address the stacks publish
            // their ports on — the one the game redirect aims at, saved as this agent's host — is each
            // stack's own OUTER_IP, read from <stack>\.env by the agent's read_env rule. The stored
            // GIO_BIND_IP stands in only while the stacks give nothing to go by: no .env yet, two stacks that
            // disagree, an address that is not this PC's. With the option off no .env is read at all. The
            // stacks are folders of the temp root; 192.0.2.x is no address of any PC.
            string envStack16 = Path.Combine(tmpRoot, "stacks", "1.6_live"), envStack28 = Path.Combine(tmpRoot, "stacks", "2.8_live");
            Directory.CreateDirectory(envStack16);
            Directory.CreateDirectory(envStack28);
            void StackEnv(string stack, string? text)
            {
                string envPath = Path.Combine(stack, ".env");
                if (text is null) File.Delete(envPath); else File.WriteAllText(envPath, text, new UTF8Encoding(false));
            }
            string HostWith(bool? keepEnv)
            {
                File.WriteAllText(LocalAgent.ConfigPath, $"GIO_AGENT_LISTEN=0.0.0.0:18099\nGIO_BIND_IP=192.0.2.10\nGIO_DIR_16={envStack16}\nGIO_DIR_28={envStack28}\n"
                    + (keepEnv is { } stated ? $"GIO_KEEP_ENV={(stated ? 1 : 0)}\n" : ""));
                return LocalAgent.ReadConfig()!.Host;
            }
            Check("localagent: Keep .env settings on, no stack has a .env yet -> the stored GIO_BIND_IP", HostWith(true) == "192.0.2.10");
            StackEnv(envStack16, "OUTER_IP=127.0.0.1\n");
            Check("localagent: Keep .env settings on + a stack whose .env publishes on 127.0.0.1 + a stored GIO_BIND_IP -> 127.0.0.1 (the agent does not apply the bind IP)",
                HostWith(true) == "127.0.0.1");
            Check("localagent: Keep .env settings off, or not in the config -> the stored GIO_BIND_IP, whatever the stacks' .env hold",
                HostWith(false) == "192.0.2.10" && HostWith(null) == "192.0.2.10");
            StackEnv(envStack28, "OUTER_IP=localhost\n");
            Check("localagent: Keep .env settings on, both stacks on loopback (127.0.0.1 and localhost) -> 127.0.0.1", HostWith(true) == "127.0.0.1");
            StackEnv(envStack28, "OUTER_IP=192.0.2.123\n");
            Check("localagent: Keep .env settings on, the two stacks disagree -> the stored GIO_BIND_IP", HostWith(true) == "192.0.2.10");
            StackEnv(envStack16, null);
            Check("localagent: Keep .env settings on, the only OUTER_IP is no address of this PC -> the stored GIO_BIND_IP", HostWith(true) == "192.0.2.10");
            // read_env is not the config rule: any key, the LAST line of a repeated key, both kinds of quotes,
            // a BOM, CRLF and a lone CR as line ends, undecodable bytes skipped.
            StackEnv(envStack28, null);
            StackEnv(envStack16, "\uFEFF" + "FIRST=1\r\n# Server external IP\r\nOUTER_IP=192.0.2.123\r\nnot a pair\r\n1ST.KEY = 'v 1' \r\n\r\n"
                + "  OUTER_IP = \"127.0.0.1\" \r\n#OUTER_IP=192.0.2.124\rLAST=x");
            var envPairs = LocalAgent.ReadStackEnv(envStack16);
            Check("localagent: a stack's .env is read by the agent's read_env rule — the LAST line of a key wins, quotes and blanks stripped, BOM / CRLF / lone CR tolerated, comments and non-pairs skipped, any key kept",
                envPairs.Count == 4 && envPairs["FIRST"] == "1" && envPairs["OUTER_IP"] == "127.0.0.1" && envPairs["1ST.KEY"] == "v 1" && envPairs["LAST"] == "x"
                && LocalAgent.StackOuterIp(envStack16) == "127.0.0.1" && HostWith(true) == "127.0.0.1");
            File.WriteAllBytes(Path.Combine(envStack16, ".env"), [.. "OUTER_IP=127.0"u8, 0xFF, .. ".0.1\n"u8]);
            Check("localagent: a byte that is not UTF-8 is skipped, as the agent reads the file (errors=\"ignore\")",
                LocalAgent.StackOuterIp(envStack16) == "127.0.0.1");
            Check("localagent: no folder name, a folder that is not there, a folder without .env -> no OUTER_IP, never a throw",
                LocalAgent.StackOuterIp("") == "" && LocalAgent.StackOuterIp(null) == "" && LocalAgent.StackOuterIp(Path.Combine(tmpRoot, "stacks", "absent")) == ""
                && LocalAgent.StackOuterIp(envStack28) == "" && LocalAgent.ReadStackEnv(envStack28).Count == 0);
            // The rule itself, with a PC that owns 192.0.2.50 standing in for the real socket test.
            string? Agreed(params string?[] outerIps) => LocalAgentConfig.AgreedOuterIp(outerIps, ip => ip == "192.0.2.50");
            Check("rule: the stacks' OUTER_IP — a blank one has no say, every loopback spelling is 127.0.0.1, another address counts when this PC can bind it",
                Agreed("", "") is null && Agreed() is null && Agreed("127.0.0.1", "") == "127.0.0.1" && Agreed("localhost", "127.0.0.9") == "127.0.0.1"
                && Agreed("", " 192.0.2.50 ") == "192.0.2.50" && Agreed("192.0.2.50", "192.0.2.50") == "192.0.2.50");
            Check("rule: the stacks' OUTER_IP — no answer when they disagree, for an address this PC cannot bind, and for 0.0.0.0 (every address of this PC, none to dial)",
                Agreed("127.0.0.1", "192.0.2.50") is null && Agreed("192.0.2.50", "192.0.2.51") is null && Agreed("192.0.2.51") is null
                && LocalAgentConfig.AgreedOuterIp(new[] { "0.0.0.0" }, _ => true) is null);
            Check("rule: HostFor still decides on top of it — the stacks' address only when the agent answers there, else loopback",
                LocalAgentConfig.HostFor(Agreed("192.0.2.50")!, "0.0.0.0") == "192.0.2.50" && LocalAgentConfig.HostFor(Agreed("192.0.2.50")!, "127.0.0.1") == "127.0.0.1");
            Check("localagent: CanBind — this PC binds 127.0.0.1; not an address it does not have, not a name, a shorthand, an IPv6 address or nothing",
                LocalAgent.CanBind("127.0.0.1") && !LocalAgent.CanBind("192.0.2.123") && !LocalAgent.CanBind("localhost")
                && !LocalAgent.CanBind("192.0.2") && !LocalAgent.CanBind("::1") && !LocalAgent.CanBind("") && !LocalAgent.CanBind(null));
            // "Keep .env settings" as the installed config holds it (what localagent.status hands the
            // re-install form's toggle): the agent's own rule for a switch that is off by default — on for
            // 1 / true / yes / on, trimmed and in any letter case, off for everything else and for a file
            // without the key. A toggle shown on the wrong side would flip the stored choice at the next
            // install, since the form writes the key as 1 or 0.
            bool KeepEnvOf(string? keepLine)
            {
                File.WriteAllText(LocalAgent.ConfigPath, "GIO_AGENT_TOKEN=x\n" + (keepLine is null ? "" : keepLine + "\n"));
                return LocalAgent.ReadConfig()!.KeepEnv;
            }
            Check("localagent: GIO_KEEP_ENV reads ON for 1 / true / yes / on — trimmed, any letter case, quoted or not",
                new[] { "GIO_KEEP_ENV=1", "GIO_KEEP_ENV=true", "GIO_KEEP_ENV=YES", "GIO_KEEP_ENV = On ", "GIO_KEEP_ENV=\"1\"", "GIO_KEEP_ENV=' True '" }
                    .All(KeepEnvOf));
            Check("localagent: GIO_KEEP_ENV reads OFF for 0, an empty value and any other word — and for a config without the key",
                new[] { "GIO_KEEP_ENV=0", "GIO_KEEP_ENV=", "GIO_KEEP_ENV=false", "GIO_KEEP_ENV=no", "GIO_KEEP_ENV=off", "GIO_KEEP_ENV=2", "GIO_KEEP_ENV=y", "GIO_KEEP_ENV=enabled" }
                    .All(keepLine => !KeepEnvOf(keepLine))
                && !KeepEnvOf(null) && !new LocalAgentConfig(new Dictionary<string, string>()).KeepEnv);
            Check("localagent: the FIRST GIO_KEEP_ENV line wins, like every repeated key",
                KeepEnvOf("GIO_KEEP_ENV=1\nGIO_KEEP_ENV=0") && !KeepEnvOf("GIO_KEEP_ENV=0\nGIO_KEEP_ENV=1"));
            var winPlan = Plan(); winPlan.Target = AgentInstallPlan.TargetWindows; winPlan.Dir16 = @"C:\stacks\1.6_live";
            winPlan.ServerName = "spike box"; winPlan.BindIp = "192.0.2.10"; winPlan.MuipKey = "Chosen_Key-12345";
            File.WriteAllText(LocalAgent.ConfigPath, winPlan.RenderConfig(), new UTF8Encoding(false));
            var rc = LocalAgent.ReadConfig();
            Check("localagent: RenderConfig -> ReadConfig round-trips the token byte-for-byte, the port, the folders and the MUIP key",
                rc is not null && rc.Token == winPlan.Token && rc.ListenPort == 18080 && rc.Dir16 == @"C:\stacks\1.6_live" && rc.Dir28 == ""
                && rc.ServerName == "spike box" && rc.Host == "192.0.2.10" && rc.All["GIO_MUIP_KEY"] == "Chosen_Key-12345");
            // What localagent.status hands the re-install form: the form writes GIO_BIND_IP and GIO_MUIP_HOST even
            // blank and MergeConfig lets those win, so both must read back RAW — the bind IP even when Host is
            // loopback (a loopback listen), the MUIP host a card may have saved.
            var loopPlan = Plan(); loopPlan.Target = AgentInstallPlan.TargetWindows; loopPlan.Listen = "127.0.0.1:18080";
            loopPlan.BindIp = "192.0.2.20"; loopPlan.MuipHost = "http://192.0.2.5:21051";
            File.WriteAllText(LocalAgent.ConfigPath, loopPlan.RenderConfig(), new UTF8Encoding(false));
            var lpc = LocalAgent.ReadConfig();
            Check("localagent: the raw bind IP and the MUIP host read back for the re-install prefill (Host stays loopback on a loopback listen)",
                lpc is { Host: "127.0.0.1", BindIp: "192.0.2.20", MuipHost: "http://192.0.2.5:21051" });
            Check("localagent: a blank MUIP host is written SET AND EMPTY and reads back as \"\"",
                rc is not null && rc.MuipHost == "" && rc.All.ContainsKey("GIO_MUIP_HOST"));

            // ── a Windows re-install keeps the admin's settings: LocalAgent.MergeConfig ──
            // The form renders only the keys it manages; everything else in the old file (the Agent settings
            // card, hand edits: GIO_FETCH_DISK_RESERVE, HTTPS_PROXY …) must come back once, below one comment.
            string rendered = winPlan.RenderConfig();
            string H = LocalAgent.KeptConfigHeader;
            Check("merge: no previous file (null / empty) -> the rendered config unchanged, no comment",
                LocalAgent.MergeConfig(rendered, null) == rendered && LocalAgent.MergeConfig(rendered, "") == rendered);
            string prevCfg = "\uFEFF# my notes\r\nGIO_DIR_16=D:\\old\\1.6_live\r\nGIO_FETCH_DISK_RESERVE=4096\r\n"
                + "HTTPS_PROXY=\"http://192.0.2.5:3128\"\r\n\r\n  GIO_TLS_PINNED_FALLBACK = 0  \r\nGIO_FETCH_DISK_RESERVE=1\r\n"
                + "not a pair\r\n1BAD=x\r\nGIO_AGENT_TOKEN=oldtoken-oldtoken-old\r\nGIO_PROVISION_MODE=never\r\nGIO_DIR_28=D:\\old\\2.8_live\r\n";
            string merged = LocalAgent.MergeConfig(rendered, prevCfg, out var keptKeys);
            Check("merge: rendered text first, then ONE comment line, then the unmanaged keys in the old file's order, as spelled there (trimmed)",
                merged == rendered + H + "\n" + "GIO_FETCH_DISK_RESERVE=4096\n" + "HTTPS_PROXY=\"http://192.0.2.5:3128\"\n"
                    + "GIO_TLS_PINNED_FALLBACK = 0\n" + "GIO_PROVISION_MODE=never\n");
            Check("merge: the kept key names are reported (first occurrence only, duplicates collapsed)",
                keptKeys.SequenceEqual(new[] { "GIO_FETCH_DISK_RESERVE", "HTTPS_PROXY", "GIO_TLS_PINNED_FALLBACK", "GIO_PROVISION_MODE" }));
            Check("merge: a managed key never comes back with its old value — even one the form renders SET AND EMPTY",
                !merged.Contains("oldtoken") && !merged.Contains(@"D:\old\") && merged.Split('\n').Count(l => l.StartsWith("GIO_DIR_28=")) == 1);
            Check("merge: no BOM, no CR, the old comments / blank lines / non-pairs / bad keys dropped, the comment once",
                !merged.Contains('\uFEFF') && !merged.Contains('\r') && !merged.Contains("my notes") && !merged.Contains("not a pair")
                && !merged.Contains("1BAD") && merged.Split('\n').Count(l => l == H) == 1);
            Check("merge: idempotent — merging onto its own output adds nothing (the old comment is not kept twice)",
                LocalAgent.MergeConfig(rendered, merged) == merged);
            Check("merge: an old file holding only managed keys and comments keeps nothing and adds NO comment",
                LocalAgent.MergeConfig(rendered, "# c\nGIO_AGENT_TOKEN=x\nGIO_DIR_16=y\n\n", out var none) == rendered && none.Count == 0);
            Check("merge: a rendered text without a final newline still gets the comment on a line of its own",
                LocalAgent.MergeConfig("GIO_DIR_16=a", "HTTPS_PROXY=b") == "GIO_DIR_16=a\n" + H + "\nHTTPS_PROXY=b\n");
            File.WriteAllText(LocalAgent.ConfigPath, merged, new UTF8Encoding(false));
            var mc = LocalAgent.ReadConfig();
            Check("merge: the merged file reads back with the NEW managed values and the kept keys (quotes stripped by the parser)",
                mc is not null && mc.Token == winPlan.Token && mc.Dir16 == @"C:\stacks\1.6_live" && mc.Dir28 == ""
                && mc.All["GIO_FETCH_DISK_RESERVE"] == "4096" && mc.All["HTTPS_PROXY"] == "http://192.0.2.5:3128"
                && mc.All["GIO_TLS_PINNED_FALLBACK"] == "0" && mc.All["GIO_PROVISION_MODE"] == "never");
            // The two stack secrets on a re-install: the form OMITS a blank one, so the line an
            // earlier install wrote is a key the rendered text does not name — kept, and reported by NAME
            // only. Rendered SET AND EMPTY like GIO_MUIP_KEY they would fall under the rule a few checks up
            // (a managed key never comes back), and a re-install would quietly drop the chosen password.
            var firstInstall = Plan(); firstInstall.Target = AgentInstallPlan.TargetWindows; firstInstall.Dir16 = winPlan.Dir16;
            firstInstall.ServerName = winPlan.ServerName; firstInstall.BindIp = winPlan.BindIp; firstInstall.MuipKey = winPlan.MuipKey;
            firstInstall.MysqlPassword = "Stored_Root-123"; firstInstall.FlaskKey = "Stored_Flask-123";
            string keptSecrets = LocalAgent.MergeConfig(rendered, firstInstall.RenderConfig(), out var keptSecretKeys);
            Check("merge: a blank re-install form keeps the stored MySQL root password and Flask key, reported by name only",
                keptSecrets == rendered + H + "\nGIO_MYSQL_ROOT_PASSWORD=Stored_Root-123\nGIO_FLASK_SECRET_KEY=Stored_Flask-123\n"
                && keptSecretKeys.SequenceEqual(new[] { "GIO_MYSQL_ROOT_PASSWORD", "GIO_FLASK_SECRET_KEY" })
                && LocalAgent.MergeConfig(rendered, keptSecrets) == keptSecrets);
            var typedPlan = Plan(); typedPlan.Target = AgentInstallPlan.TargetWindows; typedPlan.Dir16 = winPlan.Dir16;
            typedPlan.MysqlPassword = "Typed_Root-456";
            string typedMerged = LocalAgent.MergeConfig(typedPlan.RenderConfig(), keptSecrets, out var keptTyped);
            Check("merge: a typed value replaces the stored line (one line, the new value) while the other secret is still kept",
                typedMerged.Split('\n').Count(l => l.StartsWith("GIO_MYSQL_ROOT_PASSWORD=")) == 1
                && typedMerged.Contains("GIO_MYSQL_ROOT_PASSWORD=Typed_Root-456\n") && !typedMerged.Contains("Stored_Root-123")
                && typedMerged.Contains("GIO_FLASK_SECRET_KEY=Stored_Flask-123\n") && keptTyped.SequenceEqual(new[] { "GIO_FLASK_SECRET_KEY" }));
            // "Keep .env settings" on a re-install. NOT STATED, the form does not render GIO_KEEP_ENV and the
            // stored line is kept like any other key the rendered text does not name; STATED, the line is the
            // form's and the stored one never comes back behind it. And while it is ON the form renders
            // neither the bind IP nor the three secrets — whatever its fields hold —, so the lines an earlier
            // install wrote for them stay in the file as they are: the agent applies none of them while the
            // option is on, and the launcher does not get to drop them.
            AgentInstallPlan WinStated(bool? keepEnv)
            {
                var ws = Plan(); ws.Target = AgentInstallPlan.TargetWindows; ws.Dir16 = winPlan.Dir16; ws.ServerName = winPlan.ServerName;
                ws.BindIp = "192.0.2.99"; ws.MuipKey = "Typed_Key-67890"; ws.MysqlPassword = "Typed_Root-456"; ws.FlaskKey = "Typed_Flask-456";
                ws.KeepEnv = keepEnv;
                return ws;
            }
            string storedChoice = LocalAgent.MergeConfig(rendered, rendered + "GIO_KEEP_ENV=1\n", out var keptChoice);
            Check("merge: a form that does not state Keep .env settings keeps the stored GIO_KEEP_ENV line",
                storedChoice == rendered + H + "\nGIO_KEEP_ENV=1\n" && keptChoice.SequenceEqual(new[] { "GIO_KEEP_ENV" })
                && LocalAgent.MergeConfig(rendered, storedChoice) == storedChoice);
            string offRendered = WinStated(false).RenderConfig();
            string offMerged = LocalAgent.MergeConfig(offRendered, storedChoice, out var keptOff);
            Check("merge: a form that states it replaces the stored line — ONE GIO_KEEP_ENV line, the form's value, nothing kept",
                offMerged == offRendered && keptOff.Count == 0
                && offMerged.Split('\n').Count(l => l.StartsWith("GIO_KEEP_ENV=")) == 1 && offMerged.Contains("GIO_KEEP_ENV=0\n"));
            string onRendered = WinStated(true).RenderConfig();
            string onMerged = LocalAgent.MergeConfig(onRendered, firstInstall.RenderConfig(), out var keptOn);
            Check("merge: Keep .env settings on keeps the stored bind IP, MUIP key, MySQL root password and Flask key (the form renders none of them, typed or not), reported by name only",
                onMerged == onRendered + H + "\nGIO_BIND_IP=192.0.2.10\nGIO_MUIP_KEY=Chosen_Key-12345\n"
                    + "GIO_MYSQL_ROOT_PASSWORD=Stored_Root-123\nGIO_FLASK_SECRET_KEY=Stored_Flask-123\n"
                && keptOn.SequenceEqual(new[] { "GIO_BIND_IP", "GIO_MUIP_KEY", "GIO_MYSQL_ROOT_PASSWORD", "GIO_FLASK_SECRET_KEY" })
                && !onMerged.Contains("192.0.2.99") && !onMerged.Contains("Typed_")
                && LocalAgent.MergeConfig(onRendered, onMerged) == onMerged);
            File.WriteAllText(LocalAgent.ConfigPath, onMerged, new UTF8Encoding(false));
            var keepCfg = LocalAgent.ReadConfig();
            Check("merge: that file reads back with the option ON and the STORED bind IP — what the host saved for this agent falls back on while no stack names an OUTER_IP, never the form's value",
                keepCfg is { KeepEnv: true, BindIp: "192.0.2.10", Host: "192.0.2.10" } && keepCfg.All["GIO_MUIP_KEY"] == "Chosen_Key-12345");
            Check("plan: the config header points at the Agent settings card",
                rendered.StartsWith("# Relic GIO agent configuration (KEY=VALUE).", StringComparison.Ordinal) && rendered.Split('\n')[0].Contains("Agent settings"));

            Check("localagent: FirewallRuleName is the literal OpenFirewallPort uses", LocalAgent.FirewallRuleName(18099) == "Relic GIO agent (18099)");
            // THE deadlock guard. The launcher answers rpcs on the WebView2 message thread, i.e. the UI
            // thread, and that thread carries a SynchronizationContext that runs continuations ONLY when
            // it pumps. LocalAgent.Stop blocks on IsHealthyAsync; with the await inside capturing that
            // context, the continuation would wait for a thread that is itself waiting for the continuation
            // — the launcher would freeze for good (a stop or an uninstall would never return,
            // and afterwards every screen waiting on an rpc, the command catalogue above all,
            // would hang on its spinner). Reproduced here with a single-threaded context that never pumps: the
            // call must finish anyway, i.e. it must not need that thread back.
            bool healthDone = false, healthPumped = false;
            var healthThread = new Thread(() =>
            {
                var ctx = new NeverPumpingContext(() => healthPumped = true);
                SynchronizationContext.SetSynchronizationContext(ctx);
                // a port nothing listens on: the probe fails fast, and the FAILURE path awaits too
                LocalAgent.IsHealthyAsync(59_997, 400).GetAwaiter().GetResult();
                healthDone = true;
            }) { IsBackground = true };
            healthThread.Start();
            healthThread.Join(TimeSpan.FromSeconds(15));
            Check("localagent: the health probe completes when blocked on from a UI-like thread (no deadlock)", healthDone);
            Check("localagent: ... and it never asked that blocked thread to run its continuation", !healthPumped);

            // The whole Stop path against a REAL process (never the developer's own agent: a throwaway
            // python listening on a spare port, its pid in the temp root's pid file). Stop finds it by
            // pid, kills the tree and then waits for the port to go quiet — all of it blocking, and all
            // of it driven here from a thread whose SynchronizationContext never pumps.
            int dummyPort = 59_996;
            System.Diagnostics.Process? dummy = null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("python",
                    $"-c \"import http.server,socketserver; socketserver.TCPServer(('127.0.0.1',{dummyPort}), http.server.SimpleHTTPRequestHandler).serve_forever()\"")
                { UseShellExecute = false, CreateNoWindow = true };
                dummy = System.Diagnostics.Process.Start(psi);
            }
            catch { /* no python on PATH: the case is skipped below */ }
            if (dummy is not null)
            {
                Thread.Sleep(700);   // let it bind
                File.WriteAllText(Path.Combine(tmpRoot, "agent.pid"), dummy.Id.ToString());
                bool stopDone = false, stopPumped = false;
                var stopThread = new Thread(() =>
                {
                    SynchronizationContext.SetSynchronizationContext(new NeverPumpingContext(() => stopPumped = true));
                    LocalAgent.Stop(dummyPort);
                    stopDone = true;
                }) { IsBackground = true };
                stopThread.Start();
                stopThread.Join(TimeSpan.FromSeconds(20));
                Check("localagent: Stop() finishes from a UI-like thread and kills the process it found", stopDone && dummy.HasExited);
                Check("localagent: ... without needing that thread to run a continuation", !stopPumped);
                try { if (!dummy.HasExited) dummy.Kill(true); } catch { }
                dummy.Dispose();
            }
            else Console.WriteLine("        (skipped: no python on PATH for the Stop-under-UI-context case)");

            // StartAsync leaves a healthy agent alone and hands back the pid it already had. A re-install
            // over a running agent relies on exactly that: it compares the pid with the one the pid file
            // named before the install, and says that the configuration it wrote applies only after that
            // agent is restarted. A throwaway python answers /health the way an agent does, its pid in the
            // temp root's pid file; nothing may be started beside it.
            int healthPort = 59_995;
            System.Diagnostics.Process? healthyAgent = null;
            try
            {
                var healthPsi = new System.Diagnostics.ProcessStartInfo("python",
                    "-c \"import http.server as h, socketserver as s; H = type('H', (h.BaseHTTPRequestHandler,), {"
                    + "'do_GET': lambda r: (r.send_response(200), r.send_header('Content-Length', '9'), r.end_headers(), r.wfile.write(b'gio-agent')), "
                    + $"'log_message': lambda *a: None}}); s.TCPServer(('127.0.0.1', {healthPort}), H).serve_forever()\"")
                { UseShellExecute = false, CreateNoWindow = true };
                healthyAgent = System.Diagnostics.Process.Start(healthPsi);
            }
            catch { /* no python on PATH: the case is skipped below */ }
            if (healthyAgent is not null)
            {
                try
                {
                    bool answers = false;
                    for (int i = 0; i < 40 && !answers && !healthyAgent.HasExited; i++) { Thread.Sleep(250); answers = LocalAgent.IsHealthyAsync(healthPort, 500).GetAwaiter().GetResult(); }
                    if (answers)
                    {
                        File.WriteAllText(LocalAgent.PidPath, healthyAgent.Id.ToString());
                        File.WriteAllText(LocalAgent.ScriptPath, "# stub");   // StartAsync refuses an agent that is not installed
                        var pidBefore = LocalAgent.RunningPid();
                        var startLines = new List<string>();
                        int handedBack = -1;
                        try { handedBack = LocalAgent.StartAsync(healthPort, startLines.Add).GetAwaiter().GetResult(); }
                        catch (Exception ex) { startLines.Add("threw: " + ex.Message); }
                        Check("localagent: StartAsync leaves a healthy agent alone — it hands back the pid the pid file already named and logs the one line \"already running\", nothing is started beside it",
                            pidBefore.Running && pidBefore.Pid == healthyAgent.Id && handedBack == pidBefore.Pid
                            && startLines.SequenceEqual(new[] { L.T("core.deploy.alreadyRunning") }) && !healthyAgent.HasExited);
                    }
                    else Console.WriteLine("        (skipped: the python on PATH did not serve /health for the StartAsync-over-a-running-agent case)");
                }
                finally
                {
                    try { if (!healthyAgent.HasExited) healthyAgent.Kill(true); healthyAgent.WaitForExit(5000); } catch { }
                    healthyAgent.Dispose();
                    try { File.Delete(LocalAgent.PidPath); File.Delete(LocalAgent.ScriptPath); } catch { }
                }
            }
            else Console.WriteLine("        (skipped: no python on PATH for the StartAsync-over-a-running-agent case)");

            // ── the build identity (AgentBuild): one sha256 over the agent script and the payload files that
            // came with it — the value the agent works out for itself (GET /status `build`). Both sides are
            // held to the same two fixed vectors (the agent's selftest has them too), so they cannot drift:
            // two answers for one agent would read as "update available" for ever. ──
            bool idSkipsOk = true;
            foreach (var (idRel, idSkipped) in new[]
            {
                ("1.6/manifest.json", false), ("2.8/txt/a.txt", false), ("2.8/txt/x.zip", false), ("stray.txt", false),
                ("1.6/sub/navmesh.json", false), ("1.6/navmesh/navmesh.json", true), ("1.6/navmesh/NavMesh/scene_3.navmesh", true),
                ("1.6/navmesh.zip", true), ("1.6/navmesh.zip.part", true), ("1.6/NAVMESH.ZIP", true),
                ("1.6/navmesh.relic-old-12-34/navmesh.json", true), ("1.6/navmeshes.txt", true), ("1.6/other.zip", true),
                ("1.6/Other.Zip.PART", true), ("archive.zip", true), ("archive.part", true), ("1.6/.relic-navmesh-zip-refused", true),
                ("1.6/.relic-navmesh-unpack/1.6/navmesh/navmesh.json", true), (".hidden", true), ("2.8/__pycache__/x.pyc", true),
                ("2.8/txt/.keep", true), ("", true),
            })
                if (AgentBuild.Skips(idRel) != idSkipped) { idSkipsOk = false; Console.WriteLine($"        identity: Skips(\"{idRel}\") is not {idSkipped}"); }
            Check("identity: what a box adds to its payloads folder is left out — a version's navmesh bundle, archives, dot-entries, __pycache__ (the agent's identity_skips, case by case)", idSkipsOk);
            string idDir = Path.Combine(tmpRoot, "identity");
            void IdPut(string rel, string text)
            {
                string p = Path.Combine(idDir, "payloads", rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, Encoding.ASCII.GetBytes(text));
            }
            Directory.CreateDirectory(idDir);
            byte[] idScript = Encoding.ASCII.GetBytes("print('agent')\n");
            File.WriteAllBytes(Path.Combine(idDir, "gio_agent.py"), idScript);
            string idScriptSha = Convert.ToHexString(SHA256.HashData(idScript)).ToLowerInvariant();
            string? idAlone = AgentBuild.Identity(idDir);
            Check("identity: no payloads folder — the script alone; a folder without the script — none",
                idAlone == AgentBuild.Digest(idScriptSha, Array.Empty<(string, string)>()) && AgentBuild.Identity(Path.Combine(idDir, "absent")) is null);
            IdPut("1.6/manifest.json", "{}\n");
            IdPut("2.8/txt/a.txt", "a\tb\r\n");
            string? idBase = AgentBuild.Identity(idDir);
            Check("identity: the fixed vector the agent's selftest holds too (the script and two payload files, their bytes as they are)",
                idBase == "05171038f1733a8dc16e8c26930e54c98ba6fb9e8493a529cc19204a81899149" && idBase != idAlone);
            var idV2 = new[] { (".txt", new string('1', 64)), ("\U0001F600.txt", new string('2', 64)), ("a/b.txt", new string('3', 64)) };
            Check("identity: the lines are sorted by the path's UTF-8 bytes, in whatever order the files come (the second shared vector)",
                AgentBuild.Digest(new string('0', 64), idV2) == "95eb3dec39190c3b824d39465af96c41fd0af87226cf28356c32dc8eefb25cb4"
                && AgentBuild.Digest(new string('0', 64), Enumerable.Reverse(idV2)) == AgentBuild.Digest(new string('0', 64), idV2));
            bool idBoxOk = true;
            foreach (var idExtra in new[]
            {
                "1.6/navmesh/navmesh.json", "1.6/navmesh/NavMesh/scene_3.navmesh", "1.6/navmesh.zip", "1.6/navmesh.zip.part",
                "1.6/.relic-navmesh-zip-refused", "1.6/.relic-navmesh-unpack/1.6/navmesh/navmesh.json",
                "1.6/navmesh.relic-old-12-34/navmesh.json", "archive.zip", "2.8/Other.ZIP", "2.8/__pycache__/x.pyc",
            })
            {
                IdPut(idExtra, "box");
                idBoxOk &= AgentBuild.Identity(idDir) == idBase;
            }
            Check("identity: an adopted navmesh bundle, its archive, the records of an adoption and other archives of the box change nothing", idBoxOk);
            string? idCachedBefore = AgentBuild.IdentityCached(idDir);
            IdPut("2.8/txt/a.txt", "a\tb\n");
            string? idByte = AgentBuild.Identity(idDir), idCachedAfter = AgentBuild.IdentityCached(idDir);
            IdPut("2.8/txt/a.txt", "a\tb\r\n");
            IdPut("2.8/txt/x.zip", "shipped");
            string? idDeep = AgentBuild.Identity(idDir);
            File.Delete(Path.Combine(idDir, "payloads", "2.8", "txt", "x.zip"));
            File.AppendAllText(Path.Combine(idDir, "gio_agent.py"), "#\n");
            string? idScriptChanged = AgentBuild.Identity(idDir);
            Check("identity: a payload byte, a file below a version's own entries and a script byte each change it; the remembered value follows a changed file",
                idByte != idBase && idDeep != idBase && idScriptChanged != idBase && idScriptChanged != idAlone
                && idCachedBefore == idBase && idCachedAfter == idByte);
            Check("identity: \"differs\" = not the shipped one — an agent that names none is not it, and without a shipped agent nothing differs",
                !AgentBuild.Differs("aa", "aa") && AgentBuild.Differs("aa", "ab") && AgentBuild.Differs("aa", null) && AgentBuild.Differs("aa", "")
                && !AgentBuild.Differs(null, "ab") && !AgentBuild.Differs("", "ab"));
            // The same question asked of the agent itself: `gio_agent.py --build-id` prints the identity of
            // its own script and payloads folder, and the launcher has to say the same of that folder.
            string idAgentPy = Path.Combine(AppContext.BaseDirectory, "agent", "gio_agent.py");
            if (!File.Exists(idAgentPy))
                idAgentPy = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "agent", "gio_agent.py"));
            var idPython = LocalAgent.FindPython();
            if (idPython is not null && File.Exists(idAgentPy))
            {
                string idAgentDir = Path.GetDirectoryName(idAgentPy)!, idSaid;
                try
                {
                    var idPsi = new System.Diagnostics.ProcessStartInfo(idPython.Exe, $"\"{idAgentPy}\" --build-id")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    idPsi.Environment["GIO_PAYLOAD_DIR"] = Path.Combine(idAgentDir, "payloads");
                    using var idProc = System.Diagnostics.Process.Start(idPsi)!;
                    idSaid = idProc.StandardOutput.ReadToEnd().Trim();
                    idProc.WaitForExit(60_000);
                }
                catch (Exception ex) { idSaid = "threw: " + ex.Message; }
                Check($"identity: the agent's own answer (gio_agent.py --build-id: {(idSaid.Length > 12 ? idSaid[..12] : idSaid)}…) is the launcher's for the same folder",
                    idSaid.Length == 64 && idSaid == AgentBuild.Identity(idAgentDir));
            }
            else Console.WriteLine("        (skipped: no python, or no agent script, for the --build-id cross-check)");
            try { Directory.Delete(idDir, true); } catch { /* the uninstall cases below delete the whole root */ }

            // ── what a box added to its payloads folder moves into the folder that replaces it
            // (LocalAgent.CarryBoxPayloads — the rule of carry_box_payloads in install_agent.sh) ──
            string cyRoot = Path.Combine(tmpRoot, "carry");
            void CyPut(string rel, string text = "x")
            {
                string p = Path.Combine(cyRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, text);
            }
            bool CyHas(string rel)
            {
                string p = Path.Combine(cyRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(p) || Directory.Exists(p);
            }
            string CyDir(string rel) => Path.Combine(cyRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            foreach (var cyRel in new[]
            {
                "a/old/2.8/navmesh/navmesh.json", "a/old/2.8/navmesh/.relic-navmesh-zip", "a/old/2.8/navmesh/NavMesh/a.navmesh",
                "a/old/2.8/navmesh.zip", "a/old/2.8/navmesh.zip.part", "a/old/2.8/.relic-navmesh-zip-refused",
                "a/old/2.8/.relic-navmesh-unpack/x", "a/old/2.8/navmesh.relic-old-1-2/navmesh.json", "a/old/2.8/Custom.ZIP",
                "a/old/2.8/my archive.zip", "a/old/2.8/__pycache__/x.pyc", "a/old/root.zip", "a/old/notes.txt", "a/old/9.9/navmesh/x",
            })
                CyPut(cyRel);
            CyPut("a/old/2.8/manifest.json", "old"); CyPut("a/old/1.6/manifest.json", "old");
            CyPut("a/new/2.8/manifest.json", "new"); CyPut("a/new/1.6/manifest.json", "new");
            var cyA = LocalAgent.CarryBoxPayloads(CyDir("a/old"), CyDir("a/new"));
            Check("carry: with no bundle shipped, a version keeps its navmesh folder, the adoption's archive, partial, refusal record and renamed-aside folder, and every other archive — the payloads folder's own too",
                cyA.Count == 8 && CyHas("a/new/2.8/navmesh/NavMesh/a.navmesh") && CyHas("a/new/2.8/navmesh/.relic-navmesh-zip")
                && CyHas("a/new/2.8/navmesh.zip") && CyHas("a/new/2.8/navmesh.zip.part") && CyHas("a/new/2.8/.relic-navmesh-zip-refused")
                && CyHas("a/new/2.8/navmesh.relic-old-1-2/navmesh.json") && CyHas("a/new/2.8/Custom.ZIP") && CyHas("a/new/2.8/my archive.zip")
                && CyHas("a/new/root.zip"));
            Check("carry: ... and nothing else moves — the unpack folder, __pycache__, a stray file, a version the new payloads do not have; the shipped files stay the new ones",
                !CyHas("a/new/2.8/.relic-navmesh-unpack") && !CyHas("a/new/2.8/__pycache__") && !CyHas("a/new/notes.txt") && !CyHas("a/new/9.9")
                && File.ReadAllText(CyDir("a/new/2.8/manifest.json")) == "new" && File.ReadAllText(CyDir("a/new/1.6/manifest.json")) == "new"
                && !CyHas("a/old/2.8/navmesh") && !CyHas("a/old/root.zip"));
            CyPut("b/old/2.8/navmesh/navmesh.json", "box"); CyPut("b/old/2.8/navmesh.zip"); CyPut("b/old/2.8/.relic-navmesh-zip-refused");
            CyPut("b/old/1.6/navmesh.zip"); CyPut("b/old/root.zip");
            CyPut("b/new/2.8/manifest.json"); CyPut("b/new/2.8/navmesh/navmesh.json", "shipped"); CyPut("b/new/1.6/manifest.json");
            var cyB = LocalAgent.CarryBoxPayloads(CyDir("b/old"), CyDir("b/new"));
            Check("carry: a bundle shipped for a version replaces what the box had of it, and then no archive of the payloads folder itself moves either; the other version keeps its own",
                cyB.Count == 1 && File.ReadAllText(CyDir("b/new/2.8/navmesh/navmesh.json")) == "shipped" && !CyHas("b/new/2.8/navmesh.zip")
                && !CyHas("b/new/2.8/.relic-navmesh-zip-refused") && !CyHas("b/new/root.zip") && CyHas("b/new/1.6/navmesh.zip"));
            CyPut("c/old/2.8/navmesh.zip", "box"); CyPut("c/new/2.8/navmesh.zip", "shipped"); CyPut("c/new/2.8/manifest.json");
            Check("carry: a name the new folder already holds is never replaced; a folder that is not there carries nothing",
                LocalAgent.CarryBoxPayloads(CyDir("c/old"), CyDir("c/new")).Count == 0 && File.ReadAllText(CyDir("c/new/2.8/navmesh.zip")) == "shipped"
                && LocalAgent.CarryBoxPayloads(CyDir("c/absent"), CyDir("c/new")).Count == 0);
            try { Directory.Delete(cyRoot, true); } catch { /* as above */ }

            // ── the update of the agent on this PC (LocalAgent.UpdateAsync) against a stand-in agent: a small
            // python script that answers /health and /status the way an agent does, started and stopped by the
            // launcher's own StartAsync / Stop from the temp root — never the developer's real agent. The
            // shipped agents are folders of the same shape with another script: one that passes, one whose
            // self-test fails, one that ends at its start. ──
            const string updStubSource = """
                import http.server, json, os, socketserver, sys
                MARK, SELFTEST, START = "@MARK@", @SELFTEST@, "@START@"
                if "--selftest" in sys.argv:
                    print("selftest of the stand-in agent", MARK)
                    sys.exit(SELFTEST)
                if START == "exit":
                    sys.exit(5)
                config = sys.argv[sys.argv.index("--config") + 1]
                root, port = os.path.dirname(os.path.abspath(config)), 0
                for line in open(config, encoding="utf-8"):
                    if line.startswith("GIO_AGENT_LISTEN="):
                        port = int(line.strip().rsplit(":", 1)[1])
                class Handler(http.server.BaseHTTPRequestHandler):
                    def do_GET(self):
                        code = 200
                        if self.path.startswith("/health"):
                            body = {"ok": True, "service": "gio-agent", "mark": MARK}
                        elif os.path.exists(os.path.join(root, "deny.flag")):
                            code, body = 401, {"error": "unauthorized"}
                        else:
                            busy = {"id": "j1", "kind": "provision", "version": "1.6"} if os.path.exists(os.path.join(root, "busy.flag")) else None
                            body = {"busy": busy, "mark": MARK}
                        data = json.dumps(body).encode()
                        self.send_response(code)
                        self.send_header("Content-Type", "application/json")
                        self.send_header("Content-Length", str(len(data)))
                        self.end_headers()
                        self.wfile.write(data)
                    def log_message(self, *args):
                        pass
                socketserver.TCPServer(("127.0.0.1", port), Handler).serve_forever()
                """;
            static string UpdStub(string mark, int selftest = 0, bool starts = true) =>
                updStubSource.Replace("@MARK@", mark).Replace("@SELFTEST@", selftest.ToString()).Replace("@START@", starts ? "ok" : "exit");
            const int updPort = 59_994;
            var updPython = LocalAgent.FindPython();
            if (updPython is null) Console.WriteLine("        (skipped: no python for the update cases)");
            else
            {
                string updShipRoot = Path.Combine(Path.GetTempPath(), "relic-server-spike-ship-" + Guid.NewGuid().ToString("n")[..8]);
                string UpdShip(string name, string mark, int selftest = 0, bool starts = true)
                {
                    string dir = Path.Combine(updShipRoot, name);
                    Directory.CreateDirectory(Path.Combine(dir, "payloads", "1.6"));
                    File.WriteAllText(Path.Combine(dir, "gio_agent.py"), UpdStub(mark, selftest, starts), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(dir, "payloads", "1.6", "manifest.json"), "{\"mark\": \"" + mark + "\"}\n");
                    return dir;
                }
                string UpdMark()
                {
                    try
                    {
                        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
                        using var doc = JsonDocument.Parse(http.GetStringAsync($"http://127.0.0.1:{updPort}/health").GetAwaiter().GetResult());
                        return doc.RootElement.GetProperty("mark").GetString() ?? "";
                    }
                    catch { return ""; }
                }
                (LocalAgent.UpdateReport? Report, string Error, List<string> Lines) Upd(string shipped)
                {
                    var lines = new List<string>();
                    try { return (LocalAgent.UpdateAsync(lines.Add, shipped, null, 40).GetAwaiter().GetResult(), "", lines); }
                    catch (Exception ex) { return (null, ex.Message, lines); }
                }
                // An error text with one placeholder, as the part before and the part after the value.
                bool UpdSays(string error, string key, string placeholder)
                {
                    var parts = L.T(key, new Dictionary<string, object?> { [placeholder] = "\u0001" }).Split('\u0001');
                    return parts.Length == 2 && error.StartsWith(parts[0], StringComparison.Ordinal) && error.EndsWith(parts[1], StringComparison.Ordinal);
                }
                string UpdRead(string rel) => File.ReadAllText(Path.Combine(tmpRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                try
                {
                    string updGood = UpdShip("good", "second-2"), updThird = UpdShip("third", "third-33"),
                        updBadSelftest = UpdShip("badselftest", "selftest-fails", selftest: 3), updBadStart = UpdShip("badstart", "ends-at-start", starts: false);
                    File.WriteAllText(LocalAgent.ConfigPath, $"# kept as it is\nGIO_AGENT_LISTEN=127.0.0.1:{updPort}\nGIO_AGENT_TOKEN=stand-in-token-0123456789abcdef\n", new UTF8Encoding(false));
                    byte[] updConfig = File.ReadAllBytes(LocalAgent.ConfigPath);
                    File.WriteAllText(LocalAgent.PythonPath, updPython.Exe + Environment.NewLine + updPython.Exe);
                    File.WriteAllText(LocalAgent.ScriptPath, UpdStub("first"), new UTF8Encoding(false));
                    Directory.CreateDirectory(Path.Combine(LocalAgent.PayloadsDir, "1.6", "navmesh"));
                    File.WriteAllText(Path.Combine(LocalAgent.PayloadsDir, "1.6", "manifest.json"), "{\"mark\": \"first\"}\n");
                    File.WriteAllText(Path.Combine(LocalAgent.PayloadsDir, "1.6", "navmesh", "navmesh.json"), "the box's bundle");
                    File.WriteAllText(Path.Combine(LocalAgent.PayloadsDir, "1.6", "navmesh.zip"), "the box's archive");
                    LocalAgent.StartAsync(updPort).GetAwaiter().GetResult();
                    Check("update: the stand-in agent runs — started by StartAsync from the installed script", UpdMark() == "first");

                    // 1. the update itself
                    var u1 = Upd(updGood);
                    Check("update: the shipped agent is proven, the running one stopped, script and payloads replaced, the agent started again — and it is the shipped code that answers" + (u1.Error.Length > 0 ? " — " + u1.Error : ""),
                        u1.Report is { Replaced: true, Restarted: true, Running: true } && UpdMark() == "second-2"
                        && UpdRead("gio_agent.py") == UpdStub("second-2") && UpdRead("payloads/1.6/manifest.json").Contains("second-2")
                        && AgentBuild.Identity(tmpRoot) == AgentBuild.Identity(updGood)
                        && u1.Lines.Contains(L.T("core.deploy.selftestOk")) && u1.Lines.Contains(L.T("core.deploy.updateDone")));
                    Check("update: the configuration is not touched — the file is byte for byte what it was", File.ReadAllBytes(LocalAgent.ConfigPath).SequenceEqual(updConfig));
                    Check("update: the files replaced are kept as ONE previous generation; the staging folder is gone",
                        UpdRead("previous/gio_agent.py") == UpdStub("first") && UpdRead("previous/payloads/1.6/manifest.json").Contains("first")
                        && !Directory.Exists(LocalAgent.StagePath) && !LocalAgent.Updating);
                    Check("update: what the box added to its payloads moved along (an adopted navmesh folder, its archive) and is named in the report",
                        UpdRead("payloads/1.6/navmesh/navmesh.json") == "the box's bundle" && UpdRead("payloads/1.6/navmesh.zip") == "the box's archive"
                        && !Directory.Exists(Path.Combine(LocalAgent.PreviousPath, "payloads", "1.6", "navmesh"))
                        && u1.Report is not null && u1.Report.Carried.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(new[] { "1.6/navmesh", "1.6/navmesh.zip" }));

                    // 2. files already the shipped ones, the agent running: only the restart is left
                    int updPidBefore = LocalAgent.RunningPid().Pid;
                    var u2 = Upd(updGood);
                    Check("update: with the files already the shipped ones a running agent is only restarted — nothing is staged, replaced or kept again",
                        u2.Report is { Replaced: false, Restarted: true, Running: true } && UpdMark() == "second-2"
                        && LocalAgent.RunningPid() is (true, var updPidNow) && updPidNow != updPidBefore
                        && UpdRead("previous/gio_agent.py") == UpdStub("first") && u2.Lines.Contains(L.T("core.deploy.updateRestartOnly")));

                    // 3. refusals that change nothing: a job in progress, an agent that cannot be asked, a failed self-test
                    updPidBefore = LocalAgent.RunningPid().Pid;
                    bool UpdUntouched() => UpdMark() == "second-2" && LocalAgent.RunningPid().Pid == updPidBefore
                        && UpdRead("gio_agent.py") == UpdStub("second-2") && UpdRead("previous/gio_agent.py") == UpdStub("first")
                        && !Directory.Exists(LocalAgent.StagePath) && !LocalAgent.Updating;
                    File.WriteAllText(Path.Combine(tmpRoot, "busy.flag"), "");
                    var u3 = Upd(updThird);
                    File.Delete(Path.Combine(tmpRoot, "busy.flag"));
                    Check("update: an agent in the middle of a job is not stopped — refused with the job's kind, the agent, its files and the kept generation as they were",
                        u3.Report is null && u3.Error == L.T("core.deploy.updateJobRunning", new { kind = "provision" }) && UpdUntouched());
                    File.WriteAllText(Path.Combine(tmpRoot, "deny.flag"), "");
                    var u4 = Upd(updThird);
                    File.Delete(Path.Combine(tmpRoot, "deny.flag"));
                    Check("update: an agent that answers but cannot be asked about its jobs (a 401) is not stopped either — \"could not tell\", nothing changed",
                        u4.Report is null && UpdSays(u4.Error, "core.deploy.updateCannotTell", "detail") && UpdUntouched());
                    var u5 = Upd(updBadSelftest);
                    Check("update: a shipped agent whose self-test fails replaces nothing — the running agent is not even stopped",
                        u5.Report is null && UpdSays(u5.Error, "core.deploy.selftestFailed", "tail") && UpdUntouched());

                    // 4. an updated agent that does not start: every move is taken back, the previous agent runs again
                    var u6 = Upd(updBadStart);
                    Check("update: an updated agent that does not start is undone — the previous script and payloads are back in place (what the box added too) and the previous agent answers again",
                        u6.Report is null && UpdSays(u6.Error, "core.deploy.updateStartFailed", "detail") && UpdMark() == "second-2"
                        && UpdRead("gio_agent.py") == UpdStub("second-2") && UpdRead("payloads/1.6/manifest.json").Contains("second-2")
                        && UpdRead("payloads/1.6/navmesh/navmesh.json") == "the box's bundle" && UpdRead("payloads/1.6/navmesh.zip") == "the box's archive"
                        && LocalAgent.RunningPid().Running && !Directory.Exists(LocalAgent.StagePath) && !LocalAgent.Updating
                        && File.ReadAllBytes(LocalAgent.ConfigPath).SequenceEqual(updConfig));

                    // 4b. ...and when a move of that undo cannot go back — a file of the new payloads is held —
                    // the journal is a cut update's again BEFORE the undo begins: nothing is started on half
                    // a set, and the launcher's next look at the agent finishes it. The file is taken the
                    // moment the new payloads are in place, by a thread that waits for them.
                    string updSwap = Path.Combine(LocalAgent.PreviousPath, ".swap"), updSwapped = Path.Combine(LocalAgent.PreviousPath, ".swapped");
                    string updNewManifest = Path.Combine(LocalAgent.PayloadsDir, "1.6", "manifest.json");
                    FileStream? updHeld = null;
                    using (var updHoldStop = new CancellationTokenSource())
                    {
                        var updHolder = Task.Run(() =>
                        {
                            while (!updHoldStop.IsCancellationRequested && updHeld is null)
                            {
                                try
                                {
                                    if (File.Exists(updNewManifest) && File.ReadAllText(updNewManifest).Contains("ends-at-start", StringComparison.Ordinal))
                                        updHeld = new FileStream(updNewManifest, FileMode.Open, FileAccess.Read, FileShare.None);
                                }
                                catch (IOException) { }
                                catch (UnauthorizedAccessException) { }
                                if (updHeld is null) Thread.Sleep(5);
                            }
                        });
                        var u6b = Upd(updBadStart);
                        updHoldStop.Cancel();
                        updHolder.Wait();
                        bool updJournalBack = File.Exists(updSwap) && !File.Exists(updSwapped);
                        var updUndoParts = L.T("core.deploy.updateUndoFailed", new Dictionary<string, object?> { ["detail"] = "\u0001", ["undo"] = "\u0001", ["dir"] = LocalAgent.PreviousPath }).Split('\u0001');
                        Check("update: an undo that a held file stops is said as it is — the journal is a cut update's again, no agent is started on half a set" + (u6b.Error.Length > 0 ? " — " + u6b.Error : ""),
                            updHeld is not null && u6b.Report is null && updUndoParts.Length == 3
                            && u6b.Error.StartsWith(updUndoParts[0], StringComparison.Ordinal) && u6b.Error.EndsWith(updUndoParts[2], StringComparison.Ordinal)
                            && updJournalBack && UpdMark() == "" && !LocalAgent.Updating);
                        updHeld?.Dispose();
                        Check("update: ... and the launcher's next look at the agent finishes that undo — the previous script, its payloads and what the box had added are back, the journal and the staging folder gone",
                            LocalAgent.RecoverCutUpdate() && UpdRead("gio_agent.py") == UpdStub("second-2") && UpdRead("payloads/1.6/manifest.json").Contains("second-2")
                            && UpdRead("payloads/1.6/navmesh/navmesh.json") == "the box's bundle" && UpdRead("payloads/1.6/navmesh.zip") == "the box's archive"
                            && !File.Exists(updSwap) && !Directory.Exists(LocalAgent.StagePath));
                    }

                    // 5. an agent that is not running: its files are replaced and it stays stopped
                    LocalAgent.Stop(updPort);
                    var u7 = Upd(updThird);
                    Check("update: an agent that was not running gets the new files and stays stopped",
                        u7.Report is { Replaced: true, Restarted: false, Running: false } && UpdMark() == "" && UpdRead("gio_agent.py") == UpdStub("third-33")
                        && UpdRead("previous/gio_agent.py") == UpdStub("second-2") && u7.Lines.Contains(L.T("core.deploy.updateLeftStopped")));
                    var u8 = Upd(updThird);
                    Check("update: up to date and not running — nothing to do, said in one line",
                        u8.Report is { Replaced: false, Restarted: false, Running: false } && u8.Lines.SequenceEqual(new[] { L.T("core.deploy.updateNothing") }));

                    // 6. a process that is there but does not answer is never taken for an idle agent: it is
                    // not stopped, whatever it is doing. A python that only sleeps stands in for it, its pid in
                    // the pid file as an agent's is.
                    using (var updSilent = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(updPython.Exe, "-c \"import time; time.sleep(120)\"")
                        { UseShellExecute = false, CreateNoWindow = true })!)
                    {
                        try
                        {
                            File.WriteAllText(LocalAgent.PidPath, updSilent.Id.ToString());
                            var u9 = Upd(updGood);
                            Check("update: an agent process that does not answer cannot be asked about its jobs — refused (\"could not tell\"), not stopped, nothing replaced",
                                u9.Report is null && UpdSays(u9.Error, "core.deploy.updateCannotTell", "detail") && !updSilent.HasExited
                                && UpdRead("gio_agent.py") == UpdStub("third-33") && UpdRead("previous/gio_agent.py") == UpdStub("second-2")
                                && !Directory.Exists(LocalAgent.StagePath));
                            // ...and one bound to a single other address of this PC could not be seen to start
                            // again (the start is watched on 127.0.0.1): refused before it is stopped.
                            File.WriteAllText(LocalAgent.ConfigPath, $"GIO_AGENT_LISTEN=192.0.2.10:{updPort}\nGIO_AGENT_TOKEN=stand-in-token-0123456789abcdef\n", new UTF8Encoding(false));
                            var u10 = Upd(updGood);
                            Check("update: a running agent that listens on one other address only is refused before the stop — its restart could not be watched",
                                u10.Report is null && u10.Error == L.T("core.deploy.updateListenHost", new { listen = $"192.0.2.10:{updPort}" }) && !updSilent.HasExited
                                && UpdRead("gio_agent.py") == UpdStub("third-33"));
                        }
                        finally
                        {
                            try { if (!updSilent.HasExited) updSilent.Kill(true); updSilent.WaitForExit(5000); } catch { }
                            try { File.Delete(LocalAgent.PidPath); } catch { }
                            File.WriteAllBytes(LocalAgent.ConfigPath, updConfig);
                        }
                    }

                    // 7. an updated agent that does not start while another program holds the finished swap's
                    // journal: it cannot take its first name, and an undo begun from it could not be finished
                    // by anything — so nothing is undone. The new files stay in place, whole and stopped, the
                    // replaced ones kept. The journal is taken the moment it appears.
                    LocalAgent.StartAsync(updPort).GetAwaiter().GetResult();
                    FileStream? updJournalHeld = null;
                    using (var updJournalStop = new CancellationTokenSource())
                    {
                        var updJournalHolder = Task.Run(() =>
                        {
                            while (!updJournalStop.IsCancellationRequested && updJournalHeld is null)
                            {
                                try { if (File.Exists(updSwapped)) updJournalHeld = new FileStream(updSwapped, FileMode.Open, FileAccess.Read, FileShare.Read); }
                                catch (IOException) { }
                                catch (UnauthorizedAccessException) { }
                                if (updJournalHeld is null) Thread.Sleep(5);
                            }
                        });
                        var u11 = Upd(updBadStart);
                        updJournalStop.Cancel();
                        updJournalHolder.Wait();
                        var updKeptParts = L.T("core.deploy.updateStartFailedKept", new Dictionary<string, object?> { ["detail"] = "\u0001", ["dir"] = LocalAgent.PreviousPath }).Split('\u0001');
                        Check("update: an updated agent that does not start while another program holds the swap's journal is not undone from it, and is told so — the new files stay in place whole, the replaced ones kept, nothing half moved" + (u11.Error.Length > 0 ? " — " + u11.Error : ""),
                            updJournalHeld is not null && u11.Report is null && updKeptParts.Length == 2
                            && u11.Error.StartsWith(updKeptParts[0], StringComparison.Ordinal) && u11.Error.EndsWith(updKeptParts[1], StringComparison.Ordinal)
                            && UpdRead("gio_agent.py") == UpdStub("ends-at-start", starts: false) && UpdRead("payloads/1.6/manifest.json").Contains("ends-at-start")
                            && UpdRead("previous/gio_agent.py") == UpdStub("third-33") && !File.Exists(updSwap) && UpdMark() == "" && !LocalAgent.Updating);
                        updJournalHeld?.Dispose();
                        Check("update: ... and the launcher's next look leaves that whole set alone",
                            !LocalAgent.RecoverCutUpdate() && UpdRead("gio_agent.py") == UpdStub("ends-at-start", starts: false) && !Directory.Exists(LocalAgent.StagePath));
                    }
                }
                finally
                {
                    try { LocalAgent.Stop(updPort); } catch { }
                    foreach (var updLeft in new[] { LocalAgent.PayloadsDir, LocalAgent.PreviousPath, LocalAgent.StagePath, updShipRoot })
                        try { if (Directory.Exists(updLeft)) Directory.Delete(updLeft, true); } catch { }
                    foreach (var updLeft in new[] { LocalAgent.ScriptPath, LocalAgent.PythonPath, LocalAgent.PidPath, Path.Combine(tmpRoot, "busy.flag"), Path.Combine(tmpRoot, "deny.flag") })
                        try { File.Delete(updLeft); } catch { }
                }
            }

            // ── an update the launcher did not live to finish (LocalAgent.RecoverCutUpdate). The update
            // writes every file move into previous\.swap BEFORE it makes it — one line "F|D <tab> from
            // <tab> to", the format a later launcher build must still read — and a journal that is still
            // there says the moves are incomplete: each one is taken back, last first, so the agent that
            // was installed is whole again. No process is involved: files only. ──
            string rcScript = LocalAgent.ScriptPath, rcPayloads = LocalAgent.PayloadsDir, rcPrev = LocalAgent.PreviousPath, rcStage = LocalAgent.StagePath;
            string rcJournal = Path.Combine(rcPrev, ".swap");
            string RcLine(bool dir, string from, string to) => (dir ? "D" : "F") + "\t" + from + "\t" + to + "\n";
            void RcReset()
            {
                foreach (var rcDir in new[] { rcPayloads, rcPrev, rcStage })
                    if (Directory.Exists(rcDir)) Directory.Delete(rcDir, true);
                if (File.Exists(rcScript)) File.Delete(rcScript);
                Directory.CreateDirectory(rcPrev);
            }
            void RcPut(string path, string text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            File.WriteAllText(LocalAgent.ConfigPath, "GIO_AGENT_LISTEN=127.0.0.1:18977\n");
            RcReset();
            RcPut(rcScript, "installed"); RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "installed");
            Check("recover: without a journal there is nothing to undo", !LocalAgent.RecoverCutUpdate() && File.ReadAllText(rcScript) == "installed");
            // cut right after the first move: the script is set aside, nothing else moved yet
            RcReset();
            RcPut(Path.Combine(rcPrev, "gio_agent.py"), "installed"); RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "installed");
            RcPut(Path.Combine(rcStage, "gio_agent.py"), "shipped");
            File.WriteAllText(rcJournal, RcLine(false, rcScript, Path.Combine(rcPrev, "gio_agent.py")));
            bool rcNotInstalled = !LocalAgent.IsInstalled;
            Check("recover: cut after the script was set aside — the agent read as not installed; the script is back, the journal and the staging folder are gone",
                rcNotInstalled && LocalAgent.RecoverCutUpdate() && LocalAgent.IsInstalled && File.ReadAllText(rcScript) == "installed"
                && !File.Exists(rcJournal) && !Directory.Exists(rcStage) && !File.Exists(Path.Combine(rcPrev, "gio_agent.py")));
            // cut after every move but the journal's rename: new script + new payloads in place, a carried entry too
            RcReset();
            RcPut(Path.Combine(rcPrev, "gio_agent.py"), "installed"); RcPut(Path.Combine(rcPrev, "payloads", "1.6", "manifest.json"), "installed");
            RcPut(rcScript, "shipped"); RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "shipped");
            RcPut(Path.Combine(rcPayloads, "1.6", "navmesh", "navmesh.json"), "the box's bundle");
            File.WriteAllText(rcJournal,
                RcLine(false, rcScript, Path.Combine(rcPrev, "gio_agent.py")) + RcLine(true, rcPayloads, Path.Combine(rcPrev, "payloads"))
                + RcLine(false, Path.Combine(rcStage, "gio_agent.py"), rcScript) + RcLine(true, Path.Combine(rcStage, "payloads"), rcPayloads)
                + RcLine(true, Path.Combine(rcPrev, "payloads", "1.6", "navmesh"), Path.Combine(rcPayloads, "1.6", "navmesh")));
            Check("recover: cut after the last move — every move goes back in reverse order: the installed script and payloads are in place again, with what the box had added",
                LocalAgent.RecoverCutUpdate() && File.ReadAllText(rcScript) == "installed"
                && File.ReadAllText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "installed"
                && File.ReadAllText(Path.Combine(rcPayloads, "1.6", "navmesh", "navmesh.json")) == "the box's bundle"
                && !File.Exists(rcJournal) && !Directory.Exists(rcStage) && !Directory.Exists(Path.Combine(rcPrev, "payloads")));
            // a last line without its line end was being written when the launcher went away: its move never began
            RcReset();
            RcPut(Path.Combine(rcPrev, "gio_agent.py"), "installed"); RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "installed");
            string rcTorn = RcLine(true, rcPayloads, Path.Combine(rcPrev, "payloads"));
            File.WriteAllText(rcJournal, RcLine(false, rcScript, Path.Combine(rcPrev, "gio_agent.py")) + rcTorn[..^12]);
            Check("recover: a journal whose last line was cut short — that move never began: only the whole lines are undone",
                LocalAgent.RecoverCutUpdate() && File.ReadAllText(rcScript) == "installed"
                && File.ReadAllText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "installed" && !File.Exists(rcJournal));
            // going through a journal twice changes nothing (what is back is passed over); a finished swap's journal has another name
            RcReset();
            RcPut(rcScript, "shipped"); RcPut(Path.Combine(rcPrev, "gio_agent.py"), "installed"); RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "shipped");
            File.WriteAllText(Path.Combine(rcPrev, ".swapped"), RcLine(false, rcScript, Path.Combine(rcPrev, "gio_agent.py")) + RcLine(false, Path.Combine(rcStage, "gio_agent.py"), rcScript));
            Check("recover: the journal of a swap that was finished is not a cut update's — a completed update is never undone at the next start",
                !LocalAgent.RecoverCutUpdate() && File.ReadAllText(rcScript) == "shipped" && File.Exists(Path.Combine(rcPrev, ".swapped")));
            // a move that cannot go back yet (the file is held): the journal stays, the next update refuses
            // to build on files that are neither set, and the undo is finished once the file is free
            RcReset();
            RcPut(Path.Combine(rcPrev, "gio_agent.py"), "installed"); RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "installed");
            File.WriteAllText(rcJournal, RcLine(false, rcScript, Path.Combine(rcPrev, "gio_agent.py")));
            bool rcHeldUndone; string rcBlocked = "";
            using (new FileStream(Path.Combine(rcPrev, "gio_agent.py"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                rcHeldUndone = LocalAgent.RecoverCutUpdate();
                try { LocalAgent.UpdateAsync(null, Path.Combine(tmpRoot, "no-such-shipped-agent")).GetAwaiter().GetResult(); }
                catch (InvalidOperationException ex) { rcBlocked = ex.Message; }
            }
            Check("recover: while a moved file is held the undo is not finished — the journal stays, and an update refuses to build on files that are neither the old set nor the new one",
                !rcHeldUndone && File.Exists(rcJournal) && rcBlocked == L.T("core.deploy.updateCutPending", new { dir = tmpRoot }) && !LocalAgent.Updating);
            Check("recover: ... and once the file is free the same journal is gone through to its end",
                LocalAgent.RecoverCutUpdate() && File.ReadAllText(rcScript) == "installed" && !File.Exists(rcJournal));

            // The paths repeat inside a journal — the place of the script is the source of its first move
            // and the target of its third, the payloads folder likewise — so it is undone strictly last
            // first and loses each line as that move goes back. A pass a held file stops leaves every
            // earlier move alone, and the next pass goes on from that very line: gone through any other
            // way, the second pass would take the script (or the payloads) the first one had just put back.
            void RcFullCut()
            {
                RcReset();
                RcPut(Path.Combine(rcPrev, "gio_agent.py"), "installed"); RcPut(Path.Combine(rcPrev, "payloads", "1.6", "manifest.json"), "installed");
                RcPut(rcScript, "shipped"); RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "shipped");
                RcPut(Path.Combine(rcPayloads, "1.6", "navmesh", "navmesh.json"), "the box's bundle");
                File.WriteAllText(rcJournal,
                    RcLine(false, rcScript, Path.Combine(rcPrev, "gio_agent.py")) + RcLine(true, rcPayloads, Path.Combine(rcPrev, "payloads"))
                    + RcLine(false, Path.Combine(rcStage, "gio_agent.py"), rcScript) + RcLine(true, Path.Combine(rcStage, "payloads"), rcPayloads)
                    + RcLine(true, Path.Combine(rcPrev, "payloads", "1.6", "navmesh"), Path.Combine(rcPayloads, "1.6", "navmesh")));
            }
            string RcText(string path) => File.Exists(path) ? File.ReadAllText(path) : "(absent)";
            bool RcWhole() => RcText(rcScript) == "installed" && RcText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "installed"
                && RcText(Path.Combine(rcPayloads, "1.6", "navmesh", "navmesh.json")) == "the box's bundle"
                && !File.Exists(rcJournal) && !Directory.Exists(rcStage)
                && !File.Exists(Path.Combine(rcPrev, "gio_agent.py")) && !Directory.Exists(Path.Combine(rcPrev, "payloads"));
            int RcJournalLines() => File.Exists(rcJournal) ? File.ReadAllText(rcJournal).Count(rcCh => rcCh == '\n') : -1;

            // the new payloads folder is held: the undo stops AT that move
            RcFullCut();
            bool rcA; int rcALines; string rcAScript, rcAStart = "";
            using (new FileStream(Path.Combine(rcPayloads, "1.6", "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                rcA = LocalAgent.RecoverCutUpdate();
                rcALines = RcJournalLines();
                rcAScript = RcText(rcScript);
                try { LocalAgent.StartAsync(18977).GetAwaiter().GetResult(); }
                catch (InvalidOperationException ex) { rcAStart = ex.Message; }
            }
            Check("recover: a held payloads folder stops the undo AT that move — the carried entry is back and off the journal, the script's moves wait their turn (the shipped script is still in place, the installed one still set aside)",
                !rcA && rcALines == 4 && rcAScript == "shipped" && RcText(Path.Combine(rcPrev, "gio_agent.py")) == "installed"
                && RcText(Path.Combine(rcPrev, "payloads", "1.6", "navmesh", "navmesh.json")) == "the box's bundle");
            Check("recover: ... while the journal remains nothing is started from files that are neither set — a Start is refused with the folder named",
                rcAStart == L.T("core.deploy.updateCutPending", new { dir = tmpRoot }));
            Check("recover: ... and the next pass goes on from that very move: the installed script, the installed payloads and the box's bundle are in place",
                LocalAgent.RecoverCutUpdate() && RcWhole());

            // the new script is held: the payloads are already back in the staging folder, which must stay
            RcFullCut();
            bool rcB; int rcBLines; bool rcBState;
            using (new FileStream(rcScript, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                rcB = LocalAgent.RecoverCutUpdate();
                rcBLines = RcJournalLines();
                rcBState = !Directory.Exists(rcPayloads) && RcText(Path.Combine(rcStage, "payloads", "1.6", "manifest.json")) == "shipped"
                    && RcText(Path.Combine(rcPrev, "payloads", "1.6", "manifest.json")) == "installed";
            }
            Check("recover: a held script stops the undo at ITS move — the new payloads went back to the staging folder, which stays while the journal does; the installed payloads wait their turn",
                !rcB && rcBLines == 3 && rcBState);
            Check("recover: ... and the next pass ends with the installed script and the WHOLE installed payloads folder — never one that holds the carried entries alone",
                LocalAgent.RecoverCutUpdate() && RcWhole());

            // the launcher went away between a move back and the journal's rewrite: that line names a move that is no longer made
            RcReset();
            RcPut(Path.Combine(rcPrev, "gio_agent.py"), "installed"); RcPut(Path.Combine(rcPrev, "payloads", "1.6", "manifest.json"), "installed");
            RcPut(Path.Combine(rcPrev, "payloads", "1.6", "navmesh", "navmesh.json"), "the box's bundle");
            RcPut(rcScript, "shipped"); RcPut(Path.Combine(rcStage, "payloads", "1.6", "manifest.json"), "shipped");
            File.WriteAllText(rcJournal,
                RcLine(false, rcScript, Path.Combine(rcPrev, "gio_agent.py")) + RcLine(true, rcPayloads, Path.Combine(rcPrev, "payloads"))
                + RcLine(false, Path.Combine(rcStage, "gio_agent.py"), rcScript) + RcLine(true, Path.Combine(rcStage, "payloads"), rcPayloads));
            Check("recover: a line whose move was already taken back (the launcher went away before the journal was written again) is passed over, the rest undone",
                LocalAgent.RecoverCutUpdate() && RcWhole());
            // a journal written by hand in an editor ends its lines with CR LF
            RcReset();
            RcPut(Path.Combine(rcPrev, "gio_agent.py"), "installed"); RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "installed");
            File.WriteAllText(rcJournal, RcLine(false, rcScript, Path.Combine(rcPrev, "gio_agent.py")).Replace("\n", "\r\n"));
            Check("recover: a journal line that ends with CR LF names the same move",
                LocalAgent.RecoverCutUpdate() && RcText(rcScript) == "installed" && !File.Exists(rcJournal));

            // An install's payloads folder that goes into the place an undo still has to fill is one more move
            // of that journal: an install that ended before its script was in is taken back with the rest.
            RcFullCut();
            string rcFresh = Path.Combine(tmpRoot, "ship-for-a-cut-install");
            RcPut(Path.Combine(rcFresh, "1.6", "manifest.json"), "from the form");
            bool rcC; int rcCLines; string rcCLast;
            using (new FileStream(rcScript, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                rcC = LocalAgent.RecoverCutUpdate();     // stops at the held script: three lines, no payloads folder
                LocalAgent.ReplacePayloads(rcFresh);     // what an install does next
                rcCLines = RcJournalLines();
                rcCLast = File.ReadAllText(rcJournal).TrimEnd('\n').Split('\n')[^1];
            }
            Check("recover: an install's payloads folder that goes into the place the undo still has to fill is written into the journal as one more move",
                !rcC && rcCLines == 4 && rcCLast == RcLine(true, Path.Combine(tmpRoot, ".payloads-new"), rcPayloads).TrimEnd('\n')
                && RcText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "from the form");
            Check("recover: ... so an install that ended there is taken back with the rest — the installed agent is whole, and nothing of that install is left",
                LocalAgent.RecoverCutUpdate() && RcWhole() && !Directory.Exists(Path.Combine(tmpRoot, ".payloads-new")));
            Directory.Delete(rcFresh, true);

            // A line whose target AND source are both there is no state an update leaves: something else was
            // put where the file has to go back to. Nothing is moved over it, and the journal stays.
            RcReset();
            RcPut(Path.Combine(rcPrev, "gio_agent.py"), "installed"); RcPut(rcScript, "put there by something else");
            RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "installed");
            File.WriteAllText(rcJournal, RcLine(false, rcScript, Path.Combine(rcPrev, "gio_agent.py")));
            bool rcD = LocalAgent.RecoverCutUpdate();
            string rcDStart = "";
            try { LocalAgent.StartAsync(18977).GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) { rcDStart = ex.Message; }
            Check("recover: a move whose target and source are both there is not made over the file in the way — the journal stays, both files stay, and nothing is started on that set",
                !rcD && RcJournalLines() == 1 && RcText(rcScript) == "put there by something else"
                && RcText(Path.Combine(rcPrev, "gio_agent.py")) == "installed" && rcDStart == L.T("core.deploy.updateCutPending", new { dir = tmpRoot }));

            // One hand on the files at a time: the start screen, the Server page's card and a Start all ask
            // for the recovery, and each would move files on its own reading of the journal. While one
            // caller is inside the undo — kept there by a held file — the message thread's call comes back
            // at once without touching a file, and a caller that waits gets the files only afterwards.
            RcFullCut();
            bool rcFirst = false, rcWaiter = true;
            var rcHold = new FileStream(Path.Combine(rcPayloads, "1.6", "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None);
            var rcFirstThread = new Thread(() => rcFirst = LocalAgent.RecoverCutUpdate(wait: true));
            rcFirstThread.Start();
            Thread.Sleep(400);
            var rcWatch = System.Diagnostics.Stopwatch.StartNew();
            bool rcSecond = LocalAgent.RecoverCutUpdate();
            long rcSecondMs = rcWatch.ElapsedMilliseconds;
            var rcWaiterThread = new Thread(() => rcWaiter = LocalAgent.RecoverCutUpdate(wait: true));
            rcWaiterThread.Start();
            Thread.Sleep(300);
            rcHold.Dispose();
            rcFirstThread.Join();
            rcWaiterThread.Join();
            Check($"recover: one hand on the files at a time — while one caller is inside the undo a call that does not wait returns at once ({rcSecondMs} ms) and moves nothing, one that waits finds the undo done: the installed agent is whole",
                rcFirst && !rcSecond && rcSecondMs < 1000 && !rcWaiter && RcWhole());

            // ── an install over a cut update that cannot be undone. The install takes the journal's place:
            // the shipped files go in, the journal goes only once they have, and what the undo had already
            // taken back out of the payloads folder is kept. The stand-in agent's --selftest passes. ──
            if (updPython is null) Console.WriteLine("        (skipped: no python for the install-over-a-cut-update cases)");
            else
            {
                string inRoot = Path.Combine(Path.GetTempPath(), "relic-server-spike-install-" + Guid.NewGuid().ToString("n")[..8]);
                string inShip = Path.Combine(inRoot, "agent"), inStack = Path.Combine(inRoot, "stack16");
                try
                {
                    Directory.CreateDirectory(Path.Combine(inShip, "payloads", "1.6"));
                    File.WriteAllText(Path.Combine(inShip, "gio_agent.py"), UpdStub("put-in-by-the-form"), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(inShip, "payloads", "1.6", "manifest.json"), "from the form");
                    foreach (var inEntry in AgentInstallPlan.RequiredStackEntries) Directory.CreateDirectory(Path.Combine(inStack, inEntry));
                    var inPlan = new AgentInstallPlan
                    {
                        Target = AgentInstallPlan.TargetWindows, Dir16 = inStack, Dir28 = "", Listen = "127.0.0.1:18977",
                        Token = AgentInstallPlan.NewToken(), ServerName = "relic-spike",
                    };
                    // the payloads folder is in use: the install replaces nothing, and the journal is still the undo's
                    RcFullCut();
                    string inBusy = "";
                    using (new FileStream(Path.Combine(rcPayloads, "1.6", "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        try { LocalAgent.Install(inPlan, updPython, null, inShip); }
                        catch (InvalidOperationException ex) { inBusy = ex.Message; }
                    }
                    Check("install: over a cut update whose files are in use nothing is replaced — the refusal names the payloads folder, and the journal stays for the undo" + (inBusy.Length == 0 ? " — no refusal" : ""),
                        inBusy.StartsWith(L.T("core.deploy.payloadsInUse", new Dictionary<string, object?> { ["dir"] = rcPayloads, ["error"] = "\u0001" }).Split('\u0001')[0], StringComparison.Ordinal)
                        && RcJournalLines() == 4 && RcText(rcScript) == "shipped");
                    Check("install: ... which is finished once the files are free", LocalAgent.RecoverCutUpdate() && RcWhole());
                    // a file of the generation being restored is held: the undo cannot end, the install takes its place
                    RcFullCut();
                    var inLines = new List<string>(); string inError = "";
                    using (new FileStream(Path.Combine(rcPrev, "payloads", "1.6", "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        try { LocalAgent.Install(inPlan, updPython, inLines.Add, inShip); }
                        catch (Exception ex) { inError = ex.GetType().Name + ": " + ex.Message; }
                    }
                    Check("install: over a cut update that cannot be undone the shipped agent goes in, the journal and the staging folder go, and the bundle the undo had taken back is kept — named in the log" + (inError.Length > 0 ? " — " + inError : ""),
                        inError.Length == 0 && RcText(rcScript) == UpdStub("put-in-by-the-form")
                        && RcText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "from the form"
                        && RcText(Path.Combine(rcPayloads, "1.6", "navmesh", "navmesh.json")) == "the box's bundle"
                        && !File.Exists(rcJournal) && !Directory.Exists(rcStage)
                        && inLines.Contains(L.T("core.deploy.keptPayloads", new { list = "1.6/navmesh" })) && !LocalAgent.RecoverCutUpdate());
                    // the script itself is the file that is held: refused before the payloads are touched
                    RcFullCut();
                    string inHeld = "";
                    using (new FileStream(rcScript, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        try { LocalAgent.Install(inPlan, updPython, null, inShip); }
                        catch (Exception ex) { inHeld = ex.Message; }
                    }
                    Check("install: over a cut update whose SCRIPT is held nothing is replaced — refused before the payloads go in, the journal still the undo's",
                        inHeld.StartsWith(L.T("core.deploy.scriptInUse", new Dictionary<string, object?> { ["path"] = rcScript, ["error"] = "\u0001" }).Split('\u0001')[0], StringComparison.Ordinal)
                        && RcJournalLines() == 3 && !Directory.Exists(rcPayloads) && RcText(rcScript) == "shipped"
                        && !Directory.Exists(Path.Combine(tmpRoot, ".payloads-new")));
                    Check("install: ... and the undo is finished once the script is free", LocalAgent.RecoverCutUpdate() && RcWhole());
                    // a set the undo cannot sort out (a file in the way of a move back): the install is the way out
                    RcReset();
                    RcPut(Path.Combine(rcPrev, "gio_agent.py"), "installed"); RcPut(rcScript, "put there by something else");
                    RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "installed"); RcPut(Path.Combine(rcPayloads, "1.6", "navmesh.zip"), "the box's archive");
                    File.WriteAllText(rcJournal, RcLine(false, rcScript, Path.Combine(rcPrev, "gio_agent.py")));
                    string inWayOut = "";
                    try { LocalAgent.Install(inPlan, updPython, null, inShip); }
                    catch (Exception ex) { inWayOut = ex.GetType().Name + ": " + ex.Message; }
                    Check("install: over a set the undo cannot sort out the shipped agent goes in and the journal goes — what the box had added is kept, and the launcher's next look finds nothing to undo" + (inWayOut.Length > 0 ? " — " + inWayOut : ""),
                        inWayOut.Length == 0 && RcText(rcScript) == UpdStub("put-in-by-the-form")
                        && RcText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "from the form"
                        && RcText(Path.Combine(rcPayloads, "1.6", "navmesh.zip")) == "the box's archive"
                        && !File.Exists(rcJournal) && !LocalAgent.RecoverCutUpdate());

                    // An undo that stopped at the script's own move (the set-aside script is held): the installed
                    // payloads are back in place and there is no script. The folder that replaces them is no move
                    // of that journal, so the script goes in FIRST — an install that ended between the two would
                    // otherwise be "undone" into the installed script beside the shipped payloads.
                    void InStuckAtScript()
                    {
                        RcReset();
                        RcPut(Path.Combine(rcPrev, "gio_agent.py"), "installed"); RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "installed");
                        RcPut(Path.Combine(rcPayloads, "1.6", "navmesh", "navmesh.json"), "the box's bundle");
                        File.WriteAllText(rcJournal, RcLine(false, rcScript, Path.Combine(rcPrev, "gio_agent.py")));
                    }
                    InStuckAtScript();
                    bool inScriptWasIn = false; string inFirst = "";
                    string inKeptLine = L.T("core.deploy.keptPayloads", new { list = "1.6/navmesh" });
                    using (new FileStream(Path.Combine(rcPrev, "gio_agent.py"), FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        try { LocalAgent.Install(inPlan, updPython, inLine => { if (inLine == inKeptLine) inScriptWasIn = File.Exists(rcScript); }, inShip); }
                        catch (Exception ex) { inFirst = ex.GetType().Name + ": " + ex.Message; }
                    }
                    Check("install: over an undo that stopped at the script's own move the script goes in BEFORE the payloads (it was there when the payloads' entries were carried) — the shipped agent whole, the box's bundle kept, no journal" + (inFirst.Length > 0 ? " — " + inFirst : ""),
                        inFirst.Length == 0 && inScriptWasIn && RcText(rcScript) == UpdStub("put-in-by-the-form")
                        && RcText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "from the form"
                        && RcText(Path.Combine(rcPayloads, "1.6", "navmesh", "navmesh.json")) == "the box's bundle"
                        && !File.Exists(rcJournal) && !LocalAgent.RecoverCutUpdate());
                    InStuckAtScript();
                    string inBoth = "";
                    using (new FileStream(Path.Combine(rcPrev, "gio_agent.py"), FileMode.Open, FileAccess.Read, FileShare.None))
                    using (new FileStream(Path.Combine(rcPayloads, "1.6", "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        try { LocalAgent.Install(inPlan, updPython, null, inShip); }
                        catch (Exception ex) { inBoth = ex.Message; }
                    }
                    Check("install: ... and when the payloads cannot be replaced there, the script it had put in goes again — nothing was changed, the journal still the undo's",
                        inBoth.StartsWith(L.T("core.deploy.payloadsInUse", new Dictionary<string, object?> { ["dir"] = rcPayloads, ["error"] = "\u0001" }).Split('\u0001')[0], StringComparison.Ordinal)
                        && RcJournalLines() == 1 && !File.Exists(rcScript) && RcText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "installed");
                    Check("install: ... which ends once the files are free — the installed agent whole, the box's bundle with it", LocalAgent.RecoverCutUpdate() && RcWhole());

                    // A journal that will not go (another program holds it open): gone through later it would take
                    // this install back, so the install says so and builds nothing on top; run again, it finishes.
                    RcReset();
                    RcPut(Path.Combine(rcPrev, "gio_agent.py"), "installed"); RcPut(rcScript, "put there by something else");
                    RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "installed");
                    File.WriteAllText(rcJournal, RcLine(false, rcScript, Path.Combine(rcPrev, "gio_agent.py")));
                    string inJournal = "", inAgain = "";
                    using (new FileStream(rcJournal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        try { LocalAgent.Install(inPlan, updPython, null, inShip); }
                        catch (Exception ex) { inJournal = ex.Message; }
                    }
                    Check("install: a journal that will not go ends the install there — said with the file named, and the journal is still there",
                        inJournal == L.T("core.deploy.installJournalInUse", new { path = rcJournal }) && RcJournalLines() == 1);
                    try { LocalAgent.Install(inPlan, updPython, null, inShip); }
                    catch (Exception ex) { inAgain = ex.GetType().Name + ": " + ex.Message; }
                    Check("install: ... and the install run again finishes it — the shipped agent in place, no journal" + (inAgain.Length > 0 ? " — " + inAgain : ""),
                        inAgain.Length == 0 && RcText(rcScript) == UpdStub("put-in-by-the-form")
                        && RcText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "from the form" && !File.Exists(rcJournal) && !LocalAgent.RecoverCutUpdate());
                }
                finally
                {
                    try { if (Directory.Exists(inRoot)) Directory.Delete(inRoot, true); } catch { }
                    try { File.Delete(LocalAgent.PythonPath); } catch { }
                    File.WriteAllText(LocalAgent.ConfigPath, "GIO_AGENT_LISTEN=127.0.0.1:18977\n");
                }
            }

            // ── an install's payloads (LocalAgent.ReplacePayloads): the shipped folder takes the place of the
            // installed one by renames — never a delete first —, and what the box added is kept. ──
            string rpShip = Path.Combine(tmpRoot, "ship-payloads");
            RcReset();
            RcPut(Path.Combine(rpShip, "1.6", "manifest.json"), "shipped"); RcPut(Path.Combine(rpShip, "2.8", "manifest.json"), "shipped");
            LocalAgent.ReplacePayloads(rpShip);
            Check("payloads: a first install copies the shipped folder and leaves nothing beside it",
                File.ReadAllText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "shipped"
                && !Directory.Exists(Path.Combine(tmpRoot, ".payloads-new")) && !Directory.Exists(Path.Combine(tmpRoot, ".payloads-old")));
            RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "installed"); RcPut(Path.Combine(rcPayloads, "1.6", "dropped-by-the-new-build.txt"), "stale");
            RcPut(Path.Combine(rcPayloads, "2.8", "navmesh", "navmesh.json"), "the box's bundle"); RcPut(Path.Combine(rcPayloads, "2.8", "navmesh.zip"), "the box's archive");
            var rpLines = new List<string>();
            LocalAgent.ReplacePayloads(rpShip, rpLines.Add);
            Check("payloads: a re-install replaces every shipped file, drops what the new build no longer has, and keeps the adopted navmesh bundle and its archive — named in the log",
                File.ReadAllText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "shipped" && !File.Exists(Path.Combine(rcPayloads, "1.6", "dropped-by-the-new-build.txt"))
                && File.ReadAllText(Path.Combine(rcPayloads, "2.8", "navmesh", "navmesh.json")) == "the box's bundle"
                && File.ReadAllText(Path.Combine(rcPayloads, "2.8", "navmesh.zip")) == "the box's archive"
                && rpLines.SequenceEqual(new[] { L.T("core.deploy.keptPayloads", new { list = "2.8/navmesh, 2.8/navmesh.zip" }) })
                && !Directory.Exists(Path.Combine(tmpRoot, ".payloads-new")) && !Directory.Exists(Path.Combine(tmpRoot, ".payloads-old")));
            // a run that was cut after it had set the old folder aside: the next run picks the box's entries up from there
            Directory.Move(rcPayloads, Path.Combine(tmpRoot, ".payloads-old"));
            LocalAgent.ReplacePayloads(rpShip);
            Check("payloads: a folder an earlier run had set aside is not lost — it is the payloads folder again before the replacement, and the box's bundle is still there afterwards",
                File.ReadAllText(Path.Combine(rcPayloads, "2.8", "navmesh", "navmesh.json")) == "the box's bundle"
                && File.ReadAllText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "shipped" && !Directory.Exists(Path.Combine(tmpRoot, ".payloads-old")));
            // a file held open in the installed folder (an agent in the middle of a job): the folder is not set aside, so nothing is changed
            RcPut(Path.Combine(rcPayloads, "1.6", "manifest.json"), "installed");
            string rpError = "";
            using (new FileStream(Path.Combine(rcPayloads, "2.8", "navmesh.zip"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { LocalAgent.ReplacePayloads(rpShip); }
                catch (InvalidOperationException ex) { rpError = ex.Message; }
            }
            Check("payloads: with a file open in the installed folder nothing is changed — the refusal names the folder, every installed file and the box's bundle stay",
                rpError.StartsWith(L.T("core.deploy.payloadsInUse", new Dictionary<string, object?> { ["dir"] = rcPayloads, ["error"] = "\u0001" }).Split('\u0001')[0], StringComparison.Ordinal)
                && File.ReadAllText(Path.Combine(rcPayloads, "1.6", "manifest.json")) == "installed"
                && File.ReadAllText(Path.Combine(rcPayloads, "2.8", "navmesh", "navmesh.json")) == "the box's bundle"
                && !Directory.Exists(Path.Combine(tmpRoot, ".payloads-new")) && !Directory.Exists(Path.Combine(tmpRoot, ".payloads-old")));
            RcReset();
            try { Directory.Delete(rpShip, true); Directory.Delete(rcPrev, true); } catch { /* the uninstall cases below delete the whole root */ }

            // The Windows extractor pre-check of a download-marked install (7z.exe / bz.exe / System32\tar.exe
            // with liblzma): a box may lack every tool, so only "never throws" is asserted; what it found is printed.
            string? extractor = null; Exception? extractorErr = null;
            try { extractor = LocalAgent.FindExtractor(); } catch (Exception ex) { extractorErr = ex; }
            Check($"localagent: FindExtractor never throws (found: {extractor ?? "none"})", extractorErr is null);

            // Uninstall on the temp root: a stale pid file, nested payloads, no process on the port.
            Directory.CreateDirectory(Path.Combine(tmpRoot, "payloads", "1.6"));
            File.WriteAllText(Path.Combine(tmpRoot, "payloads", "1.6", "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(tmpRoot, "gio_agent.py"), "# stub");
            File.WriteAllText(Path.Combine(tmpRoot, "agent.pid"), "999999999");
            var rep = LocalAgent.Uninstall(spikePort, removeFirewall: false);
            Check("localagent: uninstall on an idle temp root removes everything, no throw, nothing to stop",
                rep is { FilesRemoved: true, Stopped: false, FirewallRemoved: false, FirewallError: null } && rep.Leftovers.Count == 0 && !Directory.Exists(tmpRoot));
            Check("localagent: a spike root never touches the REAL autostart value",
                LocalAgent.AutostartEnabled == autostartBefore && rep.AutostartRemoved == false);
            // A locked file: reported as a leftover, everything else goes.
            Directory.CreateDirectory(Path.Combine(tmpRoot, "hotpatch"));
            File.WriteAllText(LocalAgent.ConfigPath, "GIO_AGENT_LISTEN=0.0.0.0:18977\n");
            string locked = Path.Combine(tmpRoot, "hotpatch", "pack.pck");
            UninstallReport rep2;
            using (var fs = new FileStream(locked, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(new byte[16]);
                rep2 = LocalAgent.Uninstall(spikePort, removeFirewall: false);
            }
            // The leftover line is "<path>: <error>" through core.deploy.fileLeft; while en.json lacks that key
            // L.T answers the bare key, so the path is only required once the key is known.
            Check("localagent: a locked file is the ONE leftover (its folder is not reported twice); the config and the rest are gone, the root stays",
                rep2 is { FilesRemoved: false } && rep2.Leftovers.Count == 1
                && (!L.Has("core.deploy.fileLeft") || rep2.Leftovers[0].Contains(locked))
                && !File.Exists(LocalAgent.ConfigPath) && File.Exists(locked) && Directory.Exists(tmpRoot));
        }
        finally
        {
            LocalAgent.RootOverride = null;
            try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true); } catch { /* best effort */ }
        }
        Check("localagent: RootOverride cleared -> the real root again", !LocalAgent.Root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));

        // ── public agent errors and their codes: what the launcher retries and what the player reads ──
        Check("public error: server_busy is retried",
            AgentPublicClient.IsBusyRetry(new AgentPublicException(429, "the server is busy", 30, "server_busy")));
        Check("public error: a quota 429 is NOT retried, even with a short wait",
            !AgentPublicClient.IsBusyRetry(new AgentPublicException(429, "too many requests", 20, "quota_server_hour")));
        Check("public error: an older agent's code-less 429 is retried only for its busy text + retryAfter 30",
            AgentPublicClient.IsBusyRetry(new AgentPublicException(429, "the server is busy with another operation, try again shortly", 30))
            && !AgentPublicClient.IsBusyRetry(new AgentPublicException(429, "the server is busy with another operation, try again shortly", 20))
            && !AgentPublicClient.IsBusyRetry(new AgentPublicException(429, "too many requests, try again later", 1200)));
        Check("public error: an older agent's code-less quota 429 with a short token wait is NOT retried",
            !AgentPublicClient.IsBusyRetry(new AgentPublicException(429, "too many requests, try again later", 10))
            && !AgentPublicClient.IsBusyRetry(new AgentPublicException(429, "too many requests, try again later", 30)));
        Check("public error: a non-429 is never retried",
            !AgentPublicClient.IsBusyRetry(new AgentPublicException(503, "down", 30, "server_busy")));
        string Sec(int n) => L.T("core.install.time.sec", new { n });
        string Min(int n) => L.T("core.install.time.min", new { n });
        string Hours(int n) => L.T("core.agent.time.hours", new { n });
        Check("wait: rounded UP — 30 s, 1200 s = 20 min, 1201 s = 21 min, 17280 s = 5 h",
            AgentPublicClient.FormatWait(30) == Sec(30) && AgentPublicClient.FormatWait(1200) == Min(20)
            && AgentPublicClient.FormatWait(1201) == Min(21) && AgentPublicClient.FormatWait(17280) == Hours(5));
        Check("wait: thresholds — 89 s in seconds, 90 s = 2 min, 5399 s = 90 min, 5400 s = 2 h, 0 = 1 s",
            AgentPublicClient.FormatWait(89) == Sec(89) && AgentPublicClient.FormatWait(90) == Min(2)
            && AgentPublicClient.FormatWait(5399) == Min(90) && AgentPublicClient.FormatWait(5400) == Hours(2)
            && AgentPublicClient.FormatWait(0) == Sec(1));
        string quotaText = AgentPublicClient.Describe("quota_ip_hour", 1200, "too many requests, try again later");
        Check("describe: a known code is translated with its real wait",
            quotaText == L.T("core.agent.err.quota_ip_hour", new { wait = Min(20) }) && !quotaText.Contains('{'));
        Check("describe: no code, or a code this build does not know, keeps the agent's text",
            AgentPublicClient.Describe(null, null, "raw") == "raw" && AgentPublicClient.Describe("brand_new_code", 5, "raw") == "raw");
        Check("describe: a text that needs {wait} without a Retry-After keeps the agent's text",
            AgentPublicClient.Describe("quota_ip_day", null, "raw") == "raw");
        Check("describe: a code is a token, never a key path",
            AgentPublicClient.Describe("x.name_taken", null, "raw") == "raw" && AgentPublicClient.Describe("NAME_TAKEN", null, "raw") == "raw");
        Check("describe: a code without a wait needs none",
            AgentPublicClient.Describe("name_taken", null, "name taken") == L.T("core.agent.err.name_taken"));
        // The vocabulary is add-only and lives in the agent: every code it declares needs a text here.
        string agentPy = Path.Combine(AppContext.BaseDirectory, "agent", "gio_agent.py");
        if (!File.Exists(agentPy))
            agentPy = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "agent", "gio_agent.py"));
        var vocab = File.Exists(agentPy)
            ? System.Text.RegularExpressions.Regex.Match(File.ReadAllText(agentPy), @"PUBLIC_ERROR_CODES = \((.*?)\)", System.Text.RegularExpressions.RegexOptions.Singleline)
            : null;
        var codes = vocab is { Success: true }
            ? System.Text.RegularExpressions.Regex.Matches(vocab.Groups[1].Value, "\"([a-z0-9_]+)\"").Select(m => m.Groups[1].Value).ToList()
            : new List<string>();
        var untranslated = codes.Where(c => !L.Has("core.agent.err." + c)).ToList();
        Check($"describe: every code of the agent's PUBLIC_ERROR_CODES ({codes.Count}) has an en.json text"
              + (untranslated.Count > 0 ? " — missing: " + string.Join(", ", untranslated) : ""),
            codes.Count >= 23 && untranslated.Count == 0);
        // The Agent settings card labels every setting the agent's registry declares: the
        // registry lives in the agent, the texts here -- a key added there needs its two texts.
        var registry = File.Exists(agentPy)
            ? System.Text.RegularExpressions.Regex.Match(File.ReadAllText(agentPy), @"AGENT_SETTING_KEYS = \((.*?)\)", System.Text.RegularExpressions.RegexOptions.Singleline)
            : null;
        var settingKeys = registry is { Success: true }
            ? System.Text.RegularExpressions.Regex.Matches(registry.Groups[1].Value, "\"(GIO_[A-Z0-9_]+)\"").Select(m => m.Groups[1].Value).ToList()
            : new List<string>();
        var unlabelled = settingKeys.Where(k => !L.Has("agentCfg.key." + k) || !L.Has("agentCfg.key." + k + ".sub")).ToList();
        Check($"agent settings: every key of the agent's AGENT_SETTING_KEYS ({settingKeys.Count}) has an en.json label + hint"
              + (unlabelled.Count > 0 ? " — missing: " + string.Join(", ", unlabelled) : ""),
            settingKeys.Count >= 23 && unlabelled.Count == 0);
        var enumChoices = new[] { "GIO_PROVISION_MODE.once", "GIO_PROVISION_MODE.switch", "GIO_PROVISION_MODE.never",
                                  "GIO_TXT_FIXES_MODE.now", "GIO_TXT_FIXES_MODE.later" };
        Check("agent settings: every enum choice has an en.json label",
            enumChoices.All(c => L.Has("agentCfg.enum." + c)));
        // The UI texts of the two stack secrets and of the advertised-IP help, and the title of the gameplay
        // settings job. check_i18n.py only sees a key written as the literal argument of t( / T( / tf( / tk(:
        // a label handed to field() as its second argument, a key a label table holds (SRVJOBS: job kind →
        // title key) or one built from a template (`agentCfg.key.${key}.help`) can be missing from en.json
        // without failing the gate — the page would then show the raw key.
        foreach (var uiKey in new[]
        {
            "deploy.net.mysqlPassword", "deploy.net.mysqlPasswordHint", "deploy.net.flaskKey", "deploy.net.flaskKeyHint",
            "deploy.net.advHelp", "agentCfg.key.GIO_ADVERTISED_IP.help", "server.adv.mysqlVendor", "server.adv.flaskVendor",
            "secrets.mysqlRootVendor", "secrets.flaskVendor", "confirm.prepare.vendorSecrets", "secrets.copied",
            "server.job.tweaks",
            // Open administration: the Player page's banner + button, the option's card and confirm, the
            // notice of a revocation, the two toasts of the entry (picked by the reply's openKey: the key, or a
            // token already saved for the host), and the Backend's own texts (the C# side reads them with L.T).
            "server.player.openAdminTitle", "server.player.openAdminEnter", "policy.openAdmin.title", "confirm.openAdmin.onTitle",
            "server.player.openAdminRevokedTitle", "toast.openAdminEntered", "toast.openAdminEnteredToken",
            "backend.openKeyRefused", "backend.openKeyRevoked", "core.agent.openAdminUnsupported",
            // The agent update: the texts the page picks between by a condition (the agent on this PC or a
            // remote one, an update or only the restart that finishes one) — never a literal key check_i18n sees.
            "server.banner.agentUpdateLocal", "server.banner.agentUpdateRemote", "server.banner.agentUpdateRestart",
            "server.banner.agentUpdateRestartNow", "agentCfg.updateNote", "agentUpdate.title.local", "agentUpdate.title.running",
            "agentUpdate.done.local", "agentUpdate.done.remote", "confirm.agentUpdate.title", "confirm.agentUpdate.titleRestart",
            "confirm.agentUpdate.body", "confirm.agentUpdate.bodyRestart", "confirm.agentUpdate.yes", "confirm.agentUpdate.yesRestart",
        })
            Check($"i18n: en.json has {uiKey}", L.Has(uiKey));

        // Both installers rewrite the box's config from a heredoc, then re-append every stored key NOT in
        // their managed list. A heredoc key missing from that list (a lost space fuses two names) comes back
        // with its OLD value after the new one, and the old one wins (last assignment): the list and the
        // heredoc must name exactly the same keys.
        string agentDirCfg = Path.GetDirectoryName(agentPy)!;
        (List<string> heredoc, List<string> managed, string raw) InstallerKeys(string file, string heredocPattern, string managedPattern, string tokenPattern)
        {
            string path = Path.Combine(agentDirCfg, file);
            if (!File.Exists(path)) return ([], [], "");
            string text = File.ReadAllText(path).Replace("\r\n", "\n");
            var body = System.Text.RegularExpressions.Regex.Match(text, heredocPattern, System.Text.RegularExpressions.RegexOptions.Singleline);
            var list = System.Text.RegularExpressions.Regex.Match(text, managedPattern);
            var keys = body.Success
                ? System.Text.RegularExpressions.Regex.Matches(body.Groups[1].Value, @"^(GIO_[A-Z0-9_]+)=", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList()
                : [];
            var names = list.Success
                ? System.Text.RegularExpressions.Regex.Matches(list.Groups[1].Value, tokenPattern).Select(m => m.Groups[1].Value).ToList()
                : [];
            return (keys, names, list.Success ? list.Groups[1].Value : "");
        }
        foreach (var (file, heredocPattern, managedPattern, tokenPattern, bashCase) in new[]
        {
            ("install_agent.sh", @"cat > /etc/gio-agent/config <<EOF\n(.*?)\nEOF", "MANAGED=\"([^\"]*)\"", @"(\S+)", true),
            ("deploy_from_windows.ps1", @"cat > /etc/gio-agent/config <<'EOF'\n(.*?)\nEOF", @"\$managed = @\(([^)]*)\)", @"'([^']*)'", false),
        })
        {
            var (heredoc, managed, raw) = InstallerKeys(file, heredocPattern, managedPattern, tokenPattern);
            // bash tests membership as `case "$MANAGED" in *" $k "*`: a key counts only with a blank on BOTH
            // sides in the raw string, so a fused pair AND a list that lost its outer padding (first/last key
            // never matched, re-appended with the old value) both show up here; the token split alone
            // cannot see the second.
            var unmanaged = (bashCase ? heredoc.Where(k => !raw.Contains(" " + k + " ")) : heredoc.Except(managed)).ToList();
            var unknown = managed.Except(heredoc).ToList();
            Check($"installer: {file} manages exactly the {heredoc.Count} keys its config heredoc writes"
                  + (unmanaged.Count + unknown.Count > 0 ? " — unmanaged: " + string.Join(", ", unmanaged) + "; not written: " + string.Join(", ", unknown) : ""),
                heredoc.Count >= 18 && unmanaged.Count == 0 && unknown.Count == 0);
            // The sign key is a managed key of both installers, like the MUIP host — a key the launcher's
            // plan writes SET AND EMPTY must be one the installer knows, or the box's stored value would be
            // re-appended behind the blank and win.
            Check($"installer: {file} writes GIO_MUIP_KEY in its heredoc and manages it",
                heredoc.Contains("GIO_MUIP_KEY") && managed.Contains("GIO_MUIP_KEY"));
            // So are the MySQL root password, the Flask secret key and "Keep .env settings" — a value the
            // form (or a prompt) sets must replace the stored line, not be re-appended behind it: for
            // GIO_KEEP_ENV the stored line would come last and win, so an "off" could never replace a stored
            // "on". For bash the raw membership test again: the token split still finds a key that sits at
            // the end of MANAGED without its trailing blank, the installer's own `case` does not.
            foreach (var formKey in new[] { "GIO_MYSQL_ROOT_PASSWORD", "GIO_FLASK_SECRET_KEY", "GIO_KEEP_ENV" })
                Check($"installer: {file} writes {formKey} in its heredoc and manages it",
                    heredoc.Contains(formKey) && (bashCase ? raw.Contains(" " + formKey + " ") : managed.Contains(formKey)));
            int muipAt = heredoc.IndexOf("GIO_MUIP_KEY");
            Check($"installer: {file} writes the two secrets right after GIO_MUIP_KEY (the launcher plan's order)",
                muipAt >= 0 && heredoc.Skip(muipAt + 1).Take(2).SequenceEqual(new[] { "GIO_MYSQL_ROOT_PASSWORD", "GIO_FLASK_SECRET_KEY" }));
            // The download marks are installer-only: never in the heredoc, never managed.
            Check($"installer: {file} never writes GIO_FETCH_* into the config",
                !heredoc.Any(k => k.StartsWith("GIO_FETCH_", StringComparison.Ordinal)));
        }
        // deploy_from_windows.ps1 is started with `powershell -File`, i.e. Windows PowerShell 5.1, which reads
        // a BOM-less script in the ANSI code page: the last byte of a UTF-8 en / em dash (the texts in en.json
        // are full of them) decodes there to a curly quote that ENDS a double-quoted string — while pwsh 7
        // parses the same file without a word. So: every byte below 0x80, which rules out a BOM as well.
        string ps1Path = Path.Combine(agentDirCfg, "deploy_from_windows.ps1");
        byte[] ps1Bytes = File.Exists(ps1Path) ? File.ReadAllBytes(ps1Path) : [];
        int ps1Bad = Array.FindIndex(ps1Bytes, b => b >= 0x80);
        Check("installer: deploy_from_windows.ps1 is pure ASCII (Windows PowerShell 5.1 reads it in the ANSI code page)"
              + (ps1Bad >= 0 ? $" — byte 0x{ps1Bytes[ps1Bad]:x2} on line {ps1Bytes.Take(ps1Bad).Count(b => b == (byte)'\n') + 1}" : ""),
            ps1Bytes.Length > 0 && ps1Bad < 0);
        // One rule on every input surface: the launcher's form, the agent (its config at import,
        // the Secrets route) and both installers hold a secret to the SAME pattern — a value one of them
        // accepts and the agent then drops with a warning installs "random" behind the admin's back.
        foreach (var (what, pattern) in new[] { ("secret value", AgentInstallPlan.SecretValuePattern), ("MySQL root password", AgentInstallPlan.MysqlPasswordPattern) })
        {
            string core = pattern.TrimStart('^').TrimEnd('$');
            var without = new[] { "gio_agent.py", "install_agent.sh", "deploy_from_windows.ps1" }
                .Where(f => !File.Exists(Path.Combine(agentDirCfg, f)) || !File.ReadAllText(Path.Combine(agentDirCfg, f)).Contains(core, StringComparison.Ordinal)).ToList();
            Check($"rule: the agent and both installers carry the launcher's {what} pattern {core}"
                  + (without.Count > 0 ? " — not in: " + string.Join(", ", without) : ""),
                without.Count == 0);
        }
        // …and the WIRING. The two checks above only prove each pattern occurs SOMEWHERE in each file, and in
        // install_agent.sh the patterns live in two helper functions, not at the prompts: a prompt handed the
        // wrong helper passes them — the MySQL root password held to the general rule (a digit-first value is
        // written and the agent drops it), the Flask key held to the MySQL one (a digit-first key the launcher
        // accepts stops a --yes run). So does deploy_from_windows.ps1 with the MySQL pattern at its Flask
        // prompt, and either file with a pattern that lost its ^ … $ anchors. Hence, per installer: each
        // prompt names its key and its rule, each helper / $secretRe holds its pattern, anchored. Regex level,
        // so the label of a prompt stays free to be reworded. The MUIP sign key's re-ask loop is in the list
        // because it must not carry a bare [[ =~ ]] of its own instead of secret_ok, which pins
        // LC_ALL=C: a bare one is locale-dependent (under tr_TR.UTF-8 it refuses an ASCII i / I, under most
        // UTF-8 locales it accepts accented letters the agent drops).
        string secretCore = System.Text.RegularExpressions.Regex.Escape(AgentInstallPlan.SecretValuePattern.TrimStart('^').TrimEnd('$'));
        string mysqlCore = System.Text.RegularExpressions.Regex.Escape(AgentInstallPlan.MysqlPasswordPattern.TrimStart('^').TrimEnd('$'));
        string InstallerText(string file)
        {
            string path = Path.Combine(agentDirCfg, file);
            return File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
        }
        string shText = InstallerText("install_agent.sh"), ps1Text = InstallerText("deploy_from_windows.ps1");
        foreach (var (what, text, pattern) in new[]
        {
            ("install_agent.sh asks the MySQL root password through mysql_pw_ok", shText,
                """^ask_secret "[^"\n]*" GIO_MYSQL_ROOT_PASSWORD \S+ mysql_pw_ok """),
            ("install_agent.sh asks the Flask secret key through secret_ok", shText,
                """^ask_secret "[^"\n]*" GIO_FLASK_SECRET_KEY \S+ secret_ok """),
            ("install_agent.sh tests the MUIP sign key with secret_ok (no bare, locale-dependent [[ =~ ]])", shText,
                """^while .*! secret_ok "\$MUIPKEY";"""),
            ("install_agent.sh: mysql_pw_ok holds the launcher's MySQL root password pattern, anchored", shText,
                @"^mysql_pw_ok\(\) \(.*\^" + mysqlCore + @"\$"),
            ("install_agent.sh: secret_ok holds the launcher's secret value pattern, anchored", shText,
                @"^secret_ok\(\) \(.*\^" + secretCore + @"\$"),
            ("deploy_from_windows.ps1 asks the MySQL root password with the launcher's MySQL root password pattern, anchored", ps1Text,
                """^\$mysqlPw = AskSecret "[^"\n]*" \(DefOr 'GIO_MYSQL_ROOT_PASSWORD' ""\) '\^""" + mysqlCore + """\$' """),
            ("deploy_from_windows.ps1 asks the Flask secret key with $secretRe", ps1Text,
                """^\$flaskKey = AskSecret "[^"\n]*" \(DefOr 'GIO_FLASK_SECRET_KEY' ""\) \$secretRe """),
            ("deploy_from_windows.ps1 asks the MUIP sign key with $secretRe", ps1Text,
                """^\$muipKey = AskSecret "[^"\n]*" \(DefOr 'GIO_MUIP_KEY' ""\) \$secretRe """),
            ("deploy_from_windows.ps1: $secretRe is the launcher's secret value pattern, anchored", ps1Text,
                @"^\$secretRe = '\^" + secretCore + @"\$'"),
        })
            Check("wiring: " + what, System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.Multiline));
        // The token line the redaction check samples further up is a copy of what install_agent.sh prints
        // when an upgrade run replaces the box's token: the note itself, behind the summary's "token   :"
        // label. A copy that drifted from the script would prove nothing about the line an admin is shown.
        const string tokenLabel = "  token   : ";
        Check("installer: install_agent.sh prints the token note the redaction check samples — " + tokenChangesLine.Trim(),
            tokenChangesLine.StartsWith(tokenLabel, StringComparison.Ordinal)
            && shText.Contains(tokenChangesLine[tokenLabel.Length..], StringComparison.Ordinal)
            && shText.Contains(tokenLabel + "$TOKEN_NOTE", StringComparison.Ordinal));
        // "Keep .env settings" is read on both sides too: by the agent (GIO_KEEP_ENV, a switch that is off
        // unless its value is one of the agent's _TRUTHY spellings) and by the launcher out of the config of
        // the agent on this PC (LocalAgentConfig.KeepEnv, the re-install form's toggle). The two lists must
        // be one: a spelling only the agent takes as "on" would show the toggle off, and the next install
        // would write GIO_KEEP_ENV=0 over it.
        var truthyTuple = File.Exists(agentPy)
            ? System.Text.RegularExpressions.Regex.Match(File.ReadAllText(agentPy), @"^_TRUTHY = \(([^)]*)\)", System.Text.RegularExpressions.RegexOptions.Multiline)
            : null;
        var truthy = truthyTuple is { Success: true }
            ? System.Text.RegularExpressions.Regex.Matches(truthyTuple.Groups[1].Value, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList()
            : new List<string>();
        bool ReadsOn(string spelling) => new LocalAgentConfig(new Dictionary<string, string> { ["GIO_KEEP_ENV"] = spelling }).KeepEnv;
        Check($"rule: LocalAgentConfig.KeepEnv is on for exactly the agent's _TRUTHY spellings ({string.Join(" / ", truthy)})",
            truthy.Count > 0 && truthy.All(ReadsOn) && new[] { "1", "true", "yes", "on" }.All(truthy.Contains));

        // The install form's secret fields reach the plan under ONE name. app.js sends the whole form object
        // and ReadPlan (Relic.App, which the spikes do not reference) reads each field by name with Str(),
        // which answers "" for a field that is not there: a typo on either side installs "random" while the
        // admin believes a value was set, and nothing fails. Text-level, like the installer checks above.
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string appJsPath = Path.Combine(repoRoot, "app", "Relic.App", "ui", "app.js");
        string modesPath = Path.Combine(repoRoot, "app", "Relic.App", "Backend.Modes.cs");
        string appJs = File.Exists(appJsPath) ? File.ReadAllText(appJsPath) : "";
        string modesCs = File.Exists(modesPath) ? File.ReadAllText(modesPath) : "";
        foreach (var (formField, planProperty) in new[] { ("muipKey", "MuipKey"), ("mysqlPassword", "MysqlPassword"), ("flaskKey", "FlaskKey") })
            Check($"form: \"{formField}\" is one name on both sides — app.js field('{formField}', …) and ReadPlan's {planProperty} = Str(p, \"{formField}\")",
                appJs.Contains($"field('{formField}'", StringComparison.Ordinal)
                && modesCs.Contains($"{planProperty} = Str(p, \"{formField}\")", StringComparison.Ordinal));
        // "Keep .env settings" travels the same way but as a JSON bool or null, read with BoolOrNull — which
        // answers null ("not stated": the box keeps its stored choice) for a field that is not there, so a
        // typo on either side would quietly leave the option as it was, whatever the admin chose. The
        // re-install form's toggle is pre-set from localagent.status, under the same name. app.js spells
        // the name in three places that each have to hold: the toggle that sets the form's field, the
        // payload deployStart builds, and that pre-set. Each is pinned by its own text — the bare word
        // "keepEnv" stands on a dozen other lines of the file and would prove none of them.
        foreach (var (what, text) in new[]
        {
            ("the form's toggle sets the field keepEnv", "data-act=\"deployToggle\" data-arg=\"keepEnv\""),
            ("the install payload carries keepEnv", "f, { keepEnv });"),
            ("the re-install form takes keepEnv from localagent.status", "typeof st.keepEnv === 'boolean'"),
        })
            Check($"form: \"keepEnv\" in app.js — {what} ({text})", appJs.Contains(text, StringComparison.Ordinal));
        Check("form: \"keepEnv\" is one name on both sides — ReadPlan reads KeepEnv = BoolOrNull(p, \"keepEnv\")",
            modesCs.Contains("KeepEnv = BoolOrNull(p, \"keepEnv\")", StringComparison.Ordinal));
        Check("form: localagent.status answers keepEnv from the installed config (false without one)",
            modesCs.Contains("keepEnv = c?.KeepEnv ?? false", StringComparison.Ordinal));
        // With the option on the plan carries no bind IP, so the host saved for the agent on this PC has to
        // come from the config the install just wrote: its stack folders lead to each stack's own OUTER_IP,
        // and the merge kept the stored bind IP line for when they name none — the plan's own field may be
        // blank or stale.
        Check("install: the host saved for the agent on this PC is read back from the config just written",
            modesCs.Contains("string host = LocalAgent.ReadConfig()?.Host ?? LocalAgentConfig.HostFor(plan.BindIp, plan.ListenHost);", StringComparison.Ordinal));
        // A re-install over a running agent writes the new config, and LocalAgent.StartAsync leaves a healthy
        // process alone: nothing of the file is in effect until that agent restarts. The launcher does not
        // restart it (a running job would be killed), so the install log has to say it — for the process
        // the pid file named BEFORE the install, when the start hands back that same pid.
        int pidBeforeAt = modesCs.IndexOf("var before = LocalAgent.RunningPid();", StringComparison.Ordinal);
        Check("install: a re-install over a running agent says that the configuration just written applies after a restart",
            pidBeforeAt >= 0 && pidBeforeAt < modesCs.IndexOf("LocalAgent.Install(plan, python, log);", StringComparison.Ordinal)
            && modesCs.Contains("if (before.Running && pid == before.Pid) log(L.T(\"backend.localAgent.restartToApply\"));", StringComparison.Ordinal)
            && L.Has("backend.localAgent.restartToApply"));

        // The Gameplay page and the Backend meet under three rpc names and ONE job kind: the page starts the
        // job with runServerJob('tweaks', …) — rpc "server.tweaks" — and matches the events the Backend streams
        // back by that kind, which SRVJOBS turns into the job's title. Another word on either side leaves the
        // page waiting on a job whose log and outcome arrive under another name, or is answered "Unknown
        // message type". The Backend half also has to hold what cannot be run from here: both agent calls behind
        // RequireAdmin, and the read's 404 (an agent without the route) coming back as {tooOld: true}.
        string backendPath = Path.Combine(repoRoot, "app", "Relic.App", "Backend.cs");
        string backendCs = File.Exists(backendPath) ? File.ReadAllText(backendPath) : "";
        int twCaseAt = backendCs.IndexOf("case \"server.tweaks\":", StringComparison.Ordinal);
        int twAdminAt = twCaseAt < 0 ? -1 : backendCs.IndexOf("RequireAdmin();", twCaseAt, StringComparison.Ordinal);
        int twJobAt = backendCs.IndexOf("return StartServerJob(\"tweaks\", Str(p, \"version\"), (a, v, log) => a.TweaksSetAsync(v, tweaks, log));", StringComparison.Ordinal);
        int twReadAt = modesCs.IndexOf("private async Task<object?> TweaksGetAsync(string version)", StringComparison.Ordinal);
        Check("tweaks: Backend answers server.tweaks.get, server.tweaks and tweakdata.get — the agent calls behind RequireAdmin, the job under the kind \"tweaks\", a 404 on the read as {tooOld: true}",
            backendCs.Contains("case \"server.tweaks.get\": RequireAdmin(); return await TweaksGetAsync(Str(p, \"version\"));", StringComparison.Ordinal)
            && twCaseAt >= 0 && twCaseAt < twAdminAt && twAdminAt < twJobAt
            && backendCs.Contains("case \"tweakdata.get\":", StringComparison.Ordinal)
            && twReadAt >= 0 && modesCs.IndexOf("catch (AgentHttpException ex) when (ex.Status == 404) { return new { tooOld = true }; }", twReadAt, StringComparison.Ordinal) > twReadAt);
        // The page's half, over every script of the UI folder (the page need not live in app.js).
        string uiDir = Path.Combine(repoRoot, "app", "Relic.App", "ui");
        string uiJs = Directory.Exists(uiDir) ? string.Concat(Directory.GetFiles(uiDir, "*.js").Select(File.ReadAllText)) : "";
        bool UiCalls(string function, string name) =>
            uiJs.Contains($"{function}('{name}'", StringComparison.Ordinal) || uiJs.Contains($"{function}(\"{name}\"", StringComparison.Ordinal);
        Check("tweaks: the UI uses the same names — rpc 'server.tweaks.get', rpc 'tweakdata.get', the job kind 'tweaks' (runServerJob), and server.job.tweaks as its title key",
            UiCalls("rpc", "server.tweaks.get") && UiCalls("rpc", "tweakdata.get")
            && (UiCalls("runServerJob", "tweaks") || UiCalls("rpc", "server.tweaks"))
            && uiJs.Contains("server.job.tweaks", StringComparison.Ordinal));
        // The agent update: three rpc names and one family of events between the page and the Backend. The
        // start screen offers the update of the agent on this PC before any mode is chosen, so agent.build
        // and localagent.update cannot sit behind RequireAdmin (the files on this PC are the boundary, as
        // for localagent.enter); the update of a remote agent does.
        Check("agent update: Backend answers agent.build and localagent.update without a mode and agent.update behind RequireAdmin; the page calls all three and listens for agentupdate.log / .done / .error, which the Backend posts",
            backendCs.Contains("case \"agent.build\": return await AgentBuildAsync();", StringComparison.Ordinal)
            && backendCs.Contains("case \"localagent.update\": return StartLocalAgentUpdate();", StringComparison.Ordinal)
            && backendCs.Contains("case \"agent.update\": RequireAdmin(); return StartRemoteAgentUpdate(p);", StringComparison.Ordinal)
            && UiCalls("rpc", "agent.build") && UiCalls("rpc", "localagent.update") && UiCalls("rpc", "agent.update")
            && new[] { "agentupdate.log", "agentupdate.done", "agentupdate.error" }.All(ev =>
                UiCalls("on", ev) && modesCs.Contains($"PostEvent(\"{ev}\"", StringComparison.Ordinal)));
        // Over SSH only the Linux installer can run: a Windows agent that is not this PC's is updated by the
        // launcher on the PC it runs on. The page never offers it the SSH form (a note without a button),
        // and the Backend asks the agent before anything is uploaded — its platform, and a job in progress.
        int updRefuseAt = modesCs.IndexOf("await RefuseRemoteUpdateAsync(cfg);", StringComparison.Ordinal);
        Check("agent update: a Windows agent elsewhere is not offered the update over SSH — the page gates the offer on the platform, the Backend refuses it (and a busy agent) before the upload",
            appJs.Contains("(!S.admin.openKey && S.srv.info.platform !== 'windows')", StringComparison.Ordinal)
            && appJs.Contains("T('server.banner.agentUpdateWindows')", StringComparison.Ordinal)
            && updRefuseAt >= 0 && updRefuseAt < modesCs.IndexOf("var result = AgentDeployer.Upgrade(ssh,", StringComparison.Ordinal)
            && modesCs.Contains("throw new InvalidOperationException(L.T(\"backend.agent.updateWindowsRemote\"));", StringComparison.Ordinal)
            && modesCs.Contains("throw new InvalidOperationException(L.T(\"backend.agent.updateJobRunning\", new { kind }));", StringComparison.Ordinal));
        // A remote run can fail AFTER the installer replaced the agent's files; the Backend knows it from the
        // line the installer prints right before it does, and the page then reads everything again.
        string updInstallerSrc = File.Exists(Path.Combine(Path.GetDirectoryName(agentPy)!, "install_agent.sh"))
            ? File.ReadAllText(Path.Combine(Path.GetDirectoryName(agentPy)!, "install_agent.sh")) : "";
        int updPrevLineAt = updInstallerSrc.IndexOf("echo \"  previous :${KEPT} kept in $PREV\"", StringComparison.Ordinal);
        Check("agent update: a run over SSH that fails after the installer began to replace the agent's files says so to the page (the installer's \"previous :\" line comes before the first file it replaces)",
            modesCs.Contains("if (s.TrimStart().StartsWith(\"previous :\", StringComparison.Ordinal)) replacing = true;", StringComparison.Ordinal)
            && modesCs.Contains("PostEvent(\"agentupdate.error\", new { target = \"remote\", message = ex.Message, replaced = replacing });", StringComparison.Ordinal)
            && appJs.Contains("on('agentupdate.error', (d) => agentUpdateFailed(d && d.message, !!(d && d.replaced)));", StringComparison.Ordinal)
            && updPrevLineAt >= 0 && updPrevLineAt < updInstallerSrc.IndexOf("install -m 0644 \"$here/gio_agent.py\" /opt/gio-agent/gio_agent.py", StringComparison.Ordinal));
        // A finished run on this PC is about the status target's agent only when the admin server IS the
        // agent on this PC: an update of another agent here must not drop that server's cards and drafts.
        Check("agent update: a finished run drops the admin server's cards only when it was about that server's agent — a run over SSH, or one on this PC while the admin server is the agent on this PC",
            appJs.Contains("agentUpdateEnded(u, !!u && (u.target === 'remote' || !!(S.agentBuild.local && S.agentBuild.local.adminServer)));", StringComparison.Ordinal));
        // An update must leave the saved connection alone: the box keeps its token, so nothing here may save
        // a ServerConfig or switch the mode the way an install does.
        int updLocalAt = modesCs.IndexOf("private object StartLocalAgentUpdate()", StringComparison.Ordinal);
        int updConfirmAt = modesCs.IndexOf("private static async Task<string?> ConfirmUpdatedAsync(", StringComparison.Ordinal);
        string updCode = updLocalAt >= 0 && updConfirmAt > updLocalAt ? modesCs[updLocalAt..updConfirmAt] : "ServerConfigStore.Save";
        Check("agent update: neither update saves a connection, applies an installed agent or sends a token to the page",
            !updCode.Contains("ServerConfigStore.Save", StringComparison.Ordinal) && !updCode.Contains("ApplyInstalledAgent", StringComparison.Ordinal)
            && !updCode.Contains("token =", StringComparison.Ordinal) && !updCode.Contains(".Token", StringComparison.Ordinal));
        // Over SSH the installer runs as a plain upgrade: install.env holds the sentinel and at most the one
        // installer-only switch — a configuration key in it would replace the box's own value (a token the
        // box then no longer accepts, a listen address) —, the box must prove it has an agent first, and
        // no package is installed.
        string deployerPath = Path.Combine(repoRoot, "app", "Relic.Core", "Server", "AgentDeployer.cs");
        string deployerCs = File.Exists(deployerPath) ? File.ReadAllText(deployerPath) : "";
        Check("agent update: over SSH install.env carries no configuration key, the box must already have an agent, and the installer runs with --yes --no-deps",
            deployerCs.Contains("UploadText(session.Sftp, \"GIO_RELIC_ENV=1\\n\" + (force ? \"GIO_UPGRADE_FORCE=y\\n\" : \"\"), envFile);", StringComparison.Ordinal)
            && deployerCs.Contains("[ -f /etc/gio-agent/config ] && [ -f /opt/gio-agent/gio_agent.py ] || exit {NoAgentExit}; ", StringComparison.Ordinal)
            && deployerCs.Contains("exec bash ./install_agent.sh --yes --no-deps\"", StringComparison.Ordinal));
        // Both ends of the identity: the agent names it in its admin status and nowhere public, and the
        // Linux installer keeps what a box added to its payloads the way the launcher does on this PC.
        string updAgentSrc = File.Exists(agentPy) ? File.ReadAllText(agentPy) : "";
        string updInstallerPath = Path.Combine(Path.GetDirectoryName(agentPy)!, "install_agent.sh");
        string updInstaller = File.Exists(updInstallerPath) ? File.ReadAllText(updInstallerPath) : "";
        int updPublicAt = updAgentSrc.IndexOf("\ndef build_public_status(", StringComparison.Ordinal);
        int updPublicEnd = updPublicAt < 0 ? -1 : updAgentSrc.IndexOf("\ndef ", updPublicAt + 5, StringComparison.Ordinal);
        Check("agent update: the agent names its build identity in GET /status (out[\"build\"]) and the public projection does not read it",
            updAgentSrc.Contains("out[\"build\"] = agent_build()", StringComparison.Ordinal) && updPublicAt >= 0 && updPublicEnd > updPublicAt
            && !updAgentSrc[updPublicAt..updPublicEnd].Contains("build\"", StringComparison.Ordinal));
        Check("agent update: install_agent.sh replaces the payloads through carry_box_payloads — what the box added is kept, as LocalAgent.CarryBoxPayloads keeps it",
            updInstaller.Contains("carry_box_payloads() {", StringComparison.Ordinal)
            && updInstaller.Contains("CARRIED=$(carry_box_payloads /opt/gio-agent/payloads \"$NEWPAY\")", StringComparison.Ordinal)
            && updInstaller.Contains("navmesh*|*.zip|.relic-navmesh-zip-refused)", StringComparison.Ordinal));
        // Open administration: the Player page enters Server Admin mode with the key the agent hands out
        // (rpc admin.openEnter) and the Server page gives it up once this launcher closed the option (rpc
        // admin.openForget). Neither may sit behind RequireAdmin — the first is called from Player mode, the
        // second after a revocation already turned the launcher to Player. The revocation itself lives in
        // WithAgent: a 401 on an open key forgets it and tells the UI through the admin.revoked event, while
        // a refused typed or baked token keeps its error (the admin may have rotated the token on the box).
        static bool CaseFreeOfRequireAdmin(string src, int at)
        {
            if (at < 0) return false;
            int next = src.IndexOf("case \"", at + 6, StringComparison.Ordinal);
            return !(next < 0 ? src[at..] : src[at..next]).Contains("RequireAdmin();", StringComparison.Ordinal);
        }
        int oeAt = backendCs.IndexOf("case \"admin.openEnter\":", StringComparison.Ordinal);
        int ofAt = backendCs.IndexOf("case \"admin.openForget\":", StringComparison.Ordinal);
        Check("open admin: Backend answers admin.openEnter and admin.openForget, neither behind RequireAdmin",
            backendCs.Contains("case \"admin.openEnter\": return await OpenAdminEnterAsync();", StringComparison.Ordinal)
            && backendCs.Contains("case \"admin.openForget\": return OpenAdminForget(out _);", StringComparison.Ordinal)
            && CaseFreeOfRequireAdmin(backendCs, oeAt) && CaseFreeOfRequireAdmin(backendCs, ofAt));
        // The revocation posts its event only for a key it really forgot: the configured server may have changed
        // while the refused call was in flight, and the forget then has nothing to forget — the 401 is the old
        // server's own error, and an event would name the new one.
        int withAgentAt = backendCs.IndexOf("private async Task<object?> WithAgent(", StringComparison.Ordinal);
        int revokeAt = withAgentAt < 0 ? -1 : backendCs.IndexOf("catch (AgentHttpException ex) when (ex.Status == 401 && cfg.OpenKey)", withAgentAt, StringComparison.Ordinal);
        int forgotAt = revokeAt < 0 ? -1 : backendCs.IndexOf("var state = OpenAdminForget(out bool forgot);", revokeAt, StringComparison.Ordinal);
        int rethrowAt = forgotAt < 0 ? -1 : backendCs.IndexOf("if (!forgot) throw;", forgotAt, StringComparison.Ordinal);
        int eventAt = rethrowAt < 0 ? -1 : backendCs.IndexOf("PostEvent(\"admin.revoked\", new { state });", rethrowAt, StringComparison.Ordinal);
        Check("open admin: a 401 on an open key inside WithAgent forgets the key, and only a real forget posts admin.revoked and throws backend.openKeyRevoked — else the call's own 401 is rethrown",
            revokeAt > withAgentAt && forgotAt > revokeAt && rethrowAt > forgotAt && eventAt > rethrowAt
            && backendCs.IndexOf("throw new InvalidOperationException(L.T(\"backend.openKeyRevoked\"));", eventAt, StringComparison.Ordinal) > eventAt);
        // The forget itself: memory first (the mode and the connection in effect are what every following call
        // reads), then the two files, each inside a try and never fatal — a state file that cannot be written
        // right now would otherwise reject the rpc and swallow the revocation (WithAgent posts its event only
        // once the forget returns) while the backend already runs in Player mode; a leftover record lends no
        // token and is ignored.
        int forgetAt = modesCs.IndexOf("private object OpenAdminForget(out bool forgot)", StringComparison.Ordinal);
        int forgetModeAt = forgetAt < 0 ? -1 : modesCs.IndexOf("_state.Settings.Mode = \"player\";", forgetAt, StringComparison.Ordinal);
        int forgetSaveAt = forgetModeAt < 0 ? -1 : modesCs.IndexOf("try { _state.Save(); }", forgetModeAt, StringComparison.Ordinal);
        int forgetSaveCatchAt = forgetSaveAt < 0 ? -1 : modesCs.IndexOf("catch (Exception ex) { Log.Error(\"persisting the return to Player mode\", ex); }", forgetSaveAt, StringComparison.Ordinal);
        int forgetDeleteAt = forgetSaveCatchAt < 0 ? -1 : modesCs.IndexOf("try { ServerConfigStore.Delete(); }", forgetSaveCatchAt, StringComparison.Ordinal);
        int forgetDeleteCatchAt = forgetDeleteAt < 0 ? -1 : modesCs.IndexOf("catch (Exception ex) { Log.Error(\"forgetting the open key's file\", ex); }", forgetDeleteAt, StringComparison.Ordinal);
        int forgetEndAt = forgetDeleteCatchAt < 0 ? -1 : modesCs.IndexOf("return BuildInitState();", forgetDeleteCatchAt, StringComparison.Ordinal);
        Check("open admin: OpenAdminForget answers whether it forgot, forgets in memory first, then saves the state and deletes the key's file — each inside a try, no bare save",
            forgetAt >= 0 && forgetModeAt > forgetAt && forgetSaveAt > forgetModeAt && forgetSaveCatchAt > forgetSaveAt
            && forgetDeleteAt > forgetSaveCatchAt && forgetDeleteCatchAt > forgetDeleteAt && forgetEndAt > forgetDeleteCatchAt
            && !modesCs[forgetAt..forgetEndAt].Replace("try { _state.Save(); }", "", StringComparison.Ordinal).Contains("_state.Save(", StringComparison.Ordinal));
        // The entry: a real token saved for the same host enters with that token — validated, nothing fetched,
        // nothing saved, openKey false in the reply — before the key is ever asked for. A 401 on that token is a
        // dead record (the agent's token was rotated since; nothing else in the launcher drops one) and falls
        // through to the key path, whose Save replaces it — never backend.tokenRefused, which would lock the
        // launcher out of the open path for good; an unreachable agent proves nothing against the token and keeps
        // its refusal. The key's own path validates it on /status before anything is saved, refuses a 401 as
        // backend.openKeyRefused, never enters unreachable and answers openKey true.
        int enterAt = modesCs.IndexOf("private async Task<object> OpenAdminEnterAsync()", StringComparison.Ordinal);
        int keyAt = enterAt < 0 ? -1 : modesCs.IndexOf("key = await pub.OpenAdminKeyAsync();", enterAt, StringComparison.Ordinal);
        string entryHead = enterAt >= 0 && keyAt > enterAt ? modesCs[enterAt..keyAt] : "";
        int storedRefusedAt = entryHead.IndexOf("catch (AgentHttpException ex) when (ex.Status == 401)", StringComparison.Ordinal);
        int storedDeadAt = storedRefusedAt < 0 ? -1 : entryHead.IndexOf("tokenRefused = true;", storedRefusedAt, StringComparison.Ordinal);
        int storedUnreachableAt = storedDeadAt < 0 ? -1 : entryHead.IndexOf("throw new InvalidOperationException(L.T(\"backend.agentUnreachable\"", storedDeadAt, StringComparison.Ordinal);
        int storedEnterAt = storedUnreachableAt < 0 ? -1 : entryHead.IndexOf("if (!tokenRefused)", storedUnreachableAt, StringComparison.Ordinal);
        Check("open admin: a token already saved for the host enters with that token (validated; unreachable = not entered), fetching and saving no key, openKey false",
            entryHead.Contains("is { OpenKey: false } st && ServerConfig.SameHost(st.Host, host)", StringComparison.Ordinal)
            && storedUnreachableAt > storedDeadAt && storedEnterAt > storedUnreachableAt
            && entryHead.IndexOf("return new { state = BuildInitState(), warning = (string?)null, openKey = false };", storedEnterAt, StringComparison.Ordinal) > storedEnterAt
            && !entryHead.Contains("ServerConfigStore.Save(", StringComparison.Ordinal));
        Check("open admin: a stored token the agent refuses (401) is a dead record — the entry falls through to the key path instead of backend.tokenRefused",
            storedRefusedAt >= 0 && storedDeadAt > storedRefusedAt
            && !entryHead.Contains("backend.tokenRefused", StringComparison.Ordinal)
            && !entryHead[storedRefusedAt..storedDeadAt].Contains("throw ", StringComparison.Ordinal));
        Check("open admin: the entry validates the key on /status before anything is saved, refusing a 401 as backend.openKeyRefused and never entering unreachable, openKey true",
            keyAt >= 0
            && modesCs.IndexOf("throw new InvalidOperationException(L.T(\"backend.openKeyRefused\"));", keyAt, StringComparison.Ordinal) is var refusedAt && refusedAt > keyAt
            && modesCs.IndexOf("ServerConfigStore.Save(cfg);", refusedAt, StringComparison.Ordinal) is var saveAt && saveAt > refusedAt
            && !modesCs[keyAt..saveAt].Contains("warning = L.T(", StringComparison.Ordinal)
            && modesCs.IndexOf("return new { state = BuildInitState(), warning = (string?)null, openKey = true };", saveAt, StringComparison.Ordinal) > saveAt);
        // A refused bearer while a job is followed is never transient: the follow hands the 401 to its caller at
        // once (WithAgent's revocation) instead of ten retries and the "job link lost" text that would hide it.
        string agentClientPath = Path.Combine(repoRoot, "app", "Relic.Core", "Server", "AgentClient.cs");
        string agentClientCs = File.Exists(agentClientPath) ? File.ReadAllText(agentClientPath) : "";
        int followAt = agentClientCs.IndexOf("private async Task<JsonElement> FollowJobAsync(", StringComparison.Ordinal);
        Check("open admin: FollowJobAsync never retries a 401 (beside the 404), so a revocation during a job reaches WithAgent at once",
            followAt >= 0 && agentClientCs.IndexOf("ex is not AgentHttpException { Status: 404 or 401 }", followAt, StringComparison.Ordinal) > followAt);
        Check("open admin: the start screen never lends an open-key record as a saved token (AdminLoginAsync)",
            modesCs.Contains("ServerConfig.SameHost(st.Host, r.Host) && !st.OpenKey ? st : null;", StringComparison.Ordinal));
        Check("open admin: the init state tells the UI the bearer is an open key (admin.openKey)",
            backendCs.Contains("openKey = AdminServer()?.OpenKey == true,", StringComparison.Ordinal));
        Check("open admin: the UI calls both rpcs and handles the admin.revoked event",
            UiCalls("rpc", "admin.openEnter") && UiCalls("rpc", "admin.openForget") && UiCalls("on", "admin.revoked"));
        // Two label tables of the UI hold translation KEYS, which check_i18n.py does not see (it reads only a
        // key written as the literal argument of a translation call): SRVJOBS (job kind → title) and CATS (the
        // Game commands cards: name + hint). A job or a card added there without its text draws the raw key,
        // so every such key the scripts spell has to be in en.json.
        var tableKeys = System.Text.RegularExpressions.Regex.Matches(uiJs, @"'((?:server\.job|cmd\.cat)\.[A-Za-z.]+)'")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        var tableKeysMissing = tableKeys.Where(k => !L.Has(k)).ToList();
        Check($"i18n: every job title and command card key the UI's label tables hold ({tableKeys.Count}) has an en.json text"
              + (tableKeysMissing.Count > 0 ? " — missing: " + string.Join(", ", tableKeysMissing) : ""),
            tableKeys.Count >= 30 && tableKeysMissing.Count == 0);

        // ── pre-launch status parsing: "not on this server" vs stopped vs unknown ──
        // Only an explicit present:false is absent; a version the answer does not list stays unknown.
        static ServerProbeResult Pub(string json, string v)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return ServerProbe.ParsePublic(doc.RootElement, v);
        }
        static ServerProbeResult Adm(string json, string v)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return ServerProbe.Parse(doc.RootElement, v);
        }
        var otherUp = Pub("""{"versions":{"1.6":{"present":false},"2.8":{"present":true,"up":true,"healthy":true}}}""", "1.6");
        Check("public: present:false while another version runs = not hosted, and names what is",
            otherUp is { Up: false, Hosted: false, AllGood: false } && otherUp.HostedVersions == "2.8");
        var noneHosted = Pub("""{"versions":{"1.6":{"present":false},"2.8":{"present":false}}}""", "1.6");
        Check("public: present:false with nothing hosted = not hosted, empty list",
            noneHosted is { Up: false, Hosted: false } && noneHosted.HostedVersions == "");
        Check("public: a version the snapshot does not list is UNKNOWN, never absent",
            Pub("""{"versions":{"2.8":{"present":true,"up":true}}}""", "1.6") is { Up: null, Hosted: null });
        var stopped = Pub("""{"versions":{"1.6":{"present":true,"up":false,"healthy":false}}}""", "1.6");
        Check("public: present:true up:false = stopped, but hosted",
            stopped is { Up: false, Hosted: true } && stopped.HostedVersions == "1.6");
        Check("public: an older agent without present = hosted",
            Pub("""{"versions":{"1.6":{"up":true}}}""", "1.6") is { Up: true, Hosted: true, AllGood: true });
        // healthy:false names no services: degraded, but flagged so the dialog never shows the marker
        // as a service list ("down (services)").
        Check("public: up + healthy:false = degraded without a service list",
            Pub("""{"versions":{"1.6":{"present":true,"up":true,"healthy":false}}}""", "1.6")
                is { Up: true, AllGood: false, DegradedUnlisted: true });
        Check("public: up + healthy:true is not flagged as degraded",
            Pub("""{"versions":{"1.6":{"present":true,"up":true,"healthy":true}}}""", "1.6")
                is { AllGood: true, DegradedUnlisted: false });
        var admNamed = Adm("""{"versions":{"1.6":{"present":true,"up":true,"healthy":false,"servicesDown":["gameserver"]}}}""", "1.6");
        Check("admin: named dead services stay a list (not flagged unlisted)",
            admNamed is { Up: true, AllGood: false, DegradedUnlisted: false } && admNamed.Degraded == "gameserver");
        const string fog = """{"statusUnknown":true,"versions":{"1.6":{"present":true,"up":false,"healthy":false},"2.8":{"present":false}}}""";
        Check("public: statusUnknown (docker did not answer on the box) = unknown, not stopped",
            Pub(fog, "1.6") is { Up: null, AllGood: false });
        Check("public: present:false still wins under statusUnknown (a filesystem fact, not docker)",
            Pub(fog, "2.8") is { Up: false, Hosted: false });
        const string admFog = """{"error":"docker compose ls failed","versions":{"1.6":{"present":false,"up":false},"2.8":{"present":true,"up":false}}}""";
        var admAbsent = Adm(admFog, "1.6");
        Check("admin: present:false is read BEFORE the top-level docker error",
            admAbsent is { Up: false, Hosted: false } && admAbsent.HostedVersions == "2.8");
        Check("admin: a present version under the docker error stays unknown",
            Adm(admFog, "2.8") is { Up: null, Hosted: true });

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"server spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }
}

/// <summary>
/// LIVE, opt-in: installs the agent on a real Linux box through <see cref="AgentDeployer.Install"/>
/// (the same path the app's "Install the agent on a new server" uses): SSH key/password auth, sudo
/// for a non-root user, upload of agent/ + payloads, install_agent.sh --yes (selftest, systemd,
/// firewall), /health. Needs RELIC_SSH_HOST, RELIC_SSH_USER and RELIC_SSH_KEY or RELIC_SSH_PASS
/// (+ RELIC_SSH_SUDO for a sudo password); RELIC_SSH_FAKESTACK=1 creates an empty 1.6 stack skeleton
/// under ~/relic_fake_stack so the install passes the "server files present" gate on a box without
/// the vendor package. Changes the remote box (installs a systemd service) — never part of "all".
/// </summary>
static class DeploySpike
{
    public static int Run()
    {
        Console.WriteLine("== deploy spike (LIVE): AgentDeployer.Install over SSH ==");
        string host = Environment.GetEnvironmentVariable("RELIC_SSH_HOST") ?? "";
        string user = Environment.GetEnvironmentVariable("RELIC_SSH_USER") ?? "root";
        string key = Environment.GetEnvironmentVariable("RELIC_SSH_KEY") ?? "";
        string pass = Environment.GetEnvironmentVariable("RELIC_SSH_PASS") ?? "";
        string sudo = Environment.GetEnvironmentVariable("RELIC_SSH_SUDO") ?? "";
        bool fake = Environment.GetEnvironmentVariable("RELIC_SSH_FAKESTACK") == "1";
        if (host.Length == 0 || (key.Length == 0 && pass.Length == 0))
        {
            Console.WriteLine("  skipped: set RELIC_SSH_HOST + RELIC_SSH_KEY|RELIC_SSH_PASS (and RELIC_SSH_SUDO for a sudo user)");
            return 0;
        }
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
        var ssh = new SshTarget(host, 22, user, pass, key, "", sudo);
        string dir16 = "/home/1.6_live";
        if (fake)
        {
            // Skeleton of a vendor stack so MissingStackEntries passes; the agent reports it "present",
            // not bootstrapped (no compose.yml) — nothing is started.
            var ci = key.Length > 0
                ? new Renci.SshNet.ConnectionInfo(host, 22, user, new Renci.SshNet.PrivateKeyAuthenticationMethod(user, new Renci.SshNet.PrivateKeyFile(key)))
                : new Renci.SshNet.ConnectionInfo(host, 22, user, new Renci.SshNet.PasswordAuthenticationMethod(user, pass));
            using var c = new Renci.SshNet.SshClient(ci);
            c.Connect();
            string home = c.RunCommand("echo ~").Result.Trim();
            dir16 = home + "/relic_fake_stack/1.6_live";
            c.RunCommand($"mkdir -p '{dir16}/server' '{dir16}/sdk' '{dir16}/dockerfiles' && touch '{dir16}/docker-compose.yml.tmpl' '{dir16}/.env'").Execute();
            Console.WriteLine($"  fake stack skeleton at {dir16}");
        }
        var plan = new AgentInstallPlan
        {
            Target = AgentInstallPlan.TargetLinux, Dir16 = dir16, Dir28 = "", Listen = "0.0.0.0:18080",
            Token = AgentInstallPlan.NewToken(), ServerName = "relic-deploy-spike", BindIp = "", AdvertisedIp = "",
        };
        string agentDir = Path.Combine(AppContext.BaseDirectory, "agent");
        if (!File.Exists(Path.Combine(agentDir, "install_agent.sh")))
            agentDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "agent"));
        Console.WriteLine($"  agent dir: {agentDir}");
        try
        {
            var res = AgentDeployer.Install(ssh, plan, agentDir, line => Console.WriteLine("    | " + line));
            Check("install returned a host key fingerprint", res.HostKeyFingerprint.StartsWith("SHA256:"));
            Check("agent /health answered on the box", res.Health.Contains("gio-agent"));
            // Public status through the LAN/WAN path (the firewall rule must have been opened)
            try
            {
                using var pub = new AgentPublicClient(host, 18080, TimeSpan.FromSeconds(8));
                var st = pub.StatusAsync().GetAwaiter().GetResult();
                Check("public /public/status reachable from here", st.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True);
                Check("public status names the server", st.TryGetProperty("name", out var n) && n.GetString() == "relic-deploy-spike");
                Check("public status reports platform linux", st.TryGetProperty("platform", out var pl) && pl.GetString() == "linux");
            }
            catch (Exception ex) { Check($"public status reachable from here ({ex.Message})", false); }
            // Admin /status with the token
            try
            {
                var cfg = new ServerConfig(host, 22, user, "", plan.Token, 18080, "direct");
                var probe = ServerProbe.CheckAsync(cfg, "1.6").GetAwaiter().GetResult();
                Check($"admin /status answered (1.6 up={probe.Up?.ToString() ?? "unknown"})", probe.Up is not null);
            }
            catch (Exception ex) { Check($"admin status ({ex.Message})", false); }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  install failed: " + ex.Message);
            Check("install completed", false);
        }
        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"deploy spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }
}

/// <summary>
/// LIVE, opt-in: the Windows local agent path (<see cref="LocalAgent"/>): python detection, install
/// under a TEMP root (<see cref="LocalAgent.RootOverride"/> — never the developer's real
/// %LOCALAPPDATA%\Relic\agent, whose config, state and hotpatch mirror this would otherwise overwrite), start
/// with pythonw bound to 127.0.0.1 (no firewall prompt), /health + /public/status, stop; then the
/// autostart shape — an agent started WITHOUT a pid file — found by its listening port and stopped;
/// finally the uninstall (root gone, /health silent, the real Run value untouched). Needs
/// RELIC_LOCAL_DIR16 = an extracted vendor 1.6 stack folder (only its presence is checked — nothing
/// is bootstrapped or started in docker).
/// </summary>
static class LocalAgentSpike
{
    public static int Run()
    {
        Console.WriteLine("== localagent spike (LIVE): LocalAgent install/start/status/stop/uninstall under a temp root ==");
        string dir16 = Environment.GetEnvironmentVariable("RELIC_LOCAL_DIR16") ?? "";
        if (dir16.Length == 0) { Console.WriteLine("  skipped: set RELIC_LOCAL_DIR16 to an extracted 1.6 stack folder"); return 0; }
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
        var py = LocalAgent.FindPython();
        Check($"python >= 3.10 found ({py?.Exe} {py?.Version})", py is not null);
        if (py is null) return 1;
        const int port = 18099;
        var plan = new AgentInstallPlan
        {
            Target = AgentInstallPlan.TargetWindows, Dir16 = dir16, Dir28 = "", Listen = $"127.0.0.1:{port}",
            Token = AgentInstallPlan.NewToken(), ServerName = "relic-localagent-spike",
        };
        Check("plan validates", plan.Validate().Count == 0);
        Check("stack entries present", plan.MissingStackEntries(p => Directory.Exists(p) || File.Exists(p)).Count == 0);
        string tmpRoot = Path.Combine(Path.GetTempPath(), "relic-localagent-spike-" + Guid.NewGuid().ToString("n")[..8]);
        bool autostartBefore = LocalAgent.AutostartEnabled;
        LocalAgent.RootOverride = tmpRoot;
        try
        {
            Console.WriteLine($"  temp root: {tmpRoot}");
            LocalAgent.Stop(port);
            string agentDir = Path.Combine(AppContext.BaseDirectory, "agent");
            if (!File.Exists(Path.Combine(agentDir, "gio_agent.py")))
                agentDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "agent"));
            LocalAgent.Install(plan, py, line => Console.WriteLine("    | " + line), agentDir);
            Check("installed (config + script + payloads) under the temp root",
                LocalAgent.IsInstalled && File.Exists(LocalAgent.ConfigPath) && LocalAgent.ConfigPath.StartsWith(tmpRoot, StringComparison.OrdinalIgnoreCase));
            Check("ReadConfig reads back the plan (token, port, dir16)",
                LocalAgent.ReadConfig() is { } rc && rc.Token == plan.Token && rc.ListenPort == port && rc.Dir16 == dir16 && rc.Host == "127.0.0.1");
            int pid = LocalAgent.StartAsync(port, line => Console.WriteLine("    | " + line)).GetAwaiter().GetResult();
            Check($"started (pid {pid})", pid > 0);
            Check("healthy on 127.0.0.1", LocalAgent.IsHealthyAsync(port).GetAwaiter().GetResult());
            Check("ListeningPid finds the agent's own pid through the listener", LocalAgent.ListeningPid(port) == pid);
            using (var pub = new AgentPublicClient("127.0.0.1", port, TimeSpan.FromSeconds(5)))
            {
                var st = pub.StatusAsync().GetAwaiter().GetResult();
                Check("public status answers", st.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True);
                Check("public status: platform windows", st.TryGetProperty("platform", out var pl) && pl.GetString() == "windows");
                Check("public status: 1.6 present, not up", st.TryGetProperty("versions", out var vs) && vs.TryGetProperty("1.6", out var v16)
                    && v16.TryGetProperty("present", out var pr) && pr.ValueKind == JsonValueKind.True
                    && v16.TryGetProperty("up", out var up) && up.ValueKind == JsonValueKind.False);
            }
            var cfg = new ServerConfig("127.0.0.1", 22, "root", "", plan.Token, port, "direct");
            var probe = ServerProbe.CheckAsync(cfg, "1.6").GetAwaiter().GetResult();
            Check($"admin /status with the token (1.6 up={probe.Up})", probe.Up == false);
            var (running, _) = LocalAgent.RunningPid();
            Check("pid file tracks the process", running);
            LocalAgent.Stop(port, line => Console.WriteLine("    | " + line));
            Check("stopped: pid file gone, /health silent",
                !LocalAgent.RunningPid().Running && !LocalAgent.IsHealthyAsync(port).GetAwaiter().GetResult());

            // The autostart shape: pythonw started by the HKCU Run value writes no pid file. Start one the
            // same way (Process.Start, no pid file) — FindRunning must see it through the listener and
            // Stop(port) must end it.
            var psi = new System.Diagnostics.ProcessStartInfo(py.ExeW)
            {
                Arguments = $"\"{LocalAgent.ScriptPath}\" --config \"{LocalAgent.ConfigPath}\" --log \"{LocalAgent.LogPath}\"",
                WorkingDirectory = tmpRoot, UseShellExecute = false, CreateNoWindow = true,
            };
            using (var loose = System.Diagnostics.Process.Start(psi))
            {
                bool healthy = false;
                for (int i = 0; i < 40 && !healthy; i++) { Thread.Sleep(250); healthy = LocalAgent.IsHealthyAsync(port).GetAwaiter().GetResult(); }
                Check("pid-less agent (autostart shape) answers /health", healthy && loose is not null && !loose.HasExited);
                Check("pid-less agent reads as 'installed, stopped' by the pid file alone", !LocalAgent.RunningPid().Running);
                var found = LocalAgent.FindRunning(port);
                Check($"FindRunning finds the pid-less agent through its listening port (pid {found.Pid})", found.Running && loose is not null && found.Pid == loose.Id);
                LocalAgent.Stop(port, line => Console.WriteLine("    | " + line));
                Check("Stop(port) ends the pid-less agent: /health silent, process gone",
                    !LocalAgent.IsHealthyAsync(port).GetAwaiter().GetResult() && (loose is null || loose.HasExited) && !LocalAgent.FindRunning(port).Running);
            }

            var rep = LocalAgent.Uninstall(port, removeFirewall: false, line => Console.WriteLine("    | " + line));
            Check("uninstall: root gone, no leftovers, /health silent",
                rep.FilesRemoved && rep.Leftovers.Count == 0 && !Directory.Exists(tmpRoot) && !LocalAgent.IsHealthyAsync(port).GetAwaiter().GetResult());
            Check("uninstall: the real Run value is untouched (the temp root's autostart was never set)",
                LocalAgent.AutostartEnabled == autostartBefore && !rep.AutostartRemoved);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  failed: " + ex.Message);
            Check("local agent flow completed", false);
        }
        finally
        {
            try { LocalAgent.Stop(port, line => Console.WriteLine("    | " + line)); } catch { /* already gone */ }
            LocalAgent.RootOverride = null;
            try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true); } catch { /* best effort */ }
        }
        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"localagent spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }
}

/// <summary>
/// LIVE, opt-in: the update of the agent on this PC with the REAL agent (<see cref="LocalAgent.UpdateAsync"/>)
/// under a TEMP root (<see cref="LocalAgent.RootOverride"/> — never the developer's real
/// %LOCALAPPDATA%\Relic\agent). An installed agent that differs from the repository's by one comment line
/// runs on a spare port and is updated to the repository's: the staged copy's real <c>--selftest</c>, the
/// stop, the swap, the start. Before and after, the build the running agent names in GET /status must be
/// the identity the launcher works out for the same files (<see cref="AgentBuild"/>) — the two
/// implementations, on a live process. No stack folder is configured: that agent serves its status and
/// nothing else, and its state file and log live in the temp root. Takes about a minute.
/// </summary>
static class AgentUpdateSpike
{
    public static int Run()
    {
        Console.WriteLine("== agentupdate spike (LIVE): LocalAgent.UpdateAsync with the real agent under a temp root ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
        var py = LocalAgent.FindPython();
        if (py is null) { Console.WriteLine("  skipped: no python >= 3.10 on this PC"); return 0; }
        string agentDir = Path.Combine(AppContext.BaseDirectory, "agent");
        if (!File.Exists(Path.Combine(agentDir, "gio_agent.py")))
            agentDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "agent"));
        const int port = 18098;
        string tmpRoot = Path.Combine(Path.GetTempPath(), "relic-agentupdate-spike-" + Guid.NewGuid().ToString("n")[..8]);
        static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
            foreach (var d in Directory.GetDirectories(src)) CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
        }
        LocalAgent.RootOverride = tmpRoot;
        try
        {
            Console.WriteLine($"  temp root: {tmpRoot}");
            Directory.CreateDirectory(tmpRoot);
            string token = AgentInstallPlan.NewToken();
            File.WriteAllText(LocalAgent.ConfigPath, $"# the update leaves this file alone\nGIO_AGENT_LISTEN=127.0.0.1:{port}\nGIO_AGENT_TOKEN={token}\n", new UTF8Encoding(false));
            byte[] config = File.ReadAllBytes(LocalAgent.ConfigPath);
            File.WriteAllText(LocalAgent.PythonPath, py.Exe + Environment.NewLine + py.ExeW);
            // The installed agent: the repository's with one more line — the same behaviour, another identity.
            byte[] installed = File.ReadAllBytes(Path.Combine(agentDir, "gio_agent.py"))
                .Concat(Encoding.ASCII.GetBytes("# an installed copy that is not the shipped one\n")).ToArray();
            File.WriteAllBytes(LocalAgent.ScriptPath, installed);
            CopyTree(Path.Combine(agentDir, "payloads"), LocalAgent.PayloadsDir);
            string shipped = AgentBuild.Identity(agentDir) ?? "";
            string before = AgentBuild.Identity(tmpRoot) ?? "";
            Check($"the installed files are not the shipped agent ({(before.Length > 10 ? before[..10] : before)} / {(shipped.Length > 10 ? shipped[..10] : shipped)})",
                shipped.Length == 64 && before.Length == 64 && AgentBuild.Differs(shipped, before));

            var cfg = new ServerConfig("127.0.0.1", 22, "root", "", token, port, "direct");
            string Build()
            {
                var a = new AgentClient(cfg);
                try
                {
                    a.ConnectAsync().GetAwaiter().GetResult();
                    var st = a.StatusAsync().GetAwaiter().GetResult();
                    return st.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "(no build key)";
                }
                finally { a.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            }
            LocalAgent.Stop(port);
            int pid = LocalAgent.StartAsync(port, line => Console.WriteLine("    | " + line), 120).GetAwaiter().GetResult();
            Check($"the installed agent runs (pid {pid}) and names the identity the launcher works out for its files", Build() == before);

            var rep = LocalAgent.UpdateAsync(line => Console.WriteLine("    | " + line), agentDir, null, 240).GetAwaiter().GetResult();
            Check("update: replaced, restarted, running", rep is { Replaced: true, Restarted: true, Running: true });
            Check("update: the agent that answers now names the shipped identity — the update note would be gone",
                Build() == shipped && !AgentBuild.Differs(shipped, AgentBuild.Identity(tmpRoot)));
            Check("update: the configuration file is byte for byte what it was; the replaced script is kept",
                File.ReadAllBytes(LocalAgent.ConfigPath).SequenceEqual(config)
                && File.ReadAllBytes(Path.Combine(LocalAgent.PreviousPath, "gio_agent.py")).SequenceEqual(installed)
                && !Directory.Exists(LocalAgent.StagePath));
            int pidUpdated = LocalAgent.RunningPid().Pid;
            var again = LocalAgent.UpdateAsync(line => Console.WriteLine("    | " + line), agentDir, null, 240).GetAwaiter().GetResult();
            Check("update again: nothing to replace — the running agent is restarted and still names the shipped identity",
                again is { Replaced: false, Restarted: true, Running: true } && LocalAgent.RunningPid().Pid != pidUpdated && Build() == shipped);
            LocalAgent.Stop(port, line => Console.WriteLine("    | " + line));
            Check("stopped: /health silent", !LocalAgent.IsHealthyAsync(port).GetAwaiter().GetResult() && !LocalAgent.FindRunning(port).Running);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  failed: " + ex.Message);
            Check("agent update flow completed", false);
        }
        finally
        {
            try { LocalAgent.Stop(port); } catch { /* already gone */ }
            LocalAgent.RootOverride = null;
            try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true); } catch { /* best effort */ }
        }
        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"agentupdate spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }
}

/// <summary>
/// Validates the download mechanics. Local half (no network): cancel mid-transfer + resume — the
/// exact pause/cancel mechanic the install overlay exposes — against a tiny in-process TCP server.
/// Network half: a 1 KB range-probe of the CDN (expects HTTP 206 + exact total size + a ZIP
/// signature + arbitrary-offset resume) and of the Google Drive confirm-token URL (expects real zip
/// bytes, not an HTML interstitial). Real links are read from the gitignored readme so no private
/// ids are committed.
/// </summary>
static class DownloadSpike
{
    // Known-good size for the 1.6 audio zip (the smallest source), from the de-risk pass.
    const long Audio16Size = 3_708_267_740L;

    public static int Run()
    {
        Console.WriteLine("== download spike: cancel/resume (local) + CDN 206/resume + Drive confirm-token ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        try { PauseResumeChecks(Check); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  pause/resume error: {ex.GetType().Name}: {ex.Message}");
            Check("cancel mid-download + resume (local)", false);
        }

        try { QuotaPageChecks(Check); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  quota-page error: {ex.GetType().Name}: {ex.Message}");
            Check("error page does not destroy the cached download", false);
        }

        try { CatalogueVoiceChecks(Check); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  catalogue-voices error: {ex.GetType().Name}: {ex.Message}");
            Check("catalogue: voices map + legacy audio alias", false);
        }

        try { MirrorPlanChecks(Check); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  mirror-plan error: {ex.GetType().Name}: {ex.Message}");
            Check("mirror plan: per-host size + order", false);
        }

        try { EdgeRaceChecks(Check); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  edge-race error: {ex.GetType().Name}: {ex.Message}");
            Check("edge: the faster edge wins and the transfer is pinned to it", false);
        }

        try { StallResumeChecks(Check); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  stall/resume error: {ex.GetType().Name}: {ex.Message}");
            Check("stall: a silent connection is reconnected and resumed via Range", false);
        }

        string? readme = FindUp("link-uri_download_versiuni_ref/readme.txt");
        if (readme is null)
        {
            Console.Error.WriteLine("  could not locate link-uri_download_versiuni_ref/readme.txt (gitignored) — skipping the network half");
            int localFail = results.Count(r => !r.ok);
            Console.WriteLine();
            Console.WriteLine($"download spike: {results.Count - localFail}/{results.Count} checks passed (network half skipped)");
            return localFail == 0 ? 3 : 1;
        }
        string text = File.ReadAllText(readme);

        // The catalogue the app would load from here (the private versions.json, else the committed
        // sample): what the readme lists must be what Relic actually dials. The readme carries no Drive
        // ids — those live only in the private catalogue — so the Drive probe reads its id
        // from there and is SKIPPED, not failed, when it is empty (a public build).
        string? catalogPath = FindUp("config/versions.json") ?? FindUp("config/versions.sample.json");
        var catalog = catalogPath is null ? Array.Empty<GameVersionInfo>() : VersionCatalog.Load(catalogPath);
        var v16 = catalog.FirstOrDefault(v => v.Id == "1.6");
        var en16 = v16?.VoicePack(VoiceLanguages.Default);

        // Pinned to the 1.6.1 FILE NAMES: the readme may list 1.6.0 too, and "contains Audio and
        // 1.6" would match whichever comes first.
        string? cdnAudio16 = Regex.Matches(text, @"https://autopatchhk\.yuanshen\.com/\S+?\.zip")
            .Select(m => m.Value)
            .FirstOrDefault(u => u.EndsWith("/Audio_English(US)_1.6.1.zip", StringComparison.Ordinal));
        string? archiveAudio16 = Regex.Matches(text, @"https://archive\.org/download/\S+?\.zip")
            .Select(m => m.Value)
            .FirstOrDefault(u => u.EndsWith("/Audio_English%28US%29_1.6.1.zip", StringComparison.Ordinal));
        string? driveAudio16 = string.IsNullOrWhiteSpace(en16?.DriveId) ? null : en16!.DriveId;

        Check("found the 1.6.1 English audio CDN url in the readme", cdnAudio16 is not null);
        Check("found the 1.6.1 English audio archive.org url in the readme", archiveAudio16 is not null);
        if (catalogPath is null || v16 is null || en16 is null)
        {
            Check("catalogue: 1.6 with an English(US) pack loads next to the readme", false);
        }
        else
        {
            Console.WriteLine($"    catalogue: {catalogPath}");
            // The private catalogue spells out every url; the sample abbreviates the CLIENT folder
            // ("/.../") while its voice packs are complete — compare whatever is not a placeholder.
            if (!en16.CdnUrl.Contains("/.../"))
                Check("catalogue: the 1.6 English(US) CDN url is the readme's", cdnAudio16 is not null && en16.CdnUrl == cdnAudio16);
            var archiveMirror = en16.Mirrors.FirstOrDefault(m => m.Id == "archive");
            Check("catalogue: the 1.6 English(US) archive.org mirror url is the readme's",
                archiveMirror is not null && archiveAudio16 is not null && archiveMirror.Url == archiveAudio16);
            Check($"catalogue: the 1.6 English(US) pack still says {Audio16Size} bytes", en16.Size == Audio16Size);
            var clientMirror = v16.Client.Mirrors.FirstOrDefault(m => m.Id == "archive");
            Check("catalogue: the 1.6 client archive.org url appears in the readme",
                clientMirror is not null && text.Contains(clientMirror.Url, StringComparison.Ordinal));
        }
        if (driveAudio16 is null)
            Console.WriteLine("  [SKIP] Drive probe: the catalogue carries no Drive id for the 1.6 English pack (public build)");

        try
        {
            if (cdnAudio16 is not null)
            {
                var p = Downloader.ProbeAsync(cdnAudio16).GetAwaiter().GetResult();
                Console.WriteLine($"    CDN: status={p.Status} total={p.Total} type={p.ContentType} zip={p.LooksLikeZip}");
                Check("CDN honors Range (HTTP 206)", p.SupportsRange);
                Check($"CDN reports exact size ({Audio16Size})", p.Total == Audio16Size);
                Check("CDN body is a ZIP (PK header)", p.LooksLikeZip);

                // arbitrary-offset resume: fetch bytes [1024,2047] and confirm the server starts there
                using var http = Downloader.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, cdnAudio16);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1024, 2047);
                using var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                long? start = resp.Content.Headers.ContentRange?.From;
                Check("CDN resume: range [1024,2047] returns 206 @ offset 1024",
                    (int)resp.StatusCode == 206 && start == 1024);
            }

            if (driveAudio16 is not null)
            {
                string url = Downloader.DriveDirectUrl(driveAudio16);
                var p = Downloader.ProbeAsync(url).GetAwaiter().GetResult();
                Console.WriteLine($"    Drive: status={p.Status} type={p.ContentType} html={p.IsHtml} zip={p.LooksLikeZip}");
                Check("Drive confirm-token returns bytes, not HTML interstitial", !p.IsHtml);
                Check("Drive body is a ZIP (PK header)", p.LooksLikeZip);
            }

            // The other voice languages: the catalogue's urls must be the reference list's, and one CDN
            // probe per version (its smallest pack) proves 206 + the catalogued byte count. Gated on the
            // gitignored languages.txt like the readme half — an "all" run never depends on it.
            string? languages = FindUp("link-uri_download_versiuni_ref/languages.txt");
            if (languages is null)
            {
                Console.WriteLine("  [SKIP] non-English packs: link-uri_download_versiuni_ref/languages.txt not found");
            }
            else
            {
                string langText = File.ReadAllText(languages);
                foreach (var v in catalog)
                {
                    var packs = v.VoicePacks().Where(p => p.Lang != VoiceLanguages.Default).ToList();
                    Check($"catalogue {v.Id}: every non-English CDN url is in languages.txt",
                        packs.Count == 3 && packs.All(p => langText.Contains(p.Pack.CdnUrl, StringComparison.Ordinal)));
                    Check($"catalogue {v.Id}: every non-English archive.org url is in languages.txt",
                        packs.Count == 3 && packs.All(p => p.Pack.Mirrors.Any(m => m.Id == "archive" && langText.Contains(m.Url, StringComparison.Ordinal))));
                    var smallest = packs.OrderBy(p => p.Pack.Size).FirstOrDefault();
                    if (smallest.Pack is null) continue;
                    var lp = Downloader.ProbeAsync(smallest.Pack.CdnUrl).GetAwaiter().GetResult();
                    Console.WriteLine($"    CDN {v.Id} {smallest.Lang}: status={lp.Status} total={lp.Total} zip={lp.LooksLikeZip}");
                    Check($"CDN {v.Id} {smallest.Lang}: 206 + the catalogued size ({smallest.Pack.Size})",
                        lp.SupportsRange && lp.Total == smallest.Pack.Size && lp.LooksLikeZip);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  network error: {ex.GetType().Name}: {ex.Message}");
            Check("network reachable", false);
        }

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"download spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Proves pause/cancel end-to-end without network: cancel a download mid-transfer, verify the
    /// partial file survives, then resume and verify the server received a Range request at exactly
    /// the partial offset and the final file is byte-identical. The server stalls after a 128 KB
    /// burst on the FIRST request, so the cancel deterministically lands mid-transfer.
    /// </summary>
    /// <summary>
    /// Reproduces the Drive quota case: Google Drive answers a 1 KB Range probe with 206 + "PK", then
    /// answers the FULL download with 200 + the "Quota exceeded" HTML page. Two things must hold:
    /// an existing partial must SURVIVE (FileMode.Create on that page would truncate a
    /// multi-GB archive to a 2 KB web page), and the failure must be typed so the caller can switch
    /// source instead of retrying a url that will never work.
    /// </summary>
    /// <summary>
    /// Did the last request the quota stub saw carry a Range header? The stub records it because
    /// "startFresh must not resume" is otherwise invisible: both a resumed and a fresh transfer end in
    /// the same exception here, and only the request tells them apart.
    /// </summary>
    static readonly Dictionary<int, string> LastRequestPerPort = new();

    static bool RangeWasRequested(int port, out string request)
    {
        lock (LastRequestPerPort)
        {
            request = LastRequestPerPort.TryGetValue(port, out var r) ? r : "";
            return Regex.IsMatch(request, @"^Range:", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }
    }

    /// <summary>
    /// A hand-rolled HTTP/1.1 stub on one loopback address: every request is recorded, and a handler
    /// decides how the body is served (full speed, throttled, cut short, stalled, refused). Several
    /// stubs on DIFFERENT loopback addresses and the SAME port are how the edge race is reproduced
    /// without DNS: 127.0.0.1/127.0.0.2/127.0.0.3 all answer for the name "localhost".
    /// </summary>
    sealed class Stub : IDisposable
    {
        public readonly List<string> Requests = new();
        readonly TcpListener _l;
        readonly Func<string, int, NetworkStream, Task> _serve;
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;

        public Stub(IPAddress ip, int port, Func<string, int, NetworkStream, Task> serve)
        {
            _serve = serve;
            _l = new TcpListener(ip, port);
            _l.Start();
            _ = Task.Run(AcceptLoop);
        }

        async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _l.AcceptTcpClientAsync(); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var c = client;
                        var ns = c.GetStream();
                        var buf = new byte[8192];
                        var req = new StringBuilder();
                        while (!req.ToString().Contains("\r\n\r\n"))
                        {
                            int k = await ns.ReadAsync(buf);
                            if (k <= 0) return;
                            req.Append(Encoding.ASCII.GetString(buf, 0, k));
                        }
                        int index;
                        lock (Requests) { Requests.Add(req.ToString()); index = Requests.Count - 1; }
                        await _serve(req.ToString(), index, ns);
                    }
                    catch { /* client hung up */ }
                });
            }
        }

        public int Hits { get { lock (Requests) return Requests.Count; } }
        public void Dispose() => _l.Stop();
    }

    /// <summary>First byte a Range request asks for, or 0 when there is no Range header.</summary>
    static long RangeStart(string request)
    {
        var m = Regex.Match(request, @"^Range:\s*bytes=(\d+)-", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return m.Success ? long.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>Deterministic pseudo-random body that starts with "PK" so expectZip callers accept it.</summary>
    static byte[] FakeZip(int len, int seed)
    {
        var b = new byte[len];
        new Random(seed).NextBytes(b);
        b[0] = 0x50; b[1] = 0x4B;
        return b;
    }

    /// <summary>Serve <paramref name="body"/> from the requested offset, <paramref name="chunk"/> bytes at
    /// a time with <paramref name="pauseMs"/> between chunks (0 = full speed), optionally stopping after
    /// <paramref name="stopAfter"/> bytes: <c>stall</c> keeps the connection open forever, otherwise it is closed.</summary>
    static async Task ServeRange(string req, NetworkStream ns, byte[] body, int chunk = 64 * 1024, int pauseMs = 0,
        long stopAfter = long.MaxValue, bool stall = false)
    {
        long from = RangeStart(req);
        if (from >= body.Length)
        {
            await ns.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{body.Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            return;
        }
        bool ranged = from > 0;
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            (ranged ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n") +
            "Content-Type: application/octet-stream\r\n" +
            (ranged ? $"Content-Range: bytes {from}-{body.Length - 1}/{body.Length}\r\n" : "") +
            $"Content-Length: {body.Length - from}\r\nConnection: close\r\n\r\n"));
        long sent = 0;
        for (long pos = from; pos < body.Length; pos += chunk)
        {
            if (sent >= stopAfter)
            {
                if (stall) { await Task.Delay(TimeSpan.FromMinutes(5)); }
                return; // closed by the caller's using
            }
            int n = (int)Math.Min(chunk, body.Length - pos);
            await ns.WriteAsync(body.AsMemory((int)pos, n));
            sent += n;
            if (pauseMs > 0) await Task.Delay(pauseMs);
        }
    }

    /// <summary>
    /// Reproduces the multi-CDN case without touching the network: one host name, several edges
    /// of very different speed (plus one that refuses), and the transfer must end up pinned to the
    /// fast one. Three stubs share one port on three loopback addresses; the candidate list is
    /// injected so no DNS is involved.
    /// </summary>
    static void EdgeRaceChecks(Action<string, bool> check)
    {
        // DoH JSON parse (a real AdGuard answer): the A record AND the CNAME target.
        var (ips, cnames) = EdgeRace.ParseDohAnswer(
            "{\"Question\":[{\"name\":\"autopatchhk.yuanshen.com.\",\"type\":1}],\"Answer\":[" +
            "{\"name\":\"autopatchhk.yuanshen.com.\",\"data\":\"d3ln624mszu7ty.cloudfront.net.\",\"TTL\":300,\"type\":5,\"class\":1}," +
            "{\"name\":\"d3ln624mszu7ty.cloudfront.net.\",\"data\":\"3.160.251.211\",\"TTL\":60,\"type\":1,\"class\":1}],\"Status\":0}");
        check("edge: DoH JSON yields the A record", ips.Count == 1 && ips.Contains(IPAddress.Parse("3.160.251.211")));
        check("edge: DoH JSON yields the CNAME target (no trailing dot)", cnames.Count == 1 && cnames.Contains("d3ln624mszu7ty.cloudfront.net"));

        // Prune: a real answer set (15 addresses, 3 CDNs, Akamai in five /16s) must come
        // down to one per /16 with the order kept — and a same-/16 list (the loopback stubs below)
        // must not be pruned to nothing.
        var real = new[] { "80.97.208.177", "80.97.208.208", "104.18.40.146", "172.64.147.110", "3.160.251.211",
                           "18.172.106.217", "3.173.167.173", "92.123.102.128", "92.123.102.162", "23.207.210.73",
                           "23.207.210.95", "2.19.126.142", "2.19.126.153", "2.16.238.9", "2.16.238.24" }
            .Select(IPAddress.Parse).ToList();
        var pruned = EdgeRace.Prune(real);
        check("edge: prune keeps one address per /16 (15 → 10 here) in the original order",
            pruned.Count == 10 && pruned[0].Equals(real[0]) && pruned[1].Equals(real[2])
            && pruned.Select(p => p.GetAddressBytes()[0] * 256 + p.GetAddressBytes()[1]).Distinct().Count() == pruned.Count);
        check("edge: prune never drops below two candidates",
            EdgeRace.Prune([IPAddress.Loopback, IPAddress.Parse("127.0.0.2"), IPAddress.Parse("127.0.0.3")]).Count == 3);

        var body = FakeZip(6 * 1024 * 1024, 11);
        var fastIp = IPAddress.Parse("127.0.0.2");
        var slowIp = IPAddress.Loopback;
        var refuseIp = IPAddress.Parse("127.0.0.3");

        using var fast = new Stub(fastIp, 0, (req, i, ns) => ServeRange(req, ns, body));
        int port = fast.Port;
        using var slow = new Stub(slowIp, port, (req, i, ns) => ServeRange(req, ns, body, chunk: 16 * 1024, pauseMs: 40)); // ~400 KB/s
        using var refuse = new Stub(refuseIp, port, async (req, i, ns) =>
            await ns.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")));

        var oldWindow = EdgeRace.ProbeWindow; var oldBytes = EdgeRace.ProbeBytes; var oldCache = EdgeRace.CacheFile;
        EdgeRace.ProbeWindow = TimeSpan.FromSeconds(1.5);
        EdgeRace.ProbeBytes = 2 * 1024 * 1024;
        EdgeRace.CacheFile = Path.Combine(Path.GetTempPath(), $"relic_spike_edges_{Environment.ProcessId}.json");
        string dest = Path.Combine(Path.GetTempPath(), $"relic_spike_edge_{Environment.ProcessId}.bin");
        try
        {
            string url = $"http://localhost:{port}/edge.bin";
            var choice = EdgeRace.PickAsync(url, default, [slowIp, fastIp, refuseIp]).GetAwaiter().GetResult();
            check("edge: the faster edge wins the race", choice is not null && choice.Ip.Equals(fastIp));
            check("edge: the refusing edge is reported, not chosen",
                choice is not null && choice.All.Any(p => p.Ip.Equals(refuseIp) && p.Error is not null));
            check("edge: the slow edge is measured slower, not excluded",
                choice is not null && choice.All.Any(p => p.Ip.Equals(slowIp) && p.Error is null && p.Bytes > 0 && p.Bytes < choice.All.First(q => q.Ip.Equals(fastIp)).Bytes));

            // The whole transfer goes to the chosen edge — not to whatever "localhost" resolves to.
            int fastBefore = fast.Hits, slowBefore = slow.Hits;
            if (File.Exists(dest)) File.Delete(dest);
            Downloader.DownloadResumableAsync(url, dest, null, default, expectZip: true, edge: choice).GetAwaiter().GetResult();
            check("edge: the download is pinned to the chosen edge (fast stub served it, slow stub untouched)",
                fast.Hits == fastBefore + 1 && slow.Hits == slowBefore);
            check("edge: the pinned download is byte-identical",
                File.Exists(dest) && File.ReadAllBytes(dest).AsSpan().SequenceEqual(body));

            // A resume through the pinned client also goes to the pin (Range from the partial).
            File.WriteAllBytes(dest, body.AsSpan(0, 1024 * 1024).ToArray());
            fastBefore = fast.Hits;
            Downloader.DownloadResumableAsync(url, dest, null, default, expectZip: true, edge: choice).GetAwaiter().GetResult();
            check("edge: a resume is pinned too and asks for the right offset",
                fast.Hits == fastBefore + 1 && RangeStart(fast.Requests[^1]) == 1024 * 1024
                && File.ReadAllBytes(dest).AsSpan().SequenceEqual(body));

            check("edge: a single candidate means no race (null → ordinary connection)",
                EdgeRace.PickAsync(url, default, [fastIp]).GetAwaiter().GetResult() is null);
            check("edge: a literal-IP url never races",
                EdgeRace.PickAsync($"http://127.0.0.1:{port}/edge.bin", default).GetAwaiter().GetResult() is null);
            check("edge: when every edge refuses there is no choice",
                EdgeRace.PickAsync(url, default, [refuseIp, IPAddress.Parse("127.0.0.4")]).GetAwaiter().GetResult() is null);
            check("edge: a refusing edge never wins even against an html one",
                EdgeRace.PickAsync(url, default, [refuseIp, fastIp]).GetAwaiter().GetResult()?.Ip.Equals(fastIp) == true);

            // Ranking is by RATE, not bytes. Two full-speed edges that both reach ProbeBytes tie on
            // bytes; the one that answered 400 ms later must lose. It is listed FIRST so that a
            // byte-only ranking (stable sort) would crown it — which is the production case: every
            // real edge finishes 4 MiB inside the window, and only the time it took tells them apart.
            var lateIp = IPAddress.Parse("127.0.0.4");
            using var late = new Stub(lateIp, port, async (req, i, ns) => { await Task.Delay(400); await ServeRange(req, ns, body); });
            var rc = EdgeRace.PickAsync(url, default, [lateIp, fastIp]).GetAwaiter().GetResult();
            check("edge: ranking is by rate — a full-speed edge that answers 400 ms later loses although it ties on bytes",
                rc is not null && rc.Ip.Equals(fastIp)
                && rc.All.Count == 2 && rc.All.All(p => p.Error is null && p.Bytes >= EdgeRace.ProbeBytes));

            // A pin that died mid-transfer is excluded from the next race.
            var rx = EdgeRace.PickAsync(url, default, [fastIp, slowIp, refuseIp], exclude: [fastIp]).GetAwaiter().GetResult();
            check("edge: an excluded (dead) edge is never raced again — the next best wins",
                rx is not null && rx.Ip.Equals(slowIp) && rx.All.All(p => !p.Ip.Equals(fastIp)));

            // Resolution must be bounded even when a DoH resolver sends headers and then stalls mid-body
            // (a body parsed synchronously has no timeout, and the user's cancel is ignored).
            using var doh = new Stub(IPAddress.Loopback, 0, async (req, i, ns) =>
            {
                await ns.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/dns-json\r\nContent-Length: 5000\r\nConnection: close\r\n\r\n{\"Answer\":["));
                await Task.Delay(TimeSpan.FromMinutes(5));
            });
            var oldDoh = EdgeRace.DohEndpoints; var oldUdp = EdgeRace.UdpResolvers; var oldRt = EdgeRace.ResolveTimeout;
            EdgeRace.DohEndpoints = [$"http://127.0.0.1:{doh.Port}/dns-query"];
            EdgeRace.UdpResolvers = [];
            EdgeRace.ResolveTimeout = TimeSpan.FromSeconds(1);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (rips, _, _) = EdgeRace.ResolveCandidatesAsync("localhost", default).GetAwaiter().GetResult();
                check($"edge: a DoH resolver that stalls mid-body cannot hang the race (returned in {sw.Elapsed.TotalSeconds:0.0}s)",
                    sw.Elapsed < TimeSpan.FromSeconds(4) && rips.Any(ip => ip.Equals(IPAddress.Loopback)));
            }
            finally { EdgeRace.DohEndpoints = oldDoh; EdgeRace.UdpResolvers = oldUdp; EdgeRace.ResolveTimeout = oldRt; }
        }
        finally
        {
            EdgeRace.ProbeWindow = oldWindow; EdgeRace.ProbeBytes = oldBytes; EdgeRace.CacheFile = oldCache;
            try { File.Delete(dest); } catch { }
        }
    }

    /// <summary>
    /// The downloader's own mid-transfer recovery: a connection that goes silent, or is cut, is
    /// reconnected and resumed with a Range request from the bytes on disk — and a user cancel during a
    /// stall is still a cancel. Without a read timeout, a dropped connection
    /// that the socket never reports would hang the install forever, past every retry and fallback.
    /// </summary>
    static void StallResumeChecks(Action<string, bool> check)
    {
        var body = FakeZip(3 * 1024 * 1024, 23);
        var oldStall = Downloader.StallTimeout; var oldMax = Downloader.MaxResumes; var oldDelay = Downloader.ResumeDelay; var oldReset = Downloader.ProgressResetBytes;
        Downloader.StallTimeout = TimeSpan.FromSeconds(1.5);
        Downloader.ResumeDelay = TimeSpan.FromMilliseconds(100);
        Downloader.MaxResumes = 3;
        string dest = Path.Combine(Path.GetTempPath(), $"relic_spike_stall_{Environment.ProcessId}.bin");
        try
        {
            // 1) Stall: first request sends 256 KB then goes silent; every later request serves normally.
            using (var stub = new Stub(IPAddress.Loopback, 0, (req, i, ns) =>
                       i == 0 ? ServeRange(req, ns, body, stopAfter: 256 * 1024, stall: true) : ServeRange(req, ns, body)))
            {
                if (File.Exists(dest)) File.Delete(dest);
                string url = $"http://127.0.0.1:{stub.Port}/stall.bin";
                Downloader.DownloadResumableAsync(url, dest, null, default, expectZip: true).GetAwaiter().GetResult();
                check("stall: a silent connection is reconnected and resumed via Range",
                    stub.Hits == 2 && RangeStart(stub.Requests[1]) == 256 * 1024);
                check("stall: the resumed file is byte-identical",
                    File.ReadAllBytes(dest).AsSpan().SequenceEqual(body));

                // startFresh + stall: the FIRST response truncates the old copy; the reconnect must then
                // RESUME this host's bytes, not truncate again (which would restart from zero forever).
                File.WriteAllBytes(dest, FakeZip(512 * 1024, 99)); // some other host's partial
                stub.Requests.Clear();
                Downloader.DownloadResumableAsync(url, dest, null, default, expectZip: true, startFresh: true).GetAwaiter().GetResult();
                check("stall: after startFresh, the reconnect resumes this host's bytes (no second truncate)",
                    stub.Hits == 2 && RangeStart(stub.Requests[0]) == 0 && RangeStart(stub.Requests[1]) == 256 * 1024
                    && File.ReadAllBytes(dest).AsSpan().SequenceEqual(body));
            }

            // 2) Cut: the connection is closed mid-body (Content-Length promised more) — same recovery.
            using (var stub = new Stub(IPAddress.Loopback, 0, (req, i, ns) =>
                       i == 0 ? ServeRange(req, ns, body, stopAfter: 1024 * 1024) : ServeRange(req, ns, body)))
            {
                if (File.Exists(dest)) File.Delete(dest);
                string url = $"http://127.0.0.1:{stub.Port}/cut.bin";
                Downloader.DownloadResumableAsync(url, dest, null, default, expectZip: true).GetAwaiter().GetResult();
                check("drop: a connection cut mid-body is resumed via Range at the partial's length",
                    stub.Hits == 2 && RangeStart(stub.Requests[1]) == 1024 * 1024
                    && File.ReadAllBytes(dest).AsSpan().SequenceEqual(body));
            }

            // 3) Out of resumes: a host that stalls on every request surfaces an IOException (a
            // transient failure to the caller), never a cancel the caller would mistake for the user's.
            using (var stub = new Stub(IPAddress.Loopback, 0, (req, i, ns) => ServeRange(req, ns, body, stopAfter: 64 * 1024, stall: true)))
            {
                if (File.Exists(dest)) File.Delete(dest);
                Downloader.MaxResumes = 1;
                Exception? caught = null;
                try { Downloader.DownloadResumableAsync($"http://127.0.0.1:{stub.Port}/dead.bin", dest, null, default, expectZip: true).GetAwaiter().GetResult(); }
                catch (Exception ex) { caught = ex; }
                check("stall: out of resumes surfaces as IOException, not as a cancel",
                    caught is IOException && caught is not OperationCanceledException);
                check("stall: exactly MaxResumes+1 requests were made", stub.Hits == 2);
                check("stall: the partial is kept for the caller's own retry",
                    File.Exists(dest) && new FileInfo(dest).Length > 0);
                Downloader.MaxResumes = 3;
            }

            // 4) The user's cancel during a stall is still a cancel — it must not be turned into a
            // resume (which would ignore the user) nor into an IOException (which would switch source).
            using (var stub = new Stub(IPAddress.Loopback, 0, (req, i, ns) => ServeRange(req, ns, body, stopAfter: 64 * 1024, stall: true)))
            {
                if (File.Exists(dest)) File.Delete(dest);
                Downloader.StallTimeout = TimeSpan.FromSeconds(10);
                using var cts = new CancellationTokenSource(600);
                Exception? caught = null;
                try { Downloader.DownloadResumableAsync($"http://127.0.0.1:{stub.Port}/cancel.bin", dest, null, cts.Token, expectZip: true).GetAwaiter().GetResult(); }
                catch (Exception ex) { caught = ex; }
                check("stall: a user cancel during a stall is still an OperationCanceledException",
                    caught is OperationCanceledException);
                check("stall: a cancel makes no extra request", stub.Hits == 1);
            }

            // 5) A refusal is NOT resumed: the same 403 twice would be pointless — it goes straight up.
            using (var stub = new Stub(IPAddress.Loopback, 0, async (req, i, ns) =>
                       await ns.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"))))
            {
                Exception? caught = null;
                try { Downloader.DownloadResumableAsync($"http://127.0.0.1:{stub.Port}/403.bin", dest, null, default, expectZip: true).GetAwaiter().GetResult(); }
                catch (Exception ex) { caught = ex; }
                check("refusal: a 403 is not retried by the downloader (one request, HttpRequestException)",
                    stub.Hits == 1 && caught is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden });
            }

            // 6) The budget is a STREAK, not a lifetime count: a link that stalls after every 768 KB but
            //    always resumes delivers GBs over a long transfer and must never fail at "the 7th drop".
            //    Body 3 MiB served 512 KB at a time, MaxResumes 3 → 5 reconnects are needed; each delivers ≥ ProgressResetBytes.
            Downloader.StallTimeout = TimeSpan.FromSeconds(1.5);
            Downloader.MaxResumes = 3;
            Downloader.ProgressResetBytes = 512 * 1024;
            using (var stub = new Stub(IPAddress.Loopback, 0, (req, i, ns) => ServeRange(req, ns, body, stopAfter: 512 * 1024, stall: true)))
            {
                if (File.Exists(dest)) File.Delete(dest);
                Exception? caught = null;
                try { Downloader.DownloadResumableAsync($"http://127.0.0.1:{stub.Port}/flaky.bin", dest, null, default, expectZip: true).GetAwaiter().GetResult(); }
                catch (Exception ex) { caught = ex; }
                check($"streak: a link that stalls every 512 KB but always resumes completes ({stub.Hits} requests, MaxResumes {Downloader.MaxResumes})",
                    caught is null && stub.Hits > Downloader.MaxResumes + 1 && File.ReadAllBytes(dest).AsSpan().SequenceEqual(body));
            }
            Downloader.ProgressResetBytes = oldReset;

            // 7) Dead at the headers on a PINNED edge: nothing to resume and the pin is the problem —
            //    surface at once (one request), as IOException, so the caller can race another edge.
            using (var stub = new Stub(IPAddress.Loopback, 0, async (req, i, ns) => await Task.Delay(TimeSpan.FromMinutes(5))))
            {
                if (File.Exists(dest)) File.Delete(dest);
                var pin = new EdgeChoice("localhost", IPAddress.Loopback, 0, [], []);
                Exception? caught = null;
                try { Downloader.DownloadResumableAsync($"http://localhost:{stub.Port}/hdr.bin", dest, null, default, expectZip: true, edge: pin).GetAwaiter().GetResult(); }
                catch (Exception ex) { caught = ex; }
                check("stall: a pinned edge that never answers surfaces after ONE request (the caller re-races), as IOException",
                    stub.Hits == 1 && caught is IOException && caught is not OperationCanceledException);

                // 8) The same, unpinned: one no-progress reconnect, then surface — never 7 × StallTimeout.
                if (File.Exists(dest)) File.Delete(dest);
                stub.Requests.Clear();
                caught = null;
                try { Downloader.DownloadResumableAsync($"http://127.0.0.1:{stub.Port}/hdr.bin", dest, null, default, expectZip: true).GetAwaiter().GetResult(); }
                catch (Exception ex) { caught = ex; }
                check($"stall: a host that never answers gets MaxNoProgressResumes+1 = {Downloader.MaxNoProgressResumes + 1} requests, then IOException",
                    stub.Hits == Downloader.MaxNoProgressResumes + 1 && caught is IOException);
            }

            // 9) A host that IGNORES Range (200 from offset 0 to a Range request) gets the documented
            //    single restart from zero — and then the next failure SURFACES, instead of truncating and
            //    re-downloading from zero forever (each cycle would reset the streak, since it "progressed").
            using (var stub = new Stub(IPAddress.Loopback, 0, async (req, i, ns) =>
            {
                long cut = i < 2 ? 1024 * 1024 : long.MaxValue; // first two responses are cut at 1 MiB
                await ns.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                long sent = 0;
                for (int pos = 0; pos < body.Length && sent < cut; pos += 64 * 1024)
                {
                    int n = (int)Math.Min(64 * 1024, body.Length - pos);
                    await ns.WriteAsync(body.AsMemory(pos, n));
                    sent += n;
                }
            }))
            {
                if (File.Exists(dest)) File.Delete(dest);
                Exception? caught = null;
                try { Downloader.DownloadResumableAsync($"http://127.0.0.1:{stub.Port}/norange.bin", dest, null, default, expectZip: true).GetAwaiter().GetResult(); }
                catch (Exception ex) { caught = ex; }
                check("range-ignoring host: one restart from zero, then the failure surfaces as IOException (no endless sawtooth)",
                    stub.Hits == 2 && RangeStart(stub.Requests[1]) == 1024 * 1024 && caught is IOException
                    && caught.Message.Contains("Range"));
            }

            // 10) A link that is DOWN for a moment: reconnects that fail at once (no response at all) are
            //     retried with back-off inside NoProgressWindow, not given up after one — a Wi-Fi roam or
            //     a router reboot must not end a 15 GB download with "Conexiunea nu s-a putut stabili".
            using (var stub = new Stub(IPAddress.Loopback, 0, (req, i, ns) => i < 3 ? Task.CompletedTask : ServeRange(req, ns, body)))
            {
                if (File.Exists(dest)) File.Delete(dest);
                Exception? caught = null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { Downloader.DownloadResumableAsync($"http://127.0.0.1:{stub.Port}/outage.bin", dest, null, default, expectZip: true).GetAwaiter().GetResult(); }
                catch (Exception ex) { caught = ex; }
                check($"outage: three immediate connection drops are ridden out with back-off and the transfer completes ({stub.Hits} requests, {sw.Elapsed.TotalSeconds:0.0}s)",
                    caught is null && stub.Hits == 4 && File.ReadAllBytes(dest).AsSpan().SequenceEqual(body));
            }
        }
        finally
        {
            Downloader.StallTimeout = oldStall; Downloader.MaxResumes = oldMax; Downloader.ResumeDelay = oldDelay;
            Downloader.ProgressResetBytes = oldReset;
            try { File.Delete(dest); } catch { }
        }
    }

    static void QuotaPageChecks(Action<string, bool> check)
    {
        const string QuotaPage =
            "<!DOCTYPE html><html><head><title>Google Drive - Quota exceeded</title></head>" +
            "<body>Sorry, you can't view or download this file at this time.</body></html>";

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var c = client;
                        var ns = c.GetStream();
                        var buf = new byte[8192];
                        var req = new StringBuilder();
                        while (!req.ToString().Contains("\r\n\r\n"))
                        {
                            int k = await ns.ReadAsync(buf);
                            if (k <= 0) return;
                            req.Append(Encoding.ASCII.GetString(buf, 0, k));
                        }
                        // Only the small bounded probe ("bytes=0-1023") is served — everything else
                        // gets the quota page, and gets it as a 200 that IGNORES Range. That last
                        // detail is the whole point: a 200 makes the downloader treat the transfer as
                        // a fresh one, which is what would truncate the existing file.
                        lock (LastRequestPerPort) LastRequestPerPort[port] = req.ToString();
                        bool ranged = Regex.IsMatch(req.ToString(), @"Range:\s*bytes=0-\d+", RegexOptions.IgnoreCase);
                        if (ranged)
                        {
                            // Small range: served happily, exactly like Drive under quota.
                            byte[] body = { 0x50, 0x4B, 0x03, 0x04 };
                            await ns.WriteAsync(Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 206 Partial Content\r\nContent-Type: application/octet-stream\r\n" +
                                $"Content-Range: bytes 0-3/999999\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                            await ns.WriteAsync(body);
                        }
                        else
                        {
                            byte[] body = Encoding.UTF8.GetBytes(QuotaPage);
                            await ns.WriteAsync(Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                                $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                            await ns.WriteAsync(body);
                        }
                    }
                    catch { /* client hung up */ }
                });
            }
        });

        string dest = Path.Combine(Path.GetTempPath(), $"relic_spike_quota_{Environment.ProcessId}.bin");
        try
        {
            string url = $"http://127.0.0.1:{port}/quota.bin";

            // The probe still looks perfectly healthy — this is why a probe-only fallback is useless.
            var probe = Downloader.ProbeAsync(url).GetAwaiter().GetResult();
            check("range probe looks healthy even while the source is quota-blocked",
                !probe.IsHtml && probe.LooksLikeZip);

            // 1) No pre-existing file: the full GET must fail as a SOURCE problem, typed and flagged.
            if (File.Exists(dest)) File.Delete(dest);
            SourceServedPageException? caught = null;
            try { Downloader.DownloadResumableAsync(url, dest, null, default, expectZip: true).GetAwaiter().GetResult(); }
            catch (SourceServedPageException ex) { caught = ex; }
            check("an HTML error page raises SourceServedPageException", caught is not null);
            check("the quota page is recognised as a quota failure", caught?.IsQuota == true);
            check("nothing is written when the source serves a page", !File.Exists(dest));

            // 2) THE data-loss regression: a complete cached archive must survive the same response.
            var cached = new byte[64 * 1024];
            new Random(7).NextBytes(cached);
            cached[0] = 0x50; cached[1] = 0x4B;
            File.WriteAllBytes(dest, cached);
            try { Downloader.DownloadResumableAsync(url, dest, null, default, expectZip: true).GetAwaiter().GetResult(); }
            catch (SourceServedPageException) { /* expected */ }
            byte[] after = File.Exists(dest) ? File.ReadAllBytes(dest) : Array.Empty<byte>();
            check($"cached download survives the error page ({after.Length / 1024} KB of {cached.Length / 1024} KB)",
                after.AsSpan().SequenceEqual(cached));

            // 3) The FALLBACK must not cost what the previous host already delivered. When the chain
            // steps to another host the partial cannot be resumed (different copy), but deleting it up
            // front means a host that then refuses has destroyed a transfer for nothing. startFresh
            // defers the truncation to the moment the new host's body passes the page/zip sniff.
            File.WriteAllBytes(dest, cached);
            try
            {
                Downloader.DownloadResumableAsync(url, dest, null, default, expectZip: true, startFresh: true)
                    .GetAwaiter().GetResult();
            }
            catch (SourceServedPageException) { /* expected */ }
            byte[] afterFresh = File.Exists(dest) ? File.ReadAllBytes(dest) : Array.Empty<byte>();
            check("a REFUSED host switch leaves the previous host's partial untouched",
                afterFresh.AsSpan().SequenceEqual(cached));

            // ...and when the new host does serve, startFresh must rewrite from zero rather than
            // append to bytes that came from a different copy — a splice passes every length check and
            // only dies at the extract CRC hours later.
            check("startFresh sends no Range header (no splice onto the old copy)",
                !RangeWasRequested(port, out string _));

            // ── which failures deserve one retry on the SAME host ───────────────────────────
            // Stepping down the chain can mean a far slower host for 15-32 GB, so a blip at connect
            // time is retried once where it costs one request — but a refusal is not, because it says
            // the same thing twice.
            var Transient = (Func<Exception, bool>)Relic.Core.Install.InstallService.IsTransientFailure;
            check("a dropped connection is retried on the same host",
                Transient(new IOException("The response ended prematurely")));
            check("a 5xx is retried on the same host",
                Transient(new HttpRequestException("503", null, System.Net.HttpStatusCode.ServiceUnavailable)));
            check("an error page is NOT retried on the same host",
                !Transient(new SourceServedPageException("quota", true)));
            check("a 403 is NOT retried on the same host",
                !Transient(new HttpRequestException("403", null, System.Net.HttpStatusCode.Forbidden)));
            check("a user cancel is NOT retried",
                !Transient(new OperationCanceledException()));
            check("a full disk is NOT retried",
                !Transient(new IOException("disk full") { HResult = unchecked((int)0x80070070) }));

            // ── which failures switch source ────────────────────────────────────────────────
            // Google rations the same file in more shapes than the HTML page: 403/429 and a transfer
            // cut short both mean "this source is refusing", and all of them must reach the CDN.
            var Switch = (Func<Exception, bool, bool>)Relic.Core.Install.InstallService.ShouldTryOtherSource;

            // A refusal writes NOTHING (the page/403 never reaches the file, and a truncated transfer
            // is deleted by Verify first), so progressed:false is the shape every refusal arrives in.
            check("the quota page switches source",
                Switch(new SourceServedPageException("quota", true), false));
            check("an HTTP failure (403/429/5xx) switches source",
                Switch(new HttpRequestException("403 Forbidden"), false));
            check("a short/truncated transfer switches source",
                Switch(new InvalidOperationException("The download is incomplete"), false));
            check("a dead connection that delivered nothing switches source",
                Switch(new IOException("The response ended prematurely"), false));

            // The exclusions. Each one is a case where switching host costs the partial and fails
            // exactly the same way on the other host.
            check("a user cancel never switches source",
                !Switch(new OperationCanceledException(), false));
            check("a full disk never switches source (ERROR_DISK_FULL)",
                !Switch(new IOException("disk full") { HResult = unchecked((int)0x80070070) }, false));
            check("a full disk never switches source (ERROR_HANDLE_DISK_FULL)",
                !Switch(new IOException("disk full") { HResult = unchecked((int)0x80070027) }, false));

            // THE regression this predicate exists to prevent: a socket blip 22 GB into a 31.8 GB
            // download must not throw those 22 GB away. The source was delivering — it is the
            // transfer that broke, and only the SAME source can resume that partial.
            check("a mid-transfer drop after real progress does NOT switch source",
                !Switch(new IOException("The response ended prematurely"), true));
            check("even a page/403 after real progress does NOT switch source",
                !Switch(new SourceServedPageException("quota", true), true));
            // ...but a transfer that COMPLETED with the wrong length / not a zip has no partial left to
            // protect (Verify deleted it) and the same host would serve the same bytes again: next host.
            check("a completed-but-wrong download (Verify) switches source even after progress",
                Switch(new Relic.Core.Install.DownloadVerifyException("incomplete"), true));
            check("a completed-but-wrong download (Verify) is NOT retried on the same host",
                !Transient(new Relic.Core.Install.DownloadVerifyException("incomplete")));
        }
        finally
        {
            listener.Stop();
            try { File.Delete(dest); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// The download PLAN: which hosts are tried, in what order, and — the load-bearing part — with
    /// whose byte count. The sizes below are the real ones of two builds:
    /// the CDN's <c>1.6.0.zip</c>, and the <c>1.6.1</c> that Drive and archive.org both
    /// serve, exactly 16.384 bytes shorter. The catalogue names 1.6.1 on every host,
    /// so the case is kept synthetic here ON PURPOSE: a mirror is free to diverge, and if one
    /// ever inherits <c>GameSourceInfo.Size</c>, <c>Verify</c> calls a FINISHED 15 GB download
    /// "incomplete", deletes it and starts over — forever, with no error the user could act on.
    /// No network: pure ordering + bookkeeping, precisely the half that must never regress silently.
    /// </summary>
    static void MirrorPlanChecks(Action<string, bool> check)
    {
        const long CdnSize = 14_961_007_704L;   // GenshinImpact_1.6.0.zip
        const long MirrorSize = 14_960_991_320L; // GenshinImpact_1.6.1.zip — 16.384 bytes shorter
        var src = new GameSourceInfo
        {
            CdnUrl = "https://autopatchhk.yuanshen.com/x/GenshinImpact_1.6.0.zip",
            DriveId = "DRIVEID",
            Size = CdnSize,
            Mirrors =
            {
                new GameMirrorInfo
                {
                    Id = "archive", Label = "Internet Archive", Size = MirrorSize,
                    Url = "https://archive.org/download/GenshinImpact_1.6.1/GenshinImpact_1.6.1.zip",
                },
            },
        };

        var onCdn = InstallService.BuildCandidates(src, "cdn");
        var onDrive = InstallService.BuildCandidates(src, "drive");
        var onMirror = InstallService.BuildCandidates(src, "archive");

        check("plan: every host is offered exactly once", onCdn.Count == 3 && onDrive.Count == 3 && onMirror.Count == 3);
        check("plan: the chosen host goes first (cdn)", onCdn[0].Id == "cdn");
        check("plan: the chosen host goes first (drive)", onDrive[0].Id == "drive");
        check("plan: the chosen host goes first (mirror)", onMirror[0].Id == "archive");

        // The CDN leads the leftovers (no quota, and much faster than the mirror) and Drive
        // trails them: its failure mode is a daily quota shared across the owner's files, so it is the
        // host most likely to be refusing exactly when a fallback runs, and each try burns more quota.
        check("plan: leftovers are ordered cdn → mirror → drive",
            onMirror[1].Id == "cdn" && onMirror[2].Id == "drive" &&
            onDrive[1].Id == "cdn" && onDrive[2].Id == "archive");

        // THE regression this whole type exists to prevent.
        check("plan: the mirror carries ITS OWN size, not the CDN's",
            onCdn.Single(c => c.Id == "archive").Size == MirrorSize);
        check("plan: the CDN keeps the catalogue size",
            onCdn.Single(c => c.Id == "cdn").Size == CdnSize);
        // Drive's copy is not in the catalogue at all (same 1.6.1 build, no size recorded), so the
        // length has to come from ProbeAsync before the transfer — never from src.Size.
        check("plan: Drive has no assumed size (the probe reports it)",
            onCdn.Single(c => c.Id == "drive").Size is null);

        // A saved source the chosen version does not carry ("archive" while installing 2.8) must not
        // empty the plan or throw — it just leaves the default order, with the CDN first.
        var noMirror = new GameSourceInfo { CdnUrl = src.CdnUrl, DriveId = "DRIVEID", Size = CdnSize };
        var plan28 = InstallService.BuildCandidates(noMirror, "archive");
        check("plan: an unknown source id falls back to the default order",
            plan28.Count == 2 && plan28[0].Id == "cdn" && plan28[1].Id == "drive");

        // Half-filled catalogue entries must not produce empty urls to download from.
        var cdnOnly = new GameSourceInfo { CdnUrl = src.CdnUrl, Size = CdnSize };
        check("plan: a version without Drive or mirrors still has the CDN",
            InstallService.BuildCandidates(cdnOnly, "drive") is [{ Id: "cdn" }]);
        var blank = new GameSourceInfo { Mirrors = { new GameMirrorInfo { Id = "", Url = "", Size = 1 } } };
        check("plan: blank urls/ids are dropped rather than attempted",
            InstallService.BuildCandidates(blank, "cdn").Count == 0);

        // Whose host wins when a partial from a DIFFERENT host is already on disk. Both answers are
        // expensive when wrong: forgetting an automatic fallback re-downloads 15-32 GB at every
        // pause/resume, and overriding the user keeps pulling from the host they just walked away from.
        check("marker: an automatic fallback survives pause/resume (same setting)",
            InstallService.ShouldResumeOtherHost("drive", "drive"));
        check("marker: changing Source in Settings beats the marker",
            !InstallService.ShouldResumeOtherHost("drive", "cdn"));
        check("marker: switching TO a mirror beats the marker too",
            !InstallService.ShouldResumeOtherHost("cdn", "archive"));
        check("marker: a legacy url-only marker is still adopted",
            InstallService.ShouldResumeOtherHost(null, "cdn"));
        check("marker: the setting compares case-insensitively",
            InstallService.ShouldResumeOtherHost("Drive", "drive"));
    }

    /// <summary>
    /// The catalogue's voice-pack representation (no network): the legacy "audio" key folds into
    /// voices["English(US)"], an unknown language never reaches a file name, a pack with no host is
    /// dropped at load, the cache file names are stable (English KEEPS audio_&lt;id&gt;.zip so an
    /// in-flight partial resumes), and the committed sample really carries all four languages with
    /// per-host sizes that match the verified byte counts.
    /// </summary>
    static void CatalogueVoiceChecks(Action<string, bool> check)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "relic-catalogue-spike");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        try
        {
            GameVersionInfo LoadOne(string name, string json)
            {
                string path = Path.Combine(tmp, name);
                File.WriteAllText(path, json);
                return VersionCatalog.Load(path).Single();
            }
            const string Pack = """{ "cdnUrl": "https://cdn.example/x.zip", "driveId": "", "size": 10, "mirrors": [] }""";

            // (1) legacy 'audio' only — the shape of every catalogue written before 'voices' existed.
            var legacy = LoadOne("legacy.json",
                $$"""{ "versions": [ { "id": "1.6", "client": {{Pack}}, "audio": {{Pack.Replace("10", "7")}} } ] }""");
            check("legacy 'audio' becomes voices[English(US)]", legacy.VoicePack("English(US)")?.Size == 7);
            check("legacy 'audio' does not survive Load (Audio == null)", legacy.Audio is null);
            check("DefaultInstallSize = client + English", legacy.DefaultInstallSize == 17);

            // (2) 'audio' AND voices.English(US): the explicit entry wins, nothing is duplicated.
            var both = LoadOne("both.json",
                $$"""{ "versions": [ { "id": "1.6", "client": {{Pack}}, "audio": {{Pack.Replace("10", "7")}}, "voices": { "English(US)": {{Pack.Replace("10", "9")}} } } ] }""");
            check("an explicit voices.English(US) wins over legacy 'audio'",
                both.VoicePack("English(US)")?.Size == 9 && both.Voices.Count == 1);

            // (3) an unknown key is dropped; case-insensitive keys are re-spelt to the allowlist.
            var keys = LoadOne("keys.json",
                $$"""{ "versions": [ { "id": "1.6", "client": {{Pack}}, "voices": { "Klingon": {{Pack}}, "english(us)": {{Pack.Replace("10", "5")}}, "JAPANESE": {{Pack}} } } ] }""");
            check("an unknown language key is dropped", !keys.Voices.ContainsKey("Klingon") && keys.Voices.Count == 2);
            check("keys are re-spelt to the canonical name",
                keys.Voices.Keys.Contains("English(US)", StringComparer.Ordinal) && keys.Voices.Keys.Contains("Japanese", StringComparer.Ordinal));
            check("VoicePack() is case-insensitive", keys.VoicePack("japanese") is not null && keys.VoicePack("English(us)")?.Size == 5);
            check("VoicePacks() reports in display order",
                keys.VoicePacks().Select(p => p.Lang).SequenceEqual(new[] { "English(US)", "Japanese" }));

            // (4) a pack with no host at all is dropped at load — it could only ever fail hours later.
            var nohost = LoadOne("nohost.json",
                """{ "versions": [ { "id": "1.6", "client": { "cdnUrl": "https://cdn.example/c.zip", "size": 1 }, "voices": { "Korean": { "cdnUrl": "", "driveId": "", "size": 3, "mirrors": [ { "id": "", "url": "" } ] }, "Chinese": { "mirrors": [ { "id": "archive", "url": "https://archive.example/z.zip", "size": 4 } ] } } } ] }""");
            check("a pack with no url/mirror/Drive is dropped", !nohost.Voices.ContainsKey("Korean"));
            check("a mirror-only pack is kept", nohost.VoicePack("Chinese") is not null);
            check("no English(US) pack is allowed (logged, not fatal)",
                nohost.VoicePack("English(US)") is null && nohost.DefaultInstallSize == 1);

            // (5) cache file names — English keeps the historical name so a partial from the previous
            // build resumes through its .src marker; the others get a slug, never the raw name.
            string c = Path.Combine(tmp, "cache");
            check("VoiceCacheFile: English(US) keeps audio_<id>.zip",
                InstallService.VoiceCacheFile(c, "1.6", "English(US)").EndsWith("audio_1.6.zip"));
            check("VoiceCacheFile: Japanese -> audio_<id>_ja.zip",
                InstallService.VoiceCacheFile(c, "1.6", "Japanese").EndsWith("audio_1.6_ja.zip"));
            check("VoiceCacheFile: Chinese -> zh, Korean -> ko (case-insensitive input)",
                InstallService.VoiceCacheFile(c, "2.8", "Chinese").EndsWith("audio_2.8_zh.zip")
                && InstallService.VoiceCacheFile(c, "2.8", "korean").EndsWith("audio_2.8_ko.zip"));
            bool badThrew = false;
            try { InstallService.VoiceCacheFile(c, "1.6", "Klingon"); } catch (ArgumentException) { badThrew = true; }
            check("VoiceCacheFile refuses an unknown language (never a raw name in a path)", badThrew);
            check("ClientCacheFile is client_<id>.zip", InstallService.ClientCacheFile(c, "2.8").EndsWith("client_2.8.zip"));

            // ResolveVoices: what a request really means, refused BEFORE any byte moves.
            var resolved = InstallService.ResolveVoices(keys, new[] { "japanese", "English(US)", "JAPANESE" });
            check("ResolveVoices canonicalises + de-duplicates in request order",
                resolved.SequenceEqual(new[] { "Japanese", "English(US)" }));
            bool noneThrew = false, unknownThrew = false, missingThrew = false;
            try { InstallService.ResolveVoices(keys, Array.Empty<string>()); } catch (InvalidOperationException) { noneThrew = true; }
            try { InstallService.ResolveVoices(keys, new[] { "Klingon" }); } catch (InvalidOperationException) { unknownThrew = true; }
            try { InstallService.ResolveVoices(keys, new[] { "Korean" }); } catch (InvalidOperationException) { missingThrew = true; }
            check("ResolveVoices: empty / unknown / not-in-catalogue are refused", noneThrew && unknownThrew && missingThrew);

            // VoiceLanguages: the one vocabulary, and "installed" = the marker file, not the folder.
            check("VoiceLanguages.All is the four release-index names in display order",
                VoiceLanguages.All.SequenceEqual(new[] { "English(US)", "Chinese", "Japanese", "Korean" }));
            check("Canonical trims + ignores case; unknown -> null",
                VoiceLanguages.Canonical(" korean ") == "Korean" && VoiceLanguages.Canonical("Klingon") is null && !VoiceLanguages.IsKnown(""));
            string game = Path.Combine(tmp, "game");
            Directory.CreateDirectory(VoiceLanguages.SoundBanksDir(game, "Korean"));
            File.WriteAllText(VoiceLanguages.PkgVersionFile(game, "Japanese"), "{}");
            check("PkgVersionFile / SoundBanksDir spell the language as the game does",
                VoiceLanguages.PkgVersionFile(game, "english(us)").EndsWith("Audio_English(US)_pkg_version")
                && VoiceLanguages.SoundBanksDir(game, "korean").EndsWith(Path.Combine("GeneratedSoundBanks", "Windows", "Korean")));
            check("Detect: the marker file counts, a bare sound-bank folder does not",
                VoiceLanguages.Detect(game).SequenceEqual(new[] { "Japanese" }));
            check("IsOnDisk never throws on garbage",
                !VoiceLanguages.IsOnDisk("", "Japanese") && !VoiceLanguages.IsOnDisk(game, "Klingon"));

            // (6) the committed sample: all four languages per version, per-host sizes, the verified
            // byte counts, no Drive ids, and a non-English pack plans archive -> cdn when asked to.
            string? sample = FindUp("config/versions.sample.json");
            if (sample is null)
            {
                Console.WriteLine("  [SKIP] config/versions.sample.json not found from here");
            }
            else
            {
                var cat = VersionCatalog.Load(sample);
                var sizes = new Dictionary<string, long>
                {
                    ["1.6|English(US)"] = 3_708_267_740, ["1.6|Chinese"] = 3_463_112_481,
                    ["1.6|Japanese"] = 3_939_900_622, ["1.6|Korean"] = 3_246_209_458,
                    ["2.8|English(US)"] = 8_575_243_551, ["2.8|Chinese"] = 7_441_982_887,
                    ["2.8|Japanese"] = 9_363_410_719, ["2.8|Korean"] = 7_279_819_313,
                };
                check("sample: versions 1.6 and 2.8", cat.Count == 2 && cat.Any(v => v.Id == "1.6") && cat.Any(v => v.Id == "2.8"));
                foreach (var v in cat)
                {
                    check($"sample {v.Id}: all four languages", VoiceLanguages.All.All(l => v.VoicePack(l) is not null));
                    foreach (var (lang, pack) in v.VoicePacks())
                    {
                        var archive = pack.Mirrors.FirstOrDefault(m => m.Id == "archive");
                        check($"sample {v.Id} {lang}: CDN url + size, archive mirror with the SAME size",
                            !string.IsNullOrWhiteSpace(pack.CdnUrl) && pack.Size > 0
                            && archive is not null && !string.IsNullOrWhiteSpace(archive.Url) && archive.Size == pack.Size);
                        check($"sample {v.Id} {lang}: size is the verified {sizes[$"{v.Id}|{lang}"]}", pack.Size == sizes[$"{v.Id}|{lang}"]);
                        // The CDN spells the parentheses raw, archive.org percent-encodes them — and the
                        // .src marker compares urls byte for byte, so the spelling must not drift.
                        check($"sample {v.Id} {lang}: urls name the language as the game does",
                            pack.CdnUrl.Contains($"Audio_{lang}_", StringComparison.Ordinal)
                            && (archive?.Url.Contains(lang == "English(US)" ? "Audio_English%28US%29_" : $"Audio_{lang}_", StringComparison.Ordinal) ?? false));
                    }
                    check($"sample {v.Id}: no Drive ids in the committed file",
                        !v.Voices.Values.Any(p => !string.IsNullOrWhiteSpace(p.DriveId)));
                    var plan = InstallService.BuildCandidates(v.VoicePack("Japanese")!, "archive");
                    check($"sample {v.Id}: a non-English pack on 'archive' plans archive -> cdn",
                        plan.Count == 2 && plan[0].Id == "archive" && plan[1].Id == "cdn");
                }
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }
    }

    static void PauseResumeChecks(Action<string, bool> check)
    {
        const int Total = 512 * 1024;
        const int FirstBurst = 128 * 1024;
        var payload = new byte[Total];
        new Random(42).NextBytes(payload);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var rangeStarts = new List<long>(); // start offset per request; -1 = no Range header
        int served = 0;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch { return; } // listener stopped — spike is done
                int reqNo = Interlocked.Increment(ref served);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var c = client;
                        var ns = c.GetStream();
                        var buf = new byte[8192];
                        var req = new StringBuilder();
                        while (!req.ToString().Contains("\r\n\r\n"))
                        {
                            int k = await ns.ReadAsync(buf);
                            if (k <= 0) return;
                            req.Append(Encoding.ASCII.GetString(buf, 0, k));
                        }
                        var m = Regex.Match(req.ToString(), @"Range:\s*bytes=(\d+)-", RegexOptions.IgnoreCase);
                        long start = m.Success ? long.Parse(m.Groups[1].Value) : -1;
                        lock (rangeStarts) rangeStarts.Add(start);
                        int from = (int)Math.Max(0, start);
                        string head = start >= 0
                            ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {from}-{Total - 1}/{Total}\r\nContent-Length: {Total - from}\r\nConnection: close\r\n\r\n"
                            : $"HTTP/1.1 200 OK\r\nContent-Length: {Total}\r\nConnection: close\r\n\r\n";
                        await ns.WriteAsync(Encoding.ASCII.GetBytes(head));
                        if (reqNo == 1)
                        {
                            // Burst, then hold the socket open until the client hangs up: the cancel
                            // must land while the transfer is provably unfinished.
                            await ns.WriteAsync(payload.AsMemory(from, FirstBurst));
                            while (await ns.ReadAsync(buf) > 0) { } // drains until the client closes
                        }
                        else
                        {
                            await ns.WriteAsync(payload.AsMemory(from));
                        }
                    }
                    catch { /* client aborted mid-write — expected for request #1 */ }
                });
            }
        });

        string dest = Path.Combine(Path.GetTempPath(), $"relic_spike_dl_{Environment.ProcessId}.bin");
        try
        {
            if (File.Exists(dest)) File.Delete(dest);
            string url = $"http://127.0.0.1:{port}/spike.bin";

            using var cts = new CancellationTokenSource();
            bool cancelled = false;
            try
            {
                Downloader.DownloadResumableAsync(url, dest, new CancelAtProgress(64 * 1024, cts), cts.Token)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { cancelled = true; }
            long partial = File.Exists(dest) ? new FileInfo(dest).Length : 0;
            check("cancel mid-download surfaces OperationCanceledException", cancelled);
            check($"partial file survives the cancel ({partial / 1024} KB of {Total / 1024} KB)",
                partial > 0 && partial < Total);

            Downloader.DownloadResumableAsync(url, dest).GetAwaiter().GetResult();
            long resumedFrom;
            lock (rangeStarts) resumedFrom = rangeStarts.Count >= 2 ? rangeStarts[1] : -1;
            check("resume requests exactly the partial offset (Range header)", resumedFrom == partial);
            byte[] got = File.ReadAllBytes(dest);
            check("resumed file is byte-identical to the source", got.AsSpan().SequenceEqual(payload));
        }
        finally
        {
            listener.Stop();
            try { File.Delete(dest); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>IProgress that cancels once Received crosses the threshold. Deliberately NOT
    /// Progress&lt;T&gt;: this fires synchronously inside the download loop, so the cancel lands
    /// deterministically mid-transfer instead of racing a thread-pool callback.</summary>
    sealed class CancelAtProgress(long at, CancellationTokenSource cts) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress p) { if (p.Received >= at) cts.Cancel(); }
    }

    static string? FindUp(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }
}

/// <summary>
/// Non-destructive Fiddler checks: CustomRules templating (host/port substitution, no leftover
/// placeholders, telemetry blocks present), writing rules to a TEMP path, and read-only install /
/// trust queries. Does NOT install Fiddler or trust any certificate — those are gated to the test box.
/// </summary>
static class FiddlerSpike
{
    public static int Run()
    {
        Console.WriteLine("== fiddler spike: rules templating + read-only checks (non-destructive) ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        string js = FiddlerRules.Render("game.example.com", 21000);
        Check("host substituted", js.Contains("oS.host = \"game.example.com\";"));
        Check("port substituted", js.Contains("oS.port = 21000;"));
        Check("no leftover __HOST__/__PORT__ placeholders", !js.Contains("__HOST__") && !js.Contains("__PORT__"));
        Check("telemetry blocks present", js.Contains("/crash/dataUpload") && js.Contains("/sdk/dataUpload"));
        Check("all three official domains redirected",
            js.Contains(".yuanshen.com") && js.Contains(".hoyoverse.com") && js.Contains(".mihoyo.com"));

        // Decrypting the WHOLE machine's HTTPS would make a running Fiddler break every HSTS-preloaded
        // site (google, youtube) — the browser has to accept a Fiddler-minted certificate for each one,
        // and there is no "continue anyway" on those. Only the game's domains are decrypted.
        Check("non-game CONNECTs are tunnelled, not decrypted",
            js.Contains("oS.HTTPMethodIs(\"CONNECT\")") && js.Contains("oS[\"x-no-decrypt\"]"));
        // The whole gate hinges on this one word: on a CONNECT session `host` is always "host:443", so
        // gating on `host` matches no game domain either — we would tunnel the GAME and the redirect
        // would silently never fire again.
        Check("the CONNECT gate compares on hostname, not host", js.Contains("var target = oS.hostname"));
        // Two lists that can drift apart = a game host that is redirected but no longer decrypted (or
        // a host decrypted for nothing). Each domain must appear in isGameHost AND in redirects.
        static int CountOf(string hay, string needle)
        {
            int n = 0, i = 0;
            while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }
        foreach (var d in new[] { ".yuanshen.com", ".hoyoverse.com", ".mihoyo.com" })
            Check($"{d} listed for both decryption and redirect", CountOf(js, d) >= 2);

        // The bootstrap script is what actually gets a root certificate onto a fresh machine (Fiddler
        // only mints one from its own UI checkbox), so its two calls are load-bearing — and the
        // absent third one is an invariant: trusting the root stays the user's consented decision.
        string boot = FiddlerRules.RenderCertBootstrap();
        Check("bootstrap asks Fiddler for a root cert", boot.Contains("CertMaker.createRootCert()"));
        // Main() inside class Handlers is what Fiddler runs on every script compile (its own
        // SampleRules.js says so) — get either half wrong and the script loads but does nothing.
        Check("bootstrap uses Fiddler's script entry point",
            boot.Contains("class Handlers") && boot.Contains("static function Main()"));
        Check("bootstrap imports Fiddler so CertMaker resolves", boot.Contains("import Fiddler;"));
        // Guarding on rootCertExists() would be validity-blind and would make an expired root
        // permanently unrepairable — the caller has already proven there is no usable one.
        Check("bootstrap does not guard on rootCertExists", !boot.Contains("CertMaker.rootCertExists"));
        Check("bootstrap never trusts the cert itself", !boot.Contains("trustRootCert"));
        Check("bootstrap carries no unrendered placeholders", !boot.Contains("__HOST__") && !boot.Contains("__PORT__"));
        Check("bootstrap is not the redirect script", !boot.Contains("OnBeforeRequest"));

        bool threw = false;
        try { FiddlerRules.Render("", 21000); } catch (ArgumentException) { threw = true; }
        Check("empty host rejected", threw);
        threw = false;
        try { FiddlerRules.Render("h", 70000); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check("out-of-range port rejected", threw);

        string tmp = Path.Combine(Path.GetTempPath(), "relic_fiddlerspike_" + Guid.NewGuid().ToString("N")[..8], "CustomRules.js");
        try
        {
            FiddlerAutomation.WriteCustomRules("192.0.2.57", 21000, tmp);
            Check("WriteCustomRules created the file", File.Exists(tmp));
            Check("written file has the server host", File.ReadAllText(tmp).Contains("192.0.2.57"));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(tmp)!, true); } catch { }
        }

        // read-only queries must not throw
        bool installed = FiddlerAutomation.IsInstalled;
        bool trusted = FiddlerAutomation.IsRootCertTrusted();
        bool usable = FiddlerAutomation.IsRootCertUsable();
        Console.WriteLine($"    (info) Fiddler installed here: {installed}; root cert trusted: {trusted}; usable: {usable}");
        Check("IsInstalled query ran", true);
        Check("IsRootCertTrusted query ran", true);
        // "Usable" is the stricter of the two by construction: it additionally demands a
        // currently-valid copy in CurrentUser\My whose thumbprint matches the trusted one.
        Check("usable implies trusted", !usable || trusted);

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"fiddler spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }
}

/// <summary>
/// Validates the DIRECT (no-SSH) agent path: AgentClient in "direct" mode against an in-process
/// stub that speaks the agent's HTTP protocol (bearer auth, /status, /command, JSON errors).
/// Proves: direct connect without SSH, token sent correctly, HTTP failures surfaced as errors
/// (not fake-OK), and the request bodies the real agent will receive. Loopback only, no network.
/// Optional live check against a real agent:  set RELIC_AGENT_HOST / RELIC_AGENT_TOKEN
/// (and RELIC_AGENT_PORT, default 18080) — runs a read-only /status + a 401 probe.
/// </summary>
static class AgentSpike
{
    const string Token = "SPIKETOKEN-0123456789abcdef";

    public static int Run()
    {
        Console.WriteLine("== agent spike: direct-mode AgentClient vs in-process stub agent ==");
        Console.WriteLine($"    (info) BuildDefaults: server={BuildDefaults.ServerHost} " +
            $"token={(BuildDefaults.AgentToken is "" ? "ABSENT (dev build)" : "baked in")} " +
            $"builtInConfig={(ServerConfig.BuiltInDefault() is null ? "null" : "direct")}");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        var seen = new List<(string method, string path, string auth, string body, bool chunked)>();
        var serverTask = Task.Run(() => Serve(listener, seen, cts.Token));

        try
        {
            // The listener-pid lookup behind LocalAgent.FindRunning/Stop/Uninstall (netstat -ano parse),
            // proved on THIS process's own loopback listener: the one real listener a spike can vouch for.
            Check($"LocalAgent.ListeningPid finds this process behind its loopback listener (port {port})",
                LocalAgent.ListeningPid(port) == Environment.ProcessId);
            Check("LocalAgent.ListeningPid answers null for a port nobody listens on", LocalAgent.ListeningPid(1) is null);

            var good = new ServerConfig("127.0.0.1", 22, "", "", Token, port, "direct");
            WithClient(good, a =>
            {
                Check("direct connect needs no SSH fields", a.IsConnected);

                var st = a.StatusAsync().GetAwaiter().GetResult();
                Check("/status returns the stub's payload",
                    st.GetProperty("versions").GetProperty("1.6").GetProperty("up").GetBoolean());
                Check("bearer token sent on /status", seen[^1].auth == $"Bearer {Token}");

                var cmd = a.CommandAsync("1.6", "10001", "item add 202 555").GetAwaiter().GetResult();
                Check("/command round-trips OK", cmd.GetProperty("ok").GetBoolean());
                Check("/command body carries version+uid+msg",
                    seen[^1].body.Contains("\"version\":\"1.6\"") &&
                    seen[^1].body.Contains("\"uid\":\"10001\"") &&
                    seen[^1].body.Contains("\"msg\":\"item add 202 555\""));
                // The regression this stub would hide: PostAsJsonAsync sends the body
                // Transfer-Encoding: chunked (JsonContent has no known length), and an agent whose
                // Python http.server reads Content-Length ONLY — it never dechunks — sees an
                // empty body and answers "400 missing/invalid field: 'version'" to every POST.
                // The stub dechunks, so only this check can catch it.
                Check("POST body is counted, not chunked (Python http.server cannot dechunk)",
                    !seen[^1].chunked);

                // The agent refuses a reset without confirm:true, so the body must carry it.
                var reset = a.StateResetAsync().GetAwaiter().GetResult();
                Check("/agent/state/reset posts a counted {\"confirm\":true} body",
                    reset.GetProperty("ok").GetBoolean() && seen[^1].path == "/agent/state/reset"
                    && seen[^1].body.Contains("\"confirm\":true") && !seen[^1].chunked);

                // The voice-pack selection of the hotfix mirror — a JOB, and the first body that
                // carries a list. Same framing rule as every POST (the stub reads an unchunked body only
                // off a Content-Length), then the follow: 202 + job id, polled with the previous
                // snapshot's "next" until the state leaves "running".
                // A job follow has no end of its own. A client that stopped carrying "next" forward is
                // served the "running" snapshot for ever, and a poll the stub does not know is a 404 that
                // throws. Neither may hang or abort the run: every follow gets a 15 s deadline, and a hang
                // or a throw comes back as (null, why) for the checks below to FAIL on.
                (JsonElement? snap, string? error) Follow(Func<CancellationToken, Task<JsonElement>> call)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try { return (call(deadline.Token).GetAwaiter().GetResult(), null); }
                    catch (OperationCanceledException) { return (null, "no end within 15 s"); }
                    catch (InvalidOperationException ex) { return (null, ex.Message); }
                }
                // The stub's threads append to "seen"; after a follow that was cut short one may still be.
                List<(string method, string path, string auth, string body, bool chunked)> Since(int from)
                {
                    lock (seen) return seen.Skip(from).ToList();
                }
                static JsonElement? ParseBody(string json)
                {
                    try { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
                    catch (JsonException) { return null; }
                }

                int before = seen.Count;
                var voiceLog = new List<string>();
                var (voice, voiceFail) = Follow(ct => a.HotpatchVoiceAsync("1.6", ["English(US)", "Japanese"],
                    purge: true, onLine: voiceLog.Add, ct: ct));
                Check("/server/hotpatch/voice: the job follow ends within its 15 s deadline"
                    + (voiceFail is null ? "" : $" ({voiceFail})"), voice is not null);
                var voiceReqs = Since(before);
                Check("/server/hotpatch/voice posts a counted body, not chunked",
                    voiceReqs.Count > 0 && voiceReqs[0].method == "POST" && voiceReqs[0].path == "/server/hotpatch/voice"
                    && voiceReqs[0].body.Length > 0 && !voiceReqs[0].chunked);
                var vb = voiceReqs.Count > 0 ? ParseBody(voiceReqs[0].body) : null;
                Check("/server/hotpatch/voice body: version + languages as an ARRAY of the given names + purge as a bool",
                    vb is { ValueKind: JsonValueKind.Object } b
                    && b.TryGetProperty("version", out var bVersion) && bVersion.ValueKind == JsonValueKind.String
                    && bVersion.GetString() == "1.6"
                    && b.TryGetProperty("languages", out var bLangs) && bLangs.ValueKind == JsonValueKind.Array
                    && bLangs.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : null)
                        .SequenceEqual(new[] { "English(US)", "Japanese" })
                    && b.TryGetProperty("purge", out var bPurge) && bPurge.ValueKind == JsonValueKind.True);
                Check("/server/hotpatch/voice follows its job to the end (since=0, then the snapshot's next), token on every poll",
                    voiceReqs.Select(r => r.path).SequenceEqual(new[]
                        { "/server/hotpatch/voice", "/jobs/voice-packs?since=0", "/jobs/voice-packs?since=1" })
                    && voiceReqs.All(r => r.auth == $"Bearer {Token}"));
                Check("/server/hotpatch/voice streams every job line once and returns the final snapshot",
                    voice is { } done
                    && voiceLog.SequenceEqual(new[] { "voice packs: 186 files to fetch", "voice packs: done" })
                    && done.GetProperty("state").GetString() == "done"
                    && done.GetProperty("result").GetProperty("voice").GetArrayLength() == 2);

                // No language = serve none: the list must still go out as [] (never null, never omitted),
                // and purge defaults to a real false — with purge the same call deletes every cached pack.
                before = seen.Count;
                var (voiceNone, _) = Follow(ct => a.HotpatchVoiceAsync("2.8", [], ct: ct));
                var noneReqs = Since(before);
                Check("/server/hotpatch/voice with no language posts \"languages\":[] + \"purge\":false, counted",
                    noneReqs.Count > 0 && noneReqs[0].body.Contains("\"languages\":[]")
                    && noneReqs[0].body.Contains("\"purge\":false") && !noneReqs[0].chunked
                    && voiceNone is { } none && none.GetProperty("state").GetString() == "done");

                // Failed packs end the job in error (after the selection was saved): the agent's own
                // sentence is what the admin reads, not a generic failure. Same deadline: a follow that
                // never ends reports "no end within 15 s", which is not that sentence either.
                var (voiceBad, voiceErr) = Follow(ct => a.HotpatchVoiceAsync("0.0", ["Korean"], ct: ct));
                Check("/server/hotpatch/voice: a job that ends in error throws the agent's own message",
                    voiceBad is null && voiceErr == "2 of 84 voice pack files could not be fetched");

                // Stopping the running voice job is a plain POST to the SAME path — 200
                // {ok, stopping}, NOT a job. So: exactly one request (nothing is polled), a counted body that
                // carries version + cancel and nothing of a selection, and the agent's reply handed back as is.
                before = seen.Count;
                var (cancel, _) = Follow(ct => a.HotpatchVoiceCancelAsync("1.6", ct));
                var cancelReqs = Since(before);
                Check("/server/hotpatch/voice cancel posts the counted body {\"version\":\"1.6\",\"cancel\":true}, once, with the token",
                    cancelReqs.Count == 1 && cancelReqs[0].method == "POST" && cancelReqs[0].path == "/server/hotpatch/voice"
                    && cancelReqs[0].body == "{\"version\":\"1.6\",\"cancel\":true}" && !cancelReqs[0].chunked
                    && cancelReqs[0].auth == $"Bearer {Token}");
                Check("/server/hotpatch/voice cancel returns the agent's {ok, stopping} (not followed as a job)",
                    cancel is { ValueKind: JsonValueKind.Object } stop
                    && stop.TryGetProperty("ok", out var stopOk) && stopOk.ValueKind == JsonValueKind.True
                    && stop.TryGetProperty("stopping", out var stopping) && stopping.ValueKind == JsonValueKind.True);

                // ── the bodies the real agent will receive for setup, provision, pathfinding and fetch ──
                // Setup carries the provisioning choice and the pathfinding TRI-STATE: an absent decision must
                // go out as null (the agent reads "not given"), never as false — a false would switch the
                // service off for every Prepare from a form that did not ask.
                before = seen.Count;
                var (setupPlain, setupPlainErr) = Follow(ct => a.SetupServerAsync("1.6", ct: ct));
                var setupPlainReqs = Since(before);
                var sp = setupPlainReqs.Count > 0 ? ParseBody(setupPlainReqs[0].body) : null;
                Check("/server/setup default body: progress \"default\", pathfinding NULL (not false), txtFixes null, force false, counted"
                      + (setupPlainErr is null ? "" : $" ({setupPlainErr})"),
                    setupPlain is not null && setupPlainReqs[0].path == "/server/setup" && !setupPlainReqs[0].chunked
                    && sp is { ValueKind: JsonValueKind.Object } spb
                    && spb.TryGetProperty("version", out var spv) && spv.GetString() == "1.6"
                    && spb.TryGetProperty("progress", out var spp) && spp.GetString() == "default"
                    && spb.TryGetProperty("pathfinding", out var sppf) && sppf.ValueKind == JsonValueKind.Null
                    && spb.TryGetProperty("txtFixes", out var sptf) && sptf.ValueKind == JsonValueKind.Null
                    && spb.TryGetProperty("force", out var spf) && spf.ValueKind == JsonValueKind.False);
                before = seen.Count;
                var (setupKeep, _) = Follow(ct => a.SetupServerAsync("1.6", progress: "keep", pathfinding: false, ct: ct));
                var setupKeepReqs = Since(before);
                Check("/server/setup body carries the chosen progress and an explicit pathfinding false",
                    setupKeep is not null && setupKeepReqs.Count > 0
                    && setupKeepReqs[0].body.Contains("\"progress\":\"keep\"") && setupKeepReqs[0].body.Contains("\"pathfinding\":false")
                    && !setupKeepReqs[0].chunked);
                before = seen.Count;
                var (prov, provErr) = Follow(ct => a.ProvisionServerAsync("1.6", progress: "fixes", ct: ct));
                var provReqs = Since(before);
                Check("/server/provision body: {version, txtFixes:null, progress}, counted, followed to done"
                      + (provErr is null ? "" : $" ({provErr})"),
                    prov is { } provDone && provDone.GetProperty("state").GetString() == "done"
                    && provReqs.Count > 0 && provReqs[0].path == "/server/provision" && !provReqs[0].chunked
                    && provReqs[0].body == "{\"version\":\"1.6\",\"txtFixes\":null,\"progress\":\"fixes\"}");

                // Pathfinding toggle: exactly {version, enabled} — a JSON bool, never a string.
                before = seen.Count;
                var (pf, pfErr) = Follow(ct => a.PathfindingSetAsync("1.6", false, ct: ct));
                var pfReqs = Since(before);
                Check("/server/pathfinding posts the counted body {\"version\":\"1.6\",\"enabled\":false} with the token, and follows the job"
                      + (pfErr is null ? "" : $" ({pfErr})"),
                    pfReqs.Count > 0 && pfReqs[0].method == "POST" && pfReqs[0].path == "/server/pathfinding"
                    && pfReqs[0].body == "{\"version\":\"1.6\",\"enabled\":false}" && !pfReqs[0].chunked
                    && pfReqs[0].auth == $"Bearer {Token}"
                    && pf is { } pfDone && pfDone.GetProperty("state").GetString() == "done"
                    && pfDone.GetProperty("result").GetProperty("enabled").ValueKind == JsonValueKind.False
                    && pfDone.GetProperty("result").GetProperty("applied").ValueKind == JsonValueKind.True);

                // Server package download: a job of its own path, {version} only; its cancel is the same
                // non-job POST shape the voice cancel has.
                before = seen.Count;
                var fetchLog = new List<string>();
                var (fetch, fetchErr) = Follow(ct => a.FetchServerAsync("1.6", fetchLog.Add, ct));
                var fetchReqs = Since(before);
                Check("/server/fetch posts the counted body {\"version\":\"1.6\"} and follows the job to its result"
                      + (fetchErr is null ? "" : $" ({fetchErr})"),
                    fetchReqs.Count > 0 && fetchReqs[0].method == "POST" && fetchReqs[0].path == "/server/fetch"
                    && fetchReqs[0].body == "{\"version\":\"1.6\"}" && !fetchReqs[0].chunked
                    && fetch is { } fetchDone && fetchDone.GetProperty("state").GetString() == "done"
                    && fetchDone.GetProperty("result").GetProperty("fetched").ValueKind == JsonValueKind.True
                    && fetchLog.SequenceEqual(new[] { "Downloading the 1.6 server package (1.87 GB) from archive.org", "Installed into /home/1.6_live." }));
                before = seen.Count;
                var (fetchCancel, _) = Follow(ct => a.FetchCancelAsync("1.6", ct));
                var fetchCancelReqs = Since(before);
                Check("/server/fetch cancel posts the counted body {\"version\":\"1.6\",\"cancel\":true}, once, and returns {ok, stopping}",
                    fetchCancelReqs.Count == 1 && fetchCancelReqs[0].path == "/server/fetch"
                    && fetchCancelReqs[0].body == "{\"version\":\"1.6\",\"cancel\":true}" && !fetchCancelReqs[0].chunked
                    && fetchCancel is { ValueKind: JsonValueKind.Object } fc
                    && fc.TryGetProperty("stopping", out var fcs) && fcs.ValueKind == JsonValueKind.False);

                // ── admin account creation ──
                // Exactly {version, name, template} — the password key only when one was typed ("" and null
                // are the ABSENT key, never "password":"" / null) — as a counted body, then the job followed
                // like every other; the result (with the password the server generated) comes back whole.
                before = seen.Count;
                var acctLog = new List<string>();
                var (acct, acctErr) = Follow(ct => a.AccountCreateAsync("1.6", "friend1", null, "post-gaa", acctLog.Add, ct));
                var acctReqs = Since(before);
                Check("/server/account/create posts the counted body {\"version\":\"1.6\",\"name\":\"friend1\",\"template\":\"post-gaa\"} with the token"
                      + (acctErr is null ? "" : $" ({acctErr})"),
                    acctReqs.Count > 0 && acctReqs[0].method == "POST" && acctReqs[0].path == "/server/account/create"
                    && acctReqs[0].body == "{\"version\":\"1.6\",\"name\":\"friend1\",\"template\":\"post-gaa\"}"
                    && !acctReqs[0].chunked && acctReqs.All(r => r.auth == $"Bearer {Token}"));
                Check("/server/account/create follows its job to done, streams the line and returns the result (generated password included)",
                    acctReqs.Select(r => r.path).SequenceEqual(new[] { "/server/account/create", "/jobs/acct-ok?since=0" })
                    && acctLog.SequenceEqual(new[] { "Admin account creation: name=friend1 version=1.6 template=post-gaa" })
                    && acct is { } acctDone && acctDone.GetProperty("state").GetString() == "done"
                    && acctDone.GetProperty("result").GetProperty("name").GetString() == "friend1"
                    && acctDone.GetProperty("result").GetProperty("uid").GetInt32() == 10042
                    && acctDone.GetProperty("result").GetProperty("passwordGenerated").ValueKind == JsonValueKind.True
                    && acctDone.GetProperty("result").GetProperty("password").GetString() == "Gen3rated-Pw");
                before = seen.Count;
                var (acctPw, acctPwErr) = Follow(ct => a.AccountCreateAsync("1.6", "friend1", "Secret12", "post-gaa", ct: ct));
                var acctPwReqs = Since(before);
                Check("/server/account/create with a typed password: the body ends ,\"password\":\"Secret12\"}, counted"
                      + (acctPwErr is null ? "" : $" ({acctPwErr})"),
                    acct is not null && acctPw is not null && acctPwReqs.Count > 0
                    && acctPwReqs[0].body == "{\"version\":\"1.6\",\"name\":\"friend1\",\"template\":\"post-gaa\",\"password\":\"Secret12\"}"
                    && !acctPwReqs[0].chunked);
                before = seen.Count;
                var (acctEmpty, _) = Follow(ct => a.AccountCreateAsync("1.6", "friend1", "", ct: ct));
                var acctEmptyReqs = Since(before);
                Check("/server/account/create: an empty password is the absent key, and the template defaults to fresh",
                    acctEmpty is not null && acctEmptyReqs.Count > 0
                    && acctEmptyReqs[0].body == "{\"version\":\"1.6\",\"name\":\"friend1\",\"template\":\"fresh\"}");
                // A 404 on the POST can only be an agent without the route (the route's own preflight answers
                // 400, never 404): the admin reads "upgrade the agent", not "error 404: not found".
                var (acctOld, acctOldErr) = Follow(ct => a.AccountCreateAsync("0.0", "friend1", null, ct: ct));
                Check("/server/account/create: a 404 on the POST (an agent without the route) reads as core.agent.accountCreateUnsupported",
                    acctOld is null && acctOldErr == L.T("core.agent.accountCreateUnsupported"));
                // ...while a 404 FOLLOWING the job is the forgotten job, with its own text — not the upgrade advice.
                before = seen.Count;
                var (acctGone, acctGoneErr) = Follow(ct => a.AccountCreateAsync("1.6", "gone1", null, ct: ct));
                var acctGoneReqs = Since(before);
                Check("/server/account/create: a 404 while following the job keeps its own text (not the upgrade advice)"
                      + (acctGoneErr is null ? "" : $" ({acctGoneErr})"),
                    acctGone is null && acctGoneErr is not null && acctGoneErr != L.T("core.agent.accountCreateUnsupported")
                    && acctGoneErr.Contains("404")
                    && acctGoneReqs.Select(r => r.path).SequenceEqual(new[] { "/server/account/create", "/jobs/acct-gone?since=0" }));

                // ── the agent's settings, its restart, and the stack relocation ──
                before = seen.Count;
                var (cfgGet, cfgGetErr) = Follow(ct => a.AgentConfigGetAsync(ct));
                var cfgGetReqs = Since(before);
                Check("/agent/config GET: one request with the token, the agent's body handed back (started readable as text)"
                      + (cfgGetErr is null ? "" : $" ({cfgGetErr})"),
                    cfgGetReqs.Count == 1 && cfgGetReqs[0].method == "GET" && cfgGetReqs[0].path == "/agent/config"
                    && cfgGetReqs[0].auth == $"Bearer {Token}"
                    && cfgGet is { } cg && AgentClient.StartedOf(cg) == "1790000000.123"
                    && cg.GetProperty("settings")[0].GetProperty("key").GetString() == "GIO_BIND_IP");
                Check("AgentClient.StartedOf: absent or not a number/string = \"\"",
                    AgentClient.StartedOf(ParseBody("""{"agent":"1.0"}""")!.Value) == ""
                    && AgentClient.StartedOf(ParseBody("""{"started":null}""")!.Value) == ""
                    && AgentClient.StartedOf(ParseBody("[]")!.Value) == "");

                // POST {"set": {...}}: the keys go out VERBATIM (a dictionary is never camelCased), every value a
                // string, as a counted body.
                before = seen.Count;
                var (cfgSet, cfgSetErr) = Follow(ct => a.AgentConfigSetAsync(new Dictionary<string, string> { ["GIO_BIND_IP"] = "192.0.2.10" }, ct));
                var cfgSetReqs = Since(before);
                Check("/agent/config POST sends the counted body {\"set\":{\"GIO_BIND_IP\":\"192.0.2.10\"}} with the token, once"
                      + (cfgSetErr is null ? "" : $" ({cfgSetErr})"),
                    cfgSetReqs.Count == 1 && cfgSetReqs[0].method == "POST" && cfgSetReqs[0].path == "/agent/config"
                    && cfgSetReqs[0].body == "{\"set\":{\"GIO_BIND_IP\":\"192.0.2.10\"}}" && !cfgSetReqs[0].chunked
                    && cfgSetReqs[0].auth == $"Bearer {Token}"
                    && cfgSet is { } cs && cs.GetProperty("applied")[0].GetString() == "GIO_BIND_IP");
                before = seen.Count;
                Follow(ct => a.AgentConfigSetAsync(new Dictionary<string, string>
                    { ["GIO_TLS_PINNED_FALLBACK"] = "0", ["GIO_FETCH_DISK_RESERVE"] = "4096", ["GIO_CA_FILE"] = @"C:\certs\corp root.pem" }, ct));
                var cfgSet2 = Since(before);
                Check("/agent/config POST: several keys keep their spelling and order, a Windows path is JSON-escaped, \"\" values allowed",
                    cfgSet2.Count == 1
                    && cfgSet2[0].body == "{\"set\":{\"GIO_TLS_PINNED_FALLBACK\":\"0\",\"GIO_FETCH_DISK_RESERVE\":\"4096\",\"GIO_CA_FILE\":\"C:\\\\certs\\\\corp root.pem\"}}");
                before = seen.Count;
                Follow(ct => a.AgentConfigSetAsync(new Dictionary<string, string> { ["GIO_7Z"] = "" }, ct));
                var cfgSet3 = Since(before);
                Check("/agent/config POST: an empty value (= remove the key's line) goes out as \"\", never null",
                    cfgSet3.Count == 1 && cfgSet3[0].body == "{\"set\":{\"GIO_7Z\":\"\"}}");

                // Restart: POST {} and nothing else; then the wait reads started until a NEW process answers —
                // through a failed poll (503) and one last answer of the old process.
                before = seen.Count;
                var (restart, restartErr) = Follow(ct => a.AgentRestartAsync(ct));
                var restartReqs = Since(before);
                Check("/agent/restart posts the counted body {} with the token, once, and returns {ok, restarting}"
                      + (restartErr is null ? "" : $" ({restartErr})"),
                    restartReqs.Count == 1 && restartReqs[0].method == "POST" && restartReqs[0].path == "/agent/restart"
                    && restartReqs[0].body == "{}" && !restartReqs[0].chunked && restartReqs[0].auth == $"Bearer {Token}"
                    && restart is { } rs && rs.GetProperty("restarting").ValueKind == JsonValueKind.True);
                before = seen.Count;
                RestartOutcome? back = null; string? backErr = null;
                try
                {
                    // The default grace (5 s): the old started answered at ~100 ms is the old process's last
                    // word, still "not yet".
                    back = a.WaitForRestartAsync("1790000000.123", TimeSpan.FromSeconds(10),
                        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)).GetAwaiter().GetResult();
                }
                catch (Exception ex) { backErr = ex.Message; }
                var backReqs = Since(before);
                Check("restart wait: a 503 and the old started (inside the grace) are \"not yet\"; the new started = Back (three GET /agent/config)"
                      + (backErr is null ? "" : $" ({backErr})"),
                    back == RestartOutcome.Back && backReqs.Count == 3 && backReqs.All(r => r.method == "GET" && r.path == "/agent/config"));

                // The respawn failed and the old process serves again (the agent's _restart_worker fallback): it
                // answers 200 with its OLD started. Past the grace that is NotRestarted at once — never a 60 s
                // wait ending in "did not answer" while the agent is up.
                var swWait = System.Diagnostics.Stopwatch.StartNew();
                RestartOutcome? kept = null; string? keptErr = null;
                before = seen.Count;
                try
                {
                    kept = a.WaitForRestartAsync("1790000100.456", TimeSpan.FromSeconds(10),
                        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), oldAnswerGrace: TimeSpan.FromMilliseconds(300))
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex) { keptErr = ex.Message; }
                long keptMs = swWait.ElapsedMilliseconds;
                var keptReqs = Since(before);
                Check($"restart wait: the old started answered past the grace = NotRestarted, long before the budget ({keptMs} ms)"
                      + (keptErr is null ? "" : $" ({keptErr})"),
                    kept == RestartOutcome.NotRestarted && keptMs >= 300 && keptMs < 5000
                    && keptReqs.Count >= 1 && keptReqs.All(r => r.method == "GET" && r.path == "/agent/config"));

                // The old started seen only inside the grace until the budget ends is not proof of a failed
                // restart: NoAnswer, on time.
                swWait.Restart();
                RestartOutcome? stale = null;
                try
                {
                    stale = a.WaitForRestartAsync("1790000100.456", TimeSpan.FromMilliseconds(600),
                        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)).GetAwaiter().GetResult();
                }
                catch (Exception ex) { backErr = ex.Message; }
                Check("restart wait: the old started only inside the grace, then the budget ends = NoAnswer (an outcome, not an exception), on time",
                    stale == RestartOutcome.NoAnswer && swWait.ElapsedMilliseconds < 5000);

                // Every poll fails (the agent never came back: 503 / refused) = NoAnswer, on time.
                swWait.Restart();
                RestartOutcome? gone = null; string? goneErr = null;
                _agentCfgDown = true;
                try
                {
                    gone = a.WaitForRestartAsync("1790000100.456", TimeSpan.FromMilliseconds(600),
                        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), oldAnswerGrace: TimeSpan.Zero)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex) { goneErr = ex.Message; }
                finally { _agentCfgDown = false; }
                Check("restart wait: every poll failing until the budget ends = NoAnswer (never NotRestarted without an answer), on time"
                      + (goneErr is null ? "" : $" ({goneErr})"),
                    gone == RestartOutcome.NoAnswer && swWait.ElapsedMilliseconds < 5000);

                // An agent without the route: /agent/config is a plain 404 — carried as AgentHttpException{404},
                // which is what the launcher turns into {tooOld: true} ("update the agent") instead of an error.
                _agentCfgMissing = true;
                int? oldStatus = null;
                try { a.AgentConfigGetAsync().GetAwaiter().GetResult(); }
                catch (AgentHttpException ex) { oldStatus = ex.Status; }
                catch (Exception) { oldStatus = -1; }
                finally { _agentCfgMissing = false; }
                Check("/agent/config on an agent without the route throws AgentHttpException with Status 404", oldStatus == 404);

                // Relocation: the dry run, the job, the cancel — three body shapes on one path.
                const string relocDir = @"C:\relic_servers\2.8_live";
                before = seen.Count;
                var (relocChk, relocChkErr) = Follow(ct => a.RelocateCheckAsync("2.8", relocDir, "move", ct));
                var relocChkReqs = Since(before);
                Check("/server/relocate check posts the counted body {\"version\":\"2.8\",\"dir\":\"C:\\\\relic_servers\\\\2.8_live\",\"mode\":\"move\",\"check\":true} once (not followed)"
                      + (relocChkErr is null ? "" : $" ({relocChkErr})"),
                    relocChkReqs.Count == 1 && relocChkReqs[0].method == "POST" && relocChkReqs[0].path == "/server/relocate"
                    && relocChkReqs[0].body == "{\"version\":\"2.8\",\"dir\":\"C:\\\\relic_servers\\\\2.8_live\",\"mode\":\"move\",\"check\":true}"
                    && !relocChkReqs[0].chunked && relocChkReqs[0].auth == $"Bearer {Token}"
                    && relocChk is { } rc && rc.GetProperty("ok").ValueKind == JsonValueKind.True
                    && rc.GetProperty("to").GetString() == relocDir && rc.GetProperty("problem").ValueKind == JsonValueKind.Null);
                before = seen.Count;
                var relocLog = new List<string>();
                var (reloc, relocErr) = Follow(ct => a.RelocateAsync("2.8", relocDir, "move", relocLog.Add, ct));
                var relocReqs = Since(before);
                Check("/server/relocate posts the counted body {\"version\":\"2.8\",\"dir\":\"…\",\"mode\":\"move\"} with the token"
                      + (relocErr is null ? "" : $" ({relocErr})"),
                    relocReqs.Count > 0 && relocReqs[0].method == "POST" && relocReqs[0].path == "/server/relocate"
                    && relocReqs[0].body == "{\"version\":\"2.8\",\"dir\":\"C:\\\\relic_servers\\\\2.8_live\",\"mode\":\"move\"}"
                    && !relocReqs[0].chunked && relocReqs.All(r => r.auth == $"Bearer {Token}"));
                Check("/server/relocate follows its job to done (since=0, then next), streams each line once, returns the result",
                    relocReqs.Select(r => r.path).SequenceEqual(new[] { "/server/relocate", "/jobs/reloc-28?since=0", "/jobs/reloc-28?since=1" })
                    && relocLog.SequenceEqual(new[] { @"Copying the 2.8 server to C:\relic_servers\2.8_live (another drive)", "GIO_DIR_28 saved; the old folder is deleted." })
                    && reloc is { } rd && rd.GetProperty("state").GetString() == "done"
                    && rd.GetProperty("result").GetProperty("crossVolume").ValueKind == JsonValueKind.True
                    && rd.GetProperty("result").GetProperty("to").GetString() == relocDir);
                before = seen.Count;
                Follow(ct => a.RelocateAsync("2.8", "", "repoint", ct: ct));
                var repointReqs = Since(before);
                Check("/server/relocate repoint with no folder sends \"dir\":\"\" (take the version off the agent), never null",
                    repointReqs.Count > 0 && repointReqs[0].body == "{\"version\":\"2.8\",\"dir\":\"\",\"mode\":\"repoint\"}");
                before = seen.Count;
                var (relocCancel, _) = Follow(ct => a.RelocateCancelAsync("2.8", ct));
                var relocCancelReqs = Since(before);
                Check("/server/relocate cancel posts the counted body {\"version\":\"2.8\",\"cancel\":true}, once, and returns {ok, stopping}",
                    relocCancelReqs.Count == 1 && relocCancelReqs[0].path == "/server/relocate"
                    && relocCancelReqs[0].body == "{\"version\":\"2.8\",\"cancel\":true}" && !relocCancelReqs[0].chunked
                    && relocCancelReqs[0].auth == $"Bearer {Token}"
                    && relocCancel is { ValueKind: JsonValueKind.Object } rcn
                    && rcn.TryGetProperty("stopping", out var rcs) && rcs.ValueKind == JsonValueKind.True);

                // ── gameplay settings ──
                // The read: one GET, the agent's answer handed back whole (the page reads the stored document,
                // the limits and the server's own original values out of it).
                before = seen.Count;
                var (twGet, twGetErr) = Follow(ct => a.TweaksGetAsync("1.6", ct));
                var twGetReqs = Since(before);
                Check("/server/tweaks GET: one request to /server/tweaks?version=1.6 with the token, the agent's body handed back"
                      + (twGetErr is null ? "" : $" ({twGetErr})"),
                    twGetReqs.Count == 1 && twGetReqs[0].method == "GET" && twGetReqs[0].path == "/server/tweaks?version=1.6"
                    && twGetReqs[0].auth == $"Bearer {Token}"
                    && twGet is { } tg && tg.GetProperty("available").ValueKind == JsonValueKind.True
                    && tg.GetProperty("tweaks").GetProperty("resin").GetProperty("cap").GetInt32() == 300
                    && tg.GetProperty("resin").GetProperty("cap").GetInt32() == 160
                    && tg.GetProperty("limits").GetProperty("resinCap")[1].GetInt32() == 2000
                    && tg.GetProperty("sections").GetProperty("quests").GetProperty("why").GetString() == "changed");
                // A version is data, never a piece of the path: it goes out percent-escaped, so nothing in it
                // can add a second query parameter. The agent's refusal (400) keeps its own text.
                before = seen.Count;
                var (_, twOddErr) = Follow(ct => a.TweaksGetAsync("1.6 &version=2.8/x", ct));
                var twOddReqs = Since(before);
                Check("/server/tweaks GET escapes the version (/server/tweaks?version=1.6%20%26version%3D2.8%2Fx), and a 400 keeps the agent's text",
                    twOddReqs.Count == 1 && twOddReqs[0].path == "/server/tweaks?version=1.6%20%26version%3D2.8%2Fx"
                    && twOddErr is not null && twOddErr.Contains("400") && twOddErr.Contains("unknown version"));

                // The write: exactly {version, tweaks}, the document as the page built it — the same keys in the
                // same order, ids still strings, nothing sorted, renamed or dropped — as a counted body, then the
                // job followed like every other and its result handed back.
                const string twDocJson = """{"resin":{"cap":300,"minutes":4},"abyss":{"slots":{"233701003":{"3001":21010201}},"levels":{"25":90}},"domains":{"multiplier":2,"cost":10,"byId":{"5006":{"multiplier":3,"cost":5,"extra":[{"item":104003,"count":3}]}}},"quests":{"multiplier":{"archon":2,"story":2,"world":2,"event":2,"commission":2},"byReward":{"100351":{"items":[{"item":201,"count":600},{"item":104003,"count":5}]}}}}""";
                var twDoc = ParseBody(twDocJson)!.Value;
                before = seen.Count;
                var twLog = new List<string>();
                var (tw, twErr) = Follow(ct => a.TweaksSetAsync("1.6", twDoc, twLog.Add, ct));
                var twReqs = Since(before);
                Check("/server/tweaks posts the counted body {\"version\":\"1.6\",\"tweaks\":<the document, untouched>} with the token"
                      + (twErr is null ? "" : $" ({twErr})"),
                    twReqs.Count > 0 && twReqs[0].method == "POST" && twReqs[0].path == "/server/tweaks"
                    && twReqs[0].body == "{\"version\":\"1.6\",\"tweaks\":" + twDocJson + "}"
                    && !twReqs[0].chunked && twReqs.All(r => r.auth == $"Bearer {Token}"));
                Check("/server/tweaks follows its job to done (since=0, then next), streams each line once, returns the result",
                    twReqs.Select(r => r.path).SequenceEqual(new[] { "/server/tweaks", "/jobs/tweaks-16?since=0", "/jobs/tweaks-16?since=1" })
                    && twLog.SequenceEqual(new[] { "Gameplay settings: 3 files to write (resin, abyss, domains, quests)", "The game server loaded the new settings." })
                    && tw is { } twDone && twDone.GetProperty("state").GetString() == "done"
                    && twDone.GetProperty("result").GetProperty("changed").GetInt32() == 3
                    && twDone.GetProperty("result").GetProperty("effect").GetString() == "live"
                    && twDone.GetProperty("result").GetProperty("clamped").GetInt32() == 0);
                // "Restore the original game" is the EMPTY document: it must go out as {} — never null, never
                // left out, both of which would leave the agent guessing.
                before = seen.Count;
                var (twReset, twResetErr) = Follow(ct => a.TweaksSetAsync("1.6", ParseBody("{}")!.Value, ct: ct));
                var twResetReqs = Since(before);
                Check("/server/tweaks with the empty document posts {\"version\":\"1.6\",\"tweaks\":{}}, counted, and follows the job"
                      + (twResetErr is null ? "" : $" ({twResetErr})"),
                    twResetReqs.Count > 0 && twResetReqs[0].body == "{\"version\":\"1.6\",\"tweaks\":{}}" && !twResetReqs[0].chunked
                    && twReset is { } twResetDone && twResetDone.GetProperty("state").GetString() == "done"
                    && twResetDone.GetProperty("result").GetProperty("effect").GetString() == "next-start");
                // ...and a value that is no document at all never leaves this PC: the agent reads {} as "everything
                // back to the original game", and null or a list must not get the chance to be read as that.
                before = seen.Count;
                bool RefusedHere(JsonElement doc)
                {
                    try { a.TweaksSetAsync("1.6", doc).GetAwaiter().GetResult(); return false; }
                    catch (ArgumentException) { return true; }
                    catch (Exception) { return false; }
                }
                Check("/server/tweaks: a document that is not a JSON object (null, a list, text, no value) is refused before any request",
                    RefusedHere(ParseBody("null")!.Value) && RefusedHere(ParseBody("[]")!.Value)
                    && RefusedHere(ParseBody("\"{}\"")!.Value) && RefusedHere(default) && Since(before).Count == 0);
                // The agent is the one validator: what it refuses reaches it as given — an unknown section, a
                // number typed as text — so the refusal is its 400 naming the path, never a key that vanished on
                // the way. One request, nothing followed, and not the update advice: only a 404 is that.
                const string twBadJson = """{"Zeta_New":[1,true,null,"x"],"resin":{"cap":"300"}}""";
                before = seen.Count;
                var (twBad, twBadErr) = Follow(ct => a.TweaksSetAsync("1.6", ParseBody(twBadJson)!.Value, ct: ct));
                var twBadReqs = Since(before);
                Check("/server/tweaks: an unknown key and a wrong-kind value go out as given, and the agent's 400 comes back with its own text"
                      + (twBadErr is null ? "" : $" ({twBadErr})"),
                    twBad is null && twBadReqs.Count == 1 && twBadReqs[0].body == "{\"version\":\"1.6\",\"tweaks\":" + twBadJson + "}"
                    && twBadErr is not null && twBadErr.Contains("400") && twBadErr.Contains("tweaks.resin.cap")
                    && twBadErr != L.T("core.agent.tweaksUnsupported"));
                // The game server refusing the new files ends the job in error (the agent has put the previous
                // files back by then): the agent's own sentence is what the admin reads.
                var (twRefused, twRefusedErr) = Follow(ct => a.TweaksSetAsync("2.8", twDoc, ct: ct));
                Check("/server/tweaks: a job that ends in error throws the agent's own message",
                    twRefused is null
                    && twRefusedErr == "The server refused the new settings: RewardData.txt was not loaded. The previous settings are back in place.");
                // A 404 while FOLLOWING the job is the forgotten job, with its own text — not the update advice.
                before = seen.Count;
                var (twGone, twGoneErr) = Follow(ct => a.TweaksSetAsync("0.0", twDoc, ct: ct));
                var twGoneReqs = Since(before);
                Check("/server/tweaks: a 404 while following the job keeps its own text (not the update advice)"
                      + (twGoneErr is null ? "" : $" ({twGoneErr})"),
                    twGone is null && twGoneErr is not null && twGoneErr != L.T("core.agent.tweaksUnsupported")
                    && twGoneErr.Contains("404")
                    && twGoneReqs.Select(r => r.path).SequenceEqual(new[] { "/server/tweaks", "/jobs/tweaks-gone?since=0" }));
                // An agent without the routes answers both with its plain 404 (the routes themselves never do:
                // an unknown version is a 400, a refused document a 400). On the POST the admin reads "the agent
                // must be updated", not "error 404: not found"; on the GET it is carried as
                // AgentHttpException{404}, which the launcher turns into {tooOld: true} for the page's note.
                _tweaksMissing = true;
                int? twOldStatus = null;
                (JsonElement? snap, string? error) twOld;
                List<(string method, string path, string auth, string body, bool chunked)> twOldReqs;
                try
                {
                    try { a.TweaksGetAsync("1.6").GetAwaiter().GetResult(); }
                    catch (AgentHttpException ex) { twOldStatus = ex.Status; }
                    catch (Exception) { twOldStatus = -1; }
                    before = seen.Count;
                    twOld = Follow(ct => a.TweaksSetAsync("1.6", twDoc, ct: ct));
                    twOldReqs = Since(before);
                }
                finally { _tweaksMissing = false; }
                Check("/server/tweaks GET on an agent without the route throws AgentHttpException with Status 404", twOldStatus == 404);
                Check("/server/tweaks: a 404 on the POST (an agent without the route) reads as core.agent.tweaksUnsupported, nothing is followed",
                    twOld.snap is null && twOld.error == L.T("core.agent.tweaksUnsupported") && L.Has("core.agent.tweaksUnsupported")
                    && twOldReqs.Select(r => r.path).SequenceEqual(new[] { "/server/tweaks" }));
                // A refused bearer while FOLLOWING the job — the admin closed open administration, a token rotated
                // on the box — is never transient: the 401 reaches the caller at once as the 401 (one poll, no retry),
                // so the launcher reads a revoked open key from it; retried, it would come back ten seconds later as
                // the "job link lost" text, which says nothing of a revocation. The stub's bearer gate refuses the
                // polls after a POST it accepted, as the real agent does the moment the key is gone.
                Interlocked.Exchange(ref _refusePolls, 100);
                before = seen.Count;
                Exception? revoked = null;
                var revokedSw = System.Diagnostics.Stopwatch.StartNew();
                using (var revokedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    try { a.TweaksSetAsync("1.6", twDoc, ct: revokedDeadline.Token).GetAwaiter().GetResult(); }
                    catch (Exception ex) { revoked = ex; }
                }
                revokedSw.Stop();
                Interlocked.Exchange(ref _refusePolls, 0);
                var revokedReqs = Since(before);
                Check("job follow: a 401 on a poll surfaces at once as the 401 — one poll, no retry, never core.agent.jobLinkLost"
                      + (revoked is AgentHttpException { Status: 401 } ? "" : revoked is null ? " (the follow ended without an error)" : $" ({revoked.Message})"),
                    revoked is AgentHttpException { Status: 401 }
                    && revokedReqs.Select(r => r.path).SequenceEqual(new[] { "/server/tweaks", "/jobs/tweaks-16?since=0" })
                    && revokedSw.Elapsed < TimeSpan.FromSeconds(5));
            });

            WithClient(good with { AgentToken = "wrong-token" }, a =>
            {
                string? err = null;
                try { a.StatusAsync().GetAwaiter().GetResult(); }
                catch (InvalidOperationException ex) { err = ex.Message; }
                Check("wrong token -> error surfaced (no fake OK)", err is not null);
                Check("error names HTTP 401 + agent's message",
                    err is not null && err.Contains("401") && err.Contains("unauthorized"));
            });

            var ssh = good with { Mode = "ssh" };
            Check("mode flag: ssh config is not direct", !ssh.IsDirect && good.IsDirect);

            // ── the open key: the token-less route a Player-mode launcher asks for the key the admin hands out ──
            using (var pub = new AgentPublicClient("127.0.0.1", port, TimeSpan.FromSeconds(5)))
            {
                _openAdminStub = 0;
                string? key = null; string? keyErr = null;
                try { key = pub.OpenAdminKeyAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { keyErr = ex.Message; }
                Check("/public/admin/key: a 200 hands back the key, asked without any bearer" + (keyErr is null ? "" : $" ({keyErr})"),
                    key == OpenKey && seen[^1].method == "GET" && seen[^1].path == "/public/admin/key" && seen[^1].auth == "");
                _openAdminStub = 1;
                AgentPublicException? off = null;
                try { pub.OpenAdminKeyAsync().GetAwaiter().GetResult(); }
                catch (AgentPublicException ex) { off = ex; }
                Check("/public/admin/key: 403 open_admin_disabled (the option is off) keeps its code and reads as this build's text for it",
                    off is { Status: 403, Code: "open_admin_disabled" } && off.Message == L.T("core.agent.err.open_admin_disabled")
                    && L.Has("core.agent.err.open_admin_disabled"));
                _openAdminStub = 2;
                AgentPublicException? gone = null;
                try { pub.OpenAdminKeyAsync().GetAwaiter().GetResult(); }
                catch (AgentPublicException ex) { gone = ex; }
                Check("/public/admin/key: a 404 (an agent without the route) reads as core.agent.openAdminUnsupported, never as a missing job",
                    gone is { Status: 404 } && gone.Message == L.T("core.agent.openAdminUnsupported") && L.Has("core.agent.openAdminUnsupported")
                    && gone.Message != L.T("core.agent.err.not_found"));
                _openAdminStub = 3;
                AgentPublicException? blank = null;
                try { pub.OpenAdminKeyAsync().GetAwaiter().GetResult(); }
                catch (AgentPublicException ex) { blank = ex; }
                Check("/public/admin/key: a 200 without a key is the unexpected-response error, never an empty bearer to enter with",
                    blank is { Status: 502 } && blank.Message == L.T("core.agent.badResponse", new { detail = OpenKeyBlankJson }));
                _openAdminStub = 0;
            }

            ProbeChecks(good, Check);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { serverTask.Wait(1000); } catch { /* cancelled */ }
        }

        LiveCheck(Check);

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"agent spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// ServerProbe — the pre-launch "is the server up?" question. The regression it exists for: a
    /// probe that asks ONCE with a 5 s cap, at the coldest moment there is (first process after a
    /// boot, first run after an install, nothing JIT-ed, empty DNS cache, network often still coming
    /// up), reports "the server status could not be verified" for a server that is perfectly
    /// up — while the very next launch, with everything warm, goes through silently.
    /// The stalled-first-call check below is the guard: one slow call must NOT become "unknown".
    /// </summary>
    static void ProbeChecks(ServerConfig good, Action<string, bool> check)
    {
        var fast = TimeSpan.FromMilliseconds(400);
        var pause = TimeSpan.FromMilliseconds(50);

        var up = ServerProbe.CheckAsync(good, "1.6", null, fast, 3, pause).GetAwaiter().GetResult();
        check("probe: a running version reads as up", up is { Up: true, Degraded: "", AllGood: true });
        var down = ServerProbe.CheckAsync(good, "2.8", null, fast, 3, pause).GetAwaiter().GetResult();
        check("probe: a stopped version reads as down (not unknown)", down is { Up: false, AllGood: false });

        // THE regression check: the first /status never answers, the second does.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Interlocked.Exchange(ref _stallStatus, 1);
        var retried = ServerProbe.CheckAsync(good, "1.6", null, fast, 3, pause).GetAwaiter().GetResult();
        Interlocked.Exchange(ref _stallStatus, 0);
        check("probe: a cold first call that times out is RETRIED, not reported as unknown",
            retried is { Up: true, AllGood: true });
        check("probe: the retry costs about one attempt, not a whole new budget",
            sw.ElapsedMilliseconds < 2500);

        // Nothing listening at all: unknown after every attempt — and bounded by our own deadline,
        // never by the OS's ~21 s SYN retry.
        var dead = new ServerConfig("127.0.0.1", 22, "", "", Token, 1, "direct");
        sw.Restart();
        var none = ServerProbe.CheckAsync(dead, "1.6", null, fast, 2, pause).GetAwaiter().GetResult();
        check("probe: an unreachable agent is UNKNOWN, never a false 'stopped'",
            none is { Up: null, AllGood: false });
        check("probe: an unreachable agent gives up on OUR deadline", sw.ElapsedMilliseconds < 4000);

        // The status callback is what puts the wait on the splash — silence there would make the
        // shortcut look dead, which is why the check may take its time at all.
        var said = new List<string>();
        Interlocked.Exchange(ref _stallStatus, 1);
        ServerProbe.CheckAsync(good, "1.6", said.Add, fast, 3, pause).GetAwaiter().GetResult();
        Interlocked.Exchange(ref _stallStatus, 0);
        check("probe: reports progress (checking + retrying) to the splash", said.Count >= 2);

        // Parsing, on synthetic payloads: the three answers must stay distinguishable.
        using var err = System.Text.Json.JsonDocument.Parse("""{"error":"docker compose ls failed"}""");
        check("probe: an agent-level docker error is unknown, not 'stopped'",
            ServerProbe.Parse(err.RootElement, "1.6").Up is null);
        using var deg = System.Text.Json.JsonDocument.Parse(
            """{"versions":{"1.6":{"up":true,"servicesDown":["gameserver","multiserver"]}}}""");
        var degraded = ServerProbe.Parse(deg.RootElement, "1.6");
        check("probe: up with dead services is up-but-degraded",
            degraded is { Up: true, AllGood: false } && degraded.Degraded == "gameserver, multiserver");
        using var missing = System.Text.Json.JsonDocument.Parse("""{"versions":{"2.8":{"up":true}}}""");
        check("probe: a version the agent does not list reads as down",
            ServerProbe.Parse(missing.RootElement, "1.6").Up is false);
    }

    /// <summary>Connect, run, dispose — AgentClient is IAsyncDisposable, so no sync `using`.</summary>
    static void WithClient(ServerConfig cfg, Action<AgentClient> body)
    {
        var a = new AgentClient(cfg);
        try
        {
            a.ConnectAsync().GetAwaiter().GetResult();
            body(a);
        }
        finally
        {
            a.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Read-only probe of a REAL agent (opt-in via env), proving the whole direct path.</summary>
    static void LiveCheck(Action<string, bool> check)
    {
        string? host = Environment.GetEnvironmentVariable("RELIC_AGENT_HOST");
        string? token = Environment.GetEnvironmentVariable("RELIC_AGENT_TOKEN");
        if (host is null || token is null)
        {
            Console.WriteLine("    (info) live check skipped — set RELIC_AGENT_HOST + RELIC_AGENT_TOKEN to enable");
            return;
        }
        int port = int.TryParse(Environment.GetEnvironmentVariable("RELIC_AGENT_PORT"), out var p) ? p : 18080;
        Console.WriteLine($"    live agent: http://{host}:{port}/");

        var cfg = new ServerConfig(host, 22, "", "", token, port, "direct");
        WithClient(cfg, a =>
        {
            var st = a.StatusAsync().GetAwaiter().GetResult();
            check("LIVE /status answers over direct HTTP", st.ValueKind == System.Text.Json.JsonValueKind.Object);
            Console.WriteLine($"    live status: {st.GetRawText()}");
        });
        WithClient(cfg with { AgentToken = "definitely-wrong" }, a =>
        {
            bool denied = false;
            try { a.StatusAsync().GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) { denied = ex.Message.Contains("401"); }
            check("LIVE wrong token is rejected (401)", denied);
        });
    }

    /// <summary>How many more /status requests the stub swallows without answering, to imitate the
    /// cold first call of a freshly booted machine (see the ServerProbe checks).</summary>
    static int _stallStatus;

    /// <summary>How many more /jobs/ polls the bearer gate refuses with its 401, whatever bearer they carry:
    /// the open key revoked (or the token rotated) while a job is being followed (see the job follow check).</summary>
    static int _refusePolls;

    /// <summary>Minimal HTTP/1.1 stub speaking the agent's protocol. TcpListener (not HttpListener)
    /// so no URL-ACL/admin is ever needed.</summary>
    static void Serve(TcpListener listener, List<(string, string, string, string, bool)> seen, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (SocketException) { break; } // listener stopped
            // One thread per connection, like the real agent's ThreadingHTTPServer. It also has to be
            // this way for the ServerProbe checks: a stalled request that held the accept loop would
            // leave the RETRY with nobody to answer it, so the retry could never be seen working.
            new Thread(() => Handle(client, seen, ct)) { IsBackground = true }.Start();
        }
    }

    static void Handle(TcpClient client, List<(string, string, string, string, bool)> seen, CancellationToken ct)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var (method, path, auth, body, chunked) = ReadRequest(stream);
                lock (seen) seen.Add((method, path, auth, body, chunked));

                // A stalled /status is answered by nothing at all — the caller's own deadline is the
                // only thing that ends it, exactly like a machine whose network is not up yet.
                if (path == "/status" && Interlocked.Decrement(ref _stallStatus) >= 0)
                {
                    try { Task.Delay(3000, ct).Wait(ct); } catch (Exception) { /* run ending */ }
                    return;
                }

                // The token-less routes come BEFORE the bearer gate, as on the real agent. A job poll while the
                // bearer stands revoked is refused by that gate like any wrong bearer (_refusePolls).
                bool authed = auth == $"Bearer {Token}"
                    && !(path.StartsWith("/jobs/", StringComparison.Ordinal) && Interlocked.Decrement(ref _refusePolls) >= 0);
                var pub = PublicReply(method, path);
                var job = pub is null ? JobReply(method, path, body) : null;
                string json =
                    pub is not null ? pub.Value.json :
                    !authed ? """{"error":"unauthorized"}""" :
                    job is not null ? job.Value.json :
                    path == "/status" ? """{"versions":{"1.6":{"project":"16_live","up":true},"2.8":{"project":"28_live","up":false}},"running":["16_live"]}""" :
                    path == "/command" ? """{"ok":true,"response":"stub"}""" :
                    path == "/agent/state/reset" ? """{"ok":true,"corruptCopy":"state.json.corrupt-20260915T120000Z"}""" :
                    """{"error":"not found"}""";
                int code = pub?.code ?? (!authed ? 401 : job?.code ?? (path is "/status" or "/command" or "/agent/state/reset" ? 200 : 404));

                byte[] payload = Encoding.UTF8.GetBytes(json);
                string head = $"HTTP/1.1 {code} X\r\nContent-Type: application/json\r\n" +
                              $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                stream.Write(Encoding.ASCII.GetBytes(head));
                stream.Write(payload);
            }
        }
        catch (Exception)
        {
            // A caller that gave up mid-request (the timeout checks do exactly that) closes the
            // socket under us; that is the scenario, not a stub failure.
        }
    }

    /// <summary>The stub's job endpoints (voice packs; setup/provision/pathfinding/
    /// fetch; admin account creation), answered the way the real agent answers every job: 202 + a job id, then GET
    /// /jobs/&lt;id&gt;?since=N until the state leaves "running". Stateless on purpose — "since" alone
    /// picks the snapshot, so the final one is only ever served to a client that carried the first one's
    /// "next" forward. The id names the scenario: an empty selection is done at once, version "0.0" ends
    /// in an error. The voice and fetch paths with "cancel":true are the real agent's NON-job answers
    /// there: a plain 200 {ok, stopping}.</summary>
    static (int code, string json)? JobReply(string method, string path, string body)
    {
        if (method == "POST" && path == "/server/hotpatch/voice" && body.Contains("\"cancel\":true"))
            return (200, """{"ok":true,"stopping":true}""");
        if (method == "POST" && path == "/server/hotpatch/voice")
        {
            string id = body.Contains("\"0.0\"") ? "voice-error" : body.Contains("\"languages\":[]") ? "voice-none" : "voice-packs";
            return (202, $$"""{"ok":true,"async":true,"job":"{{id}}","kind":"hotpatch-voice"}""");
        }
        if (method == "POST" && path == "/server/fetch" && body.Contains("\"cancel\":true"))
            return (200, """{"ok":true,"stopping":false}""");
        if (method == "POST" && path == "/server/fetch")
            return (202, """{"ok":true,"async":true,"job":"fetch-16","kind":"fetch","version":"1.6"}""");
        if (method == "POST" && path == "/server/pathfinding")
            return (202, """{"ok":true,"async":true,"job":"pf-off","kind":"pathfinding","version":"1.6"}""");
        if (method == "POST" && path == "/server/setup")
            return (202, """{"ok":true,"async":true,"job":"setup-ok","kind":"setup","version":"1.6"}""");
        if (method == "POST" && path == "/server/provision")
            return (202, """{"ok":true,"async":true,"job":"prov-ok","kind":"provision","version":"1.6"}""");
        // Admin account creation. Version "0.0" plays an agent that has no such route (its plain 404); the
        // name "gone1" gets a job id this stub never answers for — the agent that restarted and forgot the
        // job, a 404 while FOLLOWING, which must keep its own text.
        if (method == "POST" && path == "/server/account/create")
            return body.Contains("\"0.0\"") ? (404, """{"error":"not found"}""")
                : body.Contains("\"gone1\"") ? (202, """{"ok":true,"async":true,"job":"acct-gone","kind":"accountcreate","version":"1.6"}""")
                : (202, """{"ok":true,"async":true,"job":"acct-ok","kind":"accountcreate","version":"1.6"}""");
        // The agent's settings, its restart, and the stack relocation (check / job / cancel).
        if (path == "/agent/config" && (method == "GET" || method == "POST"))
            return AgentConfigReply(method, body);
        if (method == "POST" && path == "/agent/restart")
        {
            lock (_agentCfgLock) { _restartPolls = 0; _restartPending = true; }
            return (202, """{"ok":true,"restarting":true,"how":"systemd"}""");
        }
        if (method == "POST" && path == "/server/relocate" && body.Contains("\"cancel\":true"))
            return (200, """{"ok":true,"stopping":true}""");
        if (method == "POST" && path == "/server/relocate" && body.Contains("\"check\":true"))
            return (200, """{"version":"2.8","mode":"move","from":"D:\\servers\\2.8_live","to":"C:\\relic_servers\\2.8_live","target":"absent","running":false,"sameVolume":false,"stackBytes":17000000000,"freeBytes":90000000000,"needBytes":22000000000,"ok":true,"problem":null}""");
        if (method == "POST" && path == "/server/relocate")
            return (202, """{"ok":true,"async":true,"job":"reloc-28","kind":"relocate","version":"2.8"}""");
        // Gameplay settings: the state (GET) and the job (POST).
        if ((method == "GET" || method == "POST") && (path == "/server/tweaks" || path.StartsWith("/server/tweaks?", StringComparison.Ordinal)))
            return TweaksReply(method, path, body);
        if (method != "GET") return null;
        return path switch
        {
            "/jobs/voice-packs?since=0" => (200, """{"id":"voice-packs","kind":"hotpatch-voice","version":"1.6","state":"running","error":null,"result":null,"lines":["voice packs: 186 files to fetch"],"next":1,"elapsed":0}"""),
            "/jobs/voice-packs?since=1" => (200, """{"id":"voice-packs","kind":"hotpatch-voice","version":"1.6","state":"done","error":null,"result":{"voice":["English(US)","Japanese"],"fetched":186,"bytes":7614579868,"purged":["Korean"]},"lines":["voice packs: done"],"next":2,"elapsed":1}"""),
            "/jobs/voice-none?since=0" => (200, """{"id":"voice-none","kind":"hotpatch-voice","version":"2.8","state":"done","error":null,"result":{"voice":[],"fetched":0,"bytes":0,"purged":[]},"lines":[],"next":0,"elapsed":0}"""),
            "/jobs/voice-error?since=0" => (200, """{"id":"voice-error","kind":"hotpatch-voice","version":"0.0","state":"error","error":"2 of 84 voice pack files could not be fetched","result":null,"lines":[],"next":0,"elapsed":0}"""),
            "/jobs/pf-off?since=0" => (200, """{"id":"pf-off","kind":"pathfinding","version":"1.6","state":"done","error":null,"result":{"enabled":false,"changed":true,"applied":true},"lines":["Pathfinding server excluded (profile donotstart) in docker-compose.yml.tmpl + docker-compose.yml."],"next":1,"elapsed":1}"""),
            "/jobs/setup-ok?since=0" => (200, """{"id":"setup-ok","kind":"setup","version":"1.6","state":"done","error":null,"result":{"provisioned":true,"progress":"default","defaultAccount":true,"txtFixes":"applied"},"lines":[],"next":0,"elapsed":0}"""),
            "/jobs/prov-ok?since=0" => (200, """{"id":"prov-ok","kind":"provision","version":"1.6","state":"done","error":null,"result":{"provisioned":true,"progress":"fixes","defaultAccount":false,"txtFixes":"applied"},"lines":[],"next":0,"elapsed":0}"""),
            "/jobs/fetch-16?since=0" => (200, """{"id":"fetch-16","kind":"fetch","version":"1.6","state":"running","error":null,"result":null,"lines":["Downloading the 1.6 server package (1.87 GB) from archive.org"],"next":1,"elapsed":0}"""),
            "/jobs/fetch-16?since=1" => (200, """{"id":"fetch-16","kind":"fetch","version":"1.6","state":"done","error":null,"result":{"fetched":true,"downloaded":true,"bytes":1870180624,"healed":0,"execBits":12,"dir":"/home/1.6_live"},"lines":["Installed into /home/1.6_live."],"next":2,"elapsed":1}"""),
            "/jobs/acct-ok?since=0" => (200, """{"id":"acct-ok","kind":"accountcreate","version":"1.6","state":"done","error":null,"result":{"name":"friend1","uid":10042,"template":"post-gaa","nickname":"friend1","passwordVerify":true,"passwordGenerated":true,"password":"Gen3rated-Pw"},"lines":["Admin account creation: name=friend1 version=1.6 template=post-gaa"],"next":1,"elapsed":1}"""),
            "/jobs/reloc-28?since=0" => (200, """{"id":"reloc-28","kind":"relocate","version":"2.8","state":"running","error":null,"result":null,"lines":["Copying the 2.8 server to C:\\relic_servers\\2.8_live (another drive)"],"next":1,"elapsed":0}"""),
            "/jobs/reloc-28?since=1" => (200, """{"id":"reloc-28","kind":"relocate","version":"2.8","state":"done","error":null,"result":{"version":"2.8","from":"D:\\servers\\2.8_live","to":"C:\\relic_servers\\2.8_live","mode":"move","moved":true,"crossVolume":true,"bytes":17000000000,"restarted":false},"lines":["GIO_DIR_28 saved; the old folder is deleted."],"next":2,"elapsed":1}"""),
            "/jobs/tweaks-16?since=0" => (200, """{"id":"tweaks-16","kind":"tweaks","version":"1.6","state":"running","error":null,"result":null,"lines":["Gameplay settings: 3 files to write (resin, abyss, domains, quests)"],"next":1,"elapsed":0}"""),
            "/jobs/tweaks-16?since=1" => (200, """{"id":"tweaks-16","kind":"tweaks","version":"1.6","state":"done","error":null,"result":{"version":"1.6","changed":3,"effect":"live","clamped":0},"lines":["The game server loaded the new settings."],"next":2,"elapsed":1}"""),
            "/jobs/tweaks-reset?since=0" => (200, """{"id":"tweaks-reset","kind":"tweaks","version":"1.6","state":"done","error":null,"result":{"version":"1.6","changed":3,"effect":"next-start","clamped":0},"lines":[],"next":0,"elapsed":0}"""),
            "/jobs/tweaks-refused?since=0" => (200, """{"id":"tweaks-refused","kind":"tweaks","version":"2.8","state":"error","error":"The server refused the new settings: RewardData.txt was not loaded. The previous settings are back in place.","result":null,"lines":[],"next":0,"elapsed":0}"""),
            _ => null,
        };
    }

    /// <summary>The key the stub hands out on GET /public/admin/key (48 hex characters, the shape of a token).</summary>
    const string OpenKey = "0123456789abcdef0123456789abcdef0123456789abcdef";
    /// <summary>The stub's answer without a key, for the one shape the launcher must refuse to enter with.</summary>
    const string OpenKeyBlankJson = """{"ok":true,"token":""}""";
    /// <summary>How the stub answers the token-less GET /public/admin/key: 0 = an agent whose admin lets everyone
    /// administer (200 + the key), 1 = the option off (403 open_admin_disabled), 2 = an agent without the route
    /// (its plain not-found body, whose code is a missing JOB's and must not be read as one), 3 = a 200 without
    /// a key.</summary>
    static volatile int _openAdminStub;

    /// <summary>The token-less routes, answered before the bearer gate — only the open key here; the public
    /// status and signup routes are not driven by this spike.</summary>
    static (int code, string json)? PublicReply(string method, string path)
    {
        if (method != "GET" || path != "/public/admin/key") return null;
        return _openAdminStub switch
        {
            0 => (200, $$"""{"ok":true,"token":"{{OpenKey}}"}"""),
            1 => (403, """{"error":"this server does not let everyone administer it","code":"open_admin_disabled"}"""),
            2 => (404, """{"error":"not found","code":"not_found"}"""),
            _ => (200, OpenKeyBlankJson),
        };
    }

    /// <summary>True = the stub plays an agent without the gameplay settings routes: GET and POST /server/tweaks
    /// fall through to its plain 404.</summary>
    static volatile bool _tweaksMissing;

    /// <summary>GET/POST /server/tweaks the way the agent answers. GET: the state of version "1.6", a 400 for
    /// any other version (the route never answers 404 itself). POST: a 400 naming the path for a document
    /// the agent refuses (here: a resin cap sent as text), else 202 + a job id that names the scenario — the
    /// empty document is done at once, version "2.8" ends in an error (the game server refused the files),
    /// version "0.0" gets a job id the stub never answers for (the agent that restarted and forgot the job).</summary>
    static (int code, string json)? TweaksReply(string method, string path, string body)
    {
        if (_tweaksMissing) return null;
        if (method == "GET")
            return path == "/server/tweaks?version=1.6"
                ? (200, """{"version":"1.6","available":true,"present":true,"running":true,"tweaks":{"resin":{"cap":300}},"active":{"resin":{"cap":300}},"pending":false,"refused":null,"unconfirmed":false,"sections":{"resin":{"ok":true},"abyss":{"ok":true,"files":[]},"domains":{"ok":true},"quests":{"ok":false,"why":"changed","files":["server/data/txt/RewardData.txt"]}},"limits":{"multiplierMax":10,"extraMax":8,"customMax":200,"resinCap":[1,2000],"minutes":[1,1440],"level":[1,199],"cost":[1,200]},"resin":{"cap":160,"minutes":8,"hardMax":2000}}""")
                : (400, """{"error":"unknown version"}""");
        if (body.Contains("\"cap\":\"300\""))
            return (400, """{"error":"tweaks.resin.cap: expected a whole number"}""");
        string id = body.Contains("\"0.0\"") ? "tweaks-gone" : body.Contains("\"2.8\"") ? "tweaks-refused"
            : body.Contains("\"tweaks\":{}") ? "tweaks-reset" : "tweaks-16";
        return (202, $$"""{"ok":true,"async":true,"job":"{{id}}","kind":"tweaks"}""");
    }

    // ── stub state: the process start epoch /agent/config reports, and a restart in progress ──
    static readonly object _agentCfgLock = new();
    static string _agentStarted = "1790000000.123";
    static bool _restartPending;
    static int _restartPolls;
    /// <summary>True = the stub plays an agent without the route: /agent/config is a plain 404.</summary>
    static volatile bool _agentCfgMissing;
    /// <summary>True = the stub plays an agent that never came back from a restart: every /agent/config is a 503.</summary>
    static volatile bool _agentCfgDown;

    /// <summary>GET/POST /agent/config the way the agent answers. After a POST /agent/restart the GETs play the
    /// restart: the first answers 503 (the old process closing — any failure only means "not yet"), the
    /// second still carries the OLD started (the old process answering one last time), the third a NEW one.</summary>
    static (int code, string json) AgentConfigReply(string method, string body)
    {
        if (_agentCfgMissing) return (404, """{"error":"not found"}""");
        if (_agentCfgDown) return (503, """{"error":"restarting"}""");
        string started;
        lock (_agentCfgLock)
        {
            if (method == "GET" && _restartPending)
            {
                _restartPolls++;
                if (_restartPolls == 1) return (503, """{"error":"restarting"}""");
                if (_restartPolls >= 3)
                {
                    _agentStarted = "1790000100.456";
                    _restartPending = false;
                }
            }
            started = _agentStarted;
        }
        string cfg = "\"agent\":\"1.0\",\"platform\":\"linux\",\"started\":" + started
            + ",\"file\":\"/etc/gio-agent/config\",\"writable\":true,\"restart\":\"systemd\",\"listen\":\"0.0.0.0:18080\""
            + ",\"restartPending\":[],\"tls\":{\"strict\":false,\"caFile\":\"\",\"pinnedFallback\":true,\"fallbackHosts\":[]}"
            + ",\"settings\":[{\"key\":\"GIO_BIND_IP\",\"group\":\"network\",\"type\":\"ip\",\"value\":\"192.0.2.10\",\"stored\":\"192.0.2.10\",\"default\":\"\",\"apply\":\"live\",\"env\":false}]"
            + ",\"versions\":{\"1.6\":{\"dir\":\"/home/1.6_live\",\"configured\":true,\"present\":true,\"up\":true,\"project\":\"16_live\"},\"2.8\":{\"dir\":\"\",\"configured\":false,\"present\":false,\"up\":false,\"project\":\"\"}}";
        return method == "POST"
            ? (200, "{" + cfg + ",\"applied\":[\"GIO_BIND_IP\"],\"restartRequired\":[]}")
            : (200, "{" + cfg + "}");
    }

    static (string method, string path, string auth, string body, bool chunked) ReadRequest(NetworkStream stream)
    {
        string ReadLine()
        {
            var sb = new StringBuilder();
            int c;
            while ((c = stream.ReadByte()) != -1)
            {
                if (c == '\n') break;
                if (c != '\r') sb.Append((char)c);
            }
            return sb.ToString();
        }
        byte[] ReadExactly(int len)
        {
            byte[] buf = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = stream.Read(buf, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            return buf;
        }

        string[] reqLine = ReadLine().Split(' ');
        string auth = "", line;
        int contentLength = 0;
        bool chunked = false;
        while ((line = ReadLine()).Length > 0)
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) auth = line[14..].Trim();
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) contentLength = int.Parse(line[15..].Trim());
            if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
        }

        var body = new MemoryStream();
        if (chunked)
        {
            // Kept only so a regression reads as a clean FAIL on the framing check below instead of
            // an empty body — the REAL agent (Python http.server) cannot do this, which is the whole
            // point of that check.
            while (true)
            {
                int size = Convert.ToInt32(ReadLine().Split(';')[0].Trim(), 16);
                if (size == 0) { ReadLine(); break; } // trailing CRLF after the 0-chunk
                body.Write(ReadExactly(size));
                ReadLine(); // CRLF after each chunk
            }
        }
        else if (contentLength > 0)
        {
            body.Write(ReadExactly(contentLength));
        }
        return (reqLine[0], reqLine.Length > 1 ? reqLine[1] : "", auth,
            Encoding.UTF8.GetString(body.ToArray()), chunked);
    }
}

/// <summary>Validates the UI-action -> gmTalk command-string mapping (pure; no network).</summary>
static class CommandsSpike
{
    public static int Run()
    {
        Console.WriteLine("== commands spike: UI action -> gmTalk string mapping ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        Check("grant EXP", GmCommands.GrantExp(10000) == "item add 102 10000");
        Check("give Mora", GmCommands.GiveMora(1_000_000) == "item add 202 1000000");
        Check("give Primogems", GmCommands.GivePrimogems(10_000) == "item add 201 10000");
        Check("give Genesis Crystals", GmCommands.GiveGenesisCrystals(5_000) == "item add 203 5000");
        Check("give costume (Barbara)", GmCommands.GiveCostume(340000) == "item add 340000 1");
        Check("add avatar", GmCommands.AddAvatar(10000002) == "avatar add 10000002");
        Check("add weapon (level+ascension clamped)", GmCommands.AddWeapon(13501, 90, 6) == "equip add 13501 90 6");
        Check("weapon level clamps to 90", GmCommands.AddWeapon(11101, 999, 9) == "equip add 11101 90 6");
        Check("set adventure rank (clamped to 60)", GmCommands.SetAdventureRank(999) == "player level 60");
        Check("give item passthrough", GmCommands.GiveItem(223, 50) == "item add 223 50");

        var set = GmCommands.GiveArtifactSet(new[] { 23300, 23310, 23320, 23330, 23340 }, 20);
        Check("artifact set = 5 pieces", set.Count == 5);
        // Reliquary wire levels are 1-based: display +20 = 21 on the wire. Sending the display
        // value raw gives +19 instead of +20.
        Check("artifact piece format (display +20 -> wire 21)", set[0] == "equip add 23300 21");
        Check("artifact 4-star cap (display +16 -> wire 17)",
            GmCommands.GiveArtifactPiece(23300, 16) == "equip add 23300 17");
        Check("artifact level clamps to wire 1..21",
            GmCommands.GiveArtifactPiece(23300, 99) == "equip add 23300 21"
            && GmCommands.GiveArtifactPiece(23300, -5) == "equip add 23300 1");
        Check("amount floored to >= 1", GmCommands.GiveMora(0) == "item add 202 1");

        var inv = GmCommands.GiveArtifactSetToInventory(new[] { 71544, 71524, 71554, 71514, 71534 });
        Check("inventory set = 5 pieces", inv.Count == 5);
        Check("inventory piece = item add, no level", inv[0] == "item add 71544 1");
        Check("break current avatar", GmCommands.BreakCurrentAvatar(6) == "break 6");
        Check("break clamps to 0..6", GmCommands.BreakCurrentAvatar(99) == "break 6");
        // C6 on the in-game character. "talent" = constellation in the server's vocabulary (procTalent /
        // forceUnlockAllTalent), not the talent of the game's UI — the string is the one from the GIO guide.
        Check("unlock all constellations (C6)", GmCommands.UnlockAllConstellations() == "talent unlock all");
        Check("hero's wit books", GmCommands.GiveHeroWit(500) == "item add 104003 500");
        Check("set avatar level", GmCommands.SetCurrentAvatarLevel(90) == "level 90");
        Check("avatar level clamps to 1..90", GmCommands.SetCurrentAvatarLevel(0) == "level 1");
        Check("stamina infinite on", GmCommands.StaminaInfinite(true) == "stamina infinite on");
        Check("stamina infinite off", GmCommands.StaminaInfinite(false) == "stamina infinite off");
        Check("wudi avatar on", GmCommands.WudiAvatar(true) == "wudi global avatar on");
        Check("wudi avatar off", GmCommands.WudiAvatar(false) == "wudi global avatar off");
        // Original Resin is one more "item add", with a ceiling of its own: the server refuses whole any add
        // that would put the player above its hard maximum, so an amount above it is never sent as typed.
        Check("give resin", GmCommands.GiveResin(160) == "item add 106 160" && GmCommands.GiveResin(0) == "item add 106 1");
        Check($"resin amount never goes out above the bound ({GmCommands.ResinMax})",
            GmCommands.ResinMax == 2000 && GmCommands.GiveResin(2000) == "item add 106 2000"
            && GmCommands.GiveResin(2001) == "item add 106 2000" && GmCommands.GiveResin(long.MaxValue) == "item add 106 2000");
        // The catalogue BEHIND the command screen. The screen shows "loading the content catalogue"
        // until this rpc answers, so an empty or unreadable config/gamedata.json leaves the pickers
        // blank with no other clue. Loaded through the real loader (which swallows every error and
        // answers empty) and filtered the way the screen filters it, for both versions.
        // The repo copy, found upwards: the spike runs from its own bin folder, where the app's
        // config/ does not exist. The app itself reads the copy the csproj puts next to the exe.
        string? gdPath = FindUp("config/gamedata.json");
        var gd = Relic.Core.Server.GameData.Load(gdPath);
        Check($"gamedata: the shipped catalogue loads from {gdPath ?? "(not found)"} ({gd.Avatars.Count} avatars, {gd.Weapons.Count} weapons, {gd.ArtifactSets.Count} sets)",
            gd.Avatars.Count > 0 && gd.Weapons.Count > 0 && gd.ArtifactSets.Count > 0);
        Check("gamedata: versionOrder names both shipped versions", gd.VersionOrder.Contains("1.6") && gd.VersionOrder.Contains("2.8"));
        foreach (string ver in new[] { "1.6", "2.8" })
        {
            var forV = gd.ForVersion(ver);
            var t = forV.GetType();
            int na = ((System.Collections.ICollection)t.GetProperty("avatars")!.GetValue(forV)!).Count;
            int nw = ((System.Collections.ICollection)t.GetProperty("weapons")!.GetValue(forV)!).Count;
            int ns = ((System.Collections.ICollection)t.GetProperty("artifactSets")!.GetValue(forV)!).Count;
            Check($"gamedata: version {ver} gets a non-empty catalogue ({na}/{nw}/{ns})", na > 0 && nw > 0 && ns > 0);
        }
        // 2.8 is the later version: it must see everything 1.6 sees and more.
        var g16 = gd.ForVersion("1.6"); var g28 = gd.ForVersion("2.8");
        int a16 = ((System.Collections.ICollection)g16.GetType().GetProperty("avatars")!.GetValue(g16)!).Count;
        int a28 = ((System.Collections.ICollection)g28.GetType().GetProperty("avatars")!.GetValue(g28)!).Count;
        Check($"gamedata: 2.8 is a superset of 1.6 ({a28} >= {a16})", a28 >= a16);

        // The display catalogue BEHIND the Gameplay page: names and sprite names for the ids the agent
        // answers with. The page draws "#id" and a placeholder tile for whatever the file lacks, so an empty
        // section, or a sprite name without its file, ships as a page of bare numbers with no other clue.
        // The repo copy again, found upwards; the app reads the copy the csproj puts next to the exe.
        string? tdPath = FindUp("config/tweakdata.json");
        var td = TweakData.Load(tdPath);
        Check($"tweakdata: the shipped catalogue loads from {tdPath ?? "(not found)"} (status {td.Status}{(td.Error is null ? "" : ": " + td.Error)})",
            td.Status == TweakData.StatusOk);
        string[] tdSections = ["monsters", "items", "domains", "quests"];
        JsonElement[] Entries(string section) =>
            td.Data is { } root && root.TryGetProperty(section, out var el) && el.ValueKind == JsonValueKind.Array
                ? el.EnumerateArray().ToArray() : [];
        Check($"tweakdata: monsters / items / domains / quests are non-empty arrays ({string.Join(" / ", tdSections.Select(s => Entries(s).Length))})",
            tdSections.All(s => Entries(s).Length > 0));
        // An entry without "icon" is drawn with the placeholder tile on purpose; one that names a sprite
        // the build does not ship would be a broken tile.
        string? repoDir = (tdPath ?? gdPath) is { } found ? Path.GetDirectoryName(Path.GetDirectoryName(found)) : null;
        string iconsDir = Path.Combine(repoDir ?? "", "app", "Relic.App", "ui", "assets", "icons");
        var sprites = tdSections.SelectMany(Entries)
            .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String)
            .Select(e => e.GetProperty("icon").GetString() ?? "").Distinct().ToList();
        var noFile = sprites.Where(s => s.Length == 0 || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !File.Exists(Path.Combine(iconsDir, s + ".webp"))).ToList();
        Check($"tweakdata: every entry's icon ({sprites.Count} sprites) has its file under app/Relic.App/ui/assets/icons"
              + (noFile.Count > 0 ? $" — {noFile.Count} missing, e.g. {string.Join(", ", noFile.Take(5))}" : ""),
            sprites.Count > 0 && noFile.Count == 0);
        // Characters as quest rewards: a character is an ITEM (MaterialData 1000 + n, the avatar 10000000 + n;
        // a reward row that holds it puts the character in the roster the moment it is granted), a constellation
        // item 1100 + n. The picker's chips key on the group, so every such row has to carry its own — and a
        // character's tile is the avatar's portrait, a sprite the build ships like any other. The Traveler's two
        // cards (1005 / 1007) are no choice: given to the owner of that twin the card converts to a Stella Fortuna
        // no constellation consumes, given to the owner of the other twin it adds a second protagonist — so the
        // catalogue files them under "other" (found by search only), portrait kept.
        var tdItems = Entries("items")
            .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
            .ToList();
        static string GroupOf(JsonElement e) => e.TryGetProperty("group", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : "";
        string IconOf(JsonElement e) => e.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() ?? "" : "";
        static bool TravelerCard(JsonElement e) => e.GetProperty("id").GetInt32() is 1005 or 1007;
        static string WantedGroup(JsonElement e) => TravelerCard(e) ? "other" : "character";
        var characters = tdItems.Where(e => e.GetProperty("id").GetInt32() is >= 1000 and <= 1099).ToList();
        var constellations = tdItems.Where(e => e.GetProperty("id").GetInt32() is >= 1100 and <= 1199).ToList();
        var charactersOff = characters
            .Where(e => GroupOf(e) != WantedGroup(e) || IconOf(e).Length == 0 || !File.Exists(Path.Combine(iconsDir, IconOf(e) + ".webp")))
            .Select(e => $"{e.GetProperty("id").GetInt32()} ({GroupOf(e)}{(IconOf(e).Length == 0 ? ", no icon" : "")})").ToList();
        Check($"tweakdata: every item 1000..1099 ({characters.Count}) is group \"character\" with a sprite the build ships — except the Traveler's cards 1005 / 1007, group \"other\" with their portrait"
              + (charactersOff.Count > 0 ? $" — {charactersOff.Count} off, e.g. {string.Join(", ", charactersOff.Take(5))}" : ""),
            characters.Count > 0 && characters.Count(TravelerCard) == 2 && charactersOff.Count == 0);
        var constellationsOff = constellations.Where(e => GroupOf(e) != "constellation")
            .Select(e => $"{e.GetProperty("id").GetInt32()} ({GroupOf(e)})").ToList();
        Check($"tweakdata: every item 1100..1199 ({constellations.Count}) is group \"constellation\""
              + (constellationsOff.Count > 0 ? $" — {constellationsOff.Count} off, e.g. {string.Join(", ", constellationsOff.Take(5))}" : ""),
            constellations.Count > 0 && constellationsOff.Count == 0);
        // The loader itself: a build without the file, or with one that cannot be used, answers with a status
        // for the page to show its note — never a throw — and the rpc reply carries exactly the keys that apply.
        string tdTmp = Path.Combine(Path.GetTempPath(), $"relic_spike_tweakdata_{Environment.ProcessId}.json");
        try
        {
            static string Reply(TweakData t) => JsonSerializer.Serialize(t.ToReply());
            File.Delete(tdTmp);
            var tdNone = TweakData.Load(tdTmp);
            Check("tweakdata: no file = status missing, answered as {\"status\":\"missing\"}",
                tdNone.Status == TweakData.StatusMissing && tdNone.Data is null && tdNone.Error is null
                && Reply(tdNone) == "{\"status\":\"missing\"}");
            File.WriteAllText(tdTmp, "{\"monsters\": [");
            var tdCut = TweakData.Load(tdTmp);
            File.WriteAllText(tdTmp, "[]");
            var tdList = TweakData.Load(tdTmp);
            Check("tweakdata: a file that is not JSON, or whose root is not an object, = status error with its reason (never a throw)",
                tdCut.Status == TweakData.StatusError && !string.IsNullOrEmpty(tdCut.Error) && tdCut.Data is null
                && tdList.Status == TweakData.StatusError && !string.IsNullOrEmpty(tdList.Error) && tdList.Data is null
                && Reply(tdList) == JsonSerializer.Serialize(new { status = "error", error = tdList.Error }));
            // A byte order mark in front (an editor's save) is not a reason to lose the catalogue.
            const string tdSmall = """{"versionOrder":["1.6","2.8"],"monsters":[{"id":21010201,"name":"Hilichurl Fighter","icon":"UI_MonsterIcon_Hili_None_01","since":"1.6"}]}""";
            File.WriteAllText(tdTmp, "﻿" + tdSmall);
            var tdOk = TweakData.Load(tdTmp);
            Check("tweakdata: a readable file = status ok with the whole document, answered as {\"status\":\"ok\",\"data\":<the file's JSON>}",
                tdOk.Status == TweakData.StatusOk && tdOk.Error is null
                && tdOk.Data is { } okDoc && okDoc.GetProperty("monsters")[0].GetProperty("id").GetInt32() == 21010201
                && Reply(tdOk) == "{\"status\":\"ok\",\"data\":" + tdSmall + "}");
        }
        finally
        {
            try { File.Delete(tdTmp); } catch { /* best effort */ }
        }
        // A copy in a per-user folder outranks the shipped gamedata.json; this file has no such folder: it is
        // generated with the sprites of the same build, and a copy left behind would hide what the build ships.
        string tdSource = repoDir is null ? "" : Path.Combine(repoDir, "app", "Relic.Core", "Server", "TweakData.cs");
        Check("tweakdata: only the copy shipped next to the exe is read (no per-user folder outranks it)",
            TweakData.ShippedPath == Path.Combine(AppContext.BaseDirectory, "config", "tweakdata.json")
            && File.Exists(tdSource) && !File.ReadAllText(tdSource).Contains("SpecialFolder", StringComparison.Ordinal));

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"commands spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>The repo file, looked up from the working directory and the spike output folder.</summary>
    static string? FindUp(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }
}

/// <summary>
/// Proves the parallel extraction writes byte-identical output, reports a monotonic 0..1 progress
/// over BOTH archives, and refuses a zip-slip entry BEFORE writing anything — against synthetic
/// archives in a temp dir, so no multi-GB download is involved.
/// </summary>
static class ExtractSpike
{
    public static int Run()
    {
        Console.WriteLine("== extract spike: parallel extraction, real progress, zip-slip guard ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        string tmp = Path.Combine(Path.GetTempPath(), "relic-extract-spike");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);

        try
        {
            // Two archives that both write into GenshinImpact_Data, like the real client + audio pair.
            var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            string zipA = Path.Combine(tmp, "client.zip");
            string zipB = Path.Combine(tmp, "audio.zip");
            // Highly compressible payloads: the zips stay small (quick to build) while the EXTRACTED
            // volume is large enough that the 250 ms progress poll fires many times — which is the
            // only way to prove the bar really ramps instead of jumping 0 -> 0.6 -> 1.
            BuildZip(zipA, expected, "GenshinImpact_Data/a", 60, 1_500_000);
            BuildZip(zipB, expected, "GenshinImpact_Data/StreamingAssets/b", 25, 1_500_000);

            string outDir = Path.Combine(tmp, "game");
            var fractions = new List<double>();
            var messages = new List<string>();
            var progress = new Progress<Relic.Core.Install.InstallProgress>(p =>
            {
                lock (fractions) { fractions.Add(p.Fraction); messages.Add(p.Message ?? ""); }
            });

            Relic.Core.Install.InstallService.ExtractAllAsync(
                new[] { (zipA, "Extracting the game"), (zipB, "Extracting the voices") }, outDir, progress, default)
                .GetAwaiter().GetResult();

            int wrong = 0, missing = 0;
            foreach (var (rel, want) in expected)
            {
                string path = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { missing++; continue; }
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(want)) wrong++;
            }
            Check($"all {expected.Count} entries extracted", missing == 0);
            Check("every extracted file is byte-identical", wrong == 0);

            // Progress must be a real, monotonic ramp — not a fixed ladder of 0, 0.6 and 1.
            double[] f;
            string[] msgs;
            lock (fractions) { f = fractions.ToArray(); msgs = messages.ToArray(); }
            bool monotonic = true;
            for (int i = 1; i < f.Length; i++) if (f[i] < f[i - 1] - 1e-9) monotonic = false;
            Check($"progress is monotonic ({f.Length} reports)", monotonic);
            Check("progress stays within 0..1", f.All(x => x >= -1e-9 && x <= 1 + 1e-9));
            Check("progress ends at 1", f.Length > 0 && Math.Abs(f[^1] - 1) < 1e-9);
            // How MANY intermediate reports arrive depends on how long the disk takes, so asserting a
            // count would be a clock-dependent flake. What is deterministic — and what a fixed
            // 0/0.6/1 ladder could never do — is that a mid-flight report states real transferred
            // bytes out of the real total.
            Check("mid-flight report carries actual byte counts ('X / Y')",
                msgs.Any(m => System.Text.RegularExpressions.Regex.IsMatch(m, @"\d[\d.,]*\s*(B|KB|MB|GB)\s*/\s*\d")));
            Check("no report uses the old fixed 0.6 checkpoint", !f.Any(x => Math.Abs(x - 0.6) < 1e-9));

            // Three archives — the client + two voice packs, the shape a multi-language install
            // produces. The shared counter must keep ONE ramp across EVERY boundary, not only the
            // client→audio one the run above covers. Byte shares: client 45.0 MB of 81.0 (55.5%), each
            // pack 18.0 MB — so a report labelled with a later archive that sat below the earlier
            // archives' share would be a per-archive restart. Deterministic, unlike report counts.
            var expected3 = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            string zipC = Path.Combine(tmp, "client3.zip");
            string zipJa = Path.Combine(tmp, "audio_ja.zip");
            string zipKo = Path.Combine(tmp, "audio_ko.zip");
            BuildZip(zipC, expected3, "GenshinImpact_Data/a", 30, 1_500_000);
            BuildZip(zipJa, expected3, "GenshinImpact_Data/StreamingAssets/Audio/GeneratedSoundBanks/Windows/Japanese", 12, 1_500_000);
            BuildZip(zipKo, expected3, "GenshinImpact_Data/StreamingAssets/Audio/GeneratedSoundBanks/Windows/Korean", 12, 1_500_000);
            string out3 = Path.Combine(tmp, "game-three");
            var reports3 = new List<(double Fraction, string Label)>();
            Relic.Core.Install.InstallService.ExtractAllAsync(
                new[] { (zipC, "game"), (zipJa, "Japanese voices"), (zipKo, "Korean voices") }, out3,
                new Progress<Relic.Core.Install.InstallProgress>(p => { lock (reports3) reports3.Add((p.Fraction, p.Message ?? "")); }), default)
                .GetAwaiter().GetResult();
            (double Fraction, string Label)[] r3;
            lock (reports3) r3 = reports3.ToArray();
            bool mono3 = true;
            for (int i = 1; i < r3.Length; i++) if (r3[i].Fraction < r3[i - 1].Fraction - 1e-9) mono3 = false;
            Check($"three archives: progress is one monotonic ramp ({r3.Length} reports)", mono3);
            Check("three archives: ends at 1", r3.Length > 0 && Math.Abs(r3[^1].Fraction - 1) < 1e-9);
            var ja = r3.Where(r => r.Label.Contains("Japanese voices")).Select(r => r.Fraction).ToList();
            var ko = r3.Where(r => r.Label.Contains("Korean voices")).Select(r => r.Fraction).ToList();
            Check("three archives: every archive gets at least one labelled report",
                r3.Any(r => r.Label.Contains("game")) && ja.Count > 0 && ko.Count > 0);
            Check("three archives: the 2nd archive never reports below the 1st's share (no restart at the boundary)",
                ja.All(x => x >= 0.55));
            Check("three archives: the 3rd archive never reports below the 1st + 2nd share",
                ko.All(x => x >= 0.77));
            Check("three archives: every file of all three landed byte-identical",
                expected3.All(kv =>
                {
                    string p = Path.Combine(out3, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                    return File.Exists(p) && File.ReadAllBytes(p).AsSpan().SequenceEqual(kv.Value);
                }));

            // zip-slip: an entry escaping the destination must throw BEFORE any file is written.
            string evil = Path.Combine(tmp, "evil.zip");
            using (var fs = new FileStream(evil, FileMode.Create))
            using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                var e = z.CreateEntry("../escaped.txt");
                using var w = new StreamWriter(e.Open());
                w.Write("pwned");
            }
            string evilOut = Path.Combine(tmp, "game2");
            bool threw = false;
            try
            {
                Relic.Core.Install.InstallService.ExtractAllAsync(
                    new[] { (evil, "evil") }, evilOut, new Progress<Relic.Core.Install.InstallProgress>(_ => { }), default)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidDataException) { threw = true; }
            Check("zip-slip entry rejected", threw);
            Check("zip-slip wrote nothing outside the target", !File.Exists(Path.Combine(tmp, "escaped.txt")));

            // Corrupt payload, correct length. This is the shape nothing else catches: the download
            // guards only check the total size + the leading "PK", DeflateStream cannot reject a
            // damaged STORED entry, and .NET's zip reader verifies no CRC — so without our own check
            // the install completes "successfully" over a broken game and deletes the cache.
            string bad = Path.Combine(tmp, "corrupt.zip");
            using (var fs = new FileStream(bad, FileMode.Create))
            using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                var e = z.CreateEntry("GenshinImpact_Data/payload.blk", System.IO.Compression.CompressionLevel.NoCompression);
                using var s = e.Open();
                var block = new byte[1 << 20];
                Array.Fill(block, (byte)0x41);
                for (int i = 0; i < 2; i++) s.Write(block, 0, block.Length);
            }
            // Flip one byte deep inside the stored run — length stays right, content does not.
            var raw = File.ReadAllBytes(bad);
            int at = -1;
            for (int i = 4096; i < raw.Length - 64; i++)
                if (raw[i] == 0x41 && raw[i + 32] == 0x41) { at = i + 16; break; }
            Check("corrupt-test archive prepared", at > 0);
            raw[at] ^= 0xFF;
            File.WriteAllBytes(bad, raw);

            string badOut = Path.Combine(tmp, "game3");
            bool badThrew = false;
            try
            {
                Relic.Core.Install.InstallService.ExtractAllAsync(
                    new[] { (bad, "corupt") }, badOut, new Progress<Relic.Core.Install.InstallProgress>(_ => { }), default)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidDataException) { badThrew = true; }
            // InvalidDataException specifically: Backend.TryDeleteCorruptCacheZips keys on that type.
            Check("corrupt payload raises InvalidDataException (CRC check)", badThrew);

            // ── worker-count resolution + disk probe ──────────────────────────────────────────
            // The count becomes `new Task[workers]`, so a zero or negative here is not a slow
            // extract — it is Task.WhenAll over an EMPTY array, which completes successfully having
            // written nothing, after which the install registers the version and deletes the cached
            // zips. Silent, total data loss. Every path must land >= 1.
            int auto = Relic.Core.Install.InstallService.ResolveWorkers(0);
            int autoOnDisk = Relic.Core.Install.InstallService.ResolveWorkers(0, tmp);
            Check("auto (0) resolves to at least 1 worker", auto >= 1);
            Check("auto over a real path resolves to at least 1 worker", autoOnDisk >= 1);
            Check("a negative worker count falls back to auto, never to 0",
                Relic.Core.Install.InstallService.ResolveWorkers(-4) == auto);
            Check("an absurd worker count is clamped",
                Relic.Core.Install.InstallService.ResolveWorkers(99_999) == 32);
            // A typed value must survive the disk probe: auto-detection tunes the AUTO case only,
            // and silently overriding what someone entered by hand is the one behaviour the setting
            // exists to prevent.
            Check("a manual worker count wins over disk auto-detection",
                Relic.Core.Install.InstallService.ResolveWorkers(5, tmp) == 5);

            // The probe answers bool? on purpose. Unknown must stay unknown: a null read as "not an
            // HDD" is harmless, but a null read as "HDD" would mis-tune every exotic controller.
            // The invariant worth locking is that it NEVER throws — it runs inside the install path,
            // where an exception over a hardware-detection nicety would abort a 30 GB install.
            bool? here = null;
            bool probeThrew = false;
            try { here = Relic.Core.Util.StorageInfo.HasSeekPenalty(tmp); }
            catch { probeThrew = true; }
            Check("disk probe over a real path never throws", !probeThrew);

            Check("a UNC path has no volume device and reports unknown",
                Relic.Core.Util.StorageInfo.HasSeekPenalty(@"\\nonexistent-host\share\x") is null);
            Check("an empty path reports unknown", Relic.Core.Util.StorageInfo.HasSeekPenalty("") is null);

            // Deliberately NOT asserted as null: since .NET Core, Path.GetFullPath no longer rejects
            // invalid characters, so a relative string resolves against the current directory and
            // lands on a perfectly real volume — the probe then truthfully answers about THAT disk.
            // Harmless, since every caller passes an absolute path that was just created; the only
            // thing that must hold is that it comes back at all.
            bool garbageThrew = false;
            try { Relic.Core.Util.StorageInfo.HasSeekPenalty("|||not a path|||"); }
            catch { garbageThrew = true; }
            Check("a garbage path returns cleanly rather than throwing", !garbageThrew);
            Console.WriteLine($"        (disk for {tmp}: {(here is null ? "unknown" : here.Value ? "mechanical" : "SSD/flash")}, "
                + $"auto = {autoOnDisk} threads)");
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"extract spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>An archive of deterministic pseudo-random files, recorded into <paramref name="expected"/>.</summary>
    private static void BuildZip(string path, Dictionary<string, byte[]> expected, string prefix, int count, int size)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
        for (int i = 0; i < count; i++)
        {
            string rel = $"{prefix}/file_{i:D3}.bin";
            // Deterministic and compressible: a short pseudo-random block tiled to full size, so the
            // content still catches a truncated/garbled write but the archive itself stays tiny.
            var data = new byte[size + i];
            var block = new byte[512];
            new Random(i + prefix.Length).NextBytes(block);
            for (int o = 0; o < data.Length; o += block.Length)
                Array.Copy(block, 0, data, o, Math.Min(block.Length, data.Length - o));
            expected[rel] = data;
            var entry = z.CreateEntry(rel, System.IO.Compression.CompressionLevel.Fastest);
            using var s = entry.Open();
            s.Write(data, 0, data.Length);
        }
    }
}

/// <summary>
/// Proves the state ↔ disk reconciliation that keeps a reinstall honest. The hazard is a
/// %LOCALAPPDATA%\Relic\state.json surviving an uninstall, so a fresh install shows a 30 GB version
/// as "already downloaded" with no files behind it. Uninstaller.Run removes that file, and
/// RelicState.PruneMissing is the belt to that braces — every other way the two can drift apart
/// (folder deleted by hand, a cleanup tool, a restored backup) has to end the same way.
///
/// Runs entirely against temp folders and an in-memory RelicState — no real install is touched.
/// </summary>
static class StateSpike
{
    public static int Run()
    {
        Console.WriteLine("== state spike: registrations reconciled with what is actually on disk ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        string tmp = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relic", "_spiketest_state");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);

        try
        {
            string realDir = Path.Combine(tmp, "Genshin 2.8");
            Directory.CreateDirectory(realDir);
            File.WriteAllText(Path.Combine(realDir, "GenshinImpact.exe"), "not really an exe");
            string ghostDir = Path.Combine(tmp, "Genshin 1.6");           // never created
            string emptyDir = Path.Combine(tmp, "Genshin 3.0");
            Directory.CreateDirectory(emptyDir);                          // folder there, client gone

            var st = new RelicState
            {
                Installed =
                {
                    new InstalledVersion { Id = "2.8", GameDir = realDir,  ProfileId = "priv28" },
                    new InstalledVersion { Id = "1.6", GameDir = ghostDir, ProfileId = "priv16" },
                    new InstalledVersion { Id = "3.0", GameDir = emptyDir, ProfileId = "priv30" },
                },
                SelectedVersionId = "1.6",
            };

            var gone = st.PruneMissing();

            Check("ghost registration (folder absent) dropped", gone.Contains("1.6"));
            Check("empty folder without GenshinImpact.exe dropped", gone.Contains("3.0"));
            Check("real install kept", st.FindInstalled("2.8") is not null && !gone.Contains("2.8"));
            Check("only the two ghosts went", gone.Count == 2 && st.Installed.Count == 1);
            Check("selection moved off the dropped version", st.SelectedVersionId == "2.8");

            // Idempotent: a second pass on a reconciled state must change nothing.
            Check("second pass is a no-op", st.PruneMissing().Count == 0 && st.Installed.Count == 1);

            // A version on a drive that is not mounted must NOT be unregistered — an unplugged
            // external disk is not the same thing as a deleted install.
            char free = "ZYXW".FirstOrDefault(c => !Directory.Exists($"{c}:\\"));
            if (free != '\0')
            {
                var offline = new RelicState
                {
                    Installed = { new InstalledVersion { Id = "2.8", GameDir = $@"{free}:\Relic Games\Genshin 2.8" } },
                };
                Check($"install on an unmounted drive ({free}:) is kept",
                    offline.PruneMissing().Count == 0 && offline.Installed.Count == 1);
            }
            else
            {
                Console.WriteLine("  [SKIP] unmounted-drive check (no free drive letter)");
            }

            // A registration with a garbage path is a ghost, not a crash.
            var bad = new RelicState { Installed = { new InstalledVersion { Id = "9.9", GameDir = "  " } } };
            Check("blank GameDir dropped without throwing", bad.PruneMissing().Count == 1);

            // Round-trip through the real save/load path: what Prune fixed must stay fixed.
            string statePath = Path.Combine(tmp, "state.json");
            st.Save(statePath);
            var reloaded = RelicState.Load(statePath);
            Check("pruned state survives save/load",
                reloaded.Installed.Count == 1 && reloaded.FindInstalled("2.8") is not null);

            // An older state.json has no "enhancements" key: the in-game enhancements default ON,
            // and an explicit false survives the round trip.
            string oldPath = Path.Combine(tmp, "old-state.json");
            File.WriteAllText(oldPath, """{ "Settings": { "ServerHost": "game.example.com" }, "Installed": [] }""");
            var old = RelicState.Load(oldPath);
            Check("state without 'enhancements' loads with Enhancements == true",
                old.Settings.Enhancements && old.Settings.ServerHost == "game.example.com");
            var off = new RelicState();
            off.Settings.Enhancements = false;
            string offPath = Path.Combine(tmp, "off-state.json");
            off.Save(offPath);
            Check("Enhancements = false survives save/load", !RelicState.Load(offPath).Settings.Enhancements);

            // ── installed voice languages: a cached view of the disk, refreshed at boot ──
            // "Installed" = the Audio_<Lang>_pkg_version marker the language zip lays at the game root.
            // A sound-bank folder WITHOUT it (a torn extract, or packs the client fetched itself at
            // login) must not count — the Library would stop offering Add for a language that is
            // half there.
            string voiceDir = Path.Combine(tmp, "Genshin 1.6 voices");
            Directory.CreateDirectory(voiceDir);
            File.WriteAllText(Path.Combine(voiceDir, "GenshinImpact.exe"), "not really an exe");
            File.WriteAllText(Path.Combine(voiceDir, "Audio_English(US)_pkg_version"), "{}");
            File.WriteAllText(Path.Combine(voiceDir, "Audio_Japanese_pkg_version"), "{}");
            Directory.CreateDirectory(Path.Combine(voiceDir, "GenshinImpact_Data", "StreamingAssets", "Audio", "GeneratedSoundBanks", "Windows", "Korean"));
            var vs = new RelicState { Installed = { new InstalledVersion { Id = "1.6", GameDir = voiceDir, ProfileId = "priv16" } } };
            Check("RefreshVoices: fills the field from the marker files (display order) and reports a change",
                vs.RefreshVoices() && vs.Installed[0].Voices.SequenceEqual(new[] { "English(US)", "Japanese" }));
            Check("RefreshVoices: a sound-bank folder without its marker is not counted", !vs.Installed[0].Voices.Contains("Korean"));
            Check("RefreshVoices: second pass is a no-op", !vs.RefreshVoices());
            File.Delete(Path.Combine(voiceDir, "Audio_Japanese_pkg_version"));
            Check("RefreshVoices: a pack removed outside Relic drops out",
                vs.RefreshVoices() && vs.Installed[0].Voices.SequenceEqual(new[] { "English(US)" }));
            string voicePath = Path.Combine(tmp, "voices-state.json");
            vs.Save(voicePath);
            Check("voices survive save/load", RelicState.Load(voicePath).Installed[0].Voices.SequenceEqual(new[] { "English(US)" }));
            // An older state.json has no "voices" key: an empty list, never null, and a refresh over a
            // folder with no markers stays a no-op (no pointless save at every boot).
            string noVoicesPath = Path.Combine(tmp, "novoices-state.json");
            File.WriteAllText(noVoicesPath,
                $$"""{ "Installed": [ { "Id": "2.8", "GameDir": {{JsonSerializer.Serialize(realDir)}}, "ProfileId": "priv28" } ] }""");
            var nv = RelicState.Load(noVoicesPath);
            Check("state without 'voices' loads with an empty list, never null",
                nv.Installed.Count == 1 && nv.Installed[0].Voices is { Count: 0 });
            Check("RefreshVoices over a folder with no markers is a no-op", !nv.RefreshVoices() && nv.Installed[0].Voices.Count == 0);
            // Same rule as PruneMissing: an unplugged external disk must not blank the list.
            if (free != '\0')
            {
                var offlineV = new RelicState
                {
                    Installed = { new InstalledVersion { Id = "2.8", GameDir = $@"{free}:\Relic Games\Genshin 2.8", Voices = { "English(US)", "Korean" } } },
                };
                Check($"RefreshVoices: an install on an unmounted drive ({free}:) keeps its list",
                    !offlineV.RefreshVoices() && offlineV.Installed[0].Voices.Count == 2);
            }
            var blankV = new RelicState { Installed = { new InstalledVersion { Id = "9.9", GameDir = "  ", Voices = { "Japanese" } } } };
            Check("RefreshVoices: a blank GameDir is skipped without throwing",
                !blankV.RefreshVoices() && blankV.Installed[0].Voices.Count == 1);
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { /* best effort */ }
        }

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"state spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }
}

/// <summary>
/// Proves the bundled-fix mechanic: the files the archives do not carry (2.8's patched
/// global-metadata.dat, the Win11 ayy/anime injector pair) really land in the game folder, are NOT
/// rewritten once they are current, keep the original exactly once, and repair a destination that
/// went bad. Also pins the two failure shapes the callers depend on: a required file the build does
/// not ship is a GamePatchException (never InvalidDataException — Backend keys the corrupt-cache
/// cleanup on that type), and an optional one is just a log line. The rest of the checks pin the
/// cases where "be lenient" would produce a silently unlaunchable install instead: an entry with no
/// sha256, a manifest that is present but unparsable, a dst escaping the game dir on a non-required
/// entry, and a game dir that is a bare drive root.
///
/// Everything runs against a SYNTHETIC payload root + manifest in a temp dir — kilobytes, not the
/// real 48 MB metadata — so the spike is fast and touches no install.
/// </summary>
static class PatchSpike
{
    const string MetaDst = "GenshinImpact_Data/Managed/Metadata/global-metadata.dat";
    const string NoHashDst = "extra/nohash.bin";

    public static int Run()
    {
        Console.WriteLine("== patch spike: bundled per-version fixes applied, skipped, backed up, repaired ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        string tmp = Path.Combine(Path.GetTempPath(), "relic-patch-spike");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);

        try
        {
            string payload = Path.Combine(tmp, "payload");
            // .bin sources → real names at the destination, exactly like the shipped manifest: a
            // single-file publish bundles any loose .exe/.dll INTO Relic.exe, so the payload cannot
            // carry those extensions.
            string launcherSrc = Write(payload, "common/ayy/anime/build/launcher.exe.bin", "INJECTOR-STUB");
            string dllSrc = Write(payload, "common/ayy/anime/build/mhynot2.dll.bin", "MHYNOT2-STUB");
            string metaSrc = Write(payload, "2.8/global-metadata.dat", "PATCHED-METADATA-2.8");
            Write(payload, "9.9/nohash.bin", "NO-HASH-PAYLOAD");
            string eeSrc = Write(payload, "5.5/ee/CLibrary.dll.bin", "EE-STUB");
            // Never created: "2.8/optional.bin" (an optional entry this build has no payload for),
            // "1.6/global-metadata.dat" (a REQUIRED one — the hard-failure case) and
            // "5.5/ee/Missing.dll.bin" (an inject entry this build does not ship).
            File.WriteAllText(Path.Combine(payload, "manifest.json"), $$"""
            {
              "common": [
                { "src": "common/ayy/anime/build/launcher.exe.bin", "dst": "ayy/anime/build/launcher.exe",
                  "sha256": "{{Sha(launcherSrc)}}" },
                { "src": "common/ayy/anime/build/mhynot2.dll.bin", "dst": "ayy/anime/build/mhynot2.dll",
                  "sha256": "{{Sha(dllSrc)}}" }
              ],
              "versions": {
                "2.8": [
                  { "src": "2.8/global-metadata.dat", "dst": "{{MetaDst}}",
                    "sha256": "{{Sha(metaSrc)}}", "backup": true, "required": true },
                  { "src": "2.8/optional.bin", "dst": "extra/optional.bin", "sha256": "00" }
                ],
                "1.6": [
                  { "src": "1.6/global-metadata.dat", "dst": "{{MetaDst}}",
                    "sha256": "00", "required": true }
                ],
                "9.9": [
                  { "src": "9.9/nohash.bin", "dst": "{{NoHashDst}}", "required": true }
                ],
                "5.5": [
                  { "src": "5.5/ee/CLibrary.dll.bin", "dst": "ayy/anime/ee/CLibrary.dll",
                    "sha256": "{{Sha(eeSrc)}}", "inject": true },
                  { "src": "5.5/ee/Missing.dll.bin", "dst": "ayy/anime/ee/Missing.dll",
                    "sha256": "00", "inject": true }
                ]
              }
            }
            """);

            string gameDir = Path.Combine(tmp, "Genshin 2.8");
            string meta = Path.Combine(gameDir, MetaDst.Replace('/', Path.DirectorySeparatorChar));
            string backup = meta + GamePatcher.BackupSuffix;
            string launcher = Path.Combine(gameDir, "ayy", "anime", "build", "launcher.exe");
            Write(gameDir, "GenshinImpact.exe", "not really an exe");
            Write(gameDir, MetaDst, "ORIGINAL-METADATA");   // what the 2.8 archive actually ships

            Check("plan = common entries + the version's own",
                GamePatcher.PlanFor("2.8", payload).Count == 4 && GamePatcher.PlanFor("1.6", payload).Count == 3);
            Check("a version with no entries of its own still gets the common ones",
                GamePatcher.PlanFor("3.0", payload).Count == 2);
            Check("nothing applied yet -> not up to date", !GamePatcher.IsUpToDate("2.8", gameDir, payload));

            // --- fresh apply ---
            var r1 = GamePatcher.Apply("2.8", gameDir, null, payload);
            Check("fresh apply copied the 3 shipped files", r1.Applied.Count == 3 && r1.Changed);
            Check("optional entry without a payload copy is reported, not thrown",
                r1.Missing.Count == 1 && r1.Missing[0] == "extra/optional.bin");
            Check("metadata replaced with the patched copy", Read(meta) == "PATCHED-METADATA-2.8");
            Check("injector pair created (dirs made on the way)",
                Read(launcher) == "INJECTOR-STUB" &&
                Read(Path.Combine(gameDir, "ayy", "anime", "build", "mhynot2.dll")) == "MHYNOT2-STUB");
            Check("original metadata kept as .relic-orig", Read(backup) == "ORIGINAL-METADATA");
            Check("everything is up to date afterwards", GamePatcher.IsUpToDate("2.8", gameDir, payload));

            // The late-injector case: LauncherExe/InjectDll are only ever written at registration, so
            // the play-time self-heal has to re-run this detection after patching.
            var inst = new InstalledVersion { Id = "2.8", GameDir = gameDir, ProfileId = "priv28" };
            InstallService.DetectInjector(inst);
            Check("DetectInjector picks up the just-patched injector",
                inst.LauncherExe == launcher && inst.InjectDll is not null);

            // --- idempotent re-apply: read-only destinations prove nothing was even attempted ---
            File.SetAttributes(meta, FileAttributes.ReadOnly);
            File.SetAttributes(launcher, FileAttributes.ReadOnly);
            PatchReport r2;
            try { r2 = GamePatcher.Apply("2.8", gameDir, null, payload); }
            finally
            {
                File.SetAttributes(meta, FileAttributes.Normal);
                File.SetAttributes(launcher, FileAttributes.Normal);
            }
            Check("re-apply copies nothing (read-only files survive it)",
                !r2.Changed && r2.AlreadyCurrent.Count == 3);

            // --- a destination that went bad is repaired, and the backup stays the FIRST original ---
            File.WriteAllText(meta, "CORRUPTED");
            Check("corrupt destination -> not up to date", !GamePatcher.IsUpToDate("2.8", gameDir, payload));
            var r3 = GamePatcher.Apply("2.8", gameDir, null, payload);
            Check("corrupt destination repaired", r3.Applied.Count == 1 && Read(meta) == "PATCHED-METADATA-2.8");
            Check("backup taken exactly once (still the real original)", Read(backup) == "ORIGINAL-METADATA");

            // --- required payload file the build does not ship: hard, dedicated failure ---
            string gameDir16 = Path.Combine(tmp, "Genshin 1.6");
            Write(gameDir16, "GenshinImpact.exe", "not really an exe");
            Exception? boom = Catch(() => GamePatcher.Apply("1.6", gameDir16, null, payload));
            Check("missing REQUIRED payload file throws GamePatchException", boom is GamePatchException);
            Check("and NOT InvalidDataException (that one means a corrupt zip)", boom is not InvalidDataException);
            Check("required missing -> IsUpToDate stays false", !GamePatcher.IsUpToDate("1.6", gameDir16, payload));
            Check("the common entries still went in before the failure",
                File.Exists(Path.Combine(gameDir16, "ayy", "anime", "build", "launcher.exe")));

            // --- an entry with no sha256 at all: nothing to content-address it by, so it is copied
            // every run and the copy is NOT verified against a hash that does not exist (a hashless
            // required entry would otherwise fail verification forever, leaving the version unlaunchable).
            string gameDirNoHash = Path.Combine(tmp, "Genshin 9.9");
            Write(gameDirNoHash, "GenshinImpact.exe", "not really an exe");
            string noHash = Path.Combine(gameDirNoHash, NoHashDst.Replace('/', Path.DirectorySeparatorChar));
            var rNo = TryApply("9.9", gameDirNoHash, payload);
            Check("entry without sha256 is copied, not failed as \"sha256 mismatch\"",
                rNo is not null && rNo.Applied.Contains(NoHashDst) && rNo.Missing.Count == 0);
            Check("and its payload bytes really landed", Read(noHash) == "NO-HASH-PAYLOAD");
            Check("an entry we cannot content-address is refreshed, never \"already up to date\"",
                !GamePatcher.IsUpToDate("9.9", gameDirNoHash, payload) &&
                TryApply("9.9", gameDirNoHash, payload)?.Applied.Contains(NoHashDst) == true);

            // --- a build without payload/ (dev machine) must install and play, not crash ---
            string empty = Path.Combine(tmp, "no-payload");
            Directory.CreateDirectory(empty);
            Check("absent manifest -> empty plan", GamePatcher.PlanFor("2.8", empty).Count == 0);
            Check("absent manifest -> Apply is a no-op, no throw",
                Catch(() => GamePatcher.Apply("2.8", gameDir, null, empty)) is null);
            Check("absent manifest -> nothing to do", GamePatcher.IsUpToDate("2.8", gameDir, empty));

            // --- a manifest that IS there but unparsable is the opposite case: we no longer know what
            // this version needed, and 2.8's required metadata may be exactly what was lost, so it
            // must be loud instead of launching a client that dies at "The game was not detected"
            // (core.play.gameNotDetected).
            string broken = Path.Combine(tmp, "broken-payload");
            Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "manifest.json"), "{ \"common\": [ nope");
            Check("unreadable manifest -> NOT up to date", !GamePatcher.IsUpToDate("2.8", gameDir, broken));
            Check("unreadable manifest -> Apply gives the real diagnosis",
                Catch(() => GamePatcher.Apply("2.8", gameDir, null, broken)) is GamePatchException);
            Check("unreadable manifest -> PlanFor stays empty and never throws",
                GamePatcher.PlanFor("2.8", broken).Count == 0);

            // --- a dst escaping the game dir is refused before anything is written ---
            string evil = Path.Combine(tmp, "evil-payload");
            string evilSrc = Write(evil, "x.bin", "PWNED");
            File.WriteAllText(Path.Combine(evil, "manifest.json"), $$"""
            { "common": [ { "src": "x.bin", "dst": "../escaped.bin", "sha256": "{{Sha(evilSrc)}}", "required": true } ] }
            """);
            Check("dst outside the game dir is rejected",
                Catch(() => GamePatcher.Apply("2.8", gameDir, null, evil)) is GamePatchException);
            Check("and nothing was written outside", !File.Exists(Path.Combine(tmp, "escaped.bin")));

            // ...and the Required flag must not soften it: an entry pointing outside the game is a
            // broken manifest, not an optional file to skip. Downgraded to a log line it would also leave
            // IsUpToDate false forever, so every launch would re-run Apply for nothing.
            string evilOpt = Path.Combine(tmp, "evil-optional-payload");
            string evilOptSrc = Write(evilOpt, "x.bin", "PWNED");
            File.WriteAllText(Path.Combine(evilOpt, "manifest.json"), $$"""
            { "common": [ { "src": "x.bin", "dst": "../escaped-optional.bin", "sha256": "{{Sha(evilOptSrc)}}" } ] }
            """);
            Check("an escaping dst is fatal even for a NON-required entry",
                Catch(() => GamePatcher.Apply("2.8", gameDir, null, evilOpt)) is GamePatchException);
            Check("and nothing was written outside for that one either",
                !File.Exists(Path.Combine(tmp, "escaped-optional.bin")));

            // --- a game unzipped straight to a drive root ("D:\") ---
            // Path.TrimEndingDirectorySeparator leaves a root alone, so the containment prefix cannot
            // just be gameDir + '\' — that would reject EVERY destination under a root. Driven against an
            // UNMOUNTED letter with a payload whose only source file is absent: the run fails on the
            // missing source, so this check can never write anywhere near a real drive root.
            string rootPayload = Path.Combine(tmp, "root-payload");
            Directory.CreateDirectory(rootPayload);
            File.WriteAllText(Path.Combine(rootPayload, "manifest.json"), """
            { "common": [ { "src": "missing.bin", "dst": "ayy/anime/build/launcher.exe",
                            "sha256": "00", "required": true } ] }
            """);
            string? freeRoot = FreeDriveRoot();
            if (freeRoot is null)
                Console.WriteLine("  [SKIP] drive-root game dir — every drive letter is in use on this box");
            else
                Check("a game dir that IS a drive root is not read as \"outside the game folder\"",
                    Catch(() => GamePatcher.Apply("2.8", freeRoot, null, rootPayload))
                        is GamePatchException rootEx && !rootEx.Message.Contains("outside the game folder"));

            // --- in-game enhancements: "inject" entries ride the same plan, gated by the toggle.
            // Version "5.5" ships one inject DLL and declares a second the build does not carry.
            string gameDir55 = Path.Combine(tmp, "Genshin 5.5");
            Write(gameDir55, "GenshinImpact.exe", "not really an exe");
            string eeDst55 = Path.Combine(gameDir55, "ayy", "anime", "ee", "CLibrary.dll");
            Check("inject entries are in the plan by default (common 2 + inject 2)",
                GamePatcher.PlanFor("5.5", payload).Count == 4);
            Check("withInjectables:false drops exactly the inject entries",
                GamePatcher.PlanFor("5.5", payload, withInjectables: false).Count == 2
                && GamePatcher.PlanFor("5.5", payload, withInjectables: false).All(e => !e.Inject));
            var rOff = GamePatcher.Apply("5.5", gameDir55, null, payload, withInjectables: false);
            Check("toggle OFF: only the common pair is copied, no ee/ file at all, nothing missing",
                rOff.Applied.Count == 2 && rOff.Missing.Count == 0 && rOff.MissingInjectables.Count == 0
                && !File.Exists(eeDst55));
            Check("toggle OFF: up to date without the injectables, NOT up to date with them",
                GamePatcher.IsUpToDate("5.5", gameDir55, payload, withInjectables: false)
                && !GamePatcher.IsUpToDate("5.5", gameDir55, payload));
            var rOn = TryApply("5.5", gameDir55, payload);
            Check("toggle ON: the shipped DLL lands, the unshipped one is reported, no throw",
                rOn is not null && rOn.Applied.Contains("ayy/anime/ee/CLibrary.dll") && Read(eeDst55) == "EE-STUB");
            Check("MissingInjectables = the unshipped inject entry, also listed in Missing",
                rOn is not null && rOn.MissingInjectables.SequenceEqual(new[] { "ayy/anime/ee/Missing.dll" })
                && rOn.Missing.SequenceEqual(new[] { "ayy/anime/ee/Missing.dll" }));
            Check("toggle ON: up to date afterwards (an unshipped optional entry does not block)",
                GamePatcher.IsUpToDate("5.5", gameDir55, payload));
            var inj = GamePatcher.Injectables("5.5", gameDir55, payload);
            Check("Injectables lists both inject entries in manifest order, Shipped true/false, dst resolved",
                inj.Count == 2 && inj[0].Dst == "ayy/anime/ee/CLibrary.dll" && inj[0].Shipped
                && inj[1].Dst == "ayy/anime/ee/Missing.dll" && !inj[1].Shipped
                && string.Equals(inj[0].DstPath, eeDst55, StringComparison.OrdinalIgnoreCase));
            Check("Injectables: a version without any -> empty; no payload at all -> empty, no throw",
                GamePatcher.Injectables("2.8", gameDir, payload).Count == 0
                && GamePatcher.Injectables("5.5", gameDir55, empty).Count == 0
                && GamePatcher.Injectables("5.5", gameDir55, broken).Count == 0);
            var ee55 = Enhancements.Resolve("5.5", gameDir55, true, payload);
            Check("Enhancements.Resolve on the patched dir: 1 dll, 0 missing (unshipped never counts as missing)",
                ee55.Enabled && ee55.Shipped && ee55.Dlls.Count == 1 && ee55.Missing.Count == 0
                && string.Equals(ee55.Dlls[0], eeDst55, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"patch spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    static string Write(string root, string relative, string content)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    static string Sha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>Apply, handing back null instead of throwing: a regression has to stay one FAIL line
    /// instead of an unhandled exception that hides every check after it.</summary>
    static PatchReport? TryApply(string versionId, string gameDir, string payloadRoot)
    {
        try { return GamePatcher.Apply(versionId, gameDir, null, payloadRoot); }
        catch { return null; }
    }

    /// <summary>A drive letter with nothing mounted on it (so nothing there can be read or written),
    /// or null on a box where every letter is taken.</summary>
    static string? FreeDriveRoot()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'D'; c--)
            if (!used.Contains(c)) return $"{c}:\\";
        return null;
    }

    static Exception? Catch(Action body)
    {
        try { body(); return null; }
        catch (Exception ex) { return ex; }
    }
}

/// <summary>
/// The account the player logs into THE GAME with is the one thing Relic cannot do for them automatically, so
/// the UI shows it at the end of the install, in the Library and on the launch splash. That name
/// comes from the payload manifest — the VERY save imported on the server — so the mistake that matters
/// here is not a display one but a drift one: the catalogue says "aether", the save landed on another account,
/// and the player hits a login that does not work and believes the server is down.
///
/// The first part runs on a synthetic payload in temp (kilobytes), the second checks the REAL files
/// of the repo — read-only, nothing is installed and nothing is written outside the temp folder.
/// </summary>
static class AccountsSpike
{
    public static int Run()
    {
        Console.WriteLine("== accounts spike: the game account shown to the player (synthetic payload + repo files) ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        string tmp = Path.Combine(Path.GetTempPath(), "relic-accounts-spike-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            WriteManifest(tmp, "1.6", "\"account\": \"aether\", \"password\": \"123123123\"");
            WriteManifest(tmp, "2.8", "\"account\": \"Aetherr\", \"password\": \"any\"");
            WriteManifest(tmp, "3.0", "\"title\": \"no account\"");
            Directory.CreateDirectory(Path.Combine(tmp, "9.9"));
            File.WriteAllText(Path.Combine(tmp, "9.9", "manifest.json"), "{ \"account\": nope");

            var v16 = GameAccounts.For(new GameVersionInfo { Id = "1.6", Server = "1.6" }, tmp);
            Check("the account comes from the payload manifest", v16.Account == "aether");
            Check("a concrete password from the manifest is offered as the example", v16.PasswordExample == "123123123");

            var v28 = GameAccounts.For(new GameVersionInfo { Id = "2.8", Server = "2.8" }, tmp);
            Check("the second payload has its own account", v28.Account == "Aetherr");
            // "any" is an instruction, not a password: offered as "e.g.: any" it would send the person to type exactly that.
            Check("\"any\" never becomes the password example",
                v28.PasswordExample == GameAccounts.DefaultPasswordExample);

            // The manifest is the file that describes the imported save, so it has the last word.
            Check("the manifest beats the catalogue value",
                GameAccounts.For(new GameVersionInfo { Id = "1.6", Server = "1.6", Account = "wrong" }, tmp).Account == "aether");
            // The lookup key is "server", not the id: the catalogue may name the version differently.
            Check("the key used is the server version, not the catalogue id",
                GameAccounts.For(new GameVersionInfo { Id = "1.6-ro", Server = "1.6" }, tmp).Account == "aether");

            // A build without the agent payloads: the catalogue is the safety net (publish.ps1 only
            // warns when they are missing, so this case really can reach a user).
            Check("without a payload, the catalogue account saves the display",
                GameAccounts.For(new GameVersionInfo { Id = "4.2", Server = "4.2", Account = "somebody" }, tmp).Account == "somebody");
            // Unknown MEANS unknown: the UI hides the block, it does not invent a name.
            Check("version with no source at all -> empty account (the UI stays silent)",
                GameAccounts.For(new GameVersionInfo { Id = "4.2", Server = "4.2" }, tmp).Account.Length == 0);
            Check("manifest without the account field -> empty account",
                GameAccounts.For(new GameVersionInfo { Id = "3.0", Server = "3.0" }, tmp).Account.Length == 0);
            // A broken manifest must not throw: it would take down BuildInitState, i.e. the app start.
            Check("invalid manifest -> empty account, no exception",
                GameAccounts.For(new GameVersionInfo { Id = "9.9", Server = "9.9" }, tmp).Account.Length == 0);
            // The key goes into a file path; a "server" containing ../ must not read another folder.
            Check("a key with path traversal reads nothing",
                GameAccounts.For(new GameVersionInfo { Id = "x", Server = "../../anywhere" }, tmp).Account.Length == 0);

            // ── the server's word wins: the state-aware overload the shortcut splash reads.
            // An account created through Relic on the CURRENT server → the answer the server gave last
            // time ("" = it said there is none — a keep/fixes stack — and the UI must show nothing) → the
            // manifest/catalogue, which applies only BEFORE this server ever answered. ──
            var st = new RelicState();
            st.Settings.ServerHost = "game.example.com"; st.Settings.ServerPort = 21000;
            Check("state overload: no answer from the server yet -> the manifest/catalogue account",
                GameAccounts.ForVersion("1.6", st, tmp).Account == "aether");
            Check("state overload: an EMPTY remembered answer hides the account (never falls back to the catalogue)",
                st.RememberServerAccount("game.example.com", 21000, "1.6", "") && GameAccounts.ForVersion("1.6", st, tmp).Account.Length == 0);
            Check("state overload: the server's name wins over the manifest",
                st.RememberServerAccount("game.example.com", 21000, "1.6", "traveler") && GameAccounts.ForVersion("1.6", st, tmp).Account == "traveler");
            Check("state overload: remembering the same answer again reports no change (no save on every poll)",
                !st.RememberServerAccount("game.example.com", 21000, "1.6", "traveler"));
            st.RememberAccount("game.example.com", 21000, "1.6", "mine", null);
            Check("state overload: an account created through Relic wins over everything",
                GameAccounts.ForVersion("1.6", st, tmp).Account == "mine");
            Check("state overload: another server's answer does not apply (host:port keyed)",
                GameAccounts.ForVersion("1.6", new RelicState { Settings = { ServerHost = "other.example.com", ServerPort = 21000 } }, tmp).Account == "aether");
            Check("state overload: no server configured -> the plain fallback",
                GameAccounts.ForVersion("1.6", new RelicState(), tmp).Account == "aether");
            Check("state overload: the password example is untouched by the overload (still never shown as required)",
                GameAccounts.ForVersion("1.6", st, tmp).PasswordExample == "123123123");

            // ── the real repo files ──
            string? realManifest = FindUp("agent/payloads/1.6/manifest.json");
            string? catalogPath = FindUp("config/versions.json") ?? FindUp("config/versions.sample.json");
            if (realManifest is null || catalogPath is null)
            {
                Console.WriteLine("  [SKIP] the repo files (payload/catalog) were not found from here");
            }
            else
            {
                string payloads = Path.GetDirectoryName(Path.GetDirectoryName(realManifest)!)!;
                var catalog = VersionCatalog.Load(catalogPath);
                Check("the real catalogue loaded", catalog.Count > 0);
                foreach (var v in catalog)
                {
                    string fromPayload = GameAccounts.For(new GameVersionInfo { Id = v.Id, Server = v.Server }, payloads).Account;
                    string fromCatalog = (v.Account ?? "").Trim();
                    Check($"version {v.Id} has an account to show the player",
                        fromPayload.Length > 0 || fromCatalog.Length > 0);
                    // Both set but different = exactly the drift that sends the person into a dead login.
                    if (fromPayload.Length > 0 && fromCatalog.Length > 0)
                        Check($"version {v.Id}: the catalogue names the same account as the save ({fromPayload})",
                            string.Equals(fromPayload, fromCatalog, StringComparison.Ordinal));
                }
            }

            // ── several accounts per player: the list, the login shown, an older build's state ──
            var ms = new RelicState();
            ms.Accounts["game.example.com:21000"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["2.8"] = new RememberedAccount { Name = "oldone", CreatedAt = "2026-09-20T00:00:00Z", Generation = "g1" }
            };
            Check("multi: an older build's single login reads as a list of one",
                ms.AccountListFor("game.example.com", 21000, "2.8").Select(a => a.Name).SequenceEqual(new[] { "oldone" }));
            ms.RememberAccount("game.example.com", 21000, "2.8", "second", "g1");
            Check("multi: the first account created after the upgrade joins the old one (never replaces it)",
                ms.AccountListFor("game.example.com", 21000, "2.8").Select(a => a.Name).SequenceEqual(new[] { "oldone", "second" }));
            Check("multi: the newest account is the login shown", ms.AccountFor("game.example.com", 21000, "2.8")?.Name == "second");
            ms.RememberAccount("game.example.com", 21000, "2.8", "SECOND", "g1");
            Check("multi: the same name again (any case) is one entry, moved to the end",
                ms.AccountListFor("game.example.com", 21000, "2.8").Select(a => a.Name).SequenceEqual(new[] { "oldone", "SECOND" }));
            Check("multi: use another of the list as the login", ms.UseAccount("game.example.com", 21000, "2.8", "OLDONE")
                && ms.AccountFor("game.example.com", 21000, "2.8")?.Name == "oldone");
            Check("multi: a name not in the list changes nothing", !ms.UseAccount("game.example.com", 21000, "2.8", "stranger")
                && ms.AccountFor("game.example.com", 21000, "2.8")?.Name == "oldone");
            Check("multi: lists are per server and per version",
                ms.AccountListFor("game.example.com", 21000, "1.6").Count == 0 && ms.AccountListFor("other.example.com", 21000, "2.8").Count == 0);
            string round = System.Text.Json.JsonSerializer.Serialize(ms);
            var back = System.Text.Json.JsonSerializer.Deserialize<RelicState>(round)!;
            Check("multi: the list and the login survive a save + load",
                back.AccountListFor("game.example.com", 21000, "2.8").Count == 2 && back.AccountFor("game.example.com", 21000, "2.8")?.Name == "oldone");
            var ids = new RelicState();
            string idA = ids.ClientIdFor("game.example.com"), idA2 = ids.ClientIdFor(" GAME.example.com "), idB = ids.ClientIdFor("other.example.com");
            Check("multi: the launcher id is 32 hex characters, stable per server, different per server",
                idA.Length == 32 && idA.All(Uri.IsHexDigit) && idA == idA2 && idA != idB && !string.IsNullOrEmpty(ids.ClientSecret));
            Check("multi: another installation has another id for the same server",
                new RelicState().ClientIdFor("game.example.com") != idA);
            var ids2 = new RelicState { ClientSecret = ids.ClientSecret };
            Check("multi: the id follows the saved secret (same secret = same id)", ids2.ClientIdFor("game.example.com") == idA);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"accounts spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    static void WriteManifest(string root, string version, string body)
    {
        string dir = Path.Combine(root, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{ " + body + " }");
    }

    static string? FindUp(string relative)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }
}

/// <summary>
/// A SynchronizationContext that behaves like a UI thread which is busy: it accepts continuations
/// and never runs them (a real one would run them when it pumps, which a blocked thread cannot do).
/// Anything that completes while this is installed is proof the call did not need the calling thread
/// back — the property that keeps Stop/health from freezing the launcher.
/// </summary>
sealed class NeverPumpingContext : SynchronizationContext
{
    private readonly Action _onPost;
    public NeverPumpingContext(Action onPost) => _onPost = onPost;
    public override void Post(SendOrPostCallback d, object? state) => _onPost();
    public override void Send(SendOrPostCallback d, object? state) => _onPost();
}
