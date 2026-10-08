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
/// <c>[A-Za-z_][A-Za-z0-9_]*</c>, and the FIRST occurrence of a repeated key wins. Two parsers that
/// disagree would have the launcher dial an agent on a port it never bound.
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
    public string Dir16 => Get("GIO_DIR_16");
    public string Dir28 => Get("GIO_DIR_28");
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
    /// <c>OUTER_IP</c> its own <c>.env</c> holds, so that value is read from <c>&lt;Dir16&gt;\.env</c> and
    /// <c>&lt;Dir28&gt;\.env</c> (<see cref="LocalAgent.StackOuterIp"/>) and taken when
    /// <see cref="AgreedOuterIp"/> accepts it; when it does not — no <c>.env</c> yet, stacks that
    /// disagree, an address that is not this PC's — the stored <see cref="BindIp"/> stands in. With the
    /// option on, every read of this property reads the two files again and, for an address that is not
    /// loopback, binds one UDP socket.</summary>
    public string StackBindIp => KeepEnv
        ? AgreedOuterIp(new[] { LocalAgent.StackOuterIp(Dir16), LocalAgent.StackOuterIp(Dir28) }, LocalAgent.CanBind) ?? BindIp
        : BindIp;

    /// <summary>The one address the <c>OUTER_IP</c> values of the stacks stand for, or null when they give
    /// none to go by. A blank value is a stack that names none (no <c>.env</c> yet) and has no say.
    /// "localhost" and every 127.* value count as 127.0.0.1 — the agent's <c>_is_loopback</c>. The stacks
    /// that name one must agree: the launcher keeps ONE host for both versions. An address that is not
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
/// the files it could not delete (in use — a hotpatch pack a client is downloading, an open log).</summary>
public sealed record UninstallReport(bool Stopped, bool AutostartRemoved, bool FirewallRemoved, string? FirewallError,
    bool FilesRemoved, IReadOnlyList<string> Leftovers);

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

    /// <summary>Find a usable CPython ≥ 3.10: the <c>py</c> launcher first, then <c>python</c> on PATH.
    /// Returns null when none qualifies (the UI then shows install guidance).</summary>
    public static PythonInfo? FindPython()
    {
        foreach (var (exe, args) in new[] { ("py", "-3 -c"), ("python", "-c"), ("python3", "-c") })
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    Arguments = args + " \"import sys;print(sys.executable);print('%d.%d'%sys.version_info[:2])\"",
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) continue;
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } continue; }
                if (p.ExitCode != 0) continue;
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length < 2) continue;
                string path = lines[0], ver = lines[1];
                var parts = ver.Split('.');
                if (parts.Length < 2 || !int.TryParse(parts[0], out int maj) || !int.TryParse(parts[1], out int min)) continue;
                if (maj < 3 || (maj == 3 && min < 10)) continue;
                if (!File.Exists(path)) continue;
                // WindowsApps stubs (the Store alias) resolve through an app-execution alias; that is fine.
                string exeW = Path.Combine(Path.GetDirectoryName(path)!, "pythonw.exe");
                if (!File.Exists(exeW)) exeW = path;
                return new PythonInfo(path, exeW, ver);
            }
            catch { /* next candidate */ }
        }
        return null;
    }

    /// <summary>A 7z extractor the agent on this PC can use for the server-package download (the same
    /// candidates as its <c>find_extractor()</c>): 7-Zip's 7z.exe in its usual homes or on PATH,
    /// Bandizip's bz.exe, else Windows' own <c>System32\tar.exe</c> — bsdtar, accepted only when it was
    /// built with liblzma (it reads LZMA2 .7z then), and always by FULL path: Git for Windows puts a GNU
    /// tar on PATH that shadows it and knows no 7z at all. Null = nothing usable.</summary>
    public static string? FindExtractor()
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
            if (File.Exists(tar))
            {
                var res = Proc.Run(tar, "--version");
                if (res.Ok && (res.Out + res.Err).Contains("liblzma", StringComparison.OrdinalIgnoreCase)) return tar;
            }
        }
        catch (Exception ex) { Log.Info($"tar.exe probe: {ex.Message}"); }
        return null;
    }

    /// <summary>Copy the shipped agent + payloads, write the config (merged with the previous one, see
    /// <see cref="MergeConfig(string, string?)"/>), run <c>--selftest</c>. Does not start the agent. Throws
    /// with a user-facing message on failure.</summary>
    public static IReadOnlyList<string> Install(AgentInstallPlan plan, PythonInfo python, Action<string>? log = null, string? agentDir = null)
    {
        if (!plan.IsWindows) throw new InvalidOperationException("LocalAgent.Install: plan target is not windows");
        var errs = plan.Validate();
        if (errs.Count > 0) throw new InvalidOperationException(string.Join("\n", errs));
        // A version marked for download is skipped by the stack check — but the agent will have to
        // EXTRACT what it downloads, and on Windows nothing guarantees a 7z tool: refuse now, with the
        // fix in the message, rather than let a 2 GB download fail at its very last step.
        var missing = plan.MissingStackEntries(p => Directory.Exists(p) || File.Exists(p));
        if (missing.Count > 0) throw new InvalidOperationException(L.T("core.deploy.stackIncomplete", new { list = string.Join(", ", missing) }));
        if (plan.FetchVersions().Count > 0 && FindExtractor() is null)
            throw new InvalidOperationException(L.T("core.deploy.noExtractorWindows"));

        string src = agentDir ?? ShippedAgentDir;
        string srcScript = Path.Combine(src, "gio_agent.py");
        if (!File.Exists(srcScript)) throw new InvalidOperationException(L.T("core.deploy.agentMissingInBuild", new { path = srcScript }));
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
        // Linux. An unreadable file keeps nothing (it is replaced either way); values are never logged,
        // only the key names.
        string? previous = null;
        try { if (File.Exists(ConfigPath)) previous = File.ReadAllText(ConfigPath); }
        catch (Exception ex) { Log.Error("reading the previous local agent config (non-fatal: nothing is kept)", ex); }
        string config = MergeConfig(plan.RenderConfig(), previous, out var kept);
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
        // 300 s: the selftest takes ~25 s on a fast Windows PC (real file I/O an antivirus scans); slow laptops need the margin.
        var (code, output) = Run(python.Exe, $"\"{ScriptPath}\" --selftest", Root, timeoutMs: 300_000);
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
            var (code, output) = Run(python.Exe, $"\"{stagedScript}\" --selftest", StagePath, timeoutMs: 300_000);
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

    /// <summary>The config file on this PC, parsed like the agent parses it; null when there is none (or it
    /// cannot be read — logged, never thrown: the start screen calls this on every init).</summary>
    public static LocalAgentConfig? ReadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            // ReadAllText already drops a UTF-8 BOM; the explicit trim covers a file written without one
            // being read through a different detection path.
            foreach (var raw in File.ReadAllText(ConfigPath).TrimStart('\uFEFF').Split('\n'))
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
        catch (Exception ex)
        {
            Log.Error("reading the local agent config (non-fatal)", ex);
            return null;
        }
    }

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
    /// stale (an autostarted agent never has one), the python process LISTENING on <paramref name="port"/>.
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
    /// python process listening on <paramref name="port"/> (an agent started by the HKCU Run value writes
    /// no pid file and would otherwise read as "installed, stopped" — a second start then dies on the bound
    /// port). Same "is it python" test as <see cref="RunningPid"/>.</summary>
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

    /// <summary>The first of the two elevations in Relic, admin-mode only and user-clicked: an inbound
    /// allow rule for the agent's TCP port scoped to the python executable. Returns null on success,
    /// else the error text (a declined UAC prompt is a normal outcome).</summary>
    public static string? OpenFirewallPort(int port)
    {
        try
        {
            var python = ReadPython() ?? FindPython();
            string program = python?.ExeW ?? "";
            string rule = $"advfirewall firewall add rule name=\"{FirewallRuleName(port)}\" dir=in action=allow protocol=TCP localport={port}" +
                          (program.Length > 0 ? $" program=\"{program}\"" : "");
            var psi = new ProcessStartInfo("netsh.exe", rule) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            if (p is null) return "netsh did not start";
            if (!p.WaitForExit(60_000)) return "netsh timed out";
            return p.ExitCode == 0 ? null : $"netsh exit code {p.ExitCode}";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return L.T("core.deploy.uacDeclined");
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Is our inbound rule for <paramref name="port"/> present? Read unelevated — showing rules
    /// needs no rights; netsh answers exit 1 ("No rules match") when there is none.</summary>
    public static bool FirewallRuleExists(int port)
    {
        try
        {
            var res = Proc.Run("netsh", "advfirewall", "firewall", "show", "rule", "name=" + FirewallRuleName(port));
            return res.Ok && !res.Out.Contains("No rules match", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Log.Info($"netsh show rule: {ex.Message}"); return false; }
    }

    /// <summary>The second elevation, the mirror of <see cref="OpenFirewallPort"/>: delete the rule that
    /// call added. User-clicked (the uninstall dialog's checkbox, off by default), never run from the
    /// launcher's own --uninstall-cleanup. Null on success, else the error text (UAC declined =
    /// <c>core.deploy.uacDeclined</c>).</summary>
    public static string? RemoveFirewallRule(int port)
    {
        try
        {
            string rule = $"advfirewall firewall delete rule name=\"{FirewallRuleName(port)}\"";
            var psi = new ProcessStartInfo("netsh.exe", rule) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi);
            if (p is null) return "netsh did not start";
            if (!p.WaitForExit(60_000)) return "netsh timed out";
            return p.ExitCode == 0 ? null : $"netsh exit code {p.ExitCode}";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Its own text: the "was not added" of OpenFirewallPort would tell the admin the opposite
            // of what happened and hide that the inbound rule outlives the agent.
            return L.T("core.deploy.uacDeclinedRemove");
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// Remove the agent from this PC in the order <c>uninstall_agent.sh</c> uses on Linux: the process
    /// (pid file or listener), the autostart value — only when it points at THIS root, so a spike root
    /// never touches the real one — the firewall rule when asked (the one elevated step), then every
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
        if (removeFirewall && FirewallRuleExists(port))
        {
            firewallError = RemoveFirewallRule(port);
            firewallRemoved = firewallError is null;
            if (firewallRemoved) log?.Invoke(L.T("core.deploy.firewallRemoved"));
        }

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
        return new UninstallReport(stopped, autostartRemoved, firewallRemoved, firewallError, filesRemoved, leftovers);
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
