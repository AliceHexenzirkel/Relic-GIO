using System.Text;
using Relic.Core.Util;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Relic.Core.Server;

/// <summary>SSH endpoint + credentials for a fresh-server install. Password and/or private key
/// (OpenSSH / PEM / PuTTY .ppk, optional passphrase). A non-root user needs sudo; the sudo password
/// defaults to the SSH password when blank.</summary>
public sealed record SshTarget(string Host, int Port, string User, string Password, string KeyPath, string KeyPassphrase, string SudoPassword)
{
    public bool IsRoot => string.Equals(User, "root", StringComparison.Ordinal);
    public string EffectiveSudoPassword => string.IsNullOrEmpty(SudoPassword) ? Password : SudoPassword;
}

/// <summary>Outcome of an install. <paramref name="Warning"/> is set when the installer itself succeeded
/// but the agent did not answer /health in time: the box IS configured and running the new token, so this
/// must never be raised as a failure — the caller still has to save the connection, or the admin is left
/// with an agent whose token nobody has ever seen.</summary>
public sealed record DeployResult(string HostKeyFingerprint, string Health, string Warning = "",
    IReadOnlyList<string>? Fetch = null)
{
    /// <summary>The versions the agent really has to download afterwards — the plan's marks minus the
    /// ones whose folder already held a stack (install_agent.sh skips those silently).</summary>
    public IReadOnlyList<string> FetchVersions => Fetch ?? Array.Empty<string>();
}

/// <summary>
/// Installs/upgrades the GIO agent on a Linux box over SSH by running the SAME installer an admin
/// would run by hand (<c>install_agent.sh --yes</c> with the plan as environment) — one path, so the
/// launcher-driven install cannot drift from the documented one. Distro-neutral (Alma/RHEL uses
/// firewalld, Ubuntu uses ufw or none); idempotent — a re-run is the upgrade. The installer's own
/// upgrade merge (the stored config as the run's defaults) is the one merge that exists.
/// </summary>
public static class AgentDeployer
{
    /// <summary>
    /// Install the agent on a Linux box by running the SAME installer an admin would run by hand:
    /// upload agent/ (script, installers, unit, payloads) to ~/.relic-agent-upload, then
    /// <c>install_agent.sh --yes</c> with the plan as environment — as root directly, via <c>sudo -n</c>
    /// when passwordless, else <c>sudo -S -k -p ''</c> with the sudo password as the ONLY stdin line (the
    /// script runs by path, never <c>bash -s</c>, so a non-prompting sudo can never execute the password
    /// as a command). The script selftests the uploaded agent, writes /etc/gio-agent/config, opens
    /// firewalld/ufw and enables the systemd unit. Verifies /health at the end and removes the upload.
    /// </summary>
    public static DeployResult Install(SshTarget ssh, AgentInstallPlan plan, string agentDir, Action<string>? log = null)
    {
        var errs = plan.Validate();
        if (errs.Count > 0) throw new InvalidOperationException(string.Join("\n", errs));
        RequireAgentFiles(agentDir);
        using var session = Open(ssh, log);
        var client = session.Client;
        var sftp = session.Sftp;
        string up = session.Up;

        // Stack folders must already be there — unless the version is marked for download: then the
        // folder is expected to be absent and the AGENT fills it. Nothing is started here:
        // under GIO_RELIC_ENV=1 install_agent.sh leaves the download to the launcher, which runs the
        // fetch jobs from the Server page after deploy.done (they take 10-45 minutes on archive.org).
        var missing = plan.MissingStackEntries(p => RunOut(client, $"test -e {Q(p)} && echo yes || echo no").Trim() == "yes");
        if (missing.Count > 0) throw new InvalidOperationException(L.T("core.deploy.stackIncomplete", new { list = string.Join(", ", missing) }));
        // The same probe install_agent.sh runs: a folder that already holds a stack is NOT downloaded,
        // so the launcher must not queue it either (the agent would answer 409 "already on this server"
        // and the queue behind it would be dropped).
        var willFetch = plan.FetchVersionsReally(p => RunOut(client, $"test -e {Q(p)} && echo yes || echo no").Trim() == "yes");
        foreach (var v in plan.FetchVersions())
            log?.Invoke(willFetch.Contains(v)
                ? L.T("core.deploy.willFetch", new { version = v, dir = plan.DirFor(v) })
                : L.T("core.deploy.fetchSkipped", new { version = v, dir = plan.DirFor(v) }));
        // Neither is a stop: install_agent.sh installs python3 (>= 3.9) and docker + compose v2
        // itself when they are missing -- the lines only tell the admin what the installer is about to do.
        if (RunOut(client, "command -v python3 >/dev/null 2>&1 && echo yes || echo no").Trim() != "yes")
            log?.Invoke(L.T("core.deploy.noPython3OnBox"));
        if (RunOut(client, "docker compose version >/dev/null 2>&1 && echo yes || echo no").Trim() != "yes")
            log?.Invoke(L.T("core.deploy.noDockerWarn"));

        log?.Invoke(L.T("core.deploy.uploading", new { dir = up }));
        ResetStage(client, up);
        try
        {
            UploadAgent(session, agentDir);

            // The plan goes over as a 0600 file that the installer SOURCES — never as `env KEY=...` on the
            // remote command line. There it would be readable in /proc/<pid>/cmdline by every local user and
            // written verbatim and permanently into sudo's auth.log, and it would also leak through any SSH.NET
            // exception that quotes CommandText. The token IS the security boundary.
            // GIO_AGENT_START=y: the admin clicked Install — a running agent is the expected outcome even
            // when a previous one on the box had been stopped (an upgrade run keeps that state otherwise).
            string envFile = up + "/install.env";
            var envText = new StringBuilder();
            foreach (var kv in plan.ToEnvironment()) envText.Append(kv.Key).Append('=').Append(Q(kv.Value)).Append('\n');
            envText.Append("GIO_AGENT_START=y\n");
            // Sentinel, checked by the runner below. A failed `.` (source) is NOT fatal in a plain
            // `bash -c`, so without this a file root cannot read — or a truncated upload — would let
            // the installer run with NO plan at all: it mints its OWN random token, guesses the stack
            // paths and exits 0, and we would then save and show a token the box has never seen.
            envText.Append("GIO_RELIC_ENV=1\n");
            // Installer-only, never written to /etc/gio-agent/config. Only relaxes the INCONCLUSIVE branch
            // of the pre-upgrade probe — a genuinely busy agent still refuses to be upgraded.
            if (plan.UpgradeForce) envText.Append("GIO_UPGRADE_FORCE=y\n");
            UploadText(sftp, envText.ToString(), envFile);
            RunOut(client, $"chmod 600 {Q(envFile)}");

            // set -a exports everything the file assigns, so the installer sees exactly the environment a
            // command line would hand it — empty values included, which it treats as "not set".
            string runner = "set -a; . ./install.env || exit 97; set +a; "
                + "[ \"$GIO_RELIC_ENV\" = 1 ] || exit 97; exec bash ./install_agent.sh --yes";
            var (cmdText, stdin) = RootCommand(client, ssh, up, runner);
            log?.Invoke(L.T("core.deploy.runningInstaller"));
            // Up to four secrets ride in install.env: the token and — when chosen — the MUIP sign key, the
            // MySQL root password and the Flask secret key. All are blanked wherever the
            // installer's output could echo them — a bash diagnostic quoting the failing assignment, a sudo
            // refusal, the failure tail that goes into the error modal.
            string[] secrets = SecretsOf(plan);
            var (code, output) = RunStreaming(client, cmdText, stdin, secrets, log);
            ThrowOnFailure(code, output, secrets);

            // Dial where the agent actually bound, not a hardcoded 127.0.0.1 (a specific bind answers only on
            // itself), and POLL: the installer restarts the unit and sleeps 1 s, which a loaded box can easily
            // outrun. A connection refusal comes back instantly, so a single shot would turn a finished
            // install into a hard failure — and with it the only copy of the generated token.
            string url = $"http://{plan.ProbeAuthority}/health";
            string probe = $"curl -fsS -m 5 {Q(url)} 2>/dev/null || python3 -c \"import urllib.request;print(urllib.request.urlopen('{url}',timeout=5).read().decode())\" 2>/dev/null";
            string health = "";
            for (int attempt = 0; attempt < 12; attempt++)
            {
                health = RunOut(client, probe, TimeSpan.FromSeconds(30));
                if (health.Contains("gio-agent")) break;
                if (attempt == 0) log?.Invoke(L.T("core.deploy.waitingHealth", new { url }));
                Thread.Sleep(2000);
            }
            // A silent agent is reported as a WARNING, never a failure: install_agent.sh has already written
            // /etc/gio-agent/config and restarted the unit, so throwing here would discard the only copy of a
            // token the box is already using.
            string warning = health.Contains("gio-agent") ? "" : L.T("core.deploy.noHealthRemote", new { port = plan.ProbeAuthority });
            log?.Invoke(warning.Length > 0 ? warning : L.T("core.deploy.installedOk"));
            return new DeployResult(session.Fingerprint, health.Trim(), warning, willFetch);
        }
        finally
        {
            // On EVERY exit — the staging dir holds install.env, i.e. the token.
            try { RunOut(client, $"rm -rf {Q(up)}", TimeSpan.FromSeconds(30)); } catch { }
        }
    }

    /// <summary>The runner's exit code for "this box has no agent to update".</summary>
    private const int NoAgentExit = 96;

    /// <summary>
    /// Update the agent a Linux box already has: the same upload and the same installer as
    /// <see cref="Install"/>, run as a plain upgrade. install.env carries no configuration key at all, so
    /// every value of the box's own /etc/gio-agent/config is the run's value — the token, the folders,
    /// the listen address, the secrets, "Keep .env settings" — and the service keeps its enabled /
    /// stopped state; <c>--no-deps</c>: an update installs no package. Refused before anything is
    /// replaced when the box has no agent (the runner's exit <see cref="NoAgentExit"/>): without a config
    /// the installer would set up a new one, with a token nobody ever sees. The installer itself refuses
    /// while the agent has a job in progress; <paramref name="force"/> lets it go on only when it could
    /// not tell. Nothing is asked of the agent afterwards: the caller holds the connection to it.
    /// </summary>
    public static DeployResult Upgrade(SshTarget ssh, string agentDir, bool force = false, Action<string>? log = null)
    {
        RequireAgentFiles(agentDir);
        using var session = Open(ssh, log);
        var client = session.Client;
        string up = session.Up;
        log?.Invoke(L.T("core.deploy.uploading", new { dir = up }));
        ResetStage(client, up);
        try
        {
            UploadAgent(session, agentDir);
            string envFile = up + "/install.env";
            // The sentinel alone (see Install), and the one installer-only switch an update can carry.
            UploadText(session.Sftp, "GIO_RELIC_ENV=1\n" + (force ? "GIO_UPGRADE_FORCE=y\n" : ""), envFile);
            RunOut(client, $"chmod 600 {Q(envFile)}");
            string runner = $"[ -f /etc/gio-agent/config ] && [ -f /opt/gio-agent/gio_agent.py ] || exit {NoAgentExit}; "
                + "set -a; . ./install.env || exit 97; set +a; "
                + "[ \"$GIO_RELIC_ENV\" = 1 ] || exit 97; exec bash ./install_agent.sh --yes --no-deps";
            var (cmdText, stdin) = RootCommand(client, ssh, up, runner);
            log?.Invoke(L.T("core.deploy.runningInstaller"));
            // No secret travels with an update. The installer's own lines that could show one the box
            // stores — the token it prints at the end, an assignment of a secret key — reach neither the
            // log nor the failure tail (LeaksSecret).
            string[] secrets = Array.Empty<string>();
            var (code, output) = RunStreaming(client, cmdText, stdin, secrets, log);
            if (code == NoAgentExit) throw new InvalidOperationException(L.T("core.deploy.updateNoAgent", new { host = ssh.Host }));
            ThrowOnFailure(code, output, secrets);
            return new DeployResult(session.Fingerprint, "");
        }
        finally
        {
            try { RunOut(client, $"rm -rf {Q(up)}", TimeSpan.FromSeconds(30)); } catch { }
        }
    }

    /// <summary>The SSH and SFTP connections of one run, and the staging folder on the box.</summary>
    private sealed class Session : IDisposable
    {
        public required SshClient Client { get; init; }
        public required SftpClient Sftp { get; init; }
        public string Fingerprint { get; set; } = "";
        public string Up { get; set; } = "";
        public void Dispose()
        {
            Sftp.Dispose();
            Client.Dispose();
        }
    }

    private static void RequireAgentFiles(string agentDir)
    {
        if (!File.Exists(Path.Combine(agentDir, "gio_agent.py")) || !File.Exists(Path.Combine(agentDir, "install_agent.sh")))
            throw new InvalidOperationException(L.T("core.deploy.agentMissingInBuild", new { path = agentDir }));
    }

    /// <summary>Connect with the password and / or the private key of <paramref name="ssh"/>. The host key
    /// is trusted at this first contact and its fingerprint kept for the caller to record.</summary>
    private static Session Open(SshTarget ssh, Action<string>? log)
    {
        var auth = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(ssh.KeyPath))
        {
            if (!File.Exists(ssh.KeyPath)) throw new InvalidOperationException(L.T("core.deploy.keyMissing", new { path = ssh.KeyPath }));
            var key = string.IsNullOrEmpty(ssh.KeyPassphrase) ? new PrivateKeyFile(ssh.KeyPath) : new PrivateKeyFile(ssh.KeyPath, ssh.KeyPassphrase);
            auth.Add(new PrivateKeyAuthenticationMethod(ssh.User, key));
        }
        if (!string.IsNullOrEmpty(ssh.Password)) auth.Add(new PasswordAuthenticationMethod(ssh.User, ssh.Password));
        if (auth.Count == 0) throw new InvalidOperationException(L.T("core.deploy.noCredentials"));
        var ci = new ConnectionInfo(ssh.Host, ssh.Port, ssh.User, auth.ToArray()) { Timeout = TimeSpan.FromSeconds(20) };

        var session = new Session { Client = new SshClient(ci), Sftp = new SftpClient(ci) };
        try
        {
            session.Client.HostKeyReceived += (_, e) =>
            {
                session.Fingerprint = "SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(e.HostKey)).TrimEnd('=');
                log?.Invoke(L.T("core.deploy.hostKey", new { fingerprint = session.Fingerprint }));
                e.CanTrust = true; // first contact — the fingerprint is returned for the caller to record
            };
            log?.Invoke(L.T("core.deploy.connecting", new { host = ssh.Host, port = ssh.Port, user = ssh.User }));
            session.Client.Connect();
            session.Sftp.Connect();
            string home = RunOut(session.Client, "echo ~").Trim();
            if (home.Length == 0) home = ssh.IsRoot ? "/root" : "/home/" + ssh.User;
            session.Up = home + "/.relic-agent-upload";
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>An empty staging folder, 0700 BEFORE anything lands in it: an install puts the bearer
    /// token there.</summary>
    private static void ResetStage(SshClient client, string up) =>
        RunOut(client, $"rm -rf {Q(up)} && mkdir -p {Q(up)} && chmod 700 {Q(up)}");

    /// <summary>The agent, its installers and unit, and the payloads into the staging folder.</summary>
    private static void UploadAgent(Session session, string agentDir)
    {
        string up = session.Up;
        foreach (var f in new[] { "gio_agent.py", "install_agent.sh", "uninstall_agent.sh", "gio-agent.service" })
        {
            string src = Path.Combine(agentDir, f);
            if (File.Exists(src)) UploadFile(session.Sftp, src, up + "/" + f);
        }
        // Only create payloads/ when there is something to put in it. install_agent.sh replaces the
        // box's payloads whenever the directory merely EXISTS, so an empty one would delete them.
        string payloads = Path.Combine(agentDir, "payloads");
        if (Directory.Exists(payloads) && Directory.EnumerateFiles(payloads, "*", SearchOption.AllDirectories).Any())
            UploadTree(session.Sftp, session.Client, payloads, up + "/payloads");
        // CRLF-proof the shell scripts (a repo checkout on Windows may carry CRLF).
        RunOut(session.Client, $"sed -i 's/\\r$//' {Q(up)}/install_agent.sh {Q(up)}/uninstall_agent.sh 2>/dev/null; chmod 755 {Q(up)}/install_agent.sh {Q(up)}/uninstall_agent.sh 2>/dev/null; true");
    }

    /// <summary>The command that runs <paramref name="runner"/> as root in the staging folder: directly
    /// for root, through <c>sudo -n</c> when it asks for no password, else <c>sudo -S -k -p ''</c> with
    /// the sudo password as the only stdin line.</summary>
    private static (string CmdText, string? Stdin) RootCommand(SshClient client, SshTarget ssh, string up, string runner)
    {
        if (ssh.IsRoot) return ($"cd {Q(up)} && bash -c {Q(runner)}", null);
        if (Exit(client, "sudo -n true") == 0) return ($"cd {Q(up)} && sudo -n bash -c {Q(runner)}", null);
        if (string.IsNullOrEmpty(ssh.EffectiveSudoPassword)) throw new InvalidOperationException(L.T("core.deploy.sudoPasswordNeeded"));
        return ($"cd {Q(up)} && sudo -S -k -p '' bash -c {Q(runner)}", ssh.EffectiveSudoPassword + "\n");
    }

    /// <summary>The installer run as its outcome: a sudo that wants a terminal or refuses the password, an
    /// install.env that was not applied (97), any other exit code with the redacted end of the output.</summary>
    private static void ThrowOnFailure(int code, string output, string[] secrets)
    {
        if (output.Contains("must have a tty", StringComparison.OrdinalIgnoreCase) || output.Contains("requiretty", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("core.deploy.requireTty"));
        if (output.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) || output.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("core.deploy.sudoRefused"));
        if (code == 97) throw new InvalidOperationException(L.T("core.deploy.envNotApplied"));
        if (code != 0) throw new InvalidOperationException(L.T("core.deploy.installerFailed", new { code, tail = Redact(Tail(output, 600), secrets) }));
    }

    private static string Q(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

    /// <summary>Every short probe gets a deadline. SSH.NET's default is infinite, so a wedged dockerd or a
    /// stalled NFS stack path would hang the install for good: the job flag stays held (every later server
    /// operation answers "busy") and the overlay's Close button is disabled while it runs.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    private static string RunOut(SshClient c, string text, TimeSpan? timeout = null)
    {
        using var cmd = c.CreateCommand(text);
        cmd.CommandTimeout = timeout ?? ProbeTimeout;
        try { return cmd.Execute(); }
        catch (SshOperationTimeoutException) { return ""; }
    }

    private static int Exit(SshClient c, string text, TimeSpan? timeout = null)
    {
        using var cmd = c.CreateCommand(text);
        cmd.CommandTimeout = timeout ?? ProbeTimeout;
        try { cmd.Execute(); } catch (SshOperationTimeoutException) { return -1; }
        return cmd.ExitStatus ?? -1;
    }

    /// <summary>The secrets of a plan as install.env carries them — the token and, when chosen, the MUIP
    /// sign key, the MySQL root password and the Flask secret key — trimmed, blank ones left out, LONGEST
    /// FIRST: <see cref="Redact"/> replaces in this order, and a shorter secret that begins a longer one
    /// (a sign key "abcdefgh", a root password "abcdefgh1234"), blanked first, would leave the longer
    /// one's tail readable ("***1234"). Read off <see cref="AgentInstallPlan.ToEnvironment"/>, so the list
    /// is exactly what travels: with "Keep .env settings" on the plan sends none of the three, and a value
    /// left in such a field is not validated either — listed, a short one would have
    /// <see cref="LeaksSecret"/> hide every installer line that merely contains it. Pure.</summary>
    public static string[] SecretsOf(AgentInstallPlan plan)
    {
        var sent = plan.ToEnvironment();
        return SecretKeys.Select(k => sent.TryGetValue(k, out var v) ? v.Trim() : "").Where(s => s.Length > 0)
            .OrderByDescending(s => s.Length).ToArray();
    }

    /// <summary>The config keys whose value is a secret. The launcher knows the values the admin typed,
    /// never the ones a box already STORES behind a field left blank (install_agent.sh exports those into
    /// its own environment): such a value can only be recognised by the assignment it stands in.</summary>
    private static readonly string[] SecretKeys = { "GIO_AGENT_TOKEN", "GIO_MUIP_KEY", "GIO_MYSQL_ROOT_PASSWORD", "GIO_FLASK_SECRET_KEY" };

    /// <summary><c>KEY=value</c> for one of the <see cref="SecretKeys"/>, the value running to the end of the line.</summary>
    private static readonly System.Text.RegularExpressions.Regex SecretAssignment =
        new("(" + string.Join("|", SecretKeys) + ")=[^\r\n]*");

    /// <summary>Blank out every secret (the token, the MUIP sign key, the MySQL root password, the Flask
    /// secret key) wherever it appears. Applied to everything that can reach the admin's screen or
    /// relic.log — a sudo refusal or a bash error quoting the failing expansion matches none of the known
    /// "Agent token"/"GIO_AGENT_TOKEN=" line shapes. A secret shorter than 8 characters is never replaced:
    /// it would blank ordinary words. What follows a secret key's assignment (<c>GIO_MUIP_KEY=…</c>) is
    /// blanked to the end of its line whatever it is: this text feeds installerFailed's tail, and a value
    /// the box stores is not among <paramref name="secrets"/>. Pure.</summary>
    public static string Redact(string text, params string[] secrets)
    {
        string s = text ?? "";
        foreach (var secret in secrets)
            if (secret is { Length: >= 8 }) s = s.Replace(secret, "***", StringComparison.Ordinal);
        return SecretAssignment.Replace(s, "$1=***");
    }

    /// <summary>Must this line of installer output stay out of the live log? Yes when it carries a secret's
    /// value, one of the <see cref="SecretKeys"/> as an assignment (whatever the value — see there), or is
    /// one of the installer's two lines that print the token. Pure.</summary>
    public static bool LeaksSecret(string line, params string[] secrets) =>
        secrets.Any(s => s.Length > 0 && line.Contains(s, StringComparison.Ordinal))
        || SecretKeys.Any(k => line.Contains(k + "=", StringComparison.Ordinal))
        || line.TrimStart().StartsWith("Agent token", StringComparison.Ordinal)
        || line.Contains("generated token:", StringComparison.Ordinal);

    /// <summary>Run a long command, streaming its stdout lines to <paramref name="log"/> (a line that
    /// <see cref="LeaksSecret"/> is never surfaced), optionally feeding <paramref name="stdin"/> once.</summary>
    private static (int Code, string Output) RunStreaming(SshClient c, string text, string? stdin, string[] secrets, Action<string>? log)
    {
        using var cmd = c.CreateCommand(text);
        // A fresh box runs apt-get update + the whole docker install inside this one command, which 10
        // minutes on a slow link does not cover — and a timeout here kills the install half-done.
        cmd.CommandTimeout = TimeSpan.FromMinutes(45);
        // BeginExecute FIRST, then CreateInputStream. SSH.NET only assigns the channel inside the execute
        // call, and CreateInputStream dereferences it — called first it throws before the installer ever
        // runs, which is the whole password-sudo path (a non-root admin). Documented in that order by
        // SSH.NET itself. The try/catch covers a command that finished before we got here.
        var async = cmd.BeginExecute();
        Stream? input = null;
        if (stdin is not null)
        {
            try { input = cmd.CreateInputStream(); }
            catch (InvalidOperationException) { input = null; }
        }
        if (input is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(stdin!);
            input.Write(bytes, 0, bytes.Length);
            input.Flush();
            input.Dispose();
        }
        var sb = new StringBuilder();
        using (var reader = new StreamReader(cmd.OutputStream))
        {
            while (!async.IsCompleted || !reader.EndOfStream)
            {
                string? line = reader.ReadLine();
                if (line is null) { Thread.Sleep(100); continue; }
                // Only a secret's VALUE stays out of the log (plus the installer's two lines that print the
                // token, and any line that assigns one of the secret keys). The installer's
                // "token   : kept / CHANGES" summary must reach the admin: it is the one warning that this
                // run is about to disconnect every other launcher build; its MUIP key line shows a
                // fingerprint only and its MySQL / Flask lines no value at all, so they pass.
                // A line that is kept out of the log is kept out of `output` as well — that text feeds
                // installerFailed's tail, which goes to the error modal and verbatim into relic.log, and
                // an update knows none of the box's secrets to blank in it. What is accumulated is
                // redacted first all the same.
                if (LeaksSecret(line, secrets)) continue;
                sb.AppendLine(Redact(line, secrets));
                log?.Invoke(line.TrimEnd());
            }
        }
        try { cmd.EndExecute(async); }
        catch (SshOperationTimeoutException)
        {
            // SSH.NET builds the timeout message as "Command '<CommandText>' timed out." — never let that
            // reach the admin or the log: it would quote the whole remote command line.
            throw new InvalidOperationException(L.T("core.deploy.installerTimeout", new { minutes = (int)cmd.CommandTimeout.TotalMinutes }));
        }
        // stderr goes through the same filter as stdout — a sudo/bash diagnostic can echo the assignment.
        string err = Redact(cmd.Error ?? "", secrets);
        if (err.Length > 0)
        {
            sb.AppendLine(err);
            foreach (var l in err.Split('\n')) if (l.Trim().Length > 0) log?.Invoke(l.TrimEnd());
        }
        return (cmd.ExitStatus ?? -1, sb.ToString());
    }

    private static void UploadTree(SftpClient sftp, SshClient ssh, string localDir, string remoteDir)
    {
        RunOut(ssh, $"mkdir -p {Q(remoteDir)}");
        foreach (var f in Directory.GetFiles(localDir)) UploadFile(sftp, f, remoteDir + "/" + Path.GetFileName(f));
        foreach (var d in Directory.GetDirectories(localDir)) UploadTree(sftp, ssh, d, remoteDir + "/" + Path.GetFileName(d));
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];

    private static void UploadFile(SftpClient sftp, string localPath, string remotePath)
    {
        using var fs = File.OpenRead(localPath);
        sftp.UploadFile(fs, remotePath, canOverride: true);
    }

    private static void UploadText(SftpClient sftp, string text, string remotePath)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));
        sftp.UploadFile(ms, remotePath, canOverride: true);
    }
}
