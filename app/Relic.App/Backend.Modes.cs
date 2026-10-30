using System.Text.Json;
using System.Text.Json.Nodes;
using Relic.Core.Fiddler;
using Relic.Core.Install;
using Relic.Core.Server;
using Relic.Core.Setup;
using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.App;

/// <summary>
/// Player / Server Admin modes, the start-screen flows (server address, TXT port discovery,
/// connection test, admin token), the open key a server that lets everyone administer it hands out,
/// per-version removal, the local Windows agent, fresh-server agent
/// installs and the SSH host keys of the Linux boxes (I58), player self-service accounts and the admin account policy.
/// Mode is a UX gate — the agent bearer token is the security boundary: every admin rpc goes through
/// <see cref="RequireAdmin"/>. Every change of the state's collections and every state save here runs under
/// <see cref="RelicState.Gate"/>, nothing awaited inside it.
/// </summary>
public sealed partial class Backend
{
    // ── mode + server address ──

    private bool IsAdminMode => string.Equals(_state.Settings.Mode, "admin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Throws when no server address is configured yet (a build without a baked server, before
    /// the start screen was completed): playing, installing and the cert step all template the
    /// Fiddler rules for the server, which need a host.</summary>
    private void RequireServerAddress()
    {
        if (string.IsNullOrWhiteSpace(_state.Settings.ServerHost))
            throw new InvalidOperationException(L.T("backend.noServer"));
    }

    /// <summary>Throws unless the app is in admin mode AND has an agent token.</summary>
    private void RequireAdmin()
    {
        if (!IsAdminMode || AdminServer() is null)
            throw new InvalidOperationException(L.T("backend.adminOnly"));
    }

    /// <summary>The agent connection for ADMIN calls. The HOST is always the configured server address
    /// (start screen / agent install → <c>Settings.ServerHost</c>); server.json lends only the token
    /// (and SSH details) of the record saved for that same host, else the build's baked token applies.
    /// Null when no token applies — see <see cref="ServerConfig.Effective"/>. (If a saved record won
    /// outright, changing the address on the start screen would change only the label while every call
    /// still went to the previous server.)</summary>
    private ServerConfig? AdminServer() =>
        ServerConfig.Effective(StoredServer(), _state.Settings.ServerHost, _state.Settings.AgentPort);

    /// <summary>The record server.json keeps for <paramref name="host"/> (<see cref="ServerConfig.SameHost"/>), or
    /// null — none for no host.</summary>
    private ServerConfig? RecordFor(string? host) => _records.FirstOrDefault(r => ServerConfig.SameHost(r.Host, host));

    /// <summary>The record of the CONFIGURED host (null when it has none: the app then runs on the build's
    /// defaults).</summary>
    private ServerConfig? StoredServer() => RecordFor(_state.Settings.ServerHost);

    /// <summary>The in-memory half of <see cref="ServerConfigStore.Save"/>, called right after it: <paramref name="cfg"/>
    /// comes first and every older record of its host goes — the list replaced as a whole, under the file's lock.</summary>
    private void RecordSaved(ServerConfig cfg)
    {
        lock (ServerConfigStore.Gate)
            _records = new[] { cfg }.Concat(_records.Where(r => !ServerConfig.SameHost(r.Host, cfg.Host))).ToList();
    }

    /// <summary>Takes the records <paramref name="which"/> picks out of memory — the list replaced as a whole, under the
    /// file's lock — and answers them (empty: nothing went). <paramref name="which"/> reads the record alone: nothing of
    /// the launcher's state is touched under that lock.</summary>
    private List<ServerConfig> DropRecords(Func<ServerConfig, bool> which)
    {
        lock (ServerConfigStore.Gate)
        {
            var gone = _records.Where(which).ToList();
            if (gone.Count > 0) _records = _records.Where(r => !which(r)).ToList();
            return gone;
        }
    }

    /// <summary>Token-less client for the configured server (player mode status, signup).</summary>
    private AgentPublicClient PublicClient()
    {
        string host = _state.Settings.ServerHost;
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException(L.T("backend.noServer"));
        return new AgentPublicClient(host, _state.Settings.AgentPort);
    }

    private object SetMode(string? mode)
    {
        mode = (mode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("player" or "admin" or "")) throw new InvalidOperationException(L.T("backend.badMode"));
        lock (RelicState.Gate)
        {
            _state.Settings.Mode = mode;
            _state.Save();
        }
        return BuildInitState();
    }

    /// <summary>Parse + resolve (TXT) the server address typed on the start screen — no side effects.</summary>
    private static async Task<ServerAddress.Resolved> ResolveAddressAsync(string input)
    {
        if (!ServerAddress.TryParse(input, out var addr, out var err) || addr is null)
            throw new InvalidOperationException(L.T("backend.badAddress", new { detail = err ?? "" }));
        return await addr.ResolveAsync(timeout: TimeSpan.FromSeconds(3));
    }

    /// <summary>The agent port to use for a resolved address: re-entering the SAME host keeps the agent
    /// port already configured for it (an agent installed on a custom listen port, the start screen's
    /// agent-port field) unless the resolution actually LEARNED one. Only a TXT record publishing <c>agent=</c> does
    /// that, which is why this reads <c>AgentPortFromTxt</c> rather than comparing AgentPort against
    /// the default: a record that publishes agent=18080 really did learn it. By contrast
    /// an explicit GAME port ("192.0.2.10:21000", which the start-screen placeholder invites) carries no
    /// information about the agent port, so it must not reset it to 18080 and lock admin mode out.
    /// Switching BACK to a server whose agent listens on a custom port compares against the host being
    /// left, so the agent port of the record server.json keeps for that host — any host with a record, not
    /// only the configured one — is the next witness; without it the switch would dial 18080 and read as
    /// "unreachable".</summary>
    private int AgentPortFor(ServerAddress.Resolved r)
    {
        var s = _state.Settings;
        if (r.AgentPortFromTxt is not null) return r.AgentPort;
        if (ServerConfig.SameHost(s.ServerHost, r.Host) && s.AgentPort > 0) return s.AgentPort;
        if (RecordFor(r.Host) is { AgentPort: > 0 } rec) return rec.AgentPort;
        return r.AgentPort;
    }

    /// <summary>Persist a resolved address (host, game port, agent port, where the port came from) — what decides the agent
    /// port (<see cref="AgentPortFor"/> reads the configured host and the records), the change and its save under
    /// <see cref="RelicState.Gate"/>. <paramref name="agentPortOverride"/>: the start screen's explicit agent port
    /// (validated by the caller), which beats every inference of <see cref="AgentPortFor"/>.</summary>
    private void ApplyAddress(ServerAddress.Resolved r, int? agentPortOverride = null)
    {
        var s = _state.Settings;
        int agentPort;
        lock (RelicState.Gate)
        {
            agentPort = agentPortOverride ?? AgentPortFor(r);
            s.ServerHost = r.Host;
            s.ServerPort = r.Port;
            s.AgentPort = agentPort;
            s.ServerPortSource = r.Source;
            s.ServerResolvedAt = DateTime.UtcNow.ToString("o");
            _state.Save();
        }
        Log.Info($"server address set: {r.Host}:{r.Port} agent={agentPort} source={r.Source}");
    }

    /// <summary>Point the launcher at an agent we have just installed, applying the address as a WHOLE the
    /// way <see cref="ApplyAddress"/> does. Both install targets share it: a hand-rolled
    /// partial copy that moves ServerHost but leaves <c>ServerPort</c> on the PREVIOUS server's game port
    /// makes the next PLAY template the Fiddler redirect to newhost:oldport, and the client hangs on login.
    /// The agent port is the one we just told the agent to bind, so it wins. The change and its save run under
    /// <see cref="RelicState.Gate"/>; each caller holds that lock across its whole tail — the record saved, taken into
    /// memory and applied here as one step, so a Delete all never lands between them.</summary>
    private void ApplyInstalledAgent(string host, int agentPort)
    {
        var s = _state.Settings;
        lock (RelicState.Gate)
        {
            if (!ServerConfig.SameHost(s.ServerHost, host))
            {
                // A different box: nothing is known about its GAME port yet. Drop back to the default and let
                // the next start-screen entry re-resolve it (TXT included) rather than keep a foreign port.
                s.ServerPort = ServerAddress.DefaultGamePort;
                s.ServerPortSource = ServerAddress.SourceDefault;
            }
            s.ServerHost = host;
            if (agentPort > 0) s.AgentPort = agentPort;
            s.ServerResolvedAt = DateTime.UtcNow.ToString("o");
            s.Mode = "admin";
            _state.Save();
        }
        Log.Info($"agent installed: host={host} agent={s.AgentPort} game={s.ServerPort} source={s.ServerPortSource}");
    }

    /// <summary>Parse + resolve + persist the server address typed on the start screen (player mode) or
    /// carried by a saved server. <paramref name="agentPort"/>: an explicit agent port 1–65535 beats every
    /// inference, as on <c>admin.login</c>; 0 or any other value = inferred (<see cref="AgentPortFor"/>).</summary>
    private async Task<object> SetServerAddressAsync(string input, int agentPort = 0)
    {
        var r = await ResolveAddressAsync(input);
        ApplyAddress(r, ExplicitAgentPort(agentPort));
        return new { host = r.Host, port = r.Port, agentPort = _state.Settings.AgentPort, source = r.Source };
    }

    /// <summary>A typed agent port from the start screen: 1-65535 counts, anything else (0 = the field
    /// was left empty) means "infer it" (<see cref="AgentPortFor"/>).</summary>
    private static int? ExplicitAgentPort(int agentPort) => agentPort is > 0 and <= 65535 ? agentPort : null;

    /// <summary>Connection test for the start screen: resolve, probe the game port (sdk) and the
    /// agent's public status. Never throws for an unreachable server — the result says so.</summary>
    private async Task<object> TestServerAsync(string? input, int agentPortOverride = 0)
    {
        string text = string.IsNullOrWhiteSpace(input) ? _state.Settings.ServerHost : input!;
        if (!ServerAddress.TryParse(text, out var addr, out var err) || addr is null)
            throw new InvalidOperationException(L.T("backend.badAddress", new { detail = err ?? "" }));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await addr.ResolveAsync(timeout: TimeSpan.FromSeconds(3));
        // The SAME decision the persisting paths make (see AgentPortFor): Test must dial the port that
        // admin login will actually use, or an agent on a non-default listen port is reported unreachable
        // here and then connects fine on the very next click. A port typed on the start screen wins.
        int agentPort = ExplicitAgentPort(agentPortOverride) ?? AgentPortFor(r);
        var gameTask = ServerProbe.ProbeSdkAsync(r.Host, r.Port, TimeSpan.FromSeconds(4));
        object agent;
        try
        {
            using var pub = new AgentPublicClient(r.Host, agentPort, TimeSpan.FromSeconds(5));
            var st = await pub.StatusAsync();
            var up = new List<string>();
            if (st.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Object)
                foreach (var v in vs.EnumerateObject())
                    if (v.Value.TryGetProperty("up", out var u) && u.ValueKind == JsonValueKind.True) up.Add(v.Name);
            agent = new
            {
                reachable = true,
                name = st.TryGetProperty("name", out var n) ? n.GetString() : null,
                versionsUp = up,
                signup = st.TryGetProperty("signup", out var sg) ? sg.Clone() : default,
                playerCommands = st.TryGetProperty("playerCommands", out var pc) && pc.ValueKind == JsonValueKind.True,
                status = st,
            };
        }
        catch (Exception ex)
        {
            agent = new { reachable = false, error = ex.Message };
        }
        var game = await gameTask;
        return new
        {
            host = r.Host, port = r.Port, agentPort, source = r.Source,
            game = new { reachable = game.Reachable, detail = game.Detail },
            agent,
            ms = sw.ElapsedMilliseconds,
        };
    }

    /// <summary>Admin login from the start screen. Transactional: the typed address is resolved but
    /// NOT persisted until the token is accepted and its record saved — a refused login leaves the previous server
    /// in place, and so does a saved token it borrowed that went while the agent was asked (a Delete of that host's
    /// saved server, another login's token): never saved back, <c>backend.tokenRequired</c> instead.
    /// Token precedence: the typed one → the one saved for the host it logs IN to (its record in server.json,
    /// whatever host is configured now) → the build's baked one. A typed
    /// token must be validated against the agent's /status (401 = refused; unreachable = cannot tell,
    /// so refused too); a token already stored for this host was validated before, so an unreachable
    /// agent lets the admin in with a warning (the server may simply be down right now — the launcher
    /// is still needed to install the game or start a local agent). A legacy tunnel whose SSH key the pins refuse
    /// (<see cref="HostKeyRefusedException"/>, I58) is no unreachable agent: the login is refused with that text, whatever
    /// token it holds. <paramref name="agentPort"/>: the
    /// start screen's optional explicit agent port (0 = infer), the only way to reach an agent on a
    /// non-default listen port that no <c>_relic</c> TXT record publishes.</summary>
    private async Task<object> AdminLoginAsync(string address, string token, int agentPort = 0)
    {
        var r = await ResolveAddressAsync(address);
        token = (token ?? "").Trim();
        // A record saved for an open key lends nothing: it is the agent's hand-out to every player, not a
        // token this admin typed, and the admin of that server may have revoked it since. The token is
        // required as if nothing were stored, and the record saved below replaces it.
        var storedForHost = RecordFor(r.Host) is { } st && ServerConfig.SameHost(st.Host, r.Host) && !st.OpenKey ? st : null;
        bool typed = token.Length > 0;
        if (!typed) token = storedForHost?.AgentToken ?? "";
        if (token.Length == 0) token = BuildDefaults.AgentToken;
        if (token.Length == 0) throw new InvalidOperationException(L.T("backend.tokenRequired"));
        int? explicitPort = ExplicitAgentPort(agentPort);
        // An SSH tunnel override saved for this host keeps its SSH details; anything else is direct.
        var cfg = storedForHost is not null && !storedForHost.IsDirect
            ? storedForHost with { Host = r.Host, AgentToken = token }
            : new ServerConfig(r.Host, storedForHost?.Port ?? 22, storedForHost?.User ?? "root",
                storedForHost?.Password ?? "", token, explicitPort ?? AgentPortFor(r), "direct");
        string? warning = null;
        HostKeySeen? tunnelKey = null;
        try
        {
            await using var a = new AgentClient(cfg, TunnelPinsOf(cfg));
            await a.ConnectAsync();
            await a.StatusAsync();
            tunnelKey = a.TunnelKey;
        }
        catch (AgentHttpException ex) when (ex.Status == 401)
        {
            throw new InvalidOperationException(L.T("backend.tokenRefused"));
        }
        // The tunnel of a legacy Mode: ssh record met a key the box's pins refuse (I58): no unreachable agent — the login is
        // refused with that text, the saved token never entered with.
        catch (HostKeyRefusedException) { throw; }
        catch (Exception ex)
        {
            if (typed || storedForHost is null)
                throw new InvalidOperationException(L.T("backend.agentUnreachable", new { detail = ex.Message }));
            warning = L.T("backend.agentUnreachableEnter", new { detail = ex.Message });
            Log.Info($"admin login: agent at {r.Host}:{cfg.AgentPort} unreachable ({ex.Message}) — entering with the stored token");
        }
        // The write tail is ONE step under the state's lock, nothing awaited in it: the look at the borrowed token and the
        // saves that follow it, so a Delete of the host (which forgets under the same lock) lands before or after it whole.
        lock (RelicState.Gate)
        {
            // A legacy tunnel's SSH details are its host's record: a Delete of that saved server while the agent was asked
            // forgot them (I56), so they are never saved back — nor the tunnel's key pinned —, whichever token the login
            // holds. Nothing has moved; logging in again decides from what is saved then.
            if (storedForHost is { IsDirect: false } && RecordFor(r.Host) is not { IsDirect: false })
            {
                Log.Info($"admin login: the tunnel saved for {r.Host} went while its agent was asked — not saved back");
                throw new InvalidOperationException(L.T("backend.savedTokenChanged"));
            }
            // The saved token this login borrowed went while the agent was asked — the host's saved server deleted, or
            // another login saved a token of its own there —: it is never saved back. The login asks for a token instead,
            // and nothing has moved.
            if (!typed && storedForHost is not null
                && !(RecordFor(r.Host) is { OpenKey: false } now && now.AgentToken == storedForHost.AgentToken))
            {
                Log.Info($"admin login: the token saved for {r.Host} went while its agent was asked — not saved back");
                throw new InvalidOperationException(L.T("backend.tokenRequired"));
            }
            // Persist the token (DPAPI) as this host's record FIRST, so the next start does not ask again — the records
            // of other hosts stay. A file another program holds refuses the save (it is never written over a list it could
            // not read), and the login then fails with the previous server, its ports and the mode still in place; only a
            // saved record moves the address.
            ServerConfigStore.Save(cfg);
            RecordSaved(cfg);
            ApplyAddress(r, explicitPort);
            _state.Settings.Mode = "admin";
            _state.Save();
            // A legacy tunnel the agent answered: the key it took as it came is the box's from here on (I58).
            PinTunnelKey(cfg, tunnelKey);
        }
        Log.Info($"admin login ok: {r.Host} agent={cfg.AgentPort} mode={cfg.Mode}");
        return new { state = BuildInitState(), warning };
    }

    /// <summary>"Enter admin mode" on the Player page of a server whose admin lets everyone administer it:
    /// ask the agent's token-less route for the key it hands out and enter Server Admin mode with it —
    /// transactional like <see cref="AdminLoginAsync"/>, nothing is saved until the key is accepted on
    /// /status. No address resolution: Player mode reached that page with the host and the agent port
    /// already resolved through server.setAddress. The saved record is flagged as an open key, so the start
    /// screen never offers it as a saved token and a 401 on it later is a revocation (<see cref="WithAgent"/>),
    /// not a rotated token. A 401 here is the option closed between the two calls; an unreachable agent is
    /// never entered anyway — the key was handed out a moment ago and nothing of it was validated before.
    /// <para>A real token already saved for this very host (typed on the start screen, or saved by an agent
    /// install) outranks the key: the launcher enters with that token — validated on /status like a typed one,
    /// an unreachable agent not entered — and neither asks for the key nor saves anything: server.json keeps one
    /// record per host, so the key's record would take the token's place, and the key's revocation would then
    /// leave the host with no token at all. A 401 on that
    /// token is the one case the key path takes over: the agent at this host refuses a token it once accepted
    /// (rotated on the box since the record was saved), so the record is dead, and nothing else in the launcher
    /// drops one — the start screen replaces it only with a typed token the agent accepts, which a player does
    /// not have. The key is then fetched and validated as if nothing were stored, and the Save below replaces
    /// the dead record. An unreachable agent proves nothing against the token and keeps its refusal. The reply's
    /// <c>openKey</c> tells the UI which way it entered.</para></summary>
    private async Task<object> OpenAdminEnterAsync()
    {
        string host = _state.Settings.ServerHost;
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException(L.T("backend.noServer"));
        // Effective: the configured host + agent port with the record's token (and tunnel details) — the
        // connection AdminServer() dials once the mode is admin, so what is validated is what will be used.
        if (StoredServer() is { OpenKey: false } st && ServerConfig.SameHost(st.Host, host) && !string.IsNullOrWhiteSpace(st.AgentToken)
            && ServerConfig.Effective(st, host, _state.Settings.AgentPort) is { } stored)
        {
            bool tokenRefused = false;
            HostKeySeen? tunnelKey = null;
            try
            {
                await using var a = new AgentClient(stored, TunnelPinsOf(stored));
                await a.ConnectAsync();
                await a.StatusAsync();
                tunnelKey = a.TunnelKey;
            }
            catch (AgentHttpException ex) when (ex.Status == 401)
            {
                // A dead record: nothing replaces it but the Save of the key path below (or a typed token the
                // start screen validates), so the entry goes on with the key instead of refusing for good.
                tokenRefused = true;
                Log.Info($"open admin enter: the token saved for {host} is refused (401) — a dead record, entering with the key instead");
            }
            // A legacy tunnel whose SSH key the pins refuse (I58) keeps its own text: no unreachable agent.
            catch (HostKeyRefusedException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(L.T("backend.agentUnreachable", new { detail = ex.Message }));
            }
            if (!tokenRefused)
            {
                // The record is the configured host's own, already among the records: nothing to save. It is entered with
                // only while it is still the host's — the look and the mode one step under the state's lock, nothing
                // awaited: a Delete of the host's saved server, or another login's token, landing while the agent was asked
                // is never entered over (the entry is asked again, and decides from what is saved then).
                lock (RelicState.Gate)
                {
                    if (!(StoredServer() is { OpenKey: false } now && now.AgentToken == st.AgentToken))
                    {
                        Log.Info($"open admin enter: the token saved for {host} changed while its agent was asked — not entered");
                        throw new InvalidOperationException(L.T("backend.savedTokenChanged"));
                    }
                    _state.Settings.Mode = "admin";
                    _state.Save();
                    PinTunnelKey(stored, tunnelKey);
                }
                Log.Info($"open admin enter: {host} agent={stored.AgentPort} — with the token saved for it");
                return new { state = BuildInitState(), warning = (string?)null, openKey = false };
            }
        }
        string key;
        using (var pub = PublicClient()) key = await pub.OpenAdminKeyAsync();
        var cfg = new ServerConfig(host, 22, "root", "", key, _state.Settings.AgentPort, "direct", OpenKey: true);
        try
        {
            await using var a = new AgentClient(cfg);
            await a.ConnectAsync();
            await a.StatusAsync();
        }
        catch (AgentHttpException ex) when (ex.Status == 401)
        {
            throw new InvalidOperationException(L.T("backend.openKeyRefused"));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(L.T("backend.agentUnreachable", new { detail = ex.Message }));
        }
        // Persist the key (DPAPI) like a token, flagged, as this host's record: it survives a restart as a token
        // does, but the start screen offers the Player panel for it (ui boot()) and the Server page the way in
        // again; the first refused call takes it away (WithAgent). One step under the state's lock, nothing awaited.
        lock (RelicState.Gate)
        {
            ServerConfigStore.Save(cfg);
            RecordSaved(cfg);
            _state.Settings.Mode = "admin";
            _state.Save();
        }
        Log.Info($"open admin enter: {host} agent={cfg.AgentPort}");
        return new { state = BuildInitState(), warning = (string?)null, openKey = true };
    }

    /// <summary>Leave the Server Admin mode entered through an open key, in this order: the configured host's record
    /// goes out of the records when it is that key's (flagged) — in memory FIRST, since the connection in effect is
    /// what every following call reads; the build's default then applies —, the mode returns to Player, the state
    /// is saved, and last that host's open-key record goes out of the file
    /// (<see cref="ServerConfigStore.ForgetWhere"/>: the file loses exactly what memory dropped — a real token saved
    /// for the host meanwhile stays, and so do the records of every other host). Shared by the revocation in
    /// <see cref="WithAgent"/> and the rpc the Server page calls once this very launcher switched the option off.
    /// <paramref name="forgot"/>: whether an open key was in effect at all — with none it only answers the state
    /// (idempotent; a typed or baked token is never touched here) and the caller posts no revocation. Neither file is
    /// the forget: a state file that cannot be written and a record that cannot be removed are one log line each,
    /// never a failed forget — the in-memory mode is what this session runs on and the next successful save persists
    /// it, while a leftover open-key record is harmless: it lends no token to the start screen
    /// (<see cref="ServerConfig.TokenHosts"/>) and <see cref="AdminLoginAsync"/> ignores it. A forget that threw past
    /// the in-memory half would leave the backend in Player mode with the UI never told — the rpc would reject and
    /// <see cref="WithAgent"/> would post no event. The look at the bearer in effect and the whole forget are one step under
    /// <see cref="RelicState.Gate"/>, nothing awaited: of two refused calls at the same moment one forgets, the other finds
    /// nothing left (and posts no second revocation).</summary>
    private object OpenAdminForget(out bool forgot)
    {
        lock (RelicState.Gate)
        {
            forgot = AdminServer() is { OpenKey: true };
            if (forgot)
            {
                // The configured host, read once: what memory drops and what the file loses are that host's open key.
                string host = _state.Settings.ServerHost;
                bool dropped = DropRecords(r => r.OpenKey && ServerConfig.SameHost(r.Host, host)).Count > 0;
                _state.Settings.Mode = "player";
                // The in-memory mode is what this session runs on; the next successful save persists it.
                try { _state.Save(); }
                catch (Exception ex) { Log.Error("persisting the return to Player mode", ex); }
                Log.Info($"open admin: the key for {host} forgotten — back to Player mode");
                if (dropped)
                {
                    try { ServerConfigStore.ForgetWhere(host, r => r.OpenKey); }
                    catch (Exception ex) { Log.Error("forgetting the open key's file", ex); }
                }
            }
        }
        return BuildInitState();
    }

    private void SetLoginMuted(bool muted)
    {
        lock (RelicState.Gate)
        {
            _state.Settings.LoginMuted = muted;
            _state.Save();
        }
    }

    private void SetLoginAnimOff(bool off)
    {
        lock (RelicState.Gate)
        {
            _state.Settings.LoginAnimOff = off;
            _state.Save();
        }
    }

    // ── saved servers (the start screen's list) ──

    /// <summary>
    /// <c>servers.save {address, mode, label?, agentPort?}</c>: saves the server as the entry of its host
    /// (<see cref="RelicState.SaveServer"/>: one entry per host — saving a host again replaces its entry and keeps its
    /// id —, at most <see cref="SavedServer.MaxEntries"/>). An entry is an address, a mode and a name, never a secret:
    /// nothing here reads a secret, and a token reaches server.json only through an admin login, the agent on this PC
    /// entered, an open-administration entry or an agent install the agent accepted. Refused in the page's words: an
    /// address that does not parse, a mode other than player / admin, a name longer than
    /// <see cref="SavedServer.MaxLabel"/> characters or holding a control character (a tab, a line break), a full list.
    /// The change and its save run under <see cref="RelicState.Gate"/>, nothing awaited.
    /// Answers <c>{servers}</c> (<see cref="SavedServersForPage"/>).
    /// </summary>
    private object SaveServerEntry(string address, string mode, string label, int agentPort)
    {
        var entry = SavedServer.Create(address, mode, label, agentPort, out var problem)
            ?? throw SavedServerRefused(problem, address);
        lock (RelicState.Gate)
        {
            var saved = _state.SaveServer(entry);
            if (saved != SavedServerProblem.None) throw SavedServerRefused(saved, address);
            _state.Save();
        }
        Log.Info($"saved servers: {entry.Host} saved ({entry.Mode})");
        return new { servers = SavedServersForPage() };
    }

    /// <summary>Why <see cref="SaveServerEntry"/> saved nothing, in the page's words.</summary>
    private static InvalidOperationException SavedServerRefused(SavedServerProblem problem, string address) => problem switch
    {
        SavedServerProblem.BadMode => new(L.T("backend.servers.badMode")),
        SavedServerProblem.BadLabel => new(L.T("backend.servers.badLabel")),
        SavedServerProblem.Full => new(L.T("backend.servers.full")),
        _ => new(L.T("backend.badAddress", new { detail = ServerAddress.TryParse(address, out _, out var err) ? "" : err ?? "" })),
    };

    /// <summary><c>servers.delete {id}</c>: deletes one saved server — nothing asked — and with it everything this
    /// launcher keeps for its host (<see cref="ForgetHost"/>). An id the list no longer holds is
    /// <c>backend.servers.unknown</c>. Answers <c>{servers, state, fileKept}</c>: the mode may be back at the start
    /// screen's question; <c>fileKept</c> = server.json could not lose the host's records right now (another program
    /// holds it) — forgotten in this session, back at the next start until a Delete gets through.</summary>
    private object DeleteServerEntry(string id)
    {
        bool fileKept;
        lock (RelicState.Gate)
        {
            var entry = _state.SavedServerById(id) ?? throw new InvalidOperationException(L.T("backend.servers.unknown"));
            _forgetFileFailed = false;
            ForgetHost(entry.Host);
            fileKept = _forgetFileFailed;
        }
        return new { servers = SavedServersForPage(), state = BuildInitState(), fileKept };
    }

    /// <summary>
    /// <c>servers.deleteAll</c> (the page asks once): every saved server goes with everything this launcher keeps for its
    /// host (<see cref="ForgetHost"/>), and so does every host a record names without an entry (an admin login, the agent
    /// on this PC entered, an open-administration entry or an agent install keeps a record without one) — its token or
    /// key, the game logins remembered there and its SSH host keys —; last, server.json itself goes, with the copies of
    /// unreadable files kept beside it (<see cref="ServerConfigStore.Delete"/>, inside a try: forgetting a host never
    /// removes those). One change under <see cref="RelicState.Gate"/>, the state saved once, nothing awaited. Answers
    /// <c>{servers, state, fileKept}</c>: <c>fileKept</c> = server.json could not be deleted right now (another program
    /// holds it), so its tokens come back at the next start.
    /// </summary>
    private object DeleteAllServerEntries()
    {
        int forgotten;
        bool fileKept = false;
        lock (RelicState.Gate)
        {
            var hosts = new List<string>();
            void Add(string? host)
            {
                string h = (host ?? "").Trim();
                if (h.Length > 0 && !hosts.Any(x => ServerConfig.SameHost(x, h))) hosts.Add(h);
            }
            foreach (var entry in _state.SavedServerList()) Add(entry.Host);
            foreach (var record in ServerConfigStore.LoadAll()) Add(record.Host);
            foreach (var record in _records) Add(record.Host);
            foreach (string host in hosts) ForgetHost(host, save: false);
            // Memory holds no record from here on, whatever the file below does.
            DropRecords(_ => true);
            try { ServerConfigStore.Delete(); }
            catch (Exception ex) { fileKept = true; Log.Error("saved servers: deleting server.json", ex); }
            _state.Save();
            forgotten = hosts.Count;
        }
        Log.Info($"saved servers: all deleted — {forgotten} server(s) forgotten" + (fileKept ? " (server.json kept: it could not be deleted)" : ""));
        return new { servers = SavedServersForPage(), state = BuildInitState(), fileKept };
    }

    /// <summary>Set by <see cref="ForgetHost"/> when server.json could not lose a host's records (another program holds
    /// it); <see cref="DeleteServerEntry"/> clears it before the forget and reads it after — all under
    /// <see cref="RelicState.Gate"/>.</summary>
    private bool _forgetFileFailed;

    /// <summary>
    /// Forgets everything this launcher keeps for <paramref name="host"/> (<see cref="ServerConfig.SameHost"/>), under
    /// <see cref="RelicState.Gate"/>, in this order: its saved entry, the game logins it remembers there, the server's
    /// answers about its pre-made login and its SSH host keys (<see cref="RelicState.ForgetServer"/>: every
    /// <c>&lt;host&gt;:&lt;port&gt;</c> key of the host, on any port); its records — in memory, then in server.json
    /// (<see cref="ServerConfigStore.Forget"/>, inside a try: a record that cannot be removed there is one log line, and
    /// <see cref="_forgetFileFailed"/> says so) —;
    /// when it is the configured host and Server Admin mode is left without a token, the mode goes back to the start
    /// screen's question (<c>Mode = ""</c>); then the state is saved (<paramref name="save"/> false: the caller saves
    /// once, under the same lock). Never changed: the configured host (only a host writer moves it), a token baked
    /// into the build, the <see cref="RelicState.ClientSecret"/> and anything on the server — the agent still counts
    /// this launcher's accounts by its client id. Nothing is awaited under the lock. True when anything went.
    /// </summary>
    private bool ForgetHost(string host, bool save = true)
    {
        lock (RelicState.Gate)
        {
            bool any = _state.ForgetServer(host);
            any |= DropRecords(r => ServerConfig.SameHost(r.Host, host)).Count > 0;
            try { any |= ServerConfigStore.Forget(host); }
            catch (Exception ex) { _forgetFileFailed = true; Log.Error("saved servers: forgetting the records of a server in server.json", ex); }
            if (ServerConfig.SameHost(host, _state.Settings.ServerHost) && IsAdminMode && AdminServer() is null)
                _state.Settings.Mode = "";
            if (save) _state.Save();
            Log.Info($"saved servers: what was kept for {host} forgotten" + (any ? "" : " (there was nothing)"));
            return any;
        }
    }

    /// <summary>The saved servers as the page draws them, newest first: <c>{id, address, host, mode, label, agentPort,
    /// hasToken, openKey, current}</c>. <c>hasToken</c>: server.json keeps an agent token for the host
    /// (<see cref="ServerConfig.TokenHosts"/> — never an open key); <c>openKey</c>: it keeps the key an
    /// open-administration entry got there; <c>current</c>: the host is the configured one AND the entry's mode is the
    /// configured mode. Booleans only — never a token or a key.</summary>
    private List<object> SavedServersForPage()
    {
        var records = _records;
        var tokenHosts = ServerConfig.TokenHosts(records);
        string configured = _state.Settings.ServerHost, mode = _state.Settings.Mode;
        return _state.SavedServerList().Select(s => (object)new
        {
            id = s.Id,
            address = s.Address,
            host = s.Host,
            mode = s.Mode,
            label = s.Label,
            agentPort = s.AgentPort,
            hasToken = tokenHosts.Any(h => ServerConfig.SameHost(h, s.Host)),
            openKey = records.Any(r => r.OpenKey && ServerConfig.SameHost(r.Host, s.Host)),
            current = ServerConfig.SameHost(s.Host, configured) && string.Equals(s.Mode, mode, StringComparison.OrdinalIgnoreCase),
        }).ToList();
    }

    // ── version removal ──

    private object StartRemove(string versionId, bool deleteFiles)
    {
        if (string.IsNullOrWhiteSpace(versionId)) throw new InvalidOperationException(L.T("backend.missingVersion"));
        if (Interlocked.Exchange(ref _installBusy, 1) != 0)
            throw new InvalidOperationException(L.T("backend.install.busy"));
        InstallGuardTaken($"the removal of {versionId} started");
        _ = Task.Run(async () =>
        {
            // The outcome, posted only once the install guard is free again: a page that installs or removes next on it
            // must not meet this removal's "busy".
            Action? outcome = null;
            try
            {
                await AwaitRecovery();
                var svc = new InstallService(_state, Environment.ProcessPath ?? "");
                var progress = new Progress<InstallProgress>(pr =>
                    PostEvent("remove.progress", new { versionId, phase = pr.Phase.ToString(), pr.Fraction, pr.Message }));
                var report = await svc.RemoveAsync(versionId, deleteFiles, progress);
                Log.Info($"remove done: {versionId} deleteFiles={deleteFiles} leftovers={report.Leftovers.Count}");
                var state = BuildInitState();
                outcome = () => PostEvent("remove.done", new { versionId, leftovers = report.Leftovers, state });
            }
            catch (Exception ex)
            {
                Log.Error($"remove failed: {versionId}", ex);
                outcome = () => PostEvent("remove.error", new { versionId, message = ex.Message });
            }
            finally { Volatile.Write(ref _installBusy, 0); }
            outcome?.Invoke();
        });
        return new { started = true };
    }

    // ── shell ──

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
            throw new InvalidOperationException("only http(s) links can be opened");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.ToString()) { UseShellExecute = true });
    }

    /// <summary>The GIO server guide shipped with the build — <c>assets\GIO-guide.pdf</c>, a copy of
    /// the vendor's guide book with every document property stripped (no author, producer or dates).
    /// Opened with whatever the system uses for PDFs; a build without the file says so instead of
    /// failing silently.</summary>
    private static void OpenGuide()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "GIO-guide.pdf");
        if (!File.Exists(path)) throw new InvalidOperationException(L.T("backend.guideMissing"));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ── local Windows agent ──

    private static void FiddlerGuard()
    {
        if (FiddlerAutomation.IsRunning) throw new InvalidOperationException(L.T("backend.closeFiddlerFirst"));
    }

    /// <summary>Everything here is blocking and none of it touches the state or WinForms, so it runs
    /// OFF the UI thread: WebView2 raises the message there, and the probes below are two `netstat`
    /// runs, three `netsh` rule queries, the interpreter probes of FindPython (8 s at most each) and a
    /// directory walk of a folder that can hold a multi-GB hotpatch mirror. On the UI thread that is
    /// a frozen window every time the Server page opens.</summary>
    private Task<object> LocalAgentStatusAsync() => Task.Run<object>(async () =>
    {
        LocalAgent.RecoverCutUpdate(wait: true);  // before the agent's files are looked at; nothing to do but after an update or an install that was cut short
        var c = LocalAgent.ReadConfig();
        int port = c?.ListenPort ?? ServerAddress.DefaultAgentPort;
        // FindRunning, not the pid file alone: an agent can listen on the port without one (it writes the file
        // itself only when it runs with --config, and a deleted file names nothing) and would read as
        // "installed, stopped" — the next Start would spawn a second pythonw that dies on the port.
        var (running, pid) = LocalAgent.FindRunning(port);
        bool healthy = running && await LocalAgent.IsHealthyAsync(port);
        var py = LocalAgent.FindPython();
        // What stands at each folder the config names, looked at once per version (a network path is not
        // touched): the re-install form offers "Download now" beside "Keep" only for a folder that holds no
        // server yet.
        var kindNames = ServerVersions.All.ToDictionary(v => v, v => LocalAgent.FolderKindName(c?.DirOf(v)));
        return new
        {
            installed = LocalAgent.IsInstalled,
            running,
            healthy,
            pid,
            port,
            host = c?.Host ?? "127.0.0.1",
            autostart = LocalAgent.AutostartEnabled,
            python = py is null ? null : new { py.Exe, py.Version },
            root = LocalAgent.Root,
            logPath = LocalAgent.LogPath,
            fiddlerRunning = FiddlerAutomation.IsRunning,
            // The inbound rules (netsh, by exit code): the agent's own port's, and both game ports' of a server other PCs
            // join (TCP 21000, UDP 21081 — what localagent.firewall {gamePorts} adds and the uninstall removes).
            firewallRule = LocalAgent.FirewallRuleExists(port),
            serverRules = LocalAgent.ServerRulesExist(),
            // The stack folder the installed config names for each server version ("" = none), by version
            // id: what the re-install form pre-fills its folder fields from and the uninstall question names.
            dirs = ServerVersions.All.ToDictionary(v => v, v => c?.DirOf(v) ?? ""),
            // By version id: "none" (no folder), "absent", "empty", "stack", "other", "unknown" (a network
            // path) — and present = that folder holds a server.
            kinds = kindNames,
            present = kindNames.ToDictionary(kv => kv.Key, kv => kv.Value == "stack"),
            // A 7z tool on this PC: a package that comes as a .7z only cannot be unpacked without one.
            extractor = LocalAgent.FindExtractor() is not null,
            // The rest of what a re-install form pre-fills from the installed config, so "Install again"
            // does not reset the bind, the advertised address, the name, the MUIP host or the option
            // "Keep .env settings": the form writes GIO_MUIP_HOST even blank, GIO_BIND_IP too unless that
            // option is on, and GIO_KEEP_ENV as 1 or 0 — and MergeConfig lets every key the form writes win,
            // so a value saved from the Agent settings card survives only when the form carries it.
            // listen is the agent's effective value (its own loopback default when the file names none);
            // "" = no config. bindIp is the raw GIO_BIND_IP — host above is loopback whenever the agent
            // listens on loopback or another address, and follows the stacks' own OUTER_IP while "Keep .env
            // settings" is on, so it cannot stand in for it. keepEnv is the config's GIO_KEEP_ENV read by
            // the agent's own rule; false without a config. None of them is a secret.
            // The MySQL root password and the Flask key need no pre-fill and get none: a blank field OMITS
            // the key, so MergeConfig keeps the stored line — a secret never travels to the UI.
            listen = c?.Listen ?? "",
            bindIp = c?.BindIp ?? "",
            advertisedIp = c?.AdvertisedIp ?? "",
            advertisedHost = c?.AdvertisedHost ?? "",
            serverName = c?.ServerName ?? "",
            muipHost = c?.MuipHost ?? "",
            keepEnv = c?.KeepEnv ?? false,
            // Size of the agent folder for the uninstall dialog; null = unknown (the walk gave up).
            rootBytes = RootBytes(LocalAgent.Root, TimeSpan.FromSeconds(3)),
        };
    });

    /// <summary><c>localagent.folderCheck {dir, version}</c>: what the install form's folder field names on THIS
    /// PC, for its live line — <c>{kind, drive, free, problem, extractor}</c>. <c>problem</c> = why the agent here
    /// cannot use the folder (not a full path on a drive letter, or a drive it refuses: no such drive,
    /// network, CD, RAM disk) — the sentence the install itself would refuse with; <c>kind</c> = what stands
    /// there (<see cref="LocalAgent.FolderKindName"/>), "unknown" for a refused drive, which is not touched;
    /// <c>drive</c> = "X:"; <c>free</c> = the bytes free on that drive, null when not looked at or
    /// unreadable; <c>extractor</c> = a 7z tool is on this PC. A blank <c>dir</c> answers <c>extractor</c> alone
    /// (the form asks it when it opens). Reads this PC only — no mode, no token —, off the message thread;
    /// never throws.</summary>
    private static Task<object> LocalFolderCheckAsync(JsonElement p)
    {
        // Read off the payload BEFORE the task: p points into the JsonDocument that OnMessage disposes as
        // soon as the handler returns.
        string dir = Str(p, "dir").Trim(), version = Str(p, "version").Trim();
        return Task.Run<object>(() =>
        {
            bool extractor = LocalAgent.FindExtractor() is not null;
            if (dir.Length == 0)
                return new { kind = (string?)null, drive = (string?)null, free = (long?)null, problem = (string?)null, extractor };
            try
            {
                bool onDrive = AgentInstallPlan.IsWindowsDrivePath(dir);
                string? problem = onDrive ? LocalAgent.DriveProblem(version, dir) : L.T("core.deploy.dirNotLocal", new { version, dir });
                string? drive = onDrive ? char.ToUpperInvariant(dir[0]) + ":" : null;
                string kind = problem is null ? LocalAgent.FolderKindName(dir) : "unknown";
                long? free = null;
                if (problem is null && drive is not null && kind != "unknown")
                {
                    try { free = new DriveInfo(drive + "\\").AvailableFreeSpace; }
                    catch (Exception ex) { Log.Info($"folder check: the free space of {drive} is not known ({ex.Message})"); }
                }
                return new { kind = (string?)kind, drive, free, problem, extractor };
            }
            catch (Exception ex)
            {
                Log.Info($"folder check of {dir} (non-fatal): {ex.Message}");
                return new { kind = (string?)"unknown", drive = (string?)null, free = (long?)null, problem = (string?)null, extractor };
            }
        });
    }

    /// <summary>Bytes under <paramref name="dir"/>, or null when the walk exceeds <paramref name="budget"/>:
    /// <c>hotpatch\</c> can hold GBs in thousands of files, and a status rpc must stay quick.</summary>
    private static long? RootBytes(string dir, TimeSpan budget)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long total = 0;
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (string f in Directory.EnumerateFiles(dir, "*", opts))
            {
                if (sw.Elapsed > budget) return null;
                try { total += new FileInfo(f).Length; } catch { /* vanished mid-walk */ }
            }
            return total;
        }
        catch (Exception ex) { Log.Error("measuring the agent folder (non-fatal)", ex); return null; }
    }

    /// <summary>The port the agent on this PC listens on — one parser (<see cref="LocalAgent.ReadConfig"/>),
    /// the agent's own, so a quoted or BOM-prefixed line reads the same on both sides.</summary>
    private int LocalAgentPort() => LocalAgent.ReadConfig()?.ListenPort ?? ServerAddress.DefaultAgentPort;

    /// <summary>The start screen's facts about the agent on this PC: installed, and where the launcher
    /// would dial it. Cheap by design (two File.Exists, one small read, one registry read — and, for the
    /// host while "Keep .env settings" is on, the <c>.env</c> file of each stack) — init is synchronous;
    /// whether it is RUNNING is learned when the button is pressed.</summary>
    private static object LocalAgentInit()
    {
        // An update of that agent the launcher did not live to finish is undone first — a few existence
        // checks when there is nothing to pick up —, or the agent would read as not installed with its
        // script set aside.
        LocalAgent.RecoverCutUpdate();
        var c = LocalAgent.ReadConfig();
        return new
        {
            installed = LocalAgent.IsInstalled,
            host = c?.Host ?? "127.0.0.1",
            port = c?.ListenPort ?? ServerAddress.DefaultAgentPort,
            autostart = LocalAgent.AutostartEnabled,
        };
    }

    /// <summary>"Use the agent on this PC" from the start screen: enter admin mode with the locally
    /// installed agent, persisting EXACTLY what the install persists — transactional like
    /// <see cref="AdminLoginAsync"/>, nothing is saved until the config's token is accepted. Health FIRST,
    /// whatever the pid file says: an agent can run without one (<see cref="LocalAgent.FindRunning"/>), and
    /// StartAsync would spawn a second pythonw that dies on the bound port. The Fiddler guard applies only
    /// when a start is needed — entering with an already-running agent while Fiddler is up is not a
    /// local-server operation.</summary>
    private async Task<object> LocalAgentEnterAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException(L.T("backend.localAgent.windowsOnly"));
        var c = LocalAgent.ReadConfig();
        if (c is null || !LocalAgent.IsInstalled) throw new InvalidOperationException(L.T("core.deploy.notInstalledLocally"));
        if (c.Token.Length == 0) throw new InvalidOperationException(L.T("backend.localAgent.noToken"));
        if (!await LocalAgent.IsHealthyAsync(c.ListenPort))
        {
            FiddlerGuard();
            if (Interlocked.Exchange(ref _serverJobBusy, 1) != 0) throw new InvalidOperationException(L.T("backend.server.jobBusy"));
            // Off the message thread, as localagent.start: when the install's record of its Python is gone the start looks
            // for one (FindPython's probes, 8 s at most each), and awaited from here that work would run on this thread
            // whenever the start's first step had already finished.
            try { await Task.Run(() => LocalAgent.StartAsync(c.ListenPort, s => Log.Info("local agent enter: " + s))); }
            finally { Volatile.Write(ref _serverJobBusy, 0); }
        }
        // Read ONCE: with "Keep .env settings" on the host comes out of the stacks' .env files at every
        // read, and the connection saved below and the server address must hold the same one.
        string host = c.Host;
        var cfg = new ServerConfig(host, 22, "root", "", c.Token, c.ListenPort, "direct");
        try
        {
            await using var a = new AgentClient(cfg);
            await a.ConnectAsync();
            await a.StatusAsync();
        }
        catch (AgentHttpException ex) when (ex.Status == 401)
        {
            // The running process was started with another config (a hand edit after the start).
            throw new InvalidOperationException(L.T("backend.localAgent.tokenMismatch"));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(L.T("backend.agentUnreachable", new { detail = ex.Message }));
        }
        // From here on: the install's tail, verbatim (StartLocalAgentInstall) — one step under the state's lock.
        lock (RelicState.Gate)
        {
            ServerConfigStore.Save(cfg);
            RecordSaved(cfg);
            ApplyInstalledAgent(host, c.ListenPort);
        }
        Log.Info($"local agent enter: {host}:{c.ListenPort}");
        return new { state = BuildInitState(), warning = (string?)null };
    }

    /// <summary>Remove the agent from this PC (start screen link or the local agent card). Refused
    /// while the agent runs a job (a killed bootstrap/provision/hotpatch leaves a half-done stack) and
    /// while this launcher follows one. Then <see cref="LocalAgent.Uninstall"/> — with <c>firewall</c>, every inbound rule
    /// of the agent that is there goes in one prompt, its port's and the game ports' (the report's <c>keptRules</c> names
    /// those that stay) —, every saved record of THIS
    /// agent is forgotten (<see cref="IsThisPcsAgent"/>: token match first — the host may be 127.0.0.1 or the
    /// bind IP — host+port second; a record of another server that holds this very token goes too) — in memory
    /// first, then in the file host by host (a record of it the file holds and memory does not as well), each inside
    /// a try; the records of other servers stay —, and admin mode is left when no token remains for the configured
    /// server: the UI returns to the start screen. <c>Settings.ServerHost</c> is kept — the docker stack on this PC
    /// may still run and a player can still connect to it.</summary>
    private async Task<object> LocalAgentUninstallAsync(JsonElement p)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException(L.T("backend.localAgent.windowsOnly"));
        bool firewall = Bool(p, "firewall");
        var c = LocalAgent.ReadConfig();
        if (!LocalAgent.IsInstalled && !Directory.Exists(LocalAgent.Root))
            throw new InvalidOperationException(L.T("core.deploy.notInstalledLocally"));
        int port = c?.ListenPort ?? LocalAgentPort();
        if (Interlocked.Exchange(ref _serverJobBusy, 1) != 0) throw new InvalidOperationException(L.T("backend.server.jobBusy"));
        try
        {
            // Refuse mid-job, like the Linux upgrade does. A 401 (the process runs with another token)
            // must not block the uninstall — we are about to kill it anyway; neither must an agent that
            // answers /health but not /status. Only a real "busy" object refuses.
            string? busyKind = null;
            if (c is { Token.Length: > 0 } && await LocalAgent.IsHealthyAsync(port))
            {
                try
                {
                    await using var a = new AgentClient(new ServerConfig(c.Host, 22, "root", "", c.Token, port, "direct"));
                    await a.ConnectAsync();
                    var st = await a.StatusAsync();
                    if (st.ValueKind == JsonValueKind.Object && st.TryGetProperty("busy", out var busy) && busy.ValueKind == JsonValueKind.Object)
                        busyKind = busy.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
                }
                catch (Exception ex) { Log.Info($"local agent uninstall: status probe skipped ({ex.Message})"); }
            }
            if (busyKind is not null)
                throw new InvalidOperationException(L.T("backend.localAgent.jobRunning", new { kind = busyKind }));
            // Off the UI thread: the agent root holds the hotpatch mirror (GBs in thousands of files,
            // each delete retried once), and the whole tree is walked entry by entry. On the WebView2
            // message thread that is a frozen window for as long as it takes. _serverJobBusy is
            // already held around this, so the rpc still answers exactly once.
            // The firewall removal (when ticked) deletes every rule of the agent that is there in one prompt — its port's
            // and the game ports' of a server other PCs join; the rules still there afterwards are named (keptRules).
            var rep = await Task.Run(() => LocalAgent.Uninstall(port, firewall, s => Log.Info("local agent uninstall: " + s)));
            // Every record of this agent goes: memory first (what every following call reads), then the file,
            // host by host — a record that cannot be removed there is one log line, never a failed uninstall. One step
            // under the state's lock with the mode below, nothing awaited: a Delete all lands before or after it whole.
            // The config's host may read the stacks' files: told apart before the records' own lock is taken.
            lock (RelicState.Gate)
            {
                var mine = _records.Where(r => IsThisPcsAgent(r, c, port)).ToList();
                var gone = DropRecords(mine.Contains);
                // ...and the file's own records of it that memory does not hold (server.json could not be read when the
                // launcher started): one left there would lend this agent's dead token at the next start.
                foreach (var record in ServerConfigStore.LoadAll())
                    if (IsThisPcsAgent(record, c, port) && !gone.Any(g => ServerConfig.SameHost(g.Host, record.Host))) gone.Add(record);
                foreach (string host in gone.Select(r => r.Host).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try { ServerConfigStore.Forget(host); }
                    catch (Exception ex) { Log.Error("local agent uninstall: forgetting a saved connection of this agent", ex); }
                }
                if (gone.Count > 0) Log.Info($"local agent uninstall: {gone.Count} saved connection(s) of this agent forgotten");
                if (IsAdminMode && AdminServer() is null)
                {
                    _state.Settings.Mode = "";
                    _state.Save();
                }
            }
            return new
            {
                report = new
                {
                    stopped = rep.Stopped,
                    autostartRemoved = rep.AutostartRemoved,
                    firewallRemoved = rep.FirewallRemoved,
                    firewallError = rep.FirewallError,
                    // The names of the firewall rules of the agent still there (not asked to go, or not removed).
                    keptRules = rep.KeptRules ?? Array.Empty<string>(),
                    filesRemoved = rep.FilesRemoved,
                    leftovers = rep.Leftovers,
                },
                state = BuildInitState(),
            };
        }
        finally { Volatile.Write(ref _serverJobBusy, 0); }
    }

    /// <summary>Is <paramref name="cfg"/> a connection to the agent installed on THIS PC (config
    /// <paramref name="c"/>, listening on <paramref name="port"/>)? The token first — the saved host may be
    /// 127.0.0.1 or the bind IP, whichever the install chose — then the same host + port. Shared by the
    /// uninstall (forget every saved record of this agent) and the Agent settings card
    /// (<c>local</c>: offer a folder picker on this PC).</summary>
    private static bool IsThisPcsAgent(ServerConfig? cfg, LocalAgentConfig? c, int port) =>
        cfg is not null && ((c is not null && c.Token.Length > 0 && cfg.AgentToken == c.Token)
            || (ServerConfig.SameHost(cfg.Host, c?.Host ?? "127.0.0.1") && cfg.AgentPort == port));

    // ── agent settings ──

    /// <summary><c>agent.config.get</c>: the agent's settings (GET /agent/config) plus <c>local</c>. A 404 is an
    /// agent with no such route and comes back as <c>{tooOld: true}</c> for the card to say
    /// "update the agent" — not as an error toast.</summary>
    private async Task<object?> AgentConfigGetAsync()
    {
        JsonElement cfg;
        try { cfg = (JsonElement)(await WithAgent(a => a.AgentConfigGetAsync()))!; }
        catch (AgentHttpException ex) when (ex.Status == 404) { return new { tooOld = true }; }
        return AgentConfigReply(cfg);
    }

    /// <summary><c>agent.config.set</c>: write settings through POST /agent/config; the reply is the agent's (the
    /// GET body + applied / restartRequired) plus <c>local</c>. Refusals (400 naming the key, 409 for an
    /// environment-overridden key or a file the agent cannot write) surface with the agent's own text.</summary>
    private async Task<object?> AgentConfigSetAsync(IReadOnlyDictionary<string, string> set)
    {
        var cfg = (JsonElement)(await WithAgent(a => a.AgentConfigSetAsync(set)))!;
        return AgentConfigReply(cfg);
    }

    /// <summary>The <c>set</c> object of an <c>agent.config.set</c> payload as the strings the agent expects: a
    /// string as is, a bool as "1"/"0", a number in invariant culture (an integral value without a decimal
    /// part), null as "" (= remove the key's line). Anything else goes out as its raw JSON text so the agent
    /// refuses it with a 400 that names the key, instead of the change vanishing here. Absent / not an object
    /// = an empty set, which the agent refuses (400) too.</summary>
    private static Dictionary<string, string> AgentConfigValues(JsonElement p)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("set", out var set) || set.ValueKind != JsonValueKind.Object)
            return d;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var kv in set.EnumerateObject())
        {
            var v = kv.Value;
            d[kv.Name] = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                JsonValueKind.Null => "",
                JsonValueKind.Number => v.TryGetInt64(out long l) ? l.ToString(inv)
                    : v.TryGetDecimal(out decimal m) ? (m == decimal.Truncate(m) ? decimal.Truncate(m).ToString(inv) : m.ToString(inv))
                    : v.GetRawText(),
                _ => v.GetRawText(),
            };
        }
        return d;
    }

    /// <summary>The agent's /agent/config answer with <c>local</c> added: true when the configured admin server is
    /// the agent installed on THIS PC (<see cref="IsThisPcsAgent"/>) AND the agent says it runs on Windows —
    /// the cross-check that keeps a remote box whose admin reused this PC's token from being offered a local
    /// folder picker. A non-object answer is passed through untouched.</summary>
    private object AgentConfigReply(JsonElement cfg)
    {
        if (cfg.ValueKind != JsonValueKind.Object) return cfg;
        bool local = false;
        if (OperatingSystem.IsWindows()
            && cfg.TryGetProperty("platform", out var pl) && pl.ValueKind == JsonValueKind.String
            && string.Equals(pl.GetString(), "windows", StringComparison.OrdinalIgnoreCase))
        {
            var c = LocalAgent.ReadConfig();
            local = IsThisPcsAgent(AdminServer(), c, c?.ListenPort ?? ServerAddress.DefaultAgentPort);
        }
        var obj = JsonNode.Parse(cfg.GetRawText())!.AsObject();
        obj["local"] = local;
        return obj;
    }

    /// <summary><c>agent.restart</c>: restart the agent so its restart-kind settings apply, and wait until it
    /// answers again. Refused while this launcher follows a server job, and holds that same flag while it
    /// waits (the agent itself refuses new jobs while it restarts). <c>started</c> is read first, the restart
    /// POSTed, then /agent/config polled (<see cref="AgentClient.WaitForRestartAsync"/>: first after ~1.5 s,
    /// then every second, ≤ 60 s) until a new process answers. <c>back: false</c> is an outcome with its own
    /// <c>message</c>, not an exception: <c>restarted: false</c> = the agent answers but kept its old process (it
    /// could not start a new copy of itself — the pending settings did not apply); without <c>restarted</c> = no
    /// telling answer in time — the agent may simply take longer, or need a look on the box.</summary>
    private async Task<object> AgentRestartAsync()
    {
        var cfg = AdminServer() ?? throw new InvalidOperationException(L.T("backend.server.notConfigured"));
        if (Interlocked.Exchange(ref _serverJobBusy, 1) != 0) throw new InvalidOperationException(L.T("backend.server.jobBusy"));
        try
        {
            await using var a = new AgentClient(cfg, TunnelPinsOf(cfg));
            await a.ConnectAsync();
            string before = AgentClient.StartedOf(await a.AgentConfigGetAsync());
            PinTunnelKey(cfg, a.TunnelKey);
            await a.AgentRestartAsync();
            Log.Info($"agent restart requested: {cfg.Host}:{cfg.AgentPort} (started {before})");
            var outcome = await a.WaitForRestartAsync(before, AgentClient.RestartWait);
            Log.Info("agent restart: " + outcome switch
            {
                RestartOutcome.Back => "back",
                RestartOutcome.NotRestarted => "still the old process (it could not start a new one; see the agent log)",
                _ => "no answer within " + AgentClient.RestartWait.TotalSeconds + " s",
            });
            return outcome switch
            {
                RestartOutcome.Back => (object)new { ok = true, back = true, restarted = true },
                RestartOutcome.NotRestarted => new
                {
                    ok = true,
                    back = false,
                    restarted = false,
                    message = L.T("backend.agent.restartFailed"),
                },
                _ => new
                {
                    ok = true,
                    back = false,
                    message = L.T("backend.agent.restartTimeout", new { seconds = (int)AgentClient.RestartWait.TotalSeconds }),
                },
            };
        }
        finally { Volatile.Write(ref _serverJobBusy, 0); }
    }

    /// <summary>The relocation mode of <c>server.relocate(.check)</c>, normalised (trimmed, lowercase) but NOT
    /// defaulted: <c>move</c> moves files, so an unknown value must reach the agent as itself and be refused
    /// (400), never be turned into a move here.</summary>
    private static string RelocateMode(string raw) => (raw ?? "").Trim().ToLowerInvariant();

    // ── gameplay settings ──

    /// <summary><c>server.tweaks.get</c>: the gameplay settings of one version (GET /server/tweaks), the agent's
    /// answer as it is. The route never answers 404 for a reason of its own, so a 404 is an agent with no
    /// such route and comes back as <c>{tooOld: true}</c> for the page to say "update the agent" — not as
    /// an error toast.</summary>
    private async Task<object?> TweaksGetAsync(string version)
    {
        try { return await WithAgent(a => a.TweaksGetAsync(version)); }
        catch (AgentHttpException ex) when (ex.Status == 404) { return new { tooOld = true }; }
    }

    /// <summary><c>tweakdata.get</c>: the display catalogue of the gameplay settings (Game rules tab) — <c>{status: "ok", data}</c>
    /// with the shipped config/tweakdata.json, <c>{status: "missing"}</c> or <c>{status: "error", error}</c>
    /// (<see cref="TweakData"/>). Read off the UI thread: the file is several hundred KB and comes from the
    /// disk at every call, the page keeping what it got.</summary>
    private static async Task<object> TweakDataGetAsync()
    {
        var data = await Task.Run(() => TweakData.Load());
        if (data.Status != TweakData.StatusOk)
            Log.Info($"tweakdata: {data.Status} — {TweakData.ShippedPath}" + (data.Error is null ? "" : $" ({data.Error})"));
        return data.ToReply();
    }

    private static AgentInstallPlan ReadPlan(JsonElement p)
    {
        var plan = new AgentInstallPlan
        {
            Target = Str(p, "target").Length > 0 ? Str(p, "target") : AgentInstallPlan.TargetLinux,
            BindIp = Str(p, "bindIp").Trim(),
            AdvertisedIp = Str(p, "advertisedIp").Trim(),
            AdvertisedHost = Str(p, "advertisedHost").Trim(),
            Listen = Str(p, "listen").Trim().Length > 0 ? Str(p, "listen").Trim() : "0.0.0.0:18080",
            Token = Str(p, "token").Trim(),
            MuipHost = Str(p, "muipHost").Trim(),
            // A secret like the token: it reaches the box inside install.env / the config file only and is
            // never echoed back in deploy.done / deploy.error.
            MuipKey = Str(p, "muipKey").Trim(),
            // The two stack secrets, secrets in the same way. Blank = the plan omits the key and
            // the box keeps the value it stores; the form never gets them back (localagent.status has neither).
            MysqlPassword = Str(p, "mysqlPassword").Trim(),
            FlaskKey = Str(p, "flaskKey").Trim(),
            // "Keep .env settings", a TRI-STATE: a JSON true / false is the admin's choice and is written
            // (GIO_KEEP_ENV=1 / =0); anything else, or no field at all, is "not stated" — the plan leaves the
            // key out and the box keeps the value it stores. Never collapsed to false: that would switch the
            // option off on every box whose form could not show what it stores.
            KeepEnv = BoolOrNull(p, "keepEnv"),
            Region = Str(p, "region").Trim().Length > 0 ? Str(p, "region").Trim() : "dev_docker",
            ServerName = Str(p, "serverName").Trim(),
            UpgradeForce = Bool(p, "upgradeForce"),
            // The 7z extractor the agent on this PC unpacks the server packages with (GIO_7Z — Quick Start names
            // Relic's own 7zr.exe): a path on THIS PC, so only a Windows plan takes one (Validate refuses it in a Linux
            // plan). Blank = none named: LocalAgent.Install fills it with Relic's 7zr.exe while the agent finds no tool
            // of its own.
            SevenZip = Str(p, "sevenZip").Trim(),
        };
        // One stack folder and one choice per server version, under the field names the form derives from
        // the same list (the init state's stacks): the folder, then the choice itself.
        foreach (var v in ServerVersions.All)
        {
            plan.SetDir(v, Str(p, ServerVersions.FormDir(v)).Trim());
            // The explicit choice (stack<nn>) decides; a payload without it is read by its folder (one = I have it,
            // none = not now). A later / keep row names no folder: a kept folder comes from the target's stored
            // configuration.
            string choice = Str(p, ServerVersions.FormStack(v)).Trim();
            if (choice.Length > 0)
            {
                if (!StackChoices.TryParse(choice, out var c))
                    throw new InvalidOperationException(L.T("core.deploy.badStackChoice", new { version = v, value = choice }));
                plan.SetChoice(v, c);
                if (c is StackChoice.Later or StackChoice.Keep) plan.SetDir(v, "");
            }
        }
        return plan;
    }

    /// <summary>A version marked for download needs a server package to download: the catalogues this
    /// build ships (<c>agent\payloads\&lt;version&gt;\stack.json</c>, the ones an install hands the agent)
    /// must name one, or the mark is refused before anything is installed. The form draws the Download
    /// now of such a version dead, so this only answers a payload the form did not make. A build that ships
    /// no agent ships no catalogue either: what is missing there is the agent, and that is the answer —
    /// the one the install itself would give once this one had been followed.</summary>
    private static void RequireFetchPackages(AgentInstallPlan plan)
    {
        var problems = plan.FetchProblems(v => ServerVersions.Package(v));
        if (problems.Count == 0) return;
        string script = Path.Combine(LocalAgent.ShippedAgentDir, "gio_agent.py");
        if (!File.Exists(script)) throw new InvalidOperationException(L.T("core.deploy.agentMissingInBuild", new { path = script }));
        throw new InvalidOperationException(string.Join("\n", problems));
    }

    /// <summary>Install + start the agent on this PC, then point the app at it (admin mode). Ends with
    /// <c>deploy.done</c> — carrying what the Server page runs next (<see cref="InstallFollowUpAsync"/>) — or
    /// <c>deploy.error</c>, posted once the busy flag is free again: the page's first chained job must not
    /// meet the install's own "busy". The payload's <c>sevenZip</c> (<see cref="ReadPlan"/>) names the extractor the
    /// agent unpacks with; <see cref="LocalAgent.Install"/> fills it with Relic's own 7zr.exe while the agent finds
    /// none.</summary>
    private object StartLocalAgentInstall(JsonElement p)
    {
        FiddlerGuard();
        var plan = ReadPlan(p);
        RequireFetchPackages(plan);
        // Read off the payload BEFORE the job starts: p points into the JsonDocument that OnMessage
        // disposes as soon as this returns, so touching it from inside Task.Run throws ObjectDisposedException.
        bool autostart = Bool(p, "autostart");
        // A page that runs chains (a repoint, then a fetch) says so; one that does not queues the plain
        // downloads alone (deploy.done's fetch).
        bool pageRunsChain = Bool(p, "chain");
        plan.Target = AgentInstallPlan.TargetWindows;
        if (plan.Token.Length == 0) plan.Token = AgentInstallPlan.NewToken();
        if (Interlocked.Exchange(ref _serverJobBusy, 1) != 0) throw new InvalidOperationException(L.T("backend.server.jobBusy"));
        _ = Task.Run(async () =>
        {
            void log(string s) => PostEvent("deploy.log", new { target = "windows", text = s });
            object? done = null, failed = null;
            // The token leaves with a failure only once the install may have written it on this PC (its config file): a
            // failure before that — no Python, a config that cannot be read — has no token anywhere to write down.
            bool tokenWritten = false;
            try
            {
                // Off the message thread: FindPython asks Relic's own Python, every Python registry entry and py /
                // python / python3 in turn, each a process given up to 8 s.
                var python = LocalAgent.FindPython() ?? throw new InvalidOperationException(L.T("core.deploy.noPython"));
                // The agent process the pid file names BEFORE anything is written. StartAsync leaves a
                // healthy agent alone, and a process that was already there read its configuration when it
                // started: when StartAsync hands back that same pid, the file this install writes ("Keep
                // .env settings" included) takes effect only once the agent is restarted. The log says so
                // and the restart is left to the admin — it would kill a job the agent may be running.
                var before = LocalAgent.RunningPid();
                // ...and the token that process answers to: the file this install writes may name another one.
                // Read the way the install reads the file — a moment's lock waited out, a file that stays
                // unreadable refused before anything is written —, never as "no token", which would hold
                // every download back for a token change that did not happen.
                string beforeToken = LocalAgent.ReadConfigForInstall()?.Token ?? "";
                tokenWritten = true;
                var willFetch = LocalAgent.Install(plan, python, log);
                int pid = await LocalAgent.StartAsync(plan.ListenPort, log);
                if (before.Running && pid == before.Pid) log(L.T("backend.localAgent.restartToApply"));
                bool sameProcess = before.Running && pid == before.Pid;
                if (autostart) { LocalAgent.SetAutostart(true); log(L.T("backend.localAgent.autostartOn")); }
                // Point the app at the local agent: the address the stacks publish their ports on (reachable
                // by others) only when the agent really answers on it — a wildcard bind, or a bind on that
                // very address — else loopback. Read from the config just written, which is what "Use the
                // agent on this PC" reads later (LocalAgent.ReadConfig().Host), so the two paths can never
                // save different hosts for one agent. With "Keep .env settings" off that address is the bind
                // IP the FILE holds. With it on the agent applies no bind IP, so each stack's own OUTER_IP
                // (its .env, found through the file's folders) comes first, and the stored bind IP — the
                // plan leaves GIO_BIND_IP out, the merge keeps the line — stands in only while the stacks
                // name no address of this PC they agree on. The plan's own values stand in only when the
                // file cannot be read back.
                string host = LocalAgent.ReadConfig()?.Host ?? LocalAgentConfig.HostFor(plan.BindIp, plan.ListenHost);
                var cfg = new ServerConfig(host, 22, "root", "", plan.Token, plan.ListenPort, "direct");
                // The record saved, taken into memory and applied as ONE step under the state's lock, nothing awaited.
                lock (RelicState.Gate)
                {
                    ServerConfigStore.Save(cfg);
                    RecordSaved(cfg);
                    ApplyInstalledAgent(host, plan.ListenPort);
                }
                // What the Server page runs next, decided from what the agent that answers now says: a process
                // that kept running is asked with the token it runs with, at the address saved for it (a
                // specific listen address does not answer on loopback).
                bool tokenChanged = sameProcess && !string.Equals(beforeToken, plan.Token, StringComparison.Ordinal);
                var ask = new ServerConfig(host, 22, "root", "", sameProcess ? beforeToken : plan.Token, plan.ListenPort, "direct");
                var chain = await InstallFollowUpAsync(plan, willFetch, ask, sameProcess, tokenChanged, pageRunsChain, log);
                var (fetch, steps, holds) = ChainPayload(chain);
                done = new { target = "windows", pid, token = plan.Token, fetch, chain = steps, held = holds, state = BuildInitState() };
            }
            catch (Exception ex)
            {
                Log.Error("local agent install failed", ex);
                failed = new { target = "windows", message = ex.Message, token = tokenWritten ? plan.Token : null };
            }
            finally { Volatile.Write(ref _serverJobBusy, 0); }
            // Only now: the page starts the first job of the chain as soon as it hears this.
            if (done is not null) PostEvent("deploy.done", done);
            else if (failed is not null) PostEvent("deploy.error", failed);
        });
        return new { started = true };
    }

    /// <summary>The longest the follow-up of an install waits for the agent's <c>/status</c>: the agent answered
    /// its health check a moment ago, but this PC may not reach its port at all (a firewall that drops the
    /// packets), and every answer runs <c>docker compose ls</c>, which takes seconds on a loaded box.</summary>
    private static readonly TimeSpan FollowUpStatusTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest it waits for the agent's jobs list once <c>/status</c> has answered.</summary>
    private static readonly TimeSpan FollowUpJobsTimeout = TimeSpan.FromSeconds(15);

    /// <summary>What an install hands the Server page after it: asks the agent that answers now for its
    /// <c>/status</c> — only when there is something to decide and the launcher can still ask that process,
    /// with a line in the install log and a deadline of its own (<see cref="FollowUpStatusTimeout"/>) — and
    /// lets <see cref="InstallFollowUp.Decide"/> turn the install's downloads into the page's steps. When
    /// <c>/status</c> names another job as busy, the agent's jobs list says whether a download runs beside it
    /// (<see cref="InstallFollowUp.BusyMayHideFetch"/>). Every note and every held download goes into the
    /// install log. Never throws: an agent that cannot be asked, or an answer that cannot be read, holds
    /// every download back with the reason.</summary>
    private static async Task<FetchChain> InstallFollowUpAsync(AgentInstallPlan plan, IReadOnlyList<string> downloads, ServerConfig ask,
        bool sameProcess, bool tokenChanged, bool pageRunsChain, Action<string> log)
    {
        JsonElement? status = null;
        IReadOnlyList<string>? fetching = null;
        string error = "";
        if (InstallFollowUp.NeedsStatus(plan, downloads, sameProcess, tokenChanged))
        {
            log(L.T("core.deploy.chainAsking", new { host = ask.Host, port = ask.AgentPort }));
            try
            {
                await using var a = new AgentClient(ask);
                await a.ConnectAsync();
                using (var cts = new CancellationTokenSource(FollowUpStatusTimeout))
                {
                    try { status = (await a.StatusAsync(cts.Token)).Clone(); }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        error = L.T("core.deploy.chainAskTimeout", new { seconds = (int)FollowUpStatusTimeout.TotalSeconds });
                        Log.Info($"install follow-up: the agent's status did not come within {(int)FollowUpStatusTimeout.TotalSeconds} s");
                    }
                }
                if (status is { } answered && downloads.Count > 0 && InstallFollowUp.BusyMayHideFetch(answered))
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(FollowUpJobsTimeout);
                        fetching = InstallFollowUp.RunningFetches(await a.JobsAsync(cts.Token));
                    }
                    catch (Exception ex)
                    {
                        // Not knowing it leaves the decision to /status alone: a download that runs after all
                        // makes the page's own one answer "busy", nothing worse.
                        Log.Info($"install follow-up: the agent's jobs were not read ({ex.Message})");
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Info($"install follow-up: the agent's status was not read ({ex.Message})");
            }
        }
        FetchChain chain;
        try { chain = InstallFollowUp.Decide(plan, downloads, status, sameProcess, tokenChanged, error, pageRunsChain, fetching); }
        catch (Exception ex)
        {
            Log.Error("install follow-up: deciding the downloads", ex);
            chain = new FetchChain(Array.Empty<FetchStep>(),
                plan.Versions.Where(downloads.Contains)
                    .Select(v => new FetchHeld(v, L.T("core.deploy.chainNoStatus", new { version = v, error = ex.Message }))).ToList(),
                Array.Empty<string>());
        }
        foreach (var note in chain.Notes) log(note);
        foreach (var h in chain.Held) log(h.Message);
        return chain;
    }

    /// <summary>The follow-up as <c>deploy.done</c> carries it: <c>fetch</c> = the versions of the plain
    /// downloads (what a page that runs no chains queues), <c>chain</c> = every step in order
    /// <c>{version, dir, repoint, fetch}</c>, <c>held</c> = <c>{version, message}</c> per download held back.</summary>
    private static (string[] Fetch, object[] Chain, object[] Held) ChainPayload(FetchChain chain) => (
        chain.Steps.Where(s => !s.Repoint && s.Fetch).Select(s => s.Version).ToArray(),
        chain.Steps.Select(s => (object)new { version = s.Version, dir = s.Dir, repoint = s.Repoint, fetch = s.Fetch }).ToArray(),
        chain.Held.Select(h => (object)new { version = h.Version, message = h.Message }).ToArray());

    /// <summary>Install the agent on a remote Linux box over SSH (password or key, sudo-aware), then
    /// save the connection. Streams `deploy.log`, ends with `deploy.done` (with what the Server page runs
    /// next) / `deploy.error`, posted once the busy flag is free again.
    /// <para>The box's SSH key (I58): both connections of the run are checked against the keys pinned for its host and
    /// port (<see cref="SshHostKeyCheck"/>) — a changed key ends the run before anything is uploaded —, and the key the
    /// run trusted is pinned once it succeeded. <c>hostKey {fingerprint, changed?}</c> is the key the user confirmed in
    /// Quick Start: that key and no other is trusted, and only when this session's read of the box's key saw it there; a
    /// key the pins call changed only when the user answered the changed-key warning (<c>changed</c>) for the very key
    /// that read saw as changed. Without it (the install form) a key seen for the first time is pinned by the run that
    /// succeeds.</para></summary>
    private object StartRemoteAgentInstall(JsonElement p)
    {
        var plan = ReadPlan(p);
        RequireFetchPackages(plan);
        // Read off the payload BEFORE the job starts (the JsonDocument goes when this returns).
        bool pageRunsChain = Bool(p, "chain");
        plan.Target = AgentInstallPlan.TargetLinux;
        if (plan.Token.Length == 0) plan.Token = AgentInstallPlan.NewToken();
        var ssh = new SshTarget(
            Host: Str(p, "sshHost").Trim().Length > 0 ? Str(p, "sshHost").Trim() : _state.Settings.ServerHost,
            Port: Int(p, "sshPort", 22),
            User: Str(p, "sshUser").Trim().Length > 0 ? Str(p, "sshUser").Trim() : "root",
            Password: Str(p, "sshPassword"),
            KeyPath: Str(p, "sshKeyPath").Trim(),
            KeyPassphrase: Str(p, "sshKeyPassphrase"),
            SudoPassword: Str(p, "sudoPassword"));
        if (ssh.Host.Length == 0) throw new InvalidOperationException(L.T("backend.noServer"));
        var (confirmed, trustChanged) = ConfirmedHostKey(p);
        var hostKeys = SshHostKeyCheck(ssh.Host, ssh.Port, confirmed, trustChanged);
        if (Interlocked.Exchange(ref _serverJobBusy, 1) != 0) throw new InvalidOperationException(L.T("backend.server.jobBusy"));
        _ = Task.Run(async () =>
        {
            void log(string s) => PostEvent("deploy.log", new { target = "linux", text = s });
            object? done = null, failed = null;
            try
            {
                var result = AgentDeployer.Install(ssh, plan, Path.Combine(AppContext.BaseDirectory, "agent"), log, hostKeys);
                var cfg = new ServerConfig(ssh.Host, ssh.Port, ssh.User, "", plan.Token, plan.ListenPort, "direct");
                // The run succeeded: the key it trusted is the box's from now on, pinned first — a server.json another
                // program holds cannot lose it —, then the record saved, taken into memory and applied, ONE step under the
                // state's lock, nothing awaited.
                lock (RelicState.Gate)
                {
                    PinSshHostKey(ssh.Host, ssh.Port, result.HostKeyType, result.HostKeyFingerprint);
                    ServerConfigStore.Save(cfg);
                    RecordSaved(cfg);
                    ApplyInstalledAgent(ssh.Host, plan.ListenPort);
                }
                // What the Server page runs next, decided from what the agent says after the install (the
                // installer restarted it: it knows the folders the box kept). Without an answer to /health a
                // download would be a certain failure: each is held with that reason, and the card's own
                // Download button is there once the agent is up.
                FetchChain chain;
                if (result.Warning.Length > 0)
                {
                    chain = InstallFollowUp.Decide(plan, result.FetchVersions, null, false, false, "", pageRunsChain);
                    foreach (var h in chain.Held) log(h.Message);
                }
                else chain = await InstallFollowUpAsync(plan, result.FetchVersions, cfg, false, false, pageRunsChain, log);
                var (fetch, steps, holds) = ChainPayload(chain);
                done = new { target = "linux", result.HostKeyFingerprint, token = plan.Token, warning = result.Warning, fetch, chain = steps, held = holds, state = BuildInitState() };
            }
            catch (Exception ex)
            {
                // The token goes out with the failure too: the installer may already have written it on the
                // box, and it exists nowhere else — it is generated here and filtered out of the log stream.
                Log.Error("remote agent install failed", ex);
                failed = new { target = "linux", message = ex.Message, token = plan.Token };
            }
            finally { Volatile.Write(ref _serverJobBusy, 0); }
            // Only now: the page starts the first job of the chain as soon as it hears this.
            if (done is not null) PostEvent("deploy.done", done);
            else if (failed is not null) PostEvent("deploy.error", failed);
        });
        return new { started = true };
    }

    // ── the SSH host keys of the Linux boxes (I58) ──

    /// <summary>The SSH host keys this session read of the boxes — ssh.probe's look at a key (phase <c>key</c>), and the
    /// key a phase <c>box</c> answered with — per address in the pins' one spelling (<see cref="RelicState.PinKeyFor"/>),
    /// the latest read of an address winning: what a key the user confirms is checked against
    /// (<see cref="SshHostKeyCheck"/>). Memory only, never written anywhere; under <see cref="_sshKeyReadsLock"/>.</summary>
    private readonly Dictionary<string, HostKeySeen> _sshKeyReads = new(StringComparer.Ordinal);
    private readonly object _sshKeyReadsLock = new();

    /// <summary>Notes the key this session read of the box at <paramref name="host"/>:<paramref name="port"/> and how it
    /// stood against the pins then (ssh.probe calls it with every key it answers). At most 64 addresses are kept; past
    /// that the notes start again.</summary>
    private void NoteSshKeyRead(string host, int port, HostKeySeen seen)
    {
        string key = RelicState.PinKeyFor(host, port);
        if (key.Length == 0 || seen is null || string.IsNullOrWhiteSpace(seen.Fingerprint)) return;
        lock (_sshKeyReadsLock)
        {
            if (_sshKeyReads.Count >= 64 && !_sshKeyReads.ContainsKey(key)) _sshKeyReads.Clear();
            _sshKeyReads[key] = seen;
        }
    }

    /// <summary>The key this session last read of the box at host:port (<see cref="NoteSshKeyRead"/>), or null.</summary>
    private HostKeySeen? SshKeyReadAt(string host, int port)
    {
        string key = RelicState.PinKeyFor(host, port);
        lock (_sshKeyReadsLock) return key.Length > 0 && _sshKeyReads.TryGetValue(key, out var seen) ? seen : null;
    }

    /// <summary>The SSH host keys pinned for the box at host:port (<see cref="RelicState.HostKeyPinsFor(string, int)"/>:
    /// copies, the address in the pins' one spelling), as <see cref="AgentDeployer"/> takes them.</summary>
    private List<HostKeyPin> SshPinsOf(string host, int port) =>
        _state.HostKeyPinsFor(host, port).Select(pin => new HostKeyPin(pin.Type, pin.Fingerprint)).ToList();

    /// <summary>
    /// What a run over SSH to host:port trusts (I58, <see cref="HostKeyPins.Trusts"/>): the keys pinned there — a pinned
    /// key, or a key seen for the first time, which the run pins once it succeeded; a changed key ends the run. With
    /// <paramref name="confirmed"/>, the fingerprint the user confirmed in Quick Start: that key and no other, and only
    /// when this session's read of the box's key saw that very key at that address (<see cref="NoteSshKeyRead"/>) — else
    /// <c>backend.agent.hostKeyUnread</c>, before anything connects; a key the pins call changed only with
    /// <paramref name="trustChanged"/> (the user's answer to the changed-key warning, "Trust the new key") AND when that
    /// read saw it as changed there (<see cref="HostKeyCheck.AcceptChanged"/>).
    /// </summary>
    private HostKeyCheck SshHostKeyCheck(string host, int port, string? confirmed = null, bool trustChanged = false)
    {
        var pins = SshPinsOf(host, port);
        string fingerprint = (confirmed ?? "").Trim();
        if (fingerprint.Length == 0) return new HostKeyCheck(pins);
        var read = SshKeyReadAt(host, port);
        if (read is null || !string.Equals(read.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException(L.T("backend.agent.hostKeyUnread", new { host, port }));
        return new HostKeyCheck(pins, fingerprint, AcceptChanged: trustChanged && read.Changed);
    }

    /// <summary>The key the user confirmed, as an rpc payload carries it: <c>hostKey {fingerprint, changed?}</c> — its
    /// fingerprint ("" = none) and whether the user answered the changed-key warning for it (<c>changed: true</c>, "Trust
    /// the new key"). Anything else is no confirmed key.</summary>
    private static (string Fingerprint, bool Changed) ConfirmedHostKey(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("hostKey", out var hk)
            || hk.ValueKind != JsonValueKind.Object)
            return ("", false);
        string fingerprint = Str(hk, "fingerprint").Trim();
        return (fingerprint, fingerprint.Length > 0 && Bool(hk, "changed"));
    }

    /// <summary>Pins the key a run over SSH to host:port trusted — once that run (an install, an update, ssh.probe's look
    /// at the box) succeeded — as THE key of the address (<see cref="RelicState.PinHostKey(string, int, string, string)"/>:
    /// it takes the place of the address's earlier pins, so a confirmed new key stops the old one being trusted), with the
    /// state's save, under <see cref="RelicState.Gate"/> (a caller may hold it across its whole tail; nothing is awaited).
    /// Never throws: a state file that cannot be written right now keeps the pin in memory, saved with the next
    /// save.</summary>
    private void PinSshHostKey(string host, int port, string type, string fingerprint)
    {
        lock (RelicState.Gate)
        {
            try
            {
                if (!_state.PinHostKey(host, port, type, fingerprint)) return;
                _state.Save();
            }
            catch (Exception ex)
            {
                Log.Error("saving the SSH host key of a server (it stays pinned in memory)", ex);
                return;
            }
        }
        Log.Info($"ssh host key pinned for {RelicState.PinKeyFor(host, port)}: {fingerprint}");
    }

    /// <summary>The SSH host keys a legacy <c>Mode: ssh</c> record's tunnel is checked against (I58,
    /// <see cref="AgentClient"/>): the keys pinned for its SSH host and port; null for a direct connection, which opens no
    /// SSH.</summary>
    private IReadOnlyList<HostKeyPin>? TunnelPinsOf(ServerConfig cfg) =>
        cfg.IsDirect ? null : SshPinsOf(cfg.Host, cfg.Port);

    /// <summary>Pins the key a legacy <c>Mode: ssh</c> record's tunnel took as it came — nothing was pinned for its SSH
    /// host and port (I58) — once the agent accepted the token over that connection (<paramref name="key"/> =
    /// <see cref="AgentClient.TunnelKey"/>, read after the call the agent answered: the admin login, the open key's entry,
    /// a <see cref="WithAgent"/> call, the agent's restart). From then on a changed key is refused at that address like at
    /// every other SSH connection. Nothing for a direct connection, nor once the address has a pin: the verdict of the
    /// connect is judged again against the pins as they stand now, so a pin another run wrote while the call was in
    /// flight (a key the user confirmed, an install, an update) is never replaced. Nothing either when the host's record
    /// is no longer that tunnel — a Delete of its saved server meanwhile forgot what Relic kept for it, and a pin must not
    /// outlive that. The looks and the pin are one step under <see cref="RelicState.Gate"/>.</summary>
    private void PinTunnelKey(ServerConfig cfg, HostKeySeen? key)
    {
        if (cfg.IsDirect || key is not { Verdict: HostKeyVerdict.New }) return;
        lock (RelicState.Gate)
        {
            if (RecordFor(cfg.Host) is not { IsDirect: false } now || now.Port != cfg.Port) return;
            if (HostKeyPins.Decide(SshPinsOf(cfg.Host, cfg.Port), key.Fingerprint) != HostKeyVerdict.New) return;
            PinSshHostKey(cfg.Host, cfg.Port, key.Type, key.Fingerprint);
        }
    }

    // ── agent update ──

    /// <summary><c>agent.build</c>: the build identity of the agent this launcher ships (<c>shipped</c>,
    /// null for a build without one) and the facts of the agent on this PC — <c>installed</c>, the
    /// identity of its files (<c>build</c>) and whether the configured admin server is that agent
    /// (<c>adminServer</c>: an update of it then needs no SSH). The UI compares <c>shipped</c> with
    /// <c>local.build</c> and with the <c>build</c> an agent reports in its status; they are equal or they
    /// are not — no version number is looked at. Hashed off the message thread and remembered per folder
    /// while its files keep their size and time (<see cref="AgentBuild.IdentityCached"/>).</summary>
    private Task<object> AgentBuildAsync()
    {
        var admin = AdminServer();  // on the message thread: it reads the state
        return Task.Run<object>(() =>
        {
            bool windows = OperatingSystem.IsWindows();
            if (windows) LocalAgent.RecoverCutUpdate(wait: true);
            var c = windows ? LocalAgent.ReadConfig() : null;
            bool installed = windows && LocalAgent.IsInstalled;
            return new
            {
                shipped = AgentBuild.Shipped,
                local = new
                {
                    installed,
                    build = installed ? AgentBuild.Installed : null,
                    adminServer = installed && IsThisPcsAgent(admin, c, c?.ListenPort ?? ServerAddress.DefaultAgentPort),
                },
            };
        });
    }

    /// <summary>The Start / Stop of the agent on this PC wait while its files are being replaced.</summary>
    private static void LocalAgentNotUpdating()
    {
        if (LocalAgent.Updating) throw new InvalidOperationException(L.T("backend.server.jobBusy"));
    }

    /// <summary><c>localagent.update</c>: bring the agent on this PC to the one this launcher ships
    /// (<see cref="LocalAgent.UpdateAsync"/>: its configuration is not touched, a failure puts the previous
    /// agent back). Refused while Fiddler runs — the agent is started again — and while this launcher
    /// follows a server job. Streams <c>agentupdate.log</c>, ends with <c>agentupdate.done</c> /
    /// <c>agentupdate.error</c>, posted once the busy flag is free again (a page that starts the agent next must not meet
    /// the update's own "busy"); the saved connection and the mode stay as they are.</summary>
    private object StartLocalAgentUpdate()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException(L.T("backend.localAgent.windowsOnly"));
        FiddlerGuard();
        if (Interlocked.Exchange(ref _serverJobBusy, 1) != 0) throw new InvalidOperationException(L.T("backend.server.jobBusy"));
        _ = Task.Run(async () =>
        {
            void log(string s) => PostEvent("agentupdate.log", new { target = "local", text = s });
            // The outcome, posted only once the busy flag is free again.
            Action? outcome = null;
            try
            {
                var rep = await LocalAgent.UpdateAsync(log);
                Log.Info($"local agent update: replaced={rep.Replaced} restarted={rep.Restarted} running={rep.Running} kept=[{string.Join(", ", rep.Carried)}]");
                outcome = () => PostEvent("agentupdate.done", new { target = "local", replaced = rep.Replaced, restarted = rep.Restarted, running = rep.Running, warning = (string?)null });
            }
            catch (Exception ex)
            {
                Log.Error("local agent update failed", ex);
                outcome = () => PostEvent("agentupdate.error", new { target = "local", message = ex.Message });
            }
            finally { Volatile.Write(ref _serverJobBusy, 0); }
            outcome?.Invoke();
        });
        return new { started = true };
    }

    /// <summary><c>agent.update</c>: update the agent of the configured Linux server over SSH
    /// (<see cref="AgentDeployer.Upgrade"/> — the installer as a plain upgrade: the box keeps its whole
    /// configuration, token included, so the saved connection stays valid and is not rewritten). The SSH
    /// details are asked for every time; nothing of them is stored. Both connections of the run are checked against the
    /// keys pinned for the box's host and port (I58, <see cref="SshHostKeyCheck"/>), and the key the run trusted is pinned
    /// once it succeeded. Afterwards the agent is asked through
    /// the launcher's own connection which build it runs (<see cref="ConfirmUpdatedAsync"/>). Streams
    /// <c>agentupdate.log</c>, ends with <c>agentupdate.done</c> (<c>warning</c> = what that question
    /// found) / <c>agentupdate.error</c>, posted once the busy flag is free again.</summary>
    private object StartRemoteAgentUpdate(JsonElement p)
    {
        var cfg = AdminServer() ?? throw new InvalidOperationException(L.T("backend.server.notConfigured"));
        var ssh = new SshTarget(
            Host: Str(p, "sshHost").Trim().Length > 0 ? Str(p, "sshHost").Trim() : cfg.Host,
            Port: Int(p, "sshPort", 22),
            User: Str(p, "sshUser").Trim().Length > 0 ? Str(p, "sshUser").Trim() : "root",
            Password: Str(p, "sshPassword"),
            KeyPath: Str(p, "sshKeyPath").Trim(),
            KeyPassphrase: Str(p, "sshKeyPassphrase"),
            SudoPassword: Str(p, "sudoPassword"));
        bool force = Bool(p, "upgradeForce");
        var hostKeys = SshHostKeyCheck(ssh.Host, ssh.Port);
        if (Interlocked.Exchange(ref _serverJobBusy, 1) != 0) throw new InvalidOperationException(L.T("backend.server.jobBusy"));
        _ = Task.Run(async () =>
        {
            // The installer names what it keeps of the old install ("  previous : … kept in …") right
            // before it replaces the agent's files: a run that fails after that line may have left another
            // agent on the box, one that fails before it has changed nothing — the page keeps or drops
            // what it had read from the agent accordingly.
            bool replacing = false;
            // The run's lines as they stream: the installer's refusal of a relocation that ended while it ran
            // is read from them (AgentDeployer.UpdateStoppedText).
            var lines = new List<string>();
            void log(string s)
            {
                if (s.TrimStart().StartsWith("previous :", StringComparison.Ordinal)) replacing = true;
                lines.Add(s);
                PostEvent("agentupdate.log", new { target = "remote", text = s });
            }
            // The outcome, posted only once the busy flag is free again: a page that chains a job on it (the agent
            // entered, a server job) must not meet the update's own "busy".
            Action? outcome = null;
            try
            {
                log(L.T("backend.agent.updateAsking", new { host = cfg.Host, port = cfg.AgentPort }));
                await RefuseRemoteUpdateAsync(cfg, TunnelPinsOf(cfg));
                var result = AgentDeployer.Upgrade(ssh, Path.Combine(AppContext.BaseDirectory, "agent"), force, log, hostKeys);
                // The run succeeded: the key it trusted is the box's from now on (pinned under the state's lock).
                PinSshHostKey(ssh.Host, ssh.Port, result.HostKeyType, result.HostKeyFingerprint);
                string? warning = await ConfirmUpdatedAsync(cfg, log, TunnelPinsOf(cfg));
                Log.Info($"remote agent update: {ssh.Host} done" + (warning is null ? "" : " — " + warning));
                outcome = () => PostEvent("agentupdate.done", new { target = "remote", result.HostKeyFingerprint, warning });
            }
            // The installer stopped right before it would write the configuration — a stack folder was relocated
            // while it ran —, after its "previous :" line: the new files are in place, but nothing restarted the
            // agent. The one that answers is the one that ran before, so what the page read from it still holds
            // (replaced: false), and the update is not finished: the next one completes it.
            catch (InvalidOperationException ex) when (AgentDeployer.UpdateStoppedText(ex.Message, lines) is { } stopped)
            {
                Log.Error("remote agent update stopped before the configuration was written", ex);
                outcome = () => PostEvent("agentupdate.error", new { target = "remote", message = stopped, replaced = false });
            }
            catch (Exception ex)
            {
                Log.Error("remote agent update failed", ex);
                outcome = () => PostEvent("agentupdate.error", new { target = "remote", message = ex.Message, replaced = replacing });
            }
            finally { Volatile.Write(ref _serverJobBusy, 0); }
            outcome?.Invoke();
        });
        return new { started = true };
    }

    /// <summary>Before anything is uploaded: what the agent itself says against an update over SSH. It
    /// runs on Windows — the installer is the Linux one, and a Windows agent is updated by the launcher on
    /// the PC it runs on —, or it is in the middle of a job (the installer refuses that too, but only
    /// after its self-test, a minute in). An agent that does not answer says nothing against it: the
    /// installer's own probe on the box decides then. <paramref name="tunnelPins"/>: for a legacy tunnel, the SSH
    /// host keys the Backend holds for its address (<see cref="TunnelPinsOf"/>) — what every other connection of the
    /// configured record is checked against, a pin whose save failed included.</summary>
    private static async Task RefuseRemoteUpdateAsync(ServerConfig cfg, IReadOnlyList<HostKeyPin>? tunnelPins)
    {
        JsonElement st;
        try
        {
            await using var a = new AgentClient(cfg, tunnelPins);
            await a.ConnectAsync();
            st = await a.StatusAsync();
        }
        catch (Exception ex)
        {
            Log.Info($"remote agent update: the agent was not asked first ({ex.Message})");
            return;
        }
        if (st.ValueKind != JsonValueKind.Object) return;
        if (st.TryGetProperty("platform", out var pl) && pl.ValueKind == JsonValueKind.String
            && string.Equals(pl.GetString(), "windows", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("backend.agent.updateWindowsRemote"));
        if (st.TryGetProperty("busy", out var busy) && busy.ValueKind == JsonValueKind.Object)
        {
            string kind = busy.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "?" : "?";
            throw new InvalidOperationException(L.T("backend.agent.updateJobRunning", new { kind }));
        }
    }

    /// <summary>After an update over SSH: which build the agent this launcher is connected to runs now.
    /// The installer restarted it a moment ago, so an agent that does not answer is asked again for a
    /// minute. Null = it reports the build this launcher ships. Else the warning to show: it does not
    /// answer, or it answers with another build — the SSH address was another machine's, or that agent
    /// does not run from the files the installer replaces. <paramref name="tunnelPins"/>: for a legacy tunnel, the SSH
    /// host keys the Backend holds for its address (<see cref="TunnelPinsOf"/>) — the key the update has just pinned among
    /// them, whether or not the state file could be written.</summary>
    private static async Task<string?> ConfirmUpdatedAsync(ServerConfig cfg, Action<string> log, IReadOnlyList<HostKeyPin>? tunnelPins)
    {
        string? shipped = AgentBuild.Shipped;
        log(L.T("backend.agent.updateConfirming", new { host = cfg.Host, port = cfg.AgentPort }));
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            string failure;
            try
            {
                await using var a = new AgentClient(cfg, tunnelPins);
                await a.ConnectAsync();
                var st = await a.StatusAsync();
                string build = st.ValueKind == JsonValueKind.Object && st.TryGetProperty("build", out var b)
                    && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
                if (AgentBuild.Differs(shipped, build)) return L.T("backend.agent.updateOtherBuild", new { host = cfg.Host, port = cfg.AgentPort });
                log(L.T("backend.agent.updateConfirmed"));
                return null;
            }
            catch (Exception ex) { failure = ex.Message; }
            if (DateTime.UtcNow >= until)
                return L.T("backend.agent.updateNoAnswer", new { host = cfg.Host, port = cfg.AgentPort, detail = failure });
            await Task.Delay(2000);
        }
    }

    // ── the accounts of a server (Server → Players) ──

    /// <summary><c>server.accounts {version, after?, limit?, q?}</c>: one page of the accounts of a version
    /// (<see cref="AgentClient.AccountsAsync"/>: the whitelisted answer). <c>after</c> = the <c>next</c> of the page
    /// before, <c>limit</c> = the page size (absent = the agent's 100), <c>q</c> = a search of the login names and the
    /// nicknames. A plain rpc — never <see cref="_serverJobBusy"/>: the agent reads the list beside any job, read-only,
    /// and the page asks for it only when it opens the card, at a version chip, a search or Refresh, and after a job
    /// that changes accounts — never on a timer. A 404 is an agent without the route and comes back as
    /// <c>{tooOld: true, message}</c> for the card to say "update the agent" — not as an error toast; every other
    /// refusal is the agent's own text. Neither the answer nor the search is logged.</summary>
    private async Task<object?> AccountsGetAsync(JsonElement p)
    {
        // Read off the payload BEFORE the first await, like every handler that awaits.
        string version = Str(p, "version").Trim(), q = Str(p, "q");
        long after = WholeOr(p, "after", 0);
        int limit = Int(p, "limit", 0);
        if (version.Length == 0) throw new InvalidOperationException(L.T("backend.server.missingVersion"));
        try { return await WithAgent(a => a.AccountsAsync(version, after, limit, q)); }
        catch (AgentHttpException ex) when (ex.Status == 404) { return new { tooOld = true, message = ex.Message }; }
    }

    /// <summary>The copy request of a <c>server.accountcopy</c> payload: <c>{version, from | fromUid | fromTemplate,
    /// to | toUid, dryRun?}</c>. A key absent, null or blank is one not given; a name may also come as a JSON number
    /// (its digits: the copy route takes digits alone that name no account for a game uid), a uid as a JSON number or
    /// a string of digits, <c>dryRun</c> as a JSON bool. Anything else — or not exactly one source and one target — is
    /// refused here, before a job exists (<c>backend.account.copyInvalid</c>): a request read in a way the page did not
    /// mean could run a real copy where a dry run was asked for. No version = <c>backend.server.missingVersion</c>, the
    /// text every server job gives for it.</summary>
    private static AccountCopyRequest AccountCopyFromPayload(JsonElement p)
    {
        static InvalidOperationException Invalid() => new(L.T("backend.account.copyInvalid"));
        static bool Field(JsonElement p, string name, out JsonElement v)
        {
            v = default;
            return p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out v) && v.ValueKind != JsonValueKind.Null;
        }
        static string? Text(JsonElement p, string name, bool number)
        {
            if (!Field(p, name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && number) return v.GetRawText();
            if (v.ValueKind != JsonValueKind.String) throw Invalid();
            string text = (v.GetString() ?? "").Trim();
            return text.Length > 0 ? text : null;
        }
        static long? Uid(JsonElement p, string name)
        {
            if (!Field(p, name, out var v)) return null;
            long uid;
            if (v.ValueKind == JsonValueKind.Number)
            {
                if (!v.TryGetInt64(out uid)) throw Invalid();
            }
            else if (v.ValueKind == JsonValueKind.String)
            {
                string digits = (v.GetString() ?? "").Trim();
                if (digits.Length == 0) return null;
                if (digits.Length > 10 || !digits.All(char.IsAsciiDigit)
                    || !long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uid))
                    throw Invalid();
            }
            else throw Invalid();
            return uid is >= 1 and <= AccountCopyRequest.UidMax ? uid : throw Invalid();
        }
        string version = Str(p, "version");
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException(L.T("backend.server.missingVersion"));
        bool dryRun = Field(p, "dryRun", out var dry) && dry.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(),
        };
        var request = new AccountCopyRequest(version,
            From: Text(p, "from", number: true), FromUid: Uid(p, "fromUid"), FromTemplate: Text(p, "fromTemplate", number: false),
            To: Text(p, "to", number: true), ToUid: Uid(p, "toUid"), DryRun: dryRun);
        if (request.Problem is not null) throw Invalid();
        return request;
    }

    /// <summary>The move of a <c>server.migrate</c> payload: <c>{version, source: {version} | {dir}, accounts?: [{from, to?,
    /// overwrite?}], dryRun?, keepPassword?, keepUid?}</c>, read strictly. Every name a string, <c>source</c> one key,
    /// <c>accounts</c> an array of objects each with its <c>from</c>, every flag a JSON bool; a key absent is one not given,
    /// and a null is a value of none of those kinds (only <c>accounts</c> may be null, as absent). Anything else — a key no
    /// move has, not exactly one source, a real run that names no accounts (<see cref="MigrateRequest.Problem"/>) — is
    /// refused here, before a job exists (<c>backend.migrate.invalid</c>): a request read in a way the page did not mean
    /// could run a real move where a check was asked for. No version = <c>backend.server.missingVersion</c>, the text every
    /// server job gives for it.</summary>
    private static MigrateRequest MigrateFromPayload(JsonElement p)
    {
        static InvalidOperationException Invalid() => new(L.T("backend.migrate.invalid"));
        static void OnlyKeys(JsonElement o, params string[] keys)
        {
            if (o.ValueKind != JsonValueKind.Object || o.EnumerateObject().Any(f => !keys.Contains(f.Name))) throw Invalid();
        }
        static string? Text(JsonElement o, string name)
        {
            if (!o.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) throw Invalid();
            // (a lone surrogate of an escaped string is no text either)
            try { return v.GetString(); }
            catch (InvalidOperationException) { throw Invalid(); }
        }
        static bool Flag(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(),
        };
        OnlyKeys(p, "version", "source", "accounts", "dryRun", "keepPassword", "keepUid");
        string version = Text(p, "version") ?? "";
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException(L.T("backend.server.missingVersion"));
        if (!p.TryGetProperty("source", out var source)) throw Invalid();
        OnlyKeys(source, "version", "dir");
        if (source.EnumerateObject().Count() != 1) throw Invalid();
        List<MigrateAccount>? accounts = null;
        if (p.TryGetProperty("accounts", out var list) && list.ValueKind != JsonValueKind.Null)
        {
            if (list.ValueKind != JsonValueKind.Array) throw Invalid();
            accounts = new List<MigrateAccount>();
            foreach (var item in list.EnumerateArray())
            {
                OnlyKeys(item, "from", "to", "overwrite");
                accounts.Add(new MigrateAccount(Text(item, "from") ?? throw Invalid(), Text(item, "to"), Flag(item, "overwrite")));
            }
        }
        var request = new MigrateRequest(version, Text(source, "version"), Text(source, "dir"), accounts,
            Flag(p, "dryRun"), Flag(p, "keepPassword"), Flag(p, "keepUid"));
        if (request.Problem is not null) throw Invalid();
        return request;
    }

    /// <summary>A whole number out of an rpc payload: a JSON number, or a string of one (an &lt;input&gt; value);
    /// anything else, or none, is <paramref name="fallback"/>. Never throws — see <see cref="Int"/> for why.</summary>
    private static long WholeOr(JsonElement p, string name, long fallback)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out long n) ? n : fallback,
            JsonValueKind.String => long.TryParse((v.GetString() ?? "").Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long sn) ? sn : fallback,
            _ => fallback,
        };
    }

    // ── player accounts ──

    /// <summary>The player card's account creation, through the agent's token-less signup route
    /// (<c>/public/account/create</c>) — so the Player accounts policy and the signup limits apply. Streams
    /// <c>account.log</c>, ends with <c>account.done</c> / <c>account.error</c>; every event echoes
    /// <c>version</c>, and the UI drops one whose server changed while it ran.
    /// <para>The target server (host, game port, agent port) is captured at the START: the job can take
    /// minutes (a busy slot is waited out), and a server switch meanwhile must neither send the rest of the
    /// follow to the new server nor file this account under the new server's key. The created name is filed
    /// as this PC's login for that version, only while the configured server is still the one it was created
    /// on.</para></summary>
    private object StartAccountCreate(JsonElement p)
    {
        string version = Str(p, "version"), name = Str(p, "name").Trim(), template = Str(p, "template").Trim();
        string? password = Str(p, "password");
        if (version.Length == 0 || name.Length == 0) throw new InvalidOperationException(L.T("backend.account.missingFields"));
        if (template.Length == 0) template = "fresh";
        string host = _state.Settings.ServerHost;
        int port = _state.Settings.ServerPort, agentPort = _state.Settings.AgentPort;
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException(L.T("backend.noServer"));
        // The signup carries this launcher's id on that server: the agent counts the launcher's accounts
        // against the version's maxPerPlayer by it. Made — and, the first time, saved — before the busy flag
        // is taken: a state file that cannot be written must fail this call, not leave the flag held. The secret
        // made and its save are one step under the state's lock.
        string client;
        lock (RelicState.Gate)
        {
            bool made = string.IsNullOrEmpty(_state.ClientSecret);
            client = _state.ClientIdFor(host);
            if (made) _state.Save();
        }
        if (Interlocked.Exchange(ref _accountBusy, 1) != 0) throw new InvalidOperationException(L.T("backend.server.jobBusy"));
        _ = Task.Run(async () =>
        {
            // The outcome, posted only once the busy flag is free again: a page that creates the next account on it must
            // not meet this one's "busy".
            Action? outcome = null;
            try
            {
                using var pub = new AgentPublicClient(host, agentPort);
                var result = await pub.CreateAccountAndWaitAsync(version, name, password, template,
                    line => PostEvent("account.log", new { version, text = line }), client: client);
                string created = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("name", out var n)
                    && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()) ? n.GetString()!.Trim() : name;
                string? gen = null;
                try
                {
                    var st = await pub.StatusAsync();
                    if (st.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Object
                        && vs.TryGetProperty(version, out var vv) && vv.ValueKind == JsonValueKind.Object
                        && vv.TryGetProperty("generation", out var g) && g.ValueKind == JsonValueKind.String)
                        gen = g.GetString();
                }
                catch (Exception ex) { Log.Info($"account create: generation unknown ({ex.Message})"); }
                // Same drop rule as RememberServerAccounts: the login belongs to the server it was created
                // on, and a name filed under another server's key would be shown as THAT server's login. The look at
                // the configured server, the change and its save are one step under the state's lock.
                bool remembered;
                lock (RelicState.Gate)
                {
                    remembered = ServerConfig.SameHost(_state.Settings.ServerHost, host) && _state.Settings.ServerPort == port;
                    if (remembered)
                    {
                        _state.RememberAccount(host, port, version, created, gen);
                        _state.Save();
                    }
                }
                if (!remembered)
                    Log.Info($"account created on {host}:{port} not remembered — dropped, the configured server moved to " +
                             $"{_state.Settings.ServerHost}:{_state.Settings.ServerPort} while it ran");
                Log.Info($"account create done: {version} remembered={remembered}");
                var state = BuildInitState();
                outcome = () => PostEvent("account.done", new { version, result, state });
            }
            catch (Exception ex)
            {
                Log.Error("account create failed", ex);
                outcome = () => PostEvent("account.error", new { version, message = ex.Message });
            }
            finally { Volatile.Write(ref _accountBusy, 0); }
            outcome?.Invoke();
        });
        return new { started = true };
    }

    /// <summary>The tail of an ADMIN account creation (<c>server.accountcreate</c>) with "Show it as my login
    /// on this PC": file the created name as this launcher's own login for that version, on the server it
    /// was created on (<paramref name="host"/>:<paramref name="port"/>, captured when the job started).
    /// Dropped — like <see cref="RememberServerAccounts"/> drops a late status answer — when the configured
    /// server moved meanwhile. Never throws: the account WAS created, and the job's result (the generated
    /// password included) must still reach the admin. Nothing about the password is logged or kept.</summary>
    private async Task RememberAdminCreatedAsync(AgentClient a, JsonElement snap, string version, string typed,
        string host, int port)
    {
        try
        {
            // RunJobAsync hands back the job's last snapshot {state, result: {name, …}}; an agent that did the
            // work inside the POST would return the result object itself. The agent's spelling wins over what
            // was typed (it is the name the player logs in with).
            static string? NameOf(JsonElement e) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(n.GetString()) ? n.GetString()!.Trim() : null;
            string created = (snap.ValueKind == JsonValueKind.Object && snap.TryGetProperty("result", out var r) ? NameOf(r) : null)
                ?? NameOf(snap) ?? typed;
            // The generation the Library compares against (/public/status "generation" = provisionedAt): a
            // re-provision with the default save wipes player accounts, and the stale login is then flagged.
            string? gen = null;
            try
            {
                var st = await a.StatusAsync();
                if (st.ValueKind == JsonValueKind.Object && st.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Object
                    && vs.TryGetProperty(version, out var vv) && vv.ValueKind == JsonValueKind.Object
                    && vv.TryGetProperty("provisionedAt", out var g) && g.ValueKind == JsonValueKind.String)
                    gen = g.GetString();
            }
            catch (Exception ex) { Log.Info($"admin account create: generation unknown ({ex.Message})"); }
            // The look at the configured server, the change and its save: one step under the state's lock.
            lock (RelicState.Gate)
            {
                if (!ServerConfig.SameHost(_state.Settings.ServerHost, host) || _state.Settings.ServerPort != port)
                {
                    Log.Info($"admin-created account on {host}:{port} not remembered — dropped, the configured server moved to " +
                             $"{_state.Settings.ServerHost}:{_state.Settings.ServerPort} while it ran");
                    return;
                }
                _state.RememberAccount(host, port, version, created, gen);
                _state.Save();
            }
            Log.Info($"admin-created account remembered as this PC's login: {version} on {host}:{port}");
        }
        catch (Exception ex) { Log.Error("remembering the admin-created account (non-fatal)", ex); }
    }

    private async Task<object?> PublicStatusAsync()
    {
        using var pub = PublicClient();
        return await pub.StatusAsync();
    }

    /// <summary>Every account this launcher created on the CURRENT server, per version id, oldest first — the
    /// player card lists them (several per player, up to the server's maxPerPlayer). Read under
    /// <see cref="RelicState.Gate"/>: an account an install or a job files meanwhile on another thread never changes a
    /// collection while it is enumerated here.</summary>
    private Dictionary<string, object> AccountListsForCurrentServer()
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        lock (RelicState.Gate)
        {
            string host = _state.Settings.ServerHost;
            int port = _state.Settings.ServerPort;
            string key = $"{host}:{port}";
            var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_state.AccountLists.TryGetValue(key, out var lists)) versions.UnionWith(lists.Keys);
            if (_state.Accounts.TryGetValue(key, out var cur)) versions.UnionWith(cur.Keys);
            foreach (var v in versions)
                d[v] = _state.AccountListFor(host, port, v)
                    .Select(a => new { name = a.Name, createdAt = a.CreatedAt, generation = a.Generation }).ToList();
        }
        return d;
    }

    /// <summary>The login shown per version on the CURRENT server (one of <see cref="AccountListsForCurrentServer"/>), read
    /// under <see cref="RelicState.Gate"/>.</summary>
    private Dictionary<string, object> AccountsForCurrentServer()
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        lock (RelicState.Gate)
        {
            string key = $"{_state.Settings.ServerHost}:{_state.Settings.ServerPort}";
            if (_state.Accounts.TryGetValue(key, out var per))
                foreach (var kv in per)
                    d[kv.Key] = new { name = kv.Value.Name, createdAt = kv.Value.CreatedAt, generation = kv.Value.Generation };
        }
        return d;
    }

    /// <summary>The CURRENT server's last answer about its pre-made account per version id ("" = it said
    /// there is none); empty until the server answered once. Read under <see cref="RelicState.Gate"/>: a status poll
    /// files a new answer on another thread.</summary>
    private Dictionary<string, string> ServerAccountsForCurrentServer()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lock (RelicState.Gate)
        {
            string key = $"{_state.Settings.ServerHost}:{_state.Settings.ServerPort}";
            if (_state.ServerAccounts.TryGetValue(key, out var per))
                foreach (var kv in per) d[kv.Key] = kv.Value ?? "";
        }
        return d;
    }

    private static bool Bool(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Tri-state read: true / false for a JSON bool, null when the field is absent or not a bool.
    /// <see cref="Bool"/> collapses "absent" into false, which is wrong for a field whose absence means
    /// "no decision" (server.setup's pathfinding, the install form's keepEnv).</summary>
    private static bool? BoolOrNull(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;
}
