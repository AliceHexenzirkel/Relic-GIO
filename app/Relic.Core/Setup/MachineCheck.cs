using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Relic.Core.Install;
using Relic.Core.Launch;
using Relic.Core.Server;
using Relic.Core.Util;

namespace Relic.Core.Setup;

/// <summary>A server version's folder on this PC that a machine check looks at.</summary>
public sealed record ServerFolder(string Version, string Dir);

/// <summary>What a machine check looks at besides the PC itself: the games folder, the server folders (their drives,
/// what stands there, a folder the agent here could not use), the versions whose shipped starting progress it
/// lists, the tools catalogue (Docker's minimum WSL) and the shipped payloads (null = next to the exe).</summary>
public sealed record MachineCheckRequest
{
    public string GamesRoot { get; init; } = "";
    public IReadOnlyList<ServerFolder> ServerFolders { get; init; } = Array.Empty<ServerFolder>();
    public IReadOnlyList<string> TemplateVersions { get; init; } = ServerVersions.All;
    public SetupToolsCatalog? Tools { get; init; }
    public string? PayloadsRoot { get; init; }
}

/// <summary>What the one CIM query read (<see cref="MachineCheck.ParseCim"/>): the install state of the two optional
/// features (1 = on), the memory Windows sees, whether a hypervisor runs and whether the firmware turned
/// virtualization on. Null = not read.</summary>
public sealed record CimFacts(int? VirtualMachinePlatform, int? SubsystemLinux, long? TotalPhysicalMemory, bool? HypervisorPresent,
    bool? FirmwareVirtualization)
{
    public bool Read => VirtualMachinePlatform is not null || SubsystemLinux is not null || TotalPhysicalMemory is not null
        || HypervisorPresent is not null || FirmwareVirtualization is not null;

    /// <summary>on when a hypervisor runs or the firmware turned virtualization on, off when both are known false,
    /// unknown otherwise. SLAT and the VM monitor extensions read false under a running hypervisor: never used.</summary>
    public string Virtualization => HypervisorPresent == true || FirmwareVirtualization == true ? "on"
        : HypervisorPresent == false && FirmwareVirtualization == false ? "off" : "unknown";

    /// <summary>The Virtual Machine Platform feature on (true), off or absent (false), or not known (null).</summary>
    public bool? VmpOn => VirtualMachinePlatform switch { 1 => true, 2 or 3 => false, _ => null };
}

/// <summary>Docker Desktop on this PC: <see cref="State"/> missing | installed | running (its engine answered),
/// where it is installed and its version (its uninstall entry), the engine's version, whether its licence terms
/// were accepted and whether it starts at sign-in (its settings file; null = not read).</summary>
public sealed record DockerFacts(string State, string? Location, string? Version, string? Engine, bool? Licence, bool? AutoStart)
{
    public static DockerFacts Missing { get; } = new("missing", null, null, null, null, null);
}

/// <summary>The agent on this PC as the agent update sees it: installed, running, the build its files make and the
/// build this launcher ships (null = none).</summary>
public sealed record LocalAgentFacts(bool Installed, bool Running, int Port, string? Build, string? Shipped);

/// <summary>A starting progress a version's shipped payload offers (its manifest's <c>templates</c>).</summary>
public sealed record TemplateFact(string Id, string Label);

/// <summary>A folder the check looked at: its drive ("C:"), the bytes free there (null = not read or not a local
/// drive), why the agent on this PC could not use it (null = it can) and what stands there
/// (<see cref="LocalAgent.FolderKindName"/>).</summary>
public sealed record FolderFacts(string Version, string Dir, string? Drive, long? Free, string? Problem, string Kind);

/// <summary>The Python a check found: the interpreter and its version.</summary>
public sealed record PythonFact(string Exe, string Version);

/// <summary>Everything one machine check found (<see cref="MachineCheck.RunAsync"/>); what could not be read is null
/// or "unknown", never an error.</summary>
public sealed record MachineFacts
{
    public int WindowsBuild { get; init; }
    public bool Windows11 { get; init; }
    public bool Arm { get; init; }
    public string Virtualization { get; init; } = "unknown";
    public bool? VirtualMachinePlatform { get; init; }
    /// <summary>on | old | off | unknown (<see cref="MachineCheck.WslState"/>).</summary>
    public string Wsl { get; init; } = "unknown";
    public string? WslVersion { get; init; }
    public bool? RestartPending { get; init; }
    public DockerFacts Docker { get; init; } = DockerFacts.Missing;
    public PythonFact? Python { get; init; }
    public string? Extractor { get; init; }
    public LocalAgentFacts LocalAgent { get; init; } = new(false, false, ServerAddress.DefaultAgentPort, null, null);
    public IReadOnlyDictionary<string, IReadOnlyList<TemplateFact>> Templates { get; init; } = new Dictionary<string, IReadOnlyList<TemplateFact>>();
    /// <summary>The memory Windows sees, and where that figure came from: cim | memoryStatus.</summary>
    public long? Ram { get; init; }
    public string RamSource { get; init; } = "";
    /// <summary>The memory the PC's modules hold (the firmware's table) — the figure Docker's requirement is
    /// about; null when not read.</summary>
    public long? InstalledRam { get; init; }
    /// <summary>The cap of WSL 2's memory: <c>.wslconfig</c>'s line (wslconfig), else half the memory (default).</summary>
    public long? WslCap { get; init; }
    public string WslCapSource { get; init; } = "default";
    public string? LanIp { get; init; }
    public string UserName { get; init; } = "";
    /// <summary>The drive of Relic's tools folder — where Python, 7zr and Docker Desktop go.</summary>
    public string ToolsDrive { get; init; } = "C:";
    /// <summary>Free bytes per drive ("C:") the check looked at; null = not read.</summary>
    public IReadOnlyDictionary<string, long?> Drives { get; init; } = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
    public string GamesRootDrive { get; init; } = "";
    public IReadOnlyList<FolderFacts> Folders { get; init; } = Array.Empty<FolderFacts>();
}

/// <summary>
/// Quick Start's look at this PC before it sets a server up here: Windows and the processor, virtualization, WSL 2,
/// a restart Windows waits for, Docker Desktop, Python, a 7z extractor, the agent on this PC, the memory and
/// WSL's cap of it, the drives of the folders, the LAN address and a default account name. It only reads: ONE fixed
/// CIM query through powershell.exe (nothing of the caller in it), <c>wsl.exe --version</c>, Docker's own
/// <c>docker.exe version</c>, the Python probes of <see cref="LocalAgent.FindPython"/>, the registry and files.
/// The parsers are pure.
/// </summary>
public static class MachineCheck
{
    /// <summary>The fixed script of the one CIM query: the two optional features WSL 2 needs, the memory and the
    /// hypervisor of Win32_ComputerSystem, the firmware's virtualization switch of Win32_Processor — one JSON line.
    /// It holds no double quote, so it travels on the command line as it is.</summary>
    public const string CimScript =
        "$ErrorActionPreference='SilentlyContinue'; $f=@{}; " +
        "foreach($n in @('VirtualMachinePlatform','Microsoft-Windows-Subsystem-Linux')){ $o=Get-CimInstance -ClassName Win32_OptionalFeature -Filter ('Name='''+$n+''''); if($o){$f[$n]=[int]$o.InstallState} }; " +
        "$cs=Get-CimInstance -ClassName Win32_ComputerSystem; " +
        "$fw=@(Get-CimInstance -ClassName Win32_Processor | ForEach-Object { $_.VirtualizationFirmwareEnabled } | Where-Object { $null -ne $_ }); " +
        "$r=[ordered]@{ vmp=$f['VirtualMachinePlatform']; wsl=$f['Microsoft-Windows-Subsystem-Linux']; ram=$null; hv=$null; fw=$null }; " +
        "if($cs){ $r.ram=[int64]$cs.TotalPhysicalMemory; if($null -ne $cs.HypervisorPresent){ $r.hv=[bool]$cs.HypervisorPresent } }; " +
        "if($fw.Count -gt 0){ $r.fw=[bool](@($fw | Where-Object { $_ }).Count -gt 0) }; " +
        "$r | ConvertTo-Json -Compress";

    /// <summary>
    /// What the CIM query printed: the last line that is a JSON object, with <c>vmp</c> / <c>wsl</c> (install
    /// states), <c>ram</c> (bytes), <c>hv</c> and <c>fw</c> (booleans). A key that is missing, null or of another
    /// type is not read; output that holds no such line reads as nothing. Pure; never throws.
    /// </summary>
    public static CimFacts ParseCim(string? output)
    {
        var none = new CimFacts(null, null, null, null, null);
        string? line = (output ?? "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{') && l.EndsWith('}'));
        if (line is null) return none;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return none;
            int? I(string n) => o.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v) ? v : null;
            long? L64(string n) => o.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long v) && v > 0 ? v : null;
            bool? B(string n) => o.TryGetProperty(n, out var e) ? e.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
            return new CimFacts(I("vmp"), I("wsl"), L64("ram"), B("hv"), B("fw"));
        }
        catch (JsonException) { return none; }
    }

    private static readonly Regex VersionToken = new(@"\b([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)\b");

    /// <summary>
    /// The version <c>wsl.exe --version</c> printed: the first non-empty line must name WSL and carry a version
    /// ("WSL version: 2.3.24.0", in any language). The output is UTF-8 (WSL_UTF8=1) or UTF-16LE (WSL's own default),
    /// told apart by its bytes. Null for anything else — the help text of the WSL Windows ships inbox, which knows
    /// no <c>--version</c>, included. Pure.
    /// </summary>
    public static string? ParseWslVersion(byte[]? output)
    {
        if (output is null || output.Length == 0) return null;
        bool utf16 = (output.Length >= 2 && output[0] == 0xFF && output[1] == 0xFE)
            || (output.Length >= 4 && output[1] == 0 && output[3] == 0);
        string text = utf16 ? Encoding.Unicode.GetString(output) : Encoding.UTF8.GetString(output);
        return ParseWslVersion(text);
    }

    /// <summary><see cref="ParseWslVersion(byte[])"/> on text already decoded. Pure.</summary>
    public static string? ParseWslVersion(string? text)
    {
        string first = (text ?? "").TrimStart('﻿').Replace("\0", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (!first.Contains("WSL", StringComparison.OrdinalIgnoreCase)) return null;
        var m = VersionToken.Match(first);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// WSL 2 as Docker Desktop needs it: off — the Virtual Machine Platform is off; on — it is on and
    /// <c>wsl --version</c> named <paramref name="minWsl"/> or later; old — it is on and the version is older, or
    /// <c>wsl.exe</c> answered without one (the inbox WSL); unknown — the platform's state was not read, or
    /// <c>wsl.exe</c> could not be asked (<paramref name="asked"/> false). Pure.
    /// </summary>
    public static string WslState(bool? vmpOn, bool asked, string? version, string? minWsl)
    {
        if (vmpOn == false) return "off";
        if (vmpOn != true || !asked) return "unknown";
        var v = SetupTools.ParseVersion(version);
        if (v is null) return "old";
        var min = SetupTools.ParseVersion(minWsl);
        return min is null || v >= min ? "on" : "old";
    }

    /// <summary>
    /// The account name a Windows user name gives (Quick Start's prefill): lower-cased, every character outside
    /// a–z, 0–9, '_', '.', '-' dropped, the leading characters dropped up to the first letter or digit, cut to 20.
    /// "" when what is left is shorter than 3, digits only, or starts with a prefix the agent reserves (relic, tpl,
    /// admin, gm) — the agent's own name rule. Pure.
    /// </summary>
    public static string DefaultUserName(string? userName)
    {
        var sb = new StringBuilder();
        foreach (char c in (userName ?? "").ToLowerInvariant())
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-') sb.Append(c);
        string s = sb.ToString();
        int start = 0;
        while (start < s.Length && !char.IsAsciiLetterOrDigit(s[start])) start++;
        s = s[start..];
        if (s.Length > 20) s = s[..20];
        if (s.Length < 3 || s.All(char.IsAsciiDigit)) return "";
        foreach (var p in new[] { "relic", "tpl", "admin", "gm" })
            if (s.StartsWith(p, StringComparison.Ordinal)) return "";
        return s;
    }

    /// <summary>"C:" for a path on a drive letter; the path's root otherwise ("" when it has none).</summary>
    public static string DriveOf(string? path)
    {
        string p = (path ?? "").Trim();
        if (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0])) return char.ToUpperInvariant(p[0]) + ":";
        try { return Path.GetPathRoot(p) ?? ""; } catch (ArgumentException) { return ""; }
    }

    /// <summary>
    /// Look at this PC (see the class). Everything runs off the caller's thread and side by side; a probe that fails
    /// or does not answer in time reads as not known. Never throws for a probe; <paramref name="ct"/> ends the waits.
    /// </summary>
    public static async Task<MachineFacts> RunAsync(MachineCheckRequest request, CancellationToken ct = default)
    {
        request ??= new MachineCheckRequest();
        var tools = request.Tools ?? SetupTools.Load();
        var win = WindowsInfo.Detect();
        var cimTask = Task.Run(() => Safe(() => ParseCim(RunCim(ct)), new CimFacts(null, null, null, null, null), "the CIM query"), ct);
        var wslTask = Task.Run(() => Safe(() => RunWslVersion(ct), (false, (string?)null), "wsl --version"), ct);
        var dockerTask = Task.Run(() => Safe(() => Docker(ct), DockerFacts.Missing, "Docker Desktop"), ct);
        var pythonTask = Task.Run(() => Safe(LocalAgent.FindPython, null, "Python"), ct);
        var extractorTask = Task.Run(() => Safe(LocalAgent.FindExtractor, null, "the extractor"), ct);
        var agentTask = Task.Run(() => Safe(AgentFacts, new LocalAgentFacts(false, false, ServerAddress.DefaultAgentPort, null, null), "the agent on this PC"), ct);
        await Task.WhenAll(cimTask, wslTask, dockerTask, pythonTask, extractorTask, agentTask).ConfigureAwait(false);
        var cim = cimTask.Result;
        var (asked, wslVersion) = wslTask.Result;
        var python = pythonTask.Result;

        long? memoryStatus = TotalMemoryStatus();
        long? ram = cim.TotalPhysicalMemory ?? memoryStatus;
        long? wslCapFile = WslConfig.ReadCap();
        string toolsDrive = DriveOf(SetupTools.ToolsDir) is { Length: > 0 } td ? td : "C:";

        var drives = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        long? FreeOf(string drive)
        {
            if (drive.Length == 0) return null;
            if (drives.TryGetValue(drive, out var known)) return known;
            long? free = null;
            try
            {
                if (drive.Length == 2 && drive[1] == ':' && LocalAgent.DriveTypeOf(drive[0]) is 2 or 3)
                    free = new DriveInfo(drive + "\\").AvailableFreeSpace;
            }
            catch (Exception ex) { Log.Info($"machine check: the free space of {drive} is not known ({ex.Message})"); }
            drives[drive] = free;
            return free;
        }
        FreeOf(toolsDrive);
        string gamesDrive = DriveOf(request.GamesRoot);
        FreeOf(gamesDrive);
        var folders = new List<FolderFacts>();
        foreach (var f in request.ServerFolders ?? Array.Empty<ServerFolder>())
        {
            if (f is null || string.IsNullOrWhiteSpace(f.Dir)) continue;
            string? problem = LocalAgent.FolderProblem(f.Version, f.Dir);
            string drive = DriveOf(f.Dir);
            long? free = problem is null ? FreeOf(drive) : null;
            folders.Add(new FolderFacts(f.Version, f.Dir, drive.Length > 0 ? drive : null, free, problem,
                problem is null ? LocalAgent.FolderKindName(f.Dir) : "unknown"));
        }

        return new MachineFacts
        {
            WindowsBuild = win.Build,
            Windows11 = win.IsWindows11,
            Arm = RuntimeInformation.OSArchitecture == Architecture.Arm64,
            Virtualization = cim.Virtualization,
            VirtualMachinePlatform = cim.VmpOn,
            Wsl = WslState(cim.VmpOn, asked, wslVersion, tools.Docker?.MinWsl),
            WslVersion = wslVersion,
            RestartPending = RestartPending(),
            Docker = dockerTask.Result,
            Python = python is null ? null : new PythonFact(python.Exe, python.Version),
            Extractor = extractorTask.Result,
            LocalAgent = agentTask.Result,
            Templates = Templates(request.TemplateVersions ?? ServerVersions.All, request.PayloadsRoot),
            Ram = ram,
            RamSource = cim.TotalPhysicalMemory is not null ? "cim" : memoryStatus is not null ? "memoryStatus" : "",
            InstalledRam = InstalledMemory(),
            WslCap = wslCapFile ?? (ram is { } r ? r / 2 : null),
            WslCapSource = wslCapFile is not null ? "wslconfig" : "default",
            LanIp = LanIp(),
            UserName = DefaultUserName(Environment.UserName),
            ToolsDrive = toolsDrive,
            Drives = drives,
            GamesRootDrive = gamesDrive,
            Folders = folders,
        };
    }

    private static T Safe<T>(Func<T> probe, T fallback, string what)
    {
        try { return probe(); }
        catch (Exception ex)
        {
            Log.Info($"machine check: {what} was not read ({ex.GetType().Name}: {ex.Message})");
            return fallback;
        }
    }

    private static string PowerShellExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>The wsl.exe of Windows itself (System32), never one a PATH entry puts in front of it.</summary>
    internal static string WslExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    /// <summary>The one CIM query, run now and read (<see cref="ParseCim"/>).</summary>
    internal static CimFacts ReadCim(CancellationToken ct) => ParseCim(RunCim(ct));

    private static string RunCim(CancellationToken ct)
    {
        var r = TimedProcess.Run(PowerShellExe, new[] { "-NoProfile", "-NonInteractive", "-Command", CimScript }, TimeSpan.FromSeconds(15), null, ct);
        if (r.Code is null) Log.Info("machine check: the CIM query did not answer within 15 s");
        return Encoding.UTF8.GetString(r.Stdout);
    }

    /// <summary>(asked, version): whether wsl.exe ran and ended within 10 s, and the version it named.</summary>
    private static (bool, string?) RunWslVersion(CancellationToken ct)
    {
        if (!File.Exists(WslExe)) return (false, null);
        var r = TimedProcess.Run(WslExe, new[] { "--version" }, TimeSpan.FromSeconds(10), new Dictionary<string, string> { ["WSL_UTF8"] = "1" }, ct);
        if (r.Code is null) return (false, null);
        return (true, r.Code == 0 ? ParseWslVersion(r.Stdout) : null);
    }

    /// <summary>A restart Windows waits for: the servicing stack's <c>RebootPending</c> key exists (read unelevated).
    /// Null when it cannot be told.</summary>
    public static bool? RestartPending()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", writable: false);
            return k is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private const string DockerUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Docker Desktop";

    /// <summary>Where Docker Desktop is installed and its version: its uninstall entry for this user (a per-user
    /// install), then the machine's, else the per-user folder itself. Null when none is found.</summary>
    public static (string Location, string? Version)? DockerInstall()
    {
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(DockerUninstallKey, writable: false);
                if (k?.GetValue("InstallLocation") is string loc && loc.Trim().Length > 0 && Directory.Exists(loc.Trim()))
                    return (loc.Trim().TrimEnd('\\'), k.GetValue("DisplayVersion") as string);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        string perUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DockerDesktop");
        return File.Exists(Path.Combine(perUser, "Docker Desktop.exe")) ? (perUser, null) : null;
    }

    /// <summary>Docker's own CLI inside its install folder — never a <c>docker</c> from PATH.</summary>
    public static string DockerCli(string location) => Path.Combine(location, "resources", "bin", "docker.exe");

    /// <summary>The engine's version when <c>docker.exe version</c> answers within <paramref name="timeout"/>; null
    /// otherwise.</summary>
    internal static string? EngineVersion(string location, TimeSpan timeout, CancellationToken ct)
    {
        string cli = DockerCli(location);
        if (!File.Exists(cli)) return null;
        var r = TimedProcess.Run(cli, new[] { "version", "--format", "{{.Server.Version}}" }, timeout, null, ct);
        if (r.Code != 0) return null;
        string v = Encoding.UTF8.GetString(r.Stdout).Trim();
        return v.Length > 0 ? v : "?";
    }

    private static DockerFacts Docker(CancellationToken ct)
    {
        if (DockerInstall() is not { } install) return DockerFacts.Missing;
        string? engine = EngineVersion(install.Location, TimeSpan.FromSeconds(15), ct);
        var (licence, autoStart) = DockerSettings();
        return new DockerFacts(engine is not null ? "running" : "installed", install.Location, install.Version, engine, licence, autoStart);
    }

    /// <summary>Docker Desktop's settings file (<c>%APPDATA%\Docker\settings-store.json</c>): whether it names the
    /// licence terms the user accepted (<c>LicenseTermsVersion</c>) and whether Docker Desktop starts at sign-in
    /// (<c>AutoStart</c>). Null for what the file does not say or when it cannot be read.</summary>
    public static (bool? Licence, bool? AutoStart) DockerSettings(string? path = null)
    {
        string p = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docker", "settings-store.json");
        try
        {
            if (!File.Exists(p)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(p));
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return (null, null);
            bool? licence = o.TryGetProperty("LicenseTermsVersion", out var l) ? l.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) : false;
            bool? auto = o.TryGetProperty("AutoStart", out var a) ? a.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
            return (licence, auto);
        }
        catch (Exception) { return (null, null); }
    }

    private static LocalAgentFacts AgentFacts()
    {
        // Before anything looks at the agent's files: an update that was cut short is undone first.
        LocalAgent.RecoverCutUpdate(wait: true);
        var c = LocalAgent.ReadConfig();
        int port = c?.ListenPort ?? ServerAddress.DefaultAgentPort;
        bool installed = LocalAgent.IsInstalled;
        bool running = installed && LocalAgent.FindRunning(port).Running;
        return new LocalAgentFacts(installed, running, port, AgentBuild.Installed, AgentBuild.Shipped);
    }

    /// <summary>The starting progress each version's shipped payload offers — its manifest's <c>templates</c>, id
    /// and label, in the file's order; a version without a manifest offers none. Never throws.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<TemplateFact>> Templates(IEnumerable<string> versions, string? payloadsRoot = null)
    {
        string root = string.IsNullOrWhiteSpace(payloadsRoot) ? GameAccounts.DefaultPayloadsRoot : payloadsRoot!;
        var result = new Dictionary<string, IReadOnlyList<TemplateFact>>(StringComparer.Ordinal);
        foreach (var v in versions ?? Array.Empty<string>())
        {
            string id = (v ?? "").Trim();
            if (id.Length == 0 || id.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || id.Contains("..")) continue;
            var list = new List<TemplateFact>();
            try
            {
                string path = Path.Combine(root, id, "manifest.json");
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("templates", out var ts) && ts.ValueKind == JsonValueKind.Array)
                        foreach (var t in ts.EnumerateArray())
                        {
                            if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("id", out var tid) || tid.ValueKind != JsonValueKind.String) continue;
                            string tidS = (tid.GetString() ?? "").Trim();
                            if (tidS.Length == 0) continue;
                            string label = t.TryGetProperty("label", out var tl) && tl.ValueKind == JsonValueKind.String ? (tl.GetString() ?? "") : tidS;
                            list.Add(new TemplateFact(tidS, label));
                        }
                }
            }
            catch (Exception ex) { Log.Info($"machine check: the templates of {id} were not read ({ex.Message})"); }
            result[id] = list;
        }
        return result;
    }

    /// <summary>This PC's address on its network: the local end of a UDP socket "connected" to a documentation
    /// address (no packet is sent) — IPv4, not loopback, not link-local; null otherwise.</summary>
    public static string? LanIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(IPAddress.Parse("203.0.113.1"), 9));
            if (s.LocalEndPoint is not IPEndPoint ep) return null;
            var b = ep.Address.GetAddressBytes();
            if (b.Length != 4 || b[0] == 127 || b[0] == 0 || (b[0] == 169 && b[1] == 254)) return null;
            return ep.Address.ToString();
        }
        catch (SocketException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    private static long? TotalMemoryStatus()
    {
        try
        {
            var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref m) && m.ullTotalPhys > 0 ? (long)m.ullTotalPhys : null;
        }
        catch (Exception) { return null; }
    }

    private static long? InstalledMemory()
    {
        try { return GetPhysicallyInstalledSystemMemory(out ulong kb) && kb > 0 ? (long)(kb * 1024) : null; }
        catch (Exception) { return null; }
    }
}

/// <summary>A console program run with a deadline, its output read as bytes (for tools that write UTF-16), the
/// whole process tree killed when the deadline passes. <see cref="Result.Code"/> is null then.</summary>
internal static class TimedProcess
{
    internal readonly record struct Result(int? Code, byte[] Stdout, string Stderr);

    internal static Result Run(string exe, IEnumerable<string> args, TimeSpan timeout, IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + exe);
        try { p.StandardInput.Close(); } catch (IOException) { }
        var stdout = new MemoryStream();
        var outTask = p.StandardOutput.BaseStream.CopyToAsync(stdout);
        var errTask = p.StandardError.ReadToEndAsync();
        bool exited;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            try { p.WaitForExitAsync(cts.Token).GetAwaiter().GetResult(); exited = true; }
            catch (OperationCanceledException) { exited = false; }
        }
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            try { p.WaitForExit(2000); } catch (Exception) { }
        }
        try { outTask.Wait(2000); } catch (Exception) { }
        string err = "";
        try { if (errTask.Wait(2000)) err = errTask.Result; } catch (Exception) { }
        return new Result(exited ? p.ExitCode : null, stdout.ToArray(), err);
    }
}
