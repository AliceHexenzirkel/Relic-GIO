using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Relic.Core.Install;
using Relic.Core.State;
using Relic.Core.Util;

/// <summary>
/// The import of a game folder the player already has, and "Find it on this PC":
///  - the one check of the preview and of the import (ImportCheck): the version asked for, the path (a folder, the path
///    of GenshinImpact.exe, quoted, relative, another file), the folder's shape, config.ini and the file list read
///    together (a folder that contradicts itself), the file list against the version's rows, the registrations (a folder
///    of another version, this version installed at another folder — only the explicit choice of that very folder
///    replaces it), another name of a folder (a junction) being that folder, a client running from it, a folder or a file
///    Relic cannot write (denied, read-only / hidden / system, held open in any way, mapped as a DLL, something of another
///    kind in the way), the room on the drive, the notes;
///  - GamePatcher.Preview: Apply's own run made without writing — the same report and the same refusal as Apply, on
///    plain copies, replacements and byte patches — a byte patch "ready" only after the whole hash, and the instant check
///    refusing only what the deep one refuses too;
///  - the import (InstallService.ImportFolder, a temp state file, no Fiddler, no shortcut): a refused import writes
///    nothing; a failure the check could not foresee (made between the check and the first write: a file given the
///    system attribute, a DLL mapped, a folder made unwritable, a copy that stops halfway) is taken back whole — new
///    files, folders, backups, files written over (their attributes too), a byte patch's bytes, the registration; an
///    optional entry that fails halfway leaves its place as it was; an undo that cannot put a file back keeps the copy
///    that holds its bytes; the import's order; the explicit choice moves the registration; the same folder keeps its
///    origin;
///  - cancellation of a check, and the import's turn: a check that runs when an import starts is stopped and waited for,
///    one asked for meanwhile waits for the import's end;
///  - the search (GameSearch): its pure rules (prunes, order, Windows' record of programs started read from a synthetic
///    source, the keys it never opens), a walk of a synthetic tree (clients found and classified, pruned folders, a
///    junction loop, a reparse point that is no link, a file list that is not on this PC, the depth limit and its
///    boundary, a deeper search that goes on from it, the sure places first — reached only as the walk would reach them,
///    a network path never touched —, best-first, every event naming its search), Stop mid-walk and a deeper search
///    after it, a worker held in a folder left behind at Stop, a time budget that hands back what it did not walk;
///  - the texts (en.json or a fragment beside it) and their placeholders.
/// Everything it writes is under one temp folder; the real state file and the registry are never written.
/// </summary>
static class ImportSpike
{
    public const string Name = "import";

    const string V6 = "6.6", V7 = "7.7", V8 = "8.8";
    const string UaDst = "GenshinImpact_Data/Native/UserAssembly.dll";
    const string SecondDst = "GenshinImpact_Data/Native/Second.dll";
    const string OwnDst = "extra/own.bin";
    const string RpDst = "mhypbase.dll";
    const string Md5A = "0123456789abcdef0123456789abcdef"; // 7.7.0, 1000 bytes
    const string Md5B = "fedcba9876543210fedcba9876543210"; // 6.6.0, 1500 bytes
    const string Md5C = "00112233445566778899aabbccddeeff"; // 8.8.0, 2000 bytes, exact
    const string Md5X = "ffffffffffffffffffffffffffffffff"; // no row
    const int BpSize = 2000, BpOffset = 700, BpLength = 100;
    const int SecondSize = 3000, SecondOffset = 100, SecondLength = 50;
    const string RpOriginal = "ORIGINAL-MHYPBASE", RpRelic = "RELIC-MHYPBASE", RpMine = "MY-OWN-MHYPBASE";
    const string Launcher = "ayy/anime/build/launcher.exe", Mhynot = "ayy/anime/build/mhynot2.dll";

    static readonly string[] Offered = { V6, V7, V8 };

    /// <summary>The texts the check and the search use, with the placeholders each one takes.</summary>
    static readonly Dictionary<string, string[]> TextKeys = new()
    {
        ["core.install.import.noVersion"] = Array.Empty<string>(),
        ["core.install.import.unknownVersion"] = new[] { "version" },
        ["core.install.import.noPath"] = Array.Empty<string>(),
        ["core.install.import.notFullPath"] = new[] { "version" },
        ["core.install.import.notGameExe"] = new[] { "file" },
        ["core.install.import.folderMissing"] = new[] { "dir" },
        ["core.install.import.noExe"] = Array.Empty<string>(),
        ["core.install.import.noData"] = Array.Empty<string>(),
        ["core.install.import.contradiction"] = new[] { "config", "found" },
        ["core.install.import.otherVersion"] = new[] { "found", "version" },
        ["core.install.import.notOffered"] = new[] { "found", "version" },
        ["core.install.import.alreadyRegistered"] = new[] { "version" },
        ["core.install.import.installedElsewhere"] = new[] { "version", "dir" },
        ["core.install.import.gameRunning"] = Array.Empty<string>(),
        ["core.install.import.readOnly"] = new[] { "dir" },
        ["core.install.import.fileDenied"] = new[] { "file" },
        ["core.install.import.fileAttributes"] = new[] { "file" },
        ["core.install.import.inUse"] = new[] { "file" },
        ["core.install.import.inTheWay"] = new[] { "file" },
        ["core.install.import.noRoom"] = new[] { "drive", "need", "free" },
        ["core.install.inspect.checking"] = Array.Empty<string>(),
        ["core.install.inspect.ready"] = new[] { "version" },
        ["core.install.inspect.readyBuild"] = new[] { "version", "build" },
        ["core.install.inspect.registered"] = new[] { "version" },
        ["core.install.inspect.replacing"] = new[] { "version", "dir" },
        ["core.install.inspect.unknownBuild"] = new[] { "version" },
        ["core.install.inspect.noList"] = new[] { "version" },
        ["core.install.inspect.identityFile"] = new[] { "file" },
        ["core.install.inspect.ownAsk"] = new[] { "files" },
        ["core.install.inspect.ownKeep"] = new[] { "files" },
        ["core.install.inspect.ownReplace"] = new[] { "files" },
        ["core.find.note.registered"] = new[] { "version" },
        ["core.find.note.installedAt"] = new[] { "version", "dir" },
        ["core.find.note.notOffered"] = new[] { "version" },
        ["core.find.note.contradiction"] = new[] { "config", "version" },
        ["core.find.note.unknown"] = Array.Empty<string>(),
        ["core.find.note.configOnly"] = new[] { "version" },
    };

    /// <param name="tempRoot">The folder the spike's own folder goes in (the system's temp folder when null).</param>
    /// <param name="repoRoot">The repository, for the texts (found above the working folder when null).</param>
    public static int Run(string? tempRoot = null, string? repoRoot = null)
    {
        Console.WriteLine($"== {Name} spike: the import check, the preview, the import, and the search of this PC (synthetic folders) ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            lock (results) results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        string tmp = Path.GetFullPath(Path.Combine(string.IsNullOrWhiteSpace(tempRoot) ? Path.GetTempPath() : tempRoot, "relic-import-spike"));
        Clean(tmp);
        Directory.CreateDirectory(tmp);
        string realState = RelicState.DefaultPath;
        DateTime? realStateWritten = File.Exists(realState) ? File.GetLastWriteTimeUtc(realState) : null;
        try
        {
            var fx = Fixture.Create(Path.Combine(tmp, "payload"));
            Paths(tmp, fx, Check);
            Identities(tmp, fx, Check);
            Registrations(tmp, fx, Check);
            Writability(tmp, fx, Check);
            HeldAndMapped(tmp, fx, Check);
            PreviewParity(tmp, fx, Check);
            BytePatchPreview(tmp, fx, Check);
            Room(tmp, fx, Check);
            Importing(tmp, fx, Check);
            Undoing(tmp, fx, Check);
            Cancelling(tmp, fx, Check);
            Turns(tmp, fx, Check);
            SearchRules(Check);
            SearchWalk(tmp, fx, Check);
            SearchStop(tmp, Check);
            SearchHeldWorker(tmp, Check);
            SearchBudget(tmp, fx, Check);
            Texts(repoRoot, Check);
        }
        catch (Exception ex)
        {
            Check($"the spike ran to its end (it threw {ex.GetType().Name}: {ex.Message})", false);
        }
        finally
        {
            Check("the real state file was not written",
                (File.Exists(realState) ? File.GetLastWriteTimeUtc(realState) : (DateTime?)null) == realStateWritten);
            try { Clean(tmp); }
            catch (Exception ex) { Console.WriteLine($"  (the temp folder {tmp} could not be removed: {ex.Message})"); }
        }
        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"{Name} spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    // ── fixtures ──

    /// <summary>What a payload root of the fixture holds besides its usual entries.</summary>
    sealed record PayloadOptions
    {
        /// <summary>The 7.7 payload file is there.</summary>
        public bool OwnSrc { get; init; } = true;
        /// <summary>The launcher of the injector pair is there.</summary>
        public bool Launcher { get; init; } = true;
        /// <summary>What the 7.7 payload file holds (its entry names the hash of what it was written with).</summary>
        public string? OwnContent { get; init; }
        /// <summary>The 7.7 payload file is this many bytes (a hash that takes a while).</summary>
        public int OwnBytes { get; init; }
        /// <summary>The 7.7 entry keeps a .relic-orig copy.</summary>
        public bool OwnBackup { get; init; }
        /// <summary>8.8 has a second required byte patch, of Second.dll.</summary>
        public bool SecondPatch { get; init; }
        /// <summary>The body of the manifest's "launch" object.</summary>
        public string Launch { get; init; } = "";
        public Func<string, string>? Tweak { get; init; }
    }

    sealed class Fixture
    {
        public string Payload = "";
        public byte[] UaOriginal = Array.Empty<byte>();
        public byte[] UaPatched = Array.Empty<byte>();
        public byte[] Span = Array.Empty<byte>();
        public byte[] SecondOriginal = Array.Empty<byte>();
        public byte[] SecondPatched = Array.Empty<byte>();
        public byte[] SecondSpan = Array.Empty<byte>();

        public static Fixture Create(string payload)
        {
            var fx = new Fixture { Payload = payload };
            fx.UaOriginal = Pattern(BpSize, 31, 7);
            fx.Span = new byte[BpLength];
            for (int i = 0; i < BpLength; i++) fx.Span[i] = (byte)(fx.UaOriginal[BpOffset + i] ^ 0x5A);
            fx.UaPatched = (byte[])fx.UaOriginal.Clone();
            fx.Span.CopyTo(fx.UaPatched, BpOffset);
            fx.SecondOriginal = Pattern(SecondSize, 17, 3);
            fx.SecondSpan = new byte[SecondLength];
            for (int i = 0; i < SecondLength; i++) fx.SecondSpan[i] = (byte)(fx.SecondOriginal[SecondOffset + i] ^ 0x33);
            fx.SecondPatched = (byte[])fx.SecondOriginal.Clone();
            fx.SecondSpan.CopyTo(fx.SecondPatched, SecondOffset);
            WriteManifest(fx, payload, new PayloadOptions());
            return fx;
        }

        /// <summary>A payload root: the injector pair (common), 7.7's required plain copy, 8.8's byte patch of the
        /// identity file and its replacement of mhypbase.dll — and what <paramref name="o"/> adds.</summary>
        public static string WriteManifest(Fixture fx, string payload, PayloadOptions o)
        {
            string launcher = Write(payload, "common/ayy/anime/build/launcher.exe.bin", "INJECTOR-STUB");
            string mhynot = Write(payload, "common/ayy/anime/build/mhynot2.dll.bin", "MHYNOT2-STUB");
            string own = Write(payload, "7.7/own.bin", "OWN-7.7");
            if (o.OwnBytes > 0) File.WriteAllBytes(own, Pattern(o.OwnBytes, 13, 5));
            string shaOwn = Sha(own);
            if (o.OwnContent is not null) File.WriteAllText(own, o.OwnContent);
            if (!o.OwnSrc) File.Delete(own);
            string launcherSha = Sha(launcher);
            if (!o.Launcher) File.Delete(launcher);
            string span = WriteBytes(payload, "8.8/UserAssembly.span.bin", fx.Span);
            string span2 = WriteBytes(payload, "8.8/Second.span.bin", fx.SecondSpan);
            string rp = Write(payload, "8.8/mhypbase.dll.bin", RpRelic);
            string second = !o.SecondPatch ? "" : $$"""
                  ,{ "kind": "bytepatch", "src": "8.8/Second.span.bin", "dst": "{{SecondDst}}",
                    "size": {{SecondSize}}, "offset": {{SecondOffset}}, "length": {{SecondLength}},
                    "sha256Before": "{{ShaOf(fx.SecondOriginal)}}", "sha256": "{{ShaOf(fx.SecondPatched)}}",
                    "spanSha256Before": "{{ShaOf(fx.SecondOriginal.AsSpan(SecondOffset, SecondLength).ToArray())}}", "spanSha256": "{{Sha(span2)}}",
                    "required": true }
                """;
            string json = $$"""
            {
              "launch": { {{o.Launch}} },
              "clients": [
                { "version": "{{V7}}", "build": "7.7.0", "file": "{{UaDst}}", "md5": "{{Md5A}}", "size": 1000 },
                { "version": "{{V6}}", "build": "6.6.0", "file": "{{UaDst}}", "md5": "{{Md5B}}", "size": 1500 },
                { "version": "{{V8}}", "build": "8.8.0", "file": "{{UaDst}}", "md5": "{{Md5C}}", "size": {{BpSize}}, "exact": true }
              ],
              "common": [
                { "src": "common/ayy/anime/build/launcher.exe.bin", "dst": "{{Launcher}}", "sha256": "{{launcherSha}}" },
                { "src": "common/ayy/anime/build/mhynot2.dll.bin", "dst": "{{Mhynot}}", "sha256": "{{Sha(mhynot)}}" }
              ],
              "versions": {
                "{{V7}}": [ { "src": "7.7/own.bin", "dst": "{{OwnDst}}", "sha256": "{{shaOwn}}", "required": true{{(o.OwnBackup ? ", \"backup\": true" : "")}} } ],
                "{{V8}}": [
                  { "kind": "bytepatch", "src": "8.8/UserAssembly.span.bin", "dst": "{{UaDst}}",
                    "size": {{BpSize}}, "offset": {{BpOffset}}, "length": {{BpLength}},
                    "sha256Before": "{{ShaOf(fx.UaOriginal)}}", "sha256": "{{ShaOf(fx.UaPatched)}}",
                    "spanSha256Before": "{{ShaOf(fx.UaOriginal.AsSpan(BpOffset, BpLength).ToArray())}}", "spanSha256": "{{Sha(span)}}",
                    "required": true },
                  { "src": "8.8/mhypbase.dll.bin", "dst": "{{RpDst}}", "sha256": "{{Sha(rp)}}",
                    "sha256Before": "{{ShaOf(Encoding.UTF8.GetBytes(RpOriginal))}}", "backup": true, "required": true }
                  {{second}}
                ]
              }
            }
            """;
            File.WriteAllText(Path.Combine(payload, "manifest.json"), o.Tweak is null ? json : o.Tweak(json));
            return payload;
        }
    }

    static byte[] Pattern(int length, int mul, int add)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * mul + add);
        return bytes;
    }

    static string Line(string name, string md5, long size) =>
        $"{{\"remoteName\": \"{name}\", \"md5\": \"{md5}\", \"fileSize\": {size}}}";

    /// <summary>A client folder: GenshinImpact.exe, GenshinImpact_Data, a pkg_version whose identity line names
    /// <paramref name="list"/> ("7.7", "6.6", "8.8", "x" = a build no row names, "noline" = no identity line, null = no
    /// list), its identity file (the listed size unless <paramref name="ua"/> says otherwise), and optionally config.ini,
    /// mhypbase.dll and the original Second.dll.</summary>
    static string Game(string root, string name, string? list, string? config = null, byte[]? ua = null,
        string? mhypbase = null, bool exe = true, bool data = true, Fixture? fx = null, bool second = false)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        if (exe) Write(dir, "GenshinImpact.exe", "not really an exe");
        if (data) Directory.CreateDirectory(Path.Combine(dir, "GenshinImpact_Data"));
        if (list is not null)
        {
            var lines = new List<string> { Line("GenshinImpact_Data/Managed/Metadata/global-metadata.dat", Md5X, 5) };
            (string md5, long size)? identity = list switch
            {
                V7 => (Md5A, 1000), V6 => (Md5B, 1500), V8 => (Md5C, BpSize), "x" => (Md5X, 1000), _ => null,
            };
            if (identity is { } id) lines.Add(Line(UaDst, id.md5, id.size));
            lines.Add(Line("GenshinImpact.exe", Md5X, 17));
            File.WriteAllText(Path.Combine(dir, ClientIdentity.ListFile), string.Join("\r\n", lines) + "\r\n");
            byte[]? content = ua ?? list switch
            {
                V7 or "x" => new byte[1000], V6 => new byte[1500], V8 => (fx ?? throw new ArgumentNullException(nameof(fx))).UaOriginal,
                _ => null,
            };
            if (content is not null && data) WriteBytes(dir, UaDst, content);
        }
        else if (ua is not null && data) WriteBytes(dir, UaDst, ua);
        if (config is not null) File.WriteAllText(Path.Combine(dir, "config.ini"), $"[General]\r\nchannel=1\r\ngame_version={config}.0\r\n");
        if (mhypbase is not null) Write(dir, RpDst, mhypbase);
        if (second) WriteBytes(dir, SecondDst, (fx ?? throw new ArgumentNullException(nameof(fx))).SecondOriginal);
        return dir;
    }

    static ImportRequest Req(string versionId, string path, Fixture fx, bool deep = true,
        IReadOnlyList<ImportRegistration>? installed = null, string? replaceAt = null, IReadOnlyList<string>? running = null,
        string ownChoice = OwnFileChoices.Ask, string? payload = null) => new()
    {
        VersionId = versionId,
        Path = path,
        Deep = deep,
        ReplaceInstalledAt = replaceAt,
        Installed = installed ?? Array.Empty<ImportRegistration>(),
        Offered = Offered,
        PayloadRoot = payload ?? fx.Payload,
        WithInjectables = true,
        OwnFilesChoice = ownChoice,
        IsWindows11 = false,
        RunningClients = () => running ?? Array.Empty<string>(),
        FreeSpace = _ => long.MaxValue,
    };

    static string P(string dir, string relative) => Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));

    // ── A. the version and the path ──

    static void Paths(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "paths");
        string game = Game(root, "Genshin 7.7", V7);
        string exe = Path.Combine(game, "GenshinImpact.exe");
        var byFolder = ImportCheck.Run(Req(V7, game, fx));
        Check("path: the folder of a client of the asked version -> ready, the folder named, the build named, the files the import writes listed, the room they take counted",
            byFolder.Status == ImportVerdict.Ready && byFolder.GameDir == game && byFolder.Build == "7.7.0" && byFolder.Identity == "match"
            && byFolder.Found == V7 && byFolder.Deep && byFolder.Notes.Count == 0
            && byFolder.Message == L.T("core.install.inspect.readyBuild", new { version = V7, build = "7.7.0" })
            && byFolder.Writes.OrderBy(w => w).SequenceEqual(new[] { Launcher, Mhynot, OwnDst })
            && byFolder.BytesNeeded >= "INJECTOR-STUB".Length + "MHYNOT2-STUB".Length + "OWN-7.7".Length && byFolder.FreeBytes == long.MaxValue);
        var byExe = ImportCheck.Run(Req(V7, exe, fx));
        var quoted = ImportCheck.Run(Req(V7, "  \"" + exe + "\"  ", fx));
        var lower = ImportCheck.Run(Req(V7, Path.Combine(game, "genshinimpact.EXE"), fx));
        var trailing = ImportCheck.Run(Req(V7, game + Path.DirectorySeparatorChar, fx));
        Check("path: the path of GenshinImpact.exe (as picked, quoted the way \"Copy as path\" does, in another letter case) and the folder with a trailing separator all name the same folder",
            new[] { byExe, quoted, lower, trailing }.All(v => v.Status == ImportVerdict.Ready && v.GameDir == game));
        var noVersion = new[] { "", "  " }.Select(id => ImportCheck.Run(Req(id, game, fx))).ToList();
        var unknownVersion = ImportCheck.Run(Req("9.9", game, fx));
        Check("version: no version asked -> noVersion; a version Relic does not offer -> unknownVersion, before the folder is looked at",
            noVersion.All(v => v.Reason == ImportVerdict.NoVersion && v.Message == L.T("core.install.import.noVersion") && v.GameDir == "")
            && unknownVersion.Reason == ImportVerdict.UnknownVersion && unknownVersion.GameDir == ""
            && unknownVersion.Message == L.T("core.install.import.unknownVersion", new { version = "9.9" }));
        var empty = new[] { "", "   ", "\"\"", " \" \" " }.Select(p => ImportCheck.Run(Req(V7, p, fx))).ToList();
        Check("path: nothing given (empty, blanks, empty quotes) -> noPath",
            empty.All(v => v.Status == ImportVerdict.Refused && v.Reason == ImportVerdict.NoPath && v.Message == L.T("core.install.import.noPath")));
        var relative = new[] { @"Games\Genshin 7.7", @"\Games\Genshin 7.7", "D:Games" }.Select(p => ImportCheck.Run(Req(V7, p, fx))).ToList();
        Check("path: a path that is not a full one (relative, rooted without a drive, drive-relative) -> notFullPath, never resolved against Relic's own folder",
            relative.All(v => v.Reason == ImportVerdict.NotFullPath && v.GameDir == ""
                              && v.Message == L.T("core.install.import.notFullPath", new { version = V7 })));
        string missing = Path.Combine(root, "nowhere");
        var gone = ImportCheck.Run(Req(V7, missing, fx));
        Check("path: a folder that is not there -> folderMissing with the folder",
            gone.Reason == ImportVerdict.FolderMissing && gone.Message == L.T("core.install.import.folderMissing", new { dir = missing }));
        var other = ImportCheck.Run(Req(V7, Path.Combine(game, ClientIdentity.ListFile), fx));
        Check("path: a file that is not GenshinImpact.exe -> notGameExe naming it",
            other.Reason == ImportVerdict.NotGameExe && other.Message == L.T("core.install.import.notGameExe", new { file = ClientIdentity.ListFile }));
        string noExe = Game(root, "no exe", V7, exe: false);
        string noData = Game(root, "no data", V7, data: false);
        var exeGone = ImportCheck.Run(Req(V7, Path.Combine(noExe, "GenshinImpact.exe"), fx));
        Check("path: a folder without GenshinImpact.exe -> noExe (also when the path names the exe that is not there); without GenshinImpact_Data -> noData",
            ImportCheck.Run(Req(V7, noExe, fx)).Reason == ImportVerdict.NoExe && exeGone.Reason == ImportVerdict.NoExe && exeGone.GameDir == noExe
            && ImportCheck.Run(Req(V7, noData, fx)).Reason == ImportVerdict.NoData);
        var shallow = ImportCheck.Run(Req(V7, game, fx, deep: false));
        Check("path: the instant check of a good folder says \"checking\", never \"ready\" — only the deep one does",
            shallow.Status == ImportVerdict.Checking && !shallow.Deep && shallow.Message == L.T("core.install.inspect.checking")
            && shallow.Writes.Count == 0);
    }

    // ── B. what the folder holds ──

    static void Identities(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "ids");
        string g7 = Game(root, "holds 7.7", V7);
        var asOther = ImportCheck.Run(Req(V8, g7, fx));
        Check("identity: the client of another version Relic offers -> otherVersion with its own text, found named and offered (the page can offer that version)",
            asOther.Reason == ImportVerdict.OtherVersion && asOther.Found == V7 && asOther.FoundOffered && asOther.Identity == "otherVersion"
            && asOther.Message == L.T("core.install.import.otherVersion", new { found = V7, version = V8 }));
        string cfg6 = Game(root, "config 6.6", null, config: V6);
        var byConfig = ImportCheck.Run(Req(V7, cfg6, fx));
        Check("identity: a config.ini that names another offered version -> otherVersion before the list is read, that version offered",
            byConfig.Reason == ImportVerdict.OtherVersion && byConfig.Found == V6 && byConfig.FoundOffered && byConfig.ConfigVersion == V6
            && byConfig.Identity == "" && byConfig.Message == L.T("core.install.import.otherVersion", new { found = V6, version = V7 }));
        var asConfig = ImportCheck.Run(Req(V6, cfg6, fx));
        Check("identity: ...and asked as that version it is ready (no list to say otherwise)",
            asConfig.Status == ImportVerdict.Ready && asConfig.Identity == "unknown");
        string contra = Game(root, "contradiction", V7, config: V6);
        var asList = ImportCheck.Run(Req(V7, contra, fx));
        var asCfg = ImportCheck.Run(Req(V6, contra, fx));
        Check("identity: a folder whose config.ini (6.6) and file list (7.7) disagree is refused as either version, and no version is offered for it — no switch back and forth",
            new[] { asList, asCfg }.All(v => v.Reason == ImportVerdict.Contradiction && v.Found == "" && !v.FoundOffered && v.ConfigVersion == V6
                && v.Message == L.T("core.install.import.contradiction", new { config = V6, found = V7 })));
        string live = Game(root, "live", "noline", config: "9.1");
        var notOffered = ImportCheck.Run(Req(V7, live, fx));
        Check("identity: a config.ini of a version Relic does not offer (the shape of the current official client) -> notOffered, its own text",
            notOffered.Reason == ImportVerdict.NotOffered && notOffered.Found == "9.1" && !notOffered.FoundOffered
            && notOffered.Message == L.T("core.install.import.notOffered", new { found = "9.1", version = V7 }));
        string unknown = Game(root, "unknown build", "x");
        var tolerated = ImportCheck.Run(Req(V7, unknown, fx));
        var exact = ImportCheck.Run(Req(V8, unknown, fx));
        Check("identity: a build no row names -> accepted with a note where the version tolerates one, wrongBuild where only its builds work",
            tolerated.Status == ImportVerdict.Ready && tolerated.Identity == "unknownBuild"
            && tolerated.Notes.Any(n => n.Code == "unknownBuild" && n.Text == L.T("core.install.inspect.unknownBuild", new { version = V7 }))
            && exact.Reason == ImportVerdict.WrongBuild
            && exact.Message == L.T("core.install.import.wrongBuild", new { version = V8, build = "8.8.0" }));
        string noList = Game(root, "no list", null);
        string noLine = Game(root, "no line", "noline");
        var withoutList = ImportCheck.Run(Req(V7, noList, fx));
        var withoutLine = ImportCheck.Run(Req(V7, noLine, fx));
        string noListText = L.T("core.install.inspect.noList", new { version = V7 });
        Check("identity: no pkg_version, and a pkg_version without the identity line, are accepted as they are, with the note that the build cannot be told",
            withoutList.Status == ImportVerdict.Ready && withoutList.Identity == "unknown" && withoutList.Notes.Any(n => n.Code == "noList" && n.Text == noListText)
            && withoutLine.Status == ImportVerdict.Ready && withoutLine.Identity == "unknown" && withoutLine.Notes.Any(n => n.Code == "noList" && n.Text == noListText));
        string resized = Game(root, "resized", V7, ua: new byte[999]);
        var noted = ImportCheck.Run(Req(V7, resized, fx));
        var id = ClientIdentity.Identify(resized, fx.Payload);
        var proven = ClientIdentity.Identify(g7, fx.Payload);
        Check("identity: Identify without an asked version names the row, the listed and the on-disk size; a size that differs is a note, not a refusal",
            proven is { Kind: ClientIdentityKind.Match, Version: V7, Build: "7.7.0", Exact: false, ListedSize: 1000, OnDiskSize: 1000, SizeProven: true }
            && id is { Kind: ClientIdentityKind.Match, ListedSize: 1000, OnDiskSize: 999, SizeProven: false }
            && noted.Status == ImportVerdict.Ready && HasNote(noted, "identityFile"));
        Check("identity: Identify of an exact version, of a build no row names and of a folder without a list",
            ClientIdentity.Identify(Game(root, "holds 8.8", V8, fx: fx), fx.Payload) is { Kind: ClientIdentityKind.Match, Version: V8, Exact: true, SizeProven: true }
            && ClientIdentity.Identify(unknown, fx.Payload) is { Kind: ClientIdentityKind.UnknownBuild, Version: "", File: UaDst, ListedSize: 1000 }
            && ClientIdentity.Identify(noList, fx.Payload).Kind == ClientIdentityKind.Unknown
            && ClientIdentity.Identify(noLine, fx.Payload).Kind == ClientIdentityKind.Unknown);
    }

    static bool HasNote(ImportVerdict v, string code) => v.Notes.Any(n => n.Code == code && n.Text.Length > 0);

    // ── C. the registrations and a running client ──

    static void Registrations(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "regs");
        string game = Game(root, "Genshin 7.7", V7);
        string copy = Game(root, "Genshin 7.7 copy", V7);
        string sixth = Game(root, "Genshin 6.6", V6);
        var clash = ImportCheck.Run(Req(V7, game, fx, installed: new[] { new ImportRegistration(V6, game) }));
        Check("registrations: a folder registered as another version -> registeredOther",
            clash.Reason == ImportVerdict.RegisteredOther && clash.RegisteredAs == V6
            && clash.Message == L.T("core.install.import.alreadyRegistered", new { version = V6 }));
        var again = ImportCheck.Run(Req(V7, game.ToUpperInvariant() + "\\", fx, installed: new[] { new ImportRegistration(V7, game) }));
        Check("registrations: the folder the version is registered at (in any letter case) -> ready, with the note that it is already in the Library",
            again.Status == ImportVerdict.Ready && again.RegisteredAs == V7 && HasNote(again, "registered") && again.InstalledAt == "");
        var installed = new[] { new ImportRegistration(V7, game), new ImportRegistration(V6, sixth) };
        var elsewhere = ImportCheck.Run(Req(V7, copy, fx, installed: installed));
        Check("registrations: a copy of a version installed at another folder -> installedElsewhere, never taken in its place unasked",
            elsewhere.Reason == ImportVerdict.InstalledElsewhere && elsewhere.InstalledAt == game && !elsewhere.InstalledAtGone && !elsewhere.ReplaceInstalled
            && elsewhere.Message == L.T("core.install.import.installedElsewhere", new { version = V7, dir = game }));
        var chosen = ImportCheck.Run(Req(V7, copy, fx, installed: installed, replaceAt: game.ToLowerInvariant()));
        Check("registrations: ...with the player's choice of this folder over that very one -> ready, with the note that it takes the other's place",
            chosen.Status == ImportVerdict.Ready && chosen.ReplaceInstalled && chosen.InstalledAt == game && HasNote(chosen, "replacing"));
        var wrongFolder = ImportCheck.Run(Req(V7, copy, fx, installed: installed, replaceAt: Path.Combine(root, "somewhere else")));
        var otherVersionsFolder = ImportCheck.Run(Req(V7, copy, fx, installed: installed, replaceAt: sixth));
        Check("registrations: a choice that names another folder — one the version is not at, or another version's — is no choice: installedElsewhere",
            new[] { wrongFolder, otherVersionsFolder }.All(v => v.Reason == ImportVerdict.InstalledElsewhere && !v.ReplaceInstalled));
        string emptied = Path.Combine(root, "emptied");
        Directory.CreateDirectory(emptied);
        var gone = ImportCheck.Run(Req(V7, copy, fx, installed: new[] { new ImportRegistration(V7, emptied) }));
        Check("registrations: the version's folder no longer holds the game -> still the explicit choice, and the page is told the folder is gone",
            gone.Reason == ImportVerdict.InstalledElsewhere && gone.InstalledAtGone);
        var running = ImportCheck.Run(Req(V7, game, fx, running: new[] { Path.Combine(game, "GenshinImpact.exe") }));
        var elsewhereRuns = ImportCheck.Run(Req(V7, game, fx, running: new[] { Path.Combine(copy, "GenshinImpact.exe") }));
        Check("running: a client that runs from the folder -> running; one that runs from another folder is no reason",
            running.Reason == ImportVerdict.Running && running.Message == L.T("core.install.import.gameRunning")
            && elsewhereRuns.Status == ImportVerdict.Ready);

        // Another name of the same folder is that folder: a junction to it.
        string real = Game(root, "real", null);
        string alias = Path.Combine(root, "alias");
        bool made = Junction(alias, real);
        var throughAlias = ImportCheck.Run(Req(V7, alias, fx, installed: new[] { new ImportRegistration(V6, real) }));
        var runningReal = ImportCheck.Run(Req(V7, alias, fx, running: new[] { Path.Combine(real, "GenshinImpact.exe") }));
        var sameThroughAlias = ImportCheck.Run(Req(V7, alias, fx, installed: new[] { new ImportRegistration(V7, real) }));
        Check("registrations: a junction to a folder is that folder — registered as another version (registeredOther), running from its real path (running), registered as this version (the same folder, not a copy)",
            made && ImportCheck.SameDir(alias, real) && !ImportCheck.SameDir(alias, copy)
            && throughAlias.Reason == ImportVerdict.RegisteredOther && runningReal.Reason == ImportVerdict.Running
            && sameThroughAlias.Status == ImportVerdict.Ready && sameThroughAlias.RegisteredAs == V7 && sameThroughAlias.InstalledAt == "");
        if (made) Directory.Delete(alias);
    }

    // ── D. a folder or a file Relic cannot write ──

    static void Writability(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "write");
        string game = Game(root, "protected", V7);
        string before = Snapshot(game);
        ImportVerdict denied, deniedShallow;
        string? probe;
        using (Deny(game, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, directory: true))
        {
            probe = WriteAccess.CanCreateIn(game, folders: true);
            denied = ImportCheck.Run(Req(V7, game, fx));
            deniedShallow = ImportCheck.Run(Req(V7, game, fx, deep: false));
        }
        Check("write: a folder this user may not add to -> readOnly with the folder (\"copy the game into a folder of your own\"), the instant check too, and nothing written",
            probe == WriteAccess.Denied && denied.Reason == ImportVerdict.ReadOnly && deniedShallow.Reason == ImportVerdict.ReadOnly
            && denied.Message == L.T("core.install.import.readOnly", new { dir = game }) && Snapshot(game) == before
            && !Directory.Exists(Path.Combine(game, "ayy")));
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Check("write: Program Files is asked the same way (a handle opened and closed, nothing written) and answers denied for this unelevated run",
            !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)
                ? WriteAccess.CanCreateIn(programFiles) == WriteAccess.Denied
                : WriteAccess.CanCreateIn(programFiles) is null or WriteAccess.Denied);

        string current = Patched(Game(root, "current, protected", V7), V7, fx.Payload);
        ImportVerdict currentDeep, currentShallow;
        using (Deny(current, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, directory: true))
        {
            currentDeep = ImportCheck.Run(Req(V7, current, fx));
            currentShallow = ImportCheck.Run(Req(V7, current, fx, deep: false));
        }
        Check("write: a folder whose Relic files are all current is still refused when Relic cannot create files in it — Relic repairs them there before every launch — by both checks",
            currentDeep.Reason == ImportVerdict.ReadOnly && currentShallow.Reason == ImportVerdict.ReadOnly);

        string sub = Patched(Game(root, "sub-folder denied, file current", V7), V7, fx.Payload);
        ImportVerdict subShallow, subDeep;
        using (Deny(P(sub, "extra"), FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, directory: true))
        {
            subShallow = ImportCheck.Run(Req(V7, sub, fx, deep: false));
            subDeep = ImportCheck.Run(Req(V7, sub, fx));
        }
        string subOld = Game(root, "sub-folder denied, file not current", V7);
        Write(subOld, OwnDst, "SOMETHING ELSE");
        ImportVerdict subOldShallow, subOldDeep;
        string subOldBefore = Snapshot(subOld);
        using (Deny(P(subOld, "extra"), FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, directory: true))
        {
            subOldShallow = ImportCheck.Run(Req(V7, subOld, fx, deep: false));
            subOldDeep = ImportCheck.Run(Req(V7, subOld, fx));
        }
        Check("write: the instant check refuses only what the deep one refuses too — a denied sub-folder whose file is Relic's already: checking, then ready (nothing to write there)",
            subShallow.Status == ImportVerdict.Checking && subDeep.Status == ImportVerdict.Ready && subDeep.Writes.Count == 0);
        Check("write: ...whose file is another: checking, then refused naming the file (not the whole game folder), nothing written",
            subOldShallow.Status == ImportVerdict.Checking && subOldDeep.Reason == ImportVerdict.ReadOnly
            && subOldDeep.Message == L.T("core.install.import.fileDenied", new { file = OwnDst }) && Snapshot(subOld) == subOldBefore);

        foreach (var attribute in new[] { FileAttributes.ReadOnly, FileAttributes.Hidden, FileAttributes.System })
        {
            string g = Game(root, "attr " + attribute, V7);
            string dst = Write(g, OwnDst, "SOMETHING ELSE");
            File.SetAttributes(dst, attribute);
            string snap = Snapshot(g);
            var v = ImportCheck.Run(Req(V7, g, fx));
            bool unchanged = Snapshot(g) == snap;
            File.SetAttributes(dst, FileAttributes.Normal);
            Check($"write: a {attribute.ToString().ToLowerInvariant()} file the import would copy over (Windows refuses that) -> fileAttributes naming it, nothing written",
                v.Reason == ImportVerdict.Attributes && v.Message == L.T("core.install.import.fileAttributes", new { file = OwnDst })
                && unchanged);
        }
        string held = Game(root, "held", V7);
        string heldFile = Write(held, OwnDst, "SOMETHING ELSE");
        ImportVerdict inUse;
        using (new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            inUse = ImportCheck.Run(Req(V7, held, fx));
        Check("write: a file another program holds -> inUse naming it",
            inUse.Reason == ImportVerdict.InUse && inUse.Message == L.T("core.install.import.inUse", new { file = OwnDst }));
        string readOnlyCurrent = Patched(Game(root, "read-only but current", V7), V7, fx.Payload);
        string currentFile = P(readOnlyCurrent, OwnDst);
        File.SetAttributes(currentFile, FileAttributes.ReadOnly);
        var currentReadOnly = ImportCheck.Run(Req(V7, readOnlyCurrent, fx));
        File.SetAttributes(currentFile, FileAttributes.Normal);
        Check("write: a read-only file that already is Relic's is no reason — nothing would be written over it",
            currentReadOnly.Status == ImportVerdict.Ready && currentReadOnly.Writes.Count == 0);

        // Something of another kind at a name a write needs.
        string folderAt = Game(root, "a folder where the file goes", V7);
        Directory.CreateDirectory(P(folderAt, OwnDst));
        string fileAt = Game(root, "a file where a folder goes", V7);
        Write(fileAt, "extra", "a file named like the folder");
        string tmpFolder = Game(root, "a folder at the byte patch's copy", V8, mhypbase: RpOriginal, fx: fx);
        Directory.CreateDirectory(P(tmpFolder, UaDst) + GamePatcher.TempSuffix);
        string origFolder = Game(root, "a folder at the backup's name", V8, mhypbase: RpOriginal, fx: fx);
        Directory.CreateDirectory(P(origFolder, RpDst) + GamePatcher.BackupSuffix);
        var snaps = new[] { folderAt, fileAt, tmpFolder, origFolder }.Select(Snapshot).ToList();
        var atFolder = ImportCheck.Run(Req(V7, folderAt, fx));
        var atFolderShallow = ImportCheck.Run(Req(V7, folderAt, fx, deep: false));
        var atFile = ImportCheck.Run(Req(V7, fileAt, fx));
        var atTmp = ImportCheck.Run(Req(V8, tmpFolder, fx));
        var atOrig = ImportCheck.Run(Req(V8, origFolder, fx));
        Check("write: a folder where Relic's file goes, or a file where one of its folders goes -> inTheWay naming the file (the instant check too, nothing to hash), nothing written",
            atFolder.Reason == ImportVerdict.InTheWay && atFolder.Message == L.T("core.install.import.inTheWay", new { file = OwnDst })
            && atFolderShallow.Reason == ImportVerdict.InTheWay && atFile.Reason == ImportVerdict.InTheWay);
        Check("write: a folder at the name of a byte patch's copy, or of a backup the import takes -> inTheWay naming the entry, nothing written",
            atTmp.Reason == ImportVerdict.InTheWay && atTmp.Message == L.T("core.install.import.inTheWay", new { file = UaDst })
            && atOrig.Reason == ImportVerdict.InTheWay && atOrig.Message == L.T("core.install.import.inTheWay", new { file = RpDst })
            && new[] { folderAt, fileAt, tmpFolder, origFolder }.Select(Snapshot).SequenceEqual(snaps));
    }

    // ── E. files held, mapped, denied: what a rename over them needs ──

    static void HeldAndMapped(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "held-mapped");
        string shared = Game(root, "mhypbase held, sharing delete", V8, mhypbase: RpOriginal, fx: fx);
        ImportVerdict sharedVerdict;
        string? sharedProbe;
        using (new FileStream(P(shared, RpDst), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            sharedProbe = WriteAccess.CanReplace(P(shared, RpDst));
            sharedVerdict = ImportCheck.Run(Req(V8, shared, fx));
        }
        Check("held: a file Relic renames its copy over, held open by a program that shares even deleting -> inUse (Windows refuses a rename over any open file)",
            sharedProbe == WriteAccess.InUse && sharedVerdict.Reason == ImportVerdict.InUse
            && sharedVerdict.Message == L.T("core.install.import.inUse", new { file = RpDst }));
        // ...and one that shares everything: reading, writing and deleting.
        string sharesAll = Game(root, "mhypbase held, sharing everything", V8, mhypbase: RpOriginal, fx: fx);
        ImportVerdict sharesAllVerdict;
        string? sharesAllProbe;
        using (new FileStream(P(sharesAll, RpDst), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            sharesAllProbe = WriteAccess.CanReplace(P(sharesAll, RpDst));
            sharesAllVerdict = ImportCheck.Run(Req(V8, sharesAll, fx));
        }
        Check("held: ...held open by a program that shares reading, writing and deleting -> inUse all the same",
            sharesAllProbe == WriteAccess.InUse && sharesAllVerdict.Reason == ImportVerdict.InUse
            && sharesAllVerdict.Message == L.T("core.install.import.inUse", new { file = RpDst }));

        // The preview's own hash lets others write meanwhile: a file another program has open for writing is still read
        // (the answer is that the program holds it), never a failure of the preview — and so its handle never stops
        // another program's write either.
        string writing = Game(root, "own.bin open for writing elsewhere", V7);
        string writingFile = Write(writing, OwnDst, "SOMETHING ELSE");
        PatchPreview writingPreview;
        using (new FileStream(writingFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete))
            writingPreview = GamePatcher.Preview(V7, writing, fx.Payload, undoable: true);
        Check("held: a file another program has open for writing is still hashed by the preview (it shares writing and deleting) — the answer is that the program holds it",
            writingPreview.Refusal is null && writingPreview.Report.Blocked.SingleOrDefault() is { Dst: OwnDst, Problem: WriteAccess.InUse });

        string stale = Game(root, "a cut copy held beside the byte patch", V8, mhypbase: RpOriginal, fx: fx);
        string staleCopy = WriteBytes(stale, UaDst + GamePatcher.TempSuffix, fx.UaOriginal);
        ImportVerdict staleVerdict;
        using (new FileStream(staleCopy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            staleVerdict = ImportCheck.Run(Req(V8, stale, fx));
        var staleFree = ImportCheck.Run(Req(V8, stale, fx));
        Check("held: a copy a cut run left at the name a byte patch makes again, held open -> inUse; not held -> ready (the run removes it)",
            staleVerdict.Reason == ImportVerdict.InUse && staleVerdict.Message == L.T("core.install.import.inUse", new { file = UaDst })
            && staleFree.Status == ImportVerdict.Ready);
        string staleRp = Game(root, "a cut copy held beside mhypbase.dll", V8, mhypbase: RpOriginal, fx: fx);
        string staleRpCopy = Write(staleRp, RpDst + GamePatcher.TempSuffix, RpRelic);
        ImportVerdict staleRpVerdict;
        using (new FileStream(staleRpCopy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            staleRpVerdict = ImportCheck.Run(Req(V8, staleRp, fx));
        var staleRpFree = ImportCheck.Run(Req(V8, staleRp, fx));
        Check("held: ...and the one a replacement makes again beside mhypbase.dll, held open -> inUse; not held -> ready",
            staleRpVerdict.Reason == ImportVerdict.InUse && staleRpVerdict.Message == L.T("core.install.import.inUse", new { file = RpDst })
            && staleRpFree.Status == ImportVerdict.Ready);

        string deniedDir = Game(root, "the byte patch's file may not be replaced", V8, mhypbase: RpOriginal, fx: fx);
        string before = Snapshot(deniedDir);
        ImportVerdict deniedVerdict;
        string? deniedProbe;
        using (Deny(P(deniedDir, UaDst), FileSystemRights.Delete, directory: false))
        using (Deny(Path.GetDirectoryName(P(deniedDir, UaDst))!, FileSystemRights.DeleteSubdirectoriesAndFiles, directory: true))
        {
            deniedProbe = WriteAccess.CanReplace(P(deniedDir, UaDst));
            deniedVerdict = ImportCheck.Run(Req(V8, deniedDir, fx));
        }
        Check("denied: a file neither it nor its folder lets this user delete, which a byte patch renames its copy over -> refused naming the file, nothing written",
            deniedProbe == WriteAccess.Denied && deniedVerdict.Reason == ImportVerdict.ReadOnly
            && deniedVerdict.Message == L.T("core.install.import.fileDenied", new { file = UaDst }) && Snapshot(deniedDir) == before);

        // A DLL the system has mapped as a program's code: a copy of a system DLL made the identity file of a version
        // whose byte patch is computed on it.
        var (game, payload, ua) = MappedClient(Path.Combine(root, "mapped"), pair: false);
        string mappedBefore = Snapshot(game);
        ImportVerdict mapped;
        string? mappedProbe;
        IntPtr module = NativeLibrary.Load(ua);
        try
        {
            mappedProbe = WriteAccess.CanReplace(ua);
            mapped = ImportCheck.Run(Req(V8, game, fx, payload: payload));
        }
        finally { NativeLibrary.Free(module); }
        var unmapped = ImportCheck.Run(Req(V8, game, fx, payload: payload));
        Check("mapped: the identity file a byte patch is written into, mapped as a DLL by a process -> inUse, nothing written; once let go -> ready",
            mappedProbe == WriteAccess.InUse && mapped.Reason == ImportVerdict.InUse
            && mapped.Message == L.T("core.install.import.inUse", new { file = UaDst }) && Snapshot(game) == mappedBefore
            && unmapped.Status == ImportVerdict.Ready && unmapped.Writes.SequenceEqual(new[] { UaDst }));
    }

    /// <summary>A client of version 8.8 (an exact one) whose identity file is a copy of a system DLL — one a process can
    /// map as a program's code — and a payload whose required byte patch is computed on it; with <paramref name="pair"/>
    /// the payload carries the injector pair too.</summary>
    static (string Game, string Payload, string Ua) MappedClient(string root, bool pair)
    {
        string payload = Path.Combine(root, "payload");
        string game = Path.Combine(root, "Genshin 8.8");
        Directory.CreateDirectory(Path.Combine(game, "GenshinImpact_Data"));
        Write(game, "GenshinImpact.exe", "not really an exe");
        string ua = P(game, UaDst);
        Directory.CreateDirectory(Path.GetDirectoryName(ua)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), ua);
        byte[] original = File.ReadAllBytes(ua);
        int offset = original.Length - 200, length = 64;
        byte[] span = new byte[length];
        for (int i = 0; i < length; i++) span[i] = (byte)(original[offset + i] ^ 0x5A);
        byte[] patched = (byte[])original.Clone();
        span.CopyTo(patched, offset);
        WriteBytes(payload, "span.bin", span);
        string common = "";
        if (pair)
        {
            string launcher = Write(payload, "common/launcher.exe.bin", "INJECTOR-STUB");
            string mhynot = Write(payload, "common/mhynot2.dll.bin", "MHYNOT2-STUB");
            common = $$"""
                { "src": "common/launcher.exe.bin", "dst": "{{Launcher}}", "sha256": "{{Sha(launcher)}}" },
                { "src": "common/mhynot2.dll.bin", "dst": "{{Mhynot}}", "sha256": "{{Sha(mhynot)}}" }
                """;
        }
        string md5 = Convert.ToHexString(MD5.HashData(original)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(game, ClientIdentity.ListFile), Line(UaDst, md5, original.Length) + "\r\n");
        File.WriteAllText(Path.Combine(payload, "manifest.json"), $$"""
            {
              "clients": [ { "version": "{{V8}}", "build": "8.8.0", "file": "{{UaDst}}", "md5": "{{md5}}", "size": {{original.Length}}, "exact": true } ],
              "common": [ {{common}} ],
              "versions": { "{{V8}}": [
                { "kind": "bytepatch", "src": "span.bin", "dst": "{{UaDst}}", "size": {{original.Length}}, "offset": {{offset}}, "length": {{length}},
                  "sha256Before": "{{ShaOf(original)}}", "sha256": "{{ShaOf(patched)}}",
                  "spanSha256Before": "{{ShaOf(original.AsSpan(offset, length).ToArray())}}", "spanSha256": "{{ShaOf(span)}}", "required": true }
              ] }
            }
            """);
        return (game, payload, ua);
    }

    // ── F. the preview is Apply's own run ──

    static void PreviewParity(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "parity");
        string damaged = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-damaged"), new PayloadOptions { OwnContent = "NOT THE BYTES THE MANIFEST HASHES" });
        string noOwn = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-no-own"), new PayloadOptions { OwnSrc = false });
        string noLauncher = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-no-launcher"), new PayloadOptions { Launcher = false });
        string oddKind = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-odd-kind"),
            new PayloadOptions { Tweak = j => j.Replace("\"kind\": \"bytepatch\"", "\"kind\": \"hexpatch\"") });

        var cases = new List<(string Name, string Version, Func<string, string> Make, string Payload, bool ReplaceOwn)>
        {
            ("a fresh 7.7 folder", V7, n => Game(root, n, V7), fx.Payload, false),
            ("a 7.7 folder Relic's files are in already", V7, n => Patched(Game(root, n, V7), V7, fx.Payload), fx.Payload, false),
            ("8.8: the original identity file and the original mhypbase.dll", V8, n => Game(root, n, V8, mhypbase: RpOriginal, fx: fx), fx.Payload, false),
            ("8.8: patched already", V8, n => Patched(Game(root, n, V8, mhypbase: RpOriginal, fx: fx), V8, fx.Payload), fx.Payload, false),
            ("8.8: the patched span with a byte changed elsewhere", V8, n => Game(root, n, V8, ua: Changed(fx.UaPatched, BpOffset + BpLength + 50), fx: fx), fx.Payload, false),
            ("8.8: a span that is neither", V8, n => Game(root, n, V8, ua: Changed(fx.UaOriginal, BpOffset + 5), fx: fx), fx.Payload, false),
            ("8.8: the identity file missing", V8, n => Game(root, n, null), fx.Payload, false),
            ("8.8: the player's own mhypbase.dll", V8, n => Game(root, n, V8, mhypbase: RpMine, fx: fx), fx.Payload, false),
            ("8.8: the player's own mhypbase.dll, Relic's chosen", V8, n => Game(root, n, V8, mhypbase: RpMine, fx: fx), fx.Payload, true),
            ("a payload file that does not hash to its entry", V7, n => Game(root, n, V7), damaged, false),
            ("a required payload file the build lacks", V7, n => Game(root, n, V7), noOwn, false),
            ("an optional payload file the build lacks", V7, n => Game(root, n, V7), noLauncher, false),
            ("a manifest with an unknown kind", V7, n => Game(root, n, V7), oddKind, false),
            ("8.8: a byte patch cut between its two renames", V8, n =>
            {
                string cut = Game(root, n, null);
                WriteBytes(cut, UaDst + GamePatcher.TempSuffix, fx.UaPatched);
                return cut;
            }, fx.Payload, false),
        };

        int index = 0;
        foreach (var c in cases)
        {
            string dir = c.Make($"case {index}");
            string twin = c.Make($"case {index} (undoable)");
            index++;
            string snapshot = Snapshot(dir);
            var preview = GamePatcher.Preview(c.Version, dir, c.Payload, replaceOwn: c.ReplaceOwn);
            bool untouched = Snapshot(dir) == snapshot;
            PatchReport? applied = null;
            string? thrown = null;
            try { applied = GamePatcher.Apply(c.Version, dir, null, c.Payload, replaceOwn: c.ReplaceOwn); }
            catch (Exception ex) { thrown = ex.Message; }
            bool same = preview.Refusal == thrown && (applied is null
                ? true
                : Same(preview.Report.Applied, applied.Applied) && Same(preview.Report.AlreadyCurrent, applied.AlreadyCurrent)
                  && Same(preview.Report.Missing, applied.Missing) && Same(preview.Report.Kept, applied.Kept)
                  && Same(preview.Report.ReplacedOwn, applied.ReplacedOwn));
            Check($"preview: {c.Name} — the same refusal and report as Apply" + (preview.Refusal is null ? "" : " (refused)")
                  + ", and the preview wrote nothing",
                same && untouched && preview.Report.Blocked.Count == 0 && preview.Report.Unsettled.Count == 0 && preview.Deep
                && (preview.HasPlan || preview.Refusal == L.T("core.patch.manifestUnreadable"))
                && (preview.Report.Applied.Count == 0 || preview.BytesNeeded > 0));
            if (c.ReplaceOwn) continue; // the import never writes over the player's own file
            // The import's run: the same decisions, in its own order — and, taken back, nothing left of it.
            string twinBefore = Snapshot(twin);
            var undo = new PatchUndo();
            PatchReport? undoable = null;
            string? undoThrown = null;
            try { undoable = GamePatcher.ApplyUndoable(c.Version, twin, undo, null, c.Payload); }
            catch (Exception ex) { undoThrown = ex.Message; }
            bool sameSet = undoThrown == thrown && (applied is null || undoable is null
                ? applied is null && undoable is null
                : SameSet(undoable.Applied, applied.Applied) && SameSet(undoable.AlreadyCurrent, applied.AlreadyCurrent)
                  && SameSet(undoable.Missing, applied.Missing) && SameSet(undoable.Kept, applied.Kept));
            var left = undo.Undo();
            Check($"undoable: {c.Name} — the decisions of Apply, and every write taken back leaves the folder as it was",
                sameSet && left.Count == 0 && Snapshot(twin) == twinBefore);
        }
        Check("preview: a build without a payload has no plan and nothing to say",
            GamePatcher.Preview(V7, Game(root, "no payload", V7), Path.Combine(tmp, "nowhere")) is { Refusal: null, HasPlan: false, BytesNeeded: 0 }
            && GamePatcher.Preview(V7, Game(root, "no payload 2", V7), Path.Combine(tmp, "nowhere")).Report.Applied.Count == 0);
    }

    static string Patched(string dir, string version, string payload)
    {
        GamePatcher.Apply(version, dir, null, payload);
        return dir;
    }

    static byte[] Changed(byte[] bytes, int at)
    {
        byte[] copy = (byte[])bytes.Clone();
        copy[at] ^= 0x01;
        return copy;
    }

    static bool Same(IEnumerable<string> a, IEnumerable<string> b) => a.SequenceEqual(b, StringComparer.Ordinal);

    static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        a.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);

    // ── G. a byte patch is ready only after the whole hash; the notes of a deep check ──

    static void BytePatchPreview(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "bp");
        string original = Game(root, "8.8 original", V8, mhypbase: RpOriginal, fx: fx);
        string elsewhere = Game(root, "8.8 elsewhere", V8, mhypbase: RpOriginal, ua: Changed(fx.UaPatched, 10), fx: fx);
        string neither = Game(root, "8.8 neither", V8, mhypbase: RpOriginal, ua: Changed(fx.UaOriginal, BpOffset + 1), fx: fx);
        var shallowOriginal = GamePatcher.Preview(V8, original, fx.Payload, deep: false);
        var shallowElsewhere = GamePatcher.Preview(V8, elsewhere, fx.Payload, deep: false);
        Check("bytepatch: without the whole hash the matching span decides nothing — the entry stays open, no refusal",
            shallowOriginal.Refusal is null && shallowOriginal.Report.Unsettled.Contains(UaDst) && !shallowOriginal.Deep
            && shallowElsewhere.Refusal is null && shallowElsewhere.Report.Unsettled.Contains(UaDst));
        string notOriginal = L.T("core.patch.notOriginal", new { file = UaDst });
        var inspectShallow = ImportCheck.Run(Req(V8, elsewhere, fx, deep: false));
        var inspectDeep = ImportCheck.Run(Req(V8, elsewhere, fx));
        var inspectOriginal = ImportCheck.Run(Req(V8, original, fx));
        Check("bytepatch: the page's check of a file patched by someone else (the span Relic's, a byte elsewhere not) — \"checking\", then refused by the whole hash",
            inspectShallow.Status == ImportVerdict.Checking && inspectDeep.Reason == ImportVerdict.Patch && inspectDeep.Message == notOriginal
            && inspectOriginal.Status == ImportVerdict.Ready && inspectOriginal.Writes.Contains(UaDst));
        var neitherShallow = ImportCheck.Run(Req(V8, neither, fx, deep: false));
        Check("bytepatch: a span that is neither the original's nor Relic's is refused at once, without the whole hash",
            neitherShallow.Reason == ImportVerdict.Patch && neitherShallow.Message == notOriginal);
        string own = Game(root, "8.8 own", V8, mhypbase: RpMine, fx: fx);
        var ask = ImportCheck.Run(Req(V8, own, fx));
        var keep = ImportCheck.Run(Req(V8, own, fx, ownChoice: OwnFileChoices.Keep));
        var replace = ImportCheck.Run(Req(V8, own, fx, ownChoice: OwnFileChoices.Replace));
        Check("bytepatch: the player's own mhypbase.dll stays — the deep check names it, the name it would be kept under, and what PLAY does with it (ask, keep, replace)",
            ask.Status == ImportVerdict.Ready && ask.OwnFiles.Count == 1 && ask.OwnFiles[0].Dst == RpDst
            && ask.OwnFiles[0].Kept == RpDst + GamePatcher.OwnSuffix && !ask.Writes.Contains(RpDst)
            && ask.Notes.Any(n => n.Code == "ownFiles" && n.Text == L.T("core.install.inspect.ownAsk", new { files = RpDst }))
            && keep.OwnFilesChoice == OwnFileChoices.Keep
            && keep.Notes.Any(n => n.Code == "ownFiles" && n.Text == L.T("core.install.inspect.ownKeep", new { files = RpDst }))
            && replace.OwnFilesChoice == OwnFileChoices.Replace && !replace.Writes.Contains(RpDst)
            && replace.Notes.Any(n => n.Code == "ownFiles" && n.Text == L.T("core.install.inspect.ownReplace", new { files = RpDst })));
        string w11 = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-w11"),
            new PayloadOptions { Launch = $"\"{V8}\": {{ \"windows11\": \"unsupported\" }}" });
        var onWindows11 = ImportCheck.Run(Req(V8, original, fx, payload: w11) with { IsWindows11 = true });
        var onWindows10 = ImportCheck.Run(Req(V8, original, fx, payload: w11) with { IsWindows11 = false });
        var otherVersion = ImportCheck.Run(Req(V7, Game(root, "7.7", V7), fx, payload: w11) with { IsWindows11 = true });
        Check("windows 11: a version the manifest says is not known to start there is flagged on Windows 11 only, and only that version",
            onWindows11.Status == ImportVerdict.Ready && onWindows11.Windows11Unsupported
            && onWindows10.Status == ImportVerdict.Ready && !onWindows10.Windows11Unsupported
            && otherVersion.Status == ImportVerdict.Ready && !otherVersion.Windows11Unsupported);
    }

    // ── H. the room on the drive ──

    static void Room(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "room");
        string game = Game(root, "8.8 original", V8, mhypbase: RpOriginal, fx: fx);
        string before = Snapshot(game);
        var deep = GamePatcher.Preview(V8, game, fx.Payload, undoable: true);
        long need = deep.BytesNeeded;
        // The byte patch copies the whole file on its way; the replacement keeps the original as .relic-orig.
        Check($"room: the deep preview counts the room the writes take at their fullest ({need} bytes: the byte patch's whole copy, the backup, the new files)",
            need >= BpSize + RpOriginal.Length + "INJECTOR-STUB".Length + "MHYNOT2-STUB".Length);
        var tight = ImportCheck.Run(Req(V8, game, fx) with { FreeSpace = _ => need + ImportCheck.RoomMargin - 1 });
        var enough = ImportCheck.Run(Req(V8, game, fx) with { FreeSpace = _ => need + ImportCheck.RoomMargin });
        var unknown = ImportCheck.Run(Req(V8, game, fx) with { FreeSpace = _ => null });
        Check("room: a drive with less room than that (and the margin) -> noRoom naming the drive, the need and the free space, nothing written; enough room, or a drive that does not say -> ready",
            tight.Reason == ImportVerdict.NoRoom && tight.BytesNeeded == need && tight.FreeBytes == need + ImportCheck.RoomMargin - 1
            && tight.Message == L.T("core.install.import.noRoom", new
            {
                drive = Path.GetPathRoot(game) ?? game,
                need = (need + ImportCheck.RoomMargin + (1L << 20) - 1) >> 20,
                free = (need + ImportCheck.RoomMargin - 1) >> 20,
            })
            && enough.Status == ImportVerdict.Ready && unknown.Status == ImportVerdict.Ready && unknown.FreeBytes == -1
            && Snapshot(game) == before);
        var shallow = GamePatcher.Preview(V8, game, fx.Payload, deep: false, undoable: true);
        var shallowTight = ImportCheck.Run(Req(V8, game, fx, deep: false) with { FreeSpace = _ => shallow.BytesNeeded + ImportCheck.RoomMargin });
        var shallowNone = ImportCheck.Run(Req(V8, game, fx, deep: false) with { FreeSpace = _ => 0 });
        Check("room: the instant check counts only the writes it is sure of — less than the deep one — so it refuses for room only what the deep one refuses too",
            shallow.BytesNeeded < need && shallow.BytesNeeded > 0
            && shallowTight.Status == ImportVerdict.Checking && shallowNone.Reason == ImportVerdict.NoRoom);
        string copyOver = Game(root, "7.7 with another own.bin", V7);
        Write(copyOver, OwnDst, "SOMETHING ELSE, LONGER THAN RELIC'S");
        var asImport = GamePatcher.Preview(V7, copyOver, fx.Payload, undoable: true);
        var asApply = GamePatcher.Preview(V7, copyOver, fx.Payload, undoable: false);
        Check("room: the import keeps a copy of a file it writes over without a backup — counted in its room, not in Apply's",
            asImport.BytesNeeded == asApply.BytesNeeded + "SOMETHING ELSE, LONGER THAN RELIC'S".Length);
    }

    // ── I. the import ──

    static RelicState NewState()
    {
        var state = new RelicState();
        state.Settings.CreateShortcut = false;
        state.Settings.Enhancements = true;
        return state;
    }

    static void Importing(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "import");
        string statePath = Path.Combine(tmp, "state", "state.json");
        var state = NewState();
        var service = new InstallService(state, "") { StatePath = statePath, PayloadRoot = fx.Payload, OfferedVersions = Offered };
        var v7 = new GameVersionInfo { Id = V7 };
        var v8 = new GameVersionInfo { Id = V8 };
        var progress = new Progress<InstallProgress>(_ => { });

        string refusedDir = Game(root, "8.8 patched by someone else", V8, mhypbase: RpOriginal, ua: Changed(fx.UaPatched, 10), fx: fx);
        string before = Snapshot(refusedDir);
        var refused = Catch(() => service.ImportFolder(v8, refusedDir, progress));
        string asApply = Game(root, "8.8 the same, through Apply alone", V8, mhypbase: RpOriginal, ua: Changed(fx.UaPatched, 10), fx: fx);
        var applyRefused = Catch(() => GamePatcher.Apply(V8, asApply, null, fx.Payload));
        Check("import: a refused import writes nothing — not the injector pair either (Apply alone, refusing the same folder, has put it in by then); the refusal is the check's text and nothing is registered or saved",
            refused is GamePatchException && refused.Message == L.T("core.patch.notOriginal", new { file = UaDst })
            && Snapshot(refusedDir) == before && !Directory.Exists(Path.Combine(refusedDir, "ayy"))
            && applyRefused is GamePatchException && File.Exists(Path.Combine(asApply, "ayy", "anime", "build", "launcher.exe"))
            && state.Installed.Count == 0 && !File.Exists(statePath));
        var refusedText = ImportCheck.Run(Req(V8, refusedDir, fx));
        var refusedPath = Catch(() => service.ImportFolder(v7, Path.Combine(root, "nowhere"), progress));
        Check("import: the import and the page's check refuse with the same text",
            refusedText.Message == refused?.Message && refusedPath is InvalidOperationException
            && refusedPath.Message == ImportCheck.Run(Req(V7, Path.Combine(root, "nowhere"), fx)).Message);

        string protectedDir = Game(root, "7.7 protected", V7);
        string protectedBefore = Snapshot(protectedDir);
        Exception? protectedRefusal;
        using (Deny(protectedDir, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, directory: true))
            protectedRefusal = Catch(() => service.ImportFolder(v7, protectedDir, progress));
        Check("import: a folder Relic cannot write is refused before any write (never elevation) and stays as it was",
            protectedRefusal is InvalidOperationException && protectedRefusal.Message == L.T("core.install.import.readOnly", new { dir = protectedDir })
            && Snapshot(protectedDir) == protectedBefore && state.Installed.Count == 0);

        string game = Game(root, "Genshin 7.7", V7);
        var installed = service.ImportFolder(v7, Path.Combine(game, "GenshinImpact.exe"), progress);
        var saved = RelicState.Load(statePath);
        Check("import: a good folder, picked by its GenshinImpact.exe -> Relic's files in it, registered as an import, the injector detected, saved to the given state file, no copy of an undo left",
            installed.GameDir == game && installed.Origin == "import" && installed.LauncherExe is not null
            && File.Exists(P(game, OwnDst)) && saved.Installed.Count == 1 && saved.Installed[0].GameDir == game
            && !Directory.EnumerateFiles(game, "*" + GamePatcher.UndoSuffix, SearchOption.AllDirectories).Any());

        state.Installed[0].Origin = "launcher";
        var again = service.ImportFolder(v7, game, progress);
        Check("import: the folder a version is registered at, imported again, keeps its registration's origin",
            again.Origin == "launcher" && state.Installed.Count == 1);

        string copy = Game(root, "Genshin 7.7 copy", V7);
        string copyBefore = Snapshot(copy);
        string gameBefore = Snapshot(game);
        var unasked = Catch(() => service.ImportFolder(v7, copy, progress));
        var wrongChoice = Catch(() => service.ImportFolder(v7, copy, progress, replaceInstalledAt: copy));
        Check("import: a copy of an installed version is refused without the player's choice of that very folder — the registration and both folders unchanged",
            unasked is InvalidOperationException && unasked.Message == L.T("core.install.import.installedElsewhere", new { version = V7, dir = game })
            && wrongChoice is InvalidOperationException && wrongChoice.Message == unasked.Message
            && state.Installed.Single().GameDir == game && Snapshot(copy) == copyBefore && Snapshot(game) == gameBefore);
        var moved = service.ImportFolder(v7, copy, progress, replaceInstalledAt: game);
        Check("import: ...with the choice the registration moves to the copy (one registration, the profile slot of the version), the other folder left exactly as it was",
            moved.GameDir == copy && moved.Origin == "import" && moved.ProfileId == "priv77" && state.Installed.Count == 1
            && state.Installed[0].GameDir == copy && Snapshot(game) == gameBefore
            && RelicState.Load(statePath).Installed.Single().GameDir == copy);
        string third = Game(root, "Genshin 7.7 third", V7);
        var stale = Catch(() => service.ImportFolder(v7, third, progress, replaceInstalledAt: game));
        Check("import: a choice made of the folder the version was at before is no choice once it moved: refused, the registration stays at the copy",
            stale is InvalidOperationException && stale.Message == L.T("core.install.import.installedElsewhere", new { version = V7, dir = copy })
            && state.Installed.Single().GameDir == copy);
    }

    // ── J. a failure the check could not foresee is taken back whole ──

    static void Undoing(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "undo");
        var v7 = new GameVersionInfo { Id = V7 };
        var v8 = new GameVersionInfo { Id = V8 };
        var progress = new Progress<InstallProgress>(_ => { });
        int n = 0;
        (InstallService Service, RelicState State, string StatePath) Service(string payload, Action<string> between)
        {
            var state = NewState();
            string statePath = Path.Combine(tmp, "undo-state", (n++).ToString(), "state.json");
            return (new InstallService(state, "") { StatePath = statePath, PayloadRoot = payload, OfferedVersions = Offered, BeforeFirstWrite = between }, state, statePath);
        }
        bool NoneLeft(string dir) => !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Any(f => f.EndsWith(GamePatcher.UndoSuffix, StringComparison.Ordinal) || f.EndsWith(GamePatcher.TempSuffix, StringComparison.Ordinal));

        // The byte patch (the last of the required entries) cannot make its copy: the replacement before it, with the
        // .relic-orig it took, is taken back.
        string a = Game(root, "8.8 original", V8, mhypbase: RpOriginal, fx: fx);
        string? snapA = null;
        var sa = Service(fx.Payload, dir => { Directory.CreateDirectory(P(dir, UaDst) + GamePatcher.TempSuffix); snapA = Snapshot(dir); });
        var ea = Catch(() => sa.Service.ImportFolder(v8, a, progress));
        Check("undo: a failure after the check (the byte patch cannot make its copy) — the replacement written before it is taken back with its .relic-orig, the injector pair never went in, nothing registered or saved",
            ea is GamePatchException && snapA is not null && Snapshot(a) == snapA && Read(P(a, RpDst)) == RpOriginal
            && !File.Exists(P(a, RpDst) + GamePatcher.BackupSuffix) && !Directory.Exists(Path.Combine(a, "ayy"))
            && sa.State.Installed.Count == 0 && !File.Exists(sa.StatePath));

        // ...the same over a .relic-orig taken earlier: the replaced file comes back from the copy kept beside it.
        string b = Game(root, "8.8 original, its .relic-orig there", V8, mhypbase: RpOriginal, fx: fx);
        Write(b, RpDst + GamePatcher.BackupSuffix, RpOriginal);
        string? snapB = null;
        var sb = Service(fx.Payload, dir => { Directory.CreateDirectory(P(dir, UaDst) + GamePatcher.TempSuffix); snapB = Snapshot(dir); });
        var eb = Catch(() => sb.Service.ImportFolder(v8, b, progress));
        Check("undo: ...over a .relic-orig an earlier run took — the replaced file comes back from the copy kept beside it, and that copy is gone",
            eb is GamePatchException && snapB is not null && Snapshot(b) == snapB && Read(P(b, RpDst)) == RpOriginal && NoneLeft(b));

        // A required plain copy whose payload file changes after the check: its verification fails — the file it made,
        // and the folder made for it, go again.
        string payloadC = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-undo-c"), new PayloadOptions());
        string c = Game(root, "7.7 fresh", V7);
        string? snapC = null;
        var sc = Service(payloadC, dir => { File.WriteAllText(Path.Combine(payloadC, "7.7", "own.bin"), "CHANGED AFTER THE CHECK"); snapC = Snapshot(dir); });
        var ec = Catch(() => sc.Service.ImportFolder(v7, c, progress));
        Check("undo: a required copy that fails its verification — the file it wrote and the folder made for it are removed, the optional pair (written last) never went in",
            ec is GamePatchException && ec.Message == L.T("core.patch.hashMismatch", new { file = OwnDst })
            && Snapshot(c) == snapC && !Directory.Exists(Path.Combine(c, "extra")) && !Directory.Exists(Path.Combine(c, "ayy")));

        // ...over a file of the player's: it gets its bytes back from the copy kept beside it.
        string payloadD = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-undo-d"), new PayloadOptions());
        string d = Game(root, "7.7 with another own.bin", V7);
        Write(d, OwnDst, "THE PLAYER'S BYTES");
        string? snapD = null;
        var sd = Service(payloadD, dir => { File.WriteAllText(Path.Combine(payloadD, "7.7", "own.bin"), "CHANGED AFTER THE CHECK"); snapD = Snapshot(dir); });
        var ed = Catch(() => sd.Service.ImportFolder(v7, d, progress));
        Check("undo: ...written over a file that was there without a backup — that file gets its bytes back, and the copy kept for it is gone",
            ed is GamePatchException && Snapshot(d) == snapD && Read(P(d, OwnDst)) == "THE PLAYER'S BYTES" && NoneLeft(d));

        // ...with a backup the entry takes: the .relic-orig goes back over the file.
        string payloadE = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-undo-e"), new PayloadOptions { OwnBackup = true });
        string e = Game(root, "7.7, own.bin backed up", V7);
        Write(e, OwnDst, "THE ORIGINAL");
        File.SetAttributes(P(e, OwnDst), FileAttributes.Normal); // no archive bit: a copy of it carries one
        string? snapE = null;
        var se = Service(payloadE, dir => { File.WriteAllText(Path.Combine(payloadE, "7.7", "own.bin"), "CHANGED AFTER THE CHECK"); snapE = Snapshot(dir); });
        var ee = Catch(() => se.Service.ImportFolder(v7, e, progress));
        Check("undo: ...with the backup the entry takes — the .relic-orig goes back over the file, its attributes as they were, and none is left",
            ee is GamePatchException && Snapshot(e) == snapE && Read(P(e, OwnDst)) == "THE ORIGINAL"
            && !File.Exists(P(e, OwnDst) + GamePatcher.BackupSuffix));

        // The registration itself fails (the state cannot be saved): Relic's files go again, the registrations stay.
        string f = Game(root, "7.7, the state cannot be saved", V7);
        string fBefore = Snapshot(f);
        string blocked = Path.Combine(tmp, "undo-blocked-state");
        Write(blocked, "x", "a file where the state's folder goes");
        var fState = NewState();
        fState.Installed.Add(new InstalledVersion { Id = V6, GameDir = Path.Combine(tmp, "elsewhere 6.6"), Origin = "launcher", ProfileId = "priv66" });
        fState.SelectedVersionId = V6;
        var fService = new InstallService(fState, "") { StatePath = Path.Combine(blocked, "x", "state.json"), PayloadRoot = fx.Payload, OfferedVersions = Offered };
        var ef = Catch(() => fService.ImportFolder(v7, f, progress));
        Check("undo: a registration that cannot be saved — all of Relic's files, the optional injector pair included, are taken back and the registrations are as they were",
            ef is not null && Snapshot(f) == fBefore && !Directory.Exists(Path.Combine(f, "ayy"))
            && fState.Installed.Count == 1 && fState.Installed[0].Id == V6 && fState.SelectedVersionId == V6);

        // A byte patch put in, then a later one fails: its bytes go back through a verified copy.
        string payloadG = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-undo-g"), new PayloadOptions { SecondPatch = true });
        string g = Game(root, "8.8, two byte patches", V8, mhypbase: RpOriginal, fx: fx, second: true);
        Directory.CreateDirectory(P(g, SecondDst) + GamePatcher.TempSuffix);
        string gBefore = Snapshot(g);
        var undoG = new PatchUndo();
        var eg = Catch(() => GamePatcher.ApplyUndoable(V8, g, undoG, null, payloadG));
        bool wroteFirst = File.Exists(P(g, UaDst)) && ShaOf(File.ReadAllBytes(P(g, UaDst))) == ShaOf(fx.UaPatched);
        var leftG = undoG.Undo();
        Check("undo: a byte patch written, then a later one that fails — the first file gets its original bytes back (whole hash), the replacement before them too, no copy left",
            eg is GamePatchException && wroteFirst && leftG.Count == 0 && Snapshot(g) == gBefore
            && ShaOf(File.ReadAllBytes(P(g, UaDst))) == ShaOf(fx.UaOriginal) && Read(P(g, RpDst)) == RpOriginal);

        // The import's order, and a run that went through.
        string h = Game(root, "8.8, the import's order", V8, mhypbase: RpOriginal, fx: fx);
        string hTwin = Game(root, "8.8, Apply's order", V8, mhypbase: RpOriginal, fx: fx);
        var undoH = new PatchUndo();
        var orderH = GamePatcher.ApplyUndoable(V8, h, undoH, null, fx.Payload);
        bool kept = undoH.HasWrites;
        undoH.Commit();
        var orderApply = GamePatcher.Apply(V8, hTwin, null, fx.Payload);
        Check("undoable: the import's order — the required entries first, the byte patch among them last, then the optional ones — and, committed, the folder Apply leaves",
            orderH.Applied.SequenceEqual(new[] { RpDst, UaDst, Launcher, Mhynot })
            && orderApply.Applied.SequenceEqual(new[] { Launcher, Mhynot, UaDst, RpDst })
            && kept && !undoH.HasWrites && Snapshot(h) == Snapshot(hTwin));
        string i = Game(root, "8.8, .relic-orig there, imported", V8, mhypbase: RpOriginal, fx: fx);
        Write(i, RpDst + GamePatcher.BackupSuffix, RpOriginal);
        var si = Service(fx.Payload, _ => { });
        var imported = Catch(() => si.Service.ImportFolder(v8, i, progress));
        Check("undo: an import that went through keeps no copy it made for an undo",
            imported is null && Read(P(i, RpDst)) == RpRelic && NoneLeft(i) && si.State.Installed.Count == 1);
        string none = Game(root, "nothing required", V6);
        string noneBefore = Snapshot(none);
        var undoNone = new PatchUndo();
        var noneReport = GamePatcher.ApplyUndoable(V6, none, undoNone, null, fx.Payload);
        bool noneWrote = noneReport.Applied.Count == 2 && undoNone.HasWrites;
        var noneLeft = undoNone.Undo();
        Check("undoable: the optional entries' writes are kept too — a run that went through, taken back (as when the registration after it fails), leaves the folder as it was",
            noneWrote && noneLeft.Count == 0 && Snapshot(none) == noneBefore && !undoNone.HasWrites);

        // What the check could not see, made after it: the folder is ready by the check, then changes before the writes.
        // A required file given the system attribute (Windows copies over no such file).
        string sys = Game(root, "7.7, own.bin made a system file after the check", V7);
        Write(sys, OwnDst, "THE PLAYER'S BYTES");
        string? snapSys = null;
        var sSys = Service(fx.Payload, dir => { File.SetAttributes(P(dir, OwnDst), FileAttributes.System); snapSys = Snapshot(dir); });
        var eSys = Catch(() => sSys.Service.ImportFolder(v7, sys, progress));
        bool sysAsItWas = snapSys is not null && Snapshot(sys) == snapSys;
        File.SetAttributes(P(sys, OwnDst), FileAttributes.Normal);
        Check("after the check: a required file made a system file (Windows copies over none) — refused, the file as it was, the injector pair never went in, nothing registered",
            eSys is GamePatchException && sysAsItWas && ReadIf(P(sys, OwnDst)) == "THE PLAYER'S BYTES" && NoneLeft(sys)
            && !Directory.Exists(Path.Combine(sys, "ayy")) && sSys.State.Installed.Count == 0);

        // The identity file a byte patch is written into, mapped as a program's code by a process (Windows renames over
        // no such file), with the injector pair in the plan.
        var (mapped, mappedPayload, mappedUa) = MappedClient(Path.Combine(root, "mapped after the check"), pair: true);
        IntPtr module = IntPtr.Zero;
        string? snapMapped = null;
        var sMapped = Service(mappedPayload, dir => { module = NativeLibrary.Load(mappedUa); snapMapped = Snapshot(dir); });
        var eMapped = Catch(() => sMapped.Service.ImportFolder(v8, mapped, progress));
        bool mappedAsItWas = snapMapped is not null && Snapshot(mapped) == snapMapped;
        if (module != IntPtr.Zero) NativeLibrary.Free(module);
        Check("after the check: the byte patch's file mapped as a DLL — refused, the file as it was, the injector pair never went in, nothing registered",
            module != IntPtr.Zero && eMapped is GamePatchException && mappedAsItWas && NoneLeft(mapped)
            && !Directory.Exists(Path.Combine(mapped, "ayy")) && sMapped.State.Installed.Count == 0);

        // The byte patch's folder made unwritable, after the replacement before it went in.
        string denied = Game(root, "8.8, Native made unwritable after the check", V8, mhypbase: RpOriginal, fx: fx);
        IDisposable? deny = null;
        string? snapDenied = null;
        var sDenied = Service(fx.Payload, dir =>
        {
            deny = Deny(Path.GetDirectoryName(P(dir, UaDst))!, FileSystemRights.CreateFiles, directory: true);
            snapDenied = Snapshot(dir);
        });
        var eDenied = Catch(() => sDenied.Service.ImportFolder(v8, denied, progress));
        bool deniedAsItWas = snapDenied is not null && Snapshot(denied) == snapDenied;
        deny?.Dispose();
        Check("after the check: the byte patch's folder made unwritable — refused, the mhypbase.dll written before it back to the original, its .relic-orig gone, the injector pair never went in",
            deny is not null && eDenied is GamePatchException && deniedAsItWas && ReadIf(P(denied, RpDst)) == RpOriginal
            && !File.Exists(P(denied, RpDst) + GamePatcher.BackupSuffix) && NoneLeft(denied) && !Directory.Exists(Path.Combine(denied, "ayy")));

        // A write that stops halfway, as on a disk that fills up: the payload file cannot be read past a point. A copy
        // that fails partway removes the file it was written over, which gets its bytes back from the copy kept of it.
        string payloadHalf = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-undo-half"), new PayloadOptions { OwnBytes = 4 << 20 });
        string half = Game(root, "7.7, the copy over own.bin stops halfway", V7);
        Write(half, OwnDst, "THE PLAYER'S BYTES");
        FileStream? locked = null;
        string? snapHalf = null;
        var sHalf = Service(payloadHalf, dir =>
        {
            locked = new FileStream(Path.Combine(payloadHalf, "7.7", "own.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            locked.Lock((4 << 20) - 100_000, 50_000);
            snapHalf = Snapshot(dir);
        });
        var eHalf = Catch(() => sHalf.Service.ImportFolder(v7, half, progress));
        bool halfAsItWas = snapHalf is not null && Snapshot(half) == snapHalf;
        locked?.Dispose();
        Check("after the check: a copy over a file of the player's that stops halfway — refused, the file back with its bytes, no copy left, the injector pair never went in",
            locked is not null && eHalf is GamePatchException && halfAsItWas && ReadIf(P(half, OwnDst)) == "THE PLAYER'S BYTES" && NoneLeft(half)
            && !Directory.Exists(Path.Combine(half, "ayy")) && sHalf.State.Installed.Count == 0);

        // An optional entry whose copy stops halfway, in an import that goes through: its place is as it was.
        string payloadOpt = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-undo-optional"), new PayloadOptions());
        string opt = Game(root, "7.7, the copy over launcher.exe stops halfway", V7);
        Write(opt, Launcher, "AN EARLIER LAUNCHER");
        FileStream? lockedLauncher = null;
        var sOpt = Service(payloadOpt, _ =>
        {
            lockedLauncher = new FileStream(Path.Combine(payloadOpt, "common", "ayy", "anime", "build", "launcher.exe.bin"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            lockedLauncher.Lock(4, 4);
        });
        var eOpt = Catch(() => sOpt.Service.ImportFolder(v7, opt, progress));
        lockedLauncher?.Dispose();
        Check("after the check: an optional entry whose copy stops halfway, in an import that goes through — the file it was written over back as it was, no copy left, the rest in place, registered",
            lockedLauncher is not null && eOpt is null && ReadIf(P(opt, Launcher)) == "AN EARLIER LAUNCHER" && ReadIf(P(opt, Mhynot)) == "MHYNOT2-STUB"
            && ReadIf(P(opt, OwnDst)) == "OWN-7.7" && NoneLeft(opt) && sOpt.State.Installed.Count == 1);

        // An undo that cannot put a file back (another program holds it by then): the file is named, and the copy that
        // holds its bytes — the import's own, or the entry's .relic-orig — stays beside it.
        string strand = Game(root, "7.7, own.bin held when the undo comes", V7);
        Write(strand, OwnDst, "THE PLAYER'S BYTES");
        var undoStrand = new PatchUndo();
        GamePatcher.ApplyUndoable(V7, strand, undoStrand, null, fx.Payload);
        IReadOnlyList<string> leftStrand;
        using (new FileStream(P(strand, OwnDst), FileMode.Open, FileAccess.Read, FileShare.Read))
            leftStrand = undoStrand.Undo();
        undoStrand.Commit();
        string payloadStrand = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-undo-strand"), new PayloadOptions { OwnBackup = true });
        string strandOrig = Game(root, "7.7, own.bin backed up and held when the undo comes", V7);
        Write(strandOrig, OwnDst, "THE ORIGINAL");
        var undoStrandOrig = new PatchUndo();
        GamePatcher.ApplyUndoable(V7, strandOrig, undoStrandOrig, null, payloadStrand);
        IReadOnlyList<string> leftStrandOrig;
        using (new FileStream(P(strandOrig, OwnDst), FileMode.Open, FileAccess.Read, FileShare.Read))
            leftStrandOrig = undoStrandOrig.Undo();
        undoStrandOrig.Commit();
        Check("undo: a file the undo cannot put back (another program holds it) is named, and the copy holding its bytes — the import's own, or the .relic-orig — stays beside it, through the commit too",
            leftStrand.Contains(P(strand, OwnDst)) && ReadIf(P(strand, OwnDst) + GamePatcher.UndoSuffix) == "THE PLAYER'S BYTES"
            && leftStrandOrig.Contains(P(strandOrig, OwnDst)) && ReadIf(P(strandOrig, OwnDst) + GamePatcher.BackupSuffix) == "THE ORIGINAL"
            && !Directory.Exists(Path.Combine(strand, "ayy")) && !Directory.Exists(Path.Combine(strandOrig, "ayy")));
    }

    // ── K. cancelling a check ──

    static void Cancelling(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "cancel");
        string payload = Fixture.WriteManifest(fx, Path.Combine(tmp, "payload-big"), new PayloadOptions { OwnBytes = 128 << 20 });
        string game = Game(root, "7.7", V7);
        string before = Snapshot(game);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool preCancelled = Catch(() => ImportCheck.Run(Req(V7, game, fx, payload: payload), cancelled.Token)) is OperationCanceledException;
        bool shallowPreview = Catch(() => GamePatcher.Preview(V7, game, payload, deep: false, ct: cancelled.Token)) is OperationCanceledException;
        // Cancelled 10 ms after the check asked which clients run — its last step before Relic's files — so the token
        // stops the hash of the 128 MiB payload file, which is the plan's last entry: nothing after that hash would ask.
        using var midway = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var thrown = Catch(() => ImportCheck.Run(Req(V7, game, fx, payload: payload) with
        {
            RunningClients = () =>
            {
                midway.CancelAfter(10);
                return Array.Empty<string>();
            },
        }, midway.Token));
        long ms = clock.ElapsedMilliseconds;
        Check($"cancel: a check whose token is cancelled — before it starts, or while it hashes a 128 MiB payload file ({ms} ms) — ends with OperationCanceledException, nothing written; the preview's own loop stops too",
            preCancelled && shallowPreview && thrown is OperationCanceledException && Snapshot(game) == before);
    }

    // ── K2. the import's turn: no check has a file of the folder open while the import checks it and writes ──

    static void Turns(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "turns");
        var v7 = new GameVersionInfo { Id = V7 };
        var progress = new Progress<InstallProgress>(_ => { });

        // A check that runs when the import starts (held where it asks which clients run): stopped, and the import's own
        // check and its writes wait until it has ended.
        string game = Game(root, "7.7 checked while it is imported", V7);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int leftTheWait = 0, leftAtFirstWrite = -1, firstWrite = 0;
        Exception? checkEnd = null;
        ImportVerdict? checkVerdict = null;
        var held = Req(V7, game, fx) with
        {
            RunningClients = () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                Volatile.Write(ref leftTheWait, 1);
                return Array.Empty<string>();
            },
        };
        var checking = new Thread(() =>
        {
            try { checkVerdict = ImportCheck.Run(held); }
            catch (Exception ex) { checkEnd = ex; }
        });
        checking.Start();
        bool checkRuns = entered.Wait(TimeSpan.FromSeconds(30));
        var state = NewState();
        var service = new InstallService(state, "")
        {
            StatePath = Path.Combine(tmp, "turns-state", "state.json"), PayloadRoot = fx.Payload, OfferedVersions = Offered,
            BeforeFirstWrite = _ =>
            {
                Volatile.Write(ref leftAtFirstWrite, Volatile.Read(ref leftTheWait));
                Volatile.Write(ref firstWrite, 1);
            },
        };
        Exception? importEnd = null;
        var importing = new Thread(() => importEnd = Catch(() => service.ImportFolder(v7, game, progress)));
        importing.Start();
        Thread.Sleep(300);
        bool waited = importing.IsAlive && Volatile.Read(ref firstWrite) == 0 && !Directory.Exists(Path.Combine(game, "ayy"));
        release.Set();
        bool joined = importing.Join(TimeSpan.FromSeconds(30)) & checking.Join(TimeSpan.FromSeconds(30));
        Check("turn: a check that runs when the import starts is stopped (its own token never cancelled), and the import's check and writes wait until it has ended",
            checkRuns && waited && joined && checkEnd is OperationCanceledException && checkVerdict is null
            && importEnd is null && leftAtFirstWrite == 1 && state.Installed.Count == 1 && File.Exists(P(game, OwnDst)));

        // A check asked for while an import runs — of any folder — waits for the import's end; one whose own token is
        // cancelled meanwhile ends then.
        string other = Game(root, "7.7 asked about while another is imported", V7);
        string imported = Game(root, "7.7 imported while another is asked about", V7);
        ImportVerdict? during = null;
        Exception? cancelledEnd = null;
        bool waitedForTheImport = false, cancelledEnded = false;
        Thread? asked = null;
        using var stop = new CancellationTokenSource();
        var service2 = new InstallService(NewState(), "")
        {
            StatePath = Path.Combine(tmp, "turns-state-2", "state.json"), PayloadRoot = fx.Payload, OfferedVersions = Offered,
            BeforeFirstWrite = _ =>
            {
                asked = new Thread(() => during = ImportCheck.Run(Req(V7, other, fx)));
                asked.Start();
                var cancelled = new Thread(() => cancelledEnd = Catch(() => ImportCheck.Run(Req(V7, other, fx), stop.Token)));
                cancelled.Start();
                Thread.Sleep(150);
                stop.Cancel();
                cancelledEnded = cancelled.Join(TimeSpan.FromSeconds(5));
                Thread.Sleep(300);
                waitedForTheImport = asked.IsAlive && during is null;
            },
        };
        var importedEnd = Catch(() => service2.ImportFolder(v7, imported, progress));
        bool answered = asked?.Join(TimeSpan.FromSeconds(30)) == true;
        Check("turn: a check asked for while an import runs (of another folder) waits for the import's end, then answers; one whose own token is cancelled meanwhile ends",
            importedEnd is null && waitedForTheImport && answered && during is { Status: ImportVerdict.Ready }
            && cancelledEnded && cancelledEnd is OperationCanceledException);

        // A refused import lets the checks go on.
        var refused = Catch(() => service2.ImportFolder(v7, Path.Combine(root, "nowhere"), progress));
        var after = new Thread(() => during = ImportCheck.Run(Req(V7, other, fx)));
        during = null;
        after.Start();
        Check("turn: an import that is refused lets the checks go on at once",
            refused is InvalidOperationException && after.Join(TimeSpan.FromSeconds(10)) && during is { Status: ImportVerdict.Ready });
    }

    // ── L. the search: its rules ──

    static void SearchRules(Action<string, bool> Check)
    {
        const FileAttributes Dir = FileAttributes.Directory;
        Check("search rules: the system's folders at a drive's root, and only there",
            GameSearch.Prune(0, "", "", "windows", Dir, false) == GameSearch.PruneSystem
            && GameSearch.Prune(0, "", "", "programdata", Dir, false) == GameSearch.PruneSystem
            && GameSearch.Prune(0, "", "", "$recycle.bin", Dir, false) is not null
            && GameSearch.Prune(1, "games", "", "windows", Dir, false) is null
            && GameSearch.Prune(0, "", "", "games", Dir, false) is null);
        Check("search rules: the stores of tools anywhere, the system's own folders in Program Files, Users\\Default*, Users\\All Users and every AppData",
            new[] { "node_modules", ".git", "__pycache__", ".venv", "windowsapps", "winsxs", ".cache" }.All(n => GameSearch.Prune(4, "x", "y", n, Dir, false) == GameSearch.PruneTool)
            && GameSearch.Prune(1, "program files", "", "microsoft office", Dir, false) == GameSearch.PruneSystem
            && GameSearch.Prune(1, "program files (x86)", "", "windows kits", Dir, false) == GameSearch.PruneSystem
            && GameSearch.Prune(1, "program files", "", "hoyoplay", Dir, false) is null
            && GameSearch.Prune(1, "users", "", "default", Dir, false) == GameSearch.PruneSystem
            && GameSearch.Prune(1, "users", "", "all users", Dir, false) == GameSearch.PruneSystem
            && GameSearch.Prune(1, "users", "", "public", Dir, false) is null
            && GameSearch.Prune(2, "raf", "users", "appdata", Dir, false) == GameSearch.PruneAppData
            && GameSearch.Prune(3, "documents", "raf", "appdata", Dir, false) is null);
        Check("search rules: a link or a junction is never entered (a reparse point is one unless told otherwise), a reparse point that names no other place is; content not on this PC and hidden system folders are not",
            GameSearch.Prune(2, "a", "b", "loop", Dir | FileAttributes.ReparsePoint, false) == GameSearch.PruneLink
            && GameSearch.Prune(2, "a", "b", "loop", Dir | FileAttributes.ReparsePoint, false, () => true) == GameSearch.PruneLink
            && GameSearch.Prune(2, "a", "b", "synced", Dir | FileAttributes.ReparsePoint, false, () => false) is null
            && GameSearch.Prune(2, "a", "b", "online", Dir | (FileAttributes)0x00040000, false) == GameSearch.PruneNotLocal
            && GameSearch.Prune(2, "a", "b", "online", Dir | FileAttributes.Offline, false) == GameSearch.PruneNotLocal
            && GameSearch.Prune(2, "a", "b", "hs", Dir | FileAttributes.Hidden | FileAttributes.System, false) == GameSearch.PruneHiddenSystem
            && GameSearch.Prune(2, "a", "b", "hidden", Dir | FileAttributes.Hidden, false) is null
            && GameSearch.Prune(2, "a", "b", "other_data", Dir, true) == GameSearch.PruneUnityData);
        Check("search rules: the tags that name another place — a junction, a symbolic link, one that cannot be read — and those that do not (a cloud file's)",
            GameSearch.NamesAnotherPlace(0xA0000003) && GameSearch.NamesAnotherPlace(0xA000000C) && GameSearch.NamesAnotherPlace(uint.MaxValue)
            && !GameSearch.NamesAnotherPlace(0x9000001A) && !GameSearch.NamesAnotherPlace(0));
        Check("search rules: the order — depth first, then a name that promises a game, then games or downloads, then a folder on the way to a known game",
            GameSearch.Score(3, "genshin 2.8", false) < GameSearch.Score(1, "stuff", false)
            && GameSearch.Score(2, "games", false) < GameSearch.Score(2, "stuff", false)
            && GameSearch.Score(2, "stuff", true) == GameSearch.Score(2, "genshin", false)
            && GameSearch.Score(1, "stuff", false) < GameSearch.Score(2, "stuff", false));
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var folders = GameSearch.TraceFolders(new[]
        {
            @"D:\Games\Genshin 2.8\GenshinImpact.exe.FriendlyAppName",
            @"D:\Games\Genshin 2.8\GenshinImpact.exe.ApplicationCompany",
            "\"E:\\Other\\Genshin\\GenshinImpact.exe\"",
            GameSearch.Rot13(@"S:\Ebg\TrafuvaVzcnpg.rkr"),
            "{6D809377-6AF0-444B-8957-A3773F02200E}\\Genshin Impact game\\GenshinImpact.exe",
            "Microsoft.Windows.Explorer!App",
            @"D:\Games\MyGenshinImpact.exe",
            @"relative\GenshinImpact.exe",
            @"D:\Games\Not a game\notepad.exe",
        });
        Check("search rules: Windows' record — MuiCache names (their suffixes dropped, folded into one), quoted paths, a UserAssist name after ROT13, a known folder's {GUID} resolved; an application id, another exe that ends in the name, a relative name and other programs left out",
            GameSearch.Rot13(GameSearch.Rot13("Mixed Case 123")) == "Mixed Case 123"
            && folders.SequenceEqual(new[] { @"D:\Games\Genshin 2.8", @"E:\Other\Genshin", @"F:\Rot", Path.Combine(pf, "Genshin Impact game") }, StringComparer.OrdinalIgnoreCase));
        Check("search rules: the keys of the game's profile and of its launcher are never opened — HKCU\\Software\\Cognosphere\\HYP, Software\\miHoYo, however written — and none of the keys the search reads is one",
            GameSearch.IsForbiddenKey(@"Software\Cognosphere\HYP") && GameSearch.IsForbiddenKey(@"HKCU\Software\Cognosphere\HYP\1_0")
            && GameSearch.IsForbiddenKey(@"HKEY_CURRENT_USER\SOFTWARE\miHoYo\Genshin Impact") && GameSearch.IsForbiddenKey("software/cognosphere")
            && GameSearch.IsForbiddenKey(@"Software\WOW6432Node\miHoYo") && !GameSearch.IsForbiddenKey(@"Software\CognosphereX")
            && GameSearch.TraceKeys.Count == 7 && GameSearch.TraceKeys.All(k => !GameSearch.IsForbiddenKey(k)));

        // Windows' record read from a synthetic source: what is asked of it, and what is kept.
        string muiCache = GameSearch.TraceKeys[0], userAssist = GameSearch.TraceKeys[5], gameConfig = GameSearch.TraceKeys[6];
        var subKeys = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [userAssist] = new[] { "{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}", "{F4E57C4B-2036-45F0-A9AB-443BCFE33D9F}" },
            [gameConfig] = new[] { "child-a", "child-b" },
        };
        var valueNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [muiCache] = new[] { @"D:\Games\Genshin 2.8\GenshinImpact.exe.FriendlyAppName", @"C:\Windows\notepad.exe.FriendlyAppName", "LangID" },
            [userAssist + @"\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count"] = new[] { GameSearch.Rot13(@"E:\Other\GenshinImpact.exe"), GameSearch.Rot13(@"C:\Tools\other.exe") },
        };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [gameConfig + @"\child-a|MatchedExeFullPath"] = @"F:\G\GenshinImpact.exe",
            [gameConfig + @"\child-b|MatchedExeFullPath"] = @"F:\G\game.exe",
        };
        var asked = new List<string>();
        var source = new GameSearchTraceSource
        {
            SubKeys = p => { lock (asked) asked.Add(p); return subKeys.TryGetValue(p, out var s) ? s : null; },
            ValueNames = p => { lock (asked) asked.Add(p); return valueNames.TryGetValue(p, out var v) ? v : null; },
            Value = (p, name) => { lock (asked) asked.Add(p); return values.TryGetValue(p + "|" + name, out var v) ? v : null; },
        };
        var names = GameSearch.ReadTraceNames(source);
        Check("search rules: Windows' record, read from a synthetic source — only the names that hold GenshinImpact.exe are kept (MuiCache, UserAssist after ROT13, GameConfigStore's MatchedExeFullPath), and nothing is asked of a key outside the record's own",
            names.SequenceEqual(new[] { @"D:\Games\Genshin 2.8\GenshinImpact.exe.FriendlyAppName", @"E:\Other\GenshinImpact.exe", @"F:\G\GenshinImpact.exe" })
            && asked.Count > 0 && asked.All(p => GameSearch.TraceKeys.Any(k => p.Equals(k, StringComparison.OrdinalIgnoreCase)
                                                                              || p.StartsWith(k + @"\", StringComparison.OrdinalIgnoreCase)))
            && asked.All(p => !GameSearch.IsForbiddenKey(p)));
    }

    // ── M. the search: a walk ──

    sealed class Collector
    {
        public readonly List<GameSearchHit> Hits = new();
        public readonly List<GameSearchProgress> Progress = new();
        public readonly List<GameSearchDone> Done = new();
        public readonly List<int> Searches = new();
        public readonly ManualResetEventSlim Ended = new();
        public int AfterDone;
        public void Found(GameSearchHit h) { lock (this) { if (Done.Count > 0) AfterDone++; Hits.Add(h); Searches.Add(h.Search); } }
        public void Moved(GameSearchProgress p) { lock (this) { if (Done.Count > 0) AfterDone++; Progress.Add(p); Searches.Add(p.Search); } }
        public void Finished(GameSearchDone d) { lock (this) { if (Done.Count > 0) AfterDone++; Done.Add(d); Searches.Add(d.Search); } Ended.Set(); }
        public GameSearchHit? At(string dir) { lock (this) return Hits.FirstOrDefault(h => string.Equals(h.Dir, dir, StringComparison.OrdinalIgnoreCase)); }
    }

    static int StartAndWait(GameSearch search, GameSearchContext context, bool deeper, Collector c, int seconds = 60)
    {
        int id = search.Start(context, deeper, c.Found, c.Moved, c.Finished);
        return id > 0 && c.Ended.Wait(TimeSpan.FromSeconds(seconds)) ? id : 0;
    }

    static void SearchWalk(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string tree = Path.Combine(tmp, "tree");
        string registered = Game(tree, @"a\Genshin 7.7", V7);
        Game(registered, @"GenshinImpact_Data\inside", V7);                  // a client inside a client: never reported
        string copy = Game(tree, @"b\c\d\copy", V7);                          // depth 4: a copy of the registered version
        string seven = Game(tree, @"x\y\z\w\v\u\seven", V8, fx: fx);          // depth 7
        string eight = Game(tree, @"e1\e2\e3\e4\e5\e6\e7\eight", V7);          // depth 8: the depth limit itself
        string nine = Game(tree, @"n1\n2\n3\n4\n5\n6\n7\n8\nine", V6);         // depth 9: one past it
        string deep = Game(tree, @"d1\d2\d3\d4\d5\d6\d7\d8\d9\ten", V6);       // depth 10
        string live = Game(tree, "live", "noline", config: "9.1");
        string unknown = Game(tree, "unknown", "x");
        string noList = Game(tree, "nolist", null);
        string configOnly = Game(tree, "configonly", null, config: V6);
        string contra = Game(tree, "contra", V7, config: V6);
        string resized = Game(tree, "resized", V6, ua: new byte[1499]);
        string cloudList = Game(tree, "cloudlist", V7);                        // its pkg_version not on this PC
        string synced = Game(tree, @"synced\client", V7);                     // under a reparse point that is no link
        Game(tree, "exeonly", V7, data: false);
        Game(tree, "dataonly", V7, exe: false);
        Game(tree, @"node_modules\pkg", V7);
        Game(tree, @"proj\.git\objects", V7);
        Game(tree, "Windows", V7);                                            // at the root: the system's
        Game(tree, @"unity\Other_Data\client", V7);                           // inside the data folder of a Unity program
        Write(Path.Combine(tree, "unity"), "Other.exe", "a Unity program");
        string hidden = Path.Combine(tree, "hiddensys");
        Game(hidden, "client", V7);
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
        string loop = Path.Combine(tree, "loop");
        bool junction = Junction(loop, tree);
        string syncedDir = Path.Combine(tree, "synced");

        // The listing and the entry probe of this walk: a pkg_version whose content is not on this PC, and a reparse point
        // that names no other place (a cloud folder's tag) — both as a cloud provider would show them.
        var listed = new List<string>();
        var probed = new List<string>();
        IReadOnlyList<GameSearchEntry> Lister(string dir, CancellationToken ct)
        {
            lock (listed) listed.Add(dir);
            var entries = List(dir, ct).ToList();
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(dir, cloudList, StringComparison.OrdinalIgnoreCase) && entries[i].Name == ClientIdentity.ListFile)
                    entries[i] = entries[i] with { Attributes = entries[i].Attributes | (FileAttributes)0x00400000 };
                if (string.Equals(dir, tree, StringComparison.OrdinalIgnoreCase) && entries[i].Name == "synced")
                    entries[i] = entries[i] with { Attributes = entries[i].Attributes | FileAttributes.ReparsePoint };
            }
            return entries;
        }
        GameSearchEntryInfo? EntryInfo(string path)
        {
            lock (probed) probed.Add(path);
            if (string.Equals(path, syncedDir, StringComparison.OrdinalIgnoreCase))
                return new GameSearchEntryInfo(FileAttributes.Directory | FileAttributes.ReparsePoint, 0x9000001A);
            if (!Directory.Exists(path) && !File.Exists(path)) return null;
            var attributes = File.GetAttributes(path);
            return new GameSearchEntryInfo(attributes, (attributes & FileAttributes.ReparsePoint) != 0 ? 0xA0000003u : 0u);
        }

        var context = new GameSearchContext(new[] { new ImportRegistration(V7, registered) }, Path.Combine(tmp, "no-install-root"),
            Offered, fx.Payload);
        var search = new GameSearch(new GameSearchOptions { Roots = new[] { tree }, Places = false, DepthLimit = 8, Lister = Lister, EntryInfo = EntryInfo });
        var first = new Collector();
        int firstId = StartAndWait(search, context, false, first);
        var done = first.Done.FirstOrDefault();
        Check("search: a walk of a synthetic tree ends, once, and nothing comes after its end; every event names the search Start answered",
            firstId > 0 && first.Done.Count == 1 && first.AfterDone == 0 && !search.IsRunning && done is { Stopped: false, Budget: false, Error: "" }
            && first.Searches.All(s => s == firstId));
        Check("search: the junction that loops back to the tree is made (mklink /J, no rights needed) and not followed — every folder reported once",
            junction && first.Hits.Count == first.Hits.Select(h => h.Dir.ToLowerInvariant()).Distinct().Count()
            && !listed.Any(d => d.StartsWith(loop, StringComparison.OrdinalIgnoreCase)));
        var expected = new[] { registered, copy, seven, eight, live, unknown, noList, configOnly, contra, resized, synced };
        Check("search: the clients down to the depth limit — the one at the limit itself included — are found, as \"disk\" finds; one under a reparse point that is no link too",
            expected.All(d => first.At(d) is { Source: GameSearch.SourceDisk }) && first.Hits.Count == expected.Length);
        Check("search: a client inside a client, exe without data, data without exe, node_modules, .git, a hidden system folder, the root's Windows, a Unity program's data folder and a client whose file list is not on this PC are not reported",
            first.Hits.All(h => !h.Dir.Contains("inside") && !h.Dir.Contains("exeonly") && !h.Dir.Contains("dataonly")
                && !h.Dir.Contains("node_modules") && !h.Dir.Contains(".git") && !h.Dir.Contains("hiddensys")
                && !h.Dir.EndsWith(@"\Windows", StringComparison.OrdinalIgnoreCase) && !h.Dir.Contains("Other_Data"))
            && first.At(cloudList) is null);
        Check("search: the registered folder is \"registered\", its copy \"use\" and told where the version is installed",
            first.At(registered) is { Status: GameSearch.StatusRegistered, RegisteredAs: V7, Version: V7 }
            && first.At(copy) is { Status: GameSearch.StatusUse, Version: V7, Build: "7.7.0", Proof: GameSearch.ProofIdentity, Offered: true } c
            && c.InstalledAt == registered && c.Note == L.T("core.find.note.installedAt", new { version = V7, dir = registered }));
        Check("search: a client of an exact version proven by its list and its identity file's size -> use; one whose file has another size -> use, its proof \"list\", with a note",
            first.At(seven) is { Status: GameSearch.StatusUse, Version: V8, Proof: GameSearch.ProofIdentity, Note: "" }
            && first.At(resized) is { Status: GameSearch.StatusUse, Proof: GameSearch.ProofList } r
            && r.Note == L.T("core.install.inspect.identityFile", new { file = UaDst }));
        Check("search: the shape of the official client (config.ini of a version Relic does not offer) and a folder whose config.ini and list disagree -> none, never offered",
            first.At(live) is { Status: GameSearch.StatusNone, Version: "9.1", Offered: false } l
            && l.Note == L.T("core.find.note.notOffered", new { version = "9.1" })
            && first.At(contra) is { Status: GameSearch.StatusNone } k
            && k.Note == L.T("core.find.note.contradiction", new { config = V6, version = V7 }));
        Check("search: a build no row names and a folder without a list -> pick (the version is the player's to say); config.ini alone -> pick with that version",
            first.At(unknown) is { Status: GameSearch.StatusPick, Version: "", Proof: GameSearch.ProofNone } u && u.Note == L.T("core.find.note.unknown")
            && first.At(noList) is { Status: GameSearch.StatusPick, Version: "" }
            && first.At(configOnly) is { Status: GameSearch.StatusPick, Version: V6, Proof: GameSearch.ProofConfig } co
            && co.Note == L.T("core.find.note.configOnly", new { version = V6 }));
        Check("search: the clients past the depth limit are not found, and the end says a deeper search can find more",
            first.At(nine) is null && first.At(deep) is null && done is { Deeper: true });
        Check("search: progress came as the walk went, about its one disk, the first one at its start",
            first.Progress.Count > 0 && first.Progress[0].Phase == GameSearch.PhasePlaces && first.Progress[0].Disks.Count == 0
            && first.Progress.Skip(1).All(p => p.Phase == GameSearch.PhaseDisks && p.Disks.Count == 1 && p.Disks[0].Name == tree)
            && done is { Dirs: > 0 });

        var second = new Collector();
        int secondId = StartAndWait(search, context, true, second);
        Check("search: a deeper search goes on from where the limit stopped — it finds the clients at depth 9 and 10, posts nothing found before, and names its own search",
            secondId > firstId && second.Hits.Count == 2 && second.At(nine) is { Status: GameSearch.StatusUse, Version: V6 }
            && second.At(deep) is { Status: GameSearch.StatusUse, Version: V6 }
            && second.Done.Single() is { Deeper: false, Stopped: false } && second.Done[0].Dirs < first.Done[0].Dirs
            && second.Searches.All(s => s == secondId));
        var fresh = new Collector();
        StartAndWait(search, context, false, fresh);
        Check("search: a fresh search posts everything again",
            fresh.Hits.Count == expected.Length);

        // The sure places come first: Relic's own folders (a registration, the install folder's sub-folders), the running
        // clients, Windows' record of programs started — each reached only as the walk would reach it.
        string relicRoot = Path.Combine(tmp, "relicroot");
        string fromRoot = Game(relicRoot, "Genshin 6.6", V6);
        var placesListed = new List<string>();
        var placesProbed = new List<string>();
        var places = new GameSearch(new GameSearchOptions
        {
            Roots = new[] { tree }, DepthLimit = 8,
            Lister = (dir, ct) => { lock (placesListed) placesListed.Add(dir); return List(dir, ct); },
            EntryInfo = path =>
            {
                lock (placesProbed) placesProbed.Add(path);
                if (!Directory.Exists(path) && !File.Exists(path)) return null;
                var attributes = File.GetAttributes(path);
                return new GameSearchEntryInfo(attributes, (attributes & FileAttributes.ReparsePoint) != 0 ? 0xA0000003u : 0u);
            },
            RunningClients = () => new[] { Path.Combine(seven, "GenshinImpact.exe") },
            TraceNames = () => new[]
            {
                Path.Combine(copy, "GenshinImpact.exe") + ".FriendlyAppName",
                @"\\server\share\Genshin\GenshinImpact.exe",
                Path.Combine(tree, "gone", "GenshinImpact.exe"),
                Path.Combine(loop, "unknown", "GenshinImpact.exe"),
                Path.Combine(ShortPath(resized), "GenshinImpact.exe"),
            },
        });
        var placesContext = context with { InstallRoot = relicRoot };
        var p = new Collector();
        int placesId = StartAndWait(places, placesContext, false, p);
        int firstDisk = p.Hits.FindIndex(h => h.Source == GameSearch.SourceDisk);
        // This walk lists the tree as it is: the "cloudlist" client's file list is on this PC here, so it is found too.
        Check("search: the sure places first — the registered folder and a folder of Relic's install folder (Relic), a running client, folders Windows' record names — before any folder the walk finds",
            p.At(registered) is { Source: GameSearch.SourceRelic } && p.At(fromRoot) is { Source: GameSearch.SourceRelic, Version: V6 }
            && p.At(seven) is { Source: GameSearch.SourceRunning } && p.At(copy) is { Source: GameSearch.SourceTraces }
            && firstDisk == 5 && p.Hits.Take(5).All(h => h.Source != GameSearch.SourceDisk)
            && p.At(cloudList) is { Source: GameSearch.SourceDisk } && p.Hits.Count == expected.Length + 2);
        Check("search: a folder a record names by its short 8.3 name (which the full path of it expands) is posted once — the walk, reaching it by its own path, knows it",
            p.Hits.Count(h => ImportCheck.SameDir(h.Dir, resized)) == 1
            && p.Hits.Single(h => ImportCheck.SameDir(h.Dir, resized)).Source == GameSearch.SourceTraces);
        Check("search: a place a record names through a junction is not reached that way (the walk finds that folder by its own path), and a network path is never touched — not listed, not probed",
            p.At(unknown) is { Source: GameSearch.SourceDisk } && p.Hits.All(h => !h.Dir.Contains(@"\loop\", StringComparison.OrdinalIgnoreCase))
            && placesListed.Concat(placesProbed).All(x => !x.StartsWith(@"\\", StringComparison.Ordinal))
            && placesProbed.Any(x => string.Equals(x, loop, StringComparison.OrdinalIgnoreCase)));
        Check("search: with the sure places, the first event is the progress of the places, naming the search",
            p.Progress.Count > 0 && p.Progress[0] is { Phase: GameSearch.PhasePlaces } pp && pp.Search == placesId
            && p.Searches.All(s => s == placesId));

        // Best-first: a folder whose name promises a game is reached before a bulk of others at the same depth.
        string bulk = Path.Combine(tmp, "bulk");
        for (int i = 0; i < 150; i++) Directory.CreateDirectory(Path.Combine(bulk, $"folder{i:000}", "sub"));
        string promising = Game(bulk, @"games\genshin\Genshin Impact 7.7", V7);
        int listedCount = 0, listedAtHit = -1;
        var counting = new GameSearch(new GameSearchOptions
        {
            Roots = new[] { bulk }, Places = false,
            Lister = (dir, ct) =>
            {
                int count = Interlocked.Increment(ref listedCount);
                if (string.Equals(dir, promising, StringComparison.OrdinalIgnoreCase)) listedAtHit = count;
                return List(dir, ct);
            },
        });
        var b = new Collector();
        counting.Start(context, false, b.Found, null, b.Finished);
        b.Ended.Wait(TimeSpan.FromSeconds(30));
        Check($"search: best-first — the promising folder three levels down is found after {listedAtHit} listings, not after the 150 folders beside it ({listedCount} listed in all)",
            b.At(promising) is not null && listedAtHit is > 0 and <= 6 && listedCount > 300);
        if (junction) Directory.Delete(loop);
    }

    // ── N. Stop ──

    static void SearchStop(string tmp, Action<string, bool> Check)
    {
        string wide = Path.Combine(tmp, "wide");
        for (int i = 0; i < 40; i++)
            for (int j = 0; j < 40; j++) Directory.CreateDirectory(Path.Combine(wide, $"d{i:00}", $"e{j:00}"));
        Game(wide, @"d39\e39\late", V7); // reached only at the very end of the walk
        GameSearch? search = null;
        int listed = 0, listedAtStop = -1, stopAt = 100;
        bool stopAnswer = false;
        long stopMs = -1;
        var c = new Collector();
        search = new GameSearch(new GameSearchOptions
        {
            Roots = new[] { wide }, Places = false,
            Lister = (dir, ct) =>
            {
                if (Interlocked.Increment(ref listed) == Volatile.Read(ref stopAt))
                {
                    var sw = Stopwatch.StartNew();
                    stopAnswer = search!.Stop();
                    stopMs = sw.ElapsedMilliseconds;
                    listedAtStop = Volatile.Read(ref listed);
                }
                return List(dir, ct);
            },
        });
        var context = new GameSearchContext(Array.Empty<ImportRegistration>(), "", Offered);
        var clock = Stopwatch.StartNew();
        search.Start(context, false, c.Found, c.Moved, c.Finished);
        bool ended = c.Ended.Wait(TimeSpan.FromSeconds(30));
        Thread.Sleep(300);
        Check($"stop: Stop in the middle of the walk ends it at once ({stopMs} ms) — stopped, a deeper search offered, its end the last event",
            stopAnswer && ended && stopMs is >= 0 and < 200 && c.Done.Single() is { Stopped: true, Deeper: true } && !search.IsRunning
            && c.AfterDone == 0);
        Check($"stop: no folder is listed after the one that was being listed ({listed} listed, {listedAtStop} at Stop) and nothing is posted after the end",
            listed == listedAtStop && c.AfterDone == 0 && c.Hits.Count == 0);
        Check("stop: a second Stop answers that nothing runs", !search.Stop());

        // After a Stop a deeper search walks from the roots: what an earlier search left is no longer known.
        int limitedStopAt = -1000000; // counts up to 20 only in the run that is to be stopped
        GameSearch? stopping = null;
        var stopped = new GameSearch(new GameSearchOptions
        {
            Roots = new[] { wide }, Places = false, DepthLimit = 1,
            Lister = (dir, ct) =>
            {
                if (Interlocked.Increment(ref limitedStopAt) == 20) stopping!.Stop();
                return List(dir, ct);
            },
        });
        stopping = stopped;
        var s1 = new Collector();
        StartAndWait(stopped, context, false, s1);         // the depth limit leaves the e## folders
        var s2 = new Collector();
        Interlocked.Exchange(ref limitedStopAt, 0);
        StartAndWait(stopped, context, true, s2);          // a deeper search, stopped after 20 listings
        Interlocked.Exchange(ref limitedStopAt, -1000000);
        var s3 = new Collector();
        StartAndWait(stopped, context, true, s3);          // after the Stop: from the roots again
        Check($"stop: a deeper search after a stopped one walks from the roots again ({s3.Done.SingleOrDefault()?.Dirs} folders), not from what an earlier search left, and finds what the limit and the Stop kept from it",
            s1.Done.Single() is { Stopped: false, Deeper: true } && s2.Done.Single() is { Stopped: true }
            && s3.Done.Single() is { Stopped: false } d3 && d3.Dirs >= 1 + 40 + 40 * 40
            && s3.Hits.Count == 1 && s3.At(Path.Combine(wide, "d39", "e39", "late")) is not null);
    }

    /// <summary>A worker held in a folder the system is slow to open (a disk spinning up): Stop does not wait for it.</summary>
    static void SearchHeldWorker(string tmp, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "held");
        string slow = Game(root, "slow", V7);
        Directory.CreateDirectory(Path.Combine(root, "other"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var search = new GameSearch(new GameSearchOptions
        {
            Roots = new[] { root }, Places = false,
            Lister = (dir, ct) =>
            {
                if (string.Equals(dir, slow, StringComparison.OrdinalIgnoreCase))
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30)); // deaf to the token, as a blocked open is
                }
                return List(dir, CancellationToken.None);
            },
        });
        var c = new Collector();
        var context = new GameSearchContext(Array.Empty<ImportRegistration>(), "", Offered);
        search.Start(context, false, c.Found, c.Moved, c.Finished);
        bool held = entered.Wait(TimeSpan.FromSeconds(30));
        var sw = Stopwatch.StartNew();
        bool stopped = search.Stop();
        long ms = sw.ElapsedMilliseconds;
        bool endedAtOnce = c.Ended.Wait(TimeSpan.FromMilliseconds(500));
        long endMs = sw.ElapsedMilliseconds;
        release.Set();
        Thread.Sleep(400);
        Check($"held worker: Stop does not wait for a worker held in a folder ({ms} ms): the end follows at once ({endMs} ms)",
            held && stopped && ms < 200 && endedAtOnce && c.Done.Single().Stopped);
        Check("held worker: what that worker finds once it is let go (a client, there) is never posted",
            c.Hits.Count == 0 && c.AfterDone == 0);
        var again = new Collector();
        Check("held worker: a new search starts while the old worker is gone or still finishing",
            StartAndWait(search, context, false, again) > 0 && again.At(slow) is not null);
    }

    // ── O. the time budget ──

    static void SearchBudget(string tmp, Fixture fx, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "budget");
        var dirs = new List<string>();
        for (int i = 0; i < 6; i++) dirs.Add(Game(root, $@"g{i}\h\Genshin {i}", V7));
        dirs.Add(Game(root, @"k1\k2\k3\k4\k5\k6\k7\k8\k9\Genshin deep", V7));
        for (int i = 0; i < 30; i++) Directory.CreateDirectory(Path.Combine(root, $"empty{i:00}"));
        var context = new GameSearchContext(Array.Empty<ImportRegistration>(), "", Offered, fx.Payload);
        var whole = new Collector();
        StartAndWait(new GameSearch(new GameSearchOptions { Roots = new[] { root }, Places = false, DepthLimit = 100 }), context, false, whole);
        int wholeDirs = whole.Done.Single().Dirs;
        bool slow = true;
        var search = new GameSearch(new GameSearchOptions
        {
            Roots = new[] { root }, Places = false, DepthLimit = 8, Budget = TimeSpan.FromMilliseconds(150),
            ProgressEvery = TimeSpan.FromMilliseconds(10),
            Lister = (dir, ct) =>
            {
                if (Volatile.Read(ref slow)) Thread.Sleep(60);
                return List(dir, ct);
            },
        });
        var first = new Collector();
        bool ended = StartAndWait(search, context, false, first) > 0;
        Volatile.Write(ref slow, false);
        var second = new Collector();
        bool resumed = StartAndWait(search, context, true, second) > 0;
        var all = first.Hits.Concat(second.Hits).Select(h => h.Dir).ToList();
        Check($"budget: a spent time budget ends the search and says more can be searched ({first.Hits.Count} found before it)",
            ended && first.Done.Single() is { Budget: true, Deeper: true, Stopped: false } && first.Hits.Count < dirs.Count);
        Check($"budget: the deeper search goes on from what the first handed back ({first.Done.Single().Dirs} + {second.Done.SingleOrDefault()?.Dirs} folders of {wholeDirs}) and finds the rest, each folder once, the one past the depth limit too",
            resumed && second.Done.Single() is { Budget: false } d2 && d2.Dirs < wholeDirs && all.Count == dirs.Count
            && dirs.All(d => all.Contains(d, StringComparer.OrdinalIgnoreCase)));
    }

    // ── P. the texts ──

    static void Texts(string? repoRoot, Action<string, bool> Check)
    {
        string? repo = string.IsNullOrWhiteSpace(repoRoot) ? FindRepo() : repoRoot;
        if (repo is null)
        {
            Check("texts: the repository is found (a folder holding Relic.slnx above here)", false);
            return;
        }
        string lang = Path.Combine(repo, "app", "Relic.App", "ui", "lang");
        var texts = Flat(Path.Combine(lang, "en.json"));
        string fragments = Path.Combine(lang, "fragments");
        var fragmentTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        bool fragmentsFlat = true;
        if (Directory.Exists(fragments))
            foreach (string f in Directory.GetFiles(fragments, "*.en.json"))
            {
                try { foreach (var kv in Flat(f)) fragmentTexts.TryAdd(kv.Key, kv.Value); }
                catch (Exception) { fragmentsFlat = false; }
            }
        bool agree = fragmentTexts.All(kv => !texts.TryGetValue(kv.Key, out var t) || t == kv.Value);
        foreach (var kv in fragmentTexts) texts.TryAdd(kv.Key, kv.Value);
        var missing = TextKeys.Keys.Where(k => !texts.ContainsKey(k)).ToList();
        var wrongPlaceholders = TextKeys.Where(kv => texts.TryGetValue(kv.Key, out var t)
            && !Regex.Matches(t, @"\{([A-Za-z0-9_]+)\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x)
                .SequenceEqual(kv.Value.OrderBy(x => x))).Select(kv => kv.Key).ToList();
        Check($"texts: every text of the check and of the search is in en.json or a fragment beside it ({TextKeys.Count} keys)"
              + (missing.Count > 0 ? $" — missing: {string.Join(", ", missing)}" : ""),
            missing.Count == 0 && fragmentsFlat && agree);
        Check("texts: each takes exactly the placeholders the code fills" + (wrongPlaceholders.Count > 0 ? $" — {string.Join(", ", wrongPlaceholders)}" : ""),
            wrongPlaceholders.Count == 0);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in new[] { "ImportCheck.cs", "GameSearch.cs" })
        {
            string path = Path.Combine(repo, "app", "Relic.Core", "Install", file);
            if (File.Exists(path))
                foreach (Match m in Regex.Matches(File.ReadAllText(path), "L\\.T\\(\"([A-Za-z0-9_.\\-]+)\"")) used.Add(m.Groups[1].Value);
        }
        var unlisted = used.Where(k => !TextKeys.ContainsKey(k) && k != "core.install.import.wrongBuild").ToList();
        Check("texts: every text ImportCheck.cs and GameSearch.cs use is listed here" + (unlisted.Count > 0 ? $" — {string.Join(", ", unlisted)}" : ""),
            used.Count > 25 && unlisted.Count == 0);
    }

    static Dictionary<string, string> Flat(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException(p.Name);
            d[p.Name] = p.Value.GetString() ?? "";
        }
        return d;
    }

    // ── helpers ──

    sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>A deny rule for this user on <paramref name="path"/>, taken away again when disposed.</summary>
    static IDisposable Deny(string path, FileSystemRights rights, bool directory)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        if (directory)
        {
            var info = new DirectoryInfo(path);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(rule);
            info.SetAccessControl(acl);
            return new Restore(() =>
            {
                var now = info.GetAccessControl();
                now.RemoveAccessRule(rule);
                info.SetAccessControl(now);
            });
        }
        var file = new FileInfo(path);
        var fileAcl = file.GetAccessControl();
        fileAcl.AddAccessRule(rule);
        file.SetAccessControl(fileAcl);
        return new Restore(() =>
        {
            var now = file.GetAccessControl();
            now.RemoveAccessRule(rule);
            file.SetAccessControl(now);
        });
    }

    static IReadOnlyList<GameSearchEntry> List(string dir, CancellationToken ct)
    {
        var list = new List<GameSearchEntry>();
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            ct.ThrowIfCancellationRequested();
            list.Add(new GameSearchEntry(e.Name, (e.Attributes & FileAttributes.Directory) != 0, e.Attributes,
                e is FileInfo f ? f.Length : 0));
        }
        return list;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    static extern uint GetShortPathName(string longPath, StringBuilder shortPath, int size);

    /// <summary>The path by its short (8.3) names, where the volume keeps them (else the path as it is).</summary>
    static string ShortPath(string path)
    {
        var buffer = new StringBuilder(1024);
        uint n = GetShortPathName(path, buffer, buffer.Capacity);
        return n > 0 && n < buffer.Capacity ? buffer.ToString() : path;
    }

    static bool Junction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception) { return false; }
    }

    static void Clean(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var d in new DirectoryInfo(dir).EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                try { d.Delete(); } catch (Exception) { }
                continue;
            }
            if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly)) != 0)
                d.Attributes = FileAttributes.Directory;
        }
        foreach (string f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    static string Write(string root, string relative, string content)
    {
        string path = P(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    static string WriteBytes(string root, string relative, byte[] content)
    {
        string path = P(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    static string Read(string path) => File.ReadAllText(path);

    /// <summary>The text of the file, or null when it is not there — for a check about a file a failed write may have
    /// removed.</summary>
    static string? ReadIf(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    static string Sha(string path) => ShaOf(File.ReadAllBytes(path));

    static string ShaOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Every file of the folder with its hash and attributes, and every folder: what a run that writes nothing
    /// leaves as it was.</summary>
    static string Snapshot(string dir) => string.Join("\n",
        Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(dir, f) + (File.Exists(f) ? " " + Sha(f) + " " + File.GetAttributes(f) : " <dir>")));

    static Exception? Catch(Action body)
    {
        try { body(); return null; }
        catch (Exception ex) { return ex; }
    }

    static string? FindRepo()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Relic.slnx"))) return dir.FullName;
        return null;
    }
}
