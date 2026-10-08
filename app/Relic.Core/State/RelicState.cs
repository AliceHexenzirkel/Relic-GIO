using System.Text.Json;
using Relic.Core.Util;

namespace Relic.Core.State;

public sealed class RelicSettings
{
    public string ServerHost { get; set; } = BuildDefaults.ServerHost;
    public int ServerPort { get; set; } = 21000;
    /// <summary>UI/Core language code; the strings live in <c>ui/lang/&lt;code&gt;.json</c>.</summary>
    public string Language { get; set; } = "en";
    /// <summary>PREFERRED download host, not the only one: "cdn", "drive", or a mirror id from
    /// versions.json ("archive"). The rest of the hosts stay behind it as automatic fallbacks — see
    /// <c>InstallService.BuildCandidates</c>. An id the chosen version does not carry is harmless.</summary>
    public string Source { get; set; } = "cdn";
    public bool FiddlerDecrypt { get; set; } = true;
    public bool InstallCert { get; set; } = true;
    public bool CreateShortcut { get; set; } = true;
    /// <summary>Install Fiddler without the UAC prompt (RunAsInvoker) before falling back to the
    /// consented elevated path. FiddlerSetup.exe's manifest asks for "highestAvailable", not
    /// "requireAdministrator" — it installs fine per-user, so the prompt only ever appears because
    /// the user is in the Administrators group.</summary>
    public bool FiddlerNoUac { get; set; } = true;
    /// <summary>Run the extract workers at Windows' background priority (CPU + I/O + memory) so a
    /// 20–80 GB install stops starving the rest of the desktop. Default on: it costs nothing on an
    /// idle machine — the disk is the bottleneck either way — and only yields when something else
    /// actually wants the disk. Turn it off if an extract is competing with nothing and still crawls.</summary>
    public bool GentleExtract { get; set; } = true;
    /// <summary>Extraction worker threads; 0 = auto (<c>clamp(cores/2, 2, 6)</c>). An escape hatch,
    /// not a tuning knob: 1 re-creates the ~20 MB/s single-threaded regime the parallelism exists to
    /// escape, so lower it only for a genuinely slow external disk.</summary>
    public int ExtractWorkers { get; set; }
    public string InstallRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Relic Games");

    /// <summary>"player" | "admin" | "" (not chosen yet — the start screen asks). A UX gate only: the
    /// agent bearer token is the security boundary (Backend.RequireAdmin checks both).</summary>
    public string Mode { get; set; } = BuildServers.DefaultMode;
    /// <summary>Agent port of the configured server (default 18080; may come from the TXT record).</summary>
    public int AgentPort { get; set; } = 18080;
    /// <summary>Where ServerPort/AgentPort came from: "explicit" | "txt" | "default".</summary>
    public string ServerPortSource { get; set; } = "default";
    public string? ServerResolvedAt { get; set; }
    /// <summary>Start-screen music muted (remembered across starts).</summary>
    public bool LoginMuted { get; set; }
    /// <summary>Start-screen background animation switched off — the video stays on a still frame
    /// (remembered across starts, like the music).</summary>
    public bool LoginAnimOff { get; set; }
    /// <summary>In-game enhancements (the F1 menu). ON (default): when the build ships an
    /// <c>inject</c> payload entry for the version, GamePatcher puts that DLL in place and the game
    /// starts through launcher.exe with mhynot2.dll + the DLL on BOTH Windows 10 and 11 (the real
    /// mhyprot2 driver must never load under the mod); a DLL the antivirus removed only costs the menu,
    /// never the launch. OFF: exactly the per-OS launch — Win11 through
    /// the injector with mhynot2.dll alone, Win10 direct — and the inject entries are neither copied nor
    /// verified (nor ever deleted). A state.json written by an older build reads true.</summary>
    public bool Enhancements { get; set; } = true;
}

public sealed class InstalledVersion
{
    public string Id { get; set; } = "";
    public string GameDir { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public string? ShortcutName { get; set; }
    public string? LauncherExe { get; set; }  // Win11 injector, if present
    public string? InjectDll { get; set; }
    /// <summary>"launcher" (downloaded/installed by Relic into InstallRoot) | "import" (a folder the
    /// user already had). Decides the default of "also delete the game files" on removal. Empty in
    /// state files written by older builds — <see cref="RelicState.FillOrigins"/> infers it.</summary>
    public string Origin { get; set; } = "";
    /// <summary>Installed voice languages, in <see cref="Install.VoiceLanguages.All"/> spelling and
    /// order — a cached view of the disk (the <c>Audio_&lt;Lang&gt;_pkg_version</c> markers), refreshed at
    /// boot (<see cref="RelicState.RefreshVoices"/>), at registration and after every add. Missing in
    /// state files written by older builds.</summary>
    public List<string> Voices { get; set; } = new();
}

/// <summary>An in-game account the player created through Relic on a given server.</summary>
public sealed class RememberedAccount
{
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    /// <summary>The server's provisioning generation at creation time — a re-provision wipes the
    /// SDK accounts, so a different generation means this account no longer exists.</summary>
    public string? Generation { get; set; }
}

/// <summary>Persisted launcher state (settings, installed versions, selection). JSON at %LOCALAPPDATA%\Relic\state.json.</summary>
public sealed class RelicState
{
    public RelicSettings Settings { get; set; } = new();
    public List<InstalledVersion> Installed { get; set; } = new();
    public string? SelectedVersionId { get; set; }
    /// <summary>The in-game login this PC shows per server ("host:port") and version id — the Library, the
    /// shortcut splash: one of <see cref="AccountLists"/>, the last created unless the player picked another.</summary>
    public Dictionary<string, Dictionary<string, RememberedAccount>> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every account this launcher created per server ("host:port") and version id, oldest first — a
    /// player may create several (up to the server's <c>maxPerPlayer</c>). Missing for a version in
    /// state files written by older builds, which remembered only <see cref="Accounts"/>:
    /// <see cref="AccountListFor"/> reads that one as a list of one.</summary>
    public Dictionary<string, Dictionary<string, List<RememberedAccount>>> AccountLists { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A random secret of this installation, made on first use: <see cref="ClientIdFor"/> derives from it
    /// the id a signup sends, by which the server counts this launcher's accounts.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>File a created account: added to the version's list (replacing a same-named entry) and made the
    /// login shown for it.</summary>
    public void RememberAccount(string host, int port, string version, string name, string? generation)
    {
        string key = $"{host}:{port}";
        var list = ListSlot(key, version);
        var acc = new RememberedAccount { Name = name, CreatedAt = DateTime.UtcNow.ToString("o"), Generation = generation };
        list.RemoveAll(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        list.Add(acc);
        if (!Accounts.TryGetValue(key, out var per))
            Accounts[key] = per = new Dictionary<string, RememberedAccount>(StringComparer.OrdinalIgnoreCase);
        per[version] = acc;
    }

    /// <summary>The list of a server + version, created on first use — seeded with the login an older build
    /// remembered, so the first account created after an upgrade never makes the previous one disappear.</summary>
    private List<RememberedAccount> ListSlot(string key, string version)
    {
        if (!AccountLists.TryGetValue(key, out var per))
            AccountLists[key] = per = new Dictionary<string, List<RememberedAccount>>(StringComparer.OrdinalIgnoreCase);
        if (!per.TryGetValue(version, out var list) || list is null)
        {
            per[version] = list = new List<RememberedAccount>();
            if (Accounts.TryGetValue(key, out var cur) && cur.TryGetValue(version, out var old) && old is { Name.Length: > 0 })
                list.Add(old);
        }
        return list;
    }

    public RememberedAccount? AccountFor(string host, int port, string version) =>
        Accounts.TryGetValue($"{host}:{port}", out var per) && per.TryGetValue(version, out var a) ? a : null;

    /// <summary>Every account this launcher created on a server + version, oldest first (a copy).</summary>
    public List<RememberedAccount> AccountListFor(string host, int port, string version)
    {
        string key = $"{host}:{port}";
        if (AccountLists.TryGetValue(key, out var per) && per.TryGetValue(version, out var list) && list is not null)
            return list.Where(a => a is { Name.Length: > 0 }).ToList();
        return AccountFor(host, port, version) is { Name.Length: > 0 } cur ? new List<RememberedAccount> { cur } : new List<RememberedAccount>();
    }

    /// <summary>Show another of this launcher's accounts as the version's login. False when the name is not in
    /// its list (nothing changes).</summary>
    public bool UseAccount(string host, int port, string version, string name)
    {
        var hit = AccountListFor(host, port, version).FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (hit is null) return false;
        string key = $"{host}:{port}";
        ListSlot(key, version); // an older build's single login becomes a list before the pick moves off it
        if (!Accounts.TryGetValue(key, out var per))
            Accounts[key] = per = new Dictionary<string, RememberedAccount>(StringComparer.OrdinalIgnoreCase);
        per[version] = hit;
        return true;
    }

    /// <summary>The id a signup on <paramref name="host"/> sends (32 hex characters): HMAC-SHA256 of the host
    /// under <see cref="ClientSecret"/>, so every server sees a different, stable id for this launcher and none
    /// can match it with another's. Creates the secret on first use — the caller saves the state then.</summary>
    public string ClientIdFor(string host)
    {
        if (string.IsNullOrEmpty(ClientSecret))
            ClientSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var mac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(ClientSecret));
        byte[] h = mac.ComputeHash(System.Text.Encoding.UTF8.GetBytes((host ?? "").Trim().ToLowerInvariant()));
        return Convert.ToHexString(h, 0, 16).ToLowerInvariant();
    }

    /// <summary>The last answer each server gave about its pre-made in-game account, per server
    /// ("host:port") and version id: the name, or "" when the server said there is NONE (a stack
    /// prepared with "keep my progress" / "fixes only"). Kept so the shortcut splash and the
    /// first render before a status poll show what the SERVER said last time, never the catalogue's
    /// name for an account that does not exist on it.</summary>
    public Dictionary<string, Dictionary<string, string>> ServerAccounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record a server's answer; returns true when it differs from what was stored (the caller
    /// saves only then — this runs on every status poll).</summary>
    public bool RememberServerAccount(string host, int port, string version, string name)
    {
        string key = $"{host}:{port}";
        name ??= "";
        if (!ServerAccounts.TryGetValue(key, out var per))
            ServerAccounts[key] = per = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (per.TryGetValue(version, out var old) && string.Equals(old, name, StringComparison.Ordinal)) return false;
        per[version] = name;
        return true;
    }

    /// <summary>The remembered answer, or null when this server never answered for the version ("" = it
    /// answered "no pre-made account").</summary>
    public string? ServerAccountFor(string host, int port, string version) =>
        ServerAccounts.TryGetValue($"{host}:{port}", out var per) && per.TryGetValue(version, out var n) ? n : null;

    /// <summary>Infer <see cref="InstalledVersion.Origin"/> for entries written by older builds:
    /// a launcher install always sits at <c>&lt;InstallRoot&gt;\Genshin &lt;id&gt;</c> (or at least in a
    /// folder of that name — InstallRoot may have moved); anything else was imported. Returns true
    /// when something was filled in (caller saves).</summary>
    public bool FillOrigins()
    {
        bool changed = false;
        foreach (var i in Installed)
        {
            if (!string.IsNullOrEmpty(i.Origin)) continue;
            bool launcher = false;
            try
            {
                string dir = Path.GetFullPath(i.GameDir).TrimEnd('\\', '/');
                string expected = Path.GetFullPath(Path.Combine(Settings.InstallRoot, "Genshin " + i.Id)).TrimEnd('\\', '/');
                launcher = string.Equals(dir, expected, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Path.GetFileName(dir), "Genshin " + i.Id, StringComparison.OrdinalIgnoreCase);
            }
            catch { /* odd path: treat as import (never deletes by default) */ }
            i.Origin = launcher ? "launcher" : "import";
            changed = true;
        }
        return changed;
    }

    /// <summary>Re-read <see cref="InstalledVersion.Voices"/> from the disk markers for every install
    /// whose drive is mounted — the same guard as <see cref="PruneMissing"/>: an unplugged external disk
    /// must not blank the list of a perfectly fine install. Returns true when something changed (the
    /// caller saves). Four File.Exists per version — negligible at boot.</summary>
    public bool RefreshVoices()
    {
        bool changed = false;
        foreach (var i in Installed)
        {
            if (string.IsNullOrWhiteSpace(i.GameDir)) continue;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(i.GameDir)) ?? "";
                if (root.Length == 0 || !Directory.Exists(root)) continue;   // drive not mounted
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                continue; // PruneMissing already reported the odd path
            }
            var now = Install.VoiceLanguages.Detect(i.GameDir);
            i.Voices ??= new();
            if (now.SequenceEqual(i.Voices, StringComparer.Ordinal)) continue;
            i.Voices = now;
            changed = true;
        }
        return changed;
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relic", "state.json");

    public static RelicState Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new RelicState();
        try
        {
            return JsonSerializer.Deserialize<RelicState>(File.ReadAllText(path), Opts) ?? new RelicState();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Never silently treat a corrupt/truncated state as a fresh install (that "unregisters"
            // 30+ GB of games): preserve the evidence, log, and try the .bak left by Save.
            Log.Error($"state.json corrupt/unreadable ({path}) — keeping it as .corrupt and trying .bak", ex);
            try { File.Copy(path, path + ".corrupt", overwrite: true); }
            catch (Exception ex2) { Log.Error("could not keep the .corrupt copy", ex2); }
            string bak = path + ".bak";
            if (File.Exists(bak))
            {
                try
                {
                    var fromBak = JsonSerializer.Deserialize<RelicState>(File.ReadAllText(bak), Opts);
                    if (fromBak is not null) { Log.Info("state restored from .bak"); return fromBak; }
                }
                catch (Exception ex2) when (ex2 is IOException or JsonException)
                {
                    Log.Error("state.json.bak is just as corrupt", ex2);
                }
            }
            return new RelicState();
        }
    }

    /// <summary>Atomic: write to .tmp then swap in, keeping the previous file as .bak — a crash or
    /// power loss mid-write cannot leave a truncated state.json behind.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, path + ".bak"); }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
            {
                Log.Error("File.Replace failed — using Move (no .bak)", ex);
                File.Move(tmp, path, overwrite: true);
            }
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    public InstalledVersion? FindInstalled(string id) =>
        Installed.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Drop registrations whose game files are no longer on disk, returning the ids dropped (empty
    /// when nothing changed — the caller only saves then). This is the second half of the guard against
    /// "a fresh install thinks a version is already downloaded": <see cref="Uninstaller"/> removes
    /// state.json on uninstall, and this catches every other way the two can drift apart — a folder
    /// deleted by hand, a cleanup tool, a state.json restored from a backup.
    ///
    /// A version whose DRIVE is not currently mounted is deliberately kept: an unplugged external
    /// disk must not silently unregister a 30 GB install that is perfectly fine.
    /// </summary>
    public List<string> PruneMissing()
    {
        var gone = new List<string>();
        Installed.RemoveAll(i =>
        {
            if (string.IsNullOrWhiteSpace(i.GameDir)) { gone.Add(i.Id); return true; }
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(i.GameDir)) ?? "";
                if (root.Length == 0 || !Directory.Exists(root)) return false;   // drive not mounted
                if (File.Exists(Path.Combine(i.GameDir, "GenshinImpact.exe"))) return false;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                Log.Error($"PruneMissing: invalid path for {i.Id} ('{i.GameDir}')", ex);
                gone.Add(i.Id);
                return true;
            }
            gone.Add(i.Id);
            return true;
        });

        if (gone.Any(id => string.Equals(id, SelectedVersionId, StringComparison.OrdinalIgnoreCase)))
            SelectedVersionId = Installed.FirstOrDefault()?.Id;

        return gone;
    }
}
