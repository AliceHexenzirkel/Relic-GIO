using System.Diagnostics;
using System.IO.Compression;
using Relic.Core.Download;
using Relic.Core.Fiddler;
using Relic.Core.Isolation;
using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.Core.Install;

public enum InstallPhase { DownloadClient, DownloadAudio, Extract, Import, Patch, Fiddler, Shortcut, Done }

/// <summary>
/// A download that COMPLETED but is not the file (wrong length for the host that served it, or not a
/// zip). Distinct because its policy is the opposite of a mid-transfer failure's: the file is already
/// deleted, so there is no partial to protect, and the same host would serve the same bytes again — so
/// it must step to the next host at once, even though the attempt "progressed".
/// </summary>
public sealed class DownloadVerifyException : InvalidOperationException
{
    public DownloadVerifyException(string message) : base(message) { }
}

public sealed record InstallProgress(InstallPhase Phase, double Fraction, string Message);

/// <summary>
/// Orchestrates installing one version: download the client + the chosen voice packs (CDN, mirrors or
/// Drive, resumable per file) → extract them all into the game dir → apply the bundled per-version
/// fixes → configure Fiddler (rules + decrypt prefs) → desktop shortcut that plays via Relic → register
/// in state. Reports progress. The Fiddler root-cert trust (consented) and the silent Fiddler install
/// are triggered separately by the app so the user sees what's happening. <see cref="AddVoicesAsync"/>
/// later merges more voice packs into an installed version the same way.
/// </summary>
public sealed class InstallService
{
    // Copy buffer AND FileStream buffer for extraction: the 4 KB default behind ZipFile.OpenRead turns
    // a 15–32 GB archive into millions of syscalls, which would be most of the extraction time.
    private const int ExtractBufferSize = 1 << 20;

    // Workers take entries in chunks so each one reads a contiguous run of the archive instead of
    // leapfrogging the others; handing them out dynamically self-balances the mix of a few
    // hundred-MB .blk files and thousands of tiny assets.
    private const int ExtractChunk = 16;

    // Parallel inflate + several outstanding writes hide the per-file overhead that pins extraction
    // near 20 MB/s; capped so an install onto a spinning disk doesn't degenerate into head thrash.
    private static readonly int DefaultExtractWorkers = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    /// <summary>
    /// Worker count on a spinning disk. Deliberately NOT 1: the ~20 MB/s the comment above describes
    /// is per-file-overhead bound, not disk-bound, so a single worker does not inherit the drive's
    /// sequential rate — it inherits the serialization the parallelism exists to escape, which
    /// is slower on an HDD too. 2 is the conservative floor, not the minimum
    /// possible one. (A user who genuinely wants 1 can still set it by hand.)
    /// </summary>
    private const int HddExtractWorkers = 2;

    /// <summary>
    /// Resolves <see cref="RelicSettings.ExtractWorkers"/> (0 = auto) to a usable count.
    /// A manual value is obeyed as given — auto-detection must never override what someone typed.
    /// On auto, the medium decides: a spinning disk gets <see cref="HddExtractWorkers"/>, anything
    /// else (including a disk that cannot be identified) gets the CPU-derived default. Probing is
    /// best-effort by construction — see <see cref="StorageInfo.HasSeekPenalty"/> — so an
    /// unidentifiable disk changes nothing rather than guessing.
    /// </summary>
    /// <param name="targetPath">Where the files will actually land; its volume is the one probed.
    /// Null skips the probe (the spikes' synthetic dirs do not need it).</param>
    public static int ResolveWorkers(int configured, string? targetPath = null)
    {
        if (configured > 0) return Math.Min(configured, 32);
        if (targetPath is null) return DefaultExtractWorkers;

        bool? seekPenalty = StorageInfo.HasSeekPenalty(targetPath);
        if (seekPenalty == true)
        {
            Log.Info($"extract: spinning disk detected — {HddExtractWorkers} workers instead of {DefaultExtractWorkers}");
            return HddExtractWorkers;
        }
        return DefaultExtractWorkers;
    }

    /// <summary>Bytes written so far, shared by the workers of EVERY archive so the bar runs 0→100
    /// once for the whole extract instead of restarting when the next voice zip begins.</summary>
    private sealed class ExtractCounter { public long Copied; }

    private readonly RelicState _state;
    private readonly string _relicExePath;

    public InstallService(RelicState state, string relicExePath)
    {
        _state = state;
        _relicExePath = relicExePath;
    }

    /// <summary>Where version <paramref name="versionId"/> installs to. Single source of truth for the path.</summary>
    public static string GameDir(string installRoot, string versionId) =>
        Path.Combine(installRoot, $"Genshin {versionId}");

    /// <summary>The download cache (multi-GB zips) for a version, deleted after a successful install.</summary>
    public static string CacheDir(string installRoot, string versionId) =>
        Path.Combine(GameDir(installRoot, versionId), "_download");

    /// <summary>The download cache of an ALREADY-INSTALLED version — <c>&lt;GameDir&gt;\_download</c>,
    /// not <see cref="CacheDir"/>: an imported folder sits wherever the user put it, and the cache has
    /// to live inside the folder <see cref="RemoveAsync"/> deletes, never in an InstallRoot the version
    /// does not occupy.</summary>
    public static string CacheDirFor(InstalledVersion inst) => Path.Combine(inst.GameDir, "_download");

    public static string ClientCacheFile(string cache, string versionId) => Path.Combine(cache, $"client_{versionId}.zip");

    /// <summary>Cache file of one voice pack. English(US) KEEPS the plain <c>audio_&lt;id&gt;.zip</c>
    /// name on purpose: a multi-GB partial left by an older build resumes through its <c>.src</c>
    /// marker only under the same path — renaming it would orphan that download. The other languages
    /// get a short slug (<c>audio_&lt;id&gt;_ja.zip</c>); the language never reaches the name raw.</summary>
    public static string VoiceCacheFile(string cache, string versionId, string lang)
    {
        string canonical = VoiceLanguages.Canonical(lang)
            ?? throw new ArgumentException($"unknown voice language '{lang}'", nameof(lang));
        return Path.Combine(cache, canonical == VoiceLanguages.Default
            ? $"audio_{versionId}.zip"
            : $"audio_{versionId}_{VoiceLanguages.Slug(canonical)}.zip");
    }

    /// <summary>
    /// The languages an install/add request really means: canonical spelling, duplicates folded, in
    /// the order given. Throws BEFORE any byte moves — an empty choice or a language this version has
    /// no pack for is a request error, not something to discover after the 15 GB client download.
    /// Public so the Backend can answer the rpc synchronously with the same rule.
    /// </summary>
    public static List<string> ResolveVoices(GameVersionInfo v, IEnumerable<string>? voices)
    {
        var langs = new List<string>();
        foreach (string raw in voices ?? Array.Empty<string>())
        {
            string? c = VoiceLanguages.Canonical(raw);
            if (c is null)
                throw new InvalidOperationException(L.T("core.install.voices.unknown", new { lang = (raw ?? "").Trim(), version = v.Id }));
            if (!langs.Contains(c, StringComparer.Ordinal)) langs.Add(c);
        }
        if (langs.Count == 0) throw new InvalidOperationException(L.T("core.install.voices.none"));
        foreach (string l in langs)
            if (v.VoicePack(l) is null)
                throw new InvalidOperationException(L.T("core.install.voices.unknown", new { lang = l, version = v.Id }));
        return langs;
    }

    /// <summary>The default install: the client + the English(US) voices.</summary>
    public Task<InstalledVersion> InstallAsync(
        GameVersionInfo v, IProgress<InstallProgress> progress, CancellationToken ct = default) =>
        InstallAsync(v, new[] { VoiceLanguages.Default }, progress, ct);

    /// <param name="voices">Voice languages to install (catalogue keys, see <see cref="VoiceLanguages"/>);
    /// at least one. Each pack is its own resumable download with its own cache file and <c>.src</c>
    /// marker, so pause/resume and the host fallback work per pack.</param>
    public async Task<InstalledVersion> InstallAsync(
        GameVersionInfo v, IReadOnlyList<string> voices, IProgress<InstallProgress> progress, CancellationToken ct = default)
    {
        var langs = ResolveVoices(v, voices);
        var s = _state.Settings;
        string gameDir = GameDir(s.InstallRoot, v.Id);
        string cache = CacheDir(s.InstallRoot, v.Id);
        Directory.CreateDirectory(cache);

        // The setting is the PREFERRED host, not the only one: "cdn", "drive", or a mirror id from the
        // catalogue ("archive"). An id this version does not carry just leaves the default order.
        string preferredSource = s.Source;

        // 1: download the client (resumable)
        string clientZip = await DownloadSource(v.Client, preferredSource, ClientCacheFile(cache, v.Id),
            InstallPhase.DownloadClient, L.T("core.install.label.downloadClient"), progress, ct);
        // 2: the voice packs, one download each. Still ONE phase (DownloadAudio) for all of them: the
        // UI's pause gate keys on the phase name, and a bar that restarts per pack is exactly the
        // client→audio restart it already treats as "new stage".
        var voiceZips = await DownloadVoicePacks(v, langs, preferredSource, cache, progress, ct);

        // 3: extract everything into the same dir (merges GenshinImpact_Data)
        try
        {
            var archives = new List<(string Zip, string Label)> { (clientZip, L.T("core.install.label.extractClient")) };
            archives.AddRange(voiceZips.Select(z => (z.Zip, L.T("core.install.label.extractVoice", new { lang = z.Lang }))));
            await ExtractAllAsync(archives.ToArray(), gameDir, progress, ct, s.ExtractWorkers, s.GentleExtract);
        }
        catch
        {
            // Extraction rewrites gameDir IN PLACE (FileMode.Create per entry), so any abort here —
            // user cancel, corrupt archive, disk full — leaves torn files behind. On a fresh install
            // nothing references the dir yet, but on a REINSTALL the previous registration now points
            // at a corrupt dir and the library would keep offering PLAY on it. Unregister it: honest
            // ("Not installed"), and the kept cache makes the re-run cheap (downloads short-circuit).
            // Guarded: Save() can itself throw here (disk-full is one of the very failures this
            // catch handles) and that must not REPLACE the original exception — Backend keys the
            // cancelled-vs-error flow and the corrupt-cache cleanup on its exact type.
            try
            {
                if (_state.Installed.RemoveAll(i => i.Id == v.Id) > 0)
                {
                    Log.Info($"extract aborted over an installed version — {v.Id} was unregistered (torn directory)");
                    _state.Save();
                }
            }
            catch (Exception ex) { Log.Error("unregistering the version with the aborted extract failed (non-fatal)", ex); }
            throw;
        }

        // Last cancellation checkpoint. From here on the install runs to completion: a cancel landing
        // in the steps below would report "cancelled" for a version that is in fact installed. They
        // are all quick, and all but the bundled fixes are non-fatal and repairable later (play
        // re-runs the Fiddler setup), so finishing is strictly better.
        ct.ThrowIfCancellationRequested();

        // 4: the files the archives do not carry (2.8's global-metadata.dat, the Win11 injector).
        // BEFORE the registration below: DetectInjector only records an ayy/anime pair that is
        // already on disk, and a version whose required fix failed must stay unregistered rather
        // than offer PLAY on a client that cannot start.
        ApplyBundledFixes(v.Id, gameDir, progress, _state.Settings.Enhancements);

        // 5: register IMMEDIATELY after that — a Fiddler/shortcut failure below must never leave a
        // fully-extracted multi-GB install unregistered (both are repairable later: Fiddler is
        // re-checked at play time, the shortcut is cosmetic).
        var installed = RegisterVersion(v.Id, gameDir, "launcher");

        // 6 + 7: Fiddler LAST — installed + configured only AFTER the big download, so it never sits
        // in the download path (Fiddler eats RAM proxying multi-GB transfers). Both steps non-fatal.
        await FinalizeAsync(installed, progress);

        // 8: the install succeeded — the ~18–40 GB of cached zips are no longer needed.
        try { Directory.Delete(cache, recursive: true); }
        catch (Exception ex) { Log.Error("download cache cleanup (non-fatal)", ex); }

        progress.Report(new(InstallPhase.Done, 1, L.T("core.install.versionReady", new { version = v.Id })));
        return installed;
    }

    /// <summary>One resumable download per pack, labelled "i/n" so the user sees how many are left.</summary>
    private static async Task<List<(string Lang, string Zip)>> DownloadVoicePacks(
        GameVersionInfo v, IReadOnlyList<string> langs, string preferredSource, string cache,
        IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var zips = new List<(string Lang, string Zip)>();
        for (int i = 0; i < langs.Count; i++)
        {
            string lang = langs[i];
            var pack = v.VoicePack(lang)
                ?? throw new InvalidOperationException(L.T("core.install.voices.unknown", new { lang, version = v.Id }));
            string zip = await DownloadSource(pack, preferredSource, VoiceCacheFile(cache, v.Id, lang),
                InstallPhase.DownloadAudio, L.T("core.install.label.downloadVoice", new { lang, i = i + 1, n = langs.Count }),
                progress, ct);
            zips.Add((lang, zip));
        }
        return zips;
    }

    /// <summary>
    /// Adds voice languages to an INSTALLED version: downloads only the packs that are not on disk yet
    /// (cache under <see cref="CacheDirFor"/>) and extracts them into the live game folder. Returns the
    /// languages actually added (empty when every requested one was already there).
    /// </summary>
    /// <remarks>
    /// A merge into a live install is safe only because a language zip carries nothing but its own
    /// <c>GenshinImpact_Data/StreamingAssets/Audio/…/&lt;Lang&gt;/*.pck</c> files and its
    /// <c>Audio_&lt;Lang&gt;_pkg_version</c> marker (docs/HOTFIX-1.6.md §7: the central directories of all
    /// four languages match the base output).
    /// <see cref="VerifyVoicePackLayout"/> enforces exactly that before a byte is written: a pack that
    /// carried a client file would otherwise overwrite the patched 2.8 <c>global-metadata.dat</c>, and
    /// unlike the full install nothing re-applies the bundled fixes afterwards. No Fiddler, no patch,
    /// no shortcut — the version is registered already. The caller holds the play-session gate.
    /// </remarks>
    public async Task<IReadOnlyList<string>> AddVoicesAsync(
        GameVersionInfo v, IReadOnlyList<string> voices, IProgress<InstallProgress> progress, CancellationToken ct = default)
    {
        var inst = _state.FindInstalled(v.Id)
            ?? throw new InvalidOperationException(L.T("core.install.voices.notInstalled", new { version = v.Id }));
        var langs = ResolveVoices(v, voices);
        var todo = new List<string>();
        foreach (string lang in langs)
        {
            if (VoiceLanguages.IsOnDisk(inst.GameDir, lang))
            {
                Log.Info($"add voices {v.Id}: {lang} is already installed — skipped");
                progress.Report(new(InstallPhase.DownloadAudio, 0, L.T("core.install.voices.alreadyPresent", new { lang })));
            }
            else todo.Add(lang);
        }
        if (todo.Count == 0)
        {
            // Self-healing: everything asked for is on disk, so if the record disagrees (an add whose
            // last step did not run, a pack extracted by hand) this is the moment to reconcile it —
            // otherwise the Library keeps offering "Add" for a language that is already installed and
            // every click lands right back here.
            SyncVoices(inst);
            progress.Report(new(InstallPhase.Done, 1, L.T("core.install.voices.nothingToDo")));
            return todo;
        }

        var s = _state.Settings;
        string gameDir = inst.GameDir;
        string cache = CacheDirFor(inst);
        Directory.CreateDirectory(cache);
        Log.Info($"add voices {v.Id}: {string.Join(", ", todo)} -> {gameDir} (cache {cache}, source={s.Source})");

        var zips = await DownloadVoicePacks(v, todo, s.Source, cache, progress, ct);
        // Central-directory pass only (seconds), off the calling thread like PrepareArchive.
        await Task.Run(() => { foreach (var z in zips) VerifyVoicePackLayout(z.Zip, z.Lang, ct); }, ct);

        try
        {
            await ExtractAllAsync(
                zips.Select(z => (z.Zip, L.T("core.install.label.extractVoice", new { lang = z.Lang }))).ToArray(),
                gameDir, progress, ct, s.ExtractWorkers, s.GentleExtract);
        }
        catch
        {
            // Mirror of the full install's unregister-on-torn-extract rule, scoped to THIS run's packs:
            // the marker is what "installed" means (see VoiceLanguages.PkgVersionFile), so a torn add
            // must not leave it behind — the language would read as installed, the Library would stop
            // offering Add, and the half-written .pck files would never be repaired. Deleting it keeps
            // the retry honest (the cache short-circuits, the extract rewrites in place). Best-effort:
            // this catch handles disk-full, and a second failure must not replace the first.
            foreach (var z in zips)
            {
                try { TryDelete(VoiceLanguages.PkgVersionFile(gameDir, z.Lang)); }
                catch (Exception ex) { Log.Error($"add voices {v.Id}: could not drop the {z.Lang} marker after the aborted extract (non-fatal)", ex); }
            }
            throw;
        }

        // NO cancellation checkpoint here: the packs are extracted and their markers are on disk, so
        // the language IS installed. Throwing at this point (a Stop pressed during the last seconds of
        // the extract) would end the job as "cancelled" and skip the two lines below, leaving the record
        // claiming the opposite of what the disk holds — and the Library would then offer "Add" for a
        // language that is already there. Recording it is the honest end of the work.
        SyncVoices(inst);

        // Only THIS run's files, never the whole folder: a cache shared with the client zip also holds
        // another language's paused partial (audio_<id>_<slug>.zip + its .src marker) — a multi-GB
        // download the UI promised was "saved for resuming". The folder goes only once it is empty.
        foreach (var z in zips)
        {
            TryDelete(z.Zip);
            TryDelete(z.Zip + SrcSuffix);
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(cache).Any()) Directory.Delete(cache);
        }
        catch (Exception ex) { Log.Error("voice pack cache cleanup (non-fatal)", ex); }

        Log.Info($"add voices {v.Id}: done — installed languages now {string.Join(", ", inst.Voices)}");
        progress.Report(new(InstallPhase.Done, 1,
            L.T("core.install.voices.ready", new { langs = string.Join(", ", todo), version = v.Id })));
        return todo;
    }

    /// <summary>
    /// Refuses a "voice pack" whose entries are not voice files. Allowed: anything under
    /// <c>GenshinImpact_Data/StreamingAssets/Audio/</c>, the directory entries leading to it, and the
    /// root <c>Audio_&lt;Lang&gt;_pkg_version</c> marker. Everything the client zip carries — the exe, the
    /// Managed metadata, <c>pkg_version</c> — is outside that set, which is the point: this is the guard
    /// that makes extracting into a LIVE install safe (see <see cref="AddVoicesAsync"/>).
    /// </summary>
    private static void VerifyVoicePackLayout(string zip, string lang, CancellationToken ct)
    {
        const string AudioPrefix = "GenshinImpact_Data/StreamingAssets/Audio/";
        using var archive = ZipFile.OpenRead(zip);
        foreach (var e in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string name = e.FullName.Replace('\\', '/').TrimStart('/');
            if (name.Length == 0) continue;
            bool ok = name.StartsWith(AudioPrefix, StringComparison.OrdinalIgnoreCase)
                      || (name.EndsWith('/') && AudioPrefix.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                      || (e.Name.Length > 0 && !name.Contains('/')
                          && System.Text.RegularExpressions.Regex.IsMatch(name, @"^Audio_[^/\\]+_pkg_version$"));
            if (!ok)
            {
                Log.Error($"add voices: {Path.GetFileName(zip)} ({lang}) carries a non-voice entry \"{e.FullName}\" — refusing to unpack it over the installed game");
                throw new InvalidOperationException(L.T("core.install.voices.badPack", new { lang, entry = e.FullName }));
            }
        }
    }

    /// <summary>
    /// Registers an ALREADY-DOWNLOADED copy of the game: the user picks its folder, Relic validates
    /// it and registers it in place — no download, no copy, the files stay exactly where they are —
    /// then runs the same Fiddler + shortcut finalization as a normal install.
    /// </summary>
    public async Task<InstalledVersion> ImportLocalAsync(
        GameVersionInfo v, string sourceDir, IProgress<InstallProgress> progress, CancellationToken ct = default)
    {
        progress.Report(new(InstallPhase.Import, 0.05, L.T("core.install.import.checking")));
        // Trim('"'): Explorer's "Copy as path" wraps the path in quotes, and a leading quote makes
        // GetFullPath resolve the whole thing as a RELATIVE path — a misleading "does not exist" error.
        string gameDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDir.Trim().Trim('"')));
        if (!Directory.Exists(gameDir))
            throw new InvalidOperationException(L.T("core.install.import.folderMissing", new { dir = gameDir }));
        if (!File.Exists(Path.Combine(gameDir, "GenshinImpact.exe")))
            throw new InvalidOperationException(L.T("core.install.import.noExe"));
        if (!Directory.Exists(Path.Combine(gameDir, "GenshinImpact_Data")))
            throw new InvalidOperationException(L.T("core.install.import.noData"));

        // Best-effort version check: launcher-made installs carry config.ini with game_version=X.Y.Z.
        // A repack without the file simply skips the check — but when the file IS there and points at
        // another version, registering it would produce a client that can't talk to its server.
        string? found = TryReadGameVersion(Path.Combine(gameDir, "config.ini"));
        if (found is not null && !string.Equals(found, v.Id, StringComparison.Ordinal))
            throw new InvalidOperationException(L.T("core.install.import.versionMismatch", new { found, version = v.Id }));

        // One folder = one registration: play/profile-swap/uninstall key on the id, and two ids over
        // the same dir would fight over the same files.
        var clash = _state.Installed.FirstOrDefault(i =>
            !string.Equals(i.Id, v.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(SafeFullPath(i.GameDir), gameDir, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
            throw new InvalidOperationException(L.T("core.install.import.alreadyRegistered", new { version = clash.Id }));

        // Last cancellation checkpoint — registration + finalize run to completion (same rule as the
        // download path: a cancel past this point would report "cancelled" for an installed version).
        ct.ThrowIfCancellationRequested();
        Log.Info($"import local: version={v.Id} dir={gameDir}");
        progress.Report(new(InstallPhase.Import, 0.4, L.T("core.install.import.registering")));

        // A folder the user downloaded elsewhere is the likeliest one to be missing these files.
        ApplyBundledFixes(v.Id, gameDir, progress, _state.Settings.Enhancements);
        var installed = RegisterVersion(v.Id, gameDir, "import");
        await FinalizeAsync(installed, progress);
        progress.Report(new(InstallPhase.Done, 1, L.T("core.install.versionReady", new { version = v.Id })));
        return installed;
    }

    /// <summary>The game_version (major.minor) from config.ini, or null when it is missing/unreadable.</summary>
    private static string? TryReadGameVersion(string configIni)
    {
        try
        {
            if (!File.Exists(configIni)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(configIni), @"game_version\s*=\s*([0-9]+\.[0-9]+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    /// <summary>Copies the payload files this version needs into the game dir. A required one that
    /// cannot be applied stops the install (<see cref="GamePatchException"/>) — the client would
    /// not start anyway; the optional ones are reported instead, never silently. The in-game
    /// enhancements DLL (manifest <c>inject</c> entries) is part of the plan only while
    /// <paramref name="withEnhancements"/> is on, and a missing one gets its own, milder wording —
    /// the game works without it.</summary>
    private static void ApplyBundledFixes(
        string versionId, string gameDir, IProgress<InstallProgress> progress, bool withEnhancements)
    {
        progress.Report(new(InstallPhase.Patch, 0.3, L.T("core.install.patch.preparing")));
        var report = GamePatcher.Apply(versionId, gameDir, m => Log.Info($"patch {versionId}: {m}"),
            withInjectables: withEnhancements);
        Log.Info($"patch {versionId}: {report.Summary}");
        // The likeliest optional casualty is mhynot2.dll quarantined by antivirus — it IS an
        // anti-cheat bypass — and without it a Windows 11 install cannot start the client at all.
        // The enhancements DLL is the other candidate and the opposite case: worth a note, never a
        // "may not start" — so it only gets the mild wording when it is the ONLY thing missing.
        string message;
        if (report.Missing.Count == 0)
            message = L.T("core.install.patch.complete");
        else if (report.Missing.Count == report.MissingInjectables.Count)
            message = L.T("core.install.patch.enhancementsMissing", new { files = string.Join(", ", report.MissingInjectables) });
        else
            message = L.T("core.install.patch.missingWarning", new { files = string.Join(", ", report.Missing) });
        progress.Report(new(InstallPhase.Patch, 1, message));
    }

    /// <summary>Registers <paramref name="gameDir"/> as version <paramref name="versionId"/> and
    /// persists the state — shared by the download install and the local import.</summary>
    private InstalledVersion RegisterVersion(string versionId, string gameDir, string origin)
    {
        var installed = new InstalledVersion
        {
            Id = versionId,
            GameDir = gameDir,
            Origin = origin,
            ProfileId = "priv" + versionId.Replace(".", ""),
            ShortcutName = _state.Settings.CreateShortcut ? L.T("core.install.shortcutName", new { version = versionId }) : null,
        };
        DetectInjector(installed); // pick up ayy/anime launcher if present alongside the game
        // DETECTED, not "selected": a reinstall over a folder that already had Japanese keeps it, and an
        // imported folder gets whatever packs it really carries.
        DetectVoices(installed);
        _state.Installed.RemoveAll(i => i.Id == versionId);
        _state.Installed.Add(installed);
        _state.SelectedVersionId = versionId;
        _state.Save();
        return installed;
    }

    /// <summary>The repairable tail of every install: configure Fiddler (re-checked at play time on
    /// failure) and create the desktop shortcut (cosmetic). Never throws, never cancellable — the
    /// version is already registered, so finishing is strictly better than stopping halfway.</summary>
    private async Task FinalizeAsync(InstalledVersion installed, IProgress<InstallProgress> progress)
    {
        var s = _state.Settings;
        progress.Report(new(InstallPhase.Fiddler, 0.2, L.T("core.install.fiddler.configuring")));
        try
        {
            // CancellationToken.None on purpose: past the registration checkpoint the install must
            // finish, so a user cancel that lands here is ignored rather than half-applied.
            await FiddlerAutomation.EnsureReadyAsync(
                s.ServerHost, s.ServerPort, s.FiddlerDecrypt, s.InstallCert, s.FiddlerNoUac, CancellationToken.None);
            progress.Report(new(InstallPhase.Fiddler, 1, L.T("core.install.fiddler.done")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Fiddler setup during install failed (non-fatal — re-checked at play time)", ex);
            progress.Report(new(InstallPhase.Fiddler, 1, L.T("core.install.fiddler.failed")));
        }

        // Shortcut that plays via Relic (so it does the profile swap + Fiddler). Cosmetic — non-fatal.
        if (s.CreateShortcut && installed.ShortcutName is not null)
        {
            progress.Report(new(InstallPhase.Shortcut, 0.5, L.T("core.install.shortcut.creating")));
            try
            {
                Shortcut.CreateOnDesktop(installed.ShortcutName, _relicExePath, $"--play {installed.Id}",
                    iconPath: Path.Combine(installed.GameDir, "GenshinImpact.exe"));
            }
            catch (Exception ex)
            {
                Log.Error("desktop shortcut creation failed (non-fatal)", ex);
                progress.Report(new(InstallPhase.Shortcut, 1, L.T("core.install.shortcut.failed")));
            }
        }
    }

    public sealed record RemoveReport(bool FilesDeleted, IReadOnlyList<string> Leftovers);

    /// <summary>
    /// Remove an installed version. Order is not negotiable: (1) hold the cross-process session gate for
    /// the whole operation; (2) if this version's profile is the ACTIVE one, require a sustained
    /// all-clear and restore live first (swapping under a running client is the core invariant);
    /// (3) delete the desktop shortcut(s) that launch it — matched by TargetPath + "--play &lt;id&gt;", never
    /// by target alone (the launcher's own shortcuts point at the same exe); (4) delete its profile slot;
    /// (5) unregister + reset the selection + Save; (6) THEN delete the files when asked — the whole
    /// GameDir incl. <c>_download</c>, but only a folder that really holds GenshinImpact.exe and is not a
    /// drive root (an imported folder is an arbitrary user path). Files that resist are reported, not fatal.
    /// </summary>
    public async Task<RemoveReport> RemoveAsync(string versionId, bool deleteFiles,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var inst = _state.FindInstalled(versionId)
            ?? throw new InvalidOperationException(L.T("core.install.remove.notInstalled", new { version = versionId }));
        using var gate = new Semaphore(1, 1, Play.PlaySession.SessionGateName);
        if (!gate.WaitOne(0)) throw new InvalidOperationException(L.T("core.install.remove.sessionBusy"));
        try
        {
            var profiles = ProfileStore.ForGenshin();
            if (string.Equals(profiles.ActiveId(), inst.ProfileId, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new(InstallPhase.Import, 0.05, L.T("core.install.remove.restoringLive")));
                if (!await ProcessGuard.NoneRunningSustainedAsync())
                    throw new InvalidOperationException(L.T("core.install.remove.gameRunning"));
                if (profiles.IsTainted())
                {
                    // The official client ran on this still-loaded profile (ProfileStore.MarkTainted):
                    // live goes back without exporting the slot — which is about to be deleted anyway.
                    Log.Info($"remove {inst.Id}: profile '{inst.ProfileId}' is tainted (the official client ran on it) — restoring live without exporting it");
                    profiles.RestoreLiveDiscarding();
                }
                else
                {
                    profiles.SwapTo(ProfileStore.LiveId, activeId: inst.ProfileId);
                }
            }

            progress?.Report(new(InstallPhase.Import, 0.1, L.T("core.install.remove.shortcut")));
            try
            {
                if (_relicExePath.Length > 0) Shortcut.DeleteMatching(_relicExePath, $"--play {inst.Id}");
                if (!string.IsNullOrWhiteSpace(inst.ShortcutName))
                {
                    string lnk = Path.Combine(Shortcut.DesktopDir, inst.ShortcutName + ".lnk");
                    if (File.Exists(lnk)) File.Delete(lnk);
                }
            }
            catch (Exception ex) { Log.Info($"remove {inst.Id}: shortcut cleanup: {ex.Message}"); }

            progress?.Report(new(InstallPhase.Import, 0.15, L.T("core.install.remove.profile")));
            try { profiles.DeleteProfile(inst.ProfileId); }
            catch (Exception ex) { Log.Info($"remove {inst.Id}: profile slot {inst.ProfileId}: {ex.Message}"); }

            _state.Installed.RemoveAll(i => string.Equals(i.Id, inst.Id, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_state.SelectedVersionId, inst.Id, StringComparison.OrdinalIgnoreCase))
                _state.SelectedVersionId = _state.Installed.FirstOrDefault()?.Id;
            _state.Save();
            Log.Info($"remove: unregistered {inst.Id} (origin={inst.Origin}, dir={inst.GameDir}, deleteFiles={deleteFiles})");

            var leftovers = new List<string>();
            if (deleteFiles)
            {
                string dir = inst.GameDir;
                string full = SafeFullPath(dir);
                bool safe = Directory.Exists(dir)
                    && File.Exists(Path.Combine(dir, "GenshinImpact.exe"))
                    && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), Path.TrimEndingDirectorySeparator(full), StringComparison.OrdinalIgnoreCase);
                if (!safe) throw new InvalidOperationException(L.T("core.install.remove.unsafeDir", new { dir }));
                await DeleteTreeAsync(dir, progress, leftovers, ct);
            }
            progress?.Report(new(InstallPhase.Done, 1, L.T("core.install.remove.done")));
            return new RemoveReport(deleteFiles, leftovers);
        }
        finally { gate.Release(); }
    }

    private static async Task DeleteTreeAsync(string dir, IProgress<InstallProgress>? progress, List<string> leftovers, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        int done = 0; long lastReport = 0;
        await Task.Run(() =>
        {
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
                catch (Exception ex) { leftovers.Add(f); Log.Info($"remove: {f}: {ex.Message}"); }
                done++;
                if (Environment.TickCount64 - lastReport > 200)
                {
                    lastReport = Environment.TickCount64;
                    progress?.Report(new(InstallPhase.Extract, 0.2 + 0.8 * done / Math.Max(1, files.Count),
                        L.T("core.install.remove.deleting", new { done, total = files.Count })));
                }
            }
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { if (Directory.Exists(dir)) { leftovers.Add(dir); Log.Info($"remove: dir {dir}: {ex.Message}"); } }
        }, ct);
    }

    /// <summary>One host this archive can be pulled from, with the byte count THAT host serves.</summary>
    /// <param name="Size">null when only the host can tell us (Drive: see <see cref="Downloader.ProbeAsync"/>).</param>
    public readonly record struct DownloadCandidate(string Id, string Label, string Url, long? Size)
    {
        public bool IsDrive => Id == DriveSourceId;
    }

    private const string DriveSourceId = "drive";
    private const string CdnSourceId = "cdn";

    /// <summary>
    /// Every host that can serve this archive, best-first: the user's choice, then the official CDN,
    /// then the mirrors, then Drive.
    /// </summary>
    /// <remarks>
    /// Two ordering decisions:
    /// <list type="bullet">
    /// <item>The CDN leads the leftovers because it has no quota and is far faster than the
    /// Internet Archive mirror. A mirror is insurance against the CDN going away,
    /// not a speed-up - never promote one to the front on its own.</item>
    /// <item>Drive goes LAST among the leftovers even though it is a fast host when it works.
    /// Its failure mode is a daily quota shared across the OWNER's files, so it
    /// is precisely the host most likely to be refusing at the moment a fallback runs - and every
    /// attempt against it burns more of that quota.</item>
    /// </list>
    /// A <paramref name="preferred"/> that no candidate matches (a mirror id this version does not
    /// carry, e.g. "archive" on 2.8) is not an error: the list simply stays in its default order.
    /// </remarks>
    public static List<DownloadCandidate> BuildCandidates(GameSourceInfo src, string preferred)
    {
        var all = new List<DownloadCandidate>();
        if (!string.IsNullOrWhiteSpace(src.CdnUrl))
            all.Add(new DownloadCandidate(CdnSourceId, L.T("core.download.source.cdn"), src.CdnUrl, src.Size > 0 ? src.Size : null));
        foreach (var m in src.Mirrors)
            if (!string.IsNullOrWhiteSpace(m.Url) && !string.IsNullOrWhiteSpace(m.Id))
                all.Add(new DownloadCandidate(
                    m.Id, string.IsNullOrWhiteSpace(m.Label) ? m.Id : m.Label, m.Url, m.Size > 0 ? m.Size : null));
        if (!string.IsNullOrWhiteSpace(src.DriveId))
            // No size from the catalogue: nothing records what Drive serves, and the Drive copy is not
            // guaranteed to be the same build as the CDN one (the CDN can carry 1.6.0 while Drive serves
            // 1.6.1, 16.384 bytes shorter). ProbeAsync reports the
            // real length before the transfer starts, and it is then recorded in the `.src` marker so a
            // finished Drive download is still recognised as finished on the next run.
            all.Add(new DownloadCandidate(DriveSourceId, "Google Drive", Downloader.DriveDirectUrl(src.DriveId!), null));

        int pref = all.FindIndex(c => string.Equals(c.Id, preferred, StringComparison.OrdinalIgnoreCase));
        if (pref > 0)
        {
            var chosen = all[pref];
            all.RemoveAt(pref);
            all.Insert(0, chosen);
        }
        return all;
    }

    /// <summary>One line of an exception, for a message the user reads rather than a stack trace.</summary>
    private static string Short(Exception ex)
    {
        string m = ex.Message.Trim();
        int nl = m.IndexOfAny(['\r', '\n']);
        if (nl > 0) m = m[..nl];
        return m.Length > 140 ? m[..137] + "..." : m;
    }

    /// <summary>Every host refused, and each one gets to say why.</summary>
    private static Exception AllSourcesFailed(string label, List<string> failures, Exception inner) =>
        new InvalidOperationException(
            L.T("core.download.allSourcesFailed", new { label, reasons = string.Join(" · ", failures) }), inner);

    /// <summary>
    /// The <c>.src</c> marker beside a partial: line 1 the url it came from, line 2 the SETTING in
    /// force when that url was chosen, line 3 the byte count that host serves (empty when unknown).
    /// </summary>
    /// <remarks>
    /// Line 2 is what tells an automatic fallback apart from the user changing their mind, and the two
    /// must not be answered the same way. "Preferred Drive, ended up on the CDN because Drive refused"
    /// has to survive a pause/resume — recomputing from the unchanged setting would pick Drive again
    /// and throw the CDN partial away, which is the loss the marker exists to prevent. But "the
    /// download was crawling so I switched Source to CDN in Settings" is the OPPOSITE instruction, and
    /// honouring the marker there would silently keep pulling from the host the user just rejected.
    /// Comparing the recorded setting with the current one separates them exactly.
    /// <para>
    /// Line 3 exists because the catalogue cannot answer "is this file finished?" for every host:
    /// Drive's length is never in versions.json (only its probe knows it), and a url can leave the
    /// catalogue entirely when a version is re-pointed at another build. Without a recorded length a
    /// COMPLETE multi-GB archive is not recognised as complete, gets re-attempted, and the first host
    /// switch deletes it — so the length the transfer was verified against is written down next to it.
    /// </para>
    /// A legacy one-line marker reports nulls and is adopted: it can only have been written
    /// by a build whose single fallback edge was Drive→CDN, and adopting it never destroys bytes.
    /// </remarks>
    private static (string? Url, string? Preferred, long? Total) ReadSourceMarker(string srcMarker)
    {
        try
        {
            if (!File.Exists(srcMarker)) return (null, null, null);
            var lines = File.ReadAllLines(srcMarker);
            if (lines.Length == 0 || lines[0].Length == 0) return (null, null, null);
            string? pref = lines.Length > 1 && lines[1].Length > 0 ? lines[1] : null;
            long? total = lines.Length > 2 && long.TryParse(lines[2], out long t) && t > 0 ? t : null;
            return (lines[0], pref, total);
        }
        catch { return (null, null, null); } // unreadable — treated as "no marker"
    }

    /// <summary>
    /// Records where the bytes on disk came from. Written through a temp file and moved into place:
    /// a torn in-place write would leave a truncated url, which reads back as "different host" and
    /// costs the user a multi-GB partial — the one outcome this file exists to prevent.
    /// </summary>
    private static void WriteSourceMarker(string srcMarker, string url, string preferred, long? total, string label)
    {
        try
        {
            string tmp = srcMarker + ".tmp";
            File.WriteAllText(tmp, url + "\n" + preferred + "\n" + (total?.ToString() ?? ""));
            File.Move(tmp, srcMarker, overwrite: true);
        }
        catch (Exception ex) { Log.Error($"download {label}: could not write the source marker (non-fatal)", ex); }
    }

    /// <summary>
    /// How many bytes the attempt in progress has actually put on disk. Deliberately NOT derived from
    /// the file's length: the destination may still hold ANOTHER host's partial (see
    /// <c>startFresh</c>), so comparing lengths would read the previous host's bytes as this attempt's
    /// progress — and <see cref="ShouldTryOtherSource"/> would then abandon a host that was delivering
    /// fine, or keep one that delivered nothing. Counted from the downloader's own progress reports.
    /// </summary>
    private sealed class AttemptStats { public long Written; }

    /// <summary>
    /// <see cref="IProgress{T}"/> whose callback runs SYNCHRONOUSLY on the thread that reports, unlike
    /// <see cref="Progress{T}"/> which marshals every report to the thread pool. The download loop's
    /// reports have to be seen in order and before the awaiting code continues: they are what count
    /// the attempt's bytes and what decides when the source marker may be updated.
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    /// <summary>
    /// Is this failure worth ONE immediate retry against the SAME host, before stepping down the chain?
    /// </summary>
    /// <remarks>
    /// Stepping down costs real speed — the official CDN is far faster than the archive.org
    /// mirror — so a connect-time blip must not hand the user a far slower host for a 15–32 GB
    /// transfer. Refusals are excluded because they answer the same way twice: a served error page, a
    /// 4xx, a cancel, a full disk. The caller retries only when the attempt wrote nothing, so this
    /// never re-runs a transfer that was actually progressing.
    /// </remarks>
    public static bool IsTransientFailure(Exception ex) =>
        ex is not OperationCanceledException
        && ex is not SourceServedPageException
        && ex is not DownloadVerifyException // the same host serves the same (wrong) bytes again
        && !(ex is IOException io && (io.HResult & 0xFFFF) is 0x27 or 0x70)
        && ex is not HttpRequestException { StatusCode: >= System.Net.HttpStatusCode.BadRequest
                                                        and < System.Net.HttpStatusCode.InternalServerError };

    /// <summary>
    /// A partial on disk came from <paramref name="recordedPreference"/>'s run. Should the host it came
    /// from still win over the host <paramref name="currentPreference"/> would pick today?
    /// </summary>
    /// <remarks>
    /// Public for the download spike: this single comparison separates "we fell back automatically,
    /// keep going" from "the user switched Source, obey them", and each mistake has its own cost. Answer
    /// "no" to the first and every pause/resume after a fallback re-downloads 15–32 GB. Answer "yes" to
    /// the second and the app keeps pulling from the host the user just rejected — which is exactly the
    /// move someone makes when a host has gone slow, so it would defeat the one fix available to them.
    /// A null recorded preference is a marker from an older build (url only) and is adopted, because
    /// in such a build the only host swap that could have produced it was the automatic Drive→CDN one.
    /// </remarks>
    public static bool ShouldResumeOtherHost(string? recordedPreference, string currentPreference) =>
        recordedPreference is null
        || string.Equals(recordedPreference, currentPreference, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> DownloadSource(
        GameSourceInfo src, string preferredSource, string dest, InstallPhase phase, string label,
        IProgress<InstallProgress> progress, CancellationToken ct)
    {
        // A blank setting would write an EMPTY line 2 in the marker, which reads back as "legacy marker"
        // and would make the next Source change look like no change at all. Normalise once, here.
        if (string.IsNullOrWhiteSpace(preferredSource)) preferredSource = CdnSourceId;

        string srcMarker = dest + SrcSuffix;
        var plan = BuildCandidates(src, preferredSource);
        if (plan.Count == 0)
            throw new InvalidOperationException(L.T("core.download.noSource", new { label }));

        // The source decision must OUTLIVE the run that made it. A pause keeps the partial on disk on
        // purpose ("resuming is what makes pause cheap to undo"), but a choice of host kept only in a
        // local would be lost: the next run would recompute it from the unchanged setting, pick the
        // preferred host again, and the other host's partial would be discarded on the marker
        // mismatch. Every pause/resume after a fallback would cost the whole transfer, silently -
        // for exactly the users the fallback exists to serve. The `.src` marker is already the
        // persistent record of which host a partial came from, so read it back and honour it.
        var (prevUrl, prevPreferred, prevTotal) = ReadSourceMarker(srcMarker);
        // ...but only while the user's own choice has not changed since. A different Source in Settings is
        // an explicit instruction to leave that host, not a decision to carry forward — see the remarks
        // on ReadSourceMarker. A completed file is still recognised below either way: nobody should
        // re-download 15 GB because they touched a radio button.
        if (File.Exists(dest) && prevUrl is not null && ShouldResumeOtherHost(prevPreferred, preferredSource))
        {
            int pinned = plan.FindIndex(c => string.Equals(c.Url, prevUrl, StringComparison.Ordinal));
            // -1 means the url the partial came from is no longer in versions.json — a re-pointed
            // version, a rotated link. The transfer restarts from a catalogued host (and a COMPLETE
            // file is still recognised above, by the length recorded in the marker), but it is worth a
            // line in the log: otherwise "it started over for no reason" has no explanation anywhere.
            if (pinned < 0)
                Log.Info($"download {label}: the marker points at a source that is no longer in the catalogue ({prevUrl})");
            if (pinned > 0)
            {
                Log.Info($"download {label}: the partial file comes from \"{plan[pinned].Label}\" " +
                         "(an earlier run switched) — continuing from there");
                var keep = plan[pinned];
                plan.RemoveAt(pinned);
                plan.Insert(0, keep);
            }
        }

        // Already fully downloaded (a finished zip would otherwise Range past EOF -> 416 forever, and
        // one host switch would DELETE it). The length recorded in the marker is the authority: it is
        // the length this very file was verified against, and it is the only answer that works for
        // every host. The catalogue cannot stand in for it — Drive's length is never in versions.json
        // (only its probe knows it), and a url drops out of the catalogue whenever a version is
        // re-pointed at another build. The candidate sizes are the fallback for older markers without
        // line 3; they are only trusted for the host the partial actually came from, because
        // two hosts can serve the same length and different bytes (two builds of 1.6's audio do).
        if (File.Exists(dest) && StartsWithZipMagic(dest))
        {
            long have = new FileInfo(dest).Length;
            bool complete = prevTotal is > 0
                ? prevTotal.Value == have
                : plan.Any(c => c.Size is > 0 && c.Size.Value == have
                                && (prevUrl is null || string.Equals(c.Url, prevUrl, StringComparison.Ordinal)));
            if (complete)
            {
                Log.Info($"download {label}: cache already complete ({dest}) — skipping the download");
                progress.Report(new(phase, 1, L.T("core.download.alreadyDownloaded", new { label })));
                return dest;
            }
        }

        // A partial file that doesn't start with "PK" is a poisoned cache (an HTML page saved as
        // the zip) - resuming would append real zip bytes after it. Delete and start clean.
        if (File.Exists(dest) && !StartsWithZipMagic(dest))
        {
            Log.Info($"download {label}: corrupt cache (does not start with PK) — deleting {dest}");
            File.Delete(dest);
        }

        // Verify what landed on disk BEFORE it gets baked into the game dir hours later at extract.
        // A local function because EVERY attempt below must run it: a truncated transfer is a source
        // failure like any other, and it is only detectable here, after the copy "succeeded".
        void Verify(long? total)
        {
            long finalLen = File.Exists(dest) ? new FileInfo(dest).Length : 0;
            if (!StartsWithZipMagic(dest))
            {
                TryDelete(dest);
                throw new DownloadVerifyException(L.T("core.download.verify.notZip", new { label }));
            }
            if (total is > 0 && finalLen != total.Value)
            {
                TryDelete(dest);
                throw new DownloadVerifyException(
                    L.T("core.download.verify.incomplete", new { label, have = Human(finalLen), total = Human(total.Value) }));
            }
        }

        // What every host that refused actually said. Without this the user is shown only the LAST
        // candidate's error — "Google Drive: daily limit" to someone who chose the CDN and never
        // knew their own host 403'd — and the log is the only place the real cause survives.
        var failures = new List<string>();
        for (int i = 0; i < plan.Count; i++)
        {
            var cand = plan[i];
            bool haveNext = i + 1 < plan.Count;
            long? expectedTotal = cand.Size;

            if (cand.IsDrive)
            {
                // Drive serves quota/virus-scan interstitials as HTML with 200, and quota errors as 403.
                // Probe the first bytes; anything that isn't a zip means this host is refusing today.
                // The probe CANNOT be trusted the other way round - Google enforces the daily quota on
                // the FULL transfer only, so a 1 KB Range still answers 206 + "PK" while the very next
                // full GET returns the "Quota exceeded" page.
                // That is why the attempt below still has to survive a refusal mid-transfer.
                try
                {
                    var probe = await Downloader.ProbeAsync(cand.Url, ct: ct);
                    if (probe.Status == 403 || probe.IsHtml || !probe.LooksLikeZip)
                    {
                        Log.Info($"download {label}: {cand.Label} does not serve a zip (status={probe.Status}, html={probe.IsHtml})" +
                                 (haveNext ? $" — switching to \"{plan[i + 1].Label}\"" : " — no other source left"));
                        var refused = new SourceServedPageException(
                            L.T("core.download.probe.notZip", new { source = cand.Label, status = probe.Status }), isQuota: probe.IsHtml);
                        failures.Add($"{cand.Label}: {(probe.IsHtml ? L.T("core.download.probe.servedPage") : L.T("core.download.probe.status", new { status = probe.Status }))}");
                        if (haveNext) continue;
                        throw AllSourcesFailed(label, failures, refused);
                    }
                    if (probe.Total is > 0) expectedTotal = probe.Total;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && haveNext)
                {
                    Log.Error($"download {label}: the {cand.Label} probe failed — switching to \"{plan[i + 1].Label}\"", ex);
                    failures.Add($"{cand.Label}: {Short(ex)}");
                    continue;
                }
            }

            // Is this host the one the bytes on disk came from? If not, the transfer must start over
            // — the copies are not always identical and a splice passes every length check, only dying
            // at the extract CRC hours later. But the old partial is NOT deleted here: `startFresh`
            // makes the downloader truncate the destination when the new host's response has passed
            // the page/zip sniff, i.e. only once that host has proved it can actually serve the file.
            // A fallback onto a host that then refuses must not cost the whole previous transfer.
            bool startFresh = File.Exists(dest)
                              && ReadSourceMarker(srcMarker).Url is string onDisk
                              && !string.Equals(onDisk, cand.Url, StringComparison.Ordinal);

            for (int attempt = 0; ; attempt++)
            {
                // Bytes THIS attempt wrote — never the file's length, which may still be the previous
                // host's partial (see startFresh above) or, after Verify deleted a short download, zero.
                var stats = new AttemptStats();
                try
                {
                    await AttemptDownloadAsync(
                        cand, srcMarker, dest, expectedTotal, preferredSource, startFresh, stats,
                        label, phase, progress, ct);
                    Verify(expectedTotal);
                    return dest;
                }
                // One retry against the SAME host first: stepping down the chain can mean a much slower
                // host for 15-32 GB, which is far too high a price for a blip at connect time. Only when
                // this attempt put nothing on disk — a transfer that was progressing is resumed by the
                // user's own Resume, not restarted here.
                catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested
                                           && stats.Written == 0 && IsTransientFailure(ex))
                {
                    Log.Error($"download {label}: {cand.Label} delivered nothing — retrying once from the same source", ex);
                    continue;
                }
                // Deliberately broad: a host rations the same file in several shapes - 200 + an error
                // page (SourceServedPageException), 403/429 (HttpRequestException), and a transfer cut
                // short (surfaces from Verify). All of them mean the SOURCE is refusing, so all of them
                // must reach the next host instead of the user. `ct` is consulted separately: a cancel
                // can reach us wrapped in another exception type, and switching source on the user's own
                // pause would be the worst possible reading of it.
                catch (Exception ex) when (haveNext && !ct.IsCancellationRequested
                                           && ShouldTryOtherSource(ex, progressed: stats.Written > 0))
                {
                    failures.Add($"{cand.Label}: {Short(ex)}");
                    string why = ex is SourceServedPageException { IsQuota: true }
                        ? L.T("core.download.fallback.quota", new { source = cand.Label })
                        : L.T("core.download.fallback.refused", new { source = cand.Label });
                    var next = plan[i + 1];
                    Log.Error($"download {label}: {why} — continuing automatically from \"{next.Label}\"", ex);
                    progress.Report(new(phase, 0, L.T("core.download.fallback.progress", new { label, why, next = next.Label })));
                    break;
                }
                // The end of the chain. Alone, this exception names one host; after a fallback it would
                // blame the last host for a failure that started somewhere else, so the whole chain's
                // reasons travel with it. A cancel is never rewritten — the filter and Backend both key
                // on its type.
                catch (Exception ex) when (!haveNext && failures.Count > 0
                                           && ex is not OperationCanceledException && !ct.IsCancellationRequested)
                {
                    failures.Add($"{cand.Label}: {Short(ex)}");
                    throw AllSourcesFailed(label, failures, ex);
                }
            }
        }

        throw new InvalidOperationException(L.T("core.download.allSourcesFailedShort", new { label }));
    }

    /// <summary>
    /// Is this failure worth re-attempting from the OTHER source? Everything is, except the two kinds
    /// that would only fail again: the user cancelling, and the disk being full.
    /// </summary>
    /// <remarks>
    /// Disk-full is excluded for a concrete reason, not tidiness: switching source discards the
    /// partial (different <c>.src</c>), so a 25 GB download that died because the drive filled up
    /// would be thrown away in order to re-run into the same full drive. The user needs the real
    /// error, not a second lap. HResult is masked to its Win32 facility-free code — 0x27
    /// ERROR_HANDLE_DISK_FULL, 0x70 ERROR_DISK_FULL.
    /// </remarks>
    /// <remarks>Public for the download spike: this predicate is the whole policy behind "Google
    /// throttled us, use the CDN", and its exclusions are the only thing standing between a
    /// recoverable hiccup and a pointless second 30 GB download.</remarks>
    /// <param name="progressed">Did this attempt actually put new bytes on disk?</param>
    public static bool ShouldTryOtherSource(Exception ex, bool progressed)
    {
        if (ex is OperationCanceledException) return false;
        if (ex is IOException io && (io.HResult & 0xFFFF) is 0x27 or 0x70) return false;

        // A transfer that COMPLETED with the wrong length / not a zip: Verify has already deleted the
        // file, so the "protect the partial" rule below has nothing to protect, and this host will serve
        // the same bytes again — the only useful move is the next host. Tested before `progressed`,
        // which is true for every completed transfer: such a host could otherwise never be stepped
        // past (a length divergence between builds, like 1.6.0 / 1.6.1, is exactly this case).
        if (ex is DownloadVerifyException) return true;

        // The source WAS delivering — a socket blip, not a refusal. Switching host here would be the
        // most expensive possible reaction: the partial is pinned to this url by its `.src` marker, so
        // the other source cannot resume it and would restart from zero, throwing away everything
        // downloaded so far. Let the error surface instead; the retry resumes exactly where it
        // stopped.
        // A refusal leaves `progressed` false — a quota page and a 403 write nothing at all, and a
        // truncated transfer is deleted by Verify before this runs — so a refusal still switches host.
        if (progressed) return false;

        return true;
    }

    /// <summary>
    /// One download attempt from one host: pick its fastest edge, transfer, count what actually lands,
    /// and record the source once it does. Split out of <see cref="DownloadSource"/> so every step down
    /// the candidate list gets the same treatment.
    /// </summary>
    /// <param name="knownSize">The length this host is expected to serve (catalogue size, or the one
    /// the Drive probe reported). Rendered as "X / Y" until the server states its own, and written to
    /// the marker so a finished file is still recognised as finished on the next run.</param>
    /// <param name="startFresh">The bytes on disk came from a different host: do not resume them, and
    /// do not delete them either until this host's response proves itself (see
    /// <see cref="Downloader.DownloadResumableAsync"/>).</param>
    /// <param name="stats">Filled in with the bytes this attempt put on disk.</param>
    /// <remarks>
    /// The edge race runs for every host except Drive. Drive's front-ends all enforce the same
    /// per-account throttle (several parallel connections get no more than one does),
    /// so racing them would only spend quota on probes. The CDN is the host that needs it: it is a
    /// multi-CDN name whose edges differ widely in speed, and which one DNS hands out changes from
    /// one lookup to the next (see <see cref="EdgeRace"/>).
    /// </remarks>
    private static async Task AttemptDownloadAsync(
        DownloadCandidate cand, string srcMarker, string dest, long? knownSize, string preferredSource, bool startFresh,
        AttemptStats stats, string label, InstallPhase phase, IProgress<InstallProgress> progress,
        CancellationToken ct)
    {
        string url = cand.Url;
        // Pin the bytes on disk to the host they came from. When this host is already that host the
        // marker can be written up front; when it is not, it must NOT be — until the first byte lands,
        // the file still belongs to the previous host, and a marker claiming otherwise would let the
        // next run Range-append one copy onto another. So the write is deferred to the moment the
        // truncate-and-write actually happens, which is the same moment the claim becomes true.
        bool markerWritten = false;
        if (!startFresh)
        {
            WriteSourceMarker(srcMarker, url, preferredSource, knownSize, label);
            markerWritten = true;
        }

        Log.Info($"download {label}: {url} -> {dest}{(startFresh ? " (new source — downloading from zero)" : "")}");

        // Which edge? Shown to the user only as a short wait; the measurement itself goes to the log.
        EdgeChoice? edge = null;
        if (!cand.IsDrive)
        {
            long have = !startFresh && File.Exists(dest) ? new FileInfo(dest).Length : 0;
            double haveFrac = knownSize is > 0 ? Math.Min(1, (double)have / knownSize.Value) : 0;
            progress.Report(new(phase, haveFrac, L.T("core.download.edge.choosing", new { label, source = cand.Label })));
            try { edge = await EdgeRace.PickAsync(url, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error($"download {label}: the edge race failed (non-fatal — ordinary connection)", ex);
            }
        }
        string via = edge is null ? cand.Label : $"{cand.Label} via {edge.Ip}";

        long start = -1;
        int lastPct = -1;
        var clock = Stopwatch.StartNew();
        // Rate bookkeeping for the UI line and the periodic log line. A smoothed instantaneous rate
        // (EMA over ~1 s samples) is what the user sees; the average since this attempt began is what
        // the log keeps, because without them "slow" and "stuck" look identical in both places — a UI
        // text that changes only on a whole percent (1% of 2.8 is 318 MB: minutes of frozen text at a slow
        // host) and a relic.log that only records that a download STARTED.
        long uiTick = Environment.TickCount64, uiBytes = -1, logTick = uiTick;
        double ema = -1;
        const int UiEveryMs = 1000, LogEveryMs = 60_000;
        // SyncProgress, not Progress<T>: these callbacks must run in order on the download loop's own
        // thread — they decide when the marker may be rewritten and how many bytes this attempt owns.
        var inner = new SyncProgress<DownloadProgress>(p =>
        {
            if (start < 0) start = p.Received;
            long written = p.Received - start;
            if (written > 0)
            {
                stats.Written = written;
                if (!markerWritten)
                {
                    WriteSourceMarker(srcMarker, url, preferredSource, p.Total ?? knownSize, label);
                    markerWritten = true;
                }
            }
            long now = Environment.TickCount64;
            long total = p.Total ?? knownSize ?? 0;
            int pct = (int)((p.Fraction ?? 0) * 100);
            bool due = now - uiTick >= UiEveryMs;
            if (pct == lastPct && !due) return;
            if (uiBytes >= 0 && now > uiTick)
            {
                double inst = (p.Received - uiBytes) * 1000.0 / (now - uiTick) / (1024 * 1024);
                ema = ema < 0 ? inst : 0.5 * ema + 0.5 * inst;
            }
            if (due || uiBytes < 0) { uiTick = now; uiBytes = p.Received; }
            lastPct = pct;

            string line = L.T("core.download.progress", new { label, have = Human(p.Received), total = Human(total) });
            if (ema >= 0)
            {
                line += L.T("core.download.progressRate", new { rate = ema.ToString("0.0") });
                if (total > 0 && ema > 0.05 && p.Received < total)
                    line += L.T("core.download.progressEta", new { eta = HumanTime(TimeSpan.FromSeconds((total - p.Received) / (ema * 1024 * 1024))) });
            }
            progress.Report(new(phase, p.Fraction ?? 0, line));

            if (now - logTick >= LogEveryMs)
            {
                logTick = now;
                double avg = clock.Elapsed.TotalSeconds > 0 ? written / clock.Elapsed.TotalSeconds / (1024 * 1024) : 0;
                Log.Info($"download {label}: {Human(p.Received)}/{Human(total)} — {Math.Max(ema, 0):0.0} MB/s now, " +
                         $"{avg:0.0} MB/s average ({via})");
            }
        });
        try
        {
            // A pinned edge can die mid-transfer (a node withdrawn from rotation). The downloader then
            // surfaces a network failure after its own reconnects, and the host-level policy must NOT
            // switch host (the partial is pinned to this url by its marker) — but nothing stops us from
            // choosing another edge of the SAME host and resuming, which is what a user's Resume would do
            // by hand. The dead edge is excluded from the new race; twice at most per attempt.
            var dead = new List<System.Net.IPAddress>();
            for (int edgeTry = 0; ; edgeTry++)
            {
                try
                {
                    await Downloader.DownloadResumableAsync(url, dest, inner, ct, expectZip: true,
                        startFresh: startFresh && stats.Written == 0, edge: edge);
                    return;
                }
                catch (Exception ex) when (edge is not null && edgeTry < 2 && !ct.IsCancellationRequested
                                           && IsTransientFailure(ex))
                {
                    dead.Add(edge.Ip);
                    Log.Error($"download {label}: edge {edge.Ip} stopped delivering after {Human(stats.Written)} — choosing another edge", ex);
                    long onDisk = File.Exists(dest) ? new FileInfo(dest).Length : 0;
                    progress.Report(new(phase, knownSize is > 0 ? Math.Min(1, (double)onDisk / knownSize.Value) : 0,
                        L.T("core.download.edge.died", new { label, source = cand.Label })));
                    EdgeChoice? next = null;
                    try { next = await EdgeRace.PickAsync(url, ct, exclude: dead); }
                    catch (Exception rex) when (rex is not OperationCanceledException)
                    {
                        Log.Error($"download {label}: the edge race failed (non-fatal — ordinary connection)", rex);
                    }
                    edge = next; // null = ordinary DNS connection for the rest of this attempt
                    via = edge is null ? cand.Label : $"{cand.Label} via {edge.Ip}";
                }
            }
        }
        finally
        {
            // One line per attempt, whatever happened: the numbers an investigation needs later.
            double secs = clock.Elapsed.TotalSeconds;
            double avg = secs > 0 ? stats.Written / secs / (1024 * 1024) : 0;
            Log.Info($"download {label}: {via} — {Human(stats.Written)} in {HumanTime(clock.Elapsed)} " +
                     $"({avg:0.0} MB/s average){(ct.IsCancellationRequested ? " — stopped" : "")}");
        }
    }

    /// <summary>True if the file starts with the ZIP magic bytes "PK".</summary>
    private static bool StartsWithZipMagic(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[2];
            return fs.Read(b) == 2 && b[0] == 0x50 && b[1] == 0x4B;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Error($"could not delete {path}", ex); }
    }

    /// <summary>
    /// Extracts every archive into <paramref name="gameDir"/>, reporting ONE continuous 0..1 fraction
    /// over their combined uncompressed size (the bytes the user actually waits on — a fixed
    /// 0/0.6/1 split would claim the audio zip is 40% of the work when it is ~20% of the bytes).
    /// The archives run one after another because both carry GenshinImpact_Data paths and may contain
    /// the same file — merging them concurrently would have two threads open one destination and fail,
    /// where the sequential order keeps the existing last-writer-wins merge. Continuity of the bar
    /// comes from the shared byte counter, not from a shared worker pool.
    /// </summary>
    /// <remarks>Public so the extract spike can drive it against synthetic archives — this is the one
    /// step that writes tens of GB into a real directory, so its zip-slip guard and progress
    /// accounting need a regression test that does not involve a multi-GB download.</remarks>
    /// <param name="configuredWorkers">0 = auto (<see cref="DefaultExtractWorkers"/>); otherwise the
    /// user's override from settings.</param>
    /// <param name="gentle">Run the workers in Windows' background processing mode — see
    /// <see cref="BackgroundIoScope"/>. Costs nothing on an idle machine and keeps the rest of the
    /// desktop responsive on a busy one.</param>
    public static async Task ExtractAllAsync(
        (string Zip, string Label)[] archives, string gameDir, IProgress<InstallProgress> progress,
        CancellationToken ct, int configuredWorkers = 0, bool gentle = true)
    {
        progress.Report(new(InstallPhase.Extract, 0, L.T("core.install.extract.preparing")));

        string gameDirFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir));
        Directory.CreateDirectory(gameDirFull);

        // After CreateDirectory: the disk probe resolves the path's volume, and a directory that does
        // not exist yet would send GetFullPath down a different root on a relative path.
        int workers = ResolveWorkers(configuredWorkers, gameDirFull);

        // Seconds of synchronous central-directory work — off the calling thread, whichever it is.
        var plans = await Task.Run(() => archives.Select(z => PrepareArchive(z.Zip, gameDirFull, ct)).ToArray(), ct);
        long total = plans.Sum(p => p.Bytes);
        int files = plans.Sum(p => p.Files);
        if (total <= 0)
        {
            progress.Report(new(InstallPhase.Extract, 1, L.T("core.install.extract.done")));
            return;
        }

        var counter = new ExtractCounter();
        var clock = Stopwatch.StartNew();
        int lastPct = -1; // same whole-percent throttle as the downloads; here only this thread reports

        foreach (var a in archives)
        {
            var run = ExtractArchiveAsync(a.Zip, gameDirFull, counter, ct, workers, gentle);
            while (!run.IsCompleted)
            {
                // Un-cancelled delay on purpose: a cancel makes the workers finish within a chunk, and
                // the await below is what surfaces it — a cancelled delay here would just spin.
                await Task.WhenAny(run, Task.Delay(250));

                // Clamped: an entry whose declared Length lies would otherwise push the bar past 100%.
                long done = Math.Min(Interlocked.Read(ref counter.Copied), total);
                int pct = (int)(done * 100 / total);
                if (pct == lastPct) continue;
                lastPct = pct;

                double secs = clock.Elapsed.TotalSeconds;
                long rate = secs > 0.5 ? (long)(done / secs) : 0;
                string tail = rate > 0
                    ? L.T("core.install.extract.rateTail", new { rate = Human(rate), eta = HumanTime(TimeSpan.FromSeconds((total - done) / (double)rate)) })
                    : ""; // the first reports have no meaningful rate — don't print "0.0 B/s"
                progress.Report(new(InstallPhase.Extract, done / (double)total,
                    L.T("core.install.extract.progress", new { label = a.Label, done = Human(done), total = Human(total), tail })));
            }
            await run; // this is what rethrows worker exceptions (unwrapped) and cancellation
        }

        // The one honest throughput measurement Relic has: total bytes over wall clock. Task Manager's
        // disk-% is 100 − %Idle Time and carries no throughput term at all — it pins at 100% for any
        // extractor that keeps one request outstanding — so THIS line, not that bar, is what says
        // whether an extract was slow. Keep it.
        Log.Info($"extract: {files} files, {Human(total)} in {HumanTime(clock.Elapsed)} " +
                 $"({workers} workers{(gentle ? ", low priority" : "")}, {Human((long)(total / Math.Max(clock.Elapsed.TotalSeconds, 0.001)))}/s)");
        progress.Report(new(InstallPhase.Extract, 1, L.T("core.install.extract.done")));
    }

    /// <summary>
    /// One pass over an archive's central directory doing everything that must happen before a single
    /// byte is written: validating every entry against zip-slip (a bogus archive fails in seconds, not
    /// after 15 GB), creating every destination directory serially so the copy workers never race on
    /// CreateDirectory, and summing the uncompressed size that becomes the progress denominator.
    /// </summary>
    private static (long Bytes, int Files) PrepareArchive(string zip, string gameDirFull, CancellationToken ct)
    {
        long bytes = 0;
        int files = 0;
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var e in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (e.FullName.Length == 0) continue;
                string dest = SafeDestPath(gameDirFull, e.FullName);
                if (e.Name.Length == 0)
                {
                    dirs.Add(dest); // explicit directory entry — keep empty folders, like ExtractToDirectory
                    continue;
                }
                string? parent = Path.GetDirectoryName(dest);
                if (parent is not null) dirs.Add(parent);
                bytes += e.Length;
                files++;
            }
        }

        foreach (string d in dirs) Directory.CreateDirectory(d);
        Log.Info($"extract plan: {Path.GetFileName(zip)} — {files} files, {Human(bytes)}, {dirs.Count} directories");
        return (bytes, files);
    }

    /// <summary>
    /// Extracts one archive with <paramref name="workers"/> threads. Every worker opens its OWN
    /// FileStream + ZipArchive over the same path (FileShare.Read): ZipArchive shares one underlying
    /// stream position and is not thread-safe, so concurrent entry.Open() on a single instance
    /// silently reads garbage. Dedicated long-running threads, not pool threads — these block on
    /// synchronous file IO for tens of minutes.
    /// </summary>
    private static Task ExtractArchiveAsync(
        string zip, string gameDirFull, ExtractCounter counter, CancellationToken ct, int workers, bool gentle)
    {
        int next = 0;
        int failed = 0;

        void Worker()
        {
            using var fs = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read,
                ExtractBufferSize, FileOptions.SequentialScan);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Read);
            var entries = archive.Entries;
            byte[] buffer = new byte[ExtractBufferSize];

            // Background mode starts only AFTER the setup allocations above (the central directory is
            // tens of thousands of entries): the mode also drops MEMORY priority, so anything large
            // allocated inside it is first in line to be trimmed. Safe here because Worker is fully
            // synchronous on a dedicated LongRunning thread — see BackgroundIoScope's remarks for why
            // that is the precondition, not a detail.
            using var bg = gentle ? BackgroundIoScope.Begin() : null;

            while (true)
            {
                // One failed entry dooms the install, so the other workers stop instead of extracting
                // the remaining GB first. They return QUIETLY rather than being cancelled: a
                // TaskCanceledException could sort ahead of the real one in WhenAll's exception order
                // and rob Backend of the InvalidDataException it keys the cache cleanup on.
                if (Volatile.Read(ref failed) != 0) return;

                int start = Interlocked.Add(ref next, ExtractChunk) - ExtractChunk;
                if (start >= entries.Count) return;
                int end = Math.Min(start + ExtractChunk, entries.Count);

                for (int i = start; i < end; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var e = entries[i];
                    if (e.FullName.Length == 0 || e.Name.Length == 0) continue; // directory — already created
                    try
                    {
                        ExtractEntry(e, SafeDestPath(gameDirFull, e.FullName), buffer, counter, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Leave background mode BEFORE logging: Log serializes its file-append on a
                        // process-wide lock that the UI thread also takes, and a very-low-priority
                        // thread holding it can be starved for a long time — the documented
                        // priority-inversion hazard, landing squarely on the message pump. Dispose is
                        // idempotent, so the enclosing `using` above is still correct.
                        bg?.Dispose();
                        // The entry name goes in the LOG, never into a new exception type: a corrupt
                        // archive has to reach Backend as InvalidDataException for it to delete the
                        // bad cached zip instead of failing at extract again after hours.
                        Log.Error($"extract: entry \"{e.FullName}\" from {Path.GetFileName(zip)} failed", ex);
                        Interlocked.Exchange(ref failed, 1);
                        throw;
                    }
                }
            }
        }

        var tasks = new Task[workers];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = Task.Factory.StartNew(Worker, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // WhenAll, never WaitAll: awaiting it rethrows the FIRST inner exception unwrapped, which is
        // what keeps a corrupt archive surfacing as InvalidDataException rather than AggregateException.
        return Task.WhenAll(tasks);
    }

    private static void ExtractEntry(
        ZipArchiveEntry e, string dest, byte[] buffer, ExtractCounter counter, CancellationToken ct)
    {
        using var src = e.Open();
        // FileMode.Create is exactly what ExtractToDirectory(overwriteFiles: true) does internally.
        // SequentialScan on the WRITE handle too: the underlying FILE_SEQUENTIAL_ONLY is documented as
        // "all accesses to the file are sequential" — not reads only (its sibling FILE_RANDOM_ACCESS is
        // the one that spells out "read") — and the cache-manager behaviour it buys, releasing views
        // that lie behind the file pointer for re-use, is keyed on the file pointer, not on direction.
        // This handle carries the LARGER half of the traffic (the expanded bytes), so it must not be
        // left on bare defaults.
        using var outFs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
            ExtractBufferSize, FileOptions.SequentialScan);

        // Metadata-only on NTFS, and it stops the multi-hundred-MB .blk files from being extended (and
        // fragmented) one buffer at a time. Not worth the syscall for anything under one buffer.
        // PRECONDITION for "metadata-only" — do not break it: the loop below writes strictly
        // sequentially from offset 0. Extending EOF allocates clusters WITHOUT zeroing them (that is
        // the whole reason SetFileValidData exists and is privileged), and sequential writes advance
        // the valid-data length in step, so NTFS never has a gap to zero-fill. Add a Seek, a Position
        // assignment, or parallel range-writes within one entry and this same line starts forcing real
        // zero-fill I/O over tens of GB.
        bool preallocated = e.Length > ExtractBufferSize;
        if (preallocated) outFs.SetLength(e.Length);

        long written = 0;
        uint crc = 0xFFFFFFFF;
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            outFs.Write(buffer, 0, n);
            written += n;
            crc = Crc32Update(crc, buffer, n);
            Interlocked.Add(ref counter.Copied, n);
        }
        crc ^= 0xFFFFFFFF;

        // Both checks below exist because NOTHING else validates the payload: DeflateStream can decode
        // a corrupted block as end-of-stream and simply stop, .NET's zip reader never verifies CRC-32,
        // and DownloadSource only proves the file is the right LENGTH and starts with "PK". Without
        // them a damaged archive extracts "successfully", InstallAsync registers the version, deletes
        // the 18–40 GB cache, and reports the version ready — leaving a broken game and no way back
        // except a full re-download. InvalidDataException specifically: Backend.TryDeleteCorruptCacheZips
        // keys on that exact type to drop the bad cache so the retry starts clean.
        if (written < e.Length)
        {
            if (preallocated) outFs.SetLength(written); // no zero-padded tail left for the retry
            throw new InvalidDataException(
                L.T("core.install.extract.corruptShort", new { entry = e.FullName, have = Human(written), total = Human(e.Length) }));
        }
        if (crc != e.Crc32)
            throw new InvalidDataException(L.T("core.install.extract.corruptCrc", new { entry = e.FullName }));
    }

    // CRC-32 (IEEE, the polynomial zip uses), table built once. Byte-at-a-time is ~an order of
    // magnitude faster than the disk we are already waiting on, so it costs nothing measurable.
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint Crc32Update(uint crc, byte[] buf, int count)
    {
        for (int i = 0; i < count; i++) crc = Crc32Table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>Resolves an entry to its destination and refuses anything that escapes the game dir.
    /// We build these paths by hand instead of calling ExtractToDirectory, so its zip-slip check has to be
    /// explicit — and a rooted entry name ("C:\...") would otherwise win over Path.Combine outright.</summary>
    private static string SafeDestPath(string gameDirFull, string entryName)
    {
        string dest = Path.GetFullPath(Path.Combine(gameDirFull, entryName));
        if (!dest.StartsWith(gameDirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("core.install.extract.badPath", new { entry = entryName }));
        return dest;
    }

    /// <summary>If an ayy/anime launcher.exe + mhynot2.dll sit next to the game, record them for the
    /// Win11 path. Public because the play-time self-heal re-runs it after patching: these fields,
    /// written only at registration, would never pick up an injector added later.</summary>
    public static void DetectInjector(InstalledVersion inst)
    {
        string launcher = Path.Combine(inst.GameDir, "ayy", "anime", "build", "launcher.exe");
        string dll = Path.Combine(inst.GameDir, "ayy", "anime", "build", "mhynot2.dll");
        if (File.Exists(launcher) && File.Exists(dll))
        {
            inst.LauncherExe = launcher;
            inst.InjectDll = dll;
        }
    }

    /// <summary>Records which voice languages the game folder really holds (marker file per language,
    /// see <see cref="VoiceLanguages.IsOnDisk"/>). Shared by <see cref="RegisterVersion"/>,
    /// <see cref="AddVoicesAsync"/> and <see cref="RelicState.RefreshVoices"/> so "installed" has one
    /// definition everywhere.</summary>
    /// <summary>The marker a partial download is pinned with (url / setting / verified length).</summary>
    private const string SrcSuffix = ".src";

    public static void DetectVoices(InstalledVersion inst) => inst.Voices = VoiceLanguages.Detect(inst.GameDir);

    /// <summary>Re-read the installed voice packs from the marker files and persist them when they
    /// differ from the record. The disk is the truth (a pack the player copied in, an add whose last
    /// step never ran); saving only on a change keeps this cheap enough to call on every path.</summary>
    private void SyncVoices(InstalledVersion inst)
    {
        var before = inst.Voices;
        DetectVoices(inst);
        if (before is null || !before.SequenceEqual(inst.Voices, StringComparer.OrdinalIgnoreCase))
            _state.Save();
    }

    private static string Human(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double n = bytes; int i = 0;
        while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return $"{n:0.0} {u[i]}";
    }

    /// <summary>Coarse on purpose — an extract ETA that ticks by the second reads as noise.</summary>
    private static string HumanTime(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return L.T("core.install.time.sec", new { n = (int)t.TotalSeconds });
        if (t.TotalHours < 1) return L.T("core.install.time.min", new { n = (int)t.TotalMinutes });
        return L.T("core.install.time.hourMin", new { h = (int)t.TotalHours, m = t.Minutes });
    }
}
