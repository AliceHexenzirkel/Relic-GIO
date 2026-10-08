using System.Text;
using System.Text.Json;
using Relic.Core.Util;
using Renci.SshNet;

namespace Relic.Core.Server;

/// <summary>
/// Talks to the GIO agent on the server box, authenticating every call with a bearer token.
/// "direct" mode (normal users): plain HTTP straight to Host:AgentPort — the agent binds the LAN
/// and the token (baked into the build) is the only credential the user's machine holds.
/// "ssh" mode (advanced): the agent binds 127.0.0.1 only and is reached through an SSH
/// connection + local port-forward, so nothing is exposed to the network.
/// </summary>
public sealed class AgentClient : IAsyncDisposable
{
    private readonly ServerConfig _cfg;
    private SshClient? _ssh;
    private ForwardedPortLocal? _forward;
    private HttpClient? _http;

    public AgentClient(ServerConfig cfg) => _cfg = cfg;

    public bool IsConnected => _http is not null && (_cfg.IsDirect || _ssh?.IsConnected == true);

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (_cfg.IsDirect)
        {
            _http = MakeHttp(new Uri($"http://{_cfg.Host}:{_cfg.AgentPort}/"));
            return Task.CompletedTask;
        }
        return Task.Run(() =>
        {
            var conn = new ConnectionInfo(_cfg.Host, _cfg.Port, _cfg.User,
                new PasswordAuthenticationMethod(_cfg.User, _cfg.Password))
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            _ssh = new SshClient(conn);
            _ssh.Connect();

            // local ephemeral port -> agent's 127.0.0.1:AgentPort on the server
            _forward = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)_cfg.AgentPort);
            _ssh.AddForwardedPort(_forward);
            _forward.Start();

            _http = MakeHttp(new Uri($"http://127.0.0.1:{_forward.BoundPort}/"));
        }, ct);
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
    /// service off for every caller that simply did not ask.</para></summary>
    public Task<JsonElement> SetupServerAsync(string version, string? txtFixes = null, bool force = false,
        string progress = "default", bool? pathfinding = null,
        Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/setup", new { version, txtFixes, force, progress, pathfinding }, onLine, ct);

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

    /// <summary>Copy one account's whole progress onto another (created on the box if missing).
    /// Runs with the stack up — both players just have to be OFFLINE; the agent rewrites the
    /// (uid&lt;&lt;32)|seq guids inside the save blob (naively copied rows collide in co-op), backs the
    /// target's rows up on the box, and never touches redis/t_player_uid/sdk.db beyond allocation.</summary>
    public Task<JsonElement> AccountCopyAsync(string version, string from, string to,
        Action<string>? onLine = null, CancellationToken ct = default)
        => RunJobAsync("server/account/copy", new { version, from, to }, onLine, ct);

    /// <summary>One snapshot of a job. <paramref name="since"/> is the previous snapshot's "next",
    /// so only the log lines written after it come back.</summary>
    public Task<JsonElement> GetJobAsync(string id, int since = 0, CancellationToken ct = default)
        => GetAsync($"jobs/{Uri.EscapeDataString(id)}?since={since}", ct);

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

    /// <summary>Choose which VOICE languages the hotfix mirror serves — a job. Once real
    /// revisions are advertised the client checks every pack of each enabled voice language and fetches
    /// a missing unchanged one from the BASE output; the mirror serves those only for the languages
    /// selected here (names as the release index spells them: "Chinese", "English(US)", "Japanese",
    /// "Korean"; an empty list = serve none). It touches only the mirror folder and the agent state:
    /// never the stack, nothing is restarted, and it works whether or not the hotpatch is enabled.
    /// <para>The job has TWO phases. PREPARE, under the agent's operation lock like every job: the
    /// release index is mirrored if it is missing, an unknown language is a 400, the free disk is
    /// checked (409 — its text says so when <paramref name="purge"/> alone would make the selection
    /// fit), the selection is saved (a state write that fails ends the job with 507 before any
    /// download) and <paramref name="purge"/> deletes the cached packs of every language NOT selected —
    /// per language and non-fatal: a language whose files are in use (Windows: a client is being served
    /// one) is named in the result's <c>purgeFailed</c> while the job goes on. DOWNLOAD, with the lock
    /// released: the missing packs are fetched onto the box, md5+size verified — 3.2–3.9 GB per
    /// language on 1.6, 7.3–9.4 GB on 2.8. While it downloads, the watchdog's own jobs (revive,
    /// towerfix, netfix) and player signups start beside it instead of waiting; every ADMIN job
    /// (start/stop/provision/hotpatch …) is still refused with 409 until it ends.</para>
    /// <para>The download stops early — the job ends in error, so this call throws the agent's own
    /// sentence — with 409 when the free space no longer covers the next file ("Stopped before
    /// &lt;file&gt;: …") or when <see cref="HotpatchVoiceCancelAsync"/> asked for it ("Stopped on
    /// request: N of M files fetched in this run …"), 507 when writing to the mirror folder failed (a
    /// LOCAL disk failure: no retry, no other upstream), 502 when five files in a row could not be
    /// fetched (the CDN is not delivering). The selection was saved before the first byte, what was
    /// fetched stays and a cut transfer keeps its <c>.part</c>, so the same call run again continues
    /// where it stopped (Range) and never re-downloads a complete pack. A pack that is still missing
    /// is fetched when a player's client asks for it — dependable on 1.6 only: the 2.8 downloader
    /// waits 30 s per GET and gives up after about three minutes of tries, so on 2.8 the packs have
    /// to be mirrored by this job first.</para>
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
    /// stackBytes, freeBytes, needBytes, ok, problem}</c> (<c>problem</c> = the refusal the real call would give).</summary>
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

    // ── gameplay settings (the Gameplay page) ──

    /// <summary>The gameplay settings of one version: the stored document (<c>tweaks</c>), the one the
    /// files on the box correspond to (<c>active</c>), whether a change waits for the next Start
    /// (<c>pending</c>) or was refused by the game server (<c>refused</c>), which sections can be edited on
    /// that stack (<c>sections</c>), the <c>limits</c>, and the server's own original values the page edits
    /// against — the resin row, the Abyss rotations with their floors and chambers, the domains, the
    /// quest rewards. The route answers 400 for an unknown version and never 404, so a 404 is an agent
    /// without the route — the caller turns that into "update the agent".</summary>
    public Task<JsonElement> TweaksGetAsync(string version, CancellationToken ct = default)
        => GetAsync($"server/tweaks?version={Uri.EscapeDataString(version)}", ct);

    /// <summary>Store and apply the gameplay settings of <paramref name="version"/> — a job
    /// (<c>tweaks</c>). <paramref name="tweaks"/> is the whole document as the page built it (every section
    /// and key optional, an absent key = the server's original value, <c>{}</c> = everything back to the
    /// original game) and goes out exactly as given: the agent is the one validator, so nothing here
    /// reads, sorts or drops a key of it. Only its kind is checked — the agent reads an empty document as
    /// "restore everything", so a value that is no document at all (null, a list) never leaves this PC.
    /// <para>The agent answers a document its catalogue does not allow with a synchronous 400 that names
    /// the path, before any job exists — what only the stack's own files can decide (an extra item a
    /// domain's reward already holds, more extra items than its reward row has room for) ends the JOB in
    /// error with the same kind of text — and never with a 404, so a 404 on the POST can only mean an agent
    /// without the route — reported as that. A 404 while FOLLOWING the job (the agent restarted and forgot it)
    /// keeps its own text. A running game server checks the new files at once: when it refuses them the
    /// job ends in error with the agent's own sentence and the previous files are back in place. Returns
    /// the final snapshot <c>{state, result: {version, changed, effect, clamped}}</c>: the files rewritten,
    /// <c>live</c> / <c>next-start</c> / <c>none</c>, and the reward amounts a server cap limited.</para></summary>
    public async Task<JsonElement> TweaksSetAsync(string version, JsonElement tweaks,
        Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (tweaks.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("the tweaks document must be a JSON object", nameof(tweaks));
        JsonElement started;
        try { started = await PostAsync("server/tweaks", new { version, tweaks }, ct); }
        catch (AgentHttpException ex) when (ex.Status == 404)
        {
            throw new AgentHttpException(404, L.T("core.agent.tweaksUnsupported"));
        }
        return await FollowJobAsync(started, onLine, ct);
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
            if (onLine is not null && snap.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                foreach (var line in lines.EnumerateArray())
                    onLine(line.GetString() ?? "");
            if (snap.TryGetProperty("next", out var next) && next.TryGetInt32(out int n)) since = n;
            string state = snap.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
            if (state == "error")
                throw new InvalidOperationException(
                    snap.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                        ? err.GetString()!
                        : L.T("core.agent.jobFailed"));
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
            // not be JSON (proxy/firewall interception), so fall back to the raw text.
            string detail = text.Length > 300 ? text[..300] : text;
            try
            {
                using var err = JsonDocument.Parse(text);
                if (err.RootElement.ValueKind == JsonValueKind.Object &&
                    err.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    detail = e.GetString() ?? detail;
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
