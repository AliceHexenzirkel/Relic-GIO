using System.Net;
using Relic.Core.Util;

namespace Relic.Core.Server;

/// <summary>
/// The parameters of an agent installation — identical for both targets ("a Linux box over SSH" and
/// "this Windows PC"): where the two vendor stacks live (already extracted by the admin, OR marked
/// for download — the agent fetches the ready-made package from the Internet Archive into that
/// folder once it runs), which local IP docker binds on, which IP is advertised to clients, where the
/// agent listens, its bearer token, MUIP host/region/sign key, the two stack secrets the agent sets on a
/// stack still on the vendor's published ones (MySQL root password, Flask secret key), whether the agent
/// leaves each stack's own <c>.env</c> as it is instead (<see cref="KeepEnv"/>) and the server name
/// shown to players.
/// Rendered into the KEY=VALUE config both <c>install_agent.sh</c> (as env) and
/// <c>gio_agent.py --config</c> read; the download marks ride in the installer environment only.
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
    public string Dir16 { get; set; } = "";
    public string Dir28 { get; set; } = "";
    /// <summary>The 1.6 stack is NOT on the box yet: <see cref="Dir16"/> is the folder the agent should
    /// download and extract the ready-made package into. Installer-only, like
    /// <see cref="UpgradeForce"/>: it becomes <c>GIO_FETCH_16=y</c> in <see cref="ToEnvironment"/> and never
    /// reaches the agent config — an upgrade run would otherwise re-mark a version that is long present.</summary>
    public bool Fetch16 { get; set; }
    /// <summary>Same as <see cref="Fetch16"/> for the 2.8 stack (<c>GIO_FETCH_28=y</c>).</summary>
    public bool Fetch28 { get; set; }
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

    /// <summary>The version labels marked for download AND given a folder ("1.6", "2.8" subset, in that
    /// order). A mark without a folder is a validation error, not a download — it is left out here so the
    /// installer environment and the log never name a version the agent could not place anywhere.</summary>
    public IReadOnlyList<string> FetchVersions()
    {
        var list = new List<string>(2);
        if (Fetch16 && !string.IsNullOrWhiteSpace(Dir16)) list.Add("1.6");
        if (Fetch28 && !string.IsNullOrWhiteSpace(Dir28)) list.Add("2.8");
        return list;
    }

    /// <summary>The folder configured for a version label, or "" for an unknown one.</summary>
    public string DirFor(string version) => version == "1.6" ? Dir16 : version == "2.8" ? Dir28 : "";

    /// <summary>The versions that will REALLY be downloaded, given a probe that answers "does this path
    /// exist on the target". <c>install_agent.sh</c> applies the same rule and quietly unmarks a version
    /// whose folder already holds <c>docker-compose.yml.tmpl</c> ("already holds a server stack -- the
    /// download will be skipped"). Without this the launcher would queue that version anyway, the agent
    /// would refuse it with 409 "already on this server", and the queue behind it would be dropped with a
    /// failure toast for a download that was never needed.</summary>
    public IReadOnlyList<string> FetchVersionsReally(Func<string, bool> exists)
    {
        var list = new List<string>(2);
        foreach (var v in FetchVersions())
        {
            string dir = DirFor(v);
            string probe = IsWindows ? Path.Combine(dir, "docker-compose.yml.tmpl")
                                     : dir.TrimEnd('/') + "/docker-compose.yml.tmpl";
            if (!exists(probe)) list.Add(v);
        }
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

    /// <summary>Structural validation (no I/O). Returns user-facing problems; empty = OK.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(Dir16) && string.IsNullOrWhiteSpace(Dir28))
            errs.Add(L.T("core.deploy.noStacks"));
        foreach (var (label, dir, fetch) in new[] { ("1.6", Dir16, Fetch16), ("2.8", Dir28, Fetch28) })
        {
            // "Download it" with nowhere to put it: the toggle was switched off but the folder left blank.
            if (fetch && string.IsNullOrWhiteSpace(dir)) errs.Add(L.T("core.deploy.fetchNeedsDir", new { version = label }));
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (IsWindows ? !Path.IsPathRooted(dir) : !dir.StartsWith('/'))
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
        return errs;
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

    /// <summary>Check that each configured stack dir looks like an extracted vendor stack. The check
    /// delegate answers "does this entry exist" for the target (Directory/File.Exists locally,
    /// <c>test -e</c> over SSH). Returns missing entries per version. A version marked for download is
    /// skipped: its folder is EXPECTED to be absent or empty — the agent fills it after the install.</summary>
    public IReadOnlyList<string> MissingStackEntries(Func<string, bool> exists)
    {
        var missing = new List<string>();
        foreach (var (label, dir, fetch) in new[] { ("1.6", Dir16, Fetch16), ("2.8", Dir28, Fetch28) })
        {
            if (string.IsNullOrWhiteSpace(dir) || fetch) continue;
            if (!exists(dir)) { missing.Add($"{label}: {dir}"); continue; }
            foreach (var e in RequiredStackEntries)
            {
                string p = IsWindows ? Path.Combine(dir, e) : dir.TrimEnd('/') + "/" + e;
                if (!exists(p)) missing.Add($"{label}: {e}");
            }
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
        Put("GIO_DIR_16", Dir16);
        Put("GIO_DIR_28", Dir28);
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
        PutIfSet("GIO_PROVISION_MODE", ProvisionMode);
        PutIfSet("GIO_TXT_FIXES_MODE", TxtFixesMode);
        return sb.ToString();
    }

    /// <summary>Environment assignments for <c>install_agent.sh --yes</c> (the script reads the same keys),
    /// plus the installer-only download marks <c>GIO_FETCH_16=y</c> / <c>GIO_FETCH_28=y</c> — read by the
    /// installer like <c>GIO_AGENT_START</c>, never written into the agent config (see <see cref="Fetch16"/>).</summary>
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
        foreach (var v in FetchVersions()) d[v == "1.6" ? "GIO_FETCH_16" : "GIO_FETCH_28"] = "y";
        return d;
    }

    public static string NewToken() => ServerConfigStore.NewToken();
}
