using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Relic.Core.Util;

namespace Relic.Core.Launch;

/// <summary>
/// Resolves a PID to its on-disk image path. Exists because <see cref="ProcessModule"/> access
/// (<c>Process.MainModule</c>) opens the target with PROCESS_QUERY_INFORMATION|PROCESS_VM_READ to
/// walk its module list, and the miHoYo clients deny exactly those rights:
/// MainModule fails for HoYoPlay's own HYP.exe / HYPHelper.exe
/// while <c>QueryFullProcessImageNameW</c> — which only needs PROCESS_QUERY_LIMITED_INFORMATION —
/// returns their correct paths. GenshinImpact.exe has the same anti-tamper posture, so every
/// path-based identification in the app must start from the limited-rights API or it silently
/// resolves nothing.
/// </summary>
internal static class ProcessImagePath
{
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    // DllImport rather than LibraryImport (which Relic.App uses): the generated marshalling for a
    // ref char buffer emits a `fixed` block, and Relic.Core.csproj does not set AllowUnsafeBlocks.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        int desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        nint process, int flags, StringBuilder buffer, ref int size);

    /// <summary>
    /// Full image path of <paramref name="pid"/>, or null if every tier was refused. Tiers, in
    /// order: QueryFullProcessImageNameW (limited rights, ~free) → MainModule (works for our own
    /// and other unhardened processes) → WMI. WMI is gated behind <paramref name="allowSlowFallback"/>
    /// because it spawns a PowerShell AND is itself handle-derived — it returns an EMPTY
    /// path for a restricted process such as HYP.exe. It may therefore
    /// only run at one-shot decision points, never inside a poll loop.
    /// </summary>
    public static string? TryGet(int pid, bool allowSlowFallback = false)
    {
        string? path = FromImageName(pid);
        if (path is not null) return path;

        try
        {
            using var p = Process.GetProcessById(pid);
            path = p.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path)) return path;
        }
        catch { /* denied, or the process exited between enumeration and here */ }

        if (!allowSlowFallback) return null;

        try
        {
            // Shelled out rather than referencing System.Management: that would be a new package
            // reference for a genuinely last-resort tier.
            var r = Proc.Run("powershell", "-NoProfile", "-NonInteractive", "-Command",
                $"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').ExecutablePath");
            string wmi = r.Out.Trim();
            if (r.Ok && wmi.Length > 0) return wmi;
        }
        catch { /* powershell missing or blocked — nothing left to try */ }

        return null;
    }

    private static string? FromImageName(int pid)
    {
        nint h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == nint.Zero) return null;
        try
        {
            var buf = new StringBuilder(1024);
            int size = buf.Capacity;
            // An empty success counts as a failure, not as "path is blank": callers compare paths,
            // and "" would silently become a non-match instead of falling through to the next tier.
            if (QueryFullProcessImageNameW(h, 0, buf, ref size))
                return buf.Length > 0 ? buf.ToString() : null;
            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return null;

            buf = new StringBuilder(32767); // NT max path, only ever allocated on the retry
            size = buf.Capacity;
            return QueryFullProcessImageNameW(h, 0, buf, ref size) && buf.Length > 0
                ? buf.ToString()
                : null;
        }
        finally { CloseHandle(h); }
    }
}

/// <summary>
/// Finds and waits on a game process by its FULL on-disk path. Necessary because both the private
/// and live clients are named "GenshinImpact.exe", and because the Win11 injector (launcher.exe)
/// exits immediately after resuming the game — so the only reliable exit signal is watching the real
/// GenshinImpact.exe by path.
/// </summary>
public static class GameProcessWatcher
{
    /// <summary>One pass over the candidates, split by whether their path could be read at all:
    /// Matched = resolved AND equal to the target; Unresolved = the path was refused, so the
    /// process can only be attributed by identity (see the tiers in the wait loop).</summary>
    private readonly record struct Scan(List<int> Matched, List<int> Unresolved);

    /// <summary>PID of the process matching <paramref name="processName"/> AND <paramref name="fullPath"/>, or null.</summary>
    public static int? FindByPath(string processName, string fullPath)
    {
        var scan = ScanFor(processName, fullPath);
        return scan.Matched.Count > 0 ? scan.Matched[0] : null;
    }

    /// <summary>All PIDs with this process name, no path resolution. Used to snapshot the field
    /// BEFORE a launch so a PID that appears afterwards can be attributed to it.</summary>
    public static IReadOnlyList<int> FindAllByName(string processName)
    {
        var pids = new List<int>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try { pids.Add(p.Id); }
            finally { p.Dispose(); }
        }
        return pids;
    }

    /// <summary>
    /// Wait for the game to APPEAR (up to <paramref name="appearTimeout"/>), then for it to be
    /// fully gone. Three independent signals count as "appeared", so no single failure mode can
    /// make us miss a client that is really there: the handle we started
    /// (<paramref name="started"/>), a process whose resolved path matches, or a process of this
    /// name whose PID was not in <paramref name="preExisting"/>. Returns false only if none of the
    /// three fired inside the window.
    /// </summary>
    /// <param name="started">Handle from Process.Start, when the caller started the exe itself.</param>
    /// <param name="preExisting">PIDs of this name that existed before the launch; null means
    /// nothing is excluded (the caller could not snapshot).</param>
    /// <param name="onAppeared">Fired once, the moment the game is confirmed up.</param>
    public static async Task<bool> WaitForAppearThenExitAsync(
        string processName, string fullPath, TimeSpan appearTimeout, CancellationToken ct = default,
        Process? started = null, IReadOnlyCollection<int>? preExisting = null, Action? onAppeared = null)
    {
        long deadline = Environment.TickCount64 + (long)appearTimeout.TotalMilliseconds;
        while (!(IsAlive(started) || AnyAttributable(processName, fullPath, preExisting)))
        {
            if (Environment.TickCount64 > deadline) return false;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        onAppeared?.Invoke();

        // A handle from Process.Start keeps the rights it was created with even after the anti-cheat
        // driver starts denying new opens, so when we own one it is the precise exit signal — no
        // polling, no chance of a denied Process.GetProcessById. It is never the LAST word though:
        // the game relaunches itself (elevation, crash handler) under a fresh PID, and reading that
        // as a clean exit is exactly what would let the profile swap run under a live client.
        if (started is not null)
        {
            try { await started.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Already reaped, or the handle was refused — the drain below is the fallback.
            }
        }

        // Drain: deliberately unbounded, because it must last as long as the user plays.
        while (AnyAttributable(processName, fullPath, preExisting))
            await Task.Delay(1000, ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>True if a process of this name is either at the expected path or new since the
    /// pre-launch snapshot. The name-only half does not weaken the identify-by-path invariant:
    /// PIDs that already existed are excluded by name AND identity, so only a client that started
    /// after us can match.</summary>
    private static bool AnyAttributable(string processName, string fullPath, IReadOnlyCollection<int>? preExisting)
    {
        var scan = ScanFor(processName, fullPath);
        if (scan.Matched.Count > 0) return true;
        return scan.Unresolved.Any(pid => preExisting is null || !preExisting.Contains(pid));
    }

    /// <summary>Whether a handle we started is still running. A HasExited that throws is read as
    /// "alive": under-reporting a live client is the dangerous direction (it would release the
    /// profile swap under it), over-reporting only costs a wait.</summary>
    private static bool IsAlive(Process? p)
    {
        if (p is null) return false;
        try { return !p.HasExited; } catch { return true; }
    }

    private static Scan ScanFor(string processName, string fullPath)
    {
        string target = SafeFullPath(fullPath);
        var matched = new List<int>();
        var unresolved = new List<int>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                string? path = ProcessImagePath.TryGet(p.Id);
                if (path is null) unresolved.Add(p.Id);
                else if (string.Equals(SafeFullPath(path), target, StringComparison.OrdinalIgnoreCase))
                    matched.Add(p.Id);
            }
            catch { unresolved.Add(p.Id); }
            finally { p.Dispose(); }
        }
        return new Scan(matched, unresolved);
    }

    private static string SafeFullPath(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }
}
