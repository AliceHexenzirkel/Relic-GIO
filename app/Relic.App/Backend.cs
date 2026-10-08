using System.Text.Json;
using Relic.Core.Fiddler;
using Relic.Core.Install;
using Relic.Core.Isolation;
using Relic.Core.Launch;
using Relic.Core.Play;
using Relic.Core.Server;
using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.App;

/// <summary>
/// Routes UI messages ({id, type, payload}) to the Core services and replies with {id, ok, result, error}.
/// Long-running work (install, play) replies immediately and streams progress via {event, data} messages.
/// </summary>
public sealed partial class Backend
{
    private readonly MainForm _form;
    private RelicState _state;
    private ServerConfig? _server;

    /// <summary>True when _server comes from the user's saved server.json (the token the admin logged in
    /// with on the start screen, an agent install from the app saved, or the open key the Player page
    /// entered with), false when it is the build's built-in direct-mode default (or null — dev build,
    /// unconfigured).</summary>
    private bool _serverSaved;

    /// <summary>1 while an install runs. Guards BOTH double-installs (racing RelicState mutation +
    /// Save) and playing mid-install (the game would launch from the very dir being extracted into).</summary>
    private int _installBusy;

    /// <summary>1 while a server job (start/stop/setup/provision/txt fixes/events/netfix) is being followed —
    /// and while an agent restart (agent.restart) waits for the agent to come back.</summary>
    private int _serverJobBusy;

    /// <summary>1 while an account creation through the agent's token-less signup route is being followed
    /// (<see cref="StartAccountCreate"/>). Deliberately NOT <see cref="_serverJobBusy"/>: a player's signup
    /// must not be refused because this launcher follows an admin job (or the other way round) — the agent
    /// serialises its own job slot and answers <c>server_busy</c>, which the public client waits out.</summary>
    private int _accountBusy;

    /// <summary>Number of play sessions this process is running (PLAY button or the shortcut launch
    /// run in-process) — 0 or 1 in practice, a counter so two overlapping starts (the second dies on
    /// the session gate) cannot clear each other's flag. Read by the tray "Exit completely" guard: the
    /// session's restore lives in this process, and an exit mid-session would orphan the private
    /// profile and the Fiddler proxy until the next Relic start.</summary>
    private int _playInProgress;

    /// <summary>True while a play session runs in this process (see <see cref="_playInProgress"/>).</summary>
    public bool PlayInProgress => Volatile.Read(ref _playInProgress) > 0;

    /// <summary>1 while the certificate flow runs. It borrows CustomRules.js as scratch space for up
    /// to a minute, so two of them at once would leave a Fiddler that is currently redirecting a live
    /// client with the bootstrap script loaded — no redirect, no telemetry blocks, no error.</summary>
    private int _certBusy;

    /// <summary>Cancellation for the running install. Pause and cancel are the SAME mechanic here:
    /// the download is resumable (Range + append) and the finished-zip short-circuit skips completed
    /// files, so "pause" is just a cancel whose partial cache the next install.start picks up.</summary>
    private volatile CancellationTokenSource? _installCts;

    public Backend(MainForm form)
    {
        _form = form;
        _state = RelicState.Load();
        // The C# side speaks the same language the UI will ask for (Program.Main already did this
        // from the same file; repeated here so a Backend built elsewhere is never out of sync).
        L.SetLanguage(_state.Settings.Language);
        // A state.json that outlived its game folders (leftover from an uninstall, a folder deleted
        // by hand) would otherwise show multi-GB versions as installed and offer PLAY for files
        // that are gone. Reconcile with the disk before the UI ever reads this.
        var gone = _state.PruneMissing();
        if (gone.Count > 0)
        {
            Log.Info($"state: versions with no files on disk, unregistered: {string.Join(", ", gone)}");
            _state.Save();
        }
        // Entries written by older builds carry no Origin (launcher install vs imported folder);
        // infer it once so "remove" knows whether deleting the files is the safe default.
        if (_state.FillOrigins()) _state.Save();
        // The installed voice languages are a view of the disk (one marker file per language): re-read
        // them so a state.json from an older build gets the field, and a pack added or deleted outside
        // Relic is reflected before the Library draws its Add buttons.
        if (_state.RefreshVoices()) _state.Save();
        // server.json (DPAPI) holds the token the admin logged in with (+ SSH details for a tunnel
        // override); it applies to the host it was saved for — the HOST itself always comes from
        // Settings.ServerHost (see AdminServer). Without the file the build's baked defaults apply.
        _server = ServerConfigStore.Load();
        _serverSaved = _server is not null;
        _server ??= ServerConfig.BuiltInDefault();
        // An upgrade from a build that configured the server only through server.json — adopt its host
        // when the settings carry none, so an existing install does not land on "no server".
        if (_serverSaved && string.IsNullOrWhiteSpace(_state.Settings.ServerHost) && !string.IsNullOrWhiteSpace(_server!.Host))
        {
            _state.Settings.ServerHost = _server.Host;
            _state.Settings.AgentPort = _server.AgentPort;
            _state.Save();
            Log.Info($"adopted the server host from server.json: {_server.Host}");
        }
        // Off the UI thread: recovery needs a SUSTAINED all-clear (5s of polling) plus reg.exe +
        // robocopy, and the constructor runs on the WinForms thread before the WebView has even
        // navigated — doing it inline would freeze the window on exactly the launch that follows a crash.
        // Safety is preserved by AwaitRecovery(): everything that could touch a profile waits for it.
        _recovery = Task.Run(() => RecoverCore("startup"));
        StartRecoveryLoop();
    }

    /// <summary>Completes when the CURRENT profile-recovery pass has finished. Anything that plays,
    /// installs, or reports/changes profile state must await this first — otherwise it could act on a
    /// profile the recovery is midway through restoring. Volatile because the background loop
    /// replaces it with each pass (see <see cref="StartRecoveryLoop"/>).</summary>
    private volatile Task _recovery;

    private async Task AwaitRecovery()
    {
        try { await _recovery; }
        catch (Exception ex) { Log.Error("profile recovery faulted", ex); }
    }

    /// <summary>True while a recovery pass is running — surfaced by profile.status.</summary>
    private bool Recovering => !_recovery.IsCompleted;

    /// <summary>
    /// Always-armed recovery loop. A session that ends while ANOTHER Genshin
    /// client is running (the official one started seconds after ours closed) leaves this version's
    /// profile loaded — and without this loop the only retry would be the next app
    /// start. Every 10 s: if the live profile is not active (cheap file read), run a recovery
    /// pass, which restores live (and stops a leftover Fiddler) as soon as the other client is gone.
    /// Each pass replaces <see cref="_recovery"/> so play/install/profile calls serialize behind it.
    /// </summary>
    private void StartRecoveryLoop()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(10_000).ConfigureAwait(false);
                try
                {
                    if (!_recovery.IsCompleted) continue; // a pass is already running
                    var profiles = ProfileStore.ForGenshin();
                    if (profiles.LiveIsActive()) continue; // common path: nothing to do
                    var pass = Task.Run(() => RecoverCore("loop"));
                    _recovery = pass;
                    await pass.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error("recovery loop iteration failed (non-fatal)", ex);
                }
            }
        });
    }

    private enum Recovery { Already, GateBusy, Deferred, Repaired, Restored, Discarded, Failed }

    /// <summary>
    /// The tray "Exit completely" pass: the user confirmed the exit while the live profile was not
    /// back in place (and no session runs in this process). One synchronous recovery pass — the same
    /// gate + sustained all-clear + decision table as every other pass — so a leftover Fiddler is
    /// stopped and live restored BEFORE the process that owns the recovery loop goes away. Waits for a
    /// pass already in flight first (the loop's, or the startup one) instead of colliding with it on
    /// the gate. Bounded: ~5 s of observation plus reg.exe/robocopy; a client that is running defers,
    /// exactly as the loop would, and the next Relic start finishes the job.
    /// </summary>
    public void RecoverBeforeExit()
    {
        if (PlayInProgress) return; // the session holds the gate and will do its own restore
        try { _recovery.Wait(TimeSpan.FromSeconds(20)); }
        catch (Exception ex) { Log.Error("exit: waiting for the running recovery pass (non-fatal)", ex); }
        var r = RecoverCore("exit", out string detail);
        Log.Info($"exit: recovery pass -> {r} {detail}".TrimEnd());
    }

    /// <summary>
    /// One recovery pass — shared by the constructor, the background loop and the manual
    /// "restore live" button. If a previous session ended uncleanly (crash, kill, "exit completely",
    /// shutdown) or deferred (another client was running), a version's profile may still be loaded in
    /// the registry/LocalLow. Applies the same decision table as a session end
    /// (<see cref="PlaySession.PostExitDecision"/>) against ALL registered game folders: a leftover
    /// Fiddler is stopped unless one of our own clients is running; live is restored only when no
    /// client at all is running (sustained).
    /// </summary>
    private Recovery RecoverCore(string origin, out string detail)
    {
        detail = "";
        try
        {
            var profiles = ProfileStore.ForGenshin();
            if (profiles.LiveIsActive())
                return Recovery.Already; // common path: clean marker naming live — skip the gate
            using var gate = new Semaphore(1, 1, PlaySession.SessionGateName);
            if (!gate.WaitOne(0))
            {
                Log.Info($"profile recovery ({origin}) skipped: a play session is in progress elsewhere");
                return Recovery.GateBusy;
            }
            try
            {
                // Everything is re-read INSIDE the gate: a --play session in another process may
                // have started or finished between the pre-check above and the acquire, and acting
                // on a stale marker would export the wrong data into a slot. The process check is
                // SUSTAINED, not instant: reaching this code means a session ended uncleanly or
                // deferred, so a client may be mid-respawn in a gap with no process to see.
                // (Sync-over-async is safe here — the helper uses ConfigureAwait(false) throughout.)
                var ourDirs = _state.Installed.Select(i => i.GameDir).ToArray();
                var settle = ProcessGuard.ObserveSustainedAsync(ourDirs).GetAwaiter().GetResult();
                var d = PlaySession.PostExitDecision(settle.SawOurs, settle.SawForeign, settle.SawUnknown);
                if (d.StopFiddler) StopDeferredFiddler();
                if (!d.RestoreProfile)
                {
                    // Only FOREIGN clients running (Fiddler stopped, restore deferred): the official
                    // client is running ON the still-loaded private profile right now — Relic started
                    // while it was up, or the session's own deferral is still going. The slot is a mix
                    // from here on: mark it so the restore that follows never exports it (the
                    // session end already took its snapshot; here the client may have been up for
                    // hours, so no snapshot — the slot keeps its last export).
                    if (d.StopFiddler && !profiles.IsTornLoad())
                        TaintLoaded(profiles, origin);
                    detail = d.Reason;
                    Log.Info($"profile recovery ({origin}) deferred: {d.Reason}; fiddler stopped={d.StopFiddler}");
                    return Recovery.Deferred;
                }
                if (profiles.RepairIfTorn())
                {
                    Log.Info($"profile recovery ({origin}): torn profile swap detected — discarded the mix and reloaded live");
                    return Recovery.Repaired;
                }
                string active = profiles.ActiveId();
                if (string.Equals(active, ProfileStore.LiveId, StringComparison.OrdinalIgnoreCase))
                    return Recovery.Already;
                if (profiles.IsTainted())
                {
                    // A foreign client ran on this profile (see ProfileStore.MarkTainted): live goes
                    // back WITHOUT exporting the slot, which keeps its pre-taint snapshot.
                    Log.Info($"profile recovery ({origin}): profile '{active}' still loaded and TAINTED (the official client ran on it) — restoring '{ProfileStore.LiveId}' without exporting the slot");
                    profiles.RestoreLiveDiscarding();
                    detail = active;
                    Log.Info($"profile recovery ({origin}) done (slot '{active}' kept its pre-taint snapshot)");
                    return Recovery.Discarded;
                }
                Log.Info($"profile recovery ({origin}): profile '{active}' still loaded — restoring '{ProfileStore.LiveId}'");
                profiles.SwapTo(ProfileStore.LiveId, activeId: active);
                detail = active;
                Log.Info($"profile recovery ({origin}) done");
                return Recovery.Restored;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: leave the marker in place so the next pass retries.
            Log.Error($"profile recovery ({origin}) failed", ex);
            detail = ex.Message;
            return Recovery.Failed;
        }
    }

    private void RecoverCore(string origin) => RecoverCore(origin, out _);

    /// <summary>Mark the loaded (non-live) profile tainted from a recovery pass that saw only foreign
    /// clients. Idempotent; non-fatal — the deferral stands either way and the next pass retries.</summary>
    private static void TaintLoaded(ProfileStore profiles, string origin)
    {
        try
        {
            string active = profiles.ActiveId();
            if (string.Equals(active, ProfileStore.LiveId, StringComparison.OrdinalIgnoreCase) || profiles.IsTainted(active)) return;
            profiles.MarkTainted(active);
            Log.Info($"profile recovery ({origin}): the official client is running on the loaded profile '{active}' — marked tainted, it will be restored without re-export");
        }
        catch (Exception ex) { Log.Error($"profile recovery ({origin}): marking the loaded profile tainted failed (non-fatal)", ex); }
    }

    /// <summary>A deferred session end may leave Fiddler running (its redirect rules would otherwise
    /// MITM the official client once live is back). Recovery completes that cleanup too.</summary>
    private static void StopDeferredFiddler()
    {
        try
        {
            if (!FiddlerAutomation.IsRunning) return;
            Log.Info("stopping leftover Fiddler");
            FiddlerAutomation.StopFiddler();
        }
        catch (Exception ex) { Log.Error("stopping deferred Fiddler (non-fatal)", ex); }
    }

    public async void OnMessage(string raw)
    {
        string id = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "";
            string type = root.GetProperty("type").GetString() ?? "";
            JsonElement p = root.TryGetProperty("payload", out var pp) ? pp : default;
            if (type is not ("window.drag")) Log.Info($"msg <- {type}");
            object? result = await HandleAsync(type, p);
            Reply(id, true, result, null);
        }
        catch (Exception ex)
        {
            Log.Error($"message '{id}' failed", ex);
            Reply(id, false, null, ex.Message);
        }
    }

    private void Reply(string id, bool ok, object? result, string? error)
        => _form.PostJson(new { id, ok, result, error });

    public void PostEvent(string ev, object? data)
        => _form.PostJson(new { @event = ev, data });

    private async Task<object?> HandleAsync(string type, JsonElement p)
    {
        switch (type)
        {
            case "window.minimize": _form.MinimizeWindow(); return null;
            case "window.close": _form.CloseWindow(); return null;
            case "window.drag": _form.DragWindow(); return null;

            case "app.init": return BuildInitState();

            case "settings.save":
                ApplySettings(p);
                _state.Save();
                // After Save so a language that fails to load never blocks the rest of the settings;
                // L.SetLanguage itself never throws.
                L.SetLanguage(_state.Settings.Language);
                return BuildInitState();

            case "version.select":
                _state.SelectedVersionId = Str(p, "id");
                _state.Save();
                return null;

            case "pickFolder":
                return new { path = _form.PickFolder(Str(p, "current"), Str(p, "title")) };

            case "server.status":
            {
                // Admin mode with a token: the full /status. Player mode: the agent's token-less public
                // snapshot (a different, smaller shape — the UI reads `public: true`). Either way the
                // server's word on its pre-made account is remembered (RelicState.ServerAccounts): the
                // shortcut splash and the first render before a poll must show what THIS server said,
                // never the catalogue's name for an account a keep/fixes stack does not have.
                // The target is captured BEFORE the call: a status read can sit in a 60 s timeout on a
                // dead host, and the admin may log in to another server meanwhile. Reading the
                // configured host afterwards would file server A's answer (say "aether") under server
                // B's key — the same reply-under-the-wrong-server race the UI's status epoch closes.
                string askedHost = _state.Settings.ServerHost;
                int askedPort = _state.Settings.ServerPort;
                if (IsAdminMode && AdminServer() is not null)
                {
                    var st = await WithAgent(a => a.StatusAsync());
                    if (st is JsonElement adm) RememberServerAccounts(adm, askedHost, askedPort, admin: true);
                    return st;
                }
                var pub = await PublicStatusAsync();
                if (pub is JsonElement snap) RememberServerAccounts(snap, askedHost, askedPort, admin: false);
                return new { @public = true, status = pub };
            }

            case "server.start":
                return StartServerJob("start", Str(p, "version"), (a, v, log) => a.StartServerAsync(v, log));

            case "server.stop":
                return StartServerJob("stop", Str(p, "version"), (a, v, log) => a.StopServerAsync(v, log));

            case "server.setup":
            {
                // Read off the payload BEFORE the job starts (JsonDocument lifetime — see txtfixes).
                // progress: allow-listed, anything else is the destructive default.
                // pathfinding: a TRI-STATE — absent must reach the agent as null ("not
                // given"), never as false, or every Prepare from a form that did not ask would switch
                // the service off.
                string progress = ProgressMode(Str(p, "progress"));
                bool? pathfinding = BoolOrNull(p, "pathfinding");
                return StartServerJob("setup", Str(p, "version"),
                    (a, v, log) => a.SetupServerAsync(v, progress: progress, pathfinding: pathfinding, onLine: log));
            }

            case "server.provision":
            {
                string progress = ProgressMode(Str(p, "progress"));
                return StartServerJob("provision", Str(p, "version"),
                    (a, v, log) => a.ProvisionServerAsync(v, progress: progress, onLine: log));
            }

            // Download the ready-made server package into the configured folder — a yielding
            // job the launcher follows like any other (the card offers it for an absent version).
            case "server.fetch":
                RequireAdmin();
                return StartServerJob("fetch", Str(p, "version"), (a, v, log) => a.FetchServerAsync(v, log));

            // Stop the running package download. A plain POST, NOT a job — and it has to get through
            // WHILE this launcher follows that very job, so it never goes near _serverJobBusy (same
            // reasoning as the voice cancel below). The followed job then ends as "server.error"
            // carrying the agent's "Stopped on request …"; the .part stays for the next fetch.
            case "server.fetch.cancel":
            {
                RequireAdmin();
                string version = Str(p, "version");
                if (string.IsNullOrWhiteSpace(version))
                    throw new InvalidOperationException(L.T("backend.server.missingVersion"));
                return await WithAgent(a => a.FetchCancelAsync(version));
            }

            // Run / exclude the pathfinding server (compose profile donotstart) — a job.
            case "server.pathfinding":
            {
                RequireAdmin();
                bool enabled = Bool(p, "enabled");
                return StartServerJob("pathfinding", Str(p, "version"), (a, v, log) => a.PathfindingSetAsync(v, enabled, log));
            }

            // Have the agent download the navmesh archive of a version — a job. The url, digest and size
            // come from the loaded catalogue, never from the UI: the agent verifies the file against
            // them, and the UI only names the version it wants. Read off BEFORE the job starts (the
            // JsonDocument-lifetime caveat of txtfixes below).
            case "server.navmesh":
            {
                RequireAdmin();
                string version = Str(p, "version");
                var nm = VersionCatalog.Load().FirstOrDefault(v => v.Id == version)?.Navmesh;
                if (nm is null)
                    throw new InvalidOperationException(L.T("backend.server.navmeshNotInCatalogue", new { version }));
                string url = nm.Url, sha256 = nm.Sha256;
                long size = nm.Size;
                return StartServerJob("navmesh", version, (a, v, log) => a.NavmeshFetchAsync(v, url, sha256, size, log));
            }

            // Stop the running navmesh download — a plain POST that gets through WHILE this launcher
            // follows the job (the fetch cancel's reasoning above).
            case "server.navmesh.cancel":
            {
                RequireAdmin();
                string version = Str(p, "version");
                if (string.IsNullOrWhiteSpace(version))
                    throw new InvalidOperationException(L.T("backend.server.missingVersion"));
                return await WithAgent(a => a.NavmeshCancelAsync(version));
            }

            case "server.txtfixes":
            {
                // Read off the payload BEFORE the job starts: p points into the JsonDocument that
                // OnMessage disposes as soon as this returns, and the job outlives that by minutes.
                string action = Str(p, "action") == "revert" ? "revert" : "apply";
                return StartServerJob("txtfixes", Str(p, "version"), (a, v, log) => a.TxtFixesAsync(v, action, log));
            }

            case "server.events":
                return StartServerJob("events", Str(p, "version"), (a, v, log) => a.EventsAsync(v, log));

            case "server.netfix":
                return StartServerJob("netfix", Str(p, "version"), (a, v, log) => a.NetFixAsync(v, log));

            case "server.accountcopy":
            {
                // Read off the payload BEFORE the job starts (same JsonDocument-lifetime caveat as
                // txtfixes above).
                string from = Str(p, "from"), to = Str(p, "to");
                return StartServerJob("accountcopy", Str(p, "version"),
                    (a, v, log) => a.AccountCopyAsync(v, from, to, log));
            }

            case "command.send":
                return await SendCommandAsync(p);

            // Catalogue for the command pickers, filtered to one game version (only 1.6 and 2.8
            // exist). Falls back to the selected version so an empty payload still returns something.
            case "gamedata.forVersion":
                return GameData.Load().ForVersion(
                    Str(p, "version") is { Length: > 0 } v ? v : _state.SelectedVersionId ?? "2.8");

            case "install.start":
                // No AwaitRecovery HERE: StartInstall must assign _installCts before it returns, or
                // a Pause/Cancel click during a slow (5s+) startup recovery finds nothing to cancel
                // and is silently dropped. The install task itself awaits recovery before any work.
                // A non-empty localPath = local import: the chosen folder is registered on the spot,
                // no download. `voices` = the languages to install (absent/empty = English(US), which is
                // what the dev autoinstall and an older UI send); ignored for an import, whose packs are
                // detected from the folder itself.
                StartInstall(Str(p, "versionId"), Str(p, "localPath"), Strings(p, "voices"));
                return new { started = true };

            // Add voice languages to an INSTALLED version: the same job machinery (one at a time, the
            // play-session gate, pause/cancel) and the same streamed events; install.done then carries
            // the languages added. No RequireServerAddress — nothing here templates the Fiddler rules.
            case "install.addVoices":
                StartAddVoices(Str(p, "versionId"), Strings(p, "voices"));
                return new { started = true };

            case "install.cancel":
                return CancelInstall();

            case "play.start":
                await AwaitRecovery();
                StartPlay(Str(p, "versionId"));
                return new { started = true };

            case "fiddler.trustCert":
                return await TrustCertAsync();

            case "profile.status":
                await AwaitRecovery(); // else the boot check reports the pre-recovery profile
                return ProfileStatus();

            case "profile.restoreLive":
                await AwaitRecovery();
                return await RestoreLiveAsync();

            case "startup.set":
                Startup.Set(p.TryGetProperty("enabled", out var en) && en.GetBoolean(), Environment.ProcessPath ?? "");
                return new { runAtStartup = Startup.IsEnabled() };

            // ── modes / start screen (Backend.Modes.cs) ──
            case "mode.set": return SetMode(Str(p, "mode"));
            case "server.setAddress": return await SetServerAddressAsync(Str(p, "address"));
            // agentPort (optional, 1-65535): the start screen's way to reach an agent on a non-default
            // listen port that no _relic TXT record publishes — Test dials what Enter will dial.
            case "server.test": return await TestServerAsync(Str(p, "address"), Int(p, "agentPort", 0));
            case "admin.login": return await AdminLoginAsync(Str(p, "address"), Str(p, "token"), Int(p, "agentPort", 0));
            // Neither behind RequireAdmin: the first is called from PLAYER mode, and the key the agent hands
            // out is the boundary there — exactly like admin.login's token; the second takes that key away
            // and has to work whatever mode the launcher is in (its revocation already turned it to Player).
            case "admin.openEnter": return await OpenAdminEnterAsync();
            case "admin.openForget": return OpenAdminForget(out _);
            case "login.mute": SetLoginMuted(Bool(p, "muted")); return null;
            case "login.anim": SetLoginAnimOff(Bool(p, "off")); return null;
            case "shell.openUrl": OpenUrl(Str(p, "url")); return null;
            case "help.guide": OpenGuide(); return null;
            case "pickFile":
                return new { path = _form.PickFile(Str(p, "title"), Str(p, "filter"), Str(p, "initialDir")) };

            // ── per-version removal ──
            case "version.remove":
                await AwaitRecovery();
                return StartRemove(Str(p, "versionId"), Bool(p, "deleteFiles"));

            // ── player accounts (public endpoints — no token) ──
            case "public.status": return await PublicStatusAsync();
            case "account.create": return StartAccountCreate(p);
            // Show another of this launcher's accounts on the current server as the version's login.
            case "account.use":
            {
                string version = Str(p, "version"), name = Str(p, "name").Trim();
                if (!_state.UseAccount(_state.Settings.ServerHost, _state.Settings.ServerPort, version, name))
                    throw new InvalidOperationException(L.T("backend.account.notMine"));
                _state.Save();
                return BuildInitState();
            }

            // ── admin: policy / templates / secrets / auth ──
            case "account.policy.get": RequireAdmin(); return await WithAgent(a => a.PolicyGetAsync());
            case "account.policy.set":
            {
                RequireAdmin();
                var policy = JsonSerializer.Deserialize<object>(p.GetProperty("policy").GetRawText()) ?? new { };
                return await WithAgent(a => a.PolicySetAsync(policy));
            }
            case "server.templates": RequireAdmin(); return await WithAgent(a => a.TemplatesAsync(Str(p, "version")));
            case "server.templates.ensure":
                RequireAdmin();
                return StartServerJob("templates", Str(p, "version"), (a, v, log) => a.TemplatesEnsureAsync(v, log));
            case "server.secrets.get": RequireAdmin(); return await WithAgent(a => a.SecretsGetAsync(Str(p, "version")));
            case "server.secrets.set":
            {
                RequireAdmin();
                var values = new Dictionary<string, string>();
                foreach (var k in new[] { "mysqlRoot", "flask", "internal", "muip" })
                    if (Str(p, k).Length > 0) values[k] = Str(p, k);
                return StartServerJob("secrets", Str(p, "version"), (a, v, log) => a.SecretsSetAsync(v, values, log));
            }
            case "server.auth":
            {
                RequireAdmin();
                bool verify = Bool(p, "verifyPassword");
                string defPw = Str(p, "defaultPassword");
                return StartServerJob("auth", Str(p, "version"), (a, v, log) => a.AuthAsync(v, verify, defPw, log));
            }
            case "server.account.password":
            {
                RequireAdmin();
                string name = Str(p, "name"), pw = Str(p, "password");
                return StartServerJob("password", Str(p, "version"), (a, v, log) => a.AccountPasswordAsync(v, name, pw, log));
            }
            // Create an in-game account as the ADMIN (Server status → Create a player account) —
            // no policy, no quota, any template imported on the stack. A job like any other (kind
            // "accountcreate"); an older agent's 404 comes back as the "upgrade the agent" text.
            case "server.accountcreate":
            {
                RequireAdmin();
                // Read off the payload BEFORE the job starts (same JsonDocument-lifetime caveat as
                // txtfixes above). The password is passed through untouched (never trimmed, never logged);
                // empty = none (the server generates one when it verifies passwords).
                string name = Str(p, "name").Trim(), password = Str(p, "password"), template = Str(p, "template").Trim();
                if (template.Length == 0) template = "fresh";
                // remember: file the created name as THIS PC's own login for that version. Off unless asked —
                // an admin mostly creates accounts for other people, and RelicState.Accounts is the launcher
                // user's own login (the one the Library and the splash show).
                bool remember = Bool(p, "remember");
                if (name.Length == 0) throw new InvalidOperationException(L.T("backend.account.missingFields"));
                // The server the account is created on, captured NOW: the remember step runs when the job
                // ends, and the admin may have logged in to another server meanwhile.
                string host = _state.Settings.ServerHost;
                int port = _state.Settings.ServerPort;
                return StartServerJob("accountcreate", Str(p, "version"), async (a, v, log) =>
                {
                    var r = await a.AccountCreateAsync(v, name, password.Length > 0 ? password : null, template, log);
                    if (remember) await RememberAdminCreatedAsync(a, r, v, name, host, port);
                    return r;
                });
            }
            case "server.hotpatch.get": RequireAdmin(); return await WithAgent(a => a.HotpatchGetAsync(Str(p, "version")));
            // Discard an unreadable agent state file (the agent keeps a copy on the box).
            case "agent.state.reset": RequireAdmin(); return await WithAgent(a => a.StateResetAsync());
            case "server.hotpatch":
            {
                RequireAdmin();
                // Read off the payload BEFORE the job starts (same JsonDocument-lifetime caveat as
                // txtfixes above).
                bool enabled = Bool(p, "enabled"), purge = Bool(p, "purge");
                return StartServerJob("hotpatch", Str(p, "version"), (a, v, log) => a.HotpatchSetAsync(v, enabled, purge, log));
            }
            // Which voice languages the hotfix mirror serves (downloads GBs onto the box,
            // never restarts anything). "hotpatch.voice" is the UI-side job kind.
            case "server.hotpatch.voice":
            {
                RequireAdmin();
                // Read off the payload BEFORE the job starts (same JsonDocument-lifetime caveat as
                // txtfixes above). An absent key = an empty list = serve none; non-strings are ignored.
                var languages = Strings(p, "languages");
                bool purge = Bool(p, "purge");
                return StartServerJob("hotpatch.voice", Str(p, "version"),
                    (a, v, log) => a.HotpatchVoiceAsync(v, languages, purge, log));
            }
            // Stop the running voice job of a version. A plain POST, NOT a job — and it has to get through
            // WHILE this launcher follows that very job, so it never goes near _serverJobBusy: WithAgent
            // opens a client of its own (rpcs are not serialized, the follow runs on its own task). The
            // followed job then ends as "server.error" carrying the agent's "Stopped on request …".
            case "server.hotpatch.voice.cancel":
            {
                RequireAdmin();
                string version = Str(p, "version");
                if (string.IsNullOrWhiteSpace(version))
                    throw new InvalidOperationException(L.T("backend.server.missingVersion"));
                return await WithAgent(a => a.HotpatchVoiceCancelAsync(version));
            }

            // ── the agent's own settings (Agent settings card), its restart, stack relocation ──
            // An agent with no such route answers the GET with 404 → {tooOld: true} (the card says "update the
            // agent"); every other failure is an error as usual.
            case "agent.config.get": RequireAdmin(); return await AgentConfigGetAsync();
            case "agent.config.set":
            {
                RequireAdmin();
                // Read off the payload BEFORE any await (same JsonDocument-lifetime caveat as txtfixes above).
                var set = AgentConfigValues(p);
                return await AgentConfigSetAsync(set);
            }
            case "agent.restart": RequireAdmin(); return await AgentRestartAsync();
            // Dry run of a relocation: synchronous, changes nothing (the dialog's "Check" step and its
            // debounced re-check while the admin types).
            case "server.relocate.check":
            {
                RequireAdmin();
                string version = Str(p, "version"), dir = Str(p, "dir").Trim(), mode = RelocateMode(Str(p, "mode"));
                if (string.IsNullOrWhiteSpace(version))
                    throw new InvalidOperationException(L.T("backend.server.missingVersion"));
                return await WithAgent(a => a.RelocateCheckAsync(version, dir, mode));
            }
            // Change a version's stack folder — a job (kind "relocate"): move the files or only repoint the
            // agent; dir "" with repoint takes the version off the agent.
            case "server.relocate":
            {
                RequireAdmin();
                // Read off the payload BEFORE the job starts (same JsonDocument-lifetime caveat as
                // txtfixes above).
                string dir = Str(p, "dir").Trim(), mode = RelocateMode(Str(p, "mode"));
                return StartServerJob("relocate", Str(p, "version"), (a, v, log) => a.RelocateAsync(v, dir, mode, log));
            }
            // Stop a running cross-drive copy. A plain POST, NOT a job, sent while this launcher follows
            // the relocate job — so it never goes near _serverJobBusy (same reasoning as the fetch cancel).
            case "server.relocate.cancel":
            {
                RequireAdmin();
                string version = Str(p, "version");
                if (string.IsNullOrWhiteSpace(version))
                    throw new InvalidOperationException(L.T("backend.server.missingVersion"));
                return await WithAgent(a => a.RelocateCancelAsync(version));
            }

            // ── gameplay settings (the Gameplay page) ──
            // One version's settings as the agent answers them. An agent with no such route answers the GET
            // with 404 → {tooOld: true} (the page says "update the agent"); every other failure is an error
            // as usual.
            case "server.tweaks.get": RequireAdmin(); return await TweaksGetAsync(Str(p, "version"));
            // Store and apply a version's settings — a job (kind "tweaks"). The document is the page's own
            // and reaches the agent as it is ({} = everything back to the original game); the agent
            // validates it, and an agent with no such route comes back as the "update the agent" text.
            case "server.tweaks":
            {
                RequireAdmin();
                // Cloned off the payload BEFORE the job starts (same JsonDocument-lifetime caveat as
                // txtfixes above): the clone is what outlives the document p points into.
                JsonElement tweaks = p.GetProperty("tweaks").Clone();
                return StartServerJob("tweaks", Str(p, "version"), (a, v, log) => a.TweaksSetAsync(v, tweaks, log));
            }
            // The names and sprite names that page shows for the ids of the settings — the shipped
            // config/tweakdata.json as it is, or a status when it is missing or unreadable. Not behind
            // RequireAdmin: it is this build's own display data, the same for every server.
            case "tweakdata.get": return await TweakDataGetAsync();

            // ── agent installs (admin) ──
            case "agent.install": return StartRemoteAgentInstall(p);
            case "localagent.status": return await LocalAgentStatusAsync();
            case "localagent.install": return StartLocalAgentInstall(p);
            // Task.Run for all three: this handler runs on the WebView2 message thread (= the UI
            // thread), and every one of them blocks it for seconds — StartAsync asks /health for up
            // to a minute and first walks the process list, Stop kills the tree and waits for the port to
            // go quiet, OpenFirewallPort sits on a UAC prompt for up to a minute. A blocked UI thread
            // does not merely "lag": it stops the message pump, so no later rpc is answered at all and
            // screens waiting on one (the command catalogue) hang on their spinner for good.
            case "localagent.start":
            {
                FiddlerGuard();
                LocalAgentNotUpdating();
                int startPort = LocalAgentPort();
                return new { pid = await Task.Run(() => LocalAgent.StartAsync(startPort)) };
            }
            case "localagent.stop":
            {
                LocalAgentNotUpdating();
                int stopPort = LocalAgentPort();
                await Task.Run(() => LocalAgent.Stop(stopPort));
                return await LocalAgentStatusAsync();
            }
            // Neither behind RequireAdmin: the caller is on the start screen before any mode, and the
            // config token on THIS PC is the boundary — exactly like admin.login.
            case "localagent.enter": return await LocalAgentEnterAsync();
            case "localagent.uninstall": return await LocalAgentUninstallAsync(p);
            case "localagent.autostart": LocalAgent.SetAutostart(Bool(p, "enabled")); return await LocalAgentStatusAsync();
            case "localagent.firewall":
            {
                int fwPort = LocalAgentPort();
                string? err = await Task.Run(() => LocalAgent.OpenFirewallPort(fwPort));
                if (err is not null) throw new InvalidOperationException(err);
                return await LocalAgentStatusAsync();
            }
            case "localagent.openLog":
                if (File.Exists(LocalAgent.LogPath))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LocalAgent.LogPath) { UseShellExecute = true });
                return null;

            // ── agent update ──
            // The agent this launcher ships and the one installed on this PC, as build identities. No
            // gate: the start screen asks, and nothing in the answer is a secret.
            case "agent.build": return await AgentBuildAsync();
            // Not behind RequireAdmin, like localagent.enter: the start screen offers it, and the files
            // of the agent on THIS PC are the boundary.
            case "localagent.update": return StartLocalAgentUpdate();
            case "agent.update": RequireAdmin(); return StartRemoteAgentUpdate(p);

            default:
                throw new InvalidOperationException(L.T("backend.unknownMessage", new { type }));
        }
    }

    // ── state ──

    private object BuildInitState()
    {
        var catalog = VersionCatalog.Load();
        var installedIds = _state.Installed.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var os = WindowsInfo.Detect();
        string? selected = _state.SelectedVersionId
            ?? _state.Installed.FirstOrDefault()?.Id
            ?? catalog.FirstOrDefault()?.Id;

        return new
        {
            // The whole settings object (camelCased by the serializer), language included — the UI
            // initialises its own i18n layer from settings.language.
            settings = _state.Settings,
            selectedVersionId = selected,
            // Mode-aware: a player is "configured" once a server address exists; an admin needs a
            // token as well (mode is a UX gate, the token is the boundary).
            serverConfigured = IsAdminMode ? AdminServer() is not null : !string.IsNullOrWhiteSpace(_state.Settings.ServerHost),
            mode = _state.Settings.Mode,
            defaults = new
            {
                servers = BuildServers.Servers.Select(s => new { label = s.Label, host = s.Host }),
                mode = BuildServers.DefaultMode,
            },
            serverAddr = new
            {
                host = _state.Settings.ServerHost,
                port = _state.Settings.ServerPort,
                agentPort = _state.Settings.AgentPort,
                source = _state.Settings.ServerPortSource,
            },
            // hasToken: a token applies to the CONFIGURED host (stored for it, or baked into the build).
            // tokenScope: "any" (baked) | the host the stored token was saved for | "" — the start
            // screen uses it to know whether the token field may stay empty for the address typed.
            // mode: the effective connection's "direct" | "ssh" (a legacy tunnel server.json), for the
            // Server page's agent bar — no UI creates a tunnel config. openKey: the bearer in effect is
            // the key the agent hands out while its admin lets everyone administer (entered from the
            // Player page), not the agent token — the Server page words its banner by it and the option's
            // own confirm says that closing it ends this session too. Secrets never cross
            // the bridge: never the token or the SSH password themselves.
            admin = new
            {
                hasToken = AdminServer() is not null,
                tokenScope = ServerConfig.TokenScope(StoredServer()),
                mode = AdminServer()?.Mode ?? "direct",
                openKey = AdminServer()?.OpenKey == true,
            },
            loginMuted = _state.Settings.LoginMuted,
            loginAnimOff = _state.Settings.LoginAnimOff,
            // The page may boot while the window already sits in the tray (autostart "--tray", a
            // shortcut launch): the start-screen media must not play to nobody.
            windowHidden = _form.IsHiddenToTray,
            // Accounts the player created through Relic on the CURRENT server, per version: the login shown
            // (accounts) and every one of them (accountLists — several per player).
            accounts = AccountsForCurrentServer(),
            accountLists = AccountListsForCurrentServer(),
            // What the CURRENT server last said about its pre-made account per version ("" = none) —
            // the "server's word wins" rule for the first render before any status poll.
            serverAccounts = ServerAccountsForCurrentServer(),
            isWindows = OperatingSystem.IsWindows(),
            // The agent installed on THIS PC (Windows only): the start screen's "Use the agent on this
            // PC" button keys on it. Cheap facts only — two File.Exists, one small read, one registry
            // read — never a health probe: init stays synchronous.
            localAgent = OperatingSystem.IsWindows() ? LocalAgentInit() : null,
            runAtStartup = SafeStartupEnabled(),
            os = new { os.Build, os.IsWindows11, text = os.ToString() },
            // isAdmin explains the UAC prompt to the user: FiddlerSetup.exe's manifest asks for
            // "highestAvailable", so Windows only prompts for accounts in the Administrators group.
            fiddler = new
            {
                installed = FiddlerAutomation.IsInstalled,
                // Usable, not merely trusted: an expired root reported as "Yes" in Settings would send
                // the user hunting for the real reason the game cannot reach the private server.
                certTrusted = FiddlerAutomation.IsRootCertUsable(),
                isAdmin = IsAdministrator(),
            },
            // account/accountPass: the IN-GAME LOGIN account of the pre-made save for the version
            // ("aether" on 1.6, "Aetherr" on 2.8) + a password given only as an example, because the
            // server does not verify the password. An unknown account comes back as "" and the UI
            // stays silent then.
            versions = catalog.Select(v =>
            {
                var (account, passwordExample) = GameAccounts.For(v);
                return new
                {
                    v.Id, v.Title, v.Desc, v.Server,
                    installed = installedIds.Contains(v.Id),
                    // size is game + the English voices, for the version
                    // cards; the wizard's Voices step adds the other packs on top of clientSize.
                    size = Human(v.DefaultInstallSize),
                    clientSize = v.Client.Size,
                    // Every voice pack the catalogue carries, in display order, so the wizard and the
                    // Library never hard-code a language name.
                    voices = v.VoicePacks().Select(p => new
                    {
                        lang = p.Lang, size = p.Pack.Size, sizeText = Human(p.Pack.Size),
                        isDefault = p.Lang == VoiceLanguages.Default,
                    }),
                    account,
                    accountPass = passwordExample,
                    // The navmesh archive the agent can download for this version's pathfindingserver
                    // (the catalogue entry, usable whole or null — the Pathfinding server card draws its
                    // button only from this; the backend looks the url up again when the job starts).
                    navmesh = v.Navmesh is { } nm ? new { url = nm.Url, sha256 = nm.Sha256, size = nm.Size } : null,
                    // Alternative hosts this version can be pulled from, so the wizard can offer them
                    // without knowing any of them by name. Deduplicated by id because the client and
                    // every voice pack carry the same mirror; a mirror that covers only some of the
                    // files is still listed — picking it simply leaves the others on the default order.
                    mirrors = v.Client.Mirrors.Concat(v.Voices.Values.SelectMany(p => p.Mirrors))
                        .Where(m => !string.IsNullOrWhiteSpace(m.Id) && !string.IsNullOrWhiteSpace(m.Url))
                        .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new { id = g.Key, label = g.First().Label, note = g.First().Note }),
                    // Google Drive is offered only when the catalogue carries an id for this version
                    // (the private catalogue may; the public build ships none — the card stays hidden).
                    hasDrive = !string.IsNullOrWhiteSpace(v.Client.DriveId)
                               || v.Voices.Values.Any(p => !string.IsNullOrWhiteSpace(p.DriveId)),
                    // The in-game enhancements (F1 menu) exist for this version only when the build
                    // ships its inject payload entry — the Settings section and the badges key on it.
                    hasEnhancements = Enhancements.ShippedFor(v.Id),
                };
            }),
            installed = _state.Installed.Select(i => new { i.Id, i.GameDir, i.ShortcutName, i.Origin, voices = i.Voices }),
            // The allowlist in display order — the one vocabulary for catalogue keys, install requests
            // and installed[].voices, handed to the UI so it never spells a language itself.
            voiceLanguages = VoiceLanguages.All,
        };
    }

    private void ApplySettings(JsonElement p)
    {
        var s = _state.Settings;
        // serverHost / serverPort are deliberately NOT read: the UI sends the whole settings object, and
        // writing the host raw here (no parse, no TXT ports, no agent port) would be a second host writer
        // that dials the new server on the previous one's ports. The address goes through
        // server.setAddress (start screen, Settings, install wizard), admin.login or an agent install only.
        // NumOr, not TryGetInt32: the latter THROWS for a String element (see Int) and every one of these
        // comes from an <input>. A number that arrives as "21000" is parsed; anything else keeps the old value.
        if (p.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String) s.Source = src.GetString()!;
        if (p.TryGetProperty("fiddlerDecrypt", out var fd) && fd.ValueKind is JsonValueKind.True or JsonValueKind.False) s.FiddlerDecrypt = fd.GetBoolean();
        if (p.TryGetProperty("installCert", out var ic) && ic.ValueKind is JsonValueKind.True or JsonValueKind.False) s.InstallCert = ic.GetBoolean();
        if (p.TryGetProperty("fiddlerNoUac", out var fnu) && fnu.ValueKind is JsonValueKind.True or JsonValueKind.False) s.FiddlerNoUac = fnu.GetBoolean();
        if (p.TryGetProperty("createShortcut", out var cs) && cs.ValueKind is JsonValueKind.True or JsonValueKind.False) s.CreateShortcut = cs.GetBoolean();
        if (p.TryGetProperty("gentleExtract", out var ge) && ge.ValueKind is JsonValueKind.True or JsonValueKind.False) s.GentleExtract = ge.GetBoolean();
        if (p.TryGetProperty("enhancements", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False) s.Enhancements = en.GetBoolean();
        // Clamped here rather than trusted: the value reaches Task[] as a length, so a negative or
        // absurd number from a hand-edited state.json must not become an OverflowException or a
        // thousand threads. 0 stays 0 — that is "auto", resolved by InstallService.ResolveWorkers.
        if (p.TryGetProperty("extractWorkers", out var ew))
            s.ExtractWorkers = Math.Clamp(NumOr(ew, s.ExtractWorkers), 0, 32);
        if (p.TryGetProperty("installRoot", out var ir) && ir.ValueKind == JsonValueKind.String) s.InstallRoot = ir.GetString()!;
        // UI language code ("en", "ro", …): lowercased, blank = English. The C# side switches in
        // HandleAsync right after Save; the UI reloads its own dictionary from the returned state.
        if (p.TryGetProperty("language", out var lg) && lg.ValueKind == JsonValueKind.String)
        {
            string code = (lg.GetString() ?? "").Trim().ToLowerInvariant();
            s.Language = code.Length > 0 ? code : "en";
        }
    }

    // ── server / agent ──

    private async Task<object?> WithAgent(Func<AgentClient, Task<JsonElement>> op)
    {
        // Always the effective connection for the CONFIGURED host (AdminServer): a token stored for
        // another server, or a stale host inside server.json, must never send a call elsewhere.
        var cfg = AdminServer();
        if (cfg is null) throw new InvalidOperationException(L.T("backend.server.notConfigured"));
        try
        {
            await using var a = new AgentClient(cfg);
            await a.ConnectAsync();
            return await op(a);
        }
        catch (AgentHttpException ex) when (ex.Status == 401 && cfg.OpenKey)
        {
            // The agent refuses the open key: its admin closed the option (or its state was restored),
            // which revokes every launcher that entered this way at once. Back to Player mode on the
            // spot — the status poll and every admin rpc pass through here, so the first refused call is
            // the one that does it — and the UI learns it through an event, since the call that found out
            // may be a job on its own task. A refused typed or baked token keeps its own error: the admin
            // may have rotated the token on the box, and that is theirs to sort out from the start screen.
            var state = OpenAdminForget(out bool forgot);
            // Nothing forgotten = the configured server changed while this call was in flight (the start screen
            // logged into another one meanwhile): the 401 is the old server's and the key in effect is not this
            // one any more, so the call keeps its own error — an event would name the new server.
            if (!forgot) throw;
            PostEvent("admin.revoked", new { state });
            Log.Info($"open admin: the key was refused (401) by {cfg.Host}:{cfg.AgentPort} — back to Player mode");
            throw new InvalidOperationException(L.T("backend.openKeyRevoked"));
        }
    }

    /// <summary>The provisioning choice of a Prepare / Re-apply: allow-listed, so an unknown
    /// value never reaches the agent as anything but the destructive default it would have run anyway.</summary>
    private static string ProgressMode(string raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "keep" => "keep",
        "fixes" => "fixes",
        _ => "default",
    };

    /// <summary>Remember what the server just said about its pre-made in-game account, per version, and
    /// save only when an answer changed (this runs on every status poll). Admin shape: <c>account</c> is
    /// authoritative only when <c>defaultAccount</c> is a real bool — an unreadable state file answers
    /// null for both and must not be taken as "none". Public shape: <c>defaultAccount</c> ("" = none) for
    /// every present version. A parse problem is logged, never thrown: the status reply must still reach
    /// the UI.</summary>
    private void RememberServerAccounts(JsonElement status, string host, int port, bool admin)
    {
        try
        {
            if (status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("versions", out var versions)
                || versions.ValueKind != JsonValueKind.Object) return;
            if (string.IsNullOrWhiteSpace(host)) return;
            // The answer belongs to the server it was asked of. If the configured one moved while the
            // call was in flight (the admin logged in elsewhere), drop it rather than write it under
            // the new server's key — a wrong name here is shown as THAT server's in-game login.
            if (!ServerConfig.SameHost(_state.Settings.ServerHost, host) || _state.Settings.ServerPort != port)
            {
                Log.Info($"server account answer from {host}:{port} dropped — the configured server moved to " +
                         $"{_state.Settings.ServerHost}:{_state.Settings.ServerPort} while it was in flight");
                return;
            }
            bool changed = false;
            foreach (var v in versions.EnumerateObject())
            {
                if (v.Value.ValueKind != JsonValueKind.Object) continue;
                if (v.Value.TryGetProperty("present", out var present) && present.ValueKind == JsonValueKind.False) continue;
                string? name = null;
                if (admin)
                {
                    if (!v.Value.TryGetProperty("defaultAccount", out var da) || da.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                    name = v.Value.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.String ? acc.GetString() : "";
                }
                else if (v.Value.TryGetProperty("defaultAccount", out var pda) && pda.ValueKind == JsonValueKind.String)
                    name = pda.GetString();
                if (name is null) continue;
                changed |= _state.RememberServerAccount(host, port, v.Name, name.Trim());
            }
            if (changed) _state.Save();
        }
        catch (Exception ex) { Log.Error("remembering the server's account answer (non-fatal)", ex); }
    }

    /// <summary>
    /// Fire one of the agent's long operations and follow its job: each streamed line becomes a
    /// "server.log" event, the outcome a "server.done" or "server.error". Replies immediately —
    /// a first-time setup runs for tens of minutes, far past any message round-trip.
    /// </summary>
    private object StartServerJob(string kind, string version,
        Func<AgentClient, string, Action<string>, Task<JsonElement>> run)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException(L.T("backend.server.missingVersion"));
        // The agent refuses a concurrent job with 409 anyway; this stops a second click from opening
        // a connection (and, in ssh mode, a tunnel) only to be told so.
        if (Interlocked.Exchange(ref _serverJobBusy, 1) != 0)
            throw new InvalidOperationException(L.T("backend.server.jobBusy"));
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await WithAgent(a => run(a, version,
                    line => PostEvent("server.log", new { kind, version, text = line })));
                Log.Info($"server job done: {kind} {version}");
                PostEvent("server.done", new { kind, version, result });
            }
            catch (Exception ex)
            {
                Log.Error($"server job failed: {kind} {version}", ex);
                PostEvent("server.error", new { kind, version, message = ex.Message });
            }
            finally
            {
                Volatile.Write(ref _serverJobBusy, 0);
            }
        });
        return new { started = true, kind, version };
    }

    private async Task<object?> SendCommandAsync(JsonElement p)
    {
        string version = Str(p, "version");
        string uid = Str(p, "uid");
        string msg = BuildGmMessage(Str(p, "category"), Str(p, "value"));
        PostEvent("command.log", new { type = "cmd", text = "> " + msg });
        object? res;
        if (IsAdminMode && AdminServer() is not null)
        {
            res = await WithAgent(a => a.CommandAsync(version, uid, msg));
        }
        else
        {
            // Player mode: the agent's token-less /public/command, which the admin's policy must have
            // opened (playerCommands). The agent signs; the key never leaves the box.
            using var pub = PublicClient();
            res = await pub.CommandAsync(version, uid, msg);
        }
        PostEvent("command.log", new { type = "ok", text = L.T("backend.cmd.sent", new { version }) });
        return res;
    }

    private static string BuildGmMessage(string category, string value)
    {
        value = value.Trim();
        var data = GameData.Load();
        long Amount() => long.TryParse(value, out var n) ? n : 1;
        int Id(string resolved, string kind) => int.TryParse(resolved, out var n) ? n
            : throw new InvalidOperationException(L.T("backend.cmd.unknownEntity", new { kind, value }));
        return category switch
        {
            "exp" => GmCommands.GrantExp(Amount()),
            "mora" or "resource" => GmCommands.GiveMora(Amount()),
            "primogems" => GmCommands.GivePrimogems(Amount()),
            "genesis" => GmCommands.GiveGenesisCrystals(Amount()),
            // Original Resin: the amount is read like the others and never passes GmCommands.ResinMax.
            "resin" => GmCommands.GiveResin(Amount()),
            "level" => GmCommands.SetAdventureRank(int.TryParse(value, out var lv) ? lv : 60),
            "character" => GmCommands.AddAvatar(Id(data.ResolveAvatar(value), L.T("backend.cmd.kindCharacter"))),
            "weapon" => GmCommands.AddWeapon(Id(data.ResolveWeapon(value), L.T("backend.cmd.kindWeapon"))),
            // artifact: value is "pieceId [displayLevel]" (no whole-set command exists). The wire
            // level is 1-based (+0 = 1, +20 = 21), so the display level the user types is
            // translated by GiveArtifactPiece — "71544 20" really gives +20, not +19. A bare id or
            // any other shape passes through untouched.
            "artifact" => ArtifactPieceCommand(value),
            "raw" => value,
            // fallback: pass through if it already looks like a GM command, else treat as item id
            _ => value.Any(char.IsLetter) ? value : GmCommands.GiveItem(int.TryParse(value, out var it) ? it : 0, 1),
        };
    }

    private static string ArtifactPieceCommand(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var id) && int.TryParse(parts[1], out var lvl)
            ? GmCommands.GiveArtifactPiece(id, lvl)
            : $"equip add {value}";
    }

    // ── install / play (streamed) ──

    private void StartInstall(string versionId, string localPath = "", IReadOnlyList<string>? voices = null)
    {
        RequireServerAddress(); // the install ends by templating the Fiddler rules for the server
        bool isLocal = !string.IsNullOrWhiteSpace(localPath);
        var v = Catalogued(versionId);
        // Validated HERE so a bad choice is an rpc error, not an install.error after the 15 GB client
        // download. An import installs no pack (RegisterVersion detects what the folder holds).
        var langs = isLocal
            ? new List<string>()
            : InstallService.ResolveVoices(v, voices is { Count: > 0 } ? voices : new[] { VoiceLanguages.Default });
        string root = _state.Settings.InstallRoot;
        string cache = InstallService.CacheDir(root, v.Id);
        RunInstallJob(v, cache,
            logLine: isLocal
                ? $"install start: version={versionId} local import from {localPath}"
                : $"install start: version={versionId} voices={string.Join(",", langs)} source={_state.Settings.Source} root={root}",
            // An import downloads nothing — no space check.
            preflight: isLocal ? null : () => PreflightDisk(v, langs, voicesOnly: false, cache, InstallService.GameDir(root, v.Id)),
            work: async (svc, progress, ct) =>
            {
                if (isLocal) await svc.ImportLocalAsync(v, localPath, progress, ct);
                else await svc.InstallAsync(v, langs, progress, ct);
                return null;
            });
    }

    /// <summary>Add voice languages to an installed version — the same job as an install (one at a
    /// time, the play-session gate, pause/cancel) minus Fiddler/patch/shortcut; install.done then
    /// carries the languages added.</summary>
    private void StartAddVoices(string versionId, IReadOnlyList<string> voices)
    {
        if (voices.Count == 0) throw new InvalidOperationException(L.T("backend.install.voicesMissing"));
        var inst = _state.FindInstalled(versionId)
            ?? throw new InvalidOperationException(L.T("backend.install.notInstalled", new { versionId }));
        var v = Catalogued(versionId);
        var langs = InstallService.ResolveVoices(v, voices);
        // The cache sits INSIDE the game folder (an imported one lives wherever the user put it), so the
        // space check runs against that drive and counts only the packs not on disk yet.
        string cache = InstallService.CacheDirFor(inst);
        var missing = langs.Where(l => !VoiceLanguages.IsOnDisk(inst.GameDir, l)).ToList();
        RunInstallJob(v, cache,
            logLine: $"add voices: version={versionId} langs={string.Join(",", langs)} dir={inst.GameDir} source={_state.Settings.Source}",
            preflight: missing.Count == 0 ? null : () => PreflightDisk(v, missing, voicesOnly: true, cache, inst.GameDir),
            // The cast is what makes the non-nullable Task<IReadOnlyList<string>> of AddVoicesAsync
            // fit the delegate's nullable result (a full install reports null there): Task<T> is not
            // covariant in T's nullability, so awaiting and re-wrapping is the only quiet way.
            work: async (svc, progress, ct) => (IReadOnlyList<string>?)await svc.AddVoicesAsync(v, langs, progress, ct));
    }

    private static GameVersionInfo Catalogued(string versionId) =>
        VersionCatalog.Load().FirstOrDefault(x => x.Id == versionId)
        ?? throw new InvalidOperationException(L.T("backend.install.unknownVersion", new { versionId }));

    /// <summary>
    /// The one install job: takes the _installBusy guard, publishes _installCts, waits for the startup
    /// recovery, holds the play-session gate for the whole run, streams install.progress and ends in
    /// exactly one of install.done / install.cancelled / install.error. Shared by the full install, the
    /// local import and the add-voices job so they can never drift apart on any of those.
    /// </summary>
    /// <param name="cache">Where this job's zips live — pruned selectively after a corrupt archive.</param>
    /// <param name="preflight">Runs inside the gate, before any byte moves (the disk check); null = none.</param>
    /// <param name="work">The job itself; returns the voice languages it added (null for a full install).</param>
    private void RunInstallJob(GameVersionInfo v, string cache, string logLine, Action? preflight,
        Func<InstallService, IProgress<InstallProgress>, CancellationToken, Task<IReadOnlyList<string>?>> work)
    {
        string versionId = v.Id;
        // One install at a time: two would share this RelicState (unsynchronized List mutation +
        // racing Save) and fight over the same cache zips. Reset in the task's finally.
        if (Interlocked.Exchange(ref _installBusy, 1) != 0)
            throw new InvalidOperationException(L.T("backend.install.alreadyRunning"));
        try
        {
            // Create the progress reporter on the UI thread so its callbacks marshal to the UI context.
            var progress = new Progress<InstallProgress>(ip => PostEvent("install.progress",
                new { phase = ip.Phase.ToString(), fraction = ip.Fraction, message = ip.Message }));

            var cts = new CancellationTokenSource();
            _installCts = cts;

            _ = Task.Run(async () =>
            {
                // Startup profile recovery still runs first (it needs the same session gate) — but
                // awaited inside the task, so the rpc has already returned and _installCts is
                // cancellable during the wait.
                await AwaitRecovery();

                // Cross-process exclusion with play sessions: the desktop --play shortcut runs in
                // ANOTHER process, where _installBusy is invisible. Holding the play-session gate
                // for the whole install refuses play-during-install in BOTH directions (PlaySession
                // acquires the same gate with zero timeout), in-process and cross-process alike.
                using var gate = new Semaphore(1, 1, PlaySession.SessionGateName);
                bool acquired = false;
                try
                {
                    acquired = gate.WaitOne(0);
                    if (!acquired)
                    {
                        Log.Info($"install refused: a game session is in progress ({versionId})");
                        PostEvent("install.error", new { message = L.T("backend.install.gameRunning") });
                        return;
                    }
                    try
                    {
                        // A stop clicked while recovery was still running lands here, not mid-download.
                        cts.Token.ThrowIfCancellationRequested();
                        Log.Info(logLine);
                        preflight?.Invoke();
                        var svc = new InstallService(_state, Environment.ProcessPath ?? "");
                        // InstallService mutates + saves the SAME _state instance — no reload needed
                        // (a reload here would race the UI thread's Save and could wipe the registration).
                        var added = await work(svc, progress, cts.Token);
                        Log.Info($"install done: {versionId}{(added is null ? "" : $" (voices added: {string.Join(", ", added)})")}");
                        // BuildInitState reads _state, which the UI thread also touches — marshal it.
                        void PostDone() => PostEvent("install.done", new { versionId, voices = added, state = BuildInitState() });
                        try { _form.BeginInvoke((Action)PostDone); }
                        catch (Exception) { PostDone(); } // window going down — post directly, PostJson is safe
                    }
                    // The `when` filter keeps stray OCEs honest: only a cancel WE requested reads as
                    // pause/cancel — any other OperationCanceledException still surfaces as an error.
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        // Cache and partial files stay on disk on purpose: the next install.start
                        // resumes from them, which is what makes pause/cancel cheap to undo.
                        Log.Info($"install stopped by user: {versionId}");
                        PostEvent("install.cancelled", new { versionId });
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"install failed: {versionId}", ex);
                        // A corrupt archive means a cached zip itself is bad — delete the bad one so
                        // the retry starts clean instead of failing at extract again after hours.
                        if (ex is System.IO.InvalidDataException)
                            TryDeleteCorruptCacheZips(cache, versionId);
                        PostEvent("install.error", new { message = ex.Message });
                    }
                }
                finally
                {
                    // Null the field BEFORE disposing: CancelInstall snapshots the field, so the
                    // worst race is a Cancel() on a disposed cts — caught there, answered "false".
                    _installCts = null;
                    cts.Dispose();
                    if (acquired) gate.Release();
                    Volatile.Write(ref _installBusy, 0);
                }
            });
        }
        catch
        {
            _installCts = null; // may or may not have been set yet — clear both guards either way
            Volatile.Write(ref _installBusy, 0); // nothing was started — release the guard
            throw;
        }
    }

    /// <summary>Requests cancellation of the running install and answers whether one was running.
    /// The actual stop is confirmed by the streamed "install.cancelled" event once the task unwinds
    /// (the UI decides there whether this was a pause or a full cancel).</summary>
    private object CancelInstall()
    {
        var cts = _installCts;
        if (cts is null) return new { cancelling = false };
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { return new { cancelling = false }; } // lost the race with the install's finally
        return new { cancelling = true };
    }

    /// <summary>After an InvalidDataException at extract, delete only the UNREADABLE cached zip(s)
    /// so the retry reuses the still-valid multi-GB ones via the already-complete short-circuit
    /// (the client zip and every voice pack live in the same cache, one file each). If every zip's
    /// central directory reads fine (deep data corruption — we can't tell which one is bad), drop the
    /// whole cache: a guaranteed-clean retry beats an endless extract-fail loop.</summary>
    private static void TryDeleteCorruptCacheZips(string cache, string versionId)
    {
        try
        {
            if (!Directory.Exists(cache)) return;
            var bad = Directory.GetFiles(cache, "*.zip").Where(z => !IsReadableZip(z)).ToList();
            if (bad.Count > 0)
            {
                foreach (string zip in bad)
                {
                    File.Delete(zip);
                    Log.Info($"corrupt archive — {zip} deleted for a clean retry");
                }
            }
            else
            {
                Directory.Delete(cache, recursive: true);
                Log.Info($"corrupt archive with no identifiable culprit — the whole cache for {versionId} was deleted");
            }
        }
        catch (Exception ex) { Log.Error("cache cleanup after corrupt archive (non-fatal)", ex); }
    }

    /// <summary>True if the zip's central directory can be opened and enumerated.</summary>
    private static bool IsReadableZip(string path)
    {
        try
        {
            using var z = System.IO.Compression.ZipFile.OpenRead(path);
            return z.Entries.Count >= 0; // forces reading the central directory
        }
        catch { return false; }
    }

    /// <summary>Fail early with a clear message if there isn't room for the download + extraction.
    /// Bytes already sitting in the download cache (a resume) don't need downloading again, so they
    /// are subtracted — otherwise a legit resume gets refused for "insufficient space". EVERY selected
    /// voice pack counts: 2.8 with all four languages is ~64 GB of zips plus as much extracted, and the
    /// user has to learn that here, not from "Not enough space" after a 30 GB download.</summary>
    /// <param name="voicesOnly">An add-voices job: no client zip, and nothing already extracted is
    /// credited (the packs are the only new bytes; a torn one is rewritten in place on retry).</param>
    /// <param name="cache">The job's download cache; <paramref name="gameDir"/> the folder the files
    /// land in — its drive is the one checked (an imported install may sit on another disk).</param>
    private void PreflightDisk(GameVersionInfo v, IReadOnlyList<string> voices, bool voicesOnly, string cache, string gameDir)
    {
        try
        {
            if (!voicesOnly) Directory.CreateDirectory(_state.Settings.InstallRoot);
            var packs = voices.Select(l => (Lang: l, Pack: v.VoicePack(l)!)).Where(p => p.Pack is not null).ToList();
            long total = (voicesOnly ? 0 : v.Client.Size) + packs.Sum(p => p.Pack.Size);
            long need = total * 2; // zips + extracted
            if (!voicesOnly) need -= CappedExistingSize(InstallService.ClientCacheFile(cache, v.Id), v.Client.Size);
            foreach (var p in packs)
                need -= CappedExistingSize(InstallService.VoiceCacheFile(cache, v.Id, p.Lang), p.Pack.Size);
            // Bytes already extracted are also credited: extraction re-Creates each file IN PLACE,
            // so a resume reuses that space rather than needing it twice. Without this a cancel at
            // 90% of Extract can never resume — the drive holds almost the whole install already,
            // and preflight would demand the full extracted size on top of it.
            if (!voicesOnly) need -= ExtractedBytesOnDisk(gameDir, cache, total);
            if (need < 0) need = 0;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(gameDir))!);
            if (drive.AvailableFreeSpace < need)
                throw new InvalidOperationException(L.T("backend.install.noSpace", new
                {
                    drive = drive.Name,
                    need = need / 1_000_000_000,
                    have = drive.AvailableFreeSpace / 1_000_000_000,
                }));
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { Log.Error("PreflightDisk (non-fatal)", ex); }
    }

    /// <summary>Size of an existing partial download, capped at the source's full size.</summary>
    private static long CappedExistingSize(string path, long cap)
    {
        try { return File.Exists(path) ? Math.Min(new FileInfo(path).Length, Math.Max(cap, 0)) : 0; }
        catch { return 0; }
    }

    /// <summary>Bytes already extracted for a version: the game dir's contents EXCLUDING the
    /// download cache nested inside it, capped at the estimated extracted size. The walk stops as
    /// soon as the cap is reached — a near-complete install has tens of thousands of files.</summary>
    private static long ExtractedBytesOnDisk(string gameDir, string cacheDir, long cap)
    {
        try
        {
            if (cap <= 0 || !Directory.Exists(gameDir)) return 0;
            // Both paths come from the same InstallService helpers, so a plain prefix test is exact.
            string cachePrefix = cacheDir + Path.DirectorySeparatorChar;
            long sum = 0;
            foreach (string f in Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories))
            {
                if (f.StartsWith(cachePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                sum += new FileInfo(f).Length;
                if (sum >= cap) return cap;
            }
            return sum;
        }
        catch { return 0; }
    }

    private void StartPlay(string versionId)
    {
        RequireServerAddress();
        // Playing mid-install would launch the game from the very dir the install extracts into
        // (write-locked exe → the multi-GB install dies at extract, leaving a mixed-version dir).
        if (Volatile.Read(ref _installBusy) != 0)
            throw new InvalidOperationException(L.T("backend.play.installRunning"));
        // Counted from the click, not from inside the task: an "Exit completely" a moment later must
        // already see the session that is about to hold the gate.
        Interlocked.Increment(ref _playInProgress);
        _ = Task.Run(async () =>
        {
            try
            {
                var session = new PlaySession(_state);
                session.Log += m => { Log.Info($"[play] {m}"); PostEvent("play.log", new { text = m }); };
                // Closes the UI's "Starting the game" overlay exactly when the client really appeared.
                // enhancements: whether the F1 menu is really in the process (the toast words it).
                session.GameAppeared += () => PostEvent("play.appeared", new { versionId, enhancements = session.EnhancementsActive });
                await session.PlayAsync(versionId);
                PostEvent("play.done", new { versionId });
            }
            catch (FiddlerClosedException ex)
            {
                // Not a launch error but a session decision (the user closed Fiddler, Relic closed
                // the game) — the explanatory window in the UI, not the "failed" toast. The app
                // window may be in the tray (the game was just running) — without ShowFromTray the
                // dialog would wait unseen in a hidden window.
                Log.Info($"play ended: fiddler closed mid-session ({versionId})");
                _form.ShowFromTrayCrossThread();
                PostEvent("play.fiddlerClosed", new { versionId, message = ex.Message });
            }
            catch (Exception ex)
            {
                Log.Error($"play failed: {versionId}", ex);
                PostEvent("play.error", new { message = ex.Message });
            }
            finally
            {
                Interlocked.Decrement(ref _playInProgress);
            }
        });
    }

    /// <summary>Whether the "press F1 in game" hint applies to a launch of <paramref name="versionId"/>:
    /// the setting is on and this build ships the enhancements DLL for it. A hint, not a promise —
    /// the DLL may still turn out missing at launch time (PlaySession logs that).</summary>
    public bool EnhancementsHintFor(string versionId) =>
        _state.Settings.Enhancements && Enhancements.ShippedFor(versionId);

    /// <summary>
    /// The play session started from the desktop shortcut ("Relic.exe --play"), run IN the
    /// launcher's process — which stays hidden in the tray for as long as the game runs. It repeats
    /// exactly the same checks as the PLAY button (profile recovery at start, the install guard)
    /// and, more importantly, uses the same <see cref="RelicState"/> instance: two states loaded
    /// separately in the same process would overwrite each other on Save, which is the very reason
    /// the single-instance guard exists. Reports only through callbacks: the web window is hidden,
    /// the only visible surface is the splash, and errors are shown by the caller
    /// (<see cref="MainForm"/>) in a dialog.
    /// </summary>
    public void StartPlayFromShortcut(string versionId, Action<string> log, Action appeared, Action<Exception?> finished)
    {
        Interlocked.Increment(ref _playInProgress); // see StartPlay
        _ = Task.Run(async () =>
        {
            Exception? error = null;
            try
            {
                // The startup profile recovery holds the session gate (named semaphore) for up to
                // 5 seconds, and PlaySession asks for it with WaitOne(0) — without this wait, the
                // shortcut launch could die with "A game session is already in progress" because
                // of our own recovery.
                await AwaitRecovery();
                if (Volatile.Read(ref _installBusy) != 0)
                    throw new InvalidOperationException(L.T("backend.play.installRunning"));

                var session = new PlaySession(_state);
                session.Log += m => { Log.Info($"[play] {m}"); log(m); };
                session.GameAppeared += appeared;
                await session.PlayAsync(versionId);
            }
            catch (FiddlerClosedException ex)
            {
                Log.Info($"play ended: fiddler closed mid-session ({versionId})");
                error = ex;
            }
            catch (Exception ex)
            {
                Log.Error($"shortcut play failed: {versionId}", ex);
                error = ex;
            }
            finally
            {
                Interlocked.Decrement(ref _playInProgress);
                finished(error);
            }
        });
    }

    /// <summary>Which profile the registry + LocalLow currently hold. Surfaced so the user can see
    /// at a glance whether the OFFICIAL client would start on its own data — a deferred restore
    /// leaves a version's profile loaded, and launching live Genshin then reads private-server data.</summary>
    private object ProfileStatus()
    {
        var profiles = ProfileStore.ForGenshin();
        string active = profiles.ActiveId();
        return new
        {
            active,
            liveActive = profiles.LiveIsActive(),
            torn = profiles.IsTornLoad(),
            gameRunning = ProcessGuard.AnyRunning(),
            recovering = Recovering,
        };
    }

    /// <summary>Restore the live (official) profile on demand — one explicit recovery pass with the
    /// same safety rules as the automatic ones (session gate + sustained all-clear + the shared
    /// decision table), reported back as a message.</summary>
    private async Task<object> RestoreLiveAsync()
    {
        var pass = Task.Run(() =>
        {
            var r = RecoverCore("manual", out string detail);
            return (r, detail);
        });
        _recovery = pass;
        var (result, detail) = await pass;
        return result switch
        {
            Recovery.Already => new { restored = false, already = true, message = L.T("backend.profile.alreadyLive") },
            Recovery.GateBusy => throw new InvalidOperationException(L.T("backend.profile.gameRunning")),
            Recovery.Deferred => throw new InvalidOperationException(L.T("backend.profile.clientRunning")),
            Recovery.Repaired => new { restored = true, message = L.T("backend.profile.repaired") },
            Recovery.Restored => new { restored = true, message = L.T("backend.profile.restored", new { profile = detail }) },
            Recovery.Discarded => new { restored = true, message = L.T("backend.profile.restoredDiscarded", new { profile = detail }) },
            _ => throw new InvalidOperationException(L.T("backend.profile.restoreFailed", new { detail })),
        };
    }

    /// <summary>The "Approve the certificate" button in Settings. No certificate existing yet is
    /// no reason to give up and tell the user to go run Fiddler themselves — that is exactly the
    /// state a fresh machine is in, which is precisely when the button is needed. So it
    /// runs the same create-then-trust path as a play session. The rules rewrite mirrors
    /// <see cref="FiddlerAutomation.EnsureReadyAsync(string, int, bool, bool, bool, CancellationToken)"/>:
    /// the cert step borrows CustomRules.js, so the redirect rules are restored either way.</summary>
    private async Task<object> TrustCertAsync()
    {
        RequireServerAddress(); // the rules rewrite after the cert step needs the server host
        if (FiddlerAutomation.IsRootCertUsable())
            return new { trusted = true, already = true };
        if (Interlocked.CompareExchange(ref _certBusy, 1, 0) != 0)
            throw new InvalidOperationException(L.T("backend.cert.busy"));
        try
        {
            var s = _state.Settings;
            // The whole readiness path, not just the cert step. The button is offered even when
            // Fiddler is not installed at all, and creating a certificate means RUNNING Fiddler — so
            // it has to install it first, and write the prefs that keep its first-run dialogs from
            // stealing focus. EnsureReadyAsync also restores the redirect rules in its own finally.
            // Off the UI thread: this can take a minute, and WebView2's message loop is on it.
            await Task.Run(() => FiddlerAutomation.EnsureReadyAsync(
                s.ServerHost, s.ServerPort, s.FiddlerDecrypt, trustCert: true, s.FiddlerNoUac, CancellationToken.None));
            return new { trusted = true };
        }
        finally { Interlocked.Exchange(ref _certBusy, 0); }
    }

    // ── helpers ──

    private static string Str(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";

    /// <summary>Read an array of strings out of an rpc payload: absent / not an array = empty, and
    /// non-string elements are skipped rather than thrown on (the same tolerance as <see cref="Int"/>).</summary>
    private static List<string> Strings(JsonElement p, string name)
    {
        var list = new List<string>();
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String) list.Add(el.GetString() ?? "");
        return list;
    }

    /// <summary>Read a number out of an rpc payload. Never throws on the wrong kind: JS sends every
    /// &lt;input&gt; value as a STRING (setModel only parses when the seeded value was already a number),
    /// and <see cref="JsonElement.TryGetInt32"/> THROWS — it does not return false — when the element is
    /// not a Number. That would turn a string "22" in the deploy form's sshPort into the modal
    /// "The requested operation requires an element of type 'Number'…". A numeric string is parsed rather
    /// than dropped: falling back silently would send SSH to port 22 when the admin typed 2222.</summary>
    private static int Int(JsonElement p, string name, int fallback)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return fallback;
        return NumOr(v, fallback);
    }

    /// <summary>Kind-safe int read of a single element — see <see cref="Int"/> for why.</summary>
    private static int NumOr(JsonElement v, int fallback) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt32(out var n) ? n : fallback,
        JsonValueKind.String => int.TryParse((v.GetString() ?? "").Trim(),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
            out var sn) ? sn : fallback,
        _ => fallback,
    };

    private static bool SafeStartupEnabled()
    {
        try { return Startup.IsEnabled(); } catch { return false; }
    }

    /// <summary>True if this account is in the Administrators group — the only case where Windows
    /// raises a UAC prompt for the Fiddler installer's "highestAvailable" manifest.</summary>
    private static bool IsAdministrator()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Human-readable size, always with a '.' decimal separator — the string is shown
    /// by the UI, never parsed, but a culture-dependent separator looks wrong next to the English UI.</summary>
    private static string Human(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double n = bytes; int i = 0;
        while (n >= 1024 && i < u.Length - 1) { n /= 1024; i++; }
        return n.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + u[i];
    }
}
