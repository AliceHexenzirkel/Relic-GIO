using System.Text;
using Relic.Core.Setup;
using Relic.Core.Util;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;

namespace Relic.Core.Server;

/// <summary>SSH endpoint + credentials for a fresh-server install. Password and/or private key
/// (OpenSSH / PEM / PuTTY .ppk, optional passphrase). A non-root user needs sudo; the sudo password
/// defaults to the SSH password when blank.</summary>
public sealed record SshTarget(string Host, int Port, string User, string Password, string KeyPath, string KeyPassphrase, string SudoPassword)
{
    public bool IsRoot => string.Equals(User, "root", StringComparison.Ordinal);
    public string EffectiveSudoPassword => string.IsNullOrEmpty(SudoPassword) ? Password : SudoPassword;

    /// <summary>What the record prints (an interpolation, a debugger): the address and the user — never the password,
    /// the key's passphrase or the sudo password.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Host = ").Append(Host).Append(", Port = ").Append(Port).Append(", User = ").Append(User);
        return true;
    }
}

/// <summary>Outcome of an install. <paramref name="Warning"/> is set when the installer itself succeeded
/// but the agent did not answer /health in time: the box IS configured and running the new token, so this
/// must never be raised as a failure — the caller still has to save the connection, or the admin is left
/// with an agent whose token nobody has ever seen.</summary>
public sealed record DeployResult(string HostKeyFingerprint, string Health, string Warning = "",
    IReadOnlyList<string>? Fetch = null)
{
    /// <summary>The versions the agent really has to download afterwards — the plan's downloads minus the
    /// ones whose folder already held a stack (install_agent.sh skips those silently).</summary>
    public IReadOnlyList<string> FetchVersions => Fetch ?? Array.Empty<string>();

    /// <summary>The type the server's key names itself by (<see cref="HostKeyPins.KeyType"/>) — with
    /// <see cref="HostKeyFingerprint"/>, the pin a caller records after the run.</summary>
    public string HostKeyType { get; init; } = "";
}

/// <summary>A server's SSH host key as a run saw it: its type and fingerprint (<see cref="HostKeyPin"/>), how it stands
/// against the keys pinned for its host and port (<see cref="Verdict"/>) and those pins as a sentence part
/// (<see cref="Pinned"/>, "" for none).</summary>
public sealed record HostKeySeen(string Type, string Fingerprint, HostKeyVerdict Verdict, string Pinned)
{
    /// <summary>The key is one of the pinned ones.</summary>
    public bool Known => Verdict == HostKeyVerdict.Match;

    /// <summary>Keys are pinned there and this one is none of them.</summary>
    public bool Changed => Verdict == HostKeyVerdict.Changed;

    /// <summary>new | match | changed.</summary>
    public string VerdictName => HostKeyPins.Name(Verdict);
}

/// <summary>A connection refused because of the key the server presented (<see cref="HostKeyCheck"/>): nothing was sent
/// over it. The message is the user's text (<c>core.deploy.hostKeyChanged</c>); <see cref="Seen"/> the key.</summary>
public sealed class HostKeyRefusedException : InvalidOperationException
{
    public HostKeySeen Seen { get; }

    public HostKeyRefusedException(string message, HostKeySeen seen) : base(message) => Seen = seen;
}

/// <summary>The free space of a folder's filesystem on the Linux server: the bytes free (null = not read) and where that
/// filesystem is mounted.</summary>
public sealed record BoxFree(long? Avail, string Mount);

/// <summary>What the read-only look at a Linux server found (<see cref="AgentDeployer.ParseBox"/>): the system, its
/// memory, what stands at each folder asked and the free space of its filesystem, the folders an installed agent's
/// configuration stores (only when the look ran as root), the tools that are there, whether an agent is installed,
/// its IPv4 addresses and the source address of its route out.</summary>
public sealed record BoxFacts
{
    public string OsId { get; init; } = "";
    public string OsVersion { get; init; } = "";
    /// <summary>The server's memory (MemTotal), null when not read.</summary>
    public long? MemTotal { get; init; }
    public IReadOnlyDictionary<string, bool> Exists { get; init; } = new Dictionary<string, bool>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, FolderKind> Kinds { get; init; } = new Dictionary<string, FolderKind>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, BoxFree> Free { get; init; } = new Dictionary<string, BoxFree>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> StoredDirs { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public bool HasPython { get; init; }
    public bool HasDocker { get; init; }
    /// <summary>The first 7z tool found (7zz, 7z, 7za, 7zr, bsdtar), "" for none.</summary>
    public string SevenZip { get; init; } = "";
    public bool HasCurl { get; init; }
    public bool HasWget { get; init; }
    /// <summary><c>/etc/gio-agent/config</c> exists: an agent is installed there.</summary>
    public bool AgentInstalled { get; init; }
    /// <summary>The server's own IPv4 addresses (scope global).</summary>
    public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();
    /// <summary>The source address of the server's route out ("" = not read).</summary>
    public string RouteSource { get; init; } = "";
    /// <summary>The look ran as root (else the stored folders are not known).</summary>
    public bool AsRoot { get; init; }

    /// <summary>The folder the installed agent's configuration stores for <paramref name="version"/>, "" when none (or
    /// not known).</summary>
    public string StoredDir(string version) => StoredDirs.TryGetValue(ServerVersions.DirKey(version), out var d) ? d : "";

    /// <summary>Is <paramref name="ip"/> one of the server's own addresses?</summary>
    public bool HasAddress(string? ip) => !string.IsNullOrWhiteSpace(ip) && Addresses.Contains(ip.Trim(), StringComparer.Ordinal);
}

/// <summary>What <see cref="AgentDeployer.Probe"/> answers: phase <c>key</c> — the server presented another key than the
/// one accepted, and only <see cref="HostKey"/> is filled — or phase <c>box</c>: the key, how the user may act as root
/// (<see cref="Access"/>: root | sudo | sudoPassword | needsSudoPassword | sudoRefused | noSudo), the server's facts and,
/// when asked, its public address.</summary>
public sealed record BoxProbe(string Phase, HostKeySeen HostKey, string? Access, BoxFacts? Facts, PublicIpResult? PublicIp);

/// <summary>What a root probe of a Linux box found (<see cref="AgentDeployer.ParseProbe"/>): per path asked,
/// whether it exists (<paramref name="ExistsMap"/>) and what stands there (<paramref name="Kinds"/>); the stack
/// folders the box's configuration stores, by config key (<paramref name="StoredDirs"/>, <c>GIO_DIR_16</c> →
/// folder); and whether python3 and docker compose are there.</summary>
public sealed record ProbeFacts(IReadOnlyDictionary<string, bool> ExistsMap, IReadOnlyDictionary<string, FolderKind> Kinds,
    IReadOnlyDictionary<string, string> StoredDirs, bool HasPython, bool HasDocker)
{
    /// <summary>Does <paramref name="path"/> exist on the box? A path the probe did not answer for does not.</summary>
    public bool Exists(string path) => ExistsMap.TryGetValue(path, out var e) && e;

    /// <summary>What stands at <paramref name="path"/>; <see cref="FolderKind.Absent"/> for a path the probe did not answer for.</summary>
    public FolderKind KindOf(string path) => Kinds.TryGetValue(path, out var k) ? k : FolderKind.Absent;

    /// <summary>The folder the box's configuration stores for <paramref name="version"/> (its
    /// <see cref="ServerVersions.DirKey"/> line), "" when it stores none.</summary>
    public string StoredDir(string version) => StoredDirs.TryGetValue(ServerVersions.DirKey(version), out var d) ? d : "";
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
    /// Before anything is uploaded the box is probed as root: the stack folders its configuration stores
    /// go into <paramref name="plan"/> (<see cref="AgentInstallPlan.KeepStoredFolders"/> — the plan is
    /// changed in place, as the installer keeps those folders on the box), then the plan's folders are
    /// checked. <paramref name="hostKeys"/> = the keys pinned for the box's host and port, and the one the user
    /// confirmed (<see cref="HostKeyCheck"/>): a key they do not allow ends both connections before anything is
    /// uploaded (<see cref="HostKeyRefusedException"/>); without it the key is taken as it comes and reported.
    /// </summary>
    public static DeployResult Install(SshTarget ssh, AgentInstallPlan plan, string agentDir, Action<string>? log = null, HostKeyCheck? hostKeys = null)
    {
        var errs = plan.Validate();
        if (errs.Count > 0) throw new InvalidOperationException(string.Join("\n", errs));
        RequireAgentFiles(agentDir);
        using var session = Open(ssh, log, hostKeys);
        var client = session.Client;
        var sftp = session.Sftp;
        string up = session.Up;

        // Every look at the box is ROOT's, as the installer's own: a stack folder below /root, or one the
        // SSH user may not list, reads to that user as absent. First what the box already has — the stack
        // folders its configuration stores, python3, docker compose —: a folder the box already uses for a
        // version stays whatever the form named (install_agent.sh keeps it the same way), so the plan
        // takes the stored folders before anything is checked. The plan is changed in place; the caller
        // reads its rows as the install left them.
        var box = ProbeAsRoot(client, ssh, Array.Empty<string>(), stored: true);
        foreach (var k in plan.KeepStoredFolders(box.StoredDir))
            if (k.Replaced) log?.Invoke(L.T("core.deploy.dirKept", new { version = k.Version, dir = k.Stored, given = k.Given }));
        // Then the folders the plan names, in one probe. Stack folders must already be there — unless the
        // version is chosen for download: then the folder is expected to be absent or empty and the AGENT
        // fills it. Nothing is started here: under GIO_RELIC_ENV=1 install_agent.sh leaves the download to
        // the launcher, which runs the fetch jobs from the Server page after deploy.done (they take 10-45
        // minutes on archive.org).
        var paths = plan.ProbePaths();
        var facts = paths.Count > 0 ? ProbeAsRoot(client, ssh, paths, stored: false) : ParseProbe("", paths);
        var missing = plan.MissingStackEntries(facts.Exists);
        if (missing.Count > 0) throw new InvalidOperationException(L.T("core.deploy.stackIncomplete", new { list = string.Join(", ", missing) }));
        var occupied = plan.DownloadTargetProblems(facts.KindOf);
        if (occupied.Count > 0) throw new InvalidOperationException(string.Join("\n", occupied));
        // The same rule install_agent.sh applies: a folder that already holds a stack is NOT downloaded,
        // so the launcher must not queue it either (the agent would answer 409 "already on this server"
        // and the queue behind it would be dropped).
        var willFetch = plan.FetchVersionsReally(facts.Exists);
        foreach (var v in plan.FetchVersions())
            log?.Invoke(willFetch.Contains(v)
                ? L.T("core.deploy.willFetch", new { version = v, dir = plan.DirFor(v) })
                : L.T("core.deploy.fetchSkipped", new { version = v, dir = plan.DirFor(v) }));
        // Neither is a stop: install_agent.sh installs python3 (>= 3.9) and docker + compose v2
        // itself when they are missing -- the lines only tell the admin what the installer is about to do.
        if (!box.HasPython) log?.Invoke(L.T("core.deploy.noPython3OnBox"));
        if (!box.HasDocker) log?.Invoke(L.T("core.deploy.noDockerWarn"));

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
            return new DeployResult(session.Fingerprint, health.Trim(), warning, willFetch) { HostKeyType = session.HostKeyType };
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
    /// <paramref name="hostKeys"/> as for <see cref="Install"/>.
    /// </summary>
    public static DeployResult Upgrade(SshTarget ssh, string agentDir, bool force = false, Action<string>? log = null, HostKeyCheck? hostKeys = null)
    {
        RequireAgentFiles(agentDir);
        using var session = Open(ssh, log, hostKeys);
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
            return new DeployResult(session.Fingerprint, "") { HostKeyType = session.HostKeyType };
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
        /// <summary>The fingerprint of the key the SSH connection trusted; the SFTP connection must present the same.</summary>
        public string Fingerprint { get; set; } = "";
        public string HostKeyType { get; set; } = "";
        /// <summary>The key a connection refused, when one did.</summary>
        public HostKeySeen? Refused { get; set; }
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

    /// <summary>Connect with the password and / or the private key of <paramref name="ssh"/>. BOTH connections — the SSH
    /// one and the SFTP one that uploads install.env with the token — check the key the server presents before
    /// anything is sent over them: the SSH connection trusts what <paramref name="hostKeys"/> allows
    /// (<see cref="HostKeyPins.Trusts"/>; no check = the key as it comes), and the SFTP connection only that very key.
    /// With pins, both offer only the host key algorithms of the pinned type (<see cref="PreferPinnedTypes"/>); a server
    /// that holds no key of that type is asked once more with every algorithm, and the key it presents then is judged
    /// the same way. A refused key ends the run (<see cref="HostKeyRefusedException"/>, <see cref="RefusalText"/>). The
    /// fingerprint and type are kept for the caller to record.</summary>
    private static Session Open(SshTarget ssh, Action<string>? log, HostKeyCheck? hostKeys = null)
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
        ConnectionInfo Info() => new ConnectionInfo(ssh.Host, ssh.Port, ssh.User, auth.ToArray()) { Timeout = TimeSpan.FromSeconds(20) };
        string pinned = HostKeyPins.Describe(hostKeys?.Pins);

        // The two connections of one attempt, each with its check of the key.
        Session Make(ConnectionInfo ci)
        {
            var session = new Session { Client = new SshClient(ci), Sftp = new SftpClient(ci) };
            session.Client.HostKeyReceived += (_, e) =>
            {
                string fingerprint = "SHA256:" + e.FingerPrintSHA256, type = HostKeyPins.KeyType(e.HostKey);
                session.Fingerprint = fingerprint;
                session.HostKeyType = type;
                log?.Invoke(L.T("core.deploy.hostKey", new { fingerprint }));
                e.CanTrust = HostKeyPins.Trusts(hostKeys, fingerprint);
                if (!e.CanTrust) session.Refused = new HostKeySeen(type, fingerprint, HostKeyPins.Decide(hostKeys?.Pins, fingerprint), pinned);
            };
            session.Sftp.HostKeyReceived += (_, e) =>
            {
                string fingerprint = "SHA256:" + e.FingerPrintSHA256;
                // The connection that carries the token presents the very key the first one trusted, or it is refused.
                e.CanTrust = session.Fingerprint.Length > 0 && string.Equals(fingerprint, session.Fingerprint, StringComparison.Ordinal)
                    && HostKeyPins.Trusts(hostKeys, fingerprint);
                if (!e.CanTrust)
                    session.Refused = new HostKeySeen(HostKeyPins.KeyType(e.HostKey), fingerprint, HostKeyVerdict.Changed,
                        session.Fingerprint.Length > 0 ? (session.HostKeyType + " " + session.Fingerprint).Trim() : pinned);
            };
            return session;
        }

        var info = Info();
        bool restricted = PreferPinnedTypes(info, hostKeys?.Pins);
        var current = Make(info);
        try
        {
            log?.Invoke(L.T("core.deploy.connecting", new { host = ssh.Host, port = ssh.Port, user = ssh.User }));
            try
            {
                try { current.Client.Connect(); }
                catch (SshConnectionException ex) when (restricted && current.Fingerprint.Length == 0 && ex.DisconnectReason == DisconnectReason.KeyExchangeFailed)
                {
                    // No key of the pinned type there: asked with every algorithm, the server presents the key it has.
                    current.Dispose();
                    current = Make(Info());
                    current.Client.Connect();
                }
                current.Sftp.Connect();
            }
            catch (Exception) when (current.Refused is { } seen)
            {
                throw new HostKeyRefusedException(RefusalText(ssh, seen, hostKeys), seen);
            }
            string home = RunOut(current.Client, "echo ~").Trim();
            if (home.Length == 0) home = ssh.IsRoot ? "/root" : "/home/" + ssh.User;
            current.Up = home + "/.relic-agent-upload";
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    /// <summary>Offer only the host key algorithms of the pinned key types (<see cref="HostKeyPins.AlgorithmsFor"/>), in
    /// the client's own order: a server that holds keys of several types then presents the pinned one. True when the
    /// list was narrowed; false — every algorithm stays — without a pin of a known type.</summary>
    private static bool PreferPinnedTypes(ConnectionInfo ci, IReadOnlyList<HostKeyPin>? pins)
    {
        if (HostKeyPins.AlgorithmsFor(ci.HostKeyAlgorithms.Keys, pins) is not { } keep) return false;
        foreach (var name in ci.HostKeyAlgorithms.Keys.ToList())
            if (!keep.Contains(name, StringComparer.Ordinal)) ci.HostKeyAlgorithms.Remove(name);
        return true;
    }

    /// <summary>The user's text for a key a connection refused: a key other than the one the user confirmed
    /// (<c>core.deploy.hostKeyNotConfirmed</c>, which names the confirmed one), else another key than the pinned ones
    /// (<c>core.deploy.hostKeyChanged</c>, which names the pins — never a fingerprint confirmed a moment ago as one
    /// Relic trusted before).</summary>
    private static string RefusalText(SshTarget ssh, HostKeySeen seen, HostKeyCheck? hostKeys)
    {
        string accepted = (hostKeys?.Accepted ?? "").Trim();
        if (accepted.Length > 0 && !string.Equals(seen.Fingerprint, accepted, StringComparison.Ordinal))
            return L.T("core.deploy.hostKeyNotConfirmed", new { host = ssh.Host, port = ssh.Port, type = seen.Type, fingerprint = seen.Fingerprint, accepted });
        return L.T("core.deploy.hostKeyChanged", new { host = ssh.Host, port = ssh.Port, type = seen.Type, fingerprint = seen.Fingerprint, pinned = seen.Pinned });
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

    /// <summary>The command that runs <paramref name="runner"/> as root — in the folder <paramref name="cwd"/>
    /// (the staging folder of an install or an update), or where the login lands when it is null (a probe):
    /// directly for root, through <c>sudo -n</c> when it asks for no password, else <c>sudo -S -k -p ''</c>
    /// with the sudo password as the only stdin line.</summary>
    private static (string CmdText, string? Stdin) RootCommand(SshClient client, SshTarget ssh, string? cwd, string runner)
    {
        string cd = cwd is null ? "" : $"cd {Q(cwd)} && ";
        if (ssh.IsRoot) return ($"{cd}bash -c {Q(runner)}", null);
        if (Exit(client, "sudo -n true") == 0) return ($"{cd}sudo -n bash -c {Q(runner)}", null);
        if (string.IsNullOrEmpty(ssh.EffectiveSudoPassword)) throw new InvalidOperationException(L.T("core.deploy.sudoPasswordNeeded"));
        return ($"{cd}sudo -S -k -p '' bash -c {Q(runner)}", ssh.EffectiveSudoPassword + "\n");
    }

    /// <summary>A sudo that wants a terminal or refuses the password, read from what a command run as root
    /// printed (<see cref="RootCommand"/>).</summary>
    private static void ThrowOnSudoFailure(string output)
    {
        if (output.Contains("must have a tty", StringComparison.OrdinalIgnoreCase) || output.Contains("requiretty", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("core.deploy.requireTty"));
        if (output.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) || output.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("core.deploy.sudoRefused"));
    }

    /// <summary>The installer run as its outcome: a sudo that wants a terminal or refuses the password, an
    /// install.env that was not applied (97), a stack folder relocated while it ran
    /// (<see cref="DirsChangedRefusal"/>), any other exit code with the redacted end of the output.</summary>
    private static void ThrowOnFailure(int code, string output, string[] secrets)
    {
        ThrowOnSudoFailure(output);
        if (code == 97) throw new InvalidOperationException(L.T("core.deploy.envNotApplied"));
        if (DirsChangedRefusal(code, output) is { } changed) throw new InvalidOperationException(changed);
        if (code != 0) throw new InvalidOperationException(L.T("core.deploy.installerFailed", new { code, tail = Redact(Tail(output, 600), secrets) }));
    }

    /// <summary>The first line install_agent.sh prints when a stack folder of its configuration changed while
    /// it ran (a relocation, from a launcher or with <c>--relocate</c> on the box): this text, the path of the
    /// configuration, <see cref="DirsChangedTail"/>. The script's <c>refuse_if_dirs_changed</c> prints it and
    /// exits 1 — once before anything of the agent is replaced, once with the new files in place.</summary>
    public const string DirsChangedHead = "The stack folders in ";

    /// <summary>The end of that line (<see cref="DirsChangedHead"/>).</summary>
    public const string DirsChangedTail = " changed while this ran -- a relocation, from a launcher or with --relocate on this box?";

    /// <summary>The line that ends the refusal; it says what the run had done by then.</summary>
    public const string DirsChangedEnd = "Writing this run's configuration would undo that.";

    /// <summary>What the refusal's last line says when the new agent files were already in place.</summary>
    public const string DirsChangedFilesIn = "The new agent files are in place, but the configuration was not written and the agent was not restarted.";

    /// <summary>The user-facing refusal of an installer run that stopped because a stack folder of the box's
    /// configuration changed while it ran, or null when <paramref name="output"/> (exit
    /// <paramref name="code"/>) is not that: the changed folders as the installer names them, and whether
    /// the new agent files were in place by then. Pure.</summary>
    public static string? DirsChangedRefusal(int code, string? output)
    {
        if (code != 1) return null;
        var lines = (output ?? "").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int head = lines.FindIndex(l => l.StartsWith(DirsChangedHead, StringComparison.Ordinal) && l.EndsWith(DirsChangedTail, StringComparison.Ordinal));
        if (head < 0) return null;
        var changes = new List<string>();
        bool filesIn = false;
        for (int i = head + 1; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith(DirsChangedEnd, StringComparison.Ordinal))
            {
                filesIn = line.Contains(DirsChangedFilesIn, StringComparison.Ordinal);
                break;
            }
            if (line.Length > 0) changes.Add(line);
        }
        string list = changes.Count > 0 ? string.Join("; ", changes) : "?";
        return filesIn
            ? L.T("core.deploy.dirsChangedFilesIn", new { changes = list })
            : L.T("core.deploy.dirsChanged", new { changes = list });
    }

    /// <summary>The page's text for an update over SSH that install_agent.sh stopped once the new agent files were in
    /// place, right before it would write the configuration, because a stack folder of that configuration changed
    /// while it ran (a relocation): <c>backend.agent.updateDirsChangedFilesIn</c> with the changes the installer named
    /// — or null when <paramref name="message"/> is any other failure. Read from the run's streamed
    /// <paramref name="lines"/> (an element may hold several lines; CR line ends are dropped) by the rule
    /// <see cref="DirsChangedRefusal"/> reads them by, and only when that refusal is the one the run failed with.
    /// Pure; never throws.</summary>
    public static string? UpdateStoppedText(string? message, IReadOnlyList<string>? lines)
    {
        try
        {
            string output = string.Join("\n", lines ?? Array.Empty<string>());
            if (DirsChangedRefusal(1, output) is not { } refusal || refusal != message) return null;
            var all = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int head = all.FindIndex(l => l.StartsWith(DirsChangedHead, StringComparison.Ordinal) && l.EndsWith(DirsChangedTail, StringComparison.Ordinal));
            var changes = new List<string>();
            for (int i = head + 1; head >= 0 && i < all.Count; i++)
            {
                string line = all[i].Trim();
                if (line.StartsWith(DirsChangedEnd, StringComparison.Ordinal))
                    return line.Contains(DirsChangedFilesIn, StringComparison.Ordinal)
                        ? L.T("backend.agent.updateDirsChangedFilesIn", new { changes = changes.Count > 0 ? string.Join("; ", changes) : "?" })
                        : null;
                if (line.Length > 0) changes.Add(line);
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Info($"remote agent update: the installer's lines were not read ({ex.Message})");
            return null;
        }
    }

    /// <summary>The shell script of a root probe of the box: a line <c>P&lt;i&gt; &lt;yes|no&gt; &lt;kind&gt;</c>
    /// per path of <paramref name="paths"/> — whether the path exists, and what stands there by the
    /// agent's <c>relocate_target_kind</c> (absent | empty | stack | other, the installer's
    /// <c>folder_kind</c>) —, and with <paramref name="stored"/> the box's own facts: every
    /// <c>GIO_DIR_&lt;nn&gt;</c> line of <c>/etc/gio-agent/config</c> (<c>S &lt;line&gt;</c> — only those lines:
    /// no secret of that file leaves the box), and whether <c>python3</c> (<c>PY</c>) and
    /// <c>docker compose</c> (<c>DC</c>) are there. Its stdin is <c>/dev/null</c> (sudo has read its password
    /// from the real one by then); it always exits 0. Pure.</summary>
    public static string ProbeScript(IReadOnlyList<string> paths, bool stored)
    {
        var sb = new StringBuilder();
        sb.Append("exec </dev/null\n");
        if (paths.Count > 0)
            sb.Append("k() { if [ ! -e \"$1\" ] && [ ! -L \"$1\" ]; then echo absent; elif [ ! -d \"$1\" ]; then echo other; "
                + "elif ! ls -A -- \"$1\" >/dev/null 2>&1; then echo other; elif [ -z \"$(ls -A -- \"$1\")\" ]; then echo empty; "
                + "elif [ -f \"$1/docker-compose.yml.tmpl\" ] || [ -f \"$1/docker-compose.yml\" ]; then echo stack; else echo other; fi; }\n");
        for (int i = 0; i < paths.Count; i++)
        {
            string q = Q(paths[i]);
            sb.Append($"if [ -e {q} ]; then e=yes; else e=no; fi; echo \"P{i} $e $(k {q})\"\n");
        }
        if (stored)
        {
            sb.Append("if [ -r /etc/gio-agent/config ]; then grep -E '^[[:space:]]*GIO_DIR_[0-9]+[[:space:]]*=' /etc/gio-agent/config | sed 's/^/S /'; fi\n");
            sb.Append("if command -v python3 >/dev/null 2>&1; then echo 'PY yes'; else echo 'PY no'; fi\n");
            sb.Append("if docker compose version >/dev/null 2>&1; then echo 'DC yes'; else echo 'DC no'; fi\n");
        }
        sb.Append("exit 0\n");
        return sb.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex ProbePathLine = new(@"^P([0-9]+) (yes|no) (absent|empty|stack|other)$");
    private static readonly System.Text.RegularExpressions.Regex ProbeFlagLine = new(@"^(PY|DC) (yes|no)$");
    private static readonly System.Text.RegularExpressions.Regex StoredDirKey = new(@"^GIO_DIR_[0-9]+$");

    /// <summary>What a root probe printed (<see cref="ProbeScript"/>), for <paramref name="paths"/> — the list the
    /// script was made from. A line is read with its line end dropped whatever it was (the probe's output is
    /// collected with <c>AppendLine</c>, CRLF on Windows). A stored line is read by install_agent.sh's own
    /// rule for its configuration: the key before the first '=' (blanks after it dropped), the value after
    /// it (blanks around it dropped, one pair of matching quotes stripped), only a <c>GIO_DIR_&lt;digits&gt;</c>
    /// key, the LAST line of a key wins — what systemd and the installer read. Anything else is ignored; a
    /// path the output names no line for reads as not there. Pure; never throws.</summary>
    public static ProbeFacts ParseProbe(string? output, IReadOnlyList<string> paths)
    {
        var exists = new Dictionary<string, bool>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, FolderKind>(StringComparer.Ordinal);
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        bool python = false, docker = false;
        foreach (var raw in (output ?? "").Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            var path = ProbePathLine.Match(line);
            if (path.Success)
            {
                if (int.TryParse(path.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int i)
                    && i < paths.Count)
                {
                    exists[paths[i]] = path.Groups[2].Value == "yes";
                    kinds[paths[i]] = path.Groups[3].Value switch
                    {
                        "empty" => FolderKind.Empty,
                        "stack" => FolderKind.Stack,
                        "other" => FolderKind.Other,
                        _ => FolderKind.Absent,
                    };
                }
                continue;
            }
            var flag = ProbeFlagLine.Match(line);
            if (flag.Success)
            {
                if (flag.Groups[1].Value == "PY") python = flag.Groups[2].Value == "yes"; else docker = flag.Groups[2].Value == "yes";
                continue;
            }
            if (!line.StartsWith("S ", StringComparison.Ordinal)) continue;
            string pair = line[2..].TrimStart();
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string key = pair[..eq].TrimEnd(), value = pair[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'') value = value[1..^1];
            if (StoredDirKey.IsMatch(key)) stored[key] = value;
        }
        return new ProbeFacts(exists, kinds, stored, python, docker);
    }

    /// <summary>One root probe of the box (<see cref="ProbeScript"/>, run by <see cref="RootCommand"/> from
    /// where the login lands), with a deadline of its own: a probe that hangs — a stalled network mount
    /// under a stack path, a wedged docker — refuses the install with its own text, before anything is
    /// uploaded. The sudo password travels as stdin; nothing of the probe's output is shown but the end of a
    /// failure, and that end is blanked of it.</summary>
    private static ProbeFacts ProbeAsRoot(SshClient c, SshTarget ssh, IReadOnlyList<string> paths, bool stored)
    {
        var (cmdText, stdin) = RootCommand(c, ssh, null, ProbeScript(paths, stored));
        string[] secrets = ssh.EffectiveSudoPassword is { Length: >= 8 } pw ? new[] { pw } : Array.Empty<string>();
        // Every line is read as it comes, none left out for holding a secret (RunStreaming's rule for what it
        // shows): a folder name is any text — the password's included —, and a line left out would read as a
        // folder that is not there, or as a stored folder the box does not have.
        var (code, output) = RunStreaming(c, cmdText, stdin, Array.Empty<string>(), null, ProbeTimeout,
            () => L.T("core.deploy.probeTimeout", new { seconds = (int)ProbeTimeout.TotalSeconds }));
        ThrowOnSudoFailure(output);
        if (code != 0) throw new InvalidOperationException(L.T("core.deploy.probeFailed", new { code, tail = Redact(Tail(output, 400), secrets) }));
        return ParseProbe(output, paths);
    }

    // ── Quick Start's look at a Linux server ──

    /// <summary>
    /// Phase <c>key</c>: the key the server at <paramref name="host"/>:<paramref name="port"/> presents, and how it
    /// stands against <paramref name="pins"/>. The key is read and never trusted (<c>CanTrust = false</c>), so the key
    /// exchange ends there: no authentication is attempted and no password leaves this PC. With pins only the host key
    /// algorithms of the pinned type are offered (a server that holds keys of several types presents the pinned one);
    /// a server that holds no key of that type is asked once more with every algorithm. A server that cannot be
    /// reached throws the connection's own error.
    /// </summary>
    public static HostKeySeen ReadHostKey(string host, int port, string? user, IReadOnlyList<HostKeyPin>? pins, Action<string>? log = null)
    {
        string u = string.IsNullOrWhiteSpace(user) ? "root" : user.Trim();
        string type = "", fingerprint = "";
        // One key exchange; true when it offered the pinned types alone and the server holds none of them.
        bool Read(bool preferPinned)
        {
            var ci = new ConnectionInfo(host, port, u, new NoneAuthenticationMethod(u)) { Timeout = TimeSpan.FromSeconds(20) };
            bool restricted = preferPinned && PreferPinnedTypes(ci, pins);
            using var client = new SshClient(ci);
            client.HostKeyReceived += (_, e) =>
            {
                type = HostKeyPins.KeyType(e.HostKey);
                fingerprint = "SHA256:" + e.FingerPrintSHA256;
                e.CanTrust = false;
            };
            try { client.Connect(); }
            catch (Exception) when (fingerprint.Length > 0) { /* refused on purpose, the key was read */ }
            catch (SshConnectionException ex) when (restricted && ex.DisconnectReason == DisconnectReason.KeyExchangeFailed) { return true; }
            return false;
        }
        log?.Invoke(L.T("core.deploy.connecting", new { host, port, user = u }));
        if (Read(preferPinned: true)) Read(preferPinned: false);
        if (fingerprint.Length == 0) throw new InvalidOperationException(L.T("core.deploy.hostKeyNone", new { host, port }));
        log?.Invoke(L.T("core.deploy.hostKeyRead", new { host, port, type, fingerprint }));
        return new HostKeySeen(type, fingerprint, HostKeyPins.Decide(pins, fingerprint), HostKeyPins.Describe(pins));
    }

    /// <summary>
    /// Phase <c>box</c>: connect (both connections) trusting <paramref name="acceptFingerprint"/> alone — the key the
    /// user confirmed; a key the pins call changed only with <paramref name="acceptChanged"/>, the user's answer to the
    /// changed-key warning —, then say how the user may act as root (<see cref="BoxProbe.Access"/>), look at the server
    /// read-only (<see cref="ProbeBoxScript"/>: as root when the access allows, else as the user, whose look does not
    /// see the stored folders) and, with <paramref name="publicIpServices"/>, learn its public address on the server
    /// itself (<see cref="PublicIpScript"/>, as the SSH user; <see cref="PublicAddress.Agree"/>). A server that presents
    /// another key than the accepted one, or a changed key not confirmed as such, answers phase <c>key</c> again, and
    /// nothing is sent to it.
    /// </summary>
    public static BoxProbe Probe(SshTarget ssh, string? acceptFingerprint, IReadOnlyList<HostKeyPin>? pins, IReadOnlyList<string> dirs,
        IReadOnlyList<string>? publicIpServices, Action<string>? log = null, bool acceptChanged = false)
    {
        if (string.IsNullOrWhiteSpace(acceptFingerprint))
            return new BoxProbe("key", ReadHostKey(ssh.Host, ssh.Port, ssh.User, pins, log), null, null, null);
        Session session;
        try { session = Open(ssh, log, new HostKeyCheck(pins ?? Array.Empty<HostKeyPin>(), acceptFingerprint.Trim(), acceptChanged)); }
        catch (HostKeyRefusedException ex)
        {
            var s = ex.Seen;
            return new BoxProbe("key", new HostKeySeen(s.Type, s.Fingerprint, HostKeyPins.Decide(pins, s.Fingerprint), HostKeyPins.Describe(pins)), null, null, null);
        }
        using (session)
        {
            var seen = new HostKeySeen(session.HostKeyType, session.Fingerprint, HostKeyPins.Decide(pins, session.Fingerprint), HostKeyPins.Describe(pins));
            string access = AccessOf(session.Client, ssh);
            bool asRoot = access is "root" or "sudo" or "sudoPassword";
            var facts = LookAtBox(session.Client, ssh, dirs ?? Array.Empty<string>(), asRoot);
            PublicIpResult? publicIp = null;
            if (publicIpServices is { Count: > 0 } services)
            {
                log?.Invoke(L.T("core.deploy.probe.publicIp", new { services = string.Join(", ", services.Select(PublicAddress.ServiceName)) }));
                var (_, said) = RunStreaming(session.Client, $"bash -c {Q(PublicIpScript(services))}", null, Array.Empty<string>(), null,
                    TimeSpan.FromSeconds(60), () => L.T("core.deploy.probeTimeout", new { seconds = 60 }));
                publicIp = PublicAddress.Agree(ParsePublicIp(said, services));
            }
            return new BoxProbe("box", seen, access, facts, publicIp);
        }
    }

    /// <summary>How the SSH user may act as root: <c>root</c> (the user is root), <c>sudo</c> (<c>sudo -n true</c>),
    /// <c>sudoPassword</c> (sudo takes <see cref="SshTarget.EffectiveSudoPassword"/>), <c>needsSudoPassword</c> (sudo
    /// asks one and there is none to try), <c>sudoRefused</c> (it refused that one), <c>noSudo</c> (no sudo, or the user
    /// may not use it). The password travels as the only line on the command's stdin.</summary>
    private static string AccessOf(SshClient c, SshTarget ssh)
    {
        if (ssh.IsRoot) return "root";
        if (Exit(c, "command -v sudo >/dev/null 2>&1") != 0) return "noSudo";
        if (Exit(c, "sudo -n true") == 0) return "sudo";
        if (string.IsNullOrEmpty(ssh.EffectiveSudoPassword)) return "needsSudoPassword";
        string[] secrets = ssh.EffectiveSudoPassword is { Length: >= 8 } pw ? new[] { pw } : Array.Empty<string>();
        var (code, output) = RunStreaming(c, "sudo -S -k -p '' true", ssh.EffectiveSudoPassword + "\n", secrets, null, ProbeTimeout,
            () => L.T("core.deploy.probeTimeout", new { seconds = (int)ProbeTimeout.TotalSeconds }));
        if (code == 0) return "sudoPassword";
        return SudoForbids(output) ? "noSudo" : "sudoRefused";
    }

    /// <summary>Does sudo's answer say the user may not use it at all (not in the sudoers, no rule for this host, a
    /// terminal required)? Pure.</summary>
    public static bool SudoForbids(string? output)
    {
        string o = output ?? "";
        return o.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase) || o.Contains("may not run sudo", StringComparison.OrdinalIgnoreCase)
            || o.Contains("is not allowed to", StringComparison.OrdinalIgnoreCase) || o.Contains("requiretty", StringComparison.OrdinalIgnoreCase)
            || o.Contains("must have a tty", StringComparison.OrdinalIgnoreCase) || o.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The look at the box (<see cref="ProbeBoxScript"/>) as root or as the user, with the probe's deadline.</summary>
    private static BoxFacts LookAtBox(SshClient c, SshTarget ssh, IReadOnlyList<string> dirs, bool asRoot)
    {
        string script = ProbeBoxScript(dirs);
        var (cmdText, stdin) = asRoot ? RootCommand(c, ssh, null, script) : ($"bash -c {Q(script)}", null);
        string[] secrets = ssh.EffectiveSudoPassword is { Length: >= 8 } pw ? new[] { pw } : Array.Empty<string>();
        var (code, output) = RunStreaming(c, cmdText, stdin, Array.Empty<string>(), null, ProbeTimeout,
            () => L.T("core.deploy.probeTimeout", new { seconds = (int)ProbeTimeout.TotalSeconds }));
        if (asRoot) ThrowOnSudoFailure(output);
        if (code != 0) throw new InvalidOperationException(L.T("core.deploy.probeFailed", new { code, tail = Redact(Tail(output, 400), secrets) }));
        return ParseBox(output, dirs) with { AsRoot = asRoot };
    }

    /// <summary>What stands at a path, by the agent's <c>relocate_target_kind</c> (the installer's <c>folder_kind</c>).</summary>
    private const string KindFunction =
        "k() { if [ ! -e \"$1\" ] && [ ! -L \"$1\" ]; then echo absent; elif [ ! -d \"$1\" ]; then echo other; "
        + "elif ! ls -A -- \"$1\" >/dev/null 2>&1; then echo other; elif [ -z \"$(ls -A -- \"$1\")\" ]; then echo empty; "
        + "elif [ -f \"$1/docker-compose.yml.tmpl\" ] || [ -f \"$1/docker-compose.yml\" ]; then echo stack; else echo other; fi; }\n";

    /// <summary>
    /// The ONE read-only script of Quick Start's look at a Linux server: per folder of <paramref name="dirs"/> its line
    /// <c>P&lt;i&gt; &lt;yes|no&gt; &lt;kind&gt;</c> (as <see cref="ProbeScript"/>) and <c>F&lt;i&gt; &lt;bytes free&gt; &lt;mount point&gt;</c> of
    /// its nearest existing parent (<c>df</c>); every <c>GIO_DIR_&lt;nn&gt;</c> line of <c>/etc/gio-agent/config</c>
    /// (<c>S</c> — nothing else of that file leaves the box, and only a look as root can read it) and whether it exists
    /// (<c>AG</c>); whether python3 (<c>PY</c>), <c>docker compose</c> (<c>DC</c>), a 7z tool (<c>Z &lt;name&gt;|no</c>), curl
    /// (<c>CU</c>) and wget (<c>WG</c>) are there; <c>/etc/os-release</c>'s ID and VERSION_ID (<c>O</c>), MemTotal
    /// (<c>M</c>), the server's own IPv4 addresses (<c>A</c>) and the source address of its route out (<c>R</c>, asked
    /// for a documentation address: nothing is sent). Every path is quoted; stdin is <c>/dev/null</c>; it always exits
    /// 0. Pure.
    /// </summary>
    public static string ProbeBoxScript(IReadOnlyList<string> dirs)
    {
        var sb = new StringBuilder();
        sb.Append("exec </dev/null\n");
        sb.Append(KindFunction);
        sb.Append("d() { p=$1; while [ ! -e \"$p\" ] && [ \"$p\" != / ] && [ \"$p\" != . ]; do p=$(dirname -- \"$p\"); done; "
            + "df -B1 --output=avail,target -- \"$p\" 2>/dev/null | tail -n 1; }\n");
        for (int i = 0; i < (dirs ?? Array.Empty<string>()).Count; i++)
        {
            string q = Q(dirs![i]);
            sb.Append($"if [ -e {q} ]; then e=yes; else e=no; fi; echo \"P{i} $e $(k {q})\"; echo \"F{i} $(d {q})\"\n");
        }
        sb.Append("if [ -r /etc/gio-agent/config ]; then grep -E '^[[:space:]]*GIO_DIR_[0-9]+[[:space:]]*=' /etc/gio-agent/config | sed 's/^/S /'; fi\n");
        sb.Append("if [ -e /etc/gio-agent/config ]; then echo 'AG yes'; else echo 'AG no'; fi\n");
        sb.Append("if command -v python3 >/dev/null 2>&1; then echo 'PY yes'; else echo 'PY no'; fi\n");
        sb.Append("if docker compose version >/dev/null 2>&1; then echo 'DC yes'; else echo 'DC no'; fi\n");
        sb.Append("z=no; for t in 7zz 7z 7za 7zr bsdtar; do if command -v \"$t\" >/dev/null 2>&1; then z=$t; break; fi; done; echo \"Z $z\"\n");
        sb.Append("if command -v curl >/dev/null 2>&1; then echo 'CU yes'; else echo 'CU no'; fi\n");
        sb.Append("if command -v wget >/dev/null 2>&1; then echo 'WG yes'; else echo 'WG no'; fi\n");
        sb.Append("if [ -r /etc/os-release ]; then grep -E '^(ID|VERSION_ID)=' /etc/os-release | sed 's/^/O /'; fi\n");
        sb.Append("grep -E '^MemTotal:' /proc/meminfo 2>/dev/null | sed 's/^/M /'\n");
        sb.Append("ip -4 -o addr show scope global 2>/dev/null | sed 's/^/A /'\n");
        sb.Append("ip -4 route get 203.0.113.1 2>/dev/null | head -n 1 | sed 's/^/R /'\n");
        sb.Append("exit 0\n");
        return sb.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex BoxFreeLine = new(@"^F([0-9]+)(?: ([0-9]+)(?: (.*))?)?$");
    private static readonly System.Text.RegularExpressions.Regex BoxFlagLine = new(@"^(AG|CU|WG) (yes|no)$");
    private static readonly System.Text.RegularExpressions.Regex BoxOsLine = new(@"^O (ID|VERSION_ID)=(.*)$");
    private static readonly System.Text.RegularExpressions.Regex BoxMemLine = new(@"^M MemTotal:\s+([0-9]+)\s*kB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex BoxInet = new(@"\binet ([0-9]{1,3}(?:\.[0-9]{1,3}){3})/");
    private static readonly System.Text.RegularExpressions.Regex BoxSrc = new(@"\bsrc ([0-9]{1,3}(?:\.[0-9]{1,3}){3})\b");

    /// <summary>What <see cref="ProbeBoxScript"/> printed, for <paramref name="dirs"/> — the list the script was made
    /// from: the <c>P</c>, <c>S</c>, <c>PY</c> and <c>DC</c> lines as <see cref="ParseProbe"/> reads them, and the rest of
    /// the script's lines (a line end of any kind dropped). Anything else is ignored. Pure; never throws.</summary>
    public static BoxFacts ParseBox(string? output, IReadOnlyList<string> dirs)
    {
        var list = dirs ?? Array.Empty<string>();
        var basic = ParseProbe(output, list);
        var free = new Dictionary<string, BoxFree>(StringComparer.Ordinal);
        var addresses = new List<string>();
        string osId = "", osVersion = "", sevenZip = "", route = "";
        long? mem = null;
        bool agent = false, curl = false, wget = false;
        foreach (var raw in (output ?? "").Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            var f = BoxFreeLine.Match(line);
            if (f.Success)
            {
                if (int.TryParse(f.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int i) && i < list.Count)
                    free[list[i]] = new BoxFree(f.Groups[2].Success && long.TryParse(f.Groups[2].Value, out long avail) ? avail : null,
                        f.Groups[3].Success ? f.Groups[3].Value.Trim() : "");
                continue;
            }
            var flag = BoxFlagLine.Match(line);
            if (flag.Success)
            {
                bool yes = flag.Groups[2].Value == "yes";
                switch (flag.Groups[1].Value) { case "AG": agent = yes; break; case "CU": curl = yes; break; default: wget = yes; break; }
                continue;
            }
            if (line.StartsWith("Z ", StringComparison.Ordinal)) { string z = line[2..].Trim(); sevenZip = z == "no" ? "" : z; continue; }
            var os = BoxOsLine.Match(line);
            if (os.Success)
            {
                string v = os.Groups[2].Value.Trim();
                if (v.Length >= 2 && v[0] == v[^1] && v[0] is '"' or '\'') v = v[1..^1];
                if (os.Groups[1].Value == "ID") osId = v; else osVersion = v;
                continue;
            }
            var m = BoxMemLine.Match(line);
            if (m.Success && long.TryParse(m.Groups[1].Value, out long kb)) { mem = kb * 1024; continue; }
            if (line.StartsWith("A ", StringComparison.Ordinal))
            {
                var inet = BoxInet.Match(line);
                if (inet.Success && PublicAddress.ParseIpv4(inet.Groups[1].Value) is not null && !addresses.Contains(inet.Groups[1].Value)) addresses.Add(inet.Groups[1].Value);
                continue;
            }
            if (line.StartsWith("R ", StringComparison.Ordinal))
            {
                var src = BoxSrc.Match(line);
                if (src.Success && PublicAddress.ParseIpv4(src.Groups[1].Value) is not null) route = src.Groups[1].Value;
            }
        }
        return new BoxFacts
        {
            OsId = osId, OsVersion = osVersion, MemTotal = mem,
            Exists = basic.ExistsMap, Kinds = basic.Kinds, Free = free, StoredDirs = basic.StoredDirs,
            HasPython = basic.HasPython, HasDocker = basic.HasDocker, SevenZip = sevenZip, HasCurl = curl, HasWget = wget,
            AgentInstalled = agent, Addresses = addresses, RouteSource = route,
        };
    }

    /// <summary>The script that asks the address services on the server, as the SSH user: per URL of
    /// <paramref name="services"/> (quoted) <c>curl -4 -fsS --max-time 5 &lt;url&gt; || wget -4 -qO- -T 5 &lt;url&gt;</c>, its
    /// answer's first 64 characters on a line <c>U&lt;i&gt; &lt;answer&gt;</c>. Stdin is <c>/dev/null</c>; it always exits 0.
    /// Pure.</summary>
    public static string PublicIpScript(IReadOnlyList<string> services)
    {
        var sb = new StringBuilder();
        sb.Append("exec </dev/null\n");
        sb.Append("u() { curl -4 -fsS --max-time 5 \"$1\" 2>/dev/null || wget -4 -qO- -T 5 \"$1\" 2>/dev/null; }\n");
        for (int i = 0; i < (services ?? Array.Empty<string>()).Count; i++)
            sb.Append($"echo \"U{i} $(u {Q(services![i])} | head -c 64 | tr -d '\\r\\n')\"\n");
        sb.Append("exit 0\n");
        return sb.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex PublicIpLine = new(@"^U([0-9]+) ?(.*)$");

    /// <summary>The answers <see cref="PublicIpScript"/> printed, one per service in <paramref name="services"/>' order
    /// ("" for a service the output names no answer for). Pure.</summary>
    public static IReadOnlyList<ServiceAnswer> ParsePublicIp(string? output, IReadOnlyList<string> services)
    {
        var said = new string[(services ?? Array.Empty<string>()).Count];
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var m = PublicIpLine.Match(raw.TrimEnd('\r').Trim());
            if (m.Success && int.TryParse(m.Groups[1].Value, out int i) && i < said.Length) said[i] = m.Groups[2].Value.Trim();
        }
        return (services ?? Array.Empty<string>()).Select((s, i) => new ServiceAnswer(s, said[i] ?? "")).ToList();
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
    /// <see cref="LeaksSecret"/> is never surfaced), optionally feeding <paramref name="stdin"/> once.
    /// <paramref name="timeout"/> = its deadline (45 minutes when null, the installer's), and
    /// <paramref name="timeoutText"/> what is said when it passes (the installer's text when null).</summary>
    private static (int Code, string Output) RunStreaming(SshClient c, string text, string? stdin, string[] secrets, Action<string>? log,
        TimeSpan? timeout = null, Func<string>? timeoutText = null)
    {
        using var cmd = c.CreateCommand(text);
        // A fresh box runs apt-get update + the whole docker install inside this one command, which 10
        // minutes on a slow link does not cover — and a timeout here kills the install half-done.
        cmd.CommandTimeout = timeout ?? TimeSpan.FromMinutes(45);
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
            throw new InvalidOperationException(timeoutText?.Invoke()
                ?? L.T("core.deploy.installerTimeout", new { minutes = (int)cmd.CommandTimeout.TotalMinutes }));
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
