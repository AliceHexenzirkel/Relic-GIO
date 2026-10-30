using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Relic.Core.Download;
using Relic.Core.Server;
using Relic.Core.Setup;
using Relic.Core.Util;

/// <summary>
/// Quick Start's look at this PC and at a Linux server, and what it adds — everything checked on its pure halves,
/// stand-ins and temp folders, nothing installed, nothing elevated, no network:
///  - the machine parsers: the one CIM query's line, <c>wsl --version</c> (UTF-8 and UTF-16LE), WSL 2's state, the
///    default account name, Docker Desktop's settings file, the shipped starting progress;
///  - <c>.wslconfig</c>: its memory line read and written (sections, comments, line ends);
///  - the needs: the drives, the memory verdicts and the raise, what blocks a server on this PC, what the run adds;
///  - the two catalogues (<c>config/setup-tools.json</c>, <c>config/server-needs.json</c>), usable only whole;
///  - the agent on this PC: Python's candidates in order (Relic's, the registry's, PATH) with the install manager's
///    automatic install off on every probe and the probe's input closed, the extractor's order, Relic's 7zr written as
///    GIO_7Z, the fresh PATH, the firewall's ONE command line (the agent's rule always written anew) and the rules
///    decided by netsh's exit code, the uninstall naming the rules that stay;
///  - the network: the agent's advertisable-address rule, two services agreeing, the reach check's verdicts, no
///    address of this PC in relic.log — neither the public address learned nor the address the reach check probes;
///  - SSH: the host-key pins (an IP literal keyed in its canonical form) and their one decision (a confirmed key over a
///    changed one only when confirmed as such), the key's type read from its blob, only the pinned type offered (a
///    loopback stand-in server sees what the client offers — the deployer's connections and a legacy record's tunnel
///    alike), both connections checked, the read-only look at a box (its script, its output, and the script run for
///    real by Git Bash when this PC has it);
///  - Authenticode: an unsigned file is refused, the signer is the certificate's organisation (O), revocation is
///    asked online, the file is checked through a handle held open;
///  - the installs' own rules: <c>--accept-license</c> only when ticked, a pinned download that is not the pinned file
///    (its sha256, its size) deleted, a size the server announces other than the expected one stopping the download at
///    once (named right before the end too, and no progress line after the download's outcome — played once the
///    download reports its progress inline), an unsigned Docker installer deleted and never run and a signed one held
///    open from its check to its start,
///    Docker Desktop started without the shell, the elevation only in the two WSL steps;
///  - every text it uses is in en.json or in the fragment that brings it.
/// </summary>
static class SetupSpike
{
    public const string Name = "setup";

    public static int Run(string? repoRoot = null)
    {
        Console.WriteLine($"== {Name} spike: Quick Start's machine check, needs, tools, network, SSH pins ==");
        var results = new List<(string name, bool ok)>();
        void Check(string name, bool ok)
        {
            results.Add((name, ok));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        string? repo = repoRoot is { Length: > 0 } ? repoRoot
            : Environment.GetEnvironmentVariable("RELIC_REPO_ROOT") is { Length: > 0 } fromEnv ? fromEnv
            : FindRepo();
        if (repo is null || !File.Exists(Path.Combine(repo, "Relic.slnx")))
        {
            Check("the repository is found (the argument, RELIC_REPO_ROOT, or a folder holding Relic.slnx above here)", false);
            return Summary(results);
        }

        string tmp = Path.Combine(Path.GetTempPath(), "relic-setup-spike-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            MachineParsers(tmp, Check);
            WslConfigRules(tmp, Check);
            Catalogues(repo, Check);
            Needs(Check);
            Python(repo, tmp, Check);
            Extractor(tmp, Check);
            FreshPathRules(Check);
            Firewall(tmp, Check);
            PlanSevenZip(Check);
            Network(Check);
            ReachSource(repo, Check);
            Ssh(repo, Check);
            BoxScriptRun(tmp, Check);
            AuthenticodeRules(repo, tmp, Check);
            Installs(repo, tmp, Check);
            Texts(repo, Check);
        }
        catch (Exception ex)
        {
            Check($"the spike ran to its end ({ex.GetType().Name}: {ex.Message})", false);
        }
        finally
        {
            SetupTools.ToolsDirOverride = null;
            WslConfig.PathOverride = null;
            LocalAgent.RootOverride = null;
            LocalAgent.PythonRegistryOverride = null;
            LocalAgent.PythonProbeOverride = null;
            LocalAgent.FirewallShowOverride = null;
            LocalAgent.DriveTypeOverride = null;
            PublicAddress.DocumentationRangesArePublic = false;
            ReachCheck.ProbeOverride = null;
            SetupInstall.DownloadOverride = null;
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }
        Check("the seams are cleared again (tools folder, .wslconfig, agent root, Python, firewall, address ranges, reach, downloads)",
            SetupTools.ToolsDirOverride is null && WslConfig.PathOverride is null && LocalAgent.RootOverride is null
            && LocalAgent.PythonRegistryOverride is null && LocalAgent.PythonProbeOverride is null && LocalAgent.FirewallShowOverride is null
            && !PublicAddress.DocumentationRangesArePublic && ReachCheck.ProbeOverride is null && SetupInstall.DownloadOverride is null);
        return Summary(results);
    }

    static int Summary(List<(string name, bool ok)> results)
    {
        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"{Name} spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    // ── the machine parsers ──
    static void MachineParsers(string tmp, Action<string, bool> Check)
    {
        var on = MachineCheck.ParseCim("WARNING: noise\n{\"vmp\":1,\"wsl\":1,\"ram\":34274848768,\"hv\":true,\"fw\":false}\r\n");
        var off = MachineCheck.ParseCim("{\"vmp\":2,\"wsl\":3,\"ram\":8589934592,\"hv\":false,\"fw\":false}");
        var none = MachineCheck.ParseCim("{\"vmp\":null,\"wsl\":null,\"ram\":null,\"hv\":null,\"fw\":null}");
        Check("cim: the features on (1) and off (2 / 3), the memory; a hypervisor with the firmware switch read false = virtualization on",
            on.VmpOn == true && on.SubsystemLinux == 1 && on.TotalPhysicalMemory == 34274848768 && on.Virtualization == "on"
            && off.VmpOn == false && off.Virtualization == "off" && off.TotalPhysicalMemory == 8589934592);
        Check("cim: both switches false = off; not read (null, missing keys, no JSON line, junk) = unknown and nothing read",
            none.Virtualization == "unknown" && none.VmpOn is null && !none.Read
            && MachineCheck.ParseCim("{\"ram\":1}").Virtualization == "unknown" && !MachineCheck.ParseCim("").Read
            && !MachineCheck.ParseCim(null).Read && !MachineCheck.ParseCim("{not json}").Read
            && MachineCheck.ParseCim("{\"fw\":true}").Virtualization == "on" && MachineCheck.ParseCim("{\"hv\":\"yes\"}").HypervisorPresent is null);
        Check("cim: the fixed query holds no double quote (it travels on the command line as it is), asks the two features, the memory, the hypervisor and the firmware switch, and prints one JSON line",
            !MachineCheck.CimScript.Contains('"') && MachineCheck.CimScript.Contains("VirtualMachinePlatform") && MachineCheck.CimScript.Contains("Microsoft-Windows-Subsystem-Linux")
            && MachineCheck.CimScript.Contains("TotalPhysicalMemory") && MachineCheck.CimScript.Contains("HypervisorPresent")
            && MachineCheck.CimScript.Contains("VirtualizationFirmwareEnabled") && MachineCheck.CimScript.Contains("ConvertTo-Json -Compress")
            && !MachineCheck.CimScript.Contains("SecondLevelAddressTranslation") && !MachineCheck.CimScript.Contains("VMMonitorModeExtensions"));

        const string wslOut = "WSL version: 2.3.24.0\r\nKernel version: 5.15.153.1-2\r\nWSLg version: 1.0.65\r\nWindows version: 10.0.26100.2314\r\n";
        byte[] utf8 = Encoding.UTF8.GetBytes(wslOut), utf16 = Encoding.Unicode.GetBytes(wslOut);
        byte[] utf16Bom = new byte[] { 0xFF, 0xFE }.Concat(utf16).ToArray();
        const string inbox = "Copyright (c) Microsoft Corporation. All rights reserved.\r\n\r\nUsage: wsl.exe [Argument] [Options...] [CommandLine]\r\n";
        Check("wsl --version: UTF-8, UTF-16LE (with or without a byte order mark) and a translated first line -> 2.3.24.0; the inbox help text, an error and nothing -> null",
            MachineCheck.ParseWslVersion(utf8) == "2.3.24.0" && MachineCheck.ParseWslVersion(utf16) == "2.3.24.0" && MachineCheck.ParseWslVersion(utf16Bom) == "2.3.24.0"
            && MachineCheck.ParseWslVersion("WSL-Version: 2.7.14.0\nKernelversion: 6.18.33.2-2") == "2.7.14.0"
            && MachineCheck.ParseWslVersion(Encoding.Unicode.GetBytes(inbox)) is null && MachineCheck.ParseWslVersion(Encoding.UTF8.GetBytes(inbox)) is null
            && MachineCheck.ParseWslVersion("Invalid command line option: --version") is null && MachineCheck.ParseWslVersion(Array.Empty<byte>()) is null
            && MachineCheck.ParseWslVersion((byte[]?)null) is null);
        Check("wsl state: platform off = off; on with 2.1.5 or later = on; on with an older one, or a wsl.exe that names none = old; not read, or wsl.exe not asked = unknown",
            MachineCheck.WslState(false, true, "2.3.24.0", "2.1.5") == "off" && MachineCheck.WslState(true, true, "2.3.24.0", "2.1.5") == "on"
            && MachineCheck.WslState(true, true, "2.1.5", "2.1.5") == "on" && MachineCheck.WslState(true, true, "2.0.14.0", "2.1.5") == "old"
            && MachineCheck.WslState(true, true, null, "2.1.5") == "old" && MachineCheck.WslState(null, true, "2.3.24.0", "2.1.5") == "unknown"
            && MachineCheck.WslState(true, false, null, "2.1.5") == "unknown");
        Check("account name from the Windows user: lower-cased, only a-z 0-9 _ . -, from the first letter or digit, 20 at most; none for less than 3, digits only or a reserved prefix",
            MachineCheck.DefaultUserName("Raf") == "raf" && MachineCheck.DefaultUserName("Ana-Maria Ionescu") == "ana-mariaionescu"
            && MachineCheck.DefaultUserName("__.Bob") == "bob" && MachineCheck.DefaultUserName("Ștefan") == "tefan"
            && MachineCheck.DefaultUserName("abcdefghijklmnopqrstuvwxyz") == "abcdefghijklmnopqrst" && MachineCheck.DefaultUserName("Jo") == ""
            && MachineCheck.DefaultUserName("12345") == "" && MachineCheck.DefaultUserName("Administrator") == "" && MachineCheck.DefaultUserName("GMaster") == ""
            && MachineCheck.DefaultUserName("relicuser") == "" && MachineCheck.DefaultUserName("TplUser") == "" && MachineCheck.DefaultUserName(null) == "");
        string settings = Path.Combine(tmp, "settings-store.json");
        File.WriteAllText(settings, "{\"AutoStart\": false, \"LicenseTermsVersion\": 2, \"Other\": 1}");
        var s1 = MachineCheck.DockerSettings(settings);
        File.WriteAllText(settings, "{\"AutoStart\": true}");
        var s2 = MachineCheck.DockerSettings(settings);
        File.WriteAllText(settings, "{broken");
        var s3 = MachineCheck.DockerSettings(settings);
        Check("Docker's settings file: the licence terms accepted and the start at sign-in read; a file without the terms = not accepted; a broken or missing file = not known",
            s1 == (true, false) && s2 == (false, true) && s3 == (null, null) && MachineCheck.DockerSettings(Path.Combine(tmp, "nope.json")) == (null, null));
        string payloads = Path.Combine(tmp, "payloads");
        Directory.CreateDirectory(Path.Combine(payloads, "7.1"));
        File.WriteAllText(Path.Combine(payloads, "7.1", "manifest.json"), "{\"templates\":[{\"id\":\"pre-x\",\"label\":\"Before X\"},{\"id\":\"post-x\"},{\"label\":\"no id\"}]}");
        var tpl = MachineCheck.Templates(new[] { "7.1", "7.2", "../x" }, payloads);
        Check("starting progress: a version's shipped templates (id + label, the id when no label), none without a manifest, a path id refused",
            tpl["7.1"].SequenceEqual(new[] { new TemplateFact("pre-x", "Before X"), new TemplateFact("post-x", "post-x") })
            && tpl["7.2"].Count == 0 && !tpl.ContainsKey("../x"));
        Check("drives: \"C:\" for a path on a drive letter, the root of anything else",
            MachineCheck.DriveOf(@"d:\Games\Relic") == "D:" && MachineCheck.DriveOf("C:/x") == "C:" && MachineCheck.DriveOf(@"\\server\share\x") == @"\\server\share" && MachineCheck.DriveOf("") == "");
        string? lan = MachineCheck.LanIp();
        Check($"LAN address: IPv4, not loopback, not link-local, or none ({(lan is null ? "none" : "found")})",
            lan is null || (PublicAddress.ParseIpv4(lan) is not null && !lan.StartsWith("127.") && !lan.StartsWith("169.254.")));
        MachineRun(tmp, Check);
    }

    /// <summary>One machine check of this PC, read-only (the agent root, the tools folder and .wslconfig on temp
    /// folders): every probe answers or reads as not known, nothing throws.</summary>
    static void MachineRun(string tmp, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "mc-agent"), tools = Path.Combine(tmp, "mc-tools");
        Directory.CreateDirectory(root);
        try
        {
            LocalAgent.RootOverride = root;
            SetupTools.ToolsDirOverride = tools;
            WslConfig.PathOverride = Path.Combine(tmp, "mc.wslconfig");
            var sw = Stopwatch.StartNew();
            var m = MachineCheck.RunAsync(new MachineCheckRequest
            {
                GamesRoot = Path.Combine(tmp, "Games"),
                ServerFolders = new[] { new ServerFolder("2.8", Path.Combine(tmp, "servers", "2.8_live")), new ServerFolder("3.2", @"\\server\share\3.2_live") },
                Tools = SetupTools.Parse("{\"docker\":{\"minWsl\":\"2.1.5\"}}"),
            }).GetAwaiter().GetResult();
            string tmpDrive = MachineCheck.DriveOf(tmp);
            Console.WriteLine($"        this PC: build {m.WindowsBuild}, virtualization {m.Virtualization}, WSL {m.Wsl} {m.WslVersion}, Docker {m.Docker.State}, "
                + $"Python {(m.Python is null ? "none" : m.Python.Version)}, extractor {(m.Extractor is null ? "none" : "found")}, RAM {SetupNeeds.Gb(m.Ram ?? 0)} GB ({m.RamSource}), "
                + $"WSL cap {SetupNeeds.Gb(m.WslCap ?? 0)} GB ({m.WslCapSource}), restart pending {m.RestartPending?.ToString() ?? "?"}, {sw.ElapsedMilliseconds} ms");
            Check("machine check (this PC, read-only): Windows, the CIM query, WSL, Docker, Python and the memory answer or read as not known; WSL's cap is half the memory without a .wslconfig line; the folders are looked at, a UNC one refused",
                m.WindowsBuild > 0 && m.Virtualization is "on" or "off" or "unknown" && m.Wsl is "on" or "old" or "off" or "unknown"
                && m.Docker.State is "missing" or "installed" or "running" && m.Ram is > 0 && m.RamSource is "cim" or "memoryStatus"
                && m.WslCapSource == "default" && m.WslCap == m.Ram / 2 && !m.LocalAgent.Installed && m.Templates.Count == ServerVersions.All.Count
                && m.Folders.Count == 2 && m.Folders[0] is { Problem: null, Kind: "absent" } && m.Folders[0].Drive == tmpDrive && m.Folders[1].Problem is { Length: > 0 }
                && m.Drives.ContainsKey(tmpDrive) && m.ToolsDrive == tmpDrive && sw.Elapsed < TimeSpan.FromSeconds(90));
        }
        finally
        {
            LocalAgent.RootOverride = null;
            SetupTools.ToolsDirOverride = null;
            WslConfig.PathOverride = null;
        }
    }

    // ── .wslconfig ──
    static void WslConfigRules(string tmp, Action<string, bool> Check)
    {
        Check("wslconfig: memory=8GB, memory = 8192MB and memory=12G under [wsl2] (any letter case) read as 8, 8 and 12 GiB",
            WslConfig.MemoryBytes("[wsl2]\nmemory=8GB\n") == 8L << 30 && WslConfig.MemoryBytes("[WSL2]\r\n  memory = 8192MB  \r\n") == 8L << 30
            && WslConfig.MemoryBytes("[wsl2]\nMemory=12G") == 12L << 30 && WslConfig.ParseSize("512mb") == 512L << 20 && WslConfig.ParseSize("2.5GB") == (long)(2.5 * (1L << 30)));
        Check("wslconfig: a # or ; comment, the key under another section and a value WSL would not read are no cap; the last line of [wsl2] wins",
            WslConfig.MemoryBytes("[wsl2]\n# memory=8GB\n; memory=9GB\n") is null && WslConfig.MemoryBytes("[experimental]\nmemory=8GB\n") is null
            && WslConfig.MemoryBytes("[wsl2]\nmemory=lots\n") is null && WslConfig.MemoryBytes("[wsl2]\nmemory=4GB\nmemory=6GB\n") == 6L << 30
            && WslConfig.MemoryBytes("[wsl2]\nmemory=8GB # mine\n") == 8L << 30 && WslConfig.MemoryBytes(null) is null && WslConfig.MemoryBytes("memory=8GB") is null);
        string crlf = "[wsl2]\r\nprocessors=4\r\n";
        string inserted = WslConfig.WithMemory(crlf, 13);
        string replaced = WslConfig.WithMemory("[wsl2]\nmemory=4GB\nswap=0\n", 13);
        string added = WslConfig.WithMemory("[experimental]\nautoMemoryReclaim=gradual\n", 13);
        Check("wslconfig: the line goes under an existing [wsl2] and keeps CRLF; a memory line is replaced in place; a file without the section gets it at the end; an empty file becomes the section",
            inserted == "[wsl2]\r\nmemory=13GB\r\nprocessors=4\r\n" && replaced == "[wsl2]\nmemory=13GB\nswap=0\n"
            && added == "[experimental]\nautoMemoryReclaim=gradual\n\n[wsl2]\nmemory=13GB\n" && WslConfig.WithMemory("", 13) == "[wsl2]\r\nmemory=13GB\r\n"
            && WslConfig.MemoryBytes(inserted) == 13L << 30 && WslConfig.MemoryBytes(added) == 13L << 30);
        Check("wslconfig: the other lines, a comment above, the text without a final line end and another section's memory key stay as they were",
            WslConfig.WithMemory("# mine\n[wsl2]\nmemory=4GB", 8) == "# mine\n[wsl2]\nmemory=8GB"
            && WslConfig.WithMemory("[a]\nmemory=1GB\n[wsl2]\n", 8) == "[a]\nmemory=1GB\n[wsl2]\nmemory=8GB\n");
        string path = Path.Combine(tmp, ".wslconfig");
        WslConfig.PathOverride = path;
        File.WriteAllText(path, "[wsl2]\r\nprocessors=2\r\n");
        var written = SetupInstall.WslMemory(11);
        Check("wslconfig: the memory step writes the user's file through a copy beside it, and reads it back",
            written.Path == path && File.ReadAllText(path) == "[wsl2]\r\nmemory=11GB\r\nprocessors=2\r\n" && WslConfig.ReadCap() == 11L << 30
            && !File.Exists(path + ".relic-tmp"));
        // The file's own encoding and byte order mark stay: UTF-8 with its mark, UTF-16 little endian with its mark,
        // UTF-8 without one; a file that is not there yet is UTF-8 without a mark.
        byte[] utf8Bom = { 0xEF, 0xBB, 0xBF };
        File.WriteAllBytes(path, utf8Bom.Concat(Encoding.UTF8.GetBytes("# Ștefan's\r\n[wsl2]\r\nswap=0\r\n")).ToArray());
        SetupInstall.WslMemory(9);
        byte[] afterUtf8 = File.ReadAllBytes(path);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("[wsl2]\r\nswap=0\r\n")).ToArray());
        SetupInstall.WslMemory(9);
        byte[] afterUtf16 = File.ReadAllBytes(path);
        File.Delete(path);
        SetupInstall.WslMemory(9);
        byte[] fresh = File.ReadAllBytes(path);
        Check("wslconfig: the memory step keeps the file's encoding and byte order mark (UTF-8 with one, UTF-16 with one); a new file is UTF-8 without one",
            afterUtf8.SequenceEqual(utf8Bom.Concat(Encoding.UTF8.GetBytes("# Ștefan's\r\n[wsl2]\r\nmemory=9GB\r\nswap=0\r\n")))
            && afterUtf16.SequenceEqual(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("[wsl2]\r\nmemory=9GB\r\nswap=0\r\n")))
            && fresh.SequenceEqual(Encoding.UTF8.GetBytes("[wsl2]\r\nmemory=9GB\r\n")) && WslConfig.ReadCap() == 9L << 30);
        WslConfig.PathOverride = null;
    }

    // ── the catalogues ──
    static void Catalogues(string repo, Action<string, bool> Check)
    {
        var shipped = SetupTools.Load(Path.Combine(repo, "config", "setup-tools.json"));
        Check($"setup-tools.json: the shipped file is usable whole — Python {shipped.Python?.Version}, 7-Zip {shipped.SevenZip?.Version}, Docker by its signer, {shipped.PublicIpServices.Count} address services",
            shipped.PythonUsable && shipped.SevenZipUsable && shipped.DockerUsable && shipped.PublicIpServices.Count >= 2
            && shipped.Python!.Url.Contains(shipped.Python.Version) && shipped.SevenZip!.Url.Contains(shipped.SevenZip.Version)
            && shipped.Docker!.Signer == "Docker Inc" && shipped.Docker.DiskBytes == 3221225472 && shipped.Docker.MinWsl == "2.1.5");
        var bad = SetupTools.Parse("{\"python\":{\"version\":\"3.14.8\",\"url\":\"https://x.example.com/p.zip\",\"size\":10},"
            + "\"sevenZip\":{\"version\":\"26.04\",\"url\":\"http://x.example.com/7zr.exe\",\"size\":10,\"sha256\":\"" + new string('a', 64) + "\"},"
            + "\"docker\":{\"url\":\"https://x.example.com/d.exe\",\"licenceUrl\":\"https://x.example.com/l\",\"size\":5,\"diskBytes\":5,\"minWin10Build\":1,\"minWin11Build\":1,\"minWsl\":\"2.1.5\",\"minRamBytes\":1},"
            + "\"publicIpServices\":[\"https://a.example.com\",\"http://b.example.com\"]}");
        var nosize = SetupTools.Parse("{\"python\":{\"version\":\"3.14.8\",\"url\":\"https://x.example.com/p.zip\",\"sha256\":\"" + new string('b', 64) + "\"}}");
        Check("setup-tools.json: an entry without sha256 or size, or with an address that is not https, is unusable; a Docker entry without a signer too; one plain-http service drops the list",
            bad.Python is { Usable: false } && bad.SevenZip is { Usable: false } && bad.Docker is { Usable: false } && bad.PublicIpServices.Count == 0
            && nosize.Python is { Usable: false } && nosize.SevenZip is null && nosize.Docker is null && !SetupTools.Load(Path.Combine(repo, "config", "nope.json")).PythonUsable);
        var needs = ServerNeeds.Load(Path.Combine(repo, "config", "server-needs.json"));
        Check($"server-needs.json: the shipped file parses — a row for every server version, the game client's memory for each; an unknown version has none",
            ServerVersions.All.All(v => needs.For(v) is { Ram: > 0 } r && r.RamPathfinding >= r.Ram) && ServerVersions.All.All(v => needs.Client(v) is > 0)
            && needs.For("9.9") is null && needs.Client("9.9") is null);
        var parsed = ServerNeeds.Parse("{\"versions\":{\"a\":{\"ram\":10,\"ramPathfinding\":20,\"images\":null},\"b\":{\"ram\":10,\"ramPathfinding\":5},"
            + "\"c\":{\"ram\":10,\"ramPathfinding\":10,\"images\":7},\"d\":{\"ram\":\"10\",\"ramPathfinding\":10},\"e\":{\"ram\":10,\"ramPathfinding\":10,\"images\":\"x\"}},\"clients\":{\"a\":0,\"c\":9}}");
        Check("server-needs.json: images null = not known; a row whose pathfinding need is below its need, a number written as text or bad images is left out; a client of 0 too",
            parsed.For("a") is { Images: null } && parsed.For("b") is null && parsed.For("c") is { Images: 7 } && parsed.For("d") is null && parsed.For("e") is null
            && parsed.Client("a") is null && parsed.Client("c") == 9);
    }

    // ── the needs ──
    static MachineFacts Machine(Action<MachineFactsBuilder>? change = null)
    {
        var b = new MachineFactsBuilder();
        change?.Invoke(b);
        return b.Build();
    }

    sealed class MachineFactsBuilder
    {
        public int Build_ = 26100; public bool Win11 = true, Arm; public string Virt = "on", Wsl = "on", Docker = "running";
        public bool? Restart = false; public long? Ram = 32L << 30, Installed = 32L << 30, Cap = 16L << 30;
        public string? Python = @"C:\Py\python.exe", Extractor = @"C:\7z\7z.exe"; public bool AgentInstalled;
        public Dictionary<string, long?> Drives = new(StringComparer.OrdinalIgnoreCase) { ["C:"] = 100L << 30, ["D:"] = 500L << 30 };
        public List<FolderFacts> Folders = new();
        public MachineFacts Build() => new()
        {
            WindowsBuild = Build_, Windows11 = Win11, Arm = Arm, Virtualization = Virt, Wsl = Wsl, RestartPending = Restart,
            Docker = new DockerFacts(Docker, Docker == "missing" ? null : @"C:\Docker", null, Docker == "running" ? "28.0" : null, null, null),
            Python = Python is null ? null : new PythonFact(Python, "3.14"), Extractor = Extractor,
            LocalAgent = new LocalAgentFacts(AgentInstalled, false, 18080, null, null),
            Ram = Ram, InstalledRam = Installed, WslCap = Cap, ToolsDrive = "C:", Drives = Drives, Folders = Folders,
        };
    }

    static void Needs(Action<string, bool> Check)
    {
        var tools = SetupTools.Parse("{\"python\":{\"version\":\"3.14.8\",\"url\":\"https://x.example.com/p.zip\",\"size\":12000000,\"sha256\":\"" + new string('a', 64) + "\"},"
            + "\"sevenZip\":{\"version\":\"26.04\",\"url\":\"https://x.example.com/7zr.exe\",\"size\":600000,\"sha256\":\"" + new string('b', 64) + "\"},"
            + "\"docker\":{\"url\":\"https://x.example.com/d.exe\",\"signer\":\"Docker Inc\",\"licenceUrl\":\"https://x.example.com/l\",\"size\":650000000,\"diskBytes\":3221225472,"
            + "\"minWin10Build\":19045,\"minWin11Build\":22631,\"minWsl\":\"2.1.5\",\"minRamBytes\":8589934592}}");
        const long G = 1L << 30;
        var needs = ServerNeeds.Parse("{\"versions\":{\"1.6\":{\"ram\":" + (4 * G + G / 2) + ",\"ramPathfinding\":" + (8 * G + G / 2) + ",\"images\":2000000000},"
            + "\"2.8\":{\"ram\":" + (8 * G + G / 5) + ",\"ramPathfinding\":" + 16 * G + ",\"images\":2000000000},\"3.2\":{\"ram\":" + (10 * G + G / 2) + ",\"ramPathfinding\":" + (10 * G + G / 2) + ",\"images\":null}},"
            + "\"clients\":{\"1.6\":" + 8 * G + ",\"2.8\":" + 8 * G + ",\"3.2\":" + 8 * G + "}}");
        var pkg7z = new StackPackage(true, 2_500_000_000, "7z", true, "Internet Archive") { ExtractedSize = 17_000_000_000 };
        var pkgAlt = new StackPackage(true, 2_400_000_000, "7z", false, "Internet Archive") { ExtractedSize = 18_000_000_000, ZipSize = 4_200_000_000 };
        ServerVersionNeed Srv(string v, string choice, string dir, StackPackage? p = null, bool? pf = null) => new(v, choice, dir, p ?? pkg7z, pf);

        // The drives: a game download on D:, a server download on C:, the tools and Docker's images on C:.
        var fresh = new NeedsRequest
        {
            Kind = "windows",
            Games = new[] { new GameDownloadNeed("2.8", 40_000_000_000, @"D:\Games") },
            ClientVersions = new[] { "2.8" },
            Servers = new[] { Srv("2.8", "download", @"C:\servers\2.8_live") },
        };
        var bare = Machine(m => { m.Python = null; m.Extractor = null; m.Docker = "missing"; m.Wsl = "off"; });
        var r = SetupNeeds.For(fresh, bare, null, needs, tools);
        var c = r.Drives.Single(d => d.Drive == "C:");
        var d = r.Drives.Single(x => x.Drive == "D:");
        long server = 2_500_000_000 + 17_000_000_000 + 4 * G;
        Check("needs: the game takes twice its download on its games folder's drive; C: carries the server download (archive + unpacked + 4 GiB), Python, 7zr, Docker Desktop (3 GiB) and Docker's images",
            d.Need == 80_000_000_000 && d.Items.Single().Kind == "game" && d.Ok
            && c.Items.Select(i => i.Kind).OrderBy(k => k).SequenceEqual(new[] { "docker", "images", "python", "server", "sevenZip" })
            && c.Items.Single(i => i.Kind == "server").Bytes == server && c.Items.Single(i => i.Kind == "docker").Bytes == 3221225472
            && c.Items.Single(i => i.Kind == "python").Bytes == 36_000_000 && c.Items.Single(i => i.Kind == "sevenZip").Bytes == 1_200_000
            && c.Items.Single(i => i.Kind == "images").Bytes == 2_000_000_000 && c.Need == c.Items.Sum(i => i.Bytes) && c.Ok && !r.DockerImagesUnknown);
        Check("needs: what the run adds — Python, 7-Zip's extractor (a .7z-only package), WSL turned on, Docker Desktop installed and started",
            r.Installs == new SetupInstallsNeeded(true, true, true, false, true, true));
        var shortDisk = SetupNeeds.For(fresh, Machine(m => { m.Drives["C:"] = 10L << 30; m.Drives["D:"] = null; }), null, needs, tools);
        Check("needs: a drive with less free than it must hold is short; a drive whose free space was not read is not short (unknown)",
            shortDisk.Drives.Single(x => x.Drive == "C:") is { Ok: false, Free: 10L << 30 } && shortDisk.Drives.Single(x => x.Drive == "D:") is { Ok: true, Free: null });
        var alt = SetupNeeds.For(fresh with { Servers = new[] { Srv("3.2", "download", @"C:\servers\3.2_live", pkgAlt) } }, Machine(m => m.Extractor = null), null, needs, tools);
        Check("needs: without an extractor a package that comes as a .zip too is counted by its .zip and needs no 7zr; 3.2's images are not known -> the note, not a guess",
            alt.Drives.Single(x => x.Drive == "C:").Items.Single(i => i.Kind == "server").Bytes == 4_200_000_000 + 18_000_000_000 + 4 * G
            && !alt.Installs.SevenZip && alt.DockerImagesUnknown);
        var ready = SetupNeeds.For(fresh with { Games = Array.Empty<GameDownloadNeed>(), Servers = new[] { Srv("2.8", "ready", @"C:\s\2.8_live") } }, Machine(), null, needs, tools);
        Check("needs: a prepared server and a PC that has everything need no disk at all, and nothing is added",
            ready.Drives.Count == 0 && ready.Installs == new SetupInstallsNeeded(false, false, false, false, false, false) && ready.Blocking.Count == 0);

        // The memory.
        Check("memory verdicts: fits with pathfinding, noPathfinding when only the server without it fits, short when not even that; a run that asks for it on or off is judged so",
            SetupNeeds.Verdict(8, 16, 16, null) == "fits" && SetupNeeds.Verdict(8, 16, 12, null) == "noPathfinding" && SetupNeeds.Verdict(8, 16, 6, null) == "short"
            && SetupNeeds.Verdict(8, 16, 12, false) == "fits" && SetupNeeds.Verdict(8, 16, 12, true) == "short" && SetupNeeds.Verdict(8, 16, 16, true) == "fits");
        var tight = SetupNeeds.For(fresh with { Servers = new[] { Srv("2.8", "ready", @"C:\s"), Srv("1.6", "ready", @"C:\t") } },
            Machine(m => { m.Ram = 32L << 30; m.Cap = 16L << 30; }), null, needs, tools);
        Check("memory: on a 32 GB PC whose WSL may use 16 GB, 2.8 fits with pathfinding (16 GiB) and so does 1.6",
            tight.Ram["2.8"].Verdict == "fits" && tight.Ram["1.6"].Verdict == "fits" && tight.Ram["2.8"].Cap == 16L << 30 && tight.RaiseGb == 0);
        var half = SetupNeeds.For(fresh with { Servers = new[] { Srv("2.8", "ready", @"C:\s") } }, Machine(m => { m.Ram = 16L << 30; m.Cap = 8L << 30; }), null, needs, tools);
        Check("memory: on a 16 GB PC (WSL 8 GiB) 2.8 is short — 8.2 GiB without pathfinding — and a raise to 10 GB would leave 6 GiB, less than 4 GiB + the 8 GiB client: no raise",
            half.Ram["2.8"] is { Verdict: "short", RaiseGb: 0 } && half.RaiseGb == 0);
        var big = SetupNeeds.For(fresh with { Servers = new[] { Srv("2.8", "ready", @"C:\s") } }, Machine(m => { m.Ram = 24L << 30; m.Cap = 8L << 30; }), null, needs, tools);
        Check("memory: on a 24 GB PC (WSL 8 GiB) 2.8 short -> raise to 10 GB (need + 1 GiB, whole GB), which leaves 14 GiB >= 4 + 8",
            big.Ram["2.8"] is { Verdict: "short", RaiseGb: 10 } && big.RaiseGb == 10 && SetupNeeds.RaiseGb(8 * G + G / 5, 24 * G, 8 * G) == 10);
        Check("memory: the raise counts the largest chosen game client — without a client on this PC the same 16 GB PC could take it",
            SetupNeeds.RaiseGb(8 * G + G / 5, 16 * G, 0) == 10 && SetupNeeds.RaiseGb(8 * G + G / 5, 16 * G, 8 * G) == 0 && SetupNeeds.RaiseGb(0, 16 * G, 0) == 0);
        var pf = SetupNeeds.For(fresh with { Servers = new[] { Srv("1.6", "ready", @"C:\s") } }, Machine(m => m.Cap = 6L << 30), null, needs, tools);
        Check("memory: 1.6 in a 6 GiB WSL fits only without pathfinding -> noPathfinding, no raise offered",
            pf.Ram["1.6"] is { Verdict: "noPathfinding", RaiseGb: 0 });
        var unknown = SetupNeeds.For(fresh with { Servers = new[] { Srv("9.9", "ready", @"C:\s") } }, Machine(), null, needs, tools);
        Check("memory: a version without a row gets no verdict (unknown)", unknown.Ram["9.9"].Verdict == "unknown" && unknown.Ram["9.9"].Need is null);
        var box = new BoxFacts
        {
            MemTotal = 16L << 30, Free = new Dictionary<string, BoxFree> { ["/home/2.8_live"] = new(40L << 30, "/home"), ["/home/1.6_live"] = new(40L << 30, "/home") },
        };
        var lx = SetupNeeds.For(new NeedsRequest { Kind = "linux", ClientVersions = new[] { "2.8" }, Servers = new[] { Srv("2.8", "download", "/home/2.8_live"), Srv("1.6", "download", "/home/1.6_live") } },
            null, box, needs, tools);
        Check("needs (Linux): the server's memory less 1 GiB is the cap (15 GiB: 2.8 noPathfinding, never a raise); two folders of one filesystem are summed on it; nothing is added to this PC",
            lx.Ram["2.8"] is { Verdict: "noPathfinding", RaiseGb: 0, Cap: 15L << 30 } && lx.Drives.Count == 1 && lx.Drives[0] is { Drive: "/home", Box: true, Free: 40L << 30, Ok: false }
            && lx.Drives[0].Items.Count == 2 && lx.Installs == SetupInstallsNeeded.None && lx.Blocking.Count == 0);

        // What blocks a server on this PC.
        string[] Codes(MachineFacts m, NeedsRequest? req = null, SetupToolsCatalog? t = null) =>
            SetupNeeds.For(req ?? fresh, m, null, needs, t ?? tools).Blocking.Select(b => b.Code).ToArray();
        Check("blocking: an ARM PC; virtualization off while Docker does not run; Docker to install on a Windows build below its minimum or with less than 8 GiB",
            Codes(Machine(m => m.Arm = true)).SequenceEqual(new[] { "arm" }) && Codes(Machine(m => { m.Virt = "off"; m.Docker = "installed"; })).SequenceEqual(new[] { "virtOff" })
            && Codes(Machine(m => { m.Virt = "off"; })).Length == 0
            && Codes(Machine(m => { m.Docker = "missing"; m.Win11 = false; m.Build_ = 19044; })).SequenceEqual(new[] { "windowsOld" })
            && Codes(Machine(m => { m.Docker = "missing"; m.Win11 = true; m.Build_ = 22621; })).SequenceEqual(new[] { "windowsOld" })
            && Codes(Machine(m => { m.Docker = "missing"; m.Installed = 4L << 30; m.Ram = 4L << 30; })).SequenceEqual(new[] { "ramLow" })
            && Codes(Machine(m => { m.Docker = "missing"; m.Installed = 8L << 30; m.Ram = (8L << 30) - 300_000_000; })).Length == 0
            && Codes(Machine(m => { m.Win11 = false; m.Build_ = 19044; })).Length == 0);
        Check("blocking: a restart Windows waits for only when the run turns WSL on, updates it or installs Docker",
            Codes(Machine(m => { m.Restart = true; m.Wsl = "off"; })).SequenceEqual(new[] { "restartPending" })
            && Codes(Machine(m => { m.Restart = true; m.Wsl = "old"; m.Docker = "installed"; })).SequenceEqual(new[] { "restartPending" })
            && Codes(Machine(m => { m.Restart = true; m.Docker = "missing"; })).SequenceEqual(new[] { "restartPending" })
            && Codes(Machine(m => m.Restart = true)).Length == 0 && Codes(Machine(m => { m.Restart = null; m.Wsl = "off"; })).Length == 0);
        var problem = Machine(m => m.Folders.Add(new FolderFacts("2.8", @"Z:\s\2.8_live", "Z:", null, "the drive is a network drive", "unknown")));
        Check("blocking: a folder the agent here could not use, for a chosen version only, with its own sentence",
            SetupNeeds.For(fresh, problem, null, needs, tools).Blocking.SequenceEqual(new[] { new BlockingReason("folder", "the drive is a network drive") })
            && Codes(problem, fresh with { Servers = new[] { Srv("1.6", "download", @"C:\x") } }).Length == 0);
        var noTools = SetupTools.Parse("{}");
        Check("blocking: a piece the run must add while this build pins no usable file for it — Python, the extractor, Docker Desktop",
            Codes(Machine(m => m.Python = null), t: noTools).SequenceEqual(new[] { "noPython" })
            && Codes(Machine(m => m.Extractor = null), t: noTools).SequenceEqual(new[] { "noSevenZip" })
            && Codes(Machine(m => m.Docker = "missing"), t: noTools).SequenceEqual(new[] { "noDocker" })
            && Codes(Machine(m => { m.Python = null; m.AgentInstalled = true; }), t: noTools).Length == 0
            && SetupNeeds.For(fresh, Machine(m => m.Python = null), null, needs, noTools).Blocking[0].Text == L.T("core.setup.check.noPython"));
        Check("blocking: nothing for an existing server or a Linux one; texts with their numbers",
            SetupNeeds.For(fresh with { Kind = "existing" }, Machine(m => m.Arm = true), null, needs, tools).Blocking.Count == 0
            && SetupNeeds.For(fresh with { Kind = "linux" }, Machine(m => m.Arm = true), null, needs, tools).Blocking.Count == 0
            && SetupNeeds.For(fresh, Machine(m => { m.Docker = "missing"; m.Win11 = false; m.Build_ = 19044; }), null, needs, tools).Blocking[0].Text
                == L.T("core.setup.check.windowsOld", new { win10 = 19045, win11 = 22631, build = 19044 }));
        Check("installs: WSL too old -> updated, unless Docker already runs; WSL not read -> turned on while Docker does not run; Docker installed but stopped -> started",
            SetupNeeds.Installs(fresh.Servers, Machine(m => { m.Wsl = "old"; m.Docker = "installed"; })) is { WslUpdate: true, Wsl: false, DockerStart: true, Docker: false }
            && SetupNeeds.Installs(fresh.Servers, Machine(m => m.Wsl = "old")) is { WslUpdate: false, DockerStart: false }
            && SetupNeeds.Installs(fresh.Servers, Machine(m => { m.Wsl = "unknown"; m.Docker = "missing"; })) is { Wsl: true, Docker: true }
            && SetupNeeds.Installs(fresh.Servers, Machine(m => m.Wsl = "unknown")) is { Wsl: false });
        Check("GB in the texts: one decimal at most", SetupNeeds.Gb(8L << 30) == "8" && SetupNeeds.Gb((8L << 30) - 300_000_000) == "7.7");
    }

    // ── Python ──
    static void Python(string repo, string tmp, Action<string, bool> Check)
    {
        string tools = Path.Combine(tmp, "tools");
        SetupTools.ToolsDirOverride = tools;
        Directory.CreateDirectory(SetupTools.PythonDir);
        File.WriteAllText(SetupTools.PythonExe, "stand-in");
        string regA = Touch(Path.Combine(tmp, "regA", "python.exe")), regB = Touch(Path.Combine(tmp, "regB", "python.exe"));
        string winApps = Touch(Path.Combine(tmp, "WindowsApps", "PythonSoftwareFoundation.Python.3.12", "python.exe"));
        string old = Touch(Path.Combine(tmp, "old", "python.exe"));
        string good = Touch(Path.Combine(tmp, "good", "python.exe"));
        Touch(Path.Combine(tmp, "good", "pythonw.exe"));
        var asked = new List<(string Exe, string Args, IReadOnlyDictionary<string, string> Env)>();
        Dictionary<string, (int, string)?> answers = new(StringComparer.OrdinalIgnoreCase);
        LocalAgent.PythonRegistryOverride = () => new[] { regA, regB };
        LocalAgent.PythonProbeOverride = (exe, args, env) =>
        {
            asked.Add((exe, args, env));
            return answers.TryGetValue(exe, out var a) ? a : null;
        };
        try
        {
            var none = LocalAgent.FindPython();
            Check("python: the candidates in order — Relic's own (its tools folder), the registry's (this user's, then the machine's), then py -3, python, python3 from PATH",
                none is null && asked.Select(a => a.Exe).SequenceEqual(new[] { SetupTools.PythonExe, regA, regB, "py", "python", "python3" })
                && asked[3].Args.StartsWith("-3 -c ", StringComparison.Ordinal) && asked.Where((a, i) => i != 3).All(a => a.Args.StartsWith("-c ", StringComparison.Ordinal)));
            Check("python: every probe runs with PYTHON_MANAGER_AUTOMATIC_INSTALL=false (the install manager installs nothing because a command found no runtime)",
                asked.Count == 6 && asked.All(a => a.Env.TryGetValue("PYTHON_MANAGER_AUTOMATIC_INSTALL", out var v) && v == "false"));
            asked.Clear();
            answers[SetupTools.PythonExe] = (0, winApps + "\n3.12\n");
            answers[regA] = (0, old + "\n3.9\n");
            answers[regB] = (0, good + "\r\n3.14\r\n");
            answers["py"] = (0, good + "\n3.13\n");
            var found = LocalAgent.FindPython();
            Check("python: an answer under \\WindowsApps\\ is passed over, one older than 3.10 refused, the next good one taken with the pythonw beside it",
                found is not null && found.Exe == good && found.ExeW == Path.Combine(Path.GetDirectoryName(good)!, "pythonw.exe") && found.Version == "3.14"
                && asked.Select(a => a.Exe).SequenceEqual(new[] { SetupTools.PythonExe, regA, regB }));
            answers.Clear();
            answers[SetupTools.PythonExe] = (0, good + "\n3.14\n");
            Check("python: Relic's own Python is the first answer when it qualifies", LocalAgent.FindPython()?.Exe == good);
            answers[SetupTools.PythonExe] = (9009, "");
            answers[regA] = (0, "only one line\n");
            answers[regB] = (0, Path.Combine(tmp, "gone", "python.exe") + "\n3.12\n");
            answers["py"] = (0, good + "\nthree\n");
            Check("python: a failed probe, an answer of one line, an interpreter that is not there and a version that is no number are all passed over",
                LocalAgent.FindPython() is null);
            string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Python", "bin", "python.exe");
            Check("python: the install manager's alias folder and the Store's are no runtime of their own; a plain install is",
                LocalAgent.IsAliasPython(alias) && LocalAgent.IsAliasPython(@"C:\Program Files\WindowsApps\PythonSoftwareFoundation.Python.3.12_x\python.exe")
                && !LocalAgent.IsAliasPython(@"C:\Python314\python.exe") && !LocalAgent.IsAliasPython(null));
        }
        finally
        {
            LocalAgent.PythonRegistryOverride = null;
            LocalAgent.PythonProbeOverride = null;
            SetupTools.ToolsDirOverride = null;
        }
        var reg = LocalAgent.RegistryPythons();
        Check($"python: this PC's registry entries read without throwing, each a file that is there ({reg.Count})", reg.All(File.Exists));

        // A real probe of a program that waits for an answer on its input — cmd.exe asks for commands there — must not
        // hold the search up to the probe's deadline: its input is closed, and cmd ends at once. PATH points at an empty
        // folder meanwhile, so py / python / python3 are not found and only the stand-in is run.
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string? path = Environment.GetEnvironmentVariable("PATH");
        string emptyPath = Path.Combine(tmp, "empty-path");
        Directory.CreateDirectory(emptyPath);
        try
        {
            SetupTools.ToolsDirOverride = Path.Combine(tmp, "tools-real");
            LocalAgent.PythonRegistryOverride = () => new[] { cmd };
            Environment.SetEnvironmentVariable("PATH", emptyPath);
            var sw = Stopwatch.StartNew();
            var probed = LocalAgent.FindPython();
            sw.Stop();
            Check($"python: a real probe closes the program's input — a stand-in that waits for commands there (cmd.exe) ends at once, well before the 8 s deadline, and is passed over ({sw.ElapsedMilliseconds} ms{(probed is null ? "" : ", then " + Path.GetFileName(probed.Exe) + " " + probed.Version + " found where Windows keeps py.exe")})",
                (probed is null || !probed.Exe.Equals(cmd, StringComparison.OrdinalIgnoreCase)) && sw.Elapsed < TimeSpan.FromSeconds(6));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            LocalAgent.PythonRegistryOverride = null;
            SetupTools.ToolsDirOverride = null;
        }
        string local = File.ReadAllText(Path.Combine(repo, "app", "Relic.Core", "Server", "LocalAgent.cs")).Replace("\r\n", "\n");
        string probe = Body(local, "private static (int Code, string Output)? ProbePython(string exe, string args)");
        Check("python (source): the probe is a timed process — its input closed, its output read as it comes, the tree ended at 8 s — and asks for its answer in UTF-8",
            probe.Contains("Setup.TimedProcess.Run(exe, argv, TimeSpan.FromSeconds(8), PythonProbeEnvironment", StringComparison.Ordinal)
            && local.Contains("sys.stdout.reconfigure(encoding='utf-8')", StringComparison.Ordinal) && !probe.Contains("ReadToEnd()", StringComparison.Ordinal));
    }

    static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    // ── the extractor ──
    static void Extractor(string tmp, Action<string, bool> Check)
    {
        string root = Path.Combine(tmp, "agent-root"), tools = Path.Combine(tmp, "tools-x");
        Directory.CreateDirectory(root);
        string custom = Touch(Path.Combine(tmp, "custom", "7za.exe"));
        try
        {
            LocalAgent.RootOverride = root;
            SetupTools.ToolsDirOverride = tools;
            Directory.CreateDirectory(tools);
            File.WriteAllText(SetupTools.SevenZipExe, "x");
            File.WriteAllText(LocalAgent.ConfigPath, $"GIO_AGENT_LISTEN=127.0.0.1:18977\nGIO_7Z={custom}\n");
            string? first = LocalAgent.FindExtractor();
            File.WriteAllText(LocalAgent.ConfigPath, $"GIO_7Z={Path.Combine(tmp, "custom", "gone.exe")}\n");
            string? second = LocalAgent.FindExtractor();
            File.Delete(SetupTools.SevenZipExe);
            string? third = LocalAgent.FindExtractor();
            Check($"extractor: the installed agent's GIO_7Z first, then Relic's 7zr (a GIO_7Z that is not there is passed over), then the agent's usual candidates ({third ?? "none here"})",
                first == custom && second == SetupTools.SevenZipExe && third != SetupTools.SevenZipExe && third != custom);
            File.WriteAllText(SetupTools.SevenZipExe, "x");
            var plan = new AgentInstallPlan { Target = AgentInstallPlan.TargetWindows };
            File.WriteAllText(LocalAgent.ConfigPath, $"GIO_7Z={custom}\n");
            bool keptStored = !LocalAgent.FillSevenZip(plan) && plan.SevenZip == "";
            File.WriteAllText(LocalAgent.ConfigPath, "GIO_AGENT_LISTEN=127.0.0.1:18977\n");
            bool ownHere = LocalAgent.AgentFindsExtractor();
            bool filled = LocalAgent.FillSevenZip(plan);
            Check($"extractor: an install writes Relic's 7zr as GIO_7Z only while the agent finds no tool of its own — a stored GIO_7Z that is there keeps its line (this PC: {(ownHere ? "has its own, left blank" : "has none, filled")})",
                keptStored && filled == !ownHere && (filled ? plan.SevenZip == SetupTools.SevenZipExe : plan.SevenZip == ""));
            var linux = new AgentInstallPlan { Target = AgentInstallPlan.TargetLinux };
            var named = new AgentInstallPlan { Target = AgentInstallPlan.TargetWindows, SevenZip = custom };
            Check("extractor: a Linux plan and a plan that names a tool are never filled",
                !LocalAgent.FillSevenZip(linux) && linux.SevenZip == "" && !LocalAgent.FillSevenZip(named) && named.SevenZip == custom);
        }
        finally
        {
            LocalAgent.RootOverride = null;
            SetupTools.ToolsDirOverride = null;
        }
        Check("extractor: the fill rule — Relic's 7zr when the plan names none, it is there and the agent finds none; else nothing",
            LocalAgent.SevenZipFill("", true, @"C:\R\7zr.exe", false) == @"C:\R\7zr.exe" && LocalAgent.SevenZipFill(null, true, @"C:\R\7zr.exe", false) == @"C:\R\7zr.exe"
            && LocalAgent.SevenZipFill(@"C:\x\7z.exe", true, @"C:\R\7zr.exe", false) is null && LocalAgent.SevenZipFill("", false, @"C:\R\7zr.exe", false) is null
            && LocalAgent.SevenZipFill("", true, @"C:\R\7zr.exe", true) is null);
        string apostrophe = @"C:\Users\O'Neil\AppData\Local\Relic\tools\7zr.exe";
        var oneil = new AgentInstallPlan { Target = AgentInstallPlan.TargetWindows, Listen = "127.0.0.1:18080", Token = new string('a', 32), SevenZip = apostrophe };
        Check("extractor: a tools folder under a user name with an apostrophe is filled and the plan takes it; a path the plan would refuse is never filled by itself, so it cannot refuse an install",
            LocalAgent.SevenZipFill("", true, apostrophe, false) == apostrophe && oneil.Validate().Count == 0
            && oneil.RenderConfig().Contains("\nGIO_7Z=" + apostrophe + "\n") && LocalAgent.SevenZipFill("", true, "C:\\R\\\"q\\7zr.exe", false) is null
            && LocalAgent.SevenZipFill("", true, @"R\7zr.exe", false) is null && LocalAgent.SevenZipFill("", true, "C:\\R\\a\tb\\7zr.exe", false) is null);
    }

    // ── the fresh PATH ──
    static void FreshPathRules(Action<string, bool> Check)
    {
        string sys = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        string fresh = LocalAgent.FreshPath(@"%SystemRoot%\system32;C:\A;;", @"C:\Users\me\bin;c:\a\", @"C:\A;D:\extra;c:\users\ME\bin;D:\Extra\;%NOT_EXPANDED%");
        Check("fresh PATH: the machine's then the user's (environment names expanded), then this process's own extras; each once whatever its letter case or end separator; empty entries dropped",
            fresh == $@"{sys}\system32;C:\A;C:\Users\me\bin;D:\extra;%NOT_EXPANDED%");
        Check("fresh PATH: nothing read = this process's PATH, each entry once", LocalAgent.FreshPath(null, null, @"C:\x;C:\X;D:\y") == @"C:\x;D:\y" && LocalAgent.FreshPath(null, null, null) == "");
        string now = LocalAgent.FreshPathNow();
        Check("fresh PATH: the registry's values as they are now hold this PC's System32", now.Split(';').Any(e => e.TrimEnd('\\').Equals(Path.Combine(sys, "system32"), StringComparison.OrdinalIgnoreCase)));
    }

    // ── the firewall ──
    static void Firewall(string tmp, Action<string, bool> Check)
    {
        string all = LocalAgent.FirewallCommandLine(18080, true, @"C:\Py\pythonw.exe");
        int Count(string s, string part) => Regex.Matches(s, Regex.Escape(part)).Count;
        Check("firewall: ONE cmd /d /v:off /s /c (no AutoRun, no delayed expansion): the agent's rule deleted first, whatever that answers, then three netsh advfirewall firewall add rule joined by &&, netsh by its full path",
            all.StartsWith("/d /v:off /s /c \"\"", StringComparison.Ordinal) && all.EndsWith("\"", StringComparison.Ordinal) && Count(all, "advfirewall firewall add rule") == 3
            && Count(all, " && ") == 2 && Count(all, "\\netsh.exe\" advfirewall") == 4 && Count(all, " & ") == 1
            && all.IndexOf("advfirewall firewall delete rule name=\"Relic GIO agent (18080)\" & ", StringComparison.Ordinal) is > 0 and var del
            && del < all.IndexOf("advfirewall firewall add rule", StringComparison.Ordinal));
        Check("firewall: the agent's TCP port scoped to its pythonw, TCP 21000 and UDP 21081 for any program, every rule dir=in action=allow profile=any",
            all.Contains("name=\"Relic GIO agent (18080)\" dir=in action=allow protocol=TCP localport=18080 profile=any program=\"C:\\Py\\pythonw.exe\"")
            && all.Contains("name=\"Relic GIO server (21000 TCP)\" dir=in action=allow protocol=TCP localport=21000 profile=any &&")
            && all.EndsWith("name=\"Relic GIO server (21081 UDP)\" dir=in action=allow protocol=UDP localport=21081 profile=any\"")
            && Count(all, "program=") == 1 && Count(all, "profile=any") == 3);
        string pct = LocalAgent.FirewallCommandLine(18080, false, @"C:\%USERNAME%\pythonw.exe");
        Check("firewall: without the game ports the delete and one add, no &&; a program path that holds % (or \") is left out, one with ! is kept (no delayed expansion)",
            Count(pct, "add rule") == 1 && Count(pct, "delete rule") == 1 && !pct.Contains("&&") && !pct.Contains("program=")
            && !LocalAgent.FirewallCommandLine(18080, false, "C:\\a\"b\\pythonw.exe").Contains("program=") && !LocalAgent.FirewallCommandLine(18080, false, null).Contains("program=")
            && LocalAgent.FirewallCommandLine(18080, false, @"C:\Users\a!b\pythonw.exe").Contains("program=\"C:\\Users\\a!b\\pythonw.exe\""));
        string some = LocalAgent.FirewallCommandLine(18080, true, @"C:\Py\pythonw.exe", n => n == LocalAgent.ServerRuleTcp || n == LocalAgent.FirewallRuleName(18080));
        Check("firewall: a game-port rule that is there is not added again, the agent's own rule always is — written anew for the pythonw of now (an earlier Python's rule repaired, never doubled)",
            Count(some, "add rule") == 2 && some.Contains("add rule name=\"Relic GIO agent (18080)\"") && !some.Contains("add rule name=\"Relic GIO server (21000 TCP)\"")
            && some.Contains("add rule name=\"Relic GIO server (21081 UDP)\"") && Count(some, "delete rule") == 1);
        string remove = LocalAgent.FirewallRemoveCommandLine(LocalAgent.FirewallRuleNames(18080));
        Check("firewall: the removal deletes the three rules in one cmd (no AutoRun, no delayed expansion), each tried whatever the one before answered",
            remove.StartsWith("/d /v:off /s /c \"", StringComparison.Ordinal) && Count(remove, "advfirewall firewall delete rule name=") == 3 && Count(remove, " & ") == 2
            && remove.Contains("name=\"Relic GIO agent (18080)\"") && remove.Contains("name=\"Relic GIO server (21000 TCP)\"") && remove.Contains("name=\"Relic GIO server (21081 UDP)\""));
        try
        {
            var codes = new Dictionary<string, int> { [LocalAgent.FirewallRuleName(18977)] = 0, [LocalAgent.ServerRuleTcp] = 0, [LocalAgent.ServerRuleUdp] = 1 };
            LocalAgent.FirewallShowOverride = name => codes.TryGetValue(name, out var c) ? c : 1;
            bool partly = LocalAgent.FirewallRuleExists(18977) && !LocalAgent.ServerRulesExist()
                && LocalAgent.FirewallRulesPresent(18977).SequenceEqual(new[] { "Relic GIO agent (18977)", "Relic GIO server (21000 TCP)" });
            codes[LocalAgent.ServerRuleUdp] = 0;
            bool both = LocalAgent.ServerRulesExist();
            codes[LocalAgent.FirewallRuleName(18977)] = 2;
            bool error = !LocalAgent.FirewallRuleExists(18977);
            Check("firewall: a rule is there by netsh's exit code alone — 0 there, 1 not, anything else not; the game ports' rules count only both together",
                partly && both && error);
            codes[LocalAgent.FirewallRuleName(18977)] = 0;
            string root = Path.Combine(tmp, "uninstall-root");
            Directory.CreateDirectory(Path.Combine(root, "payloads"));
            LocalAgent.RootOverride = root;
            File.WriteAllText(Path.Combine(root, "gio_agent.py"), "# stub");
            var rep = LocalAgent.Uninstall(18977, removeFirewall: false);
            Check("firewall: an uninstall that is not asked to remove the rules names every rule that stays in its report",
                rep.FilesRemoved && !rep.FirewallRemoved && rep.FirewallError is null && rep.KeptRules is { } kept
                && kept.SequenceEqual(new[] { "Relic GIO agent (18977)", "Relic GIO server (21000 TCP)", "Relic GIO server (21081 UDP)" }));
        }
        finally
        {
            LocalAgent.FirewallShowOverride = null;
            LocalAgent.RootOverride = null;
        }
        Check("firewall: the rule names", LocalAgent.FirewallRuleNames(18080, false).SequenceEqual(new[] { "Relic GIO agent (18080)" }) && LocalAgent.GameLoginPort == 21000 && LocalAgent.GameUdpPort == 21081);
    }

    // ── the plan's extractor line ──
    static void PlanSevenZip(Action<string, bool> Check)
    {
        AgentInstallPlan P(string target, string seven) => new()
        {
            Target = target, Listen = "0.0.0.0:18080", Token = new string('a', 32), Region = "dev_docker", SevenZip = seven,
        };
        string[] Lines(AgentInstallPlan p) => p.RenderConfig().Split('\n').Where(l => l.StartsWith("GIO_7Z=")).ToArray();
        var named = P(AgentInstallPlan.TargetWindows, @"C:\Users\me\AppData\Local\Relic\tools\7zr.exe");
        Check("plan: a named extractor is ONE GIO_7Z= line on a Windows plan, valid; a blank one writes none",
            Lines(named).SequenceEqual(new[] { @"GIO_7Z=C:\Users\me\AppData\Local\Relic\tools\7zr.exe" }) && named.Validate().Count == 0
            && Lines(P(AgentInstallPlan.TargetWindows, "")).Length == 0 && Lines(P(AgentInstallPlan.TargetWindows, "  ")).Length == 0);
        IReadOnlyList<string> V(string target, string seven) => P(target, seven).Validate();
        Check("plan: a relative path, one that is no .exe, a UNC path, one with a double quote or a control character, and any value on a Linux plan are refused (badSevenZip); a Linux plan writes none",
            V(AgentInstallPlan.TargetWindows, "7zr.exe").SequenceEqual(new[] { L.T("core.deploy.badSevenZip", new { path = "7zr.exe" }) })
            && V(AgentInstallPlan.TargetWindows, @"C:\tools\7zr.bat").Count == 1 && V(AgentInstallPlan.TargetWindows, @"\\srv\share\7z.exe").Count == 1
            && V(AgentInstallPlan.TargetWindows, "C:\\to\"ols\\7z.exe").Count == 1 && V(AgentInstallPlan.TargetWindows, "C:\\to\u0001ols\\7z.exe").Count == 1
            && V(AgentInstallPlan.TargetLinux, @"C:\tools\7zr.exe").Count == 1
            && Lines(P(AgentInstallPlan.TargetLinux, @"C:\tools\7zr.exe")).Length == 0 && !P(AgentInstallPlan.TargetLinux, @"C:\x\7z.exe").ToEnvironment().ContainsKey("GIO_7Z"));
        Check("plan: an apostrophe is a letter of a folder name there (C:\\Users\\O'Neil\\…): taken, and written as it is",
            V(AgentInstallPlan.TargetWindows, "C:\\Users\\O'Neil\\tools\\7zr.exe").Count == 0
            && Lines(P(AgentInstallPlan.TargetWindows, "C:\\Users\\O'Neil\\tools\\7zr.exe")).SequenceEqual(new[] { "GIO_7Z=C:\\Users\\O'Neil\\tools\\7zr.exe" })
            && LocalAgent.ParseConfig(P(AgentInstallPlan.TargetWindows, "C:\\Users\\O'Neil\\tools\\7zr.exe").RenderConfig()).All["GIO_7Z"] == "C:\\Users\\O'Neil\\tools\\7zr.exe");
        string merged = LocalAgent.MergeConfig(P(AgentInstallPlan.TargetWindows, "").RenderConfig(), "GIO_7Z=D:\\7-Zip\\7z.exe\n", out var kept);
        Check("plan: a re-install that names none keeps the stored GIO_7Z line (the merge); one that names it writes its own",
            kept.Contains("GIO_7Z") && merged.Contains("\nGIO_7Z=D:\\7-Zip\\7z.exe\n")
            && LocalAgent.MergeConfig(named.RenderConfig(), "GIO_7Z=D:\\7-Zip\\7z.exe\n").Split('\n').Count(l => l.StartsWith("GIO_7Z=")) == 1);
    }

    // ── the network ──
    static void Network(Action<string, bool> Check)
    {
        bool A(string ip, bool priv) => PublicAddress.IsAdvertisable(ip, priv);
        Check("advertisable: a public address yes; private only with allowPrivate; CGNAT never; loopback, 0/8, link-local, multicast, reserved, IPv6, text never",
            A("8.8.8.8", false) && !A("10.0.0.1", false) && !A("172.16.0.1", false) && !A("192.168.1.1", false) && A("10.0.0.1", true) && A("172.16.0.1", true)
            && A("192.168.1.1", true) && !A("100.64.0.1", false) && !A("100.64.0.1", true) && new[] { "127.0.0.1", "0.0.0.0", "169.254.1.1", "224.0.0.1", "240.0.0.1", "::1", "x", "" }
                .All(ip => !A(ip, false) && !A(ip, true)) && !A("8.8.8", false) && !A("08.8.8.8", false) && !A("198.18.0.1", false) && !A("255.255.255.255", false));
        bool before = A("203.0.113.5", false);
        PublicAddress.DocumentationRangesArePublic = true;
        bool seam = A("203.0.113.5", false) && A("198.51.100.7", false) && A("192.0.2.10", false);
        PublicAddress.DocumentationRangesArePublic = false;
        Check("advertisable: the documentation ranges are not public — only through the spike's seam", !before && seam && !A("203.0.113.5", true));
        IReadOnlyList<ServiceAnswer> Said(params string[] answers) => answers.Select((a, i) => new ServiceAnswer($"https://s{i}.example.com", a)).ToList();
        PublicAddress.DocumentationRangesArePublic = true;
        var pair = PublicAddress.Agree(Said("203.0.113.5\n", "203.0.113.5"));
        var two = PublicAddress.Agree(Said("198.51.100.7", "203.0.113.5", "203.0.113.5"));
        var disagree = PublicAddress.Agree(Said("203.0.113.5", "198.51.100.7"));
        var cgnat = PublicAddress.Agree(Said("100.64.1.2", "100.64.1.2", "203.0.113.5"));
        var none = PublicAddress.Agree(Said("<html>", ""), new[] { "s1.example.com: timeout" });
        PublicAddress.DocumentationRangesArePublic = false;
        Check("agree: an equal pair -> that address; two of three -> that address; two that differ -> none (disagree); a CGNAT pair -> refused (not public); fewer than two answers -> none with the reason",
            pair.Address == "203.0.113.5" && pair.Problem is null && two.Address == "203.0.113.5" && disagree.Address is null
            && disagree.Problem == L.T("core.setup.publicIp.disagree", new { answers = "s0.example.com: 203.0.113.5, s1.example.com: 198.51.100.7" })
            && cgnat.Address is null && cgnat.Problem == L.T("core.setup.publicIp.notPublic", new { address = "100.64.1.2" })
            && none.Address is null && none.Problem == L.T("core.setup.publicIp.none", new { error = "s1.example.com: timeout" }));
        PublicAddress.DocumentationRangesArePublic = true;
        var looked = PublicAddress.LookupAsync(new[] { "https://a.example.com", "https://b.example.com", "https://c.example.com" },
            (url, _) => url.Contains("b.") ? throw new HttpRequestException("refused") : Task.FromResult("203.0.113.9\n")).GetAwaiter().GetResult();
        PublicAddress.DocumentationRangesArePublic = false;
        Check("lookup: every service asked at once; one that fails costs its answer alone", looked.Address == "203.0.113.9"
            && looked.Answers.Select(a => a.Answer).SequenceEqual(new[] { "203.0.113.9", "", "203.0.113.9" }) && PublicAddress.ServiceName("https://api.ipify.org") == "api.ipify.org");
        string? logged = LastLogLine("public address from this PC:");
        if (logged is null) Console.WriteLine("        (skipped: relic.log could not be read — the lookup's log line is not looked at)");
        else
            Check("lookup: relic.log names the outcome and which services answered — never this PC's address or what a service said",
                !logged.Contains("203.0.113.9", StringComparison.Ordinal) && logged.Contains("two services agree", StringComparison.Ordinal)
                && logged.Contains("a.example.com answered", StringComparison.Ordinal) && logged.Contains("b.example.com gave no answer", StringComparison.Ordinal));

        Check("reach: ok when both answer, partial when the login port alone, fail otherwise",
            ReachCheck.Verdict(true, true) == "ok" && ReachCheck.Verdict(true, false) == "partial" && ReachCheck.Verdict(false, true) == "fail" && ReachCheck.Verdict(false, false) == "fail");
        Check("reach: the text per verdict and inside / outside",
            ReachCheck.TextKey("ok", true) == "qs.reach.okInside" && ReachCheck.TextKey("ok", false) == "qs.reach.okOutside" && ReachCheck.TextKey("partial", true) == "qs.reach.partial"
            && ReachCheck.TextKey("partial", false) == "qs.reach.partial" && ReachCheck.TextKey("fail", true) == "qs.reach.failInside" && ReachCheck.TextKey("fail", false) == "qs.reach.failOutside");
        try
        {
            var probed = new List<(string, int)>();
            ReachCheck.ProbeOverride = (host, port) => { probed.Add((host, port)); return Task.FromResult((port == 21000, port == 21000 ? "ok" : "refused")); };
            var reach = ReachCheck.RunAsync(" 203.0.113.9 ", 18080, inside: true).GetAwaiter().GetResult();
            Check("reach: the login port 21000 and the agent's port of the address, trimmed; the result carries what the page needs",
                probed.SequenceEqual(new[] { ("203.0.113.9", 21000), ("203.0.113.9", 18080) }) && reach is { SdkReachable: true, AgentReachable: false, Verdict: "partial", Inside: true, TextKey: "qs.reach.partial", SdkDetail: "ok" });
            // The log line names the outcome alone: the address probed — for a server on this PC that players on the Internet
            // join, this PC's own public address — never reaches relic.log, which users attach to a report.
            ReachCheck.ProbeOverride = (host, port) => Task.FromResult((false, port == 21000 ? "refused" : "timeout"));
            var silent = ReachCheck.RunAsync("198.51.100.231", 18086, inside: false).GetAwaiter().GetResult();
            string? reachLine = LastLogLine("reach check");
            if (reachLine is null) Console.WriteLine("        (skipped: relic.log could not be read — the reach check's log line is not looked at)");
            else
                Check("reach: relic.log names the outcome — the login port's answer and its detail, the agent's port, the verdict — never the address probed",
                    silent.Verdict == "fail" && !reachLine.Contains("198.51.100.231", StringComparison.Ordinal)
                    && reachLine.Contains("reach check: login port silent (refused), agent port 18086 silent -> fail", StringComparison.Ordinal));
        }
        finally { ReachCheck.ProbeOverride = null; }
    }

    /// <summary>The reach check's source: none of its log lines holds the address it probes (its own logging, read where it
    /// is written — every hole of every Log call of the file).</summary>
    static void ReachSource(string repo, Action<string, bool> Check)
    {
        string cs = File.ReadAllText(Path.Combine(repo, "app", "Relic.Core", "Setup", "ReachCheck.cs"));
        var logged = Regex.Matches(cs, @"Log\.(?:Info|Error|Write)\(\$?""[^\n]*").Select(m => m.Value).ToList();
        var holes = logged.SelectMany(l => Regex.Matches(l, @"\{([^{}]*)\}").Select(m => m.Groups[1].Value)).ToList();
        Check($"reach (source): ReachCheck logs {logged.Count} line(s), and no hole of them names the address it probes (host, address) — {string.Join(", ", holes)}",
            logged.Count > 0 && holes.Count > 0 && holes.All(h => !Regex.IsMatch(h, @"\b(?:host|address|h)\b")));
    }

    // ── SSH ──
    static byte[] Blob(string type, int bodyBytes)
    {
        var b = new List<byte>();
        void Str(byte[] s) { b.Add((byte)(s.Length >> 24)); b.Add((byte)(s.Length >> 16)); b.Add((byte)(s.Length >> 8)); b.Add((byte)s.Length); b.AddRange(s); }
        Str(Encoding.ASCII.GetBytes(type));
        Str(Enumerable.Range(0, bodyBytes).Select(i => (byte)(i * 7 + type.Length)).ToArray());
        return b.ToArray();
    }

    static void Ssh(string repo, Action<string, bool> Check)
    {
        Check("pins: the key is the host trimmed, brackets around it and its trailing dot dropped — an IP literal in its canonical form, the address a connection reaches (2001:0DB8:0:0:0:0:0:1 is 2001:db8::1, 192.0.2.010 is 192.0.2.8), any other host lower-cased —, a colon, the port (an IPv6 host told by the last colon)",
            HostKeyPins.Key("  Box.Example.COM. ", 22) == "box.example.com:22" && HostKeyPins.Key("2001:DB8::1", 2222) == "2001:db8::1:2222" && HostKeyPins.Key(null, 22) == ":22"
            && HostKeyPins.Key("[2001:DB8::1]", 2222) == "2001:db8::1:2222" && HostKeyPins.Key(" [box.example.com] ", 22) == "box.example.com:22"
            && HostKeyPins.Key("2001:0DB8:0:0:0:0:0:1", 22) == "2001:db8::1:22" && HostKeyPins.Key("[2001:db8:0::1]", 22) == "2001:db8::1:22"
            && HostKeyPins.Key("192.0.2.010", 22) == "192.0.2.8:22" && HostKeyPins.Key("192.0.2.10", 22) == "192.0.2.10:22");
        byte[] ed = Blob("ssh-ed25519", 32), rsa = Blob("ssh-rsa", 270), ecdsa = Blob("ecdsa-sha2-nistp256", 72);
        string fpEd = HostKeyPins.Fingerprint(ed), fpRsa = HostKeyPins.Fingerprint(rsa);
        Check("pins: the type comes from the key blob itself (its first SSH string) — never from the negotiated algorithm — and junk names none",
            HostKeyPins.KeyType(ed) == "ssh-ed25519" && HostKeyPins.KeyType(rsa) == "ssh-rsa" && HostKeyPins.KeyType(ecdsa) == "ecdsa-sha2-nistp256"
            && HostKeyPins.KeyType(new byte[] { 0, 0, 0, 9, 1, 2 }) == "" && HostKeyPins.KeyType(null) == "" && HostKeyPins.KeyType(new byte[] { 0, 0, 0, 2, 0x20, 0x41 }) == "");
        Check("pins: the fingerprint is OpenSSH's — SHA256: and the base64 of the blob's sha256 without padding",
            fpEd == "SHA256:" + Convert.ToBase64String(SHA256.HashData(ed)).TrimEnd('=') && !fpEd.EndsWith('=') && fpEd != fpRsa);
        var pins = new[] { new HostKeyPin("ssh-ed25519", fpEd) };
        Check("pins: ONE decision — nothing pinned = new, the pinned fingerprint = match, another = changed, and a second key type at a pinned host:port is changed too",
            HostKeyPins.Decide(null, fpEd) == HostKeyVerdict.New && HostKeyPins.Decide(Array.Empty<HostKeyPin>(), fpEd) == HostKeyVerdict.New
            && HostKeyPins.Decide(pins, fpEd) == HostKeyVerdict.Match && HostKeyPins.Decide(pins, fpRsa) == HostKeyVerdict.Changed
            && HostKeyPins.Decide(pins, fpEd.ToLowerInvariant()) == HostKeyVerdict.Changed && HostKeyPins.Name(HostKeyVerdict.Changed) == "changed");
        Check("pins: trusted — no check at all (a caller without pins); a pinned key; a key seen first by a run that records it after; the very fingerprint the user confirmed — and NOT a changed key, nor any other than the confirmed one",
            HostKeyPins.Trusts(null, fpRsa) && HostKeyPins.Trusts(new HostKeyCheck(pins), fpEd) && HostKeyPins.Trusts(new HostKeyCheck(Array.Empty<HostKeyPin>()), fpRsa)
            && !HostKeyPins.Trusts(new HostKeyCheck(pins), fpRsa) && HostKeyPins.Trusts(new HostKeyCheck(Array.Empty<HostKeyPin>(), fpRsa), fpRsa)
            && HostKeyPins.Trusts(new HostKeyCheck(pins, fpEd), fpEd)
            && !HostKeyPins.Trusts(new HostKeyCheck(Array.Empty<HostKeyPin>(), fpEd), fpRsa) && !HostKeyPins.Trusts(new HostKeyCheck(pins, fpRsa, AcceptChanged: true), fpEd)
            && !HostKeyPins.Trusts(new HostKeyCheck(pins), ""));
        Check("pins: a confirmed fingerprint the pins call changed is trusted only when it was confirmed as a changed key (the answer to the changed-key warning) — a confirmed one alone does not lift the refusal",
            !HostKeyPins.Trusts(new HostKeyCheck(pins, fpRsa), fpRsa) && HostKeyPins.Trusts(new HostKeyCheck(pins, fpRsa, AcceptChanged: true), fpRsa)
            && HostKeyPins.Trusts(new HostKeyCheck(pins, fpEd, AcceptChanged: true), fpEd));
        Check("pins: described for the texts as \"<type> <fingerprint>\"", HostKeyPins.Describe(pins) == "ssh-ed25519 " + fpEd && HostKeyPins.Describe(null) == "");
        string[] offered = { "ssh-ed25519-cert-v01@openssh.com", "rsa-sha2-512-cert-v01@openssh.com", "ssh-rsa-cert-v01@openssh.com", "ssh-ed25519",
            "ecdsa-sha2-nistp256", "rsa-sha2-512", "rsa-sha2-256", "ssh-rsa" };
        Check("pins: a pinned host offers only the algorithms of the pinned type, in the client's order — an RSA key's SHA-2 signatures with it; no pin, a pin of no known type, or a type nothing offered fits: every algorithm",
            HostKeyPins.AlgorithmsFor(offered, new[] { new HostKeyPin("ssh-rsa", fpRsa) })!.SequenceEqual(new[] { "rsa-sha2-512", "rsa-sha2-256", "ssh-rsa" })
            && HostKeyPins.AlgorithmsFor(offered, pins)!.SequenceEqual(new[] { "ssh-ed25519" })
            && HostKeyPins.AlgorithmsFor(offered, new[] { new HostKeyPin("ssh-rsa-cert-v01@openssh.com", fpRsa) })!.SequenceEqual(new[] { "rsa-sha2-512-cert-v01@openssh.com", "ssh-rsa-cert-v01@openssh.com" })
            && HostKeyPins.AlgorithmsFor(offered, null) is null && HostKeyPins.AlgorithmsFor(offered, new[] { new HostKeyPin("", fpEd) }) is null
            && HostKeyPins.AlgorithmsFor(offered, new[] { new HostKeyPin("ssh-ed448", fpEd) }) is null
            && HostKeyPins.AlgorithmsFor(offered, new[] { new HostKeyPin("ssh-ed25519", fpEd), new HostKeyPin("", fpRsa) }) is null
            && HostKeyPins.KeyTypeOf("rsa-sha2-256") == "ssh-rsa" && HostKeyPins.KeyTypeOf("ecdsa-sha2-nistp256") == "ecdsa-sha2-nistp256");

        string cs = File.ReadAllText(Path.Combine(repo, "app", "Relic.Core", "Server", "AgentDeployer.cs")).Replace("\r\n", "\n");
        string open = Body(cs, "private static Session Open(SshTarget ssh, Action<string>? log, HostKeyCheck? hostKeys = null)");
        string read = Body(cs, "public static HostKeySeen ReadHostKey(");
        int client = open.IndexOf("session.Client.HostKeyReceived += (_, e) =>", StringComparison.Ordinal);
        int sftp = open.IndexOf("session.Sftp.HostKeyReceived += (_, e) =>", StringComparison.Ordinal);
        int connect = open.IndexOf("current.Client.Connect();", StringComparison.Ordinal);
        int sftpConnect = open.IndexOf("current.Sftp.Connect();", StringComparison.Ordinal);
        Check("ssh (source): Open puts a handler on BOTH the SSH and the SFTP connection of every attempt before either connects; the SSH one trusts by HostKeyPins.Trusts, the SFTP one only the very key the first trusted",
            client > 0 && sftp > client && connect > sftp && sftpConnect > connect && open.Contains("e.CanTrust = HostKeyPins.Trusts(hostKeys, fingerprint);", StringComparison.Ordinal)
            && open.Contains("string.Equals(fingerprint, session.Fingerprint, StringComparison.Ordinal)", StringComparison.Ordinal)
            && open.Contains("&& HostKeyPins.Trusts(hostKeys, fingerprint);", StringComparison.Ordinal) && open.Contains("throw new HostKeyRefusedException(RefusalText(ssh, seen, hostKeys), seen);", StringComparison.Ordinal)
            && !open.Contains("e.CanTrust = true", StringComparison.Ordinal));
        Check("ssh (source): with pins only the pinned type is offered, and a server without a key of that type is asked once more with every algorithm — the same checks on that attempt; the refusal names the pins, never a confirmed key as one trusted before",
            open.Contains("bool restricted = PreferPinnedTypes(info, hostKeys?.Pins);", StringComparison.Ordinal)
            && open.Contains("catch (SshConnectionException ex) when (restricted && current.Fingerprint.Length == 0 && ex.DisconnectReason == DisconnectReason.KeyExchangeFailed)", StringComparison.Ordinal)
            && open.Contains("current = Make(Info());", StringComparison.Ordinal) && open.Contains("string pinned = HostKeyPins.Describe(hostKeys?.Pins);", StringComparison.Ordinal)
            && !open.Contains("Accepted", StringComparison.Ordinal) && read.Contains("PreferPinnedTypes(ci, pins)", StringComparison.Ordinal)
            && read.Contains("if (Read(preferPinned: true)) Read(preferPinned: false);", StringComparison.Ordinal));
        Check("ssh (source): the key's type from its blob and the fingerprint from FingerPrintSHA256 — never HostKeyName anywhere; phase key never trusts (no authentication, no password)",
            !cs.Contains("HostKeyName", StringComparison.Ordinal) && Regex.Matches(cs, Regex.Escape("HostKeyPins.KeyType(e.HostKey)")).Count == 3
            && Regex.Matches(cs, Regex.Escape("\"SHA256:\" + e.FingerPrintSHA256")).Count == 3 && read.Contains("e.CanTrust = false;", StringComparison.Ordinal)
            && read.Contains("new NoneAuthenticationMethod(u)", StringComparison.Ordinal) && !read.Contains("Password", StringComparison.Ordinal));
        string refusal = Body(cs, "private static string RefusalText(SshTarget ssh, HostKeySeen seen, HostKeyCheck? hostKeys)");
        Check("ssh (source): a key other than the confirmed one is refused with its own text naming the confirmed key; any other refusal with the changed-key text naming the pins",
            refusal.Contains("L.T(\"core.deploy.hostKeyNotConfirmed\"", StringComparison.Ordinal) && refusal.Contains("L.T(\"core.deploy.hostKeyChanged\"", StringComparison.Ordinal)
            && refusal.Contains("pinned = seen.Pinned", StringComparison.Ordinal) && refusal.Contains("accepted", StringComparison.Ordinal));

        // What the client really offers, seen by a loopback stand-in server that holds no key the client knows: a
        // pinned RSA host is offered RSA alone first, then — the server holding none — every algorithm once more; an
        // unpinned one every algorithm, once. No key is presented and nothing is authenticated.
        var rsaPin = new[] { new HostKeyPin("ssh-rsa", fpRsa) };
        string[] rsaOnly = { "rsa-sha2-512", "rsa-sha2-256", "ssh-rsa" };
        using (var standIn = new StandInSsh(2))
        {
            var e1 = Catch(() => AgentDeployer.ReadHostKey("127.0.0.1", standIn.Port, "root", rsaPin));
            var seen = standIn.Wait(2);
            Check($"ssh (stand-in server): phase key with an RSA pin offers {string.Join(",", seen.ElementAtOrDefault(0) ?? new[] { "nothing" })} first, then every algorithm once more ({seen.ElementAtOrDefault(1)?.Length ?? 0}); a server with neither ends it with the connection's own error",
                seen.Count == 2 && seen[0] is { } first && first.SequenceEqual(rsaOnly) && seen[1] is { Length: > 3 } second && second.Contains("ssh-ed25519") && second.Contains("rsa-sha2-512")
                && e1 is Renci.SshNet.Common.SshConnectionException);
        }
        using (var standIn = new StandInSsh(2))
        {
            var target = new SshTarget("127.0.0.1", standIn.Port, "root", "stand-in-password", "", "", "");
            var e2 = Catch(() => AgentDeployer.Probe(target, fpRsa, rsaPin, Array.Empty<string>(), null));
            var seen = standIn.Wait(2);
            Check("ssh (stand-in server): an install's connections with an RSA pin offer RSA alone first, then every algorithm once more (Open, through phase box)",
                seen.Count == 2 && seen[0] is { } first && first.SequenceEqual(rsaOnly) && seen[1] is { Length: > 3 } second && second.Contains("ssh-ed25519")
                && e2 is Renci.SshNet.Common.SshConnectionException);
        }
        using (var standIn = new StandInSsh(2))
        {
            var e3 = Catch(() => AgentDeployer.ReadHostKey("127.0.0.1", standIn.Port, "root", null));
            var seen = standIn.Wait(1);
            Check("ssh (stand-in server): without pins every algorithm is offered, once",
                seen.Count == 1 && seen[0] is { Length: > 3 } all && all.Contains("ssh-ed25519") && all.Contains("ssh-rsa") && standIn.Accepted == 1
                && e3 is Renci.SshNet.Common.SshConnectionException);
        }
        // The tunnel of a legacy Mode: ssh record (AgentClient) offers what the deployer's connections offer: with an RSA pin
        // RSA alone first, then — the server holding none — every algorithm once more; with no pin every algorithm, once.
        // The pins are handed in, so the state file of this PC is never read; the stand-in presents no key, nothing goes
        // through the tunnel and no port is forwarded — and a connect that failed reports no key (TunnelKey), so nothing
        // could ever be pinned from it.
        bool tunnelKeyReported = false;
        Exception? Tunnel(int port, IReadOnlyList<HostKeyPin> tunnelPins) => Catch(() =>
        {
            var client = new AgentClient(new ServerConfig("127.0.0.1", port, "root", "stand-in-password", "stand-in-token", 18080, "ssh"), tunnelPins);
            try { client.ConnectAsync().GetAwaiter().GetResult(); }
            finally
            {
                tunnelKeyReported |= client.TunnelKey is not null;
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
        using (var standIn = new StandInSsh(2))
        {
            var e4 = Tunnel(standIn.Port, rsaPin);
            var seen = standIn.Wait(2);
            Check("ssh (stand-in server): the tunnel of a legacy Mode: ssh record with an RSA pin offers RSA alone first, then every algorithm once more — the pinned type, as the deployer's connections",
                seen.Count == 2 && seen[0] is { } first && first.SequenceEqual(rsaOnly) && seen[1] is { Length: > 3 } second && second.Contains("ssh-ed25519")
                && e4 is Renci.SshNet.Common.SshConnectionException);
        }
        using (var standIn = new StandInSsh(2))
        {
            var e5 = Tunnel(standIn.Port, Array.Empty<HostKeyPin>());
            var seen = standIn.Wait(1);
            Check("ssh (stand-in server): the tunnel with no pin offers every algorithm, once",
                seen.Count == 1 && seen[0] is { Length: > 3 } all && all.Contains("ssh-ed25519") && all.Contains("ssh-rsa") && standIn.Accepted == 1
                && e5 is Renci.SshNet.Common.SshConnectionException);
        }
        Check("ssh (stand-in server): a tunnel whose connect failed reports no key (AgentClient.TunnelKey stays null), with a pin and without one",
            !tunnelKeyReported);
        Check("ssh (source): an install and an update take the pins and hand them to Open",
            cs.Contains("public static DeployResult Install(SshTarget ssh, AgentInstallPlan plan, string agentDir, Action<string>? log = null, HostKeyCheck? hostKeys = null)", StringComparison.Ordinal)
            && cs.Contains("public static DeployResult Upgrade(SshTarget ssh, string agentDir, bool force = false, Action<string>? log = null, HostKeyCheck? hostKeys = null)", StringComparison.Ordinal)
            && Regex.Matches(cs, Regex.Escape("using var session = Open(ssh, log, hostKeys);")).Count == 2);

        // The read-only look at a box.
        string[] dirs = { "/home/2.8_live", "/srv/it's here" };
        string script = AgentDeployer.ProbeBoxScript(dirs);
        Check("box script: reads nothing of /etc/gio-agent/config but its GIO_DIR_ lines (and whether it exists), names no token, asks no address service",
            Regex.Matches(script, "/etc/gio-agent/config").Count == 3 && script.Contains("grep -E '^[[:space:]]*GIO_DIR_[0-9]+[[:space:]]*=' /etc/gio-agent/config | sed 's/^/S /'")
            && script.Contains("if [ -e /etc/gio-agent/config ]; then echo 'AG yes'") && !script.Contains("TOKEN") && !script.Contains("cat ")
            && !script.Contains("http") && !script.Contains("curl -") && !script.Contains("wget -"));
        Check("box script: every path quoted, stdin /dev/null, the kind and the free space of each folder, the tools, the system, the memory, the addresses, exit 0",
            script.StartsWith("exec </dev/null\n", StringComparison.Ordinal) && script.EndsWith("exit 0\n", StringComparison.Ordinal)
            && script.Contains("echo \"P0 $e $(k '/home/2.8_live')\"; echo \"F0 $(d '/home/2.8_live')\"") && script.Contains(@"'/srv/it'\''s here'")
            && script.Contains("df -B1 --output=avail,target") && script.Contains("for t in 7zz 7z 7za 7zr bsdtar;") && script.Contains("/etc/os-release")
            && script.Contains("MemTotal") && script.Contains("ip -4 -o addr show scope global") && script.Contains("ip -4 route get 203.0.113.1"));
        string[] services = { "https://api.ipify.org", "https://x.example.com/it's" };
        string pub = AgentDeployer.PublicIpScript(services);
        Check("public address script: asked only when wanted, as its own script — each URL quoted, curl -4 then wget -4, 5 s each, 64 characters of the answer",
            pub.Contains("curl -4 -fsS --max-time 5 \"$1\"") && pub.Contains("wget -4 -qO- -T 5 \"$1\"") && pub.Contains("echo \"U0 $(u 'https://api.ipify.org' | head -c 64")
            && pub.Contains(@"'https://x.example.com/it'\''s'") && pub.StartsWith("exec </dev/null\n", StringComparison.Ordinal));
        var parsedIp = AgentDeployer.ParsePublicIp("U1 198.51.100.4\r\nU0 \nU7 x\nnoise\n", services);
        Check("public address script: its answers read per service, in order; a service without a line answers nothing",
            parsedIp.Select(a => a.Answer).SequenceEqual(new[] { "", "198.51.100.4" }) && parsedIp[1].Service == services[1]);
        const string output = "P0 yes stack\r\nF0 51539607552 /home\r\nP1 no absent\r\nF1 1073741824 /mnt/my disk\r\nS GIO_DIR_28=/home/2.8_live\r\nAG yes\r\nPY yes\r\nDC no\r\nZ 7za\r\n"
            + "CU yes\r\nWG no\r\nO ID=\"almalinux\"\r\nO VERSION_ID=\"9.4\"\r\nM MemTotal:       16337652 kB\r\n"
            + "A 2: eth0    inet 192.0.2.10/24 brd 192.0.2.255 scope global dynamic eth0\\       valid_lft 85000sec\r\nA 3: wg0    inet 10.8.0.1/24 scope global wg0\r\n"
            + "R 203.0.113.1 via 192.0.2.1 dev eth0 src 192.0.2.10 uid 0 \r\nnoise\r\n";
        var box = AgentDeployer.ParseBox(output, dirs);
        Check("box output: every line read whatever its end — kinds, free space and mount (a blank in it), the stored folder, the tools, the system, the memory, the addresses and the route's source",
            box.Kinds["/home/2.8_live"] == FolderKind.Stack && box.Exists["/home/2.8_live"] && !box.Exists[dirs[1]] && box.Free["/home/2.8_live"] == new BoxFree(51539607552, "/home")
            && box.Free[dirs[1]] == new BoxFree(1073741824, "/mnt/my disk") && box.StoredDir("2.8") == "/home/2.8_live" && box.AgentInstalled && box.HasPython && !box.HasDocker
            && box.SevenZip == "7za" && box.HasCurl && !box.HasWget && box.OsId == "almalinux" && box.OsVersion == "9.4" && box.MemTotal == 16337652L * 1024
            && box.Addresses.SequenceEqual(new[] { "192.0.2.10", "10.8.0.1" }) && box.RouteSource == "192.0.2.10" && box.HasAddress("192.0.2.10") && !box.HasAddress("203.0.113.1"));
        var empty = AgentDeployer.ParseBox("F0\nZ no\nM junk\n", dirs);
        Check("box output: a folder whose free space was not read, no 7z tool, no memory line and null output read as nothing",
            empty.Free["/home/2.8_live"] == new BoxFree(null, "") && empty.SevenZip == "" && empty.MemTotal is null && AgentDeployer.ParseBox(null, dirs).Addresses.Count == 0);
        Check("sudo's answers: not in the sudoers, no rule for the host, a terminal required = the user may not use it; a wrong password is a refusal",
            AgentDeployer.SudoForbids("raf is not in the sudoers file.  This incident will be reported.") && AgentDeployer.SudoForbids("Sorry, user raf may not run sudo on box.")
            && AgentDeployer.SudoForbids("sudo: sorry, you must have a tty to run sudo") && !AgentDeployer.SudoForbids("Sorry, try again.\nsudo: 1 incorrect password attempt")
            && !AgentDeployer.SudoForbids(null));
    }

    /// <summary>A loopback stand-in SSH server: per connection it sends its version and a KEXINIT whose only host key
    /// algorithm no client knows, reads the client's KEXINIT and records the host key algorithms the client offered
    /// there, then closes — the client's key exchange fails, no key is ever presented and nothing is
    /// authenticated.</summary>
    sealed class StandInSsh : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly List<string[]?> _offered = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;
        private int _accepted;

        public StandInSsh(int connections)
        {
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            _run = Task.Run(async () =>
            {
                for (int i = 0; i < connections; i++)
                {
                    System.Net.Sockets.TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (Exception) { return; }
                    Interlocked.Increment(ref _accepted);
                    using (c) Serve(c);
                }
            });
        }

        public int Port { get; }

        /// <summary>The connections accepted so far.</summary>
        public int Accepted => Volatile.Read(ref _accepted);

        /// <summary>What the clients offered, once <paramref name="count"/> connections were served (10 s at most).</summary>
        public List<string[]?> Wait(int count)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until)
            {
                lock (_offered) if (_offered.Count >= count) break;
                Thread.Sleep(20);
            }
            lock (_offered) return _offered.ToList();
        }

        private void Serve(System.Net.Sockets.TcpClient c)
        {
            string[]? offered = null;
            try
            {
                c.ReceiveTimeout = 10_000;
                c.SendTimeout = 10_000;
                var s = c.GetStream();
                s.Write(Encoding.ASCII.GetBytes("SSH-2.0-RelicStandIn_1.0\r\n"));
                for (int b = s.ReadByte(); b != '\n'; b = s.ReadByte())
                    if (b < 0) throw new EndOfStreamException();
                s.Write(Packet(KexInit()));
                byte[] payload = ReadPacket(s);
                if (payload.Length > 17 && payload[0] == 20) offered = NameList(payload, 1);
            }
            catch (Exception) { /* recorded as nothing offered */ }
            lock (_offered) _offered.Add(offered);
        }

        private static byte[] KexInit()
        {
            var b = new List<byte> { 20 };
            b.AddRange(new byte[16]);
            const string ciphers = "aes128-ctr,aes256-ctr,aes128-gcm@openssh.com,chacha20-poly1305@openssh.com", macs = "hmac-sha2-256,hmac-sha2-512,hmac-sha1";
            foreach (var list in new[] { "curve25519-sha256,curve25519-sha256@libssh.org,ecdh-sha2-nistp256,diffie-hellman-group14-sha256",
                         "x-relic-stand-in@example.com", ciphers, ciphers, macs, macs, "none", "none", "", "" })
            {
                byte[] bytes = Encoding.ASCII.GetBytes(list);
                b.AddRange(Be32(bytes.Length));
                b.AddRange(bytes);
            }
            b.Add(0);
            b.AddRange(Be32(0));
            return b.ToArray();
        }

        private static byte[] Be32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

        /// <summary>A binary packet before any key is in use: its length, the padding's length, the payload, the padding.</summary>
        private static byte[] Packet(byte[] payload)
        {
            int pad = 8 - (5 + payload.Length) % 8;
            if (pad < 4) pad += 8;
            int length = 1 + payload.Length + pad;
            var b = new byte[4 + length];
            Be32(length).CopyTo(b, 0);
            b[4] = (byte)pad;
            payload.CopyTo(b, 5);
            return b;
        }

        private static byte[] ReadPacket(Stream s)
        {
            var head = new byte[4];
            s.ReadExactly(head, 0, 4);
            int length = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
            if (length is < 5 or > 35_000) throw new InvalidDataException("packet length " + length);
            var body = new byte[length];
            s.ReadExactly(body, 0, length);
            return body[1..(length - body[0])];
        }

        /// <summary>The <paramref name="index"/>-th name-list of a KEXINIT payload (after its type and its cookie).</summary>
        private static string[] NameList(byte[] p, int index)
        {
            int at = 17;
            for (int i = 0; ; i++)
            {
                int len = (p[at] << 24) | (p[at + 1] << 16) | (p[at + 2] << 8) | p[at + 3];
                if (i == index) return Encoding.ASCII.GetString(p, at + 4, len).Split(',', StringSplitOptions.RemoveEmptyEntries);
                at += 4 + len;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch (Exception) { /* best effort */ }
            try { _run.Wait(2000); } catch (Exception) { /* best effort */ }
            _stop.Dispose();
        }
    }

    /// <summary>The box script run for real by Git Bash over temp folders, when this PC has it.</summary>
    static void BoxScriptRun(string tmp, Action<string, bool> Check)
    {
        string? bash = new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") }
            .Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.Combine(p!, "Git", "bin", "bash.exe")).FirstOrDefault(File.Exists);
        if (bash is null)
        {
            Console.WriteLine("        (skipped: no Git Bash on this PC — the box script is not run for real)");
            return;
        }
        static string Posix(string p) => "/" + char.ToLowerInvariant(p[0]) + p[2..].Replace('\\', '/');
        string root = Path.Combine(tmp, "box");
        string stack = Path.Combine(root, "stack"), empty = Path.Combine(root, "empty"), absent = Path.Combine(root, "absent", "deeper");
        Directory.CreateDirectory(stack);
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(stack, "docker-compose.yml.tmpl"), "x");
        string[] dirs = { Posix(stack), Posix(empty), Posix(absent) };
        string file = Path.Combine(root, "box.sh");
        File.WriteAllText(file, AgentDeployer.ProbeBoxScript(dirs), new UTF8Encoding(false));
        var psi = new ProcessStartInfo(bash) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(Posix(file));
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEndAsync();
        string stdout = proc.StandardOutput.ReadToEnd();
        bool ended = proc.WaitForExit(30_000);
        err.Wait(5000);
        var facts = AgentDeployer.ParseBox(stdout, dirs);
        Check("box script (Git Bash): it runs to exit 0 and tells a stack, an empty folder and an absent one apart, with the free space of each folder's nearest existing parent",
            ended && proc.ExitCode == 0 && facts.Kinds[dirs[0]] == FolderKind.Stack && facts.Kinds[dirs[1]] == FolderKind.Empty && facts.Kinds[dirs[2]] == FolderKind.Absent
            && dirs.All(d => facts.Free.TryGetValue(d, out var f) && f.Avail is > 0) && !err.Result.Contains("syntax error", StringComparison.OrdinalIgnoreCase));
    }

    // ── Authenticode ──
    static void AuthenticodeRules(string repo, string tmp, Action<string, bool> Check)
    {
        string unsigned = Path.Combine(tmp, "unsigned.exe");
        File.WriteAllBytes(unsigned, Encoding.ASCII.GetBytes("MZ not really a program"));
        Check("authenticode: an unsigned file is refused (Windows' verdict is not 0) and names no signer; a file that is not there is unreadable",
            !Authenticode.Verify(unsigned, "Docker Inc") && Authenticode.Check(unsigned, "Docker Inc") == AuthenticodeVerdict.NotTrusted
            && Authenticode.TrustResult(unsigned) != 0 && Authenticode.SignerSubject(unsigned) is null
            && Authenticode.Check(Path.Combine(tmp, "missing.exe"), "Docker Inc") == AuthenticodeVerdict.Unreadable && Authenticode.TrustResult(Path.Combine(tmp, "missing.exe")) != 0);
        Check("authenticode: the signer is the certificate's organisation (O) — every O, at least one; a CN that names it beside another organisation, or alone, is not; letter case and runs of blanks not counted, punctuation counted",
            Authenticode.SubjectNames("CN=Docker Inc, O=Docker Inc, L=Palo Alto, S=California, C=US", "Docker Inc")
            && Authenticode.SubjectNames("CN=docker  inc, O= Docker  inc, C=US", " Docker Inc ") && Authenticode.SubjectNames("CN=.NET, O=Microsoft Corporation, C=US", "Microsoft Corporation")
            && !Authenticode.SubjectNames("CN=Docker Inc, O=Evil Ltd, C=US", "Docker Inc") && !Authenticode.SubjectNames("CN=Docker Inc", "Docker Inc")
            && !Authenticode.SubjectNames("CN=Docker Inc, O=Docker Inc, O=Evil Ltd", "Docker Inc")
            && !Authenticode.SubjectNames("CN=Evil, O=Docker Inc Ltd", "Docker Inc") && !Authenticode.SubjectNames("CN=X, O=\"Docker, Inc.\"", "Docker Inc")
            && !Authenticode.SubjectNames("L=Docker Inc, C=US", "Docker Inc") && !Authenticode.SubjectNames((string?)null, "Docker Inc")
            && !Authenticode.SubjectNames("CN=Docker Inc, O=Docker Inc", "") && !Authenticode.SubjectNames("not a name", "Docker Inc")
            && !Authenticode.SubjectNames("CN=Docker Inc + O=Docker Inc", "Docker Inc"));
        string auth = File.ReadAllText(Path.Combine(repo, "app", "Relic.Core", "Setup", "Authenticode.cs")).Replace("\r\n", "\n");
        Check("authenticode (source): revocation is asked online for the whole chain but its root (never from the cache alone), through the very handle the caller holds, and the signer read from the signature Windows checked",
            auth.Contains("fdwRevocationChecks = WtdRevokeWholeChain,", StringComparison.Ordinal) && auth.Contains("dwProvFlags = WtdRevocationCheckChainExcludeRoot,", StringComparison.Ordinal)
            && !auth.Contains("CacheOnly", StringComparison.Ordinal) && !auth.Contains("WtdRevokeNone", StringComparison.Ordinal)
            && auth.Contains("hFile = handle", StringComparison.Ordinal) && auth.Contains("WTHelperGetProvSignerFromChain(provider, 0, false, 0)", StringComparison.Ordinal));
        string dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet)) Console.WriteLine("        (skipped: no dotnet.exe in Program Files — no signed file to verify)");
        else
        {
            var forMicrosoft = Authenticode.Check(dotnet, "Microsoft Corporation");
            if (forMicrosoft == AuthenticodeVerdict.RevocationUnknown)
                Console.WriteLine("        (skipped: Windows could not ask whether dotnet.exe's certificate was revoked — offline?)");
            else
            {
                bool held;
                using (var open = new FileStream(dotnet, FileMode.Open, FileAccess.Read, FileShare.Read))
                    held = Authenticode.Check(open, "Microsoft Corporation") == AuthenticodeVerdict.Trusted && Authenticode.Check(open, "Docker Inc") == AuthenticodeVerdict.OtherSigner;
                Check("authenticode: a file signed by its publisher passes for that publisher alone (dotnet.exe: Microsoft Corporation, not Docker Inc), also through a handle held open for reading",
                    forMicrosoft == AuthenticodeVerdict.Trusted && Authenticode.Verify(dotnet, "Microsoft Corporation") && !Authenticode.Verify(dotnet, "Docker Inc") && held);
            }
        }
    }

    // ── the installs' own rules ──
    static void Installs(string repo, string tmp, Action<string, bool> Check)
    {
        Check("docker: a per-user, quiet install on WSL 2, and --accept-license only when the user ticked the agreement",
            SetupInstall.DockerInstallerArguments(false).SequenceEqual(new[] { "install", "--user", "--quiet", "--backend=wsl-2" })
            && SetupInstall.DockerInstallerArguments(true).SequenceEqual(new[] { "install", "--user", "--quiet", "--backend=wsl-2", "--accept-license" }));
        string setupDir = Path.Combine(repo, "app", "Relic.Core", "Setup");
        var runas = Directory.GetFiles(setupDir, "*.cs").SelectMany(f => File.ReadAllLines(f).Select((l, i) => (File: Path.GetFileName(f), Line: i, Text: l)))
            .Where(x => x.Text.Contains("runas", StringComparison.Ordinal)).ToList();
        string install = File.ReadAllText(Path.Combine(setupDir, "SetupInstall.cs")).Replace("\r\n", "\n");
        string enable = Body(install, "public static async Task<WslDone> EnableWslAsync("), update = Body(install, "public static async Task<WslDone> UpdateWslAsync(");
        string docker = Body(install, "public static async Task<DockerInstalled> DockerAsync(");
        Check("elevation: in Relic.Core/Setup \"runas\" appears only inside EnableWslAsync and UpdateWslAsync — the Docker install runs unelevated (UseShellExecute = false)",
            runas.Count == 2 && runas.All(x => x.File == "SetupInstall.cs") && enable.Contains("Verb = \"runas\"", StringComparison.Ordinal)
            && update.Contains("Verb = \"runas\"", StringComparison.Ordinal) && !docker.Contains("runas", StringComparison.Ordinal)
            && docker.Contains("UseShellExecute = false", StringComparison.Ordinal) && enable.Contains("\"--install --no-distribution\"", StringComparison.Ordinal)
            && update.Contains("\"--update\"", StringComparison.Ordinal));
        string local = File.ReadAllText(Path.Combine(repo, "app", "Relic.Core", "Server", "LocalAgent.cs")).Replace("\r\n", "\n");
        string openPort = Body(local, "public static string? OpenFirewallPort(int port, bool gamePorts = false)");
        Check("elevation: in LocalAgent \"runas\" appears only in its firewall pair",
            Regex.Matches(local, "runas").Count == 2 && openPort.Contains("Verb = \"runas\"")
            && Body(local, "public static string? RemoveFirewallRule(int port)").Contains("Verb = \"runas\""));
        Check("firewall (source): every click writes the agent's rule anew in the one prompt — no early end because a rule of that name is there — and asks only whether the game ports' rules are",
            openPort.Contains("FirewallCommandLine(port, gamePorts, python?.ExeW, present.Contains)", StringComparison.Ordinal)
            && openPort.Contains("new[] { ServerRuleTcp, ServerRuleUdp }.Where(RuleExists)", StringComparison.Ordinal) && !openPort.Contains("return null;", StringComparison.Ordinal)
            && openPort.Contains("\"core.deploy.uacDeclinedRules\"", StringComparison.Ordinal));
        string start = Body(install, "public static async Task<DockerStarted> DockerStartAsync(");
        Check("docker (source): Docker Desktop is started as this user and never through the shell (a program that wants administrator rights is refused, error 740, not prompted for)",
            start.Contains("UseShellExecute = false", StringComparison.Ordinal) && !start.Contains("UseShellExecute = true", StringComparison.Ordinal)
            && start.Contains("ex.NativeErrorCode == 740", StringComparison.Ordinal) && start.Contains("\"core.setup.docker.startElevated\"", StringComparison.Ordinal));
        int heldAt = docker.IndexOf("held = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);", StringComparison.Ordinal);
        int checkAt = docker.IndexOf("Authenticode.Check(held, entry.Signer)", StringComparison.Ordinal);
        int runAt = docker.IndexOf("p = Process.Start(psi);", StringComparison.Ordinal);
        int letGo = docker.IndexOf("if (p is null)", StringComparison.Ordinal);
        Check("docker (source): the installer is held open for reading alone from its check until it has started — checked through that handle, started inside it, let go only after; anything but a trusted signer — a revocation that could not be asked included, in its own words — deletes it and runs nothing",
            heldAt > 0 && checkAt > heldAt && runAt > checkAt && letGo > runAt && docker.Contains("using (held)", StringComparison.Ordinal)
            && docker.Contains("if (verdict != AuthenticodeVerdict.Trusted)", StringComparison.Ordinal)
            && docker.Contains("verdict == AuthenticodeVerdict.RevocationUnknown ? \"core.setup.revocationUnknown\" : \"core.setup.badSignature\"", StringComparison.Ordinal));
        Check("elevation: the two WSL prompts start in System32, like the firewall pair",
            enable.Contains("WorkingDirectory = Environment.SystemDirectory", StringComparison.Ordinal) && update.Contains("WorkingDirectory = Environment.SystemDirectory", StringComparison.Ordinal));

        string tools = Path.Combine(tmp, "tools-install");
        SetupTools.ToolsDirOverride = tools;
        try
        {
            // A pinned download of the pinned size that is not the pinned file: deleted, badHash, nothing installed.
            SetupInstall.DownloadOverride = (url, dest, _, _) => { File.WriteAllText(dest, "not the file"); return Task.CompletedTask; };
            var wrong = new ToolEntry("26.04", "https://x.example.com/26.04/7zr.exe", 12, new string('c', 64));
            var e1 = Catch(() => SetupInstall.SevenZipAsync(wrong).GetAwaiter().GetResult());
            Check("pinned download: a file of the pinned size that is not the pinned one (its sha256 differs) is deleted and refused (badHash); nothing is installed",
                e1 is SetupInstallException { Code: "badHash" } && !File.Exists(SetupTools.SevenZipExe)
                && !Directory.EnumerateFiles(SetupTools.DownloadDir).Any());
            var e2 = Catch(() => SetupInstall.PythonAsync(new ToolEntry("3.14.8", "https://x.example.com/p.zip", 0, "")).GetAwaiter().GetResult());
            var e3 = Catch(() => SetupInstall.DockerAsync(null, false).GetAwaiter().GetResult());
            Check("pinned download: an entry this build cannot use is toolMissing before anything is downloaded",
                e2 is SetupInstallException { Code: "toolMissing" } && e3 is SetupInstallException { Code: "toolMissing" });
            // Windows' own where.exe stands in for 7zr.exe — it starts and ends at once.
            string where = Path.Combine(Environment.SystemDirectory, "where.exe");
            byte[] whereBytes = File.ReadAllBytes(where);
            var pinned = new ToolEntry("26.04", "https://x.example.com/26.04/7zr.exe", whereBytes.Length, Convert.ToHexString(SHA256.HashData(whereBytes)));
            // The size half of the pin: the very bytes whose sha256 the entry names, against an entry one byte longer and
            // one byte shorter.
            SetupInstall.DownloadOverride = (url, dest, _, _) => { File.WriteAllBytes(dest, whereBytes); return Task.CompletedTask; };
            var eLonger = Catch(() => SetupInstall.SevenZipAsync(pinned with { Size = whereBytes.Length + 1 }).GetAwaiter().GetResult());
            bool longerLeftNothing = !File.Exists(SetupTools.SevenZipExe) && !Directory.EnumerateFiles(SetupTools.DownloadDir).Any();
            var eShorter = Catch(() => SetupInstall.SevenZipAsync(pinned with { Size = whereBytes.Length - 1 }).GetAwaiter().GetResult());
            Check("pinned download: the pinned file's own bytes against an entry one byte longer, or one byte shorter, are refused all the same (badHash) — the size is half of the pin; nothing is installed and the download is deleted",
                eLonger is SetupInstallException { Code: "badHash" } && longerLeftNothing
                && eShorter is SetupInstallException { Code: "badHash" } && !File.Exists(SetupTools.SevenZipExe)
                && !Directory.EnumerateFiles(SetupTools.DownloadDir).Any());
            // The size the server announces: another than the pinned one ends the download there and then. The stand-in
            // writes half the file, names its total, waits for the download to be stopped — and only when it is not, after
            // its wait, writes the whole pinned file.
            const int AnnouncedWait = 30;
            long announced = whereBytes.Length + 5_000_000;
            SetupInstall.DownloadOverride = async (url, dest, progress, token) =>
            {
                File.WriteAllBytes(dest, whereBytes[..(whereBytes.Length / 2)]);
                progress?.Report(new DownloadProgress(whereBytes.Length / 2, announced));
                await Task.Delay(TimeSpan.FromSeconds(AnnouncedWait), token);
                File.WriteAllBytes(dest, whereBytes);
            };
            var announcedClock = Stopwatch.StartNew();
            var eAnnounced = Catch(() => SetupInstall.SevenZipAsync(pinned).GetAwaiter().GetResult());
            announcedClock.Stop();
            string wrongSizeText = L.T("core.setup.downloadFailed", new
            {
                what = "7-Zip " + pinned.Version,
                error = L.T("core.setup.wrongSize", new { size = SetupInstall.Mb(announced), expected = SetupInstall.Mb(pinned.Size) }),
            });
            Check("pinned download: a server that announces another size than the pinned one is refused at once (download, naming the size it announced), its part file deleted; nothing is installed",
                eAnnounced is SetupInstallException { Code: "download" } sizeRefused && sizeRefused.Message == wrongSizeText
                && announcedClock.Elapsed < TimeSpan.FromSeconds(AnnouncedWait - 10)
                && !File.Exists(SetupTools.SevenZipExe) && !Directory.EnumerateFiles(SetupTools.DownloadDir).Any());
            // ...and one that names a wrong size right before it ends: the size is judged on the download's own flow (the
            // progress reported inline, never posted to the thread pool), so it is refused all the same, no line comes once
            // the call returned and nothing is left to run on another thread against what the download let go. Played
            // only while SetupInstall's download reports its progress that way: a Progress<T> posts each report to the
            // thread pool, whose late handler would stop a download already let go — an exception no caller can catch.
            string downloadCs = Body(install, "private static async Task DownloadAsync(");
            if (downloadCs.Length == 0 || downloadCs.Contains("new Progress<", StringComparison.Ordinal))
                Console.WriteLine("        (skipped: SetupInstall's download reports its progress through the thread pool — the cases of a size named right before the end are played once it reports inline)");
            else
            {
                var atOnceLines = new List<string>();
                bool atOnceReturned = false, atOnceStopped = false;
                int atOnceLate = 0;
                SetupInstall.DownloadOverride = (url, dest, progress, token) =>
                {
                    File.WriteAllBytes(dest, whereBytes);
                    progress?.Report(new DownloadProgress(whereBytes.Length, announced));
                    atOnceStopped = token.IsCancellationRequested;
                    return Task.CompletedTask;
                };
                var eAtOnce = Catch(() => SetupInstall.SevenZipAsync(pinned, s => { lock (atOnceLines) { atOnceLines.Add(s); if (Volatile.Read(ref atOnceReturned)) atOnceLate++; } })
                    .GetAwaiter().GetResult());
                Volatile.Write(ref atOnceReturned, true);
                Thread.Sleep(300);
                Check("pinned download: a wrong size named right before the download ends is refused all the same (download, the size it named) — judged on the download's own flow (the stop asked for before the report returns), so no line comes once the call returned and nothing runs after it on another thread; nothing is installed",
                    eAtOnce is SetupInstallException { Code: "download" } atOnceRefused && atOnceRefused.Message == wrongSizeText && atOnceStopped
                    && Volatile.Read(ref atOnceLate) == 0 && !File.Exists(SetupTools.SevenZipExe) && !Directory.EnumerateFiles(SetupTools.DownloadDir).Any());
                // The same for the lines: a report of the whole pinned size writes its line on the thread that reported it,
                // before the report returns — never later on a thread of the pool.
                var flowLines = new List<string>();
                int flowReportThread = -1, flowLineThread = -2;
                bool flowLineBeforeReturn = false;
                SetupInstall.DownloadOverride = (url, dest, progress, token) =>
                {
                    File.WriteAllBytes(dest, whereBytes);
                    flowReportThread = Environment.CurrentManagedThreadId;
                    progress?.Report(new DownloadProgress(whereBytes.Length, whereBytes.Length));
                    lock (flowLines) flowLineBeforeReturn = flowLines.Count > 0;
                    return Task.CompletedTask;
                };
                var flowGot = SetupInstall.SevenZipAsync(pinned, s => { lock (flowLines) { if (flowLines.Count == 0) flowLineThread = Environment.CurrentManagedThreadId; flowLines.Add(s); } })
                    .GetAwaiter().GetResult();
                Check("pinned download: a progress line is written on the thread that reported it, before the report returns (the progress is reported inline — no line of a download can follow its outcome)",
                    flowGot.Path == SetupTools.SevenZipExe && flowLineBeforeReturn && flowLineThread == flowReportThread
                    && flowLines.Count > 0 && flowLines[0].StartsWith(L.T("core.setup.download", new { what = "7-Zip " + pinned.Version, done = SetupInstall.Mb(whereBytes.Length), total = SetupInstall.Mb(whereBytes.Length) }), StringComparison.Ordinal));
            }
            // The pinned file itself. A part file longer than the pinned size, left by an earlier try, cannot be the start of
            // it: it goes before the download begins.
            File.WriteAllBytes(Path.Combine(SetupTools.DownloadDir, "7zr.exe.part"), new byte[whereBytes.Length + 1]);
            bool? partThere = null;
            var lines = new List<string>();
            SetupInstall.DownloadOverride = (url, dest, _, _) => { partThere = File.Exists(dest); File.WriteAllBytes(dest, whereBytes); return Task.CompletedTask; };
            var got = SetupInstall.SevenZipAsync(pinned, lines.Add).GetAwaiter().GetResult();
            Check("pinned download: the pinned file goes into the tools folder by a copy beside it and one rename, starts within 10 s, and the download is cleaned up",
                got.Path == SetupTools.SevenZipExe && File.ReadAllBytes(SetupTools.SevenZipExe).SequenceEqual(whereBytes) && !File.Exists(SetupTools.SevenZipExe + ".relic-tmp")
                && !Directory.EnumerateFiles(SetupTools.DownloadDir).Any() && lines.Contains(L.T("core.setup.sevenZip.ready", new { path = SetupTools.SevenZipExe })));
            Check("pinned download: a part file longer than the pinned size, left by an earlier try, is deleted before the download begins",
                partThere == false);
            // What holding a file open for reading alone does: it still starts, and nothing renames or deletes it meanwhile —
            // the window between Docker's installer's check and its start is closed that way.
            string heldExe = Path.Combine(tmp, "held-where.exe");
            File.WriteAllBytes(heldExe, whereBytes);
            bool started, renameRefused, deleteRefused;
            using (var hold = new FileStream(heldExe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var psi = new ProcessStartInfo(heldExe, "/?") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi)!)
                {
                    var err = p.StandardError.ReadToEndAsync();
                    p.StandardOutput.ReadToEnd();
                    started = p.WaitForExit(10_000);
                    err.Wait(2000);
                }
                renameRefused = Catch(() => File.Move(heldExe, heldExe + ".moved")) is IOException or UnauthorizedAccessException;
                deleteRefused = Catch(() => File.Delete(heldExe)) is IOException or UnauthorizedAccessException;
            }
            Check("held file: a program held open for reading alone still starts, while a rename and a delete of it are refused",
                started && renameRefused && deleteRefused && File.Exists(heldExe) && !File.Exists(heldExe + ".moved"));
            // Docker: an installer of the right size that its publisher did not sign is deleted and never run.
            byte[] fake = Encoding.ASCII.GetBytes(new string('x', 4000));
            SetupInstall.DownloadOverride = (url, dest, _, _) => { File.WriteAllBytes(dest, fake); return Task.CompletedTask; };
            var dockerEntry = new DockerEntry("https://x.example.com/Docker%20Desktop%20Installer.exe", "Docker Inc", "https://x.example.com/l", 4000, 1, 19045, 22631, "2.1.5", 1);
            var e4 = Catch(() => SetupInstall.DockerAsync(dockerEntry, acceptLicence: true).GetAwaiter().GetResult());
            Check("docker: an installer its publisher did not sign is deleted and never run (badSignature)",
                e4 is SetupInstallException { Code: "badSignature" } && !Directory.EnumerateFiles(SetupTools.DownloadDir).Any());
            var e5 = Catch(() => SetupInstall.DockerAsync(dockerEntry with { Size = 100_000 }, acceptLicence: true).GetAwaiter().GetResult());
            Check("docker: an installer far from the expected size (more than half off) is refused before its signature is even asked (download), and deleted",
                e5 is SetupInstallException { Code: "download" } && !Directory.EnumerateFiles(SetupTools.DownloadDir).Any());
            // The size the server announces for it: more than half off ends the download there and then, before any
            // signature is asked (the stand-in writes the whole installer only when it is not stopped).
            SetupInstall.DownloadOverride = async (url, dest, progress, token) =>
            {
                File.WriteAllBytes(dest, fake[..2000]);
                progress?.Report(new DownloadProgress(2000, 100_000));
                await Task.Delay(TimeSpan.FromSeconds(AnnouncedWait), token);
                File.WriteAllBytes(dest, fake);
            };
            var dockerClock = Stopwatch.StartNew();
            var e7 = Catch(() => SetupInstall.DockerAsync(dockerEntry, acceptLicence: true).GetAwaiter().GetResult());
            dockerClock.Stop();
            Check("docker: a server that announces a size more than half off is refused at once (download, naming that size), before any signature is asked, and its part file deleted",
                e7 is SetupInstallException { Code: "download" } dockerRefused
                && dockerRefused.Message == L.T("core.setup.downloadFailed", new
                {
                    what = "Docker Desktop",
                    error = L.T("core.setup.wrongSize", new { size = SetupInstall.Mb(100_000), expected = SetupInstall.Mb(dockerEntry.Size) }),
                })
                && dockerClock.Elapsed < TimeSpan.FromSeconds(AnnouncedWait - 10) && !Directory.EnumerateFiles(SetupTools.DownloadDir).Any());
        }
        finally
        {
            SetupInstall.DownloadOverride = null;
            SetupTools.ToolsDirOverride = null;
        }
        var e6 = Catch(() => SetupInstall.WslMemory(0));
        Check("wslconfig: a memory limit of 0 GB is refused before the file is touched", e6 is SetupInstallException { Code: "failed" });
    }

    static Exception? Catch(Action act)
    {
        try { act(); return null; }
        catch (Exception ex) { return ex; }
    }

    /// <summary>The last line of relic.log's tail that holds <paramref name="marker"/>, or null (none, or the log cannot
    /// be read).</summary>
    static string? LastLogLine(string marker)
    {
        try
        {
            using var fs = new FileStream(Log.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Position = Math.Max(0, fs.Length - 64 * 1024);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd().Split('\n').LastOrDefault(l => l.Contains(marker, StringComparison.Ordinal));
        }
        catch (Exception) { return null; }
    }

    // ── the texts ──
    static void Texts(string repo, Action<string, bool> Check)
    {
        string lang = Path.Combine(repo, "app", "Relic.App", "ui", "lang");
        string fragments = Path.Combine(lang, "fragments");
        var en = Flat(Path.Combine(lang, "en.json"));
        var known = new HashSet<string>(en.Keys, StringComparer.Ordinal);
        foreach (var file in Directory.Exists(fragments) ? Directory.GetFiles(fragments, "*.en.json") : Array.Empty<string>())
            foreach (var p in FlatList(file)) known.Add(p.Key);
        // This round's files: the plan file of the Setup folder is another round's.
        var sources = Directory.GetFiles(Path.Combine(repo, "app", "Relic.Core", "Setup"), "*.cs")
            .Where(f => !Path.GetFileName(f).Equals("QuickStartPlan.cs", StringComparison.OrdinalIgnoreCase))
            .Concat(new[] { "LocalAgent.cs", "AgentDeployer.cs", "AgentInstallPlan.cs" }.Select(f => Path.Combine(repo, "app", "Relic.Core", "Server", f)));
        var used = sources.SelectMany(f => Regex.Matches(File.ReadAllText(f), "L\\.T\\(\"([a-zA-Z0-9_.]+)\"").Select(m => m.Groups[1].Value)).Distinct().ToList();
        var missing = used.Where(k => !known.Contains(k)).ToList();
        Check($"texts: every key Relic.Core/Setup, LocalAgent, AgentDeployer and AgentInstallPlan use is in en.json or in a fragment ({used.Count} keys)"
            + (missing.Count > 0 ? $" — missing: {string.Join(", ", missing.Take(6))}" : ""), missing.Count == 0);
        // The contract's texts: in en.json once the round's fragment is merged, in that fragment until then — a fragment
        // still waiting for its merge is sorted and gives no key of en.json another text.
        var frag = FlatList(Path.Combine(fragments, "m13core.en.json"));
        Check("texts: the core.setup.* texts and the three new core.deploy ones of the contract are in en.json or in the round's fragment — one still waiting for its merge sorted, and giving no key en.json has another text",
            known.Contains("core.deploy.hostKeyChanged") && known.Contains("core.deploy.hostKeyRead") && known.Contains("core.deploy.probe.publicIp")
            && known.Count(k => k.StartsWith("core.setup.", StringComparison.Ordinal)) >= 31
            && frag.Select(p => p.Key).SequenceEqual(frag.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal))
            && frag.All(p => !en.TryGetValue(p.Key, out var text) || text == p.Value));
    }

    /// <summary>A flat language file's keys and texts, in its order; none when the file is not there.</summary>
    static List<KeyValuePair<string, string>> FlatList(string file)
    {
        if (!File.Exists(file)) return new();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        return doc.RootElement.EnumerateObject()
            .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText()))
            .ToList();
    }

    /// <summary>A flat language file as key → text (the first of a key written twice).</summary>
    static Dictionary<string, string> Flat(string file) =>
        FlatList(file).GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

    /// <summary>A method's body in <paramref name="cs"/>: from its signature to the brace that closes it at member
    /// indentation; "" when the signature is not there.</summary>
    static string Body(string cs, string signature)
    {
        int at = cs.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return "";
        var end = new Regex(@"\n    \}\)?;?\n").Match(cs, at);
        return end.Success ? cs[at..end.Index] : "";
    }

    static string? FindRepo()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Relic.slnx"))) return dir.FullName;
        }
        return null;
    }
}
