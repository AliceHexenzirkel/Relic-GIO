using System.Diagnostics;
using Relic.Core.Launch;

namespace Relic.Core.Isolation;

/// <summary>
/// Both the private and live clients run an executable literally named "GenshinImpact.exe", so we
/// must guard and identify by FULL module path, never by process name alone. Used to enforce
/// "only one version at a time" and to know which client (if any) is currently running.
/// </summary>
public static class ProcessGuard
{
    /// <summary>The image name (without ".exe") of every Genshin client, the private ones and the official one alike.</summary>
    public const string ClientProcessName = "GenshinImpact";

    /// <summary>
    /// The settle window: how long "no client runs" has to hold before it is trusted. The game can exit and
    /// respawn itself (elevation, crash handler) with a gap in which no GenshinImpact.exe is visible, so the
    /// post-exit decision (stop Fiddler, put the official profile back) and every recovery pass rest on a
    /// window this long — never on one shorter.
    /// </summary>
    public static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(5);

    /// <summary>The settle window at the end of a session that never saw its client: there is no evidence of
    /// where the client is in its lifecycle, so the wait is longer.</summary>
    public static readonly TimeSpan SettleWindowNeverSeen = TimeSpan.FromSeconds(15);

    /// <summary>How often a settle window looks at the running clients.</summary>
    public static readonly TimeSpan SettleStep = TimeSpan.FromMilliseconds(500);

    /// <summary>The settle window of a session end: <see cref="SettleWindowNeverSeen"/> when the session never
    /// saw its client, else <see cref="SettleWindow"/>.</summary>
    public static TimeSpan SettleWindowFor(bool clientNeverSeen) => clientNeverSeen ? SettleWindowNeverSeen : SettleWindow;

    public readonly record struct GameProcess(int Pid, string Path);

    /// <summary>All running GenshinImpact.exe processes with their on-disk paths (path empty if inaccessible).</summary>
    public static IReadOnlyList<GameProcess> RunningGenshin() => Running(ClientProcessName);

    /// <summary>All running processes named <paramref name="processName"/> (without ".exe") with their on-disk
    /// paths (path empty if inaccessible). <see cref="RunningGenshin"/> is this for the game's own name; the
    /// session spike lists its stand-ins through it under a name of their own, because a stand-in called
    /// GenshinImpact.exe is a client to every Relic running on the same PC.</summary>
    public static IReadOnlyList<GameProcess> Running(string processName)
    {
        var list = new List<GameProcess>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                // Via ProcessImagePath, not MainModule: the client denies the VM_READ right
                // MainModule needs, so MainModule reads nothing for exactly the process we care
                // about — which would leave every real client with an empty path here.
                list.Add(new GameProcess(p.Id, ProcessImagePath.TryGet(p.Id) ?? ""));
            }
            catch
            {
                // Access denied (e.g. different bitness/elevation) still means it is running.
                list.Add(new GameProcess(p.Id, ""));
            }
            finally { p.Dispose(); }
        }
        return list;
    }

    /// <summary>True if ANY Genshin client is running, private or live.</summary>
    public static bool AnyRunning()
    {
        // Name-only on purpose, and this does not weaken the identify-by-path invariant: that rule
        // exists so we never mistake the live client for the private one, and this answers a
        // different question — "may we touch the shared profile at all?" — where every client
        // counts equally. It also needs no process handle, so unlike a path lookup it can never
        // come back access-denied, which is what makes it safe to base the restore decision on.
        var procs = Process.GetProcessesByName(ClientProcessName);
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    /// <summary>
    /// True only if NO Genshin client is seen during the whole settle window (default
    /// <see cref="SettleWindow"/>, polled every <see cref="SettleStep"/>). One instant check is not enough
    /// before a profile swap: the game can exit and respawn itself (elevation, crash handler) with a gap in
    /// which no GenshinImpact.exe exists. Deliberately not cancellable — safety decisions must complete
    /// even if the surrounding session was cancelled.
    /// </summary>
    public static async Task<bool> NoneRunningSustainedAsync(TimeSpan? window = null)
    {
        long deadline = Environment.TickCount64 + (long)(window ?? SettleWindow).TotalMilliseconds;
        while (true)
        {
            if (AnyRunning()) return false;
            if (Environment.TickCount64 >= deadline) return true;
            await Task.Delay(SettleStep).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How the running clients relate to Relic's own game folders. <c>Ours</c> = a client started
    /// from one of our game dirs (or an unreadable-path process that did not exist before our launch);
    /// <c>Foreign</c> = a client with a readable path elsewhere (the official HoYoPlay client);
    /// <c>Unknown</c> = unreadable path we cannot attribute. Unknown is treated as ours by every
    /// caller (keep Fiddler, defer the restore) — the conservative reading.
    /// </summary>
    public readonly record struct Classification(int Ours, int Foreign, int Unknown)
    {
        public bool Any => Ours + Foreign + Unknown > 0;
    }

    /// <summary>Classify every running GenshinImpact.exe by path prefix against <paramref name="ourGameDirs"/>.
    /// <paramref name="processName"/>: another image name to classify instead (the session spike's stand-ins).</summary>
    public static Classification Classify(IEnumerable<string> ourGameDirs, IReadOnlyCollection<int>? preExistingPids = null,
        string? processName = null)
    {
        var dirs = ourGameDirs.Select(NormalizeDir).Where(d => d.Length > 0).ToArray();
        int ours = 0, foreign = 0, unknown = 0;
        foreach (var g in Running(processName ?? ClientProcessName))
        {
            if (g.Path.Length > 0)
            {
                string full = SafeFull(g.Path);
                if (dirs.Any(d => full.StartsWith(d, StringComparison.OrdinalIgnoreCase))) ours++;
                else foreign++;
            }
            else if (preExistingPids is not null && !preExistingPids.Contains(g.Pid))
            {
                ours++; // new since our launch, path unreadable: the anti-cheat driver hides our own client this way
            }
            else unknown++;
        }
        return new Classification(ours, foreign, unknown);
    }

    /// <summary>What was observed during a settle window (see <see cref="ObserveSustainedAsync"/>).</summary>
    public readonly record struct Settle(bool SawOurs, bool SawForeign, bool SawUnknown)
    {
        public bool SawAny => SawOurs || SawForeign || SawUnknown;
    }

    /// <summary>
    /// Watch the running clients for a whole settle window (default <see cref="SettleWindow"/>, polled every
    /// <see cref="SettleStep"/>) and report what was seen. The window exists because the game can exit and
    /// respawn itself (elevation, crash handler) with a gap in which no GenshinImpact.exe is visible — so
    /// "nothing running" is only trusted after a sustained absence. Returns early once OUR client is seen:
    /// that outcome is final (keep Fiddler, defer the profile restore). A foreign client does not
    /// end the window — our client may still respawn behind it. Deliberately not cancellable —
    /// safety decisions must complete even if the surrounding session was cancelled.
    /// <paramref name="processName"/>: another image name to watch instead (the session spike's stand-ins).
    /// </summary>
    public static async Task<Settle> ObserveSustainedAsync(IEnumerable<string> ourGameDirs,
        IReadOnlyCollection<int>? preExistingPids = null, TimeSpan? window = null, string? processName = null)
    {
        var dirs = ourGameDirs.ToArray();
        long deadline = Environment.TickCount64 + (long)(window ?? SettleWindow).TotalMilliseconds;
        bool foreign = false, unknown = false;
        while (true)
        {
            var c = Classify(dirs, preExistingPids, processName);
            if (c.Ours > 0) return new Settle(true, foreign || c.Foreign > 0, unknown || c.Unknown > 0);
            foreign |= c.Foreign > 0;
            unknown |= c.Unknown > 0;
            if (Environment.TickCount64 >= deadline) return new Settle(false, foreign, unknown);
            await Task.Delay(SettleStep).ConfigureAwait(false);
        }
    }

    private static string NormalizeDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";
        string full = SafeFull(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full + Path.DirectorySeparatorChar;
    }

    private static string SafeFull(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    /// <summary>True if a Genshin process is running from a path OTHER than the one we are about to launch.</summary>
    public static bool AnyRunningExcept(string expectedPath)
    {
        foreach (var g in RunningGenshin())
        {
            string path = g.Path;
            if (path.Length == 0)
            {
                // Spend the slow WMI tier before blocking the user on a single unreadable PID.
                // This is a one-shot decision point, not a poll loop, so the ~130ms is affordable —
                // and with the limited-rights lookup in place, getting here at all is rare
                // rather than the normal outcome for a miHoYo process.
                path = ProcessImagePath.TryGet(g.Pid, allowSlowFallback: true) ?? "";
                if (path.Length == 0) return true; // genuinely unreadable — treat as conflicting
            }
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
