using System.Security.Cryptography;
using System.Text.Json;
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

/// <summary>One file Relic copies into the game folder, as described by payload/manifest.json.</summary>
public sealed class PatchEntry
{
    public string Src { get; set; } = "";
    public string Dst { get; set; } = "";
    /// <summary>Content address of the file: a destination that already carries it is left alone. An
    /// entry without one cannot be recognised as current, so it is re-copied on every run.</summary>
    public string Sha256 { get; set; } = "";
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

    public bool Changed => Applied.Count > 0;

    public string Summary =>
        $"{Applied.Count} applied, {AlreadyCurrent.Count} already current, {Missing.Count} missing";
}

/// <summary>
/// Copies the per-version fixes shipped in payload/ into an installed game folder: the patched
/// global-metadata.dat without which the 2.8 client does not start at all, the ayy/anime
/// injector pair the Win11 launch path needs, and — when the caller asks for injectables — the
/// optional in-game enhancements DLL (<see cref="PatchEntry.Inject"/>). Every entry is
/// content-addressed — a destination that already carries the manifest's sha256 is left alone, so a
/// launch does not rewrite 48 MB.
///
/// Runs at install/import time (before the version is registered, so DetectInjector sees the
/// injector) and again before every launch, which is what repairs versions installed by an older
/// build. A missing payload file is only fatal for entries marked "required": a dev build without
/// payload/ must still install and play 1.6.
/// </summary>
public static class GamePatcher
{
    /// <summary>Suffix of the one-time copy of whatever a "backup" entry replaced.</summary>
    public const string BackupSuffix = ".relic-orig";

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
                if (File.Exists(dst) && HashMatches(dst, e.Sha256)) continue;
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
    /// for a manifest that is there but unusable, and for any destination escaping the game dir —
    /// everything else is reported, because a missing optional file costs the user a warning, not a
    /// launch.
    /// </summary>
    public static PatchReport Apply(string versionId, string gameDir, Action<string>? log = null,
        string? payloadRoot = null, bool withInjectables = true)
    {
        var report = new PatchReport();
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

        string gameDirFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir));
        foreach (var e in entries)
        {
            // Outside the try: a destination escaping the game dir is a corrupt manifest, fatal for
            // every entry, and the non-required filter below must never soften it into a log line.
            string dst = DestPath(gameDirFull, e.Dst);
            try
            {
                ApplyEntry(root, dst, e, report, log);
            }
            catch (Exception ex) when (!e.Required)
            {
                Util.Log.Error($"patch: \"{e.Dst}\" could not be applied (optional)", ex);
                report.Missing.Add(e.Dst);
                if (e.Inject) report.MissingInjectables.Add(e.Dst);
                log?.Invoke($"\"{e.Dst}\" could not be applied: {ex.Message}");
            }
            catch (Exception ex) when (ex is not GamePatchException)
            {
                throw new GamePatchException(
                    L.T("core.patch.requiredWriteFailed", new { file = e.Dst, version = versionId, error = ex.Message }), ex);
            }
        }
        return report;
    }

    private static void ApplyEntry(
        string root, string dst, PatchEntry e, PatchReport report, Action<string>? log)
    {
        if (File.Exists(dst) && HashMatches(dst, e.Sha256))
        {
            report.AlreadyCurrent.Add(e.Dst);
            return;
        }

        string src = SourcePath(root, e.Src);
        if (!File.Exists(src))
        {
            if (e.Required)
                throw new GamePatchException(L.T("core.patch.requiredMissing", new { file = e.Dst, src }));
            report.Missing.Add(e.Dst);
            if (e.Inject) report.MissingInjectables.Add(e.Dst);
            log?.Invoke($"\"{e.Dst}\" is not included in this build — skipping.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

        // Once, ever: a second run would otherwise back up the copy WE wrote and lose the real
        // original the game shipped with.
        string backup = dst + BackupSuffix;
        if (e.Backup && File.Exists(dst) && !File.Exists(backup))
        {
            File.Copy(dst, backup);
            log?.Invoke($"the original \"{e.Dst}\" was kept as {Path.GetFileName(backup)}");
        }

        File.Copy(src, dst, overwrite: true);
        // Only an entry that carries a hash can be verified: one without it is refreshed on every
        // run precisely because we have nothing to compare the destination against.
        if (e.Sha256.Length > 0 && !HashMatches(dst, e.Sha256))
            throw new GamePatchException(L.T("core.patch.hashMismatch", new { file = e.Dst }));

        report.Applied.Add(e.Dst);
        log?.Invoke($"\"{e.Dst}\" was put in place" + (e.Reason.Length > 0 ? $" — {e.Reason}" : ""));
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

        var entries = new List<PatchEntry>(manifest.Common);
        var forVersion = manifest.Versions
            .FirstOrDefault(kv => string.Equals(kv.Key, versionId, StringComparison.OrdinalIgnoreCase)).Value;
        if (forVersion is not null) entries.AddRange(forVersion);
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
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            HashBufferSize, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
