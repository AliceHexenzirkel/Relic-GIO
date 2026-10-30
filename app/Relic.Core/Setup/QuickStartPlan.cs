using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Relic.Core.Install;
using Relic.Core.Server;
using Relic.Core.Util;

namespace Relic.Core.Setup;

/// <summary>
/// Quick Start's plan: what one run was asked to set up — the game versions, the server, the account — and the
/// state each of its steps was last seen in. The page writes it through the rpc <c>quickstart.save</c>
/// (<see cref="QuickStartStore.Save"/>) at Start, at every change of a step's state, at Stop and at the end, so a
/// restart of Windows or of the launcher resumes the run.
/// <para>The model has NO field for a secret: no SSH password, key passphrase, sudo password, agent token or account
/// password can be stored, because reading a document into the model drops every key it does not name.</para>
/// <para>The states of its steps are hints about what was started, never the truth: the runner judges every step
/// again from the facts at each look.</para>
/// <para>The JSON names are the camelCase of the properties (<c>installRoot</c>, <c>fetchedByRun</c>); the keys of
/// <see cref="QuickStartAccount.Create"/> and <see cref="Steps"/> are version and step ids, kept as written.</para>
/// </summary>
public sealed class QuickStartPlan
{
    /// <summary>8 lower-case hex characters; a plan without one gets a fresh one when it is normalised.</summary>
    public string Id { get; set; } = "";
    /// <summary>One of <see cref="States"/>.</summary>
    public string State { get; set; } = States.Running;
    /// <summary>Express (the usual settings) or Custom (every setting shown).</summary>
    public bool Express { get; set; } = true;
    /// <summary>When the run started (ISO 8601, UTC).</summary>
    public string StartedAt { get; set; } = "";
    /// <summary>When the plan was last stored (ISO 8601, UTC) — set by <see cref="QuickStartStore.Save"/>.</summary>
    public string UpdatedAt { get; set; } = "";
    public QuickStartGame Game { get; set; } = new();
    public QuickStartServer Server { get; set; } = new();
    public QuickStartAccount Account { get; set; } = new();
    /// <summary>Per step id (<c>g.host</c>, <c>s.2.8.prepare</c>, …) the state the run last recorded.</summary>
    public Dictionary<string, QuickStartStep> Steps { get; set; } = new();

    // ── the words of the plan ──

    /// <summary>The run: going on, waiting for the restart that finishes turning on WSL 2, stopped by the user, or
    /// ended. A plan that is <see cref="Done"/> never stays on disk.</summary>
    public static class States
    {
        public const string Running = "running", WaitingRestart = "waitingRestart", Paused = "paused", Done = "done";
        public static readonly string[] All = { Running, WaitingRestart, Paused, Done };
    }

    /// <summary>Where a chosen game version comes from: downloaded, a copy already on this PC, or the Library.</summary>
    public static class GameSources
    {
        public const string Download = "download", Local = "local", Installed = "installed";
        public static readonly string[] All = { Download, Local, Installed };
    }

    /// <summary>The server the run uses: one that exists already, one it sets up on this PC, or on a Linux box.</summary>
    public static class Kinds
    {
        public const string Existing = "existing", Windows = "windows", Linux = "linux";
        public static readonly string[] All = { Existing, Windows, Linux };
    }

    /// <summary>Who plays on the server: this PC only, the local network, the Internet (a Windows server), or its
    /// public address (a Linux box). A Windows server is <see cref="Local"/>, <see cref="Lan"/> or
    /// <see cref="Internet"/>; a Linux box <see cref="Lan"/> or <see cref="Public"/>.</summary>
    public static class Reaches
    {
        public const string Local = "local", Lan = "lan", Internet = "internet", Public = "public";
        public static readonly string[] All = { Local, Lan, Internet, Public };
        public static readonly string[] Windows = { Local, Lan, Internet };
        public static readonly string[] Linux = { Lan, Public };
    }

    /// <summary>What the run does with a server version: download its package, use one already there, keep the
    /// agent's folder, add it to an agent already set up, or nothing more than start it.</summary>
    public static class ServerChoices
    {
        public const string Download = "download", Have = "have", Keep = "keep", Add = "add", Ready = "ready";
        public static readonly string[] All = { Download, Have, Keep, Add, Ready };
    }

    /// <summary>A stack's starting progress (the agent's three progress modes).</summary>
    public static class ProgressModes
    {
        public const string Default = "default", Keep = "keep", Fixes = "fixes";
        public static readonly string[] All = { Default, Keep, Fixes };
    }

    /// <summary>The state of one step.</summary>
    public static class StepStates
    {
        public const string Pending = "pending", Running = "running", Done = "done", Skipped = "skipped",
            Needs = "needs", Failed = "failed", Paused = "paused";
        public static readonly string[] All = { Pending, Running, Done, Skipped, Needs, Failed, Paused };
    }

    // ── limits ──

    /// <summary>The largest plan: its JSON is smaller than this many bytes.</summary>
    public const int MaxBytes = 64 * 1024;
    /// <summary>The most entries of one version list (the game's, the server's, the accounts').</summary>
    public const int MaxVersions = 10;
    /// <summary>The most steps a plan records.</summary>
    public const int MaxSteps = 256;
    /// <summary>The longest path.</summary>
    public const int MaxPath = 4096;
    /// <summary>The longest host name.</summary>
    public const int MaxHost = 255;
    /// <summary>A step's note is cut to this many characters.</summary>
    public const int MaxNote = 1000;
    /// <summary>The largest memory limit for WSL the plan may name, in GB.</summary>
    public const int MaxWslMemoryGb = 1024;

    /// <summary>The prefixes an account name may not start with (the agent's <c>RESERVED_PREFIXES</c>).</summary>
    public static readonly string[] ReservedNamePrefixes = { "relic", "tpl", "admin", "gm" };

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    internal static readonly JsonSerializerOptions JsonIndented = new(Json) { WriteIndented = true };

    /// <summary>Reads a plan document into the model: every key the model does not name is dropped, a secret
    /// among them. Throws <see cref="QuickStartPlanException"/> for a document of <see cref="MaxBytes"/> bytes or
    /// more, for one that is not JSON, and for one whose values are not of the model's types. Neither normalised
    /// nor checked — <see cref="QuickStartStore.Save"/> does both.</summary>
    public static QuickStartPlan FromJson(string? json)
    {
        string text = json ?? "";
        if (Encoding.UTF8.GetByteCount(text) >= MaxBytes) throw new QuickStartPlanException("$");
        try
        {
            return JsonSerializer.Deserialize<QuickStartPlan>(text, Json) ?? throw new QuickStartPlanException("$");
        }
        catch (JsonException ex)
        {
            throw new QuickStartPlanException(string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path);
        }
    }

    /// <summary>The plan as JSON: indented for the file, compact for the size check.</summary>
    public string ToJson(bool indented = true) => JsonSerializer.Serialize(this, indented ? JsonIndented : Json);

    /// <summary>
    /// Puts the plan in its stored form, in place: a missing part made empty, the words in their own spelling
    /// (matched without letter case), the texts trimmed, the voice languages in their catalogue spelling once each,
    /// a step's note with the value of every secret assignment it may quote blanked (<c>GIO_AGENT_TOKEN=…</c> and the
    /// other secret keys of an installer's output — <see cref="AgentDeployer.Redact"/>) and cut to <see cref="MaxNote"/>
    /// characters, a fresh <see cref="Id"/> and <see cref="StartedAt"/> where there is none. A value that is not one of
    /// the plan's words stays as it is for <see cref="Problems"/> to name. Idempotent.
    /// </summary>
    public QuickStartPlan Normalize()
    {
        Id = (Id ?? "").Trim().ToLowerInvariant();
        if (Id.Length == 0) Id = NewId();
        State = Word(State, States.All);
        StartedAt = Stamp(StartedAt) ?? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        UpdatedAt = Stamp(UpdatedAt) ?? "";

        Game ??= new QuickStartGame();
        Game.InstallRoot = Trim(Game.InstallRoot);
        Game.Source = Trim(Game.Source);
        Game.Versions ??= new List<QuickStartGameVersion>();
        foreach (var v in Game.Versions)
        {
            if (v is null) continue;
            v.Id = Trim(v.Id);
            v.From = Word(v.From, GameSources.All);
            v.LocalPath = Trim(v.LocalPath);
            v.ReplaceInstalledAt = Trim(v.ReplaceInstalledAt);
            var voices = new List<string>();
            foreach (string? raw in v.Voices ?? new List<string>())
            {
                string lang = VoiceLanguages.Canonical(raw) ?? Trim(raw);
                if (!voices.Contains(lang, StringComparer.Ordinal)) voices.Add(lang);
            }
            v.Voices = voices;
        }

        Server ??= new QuickStartServer();
        Server.Kind = Word(Server.Kind, Kinds.All);
        Server.Address = Trim(Server.Address);
        Server.Reach = Word(Server.Reach, Reaches.All);
        Server.BindIp = Trim(Server.BindIp);
        Server.AdvertisedIp = Trim(Server.AdvertisedIp);
        Server.Listen = Trim(Server.Listen);
        Server.Ssh ??= new QuickStartSsh();
        Server.Ssh.Host = Trim(Server.Ssh.Host);
        Server.Ssh.User = Trim(Server.Ssh.User);
        Server.Ssh.KeyPath = Trim(Server.Ssh.KeyPath);
        Server.Ssh.HostKey ??= new QuickStartHostKey();
        Server.Ssh.HostKey.Type = Trim(Server.Ssh.HostKey.Type);
        Server.Ssh.HostKey.Fingerprint = Trim(Server.Ssh.HostKey.Fingerprint);
        Server.Versions ??= new List<QuickStartServerVersion>();
        foreach (var v in Server.Versions)
        {
            if (v is null) continue;
            v.Id = Trim(v.Id);
            v.Choice = Word(v.Choice, ServerChoices.All);
            v.Dir = Trim(v.Dir);
            v.Progress = Word(v.Progress, ProgressModes.All);
        }
        Server.Installs ??= new QuickStartInstalls();

        Account ??= new QuickStartAccount();
        Account.Name = Trim(Account.Name);
        Account.Create = Rekey(Account.Create);
        foreach (var c in Account.Create.Values)
            if (c is not null) c.Template = Trim(c.Template);

        Steps = Rekey(Steps);
        foreach (var s in Steps.Values)
        {
            if (s is null) continue;
            s.State = Word(s.State, StepStates.All);
            // A note is the page's text — a failure's message among them —, never a place for a secret: the value of
            // a secret key's assignment an installer's line quotes is blanked before the plan is stored.
            s.Note = AgentDeployer.Redact(s.Note ?? "");
            if (s.Note.Length > MaxNote) s.Note = s.Note[..MaxNote];
        }
        return this;
    }

    /// <summary>
    /// What is wrong with the plan, as JSON paths (<c>$.server.kind</c>, <c>$.game.versions[0].id</c>,
    /// <c>$.account.create['2.8'].template</c>) — empty when the plan can be used. Run it on a normalised plan
    /// (<see cref="Normalize"/>). <paramref name="gameIds"/>: the game versions the catalogue offers
    /// (<see cref="VersionCatalog.Load"/> when null). The rules: the words of the plan; game ids from the catalogue,
    /// server ids from <see cref="ServerVersions.All"/>, voices from <see cref="VoiceLanguages.All"/>; paths of at most
    /// <see cref="MaxPath"/> characters and hosts of at most <see cref="MaxHost"/>, without a control character; the
    /// bind and advertised addresses an IPv4 literal or empty, the listen address an IPv4 literal and a port; ports
    /// 0–65535, the SSH port 1–65535; an account name that is empty or one the agent takes; step ids of letters,
    /// digits, dots and dashes; at most <see cref="MaxVersions"/> entries per version list.
    /// </summary>
    public List<string> Problems(IReadOnlyCollection<string>? gameIds = null)
    {
        var bad = new List<string>();
        var games = new HashSet<string>(gameIds ?? CatalogueIds(), StringComparer.OrdinalIgnoreCase);

        if (!IdRe.IsMatch(Id ?? "")) bad.Add("$.id");
        if (!States.All.Contains(State, StringComparer.Ordinal)) bad.Add("$.state");
        if (Stamp(StartedAt) is null) bad.Add("$.startedAt");
        if (!string.IsNullOrEmpty(UpdatedAt) && Stamp(UpdatedAt) is null) bad.Add("$.updatedAt");

        var g = Game;
        if (g is null) bad.Add("$.game");
        else
        {
            if (!PathOk(g.InstallRoot)) bad.Add("$.game.installRoot");
            if (!SourceRe.IsMatch(g.Source ?? "")) bad.Add("$.game.source");
            var versions = g.Versions ?? new List<QuickStartGameVersion>();
            if (versions.Count > MaxVersions) { bad.Add("$.game.versions"); versions = new List<QuickStartGameVersion>(); }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < versions.Count; i++)
            {
                string at = $"$.game.versions[{i}]";
                var v = versions[i];
                if (v is null) { bad.Add(at); continue; }
                if (!games.Contains(v.Id ?? "") || !seen.Add(v.Id ?? "")) bad.Add(at + ".id");
                if (!GameSources.All.Contains(v.From, StringComparer.Ordinal)) bad.Add(at + ".from");
                var voices = v.Voices ?? new List<string>();
                if (voices.Count > MaxVersions) bad.Add(at + ".voices");
                else
                    for (int j = 0; j < voices.Count; j++)
                        if (!VoiceLanguages.All.Contains(voices[j], StringComparer.Ordinal)) bad.Add($"{at}.voices[{j}]");
                if (!PathOk(v.LocalPath)) bad.Add(at + ".localPath");
                if (!PathOk(v.ReplaceInstalledAt)) bad.Add(at + ".replaceInstalledAt");
            }
        }

        var s = Server;
        if (s is null) bad.Add("$.server");
        else
        {
            if (!Kinds.All.Contains(s.Kind, StringComparer.Ordinal)) bad.Add("$.server.kind");
            string address = s.Address ?? "";
            if (address.Length > 0 && (address.Any(char.IsControl)
                    || !ServerAddress.TryParse(address, out var parsed, out _) || parsed is null || parsed.Host.Length > MaxHost))
                bad.Add("$.server.address");
            if (s.AgentPort is < 0 or > 65535) bad.Add("$.server.agentPort");
            string[] reaches = s.Kind switch
            {
                Kinds.Windows => Reaches.Windows,
                Kinds.Linux => Reaches.Linux,
                _ => Reaches.All,
            };
            if (!reaches.Contains(s.Reach, StringComparer.Ordinal)) bad.Add("$.server.reach");
            if (!(s.BindIp is "" || IsIpv4(s.BindIp))) bad.Add("$.server.bindIp");
            if (!(s.AdvertisedIp is "" || IsIpv4(s.AdvertisedIp))) bad.Add("$.server.advertisedIp");
            if (!(s.Listen is "" || IsListen(s.Listen))) bad.Add("$.server.listen");

            var ssh = s.Ssh;
            if (ssh is null) bad.Add("$.server.ssh");
            else
            {
                if (!(ssh.Host is "" || IsHost(ssh.Host))) bad.Add("$.server.ssh.host");
                if (ssh.Port is < 1 or > 65535) bad.Add("$.server.ssh.port");
                if ((ssh.User ?? "").Length > MaxHost || (ssh.User ?? "").Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
                    bad.Add("$.server.ssh.user");
                if (!PathOk(ssh.KeyPath)) bad.Add("$.server.ssh.keyPath");
                if (ssh.HostKey is null) bad.Add("$.server.ssh.hostKey");
                else
                {
                    if (!KeyTypeRe.IsMatch(ssh.HostKey.Type ?? "")) bad.Add("$.server.ssh.hostKey.type");
                    if (!(ssh.HostKey.Fingerprint is "" || FingerprintRe.IsMatch(ssh.HostKey.Fingerprint ?? "")))
                        bad.Add("$.server.ssh.hostKey.fingerprint");
                }
            }

            var versions = s.Versions ?? new List<QuickStartServerVersion>();
            if (versions.Count > MaxVersions) { bad.Add("$.server.versions"); versions = new List<QuickStartServerVersion>(); }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < versions.Count; i++)
            {
                string at = $"$.server.versions[{i}]";
                var v = versions[i];
                if (v is null) { bad.Add(at); continue; }
                if (!ServerVersions.All.Contains(v.Id, StringComparer.Ordinal) || !seen.Add(v.Id ?? "")) bad.Add(at + ".id");
                if (!ServerChoices.All.Contains(v.Choice, StringComparer.Ordinal)) bad.Add(at + ".choice");
                if (!PathOk(v.Dir)) bad.Add(at + ".dir");
                if (!ProgressModes.All.Contains(v.Progress, StringComparer.Ordinal)) bad.Add(at + ".progress");
            }

            if (s.Installs is null) bad.Add("$.server.installs");
            else if (s.Installs.WslMemoryGb is < 0 or > MaxWslMemoryGb) bad.Add("$.server.installs.wslMemoryGb");
        }

        var a = Account;
        if (a is null) bad.Add("$.account");
        else
        {
            if (!(a.Name is "" || AccountNameOk(a.Name))) bad.Add("$.account.name");
            var create = a.Create ?? new Dictionary<string, QuickStartAccountCreate>();
            if (create.Count > MaxVersions) bad.Add("$.account.create");
            else
                foreach (var (key, c) in create)
                {
                    bool known = ServerVersions.All.Contains(key, StringComparer.Ordinal) || games.Contains(key);
                    if (!known) { bad.Add("$.account.create"); continue; }
                    if (c is null || !TemplateRe.IsMatch(c.Template ?? "")) bad.Add($"$.account.create['{key}'].template");
                }
        }

        var steps = Steps ?? new Dictionary<string, QuickStartStep>();
        if (steps.Count > MaxSteps) bad.Add("$.steps");
        else
            foreach (var (key, step) in steps)
            {
                if (!StepIdRe.IsMatch(key)) { bad.Add("$.steps"); continue; }
                if (step is null || !StepStates.All.Contains(step.State, StringComparer.Ordinal)) bad.Add($"$.steps['{key}'].state");
            }
        return bad;
    }

    /// <summary>A name the agent takes for a new account: 3–20 letters, digits, '_', '.' or '-', starting with a
    /// letter or digit, not digits alone, and not starting with a reserved prefix (<see cref="ReservedNamePrefixes"/>,
    /// without letter case). Whether it is the version's pre-made login is the page's question.</summary>
    public static bool AccountNameOk(string? name)
    {
        string n = name ?? "";
        if (!AccountNameRe.IsMatch(n) || n.All(char.IsAsciiDigit)) return false;
        return !ReservedNamePrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    // ── helpers ──

    private static readonly Regex IdRe = new(@"^[0-9a-f]{8}\z", RegexOptions.CultureInvariant);
    private static readonly Regex SourceRe = new(@"^[A-Za-z0-9._-]{0,64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex KeyTypeRe = new(@"^[A-Za-z0-9@._+-]{0,64}\z", RegexOptions.CultureInvariant);
    // OpenSSH's form: "SHA256:" and the unpadded base64 of the 32-byte digest.
    private static readonly Regex FingerprintRe = new(@"^SHA256:[A-Za-z0-9+/]{43}\z", RegexOptions.CultureInvariant);
    private static readonly Regex TemplateRe = new(@"^[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant);
    // The ids of the steps of the runner (g.host, s.2.8.prepare, s.agentUpdate): letters, digits, dots and dashes.
    private static readonly Regex StepIdRe = new(@"^[A-Za-z0-9.\-]{1,64}\z", RegexOptions.CultureInvariant);
    // The agent's NAME_RE.
    private static readonly Regex AccountNameRe = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]{2,19}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4Re = new(@"^(0|[1-9][0-9]{0,2})(\.(0|[1-9][0-9]{0,2})){3}\z", RegexOptions.CultureInvariant);

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private static string Trim(string? s) => (s ?? "").Trim();

    /// <summary>The word of <paramref name="words"/> that <paramref name="value"/> spells without letter case, or the
    /// value trimmed when it is none of them.</summary>
    private static string Word(string? value, string[] words)
    {
        string v = Trim(value);
        return words.FirstOrDefault(w => string.Equals(w, v, StringComparison.OrdinalIgnoreCase)) ?? v;
    }

    /// <summary>An ISO 8601 time in its round-trip UTC form, or null when <paramref name="value"/> is none.</summary>
    private static string? Stamp(string? value)
    {
        string v = Trim(value);
        if (v.Length == 0 || v.Length > 64) return null;
        return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>The map with its keys trimmed (the first of two keys that trim alike wins).</summary>
    private static Dictionary<string, T> Rekey<T>(Dictionary<string, T>? map)
    {
        var clean = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (key, value) in map ?? new Dictionary<string, T>())
            clean.TryAdd(Trim(key), value);
        return clean;
    }

    private static bool PathOk(string? path) => (path ?? "").Length <= MaxPath && !(path ?? "").Any(char.IsControl);

    private static bool IsIpv4(string? s) =>
        Ipv4Re.IsMatch(s ?? "") && s!.Split('.').All(o => int.Parse(o, CultureInfo.InvariantCulture) <= 255);

    private static bool IsListen(string s)
    {
        int colon = s.LastIndexOf(':');
        return colon > 0 && IsIpv4(s[..colon])
            && int.TryParse(s[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535;
    }

    private static bool IsHost(string s)
    {
        if (s.Length > MaxHost || s.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return false;
        return Uri.CheckHostName(s.TrimEnd('.')) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    private static IEnumerable<string> CatalogueIds()
    {
        try { return VersionCatalog.Load().Select(v => v.Id).ToList(); }
        catch (Exception ex)
        {
            Log.Error("quick start: the version catalogue could not be read for the plan's check", ex);
            return Array.Empty<string>();
        }
    }
}

/// <summary>The game half of a plan: where the games go, the settings the installs use, and the versions.</summary>
public sealed class QuickStartGame
{
    public string InstallRoot { get; set; } = "";
    public bool Enhancements { get; set; } = true;
    public bool FiddlerDecrypt { get; set; } = true;
    public bool InstallCert { get; set; } = true;
    public bool CreateShortcut { get; set; } = true;
    /// <summary>The preferred download host: "cdn", "drive" or a mirror id (<c>RelicSettings.Source</c>).</summary>
    public string Source { get; set; } = "cdn";
    public List<QuickStartGameVersion> Versions { get; set; } = new();
}

/// <summary>One chosen game version.</summary>
public sealed class QuickStartGameVersion
{
    /// <summary>A version id of the catalogue.</summary>
    public string Id { get; set; } = "";
    /// <summary>One of <see cref="QuickStartPlan.GameSources"/>.</summary>
    public string From { get; set; } = QuickStartPlan.GameSources.Download;
    /// <summary>The voice languages a download installs, in <see cref="VoiceLanguages.All"/> spelling.</summary>
    public List<string> Voices { get; set; } = new();
    /// <summary>The player's copy (its folder or its GenshinImpact.exe) when <see cref="From"/> is local.</summary>
    public string LocalPath { get; set; } = "";
    /// <summary>The folder the version is installed at that the copy takes the place of (the import's own rule).</summary>
    public string ReplaceInstalledAt { get; set; } = "";
}

/// <summary>The server half of a plan.</summary>
public sealed class QuickStartServer
{
    /// <summary>One of <see cref="QuickStartPlan.Kinds"/>.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The address of an existing server, as typed.</summary>
    public string Address { get; set; } = "";
    public int AgentPort { get; set; } = ServerAddress.DefaultAgentPort;
    /// <summary>The user is the existing server's admin (the token itself is never in the plan).</summary>
    public bool Admin { get; set; }
    /// <summary>One of <see cref="QuickStartPlan.Reaches"/>, of the kind's set.</summary>
    public string Reach { get; set; } = QuickStartPlan.Reaches.Local;
    /// <summary>An IPv4 literal, or "" (the agent's own choice).</summary>
    public string BindIp { get; set; } = "";
    /// <summary>An IPv4 literal, or "" (the bind address is advertised).</summary>
    public string AdvertisedIp { get; set; } = "";
    /// <summary>The agent's listen address, <c>&lt;IPv4&gt;:&lt;port&gt;</c>.</summary>
    public string Listen { get; set; } = "127.0.0.1:18080";
    /// <summary>Keep .env settings: on, off, or not stated (null).</summary>
    public bool? KeepEnv { get; set; }
    public bool Firewall { get; set; }
    public bool Autostart { get; set; } = true;
    public QuickStartSsh Ssh { get; set; } = new();
    public List<QuickStartServerVersion> Versions { get; set; } = new();
    public QuickStartInstalls Installs { get; set; } = new();
}

/// <summary>The SSH login of a Linux box — no password, passphrase or sudo password: those stay in page memory.</summary>
public sealed class QuickStartSsh
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "root";
    /// <summary>The path of the key file (never the key, never its passphrase).</summary>
    public string KeyPath { get; set; } = "";
    /// <summary>The host key the user confirmed.</summary>
    public QuickStartHostKey HostKey { get; set; } = new();
}

/// <summary>A host key: its type (for display) and its OpenSSH <c>SHA256:</c> fingerprint — not a secret.</summary>
public sealed class QuickStartHostKey
{
    public string Type { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

/// <summary>One server version of the run.</summary>
public sealed class QuickStartServerVersion
{
    /// <summary>A version id of <see cref="ServerVersions.All"/>.</summary>
    public string Id { get; set; } = "";
    /// <summary>One of <see cref="QuickStartPlan.ServerChoices"/>.</summary>
    public string Choice { get; set; } = QuickStartPlan.ServerChoices.Download;
    public string Dir { get; set; } = "";
    /// <summary>One of <see cref="QuickStartPlan.ProgressModes"/>.</summary>
    public string Progress { get; set; } = QuickStartPlan.ProgressModes.Keep;
    /// <summary>The pathfinding server on, off, or as the agent decides (null).</summary>
    public bool? Pathfinding { get; set; }
    /// <summary>The run downloaded this version's package (its package step ran a fetch).</summary>
    public bool FetchedByRun { get; set; }
}

/// <summary>What the run installs on this PC.</summary>
public sealed class QuickStartInstalls
{
    public bool Python { get; set; }
    public bool SevenZip { get; set; }
    public bool Wsl { get; set; }
    public bool WslUpdate { get; set; }
    public bool Docker { get; set; }
    public bool DockerStart { get; set; }
    /// <summary>The user ticked the acceptance of Docker's subscription service agreement.</summary>
    public bool AcceptLicence { get; set; }
    /// <summary>The memory WSL may use from its next start, in GB; 0 = left as it is.</summary>
    public int WslMemoryGb { get; set; }
    public bool AgentUpdate { get; set; }
}

/// <summary>The account half of a plan: the name, and per version the starting progress of the account to create
/// (a version not named gets none). Never a password.</summary>
public sealed class QuickStartAccount
{
    public string Name { get; set; } = "";
    public Dictionary<string, QuickStartAccountCreate> Create { get; set; } = new();
}

/// <summary>The starting progress of one account (a template id, or <c>fresh</c>).</summary>
public sealed class QuickStartAccountCreate
{
    public string Template { get; set; } = "";
}

/// <summary>What the run last recorded about one step.</summary>
public sealed class QuickStartStep
{
    /// <summary>One of <see cref="QuickStartPlan.StepStates"/>.</summary>
    public string State { get; set; } = QuickStartPlan.StepStates.Pending;
    public string Note { get; set; } = "";
}

/// <summary>A plan that cannot be used: <see cref="Detail"/> is the JSON path of the first problem (<c>$</c> for the
/// document as a whole) — the <c>{detail}</c> of the Backend's refusal text.</summary>
public sealed class QuickStartPlanException : Exception
{
    public QuickStartPlanException(string detail) : base(detail) => Detail = detail;

    public string Detail { get; }
}

/// <summary>
/// The plan file, <c>%LOCALAPPDATA%\Relic\quickstart.json</c> (UTF-8, indented, written to a temporary file and
/// renamed over the old one). Only the rpc <c>quickstart.save</c> writes it (<see cref="Save"/>) and
/// <c>quickstart.clear</c> deletes it (<see cref="Clear"/>); a plan that is done is deleted instead of written, so it
/// never stays on disk. The uninstaller removes it with the rest of the folder.
/// </summary>
public static class QuickStartStore
{
    private static readonly object FileGate = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relic", "quickstart.json");

    /// <summary>The plan on disk, normalised — or null when there is none, when it is done, and when it cannot be
    /// read or used (logged with the JSON path of its first problem). Never throws.</summary>
    public static QuickStartPlan? Load(string? path = null, IReadOnlyCollection<string>? gameIds = null)
    {
        path ??= DefaultPath;
        string text;
        lock (FileGate)
        {
            try
            {
                if (!File.Exists(path)) return null;
                // The file is written indented: allow it room beyond the compact size the plan is held to.
                if (new FileInfo(path).Length > 4L * QuickStartPlan.MaxBytes)
                {
                    Log.Error($"quick start: the plan file is too large to be a plan ({path}) — left unused");
                    return null;
                }
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Error($"quick start: the plan file could not be read ({path})", ex);
                return null;
            }
        }
        try
        {
            QuickStartPlan plan;
            try { plan = JsonSerializer.Deserialize<QuickStartPlan>(text, QuickStartPlan.Json) ?? throw new QuickStartPlanException("$"); }
            catch (JsonException ex) { throw new QuickStartPlanException(string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path); }
            plan.Normalize();
            if (Problem(plan, gameIds) is { } detail) throw new QuickStartPlanException(detail);
            return plan.State == QuickStartPlan.States.Done ? null : plan;
        }
        catch (QuickStartPlanException ex)
        {
            Log.Error($"quick start: the plan file cannot be used ({path}): {ex.Detail} — left unused");
            return null;
        }
    }

    /// <summary>
    /// Normalises and checks <paramref name="plan"/>, stamps <see cref="QuickStartPlan.UpdatedAt"/> and writes it —
    /// or, for a plan that is done, deletes the file. Returns the plan as stored. Throws
    /// <see cref="QuickStartPlanException"/> (nothing written) for a plan that cannot be used, and the I/O exception
    /// of a write that failed (the previous file left as it was).
    /// </summary>
    public static QuickStartPlan Save(QuickStartPlan plan, string? path = null, IReadOnlyCollection<string>? gameIds = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        path ??= DefaultPath;
        plan.Normalize();
        plan.UpdatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        if (Problem(plan, gameIds) is { } detail) throw new QuickStartPlanException(detail);
        lock (FileGate)
        {
            if (plan.State == QuickStartPlan.States.Done)
            {
                DeleteFile(path);
                return plan;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, plan.ToJson(indented: true), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { /* best effort: the next save writes it again */ }
                throw;
            }
        }
        return plan;
    }

    /// <summary>Deletes the plan file. True when there was one.</summary>
    public static bool Clear(string? path = null)
    {
        path ??= DefaultPath;
        lock (FileGate) return DeleteFile(path);
    }

    /// <summary>True while the run of <paramref name="plan"/> is not ended — running, or waiting for the restart of
    /// Windows: the times the resume value is armed (<see cref="State.Startup.ArmQuickStartResume"/>); paused, done and
    /// no plan disarm it.</summary>
    public static bool ResumeWanted(QuickStartPlan? plan) =>
        plan?.State is QuickStartPlan.States.Running or QuickStartPlan.States.WaitingRestart;

    /// <summary>The first problem of a normalised plan, the size included (<c>$</c>: its JSON is
    /// <see cref="QuickStartPlan.MaxBytes"/> bytes or more), or null.</summary>
    private static string? Problem(QuickStartPlan plan, IReadOnlyCollection<string>? gameIds)
    {
        if (plan.Problems(gameIds).FirstOrDefault() is { } first) return first;
        return Encoding.UTF8.GetByteCount(plan.ToJson(indented: false)) >= QuickStartPlan.MaxBytes ? "$" : null;
    }

    private static bool DeleteFile(string path)
    {
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}
