using System.Diagnostics;
using System.Text.Json;
using Relic.Core.Isolation;
using Relic.Core.Launch;
using Relic.Core.Play;
using Relic.Core.Util;

/// <summary>
/// The end of a play session and the launcher's recovery loop, without the game, Fiddler or a profile. The settle
/// window keeps its length (5 s, 15 s when the client was never seen) — the post-exit step chooses it, no caller hands
/// it one — and Fiddler is never stopped before it ends; a FOREIGN client still stops Fiddler at the end of the window
/// and defers the restore. The session's notices come in order, the "game has closed" one as soon as the exit is
/// noticed; PlaySession's own order is read from its source. The recovery loop's beat runs a full pass only when the
/// clients changed, and at the latest 10 s after the previous pass ended; a session that ends deferred hands the
/// restore over at once; the loop logs once per change of state and once when the deferral ends, names what holds the
/// session gate, does nothing — no process list, no notice — while nothing is deferred, and starts no pass once it is
/// stopped. The stand-in clients are copies of cmd.exe under a name of their own (a stand-in called GenshinImpact.exe
/// would be a client to any real Relic on this PC); the gate a pass meets here is a spike-only semaphore, never the
/// launcher's. The scenarios on the real clock that a loaded PC can stretch are tried once more when a check of theirs
/// fails (<see cref="Retried"/>), the first try's failures printed, never hidden.
/// </summary>
static class SessionSpike
{
    /// <summary>The stand-ins' image name; each case appends a letter. Never the game's.</summary>
    private const string StandInName = "RelicSessionStandIn";

    private static readonly ProcessGuard.GameProcess Official =
        new(9001, @"C:\Program Files\HoYoPlay\games\Genshin Impact game\GenshinImpact.exe");

    public static int Run()
    {
        Console.WriteLine("== session spike: the settle window, the session end's notices, the recovery loop (stand-ins only) ==");
        var results = new List<(string name, bool ok)>();
        var sync = new object();
        void Check(string name, bool ok)
        {
            lock (sync)
            {
                results.Add((name, ok));
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            }
        }

        string tmp = Path.Combine(Path.GetTempPath(), "relic_sessionspike_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var standIns = new StandIns(tmp);
        try
        {
            Constants(Check);
            Cadence(Check);
            GateWords(Check);
            LoopQuiet(Check);
            LoopDeferral(Check);
            LoopHandOver(Check);
            LoopStates(Check);
            LoopHolders(Check);
            LoopOutsidePass(Check);
            LoopRealGate(Check);
            LoopRun(Check);
            LoopWake(Check);
            ClosingNotice(Check);
            SessionSource(Check);
            Task.WhenAll(SessionEnds(Check, standIns), LoopLive(Check, standIns), LoopLiveHandOver(Check, standIns))
                .GetAwaiter().GetResult();
            ExitNotices(Check, standIns).GetAwaiter().GetResult();
            Texts(Check);
        }
        catch (Exception ex)
        {
            Check($"the spike ran to its end (it threw {ex.GetType().Name}: {ex.Message})", false);
        }
        finally
        {
            standIns.Dispose();
            try { Directory.Delete(tmp, true); } catch { /* best effort */ }
        }

        int fail = results.Count(r => !r.ok);
        Console.WriteLine();
        Console.WriteLine($"session spike: {results.Count - fail}/{results.Count} checks passed");
        return fail == 0 ? 0 : 1;
    }

    // ── the numbers the session end and the loop rest on ──

    private static void Constants(Action<string, bool> check)
    {
        check("the settle window keeps its length: 5 s, 15 s when the client was never seen, a look every 500 ms",
            ProcessGuard.SettleWindow == TimeSpan.FromSeconds(5)
            && ProcessGuard.SettleWindowNeverSeen == TimeSpan.FromSeconds(15)
            && ProcessGuard.SettleWindowFor(clientNeverSeen: false) == ProcessGuard.SettleWindow
            && ProcessGuard.SettleWindowFor(clientNeverSeen: true) == ProcessGuard.SettleWindowNeverSeen
            && ProcessGuard.SettleStep == TimeSpan.FromMilliseconds(500));
        check("the recovery loop: a look every 10 s while nothing is deferred, a 2 s beat while a restore is, a pass at the latest 10 s after the previous one ended (sooner when the clients change); its passes run as origin 'loop'",
            RecoveryLoop.IdleTick == TimeSpan.FromSeconds(10) && RecoveryLoop.BeatTick == TimeSpan.FromSeconds(2)
            && RecoveryLoop.Floor == TimeSpan.FromSeconds(10) && RecoveryLoop.Origin == "loop");
        check("the stand-ins never carry the game's name",
            ProcessGuard.ClientProcessName == "GenshinImpact"
            && !StandInName.StartsWith(ProcessGuard.ClientProcessName, StringComparison.OrdinalIgnoreCase));
        check("the decision table's reasons are the ones relic.log and a deferred pass carry",
            PlaySession.PostExitDecision(true, true, false).Reason == PlaySession.ReasonOwn
            && PlaySession.PostExitDecision(false, false, true).Reason == PlaySession.ReasonUnknown
            && PlaySession.PostExitDecision(false, true, false).Reason == PlaySession.ReasonForeign
            && PlaySession.PostExitDecision(false, false, false).Reason == PlaySession.ReasonNone
            && PlaySession.ReasonForeign == "foreign client running" && PlaySession.ReasonOwn == "own client still running");
        // No member of the post-exit step's steps can carry a window: not a length, not a function of one.
        static bool CarriesTime(Type t) => t == typeof(TimeSpan) || t == typeof(TimeSpan?)
            || (t.IsGenericType && t.GetGenericArguments().Any(CarriesTime));
        var steps = typeof(SessionEndSteps).GetProperties();
        check("the post-exit step takes no window from its caller: its steps carry the session's clients, never a length or an observer of one",
            steps.Length > 0 && !steps.Any(p => CarriesTime(p.PropertyType))
            && steps.Any(p => p.Name == nameof(SessionEndSteps.OurGameDirs)) && steps.Any(p => p.Name == nameof(SessionEndSteps.ProcessName))
            && new SessionEndSteps { OurGameDirs = Array.Empty<string>(), StopFiddler = () => { }, RestoreLive = () => { }, Taint = () => { } }
                .ProcessName == ProcessGuard.ClientProcessName);
    }

    private static void Cadence(Action<string, bool> check)
    {
        const long end = 50_000;
        var rows = new (string What, long? LastEnd, RecoveryResult? Last, bool Changed, long Now, bool Due)[]
        {
            ("no pass yet in this deferral", null, null, false, end, true),
            ("deferred, clients unchanged, 9.9 s after the end", end, RecoveryResult.Deferred, false, end + 9_900, false),
            ("deferred, clients unchanged, 10 s after the end (the floor)", end, RecoveryResult.Deferred, false, end + 10_000, true),
            ("deferred, clients changed, 0.1 s after the end", end, RecoveryResult.Deferred, true, end + 100, true),
            ("a pass the loop only waited for (result unknown), clients changed", end, null, true, end + 2_000, true),
            ("a pass the loop only waited for, unchanged, under the floor", end, null, false, end + 2_000, false),
            ("gate held, clients changed, under the floor: only the floor", end, RecoveryResult.GateBusy, true, end + 2_000, false),
            ("gate held, at the floor", end, RecoveryResult.GateBusy, false, end + 10_000, true),
            ("failed, clients changed, under the floor: only the floor", end, RecoveryResult.Failed, true, end + 9_999, false),
            ("failed, at the floor", end, RecoveryResult.Failed, true, end + 10_000, true),
        };
        var wrong = rows.Where(r => RecoveryLoop.PassDue(r.LastEnd, r.Last, r.Changed, r.Now) != r.Due).Select(r => r.What).ToList();
        check($"the cadence rule, row by row ({rows.Length} rows){(wrong.Count > 0 ? ": wrong for " + string.Join("; ", wrong) : "")}",
            wrong.Count == 0);
    }

    private static void GateWords(Action<string, bool> check)
    {
        var rows = new (bool Play, bool Install, bool Files, bool Job, bool Pass, GateHolder Want)[]
        {
            (false, false, false, false, false, GateHolder.None),
            (true, false, false, false, false, GateHolder.PlaySession),
            (true, true, false, true, false, GateHolder.PlaySession), // an install asked for during a session: the session holds
            (false, true, false, true, false, GateHolder.InstallJob),
            (false, true, false, false, false, GateHolder.Removal),
            (false, true, true, false, false, GateHolder.GameFiles),
            (false, false, false, false, true, GateHolder.RecoveryPass),
            // A recovery pass is counted while it HOLDS the gate; the others set their flag before they ask for it (a
            // session or a job may be waiting for that very pass): the pass is the holder.
            (false, true, false, true, true, GateHolder.RecoveryPass),
            (true, false, false, false, true, GateHolder.RecoveryPass),
        };
        var wrong = rows.Where(r => SessionGate.InProcess(r.Play, r.Install, r.Files, r.Job, r.Pass) != r.Want)
            .Select(r => r.Want.ToString()).ToList();
        check($"the gate's holder in this launcher from its flags ({rows.Length} rows){(wrong.Count > 0 ? ": wrong for " + string.Join(", ", wrong) : "")}",
            wrong.Count == 0);
        check("the holder in words: a session, an install / import / voice download, a removal, the game files or a recovery pass of this launcher — else another Relic process",
            SessionGate.Describe(GateHolder.PlaySession) == "a play session in this launcher"
            && SessionGate.Describe(GateHolder.InstallJob) == "an install, an import or a voice download in this launcher"
            && SessionGate.Describe(GateHolder.Removal) == "a version removal in this launcher"
            && SessionGate.Describe(GateHolder.GameFiles) == "Apply Relic's game files in this launcher"
            && SessionGate.Describe(GateHolder.RecoveryPass) == "a recovery pass of this launcher"
            && SessionGate.Describe(GateHolder.None) == SessionGate.OtherProcess
            && SessionGate.OtherProcess.StartsWith("another Relic process", StringComparison.Ordinal));
        check("held by whom: another process and a pass of this launcher are certain holders, a holder known by its flag may be about to take the gate",
            SessionGate.HeldBy(GateHolder.None) == "held by " + SessionGate.OtherProcess
            && SessionGate.HeldBy(GateHolder.RecoveryPass) == "held by a recovery pass of this launcher"
            && SessionGate.HeldBy(GateHolder.InstallJob) == "held (or about to be taken) by an install, an import or a voice download in this launcher"
            && SessionGate.HeldBy(GateHolder.PlaySession) == "held (or about to be taken) by a play session in this launcher");
        check("who puts the profile back itself: a session, a removal, a recovery pass — never an install or the game files",
            SessionGate.PutsProfileBack(GateHolder.PlaySession) && SessionGate.PutsProfileBack(GateHolder.Removal)
            && SessionGate.PutsProfileBack(GateHolder.RecoveryPass) && !SessionGate.PutsProfileBack(GateHolder.InstallJob)
            && !SessionGate.PutsProfileBack(GateHolder.GameFiles) && !SessionGate.PutsProfileBack(GateHolder.None));
    }

    // ── the recovery loop on a clock of its own ──

    private sealed record FakePass(RecoveryPass Pass, long TakesMs, List<ProcessGuard.GameProcess>? ClientsAfter);

    /// <summary>The loop with every input faked: the marker, the holder, the clients, the passes (each taking a stated
    /// time on the fake clock), the log and the notices.</summary>
    private sealed class LoopEnv
    {
        public long Now = 1_000_000;
        public long LastLookAt;
        public bool Live;
        public GateHolder Holder;
        public bool FiddlerUp;
        public List<ProcessGuard.GameProcess> ClientsNow = new();
        public int ClientCalls;
        public readonly Queue<FakePass> Passes = new();
        public readonly List<long> PassStarts = new();
        public readonly List<long> PassEnds = new();
        public readonly List<string> Lines = new();
        public readonly List<SessionNotice> Events = new();
        public Task? InFlight;
        public Func<Task<RecoveryPass>>? CustomPass;
        public Action? OnClients;
        public readonly RecoveryLoop Loop;

        public LoopEnv()
        {
            Loop = new RecoveryLoop(() => Live, () => Holder, RunPass, () => InFlight)
            {
                Clients = () => { ClientCalls++; OnClients?.Invoke(); return ClientsNow.ToList(); },
                FiddlerRunning = () => FiddlerUp,
                NowMs = () => Now,
                Info = m => Lines.Add("INFO " + m),
                Error = m => Lines.Add("ERROR " + m),
            };
            Loop.Recovered += n => Events.Add(n);
        }

        private Task<RecoveryPass> RunPass()
        {
            PassStarts.Add(Now);
            if (CustomPass is not null)
            {
                var t = CustomPass();
                PassEnds.Add(Now);
                return t;
            }
            var p = Passes.Count > 0
                ? Passes.Dequeue()
                : new FakePass(new RecoveryPass(RecoveryResult.Deferred, PlaySession.ReasonForeign), 5_100, null);
            Now += p.TakesMs;
            if (p.ClientsAfter is not null) ClientsNow = p.ClientsAfter;
            PassEnds.Add(Now);
            return Task.FromResult(p.Pass);
        }

        /// <summary>One look; then the clock moves on by the wait the loop asked for.</summary>
        public TimeSpan Step()
        {
            LastLookAt = Now;
            var wait = Loop.StepAsync().GetAwaiter().GetResult();
            Now += (long)wait.TotalMilliseconds;
            return wait;
        }

        public void StepUntilPasses(int count, long limitMs = 120_000)
        {
            long until = Now + limitMs;
            while (PassStarts.Count < count && Now < until) Step();
        }
    }

    /// <summary>The result a session's post-exit step raises when it deferred the restore.</summary>
    private static SessionNotice DeferredEnd(string kind) =>
        new(kind, "deferred") { VersionId = "2.8", Restored = false, Appeared = true };

    private static void LoopQuiet(Action<string, bool> check)
    {
        var e = new LoopEnv { Live = true };
        var waits = Enumerable.Range(0, 30).Select(_ => e.Step()).ToList();
        check("loop, nothing deferred: one look every 10 s — no process list, no pass, no line, no notice",
            waits.All(w => w == RecoveryLoop.IdleTick) && e.ClientCalls == 0 && e.PassStarts.Count == 0
            && e.Lines.Count == 0 && e.Events.Count == 0);

        e.Live = false;
        e.Holder = GateHolder.PlaySession;
        waits = Enumerable.Range(0, 30).Select(_ => e.Step()).ToList();
        check("loop, a play session of this launcher that began from the live profile: still 10 s and silent — it puts the profile back itself",
            waits.All(w => w == RecoveryLoop.IdleTick) && e.ClientCalls == 0 && e.PassStarts.Count == 0
            && e.Lines.Count == 0 && e.Events.Count == 0);

        e.Live = true;
        e.Holder = GateHolder.None;
        e.Loop.SessionEnded(new SessionNotice(SessionNotice.KindRestored, "back") { Restored = true });
        for (int i = 0; i < 5; i++) e.Step();
        check("loop, that session put the live profile back (its result hands nothing over): nothing to say, nothing listed",
            e.ClientCalls == 0 && e.Lines.Count == 0 && e.Events.Count == 0);
    }

    private static void LoopDeferral(Action<string, bool> check)
    {
        // A session ends with the official client running on the loaded profile, and nothing hands the restore over
        // (a session that failed at its end, or one of another process): the loop finds it at its next look.
        var e = new LoopEnv { Live = false, Holder = GateHolder.PlaySession };
        e.Step();
        e.Holder = GateHolder.None;
        e.ClientsNow = new() { Official };
        long first = e.Now;
        var w1 = e.Step();
        check("deferral found by a look: the full pass at once",
            e.PassStarts.Count == 1 && e.PassStarts[0] == first);
        check("deferral: one line — deferred, why, whether Fiddler was stopped, the clients by full path",
            e.Lines.Count == 1
            && e.Lines[0] == $"INFO profile recovery (loop) deferred: foreign client running; fiddler stopped=True; clients: pid 9001 '{Official.Path}'");
        check("deferral: from there the 2 s beat", w1 == RecoveryLoop.BeatTick);

        int callsBefore = e.ClientCalls;
        e.StepUntilPasses(2);
        long gap = e.PassStarts.Count >= 2 ? e.PassStarts[1] - e.PassEnds[0] : -1;
        check($"beat, clients unchanged: the next full pass comes at the floor, 10 s after the previous pass ENDED (gap {gap} ms), with a beat every 2 s in between",
            gap == 10_000 && e.ClientCalls - callsBefore >= 5);
        check("beat, the same state again: no new line", e.Lines.Count == 1);

        // The official client closes right after a look.
        e.Step();
        long lookBeforeChange = e.LastLookAt;
        e.ClientsNow = new();
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Restored, "priv28"), 5_100, null));
        e.StepUntilPasses(3);
        long react = e.PassStarts.Count >= 3 ? e.PassStarts[2] - lookBeforeChange : -1;
        check($"beat, a client closed: the full pass at the next beat ({react} ms after the last look that still saw it)",
            react is > 0 and <= 2_000);
        check("the deferral ended: one line — the passes, the time, how it ended, the last state's tally",
            e.Lines.Count == 2
            && e.Lines[1].StartsWith("INFO profile recovery (loop): the deferral ended after 3 passes over ", StringComparison.Ordinal)
            && e.Lines[1].Contains("— restored (the progress of 'priv28' saved); its last state (deferred: foreign client running) was seen 2 times over ", StringComparison.Ordinal));
        check("the deferral's end: one notice once the pass is done — recovered, the profile, Fiddler stopped, in the notice's words",
            e.Events.Count == 1 && e.Events[0].Kind == SessionNotice.KindRecovered && e.Events[0].Profile == "priv28"
            && e.Events[0].FiddlerStopped && e.Events[0].Restored && e.Events[0].Appeared is null
            && e.Events[0].Text == L.T("core.play.recovered"));

        e.Live = true;
        int callsAtEnd = e.ClientCalls;
        var waits = Enumerable.Range(0, 20).Select(_ => e.Step()).ToList();
        check("after the deferral: back to a look every 10 s — no beat, no line, no notice",
            waits.All(w => w == RecoveryLoop.IdleTick) && e.ClientCalls == callsAtEnd && e.Lines.Count == 2 && e.Events.Count == 1);
    }

    private static void LoopHandOver(Action<string, bool> check)
    {
        // A session of this launcher ends with the official client running on the loaded profile and hands the
        // restore over: its window just ran, so it is the previous pass.
        var e = new LoopEnv { Live = false, Holder = GateHolder.PlaySession, ClientsNow = new() { Official } };
        e.Step();
        e.Holder = GateHolder.None;
        long handOverAt = e.Now;
        e.Loop.SessionEnded(DeferredEnd(SessionNotice.KindDeferredForeign));
        var w = e.Step();
        check("hand-over: the session's window counts as the previous pass — no pass at once, one line, the beat from here",
            e.PassStarts.Count == 0 && w == RecoveryLoop.BeatTick && e.Lines.Count == 1
            && e.Lines[0] == "INFO profile recovery (loop): a play session of this launcher ended with the restore deferred (deferredForeign) — from here the beat follows the clients");
        e.StepUntilPasses(1);
        long firstGap = e.PassStarts.Count > 0 ? e.PassStarts[0] - handOverAt : -1;
        check($"hand-over, the client plays on: the first pass at the floor counted from the hand-over ({firstGap} ms)",
            e.PassStarts.Count == 1 && firstGap == 10_000);

        // The official client closes a moment after the hand-over: the pass at the next beat.
        e = new LoopEnv { Live = false, Holder = GateHolder.PlaySession, ClientsNow = new() { Official } };
        e.Step();
        e.Holder = GateHolder.None;
        handOverAt = e.Now;
        e.Loop.SessionEnded(DeferredEnd(SessionNotice.KindDeferredForeign));
        e.Step();
        e.ClientsNow = new();
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Restored, "priv28"), 5_100, null));
        e.StepUntilPasses(1);
        long react = e.PassStarts.Count > 0 ? e.PassStarts[0] - handOverAt : -1;
        check($"hand-over, the client closes after it: the pass at the next beat ({react} ms after the hand-over)",
            e.PassStarts.Count == 1 && react == 2_000);
        check("hand-over, then the restoring pass: the end line counts the deferral from the hand-over; one notice",
            e.Lines.Count == 2
            && e.Lines[1].StartsWith("INFO profile recovery (loop): the deferral ended after 1 pass over 7.1s — restored (the progress of 'priv28' saved)", StringComparison.Ordinal)
            && e.Events.Count == 1 && e.Events[0].Kind == SessionNotice.KindRecovered);

        // Nothing listed at the hand-over's look: the client the session's window saw is gone already.
        e = new LoopEnv { Live = false };
        e.Loop.SessionEnded(DeferredEnd(SessionNotice.KindDeferredOwn));
        e.Step();
        check("hand-over, no client listed at its look: the client the session's window saw is gone — the pass at once",
            e.PassStarts.Count == 1 && e.PassStarts[0] == e.LastLookAt);

        // A result that puts the profile back, and no result at all, hand nothing over.
        e = new LoopEnv { Live = false, ClientsNow = new() { Official } };
        e.Loop.SessionEnded(new SessionNotice(SessionNotice.KindRestored, "back") { Restored = true });
        e.Loop.SessionEnded(null);
        e.Step();
        check("a session that put the profile back, or one without a result, hands nothing over: a look finds the deferral and runs the pass at once",
            e.PassStarts.Count == 1 && !e.Lines.Any(l => l.Contains("ended with the restore deferred", StringComparison.Ordinal)));

        // The session that handed over still reads as the holder for a moment: the beat, silently; then the hand-over.
        e = new LoopEnv { Live = false, Holder = GateHolder.PlaySession, ClientsNow = new() { Official } };
        e.Step();
        e.Loop.SessionEnded(DeferredEnd(SessionNotice.KindDeferredForeign));
        var w1 = e.Step();
        int linesAt1 = e.Lines.Count;
        e.Holder = GateHolder.None;
        var w2 = e.Step();
        check("hand-over while the session still holds the gate: looked at again at the beat, silently — then taken over, no pass yet",
            w1 == RecoveryLoop.BeatTick && linesAt1 == 0 && w2 == RecoveryLoop.BeatTick && e.PassStarts.Count == 0
            && e.Lines.Count == 1 && e.Lines[0].Contains("ended with the restore deferred (deferredForeign)", StringComparison.Ordinal));

        // A hand-over older than a beat that meets a play session: a new session holds the gate and takes over.
        e = new LoopEnv { Live = false, Holder = GateHolder.PlaySession };
        e.Step();
        e.Loop.SessionEnded(DeferredEnd(SessionNotice.KindDeferredOwn));
        e.Now += 3_000;
        var ws = e.Step();
        e.Holder = GateHolder.None;
        e.Live = true;
        e.Step();
        check("a hand-over that meets a new session's gate is that session's: the loop idles and says nothing",
            ws == RecoveryLoop.IdleTick && e.Lines.Count == 0 && e.PassStarts.Count == 0 && e.ClientCalls == 0);
    }

    private static void LoopStates(Action<string, bool> check)
    {
        // The reason changes: one line each, the previous state's tally in the second.
        var e = new LoopEnv { Live = false };
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Deferred, PlaySession.ReasonOwn), 300, null));
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Deferred, PlaySession.ReasonOwn), 300, null));
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Deferred, PlaySession.ReasonForeign), 5_100, null));
        e.StepUntilPasses(3);
        check("a change of state: one line, with how often and how long the previous one was seen",
            e.Lines.Count == 2
            && e.Lines[0] == "INFO profile recovery (loop) deferred: own client still running; fiddler stopped=False; clients: none"
            && e.Lines[1].StartsWith("INFO profile recovery (loop) deferred: foreign client running; fiddler stopped=True; clients: none (before that: deferred: own client still running, seen 2 times over ", StringComparison.Ordinal));

        // A failure: in full once, repeats counted, another failure logged again; only the floor after it.
        e = new LoopEnv { Live = false };
        string boom = "System.IO.IOException: the profile is in use\r\n   at Relic.Core.Isolation.ProfileStore.LoadProfile(String id)";
        string denied = "System.UnauthorizedAccessException: denied\r\n   at Relic.Core.Isolation.ProfileStore.SaveProfile(String id)";
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Failed, boom), 5_200, new() { Official }));
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Failed, boom), 5_200, new()));
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Failed, denied), 5_200, null));
        e.StepUntilPasses(3);
        bool floorOnly = e.PassStarts.Count == 3
            && e.PassStarts[1] - e.PassEnds[0] == 10_000 && e.PassStarts[2] - e.PassEnds[1] == 10_000;
        check("a failed pass: logged in full once, the same failure counted, another one logged again (error level)",
            e.Lines.Count == 2 && e.Lines[0] == "ERROR profile recovery (loop) failed: " + boom
            && e.Lines[1].StartsWith("ERROR profile recovery (loop) failed: " + denied + " (before that: failed: System.IO.IOException: the profile is in use, seen 2 times over ", StringComparison.Ordinal));
        check("after a failed pass only the floor applies — clients that changed under it or after it bring no pass sooner (no retry storm)", floorOnly);

        // The clients changed while the pass ran: the next beat looks again even though they equal what the pass left.
        e = new LoopEnv { Live = false, ClientsNow = new() { Official } };
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Deferred, PlaySession.ReasonForeign), 5_100, new()));
        e.Step();
        e.Step();
        check("clients that changed while the pass ran: the pass again at the next beat (2 s), not at the floor",
            e.PassStarts.Count == 2 && e.PassStarts[1] - e.PassEnds[0] == 2_000);

        // …and to another client that is still listed after it (the official one gone, ours back): the set after the
        // pass equals what the next look finds, yet the pass decided on another one.
        e = new LoopEnv { Live = false, ClientsNow = new() { Official } };
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Deferred, PlaySession.ReasonForeign), 5_100,
            new() { new(9002, @"D:\Games\Relic 2.8\GenshinImpact.exe") }));
        e.Step();
        e.Step();
        check("clients that changed while the pass ran to another one, still listed after it: the pass again at the next beat",
            e.PassStarts.Count == 2 && e.PassStarts[1] - e.PassEnds[0] == 2_000);

        // One client gone and another in its place between two looks (ours closed, the official one started): the same
        // number of clients, another set.
        e = new LoopEnv { Live = false, ClientsNow = new() { new(9002, @"D:\Games\Relic 2.8\GenshinImpact.exe") } };
        e.Step();
        e.ClientsNow = new() { Official };
        e.Step();
        check("a client replaced by another between two looks (the same count): the pass at the next beat",
            e.PassStarts.Count == 2 && e.PassStarts[1] - e.PassEnds[0] == 2_000);

        // A pass that deferred on a client none of the loop's looks listed (it came and went inside the window).
        e = new LoopEnv { Live = false };
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Deferred, PlaySession.ReasonForeign), 5_100, null));
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Deferred, PlaySession.ReasonForeign), 5_100, null));
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Restored, "priv28"), 5_100, null));
        e.StepUntilPasses(3);
        long retryGap = e.PassStarts.Count >= 2 ? e.PassStarts[1] - e.PassEnds[0] : -1;
        long thenGap = e.PassStarts.Count >= 3 ? e.PassStarts[2] - e.PassEnds[1] : -1;
        check($"a pass that deferred on a client no look listed: the pass again at the next beat ({retryGap} ms) — once in a row: a second such pass leaves it to the floor ({thenGap} ms)",
            retryGap == 2_000 && thenGap == 10_000 && e.Events.Count == 1);

        // Ended by a pass that found the live profile back already: a line, no notice.
        e = new LoopEnv { Live = false };
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Already, ""), 0, null));
        e.Step();
        check("a pass that finds the live profile back already: the deferral's end is logged, no notice",
            e.Lines.Count == 1 && e.Lines[0].Contains("— the live profile was back already", StringComparison.Ordinal) && e.Events.Count == 0);

        // Discarded (the slot was tainted) and repaired: their own notices.
        e = new LoopEnv { Live = false };
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Discarded, "priv16"), 5_100, null));
        e.Step();
        check("restored without exporting a tainted slot: the notice says so and names the profile",
            e.Events.Count == 1 && e.Events[0].Kind == SessionNotice.KindRecoveredDiscarded && e.Events[0].Profile == "priv16"
            && e.Events[0].Text == L.T("core.play.recovered") + " " + L.T("core.play.recoveredDiscarded", new { profile = "priv16" })
            && e.Lines.Count == 1 && e.Lines[0].Contains("— restored without exporting the tainted profile 'priv16'", StringComparison.Ordinal));
        e = new LoopEnv { Live = false };
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Repaired, ""), 900, null));
        e.Step();
        check("an interrupted swap repaired by the loop: its notice (no profile named)",
            e.Events.Count == 1 && e.Events[0].Kind == SessionNotice.KindRepaired && e.Events[0].Profile is null
            && e.Events[0].Text == L.T("core.play.tornRepaired"));

        // A Fiddler still up when the profile is back: the notice does not claim it stopped.
        e = new LoopEnv { Live = false, FiddlerUp = true };
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Restored, "priv28"), 5_100, null));
        e.Step();
        check("a Fiddler still running after the restore: the notice says only that the profile is back",
            e.Events.Count == 1 && !e.Events[0].FiddlerStopped && e.Events[0].Text == L.T("core.play.recoveredProfile"));

        // The notice comes once the pass task completed (the host's status then reads recovering: false).
        e = new LoopEnv { Live = false };
        Task<RecoveryPass>? passTask = null;
        bool completedAtNotice = false;
        e.CustomPass = () => passTask = Task.Run(() => { Thread.Sleep(30); return new RecoveryPass(RecoveryResult.Restored, "priv32"); });
        e.Loop.Recovered += _ => completedAtNotice = passTask is { IsCompleted: true };
        e.Step();
        check("the notice is raised after the pass completed", e.Events.Count == 1 && completedAtNotice);

        // A handler that throws: logged, the loop goes on.
        e = new LoopEnv { Live = false };
        e.Loop.Recovered += _ => throw new InvalidOperationException("the page is gone");
        e.Passes.Enqueue(new FakePass(new RecoveryPass(RecoveryResult.Restored, "priv28"), 5_100, null));
        var w = e.Step();
        check("a notice handler that throws is logged and the loop goes on",
            w == RecoveryLoop.IdleTick && e.Events.Count == 1
            && e.Lines.Any(l => l.StartsWith("ERROR profile recovery (loop): the recovered notice could not be delivered (non-fatal): System.InvalidOperationException: the page is gone", StringComparison.Ordinal)));
    }

    private static void LoopHolders(Action<string, bool> check)
    {
        var e = new LoopEnv { Live = false, ClientsNow = new() { Official } };
        e.Step();
        int calls = e.ClientCalls, passes = e.PassStarts.Count;
        e.Holder = GateHolder.InstallJob;
        var waits = Enumerable.Range(0, 10).Select(_ => e.Step()).ToList();
        check("held by an install of this launcher: one line naming it; no pass, no process list, its end looked for every 2 s",
            e.Lines.Count == 2
            && e.Lines[1] == "INFO profile recovery (loop) waits: the session gate is held (or about to be taken) by an install, an import or a voice download in this launcher"
            && e.PassStarts.Count == passes && e.ClientCalls == calls && waits.All(x => x == RecoveryLoop.BeatTick));
        e.Holder = GateHolder.Removal;
        e.Step();
        e.Holder = GateHolder.GameFiles;
        e.Step();
        e.Holder = GateHolder.PlaySession; // a play session that takes over the deferred restore
        e.Step();
        check("each holder of this launcher named once: a removal, the game files, a play session that takes over the restore",
            e.Lines.Count == 5
            && e.Lines[2].StartsWith("INFO profile recovery (loop) waits: the session gate is held (or about to be taken) by a version removal in this launcher (before that: held: InstallJob, seen 10 times over ", StringComparison.Ordinal)
            && e.Lines[3] == "INFO profile recovery (loop) waits: the session gate is held (or about to be taken) by Apply Relic's game files in this launcher"
            && e.Lines[4] == "INFO profile recovery (loop) waits: the session gate is held (or about to be taken) by a play session in this launcher");
        e.Live = true;
        e.Holder = GateHolder.None;
        e.Step();
        check("that session put the live profile back: the deferral's end names it, no notice of the loop's own",
            e.Lines.Count == 6 && e.Lines[5].Contains("— put back by a play session in this launcher", StringComparison.Ordinal)
            && e.Events.Count == 0);

        // A removal of this launcher puts the loaded profile back itself; an install never touches it.
        e = new LoopEnv { Live = false };
        e.Step();
        e.Holder = GateHolder.Removal;
        e.Step();
        e.Holder = GateHolder.None;
        e.Live = true;
        e.Step();
        string afterRemoval = e.Lines.Count > 0 ? e.Lines[^1] : "";
        e = new LoopEnv { Live = false };
        e.Step();
        e.Holder = GateHolder.InstallJob;
        e.Step();
        e.Holder = GateHolder.None;
        e.Live = true;
        e.Step();
        string afterInstall = e.Lines.Count > 0 ? e.Lines[^1] : "";
        check("the profile back after a removal of this launcher held the gate: the end names the removal — after an install, someone else",
            afterRemoval.Contains("— put back by a version removal in this launcher", StringComparison.Ordinal)
            && afterInstall.Contains("— put back outside this loop (another Relic process)", StringComparison.Ordinal));

        // A job of this launcher that starts while the clients are listed keeps the gate: the pass does not run.
        e = new LoopEnv { Live = false };
        e.OnClients = () => e.Holder = GateHolder.InstallJob;
        var w = e.Step();
        check("a job of this launcher that starts while the clients are listed: no pass meets it on the gate",
            e.PassStarts.Count == 0 && w == RecoveryLoop.BeatTick);
    }

    private static void LoopOutsidePass(Action<string, bool> check)
    {
        // The launcher's own start runs a pass before the loop's first look: the loop waits for it and counts the
        // floor from its end.
        var e = new LoopEnv { Live = false, ClientsNow = new() { Official } };
        var outside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        e.InFlight = outside.Task;
        var look = e.Loop.StepAsync();
        bool waited = !look.IsCompleted;
        e.Now += 5_100;
        long outsideEnd = e.Now;
        outside.SetResult();
        e.Now += (long)look.GetAwaiter().GetResult().TotalMilliseconds;
        e.StepUntilPasses(1);
        long gap = e.PassStarts.Count >= 1 ? e.PassStarts[0] - outsideEnd : -1;
        check($"a pass the loop did not start (its start, Restore): waited for, and the floor counted from its end (first own pass {gap} ms after it)",
            waited && gap == 10_000);
        check("…and that pass's own lines are its own: the loop says nothing until its first pass", e.Lines.Count == 1);

        // A client that closes while such a pass runs is a change: the loop's own pass right after it.
        e = new LoopEnv { Live = false, ClientsNow = new() { Official } };
        e.Step();
        var restore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        e.InFlight = restore.Task;
        var look2 = e.Loop.StepAsync();
        e.ClientsNow = new();
        e.Now += 5_100;
        long restoreEnd = e.Now;
        restore.SetResult();
        look2.GetAwaiter().GetResult();
        check("a client that closed while a pass the loop did not start ran counts as a change: the loop's pass right after that pass",
            e.PassStarts.Count == 2 && e.PassStarts[1] == restoreEnd);

        // A pass of this launcher that ran and finished between two looks, and put the profile back.
        e = new LoopEnv { Live = false };
        e.Step();
        e.InFlight = Task.CompletedTask;
        e.Live = true;
        e.Step();
        check("a pass of this launcher that ran between two looks and put the profile back: the deferral's end names it",
            e.Lines.Count == 2 && e.Lines[1].Contains("— put back by another recovery pass of this launcher (its start, or Restore)", StringComparison.Ordinal));

        // The launcher starts over a restore deferred earlier: its start's pass runs before the loop's first look — the
        // loop knows of no deferral yet — and the client that pass waits on closes while it runs.
        e = new LoopEnv { Live = false, ClientsNow = new() { Official } };
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        e.InFlight = startup.Task;
        var look3 = e.Loop.StepAsync();
        e.ClientsNow = new();
        e.Now += 5_100;
        long startupEnd = e.Now;
        startup.SetResult();
        look3.GetAwaiter().GetResult();
        check("the launcher's start over a deferred restore, its client closing while the start's pass runs: a change — the loop's own pass right after that pass, not at the floor",
            e.PassStarts.Count == 1 && e.PassStarts[0] == startupEnd);

        // Nothing deferred — the live profile loaded, or a play session of this launcher that began from it: a pass the
        // loop did not start (Restore) is waited for without a process list — also when the session loaded its profile
        // before the loop's next look saw it (the loop still idle, the marker no longer the live one's).
        var quiet = new List<(string What, LoopEnv Env, TimeSpan Wait)>();
        foreach (var (what, session, seen) in new[]
                 { ("the live profile loaded", false, false), ("a session the loop has seen", true, true), ("a session the loop has not seen yet", true, false) })
        {
            var q = new LoopEnv { Live = !session, Holder = session ? GateHolder.PlaySession : GateHolder.None };
            if (seen) q.Step(); // the session runs: the loop idles at 10 s
            var restorePass = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            q.InFlight = restorePass.Task;
            var look4 = q.Loop.StepAsync();
            restorePass.SetResult();
            quiet.Add((what, q, look4.GetAwaiter().GetResult()));
        }
        var listedQuiet = quiet.Where(r => !(r.Env.ClientCalls == 0 && r.Env.PassStarts.Count == 0 && r.Env.Lines.Count == 0 && r.Wait == RecoveryLoop.IdleTick))
            .Select(r => $"{r.What}: {r.Env.ClientCalls} process list(s), {r.Env.PassStarts.Count} pass(es), {r.Env.Lines.Count} line(s), next look in {r.Wait.TotalSeconds:0.#} s").ToList();
        check("…with nothing deferred (the live profile loaded, or a play session of this launcher that began from it — seen by the loop or not yet), a pass the loop did not start lists no process: no pass, no line"
              + (listedQuiet.Count > 0 ? " — " + string.Join("; ", listedQuiet) : ""),
            listedQuiet.Count == 0);
    }

    private static void LoopRealGate(Action<string, bool> check)
    {
        // The pass meets a held semaphore: a spike-only name, held by this thread, standing for another Relic process.
        string name = $@"Local\Relic.SessionSpike.{Guid.NewGuid():N}";
        using var held = new Semaphore(1, 1, name);
        held.WaitOne();
        try
        {
            var e = new LoopEnv { Live = false };
            e.CustomPass = () => Task.Run(() =>
            {
                using var gate = new Semaphore(1, 1, name);
                if (!gate.WaitOne(0)) return new RecoveryPass(RecoveryResult.GateBusy, "");
                try { return new RecoveryPass(RecoveryResult.Deferred, PlaySession.ReasonOwn); }
                finally { gate.Release(); }
            });
            var w = e.Step();
            check("a pass that finds the gate held by another process: the line says so",
                e.PassStarts.Count == 1 && e.Lines.Count == 1
                && e.Lines[0] == "INFO profile recovery (loop) waits: the session gate is held by " + SessionGate.OtherProcess);
            check($"…and after it only the floor: no beat, the next pass 10 s on (asked to wait {w.TotalMilliseconds} ms)",
                w == RecoveryLoop.Floor);
            long busyEnd = e.PassEnds[0];
            e.Now = busyEnd + 3_000;
            e.ClientsNow = new() { Official };
            var w2 = e.Step();
            e.Now = busyEnd + 10_000;
            e.Step();
            check("…a client that starts meanwhile brings no pass before the floor; at the floor the pass, the same state counted, not logged",
                e.PassStarts.Count == 2 && e.PassStarts[1] == busyEnd + 10_000 && w2 == TimeSpan.FromMilliseconds(7_000)
                && e.Lines.Count == 1);

            // The gate taken by a job of this launcher between the look and the pass: the line names that job.
            e = new LoopEnv { Live = false };
            e.CustomPass = () =>
            {
                e.Holder = GateHolder.Removal;
                return Task.FromResult(new RecoveryPass(RecoveryResult.GateBusy, ""));
            };
            e.Step();
            check("a gate taken by a job of this launcher just before the pass: the line names the job, not another process",
                e.Lines.Count == 1 && e.Lines[0] == "INFO profile recovery (loop) waits: the session gate is held (or about to be taken) by a version removal in this launcher");

            // The gate held by another pass of this launcher (counted where it holds it): named as that.
            e = new LoopEnv { Live = false };
            e.CustomPass = () =>
            {
                e.Holder = GateHolder.RecoveryPass;
                return Task.FromResult(new RecoveryPass(RecoveryResult.GateBusy, ""));
            };
            e.Step();
            check("a gate held by another recovery pass of this launcher: the line names that pass",
                e.Lines.Count == 1 && e.Lines[0] == "INFO profile recovery (loop) waits: the session gate is held by a recovery pass of this launcher");
        }
        finally { held.Release(); }
    }

    private static void LoopRun(Action<string, bool> check)
    {
        var lines = new List<string>();
        var waits = new List<TimeSpan>();
        using var cts = new CancellationTokenSource();
        bool threw = false;
        var loop = new RecoveryLoop(
            liveIsActive: () =>
            {
                if (!threw) { threw = true; throw new IOException("the marker is being replaced"); }
                return true;
            },
            holder: () => GateHolder.None,
            runPass: () => Task.FromResult(new RecoveryPass(RecoveryResult.Already, "")))
        {
            Clients = () => new List<ProcessGuard.GameProcess>(),
            FiddlerRunning = () => false,
            Delay = (t, ct) =>
            {
                waits.Add(t);
                if (waits.Count >= 5) cts.Cancel();
                return Task.CompletedTask;
            },
            Info = m => lines.Add("INFO " + m),
            Error = m => lines.Add("ERROR " + m),
        };
        bool ended = loop.RunAsync(cts.Token).Wait(TimeSpan.FromSeconds(10));
        check("RunAsync: a look that throws is logged once and the loop goes on at 10 s; a cancel ends it",
            ended && waits.Count == 5 && waits.All(w => w == RecoveryLoop.IdleTick)
            && lines.Count == 1 && lines[0].StartsWith("ERROR recovery loop iteration failed (non-fatal): System.IO.IOException: the marker is being replaced", StringComparison.Ordinal));

        // The same failure look after look: in full once, then counted; the tally once the looks work again; another
        // kind of failure in full again.
        var lines2 = new List<string>();
        using var cts2 = new CancellationTokenSource();
        int looks = 0;
        var loop2 = new RecoveryLoop(
            liveIsActive: () =>
            {
                int n = ++looks;
                if (n <= 4) throw new IOException("the marker is locked");
                if (n == 6) throw new UnauthorizedAccessException("the marker is denied");
                return true;
            },
            holder: () => GateHolder.None,
            runPass: () => Task.FromResult(new RecoveryPass(RecoveryResult.Already, "")))
        {
            Clients = () => new List<ProcessGuard.GameProcess>(),
            FiddlerRunning = () => false,
            Delay = (t, ct) =>
            {
                if (looks >= 8) cts2.Cancel();
                return Task.CompletedTask;
            },
            Info = m => lines2.Add("INFO " + m),
            Error = m => lines2.Add("ERROR " + m),
        };
        bool ended2 = loop2.RunAsync(cts2.Token).Wait(TimeSpan.FromSeconds(10));
        check("RunAsync: the same failure look after look is logged in full once and counted; the tally when the looks work again; another failure in full",
            ended2 && lines2.Count == 3
            && lines2[0].StartsWith("ERROR recovery loop iteration failed (non-fatal): System.IO.IOException: the marker is locked", StringComparison.Ordinal)
            && lines2[1].StartsWith("INFO recovery loop: its looks work again; the last failure (System.IO.IOException: the marker is locked) was seen 4 times over ", StringComparison.Ordinal)
            && lines2[2].StartsWith("ERROR recovery loop iteration failed (non-fatal): System.UnauthorizedAccessException: the marker is denied", StringComparison.Ordinal));

        // A look under a cancelled token starts no pass.
        var e = new LoopEnv { Live = false };
        var stopped = e.Loop.StepAsync(new CancellationToken(true)).GetAwaiter().GetResult();
        check("a look of a stopped loop starts no pass", e.PassStarts.Count == 0 && stopped == RecoveryLoop.IdleTick);
    }

    private sealed class WakeState
    {
        public int Looks, Passes, HolderNone, ListThread;
        public long ListedAt;
    }

    /// <summary>RunAsync on the real clock: a session's hand-over cuts the loop's 10 s wait short, from a thread of the
    /// loop's own; a stop ends the run, and while a pass runs, only with that pass.</summary>
    private static void LoopWake(Action<string, bool> check)
    {
        var s = new WakeState();
        using var cts = new CancellationTokenSource();
        var loop = new RecoveryLoop(
            liveIsActive: () => { Interlocked.Increment(ref s.Looks); return false; },
            holder: () => Volatile.Read(ref s.HolderNone) == 1 ? GateHolder.None : GateHolder.PlaySession,
            runPass: () =>
            {
                Interlocked.Increment(ref s.Passes);
                return Task.FromResult(new RecoveryPass(RecoveryResult.Deferred, PlaySession.ReasonForeign));
            })
        {
            Clients = () =>
            {
                if (Interlocked.CompareExchange(ref s.ListedAt, Stopwatch.GetTimestamp(), 0) == 0)
                    Volatile.Write(ref s.ListThread, Environment.CurrentManagedThreadId);
                return new List<ProcessGuard.GameProcess> { Official };
            },
            FiddlerRunning = () => false,
            Info = _ => { },
            Error = _ => { },
        };
        // Stopwatch time: the moments measured here are well under the 15 ms of the tick count.
        static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
        static string F(double ms) => ms.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var run = Task.Run(() => loop.RunAsync(cts.Token));
        bool firstLook = SpinUntil(() => Volatile.Read(ref s.Looks) >= 1, 3_000);
        Thread.Sleep(300); // the session runs: the loop is in its 10 s wait
        Volatile.Write(ref s.HolderNone, 1);
        long handOverAt = Stopwatch.GetTimestamp();
        loop.SessionEnded(DeferredEnd(SessionNotice.KindDeferredForeign));
        double callMs = Ms(handOverAt, Stopwatch.GetTimestamp());
        bool listed = SpinUntil(() => Interlocked.Read(ref s.ListedAt) != 0, 3_000);
        double latency = listed ? Ms(handOverAt, Interlocked.Read(ref s.ListedAt)) : -1;
        check($"RunAsync: a session's hand-over cuts the 10 s wait short — the loop looks {F(latency)} ms after it, on a thread of its own, the call itself {F(callMs)} ms; no pass at once",
            firstLook && listed && latency is >= 0 and < 1_000 && callMs < 100
            && Volatile.Read(ref s.ListThread) != Environment.CurrentManagedThreadId && Volatile.Read(ref s.Passes) == 0);
        long stopAt = Stopwatch.GetTimestamp();
        cts.Cancel();
        bool ended = run.Wait(3_000);
        check($"RunAsync: a stop ends the run at once ({F(Ms(stopAt, Stopwatch.GetTimestamp()))} ms), no pass started",
            ended && Volatile.Read(ref s.Passes) == 0);

        // Stopped while its pass runs (the exit): the run ends only with that pass, and starts no other.
        var gate = new TaskCompletionSource<RecoveryPass>(TaskCreationOptions.RunContinuationsAsynchronously);
        int passes = 0;
        using var cts2 = new CancellationTokenSource();
        var loop2 = new RecoveryLoop(() => false, () => GateHolder.None,
            () => { Interlocked.Increment(ref passes); return gate.Task; })
        {
            Clients = () => new List<ProcessGuard.GameProcess>(),
            FiddlerRunning = () => false,
            Info = _ => { },
            Error = _ => { },
        };
        var run2 = Task.Run(() => loop2.RunAsync(cts2.Token));
        bool started = SpinUntil(() => Volatile.Read(ref passes) == 1, 3_000);
        cts2.Cancel();
        bool waitedForPass = !run2.Wait(300);
        gate.SetResult(new RecoveryPass(RecoveryResult.Deferred, PlaySession.ReasonOwn));
        bool ended2 = run2.Wait(3_000);
        check("RunAsync stopped while its pass runs: the run ends only with that pass, and starts no other",
            started && waitedForPass && ended2 && Volatile.Read(ref passes) == 1);
    }

    private static bool SpinUntil(Func<bool> condition, int timeoutMs)
    {
        long until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        long until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    /// <summary>A pass of the host's in miniature on the real clock: a spike-only gate, the real settle window over the
    /// stand-ins, the decision table, a marker of its own.</summary>
    private static Func<Task<RecoveryPass>> LivePass(StandInCase c, string gateName, Action markLive,
        List<(long Start, long End, RecoveryResult Result)> passes) => () => Task.Run(async () =>
    {
        long start = Environment.TickCount64;
        using var gate = new Semaphore(1, 1, gateName);
        if (!gate.WaitOne(0)) return new RecoveryPass(RecoveryResult.GateBusy, "");
        try
        {
            var settle = await ProcessGuard.ObserveSustainedAsync(new[] { c.OursDir }, null, null, processName: c.Name);
            var d = PlaySession.PostExitDecision(settle.SawOurs, settle.SawForeign, settle.SawUnknown);
            var pass = d.RestoreProfile
                ? new RecoveryPass(RecoveryResult.Restored, "priv28")
                : new RecoveryPass(RecoveryResult.Deferred, d.Reason);
            if (d.RestoreProfile) markLive();
            lock (passes) passes.Add((start, Environment.TickCount64, pass.Result));
            return pass;
        }
        finally { gate.Release(); }
    });

    /// <summary>A scenario on the real clock, tried once more when a check of it fails: a loaded PC (a virtual machine
    /// beside it, the other spikes' processes) can stretch its waits. The first try's failures are printed, never hidden;
    /// the second try's checks are the scenario's. <paramref name="body"/> gets the try's number (1 or 2), so each try
    /// runs stand-ins of its own.</summary>
    private static async Task Retried(Action<string, bool> check, string label, Func<Action<string, bool>, int, Task> body)
    {
        for (int attempt = 1; ; attempt++)
        {
            var rows = new List<(string Name, bool Ok)>();
            await body((name, ok) => { lock (rows) rows.Add((name, ok)); }, attempt);
            List<(string Name, bool Ok)> got;
            lock (rows) got = rows.ToList();
            if (attempt == 2 || got.All(r => r.Ok))
            {
                foreach (var (name, ok) in got) check(name, ok);
                return;
            }
            foreach (var (name, _) in got.Where(r => !r.Ok))
                Console.WriteLine($"  [RETRY] {label}, the first try failed: {name}");
        }
    }

    /// <summary>The passes of a scenario on the real clock, for a check's name: when each ran, counted from the moment its
    /// client was stopped, and what it found.</summary>
    private static string PassList(List<(long Start, long End, RecoveryResult Result)> passes, long from) =>
        passes.Count == 0 ? "no pass" : string.Join(", ", passes.Select((x, i) =>
            $"pass {i + 1} {Seconds(x.Start - from)}..{Seconds(x.End - from)} s {x.Result}"));

    private static string Seconds(long ms) => (ms / 1000.0).ToString("+0.0;-0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The loop on the real clock with real stand-ins: a FOREIGN client runs on a loaded profile, then closes.</summary>
    private static Task LoopLive(Action<string, bool> check, StandIns standIns) =>
        Retried(check, "live loop", (rows, attempt) => LoopLiveOnce(rows, standIns, attempt == 1 ? "R" : "R2"));

    private static async Task LoopLiveOnce(Action<string, bool> check, StandIns standIns, string tag)
    {
        long floorMs = (long)RecoveryLoop.Floor.TotalMilliseconds, beatMs = (long)RecoveryLoop.BeatTick.TotalMilliseconds;
        try
        {
            var c = standIns.Case(tag);
            string gateName = $@"Local\Relic.SessionSpike.{Guid.NewGuid():N}";
            int live = 0;
            var passes = new List<(long Start, long End, RecoveryResult Result)>();
            var lines = new List<string>();
            var events = new List<(SessionNotice Notice, long At)>();
            var foreign = standIns.Start(c.ForeignExe, 120);
            bool seen = await StandIns.SeenAsync(c.Name);
            var loop = new RecoveryLoop(
                liveIsActive: () => Volatile.Read(ref live) == 1,
                holder: () => GateHolder.None,
                runPass: LivePass(c, gateName, () => Volatile.Write(ref live, 1), passes))
            {
                Clients = () => ProcessGuard.Running(c.Name),
                FiddlerRunning = () => false,
                Info = m => { lock (lines) lines.Add("INFO " + m); },
                Error = m => { lock (lines) lines.Add("ERROR " + m); },
            };
            loop.Recovered += n => { lock (events) events.Add((n, Environment.TickCount64)); };
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => loop.RunAsync(cts.Token));

            bool two = await WaitUntilAsync(() => { lock (passes) return passes.Count >= 2; }, 25_000);
            await Task.Delay(3_000); // the official client plays on, unchanged: no pass before the floor
            int passesBeforeExit;
            lock (passes) passesBeforeExit = passes.Count;
            // The exit timed on this thread: the stop kills the client and waits until it is gone, so no pass that sees it
            // gone can start before stopAt, and none is measured against a moment taken late.
            long stopAt = Environment.TickCount64;
            StandIns.Stop(foreign);
            long exitAt = Environment.TickCount64;
            bool three = await WaitUntilAsync(() => { lock (passes) return passes.Count >= 3; }, 15_000);
            await WaitUntilAsync(() => { lock (events) return events.Count > 0; }, 3_000);
            cts.Cancel();
            await Task.WhenAny(run, Task.Delay(15_000));

            List<(long Start, long End, RecoveryResult Result)> p;
            lock (passes) p = passes.ToList();
            List<string> l;
            lock (lines) l = lines.ToList();
            int noticeCount;
            lock (events) noticeCount = events.Count;
            string seenPasses = PassList(p, stopAt);
            long floorGap = p.Count >= 2 ? p[1].Start - p[0].End : -1;
            check($"live loop, a FOREIGN client runs: the window's full pass (deferred), then the next one at the floor ({floorGap} ms after the previous ENDED)",
                seen && two && p.Count >= 2 && p[0].Result == RecoveryResult.Deferred && p[0].End - p[0].Start >= 5_000
                && floorGap >= floorMs && floorGap < floorMs + 1_500 && p[1].Result == RecoveryResult.Deferred);
            // The next beat, not the floor: within a beat and a margin of the stop, before the floor of the pass before.
            long react = three && p.Count >= 3 ? p[2].Start - stopAt : -1;
            check($"live loop, the client closes: no pass meanwhile, the next beat runs the pass ({react} ms after the stop, the exit seen {exitAt - stopAt} ms after it), its window whole, the profile back — {passesBeforeExit} passes before the stop; {seenPasses}",
                three && p.Count >= 3 && passesBeforeExit == 2 && react >= 0 && react <= beatMs + 1_500 && p[2].Start < p[1].End + floorMs
                && p[2].End - p[2].Start >= 5_000 && p[2].Result == RecoveryResult.Restored);
            check($"live loop: one notice, after that pass; two lines for the whole deferral (deferred, then its end) — {noticeCount} notice(s), {l.Count} line(s){(l.Count != 2 ? ": " + string.Join(" | ", l) : "")}",
                three && p.Count >= 3 && noticeCount == 1 && events[0].Notice.Kind == SessionNotice.KindRecovered && events[0].At >= p[2].End
                && l.Count == 2 && l[0].StartsWith("INFO profile recovery (loop) deferred: foreign client running; fiddler stopped=True; clients: pid ", StringComparison.Ordinal)
                && l[1].Contains("the deferral ended after 3 passes over ", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            check($"live loop: ran (it threw {ex.GetType().Name}: {ex.Message})", false);
        }
    }

    /// <summary>The loop on the real clock with real stand-ins: a session of this launcher ends with the official client
    /// running and hands the restore over; the client closes a second later.</summary>
    private static Task LoopLiveHandOver(Action<string, bool> check, StandIns standIns) =>
        Retried(check, "live hand-over", (rows, attempt) => LoopLiveHandOverOnce(rows, standIns, attempt == 1 ? "S" : "S2"));

    private static async Task LoopLiveHandOverOnce(Action<string, bool> check, StandIns standIns, string tag)
    {
        long floorMs = (long)RecoveryLoop.Floor.TotalMilliseconds, beatMs = (long)RecoveryLoop.BeatTick.TotalMilliseconds;
        try
        {
            var c = standIns.Case(tag);
            string gateName = $@"Local\Relic.SessionSpike.{Guid.NewGuid():N}";
            int live = 0, holderNone = 0;
            var passes = new List<(long Start, long End, RecoveryResult Result)>();
            var lines = new List<string>();
            var events = new List<(SessionNotice Notice, long At)>();
            var foreign = standIns.Start(c.ForeignExe, 120);
            bool seen = await StandIns.SeenAsync(c.Name);
            var loop = new RecoveryLoop(
                liveIsActive: () => Volatile.Read(ref live) == 1,
                holder: () => Volatile.Read(ref holderNone) == 1 ? GateHolder.None : GateHolder.PlaySession,
                runPass: LivePass(c, gateName, () => Volatile.Write(ref live, 1), passes))
            {
                Clients = () => ProcessGuard.Running(c.Name),
                FiddlerRunning = () => false,
                Info = m => { lock (lines) lines.Add("INFO " + m); },
                Error = m => { lock (lines) lines.Add("ERROR " + m); },
            };
            loop.Recovered += n => { lock (events) events.Add((n, Environment.TickCount64)); };
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => loop.RunAsync(cts.Token));

            await Task.Delay(300); // the session runs: the loop idles at 10 s
            Volatile.Write(ref holderNone, 1);
            long handOverAt = Environment.TickCount64;
            loop.SessionEnded(DeferredEnd(SessionNotice.KindDeferredForeign));
            await Task.Delay(1_000); // the official client plays on for a second
            // The exit timed on this thread (see LoopLiveOnce).
            long stopAt = Environment.TickCount64;
            StandIns.Stop(foreign);
            long exitAt = Environment.TickCount64;
            bool one = await WaitUntilAsync(() => { lock (passes) return passes.Count >= 1; }, 15_000);
            await WaitUntilAsync(() => { lock (events) return events.Count > 0; }, 10_000);
            cts.Cancel();
            await Task.WhenAny(run, Task.Delay(15_000));

            List<(long Start, long End, RecoveryResult Result)> p;
            lock (passes) p = passes.ToList();
            List<string> l;
            lock (lines) l = lines.ToList();
            int noticeCount;
            lock (events) noticeCount = events.Count;
            // The next beat, not the floor: within a beat and a margin of the stop, before the floor the hand-over set.
            long react = one && p.Count >= 1 ? p[0].Start - stopAt : -1;
            long back = one && p.Count >= 1 ? p[0].End - stopAt : -1;
            check($"live hand-over: no pass while the client plays on; after it closes the next beat runs the pass ({react} ms after the stop, the exit seen {exitAt - stopAt} ms after it), its window whole — the profile back {back} ms after the stop; {PassList(p, stopAt)}",
                seen && one && p.Count == 1 && react >= 0 && react <= beatMs + 1_500 && p[0].Start < handOverAt + floorMs
                && p[0].End - p[0].Start >= 5_000 && p[0].Result == RecoveryResult.Restored && back <= beatMs + 8_000);
            check($"live hand-over: two lines (the hand-over, the deferral's end after 1 pass) and one notice after that pass — {noticeCount} notice(s), {l.Count} line(s){(l.Count != 2 ? ": " + string.Join(" | ", l) : "")}",
                one && p.Count == 1 && noticeCount == 1 && events[0].At >= p[0].End && l.Count == 2
                && l[0].Contains("ended with the restore deferred (deferredForeign)", StringComparison.Ordinal)
                && l[1].Contains("the deferral ended after 1 pass over ", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            check($"live hand-over: ran (it threw {ex.GetType().Name}: {ex.Message})", false);
        }
    }

    // ── the session end, with stand-in clients ──

    private static void ClosingNotice(Action<string, bool> check)
    {
        var withFiddler = SessionEnd.Closing("2.8", fiddler: true);
        var without = SessionEnd.Closing("2.8", fiddler: false);
        check("the closing notice: kind, version, its words (with or without Fiddler)",
            withFiddler.Kind == SessionNotice.KindClosing && withFiddler.VersionId == "2.8" && withFiddler.Appeared == true
            && !withFiddler.FiddlerStopped && !withFiddler.Restored
            && withFiddler.Text == L.T("core.play.closing") && without.Text == L.T("core.play.closingNoFiddler"));
    }

    /// <summary>PlaySession itself, read from its source (the spike cannot start a session): the wait for the exit, the
    /// Fiddler-closed throw, the closing notice in the same try, the post-exit step in its finally, the result, and the
    /// gate released last — and what the post-exit step is handed.</summary>
    private static void SessionSource(Action<string, bool> check)
    {
        string? repo = FindRepo();
        string path = repo is null ? "" : Path.Combine(repo, "app", "Relic.Core", "Play", "PlaySession.cs");
        string src = File.Exists(path) ? File.ReadAllText(path) : "";
        int At(string text, int from) => from < 0 ? -1 : src.IndexOf(text, from, StringComparison.Ordinal);
        int play = At("public async Task PlayAsync(", 0);
        int wait = At("appeared = await GameLauncher.WaitForExitAsync(", play);
        int fiddlerThrow = At("if (appeared) throw new FiddlerClosedException();", wait);
        const string closingCall = "if (appeared) Raise(GameClosed, SessionEnd.Closing(versionId, startFiddler));";
        int closing = At(closingCall, fiddlerThrow);
        int fin = At("finally", closing);
        int finish = At("Result = await SessionEnd.FinishAsync(new SessionEndSteps", fin);
        int callEnd = At("say: Log);", finish);
        int ended = At("Raise(Ended, Result);", callEnd);
        int release = At("gate.Release();", ended);
        int playEnd = At("private static void TaintForeign(", play);
        check($"PlaySession: the exit, the Fiddler-closed throw, the closing notice in the same try, the post-exit step in its finally, the result, the gate released last{(src.Length == 0 ? " (source not found: RELIC_REPO_ROOT names the repository)" : "")}",
            play > 0 && wait > play && fiddlerThrow > wait && closing > fiddlerThrow && fin > closing && finish > fin
            && callEnd > finish && ended > callEnd && release > ended && playEnd > release
            && At("Raise(GameClosed", closing + closingCall.Length) < 0 && At("Raise(Ended", ended + 1) < 0);
        string call = finish > 0 && callEnd > finish ? src[finish..callEnd] : "";
        check("PlaySession: the post-exit step gets the session's own folder and pre-launch pids, Fiddler only when the session runs it, the longer window only for a client never seen",
            call.Contains("OurGameDirs = new[] { inst.GameDir },", StringComparison.Ordinal)
            && call.Contains("PreExistingPids = handle?.PreExistingPids,", StringComparison.Ordinal)
            && call.Contains("fiddler: startFiddler", StringComparison.Ordinal)
            && call.Contains("clientNeverAppeared: gameNeverAppeared", StringComparison.Ordinal)
            && At("gameNeverAppeared = !appeared;", wait) is int never && never > wait && never < fiddlerThrow);
    }

    /// <summary>What a post-exit step did: the FIRST time of each act on the window's own clock (Environment.TickCount64)
    /// — a second stop must not hide an early one — and every act in order.</summary>
    private sealed class Recorder
    {
        public long? FiddlerAt, RestoreAt, TaintAt;
        public readonly List<string> Acts = new();
        public readonly List<string> Said = new();
        public readonly List<string> Diag = new();

        public void Say(string line) { lock (Said) Said.Add(line); }

        private void Act(string what, ref long? at)
        {
            lock (Acts)
            {
                Acts.Add(what);
                at ??= Environment.TickCount64;
            }
        }

        public SessionEndSteps Steps(string name, string oursDir) => new()
        {
            OurGameDirs = new[] { oursDir },
            ProcessName = name,
            StopFiddler = () => Act("fiddler", ref FiddlerAt),
            RestoreLive = () => Act("restore", ref RestoreAt),
            Taint = () => Act("taint", ref TaintAt),
            Diag = m => { lock (Diag) Diag.Add(m); },
            OwnedFiddlerPid = 4242,
        };
    }

    private sealed record StandInCase(string Name, string OursDir, string OursExe, string ForeignExe);

    /// <summary>Copies of cmd.exe under a stand-in name, run as "ping" for a while; every one started is stopped
    /// (with its children) at the end.</summary>
    private sealed class StandIns : IDisposable
    {
        private readonly string _root;
        private readonly string _cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        private readonly List<Process> _started = new();

        public StandIns(string root) => _root = root;

        /// <summary>A folder Relic would own and one it would not, each with the stand-in of case <paramref name="tag"/>.</summary>
        public StandInCase Case(string tag)
        {
            string name = StandInName + tag;
            string ours = Path.Combine(_root, tag, "ours");
            string foreign = Path.Combine(_root, tag, "foreign");
            Directory.CreateDirectory(ours);
            Directory.CreateDirectory(foreign);
            string oursExe = Path.Combine(ours, name + ".exe");
            string foreignExe = Path.Combine(foreign, name + ".exe");
            File.Copy(_cmd, oursExe, true);
            File.Copy(_cmd, foreignExe, true);
            return new StandInCase(name, ours, oursExe, foreignExe);
        }

        public Process Start(string exe, int seconds)
        {
            var p = Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = $"/c ping -n {seconds + 1} 127.0.0.1 >nul",
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("the stand-in did not start");
            lock (_started) _started.Add(p);
            return p;
        }

        public static void Stop(Process p)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* gone already */ }
            try { p.WaitForExit(5_000); } catch { /* gone already */ }
        }

        public static async Task<bool> SeenAsync(string name, int timeoutMs = 3_000)
        {
            long until = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < until)
            {
                if (ProcessGuard.Running(name).Count > 0) return true;
                await Task.Delay(50);
            }
            return false;
        }

        public void Dispose()
        {
            lock (_started)
            {
                foreach (var p in _started)
                {
                    Stop(p);
                    p.Dispose();
                }
                _started.Clear();
            }
        }
    }

    private static async Task SessionEnds(Action<string, bool> check, StandIns standIns)
    {
        // Every case has a stand-in name of its own, so they run side by side without seeing each other.
        async Task Case(string label, Func<Task> body)
        {
            try { await body(); }
            catch (Exception ex) { check($"{label}: ran (it threw {ex.GetType().Name}: {ex.Message})", false); }
        }

        string stopping = L.T("core.play.stoppingFiddler"), saving = L.T("core.play.savingProfile"), restoredLine = L.T("core.play.liveRestored");

        await Task.WhenAll(
            Case("session end, nothing runs", async () =>
            {
                var c = standIns.Case("A");
                var rec = new Recorder();
                long t0 = Environment.TickCount64;
                var n = await SessionEnd.FinishAsync(rec.Steps(c.Name, c.OursDir), "2.8", fiddler: true, clientSeen: true, clientNeverAppeared: false, say: rec.Say);
                long stop = (rec.FiddlerAt ?? 0) - t0;
                check($"session end, nothing runs: the window lasts its 5 s — Fiddler first stopped only after it ({stop} ms on the window's clock)",
                    rec.FiddlerAt is not null && stop >= 5_000 && stop < 7_000);
                check("session end, nothing runs: Fiddler once, then the profile back, no taint — the acts in that order; the result 'restored' with Fiddler stopped, in its words",
                    rec.Acts.SequenceEqual(new[] { "fiddler", "restore" }) && n.Kind == SessionNotice.KindRestored
                    && n.FiddlerStopped && n.Restored && n.Appeared == true && n.VersionId == "2.8" && n.Text == L.T("core.play.endedRestored"));
                check("session end, nothing runs: the session's lines in order (stopping Fiddler, saving, restored) and relic.log's decision and Fiddler lines",
                    rec.Said.SequenceEqual(new[] { stopping, saving, restoredLine })
                    && rec.Diag.Contains("post-exit: ours=False foreign=False unknown=False -> stopFiddler=True restore=True (no client running)")
                    && rec.Diag.Contains("stopping Fiddler (own pid 4242)"));
            }),
            Case("session end, the client was never seen", async () =>
            {
                var c = standIns.Case("B");
                var rec = new Recorder();
                long t0 = Environment.TickCount64;
                var n = await SessionEnd.FinishAsync(rec.Steps(c.Name, c.OursDir), "2.8", fiddler: true, clientSeen: false, clientNeverAppeared: true, say: rec.Say);
                long stop = (rec.FiddlerAt ?? 0) - t0;
                check($"session end, the client never seen: the window lasts 15 s — Fiddler first stopped only after it ({stop} ms)",
                    rec.FiddlerAt is not null && stop >= 15_000 && stop < 17_000 && rec.Acts.SequenceEqual(new[] { "fiddler", "restore" }));
                check("session end, the client never seen: the result says first that the game was not detected",
                    n.Kind == SessionNotice.KindRestored && n.Appeared == false
                    && n.Text == L.T("core.play.gameNotDetected") + " " + L.T("core.play.endedRestored"));
            }),
            Case("session end, our client still runs", async () =>
            {
                var c = standIns.Case("C");
                var rec = new Recorder();
                var p = standIns.Start(c.OursExe, 60);
                bool seen = await StandIns.SeenAsync(c.Name);
                long t0 = Environment.TickCount64;
                var n = await SessionEnd.FinishAsync(rec.Steps(c.Name, c.OursDir), "2.8", fiddler: true, clientSeen: true, clientNeverAppeared: false, say: rec.Say);
                long took = Environment.TickCount64 - t0;
                check($"session end, our client still runs: the window ends at its first look ({took} ms); no act — Fiddler stays, the restore waits",
                    seen && took < 1_500 && rec.Acts.Count == 0
                    && n.Kind == SessionNotice.KindDeferredOwn && !n.FiddlerStopped && !n.Restored
                    && rec.Said.SequenceEqual(new[] { L.T("core.play.deferredOwn") }) && n.Text == L.T("core.play.endedDeferredOwn"));
                check("session end, our client still runs: relic.log names the client that blocks the restore",
                    rec.Diag.Any(d => d.StartsWith($"profile restore deferred: {c.Name}.exe pid={p.Id} path='", StringComparison.Ordinal)));
                StandIns.Stop(p);
            }),
            Case("session end, our client comes back inside the window", async () =>
            {
                var c = standIns.Case("D");
                var rec = new Recorder();
                long t0 = Environment.TickCount64;
                var respawn = Task.Run(async () => { await Task.Delay(1_200); return standIns.Start(c.OursExe, 60); });
                var n = await SessionEnd.FinishAsync(rec.Steps(c.Name, c.OursDir), "2.8", fiddler: true, clientSeen: true, clientNeverAppeared: false, say: rec.Say);
                long took = Environment.TickCount64 - t0;
                check($"session end, our client respawns 1.2 s into the window: seen ({took} ms), no act — Fiddler stays, the restore waits",
                    took is >= 1_000 and < 4_000 && rec.Acts.Count == 0 && n.Kind == SessionNotice.KindDeferredOwn);
                StandIns.Stop(await respawn);
            }),
            Case("session end, a foreign client runs", async () =>
            {
                var c = standIns.Case("E");
                var rec = new Recorder();
                var p = standIns.Start(c.ForeignExe, 60);
                bool seen = await StandIns.SeenAsync(c.Name);
                long t0 = Environment.TickCount64;
                var n = await SessionEnd.FinishAsync(rec.Steps(c.Name, c.OursDir), "2.8", fiddler: true, clientSeen: true, clientNeverAppeared: false, say: rec.Say);
                long stop = (rec.FiddlerAt ?? 0) - t0;
                check($"session end, a FOREIGN client runs: Fiddler first stopped at the END of the window ({stop} ms), never before",
                    seen && rec.FiddlerAt is not null && stop >= 5_000 && stop < 7_000);
                check("session end, a FOREIGN client runs: Fiddler once, then the slot tainted, no restore — in that order; 'deferredForeign' with Fiddler stopped",
                    rec.Acts.SequenceEqual(new[] { "fiddler", "taint" }) && n.Kind == SessionNotice.KindDeferredForeign
                    && n.FiddlerStopped && !n.Restored && n.Text == L.T("core.play.endedDeferredForeign")
                    && rec.Said.SequenceEqual(new[] { stopping, L.T("core.play.deferredForeign") })
                    && rec.Diag.Contains("post-exit: ours=False foreign=True unknown=False -> stopFiddler=True restore=False (foreign client running)"));
                StandIns.Stop(p);
            }),
            // On the real clock: tried once more when it fails (a loaded PC can start the stand-in late, Retried).
            Case("session end, a foreign client starts inside the window", () => Retried(check, "session end, a foreign client starts inside the window", async (rowCheck, attempt) =>
            {
                var c = standIns.Case(attempt == 1 ? "F" : "F2");
                var rec = new Recorder();
                long t0 = Environment.TickCount64;
                long foreignAt = 0;
                var late = Task.Run(async () =>
                {
                    await Task.Delay(2_000);
                    var started = standIns.Start(c.ForeignExe, 60);
                    foreignAt = Environment.TickCount64;
                    return started;
                });
                var n = await SessionEnd.FinishAsync(rec.Steps(c.Name, c.OursDir), "2.8", fiddler: true, clientSeen: true, clientNeverAppeared: false, say: rec.Say);
                var p = await late;
                long stop = (rec.FiddlerAt ?? 0) - t0;
                // Judged by the moment the stand-in really came up: inside the window, a second or more before its end —
                // Fiddler stopped at the window's end is then a second or more after it appeared, never at its appearance.
                rowCheck($"session end, a FOREIGN client starts 2 s into the window: Fiddler first stopped at the window's end ({stop} ms), not when it appeared ({(rec.FiddlerAt ?? 0) - foreignAt} ms after it, {foreignAt - t0} ms into the window)",
                    rec.FiddlerAt is not null && stop >= 5_000 && stop < 7_000 && foreignAt > t0 && foreignAt - t0 <= 4_000 && rec.FiddlerAt - foreignAt >= 1_000);
                rowCheck("session end, a FOREIGN client starts inside the window: Fiddler, then the taint, no restore",
                    rec.Acts.SequenceEqual(new[] { "fiddler", "taint" }) && n.Kind == SessionNotice.KindDeferredForeign);
                StandIns.Stop(p);
            })),
            Case("session end, ours and a foreign client", async () =>
            {
                var c = standIns.Case("G");
                var rec = new Recorder();
                var ours = standIns.Start(c.OursExe, 60);
                var foreign = standIns.Start(c.ForeignExe, 60);
                bool seen = await StandIns.SeenAsync(c.Name);
                var n = await SessionEnd.FinishAsync(rec.Steps(c.Name, c.OursDir), "2.8", fiddler: true, clientSeen: true, clientNeverAppeared: false, say: rec.Say);
                check("session end, ours and a foreign client: ours wins — no act, Fiddler stays (the private client must not lose it)",
                    seen && rec.Acts.Count == 0 && n.Kind == SessionNotice.KindDeferredOwn);
                StandIns.Stop(ours);
                StandIns.Stop(foreign);
            }),
            Case("session end without Fiddler", async () =>
            {
                var c = standIns.Case("H");
                var rec = new Recorder();
                var n = await SessionEnd.FinishAsync(rec.Steps(c.Name, c.OursDir), "2.8", fiddler: false, clientSeen: true, clientNeverAppeared: false, say: rec.Say);
                check("session end of a session that runs no Fiddler: Fiddler is left alone, the profile comes back, the words say only that",
                    rec.Acts.SequenceEqual(new[] { "restore" }) && n.Kind == SessionNotice.KindRestored && !n.FiddlerStopped
                    && n.Text == L.T("core.play.endedRestoredNoFiddler"));
            }));
    }

    /// <summary>The order PlaySession follows (its source is held by <see cref="SessionSource"/>) — the wait for the
    /// client's exit (the same watcher), the closing notice the moment it returns, then the post-exit step — timed
    /// against the moment the stand-in really exited (a killed process can take most of a second to finish terminating,
    /// and is a running client until it has), on the poll path (no handle: every launch through launcher.exe) at three
    /// moments between the watcher's looks — one right after a look, the slowest case —, and on the handle path (a
    /// direct start).</summary>
    private static async Task ExitNotices(Action<string, bool> check, StandIns standIns)
    {
        // The watcher's step on the poll path (GameProcessWatcher's drain): the notice comes within it and one scan.
        const int watcherStepMs = 1_000;
        foreach (var (handlePath, playMs, tag) in new[] { (false, 1_300, "K"), (false, 1_800, "L"), (false, 1_015, "M"), (true, 1_300, "J") })
        {
            string path = handlePath ? "handle path" : $"poll path, exit {playMs} ms after the appearance";
            var c = standIns.Case(tag);
            var rec = new Recorder();
            var p = standIns.Start(c.OursExe, 60);
            var sw = Stopwatch.StartNew();
            long appearedAt = -1, closingAt = -1, closingTick = 0, endedTick = 0;
            SessionNotice? closing = null, ended = null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var flow = Task.Run(async () =>
            {
                bool appeared = await GameProcessWatcher.WaitForAppearThenExitAsync(c.Name, c.OursExe, TimeSpan.FromSeconds(15), cts.Token,
                    started: handlePath ? p : null, preExisting: Array.Empty<int>(),
                    onAppeared: () => Interlocked.Exchange(ref appearedAt, sw.ElapsedMilliseconds));
                if (appeared)
                {
                    closing = SessionEnd.Closing("2.8", fiddler: true);
                    closingAt = sw.ElapsedMilliseconds;
                    closingTick = Environment.TickCount64;
                }
                ended = await SessionEnd.FinishAsync(rec.Steps(c.Name, c.OursDir), "2.8", fiddler: true,
                    clientSeen: appeared, clientNeverAppeared: !appeared, say: rec.Say);
                endedTick = Environment.TickCount64;
            });

            long until = Environment.TickCount64 + 5_000;
            while (Interlocked.Read(ref appearedAt) < 0 && Environment.TickCount64 < until) await Task.Delay(5);
            long sinceAppear = sw.ElapsedMilliseconds - Interlocked.Read(ref appearedAt);
            if (playMs > sinceAppear) await Task.Delay((int)(playMs - sinceAppear)); // a moment into the game
            // The exit is the moment the process has really ended (its handle signalled), not the kill request.
            var exited = Task.Run(() => { p.WaitForExit(); return sw.ElapsedMilliseconds; });
            StandIns.Stop(p);
            long exitAt = await exited;
            bool finished = await Task.WhenAny(flow, Task.Delay(TimeSpan.FromSeconds(40))) == flow;
            long latency = closingAt - exitAt;
            long allowed = handlePath ? 1_000 : watcherStepMs + 250;
            check($"exit ({path}): the closing notice {latency} ms after the exit — the moment the exit is noticed ({(handlePath ? "within a second" : "within the watcher's 1 s step and one scan")})",
                finished && appearedAt >= 0 && closing is not null && latency >= 0 && latency <= allowed);
            check($"exit ({path}): then the result — after the whole window ({endedTick - closingTick} ms later), Fiddler first stopped after it, then the profile back",
                finished && closing?.Kind == SessionNotice.KindClosing && ended?.Kind == SessionNotice.KindRestored
                && endedTick - closingTick >= 5_000 && rec.FiddlerAt - closingTick >= 5_000 && rec.Acts.SequenceEqual(new[] { "fiddler", "restore" }));
        }
    }

    // ── texts ──

    private static void Texts(Action<string, bool> check)
    {
        string[] keys =
        {
            "core.play.closing", "core.play.closingNoFiddler", "core.play.endedRestored", "core.play.endedRestoredNoFiddler",
            "core.play.endedDeferredForeign", "core.play.endedDeferredOwn",
            "core.play.recovered", "core.play.recoveredProfile", "core.play.recoveredDiscarded", "core.play.tornRepaired",
            "core.play.stoppingFiddler", "core.play.savingProfile", "core.play.liveRestored", "core.play.deferredForeign",
            "core.play.deferredOwn", "core.play.gameNotDetected",
        };
        var fragments = ReadFragments(out string? repo);
        var unknown = keys.Where(k => !L.Has(k) && !fragments.ContainsKey(k)).ToList();
        check($"texts: every key of the notices is in en.json, or in a fragment not merged yet{(unknown.Count > 0 ? $" (unknown: {string.Join(", ", unknown)}{(repo is null ? "; the repository was not found — RELIC_REPO_ROOT names it" : "")})" : "")}",
            unknown.Count == 0);
        var differ = fragments.Where(kv => L.Has(kv.Key) && L.T(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
        check($"texts: a fragment key en.json already holds has the same text there (the merge rule){(differ.Count > 0 ? ": " + string.Join(", ", differ) : "")}",
            differ.Count == 0);
    }

    /// <summary>Every key of ui/lang/fragments/*.en.json, when the repository is found (RELIC_REPO_ROOT, else a folder
    /// holding Relic.slnx above this build or above the working folder).</summary>
    private static Dictionary<string, string> ReadFragments(out string? repo)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        repo = FindRepo();
        if (repo is null) return keys;
        string dir = Path.Combine(repo, "app", "Relic.App", "ui", "lang", "fragments");
        if (!Directory.Exists(dir)) return keys;
        foreach (string file in Directory.GetFiles(dir, "*.en.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String) keys[prop.Name] = prop.Value.GetString() ?? "";
        }
        return keys;
    }

    private static string? FindRepo()
    {
        string? env = Environment.GetEnvironmentVariable("RELIC_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, "Relic.slnx"))) return env;
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Relic.slnx"))) return dir.FullName;
        }
        return null;
    }
}
