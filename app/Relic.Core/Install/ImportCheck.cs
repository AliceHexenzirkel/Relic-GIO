using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Relic.Core.Isolation;
using Relic.Core.Launch;
using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.Core.Install;

/// <summary>One registration of <see cref="RelicState.Installed"/>, copied out of the state: a check that runs off the
/// thread that owns the state reads this copy, never the state itself.</summary>
public sealed record ImportRegistration(string Id, string GameDir);

/// <summary>
/// What <see cref="ImportCheck.Run"/> is asked about: the folder of a game copy the player already has, to be added as
/// version <see cref="VersionId"/>. <see cref="Path"/> is what was typed or picked — the folder, or the path of its
/// GenshinImpact.exe; quotes around it (Explorer's "Copy as path") are dropped.
/// </summary>
public sealed record ImportRequest
{
    public required string VersionId { get; init; }
    public required string Path { get; init; }
    /// <summary>Hash whole files as the import does (<see cref="GamePatcher.Preview"/>). Without it the check is the
    /// instant one: it can refuse, but it never says ready.</summary>
    public bool Deep { get; init; } = true;
    /// <summary>The folder the player chose to replace with this one — the <see cref="ImportVerdict.InstalledAt"/> they
    /// were shown. Only when <see cref="VersionId"/> is installed at that very folder does the registration move to this
    /// one; without it, or when it names another folder, a copy of an installed version is refused.</summary>
    public string? ReplaceInstalledAt { get; init; }
    /// <summary>The versions Relic has registered (<see cref="Registrations"/>).</summary>
    public IReadOnlyList<ImportRegistration> Installed { get; init; } = Array.Empty<ImportRegistration>();
    /// <summary>The version ids of the catalogue. Null: <see cref="VersionCatalog.Load"/>.</summary>
    public IReadOnlyCollection<string>? Offered { get; init; }
    /// <summary>The payload folder (the shipped one when null).</summary>
    public string? PayloadRoot { get; init; }
    /// <summary><see cref="RelicSettings.Enhancements"/>: the enhancements DLL is part of the plan.</summary>
    public bool WithInjectables { get; init; } = true;
    /// <summary><see cref="RelicSettings.OwnFilesChoiceFor"/> of the version: what PLAY does with a file of the player's own.</summary>
    public string OwnFilesChoice { get; init; } = OwnFileChoices.Ask;
    public bool IsWindows11 { get; init; } = WindowsInfo.Detect().IsWindows11;
    /// <summary>The full paths of the GenshinImpact.exe processes that run. Null: <see cref="ProcessGuard.RunningGenshin"/>.</summary>
    public Func<IReadOnlyList<string>>? RunningClients { get; init; }
    /// <summary>The bytes this user may still write on the drive of a folder, or null when the system does not say.
    /// Null: <see cref="ImportCheck.FreeSpaceAt"/>.</summary>
    public Func<string, long?>? FreeSpace { get; init; }

    /// <summary>The registrations of <paramref name="state"/>, copied — on the thread that owns the state.</summary>
    public static IReadOnlyList<ImportRegistration> Registrations(RelicState state) =>
        state.Installed.Select(i => new ImportRegistration(i.Id, i.GameDir)).ToList();

    /// <summary>A request about <paramref name="path"/> as version <paramref name="versionId"/>, with what
    /// <paramref name="state"/> holds copied into it — on the thread that owns the state.</summary>
    public static ImportRequest From(RelicState state, string versionId, string path) => new()
    {
        VersionId = (versionId ?? "").Trim(),
        Path = path ?? "",
        Installed = Registrations(state),
        WithInjectables = state.Settings.Enhancements,
        OwnFilesChoice = state.Settings.OwnFilesChoiceFor(versionId ?? ""),
    };
}

/// <summary>A line of an <see cref="ImportVerdict"/> that does not stop the import: its stable
/// <paramref name="Code"/> and the text for the player.</summary>
public sealed record ImportNote(string Code, string Text);

/// <summary>A file of the player's own under one of Relic's replacements: it stays as it is at the import
/// (<see cref="Dst"/>), and <see cref="Kept"/> is the name it gets beside Relic's file if the player chooses Relic's.</summary>
public sealed record ImportOwnFile(string Dst, string Kept);

/// <summary>
/// The answer of <see cref="ImportCheck.Run"/> — the same for the wizard's preview and for the import itself, which
/// runs it before its first write. <see cref="Status"/>: <see cref="Ready"/> (only a deep check says so),
/// <see cref="Checking"/> (the instant check found nothing against it; the deep one decides) or <see cref="Refused"/>
/// (<see cref="Reason"/> says why, <see cref="Message"/> is the text). Serialised as it is for the page.
/// </summary>
public sealed class ImportVerdict
{
    public const string Ready = "ready", Checking = "checking", Refused = "refused";

    /// <summary>The <see cref="Reason"/> codes of a refusal.</summary>
    public const string NoVersion = "noVersion", UnknownVersion = "unknownVersion", NoPath = "noPath",
        NotFullPath = "notFullPath", NotGameExe = "notGameExe", FolderMissing = "folderMissing", NoExe = "noExe",
        NoData = "noData", Contradiction = "contradiction", OtherVersion = "otherVersion", NotOffered = "notOffered",
        WrongBuild = "wrongBuild", RegisteredOther = "registeredOther", InstalledElsewhere = "installedElsewhere",
        Running = "running", Patch = "patch", ReadOnly = "readOnly", Attributes = "fileAttributes", InUse = "inUse",
        InTheWay = "inTheWay", NoRoom = "noRoom";

    /// <summary>The path as it was given.</summary>
    public string Path { get; internal set; } = "";
    /// <summary>The game folder the path names ("" when the path names none).</summary>
    public string GameDir { get; internal set; } = "";
    public string VersionId { get; internal set; } = "";
    public string Status { get; internal set; } = Checking;
    public string Reason { get; internal set; } = "";
    /// <summary>The line for the player: the refusal, or what the check found.</summary>
    public string Message { get; internal set; } = "";
    /// <summary>The version the folder holds, when its config.ini or its file list says so ("" otherwise, and when the
    /// two disagree).</summary>
    public string Found { get; internal set; } = "";
    /// <summary><see cref="Found"/> is a version Relic offers — the folder can be added as that version instead.</summary>
    public bool FoundOffered { get; internal set; }
    /// <summary>What the folder's file list says of it as <see cref="VersionId"/>: "match", "otherVersion",
    /// "unknownBuild" or "unknown" ("" before the list was read).</summary>
    public string Identity { get; internal set; } = "";
    /// <summary>The build the list names (a match).</summary>
    public string Build { get; internal set; } = "";
    /// <summary>The game_version of the folder's config.ini, major.minor ("" without one).</summary>
    public string ConfigVersion { get; internal set; } = "";
    /// <summary>The folder is already registered — as this version.</summary>
    public string RegisteredAs { get; internal set; } = "";
    /// <summary><see cref="VersionId"/> is installed at another folder: that folder.</summary>
    public string InstalledAt { get; internal set; } = "";
    /// <summary>...and that folder no longer holds the game (its drive is there, its GenshinImpact.exe is not).</summary>
    public bool InstalledAtGone { get; internal set; }
    /// <summary>The player chose this folder over <see cref="InstalledAt"/> (<see cref="ImportRequest.ReplaceInstalledAt"/>
    /// named that very folder): the registration moves to this one.</summary>
    public bool ReplaceInstalled { get; internal set; }
    /// <summary>The version's client is not known to start on this Windows 11 through Relic's launch path
    /// (<see cref="LaunchSupport.Windows11Unsupported"/>): PLAY asks before it launches it.</summary>
    public bool Windows11Unsupported { get; internal set; }
    /// <summary>Files of the player's own under Relic's replacements: they stay as they are (a deep check only).</summary>
    public List<ImportOwnFile> OwnFiles { get; internal set; } = new();
    /// <summary>What PLAY does with them: ask, keep or replace (<see cref="OwnFileChoices"/>).</summary>
    public string OwnFilesChoice { get; internal set; } = OwnFileChoices.Ask;
    /// <summary>What the import would write into the folder (a deep check only).</summary>
    public List<string> Writes { get; internal set; } = new();
    /// <summary>The room Relic's files take on the game folder's drive at their fullest (<see cref="PatchPreview.BytesNeeded"/>;
    /// an instant check counts only the writes it is sure of).</summary>
    public long BytesNeeded { get; internal set; }
    /// <summary>The bytes free on that drive for this user, when the room was asked about (-1: not asked, or the system
    /// did not say).</summary>
    public long FreeBytes { get; internal set; } = -1;
    public List<ImportNote> Notes { get; internal set; } = new();
    /// <summary>Whole files were hashed, as the import hashes them.</summary>
    public bool Deep { get; internal set; }
}

/// <summary>
/// The check of a game folder the player already has, ONE for the wizard's preview (<c>install.inspectGame</c>) and for
/// the import (<see cref="InstallService.ImportLocalAsync"/>), so the two cannot answer differently — and the import runs
/// all of it before it writes anything. In order: the version asked for (one Relic offers), the path (a folder, or the
/// path of GenshinImpact.exe), the folder's shape, its config.ini and its file list read together (a folder whose two
/// halves name different versions is refused: neither can be trusted, and no version can be offered for it), the file
/// list against the version's rows (<see cref="ClientIdentity"/>), the registrations (a folder of another version; this
/// version installed at another folder, which only the player's explicit choice of that very folder replaces), a client
/// running from it, and Relic's files — the very run of <see cref="GamePatcher.Apply"/>, made without writing
/// (<see cref="GamePatcher.Preview"/>), plus the question whether Relic may write into the folder at all
/// (<see cref="WriteAccess"/>: never elevation — a folder Windows protects gets "copy the game into a folder of your
/// own") and whether its drive has room for Relic's files. Folders are compared as the file system knows them, so
/// another name of a folder (a junction, a short name) is that folder. The notes that do not stop the import: an
/// unknown build Relic tolerates, a folder without a file list, an identity file of another size than its list says,
/// the player's own files under a replacement, the folder taking the place of another, the Windows 11 question.
/// A pkg_version without the identity file's line is accepted as it is (<see cref="ClientIdentity.Check"/>).
/// While an import runs, no check has a file of a game folder open (<see cref="HoldChecks"/>).
/// </summary>
public static class ImportCheck
{
    /// <summary>The executable every client starts from, next to its data folder.</summary>
    public const string ExeName = "GenshinImpact.exe";

    /// <summary>The data folder beside it.</summary>
    public const string DataName = "GenshinImpact_Data";

    /// <summary>The config file an official launcher writes at the client's root.</summary>
    public const string ConfigName = "config.ini";

    /// <summary>The room kept free beyond what Relic's files take: the file system's own needs, and the rounding of
    /// every file to its clusters.</summary>
    public const long RoomMargin = 16L << 20;

    // A config.ini is a few hundred bytes; only its start is read.
    private const int MaxConfigBytes = 64 * 1024;

    private static readonly Regex GameVersionLine = new(@"game_version\s*=\s*([0-9]+\.[0-9]+)", RegexOptions.CultureInvariant);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);

    /// <summary>
    /// Every check of <see cref="ImportCheck"/>, in its order; the first refusal ends it. Never writes, never throws
    /// but for <paramref name="ct"/> — and for an import that takes its turn (<see cref="HoldChecks"/>): a check that runs
    /// then is stopped with an <see cref="OperationCanceledException"/>, and one asked for while an import runs first
    /// waits for its end (<paramref name="ct"/> ends that wait too). The import's own check, made on the thread that
    /// holds the checks, does not wait.
    /// </summary>
    public static ImportVerdict Run(ImportRequest request, CancellationToken ct = default)
    {
        if (t_holds > 0) return Check(request, ct);
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
        EnterTurn(turn, ct);
        try
        {
            return Check(request, turn.Token);
        }
        finally
        {
            LeaveTurn(turn);
        }
    }

    // ── the import's turn: no check holds a file of a game folder while an import checks it and writes into it ──

    private static readonly object TurnGate = new();
    private static readonly List<CancellationTokenSource> RunningChecks = new();
    private static readonly ManualResetEventSlim NoHold = new(true);
    private static int _holds;
    [ThreadStatic] private static int t_holds;

    // How long an import waits for the checks it stopped: one held in a file the system is slow to open does not hold
    // the import up — the import's own check then says what stands in its way, and a write refused afterwards is taken
    // back with the rest.
    private const int StopWaitMs = 30_000, StopPollMs = 50;

    /// <summary>
    /// The import's hold on the checks, from its own check to its registration: every check that runs is stopped and
    /// waited for, and every check asked for while the hold lasts waits until it is released — so no check has a file of
    /// the folder open while the import checks it and writes into it (Windows renames nothing over a file that is open,
    /// and the import's own check would take the check's handle for another program's). The checks made on the thread
    /// that holds do not wait. Released by disposing it, on that same thread; <paramref name="ct"/> ends the wait for the
    /// checks to stop, and nothing is held then.
    /// </summary>
    public static IDisposable HoldChecks(CancellationToken ct = default)
    {
        List<CancellationTokenSource> running;
        lock (TurnGate)
        {
            if (_holds++ == 0) NoHold.Reset();
            running = RunningChecks.ToList();
        }
        t_holds++;
        var hold = new Hold();
        try
        {
            foreach (var check in running)
            {
                try { check.Cancel(); }
                catch (ObjectDisposedException) { } // it has ended meanwhile
            }
            long deadline = Environment.TickCount64 + StopWaitMs;
            lock (TurnGate)
            {
                while (RunningChecks.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0)
                    {
                        Log.Info($"import check: {RunningChecks.Count} check(s) did not stop within {StopWaitMs / 1000} s — the import goes on");
                        break;
                    }
                    Monitor.Wait(TurnGate, (int)Math.Min(left, StopPollMs));
                }
            }
            ct.ThrowIfCancellationRequested();
            return hold;
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    private sealed class Hold : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            t_holds--;
            lock (TurnGate)
            {
                if (--_holds == 0) NoHold.Set();
                Monitor.PulseAll(TurnGate);
            }
        }
    }

    private static void EnterTurn(CancellationTokenSource turn, CancellationToken ct)
    {
        while (true)
        {
            NoHold.Wait(ct);
            lock (TurnGate)
            {
                if (_holds > 0) continue;
                RunningChecks.Add(turn);
                return;
            }
        }
    }

    private static void LeaveTurn(CancellationTokenSource turn)
    {
        lock (TurnGate)
        {
            RunningChecks.Remove(turn);
            Monitor.PulseAll(TurnGate);
        }
    }

    private static ImportVerdict Check(ImportRequest request, CancellationToken ct)
    {
        var verdict = new ImportVerdict
        {
            Path = request.Path ?? "",
            VersionId = (request.VersionId ?? "").Trim(),
            Deep = request.Deep,
            OwnFilesChoice = OwnFileChoices.Of(request.OwnFilesChoice),
        };
        string version = verdict.VersionId;
        ImportVerdict Refuse(string reason, string message)
        {
            verdict.Status = ImportVerdict.Refused;
            verdict.Reason = reason;
            verdict.Message = message;
            return verdict;
        }

        // ── the version asked for ──
        if (version.Length == 0)
            return Refuse(ImportVerdict.NoVersion, L.T("core.install.import.noVersion"));
        var offered = request.Offered ?? LoadOffered();
        bool IsOffered(string id) => offered.Contains(id, StringComparer.OrdinalIgnoreCase);
        // A catalogue that could not be read names nothing to judge by; the import itself is only ever asked about a
        // version the catalogue lists.
        if (offered.Count > 0 && !IsOffered(version))
            return Refuse(ImportVerdict.UnknownVersion, L.T("core.install.import.unknownVersion", new { version }));

        // ── the path ──
        string given = (request.Path ?? "").Trim().Trim('"').Trim();
        if (given.Length == 0)
            return Refuse(ImportVerdict.NoPath, L.T("core.install.import.noPath"));
        if (!System.IO.Path.IsPathFullyQualified(given))
            return Refuse(ImportVerdict.NotFullPath, L.T("core.install.import.notFullPath", new { version }));
        string full;
        try { full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(given)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refuse(ImportVerdict.FolderMissing, L.T("core.install.import.folderMissing", new { dir = given }));
        }
        string gameDir = full;
        if (!Directory.Exists(full))
        {
            if (string.Equals(System.IO.Path.GetFileName(full), ExeName, StringComparison.OrdinalIgnoreCase))
                gameDir = System.IO.Path.GetDirectoryName(full) ?? full; // the path of GenshinImpact.exe: its folder
            else if (File.Exists(full))
                return Refuse(ImportVerdict.NotGameExe, L.T("core.install.import.notGameExe", new { file = System.IO.Path.GetFileName(full) }));
        }
        gameDir = System.IO.Path.TrimEndingDirectorySeparator(gameDir);
        verdict.GameDir = gameDir;

        // ── the folder ──
        if (!Directory.Exists(gameDir))
            return Refuse(ImportVerdict.FolderMissing, L.T("core.install.import.folderMissing", new { dir = gameDir }));
        if (!File.Exists(System.IO.Path.Combine(gameDir, ExeName)))
            return Refuse(ImportVerdict.NoExe, L.T("core.install.import.noExe"));
        if (!Directory.Exists(System.IO.Path.Combine(gameDir, DataName)))
            return Refuse(ImportVerdict.NoData, L.T("core.install.import.noData"));
        ct.ThrowIfCancellationRequested();

        // ── what it holds: its config.ini and its file list, read together ──
        string? config = ReadConfigVersion(System.IO.Path.Combine(gameDir, ConfigName));
        verdict.ConfigVersion = config ?? "";
        var listed = ClientIdentity.Identify(gameDir, request.PayloadRoot);
        // A folder whose two halves name different versions: neither can be trusted, and no version could be offered for
        // it that the other half would not refuse.
        if (config is not null && listed.Kind == ClientIdentityKind.Match
            && !string.Equals(config, listed.Version, StringComparison.OrdinalIgnoreCase))
            return Refuse(ImportVerdict.Contradiction, L.T("core.install.import.contradiction", new { config, found = listed.Version }));
        // Launcher-made folders carry config.ini with the game_version: one that names another version would make a
        // client that cannot talk to its server. A folder without the file is judged by its list alone.
        if (config is not null)
        {
            verdict.Found = config;
            verdict.FoundOffered = IsOffered(config);
            if (!string.Equals(config, version, StringComparison.Ordinal))
                return verdict.FoundOffered
                    ? Refuse(ImportVerdict.OtherVersion, L.T("core.install.import.otherVersion", new { found = config, version }))
                    : Refuse(ImportVerdict.NotOffered, L.T("core.install.import.notOffered", new { found = config, version }));
        }

        var identity = ClientIdentity.Check(version, gameDir, request.PayloadRoot);
        verdict.Identity = identity.Kind switch
        {
            ClientIdentityKind.Match => "match",
            ClientIdentityKind.OtherVersion => "otherVersion",
            ClientIdentityKind.UnknownBuild => "unknownBuild",
            _ => "unknown",
        };
        verdict.Build = identity.Kind == ClientIdentityKind.Match ? identity.Build : "";
        if (identity.Kind == ClientIdentityKind.OtherVersion)
        {
            verdict.Found = identity.Found;
            verdict.FoundOffered = IsOffered(identity.Found);
            return verdict.FoundOffered
                ? Refuse(ImportVerdict.OtherVersion, L.T("core.install.import.otherVersion", new { found = identity.Found, version }))
                : Refuse(ImportVerdict.NotOffered, L.T("core.install.import.notOffered", new { found = identity.Found, version }));
        }
        if (identity.Kind == ClientIdentityKind.Match)
        {
            verdict.Found = version;
            verdict.FoundOffered = IsOffered(version);
        }
        if (ClientIdentity.ImportRefusal(version, identity) is { } refusal)
            return Refuse(ImportVerdict.WrongBuild, refusal);

        // ── the registrations: one folder = one registration, one version = one folder ──
        var clash = request.Installed.FirstOrDefault(i =>
            !string.Equals(i.Id, version, StringComparison.OrdinalIgnoreCase) && SameDir(i.GameDir, gameDir));
        if (clash is not null)
        {
            verdict.RegisteredAs = clash.Id;
            return Refuse(ImportVerdict.RegisteredOther, L.T("core.install.import.alreadyRegistered", new { version = clash.Id }));
        }
        var mine = request.Installed.FirstOrDefault(i => string.Equals(i.Id, version, StringComparison.OrdinalIgnoreCase));
        if (mine is not null && SameDir(mine.GameDir, gameDir))
        {
            verdict.RegisteredAs = mine.Id;
            Note(verdict, "registered", L.T("core.install.inspect.registered", new { version }));
        }
        else if (mine is not null)
        {
            verdict.InstalledAt = mine.GameDir;
            verdict.InstalledAtGone = HoldsNoGame(mine.GameDir);
            // Copying a version's folder is how a player keeps a second copy: taking the new one in its place moves
            // the registration — the profile slot and the shortcut follow the version — and is done only when the
            // player chose this folder over that very one.
            verdict.ReplaceInstalled = !string.IsNullOrWhiteSpace(request.ReplaceInstalledAt)
                                       && SameDir(request.ReplaceInstalledAt!, mine.GameDir);
            if (!verdict.ReplaceInstalled)
                return Refuse(ImportVerdict.InstalledElsewhere, L.T("core.install.import.installedElsewhere", new { version, dir = mine.GameDir }));
            Note(verdict, "replacing", L.T("core.install.inspect.replacing", new { version, dir = mine.GameDir }));
        }

        // ── a client running from it: its files are held, and swapping profiles under it is never done ──
        var running = request.RunningClients is null ? RunningClientPaths() : request.RunningClients();
        if (running.Any(exe => SameDir(System.IO.Path.GetDirectoryName(exe) ?? "", gameDir)))
            return Refuse(ImportVerdict.Running, L.T("core.install.import.gameRunning"));
        ct.ThrowIfCancellationRequested();

        // ── Relic's files: Apply's own run, made without writing ──
        var preview = GamePatcher.Preview(version, gameDir, request.PayloadRoot, request.WithInjectables,
            replaceOwn: false, deep: request.Deep, undoable: true, ct: ct);
        if (preview.Refusal is not null)
            return Refuse(ImportVerdict.Patch, preview.Refusal);
        // A folder Relic cannot write is refused before anything else is tried in it: Relic keeps its files there and
        // repairs them before every launch, and it never asks for administrator rights to do so.
        if (preview.HasPlan && WriteAccess.CanCreateIn(gameDir, folders: true) is not null)
            return Refuse(ImportVerdict.ReadOnly, L.T("core.install.import.readOnly", new { dir = gameDir }));
        if (preview.Report.Blocked.FirstOrDefault() is { } blocked)
            return RefuseBlocked(Refuse, blocked.Dst, blocked.Problem);
        verdict.BytesNeeded = preview.BytesNeeded;
        if (preview.BytesNeeded > 0)
        {
            long? free = request.FreeSpace is null ? FreeSpaceAt(gameDir) : request.FreeSpace(gameDir);
            verdict.FreeBytes = free ?? -1;
            if (free is long room && room < preview.BytesNeeded + RoomMargin)
                return Refuse(ImportVerdict.NoRoom, L.T("core.install.import.noRoom", new
                {
                    drive = System.IO.Path.GetPathRoot(gameDir) ?? gameDir,
                    need = (preview.BytesNeeded + RoomMargin + (1L << 20) - 1) >> 20,
                    free = room >> 20,
                }));
        }
        verdict.Writes = request.Deep ? preview.Report.Applied.ToList() : new List<string>();

        // ── the notes ──
        if (identity.Kind == ClientIdentityKind.UnknownBuild)
            Note(verdict, "unknownBuild", L.T("core.install.inspect.unknownBuild", new { version }));
        else if (identity.Kind == ClientIdentityKind.Unknown)
            Note(verdict, "noList", L.T("core.install.inspect.noList", new { version }));
        else if (identity.Kind == ClientIdentityKind.Match && listed.Kind == ClientIdentityKind.Match && !listed.SizeProven)
            Note(verdict, "identityFile", L.T("core.install.inspect.identityFile", new { file = listed.File }));
        verdict.Windows11Unsupported = request.IsWindows11 && LaunchSupport.Windows11Unsupported(version, request.PayloadRoot);
        if (request.Deep)
        {
            var own = GamePatcher.OwnFiles(version, gameDir, request.PayloadRoot, request.WithInjectables);
            verdict.OwnFiles = own.Select(r => new ImportOwnFile(r.Dst, r.Kept)).ToList();
            if (own.Count > 0)
            {
                var files = new { files = string.Join(", ", own.Select(r => r.Dst)) };
                Note(verdict, "ownFiles", verdict.OwnFilesChoice switch
                {
                    OwnFileChoices.Keep => L.T("core.install.inspect.ownKeep", files),
                    OwnFileChoices.Replace => L.T("core.install.inspect.ownReplace", files),
                    _ => L.T("core.install.inspect.ownAsk", files),
                });
            }
        }

        if (!request.Deep)
        {
            verdict.Status = ImportVerdict.Checking;
            verdict.Message = L.T("core.install.inspect.checking");
            return verdict;
        }
        verdict.Status = ImportVerdict.Ready;
        verdict.Message = verdict.Build.Length > 0
            ? L.T("core.install.inspect.readyBuild", new { version, build = verdict.Build })
            : L.T("core.install.inspect.ready", new { version });
        return verdict;
    }

    /// <summary>The refusal of a write of Relic's that cannot be made in the folder: <paramref name="file"/> is the
    /// entry's place, as the manifest names it.</summary>
    private static ImportVerdict RefuseBlocked(Func<string, string, ImportVerdict> refuse, string file, string problem) => problem switch
    {
        WriteAccess.InUse => refuse(ImportVerdict.InUse, L.T("core.install.import.inUse", new { file })),
        WriteAccess.Attributes => refuse(ImportVerdict.Attributes, L.T("core.install.import.fileAttributes", new { file })),
        WriteAccess.InTheWay => refuse(ImportVerdict.InTheWay, L.T("core.install.import.inTheWay", new { file })),
        _ => refuse(ImportVerdict.ReadOnly, L.T("core.install.import.fileDenied", new { file })),
    };

    private static void Note(ImportVerdict verdict, string code, string text) => verdict.Notes.Add(new ImportNote(code, text));

    /// <summary>The game_version (major.minor) of a config.ini, or null when the file is missing, unreadable or names
    /// none. Only the first 64 KiB are read.</summary>
    public static string? ReadConfigVersion(string configIni)
    {
        try
        {
            var info = new FileInfo(configIni);
            if (!info.Exists) return null;
            using var fs = new FileStream(configIni, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] buffer = new byte[(int)Math.Min(info.Length, MaxConfigBytes)];
            int read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            using var reader = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var m = GameVersionLine.Match(reader.ReadToEnd());
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The two paths name the same folder: the same full path (without letter case, without a trailing
    /// separator), or — for two folders on this PC's own drives — the same folder as the file system knows it, so that
    /// a junction, a short name or a drive letter made of a folder is the folder it names. A path that cannot be
    /// resolved is compared as it is; a network path is never opened.</summary>
    public static bool SameDir(string a, string b)
    {
        string fa = Full(a), fb = Full(b);
        if (string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase)) return true;
        return FolderIdentity.Of(fa) is { } ia && FolderIdentity.Of(fb) is { } ib && ia == ib;
    }

    private static string Full(string path)
    {
        try { return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); }
        catch (Exception) { return path ?? ""; }
    }

    /// <summary>The bytes this user may still write on the drive of <paramref name="dir"/> — a disk quota counts — or
    /// null when the system does not say.</summary>
    public static long? FreeSpaceAt(string dir)
    {
        try
        {
            return GetDiskFreeSpaceEx(dir, out ulong available, out _, out _)
                ? (long)Math.Min(available, (ulong)long.MaxValue)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A registered folder that no longer holds the game: its drive is there and its GenshinImpact.exe is
    /// not (an unplugged disk is not "gone").</summary>
    private static bool HoldsNoGame(string dir)
    {
        try
        {
            string root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(dir)) ?? "";
            return root.Length > 0 && Directory.Exists(root) && !File.Exists(System.IO.Path.Combine(dir, ExeName));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> RunningClientPaths()
    {
        try { return ProcessGuard.RunningGenshin().Select(g => g.Path).Where(p => p.Length > 0).ToList(); }
        catch (Exception ex)
        {
            Log.Error("import check: the running clients could not be listed", ex);
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyCollection<string> LoadOffered()
    {
        try { return VersionCatalog.Load().Select(v => v.Id).ToList(); }
        catch (Exception ex)
        {
            Log.Error("import check: the catalogue could not be read", ex);
            return Array.Empty<string>();
        }
    }
}

/// <summary>Which folder a path names, as the file system knows it — its volume and its own id — so that two names of
/// one folder are told to be the same. Asked only of a path on one of this PC's own drives (fixed, removable, optical
/// or RAM): a network path is never opened. Null when it cannot be told.</summary>
internal static class FolderIdentity
{
    private const uint FileReadAttributes = 0x80, ShareAll = 0x7, OpenExisting = 3, FlagBackupSemantics = 0x02000000;
    private const int FileIdInfoClass = 18;
    private const uint DriveRemovable = 2, DriveFixed = 3, DriveCdRom = 5, DriveRamDisk = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong IdLow, IdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileIdInfo info, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
    private static extern uint GetDriveType(string root);

    public static (ulong Volume, ulong High, ulong Low)? Of(string path)
    {
        try
        {
            string root = Path.GetPathRoot(path) ?? "";
            if (root.Length < 3 || root[1] != ':') return null;
            if (GetDriveType(root) is not (DriveRemovable or DriveFixed or DriveCdRom or DriveRamDisk)) return null;
            using var handle = CreateFile(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, FlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            return GetFileInformationByHandleEx(handle, FileIdInfoClass, out var info, Marshal.SizeOf<FileIdInfo>())
                ? (info.VolumeSerialNumber, info.IdHigh, info.IdLow)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Whether this user can make a write of the import's or of <see cref="GamePatcher"/>'s, asked without making it: the
/// folder or the file is opened with exactly the right that write needs (CreateFileW, OPEN_EXISTING) and closed again —
/// nothing is created, changed or deleted. A folder Relic cannot write is never answered with a request for
/// administrator rights (Relic never elevates): the answer is the reason the import is refused. An error that says
/// neither "denied" nor "in use" is no answer, and no reason to refuse — the write itself then tells.
/// </summary>
public static class WriteAccess
{
    /// <summary>The folder or the file is not this user's to write (Program Files, a protected or read-only medium).</summary>
    public const string Denied = "denied";
    /// <summary>A read-only, hidden or system file where a file would be copied over it in place, which Windows refuses.</summary>
    public const string Attributes = "attributes";
    /// <summary>Another program holds the file.</summary>
    public const string InUse = "inUse";
    /// <summary>Something of another kind stands at a name a write needs: a folder where a file goes, or a file where a
    /// folder of its path goes.</summary>
    public const string InTheWay = "inTheWay";

    private const uint FileAddFile = 0x0002, FileAddSubdirectory = 0x0004;
    private const uint FileWriteData = 0x0002, FileWriteAttributes = 0x0100, DeleteAccess = 0x00010000;
    private const uint ShareNone = 0, ShareAll = 0x7, OpenExisting = 3, FlagBackupSemantics = 0x02000000;
    private const int ErrorAccessDenied = 5, ErrorWriteProtect = 19, ErrorSharingViolation = 32, ErrorLockViolation = 33,
        ErrorUserMappedFile = 1224;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <summary>
    /// Can files be created in <paramref name="dir"/> — and, with <paramref name="folders"/>, folders too? Where the
    /// folder is still missing, the nearest folder of the path that exists is asked whether a folder may be made in it.
    /// Null: yes, or the system gave no answer.
    /// </summary>
    public static string? CanCreateIn(string dir, bool folders = false)
    {
        try
        {
            string? existing = dir;
            while (existing is not null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
            if (existing is null) return null;
            bool missing = !string.Equals(Path.TrimEndingDirectorySeparator(existing),
                Path.TrimEndingDirectorySeparator(dir), StringComparison.OrdinalIgnoreCase);
            uint access = missing ? FileAddSubdirectory : FileAddFile | (folders ? FileAddSubdirectory : 0);
            return Ask(existing, access, ShareAll, directory: true);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Can another file be copied over <paramref name="file"/> in place (File.Copy with overwrite)? Windows
    /// refuses that over a read-only, a hidden or a system file, and while a program holds the file without letting
    /// others write to it.</summary>
    public static string? CanOverwrite(string file)
    {
        try
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0) return Attributes;
            return Ask(file, FileWriteData, ShareAll, directory: false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Can another file be renamed over <paramref name="file"/> (File.Move with overwrite), its read-only
    /// attribute cleared first? Windows refuses that while any program holds the file open, whatever that program
    /// shares, and while the system has it mapped as a program's code (a DLL in use): the first answers an exclusive open
    /// for deleting with a sharing violation, the second an open for writing. The right to delete may come from the file
    /// or from its folder — an open for deleting counts the folder's right to delete what it holds.</summary>
    public static string? CanReplace(string file)
    {
        try
        {
            bool readOnly = (File.GetAttributes(file) & FileAttributes.ReadOnly) != 0;
            string? answer = Ask(file, DeleteAccess | (readOnly ? FileWriteAttributes : 0), ShareNone, directory: false);
            if (answer is not null) return answer;
            // Mapped as a program's code: only an open for writing shows it (one the read-only attribute or the file's
            // own rights refuse says nothing here).
            return !readOnly && Ask(file, FileWriteData, ShareAll, directory: false) == InUse ? InUse : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Can <paramref name="file"/> — a copy a cut run left at a name the run makes again — be removed, its
    /// attributes cleared first? Windows does not let the name be made again while any program holds the file.</summary>
    public static string? CanRemove(string file)
    {
        try
        {
            return Ask(file, DeleteAccess | FileWriteAttributes, ShareNone, directory: false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Ask(string path, uint access, uint share, bool directory)
    {
        using var handle = CreateFile(path, access, share, IntPtr.Zero, OpenExisting,
            directory ? FlagBackupSemantics : 0, IntPtr.Zero);
        if (!handle.IsInvalid) return null;
        return Marshal.GetLastWin32Error() switch
        {
            ErrorAccessDenied or ErrorWriteProtect => Denied,
            ErrorSharingViolation or ErrorLockViolation or ErrorUserMappedFile => InUse,
            _ => null,
        };
    }
}
