using System.Text.Json;
using Relic.Core.Fiddler;
using Relic.Core.Install;
using Relic.Core.Isolation;
using Relic.Core.Server;
using Relic.Core.Setup;
using Relic.Core.State;
using Relic.Core.Util;

namespace Relic.App;

/// <summary>
/// Quick Start's backend — the start screen's guided setup, whose run is the page's (<c>ui/quickstart.js</c> drives the
/// existing rpcs): the plan file and the RunOnce value that brings a run back after a restart, the look at this PC and
/// what a run needs of it, the installs a run adds to this PC, the restart of Windows that finishes turning WSL 2 on, the
/// public-address and reach checks, and the look at a Linux box over SSH with its host key. Nothing here writes the
/// server host, a token, the game install, an agent or a server job — the existing rpcs do (I57); of the state it writes
/// only the SSH host key the user confirmed (I58). None of these rpcs sits behind the admin gate: they act on this PC,
/// probe an address, or use SSH details typed a moment ago, like the agent installs. No password, passphrase, sudo
/// password or token of a payload reaches a log line, an event, an answer or the plan file.
/// </summary>
public sealed partial class Backend
{
    // ── the plan file and its resume value ──

    /// <summary>The installs <c>quickstart.install</c> runs, by the word the page sends.</summary>
    private static readonly string[] QuickStartInstalls = { "python", "sevenZip", "wsl", "wslUpdate", "docker", "dockerStart", "wslMemory" };

    /// <summary>The codes a <c>quickstart.error</c> carries (<see cref="SetupInstallException.Code"/>).</summary>
    private static readonly string[] QuickStartErrorCodes = { "uacDeclined", "badHash", "badSignature", "download", "failed", "toolMissing" };

    /// <summary>One hand at a time on the plan file and the resume value: a save and the resume value that goes with it
    /// happen together, and the restart arms the value once more under it. Under it only the plan's own lock
    /// (<see cref="_qsPlanLock"/>), the store's file lock and the log's are taken; it is never taken under the state's lock
    /// — nor the state's under it. A reader of the plan never waits for it (the resume value is written by reg.exe).</summary>
    private readonly object _qsLock = new();

    /// <summary>The plan kept here (<see cref="_qsPlan"/>, <see cref="_qsPlanRead"/>): its first read from the file and every
    /// replacement. Held for a moment only — never across a write of the file or of the resume value —, taken under
    /// <see cref="_qsLock"/>, never the other way round.</summary>
    private readonly object _qsPlanLock = new();

    /// <summary>The run's plan as quickstart.json holds it (null: none, one that is done, or one that cannot be used), read
    /// from the file the first time it is asked for, then kept by <see cref="QuickStartSave"/> and
    /// <see cref="QuickStartClear"/> — the file's one writer. Replaced as a whole, never changed in place; written under
    /// <see cref="_qsPlanLock"/>, read without a lock once <see cref="_qsPlanRead"/> is true.</summary>
    private volatile QuickStartPlan? _qsPlan;
    private volatile bool _qsPlanRead;

    /// <summary>What this process last set the resume value to (true: armed); null before it set it. Windows deletes the
    /// value when it runs it, at a sign-in — which may come after this process armed it (the autostart's start and the
    /// resume's come in either order) —, so an unchanged wish writes nothing and the restart arms it once more. Under
    /// <see cref="_qsLock"/>.</summary>
    private bool? _qsResumeSet;

    /// <summary>1 once the launcher's start has looked whether a run waits to be resumed (<see cref="QuickStartResumeOnBoot"/>).</summary>
    private int _qsBootLooked;

    /// <summary>The look at this PC, one at a time (<see cref="QuickStartCheckAsync"/>: its process probes run side by side
    /// within one look, never as several looks' worth); Python's and 7-Zip's installs take its turn too — they replace what
    /// a look runs (<c>tools\python</c>, <c>tools\7zr.exe</c>). Awaited off the message thread only, never under a lock.</summary>
    private readonly SemaphoreSlim _qsCheckGate = new(1, 1);

    /// <summary>Which install <see cref="_setupBusy"/> holds (<c>quickstart.install</c>'s word) — the last one that ran once
    /// the flag is free; read together with the flag. Set on the message thread.</summary>
    private volatile string? _setupWhat;

    /// <summary>The plan as the init state carries it (<c>quickstart: {plan}</c>): the stored plan, or null — also when it
    /// cannot be read, which the init never fails for.</summary>
    private QuickStartPlan? QuickStartPlanForInit()
    {
        try { return QuickStartPlanNow(); }
        catch (Exception ex)
        {
            Log.Error("quick start: the plan could not be read for the init state (non-fatal)", ex);
            return null;
        }
    }

    /// <summary>The stored plan (see <see cref="_qsPlan"/>), read from the file the first time
    /// (<see cref="QuickStartStore.Load"/>, which logs why a file cannot be used). Never waits for a save or the resume
    /// value: the init state asks for it on any thread.</summary>
    private QuickStartPlan? QuickStartPlanNow()
    {
        if (_qsPlanRead) return _qsPlan;
        lock (_qsPlanLock)
        {
            if (!_qsPlanRead)
            {
                _qsPlan = QuickStartStore.Load(gameIds: QuickStartGameIds());
                _qsPlanRead = true;
            }
            return _qsPlan;
        }
    }

    /// <summary>The plan kept here from now on — a save's or a clear's, after the file was written. Under
    /// <see cref="_qsLock"/>.</summary>
    private void QuickStartPlanKept(QuickStartPlan? plan)
    {
        lock (_qsPlanLock)
        {
            _qsPlan = plan;
            _qsPlanRead = true;
        }
    }

    /// <summary>The catalogue's game versions, which a plan's game versions must be among; null when the catalogue cannot
    /// be read (the store then reads it itself, and logs why).</summary>
    private static IReadOnlyCollection<string>? QuickStartGameIds()
    {
        try { return VersionCatalog.Load().Select(v => v.Id).ToList(); }
        catch (Exception ex)
        {
            Log.Error("quick start: the version catalogue could not be read for the plan's check", ex);
            return null;
        }
    }

    /// <summary>The refusal of a plan that cannot be used — <paramref name="detail"/> is the JSON path of its first problem
    /// (<c>$</c>: the document as a whole).</summary>
    private static InvalidOperationException QuickStartBadPlan(string detail) => new(L.T("backend.qs.badPlan", new { detail }));

    /// <summary>
    /// Arms (<paramref name="wanted"/>: a run that is not ended — it runs, or waits for the restart that finishes WSL 2) or
    /// disarms the resume value, <c>RunOnce\RelicQuickStart = "&lt;Relic.exe&gt;" --quickstart</c>
    /// (<see cref="Startup.SetQuickStartResume"/>), when that changes what this process last set. Under
    /// <see cref="_qsLock"/>. An arm that fails throws <c>backend.qs.resumeFailed</c>: a run that would not come back after a
    /// restart must not look saved.
    /// </summary>
    private void QuickStartResume(bool wanted)
    {
        if (_qsResumeSet == wanted) return;
        try { Startup.SetQuickStartResume(wanted); }
        catch (Exception ex)
        {
            _qsResumeSet = null;
            Log.Error("quick start: the resume value could not be set", ex);
            throw new InvalidOperationException(L.T("backend.qs.resumeFailed", new { error = ex.Message }));
        }
        _qsResumeSet = wanted;
        Log.Info($"quick start: resume after a restart {(wanted ? "armed" : "disarmed")}");
    }

    /// <summary>
    /// The launcher's start, once: a run that is not ended arms its resume value again — Windows deleted it when it ran it at
    /// the sign-in that brought this launcher back. Off the message thread; the plan is read under <see cref="_qsLock"/> right
    /// before, so a run discarded meanwhile is not armed again. Never throws.
    /// </summary>
    private void QuickStartResumeOnBoot()
    {
        if (Interlocked.Exchange(ref _qsBootLooked, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                lock (_qsLock)
                {
                    if (QuickStartStore.ResumeWanted(QuickStartPlanNow())) QuickStartResume(true);
                }
            }
            catch (Exception ex) { Log.Error("quick start: arming the resume value at the start (non-fatal)", ex); }
        });
    }

    /// <summary><c>quickstart.get {}</c> → <c>{plan, installing}</c>: the stored plan, null when there is none, and the
    /// install of <c>quickstart.install</c> that runs now (its word), null when none — a run stopped and continued learns
    /// what Stop left running.</summary>
    private object QuickStartGet() => new { plan = QuickStartPlanNow(), installing = QuickStartInstalling() };

    /// <summary>The install of <c>quickstart.install</c> that runs now (its word), or null.</summary>
    private string? QuickStartInstalling() => Volatile.Read(ref _setupBusy) != 0 ? _setupWhat : null;

    /// <summary>
    /// <c>quickstart.save {plan}</c> → <c>{plan}</c>: the plan the page sent, read into <see cref="QuickStartPlan"/> — every key
    /// the model does not name is dropped, a secret among them —, normalised, checked and written
    /// (<see cref="QuickStartStore.Save"/>: a plan that is done deletes the file instead), then the resume value armed while
    /// the run is not ended and disarmed once it is paused or done. A plan that cannot be used is <c>backend.qs.badPlan</c>
    /// and nothing is written; a file that cannot be written keeps the one before it. Answers the plan as stored.
    /// </summary>
    private object QuickStartSave(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("plan", out var raw) || raw.ValueKind != JsonValueKind.Object)
            throw QuickStartBadPlan("$");
        QuickStartPlan plan;
        try { plan = QuickStartPlan.FromJson(raw.GetRawText()); }
        catch (QuickStartPlanException ex) { throw QuickStartBadPlan(ex.Detail); }
        var gameIds = QuickStartGameIds();
        lock (_qsLock)
        {
            QuickStartPlan stored;
            try { stored = QuickStartStore.Save(plan, gameIds: gameIds); }
            catch (QuickStartPlanException ex) { throw QuickStartBadPlan(ex.Detail); }
            QuickStartPlanKept(stored.State == QuickStartPlan.States.Done ? null : stored);
            QuickStartResume(QuickStartStore.ResumeWanted(stored));
            Log.Info($"quick start: plan {stored.Id} stored ({stored.State})");
            return new { plan = stored };
        }
    }

    /// <summary><c>quickstart.clear {}</c> → <c>{plan: null}</c>: the plan file deleted and the resume value disarmed (a run
    /// discarded, or ended once the page took the user where it leads).</summary>
    private object QuickStartClear()
    {
        lock (_qsLock)
        {
            QuickStartStore.Clear();
            QuickStartPlanKept(null);
            QuickStartResume(false);
        }
        Log.Info("quick start: the plan was cleared");
        return new { plan = (object?)null };
    }

    // ── the look at this PC, and what a run needs ──

    /// <summary>
    /// <c>quickstart.check {plan, box?}</c> → <c>{machine, needs, tools}</c>: the look at this PC (<see cref="MachineCheck"/> —
    /// Windows, virtualization, WSL, Docker Desktop, Python, the extractor, the agent on this PC, the memory and the drives;
    /// the server folders only for a server on this PC; the starting progress each version's shipped manifest offers), what
    /// the run needs of the drives and the memory and what keeps a server off this PC (<see cref="SetupNeeds.For"/>, with
    /// the Linux box's facts the page got from <c>ssh.probe</c> when it sends them), and the tools this build can add (their
    /// pinned versions and sizes, Docker's licence address, the address services by name). Off the message thread, one look
    /// at a time — a look asked while another runs, or while Python's or 7-Zip's install runs, waits for it; never throws —
    /// what could not be learned is null.
    /// </summary>
    private async Task<object> QuickStartCheckAsync(JsonElement p)
    {
        // Read off the payload before anything is awaited.
        var plan = QuickStartPlanLoosely(p);
        BoxFacts? box = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("box", out var b) ? QuickStartBoxFromPage(b) : null;
        string settingsRoot = _state.Settings.InstallRoot;
        return await Task.Run(async () =>
        {
            // One look at a time — a look asked while one runs waits for it —, and none while Python's or 7-Zip's install
            // replaces what it runs (_qsCheckGate).
            await _qsCheckGate.WaitAsync();
            try
            {
                var tools = SetupTools.Load();
                string root = plan.Game.InstallRoot.Length > 0 ? plan.Game.InstallRoot : settingsRoot;
                MachineFacts? machine = null;
                try
                {
                    // Only a server on this PC has folders on this PC: a Linux box's are its own (ssh.probe looks at them).
                    var folders = plan.Server.Kind == QuickStartPlan.Kinds.Windows
                        ? plan.Server.Versions.Where(v => v is not null && v.Dir.Length > 0 && ServerVersions.All.Contains(v.Id))
                            .Select(v => new ServerFolder(v.Id, v.Dir)).ToList()
                        : new List<ServerFolder>();
                    machine = await MachineCheck.RunAsync(new MachineCheckRequest
                    {
                        GamesRoot = root,
                        ServerFolders = folders,
                        TemplateVersions = ServerVersions.All,
                        Tools = tools,
                    });
                }
                catch (Exception ex) { Log.Error("quick start: the look at this PC failed (non-fatal)", ex); }
                NeedsResult? needs = null;
                try { needs = SetupNeeds.For(QuickStartNeedsRequest(plan, root), machine, box, ServerNeeds.Load(), tools); }
                catch (Exception ex) { Log.Error("quick start: what the run needs could not be worked out (non-fatal)", ex); }
                return (object)new { machine, needs, tools = QuickStartToolsForPage(tools) };
            }
            finally { _qsCheckGate.Release(); }
        });
    }

    /// <summary>The <c>plan</c> of a payload as far as it reads — an empty one when it does not: a check refuses nothing.</summary>
    private static QuickStartPlan QuickStartPlanLoosely(JsonElement p)
    {
        try
        {
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("plan", out var raw) && raw.ValueKind == JsonValueKind.Object)
                return QuickStartPlan.FromJson(raw.GetRawText()).Normalize();
        }
        catch (Exception ex) { Log.Info($"quick start: the check got a plan it cannot read ({ex.GetType().Name}) — it looks at this PC alone"); }
        return new QuickStartPlan().Normalize();
    }

    /// <summary>What a run asks of the disks and the memory, from its plan: the game versions it downloads (the client and
    /// the voice packs it installs — the default one when none is named —, into <paramref name="root"/>), every game version
    /// it plays on this PC, and its server versions with their packages as this build's catalogue describes them.</summary>
    private static NeedsRequest QuickStartNeedsRequest(QuickStartPlan plan, string root)
    {
        var catalog = VersionCatalog.Load();
        var games = new List<GameDownloadNeed>();
        foreach (var v in plan.Game.Versions)
        {
            if (v is null || v.From != QuickStartPlan.GameSources.Download) continue;
            if (catalog.FirstOrDefault(c => string.Equals(c.Id, v.Id, StringComparison.Ordinal)) is not { } info) continue;
            var voices = v.Voices.Count > 0 ? v.Voices : new List<string> { VoiceLanguages.Default };
            games.Add(GameDownloadNeed.Of(info, voices, root));
        }
        return new NeedsRequest
        {
            Kind = plan.Server.Kind,
            Games = games,
            ClientVersions = plan.Game.Versions.Where(v => v is not null && v.Id.Length > 0).Select(v => v.Id).Distinct(StringComparer.Ordinal).ToList(),
            Servers = plan.Server.Versions
                .Where(v => v is not null && ServerVersions.All.Contains(v.Id))
                .Select(v => new ServerVersionNeed(v.Id, v.Choice, v.Dir, ServerVersions.Package(v.Id), v.Pathfinding))
                .ToList(),
        };
    }

    /// <summary>What the summary says of the tools this build can add: Python's and 7-Zip's pinned version and size, Docker
    /// Desktop's size and licence address, each with whether this build can add it at all (<c>usable</c>), and the address
    /// services the public address is asked of, by name.</summary>
    private static object QuickStartToolsForPage(SetupToolsCatalog tools) => new
    {
        python = tools.Python is { } py ? new { version = py.Version, size = py.Size, usable = py.Usable } : null,
        sevenZip = tools.SevenZip is { } sz ? new { version = sz.Version, size = sz.Size, usable = sz.Usable } : null,
        docker = tools.Docker is { } dk ? new { size = dk.Size, licenceUrl = dk.LicenceUrl, usable = dk.Usable } : null,
        publicIpServices = tools.PublicIpServices.Select(PublicAddress.ServiceName).ToList(),
    };

    /// <summary>The Linux box's facts the page sends back (the <c>facts</c> of <c>ssh.probe</c>'s phase box) as far as a check
    /// counts with them: the box's memory and, per folder, the free space of its filesystem and where it is mounted. Null
    /// for anything that is not such an object.</summary>
    private static BoxFacts? QuickStartBoxFromPage(JsonElement box)
    {
        if (box.ValueKind != JsonValueKind.Object) return null;
        long? memTotal = box.TryGetProperty("memTotal", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt64(out long mem) && mem > 0 ? mem : null;
        var free = new Dictionary<string, BoxFree>(StringComparer.Ordinal);
        if (box.TryGetProperty("free", out var f) && f.ValueKind == JsonValueKind.Object)
            foreach (var dir in f.EnumerateObject())
            {
                if (dir.Value.ValueKind != JsonValueKind.Object) continue;
                long? avail = dir.Value.TryGetProperty("avail", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out long av) && av >= 0 ? av : null;
                string mount = dir.Value.TryGetProperty("mount", out var mt) && mt.ValueKind == JsonValueKind.String ? mt.GetString() ?? "" : "";
                free[dir.Name] = new BoxFree(avail, mount);
            }
        return new BoxFacts { MemTotal = memTotal, Free = free };
    }

    // ── the installs on this PC ──

    /// <summary>
    /// <c>quickstart.install {what, acceptLicence?, memoryGb?}</c> → <c>{started: true, what}</c>: one of Quick Start's installs
    /// on this PC (<see cref="SetupInstall"/>), one at a time (<see cref="_setupBusy"/>, else <c>backend.qs.busy</c>) —
    /// <c>python</c> (Python's embeddable package into Relic's tools folder), <c>sevenZip</c> (7-Zip's 7zr.exe there),
    /// <c>wsl</c> / <c>wslUpdate</c> (<c>wsl.exe --install --no-distribution</c> / <c>--update</c> through the Windows
    /// permission prompt — the one elevation Quick Start adds, S7: only in a run whose stored plan names it, which the summary
    /// showed with the prompt), <c>docker</c> (Docker Desktop for this user, its licence accepted on its command line only
    /// with <c>acceptLicence: true</c>), <c>dockerStart</c>, <c>wslMemory</c> (WSL's memory cap in <c>.wslconfig</c>,
    /// <c>memoryGb</c>) — Docker Desktop's install and WSL's memory cap, like WSL's prompt, only in a run whose stored plan
    /// names them as its summary showed them (the licence accepted; that very cap). Docker's two refuse while Fiddler runs
    /// (S8: Fiddler is the system proxy meanwhile — the installer's signature is checked online through Windows' own proxy
    /// settings, and Docker Desktop may take the system proxy as its own when it starts); an installer whose check failed
    /// while Fiddler ran — the game install starts it in its Fiddler step — is fetched and checked once more after Fiddler
    /// has closed. Streams <c>quickstart.log {what, text}</c>, then exactly one of <c>quickstart.done {what, result}</c> /
    /// <c>quickstart.error {what, message, code}</c> — posted once the busy flag is free again. An ask for the very install
    /// that runs (a run stopped while it ran, then continued) joins it: <c>{started: true, what, joined: true}</c>, and that
    /// install's one outcome follows the answer.
    /// </summary>
    private object QuickStartInstall(JsonElement p)
    {
        string asked = Str(p, "what").Trim();
        string what = QuickStartInstalls.FirstOrDefault(w => string.Equals(w, asked, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(L.T("backend.qs.unknownInstall", new { what = asked.Length > 40 ? asked[..40] : asked }));
        // Read and set on the message thread, where the reply of this rpc is posted before any event a worker posts after
        // it: the joined install's outcome cannot overtake this answer.
        if (Volatile.Read(ref _setupBusy) != 0 && _setupWhat == what) return new { started = true, what, joined = true };
        bool acceptLicence = Bool(p, "acceptLicence");
        int memoryGb = Int(p, "memoryGb", 0);
        if (what is "wsl" or "wslUpdate") QuickStartRequireNamed(what);
        if (what is "docker" or "wslMemory") QuickStartRequireChosen(what, memoryGb);
        if (what is "docker" or "dockerStart") FiddlerGuard();
        if (Interlocked.Exchange(ref _setupBusy, 1) != 0) throw new InvalidOperationException(L.T("backend.qs.busy"));
        _setupWhat = what;
        _ = Task.Run(async () =>
        {
            void log(string s) => PostEvent("quickstart.log", new { what, text = s });
            object? done = null, failed = null;
            // Python's and 7-Zip's installs replace what a look at this PC runs: they take the look's turn (_qsCheckGate).
            bool takesLook = what is "python" or "sevenZip";
            try
            {
                if (takesLook) await _qsCheckGate.WaitAsync();
                try
                {
                    Log.Info($"quick start install: {what} started");
                    var tools = SetupTools.Load();
                    object result;
                    for (int attempt = 1; ; attempt++)
                    {
                        try
                        {
                            switch (what)
                            {
                                case "python": result = await SetupInstall.PythonAsync(tools.Python, log); break;
                                case "sevenZip": result = await SetupInstall.SevenZipAsync(tools.SevenZip, log); break;
                                case "wsl": result = await SetupInstall.EnableWslAsync(log); break;
                                case "wslUpdate": result = await SetupInstall.UpdateWslAsync(log); break;
                                case "docker": result = await SetupInstall.DockerAsync(tools.Docker, acceptLicence, log); break;
                                case "dockerStart": result = await SetupInstall.DockerStartAsync(log); break;
                                default: result = SetupInstall.WslMemory(memoryGb, log); break;
                            }
                            break;
                        }
                        // The installer failed its signature check while Fiddler ran (the game install's Fiddler step may have
                        // started it after this install was asked for): Windows asks about a revoked signer through the system
                        // proxy, which Fiddler is meanwhile. The file is gone and nothing ran — it is fetched and checked once
                        // more after Fiddler has closed.
                        catch (SetupInstallException ex) when (attempt == 1 && what == "docker" && ex.Code == "badSignature" && FiddlerAutomation.IsRunning)
                        {
                            await QuickStartAfterFiddlerAsync(ex, log);
                        }
                    }
                    Log.Info($"quick start install: {what} done");
                    done = new { what, result };
                }
                finally { if (takesLook) _qsCheckGate.Release(); }
            }
            catch (SetupInstallException ex)
            {
                Log.Error($"quick start install failed: {what} ({ex.Code})", ex);
                failed = new { what, message = ex.Message, code = QuickStartErrorCodes.Contains(ex.Code) ? ex.Code : "failed" };
            }
            catch (Exception ex)
            {
                Log.Error($"quick start install failed: {what}", ex);
                failed = new { what, message = ex.Message, code = "failed" };
            }
            finally { Volatile.Write(ref _setupBusy, 0); }
            // Only now: the page may start the next install as soon as it hears this.
            if (done is not null) PostEvent("quickstart.done", done);
            else if (failed is not null) PostEvent("quickstart.error", failed);
        });
        return new { started = true, what };
    }

    /// <summary>S7: WSL's permission prompt (<paramref name="what"/> = wsl / wslUpdate) only in a run that is not ended whose
    /// stored plan sets a server up on this PC and names that very install — the summary the user started the run from
    /// named the prompt and the restart that follows. Else <c>backend.qs.notInPlan</c>, before anything runs.</summary>
    private void QuickStartRequireNamed(string what)
    {
        var plan = QuickStartPlanNow();
        bool named = plan is not null && QuickStartStore.ResumeWanted(plan)
            && plan.Server.Kind == QuickStartPlan.Kinds.Windows
            && (what == "wsl" ? plan.Server.Installs.Wsl : plan.Server.Installs.WslUpdate);
        if (!named) throw new InvalidOperationException(L.T("backend.qs.notInPlan"));
    }

    /// <summary>Docker Desktop's install (<paramref name="what"/> = docker: its licence accepted on its command line) and
    /// WSL's memory cap (wslMemory: every WSL Linux of this PC gets it) only in a run that is not ended whose stored plan sets
    /// a server up on this PC and names them as the summary showed them — Docker Desktop with its licence accepted, that very
    /// cap of <paramref name="memoryGb"/> GB. Else <c>backend.qs.notChosen</c>, before anything runs.</summary>
    private void QuickStartRequireChosen(string what, int memoryGb)
    {
        var plan = QuickStartPlanNow();
        bool chosen = plan is not null && QuickStartStore.ResumeWanted(plan)
            && plan.Server.Kind == QuickStartPlan.Kinds.Windows
            && (what == "docker"
                ? plan.Server.Installs.Docker && plan.Server.Installs.AcceptLicence
                : memoryGb > 0 && plan.Server.Installs.WslMemoryGb == memoryGb);
        if (!chosen) throw new InvalidOperationException(L.T("backend.qs.notChosen"));
    }

    /// <summary>S7: the game's firewall rules (<c>localagent.firewall {gamePorts: true}</c> — TCP 21000 and UDP 21081 for any
    /// program, beside the agent's own rule, in its one permission prompt) only in a run that is not ended whose stored plan
    /// sets up a server on this PC that other PCs join (reach lan or internet) and names the firewall step, as its summary
    /// showed it. Else <c>backend.qs.notInPlan</c>, before the prompt.</summary>
    private void QuickStartRequireFirewall()
    {
        var plan = QuickStartPlanNow();
        bool named = plan is not null && QuickStartStore.ResumeWanted(plan)
            && plan.Server.Kind == QuickStartPlan.Kinds.Windows
            && plan.Server.Reach is (QuickStartPlan.Reaches.Lan or QuickStartPlan.Reaches.Internet)
            && plan.Server.Firewall;
        if (!named) throw new InvalidOperationException(L.T("backend.qs.notInPlan"));
    }

    /// <summary>Waits — a look every 10 seconds, 30 minutes at most — for Fiddler to close before Docker Desktop's installer
    /// is fetched and checked once more (see <see cref="QuickStartInstall"/>), with a line that says so; a Fiddler still
    /// running then ends the install with <paramref name="failure"/>, the check's own failure.</summary>
    private static async Task QuickStartAfterFiddlerAsync(SetupInstallException failure, Action<string> log)
    {
        Log.Info("quick start install: docker — the installer failed its signature check while Fiddler ran; it is checked again once Fiddler has closed");
        log(L.T("backend.qs.dockerAfterFiddler"));
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(30);
        while (FiddlerAutomation.IsRunning)
        {
            if (DateTime.UtcNow >= until) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// <c>quickstart.restart {}</c> → <c>{restarting: true}</c>: Windows restarts, with <c>qs.restart.comment</c> as its reason
    /// (<see cref="SetupInstall.RestartWindows"/>, which throws <c>core.setup.restart.failed</c> when Windows refuses) —
    /// only once the page saved the run as waiting for the restart and its resume value is armed
    /// (<c>backend.qs.restartUnsaved</c> otherwise) — armed once more right before, as Windows may have run and deleted it
    /// since — and never while something of this launcher a restart would cut runs: one of Quick Start's installs
    /// (<c>backend.qs.busy</c>), a game install still stopping (<c>backend.install.busy</c>: the page pauses it first), a
    /// server job this launcher follows — a stack download, the install or update of the agent on this PC — or an account
    /// being made (<c>backend.server.jobBusy</c>), a game session or an official profile not back yet
    /// (<c>backend.qs.restartPlaying</c>), Fiddler — the system proxy while it runs; the certificate step runs it too
    /// (<c>backend.qs.restartFiddler</c>).
    /// </summary>
    private async Task<object> QuickStartRestartAsync()
    {
        if (Volatile.Read(ref _setupBusy) != 0) throw new InvalidOperationException(L.T("backend.qs.busy"));
        if (Volatile.Read(ref _installBusy) != 0) throw new InvalidOperationException(L.T("backend.install.busy"));
        if (Volatile.Read(ref _serverJobBusy) != 0 || Volatile.Read(ref _accountBusy) != 0)
            throw new InvalidOperationException(L.T("backend.server.jobBusy"));
        if (PlayInProgress || !QuickStartLiveProfileLoaded()) throw new InvalidOperationException(L.T("backend.qs.restartPlaying"));
        if (Volatile.Read(ref _certBusy) != 0 || FiddlerAutomation.IsRunning) throw new InvalidOperationException(L.T("backend.qs.restartFiddler"));
        bool saved;
        lock (_qsLock) saved = QuickStartPlanNow() is { State: QuickStartPlan.States.WaitingRestart } && _qsResumeSet == true;
        if (!saved) throw new InvalidOperationException(L.T("backend.qs.restartUnsaved"));
        string comment = L.T("qs.restart.comment");
        await Task.Run(() =>
        {
            // What this process last wrote proves nothing: Windows deletes the value when it runs it, at a sign-in that may
            // have come after this process armed it (the autostart's start and the resume's come in either order). So it is
            // armed once more — a run cleared or paused meanwhile restarts nothing.
            lock (_qsLock)
            {
                if (QuickStartPlanNow() is not { State: QuickStartPlan.States.WaitingRestart })
                    throw new InvalidOperationException(L.T("backend.qs.restartUnsaved"));
                _qsResumeSet = null;
                QuickStartResume(true);
            }
            Log.Info("quick start: Windows restarts to finish turning on WSL 2 — the run goes on at the next sign-in");
            SetupInstall.RestartWindows(comment);
        });
        return new { restarting = true };
    }

    /// <summary>Whether the official profile is the one in place (<see cref="ProfileStore.LiveIsActive"/>) — the exit
    /// guard's question; one that cannot be read counts as in place, as there.</summary>
    private static bool QuickStartLiveProfileLoaded()
    {
        try { return ProfileStore.ForGenshin().LiveIsActive(); }
        catch (Exception ex)
        {
            Log.Error("quick start: the profile's state could not be read before the restart (non-fatal)", ex);
            return true;
        }
    }

    // ── the public address and the reach check ──

    /// <summary><c>quickstart.publicIp {}</c> → <c>{address, answers: [{service, answer}], problem}</c>: this PC's public
    /// address, asked of the address services of <c>config/setup-tools.json</c> (<see cref="PublicAddress.LookupAsync"/>:
    /// two agreeing on an address a player outside could be handed; never through the system proxy; the log names the
    /// outcome alone) — for a server on this PC that players on the Internet join.</summary>
    private static async Task<object> QuickStartPublicIpAsync()
    {
        var services = (await Task.Run(() => SetupTools.Load())).PublicIpServices;
        return await PublicAddress.LookupAsync(services);
    }

    /// <summary><c>quickstart.reach {address, agentPort, inside}</c> → <c>{sdk: {reachable, detail}, agent: {reachable},
    /// verdict, inside, textKey}</c>: from this PC, TCP to the game's login port 21000 and to the agent's port at the
    /// server's address (<see cref="ReachCheck.RunAsync"/>; UDP 21081 is not tested, and the text the page shows for the
    /// verdict, <c>textKey</c>, says so). <c>inside</c> is the page's: this PC shares the server's network. An address that
    /// does not parse is <c>backend.badAddress</c>; an agent port outside 1–65535 is the default one.</summary>
    private static async Task<object> QuickStartReachAsync(JsonElement p)
    {
        string typed = Str(p, "address").Trim();
        int agentPort = ExplicitAgentPort(Int(p, "agentPort", 0)) ?? ServerAddress.DefaultAgentPort;
        bool inside = Bool(p, "inside");
        if (!ServerAddress.TryParse(typed, out var address, out var error) || address is null)
            throw new InvalidOperationException(L.T("backend.badAddress", new { detail = error ?? "" }));
        var r = await ReachCheck.RunAsync(address.Host, agentPort, inside);
        return new
        {
            sdk = new { reachable = r.SdkReachable, detail = r.SdkDetail },
            agent = new { reachable = r.AgentReachable },
            verdict = r.Verdict,
            inside = r.Inside,
            textKey = r.TextKey,
        };
    }

    // ── a Linux box over SSH ──

    /// <summary>
    /// <c>ssh.probe {host, port, user, password?, keyPath?, passphrase?, sudoPassword?, acceptKey?: {fingerprint, changed?},
    /// dirs, publicIp}</c> — Quick Start's look at a Linux box (<see cref="AgentDeployer.Probe"/>), off the message thread.
    /// <para>Phase <c>key</c> → <c>{phase: 'key', hostKey: {type, fingerprint, known, changed, pinned, verdict}}</c>: the key the
    /// box presents, read and never trusted — no authentication is attempted and no password leaves this PC — and how it
    /// stands against the keys pinned for its host and port (I58). The answer to a request without <c>acceptKey</c>, and to
    /// one whose key this session did not read at that address or the box no longer presents.</para>
    /// <para>Phase <c>box</c> → <c>{phase: 'box', hostKey, access, facts, publicIp}</c>: with <c>acceptKey</c> — the key this
    /// session read there and the user confirmed —, both connections trust that key alone; a key the pins call changed only
    /// with <c>changed: true</c> ("Trust the new key") and only when this session's read saw that very key as changed there
    /// (<see cref="HostKeyCheck.AcceptChanged"/>). Then how the user may act as root (<c>access</c>: root | sudo |
    /// sudoPassword | needsSudoPassword | sudoRefused | noSudo), the box's facts (<c>facts</c>: its system, memory, the
    /// folders of <c>dirs</c> and the free space of their filesystems, the folders its agent's configuration stores, its
    /// tools, its addresses) and, with <c>publicIp</c>, its public address learned on the box. That success pins the key as
    /// the address's (under the state's lock, saved).</para>
    /// Every key answered is noted as this session's read of the address (what the agent install's confirmed key is held
    /// to). The password, the passphrase and the sudo password go into the SSH target alone. A host with a port in it, an SSH
    /// port outside 1–65535 or a user name with a blank is <c>backend.qs.sshTarget</c>.
    /// </summary>
    private async Task<object> SshProbeAsync(JsonElement p)
    {
        // Read off the payload before anything is awaited.
        string typedHost = Str(p, "host").Trim(), user = Str(p, "user").Trim();
        int port = Int(p, "port", 22);
        if (user.Length == 0) user = "root";
        if (!ServerAddress.TryParse(typedHost, out var address, out _) || address is null || address.Port is not null
            || port is < 1 or > 65535 || user.Length > 255 || user.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new InvalidOperationException(L.T("backend.qs.sshTarget"));
        string host = address.Host;
        var ssh = new SshTarget(host, port, user, Str(p, "password"), Str(p, "keyPath").Trim(), Str(p, "passphrase"), Str(p, "sudoPassword"));
        string accept = "";
        bool trustChanged = false;
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("acceptKey", out var acceptKey) && acceptKey.ValueKind == JsonValueKind.Object)
        {
            accept = Str(acceptKey, "fingerprint").Trim();
            trustChanged = accept.Length > 0 && Bool(acceptKey, "changed");
        }
        var dirs = SshProbeDirs(Strings(p, "dirs"));
        bool publicIp = Bool(p, "publicIp");
        var pins = SshPinsOf(host, port);
        // The key the user confirms must be the one this session read at that address (and showed): any other is read anew.
        var read = SshKeyReadAt(host, port);
        bool confirmed = accept.Length > 0 && read is not null && string.Equals(read.Fingerprint, accept, StringComparison.Ordinal);
        bool acceptChanged = confirmed && trustChanged && read!.Changed;
        var services = confirmed && publicIp ? SetupTools.Load().PublicIpServices : null;
        var probe = await Task.Run(() => AgentDeployer.Probe(ssh, confirmed ? accept : null, pins, dirs, services,
            line => Log.Info("ssh probe: " + line), acceptChanged));
        NoteSshKeyRead(host, port, probe.HostKey);
        bool box = probe.Phase == "box";
        // The box's look succeeded on the key the user confirmed: that key is the address's from now on.
        if (box) PinSshHostKey(host, port, probe.HostKey.Type, probe.HostKey.Fingerprint);
        Log.Info($"ssh probe: {host}:{port} answered phase {probe.Phase}" + (probe.Access is { } access ? $" (access {access})" : ""));
        var hostKey = SshProbeHostKeyForPage(probe.HostKey);
        if (!box) return new { phase = "key", hostKey };
        return new
        {
            phase = "box",
            hostKey,
            access = probe.Access,
            facts = probe.Facts is { } facts ? SshProbeFactsForPage(facts) : null,
            publicIp = probe.PublicIp,
        };
    }

    /// <summary>The folders of a Linux box <c>ssh.probe</c> looks at: absolute paths of at most
    /// <see cref="QuickStartPlan.MaxPath"/> characters without a control character, each once, at most
    /// <see cref="QuickStartPlan.MaxVersions"/>; the script quotes every one.</summary>
    private static List<string> SshProbeDirs(IEnumerable<string> dirs) =>
        dirs.Select(d => (d ?? "").Trim())
            .Where(d => d.Length is > 0 and <= QuickStartPlan.MaxPath && d[0] == '/' && !d.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal)
            .Take(QuickStartPlan.MaxVersions)
            .ToList();

    /// <summary>A key a box presented, as the page reads it: its type and fingerprint, whether it is the pinned one
    /// (<c>known</c>) or another than the pinned ones (<c>changed</c>), the pins as a sentence part (<c>pinned</c>, "" for
    /// none) and the verdict's word (new | match | changed).</summary>
    private static object SshProbeHostKeyForPage(HostKeySeen seen) => new
    {
        type = seen.Type,
        fingerprint = seen.Fingerprint,
        known = seen.Known,
        changed = seen.Changed,
        pinned = seen.Pinned,
        verdict = seen.VerdictName,
    };

    /// <summary>A Linux box's facts as the page reads them: <c>osId</c> / <c>osVersion</c>, <c>memTotal</c> (bytes, null when
    /// not read), per folder asked whether it <c>exists</c>, its <c>kinds</c> word (absent | empty | stack | other | unknown)
    /// and the <c>free</c> space of its filesystem (<c>{avail, mount}</c> — what <c>quickstart.check</c> takes back as
    /// <c>box</c>), the folder the box's agent configuration stores per version (<c>storedDirs</c>, read only by a look as
    /// root), its tools (python3, docker compose, the 7z tool's name, curl, wget), whether an agent is installed, its own
    /// IPv4 addresses, the source address of its route out and whether the look ran as root. No secret of the box is in it.</summary>
    private static object SshProbeFactsForPage(BoxFacts f) => new
    {
        osId = f.OsId,
        osVersion = f.OsVersion,
        memTotal = f.MemTotal,
        exists = f.Exists,
        kinds = f.Kinds.ToDictionary(kv => kv.Key, kv => kv.Value.ToString().ToLowerInvariant(), StringComparer.Ordinal),
        free = f.Free,
        storedDirs = ServerVersions.All.Where(v => f.StoredDir(v).Length > 0).ToDictionary(v => v, v => f.StoredDir(v), StringComparer.Ordinal),
        hasPython = f.HasPython,
        hasDocker = f.HasDocker,
        sevenZip = f.SevenZip,
        hasCurl = f.HasCurl,
        hasWget = f.HasWget,
        agentInstalled = f.AgentInstalled,
        addresses = f.Addresses,
        routeSource = f.RouteSource,
        asRoot = f.AsRoot,
    };
}
