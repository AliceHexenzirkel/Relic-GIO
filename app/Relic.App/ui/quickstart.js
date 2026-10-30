'use strict';

// ─────────────────────────── Quick Start ───────────────────────────
// The start screen's guided setup for a player without technical knowledge (I57): a few questions — Mode, Game,
// Server, Account —, a summary, then ONE progress screen that runs two threads, the game and the server, over the rpcs
// that exist already, and the end that takes the player where the run leads. Loaded after app.js, whose state (S),
// actions (ACTIONS), drawing helpers and jobs it uses; app.js reaches this file only through guarded hooks — render()
// draws quickStart() while S.screen is 'quickstart', applyInit and ingestStatus queue qsTick(), the handlers of the
// jobs a run starts hand their events to qsEvent(), and boot opens a run that is not ended through qsResume().
//
// What a run writes, and through what: the host only through server.setAddress then mode.set (the start screen's
// order), admin.login, localagent.enter and an agent install (its ApplyInstalledAgent); the game through install.start;
// the agent through localagent.install / agent.install / localagent.update; an agent already on this PC learns Relic's
// 7zr.exe through agent.config.set; the stack through the chain an install leaves (deploy.done), chainAdd and the
// server jobs (runServerJob); the accounts through server.accountcreate (Server Admin) or account.create (Player); the
// launcher's own settings through settings.save. The plan file is written through quickstart.save alone, with what
// qsPlanForDisk copies. What is typed to log in somewhere stays in page memory (S.qs.mem): it never reaches the plan, a
// step's note, a line of the run's log or a toast. Every step is judged again from the facts at each look; the states
// the plan records are hints about what was started.

// ─────────────────────────── state ───────────────────────────
const QS_GIB = 1073741824;
// The file WSL's memory limit is written into (WslConfig, the user's own) — named by the summary.
const QS_WSLCONFIG = '%USERPROFILE%\\.wslconfig';
const QS_PAGES = ['mode', 'game', 'server', 'account', 'summary', 'progress'];
const QS_LOG_MAX = 200;
const QS_NOTE_MAX = 300;
// How long the last server may load its data before "Server ready" asks whether to go on.
const QS_LOADING_MS = 30 * 60 * 1000;
// A local-agent or Docker step refused because Fiddler runs while the game install sets it up: asked again every
// 10 s, for 30 minutes at most (S8).
const QS_FIDDLER_MS = 30 * 60 * 1000;
const QS_FIDDLER_RETRY_MS = 10000;
// The status of the run's server read again at most this often while a step waits on it.
const QS_STATUS_GAP_MS = 15000;
// The server steps whose facts come from the status of the run's server: while one of them runs, that status is read
// again (QS_STATUS_GAP_MS) — the launcher's own poll is slower.
const QS_STATUS_KINDS = ['agent7z', 'package', 'agentRestart', 'prepare', 'pathfinding', 'hotpatch', 'start', 'account', 'ready'];
const QS_TICK_MS = 2000;
// How long a resumed run waits for the facts that judge a step the plan file records as done (the first look at this
// PC, the first status of the agent on this PC) before it takes the record as it stands.
const QS_HINT_MS = 60000;
// How long an account step waits for a status of a server whose agent does not answer before it asks the player.
const QS_ACCT_WAIT_MS = 2 * 60 * 1000;
// How long the page waits after quickstart.restart answered before it takes the restart as held (a program with unsaved
// work may hold it, and the user may cancel it there).
const QS_RESTART_HELD_MS = 90000;
// The look at this PC asked again at most this often while a step waits on Docker Desktop's engine.
const QS_CHECK_EVERY_MS = 30000;
// The phases of the game install (InstallService's InstallPhase) past its downloads, past its unpacking, past an
// import's own check.
const QS_PAST_DOWNLOAD = ['Extract', 'Patch', 'Fiddler', 'Shortcut', 'Done'];
const QS_PAST_EXTRACT = ['Patch', 'Fiddler', 'Shortcut', 'Done'];
const QS_PAST_IMPORT = ['Patch', 'Fiddler', 'Shortcut', 'Done'];
// The installs the backend gates on the stored plan: the plan is saved right before each (quickstart.install).
const QS_PLAN_GATED = ['wsl', 'wslUpdate', 'docker', 'wslMemory'];

// What is typed to log in somewhere — never in the plan, a note, a log line or a toast: the agent token of an existing
// server, the SSH password and the key's passphrase of a Linux box, its sudo password, the account password, and a
// password a server generated for an account this run created (shown once on the Done page).
function qsMemBlank() { return { token: '', ssh: '', phrase: '', sudo: '', acct: '', generated: {} }; }
// "Find it on this PC" of the Game page (install.findGames): its number, what it found, its end.
function qsFindBlank() { return { search: 0, starting: false, stopping: false, progress: null, hits: [], done: null, error: '' }; }
// The look at a Linux box (ssh.probe): phase 'key' (the key it presents, read and never trusted) or 'box' (logged in:
// access, facts, its public address when asked); confirmed = the fingerprint the user said is the box's, changed = it was
// confirmed after the changed-key warning; key = the host:port the answers are about.
function qsProbeBlank() { return { busy: false, phase: '', hostKey: null, access: '', facts: null, publicIp: null, error: '', confirmed: '', changed: false, key: '', lookup: false, tries: 0 }; }
// Everything of Quick Start the page holds. page = the screen drawn; draft = the wizard's answers (page memory, kept
// until the launcher restarts); check = quickstart.check (machine, needs, tools); la = localagent.status; copy = the
// summary's deep checks of a copy the player has; folders = localagent.folderCheck of a server folder (Custom); keyRead =
// this page's latest read of a Linux box's key; inst / setup / job / acct = the run's jobs in flight (their events are the
// run's); plan = the run's plan, rt = its runtime; open / logOpen = the progress page's lists and consoles.
function qsBlank() {
  return {
    page: 'mode', draft: null, mem: qsMemBlank(),
    // (n = how many answers have come in, at = when the last one did: what a resumed run counts as a fresh look)
    check: { seq: 0, busy: false, again: false, res: null, n: 0, at: 0 },
    la: { busy: false, again: false, res: null, n: 0 },
    find: qsFindBlank(), copy: {}, folders: {}, keyRead: null,
    inst: null, setup: null, job: null, acct: null,
    plan: null, rt: null,
    open: { game: null, server: null }, logOpen: { game: false, server: false },
    busy: '',
  };
}
S.qs = qsBlank();

// The run's runtime (page memory): what this page session saw and did — never written anywhere.
function qsRtNew(resumed) {
  return {
    resumed: !!resumed,   // made by qsResume: a run taken up again (after a restart of Windows or of the launcher, or Stop)
    stopped: false,       // Stop (or a server change the run did not make): nothing more is started
    starting: false,      // the plan of a run that starts or goes on is being written: nothing is started before it is
    steps: [],            // the step descriptors of the plan (qsStepsOf)
    // A resumed run's steps the plan file records as done, still to be judged from the facts (qsHintsJudge): the file's
    // states are hints of what was started, never the truth. hintAt = when the run was resumed; checkN / laN / gen = the
    // looks at this PC, the agent on this PC's status reads and the admin status reads the page had then (a fact read
    // after them is a fresh one).
    hints: {}, hintAt: 0, checkN: 0, laN: 0, gen: 0,
    reconnect: {},        // step id → recorded done and set going again by the facts: it reconnects (never installs anew)
    target: null,         // srvTarget() as the run last wrote or accepted it
    ownWrite: 0,          // a step is writing the host or the mode: the status target it moves to is the run's own
    deploying: false,     // the run's agent install runs (its deploy.done / deploy.error end it)
    act: {},              // step id → what its pump sent ('' = nothing yet; also 'inst:<v>' for the game install)
    retryAt: {},          // step id → not before this time (a refusal waited out)
    fiddlerSince: {},     // step id → the first refusal "close Fiddler first" while the game install set Fiddler up
    busyTries: {},        // step id → how often the agent refused its job because another one ran
    since: {},            // step id → when it began to run
    instPhase: {},        // game version → the last install phase seen
    lines: { game: [], server: [] },
    detail: {},           // step id → its latest line
    agentHere: false,     // an agent was on this PC when the run's agent step had still to be done (localagent.enter)
    settingsSaved: false, // settings.save made (once, before the first game install)
    sevenZipPath: '',     // Relic's 7zr.exe as its install answered it
    wslRan: false, restartNeeded: null, dockerInstalled: false,
    changedKey: false,    // the Linux box's key was confirmed after the changed-key warning (Trust the new key)
    needKey: null,        // the key the box presents now, when it is not the one the plan names: asked again
    phraseAsked: false,   // the key's passphrase was asked again in this page session (a resumed run)
    askLogin: false,      // the SSH login is to be typed again before the next try (a Linux install that failed)
    names: {},            // server version → an account name typed again after a refusal
    tplFresh: {},         // server version → the account starts from the beginning (its template is not on the server)
    created: {},          // server version → the account this run created there (or the launcher's own it uses)
    firstAcct: {},        // server version → its account step has had its first look (an account of this launcher there)
    needPw: {},           // server version → the account password is to be typed again
    restartLater: false, restarting: false,
    restartPaused: {},    // what Restart Windows now paused ({game, fetch}: the version) — taken up again when Windows did not restart
    fetchCancelled: {},   // server version → the run itself stopped its download (Stop, the restart): that step is paused, not failed
    agentDown: false,     // the agent's restart stopped the agent on this PC and has not started it again yet
    acctWhy: {},          // server version → why its account step asks the player: 'name' | 'password' | 'status'
    jobGen: 0,            // srvGen when the run's last server job ended (qsFresh)
    statusAt: 0,
    reach: null,          // quickstart.reach's answer
    ready: null,          // server.test's answer (an existing server)
    fiddlerNote: '',      // the game install's line of its Fiddler step
    userSkipped: {},      // step id → skipped by the user (Skip)
    dirty: false,
  };
}

// ─────────────────────────── small helpers ───────────────────────────
// GB of 2^30 bytes, one decimal at most ("8", "7.8") — the backend's own spelling (SetupNeeds.Gb).
function qsGb(bytes) {
  const r = Math.round((Math.max(0, Number(bytes) || 0) / QS_GIB) * 10) / 10;
  return r % 1 === 0 ? r.toFixed(0) : r.toFixed(1);
}
const qsIpv4 = (s) => /^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$/.test(String(s == null ? '' : s).trim());
// A private, loopback, link-local, "this network" or shared (CGNAT) IPv4 address: one a player outside cannot be handed.
function qsPrivateV4(s) {
  if (!qsIpv4(s)) return false;
  const [a, b] = String(s).trim().split('.').map(Number);
  return a === 10 || a === 127 || a === 0 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168)
    || (a === 169 && b === 254) || (a === 100 && b >= 64 && b <= 127);
}
const qsSameHost = (a, b) => !!hostOf(a) && hostOf(a) === hostOf(b);
// A port typed: 0 = none typed, -1 = not a port.
function qsPortOf(text) {
  const raw = String(text == null ? '' : text).trim();
  if (!raw) return 0;
  const n = /^\d{1,5}$/.test(raw) ? parseInt(raw, 10) : NaN;
  return n >= 1 && n <= 65535 ? n : -1;
}
const qsMachine = () => (S.qs.check.res && S.qs.check.res.machine) || null;
const qsNeeds = () => (S.qs.check.res && S.qs.check.res.needs) || null;
const qsTools = () => (S.qs.check.res && S.qs.check.res.tools) || null;
const qsInstalls = () => { const n = qsNeeds(); return (n && n.installs) || {}; };
// An agent installed on this PC (the init state's fact — localagent.status says the rest).
const qsAgentOnPc = () => !!(S.isWindows && S.localAgent.init && S.localAgent.init.installed);
// The agent on this PC is not the build this launcher ships (agent.build — a content identity, never a number).
const qsAgentDiffers = () => !!(S.agentBuild.shipped && S.agentBuild.local && S.agentBuild.local.installed && (S.agentBuild.local.build || '') !== S.agentBuild.shipped);
// A server job runs, or the chain an install left still has steps, or the agent names one of its own.
const qsServerBusy = () => jobRunning() || !!S.fetchChain || !!(S.srv.info && S.srv.info.busy);
// A step that no longer moves by itself: done, skipped, or failed (its Retry is the player's). A step that needs the
// player is not one: what waits behind it waits for the answer.
const qsFinalState = (s) => s === 'done' || s === 'skipped' || s === 'failed';
const qsOkState = (s) => s === 'done' || s === 'skipped';

// ── the catalogue ──
// The catalogue's server version of a game version (versions[].server), else its own id.
const qsServerOf = (gv) => { const v = versionById(gv); return (v && typeof v.server === 'string' && v.server) || gv; };
const qsStack = (sv) => (Array.isArray(S.stacks) ? S.stacks : []).find((s) => s && s.id === sv) || null;
const qsGameIds = (d) => S.versions.map((v) => v.id).filter((id) => d.game.on[id]);
const qsGameFrom = (d, id) => (isInstalled(id) ? 'installed' : d.game.from[id] === 'local' ? 'local' : 'download');
// The distinct server versions of the ticked game versions that S.stacks lists, in S.stacks' order.
function qsServerIds(d) {
  const want = new Set(qsGameIds(d).map(qsServerOf));
  return (Array.isArray(S.stacks) ? S.stacks : []).filter((s) => s && want.has(s.id)).map((s) => s.id);
}
// The voice packs ticked for a download of a version — English(US) (the catalogue's default) when none is: the game
// needs a voice language, and the backend refuses an install without one.
function qsVoicePacks(d, id) {
  const packs = voicesFor(id);
  const m = d.game.voices[id] || (d.game.voices[id] = {});
  let ticked = packs.filter((p) => m[p.lang] === true);
  if (!ticked.length && packs.length) {
    const def = packs.find((p) => p.isDefault) || packs[0];
    m[def.lang] = true; ticked = [def];
  }
  return ticked;
}
const qsPackBytes = (packs) => packs.reduce((n, p) => n + (Math.max(0, Number(p.size)) || 0), 0);
const qsClientBytes = (id) => Math.max(0, Number((versionById(id) || {}).clientSize)) || 0;
const qsDownloadBytes = (d, id) => qsClientBytes(id) + qsPackBytes(qsVoicePacks(d, id));
// The games' folder of the run: Custom's, else the one the settings hold.
const qsRootOf = (d) => String((d && !d.express && d.game.installRoot) || S.settings.installRoot || '').trim();
// The server folder of a version on this PC's agent (stackFolder: beside the games, its fallback for a folder the agent
// could not take) and on a Linux box.
const qsWinFolder = (d, row) => stackFolder(row, true, true, qsRootOf(d));
const qsLxFolder = (row) => stackFolder(row, false);
// The hosts a download of the chosen versions can come from (the CDN, the mirrors, Drive), in the backend's order.
function qsSources(d) {
  const out = [];
  qsGameIds(d).filter((id) => qsGameFrom(d, id) === 'download').forEach((id) => versionSources(id).forEach((s) => { if (!out.includes(s)) out.push(s); }));
  return out;
}

// ── the run's facts ──
const qsStep = (id) => (S.qs.rt ? S.qs.rt.steps.find((s) => s.id === id) : null) || null;
const qsHas = (id) => !!qsStep(id);
function qsRec(id) {
  const p = S.qs.plan;
  if (!p) return { state: 'pending', note: '' };
  const steps = p.steps || (p.steps = {});
  if (!steps[id] || typeof steps[id] !== 'object') steps[id] = { state: 'pending', note: '' };
  return steps[id];
}
const qsStateOf = (id) => (qsHas(id) ? qsRec(id).state : null);
const qsPlanVer = (sv) => ((S.qs.plan && S.qs.plan.server.versions) || []).find((x) => x && x.id === sv) || null;
const qsLastVersion = () => { const v = ((S.qs.plan && S.qs.plan.server.versions) || []).filter((x) => x && x.id); return v.length ? v[v.length - 1].id : ''; };
// The host the run writes: the address typed, this PC's (127.0.0.1 or its LAN address — the one the
// agent on this PC answers on when it was there before), the SSH host.
function qsTargetHost() {
  const p = S.qs.plan;
  if (!p) return '';
  const s = p.server;
  if (s.kind === 'linux') return hostOf(s.ssh.host);
  if (s.kind === 'windows' && S.qs.rt && S.qs.rt.agentHere) {
    const la = S.qs.la.res;
    return hostOf((la && la.host) || s.address || (S.localAgent.init && S.localAgent.init.host) || '127.0.0.1');
  }
  return hostOf(s.address);
}
// The run talks to its own server now: the step that writes the host is done, the host is the target, and a status of
// it has been read — what the facts of the server's steps are read from.
function qsOnTarget() {
  const p = S.qs.plan;
  if (!p) return false;
  const via = p.server.kind === 'existing' ? 's.connect' : 's.agent';
  return qsOkState(qsStateOf(via)) && qsSameHost(S.serverAddr.host, qsTargetHost()) && S.srv.state === 'ok' && !!S.srv.info
    && (p.server.kind === 'existing' || !S.srv.info.public);
}
// The memory verdict of a server version (quickstart.check's needs.ram): fits | noPathfinding | short | unknown.
const qsVerdict = (sv) => { const n = qsNeeds(), r = n && n.ram && n.ram[sv]; return (r && r.verdict) || 'unknown'; };
// The chosen server versions whose package comes as a .7z only (no .zip beside it) and may be downloaded by this run.
function qsNeeds7z(p) {
  return ((p && p.server.versions) || []).filter((x) => {
    if (!x || !(x.choice === 'download' || x.choice === 'add' || x.choice === 'keep')) return false;
    const row = qsStack(x.id);
    return !!row && !!row.downloadable && !!row.needsExtractor && !(Number(row.zipSize) > 0);
  }).map((x) => x.id);
}
// Relic's 7zr.exe as this run learned it (its install's answer), else the extractor the check found when it is a 7zr.
function qsSevenZipPath() {
  const rt = S.qs.rt, m = qsMachine();
  if (rt && rt.sevenZipPath) return rt.sevenZipPath;
  return m && typeof m.extractor === 'string' && /(^|\\)7zr\.exe$/i.test(m.extractor) ? m.extractor : '';
}

// ─────────────────────────── the wizard's draft ───────────────────────────
function qsDraftNew() {
  const st = S.settings || {};
  return {
    express: true,
    game: {
      on: {}, from: {}, voices: {}, path: {}, replace: {}, inspect: {},
      installRoot: st.installRoot || '', source: st.source || 'cdn',
      enhancements: st.enhancements !== false, decrypt: st.fiddlerDecrypt !== false, cert: st.installCert !== false, shortcut: st.createShortcut !== false,
    },
    server: {
      kind: '',
      // an existing server
      address: '', admin: false, addrPort: '', test: null, testing: false, testKey: '',
      // a server Quick Start sets up: per server version {kind, here, on, choice, dir, stored, progress, pf, dirTyped, noPackage}
      vers: {}, reach: 'local', firewall: true, agentPort: '18080', keepEnv: false, autostart: true, agentKeep: false,
      pubIp: { busy: false, res: null }, pubTyped: '',
      ssh: { host: '', port: '22', user: 'root', auth: 'password', keyPath: '' },
      probe: qsProbeBlank(), public: '', bind: 'auto', bindIp: '',
    },
    account: { name: '', touched: false, tpl: {}, skip: {} },
    sum: { licence: false, memory: '', goOn: false },
  };
}

// The server versions of a server Quick Start sets up, with their defaults: the ones of the chosen game versions —
// skipped, with a note, when no package is published and no server is there. This PC: beside the games (stackFolder),
// downloaded; with an agent already on this PC its own folders (Keep), a version it has none for added. A Linux box:
// /home/<folder>, the folder the box's agent stores winning. A row the user changed (Custom) keeps the change. False
// while the facts it needs (the local agent's folders) are not in yet.
function qsSyncVers(d) {
  const s = d.server, kind = s.kind;
  if (kind !== 'windows' && kind !== 'linux') return true;
  const here = kind === 'windows' && qsAgentOnPc();
  if (here && !S.qs.la.res) return false;
  const next = {};
  qsServerIds(d).forEach((sv) => {
    const row = qsStack(sv);
    if (!row) return;
    const old = s.vers[sv], def = qsVerDefault(d, row, kind, here);
    if (old && old.kind === kind && old.here === here && old.touched && !d.express) {
      if (!old.dirTyped && old.choice !== 'keep' && old.choice !== 'ready') old.dir = def.dir;
      old.stored = def.stored; old.keptEmpty = def.keptEmpty; old.id = sv;
      next[sv] = old;
      return;
    }
    next[sv] = def;
  });
  s.vers = next;
  return true;
}
// A row's defaults. A folder the checks say holds a server already (the default folder beside the games keeps the
// servers an uninstall left, I16) is used as it stands — never downloaded into, prepared with the progress kept.
function qsVerDefault(d, row, kind, here) {
  const r = { id: row.id, kind, here, on: true, choice: 'download', dir: '', stored: '', progress: 'keep', pf: 'auto', dirTyped: false, noPackage: false, touched: false, keptEmpty: false };
  if (kind === 'windows') {
    const la = S.qs.la.res, kept = la && la.dirs && typeof la.dirs[row.id] === 'string' ? la.dirs[row.id].trim() : '';
    if (here && kept) {
      // The agent's own folder: kept. One that holds no server yet takes the download (the run's own, so the shipped save
      // may go in); one the agent says is prepared already is only started.
      const at = la.kinds && la.kinds[row.id];
      r.choice = 'keep'; r.dir = kept;
      r.keptEmpty = !!row.downloadable && (at === 'absent' || at === 'empty');
      if (r.keptEmpty) r.progress = 'default';
      else if (qsReadyHere(row.id, la)) r.choice = 'ready';
    } else if (here) {
      // a version the agent here has no folder for: added to it, in the default folder (its server used where it stands)
      r.dir = qsWinFolder(d, row);
      if (qsFolderKind(row.id, r.dir) === 'stack') r.choice = 'add';
      else if (row.downloadable) { r.choice = 'add'; r.progress = 'default'; } else { r.on = false; r.noPackage = true; }
    } else {
      r.dir = qsWinFolder(d, row);
      if (qsFolderKind(row.id, r.dir) === 'stack') r.choice = 'have';
      else if (row.downloadable) { r.choice = 'download'; r.progress = 'default'; }
      else { r.on = false; r.noPackage = true; }
    }
    return r;
  }
  const f = d.server.probe.facts;
  r.dir = qsLxFolder(row);
  r.stored = f && f.storedDirs && typeof f.storedDirs[row.id] === 'string' ? f.storedDirs[row.id] : '';
  const at = r.stored || r.dir, k = f && f.kinds ? f.kinds[at] : '';
  if (k === 'stack') r.choice = 'have';
  else if (row.downloadable) { r.choice = 'download'; r.progress = 'default'; }
  else { r.on = false; r.noPackage = true; }
  return r;
}
// The agent on this PC is the server Relic is connected to in Server Admin mode now, and its status says the version is
// there, set up and prepared.
function qsReadyHere(sv, la) {
  const port = Number(la && la.port) || 0;
  if (!(S.mode === 'admin' && S.serverConfigured && la && qsSameHost(S.serverAddr.host, la.host || '127.0.0.1')
    && (!port || Number(S.serverAddr.agentPort) === port) && S.srv.state === 'ok' && S.srv.info && !S.srv.info.public)) return false;
  const f = srvFacts(sv);
  return !!(f && f.present && f.bootstrapped && f.provisioned === true);
}
// A row whose stack this run downloads: a download, a version added to the agent here, a kept folder still empty — and
// not a folder a check says holds a server already (the install, the chain and the agent leave that server as it is).
const qsRowFetches = (r) => (r.choice === 'download' || r.choice === 'add' || (r.choice === 'keep' && !!r.keptEmpty)) && !(r.choice !== 'keep' && qsRowHolds(r));
// A row's folder holds a server, as a check said: this PC's (the machine check's look at the plan's folders, Custom's
// folder check), a Linux box's (its look at the folders).
function qsRowHolds(r) {
  if (!r || !r.id || !r.dir) return false;
  if (r.kind === 'linux') {
    const f = S.qs.draft && S.qs.draft.server.probe.facts;
    return !!(f && f.kinds && f.kinds[r.stored || r.dir] === 'stack');
  }
  return qsFolderKind(r.id, r.dir) === 'stack';
}
// What stands at a server folder on this PC as a check said it (localagent.folderCheck of Custom's field, else the
// machine check's look at the plan's folders): absent | empty | stack | other | unknown | ''.
function qsFolderKind(sv, dir) {
  const fc = S.qs.folders[sv];
  if (fc && fc.res && sameFolder(fc.dir, dir, true)) return fc.res.kind || '';
  const m = qsMachine(), f = m && Array.isArray(m.folders) ? m.folders.find((x) => x && x.version === sv && sameFolder(x.dir, dir, true)) : null;
  return (f && f.kind) || '';
}
// The account versions of a draft: an existing server's — the server versions of the chosen games —, else the rows that
// are set up; less the ones Custom unticked.
function qsAcctIds(d) {
  const ids = d.server.kind === 'existing' ? qsServerIds(d) : Object.keys(d.server.vers).filter((sv) => d.server.vers[sv].on);
  return (Array.isArray(S.stacks) ? S.stacks : []).map((s) => s.id).filter((sv) => ids.includes(sv));
}
const qsAcctOn = (d, sv) => !(!d.express && d.account.skip[sv]);

// ─────────────────────────── the plan ───────────────────────────
// The plan of the draft, in the model's shape (QuickStartPlan, camelCase): what the run is to do, its steps still to
// come. The installs are the ones the latest check names (what the summary showed), the licence tick, the memory
// chosen, and the replacement of an agent on this PC that is another build.
function qsPlanOf(d) {
  const st = S.settings || {}, sv = d.server, kind = sv.kind, exp = d.express, m = qsMachine(), n = qsInstalls();
  const game = {
    installRoot: qsRootOf(d),
    enhancements: exp ? st.enhancements !== false : !!d.game.enhancements,
    fiddlerDecrypt: exp ? st.fiddlerDecrypt !== false : !!d.game.decrypt,
    installCert: exp ? st.installCert !== false : !!d.game.cert,
    createShortcut: exp ? st.createShortcut !== false : !!d.game.shortcut,
    source: exp ? (st.source || 'cdn') : (d.game.source || st.source || 'cdn'),
    versions: qsGameIds(d).map((id) => {
      const from = qsGameFrom(d, id);
      return {
        id, from, voices: from === 'download' ? qsVoicePacks(d, id).map((p) => p.lang) : [],
        localPath: from === 'local' ? String(d.game.path[id] || '').trim() : '',
        replaceInstalledAt: from === 'local' ? qsReplaceAt(d, id) : '',
      };
    }),
  };
  const server = {
    kind, address: '', agentPort: 18080, admin: false, reach: 'local', bindIp: '', advertisedIp: '', listen: '', keepEnv: null,
    firewall: false, autostart: true, ssh: { host: '', port: 22, user: 'root', keyPath: '', hostKey: { type: '', fingerprint: '' } },
    versions: [], installs: { python: false, sevenZip: false, wsl: false, wslUpdate: false, docker: false, dockerStart: false, acceptLicence: false, wslMemoryGb: 0, agentUpdate: false },
  };
  const rows = () => Object.keys(sv.vers).filter((v) => sv.vers[v].on).map((v) => {
    const r = sv.vers[v];
    return { id: v, choice: r.choice, dir: kind === 'linux' ? (r.stored || r.dir) : r.dir, progress: qsProgressOf(d, r), pathfinding: r.pf === 'on' ? true : r.pf === 'off' ? false : null, fetchedByRun: false };
  });
  if (kind === 'existing') {
    server.address = sv.address.trim();
    const port = qsPortOf(sv.addrPort);
    server.agentPort = port > 0 ? port : 0;
    server.admin = !!sv.admin && (!!S.qs.mem.token.trim() || tokenAppliesTo(server.address));
  } else if (kind === 'windows') {
    const here = qsAgentOnPc(), la = S.qs.la.res;
    if (here) {
      const listen = String((la && la.listen) || '127.0.0.1:18080');
      server.address = hostOf((la && la.host) || (S.localAgent.init && S.localAgent.init.host) || '127.0.0.1');
      server.agentPort = (la && la.port) || (S.localAgent.init && S.localAgent.init.port) || 18080;
      server.listen = /^\d+\.\d+\.\d+\.\d+:\d+$/.test(listen) ? listen : '';
      server.bindIp = qsIpv4(la && la.bindIp) ? la.bindIp.trim() : '';
      server.reach = server.address !== '127.0.0.1' ? 'lan' : 'local';
      server.autostart = !!(la && la.autostart);
    } else {
      const reach = exp ? 'local' : sv.reach, port = exp ? 18080 : (qsPortOf(sv.agentPort) > 0 ? qsPortOf(sv.agentPort) : 18080);
      const lan = (m && m.lanIp) || '';
      server.reach = reach !== 'local' && lan ? reach : 'local';
      server.agentPort = port;
      server.bindIp = server.reach === 'local' ? '' : lan;
      server.listen = (server.reach === 'local' ? '127.0.0.1' : '0.0.0.0') + ':' + port;
      server.address = server.reach === 'local' ? '127.0.0.1' : lan;
      server.advertisedIp = server.reach === 'internet' ? qsWinPublic(d) : '';
      server.firewall = !exp && server.reach !== 'local' && !!sv.firewall;
      server.keepEnv = !exp && sv.keepEnv ? true : null;
      server.autostart = exp ? true : !!sv.autostart;
    }
    server.admin = true;
    server.versions = rows();
    server.installs = {
      python: !!n.python, sevenZip: !!n.sevenZip, wsl: !!n.wsl, wslUpdate: !!n.wslUpdate, docker: !!n.docker, dockerStart: !!n.dockerStart,
      acceptLicence: !!n.docker && !!d.sum.licence, wslMemoryGb: qsRaiseChosen(d) ? qsRaiseGb() : 0,
      agentUpdate: here && qsAgentDiffers() && !(!exp && sv.agentKeep),
    };
  } else if (kind === 'linux') {
    const port = exp ? 18080 : (qsPortOf(sv.agentPort) > 0 ? qsPortOf(sv.agentPort) : 18080), h = sv.ssh, pr = sv.probe;
    server.address = h.host.trim();
    server.agentPort = port;
    server.admin = true;
    server.listen = '0.0.0.0:' + port;
    server.bindIp = !exp && sv.bind === 'fixed' && qsIpv4(sv.bindIp) ? sv.bindIp.trim() : '';
    server.reach = sv.public === 'yes' ? 'public' : 'lan';
    server.advertisedIp = qsLxAdvertised(d) || '';
    server.keepEnv = !exp && sv.keepEnv ? true : null;
    server.ssh = { host: h.host.trim(), port: qsPortOf(h.port) > 0 ? qsPortOf(h.port) : 22, user: h.user.trim() || 'root', keyPath: h.auth === 'key' ? h.keyPath.trim() : '',
      hostKey: { type: (pr.hostKey && pr.hostKey.type) || '', fingerprint: pr.confirmed || '' } };
    server.versions = rows();
  }
  const name = d.account.name.trim(), create = {};
  qsAcctIds(d).forEach((v) => { if (qsAcctOn(d, v) && !qsAcctNoAgent(d)) create[v] = { template: qsTplOf(d, v) }; });
  return { id: '', state: 'running', express: exp, startedAt: '', game, server, account: { name: qsNameOk(d, name) ? name : '', create }, steps: {} };
}

// The plan as quickstart.save writes it: a copy of the model's own fields, each of its own type and size, and nothing
// else — whatever else the page holds beside the plan (a login typed, an answer of the backend) cannot reach the file.
// A note is cut to QS_NOTE_MAX characters; the store also blanks the value of a secret assignment one may quote.
function qsPlanForDisk(p) {
  const o = p && typeof p === 'object' ? p : {};
  const g = o.game || {}, s = o.server || {}, h = s.ssh || {}, k = h.hostKey || {}, n = s.installs || {}, a = o.account || {};
  const str = (x, max) => String(x == null ? '' : x).slice(0, max || 4096);
  const int = (x, lo, hi, dflt) => { const v = Number(x); return Number.isInteger(v) && v >= lo && v <= hi ? v : dflt; };
  const tri = (x) => (x === true || x === false ? x : null);
  const list = (x) => (Array.isArray(x) ? x.filter((y) => y && typeof y === 'object').slice(0, 10) : []);
  const steps = {};
  Object.keys(o.steps || {}).slice(0, 256).forEach((id) => {
    const r = o.steps[id] || {};
    steps[str(id, 64)] = { state: str(r.state, 16), note: str(r.note, QS_NOTE_MAX) };
  });
  const create = {};
  Object.keys(a.create || {}).slice(0, 10).forEach((v) => { create[str(v, 32)] = { template: str((a.create[v] || {}).template, 64) }; });
  return {
    id: str(o.id, 8), state: str(o.state, 16), express: o.express !== false, startedAt: str(o.startedAt, 64),
    game: {
      installRoot: str(g.installRoot), enhancements: g.enhancements !== false, fiddlerDecrypt: g.fiddlerDecrypt !== false,
      installCert: g.installCert !== false, createShortcut: g.createShortcut !== false, source: str(g.source, 64),
      versions: list(g.versions).map((v) => ({ id: str(v.id, 32), from: str(v.from, 16),
        voices: (Array.isArray(v.voices) ? v.voices : []).slice(0, 10).map((x) => str(x, 64)),
        localPath: str(v.localPath), replaceInstalledAt: str(v.replaceInstalledAt) })),
    },
    server: {
      kind: str(s.kind, 16), address: str(s.address, 300), agentPort: int(s.agentPort, 0, 65535, 18080), admin: s.admin === true,
      reach: str(s.reach, 16), bindIp: str(s.bindIp, 15), advertisedIp: str(s.advertisedIp, 15), listen: str(s.listen, 21),
      keepEnv: tri(s.keepEnv), firewall: s.firewall === true, autostart: s.autostart !== false,
      ssh: { host: str(h.host, 255), port: int(h.port, 1, 65535, 22), user: str(h.user, 255), keyPath: str(h.keyPath),
        hostKey: { type: str(k.type, 64), fingerprint: str(k.fingerprint, 64) } },
      versions: list(s.versions).map((v) => ({ id: str(v.id, 32), choice: str(v.choice, 16), dir: str(v.dir), progress: str(v.progress, 16),
        pathfinding: tri(v.pathfinding), fetchedByRun: v.fetchedByRun === true })),
      installs: { python: n.python === true, sevenZip: n.sevenZip === true, wsl: n.wsl === true, wslUpdate: n.wslUpdate === true,
        docker: n.docker === true, dockerStart: n.dockerStart === true, acceptLicence: n.acceptLicence === true,
        wslMemoryGb: int(n.wslMemoryGb, 0, 1024, 0), agentUpdate: n.agentUpdate === true },
    },
    account: { name: str(a.name, 20), create },
    steps,
  };
}

// The starting progress a row goes with: Express takes the shipped save for a stack this run downloads and `keep` for
// every other (the runner checks it again); Custom its choice, `default` only where this run downloads the stack (I57).
function qsProgressOf(d, r) {
  const fetches = qsRowFetches(r);
  if (d.express) return fetches ? 'default' : 'keep';
  if (r.progress === 'default') return fetches ? 'default' : 'keep';
  return r.progress === 'fixes' ? 'fixes' : 'keep';
}
// The memory raise of the summary: chosen (it is preselected when it is possible) and possible.
const qsRaiseGb = () => { const n = qsNeeds(); return (n && Number(n.raiseGb)) || 0; };
const qsRaiseChosen = (d) => d.server.kind === 'windows' && qsRaiseGb() > 0 && d.sum.memory !== 'leave';
// A version stays short of memory: its verdict says so, and no raise makes it fit.
function qsStaysShort(d) {
  const n = qsNeeds();
  if (!n || !n.ram) return false;
  return Object.keys(n.ram).some((v) => n.ram[v] && n.ram[v].verdict === 'short' && !(qsRaiseChosen(d) && Number(n.ram[v].raiseGb) > 0));
}
// The public address of a server on this PC that players on the Internet join: this PC's own, as two services agreed
// (quickstart.publicIp), else the one typed.
function qsWinPublic(d) {
  const r = d.server.pubIp.res;
  if (r && qsIpv4(r.address)) return r.address;
  return qsIpv4(d.server.pubTyped) ? d.server.pubTyped.trim() : '';
}
// The address a Linux box advertises: '' for "only my network" and for a box whose own address is the
// public one; that address otherwise (NAT in front) — the typed one when it is public, the box's own lookup's, else the
// one typed in its field. null = it is still to be learned or typed.
function qsLxAdvertised(d) {
  const s = d.server, f = s.probe.facts;
  if (s.public !== 'yes') return '';
  const typed = hostOf(s.ssh.host), own = (f && Array.isArray(f.addresses) ? f.addresses : []).map(String);
  if (qsIpv4(typed) && !qsPrivateV4(typed)) return own.includes(typed) ? '' : typed;
  const learned = s.probe.publicIp && qsIpv4(s.probe.publicIp.address) ? s.probe.publicIp.address : '';
  const ip = learned || (qsIpv4(s.pubTyped) ? s.pubTyped.trim() : '');
  if (!ip) return null;
  return own.includes(ip) ? '' : ip;
}
// The address players on the Internet connect to for a Linux box that is public (the summary, the reach check).
function qsLxPublicOf(d) {
  const s = d.server, adv = qsLxAdvertised(d);
  if (adv) return adv;
  const typed = hostOf(s.ssh.host);
  if (qsIpv4(typed) && !qsPrivateV4(typed)) return typed;
  return (s.probe.publicIp && s.probe.publicIp.address) || s.pubTyped.trim() || typed;
}
// The folder the player's copy takes the place of, when the check found the version installed elsewhere and the player
// chose this copy (the import's own rule).
function qsReplaceAt(d, id) {
  const r = d.game.replace[id];
  return r && sameDirText(r.path, d.game.path[id]) ? r.installedAt : '';
}

// ── accounts ──
// The snapshot an existing server's Test answered (its agent's public status), or null.
const qsSnap = (d) => (d.server.test && d.server.test.agent && d.server.test.agent.reachable && d.server.test.agent.status) || null;
// A player's existing server whose agent did not answer the Test of its address: no account can be made there from here
// (the plan names none — the player creates it later, or asks the server's admin).
const qsAcctNoAgent = (d) => d.server.kind === 'existing' && !d.server.admin && !!d.server.test && d.server.testKey === qsTestKey(d) && !qsSnap(d);
// The starting progresses offered on a server version: an existing server's offer (acctOffer on its Test's snapshot),
// else the version's shipped templates and a new account.
function qsTplChoices(d, sv) {
  const fresh = { id: 'fresh', label: t('account.tpl.fresh') };
  if (d.server.kind === 'existing') {
    const offer = acctOffer(sv, qsSnap(d));
    return offer && offer.length ? offer : [fresh];
  }
  const m = qsMachine(), list = m && m.templates && Array.isArray(m.templates[sv]) ? m.templates[sv].filter((x) => x && x.id && x.id !== 'fresh') : [];
  return list.map((x) => ({ id: x.id, label: x.label || x.id })).concat([fresh]);
}
// The default: the first offered that is not a new account, else a new account.
function qsTplOf(d, sv) {
  const list = qsTplChoices(d, sv), want = d.account.tpl[sv];
  if (want && list.some((x) => x.id === want)) return want;
  const first = list.find((x) => x.id !== 'fresh');
  return first ? first.id : 'fresh';
}
const qsTplLabel = (d, sv, id) => { const x = qsTplChoices(d, sv).find((y) => y.id === id); return (x && x.label) || (id === 'fresh' ? t('account.tpl.fresh') : id); };
// The server verifies passwords on that version (an existing server's snapshot; a stack Quick Start sets up does not).
function qsVerifies(d, sv) {
  const snap = d.server.kind === 'existing' ? qsSnap(d) : null;
  return !!(snap && snap.versions && snap.versions[sv] && snap.versions[sv].passwordVerify);
}
// The pre-made login of a version: an existing server's word (its snapshot), else the shipped save's account.
function qsPreMade(d, sv) {
  if (d.server.kind === 'existing') { const snap = qsSnap(d); return (snap && snap.versions && snap.versions[sv] && snap.versions[sv].defaultAccount) || ''; }
  const gv = qsGameIds(d).find((id) => qsServerOf(id) === sv) || sv;
  return catalogueAccountOf(gv);
}
// The agent's name rule (NAME_RE, digits alone refused, RESERVED_PREFIXES) and not the version's pre-made login.
function qsNameOk(d, name) {
  const n = String(name || '');
  if (!ACCOUNT_NAME_RE.test(n) || /^\d+$/.test(n) || /^(relic|tpl|admin|gm)/i.test(n)) return false;
  return !qsAcctIds(d).some((sv) => { const pm = qsPreMade(d, sv); return !!pm && pm.toLowerCase() === n.toLowerCase(); });
}
// The rules of qsNameOk beyond the letters (plain text): digits alone, the reserved starts, the ready-made logins.
function qsNameRules(d) {
  const pms = d ? qsAcctIds(d).map((sv) => qsPreMade(d, sv)).filter((x, i, a) => !!x && a.indexOf(x) === i) : [];
  return t('qs.acct.nameRules') + (pms.length ? ' ' + t('qs.acct.nameNotPremade', { names: pms.join(', ') }) : '');
}

// ─────────────────────────── drawing ───────────────────────────
// The screen: the page of the wizard, the progress page or the Done page, under the overlays the click guards wait on
// (a question, a notice, the agent update) — drawn here as login() and shell() draw them.
function quickStart() {
  const q = S.qs;
  if (!q.draft && q.page !== 'progress' && q.page !== 'done') { q.draft = qsDraftNew(); q.page = 'mode'; }
  const page = q.page;
  let body;
  try {
    body = page === 'mode' ? qsModePage() : page === 'game' ? qsGamePage() : page === 'server' ? qsServerPage()
      : page === 'account' ? qsAccountPage() : page === 'summary' ? qsSummaryPage() : page === 'done' ? qsDonePage() : qsRunPage();
  } catch (e) { console.error(e); body = ''; }
  const wizard = page !== 'progress' && page !== 'done';
  const over = S.confirm ? confirmOverlay() : S.notice ? noticeOverlay() : S.agentUpdate ? agentUpdateOverlay() : '';
  // (the shell's frame — its height, the overlays' positioning context — without its sidebar)
  return `<div class="shell qs-shell">
    <div class="main qs-main" data-qs-page="${esc(page)}">
      <div class="pad qs-pad" style="max-width:1060px;margin:0 auto">
        <div class="flex between center qs-head" style="margin-bottom:16px"><h1 class="title">${page === 'progress' ? T('qs.run.title') : page === 'done' ? (qsDoneAllSet() ? T('qs.done.title') : T('qs.done.titlePartial')) : T('qs.title')}</h1>
          ${wizard ? `<button class="btn ghost" style="padding:9px 16px;font-size:12.5px" data-act="qsClose">${T('common.close')}</button>` : ''}</div>
        ${page === 'done' ? '' : qsStepBar(page)}
        ${body}
      </div>
    </div>${over}</div>`;
}
function qsPageName(k) {
  switch (k) {
    case 'mode': return T('qs.steps.mode');
    case 'game': return T('qs.steps.game');
    case 'server': return T('qs.steps.server');
    case 'account': return T('qs.steps.account');
    case 'summary': return T('qs.steps.summary');
    default: return T('qs.steps.progress');
  }
}
// The step bar: a page passed is a way back to it (before Start) — a button, so the keyboard reaches it too.
function qsStepBar(page) {
  const at = QS_PAGES.indexOf(page);
  return `<div class="stepper qs-steps">${QS_PAGES.map((k, i) => {
    const cls = i < at ? 'done' : i === at ? 'active' : 'todo';
    const inner = `<span class="num ${cls}">${i < at ? I.check : i + 1}</span><span class="lbl">${qsPageName(k)}</span>`;
    const head = i < at && page !== 'progress'
      ? `<button type="button" class="flex center" data-act="qsGoto" data-arg="${k}" style="gap:9px;cursor:pointer">${inner}</button>`
      : `<div class="flex center" style="gap:9px">${inner}</div>`;
    return `<div class="step">${head}${i < QS_PAGES.length - 1 ? '<span class="bar"></span>' : ''}</div>`;
  }).join('')}</div>`;
}
function qsFoot(next, act, dead) {
  return `<div class="wizfoot qs-foot">${S.qs.page !== 'mode' ? `<button class="btn ghost" data-act="qsBack">${T('common.back')}</button>` : '<span></span>'}
    <button class="btn${act === 'qsStart' ? ' glow' : ''}" data-act="${act}" data-qs-next ${dead ? 'disabled' : ''}>${next}</button></div>`;
}
// A choice card (title and sub already escaped): a button, so the keyboard reaches it (aria-pressed = chosen).
const qsCard = (act, arg, on, title, sub, dead) => `<button type="button" class="opt qs-card${on ? ' sel' : ''}${dead ? ' dis' : ''}" aria-pressed="${on ? 'true' : 'false'}"${dead ? ' disabled aria-disabled="true"' : ` data-act="${act}" data-arg="${esc(arg)}"`}><div class="h">${title}</div>${sub ? `<p>${sub}</p>` : ''}</button>`;
// A row with a switch.
const qsSwitch = (act, arg, on, title, sub, dead) => `<div class="row qs-row" style="margin-top:10px"><div><b>${title}</b>${sub ? `<small>${sub}</small>` : ''}</div><button class="toggle${on ? ' on' : ''}" data-act="${act}" data-arg="${esc(arg || '')}" ${dead ? 'disabled' : ''}><span class="knob"></span></button></div>`;
// A text field of the page: its value goes to the draft or to page memory through the page's own input handler.
function qsInput(name, value, o) {
  const x = o || {};
  return `<input class="txt${x.mono ? ' mono' : ''} qs-input" data-qsf="${esc(name)}"${x.v ? ` data-qsv="${esc(x.v)}"` : ''} type="${x.type || 'text'}" value="${esc(value == null ? '' : value)}" placeholder="${esc(x.ph || '')}"${x.max ? ` maxlength="${x.max}"` : ''} autocomplete="${x.type === 'password' ? 'new-password' : 'off'}" spellcheck="false"${x.enter ? ` data-enter="${esc(x.enter)}"${x.arg ? ` data-arg="${esc(x.arg)}"` : ''}` : ''}${x.dead ? ' disabled' : ''}>`;
}
const qsLabel = (text, top) => `<label class="fld"${top ? ' style="margin-top:0"' : ''}>${text}</label>`;
const qsHint = (text) => `<p class="hint qs-hint" style="margin:6px 0 0">${text}</p>`;
// A status line (ok | bad | busy | ''), the import check's look.
const qsLine = (cls, html) => `<div class="impv-line qs-line${cls ? ' ' + cls : ''}" style="margin-top:10px">${cls === 'busy' ? spinnerSmall() : ''}<span>${html}</span></div>`;
const qsNote = (html, top) => `<div class="note qs-note" style="margin-top:${top == null ? 10 : top}px">${html}</div>`;

// ── Mode ──
function qsModePage() {
  const d = S.qs.draft;
  const host = S.serverAddr && S.serverAddr.host;
  const cur = host && S.serverConfigured && (S.mode === 'player' || S.mode === 'admin')
    ? qsHint(T('qs.mode.current', { host, mode: S.mode === 'admin' ? t('common.mode.admin') : t('common.mode.player') })) : '';
  const express = `<div class="flex center" style="gap:10px">${T('qs.mode.express')}<span class="vchip qs-tag">${T('qs.mode.recommended')}</span></div>`;
  return `<div class="panel qs-panel"><h3>${T('qs.mode.title')}</h3>
    <div class="grid2" style="margin-top:14px">${qsCard('qsMode', 'express', d.express, express, T('qs.mode.expressSub'))}${qsCard('qsMode', 'custom', !d.express, T('qs.mode.custom'), T('qs.mode.customSub'))}</div>
    ${cur}</div>${qsFoot(T('qs.next'), 'qsNext')}`;
}

// ── Game ──
function qsGamePage() {
  const d = S.qs.draft;
  return `<div class="panel qs-panel"><h3>${T('qs.game.title')}</h3><p class="hint">${T('qs.game.hint')}</p>
    <div class="qs-vcards" style="display:flex;flex-direction:column;gap:12px">${S.versions.map((v) => qsGameCard(d, v)).join('')}</div>
    ${d.express ? '' : qsGameCustom(d)}</div>${qsFoot(T('qs.next'), 'qsNext')}`;
}
function qsGameCard(d, v) {
  const id = v.id, on = !!d.game.on[id], inst = isInstalled(id);
  const head = `<div class="flex between center" style="gap:12px">
      <div class="flex center" style="gap:14px"><button class="toggle${on ? ' on' : ''}" data-act="qsGameTick" data-arg="${esc(id)}"><span class="knob"></span></button>
        <div><b style="font-size:15px;color:var(--ink)">${T('common.versionN', { id })}</b><small style="display:block;color:var(--muted);font-weight:700;font-size:11.5px;margin-top:2px">${esc(v.desc || v.title || '')}</small></div></div>
      ${inst ? `<span class="vchip qs-chip">${T('qs.game.inLibrary')}</span>` : ''}</div>`;
  let body = '';
  if (on && inst) body = qsHint(T('qs.game.inLibraryNote'));
  else if (on) {
    const from = qsGameFrom(d, id);
    body = `<div class="grid2" style="margin-top:12px">${qsCard('qsGameFrom', id + ':download', from === 'download', T('qs.game.download'), from === 'download' ? T('qs.game.downloadSize', { size: sizeGb(qsDownloadBytes(d, id)) }) : '')}${qsCard('qsGameFrom', id + ':local', from === 'local', T('qs.game.have'), '')}</div>`
      + (from === 'download' ? qsVoicesHtml(d, id) : qsHaveHtml(d, id));
  }
  return `<div class="sect qs-vcard" style="margin:0">${head}${body}</div>`;
}
function qsVoicesHtml(d, id) {
  const packs = voicesFor(id);
  if (!packs.length) return '';
  const ticked = qsVoicePacks(d, id);
  const rows = packs.map((p) => `<div class="line"><div><b>${esc(p.lang)}</b><small>${esc(p.sizeText || sizeGb(p.size))}</small></div>
      <button class="toggle${ticked.includes(p) ? ' on' : ''}" data-act="qsVoice" data-arg="${esc(id + '|' + p.lang)}"><span class="knob"></span></button></div>`).join('');
  const client = qsClientBytes(id), voices = qsPackBytes(ticked);
  return `<div class="qs-voices" style="margin-top:14px"><b style="font-size:13px;color:var(--ink)">${T('wizard.step.voices.title')}</b>${qsHint(T('wizard.step.voices.hint'))}
    <div class="sect" style="padding:2px 18px;margin:10px 0 0">${rows}</div>${qsHint(T('wizard.step.voices.total', { total: gb(client + voices), client: gb(client), voices: gb(voices) }))}</div>`;
}
function qsHaveHtml(d, id) {
  return `<div class="flex gap" style="margin-top:12px">${qsInput('gamePath', d.game.path[id] || '', { mono: true, v: id, ph: t('wizard.step.source.localPlaceholder', { version: id }) })}
      <button class="btn ghost" style="flex:none" data-act="qsPickExe" data-arg="${esc(id)}">${T('qs.game.pickExe')}</button></div>
    <div data-live="qs-verdict:${esc(id)}">${qsVerdictHtml(d, id)}</div>
    <div data-live="qs-find:${esc(id)}">${qsFindHtml(d, id)}</div>`;
}
const qsInspectKey = (d, id) => [String(d.game.path[id] || '').trim(), id, qsReplaceAt(d, id)].join('\n');
// The instant check of the copy a version's card names (install.inspectGame, deep: false): its line under the card.
function qsVerdictHtml(d, id) {
  if (!String(d.game.path[id] || '').trim()) return '';
  const ins = d.game.inspect[id];
  if (!ins || ins.key !== qsInspectKey(d, id) || ins.phase === 'checking') return qsLine('busy', T('qs.game.checking'));
  if (ins.phase === 'error') return qsLine('bad', esc(ins.error));
  const v = ins.v || {};
  if (v.status === 'refused') {
    const instead = v.reason === 'installedElsewhere' && v.installedAt
      ? `<div class="impv-acts"><button class="btn ghost" data-act="qsUseInstead" data-arg="${esc(id)}">${T('wizard.verdict.useInstead')}</button></div>` : '';
    return qsLine('bad', esc(v.message || '')) + instead;
  }
  return qsLine(v.status === 'ready' ? 'ok' : '', esc(v.message || ''));
}
const qsFindLive = () => S.qs.find.starting || (S.qs.find.search > 0 && !S.qs.find.done);
// "Find it on this PC" under a version's card: its button, its progress, the folders found for that version.
function qsFindHtml(d, id) {
  const f = S.qs.find, live = qsFindLive();
  const hits = f.hits.map((h, i) => ({ h, i })).filter((x) => x.h && x.h.dir && (x.h.status === 'use' || x.h.status === 'pick') && x.h.version === id);
  const btn = live ? `<button class="btn ghost" data-act="qsFindStop" ${f.stopping ? 'disabled' : ''}>${T('qs.game.findStop')}</button>`
    : `<button class="btn ghost" data-act="qsFind">${T('qs.game.find')}</button>`;
  const prog = live ? qsLine('busy', T('qs.game.finding', { dirs: f.progress ? Math.max(0, Math.round(Number(f.progress.dirs) || 0)) : 0 })) : '';
  const list = hits.map((x) => `<div class="fhit"><div class="fhit-main"><b class="mono pick">${esc(x.h.dir)}</b>${x.h.note ? `<small class="warn">${esc(x.h.note)}</small>` : ''}</div>
      <div class="fhit-act">${sameDirText(x.h.dir, d.game.path[id]) ? `<span class="fhit-tag on">${T('wizard.find.chosen')}</span>` : `<button class="btn ghost" data-act="qsUseHit" data-arg="${esc(id + '|' + x.i)}">${T('qs.game.useHit')}</button>`}</div></div>`).join('');
  let none = '';
  if (!live && f.error) none = qsLine('bad', esc(f.error));
  else if (!live && f.done && !hits.length) {
    none = f.done.deeper && !f.done.error ? `${qsHint(T('qs.game.findNone'))}<div class="impv-acts"><button class="btn ghost" data-act="qsFindDeeper">${T('qs.game.findDeeper')}</button></div>`
      : qsHint(T('qs.game.findNoneAll'));
  }
  return `<div class="flex gap wrap" style="margin-top:10px">${btn}</div>${prog}${list ? `<div class="fhits" style="margin-top:8px">${list}</div>` : ''}${none}`;
}
function qsGameCustom(d) {
  const g = d.game, srcs = qsSources(d);
  return `<div class="sect qs-custom" style="margin:16px 0 0">
    ${qsLabel(T('qs.game.folder'), true)}<div class="flex gap">${qsInput('installRoot', g.installRoot, { mono: true })}<button class="btn ghost" style="flex:none" data-act="qsPickRoot">${T('common.browse')}</button></div>
    ${srcs.length > 1 ? `${qsLabel(T('qs.game.source'))}<div class="flex gap wrap">${srcs.map((s) => `<button class="lg-chip ${g.source === s ? 'on' : ''}" data-act="qsSource" data-arg="${esc(s)}">${esc(sourceLabel(s))}</button>`).join('')}</div>` : ''}
    ${qsSwitch('qsGameOpt', 'enhancements', g.enhancements, T('qs.game.enhancements'), T('qs.game.enhancementsSub'))}
    ${qsSwitch('qsGameOpt', 'decrypt', g.decrypt, T('common.decryptHttps'), T('wizard.step.fiddler.decryptNote'))}
    ${qsSwitch('qsGameOpt', 'cert', g.cert, T('wizard.step.fiddler.cert'), T('wizard.step.fiddler.certNote'))}
    ${qsSwitch('qsGameOpt', 'shortcut', g.shortcut, T('wizard.step.finish.desktopShortcut'), '')}</div>`;
}

// ── Server ──
function qsServerPage() {
  const d = S.qs.draft, k = d.server.kind;
  const cards = `<div class="grid3" style="margin-top:14px">${qsCard('qsKind', 'existing', k === 'existing', T('qs.server.have'), T('qs.server.haveSub'))}${qsCard('qsKind', 'windows', k === 'windows', T('qs.server.windows'), T('qs.server.windowsSub'), !S.isWindows)}${qsCard('qsKind', 'linux', k === 'linux', T('qs.server.linux'), T('qs.server.linuxSub'))}</div>`;
  const body = k === 'existing' ? qsExistingHtml(d) : k === 'windows' ? qsWindowsHtml(d) : k === 'linux' ? qsLinuxHtml(d) : '';
  // (Next is dead until a card is chosen: the question has no safe answer)
  return `<div class="panel qs-panel"><h3>${T('qs.server.title')}</h3>${cards}${body}</div>${qsFoot(T('qs.next'), 'qsNext', !k || (k === 'windows' && qsWinBlocked()))}`;
}
// (a) an existing server
function qsExistingHtml(d) {
  const s = d.server, m = S.qs.mem;
  const addr = `${qsLabel(T('qs.server.address'))}<div class="flex gap">${qsInput('address', s.address, { mono: true, ph: t('qs.server.addressPh'), enter: 'qsTest' })}
      <button class="btn ghost" style="flex:none" data-act="qsTest" ${s.testing ? 'disabled' : ''}>${T('login.test')}</button></div>${qsHint(T('qs.server.addressHint'))}`;
  const port = d.express ? '' : `${qsLabel(T('login.agentPortLabel'))}${qsInput('addrPort', s.addrPort, { mono: true, ph: '18080' })}${qsHint(T('login.agentPortHint'))}`;
  const token = s.admin ? `${qsLabel(T('login.tokenLabel'))}${qsInput('token', m.token, { mono: true, type: 'password', ph: tokenAppliesTo(s.address) ? t('login.tokenKept') : t('login.tokenPlaceholder') })}${qsHint(T('login.tokenHint'))}` : '';
  return `<div class="qs-existing" style="margin-top:18px">${addr}<div data-live="qs-test">${qsTestHtml(d)}</div>${port}
    ${qsSwitch('qsAdmin', '', s.admin, T('qs.server.isAdmin'), '')}${token}</div>`;
}
const qsTestKey = (d) => [d.server.address.trim(), qsPortOf(d.server.addrPort)].join('|');
// The server versions of the chosen games that an existing server's Test answer does not host.
function qsNotHosted(d, r) {
  const vs = r && r.agent && r.agent.status && r.agent.status.versions;
  if (!vs || typeof vs !== 'object') return [];
  return qsServerIds(d).filter((sv) => !vs[sv] || vs[sv].present === false);
}
// The Test's line (login.test.* — loginStatusLine's words) and Quick Start's notes about it.
function qsTestHtml(d) {
  const s = d.server;
  if (s.testing) return qsLine('busy', T('login.testing'));
  const r = s.test;
  if (!r || s.testKey !== qsTestKey(d)) return '';
  const okAgent = !!(r.agent && r.agent.reachable), okGame = !!(r.game && r.game.reachable === true);
  let txt, cls = '';
  if (okAgent) { txt = t('login.test.agentOk', { name: r.agent.name || r.host, versions: (r.agent.versionsUp || []).join(', ') || '—' }); cls = 'ok'; }
  else if (okGame) { txt = t('login.test.gameOk', { host: r.host, port: r.port }); cls = 'ok'; }
  else if (r.game && r.game.reachable === false) { txt = t('login.test.down', { host: r.host, port: r.port }); cls = 'bad'; }
  else txt = t('login.test.unknown');
  const notes = [];
  // (its admin logs in to its agent when the setup starts, and the game download waits for that: the note says so)
  if (!okAgent && s.admin) notes.push(T('qs.server.notReachableAdmin'));
  else if (!okAgent && !okGame) notes.push(T('qs.server.notReachable'));
  else if (!okAgent) notes.push(T('qs.server.noAgent'));
  const lacks = okAgent ? qsNotHosted(d, r) : [];
  if (lacks.length) notes.push(T('qs.server.notHosted', { versions: lacks.join(', ') }));
  return qsLine(cls, esc(txt) + (r.source === 'txt' ? ` · ${T('login.portFromTxt', { port: r.port })}` : '')) + notes.map((n) => qsNote(n, 8)).join('');
}
// (b) this PC: the machine check's rows, what stops a server here, and Custom's settings.
function qsWinBlocked() {
  const n = qsNeeds();
  return !S.qs.check.res || (!!n && Array.isArray(n.blocking) && n.blocking.length > 0) || (qsAgentOnPc() && !S.qs.la.res);
}
function qsWindowsHtml(d) {
  const c = S.qs.check, m = qsMachine(), n = qsNeeds();
  if (!c.res || (!m && c.busy)) return qsLine('busy', T('qs.win.checking'));
  const row = (k, v, cls) => `<div class="line qs-check-row"><div><b>${k}</b></div><span class="qs-v${cls ? ' ' + cls : ''}" style="font-weight:800;font-size:12.5px;text-align:right;color:${cls === 'bad' ? 'var(--bad)' : cls === 'ok' ? 'var(--ok)' : 'var(--ink2)'}">${v}</span></div>`;
  const rows = [];
  if (m) {
    const dk = m.docker || {}, la = m.localAgent || {};
    rows.push(row(T('qs.win.row.windows'), T('qs.win.windows', { build: m.windowsBuild || '—' })));
    rows.push(row(T('qs.win.row.virt'), m.virtualization === 'on' ? T('qs.win.virt.on') : m.virtualization === 'off' ? T('qs.win.virt.off') : T('qs.win.virt.unknown'), m.virtualization === 'on' ? 'ok' : m.virtualization === 'off' ? 'bad' : ''));
    rows.push(row(T('qs.win.row.wsl'), m.wsl === 'on' ? T('qs.win.wsl.on', { version: m.wslVersion || '—' }) : m.wsl === 'off' ? T('qs.win.wsl.off') : m.wsl === 'old' ? T('qs.win.wsl.old') : T('qs.win.wsl.unknown'), m.wsl === 'on' ? 'ok' : ''));
    rows.push(row(T('qs.win.row.docker'), dk.state === 'running' ? T('qs.win.docker.running', { version: dk.engine || dk.version || '—' }) : dk.state === 'installed' ? T('qs.win.docker.installed') : T('qs.win.docker.missing'), dk.state === 'running' ? 'ok' : ''));
    rows.push(row(T('qs.win.row.python'), m.python && m.python.version ? T('qs.win.python.ok', { version: m.python.version }) : T('qs.win.python.missing'), m.python ? 'ok' : ''));
    const plan = qsPlanOf(d), need7z = qsNeeds7z(plan).length > 0;
    rows.push(row(T('qs.win.row.extractor'), m.extractor ? T('qs.win.extractor.ok') : need7z ? T('qs.win.extractor.missing') : T('qs.win.extractor.notNeeded'), m.extractor ? 'ok' : ''));
    // (an agent of another build is replaced — unless Custom keeps it as it is)
    rows.push(row(T('qs.win.row.agent'), la.installed ? (qsAgentDiffers() && !(!d.express && d.server.agentKeep) ? T('qs.win.agent.update') : T('qs.win.agent.installed')) : T('qs.win.agent.missing')));
    rows.push(row(T('qs.win.row.memory'), T('qs.win.memory', { total: qsGb(m.ram), cap: qsGb(m.wslCap) })));
  }
  const table = `<div class="sect qs-check" style="margin:16px 0 0;padding:4px 20px">${rows.join('')}</div>`;
  const blocking = n && Array.isArray(n.blocking) ? n.blocking.filter((b) => b && b.text) : [];
  if (blocking.length) return table + qsLine('bad', T('qs.win.blocked', { reasons: blocking.map((b) => b.text).join(' ') }));
  if (qsAgentOnPc() && !S.qs.la.res) return table + qsLine('busy', T('common.checking'));
  if (qsAgentOnPc()) {
    const keep = !d.express && qsAgentDiffers() ? qsSwitch('qsWinOpt', 'agentKeep', d.server.agentKeep, T('qs.win.agentKeep'), '') : '';
    return table + (d.express ? '' : qsVerRows(d, true)) + qsNote(T('qs.win.agentKept'), 14) + keep;
  }
  return table + (d.express ? '' : qsWinCustom(d));
}
function qsWinCustom(d) {
  const s = d.server, m = qsMachine(), lan = (m && m.lanIp) || '';
  const reach = `<h4 style="margin:18px 0 6px">${T('qs.win.reach')}</h4><div class="grid3">
      ${qsCard('qsReach', 'local', s.reach === 'local' || !lan, T('qs.win.reach.local'), T('qs.win.reach.localSub'))}
      ${qsCard('qsReach', 'lan', s.reach === 'lan' && !!lan, T('qs.win.reach.lan'), !lan ? '' : s.firewall ? T('qs.win.reach.lanSub', { ip: lan }) : T('qs.win.reach.lanSubNoFw', { ip: lan }), !lan)}
      ${qsCard('qsReach', 'internet', s.reach === 'internet' && !!lan, T('qs.win.reach.internet'), lan ? T('qs.win.reach.internetSub', { ip: lan }) : '', !lan)}</div>
    ${lan ? '' : qsNote(T('qs.win.reach.noLan'))}`;
  let pub = '';
  if (lan && s.reach === 'internet') {
    const r = s.pubIp.res, services = ((qsTools() && qsTools().publicIpServices) || []).join(', ');
    if (s.pubIp.busy) pub = qsLine('busy', T('qs.lx.publicIp.learning', { services }));
    else if (r && qsIpv4(r.address)) pub = qsLine('ok', T('qs.lx.publicIp.got', { ip: r.address }));
    else pub = (r && r.problem ? qsLine('bad', esc(r.problem)) : '') + qsLabel(T('qs.lx.publicIp.type')) + qsInput('pubTyped', s.pubTyped, { mono: true, ph: '203.0.113.10' });
  }
  const fw = lan && s.reach !== 'local' ? qsSwitch('qsWinOpt', 'firewall', s.firewall, T('qs.win.firewall'), T('qs.win.firewallSub')) : '';
  const port = `${qsLabel(T('qs.server.agentPort'))}${qsInput('agentPort', s.agentPort, { mono: true, ph: '18080' })}${qsHint(T('qs.server.agentPortHint'))}`;
  const downloads = Object.keys(s.vers).some((v) => s.vers[v].on && qsRowFetches(s.vers[v]));
  const keep = qsSwitch('qsWinOpt', 'keepEnv', s.keepEnv, T('deploy.net.keepEnv'), T('deploy.net.keepEnvSub') + (downloads ? ' ' + T('deploy.net.keepEnvFetch') : ''));
  const auto = qsSwitch('qsWinOpt', 'autostart', s.autostart, T('deploy.win.autostart'), T('deploy.win.autostartSub'));
  return qsVerRows(d, true) + reach + pub + fw + port + keep + auto;
}
// Custom's rows of the server versions: on / off, download or the folder of one the player has, the folder, the starting
// progress, the pathfinding.
function qsVerRows(d, win) {
  const s = d.server, ids = (Array.isArray(S.stacks) ? S.stacks : []).map((x) => x.id).filter((v) => s.vers[v]);
  if (!ids.length) return '';
  const rows = ids.map((sv) => {
    const r = s.vers[sv], row = qsStack(sv);
    const head = `<div class="flex between center" style="gap:12px"><b>${T('common.versionN', { id: sv })}</b>
        <button class="toggle${r.on ? ' on' : ''}" data-act="qsSrvTick" data-arg="${esc(sv)}"><span class="knob"></span></button></div>`;
    if (r.noPackage && !r.on) return `<div class="sect qs-ver" style="margin:10px 0 0">${head}${qsHint(T('qs.sum.noPackage', { version: sv }))}</div>`;
    if (!r.on) return `<div class="sect qs-ver" style="margin:10px 0 0">${head}</div>`;
    let body;
    if (r.choice === 'ready') body = qsHint(T('qs.sum.serverReady', { version: sv, dir: r.dir }));
    else if (r.choice === 'keep') body = qsHint(T('deploy.stack.keepHint', { dir: r.dir }));
    else {
      const choice = r.choice === 'have' ? 'have' : 'download';
      const cards = row && row.downloadable
        ? `<div class="grid2" style="margin-top:10px">${qsCard('qsSrvChoice', sv + ':download', choice === 'download', T('deploy.stack.download'), '')}${qsCard('qsSrvChoice', sv + ':have', choice === 'have', T('deploy.stack.have'), '')}</div>` : '';
      const label = choice === 'have' ? T('deploy.stack.haveDir', { version: sv }) : T('deploy.stacks.installInto', { version: sv });
      const dir = `${qsLabel(label)}<div class="flex gap">${qsInput('verDir', qsRowDir(r), { mono: true, v: sv })}${win ? `<button class="btn ghost" style="flex:none" data-act="qsSrvPick" data-arg="${esc(sv)}">${T('common.browse')}</button>` : ''}</div>
        <div data-live="qs-folder:${esc(sv)}">${win ? qsFolderLine(d, sv) : ''}</div>`;
      body = cards + dir;
    }
    // The shipped save only over a stack this run downloads (I57): elsewhere the card is dead and says what it would wipe.
    const dl = qsRowFetches(r);
    const opt = (m) => {
      const sel = (r.progress || 'keep') === m, dead = m === 'default' && !dl;
      const lbl = m === 'default' ? T('confirm.prepare.opt.default') : m === 'keep' ? T('confirm.prepare.opt.keep') : T('confirm.prepare.opt.fixes');
      const sub = m === 'default' ? (dl ? T('confirm.prepare.opt.default.sub', { account: qsPreMade(d, sv) || '—' }) : `<span style="color:var(--bad)">${T('qs.server.defaultOverData')}</span>`)
        : m === 'keep' ? T('confirm.prepare.opt.keep.sub') : T('confirm.prepare.opt.fixes.sub');
      return qsCard('qsProgress', sv + ':' + m, sel && !dead, lbl, sub, dead);
    };
    const pf = ['auto', 'on', 'off'].map((x) => `<button class="lg-chip ${r.pf === x ? 'on' : ''}" data-act="qsPf" data-arg="${esc(sv + ':' + x)}">${x === 'auto' ? T('qs.server.pathfinding.auto') : x === 'on' ? T('qs.server.pathfinding.on') : T('qs.server.pathfinding.off')}</button>`).join('');
    const progress = r.choice === 'ready' ? '' : `${qsLabel(T('qs.acct.template'))}<div class="grid3">${opt('default')}${opt('keep')}${opt('fixes')}</div>`;
    return `<div class="sect qs-ver" style="margin:10px 0 0">${head}${body}${progress}
      ${qsLabel(T('qs.server.pathfinding'))}<div class="flex gap wrap">${pf}</div></div>`;
  });
  return `<div class="qs-vers" style="margin-top:16px">${qsHint(T('qs.win.versionsHint'))}${rows.join('')}</div>`;
}
const qsRowDir = (r) => r.dir || '';
// What this PC's check says of a Custom folder (localagent.folderCheck): why the agent here could not use it, a folder
// with other files, a server there or none, and the drive's free space.
function qsFolderLine(d, sv) {
  const r = d.server.vers[sv], fc = S.qs.folders[sv];
  if (!r || !fc || !fc.res || !sameFolder(fc.dir, r.dir, true)) return '';
  const x = fc.res;
  if (typeof x.problem === 'string' && x.problem.trim()) return qsLine('bad', esc(x.problem));
  if (r.choice === 'have' && x.kind && x.kind !== 'stack' && x.kind !== 'unknown') return qsLine('bad', T('deploy.stack.checkNotStack'));
  if (r.choice !== 'have' && x.kind === 'other') return qsLine('bad', T('deploy.stack.checkOther'));
  if (x.kind === 'stack') return qsLine('ok', T('deploy.stack.checkHasServer'));
  return isBytes(x.free) && x.drive ? qsHint(T('deploy.stack.checkFree', { drive: x.drive, free: sizeGb(x.free) })) : '';
}
// (c) a Linux box: the SSH login, the key's question, the access and the facts, "people outside", Custom's settings.
function qsLinuxHtml(d) {
  const s = d.server, h = s.ssh, m = S.qs.mem, pr = s.probe;
  const auth = `<div class="flex gap wrap" style="margin-top:12px"><button class="lg-chip ${h.auth !== 'key' ? 'on' : ''}" data-act="qsLxAuth" data-arg="password">${T('qs.lx.auth.password')}</button><button class="lg-chip ${h.auth === 'key' ? 'on' : ''}" data-act="qsLxAuth" data-arg="key">${T('qs.lx.auth.key')}</button></div>`;
  const cred = h.auth === 'key'
    ? `${qsLabel(T('deploy.ssh.key'))}<div class="flex gap">${qsInput('lxKey', h.keyPath, { mono: true })}<button class="btn ghost" style="flex:none" data-act="qsLxPickKey">${T('common.browse')}</button></div>
       ${qsLabel(T('deploy.ssh.passphrase'))}${qsInput('lxPhrase', m.phrase, { type: 'password' })}`
    : `${qsLabel(T('deploy.ssh.password'))}${qsInput('lxPassword', m.ssh, { type: 'password', enter: 'qsLxConnect' })}`;
  const login = `<div class="qs-lx" style="margin-top:18px">${qsLabel(T('qs.lx.host'), true)}${qsInput('lxHost', h.host, { mono: true, ph: t('qs.lx.hostPh') })}
    <div class="grid2" style="margin-top:2px"><div>${qsLabel(T('deploy.ssh.port'))}${qsInput('lxPort', h.port, { mono: true, ph: '22' })}</div><div>${qsLabel(T('deploy.ssh.user'))}${qsInput('lxUser', h.user, { mono: true, ph: 'root' })}</div></div>
    ${auth}${cred}
    <div class="flex gap" style="margin-top:14px"><button class="btn" data-act="qsLxConnect" ${pr.busy ? 'disabled' : ''}>${T('qs.lx.connect')}</button></div></div>`;
  return login + `<div data-live="qs-probe">${qsProbeHtml(d)}</div>`;
}
function qsProbeHtml(d) {
  const s = d.server, pr = s.probe, k = pr.hostKey;
  if (pr.busy) return qsLine('busy', T('qs.lx.connecting', { host: s.ssh.host.trim() }));
  let out = pr.error ? qsLine('bad', esc(pr.error)) : '';
  if (pr.phase === 'key' && k) {
    const changed = k.verdict === 'changed' || k.changed;
    const text = changed ? T('qs.lx.key.changed', { type: k.type, fingerprint: k.fingerprint, pinned: k.pinned || '—' }) : T('qs.lx.key.body', { type: k.type, fingerprint: k.fingerprint });
    out += `<div class="sect qs-key" style="margin:14px 0 0${changed ? ';border-color:var(--bad)' : ''}"><h4 style="margin:0 0 8px">${T('qs.lx.key.title')}</h4>
      <p class="pick" style="margin:0;font-size:12.5px;font-weight:600;line-height:1.6;color:${changed ? 'var(--bad)' : 'var(--ink2)'};word-break:break-all">${text}</p>
      <div class="flex gap" style="margin-top:12px"><button class="btn${changed ? ' danger' : ''}" data-act="qsLxTrust">${changed ? T('qs.lx.key.trustChanged') : T('qs.lx.key.trust')}</button></div></div>`;
    return out;
  }
  if (pr.phase !== 'box') return out;
  if (k && k.verdict === 'match') out += qsLine('ok', T('qs.lx.key.known'));
  const user = s.ssh.user.trim() || 'root';
  const access = pr.access === 'root' ? T('qs.lx.access.root') : pr.access === 'sudo' ? T('qs.lx.access.sudo', { user })
    : pr.access === 'sudoPassword' ? T('qs.lx.access.sudoPassword', { user }) : pr.access === 'needsSudoPassword' ? T('qs.lx.access.needsSudoPassword', { user })
      : pr.access === 'sudoRefused' ? T('qs.lx.access.sudoRefused') : T('qs.lx.access.noSudo', { user });
  out += qsLine(qsLxAccessOk(pr) ? 'ok' : 'bad', access);
  if (pr.access === 'needsSudoPassword' || pr.access === 'sudoRefused') {
    out += `${qsLabel(T('deploy.ssh.sudo'))}${qsInput('lxSudo', S.qs.mem.sudo, { type: 'password', enter: 'qsLxConnect' })}${qsHint(T('qs.lx.sudoHint'))}
      <div class="flex gap" style="margin-top:10px"><button class="btn" data-act="qsLxConnect">${T('qs.lx.connect')}</button></div>`;
  }
  const f = pr.facts;
  if (f) {
    const firstFree = Object.keys(f.free || {}).map((x) => f.free[x]).find((x) => x && isBytes(x.avail));
    out += qsHint(T('qs.lx.facts', { os: [f.osId, f.osVersion].filter(Boolean).join(' ') || '—', ram: qsGb(f.memTotal), free: firstFree ? sizeGb(firstFree.avail) : '—', docker: f.hasDocker ? t('qs.lx.facts.dockerYes') : t('qs.lx.facts.dockerNo') }));
    if (f.agentInstalled) out += qsNote(T('qs.lx.agentThere'));
  }
  if (!qsLxAccessOk(pr)) return out;
  out += `<h4 style="margin:18px 0 6px">${T('qs.lx.public')}</h4><div class="grid2">${qsCard('qsLxPublic', 'yes', s.public === 'yes', T('qs.lx.public.yes'), '')}${qsCard('qsLxPublic', 'no', s.public === 'no', T('qs.lx.public.no'), '')}</div>`;
  if (s.public === 'yes' && qsLxAdvertised(d) === null) {
    const services = ((qsTools() && qsTools().publicIpServices) || []).join(', ');
    if (pr.lookup) out += qsLine('busy', T('qs.lx.publicIp.learning', { services }));
    else out += (pr.publicIp && pr.publicIp.problem ? qsLine('bad', esc(pr.publicIp.problem)) : '') + qsLabel(T('qs.lx.publicIp.type')) + qsInput('pubTyped', s.pubTyped, { mono: true, ph: '203.0.113.10' });
  } else if (s.public === 'yes' && pr.publicIp && qsIpv4(pr.publicIp.address)) out += qsLine('ok', T('qs.lx.publicIp.got', { ip: pr.publicIp.address }));
  if (!d.express) {
    const bind = `${qsLabel(T('qs.lx.bind'))}<div class="flex gap wrap center"><button class="lg-chip ${s.bind !== 'fixed' ? 'on' : ''}" data-act="qsLxBind" data-arg="auto">${T('qs.lx.bind.auto')}</button><button class="lg-chip ${s.bind === 'fixed' ? 'on' : ''}" data-act="qsLxBind" data-arg="fixed">${T('qs.lx.bind.fixed')}</button>${s.bind === 'fixed' ? `<div style="flex:1;min-width:180px">${qsInput('bindIp', s.bindIp, { mono: true, ph: '192.0.2.10' })}</div>` : ''}</div>`;
    const port = `${qsLabel(T('qs.server.agentPort'))}${qsInput('agentPort', s.agentPort, { mono: true, ph: '18080' })}${qsHint(T('qs.server.agentPortHint'))}`;
    const keep = qsSwitch('qsWinOpt', 'keepEnv', s.keepEnv, T('deploy.net.keepEnv'), T('deploy.net.keepEnvSub') + ' ' + T('deploy.net.keepEnvSubLinux'));
    out += qsVerRows(d, false) + bind + port + keep;
  }
  return out;
}
const qsLxAccessOk = (pr) => pr.phase === 'box' && (pr.access === 'root' || pr.access === 'sudo' || pr.access === 'sudoPassword');

// ── Account ──
function qsAccountPage() {
  const d = S.qs.draft, a = d.account, ids = qsAcctIds(d);
  const needPw = ids.some((sv) => qsAcctOn(d, sv) && qsVerifies(d, sv));
  const name = `${qsLabel(T('qs.acct.name'), true)}${qsInput('acctName', a.name, { mono: true, max: 20 })}${qsHint(T('qs.acct.nameHint') + ' ' + esc(qsNameRules(d)))}`;
  const pw = needPw ? `${qsLabel(T('qs.acct.password'))}${qsInput('acctPassword', S.qs.mem.acct, { type: 'password' })}${qsHint(T('qs.acct.passwordHint'))}` : '';
  const per = ids.map((sv) => {
    const on = qsAcctOn(d, sv), notes = qsAcctNotes(d, sv);
    const skip = d.express ? '' : qsSwitch('qsAcctSkip', sv, !on, T('qs.acct.skip'), '');
    const tpl = qsTplOf(d, sv);
    // (each starting progress with its line, as the player card words it: the template's own, else the admin's choice)
    const cards = on && !notes.blocked ? `${qsLabel(T('qs.acct.template'))}<div class="grid3">${qsTplChoices(d, sv).map((x) => {
      const sub = 'account.tpl.' + x.id + '.sub';
      return qsCard('qsAcctTpl', sv + ':' + x.id, tpl === x.id, esc(x.label || x.id), T(I18N.has(sub) ? sub : 'account.tpl.other.sub'));
    }).join('')}</div>` : '';
    return `<div class="sect qs-acct" style="margin:12px 0 0"><b style="font-size:14px">${T('common.versionN', { id: sv })}</b>${notes.html}${cards}${skip}</div>`;
  }).join('');
  return `<div class="panel qs-panel"><h3>${T('qs.acct.title')}</h3>${name}${pw}${per}</div>${qsFoot(T('qs.next'), 'qsNext')}`;
}
// The notes of a version on the Account page; blocked = no account can be created there now.
function qsAcctNotes(d, sv) {
  const out = [];
  let blocked = false;
  if (d.server.kind === 'existing') {
    const snap = qsSnap(d), here = qsSameHost(d.server.address, S.serverAddr.host);
    const mine = here ? acctLive(sv) : [];
    if (mine.length) { out.push(T('qs.acct.mine', { name: mine[mine.length - 1].name })); blocked = true; }
    else if (qsAcctNoAgent(d)) { out.push(T('qs.acct.noAgent')); blocked = true; }
    else if (!d.server.admin && snap) {
      const su = snap.signup;
      if (!su || !su.enabled) { out.push(T('qs.acct.signupOff')); blocked = true; }
      else if (!acctUp(sv, snap)) { out.push(T('qs.acct.notUp', { version: sv })); blocked = true; }
      else { const lim = acctLimit(sv, snap); if (lim !== null && lim <= 0) { out.push(T('qs.acct.limit', { max: lim, version: sv })); blocked = true; } }
    }
  }
  const pm = qsPreMade(d, sv);
  if (pm && d.server.kind !== 'existing') {
    const r = d.server.vers[sv];
    if (r && qsProgressOf(d, r) === 'default') out.push(T('qs.acct.preMade', { version: sv, name: pm, password: t('qs.done.passwordAny') }));
  }
  return { blocked, html: out.map((x) => qsHint(x)).join('') };
}

// ── Summary ──
function qsSummaryPage() {
  const d = S.qs.draft, n = qsNeeds(), m = qsMachine(), tools = qsTools() || {}, s = d.server, kind = s.kind;
  const sect = (title, page, lines) => {
    const ls = lines.filter(Boolean);
    return ls.length ? `<div class="sect qs-sum-sect" style="margin:0 0 12px;padding:16px 20px"><div class="flex between center" style="margin-bottom:6px"><h4 style="margin:0">${title}</h4>
      <button class="lg-link" style="margin:0" data-act="qsGoto" data-arg="${page}">${T('qs.sum.change')}</button></div>
      ${ls.map((l) => `<div class="qs-sum-line" style="font-size:12.5px;font-weight:600;color:var(--ink2);line-height:1.6;margin:3px 0">${l}</div>`).join('')}</div>` : '';
  };
  const bad = (html) => `<span style="color:var(--bad)">${html}</span>`;
  // the game
  const game = qsGameIds(d).map((id) => {
    const from = qsGameFrom(d, id);
    if (from === 'installed') return T('qs.sum.gameKept', { version: id });
    if (from === 'download') return T('qs.sum.gameDownload', { version: id, size: sizeGb(qsDownloadBytes(d, id)), voices: qsVoicePacks(d, id).map((p) => p.lang).join(', '), dir: qsRootOf(d) });
    const cp = S.qs.copy[id];
    if (!cp || cp.key !== qsInspectKey(d, id) || cp.phase === 'checking') return T('qs.sum.copyChecking', { version: id });
    if (cp.phase === 'done' && cp.v && cp.v.status === 'ready') return T('qs.sum.gameImport', { version: id, dir: cp.v.gameDir || d.game.path[id] });
    return bad(T('qs.sum.copyRefused', { version: id, message: (cp.v && cp.v.message) || cp.error || '' }));
  });
  const plan = qsPlanOf(d);
  if (qsGameIds(d).some((id) => (versionById(id) || {}).hasEnhancements)) game.push(T('qs.sum.enhancements', { state: plan.game.enhancements ? t('qs.sum.on') : t('qs.sum.off') }));
  // Fiddler and its certificate as this run sets them up: the certificate's question only while there is one to ask.
  const installsGame = plan.game.versions.some((g) => g.from !== 'installed');
  const certAsk = installsGame && plan.game.installCert && !S.fiddler.certTrusted;
  if (certAsk) game.push(T('qs.sum.fiddler'));
  else if (installsGame && !S.fiddler.installed) game.push(T('qs.sum.fiddlerNoCert'));
  // the server
  const server = [];
  if (kind === 'existing') server.push(plan.server.admin ? T('qs.sum.connectAdmin', { address: plan.server.address }) : T('qs.sum.connect', { address: plan.server.address }));
  else if (kind === 'windows') {
    const ins = plan.server.installs, here = qsAgentOnPc();
    if (ins.python) server.push(T('qs.sum.python', { version: (tools.python && tools.python.version) || '', size: qsSize(tools.python && tools.python.size) }));
    if (ins.sevenZip) server.push(T('qs.sum.sevenZip', { size: qsSize(tools.sevenZip && tools.sevenZip.size) }));
    if (ins.wsl) server.push(T('qs.sum.wsl'));
    if (ins.wslUpdate) server.push(T('qs.sum.wslUpdate'));
    if (ins.docker) server.push(T('qs.sum.docker', { size: qsSize(tools.docker && tools.docker.size) }));
    else if (ins.dockerStart) server.push(T('qs.sum.dockerStart'));
    // (the agent here used as it is, or replaced by the shipped one; a new one started with Windows only when it is to)
    if (here) server.push(ins.agentUpdate ? T('qs.sum.agentUpdate') : T('qs.sum.agentUse'));
    else server.push(plan.server.autostart ? T('qs.sum.agentInstall') : T('qs.sum.agentInstallNoAuto'));
  } else if (kind === 'linux') {
    server.push(T('qs.sum.agentInstallLinux', { host: plan.server.ssh.host }));
    if (s.probe.facts && s.probe.facts.agentInstalled) server.push(T('qs.sum.agentReinstall', { host: plan.server.ssh.host }));
  }
  if (kind === 'windows' || kind === 'linux') {
    // (a package that comes as a .zip too is taken as one on this PC when no 7z tool will be there — the agent's rule)
    const noTool = kind === 'windows' && !(m && m.extractor) && !plan.server.installs.sevenZip;
    Object.keys(s.vers).forEach((sv) => {
      const r = s.vers[sv], row = qsStack(sv), x = plan.server.versions.find((y) => y.id === sv);
      if (r.noPackage && !r.on) { server.push(T('qs.sum.noPackage', { version: sv })); return; }
      if (!x) return;
      const progress = x.progress === 'default' ? t('confirm.prepare.opt.default') : x.progress === 'fixes' ? t('confirm.prepare.opt.fixes') : t('confirm.prepare.opt.keep');
      const arc = row && noTool && Number(row.zipSize) > 0 ? row.zipSize : row && row.size;
      if (x.choice === 'ready') server.push(T('qs.sum.serverReady', { version: sv, dir: x.dir }));
      else if (qsRowFetches(r)) server.push(T('qs.sum.serverDownload', { version: sv, size: sizeGb(arc), unpacked: sizeGb(row && row.extractedSize), dir: x.dir, progress }));
      else server.push(T('qs.sum.serverHave', { version: sv, dir: x.dir, progress }));
      if (kind === 'linux' && r.stored && !sameFolder(r.stored, r.dir, false)) server.push(T('core.deploy.dirKept', { version: sv, dir: r.stored, given: r.dir }));
      if (x.pathfinding === false || (x.pathfinding === null && qsVerdict(sv) === 'noPathfinding')) server.push(T('qs.sum.pathfindingOff', { version: sv }));
    });
    if (plan.server.versions.length > 1) server.push(T('qs.sum.oneRuns', { version: plan.server.versions[plan.server.versions.length - 1].id }));
  }
  // the account
  const account = qsAcctIds(d).map((sv) => {
    const notes = qsAcctNotes(d, sv);
    if (!qsAcctOn(d, sv)) return T('qs.sum.accountNone', { version: sv, reason: t('qs.acct.skipReason') });
    if (kind === 'existing') {
      const mine = qsSameHost(s.address, S.serverAddr.host) ? acctLive(sv) : [];
      if (mine.length) return T('qs.sum.accountMine', { version: sv, name: mine[mine.length - 1].name });
      if (notes.blocked) return T('qs.sum.accountNone', { version: sv, reason: qsAcctReasonText(d, sv) });
    }
    return T('qs.sum.accountCreate', { name: d.account.name.trim(), version: sv, progress: qsTplLabel(d, sv, qsTplOf(d, sv)) });
  });
  // the disks (a Linux box's own, named as the server's)
  const disk = [], boxHost = plan.server.ssh.host;
  ((n && n.drives) || []).forEach((x) => {
    if (!x) return;
    const drive = x.drive, box = kind === 'linux' && !!x.box;
    if (x.free == null) disk.push(box ? T('qs.sum.diskUnknownBox', { host: boxHost, drive }) : T('qs.sum.diskUnknown', { drive }));
    else if (x.ok === false) disk.push(bad(box ? T('qs.sum.diskShortBox', { host: boxHost, drive, need: sizeGb(x.need), free: sizeGb(x.free) }) : T('qs.sum.diskShort', { drive, need: sizeGb(x.need), free: sizeGb(x.free) })));
    else disk.push(box ? T('qs.sum.diskRowBox', { host: boxHost, drive, need: sizeGb(x.need), free: sizeGb(x.free) }) : T('qs.sum.diskRow', { drive, need: sizeGb(x.need), free: sizeGb(x.free) }));
  });
  if (n && n.dockerImagesUnknown && kind === 'windows') disk.push(T('qs.sum.dockerImages'));
  // the memory: one line for the versions that fit (their largest need, the cap named once), one for each that does not
  // (a Linux box's memory is the server's, not Docker's of this PC)
  const mem = [];
  const ram = (n && n.ram) || {}, box = kind === 'linux';
  const ramVers = Object.keys(ram).filter((sv) => !!ram[sv]);
  const fit = ramVers.filter((sv) => ram[sv].verdict === 'fits' || ram[sv].verdict === 'noPathfinding');
  if (fit.length) {
    const need = Math.max(...fit.map((sv) => { const r = ram[sv]; return r.verdict === 'fits' ? Math.max(Number(r.needPathfinding) || 0, Number(r.need) || 0) : Number(r.need) || 0; }));
    const cap = Math.max(...fit.map((sv) => Number(ram[sv].cap) || 0));
    mem.push(box ? T('qs.sum.memoryOkBox', { host: boxHost, cap: qsGb(cap), need: qsGb(need) }) : T('qs.sum.memoryOk', { cap: qsGb(cap), need: qsGb(need) }));
  }
  ramVers.forEach((sv) => {
    const r = ram[sv];
    if (r.verdict === 'unknown') mem.push(T('qs.sum.memoryUnknown', { version: sv }));
    else if (r.verdict !== 'short') return;
    else if (box) mem.push(bad(T('qs.sum.memoryShortBox', { version: sv, host: boxHost, need: qsGb(r.need), cap: qsGb(r.cap) })));
    // (the raise chosen below makes room: said without the warning)
    else if (qsRaiseChosen(d) && Number(r.raiseGb) > 0) mem.push(T('qs.sum.memoryShortRaise', { version: sv, need: qsGb(r.need), cap: qsGb(r.cap) }));
    else mem.push(bad(T('qs.sum.memoryShort', { version: sv, need: qsGb(r.need), cap: qsGb(r.cap) })));
  });
  if (kind === 'windows' && qsRaiseGb() > 0) {
    const raise = d.sum.memory !== 'leave';
    mem.push(`<div class="grid2" style="margin-top:6px">${qsCard('qsMemory', 'raise', raise, T('qs.sum.memoryRaise', { gb: qsRaiseGb(), path: QS_WSLCONFIG }), '')}${qsCard('qsMemory', 'leave', !raise, T('qs.sum.memoryLeave'), '')}</div>`);
  }
  if (qsStaysShort(d)) mem.push(qsSwitch('qsGoOn', '', d.sum.goOn, T('qs.sum.memoryContinue'), ''));
  if (kind === 'windows' && m && m.ram && (Number(m.ram) - (qsRaiseChosen(d) ? qsRaiseGb() * QS_GIB : Number(m.wslCap) || 0)) < 8 * QS_GIB && qsGameIds(d).length) mem.push(T('qs.sum.memoryGame', { total: qsGb(m.ram) }));
  // the network
  const net = [];
  if (kind === 'windows' && !qsAgentOnPc()) {
    const reach = plan.server.reach;
    if (reach === 'local') net.push(T('qs.sum.reachLocal'));
    else if (reach === 'lan') net.push(T('qs.sum.reachLan', { ip: plan.server.address }));
    else net.push(T('qs.sum.reachPublic', { ip: plan.server.advertisedIp || '—', agentPort: plan.server.agentPort, lanIp: plan.server.address }));
  } else if (kind === 'linux') {
    const typed = hostOf(s.ssh.host);
    if (s.public !== 'yes') net.push(T('qs.sum.reachLanOnly', { ip: typed }));
    else if (qsLxAdvertised(d) === '') net.push(T('qs.sum.reachVps', { ip: qsLxPublicOf(d), agentPort: plan.server.agentPort }));
    else {
      const f = s.probe.facts, lan = (f && (f.routeSource || (Array.isArray(f.addresses) && f.addresses[0]))) || typed;
      net.push(T('qs.sum.reachPublic', { ip: qsLxAdvertised(d) || '—', agentPort: plan.server.agentPort, lanIp: lan }));
    }
  }
  // what Windows asks: the permission prompts of this PC's server, the certificate's question, and Fiddler's own prompt
  // when it cannot install for the user alone (its fallback)
  const perm = [];
  if (kind === 'windows') {
    if (plan.server.installs.wsl) perm.push(T('qs.sum.permWsl'));
    if (plan.server.installs.wslUpdate) perm.push(T('qs.sum.permWslUpdate'));
    if (plan.server.firewall) perm.push(T('qs.sum.permFirewall'));
  }
  if (certAsk) perm.push(T('qs.sum.permCert'));
  if (installsGame && !S.fiddler.installed) perm.push(T('qs.sum.permFiddler'));
  if (!perm.length) perm.push(T('qs.sum.permNone'));
  // the licence of Docker Desktop, when this run installs it
  const lic = kind === 'windows' && plan.server.installs.docker
    ? `<div class="sect qs-licence" style="margin:0 0 12px;padding:14px 20px">${qsSwitch('qsLicence', '', d.sum.licence, T('qs.sum.dockerLicence'), T('qs.sum.dockerLicenceNote'))}
      <button class="lg-link" data-act="qsLicenceRead">${T('qs.sum.dockerLicenceLink')}</button></div>` : '';
  const block = qsStartBlock(d);
  return `<div class="panel qs-panel qs-sum"><h3>${T('qs.sum.title')}</h3><div style="margin-top:12px">
      ${sect(T('qs.sum.game'), 'game', game)}${sect(T('qs.sum.server'), 'server', server)}${sect(T('qs.sum.account'), 'account', account)}
      ${sect(T('qs.sum.disk'), 'game', disk)}${sect(T('qs.sum.memory'), 'server', mem)}${sect(T('qs.sum.network'), 'server', net)}
      ${sect(T('qs.sum.permissions'), 'server', perm)}${lic}
      ${block ? `<div data-live="qs-blocked">${qsLine(S.qs.check.busy ? 'busy' : 'bad', T('qs.sum.blocked', { reason: block }))}</div>` : ''}</div></div>
    ${qsFoot(T('qs.start'), 'qsStart', !!block)}`;
}
// Why an account is not created on a version of an existing server (its note).
function qsAcctReasonText(d, sv) {
  const snap = qsSnap(d), su = snap && snap.signup;
  if (!snap) return t('qs.acct.noAgentReason');
  if (!su || !su.enabled) return t('qs.acct.signupOff');
  if (!acctUp(sv, snap)) return t('qs.acct.notUp', { version: sv });
  return t('qs.acct.limit', { max: acctLimit(sv, snap), version: sv });
}
// What keeps Start dead, as plain text; '' = nothing.
function qsStartBlock(d) {
  const c = S.qs.check, n = qsNeeds(), kind = d.server.kind;
  if (c.busy || !c.res) return t('qs.win.checking');
  if (kind === 'windows' && n && Array.isArray(n.blocking) && n.blocking.length) return t('qs.win.blocked', { reasons: n.blocking.map((b) => b && b.text).filter(Boolean).join(' ') });
  const short = n && Array.isArray(n.drives) ? n.drives.find((x) => x && x.ok === false) : null;
  if (short && kind === 'linux' && short.box) return t('qs.sum.diskShortBox', { host: d.server.ssh.host.trim(), drive: short.drive, need: sizeGb(short.need), free: sizeGb(short.free) });
  if (short) return t('qs.sum.diskShort', { drive: short.drive, need: sizeGb(short.need), free: sizeGb(short.free) });
  for (const id of qsGameIds(d)) {
    if (qsGameFrom(d, id) !== 'local') continue;
    const cp = S.qs.copy[id];
    if (!cp || cp.key !== qsInspectKey(d, id) || cp.phase === 'checking') return t('qs.sum.copyChecking', { version: id });
    if (!(cp.phase === 'done' && cp.v && cp.v.status === 'ready')) return t('qs.sum.copyRefused', { version: id, message: (cp.v && cp.v.message) || cp.error || '' });
  }
  if (kind === 'windows' && qsInstalls().docker && !d.sum.licence) return t('qs.sum.switchFirst', { what: t('qs.sum.dockerLicence') });
  if (qsStaysShort(d) && !d.sum.goOn) return t('qs.sum.switchFirst', { what: t('qs.sum.memoryContinue') });
  return '';
}
// A size in the summary's words: MB below a GB (Python's package, 7-Zip's extractor), the server pages' GB above.
function qsSize(bytes) {
  const v = Math.max(0, Number(bytes) || 0);
  if (!(v > 0 && v < 1e9)) return sizeGb(v);
  return t('qs.sum.sizeMb', { mb: v < 1e7 ? (v / 1e6).toFixed(1) : String(Math.round(v / 1e6)) });
}

// ── the progress page ──
const qsShown = (state) => (state === 'running' ? 'working' : state === 'pending' ? 'waiting' : state);
function qsStateText(s) {
  switch (s) {
    case 'working': return T('qs.state.working');
    case 'done': return T('qs.state.done');
    case 'needs': return T('qs.state.needs');
    case 'failed': return T('qs.state.failed');
    case 'paused': return T('qs.state.paused');
    case 'skipped': return T('qs.state.skipped');
    default: return T('qs.state.waiting');
  }
}
function qsStepLabel(st) {
  const v = st.v;
  switch (st.kind) {
    case 'host': return T('qs.step.waitHost');
    case 'get': return st.local ? T('qs.step.import', { version: v }) : T('qs.step.get', { version: v });
    case 'unpack': return T('qs.step.unpack', { version: v });
    case 'patch': return T('qs.step.patch', { version: v });
    case 'fiddler': return T('qs.step.fiddler');
    case 'cert': return T('qs.step.cert');
    case 'connect': return T('qs.step.connect', { address: S.qs.plan ? S.qs.plan.server.address : '' });
    case 'python': return T('qs.step.python');
    case 'sevenzip': return T('qs.step.sevenZip');
    case 'agentUpdate': return T('qs.step.agentUpdate');
    case 'agent': return T('qs.step.agent');
    case 'agent7z': return T('qs.step.sevenZipAgent');
    case 'firewall': return T('qs.step.firewall');
    case 'wsl': return T('qs.step.wsl');
    case 'restart': return T('qs.step.restart');
    case 'wslMemory': return T('qs.step.wslMemory');
    case 'docker': return T('qs.step.docker');
    case 'package': return T('qs.step.package', { version: v });
    case 'agentRestart': return T('qs.step.agentRestart');
    case 'prepare': return T('qs.step.prepare', { version: v });
    case 'pathfinding': return T('qs.step.pathfinding', { version: v });
    case 'hotpatch': return T('qs.step.hotpatch');
    case 'start': return T('qs.step.start', { version: v });
    case 'account': return T('qs.step.account', { version: v });
    case 'ready': return T('qs.step.ready');
    case 'reach': return T('qs.step.reach');
    default: return '';
  }
}
const QS_MARK = { done: '✓', skipped: '–', failed: '!', needs: '?', paused: 'Ⅱ' };
const qsMarkColor = (s) => (s === 'done' ? 'var(--ok)' : s === 'failed' ? 'var(--bad)' : s === 'needs' ? 'var(--warn)' : s === 'working' ? 'var(--goldD)' : 'var(--muted)');
// A thread's sum: its state, its percentage and the line of the step under way.
function qsThreadSum(th) {
  const rt = S.qs.rt, list = rt.steps.filter((s) => s.thread === th && s.row);
  const states = list.map((s) => qsRec(s.id).state);
  const total = Math.max(1, list.length);
  let frac = 0;
  const run = list.find((s) => qsRec(s.id).state === 'running');
  if (run && (run.kind === 'get' || run.kind === 'unpack') && S.qs.inst && S.qs.inst.versionId === run.v) frac = Math.max(0, Math.min(1, Number(S.qs.inst.fraction) || 0));
  const doneN = states.filter(qsOkState).length;
  const pct = Math.round(Math.min(1, (doneN + frac) / total) * 100);
  let state = 'waiting';
  if (states.some((x) => x === 'running')) state = 'working';
  else if (states.some((x) => x === 'needs')) state = 'needs';
  else if (states.some((x) => x === 'failed')) state = 'failed';
  else if (states.length && states.every(qsOkState)) state = 'done';
  else if (states.some((x) => x === 'paused') || (rt.stopped && !qsThreadEnded(th))) state = 'paused';
  const cur = run || list.find((s) => qsRec(s.id).state === 'needs') || list.find((s) => qsRec(s.id).state === 'failed');
  const line = cur ? `${qsStepLabel(cur)}${rt.detail[cur.id] ? ' — ' + esc(rt.detail[cur.id]) : ''}` : '';
  return { state, pct, line };
}
const qsListOpen = (th) => (S.qs.open[th] == null ? qsThreadSum(th).state === 'working' || qsThreadSum(th).state === 'needs' : !!S.qs.open[th]);
function qsRunPage() {
  const rt = S.qs.rt;
  if (!S.qs.plan || !rt) return '';
  const threads = ['game', 'server'].filter((th) => rt.steps.some((s) => s.thread === th && s.row));
  const ended = qsEnded();
  // (no Stop while Windows is being asked to restart: the plan is saved for the restart then)
  const foot = `<div class="wizfoot qs-foot">${ended ? '<span></span>' : `<button class="btn ghost" data-act="qsStopAsk" ${rt.stopped || rt.restarting ? 'disabled' : ''}>${T('qs.run.stop')}</button>`}
    ${ended ? `<button class="btn glow" data-act="qsContinue">${T('common.continue')}</button>` : '<span></span>'}</div>`;
  // (side by side; one under the other where the window is too narrow for two)
  return `<div class="qs-run"><div class="qs-states" style="display:flex;flex-wrap:wrap;gap:16px;align-items:flex-start">
    ${threads.map(qsThreadHtml).join('')}</div>${foot}</div>`;
}
function qsThreadHtml(th) {
  const rt = S.qs.rt, sum = qsThreadSum(th), open = qsListOpen(th);
  const name = th === 'game' ? T('qs.run.game') : T('qs.run.server');
  const icon = th === 'game' ? I.download : I.stack;
  const badge = `${qsStateText(sum.state)}${sum.state === 'working' ? ` · ${sum.pct}%` : ''}`;
  const rows = rt.steps.filter((s) => s.thread === th && s.row).map(qsSubHtml).join('');
  const lines = rt.lines[th] || [];
  const log = S.qs.logOpen[th] ? `<div class="console qs-log" style="margin-top:10px"><div class="log" data-live="qs-log:${th}" style="max-height:220px">${lines.map((l) => `<div>${esc(l)}</div>`).join('')}</div></div>` : '';
  return `<div class="qs-col" style="flex:1 1 340px;min-width:0">
    <button class="qs-state is-${sum.state}" data-act="qsToggle" data-arg="${th}" aria-expanded="${open ? 'true' : 'false'}" style="width:100%;display:flex;flex-direction:column;align-items:stretch;gap:8px;padding:16px 18px;border-radius:14px;text-align:left;border:1px solid color-mix(in srgb, var(--goldD) 34%, transparent);background:linear-gradient(180deg,var(--panel),var(--cream));box-shadow:0 10px 26px rgba(40,30,10,.1);color:var(--ink)">
      <span class="flex between center" style="gap:10px"><span class="flex center" style="gap:10px;font-family:'Cinzel',serif;font-weight:700;font-size:17px"><span style="display:flex;color:var(--goldD)">${icon}</span>${name}</span>
        <span class="qs-badge" data-live="qs-badge:${th}" style="font-size:11.5px;font-weight:800;padding:5px 11px;border-radius:20px;color:${qsMarkColor(sum.state)};background:color-mix(in srgb, ${qsMarkColor(sum.state)} 14%, transparent)">${badge}</span></span>
      <span class="qs-cur" data-live="qs-line:${th}" style="font-size:12px;font-weight:700;color:var(--inkSoft);min-height:17px;overflow-wrap:anywhere">${sum.line}</span>
      <span class="qs-bar" style="display:block;height:4px;border-radius:3px;background:color-mix(in srgb, var(--goldD) 18%, transparent);overflow:hidden"><i data-live="qs-bar:${th}" style="display:block;height:100%;width:${sum.pct}%;background:linear-gradient(90deg,var(--gold2),var(--gold));transition:width .4s ease"></i></span>
    </button>
    <div class="qs-list${open ? ' open' : ''}" data-qs-list="${th}" style="overflow:hidden;transition:max-height .25s ease, opacity .25s ease, visibility .25s;${open ? 'opacity:1' : 'max-height:0;opacity:0;visibility:hidden'}">
      <div class="sect" style="margin:10px 0 0;padding:4px 18px 12px">${rows}
        <div style="margin-top:10px"><button class="lg-link" style="margin:0" data-act="qsLog" data-arg="${th}">${T('qs.run.details')}</button></div>${log}</div>
    </div></div>`;
}
function qsSubHtml(st) {
  const rt = S.qs.rt, rec = qsRec(st.id), shown = qsShown(rec.state);
  const mark = shown === 'working' ? spinnerSmall() : `<b style="color:${qsMarkColor(shown)}">${QS_MARK[shown] || '·'}</b>`;
  return `<div class="qs-sub is-${shown}" data-qs-step="${esc(st.id)}" style="display:flex;gap:10px;padding:10px 0;border-bottom:1px solid color-mix(in srgb, var(--goldD) 16%, transparent)">
    <span class="qs-mark" style="flex:none;width:18px;display:flex;justify-content:center;padding-top:1px">${mark}</span>
    <div style="flex:1;min-width:0"><div class="flex between" style="gap:8px"><b style="font-size:12.5px;color:var(--ink)">${qsStepLabel(st)}</b><small style="font-weight:800;font-size:10.5px;color:${qsMarkColor(shown)};white-space:nowrap">${qsStateText(shown)}</small></div>
      <small data-live="qs-d:${esc(st.id)}" style="display:block;color:var(--muted);font-weight:600;font-size:11.5px;line-height:1.5;overflow-wrap:anywhere">${esc(rt.detail[st.id] || '')}</small>
      ${rec.note ? `<div class="qs-subnote" style="margin-top:4px;color:${shown === 'failed' ? 'var(--bad)' : 'var(--inkSoft)'};font-size:11.5px;font-weight:700;line-height:1.5;overflow-wrap:anywhere">${esc(rec.note)}</div>` : ''}
      ${qsInlineHtml(st, rec)}${qsActsHtml(st, rec)}</div></div>`;
}
// A field a step that needs the user asks for (a token, an SSH password, another account name, the account password).
function qsInlineHtml(st, rec) {
  if (rec.state !== 'needs') return '';
  const rt = S.qs.rt, p = S.qs.plan;
  const field = (name, type) => `<div class="flex gap" style="margin-top:8px">${qsInput(name, '', { type: type || 'text', mono: true, enter: 'qsStepGo', arg: st.id })}
      <button class="btn" style="flex:none;padding:8px 14px;font-size:12px" data-act="qsStepGo" data-arg="${esc(st.id)}">${st.kind === 'account' ? T('qs.act.createAccount') : T('qs.lx.connect')}</button></div>`;
  if (st.kind === 'connect') return field('stepToken', 'password');
  if (st.kind === 'agent' && p.server.kind === 'linux' && rt.needKey) {
    const changed = rt.needKey.verdict === 'changed' || rt.needKey.changed === true;
    return `<div class="flex gap" style="margin-top:8px"><button class="btn${changed ? ' danger' : ''}" style="padding:8px 14px;font-size:12px" data-act="qsStepGo" data-arg="${esc(st.id)}">${changed ? T('qs.lx.key.trustChanged') : T('qs.lx.key.trust')}</button></div>`;
  }
  if (st.kind === 'agent' && p.server.kind === 'linux') {
    // the login typed again, and sudo's password beside it when this page does not hold one (empty when sudo asks none)
    const sudo = S.qs.mem.sudo ? '' : `<div style="margin-top:8px">${qsInput('stepSudo', '', { type: 'password', mono: true, ph: t('qs.run.sudoPh') })}</div>`;
    return sudo + field(p.server.ssh.keyPath ? 'stepPhrase' : 'stepSsh', 'password');
  }
  if (st.kind === 'account' && rt.acctWhy[st.v] === 'status') return '';
  if (st.kind === 'account' && rt.needPw && rt.needPw[st.v]) return field('stepAcctPw', 'password');
  if (st.kind === 'account') return field('stepAcctName', 'text');
  return '';
}
function qsActsHtml(st, rec) {
  const b = (act, label, cls) => `<button class="btn ${cls || 'ghost'}" style="padding:6px 12px;font-size:11.5px" data-act="${act}" data-arg="${esc(st.id)}">${label}</button>`;
  const acts = [];
  const s = rec.state, inst = S.qs.inst;
  if (s === 'running' && st.kind === 'get' && !st.local && inst && inst.versionId === st.v && !inst.stop && (!inst.phase || inst.phase === 'DownloadClient' || inst.phase === 'DownloadAudio')) acts.push(b('qsPause', T('qs.act.pause')));
  if (s === 'paused') acts.push(b('qsResumeStep', T('qs.act.resume'), ''));
  if (s === 'failed') acts.push(b('qsRetry', T('common.retry'), ''));
  if (s === 'needs' && st.kind === 'cert') acts.push(b('qsTrustCert', T('qs.act.trustCert'), ''));
  if (s === 'needs' && st.kind === 'restart' && !S.qs.rt.restarting) {
    acts.push(b('qsRestartNow', T('qs.act.restartNow'), ''));
    if (!S.qs.rt.restartLater) acts.push(b('qsRestartLater', T('qs.act.restartLater')));
  }
  if (s === 'needs' && (st.kind === 'reach' || (st.kind === 'ready' && S.qs.plan.server.kind === 'existing') || (st.kind === 'account' && S.qs.rt.acctWhy[st.v] === 'status'))) acts.push(b('qsCheckAgain', T('qs.act.checkAgain'), ''));
  if (s === 'needs' && st.kind === 'ready') acts.push(b('qsStepGo', T('common.continue'), ''));
  // (skipping the replacement of the agent here = going on with the agent as it is installed)
  if ((s === 'failed' || s === 'needs') && ['account', 'reach', 'cert', 'firewall', 'pathfinding', 'ready', 'fiddler', 'agent7z', 'agentUpdate'].includes(st.kind)) acts.push(b('qsSkip', T('qs.act.skip')));
  return acts.length ? `<div class="flex gap wrap qs-acts" style="margin-top:8px">${acts.join('')}</div>` : '';
}

// ── the Done page ──
// What it says is what the run reached, read from the facts: the server line only once Relic talks to that server in
// the mode the plan wants, the logins only there, PLAY only once every game version is in the Library, the saved entry
// only for a server reached; the steps that did not finish listed under them.
function qsDonePage() {
  const p = S.qs.plan, rt = S.qs.rt;
  if (!p || !rt) return '';
  const s = p.server, lines = [], connected = qsDoneConnected();
  const where = s.kind === 'windows' ? t('qs.done.whereThisPc') : s.ssh.host;
  if (connected && s.kind === 'existing') lines.push(s.admin ? T('qs.done.admin', { where: s.address }) : T('qs.done.player', { address: s.address }));
  else if (connected) lines.push(qsLastVersion() && qsRec(`s.${qsLastVersion()}.start`).state === 'done' ? T('qs.done.admin', { where }) : T('qs.done.adminPartial', { where }));
  if (connected) qsDoneLogins().forEach((x) => lines.push(T('qs.done.login', x)));
  if (connected && qsDoneGames()) lines.push(T('qs.done.play'));
  const m = qsMachine();
  if (s.kind === 'windows' && m && m.docker && m.docker.autoStart === false) lines.push(T('qs.done.dockerStart'));
  if (rt.reach) lines.push(esc(qsReachText(rt.reach, qsReachAddress(), s.agentPort || 18080)));
  const open = rt.steps.filter((st) => st.row && qsUnfinished(st));
  const said = (st) => { const x = qsRec(st.id).state; return x === 'pending' ? T('qs.state.notStarted') : qsStateText(qsShown(x)); };
  const partial = open.length ? `<div class="sect" style="margin:14px 0 0;padding:12px 18px">${open.map((st) => `<div class="flex between" style="gap:10px;padding:5px 0;font-size:12.5px;font-weight:700;color:var(--ink2)"><span>${qsStepLabel(st)}</span><span style="color:${qsMarkColor(qsShown(qsRec(st.id).state))}">${said(st)}</span></div>`).join('')}</div>${qsNote(T('qs.done.partial'))}` : '';
  if (connected) lines.push(T('qs.done.saved', { label: qsSavedLabel() }));
  return `<div class="panel qs-panel qs-done"><div style="display:flex;flex-direction:column;gap:8px">${lines.map((l) => `<p style="margin:0;font-size:13px;font-weight:600;color:var(--ink2);line-height:1.6">${l}</p>`).join('')}</div>${partial}</div>
    <div class="wizfoot qs-foot"><span></span><button class="btn glow" data-act="qsFinish" ${S.qs.busy ? 'disabled' : ''}>${T('common.continue')}</button></div>`;
}
// The logins the Done page names: per server version, the account this run created (or the launcher's own there), else
// the server's own word on its pre-made account — never the catalogue's (I15). The password: any, unless the server
// verifies it — the one typed, or one it generated, shown once.
function qsDoneLogins() {
  const p = S.qs.plan, out = [];
  // the versions of the server: an existing server's — the ones of the chosen games —, else the ones this run set up
  const ids = p.server.kind === 'existing' ? qsGameIdsOfPlan(p) : (p.server.versions || []).map((x) => x && x.id).filter(Boolean);
  ids.forEach((sv) => {
    const mine = S.accounts && S.accounts[sv] && S.accounts[sv].name ? S.accounts[sv].name : '';
    // the server's word on its pre-made account: its status now, else what it said before — an answered '' is none
    const f = srvFacts(sv);
    const srv = f ? f.account || '' : hasOwn(S.srvAccount, sv) ? S.srvAccount[sv] || '' : hasOwn(S.serverAccounts, sv) ? S.serverAccounts[sv] || '' : '';
    const name = mine || srv;
    if (!name) return;
    const gen = S.qs.mem.generated[sv];
    const password = gen && mine ? t('qs.done.passwordShown', { password: gen })
      : passwordVerified(sv) ? (mine ? t('qs.done.passwordYours') : t('lib.creds.verifiedAsk')) : t('qs.done.passwordAny');
    out.push({ version: sv, name, password });
  });
  return out;
}
// The server versions of the game versions a plan names, in S.stacks' order.
function qsGameIdsOfPlan(p) {
  const want = new Set(((p.game && p.game.versions) || []).filter((g) => g && g.id).map((g) => qsServerOf(g.id)));
  return (Array.isArray(S.stacks) ? S.stacks : []).map((s) => s.id).filter((sv) => want.has(sv));
}
// A step that ended without its goal: failed, still asking, never reached, or skipped for a reason the player should know.
function qsUnfinished(st) {
  const r = qsRec(st.id), rt = S.qs.rt;
  if (r.state === 'failed' || r.state === 'needs' || r.state === 'pending' || r.state === 'paused') return true;
  return r.state === 'skipped' && (!!rt.userSkipped[st.id] || (st.kind === 'account' && !!r.note && !rt.created[st.v]));
}
// The run reached the server it was for: its connecting step (an existing server's, else the agent's) ended done, and
// Relic talks to that server in the mode the plan wants — a player of an existing server, else its admin.
function qsDoneConnected() {
  const p = S.qs.plan;
  if (!p || !S.qs.rt) return false;
  const s = p.server;
  if (qsRec(s.kind === 'existing' ? 's.connect' : 's.agent').state !== 'done') return false;
  if (!S.serverConfigured || !qsSameHost(S.serverAddr.host, qsTargetHost())) return false;
  return s.kind === 'existing' && !s.admin ? S.mode === 'player' : S.mode === 'admin';
}
// Every game version the plan names is in the Library.
const qsDoneGames = () => ((S.qs.plan && S.qs.plan.game.versions) || []).every((g) => !g || !g.id || isInstalled(g.id));
// Everything is set up: the server reached, the games in the Library, no step left without its goal.
const qsDoneAllSet = () => !!S.qs.rt && qsDoneConnected() && qsDoneGames() && !S.qs.rt.steps.some((st) => st.row && qsUnfinished(st));
// The name of the saved entry: "This PC" for this PC's loopback, else the host — at most the 40 characters a name holds.
const qsSavedLabel = () => { const a = qsSavedAddress(); return hostOf(a) === '127.0.0.1' ? t('qs.done.labelThisPc') : hostOf(a).slice(0, 40); };
function qsSavedAddress() {
  const s = S.qs.plan.server;
  if (s.kind === 'existing') return s.address;
  if (s.kind === 'linux') return s.ssh.host;
  return S.serverAddr.host || s.address;
}

// ─────────────────────────── the runner ───────────────────────────
// The steps of a plan (I57), in the order each thread takes them: {id, thread, kind, v, deps (each must be
// done or skipped), after (each must have ended), row (drawn), bg (runs beside the thread's own step), local}.
function qsStepsOf(p, rt) {
  const list = [];
  const add = (id, thread, kind, o) => list.push(Object.assign({ id, thread, kind, v: '', deps: [], after: [], row: true, bg: false, local: false }, o || {}));
  const s = p.server, n = s.installs || {}, kind = s.kind;
  const vers = (s.versions || []).filter((x) => x && x.id);
  // the game
  const gets = (p.game.versions || []).filter((g) => g && (g.from === 'download' || g.from === 'local'));
  if (gets.length) {
    // The game install starts once the run has written the host: the server thread's host step (an import on this PC
    // also waits for the agent step: the import's Fiddler step and the local agent's install keep apart, S8).
    const via = kind === 'existing' ? 's.connect' : kind === 'windows' && rt.agentHere ? 's.agent' : 's.host';
    const local = gets.some((g) => g.from === 'local');
    add('g.host', 'game', 'host', { deps: [via], after: kind === 'windows' && local ? ['s.agent'] : [] });
    let prev = '';
    gets.forEach((g) => {
      const imp = g.from === 'local';
      add(`g.${g.id}.get`, 'game', 'get', { v: g.id, local: imp, deps: ['g.host'], after: prev ? [prev] : [] });
      if (!imp) add(`g.${g.id}.unpack`, 'game', 'unpack', { v: g.id, deps: [`g.${g.id}.get`] });
      add(`g.${g.id}.patch`, 'game', 'patch', { v: g.id, local: imp, deps: [imp ? `g.${g.id}.get` : `g.${g.id}.unpack`] });
      prev = `g.${g.id}.patch`;
    });
    add('g.fiddler', 'game', 'fiddler', { after: [prev] });
    add('g.cert', 'game', 'cert', { deps: ['g.fiddler'] });
  }
  // the server
  const acct = (sv) => !!(p.account && p.account.create && p.account.create[sv]);
  if (kind === 'existing') {
    add('s.connect', 'server', 'connect');
    let prev = 's.connect';
    (Array.isArray(S.stacks) ? S.stacks : []).map((x) => x.id).filter(acct).forEach((sv) => { add(`s.${sv}.account`, 'server', 'account', { v: sv, deps: ['s.connect'], after: [prev] }); prev = `s.${sv}.account`; });
    add('s.ready', 'server', 'ready', { deps: ['s.connect'], after: [prev] });
    return list;
  }
  add('s.host', 'server', 'shost', { row: false });
  if (kind === 'windows') {
    if (n.python) add('s.python', 'server', 'python');
    if (n.sevenZip) add('s.sevenzip', 'server', 'sevenzip');
    if (n.agentUpdate) add('s.agentUpdate', 'server', 'agentUpdate');
    add('s.agent', 'server', 'agent', { deps: ['s.python', 's.sevenzip', 's.host', 's.agentUpdate'] });
    // An agent already on this PC learns Relic's 7zr.exe (one this run installs gets it in its install) — beside the
    // thread's own steps: it waits for the agent's status, which the WSL and Docker steps need not wait for.
    if (rt.agentHere || (p.steps && p.steps['s.agent7z'])) add('s.agent7z', 'server', 'agent7z', { deps: ['s.agent'], bg: true });
    if (s.firewall && (s.reach === 'lan' || s.reach === 'internet')) add('s.firewall', 'server', 'firewall', { deps: ['s.agent'] });
    if (n.wsl || n.wslUpdate) { add('s.wsl', 'server', 'wsl', { deps: ['s.agent'] }); add('s.restart', 'server', 'restart', { deps: ['s.wsl'] }); }
    // (WSL's memory cap once WSL's own step is through — before the restart that step may ask for: the cap applies from
    //  WSL's next start)
    if (n.wslMemoryGb > 0) add('s.wslMemory', 'server', 'wslMemory', { deps: ['s.agent'], after: ['s.wsl'] });
    if (n.docker || n.dockerStart) add('s.docker', 'server', 'docker', { deps: ['s.agent', 's.wsl', 's.restart'], after: ['s.wslMemory'] });
  } else add('s.agent', 'server', 'agent', { deps: ['s.host'] });
  vers.forEach((x) => add(`s.${x.id}.package`, 'server', 'package', { v: x.id, bg: true, deps: kind === 'windows' ? ['s.agent', 's.agent7z'] : ['s.agent'] }));
  if (kind === 'windows') add('s.agentRestart', 'server', 'agentRestart', { deps: ['s.agent', 's.docker'], after: vers.map((x) => `s.${x.id}.package`) });
  let prev = '', lastStart = '';
  vers.forEach((x) => {
    const v = x.id;
    add(`s.${v}.prepare`, 'server', 'prepare', { v, deps: [`s.${v}.package`, 's.docker', 's.agentRestart'], after: prev ? [prev] : [] });
    add(`s.${v}.pathfinding`, 'server', 'pathfinding', { v, deps: [`s.${v}.prepare`] });
    // The official fixes of the version: on by default for a version whose agent says so, and the step judges the
    // stack from the agent's answer (a version without that default is skipped; nothing is named here by its id).
    add(`s.${v}.hotpatch`, 'server', 'hotpatch', { v, deps: [`s.${v}.prepare`] });
    add(`s.${v}.start`, 'server', 'start', { v, deps: [`s.${v}.prepare`, `s.${v}.pathfinding`, `s.${v}.hotpatch`] });
    lastStart = prev = `s.${v}.start`;
    if (acct(v)) { add(`s.${v}.account`, 'server', 'account', { v, deps: [`s.${v}.start`] }); prev = `s.${v}.account`; }
  });
  if (lastStart) add('s.ready', 'server', 'ready', { deps: [lastStart], after: [prev] });
  if ((kind === 'windows' && s.reach === 'internet') || (kind === 'linux' && s.reach === 'public')) add('s.reach', 'server', 'reach', { deps: [lastStart ? 's.ready' : 's.agent'] });
  return list;
}
// A step may start: each step it depends on (deps) is done or skipped, and each one it comes after (after) no longer
// moves — done, skipped, failed, or stuck behind a failure (qsSettled); a step that asks the player holds what comes
// after it until it is answered. A step a resumed run's plan file records as done that waits for its facts (qsHintHolds)
// holds the steps behind it meanwhile.
function qsDepsOk(st) {
  return st.deps.every((id) => !qsHas(id) || (qsOkState(qsRec(id).state) && !qsHintHolds(id)))
    && st.after.every((id) => !qsHas(id) || (qsSettled(id) && !qsHintHolds(id)));
}
// A pending step that cannot start without the player's Retry: a step it depends on failed, or is stuck so itself.
function qsStuck(st, seen) {
  if (!st || qsRec(st.id).state !== 'pending') return false;
  const been = seen || new Set();
  if (been.has(st.id)) return false;
  been.add(st.id);
  return st.deps.some((id) => { const d = qsStep(id); if (!d) return false; const s = qsRec(id).state; return s === 'failed' || (s === 'pending' && qsStuck(d, been)); });
}
// A step that no longer moves by itself: done, skipped, failed, or pending behind a failure.
const qsSettled = (id) => qsFinalState(qsRec(id).state) || qsStuck(qsStep(id));
// A thread has ended: nothing of it runs or can start — what is left is stuck behind a step that did not make it, or asks
// the player with nothing waiting behind it (the certificate, the reach check: what waits behind a question keeps the
// run going until it is answered or skipped). A restart of Windows still to come never ends it.
function qsThreadEnded(th) {
  const rt = S.qs.rt;
  return rt.steps.filter((s) => s.thread === th).every((s) => {
    const r = qsRec(s.id).state;
    return qsSettled(s.id) || (r === 'needs' && s.kind !== 'restart');
  });
}
const qsEnded = () => !!S.qs.rt && !!S.qs.plan && S.qs.plan.state !== 'waitingRestart' && !S.qs.rt.restarting
  && qsThreadEnded('game') && qsThreadEnded('server') && !S.qs.inst && !S.qs.setup && !S.qs.job && !S.qs.acct && !S.qs.rt.deploying;

// A step's state from the facts: done / skipped when they say so, null when they say nothing — a recorded state is
// never taken back. Kinds marked early are judged also before they start (what is already true when the run begins);
// the others only once they run or ask the player.
const QS_DONE = { state: 'done', note: '' };
const QS_SKIP = { state: 'skipped' };
function qsFact(st, rec) {
  const p = S.qs.plan, rt = S.qs.rt, m = qsMachine(), n = p.server.installs || {}, sv = st.v;
  switch (st.kind) {
    case 'host': return qsDepsOk(st) && qsSameHost(S.serverAddr.host, qsTargetHost()) ? QS_DONE : null;
    case 'get': return isInstalled(sv) || qsPast(sv, st.local ? QS_PAST_IMPORT : QS_PAST_DOWNLOAD) ? QS_DONE : null;
    case 'unpack': return isInstalled(sv) || qsPast(sv, QS_PAST_EXTRACT) ? QS_DONE : null;
    case 'patch': return isInstalled(sv) && !(S.qs.inst && S.qs.inst.versionId === sv) ? QS_DONE : null;
    case 'fiddler': {
      const patches = rt.steps.filter((x) => x.kind === 'patch');
      if (!patches.every((x) => qsSettled(x.id))) return null;
      if (S.fiddler.installed) return QS_DONE;
      return patches.some((x) => qsRec(x.id).state === 'done') ? { state: 'failed', note: rt.fiddlerNote } : QS_SKIP;
    }
    case 'cert':
      // (a certificate Custom chose not to install is not asked for; nor is one of a Fiddler no install set up)
      if (!qsDepsOk(st)) return null;
      if (S.fiddler.certTrusted) return QS_DONE;
      return p.game.installCert === false || (qsRec('g.fiddler').state === 'skipped' && !S.fiddler.installed) ? QS_SKIP : null;
    case 'connect': {
      const s = p.server;
      if (!qsSameHost(S.serverAddr.host, s.address)) return null;
      // (a token typed in the wizard, or an agent port Custom gave, is still to be used: this run's own login says, once
      //  its answer is in — never the mode Relic was in before it)
      const typed = !!S.qs.mem.token.trim() || (s.agentPort > 0 && Number(S.serverAddr.agentPort) !== s.agentPort);
      if (typed || (rt.act[st.id] && rt.act[st.id] !== 'ok')) return null;
      return (s.admin ? S.mode === 'admin' && S.serverConfigured : S.mode === 'player' && S.serverConfigured) ? QS_DONE : null;
    }
    case 'shost': return rt.agentHere ? QS_SKIP : qsSameHost(S.serverAddr.host, p.server.address) ? QS_DONE : null;
    case 'python': return m && m.python ? QS_DONE : null;
    case 'sevenzip': return m && m.extractor ? QS_DONE : m && !qsNeeds7z(p).length ? QS_SKIP : null;
    case 'agentUpdate': return S.agentBuild.shipped && S.agentBuild.local && S.agentBuild.local.build === S.agentBuild.shipped ? QS_DONE : null;
    // (only once this run's own step got there — its install, its entry, its login: Server Admin mode on the host alone
    //  says nothing of an agent this run is to install, or of one that does not run)
    case 'agent': return rt.act[st.id] === 'ok' && S.mode === 'admin' && S.serverConfigured && qsSameHost(S.serverAddr.host, qsTargetHost()) ? QS_DONE : null;
    case 'firewall': { const la = S.qs.la.res; return la && la.firewallRule && la.serverRules ? QS_DONE : null; }
    case 'wsl': return m && m.wsl === 'on' ? QS_DONE : null;
    case 'restart':
      if (rec.state !== 'needs' && rec.state !== 'running') return null;
      return rt.resumed && rt.restartNeeded !== true && m && m.restartPending === false && m.wsl === 'on' ? QS_DONE : null;
    case 'wslMemory': return m && m.wslCapSource === 'wslconfig' && Number(m.wslCap) >= n.wslMemoryGb * QS_GIB ? QS_DONE : null;
    case 'docker': return m && m.docker && m.docker.state === 'running' ? QS_DONE : null;
    case 'package': { if (!qsOnTarget() || !qsFresh()) return null; const f = srvFacts(sv); return f && f.present ? QS_DONE : null; }
    case 'prepare': {
      if (!qsOnTarget() || !qsFresh()) return null;
      const f = srvFacts(sv);
      if (!(f && f.present && f.bootstrapped && f.provisioned === true)) return null;
      return rec.state === 'pending' ? QS_SKIP : QS_DONE;
    }
    case 'start': { if (!qsOnTarget() || !qsFresh()) return null; const f = srvFacts(sv); return f && f.up ? QS_DONE : null; }
    // Enabled is true for a record pending its next Start too: a stopped stack is done, the Start applies the fixes.
    // A default the agent could not apply leaves the record off, and the step runs (and fails visibly). A fresh status
    // that names no fixes for the version (none packaged, or an agent that does not send the answer) is skipped: the
    // step must never hold the Start.
    case 'hotpatch': {
      if (!qsOnTarget() || !qsFresh()) return null;
      const f = srvFacts(sv), h = f && f.hotpatch;
      if (!h || h.available !== true || h.defaultEnabled !== true) return QS_SKIP;
      // A decision on record is the admin's: switched on it is done, switched off it is skipped and never turned on here.
      if (h.decided === true) return h.enabled === true ? QS_DONE : QS_SKIP;
      return h.enabled === true ? QS_DONE : null;
    }
    case 'account': return rt.created[sv] ? QS_DONE : null;
    case 'ready': {
      if (p.server.kind === 'existing') return null;
      const last = qsLastVersion(), f = last && qsOnTarget() && qsFresh() ? srvFacts(last) : null;
      return f && f.up && !f.loading ? QS_DONE : null;
    }
    default: return null;
  }
}
const QS_EARLY = ['get', 'unpack', 'patch', 'cert', 'connect', 'shost', 'python', 'sevenzip', 'agentUpdate', 'firewall', 'wsl', 'wslMemory', 'docker', 'package', 'prepare', 'start'];
// ...of which these only once their turn has come: one stack runs at a time, so an earlier version's Prepare stops a
// later one that ran when the run began.
const QS_IN_TURN = ['prepare', 'start'];
const qsPast = (v, phases) => { const ph = S.qs.rt && S.qs.rt.instPhase[v]; return !!ph && phases.includes(ph); };
// The admin status in hand was read after the run's last server job ended (srvGen counts the admin status reads): what
// a job changed is in it.
const qsFresh = () => !S.qs.rt || srvGen > (S.qs.rt.jobGen || 0);

// ── a resumed run's records: hints of what was started, never the truth (I57) ──
// The kinds whose "done" the plan file records a resumed run takes only once the facts agree: the host and the mode
// (known at once), the agent, this PC's installs (a look at this PC made after the resume), the firewall rules (the
// agent on this PC's status), the agent's build, the server packages and the last version's start (a status of the
// run's server read after the resume) — a restart of Windows or of the launcher, or the player's own moves while the
// run was stopped, may have changed any of them. The ones whose facts come from a look at this PC hold the steps behind
// them until the look is in (QS_HINT_MS at most).
const QS_HINT_KINDS = ['host', 'connect', 'shost', 'agent', 'python', 'sevenzip', 'wsl', 'wslMemory', 'docker', 'firewall', 'agentUpdate', 'package', 'hotpatch', 'start'];
const QS_HINT_HOLDS = ['python', 'sevenzip', 'wsl', 'wslMemory', 'docker', 'firewall', 'agentUpdate'];
function qsHintHolds(id) {
  const rt = S.qs.rt;
  if (!rt || !rt.hints[id]) return false;
  const st = qsStep(id);
  return !!st && QS_HINT_HOLDS.includes(st.kind);
}
// A recorded "done" against the facts: 'keep' (they agree), 'again' (they say otherwise: the step goes again), null (they
// are not in yet).
function qsHintVerdict(st) {
  const p = S.qs.plan, rt = S.qs.rt, s = p.server, n = s.installs || {};
  const m = S.qs.check.n > rt.checkN ? qsMachine() : null, la = S.qs.la.n > rt.laN ? S.qs.la.res : null;
  switch (st.kind) {
    case 'host': return S.serverConfigured && qsSameHost(S.serverAddr.host, qsTargetHost()) ? 'keep' : 'again';
    case 'shost': return rt.agentHere || (S.serverConfigured && qsSameHost(S.serverAddr.host, s.address)) ? 'keep' : 'again';
    case 'connect': return S.serverConfigured && qsSameHost(S.serverAddr.host, s.address) && S.mode === (s.admin ? 'admin' : 'player') ? 'keep' : 'again';
    // The agent on this PC is entered again whatever the mode says — health first, started when it does not run (its
    // start with Windows may be off); a Linux box's is kept while Relic is its admin, else logged in to again.
    case 'agent':
      if (s.kind === 'windows') return 'again';
      return S.mode === 'admin' && S.serverConfigured && qsSameHost(S.serverAddr.host, qsTargetHost()) ? 'keep' : 'again';
    case 'python': return !m ? null : m.python ? 'keep' : 'again';
    case 'sevenzip': return !m ? null : m.extractor || !qsNeeds7z(p).length ? 'keep' : 'again';
    // (never asked again while the restart that finishes turning it on is to come)
    case 'wsl': return !m ? null : (m.wsl === 'off' || m.wsl === 'old') && m.restartPending !== true && qsRec('s.restart').state !== 'needs' ? 'again' : 'keep';
    case 'wslMemory': return !m ? null : m.wslCapSource === 'wslconfig' && Number(m.wslCap) >= (Number(n.wslMemoryGb) || 0) * QS_GIB ? 'keep' : 'again';
    case 'docker': return !m || !m.docker ? null : m.docker.state === 'running' ? 'keep' : 'again';
    case 'firewall': return !la ? null : la.firewallRule && la.serverRules ? 'keep' : 'again';
    case 'agentUpdate': {
      const b = S.agentBuild;
      if (!b.shipped) return null;
      return !(b.local && b.local.installed) || b.local.build === b.shipped ? 'keep' : 'again';
    }
    // Judged from a status read after the resume: enabled, or a decision the admin made, the record stands; off and
    // undecided, the step goes again (the run switches the default on).
    case 'hotpatch': {
      if (!qsOnTarget() || srvGen <= rt.gen) return null;
      const f = srvFacts(st.v), h = f && f.hotpatch;
      if (!h || h.available !== true || h.defaultEnabled !== true || h.decided === true) return 'keep';
      return h.enabled === true ? 'keep' : 'again';
    }
    case 'package': case 'start': {
      // (the start of the last version only: one stack runs at a time, an earlier one is down by design)
      if (st.kind === 'start' && st.v !== qsLastVersion()) return 'keep';
      if (!qsOnTarget() || srvGen <= rt.gen) return null;
      const f = srvFacts(st.v);
      if (!f) return 'keep';
      return (st.kind === 'package' ? f.present : f.up) ? 'keep' : 'again';
    }
    default: return 'keep';
  }
}
// The recorded "done" steps whose facts are in, judged: kept, or set going again — a step set going again reconnects
// where it connects (it never installs anew what it installed), and a server package gone takes its version's steps
// after it along. True when anything changed.
function qsHintsJudge() {
  const rt = S.qs.rt;
  let changed = false;
  Object.keys(rt.hints).forEach((id) => {
    const st = qsStep(id);
    if (!st || qsRec(id).state !== 'done') { delete rt.hints[id]; return; }
    let v = qsHintVerdict(st);
    if (v === null && QS_HINT_HOLDS.includes(st.kind) && Date.now() - rt.hintAt > QS_HINT_MS) v = 'keep';
    if (v === null) return;
    delete rt.hints[id];
    changed = true;
    if (v === 'keep') return;
    rt.act[id] = ''; rt.reconnect[id] = true;
    qsSetState(id, 'pending', '');
    if (st.kind === 'package') {
      rt.steps.filter((x) => x.v === st.v && ['prepare', 'pathfinding', 'hotpatch', 'start', 'account'].includes(x.kind) && qsOkState(qsRec(x.id).state))
        .forEach((x) => { delete rt.hints[x.id]; rt.act[x.id] = ''; qsSetState(x.id, 'pending', ''); });
    }
  });
  return changed;
}

// Records a step's state (and its note, cut short): the plan is saved soon after, and a step that changes this PC asks
// the check (or the local agent's status) again.
function qsSetState(id, state, note) {
  const rt = S.qs.rt;
  if (!S.qs.plan || !qsHas(id)) return false;
  const r = qsRec(id), was = r.state;
  const nn = note === undefined ? (state === was ? r.note : '') : String(note == null ? '' : note).slice(0, QS_NOTE_MAX);
  if (was === state && r.note === nn) return false;
  r.state = state; r.note = nn;
  if (rt) {
    rt.dirty = true;
    if (state === 'running' && was !== 'running') rt.since[id] = Date.now();
  }
  qsSaveSoon();
  if (was !== state) qsLooksFor(id);
  return true;
}
const qsSetNote = (id, note) => { if (qsHas(id) && qsRec(id).note !== String(note || '').slice(0, QS_NOTE_MAX)) qsSetState(id, qsRec(id).state, note); };
function qsLooksFor(id) {
  const st = qsStep(id);
  if (!st) return;
  if (['python', 'sevenzip', 'wsl', 'restart', 'wslMemory', 'docker', 'agent', 'agentUpdate'].includes(st.kind)) qsCheckSoon(1500);
  if (['agent', 'firewall', 'agentRestart', 'agentUpdate'].includes(st.kind) && S.qs.plan.server.kind === 'windows') qsLaSoon(1500);
}

let qsTicking = false, qsTickAgain = false;
// The runner's look: every step judged again from the facts, then each thread's next step started when
// its dependencies allow (one at a time per thread, the background package steps beside), and the page redrawn when
// anything changed. app.js queues it after every init state and every status read; it runs every QS_TICK_MS too while a
// run goes on, and after each event of the run's jobs.
function qsTick() {
  if (!S.qs || !S.qs.plan || !S.qs.rt) return;
  if (qsTicking) { qsTickAgain = true; return; }
  qsTicking = true;
  let changed = false;
  try {
    for (let i = 0; i < 25; i++) {
      qsTickAgain = false;
      const c = qsTickOnce();
      changed = changed || c;
      if (!c && !qsTickAgain) break;
    }
  } catch (e) { console.error(e); }
  finally { qsTicking = false; }
  const rt = S.qs.rt;
  if ((changed || (rt && rt.dirty)) && S.screen === 'quickstart') { if (rt) rt.dirty = false; renderOrOwe(); }
  else qsLiveRun();
}
function qsTickOnce() {
  const p = S.qs.plan, rt = S.qs.rt;
  if (!p || !rt) return false;
  // A change of the status target that no step of the run made stops the run.
  const tgt = srvTarget();
  if (rt.target == null || rt.ownWrite > 0) rt.target = tgt;
  else if (tgt !== rt.target && qsRunning()) { qsRetargetStop(); return true; }
  let changed = false;
  // (a resumed run's recorded "done" steps, judged from the facts as they come in)
  if (Object.keys(rt.hints).length && qsHintsJudge()) changed = true;
  rt.steps.forEach((st) => {
    const rec = qsRec(st.id);
    if (rec.state === 'done' || rec.state === 'skipped') return;
    if (rec.state === 'pending' && (!QS_EARLY.includes(st.kind) || (QS_IN_TURN.includes(st.kind) && !qsDepsOk(st)))) return;
    const f = qsFact(st, rec);
    if (f && f.state !== rec.state && qsSetState(st.id, f.state, f.note === undefined ? '' : f.note)) changed = true;
  });
  qsForgetLogins();
  if (!qsRunning()) return changed;
  ['game', 'server'].forEach((th) => {
    rt.steps.filter((st) => st.thread === th && st.bg && qsRec(st.id).state === 'pending' && qsDepsOk(st)).forEach((st) => { if (qsSetState(st.id, 'running', '')) changed = true; });
    const busy = rt.steps.some((st) => st.thread === th && !st.bg && qsRec(st.id).state === 'running');
    if (!busy) {
      const next = rt.steps.find((st) => st.thread === th && !st.bg && qsRec(st.id).state === 'pending' && qsDepsOk(st));
      if (next && qsSetState(next.id, 'running', '')) changed = true;
    }
  });
  rt.steps.forEach((st) => {
    const rec = qsRec(st.id);
    if (rec.state === 'running') {
      try { qsPump(st, rec); } catch (e) { console.error(e); }
    }
  });
  // A step that waits on the status of the run's server has it read again (QS_STATUS_GAP_MS at most).
  if (rt.steps.some((st) => st.thread === 'server' && QS_STATUS_KINDS.includes(st.kind) && qsRec(st.id).state === 'running')) qsStatusSoon();
  return changed;
}
// The runner starts steps: a run that runs — not stopped, not while its plan is first written (a run that starts or goes
// on), not while Windows is asked to restart.
const qsRunning = () => !!(S.qs.plan && S.qs.rt && !S.qs.rt.stopped && !S.qs.rt.starting && !S.qs.rt.restarting && S.qs.plan.state === 'running');
// What was typed to log in leaves page memory once nothing of the run needs it any more: the SSH login (password,
// passphrase, sudo's password) once the agent is installed on the box, the account password once every account step
// has ended. A step that needs one again asks for it.
function qsForgetLogins() {
  const p = S.qs.plan, m = S.qs.mem, rt = S.qs.rt;
  if (p.server.kind === 'linux' && qsRec('s.agent').state === 'done' && (m.ssh || m.phrase || m.sudo)) { m.ssh = ''; m.phrase = ''; m.sudo = ''; }
  const accts = rt.steps.filter((st) => st.kind === 'account');
  if (m.acct && accts.length && accts.every((st) => qsSettled(st.id))) m.acct = '';
}
let qsSoonTimer = 0;
function qsTickSoonQs() { if (qsSoonTimer) return; qsSoonTimer = setTimeout(() => { qsSoonTimer = 0; qsTick(); }, 0); }
setInterval(() => { if (qsRunning()) qsTick(); }, QS_TICK_MS);

// What a running step does (its pump): the action it starts once its preconditions hold, its waits and its timeouts.
function qsPump(st, rec) {
  const rt = S.qs.rt;
  if (rt.retryAt[st.id] && Date.now() < rt.retryAt[st.id]) return;
  const p = S.qs.plan, n = p.server.installs || {};
  switch (st.kind) {
    case 'get': case 'unpack': case 'patch': qsEnsureInstall(st); return;
    case 'cert': if (!S.fiddler.certTrusted) qsSetState(st.id, 'needs', t('qs.note.certNo')); return;
    case 'connect': qsPumpConnect(st); return;
    case 'shost': qsPumpHost(st); return;
    case 'python': qsPumpSetup(st, 'python'); return;
    case 'sevenzip': qsPumpSetup(st, 'sevenZip'); return;
    case 'wsl': qsPumpSetup(st, n.wsl ? 'wsl' : 'wslUpdate'); return;
    case 'wslMemory': qsPumpSetup(st, 'wslMemory', { memoryGb: n.wslMemoryGb }); return;
    case 'docker': qsPumpDocker(st); return;
    case 'restart': qsPumpRestart(st); return;
    case 'agentUpdate': qsPumpAgentUpdate(st); return;
    case 'agent': if (p.server.kind === 'linux') qsPumpAgentLinux(st); else qsPumpAgentWindows(st); return;
    case 'agent7z': qsPumpAgent7z(st); return;
    case 'firewall': qsPumpFirewall(st); return;
    case 'package': qsPumpPackage(st); return;
    case 'agentRestart': qsPumpAgentRestart(st); return;
    case 'prepare': qsPumpPrepare(st); return;
    case 'pathfinding': qsPumpPathfinding(st); return;
    case 'hotpatch': qsPumpHotpatch(st); return;
    case 'start': qsPumpStart(st); return;
    case 'account': if (p.server.admin || p.server.kind !== 'existing') qsPumpAccountAdmin(st); else qsPumpAccountPlayer(st); return;
    case 'ready': qsPumpReady(st, rec); return;
    case 'reach': qsPumpReach(st); return;
    default: return;
  }
}

// ── the game ──
// The version's game install: started (once at a time, the run's own) unless it runs or the version is installed.
function qsEnsureInstall(st) {
  const rt = S.qs.rt, v = st.v;
  if (S.qs.inst || isInstalled(v) || rt.act['inst:' + v] || S.installing) return;
  rt.act['inst:' + v] = 'sent';
  qsStartInstall(v);
}
async function qsStartInstall(v) {
  const rt = S.qs.rt, p = S.qs.plan;
  const g = (p.game.versions || []).find((x) => x && x.id === v) || { id: v, from: 'download', voices: [] };
  const local = g.from === 'local';
  try {
    await qsSettingsOnce();
    if (S.qs.rt !== rt || !qsRunning()) { rt.act['inst:' + v] = ''; return; }
    const payload = { versionId: v, localPath: local ? g.localPath : '', voices: local ? [] : (g.voices || []).slice() };
    if (local && g.replaceInstalledAt) payload.replaceInstalledAt = g.replaceInstalledAt;
    S.qs.inst = { versionId: v, local, phase: '', fraction: 0, message: '', stop: '' };
    await rpc('install.start', payload);
  } catch (e) {
    if (S.qs.inst && S.qs.inst.versionId === v) S.qs.inst = null;
    rt.act['inst:' + v] = '';
    const id = qsGameStepOf(v);
    if (id) qsSetState(id, 'failed', e.message);
    qsTickSoonQs();
  }
}
// The run's settings, once before its first game install (settings.save — the games' folder, the download host, the
// enhancements, the HTTPS decryption, the certificate, the desktop shortcut), when they differ from the ones held.
async function qsSettingsOnce() {
  const rt = S.qs.rt;
  if (rt.settingsSaved) return;
  const g = S.qs.plan.game, st = S.settings;
  const want = { installRoot: g.installRoot || st.installRoot, source: g.source || st.source, enhancements: g.enhancements !== false,
    fiddlerDecrypt: g.fiddlerDecrypt !== false, installCert: g.installCert !== false, createShortcut: g.createShortcut !== false };
  if (Object.keys(want).every((k) => st[k] === want[k])) { rt.settingsSaved = true; return; }
  // (the whole settings object, as the Settings page sends it: settings.save reads no host — S2)
  const d = await rpc('settings.save', Object.assign({}, st, want));
  rt.settingsSaved = true;
  if (d) applyInit(d);
}
// The step an install phase belongs to: the downloads (or an import's check), the unpacking, the rest.
function qsGameStepForPhase(v, phase) {
  const kind = phase === 'DownloadClient' || phase === 'DownloadAudio' || phase === 'Import' ? 'get' : phase === 'Extract' ? 'unpack' : phase ? 'patch' : '';
  const id = kind ? `g.${v}.${kind}` : '';
  return id && qsHas(id) ? id : '';
}
// The step of a version's game install now under way: its download (or check), its unpacking, its patch.
function qsGameStepOf(v) {
  const ids = [`g.${v}.get`, `g.${v}.unpack`, `g.${v}.patch`].filter(qsHas);
  return ids.find((id) => !qsOkState(qsRec(id).state)) || ids[ids.length - 1] || '';
}

// ── the host ──
// The start screen's order: server.setAddress, then mode.set {player} (its reply is the init state) — the status target
// these move to is the run's own.
async function qsWriteHost(address, agentPort) {
  const rt = S.qs.rt;
  rt.ownWrite++;
  try {
    await rpc('server.setAddress', { address, agentPort: agentPort > 0 ? agentPort : 0 });
    const d = await rpc('mode.set', { mode: 'player' });
    applyInit(d);
  } finally { qsOwnWriteDone(rt); }
}
// A host write of the run has ended: what it moved the status target to is the run's from here on — once the code that
// took its answer in has finished (a deploy.done's handler runs after its observer).
function qsOwnWriteDone(rt) {
  setTimeout(() => {
    if (S.qs.rt !== rt) return;
    rt.ownWrite = Math.max(0, rt.ownWrite - 1);
    rt.target = srvTarget();
    qsTickSoonQs();
  }, 0);
}
async function qsPumpConnect(st) {
  const rt = S.qs.rt;
  if (rt.act[st.id]) return;
  const s = S.qs.plan.server, address = s.address, agentPort = s.agentPort > 0 ? s.agentPort : 0;
  rt.act[st.id] = 'sent';
  if (!s.admin) {
    try { await qsWriteHost(address, agentPort); rt.act[st.id] = 'ok'; } catch (e) { rt.act[st.id] = ''; qsSetState(st.id, 'failed', e.message); }
    qsTickSoonQs();
    return;
  }
  const token = S.qs.mem.token.trim();
  if (!token && !tokenAppliesTo(address)) { rt.act[st.id] = ''; qsSetState(st.id, 'needs', t('qs.note.tokenAgain', { host: hostOf(address) })); return; }
  try {
    await qsAdminLogin(address, token, agentPort);
    rt.act[st.id] = 'ok';
  } catch (e) {
    rt.act[st.id] = '';
    const again = e.message === t('backend.tokenRefused') || e.message === t('backend.tokenRequired');
    if (again) S.qs.mem.token = '';
    qsSetState(st.id, again ? 'needs' : 'failed', again ? `${e.message} ${t('qs.note.tokenAgain', { host: hostOf(address) })}` : e.message);
  }
  qsTickSoonQs();
}
// The admin login of the run (admin.login: an existing server's admin, or a Linux box the run set up that Relic is no
// longer the admin of) — the token typed leaves page memory once it took it; '' = the token saved for that host.
async function qsAdminLogin(address, token, agentPort) {
  const rt = S.qs.rt;
  rt.ownWrite++;
  try {
    const r = await rpc('admin.login', { address, token, agentPort });
    S.qs.mem.token = '';
    applyInit(r && r.state);
    if (r && r.warning) showToast(r.warning, 'bad');
    return r;
  } finally { qsOwnWriteDone(rt); }
}
async function qsPumpHost(st) {
  const rt = S.qs.rt;
  if (rt.act[st.id]) return;
  const s = S.qs.plan.server;
  rt.act[st.id] = 'sent';
  try { await qsWriteHost(s.address, s.agentPort); } catch (e) { rt.act[st.id] = ''; qsSetState(st.id, 'failed', e.message); }
  qsTickSoonQs();
}

// ── this PC's installs (quickstart.install) ──
// A refusal "close Fiddler first" while the game install's Fiddler or Shortcut step runs is waited out: asked again
// every QS_FIDDLER_RETRY_MS, QS_FIDDLER_MS at most (S8). True when it is waited out.
function qsFiddlerWait(st, message) {
  const rt = S.qs.rt;
  if (message !== t('backend.closeFiddlerFirst')) return false;
  const ph = S.qs.inst && S.qs.inst.phase;
  if (ph !== 'Fiddler' && ph !== 'Shortcut') return false;
  const first = rt.fiddlerSince[st.id] || (rt.fiddlerSince[st.id] = Date.now());
  if (Date.now() - first > QS_FIDDLER_MS) return false;
  rt.act[st.id] = '';
  rt.retryAt[st.id] = Date.now() + QS_FIDDLER_RETRY_MS;
  qsSetNote(st.id, t('qs.note.fiddlerWait'));
  return true;
}
function qsPumpSetup(st, what, extra) {
  const rt = S.qs.rt;
  if (rt.act[st.id] || S.qs.setup || !qsMachine() || !qsFreshCheck()) return;
  rt.act[st.id] = 'sent';
  qsSetup(st, what, extra);
}
// A resumed run acts on this PC only from a look made after it was taken up again — an install or a prompt that was under
// way when the run stopped may have ended meanwhile (its result is then there: no second download, no second permission
// prompt) —, QS_HINT_MS at most; the same for the agent on this PC's status.
const qsFreshCheck = () => { const rt = S.qs.rt; return !rt || !rt.resumed || S.qs.check.n > rt.checkN || Date.now() - rt.hintAt > QS_HINT_MS; };
const qsFreshLa = () => { const rt = S.qs.rt; return !rt || !rt.resumed || S.qs.la.n > rt.laN || Date.now() - rt.hintAt > QS_HINT_MS; };
async function qsSetup(st, what, extra) {
  const rt = S.qs.rt;
  try {
    // The backend runs WSL's prompt, Docker Desktop's install and WSL's memory limit only for the plan it stores.
    if (QS_PLAN_GATED.includes(what)) await qsSaveNow();
    S.qs.setup = { what, step: st.id };
    await rpc('quickstart.install', Object.assign({ what }, extra || {}));
  } catch (e) {
    if (S.qs.setup && S.qs.setup.what === what) S.qs.setup = null;
    rt.act[st.id] = '';
    if ((what === 'docker' || what === 'dockerStart') && qsFiddlerWait(st, e.message)) return;
    if (e.message === t('backend.qs.busy')) { rt.retryAt[st.id] = Date.now() + 5000; return; }
    qsSetState(st.id, 'failed', e.message);
  }
  qsTickSoonQs();
}
const QS_SETUP_STEP = { python: 's.python', sevenZip: 's.sevenzip', wsl: 's.wsl', wslUpdate: 's.wsl', wslMemory: 's.wslMemory', docker: 's.docker', dockerStart: 's.docker' };
// The end of an install of quickstart.install (quickstart.done / quickstart.error — posted once its busy flag is free).
function qsSetupEnd(done, err) {
  const x = done || err || {}, what = String(x.what || ''), job = S.qs.setup;
  if (job && job.what === what) S.qs.setup = null;
  const rt = S.qs.rt, id = (job && job.what === what && job.step) || QS_SETUP_STEP[what] || '';
  if (!S.qs.plan || !rt || !qsHas(id)) { qsCheckSoon(); return; }
  rt.act[id] = '';
  if (err) {
    qsSetState(id, 'failed', what === 'dockerStart' ? `${err.message || ''} ${t('qs.note.dockerFirst')}` : (err.message || t('common.unknownError')));
  } else {
    const r = (done && done.result) || {};
    if (what === 'sevenZip' && typeof r.path === 'string' && r.path) rt.sevenZipPath = r.path;
    if (what === 'wsl' || what === 'wslUpdate') { rt.wslRan = true; rt.restartNeeded = r.restartNeeded === true; }
    if (what === 'docker') rt.dockerInstalled = true; // its start follows, in the same step
    else qsSetState(id, 'done', '');
  }
  qsCheckSoon();
  qsTickSoonQs();
}
on('quickstart.log', (d) => {
  if (!S.qs.plan || !S.qs.rt || !d) return;
  const text = String(d.text || ''), id = (S.qs.setup && S.qs.setup.what === d.what && S.qs.setup.step) || QS_SETUP_STEP[d.what];
  qsLogLine('server', text);
  if (id) qsDetail(id, text);
});
on('quickstart.done', (d) => qsSetupEnd(d || {}, null));
on('quickstart.error', (d) => qsSetupEnd(null, d || {}));
function qsPumpDocker(st) {
  const rt = S.qs.rt, m = qsMachine(), n = S.qs.plan.server.installs;
  if (rt.act[st.id] || S.qs.setup || !m || !m.docker || !qsFreshCheck()) return;
  if (m.docker.state === 'running') return;
  const install = !rt.dockerInstalled && m.docker.state === 'missing';
  if (install && !n.docker) { qsSetState(st.id, 'failed', t('backend.qs.notChosen')); return; }
  rt.act[st.id] = 'sent';
  qsSetup(st, install ? 'docker' : 'dockerStart', install && n.acceptLicence === true ? { acceptLicence: true } : {});
}
// Windows restarts to finish turning WSL 2 on: the step asks the player; after the restart the check
// says no restart is pending and WSL is on.
function qsPumpRestart(st) {
  const rt = S.qs.rt, m = qsMachine();
  if (rt.restartNeeded === false || (!rt.wslRan && qsRec('s.wsl').state === 'done' && rt.restartNeeded == null && !rt.resumed)) { qsSetState(st.id, 'skipped', ''); return; }
  if (rt.restartNeeded == null) {
    if (!m) return;
    if (m.restartPending !== true && m.wsl === 'on') { qsSetState(st.id, 'done', ''); return; }
  }
  qsSetState(st.id, 'needs', rt.restarting ? t('qs.note.restarting') : rt.restartLater ? t('qs.note.restartLater') : qsRestartNote());
}

// ── the agent ──
async function qsPumpAgentUpdate(st) {
  const rt = S.qs.rt;
  if (rt.act[st.id]) return;
  rt.act[st.id] = 'sent';
  try { await rpc('localagent.update'); }
  catch (e) { rt.act[st.id] = ''; if (!qsFiddlerWait(st, e.message)) qsSetState(st.id, 'failed', e.message); qsTickSoonQs(); }
}
async function qsPumpAgentWindows(st) {
  const rt = S.qs.rt;
  if (rt.act[st.id]) return;
  // (the agent this run installed is entered again; one that is no longer on this PC is installed anew only on the
  //  player's Retry)
  if (rt.reconnect[st.id] && !rt.agentHere) { rt.reconnect[st.id] = false; qsSetState(st.id, 'failed', t('core.deploy.notInstalledLocally')); return; }
  rt.act[st.id] = 'sent';
  rt.ownWrite++;
  if (rt.agentHere) {
    // (localagent.enter: health first, the agent started when it does not run, its token checked, then Server Admin mode)
    try {
      const r = await rpc('localagent.enter');
      rt.act[st.id] = 'ok';
      applyInit(r && r.state);
      if (r && r.warning) showToast(r.warning, 'bad');
      qsLaSoon(0);
    } catch (e) { rt.act[st.id] = ''; if (!qsFiddlerWait(st, e.message)) qsSetState(st.id, 'failed', e.message); }
    finally { qsOwnWriteDone(rt); }
    qsTickSoonQs();
    return;
  }
  rt.deploying = true;
  try { await rpc('localagent.install', qsWinPayload()); }
  catch (e) {
    rt.deploying = false; qsOwnWriteDone(rt); rt.act[st.id] = '';
    if (!qsFiddlerWait(st, e.message)) qsSetState(st.id, 'failed', e.message);
    qsTickSoonQs();
  }
}
// The Windows install's payload: no token (the backend makes one), no secret typed, the network of the
// plan, Relic's 7zr.exe when this run has one, one choice and one folder per row of S.stacks.
function qsWinPayload() {
  const s = S.qs.plan.server;
  const payload = { target: 'windows', chain: true, autostart: s.autostart !== false, token: '', listen: s.listen, bindIp: s.bindIp,
    advertisedIp: s.advertisedIp, advertisedHost: '', muipHost: '', muipKey: '', mysqlPassword: '', flaskKey: '', serverName: '',
    region: 'dev_docker', keepEnv: s.keepEnv === true ? true : null, sevenZip: qsSevenZipPath() };
  qsStackFields(payload);
  return payload;
}
function qsStackFields(payload) {
  const vers = S.qs.plan.server.versions || [];
  (Array.isArray(S.stacks) ? S.stacks : []).forEach((row) => {
    const x = vers.find((y) => y && y.id === row.id);
    const choice = x && (x.choice === 'download' || x.choice === 'have') ? x.choice : 'later';
    payload[row.stackField] = choice;
    payload[row.dirField] = choice === 'later' ? '' : x.dir;
  });
}
// A Linux box's install (I58): the SSH login typed (page memory) and the key the user confirmed, read again
// first when this page session has not read it (a restart of the launcher forgets the reads — agent.install refuses a
// key this session did not read). The read logs in nowhere: host, port and user alone.
async function qsPumpAgentLinux(st) {
  const rt = S.qs.rt, p = S.qs.plan, h = p.server.ssh, m = S.qs.mem;
  if (rt.act[st.id]) return;
  // The agent this run installed there, which Relic is no longer the admin of (a resumed run): logged in to again with
  // the token saved for the box — never installed anew on its own (that would hand the box a new token). With no token
  // saved there any more, or one it refuses, the install is the way on — once the player has typed the SSH login again.
  if (rt.reconnect[st.id]) {
    if (!tokenAppliesTo(h.host)) { rt.reconnect[st.id] = false; rt.askLogin = true; }
    else {
      rt.act[st.id] = 'sent';
      try { await qsAdminLogin(h.host, '', p.server.agentPort > 0 ? p.server.agentPort : 0); rt.act[st.id] = 'ok'; }
      catch (e) {
        rt.act[st.id] = ''; rt.reconnect[st.id] = false;
        qsLogLine('server', e.message);
        if (e.message === t('backend.tokenRefused') || e.message === t('backend.tokenRequired')) rt.askLogin = true;
        else qsSetState(st.id, 'failed', e.message);
      }
      qsTickSoonQs();
      return;
    }
  }
  // (what was typed to log in is gone after a restart of the launcher, and may be what an install that failed tripped on:
  // asked again — a key's passphrase once, empty allowed)
  const ask = rt.askLogin || (h.keyPath ? rt.resumed && !m.phrase && !rt.phraseAsked : !m.ssh);
  if (ask) { qsSetState(st.id, 'needs', h.keyPath ? t('qs.note.phraseAgain', { host: h.host }) : t('qs.note.sshAgain', { host: h.host })); return; }
  rt.act[st.id] = 'sent';
  const kr = S.qs.keyRead;
  if (!(kr && qsSameHost(kr.host, h.host) && kr.port === h.port && kr.fingerprint === h.hostKey.fingerprint)) {
    let r = null;
    try { r = await rpc('ssh.probe', { host: h.host, port: h.port, user: h.user, dirs: [], publicIp: false }); }
    catch (e) { rt.act[st.id] = ''; qsSetState(st.id, 'failed', e.message); qsTickSoonQs(); return; }
    const k = r && r.hostKey;
    if (k && k.fingerprint) S.qs.keyRead = { host: h.host, port: h.port, fingerprint: k.fingerprint };
    if (!k || k.fingerprint !== h.hostKey.fingerprint) {
      // Another key than the one confirmed: the question again, with the warning when the pins hold another one.
      rt.act[st.id] = '';
      rt.needKey = k || null;
      const changed = !!k && (k.verdict === 'changed' || k.changed === true);
      qsSetState(st.id, 'needs', !k ? t('common.unknownError') : changed
        ? t('qs.lx.key.changed', { type: k.type, fingerprint: k.fingerprint, pinned: k.pinned || h.hostKey.fingerprint || '—' })
        : t('qs.lx.key.body', { type: k.type, fingerprint: k.fingerprint }));
      qsTickSoonQs();
      return;
    }
  }
  rt.ownWrite++; rt.deploying = true;
  try { await rpc('agent.install', qsLxPayload()); }
  catch (e) {
    rt.deploying = false; qsOwnWriteDone(rt); rt.act[st.id] = '';
    if (e.message === t('backend.agent.hostKeyUnread', { host: h.host, port: h.port })) { S.qs.keyRead = null; qsTickSoonQs(); return; }
    qsSetState(st.id, 'failed', e.message);
    qsTickSoonQs();
  }
}
function qsLxPayload() {
  const s = S.qs.plan.server, h = s.ssh, m = S.qs.mem, rt = S.qs.rt;
  const payload = { target: 'linux', chain: true, sshHost: h.host, sshPort: h.port, sshUser: h.user, sshPassword: h.keyPath ? '' : m.ssh,
    sshKeyPath: h.keyPath, sshKeyPassphrase: h.keyPath ? m.phrase : '', sudoPassword: m.sudo,
    hostKey: rt.changedKey ? { fingerprint: h.hostKey.fingerprint, changed: true } : { fingerprint: h.hostKey.fingerprint },
    listen: s.listen, bindIp: s.bindIp, advertisedIp: s.advertisedIp, advertisedHost: '', token: '', muipHost: '', muipKey: '',
    mysqlPassword: '', flaskKey: '', serverName: '', region: 'dev_docker', keepEnv: s.keepEnv === true ? true : null };
  qsStackFields(payload);
  return payload;
}
// An agent already on this PC learns Relic's 7zr.exe (agent.config.set {GIO_7Z}) while its status names no tool for a
// chosen version whose package comes as a .7z only; the next status names one.
function qsPumpAgent7z(st) {
  const rt = S.qs.rt, p = S.qs.plan;
  if (!qsOnTarget()) return;
  const need = qsNeeds7z(p).filter((sv) => { const f = srvFacts(sv); return !(f && f.present); });
  const missing = need.filter((sv) => { const f = srvFacts(sv); return !!(f && f.fetch && f.fetch.tool == null); });
  if (!missing.length) { qsSetState(st.id, rt.act[st.id] ? 'done' : 'skipped', ''); return; }
  if (rt.act[st.id] === 'sent') return;
  if (rt.act[st.id] === 'set') {
    // three status reads after the change that still name no tool: the agent did not take it
    if (S.srv.info && rt.set7zGen !== srvGen && ++rt.set7zReads > 2) {
      const f = srvFacts(missing[0]);
      qsSetState(st.id, 'failed', (f && fetchProblem(f.fetch)) || '');
    }
    rt.set7zGen = srvGen;
    return;
  }
  const path = qsSevenZipPath();
  if (!path) { qsSetState(st.id, 'skipped', ''); return; }
  rt.act[st.id] = 'sent';
  rpc('agent.config.set', { set: { GIO_7Z: path } })
    .then(() => { rt.act[st.id] = 'set'; rt.set7zReads = 0; rt.set7zGen = srvGen; refreshServersQuiet(); })
    .catch((e) => { rt.act[st.id] = ''; qsSetState(st.id, 'failed', e.message); })
    .finally(qsTickSoonQs);
}
async function qsPumpFirewall(st) {
  const rt = S.qs.rt;
  if (rt.act[st.id] || !qsFreshLa()) return;
  rt.act[st.id] = 'sent';
  try {
    await qsSaveNow();
    const r = await rpc('localagent.firewall', { gamePorts: true });
    if (r && typeof r === 'object') { S.qs.la.res = r; S.qs.la.n++; }
    qsSetState(st.id, r && r.firewallRule && r.serverRules ? 'done' : 'failed', '');
  } catch (e) {
    rt.act[st.id] = '';
    if (e.message === t('backend.firewall.busy')) rt.retryAt[st.id] = Date.now() + 5000;
    else qsSetState(st.id, 'failed', e.message);
  }
  qsTickSoonQs();
}
// The agent started before Docker Desktop was installed sees no docker on its PATH: restarted once — stopped, started
// with the PATH the registry holds now — while its status carries an error, Docker runs and no job runs.
async function qsPumpAgentRestart(st) {
  const rt = S.qs.rt;
  // The agent this step stopped is started again whatever the status says (a stopped agent answers none): a start
  // refused while Fiddler runs is waited out, and a Retry starts it.
  if (rt.agentDown) {
    if (rt.act[st.id]) return;
    if (qsFiddlerPhase()) { qsSetNote(st.id, t('qs.note.fiddlerWait')); rt.retryAt[st.id] = Date.now() + QS_FIDDLER_RETRY_MS; return; }
    rt.act[st.id] = 'sent';
    try {
      await rpc('localagent.start');
      rt.agentDown = false;
      rt.act[st.id] = 'restarted'; rt.restartReads = 0; rt.restartGen = srvGen;
      refreshServersQuiet();
    } catch (e) { rt.act[st.id] = ''; if (!qsFiddlerWait(st, e.message)) qsSetState(st.id, 'failed', e.message); }
    qsTickSoonQs();
    return;
  }
  if (!qsOnTarget()) return;
  const err = !!S.srv.info.error;
  if (rt.act[st.id] === 'restarted') {
    if (!err) qsSetState(st.id, 'done', '');
    else if (rt.restartGen !== srvGen && ++rt.restartReads > 2) qsSetState(st.id, 'failed', String(S.srv.info.error));
    rt.restartGen = srvGen;
    return;
  }
  if (rt.act[st.id]) return;
  if (!err) { qsSetState(st.id, 'skipped', ''); return; }
  const m = qsMachine();
  // (Docker's engine is looked at again now and then while it is not seen running — never on every look of the runner)
  if (m && m.docker && m.docker.state !== 'running') qsCheckEvery(QS_CHECK_EVERY_MS);
  if (!m || !m.docker || m.docker.state !== 'running' || qsServerBusy()) return;
  // (never stopped while the game install's Fiddler step runs: its start would be refused then — S8)
  if (qsFiddlerPhase()) { qsSetNote(st.id, t('qs.note.fiddlerWait')); return; }
  rt.act[st.id] = 'sent';
  try {
    await rpc('localagent.stop');
    rt.agentDown = true;
    await rpc('localagent.start');
    rt.agentDown = false;
    rt.act[st.id] = 'restarted'; rt.restartReads = 0; rt.restartGen = srvGen;
    refreshServersQuiet();
  } catch (e) { rt.act[st.id] = ''; if (!qsFiddlerWait(st, e.message)) qsSetState(st.id, 'failed', e.message); }
  qsTickSoonQs();
}
// The game install's Fiddler step runs (or its desktop shortcut's, right after): Fiddler is the system proxy then, and
// the agent on this PC refuses to start (S8).
const qsFiddlerPhase = () => { const ph = S.qs.inst && S.qs.inst.phase; return ph === 'Fiddler' || ph === 'Shortcut'; };
// The look at this PC asked again when the last one is older than `ms` (never on every look of the runner).
function qsCheckEvery(ms) {
  const c = S.qs.check;
  if (!c.busy && Date.now() - (c.at || 0) > ms) qsCheckSoon(0);
}

// ── the stack ──
// A server version's package: the chain an install left downloads it (deploy.done), and so does the run when it has to —
// a version the agent here has no folder for (chainAdd: a re-point, then the download — the re-point alone where a check
// says the folder holds that server already), a configured folder that holds no server (the download), a chain that is
// gone.
function qsPumpPackage(st) {
  const rt = S.qs.rt, sv = st.v, x = qsPlanVer(sv);
  if (!qsOnTarget() || !qsFresh() || !x) return;
  const f = srvFacts(sv);
  if (f && f.present) return;
  if (qsServerBusy()) return;
  if (rt.act[st.id] === 'chain') {
    // The chain the run queued is gone and the version still has no server (the chain left the step to the Server page):
    // looked at again, three times at most.
    if ((rt.busyTries[st.id] = (rt.busyTries[st.id] || 0) + 1) > 3) { rt.act[st.id] = ''; qsSetState(st.id, 'failed', t('common.unknownError')); return; }
    rt.act[st.id] = '';
  }
  if (rt.act[st.id]) return;
  if (x.choice === 'have') { qsSetState(st.id, 'failed', t('deploy.stack.checkNotStack')); return; }
  if (!f || f.configured === false) {
    // a version the agent here has no folder for: its folder first (a re-point), then the download — the chain's two jobs.
    // A folder a check says holds that server already gets the re-point alone: no download is asked over it (the agent
    // would refuse it). Where that check was wrong (the folder still empty after the re-point), the next look finds the
    // folder configured and downloads into it — the package's own job.
    rt.act[st.id] = 'chain';
    chainAdd({ version: sv, dir: x.dir, repoint: true, fetch: qsFolderKind(sv, x.dir) !== 'stack', stop: false });
    return;
  }
  if (f.fetch && f.fetch.available) { qsRunJob(st, 'fetch', sv); return; }
  qsSetState(st.id, 'failed', t('qs.sum.noPackage', { version: sv }));
}
// A server job of the run (runServerJob — its console, its toasts). No job made (another one runs here, or the server is
// another): asked again after a moment; refused before any job existed because another one runs on the server: waited
// out, ten times at most; any other refusal is the step's failure.
async function qsRunJob(st, kind, sv, extra) {
  const rt = S.qs.rt, before = S.srvJob;
  rt.act[st.id] = 'sent';
  S.qs.job = { kind, version: sv, step: st.id, template: (extra && typeof extra.template === 'string' && extra.template) || '' };
  await runServerJob(kind, sv, extra);
  const j = S.srvJob;
  // (its outcome may be in already: qsServerEnd took the step on)
  if (!S.qs.job || S.qs.job.step !== st.id) { qsTickSoonQs(); return; }
  if (j === before) { S.qs.job = null; rt.act[st.id] = ''; rt.retryAt[st.id] = Date.now() + 5000; qsTickSoonQs(); return; }
  if (j && j.kind === kind && String(j.version) === String(sv) && !j.running && j.error) {
    const job = S.qs.job;
    S.qs.job = null; rt.act[st.id] = '';
    if (qsBusyRefusal(j.error) && (rt.busyTries[st.id] = (rt.busyTries[st.id] || 0) + 1) <= 10) rt.retryAt[st.id] = Date.now() + 10000;
    else if (st.kind === 'account') qsAccountRefused(st, j.error, job);
    else qsSetState(st.id, 'failed', j.error);
  }
  qsTickSoonQs();
}
const qsBusyRefusal = (msg) => /\bAnother operation is already running\b/.test(String(msg || '')) || msg === t('backend.server.jobBusy') || msg === t('toast.jobInProgress');
function qsPumpPrepare(st) {
  const rt = S.qs.rt, sv = st.v, x = qsPlanVer(sv);
  if (rt.act[st.id] || !qsOnTarget() || qsServerBusy() || !x) return;
  const f = srvFacts(sv), raw = S.srv.versions[sv];
  if (!f || !f.present) return;
  // (a stack the agent has a Prepare on record for — provisioned, or a progress it was prepared with — is no fresh
  //  download of this run's, whatever the run noted: never the shipped save over it)
  if (x.fetchedByRun && raw && !(raw.provisioned === false && raw.progress == null)) { x.fetchedByRun = false; qsSaveSoon(); }
  // The shipped save only over a stack this run downloaded that the agent reports not bootstrapped (I57); `keep` for
  // every other one — or the fixes alone, when Custom chose them.
  const progress = x.progress === 'default' && x.fetchedByRun && raw && raw.bootstrapped === false ? 'default' : x.progress === 'fixes' ? 'fixes' : 'keep';
  const pathfinding = typeof x.pathfinding === 'boolean' ? x.pathfinding : qsVerdict(sv) === 'noPathfinding' ? false : null;
  qsRunJob(st, 'setup', sv, pathfinding === null ? { progress } : { progress, pathfinding });
}
// The official fixes switched on for a version whose agent says they are on by default and that the stack does not
// hold on. The Prepare step made the default already; this step is the one that makes it true when the Prepare did
// not (a failed default leaves the record off, and the job below says why). A stack whose status is not read yet
// waits for it. A stack that holds a decision (switched on or off by the admin) is never changed here.
function qsPumpHotpatch(st) {
  const rt = S.qs.rt, sv = st.v;
  if (rt.act[st.id] || !qsOnTarget() || !qsFresh() || qsServerBusy()) return;
  const f = srvFacts(sv), h = f && f.hotpatch;
  if (!h || h.available !== true || h.defaultEnabled !== true || h.decided !== false || h.enabled === true) return;
  qsRunJob(st, 'hotpatch', sv, { enabled: true });
}
// The pathfinding server as the run wants it, on a stack whose Prepare did not set it (prepared before, so its Prepare was
// skipped): off where the memory verdict says so and Custom left it to the memory, else as Custom chose; nothing to do
// when the stack already has it so, or its compose file does not say.
function qsPumpPathfinding(st) {
  const rt = S.qs.rt, sv = st.v, x = qsPlanVer(sv);
  if (rt.act[st.id] || !qsOnTarget() || !qsFresh()) return;
  const f = srvFacts(sv);
  if (!f || !x) return;
  const want = typeof x.pathfinding === 'boolean' ? x.pathfinding : qsVerdict(sv) === 'noPathfinding' ? false : null;
  if (want === null || typeof f.pathfinding !== 'boolean' || f.pathfinding === want) { qsSetState(st.id, 'skipped', ''); return; }
  if (qsServerBusy()) return;
  qsRunJob(st, 'pathfinding', sv, { enabled: want });
}
function qsPumpStart(st) {
  const rt = S.qs.rt;
  if (rt.act[st.id] || !qsOnTarget() || qsServerBusy()) return;
  qsRunJob(st, 'start', st.v);
}
// The run's server job ended (server.done / server.error, after app.js's handlers): its step's outcome.
function qsServerEnd(ok, d) {
  const job = S.qs.job, kind = d && d.kind, v = String((d && d.version) || ''), rt = S.qs.rt;
  const msg = (d && d.message) || t('common.unknownError');
  // what the facts of the server's steps are read from from now on: a status read after this job's end (qsFresh)
  if (rt) rt.jobGen = srvGen;
  if (ok && kind === 'fetch' && rt && !rt.stopped) qsFetchedByRun(v);
  // A download the run itself stopped (Stop, the restart of Windows) is paused, not failed: the run takes it up again
  // where it stopped (its .part kept) once it goes on.
  const cancelled = !ok && kind === 'fetch' && !!rt && (!!rt.fetchCancelled[v] || ((rt.stopped || rt.restarting) && /^Stopped on request\b/.test(msg)));
  if (cancelled) delete rt.fetchCancelled[v];
  if (job && job.kind === kind && String(job.version) === v) {
    S.qs.job = null;
    const st = qsStep(job.step);
    if (st && rt) {
      rt.act[st.id] = ok ? 'ended' : '';
      if (ok && st.kind === 'account') qsAccountMade(st, jobResultOf(d), job);
      else if (ok) qsSetState(st.id, 'done', '');
      else if (cancelled) qsSetState(st.id, 'paused', '');
      else if (qsBusyRefusal(msg) && (rt.busyTries[st.id] = (rt.busyTries[st.id] || 0) + 1) <= 10) rt.retryAt[st.id] = Date.now() + 10000;
      else if (st.kind === 'account') qsAccountRefused(st, msg, job);
      else qsSetState(st.id, 'failed', msg);
    }
  } else if (!ok && (kind === 'fetch' || kind === 'relocate') && rt && qsHas(`s.${v}.package`) && !qsOkState(qsRec(`s.${v}.package`).state)
    && !qsBusyRefusal(msg)) {
    // the chain an install (or the run) left: its download of a version is that version's package step — one the facts
    // found done already stays done (a download the agent refused because that server is in its folder already)
    rt.act[`s.${v}.package`] = '';
    qsSetState(`s.${v}.package`, cancelled ? 'paused' : 'failed', cancelled ? '' : msg);
  }
  qsStatusSoon(true);
  qsTickSoonQs();
}
// The run downloaded this version's stack: a download job of its own ran — its first line in (the agent writes one only
// once its check found the folder free: a download refused before it began writes none) or its end done. `default` is
// possible there (qsPumpPrepare).
function qsFetchedByRun(v) {
  const x = qsPlanVer(v);
  if (x && !x.fetchedByRun) { x.fetchedByRun = true; qsSaveSoon(); }
}

// ── accounts ──
const qsAcctName = (sv) => (S.qs.rt.names[sv] || S.qs.plan.account.name || '').trim();
// The run's first look at a version's account step: this launcher has an account there already (the name the plan names,
// or another one) — Quick Start uses it, and none is created. True when the step ended so.
function qsAcctMine(st) {
  const rt = S.qs.rt, sv = st.v, name = qsAcctName(sv), mine = acctLive(sv);
  const same = mine.find((a) => a && a.name && a.name.toLowerCase() === name.toLowerCase());
  if (same) { rt.created[sv] = same.name; qsSetState(st.id, 'done', ''); return true; }
  if (rt.firstAcct[sv]) return false;
  rt.firstAcct[sv] = true;
  if (!mine.length || rt.created[sv]) return false;
  rt.created[sv] = mine[mine.length - 1].name;
  qsSetState(st.id, 'done', t('qs.acct.mine', { name: mine[mine.length - 1].name }));
  return true;
}
// An account step waiting for a status of the run's server: read again (QS_STATUS_GAP_MS at most). A server whose agent
// has not answered for QS_ACCT_WAIT_MS asks the player — Check again, or skip the account and make it later.
function qsAcctWaitStatus(st) {
  const rt = S.qs.rt;
  qsStatusSoon();
  if (S.srv.state !== 'error' || Date.now() - (rt.since[st.id] || Date.now()) < QS_ACCT_WAIT_MS) return;
  rt.acctWhy[st.v] = 'status';
  qsSetState(st.id, 'needs', t('qs.note.noStatus'));
}
// Server Admin (a server this run set up, or an admin token): the admin's create (server.accountcreate, remembered as
// this launcher's login there), once the version runs. A starting progress the agent says is not imported goes once more
// as a new account (qsAccountRefused).
function qsPumpAccountAdmin(st) {
  const rt = S.qs.rt, p = S.qs.plan, sv = st.v;
  if (rt.act[st.id]) return;
  if (!qsOnTarget()) { qsAcctWaitStatus(st); return; }
  if (!qsFresh()) return;
  if (qsAcctMine(st)) return;
  const f = srvFacts(sv);
  if (!f || !f.up) {
    if (p.server.kind === 'existing') qsSetState(st.id, 'skipped', t('qs.acct.notUp', { version: sv }));
    return;
  }
  if (qsServerBusy()) return;
  const name = qsAcctName(sv);
  if (!ACCOUNT_NAME_RE.test(name)) { qsSetState(st.id, 'needs', t('qs.note.accountName', { reason: t('toast.accountBadName') })); return; }
  // A server that verifies passwords: the one typed (this launcher shows it as "the one you chose" at every PLAY).
  const verify = passwordVerified(sv);
  if (verify && !ACCOUNT_PASSWORD_RE.test(S.qs.mem.acct)) { rt.needPw[sv] = true; qsSetState(st.id, 'needs', t('qs.note.passwordAgain')); return; }
  const want = (p.account.create[sv] && p.account.create[sv].template) || 'fresh';
  const template = rt.tplFresh[sv] ? 'fresh' : want;
  qsRunJob(st, 'accountcreate', sv, { name, password: verify ? S.qs.mem.acct : '', template, remember: true });
}
// Player mode: the player's own create (account.create), once the server offers it on the running version.
function qsPumpAccountPlayer(st) {
  const rt = S.qs.rt, sv = st.v, pub = S.pub.status;
  if (rt.act[st.id] || S.qs.acct) return;
  if (!qsOnTarget() || !pub) { qsAcctWaitStatus(st); return; }
  const name = qsAcctName(sv);
  if (qsAcctMine(st)) return;
  const su = pub.signup;
  if (!su || !su.enabled) { qsSetState(st.id, 'skipped', t('qs.acct.signupOff')); return; }
  const offer = acctOffer(sv);
  if (!offer || !acctUp(sv)) { qsSetState(st.id, 'skipped', t('qs.acct.notUp', { version: sv })); return; }
  const lim = acctLimit(sv);
  if (lim !== null && acctLive(sv).length >= lim) { qsSetState(st.id, 'skipped', t('qs.acct.limit', { max: lim, version: sv })); return; }
  if (!ACCOUNT_NAME_RE.test(name)) { qsSetState(st.id, 'needs', t('qs.note.accountName', { reason: t('toast.accountBadName') })); return; }
  const want = (S.qs.plan.account.create[sv] && S.qs.plan.account.create[sv].template) || '';
  const template = offer.some((x) => x.id === want) ? want : ((offer.find((x) => x.id !== 'fresh') || offer[0]).id);
  const verifies = !!(pub.versions && pub.versions[sv] && pub.versions[sv].passwordVerify);
  const password = verifies ? S.qs.mem.acct : '';
  if (verifies && !ACCOUNT_PASSWORD_RE.test(password)) { rt.needPw[sv] = true; qsSetState(st.id, 'needs', t('qs.note.passwordAgain')); return; }
  rt.act[st.id] = 'sent';
  S.qs.acct = { version: sv, name, step: st.id };
  rpc('account.create', { version: sv, name, password, template }).catch((e) => {
    if (S.qs.acct && S.qs.acct.step === st.id) S.qs.acct = null;
    rt.act[st.id] = '';
    qsAccountRefused(st, e.message, null);
    qsTickSoonQs();
  });
}
function qsAccountMade(st, res, job) {
  const rt = S.qs.rt, sv = st.v, r = res || {};
  const name = typeof r.name === 'string' && r.name ? r.name : qsAcctName(sv);
  if (r.passwordGenerated && typeof r.password === 'string' && r.password) S.qs.mem.generated[sv] = r.password;
  rt.created[sv] = name;
  qsSetState(st.id, 'done', job && job.template && job.template !== ((S.qs.plan.account.create[sv] || {}).template) ? t('qs.note.templateFresh') : '');
}
// A refused account: another name is asked for when the name is the reason; a starting progress the server does not
// have yet is replaced by a new account once; anything else is the step's failure.
function qsAccountRefused(st, msg, job) {
  const rt = S.qs.rt, sv = st.v, text = String(msg || t('common.unknownError'));
  delete rt.acctWhy[sv];
  // (the player route's texts are the launcher's own; the admin job's are the agent's words: "name taken", "that name is
  // reserved", "name must be 3-20 characters…")
  const nameTexts = [t('core.agent.err.name_taken'), t('core.agent.err.name_reserved'), t('core.agent.err.name_invalid')];
  if (nameTexts.includes(text) || /\bname (taken|is reserved|must be)\b/i.test(text)) { qsSetState(st.id, 'needs', t('qs.note.accountName', { reason: text })); return; }
  const tpl = job ? job.template : '';
  if (!rt.tplFresh[sv] && ((tpl && tpl !== 'fresh' && /template/i.test(text)) || text === t('core.agent.err.template_not_ready') || text === t('core.agent.err.template_unavailable'))) {
    rt.tplFresh[sv] = true;
    qsSetNote(st.id, t('qs.note.templateFresh'));
    return;
  }
  if (text === t('core.agent.err.password_required') || text === t('core.agent.err.password_invalid') || /\bpassword of 8-64\b/i.test(text)) {
    rt.needPw[sv] = true; qsSetState(st.id, 'needs', t('qs.note.passwordAgain')); return;
  }
  qsSetState(st.id, 'failed', text);
}

// ── the end of the server thread ──
function qsPumpReady(st, rec) {
  const rt = S.qs.rt, p = S.qs.plan;
  if (p.server.kind === 'existing') { qsPumpReadyExisting(st); return; }
  if (rec.state === 'needs') return;
  const last = qsLastVersion(), f = last && qsOnTarget() ? srvFacts(last) : null;
  if (f && f.up && f.loading) qsSetNote(st.id, t('qs.note.loading'));
  if (Date.now() - (rt.since[st.id] || Date.now()) > QS_LOADING_MS) { qsSetState(st.id, 'needs', t('qs.note.loadingLong')); return; }
  qsStatusSoon();
}
// An existing server: its game port answers, and its agent where an account was asked for (server.test).
async function qsPumpReadyExisting(st) {
  const rt = S.qs.rt, s = S.qs.plan.server;
  if (rt.act[st.id] || qsRec(st.id).state !== 'running') return;
  rt.act[st.id] = 'sent';
  try {
    const r = await rpc('server.test', { address: s.address, agentPort: s.agentPort > 0 ? s.agentPort : 0 });
    rt.ready = r;
    const okGame = !!(r && r.game && r.game.reachable === true), okAgent = !!(r && r.agent && r.agent.reachable);
    const wantAgent = Object.keys(S.qs.plan.account.create || {}).length > 0;
    if (okGame && (okAgent || !wantAgent)) qsSetState(st.id, 'done', '');
    else qsSetState(st.id, 'needs', okGame ? t('qs.server.noAgent') : t('qs.server.notReachable'));
  } catch (e) { qsSetState(st.id, 'needs', e.message); }
  rt.act[st.id] = '';
  qsTickSoonQs();
}
// The reach check of a server players on the Internet join: TCP from this PC; a fail asks, never fails.
function qsReachAddress() {
  const s = S.qs.plan.server;
  return s.kind === 'windows' ? s.advertisedIp : (s.advertisedIp || hostOf(s.ssh.host));
}
async function qsPumpReach(st) {
  const rt = S.qs.rt, s = S.qs.plan.server;
  if (rt.act[st.id]) return;
  const address = qsReachAddress(), agentPort = s.agentPort > 0 ? s.agentPort : 18080;
  const inside = s.kind === 'windows' ? true : qsPrivateV4(hostOf(s.ssh.host));
  rt.act[st.id] = 'sent';
  try {
    const r = await rpc('quickstart.reach', { address, agentPort, inside });
    rt.reach = r;
    qsSetState(st.id, r && r.verdict === 'ok' ? 'done' : 'needs', qsReachText(r, address, agentPort));
  } catch (e) { qsSetState(st.id, 'needs', e.message); }
  rt.act[st.id] = 'ended';
  qsTickSoonQs();
}
// The text of a reach answer (ReachResult.TextKey names one of the qs.reach.* keys).
function qsReachText(r, ip, agentPort) {
  const v = { ip, agentPort };
  switch (r && r.textKey) {
    case 'qs.reach.okInside': return t('qs.reach.okInside', v);
    case 'qs.reach.okOutside': return t('qs.reach.okOutside', v);
    case 'qs.reach.partial': return t('qs.reach.partial', v);
    case 'qs.reach.failInside': return t('qs.reach.failInside', v);
    default: return t('qs.reach.failOutside', v);
  }
}

// ── the run's log, details and live parts ──
function qsLogLine(th, text) {
  const rt = S.qs.rt;
  if (!rt || !text) return;
  const lines = rt.lines[th];
  if (lines[lines.length - 1] === text) return;
  lines.push(text);
  while (lines.length > QS_LOG_MAX) lines.shift();
  if (S.screen !== 'quickstart' || !S.qs.logOpen[th]) return;
  const el = qsLive('log:' + th);
  if (el) {
    const pinned = el.scrollTop + el.clientHeight >= el.scrollHeight - 4;
    el.insertAdjacentHTML('beforeend', `<div>${esc(text)}</div>`);
    while (el.childElementCount > QS_LOG_MAX) el.removeChild(el.firstElementChild);
    if (pinned) el.scrollTop = el.scrollHeight;
  }
}
function qsDetail(id, text) {
  const rt = S.qs.rt;
  if (!rt || !qsHas(id)) return;
  rt.detail[id] = String(text || '');
  qsLiveRun();
}
// The node of a live part of the page, by its key (data-live="qs-<key>": the page's own, beside app.js's data-live ids).
function qsLive(key) {
  const want = 'qs-' + key;
  for (const el of document.querySelectorAll('#app [data-live^="qs-"]')) if (el.getAttribute('data-live') === want) return el;
  return null;
}
function qsPut(key, html) {
  const el = qsLive(key);
  if (!el) return false;
  if (el.relicQs !== html) { el.innerHTML = html; el.relicQs = html; }
  return true;
}
// The progress page follows in place: the threads' lines, badges and bars, the steps' details (a step that changes its
// state redraws the page — qsTick).
function qsLiveRun() {
  const rt = S.qs.rt;
  if (S.screen !== 'quickstart' || S.qs.page !== 'progress' || !rt) return;
  ['game', 'server'].forEach((th) => {
    if (!rt.steps.some((s) => s.thread === th && s.row)) return;
    const sum = qsThreadSum(th);
    qsPut('line:' + th, sum.line);
    qsPut('badge:' + th, `${qsStateText(sum.state)}${sum.state === 'working' ? ` · ${sum.pct}%` : ''}`);
    const bar = qsLive('bar:' + th);
    if (bar) bar.style.width = sum.pct + '%';
  });
  rt.steps.forEach((st) => { if (rt.detail[st.id] != null) qsPut('d:' + st.id, esc(rt.detail[st.id])); });
}

// ── the facts this page asks for ──
// quickstart.check {plan, box}: one look at a time, the next one asked while one runs made after it; never on the 2 s
// tick (it runs several processes) — on the pages that show it and at the changes of the run's steps.
let qsCheckTimer = 0;
function qsCheckSoon(delay) {
  clearTimeout(qsCheckTimer);
  qsCheckTimer = setTimeout(() => { qsCheckTimer = 0; qsCheckRun(); }, delay == null ? 0 : delay);
}
async function qsCheckRun() {
  const c = S.qs.check;
  if (c.busy) { c.again = true; return; }
  c.busy = true; c.again = false;
  const seq = ++c.seq;
  const d = S.qs.draft, plan = S.qs.plan || (d ? qsPlanOf(d) : null);
  const facts = !S.qs.plan && d && d.server.kind === 'linux' ? d.server.probe.facts : null;
  let r = null;
  try { r = await rpc('quickstart.check', Object.assign({ plan: qsPlanForDisk(plan) }, facts ? { box: facts } : {})); } catch (e) { r = null; }
  c.busy = false;
  if (seq === c.seq && r && typeof r === 'object') { c.res = r; c.n++; c.at = Date.now(); }
  if (c.again) { qsCheckRun(); return; }
  qsAfterFacts();
}
// localagent.status (this PC's agent: its folders, host, rules), the same way.
let qsLaTimer = 0;
function qsLaSoon(delay) {
  if (!S.isWindows) return;
  clearTimeout(qsLaTimer);
  qsLaTimer = setTimeout(() => { qsLaTimer = 0; qsLaRun(); }, delay == null ? 0 : delay);
}
async function qsLaRun() {
  const la = S.qs.la;
  if (la.busy) { la.again = true; return; }
  la.busy = true; la.again = false;
  try { const r = await rpc('localagent.status'); if (r && typeof r === 'object') { la.res = r; la.n++; } } catch (e) { /* the last answer stays */ }
  la.busy = false;
  if (la.again) { qsLaRun(); return; }
  qsAfterFacts();
}
// The status of the run's server read again — at most every QS_STATUS_GAP_MS unless asked now.
function qsStatusSoon(now) {
  const rt = S.qs.rt;
  if (!rt || !S.serverConfigured) return;
  if (!now && Date.now() - rt.statusAt < QS_STATUS_GAP_MS) return;
  rt.statusAt = Date.now();
  refreshServersQuiet();
}
// New facts in: a wizard page draws them (its rows follow), a run judges its steps again.
function qsAfterFacts() {
  const d = S.qs.draft;
  if (d && !S.qs.plan && S.qs.page === 'server') qsSyncVers(d);
  if (d && !S.qs.plan && S.qs.page === 'account') qsAcctPrefill(d);
  if (S.qs.plan && S.qs.rt) { S.qs.rt.dirty = true; qsTick(); return; }
  if (S.screen === 'quickstart') renderOrOwe();
}
function qsAcctPrefill(d) {
  const m = qsMachine();
  if (!d.account.touched && !d.account.name && m && m.userName) d.account.name = m.userName;
}

// ── saving the plan ──
// At Start, at every change of a step's state (debounced 500 ms), before the installs the backend gates on the stored plan,
// at Stop, before the restart, at the end. One save after another, each of the plan as it stands then.
let qsSaveTimer = 0, qsSaveChain = Promise.resolve();
function qsSaveSoon() {
  if (!S.qs.plan) return;
  clearTimeout(qsSaveTimer);
  qsSaveTimer = setTimeout(() => { qsSaveTimer = 0; qsSaveNow(); }, 500);
}
// Resolves to {written, plan, error}: written = the file holds the plan (the answer came, or only the resume value could
// not be armed — backend.qs.resumeFailed, after which the file IS written), plan = the stored plan as answered.
function qsSaveNow() {
  clearTimeout(qsSaveTimer); qsSaveTimer = 0;
  qsSaveChain = qsSaveChain.then(qsSaveOnce, qsSaveOnce);
  return qsSaveChain;
}
async function qsSaveOnce() {
  const p = S.qs.plan;
  if (!p) return { written: false, plan: null, error: '' };
  const resumePre = t('backend.qs.resumeFailed', { error: '\u0001' }).split('\u0001')[0];
  let r = null, error = '', written = false;
  for (let attempt = 1; attempt <= 2; attempt++) {
    try { r = await rpc('quickstart.save', { plan: qsPlanForDisk(p) }); written = true; error = ''; break; }
    catch (e) {
      error = (e && e.message) || t('common.unknownError');
      const resume = !!resumePre && error.indexOf(resumePre) === 0;
      written = written || resume;
      // the resume value could not be armed: once more after a moment
      if (resume && attempt === 1) { await new Promise((ok) => setTimeout(ok, 1000)); continue; }
      break;
    }
  }
  if (error && S.qs.rt) qsLogLine('server', error);
  const stored = r && r.plan && typeof r.plan === 'object' ? r.plan : null;
  if (stored) {
    if (S.qs.plan === p && typeof stored.id === 'string' && stored.id) { p.id = stored.id; if (!p.startedAt && stored.startedAt) p.startedAt = stored.startedAt; }
    S.qsPlan = stored.state === 'done' ? null : stored;
  }
  return { written, plan: stored, error };
}

// ─────────────────────────── the run's beginning, its resume, its end ───────────────────────────
// Start (the summary's): the plan of the draft written, then the runner.
async function qsStartRun() {
  const d = S.qs.draft;
  if (!d || S.qs.busy || qsStartBlock(d)) return;
  const p = qsPlanOf(d);
  p.id = ''; p.state = 'running'; p.startedAt = new Date().toISOString(); p.steps = {};
  S.qs.busy = 'start';
  qsFindHalt();
  S.qs.plan = p;
  const rt = S.qs.rt = qsRtNew(false);
  rt.agentHere = p.server.kind === 'windows' && qsAgentOnPc();
  rt.changedKey = p.server.kind === 'linux' && !!d.server.probe.changed;
  rt.steps = qsStepsOf(p, rt);
  rt.steps.forEach((st) => qsRec(st.id));
  S.qs.open = { game: null, server: null };
  // (nothing is started before the plan is written: a plan the backend does not take means a run that never began)
  rt.starting = true;
  const saved = await qsSaveNow();
  S.qs.busy = '';
  if (!saved.written) {
    // the plan was not taken: nothing has started — the summary stays, with the backend's reason
    S.qs.plan = null; S.qs.rt = null;
    showToast(saved.error || t('common.unknownError'), 'bad');
    render();
    return;
  }
  rt.starting = false;
  S.qs.page = 'progress';
  render();
  qsTick();
  qsStatusSoon(true);
}
// A run that is not ended taken up again (boot, after a restart of Windows or of the launcher; the start screen's
// Continue): its plan as stored, whose states are hints of what was started (I57) — a step that ran, was paused or asked
// the player goes again (the restart's question excepted: the look at this PC answers it), and one it records as done is
// taken so only once the facts agree (qsHintsJudge). An agent on this PC is the run's agent from here (localagent.enter:
// started when it does not run).
function qsResume(plan) {
  if (!plan || typeof plan !== 'object') return;
  let p;
  try { p = JSON.parse(JSON.stringify(plan)); } catch (e) { return; }
  if (!p.game || !p.server || !p.account) return;
  if (!p.steps || typeof p.steps !== 'object') p.steps = {};
  Object.keys(p.steps).forEach((id) => {
    const r = p.steps[id];
    if (r && (r.state === 'running' || r.state === 'paused' || (r.state === 'needs' && id !== 's.restart'))) { r.state = 'pending'; r.note = ''; }
  });
  (p.game.versions || []).forEach((g) => {
    if (!g || isInstalled(g.id)) return;
    ['get', 'unpack', 'patch'].forEach((k) => { const r = p.steps[`g.${g.id}.${k}`]; if (r && r.state === 'done') r.state = 'pending'; });
  });
  p.state = 'running';
  S.qs.plan = p;
  const was = S.qs.rt;
  const rt = S.qs.rt = qsRtNew(true);
  // (an agent install or a replacement of the agent this run started that still runs — Stop, then Continue — is the
  //  run's still: its end comes here, never a second one)
  if (was && was.deploying) { rt.deploying = true; rt.ownWrite = 1; rt.act['s.agent'] = 'sent'; }
  if (was && was.act['s.agentUpdate'] === 'sent') rt.act['s.agentUpdate'] = 'sent';
  rt.agentHere = p.server.kind === 'windows' && qsAgentOnPc();
  rt.steps = qsStepsOf(p, rt);
  rt.steps.forEach((st) => qsRec(st.id));
  rt.steps.forEach((st) => { if (QS_HINT_KINDS.includes(st.kind) && qsRec(st.id).state === 'done') rt.hints[st.id] = true; });
  rt.hintAt = Date.now(); rt.checkN = S.qs.check.n; rt.laN = S.qs.la.n; rt.gen = srvGen;
  // (nothing is started before the plan is written again: the resume value is armed anew with it)
  rt.starting = true;
  S.qs.page = 'progress';
  S.qs.open = { game: null, server: null };
  // a run stopped while one of this PC's installs ran: that install's outcome is the run's
  rpc('quickstart.get').then((r) => {
    const what = r && typeof r.installing === 'string' ? r.installing : '';
    if (what && !S.qs.setup && QS_SETUP_STEP[what]) S.qs.setup = { what, step: QS_SETUP_STEP[what] };
  }).catch(() => {});
  qsSaveNow().then(() => { rt.starting = false; qsCheckSoon(); if (p.server.kind === 'windows') qsLaSoon(); qsStatusSoon(true); qsTick(); });
}
// Stop: nothing more is started — the downloads queued in the chain an install left or the run started included (a job
// of it that runs ends on its own; the package steps take the rest up when the run goes on) —, a game download is
// paused, a server download stopped, every other job left to finish; the plan saved paused; the start screen, or the
// shell in Server Admin mode.
async function qsStopRun(note) {
  const rt = S.qs.rt, p = S.qs.plan;
  if (!rt || !p) return;
  rt.stopped = true;
  // (while Quick Start's screen is exclusive the chain is the run's own)
  S.fetchChain = null;
  if (S.qs.inst && !S.qs.inst.stop) { S.qs.inst.stop = 'pause'; rpc('install.cancel').catch(() => {}); }
  if (jobRunning() && S.srvJob.kind === 'fetch') { rt.fetchCancelled[String(S.srvJob.version)] = true; rpc('server.fetch.cancel', { version: S.srvJob.version }).catch(() => {}); }
  p.state = 'paused';
  await qsSaveNow();
  if (note) S.notice = { title: t('qs.title'), body: note };
  qsLeave();
}
// A change of the server Relic is connected to that the run did not make: the run stops, and says so.
function qsRetargetStop() {
  const rt = S.qs.rt;
  if (!rt || rt.stopped) return;
  qsStopRun(t('qs.note.retarget'));
}
// Out of Quick Start's screen: into the shell when Relic is in Server Admin mode with its server, else the start screen.
async function qsLeave() {
  if (S.mode === 'admin' && S.serverConfigured) {
    let d = null;
    try { d = await rpc('app.init'); } catch (e) { d = null; }
    enterShell(d);
    return;
  }
  S.screen = 'login'; S.login.panel = null;
  onceForget('login:');
  render();
}
// Restart Windows now: the game download paused and waited for, a server download stopped and waited
// for, the downloads queued behind it dropped, the plan saved waiting for the restart, then the restart — asked again
// when the backend has not armed the resume yet.
async function qsRestartGo() {
  const rt = S.qs.rt, p = S.qs.plan;
  if (!rt || !p || rt.restarting || rt.stopped) return;
  // (from here the runner starts nothing — qsRunning — and Stop waits: the plan is saved for the restart; nor does the
  //  chain of downloads: the package steps take them up when the run goes on — after the restart, or once a restart that
  //  did not come is taken back)
  rt.restarting = true;
  rt.restartPaused = {};
  S.fetchChain = null;
  qsSetNote('s.restart', t('qs.note.restarting'));
  render();
  const waitFor = async (cond) => { for (let i = 0; i < 240 && !cond(); i++) await new Promise((ok) => setTimeout(ok, 250)); };
  if (S.qs.inst) { if (!S.qs.inst.stop) { S.qs.inst.stop = 'pause'; rt.restartPaused.game = S.qs.inst.versionId; rpc('install.cancel').catch(() => {}); } await waitFor(() => !S.qs.inst); }
  if (jobRunning() && S.srvJob.kind === 'fetch') { const j = S.srvJob; rt.fetchCancelled[String(j.version)] = true; rt.restartPaused.fetch = String(j.version); rpc('server.fetch.cancel', { version: j.version }).catch(() => {}); await waitFor(() => !j.running); }
  if (S.qs.rt !== rt || rt.stopped) return;
  p.state = 'waitingRestart';
  await qsSaveNow();
  for (let i = 0; i < 6; i++) {
    try { await rpc('quickstart.restart', {}); return; }
    catch (e) {
      if (e.message === t('backend.qs.restartUnsaved') && i < 5) { await new Promise((ok) => setTimeout(ok, 1500)); continue; }
      // Windows was not asked: the run goes on as it was — what the restart paused included
      qsRestartBack(rt, qsRestartNote());
      showToast(e.message, 'bad');
      return;
    }
  }
}
// The run goes on after a restart that did not happen (refused, or held by Windows): running again, its resume still
// armed, what Restart Windows now paused taken up again — the game download, the server download —, the restart's
// question asked again with `note`.
function qsRestartBack(rt, note) {
  const p = S.qs.plan;
  if (S.qs.rt !== rt || !p) return;
  rt.restarting = false;
  p.state = 'running';
  const g = rt.restartPaused.game, f = rt.restartPaused.fetch;
  rt.restartPaused = {};
  if (g) { const id = qsGameStepOf(g); if (id && qsRec(id).state === 'paused') { rt.act['inst:' + g] = ''; qsSetState(id, 'pending', ''); } }
  if (f && qsHas(`s.${f}.package`) && qsRec(`s.${f}.package`).state === 'paused') { rt.act[`s.${f}.package`] = ''; qsSetState(`s.${f}.package`, 'pending', ''); }
  qsSetNote('s.restart', note);
  qsSaveNow();
  qsTick();
}
// Windows asks the open programs to close before it restarts: one with unsaved work can hold the restart, and the user
// may cancel it there. A page still alive QS_RESTART_HELD_MS after the restart was asked takes it as held: the run goes
// on, and its restart is asked for again.
function qsRestartHeld(rt) {
  setTimeout(() => {
    const p = S.qs.plan;
    if (S.qs.rt !== rt || !rt.restarting || !p || p.state !== 'waitingRestart') return;
    qsRestartBack(rt, t('qs.note.restartHeld'));
  }, QS_RESTART_HELD_MS);
}
// The restart's note: the game download named only while one is to come or under way (it is paused for the restart).
function qsRestartNote() {
  const rt = S.qs.rt;
  const dl = !!rt && rt.steps.some((st) => st.kind === 'get' && !st.local && ['pending', 'running', 'paused'].includes(qsRec(st.id).state));
  return dl ? t('qs.note.restart') : t('qs.note.restartNoGame');
}
// The progress page's Continue: the run is over — the plan file goes (a done plan never stays on disk) — and the Done page.
async function qsContinue() {
  const p = S.qs.plan;
  if (!p || !qsEnded()) return;
  p.state = 'done';
  await qsSaveNow();
  S.qs.rt.stopped = true;
  S.qs.page = 'done';
  render();
}
// The Done page's Continue: the server on the start screen's saved servers (no token, an agent port
// only when it is not 18080), the plan cleared, the shell — the Overview in Server Admin mode, the Library else.
async function qsFinish() {
  const p = S.qs.plan;
  if (!p || S.qs.busy) return;
  S.qs.busy = 'finish';
  render();
  // (only a server the run reached, in the mode it wanted, goes into the saved servers — one it did not reach is none to
  //  connect to)
  const connected = qsDoneConnected();
  const admin = S.mode === 'admin' && S.serverConfigured;
  const address = qsSavedAddress();
  const agentPort = p.server.agentPort && p.server.agentPort !== 18080 ? p.server.agentPort : 0;
  if (connected) {
    try {
      const r = await rpc('servers.save', { address, mode: admin ? 'admin' : 'player', label: qsSavedLabel(), agentPort });
      if (r && Array.isArray(r.servers)) S.savedServers = r.servers;
    } catch (e) { showToast(e.message, 'bad'); }
  }
  try { const r = await rpc('quickstart.clear', {}); S.qsPlan = r && r.plan ? r.plan : null; } catch (e) { /* a done plan is not on disk anyway */ }
  let d = null;
  try { d = await rpc('app.init'); } catch (e) { d = null; }
  S.qs = qsBlank();
  // A run that did not reach its server (its connecting step did not make it): the start screen.
  if (!connected) {
    applyInit(d);
    S.screen = 'login'; S.login.panel = null;
    onceForget('login:');
    render();
    return;
  }
  enterShell(d);
  if (admin) ACTIONS.serverOverview();
}

// ─────────────────────────── the events of the run's jobs ───────────────────────────
// app.js hands the events of the jobs a run may start to this, one guarded line in each handler. True = the event is the
// run's and the handler leaves it (the game install the run started — qsOwnsInstall —, the account it is creating, its
// search of this PC); the others are observed, and the handler goes on.
function qsEvent(name, d) {
  try {
    switch (name) {
      case 'install.progress': case 'install.done': case 'install.error': case 'install.cancelled': return qsInstallEvent(name, d);
      case 'findgames.progress': return qsFindEvent('progress', d);
      case 'findgames.found': return qsFindEvent('found', d);
      case 'findgames.done': return qsFindEvent('done', d);
      case 'account.log': case 'account.done': case 'account.error': return qsAccountEvent(name, d);
      case 'server.log': qsServerLog(d); return false;
      case 'server.done': qsServerEnd(true, d); return false;
      case 'server.error': qsServerEnd(false, d); return false;
      case 'deploy.log': case 'deploy.done': case 'deploy.error': qsDeployEvent(name, d); return false;
      case 'agentupdate.log': case 'agentupdate.done': case 'agentupdate.error': qsAgentUpdateEvent(name, d); return false;
      default: return false;
    }
  } catch (e) {
    console.error(e);
    return name.indexOf('install.') === 0 ? !!S.qs.inst : false;
  }
}
// The run started the game install that runs (its events are the run's until its outcome).
function qsOwnsInstall() { return !!(S.qs && S.qs.inst); }
function qsInstallEvent(name, d) {
  const inst = S.qs.inst;
  if (!inst) return false;
  const v = inst.versionId, rt = S.qs.rt;
  if (name === 'install.progress') {
    inst.phase = String((d && d.phase) || '');
    inst.fraction = Number(d && d.fraction) || 0;
    inst.message = String((d && d.message) || '');
    if (rt) {
      const was = rt.instPhase[v];
      rt.instPhase[v] = inst.phase;
      if (inst.phase === 'Fiddler') rt.fiddlerNote = inst.message;
      qsLogLine('game', inst.message);
      const id = qsGameStepForPhase(v, inst.phase) || qsGameStepOf(v);
      if (id) rt.detail[id] = inst.message;
      if (was !== inst.phase) qsTickSoonQs(); else qsLiveRun();
    }
    return true;
  }
  S.qs.inst = null;
  if (rt) rt.act['inst:' + v] = '';
  if (name === 'install.done') {
    if (d && d.state) applyInit(d.state);
    if (rt) rt.instPhase[v] = 'Done';
    ['get', 'unpack', 'patch'].forEach((k) => { if (qsHas(`g.${v}.${k}`)) qsSetState(`g.${v}.${k}`, 'done', ''); });
  } else if (name === 'install.error') {
    const id = qsGameStepOf(v);
    if (id) qsSetState(id, 'failed', (d && d.message) || t('common.unknownError'));
  } else {
    const id = qsGameStepOf(v);
    if (id) qsSetState(id, 'paused', '');
  }
  qsTickSoonQs();
  return true;
}
// account.create's events (the backend echoes the version): the run's own while it creates there.
function qsAccountEvent(name, d) {
  const a = S.qs.acct;
  if (!a || !d || String(d.version) !== String(a.version)) return false;
  if (name === 'account.log') { qsLogLine('server', String(d.text || '')); qsDetail(a.step, String(d.text || '')); return true; }
  S.qs.acct = null;
  const st = qsStep(a.step), rt = S.qs.rt;
  if (rt) rt.act[a.step] = '';
  if (name === 'account.done') {
    if (d.state) applyInit(d.state);
    const r = d.result || {};
    if (rt) rt.created[a.version] = typeof r.name === 'string' && r.name ? r.name : a.name;
    if (st) qsSetState(st.id, 'done', '');
  } else if (st) qsAccountRefused(st, d.message, null);
  qsTickSoonQs();
  return true;
}
function qsServerLog(d) {
  if (!S.qs.plan || !S.qs.rt || !d) return;
  const kind = String(d.kind || ''), v = String(d.version || ''), text = String(d.text || '');
  if (kind === 'fetch' && v && !S.qs.rt.stopped) qsFetchedByRun(v);
  qsLogLine('server', v ? `${v} · ${text}` : text);
  const job = S.qs.job;
  const id = job && job.kind === kind && String(job.version) === v ? job.step : (kind === 'fetch' || kind === 'relocate') && qsHas(`s.${v}.package`) ? `s.${v}.package` : '';
  if (id) qsDetail(id, text);
}
// An agent install's events, observed before app.js's handlers (Quick Start's install draws no overlay; the handler
// takes its init state in and builds the chain of what the install left). Never the token the event carries.
function qsDeployEvent(name, d) {
  const rt = S.qs.rt;
  if (!S.qs.plan || !rt) return;
  if (name === 'deploy.log') { const text = String((d && d.text) || ''); qsLogLine('server', text); qsDetail('s.agent', text); return; }
  if (!rt.deploying) return;
  rt.deploying = false;
  qsOwnWriteDone(rt);
  if (name === 'deploy.error') {
    rt.act['s.agent'] = '';
    // a Linux install's Retry asks for the login again first (a password typed wrong would fail it the same way)
    if (S.qs.plan.server.kind === 'linux') rt.askLogin = true;
    qsSetState('s.agent', 'failed', (d && d.message) || t('common.unknownError'));
  } else {
    // (the agent step is done once the handler after this took the install's init state in: qsFact)
    rt.act['s.agent'] = 'ok';
    if (d && typeof d.warning === 'string' && d.warning) qsLogLine('server', d.warning);
    if (S.qs.plan.server.kind === 'windows') qsLaSoon(0);
    // A run stopped while its install ran starts nothing more: the jobs the install leaves (the downloads its
    // handler would chain) are left out of the event it hands on — the package steps take them up at Continue.
    if (rt.stopped && d && typeof d === 'object') { d.chain = []; d.fetch = []; }
  }
  setTimeout(qsTickSoonQs, 0);
}
function qsAgentUpdateEvent(name, d) {
  const rt = S.qs.rt;
  if (!rt || rt.act['s.agentUpdate'] !== 'sent') return;
  if (name === 'agentupdate.log') { const text = String((d && d.text) || ''); qsLogLine('server', text); qsDetail('s.agentUpdate', text); return; }
  rt.act['s.agentUpdate'] = 'ended';
  if (name === 'agentupdate.done') { qsSetState('s.agentUpdate', 'done', ''); loadAgentBuild().then(qsTickSoonQs, qsTickSoonQs); }
  else { rt.act['s.agentUpdate'] = ''; qsSetState('s.agentUpdate', 'failed', (d && d.message) || t('common.unknownError')); }
  qsLaSoon(500);
  qsTickSoonQs();
}

// ── "Find it on this PC" of the Game page (install.findGames; the events name their search) ──
let qsFindBuf = [];
async function qsFindRun(deeper) {
  const f = S.qs.find;
  if (qsFindLive()) return;
  if (!deeper) f.hits = [];
  Object.assign(f, { search: 0, starting: true, stopping: false, progress: null, done: null, error: '' });
  qsFindBuf = [];
  qsFindSync();
  const payload = deeper ? { deeper: true } : {};
  let n = 0, error = '';
  try {
    let r = await rpc('install.findGames', payload);
    // 0 = a search runs already that this page does not follow: stopped, and this one asked for once more
    if (!(r && Number(r.search) > 0) && !f.stopping) {
      await rpc('install.findGamesStop').catch(() => null);
      r = f.stopping || S.qs.find !== f ? null : await rpc('install.findGames', payload);
    }
    n = r && Number(r.search) > 0 ? Number(r.search) : 0;
  } catch (e) { error = (e && e.message) || String(e); }
  if (S.qs.find !== f) return;
  const buf = qsFindBuf; qsFindBuf = [];
  f.starting = false;
  if (!n) { f.stopping = false; f.error = error; qsFindSync(); return; }
  f.search = n;
  buf.forEach(([kind, x]) => qsFindApply(kind, x));
  if (f.stopping && !f.done) { f.stopping = false; qsFindHalt(); return; }
  qsFindSync();
}
async function qsFindHalt() {
  const f = S.qs.find;
  if (!qsFindLive() || f.stopping) return;
  f.stopping = true;
  qsFindSync();
  try { await rpc('install.findGamesStop'); } catch (e) { /* its end, or the wait below */ }
  setTimeout(() => {
    if (S.qs.find !== f || f.done || f.starting) return;
    f.stopping = false;
    if (f.search) f.done = { search: f.search, stopped: true, deeper: true, budget: false, found: f.hits.length, dirs: 0, error: '' };
    qsFindSync();
  }, 2000);
}
function qsFindEvent(kind, d) {
  const f = S.qs.find;
  if (!d || !(Number(d.search) > 0)) return false;
  if (f.starting) { if (qsFindBuf.length < 4000) qsFindBuf.push([kind, d]); return true; }
  if (Number(d.search) !== f.search || !f.search) return false;
  if (qsFindApply(kind, d)) qsFindSync();
  return true;
}
function qsFindApply(kind, d) {
  const f = S.qs.find;
  if (Number(d.search) !== f.search || f.done) return false;
  if (kind === 'progress') { f.progress = d; return true; }
  if (kind === 'found') {
    if (typeof d.dir !== 'string' || !d.dir) return false;
    const at = f.hits.findIndex((h) => sameDirText(h.dir, d.dir));
    if (at >= 0) f.hits[at] = d; else f.hits.push(d);
    return true;
  }
  if (kind === 'done') { f.done = d; f.stopping = false; return true; }
  return false;
}
// The cards' search parts follow in place (a whole redraw would take the caret of a field).
function qsFindSync() {
  const d = S.qs.draft;
  if (!d || S.screen !== 'quickstart' || S.qs.page !== 'game') return;
  qsGameIds(d).filter((id) => qsGameFrom(d, id) === 'local').forEach((id) => { if (!qsPut('find:' + id, qsFindHtml(d, id))) renderOrOwe(); });
}

// ── a copy the player has: the checks (install.inspectGame; one at a time — a newer one cancels the one that runs) ──
let qsInspectChain = Promise.resolve(), qsInspectTimer = {};
function qsInspectSoon(id, delay) {
  clearTimeout(qsInspectTimer[id]);
  qsInspectTimer[id] = setTimeout(() => { qsInspectChain = qsInspectChain.then(() => qsInspect(id), () => qsInspect(id)); }, delay == null ? 450 : delay);
}
async function qsInspect(id) {
  const d = S.qs.draft;
  if (!d) return;
  const path = String(d.game.path[id] || '').trim(), key = qsInspectKey(d, id);
  if (!path) { delete d.game.inspect[id]; qsVerdictSync(id); return; }
  const cur = d.game.inspect[id];
  if (cur && cur.key === key && cur.phase === 'done') { qsVerdictSync(id); return; }
  d.game.inspect[id] = { key, phase: 'checking', v: null, error: '' };
  qsVerdictSync(id);
  const req = { path, versionId: id, deep: false };
  const at = qsReplaceAt(d, id);
  if (at) req.replaceInstalledAt = at;
  let v = null, error = '';
  for (let i = 0; i < 2; i++) {
    try { v = await rpc('install.inspectGame', req); } catch (e) { error = (e && e.message) || String(e); }
    if (!(v && v.status === 'cancelled')) break;
  }
  if (S.qs.draft !== d || qsInspectKey(d, id) !== key) return;
  d.game.inspect[id] = v && v.status && v.status !== 'cancelled' ? { key, phase: 'done', v, error: '' } : { key, phase: 'error', v: null, error: error || t('common.unknownError') };
  qsVerdictSync(id);
}
function qsVerdictSync(id) {
  const d = S.qs.draft;
  if (!d || S.screen !== 'quickstart' || S.qs.page !== 'game') return;
  if (!qsPut('verdict:' + id, qsVerdictHtml(d, id))) renderOrOwe();
}
// The summary's deep check of every copy the player has (only a deep check answers ready), one after the other.
async function qsCopiesCheck() {
  const d = S.qs.draft;
  for (const id of qsGameIds(d)) {
    if (qsGameFrom(d, id) !== 'local') continue;
    const key = qsInspectKey(d, id), cp = S.qs.copy[id];
    if (cp && cp.key === key && (cp.phase === 'done' || cp.phase === 'checking')) continue;
    S.qs.copy[id] = { key, phase: 'checking', v: null, error: '' };
    renderOrOwe();
    const req = { path: String(d.game.path[id] || '').trim(), versionId: id, deep: true };
    const at = qsReplaceAt(d, id);
    if (at) req.replaceInstalledAt = at;
    let v = null, error = '';
    for (let i = 0; i < 2; i++) {
      try { v = await rpc('install.inspectGame', req); } catch (e) { error = (e && e.message) || String(e); }
      if (!(v && v.status === 'cancelled')) break;
    }
    if (S.qs.draft !== d || !S.qs.copy[id] || S.qs.copy[id].key !== key) continue;
    S.qs.copy[id] = v && v.status && v.status !== 'cancelled' ? { key, phase: 'done', v, error: '' } : { key, phase: 'error', v: null, error: error || t('common.unknownError') };
    renderOrOwe();
  }
}
// A Custom server folder on this PC looked at (localagent.folderCheck), debounced while it is typed.
let qsFolderTimer = {};
function qsFolderSoon(sv, delay) {
  clearTimeout(qsFolderTimer[sv]);
  qsFolderTimer[sv] = setTimeout(async () => {
    const d = S.qs.draft, r = d && d.server.vers[sv];
    if (!r || !r.dir) return;
    const dir = r.dir;
    let res = null;
    try { res = await rpc('localagent.folderCheck', { dir, version: sv }); } catch (e) { res = null; }
    if (S.qs.draft !== d || !d.server.vers[sv] || d.server.vers[sv].dir !== dir) return;
    S.qs.folders[sv] = { dir, res: res && typeof res === 'object' ? res : null };
    if (!qsPut('folder:' + sv, qsFolderLine(d, sv))) renderOrOwe();
  }, delay == null ? 500 : delay);
}

// ── the Linux box (ssh.probe) ──
const qsLxDirs = (d) => Object.keys(d.server.vers).map((sv) => d.server.vers[sv].dir).concat((Array.isArray(S.stacks) ? S.stacks : []).filter((row) => qsServerIds(d).includes(row.id)).map(qsLxFolder))
  .filter((x, i, a) => x && x[0] === '/' && a.indexOf(x) === i).slice(0, 10);
async function qsLxProbe(lookup) {
  const d = S.qs.draft, s = d.server, h = s.ssh, m = S.qs.mem, pr = s.probe;
  const host = h.host.trim(), port = qsPortOf(h.port) > 0 ? qsPortOf(h.port) : (h.port.trim() ? -1 : 22);
  if (!host) return showToast(t('qs.server.addressFirst'), 'bad');
  if (port < 0) return showToast(t('backend.qs.sshTarget'), 'bad');
  const key = hostOf(host) + ':' + port;
  // another box: its key, its answers and whether people outside join are asked anew
  if (pr.key !== key) { Object.assign(pr, qsProbeBlank()); pr.key = key; s.public = ''; s.pubTyped = ''; }
  if (lookup) pr.lookup = true; else pr.busy = true;
  pr.error = '';
  renderOrOwe();
  // The key's read (no acceptKey) logs in nowhere: the login goes only with the key the user confirmed.
  const req = { host, port, user: h.user.trim() || 'root', dirs: qsLxDirs(d), publicIp: !!lookup };
  if (pr.confirmed) {
    req.acceptKey = pr.changed ? { fingerprint: pr.confirmed, changed: true } : { fingerprint: pr.confirmed };
    Object.assign(req, h.auth === 'key' ? { keyPath: h.keyPath.trim(), passphrase: m.phrase } : { password: m.ssh });
    if (m.sudo) req.sudoPassword = m.sudo;
  }
  let r = null;
  try { r = await rpc('ssh.probe', req); }
  catch (e) { pr.busy = false; pr.lookup = false; pr.error = e.message; renderOrOwe(); return; }
  if (S.qs.draft !== d || pr.key !== key) return;
  pr.busy = false; pr.lookup = false;
  const k = r && r.hostKey;
  if (k && k.fingerprint) S.qs.keyRead = { host, port, fingerprint: k.fingerprint };
  pr.hostKey = k || null;
  if (!r || r.phase !== 'box') {
    // phase key: the key the box presents now — known keys go on without a question
    pr.phase = 'key'; pr.access = ''; pr.facts = null;
    if (pr.confirmed && (!k || k.fingerprint !== pr.confirmed)) { pr.confirmed = ''; pr.changed = false; }
    if (k && k.verdict === 'match' && pr.tries < 2) { pr.tries++; pr.confirmed = k.fingerprint; pr.changed = false; qsLxProbe(lookup); return; }
    renderOrOwe();
    return;
  }
  pr.tries = 0;
  pr.phase = 'box'; pr.access = r.access || ''; pr.facts = r.facts || null;
  if (r.publicIp) pr.publicIp = r.publicIp;
  if (!s.public) s.public = qsIpv4(hostOf(host)) && qsPrivateV4(hostOf(host)) ? 'no' : 'yes';
  qsSyncVers(d);
  qsCheckSoon();
  renderOrOwe();
  if (!lookup && s.public === 'yes' && qsLxAccessOk(pr) && qsLxAdvertised(d) === null && !pr.publicIp) qsLxProbe(true);
}

// The SSH login was edited: what the box answered is about another login now — Connect again. Another address or port
// is another box: its key is asked anew too. (In place: a redraw would take the caret.)
function qsLxEdited(d, box) {
  const pr = d.server.probe, h = d.server.ssh;
  const port = qsPortOf(h.port) > 0 ? qsPortOf(h.port) : 22;
  if (box && pr.key && pr.key !== hostOf(h.host) + ':' + port) Object.assign(pr, qsProbeBlank());
  else if (pr.phase === 'box') { pr.phase = ''; pr.access = ''; pr.facts = null; pr.publicIp = null; }
  else return;
  if (S.qs.page === 'server') qsPut('probe', qsProbeHtml(d));
}

// ─────────────────────────── the pages' moves ───────────────────────────
function qsGoPage(page) {
  const q = S.qs, d = q.draft, was = q.page;
  if (was === 'game' && page !== 'game') qsFindHalt();
  q.page = page;
  if (page === 'server' && d) {
    qsSyncVers(d);
    if (d.server.kind === 'windows') { qsCheckSoon(); if (qsAgentOnPc()) qsLaSoon(); }
  }
  if (page === 'account' && d) qsAcctPrefill(d);
  if (page === 'summary' && d) { qsCheckSoon(); qsCopiesCheck(); if (d.server.kind === 'windows' && qsAgentOnPc()) qsLaSoon(); }
  render();
}
// What the page asks before its Next (a toast says what is missing), or '' when it may go on.
function qsPageProblem(d, page) {
  if (page === 'game') {
    const ids = qsGameIds(d);
    if (!ids.length) return t('qs.game.pickOne');
    for (const id of ids) {
      if (qsGameFrom(d, id) !== 'local') continue;
      const ins = d.game.inspect[id];
      if (!String(d.game.path[id] || '').trim() || !ins || ins.key !== qsInspectKey(d, id) || ins.phase !== 'done' || !ins.v || ins.v.status === 'refused') return t('qs.game.needCopy', { version: id });
    }
    return '';
  }
  if (page === 'server') {
    const s = d.server;
    if (!s.kind) return t('qs.server.pickOne');
    if (s.kind === 'existing') {
      if (!s.address.trim()) return t('qs.server.addressFirst');
      if (qsPortOf(s.addrPort) < 0) return t('login.badAgentPort');
      if (s.admin && !S.qs.mem.token.trim() && !tokenAppliesTo(s.address)) return t('backend.tokenRequired');
      return '';
    }
    if (s.kind === 'windows') {
      const n = qsNeeds(), blocking = n && Array.isArray(n.blocking) ? n.blocking.filter((b) => b && b.text) : [];
      if (blocking.length) return t('qs.win.blocked', { reasons: blocking.map((b) => b.text).join(' ') });
      if (qsWinBlocked()) return t('qs.win.checking');
      if (!d.express && !qsAgentOnPc()) {
        if (qsPortOf(s.agentPort) < 0) return t('login.badAgentPort');
        const m = qsMachine();
        if (s.reach === 'internet' && m && m.lanIp && !qsWinPublic(d)) return t('qs.server.publicNeeded');
      }
      return qsVerProblem(d);
    }
    if (!qsLxAccessOk(s.probe)) return t('qs.lx.connectFirst');
    if (s.public === 'yes' && qsLxAdvertised(d) === null) return t('qs.server.publicNeeded');
    if (!d.express && s.bind === 'fixed' && !qsIpv4(s.bindIp)) return t('qs.lx.bindBad');
    if (!d.express && qsPortOf(s.agentPort) < 0) return t('login.badAgentPort');
    return qsVerProblem(d);
  }
  if (page === 'account') {
    const ids = qsAcctIds(d).filter((sv) => qsAcctOn(d, sv) && !qsAcctNoAgent(d));
    if (!ids.length) return '';
    if (!qsNameOk(d, d.account.name.trim())) return t('toast.accountBadName') + ' ' + qsNameRules(d);
    if (ids.some((sv) => qsVerifies(d, sv)) && !ACCOUNT_PASSWORD_RE.test(S.qs.mem.acct)) return t('toast.accountBadPassword');
    return '';
  }
  return '';
}
// A Custom row that cannot be set up as it stands: a folder of a server the player has that holds none, a folder the
// agent here could not use.
function qsVerProblem(d) {
  for (const sv of Object.keys(d.server.vers)) {
    const r = d.server.vers[sv];
    if (!r.on || r.choice === 'keep') continue;
    if (!String(r.dir || '').trim()) return t('deploy.stacks.installInto', { version: sv });
    const fc = S.qs.folders[sv];
    if (d.server.kind === 'windows' && fc && fc.res && sameFolder(fc.dir, r.dir, true)) {
      if (fc.res.problem) return String(fc.res.problem);
      if (r.choice === 'have' && fc.res.kind !== 'stack' && fc.res.kind !== 'unknown') return t('deploy.stack.checkNotStack');
    }
  }
  return '';
}

// ─────────────────────────── actions ───────────────────────────
Object.assign(ACTIONS, {
  // The start screen's entry and links: a run that is not ended goes on; else the draft (kept until the launcher
  // restarts) — a fresh one on the Mode page.
  qsOpen: () => {
    if (S.installing || (S.agentInstall && S.agentInstall.running)) return showToast(t('toast.installInProgress'), 'bad');
    // (an install form left open on the start screen does not follow into Quick Start — enterShell's rule)
    if (S.agentInstall) S.agentInstall = null;
    const kept = S.qsPlan && typeof S.qsPlan.state === 'string' && S.qsPlan.state !== 'done' ? S.qsPlan : null;
    S.screen = 'quickstart';
    if (kept) {
      const own = S.qs.plan && S.qs.plan.id && S.qs.plan.id === kept.id ? S.qs.plan : kept;
      qsResume(own);
      render();
      return;
    }
    if (!S.qs.draft) { S.qs.draft = qsDraftNew(); S.qs.page = 'mode'; }
    if (S.qs.page === 'progress' || S.qs.page === 'done') S.qs.page = 'mode';
    qsCheckSoon();
    render();
  },
  // Close before Start: the start screen — nothing changed; the draft stays.
  qsClose: () => {
    qsFindHalt();
    S.screen = 'login';
    onceForget('login:');
    render();
  },
  qsBack: () => {
    const at = QS_PAGES.indexOf(S.qs.page);
    if (at > 0 && S.qs.page !== 'progress') qsGoPage(QS_PAGES[at - 1]);
  },
  qsGoto: (page) => { if (QS_PAGES.includes(page) && page !== 'progress' && S.qs.page !== 'progress' && S.qs.draft) qsGoPage(page); },
  qsNext: async () => {
    const d = S.qs.draft, page = S.qs.page;
    if (!d || S.qs.busy) return;
    if (page === 'server' && d.server.kind === 'existing' && d.server.testKey !== qsTestKey(d) && d.server.address.trim()) {
      await ACTIONS.qsTest();
      if (!d.server.test || d.server.testKey !== qsTestKey(d)) return;
    }
    const why = qsPageProblem(d, page);
    if (why) return showToast(why, 'bad');
    const at = QS_PAGES.indexOf(page);
    if (at >= 0 && at < QS_PAGES.indexOf('summary')) qsGoPage(QS_PAGES[at + 1]);
  },
  qsMode: (m) => { const d = S.qs.draft; if (!d) return; d.express = m !== 'custom'; render(); },
  // ── the Game page ──
  qsGameTick: (id) => {
    const d = S.qs.draft;
    if (!d || !versionById(id)) return;
    d.game.on[id] = !d.game.on[id];
    if (d.game.on[id] && !isInstalled(id) && !d.game.from[id]) d.game.from[id] = 'download';
    render();
  },
  qsGameFrom: (arg) => {
    const d = S.qs.draft, s = String(arg || ''), i = s.lastIndexOf(':');
    const id = s.slice(0, i), from = s.slice(i + 1);
    if (!d || !versionById(id)) return;
    d.game.from[id] = from === 'local' ? 'local' : 'download';
    render();
    if (from === 'local' && String(d.game.path[id] || '').trim()) qsInspectSoon(id, 0);
  },
  qsVoice: (arg) => {
    const d = S.qs.draft, s = String(arg || ''), i = s.indexOf('|');
    const id = s.slice(0, i), lang = s.slice(i + 1);
    if (!d || !id || !lang) return;
    const ticked = qsVoicePacks(d, id), on = ticked.some((p) => p.lang === lang);
    if (on && ticked.length <= 1) return showToast(t('toast.pickVoiceFirst'), 'bad');
    d.game.voices[id][lang] = !on;
    render();
  },
  qsPickExe: async (id) => {
    const d = S.qs.draft;
    if (!d) return;
    try {
      const r = await rpc('pickFile', { title: t('qs.game.pickExeTitle'), filter: t('wizard.pickLocalFilter'), initialDir: String(d.game.path[id] || '') });
      if (r && r.path) { d.game.path[id] = pickedGameDir(r.path); delete d.game.replace[id]; render(); qsInspectSoon(id, 0); }
    } catch (e) { showToast(e.message, 'bad'); }
  },
  qsUseInstead: (id) => {
    const d = S.qs.draft, ins = d && d.game.inspect[id], v = ins && ins.v;
    if (!v || v.reason !== 'installedElsewhere' || !v.installedAt) return;
    d.game.replace[id] = { path: d.game.path[id], installedAt: v.installedAt };
    qsInspectSoon(id, 0);
  },
  qsFind: () => qsFindRun(false),
  qsFindDeeper: () => qsFindRun(true),
  qsFindStop: () => qsFindHalt(),
  qsUseHit: (arg) => {
    const d = S.qs.draft, s = String(arg || ''), i = s.lastIndexOf('|');
    const id = s.slice(0, i), h = S.qs.find.hits[Number(s.slice(i + 1))];
    if (!d || !h || !h.dir) return;
    d.game.path[id] = h.dir;
    delete d.game.replace[id];
    render();
    qsInspectSoon(id, 0);
  },
  qsPickRoot: async () => {
    const d = S.qs.draft;
    if (!d) return;
    try { const r = await rpc('pickFolder', { current: d.game.installRoot, title: t('qs.game.folder') }); if (r && r.path) { d.game.installRoot = r.path; render(); } }
    catch (e) { showToast(e.message, 'bad'); }
  },
  qsSource: (src) => { const d = S.qs.draft; if (d) { d.game.source = src; render(); } },
  qsGameOpt: (k) => { const d = S.qs.draft; if (d && ['enhancements', 'decrypt', 'cert', 'shortcut'].includes(k)) { d.game[k] = !d.game[k]; render(); } },
  // ── the Server page ──
  qsKind: (k) => {
    const d = S.qs.draft;
    if (!d || !['existing', 'windows', 'linux'].includes(k) || (k === 'windows' && !S.isWindows)) return;
    if (d.server.kind !== k) { d.server.kind = k; d.server.vers = {}; }
    qsSyncVers(d);
    if (k === 'windows') { qsCheckSoon(); if (qsAgentOnPc()) qsLaSoon(); }
    render();
  },
  qsTest: async () => {
    const d = S.qs.draft, s = d && d.server;
    if (!s || s.testing) return;
    const address = s.address.trim();
    if (!address) return showToast(t('qs.server.addressFirst'), 'bad');
    const port = qsPortOf(s.addrPort);
    if (port < 0) return showToast(t('login.badAgentPort'), 'bad');
    const key = qsTestKey(d);
    s.testing = true;
    if (!qsPut('test', qsTestHtml(d))) renderOrOwe();
    try {
      const r = await rpc('server.test', { address, agentPort: port > 0 ? port : 0 });
      if (qsTestKey(d) === key) { s.test = r; s.testKey = key; }
    } catch (e) { if (qsTestKey(d) === key) showToast(e.message, 'bad'); }
    s.testing = false;
    renderOrOwe();
  },
  qsAdmin: () => { const d = S.qs.draft; if (d) { d.server.admin = !d.server.admin; render(); } },
  qsReach: (r) => {
    const d = S.qs.draft, m = qsMachine();
    if (!d || !['local', 'lan', 'internet'].includes(r) || (r !== 'local' && !(m && m.lanIp))) return;
    d.server.reach = r;
    render();
    if (r === 'internet' && !d.server.pubIp.res && !d.server.pubIp.busy) {
      d.server.pubIp.busy = true;
      rpc('quickstart.publicIp', {}).then((x) => { d.server.pubIp.res = x || null; }).catch((e) => { d.server.pubIp.res = { address: null, answers: [], problem: e.message }; })
        .finally(() => { d.server.pubIp.busy = false; renderOrOwe(); });
    }
  },
  qsWinOpt: (k) => {
    const d = S.qs.draft;
    if (!d || !['firewall', 'keepEnv', 'autostart', 'agentKeep'].includes(k)) return;
    d.server[k] = !d.server[k];
    render();
  },
  // A version's tick (Custom). One with no package published can be set up only from a folder that holds its server.
  qsSrvTick: (sv) => {
    const d = S.qs.draft, r = d && d.server.vers[sv];
    if (!r || d.express) return;
    r.on = !r.on; r.touched = true;
    if (r.on && r.noPackage && r.choice !== 'have') { r.choice = 'have'; if (r.progress === 'default') r.progress = 'keep'; }
    render();
    if (r.on && d.server.kind === 'windows' && r.dir) qsFolderSoon(sv, 0);
  },
  qsSrvChoice: (arg) => {
    const d = S.qs.draft, s = String(arg || ''), i = s.lastIndexOf(':'), sv = s.slice(0, i), c = s.slice(i + 1);
    const r = d && d.server.vers[sv];
    if (!r || r.choice === 'keep' || r.choice === 'ready' || !['download', 'have'].includes(c)) return;
    r.choice = c === 'have' ? 'have' : (r.here ? 'add' : 'download');
    if (r.choice === 'have' && r.progress === 'default') r.progress = 'keep';
    r.touched = true;
    render();
    if (d.server.kind === 'windows') qsFolderSoon(sv, 0);
  },
  qsSrvPick: async (sv) => {
    const d = S.qs.draft, r = d && d.server.vers[sv];
    if (!r) return;
    try {
      const x = await rpc('pickFolder', { current: r.dir || '', title: r.choice === 'have' ? t('deploy.stacks.pickTitle') : t('deploy.stacks.pickTitleInstall') });
      if (x && x.path) { r.dir = x.path; r.dirTyped = true; r.touched = true; render(); qsFolderSoon(sv, 0); }
    } catch (e) { showToast(e.message, 'bad'); }
  },
  qsProgress: (arg) => {
    const d = S.qs.draft, s = String(arg || ''), i = s.lastIndexOf(':'), sv = s.slice(0, i), m = s.slice(i + 1);
    const r = d && d.server.vers[sv];
    if (!r || !['default', 'keep', 'fixes'].includes(m)) return;
    if (m === 'default' && !qsRowFetches(r)) return;
    r.progress = m; r.touched = true;
    render();
  },
  qsPf: (arg) => {
    const d = S.qs.draft, s = String(arg || ''), i = s.lastIndexOf(':'), sv = s.slice(0, i), m = s.slice(i + 1);
    const r = d && d.server.vers[sv];
    if (!r || !['auto', 'on', 'off'].includes(m)) return;
    r.pf = m; r.touched = true;
    render();
  },
  qsLxAuth: (a) => {
    const d = S.qs.draft;
    if (!d) return;
    const auth = a === 'key' ? 'key' : 'password';
    if (d.server.ssh.auth !== auth) { d.server.ssh.auth = auth; qsLxEdited(d, false); }
    render();
  },
  qsLxPickKey: async () => {
    const d = S.qs.draft;
    if (!d) return;
    try { const r = await rpc('pickFile', { title: t('deploy.ssh.pickKeyTitle'), filter: t('deploy.ssh.pickKeyFilter') }); if (r && r.path) { d.server.ssh.keyPath = r.path; render(); } }
    catch (e) { showToast(e.message, 'bad'); }
  },
  qsLxConnect: () => { const d = S.qs.draft; if (d && !d.server.probe.busy) qsLxProbe(false); },
  // "Yes, this is my server" / "Trust the new key": the key read is the box's; the look at the box follows. Changed only
  // from the changed-key warning's own button.
  qsLxTrust: () => {
    const d = S.qs.draft, pr = d && d.server.probe, k = pr && pr.hostKey;
    if (!k || !k.fingerprint) return;
    pr.confirmed = k.fingerprint;
    pr.changed = k.verdict === 'changed' || k.changed === true;
    qsLxProbe(false);
  },
  qsLxPublic: (x) => {
    const d = S.qs.draft;
    if (!d) return;
    d.server.public = x === 'yes' ? 'yes' : 'no';
    render();
    if (d.server.public === 'yes' && qsLxAdvertised(d) === null && !d.server.probe.publicIp && !d.server.probe.lookup) qsLxProbe(true);
  },
  qsLxBind: (x) => { const d = S.qs.draft; if (d) { d.server.bind = x === 'fixed' ? 'fixed' : 'auto'; render(); } },
  // ── the Account page ──
  qsAcctTpl: (arg) => {
    const d = S.qs.draft, s = String(arg || ''), i = s.lastIndexOf(':');
    if (!d || i < 0) return;
    d.account.tpl[s.slice(0, i)] = s.slice(i + 1);
    render();
  },
  qsAcctSkip: (sv) => { const d = S.qs.draft; if (d && !d.express) { d.account.skip[sv] = !d.account.skip[sv]; render(); } },
  // ── the Summary ──
  qsLicence: () => { const d = S.qs.draft; if (d) { d.sum.licence = !d.sum.licence; render(); } },
  qsLicenceRead: () => {
    const tools = qsTools(), url = tools && tools.docker && tools.docker.licenceUrl;
    if (url) rpc('shell.openUrl', { url }).catch((e) => showToast(e.message, 'bad'));
  },
  qsMemory: (x) => { const d = S.qs.draft; if (d) { d.sum.memory = x === 'leave' ? 'leave' : 'raise'; render(); } },
  qsGoOn: () => { const d = S.qs.draft; if (d) { d.sum.goOn = !d.sum.goOn; render(); } },
  qsStart: () => qsStartRun(),
  // ── the progress page ──
  // A state button opens and closes its list with a slide (in place — a redraw would cut the slide).
  qsToggle: (th) => {
    if (th !== 'game' && th !== 'server') return;
    const open = !qsListOpen(th);
    S.qs.open[th] = open;
    const btn = document.querySelector(`#app .qs-state[data-arg="${th}"]`);
    const list = document.querySelector(`#app [data-qs-list="${th}"]`);
    if (!btn || !list) { render(); return; }
    btn.setAttribute('aria-expanded', open ? 'true' : 'false');
    list.classList.toggle('open', open);
    const h = list.scrollHeight;
    // (a closed list is hidden too, so the keyboard never lands on its buttons: shown before it slides in, hidden once
    //  it has slid out — the transition of visibility flips it at its end)
    if (open) {
      list.style.maxHeight = '0px'; list.style.opacity = '0'; list.style.visibility = 'visible';
      requestAnimationFrame(() => { list.style.maxHeight = h + 'px'; list.style.opacity = '1'; });
      setTimeout(() => { if (S.qs.open[th]) list.style.maxHeight = ''; }, 280);
    } else {
      list.style.maxHeight = h + 'px';
      requestAnimationFrame(() => { list.style.maxHeight = '0px'; list.style.opacity = '0'; list.style.visibility = 'hidden'; });
    }
  },
  qsLog: (th) => { if (th === 'game' || th === 'server') { S.qs.logOpen[th] = !S.qs.logOpen[th]; render(); } },
  qsRetry: (id) => {
    const rt = S.qs.rt;
    if (!rt || !qsHas(id) || qsRec(id).state !== 'failed') return;
    rt.act[id] = ''; delete rt.retryAt[id]; delete rt.fiddlerSince[id]; delete rt.busyTries[id];
    const st = qsStep(id);
    if (st && (st.kind === 'get' || st.kind === 'unpack' || st.kind === 'patch')) rt.act['inst:' + st.v] = '';
    if (st && st.kind === 'account') { delete rt.tplFresh[st.v]; if (rt.firstAcct) rt.firstAcct[st.v] = true; }
    qsSetState(id, 'pending', '');
    qsTick();
  },
  qsSkip: (id) => {
    const rt = S.qs.rt;
    if (!rt || !qsHas(id) || !['failed', 'needs'].includes(qsRec(id).state)) return;
    rt.userSkipped[id] = true;
    qsSetState(id, 'skipped', qsRec(id).note);
    qsTick();
  },
  qsPause: (id) => {
    const st = qsStep(id), inst = S.qs.inst;
    if (!st || !inst || inst.versionId !== st.v || inst.stop) return;
    inst.stop = 'pause';
    render();
    rpc('install.cancel').then((r) => { if (!r || !r.cancelling) { if (S.qs.inst === inst) inst.stop = ''; render(); } }).catch((e) => { if (S.qs.inst === inst) inst.stop = ''; showToast(e.message, 'bad'); });
  },
  qsResumeStep: (id) => {
    const st = qsStep(id), rt = S.qs.rt;
    if (!st || !rt || qsRec(id).state !== 'paused') return;
    if (st.v) rt.act['inst:' + st.v] = '';
    qsSetState(id, 'pending', '');
    qsTick();
  },
  qsTrustCert: async () => {
    try { const r = await rpc('fiddler.trustCert'); S.fiddler.certTrusted = true; showToast(r && r.already ? t('toast.certAlready') : t('toast.certApproved'), 'ok'); }
    catch (e) { showToast(e.message, 'bad'); }
    qsTick();
  },
  qsRestartNow: () => {
    if (!S.qs.rt || S.qs.rt.restarting) return;
    S.confirm = { title: t('confirm.qsRestart.title'), body: t('confirm.qsRestart.body'), yes: t('confirm.qsRestart.yes'), danger: false, act: 'qsRestartGo', arg: '', at: Date.now() };
    render();
  },
  // (a restart Windows was asked for and that has not happened a while later was held: qsRestartHeld)
  qsRestartGo: () => { const rt = S.qs.rt; qsRestartGo().then(() => { if (rt && rt.restarting) qsRestartHeld(rt); }); },
  qsRestartLater: () => { const rt = S.qs.rt; if (rt) { rt.restartLater = true; qsSetNote('s.restart', t('qs.note.restartLater')); qsTick(); } },
  qsCheckAgain: (id) => {
    const rt = S.qs.rt, st = qsStep(id);
    if (!rt || !st) return;
    rt.act[id] = '';
    if (st.kind === 'account') delete rt.acctWhy[st.v];
    qsSetState(id, 'running', '');
    qsTick();
  },
  // A step that asked for something: what was typed into it (page memory), and the step goes again.
  qsStepGo: (id) => {
    const rt = S.qs.rt, st = qsStep(id);
    if (!rt || !st || qsRec(id).state !== 'needs') return;
    const val = (name) => { const el = document.querySelector(`#app [data-qs-step="${id}"] [data-qsf="${name}"]`); return el ? el.value : ''; };
    if (st.kind === 'ready') { rt.userSkipped[id] = false; qsSetState(id, 'done', ''); qsTick(); return; }
    if (st.kind === 'connect') { const v = val('stepToken').trim(); if (!v) return; S.qs.mem.token = v; }
    else if (st.kind === 'agent') {
      const h = S.qs.plan.server.ssh, k = rt.needKey;
      if (k) {
        // the key the box presents now is the box's (its button: Trust the new key after the warning, else Yes)
        h.hostKey = { type: k.type || '', fingerprint: k.fingerprint || '' };
        rt.changedKey = k.verdict === 'changed' || k.changed === true;
        rt.needKey = null;
        qsSaveSoon();
      } else {
        if (h.keyPath) { S.qs.mem.phrase = val('stepPhrase'); rt.phraseAsked = true; }
        else { const v = val('stepSsh'); if (!v) return; S.qs.mem.ssh = v; }
        const sudo = val('stepSudo');
        if (sudo) S.qs.mem.sudo = sudo;
        rt.askLogin = false;
      }
    } else if (st.kind === 'account') {
      if (rt.needPw && rt.needPw[st.v]) { const v = val('stepAcctPw'); if (!ACCOUNT_PASSWORD_RE.test(v)) return showToast(t('toast.accountBadPassword'), 'bad'); S.qs.mem.acct = v; rt.needPw[st.v] = false; }
      else { const v = val('stepAcctName').trim(); if (!ACCOUNT_NAME_RE.test(v) || /^\d+$/.test(v) || /^(relic|tpl|admin|gm)/i.test(v)) return showToast(t('toast.accountBadName') + ' ' + qsNameRules(null), 'bad'); rt.names[st.v] = v; }
    }
    rt.act[id] = '';
    qsSetState(id, 'pending', '');
    qsTick();
  },
  qsStopAsk: () => {
    if (!S.qs.rt || S.qs.rt.stopped) return;
    S.confirm = { title: t('confirm.qsStop.title'), body: t('confirm.qsStop.body'), yes: t('confirm.qsStop.yes'), act: 'qsStopGo', arg: '', at: Date.now() };
    render();
  },
  qsStopGo: () => qsStopRun(''),
  qsContinue: () => qsContinue(),
  qsFinish: () => qsFinish(),
});

// ─────────────────────────── the page's fields ───────────────────────────
// What is typed into a field of Quick Start (data-qsf; data-qsv = the version it is about) goes to the draft or to page
// memory as it is typed — no redraw (it would take the caret); the parts that follow it are drawn in place.
document.addEventListener('input', (e) => {
  const el = e.target && e.target.closest ? e.target.closest('[data-qsf]') : null;
  if (!el || S.screen !== 'quickstart') return;
  const name = el.getAttribute('data-qsf'), v = el.getAttribute('data-qsv') || '', val = el.value;
  const d = S.qs.draft, m = S.qs.mem;
  switch (name) {
    case 'gamePath':
      if (!d) return;
      d.game.path[v] = val;
      if (d.game.replace[v] && !sameDirText(d.game.replace[v].path, val)) delete d.game.replace[v];
      qsInspectSoon(v);
      qsVerdictSync(v);
      return;
    case 'installRoot': if (d) d.game.installRoot = val; return;
    case 'address':
      if (!d) return;
      d.server.address = val;
      if (d.server.test) { d.server.test = null; qsPut('test', ''); }
      return;
    case 'addrPort': if (d) { d.server.addrPort = val; if (d.server.test) { d.server.test = null; qsPut('test', ''); } } return;
    case 'token': m.token = val; return;
    case 'agentPort': if (d) d.server.agentPort = val; return;
    case 'pubTyped': if (d) d.server.pubTyped = val; return;
    case 'verDir': {
      const r = d && d.server.vers[v];
      if (!r) return;
      r.dir = val; r.dirTyped = true; r.touched = true;
      if (d.server.kind === 'windows') qsFolderSoon(v);
      return;
    }
    case 'lxHost': if (d) { d.server.ssh.host = val; qsLxEdited(d, true); } return;
    case 'lxPort': if (d) { d.server.ssh.port = val; qsLxEdited(d, true); } return;
    case 'lxUser': if (d) { d.server.ssh.user = val; qsLxEdited(d, false); } return;
    case 'lxKey': if (d) { d.server.ssh.keyPath = val; qsLxEdited(d, false); } return;
    case 'lxPassword': m.ssh = val; if (d) qsLxEdited(d, false); return;
    case 'lxPhrase': m.phrase = val; if (d) qsLxEdited(d, false); return;
    case 'lxSudo': m.sudo = val; return;
    case 'bindIp': if (d) d.server.bindIp = val; return;
    case 'acctName': if (d) { d.account.name = val; d.account.touched = true; } return;
    case 'acctPassword': m.acct = val; return;
    default: return;
  }
});
