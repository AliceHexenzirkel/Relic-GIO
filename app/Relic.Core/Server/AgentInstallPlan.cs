using System.Net;
using Relic.Core.Util;

namespace Relic.Core.Server;

/// <summary>
/// The parameters of an agent installation — identical for both targets ("a Linux box over SSH" and
/// "this Windows PC"): what the install does with each server version (<see cref="Versions"/>,
/// <see cref="ChoiceFor"/>: not now, the extracted package is in a folder, the agent downloads the
/// ready-made package from the Internet Archive into a folder once it runs, or the version keeps the
/// folder the target already uses — <see cref="KeepStoredFolders"/> puts that stored folder into the row,
/// since a folder changes only through a relocation), which local IP docker binds on, which IP
/// is advertised to clients, where the agent listens, its bearer token, MUIP host/region/sign key, the
/// two stack secrets the agent sets on a stack still on the vendor's published ones (MySQL root
/// password, Flask secret key), whether the agent leaves each stack's own <c>.env</c> as it is instead
/// (<see cref="KeepEnv"/>) and the server name shown to players.
/// Rendered into the KEY=VALUE config both <c>install_agent.sh</c> (as env) and
/// <c>gio_agent.py --config</c> read; the per-version choices ride in the installer environment only.
/// </summary>
public sealed class AgentInstallPlan
{
    public const string TargetLinux = "linux";
    public const string TargetWindows = "windows";

    /// <summary>Files/dirs a vendor stack folder must contain before an install is attempted.</summary>
    public static readonly string[] RequiredStackEntries = { "docker-compose.yml.tmpl", ".env", "server", "sdk", "dockerfiles" };

    /// <summary>What a secret the agent takes from its config may look like (the MUIP sign key, the Flask
    /// secret key) — identical to the agent's <c>SECRET_VALUE_RE</c>. The alphabet already bars quotes,
    /// backslashes and whitespace, the characters systemd's EnvironmentFile parser would reinterpret (the
    /// value is written unquoted). .NET's <c>$</c> also matches before a final newline, which is why
    /// <see cref="Validate"/> matches the TRIMMED value.</summary>
    public const string SecretValuePattern = "^[A-Za-z0-9_-]{8,128}$";

    /// <summary>An alias of <see cref="SecretValuePattern"/>, named after the MUIP sign key.</summary>
    public const string MuipKeyPattern = SecretValuePattern;

    /// <summary>The MySQL root password has its own, stricter rule — the agent's <c>MYSQL_ROOT_RE</c>: the
    /// same alphabet and length (8–128 characters), but the FIRST character must be a letter. The vendor
    /// stack renders this one value UNQUOTED into its docker-compose.yml, where docker compose reads a
    /// value that starts with a digit or '-' as a number or a date ("01234567" → 342391, "2026-10-01"):
    /// the database would then be initialised with another string than <c>.env</c> holds.</summary>
    public const string MysqlPasswordPattern = "^[A-Za-z][A-Za-z0-9_-]{7,127}$";

    public string Target { get; set; } = TargetLinux;

    /// <summary>The server versions this plan is about, in the order their folders are written into the
    /// config: every loop over versions below goes through this list, and each key of a version is
    /// derived from its id (<see cref="ServerVersions.DirKey"/>, <see cref="ServerVersions.StackKey"/>).
    /// <see cref="ServerVersions.All"/> unless a caller hands in its own list.</summary>
    public IReadOnlyList<string> Versions { get; init; } = ServerVersions.All;

    private readonly Dictionary<string, string> _dirs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fetch = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StackChoice> _choice = new(StringComparer.Ordinal);
    private readonly HashSet<string> _storedFolders = new(StringComparer.Ordinal);

    /// <summary>The folder configured for a version id, or "" when none was given.</summary>
    public string DirFor(string version) => _dirs.TryGetValue(version ?? "", out var dir) ? dir : "";

    /// <summary>Give a version its stack folder ("" = none). A folder set here is the caller's, not the
    /// target's stored one (<see cref="FolderIsStored"/> is false afterwards).</summary>
    public void SetDir(string version, string? dir)
    {
        _dirs[version] = dir ?? "";
        _storedFolders.Remove(version);
    }

    /// <summary>What the install does with a version: the explicit choice when one was made
    /// (<see cref="SetChoice"/>); else <see cref="StackChoice.Download"/> while the version carries the
    /// download mark (<see cref="SetFetch"/>), <see cref="StackChoice.Have"/> while it has a folder,
    /// <see cref="StackChoice.Later"/> otherwise — so a plan built from folders and marks alone is read by its
    /// folders and marks. Installer-only, like <see cref="UpgradeForce"/>: the choice travels as
    /// <c>GIO_STACK_&lt;nn&gt;</c> in <see cref="ToEnvironment"/> and never reaches the agent config — an
    /// upgrade run would otherwise act on a choice made for an earlier install.</summary>
    public StackChoice ChoiceFor(string version)
    {
        string v = version ?? "";
        if (_choice.TryGetValue(v, out var choice)) return choice;
        if (_fetch.Contains(v)) return StackChoice.Download;
        return string.IsNullOrWhiteSpace(DirFor(v)) ? StackChoice.Later : StackChoice.Have;
    }

    /// <summary>Make the version's choice explicit (<see cref="ChoiceFor"/>). The folder and the download mark
    /// stay as they are; the row is the caller's, not one holding the target's stored folder
    /// (<see cref="FolderIsStored"/> is false afterwards).</summary>
    public void SetChoice(string version, StackChoice choice)
    {
        _choice[version] = choice;
        _storedFolders.Remove(version);
    }

    /// <summary>The version's stack is NOT on the target yet: <see cref="DirFor"/> is the folder the agent
    /// should download and extract the ready-made package into (<see cref="ChoiceFor"/> is
    /// <see cref="StackChoice.Download"/>).</summary>
    public bool FetchFor(string version) => ChoiceFor(version) == StackChoice.Download;

    /// <summary>Mark a version for download, or take the mark back — the way a plan is built without an
    /// explicit choice: an explicit choice made earlier is taken back with it (the mark and the folder
    /// decide again, <see cref="ChoiceFor"/>), and so is a stored folder (<see cref="FolderIsStored"/>).</summary>
    public void SetFetch(string version, bool fetch)
    {
        _choice.Remove(version);
        _storedFolders.Remove(version);
        if (fetch) _fetch.Add(version); else _fetch.Remove(version);
    }

    /// <summary>True once <see cref="KeepStoredFolders"/> put the target's stored folder into the version's
    /// row — the folder the target already uses for it, which a re-install keeps (a folder changes only
    /// through a relocation). Such a folder is the target's own: <see cref="Validate"/> does not hold it to
    /// the spelling rules of a folder the form names, and <see cref="DownloadTargetProblems"/> leaves it to
    /// the agent to judge.</summary>
    public bool FolderIsStored(string version) => _storedFolders.Contains(version ?? "");

    /// <summary>Put the folder the target's configuration stores for each version into its row — the one
    /// rule both install paths apply before anything is checked or written: a folder the target already
    /// uses for a version stays, whatever the form named (a stack folder changes only through a
    /// relocation). <paramref name="storedDir"/> answers the stored folder of a version ("" = the target
    /// stores none). A version whose target stores a folder takes it, choice <see cref="StackChoice.Keep"/> —
    /// or <see cref="StackChoice.Download"/>, into that folder, when the form chose a download; one entry per
    /// such version, <see cref="StoredFolder.Replaced"/> when the form had named another folder (only those
    /// are worth a line). A row that holds a stored folder while the configuration names none (a relocation
    /// took the version off meanwhile) is set up later. Idempotent: called again with the same answers it
    /// changes nothing, so the install can call it once more over a configuration read again right before it
    /// is written.</summary>
    public IReadOnlyList<StoredFolder> KeepStoredFolders(Func<string, string> storedDir)
    {
        var kept = new List<StoredFolder>();
        foreach (var v in Versions)
        {
            string stored = (storedDir(v) ?? "").Trim();
            bool alreadyStored = FolderIsStored(v);
            if (stored.Length > 0)
            {
                if (alreadyStored && DirFor(v) == stored)
                {
                    kept.Add(new StoredFolder(v, stored, "", false));
                    continue;
                }
                var choice = ChoiceFor(v);
                string given = (choice is StackChoice.Later or StackChoice.Keep) || alreadyStored ? "" : DirFor(v).Trim();
                SetDir(v, stored);
                SetChoice(v, choice == StackChoice.Download ? StackChoice.Download : StackChoice.Keep);
                _storedFolders.Add(v);
                kept.Add(new StoredFolder(v, stored, given, given.Length > 0 && !SameFolder(given, stored, IsWindows)));
            }
            else if (alreadyStored)
            {
                SetDir(v, "");
                SetChoice(v, StackChoice.Later);
            }
        }
        return kept;
    }

    /// <summary>Do two folder names name the same folder? Both trimmed; on Windows '/' reads as '\', a
    /// trailing '\' is dropped, letter case does not count and a full path on a drive letter is first spelled the way
    /// the agent answers it (<see cref="NormalWindowsFolder"/>: a doubled separator, a "." or ".." segment resolved);
    /// elsewhere a trailing '/' is dropped and letter case counts. Two blank names are no folder at all, so never the
    /// same one. Pure.</summary>
    public static bool SameFolder(string? a, string? b, bool windows)
    {
        string x = (a ?? "").Trim(), y = (b ?? "").Trim();
        if (x.Length == 0 || y.Length == 0) return false;
        if (windows)
        {
            static string Spelled(string d) => (IsWindowsDrivePath(d) ? NormalWindowsFolder(d) : d).Replace('/', '\\').TrimEnd('\\');
            return string.Equals(Spelled(x), Spelled(y), StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(x.TrimEnd('/'), y.TrimEnd('/'), StringComparison.Ordinal);
    }

    private static readonly System.Text.RegularExpressions.Regex DrivePathRe = new(@"^[A-Za-z]:[\\/]");

    /// <summary>A full path on a drive letter of a Windows PC ("C:\…", "D:/…") — the only kind of stack
    /// folder the agent on Windows takes (Docker Desktop cannot bind-mount a network path). A UNC path
    /// (<c>\\server\share</c>), a drive-relative one (<c>C:folder</c>) and anything else are not. Pure.</summary>
    public static bool IsWindowsDrivePath(string? dir) => DrivePathRe.IsMatch(dir ?? "");

    /// <summary>The folder of the 1.6 stack (<see cref="DirFor"/>).</summary>
    public string Dir16 { get => DirFor("1.6"); set => SetDir("1.6", value); }
    /// <summary>The folder of the 2.8 stack (<see cref="DirFor"/>).</summary>
    public string Dir28 { get => DirFor("2.8"); set => SetDir("2.8", value); }
    /// <summary>The download mark of the 1.6 stack (<see cref="FetchFor"/>).</summary>
    public bool Fetch16 { get => FetchFor("1.6"); set => SetFetch("1.6", value); }
    /// <summary>The download mark of the 2.8 stack (<see cref="FetchFor"/>).</summary>
    public bool Fetch28 { get => FetchFor("2.8"); set => SetFetch("2.8", value); }
    public string BindIp { get; set; } = "";
    public string AdvertisedIp { get; set; } = "";
    /// <summary>A DNS name (DDNS) the agent follows instead of a fixed <see cref="AdvertisedIp"/>; the two
    /// are exclusive. The agent resolves it at start and every 2 minutes and re-applies a changed address.</summary>
    public string AdvertisedHost { get; set; } = "";
    public string Listen { get; set; } = "0.0.0.0:18080";
    public string Token { get; set; } = "";
    public string MuipHost { get; set; } = "";
    public string Region { get; set; } = "dev_docker";
    /// <summary>The MUIP sign key the agent writes into a stack it prepares while the stack still carries
    /// the vendor's published key; empty = the agent picks a random one (<c>GIO_MUIP_KEY</c>).
    /// Emitted SET AND EMPTY like <see cref="MuipHost"/>, so install_agent.sh keeps a stored key on an
    /// upgrade run — and not at all while <see cref="KeepEnv"/> is on. A secret: never echoed in any event
    /// or log (AgentDeployer redacts it like the token).</summary>
    public string MuipKey { get; set; } = "";
    /// <summary>The MySQL root password the agent sets when it prepares a stack that still carries the
    /// vendor's published one (<c>GIO_MYSQL_ROOT_PASSWORD</c>) — a stack whose password is already
    /// private is never changed from here. Empty = keep the value the box stores; none stored = the agent
    /// picks a random one. OMITTED when blank, unlike <see cref="MuipKey"/> (see <see cref="RenderConfig"/>),
    /// and whatever it holds while <see cref="KeepEnv"/> is on; held to its own pattern
    /// (<see cref="MysqlPasswordPattern"/>). A secret: never echoed in any event or log (AgentDeployer
    /// redacts it like the token).</summary>
    public string MysqlPassword { get; set; } = "";
    /// <summary>The Flask secret key — what the stack's login service (sdk) signs its web cookies with —
    /// replaced like <see cref="MysqlPassword"/> while the stack still carries the vendor's published one
    /// (<c>GIO_FLASK_SECRET_KEY</c>). Omitted by the same two rules (blank, or <see cref="KeepEnv"/> on:
    /// the box keeps its stored value), same secrecy; the general <see cref="SecretValuePattern"/>.</summary>
    public string FlaskKey { get; set; } = "";
    /// <summary>"Keep .env settings" (<c>GIO_KEEP_ENV</c>): while it is on, the agent leaves each server's
    /// own <c>.env</c> as it is — it replaces none of the vendor's published passwords (MySQL root, Flask
    /// key — nor the MUIP sign key or the stack internal password) and does not rewrite <c>OUTER_IP</c>.
    /// A TRI-STATE, because the launcher knows what the box stores only for the agent on this PC:
    /// <c>true</c> writes <c>GIO_KEEP_ENV=1</c> and leaves <see cref="BindIp"/>, <see cref="MuipKey"/>,
    /// <see cref="MysqlPassword"/> and <see cref="FlaskKey"/> out of the config whatever they hold (the
    /// agent applies none of them while the option is on, and what the box stores for those keys stays);
    /// <c>false</c> writes <c>GIO_KEEP_ENV=0</c>; <c>null</c> = not stated — the key is left out and the
    /// box keeps its stored value.</summary>
    public bool? KeepEnv { get; set; }
    /// <summary>The 7z extractor the agent on this PC unpacks the server packages with (<c>GIO_7Z</c>), a full path to
    /// an <c>.exe</c> on a drive letter — Relic's own <c>7zr.exe</c> when the agent finds no tool of its own
    /// (<see cref="LocalAgent.FillSevenZip"/>). Windows only; blank = not written, so a re-install keeps the line the
    /// config stores (<see cref="LocalAgent.MergeConfig(string, string?)"/>).</summary>
    public string SevenZip { get; set; } = "";
    public string ServerName { get; set; } = "";
    // Blank by DESIGN, like every other optional field: blank means "keep what the box already has".
    // Emitting "once"/"now" unconditionally would make every launcher-driven upgrade silently reset an admin
    // who had chosen "never"/"later" — and a reset to "once" can trigger a provision, which drops and
    // re-imports the player accounts. The agent and install_agent.sh each default to once/now anyway.
    public string ProvisionMode { get; set; } = "";
    public string TxtFixesMode { get; set; } = "";

    /// <summary>Admin ticked "upgrade even if the running agent cannot be reached". Installer-only, so it
    /// is NOT part of <see cref="RenderConfig"/> — it never belongs in /etc/gio-agent/config. The script
    /// honours it only when the pre-upgrade probe is INCONCLUSIVE; a genuinely busy agent still refuses.</summary>
    public bool UpgradeForce { get; set; }

    public bool IsWindows => string.Equals(Target, TargetWindows, StringComparison.OrdinalIgnoreCase);

    /// <summary>False while <see cref="KeepEnv"/> is on: the four values the agent would put in place of a
    /// stack's own settings — <see cref="BindIp"/>, <see cref="MuipKey"/>, <see cref="MysqlPassword"/>,
    /// <see cref="FlaskKey"/> — are then neither sent (<see cref="RenderConfig"/>) nor checked
    /// (<see cref="Validate"/>).</summary>
    private bool SendsStackOverrides => KeepEnv != true;

    /// <summary>The versions chosen for download AND given a folder (a subset of <see cref="Versions"/>,
    /// in its order). A download without a folder is a validation error, not a download — it is left out
    /// here so the log never names a version the agent could not place anywhere.</summary>
    public IReadOnlyList<string> FetchVersions() =>
        Versions.Where(v => ChoiceFor(v) == StackChoice.Download && !string.IsNullOrWhiteSpace(DirFor(v))).ToList();

    /// <summary>One user-facing problem per version chosen for download that has no server package to
    /// download (<see cref="StackPackage.Published"/>); empty = every download can be honoured. Kept out of
    /// <see cref="Validate"/>, which reads no file: <paramref name="package"/> is the caller's lookup into
    /// the catalogues that go to the box (<see cref="ServerVersions.Package"/> with their folder).</summary>
    public IReadOnlyList<string> FetchProblems(Func<string, StackPackage> package) =>
        Versions.Where(v => ChoiceFor(v) == StackChoice.Download && !package(v).Published)
            .Select(v => L.T("core.deploy.fetchNoPackage", new { version = v })).ToList();

    /// <summary>One user-facing problem per download into a folder that holds other files and no server —
    /// the agent would refuse that download ("contains files but is not a server stack"). Asked only of a
    /// folder the FORM names: <paramref name="kindOf"/> (what stands at a folder of the target) is asked
    /// nothing about a folder the target already stores for the version (<see cref="FolderIsStored"/>),
    /// which a re-install keeps whatever it holds — the agent judges a download there when it is asked for
    /// one.</summary>
    public IReadOnlyList<string> DownloadTargetProblems(Func<string, FolderKind> kindOf)
    {
        var problems = new List<string>();
        foreach (var v in Versions)
        {
            if (ChoiceFor(v) != StackChoice.Download || FolderIsStored(v)) continue;
            string dir = DirFor(v);
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (kindOf(dir) == FolderKind.Other) problems.Add(L.T("core.deploy.downloadDirNotEmpty", new { version = v, dir }));
        }
        return problems;
    }

    /// <summary>Every path of the target the checks of an install ask about — <see cref="MissingStackEntries"/>,
    /// <see cref="FetchVersionsReally"/> and <see cref="DownloadTargetProblems"/> —, in the plan's order and
    /// each once: for a version whose package is in its folder that folder and each of
    /// <see cref="RequiredStackEntries"/> in it, for a download the folder and its
    /// <c>docker-compose.yml.tmpl</c>. One probe of the target answers all of them.</summary>
    public IReadOnlyList<string> ProbePaths()
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string p) { if (seen.Add(p)) paths.Add(p); }
        foreach (var v in Versions)
        {
            string dir = DirFor(v);
            if (string.IsNullOrWhiteSpace(dir)) continue;
            switch (ChoiceFor(v))
            {
                case StackChoice.Have:
                    Add(dir);
                    foreach (var e in RequiredStackEntries) Add(Join(dir, e));
                    break;
                case StackChoice.Download:
                    Add(dir);
                    Add(Join(dir, "docker-compose.yml.tmpl"));
                    break;
            }
        }
        return paths;
    }

    /// <summary>An entry of a stack folder of the target, spelled the target's way.</summary>
    private string Join(string dir, string entry) => IsWindows ? Path.Combine(dir, entry) : dir.TrimEnd('/') + "/" + entry;

    /// <summary>True when a download this plan asks for takes a 7z extractor on the box
    /// (<see cref="StackPackage.NeedsExtractor"/>): a package that comes as a .zip is unpacked by the agent
    /// itself, so a plan that downloads only such packages asks for no tool.</summary>
    public bool FetchNeedsExtractor(Func<string, StackPackage> package) =>
        FetchVersions().Any(v => package(v).NeedsExtractor);

    /// <summary>The versions that will REALLY be downloaded, given a probe that answers "does this path
    /// exist on the target". <c>install_agent.sh</c> applies the same rule and quietly skips the download of
    /// a version whose folder already holds <c>docker-compose.yml.tmpl</c> ("already holds a server stack --
    /// the download will be skipped"). Without this the launcher would queue that version anyway, the agent
    /// would refuse it with 409 "already on this server", and the queue behind it would be dropped with a
    /// failure toast for a download that was never needed.</summary>
    public IReadOnlyList<string> FetchVersionsReally(Func<string, bool> exists)
    {
        var list = new List<string>();
        foreach (var v in FetchVersions())
            if (!exists(Join(DirFor(v), "docker-compose.yml.tmpl"))) list.Add(v);
        return list;
    }

    /// <summary>Agent listen port (from <see cref="Listen"/>), 18080 when unparsable.</summary>
    public int ListenPort
    {
        get
        {
            var s = (Listen ?? "").Trim();
            int i = s.LastIndexOf(':');
            string p = i >= 0 ? s[(i + 1)..] : s;
            return int.TryParse(p, out int port) && port is > 0 and < 65536 ? port : ServerAddress.DefaultAgentPort;
        }
    }

    /// <summary>Listen host, derived EXACTLY as the agent's own <c>parse_listen()</c> derives it. The two
    /// must agree or Relic saves a direct config for an address the agent never bound: a value with no
    /// usable host half ("18080", "0.0.0.0", ":18080") makes the agent bind LOOPBACK — not the wildcard.
    /// <see cref="HasExplicitListenHost"/> rejects those before we get
    /// here, so this is the agreement guarantee rather than the only defence.</summary>
    public string ListenHost
    {
        get
        {
            var s = (Listen ?? "").Trim();
            int i = s.LastIndexOf(':');
            if (i <= 0) return "127.0.0.1";     // no separator, or ":18080" — agent falls back to loopback
            return s[..i].Trim('[', ']');       // "[::]:18080" -> "::"
        }
    }

    /// <summary>True when <see cref="Listen"/> spells out a host AND a port, so both sides parse it the
    /// same way. "18080" or "0.0.0.0" alone are ambiguous and are refused by <see cref="Validate"/>.</summary>
    public bool HasExplicitListenHost
    {
        get
        {
            var s = (Listen ?? "").Trim();
            int i = s.LastIndexOf(':');
            return i > 0 && int.TryParse(s[(i + 1)..], out int port) && port is > 0 and < 65536;
        }
    }

    /// <summary>Where a health check running ON THE BOX should dial: a wildcard bind answers on loopback,
    /// a specific bind answers only on itself. IPv6 comes back bracketed, ready for a URL.</summary>
    public string ProbeAuthority
    {
        get
        {
            string h = ListenHost;
            if (h is "0.0.0.0" or "::" or "*" or "") h = "127.0.0.1";
            return h.Contains(':') ? $"[{h}]:{ListenPort}" : $"{h}:{ListenPort}";
        }
    }

    /// <summary>Structural validation (no I/O). Returns user-facing problems; empty = OK. Whether a version
    /// chosen for download has a package to download is a question about a file, asked separately
    /// (<see cref="FetchProblems"/>), and so are the folders themselves (<see cref="MissingStackEntries"/>,
    /// <see cref="DownloadTargetProblems"/>). A plan that sets no version up is valid: the agent then runs
    /// alone, and the Server page adds versions.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errs = new List<string>();
        foreach (var label in Versions)
        {
            var choice = ChoiceFor(label);
            // Only a folder the form names is held to these rules: a version set up later or kept names
            // none, and a folder the target already stores is its own (KeepStoredFolders).
            if ((choice is StackChoice.Later or StackChoice.Keep) || FolderIsStored(label)) continue;
            string dir = DirFor(label);
            if (string.IsNullOrWhiteSpace(dir))
            {
                // "Download it" or "I have it" with no folder: there is nowhere to put it, or to find it.
                errs.Add(choice == StackChoice.Download
                    ? L.T("core.deploy.fetchNeedsDir", new { version = label })
                    : L.T("core.deploy.haveNeedsDir", new { version = label }));
                continue;
            }
            // Windows: a full path on a drive letter of this PC. Path.IsPathRooted would take a UNC share
            // (\\server\share) and a drive-relative "C:folder" as well — the agent takes neither.
            if (IsWindows)
            {
                if (!IsWindowsDrivePath(dir)) errs.Add(L.T("core.deploy.dirNotLocal", new { version = label, dir }));
            }
            else if (!dir.StartsWith('/'))
                errs.Add(L.T("core.deploy.dirNotAbsolute", new { version = label, dir }));
            // Same reason as the token: the path is written unquoted into an EnvironmentFile,
            // and silently stripping it would point the agent at a directory that does not exist.
            if (dir.IndexOfAny(new[] { '"', '\'' }) >= 0)
                errs.Add(L.T("core.deploy.dirQuotes", new { version = label, dir }));
        }
        // The bind IP — like the three secrets further down — is checked only when it is sent: with
        // "Keep .env settings" on, a value left in a field the form hides must not refuse a plan it is not
        // part of.
        if (SendsStackOverrides && !string.IsNullOrWhiteSpace(BindIp) && !IsPlainIp(BindIp))
            errs.Add(L.T("core.deploy.badBindIp", new { ip = BindIp }));
        if (!string.IsNullOrWhiteSpace(AdvertisedIp) && !IsPlainIp(AdvertisedIp))
            errs.Add(L.T("core.deploy.badAdvertisedIp", new { ip = AdvertisedIp }));
        // A NAME, not an IP (an IP belongs in the field above) and nothing systemd's EnvironmentFile parser
        // would reinterpret: CheckHostName answers Dns only for a plain host name.
        if (!string.IsNullOrWhiteSpace(AdvertisedHost) && Uri.CheckHostName(AdvertisedHost.Trim().TrimEnd('.')) != UriHostNameType.Dns)
            errs.Add(L.T("core.deploy.badAdvertisedHost", new { host = AdvertisedHost }));
        // Both set would install a config whose IP the agent silently ignores (the host wins) — refuse instead.
        if (!string.IsNullOrWhiteSpace(AdvertisedHost) && !string.IsNullOrWhiteSpace(AdvertisedIp))
            errs.Add(L.T("core.deploy.advertisedBoth"));
        if (string.IsNullOrWhiteSpace(Token) || Token.Trim().Length < 16)
            errs.Add(L.T("core.deploy.tokenTooShort"));
        if (Token.Any(char.IsWhiteSpace)) errs.Add(L.T("core.deploy.tokenWhitespace"));
        // Quotes are refused rather than stripped: RenderConfig writes the value unquoted into an
        // EnvironmentFile, but stripping would install a DIFFERENT token than the one saved in
        // server.json and shown to the admin — a 401 against the agent we just installed.
        if (Token.IndexOfAny(new[] { '"', '\'', '\\' }) >= 0) errs.Add(L.T("core.deploy.tokenQuotes"));
        if (!Regex("^[A-Za-z0-9_.:\\[\\]-]*$", Listen)) errs.Add(L.T("core.deploy.badListen", new { listen = Listen }));
        // Both halves must be spelled out. ListenPort defaults to 18080 for anything unparsable, so a
        // "ListenPort <= 0" test could never fire — and a bare "18080" quietly binds the agent to
        // loopback, which reads as a clean install the launcher can then never reach.
        if (!HasExplicitListenHost) errs.Add(L.T("core.deploy.badListen", new { listen = Listen }));
        if (!string.IsNullOrWhiteSpace(MuipHost) && !Uri.TryCreate(MuipHost.Trim(), UriKind.Absolute, out _))
            errs.Add(L.T("core.deploy.badMuipHost", new { host = MuipHost }));
        // Quotes and backslashes are what systemd's EnvironmentFile parser consumes, and this value is
        // written into it unquoted. Uri.TryCreate happily accepts a quote in the path, and one unbalanced
        // quote swallows every assignment after it (payload dir, state path) — the agent then starts blind.
        if (!string.IsNullOrWhiteSpace(MuipHost) && MuipHost.IndexOfAny(new[] { '"', '\'', '\\' }) >= 0)
            errs.Add(L.T("core.deploy.badMuipHost", new { host = MuipHost }));
        if (!Regex("^[A-Za-z0-9_.-]{1,40}$", Region)) errs.Add(L.T("core.deploy.badRegion"));
        // Empty = "random at the next Prepare server" is fine; anything else must be a key muipserver
        // accepts AND the agent's own validator (SECRET_VALUE_RE) would not throw away with a warning.
        if (SendsStackOverrides && !string.IsNullOrWhiteSpace(MuipKey) && !Regex(SecretValuePattern, MuipKey.Trim()))
            errs.Add(L.T("core.deploy.badMuipKey"));
        // The two stack secrets, same idea: blank is fine ("keep what the box has"), anything else must be
        // a value the agent would not drop — the root password by its own, stricter rule.
        // The two texts have NO placeholder on purpose: every message of this list reaches the error modal
        // and relic.log, and it must never quote a password.
        if (SendsStackOverrides && !string.IsNullOrWhiteSpace(MysqlPassword) && !Regex(MysqlPasswordPattern, MysqlPassword.Trim()))
            errs.Add(L.T("core.deploy.badMysqlPassword"));
        if (SendsStackOverrides && !string.IsNullOrWhiteSpace(FlaskKey) && !Regex(SecretValuePattern, FlaskKey.Trim()))
            errs.Add(L.T("core.deploy.badFlaskKey"));
        // The extractor is a path on THIS PC, and only a Windows plan writes it (SevenZipPathOk).
        if (!string.IsNullOrWhiteSpace(SevenZip) && (!IsWindows || !SevenZipPathOk(SevenZip)))
            errs.Add(L.T("core.deploy.badSevenZip", new { path = SevenZip.Trim() }));
        return errs;
    }

    /// <summary>What <c>GIO_7Z</c> may name on this PC: a full path on a drive letter to an <c>.exe</c>, without a double
    /// quote or a control character — the agent on Windows reads its configuration a line at a time and strips only a
    /// pair of quotes around a whole value, so an apostrophe inside a folder name (<c>C:\Users\O'Neil\…</c>) is just a
    /// letter there. Pure.</summary>
    public static bool SevenZipPathOk(string? tool)
    {
        string t = (tool ?? "").Trim();
        return IsWindowsDrivePath(t) && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && t.IndexOf('"') < 0 && !t.Any(char.IsControl);
    }

    /// <summary>A folder of this PC spelled the way the agent answers it (<c>os.path.normpath</c>): a full path with
    /// '\' separators, no doubled separator, no "." or ".." segment, no separator at the end (a drive's root keeps
    /// its own). A name that is not a full path on a drive letter, or that Windows cannot make whole, comes back as
    /// it is. Pure.</summary>
    public static string NormalWindowsFolder(string? dir)
    {
        string d = (dir ?? "").Trim();
        if (!IsWindowsDrivePath(d)) return d;
        try
        {
            string full = Path.GetFullPath(d);
            string root = Path.GetPathRoot(full) ?? "";
            return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return d;
        }
    }

    /// <summary>On a Windows plan, put every folder the FORM names (a version to download or that is there — never a
    /// folder the target stores, which is the agent's own) in the spelling the agent answers with
    /// (<see cref="NormalWindowsFolder"/>): the folder written into the configuration and the one a follow-up step
    /// names are then the agent's, and a re-point that went where it was asked compares equal. Returns the versions
    /// whose folder changed.</summary>
    public IReadOnlyList<string> NormalizeWindowsFolders()
    {
        var changed = new List<string>();
        if (!IsWindows) return changed;
        foreach (var v in Versions)
        {
            if (FolderIsStored(v) || ChoiceFor(v) is not (StackChoice.Have or StackChoice.Download)) continue;
            string dir = DirFor(v), normal = NormalWindowsFolder(dir);
            if (normal.Length == 0 || string.Equals(normal, dir, StringComparison.Ordinal)) continue;
            // An explicit choice stays explicit: SetDir changes the folder alone.
            var choice = ChoiceFor(v);
            SetDir(v, normal);
            if (ChoiceFor(v) != choice) SetChoice(v, choice);
            changed.Add(v);
        }
        return changed;
    }

    /// <summary>An address written in full, the way the agent's own validator reads it. NOT bare
    /// <see cref="IPAddress.TryParse"/>: .NET still honours the classic shorthand, so a mistyped
    /// port ("4206") parses as 0.0.16.110 and a truncated quad ("192.168.1") as 192.168.0.1 — both
    /// would be written into GIO_ADVERTISED_IP and handed verbatim to every player (the agent does
    /// no validation of its own), and under <c>--yes</c> install_agent.sh, which validates with
    /// Python's strict <c>ipaddress.ip_address</c>, would abort the install on a value this form had
    /// already accepted. Round-tripping the parse is what rejects the shorthand while leaving real
    /// IPv4 and IPv6 literals (including their canonical lowercase spelling) alone.</summary>
    private static bool IsPlainIp(string value)
    {
        string s = (value ?? "").Trim();
        return IPAddress.TryParse(s, out var ip)
               && string.Equals(ip.ToString(), s, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Regex(string pattern, string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value ?? "", pattern);

    /// <summary>Check that the folder of each version whose package the admin says is there
    /// (<see cref="StackChoice.Have"/>) looks like an extracted vendor stack. The check delegate answers
    /// "does this entry exist" for the target (Directory/File.Exists locally, the root probe over SSH).
    /// Returns missing entries per version. Every other row is skipped: a download's folder is EXPECTED to
    /// be absent or empty — the agent fills it after the install —, a version set up later names no
    /// folder, and a kept folder is the target's own.</summary>
    public IReadOnlyList<string> MissingStackEntries(Func<string, bool> exists)
    {
        var missing = new List<string>();
        foreach (var label in Versions)
        {
            string dir = DirFor(label);
            if (string.IsNullOrWhiteSpace(dir) || ChoiceFor(label) != StackChoice.Have) continue;
            if (!exists(dir)) { missing.Add($"{label}: {dir}"); continue; }
            foreach (var e in RequiredStackEntries)
                if (!exists(Join(dir, e))) missing.Add($"{label}: {e}");
        }
        return missing;
    }

    /// <summary>The agent config as KEY=VALUE lines (what <c>/etc/gio-agent/config</c> and
    /// <c>%LOCALAPPDATA%\Relic\agent\config</c> hold). Values are written verbatim — no quoting — so
    /// they must not contain newlines; callers validate first.</summary>
    public string RenderConfig()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# Relic GIO agent configuration (KEY=VALUE). Written by Relic; change it from the launcher's Agent settings card, or edit it and restart the agent.\n");
        // Only GIO_SERVER_NAME is scrubbed of quotes: systemd's EnvironmentFile parser treats ' and "
        // as quoting that SPANS LINES, so one apostrophe in that free-text field would swallow every
        // assignment written after it. It must NOT be done to every value: silently rewriting the token
        // or a stack path here would make the box disagree with server.json and with what the admin is
        // shown — Validate() refuses those instead, so the admin is told.
        void Put(string k, string v) => sb.Append(k).Append('=')
            .Append((v ?? "").Replace("\r", "").Replace("\n", " ").Trim()).Append('\n');
        void PutText(string k, string v) => Put(k, (v ?? "").Replace("\"", "").Replace("'", ""));
        // Omitted entirely when blank, so install_agent.sh keeps the box's stored value.
        void PutIfSet(string k, string v) { if (!string.IsNullOrWhiteSpace(v)) Put(k, v); }
        // One folder line per version of the plan, SET AND EMPTY for a version set up later or without a
        // folder: the installer reads the empty value as "not on this box", and on Windows a key this text
        // names is the form's (LocalAgent.MergeConfig). A folder the target already stores is in the row by
        // then (KeepStoredFolders): a re-install writes it back as it was.
        foreach (var v in Versions) Put(ServerVersions.DirKey(v), ChoiceFor(v) == StackChoice.Later ? "" : DirFor(v));
        Put("GIO_AGENT_LISTEN", Listen);
        Put("GIO_AGENT_TOKEN", Token);
        PutText("GIO_SERVER_NAME", ServerName);
        // With "Keep .env settings" on, the bind IP and — further down — the three configured secrets are
        // left out ENTIRELY, not written empty: the agent applies none of them while the option is on, and
        // a key this text does not name is one the box keeps on both targets (install_agent.sh for a key its
        // environment lacks, LocalAgent.MergeConfig for a key the form does not render).
        if (SendsStackOverrides) Put("GIO_BIND_IP", BindIp);
        Put("GIO_ADVERTISED_IP", AdvertisedIp);
        Put("GIO_ADVERTISED_HOST", AdvertisedHost);
        Put("GIO_MUIP_HOST", MuipHost);
        Put("GIO_AGENT_REGION", Region);
        if (SendsStackOverrides)
        {
            // SET AND EMPTY like the MUIP host: install_agent.sh treats an empty value as "not set" and
            // keeps the key the box already stores, so a blank form field never wipes a key chosen at
            // first install.
            Put("GIO_MUIP_KEY", MuipKey);
            // The two stack secrets are OMITTED when blank, unlike the MUIP key above: blank must mean
            // "keep what the box has" on BOTH targets. install_agent.sh keeps its stored value for an
            // unset key exactly as for an empty one, but on Windows LocalAgent.MergeConfig keeps the old
            // line only of a key this text does NOT name — a rendered key, even empty, replaces it (which
            // is what a blank GIO_MUIP_KEY does to a stored sign key there). Rendered blank, a re-install
            // would drop the chosen value from the config, and the next stack still on the vendor's
            // default would get a random one.
            PutIfSet("GIO_MYSQL_ROOT_PASSWORD", MysqlPassword);
            PutIfSet("GIO_FLASK_SECRET_KEY", FlaskKey);
        }
        // Stated = one line, 1 or 0 and never empty: install_agent.sh lets only a NON-EMPTY value replace
        // what the box stores, and an "off" has to be able to replace a stored "on". Not stated = no line,
        // so the box keeps its own value.
        if (KeepEnv is { } keepEnv) Put("GIO_KEEP_ENV", keepEnv ? "1" : "0");
        // The extractor of the agent on this PC: written only when named, so a re-install keeps the stored line.
        if (IsWindows) PutIfSet("GIO_7Z", SevenZip);
        PutIfSet("GIO_PROVISION_MODE", ProvisionMode);
        PutIfSet("GIO_TXT_FIXES_MODE", TxtFixesMode);
        return sb.ToString();
    }

    /// <summary>Environment assignments for <c>install_agent.sh --yes</c> (the script reads the same keys),
    /// plus the installer-only choice of EVERY version of the plan, <c>GIO_STACK_&lt;nn&gt;</c> =
    /// download | have | later | keep (<see cref="ChoiceFor"/>) — read by the installer like
    /// <c>GIO_AGENT_START</c>, never written into the agent config. Never a <c>GIO_FETCH_&lt;nn&gt;</c>: that
    /// is the hand-run key, and the launcher always uploads the installer that reads the choice.</summary>
    public IReadOnlyDictionary<string, string> ToEnvironment()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in RenderConfig().Split('\n'))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            d[line[..eq]] = line[(eq + 1)..];
        }
        foreach (var v in Versions) d[ServerVersions.StackKey(v)] = StackChoices.Token(ChoiceFor(v));
        return d;
    }

    public static string NewToken() => ServerConfigStore.NewToken();
}
