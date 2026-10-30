using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;
using Relic.Core.Util;

namespace Relic.Core.Server;

/// <summary>
/// The agent config on this PC, parsed the way the agent itself parses it (<c>parse_config_text</c> +
/// <c>apply_config_file</c> in gio_agent.py): BOM tolerated, '#' comments and blank lines skipped,
/// blanks around '=' trimmed, matching surrounding quotes stripped, keys limited to
/// <c>[A-Za-z_][A-Za-z0-9_]*</c>, and the FIRST occurrence of a repeated key wins — letter case not
/// counted, as in the environment of Windows the agent reads them into. Two parsers that disagree would
/// have the launcher dial an agent on a port it never bound.
/// </summary>
public sealed record LocalAgentConfig(IReadOnlyDictionary<string, string> All)
{
    private string Get(string key) => All.TryGetValue(key, out var v) ? v : "";

    public string Token => Get("GIO_AGENT_TOKEN");
    /// <summary>Blank in the file = the agent's own default bind (loopback, 18080).</summary>
    public string Listen => Get("GIO_AGENT_LISTEN") is { Length: > 0 } l ? l : "127.0.0.1:18080";
    public string BindIp => Get("GIO_BIND_IP");
    public string AdvertisedIp => Get("GIO_ADVERTISED_IP");
    public string AdvertisedHost => Get("GIO_ADVERTISED_HOST");
    /// <summary>The stack folder the config names for a version (its <see cref="ServerVersions.DirKey"/>
    /// line), or "" when it names none.</summary>
    public string DirOf(string version) => Get(ServerVersions.DirKey(version));
    public string Dir16 => DirOf("1.6");
    public string Dir28 => DirOf("2.8");
    public string ServerName => Get("GIO_SERVER_NAME");
    /// <summary>Blank = derived by the agent per version from the stack's <c>.env</c> <c>OUTER_IP</c>. Not a
    /// secret (the sign key is <c>GIO_MUIP_KEY</c>): a re-install form pre-fills it, since the form writes the
    /// key even blank and a blank field would drop a value saved from the Agent settings card.</summary>
    public string MuipHost => Get("GIO_MUIP_HOST");
    /// <summary>"Keep .env settings" (<c>GIO_KEEP_ENV</c>), read by the agent's own rule for a switch that
    /// is off by default: on for 1 / true / yes / on — trimmed, in any letter case —, off for anything
    /// else and for a file without the key. A re-install form shows it, since the form writes the key as
    /// 1 or 0 and a toggle left on the wrong side would flip the stored choice.</summary>
    public bool KeepEnv => Get("GIO_KEEP_ENV").Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
    public int ListenPort => new AgentInstallPlan { Listen = Listen }.ListenPort;
    public string ListenHost => new AgentInstallPlan { Listen = Listen }.ListenHost;
    /// <summary>The address the launcher saves and dials for this agent, and aims the game redirect at:
    /// <see cref="HostFor"/> applied to the address the stacks publish their ports on
    /// (<see cref="StackBindIp"/>).</summary>
    public string Host => HostFor(StackBindIp, ListenHost);

    /// <summary>The local address the server stacks publish their ports on, as far as this PC can tell.
    /// With "Keep .env settings" off that is the configured <see cref="BindIp"/> (blank when none is
    /// set): the agent puts it into a stack's <c>.env</c> (<c>OUTER_IP</c>) when it prepares the stack.
    /// While <see cref="KeepEnv"/> is on the agent applies no bind IP and each stack keeps the
    /// <c>OUTER_IP</c> its own <c>.env</c> holds, so that value is read from the <c>.env</c> of every
    /// stack folder the config names (<see cref="StackDirs"/>, <see cref="LocalAgent.StackOuterIp"/>) and
    /// taken when <see cref="AgreedOuterIp"/> accepts it; when it does not — no <c>.env</c> yet, stacks
    /// that disagree, an address that is not this PC's — the stored <see cref="BindIp"/> stands in. With
    /// the option on, every read of this property reads those files again and, for an address that is not
    /// loopback, binds one UDP socket.</summary>
    public string StackBindIp => KeepEnv
        ? AgreedOuterIp(StackDirs.Select(LocalAgent.StackOuterIp), LocalAgent.CanBind) ?? BindIp
        : BindIp;

    private static readonly System.Text.RegularExpressions.Regex StackDirKey = new(@"^GIO_DIR_[0-9]+\z");

    /// <summary>Every stack folder the config names: the value of each <c>GIO_DIR_&lt;digits&gt;</c> key,
    /// whatever version it stands for — the agent that reads this file may know a version this launcher
    /// does not list, and that stack publishes its ports on this PC like the others.</summary>
    private IEnumerable<string> StackDirs => All.Where(kv => StackDirKey.IsMatch(kv.Key)).Select(kv => kv.Value);

    /// <summary>The one address the <c>OUTER_IP</c> values of the stacks stand for, or null when they give
    /// none to go by. A blank value is a stack that names none (no <c>.env</c> yet) and has no say.
    /// "localhost" and every 127.* value count as 127.0.0.1 — the agent's <c>_is_loopback</c>. The stacks
    /// that name one must agree: the launcher keeps ONE host for every version. An address that is not
    /// loopback must be one <paramref name="canBind"/> accepts — docker publishes the stack's ports on
    /// it, which this PC can only do on an address it has — and never 0.0.0.0, which publishes on every
    /// address of this PC and leaves none to dial. Pure.</summary>
    public static string? AgreedOuterIp(IEnumerable<string?> outerIps, Func<string, bool> canBind)
    {
        string? agreed = null;
        foreach (var raw in outerIps)
        {
            string ip = (raw ?? "").Trim();
            if (ip.Length == 0) continue;
            if (ip == "localhost" || ip.StartsWith("127.", StringComparison.Ordinal)) ip = "127.0.0.1";
            if (agreed is null) agreed = ip;
            else if (agreed != ip) return null;
        }
        if (agreed is null or "127.0.0.1") return agreed;
        return agreed != "0.0.0.0" && canBind(agreed) ? agreed : null;
    }

    /// <summary>The ONE rule for "which host do we save for the agent on this PC": the bind IP (reachable
    /// by other launchers on the LAN) only when the agent actually answers on it — a wildcard bind, or a
    /// bind on that very address; otherwise loopback. Saving the bind IP for a
    /// loopback-bound agent would be a direct config for an address the agent never bound.</summary>
    public static string HostFor(string bindIp, string listenHost)
    {
        bindIp = (bindIp ?? "").Trim();
        listenHost = (listenHost ?? "").Trim();
        bool wildcard = listenHost is "0.0.0.0" or "::" or "*" or "";
        if (bindIp.Length > 0 && (wildcard || string.Equals(bindIp, listenHost, StringComparison.OrdinalIgnoreCase)))
            return bindIp;
        return "127.0.0.1";
    }
}

/// <summary>What <see cref="LocalAgent.Uninstall"/> did, step by step; <paramref name="Leftovers"/> names
/// the files it could not delete (in use — a hotpatch pack a client is downloading, an open log), and
/// <paramref name="KeptRules"/> the firewall rules of the agent that are still there afterwards (not asked to go, or
/// not removed).</summary>
public sealed record UninstallReport(bool Stopped, bool AutostartRemoved, bool FirewallRemoved, string? FirewallError,
    bool FilesRemoved, IReadOnlyList<string> Leftovers, IReadOnlyList<string>? KeptRules = null);

/// <summary>
/// The GIO agent running on THIS Windows PC (Docker Desktop + Python). Relic ships the same
/// <c>gio_agent.py</c> it deploys to Linux boxes; here it copies it (plus the provisioning payloads)
/// under <c>%LOCALAPPDATA%\Relic\agent\</c>, writes the KEY=VALUE config, runs the agent's selftest,
/// starts it with <c>pythonw</c> (hidden, logging to a file) and can register a per-user autostart.
/// No admin rights anywhere except the two explicit, user-clicked firewall steps
/// (<see cref="OpenFirewallPort"/> at install, <see cref="RemoveFirewallRule"/> at uninstall), which
/// Windows cannot do unelevated.
/// </summary>
public static class LocalAgent
{
    /// <summary>Spikes only: point every path at a temp folder so a test never installs into, stops or
    /// deletes the developer's real agent (state file, hotpatch mirror). Null = the real root.</summary>
    public static string? RootOverride { get; set; }

    public static string Root => RootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Relic", "agent");
    public static string ConfigPath => Path.Combine(Root, "config");
    public static string ScriptPath => Path.Combine(Root, "gio_agent.py");
    public static string PayloadsDir => Path.Combine(Root, "payloads");
    public static string LogPath => Path.Combine(Root, "agent.log");
    public static string PidPath => Path.Combine(Root, "agent.pid");
    public static string PythonPath => Path.Combine(Root, "python.txt");
    /// <summary>Where <see cref="UpdateAsync"/> puts the agent it is about to install, to prove it first.</summary>
    public static string StagePath => Path.Combine(Root, ".update");
    /// <summary>The script and payloads the last <see cref="UpdateAsync"/> replaced — one generation.</summary>
    public static string PreviousPath => Path.Combine(Root, "previous");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RelicGioAgent";

    /// <summary>Where the shipped agent lives next to the exe (copied by the csproj / publish).</summary>
    public static string ShippedAgentDir => Path.Combine(AppContext.BaseDirectory, "agent");

    public sealed record PythonInfo(string Exe, string ExeW, string Version);

    public static bool IsInstalled => File.Exists(ConfigPath) && File.Exists(ScriptPath);

    /// <summary>Spikes only: the interpreters the Python registry entries name, in the order they are tried, in
    /// place of this PC's (<see cref="RegistryPythons"/>).</summary>
    public static Func<IReadOnlyList<string>>? PythonRegistryOverride { get; set; }

    /// <summary>Spikes only: one interpreter probe in place of a process — (program, arguments, the environment it
    /// runs with) → (exit code, output), or null for a program that did not start.</summary>
    public static Func<string, string, IReadOnlyDictionary<string, string>, (int Code, string Output)?>? PythonProbeOverride { get; set; }

    /// <summary>What every interpreter probe adds to its environment: the Python install manager installs no runtime
    /// because a launch command found none.</summary>
    public static readonly IReadOnlyDictionary<string, string> PythonProbeEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["PYTHON_MANAGER_AUTOMATIC_INSTALL"] = "false" };

    /// <summary>The probe: the interpreter's own path and its version, written as UTF-8 whatever the console's code page
    /// (an embeddable Python ignores PYTHONIOENCODING, and a user folder may hold a letter that code page lacks).</summary>
    private const string PythonProbeCode = "import sys;sys.stdout.reconfigure(encoding='utf-8');print(sys.executable);print('%d.%d'%sys.version_info[:2])";

    /// <summary>The interpreters <see cref="FindPython"/> asks, in order: Relic's own Python (its tools folder), then the
    /// Python registry entries (PEP 514: this user's, then the machine's, <see cref="RegistryPythons"/>), then
    /// <c>py -3</c>, <c>python</c> and <c>python3</c> from PATH — (program, the arguments before the probe's code).</summary>
    public static IReadOnlyList<(string Exe, string Args)> PythonCandidates()
    {
        var list = new List<(string, string)>();
        if (File.Exists(Setup.SetupTools.PythonExe)) list.Add((Setup.SetupTools.PythonExe, "-c"));
        foreach (var exe in PythonRegistryOverride?.Invoke() ?? RegistryPythons()) list.Add((exe, "-c"));
        list.Add(("py", "-3 -c"));
        list.Add(("python", "-c"));
        list.Add(("python3", "-c"));
        return list;
    }

    /// <summary>The interpreters the Python registry entries name (PEP 514, <c>Software\Python\PythonCore\&lt;tag&gt;\InstallPath</c>
    /// — its <c>ExecutablePath</c>, else <c>python.exe</c> in the folder it names): this user's first, then the
    /// machine's (64-bit view); within each the newest version first, a 64-bit one before a 32-bit one of the same
    /// version; each once, and only one that is there. Never throws.</summary>
    public static IReadOnlyList<string> RegistryPythons()
    {
        var found = new List<(int Order, Version Version, bool X86, string Exe)>();
        var tagVersion = new System.Text.RegularExpressions.Regex(@"^([0-9]+)\.([0-9]+)");
        foreach (var (hive, view, order) in new[] { (RegistryHive.CurrentUser, RegistryView.Default, 0), (RegistryHive.LocalMachine, RegistryView.Registry64, 1) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var core = root.OpenSubKey(@"Software\Python\PythonCore", writable: false);
                if (core is null) continue;
                foreach (var tag in core.GetSubKeyNames())
                {
                    using var ip = core.OpenSubKey(tag + @"\InstallPath", writable: false);
                    if (ip is null) continue;
                    string? exe = ip.GetValue("ExecutablePath") as string;
                    if (string.IsNullOrWhiteSpace(exe) && ip.GetValue("") is string dir && dir.Trim().Length > 0)
                        exe = Path.Combine(dir.Trim(), "python.exe");
                    if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe.Trim())) continue;
                    var m = tagVersion.Match(tag);
                    var version = m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : new Version(0, 0);
                    found.Add((order, version, tag.EndsWith("-32", StringComparison.OrdinalIgnoreCase), exe.Trim()));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or FormatException or OverflowException)
            {
                Log.Info($"python registry entries ({hive}): {ex.Message}");
            }
        }
        return found.OrderBy(f => f.Order).ThenByDescending(f => f.Version).ThenBy(f => f.X86)
            .Select(f => f.Exe).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>An interpreter that is no runtime of its own: one under a <c>\WindowsApps\</c> folder (the Store's
    /// app-execution aliases and its packages, whose writes to AppData Windows redirects) or in the Python install
    /// manager's alias folder (<c>%LocalAppData%\Python\bin</c>). Never the answer of <see cref="FindPython"/>.</summary>
    public static bool IsAliasPython(string? path)
    {
        string p = (path ?? "").Trim().Replace('/', '\\');
        if (p.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return true;
        string aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Python", "bin");
        return p.StartsWith(aliases.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Find a usable CPython ≥ 3.10 among <see cref="PythonCandidates"/>, in their order: each is asked for
    /// its <c>sys.executable</c> and version (8 s at most, its input closed — a program that waits for an answer is not
    /// waited for), with <see cref="PythonProbeEnvironment"/> in its environment, so the Python install manager never
    /// downloads a runtime because a command found none. An answer under <c>\WindowsApps\</c> or in the install
    /// manager's alias folder is passed over (<see cref="IsAliasPython"/>). Returns null when none qualifies (the UI
    /// then shows install guidance). Every probe is a process: a caller on the message thread runs this off it.</summary>
    public static PythonInfo? FindPython()
    {
        foreach (var (exe, args) in PythonCandidates())
        {
            try
            {
                if (ProbePython(exe, args) is not { Code: 0 } answer) continue;
                var lines = answer.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length < 2) continue;
                string path = lines[0], ver = lines[1];
                var parts = ver.Split('.');
                if (parts.Length < 2 || !int.TryParse(parts[0], out int maj) || !int.TryParse(parts[1], out int min)) continue;
                if (maj < 3 || (maj == 3 && min < 10)) continue;
                if (IsAliasPython(path) || !File.Exists(path)) continue;
                string exeW = Path.Combine(Path.GetDirectoryName(path)!, "pythonw.exe");
                if (!File.Exists(exeW)) exeW = path;
                return new PythonInfo(path, exeW, ver);
            }
            catch { /* next candidate */ }
        }
        return null;
    }

    /// <summary>One interpreter probe of <paramref name="exe"/> with <paramref name="args"/> (the arguments before the
    /// probe's code): (exit code, standard output read as UTF-8), null when the program did not answer within 8 s —
    /// its input closed, its output read as it comes, the whole process tree ended at the deadline
    /// (<see cref="Setup.TimedProcess"/>). A program that does not start throws.</summary>
    private static (int Code, string Output)? ProbePython(string exe, string args)
    {
        if (PythonProbeOverride is { } fake) return fake(exe, args + " \"" + PythonProbeCode + "\"", PythonProbeEnvironment);
        var argv = args.Split(' ', StringSplitOptions.RemoveEmptyEntries).Append(PythonProbeCode).ToList();
        var r = Setup.TimedProcess.Run(exe, argv, TimeSpan.FromSeconds(8), PythonProbeEnvironment, CancellationToken.None);
        return r.Code is { } code ? (code, System.Text.Encoding.UTF8.GetString(r.Stdout)) : null;
    }

    /// <summary>A 7z extractor the agent on this PC can use for the server-package download, in this order: the
    /// <c>GIO_7Z</c> the installed agent's configuration names (when it is a file that is there), Relic's own
    /// <c>7zr.exe</c> (its tools folder), then the agent's usual candidates (<see cref="OwnExtractor"/>). Relic's
    /// <c>7zr.exe</c> reaches the agent only through <c>GIO_7Z</c>: every install writes it when the agent finds no
    /// tool of its own (<see cref="FillSevenZip"/>). Null = nothing usable.</summary>
    public static string? FindExtractor() => ConfiguredExtractor() ?? RelicSevenZip() ?? OwnExtractor();

    /// <summary>The <c>GIO_7Z</c> of the installed agent's configuration when it names a file that is there (a tar is
    /// taken only when it reads 7z, as the agent takes it); null otherwise.</summary>
    private static string? ConfiguredExtractor()
    {
        var c = ReadConfig();
        if (c is null || !c.All.TryGetValue("GIO_7Z", out var raw)) return null;
        string tool = (raw ?? "").Trim();
        if (tool.Length == 0) return null;
        try
        {
            if (!File.Exists(tool)) return null;
            string name = Path.GetFileNameWithoutExtension(tool).ToLowerInvariant();
            return name is "tar" or "bsdtar" && !TarReadsSevenZip(tool) ? null : tool;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Relic's <c>7zr.exe</c> when it is there.</summary>
    private static string? RelicSevenZip() => File.Exists(Setup.SetupTools.SevenZipExe) ? Setup.SetupTools.SevenZipExe : null;

    /// <summary>Does the agent on this PC find an extractor without Relic's — the one its configuration names
    /// (<c>GIO_7Z</c>) or one of its usual candidates?</summary>
    public static bool AgentFindsExtractor() => ConfiguredExtractor() is not null || OwnExtractor() is not null;

    /// <summary>The <c>GIO_7Z</c> an install writes when the plan names none: <paramref name="relicPath"/> (Relic's
    /// <c>7zr.exe</c>) when that file is there, the agent finds no extractor of its own and the path is one the plan
    /// takes (<see cref="AgentInstallPlan.SevenZipPathOk"/> — a value written by itself never refuses an install); null
    /// — nothing to write — otherwise. Pure.</summary>
    public static string? SevenZipFill(string? named, bool relicHas, string relicPath, bool agentFindsOwn) =>
        string.IsNullOrWhiteSpace(named) && relicHas && !agentFindsOwn && AgentInstallPlan.SevenZipPathOk(relicPath) ? relicPath.Trim() : null;

    /// <summary>Fill a Windows plan's <see cref="AgentInstallPlan.SevenZip"/> by <see cref="SevenZipFill"/> from this
    /// PC's files, so no install — the form's included — leaves the agent a <c>.7z</c> package it cannot unpack. True
    /// when it filled it.</summary>
    public static bool FillSevenZip(AgentInstallPlan plan)
    {
        if (plan is null || !plan.IsWindows) return false;
        string relic = Setup.SetupTools.SevenZipExe;
        bool there = File.Exists(relic);
        if (SevenZipFill(plan.SevenZip, there, relic, AgentFindsExtractor()) is not { } fill)
        {
            if (there && string.IsNullOrWhiteSpace(plan.SevenZip) && !AgentInstallPlan.SevenZipPathOk(relic))
                Log.Info($"local agent install: Relic's 7zr.exe is not written as GIO_7Z ({relic} is no path the configuration can hold)");
            return false;
        }
        plan.SevenZip = fill;
        return true;
    }

    /// <summary>The extractors the agent on this PC finds by itself (the same candidates as its
    /// <c>find_extractor()</c> after <c>GIO_7Z</c>): 7-Zip's 7z.exe in its usual homes or on PATH,
    /// Bandizip's bz.exe, else Windows' own <c>System32\tar.exe</c> — bsdtar, accepted only when it was
    /// built with liblzma (it reads LZMA2 .7z then), and always by FULL path: Git for Windows puts a GNU
    /// tar on PATH that shadows it and knows no 7z at all. Null = nothing usable.</summary>
    private static string? OwnExtractor()
    {
        var candidates = new List<string>();
        foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)" })
        {
            string? pf = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "7-Zip", "7z.exe"));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "7-Zip", "7z.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { candidates.Add(Path.Combine(dir, "7z.exe")); } catch { /* an unusable PATH entry */ }
        }
        string? pf64 = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrEmpty(pf64)) candidates.Add(Path.Combine(pf64, "Bandizip", "bz.exe"));
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { /* next */ }
        }
        string sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        string tar = Path.Combine(sysRoot, "System32", "tar.exe");
        try
        {
            if (File.Exists(tar) && TarReadsSevenZip(tar)) return tar;
        }
        catch (Exception ex) { Log.Info($"tar.exe probe: {ex.Message}"); }
        return null;
    }

    /// <summary>What <c>tar.exe --version</c> said of the file at its path, time and size: whether that tar was
    /// built with liblzma changes only with the file itself, while asking it starts a process — and the
    /// install form asks <see cref="FindExtractor"/> at every folder it checks.</summary>
    private static (string Path, DateTime Written, long Length, bool Lzma)? _tarProbe;
    private static readonly object TarProbeLock = new();

    /// <summary>Does the tar at <paramref name="tar"/> read a .7z (its <c>--version</c> names liblzma)? Asked
    /// once per file: the answer is remembered by the file's path, time and size. A run that does not end
    /// well is not remembered.</summary>
    private static bool TarReadsSevenZip(string tar)
    {
        var file = new FileInfo(tar);
        var (path, written, length) = (file.FullName, file.LastWriteTimeUtc, file.Length);
        lock (TarProbeLock)
        {
            if (_tarProbe is { } known && known.Path == path && known.Written == written && known.Length == length) return known.Lzma;
        }
        var res = Proc.Run(tar, "--version");
        bool lzma = res.Ok && (res.Out + res.Err).Contains("liblzma", StringComparison.OrdinalIgnoreCase);
        if (res.Ok) lock (TarProbeLock) _tarProbe = (path, written, length, lzma);
        return lzma;
    }

    /// <summary>Copy the shipped agent + payloads, write the config (merged with the previous one, see
    /// <see cref="MergeConfig(string, string?)"/>), run <c>--selftest</c>. Does not start the agent. Throws
    /// with a user-facing message on failure — every refusal of the plan comes before anything is written.
    /// A folder the previous config stores for a version is kept, whatever the form named: a stack folder
    /// changes only through a relocation (<see cref="AgentInstallPlan.KeepStoredFolders"/>, applied to
    /// <paramref name="plan"/> itself, so the caller reads the rows as the install left them). The config is
    /// read again right before it is written, and a folder the running agent relocated meanwhile is the one
    /// the version keeps; a config that cannot be read then is not written at all (the new agent files are
    /// in place by then, the same state an update leaves). Returns the versions the agent really has to
    /// download.</summary>
    public static IReadOnlyList<string> Install(AgentInstallPlan plan, PythonInfo python, Action<string>? log = null, string? agentDir = null)
    {
        if (!plan.IsWindows) throw new InvalidOperationException("LocalAgent.Install: plan target is not windows");
        // The configuration this install replaces is read FIRST: the folders it stores stay, and a file
        // that is there but cannot be read refuses the install before anything is written.
        string? previous = ReadConfigTextForInstall();
        var stored = previous is null ? null : ParseConfig(previous);
        foreach (var k in plan.KeepStoredFolders(v => stored?.DirOf(v) ?? ""))
        {
            if (!k.Replaced) continue;
            string line = L.T("core.deploy.dirKept", new { version = k.Version, dir = k.Stored, given = k.Given });
            log?.Invoke(line);
            Log.Info("local agent install: " + line);
        }
        // A folder the form names is written, and handed to the follow-up, the way the agent answers it
        // (os.path.normpath): "C:\relic_servers\\3.2_live" would otherwise read as another folder than the
        // agent's "C:\relic_servers\3.2_live" once a re-point went where it was asked.
        plan.NormalizeWindowsFolders();
        // The extractor the agent will unpack with: Relic's own 7zr.exe when the agent finds no tool of its own.
        if (FillSevenZip(plan)) log?.Invoke(L.T("core.deploy.sevenZipUsed", new { path = plan.SevenZip }));
        var errs = plan.Validate();
        if (errs.Count > 0) throw new InvalidOperationException(string.Join("\n", errs));
        if (!string.IsNullOrWhiteSpace(plan.SevenZip) && !File.Exists(plan.SevenZip.Trim()))
            throw new InvalidOperationException(L.T("core.deploy.sevenZipMissing", new { path = plan.SevenZip.Trim() }));
        // A folder the form names must be on a drive the agent can use (the agent's own relocation rule):
        // Docker Desktop bind-mounts no network drive, a CD cannot be written, a RAM disk loses its files at
        // a reboot. A folder the configuration stores is the agent's own and is not asked about.
        var drives = plan.Versions
            .Where(v => (plan.ChoiceFor(v) is StackChoice.Have or StackChoice.Download) && !plan.FolderIsStored(v)
                && !string.IsNullOrWhiteSpace(plan.DirFor(v)))
            .Select(v => DriveProblem(v, plan.DirFor(v))).OfType<string>().ToList();
        if (drives.Count > 0) throw new InvalidOperationException(string.Join("\n", drives));
        var missing = plan.MissingStackEntries(p => Directory.Exists(p) || File.Exists(p));
        if (missing.Count > 0) throw new InvalidOperationException(L.T("core.deploy.stackIncomplete", new { list = string.Join(", ", missing) }));
        // A download into a folder the form names that holds other files: the agent would refuse it.
        var occupied = plan.DownloadTargetProblems(FolderKindOf);
        if (occupied.Count > 0) throw new InvalidOperationException(string.Join("\n", occupied));
        // The agent to install comes first: a build that ships none has no catalogue either, and the
        // answers below would then name a package that is not published where the agent itself is what
        // is missing.
        string src = agentDir ?? ShippedAgentDir;
        string srcScript = Path.Combine(src, "gio_agent.py");
        if (!File.Exists(srcScript)) throw new InvalidOperationException(L.T("core.deploy.agentMissingInBuild", new { path = srcScript }));
        // A version marked for download is skipped by the stack check, so two things are asked of its
        // package instead — of the catalogue this install copies to the agent. There must be one: a mark
        // the agent has nothing to download for would only fail once the install is done. And where the
        // package comes as a .7z the agent will have to EXTRACT it with a tool, which nothing guarantees
        // on Windows: refuse now, with the fix in the message, rather than let a multi-GB download fail
        // at its very last step. A package that comes as a .zip takes none — the agent reads it itself.
        StackPackage Package(string version) => ServerVersions.Package(version, Path.Combine(src, "payloads"));
        var unpublished = plan.FetchProblems(Package);
        if (unpublished.Count > 0) throw new InvalidOperationException(string.Join("\n", unpublished));
        if (plan.FetchNeedsExtractor(Package) && string.IsNullOrWhiteSpace(plan.SevenZip) && !AgentFindsExtractor())
            throw new InvalidOperationException(L.T("core.deploy.noExtractorWindows"));

        if (Updating) throw new InvalidOperationException(L.T("core.deploy.updateRunning"));
        Directory.CreateDirectory(Root);
        log?.Invoke(L.T("core.deploy.copyingAgent", new { dir = Root }));
        // One hand on the agent's files at a time: a recovery another thread starts meanwhile (the
        // start screen, the Server page's card) finds the lock taken and leaves them alone.
        lock (FilesLock)
        {
            RecoverCutUpdateCore();
            // Still there = it could not be undone to its end (a file is held): this install takes its place.
            bool cut = File.Exists(SwapJournalPath);
            // Before anything is replaced: a script another program holds open cannot be written, and
            // the payloads must not go in ahead of a script that then stays what it was.
            if (File.Exists(ScriptPath))
            {
                try { RetryIo(() => { using var probe = new FileStream(ScriptPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(L.T("core.deploy.scriptInUse", new { path = ScriptPath, error = ex.Message }));
                }
            }
            // An undo that stopped at the script's own move leaves the installed payloads in their place
            // and no script. The folder that takes their place is no move of that journal, so the script
            // goes in first: the journal's line then has its target and its source both there, no pass
            // takes it back, and an install that ends before its payloads are in is finished by the next
            // install — never undone into the installed script beside the shipped payloads.
            bool scriptFirst = cut && !File.Exists(ScriptPath) && Directory.Exists(PayloadsDir);
            if (scriptFirst) File.Copy(srcScript, ScriptPath, overwrite: true);
            string srcPayloads = Path.Combine(src, "payloads");
            try
            {
                if (Directory.Exists(srcPayloads)) ReplacePayloads(srcPayloads, log);
            }
            catch when (scriptFirst && !Directory.Exists(PayloadsAsidePath))
            {
                // Refused with the installed payloads still in their place: the script goes again, so
                // nothing was changed and the undo ends once its file is free.
                try { RetryIo(() => File.Delete(ScriptPath)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Info($"local agent install: {ScriptPath} was left behind: {ex.Message}");
                }
                throw;
            }
            if (!scriptFirst) File.Copy(srcScript, ScriptPath, overwrite: true);
            if (cut)
            {
                // What that undo had already taken back out of the payloads folder sits with the
                // generation it was restoring, and belongs to this PC all the same. Each move goes
                // into the journal like an update's own: an install that ends before the journal is
                // gone is taken back whole, these entries with it.
                var back = CarryBoxPayloads(Path.Combine(PreviousPath, AgentBuild.PayloadsName), PayloadsDir,
                    s => Log.Info("local agent install: not kept: " + s), JournalMove);
                if (back.Count > 0) log?.Invoke(L.T("core.deploy.keptPayloads", new { list = CarriedNames(back, PayloadsDir) }));
            }
            // The script and the payloads are the shipped ones from here on: a journal a cut update
            // left behind, and that could not be undone above, describes files that are no longer
            // there. It goes only now, once the files are replaced — an install that fails before
            // leaves the undo its record — and it has to go: gone through later it would take this
            // install back, so nothing more is done on top of one that stays.
            DeleteJournals();
            if (File.Exists(SwapJournalPath))
                throw new InvalidOperationException(L.T("core.deploy.installJournalInUse", new { path = SwapJournalPath }));
            if (cut) DeleteQuietly(StagePath);
        }
        // MERGE, never overwrite: the form renders only the keys it manages, and everything else in the
        // file — set from the Agent settings card, added by hand (GIO_FETCH_DISK_RESERVE,
        // HTTPS_PROXY, …) or typed at an earlier install into a field left blank now (the MySQL root
        // password, the Flask key) — survives the re-install, like install_agent.sh keeps it on
        // Linux. Values are never logged, only the key names. The file is read once more right before it
        // is written: the agent that runs may have changed it meanwhile (Agent settings, a relocation),
        // and a folder it relocated is the one the version keeps. One that cannot be read now is not
        // written: merged over the first reading, it would point a relocated version back at the folder
        // it left. The new files stay — the configuration is the one the agent already has, as after an
        // update — and the next install finishes the job.
        string? late;
        try { late = ReadConfigText(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("local agent install: the configuration could not be read again before it is written (left as it is)", ex);
            throw new InvalidOperationException(L.T("core.deploy.configUnreadableLate", new { path = ConfigPath, error = ex.Message }));
        }
        if (!string.Equals(late, previous, StringComparison.Ordinal))
        {
            var lateCfg = late is null ? null : ParseConfig(late);
            plan.KeepStoredFolders(v => lateCfg?.DirOf(v) ?? "");
            Log.Info("local agent install: the configuration changed during the install -- its folders are taken as they are now");
        }
        string config = MergeConfig(plan.RenderConfig(), late, out var kept);
        File.WriteAllText(ConfigPath, config, new System.Text.UTF8Encoding(false));
        if (kept.Count > 0)
        {
            Log.Info($"local agent install: kept from the previous config: {string.Join(", ", kept)}");
            log?.Invoke(L.T("core.deploy.keptConfig", new { keys = string.Join(", ", kept) }));
        }
        File.WriteAllText(PythonPath, python.Exe + Environment.NewLine + python.ExeW, new System.Text.UTF8Encoding(false));
        // Same rule as install_agent.sh: a folder that already holds a stack is not downloaded. The
        // caller uses the returned list to queue the fetch jobs, so a version skipped here is never
        // queued and can never fail with the agent's 409 "already on this server".
        var willFetch = plan.FetchVersionsReally(p => Directory.Exists(p) || File.Exists(p));
        foreach (var v in plan.FetchVersions())
            log?.Invoke(willFetch.Contains(v)
                ? L.T("core.deploy.willFetch", new { version = v, dir = plan.DirFor(v) })
                : L.T("core.deploy.fetchSkipped", new { version = v, dir = plan.DirFor(v) }));

        log?.Invoke(L.T("core.deploy.selftest"));
        // 900 s: the selftest takes about two minutes on a fast Windows PC (real file I/O an antivirus scans); slow laptops need the margin.
        var (code, output) = Run(python.Exe, $"\"{ScriptPath}\" --selftest", Root, timeoutMs: 900_000);
        Log.Info($"local agent selftest exit={code}\n{Tail(output, 2000)}");
        if (code != 0) throw new InvalidOperationException(L.T("core.deploy.selftestFailed", new { tail = Tail(output, 600) }));
        log?.Invoke(L.T("core.deploy.selftestOk"));
        return willFetch;
    }

    /// <summary>What this PC added to its payloads folder moves into the folder that replaces it — the
    /// rule of <c>carry_box_payloads</c> in install_agent.sh: per version, unless the new payloads ship a
    /// navmesh bundle for that version, the navmesh folder, a folder an adoption renamed aside, the
    /// archives and the refusal record of an adoption (<c>navmesh*</c>, <c>*.zip</c>,
    /// <c>.relic-navmesh-zip-refused</c>); and the archives of the payloads folder itself unless the new
    /// payloads ship a bundle for any version. All of them are entries the build identity leaves out
    /// (<see cref="AgentBuild.Skips"/>). A name the new folder already holds is never replaced; an entry
    /// that will not move stays behind and is named to <paramref name="warn"/>. <paramref name="moving"/>
    /// is told of each move BEFORE it is made (an update writes it into its journal). Returns the moves
    /// done, in order.</summary>
    public static List<(string From, string To, bool IsDir)> CarryBoxPayloads(string oldPayloads, string newPayloads,
        Action<string>? warn = null, Action<string, string, bool>? moving = null)
    {
        var moved = new List<(string From, string To, bool IsDir)>();
        if (!Directory.Exists(oldPayloads) || !Directory.Exists(newPayloads)) return moved;
        static bool Has(string dir, string name) => File.Exists(Path.Combine(dir, name)) || Directory.Exists(Path.Combine(dir, name));
        void Carry(string from, string name, string toDir)
        {
            if (Has(toDir, name)) return;
            bool isDir = Directory.Exists(from);
            string to = Path.Combine(toDir, name);
            try
            {
                moving?.Invoke(from, to, isDir);
                if (isDir) Directory.Move(from, to); else File.Move(from, to);
                moved.Add((from, to, isDir));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warn?.Invoke($"{from}: {ex.Message}"); }
        }
        bool shipsBundle = Directory.GetDirectories(newPayloads).Any(v => Has(v, "navmesh"));
        if (!shipsBundle)
            foreach (var f in Directory.GetFiles(oldPayloads))
                if (f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) Carry(f, Path.GetFileName(f), newPayloads);
        foreach (var d in Directory.GetDirectories(oldPayloads))
        {
            if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            string target = Path.Combine(newPayloads, Path.GetFileName(d));
            if (!Directory.Exists(target) || Has(target, "navmesh")) continue;
            foreach (var e in Directory.GetFileSystemEntries(d))
            {
                string n = Path.GetFileName(e);
                if (n.StartsWith("navmesh", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || n.Equals(".relic-navmesh-zip-refused", StringComparison.OrdinalIgnoreCase))
                    Carry(e, n, target);
            }
        }
        return moved;
    }

    /// <summary>The carried entries as the log names them: their paths inside the payloads folder.</summary>
    private static string CarriedNames(IEnumerable<(string From, string To, bool IsDir)> carried, string payloads) =>
        string.Join(", ", carried.Select(m => Path.GetRelativePath(payloads, m.To).Replace('\\', '/')));

    /// <summary>An install's payloads: the shipped folder takes the place of the installed one, and what
    /// this PC added to the installed one is kept (<see cref="CarryBoxPayloads"/>). The new folder is put
    /// together beside the old one; the old one is set aside by ONE rename — a folder with a file open
    /// in it (an agent in the middle of a job) refuses that as a whole, and then nothing was changed —,
    /// the new one renamed into its place, and only then the box's entries move over and the old folder
    /// goes. So nothing the box added ever sits in a folder a later run deletes; a run that was cut short
    /// is picked up by the next one from the folder it had set aside.</summary>
    public static void ReplacePayloads(string srcPayloads, Action<string>? log = null)
    {
        lock (FilesLock) ReplacePayloadsLocked(srcPayloads, log);
    }

    private static string PayloadsAsidePath => Path.Combine(Root, ".payloads-old");

    /// <summary>The payloads folder an install had set aside and did not live to empty: while there is
    /// no payloads folder it IS the payloads folder again; beside a new one it still holds what this PC
    /// had added, which moves over before it goes.</summary>
    private static void RecoverSetAsidePayloads()
    {
        string aside = PayloadsAsidePath;
        if (!Directory.Exists(aside)) return;
        if (!Directory.Exists(PayloadsDir))
        {
            Directory.Move(aside, PayloadsDir);
            Log.Info("local agent: the payloads folder an install had set aside is the payloads folder again");
            return;
        }
        var carried = CarryBoxPayloads(aside, PayloadsDir, s => Log.Info("local agent: not kept: " + s));
        if (carried.Count > 0) Log.Info("local agent: kept from a payloads folder an install had set aside: " + CarriedNames(carried, PayloadsDir));
        DeleteQuietly(aside);
    }

    private static void ReplacePayloadsLocked(string srcPayloads, Action<string>? log)
    {
        string fresh = Path.Combine(Root, ".payloads-new"), aside = PayloadsAsidePath;
        void NotKept(string s) => Log.Info("local agent install: not kept: " + s);
        RecoverSetAsidePayloads();
        if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
        CopyTree(srcPayloads, fresh);
        bool had = Directory.Exists(PayloadsDir);
        if (had)
        {
            try { MoveRetry(PayloadsDir, aside, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DeleteQuietly(fresh);
                throw new InvalidOperationException(L.T("core.deploy.payloadsInUse", new { dir = PayloadsDir, error = ex.Message }));
            }
        }
        try
        {
            // While the journal of an undo remains and the payloads' place is empty, that undo still has
            // the installed payloads to put back there: the folder going in is one more move of that
            // journal, so an install that ends before its script is in is taken back with the rest.
            if (!had && File.Exists(SwapJournalPath)) JournalMove(fresh, PayloadsDir, true);
            Directory.Move(fresh, PayloadsDir);
        }
        catch
        {
            if (had && !Directory.Exists(PayloadsDir)) Directory.Move(aside, PayloadsDir);
            throw;
        }
        if (!had) return;
        var carried = CarryBoxPayloads(aside, PayloadsDir, NotKept);
        if (carried.Count > 0) log?.Invoke(L.T("core.deploy.keptPayloads", new { list = CarriedNames(carried, PayloadsDir) }));
        DeleteQuietly(aside);
    }

    /// <summary>What <see cref="UpdateAsync"/> did: <paramref name="Replaced"/> = the script and the
    /// payloads were replaced (false = they already were the shipped ones); <paramref name="Restarted"/> =
    /// the agent ran and was stopped; <paramref name="Running"/> = it runs again;
    /// <paramref name="Carried"/> = what was kept from the old payloads folder.</summary>
    public sealed record UpdateReport(bool Replaced, bool Restarted, bool Running, IReadOnlyList<string> Carried);

    private static int _updating;

    /// <summary>True while <see cref="UpdateAsync"/> runs: the agent's files are being replaced and its
    /// process is stopped on purpose, so nothing else may start or stop it meanwhile.</summary>
    public static bool Updating => Volatile.Read(ref _updating) != 0;

    /// <summary>The journal of an update's file moves while they are being made: one line per move,
    /// written — and flushed to the disk — BEFORE the move. While it exists the moves are incomplete:
    /// the update is in the middle of them, or the launcher went away there (<see cref="RecoverCutUpdate"/>).</summary>
    private static string SwapJournalPath => Path.Combine(PreviousPath, ".swap");

    /// <summary>The same journal once every move is made: kept only until the updated agent has started.
    /// An agent that does not start gives it its first name back and is undone from it; nothing else
    /// looks for it.</summary>
    private static string SwappedJournalPath => Path.Combine(PreviousPath, ".swapped");

    private static void JournalMove(string from, string to, bool isDir)
    {
        using var fs = new FileStream(SwapJournalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        fs.Write(System.Text.Encoding.UTF8.GetBytes((isDir ? "D" : "F") + "\t" + from + "\t" + to + "\n"));
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Take back the moves a journal names, strictly last first. A move whose target is there
    /// and whose source is not goes back; one whose target is not there was never made (the line was
    /// written, the launcher went away before the move) or is back already, and is passed over. After
    /// each line the journal is written again WITHOUT it, so the journal only ever names the moves that
    /// are still made — the paths repeat (the place of the script is the source of one move and the
    /// target of another), and a line gone through twice would move a file that is already back. For the
    /// same reason the first move that cannot go back ends the pass: nothing before it is touched until it
    /// has. A target and a source that are BOTH there end it too: no move of an update leaves that —
    /// something else was put where the file has to go back to — and nothing is moved over it; an install
    /// puts the shipped agent in place of such a set. A last line without its line end was cut while it
    /// was written: its move never began. Null when every move is back (the journal is deleted then),
    /// else what stopped the pass — the journal stays for the next one. Called with
    /// <see cref="FilesLock"/> held, or from the update itself.</summary>
    private static string? UndoMoves(string journal)
    {
        if (!File.Exists(journal)) return null;
        try
        {
            var lines = File.ReadAllText(journal).Split('\n').ToList();
            // What follows the last line end is "" for a whole journal, else a line that was cut short.
            lines.RemoveAt(lines.Count - 1);
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var parts = lines[i].TrimEnd('\r').Split('\t');
                if (parts.Length == 3)
                {
                    bool isDir = parts[0] == "D";
                    string from = parts[1], to = parts[2];
                    bool There(string p) => isDir ? Directory.Exists(p) : File.Exists(p);
                    if (There(to) && There(from))
                    {
                        Log.Info($"local agent update: {to} cannot be put back, {from} is there as well");
                        return L.T("core.deploy.updateUndoBlocked", new { path = from });
                    }
                    if (There(to))
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                            MoveRetry(to, from, isDir);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            Log.Error($"local agent update: {to} could not be put back", ex);
                            return $"{to}: {ex.Message}";
                        }
                    }
                }
                if (i > 0) RewriteJournal(journal, lines, i);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The journal itself could not be read, or written again without the line just undone:
            // nothing more is moved on its word. The line it still names is passed over next time.
            Log.Error($"local agent update: the journal {journal} could not be gone through", ex);
            return $"{journal}: {ex.Message}";
        }
        // Every move is back. A journal that will not go now names moves that are no longer made, and
        // is passed over line by line the next time it is looked at.
        try { File.Delete(journal); File.Delete(journal + ".tmp"); }
        catch (Exception ex) { Log.Info($"local agent update: the journal {journal} was left behind: {ex.Message}"); }
        return null;
    }

    /// <summary>The journal with its first <paramref name="count"/> lines only, put in place of the old
    /// one in one step (written beside it, flushed, renamed over it).</summary>
    private static void RewriteJournal(string journal, List<string> lines, int count)
    {
        string tmp = journal + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(System.Text.Encoding.UTF8.GetBytes(string.Concat(lines.Take(count).Select(l => l + "\n"))));
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, journal, overwrite: true);
    }

    /// <summary>One hand on the agent's files at a time: an install, an update's own recovery and the
    /// recovery the start screen or a card asks for all move the same files.</summary>
    private static readonly object FilesLock = new();

    /// <summary>Undo an update whose file moves were cut short — the launcher was closed, or the PC went
    /// down, between the first move and the last: the agent then has no script (it reads as not
    /// installed), or the new script without a payloads folder, or the new script and payloads without
    /// what this PC had added. Every move the journal still names is taken back,
    /// so the agent that was installed before is in place again, whole; a payloads folder an install had
    /// set aside is picked up the same way. Called before anything that looks at the installed agent or
    /// starts it; a no-op without anything to pick up and while an update runs. While another thread
    /// works on these files a caller on the message thread goes on without it (the start screen: that
    /// thread's own pass is under way), one off it says <paramref name="wait"/> and looks at the files
    /// only once they are at rest. An agent Windows started meanwhile from the files as they were keeps
    /// running on what it loaded: the build it reports then differs from the files, and the update is
    /// offered again. True when an update was undone. Never throws.</summary>
    public static bool RecoverCutUpdate(bool wait = false)
    {
        if (Updating) return false;
        if (wait) Monitor.Enter(FilesLock);
        else if (!Monitor.TryEnter(FilesLock)) return false;
        try { return !Updating && RecoverCutUpdateCore(); }
        finally { Monitor.Exit(FilesLock); }
    }

    /// <summary><see cref="RecoverCutUpdate"/> for a caller that holds <see cref="FilesLock"/>.</summary>
    private static bool RecoverCutUpdateCore()
    {
        bool undone = false;
        try
        {
            if (File.Exists(SwapJournalPath))
            {
                string? error = UndoMoves(SwapJournalPath);
                // The staging folder holds what the undo moved back into it: it goes only once the
                // journal is done with, never under a move that is still to be taken back.
                if (error is null)
                {
                    DeleteQuietly(StagePath);
                    // ...and so does the folder an install's payloads were taken back to.
                    DeleteQuietly(Path.Combine(Root, ".payloads-new"));
                }
                Log.Info(error is null
                    ? "local agent: an update that was cut short was undone — the files it had moved are back in place"
                    : "local agent: an update that was cut short could not be undone completely (tried again next time): " + error);
                undone = error is null;
            }
            if (!File.Exists(SwapJournalPath)) RecoverSetAsidePayloads();
        }
        catch (Exception ex)
        {
            Log.Error("local agent: undoing an update that was cut short (non-fatal)", ex);
        }
        return undone;
    }

    /// <summary>Does an agent with this configuration answer on 127.0.0.1 — where the launcher asks
    /// /health (<see cref="IsHealthyAsync"/>)? Yes for the wildcard or a loopback listen address (the
    /// agent's server is IPv4); an agent bound to one other address of this PC answers only there.</summary>
    private static bool AnswersOnLoopback(LocalAgentConfig? c)
    {
        string h = (c?.ListenHost ?? "127.0.0.1").Trim();
        return h is "" or "0.0.0.0" or "localhost" || h.StartsWith("127.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Bring the agent on this PC to the one this launcher ships, leaving its configuration alone: the
    /// config file is never written — not rewritten, not merged; token, folders and every setting stay
    /// byte for byte — and only its listen address and token are read, to find, ask, stop and start the
    /// agent. In this order, so a step that fails leaves a working agent behind: a running agent is asked
    /// for a job in progress (<paramref name="busyKind"/>: a job, or an agent that cannot be asked,
    /// refuses the update); the shipped script + payloads are copied to <see cref="StagePath"/> and proven
    /// there (<c>--selftest</c> on the staged copy); the agent — as it is then: one started meanwhile
    /// counts — is asked once more and stopped; the installed script and payloads move to
    /// <see cref="PreviousPath"/> (the generation kept there goes at that moment, not earlier), the staged
    /// ones into their place, and what this PC added to the old payloads folder moves along
    /// (<see cref="CarryBoxPayloads"/>); an agent that ran is started again. Every move is written into a
    /// journal first, so one that fails — or an updated agent that does not start — is undone: each move
    /// goes back in reverse order and the previous agent is started again; an update the launcher did not
    /// live to finish is undone the same way by <see cref="RecoverCutUpdate"/>. An agent that was not
    /// running stays stopped. When the files already are the shipped ones only the restart is left — a
    /// running agent was started before them. Throws with a user-facing message when it changed nothing
    /// or put everything back — and, the one exception, when the updated agent did not start and the
    /// journal could not be used for the undo: the new files then stay in place, whole, and stopped.
    /// </summary>
    public static async Task<UpdateReport> UpdateAsync(Action<string>? log = null, string? agentDir = null,
        Func<LocalAgentConfig?, int, Task<string?>>? busyKind = null, int startWaitSeconds = 90)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException(L.T("backend.localAgent.windowsOnly"));
        if (Interlocked.Exchange(ref _updating, 1) != 0) throw new InvalidOperationException(L.T("core.deploy.updateRunning"));
        try
        {
            // Under the lock: a recovery another thread began before the flag above was set is waited
            // for here, and none can begin after it.
            lock (FilesLock) RecoverCutUpdateCore();
            // A journal that is still there could not be gone through to its end (a file is held): the
            // agent's files are neither the old set nor the new one, and nothing may be built on that.
            if (File.Exists(SwapJournalPath)) throw new InvalidOperationException(L.T("core.deploy.updateCutPending", new { dir = Root }));
            if (!IsInstalled) throw new InvalidOperationException(L.T("core.deploy.notInstalledLocally"));
            string src = agentDir ?? ShippedAgentDir;
            string srcScript = Path.Combine(src, AgentBuild.ScriptName);
            if (!File.Exists(srcScript)) throw new InvalidOperationException(L.T("core.deploy.agentMissingInBuild", new { path = srcScript }));
            return await UpdateCoreAsync(src, srcScript, log, busyKind ?? BusyKindAsync, startWaitSeconds).ConfigureAwait(false);
        }
        finally
        {
            // While a journal remains the staging folder holds what its undo moved back into it.
            if (!File.Exists(SwapJournalPath)) DeleteQuietly(StagePath);
            Volatile.Write(ref _updating, 0);
        }
    }

    private static async Task<UpdateReport> UpdateCoreAsync(string src, string srcScript, Action<string>? log,
        Func<LocalAgentConfig?, int, Task<string?>> busyKind, int startWaitSeconds)
    {
        var c = ReadConfig();
        int port = c?.ListenPort ?? ServerAddress.DefaultAgentPort;
        string? shipped = AgentBuild.IdentityCached(src);
        bool same = shipped is not null && shipped == AgentBuild.IdentityCached(Root);
        bool wasRunning = FindRunning(port).Running;
        if (same && !wasRunning)
        {
            log?.Invoke(L.T("core.deploy.updateNothing"));
            return new UpdateReport(false, false, false, Array.Empty<string>());
        }

        // Stopping an agent in the middle of a job leaves a stack cut in half, so a running agent is
        // ASKED, and one that cannot be asked — it does not answer, a 401, a config without a token — is
        // not stopped either: the admin can stop it on the Server page and update again. Asked twice:
        // before the minute the self-test takes, so a busy agent is refused at once, and again right
        // before the stop, which is the answer that counts. A process that is there but silent is never
        // taken for an idle one.
        async Task RefuseWhileBusyAsync()
        {
            if (!FindRunning(port).Running) return;
            // Its restart could not be seen to work: the start is watched on 127.0.0.1 alone.
            if (!AnswersOnLoopback(c)) throw new InvalidOperationException(L.T("core.deploy.updateListenHost", new { listen = c?.Listen ?? "" }));
            string? kind;
            try { kind = await busyKind(c, port).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Info($"local agent update: the job probe failed: {ex.Message}");
                throw new InvalidOperationException(L.T("core.deploy.updateCannotTell", new { detail = ex.Message }));
            }
            if (kind is not null) throw new InvalidOperationException(L.T("core.deploy.updateJobRunning", new { kind }));
        }
        await RefuseWhileBusyAsync().ConfigureAwait(false);

        string stagedScript = Path.Combine(StagePath, AgentBuild.ScriptName);
        string stagedPayloads = Path.Combine(StagePath, AgentBuild.PayloadsName);
        if (same) log?.Invoke(L.T("core.deploy.updateRestartOnly"));
        else
        {
            var python = ReadPython() ?? FindPython() ?? throw new InvalidOperationException(L.T("core.deploy.noPython"));
            log?.Invoke(L.T("core.deploy.updateStaging", new { dir = StagePath }));
            try
            {
                ResetDir(StagePath);
                File.Copy(srcScript, stagedScript, overwrite: true);
                string srcPayloads = Path.Combine(src, AgentBuild.PayloadsName);
                if (Directory.Exists(srcPayloads)) CopyTree(srcPayloads, stagedPayloads);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(L.T("core.deploy.updateFolderBusy", new { dir = StagePath, error = ex.Message }));
            }
            log?.Invoke(L.T("core.deploy.selftest"));
            var (code, output) = Run(python.Exe, $"\"{stagedScript}\" --selftest", StagePath, timeoutMs: 900_000);
            Log.Info($"local agent update: selftest of the staged agent exit={code}\n{Tail(output, 2000)}");
            if (code != 0) throw new InvalidOperationException(L.T("core.deploy.selftestFailed", new { tail = Tail(output, 600) }));
            log?.Invoke(L.T("core.deploy.selftestOk"));
        }

        // The agent as it is NOW: one that was started while the self-test ran is asked and stopped like
        // one that ran from the beginning, and it is started again afterwards.
        bool running = FindRunning(port).Running;
        if (running)
        {
            await RefuseWhileBusyAsync().ConfigureAwait(false);
            Stop(port, log);
            if (FindRunning(port).Running || await IsHealthyAsync(port, 800).ConfigureAwait(false))
                throw new InvalidOperationException(L.T("core.deploy.updateStopFailed", new { port }));
        }
        bool restart = wasRunning || running;

        var carried = new List<string>();
        if (!same)
        {
            log?.Invoke(L.T("core.deploy.updateReplacing", new { dir = PreviousPath }));
            try
            {
                static void Move(string from, string to, bool isDir)
                {
                    JournalMove(from, to, isDir);
                    MoveRetry(from, to, isDir);
                }
                string previousPayloads = Path.Combine(PreviousPath, AgentBuild.PayloadsName);
                // The generation kept so far goes only now, once this update is certain to replace it: a
                // refused update (a job, a failed self-test, an agent that will not stop) leaves it alone.
                ResetDir(PreviousPath);
                // Payloads are replaced only when the build ships some, as at an install.
                bool newPayloads = Directory.Exists(stagedPayloads);
                Move(ScriptPath, Path.Combine(PreviousPath, AgentBuild.ScriptName), false);
                if (newPayloads && Directory.Exists(PayloadsDir)) Move(PayloadsDir, previousPayloads, true);
                Move(stagedScript, ScriptPath, false);
                if (newPayloads)
                {
                    Move(stagedPayloads, PayloadsDir, true);
                    var kept = CarryBoxPayloads(previousPayloads, PayloadsDir, s => Log.Info("local agent update: not kept: " + s), JournalMove);
                    carried.AddRange(kept.Select(m => Path.GetRelativePath(PayloadsDir, m.To).Replace('\\', '/')));
                    if (kept.Count > 0) log?.Invoke(L.T("core.deploy.keptPayloads", new { list = string.Join(", ", carried) }));
                }
                // Every move is made. Under its other name the journal is no longer a cut update's.
                File.Move(SwapJournalPath, SwappedJournalPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("local agent update: replacing the files failed", ex);
                string? undo = UndoMoves(SwapJournalPath);
                string old = undo is null && restart ? await StartOldAsync(port, startWaitSeconds).ConfigureAwait(false) : "";
                throw new InvalidOperationException(undo is not null
                    ? L.T("core.deploy.updateUndoFailed", new { detail = ex.Message, undo, dir = PreviousPath })
                    : old.Length > 0 ? L.T("core.deploy.updateReplaceFailedOldDown", new { detail = ex.Message, old })
                    : L.T("core.deploy.updateReplaceFailed", new { detail = ex.Message }));
            }
        }

        bool runsAgain = false;
        if (restart)
        {
            try
            {
                await StartAsync(port, log, startWaitSeconds).ConfigureAwait(false);
                runsAgain = true;
            }
            catch (Exception ex)
            {
                Log.Error("local agent update: the agent did not start", ex);
                if (same) throw new InvalidOperationException(L.T("core.deploy.updateRestartFailed", new { detail = ex.Message }));
                Stop(port);  // whatever the failed start left behind
                // Under its first name again BEFORE the undo begins: an undo the launcher does not live to
                // finish, or that a held file stops, is then an update cut short like any other — picked
                // up when the launcher next looks at this agent.
                try { MoveRetry(SwappedJournalPath, SwapJournalPath, false); }
                catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException)
                {
                    // Held by another program for longer than a moment: an undo begun from a journal that
                    // cannot be written again without the line just undone is one nothing could finish.
                    // The swap stays whole — the shipped files in place, the ones they replaced kept.
                    Log.Error("local agent update: the journal could not take its first name, the swap is left in place", e2);
                    throw new InvalidOperationException(L.T("core.deploy.updateStartFailedKept", new { detail = ex.Message, dir = PreviousPath }));
                }
                string? undo = UndoMoves(SwapJournalPath);
                string old = undo is null ? await StartOldAsync(port, startWaitSeconds).ConfigureAwait(false) : "";
                throw new InvalidOperationException(undo is not null
                    ? L.T("core.deploy.updateUndoFailed", new { detail = ex.Message, undo, dir = PreviousPath })
                    : old.Length > 0 ? L.T("core.deploy.updateStartFailedOldDown", new { detail = ex.Message, old })
                    : L.T("core.deploy.updateStartFailed", new { detail = ex.Message }));
            }
        }
        else log?.Invoke(L.T("core.deploy.updateLeftStopped"));
        try { File.Delete(SwappedJournalPath); } catch (Exception ex) { Log.Info("local agent update: " + ex.Message); }
        log?.Invoke(L.T("core.deploy.updateDone"));
        return new UpdateReport(!same, running, runsAgain, carried);
    }

    /// <summary>The job the agent on this PC is running — its kind, or null for none — asked with the
    /// config's own token (<c>GET /status</c>, key <c>busy</c>) at the address the agent answers on.
    /// Throws when it cannot be told: no token in the config, a 401 (the process runs with another
    /// token), no answer.</summary>
    private static async Task<string?> BusyKindAsync(LocalAgentConfig? c, int port)
    {
        if (c is null || c.Token.Length == 0) throw new InvalidOperationException(L.T("backend.localAgent.noToken"));
        await using var a = new AgentClient(new ServerConfig(c.Host, 22, "root", "", c.Token, port, "direct"));
        await a.ConnectAsync().ConfigureAwait(false);
        var st = await a.StatusAsync().ConfigureAwait(false);
        if (st.ValueKind != JsonValueKind.Object || !st.TryGetProperty("busy", out var busy) || busy.ValueKind != JsonValueKind.Object)
            return null;
        return busy.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "?" : "?";
    }

    /// <summary>Start the agent whose files an undo put back; "" when it answers, else why it does not.</summary>
    private static async Task<string> StartOldAsync(int port, int startWaitSeconds)
    {
        try
        {
            await StartAsync(port, s => Log.Info("local agent update: previous agent: " + s), startWaitSeconds).ConfigureAwait(false);
            return "";
        }
        catch (Exception ex)
        {
            Log.Error("local agent update: the previous agent did not start either", ex);
            return ex.Message;
        }
    }

    /// <summary>A rename inside the agent folder, tried for three seconds: an antivirus or the indexer
    /// holds a file it has just seen for a moment, and Windows renames nothing that is open elsewhere.</summary>
    private static void MoveRetry(string from, string to, bool isDir)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (isDir) Directory.Move(from, to); else File.Move(from, to);
                return;
            }
            catch (Exception ex) when (attempt < 15 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>One file operation, tried for three seconds like <see cref="MoveRetry"/>.</summary>
    private static void RetryIo(Action act)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                act();
                return;
            }
            catch (Exception ex) when (attempt < 15 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>Both journals of an update and the copy one is rewritten through; what will not go is
    /// logged and left for the caller to see.</summary>
    private static void DeleteJournals()
    {
        foreach (var path in new[] { SwapJournalPath, SwapJournalPath + ".tmp", SwappedJournalPath })
        {
            if (!File.Exists(path)) continue;
            try { RetryIo(() => File.Delete(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"local agent install: {path} was left behind: {ex.Message}");
            }
        }
    }

    /// <summary>An empty folder at <paramref name="dir"/>, whatever was there.</summary>
    private static void ResetDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }

    private static void DeleteQuietly(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { Log.Info($"local agent: {dir} was left behind: {ex.Message}"); }
    }

    /// <summary>The one comment line <see cref="MergeConfig(string, string?)"/> puts above the kept keys.</summary>
    public const string KeptConfigHeader = "# Kept from the previous configuration (an earlier install, Agent settings or hand edits):";

    /// <summary>A config key as the agent accepts it (<c>parse_config_text</c>).</summary>
    private static readonly System.Text.RegularExpressions.Regex ConfigKeyRe = new("^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>The key of one config line, read exactly as <see cref="ReadConfig"/> reads it; null for a
    /// comment, a blank line, a line without '=' or a key the agent would not accept.</summary>
    private static string? ConfigLineKey(string raw)
    {
        string line = raw.Trim();
        if (line.Length == 0 || line[0] == '#') return null;
        int eq = line.IndexOf('=');
        if (eq < 0) return null;
        string k = line[..eq].Trim();
        return ConfigKeyRe.IsMatch(k) ? k : null;
    }

    /// <summary>The config a (re-)install writes: <paramref name="rendered"/> (the install form,
    /// <see cref="AgentInstallPlan.RenderConfig"/>) followed — under the one line
    /// <see cref="KeptConfigHeader"/> — by every KEY=VALUE line of the <paramref name="existing"/> file whose
    /// key the rendered text does not name: its FIRST occurrence (the agent's <c>--config</c> reading is
    /// first-wins), written as it was spelled there (trimmed; quotes and blanks around '=' kept, the agent
    /// parses them the same way), in the file's order. Comments, blank lines, non-pairs and bad keys are
    /// dropped — the previous header included, so a merge of a merge adds nothing. A key the form renders,
    /// even SET AND EMPTY, is the form's: its old line never comes back behind the new one. The keys the
    /// form leaves out are therefore kept — a blank MySQL root password or Flask secret key, the provision
    /// / txt-fix modes, <c>GIO_KEEP_ENV</c> when the option is not stated and, while it is on, the bind
    /// IP, the MUIP sign key and both secrets whatever the form holds —, the same "what is not sent keeps
    /// the box's value" rule install_agent.sh applies. Null / empty existing, or nothing to keep: the
    /// rendered text unchanged, no header. A BOM and CRLF in the old file are tolerated; the output uses
    /// LF. Pure — no I/O.</summary>
    public static string MergeConfig(string rendered, string? existing) => MergeConfig(rendered, existing, out _);

    /// <summary><see cref="MergeConfig(string, string?)"/>, also naming the keys it kept (for the install log —
    /// the names only: a kept value may hold a proxy password or one of the stack secrets).</summary>
    public static string MergeConfig(string rendered, string? existing, out IReadOnlyList<string> keptKeys)
    {
        rendered ??= "";
        var managed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rendered.Split('\n'))
            if (ConfigLineKey(raw) is { } k) managed.Add(k);
        var keys = new List<string>();
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(existing))
            foreach (var raw in existing.TrimStart('﻿').Split('\n'))
            {
                if (ConfigLineKey(raw) is not { } k || managed.Contains(k) || !seen.Add(k)) continue;
                keys.Add(k);
                lines.Add(raw.Trim());
            }
        keptKeys = keys;
        if (keys.Count == 0) return rendered;
        var sb = new System.Text.StringBuilder(rendered);
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
        sb.Append(KeptConfigHeader).Append('\n');
        foreach (var l in lines) sb.Append(l).Append('\n');
        return sb.ToString();
    }

    /// <summary>The config file on this PC, parsed like the agent parses it (<see cref="ParseConfig"/>); null
    /// when there is none (or it cannot be read — logged, never thrown: the start screen calls this on
    /// every init).</summary>
    public static LocalAgentConfig? ReadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            return ParseConfig(File.ReadAllText(ConfigPath));
        }
        catch (Exception ex)
        {
            Log.Error("reading the local agent config (non-fatal)", ex);
            return null;
        }
    }

    /// <summary>A config text parsed the way the agent on this PC reads its file (<see cref="LocalAgentConfig"/>): a
    /// BOM tolerated, '#' comments and blank lines skipped, blanks around '=' trimmed, matching surrounding
    /// quotes stripped, keys limited to <c>[A-Za-z_][A-Za-z0-9_]*</c>, the FIRST occurrence of a repeated
    /// key wins — whatever its letter case: the agent puts the values into the environment of Windows, where
    /// <c>gio_dir_16</c> and <c>GIO_DIR_16</c> are one name. Pure; never throws.</summary>
    public static LocalAgentConfig ParseConfig(string? text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // File.ReadAllText already drops a UTF-8 BOM; the explicit trim covers a text that still has one.
        foreach (var raw in (text ?? "").TrimStart('\uFEFF').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string k = line[..eq].Trim(), v = line[(eq + 1)..].Trim();
            if (v.Length >= 2 && v[0] == v[^1] && v[0] is '"' or '\'') v = v[1..^1];
            if (!ConfigKeyRe.IsMatch(k)) continue;
            // FIRST occurrence wins: apply_config_file only sets a key the environment lacks.
            if (!d.ContainsKey(k)) d[k] = v;
        }
        return new LocalAgentConfig(d);
    }

    /// <summary>The text of the config file on this PC, null when there is none. A file another program
    /// holds is waited for like any file of the agent (<see cref="RetryIo"/>); one that still cannot be
    /// read throws its <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> — the caller
    /// decides what an unreadable configuration means.</summary>
    private static string? ReadConfigText()
    {
        string? text = null;
        RetryIo(() => text = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null);
        return text;
    }

    /// <summary><see cref="ReadConfigText"/> as an install reads it, before anything is written: a file that
    /// is there but still cannot be read refuses the install (<c>core.deploy.configUnreadable</c>) — merged
    /// over nothing, the re-install would drop every folder and setting the agent has.</summary>
    private static string? ReadConfigTextForInstall()
    {
        try { return ReadConfigText(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(L.T("core.deploy.configUnreadable", new { path = ConfigPath, error = ex.Message }));
        }
    }

    /// <summary>The config file on this PC as an install reads it (<see cref="ParseConfig"/>): null when there is
    /// none; a file another program holds is waited for, and one that still cannot be read throws the
    /// install's own refusal, <c>core.deploy.configUnreadable</c> — never a config read as empty, which would
    /// name another token than the one the running agent answers to.</summary>
    public static LocalAgentConfig? ReadConfigForInstall() => ReadConfigTextForInstall() is { } text ? ParseConfig(text) : null;

    /// <summary>Spikes only: the drive type <see cref="DriveTypeOf"/> answers for a drive letter, in place of
    /// the system's (like <see cref="RootOverride"/>). Null = the system's answer.</summary>
    public static Func<char, int>? DriveTypeOverride { get; set; }

    /// <summary>The type of a drive of this PC, by GetDriveTypeW's numbers: 0 unknown, 1 no such drive
    /// (no root), 2 removable, 3 fixed, 4 network, 5 CD / DVD, 6 RAM disk. 0 on any failure; never throws.</summary>
    public static int DriveTypeOf(char letter)
    {
        try { return DriveTypeOverride?.Invoke(letter) ?? (int)new DriveInfo(letter + ":\\").DriveType; }
        catch { return 0; }
    }

    /// <summary>Why the agent on this PC could not use <paramref name="dir"/> as a stack folder of
    /// <paramref name="version"/> because of its drive, or null: the drive types the agent's own relocation
    /// refuses (<c>_WIN_DRIVE_REFUSED</c>) — no such drive, a network drive (a mapped letter), a CD / DVD
    /// drive, a RAM disk. Only a full path on a drive letter is judged
    /// (<see cref="AgentInstallPlan.IsWindowsDrivePath"/>); anything else is null. Never throws.</summary>
    public static string? DriveProblem(string version, string dir)
    {
        if (!AgentInstallPlan.IsWindowsDrivePath(dir)) return null;
        char letter = char.ToUpperInvariant(dir[0]);
        string? why = DriveTypeOf(letter) switch
        {
            1 => L.T("core.deploy.drive.none"),
            4 => L.T("core.deploy.drive.network"),
            5 => L.T("core.deploy.drive.cd"),
            6 => L.T("core.deploy.drive.ram"),
            _ => null,
        };
        return why is null ? null : L.T("core.deploy.dirDriveType", new { version, dir, drive = letter + ":", why });
    }

    /// <summary>Why the agent on this PC could not use <paramref name="dir"/> as a stack folder of
    /// <paramref name="version"/>, or null: not a full path on a drive letter (<c>core.deploy.dirNotLocal</c>), or a
    /// drive it refuses (<see cref="DriveProblem"/>). Null for a blank folder. Never throws.</summary>
    public static string? FolderProblem(string version, string? dir)
    {
        string d = (dir ?? "").Trim();
        if (d.Length == 0) return null;
        return AgentInstallPlan.IsWindowsDrivePath(d) ? DriveProblem(version, d) : L.T("core.deploy.dirNotLocal", new { version, dir = d });
    }

    /// <summary>What stands at a stack folder of this PC — the agent's <c>relocate_target_kind</c>: a blank
    /// name or nothing at that name = <see cref="FolderKind.Absent"/>; a folder without an entry = Empty;
    /// one that holds a <c>docker-compose.yml.tmpl</c> or <c>docker-compose.yml</c> file = Stack; a file, a
    /// folder with other files, a link to a folder that is not there, a folder that cannot be listed =
    /// Other. A network path — UNC, or a drive letter of a network drive — is <see cref="FolderKind.Unknown"/>
    /// and NOT touched: a share that is not connected would hold the caller for the network's timeout.
    /// Never throws.</summary>
    public static FolderKind FolderKindOf(string? dir)
    {
        string d = (dir ?? "").Trim();
        if (d.Length == 0) return FolderKind.Absent;
        if (d.StartsWith(@"\\", StringComparison.Ordinal) || d.StartsWith("//", StringComparison.Ordinal)) return FolderKind.Unknown;
        if (d.Length >= 2 && d[1] == ':' && char.IsAsciiLetter(d[0]) && DriveTypeOf(char.ToUpperInvariant(d[0])) == 4) return FolderKind.Unknown;
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(d); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return FolderKind.Absent; }
            if (!attributes.HasFlag(FileAttributes.Directory) || !Directory.Exists(d)) return FolderKind.Other;
            if (!Directory.EnumerateFileSystemEntries(d).Any()) return FolderKind.Empty;
            if (File.Exists(Path.Combine(d, "docker-compose.yml.tmpl")) || File.Exists(Path.Combine(d, "docker-compose.yml")))
                return FolderKind.Stack;
            return FolderKind.Other;
        }
        catch (Exception) { return FolderKind.Other; }
    }

    /// <summary><see cref="FolderKindOf"/> as the page reads it: "none" for a blank folder name, else
    /// "absent", "empty", "stack", "other" or "unknown". Never throws.</summary>
    public static string FolderKindName(string? dir) =>
        string.IsNullOrWhiteSpace(dir) ? "none" : FolderKindOf(dir).ToString().ToLowerInvariant();

    /// <summary>UTF-8 that skips the bytes it cannot decode — how the agent opens a stack's <c>.env</c>
    /// (<c>errors="ignore"</c>).</summary>
    private static readonly System.Text.Encoding EnvEncoding = System.Text.Encoding.GetEncoding(
        System.Text.Encoding.UTF8.CodePage, System.Text.EncoderFallback.ReplacementFallback, new System.Text.DecoderReplacementFallback(""));

    /// <summary>A server stack's own <c>.env</c>, read by the agent's <c>read_env</c> rule — which is not
    /// the config rule of <see cref="ReadConfig"/>: every line that holds a '=' and does not start with
    /// '#' is a pair, whatever its key looks like; the blanks around the key and around the value are
    /// dropped, then the double and the single quotes around the value; a BOM is tolerated; a line ends
    /// at LF, CR or CRLF; and the LAST line of a repeated key wins. Empty for a blank folder name, a
    /// folder without a <c>.env</c> and a file that cannot be read (logged, never thrown: the start
    /// screen's init gets here). The pairs include the stack's passwords: a caller takes the value it
    /// needs and neither logs nor hands on the rest.</summary>
    public static IReadOnlyDictionary<string, string> ReadStackEnv(string? stackDir)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(stackDir)) return d;
        try
        {
            string path = Path.Combine(stackDir, ".env");
            if (!File.Exists(path)) return d;
            foreach (var raw in EnvEncoding.GetString(File.ReadAllBytes(path)).Split('\n', '\r'))
            {
                string line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                d[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"').Trim('\'');
            }
        }
        catch (Exception ex) { Log.Info($"reading the .env of the stack in {stackDir} (non-fatal): {ex.Message}"); }
        return d;
    }

    /// <summary><c>OUTER_IP</c> of the stack in <paramref name="stackDir"/> — the address its compose file
    /// publishes every port on —, trimmed; "" when <see cref="ReadStackEnv"/> finds none.</summary>
    public static string StackOuterIp(string? stackDir) =>
        ReadStackEnv(stackDir).TryGetValue("OUTER_IP", out var ip) ? ip.Trim() : "";

    /// <summary>Can this PC bind a socket to <paramref name="ip"/>? The agent's <c>_bindable</c>: a UDP
    /// socket bound to that address on a port the system picks and closed at once — nothing is sent. An
    /// address no interface of this PC has (the router's public one, another machine's) is refused, as
    /// docker refuses to publish a port on it. Only an IPv4 address written in full is tried: the agent
    /// binds an IPv4 socket, and a shorthand ("192.168.1") is no address docker takes.</summary>
    public static bool CanBind(string? ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork
            || address.ToString() != ip)
            return false;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(address, 0));
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>
    /// The PATH a program started now should get: the machine's <c>Path</c> (the registry's, environment names
    /// expanded), then the user's, then every entry of <paramref name="current"/> — this process's PATH, which was
    /// fixed when the launcher started — that is not in yet. Each entry once, letter case and a separator at its end
    /// not counted; empty entries dropped. Pure (the expansion reads this process's environment).
    /// </summary>
    public static string FreshPath(string? machine, string? user, string? current)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        void Add(string? list, bool expand)
        {
            foreach (var raw in (list ?? "").Split(';'))
            {
                string entry = raw.Trim();
                if (entry.Length == 0) continue;
                if (expand) entry = Environment.ExpandEnvironmentVariables(entry);
                string key = entry.TrimEnd('\\', '/');
                if (seen.Add(key.Length > 0 ? key : entry)) parts.Add(entry);
            }
        }
        Add(machine, true);
        Add(user, true);
        Add(current, false);
        return string.Join(";", parts);
    }

    /// <summary><see cref="FreshPath"/> with the registry's two <c>Path</c> values as Windows holds them now and this
    /// process's PATH. A value that cannot be read counts as empty.</summary>
    public static string FreshPathNow()
    {
        static string? Read(RegistryKey root, string sub)
        {
            try
            {
                using var k = root.OpenSubKey(sub, writable: false);
                return k?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }
        return FreshPath(Read(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
            Read(Registry.CurrentUser, "Environment"), Environment.GetEnvironmentVariable("PATH"));
    }

    /// <summary>Start the agent hidden with <c>pythonw</c> (stdout/stderr go to <see cref="LogPath"/>
    /// through the agent's own <c>--log</c>). Returns the pid. Idempotent: a healthy running agent is
    /// left alone. /health is asked every quarter of a second until it answers, for
    /// <paramref name="waitSeconds"/> (a minute by default: the agent first puts its house in order —
    /// its state file, an archive waiting in its payloads folder); a process that ends earlier ends the
    /// wait at once. An update that was cut short is undone first; while its journal remains — a file
    /// is held — the files in place are neither the set that was installed nor the shipped one, and
    /// nothing is started from them.</summary>
    public static async Task<int> StartAsync(int port, Action<string>? log = null, int waitSeconds = 60)
    {
        // Off the caller's thread: the start screen calls this on the message thread, and the files may
        // be in another thread's hands for a moment.
        await Task.Run(() => RecoverCutUpdate(wait: true)).ConfigureAwait(false);
        if (File.Exists(SwapJournalPath)) throw new InvalidOperationException(L.T("core.deploy.updateCutPending", new { dir = Root }));
        if (!IsInstalled) throw new InvalidOperationException(L.T("core.deploy.notInstalledLocally"));
        var (running, pid) = RunningPid();
        if (running && await IsHealthyAsync(port).ConfigureAwait(false)) { log?.Invoke(L.T("core.deploy.alreadyRunning")); return pid; }
        var python = ReadPython() ?? FindPython() ?? throw new InvalidOperationException(L.T("core.deploy.noPython"));
        var psi = new ProcessStartInfo(python.ExeW)
        {
            Arguments = $"\"{ScriptPath}\" --config \"{ConfigPath}\" --log \"{LogPath}\"",
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // The PATH Windows holds now, not the one this launcher started with: a program installed since — Docker
        // Desktop for this user puts its folder on the user's PATH — is found by the agent at once.
        psi.Environment["PATH"] = FreshPathNow();
        var p = Process.Start(psi) ?? throw new InvalidOperationException(L.T("core.deploy.startFailed"));
        File.WriteAllText(PidPath, p.Id.ToString());
        log?.Invoke(L.T("core.deploy.started", new { pid = p.Id }));
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(waitSeconds);
        do
        {
            await Task.Delay(250).ConfigureAwait(false);
            if (p.HasExited) throw new InvalidOperationException(L.T("core.deploy.exitedEarly", new { code = p.ExitCode, log = LogPath }));
            if (await IsHealthyAsync(port).ConfigureAwait(false)) return p.Id;
        }
        while (DateTime.UtcNow < until);
        throw new InvalidOperationException(L.T("core.deploy.noHealth", new { port, log = LogPath }));
    }

    /// <summary>Stop the agent on this PC — the one the pid file names, or, when that file is gone or
    /// stale, the python process LISTENING on <paramref name="port"/>.
    /// Kills the whole tree, then waits (≤ 3 s) until /health stops answering, so a StartAsync right
    /// after never races the dying process for the port.</summary>
    public static void Stop(int port, Action<string>? log = null)
    {
        var (running, pid) = FindRunning(port);
        if (!running)
        {
            log?.Invoke(L.T("core.deploy.notRunning"));
            try { File.Delete(PidPath); } catch { }
            return;
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            log?.Invoke(L.T("core.deploy.stopped"));
        }
        catch (Exception ex) { Log.Info($"local agent stop (pid {pid}): {ex.Message}"); }
        for (int i = 0; i < 12; i++)
        {
            if (!IsHealthyAsync(port, 500).GetAwaiter().GetResult()) break;
            Thread.Sleep(250);
        }
        try { File.Delete(PidPath); } catch { }
    }

    /// <summary>Stop on the port the config names (18080 when there is no config).</summary>
    public static void Stop(Action<string>? log = null) => Stop(ReadConfig()?.ListenPort ?? ServerAddress.DefaultAgentPort, log);

    /// <summary>(running, pid) from the pid file, verified against a live python process.</summary>
    public static (bool Running, int Pid) RunningPid()
    {
        try
        {
            if (!File.Exists(PidPath)) return (false, 0);
            if (!int.TryParse(File.ReadAllText(PidPath).Trim(), out int pid)) return (false, 0);
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return (false, pid);
            string name = p.ProcessName.ToLowerInvariant();
            return (name.Contains("python"), pid);
        }
        catch { return (false, 0); }
    }

    /// <summary>The agent process on this PC whether or not WE started it: the pid file first, else the
    /// python process listening on <paramref name="port"/> (the agent writes that file itself only when it
    /// runs with <c>--config</c>; one started otherwise, or whose file was deleted, would read as "installed,
    /// stopped" — a second start then dies on the bound port). Same "is it python" test as
    /// <see cref="RunningPid"/>.</summary>
    public static (bool Running, int Pid) FindRunning(int port)
    {
        var byFile = RunningPid();
        if (byFile.Running) return byFile;
        int? pid = ListeningPid(port);
        if (pid is null || pid.Value <= 0) return (false, 0);
        try
        {
            using var p = Process.GetProcessById(pid.Value);
            if (p.HasExited) return (false, 0);
            return (p.ProcessName.ToLowerInvariant().Contains("python"), pid.Value);
        }
        catch { return (false, 0); }
    }

    /// <summary>Owner pid of the TCP socket LISTENING on <paramref name="port"/> (IPv4 and IPv6 tables),
    /// or null. <c>netstat -ano</c> is shelled out unelevated — the repo's "no new package" rule
    /// (GameProcessWatcher does the same with PowerShell as a last tier). A listening row is recognised
    /// by its foreign address ending in ":0" as well as by the state word, so a localised Windows whose
    /// netstat prints the state in another language still answers.</summary>
    public static int? ListeningPid(int port)
    {
        string suffix = ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var proto in new[] { "tcp", "tcpv6" })
        {
            Proc.Result res;
            try { res = Proc.Run("netstat", "-ano", "-p", proto); }
            catch (Exception ex) { Log.Info($"netstat -p {proto}: {ex.Message}"); continue; }
            if (!res.Ok) continue;
            foreach (var raw in res.Out.Split('\n'))
            {
                var cols = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 5 || !cols[0].StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                if (!cols[1].EndsWith(suffix, StringComparison.Ordinal)) continue;
                bool listening = cols[3].Contains("LISTEN", StringComparison.OrdinalIgnoreCase)
                    || cols[2].EndsWith(":0", StringComparison.Ordinal);
                if (!listening) continue;
                if (int.TryParse(cols[^1], out int pid) && pid > 0) return pid;
            }
        }
        return null;
    }

    /// <remarks>ConfigureAwait(false) is load-bearing, not decoration: <see cref="Stop"/> blocks on this
    /// method, and a Stop called straight from the WebView2 message handler runs on the UI
    /// thread. With the continuation posted back to that same (blocked) thread the launcher would
    /// deadlock for good: no repaint, no further rpc reply, every later screen on its spinner
    /// forever. The blocking callers run off the UI thread as well, and
    /// this keeps the method safe to block on from anywhere.</remarks>
    public static async Task<bool> IsHealthyAsync(int port, int timeoutMs = 1500)
    {
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            string body = await http.GetStringAsync($"http://127.0.0.1:{port}/health").ConfigureAwait(false);
            return body.Contains("gio-agent");
        }
        catch { return false; }
    }

    public static bool AutostartEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return k?.GetValue(RunValue) is string s && s.Length > 0;
            }
            catch { return false; }
        }
    }

    /// <summary>Per-user autostart via HKCU\...\Run (no admin; <c>schtasks /sc onlogon</c> would need it).</summary>
    public static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true) ?? throw new InvalidOperationException("HKCU Run key unavailable");
        if (!on) { k.DeleteValue(RunValue, throwOnMissingValue: false); return; }
        var python = ReadPython() ?? FindPython() ?? throw new InvalidOperationException(L.T("core.deploy.noPython"));
        k.SetValue(RunValue, $"\"{python.ExeW}\" \"{ScriptPath}\" --config \"{ConfigPath}\" --log \"{LogPath}\"", RegistryValueKind.String);
    }

    /// <summary>The Run value's command line, or null when there is none.</summary>
    private static string? AutostartCommand()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return k?.GetValue(RunValue) as string;
        }
        catch { return null; }
    }

    /// <summary>The one name the add/show/delete netsh calls share, so they can never drift apart.</summary>
    public static string FirewallRuleName(int port) => $"Relic GIO agent ({port})";

    /// <summary>The game's login port (TCP) and its game port (UDP): what a server other PCs join must let in.</summary>
    public const int GameLoginPort = 21000, GameUdpPort = 21081;

    /// <summary>The rule for the game's login port (any program, any network profile).</summary>
    public const string ServerRuleTcp = "Relic GIO server (21000 TCP)";

    /// <summary>The rule for the game's UDP port (any program, any network profile).</summary>
    public const string ServerRuleUdp = "Relic GIO server (21081 UDP)";

    /// <summary>The rules of the agent on this PC, in the order they are added: the agent's port, then — for a server
    /// other PCs join — the game's two ports.</summary>
    public static IReadOnlyList<string> FirewallRuleNames(int port, bool gamePorts = true) =>
        gamePorts ? new[] { FirewallRuleName(port), ServerRuleTcp, ServerRuleUdp } : new[] { FirewallRuleName(port) };

    private static string NetshExe => Path.Combine(Environment.SystemDirectory, "netsh.exe");
    private static string CmdExe => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>
    /// The arguments of the ONE elevated <c>cmd.exe</c> that writes the rules:
    /// <c>/d /v:off /s /c "&lt;netsh delete&gt; &amp; &lt;netsh add&gt; &amp;&amp; …"</c> — no AutoRun command, no delayed
    /// expansion (a '!' in a path stays a letter), netsh by its full path. The agent's own rule
    /// (<see cref="FirewallRuleName"/>: TCP <paramref name="port"/>, scoped to <paramref name="program"/>, its pythonw;
    /// the program is left out when the path holds a '%' or a '"', which cmd would read) is always written anew: every
    /// rule of that name is deleted — whatever the delete answers — and the rule added for the pythonw of now, so a rule
    /// an earlier Python or install left is repaired and never doubled. With <paramref name="gamePorts"/> TCP 21000 and
    /// UDP 21081 follow for any program, each unless <paramref name="present"/> names it. Every rule is
    /// <c>dir=in action=allow profile=any</c>; the adds are joined by <c>&amp;&amp;</c>. Pure.
    /// </summary>
    public static string FirewallCommandLine(int port, bool gamePorts, string? program, Func<string, bool>? present = null)
    {
        string netsh = "\"" + NetshExe + "\"";
        string prog = (program ?? "").Trim();
        bool scoped = prog.Length > 0 && prog.IndexOfAny(new[] { '%', '"' }) < 0;
        var adds = new List<string>();
        void Add(string name, string protocol, int localPort, bool withProgram) =>
            adds.Add($"{netsh} advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol={protocol} localport={localPort} profile=any"
                + (withProgram && scoped ? $" program=\"{prog}\"" : ""));
        Add(FirewallRuleName(port), "TCP", port, true);
        if (gamePorts)
        {
            if (present?.Invoke(ServerRuleTcp) != true) Add(ServerRuleTcp, "TCP", GameLoginPort, false);
            if (present?.Invoke(ServerRuleUdp) != true) Add(ServerRuleUdp, "UDP", GameUdpPort, false);
        }
        return "/d /v:off /s /c \"" + $"{netsh} advfirewall firewall delete rule name=\"{FirewallRuleName(port)}\" & "
            + string.Join(" && ", adds) + "\"";
    }

    /// <summary>The arguments of the ONE elevated <c>cmd.exe</c> that deletes the rules <paramref name="names"/>: one
    /// <c>netsh advfirewall firewall delete rule</c> per name, each run whatever the one before answered (no AutoRun
    /// command, no delayed expansion). Pure.</summary>
    public static string FirewallRemoveCommandLine(IEnumerable<string> names)
    {
        string netsh = "\"" + NetshExe + "\"";
        return "/d /v:off /s /c \"" + string.Join(" & ", (names ?? Array.Empty<string>()).Select(n => $"{netsh} advfirewall firewall delete rule name=\"{n}\"")) + "\"";
    }

    /// <summary>
    /// The first elevation of the agent on this PC, user-clicked: the inbound allow rules (<see cref="FirewallCommandLine"/>)
    /// in ONE Windows permission prompt — the agent's TCP port scoped to its pythonw, always written anew (a rule left
    /// for another pythonw is repaired) and, with <paramref name="gamePorts"/> (a server other PCs join), TCP 21000 and
    /// UDP 21081 when they are not there yet. Returns null on success, else the error text (a declined prompt is a
    /// normal outcome).
    /// </summary>
    public static string? OpenFirewallPort(int port, bool gamePorts = false)
    {
        try
        {
            var present = new HashSet<string>(gamePorts ? new[] { ServerRuleTcp, ServerRuleUdp }.Where(RuleExists) : Array.Empty<string>(), StringComparer.Ordinal);
            bool several = gamePorts && present.Count < 2;
            var python = ReadPython() ?? FindPython();
            string args = FirewallCommandLine(port, gamePorts, python?.ExeW, present.Contains);
            var psi = new ProcessStartInfo(CmdExe, args)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.SystemDirectory,
            };
            try
            {
                using var p = Process.Start(psi);
                if (p is null) return L.T("core.deploy.firewallFailed", new { code = -1 });
                if (!p.WaitForExit(60_000)) return L.T("core.deploy.firewallTimeout");
                return p.ExitCode == 0 ? null : L.T("core.deploy.firewallFailed", new { code = p.ExitCode });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return L.T(several ? "core.deploy.uacDeclinedRules" : "core.deploy.uacDeclined");
            }
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Spikes only: what <c>netsh advfirewall firewall show rule</c> answers for a rule name — its exit code —
    /// in place of netsh.</summary>
    public static Func<string, int>? FirewallShowOverride { get; set; }

    /// <summary>Is the rule named <paramref name="name"/> there? Read unelevated — showing rules needs no rights — and
    /// decided by netsh's exit code alone (0 = there, 1 = "no rules match"; its text is in the language of Windows).
    /// Never throws.</summary>
    private static bool RuleExists(string name)
    {
        if (FirewallShowOverride is { } fake) return fake(name) == 0;
        try { return Proc.Run(NetshExe, "advfirewall", "firewall", "show", "rule", "name=" + name).Code == 0; }
        catch (Exception ex) { Log.Info($"netsh show rule: {ex.Message}"); return false; }
    }

    /// <summary>Is the inbound rule for the agent's <paramref name="port"/> there (<see cref="RuleExists"/>)?</summary>
    public static bool FirewallRuleExists(int port) => RuleExists(FirewallRuleName(port));

    /// <summary>Are both rules of the game's ports there (<see cref="ServerRuleTcp"/>, <see cref="ServerRuleUdp"/>)?</summary>
    public static bool ServerRulesExist() => RuleExists(ServerRuleTcp) && RuleExists(ServerRuleUdp);

    /// <summary>The rules of the agent on this PC that are there, by name, in <see cref="FirewallRuleNames"/>' order.</summary>
    public static IReadOnlyList<string> FirewallRulesPresent(int port) => FirewallRuleNames(port).Where(RuleExists).ToList();

    /// <summary>The second elevation, the mirror of <see cref="OpenFirewallPort"/>: delete every rule it added that is
    /// there — the agent's port's and the game ports' — in ONE Windows permission prompt
    /// (<see cref="FirewallRemoveCommandLine"/>). User-clicked (the uninstall dialog's checkbox, off by default), never
    /// run from the launcher's own --uninstall-cleanup. Null when none is left, else the error text (a declined prompt
    /// = <c>core.deploy.uacDeclinedRemove</c>, or <c>core.deploy.uacDeclinedRemoveRules</c> naming the rules for more
    /// than one; rules that stayed are named).</summary>
    public static string? RemoveFirewallRule(int port)
    {
        var names = new List<string>();
        try
        {
            names.AddRange(FirewallRulesPresent(port));
            if (names.Count == 0) return null;
            var psi = new ProcessStartInfo(CmdExe, FirewallRemoveCommandLine(names))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.SystemDirectory,
            };
            using var p = Process.Start(psi);
            if (p is null) return L.T("core.deploy.firewallRemoveFailed", new { rules = string.Join(", ", names) });
            if (!p.WaitForExit(60_000)) return L.T("core.deploy.firewallTimeout");
            var left = FirewallRulesPresent(port);
            return left.Count == 0 ? null : L.T("core.deploy.firewallRemoveFailed", new { rules = string.Join(", ", left) });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Its own text: the "was not added" of OpenFirewallPort would tell the admin the opposite
            // of what happened and hide that the inbound rules outlive the agent.
            return names.Count > 1 ? L.T("core.deploy.uacDeclinedRemoveRules", new { rules = string.Join(", ", names) }) : L.T("core.deploy.uacDeclinedRemove");
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// Remove the agent from this PC in the order <c>uninstall_agent.sh</c> uses on Linux: the process
    /// (pid file or listener), the autostart value — only when it points at THIS root, so a spike root
    /// never touches the real one — the firewall rules when asked (the one elevated step; the rules that stay are
    /// named in the report), then every
    /// file under <see cref="Root"/> entry by entry, bottom-up, one retry after a second: a single locked
    /// file (a voice pack a client is being served, an open log) must cost that file alone, not the whole
    /// cleanup, and is reported in the leftovers. Nothing outside the root is ever touched — the docker
    /// stacks the config names are only NAMED in the confirm dialog. Throws only off Windows.
    /// </summary>
    public static UninstallReport Uninstall(int port, bool removeFirewall, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException(L.T("backend.localAgent.windowsOnly"));
        var (wasRunning, _) = FindRunning(port);
        Stop(port, log);
        bool stopped = wasRunning && !FindRunning(port).Running;

        bool autostartRemoved = false;
        try
        {
            string? cmd = AutostartCommand();
            if (cmd is not null && cmd.Contains(ScriptPath, StringComparison.OrdinalIgnoreCase))
            {
                SetAutostart(false);
                autostartRemoved = !AutostartEnabled;
                if (autostartRemoved) log?.Invoke(L.T("core.deploy.autostartOff"));
            }
        }
        catch (Exception ex) { Log.Error("removing the agent autostart value (non-fatal)", ex); }

        bool firewallRemoved = false;
        string? firewallError = null;
        if (removeFirewall && FirewallRulesPresent(port).Count > 0)
        {
            firewallError = RemoveFirewallRule(port);
            firewallRemoved = firewallError is null;
            if (firewallRemoved) log?.Invoke(L.T("core.deploy.firewallRemoved"));
        }
        // Every rule that stays is named: an inbound rule outlives the agent it was added for.
        var keptRules = FirewallRulesPresent(port);
        foreach (var rule in keptRules) log?.Invoke(L.T("core.deploy.firewallRuleKept", new { rule }));

        var leftovers = new List<string>();
        string root = Root;
        if (Directory.Exists(root))
        {
            DeleteTreeEntryByEntry(root, leftovers);
            if (leftovers.Count == 0)
            {
                try { Directory.Delete(root); }
                catch (Exception ex) { leftovers.Add(L.T("core.deploy.fileLeft", new { path = root, error = ex.Message })); }
            }
        }
        bool filesRemoved = !Directory.Exists(root);
        if (filesRemoved) log?.Invoke(L.T("core.deploy.filesRemoved", new { dir = root }));
        else foreach (var l in leftovers) log?.Invoke(l);
        return new UninstallReport(stopped, autostartRemoved, firewallRemoved, firewallError, filesRemoved, leftovers, keptRules);
    }

    /// <summary>Delete <paramref name="dir"/>'s contents one entry at a time (never
    /// <c>Directory.Delete(recursive: true)</c>, which aborts everything at the first locked file),
    /// deepest first, one retry after 1 s per failure; failures are collected, never thrown.</summary>
    private static void DeleteTreeEntryByEntry(string dir, List<string> leftovers)
    {
        string[] subdirs, files;
        try { subdirs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
        catch (Exception ex) { leftovers.Add(L.T("core.deploy.fileLeft", new { path = dir, error = ex.Message })); return; }
        foreach (var sub in subdirs)
        {
            // A junction/symlink is deleted as the LINK, never walked: the hotpatch mirror lives
            // inside this root and is exactly the thing an admin redirects to another drive with
            // `mklink /J`. Recursing into it would delete the target's contents — outside the root
            // this method promises never to leave.
            try
            {
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    TryTwice(sub, leftovers, () => Directory.Delete(sub));
                    continue;
                }
            }
            catch (Exception ex) { leftovers.Add(L.T("core.deploy.fileLeft", new { path = sub, error = ex.Message })); continue; }
            DeleteTreeEntryByEntry(sub, leftovers);
            // A folder that still holds a leftover cannot go, and saying so twice (file + folder) would
            // only pad the report: the file's own line already explains why the folder stayed.
            bool empty;
            try { empty = !Directory.EnumerateFileSystemEntries(sub).Any(); }
            catch { empty = false; }
            if (empty) TryTwice(sub, leftovers, () => Directory.Delete(sub));
        }
        foreach (var f in files)
            TryTwice(f, leftovers, () =>
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            });
    }

    private static void TryTwice(string path, List<string> leftovers, Action del)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { del(); return; }
            catch (DirectoryNotFoundException) { return; }
            catch (FileNotFoundException) { return; }
            catch (Exception ex)
            {
                if (attempt == 1) { Thread.Sleep(1000); continue; }
                Log.Info($"local agent uninstall: {path}: {ex.Message}");
                leftovers.Add(L.T("core.deploy.fileLeft", new { path, error = ex.Message }));
                return;
            }
        }
    }

    private static PythonInfo? ReadPython()
    {
        try
        {
            if (!File.Exists(PythonPath)) return null;
            var lines = File.ReadAllLines(PythonPath);
            if (lines.Length == 0 || !File.Exists(lines[0])) return null;
            string w = lines.Length > 1 && File.Exists(lines[1]) ? lines[1] : lines[0];
            return new PythonInfo(lines[0], w, "");
        }
        catch { return null; }
    }

    private static (int Code, string Output) Run(string exe, string args, string cwd, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PATH"] = FreshPathNow();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {exe}");
        var sb = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, sb.ToString() + "\n(timeout)"); }
        p.WaitForExit();
        return (p.ExitCode, sb.ToString());
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src)) CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
}
