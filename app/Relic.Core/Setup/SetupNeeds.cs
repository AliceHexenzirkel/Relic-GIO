using System.Globalization;
using Relic.Core.Install;
using Relic.Core.Server;
using Relic.Core.Util;

namespace Relic.Core.Setup;

/// <summary>A game version Quick Start downloads: its bytes (the client and the chosen voice packs) and the games
/// folder it goes into.</summary>
public sealed record GameDownloadNeed(string Version, long Bytes, string Root)
{
    /// <summary>The download of <paramref name="info"/> with the voice packs <paramref name="voices"/> into
    /// <paramref name="root"/>: the client's size and each chosen pack's, as the catalogue names them.</summary>
    public static GameDownloadNeed Of(GameVersionInfo info, IEnumerable<string> voices, string root) =>
        new(info.Id, info.Client.Size + (voices ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Sum(v => info.VoicePack(v)?.Size ?? 0), root);
}

/// <summary>A server version a run sets up: its choice (download | have | keep | add | ready), its folder, its
/// package as the shipped catalogue describes it, the pathfinding the run asks for (null = as the memory allows)
/// and whether its stack is prepared already (its images are there then).</summary>
public sealed record ServerVersionNeed(string Version, string Choice, string Dir, StackPackage Package, bool? Pathfinding, bool Prepared = false);

/// <summary>What a run is about, as <see cref="SetupNeeds.For"/> needs it: where the server runs
/// (existing | windows | linux), the game downloads, every chosen game version (the clients that play on this PC)
/// and the server versions.</summary>
public sealed record NeedsRequest
{
    public string Kind { get; init; } = "";
    public IReadOnlyList<GameDownloadNeed> Games { get; init; } = Array.Empty<GameDownloadNeed>();
    public IReadOnlyList<string> ClientVersions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ServerVersionNeed> Servers { get; init; } = Array.Empty<ServerVersionNeed>();
}

/// <summary>One thing a drive must hold: kind game | server | python | sevenZip | docker | images, its version
/// (null for a tool) and its bytes.</summary>
public sealed record DriveItem(string Kind, string? Version, long Bytes);

/// <summary>What a drive must hold for the run against what it has free (<see cref="Free"/> null = not read):
/// <see cref="Ok"/> unless the free space is known and short. <see cref="Box"/> = a filesystem of the Linux server
/// (named by where it is mounted), else a drive of this PC.</summary>
public sealed record DriveNeed(string Drive, long? Free, long Need, bool Ok, IReadOnlyList<DriveItem> Items, bool Box = false);

/// <summary>A server version's memory: what it needs without and with its pathfinding server, the memory it may
/// use (WSL 2's cap, or the server's memory less 1 GiB on Linux), the verdict (fits | noPathfinding | short |
/// unknown) and, for a server that stays short on this PC, the cap in GB that would let it fit
/// (<see cref="RaiseGb"/>; 0 = none that leaves Windows and the game client enough).</summary>
public sealed record RamNeed(long? Need, long? NeedPathfinding, long? Cap, string Verdict, int RaiseGb);

/// <summary>A reason the server cannot be set up on this PC: its code and its text.</summary>
public sealed record BlockingReason(string Code, string Text);

/// <summary>What the run has to add to this PC for a server here.</summary>
public sealed record SetupInstallsNeeded(bool Python, bool SevenZip, bool Wsl, bool WslUpdate, bool Docker, bool DockerStart)
{
    public static SetupInstallsNeeded None { get; } = new(false, false, false, false, false, false);
}

/// <summary>Everything <see cref="SetupNeeds.For"/> worked out: the drives, whether Docker's images could not be
/// counted, the memory per server version and the cap that fits the largest (<see cref="RaiseGb"/>), what blocks a
/// server on this PC, and what the run installs.</summary>
public sealed record NeedsResult(IReadOnlyList<DriveNeed> Drives, bool DockerImagesUnknown, IReadOnlyDictionary<string, RamNeed> Ram,
    int RaiseGb, IReadOnlyList<BlockingReason> Blocking, SetupInstallsNeeded Installs);

/// <summary>
/// What a Quick Start run needs of the disks, of the memory and of this PC, worked out from the plan, the machine
/// check (<see cref="MachineCheck"/>), the Linux server's facts (<see cref="BoxFacts"/>) and the two shipped
/// catalogues. Pure: nothing is read here.
/// </summary>
public static class SetupNeeds
{
    public const long GiB = 1L << 30;

    /// <summary>The room the agent keeps free beside a server download (<c>GIO_FETCH_DISK_RESERVE</c>'s default).</summary>
    public const long FetchReserve = 4 * GiB;

    /// <summary>The memory Windows keeps beside a raised WSL cap and the game client.</summary>
    public const long WindowsKeeps = 4 * GiB;

    /// <summary>The memory a Linux server keeps for itself beside the stack.</summary>
    public const long BoxKeeps = GiB;

    /// <summary>Work it out (see the class). <paramref name="machine"/> = this PC (null = not checked: no free space,
    /// no memory verdict on this PC, nothing blocks); <paramref name="box"/> = the Linux server (null = not probed).</summary>
    public static NeedsResult For(NeedsRequest request, MachineFacts? machine, BoxFacts? box, ServerNeedsCatalog needs, SetupToolsCatalog tools)
    {
        request ??= new NeedsRequest();
        needs ??= ServerNeedsCatalog.Empty;
        tools ??= SetupToolsCatalog.Empty;
        bool windows = string.Equals(request.Kind, "windows", StringComparison.Ordinal) && machine is not null;
        bool linux = string.Equals(request.Kind, "linux", StringComparison.Ordinal);
        var servers = (request.Servers ?? Array.Empty<ServerVersionNeed>()).Where(s => s is not null && s.Version.Length > 0).ToList();
        var installs = windows ? Installs(servers, machine!) : SetupInstallsNeeded.None;

        // ── the drives ──
        var items = new List<(string Drive, bool Box, DriveItem Item)>();
        foreach (var g in request.Games ?? Array.Empty<GameDownloadNeed>())
            if (g is not null && g.Bytes > 0) items.Add((MachineCheck.DriveOf(g.Root), false, new DriveItem("game", g.Version, 2 * g.Bytes)));
        bool extractorThere = linux || machine?.Extractor is not null || installs.SevenZip;
        foreach (var s in servers.Where(s => s.Choice == "download" && s.Package.Published))
        {
            long archive = s.Package.Archive == "zip" ? s.Package.Size
                : !extractorThere && s.Package.ZipSize > 0 ? s.Package.ZipSize : s.Package.Size;
            long unpacked = s.Package.ExtractedSize > 0 ? s.Package.ExtractedSize : 3 * archive;
            var item = new DriveItem("server", s.Version, archive + unpacked + FetchReserve);
            if (linux) items.Add((BoxMount(box, s.Dir), true, item));
            else if (windows) items.Add((MachineCheck.DriveOf(s.Dir), false, item));
        }
        bool imagesUnknown = false;
        if (windows)
        {
            string tools0 = machine!.ToolsDrive;
            if (installs.Python && tools.Python is { } py) items.Add((tools0, false, new DriveItem("python", null, 3 * Math.Max(py.Size, 0))));
            if (installs.SevenZip && tools.SevenZip is { } sz) items.Add((tools0, false, new DriveItem("sevenZip", null, 2 * Math.Max(sz.Size, 0))));
            if (installs.Docker && tools.Docker is { } dk) items.Add((tools0, false, new DriveItem("docker", null, Math.Max(dk.DiskBytes, 0))));
            // The stacks of every version use the same images: they are counted once, for the largest.
            var toBuild = servers.Where(s => s.Choice != "ready" && !s.Prepared).Select(s => needs.For(s.Version)).ToList();
            imagesUnknown = toBuild.Any(r => r?.Images is null);
            long images = toBuild.Where(r => r?.Images is not null).Select(r => r!.Images!.Value).DefaultIfEmpty(0).Max();
            if (images > 0) items.Add((tools0, false, new DriveItem("images", null, images)));
        }
        var drives = items.Where(i => i.Drive.Length > 0 && i.Item.Bytes > 0)
            .GroupBy(i => (i.Box, Key: i.Box ? i.Drive : i.Drive.ToUpperInvariant()))
            .Select(g =>
            {
                long need = g.Sum(i => i.Item.Bytes);
                long? free = g.Key.Box ? BoxFree(box, g.Key.Key) : DriveFree(machine, g.Key.Key);
                return new DriveNeed(g.Key.Key, free, need, free is null || free.Value >= need, g.Select(i => i.Item).ToList(), g.Key.Box);
            }).ToList();

        // ── the memory ──
        long? cap = linux ? (box?.MemTotal is > 0 ? box.MemTotal - BoxKeeps : null) : windows ? machine!.WslCap : null;
        long client = windows ? (request.ClientVersions ?? Array.Empty<string>()).Select(v => needs.Client(v) ?? 0).DefaultIfEmpty(0).Max() : 0;
        var ram = new Dictionary<string, RamNeed>(StringComparer.Ordinal);
        foreach (var s in servers)
        {
            if (ram.ContainsKey(s.Version)) continue;
            var row = needs.For(s.Version);
            if (row is null || cap is null) { ram[s.Version] = new RamNeed(row?.Ram, row?.RamPathfinding, cap, "unknown", 0); continue; }
            string verdict = Verdict(row.Ram, row.RamPathfinding, cap.Value, s.Pathfinding);
            int raise = 0;
            if (windows && verdict == "short" && machine!.Ram is { } total)
                raise = RaiseGb(s.Pathfinding == true ? row.RamPathfinding : row.Ram, total, client);
            ram[s.Version] = new RamNeed(row.Ram, row.RamPathfinding, cap, verdict, raise);
        }
        int raiseGb = ram.Values.Select(r => r.RaiseGb).DefaultIfEmpty(0).Max();

        // ── what keeps a server off this PC ──
        var blocking = windows ? Blocking(servers, machine!, tools, installs) : new List<BlockingReason>();
        return new NeedsResult(drives, imagesUnknown, ram, raiseGb, blocking, installs);
    }

    /// <summary>fits — the server fits with its pathfinding (as the agent decides it); noPathfinding — only without it;
    /// short — not even then. A run that asks for pathfinding off is judged without it, one that asks for it on with
    /// it (fits or short). Pure.</summary>
    public static string Verdict(long need, long needPathfinding, long cap, bool? pathfinding) => pathfinding switch
    {
        false => need <= cap ? "fits" : "short",
        true => needPathfinding <= cap ? "fits" : "short",
        _ => needPathfinding <= cap ? "fits" : need <= cap ? "noPathfinding" : "short",
    };

    /// <summary>The WSL cap in whole GB that fits <paramref name="need"/> with 1 GiB to spare, when the PC's
    /// <paramref name="total"/> memory still leaves Windows <see cref="WindowsKeeps"/> and the game client
    /// <paramref name="client"/> beside it; 0 otherwise. Pure.</summary>
    public static int RaiseGb(long need, long total, long client)
    {
        if (need <= 0 || total <= 0) return 0;
        long gb = (need + GiB + GiB - 1) / GiB;
        return total - gb * GiB >= WindowsKeeps + Math.Max(client, 0) ? (int)gb : 0;
    }

    /// <summary>What the run adds to this PC for a server here: Python when no usable one is found and the agent is
    /// to be installed; 7-Zip's extractor when none is found and a package to download comes as a .7z alone; WSL 2
    /// turned on (off, or not read while Docker does not run) or updated (too old while Docker does not run);
    /// Docker Desktop installed (missing) and started (not running). Pure.</summary>
    public static SetupInstallsNeeded Installs(IReadOnlyList<ServerVersionNeed> servers, MachineFacts machine)
    {
        bool dockerRunning = machine.Docker.State == "running";
        bool python = machine.Python is null && !machine.LocalAgent.Installed;
        bool sevenZip = machine.Extractor is null && (servers ?? Array.Empty<ServerVersionNeed>())
            .Any(s => s.Choice == "download" && s.Package.Published && s.Package.NeedsExtractor);
        bool wsl = machine.Wsl == "off" || (machine.Wsl == "unknown" && !dockerRunning);
        bool wslUpdate = machine.Wsl == "old" && !dockerRunning;
        bool docker = machine.Docker.State == "missing";
        return new SetupInstallsNeeded(python, sevenZip, wsl, wslUpdate, docker, !dockerRunning);
    }

    private static List<BlockingReason> Blocking(IReadOnlyList<ServerVersionNeed> servers, MachineFacts m, SetupToolsCatalog tools, SetupInstallsNeeded installs)
    {
        var list = new List<BlockingReason>();
        void Add(string code, string text) => list.Add(new BlockingReason(code, text));
        bool dockerRunning = m.Docker.State == "running";
        if (m.Arm) Add("arm", L.T("core.setup.check.arm"));
        if (!dockerRunning && m.Virtualization == "off") Add("virtOff", L.T("core.setup.check.virtOff"));
        if (installs.Docker && tools.Docker is { Usable: true } d)
        {
            int min = m.Windows11 ? d.MinWin11Build : d.MinWin10Build;
            if (m.WindowsBuild > 0 && m.WindowsBuild < min)
                Add("windowsOld", L.T("core.setup.check.windowsOld", new { win10 = d.MinWin10Build, win11 = d.MinWin11Build, build = m.WindowsBuild }));
            long? have = m.InstalledRam ?? m.Ram;
            if (have is { } h && h < d.MinRamBytes)
                Add("ramLow", L.T("core.setup.check.ramLow", new { need = Gb(d.MinRamBytes), have = Gb(h) }));
        }
        if (m.RestartPending == true && (installs.Wsl || installs.WslUpdate || installs.Docker))
            Add("restartPending", L.T("core.setup.check.restartPending"));
        var chosen = new HashSet<string>(servers.Select(s => s.Version), StringComparer.Ordinal);
        foreach (var f in m.Folders)
            if (f.Problem is { Length: > 0 } p && chosen.Contains(f.Version)) Add("folder", p);
        if (installs.Python && !tools.PythonUsable) Add("noPython", L.T("core.setup.check.noPython"));
        if (installs.SevenZip && !tools.SevenZipUsable) Add("noSevenZip", L.T("core.setup.check.noSevenZip"));
        if (installs.Docker && !tools.DockerUsable) Add("noDocker", L.T("core.setup.toolMissing", new { what = "Docker Desktop" }));
        return list;
    }

    /// <summary>GB with one decimal at most ("8", "7.8").</summary>
    public static string Gb(long bytes) => (bytes / (double)GiB).ToString("0.#", CultureInfo.InvariantCulture);

    private static string BoxMount(BoxFacts? box, string dir) =>
        box is not null && box.Free.TryGetValue(dir, out var f) && f.Mount.Length > 0 ? f.Mount : (dir ?? "").Trim();

    private static long? DriveFree(MachineFacts? machine, string drive) =>
        machine is not null && machine.Drives.TryGetValue(drive, out var free) ? free : null;

    private static long? BoxFree(BoxFacts? box, string mount)
    {
        if (box is null) return null;
        foreach (var kv in box.Free)
            if (string.Equals(kv.Value.Mount.Length > 0 ? kv.Value.Mount : kv.Key, mount, StringComparison.Ordinal)) return kv.Value.Avail;
        return null;
    }
}
