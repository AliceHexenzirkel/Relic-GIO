using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relic.Core.Util;
using Renci.SshNet;

namespace Relic.Core.Server;

/// <summary>
/// Talks to the GIO agent on the server box, authenticating every call with a bearer token.
/// "direct" mode (normal users): plain HTTP straight to Host:AgentPort — the agent binds the LAN
/// and the token (baked into the build) is the only credential the user's machine holds.
/// "ssh" mode (a legacy record, which no UI creates): the agent binds 127.0.0.1 only and is reached
/// through an SSH connection + local port-forward, so nothing is exposed to the network. That connection's host
/// key is checked against the keys pinned for its host and port (I58) before anything is sent through it. The tunnel
/// writes no pin itself: it reports the key it trusted (<see cref="TunnelKey"/>), and the Backend's admin calls pin a key
/// seen there for the first time once the agent accepted the token over it.
/// </summary>
public sealed class AgentClient : IAsyncDisposable
{
    private readonly ServerConfig _cfg;
    private readonly IReadOnlyList<Setup.HostKeyPin>? _sshHostKeys;
    private SshClient? _ssh;
    private ForwardedPortLocal? _forward;
    private HttpClient? _http;

    /// <param name="cfg">The connection.</param>
    /// <param name="sshHostKeys">For a tunnel (<c>Mode: ssh</c>): the SSH host keys pinned for its host and port, as the
    /// caller holds them; null = the pins the launcher's state file holds, read when the tunnel connects. A direct
    /// connection opens no SSH and never looks at them.</param>
    public AgentClient(ServerConfig cfg, IReadOnlyList<Setup.HostKeyPin>? sshHostKeys = null)
    {
        _cfg = cfg;
        _sshHostKeys = sshHostKeys;
    }

    public bool IsConnected => _http is not null && (_cfg.IsDirect || _ssh?.IsConnected == true);

    /// <summary>The SSH host key the tunnel's connection trusted — its type, its fingerprint and how it stood against the
    /// pins the tunnel was checked against (<see cref="Setup.HostKeyVerdict.New"/>: none was pinned for its host and port, the
    /// key taken as it came). Null for a direct connection and until <see cref="ConnectAsync"/> connected.</summary>
    public HostKeySeen? TunnelKey { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (_cfg.IsDirect)
        {
            _http = MakeHttp(new Uri($"http://{_cfg.Host}:{_cfg.AgentPort}/"));
            return Task.CompletedTask;
        }
        return Task.Run(() =>
        {
            // The box's SSH host key is checked like every SSH connection of the launcher (I58, AgentDeployer): against
            // the keys pinned for this host and port (HostKeyPins.Trusts — a pinned key, or with no pin the key as it
            // comes; a changed key is refused, so the bearer token never goes through a connection to another key),
            // offering only the host key algorithms of the pinned type; a box without a key of that type (the key
            // exchange fails before any key is seen) is asked once more with every algorithm, its key judged the same
            // way. A refused key ends the connect with the Core's own refusal (HostKeyRefusedException): a caller tells it
            // from a box that does not answer. The key trusted is reported once the connection stands (TunnelKey): the
            // Backend's admin calls pin one seen for the first time after the agent accepted the token over it.
            var pins = _sshHostKeys ?? PinnedHostKeys(_cfg.Host, _cfg.Port);
            var check = new Setup.HostKeyCheck(pins);
            HostKeySeen? refused = null, trusted = null;
            bool keySeen = false;
            SshClient Make(bool narrow, out bool narrowed)
            {
                var conn = new ConnectionInfo(_cfg.Host, _cfg.Port, _cfg.User,
                    new PasswordAuthenticationMethod(_cfg.User, _cfg.Password))
                {
                    Timeout = TimeSpan.FromSeconds(15),
                };
                narrowed = false;
                if (narrow && Setup.HostKeyPins.AlgorithmsFor(conn.HostKeyAlgorithms.Keys, pins) is { } keep)
                {
                    foreach (string name in conn.HostKeyAlgorithms.Keys.ToList())
                        if (!keep.Contains(name, StringComparer.Ordinal)) conn.HostKeyAlgorithms.Remove(name);
                    narrowed = true;
                }
                var ssh = new SshClient(conn);
                ssh.HostKeyReceived += (_, e) =>
                {
                    string fingerprint = "SHA256:" + e.FingerPrintSHA256;
                    keySeen = true;
                    e.CanTrust = Setup.HostKeyPins.Trusts(check, fingerprint);
                    var key = new HostKeySeen(Setup.HostKeyPins.KeyType(e.HostKey), fingerprint,
                        Setup.HostKeyPins.Decide(pins, fingerprint), Setup.HostKeyPins.Describe(pins));
                    if (e.CanTrust) trusted = key;
                    else refused = key;
                };
                return ssh;
            }
            _ssh = Make(narrow: true, out bool restricted);
            try
            {
                try { _ssh.Connect(); }
                catch (Renci.SshNet.Common.SshConnectionException ex) when (restricted && !keySeen
                    && ex.DisconnectReason == Renci.SshNet.Messages.Transport.DisconnectReason.KeyExchangeFailed)
                {
                    _ssh.Dispose();
                    _ssh = Make(narrow: false, out _);
                    _ssh.Connect();
                }
            }
            catch (Exception) when (refused is { } seen)
            {
                throw new HostKeyRefusedException(L.T("core.deploy.hostKeyChanged", new
                {
                    host = _cfg.Host, port = _cfg.Port, type = seen.Type, fingerprint = seen.Fingerprint, pinned = seen.Pinned,
                }), seen);
            }
            TunnelKey = trusted;

            // local ephemeral port -> agent's 127.0.0.1:AgentPort on the server
            _forward = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)_cfg.AgentPort);
            _ssh.AddForwardedPort(_forward);
            _forward.Start();

            _http = MakeHttp(new Uri($"http://127.0.0.1:{_forward.BoundPort}/"));
        }, ct);
    }

    /// <summary>The SSH host keys the launcher's state pins for a box's address (I58), as the state FILE holds them —
    /// what a tunnel made without the caller's pins is checked against, in every process of this user (the launcher, a
    /// desktop shortcut's play process). None when none is pinned or when there is no state file: the key is then taken
    /// as it comes. A state file that is there but cannot be read may pin this very box, so the tunnel is refused
    /// (<c>core.deploy.hostKeysUnreadable</c>) rather than trusting whatever key comes.</summary>
    private static IReadOnlyList<Setup.HostKeyPin> PinnedHostKeys(string host, int port)
    {
        var state = State.RelicState.TryRead();
        if (state is null)
        {
            if (File.Exists(State.RelicState.DefaultPath))
                throw new InvalidOperationException(L.T("core.deploy.hostKeysUnreadable", new { host, port }));
            return Array.Empty<Setup.HostKeyPin>();
        }
        return state.HostKeyPinsFor(host, port).Select(pin => new Setup.HostKeyPin(pin.Type, pin.Fingerprint)).ToList();
    }

    private HttpClient MakeHttp(Uri baseAddress)
    {
        // UseProxy=false: agent calls must NOT go through the system (WinINET) proxy. During a play
        // session Relic points that proxy at Fiddler (the game-redirect mechanism); a direct-mode
        // call to the LAN host would otherwise transit Fiddler and fail whenever it is mid-start/stop
        // or dead. The agent is always reached directly (LAN host in direct mode, the SSH-forwarded
        // loopback port in tunnel mode).
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _cfg.AgentToken);
        return http;
    }

    public Task<JsonElement> StatusAsync(CancellationToken ct = default) => GetAsync("status", ct);

    public Task<JsonElement> StartServerAsync(string version, Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/start", new { version }, onLine, ct);

    public Task<JsonElement> StopServerAsync(string version, Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/stop", new { version }, onLine, ct);

    /// <summary>First install of a version's stack on the box: bootstrap.sh, then the provisioning
    /// pass. Tens of minutes the first time (docker images + MariaDB init).
    /// <para><paramref name="progress"/>: <c>default</c> imports the shipped save (the
    /// pre-made account, every player account on the version wiped), <c>keep</c> touches neither the
    /// database nor sdk.db/redis (no pre-made login; the rest — config files, events, abyss calendar,
    /// advertised IP, hotpatch, templates — is done), <c>fixes</c> replaces only the configuration
    /// (txt) files. An older agent ignores the field and imports the save.</para>
    /// <para><paramref name="pathfinding"/> is a TRI-STATE: null goes out as
    /// <c>"pathfinding":null</c> = "not given" (the agent keeps its stored decision, or GIO_PATHFINDING
    /// on a stack that was never bootstrapped); true/false is applied to the compose template BEFORE
    /// the bootstrap renders it. Never collapse an absent field to false: that would switch the
    /// service off for every caller that simply did not ask.</para>
    /// <para><paramref name="hotpatch"/> is the same tri-state for the official in-version fixes: null goes out
    /// as <c>"hotpatch":null</c> = "not given", and the agent applies its own default to a stack that holds no
    /// decision yet; true / false decide it at this Prepare. A stack already decided keeps its record.</para></summary>
    public Task<JsonElement> SetupServerAsync(string version, string? txtFixes = null, bool force = false,
        string progress = "default", bool? pathfinding = null, bool? hotpatch = null,
        Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/setup", new { version, txtFixes, force, progress, pathfinding, hotpatch }, onLine, ct);

    /// <summary>Re-apply the pre-GAA save, the endless events and the advertised IP to an already
    /// installed stack. <paramref name="progress"/> as in <see cref="SetupServerAsync"/>: <c>keep</c>
    /// and <c>fixes</c> are the non-destructive re-runs.</summary>
    public Task<JsonElement> ProvisionServerAsync(string version, string? txtFixes = null,
        string progress = "default", Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/provision", new { version, txtFixes, progress }, onLine, ct);

    /// <summary>Download the ready-made server package of <paramref name="version"/> from the
    /// Internet Archive into the configured stack folder — a job that YIELDS like the voice-pack one
    /// (the watchdog's own jobs and player signups run beside its download + extraction; every
    /// admin-started job waits). Resumable: a cut keeps the <c>.part</c> and the same call continues by
    /// Range; a verified <c>&lt;dir&gt;.7z</c> already next to the folder is adopted without downloading.
    /// The stack "appears" only at the final rename, so /status never sees a half-extracted one.
    /// Refused synchronously (409) when the version is already on the box, the folder holds files that
    /// are not a stack, no 7z extractor exists there or the disk is too small; 404 when the version has
    /// no folder configured. A cancel (<see cref="FetchCancelAsync"/>) ends it with the agent's
    /// "Stopped on request …" 409 — that is what the follower receives.</summary>
    public Task<JsonElement> FetchServerAsync(string version, Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/fetch", new { version }, onLine, ct);

    /// <summary>Ask the running package download of <paramref name="version"/> to stop — a
    /// plain POST of <c>{version, cancel: true}</c> to the job's own path, NOT a job: answers at once with
    /// <c>{ok, stopping}</c> (<c>false</c> = no fetch job of that version runs). The <c>.part</c> stays and
    /// the next <see cref="FetchServerAsync"/> resumes it. Takes no agent lock — meant to be sent WHILE
    /// this client follows the job.</summary>
    public Task<JsonElement> FetchCancelAsync(string version, CancellationToken ct = default)
        => PostAsync("server/fetch", new { version, cancel = true }, ct);

    /// <summary>Run the pathfinding server (monster navigation, ~4 GB of RAM) with the stack
    /// or exclude it — a job. Edits BOTH compose files (the .tmpl every re-render reads and the rendered
    /// one <c>up -d</c> reads) with the vendor's own <c>profiles: [donotstart]</c> syntax; on a running
    /// stack it stops and removes the container (disable) or starts it and runs the 75 s stability
    /// check (enable — on a box without the RAM that check fails with the OOM restart loop in the log).
    /// Result <c>{enabled, changed, applied}</c>.</summary>
    public Task<JsonElement> PathfindingSetAsync(string version, bool enabled, Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/pathfinding", new { version, enabled }, onLine, ct);

    /// <summary>Have the agent download the navmesh archive of <paramref name="version"/> (the catalogue's
    /// <c>navmesh</c> entry — https only, Range-resumable, the size pinned and the sha256 verified once the
    /// file is complete) into its own payload folder, adopt it as the version's bundled navmesh, install
    /// the files into the stack when one is present and — when that version's pathfindingserver is
    /// running — restart that one service so the files load. A job; its result names what happened.
    /// An agent without the route answers 404 on the POST, which is turned into the "agent must be
    /// updated" text (the pattern of <see cref="AccountCreateAsync"/>; a 404 while FOLLOWING the job is a
    /// forgotten job and stays what it is). A cancel (<see cref="NavmeshCancelAsync"/>) ends it with the
    /// agent's "Stopped on request …" 409 and the <c>.part</c> stays for the next call.</summary>
    public async Task<JsonElement> NavmeshFetchAsync(string version, string url, string sha256, long size,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        JsonElement started;
        try { started = await PostAsync("server/navmesh", new { version, url, sha256, size }, ct); }
        catch (AgentHttpException ex) when (ex.Status == 404)
        {
            throw new AgentHttpException(404, L.T("core.agent.navmeshUnsupported"));
        }
        return await FollowJobAsync(started, onLine, ct);
    }

    /// <summary>Ask the running navmesh download of <paramref name="version"/> to stop — a plain POST
    /// of <c>{version, cancel: true}</c> to the job's own path, like <see cref="FetchCancelAsync"/>:
    /// answers at once with <c>{ok, stopping}</c>, takes no agent lock, meant to be sent WHILE this
    /// client follows the job.</summary>
    public Task<JsonElement> NavmeshCancelAsync(string version, CancellationToken ct = default)
        => PostAsync("server/navmesh", new { version, cancel = true }, ct);

    /// <summary>Have the agent download the pathfinding server's own data of <paramref name="version"/> —
    /// the archive its catalogue <c>payloads/&lt;version&gt;/navdata.json</c> names: Range-resumable, size
    /// and md5 verified —, unpack it on the box and add to the stack's <c>server/res</c> the files it lacks
    /// (a file already there is left as it is), and — when that version's pathfindingserver is running —
    /// restart that one service. A job; its result names what happened. The route's own refusals are never
    /// 404, so a 404 on the POST is an agent without the route: the "agent must be updated" text (a 404
    /// while FOLLOWING the job is a forgotten job and stays what it is). A cancel
    /// (<see cref="NavdataCancelAsync"/>) ends it with the agent's "Stopped on request …" 409 and the
    /// <c>.part</c> stays for the next call.</summary>
    public async Task<JsonElement> NavdataFetchAsync(string version, Action<string>? onLine = null, CancellationToken ct = default)
    {
        JsonElement started;
        try { started = await PostAsync("server/navdata", new { version }, ct); }
        catch (AgentHttpException ex) when (ex.Status == 404)
        {
            throw new AgentHttpException(404, L.T("core.agent.navdataUnsupported"));
        }
        return await FollowJobAsync(started, onLine, ct);
    }

    /// <summary>Ask the running pathfinding data download of <paramref name="version"/> to stop — a plain
    /// POST of <c>{version, cancel: true}</c> to the job's own path, like <see cref="NavmeshCancelAsync"/>:
    /// answers at once with <c>{ok, stopping}</c>, takes no agent lock, meant to be sent WHILE this client
    /// follows the job.</summary>
    public Task<JsonElement> NavdataCancelAsync(string version, CancellationToken ct = default)
        => PostAsync("server/navdata", new { version, cancel = true }, ct);

    /// <summary>Install ("apply") or restore ("revert") the fixed excel-config txt files.</summary>
    public Task<JsonElement> TxtFixesAsync(string version, string action = "apply",
        Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/txtfixes", new { version, action }, onLine, ct);

    /// <summary>Re-apply ONLY the event schedule (GAA open until 2050, everything else pushed into a
    /// closed window in 1998). Separate from provisioning on purpose: that one re-imports the player
    /// save, and changing which events run must not cost the player their progress. Answers 409 while
    /// the stack is stopped — every start re-applies the schedule anyway.</summary>
    public Task<JsonElement> EventsAsync(string version, Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/events", new { version }, onLine, ct);

    /// <summary>Re-point the stack's advertised (WAN) address at the agent's configured IP.</summary>
    public Task<JsonElement> NetFixAsync(string version, Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/netfix", new { version }, onLine, ct);

    /// <summary>Copy one account's whole progress onto another, both named by their login: the plain request
    /// <c>{version, from, to}</c> every agent reads the same way (<see cref="AccountCopyAsync(AccountCopyRequest, Action{string}, CancellationToken)"/>).</summary>
    public Task<JsonElement> AccountCopyAsync(string version, string from, string to,
        Action<string>? onLine = null, CancellationToken ct = default)
        => AccountCopyAsync(new AccountCopyRequest(version, From: from, To: to), onLine, ct);

    /// <summary>Copy a save over a player of the same stack — a job (<c>accountcopy</c>). Runs with the stack up —
    /// both players just have to be OFFLINE; the agent rewrites the (uid&lt;&lt;32)|seq guids inside the save blob
    /// (naively copied rows collide in co-op), backs the target's rows up on the box, and writes to sdk.db and
    /// t_player_uid only to create and allocate a target that needs it. The body is
    /// <see cref="AccountCopyRequest.Body"/>: ONE source (an account, a game uid, a progress template), ONE target
    /// (an account — created when missing, under a name a signup may take — or a game uid) and <c>dryRun</c> only
    /// when one is asked for: a dry run checks the whole copy and creates, allocates and writes nothing.
    /// <para>Only the plain request (<see cref="AccountCopyRequest.IsPlain"/>) means the same to every agent. An
    /// agent without the account list (GET /server/accounts) registers and allocates the target of a dry run, and
    /// refuses a uid or a template source as a missing field — so every other request goes out only once the agent
    /// has shown that it has that route (<see cref="HasAccountListAsync"/>); to one that has not, nothing is posted
    /// and the "update the agent" text comes back as a 404. The route refuses a malformed request itself with a 400
    /// before any job exists, never with a 404. Returns the final snapshot <c>{state, result: {srcUid, dstUid, guids,
    /// blocks, dryRun, backup, template, passwordNeeded, …}}</c> as the agent wrote it —
    /// <see cref="AccountCopyForPage"/> is what the page may see of it. Its lines and its error come as every job's
    /// do, through <see cref="JobTextForPage"/>: the line that names the backup names its file alone.</para></summary>
    public async Task<JsonElement> AccountCopyAsync(AccountCopyRequest request,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        var body = request.Body();
        if (!request.IsPlain && !await HasAccountListAsync(request.Version, ct))
            throw new AgentHttpException(404, L.T("core.agent.accountCopyUnsupported"));
        return await RunJobAsync("server/account/copy", body, onLine, ct);
    }

    /// <summary>Does the agent have the account list (GET /server/accounts) — and with it the copy route that takes a
    /// dry run, a game uid and a progress template? Asked with a query the route refuses before it reads anything
    /// (<c>limit=0</c>: a 400 from an agent that has it); an agent without the route answers 404. Every other answer
    /// is the route's own, so it is there. A 401 (the revocation path of an open key) and a connection that fails
    /// reach the caller as they are.</summary>
    private async Task<bool> HasAccountListAsync(string version, CancellationToken ct)
    {
        try
        {
            await GetAsync($"server/accounts?version={Uri.EscapeDataString(version)}&limit=0", ct);
            return true;
        }
        catch (AgentHttpException ex) when (ex.Status == 404) { return false; }
        catch (AgentHttpException ex) when (ex.Status != 401) { return true; }
    }

    /// <summary>The final snapshot of a copy job as the page may see it: <c>{state, result}</c>, the result holding only
    /// the fields the copy route documents — the uids (<c>srcUid</c>, <c>dstUid</c>; <c>dstUid</c> null in a dry run
    /// whose target has no game uid yet), <c>guids</c>, <c>blocks</c>, <c>dryRun</c>, <c>template</c>,
    /// <c>passwordNeeded</c>, what a dry run would do (<c>wouldCreate</c>, <c>wouldAllocate</c>, <c>wouldGetUid</c>)
    /// and what a copy did (<c>created</c>, <c>allocated</c>) — every one null when the agent named none, and
    /// <c>backup</c> as a bool: the agent names the backup by its file name or by its path on the box, and a path of
    /// the box never reaches the page — the job's line about the backup reaches it with the file name alone
    /// (<see cref="JobTextForPage"/>).
    /// Never the job's lines: they reach the page as the job streams them. An answer that carries no <c>result</c>
    /// object is read as the result itself. Pure, never throws.</summary>
    public static JsonElement AccountCopyForPage(JsonElement snapshot)
    {
        var r = snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("result", out var inner)
            && inner.ValueKind == JsonValueKind.Object ? inner : snapshot;
        var result = new JsonObject();
        foreach (string name in new[] { "srcUid", "dstUid", "guids", "blocks" }) result[name] = WholeOf(r, name);
        result["dryRun"] = FlagOf(r, "dryRun");
        result["backup"] = JobText(r, "backup") is { Length: > 0 };
        result["template"] = JobText(r, "template");
        result["passwordNeeded"] = FlagOf(r, "passwordNeeded");
        result["wouldCreate"] = FlagOf(r, "wouldCreate");
        result["wouldAllocate"] = FlagOf(r, "wouldAllocate");
        result["wouldGetUid"] = WholeOf(r, "wouldGetUid");
        result["created"] = FlagOf(r, "created");
        result["allocated"] = FlagOf(r, "allocated");
        return JsonSerializer.SerializeToElement(new JsonObject { ["state"] = JobText(snapshot, "state"), ["result"] = result });
    }

    /// <summary>Move accounts, with all they have, from a server of another version on the agent's machine — or from
    /// another server of this version, kept in a folder — into a version that keeps running: a job (<c>migrate</c>). The
    /// body is <see cref="MigrateRequest.Body"/>. A check (<c>dryRun</c>) reads the source through a throwaway container
    /// and writes nothing to the target; a real run names its accounts and writes them one at a time. The route refuses a
    /// malformed request with a 400 before any job exists and never answers 404, so no probe goes first: a 404 on the POST
    /// is an agent without the route, and comes back as the "agent must be updated" text (a 404 while FOLLOWING the job is
    /// a forgotten job and stays what it is). A 401 stays the 401 it is — the revocation path of an open key. Cancelling
    /// <paramref name="ct"/> only stops FOLLOWING the job; stopping the job itself is <see cref="MigrateCancelAsync"/>.
    /// Returns the final snapshot as the agent wrote it — <see cref="MigrateForPage"/> is what the page may see of it. The
    /// job's lines and its error come as every job's do, through <see cref="JobTextForPage"/>.</summary>
    public async Task<JsonElement> MigrateAsync(MigrateRequest request, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var body = request.Body();
        JsonElement started;
        try { started = await PostAsync("server/migrate", body, ct); }
        catch (AgentHttpException ex) when (ex.Status == 404)
        {
            throw new AgentHttpException(404, L.T("core.agent.migrateUnsupported"));
        }
        return await FollowJobAsync(started, onLine, ct);
    }

    /// <summary>Ask the running move of <paramref name="version"/> to stop — a plain POST of <c>{version, cancel:
    /// true}</c> to the job's own path, like <see cref="NavdataCancelAsync"/>: NOT a job, it answers at once with
    /// <c>{ok, stopping}</c> (<c>false</c> = no move of that version runs, which is no error), takes no agent lock and is
    /// meant to be sent WHILE this client follows the job. The job goes on to its tail for the accounts already moved and
    /// ends done, its result saying <c>stopped</c>. An agent without the route answers 404 (or a 400, a route that reads no
    /// stop): both come back as <c>{ok: false, stopping: false, unsupported: true}</c>, never as a failed call.</summary>
    public async Task<JsonElement> MigrateCancelAsync(string version, CancellationToken ct = default)
    {
        try { return await PostAsync("server/migrate", new { version, cancel = true }, ct); }
        catch (AgentHttpException ex) when (ex.Status is 400 or 404)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, stopping = false, unsupported = true });
        }
    }

    /// <summary>How many accounts a move's result names (the agent's own limit), and how many ids it names of one
    /// list.</summary>
    private const int MigrateListMax = 2000, MigrateIdsMax = 32;

    /// <summary>The final snapshot of a move as the page may see it: <c>{state, result}</c>, the result holding only the
    /// fields the route documents, for a check and a real run alike — <c>dryRun</c>, <c>target</c>, <c>source {kind,
    /// version}</c>, <c>passwordVerify {source, target}</c>, <c>keepPassword</c>, <c>keepUid</c>, <c>listed</c>,
    /// <c>checked</c>, <c>notChecked</c>, <c>totals</c>, <c>stopped</c>, <c>failed {from, reason}</c>,
    /// <c>leftRegistered [{name, inGame}]</c> and <c>accounts</c> (<see cref="MigrateAccountForPage"/>). A field absent or
    /// of another kind is null, a version id that is none is null too, a list of another kind is empty and an answer that
    /// carries no <c>result</c> object is read as the result itself. No folder of the box: a folder source is named by its
    /// kind and its version alone. Never the job's lines: they reach the page as the job streams them. Pure, never
    /// throws.</summary>
    public static JsonElement MigrateForPage(JsonElement snapshot)
    {
        var r = snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("result", out var inner)
            && inner.ValueKind == JsonValueKind.Object ? inner : snapshot;
        var result = new JsonObject
        {
            ["dryRun"] = FlagOf(r, "dryRun"),
            ["target"] = MigrateVersionOf(r, "target"),
            ["source"] = ObjectOf(r, "source", out var source)
                ? new JsonObject { ["kind"] = JobText(source, "kind") is "version" or "folder" ? JobText(source, "kind") : null, ["version"] = MigrateVersionOf(source, "version") } : null,
            ["passwordVerify"] = ObjectOf(r, "passwordVerify", out var verify)
                ? new JsonObject { ["source"] = FlagOf(verify, "source"), ["target"] = FlagOf(verify, "target") } : null,
            ["keepPassword"] = FlagOf(r, "keepPassword"),
            ["keepUid"] = FlagOf(r, "keepUid"),
            ["listed"] = WholeOf(r, "listed"),
            ["checked"] = WholeOf(r, "checked"),
            ["notChecked"] = WholeOf(r, "notChecked"),
            ["stopped"] = FlagOf(r, "stopped") == true,
        };
        var totals = new JsonObject();
        ObjectOf(r, "totals", out var counts);
        foreach (string name in new[] { "create", "overwrite", "skip", "moved", "failed", "notDone" }) totals[name] = WholeOf(counts, name);
        result["totals"] = totals;
        result["failed"] = ObjectOf(r, "failed", out var failed)
            ? new JsonObject { ["from"] = JobText(failed, "from"), ["reason"] = PageTextOf(failed, "reason") } : null;
        var left = new JsonArray();
        foreach (var row in ObjectsOf(r, "leftRegistered", MigrateListMax))
            left.Add(new JsonObject { ["name"] = JobText(row, "name"), ["inGame"] = FlagOf(row, "inGame") == true });
        result["leftRegistered"] = left;
        var accounts = new JsonArray();
        foreach (var row in ObjectsOf(r, "accounts", MigrateListMax)) accounts.Add(MigrateAccountForPage(row));
        result["accounts"] = accounts;
        return JsonSerializer.SerializeToElement(new JsonObject { ["state"] = JobText(snapshot, "state"), ["result"] = result });
    }

    /// <summary>One account of a move's result as the page may see it: the documented fields and nothing else — its login,
    /// source uid and character, where it goes and the verdict with its code (<see cref="MigrateWordOf"/>) and sentence, the
    /// target account it meets, the uids, what the transform did (<c>relocated</c>, the counts, <c>cut</c> and the id lists,
    /// each <see cref="MigrateIds"/>), what the server discards at the first login, the password's fate and the two facts
    /// of a real run. No password, hash, e-mail, IP or path; a sentence of the agent passes through
    /// <see cref="JobTextForPage"/>.</summary>
    private static JsonObject MigrateAccountForPage(JsonElement e)
    {
        var cut = new JsonArray();
        foreach (var entry in ObjectsOf(e, "cut", MigrateIdsMax))
            cut.Add(new JsonObject { ["activity"] = WholeOf(entry, "activity"), ["conds"] = MigrateIds(entry, "conds") });
        var kept = new JsonObject();
        if (ObjectOf(e, "kept", out var classes))
            foreach (var f in classes.EnumerateObject())
                if (MigrateWord.IsMatch(f.Name) && kept.Count < MigrateIdsMax && WholeOf(classes, f.Name) is long n) kept[f.Name] = n;
        JsonObject? unknown = null;
        if (ObjectOf(e, "unknownIds", out var categories))
        {
            unknown = new JsonObject();
            foreach (var f in categories.EnumerateObject())
                if (MigrateWord.IsMatch(f.Name) && unknown.Count < MigrateIdsMax && MigrateIds(categories, f.Name) is { } ids) unknown[f.Name] = ids;
        }
        return new JsonObject
        {
            ["from"] = JobText(e, "from"),
            ["srcUid"] = WholeOf(e, "srcUid"),
            ["nickname"] = JobText(e, "nickname"),
            ["level"] = WholeOf(e, "level"),
            ["to"] = JobText(e, "to"),
            ["verdict"] = MigrateWordOf(e, "verdict"),
            ["why"] = MigrateWordOf(e, "why"),
            ["reason"] = PageTextOf(e, "reason"),
            ["existing"] = JobText(e, "existing"),
            ["existingUid"] = WholeOf(e, "existingUid"),
            ["existingNickname"] = JobText(e, "existingNickname"),
            ["existingLevel"] = WholeOf(e, "existingLevel"),
            ["caseOnly"] = FlagOf(e, "caseOnly"),
            ["dstUid"] = WholeOf(e, "dstUid"),
            ["wouldGetUid"] = WholeOf(e, "wouldGetUid"),
            ["keepsUid"] = FlagOf(e, "keepsUid"),
            ["relocated"] = ObjectOf(e, "relocated", out var moved) ? new JsonObject { ["from"] = WholeOf(moved, "from"), ["to"] = WholeOf(moved, "to") } : null,
            ["dropped"] = WholeOf(e, "dropped"),
            ["others"] = WholeOf(e, "others"),
            ["cut"] = cut,
            ["settled"] = MigrateIds(e, "settled"),
            ["dungeons"] = MigrateIds(e, "dungeons"),
            ["serverRemoves"] = ObjectOf(e, "serverRemoves", out var removes)
                ? new JsonObject { ["blocks"] = WholeOf(removes, "blocks"), ["scenes"] = WholeOf(removes, "scenes"), ["marks"] = WholeOf(removes, "marks") } : null,
            ["expiring"] = MigrateIds(e, "expiring"),
            ["expiryOther"] = MigrateIds(e, "expiryOther"),
            ["gachaUnknown"] = MigrateIds(e, "gachaUnknown"),
            ["kept"] = kept,
            ["unknownIds"] = unknown,
            ["password"] = JobText(e, "password") is "copied" or "notCopied" ? JobText(e, "password") : null,
            ["passwordWhy"] = PageTextOf(e, "passwordWhy"),
            ["passwordNeeded"] = FlagOf(e, "passwordNeeded") == true,
            ["backup"] = FlagOf(e, "backup") == true || JobText(e, "backup") is { Length: > 0 },
            ["online"] = FlagOf(e, "online"),
        };
    }

    /// <summary>An id list of a move's result, <c>{n, ids}</c>: the total and at most <see cref="MigrateIdsMax"/> ids, whole
    /// numbers. Null for a field absent or of another kind.</summary>
    private static JsonObject? MigrateIds(JsonElement e, string name)
    {
        if (!ObjectOf(e, name, out var list)) return null;
        var ids = new JsonArray();
        if (list.TryGetProperty("ids", out var all) && all.ValueKind == JsonValueKind.Array)
            foreach (var id in all.EnumerateArray())
                if (ids.Count < MigrateIdsMax && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long n)) ids.Add(JsonValue.Create(n));
        return new JsonObject { ["n"] = WholeOf(list, "n") ?? ids.Count, ["ids"] = ids };
    }

    /// <summary>A word of the agent's own vocabulary in a move's result (a class of kept values, a category of ids, a
    /// verdict, a skip's code): one short word.</summary>
    private static readonly System.Text.RegularExpressions.Regex MigrateWord = new(@"\A[A-Za-z][A-Za-z0-9_]{0,31}\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A version id in a move's result: numbers joined by dots, at most three — a path, a host or an address is
    /// none.</summary>
    private static readonly System.Text.RegularExpressions.Regex MigrateVersion = new(@"\A[0-9]{1,3}(?:\.[0-9]{1,3}){0,2}\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A field of a move's result that holds a <see cref="MigrateWord"/>, or null for any other text.</summary>
    private static string? MigrateWordOf(JsonElement e, string name) => JobText(e, name) is string word && MigrateWord.IsMatch(word) ? word : null;

    /// <summary>A field of a move's result that holds a <see cref="MigrateVersion"/>, or null for any other text.</summary>
    private static string? MigrateVersionOf(JsonElement e, string name) => JobText(e, name) is string id && MigrateVersion.IsMatch(id) ? id : null;

    /// <summary>A field of an agent answer that is an object, or false: absent, of another kind, or no object to read it
    /// from.</summary>
    private static bool ObjectOf(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    /// <summary>The objects of a list field of an agent answer, at most <paramref name="max"/>: an item of another kind is
    /// left out, a field of another kind gives none.</summary>
    private static IEnumerable<JsonElement> ObjectsOf(JsonElement e, string name, int max)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        int given = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (given++ == max) yield break;
            yield return item;
        }
    }

    /// <summary>A sentence of an agent answer as the page may see it (<see cref="JobTextForPage"/>), or null.</summary>
    private static string? PageTextOf(JsonElement e, string name) => JobText(e, name) is string text ? JobTextForPage(text) : null;

    /// <summary>One snapshot of a job. <paramref name="since"/> is the previous snapshot's "next",
    /// so only the log lines written after it come back.</summary>
    public Task<JsonElement> GetJobAsync(string id, int since = 0, CancellationToken ct = default)
        => GetAsync($"jobs/{Uri.EscapeDataString(id)}?since={since}", ct);

    /// <summary>The agent's last jobs (GET /jobs) as the page may see them: per job id, kind, version, state, error and
    /// elapsed, in the agent's order (oldest first). Never a job's result — an admin account creation's generated
    /// password lives there — nor its lines. A 404 (an agent without the route) reaches the caller as it is.</summary>
    public async Task<JsonElement> JobsAsync(CancellationToken ct = default) => JobsForPage(await GetAsync("jobs", ct));

    /// <summary>One job's lines (GET /jobs/&lt;id&gt;?since=0) as the page may see them: id, kind, version, state, elapsed and
    /// the lines the agent holds. Never its result. A 404 = a job the agent no longer has (it keeps its last 20 and forgets
    /// them when it restarts): the agent's own text.</summary>
    public async Task<JsonElement> JobLogAsync(string id, CancellationToken ct = default) => JobLogForPage(await GetJobAsync(id, 0, ct));

    /// <summary>The whitelist of JobsAsync: {jobs: [{id, kind, version, state, error, elapsed}]} — an entry without a string
    /// id left out, a field of another kind as null (elapsed as 0), an answer of another shape as an empty list, an error
    /// as <see cref="JobTextForPage"/> gives it. Pure, never throws.</summary>
    public static JsonElement JobsForPage(JsonElement answer)
    {
        var jobs = new JsonArray();
        if (answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("jobs", out var list)
            && list.ValueKind == JsonValueKind.Array)
            foreach (var job in list.EnumerateArray())
            {
                if (JobText(job, "id") is not string id) continue;
                jobs.Add(new JsonObject
                {
                    ["id"] = id,
                    ["kind"] = JobText(job, "kind"),
                    ["version"] = JobText(job, "version"),
                    ["state"] = JobText(job, "state"),
                    ["error"] = JobText(job, "error") is string error ? JobTextForPage(error) : null,
                    ["elapsed"] = JobSeconds(job),
                });
            }
        return JsonSerializer.SerializeToElement(new JsonObject { ["jobs"] = jobs });
    }

    /// <summary>The whitelist of JobLogAsync: {id, kind, version, state, elapsed, lines} — lines = the strings of the
    /// snapshot's <c>lines</c> as <see cref="JobTextForPage"/> gives them, anything else dropped. Pure, never
    /// throws.</summary>
    public static JsonElement JobLogForPage(JsonElement snapshot)
    {
        var lines = new JsonArray();
        if (snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("lines", out var all)
            && all.ValueKind == JsonValueKind.Array)
            foreach (var line in all.EnumerateArray())
                if (TextOf(line) is string text) lines.Add(JsonValue.Create(JobTextForPage(text)));
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["id"] = JobText(snapshot, "id"),
            ["kind"] = JobText(snapshot, "kind"),
            ["version"] = JobText(snapshot, "version"),
            ["state"] = JobText(snapshot, "state"),
            ["elapsed"] = JobSeconds(snapshot),
            ["lines"] = lines,
        });
    }

    /// <summary>A job's line or error as the page may see it: the agent's own text, its sentences about a backup and
    /// about a register page without what they can name on the box. The line about the backup of a save copied over
    /// (<c>Destination progress backup: &lt;file&gt;</c> — every copy of a save writes one: an account copy, a starting
    /// progress, an account created from a template, a move) and the one about the copy of the account database a move
    /// keeps (<c>Account database backup: &lt;file&gt;</c>) name the file alone, whether the agent wrote the file's name or
    /// its path on the box; whatever stands before that sentence stays — the agent begins every line it keeps with the
    /// time it wrote it (<c>12:00:00 </c>). The failure of a registration whose register page did not answer loses the
    /// page's address when it names one (<c>Could not reach the register page (&lt;address&gt;:&lt;port&gt;): …</c> — the
    /// job's error, and its <c>ERROR:</c> line); one that names none (<c>Could not reach the register page: …</c>) passes
    /// as it is. Every follow of a job (its lines, its error) and the Logs' projections pass through it, so neither that
    /// path nor that address reaches the page, an event or relic.log. Every other text is passed on as it is. Pure,
    /// never throws.</summary>
    public static string JobTextForPage(string text)
    {
        foreach (string line in BackupLines)
        {
            int at = text.IndexOf(line, StringComparison.Ordinal);
            if (at < 0) continue;
            int name = at + line.Length, cut = text.LastIndexOfAny(['/', '\\']);
            return cut < name ? text : text[..name] + text[(cut + 1)..];
        }
        return RegisterPageAt.Replace(text, "");
    }

    /// <summary>The agent's sentences about a backup it keeps, each up to the path it names.</summary>
    private static readonly string[] BackupLines = ["Destination progress backup: ", "Account database backup: "];

    /// <summary>The address the agent names the sdk's register page by when it did not answer: the <c>(&lt;address&gt;:&lt;port&gt;)</c>
    /// after <c>register page</c>.</summary>
    private static readonly System.Text.RegularExpressions.Regex RegisterPageAt = new(@"(?<=register page) \([^()\r\n]*:[0-9]{1,5}\)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A string field of a job snapshot or of another agent answer (<see cref="TextOf"/>), or null: the field
    /// is absent or of another kind, or the answer is no object.</summary>
    private static string? JobText(JsonElement job, string name)
        => job.ValueKind == JsonValueKind.Object && job.TryGetProperty(name, out var v) ? TextOf(v) : null;

    /// <summary>The text of a JSON string, or null for any other kind. A string that is no valid UTF-16 — the agent
    /// writes an undecodable byte of a file name as an escaped lone surrogate, <c>\udcXX</c>, which
    /// <see cref="JsonElement.GetString"/> refuses — is given as the agent wrote it, escapes included.</summary>
    private static string? TextOf(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String) return null;
        try { return v.GetString(); }
        catch (InvalidOperationException) { return v.GetRawText()[1..^1]; }
    }

    /// <summary>A job snapshot's <c>elapsed</c> (seconds, the agent rounds them to a tenth), or 0 for anything that is
    /// not a finite number — a number too large for a double reads as infinity.</summary>
    private static double JobSeconds(JsonElement job)
        => job.ValueKind == JsonValueKind.Object && job.TryGetProperty("elapsed", out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double s) && double.IsFinite(s) ? s : 0;

    /// <summary>Run a GM Talk command against the given version's server for player <paramref name="uid"/>.</summary>
    public Task<JsonElement> CommandAsync(string version, string uid, string msg, CancellationToken ct = default)
        => PostAsync("command", new { version, uid, msg }, ct);

    // ── agent admin endpoints (see docs/AGENT-API.md) ──

    /// <summary>The player-account policy (signup enabled/templates/maxPerDay per version, playerCommands).</summary>
    public Task<JsonElement> PolicyGetAsync(CancellationToken ct = default) => GetAsync("server/account/policy", ct);

    /// <summary>Merge a partial policy; returns the full policy.</summary>
    public Task<JsonElement> PolicySetAsync(object policy, CancellationToken ct = default)
        => PostAsync("server/account/policy", policy, ct);

    public Task<JsonElement> TemplatesAsync(string version, CancellationToken ct = default)
        => GetAsync($"server/templates?version={Uri.EscapeDataString(version)}", ct);

    /// <summary>(Re)import the template databases from the manifest — a job; the stack stays up.</summary>
    public Task<JsonElement> TemplatesEnsureAsync(string version, Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/templates/ensure", new { version }, onLine, ct);

    /// <summary>Current server secrets (root/flask/internal; the MUIP key only as a fingerprint).</summary>
    public Task<JsonElement> SecretsGetAsync(string version, CancellationToken ct = default)
        => GetAsync($"server/secrets?version={Uri.EscapeDataString(version)}", ct);

    /// <summary>Rotate secrets — a job; on a bootstrapped stack it restarts the server.</summary>
    public Task<JsonElement> SecretsSetAsync(string version, IDictionary<string, string> values,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["version"] = version };
        foreach (var kv in values) if (!string.IsNullOrEmpty(kv.Value)) body[kv.Key] = kv.Value;
        return RunJobAsync("server/secrets", body, onLine, ct);
    }

    /// <summary>Turn in-game password verification on/off — a job.</summary>
    public Task<JsonElement> AuthAsync(string version, bool verifyPassword, string? defaultPassword = null,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["version"] = version, ["verifyPassword"] = verifyPassword };
        if (!string.IsNullOrEmpty(defaultPassword)) body["defaultPassword"] = defaultPassword;
        return RunJobAsync("server/auth", body, onLine, ct);
    }

    /// <summary>Set an account's password (bcrypt computed on the box) — a job.</summary>
    public Task<JsonElement> AccountPasswordAsync(string version, string name, string password,
        Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/account/password", new { version, name, password }, onLine, ct);

    /// <summary>Create an in-game account on <paramref name="version"/> as the ADMIN — a job
    /// (<c>accountcreate</c>). Unlike the player signup it ignores the Player accounts policy and every
    /// quota, and <paramref name="template"/> may be any progress template imported on that stack
    /// (<c>fresh</c> = a new account with no progress copied). The name follows the signup's rules,
    /// reserved names included. <paramref name="password"/> goes out ONLY when non-empty: with
    /// password verification on, an absent one makes the server generate it and the job result carries
    /// it (<c>password</c>, <c>passwordGenerated</c>) — that result is the one place it ever travels,
    /// it is never in a job line. With verification off a typed password is stored all the same and not
    /// echoed.
    /// <para>The agent answers a bad name / template / password with a synchronous 400 before any job
    /// exists and never with a 404, so a 404 on the POST can only mean an older agent with no
    /// such route — reported as that. A 404 while FOLLOWING the job (the agent restarted and forgot
    /// it) keeps its own text. Returns the final snapshot <c>{state, result: {name, uid, template,
    /// nickname, passwordVerify, passwordGenerated, password}}</c>.</para></summary>
    public async Task<JsonElement> AccountCreateAsync(string version, string name, string? password,
        string template = "fresh", Action<string>? onLine = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["version"] = version, ["name"] = name, ["template"] = template };
        // Never "" or null on the wire: "no password" is the ABSENT key (the same shape the public signup
        // sends), so the body carries a password only when the admin actually typed one.
        if (!string.IsNullOrEmpty(password)) body["password"] = password;
        JsonElement started;
        try { started = await PostAsync("server/account/create", body, ct); }
        catch (AgentHttpException ex) when (ex.Status == 404)
        {
            throw new AgentHttpException(404, L.T("core.agent.accountCreateUnsupported"));
        }
        return await FollowJobAsync(started, onLine, ct);
    }

    // ── the account list (Server → Players) ──

    /// <summary>The largest page the agent hands out in one read of the account list.</summary>
    public const int AccountsLimitMax = 500;

    /// <summary>How long one read of the account list may take before it is given up: under the client's 60 s HTTP
    /// timeout, above the agent's own worst case — at most 5 s waiting for a read already under way, inside a 40 s
    /// budget for its docker calls counted from the request, plus the seconds one hung docker call takes to be
    /// killed.</summary>
    public static readonly TimeSpan AccountsTimeout = TimeSpan.FromSeconds(55);

    /// <summary>The relative URL of one read of the account list: <c>server/accounts?version=…</c>, then <c>after</c>
    /// (the <c>next</c> of the page before) when above 0, <c>limit</c> when above 0 — at most
    /// <see cref="AccountsLimitMax"/>; without it the agent hands out 100 — and <c>q</c>, trimmed, when not blank.
    /// Every value is percent-encoded with <see cref="Uri.EscapeDataString"/>: the agent reads the query as a form,
    /// where a bare '+' is a space. Pure.</summary>
    public static string AccountsQuery(string version, long after = 0, int limit = 0, string? q = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var url = new StringBuilder("server/accounts?version=").Append(Uri.EscapeDataString(version ?? ""));
        if (after > 0) url.Append("&after=").Append(after.ToString(inv));
        if (limit > 0) url.Append("&limit=").Append(Math.Min(limit, AccountsLimitMax).ToString(inv));
        string search = (q ?? "").Trim();
        if (search.Length > 0) url.Append("&q=").Append(Uri.EscapeDataString(search));
        return url.ToString();
    }

    /// <summary>One page of the accounts of <paramref name="version"/> (GET /server/accounts, <see cref="AccountsQuery"/>)
    /// as the page may see it (<see cref="AccountsForPage"/>): read-only on the box — the logins from a copy of the
    /// sdk's database, the characters and who is online only while the version runs — and never a job. The route
    /// answers a malformed query with a 400, a version without a stack here — or a read already under way past its
    /// short wait — with a 409, an unreadable account database with a 500, and never with a 404: a 404 is an agent
    /// without the route, reported as that. A read that takes longer than <see cref="AccountsTimeout"/> is given
    /// up with a text that says so. Nothing of the answer is logged.</summary>
    public async Task<JsonElement> AccountsAsync(string version, long after = 0, int limit = 0, string? q = null,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AccountsTimeout);
        JsonElement answer;
        try { answer = await GetAsync(AccountsQuery(version, after, limit, q), timeout.Token); }
        catch (AgentHttpException ex) when (ex.Status == 404)
        {
            throw new AgentHttpException(404, L.T("core.agent.accountsUnsupported"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(L.T("core.agent.accountsTimeout", new { seconds = (int)AccountsTimeout.TotalSeconds }));
        }
        return AccountsForPage(answer);
    }

    /// <summary>The whitelist of <see cref="AccountsAsync"/>: <c>{version, running, asOf, total, matched,
    /// withCharacter, online, characters, presence, reason, next, accounts}</c>, each row <c>{name, sdkUid, guest,
    /// created, lastLogin, uid, uidCreated, nickname, level, lastSave, online, presenceStale, origin}</c> — the fields
    /// the route documents and nothing else, so no other field the answer might carry reaches the page. A field
    /// absent or of another kind is null (<c>running</c> and a row's <c>guest</c> false); a row without a whole-number
    /// <c>sdkUid</c> is left out; an answer of another shape gives no rows. The texts are read through
    /// <see cref="TextOf"/>. Pure, never throws.</summary>
    public static JsonElement AccountsForPage(JsonElement answer)
    {
        var rows = new JsonArray();
        if (answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("accounts", out var list)
            && list.ValueKind == JsonValueKind.Array)
            foreach (var row in list.EnumerateArray())
            {
                if (WholeOf(row, "sdkUid") is not long sdkUid) continue;
                rows.Add(new JsonObject
                {
                    ["name"] = JobText(row, "name"),
                    ["sdkUid"] = sdkUid,
                    ["guest"] = FlagOf(row, "guest") == true,
                    ["created"] = JobText(row, "created"),
                    ["lastLogin"] = JobText(row, "lastLogin"),
                    ["uid"] = WholeOf(row, "uid"),
                    ["uidCreated"] = JobText(row, "uidCreated"),
                    ["nickname"] = JobText(row, "nickname"),
                    ["level"] = WholeOf(row, "level"),
                    ["lastSave"] = JobText(row, "lastSave"),
                    ["online"] = FlagOf(row, "online"),
                    ["presenceStale"] = FlagOf(row, "presenceStale"),
                    ["origin"] = JobText(row, "origin"),
                });
            }
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["version"] = JobText(answer, "version"),
            ["running"] = FlagOf(answer, "running") == true,
            ["asOf"] = JobText(answer, "asOf"),
            ["total"] = WholeOf(answer, "total"),
            ["matched"] = WholeOf(answer, "matched"),
            ["withCharacter"] = WholeOf(answer, "withCharacter"),
            ["online"] = WholeOf(answer, "online"),
            ["characters"] = JobText(answer, "characters"),
            ["presence"] = JobText(answer, "presence"),
            ["reason"] = JobText(answer, "reason"),
            ["next"] = WholeOf(answer, "next"),
            ["accounts"] = rows,
        });
    }

    /// <summary>A whole-number field of an agent answer, or null: absent, of another kind, a fraction, or too large
    /// for a long.</summary>
    private static long? WholeOf(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    /// <summary>A bool field of an agent answer, or null: absent or of another kind.</summary>
    private static bool? FlagOf(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    /// <summary>The official-hotfix mirror state of one version (available/enabled/pending/complete,
    /// mirror URL, output revisions, cached file counts).</summary>
    public Task<JsonElement> HotpatchGetAsync(string version, CancellationToken ct = default)
        => GetAsync($"server/hotpatch?version={Uri.EscapeDataString(version)}", ct);

    /// <summary>Enable/disable the official-hotfix mirror — a job: enabling downloads the manifest
    /// files onto the box and rewrites the dispatch advertisement (restarts the config-holding
    /// services when the stack is up); disabling reverts it, <paramref name="purge"/> also deletes
    /// the cached files.</summary>
    public Task<JsonElement> HotpatchSetAsync(string version, bool enabled, bool purge = false,
        Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/hotpatch", new { version, enabled, purge }, onLine, ct);

    /// <summary>Choose which VOICE languages the hotfix mirror serves and fetches — a job. Once real
    /// revisions are advertised the client checks every pack of each enabled voice language and fetches
    /// a missing unchanged one from the BASE output; the mirror serves and fetches those only for the languages
    /// selected here (names as the release index spells them: "Chinese", "English(US)", "Japanese",
    /// "Korean"; an empty list = none), a pack already on the box included: the packs of a language that is
    /// not selected stay on disk unserved until it is selected again (<paramref name="purge"/> deletes them), and
    /// nothing is served while the version's hotpatch is not enabled. It touches only the mirror folder and the agent state: never
    /// the stack, nothing is restarted, and it runs whether or not the hotpatch is enabled.
    /// <para>The job has TWO phases. PREPARE, under the agent's operation lock like every job: the
    /// release index is mirrored if it is missing, an unknown language is a 400, the free disk is
    /// checked (409 — its text says so when <paramref name="purge"/> alone would make the selection
    /// fit), the selection is saved (a state write that fails ends the job with 507 before any
    /// download) and <paramref name="purge"/> deletes the cached packs of every language NOT selected —
    /// per language and non-fatal: a language whose files are in use (Windows: a client is being served
    /// one) is named in the result's <c>purgeFailed</c> while the job goes on. DOWNLOAD, with the lock
    /// released: the missing packs are fetched onto the box — from the language's archive.org bundle when
    /// the manifest names one and the agent can use it (the mirror source, its switch, a tool, enough of the
    /// language missing), else file by file from the manifest's upstreams —, md5+size verified: 3.2–3.9 GB
    /// per language on 1.6, 7.3–9.4 GB on 2.8, 7.4–9.6 GB on 3.2. While it downloads, the watchdog's own
    /// jobs (revive, towerfix, netfix) and player signups start beside it instead of waiting; every ADMIN job
    /// (start/stop/provision/hotpatch …) is still refused with 409 until it ends.</para>
    /// <para>The download stops early — the job ends in error, so this call throws the agent's own
    /// sentence — with 409 when the free space no longer covers the next file ("Stopped before
    /// &lt;file&gt;: …") or when <see cref="HotpatchVoiceCancelAsync"/> asked for it ("Stopped on
    /// request: N of M files fetched in this run …"), 507 when writing to the mirror folder failed (a
    /// LOCAL disk failure: no retry, no other upstream), 502 when five files in a row could not be
    /// fetched (the upstream is not delivering). The selection was saved before the first byte, what was
    /// fetched stays and a cut transfer keeps its <c>.part</c>, so the same call run again continues
    /// where it stopped (Range) and never re-downloads a complete pack. A pack that is still missing
    /// is fetched when a player's client asks for it only while on-demand fetching is on (the agent's
    /// <c>GIO_HOTPATCH_UPSTREAM</c> and the policy's <c>hotpatchUpstream</c>: GET /server/hotpatch
    /// <c>upstream</c>) and the version's hotpatch is enabled — a fallback, not a plan: a version whose GET
    /// /server/hotpatch says <c>voice.shortWait</c> needs its packs mirrored by this job first, its game not
    /// waiting long enough for a big pack fetched on demand.</para>
    /// <para>Result: <c>{voice, fetched, bytes, purged, purgeFailed}</c>. Cancelling
    /// <paramref name="ct"/> only stops FOLLOWING the job; stopping the job itself is
    /// <see cref="HotpatchVoiceCancelAsync"/>. An agent without the route answers 404.</para></summary>
    public Task<JsonElement> HotpatchVoiceAsync(string version, IReadOnlyList<string> languages, bool purge = false,
        Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/hotpatch/voice", new { version, languages, purge }, onLine, ct);

    /// <summary>Ask the running voice-pack job of <paramref name="version"/> to stop — a
    /// plain POST of <c>{version, cancel: true}</c> to the job's own path, NOT a job: nothing to follow,
    /// it answers at once with <c>{ok, stopping}</c> (<c>stopping: false</c> = no voice job of that
    /// version is running, which is not an error). The job ends within about one chunk with its 409
    /// "Stopped on request …" — that is what whoever follows it receives; the file it was writing keeps
    /// its <c>.part</c> and the next <see cref="HotpatchVoiceAsync"/> resumes it by Range. Meant to be
    /// sent WHILE another client follows the job: it takes no agent lock and waits for nothing.</summary>
    public Task<JsonElement> HotpatchVoiceCancelAsync(string version, CancellationToken ct = default)
        => PostAsync("server/hotpatch/voice", new { version, cancel = true }, ct);

    /// <summary>Set an unreadable state file aside on the box (kept as
    /// <c>state.json.corrupt-&lt;ts&gt;</c>) and start the agent's state from empty. The agent refuses
    /// every state change while the file is unreadable; this is the admin's way out besides a hand
    /// repair. <c>confirm: true</c> is required by the agent, so a stray POST cannot reset anything.</summary>
    public Task<JsonElement> StateResetAsync(CancellationToken ct = default)
        => PostAsync("agent/state/reset", new { confirm = true }, ct);

    // ── the agent's own settings, its restart, and moving a stack folder ──

    /// <summary>The agent's editable settings (the Agent settings card) — every key of the
    /// registry with the value the running process uses, the file's value, the default, whether it applies
    /// live or after a restart and whether an environment variable overrides it; plus the config file, the
    /// restart mode, <c>started</c> (the process start epoch — see <see cref="WaitForRestartAsync"/>), the
    /// TLS facts and each version's stack folder. An older agent has no such route and answers
    /// 404 — the caller turns that into "update the agent".</summary>
    public Task<JsonElement> AgentConfigGetAsync(CancellationToken ct = default) => GetAsync("agent/config", ct);

    /// <summary>Write settings into the agent's config file — <c>{"set": {"GIO_KEY": "value"}}</c>,
    /// the keys verbatim (a dictionary: the Web defaults camelCase property names, never dictionary keys),
    /// every value a STRING ("" = drop the key's line, the default applies again). The agent validates the
    /// whole set first (400 naming the key, 409 for a key an environment variable overrides or a file it
    /// cannot write), writes, applies the live keys at once and answers the GET body plus
    /// <c>applied</c> / <c>restartRequired</c>. Not a job.</summary>
    public Task<JsonElement> AgentConfigSetAsync(IReadOnlyDictionary<string, string> set, CancellationToken ct = default)
        => PostAsync("agent/config", new { set }, ct);

    /// <summary>Ask the agent to restart itself (systemd's Restart=always on Linux, a respawn of
    /// its own command line on Windows) so the restart-kind settings take effect. Body <c>{}</c>; answers
    /// 202 <c>{ok, restarting, how}</c> BEFORE it goes down, 409 while any job runs or when it cannot
    /// restart itself. Whether it came back is <see cref="WaitForRestartAsync"/>'s question.</summary>
    public Task<JsonElement> AgentRestartAsync(CancellationToken ct = default)
        => PostAsync("agent/restart", new { }, ct);

    /// <summary>How long <see cref="WaitForRestartAsync"/> waits by default, its first poll and the pause between
    /// polls: systemd restarts the service 2 s after the exit, a Windows respawn needs the interpreter start
    /// plus up to ~10 s of bind retries while the old socket closes.</summary>
    public static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RestartFirstPoll = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan RestartPoll = TimeSpan.FromSeconds(1);

    /// <summary>From this long after the restart request on, an answer carrying the OLD <c>started</c> means the
    /// restart failed (<see cref="RestartOutcome.NotRestarted"/>) instead of "not yet". The old process closes its
    /// listening socket ~0.5–1 s after the 202 (the agent's <c>_restart_worker</c>) and answers every request on
    /// a closed connection (HTTP/1.0, no keep-alive), so past that only its fallback can answer with its own
    /// <c>started</c>: the new process could not be spawned, and the old one bound the port again and keeps
    /// serving.</summary>
    public static readonly TimeSpan RestartOldAnswerGrace = TimeSpan.FromSeconds(5);

    /// <summary>One poll never waits longer than this: a host that stopped answering altogether must cost
    /// the budget a bounded time per try, not the client's 60 s HTTP timeout — yet an agent that IS answering
    /// needs room: every GET agent/config runs <c>docker compose ls</c>, which takes seconds on a Docker
    /// Desktop still starting or a loaded box. A host that is down fails fast anyway (connection refused).</summary>
    private static readonly TimeSpan RestartPollTimeout = TimeSpan.FromSeconds(15);

    /// <summary><c>started</c> of an <c>agent/config</c> answer as its raw JSON text ("" when absent) — compared as
    /// text, never through a double, so the same epoch always reads the same.</summary>
    public static string StartedOf(JsonElement config)
        => config.ValueKind == JsonValueKind.Object && config.TryGetProperty("started", out var s)
            && s.ValueKind is JsonValueKind.Number or JsonValueKind.String ? s.GetRawText() : "";

    /// <summary>After <see cref="AgentRestartAsync"/>: poll <c>agent/config</c> (first after
    /// <paramref name="firstPoll"/>, then every <paramref name="poll"/>) until its <c>started</c> differs from
    /// <paramref name="startedBefore"/> — a new process answered: <see cref="RestartOutcome.Back"/>. While the old
    /// process closes and the new one binds, every failure (refused connection, timeout, a 5xx) only means "not
    /// yet"; the OLD process may also still answer with the old <c>started</c> for a moment. The same old
    /// <c>started</c> answered once <paramref name="oldAnswerGrace"/> (default <see cref="RestartOldAnswerGrace"/>)
    /// has passed is <see cref="RestartOutcome.NotRestarted"/> at once: the agent is up but could not start a new
    /// copy of itself, so the restart-kind settings are still pending — waiting out the budget would only end in
    /// a false "did not answer". Nothing that tells within <paramref name="timeout"/> =
    /// <see cref="RestartOutcome.NoAnswer"/>. All three are outcomes, not exceptions. A 401 or 404 counts as back:
    /// the old process accepted this token on this route seconds ago, so whatever refuses it now is a different
    /// process (a token or agent replaced by hand) — the next call surfaces that with its own error.</summary>
    public async Task<RestartOutcome> WaitForRestartAsync(string startedBefore, TimeSpan timeout,
        TimeSpan? firstPoll = null, TimeSpan? poll = null, TimeSpan? oldAnswerGrace = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var grace = oldAnswerGrace ?? RestartOldAnswerGrace;
        await Task.Delay(firstPoll ?? RestartFirstPoll, ct);
        while (true)
        {
            var left = timeout - sw.Elapsed;
            if (left <= TimeSpan.Zero) return RestartOutcome.NoAnswer;
            using (var one = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                one.CancelAfter(left < RestartPollTimeout ? left : RestartPollTimeout);
                try
                {
                    // The moment the poll went out, not when it came back: the grace is about which process
                    // could still hold the socket when the request reached it.
                    var sent = sw.Elapsed;
                    string now = StartedOf(await AgentConfigGetAsync(one.Token));
                    if (now.Length > 0 && now != startedBefore) return RestartOutcome.Back;
                    if (now.Length > 0 && sent >= grace) return RestartOutcome.NotRestarted;
                }
                catch (AgentHttpException ex) when (ex.Status is 401 or 404) { return RestartOutcome.Back; }
                catch (Exception) when (!ct.IsCancellationRequested) { /* not back yet */ }
            }
            if (sw.Elapsed >= timeout) return RestartOutcome.NoAnswer;
            await Task.Delay(poll ?? RestartPoll, ct);
        }
    }

    /// <summary>Dry run of <see cref="RelocateAsync"/> — changes nothing, answers at once with what the
    /// move would meet: <c>{version, mode, from, to, target: absent|empty|stack|other, running, sameVolume,
    /// stackBytes, freeBytes, needBytes, ok, problem}</c> (<c>problem</c> = the refusal the real call would give).
    /// An agent that reports them adds <c>carry</c> — for a repoint away from a folder that holds no server, what
    /// a download left beside it and the repoint takes along, <c>[{name, size, fits}]</c> — and <c>download</c> —
    /// for a repoint to an absent or empty folder, a download into that folder once the version points there,
    /// <c>{archive, size, extractedSize, reserve, need, free, tool, problem}</c>; neither refuses a repoint. The
    /// answer reaches the page as the agent gave it.</summary>
    public Task<JsonElement> RelocateCheckAsync(string version, string dir, string mode, CancellationToken ct = default)
        => PostAsync("server/relocate", new { version, dir, mode, check = true }, ct);

    /// <summary>Change the stack folder of <paramref name="version"/> — a job (<c>relocate</c>).
    /// <paramref name="mode"/> <c>move</c> moves the files (a rename on the same volume, a verified copy +
    /// delete across volumes) and <c>repoint</c> only points the agent at <paramref name="dir"/> (files
    /// untouched; <c>""</c> takes the version off this agent). A running stack is stopped first and started
    /// again at the end; the new folder is written into the agent's config before anything old is deleted.
    /// Result <c>{version, from, to, mode, moved, crossVolume, bytes, restarted}</c>. A cross-volume copy
    /// stops on <see cref="RelocateCancelAsync"/> with the agent's 409 "Stopped on request", the old folder
    /// and the config untouched.</summary>
    public Task<JsonElement> RelocateAsync(string version, string dir, string mode,
        Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/relocate", new { version, dir, mode }, onLine, ct);

    /// <summary>Ask the running relocation of <paramref name="version"/> to stop — a plain POST of
    /// <c>{version, cancel: true}</c> to the job's own path, NOT a job, answered at once with
    /// <c>{ok, stopping}</c>. Like the fetch cancel it takes no agent lock and is meant to be sent WHILE
    /// another client follows the job.</summary>
    public Task<JsonElement> RelocateCancelAsync(string version, CancellationToken ct = default)
        => PostAsync("server/relocate", new { version, cancel = true }, ct);

    // ── gameplay settings (the Game rules tab) ──

    /// <summary>The gameplay settings of one version: the stored document (<c>tweaks</c>), the one the
    /// files on the box correspond to (<c>active</c>), whether a change waits for the next Start
    /// (<c>pending</c>) or was refused by the game server (<c>refused</c>), which sections can be edited on
    /// that stack (<c>sections</c>: per card the files it cannot use and why, the files taken over from
    /// changes made outside Relic, the settings that cannot be applied, the files a Revert or a Reset of the
    /// card acts on), the key of the files taken over (<c>takenKey</c>, sent back with every write), the
    /// <c>limits</c>, and the values the page edits against — what each file's base holds (the server's
    /// original, or a file changed outside Relic that the agent took over), with the original game's value
    /// beside it where the two differ: the resin row, the Abyss rotations with their floors and chambers, the
    /// domains, the quest rewards. The route answers 400 for an unknown version and never 404, so a 404 is
    /// an agent without the route — the caller turns that into "update the agent".</summary>
    public Task<JsonElement> TweaksGetAsync(string version, CancellationToken ct = default)
        => GetAsync($"server/tweaks?version={Uri.EscapeDataString(version)}", ct);

    /// <summary>Store and apply the gameplay settings of <paramref name="version"/> — a job
    /// (<c>tweaks</c>). <paramref name="tweaks"/> is the whole document as the page built it (every section
    /// and key optional, an absent key = the value the file's base holds, <c>{}</c> = every setting taken
    /// out) and goes out exactly as given: the agent is the one validator, so nothing here reads, sorts or
    /// drops a key of it. Only its kind is checked — the agent reads an empty document as "no setting at
    /// all", so a value that is no document at all (null, a list) never leaves this PC.
    /// <paramref name="takenKey"/> is the <c>takenKey</c> of the answer the page built the document on
    /// (<c>""</c> when it named none): the agent refuses the job with a 409 when a data file was changed
    /// outside Relic since that answer, so a document never lands on a file it was not built for. It always
    /// goes out — an agent that knows nothing of it leaves it unread.
    /// <para>The agent answers a document its catalogue does not allow with a synchronous 400 that names
    /// the path, before any job exists — what only the stack's own files can decide (an extra item a
    /// domain's reward already holds, more extra items than its reward row has room for) ends the JOB in
    /// error with the same kind of text — and never with a 404, so a 404 on the POST can only mean an agent
    /// without the route — reported as that. A 404 while FOLLOWING the job (the agent restarted and forgot it)
    /// keeps its own text. A running game server checks the new files at once: when it refuses them the
    /// job ends in error with the agent's own sentence and the previous files are back in place. Returns
    /// the final snapshot <c>{state, result: {version, changed, effect, clamped, blocked?}}</c>: the files
    /// rewritten, <c>live</c> / <c>next-start</c> / <c>none</c>, the reward amounts a server cap limited and
    /// the settings that could not be applied (a file they need cannot be used).</para></summary>
    public async Task<JsonElement> TweaksSetAsync(string version, JsonElement tweaks, string takenKey,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (tweaks.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("the tweaks document must be a JSON object", nameof(tweaks));
        JsonElement started;
        try { started = await PostAsync("server/tweaks", new { version, tweaks, takenKey = takenKey ?? "" }, ct); }
        catch (AgentHttpException ex) when (ex.Status == 404)
        {
            throw new AgentHttpException(404, L.T("core.agent.tweaksUnsupported"));
        }
        return await FollowJobAsync(started, onLine, ct);
    }

    /// <summary>Put the data files of the gameplay settings' <paramref name="cards"/> back to the settings Relic
    /// had in effect before the changes made outside Relic — a job (<c>tweaks</c>), POST
    /// <c>{version, revert: [cards], takenKey}</c>. At once, whether the game server runs or not: the agent keeps
    /// a copy of every file changed outside Relic, puts the original game's file in place of each file of the
    /// cards it acts on — a file taken over, one changed outside Relic and not taken over, a missing one (the
    /// drop table belongs to the Domain card and to the Quest card alike) —, then writes the settings on top
    /// like an Apply — judged by a running game server, else at its next Start. The cards are the
    /// document's sections (<c>resin</c>, <c>abyss</c>, <c>domains</c>, <c>quests</c>), at least one, none twice:
    /// another list never leaves this PC. <paramref name="takenKey"/> as for <see cref="TweaksSetAsync"/>.
    /// <para>An agent that cannot do it answers with a 404 (no such route) or a 400 (a route that only takes a
    /// document): both come back as the "update the agent" text, nothing followed.</para></summary>
    public Task<JsonElement> TweaksRevertAsync(string version, IReadOnlyList<string> cards, string takenKey,
        Action<string>? onLine = null, CancellationToken ct = default)
        => TweaksFilesAsync(version, "revert", cards, takenKey, onLine, ct);

    /// <summary>Put the data files of the gameplay settings' <paramref name="cards"/> back to the original
    /// game and take those cards' settings out — a job (<c>tweaks</c>), POST
    /// <c>{version, reset: [cards], takenKey}</c>. At once, like <see cref="TweaksRevertAsync"/> (a copy of every
    /// file changed outside Relic kept, the original in place of each file of the cards that is not the original
    /// as it stands or that a setting of theirs writes); the settings of the other cards that live in a file of
    /// theirs (the drop table) are written on it again. The same checks and the same "update the agent" text for
    /// an agent that cannot do it.</summary>
    public Task<JsonElement> TweaksResetAsync(string version, IReadOnlyList<string> cards, string takenKey,
        Action<string>? onLine = null, CancellationToken ct = default)
        => TweaksFilesAsync(version, "reset", cards, takenKey, onLine, ct);

    /// <summary>The request of a Revert (<paramref name="op"/> <c>revert</c>) or a Reset (<c>reset</c>). The cards go
    /// out as a list of at least one name, none empty, none twice — the agent refuses anything else with a 400,
    /// which an agent that knows no such request answers too, so the two are never left to be told apart by
    /// guessing: another list is refused here, before any request.</summary>
    private async Task<JsonElement> TweaksFilesAsync(string version, string op, IReadOnlyList<string> cards,
        string takenKey, Action<string>? onLine, CancellationToken ct)
    {
        if (cards is null || cards.Count == 0 || cards.Any(string.IsNullOrEmpty)
            || cards.Distinct(StringComparer.Ordinal).Count() != cards.Count)
            throw new ArgumentException("the cards must be a list of distinct card names, at least one", nameof(cards));
        string[] list = cards.ToArray();
        object body = op == "revert" ? new { version, revert = list, takenKey = takenKey ?? "" }
            : new { version, reset = list, takenKey = takenKey ?? "" };
        JsonElement started;
        try { started = await PostAsync("server/tweaks", body, ct); }
        catch (AgentHttpException ex) when (ex.Status is 400 or 404)
        {
            throw new AgentHttpException(ex.Status, L.T("core.agent.tweaksFilesUnsupported"));
        }
        return await FollowJobAsync(started, onLine, ct);
    }

    /// <summary>Ask the gameplay-settings waits of the running job of <paramref name="version"/> to end —
    /// a <c>tweaks</c> job, or the last step of a start / provision / setup / relocate, the step that hands
    /// the stored settings to the game server. A plain POST of <c>{version, cancel: true}</c> to the route's
    /// own path, NOT a job: answered at once with <c>{ok, stopping}</c> (<c>false</c> = no such job of that
    /// version runs). Takes no agent lock — meant to be sent WHILE this client follows the job, which then
    /// ends as the agent says: before a write nothing is written, while the game server judges new files the
    /// previous ones go back, after its verdict the other services are not asked. The body never carries a
    /// document: an agent whose route only applies documents would read one as settings to apply.
    /// <para>An agent that cannot stop those waits answers with a 404 (no such route) or a 400 (a route that
    /// wants a document); both come back as the "agent must be updated" text, the status kept.</para></summary>
    public async Task<JsonElement> TweaksStopAsync(string version, CancellationToken ct = default)
    {
        try { return await PostAsync("server/tweaks", new { version, cancel = true }, ct); }
        catch (AgentHttpException ex) when (ex.Status is 400 or 404)
        {
            throw new AgentHttpException(ex.Status, L.T("core.agent.tweaksStopUnsupported"));
        }
    }

    /// <summary>How often a running job is polled. Each poll asks only for the lines after the last
    /// "next", so the request stays cheap however long the job's log grows.</summary>
    private static readonly TimeSpan JobPoll = TimeSpan.FromSeconds(1);

    /// <summary>How many CONSECUTIVE failed polls the follow survives. A first-time setup polls for
    /// the better part of an hour, so a Wi-Fi roam, a dropped SSH forward, a restarted agent or one
    /// 60 s HTTP timeout must not be reported as a failed operation — the job keeps running on the
    /// box whatever happens to our connection.</summary>
    private const int JobPollRetries = 10;

    /// <summary>
    /// Start a long agent operation and follow its job to the end: every new log line goes to
    /// <paramref name="onLine"/>, and a job that ends in "error" throws with the agent's own
    /// message. Returns the final snapshot.
    /// The whole job runs on THIS client, so the POST and every poll share one connection — in ssh
    /// mode a per-poll client would rebuild the SSH tunnel once a second for a 20-minute bootstrap.
    /// Cancelling only stops us FOLLOWING the job; the agent keeps running it to completion.
    /// </summary>
    private async Task<JsonElement> RunJobAsync(string path, object body, Action<string>? onLine, CancellationToken ct)
        => await FollowJobAsync(await PostAsync(path, body, ct), onLine, ct);

    /// <summary>The follow half of <see cref="RunJobAsync"/>, given the POST's reply — split out so a caller
    /// can read the POST's own failure differently from a failure while following (a 404 on the POST of a
    /// route the agent does not have vs. a job the agent forgot, see <see cref="AccountCreateAsync"/>).</summary>
    private async Task<JsonElement> FollowJobAsync(JsonElement started, Action<string>? onLine, CancellationToken ct)
    {
        // An agent that does the work inside the POST never reports a job id.
        if (!started.TryGetProperty("job", out var jobId) || jobId.ValueKind != JsonValueKind.String)
            return started;
        string id = jobId.GetString()!;
        int since = 0, failures = 0;
        while (true)
        {
            JsonElement snap;
            try
            {
                snap = await GetJobAsync(id, since, ct);
                failures = 0;
            }
            // 404 = the agent no longer knows this job (it restarted); polling again cannot bring it
            // back, so its own message goes to the user. 401 = the bearer is refused (an open key the
            // admin revoked, a token rotated on the box): never transient, so it reaches the caller at
            // once — the launcher reads a refused open key as the return to Player mode, which the "job
            // link lost" text ten polls later would hide. Anything else is the connection, not the job.
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not AgentHttpException { Status: 404 or 401 })
            {
                if (++failures > JobPollRetries)
                    throw new InvalidOperationException(
                        L.T("core.agent.jobLinkLost", new { error = ex.Message }), ex);
                await Task.Delay(JobPoll, ct);
                continue;
            }
            // The lines and the error are read through TextOf: a text the agent could not decode (an undecodable
            // byte of a file name, an escaped lone surrogate in its JSON) is still text — passed on as the agent
            // wrote it — and never turns a job that runs on into a failed one. Both go on as JobTextForPage gives
            // them: the backup of a save copied over by its file name, a register page without its address.
            if (onLine is not null && snap.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                foreach (var line in lines.EnumerateArray())
                    onLine(JobTextForPage(TextOf(line) ?? ""));
            if (snap.TryGetProperty("next", out var next) && next.TryGetInt32(out int n)) since = n;
            string state = snap.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
            if (state == "error")
                throw new InvalidOperationException(snap.TryGetProperty("error", out var err) && TextOf(err) is string why
                    ? JobTextForPage(why) : L.T("core.agent.jobFailed"));
            if (state != "running") return snap;
            await Task.Delay(JobPoll, ct);
        }
    }

    /// <summary>Web defaults (camelCase) so the property names keep matching the agent's fields
    /// verbatim — `version`, `txtFixes`, `force`, `action`, `uid`, `msg`.</summary>
    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web);

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
        => await ReadJson(await Http.GetAsync(path, ct), ct);

    /// <summary>
    /// The body is serialized up front and sent as a COUNTED <see cref="StringContent"/> — never
    /// <c>PostAsJsonAsync</c>. JsonContent streams and cannot report its length, so HttpClient falls
    /// back to <c>Transfer-Encoding: chunked</c>, and the agent's Python <c>http.server</c> reads
    /// only Content-Length: it never dechunks, so an agent that does not dechunk by itself sees an
    /// EMPTY body and answers every command with "400 missing/invalid field: 'version'". The agent
    /// dechunks, but keep sending a counted body — it is what lets the app drive an older agent
    /// that does not, without a redeploy.
    /// </summary>
    private async Task<JsonElement> PostAsync(string path, object body, CancellationToken ct)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(body, BodyJson), Encoding.UTF8, "application/json");
        return await ReadJson(await Http.PostAsync(path, content, ct), ct);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp, CancellationToken ct)
    {
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Surface agent failures instead of returning them as a fake-OK payload. The body may
            // not be JSON (proxy/firewall interception), so fall back to the raw text. The agent's own
            // sentence is read through TextOf: one that names an undecodable byte keeps its status.
            string detail = text.Length > 300 ? text[..300] : text;
            try
            {
                using var err = JsonDocument.Parse(text);
                if (err.RootElement.ValueKind == JsonValueKind.Object &&
                    err.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    detail = TextOf(e) ?? detail;
            }
            catch (JsonException) { /* keep raw text */ }
            throw new AgentHttpException((int)resp.StatusCode,
                L.T("core.agent.httpError", new { status = (int)resp.StatusCode, detail }));
        }
        // A 200 with a non-JSON body is the same interception the error path anticipates (captive
        // portal / a different service on the port), more likely over a LAN. Surface it as a
        // friendly error instead of leaking raw parser internals into the UI toast.
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            string detail = text.Length > 300 ? text[..300] : text;
            throw new InvalidOperationException(
                L.T("core.agent.badResponse", new { detail }));
        }
    }

    private HttpClient Http => _http ?? throw new InvalidOperationException("AgentClient is not connected");

    public ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_forward is not null) { try { _forward.Stop(); } catch { /* ignore */ } _forward.Dispose(); }
        if (_ssh is not null) { try { _ssh.Disconnect(); } catch { /* ignore */ } _ssh.Dispose(); }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A reply from the agent that was not a success status. The code is carried alongside the message
/// because the job follow loop has to tell a job the agent has forgotten (404) from a connection
/// failure worth retrying. Still an <see cref="InvalidOperationException"/> so every caller that only
/// forwards Message keeps working.
/// </summary>
public sealed class AgentHttpException : InvalidOperationException
{
    public AgentHttpException(int status, string message) : base(message) => Status = status;

    public int Status { get; }
}

/// <summary>One request of POST /server/account/copy (<see cref="AgentClient.AccountCopyAsync(AccountCopyRequest, Action{string}, CancellationToken)"/>):
/// ONE source — the account <see cref="From"/> (a login name; the route takes digits alone that name no account for
/// a game uid), the game uid <see cref="FromUid"/> (the row of a guest, or of a game uid no login names) or the
/// progress template <see cref="FromTemplate"/> (an id the version's manifest ships) —, ONE
/// target — the account <see cref="To"/> (created when missing, under a name a signup may take, never digits alone)
/// or the game uid <see cref="ToUid"/> — and <see cref="DryRun"/>. A blank name or template is one not given.</summary>
public sealed record AccountCopyRequest(string Version, string? From = null, long? FromUid = null,
    string? FromTemplate = null, string? To = null, long? ToUid = null, bool DryRun = false)
{
    /// <summary>The largest game uid: t_player_uid.uid is an unsigned 32-bit column.</summary>
    public const long UidMax = 4294967295;

    /// <summary>The plain request <c>{version, from, to}</c> — two accounts by name, no dry run: the one every agent
    /// reads the same way.</summary>
    public bool IsPlain => Problem is null && Given(From) && Given(To) && !DryRun;

    /// <summary>Why the request cannot be sent — a short English reason — or null: no version, not exactly one source
    /// and one target, or a game uid outside 1 to <see cref="UidMax"/>.</summary>
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Version)) return "the version is missing";
            int sources = (Given(From) ? 1 : 0) + (FromUid is null ? 0 : 1) + (Given(FromTemplate) ? 1 : 0);
            int targets = (Given(To) ? 1 : 0) + (ToUid is null ? 0 : 1);
            if (sources != 1) return "a copy names exactly one source: an account, a game uid or a progress template";
            if (targets != 1) return "a copy names exactly one target: an account or a game uid";
            if (FromUid is < 1 or > UidMax || ToUid is < 1 or > UidMax)
                return $"a game uid is a whole number from 1 to {UidMax}";
            return null;
        }
    }

    /// <summary>The body as the agent reads it: <c>version</c>, then the source's key (<c>from</c>, <c>fromUid</c> or
    /// <c>fromTemplate</c>), the target's (<c>to</c> or <c>toUid</c>) and <c>dryRun: true</c> only for a dry run. A key
    /// not given is left out — the route counts <c>""</c> and <c>0</c> as given —, a name and a template go out
    /// trimmed, a uid as a JSON number. An <see cref="ArgumentException"/> for a request with a
    /// <see cref="Problem"/>.</summary>
    public Dictionary<string, object?> Body()
    {
        if (Problem is string why) throw new ArgumentException(why);
        var body = new Dictionary<string, object?> { ["version"] = Version };
        if (Given(From)) body["from"] = From!.Trim();
        else if (FromUid is long fromUid) body["fromUid"] = fromUid;
        else body["fromTemplate"] = FromTemplate!.Trim();
        if (Given(To)) body["to"] = To!.Trim();
        else body["toUid"] = ToUid!.Value;
        if (DryRun) body["dryRun"] = true;
        return body;
    }

    private static bool Given(string? text) => !string.IsNullOrWhiteSpace(text);
}

/// <summary>One account of a move: the login <see cref="From"/> on the source, exactly as it is written there; the target
/// login <see cref="To"/> (null = the same login); <see cref="Overwrite"/> = write over the existing target account whose
/// login is exactly that one, instead of creating an account.</summary>
public sealed record MigrateAccount(string From, string? To = null, bool Overwrite = false);

/// <summary>One request of POST /server/migrate (<see cref="AgentClient.MigrateAsync"/>): the running target
/// <see cref="Version"/>, ONE source — the configured version <see cref="SourceVersion"/> or the folder
/// <see cref="SourceDir"/> on the agent's machine —, the accounts it moves (null = in a check only: every named account of
/// the source), <see cref="DryRun"/> = a check, and the two options, which concern the accounts a move creates:
/// <see cref="KeepPassword"/> (the password it had on the source) and <see cref="KeepUid"/> (the game uid it had, where
/// that uid is free on the target).</summary>
public sealed record MigrateRequest(string Version, string? SourceVersion = null, string? SourceDir = null,
    IReadOnlyList<MigrateAccount>? Accounts = null, bool DryRun = false, bool KeepPassword = false, bool KeepUid = false)
{
    /// <summary>How many accounts one request may name, and the longest login it may name (the agent's own limits).</summary>
    public const int AccountsMax = 100, NameMax = 128;

    /// <summary>Why the request cannot be sent — a short English reason — or null: no version, not exactly one source, a
    /// real run that names no accounts, a list of accounts empty or too long, a name that is no one line of plain text, or
    /// two accounts of one login or going to one name (in any letter case). What depends on the box — a folder, a name a
    /// signup may take — is the agent's to judge.</summary>
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Version)) return "the version is missing";
            if (Given(SourceVersion) == Given(SourceDir)) return "a move names exactly one source: a version or a folder";
            if (Accounts is null) return DryRun ? null : "a move names the accounts it moves";
            if (Accounts.Count is < 1 or > AccountsMax) return $"a move names 1 to {AccountsMax} accounts";
            var froms = new HashSet<string>(StringComparer.Ordinal);
            var tos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in Accounts)
            {
                if (!IsName(account.From) || (account.To is not null && !IsName(account.To)))
                    return $"an account name is one line of plain text, at most {NameMax} characters";
                if (!froms.Add(account.From)) return "two accounts of a move name the same login";
                if (!tos.Add(account.To ?? account.From)) return "two accounts of a move go to the same name";
            }
            return null;
        }
    }

    /// <summary>The body as the agent reads it: <c>version</c>, <c>source</c> (<c>{version}</c> or <c>{dir}</c>),
    /// <c>accounts</c> when the request names them (each <c>from</c> exactly as given, <c>to</c> when given and
    /// <c>overwrite</c> only when true), and <c>dryRun</c>, <c>keepPassword</c> and <c>keepUid</c> only when true. An
    /// <see cref="ArgumentException"/> for a request with a <see cref="Problem"/>.</summary>
    public Dictionary<string, object?> Body()
    {
        if (Problem is string why) throw new ArgumentException(why);
        var body = new Dictionary<string, object?>
        {
            ["version"] = Version,
            ["source"] = Given(SourceVersion)
                ? new Dictionary<string, string> { ["version"] = SourceVersion!.Trim() }
                : new Dictionary<string, string> { ["dir"] = SourceDir!.Trim() },
        };
        if (Accounts is not null)
            body["accounts"] = Accounts.Select(account =>
            {
                var item = new Dictionary<string, object?> { ["from"] = account.From };
                if (account.To is not null) item["to"] = account.To;
                if (account.Overwrite) item["overwrite"] = true;
                return item;
            }).ToList();
        if (DryRun) body["dryRun"] = true;
        if (KeepPassword) body["keepPassword"] = true;
        if (KeepUid) body["keepUid"] = true;
        return body;
    }

    private static bool Given(string? text) => !string.IsNullOrWhiteSpace(text);

    private static bool IsName(string text) => !string.IsNullOrWhiteSpace(text) && text.Length <= NameMax
        && !text.Any(c => char.IsControl(c)
        || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator);
}

/// <summary>How <see cref="AgentClient.WaitForRestartAsync"/> ended.</summary>
public enum RestartOutcome
{
    /// <summary>A new agent process answered (another <c>started</c>, or a 401/404 from a replaced one).</summary>
    Back,
    /// <summary>The agent answers, still with its old <c>started</c>, after the grace: it could not start a new copy
    /// of itself (its log names why) and keeps serving — the restart-kind settings did not apply.</summary>
    NotRestarted,
    /// <summary>No answer that tells either way within the budget — the agent may be slow, or need a look on the box.</summary>
    NoAnswer,
}
