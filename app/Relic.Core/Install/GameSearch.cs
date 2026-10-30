using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Relic.Core.Isolation;
using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.Core.Install;

/// <summary>One entry of a folder's listing as the search sees it — what the listing itself carries, no extra read.</summary>
public readonly record struct GameSearchEntry(string Name, bool IsDirectory, FileAttributes Attributes, long Length);

/// <summary>What a folder's listing says of one of its entries (<see cref="GameSearchOptions.EntryInfo"/>): its
/// attributes and, for a reparse point, its tag — the entry itself is never opened.</summary>
public readonly record struct GameSearchEntryInfo(FileAttributes Attributes, uint ReparseTag);

/// <summary>
/// A game folder the search found (<c>findgames.found</c>). <paramref name="Search"/>: the number of the search that found
/// it (<see cref="GameSearch.Start"/>). <paramref name="Status"/>: <see cref="GameSearch.StatusUse"/> (its file list
/// names a build of a version Relic offers), <see cref="GameSearch.StatusRegistered"/> (already in the Library, as
/// <paramref name="RegisteredAs"/>), <see cref="GameSearch.StatusPick"/> (the version cannot be told from its file list:
/// <paramref name="Version"/> is what its config.ini says — the only version it can be added as — or "", and then it
/// can be added only as a version the player picks) or <see cref="GameSearch.StatusNone"/> (a version Relic does not
/// offer, or a folder that contradicts itself — never added).
/// <paramref name="Proof"/>: how the version was told — <see cref="GameSearch.ProofIdentity"/> (the list names the build
/// and the identity file has the size the list gives it), <see cref="GameSearch.ProofList"/> (the list names it, the file
/// is missing or of another size), <see cref="GameSearch.ProofConfig"/> (config.ini alone) or "" (nothing).
/// <paramref name="InstalledAt"/>: the version is installed at another folder. <paramref name="Note"/>: the line for the
/// player. <paramref name="Source"/>: where it was found — Relic's own folders, a client that runs, Windows' record of
/// programs started, the walk of the disks. <paramref name="ExeSize"/>: the size of its GenshinImpact.exe, a hint only.
/// </summary>
public sealed record GameSearchHit(
    int Search, string Dir, string Status, string Version, string Build, string Proof, bool Offered,
    string RegisteredAs, string InstalledAt, string Note, string Source, long ExeSize);

/// <summary>One physical disk of the search (<c>findgames.progress</c>): its drive letters, the folders listed on it, a
/// spinning disk, and "waiting" | "searching" | "done" | "stopped".</summary>
public sealed record GameSearchDiskState(string Name, int Dirs, bool Hdd, string State);

/// <summary>Where the search is (<c>findgames.progress</c>, at most four a second, the first one at its start):
/// <paramref name="Search"/>, "places" (Relic's folders, running clients, Windows' record, the drives asked which disk
/// they are on) or "disks" (the walk), the folders listed, the game folders found, the seconds since it began.</summary>
public sealed record GameSearchProgress(int Search, string Phase, int Dirs, int Found, double Seconds, IReadOnlyList<GameSearchDiskState> Disks);

/// <summary>
/// The end of a search (<c>findgames.done</c>, once, the last of that search). <paramref name="Stopped"/>: Stop ended
/// it. <paramref name="Deeper"/>: a deeper search (<see cref="GameSearch.Start"/> with deeper) can look at folders this
/// one did not — the depth limit or the time budget left some, a drive did not say in time which disk it is on, or it
/// was stopped. <paramref name="Budget"/>: the time budget ended it. <paramref name="Error"/>: "" unless the search failed.
/// </summary>
public sealed record GameSearchDone(int Search, bool Stopped, bool Deeper, bool Budget, int Found, int Dirs, double Seconds, string Error);

/// <summary>What the search knows of Relic when it starts — copied out of the state on the thread that owns it.</summary>
public sealed record GameSearchContext(
    IReadOnlyList<ImportRegistration> Installed, string InstallRoot, IReadOnlyCollection<string> Offered, string? PayloadRoot = null)
{
    public static GameSearchContext From(RelicState state, IEnumerable<GameVersionInfo> catalogue, string? payloadRoot = null) =>
        new(ImportRequest.Registrations(state), state.Settings.InstallRoot ?? "", catalogue.Select(v => v.Id).ToList(), payloadRoot);
}

/// <summary>
/// Where <see cref="GameSearch.ReadTraceNames"/> reads Windows' record of programs started, by the path of a key under
/// HKEY_CURRENT_USER: the names of its sub-keys, the names of its values and one string value (null: no such key or
/// value). <see cref="Registry"/> opens each key read-only and refuses one <see cref="GameSearch.IsForbiddenKey"/> names
/// before it is opened; another source exists for the spike.
/// </summary>
public sealed class GameSearchTraceSource
{
    public required Func<string, IReadOnlyList<string>?> SubKeys { get; init; }
    public required Func<string, IReadOnlyList<string>?> ValueNames { get; init; }
    public required Func<string, string, string?> Value { get; init; }

    /// <summary>This user's registry, read-only.</summary>
    public static GameSearchTraceSource Registry { get; } = new()
    {
        SubKeys = path => Names(path, k => k.GetSubKeyNames()),
        ValueNames = path => Names(path, k => k.GetValueNames()),
        Value = (path, name) =>
        {
            using var key = Open(path);
            try { return key?.GetValue(name) as string; }
            catch (Exception) { return null; }
        },
    };

    private static IReadOnlyList<string>? Names(string path, Func<RegistryKey, string[]> read)
    {
        using var key = Open(path);
        if (key is null) return null;
        try { return read(key); }
        catch (Exception) { return null; }
    }

    private static RegistryKey? Open(string path)
    {
        if (GameSearch.IsForbiddenKey(path)) return null;
        try { return Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, writable: false); }
        catch (Exception) { return null; }
    }
}

/// <summary>How a <see cref="GameSearch"/> searches. The defaults are the launcher's; the rest exists for the spike.</summary>
public sealed class GameSearchOptions
{
    /// <summary>Folders deeper than this below a drive's root are left for a deeper search.</summary>
    public int DepthLimit { get; init; } = 8;
    /// <summary>The time after which a search hands back what it did not walk and ends (null: none — Stop ends it).</summary>
    public TimeSpan? Budget { get; init; }
    /// <summary>The roots to walk, each one its own disk. Null: this PC's fixed drives, one worker per physical disk.</summary>
    public IReadOnlyList<string>? Roots { get; init; }
    /// <summary>Look at the sure places first: Relic's folders, running clients, Windows' record of programs started.</summary>
    public bool Places { get; init; } = true;
    /// <summary>Lists one folder (the file system's own listing when null).</summary>
    public Func<string, CancellationToken, IReadOnlyList<GameSearchEntry>>? Lister { get; init; }
    /// <summary>What its folder's listing says of one entry, or null when there is no such entry (the file system's own
    /// answer when null): how the walk tells a link from another reparse point, and how a sure place is held to the
    /// walk's rules on its way down from its drive's root.</summary>
    public Func<string, GameSearchEntryInfo?>? EntryInfo { get; init; }
    /// <summary>The full paths of the running GenshinImpact.exe processes (<see cref="ProcessGuard.RunningGenshin"/> when null).</summary>
    public Func<IReadOnlyList<string>>? RunningClients { get; init; }
    /// <summary>The names Windows' record of programs started holds (<see cref="GameSearch.ReadTraceNames"/> of this
    /// user's registry when null).</summary>
    public Func<IReadOnlyList<string>>? TraceNames { get; init; }
    public TimeSpan ProgressEvery { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// "Find it on this PC": a search of this PC's own disks for game folders, which only ever reads — it creates, changes
/// and deletes nothing, writes no registry value, asks for no rights, and never opens the game's or its launcher's keys
/// (<see cref="IsForbiddenKey"/>). The sure places first: Relic's own folders, the clients that run (by the full path of
/// their module) and Windows' record of programs started (<see cref="TraceKeys"/>) — each reached only as the walk would
/// reach it (no link, junction or mount point on its way down, nothing that is not on this PC). Then a best-first walk of
/// every fixed drive, one worker per physical disk (a drive with no disk extent — a virtual or cloud drive — is left out,
/// and so, until a deeper search, is a drive that does not say in time which disk it is on), folders whose names promise
/// a game first, past the folders that never hold one (<see cref="Prune"/>: the system's, caches and tool folders, links
/// and junctions, folders whose content is not on this PC) and down to <see cref="GameSearchOptions.DepthLimit"/>; a
/// deeper search goes on from the folders that limit left. A candidate is a folder whose listing holds the file
/// GenshinImpact.exe and the folder GenshinImpact_Data — nothing below it is walked — and it is told by its own file
/// list (<see cref="ClientIdentity.Identify"/>: the identity file's line, and that file's size), never by a hash.
/// Results stream as they are found, through one poster that hands them on one at a time, in the order they were made,
/// each event naming its search; Stop ends the search at once — its end follows everything posted before it and is the
/// last of it, a worker held in a folder the system is slow to open is left behind, and nothing it finds afterwards is
/// posted.
/// </summary>
public sealed class GameSearch
{
    public const string StatusUse = "use", StatusRegistered = "registered", StatusPick = "pick", StatusNone = "none";
    public const string ProofIdentity = "identity", ProofList = "list", ProofConfig = "config", ProofNone = "";
    public const string SourceRelic = "relic", SourceRunning = "running", SourceTraces = "traces", SourceDisk = "disk";
    public const string PhasePlaces = "places", PhaseDisks = "disks";
    public const string DiskWaiting = "waiting", DiskSearching = "searching", DiskDone = "done", DiskStopped = "stopped";

    /// <summary>The reasons <see cref="Prune"/> gives.</summary>
    public const string PruneLink = "link", PruneNotLocal = "notLocal", PruneHiddenSystem = "hiddenSystem",
        PruneSystem = "system", PruneTool = "tool", PruneAppData = "appData", PruneUnityData = "unityData";

    /// <summary>The keys of HKEY_CURRENT_USER the search reads, all of them Windows' own record of programs that were
    /// started: the value names of MuiCache, the Program Compatibility Assistant store and FeatureUsage, the ROT13 value
    /// names under UserAssist's <c>{GUID}\Count</c>, and <c>MatchedExeFullPath</c> under each child of GameConfigStore.</summary>
    public static readonly IReadOnlyList<string> TraceKeys = new[]
    {
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\ShowJumpView",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppLaunch",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist",
        @"System\GameConfigStore\Children",
    };

    // The keys of the game's profile and of its launcher: never opened, whatever asks.
    private static readonly string[] ForbiddenKeys =
    {
        @"Software\Cognosphere", @"Software\miHoYo", @"Software\WOW6432Node\Cognosphere", @"Software\WOW6432Node\miHoYo",
    };

    // Folder names at a drive's root that never hold a game.
    private static readonly HashSet<string> RootSkip = new(StringComparer.Ordinal)
    {
        "windows", "$recycle.bin", "system volume information", "$winreagent", "recovery", "perflogs", "windows.old",
        "$windows.~bt", "$windows.~ws", "$sysreset", "$getcurrent", "config.msi", "msocache", "onedrivetemp", "programdata",
        "$av_asw", "$extend",
    };

    // Folder names that never hold a game, anywhere: the system's stores and the caches of developer tools.
    private static readonly HashSet<string> AnySkip = new(StringComparer.Ordinal)
    {
        "node_modules", ".git", ".svn", ".hg", "__pycache__", ".venv", "venv", "site-packages", ".nuget", ".gradle",
        ".m2", ".cargo", ".rustup", ".vs", ".idea", ".vscode", ".cache", ".npm", "windowsapps", "wpsystem", "winsxs",
        "$recycle.bin", "system volume information",
    };

    // The folders of a Program Files folder that are the system's or a tool chain's own.
    private static readonly string[] ProgramFilesSkip =
    {
        "microsoft", "windows", "common files", "dotnet", "reference assemblies", "msbuild", "internet explorer",
        "modifiablewindowsapps",
    };

    private static readonly Regex Promising = new("genshin|yuanshen|impact|hk4e|gio|relic|mihoyo|hoyo|anime|grasscutter|cultivation",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Likely = new("games?|jocuri|spiele|juegos|jeux|download|desktop|program files|steam|epic",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // The content of the folder or the file is not on this PC: listing the one, or reading the other, fetches it.
    private const FileAttributes NotLocalAttributes =
        FileAttributes.Offline | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

    // A reparse point whose tag carries this bit names another place: a link, a junction, a mount point.
    private const uint NameSurrogateBit = 0x20000000;

    // How long a drive letter may take to say which disk it is on; one that does not answer is left for a deeper
    // search, which waits longer.
    private static readonly TimeSpan LetterProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeeperLetterProbeTimeout = TimeSpan.FromSeconds(15);
    // How long the workers have, once the time budget is spent, to hand back what they did not walk.
    private static readonly TimeSpan YieldGrace = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly GameSearchOptions _options;
    private readonly Func<string, CancellationToken, IReadOnlyList<GameSearchEntry>> _lister;
    private readonly Func<string, GameSearchEntryInfo?> _entryInfo;
    // The folders found since the last fresh search, by path and as the file system knows them (another name of a
    // folder — a short name, a drive letter made of a folder — is that folder): a deeper search posts only what is new.
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(ulong Volume, ulong High, ulong Low)> _knownIds = new();
    // The callbacks to hand on, in the order they were made (under _gate), and whether the poster is at work.
    private readonly Queue<Action> _posts = new();
    private bool _posting;
    private Run? _run;
    // Per disk, what the last search left to walk; a disk without an entry, or with null, is walked from its roots.
    private Dictionary<string, List<Node>?>? _frontier;
    private int _runs;

    public GameSearch(GameSearchOptions? options = null)
    {
        _options = options ?? new GameSearchOptions();
        _lister = _options.Lister ?? ListDirectory;
        _entryInfo = _options.EntryInfo ?? Native.Entry;
    }

    /// <summary>A search runs (a stopped one does not, even while a worker it left behind is still held).</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _run is { Finished: false }; }
    }

    /// <summary>
    /// Starts a search and answers its number (every event of it names it), or 0 when one runs already.
    /// <paramref name="deeper"/>: go on from the folders the last search left, with no depth limit (from the roots where
    /// it left nothing known), posting only folders not found yet; otherwise a fresh search. The callbacks are called one
    /// at a time from the search's poster, in the order the search made them, never after <paramref name="done"/>; each
    /// must return at once.
    /// </summary>
    public int Start(GameSearchContext context, bool deeper, Action<GameSearchHit>? found,
        Action<GameSearchProgress>? progress, Action<GameSearchDone>? done)
    {
        lock (_gate)
        {
            if (_run is { Finished: false }) return 0;
            if (!deeper)
            {
                _known.Clear();
                _knownIds.Clear();
                _frontier = null;
            }
            var run = new Run(++_runs, context, deeper, found, progress, done);
            _run = run;
            new Thread(() => Coordinate(run)) { IsBackground = true, Name = "relic-find-games" }.Start();
            Log.Info($"find games: search {run.Id} started{(deeper ? " (deeper)" : "")}");
            return run.Id;
        }
    }

    /// <summary>Ends the running search at once: nothing of it is posted after its end (stopped), which follows what was
    /// posted before it. False when none ran.</summary>
    public bool Stop()
    {
        lock (_gate)
        {
            var run = _run;
            if (run is null || run.Finished) return false;
            FinishLocked(run, stopped: true, budget: false, error: "");
            return true;
        }
    }

    private sealed record Node(string Path, int Depth, string Name, string Parent, string GrandParent);

    private sealed class Disk(string key, string name, bool hdd, List<Node> seeds)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public bool Hdd { get; } = hdd;
        public List<Node> Seeds { get; } = seeds;
        public int Dirs;
        public string State = DiskWaiting;
        // Handed back by its worker: what it did not walk (null: nothing known — a deeper search walks it again).
        public List<Node>? Rest;
        public bool HandedBack;
    }

    private sealed class Run(int id, GameSearchContext context, bool deeper, Action<GameSearchHit>? found,
        Action<GameSearchProgress>? progress, Action<GameSearchDone>? done)
    {
        public int Id { get; } = id;
        public GameSearchContext Context { get; } = context;
        public bool Deeper { get; } = deeper;
        public Action<GameSearchHit>? Found { get; } = found;
        public Action<GameSearchProgress>? Progress { get; } = progress;
        public Action<GameSearchDone>? Done { get; } = done;
        public CancellationTokenSource Cts { get; } = new();
        public CancellationToken Token => Cts.Token;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public bool Finished;
        public int? MaxDepth;
        public string Phase = PhasePlaces;
        public List<Disk> Disks = new();
        public HashSet<string> Hot = new(StringComparer.OrdinalIgnoreCase);
        public CountdownEvent? Pending;
        public int Dirs;
        public int Hits;
        // Drive letters left out because they did not say in time which disk they are on.
        public int LettersLeft;
        public TimeSpan LastProgress = TimeSpan.Zero;
        public volatile bool YieldRequested;
    }

    // ── the search ──

    private void Coordinate(Run run)
    {
        try
        {
            PostProgress(run); // it has begun: the sure places first
            if (_options.Places) SurePlaces(run);
            run.Token.ThrowIfCancellationRequested();
            run.MaxDepth = run.Deeper ? null : Math.Max(0, _options.DepthLimit);
            var disks = PlanDisks(run);
            HotFolders(run);
            var pending = new CountdownEvent(disks.Count);
            lock (_gate)
            {
                if (run.Finished) return;
                run.Disks = disks;
                run.Pending = pending;
                run.Phase = PhaseDisks;
                foreach (var disk in disks) disk.State = DiskSearching;
            }
            foreach (var disk in disks)
                new Thread(() => Walk(run, disk)) { IsBackground = true, Name = "relic-find-games " + disk.Name }.Start();
            PostProgress(run); // the disks the walk is on, at once
            while (true)
            {
                bool all = pending.Wait(_options.ProgressEvery, run.Token);
                if (all)
                {
                    Finish(run, stopped: false, budget: false);
                    return;
                }
                PostProgress(run);
                if (_options.Budget is { } budget && run.Clock.Elapsed >= budget)
                {
                    run.YieldRequested = true;
                    pending.Wait(YieldGrace, run.Token);
                    Finish(run, stopped: false, budget: true);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            // Stop ended it and has posted its end already.
        }
        catch (Exception ex)
        {
            Log.Error($"find games: search {run.Id} failed", ex);
            lock (_gate) FinishLocked(run, stopped: false, budget: false, error: ex.Message);
        }
    }

    private void Finish(Run run, bool stopped, bool budget)
    {
        lock (_gate) FinishLocked(run, stopped, budget, error: "");
    }

    private void FinishLocked(Run run, bool stopped, bool budget, string error)
    {
        if (run.Finished) return;
        run.Finished = true;
        run.Cts.Cancel();
        bool more;
        if (stopped || error.Length > 0)
        {
            _frontier = null;
            more = stopped;
        }
        else
        {
            _frontier = new Dictionary<string, List<Node>?>(StringComparer.OrdinalIgnoreCase);
            foreach (var disk in run.Disks) _frontier.TryAdd(disk.Key, disk.HandedBack ? disk.Rest : null);
            more = budget || run.LettersLeft > 0
                   || (run.MaxDepth is not null && run.Disks.Any(d => !d.HandedBack || d.Rest is null || d.Rest.Count > 0));
        }
        foreach (var disk in run.Disks)
            if (disk.State == DiskSearching) disk.State = disk.HandedBack ? DiskDone : DiskStopped;
        double seconds = Math.Round(run.Clock.Elapsed.TotalSeconds, 2);
        Log.Info($"find games: search {run.Id} ended — {run.Hits} game folder(s), {run.Dirs} folder(s) listed in {seconds:0.00} s"
                 + (stopped ? ", stopped" : budget ? ", time budget spent" : "") + (more ? ", more can be searched" : "")
                 + (error.Length > 0 ? $", failed: {error}" : ""));
        if (run.Done is { } done)
        {
            var end = new GameSearchDone(run.Id, stopped, more, budget, run.Hits, run.Dirs, seconds, error);
            Post(() => done(end));
        }
    }

    private void PostProgress(Run run)
    {
        lock (_gate)
        {
            if (run.Finished || run.Progress is null) return;
            var disks = run.Disks.Select(d => new GameSearchDiskState(d.Name, Volatile.Read(ref d.Dirs), d.Hdd, d.State)).ToList();
            var now = new GameSearchProgress(run.Id, run.Phase, Volatile.Read(ref run.Dirs), run.Hits,
                Math.Round(run.Clock.Elapsed.TotalSeconds, 2), disks);
            var progress = run.Progress;
            Post(() => progress(now));
            run.LastProgress = run.Clock.Elapsed;
        }
    }

    /// <summary>Hands a callback to the search's poster (under <see cref="_gate"/>, so the order of the queue is the
    /// order the search made them): the callbacks run one at a time, in that order, never on a worker's thread and
    /// never under the search's lock.</summary>
    private void Post(Action callback)
    {
        _posts.Enqueue(callback);
        if (_posting) return;
        _posting = true;
        ThreadPool.UnsafeQueueUserWorkItem(_ => Drain(), null);
    }

    private void Drain()
    {
        while (true)
        {
            Action? next;
            lock (_gate)
            {
                if (!_posts.TryDequeue(out next))
                {
                    _posting = false;
                    return;
                }
            }
            try { next(); }
            catch (Exception ex) { Log.Error("find games: posting an event failed", ex); }
        }
    }

    /// <summary>A candidate folder: told by its own files, posted once per search (and per deeper search after it),
    /// whatever name it was reached by.</summary>
    private void Consider(Run run, string dir, IReadOnlyList<GameSearchEntry> entries, string source)
    {
        string key = Normalize(dir) ?? dir;
        var id = FolderIdentity.Of(key);
        lock (_gate)
        {
            if (run.Finished || Known(key, id)) return;
        }
        var classified = Classify(key, entries, run.Context, source);
        if (classified is null) return;
        var hit = classified with { Search = run.Id };
        lock (_gate)
        {
            if (run.Finished || Known(key, id)) return;
            _known.Add(key);
            if (id is { } folder) _knownIds.Add(folder);
            run.Hits++;
            if (run.Found is { } found) Post(() => found(hit));
        }
        Log.Info($"find games: {hit.Dir} — {hit.Status}{(hit.Version.Length > 0 ? " " + hit.Version : "")} ({hit.Source})");
    }

    /// <summary>The folder was found already, by this name or by another (under <see cref="_gate"/>).</summary>
    private bool Known(string key, (ulong Volume, ulong High, ulong Low)? id) =>
        _known.Contains(key) || (id is { } folder && _knownIds.Contains(folder));

    /// <summary>Relic's own folders, the clients that run and Windows' record of programs started: each folder they name
    /// that the walk could reach is looked at once (a network path, a removable or optical drive, a folder past a link
    /// is never touched).</summary>
    private void SurePlaces(Run run)
    {
        var places = new List<(string Dir, string Source)>();
        foreach (var r in run.Context.Installed) places.Add((r.GameDir, SourceRelic));
        string? root = Normalize(run.Context.InstallRoot);
        if (root is not null && OnFixedDrive(root) && Reachable(root))
        {
            places.Add((root, SourceRelic));
            try
            {
                foreach (var entry in _lister(root, run.Token))
                    if (entry.IsDirectory && (entry.Attributes & (FileAttributes.ReparsePoint | NotLocalAttributes)) == 0)
                        places.Add((Path.Combine(root, entry.Name), SourceRelic));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Info($"find games: Relic's install folder could not be listed ({ex.Message})");
            }
        }
        try
        {
            var running = _options.RunningClients is null
                ? ProcessGuard.RunningGenshin().Select(g => g.Path).Where(p => p.Length > 0).ToList()
                : _options.RunningClients();
            foreach (string exe in running)
                if (Path.GetDirectoryName(exe) is { Length: > 0 } dir) places.Add((dir, SourceRunning));
        }
        catch (Exception ex) { Log.Info($"find games: the running clients could not be listed ({ex.Message})"); }
        try
        {
            var names = _options.TraceNames is null ? ReadTraceNames() : _options.TraceNames();
            foreach (string dir in TraceFolders(names)) places.Add((dir, SourceTraces));
        }
        catch (Exception ex) { Log.Info($"find games: Windows' record of programs started could not be read ({ex.Message})"); }

        foreach (var (dir, source) in places)
        {
            run.Token.ThrowIfCancellationRequested();
            LookAt(run, dir, source);
            if (run.Clock.Elapsed - run.LastProgress >= _options.ProgressEvery) PostProgress(run);
        }
    }

    private void LookAt(Run run, string dir, string source)
    {
        string? full = Normalize(dir);
        if (full is null || !OnFixedDrive(full)) return;
        lock (_gate)
        {
            if (run.Finished || _known.Contains(full)) return;
        }
        try
        {
            if (!Reachable(full)) return;
            var entries = _lister(full, run.Token);
            if (IsClient(entries)) Consider(run, full, entries, source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A folder a record names that is no longer there, or cannot be read.
        }
    }

    /// <summary>The way down from its drive's root to <paramref name="full"/> is one the walk takes: every folder on it is
    /// a folder on this PC, and none is a link, a junction or a mount point — each judged by its parent's listing
    /// (<see cref="GameSearchOptions.EntryInfo"/>), never opened, so a place a record names never leads off this PC's own
    /// drive.</summary>
    private bool Reachable(string full)
    {
        string root = Path.GetPathRoot(full) ?? "";
        if (root.Length == 0) return false;
        string path = root;
        foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            path = Path.Combine(path, part);
            if (_entryInfo(path) is not { } info) return false;
            if ((info.Attributes & FileAttributes.Directory) == 0 || (info.Attributes & NotLocalAttributes) != 0) return false;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && NamesAnotherPlace(info.ReparseTag)) return false;
        }
        return true;
    }

    /// <summary>The disks to walk, each with the folders to start from: its drive roots, or what the last search left.</summary>
    private List<Disk> PlanDisks(Run run)
    {
        List<(string Key, string Name, bool Hdd, List<string> Roots)> groups;
        if (_options.Roots is { } roots)
            groups = roots.Select(r => (Key: r, Name: r, Hdd: false, Roots: new List<string> { r })).ToList();
        else
        {
            var (fixedDisks, left) = FixedDisks(run.Token, run.Deeper ? DeeperLetterProbeTimeout : LetterProbeTimeout);
            groups = fixedDisks;
            run.LettersLeft = left;
        }
        Dictionary<string, List<Node>?>? frontier;
        lock (_gate) frontier = run.Deeper ? _frontier : null;
        var disks = new List<Disk>();
        foreach (var g in groups)
        {
            List<Node> seeds = frontier is not null && frontier.TryGetValue(g.Key, out var rest) && rest is not null
                ? rest
                : g.Roots.Select(r => new Node(r, 0, "", "", "")).ToList();
            disks.Add(new Disk(g.Key, g.Name, g.Hdd, seeds));
        }
        return disks;
    }

    /// <summary>The folders on the way to the folders that hold a known game: copies tend to sit beside each other.</summary>
    private void HotFolders(Run run)
    {
        var dirs = run.Context.Installed.Select(i => i.GameDir).ToList();
        lock (_gate) dirs.AddRange(_known);
        foreach (string dir in dirs)
        {
            string? p = Normalize(dir);
            p = p is null ? null : Path.GetDirectoryName(p);
            while (p is not null)
            {
                run.Hot.Add(Path.TrimEndingDirectorySeparator(p));
                p = Path.GetDirectoryName(p);
            }
        }
    }

    private void Walk(Run run, Disk disk)
    {
        var queue = new PriorityQueue<Node, (int Score, long Order)>();
        var later = new List<Node>();
        long order = 0;
        try
        {
            foreach (var seed in disk.Seeds) queue.Enqueue(seed, (Score(seed.Depth, seed.Name, run.Hot.Contains(seed.Path)), order++));
            while (queue.TryDequeue(out var node, out _))
            {
                if (run.Token.IsCancellationRequested) return;
                if (run.YieldRequested)
                {
                    var rest = new List<Node> { node };
                    rest.AddRange(queue.UnorderedItems.Select(i => i.Element));
                    rest.AddRange(later);
                    HandBack(run, disk, rest);
                    return;
                }
                IReadOnlyList<GameSearchEntry> entries;
                try { entries = _lister(node.Path, run.Token); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { continue; } // gone, or not this user's to list
                Interlocked.Increment(ref run.Dirs);
                Interlocked.Increment(ref disk.Dirs);
                if (IsClient(entries))
                {
                    Consider(run, node.Path, entries, SourceDisk);
                    continue; // a client is a leaf: nothing in it is another client
                }
                var unity = UnityDataFolders(entries);
                foreach (var entry in entries)
                {
                    if (!entry.IsDirectory) continue;
                    string name = entry.Name.ToLowerInvariant();
                    string path = Path.Combine(node.Path, entry.Name);
                    if (Prune(node.Depth, node.Name, node.Parent, name, entry.Attributes, unity?.Contains(name) == true,
                            () => _entryInfo(path) is not { } info || NamesAnotherPlace(info.ReparseTag)) is not null)
                        continue;
                    var child = new Node(path, node.Depth + 1, name, node.Name, node.Parent);
                    if (run.MaxDepth is int max && child.Depth > max)
                    {
                        later.Add(child);
                        continue;
                    }
                    queue.Enqueue(child, (Score(child.Depth, name, run.Hot.Contains(path)), order++));
                }
            }
            HandBack(run, disk, later);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"find games: the walk of {disk.Name} failed", ex);
            HandBack(run, disk, null);
        }
    }

    private void HandBack(Run run, Disk disk, List<Node>? rest)
    {
        lock (_gate)
        {
            if (disk.HandedBack) return;
            disk.HandedBack = true;
            disk.Rest = rest;
            if (!run.Finished) disk.State = DiskDone;
        }
        try { run.Pending?.Signal(); }
        catch (InvalidOperationException) { }
    }

    // ── the rules, pure ──

    /// <summary>
    /// The reason a folder met in the listing of a folder at <paramref name="parentDepth"/> (a drive's root: 0) is not
    /// entered, or null to enter it. Names in lower case. Never entered: a link, a junction or a mount point
    /// (<paramref name="nameSurrogate"/> tells a reparse point that names another place from one that does not; without
    /// it every reparse point counts as one), a folder whose content is not on this PC, a hidden system folder, the
    /// system's folders at a drive's root and in Program Files, the profiles Users\Default* and Users\All Users, every
    /// user's AppData, the stores of developer tools, and the data folder of a Unity program beside its .exe
    /// (<paramref name="unityData"/>).
    /// </summary>
    public static string? Prune(int parentDepth, string parentName, string grandParentName, string name,
        FileAttributes attributes, bool unityData, Func<bool>? nameSurrogate = null)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0 && (nameSurrogate?.Invoke() ?? true)) return PruneLink;
        if ((attributes & NotLocalAttributes) != 0) return PruneNotLocal;
        if ((attributes & FileAttributes.Hidden) != 0 && (attributes & FileAttributes.System) != 0) return PruneHiddenSystem;
        if (parentDepth == 0 && RootSkip.Contains(name)) return PruneSystem;
        if (AnySkip.Contains(name)) return PruneTool;
        if (parentName.StartsWith("program files", StringComparison.Ordinal)
            && ProgramFilesSkip.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            return PruneSystem;
        if (parentDepth == 1 && parentName == "users" && (name.StartsWith("default", StringComparison.Ordinal) || name == "all users"))
            return PruneSystem;
        if (parentDepth == 2 && grandParentName == "users" && name == "appdata") return PruneAppData;
        if (unityData) return PruneUnityData;
        return null;
    }

    /// <summary>A reparse tag that names another place (a link, a junction, a mount point) — or one that could not be
    /// read (<see cref="uint.MaxValue"/>), which counts the same.</summary>
    public static bool NamesAnotherPlace(uint reparseTag) => reparseTag == uint.MaxValue || (reparseTag & NameSurrogateBit) != 0;

    /// <summary>The order of the walk (smaller first): depth before everything, then a name that promises a game, a name
    /// that promises games or downloads, and a folder on the way to a known game folder.</summary>
    public static int Score(int depth, string name, bool hot)
    {
        int bonus = Promising.IsMatch(name) ? 40 : Likely.IsMatch(name) ? 12 : 0;
        if (hot) bonus = Math.Max(bonus, 40);
        return depth * 10 - bonus;
    }

    /// <summary>The listing holds the file GenshinImpact.exe and the folder GenshinImpact_Data.</summary>
    public static bool IsClient(IReadOnlyList<GameSearchEntry> entries)
    {
        bool exe = false, data = false;
        foreach (var e in entries)
        {
            if (!e.IsDirectory && string.Equals(e.Name, ImportCheck.ExeName, StringComparison.OrdinalIgnoreCase)) exe = true;
            else if (e.IsDirectory && string.Equals(e.Name, ImportCheck.DataName, StringComparison.OrdinalIgnoreCase)) data = true;
        }
        return exe && data;
    }

    /// <summary>The "&lt;name&gt;_data" folders of the listing that sit beside a "&lt;name&gt;.exe" (lower case), or null.</summary>
    private static HashSet<string>? UnityDataFolders(IReadOnlyList<GameSearchEntry> entries)
    {
        HashSet<string>? set = null;
        foreach (var e in entries)
        {
            if (e.IsDirectory || !e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            (set ??= new HashSet<string>(StringComparer.Ordinal)).Add(e.Name[..^4].ToLowerInvariant() + "_data");
        }
        return set;
    }

    /// <summary>
    /// What a candidate folder is: the version its own file list names (the identity file's line and that file's size,
    /// <see cref="ClientIdentity.Identify"/>) or its config.ini, against the catalogue and the registrations (a folder is
    /// compared as the file system knows it, <see cref="ImportCheck.SameDir"/>). Null for a folder whose file list or
    /// config.ini is not on this PC (a cloud placeholder: reading it would fetch it). The hit names no search (0).
    /// </summary>
    public static GameSearchHit? Classify(string dir, IReadOnlyList<GameSearchEntry> entries, GameSearchContext context, string source)
    {
        GameSearchEntry? FileEntry(string name)
        {
            foreach (var e in entries)
                if (!e.IsDirectory && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }
        var list = FileEntry(ClientIdentity.ListFile);
        var configFile = FileEntry(ImportCheck.ConfigName);
        if (list is { } l && (l.Attributes & NotLocalAttributes) != 0) return null;
        if (configFile is { } c && (c.Attributes & NotLocalAttributes) != 0) return null;

        string config = configFile is null ? "" : ImportCheck.ReadConfigVersion(Path.Combine(dir, ImportCheck.ConfigName)) ?? "";
        var id = list is null ? new ClientIdentification(ClientIdentityKind.Unknown) : ClientIdentity.Identify(dir, context.PayloadRoot);
        long exeSize = FileEntry(ImportCheck.ExeName)?.Length ?? -1;

        string version, proof;
        if (id.Kind == ClientIdentityKind.Match)
        {
            version = id.Version;
            proof = id.SizeProven ? ProofIdentity : ProofList;
        }
        else if (config.Length > 0)
        {
            version = config;
            proof = ProofConfig;
        }
        else
        {
            version = "";
            proof = ProofNone;
        }
        bool offered = version.Length > 0 && context.Offered.Contains(version, StringComparer.OrdinalIgnoreCase);
        var registered = context.Installed.FirstOrDefault(i => ImportCheck.SameDir(i.GameDir, dir));
        var elsewhere = version.Length == 0 ? null : context.Installed.FirstOrDefault(i =>
            string.Equals(i.Id, version, StringComparison.OrdinalIgnoreCase) && !ImportCheck.SameDir(i.GameDir, dir));

        string status, note;
        if (id.Kind == ClientIdentityKind.Match && config.Length > 0 && !string.Equals(config, id.Version, StringComparison.OrdinalIgnoreCase))
        {
            status = StatusNone;
            note = L.T("core.find.note.contradiction", new { config, version });
        }
        else if (registered is not null)
        {
            status = StatusRegistered;
            note = L.T("core.find.note.registered", new { version = registered.Id });
        }
        else if (version.Length > 0 && !offered)
        {
            status = StatusNone;
            note = L.T("core.find.note.notOffered", new { version });
        }
        else if (offered && proof is ProofIdentity or ProofList)
        {
            status = StatusUse;
            note = proof == ProofList ? L.T("core.install.inspect.identityFile", new { file = id.File }) : "";
        }
        else
        {
            status = StatusPick;
            note = proof == ProofConfig ? L.T("core.find.note.configOnly", new { version }) : L.T("core.find.note.unknown");
        }
        string installedAt = "";
        if (status is StatusUse or StatusPick && elsewhere is not null)
        {
            installedAt = elsewhere.GameDir;
            string where = L.T("core.find.note.installedAt", new { version, dir = elsewhere.GameDir });
            note = note.Length > 0 ? note + " " + where : where;
        }
        return new GameSearchHit(0, dir, status, version, id.Kind == ClientIdentityKind.Match ? id.Build : "", proof, offered,
            registered?.Id ?? "", installedAt, note, source, exeSize);
    }

    /// <summary>
    /// The folders that hold a GenshinImpact.exe named in these strings of Windows' record of programs started (a value
    /// name of MuiCache — "&lt;path&gt;.FriendlyAppName" —, a path of the Compatibility Assistant store, of FeatureUsage
    /// or of GameConfigStore, a UserAssist name after ROT13 — a known folder's {GUID} in front resolved), in their order,
    /// each once. Nothing is looked at on disk: a name that is no full path (an application id) is left out.
    /// </summary>
    public static IReadOnlyList<string> TraceFolders(IEnumerable<string> names)
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string name = ResolveKnownFolder(raw.Trim().Trim('"'));
            int at = name.IndexOf(ImportCheck.ExeName, StringComparison.OrdinalIgnoreCase);
            while (at > 0 && name[at - 1] is not ('\\' or '/'))
                at = name.IndexOf(ImportCheck.ExeName, at + 1, StringComparison.OrdinalIgnoreCase);
            if (at <= 0) continue;
            string exe = name[..(at + ImportCheck.ExeName.Length)];
            if (!Path.IsPathFullyQualified(exe)) continue;
            string? dir = Normalize(Path.GetDirectoryName(exe) ?? "");
            if (dir is not null && seen.Add(dir)) folders.Add(dir);
        }
        return folders;
    }

    /// <summary>ROT13, the way UserAssist keeps its value names.</summary>
    public static string Rot13(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            span[i] = c switch
            {
                >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
                >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
                _ => c,
            };
        }
    });

    /// <summary>A name that starts with a known folder's {GUID} (UserAssist writes some so) with that folder's path in
    /// its place; any other name as it is.</summary>
    public static string ResolveKnownFolder(string name)
    {
        if (name.Length < 38 || name[0] != '{' || name[37] != '}' || !Guid.TryParse(name[..38], out var id)) return name;
        return KnownFolderPath(id) is { Length: > 0 } folder ? folder + name[38..] : name;
    }

    /// <summary>The key is the game's or its launcher's (under Software\miHoYo or Software\Cognosphere): never opened.</summary>
    public static bool IsForbiddenKey(string path)
    {
        string p = (path ?? "").Replace('/', '\\').TrimStart('\\');
        foreach (string hive in new[] { @"HKEY_CURRENT_USER\", @"HKCU\" })
            if (p.StartsWith(hive, StringComparison.OrdinalIgnoreCase)) p = p[hive.Length..];
        return ForbiddenKeys.Any(k => p.Equals(k, StringComparison.OrdinalIgnoreCase)
                                      || p.StartsWith(k + @"\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The names of <see cref="TraceKeys"/> that name a GenshinImpact.exe — read-only, nothing else kept — from
    /// <paramref name="source"/> (this user's registry when null). A key <see cref="IsForbiddenKey"/> names is never
    /// asked for, whatever the source.</summary>
    public static IReadOnlyList<string> ReadTraceNames(GameSearchTraceSource? source = null)
    {
        var from = source ?? GameSearchTraceSource.Registry;
        var names = new List<string>();
        void Keep(IEnumerable<string>? values)
        {
            if (values is null) return;
            foreach (string v in values)
                if (v is not null && v.Contains(ImportCheck.ExeName, StringComparison.OrdinalIgnoreCase)) names.Add(v);
        }
        IReadOnlyList<string> SubKeys(string path) => IsForbiddenKey(path) ? Array.Empty<string>() : from.SubKeys(path) ?? Array.Empty<string>();
        IReadOnlyList<string>? ValueNames(string path) => IsForbiddenKey(path) ? null : from.ValueNames(path);
        foreach (string path in TraceKeys)
        {
            if (path.EndsWith(@"\UserAssist", StringComparison.Ordinal))
            {
                foreach (string guid in SubKeys(path))
                    Keep(ValueNames(path + @"\" + guid + @"\Count")?.Select(Rot13));
            }
            else if (path.EndsWith(@"\GameConfigStore\Children", StringComparison.Ordinal))
            {
                foreach (string child in SubKeys(path))
                {
                    string key = path + @"\" + child;
                    if (!IsForbiddenKey(key) && from.Value(key, "MatchedExeFullPath") is { } exe) Keep(new[] { exe });
                }
            }
            else Keep(ValueNames(path));
        }
        return names;
    }

    // ── the file system ──

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string p = path.Trim().Trim('"');
            return Path.IsPathFullyQualified(p) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The path lies on a fixed drive of this PC: never a network path, a removable or an optical drive.</summary>
    private static bool OnFixedDrive(string path)
    {
        try
        {
            string root = Path.GetPathRoot(path) ?? "";
            if (root.Length < 2 || root[1] != ':') return false;
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception) { return false; }
    }

    private static IReadOnlyList<GameSearchEntry> ListDirectory(string dir, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false,
        };
        var list = new List<GameSearchEntry>();
        foreach (var entry in new FileSystemEnumerable<GameSearchEntry>(dir,
                     (ref FileSystemEntry e) => new GameSearchEntry(e.FileName.ToString(), e.IsDirectory, e.Attributes, e.Length), options))
        {
            ct.ThrowIfCancellationRequested();
            list.Add(entry);
        }
        return list;
    }

    /// <summary>The fixed drives, grouped by the physical disk they are on (one worker each). A drive without a disk
    /// extent (a virtual drive) and a drive SUBST made of a folder are left out, and so is one that does not say within
    /// <paramref name="wait"/> which disk it is on — counted in the answer, so the end can offer a deeper search, which
    /// waits longer.</summary>
    private static (List<(string Key, string Name, bool Hdd, List<string> Roots)> Groups, int Left) FixedDisks(
        CancellationToken ct, TimeSpan wait)
    {
        var roots = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.Name).ToList();
        var probes = roots.Select(root => Task.Run(() => ProbeLetter(root))).ToArray();
        try { Task.WaitAll(probes, (int)wait.TotalMilliseconds, ct); }
        catch (AggregateException) { }
        var groups = new List<(string Key, string Name, bool Hdd, List<string> Roots)>();
        int left = 0;
        for (int i = 0; i < roots.Count; i++)
        {
            string root = roots[i];
            string letter = root.TrimEnd('\\');
            if (!probes[i].IsCompletedSuccessfully)
            {
                left++;
                Log.Info($"find games: {letter} did not say within {wait.TotalSeconds:0} s which disk it is on — not searched now");
                continue;
            }
            (bool use, string key, bool hdd) = probes[i].Result;
            if (!use) continue;
            int at = groups.FindIndex(g => g.Key == key);
            if (at < 0) groups.Add((key, letter, hdd, new List<string> { root }));
            else
            {
                var g = groups[at];
                g.Roots.Add(root);
                groups[at] = (g.Key, g.Name + " " + letter, g.Hdd || hdd, g.Roots);
            }
        }
        return (groups, left);
    }

    /// <summary>Which disk a drive letter is on, asked first of what answers without the drive's own media (the letter's
    /// target, the volume's disk extents), so a virtual drive is told before anything waits on it.</summary>
    private static (bool Use, string Key, bool Hdd) ProbeLetter(string root)
    {
        try
        {
            string letter = root[..2];
            if (Native.DosDevice(letter) is { } target && target.StartsWith(@"\??\", StringComparison.Ordinal))
                return (false, "", false); // SUBST: a folder of another drive, walked there
            int disk = Native.DiskNumber(letter);
            if (disk < 0)
            {
                Log.Info($"find games: {letter} has no disk extent (a virtual drive) — not searched");
                return (false, "", false);
            }
            if (!new DriveInfo(root).IsReady) return (false, "", false);
            return (true, "disk:" + disk, StorageInfo.HasSeekPenalty(root) == true);
        }
        catch (Exception ex)
        {
            Log.Info($"find games: {root} could not be probed ({ex.Message}) — not searched");
            return (false, "", false);
        }
    }

    private static string? KnownFolderPath(Guid id)
    {
        try { return Native.KnownFolder(id); }
        catch (Exception) { return null; }
    }

    private static class Native
    {
        private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
        private const uint ShareReadWrite = 0x3, OpenExisting = 3;
        private const uint KnownFolderDontVerify = 0x4000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize,
            byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryDosDeviceW")]
        private static extern uint QueryDosDevice(string device, char[] target, int max);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Win32FindData
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "FindFirstFileExW")]
        private static extern IntPtr FindFirstFileEx(string name, int infoLevel, out Win32FindData data, int searchOp, IntPtr filter, int flags);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr handle);

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        /// <summary>The number of the physical disk the volume of <paramref name="letter"/> ("C:") lies on, asked with a
        /// handle that carries no access right (no elevation); -1 when it has no disk extent.</summary>
        public static int DiskNumber(string letter)
        {
            using var volume = CreateFile(@"\\.\" + letter, 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (volume.IsInvalid) return -1;
            byte[] buffer = new byte[256];
            if (!DeviceIoControl(volume, IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, buffer, buffer.Length, out _, IntPtr.Zero))
                return -1;
            // VOLUME_DISK_EXTENTS: a DWORD count, padding, then DISK_EXTENT { DWORD DiskNumber; ... }.
            return BitConverter.ToInt32(buffer, 0) < 1 ? -1 : BitConverter.ToInt32(buffer, 8);
        }

        /// <summary>What a drive letter ("C:") stands for: \Device\HarddiskVolumeN, or \??\&lt;folder&gt; for SUBST.</summary>
        public static string? DosDevice(string letter)
        {
            var buffer = new char[1024];
            uint n = QueryDosDevice(letter, buffer, buffer.Length);
            return n == 0 ? null : new string(buffer, 0, (int)n).Split('\0')[0];
        }

        /// <summary>What the listing of its folder says of the entry at <paramref name="path"/>: its attributes and, for a
        /// reparse point, its tag — the entry itself is never opened. Null when there is no such entry.</summary>
        public static GameSearchEntryInfo? Entry(string path)
        {
            IntPtr handle = FindFirstFileEx(path, 1 /* FindExInfoBasic */, out var data, 0 /* FindExSearchNameMatch */, IntPtr.Zero, 0);
            if (handle == new IntPtr(-1)) return null;
            FindClose(handle);
            var attributes = (FileAttributes)data.FileAttributes;
            return new GameSearchEntryInfo(attributes, (attributes & FileAttributes.ReparsePoint) != 0 ? data.Reserved0 : 0);
        }

        public static string? KnownFolder(Guid id)
        {
            if (SHGetKnownFolderPath(id, KnownFolderDontVerify, IntPtr.Zero, out IntPtr path) != 0) return null;
            try { return Marshal.PtrToStringUni(path); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
    }
}
