using System.Diagnostics;
using Relic.Core.Util;

namespace Relic.Core.Launch;

public enum LaunchMethod
{
    /// <summary>Start GenshinImpact.exe directly: Win10 without the in-game enhancements.</summary>
    DirectExe,
    /// <summary>Start launcher.exe, which injects mhynot2.dll (plus the enhancements DLLs, if any)
    /// into the suspended game, resumes it and exits: Win11 always, Win10 when enhancements are on.</summary>
    InjectedLauncher,
}

/// <summary>A specific game install: its directory plus the optional injector + dll.</summary>
public sealed record GameInstall(string GameDir, string? LauncherExe = null, string? InjectDll = null)
{
    public string GameExe => Path.Combine(GameDir, "GenshinImpact.exe");
    public bool CanInject => LauncherExe is not null && InjectDll is not null
                             && File.Exists(LauncherExe) && File.Exists(InjectDll);
    /// <summary>Extra DLLs launcher.exe injects AFTER mhynot2.dll (the in-game enhancements):
    /// absolute paths, already verified to exist by <see cref="Enhancements.Resolve"/> — launcher.exe
    /// exits 1 over a missing argv DLL before the game is even created. Computed at launch time, never
    /// persisted. Non-empty forces the injected method on Windows 10 too: the real mhyprot2 driver must
    /// never load under the mod.</summary>
    public IReadOnlyList<string> ExtraDlls { get; init; } = Array.Empty<string>();
}

public sealed record LaunchHandle(
    LaunchMethod Method, string GameExePath, Process? Direct,
    IReadOnlyCollection<int>? PreExistingPids = null);

/// <summary>
/// Starts the game with the right method per OS. Win11 needs the launcher.exe injector (bypasses the
/// mhyprot2 anti-cheat so the old client runs); Win10 runs GenshinImpact.exe directly — unless the
/// in-game enhancements are on, in which case both OSes go through the injector with mhynot2.dll +
/// the enhancements DLL(s) (<see cref="GameInstall.ExtraDlls"/>). Either way the authoritative exit
/// signal is <see cref="GameProcessWatcher"/> watching the exe by path — never the launcher/shell
/// handle, which returns early.
/// </summary>
public static class GameLauncher
{
    /// <summary>Does this launch have to go through launcher.exe? On Windows 11 always (the mhyprot2
    /// driver), on any OS when there are extra DLLs to inject. Whether it CAN is
    /// <see cref="GameInstall.CanInject"/>.</summary>
    public static bool NeedsInjector(GameInstall install, bool? forceInjected = null) =>
        (forceInjected ?? WindowsInfo.Detect().IsWindows11) || install.ExtraDlls.Count > 0;

    public static LaunchMethod ChooseMethod(GameInstall install, bool? forceInjected = null) =>
        (NeedsInjector(install, forceInjected) && install.CanInject)
            ? LaunchMethod.InjectedLauncher
            : LaunchMethod.DirectExe;

    /// <summary>
    /// True when this machine needs the injector but the install has none, so <see cref="ChooseMethod"/>
    /// falls back to DirectExe and the client will almost certainly never come up ("The game was
    /// not detected within 90 seconds"). The fallback itself stays — it is the only thing left to try —
    /// but it must not be silent. On Win10 DirectExe is the CORRECT
    /// method rather than a fallback, so this is false there — deliberately OS-only: a Win10 box
    /// lacking the pair merely loses the enhancements, which PlaySession words separately.
    /// </summary>
    public static bool InjectorMissing(GameInstall install, bool? forceInjected = null) =>
        (forceInjected ?? WindowsInfo.Detect().IsWindows11) && !install.CanInject;

    /// <summary>
    /// The launcher.exe invocation, pure so the spike can pin the argv:
    /// <c>launcher.exe &lt;GameDir&gt; &lt;mhynot2.dll&gt; [&lt;extra dll&gt; ...]</c> — mhynot2 FIRST, the
    /// driver emulation must be in place before anything else loads (launcher.cpp injects in argv
    /// order). stdout is captured: launcher.exe prints exactly why it gave up, and that must not go
    /// nowhere.
    /// </summary>
    public static ProcessStartInfo LauncherStartInfo(GameInstall install)
    {
        var psi = new ProcessStartInfo(install.LauncherExe!)
        {
            WorkingDirectory = install.GameDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add(install.GameDir);   // argv[1]: base directory (launcher sets it as CWD)
        psi.ArgumentList.Add(install.InjectDll!); // argv[2]: mhynot2.dll, injected into the suspended game first
        foreach (var dll in install.ExtraDlls)
            psi.ArgumentList.Add(dll);            // argv[3..]: the enhancements, in manifest order
        return psi;
    }

    public static LaunchHandle Launch(GameInstall install, bool? forceInjected = null)
    {
        if (!File.Exists(install.GameExe))
            throw new FileNotFoundException("GenshinImpact.exe not found", install.GameExe);

        var method = ChooseMethod(install, forceInjected);
        if (InjectorMissing(install, forceInjected))
            Log.Error("launch: Windows 11 without the injector (ayy/anime/build/launcher.exe + mhynot2.dll) — " +
                $"trying {install.GameExe} directly, which usually does not start");

        // Snapshot the field BEFORE starting anything. The client denies the handle rights needed
        // to read its image path, so when the path lookup comes back empty the only remaining way
        // to tell OUR client from a stranger's is that its PID was not there a moment ago. The
        // injected path has no other identity signal at all — launcher.exe exits and its PID is
        // not the game's. Taken ONCE, before the first attempt: the direct fallback below must not
        // re-snapshot after a launcher that may have briefly created (and terminated) a client.
        var preExisting = GameProcessWatcher.FindAllByName("GenshinImpact");

        if (method == LaunchMethod.InjectedLauncher)
        {
            int code;
            string output;
            using (var launcher = Process.Start(LauncherStartInfo(install))
                ?? throw new InvalidOperationException("failed to start launcher.exe"))
            {
                // Drain stdout BEFORE waiting: a full pipe would block the launcher's printf and it
                // would never exit. The game does not inherit the pipe (CreateProcessW is called with
                // bInheritHandles FALSE in launcher.cpp), so the read ends when the launcher does.
                output = launcher.StandardOutput.ReadToEnd();
                launcher.WaitForExit(); // injects then exits immediately; the game keeps running
                code = launcher.ExitCode;
            }
            if (code == 0)
                return new LaunchHandle(method, install.GameExe, Direct: null, PreExistingPids: preExisting);

            Log.Error($"launch: launcher.exe exited {code}: {output.Trim()}");
            // On Windows 11 the injector is the only way the client starts, so there is nothing
            // better to try: hand back the injected handle and let the watcher say "not detected".
            // When it was wanted ONLY for the extra DLLs (Windows 10 + the
            // enhancements), the plain start is what the user had without them, and it is safe:
            // exit != 0 means launcher.exe never created the game or already terminated it (the one
            // exception, a failed ResumeThread, leaves a suspended client we own the PID snapshot
            // against). The caller reads Method == DirectExe off the handle to tell the user.
            if (forceInjected ?? WindowsInfo.Detect().IsWindows11)
                return new LaunchHandle(method, install.GameExe, Direct: null, PreExistingPids: preExisting);
            Log.Info("launch: the injector was needed only for the extra DLLs — starting GenshinImpact.exe directly without them");
            method = LaunchMethod.DirectExe;
        }

        {
            var psi = new ProcessStartInfo(install.GameExe)
            {
                WorkingDirectory = install.GameDir,
                UseShellExecute = true, // allow the game to relaunch/elevate itself as it expects
            };
            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start GenshinImpact.exe");
            return new LaunchHandle(method, install.GameExe, Direct: proc, PreExistingPids: preExisting);
        }
    }

    /// <summary>Block until the game started by this launch has fully exited.
    /// Returns false only when NO client could be attributed to this launch within the appear
    /// window — it most likely never started. That is not on its own a reason to skip the profile
    /// restore: the caller decides that by re-checking what is actually running now.</summary>
    /// <param name="onAppeared">Fired once the game is confirmed up — the honest moment to tell
    /// the user it is running, which is not the moment we asked Windows to start it.</param>
    public static Task<bool> WaitForExitAsync(
        LaunchHandle h, CancellationToken ct = default, Action? onAppeared = null)
        => GameProcessWatcher.WaitForAppearThenExitAsync(
            "GenshinImpact", h.GameExePath, appearTimeout: TimeSpan.FromSeconds(90), ct,
            started: h.Direct, preExisting: h.PreExistingPids, onAppeared: onAppeared);
}
