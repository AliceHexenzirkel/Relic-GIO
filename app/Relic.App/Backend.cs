using System.Text.Json;
using Relic.Core.Fiddler;
using Relic.Core.Install;
using Relic.Core.Isolation;
using Relic.Core.Launch;
using Relic.Core.Play;
using Relic.Core.Server;
using Relic.Core.State;
using Relic.Core.Util;
using Recovery = Relic.Core.Play.RecoveryResult;

namespace Relic.App;

/// <summary>
/// Routes UI messages ({id, type, payload}) to the Core services and replies with {id, ok, result, error}.
/// Long-running work (install, play) replies immediately and streams progress via {event, data} messages.
/// </summary>
public sealed partial class Backend
{
    private readonly MainForm _form;
    private RelicState _state;

    /// <summary>The records of server.json, at most one per host, the most recently saved first
    /// (<see cref="ServerConfigStore.LoadAll"/>): the token the admin logged in with on the start screen, the one an agent
    /// install from the app saved, or the open key the Player page entered with — each lent only for the host it was
    /// saved for (<see cref="RecordFor"/>, <see cref="StoredServer"/>). Replaced as a whole under
    /// <see cref="ServerConfigStore.Gate"/>, never changed in place: a reader on any thread sees one whole list. With no
    /// record for the configured host the build's baked token applies, or none (<see cref="ServerConfig.Effective"/>).</summary>
    private volatile IReadOnlyList<ServerConfig> _records;

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

    /// <summary>1 while one of Quick Start's installs on this PC runs (<c>quickstart.install</c>: Python, 7-Zip's
    /// extractor, WSL, Docker Desktop, its start, WSL's memory cap) — one at a time, beside any install or server job;
    /// its outcome (<c>quickstart.done</c> / <c>quickstart.error</c>) is posted only once this is free again.</summary>
    private int _setupBusy;

    /// <summary>1 while the firewall step of the agent on this PC waits on its Windows permission prompt
    /// (<c>localagent.firewall</c>) — one prompt at a time.</summary>
    private int _firewallBusy;

    /// <summary>Number of play sessions this process is running (PLAY button or the shortcut launch
    /// run in-process) — 0 or 1 in practice, a counter so two overlapping starts (the second dies on
    /// the session gate) cannot clear each other's flag. Read by the tray "Exit completely" guard: the
    /// session's restore lives in this process, and an exit mid-session would orphan the private
    /// profile and the Fiddler proxy until the next Relic start.</summary>
    private int _playInProgress;

    /// <summary>True while a play session runs in this process (see <see cref="_playInProgress"/>).</summary>
    public bool PlayInProgress => Volatile.Read(ref _playInProgress) > 0;

    /// <summary>1 while Settings' "Apply Relic's game files to every installed version" runs
    /// (<see cref="ApplyAllGameFilesAsync"/>). It holds <see cref="_installBusy"/> as well; this flag only
    /// lets a PLAY refused meanwhile name the right cause.</summary>
    private int _filesApplying;

    /// <summary>1 while the certificate flow runs. It borrows CustomRules.js as scratch space for up
    /// to a minute, so two of them at once would leave a Fiddler that is currently redirecting a live
    /// client with the bootstrap script loaded — no redirect, no telemetry blocks, no error.</summary>
    private int _certBusy;

    /// <summary>Cancellation for the running install. Pause and cancel are the SAME mechanic here:
    /// the download is resumable (Range + append) and the finished-zip short-circuit skips completed
    /// files, so "pause" is just a cancel whose partial cache the next install.start picks up.</summary>
    private volatile CancellationTokenSource? _installCts;

    /// <summary>"Find it on this PC" (<see cref="GameSearch"/>): one for the launcher's life, so a deeper search goes on
    /// from what the search before it left.</summary>
    private readonly GameSearch _gameSearch = new();

    /// <summary>The install wizard's checks of a game folder (<c>install.inspectGame</c>), one at a time: the one that runs,
    /// and the newest one asked while it runs, which waits for it. A check's first looks at a path cannot be cancelled (a
    /// path on a network drive that does not answer holds its thread until Windows gives up on it), so a newer check
    /// cancels the one that runs and waits for its end — checks asked one after another never pile up threads; an older
    /// one still waiting answers <see cref="InspectCancelled"/> at once. A job that takes the install guard gives up both
    /// (<see cref="InstallGuardTaken"/>). Under <see cref="_inspectLock"/>.</summary>
    private readonly object _inspectLock = new();
    private InspectAsk? _inspectRunning, _inspectWaiting;

    /// <summary>What the page reads as a check given up — by a newer one, by a job that took the install guard, by an import
    /// that took its turn — or never made (a job held the install guard): it drops the answer.</summary>
    private static readonly object InspectCancelled = new { status = "cancelled" };

    /// <summary>One check of <c>install.inspectGame</c>: its request (copied out of the state on the message thread), its
    /// cancellation, and the answer its rpc waits for.</summary>
    private sealed class InspectAsk(ImportRequest request)
    {
        public ImportRequest Request { get; } = request;
        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource<object> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Backend(MainForm form)
    {
        _form = form;
        _state = RelicState.Load();
        // The C# side speaks the same language the UI will ask for (Program.Main already did this
        // from the same file; repeated here so a Backend built elsewhere is never out of sync).
        L.SetLanguage(_state.Settings.Language);
        // Every change of the state's collections runs with its save under the state's one lock (RelicState.Gate),
        // here as everywhere: nothing else of this process reads the state yet, and the rule has no exception.
        lock (RelicState.Gate)
        {
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
        }
        // server.json (DPAPI) holds one record per host: the token the admin logged in with (+ SSH details
        // for a tunnel override), each lent only for the host it was saved for — the HOST itself always
        // comes from Settings.ServerHost (see AdminServer). With no record for it the build's baked
        // defaults apply.
        _records = ServerConfigStore.LoadAll();
        // A state that names no server beside a file that names exactly one (a server configured through
        // server.json alone): adopt that host, so the install does not land on "no server". With several
        // records no host is the one to adopt — the start screen asks.
        if (string.IsNullOrWhiteSpace(_state.Settings.ServerHost) && _records.Count == 1 && _records[0] is { } only
            && !string.IsNullOrWhiteSpace(only.Host))
        {
            lock (RelicState.Gate)
            {
                _state.Settings.ServerHost = only.Host;
                _state.Settings.AgentPort = only.AgentPort;
                _state.Save();
            }
            Log.Info($"adopted the server host from server.json: {only.Host}");
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

    /// <summary>The always-armed recovery loop (<see cref="StartRecoveryLoop"/>), its stop and its run: the exit stops it
    /// and waits for its run before the exit's own pass (<see cref="RecoverBeforeExit"/>).</summary>
    private RecoveryLoop? _recoveryLoop;
    private readonly CancellationTokenSource _recoveryLoopStop = new();
    private Task _recoveryLoopRun = Task.CompletedTask;

    /// <summary>How many recovery passes of this launcher hold the play-session gate right now (0 or 1): a pass that
    /// finds the gate held names a pass of this launcher instead of another Relic process.</summary>
    private int _passGateHolds;

    /// <summary>What a play session or the recovery loop tells the person — the game has closed, what the session end
    /// did, the official profile back after a deferred restore — for the tray: the window may be hidden (a desktop
    /// shortcut's session, a game played full screen). Raised on a worker thread.</summary>
    public event Action<SessionNotice>? Notice;

    private async Task AwaitRecovery()
    {
        try { await _recovery; }
        catch (Exception ex) { Log.Error("profile recovery faulted", ex); }
    }

    /// <summary>True while a recovery pass is running — surfaced by profile.status.</summary>
    private bool Recovering => !_recovery.IsCompleted;

    /// <summary>
    /// The always-armed recovery loop; when a pass runs and what the log says about it is <see cref="RecoveryLoop"/>'s.
    /// A session that ends while ANOTHER Genshin client is running (the official one started seconds after ours
    /// closed) leaves this version's profile loaded — without the loop the only retry would be the next app start.
    /// Nothing deferred: a marker read every 10 s. A play session of this launcher whose restore was deferred hands it
    /// over (<see cref="RecoveryLoop.SessionEnded"/>). A restore deferred: a 2 s beat over the clients by full path (no
    /// gate, no window), and a full pass (<see cref="RecoverCore(string, out string)"/>: the gate, the sustained window,
    /// the decision table) when the clients changed, and at the latest 10 s after the previous pass ended — none while
    /// a job of this launcher holds the session gate (<see cref="GateHolderHere"/>). Each pass replaces
    /// <see cref="_recovery"/> so play/install/profile calls serialize behind it.
    /// </summary>
    private void StartRecoveryLoop()
    {
        var loop = new RecoveryLoop(
            liveIsActive: () => ProfileStore.ForGenshin().LiveIsActive(),
            holder: GateHolderHere,
            runPass: () =>
            {
                var pass = Task.Run(() =>
                {
                    var result = RecoverCore(RecoveryLoop.Origin, out string detail);
                    return new RecoveryPass(result, detail);
                });
                _recovery = pass;
                return pass;
            },
            passInFlight: () => _recovery);
        loop.Recovered += OnLoopRecovered;
        _recoveryLoop = loop;
        var stop = _recoveryLoopStop.Token;
        _recoveryLoopRun = Task.Run(() => loop.RunAsync(stop));
    }

    /// <summary>What of this launcher holds — or is about to take — the play-session gate: the recovery loop runs no
    /// pass meanwhile (it would only make that job fail on the gate), and a pass that finds the gate held names it. A
    /// recovery pass is counted where it holds the gate (<see cref="_passGateHolds"/>); every other holder sets its flag
    /// a moment before it asks for the gate.</summary>
    private GateHolder GateHolderHere() => SessionGate.InProcess(
        playInProgress: PlayInProgress,
        installBusy: Volatile.Read(ref _installBusy) != 0,
        filesApplying: Volatile.Read(ref _filesApplying) != 0,
        installJob: _installCts is not null,
        recoveryPass: Volatile.Read(ref _passGateHolds) > 0);

    /// <summary>A pass of the loop put the official profile back after a deferred restore — raised once that pass
    /// completed, so the status reads recovering: false — the page (Settings card and notice) and the tray.</summary>
    private void OnLoopRecovered(SessionNotice notice)
    {
        PostEvent("profile.changed", new
        {
            kind = notice.Kind,
            text = notice.Text,
            profile = notice.Profile,
            fiddlerStopped = notice.FiddlerStopped,
            status = ProfileStatus(),
        });
        RaiseNotice(notice);
    }

    /// <summary>A notice for the tray (<see cref="Notice"/>); a subscriber that fails costs the notice, never the session.</summary>
    private void RaiseNotice(SessionNotice notice)
    {
        try { Notice?.Invoke(notice); }
        catch (Exception ex) { Log.Error($"the {notice.Kind} notice could not be shown (non-fatal)", ex); }
    }

    /// <summary>
    /// The tray "Exit completely" pass: the user confirmed the exit while the live profile was not
    /// back in place (and no session runs in this process). One synchronous recovery pass — the same
    /// gate + sustained all-clear + decision table as every other pass — so a leftover Fiddler is
    /// stopped and live restored BEFORE the process that owns the recovery loop goes away. Stops the loop
    /// and waits for a pass already in flight first (the loop's, or the startup one) instead of colliding
    /// with it on the gate. Bounded: ~5 s of observation plus reg.exe/robocopy; a client that is running defers,
    /// exactly as the loop would, and the next Relic start finishes the job.
    /// </summary>
    public void RecoverBeforeExit()
    {
        if (PlayInProgress) return; // the session holds the gate and will do its own restore
        // The loop first: a pass it started between the wait below and the exit's own pass would hold the gate, and
        // the process would exit under it (Fiddler up, a swap cut). Its run ends once its current look has — with any
        // pass that look started —, and no pass starts after the stop. One 20 s bound for both waits.
        long until = Environment.TickCount64 + 20_000;
        TimeSpan Left() => TimeSpan.FromMilliseconds(Math.Max(0, until - Environment.TickCount64));
        _recoveryLoopStop.Cancel();
        try { _recoveryLoopRun.Wait(Left()); }
        catch (Exception ex) { Log.Error("exit: waiting for the recovery loop to stop (non-fatal)", ex); }
        try { _recovery.Wait(Left()); }
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
                // A pass of the loop leaves this line to the loop: once per streak (RecoveryLoop).
                if (origin != RecoveryLoop.Origin)
                    Log.Info($"profile recovery ({origin}) skipped: the session gate is {SessionGate.HeldBy(GateHolderHere())}");
                return Recovery.GateBusy;
            }
            // Counted while held: a pass or a job of this launcher that finds the gate held names this pass.
            Interlocked.Increment(ref _passGateHolds);
            try
            {
                // Everything is re-read INSIDE the gate: a --play session in another process may
                // have started or finished between the pre-check above and the acquire, and acting
                // on a stale marker would export the wrong data into a slot. The process check is
                // SUSTAINED, not instant: reaching this code means a session ended uncleanly or
                // deferred, so a client may be mid-respawn in a gap with no process to see.
                // (Sync-over-async is safe here — the helper uses ConfigureAwait(false) throughout.)
                // The folders are copied under the state's lock: a job registers versions on a worker thread.
                string[] ourDirs;
                lock (RelicState.Gate) ourDirs = _state.Installed.Select(i => i.GameDir).ToArray();
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
                    // A pass of the loop leaves this line to the loop: once per change of state (RecoveryLoop).
                    if (origin != RecoveryLoop.Origin)
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
                Interlocked.Decrement(ref _passGateHolds);
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: leave the marker in place so the next pass retries. A pass of the loop hands the whole
            // failure to the loop, which logs it in full once per streak (RecoveryLoop).
            bool loop = origin == RecoveryLoop.Origin;
            if (!loop) Log.Error($"profile recovery ({origin}) failed", ex);
            detail = loop ? ex.ToString() : ex.Message;
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

            case "app.init":
                // A launcher that starts while a Quick Start run is not ended arms its resume value again, once.
                QuickStartResumeOnBoot();
                return BuildInitState();

            case "settings.save":
                // The change and its save under the state's lock, like every writer of the state.
                lock (RelicState.Gate)
                {
                    ApplySettings(p);
                    _state.Save();
                }
                // After Save so a language that fails to load never blocks the rest of the settings;
                // L.SetLanguage itself never throws.
                L.SetLanguage(_state.Settings.Language);
                return BuildInitState();

            case "version.select":
                lock (RelicState.Gate)
                {
                    _state.SelectedVersionId = Str(p, "id");
                    _state.Save();
                }
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
                // hotpatch: the same tri-state. Absent = the agent decides (its default for a stack with no
                // decision on record); true / false decide the official fixes at this Prepare.
                bool? hotpatch = BoolOrNull(p, "hotpatch");
                return StartServerJob("setup", Str(p, "version"),
                    (a, v, log) => a.SetupServerAsync(v, progress: progress, pathfinding: pathfinding, hotpatch: hotpatch, onLine: log));
            }

            case "server.provision":
            {
                string progress = ProgressMode(Str(p, "progress"));
                return StartServerJob("provision", Str(p, "version"),
                    (a, v, log) => a.ProvisionServerAsync(v, progress: progress, onLine: log));
            }

            // Download the ready-made server package into the configured folder — a yielding
            // job the launcher follows like any other (an absent version's Overview row and its Server packages
            // row offer it behind a question; the downloads an agent install queued start without one).
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

            // Have the agent download the pathfinding server's own data of a version and add the files
            // its stack lacks — a job. The archive is the agent's catalogue entry (payloads/<v>/navdata.json
            // on the box): the version is all it is told.
            case "server.navdata":
                RequireAdmin();
                return StartServerJob("navdata", Str(p, "version"), (a, v, log) => a.NavdataFetchAsync(v, log));

            // Stop the running pathfinding data download — the navmesh cancel's way, through while the
            // job is followed.
            case "server.navdata.cancel":
            {
                RequireAdmin();
                string version = Str(p, "version");
                if (string.IsNullOrWhiteSpace(version))
                    throw new InvalidOperationException(L.T("backend.server.missingVersion"));
                return await WithAgent(a => a.NavdataCancelAsync(version));
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

            // One page of a version's accounts (Server → Players → Accounts on this server): a plain rpc, never
            // _serverJobBusy — the agent reads it beside any job, and a list must not wait for a job this launcher
            // follows. An agent without the route answers {tooOld: true, message}. Nothing of the answer is logged.
            case "server.accounts": RequireAdmin(); return await AccountsGetAsync(p);

            // Copy a save over a player — a job (kind "accountcopy"): {version, from | fromUid | fromTemplate,
            // to | toUid, dryRun?}. A dry run checks the whole copy and changes nothing; AgentClient sends any
            // request but the plain {version, from, to} only to an agent that has the account list.
            case "server.accountcopy":
            {
                RequireAdmin();
                // Read off the payload BEFORE the job starts (same JsonDocument-lifetime caveat as
                // txtfixes above).
                var copy = AccountCopyFromPayload(p);
                return StartServerJob("accountcopy", copy.Version,
                    async (a, v, log) => AgentClient.AccountCopyForPage(await a.AccountCopyAsync(copy, log)));
            }

            // Move accounts from a server of another version, or from a folder — a job (kind "migrate"): {version, source:
            // {version} | {dir}, accounts?, dryRun?, keepPassword?, keepUid?}. A check reads the source and writes nothing to
            // the target. The folder of a server is read with the real token alone: the agent is the gate, and an open key's
            // request does not leave this launcher. The result reaches the page through AgentClient.MigrateForPage alone.
            case "server.migrate":
            {
                RequireAdmin();
                // Read off the payload BEFORE the job starts (same JsonDocument-lifetime caveat as txtfixes above).
                var m = MigrateFromPayload(p);
                if (m.SourceDir is not null && AdminServer()?.OpenKey == true)
                    throw new InvalidOperationException(L.T("backend.migrate.folderNeedsToken"));
                return StartServerJob("migrate", m.Version,
                    async (a, v, log) => AgentClient.MigrateForPage(await a.MigrateAsync(m, log)));
            }

            // Stop the running move — a plain POST that gets through WHILE this launcher follows the job, so it never goes
            // near _serverJobBusy (the fetch cancel's reasoning). An agent that cannot do it answers {unsupported: true}.
            case "server.migrate.cancel":
            {
                RequireAdmin();
                string version = Str(p, "version");
                if (string.IsNullOrWhiteSpace(version))
                    throw new InvalidOperationException(L.T("backend.server.missingVersion"));
                return await WithAgent(a => a.MigrateCancelAsync(version));
            }

            case "command.send":
                return await SendCommandAsync(p);

            // Catalogue for the command pickers, filtered to one game version. Falls back to the
            // selected version so an empty payload still returns something; with none selected either,
            // the unfiltered catalogue (ForVersion is permissive for a version it does not know).
            case "gamedata.forVersion":
                return GameData.Load().ForVersion(
                    Str(p, "version") is { Length: > 0 } v ? v : _state.SelectedVersionId ?? "");

            case "install.start":
                // No AwaitRecovery HERE: StartInstall must assign _installCts before it returns, or
                // a Pause/Cancel click during a slow (5s+) startup recovery finds nothing to cancel
                // and is silently dropped. The install task itself awaits recovery before any work.
                // A non-empty localPath = local import: the chosen folder is registered on the spot,
                // no download. `voices` = the languages to install (absent/empty = English(US), which is
                // what the dev autoinstall and an older UI send); ignored for an import, whose packs are
                // detected from the folder itself. replaceInstalledAt (an import only): the folder the version is
                // installed at that the player chose to replace with this one (install.inspectGame named it) —
                // without it a copy of an installed version is refused.
                StartInstall(Str(p, "versionId"), Str(p, "localPath"), Strings(p, "voices"), Str(p, "replaceInstalledAt"));
                return new { started = true };

            // The install wizard's check of a game folder the player already has — the folder or its GenshinImpact.exe,
            // typed or picked —: the very check the import runs before its first write (ImportCheck). deep: whole files
            // hashed, as the import hashes them — only a deep check answers "ready". replaceInstalledAt: the folder the
            // version is installed at that the player chose to replace with this one. No gate: it only reads.
            case "install.inspectGame": return await InspectGameAsync(p);

            // "Find it on this PC": starts a search and answers its number (0: one runs already); what it finds, where it
            // is and its end stream as findgames.found / findgames.progress / findgames.done, each naming its search.
            // deeper: go on from what the search before it left. Refused while a job holds the install guard.
            case "install.findGames": return FindGames(Bool(p, "deeper"));

            // Ends the running search at once — its findgames.done {stopped: true} follows everything it posted before;
            // stopping: false = none ran.
            case "install.findGamesStop": return new { stopping = StopGameSearch("the page asked") };

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
                // ownFiles: what the page's question about the game folder's own files got for this launch
                // ("keep" / "replace"); absent when it asked nothing — the version's stored choice applies.
                StartPlay(Str(p, "versionId"), Str(p, "ownFiles"));
                return new { started = true };

            // The game folder's own files of a version — a file of the player's own in the place of one
            // Relic ships as a replacement — and the version's stored choice: PLAY asks while it is "ask".
            case "play.ownFiles": return await OwnFilesReplyAsync(Str(p, "versionId"));

            // The answer PLAY gives without asking (Settings → Game files, "Don't ask again" on the
            // question): a setting of its own, saved at once — settings.save never writes it.
            case "settings.ownFiles": return SetOwnFilesChoice(Str(p, "versionId"), Str(p, "choice"));

            // Settings → "Apply Relic's game files to every installed version".
            case "patch.applyAll": return await ApplyAllGameFilesAsync();

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
            // agentPort (optional, 1-65535) on all three: the start screen's way to reach an agent on a
            // non-default listen port that no _relic TXT record publishes — Test dials what Enter will dial,
            // and a saved server connects with the port saved with it. 0 or any other value = inferred.
            case "server.setAddress": return await SetServerAddressAsync(Str(p, "address"), Int(p, "agentPort", 0));
            case "server.test": return await TestServerAsync(Str(p, "address"), Int(p, "agentPort", 0));
            case "admin.login": return await AdminLoginAsync(Str(p, "address"), Str(p, "token"), Int(p, "agentPort", 0));
            // Neither behind RequireAdmin: the first is called from PLAYER mode, and the key the agent hands
            // out is the boundary there — exactly like admin.login's token; the second takes that key away
            // and has to work whatever mode the launcher is in (its revocation already turned it to Player).
            case "admin.openEnter": return await OpenAdminEnterAsync();
            case "admin.openForget": return OpenAdminForget(out _);
            case "login.mute": SetLoginMuted(Bool(p, "muted")); return null;
            case "login.anim": SetLoginAnimOff(Bool(p, "off")); return null;
            // The start screen's saved servers: an address, a mode and a name per host — this PC's own list, so
            // none of the three sits behind the admin gate, and nothing secret crosses the bridge either way (a
            // token reaches server.json only through admin.login, localagent.enter, admin.openEnter or an agent
            // install). Delete and Delete all forget everything this launcher keeps for a host (ForgetHost).
            case "servers.save": return SaveServerEntry(Str(p, "address"), Str(p, "mode"), Str(p, "label"), Int(p, "agentPort", 0));
            case "servers.delete": return DeleteServerEntry(Str(p, "id"));
            case "servers.deleteAll": return DeleteAllServerEntries();
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
                // The pick and its save under the state's lock: an account created on a worker thread may be
                // remembered at the same moment.
                lock (RelicState.Gate)
                {
                    if (!_state.UseAccount(_state.Settings.ServerHost, _state.Settings.ServerPort, version, name))
                        throw new InvalidOperationException(L.T("backend.account.notMine"));
                    _state.Save();
                }
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
            // Create an in-game account as the ADMIN (Server → Players → Create a player account) —
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
            // Which voice languages the hotfix mirror fetches (downloads GBs onto the box,
            // never restarts anything). "hotpatch.voice" is the UI-side job kind.
            case "server.hotpatch.voice":
            {
                RequireAdmin();
                // Read off the payload BEFORE the job starts (same JsonDocument-lifetime caveat as
                // txtfixes above). An absent key = an empty list = fetch none; non-strings are ignored.
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

            // ── the agent's last operations (the Agent tab's Logs) ──
            // Metadata only, never a job's result (AgentClient.JobsForPage). An agent with no such route answers
            // 404 → {tooOld: true} (the card says "update the agent").
            case "agent.jobs":
            {
                RequireAdmin();
                try { return await WithAgent(a => a.JobsAsync()); }
                catch (AgentHttpException ex) when (ex.Status == 404) { return new { tooOld = true }; }
            }
            // One job's lines, on demand (AgentClient.JobLogForPage: never its result). A job the agent no longer has
            // answers with the agent's own 404 text.
            case "agent.jobLog": RequireAdmin(); return await WithAgent(a => a.JobLogAsync(Str(p, "id")));

            // ── gameplay settings (the Game rules tab) ──
            // One version's settings as the agent answers them. An agent with no such route answers the GET
            // with 404 → {tooOld: true} (the page says "update the agent"); every other failure is an error
            // as usual.
            case "server.tweaks.get": RequireAdmin(); return await TweaksGetAsync(Str(p, "version"));
            // A version's settings — ONE job kind ("tweaks") for the three requests the page makes: {version,
            // tweaks, takenKey} stores and applies its document ({} = every setting taken out), {version,
            // revert | reset: [cards], takenKey} puts the data files of those cards back at once (Revert: to
            // the settings Relic had in effect before the changes made outside Relic; Reset: to the original
            // game, those cards' settings taken out) and applies what is left on them. takenKey is the one of
            // the answer the page built the request on: the agent refuses a request made before a data file
            // was changed outside Relic. Everything reaches the agent as the page built it; the agent
            // validates it, and an agent with no such route — or, for a Revert / a Reset, one whose route
            // only takes a document — comes back as the "update the agent" text.
            case "server.tweaks":
            {
                RequireAdmin();
                string takenKey = Str(p, "takenKey");
                Func<AgentClient, string, Action<string>, Task<JsonElement>> work;
                if (p.TryGetProperty("revert", out _))
                {
                    var cards = Strings(p, "revert");
                    work = (a, v, log) => a.TweaksRevertAsync(v, cards, takenKey, log);
                }
                else if (p.TryGetProperty("reset", out _))
                {
                    var cards = Strings(p, "reset");
                    work = (a, v, log) => a.TweaksResetAsync(v, cards, takenKey, log);
                }
                else
                {
                    // Cloned off the payload BEFORE the job starts (same JsonDocument-lifetime caveat as
                    // txtfixes above): the clone is what outlives the document p points into.
                    JsonElement tweaks = p.GetProperty("tweaks").Clone();
                    work = (a, v, log) => a.TweaksSetAsync(v, tweaks, takenKey, log);
                }
                return StartServerJob("tweaks", Str(p, "version"), work);
            }
            // End the gameplay-settings waits of the running job of a version (a tweaks job, or the last step
            // of a start / provision / setup / relocate). A plain POST, NOT a job, sent while this launcher
            // follows that job — so it never goes near _serverJobBusy (the fetch cancel's reasoning). An agent
            // that cannot do it (no such route, or a route that only applies documents) is answered as
            // {unsupported: true} with the "update the agent" text: the page takes its control away and
            // says why, and the job goes on as it would have.
            case "server.tweaksStop":
            {
                RequireAdmin();
                string version = Str(p, "version");
                if (string.IsNullOrWhiteSpace(version))
                    throw new InvalidOperationException(L.T("backend.server.missingVersion"));
                try { return await WithAgent(a => a.TweaksStopAsync(version)); }
                catch (AgentHttpException ex) when (ex.Status is 400 or 404)
                {
                    return new { ok = false, stopping = false, unsupported = true, message = ex.Message };
                }
            }
            // The names and sprite names that tab shows for the ids of the settings — the shipped
            // config/tweakdata.json as it is, or a status when it is missing or unreadable. Not behind
            // RequireAdmin: it is this build's own display data, the same for every server.
            case "tweakdata.get": return await TweakDataGetAsync();

            // ── agent installs (admin) ──
            case "agent.install": return StartRemoteAgentInstall(p);
            case "localagent.status": return await LocalAgentStatusAsync();
            // What a folder field of the install form names on THIS PC, and whether a 7z tool is here. Not behind
            // RequireAdmin, like localagent.status: it reads this PC only — no mode, no token.
            case "localagent.folderCheck": return await LocalFolderCheckAsync(p);
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
            case "localagent.autostart":
            {
                // Off the message thread: turning it on looks for Python (FindPython's probes, seconds each) when the
                // install's record of its interpreter is gone.
                bool autostartOn = Bool(p, "enabled");
                await Task.Run(() => LocalAgent.SetAutostart(autostartOn));
                return await LocalAgentStatusAsync();
            }
            case "localagent.firewall":
            {
                // gamePorts: a server other PCs join (Quick Start's lan / internet reach) gets the game's rules too — TCP
                // 21000 and UDP 21081 beside the agent's port, in the one Windows permission prompt (S7) — only for the
                // run whose summary named them (backend.qs.notInPlan otherwise).
                bool gamePorts = Bool(p, "gamePorts");
                if (gamePorts) QuickStartRequireFirewall();
                // One prompt at a time: two calls in flight would each read the rules there before either prompt is
                // answered, and add the game's rules twice.
                if (Interlocked.Exchange(ref _firewallBusy, 1) != 0) throw new InvalidOperationException(L.T("backend.firewall.busy"));
                try
                {
                    int fwPort = LocalAgentPort();
                    string? err = await Task.Run(() => LocalAgent.OpenFirewallPort(fwPort, gamePorts));
                    if (err is not null) throw new InvalidOperationException(err);
                }
                finally { Volatile.Write(ref _firewallBusy, 0); }
                return await LocalAgentStatusAsync();
            }
            case "localagent.openLog":
                if (File.Exists(LocalAgent.LogPath))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LocalAgent.LogPath) { UseShellExecute = true });
                return null;
            // Relic's own log (relic.log) in the system's viewer — this PC's file, like localagent.openLog.
            case "app.openLog":
                if (!File.Exists(Log.LogFile))
                    throw new InvalidOperationException(L.T("backend.relicLog.missing", new { path = Log.LogFile }));
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.LogFile) { UseShellExecute = true });
                return null;

            // ── Quick Start (Backend.QuickStart.cs) ──
            // None sits behind the admin gate: they act on this PC (the plan file and its resume value, the look at this PC,
            // the installs it adds, the restart), probe an address, or use SSH details typed a moment ago — like
            // localagent.install and agent.install. The admin-only jobs a run drives keep their own gate.
            case "quickstart.get": return QuickStartGet();
            case "quickstart.save": return QuickStartSave(p);
            case "quickstart.clear": return QuickStartClear();
            case "quickstart.check": return await QuickStartCheckAsync(p);
            case "quickstart.install": return QuickStartInstall(p);
            case "quickstart.restart": return await QuickStartRestartAsync();
            case "quickstart.publicIp": return await QuickStartPublicIpAsync();
            case "quickstart.reach": return await QuickStartReachAsync(p);
            case "ssh.probe": return await SshProbeAsync(p);

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
        var os = WindowsInfo.Detect();
        // What the state's collections hold, copied under its lock: a job changes them on a worker thread, and this
        // answer is serialised after it returns — on such a thread too. Everything else is read outside the lock.
        HashSet<string> installedIds;
        string? selected;
        List<object> installed;
        object accounts, accountLists, serverAccounts;
        lock (RelicState.Gate)
        {
            installedIds = _state.Installed.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected = _state.SelectedVersionId ?? _state.Installed.FirstOrDefault()?.Id;
            installed = _state.Installed
                .Select(i => (object)new { i.Id, i.GameDir, i.ShortcutName, i.Origin, voices = i.Voices?.ToList() })
                .ToList();
            accounts = AccountsForCurrentServer();
            accountLists = AccountListsForCurrentServer();
            serverAccounts = ServerAccountsForCurrentServer();
        }
        selected ??= catalog.FirstOrDefault()?.Id;

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
            // tokenScope: "any" (baked) | the configured host when its record holds an agent token | "" —
            // the Access tab's connection card names a token built into this launcher by it. tokenHosts: the
            // hosts server.json keeps an agent token for (host names alone; never an open key's host) — the
            // start screen's token field may stay empty for an address of one of them.
            // mode: the effective connection's "direct" | "ssh" (a legacy tunnel record), for the
            // connection facts of the Overview and the Access tab — no UI creates a tunnel config. openKey:
            // the bearer in effect is the key the agent hands out while its admin lets everyone administer
            // (entered from the Player page), not the agent token — the status strip words its banner by it,
            // the Access tab's connection card names it, and the option's own confirm says that closing it
            // ends this session too. Secrets never cross the bridge: never the token or the SSH password
            // themselves.
            admin = new
            {
                hasToken = AdminServer() is not null,
                tokenScope = ServerConfig.TokenScope(StoredServer()),
                tokenHosts = ServerConfig.TokenHosts(_records),
                mode = AdminServer()?.Mode ?? "direct",
                openKey = AdminServer()?.OpenKey == true,
            },
            // The start screen's saved servers, newest first (SavedServersForPage): an address, a mode, a name and
            // booleans per entry — never a token.
            savedServers = SavedServersForPage(),
            // The Quick Start run the start screen offers to continue, or resumes at once (running, or waiting for the
            // restart that finishes WSL 2): its plan as quickstart.json holds it — no secret is ever in it — or null.
            quickstart = new { plan = QuickStartPlanForInit() },
            loginMuted = _state.Settings.LoginMuted,
            loginAnimOff = _state.Settings.LoginAnimOff,
            // The page may boot while the window already sits in the tray (autostart "--tray", a
            // shortcut launch): the start-screen media must not play to nobody.
            windowHidden = _form.IsHiddenToTray,
            // Accounts the player created through Relic on the CURRENT server, per version: the login shown
            // (accounts) and every one of them (accountLists — several per player).
            accounts,
            accountLists,
            // What the CURRENT server last said about its pre-made account per version ("" = none) —
            // the "server's word wins" rule for the first render before any status poll.
            serverAccounts,
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
            // ("aether" on 1.6, "aetherr" on 2.8) + a password given only as an example, because the
            // server does not verify the password. An unknown account — and a version whose payload
            // ships no save has none — comes back as "" and the UI stays silent then.
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
                    // The official CDN is offered only when the catalogue names it for this version: a
                    // version may be hosted on mirrors alone, and the wizard then starts from the first.
                    hasCdn = v.HasCdn,
                    // The pictures this build ships for the version (the library card and hero, the
                    // summer theme's hero): the page draws one only when its file is there, and the
                    // plain gradient otherwise.
                    heroArt = ShipsArt("hero", v.Id),
                    summerArt = ShipsArt("summer", v.Id),
                    // The in-game enhancements (F1 menu) exist for this version only when the build
                    // ships its inject payload entry — the Settings section and the badges key on it.
                    hasEnhancements = Enhancements.ShippedFor(v.Id),
                    // The files this build ships in the place of a file of the game (a payload entry that
                    // names the original it replaces): Settings → Game files offers, for an installed version
                    // that has one, the answer PLAY gives when the folder holds a file of the player's own there.
                    replacements = GamePatcher.Replacements(v.Id).Select(r => new { dst = r.Dst, reason = r.Reason }),
                    // This PC runs Windows 11 and the build's payload manifest says the version's client
                    // is not known to start there through Relic's launch path: PLAY asks first (cancel
                    // is the default answer, "Play anyway" launches as usual) and the install wizard says
                    // so before the download.
                    windows11Unsupported = LaunchSupport.AskOnWindows11(LaunchSupport.Windows11Unsupported(v.Id), os.IsWindows11),
                };
            }),
            installed,
            // What the name of the player's own file gets after it when Relic's file takes its place: the
            // texts of Settings → Game files say it, the page never spells it.
            ownSuffix = GamePatcher.OwnSuffix,
            // The allowlist in display order — the one vocabulary for catalogue keys, install requests
            // and installed[].voices, handed to the UI so it never spells a language itself.
            voiceLanguages = VoiceLanguages.All,
            // The server stacks the install-agent form asks about, one row per version an agent can be set
            // up for, in display order: the names of the version's two payload fields (its folder, and its
            // one choice — download now, I have it, not now, keep), the folder name a stack of it
            // conventionally has, and what this build's catalogue says of its ready-made package — the
            // archive's bytes, whether there is one to download at all, its format, whether unpacking it
            // takes a 7z tool, the bytes of the unpacked tree (0 = the catalogue names none) and the bytes of
            // the package as a .zip, which a machine without a 7z tool downloads (0 = there is no .zip). The
            // form is drawn from these rows and names no version itself.
            stacks = ServerVersions.All.Select(v =>
            {
                var package = ServerVersions.Package(v);
                return new
                {
                    id = v,
                    dirField = ServerVersions.FormDir(v),
                    stackField = ServerVersions.FormStack(v),
                    folder = ServerVersions.ConventionalFolder(v),
                    size = package.Size,
                    downloadable = package.Published,
                    archive = package.Archive,
                    needsExtractor = package.NeedsExtractor,
                    extractedSize = package.ExtractedSize,
                    zipSize = package.ZipSize,
                };
            }),
        };
    }

    /// <summary>Does this build ship <c>ui/assets/&lt;kind&gt;-&lt;id&gt;.webp</c>, the picture of a version?
    /// The id is a catalogue value that goes into a file name, spelt as the page will ask for it: one
    /// with separators or ".." names no picture, not a file somewhere else. Never throws — a path the
    /// system refuses is "no picture".</summary>
    private static bool ShipsArt(string kind, string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId) || versionId.IndexOfAny(['/', '\\', ':']) >= 0 || versionId.Contains("..")) return false;
        try { return File.Exists(Path.Combine(AppContext.BaseDirectory, "ui", "assets", $"{kind}-{versionId}.webp")); }
        catch { return false; }
    }

    private void ApplySettings(JsonElement p)
    {
        var s = _state.Settings;
        // serverHost / serverPort are deliberately NOT read: the UI sends the whole settings object, and
        // writing the host raw here (no parse, no TXT ports, no agent port) would be a second host writer
        // that dials the new server on the previous one's ports. The address goes through
        // server.setAddress (the start screen; Settings and the install wizard in Player mode), admin.login or
        // an agent install only.
        // ownFiles is not read either: its one writer is settings.ownFiles, saved at once — the copy a page
        // sends here may be older than an answer the desktop shortcut stored meanwhile.
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
            // A legacy Mode: ssh record's tunnel checks the box's key against the pins this launcher holds (I58), and the
            // key it took as it came is pinned once the agent answered the call.
            await using var a = new AgentClient(cfg, TunnelPinsOf(cfg));
            await a.ConnectAsync();
            var answer = await op(a);
            PinTunnelKey(cfg, a.TunnelKey);
            return answer;
        }
        catch (AgentHttpException ex) when (ex.Status == 401 && cfg.OpenKey)
        {
            // The agent refuses the open key: its admin closed the option (or its state was restored),
            // which revokes every launcher that entered this way at once. Back to Player mode on the
            // spot — the status poll and every admin rpc pass through here, so the first refused call is
            // the one that does it — and the UI learns it through an event, since the call that found out
            // may be a job on its own task. A refused typed or baked token keeps its own error: the admin
            // may have rotated the token on the box, and that is theirs to sort out from the start screen.
            // Only the key this call was refused with is forgotten, and only while it is still the bearer in
            // effect: the configured server may have changed while the call was in flight (the start screen
            // logged into another one, maybe entered with a key of its own there) — the 401 is the old server's,
            // so the call keeps its own error, and an event would name the new server.
            if (AdminServer() is not { OpenKey: true } now
                || !ServerConfig.SameHost(now.Host, cfg.Host) || now.AgentToken != cfg.AgentToken)
                throw;
            var state = OpenAdminForget(out bool forgot);
            // Nothing forgotten = the key went meanwhile by another way (another refused call, the Server page's
            // own forget): the revocation was told once already.
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
            // The question of the configured server, the answers and their save under the state's lock: a host writer
            // (a login, an agent install) or another answer may change the state on another thread at the same moment.
            lock (RelicState.Gate)
            {
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
        }
        catch (Exception ex) { Log.Error("remembering the server's account answer (non-fatal)", ex); }
    }

    /// <summary>
    /// Fire one of the agent's long operations and follow its job: each streamed line becomes a
    /// "server.log" event, the outcome a "server.done" or "server.error" — posted once the busy flag is
    /// free again (the rule of deploy.done), so a page that starts the next job of a chain (a repoint, then
    /// the download) the moment it hears one never meets this job's own "busy". Replies immediately — a
    /// first-time setup runs for tens of minutes, far past any message round-trip.
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
            object? done = null, failed = null;
            try
            {
                var result = await WithAgent(a => run(a, version,
                    line => PostEvent("server.log", new { kind, version, text = line })));
                Log.Info($"server job done: {kind} {version}");
                done = new { kind, version, result };
            }
            catch (Exception ex)
            {
                Log.Error($"server job failed: {kind} {version}", ex);
                failed = new { kind, version, message = ex.Message };
            }
            finally
            {
                Volatile.Write(ref _serverJobBusy, 0);
            }
            // Only now: the page may start its next job as soon as it hears this.
            if (done is not null) PostEvent("server.done", done);
            else if (failed is not null) PostEvent("server.error", failed);
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

    /// <param name="replaceInstalledAt">An import only: the folder the version is installed at that the player chose to
    /// replace with this one; the import's check moves the registration only when the version is installed at that very
    /// folder (<see cref="ImportRequest.ReplaceInstalledAt"/>).</param>
    private void StartInstall(string versionId, string localPath = "", IReadOnlyList<string>? voices = null,
        string replaceInstalledAt = "")
    {
        RequireServerAddress(); // the install ends by templating the Fiddler rules for the server
        bool isLocal = !string.IsNullOrWhiteSpace(localPath);
        string? replace = isLocal && !string.IsNullOrWhiteSpace(replaceInstalledAt) ? replaceInstalledAt : null;
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
                ? $"install start: version={versionId} local import from {localPath}" + (replace is null ? "" : $" in the place of {replace}")
                : $"install start: version={versionId} voices={string.Join(",", langs)} source={_state.Settings.Source} root={root}",
            // An import downloads nothing — no space check.
            preflight: isLocal ? null : () => PreflightDisk(v, langs, voicesOnly: false, cache, InstallService.GameDir(root, v.Id)),
            work: async (svc, progress, ct) =>
            {
                if (isLocal) await svc.ImportLocalAsync(v, localPath, progress, ct, replace);
                else await svc.InstallAsync(v, langs, progress, ct);
                return null;
            });
    }

    // ── a game folder the player already has: the wizard's check, "Find it on this PC" ──

    /// <summary>
    /// install.inspectGame: <see cref="ImportCheck.Run"/> for the install wizard — the request copied out of the state
    /// here, on the message thread; the check itself off it: it waits while an import holds the checks, and a deep one
    /// hashes whole files. One check at a time (<see cref="_inspectRunning"/>): this one cancels the one that runs and
    /// waits for its end, and takes the place of an older one waiting (which answers {status: "cancelled"} at once). A
    /// check cancelled — by a newer one, by a job that takes the install guard (<see cref="InstallGuardTaken"/>), or
    /// stopped by an import that took its turn — answers {status: "cancelled"}, which the page drops; so does a check
    /// asked while a job holds the install guard, which is not made: that job changes the registrations on a worker
    /// thread, and the page asks again once it is done. Otherwise the verdict as it is.
    /// </summary>
    private async Task<object> InspectGameAsync(JsonElement p)
    {
        if (Volatile.Read(ref _installBusy) != 0) return InspectCancelled;
        string replace = Str(p, "replaceInstalledAt");
        InspectAsk ask;
        // The registrations are copied out of the state under its lock (a job changes them on a worker thread).
        lock (RelicState.Gate)
        {
            var request = ImportRequest.From(_state, Str(p, "versionId"), Str(p, "path")) with
            {
                // Set from the payload, never left to the record, which defaults to the import's deep check: the wizard asks
                // for the deep one only once the instant one found nothing against the folder.
                Deep = Bool(p, "deep"),
                ReplaceInstalledAt = string.IsNullOrWhiteSpace(replace) ? null : replace,
                Offered = VersionCatalog.Load().Select(v => v.Id).ToList(),
            };
            ask = new InspectAsk(request);
        }
        InspectAsk? displaced = null;
        bool runNow;
        lock (_inspectLock)
        {
            runNow = _inspectRunning is null;
            if (runNow) _inspectRunning = ask;
            else
            {
                displaced = _inspectWaiting;
                _inspectWaiting = ask;
                CancelQuietly(_inspectRunning!.Cts); // older than this one: it stops at its next look
            }
        }
        displaced?.Answer.TrySetResult(InspectCancelled);
        if (runNow) RunInspect(ask);
        return await ask.Answer.Task;
    }

    /// <summary>Runs one check of <see cref="InspectGameAsync"/> off the message thread and answers its rpc; then the one
    /// that waited for it runs, if any. Its cancellation is disposed only once it is no longer the one that runs, so
    /// nothing cancels a disposed one.</summary>
    private void RunInspect(InspectAsk ask) => _ = Task.Run(() =>
    {
        object? verdict = null;
        Exception? failure = null;
        try
        {
            var v = ImportCheck.Run(ask.Request, ask.Cts.Token);
            if (v.Deep)
                Log.Info($"import check (wizard): version={v.VersionId} \"{v.GameDir}\" — {v.Status}"
                         + (v.Reason.Length > 0 ? $" ({v.Reason})" : ""));
            verdict = v;
        }
        catch (OperationCanceledException) { verdict = InspectCancelled; }
        catch (Exception ex) { failure = ex; }
        InspectAsk? next;
        lock (_inspectLock)
        {
            next = _inspectWaiting;
            _inspectWaiting = null;
            _inspectRunning = next;
        }
        ask.Cts.Dispose();
        if (failure is not null) ask.Answer.TrySetException(failure);
        else ask.Answer.TrySetResult(verdict!);
        if (next is not null) RunInspect(next);
    });

    /// <summary>
    /// install.findGames: starts "Find it on this PC" (<see cref="GameSearch"/>) — its context copied out of the state
    /// here, on the message thread — and answers the search's number (0: one runs already). Its events reach the page one
    /// at a time, in the order the search made them, each naming its search: findgames.progress (at most four a second,
    /// the first one at its start), findgames.found, and findgames.done, once, its last. Refused while a job holds the
    /// install guard: an install or an import writes into the very folders the walk would find half made.
    /// </summary>
    private object FindGames(bool deeper)
    {
        if (Volatile.Read(ref _installBusy) != 0)
            throw new InvalidOperationException(L.T("backend.find.installBusy"));
        // The context is copied out of the state under its lock (a job changes the registrations on a worker thread); the
        // search starts outside it — it takes a lock of its own.
        GameSearchContext context;
        lock (RelicState.Gate) context = GameSearchContext.From(_state, VersionCatalog.Load());
        int search = _gameSearch.Start(context, deeper,
            found: hit => PostEvent("findgames.found", hit),
            progress: now => PostEvent("findgames.progress", now),
            done: end => PostEvent("findgames.done", end));
        return new { search };
    }

    /// <summary>Ends the running "Find it on this PC" search (<see cref="GameSearch.Stop"/>: its findgames.done
    /// {stopped: true} comes after everything it posted before): the page asked, a job took the install guard, the window
    /// went to the tray. False when none ran. Any thread; it never waits.</summary>
    public bool StopGameSearch(string why)
    {
        bool stopped = _gameSearch.Stop();
        if (stopped) Log.Info($"find games: stopped — {why}");
        return stopped;
    }

    /// <summary>A job has taken the install guard — an install, an import, a voice download, a removal, Apply Relic's game
    /// files: the "Find it on this PC" search stops (it would walk the folders the job writes into; none starts while the
    /// guard is held), and the wizard's checks of a folder are given up (<see cref="CancelInspects"/>; an import stops the
    /// checks anyway and waits for them — <see cref="ImportCheck.HoldChecks"/> —; this only makes it sooner; none is made
    /// while the guard is held). Never throws: the job goes on either way.</summary>
    private void InstallGuardTaken(string job)
    {
        try
        {
            StopGameSearch(job);
            CancelInspects();
        }
        catch (Exception ex) { Log.Error($"stopping the game search and the folder check ({job}) failed (non-fatal)", ex); }
    }

    /// <summary>Gives up the wizard's checks of a folder: the one that runs is cancelled (it stops at its next look), and
    /// the one that waits for it answers {status: "cancelled"} at once.</summary>
    private void CancelInspects()
    {
        InspectAsk? waiting;
        lock (_inspectLock)
        {
            waiting = _inspectWaiting;
            _inspectWaiting = null;
            if (_inspectRunning is { } running) CancelQuietly(running.Cts);
        }
        waiting?.Answer.TrySetResult(InspectCancelled);
    }

    /// <summary>Cancels a check's cancellation source; one that has ended meanwhile is left as it is.</summary>
    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { } // it has ended meanwhile
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
            InstallGuardTaken($"an install job of {versionId} started");
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
                // The outcome — exactly one of install.done / install.cancelled / install.error — is posted only once the
                // guard and the gate are free again (the bridge rule): a page that starts the next install, or restarts
                // Windows, the moment it hears it never meets this job's own "busy".
                bool installed = false;
                IReadOnlyList<string>? added = null;
                string? outcome = null;
                object? outcomeData = null;
                try
                {
                    acquired = gate.WaitOne(0);
                    if (!acquired)
                    {
                        Log.Info($"install refused: a game session is in progress ({versionId})");
                        outcome = "install.error";
                        outcomeData = new { message = L.T("backend.install.gameRunning") };
                    }
                    else
                    {
                        try
                        {
                            // A stop clicked while recovery was still running lands here, not mid-download.
                            cts.Token.ThrowIfCancellationRequested();
                            Log.Info(logLine);
                            preflight?.Invoke();
                            var svc = new InstallService(_state, Environment.ProcessPath ?? "");
                            // InstallService mutates + saves the SAME _state instance — no reload needed
                            // (a reload here would race the UI thread's Save and could wipe the registration).
                            added = await work(svc, progress, cts.Token);
                            Log.Info($"install done: {versionId}{(added is null ? "" : $" (voices added: {string.Join(", ", added)})")}");
                            installed = true;
                        }
                        // The `when` filter keeps stray OCEs honest: only a cancel WE requested reads as
                        // pause/cancel — any other OperationCanceledException still surfaces as an error.
                        catch (OperationCanceledException) when (cts.IsCancellationRequested)
                        {
                            // Cache and partial files stay on disk on purpose: the next install.start
                            // resumes from them, which is what makes pause/cancel cheap to undo.
                            Log.Info($"install stopped by user: {versionId}");
                            outcome = "install.cancelled";
                            outcomeData = new { versionId };
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"install failed: {versionId}", ex);
                            // A corrupt archive means a cached zip itself is bad — delete the bad one so
                            // the retry starts clean instead of failing at extract again after hours.
                            if (ex is System.IO.InvalidDataException)
                                TryDeleteCorruptCacheZips(cache, versionId);
                            outcome = "install.error";
                            outcomeData = new { message = ex.Message };
                        }
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
                // Only now, the guard and the gate free.
                if (installed)
                {
                    // BuildInitState reads _state, which the UI thread also touches — marshal it.
                    void PostDone() => PostEvent("install.done", new { versionId, voices = added, state = BuildInitState() });
                    try { _form.BeginInvoke((Action)PostDone); }
                    catch (Exception) { PostDone(); } // window going down — post directly, PostJson is safe
                }
                else if (outcome is not null) PostEvent(outcome, outcomeData);
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

    /// <param name="ownFiles">The answer the page's question about the game folder's own files got for this
    /// launch (keep / replace); anything else leaves the version's stored choice in effect.</param>
    private void StartPlay(string versionId, string? ownFiles = null)
    {
        RequireServerAddress();
        if (Volatile.Read(ref _filesApplying) != 0)
            throw new InvalidOperationException(L.T("backend.play.filesApplying"));
        // Playing mid-install would launch the game from the very dir the install extracts into
        // (write-locked exe → the multi-GB install dies at extract, leaving a mixed-version dir).
        if (Volatile.Read(ref _installBusy) != 0)
            throw new InvalidOperationException(L.T("backend.play.installRunning"));
        // Counted from the click, not from inside the task: an "Exit completely" a moment later must
        // already see the session that is about to hold the gate.
        Interlocked.Increment(ref _playInProgress);
        string? answer = OwnFileChoices.Answer(ownFiles);
        _ = Task.Run(async () =>
        {
            // What the session end did (PlaySession.Ended): a restore it deferred goes to the recovery loop once the
            // session has let go of everything (the finally below).
            SessionNotice? ended = null;
            try
            {
                var session = new PlaySession(_state) { OwnFilesAnswer = answer };
                session.Log += m => { Log.Info($"[play] {m}"); PostEvent("play.log", new { text = m }); };
                // Closes the UI's "Starting the game" overlay exactly when the client really appeared.
                // enhancements: whether the F1 menu is really in the process (the toast words it).
                session.GameAppeared += () => PostEvent("play.appeared", new { versionId, enhancements = session.EnhancementsActive });
                // The moment the game is seen gone, before the settle window: the page says what happens next.
                session.GameClosed += n => { PostEvent("play.closing", n); RaiseNotice(n); };
                session.Ended += n => ended = n;
                await session.PlayAsync(versionId);
                // What the session end did — the Fiddler and the profile decision — in words (session.Result).
                PostEvent("play.done", (object?)session.Result ?? new { versionId });
                if (session.Result is { } result) RaiseNotice(result);
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
                _recoveryLoop?.SessionEnded(ended);
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
    /// the single-instance guard exists. Reports through callbacks: the web window is hidden, the
    /// only visible surface is the splash, and errors are shown by the caller (<see cref="MainForm"/>)
    /// in a dialog. The session's end reaches the page as it does from PLAY — the window may be opened
    /// during the game —: play.closing, then play.done (PLAY stays off between them), or, when the
    /// session end itself failed after the closing was said, play.error, which takes that notice away;
    /// the tray says them while nobody sees the page (<see cref="Notice"/>). <paramref name="ownFiles"/>:
    /// the answer the splash's question about the game folder's own files got (keep / replace), null when
    /// it asked nothing.
    /// </summary>
    public void StartPlayFromShortcut(string versionId, Action<string> log, Action appeared, Action<Exception?> finished,
        string? ownFiles = null)
    {
        Interlocked.Increment(ref _playInProgress); // see StartPlay
        string? answer = OwnFileChoices.Answer(ownFiles);
        _ = Task.Run(async () =>
        {
            Exception? error = null;
            // What the session end did (PlaySession.Ended): a restore it deferred goes to the recovery loop once the
            // session has let go of everything (the finally below).
            SessionNotice? ended = null;
            // The page was told the game has closed: one of play.done / play.error follows, so PLAY does not stay off.
            bool closingPosted = false;
            try
            {
                // The startup profile recovery holds the session gate (named semaphore) for up to
                // 5 seconds, and PlaySession asks for it with WaitOne(0) — without this wait, the
                // shortcut launch could die with "A game session is already in progress" because
                // of our own recovery.
                await AwaitRecovery();
                if (Volatile.Read(ref _filesApplying) != 0)
                    throw new InvalidOperationException(L.T("backend.play.filesApplying"));
                if (Volatile.Read(ref _installBusy) != 0)
                    throw new InvalidOperationException(L.T("backend.play.installRunning"));

                var session = new PlaySession(_state) { OwnFilesAnswer = answer };
                session.Log += m => { Log.Info($"[play] {m}"); log(m); };
                session.GameAppeared += appeared;
                // The window stays in the tray, and may be opened during the game: the page hears the session's end as
                // it does from PLAY, the tray says it while nobody sees the page (Notice).
                session.GameClosed += n => { closingPosted = true; PostEvent("play.closing", n); RaiseNotice(n); };
                session.Ended += n => ended = n;
                await session.PlayAsync(versionId);
                PostEvent("play.done", (object?)session.Result ?? new { versionId });
                if (session.Result is { } result) RaiseNotice(result);
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
                // A session end that failed after the page heard the game close: no play.done follows, so play.error
                // takes the closing notice away (the error itself is the caller's dialog).
                if (error is not null && closingPosted) PostEvent("play.error", new { message = error.Message });
                Interlocked.Decrement(ref _playInProgress);
                _recoveryLoop?.SessionEnded(ended);
                finished(error);
            }
        });
    }

    // ── the game folder's own files (a file of the player's own in the place of one Relic ships) ──

    /// <summary>The version's stored choice and — while it is "ask", the one case in which PLAY asks — the
    /// replacements of <paramref name="versionId"/>'s plan whose destination holds a file of the player's own
    /// (<see cref="GamePatcher.OwnFiles"/> — hashed off the message thread, with the version's enhancements
    /// setting as a launch has it). A version that is not installed has no such file, and with an answer
    /// stored nothing is looked at. Called on the UI thread (the page's rpc, the desktop shortcut's splash).</summary>
    public async Task<(IReadOnlyList<GamePatcher.Replacement> Files, string Choice)> OwnFilesAsync(string versionId)
    {
        string choice = _state.Settings.OwnFilesChoiceFor(versionId);
        var inst = _state.FindInstalled(versionId);
        if (inst is null || choice != OwnFileChoices.Ask) return (Array.Empty<GamePatcher.Replacement>(), choice);
        string id = inst.Id, dir = inst.GameDir;
        bool withInjectables = _state.Settings.Enhancements;
        var files = await Task.Run(() => GamePatcher.OwnFiles(id, dir, withInjectables: withInjectables));
        return (files, choice);
    }

    private async Task<object> OwnFilesReplyAsync(string versionId)
    {
        var (files, choice) = await OwnFilesAsync(versionId);
        return new { files = files.Select(f => new { dst = f.Dst, reason = f.Reason, kept = f.Kept }), choice };
    }

    /// <summary>settings.ownFiles: the answer PLAY gives for <paramref name="versionId"/> without asking —
    /// keep, replace, or ask (the entry removed; any other word means the same). A version the catalogue
    /// does not know is refused.</summary>
    private object SetOwnFilesChoice(string versionId, string choice)
    {
        var v = Catalogued(versionId.Trim());
        RememberOwnFilesChoice(v.Id, choice);
        return new { ownFiles = _state.Settings.OwnFiles };
    }

    /// <summary>Store the answer about <paramref name="versionId"/>'s own game files — from Settings, from
    /// "Don't ask again" on the page's question or on the desktop shortcut's (MainForm) — and tell the page,
    /// whichever asked: the Settings rows follow it. UI thread.</summary>
    public void RememberOwnFilesChoice(string versionId, string choice)
    {
        lock (RelicState.Gate)
        {
            if (_state.Settings.SetOwnFilesChoice(versionId, choice)) _state.Save();
        }
        Log.Info($"own game files of {versionId}: {_state.Settings.OwnFilesChoiceFor(versionId)}");
        PostEvent("settings.ownFiles", new { ownFiles = _state.Settings.OwnFiles });
    }

    /// <summary>
    /// Another Relic process stored a version's answer about the player's own game files in the state FILE:
    /// the play process of a desktop shortcut started while this launcher runs (Program.RunPlay). The map is
    /// read in from there — that map alone: everything else this process holds stays as it is, and its next
    /// save keeps the answer instead of putting the question back. A file that cannot be read changes
    /// nothing. UI thread.
    /// </summary>
    public void ReloadOwnFiles()
    {
        try
        {
            var disk = RelicState.TryRead();
            if (disk is null) return;
            lock (RelicState.Gate) _state.Settings.OwnFiles = disk.Settings.OwnFiles;
            Log.Info("own game files: the answers were read again from state.json (stored by another Relic process)");
            PostEvent("settings.ownFiles", new { ownFiles = _state.Settings.OwnFiles });
        }
        catch (Exception ex) { Log.Error("own game files: reading the answers again (non-fatal)", ex); }
    }

    /// <summary>What <see cref="ApplyAllGameFilesAsync"/> did for one version: the files put in place, those
    /// already current, the names the player's own files were kept under (Relic's written over them), the
    /// files that could not be put in place (an optional one this build lacks, or one that could not be
    /// written), the player's own files left as they are, and the version's error — the run goes on past it.</summary>
    private sealed record GameFilesResult(string VersionId, IReadOnlyList<string> Applied, IReadOnlyList<string> AlreadyCurrent,
        IReadOnlyList<string> OwnKept, IReadOnlyList<string> Missing, IReadOnlyList<string> Kept, string? Error);

    /// <summary>
    /// Settings → "Apply Relic's game files to every installed version": the plan of each installed version
    /// put in place as a launch puts it — the enhancements setting decides its inject entries — and over every
    /// file of the player's own under a replacement, theirs kept beside it as <see cref="GamePatcher.OwnSuffix"/>.
    /// Refused while an install, an import or a removal runs (and none starts meanwhile: the install guard is
    /// held), while a play session holds the session gate — in this process or in the play process of a
    /// desktop shortcut — and while a Genshin client runs; a PLAY meanwhile is refused (<see cref="_filesApplying"/>).
    /// One result per version: a version that fails is a line of the answer, never the end of the run. An
    /// injector pair that arrived with the files is recorded the way a launch records it
    /// (<see cref="InstallService.DetectInjector"/>).
    /// </summary>
    private async Task<object> ApplyAllGameFilesAsync()
    {
        if (Interlocked.Exchange(ref _installBusy, 1) != 0)
            throw new InvalidOperationException(L.T("backend.install.busy"));
        Volatile.Write(ref _filesApplying, 1);
        try
        {
            InstallGuardTaken("Apply Relic's game files started");
            if (PlayInProgress) throw new InvalidOperationException(L.T("backend.patch.gameRunning"));
            // The recovery pass holds the session gate for seconds: a refusal must not come from it.
            await AwaitRecovery();
            bool withInjectables = _state.Settings.Enhancements;
            List<(string Id, string GameDir)> versions;
            lock (RelicState.Gate) versions = _state.Installed.Select(i => (i.Id, i.GameDir)).ToList();
            var results = await Task.Run(() =>
            {
                using var gate = new Semaphore(1, 1, PlaySession.SessionGateName);
                if (!gate.WaitOne(0)) throw new InvalidOperationException(L.T("backend.patch.gameRunning"));
                try
                {
                    if (ProcessGuard.AnyRunning()) throw new InvalidOperationException(L.T("backend.patch.clientRunning"));
                    // No check of the install wizard holds a file of a game folder while the files go in (an import's
                    // rule): a rename over a file that is open would fail, and the files' own check would take the
                    // check's handle for another program's.
                    using var hold = ImportCheck.HoldChecks();
                    return versions.Select(v => ApplyGameFiles(v.Id, v.GameDir, withInjectables)).ToList();
                }
                finally { gate.Release(); }
            });
            // The registrations' injector fields and their save under the state's lock.
            lock (RelicState.Gate)
            {
                bool changed = false;
                foreach (var inst in _state.Installed)
                {
                    string? before = inst.LauncherExe;
                    InstallService.DetectInjector(inst);
                    changed |= !string.Equals(before, inst.LauncherExe, StringComparison.OrdinalIgnoreCase);
                }
                if (changed) _state.Save();
            }
            return new { results };
        }
        finally
        {
            Volatile.Write(ref _filesApplying, 0);
            Volatile.Write(ref _installBusy, 0);
        }
    }

    /// <summary>
    /// One version of <see cref="ApplyAllGameFilesAsync"/>, in two steps: the plan put in place as a launch puts
    /// it (<see cref="GamePatcher.Apply"/> — the player's own files stay), then Relic's file over every file of
    /// the player's own (<see cref="GamePatcher.ReplaceOwnFiles"/>). The second step runs also when the first
    /// failed: an entry of the plan that cannot be applied (a changed UserAssembly.dll, say) does not keep
    /// Relic's file out of the place this button puts it into, and the answer says what both steps did. A
    /// failure of either is the version's error.
    /// </summary>
    private static GameFilesResult ApplyGameFiles(string versionId, string gameDir, bool withInjectables)
    {
        // A folder whose game is gone receives nothing: the payload would be written into an empty shell.
        if (!File.Exists(Path.Combine(gameDir, "GenshinImpact.exe")))
        {
            string gone = L.T("backend.patch.filesMissing", new { version = versionId, dir = gameDir });
            Log.Info($"patch {versionId} (every installed version): {gone}");
            return new GameFilesResult(versionId, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>(), gone);
        }
        void Note(string m) => Log.Info($"patch {versionId}: {m}");
        var errors = new List<string>();
        PatchReport? plan = null, own = null;
        try
        {
            plan = GamePatcher.Apply(versionId, gameDir, Note, withInjectables: withInjectables);
            Log.Info($"patch {versionId} (every installed version): {plan.Summary}");
        }
        catch (Exception ex)
        {
            Log.Error($"patch {versionId} (every installed version) failed", ex);
            errors.Add(ex.Message);
        }
        try
        {
            own = GamePatcher.ReplaceOwnFiles(versionId, gameDir, Note, withInjectables: withInjectables);
            if (own.Changed || own.Kept.Count > 0 || own.Missing.Count > 0)
                Log.Info($"patch {versionId} (every installed version, the player's own files): {own.Summary}");
        }
        catch (Exception ex)
        {
            Log.Error($"patch {versionId} (every installed version, the player's own files) failed", ex);
            if (!errors.Contains(ex.Message)) errors.Add(ex.Message); // the same refusal twice is one
        }
        IEnumerable<string> Of(List<string>? list) => list ?? Enumerable.Empty<string>();
        var replaced = Of(own?.ReplacedOwn).ToList();
        return new GameFilesResult(versionId,
            Applied: Of(plan?.Applied).Concat(Of(own?.Applied)).ToList(),
            AlreadyCurrent: Of(plan?.AlreadyCurrent).ToList(),
            OwnKept: replaced.Select(d => d + GamePatcher.OwnSuffix).ToList(),
            Missing: Of(plan?.Missing).Concat(Of(own?.Missing)).Distinct().ToList(),
            // Left as the player's: what the plan kept and the second step did not write over — nor failed to
            // (that one is under Missing).
            Kept: Of(plan?.Kept).Concat(Of(own?.Kept)).Except(replaced).Except(Of(own?.Missing)).Distinct().ToList(),
            Error: errors.Count > 0 ? string.Join(" ", errors) : null);
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
