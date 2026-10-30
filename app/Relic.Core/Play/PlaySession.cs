using Relic.Core.Fiddler;
using Relic.Core.Install;
using Relic.Core.Isolation;
using Relic.Core.Launch;
using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.Core.Play;

/// <summary>
/// Runs one play session end to end: gate (one session at a time, cross-process) → guard (only one
/// version at a time) → swap in this version's isolated profile → start Fiddler → launch the game
/// (per-OS) → wait for the game to exit → stop Fiddler → swap the profile back and restore the
/// "live" profile. The "live" slot preserves the official client's account data untouched.
/// </summary>
public sealed class PlaySession
{
    public const string LiveProfile = ProfileStore.LiveId;

    /// <summary>
    /// Cross-process gate: only one play session at a time, whether started from the PLAY button
    /// or the desktop "--play" shortcut. A named Semaphore rather than a Mutex because the session
    /// body awaits — a Mutex can only be released by the thread that acquired it, and after an await
    /// the continuation may run on a different thread.
    /// </summary>
    public const string SessionGateName = @"Local\Relic.PlaySession";

    private readonly RelicState _state;
    public event Action<string>? Log;

    /// <summary>Fired once, when the game client is first sighted running. This is the moment the
    /// "Starting the game" splash (in-app overlay and the --play shortcut window alike) goes away —
    /// from here on the game's own window is what the player watches.</summary>
    public event Action? GameAppeared;

    /// <summary>Fired once, the moment the session's client is seen gone — before the settle window, which takes
    /// seconds: the host says at once what happens next (<see cref="SessionEnd.Closing"/>). Not for a client that
    /// never appeared, nor when Relic closed the game because Fiddler was closed (that has its own message).</summary>
    public event Action<SessionNotice>? GameClosed;

    /// <summary>Fired once the post-exit step is done (the settle window, then Fiddler, then the profile), with what
    /// it did — also <see cref="Result"/>. A restore it deferred is the host's recovery loop's from then on
    /// (<see cref="RecoveryLoop.SessionEnded"/>). Not fired when that step itself failed: PlayAsync throws then.</summary>
    public event Action<SessionNotice>? Ended;

    /// <summary>What the post-exit step did (see <see cref="Ended"/>); null until it is done.</summary>
    public SessionNotice? Result { get; private set; }

    /// <summary>True once the game was started through launcher.exe WITH the enhancements DLL(s) —
    /// i.e. the F1 menu really is in the process. False for every other launch, including the plain
    /// start Launch falls back to after a launcher failure. Meaningful from <see cref="GameAppeared"/>
    /// on (the host reads it to word the "game is running" toast).</summary>
    public bool EnhancementsActive { get; private set; }

    /// <summary>The answer the player gave for THIS launch about the game folder's own files — a file of
    /// theirs in the place of one Relic ships as a replacement (<c>GamePatcher.OwnFiles</c>):
    /// <see cref="OwnFileChoices.Keep"/> or <see cref="OwnFileChoices.Replace"/>. Anything else (null — no
    /// question was asked) leaves the version's stored choice in effect (<see cref="RelicSettings.OwnFiles"/>),
    /// and without one the player's file stays: a launch never waits for an answer.</summary>
    public string? OwnFilesAnswer { get; set; }

    /// <summary>True in a desktop shortcut's own play process (<c>Program.RunPlay</c>): its state was read when it
    /// started, so the injector found after the patch is stored in the file alone
    /// (<see cref="RelicState.RememberInjector"/>), never by a save of that whole state.</summary>
    public bool StateReadAtStart { get; init; }

    public PlaySession(RelicState state) => _state = state;

    public async Task PlayAsync(string versionId, bool startFiddler = true, CancellationToken ct = default)
    {
        var inst = _state.FindInstalled(versionId)
            ?? throw new InvalidOperationException(L.T("core.play.notInstalled", new { version = versionId }));

        // Registered but gone from disk (folder deleted by hand, state.json that outlived an
        // uninstall). Caught HERE, before the profile swap: failing later would leave the private
        // profile loaded over the official one for nothing. Deliberately not self-healing — the
        // --play shortcut runs in its own process and must not race the app's state.json.
        if (!File.Exists(Path.Combine(inst.GameDir, "GenshinImpact.exe")))
            throw new InvalidOperationException(
                L.T("core.play.filesMissing", new { version = versionId, dir = inst.GameDir }));

        using var gate = new Semaphore(1, 1, SessionGateName);
        if (!gate.WaitOne(0))
            throw new InvalidOperationException(L.T("core.play.sessionInProgress"));
        try
        {
            // Only one client may run — never two versions together (hardware + isolation).
            if (ProcessGuard.AnyRunning())
                throw new InvalidOperationException(L.T("core.play.clientRunning"));

            var profiles = ProfileStore.ForGenshin();

            // A marker that is not clean-live means the previous session ended uncleanly — exactly
            // the situation where a client may be mid-respawn, in a gap with no process to see.
            // Require a sustained all-clear before touching profiles (the clean common path above
            // keeps its instant check, so normal PLAY latency is unchanged).
            if (!profiles.LiveIsActive() && !await ProcessGuard.NoneRunningSustainedAsync())
                throw new InvalidOperationException(L.T("core.play.clientRespawning"));

            // A previous swap torn by a crash/kill leaves the disk an unusable mix — discard it and
            // reload live before doing anything else. Runs here (not only at app startup) so the
            // --play shortcut process and an in-app retry are covered too.
            if (profiles.RepairIfTorn())
                Log?.Invoke(L.T("core.play.tornRepaired"));

            // A still-loaded profile the official client ran on (see ProfileStore.MarkTainted): the
            // SwapTo below never exports it — said out loud here because this session's slot keeps
            // its older snapshot, and a private login/settings reset without a word looks like a bug.
            // (In the app the startup recovery normally restored live long before this point; the
            // standalone "--play" process has no recovery pass, so this is its only notice.)
            if (profiles.IsTainted())
                Log?.Invoke(L.T("core.play.taintDiscarded", new { profile = profiles.ActiveId() }));

            Log?.Invoke(L.T("core.play.preparingProfile", new { version = versionId }));
            // activeId comes from the persisted marker, not a hardcoded "live": after an unclean
            // exit the previous version's profile may still be loaded, and exporting it as "live"
            // would destroy the official account data. The swap stays OUTSIDE the try below on
            // purpose — if it fails halfway, restoring here could overwrite live with a torn slot;
            // the marker lets the next run recover instead.
            profiles.SwapTo(inst.ProfileId, activeId: profiles.ActiveId());

            bool gameNeverAppeared = false;
            bool clientSeen = false;
            LaunchHandle? handle = null;
            int? ownedFiddlerPid = null;
            try
            {
                if (startFiddler)
                {
                    var s = _state.Settings;
                    Log?.Invoke(L.T("core.play.checkingFiddler"));
                    var ready = FiddlerAutomation.CheckReady(s.ServerHost, s.ServerPort);
                    if (!ready.AllGood)
                    {
                        Util.Log.Info($"fiddler not ready: installed={ready.Installed} cert={ready.CertTrusted} rules={ready.RulesPresent}");
                        // Says a minute out loud because it can be: if the certificate has to be
                        // created, Fiddler is started to mint it and that is the slow part. A Fiddler
                        // window flashing open mid-launch looks like a bug if nothing warned you.
                        Log?.Invoke(L.T("core.play.preparingFiddler"));
                        await FiddlerAutomation.EnsureReadyAsync(
                            s.ServerHost, s.ServerPort, s.FiddlerDecrypt, s.InstallCert, s.FiddlerNoUac, ct);
                    }
                    // A previous session that ended deferred leaves Fiddler running on purpose;
                    // starting a second one would just fight the first for the proxy port. Its rules
                    // are already current — Fiddler auto-reloads CustomRules.js when the file changes.
                    if (FiddlerAutomation.IsRunning)
                    {
                        Log?.Invoke(L.T("core.play.fiddlerReused"));
                    }
                    else
                    {
                        Log?.Invoke(L.T("core.play.startingFiddler"));
                        ownedFiddlerPid = FiddlerAutomation.StartFiddler().Id;
                    }
                }

                // Same idea as the Fiddler check above: repair at play time what an install may not
                // have done. A version installed by an older build never got these files at all.
                ApplyBundledFixes(inst);
                // Always, not only when the patch changed something: two File.Exists and a Save only
                // when the pair really is new — and it is what makes an injector that arrived by any
                // other route (a hand copy, a payload repaired while the app was closed) count. It
                // matters on Windows 10 too, which needs the pair for the enhancements.
                RefreshInjector(inst);

                // The in-game enhancements: computed NOW, from the toggle + what this build ships +
                // what is actually on disk at this moment. A DLL the antivirus removed is simply not
                // handed to launcher.exe (it refuses the whole launch over a missing argv DLL).
                var ee = Enhancements.Resolve(inst.Id, inst.GameDir, _state.Settings.Enhancements);
                if (ee.Enabled && ee.Missing.Count > 0)
                    Log?.Invoke(L.T("core.play.enhancementsMissing"));

                var install = new GameInstall(inst.GameDir, inst.LauncherExe, inst.InjectDll) { ExtraDlls = ee.Dlls };
                var method = GameLauncher.ChooseMethod(install);
                Util.Log.Info($"launch method: {method} (enhancements: enabled={ee.Enabled} shipped={ee.Shipped} dlls={ee.Dlls.Count} missing={ee.Missing.Count})");
                if (GameLauncher.InjectorMissing(install))
                    Log?.Invoke(L.T("core.play.injectorMissing"));
                else if (ee.Dlls.Count > 0 && method == LaunchMethod.DirectExe)
                    Log?.Invoke(L.T("core.play.enhancementsNoInjector"));
                if (ee.Dlls.Count > 0 && method == LaunchMethod.InjectedLauncher)
                    Log?.Invoke(L.T("core.play.enhancementsOn"));
                Log?.Invoke(L.T("core.play.launching", new { version = versionId }));
                var h = GameLauncher.Launch(install);
                handle = h;
                // launcher.exe may have refused (exit != 0) and Launch fallen back to the plain start
                // — only ever when the injector was wanted for the enhancements alone, so the game is
                // up WITHOUT them and the user must hear why F1 does nothing.
                EnhancementsActive = ee.Dlls.Count > 0 && h.Method == LaunchMethod.InjectedLauncher;
                if (method == LaunchMethod.InjectedLauncher && h.Method == LaunchMethod.DirectExe)
                    Log?.Invoke(L.T("core.play.enhancementsLauncherFailed"));

                // Without Fiddler the game no longer sees the private server: if the user closes it
                // mid-session the client hangs on network errors with no explanation. The watcher runs
                // alongside the wait below and, once Fiddler is really gone, kills the game — the wait
                // then ends by itself and the outer finally does the normal restore. Its cancel is
                // linked to ct so a cancelled PlayAsync does not leave it polling forever.
                using var fiddlerWatchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task<bool>? fiddlerWatch = startFiddler ? WatchFiddlerAsync(h, fiddlerWatchCts.Token) : null;

                bool appeared;
                try
                {
                    Log?.Invoke(L.T("core.play.waitingForClient"));
                    // "The game is running" comes from the callback, not from here: the client takes
                    // a while to come up, so printing it right after Launch() would claim something we have
                    // not yet observed — and that is precisely the sighting the restore decision hangs on.
                    appeared = await GameLauncher.WaitForExitAsync(h, ct,
                        onAppeared: () =>
                        {
                            clientSeen = true;
                            Log?.Invoke(L.T("core.play.gameRunning"));
                            GameAppeared?.Invoke();
                        });
                }
                finally
                {
                    // Stopped BEFORE the outer finally gets to StopFiddler: otherwise the normal stop
                    // of Fiddler at the end of the session would look, to a watcher still alive,
                    // exactly like a Fiddler closed by the user.
                    fiddlerWatchCts.Cancel();
                }
                gameNeverAppeared = !appeared;
                bool fiddlerKilled = fiddlerWatch is not null && await fiddlerWatch.ConfigureAwait(false);
                if (fiddlerKilled)
                {
                    // Only for a client actually SEEN running: a game that never started has nothing
                    // to be "closed by Relic" — the right diagnosis remains the one below ("probably
                    // did not start"), not a story about Fiddler. A real kill cannot miss the
                    // sighting: the watcher enters the kill path ~3 s after launch at the earliest,
                    // while the appear poll sees any client within 250 ms.
                    if (appeared) throw new FiddlerClosedException();
                    Util.Log.Info("fiddler closed during a launch that never produced a client — reporting the launch failure instead");
                }
                Log?.Invoke(appeared
                    ? L.T("core.play.gameClosed")
                    : L.T("core.play.gameNotDetected"));
                // Said the moment the client is seen gone: the settle window below takes seconds, and until its end
                // nothing else would tell the player that Relic is putting things back.
                if (appeared) Raise(GameClosed, SessionEnd.Closing(versionId, startFiddler));
            }
            finally
            {
                // Decide whether it is SAFE to restore live now. Swapping live in under a client
                // that is (or is about to be) running would let it write private-server state onto
                // live-labeled data, which the next save would export into the live slot.
                //
                // The question is deliberately "is a client running right now", NOT "did we manage
                // to SEE one start": deferring the restore on a lost sighting alone would let
                // one failed path lookup strand the official client on the private profile until
                // the next Relic start. The settle window exists because the game may exit and
                // respawn itself (elevation, crash handler) with a gap in which NO GenshinImpact.exe
                // is visible, and it is widened when we never saw the client: we then have no
                // evidence of where it is in its lifecycle.
                //
                // Fiddler is a SEPARATE decision from the profile: a foreign
                // client (the official HoYoPlay one, started seconds after ours closed) must defer
                // the profile restore — swapping live in under it would corrupt the data — but it
                // must NOT keep Fiddler alive, because Fiddler's rules would redirect that official
                // client to the private server. Only OUR client still running (a respawn) keeps
                // Fiddler up, and it is never stopped before the window ends: stopping it in the
                // respawn gap would let the returning private client reach the official hosts with
                // the private profile loaded. An unattributable client counts as ours (conservative).
                // The window, the decision and its steps: SessionEnd.FinishAsync — the window's length is its own.
                Result = await SessionEnd.FinishAsync(new SessionEndSteps
                {
                    OurGameDirs = new[] { inst.GameDir },
                    PreExistingPids = handle?.PreExistingPids,
                    StopFiddler = FiddlerAutomation.StopFiddler,
                    RestoreLive = () => profiles.SwapTo(LiveProfile, activeId: inst.ProfileId), // save this version out, restore live
                    Taint = () => TaintForeign(profiles, inst.ProfileId),
                    OwnedFiddlerPid = ownedFiddlerPid,
                }, versionId, fiddler: startFiddler, clientSeen, clientNeverAppeared: gameNeverAppeared, say: Log);
                Raise(Ended, Result);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The deferred-foreign case: the official client started on this version's still-loaded profile
    /// before the settle window ended. From this moment the disk is a mix — whatever that client
    /// writes (its login token once the user signs in, its settings) lands under the private
    /// profile's key, and the recovery's usual export would carry it into the private slot, from
    /// where the next private session would present the official token to the private server. So:
    /// snapshot the slot NOW — best-effort: the foreign client has only just started and its token is
    /// written after a completed login, which takes longer than the settle window, but robocopy may
    /// still trip over a file it holds open, in which case the slot keeps its previous snapshot — and
    /// mark it tainted, so every restore path puts live back without exporting (ProfileStore.MarkTainted).
    /// </summary>
    private static void TaintForeign(ProfileStore profiles, string profileId)
    {
        try
        {
            profiles.SaveProfile(profileId);
            Util.Log.Info($"deferred-foreign: snapshot of '{profileId}' taken before the taint");
        }
        catch (Exception ex)
        {
            Util.Log.Error($"deferred-foreign: snapshot of '{profileId}' failed — the slot keeps its previous snapshot", ex);
        }
        try
        {
            profiles.MarkTainted(profileId);
            Util.Log.Info($"deferred-foreign: profile '{profileId}' marked tainted — it will be restored without re-export");
        }
        catch (Exception ex)
        {
            Util.Log.Error($"deferred-foreign: marking '{profileId}' tainted failed", ex);
        }
    }

    /// <summary>What to do once the session's game is gone, given what the settle window saw.</summary>
    public readonly record struct PostExit(bool StopFiddler, bool RestoreProfile, string Reason);

    /// <summary>The reasons of <see cref="PostExitDecision"/>, as relic.log and a deferred recovery pass carry them.</summary>
    public const string ReasonOwn = "own client still running";
    public const string ReasonUnknown = "unattributable client running";
    public const string ReasonForeign = "foreign client running";
    public const string ReasonNone = "no client running";

    /// <summary>
    /// Pure decision table shared by the session end and by the app's background recovery:
    /// our own client (or one we cannot attribute) still running → keep Fiddler AND defer the profile
    /// restore; only foreign clients → stop Fiddler (it must not MITM them) but defer the restore;
    /// nothing running → stop Fiddler and restore live.
    /// </summary>
    public static PostExit PostExitDecision(bool sawOurs, bool sawForeign, bool sawUnknown)
    {
        if (sawOurs) return new PostExit(false, false, ReasonOwn);
        if (sawUnknown) return new PostExit(false, false, ReasonUnknown);
        if (sawForeign) return new PostExit(true, false, ReasonForeign);
        return new PostExit(true, true, ReasonNone);
    }

    /// <summary>A notice to a host's handler: a handler that throws must never break the session end.</summary>
    private static void Raise(Action<SessionNotice>? handler, SessionNotice notice)
    {
        try { handler?.Invoke(notice); }
        catch (Exception ex) { Util.Log.Error($"play session: the {notice.Kind} notice could not be delivered (non-fatal)", ex); }
    }

    /// <summary>
    /// Re-applies the files payload/ carries for this version before launching. Cheap when they are
    /// already in place (content-addressed), and it is what repairs a version installed by a build
    /// that predates them. Only a REQUIRED file we cannot put in place aborts the launch — the 2.8
    /// client does not start without its global-metadata.dat, so failing here with a clear message
    /// beats "The game was not detected within 90 seconds". The enhancements DLL (manifest
    /// <c>inject</c> entries) is in the plan only while the setting is on: off means never copied,
    /// never verified, never deleted — the launch is then byte-for-byte the plain per-OS one.
    /// A file of the player's own in the place of a replacement counts here as nothing to do and stays;
    /// what the launch does with it is <see cref="ReplaceOwnFiles"/>' business, afterwards.
    /// </summary>
    private void ApplyBundledFixes(InstalledVersion inst)
    {
        bool withInjectables = _state.Settings.Enhancements;
        try
        {
            if (!GamePatcher.IsUpToDate(inst.Id, inst.GameDir, withInjectables: withInjectables))
            {
                Log?.Invoke(L.T("core.play.applyingPatches"));
                var report = GamePatcher.Apply(inst.Id, inst.GameDir, m => Util.Log.Info($"patch {inst.Id}: {m}"),
                    withInjectables: withInjectables);
                Util.Log.Info($"patch {inst.Id}: {report.Summary}");
            }
        }
        catch (GamePatchException ex)
        {
            Util.Log.Error($"patch {inst.Id} failed", ex);
            throw new InvalidOperationException(
                L.T("core.play.cannotStart", new { version = inst.Id, reason = ex.Message }));
        }
        catch (Exception ex)
        {
            Util.Log.Error($"patch {inst.Id} failed (non-fatal)", ex);
            Log?.Invoke(L.T("core.play.patchCheckFailed"));
        }
        ReplaceOwnFiles(inst, withInjectables);
    }

    /// <summary>
    /// "Use Relic's file" — the answer of this launch (<see cref="OwnFilesAnswer"/>) or else the version's
    /// stored choice: a file of the player's own in the place of a replacement is written over, theirs kept
    /// beside it (<see cref="GamePatcher.ReplaceOwnFiles"/>). Those files alone, once the rest of the plan went
    /// through the launch's own check: the answer never adds a refusal "Keep mine" would not meet. Nor does a
    /// failure here stop the launch — the player's file is still in place, and the game starts with it as
    /// with "Keep mine"; the player is told.
    /// </summary>
    private void ReplaceOwnFiles(InstalledVersion inst, bool withInjectables)
    {
        string? answer = OwnFileChoices.Answer(OwnFilesAnswer);
        if ((answer ?? _state.Settings.OwnFilesChoiceFor(inst.Id)) != OwnFileChoices.Replace) return;
        string why = answer is null ? "the version's stored choice" : "the answer of this launch";
        try
        {
            var report = GamePatcher.ReplaceOwnFiles(inst.Id, inst.GameDir, m => Util.Log.Info($"patch {inst.Id}: {m}"),
                withInjectables: withInjectables);
            if (report.ReplacedOwn.Count == 0 && report.Kept.Count == 0 && report.Missing.Count == 0) return;
            Util.Log.Info($"patch {inst.Id}: Relic's files go over the player's own ({why}): {report.Summary}");
            if (report.ReplacedOwn.Count > 0)
                Log?.Invoke(L.T("core.play.ownFilesReplaced", new
                {
                    files = string.Join(", ", report.ReplacedOwn),
                    kept = string.Join(", ", report.ReplacedOwn.Select(d => d + GamePatcher.OwnSuffix)),
                }));
            var stayed = report.Kept.Concat(report.Missing).ToList();
            if (stayed.Count > 0)
                Log?.Invoke(L.T("core.play.ownFilesKept", new { files = string.Join(", ", stayed) }));
        }
        catch (Exception ex)
        {
            Util.Log.Error($"patch {inst.Id}: Relic's files could not go over the player's own ({why}) — the game starts with theirs", ex);
            Log?.Invoke(L.T("core.play.ownFilesNotReplaced", new { reason = ex.Message }));
        }
    }

    /// <summary>
    /// Runs while the game's exit is awaited and answers a single question: did the user close
    /// Fiddler during the session? If so, it closes the game and returns true — the message for the
    /// user is raised by the caller (FiddlerClosedException). IsRunning is name-based and a Fiddler
    /// that is still shutting down still answers "yes", so the absence must be seen twice in a row,
    /// so that a transient false negative of the process enumeration does not trigger a reaction.
    /// </summary>
    private async Task<bool> WatchFiddlerAsync(LaunchHandle handle, CancellationToken ct)
    {
        bool killing = false;
        try
        {
            int misses = 0;
            while (true)
            {
                await Task.Delay(1500, ct).ConfigureAwait(false);
                if (FiddlerAutomation.IsRunning) { misses = 0; continue; }
                if (++misses < 2) continue;
                break; // absence seen twice in a row — Fiddler really is closed
            }

            killing = true;
            Util.Log.Info("fiddler closed mid-session — killing the game");
            Log?.Invoke(L.T("core.play.fiddlerClosedKilling"));

            // With retries, not a single shot: in the first seconds after launch the client may not
            // be attributable yet (process not yet created on the injected path), and a kill that
            // misses would leave the game alive, without a proxy, and the "was closed" message would
            // lie. True is answered only after a SUSTAINED absence: the game has a documented respawn
            // gap (elevation, crash handler) in which no process is visible — an instant read in that
            // gap would declare dead a client that comes back a few seconds later.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                KillGame(handle);
                if (await OurGameGoneSustainedAsync(handle, ct).ConfigureAwait(false)) return true;
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            Util.Log.Info("fiddler closed but the game outlived every kill attempt — not claiming the kill");
            return false;
        }
        catch (OperationCanceledException)
        {
            // The main wait has ended (the game is no longer seen). Before the kill started, that is
            // the normal session: the game closed first. After — the kill is claimed only on a
            // sustained, non-cancellable absence (ct is already cancelled here), for the same reason
            // as above: the respawn gap is not proof of death.
            return killing && await OurGameGoneSustainedAsync(handle, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The watcher is a forgotten task if it throws: an error here must neither become an
            // UnobservedTaskException nor pass for "Fiddler closed".
            Util.Log.Error("fiddler watch failed", ex);
            return false;
        }
    }

    /// <summary>Sustained absence of the session's client: the attributable (by path) counterpart of
    /// ProcessGuard.NoneRunningSustainedAsync, with the same 5 s window / 500 ms step — the game's
    /// respawn gap lasts seconds, and a single empty read inside it proves nothing. A client that
    /// reappears inside the window returns false, so the kill loop catches it on the next pass
    /// instead of leaving it orphaned without a proxy.</summary>
    private static async Task<bool> OurGameGoneSustainedAsync(LaunchHandle handle, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            if (OurGameRunning(handle)) return false;
            if (Environment.TickCount64 >= deadline) return true;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Is any client attributable to this session still running? Same attribution as in
    /// KillGame and GameProcessWatcher: full path, or a process new since the pre-launch snapshot
    /// when the path is unreadable.</summary>
    private static bool OurGameRunning(LaunchHandle handle)
    {
        foreach (var g in ProcessGuard.RunningGenshin())
            if (IsOurs(handle, g)) return true;
        return false;
    }

    private static bool IsOurs(LaunchHandle handle, ProcessGuard.GameProcess g) =>
        g.Path.Length > 0
            ? string.Equals(SafeFullPath(g.Path), SafeFullPath(handle.GameExePath),
                StringComparison.OrdinalIgnoreCase)
            : handle.PreExistingPids is null || !handle.PreExistingPids.Contains(g.Pid);

    /// <summary>
    /// Closes the client started by THIS session. On Win10 we hold the handle from Process.Start,
    /// which keeps its rights even after the anti-cheat driver refuses new opens. On the injected
    /// path (Win11) there is no handle, so the PID is attributed as in GameProcessWatcher: the full
    /// exe path first, and a process with an unreadable path only if it did not exist before the
    /// launch — never by name alone, both clients are called GenshinImpact.exe.
    /// </summary>
    private static void KillGame(LaunchHandle handle)
    {
        // The handle first, then the path scan REGARDLESS: the game can relaunch itself (elevation,
        // crash handler) under another PID, and a handle-only kill would leave the respawned client
        // alive — and the session would hang waiting for it to close.
        if (handle.Direct is not null)
        {
            try { handle.Direct.Kill(); }
            catch (Exception ex) { Util.Log.Info($"kill via direct handle failed: {ex.Message}"); }
        }

        foreach (var g in ProcessGuard.RunningGenshin())
        {
            if (!IsOurs(handle, g)) continue;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(g.Pid);
                p.Kill();
            }
            catch (Exception ex)
            {
                Util.Log.Info($"kill GenshinImpact pid={g.Pid} failed: {ex.Message}");
            }
        }
    }

    private static string SafeFullPath(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    /// <summary>Record an injector that only just arrived: LauncherExe/InjectDll are otherwise written
    /// once, at registration, so a version registered before the payload existed would keep launching
    /// the Win10 way on Win11 (and without the enhancements on Win10). Runs after every
    /// ApplyBundledFixes; saved only when it really changed — the --play shortcut runs in its own
    /// process, and a Save that races the app's is worth doing exactly once per version, not per launch.
    /// In that process (<see cref="StateReadAtStart"/>) only the version's injector goes into the state FILE
    /// (<see cref="RelicState.RememberInjector"/>), never the whole state it read at its start. The record's change
    /// and its save run under <see cref="RelicState.Gate"/>, the state's one lock.</summary>
    private void RefreshInjector(InstalledVersion inst)
    {
        lock (RelicState.Gate)
        {
            string? before = inst.LauncherExe;
            InstallService.DetectInjector(inst);
            if (string.Equals(before, inst.LauncherExe, StringComparison.OrdinalIgnoreCase)) return;
            Util.Log.Info($"injector detected after patch for {inst.Id}: {inst.LauncherExe}");
            if (StateReadAtStart) RelicState.RememberInjector(inst.Id, inst.LauncherExe, inst.InjectDll);
            else _state.Save();
        }
    }
}
