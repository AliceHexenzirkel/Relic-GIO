using System.Globalization;
using System.Text.Json;
using Relic.Core.Server;
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

    /// <summary>Per game version id: what a launch does when the version's game folder holds a file of the
    /// player's own in the place of one Relic ships as a replacement (<c>GamePatcher.OwnFiles</c>) —
    /// <see cref="OwnFileChoices.Keep"/> (the player's file stays) or <see cref="OwnFileChoices.Replace"/>
    /// (Relic's goes in, the player's is kept beside it as <c>.relic-own</c>); a version without an entry is
    /// <see cref="OwnFileChoices.Ask"/>: PLAY asks. Normalised whenever it is set, the read of state.json
    /// included: version ids without letter case, every other value dropped — so it reads as "ask". Read it
    /// through <see cref="OwnFilesChoiceFor"/>.</summary>
    public Dictionary<string, string> OwnFiles
    {
        get => _ownFiles;
        set => _ownFiles = OwnFileChoices.Normalize(value);
    }
    private Dictionary<string, string> _ownFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The stored choice of <paramref name="versionId"/>: keep, replace, or ask (none stored).</summary>
    public string OwnFilesChoiceFor(string versionId) =>
        OwnFileChoices.Of(_ownFiles.TryGetValue((versionId ?? "").Trim(), out var c) ? c : null);

    /// <summary>Store <paramref name="choice"/> for <paramref name="versionId"/> — "ask" (or anything that is
    /// neither keep nor replace) removes the entry. True when the map changed (the caller saves only then).
    /// The map is replaced, never changed in place: a play session reads it on another thread. Under
    /// <see cref="RelicState.Gate"/>, so two answers given at the same moment both land.</summary>
    public bool SetOwnFilesChoice(string versionId, string choice)
    {
        string id = (versionId ?? "").Trim();
        if (id.Length == 0) return false;
        string c = OwnFileChoices.Of(choice);
        lock (RelicState.Gate)
        {
            var current = _ownFiles;
            if (c == OwnFileChoices.Ask ? !current.ContainsKey(id) : current.TryGetValue(id, out var old) && old == c)
                return false;
            var next = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
            if (c == OwnFileChoices.Ask) next.Remove(id);
            else next[id] = c;
            _ownFiles = next;
            return true;
        }
    }
}

/// <summary>The words of <see cref="RelicSettings.OwnFiles"/> and of the answer a launch is given about the
/// player's own game files.</summary>
public static class OwnFileChoices
{
    /// <summary>Nothing decided: PLAY asks, and a launch that got no answer keeps the player's file.</summary>
    public const string Ask = "ask";
    /// <summary>The player's own file stays; the game starts with it.</summary>
    public const string Keep = "keep";
    /// <summary>Relic's file takes its place; the player's is kept beside it.</summary>
    public const string Replace = "replace";

    /// <summary><see cref="Keep"/> or <see cref="Replace"/> (trimmed, any letter case); anything else is
    /// <see cref="Ask"/>.</summary>
    public static string Of(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        Keep => Keep,
        Replace => Replace,
        _ => Ask,
    };

    /// <summary>An answer handed to one launch: <see cref="Keep"/> or <see cref="Replace"/>, else null —
    /// the version's stored choice applies.</summary>
    public static string? Answer(string? value) => Of(value) is var c && c != Ask ? c : null;

    /// <summary>The stored map as it is kept: version ids trimmed, without letter case, and only the entries
    /// that say keep or replace.</summary>
    public static Dictionary<string, string> Normalize(Dictionary<string, string>? map)
    {
        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (map is null) return clean;
        foreach (var (key, value) in map)
        {
            string id = (key ?? "").Trim();
            string c = Of(value);
            if (id.Length > 0 && c != Ask) clean[id] = c;
        }
        return clean;
    }
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

/// <summary>Why <see cref="SavedServer.Create"/> or <see cref="RelicState.SaveServer"/> refused an entry.</summary>
public enum SavedServerProblem { None, BadAddress, BadMode, BadLabel, Full }

/// <summary>A server of the start screen's saved list (<see cref="RelicState.SavedServers"/>): an address, the mode it is
/// entered in and a name — never a secret. The token of a host is the one its record in server.json holds
/// (<c>ServerConfigStore</c>), which belongs to the host — hence ONE entry per host (<see cref="ServerConfig.SameHost"/>).</summary>
public sealed class SavedServer
{
    public const string ModePlayer = "player", ModeAdmin = "admin";
    /// <summary>The longest name, in characters after trimming.</summary>
    public const int MaxLabel = 40;
    /// <summary>The most entries the list holds.</summary>
    public const int MaxEntries = 50;

    /// <summary>8 random hex characters, kept when the host's entry is saved again.</summary>
    public string Id { get; set; } = "";
    /// <summary>The address as typed, trimmed, without a scheme or a path: <c>host</c>, <c>host:port</c> or
    /// <c>[v6]:port</c>.</summary>
    public string Address { get; set; } = "";
    /// <summary>The host of <see cref="Address"/> (<see cref="ServerAddress.TryParse"/>, no DNS).</summary>
    public string Host { get; set; } = "";
    /// <summary><see cref="ModePlayer"/> or <see cref="ModeAdmin"/>.</summary>
    public string Mode { get; set; } = "";
    /// <summary>At most <see cref="MaxLabel"/> characters without a control character; "" = the page shows the address.</summary>
    public string Label { get; set; } = "";
    /// <summary>0 (inferred, the start screen's rule) or 1–65535.</summary>
    public int AgentPort { get; set; }
    /// <summary>When the entry was saved (ISO 8601, UTC).</summary>
    public string SavedAt { get; set; } = "";

    public SavedServer Copy() => (SavedServer)MemberwiseClone();

    /// <summary>An entry for the values a page sent — its <see cref="Id"/> still empty, <see cref="SavedAt"/> now — or
    /// null with the reason: an address <see cref="ServerAddress.TryParse"/> refuses, a mode other than player / admin
    /// (without letter case), a name longer than <see cref="MaxLabel"/> characters or holding a control character. An
    /// agent port outside 1–65535 is 0: inferred.</summary>
    public static SavedServer? Create(string? address, string? mode, string? label, int agentPort, out SavedServerProblem problem)
    {
        problem = SavedServerProblem.None;
        if (!ServerAddress.TryParse(address, out var parsed, out _) || parsed is null)
        {
            problem = SavedServerProblem.BadAddress;
            return null;
        }
        string m = (mode ?? "").Trim().ToLowerInvariant();
        if (m is not (ModePlayer or ModeAdmin))
        {
            problem = SavedServerProblem.BadMode;
            return null;
        }
        string l = (label ?? "").Trim();
        if (l.Length > MaxLabel || l.Any(char.IsControl))
        {
            problem = SavedServerProblem.BadLabel;
            return null;
        }
        return new SavedServer
        {
            Address = parsed.ToString(),
            Host = parsed.Host,
            Mode = m,
            Label = l,
            AgentPort = agentPort is > 0 and <= 65535 ? agentPort : 0,
            SavedAt = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>A pinned SSH host key of a Linux box: the key's own type (read from the key blob, for display) and its
/// OpenSSH fingerprint, <c>SHA256:&lt;base64, no padding&gt;</c> — what <c>ssh-keygen -lf</c> prints. Not a secret.</summary>
public sealed class SshHostKeyPin
{
    public string Type { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

/// <summary>Persisted launcher state (settings, installed versions, selection). JSON at %LOCALAPPDATA%\Relic\state.json.</summary>
public sealed class RelicState
{
    /// <summary>
    /// The one lock on the launcher's state in this process. <see cref="Save"/> serialises and writes under it, and
    /// EVERY change of a collection reachable from the state — <see cref="Installed"/>, <see cref="Accounts"/> /
    /// <see cref="AccountLists"/> / <see cref="ServerAccounts"/>, <see cref="RelicSettings.OwnFiles"/>,
    /// <see cref="SavedServers"/>, <see cref="SshHostKeys"/> — runs under it together with the save that keeps it:
    /// <c>lock (RelicState.Gate) { change; state.Save(); }</c>. A game install and an agent install that end at the same
    /// moment then never serialise a list the other is changing, nor save over each other's change. The state's own
    /// methods take it themselves (it is re-entrant), so a caller holds it only to keep a change and its save together.
    /// Nothing the state does under it waits for another lock of the launcher (the log's own aside), and a holder never
    /// awaits, never takes another gate of the launcher (the session gate, the import's hold of the checks) and never
    /// waits for the message thread while it holds this one. Lock order: <c>ServerConfigStore.Gate</c> may be taken while
    /// this one is held — never the other way round, so no state method (its <see cref="Save"/> included) is called while
    /// that one is held.
    /// </summary>
    public static readonly object Gate = new();

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
        lock (Gate)
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
    }

    /// <summary>The list of a server + version, created on first use — seeded with the login an older build
    /// remembered, so the first account created after an upgrade never makes the previous one disappear.
    /// Called under <see cref="Gate"/>.</summary>
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

    public RememberedAccount? AccountFor(string host, int port, string version)
    {
        lock (Gate)
            return Accounts.TryGetValue($"{host}:{port}", out var per) && per.TryGetValue(version, out var a) ? a : null;
    }

    /// <summary>Every account this launcher created on a server + version, oldest first (a copy).</summary>
    public List<RememberedAccount> AccountListFor(string host, int port, string version)
    {
        lock (Gate)
        {
            string key = $"{host}:{port}";
            if (AccountLists.TryGetValue(key, out var per) && per.TryGetValue(version, out var list) && list is not null)
                return list.Where(a => a is { Name.Length: > 0 }).ToList();
            return AccountFor(host, port, version) is { Name.Length: > 0 } cur ? new List<RememberedAccount> { cur } : new List<RememberedAccount>();
        }
    }

    /// <summary>Show another of this launcher's accounts as the version's login. False when the name is not in
    /// its list (nothing changes).</summary>
    public bool UseAccount(string host, int port, string version, string name)
    {
        lock (Gate)
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
    }

    /// <summary>The id a signup on <paramref name="host"/> sends (32 hex characters): HMAC-SHA256 of the host
    /// under <see cref="ClientSecret"/>, so every server sees a different, stable id for this launcher and none
    /// can match it with another's. Creates the secret on first use — the caller saves the state then. Under
    /// <see cref="Gate"/>: two first uses at the same moment make one secret.</summary>
    public string ClientIdFor(string host)
    {
        string secret;
        lock (Gate)
        {
            if (string.IsNullOrEmpty(ClientSecret))
                ClientSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            secret = ClientSecret;
        }
        using var mac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        byte[] h = mac.ComputeHash(System.Text.Encoding.UTF8.GetBytes((host ?? "").Trim().ToLowerInvariant()));
        return Convert.ToHexString(h, 0, 16).ToLowerInvariant();
    }

    /// <summary>The start screen's saved servers (<see cref="SavedServer"/>), one per host, the most recently saved
    /// first. A state file without the field reads as an empty list. Change it only through <see cref="SaveServer"/> and
    /// <see cref="ForgetServer"/>; read it through <see cref="SavedServerList"/>.</summary>
    public List<SavedServer> SavedServers
    {
        get => _savedServers;
        set => _savedServers = (value ?? new List<SavedServer>()).Where(s => s is not null).ToList();
    }
    private List<SavedServer> _savedServers = new();

    /// <summary>The pinned SSH host keys of the Linux boxes, per <c>"&lt;host&gt;:&lt;port&gt;"</c> in the one spelling
    /// <see cref="PinKeyFor"/> gives (the host trimmed, without brackets, its trailing dots dropped, an IP literal in its
    /// canonical form, any other host lower-cased — the key <c>HostKeyPins.Key</c> spells) — a list of
    /// <see cref="SshHostKeyPin"/>. Every key a caller hands in is put in that spelling first, and so is every key a state
    /// file holds when it is read: no other spelling of an address can miss its pins, pin it twice or outlive
    /// <see cref="ForgetServer"/>. Change it only through <see cref="PinHostKey(string, int, string, string)"/> and
    /// <see cref="ForgetServer"/>; read it through <see cref="HostKeyPinsFor(string, int)"/>.</summary>
    public Dictionary<string, List<SshHostKeyPin>> SshHostKeys
    {
        get => _sshHostKeys;
        set => _sshHostKeys = PinMap(value);
    }
    private Dictionary<string, List<SshHostKeyPin>> _sshHostKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Saves <paramref name="entry"/> (made by <see cref="SavedServer.Create"/>) as the entry of its host: an
    /// entry the host has already is replaced — its id is kept —, and the entry comes first. <see cref="SavedServerProblem.Full"/>
    /// (nothing changed) when <see cref="SavedServer.MaxEntries"/> other hosts are saved already. The caller saves the
    /// state when the answer is <see cref="SavedServerProblem.None"/>, under <see cref="Gate"/> with this call. An entry
    /// that names no host is <see cref="SavedServerProblem.BadAddress"/>.</summary>
    public SavedServerProblem SaveServer(SavedServer entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Host)) return SavedServerProblem.BadAddress;
        lock (Gate)
        {
            var sameHost = _savedServers.Where(s => ServerConfig.SameHost(s.Host, entry.Host)).ToList();
            if (sameHost.Count == 0 && _savedServers.Count >= SavedServer.MaxEntries) return SavedServerProblem.Full;
            var stored = entry.Copy();
            stored.Id = sameHost.Select(s => s.Id).FirstOrDefault(IsSavedServerId)
                ?? NewSavedServerId(_savedServers.Select(s => s.Id));
            _savedServers.RemoveAll(s => ServerConfig.SameHost(s.Host, entry.Host));
            _savedServers.Insert(0, stored);
            return SavedServerProblem.None;
        }
    }

    /// <summary>The saved servers, newest first (by <see cref="SavedServer.SavedAt"/>) — copies.</summary>
    public List<SavedServer> SavedServerList()
    {
        lock (Gate)
            return _savedServers
                .Select(s => s.Copy())
                .OrderByDescending(s => DateTimeOffset.TryParse(s.SavedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue)
                .ToList();
    }

    /// <summary>The saved server with that id (a copy), or null.</summary>
    public SavedServer? SavedServerById(string? id)
    {
        lock (Gate)
            return _savedServers.FirstOrDefault(s => string.Equals(s.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase))?.Copy();
    }

    /// <summary>The pinned keys of a box's SSH address, copies — empty when none is pinned (and for no host or a port
    /// outside 1–65535).</summary>
    public List<SshHostKeyPin> HostKeyPinsFor(string host, int port) => PinsAt(PinKeyFor(host, port));

    /// <summary>The pinned keys of a box's SSH address written as one key, <c>"&lt;host&gt;:&lt;port&gt;"</c> (the port after
    /// its LAST colon — <c>HostKeyPins.Key</c>'s form, a bracketed IPv6 host as well), copies — empty when none is pinned
    /// and for a key that names no host or no port.</summary>
    public List<SshHostKeyPin> HostKeyPinsFor(string key) => PinsAt(PinKey(key));

    private List<SshHostKeyPin> PinsAt(string k)
    {
        if (k.Length == 0) return new List<SshHostKeyPin>();
        lock (Gate)
            return _sshHostKeys.TryGetValue(k, out var pins)
                ? pins.Select(p => new SshHostKeyPin { Type = p.Type, Fingerprint = p.Fingerprint }).ToList()
                : new List<SshHostKeyPin>();
    }

    /// <summary>Pins the key a box presented at its SSH address — the key the user confirmed, or the one an install that
    /// then succeeded was shown — as THE key of that address: it takes the place of every earlier pin there, since a
    /// changed key the user confirmed is the box's key now and the old one must stop being trusted. True when the pins
    /// changed (the caller saves the state then, under <see cref="Gate"/> with this call). Nothing is pinned for no host,
    /// a port outside 1–65535 or an empty fingerprint.</summary>
    public bool PinHostKey(string host, int port, string type, string fingerprint) =>
        PinAt(PinKeyFor(host, port), type, fingerprint);

    /// <summary><see cref="PinHostKey(string, int, string, string)"/> for an address written as one key,
    /// <c>"&lt;host&gt;:&lt;port&gt;"</c> (<see cref="HostKeyPinsFor(string)"/>); a key that names no host or no port pins
    /// nothing.</summary>
    public bool PinHostKey(string key, string type, string fingerprint) => PinAt(PinKey(key), type, fingerprint);

    private bool PinAt(string k, string type, string fingerprint)
    {
        string fp = (fingerprint ?? "").Trim(), t = (type ?? "").Trim();
        if (k.Length == 0 || fp.Length == 0) return false;
        lock (Gate)
        {
            if (_sshHostKeys.TryGetValue(k, out var pins) && pins.Count == 1
                && string.Equals(pins[0].Fingerprint, fp, StringComparison.Ordinal) && string.Equals(pins[0].Type, t, StringComparison.Ordinal))
                return false;
            _sshHostKeys[k] = new List<SshHostKeyPin> { new() { Type = t, Fingerprint = fp } };
            return true;
        }
    }

    /// <summary>The key the pins of a box's SSH address are kept under: the host in the pins' spelling
    /// (<see cref="PinHost"/>), a colon, the port — the spelling <c>HostKeyPins.Key</c> gives. "" for no host or a port
    /// outside 1–65535. Pure.</summary>
    public static string PinKeyFor(string? host, int port)
    {
        string h = PinHost(host);
        return h.Length == 0 || port is < 1 or > 65535 ? "" : h + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A host in the pins' one spelling: trimmed, one pair of brackets around it dropped (an IPv6 literal in
    /// URL form), its trailing dots dropped; an IP literal then in its canonical form — the address a connection to it
    /// reaches: an IPv6 one compressed and lower-cased (<c>2001:0db8:0:0:0:0:0:1</c> is <c>2001:db8::1</c>), an IPv4 one
    /// written with leading zeros as the address it is read as —, any other host lower-cased. Pure.</summary>
    private static string PinHost(string? host)
    {
        string h = HostCore(host).TrimEnd('.');
        return System.Net.IPAddress.TryParse(h, out var ip) ? ip.ToString() : h.ToLowerInvariant();
    }

    /// <summary>A <c>"&lt;host&gt;:&lt;port&gt;"</c> key in the pins' own spelling (<see cref="PinKeyFor"/>) — the port being
    /// the digits after the key's LAST colon —, or "" when it names no host or no port. Pure.</summary>
    public static string PinKey(string? key)
    {
        string k = (key ?? "").Trim();
        int colon = k.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(k.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int port)) return "";
        return PinKeyFor(k[..colon], port);
    }

    /// <summary>A host trimmed and without one pair of brackets around it (<c>[2001:db8::1]</c> is <c>2001:db8::1</c>).</summary>
    private static string HostCore(string? host)
    {
        string h = (host ?? "").Trim();
        return h.Length >= 2 && h[0] == '[' && h[^1] == ']' ? h[1..^1].Trim() : h;
    }

    /// <summary>
    /// Forgets everything this launcher keeps in the state for <paramref name="host"/> (<see cref="ServerConfig.SameHost"/>):
    /// its saved-server entries, and every key of <see cref="Accounts"/>, <see cref="AccountLists"/>,
    /// <see cref="ServerAccounts"/> and <see cref="SshHostKeys"/> whose host part is that host, on any port — the host part
    /// being the text before the LAST <c>:</c> of <c>"&lt;host&gt;:&lt;port&gt;"</c>, which an IPv6 host's own colons do not
    /// disturb; a host written in brackets (<c>[2001:db8::1]</c>) is that host, and the pins compare it in their own
    /// spelling (<see cref="PinHost"/>: another spelling of one IP address is that host too). True when anything went.
    /// The configured host, the <see cref="ClientSecret"/> and the server's own files are never touched (the caller saves
    /// the state, under <see cref="Gate"/> with this call).
    /// </summary>
    public bool ForgetServer(string host)
    {
        string h = HostCore(host);
        if (h.Length == 0) return false;
        lock (Gate)
        {
            bool any = _savedServers.RemoveAll(s => ServerConfig.SameHost(HostCore(s.Host), h)) > 0;
            any |= DropHost(Accounts, h);
            any |= DropHost(AccountLists, h);
            any |= DropHost(ServerAccounts, h);
            any |= DropPins(h);
            return any;
        }
    }

    /// <summary>Drops the pins of every port of <paramref name="host"/> (under <see cref="Gate"/>): the pins are kept in
    /// their own spelling (<see cref="PinHost"/>), so the host is put in it too — another spelling of one IP address is
    /// the same box. True when any went.</summary>
    private bool DropPins(string host)
    {
        string h = PinHost(host);
        if (h.Length == 0) return false;
        var keys = _sshHostKeys.Keys
            .Where(k => string.Equals(HostPartOf(k), h, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (string k in keys) _sshHostKeys.Remove(k);
        return keys.Count > 0;
    }

    /// <summary>The host part of a <c>"&lt;host&gt;:&lt;port&gt;"</c> key: the text before its last colon (the whole key
    /// when it has none).</summary>
    public static string HostPartOf(string key)
    {
        string k = key ?? "";
        int colon = k.LastIndexOf(':');
        return colon < 0 ? k : k[..colon];
    }

    private static bool DropHost<T>(Dictionary<string, T>? map, string host)
    {
        if (map is null) return false;
        var keys = map.Keys.Where(k => ServerConfig.SameHost(HostCore(HostPartOf(k)), host)).ToList();
        foreach (string k in keys) map.Remove(k);
        return keys.Count > 0;
    }

    private static bool IsSavedServerId(string? id) =>
        id is { Length: 8 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string NewSavedServerId(IEnumerable<string?> taken)
    {
        var used = new HashSet<string>(taken.OfType<string>(), StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            if (used.Add(id)) return id;
        }
    }

    /// <summary>The pins as they are kept: every key in the pins' own spelling (<see cref="PinKey"/> — the first of two
    /// keys that spell one address wins, a key that names no host or no port is dropped), and only pins that name a
    /// fingerprint.</summary>
    private static Dictionary<string, List<SshHostKeyPin>> PinMap(Dictionary<string, List<SshHostKeyPin>>? map)
    {
        var clean = new Dictionary<string, List<SshHostKeyPin>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, pins) in map ?? new Dictionary<string, List<SshHostKeyPin>>())
        {
            string k = PinKey(key);
            if (k.Length == 0 || pins is null) continue;
            var kept = pins.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Fingerprint)).ToList();
            if (kept.Count > 0) clean.TryAdd(k, kept);
        }
        return clean;
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
        lock (Gate)
        {
            string key = $"{host}:{port}";
            name ??= "";
            if (!ServerAccounts.TryGetValue(key, out var per))
                ServerAccounts[key] = per = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (per.TryGetValue(version, out var old) && string.Equals(old, name, StringComparison.Ordinal)) return false;
            per[version] = name;
            return true;
        }
    }

    /// <summary>The remembered answer, or null when this server never answered for the version ("" = it
    /// answered "no pre-made account").</summary>
    public string? ServerAccountFor(string host, int port, string version)
    {
        lock (Gate)
            return ServerAccounts.TryGetValue($"{host}:{port}", out var per) && per.TryGetValue(version, out var n) ? n : null;
    }

    /// <summary>Infer <see cref="InstalledVersion.Origin"/> for entries written by older builds:
    /// a launcher install always sits at <c>&lt;InstallRoot&gt;\Genshin &lt;id&gt;</c> (or at least in a
    /// folder of that name — InstallRoot may have moved); anything else was imported. Returns true
    /// when something was filled in (caller saves).</summary>
    public bool FillOrigins()
    {
        lock (Gate)
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
    }

    /// <summary>Re-read <see cref="InstalledVersion.Voices"/> from the disk markers for every install
    /// whose drive is mounted — the same guard as <see cref="PruneMissing"/>: an unplugged external disk
    /// must not blank the list of a perfectly fine install. Returns true when something changed (the
    /// caller saves). Four File.Exists per version — negligible at boot.</summary>
    public bool RefreshVoices()
    {
        lock (Gate)
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
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relic", "state.json");

    /// <summary>The state on disk; a fresh one when there is none. Under <see cref="Gate"/>, so it never reads the file
    /// in the middle of a save of this process.</summary>
    public static RelicState Load(string? path = null)
    {
        path ??= DefaultPath;
        lock (Gate)
        {
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
    }

    /// <summary>Atomic: write to .tmp then swap in, keeping the previous file as .bak — a crash or
    /// power loss mid-write cannot leave a truncated state.json behind. Serialises and writes under
    /// <see cref="Gate"/>: a change made under it is never half in the file.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        lock (Gate)
        {
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
    }

    /// <summary>The state file as the disk holds it at the call, or null when it is not there or cannot be read —
    /// no fallback to the .bak and nothing kept aside, unlike <see cref="Load"/>: for a reader that must
    /// never act on a state it did not read. Another process may be replacing the file at this very
    /// moment (<see cref="Save"/>: one rename puts the old file aside, a second one the new file in its
    /// place), so a busy file — and one that is not there — is read again a few times.</summary>
    public static RelicState? TryRead(string? path = null)
    {
        path ??= DefaultPath;
        for (int attempt = 1; ; attempt++)
        {
            if (!File.Exists(path))
            {
                if (attempt < 4) { Thread.Sleep(150); continue; }
                Log.Info($"state.json is not there ({path})");
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<RelicState>(File.ReadAllText(path), Opts);
            }
            catch (IOException ex) when (attempt < 4)
            {
                Log.Info($"state.json is busy ({ex.Message}) — reading it again");
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                Log.Error($"state.json could not be read ({path})", ex);
                return null;
            }
        }
    }

    /// <summary>Store a version's answer about the player's own game files straight in the state FILE: the
    /// file read again, that one entry set, saved. For the play process of a desktop shortcut started while
    /// the launcher runs: a save of the whole state it read at its start would put back what the launcher
    /// saved since. False when the file could not be read or written — the answer then holds for this
    /// launch alone. Never throws. The read, the change and the save run under <see cref="Gate"/> as one.</summary>
    public static bool RememberOwnFilesChoice(string versionId, string choice, string? path = null)
    {
        try
        {
            lock (Gate)
            {
                var fresh = TryRead(path);
                if (fresh is null) return false;
                if (fresh.Settings.SetOwnFilesChoice(versionId, choice)) fresh.Save(path);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"storing the answer about the own game files of {versionId} failed", ex);
            return false;
        }
    }

    /// <summary>Store the injector a version's folder holds (LauncherExe / InjectDll, as
    /// <c>InstallService.DetectInjector</c> found them) straight in the state FILE: the file read again, that one
    /// registration set, saved. For the play process of a desktop shortcut: a save of the whole state it read at its
    /// start would put back what the launcher saved since. False when the file could not be read or written, or does
    /// not register the version. Never throws. The read, the change and the save run under <see cref="Gate"/> as
    /// one.</summary>
    public static bool RememberInjector(string versionId, string? launcherExe, string? injectDll, string? path = null)
    {
        try
        {
            lock (Gate)
            {
                var fresh = TryRead(path);
                if (fresh is null || fresh.FindInstalled(versionId) is not { } inst) return false;
                inst.LauncherExe = launcherExe;
                inst.InjectDll = injectDll;
                fresh.Save(path);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"storing the injector of {versionId} failed", ex);
            return false;
        }
    }

    public InstalledVersion? FindInstalled(string id)
    {
        lock (Gate)
            return Installed.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Takes back what a registration of <paramref name="versionId"/> changed, after it failed: the version's entries in
    /// <see cref="Installed"/> become again those of <paramref name="before"/> — the list as it was read before the
    /// registration: the same entries, at their places — and the selection, while it still names that version, becomes
    /// <paramref name="selectedBefore"/> again. The entries of every other version and a selection made since stay as they
    /// are now: a change another part of the launcher made meanwhile is never undone with it. Under <see cref="Gate"/>;
    /// nothing is saved (a registration whose save failed left the file as it was).
    /// </summary>
    public void RestoreRegistration(string versionId, IReadOnlyList<InstalledVersion> before, string? selectedBefore)
    {
        ArgumentNullException.ThrowIfNull(before);
        bool Mine(InstalledVersion? i) => i is not null && string.Equals(i.Id, versionId, StringComparison.OrdinalIgnoreCase);
        lock (Gate)
        {
            Installed.RemoveAll(i => Mine(i) && !before.Contains(i));
            for (int k = 0; k < before.Count; k++)
            {
                var old = before[k];
                if (!Mine(old) || Installed.Contains(old)) continue;
                Installed.Insert(Math.Min(k, Installed.Count), old);
            }
            if (string.Equals(SelectedVersionId, versionId, StringComparison.OrdinalIgnoreCase)) SelectedVersionId = selectedBefore;
        }
    }

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
        lock (Gate)
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
}
