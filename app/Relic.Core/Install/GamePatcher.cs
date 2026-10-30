using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relic.Core.Util;

namespace Relic.Core.Install;

/// <summary>
/// A bundled fix the client cannot start without could not be applied. A dedicated type, and
/// deliberately NOT <see cref="InvalidDataException"/>: Backend keys the corrupt-zip-cache cleanup on
/// that exact type, and nothing here has anything to do with a downloaded archive.
/// </summary>
public sealed class GamePatchException : Exception
{
    public GamePatchException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>One entry of payload/manifest.json: a file Relic copies into the game folder, or — kind
/// <see cref="GamePatcher.BytePatchKind"/> — a run of bytes it writes into a file the game folder
/// already holds.</summary>
public sealed class PatchEntry
{
    public string Src { get; set; } = "";
    public string Dst { get; set; } = "";
    /// <summary>Content address of the file: a destination that already carries it is left alone. An
    /// entry without one cannot be recognised as current, so it is re-copied on every run. For a byte
    /// patch it is the hash of the whole destination with the bytes in it.</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>How the entry reaches the game folder. Absent or empty: <see cref="Src"/> is copied
    /// over <see cref="Dst"/>. <see cref="GamePatcher.BytePatchKind"/>: Src holds <see cref="Length"/>
    /// bytes, written at <see cref="Offset"/> of the Dst the player already has. Any other value makes
    /// the manifest unusable — an entry Relic cannot read must never be applied as a copy.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Byte patch: the length of the destination, the same before and after.</summary>
    public long Size { get; set; }
    /// <summary>Byte patch: where in the destination the bytes go.</summary>
    public long Offset { get; set; }
    /// <summary>Byte patch: how many bytes — the length of Src.</summary>
    public int Length { get; set; }
    /// <summary>The hash of the whole destination as the game build ships it. A byte patch writes its
    /// bytes into that file only. A plain entry that carries it REPLACES that file of the game: it is
    /// written over the original or over an earlier payload file of its own (<see cref="Sha256Earlier"/>),
    /// and a destination with any other content is the player's own file, left exactly as it is unless the
    /// player chooses Relic's file (<see cref="GamePatcher.ReplaceOwnFiles"/>).</summary>
    public string Sha256Before { get; set; } = "";
    /// <summary>A replacement (a plain entry with <see cref="Sha256Before"/>): the hashes of the earlier
    /// payload files of this entry, which a newer one may be written over.</summary>
    public List<string> Sha256Earlier { get; set; } = new();
    /// <summary>Byte patch: the hash of Src, which is also the hash of that span of a destination
    /// that carries the patch.</summary>
    public string SpanSha256 { get; set; } = "";
    /// <summary>Byte patch: the hash of the same span of the unmodified destination.</summary>
    public string SpanSha256Before { get; set; } = "";
    /// <summary>The entry is listed under the manifest's "versions" — it belongs to one game version —
    /// and not under "common". Set by the loader, never read from the manifest.</summary>
    [JsonIgnore] public bool PerVersion { get; internal set; }
    /// <summary>Keep whatever was at Dst as "&lt;dst&gt;.relic-orig" the FIRST time we replace it.</summary>
    public bool Backup { get; set; }
    /// <summary>The client does not start without it, so a missing payload copy is a hard failure
    /// rather than a warning.</summary>
    public bool Required { get; set; }
    /// <summary>Injected by launcher.exe after mhynot2.dll when the in-game enhancements are on
    /// (<c>RelicSettings.Enhancements</c>). Such an entry is copied/verified only when the caller asks
    /// for injectables — toggle OFF means the file is never put in place, never checked, never deleted.</summary>
    public bool Inject { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>An entry a preview found it could not write: <paramref name="Problem"/> is one of
/// <see cref="WriteAccess.Denied"/>, <see cref="WriteAccess.Attributes"/>, <see cref="WriteAccess.InUse"/> or
/// <see cref="WriteAccess.InTheWay"/>.</summary>
public sealed record PatchBlock(string Dst, string Problem);

/// <summary>
/// What <see cref="GamePatcher.Apply"/> would do to a game folder, found out without writing anything
/// (<see cref="GamePatcher.Preview"/>). <see cref="Refusal"/>: the text Apply would refuse the run with, or null.
/// <see cref="Report"/>: the run's report — Applied = what Apply would write, the rest as Apply fills it — plus
/// <see cref="PatchReport.Unsettled"/> and <see cref="PatchReport.Blocked"/>. <see cref="HasPlan"/>: this build ships a
/// plan for the version (a payload with at least one entry). <see cref="Deep"/>: whole files were hashed, as Apply does.
/// <see cref="BytesNeeded"/>: the room the writes of <see cref="PatchReport.Applied"/> take on the game folder's drive at
/// their fullest — what they add to it for good, and the most one of them takes beyond that on its way (a copy beside
/// the file it takes the place of).
/// </summary>
public sealed record PatchPreview(string? Refusal, PatchReport Report, bool HasPlan, bool Deep, long BytesNeeded = 0);

/// <summary>
/// The writes of one <see cref="GamePatcher.ApplyUndoable"/> run, kept so that the run can be taken back whole
/// (<see cref="Undo"/>) — when it throws, or when what follows it (the import's registration) fails: a refused import
/// leaves the game folder as it was. Every step is written down BEFORE the write it is about, so a write that stops
/// halfway is taken back too: a file that was not there is removed again; a file that was there gets its bytes — and its
/// attributes — back by one rename from the copy taken of it first (the entry's own
/// "&lt;dst&gt;<see cref="GamePatcher.BackupSuffix"/>" when the run takes one, else one beside it,
/// "&lt;dst&gt;<see cref="GamePatcher.UndoSuffix"/>"); the bytes of a byte patch go back by the same verified copy and
/// rename they came in by; the folders the run made are removed again, deepest first, while they are empty. A copy a cut
/// run of Relic's own left beside a file ("&lt;dst&gt;<see cref="GamePatcher.TempSuffix"/>"), which every run removes, is
/// not brought back. An entry that fails is taken back alone when the run goes on without it (an optional one).
/// <see cref="Commit"/>: the import went through, and the copies kept for an undo are removed.
/// </summary>
public sealed class PatchUndo
{
    private abstract record Step(string Path);
    /// <summary>The file was not there: removed.</summary>
    private sealed record Remove(string Path) : Step(Path);
    /// <summary>The file gets the bytes of <paramref name="From"/> back, by one rename over it, and — when they were
    /// written down — the attributes it had (<paramref name="Attributes"/>: a copy carries the archive bit besides
    /// them).</summary>
    private sealed record MoveBack(string Path, string From, FileAttributes? Attributes) : Step(Path);
    /// <summary>The folders were not there (outermost first): removed while empty, deepest first.</summary>
    private sealed record Folders(string Path, IReadOnlyList<string> Made) : Step(Path);
    /// <summary>The bytes a byte patch wrote over, put back through a verified copy.</summary>
    private sealed record Span(string Path, PatchEntry Entry, byte[] Original) : Step(Path);

    private readonly List<Step> _steps = new();
    private readonly List<string> _copies = new();

    /// <summary>There is something to take back.</summary>
    public bool HasWrites => _steps.Count > 0;

    internal void Made(string file) => _steps.Add(new Remove(file));

    internal void Restores(string file, string from, FileAttributes? attributes = null) =>
        _steps.Add(new MoveBack(file, from, attributes));

    internal void FoldersMade(IReadOnlyList<string> made)
    {
        if (made.Count > 0) _steps.Add(new Folders(made[0], made));
    }

    internal void SpanWritten(string file, PatchEntry e, byte[] original) => _steps.Add(new Span(file, e, original));

    /// <summary>The bytes of <paramref name="file"/>, about to be written over, kept beside it until the run is
    /// committed or taken back.</summary>
    internal void KeepCopy(string file)
    {
        string copy = file + GamePatcher.UndoSuffix;
        GamePatcher.RemoveLeftover(copy); // what an import that was cut short left
        FileAttributes attributes = File.GetAttributes(file);
        _steps.Add(new Remove(copy));
        File.Copy(file, copy);
        _copies.Add(copy);
        _steps.Add(new MoveBack(file, copy, attributes));
    }

    /// <summary>Where the run is now: what is kept after it, <see cref="UndoSince"/> takes back.</summary>
    internal int Mark => _steps.Count;

    /// <summary>Takes every kept write back, the last first; never throws. The answer: the files that could not be
    /// taken back (each with a log line), or none.</summary>
    public IReadOnlyList<string> Undo() => UndoSince(0);

    /// <summary>The writes kept after <paramref name="mark"/> (<see cref="Mark"/>) taken back, the last first, and
    /// forgotten — all of them, or those of one entry that failed; never throws. The answer: the files that could not be
    /// taken back (each with a log line), or none. A copy kept of a file that could not get its bytes back — the
    /// import's own, or the entry's .relic-orig — stays where it is, since it holds them: neither the removal written
    /// down for it nor <see cref="Commit"/> removes it.</summary>
    internal IReadOnlyList<string> UndoSince(int mark)
    {
        mark = Math.Clamp(mark, 0, _steps.Count);
        var left = new List<string>();
        var holdBytes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = _steps.Count - 1; i >= mark; i--)
        {
            var step = _steps[i];
            if (step is Remove r && holdBytes.Contains(r.Path))
            {
                Util.Log.Info($"patch: \"{r.Path}\" is kept — it holds the bytes of a file that could not get them back");
                continue;
            }
            try
            {
                TakeBack(step);
            }
            catch (Exception ex)
            {
                left.Add(step.Path);
                Util.Log.Error($"patch: \"{step.Path}\" could not be taken back", ex);
                if (step is MoveBack failed) holdBytes.Add(failed.From);
            }
            if (step is MoveBack m) _copies.Remove(m.From);
        }
        _steps.RemoveRange(mark, _steps.Count - mark);
        return left;
    }

    /// <summary>The run went through: the copies kept for an undo are removed (what cannot be removed now is a log
    /// line). Never throws.</summary>
    public void Commit()
    {
        foreach (string copy in _copies) GamePatcher.TryRemoveLeftover(copy);
        _copies.Clear();
        _steps.Clear();
    }

    private static void TakeBack(Step step)
    {
        switch (step)
        {
            case Remove r:
                GamePatcher.RemoveLeftover(r.Path);
                break;
            case MoveBack m:
                if (!File.Exists(m.From))
                    throw new FileNotFoundException($"the copy \"{m.From}\" is gone", m.From);
                GamePatcher.ClearReadOnlyOf(m.Path); // Windows does not rename over a read-only file
                File.Move(m.From, m.Path, overwrite: true);
                if (m.Attributes is FileAttributes before && File.GetAttributes(m.Path) != before)
                    File.SetAttributes(m.Path, before);
                break;
            case Folders f:
                for (int i = f.Made.Count - 1; i >= 0; i--)
                    if (Directory.Exists(f.Made[i]) && !Directory.EnumerateFileSystemEntries(f.Made[i]).Any())
                        Directory.Delete(f.Made[i]);
                break;
            case Span s:
                GamePatcher.WriteSpan(s.Path, s.Original, s.Entry.Offset, s.Entry.Sha256Before, s.Entry.Dst);
                break;
        }
    }
}

/// <summary>What one <see cref="GamePatcher.Apply"/> run did, for the caller's log.</summary>
public sealed class PatchReport
{
    public List<string> Applied { get; } = new();
    public List<string> AlreadyCurrent { get; } = new();
    /// <summary>Optional entries whose payload copy is absent (dev build without payload/) or that
    /// could not be written — recorded instead of thrown, since the game still runs without them.</summary>
    public List<string> Missing { get; } = new();
    /// <summary>The subset of <see cref="Missing"/> flagged <see cref="PatchEntry.Inject"/>: the
    /// enhancements DLL the build ships but could not be put in place (antivirus, IO). Lets the
    /// caller word the warning as "the game works without them" instead of "the game may not start".</summary>
    public List<string> MissingInjectables { get; } = new();
    /// <summary>Replacements (<see cref="PatchEntry.Sha256Before"/>) whose destination holds the
    /// player's own file — neither the original nor one Relic put there — and was left as it is.</summary>
    public List<string> Kept { get; } = new();
    /// <summary>The subset of <see cref="Applied"/> written over the player's own file, because the run was
    /// asked to (<c>replaceOwn</c>): that file is kept beside it as "&lt;dst&gt;<see cref="GamePatcher.OwnSuffix"/>".</summary>
    public List<string> ReplacedOwn { get; } = new();
    /// <summary>A preview's alone (<see cref="GamePatcher.Preview"/> without its whole-file hashes): the entries whose
    /// decision needs the hash of a whole file. Apply never fills it.</summary>
    public List<string> Unsettled { get; } = new();
    /// <summary>A preview's alone (<see cref="GamePatcher.Preview"/>): the entries the run would write whose place
    /// cannot be written now — the folder or the file is denied to this user, the file is read-only, hidden or a system
    /// file where it would be copied over, another program holds it, or something of another kind stands at the name the
    /// write needs. Apply never fills it.</summary>
    public List<PatchBlock> Blocked { get; } = new();

    public bool Changed => Applied.Count > 0;

    public string Summary =>
        $"{Applied.Count} applied, {AlreadyCurrent.Count} already current, {Missing.Count} missing"
        + (Kept.Count > 0 ? $", {Kept.Count} left as the player's own" : "")
        + (ReplacedOwn.Count > 0 ? $", {ReplacedOwn.Count} written over the player's own (kept as {GamePatcher.OwnSuffix})" : "");
}

/// <summary>
/// Copies the per-version fixes shipped in payload/ into an installed game folder: the patched
/// global-metadata.dat without which the 2.8 client does not start at all, the ayy/anime
/// injector pair the Win11 launch path needs, and — when the caller asks for injectables — the
/// optional in-game enhancements DLL (<see cref="PatchEntry.Inject"/>). Every entry is
/// content-addressed — a destination that already carries the manifest's sha256 is left alone, so a
/// launch does not rewrite 48 MB.
///
/// An entry of kind <see cref="BytePatchKind"/> ships no file of the game: only the few bytes that
/// differ, written into the player's own copy — and only when that copy is the unmodified file of
/// the game build. A file that is neither the original nor the result is refused with a request
/// for the original, never written into.
///
/// A plain entry that names the original it replaces (<see cref="PatchEntry.Sha256Before"/>) is
/// written only over that original or over an earlier payload file of its own, through a verified copy
/// and one rename. A destination with any other content is the player's own file: it is left as it is
/// (<see cref="PatchReport.Kept"/>) and the run goes on — unless the player chose Relic's file
/// (<c>replaceOwn</c>, <see cref="ReplaceOwnFiles"/>): theirs is then kept beside it as
/// "&lt;dst&gt;<see cref="OwnSuffix"/>" and Relic's takes its place. <see cref="OwnFiles"/> says which
/// destinations of a folder hold such a file, so the launcher can ask.
///
/// Runs at install/import time (before the version is registered, so DetectInjector sees the
/// injector) and again before every launch, which is what repairs versions installed by an older
/// build. A missing payload file is only fatal for entries marked "required": a dev build without
/// payload/ must still install and play 1.6. When the plan holds a file of the version's own, a
/// folder that holds ANOTHER version than the one asked for receives nothing at all
/// (<see cref="ClientIdentity"/>). The import runs it as <see cref="ApplyUndoable"/>, after
/// <see cref="Preview"/> — Apply's own run, made without writing — said nothing stands in its way.
/// </summary>
public static class GamePatcher
{
    /// <summary>Suffix of the one-time copy of whatever a "backup" entry replaced.</summary>
    public const string BackupSuffix = ".relic-orig";

    /// <summary>The manifest "kind" of an entry that writes bytes into a file of the game folder.</summary>
    public const string BytePatchKind = "bytepatch";

    /// <summary>Suffix of the copy a byte patch or a replacement is made in before it takes the
    /// destination's place — and, after <see cref="OwnSuffix"/>, of the copy of the player's own file on
    /// its way to that name.</summary>
    public const string TempSuffix = ".relic-tmp";

    /// <summary>Suffix of the copy of the player's own file that a replacement was written over because
    /// the player chose Relic's file (<c>replaceOwn</c>). The player's file of a later run takes the place
    /// of an earlier copy.</summary>
    public const string OwnSuffix = ".relic-own";

    /// <summary>Suffix of the copy an import keeps of a file it writes over without a <see cref="BackupSuffix"/> copy of
    /// its own, so that the import can be taken back (<see cref="PatchUndo"/>); removed once the import went through.</summary>
    public const string UndoSuffix = ".relic-undo";

    private const int HashBufferSize = 1 << 20;

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    private sealed class Manifest
    {
        public List<PatchEntry> Common { get; set; } = new();
        public Dictionary<string, List<PatchEntry>> Versions { get; set; } = new();
    }

    /// <summary>The payload folder shipped next to the exe, or null in a build without one.</summary>
    public static string? PayloadRoot() =>
        new[] { Path.Combine(AppContext.BaseDirectory, "payload") }
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "manifest.json")));

    /// <summary>
    /// Everything that must be in place for <paramref name="versionId"/> — the common entries plus
    /// that version's own. Empty (and never throwing) when the payload is absent or its manifest
    /// cannot be read: that is a dev build, not an error.
    /// </summary>
    public static IReadOnlyList<PatchEntry> PlanFor(string versionId, string? payloadRoot = null,
        bool withInjectables = true) =>
        LoadPlan(versionId, payloadRoot, withInjectables).Entries;

    /// <summary>
    /// True when <see cref="Apply"/> would neither copy anything nor fail — i.e. every destination
    /// already carries the manifest hash, or the only outstanding entries are optional ones this
    /// build has no payload for. False on a manifest/IO problem, INCLUDING a manifest that is there
    /// but unusable, so the caller runs Apply and gets the real diagnosis from it. Only a build with
    /// no manifest at all is "nothing to do".
    ///
    /// This is asked before every launch, so a byte patch is recognised by the destination's length
    /// and by its span alone — one read of the patched bytes, not of the whole file. Bytes changed
    /// elsewhere in such a file are therefore not seen here: Apply, which hashes it whole, is what an
    /// install and an import run.
    /// </summary>
    public static bool IsUpToDate(string versionId, string gameDir, string? payloadRoot = null,
        bool withInjectables = true)
    {
        var (root, entries, unreadable) = LoadPlan(versionId, payloadRoot, withInjectables);
        if (unreadable) return false;
        if (root is null || entries.Count == 0) return true;
        try
        {
            string gameDirFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir));
            foreach (var e in entries)
            {
                string dst = DestPath(gameDirFull, e.Dst);
                if (IsBytePatch(e)) { if (BytePatchInPlace(dst, e)) continue; }
                else if (File.Exists(dst))
                {
                    string now = HashOf(dst);
                    if (SameHash(now, e.Sha256) || IsPlayersOwn(now, e)) continue; // Apply would leave it
                }
                if (!e.Required && !File.Exists(SourcePath(root, e.Src))) continue; // nothing we could do
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Util.Log.Error($"patch: checking version {versionId} failed — trying to apply", ex);
            return false;
        }
    }

    /// <summary>
    /// Puts every entry of the plan in place under <paramref name="gameDir"/> and returns what it
    /// did. Throws <see cref="GamePatchException"/> for a REQUIRED entry that could not be applied,
    /// for a manifest that is there but unusable, for any destination escaping the game dir and for
    /// a folder that holds another version than <paramref name="versionId"/> — everything else is
    /// reported, because a missing optional file costs the user a warning, not a launch.
    /// <paramref name="replaceOwn"/>: the player chose Relic's file over their own — a replacement whose
    /// destination holds the player's own file is written over it in the same pass, theirs kept as
    /// "&lt;dst&gt;<see cref="OwnSuffix"/>" (<see cref="PatchReport.ReplacedOwn"/>). Without it such a file is left
    /// as it is (<see cref="PatchReport.Kept"/>), and <see cref="ReplaceOwnFiles"/> can write over it afterwards.
    /// </summary>
    public static PatchReport Apply(string versionId, string gameDir, Action<string>? log = null,
        string? payloadRoot = null, bool withInjectables = true, bool replaceOwn = false) =>
        Run(versionId, gameDir, log, payloadRoot, withInjectables, replaceOwn, ownOnly: false);

    /// <summary>
    /// The player's own files under the replacements of the plan (<see cref="OwnFiles"/>) written over with
    /// Relic's, theirs kept beside them as "&lt;dst&gt;<see cref="OwnSuffix"/>" (<see cref="PatchReport.ReplacedOwn"/>)
    /// — and nothing else of the plan: no other entry is looked at, so none can refuse the run. This is the
    /// answer "Use Relic's file" on its own, after the plan was put in place by its own rules
    /// (<see cref="IsUpToDate"/>, <see cref="Apply"/>): the answer then never refuses a launch that "Keep mine"
    /// starts, and an entry of the plan that failed does not keep Relic's file out of the place of the player's.
    /// The rules of <see cref="Apply"/> hold: an unusable manifest, a destination outside the game folder and,
    /// once a file of the player's own is found, a folder of another version throw a
    /// <see cref="GamePatchException"/>, and so does a required replacement that could not be written — the
    /// player's file then stays as it was. A replacement whose payload file this build lacks leaves the player's
    /// file as it is (<see cref="PatchReport.Kept"/>).
    /// </summary>
    public static PatchReport ReplaceOwnFiles(string versionId, string gameDir, Action<string>? log = null,
        string? payloadRoot = null, bool withInjectables = true) =>
        Run(versionId, gameDir, log, payloadRoot, withInjectables, replaceOwn: true, ownOnly: true);

    /// <summary>
    /// <see cref="Apply"/> as the import runs it, so that the run can be taken back whole through
    /// <paramref name="undo"/> (<see cref="PatchUndo.Undo"/>): every destination is proven to lie inside the game folder
    /// before anything is written, and every change of the run is written into <paramref name="undo"/> before it is
    /// made. The plan's required entries come first — the byte patches among them last, the one write whose undo copies
    /// a whole file again — so that a run they refuse has nothing else to take back; the optional entries follow (one
    /// that fails is reported, never thrown, and what it wrote is taken back at once, so its place is as it was). The
    /// player's own files are left as they are. The decisions and the report are Apply's; only the order of the writes,
    /// and the place an optional entry that failed leaves, are the import's.
    /// </summary>
    public static PatchReport ApplyUndoable(string versionId, string gameDir, PatchUndo undo, Action<string>? log = null,
        string? payloadRoot = null, bool withInjectables = true) =>
        Run(versionId, gameDir, log, payloadRoot, withInjectables, replaceOwn: false, ownOnly: false, dry: null, undo);

    /// <summary>
    /// What <see cref="Apply"/> would do to <paramref name="gameDir"/>, found out without writing anything: the very
    /// run of Apply — the same plan, the same checks in the same order — in which every write is replaced by the
    /// question whether it could be made (<see cref="WriteAccess"/>: the folder, the file where one stands in the way,
    /// the names the write's own copies take, and nothing of another kind at any of them). The answer: the text Apply
    /// would refuse the run with, or null, the run's report — Applied = what Apply would write,
    /// <see cref="PatchReport.Blocked"/> = what of that cannot be written now — and the room those writes take
    /// (<see cref="PatchPreview.BytesNeeded"/>). <paramref name="undoable"/>: the room is the import's
    /// (<see cref="ApplyUndoable"/>), which also keeps a copy of each file it writes over without a backup of the entry's
    /// own.
    /// <paramref name="deep"/>: whole files are hashed, as Apply hashes them — a byte patch's destination, a replaced
    /// file, the payload files that would be copied. Without it no whole file is read: a decision that needs such a hash
    /// stays open (<see cref="PatchReport.Unsettled"/>) and nothing about that entry's place, or its room, is held
    /// against the folder — only a deep preview can say whether it is written at all, so a shallow one refuses only what
    /// the deep one refuses too, and only the deep one can say that nothing stands in the way: a span that matches says
    /// nothing about the rest of the file. Everything that needs no such hash (the manifest, the payload's span files, a
    /// missing or other-length destination, a span that is neither the original's nor the patched one, a folder of
    /// another version) answers the same either way. Never writes, never throws but for <paramref name="ct"/>.
    /// </summary>
    public static PatchPreview Preview(string versionId, string gameDir, string? payloadRoot = null,
        bool withInjectables = true, bool replaceOwn = false, bool deep = true, bool undoable = false,
        CancellationToken ct = default)
    {
        var dry = new DryRun(deep, undoable, ct);
        try
        {
            Run(versionId, gameDir, log: null, payloadRoot, withInjectables, replaceOwn, ownOnly: false, dry);
            return new PatchPreview(null, dry.Report, dry.HasPlan, deep, dry.BytesNeeded);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Whatever Apply would end on — a GamePatchException, or the error of a path it cannot use.
            return new PatchPreview(ex.Message, dry.Report, dry.HasPlan, deep, dry.BytesNeeded);
        }
    }

    /// <summary>A preview's run (<see cref="Preview"/>): it writes nothing, hashes whole files only when deep, and
    /// keeps the report it fills so a refusal thrown halfway still hands back what was found before it.</summary>
    private sealed class DryRun(bool deep, bool undoable, CancellationToken token)
    {
        private long _kept, _largestOnTheWay;

        public bool Deep { get; } = deep;
        /// <summary>The run predicted is the import's (<see cref="ApplyUndoable"/>).</summary>
        public bool Undoable { get; } = undoable;
        public CancellationToken Token { get; } = token;
        public PatchReport Report { get; } = new();
        public bool HasPlan { get; set; }

        /// <summary>The room the writes take at their fullest: what they leave and keep, and the most one of them takes
        /// beyond that on its way and frees again.</summary>
        public long BytesNeeded => _kept + _largestOnTheWay;

        /// <summary>The entry would be written; a problem with its place is recorded, never thrown. <paramref name="kept"/>:
        /// the bytes the write adds to the drive for good (a new file, what a file grows by, a copy kept);
        /// <paramref name="onTheWay"/>: what it takes beyond that while it is made and frees again (a copy beside the
        /// file it takes the place of).</summary>
        public void Writes(PatchEntry e, string? problem, long kept, long onTheWay)
        {
            Report.Applied.Add(e.Dst);
            if (problem is not null) Report.Blocked.Add(new PatchBlock(e.Dst, problem));
            _kept += Math.Max(0, kept);
            _largestOnTheWay = Math.Max(_largestOnTheWay, Math.Max(0, onTheWay));
        }

        /// <summary>What the copy Apply verifies would be found to hold: the payload file hashed against the entry's
        /// sha256 (a deep preview only). A damaged payload file is refused as Apply refuses the copy of it.</summary>
        public void ChecksPayload(string src, PatchEntry e)
        {
            if (Deep && e.Sha256.Length > 0 && !SameHash(HashOf(src, Token), e.Sha256))
                throw new GamePatchException(L.T("core.patch.hashMismatch", new { file = e.Dst }));
        }
    }

    /// <summary>Thrown inside a shallow preview where a decision needs the hash of a whole file.</summary>
    private sealed class HashDeferredException : Exception;

    /// <summary>The run of <see cref="Apply"/> — every entry of the plan — or, with <paramref name="ownOnly"/>,
    /// of <see cref="ReplaceOwnFiles"/>: the replacements whose destination holds the player's own file alone.
    /// <paramref name="dry"/>: the run of a <see cref="Preview"/> — the same decisions, no write.
    /// <paramref name="undo"/>: the run of <see cref="ApplyUndoable"/> — the import's order, every write kept.</summary>
    private static PatchReport Run(string versionId, string gameDir, Action<string>? log, string? payloadRoot,
        bool withInjectables, bool replaceOwn, bool ownOnly, DryRun? dry = null, PatchUndo? undo = null)
    {
        var report = dry?.Report ?? new PatchReport();
        var (root, entries, unreadable) = LoadPlan(versionId, payloadRoot, withInjectables);
        // Not the same as shipping no payload: the manifest is ours and it is broken, so we cannot
        // tell whether this version's required fixes are among the entries we just lost.
        if (unreadable)
            throw new GamePatchException(L.T("core.patch.manifestUnreadable"));
        if (root is null)
        {
            log?.Invoke("payload/manifest.json is missing — no extra files are applied.");
            return report;
        }
        if (dry is not null) dry.HasPlan = entries.Count > 0;

        string gameDirFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir));
        // The run over the player's own files has nothing to do in a folder that holds none — not even the
        // read of the folder's file list.
        if (ownOnly && !entries.Any(e => Replaceable(e) && MayHoldPlayersOwn(gameDirFull, e)))
            return report;
        IReadOnlyList<PatchEntry> order = entries;
        if (undo is not null)
        {
            // Nothing is written before every destination is known to lie inside the game folder, and the entries that
            // can refuse the run go first: a run they refuse has nothing of the others to take back.
            foreach (var e in entries) DestPath(gameDirFull, e.Dst);
            order = entries.OrderBy(UndoableRank).ToList();
        }
        RefuseOtherVersion(versionId, gameDirFull, root, entries);
        foreach (var e in order)
        {
            if (ownOnly && !Replaceable(e)) continue;
            dry?.Token.ThrowIfCancellationRequested();
            // Outside the try: a destination escaping the game dir is a corrupt manifest, fatal for
            // every entry, and the non-required filter below must never soften it into a log line.
            string dst = DestPath(gameDirFull, e.Dst);
            int mark = undo?.Mark ?? 0;
            try
            {
                ApplyEntry(root, dst, e, report, log, replaceOwn, ownOnly, dry, undo);
            }
            catch (HashDeferredException) when (dry is not null)
            {
                // A shallow preview: the decision needs a whole hash, and only that hash says whether the entry is
                // written at all — so nothing about its place is held against the folder here.
                report.Unsettled.Add(e.Dst);
            }
            catch (Exception ex) when (!e.Required && ex is not OperationCanceledException)
            {
                if (dry is null) Util.Log.Error($"patch: \"{e.Dst}\" could not be applied (optional)", ex);
                // The import's run: what the entry wrote before it failed is taken back — a copy that fails halfway
                // removes the file it was written over, which then gets its bytes back from the copy kept of it.
                undo?.UndoSince(mark);
                report.Missing.Add(e.Dst);
                if (e.Inject) report.MissingInjectables.Add(e.Dst);
                log?.Invoke($"\"{e.Dst}\" could not be applied: {ex.Message}");
            }
            catch (Exception ex) when (ex is not GamePatchException and not OperationCanceledException)
            {
                throw new GamePatchException(
                    L.T("core.patch.requiredWriteFailed", new { file = e.Dst, version = versionId, error = ex.Message }), ex);
            }
        }
        return report;
    }

    /// <summary>The import's order (<see cref="ApplyUndoable"/>): the required entries, the byte patches among them
    /// last, then the optional ones; the manifest's order within each.</summary>
    private static int UndoableRank(PatchEntry e) => e.Required ? (IsBytePatch(e) ? 1 : 0) : 2;

    /// <summary>Whether the destination of the replacement <paramref name="e"/> may hold a file of the player's
    /// own: it does, or it cannot be looked at — the run then reaches it and reports what stands in its way.</summary>
    private static bool MayHoldPlayersOwn(string gameDirFull, PatchEntry e)
    {
        try
        {
            string dst = DestPath(gameDirFull, e.Dst);
            return File.Exists(dst) && IsPlayersOwn(HashOf(dst), e);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// A game folder never receives the files of a version it does not hold. When the plan carries
    /// an entry of the version's own and the folder's file list names a build of ANOTHER version,
    /// nothing is written — the common entries included — whatever "required" says: the entry would
    /// replace that other version's file, or be refused under a text that blames the file. A folder
    /// whose list names no known build, or that has no list, is not this check's to refuse.
    /// </summary>
    private static void RefuseOtherVersion(
        string versionId, string gameDirFull, string root, IReadOnlyList<PatchEntry> entries)
    {
        if (!entries.Any(e => e.PerVersion)) return;
        var identity = ClientIdentity.Check(versionId, gameDirFull, root);
        if (identity.Kind == ClientIdentityKind.OtherVersion)
            throw new GamePatchException(
                L.T("core.patch.otherVersion", new { found = identity.Found, version = versionId }));
    }

    private static void ApplyEntry(string root, string dst, PatchEntry e, PatchReport report, Action<string>? log,
        bool replaceOwn, bool ownOnly, DryRun? dry, PatchUndo? undo)
    {
        if (IsBytePatch(e))
        {
            ApplyBytePatch(root, dst, e, report, log, dry, undo);
            return;
        }

        string? now = File.Exists(dst) ? Hash(dst, dry) : null;
        // A replacement is written only over the original of the game build or over an earlier payload
        // file of its own. Any other content is the player's own file.
        bool own = now is not null && IsPlayersOwn(now, e);
        if (ownOnly && !own) return; // the run over the player's own files: nothing else of the plan is its business
        if (now is not null && SameHash(now, e.Sha256))
        {
            report.AlreadyCurrent.Add(e.Dst);
            return;
        }

        string src = SourcePath(root, e.Src);
        if (own)
        {
            // It stays exactly as it is and the run goes on — the game may well start with it, and nothing is
            // lost that cannot be had back. Only the player's choice of Relic's file (replaceOwn) writes over it,
            // only for an entry whose own file can be told from theirs (Replaceable), and only with Relic's file
            // at hand: a build that lacks it has nothing to put in its place, and that is no reason to refuse a
            // run that would start the game with the player's file.
            if (!(replaceOwn && Replaceable(e)))
            {
                KeepOwn(e, report, log, $"\"{e.Dst}\" is neither the file this game build comes with nor one Relic put there — it is left as it is.");
                return;
            }
            if (!File.Exists(src))
            {
                KeepOwn(e, report, log, $"\"{e.Dst}\" is the player's own and Relic's file is not included in this build — it is left as it is.");
                return;
            }
            if (dry is not null)
            {
                // WriteReplacement: a verified copy beside the destination, Relic's file renamed over the player's,
                // then the copy of the player's file renamed to its kept name — over an older one, if there is one.
                dry.ChecksPayload(src, e);
                string kept = dst + OwnSuffix, keptOnTheWay = kept + TempSuffix;
                string? problem = InTheWay(dst + TempSuffix) || InTheWay(kept) || InTheWay(keptOnTheWay)
                    ? WriteAccess.InTheWay : null;
                problem ??= WriteAccess.CanCreateIn(Path.GetDirectoryName(dst)!) ?? WriteAccess.CanReplace(dst)
                    ?? (File.Exists(kept) ? WriteAccess.CanReplace(kept) : null)
                    ?? Leftover(dst + TempSuffix) ?? Leftover(keptOnTheWay);
                // Relic's file stays, the player's lives on as its kept copy; on the way both copies stand beside it.
                long srcLength = new FileInfo(src).Length, dstLength = new FileInfo(dst).Length;
                dry.Writes(e, problem, kept: srcLength, onTheWay: dstLength);
                report.ReplacedOwn.Add(e.Dst);
                return;
            }
            WriteReplacement(src, dst, e, now, log);
            report.Applied.Add(e.Dst);
            report.ReplacedOwn.Add(e.Dst);
            log?.Invoke($"\"{e.Dst}\" was put in place" + (e.Reason.Length > 0 ? $" — {e.Reason}" : ""));
            return;
        }

        if (SourceMissing(src, e, report, log)) return;

        // Once, ever: a second run would otherwise back up the copy WE wrote and lose the real
        // original the game shipped with. A replacement backs up nothing but the original itself.
        string backup = dst + BackupSuffix;
        bool backupable = !IsReplacement(e) || (now is not null && SameHash(now, e.Sha256Before));
        bool takesBackup = e.Backup && now is not null && backupable && !File.Exists(backup);

        if (dry is not null)
        {
            // The folder (made where it is missing, a .relic-orig taken in it), then the file standing at the
            // destination: a replacement renames its verified copy over it, any other entry copies over it in place.
            // The import also keeps a copy of a file it writes over without a backup of the entry's own.
            dry.ChecksPayload(src, e);
            bool keepsCopy = dry.Undoable && now is not null && !takesBackup;
            string tmp = dst + TempSuffix, copy = dst + UndoSuffix;
            string? problem = InTheWay(dst) || (IsReplacement(e) && InTheWay(tmp)) || (takesBackup && InTheWay(backup))
                || (keepsCopy && InTheWay(copy)) ? WriteAccess.InTheWay : null;
            problem ??= WriteAccess.CanCreateIn(Path.GetDirectoryName(dst)!);
            if (problem is null && now is not null)
                problem = IsReplacement(e) ? WriteAccess.CanReplace(dst) : WriteAccess.CanOverwrite(dst);
            if (problem is null && IsReplacement(e)) problem = Leftover(tmp);
            if (problem is null && keepsCopy) problem = Leftover(copy);
            // A new file takes its own room; one written over an existing file takes what it adds, and on its way —
            // a replacement's copy beside the file it takes the place of, or a copy made in place — up to the smaller of
            // the two besides.
            long srcLength = new FileInfo(src).Length, dstLength = now is null ? 0 : new FileInfo(dst).Length;
            dry.Writes(e, problem,
                kept: (now is null ? srcLength : Math.Max(0, srcLength - dstLength))
                      + (takesBackup ? dstLength : 0) + (keepsCopy ? dstLength : 0),
                onTheWay: Math.Min(srcLength, dstLength));
            return;
        }

        // Every change below is kept for an undo before it is made (an undoable run): the folders, the backup, the copy
        // of a file written over without one, a file that was not there.
        string parent = Path.GetDirectoryName(dst)!;
        undo?.FoldersMade(MissingFolders(parent));
        Directory.CreateDirectory(parent);

        bool tookBackup = false;
        if (takesBackup)
        {
            FileAttributes? held = undo is null ? null : File.GetAttributes(dst);
            undo?.Made(backup);
            File.Copy(dst, backup);
            undo?.Restores(dst, backup, held);
            tookBackup = true;
            log?.Invoke($"the original \"{e.Dst}\" was kept as {Path.GetFileName(backup)}");
        }
        if (now is null) undo?.Made(dst);
        else if (!tookBackup) undo?.KeepCopy(dst);

        if (IsReplacement(e))
        {
            // Through a verified copy and one rename: a cut or damaged copy of Relic's file left at the
            // destination would read as the player's own at the next run — left in place, the game started with it.
            WriteReplacement(src, dst, e, null, log);
        }
        else
        {
            File.Copy(src, dst, overwrite: true);
            // Only an entry that carries a hash can be verified: one without it is refreshed on every
            // run precisely because we have nothing to compare the destination against.
            if (e.Sha256.Length > 0 && !HashMatches(dst, e.Sha256))
                throw new GamePatchException(L.T("core.patch.hashMismatch", new { file = e.Dst }));
        }

        report.Applied.Add(e.Dst);
        log?.Invoke($"\"{e.Dst}\" was put in place" + (e.Reason.Length > 0 ? $" — {e.Reason}" : ""));
    }

    /// <summary>A file of the player's own under a replacement, left exactly as it is.</summary>
    private static void KeepOwn(PatchEntry e, PatchReport report, Action<string>? log, string line)
    {
        report.Kept.Add(e.Dst);
        log?.Invoke(line);
    }

    /// <summary>True when this build ships no payload file for the entry: a hard failure for a
    /// required one, a reported skip for the rest.</summary>
    private static bool SourceMissing(string src, PatchEntry e, PatchReport report, Action<string>? log)
    {
        if (File.Exists(src)) return false;
        if (e.Required)
            throw new GamePatchException(L.T("core.patch.requiredMissing", new { file = e.Dst, src }));
        report.Missing.Add(e.Dst);
        if (e.Inject) report.MissingInjectables.Add(e.Dst);
        log?.Invoke($"\"{e.Dst}\" is not included in this build — skipping.");
        return true;
    }

    private static bool IsBytePatch(PatchEntry e) => e.Kind == BytePatchKind;

    /// <summary>A plain entry that names the original file of the game it takes the place of
    /// (<see cref="PatchEntry.Sha256Before"/>).</summary>
    private static bool IsReplacement(PatchEntry e) => !IsBytePatch(e) && e.Sha256Before.Length > 0;

    /// <summary>A replacement whose destination, of hash <paramref name="now"/>, is neither the result,
    /// nor the original of the game build, nor an earlier payload file of the entry: the player's own.</summary>
    private static bool IsPlayersOwn(string now, PatchEntry e) =>
        IsReplacement(e)
        && !SameHash(now, e.Sha256) && !SameHash(now, e.Sha256Before)
        && !e.Sha256Earlier.Any(h => SameHash(now, h));

    /// <summary>A replacement whose own file can be told from the player's — its sha256 is a sha256 —
    /// so that the player's file can be written over on their word: without the hash of the result
    /// every later run would take Relic's own copy for the player's and copy it over theirs.</summary>
    private static bool Replaceable(PatchEntry e) => IsReplacement(e) && IsSha256(e.Sha256);

    /// <summary>
    /// A replacement's payload file put at <paramref name="dst"/>. It is copied into
    /// "&lt;dst&gt;<see cref="TempSuffix"/>", flushed and hashed to the entry's sha256 before it takes the
    /// destination's place in one rename, so the destination never holds a cut or damaged copy of Relic's
    /// file — a later run would take one for a file of the player's own, leave it where it is and start the
    /// game with it. The file at the destination afterwards carries the attributes the one there before had.
    ///
    /// <paramref name="playersHash"/>: the destination is the player's own file, of that hash, written over
    /// because the player chose Relic's. It is copied first, into
    /// "&lt;dst&gt;<see cref="OwnSuffix"/><see cref="TempSuffix"/>", flushed, and the copy must hash to
    /// <paramref name="playersHash"/> — else nothing is changed. Only once Relic's file is in place does that
    /// copy become "&lt;dst&gt;<see cref="OwnSuffix"/>", where it takes the place of a copy an earlier choice
    /// left: a rename over the destination that fails (another program holds the file) leaves that older copy
    /// as it was. When the copy cannot take its name, the player's file goes back over Relic's and the run
    /// fails with nothing changed. "&lt;dst&gt;<see cref="BackupSuffix"/>" is not touched here: it holds the
    /// original of the game build, or nothing.
    /// </summary>
    private static void WriteReplacement(string src, string dst, PatchEntry e, string? playersHash, Action<string>? log)
    {
        string tmp = dst + TempSuffix;
        string own = dst + OwnSuffix;
        string side = own + TempSuffix;
        DeleteTemp(tmp); // what a run that was cut short left behind
        // Likewise: the player's file is at the destination, so a copy of it an earlier run left is not its
        // only one — and the file of this run takes the place of an earlier copy in any case.
        if (playersHash is not null) DeleteTemp(side);
        FileAttributes? attributes = File.Exists(dst) ? File.GetAttributes(dst) : null;
        bool relicsInPlace = false;
        try
        {
            CopyFlushed(src, tmp);
            if (e.Sha256.Length > 0 && !HashMatches(tmp, e.Sha256))
                throw new GamePatchException(L.T("core.patch.hashMismatch", new { file = e.Dst }));
            if (playersHash is not null)
            {
                CopyFlushed(dst, side);
                File.SetLastWriteTimeUtc(side, File.GetLastWriteTimeUtc(dst));
                if (!HashMatches(side, playersHash))
                    throw new GamePatchException(L.T("core.patch.ownCopyMismatch", new { file = e.Dst }));
            }

            if (attributes is FileAttributes held && (held & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(dst, held & ~FileAttributes.ReadOnly); // Windows does not rename over a read-only file
            File.Move(tmp, dst, overwrite: true);
            relicsInPlace = true;
            if (playersHash is null) return;

            try
            {
                ClearReadOnly(own); // an older copy may have been made read-only
                File.Move(side, own, overwrite: true);
            }
            catch (Exception)
            {
                try
                {
                    File.Move(side, dst, overwrite: true); // the player's file back over Relic's: nothing has changed
                    relicsInPlace = false;
                }
                catch (Exception back)
                {
                    Util.Log.Error($"patch: the player's own \"{e.Dst}\" could not be put back — it lies beside it as {Path.GetFileName(side)}", back);
                }
                throw;
            }
            log?.Invoke($"the player's own \"{e.Dst}\" was kept as {Path.GetFileName(own)}");
        }
        catch
        {
            TryDeleteTemp(tmp);
            // With Relic's file in place the copy is the only one of the player's file: it stays.
            if (playersHash is not null && !relicsInPlace) TryDeleteTemp(side);
            throw;
        }
        finally
        {
            if (attributes is FileAttributes before)
            {
                try { if (File.Exists(dst) && File.GetAttributes(dst) != before) File.SetAttributes(dst, before); }
                catch (Exception ex) { Util.Log.Error($"patch: the attributes of \"{dst}\" could not be put back", ex); }
            }
        }
    }

    /// <summary><paramref name="from"/> copied into the new file <paramref name="to"/> and flushed to the disk:
    /// a copy the next step relies on survives a power cut that comes after it.</summary>
    private static void CopyFlushed(string from, string to)
    {
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read,
            HashBufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, HashBufferSize);
        input.CopyTo(output, HashBufferSize);
        output.Flush(flushToDisk: true);
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return;
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    /// <summary>
    /// Writes the bytes of a byte-patch entry into the destination the game folder already holds.
    /// The checks run from the cheapest to the dearest and every one of them ends without a write:
    /// the entry, the payload file, then the destination — its length, its span, and only then ONE
    /// hash of the whole file, which alone decides between "already current", "the original: write"
    /// and "neither". A destination that is not there is refused too, unless the verified copy of a
    /// replace that was cut in two lies beside it (<see cref="FinishCutReplace"/>).
    /// </summary>
    private static void ApplyBytePatch(
        string root, string dst, PatchEntry e, PatchReport report, Action<string>? log, DryRun? dry, PatchUndo? undo)
    {
        if (!BytePatchShapeOk(e))
            throw new GamePatchException(L.T("core.patch.manifestUnreadable"));

        string src = SourcePath(root, e.Src);
        if (SourceMissing(src, e, report, log)) return;
        byte[] span = new FileInfo(src).Length == e.Length ? File.ReadAllBytes(src) : Array.Empty<byte>();
        if (span.Length != e.Length || !SameHash(Convert.ToHexString(SHA256.HashData(span)), e.SpanSha256))
            throw new GamePatchException(L.T("core.patch.payloadDamaged", new { file = e.Dst, src = e.Src }));

        var target = new FileInfo(dst);
        if (!target.Exists)
        {
            if (!FinishCutReplace(dst, e, dry, undo))
                throw new GamePatchException(L.T("core.patch.targetMissing", new { file = e.Dst }));
            if (dry is not null)
            {
                // One rename of the verified copy to the destination's name.
                string? problem = Directory.Exists(dst) ? WriteAccess.InTheWay : null;
                problem ??= WriteAccess.CanCreateIn(Path.GetDirectoryName(dst)!) ?? Leftover(dst + TempSuffix);
                dry.Writes(e, problem, kept: 0, onTheWay: 0);
                return;
            }
            report.Applied.Add(e.Dst);
            log?.Invoke($"\"{e.Dst}\": the verified copy a replace that was cut short left beside it was put in its place");
            return;
        }
        if (target.Length != e.Size)
            throw new GamePatchException(L.T("core.patch.notOriginal", new { file = e.Dst }));
        string spanNow = SpanHash(dst, e);
        if (!SameHash(spanNow, e.SpanSha256) && !SameHash(spanNow, e.SpanSha256Before))
            throw new GamePatchException(L.T("core.patch.notOriginal", new { file = e.Dst }));

        string whole = Hash(dst, dry);
        if (SameHash(whole, e.Sha256))
        {
            report.AlreadyCurrent.Add(e.Dst);
            return;
        }
        if (!SameHash(whole, e.Sha256Before))
            throw new GamePatchException(L.T("core.patch.notOriginal", new { file = e.Dst }));

        if (dry is not null)
        {
            // WriteBytePatch: a copy of the whole file beside the destination, renamed over it (its read-only attribute
            // cleared first), a copy a cut run left removed before.
            string tmp = dst + TempSuffix;
            string? problem = InTheWay(tmp) ? WriteAccess.InTheWay : null;
            problem ??= WriteAccess.CanCreateIn(Path.GetDirectoryName(dst)!) ?? WriteAccess.CanReplace(dst) ?? Leftover(tmp);
            dry.Writes(e, problem, kept: 0, onTheWay: e.Size);
            return;
        }
        // The bytes the patch writes over, for an undo: the file is the original, so its span is the original's.
        byte[]? original = undo is not null ? ReadSpan(dst, e) : null;
        WriteBytePatch(dst, span, e);
        if (original is not null) undo!.SpanWritten(dst, e, original);
        report.Applied.Add(e.Dst);
        log?.Invoke($"\"{e.Dst}\": {e.Length} bytes were written at offset {e.Offset}"
            + (e.Reason.Length > 0 ? $" — {e.Reason}" : ""));
    }

    /// <summary>
    /// The write of a byte patch never touches the destination in place: a span can straddle two
    /// disk sectors, and a write cut between them would leave a file that is neither state. The
    /// bytes go into a copy beside the destination, the copy is flushed and must hash to the
    /// entry's sha256, and only then does it take the destination's place in one rename on the same
    /// volume. A file under the destination's name is thus never anything but the original or the
    /// verified result, which is why this kind keeps no <see cref="BackupSuffix"/> copy. Whether
    /// that name can be without a file for a moment is the file system's doing: one that journals
    /// the rename swaps the two in one step, while one without a journal (FAT, exFAT) may remove
    /// the old file before it renames the copy, and a power cut between the two leaves the
    /// verified copy alone — <see cref="FinishCutReplace"/> gives it the name at the next run.
    /// </summary>
    private static void WriteBytePatch(string dst, byte[] span, PatchEntry e) =>
        WriteSpan(dst, span, e.Offset, e.Sha256, e.Dst);

    /// <summary>The write of <see cref="WriteBytePatch"/>: <paramref name="span"/> at <paramref name="offset"/> of
    /// <paramref name="dst"/>, through a copy that must hash to <paramref name="expected"/> before it takes the
    /// destination's place — also how an undo puts the original bytes back (<see cref="PatchUndo"/>).
    /// <paramref name="name"/>: the file as the texts name it.</summary>
    internal static void WriteSpan(string dst, byte[] span, long offset, string expected, string name)
    {
        string tmp = dst + TempSuffix;
        DeleteTemp(tmp); // what a run that was cut short left behind
        FileAttributes attributes = File.GetAttributes(dst);
        try
        {
            File.Copy(dst, tmp);
            // The copy takes the destination's attributes with it, and a read-only copy could be
            // neither written into here nor removed by a later run.
            File.SetAttributes(tmp, FileAttributes.Normal);
            using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                fs.Write(span, 0, span.Length);
                fs.Flush(flushToDisk: true);
            }
            if (!HashMatches(tmp, expected))
                throw new GamePatchException(L.T("core.patch.patchMismatch", new { file = name }));

            // Windows does not rename over a read-only file.
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(dst, attributes & ~FileAttributes.ReadOnly);
            File.Move(tmp, dst, overwrite: true);
        }
        catch
        {
            try { DeleteTemp(tmp); }
            catch (Exception ex) { Util.Log.Error($"patch: \"{tmp}\" could not be removed — the next run removes it", ex); }
            throw;
        }
        finally
        {
            // Whichever of the two files is at the destination, it carries the attributes the player's file had.
            try { if (File.GetAttributes(dst) != attributes) File.SetAttributes(dst, attributes); }
            catch (Exception ex) { Util.Log.Error($"patch: the attributes of \"{dst}\" could not be put back", ex); }
        }
    }

    private static void DeleteTemp(string tmp)
    {
        if (!File.Exists(tmp)) return;
        File.SetAttributes(tmp, FileAttributes.Normal);
        File.Delete(tmp);
    }

    /// <summary><see cref="DeleteTemp"/> on the way out of a run that failed: what cannot be removed now is a
    /// log line, and the next run removes it.</summary>
    private static void TryDeleteTemp(string tmp)
    {
        try { DeleteTemp(tmp); }
        catch (Exception ex) { Util.Log.Error($"patch: \"{tmp}\" could not be removed — the next run removes it", ex); }
    }

    /// <summary>A file of the run's own removed, whatever its attributes (<see cref="PatchUndo"/>).</summary>
    internal static void RemoveLeftover(string path) => DeleteTemp(path);

    /// <summary><see cref="RemoveLeftover"/> that only logs what it cannot remove.</summary>
    internal static void TryRemoveLeftover(string path) => TryDeleteTemp(path);

    /// <summary>The read-only attribute of a file cleared, if it has it (<see cref="PatchUndo"/>).</summary>
    internal static void ClearReadOnlyOf(string path) => ClearReadOnly(path);

    /// <summary>
    /// A missing destination with its temp copy beside it, whole and verified, is a replace that
    /// was cut between removing the old file and renaming the copy. The copy has the entry's
    /// length and sha256 — it IS the result — so it gets the destination's name, and true is
    /// answered. Any other file under the temp name proves nothing and is left where it is.
    /// </summary>
    private static bool FinishCutReplace(string dst, PatchEntry e, DryRun? dry = null, PatchUndo? undo = null)
    {
        string tmp = dst + TempSuffix;
        var copy = new FileInfo(tmp);
        if (!copy.Exists || copy.Length != e.Size || !SameHash(Hash(tmp, dry), e.Sha256)) return false;
        if (dry is null)
        {
            File.Move(tmp, dst);
            undo?.Restores(tmp, dst); // taken back, the copy lies beside the missing file again
        }
        return true;
    }

    /// <summary>The bytes of the entry's span of <paramref name="path"/>, a file of the entry's length.</summary>
    private static byte[] ReadSpan(string path, PatchEntry e)
    {
        byte[] buffer = new byte[e.Length];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(e.Offset, SeekOrigin.Begin);
        fs.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>The folders of <paramref name="dir"/>'s path that are not there, outermost first.</summary>
    private static List<string> MissingFolders(string dir)
    {
        var missing = new List<string>();
        for (string? d = dir; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Insert(0, d);
        return missing;
    }

    /// <summary>Something of another kind stands at the name a write of a file needs: a folder at the file's own name,
    /// or a file at the name of a folder on its way down (a preview's question).</summary>
    private static bool InTheWay(string file)
    {
        if (Directory.Exists(file)) return true;
        for (string? d = Path.GetDirectoryName(file); d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d))
            if (File.Exists(d)) return true;
        return false;
    }

    /// <summary>A copy a cut run left at a name the run makes again (it removes it first): whether it can be removed,
    /// or null when there is none (a preview's question).</summary>
    private static string? Leftover(string file) => File.Exists(file) ? WriteAccess.CanRemove(file) : null;

    /// <summary>What a launch asks of a byte patch: the destination has the entry's length and its
    /// span is the patched one.</summary>
    private static bool BytePatchInPlace(string dst, PatchEntry e)
    {
        if (!BytePatchShapeOk(e)) return false;
        var target = new FileInfo(dst);
        return target.Exists && target.Length == e.Size && SameHash(SpanHash(dst, e), e.SpanSha256);
    }

    /// <summary>The span lies inside the file, all four hashes are sha256 values and each "before"
    /// differs from its "after" — an entry that fails this describes no patch.</summary>
    private static bool BytePatchShapeOk(PatchEntry e) =>
        e.Size > 0 && e.Offset >= 0 && e.Length > 0 && e.Offset <= e.Size - e.Length
        && IsSha256(e.Sha256) && IsSha256(e.Sha256Before) && IsSha256(e.SpanSha256) && IsSha256(e.SpanSha256Before)
        && !SameHash(e.Sha256, e.Sha256Before) && !SameHash(e.SpanSha256, e.SpanSha256Before);

    private static bool IsSha256(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    private static bool SameHash(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The sha256 of the entry's span of <paramref name="path"/> — one read of
    /// <see cref="PatchEntry.Length"/> bytes. A file that ends inside the span hashes what it has,
    /// which matches nothing.</summary>
    private static string SpanHash(string path, PatchEntry e)
    {
        byte[] buffer = new byte[e.Length];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(e.Offset, SeekOrigin.Begin);
        int read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
    }

    /// <summary>One <see cref="PatchEntry.Inject"/> entry of a version's plan, as seen from the launch
    /// side: where it lands in the game dir (null when no game dir was given or the manifest points
    /// outside it) and whether this build actually ships its payload source.</summary>
    public sealed record Injectable(string Dst, string? DstPath, bool Shipped);

    /// <summary>
    /// The <see cref="PatchEntry.Inject"/> entries <paramref name="versionId"/>'s plan carries, in
    /// manifest order, regardless of the toggle — this is a query about the BUILD, not about what was
    /// applied. Never throws: a build without payload, an unreadable manifest or an odd game dir all
    /// come back as an empty list (the launch then simply happens without enhancements).
    /// </summary>
    public static IReadOnlyList<Injectable> Injectables(string versionId, string? gameDir = null,
        string? payloadRoot = null)
    {
        try
        {
            var (root, entries, _) = LoadPlan(versionId, payloadRoot, withInjectables: true);
            if (root is null) return Array.Empty<Injectable>();
            string? gameDirFull = gameDir is null
                ? null
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir));
            var list = new List<Injectable>();
            foreach (var e in entries)
            {
                if (!e.Inject) continue;
                string? dstPath = null;
                if (gameDirFull is not null)
                {
                    try { dstPath = DestPath(gameDirFull, e.Dst); }
                    catch (GamePatchException ex)
                    {
                        // A dst escaping the game dir is fatal for Apply; here it only means "this
                        // entry has no usable location", so the launch goes on without it.
                        Util.Log.Error($"patch: injectable \"{e.Dst}\" ignored", ex);
                    }
                }
                list.Add(new Injectable(e.Dst, dstPath, File.Exists(SourcePath(root, e.Src))));
            }
            return list;
        }
        catch (Exception ex)
        {
            Util.Log.Error($"patch: listing the injectables of {versionId} failed", ex);
            return Array.Empty<Injectable>();
        }
    }

    /// <summary>A replacement of a version's plan as the launcher shows it: where it goes in the game
    /// folder (the manifest's dst), what it is for (its reason, data of the manifest) and the name the
    /// player's own file there is kept under when Relic's takes its place.</summary>
    public sealed record Replacement(string Dst, string Reason)
    {
        public string Kept => Dst + OwnSuffix;
    }

    /// <summary>
    /// The replacements of <paramref name="versionId"/>'s plan (a plain entry that names the original it
    /// replaces, <see cref="PatchEntry.Sha256Before"/>) whose payload file this build ships, in manifest
    /// order — a question about the BUILD: the versions whose game folder can hold a file of the player's own
    /// in the place of one of Relic's. Never throws: no payload, an unusable manifest — an empty list.
    /// </summary>
    public static IReadOnlyList<Replacement> Replacements(string versionId, string? payloadRoot = null)
    {
        var (root, entries, _) = LoadPlan(versionId, payloadRoot, withInjectables: true);
        if (root is null) return Array.Empty<Replacement>();
        return entries.Where(e => Replaceable(e) && Shipped(root, e)).Select(e => new Replacement(e.Dst, e.Reason)).ToList();
    }

    /// <summary>This build ships the entry's payload file.</summary>
    private static bool Shipped(string root, PatchEntry e)
    {
        try { return File.Exists(SourcePath(root, e.Src)); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The replacements of the plan whose destination under <paramref name="gameDir"/> holds the player's
    /// own file — neither the original of the game build, nor Relic's file, nor an earlier one of its own:
    /// what <see cref="Apply"/> leaves as it is unless the player chooses Relic's file (<c>replaceOwn</c>,
    /// <see cref="ReplaceOwnFiles"/>). A replacement whose payload file this build lacks is not named: there
    /// is nothing of Relic's to put in its place. One hash per replacement whose destination is there. A
    /// folder that holds another version receives nothing at all (<see cref="RefuseOtherVersion"/>), so
    /// nothing in it is asked about. Never throws: no payload, an unusable manifest, a destination outside
    /// the game folder or a file that cannot be read — an empty list and a log line.
    /// </summary>
    public static IReadOnlyList<Replacement> OwnFiles(string versionId, string gameDir, string? payloadRoot = null,
        bool withInjectables = true)
    {
        try
        {
            var (root, entries, unreadable) = LoadPlan(versionId, payloadRoot, withInjectables);
            if (unreadable)
            {
                Util.Log.Info($"patch: the payload manifest cannot be used — no file of version {versionId} is asked about");
                return Array.Empty<Replacement>();
            }
            if (root is null || !entries.Any(e => Replaceable(e) && Shipped(root, e))) return Array.Empty<Replacement>();
            string gameDirFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir));
            if (entries.Any(e => e.PerVersion)
                && ClientIdentity.Check(versionId, gameDirFull, root).Kind == ClientIdentityKind.OtherVersion)
                return Array.Empty<Replacement>();
            var own = new List<Replacement>();
            foreach (var e in entries)
            {
                if (!Replaceable(e) || !Shipped(root, e)) continue;
                string dst = DestPath(gameDirFull, e.Dst);
                if (File.Exists(dst) && IsPlayersOwn(HashOf(dst), e)) own.Add(new Replacement(e.Dst, e.Reason));
            }
            return own;
        }
        catch (Exception ex)
        {
            Util.Log.Error($"patch: looking for the player's own files of version {versionId} failed", ex);
            return Array.Empty<Replacement>();
        }
    }

    /// <summary>
    /// The entries of <paramref name="versionId"/>'s plan. Never throws; instead it separates the two
    /// cases the callers must not confuse — a build that simply ships no manifest (Root null,
    /// Unreadable false: there is genuinely nothing to apply) and a manifest that is on disk but
    /// unusable (Unreadable true: we do not know what we were supposed to apply). With
    /// <paramref name="withInjectables"/> false the <see cref="PatchEntry.Inject"/> entries are dropped
    /// from the plan altogether — the enhancements toggle is off, so they are neither copied, nor
    /// verified, nor ever deleted.
    /// </summary>
    private static (string? Root, IReadOnlyList<PatchEntry> Entries, bool Unreadable) LoadPlan(
        string versionId, string? payloadRoot, bool withInjectables)
    {
        string? root = payloadRoot ?? PayloadRoot();
        if (root is null) return (null, Array.Empty<PatchEntry>(), false);
        string manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath)) return (null, Array.Empty<PatchEntry>(), false);

        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), Opts);
        }
        catch (Exception ex)
        {
            // Every way of failing to read our own manifest means the same thing (unparsable JSON,
            // a lock, no read permission), so they are all caught here — LoadPlan is called outside
            // the callers' try blocks and an escaping exception would break their contracts.
            Util.Log.Error($"patch: payload/manifest.json unreadable ({root})", ex);
            return (null, Array.Empty<PatchEntry>(), true);
        }
        if (manifest is null) return (null, Array.Empty<PatchEntry>(), true);

        // An entry of a kind this build does not know cannot be told from a plain copy, and applied
        // as one it would put a payload file of a few bytes in the place of a whole game file. The
        // manifest is ours, so such an entry anywhere in it means the manifest is not usable.
        var listed = manifest.Common.Concat(
            manifest.Versions.Values.Where(list => list is not null).SelectMany(list => list));
        foreach (PatchEntry? e in listed)
        {
            if (e is not null && (string.IsNullOrEmpty(e.Kind) || e.Kind == BytePatchKind)) continue;
            Util.Log.Error($"patch: payload/manifest.json holds an entry of an unknown kind (\"{e?.Kind}\", dst \"{e?.Dst}\") — {root}");
            return (null, Array.Empty<PatchEntry>(), true);
        }

        var entries = new List<PatchEntry>(manifest.Common);
        var forVersion = manifest.Versions
            .FirstOrDefault(kv => string.Equals(kv.Key, versionId, StringComparison.OrdinalIgnoreCase)).Value;
        if (forVersion is not null)
        {
            foreach (var e in forVersion) e.PerVersion = true;
            entries.AddRange(forVersion);
        }
        if (!withInjectables) entries.RemoveAll(e => e.Inject);
        return (root, entries, false);
    }

    private static string SourcePath(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Resolves an entry's destination and refuses anything that escapes the game dir — we
    /// build these paths by hand, so the containment check has to be explicit.</summary>
    private static string DestPath(string gameDirFull, string relative)
    {
        string dest = Path.GetFullPath(Path.Combine(gameDirFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        // A game unzipped straight to a drive root already ends in the separator (TrimEnding leaves a
        // root alone), and appending a second one would reject every destination under it.
        string prefix = Path.EndsInDirectorySeparator(gameDirFull)
            ? gameDirFull
            : gameDirFull + Path.DirectorySeparatorChar;
        if (!dest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new GamePatchException(L.T("core.patch.outsideGameDir", new { path = relative }));
        return dest;
    }

    private static bool HashMatches(string path, string expected)
    {
        if (expected.Length == 0) return false; // nothing to content-address by: always refresh it
        return SameHash(HashOf(path), expected);
    }

    private static string HashOf(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            HashBufferSize, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>The hash a decision of the run needs: Apply's own, or a preview's — read in chunks that its token can
    /// stop between, and refused to a shallow preview (<see cref="HashDeferredException"/>).</summary>
    private static string Hash(string path, DryRun? dry)
    {
        if (dry is null) return HashOf(path);
        if (!dry.Deep) throw new HashDeferredException();
        return HashOf(path, dry.Token);
    }

    /// <summary>A preview's hash: it lets every other program read, write and delete the file meanwhile — a file written
    /// meanwhile only makes the answer stale, and the import asks again. (A rename over the file still waits for the hash
    /// to end: Windows refuses one while any handle is open.)</summary>
    private static string HashOf(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            HashBufferSize, FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[HashBufferSize];
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
