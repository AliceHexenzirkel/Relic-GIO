using Relic.Core.Fiddler;
using Relic.Core.Isolation;
using Relic.Core.Util;

namespace Relic.Core.Play;

/// <summary>What one recovery pass came to (the host's pass: the session gate, the sustained settle window over
/// every registered game folder, the post-exit decision table, the taint mark, the swap).</summary>
public enum RecoveryResult { Already, GateBusy, Deferred, Repaired, Restored, Discarded, Failed }

/// <summary>One recovery pass: its result and its detail — the decision's reason for
/// <see cref="RecoveryResult.Deferred"/> (<see cref="PlaySession.PostExitDecision"/>), the profile put away for
/// Restored / Discarded, the failure for Failed (the whole exception text for a pass of the recovery loop, which
/// logs that failure itself).</summary>
public readonly record struct RecoveryPass(RecoveryResult Result, string Detail);

/// <summary>
/// The launcher's always-armed recovery loop: WHEN a recovery pass runs and what the log says about it. The pass
/// itself is the host's and the same for every origin (startup, the loop, Restore, the exit); a session that ended
/// while another client ran leaves its profile loaded, and this loop finishes that restore once every client is gone.
/// <list type="bullet">
/// <item>Nothing deferred — the live profile is loaded, or a play session of this launcher began from it and will
/// put it back itself: one marker read every <see cref="IdleTick"/>; no process list, no pass, no line, no notice.</item>
/// <item>A play session of this launcher that ends with its restore deferred hands it over (<see cref="SessionEnded"/>):
/// its post-exit step ran the settle window a moment ago, so it counts as the previous pass, and the loop looks at once
/// — the beat starts there.</item>
/// <item>Something of this launcher holds — or is about to take — the session gate (<see cref="GateHolder"/>: a play
/// session that took over a deferred restore, an install, an import, a voice download, a removal, Apply Relic's game
/// files, another recovery pass): no pass — it would only make that job fail on the gate —, the holder looked at
/// again every <see cref="BeatTick"/>.</item>
/// <item>A restore deferred, or an unclean end: every <see cref="BeatTick"/> the clients are listed by full path (no
/// gate, no window), and the full pass runs when that set changed since the previous pass ended — or changed while it
/// ran, or the pass deferred on a client none of the loop's looks listed (once in a row) —, and at the latest
/// <see cref="Floor"/> after the previous pass ENDED. After a pass that found the gate held (another Relic process)
/// or failed, only the floor applies. The settle window of the pass is never shortened.</item>
/// <item>A pass of the host's that the loop did not start (the host's start, Restore) is waited for and counts as the
/// previous pass; the clients listed before it — while a restore is deferred, or while the live profile is not loaded,
/// no play session of this launcher holds the gate and the loop knows of no deferral yet — make a client that closes
/// while it runs a change.</item>
/// </list>
/// The log says it once per change of state — deferred and why, the gate held and by whom, a failure (in full) —
/// with how often and how long the previous state was seen, and once more when the deferral ends; a look that fails
/// is logged in full once per kind of failure. A pass of this loop that put the official profile back raises
/// <see cref="Recovered"/>, after that pass completed. Once <see cref="RunAsync"/>'s token is cancelled no pass starts.
/// </summary>
public sealed class RecoveryLoop
{
    /// <summary>The origin the loop's passes run under: the host's pass leaves its per-pass lines to the loop.</summary>
    public const string Origin = "loop";

    /// <summary>The cadence while nothing is deferred: one marker read.</summary>
    public static readonly TimeSpan IdleTick = TimeSpan.FromSeconds(10);

    /// <summary>The beat while a restore is deferred: the clients by full path, and the holder of the gate.</summary>
    public static readonly TimeSpan BeatTick = TimeSpan.FromSeconds(2);

    /// <summary>The longest gap while a restore is deferred: a pass runs at the latest this long after the previous
    /// one ENDED, whatever the clients do — sooner when they change; after a pass that found the gate held or failed,
    /// exactly this long.</summary>
    public static readonly TimeSpan Floor = TimeSpan.FromSeconds(10);

    private static readonly long FloorMs = (long)Floor.TotalMilliseconds;
    private static readonly long BeatMs = (long)BeatTick.TotalMilliseconds;

    private readonly Func<bool> _liveIsActive;
    private readonly Func<GateHolder> _holder;
    private readonly Func<Task<RecoveryPass>> _runPass;
    private readonly Func<Task?> _passInFlight;

    /// <param name="liveIsActive">The marker: the live profile is loaded, clean.</param>
    /// <param name="holder">What of this launcher holds — or is about to take — the session gate right now
    /// (<see cref="SessionGate.InProcess"/>).</param>
    /// <param name="runPass">Runs one recovery pass under <see cref="Origin"/> and publishes it to everything that waits
    /// for the current pass (the task it returns is the one <paramref name="passInFlight"/> then answers); the task
    /// completes when the pass is done.</param>
    /// <param name="passInFlight">The pass the host ran last, whoever started it (its start, Restore, this loop): one
    /// the loop did not start is waited for while it runs, and its end counts as the end of the previous pass.</param>
    public RecoveryLoop(Func<bool> liveIsActive, Func<GateHolder> holder, Func<Task<RecoveryPass>> runPass,
        Func<Task?>? passInFlight = null)
    {
        _liveIsActive = liveIsActive;
        _holder = holder;
        _runPass = runPass;
        _passInFlight = passInFlight ?? (() => null);
    }

    /// <summary>The clients by full path (the beat). The session spike lists its stand-ins here.</summary>
    public Func<IReadOnlyList<ProcessGuard.GameProcess>> Clients { get; init; } = ProcessGuard.RunningGenshin;

    /// <summary>Whether a Fiddler runs, for the notice of a restore.</summary>
    public Func<bool> FiddlerRunning { get; init; } = () => FiddlerAutomation.IsRunning;

    /// <summary>The clock (milliseconds) the cadence is counted on.</summary>
    public Func<long> NowMs { get; init; } = () => Environment.TickCount64;

    /// <summary>The wait between two looks (<see cref="RunAsync"/> only; a session's hand-over cuts it short).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (t, ct) => Task.Delay(t, ct);

    /// <summary>relic.log, info level.</summary>
    public Action<string> Info { get; init; } = m => Log.Info(m);

    /// <summary>relic.log, error level.</summary>
    public Action<string> Error { get; init; } = m => Log.Error(m);

    /// <summary>A pass of this loop put the official profile back (Restored, Discarded or Repaired). Raised once per
    /// deferral, after that pass completed — never while nothing is deferred.</summary>
    public event Action<SessionNotice>? Recovered;

    private enum Phase { Idle, Session, Pending }

    private Phase _phase = Phase.Idle;

    // The deferral and the state its last line named (with the holder, when that state is the gate held).
    private long _pendingSince;
    private int _passes;
    private string? _stateKey;
    private int _stateSeen;
    private long _stateSince;
    private GateHolder? _heldBy;

    // The cadence: the end of the previous pass, its result (null: a pass the loop waited for), the clients after it,
    // whether they changed while it ran, and whether it ran only because it deferred on a client no look listed.
    private long? _lastEnd;
    private RecoveryResult? _lastResult;
    private string? _baseline;
    private bool _changedUnder;
    private bool _unlistedRetried;

    // The last pass of the host the loop has seen — its own or another's —, to tell a pass it did not start.
    private Task? _seenPass;

    // A session's hand-over and the wake of the wait between two looks (both under _wakeLock).
    private readonly object _wakeLock = new();
    private HandOver? _handOver;
    private TaskCompletionSource _wake = NewWake();

    // A look that failed: its kind (the first line of the failure), how often, since when.
    private string? _failKey;
    private int _failSeen;
    private long _failSince;

    private sealed record HandOver(long At, string Kind);

    /// <summary>Runs until <paramref name="ct"/> is cancelled: the run ends once its current look has — with the pass
    /// that look started, if any —, and no pass starts after the cancel. Never throws: a look that fails is logged (in
    /// full once per kind of failure) and the loop goes on at <see cref="IdleTick"/>.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await StepAsync(ct).ConfigureAwait(false);
                LookWorked();
            }
            catch (Exception ex)
            {
                LookFailed(ex);
                wait = IdleTick;
            }
            if (ct.IsCancellationRequested) return;
            await WaitOrWakeAsync(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A play session of this launcher has ended — the result its post-exit step raised (<see cref="PlaySession.Ended"/>),
    /// handed over once the session has let go of the gate. A session that put the official profile back, and one
    /// without a result (its post-exit step failed), need nothing from here: the loop's next look acts on the marker as
    /// after any unclean end. A session whose restore was deferred hands it over: its post-exit step ran the settle
    /// window and the decision table a moment ago, so it counts as the previous pass — the loop looks at once, the beat
    /// starts there, and the next full pass runs when the clients change, at the latest <see cref="Floor"/> after this
    /// moment. Never blocks: the look runs on the loop's own thread.
    /// </summary>
    public void SessionEnded(SessionNotice? result)
    {
        if (result is null || result.Restored) return;
        long at = NowMs();
        lock (_wakeLock)
        {
            _handOver = new HandOver(at, result.Kind);
            _wake.TrySetResult();
        }
    }

    /// <summary>One look; returns how long to wait before the next one. Once <paramref name="ct"/> is cancelled the
    /// look starts no pass.</summary>
    public async Task<TimeSpan> StepAsync(CancellationToken ct = default)
    {
        bool outside = await WaitForOutsidePassAsync().ConfigureAwait(false);

        if (_liveIsActive())
        {
            TakeHandOver(); // a restore handed over and finished since: nothing left to take over
            if (_phase == Phase.Pending) EndDeferral(PutBackBy(outside));
            _phase = Phase.Idle;
            ResetCadence();
            return IdleTick;
        }

        var holder = _holder();
        if (holder != GateHolder.None)
        {
            if (holder == GateHolder.PlaySession && PeekHandOver() is { } waiting)
            {
                // The session that handed its restore over may still be letting go of the gate: look again at the
                // beat. A hand-over older than a beat meets a NEW session's gate: that session takes the restore over,
                // and puts it back or hands it over itself.
                if (NowMs() - waiting.At <= BeatMs) return BeatTick;
                TakeHandOver();
            }
            if (holder == GateHolder.PlaySession && _phase != Phase.Pending)
            {
                // A play session of this launcher that began from the live profile puts it back itself: nothing is
                // deferred, and the loop has nothing to say about it.
                _phase = Phase.Session;
                return IdleTick;
            }
            BeginDeferral(PeekHandOver()?.At); // a restore handed over waits for that job too: its deferral began there
            NoteHeld(holder);
            return BeatTick;
        }

        if (TakeHandOver() is { } handOver)
        {
            // The session's post-exit step is the previous pass: its window ended at the hand-over, on a deferral.
            BeginDeferral(handOver.At);
            _lastEnd = handOver.At;
            _lastResult = RecoveryResult.Deferred;
            _baseline = null;
            _changedUnder = false;
            _unlistedRetried = false;
            Note($"handed over: {handOver.Kind}",
                $"profile recovery ({Origin}): a play session of this launcher ended with the restore deferred ({handOver.Kind}) — from here the beat follows the clients");
        }

        BeginDeferral();
        string clients = Key(Clients());
        _baseline ??= clients; // after a hand-over or a pass the loop only waited for, the clients it finds now
        bool listedChange = _changedUnder || clients != _baseline;
        // A pass that deferred saw a client in its window: one no look of the loop listed (gone before the look that
        // followed the pass) is a change as well — once in a row, so a client that only ever shows up inside windows
        // is left to the floor.
        bool unlistedChange = !listedChange && !_unlistedRetried
            && _lastResult == RecoveryResult.Deferred && clients.Length == 0;
        long now = NowMs();
        if (!PassDue(_lastEnd, _lastResult, listedChange || unlistedChange, now)) return Wait(now);

        // Stopping (the host exits): never a new pass. And once more right before the gate: a job of this launcher
        // that started since must not meet this pass there.
        if (ct.IsCancellationRequested) return IdleTick;
        if (_holder() != GateHolder.None) return BeatTick;

        bool atFloor = _lastEnd is not long last || now - last >= FloorMs;
        var task = _runPass();
        _seenPass = task;
        var pass = await task.ConfigureAwait(false);
        _passes++;
        long end = NowMs();
        var after = Clients();
        string afterKey = Key(after);
        _lastEnd = end;
        _lastResult = pass.Result;
        _changedUnder = afterKey != clients;
        _baseline = afterKey;
        _unlistedRetried = unlistedChange && !atFloor;
        OnPass(pass, after);
        return _phase == Phase.Pending ? Wait(end) : IdleTick;
    }

    /// <summary>
    /// The cadence rule. A deferral's first look has no previous pass to count from: the pass runs. Otherwise a pass is
    /// due once <see cref="Floor"/> has passed since the previous one ENDED; sooner only when the clients changed — and
    /// never sooner after a pass that found the gate held or failed (a retry storm helps neither).
    /// </summary>
    public static bool PassDue(long? lastEndMs, RecoveryResult? lastResult, bool clientsChanged, long nowMs)
    {
        if (lastEndMs is not long end) return true;
        if (nowMs - end >= FloorMs) return true;
        if (lastResult is RecoveryResult.GateBusy or RecoveryResult.Failed) return false;
        return clientsChanged;
    }

    /// <summary>
    /// A pass of the host's that the loop did not start (its start, Restore) since the previous look. One still running
    /// is waited for, and its end counts as the end of the previous pass: the clients from before it are the baseline,
    /// so a change while it ran counts as a change — also for the pass of the host's start over a restore deferred
    /// earlier, which runs before the loop has looked at anything. One that ran between two looks leaves the cadence as
    /// it was. Returns whether there was one (the end of a deferral then names it).
    /// </summary>
    private async Task<bool> WaitForOutsidePassAsync()
    {
        var running = _passInFlight();
        if (running is null || ReferenceEquals(running, _seenPass)) return false;
        _seenPass = running;
        if (running.IsCompleted) return true;
        // Listed while a restore is deferred, and before the loop knows of any deferral while the live profile is not
        // loaded and no play session of this launcher holds the gate (the host's start over a restore deferred earlier:
        // its client may close while that pass runs). Nothing deferred — the live profile loaded, or a play session of
        // this launcher that began from it, also before the loop's next look has seen that session — lists no process:
        // the holder is asked first, a flag of this launcher, then the marker.
        string? before = _phase == Phase.Pending
            || (_phase == Phase.Idle && _holder() != GateHolder.PlaySession && !_liveIsActive())
            ? Key(Clients()) : null;
        try { await running.ConfigureAwait(false); }
        catch { /* that pass reports its own failure */ }
        _lastEnd = NowMs();
        _lastResult = null;
        _baseline = before;
        _changedUnder = false;
        _unlistedRetried = false;
        return true;
    }

    /// <summary>The wait between two looks: <paramref name="wait"/>, cut short by a session's hand-over.</summary>
    private async Task WaitOrWakeAsync(TimeSpan wait, CancellationToken ct)
    {
        Task wake;
        lock (_wakeLock) wake = _wake.Task;
        if (!wake.IsCompleted)
        {
            Task delay;
            try { delay = Delay(wait, ct); }
            catch (OperationCanceledException) { return; }
            await Task.WhenAny(delay, wake).ConfigureAwait(false);
            // A delay the wake cut short runs out on its own; one that failed is observed, never left unobserved.
            if (delay.IsFaulted) _ = delay.Exception;
        }
        lock (_wakeLock)
            if (_wake.Task.IsCompleted) _wake = NewWake();
    }

    // Continuations of the wake run on the pool: the session that hands over never runs a look on its own thread.
    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HandOver? PeekHandOver()
    {
        lock (_wakeLock) return _handOver;
    }

    private HandOver? TakeHandOver()
    {
        lock (_wakeLock)
        {
            var h = _handOver;
            _handOver = null;
            return h;
        }
    }

    private TimeSpan Wait(long now)
    {
        if (_lastEnd is not long end) return BeatTick;
        long toFloor = end + FloorMs - now;
        // After a pass that found the gate held or failed only the floor applies: no beat until then.
        if (_lastResult is RecoveryResult.GateBusy or RecoveryResult.Failed)
            return TimeSpan.FromMilliseconds(Math.Max(toFloor, 100));
        return TimeSpan.FromMilliseconds(Math.Clamp(toFloor, 100, BeatMs));
    }

    private void OnPass(RecoveryPass pass, IReadOnlyList<ProcessGuard.GameProcess> clients)
    {
        switch (pass.Result)
        {
            case RecoveryResult.Deferred:
                Note($"deferred: {pass.Detail}",
                    $"profile recovery ({Origin}) deferred: {pass.Detail}; fiddler stopped={pass.Detail == PlaySession.ReasonForeign}; clients: {Describe(clients)}");
                break;
            case RecoveryResult.GateBusy:
                // A job or a pass of this launcher that took the gate in the meantime, or else another Relic process.
                NoteHeld(_holder());
                break;
            case RecoveryResult.Failed:
                Note($"failed: {FirstLine(pass.Detail)}", $"profile recovery ({Origin}) failed: {pass.Detail}", error: true);
                break;
            case RecoveryResult.Restored:
                EndDeferral($"restored (the progress of '{pass.Detail}' saved)");
                Raise(RecoveredNotice(pass));
                break;
            case RecoveryResult.Discarded:
                EndDeferral($"restored without exporting the tainted profile '{pass.Detail}'");
                Raise(RecoveredNotice(pass));
                break;
            case RecoveryResult.Repaired:
                EndDeferral("an interrupted profile swap repaired, the live profile loaded");
                Raise(RecoveredNotice(pass));
                break;
            default: // Already: the live profile was back before the pass got to it
                EndDeferral("the live profile was back already");
                break;
        }
    }

    private SessionNotice RecoveredNotice(RecoveryPass pass)
    {
        bool fiddlerOff;
        try { fiddlerOff = !FiddlerRunning(); }
        catch { fiddlerOff = false; }
        if (pass.Result == RecoveryResult.Repaired)
            return new(SessionNotice.KindRepaired, L.T("core.play.tornRepaired")) { FiddlerStopped = fiddlerOff, Restored = true };
        string text = fiddlerOff ? L.T("core.play.recovered") : L.T("core.play.recoveredProfile");
        if (pass.Result == RecoveryResult.Discarded)
            return new(SessionNotice.KindRecoveredDiscarded, text + " " + L.T("core.play.recoveredDiscarded", new { profile = pass.Detail }))
            {
                Profile = pass.Detail,
                FiddlerStopped = fiddlerOff,
                Restored = true,
            };
        return new(SessionNotice.KindRecovered, text) { Profile = pass.Detail, FiddlerStopped = fiddlerOff, Restored = true };
    }

    private void Raise(SessionNotice notice)
    {
        try { Recovered?.Invoke(notice); }
        catch (Exception ex) { Error($"profile recovery ({Origin}): the {notice.Kind} notice could not be delivered (non-fatal): {ex}"); }
    }

    private void BeginDeferral(long? since = null)
    {
        if (_phase == Phase.Pending) return;
        _phase = Phase.Pending;
        _pendingSince = since ?? NowMs();
        _passes = 0;
        _stateKey = null;
        _stateSeen = 0;
        _heldBy = null;
    }

    private void NoteHeld(GateHolder holder) =>
        Note(HeldKey(holder), $"profile recovery ({Origin}) waits: the session gate is {SessionGate.HeldBy(holder)}", heldBy: holder);

    /// <summary>One line per change of state: the same state seen again is counted, not logged.</summary>
    private void Note(string key, string line, bool error = false, GateHolder? heldBy = null)
    {
        long now = NowMs();
        if (key == _stateKey) { _stateSeen++; return; }
        string before = _stateKey is not null && _stateSeen > 1
            ? $" (before that: {_stateKey}, seen {_stateSeen} times over {Dur(now - _stateSince)})"
            : "";
        (error ? Error : Info)(line + before);
        _stateKey = key;
        _stateSeen = 1;
        _stateSince = now;
        _heldBy = heldBy;
    }

    /// <summary>Who put the live profile back while the loop was not the one to: a pass of this launcher the loop did
    /// not start (seen since the previous look), the holder of the gate the last line named when it is one that puts
    /// the profile back, else another Relic process.</summary>
    private string PutBackBy(bool outside) =>
        outside ? "put back by another recovery pass of this launcher (its start, or Restore)"
        : _heldBy is { } holder && SessionGate.PutsProfileBack(holder) ? "put back by " + SessionGate.Describe(holder)
        : "put back outside this loop (another Relic process)";

    private void EndDeferral(string how)
    {
        long now = NowMs();
        string last = _stateKey is not null && _stateSeen > 1
            ? $"; its last state ({_stateKey}) was seen {_stateSeen} times over {Dur(now - _stateSince)}"
            : "";
        Info($"profile recovery ({Origin}): the deferral ended after {_passes} {(_passes == 1 ? "pass" : "passes")} over {Dur(now - _pendingSince)} — {how}{last}");
        _phase = Phase.Idle;
        _stateKey = null;
        _stateSeen = 0;
        _heldBy = null;
        _passes = 0;
        ResetCadence();
    }

    private void ResetCadence()
    {
        _lastEnd = null;
        _lastResult = null;
        _baseline = null;
        _changedUnder = false;
        _unlistedRetried = false;
    }

    /// <summary>A look that failed: in full the first time, the same failure again only counted — a marker that stays
    /// unreadable must not write a stack trace at every look.</summary>
    private void LookFailed(Exception ex)
    {
        string key = FirstLine(ex.ToString());
        long now = NowMs();
        if (key == _failKey) { _failSeen++; return; }
        string before = _failKey is not null && _failSeen > 1
            ? $" (before that: {_failKey}, seen {_failSeen} times over {Dur(now - _failSince)})"
            : "";
        Error($"recovery loop iteration failed (non-fatal): {ex}{before}");
        _failKey = key;
        _failSeen = 1;
        _failSince = now;
    }

    /// <summary>A look that worked after failed ones: the tally of the repeats, once.</summary>
    private void LookWorked()
    {
        if (_failKey is null) return;
        if (_failSeen > 1)
            Info($"recovery loop: its looks work again; the last failure ({_failKey}) was seen {_failSeen} times over {Dur(NowMs() - _failSince)}");
        _failKey = null;
        _failSeen = 0;
    }

    private static string HeldKey(GateHolder holder) => $"held: {holder}";

    /// <summary>The set of clients by pid and full path, in a form two looks can be compared by.</summary>
    private static string Key(IReadOnlyList<ProcessGuard.GameProcess> clients) =>
        string.Join(";", clients.OrderBy(c => c.Pid).Select(c => $"{c.Pid}={c.Path.ToUpperInvariant()}"));

    private static string Describe(IReadOnlyList<ProcessGuard.GameProcess> clients) =>
        clients.Count == 0
            ? "none"
            : string.Join(", ", clients.Select(c => c.Path.Length > 0 ? $"pid {c.Pid} '{c.Path}'" : $"pid {c.Pid} (path unreadable)"));

    private static string FirstLine(string text)
    {
        int nl = text.IndexOfAny(new[] { '\r', '\n' });
        return nl < 0 ? text : text[..nl];
    }

    private static string Dur(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{t.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s";
    }
}
