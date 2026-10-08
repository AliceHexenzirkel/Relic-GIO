using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.Core.Isolation;

/// <summary>
/// Swaps Genshin's per-account "profile" — its whole registry key AND its whole LocalLow folder —
/// as a single unit, so the private-server client and the official/live client never share login,
/// identity, or session state. Both clients build as miHoYo / Genshin Impact and therefore write
/// to the SAME registry key and LocalLow folder; the only robust isolation is a full swap performed
/// while GenshinImpact.exe is NOT running.
///
/// Paths are injectable so the exact same mechanism can be exercised against a synthetic key in tests
/// without touching a real Genshin install. Everything is under HKCU / the user profile, so NO
/// elevation is required.
/// </summary>
public sealed class ProfileStore
{
    /// <summary>The slot that preserves the official client's account data untouched.</summary>
    public const string LiveId = "live";

    private readonly string _regKey;    // e.g. HKCU\Software\miHoYo\Genshin Impact
    private readonly string _localLow;  // e.g. %USERPROFILE%\AppData\LocalLow\miHoYo\Genshin Impact
    private readonly string _storeRoot; // e.g. %LOCALAPPDATA%\Relic\profiles

    public ProfileStore(string regKey, string localLow, string storeRoot)
    {
        _regKey = regKey;
        _localLow = localLow;
        _storeRoot = storeRoot;
    }

    /// <summary>
    /// Called with the slot id right after EVERY <see cref="LoadProfile"/> completed (phase-2 marker
    /// written), on whichever path loaded it — a session start, a session end, a recovery pass, a torn
    /// repair, a version removal, the uninstaller. A failure inside the hook is logged, never
    /// propagated: the profile itself is loaded and consistent at that point.
    /// </summary>
    public Action<string>? Loaded { get; init; }

    /// <summary>
    /// The hook the real store runs: arm the RunOnce logon recovery while a private profile is loaded,
    /// remove it once live is back (see <see cref="Startup.SetLogonRecovery"/>). One place for every
    /// LoadProfile(live) — RecoverCore, RepairIfTorn, RemoveAsync, the uninstaller — rather than a call
    /// after each swap, so no restore path can forget to disarm it. Public so the isolation spike can
    /// wire the very same delegate to a synthetic store + key.
    /// </summary>
    public static readonly Action<string> LogonRecoveryHook = id => Startup.SetLogonRecovery(armed: !IsLive(id));

    /// <summary>Bind to the real Genshin registry key + LocalLow folder for the current user.</summary>
    public static ProfileStore ForGenshin()
    {
        string localLow = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "miHoYo", "Genshin Impact");
        string store = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Relic", "profiles");
        return new ProfileStore(@"HKCU\Software\miHoYo\Genshin Impact", localLow, store) { Loaded = LogonRecoveryHook };
    }

    private static bool IsLive(string id) => string.Equals(id, LiveId, StringComparison.OrdinalIgnoreCase);

    private string SlotDir(string id) => Path.Combine(_storeRoot, id);
    private string RegFile(string id) => Path.Combine(SlotDir(id), "genshin.reg");
    private string LocalLowStore(string id) => Path.Combine(SlotDir(id), "LocalLow");
    private string TaintFile(string id) => Path.Combine(SlotDir(id), ".tainted");
    private string ActiveMarkerFile => Path.Combine(_storeRoot, "active");

    // Two-phase marker: "loading:<id>" while LoadProfile is mutating, plain "<id>" only once the
    // load fully completed. A crash mid-load therefore never leaves a marker claiming the disk
    // holds a clean profile — in EITHER direction. (A plain "live" over half-restored private data
    // would make the next save export private-server data into the live slot.)
    private const string LoadingPrefix = "loading:";

    private string ReadMarker()
    {
        try
        {
            // FileShare.Delete so this read can never make a concurrent WriteMarker replace
            // (File.Move needs DELETE access on the target) fail with a sharing violation.
            using var fs = new FileStream(ActiveMarkerFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Trim();
        }
        catch (FileNotFoundException) { return ""; }
        catch (DirectoryNotFoundException) { return ""; }
    }

    private void WriteMarker(string value)
    {
        // Atomic (write-temp + rename) so a torn write can't leave a plausible wrong id.
        Directory.CreateDirectory(_storeRoot);
        string tmp = ActiveMarkerFile + ".tmp";
        File.WriteAllText(tmp, value);
        // Brief retry: a reader that opened the marker without FileShare.Delete (external tools)
        // blocks the replace only for microseconds.
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(tmp, ActiveMarkerFile, overwrite: true); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(50 * attempt); }
        }
    }

    /// <summary>
    /// Id of the profile the disk currently holds (or was being loaded when a crash tore the swap —
    /// check <see cref="IsTornLoad"/> / run <see cref="RepairIfTorn"/> before saving anything).
    /// No marker (first run, pre-marker installs) means "live".
    /// </summary>
    public string ActiveId()
    {
        string m = ReadMarker();
        if (m.StartsWith(LoadingPrefix, StringComparison.OrdinalIgnoreCase))
            m = m[LoadingPrefix.Length..].Trim();
        return m.Length > 0 ? m : LiveId;
    }

    /// <summary>True if a previous LoadProfile was interrupted — the on-disk key + LocalLow are an
    /// unusable mix of two profiles and must not be saved into any slot.</summary>
    public bool IsTornLoad() => ReadMarker().StartsWith(LoadingPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the disk verifiably holds the live profile (clean marker naming live).</summary>
    public bool LiveIsActive() => !IsTornLoad() && string.Equals(ActiveId(), LiveId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// If a previous LoadProfile was torn by a crash/kill, discard the on-disk mix (both halves were
    /// already saved to their slots before the load began — nothing of value is lost) and re-load the
    /// live slot. Returns true if a repair ran. Callers must hold the play-session gate and ensure no
    /// game client is running.
    /// </summary>
    public bool RepairIfTorn()
    {
        if (!IsTornLoad()) return false;
        LoadProfile(LiveId);
        return true;
    }

    // ── taint: a foreign client ran on a loaded private profile ──
    //
    // The official client started while a version's profile was still loaded (a deferred restore —
    // it appeared seconds after ours closed — or Relic was not alive to restore) runs on THAT key +
    // LocalLow and writes its own login token and settings into them. The disk is then a mix: not
    // this slot's data any more, and not live's. Exporting it into the slot (what a plain SwapTo does)
    // would carry the official token into the private slot, from where the next private session
    // would present it to the private server. So the slot is marked TAINTED, and every restore path
    // puts live back WITHOUT exporting; the slot keeps the last snapshot taken before the taint.
    // Recorded as a sidecar file next to the slot's snapshot (profiles\<id>\.tainted), not as a
    // third marker phase: the marker describes the DISK (which profile, torn or not) and keeps its
    // single meaning; the taint is a property of the SLOT and travels with it (deleted with it).

    /// <summary>Mark slot <paramref name="id"/> as tainted (see the note above). The live slot can
    /// never be tainted — the official client is exactly the one entitled to write on it.</summary>
    public void MarkTainted(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || IsLive(id))
            throw new InvalidOperationException("the live slot cannot be tainted");
        Directory.CreateDirectory(SlotDir(id));
        File.WriteAllText(TaintFile(id), DateTime.UtcNow.ToString("o"));
    }

    /// <summary>True when slot <paramref name="id"/> — by default the ACTIVE one — carries the taint
    /// mark. Live is never tainted, so a clean live disk always answers false.</summary>
    public bool IsTainted(string? id = null)
    {
        id ??= ActiveId();
        return !IsLive(id) && File.Exists(TaintFile(id));
    }

    /// <summary>Drop the taint mark of slot <paramref name="id"/> (no-op when absent).</summary>
    public void ClearTaint(string id)
    {
        try { File.Delete(TaintFile(id)); }
        catch (DirectoryNotFoundException) { /* slot never saved — nothing to clear */ }
    }

    /// <summary>
    /// Put live back WITHOUT exporting the active slot first — the restore for a tainted profile. The
    /// slot keeps its pre-taint snapshot. The taint is cleared only once live is fully loaded: cleared
    /// earlier, a load that fails at its very first step would leave the mix on disk with nothing
    /// saying so, and the next recovery pass would export it. Returns the id that was discarded (live
    /// when nothing else was loaded — then this is a plain reload of live). Callers hold the session
    /// gate and have the sustained all-clear, exactly as for SwapTo.
    /// </summary>
    public string RestoreLiveDiscarding()
    {
        string active = ActiveId();
        LoadProfile(LiveId);
        ClearTaint(active);
        return active;
    }

    /// <summary>True if profile slot <paramref name="id"/> has been saved at least once.</summary>
    public bool HasProfile(string id) => File.Exists(RegFile(id)) || Directory.Exists(LocalLowStore(id));

    /// <summary>Persist the CURRENTLY active registry key + LocalLow into profile slot <paramref name="id"/>.</summary>
    /// <remarks>
    /// Uses <c>reg export</c> (a plain read of a key we own) rather than <c>reg save</c>, because
    /// RegSaveKey requires SeBackupPrivilege (elevation) even for your own HKCU key. Export/import
    /// needs no elevation, which lets the whole launcher run as a normal (asInvoker) process.
    /// </remarks>
    public void SaveProfile(string id)
    {
        Directory.CreateDirectory(SlotDir(id));

        string reg = RegFile(id);
        if (RegKeyExists(_regKey))
        {
            // Export to a temp file and swap it in only on success, so a failed export can never
            // destroy the slot's previous (still valid) snapshot.
            string tmp = reg + ".tmp";
            Reg("export", _regKey, tmp, "/y");
            File.Move(tmp, reg, overwrite: true);
        }
        else if (File.Exists(reg))
        {
            File.Delete(reg); // key absent — leave no .reg so LoadProfile treats the slot as fresh/empty.
        }

        if (Directory.Exists(_localLow))
            Robocopy(_localLow, LocalLowStore(id));
    }

    /// <summary>Make profile slot <paramref name="id"/> the active registry key + LocalLow.</summary>
    public void LoadProfile(string id)
    {
        // Phase 1 BEFORE any mutation: a crash from here on leaves "loading:<id>", telling the next
        // run the disk is a discardable mix — never a clean profile it might save into a slot.
        WriteMarker(LoadingPrefix + id);

        string reg = RegFile(id);
        // Purge the current key first so no stale values from the previous profile survive, then
        // import the target's values (import alone MERGES, so the delete gives clean-replace semantics).
        if (RegKeyExists(_regKey))
            Reg("delete", _regKey, "/f");
        if (File.Exists(reg))
            Reg("import", reg);
        // else: fresh profile — leave the key absent (the game recreates it clean on next launch).

        string llStore = LocalLowStore(id);
        Directory.CreateDirectory(_localLow);
        if (Directory.Exists(llStore))
            Robocopy(llStore, _localLow);
        else
            ClearDirectory(_localLow);     // fresh profile: empty LocalLow

        // Phase 2: only now does the disk hold a clean <id> profile.
        WriteMarker(id);
        // A slot freshly loaded FROM its snapshot is by definition not a mix any more: a taint left
        // over from an earlier session on this slot (the restore that cleared it tore, or never ran)
        // must not make the next session's end discard that session's own state.
        ClearTaint(id);
        NotifyLoaded(id);
    }

    private void NotifyLoaded(string id)
    {
        try { Loaded?.Invoke(id); }
        catch (Exception ex) { Log.Error($"profile loaded hook for '{id}' failed (non-fatal)", ex); }
    }

    /// <summary>
    /// Save the active profile out as <paramref name="activeId"/>, then load <paramref name="targetId"/>
    /// in. A TAINTED active slot is never exported, whichever caller reaches here (the callers that
    /// know about the taint restore through <see cref="RestoreLiveDiscarding"/> and log it; this is
    /// for the ones that do not — a "--play" session of another version started while a tainted slot
    /// is still loaded): the target is loaded over the mix and the taint is dropped. Same id + taint
    /// is not the usual no-op either — the slot is reloaded from its snapshot, so the session never
    /// starts on the mix.
    /// </summary>
    public void SwapTo(string targetId, string activeId)
    {
        bool tainted = IsTainted(activeId);
        if (string.Equals(targetId, activeId, StringComparison.OrdinalIgnoreCase) && !tainted)
            return;
        if (!tainted) SaveProfile(activeId);
        LoadProfile(targetId);
        if (tainted) ClearTaint(activeId);
    }

    /// <summary>
    /// Delete a stored profile slot (registry export + LocalLow copy) when its version is removed.
    /// Refuses the live slot (the official client's only copy), the ACTIVE slot (a loaded profile
    /// must be swapped out first — the caller restores live before calling this) and a torn load.
    /// A slot that does not exist is not an error.
    /// </summary>
    public void DeleteProfile(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains("..") || id.Contains('\\') || id.Contains('/'))
            throw new ArgumentException("invalid profile id", nameof(id));
        if (string.Equals(id, LiveId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("the live slot is never deleted");
        if (IsTornLoad())
            throw new InvalidOperationException("profile store is torn — repair it first");
        if (string.Equals(ActiveId(), id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("profile is active — restore live first");
        string dir = SlotDir(id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    // ---- reg.exe / robocopy helpers ----

    private static void Reg(params string[] args)
    {
        var r = Proc.Run("reg", args);
        if (!r.Ok)
            throw new InvalidOperationException(
                $"reg {string.Join(' ', args)} failed ({r.Code}): {r.Err.Trim()} {r.Out.Trim()}");
    }

    private static bool RegKeyExists(string key) => Proc.Run("reg", "query", key).Ok;

    private static void Robocopy(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        string[] args =
        {
            src, dst, "/MIR",
            "/R:1", "/W:1", "/NP", "/NFL", "/NDL", "/NJH", "/NJS",
            "/XD", "Crashes", "logs", "webCaches",   // volatile dirs: don't copy, don't mirror-delete
            "/XF", "*.log",
        };
        var r = Proc.Run("robocopy", args);
        // robocopy exit codes 0-7 are success (bit flags); >= 8 is a real failure.
        if (r.Code >= 8)
            throw new InvalidOperationException($"robocopy {src} -> {dst} failed ({r.Code}): {r.Out.Trim()} {r.Err.Trim()}");
    }

    private static void ClearDirectory(string dir)
    {
        if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); return; }
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        foreach (var d in Directory.GetDirectories(dir)) Directory.Delete(d, recursive: true);
    }
}
