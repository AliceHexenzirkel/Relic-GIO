'use strict';

// ─────────────────────────── state ───────────────────────────
const S = {
  screen: 'login',
  // The class is already on <body> (the inline script in index.html sets it before first paint);
  // here it is only read back as state so the Settings selector knows which one is active.
  theme: document.body.classList.contains('theme-summer') ? 'summer' : 'classic',
  os: { text: '', isWindows11: false },
  fiddler: { installed: false, certTrusted: false },
  // extractWorkers must start as a NUMBER: setModel only parses input when the current value is one,
  // so seeding it '' would store the raw string. The backend parses a numeric string instead of
  // throwing on it (Backend.NumOr), but seed numbers anyway — that is what keeps setModel coercing.
  // ownFiles: {versionId: 'keep' | 'replace'} — the answer PLAY gives without asking when a version's game
  // folder holds a file of the player's own in the place of one Relic ships (no entry = ask). Written only
  // through settings.ownFiles, which saves it at once; settings.save leaves it alone.
  settings: { serverHost: '', serverPort: 21000, source: 'cdn', fiddlerDecrypt: true, installCert: true, createShortcut: true, fiddlerNoUac: true, gentleExtract: true, extractWorkers: 0, installRoot: '', language: 'en', enhancements: true, ownFiles: {} },
  // What the name of the player's own file gets after it when Relic's file takes its place (init state
  // ownSuffix) — said by the texts of Settings → Game files.
  ownSuffix: '',
  // The answer the question about the game folder's own files got on the PLAY under way: {id, choice} —
  // handed to play.start by playGo for that version alone; every PLAY starts without one.
  playOwn: null,
  // Settings → "Apply Relic's game files to every installed version" in flight (patch.applyAll).
  filesApplying: false,
  // Which profile the registry/LocalLow currently hold. null until Settings asks the backend.
  profile: null,
  versions: [], installed: [], selectedVersionId: null, serverConfigured: false, runAtStartup: false,
  // The server stacks an agent can be set up for (init state stacks), in display order — one row per
  // version: {id, dirField, stackField (the names of its two fields in the install-agent form and its
  // payload: its folder, its choice), folder (the conventional folder name), size (bytes of the ready-made
  // package), downloadable (the build knows a package to download), archive (that package's format),
  // needsExtractor, extractedSize (its bytes once unpacked; 0 = the catalogue names none), zipSize (the bytes
  // of its .zip; 0 = it has none)}. Everything about the install form that is per version comes from these
  // rows; nothing here names a version.
  stacks: [],

  installStep: 0,
  // voices: the wizard's voice-language ticks {lang: bool}, kept by language across versions (the
  // names are the same on each) and seeded lazily — see selectedVoicePacks.
  wiz: { versionId: null, source: 'cdn', localPath: '', decrypt: true, cert: true, shortcut: true, shortcutName: '', voices: {} },
  // The wizard's local folder (source 'local'): the check of what its field names as the chosen version
  // (install.inspectGame — see impBlank) and "Find it on this PC" (install.findGames — see findBlank).
  imp: impBlank(),
  find: findBlank(),
  // Local folder of the install in progress — snapshot at start (wiz is mutable), needed by Resume.
  activeInstallLocal: null,
  // The languages an "add voice pack" job (install.addVoices) is adding — null for a full install. It
  // picks the overlay's words (title, the done box without login details) and the toasts.
  activeInstallVoices: null,
  // The very rpc that started the running install job ({type, payload}), replayed by Resume: the
  // wizard's ticks are mutable meanwhile, and a resume must continue exactly the job that paused.
  activeInstallReq: null,
  installing: false, installDone: false, installFraction: 0, installMsg: '', installPhase: '', installError: null,
  // 'pause' | 'cancel' while an install.cancel rpc is in flight — it decides what the backend's
  // install.cancelled event means; installPaused keeps the (frozen) overlay up until Resume/Give up.
  installStopIntent: null, installPaused: false,
  activeInstallId: null, // snapshot of the id actually installing — wiz.versionId is mutable mid-install

  cmdUid: '',
  // The version Game commands act on when a row of the account list opened them (acctsCommands — a version this PC
  // has not installed included); null = the Library's selected version (cmdVersionId). The Library's own choice
  // (selectVersion) and another server (srvRetarget) give the commands back to the Library's version.
  cmdVersion: null,
  cmd: { exp: '10000', character: '10000002', artifact: '71544 20', weapon: '13501', level: '60', mora: '100000', primogems: '10000', resin: '160' },
  // Seeded in boot() once the language is loaded (the "console ready" line is translated).
  log: [],
  // Content catalogue for the command pickers, keyed on the version it was fetched for. Any status
  // other than 'ok' falls back to the raw-id cards, so the pickers never depend on this rpc. costumes =
  // the skins that exist in that version: the backend's own list, kept from every answer ('ok' or
  // 'empty'). Without an answer ('loading', 'error') there is no list and the skins card is not drawn
  // (costumeCard); the raw command input sends a skin as well (item add <id> 1).
  gd: { version: null, status: 'idle', error: '', avatars: [], weapons: [], sets: [], costumes: [] },
  // artifactMode: 'equip' = the pieces are equipped DIRECTLY on the in-game character, at the chosen
  // level (equip add — default: "item add" has no level parameter, so +20 pieces only come this way);
  // 'inv' = the pieces land in the inventory unequipped, at +0 (item add).
  sel: { avatarId: null, weaponId: null, weaponLevel: '90', weaponPromote: '6', setName: null, artifactLevel: '20', artifactMode: 'equip' },
  ingame: { promote: '6' }, // "In-game character" card: ascension level for break
  helpOpen: false,          // player instructions, collapsed by default
  picker: null,          // {kind:'avatar'|'weapon'|'artifact', q, el, type, r} while the modal is open
  rawOpen: false, rawCmd: '',
  sending: false,        // one GM round-trip at a time — a double-click must not fire two sets

  // "Starting the game" overlay: {versionId, msg} while the session starts; null once the client has
  // actually appeared (play.appeared) or the session ended/failed.
  playing: null,
  // The Library's notice of the play session's end (sessionNote): null, or {phase, kind, text, versionId, appeared} —
  // phase 'closing' = the client is seen gone and the session end runs (play.closing; PLAY stays off until play.done,
  // play.error or play.fiddlerClosed), 'done' = what the session end did (play.done: kind restored | deferredOwn |
  // deferredForeign, appeared false = the client was never seen), 'profile' = the recovery loop put the official
  // profile back after a deferred restore (profile.changed). The text comes from the backend, already translated.
  session: null,
  // Generic "are you sure?" dialog: {title, body, yes, act, arg, at, checkbox?} — confirmYes runs
  // ACTIONS[act](arg, checkboxChecked, selected). A chooser adds options [{id, label, sub, danger?}] +
  // selected, with bodyFor / yesFor {id: text} picking the body and the Yes label of the selected
  // option (the Prepare-the-server dialog); wide = the 600 px box for a dialog with option cards. A
  // question of the gameplay settings carries tw = the version it is about (twOpen takes the question away
  // when another version comes on screen under it). focusNo = the keyboard starts on Cancel (a question
  // whose safe answer is not to go on: Enter or Space then cancels). A question whose two buttons are
  // two answers carries no = the label of the second (Cancel otherwise) and noAct = its action, run like
  // act — by that button and by Esc; danger: false = a Yes that is not red (nothing irreversible is asked).
  // ask = the action that opened a question worded from the server's status: confirmYes runs it again with
  // arg and goes on only while it asks the same question.
  confirm: null,
  // "OK only" information window: {title, body, pick?, rows?} (see noticeOverlay). For events the user
  // must READ (e.g. the game closed because Fiddler was closed) — a toast would vanish on its own.
  notice: null,

  // Server status exactly as the agent reports it: versions[id] = the raw object
  // {up, present, bootstrapped, provisioned, txtFixes, account, ...}, info = the rest (agent,
  // advertisedIp, busy, error). state: 'idle' | 'checking' | 'ok' | 'error'.
  srv: { state: 'idle', error: '', versions: {}, info: null },
  // The connected server's own word on the pre-made in-game login, per version, from the status reads
  // of THIS target (ingestStatus; srvRetarget clears it): name, or '' = the server said there is NONE
  // (a keep / fixes server) — an answered '' never falls back to the catalogue.
  srvAccount: {},
  // The same answer as the backend persisted it for the current server (init state `serverAccounts`),
  // for the first render before the first status read of a session.
  serverAccounts: {},
  // Long server operation in progress + its streamed log. null = nothing running.
  srvJob: null,          // {kind, version, target, host, purge, progress, enabled, dryRun, template, lines: [], running, error} — target: see jobHere
  // "Stop waiting" (tweaksStop): no[target] = the build of that server's agent when it answered that it
  // cannot stop the waits ('' = it named none) — the control is not offered to it again until another build
  // answers there (twStopRefused); busySent = `<target>|<job id>` of the job started elsewhere a stop was
  // sent for — its button stays dead until the status names another job.
  twStop: { no: {}, busySent: '' },
  // The tab the 'server' screen draws in Server Admin mode: overview | players | content | access | agent
  // (ADMIN_SERVER_TABS). Game rules is a screen of its own ('tweaks'). Kept while the admin visits other
  // screens; another status target opens on the Overview (srvRetarget). Never part of srvTarget().
  adminTab: 'overview',
  // "Copy by name or uid" form (the Players tab): null or {version, from, to}.
  acctCopy: null,
  // The Players tab's "Accounts on this server" (see acctsBlank).
  accts: acctsBlank(),
  // "Bring progress from another version" (the Players tab) — see migrateBlank. Per server: srvRetarget resets it.
  migrate: migrateBlank(),

  // ── modes / start screen ──
  // mode: '' (not chosen yet — the start screen asks) | 'player' | 'admin'. A UX gate only: the agent
  // bearer token is the security boundary (the backend refuses admin rpcs without it).
  mode: '',
  // muted / animOff: the two start-screen media pills (remembered in localStorage + state.json).
  // hidden: the window sits in the tray — the media pause, whatever the pills say.
  // agentPort: the optional agent port typed on the admin panel ('' = the TXT record / the port known
  // for the host / 18080) — a text field, parsed when Test / Enter send it.
  // saving: the Save row under the address, open = {label, host} (the name typed, and the host of the address the row
  // was opened for), null = closed.
  // savedBusy: a start-screen rpc in flight that the saved servers wait on — servers.save / delete / deleteAll, a
  // Connect, the panel's own Enter ('save' | 'delete' | 'all' | 'connect' | 'enter'; '' = none): the list's buttons,
  // Save and Enter are dead meanwhile, one at a time.
  login: { panel: null, address: '', token: '', agentPort: '', testing: false, test: null, muted: false, animOff: false, hidden: false, saving: null, savedBusy: '' },
  defaults: { servers: [], mode: '' },           // baked by publish.ps1 (quick-pick servers)
  // The start screen's saved servers (init state savedServers; servers.save / delete / deleteAll answer the list
  // again): [{id, address, host, mode, label, agentPort, hasToken, openKey, current}], newest first — an address, a
  // mode and a name, with booleans about what Relic keeps for the host; never a token.
  savedServers: [],
  // Quick Start's run as this PC stores it (init state quickstart.plan — no secret is ever in it; quickstart.save and
  // quickstart.clear answer it again), or null: the start screen offers to continue a paused run, and boot opens the
  // progress page of one that is not ended (running, or waiting for the restart that finishes WSL 2). The screens and
  // the runner are quickstart.js's (S.qs); this is only what the start screen and boot read.
  qsPlan: null,
  serverAddr: { host: '', port: 21000, agentPort: 18080, source: 'default' },
  // The server address typed in Settings / the install wizard and not saved yet (null = none): it is
  // saved through server.setAddress, never through settings.save (see saveAddressEdit).
  addrEdit: null,
  // tokenScope: 'any' (baked into the build) | host the stored token is for | ''. tokenHosts: the hosts
  // server.json holds a saved agent token for (host names alone, never a token — an open key is none).
  // mode: 'direct' | 'ssh' — how the configured host's token connects (an ssh-mode server.json: nothing in
  // the UI creates one, the Overview and the Access tab only name it). openKey: the admin entry is the
  // key an open server hands every player (admin.openEnter), not the agent token — the Server page says
  // so, and a 401 on it ends admin mode (admin.revoked) instead of a toast.
  admin: { hasToken: false, tokenScope: '', tokenHosts: [], mode: 'direct', openKey: false },
  isWindows: true,
  // The voice languages this build knows, in the game's own order (init state voiceLanguages) — the
  // catalogue's packs (versions[].voices) already come in it; kept so nothing here hard-codes a name.
  voiceLanguages: [],
  // Player side: last /public/status of the configured server + the accounts created through Relic:
  // accounts = the login shown per version, accountLists = every one this launcher created there
  // (per version, oldest first -- a player may create several).
  pub: { status: null },
  accounts: {},
  accountLists: {},
  // The player's "Your account on this server" card (see accountBlank). version = the chooser's pick,
  // reset only with the server (srvRetarget) — never by applyInit, whose selectedVersionId is the
  // Library's and says nothing about which version the player wants an account on.
  account: accountBlank(),
  // Server Admin mode's "Create a player account" card (see acctNewBlank).
  acctNew: acctNewBlank(),
  // The player card's account.create rpc in flight: {target, version, name} | null. Kept across a
  // server switch ON PURPOSE: its account.* events still arrive, and must then land nowhere but a
  // toast — never in the new server's form (see the account.* handlers).
  acctPending: null,
  // Admin side: policy / secrets / local agent / install-agent overlay / removal overlay.
  policy: { data: null, loading: false },
  secrets: { version: null, data: null, form: {}, loading: false },
  // Official in-version hotpatch mirror: the last GET /server/hotpatch of the selected version.
  // voiceSel = the admin's voice-pack ticks {language: bool} — render() rewrites #app, so
  // they live here; voiceKey = what they were taken from (server + version + the server's selection,
  // see syncVoiceSel). busy = the hotpatch / voice job the last admin /status named (trackHotpatchBusy);
  // voiceStop = the version whose "Stop mirroring" was pressed — dead until that job's state changes.
  hotpatch: hotpatchBlank(),
  // "Agent settings" card (GET/POST /agent/config, POST /agent/restart, the stack folders
  // through POST /server/relocate) — see agentCfgBlank. Per server: srvRetarget resets it.
  agentCfg: agentCfgBlank(),
  // The Agent tab's Logs (agent.jobs / agent.jobLog) — see agentJobsBlank. Per server: srvRetarget resets it.
  agentJobs: agentJobsBlank(),
  // The gameplay settings of the Game rules tab (GET /server/tweaks, the job POST /server/tweaks): the
  // server's own data — resin, the Spiral Abyss line-up, the domains' rewards, enemies and enemy level,
  // quest rewards — as one document per version, edited as a draft and sent with one button. See twBlank.
  // Per server: srvRetarget resets it.
  tw: twBlank(),
  // The display catalogue of those settings (tweakdata.get: names and sprite names by id). It belongs to this
  // build, not to a server, so it is read once and kept. status: 'idle' | 'loading' | 'ok' | 'missing' |
  // 'error'; list[kind] = the entries, by[kind][id] = one of them (kind: monsters, items, domains, quests).
  twData: { status: 'idle', error: '', list: {}, by: {} },
  // The agent installed on THIS PC (Windows). init = the init-state fact {installed, host, port,
  // autostart} | null (off Windows), refreshed by every reply that carries the init state, so the start
  // screen can offer it before any mode is chosen; status = the admin-only localagent.status of the
  // Server page card; busy = an enter / uninstall in flight (the UAC prompt of the firewall step can
  // hold it for a minute — both buttons stay dead meanwhile).
  localAgent: { status: null, init: null, busy: false },
  // The agent this launcher ships and the one installed on this PC, as build identities (agent.build):
  // shipped = this build's (null: a build without an agent — nothing is offered then); local = {installed,
  // build, adminServer} | null before the first answer, adminServer = the configured admin server IS the
  // agent on this PC (its update needs no SSH). An agent names its own identity as `build` in its status:
  // "update available" = that is not `shipped` (agentBuildDiffers). seq: the last read asked wins.
  agentBuild: { shipped: null, local: null, seq: 0 },
  // The update overlay: null, or {target: 'local' | 'remote', phase: 'form' | 'run' | 'done' | 'error',
  // form (the SSH details of a remote update, never stored), lines, error, warning, srvTarget (the status
  // target it was started under)}.
  agentUpdate: null,
  // The player Server page's "Enter admin mode" (admin.openEnter) in flight: the button is dead and
  // says so meanwhile — patched in place, never through render() (syncOpenAdminButton).
  openAdmin: { busy: false },
  agentInstall: null,   // {open, target, form, running, lines, done, error, result} — see deployOpen
  // The jobs an agent install (deploy.done `chain`) or "Add version…" left for the Server page, run one at a
  // time (chainNext): null, or {target (the status target they belong to), win (that server's agent runs on
  // Windows: how its folders compare), steps: [{version, dir, repoint, fetch, stop}] (the first one runs; stop =
  // its re-point may copy a download's partial file to another drive, which Stop ends), phase (null | 'fetch' = the
  // first step's re-point is done), busyTries (how often the agent refused its start while another operation ran),
  // running ({kind, version} of the first step's job while it runs)}.
  fetchChain: null,
  remove: null,         // {versionId, running, lines, msg, fraction, error, done, leftovers}

  toast: null,
};

const isInstalled = (id) => S.installed.some((i) => i.id === id) || S.versions.some((v) => v.id === id && v.installed);
const versionById = (id) => S.versions.find((v) => v.id === id);
// In-game enhancements (F1 menu) apply to a version when the setting is on (default) AND this build
// ships the DLL for it (versions[].hasEnhancements) — the same rule as Backend.EnhancementsHintFor.
const eeWanted = (id) => S.settings.enhancements !== false && !!((versionById(id) || {}).hasEnhancements);
// The version's IN-GAME login (the account of the prepared save: "aether" on 1.6, "aetherr" on 2.8).
// It comes from the backend — see GameAccounts in Relic.Core — so it is NOT hand-written here; a
// version without a known account returns '' and every place that shows it hides itself.
// Policy-aware: an account the player created through Relic on THIS server wins; then
// the server's own word (a server prepared with "keep my progress" or "fixes only" has NO
// pre-made account and answers '' — that hides the login everywhere, it never falls back); then the
// answer the backend remembered for this server; the catalogue/payload value only before any answer.
const hasOwn = (o, k) => !!o && Object.prototype.hasOwnProperty.call(o, k);
const accountOf = (id) => {
  const mine = S.accounts && S.accounts[id];
  if (mine && mine.name) return mine.name;
  if (hasOwn(S.srvAccount, id)) return S.srvAccount[id] || '';
  if (hasOwn(S.serverAccounts, id)) return S.serverAccounts[id] || '';
  const v = versionById(id); return (v && v.account) || '';
};
// The catalogue's (or the shipped manifest's) account of the pre-made save — what a "default" Prepare
// imports. Not accountOf(): that is the server's CURRENT word, '' on a keep / fixes server, which is
// exactly what the Prepare dialog is about to change.
const catalogueAccountOf = (id) => ((versionById(id) || {}).account) || '';
// True when the server verifies passwords for this version (the "any" wording must change). Read from
// the normalised status (srvFacts carries passwordVerify for BOTH shapes — the admin /status and the
// public snapshot), so an admin whose stack verifies passwords sees the right word too.
const passwordVerified = (id) => { const f = srvFacts(id); return !!(f && f.passwordVerify); };
// True when the server ADVERTISES its mirror of the official in-version fixes for this version (the client
// then downloads the fixes once at login): the public snapshot in player mode (its `enabled` already
// means "enabled and not pending"), the admin's /status otherwise — there `pending` (database half
// owed, or an unusable mirror URL) means no client is told anything yet, so no badge.
// ingestStatus keeps the field in S.srv.versions for both shapes.
const hotpatchServed = (id) => {
  const pub = S.pub.status && S.pub.status.versions && S.pub.status.versions[id];
  if (pub && pub.hotpatch && pub.hotpatch.enabled) return true;
  const e = S.srv.versions && S.srv.versions[id];
  return !!(e && e.hotpatch && e.hotpatch.enabled && !e.hotpatch.pending);
};
const accountIsMine = (id) => !!(S.accounts && S.accounts[id] && S.accounts[id].name);
// The value shown for the in-game password, the same everywhere: "any" (the server does not verify
// it — the normal case) or, when it does, whose it is. Never an example value: "e.g. 123" reads as a
// required password.
const passwordWord = (id) => passwordVerified(id) ? (accountIsMine(id) ? t('account.pwYours') : t('lib.creds.verifiedAsk')) : t('lib.creds.any');
const selected = () => versionById(S.selectedVersionId) || S.versions[0];
// A version's picture is drawn only when this build ships its file (init state heroArt / summerArt):
// the style attribute of the element it goes on, or nothing — the element then keeps the plain gradient
// of its own rule in relic.css.
const heroStyle = (id) => ((versionById(id) || {}).heroArt ? `style="background-image:url('assets/hero-${id}.webp')"` : '');
const isSummer = () => S.theme === 'summer';
// Summer theme: the hero background is the selected version's summer picture, else its usual one.
const summerStyle = (id) => ((versionById(id) || {}).summerArt ? `style="background-image:url('assets/summer-${id}.webp')"` : heroStyle(id));
function applyTheme(t) {
  S.theme = t === 'summer' ? 'summer' : 'classic';
  document.body.className = 'theme-' + S.theme;
  try { localStorage.setItem('relic-theme', S.theme); } catch (e) { /* no localStorage */ }
}
const splashImg = (id) => `assets/splash/${id}.webp`;
// Weapon/artifact sprites are keyed by the game's own icon name (UI_EquipIcon_*, UI_RelicIcon_*),
// fetched by build/make_catalog.py. Missing art degrades to the tile's placeholder underneath.
const iconImg = (n) => `assets/icons/${n}.webp`;

// ─────────────────────────── catalogue ───────────────────────────
const EL = { pyro: '#e4664e', hydro: '#4cb2e6', anemo: '#6fd0b0', electro: '#b48ee0', dendro: '#8fbf50', cryo: '#9fd8e8', geo: '#e0a94a' };
const ELEMENTS = ['Pyro', 'Hydro', 'Anemo', 'Electro', 'Dendro', 'Cryo', 'Geo'];
const elColor = (e) => EL[String(e).toLowerCase()] || '#9b7d3a';
// Label tables hold translation KEYS, translated at use: they are evaluated at module load, before
// the language file exists.
const WTYPES = [
  { key: 'sword', many: 'cmd.wtype.sword.many', one: 'cmd.wtype.sword.one' },
  { key: 'claymore', many: 'cmd.wtype.claymore.many', one: 'cmd.wtype.claymore.one' },
  { key: 'polearm', many: 'cmd.wtype.polearm.many', one: 'cmd.wtype.polearm.one' },
  { key: 'catalyst', many: 'cmd.wtype.catalyst.many', one: 'cmd.wtype.catalyst.one' },
  { key: 'bow', many: 'cmd.wtype.bow.many', one: 'cmd.wtype.bow.one' },
];
const wtype = (t) => WTYPES.find((w) => w.key === String(t).toLowerCase());
const SLOT_KEYS = ['flower', 'plume', 'sands', 'goblet', 'circlet'];
const SLOT_NAMES = ['cmd.slot.flower', 'cmd.slot.plume', 'cmd.slot.sands', 'cmd.slot.goblet', 'cmd.slot.circlet'];
const slotName = (i) => (SLOT_NAMES[i] ? t(SLOT_NAMES[i]) : t('cmd.slot.piece', { n: i + 1 }));

const curAvatar = () => S.gd.avatars.find((a) => a.id === S.sel.avatarId) || null;
const curWeapon = () => S.gd.weapons.find((w) => w.id === S.sel.weaponId) || null;
const curSet = () => S.gd.sets.find((s) => s.name === S.sel.setName) || null;
// The letter a tile shows in place of art that is missing: the first character of the name, past an
// opening quote or bracket ("The Catch" is T).
const initial = (s) => (String(s || '').replace(/^[\s"'“‘«(\[]+/, '').charAt(0) || '?').toUpperCase();
const stars = (n) => `<span class="stars">${'★'.repeat(Math.max(0, Math.min(5, n)))}</span>`;
const clampInt = (v, lo, hi, dflt) => { const n = parseInt(v, 10); return isNaN(n) ? dflt : Math.max(lo, Math.min(hi, n)); };

// The catalogue is produced by a separate data file whose field names may drift, so every record is
// read through pick(): a renamed or missing field degrades to a blank instead of throwing.
const pick = (o, ...keys) => { for (const k of keys) { const v = o && o[k]; if (v !== undefined && v !== null && v !== '') return v; } return ''; };
const num = (v) => { const n = parseInt(v, 10); return isNaN(n) ? 0 : n; };
const aliasText = (v) => (Array.isArray(v) ? v.join(' ') : String(v || ''));
// One lowercased haystack per record, built once at load: the picker's keystroke filter must stay a
// plain substring test, never a per-key rebuild of the record list.
const searchKey = (...parts) => parts.filter(Boolean).join(' ').toLowerCase();

function normAvatar(r) {
  const id = num(pick(r, 'id', 'avatarId'));
  if (!id) return null;
  const name = String(pick(r, 'name', 'title') || id);
  return {
    id, name,
    element: String(pick(r, 'element', 'el')),
    weapon: String(pick(r, 'weapon', 'weaponType', 'type')),
    rarity: num(pick(r, 'rarity', 'stars', 'quality')) || 5,
    since: String(pick(r, 'since', 'version', 'addedIn')),
    s: searchKey(name, aliasText(r && r.aliases), id),
  };
}

function normWeapon(r) {
  const id = num(pick(r, 'id', 'weaponId'));
  if (!id) return null;
  const name = String(pick(r, 'name', 'title') || id);
  return {
    id, name,
    type: String(pick(r, 'type', 'weaponType', 'weapon')),
    rarity: num(pick(r, 'rarity', 'stars', 'quality')) || 5,
    since: String(pick(r, 'since', 'version', 'addedIn')),
    icon: String(pick(r, 'icon') || ''),
    s: searchKey(name, aliasText(r && r.aliases), id),
  };
}

/// A small sprite tile with the item's initial showing through when the art is missing.
function iconTile(icon, label, cls) {
  // data-artwrap opts into the same capture-phase error handler the splash art uses: a sprite whose
  // file is missing hides the <img> and leaves the tinted initial, instead of a broken-image glyph.
  return `<span class="spr ${cls || ''}" data-artwrap><i>${esc(initial(label))}</i>${
    icon ? `<img src="${esc(iconImg(icon))}" alt="" loading="lazy" decoding="async"/>` : ''}</span>`;
}

function normSet(r) {
  const name = String(pick(r, 'name', 'setName', 'set'));
  const ids = setPieceIds(r);
  if (!name || !ids.length) return null;
  const raw = (r && (r.pieces || r.parts || r.items)) || null;
  return {
    name, ids,
    // One sprite per piece, in the same order as ids, so a set previews as its five real pieces.
    icons: Array.isArray(raw) ? raw.map((p) => String((p && p.icon) || '')) : [],
    rarity: num(pick(r, 'rarity', 'stars', 'quality')) || 5,
    since: String(pick(r, 'since', 'version', 'addedIn')),
    s: searchKey(name, aliasText(r && r.aliases), ids.join(' ')),
  };
}

// Piece ids arrive as an explicit list, as a slot→id object, or only as the set's base id — the five
// pieces of a set are five consecutive ids from that base (23300..23304), so derive them when needed.
function setPieceIds(r) {
  const raw = (r && (r.pieces || r.parts || r.items)) || null;
  let ids = [];
  if (Array.isArray(raw)) ids = raw.map((p) => num(p && typeof p === 'object' ? pick(p, 'id', 'pieceId') : p));
  else if (raw && typeof raw === 'object') ids = SLOT_KEYS.map((k) => num(raw[k]));
  else if (r && SLOT_KEYS.some((k) => r[k])) ids = SLOT_KEYS.map((k) => num(r[k]));
  else { const base = num(pick(r, 'baseId', 'firstId', 'setId', 'id')); if (base) ids = [0, 1, 2, 3, 4].map((i) => base + i); }
  return ids.filter((n) => n > 0).slice(0, 5);
}

// A skin of the backend's list: the item id, the character that wears it, its name.
function normCostume(r) {
  const id = num(pick(r, 'id'));
  if (!id) return null;
  return { id, char: String(pick(r, 'character', 'char')), name: String(pick(r, 'name') || id) };
}

const normList = (arr, fn) => (Array.isArray(arr) ? arr.map(fn).filter(Boolean) : []);

// A fast version switch can leave a slow reply in flight; stamp each request so a stale one cannot
// overwrite the catalogue of the version the user is actually looking at.
let gdSeq = 0;
// The version Game commands act on: the one a row of the account list opened them for, else the Library's selection.
function cmdVersionId() { return S.cmdVersion || S.selectedVersionId; }
async function loadGameData(force) {
  const version = cmdVersionId();
  // No version selected (an empty catalogue, a state file that names none): there is nothing to
  // fetch, and returning while S.gd stays "idle" would leave the screen on "loading the content
  // catalogue" for ever. Say it is unavailable and open the raw-id inputs, which is the whole
  // capability without a catalogue.
  if (!version) {
    S.gd = { version: null, status: "empty", error: "", avatars: [], weapons: [], sets: [], costumes: [] };
    S.rawOpen = true;
    render();
    return;
  }
  if (!force && S.gd.version === version && S.gd.status === 'ok') return;
  const seq = ++gdSeq;
  S.gd = { version, status: 'loading', error: '', avatars: [], weapons: [], sets: [], costumes: [] };
  render();
  try {
    const reply = await rpc('gamedata.forVersion', { version });
    if (seq !== gdSeq) return;
    // covered: false = the catalogue does not describe this version (its version order and its skins
    // table both leave it out): its lists would be the content of the versions it does describe, under
    // this version's name. None of them is taken — the screen says the catalogue is not available for the
    // version and opens the numeric-id inputs, as for an empty catalogue.
    const d = reply && reply.covered === false ? null : reply;
    const avatars = normList(d && (d.avatars || d.characters), normAvatar)
      .sort((a, b) => (b.rarity - a.rarity) || a.name.localeCompare(b.name, I18N.code));
    // 4★ and 5★ weapons and sets are offered (5★ first); a catalogue without a rarity field
    // defaults to 5, so a missing field lists everything instead of silently emptying the picker.
    const weapons = normList(d && d.weapons, normWeapon).filter((w) => w.rarity >= 4)
      .sort((a, b) => (b.rarity - a.rarity) || a.name.localeCompare(b.name, I18N.code));
    const sets = normList(d && (d.sets || d.artifactSets), normSet).filter((s) => s.rarity >= 4)
      .sort((a, b) => (b.rarity - a.rarity) || a.name.localeCompare(b.name, I18N.code));
    const costumes = normList(d && d.costumes, normCostume);
    S.gd = { version, status: (avatars.length || weapons.length || sets.length) ? 'ok' : 'empty', error: '', avatars, weapons, sets, costumes };
    if (!S.gd.avatars.some((a) => a.id === S.sel.avatarId)) S.sel.avatarId = avatars.length ? avatars[0].id : null;
    if (!S.gd.weapons.some((w) => w.id === S.sel.weaponId)) S.sel.weaponId = null;
    if (!S.gd.sets.some((s) => s.name === S.sel.setName)) S.sel.setName = null;
    if (S.gd.status !== 'ok') S.rawOpen = true;
  } catch (e) {
    if (seq !== gdSeq) return;
    S.gd.status = 'error'; S.gd.error = e.message;
    S.rawOpen = true; // the manual id inputs are the whole capability now — do not make them hunt
  }
  render();
}

// ─────────────────────────── icons ───────────────────────────
const I = {
  diamond: '<svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><path d="M12 2 22 12 12 22 2 12z"/></svg>',
  grid: '<svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"><rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/></svg>',
  download: '<svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12"/><path d="m7 11 5 5 5-5"/><path d="M4 21h16"/></svg>',
  wand: '<svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M15 4V2M15 16v-2M8 9h2M20 9h2M17.8 11.8 19 13M15 9h.01M17.8 6.2 19 5M3 21l9-9M12.2 6.2 11 5"/></svg>',
  stack: '<svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="3" width="20" height="8" rx="2"/><rect x="2" y="13" width="20" height="8" rx="2"/><path d="M6 7h.01M6 17h.01"/></svg>',
  gear: '<svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.6 1.6 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.6 1.6 0 0 0-2.75 1.14V21a2 2 0 1 1-4 0v-.09A1.6 1.6 0 0 0 9 19.4a1.6 1.6 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.6 1.6 0 0 0 4.6 15a1.6 1.6 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.6 1.6 0 0 0 4.6 9a1.6 1.6 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.6 1.6 0 0 0 9 4.6a1.6 1.6 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.6 1.6 0 0 0 2.75 1.14l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.6 1.6 0 0 0 19.4 9a1.6 1.6 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.6 1.6 0 0 0-1.51 1z"/></svg>',
  play: '<svg width="17" height="17" viewBox="0 0 24 24" fill="currentColor"><path d="M6 4l14 8-14 8z"/></svg>',
  folder: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"/></svg>',
  check: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="m5 12 4 4 8-9"/></svg>',
  star: '<svg width="21" height="21" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"><path d="m12 2 2.9 6.3 6.9.6-5.2 4.5 1.6 6.7L12 17l-6.2 3.6 1.6-6.7L2.2 8.9l6.9-.6z"/></svg>',
  user: '<svg width="21" height="21" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/></svg>',
  userSm: '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3"><circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/></svg>',
  atom: '<svg width="21" height="21" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><circle cx="12" cy="12" r="2.6"/><path d="M12 3a3 3 0 0 0 0 6M12 15a3 3 0 0 0 0 6M4.5 7.5a3 3 0 0 0 5.2 3M14.3 13.5a3 3 0 0 0 5.2 3M4.5 16.5a3 3 0 0 1 5.2-3M14.3 10.5a3 3 0 0 1 5.2-3"/></svg>',
  sword: '<svg width="21" height="21" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M14.5 17.5 3 6V3h3l11.5 11.5"/><path d="m13 19 6-6"/><path d="m16 16 4 4"/><path d="m19 21 2-2"/></svg>',
  up: '<svg width="21" height="21" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="m17 11-5-5-5 5"/><path d="m17 18-5-5-5 5"/></svg>',
  coin: '<svg width="21" height="21" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><circle cx="12" cy="12" r="8"/><path d="M12 8v8M9.5 10h5M9.5 14h5"/></svg>',
  moon: '<svg width="21" height="21" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"><path d="M20.5 13.4A8.6 8.6 0 1 1 10.6 3.5a6.7 6.7 0 0 0 9.9 9.9z"/></svg>',
  warn: '<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="#c07b2a" stroke-width="1.9"><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>',
};
const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
// Translation helpers for HTML context: T() = escaped plain text; tf() additionally renders the
// {b}{/b}{i}{/i}{br} placeholders of the few emphasised strings (text stays escaped).
const T = (k, v) => esc(t(k, v));
function tf(k, v) { return esc(t(k, v)).replace(/\{b\}/g, '<b>').replace(/\{\/b\}/g, '</b>').replace(/\{i\}/g, '<i>').replace(/\{\/i\}/g, '</i>').replace(/\{br\}/g, '<br>'); }
// A "?" beside a label: hovering it — or reaching it with Tab — shows `text` (plain text, escaped
// here) in a bubble (relic.css .help; the element it sits in carries .helpbox). Pure CSS on purpose:
// the bubble is a child of the badge, so render() rewriting #app has no state to carry and can leave
// no stale bubble behind. A span, not a button — inside a <label> a button would become the label's
// control — and NO title attribute: WebView2 draws native tooltips, and one would sit on the bubble.
const helpBadge = (text) => `<span class="help" tabindex="0" role="note" aria-label="${esc(text)}">?<span class="tip" aria-hidden="true">${esc(text)}</span></span>`;

// Bitmap icons for the categories with in-game art (artifacts + weapons) — the rest stay SVGs.
const CMDICON = {
  artifact: '<img class="cmdic" src="assets/cmd-artifacts.webp" alt="" decoding="async"/>',
  weapon: '<img class="cmdic" src="assets/cmd-swords.png" alt="" decoding="async"/>',
};

// ─────────────────────────── render ───────────────────────────
// The confirm dialog (S.confirm) the last render drew: its keyboard focus is carried across a redraw.
let confirmDrawn = null;
// A redraw that a field with the caret put off (render() would take the caret and what is being typed), owed until
// the next render(): renderIfOwed makes it once the caret has left the fields, and so does the next quiet status read.
let renderOwed = false;
function render() {
  renderOwed = false;
  const app = document.getElementById('app');
  // render() rewrites the whole app; streamed log events call it while the (taller than the
  // viewport) commands screen is scrolled, so carry the scroll positions across the rewrite or every
  // log line yanks the user back to the top. EVERY pane scrolled at this moment is carried, found by
  // its offset rather than by a list of selectors: the agent-install
  // form is a box of its own with overflow-y:auto, and each of its switches — "Start the agent with
  // Windows", the choice of a server version, the target cards — would otherwise re-render it back to the top.
  const panes = scrolledPanes(app);
  // The consoles are the panes that must follow the NEWEST line, not the old offset: play.log and
  // server.log stream a line at a time and each one re-renders, so restoring a stale scrollTop would
  // pin the user to the top while the messages they are waiting on scroll out of sight below.
  const logs = LOGPANES.map((sel) => logScroll(app, sel));
  // The button of the confirm dialog the keyboard is on — of the dialog drawn last time, when it is still
  // the one on screen: the rewrite would drop the focus with the node.
  const focused = S.confirm && S.confirm === confirmDrawn && document.activeElement && document.activeElement.closest
    && document.activeElement.closest('.overlay [data-act="confirmYes"], .overlay [data-act="confirmNo"]');
  const focusedAct = focused ? focused.getAttribute('data-act') : '';
  // The button the keyboard is on in Quick Start's screen (qsFocusOf): its choices and step actions are reached with it.
  const qsFocus = S.screen === 'quickstart' ? qsFocusOf(app) : null;
  // Quick Start's screen (quickstart.js) takes the window whole while it is open — no sidebar, so no other host editor
  // is in reach — and draws the overlays the click guards wait on itself; without that file the start screen stands.
  const page = S.screen === 'quickstart' && typeof quickStart === 'function' ? quickStart()
    : S.screen === 'login' || S.screen === 'quickstart' ? login() : shell();
  app.innerHTML = titlebar() + page + (S.toast ? toast() : '');
  confirmDrawn = S.confirm;
  panes.forEach((p) => restorePane(app, p));
  logs.forEach((l) => restoreLogScroll(app, l));
  if (qsFocus) qsFocusBack(app, qsFocus);
  // A question whose safe answer is Cancel (S.confirm.focusNo) opens with the keyboard on Cancel; a button
  // the user moved to stays focused across the rewrite.
  if (S.confirm && S.confirm.focusNo) {
    const b = app.querySelector(`.overlay [data-act="${focusedAct || 'confirmNo'}"]`);
    if (b) b.focus();
  }
  // A chip click or a selection inside the picker re-renders it — put the caret back in the search
  // box so the typed query keeps working. Strictly conditional: nothing else may steal focus.
  if (S.picker) {
    const q = app.querySelector('.pickbar input[data-filter]');
    if (q) { q.focus(); q.setSelectionRange(q.value.length, q.value.length); }
  }
  // The progress-bar nodes are new after the rewrite: give them the current value and, if there is
  // still ground to cover up to the target, restart the loop. Without an overlay, stop the loop.
  syncProgress();
  // The start-screen video/audio are static nodes outside #app: only their visibility / play state
  // follows the screen, so they never restart on a render.
  LOGIN_MEDIA.sync();
  // A search of this PC runs only while the wizard's Source step shows its list.
  findLeft();
}
// Every element scrolled away from its top, as {path, top}. The path is one step per node below #app —
// tag, id, class and the index among the siblings that share them —, which is what the same screen
// draws again after a switch is flipped; a pane the new markup no longer has is simply not found, and a
// node at the same path with less content clamps the offset itself. Reading scrollTop on every node is
// cheap: the layout is clean at this point and nothing is written between the reads.
function scrolledPanes(app) {
  const panes = [];
  for (const el of app.querySelectorAll('*')) if (el.scrollTop > 0) panes.push({ path: pathIn(app, el), top: el.scrollTop });
  return panes;
}
function paneKey(el) { return el.tagName + '#' + el.id + '.' + (el.getAttribute('class') || ''); }
function pathIn(app, el) {
  const path = [];
  for (let n = el; n && n !== app; n = n.parentElement) {
    const key = paneKey(n);
    let index = 0;
    for (let s = n.previousElementSibling; s; s = s.previousElementSibling) if (paneKey(s) === key) index++;
    path.unshift({ key, index });
  }
  return path;
}
function restorePane(app, pane) {
  let n = app;
  for (const step of pane.path) {
    let next = null, seen = 0;
    for (const c of n.children) if (paneKey(c) === step.key && seen++ === step.index) { next = c; break; }
    if (!next) return;
    n = next;
  }
  n.scrollTop = pane.top;
}
const LOGPANES = ['#cmdlog', '#srvlog', '#deploylog', '#acctlog', '#joblog'];
function logScroll(app, sel) {
  const el = app.querySelector(sel);
  return { sel, top: el ? el.scrollTop : 0, pinned: !el || el.scrollTop + el.clientHeight >= el.scrollHeight - 4 };
}
function restoreLogScroll(app, s) {
  const el = app.querySelector(s.sel);
  if (el) el.scrollTop = s.pinned ? el.scrollHeight : s.top;
}
// Quick Start's screen is walked with the keyboard too, and the rewrite drops the focus with the node: the button the
// keyboard is on — told by its data-act and data-arg, and by which of the buttons alike it is — takes it again on the
// page drawn, when that is the same page (data-qs-page), the button is still there in the same layer and live, and no
// overlay drawn since stands over it. Nothing else gets the focus: a page drawn anew starts from the top.
function qsFocusOf(app) {
  const el = document.activeElement;
  if (!el || !el.matches || !el.matches('button[data-act]') || !app.contains(el)) return null;
  const arg = el.getAttribute('data-arg');
  const sel = `button[data-act="${CSS.escape(el.getAttribute('data-act'))}"]` + (arg === null ? ':not([data-arg])' : `[data-arg="${CSS.escape(arg)}"]`);
  const page = app.querySelector('[data-qs-page]');
  return { sel, at: Array.from(app.querySelectorAll(sel)).indexOf(el), page: page ? page.getAttribute('data-qs-page') : '', overlay: !!el.closest('.overlay') };
}
function qsFocusBack(app, f) {
  const page = app.querySelector('[data-qs-page]');
  if (S.screen !== 'quickstart' || (page ? page.getAttribute('data-qs-page') : '') !== f.page) return;
  const el = app.querySelectorAll(f.sel)[f.at];
  if (!el || el.disabled || !!el.closest('.overlay') !== f.overlay || (!f.overlay && app.querySelector('.overlay'))) return;
  el.focus({ preventScroll: true });
}

function titlebar() {
  const subtitle = S.screen === 'login' || S.screen === 'quickstart' ? '' : `<span>·  ${T('common.selectedVersion', { id: S.selectedVersionId || '—' })}</span>`;
  const brand = isSummer()
    ? `<img src="assets/dodoco.webp" alt="" style="width:22px;height:22px;object-fit:contain;display:block">`
    : I.diamond;
  return `<div class="titlebar" data-drag>
    <div class="brand" style="color:var(--gold)">${brand}<b>RELIC</b>${subtitle}</div>
    <div class="winbtns">
      <button data-act="win.min" title="${T('common.minimize')}"><svg width="12" height="12" viewBox="0 0 12 12"><line x1="2" y1="6" x2="10" y2="6" stroke="currentColor" stroke-width="1.3"/></svg></button>
      <button class="close" data-act="win.close" title="${T('common.close')}"><svg width="12" height="12" viewBox="0 0 12 12"><line x1="2.5" y1="2.5" x2="9.5" y2="9.5" stroke="currentColor" stroke-width="1.3"/><line x1="9.5" y1="2.5" x2="2.5" y2="9.5" stroke="currentColor" stroke-width="1.3"/></svg></button>
    </div>
  </div>`;
}

// ── start screen ──
// "Which Way?": Player mode (a server address is all it needs) or Server Admin mode (address + agent
// token, or install the agent on a new server). The background video and the music are STATIC nodes
// in index.html (siblings of #app) — render() rewrites #app on every state change and a media element
// inside it would restart each time; LOGIN_MEDIA only toggles visibility and play state.
const LOGIN_MEDIA = (() => {
  let shownBefore = false;
  function sync() {
    const wrap = document.getElementById('login-media');
    const video = document.getElementById('login-bg');
    const audio = document.getElementById('login-audio');
    if (!wrap) return;
    const show = S.screen === 'login';
    wrap.classList.toggle('hidden', !show);
    // In the tray (X → background) nobody sees the screen: nothing plays — the music most of all.
    // Both resume when the window comes back. "Animation off" keeps the video on its current frame
    // (a still picture of the same scene), so the pill switches it without a visual jump.
    if (show && !S.login.hidden) {
      if (video) {
        if (S.login.animOff) { if (!video.paused) video.pause(); }
        else if (video.paused) video.play().catch(() => {});
      }
      if (audio) {
        audio.muted = !!S.login.muted;
        if (!S.login.muted && audio.paused) audio.play().catch(() => { /* autoplay refused: the pill click starts it */ });
        if (S.login.muted && !audio.paused) audio.pause();
      }
      shownBefore = true;
    } else if (shownBefore) {
      if (video && !video.paused) video.pause();
      if (audio && !audio.paused) audio.pause();
    }
  }
  return { sync };
})();

const ICON_LOGIN = {
  motion: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="var(--gold2)" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="M10 9.5v5l4-2.5z" fill="var(--gold2)" stroke="none"/></svg>',
  still: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="#9aa3b5" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="5" width="18" height="14" rx="2"/><line x1="10" y1="9.5" x2="10" y2="14.5"/><line x1="14" y1="9.5" x2="14" y2="14.5"/></svg>',
  sound: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="var(--gold2)" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M11 5 6 9H3v6h3l5 4z"/><path d="M15.5 8.8a4.6 4.6 0 0 1 0 6.4"/><path d="M18.4 5.6a9 9 0 0 1 0 12.8"/></svg>',
  mute: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="#9aa3b5" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M11 5 6 9H3v6h3l5 4z"/><line x1="16" y1="9.5" x2="21" y2="14.5"/><line x1="21" y1="9.5" x2="16" y2="14.5"/></svg>',
  player: '<svg width="23" height="23" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" style="flex-shrink:0"><circle cx="12" cy="8" r="3.6"/><path d="M4.5 21a7.5 7.5 0 0 1 15 0"/></svg>',
  shield: '<svg width="23" height="23" viewBox="0 0 24 24" fill="none" stroke="var(--gold2)" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" style="flex-shrink:0"><path d="M12 3 20 6v5.6c0 4.5-3.4 8.4-8 9.4-4.6-1-8-4.9-8-9.4V6z"/><circle cx="12" cy="11" r="1.7"/><path d="M12 12.7V15"/></svg>',
  quick: '<svg width="23" height="23" viewBox="0 0 24 24" fill="none" stroke="var(--gold2)" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" style="flex-shrink:0"><path d="M15 4V2M15 16v-2M8 9h2M20 9h2M17.8 11.8 19 13M15 9h.01M17.8 6.2 19 5M3 21l9-9M12.2 6.2 11 5"/></svg>',
  info: '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9"><circle cx="12" cy="12" r="9"/><path d="M12 8h.01M11 12h1v4h1"/></svg>',
  lock: '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9"><rect x="4" y="10" width="16" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/></svg>',
  x: '<svg width="11" height="11" viewBox="0 0 12 12"><line x1="2.5" y1="2.5" x2="9.5" y2="9.5" stroke="currentColor" stroke-width="1.4"/><line x1="9.5" y1="2.5" x2="2.5" y2="9.5" stroke="currentColor" stroke-width="1.4"/></svg>',
};

function loginStatusLine() {
  const r = S.login.test;
  if (S.login.testing) return `<div class="lg-status warn"><span class="dot warn"></span><span>${T('login.testing')}</span></div>`;
  if (!r) return '';
  const okAgent = !!(r.agent && r.agent.reachable);
  const okGame = !!(r.game && r.game.reachable === true);
  const cls = okGame || okAgent ? 'ok' : (r.game && r.game.reachable === false ? 'bad' : 'warn');
  let txt;
  if (okAgent) txt = t('login.test.agentOk', { name: r.agent.name || r.host, versions: (r.agent.versionsUp || []).join(', ') || '—' });
  else if (okGame) txt = t('login.test.gameOk', { host: r.host, port: r.port });
  else if (r.game && r.game.reachable === false) txt = t('login.test.down', { host: r.host, port: r.port });
  else txt = t('login.test.unknown');
  const src = r.source === 'txt' ? ` · ${T('login.portFromTxt', { port: r.port })}` : '';
  return `<div class="lg-status ${cls}"><span class="dot ${cls === 'ok' ? 'live' : cls === 'bad' ? 'down' : 'unknown'}"></span><span>${esc(txt)}${src}</span></div>`;
}

// The bare host of what the user typed ("http://Host:21000/" -> "host"): the start screen compares it
// with the host a stored token was saved for.
function hostOf(addr) {
  let h = String(addr || '').trim().toLowerCase();
  const i = h.indexOf('://'); if (i >= 0) h = h.slice(i + 3);
  h = h.split('/')[0];
  if (h.startsWith('[')) { const c = h.indexOf(']'); return c > 0 ? h.slice(1, c) : h; }
  if ((h.match(/:/g) || []).length === 1) h = h.split(':')[0];
  return h.replace(/\.$/, '');
}
// Does a stored/baked token apply to this address? The build's token applies to any host; a token saved
// for another server is not a token for this one (the backend applies the same rule — admin.login lends the
// record of the host it logs in to): a saved one counts for the hosts init's admin.tokenHosts names.
function tokenAppliesTo(address) {
  if (S.admin.tokenScope === 'any') return true;
  const h = hostOf(address);
  return !!h && (S.admin.tokenHosts || []).some((x) => hostOf(x) === h);
}
// Whether the start-screen CTA is usable RIGHT NOW — recomputed on every keystroke (syncLoginCta),
// not only at render: typing does not re-render, and the button would stay disabled until Test.
function loginCanEnter() {
  if (S.login.testing || S.login.savedBusy) return false;
  if (S.login.address.trim().length === 0) return false;
  if (S.login.panel !== 'admin') return true;
  return S.login.token.trim().length > 0 || tokenAppliesTo(S.login.address);
}
// The main CTA only: the "Use the agent on this PC" button (.lg-cta-alt) needs neither address nor token.
// The local-agent buttons of the start screen, patched IN PLACE. Flipping S.localAgent.busy through
// render() would rewrite all of #app for a label and a disabled flag: the whole start
// screen (logo, panel, pills) would be rebuilt under the pointer and read as the launcher "refreshing"
// itself on the click. Same rule as the CTA below and the install overlay: what can be patched is
// patched, never re-rendered.
function syncLocalAgentButtons() {
  const busy = !!S.localAgent.busy;
  const enter = document.querySelector('.lg-cta-alt[data-act="localAgentEnter"]');
  if (enter) {
    enter.disabled = busy;
    enter.textContent = busy ? t('login.localAgent.entering') : t('login.localAgent.enter');
  }
  document.querySelectorAll('[data-act="localAgentUninstall"], [data-act="agentUpdateLocal"]').forEach((b) => { b.disabled = busy; });
}

// ── agent update ──
// "Update available" is a question of content, never of a version number: an agent's build identity —
// the `build` of its status, or the one of the files on this PC — is the one this launcher ships, or it
// is not. An agent that names none is not the shipped one. A build that ships no agent offers nothing.
const agentBuildDiffers = (build) => !!S.agentBuild.shipped && (build || '') !== S.agentBuild.shipped;
// The agent FILES on this PC are not the shipped ones (the start screen, the card of that agent).
const localAgentUpdate = () => !!(S.isWindows && S.agentBuild.local && S.agentBuild.local.installed
  && agentBuildDiffers(S.agentBuild.local.build));
// The admin server is the agent on this PC: it is updated in place, without SSH.
const srvAgentIsLocal = () => !!(S.isWindows && S.agentBuild.local && S.agentBuild.local.adminServer
  && S.srv.info && S.srv.info.platform === 'windows');
// The agent of the admin server answers with another build than the shipped one. Read off an admin
// status only: the public snapshot carries no identity, an agent that does not answer says nothing.
const srvAgentUpdate = () => isAdminMode() && !!S.srv.info && !S.srv.info.public && agentBuildDiffers(S.srv.info.build);
// May this launcher offer that update? The agent on this PC: yes. A remote one is updated over SSH with
// the Linux installer — so not one that runs on Windows (srvAgentElsewhereOnWindows), and not by a guest
// of open administration, who has no SSH access to the server.
const srvAgentUpdateOffered = () => srvAgentUpdate() && (srvAgentIsLocal() || (!S.admin.openKey && S.srv.info.platform !== 'windows'));
// A Windows agent that is not this PC's: only the launcher on the PC it runs on can update it. Said as
// a note without a button (not to a guest, who could do nothing about it).
const srvAgentElsewhereOnWindows = () => srvAgentUpdate() && !srvAgentIsLocal() && S.srv.info.platform === 'windows' && !S.admin.openKey;
// The files on this PC already are the shipped ones and only the running process is not: started
// before them, it is restarted — nothing is replaced.
const srvAgentRestartOnly = () => srvAgentIsLocal() && !localAgentUpdate();
const agentUpdateBusy = () => !!S.agentUpdate || S.localAgent.busy;

// The start screen's row under "Use the agent on this PC" (also patched in place: syncLocalAgentUpdate).
function loginLocalAgentUpdate() {
  if (!localAgentUpdate()) return '';
  return `<div class="lg-update">${ICON_LOGIN.info}<span>${T('login.localAgent.update')}</span><button data-act="agentUpdateLocal" ${agentUpdateBusy() ? 'disabled' : ''}>${T('login.localAgent.updateNow')}</button></div>`;
}
// The answer of agent.build arrives after the start screen is drawn: the row is put in, or taken out,
// in place — the rule of syncLocalAgentButtons.
function syncLocalAgentUpdate() {
  const box = document.querySelector('.lg-local');
  if (!box) return;
  const old = box.querySelector('.lg-update'), html = loginLocalAgentUpdate();
  if (old) old.remove();
  if (!html) return;
  const link = box.querySelector('[data-act="localAgentUninstall"]');
  if (link) link.insertAdjacentHTML('beforebegin', html); else box.insertAdjacentHTML('beforeend', html);
}
// What the update notes are drawn from, and whether the admin server is the agent on this PC (the Agent on
// this PC card says so); a read that changes none of it redraws nothing.
const agentUpdateKey = () => JSON.stringify([localAgentUpdate(), srvAgentUpdate(), srvAgentIsLocal(), S.agentBuild.local ? !!S.agentBuild.local.adminServer : null]);
async function loadAgentBuild() {
  const seq = ++S.agentBuild.seq;
  let r = null;
  try { r = await rpc('agent.build'); } catch (e) { return; }
  if (seq !== S.agentBuild.seq || !r) return;
  const before = agentUpdateKey();
  S.agentBuild.shipped = typeof r.shipped === 'string' && r.shipped ? r.shipped : null;
  S.agentBuild.local = r.local && typeof r.local === 'object' ? r.local : null;
  if (agentUpdateKey() === before) return;
  if (S.screen === 'login') syncLocalAgentUpdate();
  else if (S.screen === 'server' || S.screen === 'tweaks') { if (typingNow()) syncStrip(); renderOrOwe(); }
}

// The status strip's banner (every tab of the Server Admin area): the agent that answers is not the one this
// launcher ships.
function agentUpdateNote() {
  if (srvAgentElsewhereOnWindows()) return `<div class="banner">${I.warn}<div><b>${T('server.banner.agentUpdateTitle')}</b><p>${T('server.banner.agentUpdateWindows')}</p></div></div>`;
  if (!srvAgentUpdateOffered()) return '';
  const local = srvAgentIsLocal(), restart = srvAgentRestartOnly();
  return `<div class="banner">${I.warn}<div><b>${T('server.banner.agentUpdateTitle')}</b>
      <p>${T(restart ? 'server.banner.agentUpdateRestart' : local ? 'server.banner.agentUpdateLocal' : 'server.banner.agentUpdateRemote')}</p>
      <button class="btn" style="margin-top:10px;padding:8px 14px;font-size:12px" data-act="agentUpdate" ${srvBusy() || agentUpdateBusy() ? 'disabled' : ''}>${T(restart ? 'server.banner.agentUpdateRestartNow' : 'server.banner.agentUpdateNow')}</button></div></div>`;
}
// The same offer as a row of the Agent tab's "This server's agent" card (agentInfoCard).
function agentCfgUpdateRow() {
  if (srvAgentElsewhereOnWindows()) return `<div class="note" style="margin-top:12px">${T('server.banner.agentUpdateWindows')}</div>`;
  if (!srvAgentUpdateOffered()) return '';
  const restart = srvAgentRestartOnly();
  return `<div class="note" style="margin-top:12px;display:flex;align-items:center;justify-content:space-between;gap:12px">
      <span>${T(restart ? 'server.banner.agentUpdateRestart' : 'agentCfg.updateNote')}</span>
      <button class="btn" style="padding:8px 14px;font-size:12px;flex:none" data-act="agentUpdate" ${srvBusy() || agentUpdateBusy() || S.agentCfg.restarting ? 'disabled' : ''}>${T(restart ? 'server.banner.agentUpdateRestartNow' : 'server.banner.agentUpdateNow')}</button></div>`;
}

// The update overlay. A remote update first asks for the SSH details (phase 'form'); then, as for the
// agent on this PC, the console follows the run and names how it ended.
function agentUpdateOverlay() {
  const u = S.agentUpdate, local = u.target === 'local';
  if (u.phase === 'form') {
    const f = u.form;
    const field = (key, label, ph, type) =>
      `<label class="fld">${T(label)}</label><input class="txt mono" type="${type || 'text'}" data-model="agentUpdate.form.${key}" value="${esc(f[key] == null ? '' : f[key])}" placeholder="${esc(ph || '')}" autocomplete="off" spellcheck="false">`;
    return `<div class="overlay"><div class="box" style="width:640px;max-height:640px;overflow-y:auto">
      <h3 class="serif" style="margin:0 0 4px">${T('agentUpdate.title.remote')}</h3><p class="hint">${T('agentUpdate.remoteIntro')}</p>
      <h4 class="serif" style="margin:14px 0 4px">${T('deploy.ssh.title')}</h4>
      <div class="grid2">
        <div>${field('sshHost', 'deploy.ssh.host', 'game.example.com')}</div><div>${field('sshPort', 'deploy.ssh.port', '22')}</div>
        <div>${field('sshUser', 'deploy.ssh.user', 'root')}</div><div>${field('sshPassword', 'deploy.ssh.password', '', 'password')}</div>
        <div>${field('sshKeyPath', 'deploy.ssh.key', 'C:\\path\\key.ppk')}<button class="btn ghost" style="margin-top:6px;padding:7px 12px;font-size:12px" data-act="agentUpdatePickKey">${T('common.browse')}</button></div>
        <div>${field('sshKeyPassphrase', 'deploy.ssh.passphrase', '', 'password')}</div>
        <div>${field('sudoPassword', 'deploy.ssh.sudo', '', 'password')}<small class="hint">${T('deploy.ssh.sudoHint')}</small></div>
      </div>
      <div class="row" style="margin-top:12px"><div><b>${T('agentUpdate.force')}</b><small>${T('agentUpdate.forceSub')}</small></div><button class="toggle ${f.upgradeForce ? 'on' : ''}" data-act="agentUpdateForce"><span class="knob"></span></button></div>
      <div class="flex between center" style="margin-top:18px">
        <button class="btn ghost" data-act="agentUpdateClose">${T('common.cancel')}</button>
        <button class="btn glow" data-act="agentUpdateStart">${T('agentUpdate.start')}</button>
      </div></div></div>`;
  }
  const ended = u.phase === 'done' || u.phase === 'error';
  return `<div class="overlay"><div class="box" style="width:640px">
      <h3 class="serif" style="margin:0 0 10px">${T(local ? 'agentUpdate.title.local' : 'agentUpdate.title.running')}</h3>
      <div class="console"><div class="bar"><span class="lights"><i style="background:#e4664e"></i><i style="background:#e8b04a"></i><i style="background:#67b86a"></i></span><span>${T('agentUpdate.console')} · ${esc(local ? 'localhost' : ((u.form && u.form.sshHost) || '—'))}</span></div><div class="log" id="deploylog" style="max-height:260px">${(u.lines || []).map((l) => `<div>${esc(l)}</div>`).join('')}</div></div>
      ${u.phase === 'error' ? `<div class="note" style="margin-top:12px;white-space:pre-wrap">${esc(u.error)}</div>` : ''}
      ${u.phase === 'done' ? (u.warning ? `<div class="note" style="margin-top:12px">${esc(u.warning)}</div>`
    : `<div class="summary" style="margin-top:12px"><div class="k">${T(local ? 'agentUpdate.done.local' : 'agentUpdate.done.remote')}</div></div>`) : ''}
      <div class="flex between center" style="margin-top:16px"><span class="hint" style="margin:0">${ended ? '' : T('agentUpdate.running')}</span>
        <div class="flex gap">${u.phase === 'error' && !local ? `<button class="btn" data-act="agentUpdateBack">${T('deploy.backToForm')}</button>` : ''}<button class="btn ghost" data-act="agentUpdateClose" ${ended ? '' : 'disabled'}>${T('common.close')}</button></div></div></div></div>`;
}
// A run ended. replaced = another agent process may answer the status target from here on — a run about
// that server's agent finished (new files, or the same files started again), or a remote one failed
// after the installer had begun to replace the agent's files — so everything read from the previous one
// is dropped and read again: the status first, or its old `build` would draw the offer once more until
// the new answer arrives. Otherwise — a run that failed before that, or a run about the agent on this PC
// while the admin server is another machine — the server's agent is the one it was: what the admin
// typed and has not saved (Agent settings, a gameplay-settings draft) is still that agent's and stays;
// only the status is read again. Either way only for the server the run was started under — after a
// switch the cards are another server's.
function agentUpdateEnded(u, replaced) {
  S.localAgent.busy = false;
  loadAgentBuild();
  const here = !!u && u.srvTarget === srvTarget() && isAdminMode() && S.serverConfigured && S.screen !== 'login';
  if (here && replaced) {
    S.srv = { state: 'checking', error: '', versions: {}, info: null };
    S.hotpatch = hotpatchBlank();
    agentCfgSeq++; S.agentCfg = agentCfgBlank();
    agentJobsSeq++; agentJobLogSeq++; S.agentJobs = agentJobsBlank();
    twSeq++; S.tw = twBlank(); twPickerGone();
    // The account list was the previous process's answer — and whether this agent has the route at all is its own. A
    // copy's question asked over that list goes with it: the copy it would start is gone.
    S.accts = acctsBlank();
    if (S.confirm && S.confirm.act === 'acctsCopyGo') S.confirm = null;
    migrateReset();
    render(); checkServers(true);
    loadLocalAgent(); loadPolicy(); loadHotpatch(S.selectedVersionId || (S.versions[0] && S.versions[0].id)); loadAgentCfg();
    loadAcctTemplatesAll();
    return;
  }
  render();
  if (here) { checkServers(true); loadLocalAgent(); }
}
// replaced: only a remote run can fail AFTER the installer began to replace the agent's files (the
// backend says so); a local one that fails has changed nothing, put the previous agent back, or left
// the agent stopped — nothing answers that the cards could be read from again.
function agentUpdateFailed(message, replaced) {
  const u = S.agentUpdate;
  if (u) { u.phase = 'error'; u.error = message || t('common.unknownError'); }
  agentUpdateEnded(u, replaced === true);
}

function syncLoginCta() {
  const b = document.querySelector('.lg-cta:not(.lg-cta-alt)');
  if (b) b.disabled = !loginCanEnter();
  // (Save beside Test and the Save of its row: dead while the address is empty)
  document.querySelectorAll('[data-act="loginSave"], [data-act="loginSaveGo"]').forEach((s) => { s.disabled = !loginCanSave(); });
  const tk = document.querySelector('input[data-model="login.token"]');
  if (tk) tk.placeholder = tokenAppliesTo(S.login.address) ? t('login.tokenKept') : t('login.tokenPlaceholder');
}

function loginPanel() {
  const isAdmin = S.login.panel === 'admin';
  const chips = (S.defaults.servers || []).map((s) =>
    `<button class="lg-chip ${S.login.address === s.host ? 'on' : ''}" data-act="loginChip" data-arg="${esc(s.host)}">${esc(s.label || s.host)}</button>`).join('');
  const canEnter = loginCanEnter();
  // Entry animation through once(): every button in the panel (chips, Test, tabs, a toast) goes
  // through render(), which rewrites #app — a CSS animation on the class would replay the whole
  // box flying in on each click, which reads as the panel closing and reopening.
  return `<div class="lg-panel" style="${once('login:panel', 'rl-boxin', .35)}">
    <span class="corner tl"></span><span class="corner tr"></span><span class="corner bl"></span><span class="corner br"></span>
    <div class="ph"><h2>${isAdmin ? T('login.panel.admin') : T('login.panel.player')}</h2>
      <button class="x" data-act="loginClose" title="${T('common.back')}">${ICON_LOGIN.x}</button></div>
    <div class="lg-tabs">
      <button class="${!isAdmin ? 'on' : ''}" data-act="loginTab" data-arg="player">${T('login.tab.player')}</button>
      <button class="${isAdmin ? 'on' : ''}" data-act="loginTab" data-arg="admin">${T('login.tab.admin')}</button>
    </div>
    <label class="lg-label">${T('login.serverLabel')}</label>
    ${chips ? `<div class="lg-chips">${chips}</div>` : ''}
    <div class="lg-row">
      <input class="txt mono" data-model="login.address" value="${esc(S.login.address)}" placeholder="${T('login.serverPlaceholder')}" spellcheck="false" autocomplete="off">
      <button class="lg-test" data-act="loginTest" ${S.login.testing ? 'disabled' : ''}>${T('login.test')}</button>
      <button class="lg-test" data-act="loginSave" ${loginCanSave() ? '' : 'disabled'}>${T('login.saved.save')}</button>
    </div>
    ${loginStatusLine()}
    ${loginSavingRow()}
    ${loginSavedList()}
    ${S.login.saving ? '' : `<div class="lg-hint">${ICON_LOGIN.info}<span>${T('login.serverHint')}</span></div>`}
    ${isAdmin ? `
    <label class="lg-label">${T('login.tokenLabel')}</label>
    <input class="txt mono" type="password" data-model="login.token" value="${esc(S.login.token)}" placeholder="${esc(tokenAppliesTo(S.login.address) ? t('login.tokenKept') : t('login.tokenPlaceholder'))}" autocomplete="off">
    <div class="lg-hint">${ICON_LOGIN.lock}<span>${T('login.tokenHint')}</span></div>
    <label class="lg-label">${T('login.agentPortLabel')}</label>
    <input class="txt mono" data-model="login.agentPort" value="${esc(S.login.agentPort)}" placeholder="${hostOf(S.login.address) && hostOf(S.login.address) === hostOf(S.serverAddr.host) ? S.serverAddr.agentPort : 18080}" inputmode="numeric" autocomplete="off" spellcheck="false">
    <div class="lg-hint">${ICON_LOGIN.info}<span>${T('login.agentPortHint')}</span></div>
    <button class="lg-link" data-act="deployOpen">${T('login.installAgentLink')}</button>
    <button class="lg-link" data-act="openGuide" style="display:block;margin-top:6px">${T('help.guideLink')}</button>` : ''}
    <button class="lg-cta" data-act="loginEnter" ${canEnter ? '' : 'disabled'}>${isAdmin ? T('login.cta.admin') : T('login.cta.player')}</button>
    ${loginQuickStartLink()}
    ${isAdmin ? loginLocalAgent() : ''}
  </div>`;
}

// ── Quick Start (its screens and its runner: quickstart.js) ──
// The start screen's ways in: the highlighted entry above the two modes (relic.css .lg-qs) and a link under each
// panel's main button. Both open ACTIONS.qsOpen — a fresh draft on the Mode page, or the progress page of a run that
// was stopped half way (paused), which the entry then offers to continue, beside a link that discards it after one
// question. Without quickstart.js nothing of it is drawn.
const qsHere = () => typeof quickStart === 'function';
// A run this PC keeps that is not ended: paused is the start screen's to offer; one that runs or waits for the restart
// is boot's to open (a done run is never kept), and is offered the same way when the start screen is drawn over it.
const qsKept = () => !!(S.qsPlan && typeof S.qsPlan.state === 'string' && S.qsPlan.state && S.qsPlan.state !== 'done');
// A server job is the one Quick Start's run started: quickstart.js holds that one as S.qs.job until its outcome is in,
// which its own listener takes after the handlers of this file (false without quickstart.js).
const qsRunsJob = (j) => !!(j && S.qs && S.qs.job && S.qs.job.kind === j.kind && String(S.qs.job.version) === String(j.version));
function loginQuickStart() {
  if (!qsHere()) return '';
  const cont = qsKept();
  // (the glow is continuous: phased so a redraw does not restart it — phaseOf; the entry plays once — once)
  return `<div class="lg-qs${cont ? ' cont' : ''}" style="${once('login:qs', 'rl-boxin', .45)}">
    <button data-act="qsOpen" style="animation-delay:${phaseOf(6)}">${ICON_LOGIN.quick}<span class="t"><b>${cont ? T('login.qs.continue') : T('login.qs.title')}</b><small>${cont ? T('login.qs.continueSub') : T('login.qs.sub')}</small></span></button>
    ${cont ? `<button class="lg-qs-discard" data-act="qsDiscardAsk">${T('login.qs.discard')}</button>` : ''}
  </div>`;
}
function loginQuickStartLink() {
  if (!qsHere()) return '';
  return `<button class="lg-link lg-qs-link" data-act="qsOpen" ${S.login.savedBusy ? 'disabled' : ''}>${qsKept() ? T('login.qs.continue') : T('login.qs.link')}</button>`;
}

// ── saved servers: an address, a mode and a name per host — never a token ──
// Drawn on both tabs of the panel. Save (beside Test) keeps what the field names under a name; a row fills
// the panel as a chip does, Connect enters through the start screen's own path, Delete forgets everything
// Relic keeps for the host (no question), Delete all asks once.
const savedById = (id) => (Array.isArray(S.savedServers) ? S.savedServers : []).find((e) => e && String(e.id) === String(id)) || null;
// The saved server of an address's host (one entry per host), or null.
const savedOfHost = (address) => {
  const h = hostOf(address);
  return h ? (Array.isArray(S.savedServers) ? S.savedServers : []).find((e) => e && hostOf(e.host || e.address) === h) || null : null;
};
// What a row is called: its name, or its address when it has none.
const savedName = (e) => String((e && (e.label || e.address)) || '');
// Save is dead while the address is empty, and while a saved-server rpc is in flight.
const loginCanSave = () => S.login.address.trim().length > 0 && !S.login.savedBusy;
// The Save row as it opens for an address: the name of its host's saved entry (none included), else the host — 40
// characters at most —, and that host, the one the row is for.
function loginSavingFor(address) {
  const known = savedOfHost(address);
  return { label: (known ? String(known.label || '') : hostOf(address)).slice(0, 40), host: hostOf(address) };
}
// The row Save opens: the name (prefilled with the host by loginSave), its Save and Cancel. Enter in the
// name is its Save (data-enter), never the panel's Enter.
function loginSavingRow() {
  const sv = S.login.saving;
  if (!sv) return '';
  return `<div class="lg-saving">
      <span class="lg-sv-namelbl">${T('login.saved.name')}</span>
      <input class="txt" data-model="login.saving.label" data-enter="loginSaveGo" value="${esc(sv.label)}" placeholder="${T('login.saved.namePlaceholder')}" maxlength="40" spellcheck="false" autocomplete="off">
      <button class="lg-test lg-sv-save" data-act="loginSaveGo" ${loginCanSave() ? '' : 'disabled'}>${T('login.saved.save')}</button>
      <button class="lg-test" data-act="loginSaveCancel">${T('common.cancel')}</button>
    </div>`;
}
// One row per saved server: its name (or address), "in use" when it is the configured host in the configured
// mode; the address (when the name is not the address itself), the mode, "token saved" when server.json holds
// a token for the host. Clicking the row picks it (savedPick) — its name is that pick's button, so the keyboard
// reaches it too; its two buttons act. Delete all sits in the list's head, at the right of its title.
function loginSavedList() {
  const list = Array.isArray(S.savedServers) ? S.savedServers.filter((e) => e && e.id) : [];
  if (!list.length) return '';
  const dead = S.login.savedBusy ? 'disabled' : '';
  const rows = list.map((e) => {
    const id = esc(e.id), name = savedName(e), addr = String(e.address || ''), admin = e.mode === 'admin';
    return `<div class="lg-sv-row${e.current ? ' cur' : ''}" data-act="savedPick" data-arg="${id}">
        <div class="lg-sv-name"><button class="lg-sv-pick" data-act="savedPick" data-arg="${id}" title="${esc(name)}"><b>${esc(name)}</b></button>${e.current ? `<span class="lg-sv-cur">${T('login.saved.current')}</span>` : ''}</div>
        <div class="lg-sv-acts">
          <button class="lg-sv-go" data-act="savedConnect" data-arg="${id}" ${dead}>${T('login.saved.connect')}</button>
          <button class="lg-sv-del" data-act="savedDelete" data-arg="${id}" ${dead}>${T('login.saved.delete')}</button>
        </div>
        <div class="lg-sv-meta">${name !== addr ? `<span class="lg-sv-addr mono" title="${esc(addr)}">${esc(addr)}</span>` : ''}<span class="lg-sv-mode ${admin ? 'admin' : 'player'}">${admin ? T('common.mode.admin') : T('common.mode.player')}</span>${e.hasToken ? `<span class="lg-sv-tok">${ICON_LOGIN.lock}${T('login.saved.tokenKept')}</span>` : ''}</div>
      </div>`;
  }).join('');
  return `<div class="lg-saved">
      <div class="lg-sv-head"><div class="lg-label lg-sv-title">${T('login.saved.title')}</div><button class="lg-sv-all" data-act="savedDeleteAllAsk" ${dead}>${T('login.saved.deleteAll')}</button></div>
      <div class="lg-sv-list">${rows}</div>
    </div>`;
}
// A click that puts an address in the field — a saved server's pick, a quick-pick chip, the admin tab a refused
// Connect opens: a token typed there for another host goes with the address it was typed for, so such a click never
// carries a typed token to another server.
function loginAddressIn(address) {
  if (hostOf(address) !== hostOf(S.login.address)) S.login.token = '';
  S.login.address = address;
}
// A reply of servers.delete / servers.deleteAll: the init state (a Server Admin mode left without a token went
// back to the start screen's question) and the list.
function savedReply(r) {
  if (r && r.state) applyInit(r.state);
  if (r && Array.isArray(r.servers)) S.savedServers = r.servers;
}
// A Delete or Delete all that failed: the list and the mode are read again — the backend may have forgotten part of
// what it was asked before the failure (an entry already gone is backend.servers.unknown the next time).
async function savedReread() {
  try { savedReply({ state: await rpc('app.init') }); } catch (e) { /* the list stays as drawn */ }
}

// Admin tab, Windows, an agent installed on this PC (the init-state fact, no health probe): a second
// way in that needs neither the address nor the token — localagent.enter reads both from the agent's
// own config, starts it when it is not running and validates the token before anything is saved.
// The uninstall link is the counterpart of the Uninstall button of the Agent tab's "Agent on this PC" card (same
// confirm).
function loginLocalAgent() {
  const init = S.localAgent.init;
  if (!S.isWindows || !init || !init.installed) return '';
  const busy = S.localAgent.busy;
  return `<div class="lg-local">
    <button class="lg-cta lg-cta-alt" data-act="localAgentEnter" ${busy ? 'disabled' : ''}>${busy ? T('login.localAgent.entering') : T('login.localAgent.enter')}</button>
    <div class="lg-hint">${ICON_LOGIN.info}<span>${T('login.localAgent.hint', { host: init.host || '127.0.0.1', port: init.port || 18080 })}</span></div>
    ${loginLocalAgentUpdate()}
    <button class="lg-link" data-act="localAgentUninstall" ${busy ? 'disabled' : ''}>${T('login.localAgent.uninstallLink')}</button>
  </div>`;
}

// The agent port typed on the admin panel: null when the field is empty (the backend then resolves
// it — TXT record, the port known for the host, 18080), a number when it is a valid port, false when
// it is neither (the caller refuses instead of silently dialling the default).
function loginAgentPort() {
  const raw = String(S.login.agentPort || '').trim();
  if (!raw) return null;
  const n = /^\d{1,5}$/.test(raw) ? parseInt(raw, 10) : NaN;
  return n >= 1 && n <= 65535 ? n : false;
}
// The agent port Test and Enter send with the address: on the admin tab the typed one (loginAgentPort); the Player tab
// has no such field — there the saved server of that host brings the port it was saved with, as its Connect does
// (null = none: the backend resolves it).
function loginPortFor(address) {
  if (S.login.panel === 'admin') return loginAgentPort();
  const e = savedOfHost(address);
  return e && e.agentPort >= 1 && e.agentPort <= 65535 ? e.agentPort : null;
}

function loginFootPill() {
  const host = (S.serverAddr && S.serverAddr.host) || '';
  if (!host) return `<div class="pill"><span class="dot unknown"></span>${T('login.noServer')}</div>`;
  const live = S.srv.state === 'ok' ? srvLiveId() : null;
  const known = S.srv.state === 'ok' && (!!live || !srvDockerError());
  // Up, and the game server still loads its data: not "online" yet — nobody gets in until it is done.
  const lf = live ? srvFacts(live) : null;
  const loading = !!lf && lf.loading && !lf.degraded;
  const dot = known ? (live ? (loading ? 'warn' : 'live') : 'down') : 'unknown';
  const label = known ? (live ? (loading ? t('login.pill.loading') : t('login.pill.online')) : t('login.pill.offline')) : t('login.pill.unknown');
  return `<div class="pill"><span class="dot ${dot}"></span>${esc(label)} · ${esc(host)}</div>`;
}

function login() {
  // The logo float is continuous: phase it so the rewrite of #app does not restart it (see phaseOf).
  const logo = isSummer()
    ? `<img class="dodoco" src="assets/dodoco.webp" alt="" style="animation-delay:${phaseOf(5)}">`
    : `<span class="float" style="animation-delay:${phaseOf(5)}">${I.diamond.replace('width="17" height="17"', 'width="58" height="58"')}</span>`;
  const muted = !!S.login.muted, still = !!S.login.animOff;
  return `<div class="login">
    <div class="lg-pills">
      <button class="lg-mute" data-act="loginAnim" title="${T('login.anim')}">${still ? ICON_LOGIN.still : ICON_LOGIN.motion}${still ? T('login.animOff') : T('login.animOn')}</button>
      <button class="lg-mute" data-act="loginMute" title="${T('login.music')}">${muted ? ICON_LOGIN.mute : ICON_LOGIN.sound}${muted ? T('login.muted') : T('login.musicOn')}</button>
    </div>
    <div class="lg-head">${logo}<div><div class="logo">RELIC</div><div class="tagline">${T('login.tagline')}</div></div></div>
    ${S.login.panel ? loginPanel() : `
    <div class="lg-which"><i></i><span>${T('login.whichWay')}</span><i class="r"></i></div>
    ${loginQuickStart()}
    <div class="lg-mode player"><button data-act="pickMode" data-arg="player" style="animation-delay:${phaseOf(3.4)}">${ICON_LOGIN.player}<span class="t"><b>${T('login.mode.player')}</b><small>${T('login.mode.playerSub')}</small></span></button></div>
    <div class="lg-mode admin"><button data-act="pickMode" data-arg="admin" style="${once('login:modes', 'rl-boxin', .5)}">${ICON_LOGIN.shield}<span class="t"><b>${T('login.mode.admin')}</b><small>${T('login.mode.adminSub')}</small></span></button></div>
    <div class="lg-foot">${loginFootPill()}</div>`}
    ${S.confirm ? confirmOverlay() : S.notice ? noticeOverlay() : S.agentUpdate ? agentUpdateOverlay() : S.agentInstall && S.agentInstall.open ? agentInstallOverlay() : ''}
  </div>`;
}

function shell() {
  // The overlays live on the SHELL, not inside .main: they must cover the sidebar too, so nothing
  // (Play, navigation) is clickable while one is up, on whichever screen the user is. Priority:
  // the confirm dialog wins (it is always an explicit question), then install, then the game-launch
  // splash, then the content picker.
  // The install overlay / the wizard are gone entirely: their "once" animations may run again next time.
  if (!S.installing) onceForget('inst:');
  if (S.screen !== 'install') { onceForget('wiz:'); wizStep = -1; }
  const over = S.confirm ? confirmOverlay()
    : S.notice ? noticeOverlay()
      : S.installing ? installOverlay()
        : S.remove ? removeOverlay()
          : S.agentUpdate ? agentUpdateOverlay()
          : S.agentInstall && S.agentInstall.open ? agentInstallOverlay()
            : S.agentCfg.reloc ? relocOverlay()
            : S.playing ? playOverlay()
              : S.picker ? pickerOverlay() : '';
  return `<div class="shell">${sidebar()}<div class="main scr-${mainKey()}">${mainContent()}</div>${over}</div>`;
}

// "Are you sure?" — one generic dialog for the irreversible actions (the progress reset, a purge...)
// and, with `options`, the chooser of the Prepare-the-server dialog: option cards above the body, the
// body and the Yes label following the selected option (bodyFor / yesFor), Yes red only for an option
// flagged dangerous (a plain dialog stays red — every one of them asks about something irreversible —
// unless it says danger: false). With no / noAct the second button is an answer of its own, not Cancel.
// The wide chooser has no warning icon: those 74 px are what lets Prepare the server — three cards,
// the body with the vendor-secrets sentence, the pathfinding row, the buttons — fit its
// box (relic.css .box.wide) and open with all of it in sight, nothing to scroll to.
// The backdrop does NOT close the dialog (a selection drag that slips outside the box would close it
// by accident), and the buttons — the option cards too — ignore the first ~400 ms: the second click
// of a double-click on the button that opened it would otherwise land exactly on "Yes" or "Cancel"
// (or pick an option) before the user can read.
const CONFIRM_ARM_MS = 400;
function confirmOverlay() {
  const c = S.confirm;
  const opts = Array.isArray(c.options) && c.options.length ? c.options : null;
  const sel = opts ? (opts.find((o) => o.id === c.selected) || opts[0]) : null;
  const body = (sel && c.bodyFor && c.bodyFor[sel.id]) || c.body || '';
  const yes = (sel && c.yesFor && c.yesFor[sel.id]) || c.yes || t('confirm.yesDefault');
  const danger = opts ? !!(sel && sel.danger) : c.danger !== false;
  const cards = opts ? `<div class="copts">${opts.map((o) => `<div class="opt ${sel && sel.id === o.id ? 'sel' : ''}" data-act="confirmPick" data-arg="${esc(o.id)}"><div class="h">${esc(o.label)}</div>${o.sub ? `<p>${esc(o.sub)}</p>` : ''}</div>`).join('')}</div>` : '';
  const cb = c.checkbox, hp = c.hotpatch;
  return `<div class="overlay"><div class="box${c.wide ? ' wide' : ''}" style="text-align:center">
    ${c.wide ? '' : `<div style="width:60px;height:60px;border-radius:50%;margin:0 auto 14px;background:rgba(208,106,94,.15);display:flex;align-items:center;justify-content:center">${I.warn}</div>`}
    <div class="serif" style="font-weight:700;font-size:19px">${esc(c.title)}</div>
    ${cards}
    <p style="color:var(--inkSoft);font-size:12.5px;font-weight:600;line-height:1.6;margin:10px 0 22px;text-align:left">${esc(body)}</p>
    ${cb ? `<label class="row" style="margin:0 0 18px;cursor:pointer;text-align:left;gap:14px"><div><b>${esc(cb.label)}</b>${cb.sub ? `<small>${esc(cb.sub)}</small>` : ''}</div><button class="toggle ${cb.checked ? 'on' : ''}" data-act="confirmCheckbox"><span class="knob"></span></button></label>` : ''}
    ${hp ? `<label class="row" style="margin:0 0 18px;cursor:pointer;text-align:left;gap:14px"><div><b>${esc(hp.label)}</b>${hp.sub ? `<small>${esc(hp.sub)}</small>` : ''}</div><button class="toggle ${hp.checked ? 'on' : ''}" data-act="confirmHotpatch"><span class="knob"></span></button></label>` : ''}
    <div class="flex center gap" style="justify-content:center">
      <button class="btn ghost" data-act="confirmNo">${c.no ? esc(c.no) : T('common.cancel')}</button>
      <button class="btn${danger ? ' danger' : ''}" data-act="confirmYes">${esc(yes)}</button>
    </div></div></div>`;
}

// Information window — the "OK only" twin of the confirm dialog: same drawing, one button. No arming
// window: there is no dangerous "Yes" for a double-click to steal. Drawn on the start screen too (login):
// a notice raised while it is up — an admin create that finished after "Change mode or server" — would
// otherwise be invisible while the click guard still treats it as modal. `pick`: a one-time secret (a
// server-generated password) shown as a selectable credential with a Copy button — the body text is
// user-select:none and a 16-character random password must not be copied by hand. `rows`: several
// values to read AND copy, [{label, value, copy}] — the Secrets card's "Show current values", where the
// MySQL root password may be 32 random characters the agent picked: a line each, the value
// selectable with its own Copy button when `copy` is set, plain words otherwise (the MUIP line is a
// fingerprint, never the key). A notice made of rows needs no body.
function noticeOverlay() {
  const n = S.notice;
  const pick = typeof n.pick === 'string' && n.pick
    ? `<div class="creds" style="margin:0 0 18px;text-align:left"><div class="crow"><div><small>${T('lib.creds.password')}</small><b class="mono pick">${esc(n.pick)}</b></div><button class="crcopy" data-act="noticeCopy">${T('common.copy')}</button></div></div>`
    : '';
  const rows = Array.isArray(n.rows) && n.rows.length
    ? `<div class="creds" style="margin:${n.body ? 0 : 14}px 0 18px;text-align:left">${n.rows.map((r, i) => `<div class="crow"><div style="min-width:0"><small>${esc(r.label)}</small>${r.copy
      ? `<b class="mono pick" style="word-break:break-all">${esc(r.value)}</b></div><button class="crcopy" data-act="noticeCopyRow" data-arg="${i}">${T('common.copy')}</button>`
      : `<b class="pw">${esc(r.value)}</b></div>`}</div>`).join('')}</div>`
    : '';
  const body = n.body || !rows
    ? `<p style="color:var(--inkSoft);font-size:12.5px;font-weight:600;line-height:1.6;margin:10px 0 ${pick || rows ? 12 : 22}px;text-align:left">${esc(n.body)}</p>` : '';
  return `<div class="overlay"><div class="box" style="text-align:center">
    <div style="width:60px;height:60px;border-radius:50%;margin:0 auto 14px;background:rgba(208,106,94,.15);display:flex;align-items:center;justify-content:center">${I.warn}</div>
    <div class="serif" style="font-weight:700;font-size:19px">${esc(n.title)}</div>
    ${body}
    ${pick}${rows}
    <div class="flex center gap" style="justify-content:center">
      <button class="btn" data-act="noticeOk">${T('common.understood')}</button>
    </div></div></div>`;
}

// "Starting the game" splash — the in-app counterpart of the native window of the --play shortcut.
// Closes itself when the client has actually appeared (play.appeared) or on error/end.
function playOverlay() {
  const p = S.playing;
  return `<div class="overlay"><div class="box" style="text-align:center">
    <div class="flex center gap" style="justify-content:center;margin-bottom:10px">${spinnerHtml()}
      <div class="serif" style="font-weight:700;font-size:19px">${T('play.starting')}</div></div>
    <div style="color:var(--goldD);font-weight:800;font-size:12px;letter-spacing:2px;margin-bottom:14px">${T('play.versionLabel', { id: p.versionId })}</div>
    <p style="color:var(--inkSoft);font-size:12.5px;font-weight:600;min-height:34px;margin:0 0 16px;word-break:break-word">${esc(p.msg || t('play.preparing'))}</p>
    ${credsLine(p.versionId)}
    ${eeWanted(p.versionId) ? `<p style="color:var(--goldD);font-size:11.5px;font-weight:700;margin:0 0 10px">${T('play.eeHint')}</p>` : ''}
    <p style="color:var(--muted);font-size:11.5px;font-weight:700;margin:0 0 14px">${T('play.firstStartNote')}</p>
    <button class="btn ghost" style="padding:8px 16px;font-size:12px" data-act="hidePlaySplash">${T('common.hide')}</button>
  </div></div>`;
}

function navBtn(id, label, icon) {
  // The Server Admin area is ONE entry, lit on both of its screens ('server' and Game rules, 'tweaks'): on Game
  // rules a click enters Game rules again; from any other screen it opens the 'server' screen on the tab it showed
  // last (S.adminTab — Game rules is reached through its tab).
  const area = id === 'server' && isAdminMode() && S.screen === 'tweaks';
  return `<button class="nav ${S.screen === id || area ? 'active' : ''}" data-act="go" data-arg="${area ? 'tweaks' : id}"><span style="display:flex">${icon}</span><span>${label}</span></button>`;
}

// Mode gating of the navigation. Player mode: Library, Install, Server (status + own account) and
// Settings; GM commands only when the server's policy allows players to send them. Admin mode:
// everything. This only hides entries — the backend refuses admin rpcs on its own (RequireAdmin).
const isAdminMode = () => S.mode === 'admin';
// The tabs the 'server' screen draws in Server Admin mode (Game rules is the 'tweaks' screen).
const ADMIN_SERVER_TABS = ['overview', 'players', 'content', 'access', 'agent'];
const adminTabOn = () => (ADMIN_SERVER_TABS.includes(S.adminTab) ? S.adminTab : 'overview');
const playerCommandsAllowed = () => !!(S.pub.status && S.pub.status.playerCommands);
function sidebar() {
  const brand = isSummer()
    ? `<img src="assets/dodoco.webp" alt="" style="width:32px;height:32px;object-fit:contain;display:block;flex:0 0 auto">`
    : `<span style="color:var(--gold)">${I.diamond.replace(/17/g,'26')}</span>`;
  const showCommands = isAdminMode() || playerCommandsAllowed();
  const modeLabel = isAdminMode() ? t('common.mode.admin') : t('common.mode.player');
  return `<div class="sidebar">
    <div class="head">${brand}<div><b>RELIC</b><small>${T('common.selectedVersion', { id: S.selectedVersionId || '—' })}</small></div></div>
    <div class="divider"></div>
    ${navBtn('library', T('common.nav.library'), I.grid)}
    ${navBtn('install', T('common.nav.install'), I.download)}
    ${showCommands ? navBtn('commands', T('common.nav.commands'), I.wand) : ''}
    ${navBtn('server', isAdminMode() ? T('common.nav.serverAdmin') : T('common.nav.server'), I.stack)}
    ${navBtn('settings', T('common.nav.settings'), I.gear)}
    <div class="spacer"></div>
    <button class="userchip" data-act="changeMode" title="${T('common.changeMode')}" style="width:100%;text-align:left;cursor:pointer">
      <div class="av">${isAdminMode() ? 'A' : T('common.playerInitial')}</div>
      <div style="min-width:0"><b>${esc(modeLabel)}</b><small style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;display:block">${esc(S.serverAddr.host || t('login.noServer'))}</small></div>
    </button>
  </div>`;
}

function mainContent() {
  switch (S.screen) {
    case 'library': return library();
    case 'install': return install();
    case 'commands': return commands();
    case 'tweaks': return isAdminMode() ? tweaks() : library();
    case 'server': return server();
    case 'settings': return settings();
    default: return library();
  }
}
// The key of .main: the screen, and in Server Admin mode the tab of the Server area. render() puts each scrolled
// pane back by its tag#id.class path (restorePane), so another key — a tab switch, a screen switch — opens at the
// top, while a render of the same screen and tab keeps its place. It follows what mainContent draws.
function mainKey() {
  switch (S.screen) {
    case 'library': case 'install': case 'commands': case 'settings': return S.screen;
    case 'tweaks': return isAdminMode() ? 'rules' : 'library';
    case 'server': return isAdminMode() ? 'server-' + adminTabOn() : 'server';
    default: return 'library';
  }
}

// ── in-game login details ──
// The account is not created anywhere: it comes ready-made in the version's save, so the only thing
// that can block the player on the client's login screen is not knowing it. One block, the same words
// everywhere (end of install, library, game start) — otherwise they would learn two forms for the same
// thing. The values are mouse-selectable (the rest of the app is user-select:none) and have a copy
// button, because "aetherr" with two r's is easy to mistype.
function credsBox(id) {
  const acc = accountOf(id);
  if (!acc) return '';
  return `<div class="creds">
    <div class="crow">
      <div><small>${T('lib.creds.accountName')}</small><b class="mono pick">${esc(acc)}</b></div>
      <button class="crcopy" data-act="copyAccount" data-arg="${esc(acc)}">${T('common.copy')}</button>
    </div>
    <div class="crow">
      <div><small>${T('lib.creds.password')}</small><b>${esc(passwordWord(id))}</b></div>
    </div>
  </div>`;
}

// One-line variant, for the places too narrow for the block (the "Starting the game" splash). A version
// this server has no ready-made login for (noLoginHere) gets the line that says so.
function credsLine(id) {
  const acc = accountOf(id);
  if (!acc) return noLoginHere(id) ? `<div class="credline">${T('lib.creds.lineNone', { id })}</div>` : '';
  return `<div class="credline">${T('lib.creds.lineAccount')} <b class="mono pick">${esc(acc)}</b> · ${T('lib.creds.linePassword')} <b>${esc(passwordWord(id))}</b></div>`;
}

// ── no ready-made login ──
// A version this server has no pre-made in-game account for — its payload ships no save, or the stack
// was prepared with "keep my progress" / "fixes only" — and none this launcher created there: without a
// word the player reaches the game's login screen with nothing to type. Said only on the server's own
// word read this session (S.srvAccount: the admin /status or the public snapshot named the version
// without one); before such an answer an empty accountOf says only that nothing is known.
const noLoginHere = (id) => hasOwn(S.srvAccount, id) && !accountOf(id);
// Where an account comes from on this server: in Server Admin mode the Players tab's "Create a player
// account"; else the player's own account card where the server lets players create one (acctOffer);
// else its admin, who hands accounts out. Each with its long sentence (the Library, the end of an
// install) and its short one (a summary row); only the first two have a button to that card.
const LOGIN_WAY = {
  admin: { long: 'lib.login.admin', short: 'lib.login.adminShort', create: true },
  signup: { long: 'lib.login.signup', short: 'lib.login.signupShort', create: true },
  ask: { long: 'lib.login.ask', short: 'lib.login.askShort', create: false },
};
const loginWay = (id) => LOGIN_WAY[isAdminMode() ? 'admin' : acctOffer(id) ? 'signup' : 'ask'];
const loginCreateBtn = (id, style) => (loginWay(id).create
  ? `<button class="btn" ${style ? `style="${style}"` : ''} data-act="loginCreate" data-arg="${esc(id)}">${T('lib.login.create')}</button>` : '');

// Library card: shown only for an INSTALLED version, because only then does the player have a use
// for it — and it is the first thing under the hero with the PLAY button, i.e. exactly where they
// look before going in. A version without a ready-made login gets the card that says where one comes
// from instead.
function accountSect(id) {
  if (!accountOf(id)) {
    if (!noLoginHere(id)) return '';
    return `<div class="sect" style="margin-bottom:18px">
      <h4 style="margin:0">${T('lib.login.title', { id })}</h4>
      <p class="hint" style="margin:8px 0 ${loginWay(id).create ? '12px' : '0'}">${T(loginWay(id).long, { id })}</p>
      ${loginCreateBtn(id, 'padding:9px 16px;font-size:12.5px')}
    </div>`;
  }
  return `<div class="sect" style="margin-bottom:18px">
    <div class="flex between center wrap" style="gap:10px">
      <h4 style="margin:0">${T('lib.account.title', { id })}</h4>
      <span class="vchip">${T('lib.account.chip')}</span>
    </div>
    <p class="hint" style="margin:8px 0 12px">${T('lib.account.hint')}</p>
    ${credsBox(id)}
  </div>`;
}

// ── voice languages ──
// The catalogue's voice packs of a version ([{lang, size, sizeText, isDefault}], in the game's own
// language order — the backend builds the list, nothing here hard-codes a name); [] for an older
// catalogue or before the backend answered.
const voicesFor = (id) => { const v = versionById(id); return v && Array.isArray(v.voices) ? v.voices : []; };
// The packs ticked for an install of `id`. The ticks live by language (S.wiz.voices), so a choice
// carries over from one version to another; a version none of whose packs is ticked — the first visit,
// or the ticked language exists only on another version — seeds the default pack (English, else
// the first one): the list is never empty, because the game needs a voice language to start with
// and the backend refuses an install without one.
function selectedVoicePacks(id) {
  const packs = voicesFor(id);
  let ticked = packs.filter((p) => S.wiz.voices[p.lang] === true);
  if (!ticked.length && packs.length) {
    const d = packs.find((p) => p.isDefault) || packs[0];
    S.wiz.voices[d.lang] = true; ticked = [d];
  }
  return ticked;
}
const selectedVoices = (id) => selectedVoicePacks(id).map((p) => p.lang);
const packBytes = (packs) => packs.reduce((n, p) => n + (Math.max(0, Number(p.size)) || 0), 0);
// What is on disk for an installed version: the Audio_<Lang>_pkg_version markers the backend detects
// at every start / registration / add (installed[].voices) — never the wizard's ticks.
const installedVoices = (id) => { const i = S.installed.find((x) => x.id === id); return i && Array.isArray(i.voices) ? i.voices : []; };

// Library: the voice languages of an installed version — the catalogue's packs against what is on
// disk. A missing one is added from here, one language per job (install.addVoices, the same overlay
// as an install), so the player learns it BEFORE the game asks the server for 3–10 GB at login.
// It sits UNDER the "Your versions" grid: the versions are what the player came for,
// a voice pack is an occasional add-on — only the account card stays between the hero and the grid.
function voicesSect(id) {
  const packs = voicesFor(id);
  if (!packs.length) return '';
  const have = installedVoices(id);
  const busy = S.installing || S.remove || S.playing ? 'disabled' : '';
  const rows = packs.map((p) => {
    const on = have.includes(p.lang);
    return `<div class="line"><div><b>${esc(p.lang)}</b><small>${esc(p.sizeText || sizeGb(p.size))}</small></div>
      ${on ? `<span style="color:var(--ok);font-weight:800;font-size:12px;white-space:nowrap"><span class="dot" style="background:var(--ok);display:inline-block;margin-right:6px;vertical-align:middle"></span>${T('lib.voices.installed')}</span>`
        : `<button class="btn ghost" style="padding:8px 12px;font-size:12px;white-space:nowrap" data-act="addVoice" data-arg="${esc(id + '|' + p.lang)}" ${busy}>${T('lib.voices.add', { gb: gb(p.size) })}</button>`}</div>`;
  }).join('');
  return `<div class="sect" style="margin:24px 0 0"><h4>${T('lib.voices.title', { id })}</h4>
    <p class="hint">${T('lib.voices.hint')}</p>${rows}</div>`;
}

// The connected server lists this version with present:false — it does not have it. Only on FRESH data:
// never while checking, after an error or without an entry for the version (an older agent, or an
// unreachable one, must not mark a version as missing).
function notHostedHere(id) {
  const e = S.srv.versions && S.srv.versions[id];
  return !!(S.serverConfigured && S.srv.state === 'ok' && e && e.present === false);
}

// ── the end of a play session: the Library's notice ──
// S.session (see S): the closing step while it runs — PLAY is off meanwhile —, then what it did, or the official
// profile put back by the recovery loop. Its words are the backend's (already translated); its tone is read from the
// facts: a client never seen (appeared false) is a launch that failed whatever the kind, a restore that waits for a
// client is a warning. Another screen than the Library gets a toast of the same words; a notice of the past is put
// away by its ×, by the next PLAY and by the next notice.
const sessionClosing = () => !!S.session && S.session.phase === 'closing';
const sessionText = (d, fallback) => (d && typeof d.text === 'string' && d.text.trim() ? d.text : fallback);
function sessionTone(n) {
  if (n.phase === 'closing') return 'busy';
  if (n.phase === 'profile') return 'ok';
  if (n.appeared === false) return 'bad';
  return n.kind === 'deferredOwn' || n.kind === 'deferredForeign' ? 'warn' : 'ok';
}
function sessionSay(n) {
  S.session = n;
  if (S.screen === 'library') { render(); return; }
  const tone = sessionTone(n);
  showToast(n.text, tone === 'bad' ? 'bad' : tone === 'ok' ? 'ok' : '');
}
function sessionNote() {
  const n = S.session;
  if (!n || !n.text) return '';
  const tone = sessionTone(n);
  const icon = tone === 'busy' ? spinnerSmall() : tone === 'ok' ? I.check
    : I.warn.replace('width="22" height="22"', 'width="18" height="18"').replace('stroke="#c07b2a"', 'stroke="currentColor"');
  return `<div class="sessnote ${tone}" role="status">
    <span class="sn-ic">${icon}</span>
    <div class="sn-body">${n.versionId ? `<small>${T('play.versionLabel', { id: n.versionId })}</small>` : ''}<span>${esc(n.text)}</span></div>
    ${tone === 'busy' ? '' : `<button class="sn-x" data-act="sessionNoteClose" title="${T('common.close')}">×</button>`}
  </div>`;
}

// ── library ──
function library() {
  const sel = selected();
  if (!sel) return `<div class="pad"><h1 class="title">${T('lib.title')}</h1><p class="sub">${T('lib.noCatalog')}</p></div>`;
  const selInstalled = isInstalled(sel.id);
  const cards = S.versions.map((v) => {
    const inst = isInstalled(v.id);
    const isSel = v.id === S.selectedVersionId;
    const cta = !inst
      ? `<div class="cta install">${I.download} ${T('lib.card.install')}</div>`
      : (isSel ? `<div class="cta sel">${I.check} ${T('lib.card.selected')}</div>` : `<div class="cta pick">${T('lib.card.select')}</div>`);
    // On an installed version, "Installed" alone tells the user nothing useful: the game account is
    // the information they actually need later, so it sits on the card next to the install state.
    const acc = inst && accountOf(v.id)
      ? `<small class="accline">${I.userSm} ${T('lib.ingameAccount')} <b class="mono">${esc(accountOf(v.id))}</b></small>` : '';
    // The server serves the official in-version fixes: the client fetches them once at login, so
    // the player learns here why the first login of this version shows a download.
    const hp = hotpatchServed(v.id)
      ? `<small class="accline" title="${T('lib.hotpatchTitle')}"><span class="dot" style="background:var(--ok)"></span> ${T('lib.hotpatchServed')}</small>` : '';
    // Before a multi-GB download (or a PLAY that can never connect): the server has no stack for it.
    const nh = notHostedHere(v.id)
      ? `<small class="accline" style="color:var(--muted)"><span class="dot" style="background:var(--warn)"></span> ${T('lib.card.notHosted')}</small>` : '';
    return `<div class="vcard ${isSel ? 'sel' : ''}" data-act="selectVersion" data-arg="${v.id}">
      <div class="thumb" ${heroStyle(v.id)}><span class="st"><span class="dot" style="background:${inst ? 'var(--ok)' : '#d06a5e'}"></span>${inst ? T('lib.card.installed') : T('lib.card.new')}</span>${inst && eeWanted(v.id) ? `<span class="st ee">${T('lib.card.enhancements')}</span>` : ''}<span class="vid">${esc(v.id)}</span></div>
      <div class="body"><b>${esc(v.title)}</b><small>${esc(v.desc)}</small><small class="sz">${esc(v.size)}</small>${acc}${hp}${nh}${cta}</div>
    </div>`;
  }).join('');

  // (off while a session's end runs: it holds the session gate until it is done)
  const playBtn = selInstalled
    ? `<button class="btn big" data-act="play" data-arg="${sel.id}" ${sessionClosing() ? 'disabled' : ''}>${I.play} ${T('lib.play')}</button>`
    : `<button class="btn big" data-act="selectVersion" data-arg="${sel.id}">${I.download} ${T('lib.installBig')}</button>`;
  // Per-version removal: a launcher install is uninstalled (files deleted by default), an imported
  // folder is only unregistered unless the user opts in to deleting it (the confirm dialog asks).
  const removeBtn = selInstalled
    ? `<button class="btn dark" data-act="removeVersion" data-arg="${sel.id}" title="${T('lib.remove.title')}">${T('lib.remove.button')}</button>` : '';

  // "Install another version" closes the grid of version cards: a tile of its own beside them, or — when
  // the versions fill their rows of three — a bar across the grid rather than a tile alone on a row.
  return `<div>
    <div class="hero" ${isSummer() ? summerStyle(sel.id) : heroStyle(sel.id)}><div class="inner">
      <div class="kicker">${T('lib.hero.kicker')}</div>
      <h2>${T('common.versionN', { id: sel.id })}</h2>
      <div class="flex center gap wrap">
        <span class="badge"><span class="dot" style="background:${selInstalled ? 'var(--ok)' : 'var(--warn)'}"></span>${selInstalled ? T('lib.card.installed') : T('lib.hero.notInstalled')}</span>
        ${srvBadge()}
        ${selInstalled && accountOf(sel.id) ? `<span class="badge">${I.userSm} ${T('lib.ingameAccount')} <b class="mono" style="color:var(--gold2)">${esc(accountOf(sel.id))}</b></span>` : ''}
        ${selInstalled && noLoginHere(sel.id) ? `<span class="badge">${I.userSm} ${T('lib.login.badge')}</span>` : ''}
        ${selInstalled && eeWanted(sel.id) ? `<span class="badge"><span class="dot" style="background:var(--gold)"></span>${T('lib.hero.enhancements')}</span>` : ''}
        ${selInstalled && installedVoices(sel.id).length ? `<span class="badge">${T('lib.hero.voices', { langs: installedVoices(sel.id).join(', ') })}</span>` : ''}
        ${hotpatchServed(sel.id) ? `<span class="badge" title="${T('lib.hotpatchTitle')}"><span class="dot" style="background:var(--ok)"></span>${T('lib.hotpatchServed')}</span>` : ''}
        <span class="badge">${I.stack.replace('width="19" height="19"', 'width="13" height="13"')}${esc(sel.size)}</span>
      </div>
      <div class="flex center gap" style="margin-top:6px">${playBtn}
        <button class="btn dark" data-act="serverOverview">${T('lib.checkServer')}</button>${removeBtn}</div>
    </div></div>
    <div class="pad">
      ${sessionNote()}
      ${selInstalled ? accountSect(sel.id) : ''}
      <div class="flex between center" style="margin-bottom:16px"><div class="serif" style="font-weight:700;font-size:19px">${T('lib.yourVersions')}</div>
        <span style="color:var(--muted);font-size:12.5px;font-weight:700">${T('lib.installedCount', { n: S.installed.length })}</span></div>
      <div class="grid3">${cards}
        <button class="addcard" ${S.versions.length % 3 === 0 ? 'style="grid-column:1/-1;min-height:0;flex-direction:row;padding:14px"' : ''} data-act="go" data-arg="install"><span style="width:52px;height:52px;border-radius:50%;background:color-mix(in srgb, var(--gold) 16%, transparent);display:flex;align-items:center;justify-content:center;color:var(--goldD)"><svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg></span>${T('lib.installAnother')}</button>
      </div>
      ${selInstalled ? voicesSect(sel.id) : ''}
    </div>
  </div>`;
}

// ── install wizard ──
// The step list is the one place the count lives: the stepper, the Continue/START switch, wizNext's
// clamp and the dev autoinstall all read LAST — a step added here must not leave one of them at a
// hard-coded count, i.e. an unreachable step or a START button one step early.
const STEPS = ['wizard.steps.version', 'wizard.steps.isolation', 'wizard.steps.source', 'wizard.steps.voices', 'wizard.steps.fiddler', 'wizard.steps.shortcut'];
const LAST = STEPS.length - 1;
// Last step DRAWN. Every keystroke in a wizard field redraws the whole screen, so the panel's entry
// animation cannot be tied to render, only to the step change: on any change (forward or back) the
// key is forgotten so the new step animates in again.
let wizStep = -1;
function install() {
  const step = S.installStep;
  if (wizStep !== step) { wizStep = step; onceForget('wiz:'); }
  const stepper = STEPS.map((key, i) => {
    const cls = i < step ? 'done' : (i === step ? 'active' : 'todo');
    const inner = i < step ? I.check : (i + 1);
    return `<div class="step"><div class="flex center" style="gap:9px;cursor:pointer" data-act="wizGoto" data-arg="${i}"><span class="num ${cls}">${inner}</span><span class="lbl">${T(key)}</span></div>${i < LAST ? '<span class="bar"></span>' : ''}</div>`;
  }).join('');

  return `<div class="pad">
    <h1 class="title">${T('wizard.title')}</h1>
    <p class="sub">${T('wizard.sub')}</p>
    <div class="stepper">${stepper}</div>
    <div class="panel" style="${once('wiz:' + step, 'rl-fadein', .26)}">${wizardStep(step)}</div>
    <div class="wizfoot">
      ${step > 0 ? `<button class="btn ghost" data-act="wizPrev">${T('common.back')}</button>` : '<span></span>'}
      ${step < LAST ? `<button class="btn" data-act="wizNext">${T('common.continue')}</button>`
        : `<button class="btn glow" style="font-family:'Cinzel',serif;letter-spacing:1px" data-act="startInstall" ${wizStartOk() ? '' : 'disabled'}>${T('wizard.start')}</button>`}
    </div>
  </div>`;
}

// Mirrors come from the catalogue, not from code: each version may have different alternative hosts,
// and the id is what gets saved in the settings.
const mirrorsFor = (versionId) => (S.versions.find((v) => v.id === versionId) || {}).mirrors || [];
const allMirrors = () => [...new Map(S.versions.flatMap((v) => v.mirrors || []).map((m) => [m.id, m])).values()];
// The official CDN is one of a version's hosts only when the catalogue names it there (versions[].hasCdn):
// a version may be hosted on mirrors alone.
const hasCdn = (v) => !v || v.hasCdn !== false;
// The hosts a download of this version can start from, in the order the backend tries them when none is
// preferred (InstallService.BuildCandidates): the CDN, the mirrors, Drive.
function versionSources(versionId) {
  const v = versionById(versionId);
  return [...(hasCdn(v) ? ['cdn'] : []), ...mirrorsFor(versionId).map((m) => m.id), ...(v && v.hasDrive ? ['drive'] : [])];
}
// The host a download of this version really starts from: the preferred one when the version has it,
// else the first one it has — exactly what the backend does with a preference the version does not
// carry (it stays on its default order). Read-only: the saved preference is never rewritten by it.
const effectiveSource = (versionId, source) => {
  const have = versionSources(versionId);
  return have.includes(source) ? source : (have[0] || 'cdn');
};
// Label of a source; an id no version carries is named as it is.
const sourceLabel = (id) => (id === 'drive' ? 'Google Drive'
  : id === 'local' ? t('wizard.source.local')
  : id === 'cdn' ? t('wizard.source.cdn')
  : (allMirrors().find((m) => m.id === id) || {}).label || id);

function wizardStep(step) {
  const w = S.wiz;
  if (step === 0) {
    // v.size = the game + the English voices; with more packs to pick from at the Voices step it is
    // a "from" figure, not the total.
    const opts = S.versions.map((v) => `<div class="opt center ${w.versionId === v.id ? 'sel' : ''}" data-act="pickVersion" data-arg="${v.id}">
      <div class="vbig">${esc(v.id)}</div><div style="color:var(--muted);font-size:11.5px;font-weight:700;margin-top:4px">${esc(v.desc)}</div>
      <div style="color:var(--inkLbl);font-size:11.5px;font-weight:800;margin-top:8px">${voicesFor(v.id).length > 1 ? T('wizard.step.version.sizeFrom', { size: v.size }) : esc(v.size)}</div></div>`).join('');
    // Non-blocking: the version may still be wanted for another server.
    const nh = w.versionId && notHostedHere(w.versionId)
      ? `<p class="hint" style="margin:10px 0 0">${T('install.notHostedHint', { id: w.versionId })}</p>` : '';
    return `<h3>${T('wizard.step.version.title')}</h3><p class="hint">${T('wizard.step.version.hint')}</p><div class="grid3">${opts}</div>${nh}${wizWindows11Note(w.versionId)}`;
  }
  if (step === 1) {
    return `<h3>${T('wizard.step.isolation.title')}</h3><p class="hint">${T('wizard.step.isolation.hint')}</p>
      <div class="opt sel"><div class="h">${I.user} ${T('wizard.step.isolation.optTitle')}</div>
      <p>${T('wizard.step.isolation.optBody')}</p></div>`;
  }
  if (step === 2) {
    const vid = w.versionId || S.selectedVersionId;
    const mirrors = mirrorsFor(vid);
    // A saved source this version does NOT have (a mirror only another version carries, the CDN on a
    // version hosted on mirrors alone) is not an error: the backend stays on the version's default
    // order, so the host that order starts from is exactly what we show as selected.
    const eff = w.source === 'local' ? 'local' : effectiveSource(vid, w.source);
    const card = (id, icon, title, note) => `<div class="opt ${eff === id ? 'sel' : ''}" data-act="setSource" data-arg="${esc(id)}">
        <div class="h">${icon} ${esc(title)}</div><p>${esc(note)}</p></div>`;
    // The CDN and Google Drive are offered only when the catalogue carries them for this version (a
    // private catalogue may name a Drive id; the public build ships none — the card stays hidden, the
    // chain skips it).
    const v = versionById(vid);
    const cards = [
      ...(hasCdn(v) ? [card('cdn', I.download, t('wizard.source.cdn'), t('wizard.source.cdnNote'))] : []),
      ...(v && v.hasDrive ? [card('drive', I.download, 'Google Drive', t('wizard.source.driveNote'))] : []),
      ...mirrors.map((m) => card(m.id, I.download, m.label, m.note || t('wizard.source.mirrorNote'))),
      card('local', I.folder, t('wizard.source.local'), t('wizard.source.localNote')),
    ];
    return `<h3>${T('wizard.step.source.title')}</h3><p class="hint">${T('wizard.step.source.hint')}</p>
      <div class="${cards.length > 3 ? 'grid2' : 'grid3'}">${cards.join('')}</div>
      ${eff !== w.source ? `<p class="hint" style="margin:10px 0 0">${T('wizard.step.source.missing', { version: vid || '', source: sourceLabel(w.source), fallback: sourceLabel(eff) })}</p>` : ''}
      ${w.source === 'local' ? localSourceHtml(vid) : ''}`;
  }
  if (step === 3) {
    const id = w.versionId || S.selectedVersionId;
    // A folder already on disk: the packs are detected when it is registered; nothing to pick here.
    if (w.source === 'local') return `<h3>${T('wizard.step.voices.title')}</h3><p class="hint">${T('wizard.step.voices.localHint')}</p>`;
    const packs = voicesFor(id);
    const ticked = selectedVoicePacks(id);
    const rows = packs.map((p) => `<div class="line"><div><b>${esc(p.lang)}</b><small>${esc(p.sizeText || sizeGb(p.size))}</small></div>
      <button class="toggle ${ticked.includes(p) ? 'on' : ''}" data-act="wizVoiceToggle" data-arg="${esc(p.lang)}"><span class="knob"></span></button></div>`).join('');
    const client = Math.max(0, Number((versionById(id) || {}).clientSize)) || 0, voices = packBytes(ticked);
    // No packs = an older catalogue / backend: the hint alone, no "0.0 GB" total to puzzle over.
    return `<h3>${T('wizard.step.voices.title')}</h3><p class="hint">${T('wizard.step.voices.hint')}</p>
      ${rows ? `<div class="sect" style="padding:2px 18px;margin-bottom:0">${rows}</div>
      <p class="hint" style="margin:14px 0 0">${T('wizard.step.voices.total', { total: gb(client + voices), client: gb(client), voices: gb(voices) })}</p>` : ''}`;
  }
  if (step === 4) {
    return `<h3>${T('wizard.step.fiddler.title')}</h3><p class="hint">${T('wizard.step.fiddler.hint')}</p>
      <div class="row" style="margin-bottom:12px"><div><b>${T('common.decryptHttps')}</b><small>${T('wizard.step.fiddler.decryptNote')}</small></div>${toggle('decrypt', w.decrypt)}</div>
      <div class="row" style="margin-bottom:12px"><div><b>${T('wizard.step.fiddler.cert')}</b><small>${T('wizard.step.fiddler.certNote')}</small></div>${toggle('cert', w.cert)}</div>
      <label class="fld">${T('wizard.step.fiddler.serverAddress')}</label>
      ${isAdminMode() ? addressReadOnly() : `<input class="txt mono" data-model="addrEdit" value="${esc(addrShown())}" placeholder="${T('login.serverPlaceholder')}" spellcheck="false" autocomplete="off"/>`}`;
  }
  // step LAST (5): shortcut + summary
  const name = w.shortcutName || t('wizard.step.finish.defaultShortcut', { version: w.versionId || S.selectedVersionId || '' });
  const isLocal = w.source === 'local';
  // The label must name the host the download will ACTUALLY use: if the chosen version does not have
  // the source saved in settings, the backend starts from the first one it has — the summary would lie
  // otherwise.
  const srcLabel = isLocal ? t('wizard.source.local') : sourceLabel(effectiveSource(w.versionId || S.selectedVersionId, w.source));
  // The languages the download will bring, with the whole figure (game + packs): the number the user
  // must have seen before START, not at "Not enough space" after a 30 GB download.
  const packs = isLocal ? [] : selectedVoicePacks(w.versionId || S.selectedVersionId);
  const voicesRow = packs.length
    ? `<div>${T('wizard.step.finish.sumVoices')} <b>${esc(packs.map((p) => p.lang).join(', '))}</b> · ${esc(sizeGb((Math.max(0, Number((versionById(w.versionId || S.selectedVersionId) || {}).clientSize)) || 0) + packBytes(packs)))}</div>` : '';
  // A folder of the player's own: what its check says, under it — START waits for "ready" (wizStartOk).
  const location = isLocal
    ? `<label class="fld" style="margin-top:0">${T('wizard.step.finish.localFolder')}</label>
       <div><input class="txt mono" value="${esc(w.localPath || '—')}" disabled/></div>
       <div class="impv" data-live="impv" data-mode="short" style="margin-bottom:14px">${impHtml('short')}</div>`
    : `<label class="fld" style="margin-top:0">${T('wizard.step.finish.location')}</label>
       <div class="flex gap" style="margin-bottom:14px"><input class="txt mono" data-model="settings.installRoot" value="${esc(S.settings.installRoot)}"/><button class="btn ghost" data-act="pickFolder">${T('common.browse')}</button></div>`;
  return `<h3>${T('wizard.step.finish.title')}</h3><p class="hint">${isLocal ? T('wizard.step.finish.hintLocal') : T('wizard.step.finish.hint')}</p>
    ${location}
    <div class="row" style="margin-bottom:14px"><div class="flex center gap"><b>${T('wizard.step.finish.desktopShortcut')}</b></div>${toggle('shortcut', w.shortcut)}</div>
    ${w.shortcut ? `<label class="fld">${T('wizard.step.finish.shortcutName')}</label><input class="txt" data-model="wiz.shortcutName" value="${esc(name)}"/>` : ''}
    <div class="summary" style="margin-top:16px"><div class="k">${T('wizard.step.finish.summary')}</div><div class="g">
      <div>${T('wizard.step.finish.sumVersion')} <b>${esc(w.versionId || '—')}</b></div><div>${T('wizard.step.finish.sumSource')} <b>${esc(srcLabel)}</b></div>
      ${voicesRow}
      <div>${T('wizard.step.finish.sumIsolation')} <b>${T('wizard.step.finish.sumIsolationValue')}</b></div><div>${T('wizard.step.finish.sumServer')} <b class="mono">${esc(!isAdminMode() && S.addrEdit != null && S.addrEdit.trim() !== addrText() ? S.addrEdit.trim() : `${S.serverAddr.host}:${S.serverAddr.port}`)}</b></div>
      ${accountOf(w.versionId) ? `<div>${T('wizard.step.finish.sumAccount')} <b class="mono">${esc(accountOf(w.versionId))}</b></div><div>${T('wizard.step.finish.sumPassword')} <b>${esc(passwordWord(w.versionId))}</b></div>`
        : w.versionId && noLoginHere(w.versionId) ? `<div style="grid-column:1/-1">${T('wizard.step.finish.sumAccount')} <b>${T(loginWay(w.versionId).short)}</b></div>` : ''}
    </div></div>${wizWindows11Note(w.versionId)}`;
}
// A version whose client is not known to start on this PC's Windows through Relic's launch path
// (versions[].windows11Unsupported): said where it is picked and again above START — before a download
// of tens of GB, not at the first PLAY.
function wizWindows11Note(id) {
  return id && (versionById(id) || {}).windows11Unsupported
    ? `<div class="note" style="margin-top:12px">${T('wizard.windows11Note', { id })}</div>` : '';
}

// ── the wizard's local folder: the check of what the field names, and "Find it on this PC" ──
// The field takes the game's folder or the path of its GenshinImpact.exe (Explorer's "Copy as path" quotes included);
// Browse... opens the file dialog on GenshinImpact.exe and puts the folder of the file chosen in the field. What the
// field names is checked as the chosen version while it is typed — install.inspectGame, the very check the import runs
// before its first write: the instant check first (it can refuse, never say ready), then the deep one, which hashes
// Relic's files as the import does and alone says ready. START waits for that ready (wizStartOk).
function localSourceHtml(vid) {
  const w = S.wiz;
  return `<label class="fld">${T('wizard.step.source.localFolder')}</label>
    <div class="flex gap"><input class="txt mono" data-model="wiz.localPath" value="${esc(w.localPath)}" placeholder="${T('wizard.step.source.localPlaceholder', { version: vid || '' })}" spellcheck="false" autocomplete="off"/><button class="btn ghost" style="flex:none" data-act="pickLocalGame">${T('common.browse')}</button></div>
    <div class="impv" data-live="impv" data-mode="full">${impHtml('full')}</div>
    <p class="hint" style="margin:10px 0 0">${T('wizard.step.source.localHint')}</p>
    ${findSect()}`;
}
// A path as the player types it, reduced to the folder it names, for comparing two of them: quotes, the trailing
// GenshinImpact.exe and separators dropped, either slash, any case (Windows' own folder names ignore case).
function dirOfPathText(p) {
  let s = String(p == null ? '' : p).trim().replace(/^"+|"+$/g, '').trim().replace(/\//g, '\\');
  if (/(^|\\)genshinimpact\.exe$/i.test(s)) s = s.slice(0, -'GenshinImpact.exe'.length);
  return s.replace(/\\+$/, '').toLowerCase();
}
const sameDirText = (a, b) => !!dirOfPathText(a) && dirOfPathText(a) === dirOfPathText(b);
// The field's text for what the file dialog chose: the folder of a GenshinImpact.exe (a drive's root keeps its
// backslash); any other file stays as it was chosen, and the check names it.
function pickedGameDir(file) {
  const s = String(file || '');
  if (!/[\\/]genshinimpact\.exe$/i.test(s)) return s;
  const dir = s.slice(0, -'GenshinImpact.exe'.length - 1);
  return /^[A-Za-z]:$/.test(dir) ? dir + '\\' : dir;
}
// The folder the file dialog opens in: the one the field names, as typed (the dialog falls back to its own when it
// does not exist).
function impInitialDir() {
  let s = impPath().replace(/^"+|"+$/g, '').trim();
  if (/[\\/]genshinimpact\.exe$/i.test(s)) s = s.slice(0, -'GenshinImpact.exe'.length - 1);
  return s;
}
const spinnerSmall = () => `<span class="rl-spin" style="animation-delay:${phaseOf(0.9)}"></span>`;

// The check of the local folder. key = what it was asked about — the field's text, the version, the folder chosen to be
// replaced (impKey); phase: 'idle' (nothing asked about key) | 'quick' (the instant check runs) | 'deep' (the deep one
// runs, verdict = the instant one's answer) | 'done' (verdict = the answer) | 'error' (the rpc failed: error) |
// 'cancelled' (the backend gave the check up: a newer check, or an import that took its turn). stale = the answer may be
// out of date (the Library changed, the wizard was entered again): it is asked again before it counts. replace = the
// player chose this folder over the one the version is installed at ({path, versionId, installedAt}: it counts only
// while the field and the version are those). hit = the warning of a found folder taken from the list ({dir, text}),
// shown while the field names that folder.
function impBlank() { return { key: '', phase: 'idle', verdict: null, error: '', stale: false, replace: null, hit: null }; }
let impSeq = 0, impTimer = 0;
// The pause in the typing after which what the field names is checked.
const IMP_TYPED_MS = 450;
const impPath = () => String(S.wiz.localPath || '').trim();
const impVersion = () => S.wiz.versionId || S.selectedVersionId || '';
function impReplaceAt() {
  const r = S.imp.replace;
  return r && r.path === impPath() && r.versionId === impVersion() ? r.installedAt : '';
}
const impKey = () => [impPath(), impVersion(), impReplaceAt()].join('\n');
// The check about what the field names now, or null: an answer about another text, version or choice says nothing of it.
function impNow() { const im = S.imp; return impPath() && im.key === impKey() && !im.stale ? im : null; }
// The verdict START goes on with: a deep check's "ready" about what the field names now.
function impReady() { const im = impNow(); return im && im.phase === 'done' && im.verdict && im.verdict.status === 'ready' ? im.verdict : null; }
const wizStartOk = () => S.wiz.source !== 'local' || !!impReady();
// Asks the check: the instant one, then — when it found nothing against the folder (status 'checking') — the deep one,
// with the same request. The last check asked wins (impSeq): the backend cancels an older one, whose late answer is
// dropped here. force: ask again although an answer about the same request is in (Check again, a found folder, Browse).
async function impCheck(force) {
  clearTimeout(impTimer); impTimer = 0;
  // None while an install of this page runs (it holds the install guard; an import holds the checks too): what the field
  // names is then newer than the last check, and the next look at the folder once the install is over asks it.
  if (S.installing) return;
  const im = S.imp, key = impKey();
  if (!impPath()) { impSeq++; Object.assign(im, { key, phase: 'idle', verdict: null, error: '', stale: false }); syncImp(); return; }
  if (!force && im.key === key && !im.stale && (im.phase === 'quick' || im.phase === 'deep' || im.phase === 'done')) return;
  const seq = ++impSeq;
  const req = { path: S.wiz.localPath, versionId: impVersion() };
  const at = impReplaceAt();
  if (at) req.replaceInstalledAt = at;
  Object.assign(im, { key, phase: 'quick', verdict: null, error: '', stale: false });
  syncImp();
  const ask = async (deep) => {
    try { return { v: await rpc('install.inspectGame', Object.assign({}, req, { deep })) }; }
    catch (e) { return { error: (e && e.message) || String(e) }; }
  };
  let a = await ask(false);
  if (seq !== impSeq || impSettled(im, a)) return;
  im.verdict = a.v;
  if (a.v.status !== 'checking') { im.phase = 'done'; syncImp(); return; }
  im.phase = 'deep'; syncImp();
  a = await ask(true);
  if (seq !== impSeq || impSettled(im, a)) return;
  im.verdict = a.v; im.phase = 'done'; syncImp();
}
// An answer that ends the check without a verdict — the rpc failed, or the backend gave the check up ({status:
// 'cancelled'}, dropped as an answer): true when it did.
function impSettled(im, a) {
  const v = a.v;
  if (a.error || !v || typeof v !== 'object' || typeof v.status !== 'string') {
    im.phase = 'error'; im.error = a.error || t('common.unknownError'); syncImp(); return true;
  }
  if (v.status === 'cancelled') { im.phase = 'cancelled'; syncImp(); return true; }
  return false;
}
// A keystroke in the field: the check is asked once the typing has paused, and its box says meanwhile that the folder is
// being checked. An edit drops the choice made for the folder the field named, and a found folder's warning.
function impTyped() {
  const im = S.imp;
  if (im.replace && im.replace.path !== impPath()) im.replace = null;
  if (im.hit && !sameDirText(im.hit.dir, impPath())) im.hit = null;
  clearTimeout(impTimer); impTimer = 0;
  if (!impPath()) { impCheck(); return; }
  impTimer = setTimeout(() => { impTimer = 0; impCheck(); }, IMP_TYPED_MS);
  syncImp();
}
// Asks the check when the local folder is chosen and nothing current answers about what the field names. A check that
// failed or was given up is asked again by its own button only.
function impEnsure() {
  if (S.screen !== 'install' || S.wiz.source !== 'local' || S.installing || !impPath() || impTimer) return;
  const im = S.imp;
  if (im.key === impKey() && !im.stale && im.phase !== 'idle') return;
  impCheck(true);
}
// The check's boxes and START follow in place: an answer may land while a field of the wizard has the caret. A box is
// never redrawn under a pressed pointer (the button the click lands on would be swapped): it waits for the pointer to
// come up.
let impOwed = false;
function syncImp() {
  if (S.screen !== 'install') return;
  if (pointerDown) { impOwed = true; return; }
  impOwed = false;
  document.querySelectorAll('#app [data-live="impv"]').forEach((el) => { el.innerHTML = impHtml(el.getAttribute('data-mode')); });
  const go = document.querySelector('#app .wizfoot [data-act="startInstall"]');
  if (go) go.disabled = !wizStartOk();
}
// The notes of a verdict that do not stop the import; the ones that ask for care are drawn as warnings.
const IMP_NOTE_WARN = ['unknownBuild', 'noList', 'identityFile'];
function impNotes(v, full) {
  const notes = Array.isArray(v.notes) ? v.notes.filter((n) => n && typeof n.text === 'string' && n.text) : [];
  const out = notes.map((n) => `<div class="impv-note${IMP_NOTE_WARN.includes(n.code) ? ' warn' : ''}">${esc(n.text)}</div>`);
  if (full && v.windows11Unsupported) out.push(`<div class="impv-note warn">${T('wizard.windows11Note', { id: v.versionId || impVersion() })}</div>`);
  return out.join('');
}
const impMb = (bytes) => Math.max(1, Math.ceil((Math.max(0, Number(bytes)) || 0) / 1048576));
// The check's box. mode 'full' = the Source step (the Windows 11 note, a found folder's warning); 'short' = the summary
// above START, which says why START waits and leads back to the Source step.
function impHtml(mode) {
  const full = mode !== 'short', im = S.imp;
  const line = (cls, text, spin) => `<div class="impv-line${cls ? ' ' + cls : ''}">${spin ? spinnerSmall() : ''}<span>${text}</span></div>`;
  const btn = (act, label, arg) => `<button class="btn ghost" data-act="${act}"${arg == null ? '' : ` data-arg="${esc(arg)}"`}>${label}</button>`;
  const back = full ? '' : btn('wizGoto', T('wizard.verdict.changeFolder'), 2);
  const again = btn('impAgain', T('wizard.verdict.again'));
  const acts = (...list) => { const b = list.filter(Boolean); return b.length ? `<div class="impv-acts">${b.join('')}</div>` : ''; };
  const why = full ? '' : `<p class="impv-why">${T('wizard.verdict.startNeedsReady')}</p>`;
  // Nothing chosen: the Source step says how to choose (Browse..., the field, the search below it); the summary, which has
  // none of them, says where.
  if (!impPath()) return full ? `<p class="impv-empty">${T('wizard.verdict.empty')}</p>`
    : line('bad', T('wizard.verdict.emptyShort')) + acts(btn('wizGoto', T('wizard.verdict.chooseFolder'), 2)) + why;
  const hit = full && im.hit && sameDirText(im.hit.dir, impPath()) ? `<div class="impv-note warn">${esc(im.hit.text)}</div>` : '';
  const cur = impNow();
  // Typed since the last answer, or that answer may be out of date: a check is due — or nothing asks it yet.
  if (!cur) return (impTimer ? line('busy', T('wizard.verdict.waiting'), true) : line('', T('wizard.verdict.notChecked')) + acts(again, back)) + hit + why;
  if (cur.phase === 'quick') return line('busy', T('wizard.verdict.waiting'), true) + hit + why;
  if (cur.phase === 'error') return line('bad', T('wizard.verdict.failed', { error: cur.error })) + hit + acts(again, back) + why;
  if (cur.phase === 'cancelled') return line('', T('wizard.verdict.interrupted')) + hit + acts(again, back) + why;
  const v = cur.verdict || {};
  const notes = impNotes(v, full);
  if (cur.phase === 'deep') {
    const found = v.found ? (v.build ? T('wizard.verdict.foundBuild', { version: v.found, build: v.build }) : T('wizard.verdict.found', { version: v.found })) : '';
    return line('busy', esc(v.message || t('wizard.verdict.waiting')), true) + (found ? `<div class="impv-note">${found}</div>` : '') + hit + notes + why;
  }
  if (v.status === 'ready') {
    const n = Array.isArray(v.writes) ? v.writes.length : 0;
    return line('ok', esc(v.message)) + hit + notes
      + `<div class="impv-note">${n ? T('wizard.verdict.writes', { n, mb: impMb(v.bytesNeeded) }) : T('wizard.verdict.current')}</div>`
      + acts(v.replaceInstalled ? btn('impKeepOther', T('wizard.verdict.keepOther')) : '');
  }
  // Refused: the check's own sentence, and the one way on its reason offers — another version of the catalogue the folder
  // holds, or this folder in the place of the installed one; a folder that contradicts itself, or holds a version Relic
  // does not offer, gets none.
  const gone = v.reason === 'installedElsewhere' && v.installedAtGone ? `<div class="impv-note">${T('wizard.verdict.installedAtGone')}</div>` : '';
  const other = v.reason === 'otherVersion' && v.foundOffered && v.found && versionById(v.found)
    ? btn('impSwitch', T('wizard.verdict.switch', { version: v.found }), v.found) : '';
  const instead = v.reason === 'installedElsewhere' && v.installedAt ? btn('impUseInstead', T('wizard.verdict.useInstead')) : '';
  return line(v.status === 'refused' ? 'bad' : '', esc(v.message || '')) + hit + notes + gone + acts(other, instead, again, back) + why;
}

// "Find it on this PC" (install.findGames / install.findGamesStop; the events findgames.progress / found / done, every one
// naming its search). search = the number Start answered (0 = none yet); starting = Start's answer is outstanding — the
// events that land meanwhile wait in findBuf, and only those of the number it answers count; stopping = Stop was asked;
// progress = the last findgames.progress; hits = the game folders found, in the order they came (a deeper search adds
// the ones it finds anew); done = the search's findgames.done — nothing of it counts after it; error = why no search
// started; othersOpen = the list of the folders that cannot be added is open.
function findBlank() { return { search: 0, starting: false, stopping: false, progress: null, hits: [], done: null, error: '', othersOpen: false }; }
let findBuf = [], findReq = 0, findStopTimer = 0, findOwed = false;
const FIND_BUF_MAX = 4000;
// How long a Stop waits for the search's own end before it puts the search away by itself.
const FIND_STOP_WAIT_MS = 2000;
const findLive = () => S.find.starting || (S.find.search > 0 && !S.find.done);
// A search runs only while its list is in sight: the Source step of the wizard, the local folder chosen, no install.
const findShown = () => S.screen === 'install' && S.installStep === 2 && S.wiz.source === 'local' && !S.installing;
const findNum = (n) => (Number.isFinite(Number(n)) ? Math.max(0, Math.round(Number(n))) : 0);
async function findRun(deeper) {
  const f = S.find;
  if (findLive()) return;
  if (S.installing) return showToast(t('toast.installInProgress'), 'bad');
  if (!deeper) { f.hits = []; f.othersOpen = false; }
  Object.assign(f, { search: 0, starting: true, stopping: false, progress: null, done: null, error: '' });
  findBuf = [];
  clearTimeout(findStopTimer);
  syncFind();
  const req = ++findReq, payload = deeper ? { deeper: true } : {};
  let n = 0, error = '';
  try {
    let r = await rpc('install.findGames', payload);
    // 0 = a search runs already, and not one this page follows: it is stopped, and this one asked for once more — unless
    // a Stop was asked while that stop was out (Stop, the Source step left, the window to the tray, an install): it
    // reached the backend before this second start would, and nothing would end the search that start began.
    if (!(r && Number(r.search) > 0) && !f.stopping) {
      await rpc('install.findGamesStop').catch(() => null);
      r = f.stopping || req !== findReq || S.find !== f ? null : await rpc('install.findGames', payload);
    }
    n = r && Number(r.search) > 0 ? Number(r.search) : 0;
  } catch (e) { error = (e && e.message) || String(e); }
  if (req !== findReq || S.find !== f) return;
  const buf = findBuf; findBuf = [];
  f.starting = false;
  if (!n) {
    const stopped = f.stopping;
    f.stopping = false;
    f.error = error || (stopped ? '' : t('wizard.find.busy'));
    syncFind();
    return;
  }
  f.search = n;
  buf.forEach(([kind, d]) => findApply(kind, d));
  // A Stop asked while this answer was out reached the backend after the start it answers: it ends this very search,
  // whose done comes. Until that done, the search is asked to stop once more now that its number is known, and the wait
  // that puts it away by itself starts again from here (findHalt's wait does nothing while a start is out).
  if (f.stopping && !f.done) { f.stopping = false; findHalt(); return; }
  syncFind();
}
// Stop: the search's end comes as its findgames.done (stopped), which the backend queues before its answer and which may
// land after it. One that does not come — a backend that had none running — leaves the search put away here.
async function findHalt() {
  const f = S.find;
  if (!findLive() || f.stopping) return;
  f.stopping = true;
  syncFind();
  try { await rpc('install.findGamesStop'); } catch (e) { /* the wait below puts it away */ }
  if (S.find !== f) return;
  clearTimeout(findStopTimer);
  findStopTimer = setTimeout(() => {
    if (S.find !== f || f.done || f.starting) return;
    f.stopping = false;
    if (f.search) f.done = { search: f.search, stopped: true, deeper: true, budget: false, found: f.hits.length, dirs: f.progress ? findNum(f.progress.dirs) : 0, error: '' };
    syncFind();
  }, FIND_STOP_WAIT_MS);
}
// Leaving the Source step — another step, another source, another screen, an install — stops a search that runs
// (render() asks after every drawing).
function findLeft() { if (findLive() && !S.find.stopping && !findShown()) setTimeout(findHalt, 0); }
// One event of a search: kept while Start's answer is outstanding; else counted only when it is the search's own and
// comes before its done.
function findEvent(kind, d) {
  if (!d || !(Number(d.search) > 0)) return;
  if (S.find.starting) { if (findBuf.length < FIND_BUF_MAX) findBuf.push([kind, d]); return; }
  if (!findApply(kind, d)) return;
  if (kind === 'progress') syncFindProgress(); else syncFind();
}
function findApply(kind, d) {
  const f = S.find;
  if (Number(d.search) !== f.search || f.done) return false;
  if (kind === 'progress') { f.progress = d; return true; }
  if (kind === 'found') {
    if (typeof d.dir !== 'string' || !d.dir) return false;
    // A folder the list already holds (another way to the same folder) takes the place of its row.
    const at = f.hits.findIndex((h) => sameDirText(h.dir, d.dir));
    if (at >= 0) f.hits[at] = d; else f.hits.push(d);
    return true;
  }
  if (kind === 'done') { f.done = d; f.stopping = false; clearTimeout(findStopTimer); return true; }
  return false;
}
// The search's parts follow in place: its progress up to four times a second, the list and the buttons when they change —
// a whole render() would take the field's caret, and a button swapped under a click would drop the click (the list and
// the buttons wait for the pointer to come up).
function syncFindProgress() {
  if (!findShown()) return;
  const el = document.querySelector('#app [data-live="findprog"]');
  if (el) el.innerHTML = findProgHtml();
}
function syncFind() {
  if (!findShown()) return;
  if (pointerDown) { findOwed = true; return; }
  findOwed = false;
  const put = (key, html) => { const el = document.querySelector(`#app [data-live="${key}"]`); if (el && el.relicHtml !== html) { el.innerHTML = html; el.relicHtml = html; } };
  put('findctl', findCtlHtml());
  put('findhits', findHitsHtml());
  syncFindProgress();
}
function findSect() {
  return `<div class="fsec">
    <div class="fsec-head"><div><b>${T('wizard.find.title')}</b><small>${T('wizard.find.sub')}</small></div>
      <div class="fsec-ctl" data-live="findctl">${findCtlHtml()}</div></div>
    <div data-live="findprog">${findProgHtml()}</div>
    <div data-live="findhits">${findHitsHtml()}</div>
  </div>`;
}
function findCtlHtml() {
  const f = S.find;
  if (findLive()) return `<button class="btn ghost" data-act="findStop" ${f.stopping ? 'disabled' : ''}>${f.stopping ? T('wizard.find.stopping') : T('wizard.find.stop')}</button>`;
  const deeper = f.done && f.done.deeper && !f.done.error ? `<button class="btn ghost" data-act="findDeeper">${T('wizard.find.deeper')}</button>` : '';
  return `${deeper}<button class="btn" data-act="findStart">${f.done || f.error ? T('wizard.find.again') : T('wizard.find.start')}</button>`;
}
function findDiskHtml(d) {
  const st = d && d.state;
  const state = st === 'searching' ? T('wizard.find.disk.searching') : st === 'done' ? T('wizard.find.disk.done')
    : st === 'stopped' ? T('wizard.find.disk.stopped') : T('wizard.find.disk.waiting');
  return `<span class="fdisk ${st === 'searching' || st === 'done' || st === 'stopped' ? st : 'waiting'}"><i></i><b>${esc((d && d.name) || '')}</b>${d && d.hdd ? `<em>${T('wizard.find.hdd')}</em>` : ''}<span>${state}</span><small>${T('wizard.find.diskDirs', { n: findNum(d && d.dirs) })}</small></span>`;
}
function findProgHtml() {
  const f = S.find;
  if (f.starting) return `<div class="fprog">${spinnerSmall()}<span>${T('wizard.find.starting')}</span></div>`;
  if (findLive()) {
    const p = f.progress;
    const disks = p && Array.isArray(p.disks) ? p.disks.filter((d) => d && typeof d === 'object') : [];
    return `<div class="fprog">${spinnerSmall()}<span>${p && p.phase === 'disks' ? T('wizard.find.disks') : T('wizard.find.places')}</span>
      ${p ? `<small>${T('wizard.find.counts', { dirs: findNum(p.dirs), found: f.hits.length })}</small>` : ''}</div>
      ${disks.length ? `<div class="fdisks">${disks.map(findDiskHtml).join('')}</div>` : ''}`;
  }
  if (f.error) return `<div class="fdone bad">${esc(f.error)}</div>`;
  const d = f.done;
  if (!d) return '';
  const vals = { found: f.hits.length, dirs: findNum(d.dirs) };
  const err = typeof d.error === 'string' && d.error ? d.error : '';
  const text = err ? T('wizard.find.failed', { error: err }) : d.stopped ? T('wizard.find.stopped', vals) : d.budget ? T('wizard.find.budget', vals) : T('wizard.find.done', vals);
  return `<div class="fdone${err ? ' bad' : ''}">${text}${d.deeper && !err ? ` ${T('wizard.find.deeperHint')}` : ''}</div>`;
}
// The folders found: the ones that can be added (or are in the Library already) in the order they came, then — folded
// away — the ones that cannot (a version Relic does not offer, a folder that contradicts itself), never with a button.
function findHitsHtml() {
  const f = S.find;
  const rows = f.hits.map((h, i) => ({ h, i }));
  const usable = rows.filter((r) => r.h.status !== 'none'), others = rows.filter((r) => r.h.status === 'none');
  const none = f.done && !f.done.error && !f.hits.length ? `<p class="fnone">${T('wizard.find.none')}</p>` : '';
  const list = usable.length ? `<div class="fhits">${usable.map((r) => findHitHtml(r.h, r.i)).join('')}</div>` : '';
  const more = others.length ? `<div class="fothers"><button class="flink" data-act="findOthers">${f.othersOpen ? T('wizard.find.othersHide') : T('wizard.find.others', { n: others.length })}</button>
    ${f.othersOpen ? `<div class="fhits">${others.map((r) => findHitHtml(r.h, r.i)).join('')}</div>` : ''}</div>` : '';
  return none + list + more;
}
// One folder found: where, what its own files say it is, where it was found and its note; its way into the field —
// as the version its files name, or (a folder that says nothing) as the version chosen at the first step.
function findHitHtml(h, i) {
  const off = h.status === 'none';
  const chosen = !off && sameDirText(h.dir, impPath());
  const ver = h.version ? (h.build ? T('wizard.verdict.foundBuild', { version: h.version, build: h.build }) : T('wizard.verdict.found', { version: h.version }))
    : T('wizard.find.versionUnknown');
  const src = h.source === 'relic' ? T('wizard.find.src.relic') : h.source === 'running' ? T('wizard.find.src.running')
    : h.source === 'traces' ? T('wizard.find.src.traces') : T('wizard.find.src.disk');
  const as = h.version || impVersion();
  const act = h.status === 'registered' ? `<span class="fhit-tag">${T('wizard.find.registered')}</span>`
    : off ? ''
      : chosen ? `<span class="fhit-tag on">${T('wizard.find.chosen')}</span>`
        : `<button class="btn ghost" data-act="findUse" data-arg="${i}" ${as ? '' : 'disabled'}>${T('wizard.find.useAs', { version: as || '—' })}</button>`;
  const warn = h.status === 'pick' || h.proof === 'list' || !!h.installedAt;
  const note = h.status !== 'registered' && typeof h.note === 'string' && h.note ? `<small${warn && !off ? ' class="warn"' : ''}>${esc(h.note)}</small>` : '';
  return `<div class="fhit${chosen ? ' on' : ''}${off ? ' off' : ''}">
    <div class="fhit-main"><b class="mono pick">${esc(h.dir)}</b><small>${ver} · ${src}</small>${note}</div>
    ${act ? `<div class="fhit-act">${act}</div>` : ''}
  </div>`;
}

// ── animation continuity across render() ──
// render() rewrites all of #app, and a CSS animation starts from zero on every NEW element: the ring
// would jump back to 0° on every percent and the waves would start over. The clock is shared
// (performance.now), so a negative animation-delay equal to the current phase makes the fresh element
// continue exactly where the one it replaced left off. It goes AFTER any inline `animation:` — the
// shorthand resets the delay, the longhand after it wins.
const animNow = () => performance.now() / 1000;
const phaseOf = (dur, off) => (-((animNow() + (off || 0)) % dur)).toFixed(3) + 's';
// "Once" animations (the box entering, the note appearing) have the same problem in reverse: a stray
// render() (a toast, the server status poll) would restart them endlessly. Remember the moment of
// the first appearance and, once consumed, emit nothing.
const ONCE = new Map();
function once(key, name, dur, ease) {
  if (!ONCE.has(key)) ONCE.set(key, animNow());
  const e = animNow() - ONCE.get(key);
  return e >= dur ? '' : `animation:${name} ${dur}s ${ease || 'ease'} both;animation-delay:${(-e).toFixed(3)}s;`;
}
const onceForget = (prefix) => { ONCE.forEach((v, k) => { if (k.indexOf(prefix) === 0) ONCE.delete(k); }); };
// The dark backdrop fades in once, when the install overlay appears — NOT on every stage change
// (progress → paused → done), otherwise the screen would flicker under the box.
// The box, by contrast, enters on every stage: it is a new sheet with different content.
const ovIn = () => once('inst:ov', 'rl-fade', .22);
const boxIn = (variant) => once('inst:box:' + variant, 'rl-boxin', .34, 'cubic-bezier(.2,.7,.2,1)');

// ── progress, interpolated ──
// The backend reports in jumps (a package downloaded, a file extracted). `shown` tracks `target`
// exponentially, frame by frame, so the bar, the buoy on the wave and the percent figure move
// smoothly and at the same pace however rarely or irregularly the events arrive. The displayed
// value lives HERE, not in the DOM: a structural render() (the extract note appears, the buttons
// change) redraws the bar straight at `shown`, without jumping back.
const PROG = { shown: 0, target: 0, raf: 0, last: 0 };
function resetProgress() { PROG.shown = 0; PROG.target = 0; PROG.last = 0; }
function paintProgress() {
  const w = (PROG.shown * 100).toFixed(2) + '%';
  document.querySelectorAll('[data-fill]').forEach((el) => { el.style.width = w; });
  document.querySelectorAll('[data-buoy]').forEach((el) => { el.style.left = w; });
  const txt = Math.round(PROG.shown * 100) + '%';
  document.querySelectorAll('[data-pct]').forEach((el) => { if (el.textContent !== txt) el.textContent = txt; });
}
function progTick(ts) {
  PROG.raf = 0;
  if (!document.querySelector('[data-fill]')) { PROG.last = 0; return; } // the overlay is gone
  // The step is computed from real time, not from the frame count: a dropped frame (extraction
  // really loads the machine) does not slow the tracking down.
  const dt = PROG.last ? Math.min(.1, (ts - PROG.last) / 1000) : 1 / 60;
  PROG.last = ts;
  PROG.shown += (PROG.target - PROG.shown) * (1 - Math.pow(.004, dt));
  if (Math.abs(PROG.target - PROG.shown) < 0.0004) PROG.shown = PROG.target;
  paintProgress();
  if (PROG.shown !== PROG.target) PROG.raf = requestAnimationFrame(progTick); else PROG.last = 0;
}
function syncProgress() {
  const t = Math.max(0, Math.min(1, S.installFraction || 0));
  // Interpolation goes FORWARD only; a decrease is applied directly. The fraction the backend reports
  // is per PHASE, not per whole install (client 0→1, then voices 0→1, then extraction 0→1, then the
  // Patch/Fiddler/Shortcut tail) — so at every phase change it starts again from ~0. Interpolated,
  // that drop would slowly empty the bar from the right for ~1.4 s and look exactly like work being
  // undone; jumped, it reads as "new stage".
  if (t < PROG.shown) PROG.shown = t;
  PROG.target = t;
  if (!document.querySelector('[data-fill]')) {
    if (PROG.raf) { cancelAnimationFrame(PROG.raf); PROG.raf = 0; }
    PROG.last = 0; return;
  }
  paintProgress();
  if (!PROG.raf && PROG.shown !== PROG.target) { PROG.last = 0; PROG.raf = requestAnimationFrame(progTick); }
}

// The spinner and the progress bar have a themed shape: classic = the circle + the golden bar,
// archipelago = the spinning lifebuoy + the wave bar (markup chosen here, styles in CSS).
function spinnerHtml() {
  return isSummer()
    ? `<span class="lifebuoy" style="animation-delay:${phaseOf(2.2)}"></span>`
    : `<span style="animation:rl-spin 1s linear infinite;animation-delay:${phaseOf(1)};color:var(--goldD)"><svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12a9 9 0 1 1-6.2-8.5"/></svg></span>`;
}
// Widths are drawn from PROG.shown (not S.installFraction): the fresh markup continues from where the
// old drawing was, and the rAF loop takes over from there.
function progressHtml() {
  const w = (Math.max(0, Math.min(1, PROG.shown)) * 100).toFixed(2) + '%';
  // The sheen is an ELEMENT, not a ::before/::after: a pseudo-element cannot receive phaseOf()'s
  // inline animation-delay, so it would have been the only thing in the overlay still restarting.
  if (!isSummer()) return `<div class="pbar"><i data-fill style="width:${w}"><b class="sheen" style="animation-delay:${phaseOf(5.4)}"></b></i></div>`;
  return `<div class="wavewrap"><span class="buoy" data-buoy style="left:${w};animation-delay:${phaseOf(1.3)}"></span>
    <div class="wavebar"><div class="fill" data-fill style="width:${w}">
      <i class="back" style="animation-delay:${phaseOf(5.6)},${phaseOf(6.9)}"></i>
      <i class="mid" style="animation-delay:${phaseOf(3.4)},${phaseOf(5.1)}"></i>
      <i class="front" style="animation-delay:${phaseOf(2.1)},${phaseOf(4.3)}"></i>
      <i class="sheen" style="animation-delay:${phaseOf(6.2)}"></i>
    </div></div></div>`;
}

// Pause only while the work is resumable by design: the downloads ('' = the pre-download probe).
// Extract can only be cancelled — the downloaded zips stay, but extraction restarts on retry.
// From Fiddler on the version is registered and the backend no longer honors cancellation.
// A local import has nothing to "resume" — Pause would lead to an overlay about downloads; Cancel only.
function installGuards() {
  const canPause = !S.activeInstallLocal
    && (!S.installPhase || S.installPhase === 'DownloadClient' || S.installPhase === 'DownloadAudio');
  return { canPause, canCancel: canPause || S.installPhase === 'Extract' };
}
// Signature of everything that CANNOT be patched in place in the progress overlay (title, buttons,
// the extract note, the theme-dictated shape). While it does not change, a progress event only sets
// the new text and leaves the DOM — and therefore the animations — alone. Changes → full render().
function installLiveKey() {
  const g = installGuards();
  return [S.activeInstallId, S.activeInstallVoices ? 'v' : '-', isSummer() ? 's' : 'c', S.installPhase === 'Extract' ? 'x' : '-',
    g.canPause ? 'p' : '-', g.canCancel ? 'c' : '-', S.installStopIntent || '-'].join('|');
}
function patchInstallLive() {
  if (!S.installing || S.installError || S.installDone || S.installPaused) return false;
  const box = document.querySelector('[data-live="install"]');
  if (!box || box.getAttribute('data-key') !== installLiveKey()) return false;
  const m = box.querySelector('[data-live="msg"]');
  const txt = S.installMsg || '...';
  if (m && m.textContent !== txt) m.textContent = txt;
  return true; // the percent is moved by the tween in syncProgress()
}

function installOverlay() {
  if (S.installError) {
    return `<div class="overlay" style="${ovIn()}"><div class="box" style="text-align:center;${boxIn('error')}">
      <div style="width:60px;height:60px;border-radius:50%;margin:0 auto 14px;background:rgba(208,106,94,.15);display:flex;align-items:center;justify-content:center">${I.warn}</div>
      <div class="serif" style="font-weight:700;font-size:19px;color:#8a3a2a">${S.activeInstallLocal ? T('install.importFailed') : T('install.failed')}</div>
      <p style="color:#7a5040;font-size:12.5px;font-weight:600;margin:8px 0 20px;word-break:break-word">${esc(S.installError)}</p>
      <button class="btn ghost" data-act="finishInstall">${T('common.close')}</button></div></div>`;
  }
  if (S.installDone) {
    // The last install screen is also the only moment someone is certainly looking: the game's login
    // details go here, otherwise the player reaches the client's account screen without knowing what
    // to type and thinks they have to create one. They remain in the Library afterwards.
    const id = S.activeInstallId;
    const check = `<div style="width:66px;height:66px;border-radius:50%;margin:0 auto 16px;background:linear-gradient(180deg,var(--gold2),var(--gold));display:flex;align-items:center;justify-content:center;box-shadow:0 0 30px color-mix(in srgb, var(--gold) 50%, transparent);color:var(--btnInk)">${I.check.replace('15','34')}</div>`;
    // A voice pack added to an installed version: no login details (the player has them already) —
    // the one thing to say is where the language is picked (in the game's settings).
    if (S.activeInstallVoices) {
      return `<div class="overlay" style="${ovIn()}"><div class="box" style="text-align:center;${boxIn('done')}">
        ${check}
        <div class="serif" style="font-weight:700;font-size:20px">${T('install.voices.done.title')}</div>
        <p style="color:var(--inkSoft);font-size:13px;font-weight:600;margin:8px 0 20px">${T('install.voices.done.ready', { langs: S.activeInstallVoices.join(', '), id })}</p>
        <button class="btn" data-act="finishInstall">${T('common.close')}</button></div></div>`;
    }
    const acc = accountOf(id);
    // No ready-made login on this server for the version: where an account comes from, in place of the
    // details — and the way to the card that creates one, beside the way to the Library.
    const none = !acc && noLoginHere(id);
    return `<div class="overlay" style="${ovIn()}"><div class="box" style="text-align:center;${boxIn('done')}">
      ${check}
      <div class="serif" style="font-weight:700;font-size:20px">${T('install.done.title')}</div>
      <p style="color:var(--inkSoft);font-size:13px;font-weight:600;margin:8px 0 ${acc || none ? '18px' : '20px'}">${T('install.done.ready', { id })}${
        acc ? ' ' + T('install.done.oneMore') : ''}</p>
      ${acc ? `<div style="text-align:left">
        <div class="credhead">${T('install.done.credsHead')}</div>
        ${credsBox(id)}
        <p style="color:var(--inkSoft);font-size:12px;font-weight:600;line-height:1.6;margin:12px 0 18px">
          ${tf('install.done.credsNote')}</p>
      </div>` : none ? `<div style="text-align:left">
        <div class="credhead">${T('install.done.credsHead')}</div>
        <div class="note" style="margin:0 0 18px">${T(loginWay(id).long, { id })}</div>
      </div>` : ''}
      <div class="flex center gap" style="justify-content:center">${none && loginWay(id).create ? `<button class="btn ghost" data-act="finishInstallLogin" data-arg="${esc(id)}">${T('lib.login.create')}</button>` : ''}
        <button class="btn" data-act="finishInstall">${T('install.done.openLibrary')}</button></div></div></div>`;
  }
  if (S.installPaused) {
    return `<div class="overlay" style="${ovIn()}"><div class="box" style="text-align:center;${boxIn('paused')}">
      <div style="width:60px;height:60px;border-radius:50%;margin:0 auto 14px;background:color-mix(in srgb, var(--gold) 16%, transparent);display:flex;align-items:center;justify-content:center;color:var(--goldD)"><svg width="24" height="24" viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="4" width="4" height="16" rx="1.4"/><rect x="14" y="4" width="4" height="16" rx="1.4"/></svg></div>
      <div class="serif" style="font-weight:700;font-size:19px">${T('install.paused.title')}</div>
      <p style="color:var(--inkSoft);font-size:12.5px;font-weight:600;margin:8px 0 18px">${T('install.paused.note')}</p>
      ${progressHtml()}
      <div data-pct style="text-align:right;color:var(--goldD);font-weight:800;font-size:12px;margin:6px 0 20px">${Math.round(PROG.shown * 100)}%</div>
      <div class="flex center gap" style="justify-content:center">
        <button class="btn" data-act="resumeInstall">${T('install.paused.resume')}</button>
        <button class="btn ghost" data-act="abandonInstall">${T('common.giveUp')}</button>
      </div></div></div>`;
  }
  const { canPause, canCancel } = installGuards();
  const stopping = !!S.installStopIntent;
  const controls = canCancel ? `<div class="flex center gap" style="justify-content:flex-end;margin-top:18px">
      ${canPause ? `<button class="btn ghost" data-act="pauseInstall" ${stopping ? 'disabled' : ''}>${S.installStopIntent === 'pause' ? T('install.pausing') : T('install.pause')}</button>` : ''}
      <button class="btn ghost" data-act="cancelInstall" ${stopping ? 'disabled' : ''}>${S.installStopIntent === 'cancel' ? T('install.cancelling') : T('common.cancel')}</button>
    </div>` : '';
  // min-height on the message row: the texts have 1 or 2 lines, and without it the (centred) box
  // would change height at every step and everything below it would jump by a few pixels.
  const title = S.activeInstallVoices
    ? T('install.voices.installing', { id: S.activeInstallId, langs: S.activeInstallVoices.join(', ') })
    : T('install.installing', { id: S.activeInstallId });
  return `<div class="overlay" style="${ovIn()}" data-live="install" data-key="${esc(installLiveKey())}"><div class="box" style="${boxIn('run')}">
    <div class="flex center gap" style="margin-bottom:6px">${spinnerHtml()}<div class="serif" style="font-weight:700;font-size:18px">${title}</div></div>
    <div data-live="msg" style="color:var(--ink2);font-size:13px;font-weight:700;margin:2px 0 16px;min-height:38px;line-height:1.45">${esc(S.installMsg || '...')}</div>
    ${progressHtml()}
    <div data-pct style="text-align:right;color:var(--goldD);font-weight:800;font-size:12.5px;margin-top:6px">${Math.round(PROG.shown * 100)}%</div>
    ${S.installPhase === 'Extract' ? `<div style="margin-top:14px;padding:10px 12px;border-radius:10px;background:color-mix(in srgb, var(--gold) 10%, transparent);color:var(--inkSoft);font-size:11.5px;font-weight:600;line-height:1.5;${once('inst:note', 'rl-fadein', .32)}">
      ${T('install.extractNote')}</div>` : ''}
    ${controls}
  </div></div>`;
}

// ── commands ──
// name/desc are translation keys (translated when the card is drawn).
const CATS = [
  { id: 'exp', name: 'cmd.cat.exp.name', desc: 'cmd.cat.exp.desc', icon: I.star, color: '#c98b1e', bg: 'rgba(232,176,74,.14)', ph: '10000' },
  { id: 'character', name: 'cmd.cat.character.name', desc: 'cmd.cat.character.desc', icon: I.user, color: '#2b8fc4', bg: 'rgba(76,178,230,.14)', ph: '10000002' },
  { id: 'artifact', name: 'cmd.cat.artifact.name', desc: 'cmd.cat.artifact.desc', icon: CMDICON.artifact, color: '#8a5fc4', bg: 'rgba(180,142,224,.16)', ph: '71544 20' },
  { id: 'weapon', name: 'cmd.cat.weapon.name', desc: 'cmd.cat.weapon.desc', icon: CMDICON.weapon, color: '#c9462e', bg: 'rgba(228,102,78,.14)', ph: '13501' },
  { id: 'level', name: 'cmd.cat.level.name', desc: 'cmd.cat.level.desc', icon: I.up, color: '#5f8a24', bg: 'rgba(143,191,80,.16)', ph: '60' },
  { id: 'mora', name: 'cmd.cat.mora.name', desc: 'cmd.cat.mora.desc', icon: I.coin, color: '#9b7d3a', bg: 'rgba(200,160,79,.16)', ph: '100000' },
  { id: 'primogems', name: 'cmd.cat.primogems.name', desc: 'cmd.cat.primogems.desc', icon: I.diamond, color: '#5a7fc4', bg: 'rgba(90,127,196,.14)', ph: '10000' },
  { id: 'genesis', name: 'cmd.cat.genesis.name', desc: 'cmd.cat.genesis.desc', icon: I.atom, color: '#8a5fc4', bg: 'rgba(180,142,224,.14)', ph: '5000' },
  // Original Resin (item add 106 <n>): the server refuses an amount that would put the player above
  // the 2000 it lets anyone hold.
  { id: 'resin', name: 'cmd.cat.resin.name', desc: 'cmd.cat.resin.desc', icon: I.moon, color: '#2f8fa6', bg: 'rgba(63,178,204,.16)', ph: '160' },
];
// The plain-number cards (exp/level/mora/primogems/genesis/resin) stay simple; the other three are the
// raw-id inputs, used only by the manual section and by the fallback when the catalogue is missing.
const NUM_CATS = ['exp', 'level', 'mora', 'primogems', 'genesis', 'resin'];

const ID_CATS = ['character', 'weapon', 'artifact'];

function cmdCards(ids) {
  return ids.map((id) => CATS.find((c) => c.id === id)).filter(Boolean).map((c) => `<div class="cmdcard">
    <div class="top"><span class="ic" style="background:${c.bg};color:${c.color}">${c.icon}</span><div><b>${T(c.name)}</b><br><small>${T(c.desc)}</small></div></div>
    <div class="in"><input data-model="cmd.${c.id}" value="${esc(S.cmd[c.id])}" placeholder="${c.ph}"/><button class="send" data-act="sendCmd" data-arg="${c.id}">${T('common.send')}</button></div>
  </div>`).join('');
}

function commands() {
  const body = S.gd.status === 'ok'
    ? avatarHero() + `<div class="grid2" style="margin-top:16px">${weaponSlot()}${artifactSlot()}</div>`
    : catalogFallback();
  return `<div class="pad">
    ${cmdHeader()}
    ${cmdHelp()}
    ${body}
    ${ingameCard()}
    ${costumeCard()}
    <div class="cmdgrid" style="margin-top:16px">${cmdCards(NUM_CATS)}</div>
    ${rawSection()}
    ${cmdConsole()}
  </div>`;
}

// ── commands: player instructions ──
// Collapsed by default but always in sight: the rules that are otherwise learned only from failed
// attempts (UID + online required, where the items land, what equipped mode means).
function cmdHelp() {
  const head = `<button class="btn ghost" style="padding:9px 14px;font-size:12px" data-act="toggleHelp">
      ${S.helpOpen ? '▾' : '▸'} ${T('cmd.help.toggle')}</button>`;
  if (!S.helpOpen) return `<div style="margin-bottom:16px">${head}</div>`;
  const li = (html) => `<li style="margin:0 0 9px;line-height:1.55">${html}</li>`;
  return `<div style="margin-bottom:16px">${head}
    <div class="sect" style="margin-top:12px;margin-bottom:0"><h4>${T('cmd.help.title')}</h4>
      <ul style="margin:0;padding-left:20px;color:var(--inkSoft);font-size:12.5px;font-weight:600">
        ${li(tf('cmd.help.online'))}
        ${li(tf('cmd.help.uid'))}
        ${li(tf('cmd.help.characters'))}
        ${li(tf('cmd.help.weapons'))}
        ${li(tf('cmd.help.sets'))}
        ${li(tf('cmd.help.ingame'))}
        ${li(tf('cmd.help.c6'))}
        ${li(tf('cmd.help.setCount'))}
      </ul>
    </div></div>`;
}

// ── commands: the character controlled IN GAME (not the one chosen in the catalogue above) ──
// Commands verified in the gameserver binary: "break <n>" (setBreakLevel) and "level <n>"
// (setLevel, registered right next to it) work on the current character, "stamina infinite on/off"
// is literal in the binary, and invincibility is setWudi — "wudi global avatar on/off" from the GIO
// guide. Each ascension level has its own cap, so break + level are sent together.
// "talent unlock all" (procTalent / forceUnlockAllTalent in the binary) unlocks
// all constellations — C6. The server calls the constellation "talent"; unrelated to the talents
// (abilities) of the game UI, and a per-chosen-character form does not exist: the command works on
// the controlled character too, like break/level.
const PROMOTE_LEVEL_CAP = [20, 40, 50, 60, 70, 80, 90];
function ingameCard() {
  const promote = clampInt(S.ingame.promote, 0, 6, 6);
  const dis = S.sending ? 'disabled' : '';
  const onoff = (label, act) => `<div>
      <label class="fld" style="margin:0 0 5px">${label}</label>
      <div class="flex" style="gap:8px">
        <button class="btn" style="padding:10px 16px;font-size:12.5px" data-act="${act}" data-arg="on" ${dis}>${T('cmd.ingame.on')}</button>
        <button class="btn ghost" style="padding:10px 16px;font-size:12.5px" data-act="${act}" data-arg="off" ${dis}>${T('cmd.ingame.off')}</button>
      </div></div>`;
  return `<div class="sect" style="margin-top:16px;margin-bottom:0">
    <div class="flex between center wrap" style="gap:10px;margin-bottom:4px">
      <h4 style="margin:0">${T('cmd.ingame.title')}</h4>
      <span class="vchip">${T('cmd.ingame.chip')}</span>
    </div>
    <p class="hint" style="margin:8px 0 14px">${T('cmd.ingame.hint')}</p>
    <div class="flex gap wrap center">
      <div><label class="fld" style="margin:0 0 5px">${T('cmd.ingame.constellations')}</label>
        <button class="btn" style="padding:10px 16px;font-size:12.5px" data-act="sendC6" ${dis}
          title="talent unlock all">${T('cmd.ingame.unlockC6')}</button></div>
      <div><label class="fld" style="margin:0 0 5px">${T('cmd.ingame.ascension', { level: PROMOTE_LEVEL_CAP[promote] })}</label>
        <div class="flex" style="gap:8px"><input class="num" style="width:76px;padding:9px 11px;border-radius:9px;border:1px solid color-mix(in srgb, var(--goldD) 40%, transparent);background:#fff;color:var(--ink);font-size:13px;font-weight:700" data-model="ingame.promote" value="${esc(S.ingame.promote)}" placeholder="6"/>
        <button class="btn" style="padding:10px 16px;font-size:12.5px" data-act="sendBreak" ${dis}>${T('cmd.ingame.applyAscension')}</button></div></div>
      <div style="flex:1"></div>
      ${onoff(T('cmd.ingame.stamina'), 'sendStamina')}
      ${onoff(T('cmd.ingame.hp'), 'sendWudi')}
    </div>
    <div class="note" style="margin-top:12px">${T('cmd.ingame.fallbackPre')} <button class="btn ghost" style="padding:5px 10px;font-size:11px" data-act="sendHeroWit" ${dis}>${T('cmd.ingame.heroWit')}</button> ${T('cmd.ingame.fallbackPost')}</div>
  </div>`;
}

// ── commands: skins (costumes) ──
// Skins are ITEMS (item add <id> 1 → the item module calls addCostume — see GmCommands). One button per
// skin; which ones exist in the selected version is the backend's answer (gamedata.forVersion, the
// rule that filters the pickers), so nothing here compares versions. The card is drawn only from an
// answered list: none while the answer is on its way, none after a failed one.
function costumeCard() {
  const v = S.gd.version || cmdVersionId() || '';
  const list = S.gd.costumes || [];
  if (!list.length) return '';
  const dis = S.sending ? 'disabled' : '';
  const btns = list.map((c) => `<button class="btn ghost" style="padding:9px 13px;font-size:12px"
      data-act="sendCostume" data-arg="${c.id}" ${dis} title="item add ${c.id} 1"><b>${esc(c.char)}</b>&nbsp;· ${esc(c.name)}</button>`).join('');
  return `<div class="sect" style="margin-top:16px;margin-bottom:0">
    <div class="flex between center wrap" style="gap:10px;margin-bottom:4px">
      <h4 style="margin:0">${T('cmd.costume.title')}</h4>
      <span class="vchip">${T('cmd.costume.chip', { version: v, n: list.length })}</span>
    </div>
    <p class="hint" style="margin:8px 0 12px">${T('cmd.costume.hint')}</p>
    <div class="flex gap wrap">${btns}</div>
  </div>`;
}

function cmdHeader() {
  const v = S.gd.version || cmdVersionId() || '—';
  return `<div class="flex between center wrap" style="gap:14px;margin-bottom:20px">
    <div><h1 class="title">${T('cmd.title')}</h1>
      <div class="flex center gap wrap" style="margin-top:7px">
        <p class="sub" style="margin:0">${T('cmd.sub')}</p>
        <button class="vchip" data-act="go" data-arg="library" title="${T('cmd.changeVersionTitle')}">${T('cmd.onlyContentFrom', { version: v })}</button>
      </div></div>
    <div class="flex center gap"><label class="fld" style="margin:0">${T('cmd.uid')}</label><input class="txt" style="width:130px" data-model="cmdUid" value="${esc(S.cmdUid)}" placeholder="${T('cmd.uidPlaceholder')}"/></div>
  </div>`;
}

function cmdConsole() {
  const logs = S.log.map((l) => `<div style="color:${l.t === 'ok' ? '#7fc083' : l.t === 'cmd' ? '#e6cf93' : l.t === 'bad' ? '#e08a7f' : '#8aa0c8'}">${esc(l.m)}</div>`).join('');
  return `<div class="console"><div class="bar"><span class="lights"><i style="background:#e4664e"></i><i style="background:#e8b04a"></i><i style="background:#67b86a"></i></span><span>${T('cmd.console.label')} · ${esc(S.settings.serverHost)}</span></div><div class="log" id="cmdlog">${logs}</div></div>`;
}

// Everything that is not a loaded catalogue lands here: the screen keeps its base capability
// (three raw-id cards), it never blocks and it never shows an error page.
function catalogFallback() {
  const v = S.gd.version || cmdVersionId() || '—';
  const note = S.gd.status === 'loading' || S.gd.status === 'idle'
    ? T('cmd.catalog.loading', { version: v })
    : `${T('cmd.catalog.unavailable', { version: v })}${S.gd.error ? ' (' + esc(S.gd.error) + ')' : ''}`;
  return `<div class="note">${note}</div><div class="cmdgrid" style="margin-top:16px">${cmdCards(ID_CATS)}</div>`;
}

// ── commands: character ──
function avatarHero() {
  const a = curAvatar();
  if (!a) {
    return `<div class="gmhero"><div class="bg" ${heroStyle(S.gd.version)}></div>
      <div class="txt"><div class="kicker">${T('cmd.avatar.kicker')}</div><h2>${T('cmd.avatar.none')}</h2>
        <p>${T('cmd.avatar.pickHint', { version: S.gd.version, n: S.gd.avatars.length })}</p>
        <div class="flex gap"><button class="btn" data-act="openPicker" data-arg="avatar">${T('cmd.avatar.pick')}</button></div></div></div>`;
  }
  const c = elColor(a.element);
  const w = wtype(a.weapon);
  return `<div class="gmhero" data-artwrap>
    <div class="bg" style="background-image:url('${esc(splashImg(a.id))}')"></div>
    <div class="glow" style="background:radial-gradient(420px 300px at 78% 55%,${c}55,transparent 70%)"></div>
    <span class="ph" style="background:linear-gradient(180deg,${c}66,${c}18)">${esc(initial(a.name))}</span>
    <img class="art" src="${esc(splashImg(a.id))}" alt="" loading="lazy" decoding="async"/>
    <div class="txt">
      <div class="kicker">${T('cmd.avatar.selectedKicker')}</div>
      <h2>${esc(a.name)}</h2>
      <div class="flex center gap wrap">
        ${a.element ? `<span class="badge"><span class="dot" style="background:${c}"></span>${esc(a.element)}</span>` : ''}
        ${w ? `<span class="badge">${T(w.one)}</span>` : ''}
        <span class="badge">${stars(a.rarity)}</span>
        <span class="badge mono">${T('cmd.idBadge', { id: a.id })}</span>
        ${a.since && a.since === S.gd.version ? `<span class="badge" style="color:var(--gold2)">${T('cmd.avatar.newIn', { version: a.since })}</span>` : ''}
      </div>
      <div class="flex gap" style="margin-top:4px">
        <button class="btn" data-act="sendAvatar" ${S.sending ? 'disabled' : ''}>${T('cmd.avatar.add')}</button>
        <button class="btn dark" data-act="openPicker" data-arg="avatar">${T('cmd.avatar.change')}</button>
      </div>
    </div>
  </div>`;
}

// ── commands: weapon ──
function weaponSlot() {
  const w = curWeapon();
  const wt = w && wtype(w.type);
  return `<div class="gmslot">
    <div class="top"><span class="ic" style="background:rgba(228,102,78,.14);color:#c9462e">${CMDICON.weapon}</span>
      <div><b>${T('cmd.weapon.title')}</b><br><small>${T('cmd.inVersionCount', { n: S.gd.weapons.length, version: S.gd.version })}</small></div></div>
    ${w ? `<div class="pickedrow">${iconTile(w.icon, w.name, 'big')}<div>
             <div class="name">${esc(w.name)}</div>
             <div class="idm">${wt ? T(wt.one) + ' · ' : ''}${T('cmd.idBadge', { id: w.id })} · ${stars(w.rarity)}</div></div></div>`
        : `<div class="name empty">${T('cmd.weapon.none')}</div><div class="idm">${T('cmd.weapon.pickOne')}</div>`}
    <div class="params">
      <div><label class="fld">${T('cmd.weapon.level')}</label><input class="num" data-model="sel.weaponLevel" value="${esc(S.sel.weaponLevel)}" placeholder="90"/></div>
      <div><label class="fld">${T('cmd.weapon.ascension')}</label><input class="num" data-model="sel.weaponPromote" value="${esc(S.sel.weaponPromote)}" placeholder="6"/></div>
    </div>
    <div class="fill"></div>
    <div class="foot"><button class="btn ghost" data-act="openPicker" data-arg="weapon">${T('cmd.weapon.pick')}</button>
      <button class="btn" data-act="sendWeapon" ${w && !S.sending ? '' : 'disabled'}>${T('cmd.weapon.send')}</button></div>
  </div>`;
}

// ── commands: artifact set ──
// Real max level of a piece: +20 at 5★, +16 at 4★ — the clamp at send time respects it.
const setMaxLevel = (st) => (st && st.rarity === 4 ? 16 : 20);
function artifactSlot() {
  const st = curSet();
  const pcs = st ? st.ids.map((id, i) => `${slotName(i)} ${id}`).join(' · ') : '';
  const inv = S.sel.artifactMode !== 'equip';
  const mode = (id, on, label) =>
    `<button class="elchip${on ? ' on' : ''}" data-act="setArtifactMode" data-arg="${id}"${on ? ' style="background:var(--goldD);border-color:var(--goldD)"' : ''}>${label}</button>`;
  return `<div class="gmslot">
    <div class="top"><span class="ic" style="background:rgba(180,142,224,.16);color:#8a5fc4">${CMDICON.artifact}</span>
      <div><b>${T('cmd.artifact.title')}</b><br><small>${T('cmd.inVersionCount', { n: S.gd.sets.length, version: S.gd.version })}</small></div></div>
    ${st ? `<div class="name">${esc(st.name)} ${stars(st.rarity)}</div>
            <div class="sprrow">${st.ids.map((id, i) => iconTile(st.icons[i], slotName(i), '')).join('')}</div>
            <div class="idm">${esc(pcs)}</div>`
         : `<div class="name empty">${T('cmd.artifact.none')}</div><div class="idm">${T('cmd.artifact.pickOne')}</div>`}
    <div class="chips" style="justify-content:flex-start;margin-top:10px">
      ${mode('equip', !inv, T('cmd.artifact.modeEquip'))}${mode('inv', inv, T('cmd.artifact.modeInv'))}
    </div>
    ${inv ? '' : `<div class="params">
      <div><label class="fld">${T('cmd.artifact.pieceLevel', { max: setMaxLevel(st) })}</label><input class="num" data-model="sel.artifactLevel" value="${esc(S.sel.artifactLevel)}" placeholder="${setMaxLevel(st)}"/></div>
    </div>`}
    <div class="note">${inv
      ? T('cmd.artifact.noteInv', { n: st ? st.ids.length : 5 })
      : T('cmd.artifact.noteEquip', { n: st ? st.ids.length : 5 })}</div>
    <div class="fill"></div>
    <div class="foot"><button class="btn ghost" data-act="openPicker" data-arg="artifact">${T('cmd.artifact.pick')}</button>
      <button class="btn" data-act="sendSet" ${st && !S.sending ? '' : 'disabled'}>${T('cmd.artifact.send')}${st ? ` · ${T('cmd.artifact.nCommands', { n: st.ids.length })}` : ''}</button></div>
  </div>`;
}

// Kept for power users and as the automatic escape hatch when the catalogue is missing: raw ids and
// a raw GM command are the only way to reach content the catalogue does not list.
function rawSection() {
  const head = `<button class="btn ghost" style="padding:9px 14px;font-size:12px" data-act="toggleRaw">
      ${S.rawOpen ? '▾' : '▸'} ${T('cmd.raw.toggle')}</button>`;
  if (!S.rawOpen) return `<div style="margin:18px 0">${head}</div>`;
  return `<div style="margin:18px 0">${head}
    <div class="sect" style="margin-top:14px"><h4>${T('cmd.raw.title')}</h4>
      <p class="hint">${T('cmd.raw.hint1')}
        ${T('cmd.raw.hint2')} <span class="mono">equip add</span>${T('cmd.raw.hint3')} <span class="mono">${T('cmd.raw.itemAddExample')}</span>.</p>
      <div class="cmdgrid">${cmdCards(ID_CATS)}</div>
      <label class="fld">${T('cmd.raw.label')}</label>
      <div class="flex gap"><input class="txt mono" data-model="rawCmd" value="${esc(S.rawCmd)}" placeholder="${T('cmd.raw.placeholder')}"/>
        <button class="btn" data-act="sendRaw" ${S.sending ? 'disabled' : ''}>${T('common.send')}</button></div>
    </div>
  </div>`;
}

// ── commands: the modal picker ──
// One modal for every list an id is chosen from (S.picker.kind): the Game commands page's avatar /
// weapon / artifact over the Library version's catalogue (S.gd), and the gameplay settings' own kinds
// (TW_PICK) over what a server version holds.
// A picker the pointer has just opened takes no click for CONFIRM_ARM_MS, like the confirm dialogs: the
// second click of a double-click on what opened it would land on whatever the picker drew under the
// pointer — a row, picked before it was read, or the backdrop, which closes it. S.picker.at = when the
// pointer opened it (the click listener notes it); a picker opened without the pointer — the keyboard's
// Enter on the focused button, a click that carries no click count — has no second click to wait out.
const pickerArmed = () => !!(S.picker && S.picker.at && Date.now() - S.picker.at < CONFIRM_ARM_MS);
function pickerRows() {
  const p = S.picker, tw = twPicker(p);
  if (tw) return tw.rows(p);
  const rar = (list) => (p.r ? list.filter((x) => x.rarity === p.r) : list);
  if (p.kind === 'avatar') return p.el ? S.gd.avatars.filter((a) => String(a.element).toLowerCase() === p.el) : S.gd.avatars;
  if (p.kind === 'weapon') return rar(p.type ? S.gd.weapons.filter((w) => String(w.type).toLowerCase() === p.type) : S.gd.weapons);
  return rar(S.gd.sets);
}

// What the picker's body holds for its state — the list and the line of a list that shows nothing —
// and how many rows match the search text.
function pickerBody() {
  const p = S.picker, tw = twPicker(p);
  const version = tw ? p.v : S.gd.version;
  const rows = pickerRows();
  const q = p.q.trim().toLowerCase();
  // The query hides rows with a class instead of dropping them, so deleting a character can bring
  // them back without a render — the keystroke filter never rebuilds the list. (The rows of a live
  // kind all match: its list is the one that is rebuilt, see filterPicker.)
  const hidden = (r) => (q && !r.s.includes(q) ? ' hide' : '');
  const shown = rows.filter((r) => !q || r.s.includes(q)).length;
  const list = tw ? tw.list(rows, hidden, p)
    : p.kind === 'avatar' ? avatarGrid(rows, hidden)
      : p.kind === 'weapon' ? weaponList(rows, hidden)
        : artifactList(rows, hidden);
  const empty = rows.length === 0 && !(tw && tw.live && q)
    ? `<div class="pickempty">${tw && tw.empty ? tw.empty(p) : T('cmd.picker.emptyCatalog', { version })}</div>`
    : `<div class="pickempty${shown ? ' hide' : ''}">${T('cmd.picker.noResultsPre')}<b data-emptyq>${esc(p.q)}</b>${T('cmd.picker.noResultsPost')}</div>`;
  return { html: `${tw ? tw.note(p) : ''}${list}${empty}`, shown };
}

function pickerOverlay() {
  const p = S.picker, tw = twPicker(p);
  const title = tw ? tw.title(p) : { avatar: T('cmd.avatar.pick'), weapon: T('cmd.weapon.pick'), artifact: T('cmd.picker.titleArtifact') }[p.kind];
  const version = tw ? p.v : S.gd.version;
  const body = pickerBody();
  const head = `<div class="flex center gap"><div class="serif" style="font-weight:700;font-size:18px">${title}</div>
        <span class="vchip">${T('cmd.picker.onlyFrom', { version })} · <b data-count>${esc(twAmount(body.shown))}</b> ${T('cmd.picker.results')}</span></div>`;
  return `<div class="overlay" data-act="closePicker"><div class="pickbox" data-act="noop">
    <div class="pickhead">
      ${tw ? `<div class="pickttl">${head}<div class="pickwhere">${tw.where(p)}</div></div>` : head}
      <button class="pclose" data-act="closePicker" title="${T('common.close')}"><svg width="13" height="13" viewBox="0 0 12 12"><line x1="2.5" y1="2.5" x2="9.5" y2="9.5" stroke="currentColor" stroke-width="1.5"/><line x1="9.5" y1="2.5" x2="2.5" y2="9.5" stroke="currentColor" stroke-width="1.5"/></svg></button>
    </div>
    <div class="pickbar"><input class="txt" data-model="picker.q" data-filter value="${esc(p.q)}" placeholder="${tw ? tw.placeholder(p) : T('cmd.picker.searchPlaceholder')}"/>${pickerChips()}</div>
    <div class="pickbody">${body.html}</div>
    <div class="pickfoot"><p class="hint">${tw ? tw.hint(p) : T('cmd.picker.searchHint')}</p>
      <button class="btn ghost" style="padding:9px 16px;font-size:12.5px" data-act="closePicker">${T('common.close')}</button></div>
  </div></div>`;
}

function pickerChips() {
  const p = S.picker, tw = twPicker(p);
  if (tw) return tw.chips(p);
  const chip = (act, val, on, label, color) =>
    `<button class="elchip${on ? ' on' : ''}" data-act="${act}" data-arg="${esc(val)}"${on && color ? ` style="background:${color};border-color:${color}"` : ''}>${esc(label)}</button>`;
  const rarChips = `<div class="chips">${chip('setPickerRar', '', !p.r, t('common.all'), '')}
      ${chip('setPickerRar', '5', p.r === 5, '5★', 'var(--goldD)')}${chip('setPickerRar', '4', p.r === 4, '4★', 'var(--goldD)')}</div>`;
  if (p.kind === 'avatar') {
    return `<div class="chips">${chip('setPickerEl', '', !p.el, t('common.all'), '')}
      ${ELEMENTS.map((e) => chip('setPickerEl', e.toLowerCase(), p.el === e.toLowerCase(), e, elColor(e))).join('')}</div>`;
  }
  if (p.kind === 'weapon') {
    return `<div class="chips">${chip('setPickerType', '', !p.type, t('common.all'), '')}
      ${WTYPES.map((w) => chip('setPickerType', w.key, p.type === w.key, t(w.many), 'var(--goldD)')).join('')}</div>${rarChips}`;
  }
  return rarChips;
}

function avatarGrid(rows, hidden) {
  const tiles = rows.map((a) => {
    const c = elColor(a.element);
    return `<button class="ptile r${a.rarity}${S.sel.avatarId === a.id ? ' sel' : ''}${hidden(a)}" data-artwrap
      data-act="pickAvatar" data-arg="${a.id}" data-s="${esc(a.s)}" title="${esc(a.name)}">
      <span class="ph" style="background:linear-gradient(180deg,${c}66,${c}18)">${esc(initial(a.name))}</span>
      <img src="${esc(splashImg(a.id))}" alt="" loading="lazy" decoding="async"/>
      <span class="el" style="background:${c}"></span>
      ${a.since && a.since === S.gd.version ? `<span class="new">${T('cmd.picker.newBadge')}</span>` : ''}
      <span class="nm">${esc(a.name)}</span>
    </button>`;
  }).join('');
  return `<div class="ptilegrid">${tiles}</div>`;
}

function weaponList(rows, hidden) {
  const groups = WTYPES.map((wt) => [t(wt.many), rows.filter((w) => String(w.type).toLowerCase() === wt.key)]);
  const known = WTYPES.map((wt) => wt.key);
  groups.push([t('cmd.picker.others'), rows.filter((w) => known.indexOf(String(w.type).toLowerCase()) < 0)]);
  return groups.filter((g) => g[1].length).map(([label, ws]) => {
    const items = ws.map((w) => `<button class="wrow${S.sel.weaponId === w.id ? ' sel' : ''}${hidden(w)}"
      data-act="pickWeapon" data-arg="${w.id}" data-s="${esc(w.s)}">
      ${iconTile(w.icon, w.name, '')}<b>${esc(w.name)}</b>
      <span class="meta"><span class="wid">${T('cmd.idBadge', { id: w.id })}</span>${stars(w.rarity)}</span></button>`).join('');
    // A group whose every row is filtered out would leave a dangling heading; filterPicker applies
    // the same rule live, so the two paths agree.
    const dead = ws.every((w) => hidden(w)) ? ' hide' : '';
    return `<div class="wgroup${dead}"><h5>${esc(label)}</h5><div class="wgrid">${items}</div></div>`;
  }).join('');
}

function artifactList(rows, hidden) {
  const cards = rows.map((s) => `<button class="aset${S.sel.setName === s.name ? ' sel' : ''}${hidden(s)}"
    data-act="pickSet" data-arg="${esc(s.name)}" data-s="${esc(s.s)}">
    <div class="sprrow">${s.ids.map((id, i) => iconTile(s.icons[i], slotName(i), '')).join('')}</div>
    <div class="sn">${esc(s.name)} ${stars(s.rarity)}</div>
    <div class="pcs">${s.ids.map((id, i) => esc(`${slotName(i)} ${id}`)).join(' · ')}</div></button>`).join('');
  return `<div class="asetgrid">${cards}</div>`;
}

// Keystroke filtering: toggle .hide on the already rendered rows. A render() per keystroke would
// destroy the caret and rebuild every tile, so this path must never call render(). A live kind of the
// gameplay settings (TW_PICK) draws only some of its rows: its body is rebuilt for the text instead — the
// search field is outside it and keeps the caret.
function filterPicker(q) {
  const box = document.querySelector('.pickbody');
  if (!box) return;
  const tw = twPicker(S.picker);
  if (tw && tw.live) {
    const body = pickerBody(), n = document.querySelector('.pickhead [data-count]');
    box.innerHTML = body.html; box.scrollTop = 0;
    if (n) n.textContent = twAmount(body.shown);
    return;
  }
  const needle = String(q).trim().toLowerCase();
  let shown = 0;
  box.querySelectorAll('[data-s]').forEach((el) => {
    const hit = !needle || el.getAttribute('data-s').indexOf(needle) >= 0;
    el.classList.toggle('hide', !hit);
    if (hit) shown++;
  });
  box.querySelectorAll('.wgroup').forEach((g) => g.classList.toggle('hide', !g.querySelector('[data-s]:not(.hide)')));
  const empty = box.querySelector('.pickempty');
  if (empty && empty.querySelector('[data-emptyq]')) {
    empty.classList.toggle('hide', shown > 0);
    empty.querySelector('[data-emptyq]').textContent = q;
  }
  const count = document.querySelector('.pickhead [data-count]');
  if (count) count.textContent = shown;
}
// A chip of a live picker gave it another list: that one starts at its top (render() carries the
// scroll offset of every pane across).
function pickerTop() {
  const body = document.querySelector('#app .pickbody');
  if (body) body.scrollTop = 0;
}

// ── server ──
// The agent reports more than "running / not running": a stack can be MISSING from the box or exist
// without ever having been installed. A Start of an uninstalled one can only fail,
// so those states have to be drawn separately, not folded into OFFLINE. Two more are not LIVE either:
// a stack whose services all run while the game server still loads its data (nobody gets in yet), and
// a version the agent's answer does not list at all (it knows other versions, not this one).
// Label tables hold translation keys (translated when drawn).
const SRVMETA = {
  live: ['server.state.live', 'var(--ok)'],
  loading: ['server.state.loading', 'var(--warn)'],
  down: ['server.state.down', 'var(--bad)'],
  absent: ['server.state.absent', '#9098aa'],
  notready: ['server.state.notready', 'var(--warn)'],
  working: ['server.state.working', 'var(--warn)'],
  checking: ['server.state.checking', 'var(--warn)'],
  unknown: ['server.state.unknown', '#9098aa'],
  unlisted: ['server.state.unlisted', '#9098aa'],
};
const SRVNOTE = {
  live: 'server.note.live',
  loading: 'server.note.loading',
  down: 'server.note.down',
  absent: 'server.note.absent',
  notready: 'server.note.notready',
  working: 'server.note.working',
  checking: 'server.note.checking',
  unknown: 'server.note.unknown',
  unlisted: 'server.note.unlisted',
};
const SRVJOBS = {
  start: 'server.job.start', stop: 'server.job.stop', setup: 'server.job.setup',
  provision: 'server.job.provision', txtfixes: 'server.job.txtfixes', netfix: 'server.job.netfix',
  events: 'server.job.events', revive: 'server.job.revive',
  towerfix: 'server.job.towerfix', accountcopy: 'server.job.accountcopy',
  templates: 'server.job.templates', 'templates.ensure': 'server.job.templates',
  secrets: 'server.job.secrets', 'secrets.set': 'server.job.secrets',
  auth: 'server.job.auth', password: 'server.job.password', 'account.password': 'server.job.password', signup: 'server.job.signup',
  hotpatch: 'server.job.hotpatch',
  // The ready-made stack downloaded from the Internet Archive (POST /server/fetch).
  fetch: 'server.job.fetch',
  // The pathfinding server switched on / off (POST /server/pathfinding).
  pathfinding: 'server.job.pathfinding',
  navmesh: 'server.job.navmesh',
  // The pathfinding server's own data of a version downloaded and added to the stack (POST /server/navdata).
  navdata: 'server.job.navdata',
  // An account created from Server Admin mode (POST /server/account/create).
  accountcreate: 'server.job.accountcreate',
  // 'hotpatch.voice' = our own job, 'hotpatch-voice' = the agent's kind (a job started elsewhere).
  'hotpatch.voice': 'server.job.hotpatchVoice', 'hotpatch-voice': 'server.job.hotpatchVoice',
  // A version's stack folder moved / re-pointed / taken off the agent (POST /server/relocate).
  relocate: 'server.job.relocate',
  // The gameplay settings of a version stored and applied (POST /server/tweaks, the Game rules tab).
  tweaks: 'server.job.tweaks',
  // Accounts moved from a server of another version (POST /server/migrate).
  migrate: 'server.job.migrate',
};
// Jobs started by the agent itself (watchdogs), not by a person — "operation started elsewhere
// (another computer)" would be a lie that sends the user to wait for a phantom admin.
const AGENT_SELF_JOBS = { revive: true, towerfix: true };
// The agent's watchdog jobs start BESIDE a voice download (the job's yielding phase) and
// /status then names the newer job as `busy`: such a busy says nothing about the voice job itself.
const BESIDE_VOICE_JOBS = { revive: true, towerfix: true, netfix: true };
const TXTFIX = { none: 'server.txtfix.none', pending: 'server.txtfix.pending', applied: 'server.txtfix.applied' };
// The provisioning choice a stack was prepared with (the progress line of the Players tab, the Overview's
// Prepared step).
const PROGRESS_MODE = { none: 'server.adv.progressMode.none', default: 'server.adv.progressMode.default', keep: 'server.adv.progressMode.keep', fixes: 'server.adv.progressMode.fixes' };
const jobTitle = (kind) => t(SRVJOBS[kind] || 'server.job.generic');
// The title of a job OBJECT: a Prepare / progress job says which choice it carries — "keep" and
// "fixes" have their own words, because "Progress re-apply" would announce the very import the
// admin declined in the dialog.
const jobTitleOf = (j) => {
  if (j && (j.kind === 'setup' || j.kind === 'provision')) {
    if (j.progress === 'keep') return t('server.job.provisionKeep');
    if (j.progress === 'fixes') return t('server.job.provisionFixes');
  }
  // An account copy from a progress template is a starting progress, and a copy that only checks says so.
  if (j && j.kind === 'accountcopy' && j.template) return j.dryRun ? t('server.job.accountTemplateCheck') : t('server.job.accountTemplate');
  if (j && j.kind === 'accountcopy' && j.dryRun) return t('server.job.accountcopyCheck');
  if (j && j.kind === 'migrate' && j.dryRun) return t('server.job.migrateCheck');
  return jobTitle(j ? j.kind : '');
};
const jobRunning = () => !!(S.srvJob && S.srvJob.running);
// The followed job carries the server it was started on (`target`, runServerJob / srvJobFor): after a
// switch to another server it still runs THERE — and a voice packs job does so for an hour. jobHere =
// it is this server's: the only case in which a card may draw it as its own (WORKING, the voice
// running note, Stop — a Stop goes to the CONFIGURED host, so from another server's card it would hit
// the wrong box). The button gating stays on jobRunning(): one followed job per launcher, the backend
// refuses a second one anyway; jobElsewhere says on screen why this server's buttons are dead.
const jobHere = () => jobRunning() && S.srvJob.target === srvTarget();
const jobElsewhere = () => jobRunning() && S.srvJob.target !== srvTarget();
// Busy = our job OR one started elsewhere (another computer, another admin): the agent accepts a
// single operation at a time, so the buttons must be dead in both cases, not only in ours.
const srvBusy = () => jobRunning() || !!(S.srv.info && S.srv.info.busy);
// A job of THIS version runs on this server, whatever its kind: the one this launcher follows (jobHere
// — never one it follows on another server), or the one /status names (started elsewhere, one of the
// agent's own, or ours after the link was lost). What the agent keeps on record while such a job works
// is not yet a state to report: the stack internal password's "did not finish" notes (vendorSummary,
// secretsCard) wait for the job's end.
const versionJobRunning = (id) => (jobHere() && String(S.srvJob.version) === String(id))
  || !!(S.srv.info && S.srv.info.busy && S.srv.info.busy.version != null && String(S.srv.info.busy.version) === String(id));
// "Keep .env settings" on the connected agent (GIO_KEEP_ENV — `keepEnv` of the admin /status): the agent
// then replaces none of the vendor's published secrets on its own and does not rewrite OUTER_IP, so
// nothing on screen may promise a replacement at the next Prepare / Start (vendorSummary, secretsCard,
// the Prepare dialog). Only an answered true counts: no status, a failed read and the public snapshot,
// which never carries the field, read as off.
const srvKeepEnv = () => !!S.srv.info && S.srv.info.keepEnv === true;

// An older agent does not send present/bootstrapped. Their absence means "it is there and installed"
// — otherwise the screen would declare absent some stacks that work perfectly.
function srvFacts(id) {
  const e = S.srv.versions[id];
  if (!e) return null;
  return {
    up: !!e.up, present: e.present !== false, bootstrapped: e.bootstrapped !== false,
    // The agent sends null when its state file is unreadable: unknown, not "not applied".
    provisioned: e.provisioned === null ? null : !!e.provisioned, provisionedAt: e.provisionedAt || '',
    txtFixes: e.txtFixes || 'none', account: e.account || '', dir: e.dir || '',
    // The compose services NOT running although the stack is up (the gameserver exits
    // with code 0 when it dies, so "up" alone says nothing). Absent on old agents = we assert nothing.
    down: Array.isArray(e.servicesDown) ? e.servicesDown : [],
    // Up with dead game services. The public snapshot sets `degraded` without naming them (empty down),
    // so readers test this flag and show the list only when it is not empty.
    degraded: !!e.degraded || (Array.isArray(e.servicesDown) && e.servicesDown.length > 0),
    // Every service runs, but the game server is still loading its data and lets nobody in until it is
    // done. Only an answered true: false, null (the agent's log cannot tell) and an agent that does not
    // send the key all leave the server as it reads without it.
    loading: e.loading === true,
    // Progress templates present on the box + whether passwords are verified in game.
    templates: Array.isArray(e.templates) ? e.templates : (e.templates && typeof e.templates === 'object' ? Object.keys(e.templates).map((id) => ({ id, label: id })) : []),
    passwordVerify: !!e.passwordVerify,
    // {available, enabled, pending, complete} of the hotpatch mirror (the official in-version fixes).
    hotpatch: e.hotpatch && typeof e.hotpatch === 'object' ? e.hotpatch : null,
    // 'vendor' = the stack still carries the archive's published MUIP sign key (anyone with
    // the archive can sign GM commands) | 'custom' | null (absent stack / older agent).
    muipKey: e.muipKey === 'vendor' || e.muipKey === 'custom' ? e.muipKey : null,
    // The same word on the MySQL root password and on the Flask secret key — 'vendor' = still
    // the archive's published value, which the agent replaces with a private one when it prepares or
    // starts the stack | 'custom' | null (absent stack, a value it cannot read, or an older agent, which
    // sends neither field: nothing is said then).
    mysqlPassword: e.mysqlPassword === 'vendor' || e.mysqlPassword === 'custom' ? e.mysqlPassword : null,
    flaskKey: e.flaskKey === 'vendor' || e.flaskKey === 'custom' ? e.flaskKey : null,
    // The same word on the stack internal password (redis + hk4e_work / hk4e_readonly, judged
    // by what redis and the game services really use), plus 'pending' = a change of it is on record and
    // not finished: the next Start of the version completes it (Prepare server, while the version is not
    // installed yet). The agent also keeps that record on purpose while its own job changes the password
    // (a fresh Prepare's bootstrap, a Secrets rotation): no note while a job of the version runs
    // (versionJobRunning). An agent that does not send the field says nothing.
    internalPassword: e.internalPassword === 'vendor' || e.internalPassword === 'custom' || e.internalPassword === 'pending' ? e.internalPassword : null,
    // The choice the stack was prepared with ('default' = the shipped save imported, 'keep'
    // = the admin's own progress, 'fixes' = configuration files only; null = never / older agent) and
    // whether the shipped save — the default account — is in THIS database (null = the agent's state
    // file is unreadable, undefined = older agent). The account shown on the card follows it.
    progress: e.progress === 'default' || e.progress === 'keep' || e.progress === 'fixes' ? e.progress : null,
    defaultAccount: e.defaultAccount === undefined ? undefined : e.defaultAccount,
    // What the agent's payload does NOT ship for this version — each true only when the agent has the
    // version's payload manifest (hasPayload) AND answers the fact with false: without a manifest it
    // reports all three false while nothing tells what the version ships, and an agent that does not
    // send them says nothing. noSave = no pre-made account a Prepare could import, noTxtFixes = nothing
    // to apply or revert, noEvents = the server keeps the event schedule it came with.
    noSave: e.hasPayload === true && e.hasSave === false,
    noTxtFixes: e.hasPayload === true && e.hasTxtFixes === false,
    noEvents: e.hasPayload === true && e.hasEvents === false,
    // The pathfinding server in the stack's compose file — true/false, or null = unknown
    // (older agent, an absent stack, or the service not in the file): the toggle asserts nothing then.
    pathfinding: typeof e.pathfinding === 'boolean' ? e.pathfinding : null,
    // What a Prepare without a choice would apply to THIS stack (the agent's configured default, and
    // only with a navmesh file to load) — true/false, or null when the agent does not say.
    pathfindingDefault: typeof e.pathfindingDefault === 'boolean' ? e.pathfindingDefault : null,
    // {available, size, host, topDir, part, archive, tool, fetchedAt} of the ready-made
    // stack download (POST /server/fetch); null on an older agent — the card keeps the plain MISSING.
    // configured:false = the version has no folder on this agent, nothing to download into.
    fetch: e.fetch && typeof e.fetch === 'object' ? e.fetch : null,
    configured: e.configured !== false,
    // {vendorList, bundled, installed, scenes, stackFiles, archive} of the navmesh files for the
    // pathfindingserver: what the agent's payload ships (bundled), how many of them are in the stack
    // (installed), the scene ids they cover, how many navmesh files the stack's own folder holds
    // (stackFiles — the package's and the installed ones; an agent may not send it) and the zip the
    // payload was adopted from ({name, sha256, adoptedAt}, null = the installer shipped it);
    // vendorList:false = the stack has no md5 list, so the service loads nothing; bundled null = the
    // agent could not read its own state. null = an older agent or an absent stack: the pathfinding card
    // says nothing.
    navmesh: e.navmesh && typeof e.navmesh === 'object' ? e.navmesh : null,
    // The versions whose accounts can move into this one (admin /status only, in the manifest's order). An agent that
    // cannot move any names none, and the card is not drawn.
    migrateFrom: Array.isArray(e.migrateFrom) ? e.migrateFrom.filter((x) => typeof x === 'string' && x) : [],
  };
}

// The agent answered, but `docker compose ls` failed/timed out on the box: every version then carries
// up:false without it being true. The library badge refuses to call that STOPPED; the cards
// and the start-screen pill must not call it OFFLINE either.
const srvDockerError = () => !!(S.srv.info && S.srv.info.error);
function srvState(id) {
  // The followed job turns its version's card to WORKING when it runs on THIS server (jobHere) and
  // works on the stack. A voice packs job only fills the mirror folder — for an hour — while the game
  // server stays up: the card keeps LIVE and its DEGRADED line (the agent goes on reporting
  // servicesDown during that job for this very reason); the buttons are dead through srvBusy() anyway.
  // An account creation is the same case: it only writes the SDK and the player database
  // of a RUNNING stack — it needs the stack up and stops nothing, so the card stays LIVE. So is an
  // account's password ('account.password' until its first event names it 'password'): it only writes the
  // SDK's database of a running stack.
  // So is a gameplay-settings job: it rewrites data files the running server is asked to read again;
  // nothing is stopped and nobody is disconnected. So is a move of accounts: it writes into the running target's
  // database while its players stay online.
  if (jobHere() && S.srvJob.version === id && !['hotpatch.voice', 'accountcreate', 'password', 'account.password', 'tweaks', 'migrate'].includes(S.srvJob.kind)) return 'working';
  const f = srvFacts(id);
  if (!f) return S.srv.state === 'checking' ? 'checking' : srvUnlisted(id) ? 'unlisted' : 'unknown';
  if (!f.present) return 'absent';
  if (!f.bootstrapped) return 'notready';
  // Dead services outrank the load: the degraded card has its own line and its Restart button.
  if (f.up) return f.loading && !f.degraded ? 'loading' : 'live';
  return srvDockerError() ? 'unknown' : 'down';
}
// A version map (statusVersions) that names versions and not this one. A map without a single version
// says nothing about the list, and one that names the id with an entry nothing can be read from is a
// broken answer about a version it does name — neither is "the agent does not know this version".
const versionsLack = (vs, id) => !!vs && Object.keys(vs).length > 0 && !Object.prototype.hasOwnProperty.call(vs, id);
// The agent answered with the versions it knows, and this one is not among them: nothing about it can
// be shown or started here. Only on a fresh answer that could ask docker and lists at least one version —
// while checking, after an error or under the docker fog nothing is said about the list.
function srvUnlisted(id) {
  return S.srv.state === 'ok' && !srvDockerError() && versionsLack(S.srv.versions, id);
}

// The version whose stack is running on the box RIGHT NOW, or null. Only one can run at a time
// (identical ports), so the first one found is the complete answer.
function srvLiveId() {
  for (const id of Object.keys(S.srv.versions || {}))
    if (S.srv.versions[id] && S.srv.versions[id].up) return id;
  return null;
}

// Server status on the main page: GLOBAL (what runs on the box), not the selected version's — the
// player wants to know whether there is something to connect to and on which version. The data comes
// from the same S.srv as the "Server status" screen, refreshed by the polling in boot(). Without a
// configured server nothing is asserted; a contact error = "unverified", not an invented STOPPED (the
// agent may be down with the game server perfectly functional).
function srvBadge() {
  if (!S.serverConfigured) return '';
  const go = `data-act="serverOverview" style="cursor:pointer"`;
  if (S.srv.state === 'ok') {
    const live = srvLiveId();
    // up:true is an affirmative fact and stays LIVE even next to an error reported by the agent...
    if (live) {
      // ...but "running" with dead game services is not LIVE: the agent restarts them on its own
      // (the service watchdog, under ~2 minutes) — until then the player sees DEGRADED, not a LIVE
      // that ends in a white screen in game.
      const lf = srvFacts(live);
      if (lf && lf.degraded)
        return `<span class="badge" ${go} title="${lf.down.length ? T('server.badge.degradedTitle', { services: lf.down.join(', ') }) : T('server.badge.degradedTitleNoList')}"><span class="dot" style="background:var(--warn)"></span>${T('server.badge.label')} <b style="color:var(--warn)">&nbsp;${T('server.badge.degraded', { version: live })}</b></span>`;
      // Every service is up and the game server still loads its data: a client that connects is turned
      // away, so this is not LIVE yet either.
      if (lf && lf.loading)
        return `<span class="badge" ${go} title="${T('server.badge.loadingTitle')}"><span class="dot" style="background:var(--warn)"></span>${T('server.badge.label')} <b style="color:var(--warn)">&nbsp;${T('server.badge.loading', { version: live })}</b></span>`;
      return `<span class="badge" ${go} title="${T('server.badge.liveTitle', { version: live })}"><span class="dot live"></span>${T('server.badge.label')} <b style="color:var(--ok)">&nbsp;${T('server.badge.live', { version: live })}</b></span>`;
    }
    // ...but a "nothing up" with a top-level error is not STOPPED: docker compose ls failed on the box
    // and up:false is fabricated for every version — the server may be humming along. Same rule as the
    // PLAY pre-check and as in --play: without data, nothing is asserted.
    if (!(S.srv.info && S.srv.info.error))
      return `<span class="badge" ${go} title="${T('server.badge.stoppedTitle')}"><span class="dot" style="background:var(--bad)"></span>${T('server.badge.label')} <b style="color:var(--bad)">&nbsp;${T('server.badge.stopped')}</b></span>`;
  }
  if (S.srv.state === 'error' || S.srv.state === 'ok') {
    const why = S.srv.error || (S.srv.info && S.srv.info.error) || t('server.badge.unreachable');
    return `<span class="badge" ${go} title="${esc(String(why))}"><span class="dot" style="background:#9098aa"></span>${T('server.badge.label')} ${T('server.badge.unverified')}</span>`;
  }
  return `<span class="badge"><span class="dot" style="background:var(--warn)"></span>${T('server.badge.label')} ${T('server.badge.checking')}</span>`;
}

// The version cards of the Server status page (Player mode) side by side: three in a row for three versions
// (not two and one alone under them), two rows of two for four, rows of three beyond; one or two keep the two
// columns.
const cardsColumns = (n) => (n === 3 || n > 4 ? 3 : 2);
const cardsGridStyle = (n) => `style="grid-template-columns:repeat(${cardsColumns(n)},minmax(0,1fr))"`;
// The words of a version row on the admin tabs while its facts say nothing more specific ('' = they do): no facts
// (checking, a failed read, a version this server's agent does not list), missing, not installed yet.
function rowState(id, f) {
  if (!f) return srvUnlisted(id) ? T('server.adv.stateUnlisted') : T('server.adv.stateUnknown');
  if (!f.present) return T('server.adv.stateAbsent');
  return f.bootstrapped ? '' : T('server.adv.stateNotReady');
}
// What a card of version rows draws in place of its rows while no server is configured: the rows would only say
// that nothing is known and send the admin to a Check the header does not draw then (the strip says how to
// choose a server).
const noServerLine = () => `<p class="hint" style="margin:0">${T('server.agent.notConfigured')}</p>`;
// The Overview tab: the server's facts, one row per catalogue version (overviewRow), and the rule that only one
// version runs at a time. The status is read again with the header's Check.
function overviewCard() {
  return `<div class="sect"><h4>${T('server.overview.title')}</h4>${overviewFacts()}${S.serverConfigured ? S.versions.map(overviewRow).join('') : noServerLine()}
    <p class="hint ovhint">${T('server.overview.oneAtATime')}</p></div>`;
}
// Where the agent is dialled and how, which agent answers, the public address (a DNS name with the address it
// resolves to, the lookup error as its tooltip).
function overviewFacts() {
  if (!S.serverConfigured) return '';
  const info = S.srv.info && !S.srv.info.public ? S.srv.info : {};
  const parts = [esc(agentAddr()), S.admin.mode === 'ssh' ? T('server.agent.ssh') : T('server.agent.direct')];
  if (info.agent) parts.push(`${T('server.agent.agentLabel')} ${esc(info.agent)}${info.platform ? ' · ' + esc(info.platform) : ''}`);
  if (info.advertisedHost) parts.push(`<span title="${esc(info.advertisedError || '')}">${esc(info.advertisedHost)} → ${esc(info.advertisedIp || t('server.agent.unresolved'))}</span>`);
  else if (info.advertisedIp) parts.push(esc(info.advertisedIp));
  return `<div class="ovfacts">${parts.join(' · ')}</div>`;
}
// A version the agent has no folder for (`configured` false in the admin /status; the public snapshot names no such
// fact, so a player's view keeps its own words): NOT SET UP — not a state of srvState, the Overview row and the
// package row draw it themselves, with "Add version…" as the way on.
const srvNotSetUp = (st, f) => st === 'absent' && !!f && f.configured === false;
// What a download into a version's folder asks of the server's disk, as the agent reports it in `fetch` (bytes; an
// agent that reports none says nothing here): need = what the volume of the folder's parent still has to take,
// free = what it has free, problem = the agent's own sentence of the refusal a download would meet now ('' = none).
const isBytes = (n) => typeof n === 'number' && isFinite(n) && n >= 0;
const fetchProblem = (fe) => (fe && typeof fe.problem === 'string' ? fe.problem.trim() : '');
const fetchShort = (fe) => !!fe && isBytes(fe.need) && isBytes(fe.free) && fe.free < fe.need;
// A download the agent would refuse now (its sentence, or a disk too small): its button is dead and it gets no question.
const fetchRefused = (fe) => !!fetchProblem(fe) || fetchShort(fe);
// The version's package download can start now: its folder holds no server yet, a package is known and this server
// can unpack it, nothing the agent reports refuses it, and no job runs.
function fetchReady(id) {
  const f = srvFacts(id), fe = f && f.fetch;
  return !!f && !f.present && f.configured && !!fe && !!fe.available && !!fe.tool && !fetchRefused(fe) && !srvBusy();
}
// The disk line of a download (server.pkg.disk), escaped: the need, made of what is left of the archive, the unpacked
// server (the catalogue's size, three times the archive when it names none — the agent's own rule) and the reserve,
// then the free space of that drive — the need alone when the agent could not read it.
function fetchDiskText(fe) {
  const size = Math.max(0, Number(fe.size) || 0), unpacked = isBytes(fe.extractedSize) && fe.extractedSize > 0 ? fe.extractedSize : 3 * size;
  const reserve = isBytes(fe.reserve) ? fe.reserve : 0, rest = Math.max(0, fe.need - unpacked - reserve);
  const parts = { need: sizeGb(fe.need), size: sizeGb(rest), unpacked: sizeGb(unpacked), reserve: sizeGb(reserve) };
  return isBytes(fe.free) ? T('server.pkg.disk', Object.assign(parts, { free: sizeGb(fe.free) })) : T('server.pkg.diskNoFree', parts);
}
// Why a version's folder cannot be set from here now: "Add version…" and "Change folder…" go through the agent's
// relocation, which writes its configuration file. '' = it can; null = not known yet (the agent's settings are being
// read, or the agent restarts); else the reason — the settings could not be read, this agent cannot change them, or it
// cannot write its file.
function folderBlock() {
  const c = S.agentCfg, d = c.data;
  if (!d) return c.loading || !c.error ? null : t('agentCfg.loadFailed', { error: c.error });
  if (d.tooOld) return t('agentCfg.tooOld');
  if (!d.writable) return d.file ? t('agentCfg.readOnly') + (d.writableReason ? ` (${d.writableReason})` : '') : t('agentCfg.noFile');
  return c.restarting ? null : '';
}
// The words under a row whose "Add version…" / "Change folder…" is dead for a reason the agent's settings give,
// escaped ('' = none to say).
function folderBlockText() {
  const b = folderBlock();
  if (!b) return '';
  return S.agentCfg.data ? T('server.overview.addNeedsConfig', { reason: b }) : esc(b);
}
// "Add version {id}…" (a version the agent has no folder for) and "Change folder…" (a configured version whose folder
// holds no server yet): the folder dialog that points the agent at a folder and, when there is a package to download,
// downloads it there (relocOverlay, flow 'add' / 'change'). Dead while a job runs and while the folder cannot be set.
const folderDead = () => srvBusy() || folderBlock() !== '';
const addVersionButton = (id) => `<button class="btn" data-act="addVersion" data-arg="${esc(id)}" ${folderDead() ? 'disabled' : ''}>${T('server.card.addVersion', { id })}</button>`;
const changeFolderButton = (id) => `<button class="btn ghost" data-act="changeFolder" data-arg="${esc(id)}" ${folderDead() ? 'disabled' : ''}>${T('server.card.changeFolder')}</button>`;
// One version on the Overview: its state badge, the lifecycle line, the in-game account (or the admin's way to one),
// the pathfinding fact, the note of its state and ONE next step. Dead game services on a live stack: the agent's
// watchdog restarts them within minutes, "Restart services" (server.start over the running stack) is the supervised
// repair beside Stop. A version without a folder reads NOT SET UP and is added here ("Add version…"); a folder that
// holds no server yet gets the download, the disk it needs or the agent's refusal of it, and "Change folder…".
function overviewRow(v) {
  const st = srvState(v.id), f = srvFacts(v.id), meta = SRVMETA[st], fe = f && f.fetch;
  const dis = srvBusy() ? 'disabled' : '';
  const isDegraded = st === 'live' && !!f && f.degraded;
  const installed = st === 'live' || st === 'loading' || st === 'down';
  const notSetUp = srvNotSetUp(st, f);
  const canFetch = st === 'absent' && !!(fe && fe.available && f.configured);
  // The agent's refusal of a download there (its sentence states the numbers of a disk too small), else the disk the
  // download needs — once the agent reports them, which is also when the folder can be changed from the row.
  const problem = canFetch ? fetchProblem(fe) : '', disk = canFetch && !problem && isBytes(fe.need);
  const changeable = canFetch && (disk || !!problem);
  // A package this server could not unpack (no 7z tool for a .7z-only package): said under the download it offers — a
  // version without a folder too, whose note promises that download.
  const noTool = (canFetch || (notSetUp && !!fe && !!fe.available)) && !fe.tool && !problem;
  const note = st === 'unknown' && srvDockerError() ? T('server.note.dockerError', { error: String(S.srv.info.error) })
    : notSetUp ? (fe && fe.available ? T('server.overview.notSetUp', { id: v.id, size: sizeGb(fe.size) }) : T('server.overview.notSetUpNoPackage', { id: v.id }))
      : canFetch ? T('server.note.absentFetch', { size: sizeGb(fe.size), dir: f.dir })
        : st === 'absent' && !fe ? T('server.overview.absentOld', { id: v.id, dir: f.dir })
          : st === 'absent' ? T('server.overview.absentNoPackage', { id: v.id, dir: f.dir })
            : T(SRVNOTE[st]);
  const warn = (html) => `<p class="ovnote warn">${html}</p>`, bad = (html) => `<p class="ovnote bad">${html}</p>`;
  const block = notSetUp || changeable ? folderBlockText() : '';
  const extra = (isDegraded ? warn(f.down.length ? T('server.card.degraded', { services: f.down.join(', ') }) : T('server.card.degradedNoList')) : '')
    + (problem ? bad(esc(problem)) : '')
    + (disk ? (fetchShort(fe) ? bad(fetchDiskText(fe)) : `<p class="ovnote">${fetchDiskText(fe)}</p>`) : '')
    + (noTool ? warn(T('server.note.noExtractor')) : '')
    + (block ? warn(block) : '')
    + (f && !f.account && (f.progress === 'keep' || f.progress === 'fixes') && installed ? `<p class="ovnote">${T('server.overview.noAccount')}</p>` : '');
  const facts = (f && f.account ? `<span><small>${T('server.card.accountLabel')}</small><b class="mono">${esc(f.account)}</b></span>` : '')
    + (f && f.present && f.pathfinding !== null ? `<span><small>${T('server.card.pathfindingLabel')}</small><b>${f.pathfinding ? T('server.pathfinding.on') : T('server.pathfinding.off')}</b></span>` : '');
  const btn = (act, label, ghost) => `<button class="btn${ghost ? ' ghost' : ''}" data-act="${act}" data-arg="${esc(v.id)}" ${dis}>${label}</button>`;
  const cta = notSetUp ? addVersionButton(v.id)
    : st === 'absent' ? (canFetch ? fetchButton(v.id, fe) : '') + (changeable ? changeFolderButton(v.id) : '')
      : st === 'unknown' || st === 'unlisted' || st === 'working' || st === 'checking' ? ''
        : st === 'notready' ? btn('setupServer', T('server.card.setup'))
          : st === 'live' || st === 'loading' ? (isDegraded ? btn('startServer', T('server.card.restartServices')) : '') + btn('stopServer', T('server.card.stop'), true)
            : btn('startServer', T('server.card.start'));
  return `<div class="ovrow" data-ov="${esc(v.id)}">
    <div class="ovid"><b>${T('common.versionN', { id: v.id })}</b><span class="badge" style="background:rgba(0,0,0,.06);color:${meta[1]}"><span class="dot" style="background:${meta[1]}"></span>${notSetUp ? T('server.overview.badgeNotSetUp') : T(meta[0])}</span></div>
    <div class="ovmain">${lifecycleLine(st, f)}${facts ? `<div class="ovfact">${facts}</div>` : ''}<p class="ovnote">${note}</p>${extra}</div>
    <div class="ovacts">${cta}</div></div>`;
}
// The package download's button of a version the server lacks (Overview row, Server packages): Download / Resume /
// Unpack with the size, dead while a job runs, on a box that cannot unpack the archive and while the agent would
// refuse the download (fetchRefused: the row says why).
function fetchButton(id, fe) {
  const size = sizeGb(fe.size), part = Math.max(0, Number(fe.part)) || 0;
  const label = fe.archive === true ? T('server.card.fetchUnpack', { size }) : part > 0 ? T('server.card.fetchResume', { have: sizeGb(part), size }) : T('server.card.fetch', { size });
  return `<button class="btn" data-act="fetchServer" data-arg="${esc(id)}" ${srvBusy() || !fe.tool || fetchRefused(fe) ? 'disabled' : ''}>${label}</button>`;
}
// Folder → Package → Prepared (mode) → Running, read off the status' facts: a step is done, the first step not done is
// the current one, the later ones are not yet; Running reads "Starting" while the game server loads, warns while
// services are down and asserts nothing under the docker fog. Nothing without facts.
function lifecycleLine(st, f) {
  if (!f) return '';
  const folder = f.present || f.configured, pkg = f.present, prepared = f.present && f.bootstrapped;
  const run = prepared && f.up ? (st === 'loading' || f.degraded ? 'warn' : 'done') : prepared ? (srvDockerError() ? 'unk' : 'cur') : 'todo';
  const cls = [folder ? 'done' : 'cur', pkg ? 'done' : folder ? 'cur' : 'todo', prepared ? 'done' : pkg ? 'cur' : 'todo', run];
  const mode = prepared && f.progress ? t(PROGRESS_MODE[f.progress]) : '';
  const labels = [T('server.life.folder'), T('server.life.package'), mode ? T('server.life.preparedMode', { mode }) : T('server.life.prepared'),
    st === 'loading' ? T('server.life.starting') : T('server.life.running')];
  return `<div class="lifeline">${labels.map((label, i) => `<span class="lcstep ${cls[i]}"${i === 0 && f.dir ? ` title="${esc(f.dir)}"` : ''}><i></i>${label}</span>`).join('')}</div>`;
}

// The agent prefixes its lines with the time, so "ERROR" is not necessarily at the start.
const srvLogLine = (l) => `<div style="color:${l.indexOf('ERROR') >= 0 ? '#e08a7f' : '#8aa0c8'}">${esc(l)}</div>`;
// The "Stop waiting" button of a job of that kind and version (act: the console's tweaksStop, or
// tweaksStopBusy beside the note of a job started elsewhere): a tweaks job's waits are the whole job, the
// others' only their last step — label and hint say which. Dead once its stop was sent.
const twStopButton = (act, kind, version, sent) => `<button class="btn ghost" data-act="${act}" data-arg="${esc(version)}" title="${T(kind === 'tweaks' ? 'server.console.stopWaitingTitle' : 'server.console.stopWaitingSettingsTitle')}" ${sent ? 'disabled' : ''}>${T(kind === 'tweaks' ? 'server.console.stopWaiting' : 'server.console.stopWaitingSettings')}</button>`;
// One "Stop waiting" request (rpc server.tweaksStop → POST /server/tweaks {version, cancel: true}) and what
// the page says about its answer; true = the waits end (stopping), and the button stays dead. stopping:false
// = no job of that version whose work ends in those waits runs any more — it ended meanwhile: said, and the
// status is read again. unsupported = an agent that cannot do it (its 400 / 404): its reason is said, and
// the control is not offered to that agent again (twStopRefused) — the job goes on as it would have. An
// answer that arrives after another server came on screen says nothing.
async function twStopSend(version) {
  const target = srvTarget();
  try {
    const r = await rpc('server.tweaksStop', { version });
    if (srvTarget() !== target) return false;
    if (r && r.unsupported) {
      const info = S.srv.info;
      S.twStop.no[target] = info && !info.public && typeof info.build === 'string' ? info.build : '';
      showToast(String(r.message || t('core.agent.tweaksStopUnsupported')), 'bad');
      return false;
    }
    if (r && r.stopping) { showToast(t('toast.tweaksStopSent'), 'ok'); return true; }
    showToast(t('toast.tweaksStopNone', { version }), 'ok');
    checkServers(true);
    return false;
  } catch (e) {
    if (srvTarget() === target) showToast(e.message, 'bad');
    return false;
  }
}

// Log of the operation in progress — the same console as on the commands screen, so the user does
// not learn two forms for the same thing.
function serverConsole() {
  const j = S.srvJob;
  if (!j) return '';
  const lines = j.lines.map(srvLogLine).join('');
  const tail = j.running ? '' : (j.error ? ' · ' + T('server.console.failed') : ' · ' + T('server.console.done'));
  // A running stack download of THIS server (jobHere — the cancel goes to the configured host): the
  // agent's cancel is a plain call that gets through while the job runs, like the voice job's Stop;
  // the .part stays and the download's button (the Overview row, Server packages) resumes it. One click,
  // dead until the job's state changes.
  // A stack folder copied to another drive (relocate) stops the same way: the copy is
  // deleted, the old folder and the configuration stay as they were. crossDrive comes from the folder
  // dialog's check, or from the agent's own "Copying N files" line (server.log) — a rename on the same
  // drive takes a moment and has nothing to stop.
  // A navmesh archive download and a pathfinding data download stop the same way as the stack download
  // (their .part stays too). A move of accounts stops the same way: the account in hand ends, the others are not
  // touched. A voice packs job of THIS server stops the same way as the hotpatch card's Stop
  // (hotpatchVoiceStop); both read S.hotpatch.voiceStop.
  const voice = j.kind === 'hotpatch.voice', voiceSent = voice && S.hotpatch.voiceStop === String(j.version);
  const stopAct = j.kind === 'fetch' ? 'fetchCancel' : j.kind === 'navmesh' ? 'navmeshCancel' : j.kind === 'navdata' ? 'navdataCancel'
    : j.kind === 'migrate' ? 'migrateCancel' : j.kind === 'relocate' && j.crossDrive ? 'relocCancel' : voice ? 'hotpatchVoiceStop' : '';
  const stopLabel = stopAct === 'relocCancel' ? T('agentCfg.reloc.stop') : stopAct === 'migrateCancel' ? T('migrate.stop') : !voice ? T('server.card.fetchCancel')
    : voiceSent ? T('hotpatch.voice.stopping') : T('hotpatch.voice.stop');
  const stopBtn = stopAct ? `<button class="btn ghost" data-act="${stopAct}" data-arg="${esc(j.version)}" ${(voice ? voiceSent : j.cancelSent) ? 'disabled' : ''}>${stopLabel}</button>` : '';
  // A job whose work ends in the gameplay settings' waits for the game server (TW_STOP_KINDS: a tweaks job,
  // or a start / provision / setup / relocate whose last step hands the stored settings over) can be told
  // to stop waiting — a plain call that gets through while the job runs, like the stops above; a tweaks job
  // then ends, the others go on without that step. Not offered to an agent that said it cannot do it.
  const twStop = TW_STOP_KINDS.includes(j.kind) && !twStopRefused() ? twStopButton('tweaksStop', j.kind, j.version, j.tweaksStopSent) : '';
  const cancel = j.running && (stopBtn || twStop) && jobHere()
    ? `<div class="flex gap wrap" style="justify-content:flex-end;margin-top:10px">${twStop}${stopBtn}</div>` : '';
  return `<div class="console"><div class="bar"><span class="lights"><i style="background:#e4664e"></i><i style="background:#e8b04a"></i><i style="background:#67b86a"></i></span>
      <span>${esc(jobTitleOf(j).toLowerCase())} · ${T('server.console.version', { version: j.version })}${tail}</span></div>
    <div class="log" id="srvlog">${lines || `<div style="color:#8aa0c8">${T('server.console.connecting')}</div>`}</div></div>
    ${j.running ? cancel : `<div class="flex gap" style="justify-content:flex-end;margin-top:10px"><button class="btn ghost" data-act="closeSrvLog">${T('server.console.closeLog')}</button></div>`}`;
}

// The Players tab's "Copy by name or uid", below the account list: a row per catalogue version and, under the row it
// was opened from, the form (S.acctCopy) — the way to an account the list does not show (a game uid without a login,
// a name typed by hand). The copy works through the live MySQL without a stop: only a RUNNING version takes it, and
// under the docker fog nothing tells whether one runs. An agent that answered the account list checks the copy first
// (acctCopyGo) and creates a target only under a name a signup may take, and the form says so.
function acctCopyCard() {
  const busy = srvBusy(), fog = srvDockerError();
  const rows = S.versions.map((v) => {
    const f = srvFacts(v.id);
    const cdis = busy || !f || !f.up ? 'disabled' : '';
    const sub = rowState(v.id, f) || (fog ? T('server.adv.stateUnknown') : f.up ? '' : T('server.copy.notRunning'));
    const c = S.acctCopy && S.acctCopy.version === v.id ? S.acctCopy : null;
    const copyForm = !c ? '' : `
      <div class="sect" style="margin-top:10px;padding:14px">
        <b>${T('server.acct.title', { version: v.id })}</b>
        <p class="hint" style="margin:8px 0 10px">${T('server.acct.hint')}${S.accts.supported ? ' ' + T('server.acct.create') + ' ' + T('server.acct.check') : ''}</p>
        <div class="flex gap wrap">
          <div><label class="fld">${T('server.acct.from')}</label><input class="txt" style="width:170px" data-model="acctCopy.from" value="${esc(c.from)}" placeholder="${T('server.acct.fromPlaceholder')}"/></div>
          <div><label class="fld">${T('server.acct.to')}</label><input class="txt" style="width:190px" data-model="acctCopy.to" value="${esc(c.to)}" placeholder="${T('server.acct.toPlaceholder')}"/></div>
        </div>
        <div class="flex gap" style="margin-top:10px">
          <button class="btn" style="padding:8px 14px;font-size:12px" data-act="acctCopyGo">${T('server.acct.go')}</button>
          <button class="btn ghost" style="padding:8px 14px;font-size:12px" data-act="acctCopyClose">${T('common.giveUp')}</button>
        </div>
      </div>`;
    return `<div class="line"><div><b>${T('common.versionN', { id: v.id })}</b>${sub ? `<small>${sub}</small>` : ''}</div>
      <button class="btn ghost" style="padding:8px 12px;font-size:12px" data-act="acctCopyOpen" data-arg="${esc(v.id)}" ${cdis} title="${T('server.adv.copyTitle')}">${T('server.adv.copy')}</button></div>${copyForm}`;
  }).join('');
  return `<div class="sect"><h4>${T('server.copy.title')}</h4><p class="hint">${T('server.copy.hint')}</p>${S.serverConfigured ? rows : noServerLine()}</div>`;
}

// ── admin: "Accounts on this server" (the Players tab's first card) ──
// Every account of one version as the server holds it, from GET /server/accounts (server.accounts: a read, never a
// job): the logins from the sdk and, while the version runs, each account's game uid, nickname, adventure rank, last
// save and whether it is online. Read when the tab opens with nothing held for the version on screen (acctsOpen), at a
// chip, a search and Refresh, and again after a job of that version — never on a timer. A row acts through the jobs
// that exist: the account copy (a check first, then the copy), a starting progress (a copy from a progress template),
// the password, and Game commands with the version and the uid filled in.
// One shape for every reset: boot, another server (srvRetarget), an agent install (deploy.done), an agent update that
// replaced the agent (agentUpdateEnded). version = the chip picked (null = acctsCur decides); q = the search field as
// typed; data = the last answer (acctsData) and rows = the rows of every page read since; loading / more = a first or
// a next page in flight; seq = the read that owns the card; error / errorVersion / errorQuery = why the last first-page
// read failed, for which version and with which search; moreError = why a next page failed; unsupported = the agent has
// no such route; supported = it answered the route in this session (and has not answered since that it has none): the
// copy's check and its uid and template sources go to such an agent only (one without the route would run the check
// as the copy itself); up = the status' word on that version's stack when it was
// read; old = what is held was read before or during a job of the version, and is read again once that is over; busyOf
// = the version of a job the status names that this launcher does not follow. menu = the row whose actions are open;
// pick = "Copy progress to…" waiting for its target ({version, from, name}); panel = an editor under a row ({kind:
// 'template' | 'password', version, key, row, …}); flow = the copy under way ({kind: 'copy' | 'template', version, body,
// from, to, tpl, form, asNew, step: 'dry' | 'look' | 'confirm' | 'real', job, res}).
function acctsBlank() {
  return { version: null, q: '', data: null, rows: [], loading: false, more: false, seq: 0, error: '', errorVersion: '', errorQuery: '', moreError: '',
    unsupported: false, supported: false, up: null, old: false, busyOf: '', menu: '', pick: null, panel: null, flow: null };
}
// The versions the card offers: the ones the server has (present in the admin /status), the running one first.
function acctsVersions() {
  const ids = S.versions.map((v) => v.id);
  Object.keys(S.srv.versions || {}).forEach((id) => { if (!ids.includes(id)) ids.push(id); });
  const have = ids.filter((id) => { const f = srvFacts(id); return !!f && f.present; });
  return have.filter((id) => acctsUp(id)).concat(have.filter((id) => !acctsUp(id)));
}
// The version on screen: the chip picked while the server still has that version, else the one that runs, else the
// first one it has. A status read that failed names none: the version whose answer is held stays on screen.
function acctsCur() {
  const ids = acctsVersions(), a = S.accts;
  if (a.version && (ids.includes(a.version) || (!ids.length && !!a.data && a.data.version === a.version))) return a.version;
  return ids[0] || (a.data ? a.data.version : '');
}
const acctsUp = (v) => { const f = srvFacts(v); return !!(f && f.up); };
// The answer held for the version on screen (null = none).
const acctsHeld = () => { const a = S.accts, v = acctsCur(); return v && a.data && a.data.version === v ? a.data : null; };
const acctsRow = (key) => S.accts.rows.find((r) => r.key === key) || null;
// What an action keeps of a row: the account, its game uid and its character.
const acctsSnap = (r) => ({ key: r.key, name: r.name, sdkUid: r.sdkUid, guest: r.guest, uid: r.uid, nickname: r.nickname, level: r.level });
// The progress templates imported on a version's stack (acctNewStock's word): what a starting progress is copied from.
const acctsTemplates = (v) => acctNewStock(v).filter((x) => x.id !== 'fresh' && x.ready);
// The version ships no progress template at all: the agent's own list of its templates (GET /server/templates) names
// none. (The /status records name only the imported ones, so they never tell that.)
function acctsShipsNoTemplate(v) {
  const r = S.acctNew.tpl[v];
  return !!(r && Array.isArray(r.list)) && !r.list.some((x) => x.id !== 'fresh');
}
// The card drawn again after something it shows changed — later, while a field has the caret (renderOrOwe).
function acctsRedraw() { if (S.screen === 'server' && isAdminMode() && adminTabOn() === 'players') renderOrOwe(); }
// The field with the caret given up before a read the card will draw (a redraw waits while a field has it).
function acctsBlurField() { const el = document.activeElement; if (el && el.blur && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA')) el.blur(); }

// One row of an answer: the fields the card draws and nothing else; a row without an sdk uid is left out. A guest
// login has no name to log in or to set a password with (the route's own rule): one the agent marks a guest is drawn
// and reached as a guest — by its game uid — whatever name its row carries.
function acctsRowOf(x) {
  if (!x || typeof x !== 'object') return null;
  const n = (v) => (Number.isInteger(v) && v >= 0 ? v : null), s = (v) => (typeof v === 'string' ? v : null);
  const sdk = n(x.sdkUid);
  if (sdk === null) return null;
  const guest = x.guest === true || !(typeof x.name === 'string' && x.name), name = guest ? null : x.name;
  return { key: 's' + sdk, sdkUid: sdk, name, guest, uid: n(x.uid), nickname: s(x.nickname), level: n(x.level),
    lastSave: s(x.lastSave), lastLogin: s(x.lastLogin), online: typeof x.online === 'boolean' ? x.online : null,
    presenceStale: x.presenceStale === true, origin: s(x.origin) || '' };
}
// The answer's own facts. The counts cover every page; characters and presence say whether the game's half could be
// read (ok | stopped | error — presence never better than characters), withCharacter and online only while it could.
function acctsData(r, v, q) {
  const n = (x) => (Number.isInteger(x) && x >= 0 ? x : null);
  const st = (x) => (x === 'ok' || x === 'stopped' || x === 'error' ? x : 'error');
  const characters = st(r.characters), presence = characters === 'ok' ? st(r.presence) : characters;
  return { version: v, query: q, asOf: typeof r.asOf === 'string' ? r.asOf : '', total: n(r.total), matched: n(r.matched),
    withCharacter: characters === 'ok' ? n(r.withCharacter) : null, online: presence === 'ok' ? n(r.online) : null,
    characters, presence, reason: typeof r.reason === 'string' ? r.reason : '', next: n(r.next) || null };
}
// An agent without the route: the backend's "the agent must be updated" answer, as a flag of its reply or as the text
// of its error.
const acctsUnsupported = (r, e) => (!!r && (r.unsupported === true || r.tooOld === true))
  || (!!e && String(e.message || '') === t('core.agent.accountsUnsupported'));

// GET /server/accounts of the version on screen: the first page (with the search typed), or the next one (more: the
// held answer's `next`, its search). The last request wins, and only under the target it was sent to and for the
// card state it was sent for — the rule of loadHotpatch (srvRetarget and the other resets replace S.accts; the counter
// is module-wide, so a new S.accts never re-issues a number an older request holds).
let acctsSeq = 0;
const ACCTS_PAGE = 100;
// A search as the agent takes it: one line of plain text, trimmed — a control character or a line break pasted into the
// field (which the agent refuses) is left out.
const acctsQuery = (q) => String(q || '').replace(/[\u0000-\u001f\u007f-\u009f\u2028\u2029]/g, '').trim();
async function loadAccts(more) {
  if (!isAdminMode() || !S.serverConfigured) return;
  const a = S.accts, v = acctsCur();
  if (!v) return;
  const held = a.data && a.data.version === v ? a.data : null;
  if (more && !(held && held.next)) return;
  const q = more ? held.query : acctsQuery(a.q);
  const seq = ++acctsSeq, target = srvTarget(), up = acctsUp(v), during = versionJobRunning(v);
  const mine = () => S.accts === a && a.seq === seq && srvTarget() === target;
  a.seq = seq;
  if (more) a.more = true; else a.loading = true;
  a.moreError = '';
  acctsRedraw();
  let r = null, err = null;
  try { r = await rpc('server.accounts', { version: v, after: more ? held.next : 0, limit: ACCTS_PAGE, q }); } catch (e) { err = e; }
  if (!mine()) return;
  a.loading = false; a.more = false;
  // (an agent that has no such route now — one put back after an answer of this session — closes the gate again)
  if (acctsUnsupported(r, err)) {
    Object.assign(a, { unsupported: true, supported: false, data: null, rows: [], error: '', errorVersion: '', errorQuery: '', menu: '', pick: null, panel: null });
    acctsRedraw();
    return;
  }
  if (!err && !(r && typeof r === 'object' && Array.isArray(r.accounts) && (r.version == null || String(r.version) === v))) err = new Error(t('accts.err.badAnswer'));
  if (err) {
    const why = err.message || String(err);
    if (more) a.moreError = why;
    else Object.assign(a, { error: why, errorVersion: v, errorQuery: q, data: null, rows: [], up, old: false, menu: '' });
    acctsRedraw();
    return;
  }
  const rows = r.accounts.map(acctsRowOf).filter(Boolean);
  // (a first page of another version than the one held: a row's open actions were another account's — the keys are sdk
  // uids, the same on every version)
  if (more) { const seen = new Set(a.rows.map((x) => x.key)); a.rows = a.rows.concat(rows.filter((x) => !seen.has(x.key))); }
  else Object.assign(a, { rows, up, old: during || versionJobRunning(v), menu: held ? a.menu : '' });
  Object.assign(a, { data: acctsData(r, v, q), supported: true, unsupported: false, error: '', errorVersion: '' });
  a.errorQuery = '';
  if (a.menu && !acctsRow(a.menu)) a.menu = '';
  acctsRedraw();
  if (!more) acctNewRescroll();
}
// A status read has just landed, or the tab opened: the version on screen is read when nothing is held for it, again
// when its stack went up or down since the read, and again when a job of it is over since — not while one runs (the
// agent then answers without the game's half), nor while a next page is read (the status read after it looks again). A
// read that failed is read again on those changes alone, and an agent without the route is not asked again. A job the
// status named, and names no more, makes what is held old whatever is on screen; a job this launcher follows does it
// at its own end (acctsAfterJob).
function acctsOpen() {
  const a = S.accts, info = S.srv.info;
  if (info && !info.public) {
    const b = info.busy;
    const seen = b && b.version != null && !(jobHere() && String(S.srvJob.version) === String(b.version)) ? String(b.version) : '';
    if (a.busyOf && a.busyOf !== seen) { if ((a.data && a.data.version === a.busyOf) || a.errorVersion === a.busyOf) a.old = true; a.busyOf = ''; }
    if (seen) a.busyOf = seen;
  }
  if (S.screen !== 'server' || adminTabOn() !== 'players' || !isAdminMode() || !S.serverConfigured || (info && info.public)) return;
  const v = acctsCur();
  if (!v || a.loading || a.more || a.unsupported) return;
  const held = !!a.data && a.data.version === v, failed = !held && a.errorVersion === v;
  if ((held || failed) && versionJobRunning(v)) return;
  if ((held || failed) && !a.old && a.up === acctsUp(v)) return;
  loadAccts();
}
// A job of this server ended: what the card holds of its version is from before it — read again at the status read
// that follows when the card is on screen (acctsOpen), else when it opens. A copy's check, and a move's, changed nothing.
function acctsAfterJob(j) {
  const a = S.accts, v = String(j.version == null ? '' : j.version);
  if (!v || ((j.kind === 'accountcopy' || j.kind === 'migrate') && j.dryRun)) return;
  if ((a.data && a.data.version === v) || a.errorVersion === v) a.old = true;
}

// How long before the answer's own moment (asOf, the server's clock) a time of the answer was: "just now", minutes,
// hours, days, or the date beyond two months ('' = no such time).
function acctsAgo(iso, asOf) {
  const at = Date.parse(iso || ''), now = Date.parse(asOf || '');
  if (isNaN(at)) return '';
  const date = () => { try { return new Date(at).toLocaleDateString(I18N.code); } catch (e) { return new Date(at).toDateString(); } };
  if (isNaN(now)) return t('accts.ago.date', { date: date() });
  const s = Math.max(0, Math.floor((now - at) / 1000));
  if (s < 60) return t('accts.ago.now');
  if (s < 3600) return t('accts.ago.min', { n: Math.floor(s / 60) });
  if (s < 86400) return t('accts.ago.hour', { n: Math.floor(s / 3600) });
  if (s < 86400 * 60) return t('accts.ago.day', { n: Math.floor(s / 86400) });
  return t('accts.ago.date', { date: date() });
}
// The answer's moment (asOf) as this PC writes a time: the hour, and the day too when it is not today.
function acctsAsOf(iso) {
  const at = Date.parse(iso || '');
  if (isNaN(at)) return '';
  const d = new Date(at), today = d.toDateString() === new Date().toDateString();
  try { return today ? d.toLocaleTimeString(I18N.code, { hour: '2-digit', minute: '2-digit' }) : d.toLocaleString(I18N.code, { dateStyle: 'medium', timeStyle: 'short' }); }
  catch (e) { return today ? d.toTimeString().slice(0, 5) : d.toString(); }
}
// A row's last time in the game: its last save, else its last login to the sdk, else never.
function acctsLast(r, asOf) {
  if (r.lastSave) return acctsAgo(r.lastSave, asOf);
  if (r.lastLogin) return t('accts.row.loggedIn', { ago: acctsAgo(r.lastLogin, asOf) });
  return t('accts.row.never');
}
// An account as a sentence names it: its login in quotes, or the guest login with its sdk uid; a name or uid typed
// by hand ({text}) as it was typed.
function acctsName(s) {
  if (!s) return '';
  if (typeof s.text === 'string') return t('accts.who.login', { name: s.text });
  return s.name ? t('accts.who.login', { name: s.name }) : t('accts.who.guest', { sdk: s.sdkUid });
}
// The account a copy's side is: a row of the list as it is, a name or uid typed by hand as the row of the uid the
// copy's check found for it when the list of that version holds that row (else as typed — acctsLookup reads it).
function acctsSide(s, uid, version) {
  const a = S.accts;
  if (!s || typeof s.text !== 'string' || uid == null || !a.data || a.data.version !== version) return s;
  return a.rows.find((r) => r.uid === uid) || s;
}
// An account with what tells it apart: its character's nickname and adventure rank and its game uid, or that it has no
// character or has not played (acctsSide: a typed one with the uid the check found for it). A side typed by hand that
// no row was found for is named by that uid alone: nothing is known of its character.
function acctsWho(s, uid, version) {
  if (!s) return '';
  const row = acctsSide(s, uid, version), name = acctsName(row);
  if (typeof row.text === 'string') return uid != null ? t('accts.who.typed', { name, uid }) : name;
  const u = row.uid != null ? row.uid : uid;
  if (row.level != null) return t('accts.who.char', { name, nick: row.nickname || '—', level: row.level, uid: u });
  if (u != null) return t('accts.who.uid', { name, uid: u });
  return t('accts.who.none', { name });
}
// The presence dot of a row: online, online by the server's records while its last save is old (advisory: the session
// may have ended without a logout), not online, or not known (the presence could not be read).
function acctsDot(r, pres, asOf) {
  const dot = (cls, title) => `<span class="accdot ${cls}" role="img" aria-label="${title}" title="${title}"></span>`;
  if (!pres || r.online === null) return dot('unk', T('accts.dot.unknown'));
  if (r.online && r.presenceStale) return dot('stale', T('accts.dot.stale', { ago: acctsAgo(r.lastSave, asOf) }));
  return r.online ? dot('on', T('accts.dot.online')) : dot('off', T('accts.dot.offline'));
}
// Where an account most likely comes from (the agent's `origin`): a hint for the eye, never a reason to allow or refuse
// anything.
function acctsOrigin(o) {
  const tag = (label, title, cls) => `<span class="accorig${cls ? ' ' + cls : ''}" title="${title}">${label}</span>`;
  if (o === 'default') return tag(T('accts.origin.default'), T('accts.origin.defaultTitle'));
  if (o === 'package') return tag(T('accts.origin.package'), T('accts.origin.packageTitle'), 'kind');
  if (o === 'signup') return tag(T('accts.origin.signup'), T('accts.origin.signupTitle'));
  if (o === 'relic') return tag(T('accts.origin.relic'), T('accts.origin.relicTitle'));
  if (o === 'other') return tag(T('accts.origin.other'), T('accts.origin.otherTitle'), 'kind');
  return '';
}

// The card: the version chips (the running one first), then what the answer of the version on screen says — its one
// fact line, what the server could not read, the search, the copy's question for its target, the rows and the way to
// the next page.
function acctsCard() {
  // The public snapshot as the last status: not an admin's word, nothing to read from it (acctNewCard's rule).
  if (S.srv.info && S.srv.info.public) return '';
  const a = S.accts, v = S.serverConfigured ? acctsCur() : '';
  const refresh = v ? `<button class="btn ghost" data-act="acctsRefresh" ${a.loading ? 'disabled' : ''}>${T('accts.refresh')}</button>` : '';
  const head = `<div class="acchead"><h4>${T('accts.title')}</h4>${refresh}</div><p class="hint">${T('accts.hint')}</p>`;
  if (!S.serverConfigured) return `<div class="sect">${head}${noServerLine()}</div>`;
  if (!v) {
    const why = S.srv.info ? `<div class="note">${T('accts.noVersion')}</div>` : S.srv.state === 'error' ? '' : `<p class="hint" style="margin:0">${T('common.checking')}</p>`;
    return `<div class="sect">${head}${why}</div>`;
  }
  const ids = acctsVersions();
  const chips = ids.length ? `<div class="accchips">${ids.map((id) => `<button class="lg-chip ${id === v ? 'on' : ''}" data-act="acctsVersion" data-arg="${esc(id)}">${acctsUp(id) ? `<span class="accup" title="${T('accts.chipRunning')}"></span>` : ''}${T('common.versionN', { id })}</button>`).join('')}</div>` : '';
  const d = a.data && a.data.version === v ? a.data : null;
  // A read that failed with a search keeps the search above its reason: the search the agent refused can be changed or
  // cleared there.
  const failed = () => (a.errorQuery ? acctsFindHtml(true) : '') + `<div class="note"${a.errorQuery ? ' style="margin-top:12px"' : ''}>${T('accts.err.read', { error: a.error })}</div>`;
  const body = a.unsupported ? `<div class="note">${T('core.agent.accountsUnsupported')}</div>`
    : d ? acctsBody(v, d)
      : !a.loading && a.errorVersion === v ? failed()
        : `<p class="hint" style="margin:0">${T('accts.reading')}</p>`;
  return `<div class="sect">${head}${chips}${body}</div>`;
}
// The search: its field (Enter = Search), Search, and Show all while a search is on screen (shown).
function acctsFindHtml(shown) {
  const a = S.accts, off = a.loading ? 'disabled' : '';
  return `<div class="accfind"><input class="txt" data-model="accts.q" data-enter="acctsSearch" value="${esc(a.q)}" placeholder="${T('accts.searchPh')}" maxlength="64" spellcheck="false" autocomplete="off">
      <button class="btn ghost" data-act="acctsSearch" ${off}>${T('accts.search')}</button>${shown ? `<button class="btn ghost" data-act="acctsClear" ${off}>${T('accts.clear')}</button>` : ''}</div>`;
}
function acctsBody(v, d) {
  const a = S.accts, ctx = { v, d, chars: d.characters === 'ok', pres: d.presence === 'ok', live: acctsUp(v), busy: srvBusy() };
  const fact = (label, value) => `<span><small>${label}</small><b>${value}</b></span>`;
  const asOf = acctsAsOf(d.asOf);
  const facts = (d.total !== null ? fact(T('accts.fact.accounts'), esc(d.total)) : '')
    + (ctx.chars && d.withCharacter !== null ? fact(T('accts.fact.withCharacter'), esc(d.withCharacter)) : '')
    + (ctx.pres && d.online !== null ? fact(T('accts.fact.online'), esc(d.online)) : '')
    + (asOf ? fact(T('accts.fact.asOf'), T('accts.fact.asOfValue', { time: asOf })) : '')
    + (a.loading ? `<span class="accbusy">${T('accts.reading')}</span>` : '');
  // What the server could not say: the stack is stopped (the logins alone — of the package, while the version is not
  // prepared yet), or the game's half failed — its reason is the agent's own sentence, drawn as it is.
  const f = srvFacts(v);
  const say = d.characters === 'stopped' ? (f && !f.bootstrapped ? T('accts.notPrepared', { version: v }) : T('accts.stopped', { version: v }))
    : d.characters === 'error' ? (d.reason ? T('accts.charactersError', { reason: d.reason }) : T('accts.charactersUnknown'))
      : d.presence === 'error' ? (d.reason ? T('accts.presenceError', { reason: d.reason }) : T('accts.presenceUnknown')) : '';
  const find = d.total !== 0 || d.query ? `${acctsFindHtml(!!d.query)}
    ${d.query ? `<p class="accfound">${T('accts.found', { q: d.query, matched: d.matched === null ? a.rows.length : d.matched, total: d.total === null ? '?' : d.total })}</p>` : ''}` : '';
  const pick = a.pick && a.pick.version === v ? acctsPickBox(ctx) : '';
  // An editor whose row this page does not hold (the account a copy just created, the search moved on) sits above the rows.
  const lone = !pick && a.panel && a.panel.version === v && !a.rows.some((r) => r.key === a.panel.key) ? acctsPanel(a.panel, ctx) : '';
  const list = a.rows.length
    ? `<div class="acclist"><div class="acchd"><span></span><span>${T('accts.col.account')}</span><span>${T('accts.col.uid')}</span><span>${T('accts.col.nickname')}</span><span>${T('accts.col.level')}</span><span>${T('accts.col.last')}</span><span></span></div>${a.rows.map((r) => acctsRowHtml(r, ctx)).join('')}</div>`
    : `<p class="accnone">${d.query ? T('accts.noneFound', { q: d.query }) : T('accts.none', { version: v })}</p>`;
  const count = d.query ? d.matched : d.total;
  const more = d.next ? `<div class="accmore"><span>${count !== null ? T('accts.shown', { shown: a.rows.length, count }) : ''}</span><button class="btn ghost" data-act="acctsMore" ${a.more || a.loading ? 'disabled' : ''}>${a.more ? T('accts.reading') : T('accts.more')}</button></div>` : '';
  const moreErr = a.moreError ? `<div class="note" style="margin-top:10px">${T('accts.err.more', { error: a.moreError })}</div>` : '';
  return `${facts ? `<div class="accfacts">${facts}</div>` : ''}${say ? `<div class="note">${say}</div>` : ''}${find}${pick}${lone}${list}${more}${moreErr}`;
}
// One account: its presence, its login (with Copy) over its sdk uid and origin, its game uid, nickname, adventure rank
// and last time in the game, and at its end either its actions (while the version runs) or, while a copy waits for its
// target, "Copy here". Its actions or its open editor are drawn under it.
function acctsRowHtml(r, ctx) {
  const a = S.accts, pick = a.pick && a.pick.version === ctx.v ? a.pick : null;
  const dim = (html) => `<span class="accdim">${html}</span>`;
  const name = r.name
    ? `<b class="mono pick">${esc(r.name)}</b><button class="acccopy" data-act="copyAccount" data-arg="${esc(r.name)}" title="${T('accts.copyNameTitle')}">${T('common.copy')}</button>`
    : `<b class="accguest">${T('accts.guest')}</b>`;
  const char = r.level !== null;
  const cells = acctsDot(r, ctx.pres, ctx.d.asOf)
    + `<div class="accname"><div class="accnm">${name}</div><div class="accsub"><small class="mono">${T('accts.row.sdk', { sdk: r.sdkUid })}</small>${acctsOrigin(r.origin)}</div></div>`
    + (r.uid !== null ? `<span class="mono pick">${esc(r.uid)}</span>` : dim('—'))
    + (char ? (r.nickname ? `<span class="accnick pick">${esc(r.nickname)}</span>` : dim('—')) : dim(ctx.chars ? T('accts.row.noCharacter') : '—'))
    + (char ? `<b>${esc(r.level)}</b>` : dim('—'))
    + `<span class="acclast">${esc(acctsLast(r, ctx.d.asOf))}</span>`;
  const src = !!pick && pick.from.key === r.key;
  const panel = !pick && a.panel && a.panel.version === ctx.v && a.panel.key === r.key;
  const end = pick ? (src ? `<span class="accorig src">${T('accts.pick.source')}</span>`
    : acctsTargetOk(r, ctx) ? `<button class="btn ghost" data-act="acctsCopyHere" data-arg="${esc(r.key)}" ${ctx.busy ? 'disabled' : ''}>${T('accts.pick.here')}</button>` : '')
    : ctx.live || panel ? `<button class="btn ghost" data-act="acctsMenu" data-arg="${esc(r.key)}">${a.menu === r.key || panel ? T('accts.actionsClose') : T('accts.actions')}</button>` : '';
  const under = pick ? '' : panel ? acctsPanel(a.panel, ctx) : a.menu === r.key && ctx.live ? acctsActs(r, ctx) : '';
  return `<div class="accrow${under ? ' on' : ''}${src ? ' src' : ''}" data-acc="${esc(r.key)}">${cells}<div class="accend">${end}</div></div>${under}`;
}
// A row that can take the progress a copy waits to give: another account, one the copy can name (a game uid, or a
// login when it has not played yet), of a version that runs, and not in the game right now.
function acctsTargetOk(r, ctx) {
  const p = S.accts.pick;
  return !!p && r.key !== p.from.key && (r.uid !== null || !!r.name) && ctx.live && ctx.chars && !(ctx.pres && r.online === true);
}
// A row's actions: Copy progress to… (an account with a character), Give a starting progress… (a template imported
// on the stack), Set password… (an account with a login, whatever the server verifies), Game commands and Copy uid
// (an account with a game uid). The copy and the starting progress need the version's characters read and the player
// out of the game; while another operation runs, nothing starts. A line says why one is not offered.
function acctsActs(r, ctx) {
  const v = ctx.v, online = ctx.pres && r.online === true, tpls = acctsTemplates(v), ready = ctx.live && ctx.chars && !ctx.busy;
  const copyOk = ready && !online && r.uid !== null && r.level !== null;
  const tplOk = ready && !online && (r.uid !== null || !!r.name) && tpls.length > 0;
  const pwOk = ctx.live && !ctx.busy && !!r.name;
  const btn = (act, label, ok, title) => `<button class="btn ghost" data-act="${act}" data-arg="${esc(r.key)}" ${ok ? '' : 'disabled'}${title ? ` title="${title}"` : ''}>${label}</button>`;
  const why = [];
  if (ctx.busy) why.push(T('accts.why.busy'));
  else if (!ctx.chars) why.push(T('accts.why.characters'));
  // (a presence the server's records keep from a session that may have ended without a logout says so, and how such a
  // record clears)
  else if (online) why.push(r.presenceStale && r.lastSave ? T('accts.why.onlineStale', { ago: acctsAgo(r.lastSave, ctx.d.asOf) }) : T('accts.why.online'));
  else {
    if (r.level === null) why.push(T('accts.why.noCharacter'));
    if (!tpls.length) why.push(acctsShipsNoTemplate(v) ? T('accts.why.noTemplatesShipped', { version: v }) : T('accts.why.noTemplates', { version: v }));
  }
  return `<div class="accacts">${btn('acctsCopyFrom', T('accts.act.copy'), copyOk)}${btn('acctsTemplate', T('accts.act.template'), tplOk)}${r.name ? btn('acctsPassword', T('accts.act.password'), pwOk) : ''}${r.uid !== null
    ? btn('acctsCommands', T('accts.act.commands'), true, T('accts.act.commandsTitle', { version: v })) + btn('acctsCopyUid', T('accts.act.copyUid'), true) : ''}${why.length ? `<div class="accwhy">${why.map((w) => `<small>${w}</small>`).join('')}</div>` : ''}</div>`;
}
const acctsPanel = (p, ctx) => (p.kind === 'template' ? acctsTplPanel(p, ctx) : p.kind === 'password' ? acctsPwPanel(p, ctx) : '');
// "Give a starting progress…": the templates imported on the stack (the card's own choice kept valid), what the copy
// does to the account, and the check that runs first.
function acctsTplPanel(p, ctx) {
  const tpls = acctsTemplates(ctx.v);
  if (!tpls.some((x) => x.id === p.tpl)) p.tpl = tpls.length ? tpls[0].id : '';
  const cards = tpls.map((tp) => {
    const sub = 'account.tpl.' + tp.id + '.sub';
    return `<div class="opt ${p.tpl === tp.id ? 'sel' : ''}" data-act="acctsTpl" data-arg="${esc(tp.id)}"><div class="h">${esc(tp.label || tp.id)}</div>${I18N.has(sub) ? `<p>${T(sub)}</p>` : ''}</div>`;
  }).join('');
  const ok = ctx.live && ctx.chars && !ctx.busy && !!p.tpl;
  return `<div class="accpanel"><b>${T('accts.tpl.title', { who: acctsWho(p.row, null, ctx.v) })}</b><p class="hint">${T('accts.tpl.body')}</p>
    ${tpls.length ? `<div class="grid2">${cards}</div>` : `<div class="note">${acctsShipsNoTemplate(ctx.v) ? T('accts.why.noTemplatesShipped', { version: ctx.v }) : T('accts.tpl.none', { version: ctx.v })}</div>`}
    <div class="flex gap accpanel-go"><button class="btn" data-act="acctsTplGo" ${ok ? '' : 'disabled'}>${T('accts.tpl.go')}</button><button class="btn ghost" data-act="acctsPanelClose">${T('common.cancel')}</button></div></div>`;
}
// "Set password…": one generated (16 random characters, shown once when the job is done) or one typed; what it means on
// a server that verifies passwords, and on one that does not yet. The typed value lives in the field and the editor
// until it is sent, never longer.
function acctsPwPanel(p, ctx) {
  const ok = ctx.live && !ctx.busy;
  const seg = `<div class="lg-tabs accseg"><button class="${p.gen ? 'on' : ''}" data-act="acctsPwMode" data-arg="gen">${T('accts.pw.generate')}</button><button class="${p.gen ? '' : 'on'}" data-act="acctsPwMode" data-arg="type">${T('accts.pw.type')}</button></div>`;
  const field = p.gen ? `<p class="hint accpw-sub">${T('accts.pw.generateSub')}</p>`
    : `<label class="fld">${T('account.password')}</label><input class="txt mono" type="password" data-model="accts.panel.pw" data-enter="acctsPwSet" value="${esc(p.pw)}" placeholder="${T('account.passwordPh')}" maxlength="64" autocomplete="new-password">`;
  return `<div class="accpanel">${p.needed ? `<div class="note accpanel-note">${T('accts.pw.needed', { name: p.row.name })}</div>` : ''}<b>${T('accts.pw.title', { name: p.row.name })}</b>
    <p class="hint">${passwordVerified(ctx.v) ? T('accts.pw.verifyOn') : T('accts.pw.verifyOff')}</p>${seg}${field}
    <div class="flex gap accpanel-go"><button class="btn" data-act="acctsPwSet" ${ok ? '' : 'disabled'}>${T('accts.pw.go')}</button><button class="btn ghost" data-act="acctsPanelClose">${T('common.cancel')}</button></div></div>`;
}
// "Copy progress to…" waiting for its target: the account it copies from, "Copy here" on the rows, or a new account
// by name — a check runs first, its question comes before anything is copied.
function acctsPickBox(ctx) {
  const p = S.accts.pick;
  return `<div class="accpick"><div class="accpick-say"><b>${T('accts.pick.title', { who: acctsWho(p.from, null, ctx.v) })}</b><p>${T('accts.pick.body')}</p>
      <label class="fld">${T('accts.pick.newLabel')}</label><div class="accpick-new"><input class="txt mono" data-model="accts.pick.name" data-enter="acctsPickNew" value="${esc(p.name)}" placeholder="${T('accts.pick.newPh')}" maxlength="20" spellcheck="false" autocomplete="off"><button class="btn ghost" data-act="acctsPickNew" ${ctx.busy ? 'disabled' : ''}>${T('accts.pick.newGo')}</button></div></div>
    <button class="btn ghost accpick-x" data-act="acctsPickCancel">${T('common.cancel')}</button></div>`;
}

// The check of a copy as a job (dryRun: the agent resolves both sides and runs every refusal, and creates and allocates
// nothing); its end asks the question with what it found (acctsCopyDone), and that question's Yes starts the copy
// itself (acctsCopyGo). form = the "Copy by name or uid" form started it: it closes once the copy itself starts. asNew
// = the target is the name typed for a new account (to.asNew): a check that finds an account of that name says so.
function acctsCopyStart(kind, version, body, from, to, tpl, form) {
  if (srvBusy()) return showToast(t('toast.jobInProgress'), 'bad');
  const a = S.accts;
  Object.assign(a, { pick: null, panel: null, menu: '' });
  const job = acctsJob('accountcopy', version, Object.assign({}, body, { dryRun: true }));
  a.flow = job ? { kind, version, body, from, to, tpl, form: !!form, asNew: !!(to && to.asNew), step: 'dry', job, res: null } : null;
}
// A job the card starts: the job object the page then follows, or null when none started.
function acctsJob(kind, version, extra) {
  const before = S.srvJob;
  runServerJob(kind, version, extra);
  return S.srvJob !== before && S.srvJob && S.srvJob.running ? S.srvJob : null;
}
const acctsTplLabel = (f) => (f.tpl && (f.tpl.label || f.tpl.id)) || '';
// The question after a copy's check, worded from what the check found: both accounts with their characters (the
// target's nickname and adventure rank), that what the target has is replaced and kept as a backup, a target the copy
// creates or gives a game uid — one that has nothing yet is named by its login, and nothing of it is replaced —, a
// name typed for a new account that is an account already, and a password the new account will need.
function acctsQuestion(f) {
  const r = f.res || {}, toName = acctsName(acctsSide(f.to, r.dstUid, f.version));
  const fresh = r.wouldCreate || r.wouldAllocate;
  const to = fresh ? toName : acctsWho(f.to, r.dstUid, f.version);
  const more = (f.asNew && !r.wouldCreate ? ' ' + t('accts.confirm.exists', { to: toName }) : '')
    + (r.wouldCreate ? ' ' + t('accts.confirm.created', { to: toName }) : r.wouldAllocate ? ' ' + t('accts.confirm.allocated', { to: toName }) : '')
    + (r.passwordNeeded ? ' ' + t('accts.confirm.pwNeeded') : '');
  const ask = { act: 'acctsCopyGo', arg: '', noAct: 'acctsFlowCancel', at: Date.now() };
  if (f.kind === 'template') {
    const template = acctsTplLabel(f);
    return Object.assign(ask, { title: t('accts.confirm.tpl.title', { to: toName, template }),
      body: (fresh ? t('accts.confirm.tpl.bodyNew', { to, template }) : t('accts.confirm.tpl.body', { to, template })) + more, yes: t('accts.confirm.tpl.yes') });
  }
  const from = acctsWho(f.from, r.srcUid, f.version);
  return Object.assign(ask, { title: t('accts.confirm.copy.title', { from: acctsName(acctsSide(f.from, r.srcUid, f.version)), to: toName }),
    body: (fresh ? t('accts.confirm.copy.bodyNew', { from, to }) : t('accts.confirm.copy.body', { from, to })) + more, yes: t('accts.confirm.copy.yes') });
}
// The sides of a copy typed by hand ("Copy by name or uid", a new account's name) whose row the card does not hold —
// another version on screen, a search, a page not read yet: read by the name typed (the list's search), the row of the
// game uid the check found is that side's account, with its character. A side no row is found for, and a read that
// fails or takes longer than ACCTS_LOOKUP_MS in all, stay as typed (acctsWho names them by their uid alone).
const ACCTS_LOOKUP_MS = 8000;
async function acctsLookup(f) {
  const end = Date.now() + ACCTS_LOOKUP_MS;
  for (const side of ['from', 'to']) {
    const s = f[side], uid = side === 'from' ? f.res.srcUid : f.res.dstUid, q = s && typeof s.text === 'string' ? acctsQuery(s.text) : '';
    if (!q || q.length > 64 || uid == null || acctsSide(s, uid, f.version) !== s) continue;
    const left = end - Date.now();
    if (left <= 0) return;
    let r = null;
    try { r = await Promise.race([rpc('server.accounts', { version: f.version, after: 0, limit: ACCTS_PAGE, q }), new Promise((go) => setTimeout(() => go(null), left))]); }
    catch (e) { r = null; }
    if (S.accts.flow !== f || f.step !== 'look') return;
    const rows = r && typeof r === 'object' && Array.isArray(r.accounts) && (r.version == null || String(r.version) === f.version) ? r.accounts.map(acctsRowOf).filter(Boolean) : [];
    const row = rows.find((x) => x.uid === uid);
    if (row) f[side] = acctsSnap(row);
  }
}
// A copy this card follows ended well. Its check opens the question (once the sides typed by hand are read:
// acctsLookup); the copy itself says so, and an account it created on a server that verifies passwords is offered its
// password at once. Neither question covers one already open. An agent that answered the check with a real copy (no
// `dryRun` in its result) did the copy.
function acctsCopyDone(j, res) {
  const a = S.accts, f = a.flow, r = res || {};
  if (f.step === 'dry') {
    if (r.dryRun !== true) { a.flow = null; showToast(t('toast.jobDone', { job: jobTitleOf(j), version: j.version }), 'ok'); return; }
    const n = (x) => (Number.isInteger(x) ? x : null);
    f.res = { srcUid: n(r.srcUid), dstUid: n(r.dstUid), wouldCreate: r.wouldCreate === true, wouldAllocate: r.wouldAllocate === true, passwordNeeded: r.passwordNeeded === true };
    f.step = 'look';
    acctsLookup(f).catch(() => {}).then(() => {
      if (S.accts.flow !== f || f.step !== 'look') return;
      // A question opened meanwhile — the admin's own, anywhere in the launcher — is never covered by this one: the copy
      // is not asked, and the toast says that nothing was copied.
      if (S.confirm) { S.accts.flow = null; showToast(t('toast.acctsNotAsked'), 'bad'); return; }
      f.step = 'confirm';
      S.confirm = acctsQuestion(f);
      render();
    });
    return;
  }
  a.flow = null;
  const res0 = f.res || {}, to = acctsName(acctsSide(f.to, res0.dstUid, f.version));
  showToast(f.kind === 'template' ? t('toast.acctsTemplated', { to, template: acctsTplLabel(f), version: j.version })
    : t('toast.acctsCopied', { from: acctsName(acctsSide(f.from, res0.srcUid, f.version)), to, version: j.version }), 'ok');
  const name = f.to && (f.to.name || f.to.text);
  if (r.passwordNeeded === true && name) {
    // (a question open meanwhile is not covered: the toast then says what the new account still needs)
    if (S.confirm) return showToast(t('accts.pw.needed', { name }), 'bad');
    S.confirm = { title: t('accts.pwNeeded.title', { name }), body: t('accts.pw.needed', { name }), yes: t('accts.pwNeeded.yes'), danger: false,
      act: 'acctsPwOpenFor', arg: j.version + '|' + name, at: Date.now() };
    render();
  }
}
// The password job this card started: {job, target, host, version, name, password} — password only when it was
// generated here, shown once when the job is done (acctsPwDone). In memory only: never in S.srvJob, an event or a
// log line. Kept across a change of server, like the job itself: its end still has a password to show.
let acctsPwPending = null;
function acctsPwDone(j) {
  const p = acctsPwPending;
  acctsPwPending = null;
  if (!p.password) return showToast(t('toast.acctsPwSet', { name: p.name }), 'ok');
  const here = p.target === srvTarget();
  S.notice = { title: t('accts.pw.doneTitle', { name: p.name }), pick: p.password,
    body: here ? t('accts.pw.doneBody', { name: p.name, version: j.version }) : t('accts.pw.doneMovedBody', { name: p.name, version: j.version, host: p.host || '—' }) };
  render();
}
// A password for "Generate one": 16 letters and digits none of which is easily taken for another, from the system's
// random source.
function acctsNewPassword() {
  const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789';
  const buf = new Uint32Array(16);
  crypto.getRandomValues(buf);
  return Array.from(buf, (n) => chars[n % chars.length]).join('');
}
// loginCreate's way to "Create a player account", under the account list: .main scrolled to it once drawn, and once
// more when the list's first answer moved it down — only while nothing else has scrolled the page since.
let acctNewAt = null;
function acctNewScroll() {
  const m = document.querySelector('#app .main'), b = document.querySelector('#app [data-blk="acctnew"]');
  if (!m || !b) { acctNewAt = null; return; }
  m.scrollTop = Math.max(0, b.getBoundingClientRect().top - m.getBoundingClientRect().top + m.scrollTop - 12);
  acctNewAt = m.scrollTop;
}
function acctNewRescroll() {
  if (acctNewAt === null) return;
  const m = document.querySelector('#app .main');
  if (m && m.scrollTop === acctNewAt && S.screen === 'server' && adminTabOn() === 'players' && !typingNow()) acctNewScroll();
  acctNewAt = null;
}

// ── admin: "Bring progress from another version" (the Players tab, after "Copy by name or uid") ──
// Accounts move one at a time, with all they have, from a server of another version on the agent's machine — or from
// another server of this version, kept in a folder — into a version that keeps running (POST /server/migrate, the job
// `migrate`). The check (dryRun) reads the source and writes nothing to the target; the card draws what it found per
// account, the admin chooses, and the move itself runs only from its question's Yes, with those choices. The page never
// repeats a check by itself, and the agent judges every account again in the move.
// One shape for every reset (migrateReset): boot, another server (srvRetarget), an agent install (deploy.done), an agent
// update that replaced the agent (agentUpdateEnded). target = the chip picked (null = migrateNow decides); srcKind,
// srcVersion, dir = the source as chosen; keepPassword, keepUid = the options; check = the last check {job, at, req, res}
// (req = the body it went out with, res = its result, null while it runs); choice = {source login: {action: 'create' |
// 'skip' | 'rename' | 'overwrite', to}}, one per row of that result that can be chosen; open = {row: true}, the rows
// whose details are open; run = the last move {job, res} (res null while it runs). choice and open have no prototype: a
// login may be any text.
function migrateBlank() {
  return { target: null, srcKind: 'version', srcVersion: '', dir: '', keepPassword: true, keepUid: false, check: null,
    choice: Object.create(null), open: Object.create(null), run: null };
}
// The reset, with the question asked over what it drops.
function migrateReset() {
  S.migrate = migrateBlank();
  if (S.confirm && S.confirm.act === 'migrateRun') S.confirm = null;
}
// The versions a move into v can name as its source: the agent's list for v (migrateFrom, the sources whose save it
// reads) without v itself — a version moves from a folder only — and without a version the agent has no stack of.
function migrateSources(v) {
  const f = srvFacts(v);
  return (f ? f.migrateFrom : []).filter((id) => { const s = srvFacts(id); return id !== v && !!s && s.configured && s.present; });
}
// The versions a move can go into: on the server and prepared, the running one first, with a source to offer — a version
// of its list, or a folder for a token. The public snapshot names none.
function migrateTargets() {
  if (!S.srv.info || S.srv.info.public) return [];
  return acctsVersions().filter((id) => {
    const f = srvFacts(id);
    return f.bootstrapped && f.migrateFrom.length > 0 && (!S.admin.openKey || migrateSources(id).length > 0);
  });
}
// What the card stands on: the target on screen and the ones on offer, the versions it can read from, whether a folder
// is offered (never to an open key: only the real token reads folders on the server's machine), and the source chosen.
function migrateNow() {
  const m = S.migrate, ids = migrateTargets();
  const v = ids.includes(m.target) ? m.target : ids[0] || '';
  const sources = v ? migrateSources(v) : [], folder = !S.admin.openKey;
  const kind = folder && (m.srcKind === 'folder' || !sources.length) ? 'folder' : 'version';
  return { ids, v, sources, folder, kind, src: sources.includes(m.srcVersion) ? m.srcVersion : sources[0] || '' };
}
// The check that answers what the card asks now: one that ended, for the same target, source and options. Another
// choice on the card keeps it, unused, until the next check.
function migrateHeld(n) {
  const m = S.migrate, c = m.check, q = c && c.req;
  if (!q || !c.res || !n.v || q.version !== n.v) return null;
  const same = n.kind === 'folder' ? q.source.dir === m.dir.trim() : q.source.version === n.src;
  return same && q.keepPassword === m.keepPassword && q.keepUid === m.keepUid ? c : null;
}
// A check needs a job slot, a running target and a source.
const migrateCanCheck = (n) => !!n.v && !srvBusy() && acctsUp(n.v) && (n.kind === 'folder' ? S.migrate.dir.trim().length > 0 : !!n.src);
// The folder typed: the check's button follows in place — no render.
function migrateSync() {
  const b = document.querySelector('[data-act="migrateCheck"]');
  if (b) b.disabled = !migrateCanCheck(migrateNow());
}
// The body of a request as the card stands: the target, the source, the options and the accounts chosen on the rows of
// its check, each by its exact login: a name the agent gave goes out as it is written there, only a name typed here is
// trimmed. None chosen — before a check too — names no accounts, which asks the agent for every account of the source.
// Pure: it reads `m` alone.
function migrateBody(m) {
  const source = m.srcKind === 'folder' ? { dir: String(m.dir || '').trim() } : { version: m.srcVersion };
  const body = { version: m.target, source, keepPassword: m.keepPassword === true, keepUid: m.keepUid === true };
  const accounts = [];
  for (const from of Object.keys(m.choice || {})) {
    const c = m.choice[from], to = String((c && c.to) || '');
    if (c && c.action === 'create') accounts.push(to && to !== from ? { from, to } : { from });
    else if (c && c.action === 'rename') accounts.push({ from, to: to.trim() });
    else if (c && c.action === 'overwrite') accounts.push({ from, to, overwrite: true });
  }
  if (accounts.length) body.accounts = accounts;
  return body;
}
// How many accounts one request names, and how many with a save one check of a whole source reads (the agent's limit).
const MIGRATE_ACCOUNTS_MAX = 100;
// What the page itself refuses of the accounts a request names: more than one request takes, a new name no signup could
// take (never digits alone), two accounts going to one name. A sentence, or ''.
function migrateNamesProblem(list) {
  if (list.length > MIGRATE_ACCOUNTS_MAX) return t('migrate.tooMany', { max: MIGRATE_ACCOUNTS_MAX });
  const seen = new Set();
  for (const a of list) {
    if (a.to !== undefined && !a.overwrite && (!ACCOUNT_NAME_RE.test(a.to) || /^\d+$/.test(a.to))) return t('toast.acctsNewName');
    const name = a.to === undefined ? a.from : a.to, key = name.toLowerCase();
    if (seen.has(key)) return t('migrate.sameName', { name });
    seen.add(key);
  }
  return '';
}
// The first choices of a result: what its check would do moves — a free name, an overwrite the request named —, every
// other row stays out until the admin says otherwise.
function migrateChoices(res) {
  const out = Object.create(null);
  for (const e of Array.isArray(res.accounts) ? res.accounts : []) {
    if (!e || typeof e.from !== 'string' || !e.from) continue;
    out[e.from] = e.verdict === 'create' || e.verdict === 'overwrite' ? { action: e.verdict, to: e.to || '' } : { action: 'skip', to: '' };
  }
  return out;
}
// The row of the held check that a control of the card names.
function migrateEntry(from) {
  const c = S.migrate.check, list = c && c.res && Array.isArray(c.res.accounts) ? c.res.accounts : [];
  return list.find((e) => e && e.from === from) || null;
}
// The rows of the held check chosen although the agent did not read them (past the accounts one check reads): they go
// into the next check, which reads what it names, and never straight into a move.
function migrateUnread(m) {
  const c = m.check, list = c && c.res && Array.isArray(c.res.accounts) ? c.res.accounts : [];
  return list.filter((e) => { const x = e && e.why === 'notChecked' ? m.choice[e.from] : null; return !!x && x.action !== 'skip'; });
}
// An id list of a result ({n, ids}: the agent names the first of a long one) as a sentence writes it.
function migrateIds(x) {
  const ids = x && Array.isArray(x.ids) ? x.ids : [], more = x && Number.isInteger(x.n) ? x.n - ids.length : 0;
  return more > 0 ? t('migrate.idsMore', { ids: ids.join(', '), more }) : ids.join(', ');
}
const migrateCategory = (c) => ({ avatar: t('migrate.cat.avatar'), skillDepot: t('migrate.cat.skillDepot'), material: t('migrate.cat.material'),
  weapon: t('migrate.cat.weapon'), reliquary: t('migrate.cat.reliquary'), furniture: t('migrate.cat.furniture'), reliquaryMainProp: t('migrate.cat.reliquaryMainProp'),
  reliquaryAffix: t('migrate.cat.reliquaryAffix'), scene: t('migrate.cat.scene') }[c] || c);
// One category of the ids a save names and the target lacks, as a sentence.
const migrateUnknown = (e, c, target) => t('migrate.d.unknownIds', { target, category: migrateCategory(c), ids: migrateIds(e.unknownIds[c]) });
// What happens to an account, as [the row's tone, the sentence]: the verdict of a check or of a move, and for a skip its
// code (the agent's stable word; a new one draws the agent's own sentence). A save refused for ids it names says so in
// the page's words; the agent's sentence names them by its own category codes.
function migrateVerdict(e, target) {
  const to = e.to || e.from || '—', reason = e.reason || '—';
  switch (e.verdict) {
    case 'create': return ['ok', t('migrate.v.create', { to })];
    case 'overwrite': return ['warn', t('migrate.v.overwrite', { to })];
    case 'moved': return ['ok', t('migrate.v.moved', { to, uid: e.dstUid == null ? '—' : e.dstUid })];
    case 'failed': return ['bad', t('migrate.v.failed', { reason })];
    case 'notDone': return ['', t('migrate.v.notDone')];
    default: break;
  }
  switch (e.why) {
    case 'guest': return ['', t('migrate.v.guest')];
    case 'noSave': return ['', t('migrate.v.noSave')];
    case 'deleted': return ['', t('migrate.v.deleted')];
    case 'nameTaken': return ['warn', e.caseOnly ? t('migrate.v.nameTakenCase', { existing: e.existing || to, target }) : t('migrate.v.nameTaken', { to, target })];
    case 'nameNotAllowed': return ['warn', t('migrate.v.nameNotAllowed', { to, reason })];
    case 'noTarget': return ['warn', t('migrate.v.noTarget', { to, target })];
    case 'targetOnline': return ['warn', t('migrate.v.targetOnline', { to, target })];
    case 'targetRecent': return ['warn', t('migrate.v.targetRecent', { to })];
    case 'batchDuplicate': return ['', t('migrate.v.batchDuplicate')];
    case 'notChecked': return ['', t('migrate.v.notChecked')];
    case 'notInSource': return ['', t('migrate.v.notInSource')];
    case 'clientUnknown': return ['bad', t('migrate.v.clientUnknown', { target })];
    case 'refused': return ['bad', t('migrate.v.refused', { reason: Object.keys(e.unknownIds || {}).map((c) => migrateUnknown(e, c, target)).join(' ') || reason })];
    case 'takenMeanwhile': return ['warn', t('migrate.v.takenMeanwhile', { to })];
    default: return ['', e.reason || String(e.why || '—')];
  }
}
// The details of a row, each a sentence ready for the page: what the move changes in the save, what the server itself
// discards at the first login, the uid, the password and what the admin still has to do.
function migrateDetails(e, target) {
  const out = [], any = (x) => !!x && Number.isInteger(x.n) && x.n > 0, ids = migrateIds;
  const removes = e.serverRemoves || {};
  if (e.existing) {
    out.push(e.existingLevel != null ? T('migrate.d.existing', { existing: e.existing, target, nick: e.existingNickname || '—', level: e.existingLevel })
      : T('migrate.d.existingEmpty', { existing: e.existing, target }));
  }
  if (e.relocated) out.push(T('migrate.d.relocated', { scene: e.relocated.from, target }));
  (Array.isArray(e.cut) ? e.cut : []).forEach((c) => out.push(T('migrate.d.cut', { activity: c.activity, ids: ids(c.conds), target })));
  if (any(e.settled)) out.push(T('migrate.d.settled', { ids: ids(e.settled), target }));
  if (any(e.dungeons)) out.push(T('migrate.d.dungeons', { ids: ids(e.dungeons), target }));
  if (e.dropped > 0) out.push(T('migrate.d.dropped', { n: e.dropped, target }));
  if (e.others > 0) out.push(T('migrate.d.others', { n: e.others }));
  if (removes.blocks > 0) out.push(T('migrate.d.serverBlocks', { n: removes.blocks }));
  if (removes.scenes > 0) out.push(T('migrate.d.serverScenes', { n: removes.scenes }));
  if (removes.marks > 0) out.push(T('migrate.d.serverMarks', { n: removes.marks }));
  if (any(e.expiring)) out.push(T('migrate.d.expiring', { n: e.expiring.n, ids: ids(e.expiring) }));
  if (any(e.expiryOther)) out.push(T('migrate.d.expiryOther', { n: e.expiryOther.n, ids: ids(e.expiryOther), target }));
  if (any(e.gachaUnknown)) out.push(T('migrate.d.gacha', { ids: ids(e.gachaUnknown), target }));
  const kept = Object.values(e.kept || {}).reduce((sum, n) => sum + (Number.isInteger(n) ? n : 0), 0);
  if (kept > 0) out.push(T('migrate.d.kept', { n: kept }));
  if (e.keepsUid === true) out.push(T('migrate.d.keepsUid', { uid: e.dstUid != null ? e.dstUid : e.wouldGetUid != null ? e.wouldGetUid : e.srcUid }));
  else if (e.keepsUid === false && !e.existing && (e.verdict === 'create' || e.verdict === 'moved')) out.push(T('migrate.d.uidTaken', { uid: e.srcUid, target }));
  if (e.password === 'copied') out.push(T('migrate.d.passwordCopied'));
  else if (e.password === 'notCopied' && e.passwordWhy) out.push(T('migrate.d.passwordNot', { reason: e.passwordWhy }));
  if (e.passwordNeeded === true) out.push(T('migrate.d.passwordNeeded', { target }));
  if (e.online === true) out.push(T('migrate.d.online'));
  Object.keys(e.unknownIds || {}).forEach((c) => out.push(esc(migrateUnknown(e, c, target))));
  return out;
}
// The choice of a row of the held check: Move for what the check would do; Check next for an account it did not read,
// off until the admin says otherwise — a whole source reads its first accounts again, so this is the way to the others;
// for a name that is taken, or one a signup could not take, a select — Skip first —, the new name's field and, for a
// taken name, Overwrite of the account it met, by that account's own login. Any other row has no choice.
function migrateControl(e, busy) {
  const c = S.migrate.choice[e.from], off = busy ? 'disabled' : '';
  if (!c) return '';
  const toggle = (label) => `<div class="migctl"><span>${label}</span><button class="toggle ${c.action === 'skip' ? '' : 'on'}" data-act="migrateMove" data-arg="${esc(e.from)}" ${off}><span class="knob"></span></button></div>`;
  if (e.why === 'notChecked') return toggle(T('migrate.choice.checkNext'));
  if (e.verdict === 'create' || e.verdict === 'overwrite') return toggle(T('migrate.choice.move'));
  if (e.why !== 'nameTaken' && e.why !== 'nameNotAllowed') return '';
  const opt = (value, label, cls) => `<option value="${value}"${cls ? ` class="${cls}"` : ''}${c.action === value ? ' selected' : ''}>${label}</option>`;
  const over = e.why === 'nameTaken' && e.existing ? opt('overwrite', T('migrate.choice.overwrite', { existing: e.existing }), 'danger') : '';
  const field = c.action !== 'rename' ? '' : `<input class="txt mono" data-migrate-to data-arg="${esc(e.from)}" data-enter="migrateGo" value="${esc(c.to)}" placeholder="${T('migrate.choice.renamePh')}" maxlength="20" spellcheck="false" autocomplete="off" ${off}>`;
  return `<div class="migctl"><select class="txt${c.action === 'overwrite' ? ' danger' : ''}" data-migrate-choice data-arg="${esc(e.from)}" ${off}>${opt('skip', T('migrate.choice.skip'))}${opt('rename', T('migrate.choice.rename'))}${over}</select>${field}</div>`;
}
// One account: its login and source uid (a row the agent marks a guest stays a guest, whatever name it carries), its
// character, what happens to it with its choice, and its details under it, opened by their button.
function migrateRow(e, i, target, held, busy) {
  const guest = e.why === 'guest' || !e.from, open = S.migrate.open[i] === true;
  const who = guest ? `<b class="accguest">${T('accts.guest')}</b>` : `<b class="mono pick">${esc(e.from)}</b>`;
  const uid = e.srcUid != null ? `<div class="accsub"><small class="mono">${T('migrate.row.uid', { uid: e.srcUid })}</small></div>` : '';
  const char = e.level != null ? `<span class="accnick pick">${T('migrate.row.char', { nick: e.nickname || '—', level: e.level })}</span>` : '<span class="accdim">—</span>';
  const [tone, text] = migrateVerdict(e, target), details = migrateDetails(e, target);
  const more = details.length ? `<button class="migmore" data-act="migrateDetail" data-arg="${i}">${open ? T('migrate.detailsClose') : T('migrate.details', { n: details.length })}</button>` : '';
  return `<div class="migrow"><div class="accname"><div class="accnm">${who}</div>${uid}</div><div>${char}</div>
    <div class="migwhat"><span class="migv ${tone}">${esc(text)}</span>${held && !guest ? migrateControl(e, busy) : ''}${more}</div></div>
    ${open && details.length ? `<ul class="migdet">${details.map((d) => `<li>${d}</li>`).join('')}</ul>` : ''}`;
}
function migrateRows(res, target, held, busy) {
  const list = Array.isArray(res.accounts) ? res.accounts : [];
  return `<div class="miglist"><div class="mighd"><span>${T('migrate.col.account')}</span><span>${T('migrate.col.character')}</span><span>${T('migrate.col.what')}</span></div>${list.map((e, i) => migrateRow(e, i, target, held, busy)).join('')}</div>`;
}
// A check's answer: the two servers' password check, the rows with their choices, what those choices come to and the
// way to the move: dead while a chosen row still waits for its check. The totals follow the choices: they are what the
// move would be asked.
function migrateChecked(c, n) {
  const m = S.migrate, res = c.res, verify = res.passwordVerify || {}, busy = srvBusy();
  const word = (x) => (x === true ? t('migrate.pv.on') : x === false ? t('migrate.pv.off') : t('migrate.pv.unknown'));
  const list = migrateBody(m).accounts || [], over = list.filter((a) => a.overwrite === true).length, unread = migrateUnread(m).length;
  const rows = Array.isArray(res.accounts) ? res.accounts.length : 0;
  const go = unread ? `<span class="migsay">${T('migrate.checkFirst')}</span>`
    : list.length ? `<button class="btn" data-act="migrateGo" ${busy || !acctsUp(n.v) ? 'disabled' : ''}>${T('migrate.go', { n: list.length })}</button>` : `<span class="migsay">${T('migrate.nothing')}</span>`;
  return `<div class="migres"><p class="migsay">${T('migrate.pv', { source: word(verify.source), target: n.v, targetState: word(verify.target) })}</p>
    ${migrateRows(res, n.v, true, busy)}
    <div class="miggo"><span class="migsum">${T('migrate.sum', { create: list.length - over - unread, overwrite: over, skip: Math.max(0, rows - list.length) })}</span>${go}</div></div>`;
}
// A move's answer: what it came to, what stopped it, the names left registered without progress, and the rows.
function migrateRan(r, target) {
  const tot = r.totals || {}, num = (x) => (Number.isInteger(x) ? x : 0);
  const counts = { moved: num(tot.moved), failed: num(tot.failed), notDone: num(tot.notDone), skip: num(tot.skip) };
  const left = Array.isArray(r.leftRegistered) ? r.leftRegistered.map((x) => x && x.name).filter(Boolean) : [];
  const failed = r.failed ? `<div class="note migstop">${T('toast.migrateFailed', { moved: num(tot.moved), name: r.failed.from || '—', reason: r.failed.reason || '—' })}</div>` : '';
  const named = left.length ? `<div class="note migstop">${T('toast.migrateLeftRegistered', { names: left.join(', ') })}</div>` : '';
  return `<div class="migres"><div class="migsum">${counts.skip ? T('migrate.sumDoneSkipped', counts) : T('migrate.sumDone', counts)}</div>${failed}${named}${migrateRows(r, target, false, false)}</div>`;
}
// The card: drawn only for a version the agent names sources for, with a source to offer. Into which version (the
// running one first), from what, the two options, the check, and what the last check or move answered. What the card
// asks stays as it is while a check or a move of its own runs: their answer is drawn under the choices that asked it.
function migrateCard() {
  const n = migrateNow(), v = n.v, m = S.migrate;
  if (!v) return '';
  const busy = srvBusy(), up = acctsUp(v), held = migrateHeld(n);
  const pending = m.check && !m.check.res ? T('migrate.checking') : m.run && !m.run.res ? T('migrate.moving') : '';
  const lock = busy && pending ? 'disabled' : '';
  const chip = (act, arg, on, label, live) => `<button class="lg-chip ${on ? 'on' : ''}" data-act="${act}" data-arg="${esc(arg)}" ${lock}>${live ? `<span class="accup" title="${T('accts.chipRunning')}"></span>` : ''}${label}</button>`;
  const targets = n.ids.map((id) => chip('migrateTarget', id, id === v, T('common.versionN', { id }), acctsUp(id))).join('');
  const sources = n.sources.map((id) => chip('migrateSource', id, n.kind === 'version' && id === n.src, T('migrate.fromVersion', { version: id }), false)).join('')
    + (n.folder ? chip('migrateFolder', '', n.kind === 'folder', T('migrate.fromFolder'), false) : '');
  const folder = n.kind === 'folder' ? `<label class="fld">${T('migrate.folderLabel')}</label><input class="txt mono migdir" data-model="migrate.dir" data-enter="migrateCheck" value="${esc(m.dir)}" placeholder="${T('migrate.folderPh')}" maxlength="4096" spellcheck="false" autocomplete="off" ${lock}><small class="migfold">${T('migrate.folderHint')}</small>`
    : n.folder ? '' : `<small class="migfold">${T('migrate.folderNeedsToken')}</small>`;
  const opt = (key, label, sub) => `<div class="row"><div><b>${label}</b><small>${sub}</small></div><button class="toggle ${m[key] ? 'on' : ''}" data-act="migrateOpt" data-arg="${key}" ${lock}><span class="knob"></span></button></div>`;
  const stopped = up ? '' : `<div class="note migstop">${srvDockerError() ? T('server.adv.stateUnknown') : T('migrate.targetStopped', { target: v })}</div>`;
  return `<div class="sect"><h4>${T('migrate.title')}</h4><p class="hint">${T('migrate.hint', { target: v })}</p>
    <label class="fld">${T('migrate.target')}</label><div class="accchips">${targets}</div>${stopped}
    <label class="fld">${T('migrate.from')}</label><div class="accchips">${sources}</div>${folder}
    <div class="grid2 migopts">${opt('keepPassword', T('migrate.keepPassword'), T('migrate.keepPasswordSub', { target: v }))}${opt('keepUid', T('migrate.keepUid'), T('migrate.keepUidSub', { target: v }))}</div>
    <div class="miggo"><button class="btn ghost" data-act="migrateCheck" ${migrateCanCheck(n) ? '' : 'disabled'}>${T('migrate.check')}</button>${pending ? `<span class="migsay">${pending}</span>` : ''}</div>
    ${busy && pending ? '' : held ? migrateChecked(held, n) : ''}${m.run && m.run.res && String(m.run.job.version) === v ? migrateRan(m.run.res, v) : ''}</div>`;
}
// The move's question: how many, from what, what is overwritten, and — when a chosen account has any — that the server
// discards at the first login what the target retires. Worded from the held check and the choices alone, so that
// its Yes can ask it again (ask).
function migrateQuestion(m) {
  const list = migrateBody(m).accounts || [], res = m.check.res, target = m.target, src = res.source || {};
  const over = list.filter((a) => a.overwrite === true).map((a) => a.to), picked = new Set(list.map((a) => a.from));
  const version = src.version || '—';
  const source = src.kind === 'folder' ? t('migrate.src.folder', { version }) : t('migrate.src.version', { version });
  const discards = (Array.isArray(res.accounts) ? res.accounts : []).some((e) => {
    const x = e.serverRemoves || {};
    return picked.has(e.from) && (x.blocks > 0 || x.scenes > 0 || x.marks > 0 || (!!e.expiring && e.expiring.n > 0));
  });
  return {
    title: t('migrate.confirm.title', { n: list.length, source, target }),
    body: t('migrate.confirm.body', { create: list.length - over.length, overwrite: over.length, target,
      names: over.length ? t('migrate.confirm.names', { names: over.join(', ') }) : '' }) + (discards ? ' ' + t('migrate.confirm.server', { target }) : ''),
    yes: t('migrate.confirm.yes'), danger: over.length > 0, act: 'migrateRun', arg: target, ask: 'migrateGo', at: Date.now(),
  };
}
// A move or a check of this card ended. The job's RESULT says which of the two it was: a console re-created from the
// events knows no dryRun. A check takes its place on the card with its first choices; a check that was stopped holds
// nothing. A move replaces the check it ran on — its accounts are the target's now —, its rows open where the admin has
// something left to do, and its toast says how it ended. A job of another server only says so.
function migrateDone(j, res) {
  const m = S.migrate, r = res || {}, here = j.target === srvTarget();
  if (r.dryRun === true) {
    const c = here ? m.check : null, open = !!c && !c.res;
    if (r.stopped === true) { if (open) m.check = null; return showToast(t('toast.migrateCheckStopped'), 'ok'); }
    if (open) Object.assign(m, { check: Object.assign(c, { at: Date.now(), res: r }), choice: migrateChoices(r), open: Object.create(null) });
    return showToast(t('toast.migrateChecked'), 'ok');
  }
  const tot = r.totals || {}, num = (x) => (Number.isInteger(x) ? x : 0), moved = num(tot.moved);
  if (here) {
    const open = Object.create(null);
    (Array.isArray(r.accounts) ? r.accounts : []).forEach((e, i) => { if (e && (e.online === true || e.passwordNeeded === true)) open[i] = true; });
    Object.assign(m, { run: { job: j, res: r }, check: null, choice: Object.create(null), open });
  }
  const left = Array.isArray(r.leftRegistered) ? r.leftRegistered.map((x) => x && x.name).filter(Boolean) : [];
  const more = left.length ? ' ' + t('toast.migrateLeftRegistered', { names: left.join(', ') }) : '';
  if (r.failed) return showToast(t('toast.migrateFailed', { moved, name: r.failed.from || '—', reason: r.failed.reason || '—' }) + more, 'bad');
  if (r.stopped === true) return showToast(t('toast.migrateStopped', { moved, left: num(tot.notDone) }) + more, left.length ? 'bad' : 'ok');
  if (!moved) return showToast(t('toast.migrateNone') + more, 'bad');
  return showToast(t('toast.migrateDone', { moved, target: j.version }) + more, left.length ? 'bad' : 'ok');
}
// A check or a move that ended without a result — refused before a job existed, or failed: a check that had no answer
// is no check; a move that fails wrote nothing, so the check it ran on stays.
function migrateFailed() {
  const m = S.migrate;
  if (m.check && !m.check.res) m.check = null;
  if (m.run && !m.run.res) m.run = null;
}

// The Players tab's ONE way to the Prepare chooser of an installed version (provisionServer → prepareDialog
// 'provision'; its choices and its default selection are the dialog's own): the progress the stack holds and the
// choice it was prepared with. Red only where the chooser can import the default save — a version whose payload ships
// no save is offered the two choices that touch no save, and a version without facts is not drawn red.
function progressCard() {
  const busy = srvBusy();
  const rows = S.versions.map((v) => {
    const f = srvFacts(v.id);
    const dis = busy || !f || !f.present || !f.bootstrapped ? 'disabled' : '';
    const state = rowState(v.id, f) || (f.provisioned === null ? `${T('server.adv.progress')} ${T('server.adv.unknown')}`
      : `${T('server.adv.progress')} ${f.provisioned ? T('server.adv.applied') + (f.provisionedAt ? ` (${esc(f.provisionedAt)})` : '') : T('server.adv.notApplied')}`);
    const modeWord = f && f.present && f.bootstrapped ? t(PROGRESS_MODE[f.progress || 'none']) : '';
    return `<div class="line"><div><b>${T('common.versionN', { id: v.id })}</b><small>${state}${modeWord ? ` · ${esc(modeWord)}` : ''}</small></div>
      <button class="btn ghost${f && !f.noSave ? ' danger' : ''}" style="padding:8px 12px;font-size:12px" data-act="provisionServer" data-arg="${esc(v.id)}" ${dis}>${T('server.progress.button')}</button></div>`;
  }).join('');
  return `<div class="sect"><h4>${T('server.progress.title')}</h4><p class="hint">${T('server.progress.hint')}</p>${S.serverConfigured ? rows : noServerLine()}</div>`;
}
// The Players tab's password verification, a fact per version: whether the game server checks the password a player
// types. It is switched on the server only (POST /server/auth): Relic shows it. null = the agent's state file is
// unreadable; an agent that does not send the field says nothing of it.
function pwVerifyCard() {
  const rows = S.versions.map((v) => {
    const f = srvFacts(v.id), e = S.srv.versions[v.id];
    const raw = e ? e.passwordVerify : undefined;
    const text = !f ? rowState(v.id, f) : !f.present ? T('server.adv.stateAbsent')
      : raw === true ? T('server.pwv.on') : raw === false ? T('server.pwv.off') : raw === null ? T('server.adv.unknown') : T('server.pwv.notReported');
    return `<div class="line"><b>${T('common.versionN', { id: v.id })}</b><span class="pwv${raw === true ? ' on' : ''}">${text}</span></div>`;
  }).join('');
  return `<div class="sect"><h4>${T('server.pwv.title')}</h4><p class="hint">${T('server.pwv.hint')}</p>${S.serverConfigured ? rows : noServerLine()}</div>`;
}

// ── player: "Your account on this server" (self-service signup, when the admin allows it) ──
// One shape for every reset: boot and another server (srvRetarget). version = the chooser's pick (null
// = acctVersionCur decides — until the player touches the form, which pins the version DRAWN);
// tplBy = {version: template id}, the progress choice of each version (a draw of one version never
// rewrites another's); another = {version: true} while "Create another account" replaces a
// remembered login; shown = the signature of the form last DRAWN (accountCreate compares it).
function accountBlank() { return { version: null, another: {}, shown: '', name: '', password: '', tplBy: {}, running: false, lines: [], error: null, result: null }; }
// The progress choices the server offers a player on this version ([{id, label}]) — or null when it
// offers none: signup off, the version not in the policy, or every template the policy
// lists still unimported on that stack, which the snapshot then leaves out. An agent that sends no
// list offers the new account only. acctOffer, acctUp and acctLimit read the configured server's public snapshot
// (S.pub.status), or the one they are handed — Quick Start judges the server a Test answered (server.test's
// agent.status) by the same rules before Relic is connected to it; a snapshot handed as null or undefined is none
// (never the configured server's).
function acctOffer(id, snap) {
  const pub = arguments.length > 1 ? snap : S.pub.status;
  const su = pub && pub.signup;
  const sv = su && su.enabled && su.versions && su.versions[id];
  if (!sv) return null;
  if (!Array.isArray(sv.templates)) return [{ id: 'fresh', label: t('account.tpl.fresh') }];
  const list = sv.templates.filter((tp) => tp && typeof tp.id === 'string' && tp.id);
  return list.length ? list : null;
}
// The starting progresses the admin allows on this version that are not on the server yet ([{id,
// label}], the snapshot's signup.notReady — never imported there, or the import failed). The card
// draws them greyed and says why: nothing can be created from one. An agent that sends no such list
// names none.
function acctNotReady(id) {
  const su = S.pub.status && S.pub.status.signup;
  const list = su && su.enabled && su.notReady && su.notReady[id];
  if (!Array.isArray(list)) return [];
  const offer = acctOffer(id) || [];
  return list.filter((tp) => tp && typeof tp.id === 'string' && tp.id && tp.id !== 'fresh' && !offer.some((o) => o.id === tp.id));
}
// Running right now, as the public snapshot says (a stopped version would refuse the signup anyway).
function acctUp(id, snap) {
  const pub = arguments.length > 1 ? snap : S.pub.status;
  return !!(pub && pub.versions && pub.versions[id] && pub.versions[id].up === true);
}
// The versions the card can speak about: the ones the server offers an account on, the ones whose
// starting progresses are all still to come (acctNotReady), and the ones this PC already remembers a
// login for.
const acctVersions = () => S.versions.map((v) => v.id).filter((id) => acctOffer(id) || acctNotReady(id).length > 0 || accountIsMine(id) || acctMineAll(id).length > 0);
// The version the card shows. Not simply the Library's selection, which with nothing installed is
// the catalogue's first version: a player who never installed 2.8 could never create a 2.8 account,
// however the admin had opened it. Instead: the player's own pick; else the selection when an account can
// be created on it right now; else the first version where one can, preferring one that offers a
// prepared save (what the admin opened signups for); else the selection / the first one listed — one
// with a form or a remembered login before one whose starting progresses are all still to come.
function acctVersionCur() {
  const list = acctVersions();
  const ready = (id) => !!acctOffer(id) && acctUp(id);
  const sel = S.selectedVersionId;
  if (S.account.version && list.includes(S.account.version)) return S.account.version;
  if (sel && list.includes(sel) && ready(sel)) return sel;
  const saved = list.find((id) => ready(id) && acctOffer(id).some((tp) => tp.id !== 'fresh'));
  if (saved) return saved;
  const first = list.find(ready);
  if (first) return first;
  const real = (id) => !!acctOffer(id) || accountIsMine(id) || acctMineAll(id).length > 0;
  if (sel && list.includes(sel) && real(sel)) return sel;
  const other = list.find(real);
  if (other) return other;
  if (sel && list.includes(sel)) return sel;
  return list[0] || null;
}
// What the form offers for a version, as one string: the version, the templates and whether a password
// is asked. accountCard records the one it DREW; accountCreate recomputes it from the current snapshot —
// a policy change that landed without a redraw (the user was typing) must not send a stale choice.
function acctSig(id) {
  const offer = acctOffer(id);
  const pub = S.pub.status;
  const pv = !!(pub && pub.versions && pub.versions[id] && pub.versions[id].passwordVerify);
  return id + '|' + (offer ? offer.map((tp) => tp.id).join(',') : '-') + '|' + pv + '|' + acctLimit(id);
}
// Every account this launcher created on a version of the current server (init state accountLists; an
// older build remembered only the login, S.accounts), oldest first.
function acctMineAll(id) {
  const lists = S.accountLists && S.accountLists[id];
  if (Array.isArray(lists)) return lists.filter((a) => a && typeof a.name === 'string' && a.name);
  return S.accounts[id] && S.accounts[id].name ? [S.accounts[id]] : [];
}
// The ones that still exist there: an account of an earlier generation was wiped by a re-provision
// with the default save. gen = the server's generation now (unknown = nothing is judged gone).
const acctGone = (a, gen) => !!(a.generation && gen && a.generation !== gen);
// How many accounts one launcher may create on this version (maxPerPlayer), null when the
// server does not say (an older agent: no such limit).
function acctLimit(id, snap) {
  const pub = arguments.length > 1 ? snap : S.pub.status;
  const su = pub && pub.signup;
  const sv = su && su.versions && su.versions[id];
  return sv && Number.isInteger(sv.maxPerPlayer) && sv.maxPerPlayer >= 0 ? sv.maxPerPlayer : null;
}
// The server's generation of a version as the public snapshot says (null = unknown).
const acctGen = (id) => { const pub = S.pub.status; return (pub && pub.versions && pub.versions[id] && pub.versions[id].generation) || null; };
// This launcher's accounts on a version that still exist on the server.
const acctLive = (id) => { const gen = acctGen(id); return acctMineAll(id).filter((a) => !acctGone(a, gen)); };
function accountCard(versionId) {
  const pub = S.pub.status;
  if (!pub) return '';
  const offer = acctOffer(versionId);
  const up = acctUp(versionId);
  const pv = !!(pub.versions && pub.versions[versionId] && pub.versions[versionId].passwordVerify);
  const gen = acctGen(versionId);
  const all = acctMineAll(versionId);
  const live = all.filter((a) => !acctGone(a, gen));
  const gone = all.filter((a) => acctGone(a, gen));
  // The login the Library shows for this version (one of the list: the last created unless picked).
  const shownName = S.accounts[versionId] && S.accounts[versionId].name ? S.accounts[versionId].name.toLowerCase() : '';
  const limit = acctLimit(versionId);
  const full = limit !== null && live.length >= limit;
  const ac = S.account;
  const list = acctVersions();
  // The version chooser: only when there is a choice. Dead while a create runs — its log and its
  // result belong to the version it was started on.
  const chips = list.length > 1
    ? `<div class="flex gap wrap" style="margin-bottom:12px">${list.map((id) => `<button class="lg-chip ${versionId === id ? 'on' : ''}" data-act="accountVersion" data-arg="${esc(id)}" ${ac.running ? 'disabled' : ''}>${T('common.versionN', { id })}</button>`).join('')}</div>`
    : '';
  const title = `<h4>${T(live.length ? 'account.listTitle' : 'account.titleFor', { version: versionId })}</h4>`;
  // "Create a new account" opens the form under the list — only while the server offers one on this
  // version and this launcher is still under its limit there.
  const another = live.length > 0 && !!ac.another[versionId] && !!offer && !full;
  const formOpen = !!offer && !full && (!live.length || another);
  // "Create it again below" only above a form: without one (nothing offered yet, or the limit is 0) the
  // note says what happened and nothing more.
  const staleNote = gone.length && !live.length ? `<div class="note" style="margin-bottom:10px">${T(formOpen ? 'account.stale' : 'account.staleNoForm', { name: gone[gone.length - 1].name })}</div>` : '';
  let listHtml = '';
  if (live.length) {
    const rows = live.map((a) => {
      const isShown = a.name.toLowerCase() === shownName;
      const use = isShown ? '' : `<button class="crcopy" data-act="accountUse" data-arg="${esc(versionId + ':' + a.name)}" title="${T('account.useTitle')}">${T('account.use')}</button>`;
      return `<div class="crow"><div><small>${T(isShown ? 'account.shownInLibrary' : 'account.accountLabel')}</small><b class="mono pick">${esc(a.name)}</b></div>
        <div class="flex gap">${use}<button class="crcopy" data-act="copyAccount" data-arg="${esc(a.name)}">${T('common.copy')}</button></div></div>`;
    }).join('');
    listHtml = `<div class="creds">${rows}<div class="crow"><div><small>${T('lib.creds.password')}</small><b class="pw">${esc(passwordWord(versionId))}</b></div></div></div>
      <p class="hint" style="margin:10px 0 0">${T('account.loginHint', { version: versionId })}</p>`;
  }
  // Allowed by the admin, not on the server yet: greyed cards that say so, and one note with the
  // reason — never a choice (no data-act: a click does nothing).
  const waiting = acctNotReady(versionId);
  const waitingCards = waiting.map((tp) => `<div class="opt dis" aria-disabled="true"><div class="h">${esc(tp.label || tp.id)}</div><p>${T('account.tplNotReadyCard')}</p></div>`).join('');
  const waitingNote = waiting.length ? `<div class="note" style="margin-top:10px">${T('account.tplNotReady')}</div>` : '';
  if (!formOpen) {
    if (!live.length && !offer) {
      // Nothing an account could start from yet, on a version the admin opened: the card shows what
      // it waits for instead of staying empty.
      if (waiting.length) {
        return `<div class="sect">${title}${chips}${staleNote}<label class="fld">${T('account.template')}</label>
          <div class="grid2">${waitingCards}</div>${waitingNote}</div>`;
      }
      return list.length > 1 || staleNote ? `<div class="sect">${title}${chips}${staleNote}</div>` : '';
    }
    // Offered, but this launcher may create no (more) account here: its limit is used up, or it is 0.
    const limitNote = offer && full ? `<div class="note" style="margin-top:${live.length ? 12 : 0}px">${T(live.length ? 'account.limitReached' : 'account.limitClosed', { version: versionId, limit })}</div>` : '';
    const used = live.length && limit !== null ? `<span class="hint" style="margin:0">${T('account.used', { count: live.length, limit })}</span>` : '';
    const btn = live.length && offer && !full ? `<button class="btn" style="padding:9px 16px;font-size:12.5px" data-act="accountAnother" data-arg="${esc(versionId)}">${T('account.another')}</button>` : '';
    const foot = used || btn ? `<div class="flex between center" style="margin-top:12px">${used || '<span></span>'}${btn}</div>` : '';
    return `<div class="sect">${title}${chips}${staleNote}${listHtml}${foot}${limitNote}</div>`;
  }
  const templates = offer;
  // Normalised for THIS version only: a draw of another one (the card follows the poll until the
  // player touches it) must not reset the progress picked here to the new account.
  if (!templates.some((tp) => tp.id === ac.tplBy[versionId])) ac.tplBy[versionId] = templates[0].id;
  const tpl = ac.tplBy[versionId];
  ac.shown = acctSig(versionId);
  // Offered but not running: the form stays (the player can prepare it), Create waits for the server.
  const dis = ac.running || !up || !!S.acctPending ? 'disabled' : '';
  const limits = limit !== null ? T('account.limitsPlayer', { limit }) : T('account.limits');
  return `<div class="sect">${title}${chips}${staleNote}${listHtml}
    ${another ? `<h4 style="margin-top:18px">${T('account.newTitle')}</h4>` : ''}
    <p class="hint">${T(another ? 'account.anotherHint' : 'account.intro', { version: versionId })}</p>
    ${!up ? `<div class="note" style="margin-bottom:10px">${T('account.notRunning', { version: versionId })}</div>` : ''}
    <label class="fld">${T('account.name')}</label><input class="txt mono" data-model="account.name" value="${esc(ac.name)}" placeholder="${T('account.namePh')}" maxlength="20" autocomplete="off" spellcheck="false">
    ${pv ? `<label class="fld">${T('account.password')}</label><input class="txt mono" type="password" data-model="account.password" value="${esc(ac.password)}" placeholder="${T('account.passwordPh')}" autocomplete="new-password">` : ''}
    <label class="fld">${T('account.template')}</label>
    <div class="grid2">${templates.map((tp) => `<div class="opt ${tpl === tp.id ? 'sel' : ''}" data-act="accountTemplate" data-arg="${esc(versionId + ':' + tp.id)}"><div class="h">${esc(tp.label || tp.id)}</div><p>${T(I18N.has('account.tpl.' + tp.id + '.sub') ? 'account.tpl.' + tp.id + '.sub' : 'account.tpl.other.sub')}</p></div>`).join('')}${waitingCards}</div>${waitingNote}
    ${ac.running || ac.lines.length ? `<div class="console" style="margin-top:12px"><div class="log" id="acctlog">${ac.lines.map((l) => `<div>${esc(l)}</div>`).join('')}</div></div>` : ''}
    ${ac.error ? `<div class="note" style="margin-top:10px">${esc(ac.error)}</div>` : ''}
    <div class="flex between center" style="margin-top:14px"><span class="hint" style="margin:0">${limits}</span>
      <div class="flex gap">${another ? `<button class="btn ghost" data-act="accountAnotherCancel" data-arg="${esc(versionId)}" ${ac.running ? 'disabled' : ''}>${T('common.cancel')}</button>` : ''}
        <button class="btn" data-act="accountCreate" data-arg="${esc(versionId)}" ${dis}>${ac.running ? T('account.creating') : T('account.create')}</button></div></div>
  </div>`;
}

// The player Server page's "Enter admin mode" button, patched IN PLACE while admin.openEnter runs
// (the same rule as the start screen's local-agent button, syncLocalAgentButtons): a render() for a
// label and a disabled flag would rebuild the page under the pointer mid-click.
function syncOpenAdminButton() {
  const busy = !!S.openAdmin.busy;
  document.querySelectorAll('[data-act="openAdminEnter"]').forEach((b) => {
    b.disabled = busy;
    b.textContent = busy ? t('server.player.openAdminEntering') : t('server.player.openAdminEnter');
  });
}

// Player-mode server page: read-only status from the public snapshot (or the SDK probe) + the
// account card. No admin buttons — the agent would refuse them anyway; the one way INTO admin mode
// a server may offer (open administration) is a banner under the cards.
function playerServer() {
  const pub = S.pub.status;
  const cards = S.versions.map((v) => {
    // (an entry that is no object is no entry here, as in statusVersions)
    const named = pub && pub.versions && pub.versions[v.id];
    const e = isVersionEntry(named) ? named : null;
    // Only fresh data asserts anything: while checking, after an error, or before a new server was read,
    // the previous snapshot must never be drawn under this server's name. statusUnknown (docker did not
    // answer on the box) makes every up:false fabricated: unverified, not OFFLINE. A version the snapshot
    // does not name while it names others is one the agent does not know (srvUnlisted); any other
    // snapshot without an entry for the version says nothing about it — only an answered present:false
    // is MISSING. A running one whose game server still loads its data (`loading`, said only while
    // true) lets nobody in yet.
    const st = S.srv.state === 'checking' ? 'checking' : S.srv.state !== 'ok' || !pub ? 'unknown'
      : !e ? (srvUnlisted(v.id) ? 'unlisted' : 'unknown') : e.present === false ? 'absent'
        : e.up ? (e.healthy === false ? 'working' : e.loading === true ? 'loading' : 'live') : pub.statusUnknown ? 'unknown' : 'down';
    const meta = SRVMETA[st];
    const acc = e && e.defaultAccount ? `<div><small style="color:var(--muted);font-weight:800;font-size:10.5px">${T('server.card.accountLabel')}</small><div style="font-weight:800" class="mono">${esc(e.defaultAccount)}</div></div>` : '';
    return `<div class="cmdcard" style="padding:20px">
      <div class="flex between center"><div class="serif" style="font-weight:700;font-size:18px">${T('server.card.title', { id: v.id })}</div>
        <span class="badge" style="background:rgba(0,0,0,.06);color:${meta[1]}"><span class="dot" style="background:${meta[1]}"></span>${T(meta[0])}</span></div>
      <p style="color:var(--inkSoft);font-size:12.5px;font-weight:600;line-height:1.5;margin:11px 0 0">${st === 'working' ? T('server.player.degraded') : T(SRVNOTE[st])}</p>
      <div class="flex gap wrap" style="margin:14px 0 18px">
        <div><small style="color:var(--muted);font-weight:800;font-size:10.5px">${T('server.card.serverLabel')}</small><div style="font-weight:800">${esc(S.serverAddr.host)}:${S.serverAddr.port}</div></div>${acc}
      </div>
      <div class="flex gap"><button class="btn ghost" style="flex:1;padding:11px" data-act="checkServers" ${S.srv.state === 'checking' ? 'disabled' : ''}>${T('server.card.check')}</button></div>
    </div>`;
  }).join('');
  // The account card picks its own version (acctVersionCur) — not the Library's selection.
  const acctId = acctVersionCur();
  const errNote = S.srv.state === 'error' ? `<div class="note" style="margin-top:16px">${T('server.player.unreachable', { error: S.srv.error })}</div>` : '';
  const src = S.srv.info && S.srv.info.public ? T('server.player.viaAgent', { name: (pub && pub.name) || S.serverAddr.host }) : T('server.player.viaProbe');
  // The admin opened Server Admin mode to every player (`openAdmin` of the public snapshot — the
  // agent's own word; the game-port probe cannot say it): what entering means, and the button. Fresh
  // data only, like the cards: a previous server's snapshot must never offer it under this one's name.
  const busy = !!S.openAdmin.busy;
  const openNote = S.srv.state === 'ok' && S.srv.info && S.srv.info.public && pub && pub.openAdmin === true
    ? `<div class="banner">${I.warn}<div><b>${T('server.player.openAdminTitle')}</b><p>${T('server.player.openAdminBody')}</p>
        <button class="btn" style="margin-top:10px;padding:8px 14px;font-size:12px" data-act="openAdminEnter" ${busy ? 'disabled' : ''}>${busy ? T('server.player.openAdminEntering') : T('server.player.openAdminEnter')}</button></div></div>`
    : '';
  return `<div class="pad"><h1 class="title">${T('server.title')}</h1><p class="sub">${T('server.player.sub')}</p>
    <div class="grid2" ${cardsGridStyle(S.versions.length)}>${cards}</div>${openNote}${errNote}
    <div class="agentbar" style="margin-top:16px"><span class="dot ${S.srv.state === 'ok' ? 'live' : 'unknown'}"></span><span>${esc(S.serverAddr.host)}:${S.serverAddr.port}</span><small>· ${src}</small></div>
    ${acctId ? accountCard(acctId) : ''}
  </div>`;
}

// ── admin: "Create a player account" (POST /server/account/create) ──
// An in-game login for the admin or a friend, with any progress imported on the stack — the Player
// accounts policy and its quotas do not apply (it is an admin job, behind the token).
// One shape for every reset: boot and another server (srvRetarget). tpl[v] = the last GET
// /server/templates of v ({list, loading} — list null when it failed); tplSeq[v] = its request seq;
// created = this session's results, newest first (a generated password lives only here, in memory);
// pending = {remember, name} of the create in flight (runServerJob keeps no extras).
// version = the chip picked (null = acctNewCur decides — until the admin touches the form, which pins
// shownVersion, the version the card last DREW); tplBy = the progress choice per version, as on the
// player card.
function acctNewBlank() { return { version: null, shownVersion: '', name: '', password: '', tplBy: {}, remember: false, tpl: {}, tplSeq: {}, created: [], pending: null }; }
const acctNewCur = () => S.acctNew.version || srvLiveId() || S.selectedVersionId || (S.versions[0] && S.versions[0].id) || '';
// The agent's own rules (NAME_RE / PASSWORD_RE in gio_agent.py): said here before a job is started.
const ACCOUNT_NAME_RE = /^[A-Za-z0-9][A-Za-z0-9_.-]{2,19}$/;
const ACCOUNT_PASSWORD_RE = /^[\x21-\x7e]{8,64}$/;
// The card creates through the admin route alone — a server job, behind the token — so it needs the
// admin /status as the last status read: false before the first read and after a failed one (S.srv.info
// null), and for the token-less public snapshot ({public: true}), which is not an admin's word.
const acctNewAdmin = () => !!S.srv.info && !S.srv.info.public;
// The progress templates of a stack, [{id, label, ready, present, failed}]: the agent's GET
// /server/templates when it answered (loadAcctTemplates), else the admin /status records ({id:
// {createdAt, error}} — no labels there, and a template never imported has no record at all). ready =
// imported without an error, the agent's own template_ready test; failed = its last import ended in an
// error; present = its save file is among the agent's files (false = nothing to import it from — only
// the agent's own list says so).
function acctNewStock(v) {
  const r = S.acctNew.tpl[v];
  if (r && Array.isArray(r.list)) return r.list.map((x) => ({ id: x.id, label: x.label, ready: x.imported, present: x.present !== false, failed: !!x.failed }));
  const e = S.srv.versions && S.srv.versions[v];
  const recs = e && e.templates && typeof e.templates === 'object' && !Array.isArray(e.templates) ? e.templates : {};
  return Object.keys(recs).map((id) => ({ id, label: id, ready: !!(recs[id] && recs[id].createdAt && !recs[id].error), present: true, failed: !!(recs[id] && recs[id].error) }));
}
// What the form offers ({list, notReady}): the new account + every imported template. notReady =
// templates known but not imported — drawn greyed, each with its reason.
function acctNewChoices(v) {
  const fresh = { id: 'fresh', label: t('account.tpl.fresh') };
  const stock = acctNewStock(v).filter((x) => x.id !== 'fresh');
  return { list: [fresh].concat(stock.filter((x) => x.ready)), notReady: stock.filter((x) => !x.ready) };
}
// A template's label for a result row (the agent's result names the id only).
function acctNewTplLabel(v, id) {
  if (!id || id === 'fresh') return t('account.tpl.fresh');
  const x = acctNewStock(v).find((s) => s.id === id);
  return (x && x.label) || id;
}
// A finished create, into the session list (newest first, ten at most). key = a stable handle for the
// row's copy button: the password never goes into an attribute.
let acctNewKey = 0;
function acctNewPush(e) {
  S.acctNew.created.unshift(Object.assign({ key: ++acctNewKey }, e));
  if (S.acctNew.created.length > 10) S.acctNew.created.length = 10;
}
function acctNewCard() {
  // The public snapshot as the last status: an answer, but not one a job can be started on — the card
  // has nothing to offer or to say, so none is drawn.
  if (S.srv.info && S.srv.info.public) return '';
  const a = S.acctNew;
  const v = acctNewCur();
  const chips = S.versions.map((x) => `<button class="lg-chip ${v === x.id ? 'on' : ''}" data-act="acctNewVersion" data-arg="${esc(x.id)}">${T('common.versionN', { id: x.id })}</button>`).join('');
  const f = srvFacts(v);
  let body;
  // No status at all: nothing can be said, and no form is offered. "Checking..." only while a read is
  // still owed — a failed one has the page's own error note, and without a configured server there is
  // nothing being checked.
  if (!acctNewAdmin()) body = S.serverConfigured && S.srv.state !== 'error' ? `<p class="hint">${T('common.checking')}</p>` : '';
  else if (!f || !f.present) body = `<div class="note">${T('server.acctNew.notPresent', { version: v })}</div>`;
  else {
    const choices = acctNewChoices(v);
    const verify = passwordVerified(v);
    // Keep the choice valid — for THIS version only (see accountCard): the new account, always
    // offered first.
    if (!choices.list.some((tp) => tp.id === a.tplBy[v])) a.tplBy[v] = choices.list[0].id;
    const tpl = a.tplBy[v];
    a.shownVersion = v;
    // The password field: the agent generates one when verification is on and the field is left empty
    // — except for the admin's OWN login ("Show it as my login"): the launcher then calls it "the
    // password you chose", so it must be one the admin typed. Without verification nobody asks for one.
    const pwField = verify
      ? `<label class="fld">${T('account.password')}</label><input class="txt mono" type="password" data-model="acctNew.password" value="${esc(a.password)}" placeholder="${T('account.passwordPh')}" autocomplete="new-password">
        <p class="hint" style="margin:8px 0 0">${T(a.remember ? 'server.acctNew.passwordMine' : 'server.acctNew.passwordHint')}</p>`
      : `<p class="hint" style="margin:12px 0 0">${T('server.acctNew.passwordAny')}</p>`;
    const tplCards = choices.list.map((tp) => {
      const sub = 'account.tpl.' + tp.id + '.sub';
      return `<div class="opt ${tpl === tp.id ? 'sel' : ''}" data-act="acctNewTemplate" data-arg="${esc(v + ':' + tp.id)}"><div class="h">${esc(tp.label || tp.id)}</div>${I18N.has(sub) ? `<p>${T(sub)}</p>` : ''}</div>`;
    }).join('');
    // Known but not imported: greyed cards after the choices, each with its own reason — never a
    // choice (no data-act: a click does nothing).
    const waitCards = choices.notReady.map((tp) => `<div class="opt dis" aria-disabled="true"><div class="h">${esc(tp.label || tp.id)}</div><p>${T(!tp.present ? 'server.acctNew.tplCard.missing' : tp.failed ? 'server.acctNew.tplCard.failed' : 'server.acctNew.tplCard.notImported')}</p></div>`).join('');
    // What brings them in depends on the stack, and the note under the cards says which: "Prepare the
    // server" while the version is not installed (no import button: there is nothing to import into),
    // else an import into the running stack — "Import them now" beside the note (POST
    // /server/templates/ensure): a job, dead while another runs or while the stack is stopped (the
    // agent refuses to import into a stopped stack). A template whose save file the agent does not
    // have cannot be imported at all: named in a note of its own.
    const importable = choices.notReady.filter((x) => x.present);
    const lost = choices.notReady.filter((x) => !x.present);
    const why = !f.bootstrapped ? 'server.acctNew.tplWhy.prepare' : !f.up ? 'server.acctNew.tplWhy.start' : 'server.acctNew.tplWhy.import';
    const notReady = (importable.length
      ? `<div class="flex center gap" style="margin-top:10px"><div class="note" style="flex:1">${T(why, { version: v })}</div>
        ${f.bootstrapped ? `<button class="btn ghost" style="padding:8px 12px;font-size:12px" data-act="acctNewImport" data-arg="${esc(v)}" ${srvBusy() || !f.up ? 'disabled' : ''}>${T('server.acctNew.importTemplates')}</button>` : ''}</div>` : '')
      + (lost.length ? `<div class="note" style="margin-top:10px">${T('server.acctNew.tplWhy.missing', { list: lost.map((x) => x.label || x.id).join(', ') })}</div>` : '');
    // The create runs as a server job (one at a time, its log in the page's console). "Creating..."
    // only on the chip of the version being created — another chip's button is merely dead meanwhile.
    const running = jobHere() && S.srvJob.kind === 'accountcreate' && S.srvJob.version === v;
    const dis = srvBusy() || !f.up ? 'disabled' : '';
    // Not running: nothing can be created yet, and the note says what to press — "Prepare the server"
    // while the version is not installed (it has no Start), else Start.
    body = `${!f.up ? `<div class="note" style="margin-bottom:10px">${T(f.bootstrapped ? 'server.acctNew.notLive' : 'server.acctNew.notInstalled', { version: v })}</div>` : ''}
      <label class="fld">${T('account.name')}</label><input class="txt mono" data-model="acctNew.name" value="${esc(a.name)}" placeholder="${T('account.namePh')}" maxlength="20" autocomplete="off" spellcheck="false">
      ${pwField}
      <label class="fld">${T('account.template')}</label>
      <div class="grid2">${tplCards}${waitCards}</div>${notReady}
      <div class="line" style="margin-top:12px"><div><b>${T('server.acctNew.remember')}</b><small>${T('server.acctNew.rememberSub')}</small></div><button class="toggle ${a.remember ? 'on' : ''}" data-act="acctNewRemember"><span class="knob"></span></button></div>
      <div class="flex" style="justify-content:flex-end;margin-top:14px"><button class="btn" data-act="acctNewCreate" data-arg="${esc(v)}" ${dis}>${running ? T('account.creating') : T('account.create')}</button></div>`;
  }
  // This session's results — never a lock on the form: the next account can be typed right away.
  const rows = a.created.map((c) => {
    const uid = c.uid != null && c.uid !== '' ? t('server.acctNew.uid', { uid: c.uid }) : t('server.acctNew.uidLater');
    const pw = c.password
      ? `<div class="crow"><div><small>${c.generated ? T('server.acctNew.pwGenerated') : T('lib.creds.password')}</small><b class="mono pick">${esc(c.password)}</b></div><button class="crcopy" data-act="acctNewCopyPw" data-arg="${c.key}">${T('common.copy')}</button></div>`
      : `<div class="crow"><div><small>${T('lib.creds.password')}</small><b class="pw">${c.own ? T('account.pwYours') : T('lib.creds.any')}</b></div></div>`;
    return `<div class="creds" style="margin-top:8px">
      <div class="crow"><div><small>${T('server.acctNew.rowLabel', { version: c.version, template: c.templateLabel, uid })}</small><b class="mono pick">${esc(c.name)}</b></div><button class="crcopy" data-act="copyAccount" data-arg="${esc(c.name)}">${T('common.copy')}</button></div>
      ${pw}</div>`;
  }).join('');
  const results = rows ? `<label class="fld">${T('server.acctNew.created')}</label>${rows}<p class="hint" style="margin:10px 0 0">${T('server.acctNew.loginHint')}</p>` : '';
  return `<div class="sect"><h4>${T('server.acctNew.title')}</h4><p class="hint">${T('server.acctNew.hint')}</p>
    <div class="flex gap wrap" style="margin-bottom:12px">${chips}</div>
    ${body}${results}
  </div>`;
}

// ── admin: player-account policy ──
// A version's template chips, [{id, label, ready}]: the new account, then the stock the admin card
// reads (acctNewStock — the labels and the imports of GET /server/templates, else the /status records,
// failed imports included), then any id the policy already names, so a chip it names can always be
// switched off. stockKnown = one of those two reads is there: without it readiness is not known and
// nothing may be called "not imported".
function policyTplChips(v, allowed) {
  const out = [{ id: 'fresh', label: t('account.tpl.fresh'), ready: true }];
  const add = (id, label, ready) => { if (id && !out.some((x) => x.id === id)) out.push({ id, label: label || id, ready: !!ready }); };
  acctNewStock(v).forEach((x) => add(x.id, x.label, x.ready));
  allowed.forEach((id) => add(id, id, false));
  return out;
}
function acctNewStockKnown(v) {
  const r = S.acctNew.tpl[v];
  if (r && Array.isArray(r.list)) return true;
  const e = S.srv.versions && S.srv.versions[v];
  return !!(e && e.templates && typeof e.templates === 'object' && !Array.isArray(e.templates));
}
function policyCard() {
  const pol = S.policy.data;
  if (!pol) return `<div class="sect"><h4>${T('policy.title')}</h4><p class="hint">${S.policy.loading ? T('common.checking') : T('policy.unavailable')}</p>
    <button class="btn ghost" style="padding:8px 14px;font-size:12px" data-act="policyLoad">${T('common.retry')}</button></div>`;
  const row = (label, sub, on, act, arg) => `<div class="line"><div><b>${label}</b><small>${sub}</small></div><button class="toggle ${on ? 'on' : ''}" data-act="${act}" ${arg ? `data-arg="${esc(arg)}"` : ''}><span class="knob"></span></button></div>`;
  let html = `<div class="sect"><h4>${T('policy.title')}</h4><p class="hint">${T('policy.hint')}</p>
    ${row(T('policy.signup'), T('policy.signupSub'), !!(pol.signup && pol.signup.enabled), 'policyToggleSignup')}`;
  S.versions.forEach((v) => {
    const pv = (pol.signup && pol.signup.versions && pol.signup.versions[v.id]) || { templates: ['fresh'], maxPerDay: 50 };
    const allowed = Array.isArray(pv.templates) ? pv.templates : [];
    const avail = policyTplChips(v.id, allowed);
    // The agent offers players only the templates imported on the stack: an allowed one that is not
    // (never imported, or its import failed) cannot be chosen — where their card draws it, it is
    // greyed. Said here, as a note — the chip stays live: allowing it now is fine, it is offered once
    // imported.
    const unready = acctNewStockKnown(v.id) ? avail.filter((x) => x.id !== 'fresh' && !x.ready && allowed.includes(x.id)) : [];
    html += `<div class="line"><div><b>${T('policy.versionTemplates', { version: v.id })}</b><small>${T('policy.versionTemplatesSub')}</small>
      <div class="flex gap wrap" style="margin-top:8px">${avail.map((tp) => `<button class="lg-chip ${allowed.includes(tp.id) ? 'on' : ''}" data-act="policyToggleTemplate" data-arg="${esc(v.id + ':' + tp.id)}">${esc(tp.label)}</button>`).join('')}</div></div>
      <div style="text-align:right"><small style="display:block;color:var(--muted);font-size:10.5px;font-weight:800">${T('policy.maxPerDay')}</small><input class="txt" style="width:90px;padding:8px 10px" data-policy-max data-arg="${esc(v.id)}" value="${esc(String(pv.maxPerDay == null ? 50 : pv.maxPerDay))}">
        ${Number.isInteger(pv.maxPerPlayer) ? `<small style="display:block;color:var(--muted);font-size:10.5px;font-weight:800;margin-top:8px" title="${T('policy.maxPerPlayerSub')}">${T('policy.maxPerPlayer')}</small><input class="txt" style="width:90px;padding:8px 10px" data-policy-maxplayer data-arg="${esc(v.id)}" value="${esc(String(pv.maxPerPlayer))}" title="${T('policy.maxPerPlayerSub')}">` : ''}</div></div>
      ${unready.length ? `<div class="note" style="margin:0 0 10px">${T('policy.tplNotReady', { list: unready.map((x) => x.label).join(', ') })}</div>` : ''}`;
  });
  html += row(T('policy.playerCommands'), T('policy.playerCommandsSub'), !!pol.playerCommands, 'policyTogglePlayerCommands');
  html += `<div class="flex between center" style="margin-top:14px"><span class="hint" style="margin:0">${T('policy.note')}</span><button class="btn" data-act="policySave">${T('policy.save')}</button></div></div>`;
  return html;
}

// The Access tab's "Server connection", read-only: the server this launcher administers — chosen on the start screen
// with its token, or by an agent install — and what this launcher holds admin rights there with.
function connectionCard() {
  const fld = 'color:var(--muted);font-weight:800;font-size:10.5px';
  const auth = S.admin.openKey ? T('server.conn.authOpenKey') : S.admin.tokenScope === 'any' ? T('server.conn.authBaked') : T('server.conn.authToken');
  const facts = !S.serverConfigured ? `<p class="hint" style="margin:0">${T('server.agent.notConfigured')}</p>` : `<div class="flex gap wrap" style="row-gap:10px">
      <div><small style="${fld}">${T('server.card.serverLabel')}</small><div style="font-weight:800" class="mono">${esc(S.serverAddr.host)}</div></div>
      <div><small style="${fld}">${T('server.conn.gamePort')}</small><div style="font-weight:800" class="mono">${esc(S.serverAddr.port)}</div></div>
      <div><small style="${fld}">${T('server.conn.agent')}</small><div style="font-weight:800"><span class="mono">${esc(agentAddr())}</span> · ${S.admin.mode === 'ssh' ? T('server.agent.ssh') : T('server.agent.direct')}</div></div>
      <div><small style="${fld}">${T('server.conn.auth')}</small><div style="font-weight:800">${auth}</div></div>
    </div>`;
  return `<div class="sect"><h4>${T('server.conn.title')}</h4><p class="hint">${T('server.conn.hint')}</p>${facts}
    <div class="flex" style="justify-content:flex-end;margin-top:12px"><button class="btn ghost" style="padding:8px 14px;font-size:12px" data-act="changeMode">${T('common.changeMode')}</button></div></div>`;
}
// The Access tab's "Public address": the address the agent tells game clients to connect to (a DNS name it follows,
// with the address it resolves to; the lookup's or the follow's problem; an answer waiting for its confirming lookup)
// and, per version, what the agent's record says it applied — its configuration files' half and its database's — with
// Re-apply (netfixServer, asked first: what restarts depends on whether the version runs). Nothing to apply without an
// address, nothing to say of up or down under the docker fog.
function publicAddressCard() {
  const info = S.srv.info && !S.srv.info.public ? S.srv.info : null, busy = srvBusy(), fog = srvDockerError();
  const fld = 'color:var(--muted);font-weight:800;font-size:10.5px';
  const target = info && typeof info.advertisedIp === 'string' ? info.advertisedIp : '';
  const value = !info ? '' : info.advertisedHost ? `${esc(info.advertisedHost)} → ${esc(info.advertisedIp || t('server.agent.unresolved'))}`
    : info.advertisedIp ? esc(info.advertisedIp) : T('server.addr.none');
  const facts = !info ? '' : `<div><small style="${fld}">${T('server.addr.label')}</small><div style="font-weight:800"${info.advertisedHost || info.advertisedIp ? ' class="mono"' : ''}>${value}</div></div>`
    + (info.advertisedError ? `<div class="note" style="margin-top:10px">${T('server.addr.error', { error: String(info.advertisedError) })}</div>` : '')
    + (info.advertisedPending ? `<div class="note" style="margin-top:10px">${T('server.addr.pending', { ip: String(info.advertisedPending) })}</div>` : '');
  const rows = S.versions.map((v) => {
    const f = srvFacts(v.id), e = S.srv.versions[v.id] || {};
    const ip = typeof e.advertisedIp === 'string' ? e.advertisedIp : '', sql = typeof e.advertisedIpSql === 'string' ? e.advertisedIpSql : '';
    const sub = rowState(v.id, f) || (!ip ? T('server.addr.versionNone') : sql === ip ? T('server.addr.versionIp', { ip }) : T('server.addr.versionDbBehind', { ip }));
    const dis = busy || fog || !f || !f.present || !f.bootstrapped ? 'disabled' : !target ? `disabled title="${T('server.addr.noAddress')}"` : '';
    return `<div class="line"><div><b>${T('common.versionN', { id: v.id })}</b><small>${sub}</small></div>
      <button class="btn ghost" style="padding:8px 12px;font-size:12px" data-act="netfixServer" data-arg="${esc(v.id)}" ${dis}>${T('server.adv.netfix')}</button></div>`;
  }).join('');
  return `<div class="sect"><h4>${T('server.addr.title')}</h4><p class="hint">${T('server.addr.hint')}</p>${facts}<div style="margin-top:8px">${S.serverConfigured ? rows : noServerLine()}</div></div>`;
}

// ── admin: open administration ──
// The policy flag `openAdmin`: while it is on, the agent hands every launcher that asks a key with
// the agent token's rights (GET /public/admin/key), and the player Server page offers "Enter admin
// mode". A card of its own, not a row of the policy card: the switch is dangerous enough for a
// confirm and an immediate write of its own (openAdminGo — a one-field merge, like
// setHotpatchSource), never the "Apply policy" batch, and its warning needs a paragraph, which a
// row's <small> cannot hold. The warning is drawn whether the option is on or off: the admin reads it
// before flipping. The toggle is dead while a job runs; without the policy (not loaded, or the read
// failed) the card takes the policy card's own unavailable shape.
function openAdminCard() {
  const pol = S.policy.data;
  if (!pol) return `<div class="sect"><h4>${T('policy.openAdmin.title')}</h4><p class="hint">${S.policy.loading ? T('common.checking') : T('policy.unavailable')}</p>
    <button class="btn ghost" style="padding:8px 14px;font-size:12px" data-act="policyLoad">${T('common.retry')}</button></div>`;
  return `<div class="sect"><h4>${T('policy.openAdmin.title')}</h4><p class="hint">${T('policy.openAdmin.hint')}</p>
    <div class="banner" style="margin:0">${I.warn}<div><b>${T('policy.openAdmin.warnTitle')}</b><p>${T('policy.openAdmin.warnBody')}</p></div></div>
    <div class="line"><div><b>${T('policy.openAdmin.toggle')}</b><small>${T('policy.openAdmin.toggleSub')}</small></div><button class="toggle ${pol.openAdmin === true ? 'on' : ''}" data-act="openAdminToggle" ${srvBusy() ? 'disabled' : ''}><span class="knob"></span></button></div>
  </div>`;
}

// ── admin: server secrets (per version) ──
// The MUIP line of GET /server/secrets, in words: never the key (the agent answers a fingerprint
// only), and whether it is still the archive's published one — which the agent replaces with a random
// key at the next Prepare server / Start on its own, or keeps as it is while it keeps .env settings
// (srvKeepEnv).
function muipKeyText(m) {
  if (!m || !m.set) return t('secrets.muipUnset');
  const fp = m.fingerprint || '';
  if (!m.vendorDefault) return t('secrets.muipSet', { fp });
  return srvKeepEnv() ? t('secrets.muipVendorKept', { fp }) : t('secrets.muipVendor', { fp });
}
// "Show current values" as the rows of a notice (noticeOverlay `rows`): the three passwords in the
// clear, each selectable and with its own Copy button — one the agent made is 32 random characters,
// nothing to retype by eye into adminer —, the MUIP line in words (a fingerprint). The
// card's own labels, not English literals: that window is the one place the values are shown in the
// clear, and a non-English build must not fall back to hard-coded text there.
function secretsRows(d) {
  const secret = (label, value) => (value ? { label, value: String(value), copy: true } : { label, value: '—', copy: false });
  return [secret(t('secrets.mysqlRoot'), d.mysqlRoot), secret(t('secrets.internal'), d.internal), secret(t('secrets.flask'), d.flask),
    { label: t('secrets.muip'), value: muipKeyText(d.muip), copy: false }];
}
// What the form asks the agent to change ('' = keep the current value): the four fields.
function secretsAsked() {
  const f = S.secrets.form;
  return { mysqlRoot: f.mysqlRoot || '', internal: f.internal || '', flask: f.flask || '', muip: f.muip || '' };
}
// The agent's own rule (MYSQL_ROOT_RE in gio_agent.py, do_secrets) for the two values the stack reads
// where a first character other than a letter can break them: the MySQL root password (unquoted in the
// compose file, where "0123…" reads as a number) and the stack internal password (on
// redis-server's command line, where "--…" reads as an option and redis does not start). Said here
// before the job is started, each with its own text, and the form keeps what was typed; Generate always
// makes such a value.
const SECRET_LETTER_FIRST_RE = /^[A-Za-z][A-Za-z0-9_-]{7,127}$/;
const SECRETS_LETTER_FIRST = { mysqlRoot: 'toast.secretsBadRoot', internal: 'toast.secretsBadInternal' };
// The text of the first asked value that breaks that rule ('' = none). An empty field keeps the
// current value: nothing to check.
function secretsBroken(asked) {
  const k = Object.keys(SECRETS_LETTER_FIRST).find((key) => asked[key] && !SECRET_LETTER_FIRST_RE.test(asked[key]));
  return k ? SECRETS_LETTER_FIRST[k] : '';
}
// The Access tab's word on the vendor's published secrets, from the admin /status alone (no Load): per version a line
// per value still the archive's published one — the MUIP sign key, the MySQL root password, the Flask key, the stack
// internal password — with what becomes of it (replaced at the next Prepare / Start, or kept as it is by an agent that
// keeps .env settings: srvKeepEnv), or a change of that internal password that did not finish. Only the warnings are
// drawn; the unfinished change only once no job of the version runs (versionJobRunning): a fresh Prepare's bootstrap
// and a Secrets rotation keep it on record on purpose until they are done.
function vendorSummary() {
  const keep = srvKeepEnv();
  return S.versions.map((v) => {
    const f = srvFacts(v.id);
    const lines = [f && f.muipKey === 'vendor' && (keep ? T('server.adv.muipVendorKept') : T('server.adv.muipVendor')),
      f && f.mysqlPassword === 'vendor' && (keep ? T('server.adv.mysqlVendorKept') : T('server.adv.mysqlVendor')),
      f && f.flaskKey === 'vendor' && (keep ? T('server.adv.flaskVendorKept') : T('server.adv.flaskVendor')),
      f && f.internalPassword === 'vendor' && (keep ? T('server.adv.internalVendorKept') : T('server.adv.internalVendor')),
      f && f.internalPassword === 'pending' && !versionJobRunning(v.id) && T('server.adv.internalPending')].filter(Boolean);
    return lines.length ? `<div class="note" style="margin:0 0 10px"><b>${T('common.versionN', { id: v.id })}</b><br>${lines.join('<br>')}</div>` : '';
  }).join('');
}
function secretsCard() {
  const s = S.secrets;
  const versions = S.versions.map((v) => v.id);
  const cur = s.version || versions[0] || '';
  const tabs = versions.map((id) => `<button class="lg-chip ${cur === id ? 'on' : ''}" data-act="secretsVersion" data-arg="${esc(id)}">${T('common.versionN', { id })}</button>`).join('');
  const d = s.data && s.version === cur ? s.data : null;
  const field = (key, label, sub) => `<div><label class="fld">${label}</label><div class="flex gap"><input class="txt mono" type="password" data-model="secrets.form.${key}" value="${esc(s.form[key] || '')}" placeholder="${esc(d && d[key] ? t('secrets.keep') : '')}" autocomplete="new-password"><button class="btn ghost" style="padding:8px 12px;font-size:12px" data-act="secretsGen" data-arg="${key}">${T('deploy.net.generate')}</button></div><small class="hint">${sub}</small></div>`;
  const warn = (text) => `<span style="color:var(--warn);font-weight:800">${text}</span>`;
  // The MySQL root password / the Flask key / the stack internal password is still the archive's
  // published one — said in the warning colour under its field (the MUIP line says it in its own
  // words), with what becomes of it: replaced at the next Prepare server / Start, or kept as it is by an
  // agent that keeps .env settings (keep) until one is entered here. Only an answered `true` draws it:
  // an agent that sends no flag says nothing.
  const keep = srvKeepEnv();
  const vendorNote = (flag, text) => (flag === true ? ' ' + warn(text) : '');
  // The internal password may instead be in the middle of a change that did not finish
  // (`internalPending`, the same rule): the next Start of the version completes it (Prepare server
  // while it is not installed yet) — said in place of the vendor note (the agent never calls such a
  // password vendor, and finishing the change is what replaces a vendor one). Not while a job of the
  // version runs (versionJobRunning): its fresh Prepare or Secrets rotation keeps the change on record
  // until it is done, and the values are re-read when a job this launcher follows ends.
  const internalNote = (x) => (x.internalPending === true ? (versionJobRunning(cur) ? '' : ' ' + warn(T('secrets.internalPending')))
    : vendorNote(x.internalVendorDefault, keep ? T('secrets.internalVendorKept') : T('secrets.internalVendor')));
  return `<div class="sect"><h4>${T('secrets.title')}</h4><p class="hint">${T('secrets.hint')}</p>${vendorSummary()}
    <div class="flex gap wrap" style="margin-bottom:12px">${tabs}</div>
    ${!d ? `<button class="btn ghost" style="padding:8px 14px;font-size:12px" data-act="secretsLoad" data-arg="${esc(cur)}">${s.loading ? T('common.checking') : T('secrets.load')}</button>` : `
    <div class="grid2">
      ${field('mysqlRoot', T('secrets.mysqlRoot'), T('secrets.mysqlRootSub') + vendorNote(d.mysqlRootVendorDefault, keep ? T('secrets.mysqlRootVendorKept') : T('secrets.mysqlRootVendor')))}
      ${field('internal', T('secrets.internal'), T('secrets.internalSub') + internalNote(d))}
      ${field('flask', T('secrets.flask'), T('secrets.flaskSub') + vendorNote(d.flaskVendorDefault, keep ? T('secrets.flaskVendorKept') : T('secrets.flaskVendor')))}
      ${field('muip', T('secrets.muip'), esc(muipKeyText(d.muip)))}
    </div>
    <div class="note" style="margin-top:12px">${d.bootstrapped ? T('secrets.restartWarning') : T('secrets.freshNote')}</div>
    <div class="flex between center" style="margin-top:12px"><button class="btn ghost" style="padding:8px 12px;font-size:12px" data-act="secretsShow" data-arg="${esc(cur)}">${T('secrets.show')}</button><button class="btn" data-act="secretsApply" data-arg="${esc(cur)}" ${srvBusy() ? 'disabled' : ''}>${T('secrets.apply')}</button></div>`}
  </div>`;
}

// The Content tab's "Server packages": per catalogue version where its server is — on the server, the package the
// agent can download (size, format, host, the folder it goes into, what unpacks it, a part already on the box, the
// disk it needs or the agent's refusal of it), NOT SET UP (no folder: "Add version…"), or why there is none — with the
// download's own button (fetchButton, the Overview row's too) and "Change folder…" beside it, and Stop while this
// launcher follows that download here.
function packagesCard() {
  const rows = S.versions.map((v) => {
    const st = srvState(v.id), f = srvFacts(v.id), fe = f && f.fetch, j = S.srvJob;
    const mine = jobHere() && j.kind === 'fetch' && String(j.version) === String(v.id);
    // more = the lines under the first one: the agent's refusal of a download, the disk it needs, why the folder
    // cannot be set from here (each already escaped, with its class).
    let text, action = '';
    const more = [];
    if (!f) text = st === 'unlisted' ? T('server.pkg.unlisted') : T('server.adv.stateUnknown');
    else if (f.present) text = T('server.pkg.present', { dir: f.dir || '—' });
    // (read off the facts, not the state: while the re-point of "Add version…" runs the row reads WORKING above it, and
    // the version still has no folder to offer a download into)
    else if (f.configured === false) {
      text = T('server.pkg.notSetUp');
      action = addVersionButton(v.id);
      if (fe && fe.available && !fe.tool) more.push(['warn', T('server.note.noExtractor')]);
      const block = folderBlockText(); if (block) more.push(['warn', block]);
    } else if (mine) {
      text = T('server.pkg.running');
      action = `<button class="btn ghost" data-act="fetchCancel" data-arg="${esc(v.id)}" ${j.cancelSent ? 'disabled' : ''}>${T('server.card.fetchCancel')}</button>`;
    } else if (!fe) text = T('server.pkg.old', { dir: f.dir });
    else if (!fe.available) text = T('server.pkg.unknownPkg', { dir: f.dir });
    else {
      const formats = Array.isArray(fe.formats) ? fe.formats.map((x) => '.' + x).join(' / ') : '';
      const part = Math.max(0, Number(fe.part)) || 0, problem = fetchProblem(fe);
      // (no tool on the box: said by the agent's own refusal when it reports one, else here)
      text = [T('server.pkg.offer', { size: sizeGb(fe.size), kind: fe.kind ? '.' + fe.kind : formats || '—', host: fe.host || '—', dir: f.dir }),
        fe.archive === true ? T('server.pkg.archive') : part > 0 ? T('server.pkg.part', { have: sizeGb(part) }) : '',
        !fe.tool ? (problem ? '' : T('server.note.noExtractor')) : fe.tool === 'zipfile' ? T('server.pkg.toolSelf') : T('server.pkg.tool', { tool: fe.tool })].filter(Boolean).join(' · ');
      if (problem) more.push(['bad', esc(problem)]);
      else if (isBytes(fe.need)) more.push([fetchShort(fe) ? 'bad' : '', fetchDiskText(fe)]);
      const block = folderBlockText(); if (block) more.push(['warn', block]);
      action = `<div class="pkgacts">${fetchButton(v.id, fe)}${changeFolderButton(v.id)}</div>`;
    }
    return `<div class="line pkg"><div style="min-width:0"><b>${T('common.versionN', { id: v.id })}</b><small style="overflow-wrap:anywhere">${text}</small>${
      more.map(([cls, html]) => `<small class="${cls}" style="overflow-wrap:anywhere">${html}</small>`).join('')}</div>${action}</div>`;
  }).join('');
  return `<div class="sect"><h4>${T('server.pkg.title')}</h4><p class="hint">${T('server.pkg.hint')}</p>${S.serverConfigured ? rows : noServerLine()}</div>`;
}
// The Content tab's "Server folders": where each version's server lives on the box (the agent's own config read,
// S.agentCfg — the Agent settings card's data) and Change… = the folder dialog (agentCfgReloc, relocOverlay); beside it
// Add… for a version without a folder (the dialog of "Add version…") and Download for a folder that holds no server yet
// (the package download's question, fetchServer). The catalogue's versions first, then any other the agent knows. Dead
// while a job runs, during a restart and while the agent cannot write its config file (the note says why; Download
// writes no configuration and needs only a package the agent can fetch); a version this agent does not list gets no
// folder from it.
function foldersCard() {
  const c = S.agentCfg, d = c.data;
  const head = `<h4>${T('agentCfg.dirs.title')}</h4><p class="hint">${T('agentCfg.dirs.hint')}</p>`;
  if (!S.serverConfigured) return `<div class="sect">${head}<p class="hint" style="margin:0">${T('server.agent.notConfigured')}</p></div>`;
  if (!d) return `<div class="sect">${head}${c.loading ? `<p class="hint" style="margin:0">${T('common.checking')}</p>` : `${c.error ? `<div class="note" style="margin-bottom:10px">${T('agentCfg.loadFailed', { error: c.error })}</div>` : ''}<button class="btn ghost" style="padding:8px 14px;font-size:12px" data-act="agentCfgLoad">${T('common.retry')}</button>`}</div>`;
  if (d.tooOld) return `<div class="sect">${head}<div class="note">${T('agentCfg.tooOld')}</div></div>`;
  const busy = srvBusy() || c.restarting;
  const why = !d.writable && d.file ? `<div class="note" style="margin-bottom:10px">${T('agentCfg.readOnly')}${d.writableReason ? `<br><small class="mono">${esc(d.writableReason)}</small>` : ''}</div>`
    : !d.file ? `<div class="note" style="margin-bottom:10px">${T('agentCfg.noFile')}</div>` : '';
  const ids = S.versions.map((v) => v.id);
  Object.keys(d.versions && typeof d.versions === 'object' ? d.versions : {}).forEach((id) => { if (!ids.includes(id)) ids.push(id); });
  const rows = ids.map((id) => {
    const vi = agentCfgVersion(id), unlisted = srvUnlisted(id) || versionsLack(d.versions, id);
    const st = !vi.configured ? 'none' : vi.up ? 'running' : vi.present ? 'present' : 'missing';
    const color = unlisted ? 'var(--muted)' : st === 'running' ? 'var(--ok)' : st === 'missing' ? 'var(--warn)' : 'var(--muted)';
    const small = 'class="btn ghost" style="padding:8px 12px;font-size:12px;flex:none"';
    const extra = st === 'none' ? `<button ${small} data-act="addVersion" data-arg="${esc(id)}" ${busy || !d.writable || unlisted ? 'disabled' : ''}>${T('agentCfg.dir.add')}</button>`
      : st === 'missing' ? `<button ${small} data-act="fetchServer" data-arg="${esc(id)}" ${unlisted || !fetchReady(id) ? 'disabled' : ''}>${T('agentCfg.dir.download')}</button>` : '';
    return `<div class="line"><div style="min-width:0"><b>${T('common.versionN', { id })}</b>
        <small class="${vi.dir ? 'mono' : ''}" style="word-break:break-all">${vi.dir ? esc(vi.dir) : T('agentCfg.dir.none')}</small>
        <small style="color:${color};font-weight:800">${unlisted ? T('server.adv.stateUnlisted') : T(AGENT_DIR_STATES[st])}</small></div>
      <div class="flex gap" style="flex:none">${extra}<button ${small} data-act="agentCfgReloc" data-arg="${esc(id)}" ${busy || !d.writable || unlisted ? 'disabled' : ''}>${T('agentCfg.dir.change')}</button></div></div>`;
  }).join('');
  return `<div class="sect">${head}${why}${rows}</div>`;
}

// ── admin: pathfinding server (per version) ──
// pathfindingserver computes the routes monsters and NPCs walk and needs about 4 GB of RAM of its own:
// on a small box the kernel kills it and restarts it in a loop, and the whole server slows down. The
// agent excludes it through the compose profile `donotstart` — the vendor's own way, used on oaserver
// — on BOTH compose files, and stops / starts the container on a running stack. The row shows the
// file's truth; null (older agent, an absent stack, a foreign compose) asserts nothing and is dead.
function pathfindingCard() {
  const busy = srvBusy();
  const rows = S.versions.map((v) => {
    const f = srvFacts(v.id);
    const on = !!(f && f.pathfinding);
    const sub = !f || f.pathfinding === null ? T('server.pathfinding.unknown') : on ? T('server.pathfinding.onSub') : T('server.pathfinding.offSub');
    const dis = busy || !f || !f.present || f.pathfinding === null ? 'disabled' : '';
    return `<div class="line"><div><b>${T('common.versionN', { id: v.id })}</b><small>${sub}</small></div>
      <button class="toggle ${on ? 'on' : ''}" data-act="pathfindingToggle" data-arg="${esc(v.id)}" ${dis}><span class="knob"></span></button></div>${navdataLine(v, f, busy)}${navmeshLine(v, f, busy)}`;
  }).join('');
  return `<div class="sect"><h4>${T('server.pathfinding.title')}</h4><p class="hint">${T('server.pathfinding.hint')}</p>${rows}</div>`;
}

// A job of `kind` for THIS version runs ON THIS SERVER: the one this launcher follows (jobHere), or the
// one /status names (started elsewhere, or ours after the link was lost).
const versionKindRunning = (kind, id) => {
  const j = S.srvJob, b = S.srv.info && S.srv.info.busy;
  if (jobHere() && j.kind === kind && String(j.version) === String(id)) return true;
  return !!(b && b.kind === kind && String(b.version) === String(id));
};
// A navmesh download of this version, or a download of its pathfinding data.
const navmeshJobRunning = (id) => versionKindRunning('navmesh', id);
const navdataJobRunning = (id) => versionKindRunning('navdata', id);
// A count the agent reports: a whole number of 0 or more, whatever arrived.
const countOf = (n) => Math.max(0, Math.floor(Number(n)) || 0);
// The pathfinding server's own data of a version as the agent reports it (navmesh.data of the admin
// /status: {size, host, files, missing, missingVendor, part, archive, tool} — the archive its catalogue
// names and how many of the catalogue's files the stack's server/res lacks), or null: an agent that does
// not send it, a version without such a catalogue, a stack the agent does not have.
const navdataOf = (f) => {
  const d = f && f.navmesh && f.navmesh.data;
  return d && typeof d === 'object' && !Array.isArray(d) ? d : null;
};
// Nothing for the pathfinding server to load on the version's stack: no navmesh file in the stack's own
// folder (an answered 0 — an agent that does not send the count says nothing) and no bundle the agent
// counted (a counted one lifts it: a Prepare and the switch both install it). The Prepare dialog leaves
// the service off for such a stack, and switching it on says so in its question.
const pfNothingToLoad = (f) => !!(f && f.navmesh && f.navmesh.stackFiles === 0 && !(Number(f.navmesh.bundled) > 0));
// The navmesh files Relic adds for the scenes the vendor's set lacks reach this server no other way: no
// bundle on the agent (an answered 0 — one it could not count lifts nothing) and no navmesh archive in the
// catalogue for the version.
const navmeshNoSource = (v, f) => {
  const n = f && f.navmesh;
  return !!n && n.bundled !== null && n.bundled !== undefined && !(Number(n.bundled) > 0) && !(v.navmesh && v.navmesh.url);
};
// The data line is on the card: the stack lacks files of the vendor's own set — or files Relic adds that
// nothing else brings (navmeshNoSource) —, or their download runs.
const navdataShown = (v, f) => {
  const d = navdataOf(f);
  return !!d && (navdataJobRunning(v.id) || countOf(d.missingVendor) > 0 || (countOf(d.missing) > 0 && navmeshNoSource(v, f)));
};
// The data line offers its download: it is drawn, nothing of it runs, and the server has a tool that
// unpacks the archive. The navmesh line below then offers no archive of its own, and the Prepare dialog
// points at this one.
const navdataOffered = (v, f) => { const d = navdataOf(f); return !!d && navdataShown(v, f) && !navdataJobRunning(v.id) && d.tool !== null; };
// The pathfinding server's own data under the version's toggle row — drawn only from what the agent
// reports (navdataOf) and only while the stack lacks files of the vendor's set (missingVendor), or files
// Relic adds that neither a bundle on the agent nor the catalogue's navmesh archive brings (the cheaper
// way otherwise: the navmesh line below): how many are missing, and the button that has the agent
// download the archive and add them. A server without a tool to unpack the archive (tool null) gets that
// said instead of a button; bytes a stopped download kept (part) are continued from; an archive that is
// already complete on the box (archive) is put in place without a download. A running download of this
// version is said in place of the rest.
function navdataLine(v, f, busy) {
  const d = navdataOf(f);
  if (!d || !navdataShown(v, f)) return '';
  let text, button = '';
  if (navdataJobRunning(v.id)) text = T('server.navdata.downloading');
  else {
    text = T('server.navdata.missing', { missing: countOf(d.missing), files: countOf(d.files) });
    if (d.tool === null) text += ` ${T('server.navdata.noTool')}`;
    else {
      const part = countOf(d.part);
      const label = d.archive === true ? T('server.navdata.install')
        : part > 0 ? T('server.navdata.resume', { have: mb(part), mb: mb(d.size) }) : T('server.navdata.download', { mb: mb(d.size) });
      button = `<button class="btn ghost" style="padding:8px 12px;font-size:12px;white-space:nowrap" data-act="navdataFetch" data-arg="${esc(v.id)}" ${busy ? 'disabled' : ''}>${label}</button>`;
    }
  }
  return `<div class="line" style="padding-left:18px"><div><small>${text}</small></div>${button}</div>`;
}
// The navmesh files of the version under its toggle row — the scenes the server's own set lacks, which
// the pathfindingserver loads at its boot. Drawn only from what the agent reports (f.navmesh: null = an
// older agent or an absent stack, nothing is said; `bundled` null = the agent could not read its own
// state, nothing is said either — a 0 it never counted must not read as "missing") and from the
// catalogue (v.navmesh: the archive the agent can download, or null). The "monsters have no routes"
// sentence is about THAT archive, so it is drawn only when the catalogue names one for the version
// (the vendor set of a version the catalogue leaves alone — 1.6 and its archipelago — is complete as
// far as the launcher knows: a neutral line then). The agent counts `bundled` from its own payload
// folder and `installed` from the stack, so "N files on the server" with fewer installed means they
// wait for the version's next Start (or the pathfinding enable, which installs them too). A bundle
// adopted from a zip whose sha256 is not the catalogue's is an older release: the button offers the
// update next to the state (a bundle the installer shipped reports no archive — nothing is offered,
// the files may well be the same). A running download of this version is said in place of the state.
// While the pathfinding data line above it offers its download or that download runs (navdataOffered),
// that line is the one sentence about what the service lacks and its download the one offered: this one
// says nothing of a package without navmesh files or without a list, and offers no archive of its own —
// the pathfinding data also carries the navmesh files Relic adds for the scenes the vendor's set lacks
// (the files of its catalogue that are not the vendor's). A data line without a button (no tool on the
// server to unpack its archive) leaves this line as it would be without it: the navmesh archive is a zip
// the agent reads itself. Once every file of that data is in the stack (navdataOf: missing 0), those
// scenes have their files: without a bundle on the agent the line says so and offers nothing either —
// whether the catalogue's archive is a later build of the same files cannot be told from here (as with a
// bundle the installer shipped).
function navmeshLine(v, f, busy) {
  const n = f && f.navmesh;
  if (!n || n.bundled === null || n.bundled === undefined) return '';
  const bundled = Number(n.bundled) || 0, installed = Number(n.installed) || 0;
  const cat = v.navmesh && v.navmesh.url ? v.navmesh : null;
  const dataLine = navdataShown(v, f) && (navdataJobRunning(v.id) || navdataOffered(v, f));
  // Every file of the pathfinding data is in the stack — only an answered 0 counts, like stackFiles below.
  const d = navdataOf(f);
  const dataComplete = !!d && d.missing === 0;
  const btn = (key) => `<button class="btn ghost" style="padding:8px 12px;font-size:12px;white-space:nowrap" data-act="navmeshFetch" data-arg="${esc(v.id)}" ${busy ? 'disabled' : ''}>${T(key, { mb: mb(cat.size) })}</button>`;
  // Nothing at all: no bundle on the agent and no navmesh file in the stack's own folder (stackFiles,
  // which an agent may not send — only an answered 0 counts). ONE sentence then: "runs on the server
  // package's own set" beside "the package has no list" would say two opposite things.
  const empty = bundled <= 0 && n.stackFiles === 0;
  let text, button = '';
  if (navmeshJobRunning(v.id)) text = T('server.navmesh.downloading');
  else if (bundled <= 0) {
    if (dataLine) {
      if (empty) return '';
      text = T('server.navmesh.noBundle');
    }
    else if (dataComplete) text = T('server.navmesh.dataComplete');
    else {
      text = empty ? T('server.navmesh.empty') : T(cat ? 'server.navmesh.none' : 'server.navmesh.noBundle');
      if (cat) button = btn('server.navmesh.download');
    }
  }
  else {
    text = installed < bundled
      ? T('server.navmesh.pending', { n: bundled, id: v.id })
      : T('server.navmesh.installed', { n: bundled, scenes: (Array.isArray(n.scenes) ? n.scenes : []).join(', ') || '—' });
    const adopted = n.archive && typeof n.archive === 'object' ? String(n.archive.sha256 || '').toLowerCase() : '';
    if (cat && adopted && adopted !== String(cat.sha256).toLowerCase()) {
      text += ` ${T('server.navmesh.newer')}`;
      button = btn('server.navmesh.update');
    }
  }
  const noList = !empty && !dataLine && n.vendorList === false ? ` ${T('server.navmesh.noList')}` : '';
  return `<div class="line" style="padding-left:18px"><div><small>${text}${noList}</small></div>${button}</div>`;
}

// ── admin: the hotpatch mirror of the official in-version fixes (per version) ──
// The agent mirrors the official client hotfix outputs and makes the dispatch advertise them, so every
// client hotpatches itself at login the way the official client does. Read-only here except
// the two confirmed jobs; the numbers come from GET /server/hotpatch (never inferred).
// Decimal MB (/1e6), the same convention as the agent's job log and the docs (127.1 MB for 1.6).
const mb = (n) => (Math.max(0, Number(n) || 0) / 1e6).toFixed(1);
// Decimal GB (/1e9), one digit: the voice packs are 3–10 GB per language (the agent's log says the same).
// An amount under 0.05 GB is not printed as "0.0" (what a stopped run leaves on 1.6 is a few tens of MB).
const gb = (n) => { const v = Math.max(0, Number(n) || 0) / 1e9; return v > 0 && v < 0.05 ? Math.max(v, 0.01).toFixed(2) : v.toFixed(1); };
// "1.9 GB" — decimal GB with the unit from the dictionary: one form for every size the server pages show.
const sizeGb = (n) => t('common.sizeGb', { gb: gb(n) });
// One shape for every reset: boot, another server (srvRetarget), an agent install (deploy.done).
function hotpatchBlank() { return { version: null, data: null, loading: false, voiceSel: null, voiceKey: '', busy: null, voiceStop: null }; }
// The tab the card shows — and the only version a finished job may re-read (never a tab switch).
const hotpatchCur = () => S.hotpatch.version || S.selectedVersionId || (S.versions[0] && S.versions[0].id) || '';
// The loaded reply, but only as THIS version's: loadHotpatch names the tab before the reply is in, and
// the agent's payload says which version it describes (one that does not say is taken at its word).
const hotpatchDataOf = (id) => {
  const h = S.hotpatch, d = h.data;
  return d && h.version === id && (d.version == null || String(d.version) === String(id)) ? d : null;
};

// ── voice packs of the hotpatch mirror ──
// Once real revisions are advertised the client checks every pack of the voice language it USES (on a
// fresh profile: the Windows display language) and downloads a missing one from the mirror. Those are
// packs of the BASE build (3–10 GB per language), which the agent fetches only for the languages the
// admin selected, and serves only those — a pack already cached included (one of a language that is not
// selected stays on disk, unserved) —, while the version's hotpatch is enabled. When an agent sends no `voice`,
// nothing is drawn.
const hotpatchVoiceOf = (d) => (d && d.available && d.voice && d.voice.base && Array.isArray(d.voice.languages) && d.voice.languages.length ? d.voice : null);
// The manifest's word, passed on as GET /server/hotpatch `voice.shortWait`: the version's game does not wait long
// enough for a big pack the server still has to fetch when the game asks for it, so its languages are mirrored
// FIRST — said under the on-demand note, in the Enable question and in the voice job's question. Read from the
// answer, never from the version id; an agent that sends no such key says nothing.
const voiceShortWait = (d) => !!(d && d.voice && typeof d.voice === 'object' && d.voice.shortWait === true);
// The per-file source of a version's mirror as the texts name it: the host names of its manifest's upstreams
// (GET /server/hotpatch `upstreamHosts`, in their order — the official CDN, or a public copy of its files where the
// CDN does not hold them), "<host> then <host>" the way the agent's job lines name them; neutral words when the
// agent names none. The agent's text: drawn through T() / the dialog's escaping like every other value.
const hotpatchHosts = (d) => {
  const hosts = d && Array.isArray(d.upstreamHosts) ? d.upstreamHosts.filter((h) => typeof h === 'string' && h.trim()).map((h) => h.trim()) : [];
  return hosts.length ? hosts.reduce((a, b) => t('hotpatch.hostsThen', { a, b })) : t('hotpatch.hostsNone');
};
// Where the server fills its mirror from first ('archive' | 'cdn'): the policy on screen — the Mirror source card
// writes it the moment it is clicked — else what the hotpatch answer said (`mirrorSource`).
const hotpatchMirrorSource = (d) => {
  const pol = S.policy.data;
  return pol && (pol.hotpatchSource === 'archive' || pol.hotpatchSource === 'cdn') ? pol.hotpatchSource : d && d.mirrorSource;
};
// Whether a job of this version may take its files from an archive.org bundle: the manifest names one (GET
// /server/hotpatch `bundles.hotpatch` for the fix files, `bundles.voice.<Lang>` for a voice language — null or
// absent = filled file by file), the server's switch is on and it has a tool to unpack it, and the mirror source is
// the Internet Archive. The agent's own rule decides the rest (enough missing, enough disk): "when it can".
const hotpatchBundleUsable = (d, lang) => {
  const b = d && d.bundles && typeof d.bundles === 'object' ? d.bundles : null;
  if (!b || b.enabled !== true || !b.extractor || hotpatchMirrorSource(d) === 'cdn') return false;
  const spec = lang == null ? b.hotpatch : hasOwn(b.voice, lang) ? b.voice[lang] : null;
  return !!(spec && typeof spec === 'object');
};
// A voice job of this version runs ON THIS SERVER: the one this launcher follows (jobHere — never one
// it started on another server before the address was changed), or the one /status names (started
// elsewhere, or ours after the link was lost — the agent's kind is 'hotpatch-voice').
const voiceJobRunning = (id) => {
  const j = S.srvJob, b = S.hotpatch.busy;
  if (jobHere() && j.kind === 'hotpatch.voice' && String(j.version) === String(id)) return true;
  return !!(b && b.kind === 'hotpatch-voice' && b.version === String(id) && S.srv.info && S.srv.info.busy);
};
// Either hotpatch job of this version running ON THIS SERVER: the Enable / "Fetch missing files" run
// ('hotpatch') and the voice packs run ('hotpatch.voice'; the agent's own kind is 'hotpatch-voice').
// Both spend nearly all their time downloading files, which is exactly what this card counts —
// refreshHotpatchQuiet follows them.
const hotpatchJobRunning = (id) => {
  const j = S.srvJob, b = S.hotpatch.busy;
  if (jobHere() && (j.kind === 'hotpatch' || j.kind === 'hotpatch.voice') && String(j.version) === String(id)) return true;
  return !!(b && b.version === String(id) && S.srv.info && S.srv.info.busy);
};
// The Enable / Disable run of THIS version, as the State line has to show it: an Enable flips the
// agent's stored state only once every file is fetched (minutes), so until then a card that just
// read the agent would keep saying "Disabled" over a run that is enabling it — as if the button had
// done nothing. Ours carries the intent it was started with (runServerJob); one started elsewhere
// (/status busy) does not say which way it goes, so it is only named as a run.
const hotpatchApplyJob = (id) => {
  const j = S.srvJob, b = S.hotpatch.busy;
  if (jobHere() && j.kind === 'hotpatch' && String(j.version) === String(id))
    return j.enabled === true ? 'enabling' : j.enabled === false ? 'disabling' : 'working';
  return b && b.kind === 'hotpatch' && b.version === String(id) && S.srv.info && S.srv.info.busy ? 'working' : '';
};
const HOTPATCH_APPLY = { enabling: 'hotpatch.stateEnabling', disabling: 'hotpatch.stateDisabling', working: 'hotpatch.stateWorking' };
// The followed voice job belongs to ANOTHER server: this card draws neither its running note nor a
// Stop (the cancel call goes to the configured host = this one) — it says where that job lives.
const voiceJobElsewhere = () => jobElsewhere() && S.srvJob.kind === 'hotpatch.voice';
// The server's rows against the admin's ticks — one reading for the section and its confirm dialogs.
// Names and dates in here are the agent's text: escaped where they are drawn.
function voicePlan(d) {
  const v = hotpatchVoiceOf(d);
  if (!v) return null;
  const sel = S.hotpatch.voiceSel;
  const langs = v.languages.filter((l) => l && typeof l.name === 'string' && l.name).map((l) => {
    const files = Number(l.files) || 0, cached = Number(l.cached) || 0, selected = !!l.selected;
    // diskBytes: EVERYTHING in the language's folder, a stopped run's .part files included
    // — what the job's purge deletes and what the agent credits for it. cachedBytes counts complete
    // packs only and stands in for an agent that does not send diskBytes.
    const cachedBytes = Number(l.cachedBytes) || 0, disk = Number(l.diskBytes);
    return {
      name: l.name, files, cached, bytes: Number(l.bytes) || 0, cachedBytes,
      diskBytes: l.diskBytes != null && Number.isFinite(disk) ? Math.max(disk, cachedBytes) : cachedBytes,
      selected, ticked: sel ? sel[l.name] === true : selected, full: files > 0 && cached >= files,
      requested: Number(l.requested) || 0, requestedAt: typeof l.requestedAt === 'string' ? l.requestedAt : '',
      // Where the last mirror run took this language's packs from ('bundle' = the archive.org bundle,
      // 'cdn' = file by file from the upstreams, 'bundle+cdn'); '' when the agent names none.
      source: typeof l.source === 'string' ? l.source : '',
    };
  });
  if (!langs.length) return null;
  const ticked = langs.filter((l) => l.ticked);
  // What the job's `purge` deletes: every unticked language with anything on disk — complete packs or
  // only the .part a stopped run left (invisible in the rows, and this job is the only way to delete it).
  const loose = langs.filter((l) => !l.ticked && l.diskBytes > 0);
  const download = ticked.reduce((n, l) => n + Math.max(0, l.bytes - l.cachedBytes), 0);
  const looseBytes = loose.reduce((n, l) => n + l.diskBytes, 0);
  // Free space against the agent's rule (a run must leave `reserve` free, else 409). The agent credits
  // the purge only when it was ASKED for, so there are two verdicts: `hard` = does not fit even with
  // the purge, `needsPurge` = fits only with the purge box ticked. A heads-up only — the agent's own
  // count is the authority (it credits half-fetched files).
  const free = typeof v.free === 'number' ? v.free : null, reserve = Math.max(0, Number(v.reserve) || 0);
  const hard = free != null && download > 0 && free + looseBytes < download + reserve;
  const needsPurge = free != null && download > 0 && !hard && free < download + reserve;
  // Selected, not complete on disk: the server fetches such a pack when a player's game asks for it — only
  // while the version's hotpatch is enabled, a fallback, not a plan (voiceShortWait) — and not at all with
  // on-demand fetching switched off (d.upstream === false: the mirror answers a plain 404 until the mirror job
  // has run).
  const incomplete = langs.filter((l) => l.selected && !l.full);
  return {
    v, langs, ticked, loose, download, looseBytes, free, reserve, hard, needsPurge,
    changed: langs.some((l) => l.ticked !== l.selected),
    diskBytes: langs.reduce((n, l) => n + l.diskBytes, 0), // what Disable + purge deletes with the branch
    // Not selected: the mirror serves none of its packs, whether or not they are complete on disk (I52) —
    // a language kept on disk and not selected is named here too.
    unserved: langs.filter((l) => !l.selected),
    unfetched: d.upstream === false ? incomplete : [],
    onDemand: d.upstream === false ? [] : incomplete,
  };
}
// The ticks follow the server's selection when the server / version changes and when the selection
// itself changed there (a finished job, another admin); a background refresh of the same version with
// the same selection keeps the admin's unsaved ticks. voiceKey = '' makes the next load re-take them.
function syncVoiceSel(id, d, target) {
  const h = S.hotpatch, v = hotpatchVoiceOf(d);
  if (!v) { h.voiceSel = null; h.voiceKey = ''; return; }
  const rows = v.languages.filter((l) => l && typeof l.name === 'string' && l.name);
  const key = [target == null ? srvTarget() : target, id, JSON.stringify(rows.filter((l) => l.selected).map((l) => l.name))].join('|');
  if (h.voiceSel && h.voiceKey === key) return;
  h.voiceKey = key; h.voiceSel = {};
  rows.forEach((l) => { h.voiceSel[l.name] = !!l.selected; });
}
function hotpatchVoiceSection(cur, d) {
  const p = voicePlan(d);
  if (!p) return '';
  const v = p.v, busy = srvBusy();
  const rows = p.langs.map((l) => {
    // Complete on disk but not selected: kept there, not served (the mirror serves selected languages only).
    const st = l.full && l.selected ? ['hotpatch.voice.stateMirrored', 'var(--ok)']
      : l.full ? ['hotpatch.voice.stateKept', 'var(--muted)']
      : l.cached > 0 ? ['hotpatch.voice.statePartial', 'var(--warn)']
      : l.selected ? (d.upstream === false ? ['hotpatch.voice.stateSelectedOff', 'var(--bad)'] : ['hotpatch.voice.stateSelected', 'var(--warn)'])
      : ['hotpatch.voice.stateNone', 'var(--muted)'];
    // Requests the mirror REFUSED since the agent started: a player needs this language.
    const asked = l.requested > 0 && !l.selected
      ? `<small style="color:var(--bad);font-weight:800">${T(l.requestedAt ? 'hotpatch.voice.requested' : 'hotpatch.voice.requestedNoTime', { n: l.requested, at: l.requestedAt })}</small>` : '';
    return `<div class="line"><div><b>${esc(l.name)}</b><small>${T('hotpatch.voice.rowSub', { gb: gb(l.bytes), cached: l.cached, total: l.files })}${l.source === 'bundle' ? ` · ${T('hotpatch.voice.fromBundle')}` : ''}</small>${asked}</div>
      <div class="flex gap center"><span style="font-weight:800;font-size:12px;text-align:right;color:${st[1]}">${T(st[0])}</span><button class="toggle ${l.ticked ? 'on' : ''}" data-act="hotpatchVoiceToggle" data-arg="${esc(l.name)}" ${busy ? 'disabled' : ''}><span class="knob"></span></button></div></div>`;
  }).join('');
  const note = (html) => `<div class="note" style="margin-top:10px">${html}</div>`;
  const names = (list) => list.map((l) => l.name).join(', ');
  const freeLine = p.free == null ? '' : `<p class="hint" style="margin:10px 0 0">${T(p.reserve > 0 ? 'hotpatch.voice.free' : 'hotpatch.voice.freeOnly', { free: gb(p.free), reserve: gb(p.reserve) })}</p>`;
  // The agent refuses (409) a run that would eat into its reserve — see voicePlan for the two verdicts.
  const space = { need: gb(p.download), free: gb(p.free), reserve: gb(p.reserve), langs: names(p.loose), gb: gb(p.looseBytes) };
  const noSpace = p.hard ? note(T('hotpatch.voice.noSpace', space)) : p.needsPurge ? note(T('hotpatch.voice.needsPurge', space)) : '';
  const notIndexed = !v.indexed ? `<p class="hint" style="margin:10px 0 0">${T('hotpatch.voice.notIndexed')}</p>` : '';
  const unserved = d.enabled && p.unserved.length ? note(T('hotpatch.voice.unserved', { langs: names(p.unserved) })) : '';
  // Selected but not complete on the box, while clients are being sent here: refused outright with
  // on-demand fetching off, left to the on-demand fallback otherwise (fetched from the version's upstreams —
  // which the game of a version whose manifest says voice.shortWait does not wait for).
  const unfetched = d.enabled && p.unfetched.length ? note(T('hotpatch.voice.unfetched', { langs: names(p.unfetched) })) : '';
  const onDemand = d.enabled && p.onDemand.length
    ? note(T('hotpatch.voice.onDemand', { langs: names(p.onDemand), hosts: hotpatchHosts(d) }) + (voiceShortWait(d) ? ' ' + T('hotpatch.voice.shortWait') : '')) : '';
  // The voice job this launcher follows runs on another server: why every button here is dead, and
  // where that job can be stopped (voiceJobElsewhere) — instead of a running note + Stop of this card.
  const elsewhere = voiceJobElsewhere()
    ? note(T('hotpatch.voice.followedElsewhere', { host: S.srvJob.host || '—', version: S.srvJob.version })) : '';
  // Nothing to do = the ticks are the server's selection, every selected language is complete and no
  // unticked language has anything left on disk (this job is also the only way to delete that).
  const idle = !p.changed && !p.loose.length && p.langs.every((l) => !l.selected || l.full);
  // While this version's voice job runs, the (dead) Mirror button gives way to Stop: one click, then
  // dead too until the job's state changes (S.hotpatch.voiceStop).
  const running = voiceJobRunning(cur), stopSent = S.hotpatch.voiceStop === cur;
  const todo = running ? T('hotpatch.voice.running')
    : [p.changed ? T('hotpatch.voice.changed') : '', p.download > 0 ? T('hotpatch.voice.todo', { gb: gb(p.download) }) : ''].filter(Boolean).join(' · ');
  const action = running
    ? `<button class="btn ghost" data-act="hotpatchVoiceStop" data-arg="${esc(cur)}" ${stopSent ? 'disabled' : ''}>${T(stopSent ? 'hotpatch.voice.stopping' : 'hotpatch.voice.stop')}</button>`
    : `<button class="btn" data-act="hotpatchVoice" data-arg="${esc(cur)}" ${busy || idle ? 'disabled' : ''}>${T('hotpatch.voice.mirror')}</button>`;
  return `<div style="margin-top:18px;padding-top:16px;border-top:1px solid color-mix(in srgb, var(--goldD) 18%, transparent)">
    <h4>${T('hotpatch.voice.title')}</h4><p class="hint">${T('hotpatch.voice.hint')}</p>
    <div>${rows}</div>${freeLine}${notIndexed}${noSpace}${unserved}${unfetched}${onDemand}${elsewhere}
    <div class="flex between center" style="margin-top:12px"><span class="hint" style="margin:0">${todo}</span>${action}</div></div>`;
}
// Where the last Enable run took the fix files from (GET /server/hotpatch `source`): only
// the words the agent defines are drawn — an unknown one (a newer agent) shows nothing rather than a
// missing-key label.
// The two ways the server can fill the hotpatch mirror, as the admin chooses between them in this card
// (hotpatchMirrorSection): the Internet Archive's bundles first — the default, one pinned download instead of a
// request per file — or every file on its own from the upstreams each version's manifest names ('cdn', drawn as
// "File by file": the official CDN, or a public copy of its files where the CDN does not hold them). The value is
// the agent's policy field `hotpatchSource`, not a setting of this launcher.
const HOTPATCH_MIRRORS = {
  archive: { label: 'hotpatch.mirror.archive', sub: 'hotpatch.mirror.archiveDesc' },
  cdn: { label: 'hotpatch.mirror.cdn', sub: 'hotpatch.mirror.cdnDesc' },
};

// Where the SERVER fills its mirror from -- the fixes and the voice packs alike. It is the agent's
// policy, saved on the box the moment it is clicked (not with the launcher's own "Save settings"),
// which is why it lives in this card and not in Settings: it belongs to the server the card shows.
// An agent that has no such field leaves neither option marked, and a click reports
// whatever that agent answers.
function hotpatchMirrorSection() {
  if (!isAdminMode() || !S.serverConfigured) return '';
  const pol = S.policy.data;
  const body = !pol
    ? `<p class="hint" style="margin:0 0 10px">${S.policy.loading ? T('common.checking') : T('policy.unavailable')}</p>
      <button class="btn ghost" style="padding:8px 14px;font-size:12px" data-act="policyLoad" ${S.policy.loading ? 'disabled' : ''}>${T('common.retry')}</button>`
    : `<div class="grid2">${Object.keys(HOTPATCH_MIRRORS).map((id) => `<div class="opt ${pol.hotpatchSource === id ? 'sel' : ''}" data-act="setHotpatchSource" data-arg="${id}">
        <div class="h">${T(HOTPATCH_MIRRORS[id].label)}</div><p>${T(HOTPATCH_MIRRORS[id].sub)}</p></div>`).join('')}</div>
      <p class="hint" style="margin:10px 0 0">${T('hotpatch.mirror.hint')}</p>`;
  return `<div style="margin-top:18px;padding-top:16px;border-top:1px solid color-mix(in srgb, var(--goldD) 18%, transparent)">
    <h4>${T('hotpatch.mirror.title')}</h4>${body}</div>`;
}

const HOTPATCH_FILL = { bundle: 'hotpatch.source.bundle', cdn: 'hotpatch.source.cdn', 'bundle+cdn': 'hotpatch.source.bundle+cdn', cached: 'hotpatch.source.cached' };
function hotpatchCard() {
  const h = S.hotpatch;
  const versions = S.versions.map((v) => v.id);
  const cur = hotpatchCur();
  const tabs = versions.map((id) => `<button class="lg-chip ${cur === id ? 'on' : ''}" data-act="hotpatchVersion" data-arg="${esc(id)}">${T('common.versionN', { id })}</button>`).join('');
  const d = hotpatchDataOf(cur);
  const dis = srvBusy() ? 'disabled' : '';
  const fld = 'color:var(--muted);font-weight:800;font-size:10.5px';
  let body;
  if (!d) {
    body = `<p class="hint">${!S.serverConfigured ? T('server.agent.notConfigured') : h.loading ? T('common.checking') : T('hotpatch.noData')}</p>
      <button class="btn ghost" style="padding:8px 14px;font-size:12px" data-act="hotpatchLoad" data-arg="${esc(cur)}" ${h.loading ? 'disabled' : ''}>${T('hotpatch.load')}</button>`;
  } else if (!d.available) {
    body = `<p class="hint" style="margin:0">${T('hotpatch.unavailable')}</p>`;
  } else {
    const files = d.files || {};
    const total = Number(files.total) || 0, cached = Number(files.cached) || 0;
    // `problem` = the agent cannot derive a mirror URL players could reach (loopback listener, no
    // advertised IP, a URL over 128 chars): an enabled mirror in that state is NOT advertised and
    // stays pending until the address is fixed — a distinct label, not "pending restart".
    const blocked = d.enabled && typeof d.problem === 'string' && d.problem.trim() !== '';
    // A running Enable / Disable of this version owns the line: what the agent has stored is the
    // state from BEFORE that run until it has fetched (or reverted) everything — hotpatchApplyJob.
    const applying = hotpatchApplyJob(cur);
    // "Fetch missing files" is that same 'hotpatch' job on a mirror that is ALREADY enabled: it
    // switches nothing on, so it must not read as if the mirror were off for its duration.
    const applyKey = applying === 'enabling' && d.enabled ? 'hotpatch.stateRefetching' : HOTPATCH_APPLY[applying];
    const state = applying ? T(applyKey)
      : d.enabled ? (blocked ? T('hotpatch.stateBlocked') : d.pending ? T('hotpatch.statePending') : T('hotpatch.stateEnabled')) : T('hotpatch.stateDisabled');
    const stateColor = applying ? 'var(--warn)'
      : d.enabled ? (blocked ? 'var(--bad)' : d.pending ? 'var(--warn)' : 'var(--ok)') : 'var(--muted)';
    const rev = (k) => (d[k] && d[k].revision) != null ? String(d[k].revision) : '—';
    const problem = blocked ? `<div class="note" style="margin-top:12px">${T('hotpatch.problem', { problem: d.problem })}</div>` : '';
    // A missing file is fetched once from the manifest's upstreams when a client asks for it — only while the
    // version's fixes are enabled — unless the admin switched that off (d.upstream === false): only then would a
    // client fail on a file missing from the server.
    const partial = d.enabled && !d.complete ? `<div class="note" style="margin-top:12px">${d.upstream === false
      ? T('hotpatch.incompleteNoUpstream', { cached, total }) : T('hotpatch.incomplete', { cached, total, hosts: hotpatchHosts(d) })}</div>` : '';
    // The agent fills the mirror from the archive.org bundles first and falls back to the manifest's
    // upstreams file by file; without a 7z/tar tool on the box the bundles cannot be unpacked — said here,
    // so the admin knows why an Enable is slower and what to install.
    const source = HOTPATCH_FILL[d.source] ? `<div><small style="${fld}">${T('hotpatch.sourceLabel')}</small><div style="font-weight:800">${T(HOTPATCH_FILL[d.source])}</div></div>` : '';
    // ... and only while bundles are actually in play: the manifest names one (the fix files' or a voice
    // language's) and the mirror source is not File by file — otherwise no archive is ever unpacked, so asking
    // for a 7z tool would be noise.
    const bundles = d.bundles && typeof d.bundles === 'object' ? d.bundles : null;
    const bundled = !!bundles && (!!(bundles.hotpatch && typeof bundles.hotpatch === 'object')
      || (!!bundles.voice && typeof bundles.voice === 'object' && Object.keys(bundles.voice).length > 0));
    const noExtractor = bundled && bundles.enabled && !bundles.extractor && hotpatchMirrorSource(d) !== 'cdn'
      ? `<div class="note" style="margin-top:12px">${T('hotpatch.noExtractor', { hosts: hotpatchHosts(d) })}</div>` : '';
    // A manifest whose upstream is a third party's copy of the official outputs (`listedOnly`): the mirror takes
    // from it, and serves, only what the fix list and the verified release index name.
    const listed = d.listedOnly === true ? `<p class="hint" style="margin:0 0 12px">${T('hotpatch.listedOnly', { hosts: hotpatchHosts(d) })}</p>` : '';
    body = `<div class="flex gap wrap" style="margin:4px 0 14px">
      <div><small style="${fld}">${T('hotpatch.stateLabel')}</small><div style="font-weight:800;color:${stateColor}">${state}</div></div>
      <div><small style="${fld}">${T('hotpatch.cachedLabel')}</small><div style="font-weight:800">${T('hotpatch.cached', { cached, total, mb: mb(files.cachedBytes), totalMb: mb(files.bytes) })}</div></div>
      <div><small style="${fld}">${T('hotpatch.revisionsLabel')}</small><div style="font-weight:800" class="mono">${T('hotpatch.revisions', { res: rev('res'), data: rev('data'), silence: rev('silence') })}</div></div>
      ${source}
      ${d.url ? `<div style="min-width:0"><small style="${fld}">${T('hotpatch.urlLabel')}</small><div style="font-weight:800;word-break:break-all" class="mono">${esc(d.url)}</div></div>` : ''}
      ${d.appliedAt ? `<div><small style="${fld}">${T('hotpatch.appliedLabel')}</small><div style="font-weight:800" class="mono">${esc(d.appliedAt)}</div></div>` : ''}
    </div>${listed}${problem}${partial}${noExtractor}
    <div class="flex between center" style="margin-top:12px"><button class="btn ghost" style="padding:8px 12px;font-size:12px" data-act="hotpatchLoad" data-arg="${esc(cur)}" ${h.loading ? 'disabled' : ''}>${T('hotpatch.refresh')}</button>
      <div class="flex gap">${d.enabled && !d.complete
        ? `<button class="btn" data-act="hotpatchRefetch" data-arg="${esc(cur)}" ${dis}>${T('hotpatch.refetch')}</button>` : ''}
      ${d.enabled
        ? `<button class="btn ghost danger" data-act="hotpatchDisable" data-arg="${esc(cur)}" ${dis}>${T('hotpatch.disable')}</button>`
        : `<button class="btn" data-act="hotpatchEnable" data-arg="${esc(cur)}" ${dis}>${T('hotpatch.enable')}</button>`}</div></div>${hotpatchVoiceSection(cur, d)}`;
  }
  return `<div class="sect"><h4>${T('hotpatch.title')}</h4><p class="hint">${T('hotpatch.hint')}</p>
    <div class="flex gap wrap" style="margin-bottom:12px">${tabs}</div>
    ${body}${hotpatchMirrorSection()}
  </div>`;
}

// ── admin: the agent's own settings ──
// GET /agent/config = the KEY=VALUE configuration the agent runs with, as a list of typed settings
// (value = what the process uses, stored = the file's line or null, default), plus the stack folder of
// each version. POST /agent/config {set} writes the file on the box — the agent keeps its comments and
// every other line — and applies the "live" keys at once; the "restart" keys wait for POST
// /agent/restart (agent.restart, which the backend follows until the agent answers again). Values
// travel as strings: a switch "1"/"0", a size in MiB, '' = the line is removed and the default applies
// again (the placeholder shows it). A key an environment variable fixes is drawn disabled and never
// sent. An agent without the route answers 404, which the backend returns as {tooOld: true}.
// One shape for every reset: boot, another server (srvRetarget), an agent install (deploy.done).
// form = every field as drawn ({KEY: string}; data-model keys hold no dots), open = the settings groups
// unfolded, restartRequired = the restart keys the last Save wrote (only for an agent that does not
// report restartPending), reloc = the folder dialog of one version (agentCfgReloc) or null.
function agentCfgBlank() { return { data: null, loading: false, error: '', form: {}, saving: false, restarting: false, open: false, restartRequired: [], reloc: null }; }
const AGENT_CFG_GROUPS = ['general', 'network', 'storage', 'downloads'];
// Label tables (translation keys, translated when drawn): a folder's state on the settings card, the
// folder dialog's three ways (card title / description / what a running server goes through / the Yes
// label) and the agent's word on the target folder (server.relocate.check `target`).
const AGENT_DIR_STATES = { running: 'agentCfg.dir.state.running', present: 'agentCfg.dir.state.present', missing: 'agentCfg.dir.state.missing', none: 'agentCfg.dir.state.none' };
const RELOC_MODES = {
  move: { label: 'agentCfg.reloc.move', sub: 'agentCfg.reloc.moveSub', running: 'agentCfg.reloc.running.move', yes: 'agentCfg.reloc.yes.move' },
  repoint: { label: 'agentCfg.reloc.repoint', sub: 'agentCfg.reloc.repointSub', running: 'agentCfg.reloc.running.repoint', yes: 'agentCfg.reloc.yes.repoint' },
  remove: { label: 'agentCfg.reloc.remove', sub: 'agentCfg.reloc.removeSub', running: 'agentCfg.reloc.running.remove', yes: 'agentCfg.reloc.yes.remove' },
};
const RELOC_TARGETS = { absent: 'agentCfg.reloc.target.absent', empty: 'agentCfg.reloc.target.empty', stack: 'agentCfg.reloc.target.stack', other: 'agentCfg.reloc.target.other' };
// Only keys setModel can walk to (a dotted key would split the path): a newer agent's odd key is left out.
const agentCfgSettings = () => {
  const d = S.agentCfg.data;
  return d && Array.isArray(d.settings) ? d.settings.filter((s) => s && typeof s.key === 'string' && /^[A-Za-z0-9_]+$/.test(s.key)) : [];
};
const agentCfgGroup = (s) => (typeof s.group === 'string' && s.group ? s.group : 'general');
// The agent's own truthy and falsy words (it normalises what it writes to 1/0, a hand-edited file may
// say "yes" — or "", or "enabled"). It reads a switch two ways, by its default: a default-ON switch is
// on unless the line says 0/false/no/off (_flag_on), a default-OFF one only for 1/true/yes/on (_flag_off).
const agentCfgTrue = (v) => ['1', 'true', 'yes', 'on'].includes(String(v == null ? '' : v).trim().toLowerCase());
const agentCfgFalse = (v) => ['0', 'false', 'no', 'off'].includes(String(v == null ? '' : v).trim().toLowerCase());
// A field as it is drawn before any edit: what the FILE says ('' = no line, the default applies); a
// switch has no "no line" position, so its effective 1/0 — the file's line read the agent's way, else
// the default. A key an environment variable fixes shows what the process uses.
function agentCfgBase(s) {
  if (s.env) return String(s.value == null ? '' : s.value);
  if (s.type === 'bool') {
    const raw = s.stored != null ? s.stored : s.default;
    return (String(s.default) === '1' ? !agentCfgFalse(raw) : agentCfgTrue(raw)) ? '1' : '0';
  }
  return s.stored == null ? '' : String(s.stored);
}
const agentCfgField = (s) => (hasOwn(S.agentCfg.form, s.key) ? String(S.agentCfg.form[s.key]) : agentCfgBase(s));
// What Save sends: the fields that differ from the file (trimmed — the agent strips them anyway).
const agentCfgChanged = () => agentCfgSettings().filter((s) => !s.env && agentCfgField(s).trim() !== agentCfgBase(s).trim());
// A reply replaces the data and re-seeds the fields from it. keepEdits (a background re-read: a folder
// job settled, the Server page opened again): what the admin changed and has not saved stays typed.
function agentCfgApply(d, keepEdits) {
  const c = S.agentCfg, edits = {};
  if (keepEdits) agentCfgChanged().forEach((s) => { edits[s.key] = agentCfgField(s); });
  c.data = d && typeof d === 'object' ? d : null;
  c.form = {};
  agentCfgSettings().forEach((s) => { c.form[s.key] = hasOwn(edits, s.key) && !s.env ? edits[s.key] : agentCfgBase(s); });
}
const agentCfgLabel = (key) => { const k = `agentCfg.key.${key}`; return I18N.has(k) ? t(k) : key; };
const agentCfgChoice = (key, ch) => (I18N.has(`agentCfg.enum.${key}.${ch}`) ? t(`agentCfg.enum.${key}.${ch}`) : ch);
// The restart keys the running agent does not use yet. The agent's restartPending (stored or default ≠
// what the process runs with) is the truth and survives a reload — also the reply of a Save, whose
// restartRequired would still name a key saved back to the value the process already uses; that list
// only stands in for an agent that does not report restartPending.
function agentCfgPending() {
  const c = S.agentCfg, d = c.data || {};
  const list = Array.isArray(d.restartPending) ? d.restartPending : Array.isArray(c.restartRequired) ? c.restartRequired : [];
  return list.filter((k, i) => typeof k === 'string' && k && list.indexOf(k) === i);
}
// One version's folder on the agent: the folder and "configured" from the config read (the agent's
// VERSIONS), present / running from the live status poll when there is one — a download or a Prepare
// changes them long after the config was read.
function agentCfgVersion(id) {
  const d = S.agentCfg.data;
  const cv = d && d.versions && typeof d.versions === 'object' && d.versions[id] && typeof d.versions[id] === 'object' ? d.versions[id] : null;
  const f = srvFacts(id), live = S.srv.state === 'ok' && !!f;
  const dir = cv && typeof cv.dir === 'string' ? cv.dir : (f && f.dir) || '';
  const configured = cv ? !!cv.configured && !!dir : !!(f && f.configured && dir);
  const present = configured && (live ? f.present : !!(cv && cv.present));
  return { dir, configured, present, up: present && (live ? f.up : !!(cv && cv.up)) };
}
function agentCfgRow(s, locked) {
  const key = s.key, v = agentCfgField(s), dis = locked || s.env ? 'disabled' : '';
  const subKey = `agentCfg.key.${key}.sub`;
  const sub = I18N.has(subKey) ? `<small>${T(subKey)}</small>` : '';
  // An optional longer explanation (agentCfg.key.<KEY>.help — only the public IP has one): the "?"
  // badge right after the label, its bubble laid out against the row's text half (.helpbox).
  const helpKey = `agentCfg.key.${key}.help`;
  const help = I18N.has(helpKey) ? helpBadge(t(helpKey)) : '';
  const badge = s.apply === 'restart' ? `<span class="acfg-badge">${T('agentCfg.restartBadge')}</span>` : '';
  const env = s.env ? `<small style="color:var(--warn);font-weight:800">${T('agentCfg.envOverride')}</small>` : '';
  const running = String(s.value == null ? '' : s.value);
  const waiting = s.apply === 'restart' && !s.env && agentCfgPending().includes(key)
    ? `<small style="color:var(--warn);font-weight:800">${T('agentCfg.pendingValue', { value: running || t('agentCfg.valueDefault') })}</small>` : '';
  // The default greyed in an empty field; without one (a derived default: the host name, a folder
  // beside the state file) what the process resolved it to, as long as the file does not set it.
  const ph = String(s.default == null ? '' : s.default) || (s.stored == null ? running : '');
  const model = `data-model="agentCfg.form.${key}"`;
  let ctl;
  if (s.type === 'bool') {
    ctl = `<button class="toggle ${v === '1' ? 'on' : ''}" data-act="agentCfgToggle" data-arg="${esc(key)}" ${dis}><span class="knob"></span></button>`;
  } else if (s.type === 'enum') {
    const choices = (Array.isArray(s.choices) ? s.choices : []).map(String);
    if (v && !choices.includes(v)) choices.push(v); // a hand-edited value outside the list: shown as it is
    const dflt = String(s.default == null ? '' : s.default);
    ctl = `<select class="txt acfg-in" ${model} ${dis}><option value=""${v === '' ? ' selected' : ''}>${T('agentCfg.enumDefault', { choice: dflt ? agentCfgChoice(key, dflt) : '—' })}</option>${
      choices.map((ch) => `<option value="${esc(ch)}"${ch === v ? ' selected' : ''}>${esc(agentCfgChoice(key, ch))}</option>`).join('')}</select>`;
  } else if (s.type === 'mib' || s.type === 'seconds') {
    // A text field, not type=number: '' has to mean "remove the line", and a number input reports a
    // half-typed "12a" as '' too. Save refuses anything but digits; the agent checks the range.
    ctl = `<input class="txt mono acfg-in acfg-num" ${model} value="${esc(v)}" placeholder="${esc(ph)}" inputmode="numeric" autocomplete="off" spellcheck="false" ${dis}><span class="acfg-unit">${s.type === 'mib' ? T('agentCfg.unit.mib') : T('agentCfg.unit.seconds')}</span>`;
  } else {
    ctl = `<input class="txt mono acfg-in" ${model} value="${esc(v)}" placeholder="${esc(ph)}" autocomplete="off" spellcheck="false" ${dis}>`;
  }
  return `<div class="line"><div${help ? ' class="helpbox"' : ''} style="min-width:0"><b>${esc(agentCfgLabel(key))}</b>${help}${badge}${sub}${env}${waiting}</div><div class="acfg-ctl">${ctl}</div></div>`;
}
// The Agent tab's "This server's agent": which agent answers and how it runs — its version, platform and start, its
// build against the one this launcher ships, where it listens, how it checks HTTPS — and the update offer
// (agentCfgUpdateRow). The admin /status alone (version, platform, build) while GET /agent/config is not there.
function agentInfoCard() {
  const head = `<h4>${T('agentCfg.infoTitle')}</h4>`;
  if (!S.serverConfigured) return `<div class="sect">${head}<p class="hint" style="margin:0">${T('server.agent.notConfigured')}</p></div>`;
  const c = S.agentCfg, d = c.data && !c.data.tooOld ? c.data : null;
  const info = S.srv.info && !S.srv.info.public ? S.srv.info : null;
  if (!d && !info) {
    const wait = c.loading || S.srv.state === 'checking';
    return `<div class="sect">${head}${wait ? `<p class="hint" style="margin:0">${T('common.checking')}</p>` : c.error ? `<div class="note">${T('agentCfg.loadFailed', { error: c.error })}</div>` : ''}${agentCfgUpdateRow()}</div>`;
  }
  const fld = 'color:var(--muted);font-weight:800;font-size:10.5px';
  const started = d && Number(d.started) > 0 ? new Date(Number(d.started) * 1000).toLocaleString() : '';
  const tls = d && d.tls && typeof d.tls === 'object' ? d.tls : null;
  // The extra roots: the file only when one is set; one the agent could not load (caFileError: its
  // reason) is said in the warning colour — the downloads then run on the system store alone.
  const caFile = tls && typeof tls.caFile === 'string' ? tls.caFile.trim() : '';
  const caErr = tls && typeof tls.caFileError === 'string' ? tls.caFileError.trim() : '';
  const caText = caErr ? `<span style="color:var(--warn)">${T('agentCfg.tls.caFileBad', { file: caFile || String((agentCfgSettings().find((s) => s.key === 'GIO_CA_FILE') || {}).value || '').trim() || '?', why: caErr })}</span>`
    : caFile ? T('agentCfg.tls.caFile', { file: caFile }) : '';
  const tlsText = tls ? [tls.strict ? T('agentCfg.tls.strict') : T('agentCfg.tls.verified'), tls.pinnedFallback ? T('agentCfg.tls.fallbackOn') : T('agentCfg.tls.fallbackOff'),
    caText].filter(Boolean).join(' · ') : '';
  const hosts = tls && Array.isArray(tls.fallbackHosts) ? tls.fallbackHosts.filter((h) => typeof h === 'string' && h) : [];
  // The build the agent names in its status (the first characters of its identity) and whether it is
  // the one this launcher ships — drawn once that status is read, and only by a build that ships an agent.
  const build = info && typeof info.build === 'string' ? info.build : '';
  const buildFact = !info || !S.agentBuild.shipped ? '' : `<div><small style="${fld}">${T('agentCfg.build')}</small><div style="font-weight:800">${build
    ? `<span class="mono">${esc(build.slice(0, 10))}</span> · ${agentBuildDiffers(build) ? `<span style="color:var(--warn)">${T('agentCfg.buildOther')}</span>` : T('agentCfg.buildSame')}`
    : `<span style="color:var(--warn)">${T('agentCfg.buildNone')}</span>`}</div></div>`;
  const agent = (d && d.agent) || (info && info.agent) || '?', platform = (d && d.platform) || (info && info.platform) || '';
  return `<div class="sect">${head}<div class="flex gap wrap" style="row-gap:10px;margin-bottom:4px">
      <div><small style="${fld}">${T('agentCfg.agent')}</small><div style="font-weight:800">${esc(agent)}${platform ? ` · ${esc(platform)}` : ''}${started ? ` · ${T('agentCfg.since', { time: started })}` : ''}</div></div>
      ${buildFact}
      ${d && d.listen ? `<div><small style="${fld}">${T('agentCfg.listen')}</small><div style="font-weight:800" class="mono">${esc(d.listen)}</div></div>` : ''}
      ${tlsText ? `<div><small style="${fld}">${T('agentCfg.tls')}</small><div style="font-weight:800">${tlsText}</div></div>` : ''}
    </div>${hosts.length ? `<div class="note" style="margin-top:10px">${T('agentCfg.tlsFallbackHosts', { hosts: hosts.join(', ') })}</div>` : ''}${agentCfgUpdateRow()}</div>`;
}
function agentCfgCard() {
  const c = S.agentCfg, d = c.data;
  const head = `<h4>${T('agentCfg.title')}</h4><p class="hint">${T('agentCfg.hint')}</p>`;
  if (!S.serverConfigured) return `<div class="sect acfg">${head}<p class="hint" style="margin:0">${T('server.agent.notConfigured')}</p></div>`;
  if (!d) {
    return `<div class="sect acfg">${head}${c.loading ? `<p class="hint" style="margin:0">${T('common.checking')}</p>` : `
      ${c.error ? `<div class="note" style="margin-bottom:10px">${T('agentCfg.loadFailed', { error: c.error })}</div>` : ''}
      <button class="btn ghost" style="padding:8px 14px;font-size:12px" data-act="agentCfgLoad">${T('common.retry')}</button>`}</div>`;
  }
  if (d.tooOld) return `<div class="sect acfg">${head}<div class="note">${T('agentCfg.tooOld')}</div></div>`;
  // A restart in flight holds the edits too: the agent is going away under them.
  const busy = srvBusy() || c.restarting, locked = !d.writable || c.saving || c.restarting;
  const settings = agentCfgSettings(), changed = agentCfgChanged(), pending = agentCfgPending();
  const fld = 'color:var(--muted);font-weight:800;font-size:10.5px';
  // Which file the agent edits, and why nothing can be saved when it cannot.
  const file = d.file ? (d.writable ? `<span class="mono">${esc(d.file)}</span>` : T('agentCfg.fileReadOnly', { file: d.file })) : T('agentCfg.fileNone');
  const fileFact = `<div style="min-width:0;margin-bottom:4px"><small style="${fld}">${T('agentCfg.file')}</small><div style="font-weight:800;word-break:break-all">${file}</div></div>
    ${!d.writable && d.file ? `<div class="note" style="margin-top:10px">${T('agentCfg.readOnly')}${d.writableReason ? `<br><small class="mono">${esc(d.writableReason)}</small>` : ''}</div>`
      : !d.file ? `<div class="note" style="margin-top:10px">${T('agentCfg.noFile')}</div>` : ''}`;
  // Saved but not in use yet: the restart, from here when the agent can do it itself (a restart
  // refuses while any job runs — the agent's rule, mirrored by the dead button).
  const restart = !pending.length ? '' : `<div class="note" style="margin-top:12px;display:flex;align-items:center;justify-content:space-between;gap:12px">
      <span>${T('agentCfg.restartNote', { keys: pending.map(agentCfgLabel).join(', ') })}${d.restart ? '' : ` ${T('agentCfg.restartManual')}`}</span>
      ${d.restart ? `<button class="btn" style="padding:8px 14px;font-size:12px;flex:none" data-act="agentRestart" ${busy || c.restarting ? 'disabled' : ''}>${c.restarting ? T('agentCfg.restarting') : T('agentCfg.restart')}</button>` : ''}</div>`;
  // The settings, folded by default (the Agent tab holds other cards too): one section per group, in the agent's order.
  const groups = AGENT_CFG_GROUPS.concat(settings.map(agentCfgGroup).filter((g, i, a) => !AGENT_CFG_GROUPS.includes(g) && a.indexOf(g) === i));
  const body = !c.open ? '' : groups.map((g) => {
    const rows = settings.filter((s) => agentCfgGroup(s) === g);
    if (!rows.length) return '';
    const gk = `agentCfg.group.${g}`;
    return `<h5 class="acfg-group">${I18N.has(gk) ? T(gk) : esc(g)}</h5>${rows.map((s) => agentCfgRow(s, locked)).join('')}`;
  }).join('');
  const fold = settings.length ? `<div style="margin-top:16px"><button class="btn ghost" style="padding:9px 14px;font-size:12px" data-act="agentCfgOpen">${c.open ? '▾' : '▸'} ${c.open ? T('agentCfg.hideSettings') : T('agentCfg.showSettings', { n: settings.length })}</button></div>` : '';
  // Unsaved edits keep the bar on screen after the groups are folded again.
  const saveBar = !(c.open || changed.length) ? '' : `<div class="flex between center" style="margin-top:14px;gap:12px">
      <span class="hint" style="margin:0" id="acfg-unsaved">${changed.length ? T('agentCfg.unsaved', { n: changed.length }) : ''}</span>
      <button class="btn" data-act="agentCfgSave" ${locked || !changed.length ? 'disabled' : ''}>${c.saving ? T('agentCfg.saving') : T('agentCfg.save')}</button></div>`;
  return `<div class="sect acfg">${head}${fileFact}${restart}${fold}${body}${saveBar}</div>`;
}
// Typing does not re-render (the caret would go): the Save button and its count follow in place.
function syncAgentCfgSave() {
  const c = S.agentCfg, n = agentCfgChanged().length;
  const b = document.querySelector('[data-act="agentCfgSave"]');
  if (b) b.disabled = !n || c.saving || c.restarting || !(c.data && c.data.writable);
  const u = document.getElementById('acfg-unsaved');
  if (u) u.textContent = n ? t('agentCfg.unsaved', { n }) : '';
}

// The folder dialog of one version (agentCfgReloc; POST /server/relocate). Three ways, as option cards:
// move the stack (it must be there — renamed on the same drive, copied and verified on another), point
// the agent at another folder (nothing is moved: a server already there, or an empty folder to download
// into), or take the version off the agent (a re-point to no folder, only for a configured version).
// The same dialog adds a version the agent has no folder for ("Add version…", flow 'add') and gives a configured
// version whose folder holds no server yet another one ("Change folder…", flow 'change'): two ways, both a re-point —
// download the package into the folder (the re-point, then the download, one after the other: chainAdd) or use a
// package already extracted there.
// The agent checks exactly what is on screen (server.relocate.check, a dry run — debounced while
// typing) and Start stays dead until that check said ok; the job re-validates everything under its lock.
// r = {version, from, dir, mode: 'move'|'repoint'|'remove' — 'download'|'use' in a flow —, flow: 'add'|'change'|undefined,
// check: {ui (the mode it ran for), dir, mode, res, error} | null, checking, soon (a debounced check is due), seq,
// at (the arming window of the confirm dialog)}.
function relocModes(r) {
  if (r.flow) {
    // A download is offered only for a version the agent knows a package of, and taken only on a server that can unpack
    // that package (the status names the tool a download there would use; none = no 7z tool for a .7z-only package).
    const fe = (srvFacts(r.version) || {}).fetch;
    return (fe && fe.available ? [{ id: 'download', dis: !fe.tool }] : []).concat([{ id: 'use', dis: false }]);
  }
  const vi = agentCfgVersion(r.version);
  const out = [{ id: 'move', dis: !vi.present }, { id: 'repoint', dis: false }];
  if (vi.configured) out.push({ id: 'remove', dis: false });
  return out;
}
const relocModeOk = (r) => relocModes(r).some((o) => o.id === r.mode && !o.dis);
// What the agent is asked: "remove" is a re-point to no folder, and both ways of a flow are a re-point to the folder.
const relocQuery = (r) => (r.mode === 'remove' ? { version: r.version, dir: '', mode: 'repoint' } : { version: r.version, dir: String(r.dir || '').trim(), mode: r.flow ? 'repoint' : r.mode });
// A check stands for exactly what is on screen: the same query AND the same option card (ui) — "remove"
// and a re-point with a blank field ask the agent the same thing, and a remove verdict must never
// unlock "Use this folder" (which would take the version off the agent). Outside "remove" a folder
// has to be typed. The two ways of a flow ask the agent the same re-point: one verdict serves both.
const relocCheckFits = (r, q, ch) => !!ch && (!!r.flow || ch.ui === r.mode) && ch.dir === q.dir && ch.mode === q.mode;
// The agent's figures for a download into the dialog's folder (its check's `download` block) refuse it: its own
// sentence, or a drive with less free than the download needs.
const relocDownloadRefused = (b) => !!b && typeof b === 'object'
  && ((typeof b.problem === 'string' && !!b.problem.trim()) || (isBytes(b.need) && isBytes(b.free) && b.free < b.need));
function relocStartOk(r) {
  const q = relocQuery(r), ch = r.check;
  if (!(relocModeOk(r) && (r.mode === 'remove' || !!q.dir) && !srvBusy() && !r.checking && !r.soon && relocCheckFits(r, q, ch)
    && !!ch.res && ch.res.ok === true && !ch.res.problem)) return false;
  // A flow goes on only while the version still uses the folder the dialog was opened on (relocFromNow).
  if (r.flow && relocFromNow(r) !== null) return false;
  // "Use a server package already there" takes a folder that holds one.
  if (r.flow && r.mode === 'use') return ch.res.target === 'stack';
  // A download goes only into a folder that holds no server, and only while the agent's figures let it start.
  return r.mode !== 'download' || (ch.res.target !== 'stack' && !relocDownloadRefused(ch.res.download));
}
// The folder the version of a flow uses by now, when the agent's check of the dialog's folder (its `from`) or the
// latest status names another one than the dialog was opened on — another admin, another launcher or gio_agent.py
// --relocate set it meanwhile: '' = it has none by now, else that folder; null = they agree, or nothing names it. Start
// stays dead then (relocStartOk): the version is not the one the admin chose to add or move.
function relocFromNow(r) {
  if (!r || !r.flow) return null;
  const win = srvWindows(), fits = (dir) => (r.from ? sameFolder(dir, r.from, win) : !String(dir || '').trim());
  const ch = r.check;
  const said = ch && ch.res && relocCheckFits(r, relocQuery(r), ch) && typeof ch.res.from === 'string' ? ch.res.from.trim() : null;
  if (said !== null && !fits(said)) return said;
  const at = chainDirOf(r.version);
  return at !== null && !fits(at) ? at : null;
}
// The Start label of the dialog (plain text: drawn escaped, and set in place as textContent by syncReloc).
function relocYesText(r) {
  if (!r.flow) return t(RELOC_MODES[r.mode].yes);
  const size = sizeGb(relocDownloadSize(r));
  if (r.flow === 'add') return r.mode === 'download' ? t('addver.yesDownload', { size }) : t('addver.yesUse');
  return r.mode === 'download' ? t('addver.yesChangeDownload', { size }) : t('addver.yesChangeUse');
}
// The archive a download into the dialog's folder takes: the one the agent's check of that folder names, else the
// status' figure.
function relocDownloadSize(r) {
  const ch = r.check, b = ch && ch.res && ch.res.download, fe = (srvFacts(r.version) || {}).fetch;
  if (b && isBytes(b.size) && relocCheckFits(r, relocQuery(r), ch)) return b.size;
  return fe && isBytes(fe.size) ? fe.size : 0;
}
// What a download left beside the current folder, as the status names it (`fetch`: its .part, a complete archive), for
// an agent whose check reports no `carry`: such an agent leaves both where they are. [{name, size}].
function relocLeftBehind(from, fe) {
  if (!from || !fe) return [];
  const base = String(from).replace(/[\\/]+$/, '').split(/[\\/]/).pop();
  const kind = fe.kind || (Array.isArray(fe.formats) && fe.formats[0]) || '';
  if (!base || !kind) return [];
  const part = Math.max(0, Number(fe.part)) || 0, out = [];
  if (fe.archive === true) out.push({ name: `${base}.${kind}`, size: fe.size });
  if (part > 0) out.push({ name: `${base}.${kind}.part`, size: part });
  return out;
}
// The check of "Add version…" / "Change folder…": what stands at the folder, what a download into it needs and what
// unpacks it (the agent's `download` block — an agent that reports none checks the free space when the download
// starts), and what stands beside the current folder: what follows the version (`carry`), what stays — and, from an
// agent that reports no `carry`, what this status names there, which stays and is not continued.
function relocFlowHtml(r, res, line) {
  // A version that uses another folder by now than the dialog was opened on: that is the whole answer (Start is dead).
  const moved = relocFromNow(r);
  if (moved !== null) return line(moved ? T('addver.fromChanged', { version: r.version, from: moved }) : T('addver.fromGone', { version: r.version }), 'var(--bad)');
  const out = [], dl = r.mode === 'download', b = res.download && typeof res.download === 'object' ? res.download : null;
  if (res.target === 'absent') out.push(line(T('agentCfg.reloc.target.absentRepoint')));
  else if (res.target === 'empty') out.push(line(T('agentCfg.reloc.target.empty')));
  else if (res.target === 'stack') out.push(line(T('agentCfg.reloc.target.stack')) + (dl ? line(T('addver.targetStack'), 'var(--bad)') : ''));
  else if (res.target === 'other') out.push(line(T('agentCfg.reloc.target.other')));
  // (a package already there needs a folder that holds one)
  if (!dl && res.target !== 'stack') out.push(line(T('addver.useNoServer'), 'var(--bad)'));
  if (res.running) out.push(line(T('agentCfg.reloc.running.repoint'), 'var(--warn)'));
  if (dl && res.target !== 'stack') {
    if (b) {
      if (isBytes(b.need)) {
        const size = isBytes(b.size) ? b.size : 0, unpacked = isBytes(b.extractedSize) && b.extractedSize > 0 ? b.extractedSize : 3 * size;
        const reserve = isBytes(b.reserve) ? b.reserve : 0, rest = Math.max(0, b.need - unpacked - reserve);
        const parts = { need: sizeGb(b.need), size: sizeGb(rest), unpacked: sizeGb(unpacked), reserve: sizeGb(reserve) };
        out.push(isBytes(b.free) ? line(T('addver.disk', Object.assign(parts, { free: sizeGb(b.free) })), b.free < b.need ? 'var(--bad)' : '')
          : line(T('addver.diskNoFree', parts)));
      }
      if (b.tool) out.push(line(b.tool === 'zipfile' ? T('addver.toolZip') : T('addver.tool7z', { tool: String(b.tool) })));
      const p = typeof b.problem === 'string' ? b.problem.trim() : '';
      if (p) out.push(line(esc(p), 'var(--bad)'));
    } else out.push(line(T('addver.noBlock')));
  }
  // (what follows the version only on a way the admin can take: the download, or a package already there)
  if (dl || res.target === 'stack') out.push(relocCarryHtml(r, res, line));
  if (relocStartOk(r)) out.push(line(T('agentCfg.reloc.ok'), 'var(--ok)'));
  return out.join('');
}
// What a re-point away from a folder that holds no server does with what a download left beside that folder: what
// follows the version (`carry`, fits), what stays because the new drive has no room for it — and, from an agent that
// reports no `carry`, what this status names there, which stays and is not continued ({list} = "<name> (<size>)" items).
function relocCarryHtml(r, res, line) {
  const items = (list) => list.map((e) => `${e.name} (${sizeGb(e.size)})`).join(', ');
  if (Array.isArray(res.carry)) {
    const go = res.carry.filter((e) => e && typeof e.name === 'string' && e.fits), stay = res.carry.filter((e) => e && typeof e.name === 'string' && !e.fits);
    return (go.length ? line(T('addver.carried', { list: items(go) })) : '') + (stay.length ? line(T('addver.carriedNoRoom', { list: items(stay) }), 'var(--warn)') : '');
  }
  const left = relocLeftBehind(r.from, (srvFacts(r.version) || {}).fetch);
  return left.length ? line(T('addver.leftBehind', { list: items(left) }), 'var(--warn)') : '';
}
function relocCheckHtml(r) {
  const q = relocQuery(r), ch = r.check, remove = r.mode === 'remove';
  const line = (html, color) => `<p style="margin:6px 0 0;font-size:12.5px;font-weight:700;line-height:1.5;color:${color || 'var(--inkSoft)'}">${html}</p>`;
  if (!remove && !q.dir) return line(T('agentCfg.reloc.enterDir'), 'var(--muted)');
  if (r.checking || r.soon) return line(T('agentCfg.reloc.checking'), 'var(--muted)');
  if (!relocCheckFits(r, q, ch)) return line(T('agentCfg.reloc.needCheck'), 'var(--muted)');
  if (ch.error) return line(T('agentCfg.reloc.checkFailed', { error: ch.error }), 'var(--bad)');
  const res = ch.res || {}, out = [];
  // A refusal is the whole answer: the agent's own sentence (not enough space, a folder that holds
  // something else...), which would otherwise repeat the target line below it word for word.
  if (res.problem) return line(esc(String(res.problem)), 'var(--bad)');
  if (r.flow) return relocFlowHtml(r, res, line);
  // An absent folder: a move creates it; a re-point creates nothing (a later download does).
  if (!remove && hasOwn(RELOC_TARGETS, res.target)) {
    out.push(line(T(res.target === 'absent' && q.mode === 'repoint' ? 'agentCfg.reloc.target.absentRepoint' : RELOC_TARGETS[res.target])));
  }
  if (q.mode === 'move' && res.sameVolume === true) out.push(line(T('agentCfg.reloc.sameDrive')));
  else if (q.mode === 'move' && res.sameVolume === false) {
    out.push(line(T('agentCfg.reloc.otherDrive', { size: res.stackBytes == null ? '?' : sizeGb(res.stackBytes), free: res.freeBytes == null ? '?' : sizeGb(res.freeBytes) })));
  }
  if (res.running) out.push(line(T(RELOC_MODES[r.mode].running), 'var(--warn)'));
  if (q.mode === 'repoint' && !remove) out.push(relocCarryHtml(r, res, line));
  if (res.ok === true) out.push(line(T('agentCfg.reloc.ok'), 'var(--ok)'));
  return out.join('');
}
// The platform of the administered server's agent (its /status, else its settings): where its conventional folders lie.
const srvWindows = () => ((S.srv.info && !S.srv.info.public && typeof S.srv.info.platform === 'string' ? S.srv.info.platform : (S.agentCfg.data || {}).platform) === 'windows');
function relocOverlay() {
  const r = S.agentCfg.reloc, d = S.agentCfg.data || {};
  const remove = r.mode === 'remove', flow = r.flow;
  const fe = flow ? (srvFacts(r.version) || {}).fetch : null;
  const sizes = () => { const size = fe && isBytes(fe.size) ? fe.size : 0; return { size: sizeGb(size), unpacked: sizeGb(fe && isBytes(fe.extractedSize) && fe.extractedSize > 0 ? fe.extractedSize : 3 * size) }; };
  const label = (id) => (id === 'download' ? T('addver.download') : id === 'use' ? T('addver.use') : T(RELOC_MODES[id].label));
  const sub = (id) => (id === 'download' ? T('addver.downloadSub', sizes()) : id === 'use' ? T('addver.useSub') : T(RELOC_MODES[id].sub));
  // An option that is unavailable — "move" without server files to move, a download the server could not unpack — says
  // why instead of its description.
  const off = (id) => (id === 'download' ? T('addver.downloadNoTool') : T('agentCfg.reloc.moveOff'));
  const cards = relocModes(r).map((o) => `<div class="opt${r.mode === o.id ? ' sel' : ''}${o.dis ? ' dis' : ''}" data-act="relocMode" data-arg="${o.id}">
      <div class="h">${label(o.id)}</div><p>${o.dis ? off(o.id) : sub(o.id)}</p></div>`).join('');
  // The conventional folder of that version's stack on the agent's platform — beside the games when the admin server is
  // the agent on this PC (agent.build's local.adminServer), C:\relic_servers for a Windows agent elsewhere (stackFolder);
  // a version this page has no stack row for gets no suggestion.
  const stack = S.stacks.find((s) => s.id === r.version);
  const ph = stack ? stackFolder(stack, srvWindows(), !!(S.agentBuild.local && S.agentBuild.local.adminServer)) : '';
  const title = flow === 'add' ? T('addver.title', { version: r.version }) : flow === 'change' ? T('addver.changeTitle', { version: r.version }) : T('agentCfg.reloc.title', { version: r.version });
  // Browse opens THIS PC's folder picker: offered only when the agent runs on this PC.
  const dirField = remove ? '' : `<label class="fld">${flow ? T('addver.dir') : T('agentCfg.reloc.newDir')}</label>
    <div class="flex gap"><input class="txt mono" data-model="agentCfg.reloc.dir" value="${esc(r.dir)}" placeholder="${esc(ph)}" autocomplete="off" spellcheck="false">
      ${d.local ? `<button class="btn ghost" style="padding:8px 12px;font-size:12px;flex:none" data-act="relocPick">${T('common.browse')}</button>` : ''}
      <button class="btn ghost" style="padding:8px 12px;font-size:12px;flex:none" data-act="relocCheck" ${r.checking ? 'disabled' : ''}>${T('agentCfg.reloc.check')}</button></div>`;
  return `<div class="overlay"><div class="box wide">
    <div style="width:60px;height:60px;border-radius:50%;margin:0 auto 14px;background:color-mix(in srgb, var(--gold) 16%, transparent);color:var(--goldD);display:flex;align-items:center;justify-content:center">${I.folder.replace('width="18" height="18"', 'width="26" height="26"')}</div>
    <div class="serif" style="font-weight:700;font-size:19px;text-align:center">${title}</div>
    <p style="color:var(--inkSoft);font-size:12.5px;font-weight:600;line-height:1.6;margin:10px 0 0;text-align:center">${T('agentCfg.reloc.current')}: <b class="${r.from ? 'mono pick' : ''}" style="color:var(--ink);word-break:break-all">${r.from ? esc(r.from) : T('agentCfg.dir.none')}</b></p>
    <div class="copts">${cards}</div>
    ${dirField}
    <div id="reloc-check" style="min-height:24px;margin:6px 0 20px">${relocCheckHtml(r)}</div>
    <div class="flex center gap" style="justify-content:center">
      <button class="btn ghost" data-act="relocClose">${T('common.cancel')}</button>
      <button class="btn" data-act="relocStart" ${relocStartOk(r) ? '' : 'disabled'}>${esc(relocYesText(r))}</button>
    </div></div></div>`;
}
// The check area and Start follow in place: the check lands while the admin may still be typing (the size of a
// download the Start label names comes with the check).
function syncReloc() {
  const r = S.agentCfg.reloc, box = document.getElementById('reloc-check');
  if (!r || !box) { if (!typingNow()) render(); return; }
  box.innerHTML = relocCheckHtml(r);
  const b = document.querySelector('[data-act="relocStart"]'); if (b) { b.disabled = !relocStartOk(r); b.textContent = relocYesText(r); }
  const k = document.querySelector('[data-act="relocCheck"]'); if (k) k.disabled = !!r.checking;
}
let relocTimer = 0;
function relocCheckSoon() {
  const r = S.agentCfg.reloc;
  if (!r) return;
  clearTimeout(relocTimer);
  r.soon = true; syncReloc();
  relocTimer = setTimeout(() => { if (S.agentCfg.reloc === r) relocCheck(); }, 700);
}
// The last check asked wins (seq); one for another dialog, or for a folder typed over since, is dropped.
async function relocCheck() {
  clearTimeout(relocTimer);
  const r = S.agentCfg.reloc;
  if (!r) return;
  r.soon = false;
  const q = relocQuery(r), ui = r.mode, seq = ++r.seq;
  if (!relocModeOk(r) || (r.mode !== 'remove' && !q.dir)) { r.checking = false; syncReloc(); return; }
  r.checking = true; syncReloc();
  let res = null, error = '';
  try { res = await rpc('server.relocate.check', q); } catch (e) { error = e.message || String(e); }
  if (S.agentCfg.reloc !== r || r.seq !== seq) return;
  const good = !!res && typeof res === 'object';
  r.checking = false;
  r.check = { ui, dir: q.dir, mode: q.mode, res: good ? res : null, error: error || (good ? '' : t('common.unknownError')) };
  syncReloc();
}
// The folder dialog of "Add version…" (flow 'add': a version the agent has no folder for) or "Change folder…" (flow
// 'change': a configured version whose folder holds no server yet), starting on the download when the agent knows a
// package of the version this server can unpack. Nothing opens while a job runs, while the folder cannot be set from
// here (folderBlock), for a version the agent does not list, or for one that does not stand as the button said: the
// page is drawn again from the status. The version is judged by what the rows drew the button from — the status, when
// it says whether the version has a folder —, else by the agent's settings read; one that disagrees with the status has
// the settings read again (agentCfgResync).
function openFolderFlow(id, flow) {
  const c = S.agentCfg, f = srvFacts(id), fe = (f || {}).fetch, e = S.srv.versions && S.srv.versions[id];
  const told = !!f && isVersionEntry(e) && typeof e.configured === 'boolean' && !!S.srv.info && !S.srv.info.public;
  const vi = told ? { dir: f.dir, configured: e.configured, present: f.present } : agentCfgVersion(id);
  if (folderBlock() !== '' || srvBusy() || srvUnlisted(id) || (flow === 'add' ? vi.configured : !vi.configured || vi.present)) { agentCfgResync(); return render(); }
  clearTimeout(relocTimer);
  c.reloc = { version: id, from: flow === 'add' ? '' : vi.dir, dir: '', mode: fe && fe.available && fe.tool ? 'download' : 'use', flow,
    check: null, checking: false, soon: false, seq: 0, at: Date.now() };
  render();
}

// ── admin (Windows): the agent running on this PC ──
function localAgentCard() {
  if (!S.isWindows) return '';
  const la = S.localAgent.status;
  if (!la) return `<div class="sect"><h4>${T('localAgent.title')}</h4><p class="hint">${T('common.checking')}</p></div>`;
  const dot = la.healthy ? 'live' : la.running ? 'warn' : 'down';
  // An enter / uninstall in flight (S.localAgent.busy) holds every button of the card: the uninstall
  // may sit on a UAC prompt for a minute, and a Start pressed meanwhile would race the kill.
  const busy = S.localAgent.busy ? 'disabled' : '';
  // Whether this PC's agent is the one of the server administered here (agent.build's adminServer), said once known.
  const which = la.installed && S.agentBuild.local ? `<p class="hint">${S.agentBuild.local.adminServer ? T('localAgent.thisServer') : T('localAgent.otherServer', { host: S.serverAddr.host || '—' })}</p>` : '';
  return `<div class="sect"><h4>${T('localAgent.title')}</h4>${which}
    <div class="line"><div><b><span class="dot ${dot}" style="display:inline-block;margin-right:6px;vertical-align:middle"></span>${la.installed ? (la.healthy ? T('localAgent.healthy', { port: la.port }) : la.running ? T('localAgent.starting') : T('localAgent.stopped')) : T('localAgent.notInstalled')}</b>
      <small>${esc(la.root)} · ${la.python ? T('localAgent.python', { v: la.python.version }) : T('localAgent.noPython')}</small></div>
      <div class="flex gap">${la.installed
        ? `${la.running ? `<button class="btn ghost" data-act="localAgentStop" ${busy}>${T('localAgent.stop')}</button>` : `<button class="btn" data-act="localAgentStart" ${busy}>${T('localAgent.start')}</button>`}<button class="btn ghost" data-act="localAgentUninstall" ${busy}>${T('localAgent.uninstall')}</button>`
        : `<button class="btn" data-act="deployOpenWindows">${T('localAgent.install')}</button>`}</div></div>
    ${la.installed && localAgentUpdate() ? `
    <div class="line"><div><b>${T('localAgent.update')}</b><small>${T('localAgent.updateSub')}</small></div><button class="btn" style="padding:8px 12px;font-size:12px" data-act="agentUpdateLocal" ${busy || S.agentUpdate ? 'disabled' : ''}>${T('server.banner.agentUpdateNow')}</button></div>` : ''}
    ${la.installed ? `
    <div class="line"><div><b>${T('localAgent.autostart')}</b><small>${T('localAgent.autostartSub')}</small></div><button class="toggle ${la.autostart ? 'on' : ''}" data-act="localAgentAutostart"><span class="knob"></span></button></div>
    <div class="line"><div><b>${T('localAgent.firewall')}</b><small>${T('localAgent.firewallSub', { port: la.port })}</small></div><button class="btn ghost" style="padding:8px 12px;font-size:12px" data-act="localAgentFirewall">${T('localAgent.firewallBtn')}</button></div>` : ''}
    ${la.fiddlerRunning ? `<div class="note" style="margin-top:10px">${T('deploy.fiddlerWarning')}</div>` : ''}
  </div>`;
}
// The Agent tab's "Agent state file", only while the agent cannot read it: where it is, why, and the reset
// (stateResetAsk); the strip's note on every tab points here.
function agentStateCard() {
  const info = S.srv.info || {};
  const ast = info.state && typeof info.state === 'object' ? info.state : null;
  if (!ast || S.srv.state !== 'ok' || ast.ok !== false) return '';
  return `<div class="sect"><h4>${T('server.agentState.title')}</h4><p class="hint">${T('server.agentState.cardBody', { path: String(ast.path || 'state.json'), error: String(ast.error || '') })}</p>
    <button class="btn ghost danger" style="padding:8px 14px;font-size:12px" data-act="stateResetAsk" ${srvBusy() ? 'disabled' : ''}>${T('server.agentState.resetButton')}</button></div>`;
}
// The Agent tab's way to set up an agent on another server (the start screen offers the same overlay).
function agentInstallCard() {
  return `<div class="sect"><h4>${T('deploy.title')}</h4><p class="hint">${S.isWindows ? T('server.install.hint') : T('server.install.hintLinux')}</p>
    <button class="btn ghost" style="padding:8px 14px;font-size:12px" data-act="deployOpen">${T('server.agent.installNew')}</button></div>`;
}
// The Agent tab's Logs: the agent's last operations (agent.jobs → GET /jobs, metadata only — never a job's result)
// and one operation's lines on demand (agent.jobLog → GET /jobs/<id>?since=0). One shape for every reset: boot,
// another server (srvRetarget), another agent process (agentUpdateEnded, deploy.done). list = the entries (null = not
// read), open = the job whose lines are shown ('' = none), log = {kind, version, state, elapsed, lines, earlier} of it:
// its last SRVLOG_MAX lines, like the job console's tail, and how many came before them.
function agentJobsBlank() { return { list: null, loading: false, error: '', tooOld: false, open: '', log: null, logLoading: false, logError: '' }; }
let agentJobsSeq = 0, agentJobLogSeq = 0;
// The last request wins, and only under the target it was sent to and for the object it was sent for.
async function loadAgentJobs() {
  if (!isAdminMode() || !S.serverConfigured) return;
  const j = S.agentJobs, seq = ++agentJobsSeq, target = srvTarget();
  const mine = () => seq === agentJobsSeq && S.agentJobs === j && srvTarget() === target;
  j.loading = true; j.error = '';
  if (S.screen === 'server') renderOrOwe();
  try {
    const r = await rpc('agent.jobs');
    if (!mine()) return;
    j.tooOld = !!(r && r.tooOld);
    j.list = !j.tooOld && r && Array.isArray(r.jobs) ? r.jobs.filter((x) => x && typeof x.id === 'string' && x.id) : [];
  } catch (e) { if (!mine()) return; j.error = e.message || String(e); }
  j.loading = false;
  if (S.screen === 'server') renderOrOwe();
}
async function loadAgentJobLog(id) {
  if (!isAdminMode() || !S.serverConfigured || !id) return;
  const j = S.agentJobs, seq = ++agentJobLogSeq, target = srvTarget();
  const mine = () => seq === agentJobLogSeq && S.agentJobs === j && srvTarget() === target && j.open === id;
  j.open = id; j.log = null; j.logError = ''; j.logLoading = true; render();
  let failed = false;
  try {
    const r = await rpc('agent.jobLog', { id });
    if (!mine()) return;
    const lines = r && Array.isArray(r.lines) ? r.lines.filter((l) => typeof l === 'string') : [];
    j.log = { kind: String((r && r.kind) || ''), version: r && r.version != null ? String(r.version) : '', state: String((r && r.state) || ''),
      elapsed: r ? Number(r.elapsed) || 0 : 0, lines: lines.slice(-SRVLOG_MAX), earlier: Math.max(0, lines.length - SRVLOG_MAX) };
  } catch (e) { if (!mine()) return; j.logError = e.message || String(e); failed = true; }
  j.logLoading = false;
  if (S.screen === 'server') renderOrOwe();
  // A job the agent no longer has (it restarted, or the job left its last 20): the list is read again.
  if (failed && j.list) loadAgentJobs();
}
const jobTime = (sec) => { const s = Math.max(0, Math.round(Number(sec) || 0)); return s < 60 ? t('server.logs.seconds', { n: s }) : t('server.logs.minutes', { n: Math.round(s / 60) }); };
// The title of an operation of the Logs. The list names no progress choice, so a progress job is called by the
// button that starts it, true of all three choices ("Progress re-apply" would announce an import the admin may have
// declined: jobTitleOf).
const logsJobTitle = (kind) => (kind === 'provision' ? t('server.logs.kindProvision') : jobTitle(kind));
// One row of the Logs list: the job's title, its version, how it stands, and its failure reason.
function jobRow(x) {
  const version = x.version == null ? '—' : String(x.version), time = jobTime(x.elapsed);
  const words = x.state === 'running' ? T('server.logs.rowRunning', { version, time }) : x.state === 'error' ? T('server.logs.rowFailed', { version, time }) : T('server.logs.rowDone', { version, time });
  return `<div class="line"><div style="min-width:0"><b>${esc(logsJobTitle(x.kind))}</b><small>${words}</small>${x.state === 'error' && x.error ? `<small style="color:var(--bad);overflow-wrap:anywhere">${esc(String(x.error))}</small>` : ''}</div>
    <button class="btn ghost" style="padding:8px 12px;font-size:12px;flex:none" data-act="agentJobOpen" data-arg="${esc(x.id)}">${S.agentJobs.open === String(x.id) ? T('server.logs.hide') : T('server.logs.show')}</button></div>`;
}
// The lines of the operation the Logs show (#joblog, a LOGPANES pane: it opens at its last line — DONE. or ERROR: …),
// its last SRVLOG_MAX at most: the bar says how many came before them.
function jobLogView() {
  const j = S.agentJobs, l = j.log;
  const body = j.logLoading ? `<div style="color:#8aa0c8">${T('common.checking')}</div>`
    : j.logError ? `<div style="color:#e08a7f">${T('server.logs.logFailed', { error: j.logError })}</div>`
      : l && l.lines.length ? l.lines.map(srvLogLine).join('') : `<div style="color:#8aa0c8">${T('server.logs.noLines')}</div>`;
  const title = l ? `${esc(logsJobTitle(l.kind).toLowerCase())} · ${T('server.console.version', { version: l.version || '—' })}${l.earlier > 0 ? ` · ${T('server.logs.earlier', { n: l.earlier })}` : ''}` : '';
  return `<div class="console"><div class="bar"><span class="lights"><i style="background:#e4664e"></i><i style="background:#e8b04a"></i><i style="background:#67b86a"></i></span><span>${title}</span></div><div class="log" id="joblog">${body}</div></div>`;
}
// The Agent tab's Logs: the agent's last operations (on demand, newest first), one operation's lines, the log of the
// agent on this PC (Windows, installed) and Relic's own.
function logsCard() {
  const j = S.agentJobs, la = S.localAgent.status;
  let list = '';
  if (!S.serverConfigured) list = `<p class="hint" style="margin:0">${T('server.agent.notConfigured')}</p>`;
  else if (j.tooOld) list = `<div class="note">${T('server.logs.tooOld')}</div>`;
  else if (j.error) list = `<div class="note notebtn"><span>${T('server.logs.failed', { error: j.error })}</span><button class="btn ghost" data-act="agentJobsLoad">${T('common.retry')}</button></div>`;
  else if (!j.list) list = `<button class="btn ghost" style="padding:8px 14px;font-size:12px" data-act="agentJobsLoad" ${j.loading ? 'disabled' : ''}>${j.loading ? T('common.checking') : T('server.logs.load')}</button>`;
  else list = (j.list.length ? j.list.slice().reverse().map(jobRow).join('') : `<p class="hint" style="margin:0">${T('server.logs.empty')}</p>`)
    + `<div class="flex" style="justify-content:flex-end;margin-top:10px"><button class="btn ghost" style="padding:8px 14px;font-size:12px" data-act="agentJobsLoad" ${j.loading ? 'disabled' : ''}>${T('server.logs.refresh')}</button></div>`;
  const local = S.isWindows && la && la.installed ? `<div class="line"><div style="min-width:0"><b>${T('localAgent.log')}</b><small style="overflow-wrap:anywhere">${esc(la.logPath)}</small></div><button class="btn ghost" style="padding:8px 12px;font-size:12px;flex:none" data-act="localAgentOpenLog">${T('localAgent.openLog')}</button></div>` : '';
  return `<div class="sect"><h4>${T('server.logs.title')}</h4><p class="hint">${T('server.logs.hint')}</p>${list}${j.open ? jobLogView() : ''}
    <div style="margin-top:12px">${local}<div class="line"><div><b>${T('server.logs.relic')}</b><small>${T('server.logs.relicSub')}</small></div><button class="btn ghost" style="padding:8px 12px;font-size:12px;flex:none" data-act="relicOpenLog">${T('localAgent.openLog')}</button></div></div></div>`;
}

// "Guide (PDF)" — the vendor's GIO guide book, next to everything that asks the admin to set up a
// server: the install form, the admin start-screen panel, About.
const ICON_BOOK = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="margin-right:6px;vertical-align:-2px"><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z"/></svg>';
const guideBtn = () => `<button class="btn ghost" style="padding:7px 12px;font-size:11.5px;white-space:nowrap" data-act="openGuide" title="${T('help.guideTitle')}">${ICON_BOOK}${T('help.guide')}</button>`;

// ── install-agent overlay (from the start screen and the Agent tab) ──
function agentInstallOverlay() {
  const a = S.agentInstall;
  const f = a.form;
  const win = a.target === 'windows';
  // "Keep .env settings" on: the four values the agent then leaves as each server has them
  // (DEPLOY_KEPT) are not asked for — the input is dead and drawn empty, its placeholder and the hint
  // under it say the server's own value is kept. What was typed stays in the form state and is drawn
  // again when the toggle goes off.
  const kept = (key) => f.keepEnv === true && DEPLOY_KEPT.includes(key);
  // help = the plain TEXT of a "?" badge after the label (helpBadge); the label is then the box its
  // bubble is laid out against (.helpbox), so the bubble can never be wider than the field's column.
  const field = (key, label, ph, type, mono, help) =>
    `<label class="fld${help ? ' helpbox' : ''}">${T(label)}${help ? helpBadge(help) : ''}</label><input class="txt ${mono === false ? '' : 'mono'}" type="${type || 'text'}" data-model="agentInstall.form.${key}" value="${esc(kept(key) ? '' : f[key] || '')}" placeholder="${esc(kept(key) ? t('deploy.net.kept') : ph || '')}" autocomplete="off" spellcheck="false"${kept(key) ? ' disabled style="opacity:.6;cursor:not-allowed"' : ''}>`;
  // The hint under one of those four fields: its own text (already escaped), or the "kept" one.
  const hint = (key, own) => `<small class="hint">${kept(key) ? T('deploy.net.keptHint') : own}</small>`;
  // "Keep .env settings" on while a version is to be downloaded: the package the agent downloads
  // arrives with the vendor's own .env (its ports on 127.0.0.1, the published passwords), and the option
  // keeps exactly that file — a note under the toggle says so, and what to do about it.
  const keptFetch = f.keepEnv === true && S.stacks.some((s) => deployChoice(a, s) === 'download');
  // One server stack (a row of S.stacks): its version and ONE choice (deployChoice) — Download now (the agent
  // downloads the ready-made package from the Internet Archive into the folder once it runs: deploy.done `chain`), I
  // have it (the folder holds the extracted vendor package, the install checks it), Not now — or, on the Windows form,
  // Keep for a version the agent on this PC already has a folder for (shown, not edited: a folder changes only on the
  // Server page), with Download now beside it while that folder holds no server yet. Under it the folder field of the
  // choice, its hint (the sizes the target will really take: deployDownloadHint) and, on Windows, what this PC's check
  // of the folder says (deployCheckLine). A version the build knows no package for has nothing to download: its
  // Download now is dead, and the line under it says so.
  const stackCell = (s) => {
    const c = deployChoice(a, s), kept = deployKept(a, s), kind = (a.kinds && a.kinds[s.id]) || '';
    const seg = (choice, label, dead) => `<button class="${c === choice ? 'on' : ''}" data-act="deployStack" data-arg="${esc(s.stackField)}:${choice}" ${dead ? 'disabled' : ''}>${label}</button>`;
    const segs = kept ? seg('keep', T('deploy.stack.keep')) + (deployKeptEmpty(a, s) ? seg('download', T('deploy.stack.download')) : '')
      : seg('download', T('deploy.stack.download'), !s.downloadable) + seg('have', T('deploy.stack.have')) + seg('later', T('deploy.stack.later'));
    const line = (html, cls, attr) => `<small class="stkline${cls ? ' ' + cls : ''}"${attr || ''}>${html}</small>`;
    const hintAttr = ` data-dhint="${esc(s.id)}"`;
    let body;
    if (kept) {
      body = `<div class="roaddr stkdir">${esc(kept)}</div>${line(T('deploy.stack.keepHint', { dir: kept }))}${
        c === 'download' ? line(deployDownloadHint(a, s), '', hintAttr)
          : deployKeptEmpty(a, s) ? line(T('deploy.stack.keepDownload'))
            : kind === 'other' ? line(T('deploy.stack.keepOther'), 'warn')
              : kind === 'unknown' ? line(T('deploy.stack.keepUnknown')) : ''}`;
    } else if (c === 'download' || c === 'have') {
      const dl = c === 'download';
      body = `<label class="fld">${dl ? T('deploy.stacks.installInto', { version: s.id }) : T('deploy.stack.haveDir', { version: s.id })}</label>
        <div class="flex gap"><input class="txt mono" data-model="agentInstall.form.${s.dirField}" value="${esc(f[s.dirField] || '')}" placeholder="${esc(stackFolder(s, win, win))}" autocomplete="off" spellcheck="false">${
          win ? `<button class="btn ghost" style="padding:8px 12px;font-size:12px;flex:none" data-act="deployPickDir" data-arg="${esc(s.dirField)}">${T('common.browse')}</button>` : ''}</div>
        ${dl ? line(deployDownloadHint(a, s), '', hintAttr) : line(T('deploy.stack.haveHint'))}`;
    } else body = line((s.downloadable ? T('deploy.stack.laterHint') : T('deploy.stacks.noPackage')) + (win ? '' : ' ' + T('deploy.stack.laterLinux')));
    const noPackage = !s.downloadable && !kept && c !== 'later' ? line(T('deploy.stacks.noPackage')) : '';
    return `<div class="stkrow"><div class="stkhead"><b>${T('common.versionN', { id: s.id })}</b><div class="lg-tabs stkseg">${segs}</div></div>
      ${noPackage}${body}<div data-dchk="${esc(s.id)}">${deployCheckLine(a, s)}</div></div>`;
  };
  if (a.running || a.done || a.error) {
    // What the install handed the Server page (deploy.done `chain`: a re-point and / or a download per step, run one
    // after the other) and what it held back with its reason (`held`).
    const res = a.result || {}, steps = chainStepsOf(res), downloads = steps.filter((x) => x.fetch).length;
    const held = Array.isArray(res.held) ? res.held.filter((h) => h && typeof h.message === 'string' && h.message) : [];
    return `<div class="overlay"><div class="box" style="width:640px">
      <h3 class="serif" style="margin:0 0 10px">${T(win ? 'deploy.title.windows' : 'deploy.title.linux')}</h3>
      <div class="console"><div class="bar"><span class="lights"><i style="background:#e4664e"></i><i style="background:#e8b04a"></i><i style="background:#67b86a"></i></span><span>${T('deploy.console.label')} · ${esc(win ? 'localhost' : (f.sshHost || '—'))}</span></div><div class="log" id="deploylog" style="max-height:260px">${(a.lines || []).map((l) => `<div>${esc(l)}</div>`).join('')}</div></div>
      ${a.error ? `<div class="note" style="margin-top:12px;white-space:pre-wrap">${esc(a.error)}</div>` : ''}
      ${!a.error && a.done && a.result && a.result.warning ? `<div class="note" style="margin-top:12px">${esc(a.result.warning)}</div>` : ''}
      ${(a.done || a.error) && a.result && a.result.token ? `<div class="summary" style="margin-top:12px"><div class="k">${T(a.error ? 'deploy.done.titleFailed' : 'deploy.done.title')}</div><div class="g"><span>${T('deploy.done.token')}</span><b class="pick mono" style="word-break:break-all">${esc(a.result.token)}</b></div><p class="hint" style="margin:8px 0 0">${T(a.error ? 'deploy.done.hintFailed' : 'deploy.done.hint')}</p></div>` : ''}
      ${!a.error && a.done ? held.map((h) => `<div class="note" style="margin-top:12px">${esc(h.message)}</div>`).join('') : ''}
      ${!a.error && a.done && downloads ? `<div class="note" style="margin-top:12px">${downloads === 1 ? T('deploy.done.fetchStarting') : T('deploy.done.chainStarting')}</div>` : ''}
      <div class="flex between center" style="margin-top:16px"><span class="hint" style="margin:0">${a.running ? T('deploy.running') : ''}</span>
        <div class="flex gap">${guideBtn()}${a.error ? `<button class="btn" data-act="deployBack">${T('deploy.backToForm')}</button>` : ''}<button class="btn ghost" data-act="deployClose" ${a.running ? 'disabled' : ''}>${T('common.close')}</button></div></div></div></div>`;
  }
  return `<div class="overlay"><div class="box" style="width:700px;max-height:640px;overflow-y:auto">
    <div class="flex between center" style="gap:12px"><h3 class="serif" style="margin:0 0 4px">${T('deploy.title')}</h3>${guideBtn()}</div><p class="hint">${T('deploy.intro')}</p>
    <div class="grid2" style="margin-bottom:12px">
      <div class="opt ${!win ? 'sel' : ''}" data-act="deployTarget" data-arg="linux"><div class="h">${I.stack} ${T('deploy.target.linux')}</div><p>${T('deploy.target.linuxSub')}</p></div>
      <div class="opt ${win ? 'sel' : ''}" data-act="deployTarget" data-arg="windows"><div class="h">${I.gear} ${T('deploy.target.windows')}</div><p>${T('deploy.target.windowsSub')}</p></div>
    </div>
    <div class="note">${T('deploy.filesWarning')}</div>
    ${win ? `<div class="note" style="margin-top:8px">${T('deploy.fiddlerWarning')}</div>` : ''}
    ${!win ? `<h4 class="serif" style="margin:16px 0 4px">${T('deploy.ssh.title')}</h4>
    <div class="grid2">
      <div>${field('sshHost', 'deploy.ssh.host', 'game.example.com')}</div><div>${field('sshPort', 'deploy.ssh.port', '22')}</div>
      <div>${field('sshUser', 'deploy.ssh.user', 'root')}</div><div>${field('sshPassword', 'deploy.ssh.password', '', 'password')}</div>
      <div>${field('sshKeyPath', 'deploy.ssh.key', 'C:\\path\\key.ppk')}<button class="btn ghost" style="margin-top:6px;padding:7px 12px;font-size:12px" data-act="deployPickKey">${T('common.browse')}</button></div>
      <div>${field('sshKeyPassphrase', 'deploy.ssh.passphrase', '', 'password')}</div>
      <div>${field('sudoPassword', 'deploy.ssh.sudo', '', 'password')}<small class="hint">${T('deploy.ssh.sudoHint')}</small></div>
    </div>` : ''}
    <h4 class="serif" style="margin:16px 0 4px">${T('deploy.stacks.title')}</h4>
    <div class="stkrows">
      ${S.stacks.map(stackCell).join('\n      ')}
    </div>
    <h4 class="serif" style="margin:16px 0 4px">${T('deploy.net.title')}</h4>
    <div class="row" style="margin:6px 0 2px;gap:14px"><div><b>${T('deploy.net.keepEnv')}</b><small>${T('deploy.net.keepEnvSub')}${win ? '' : ` ${T('deploy.net.keepEnvSubLinux')}`}</small></div><button class="toggle ${f.keepEnv === true ? 'on' : ''}" data-act="deployToggle" data-arg="keepEnv"><span class="knob"></span></button></div>
    ${keptFetch ? `<div class="note" style="margin-top:8px">${T('deploy.net.keepEnvFetch')}</div>` : ''}
    <div class="grid2">
      <div>${field('bindIp', 'deploy.net.bindIp', t('deploy.net.auto'))}${hint('bindIp', T('deploy.net.bindHint'))}</div>
      <div>${field('advertisedIp', 'deploy.net.advertisedIp', '', 'text', true, t('deploy.net.advHelp'))}<small class="hint">${T('deploy.net.advHint')}</small></div>
      <div>${field('advertisedHost', 'deploy.net.advertisedHost', 'game.example.com')}<small class="hint">${T('deploy.net.advHostHint')}</small></div>
      <div>${field('listen', 'deploy.net.listen', '0.0.0.0:18080')}</div>
      <div>${field('serverName', 'deploy.net.name', t('deploy.net.namePh'), 'text', false)}</div>
      <div>${field('token', 'deploy.net.token', '')}<button class="btn ghost" style="margin-top:6px;padding:7px 12px;font-size:12px" data-act="deployGenToken">${T('deploy.net.generate')}</button></div>
      <div>${field('muipHost', 'deploy.net.muip', t('deploy.net.auto'))}<small class="hint">${T('deploy.net.muipHint')}</small></div>
      <div>${field('muipKey', 'deploy.net.muipKey', t('deploy.net.random'))}${hint('muipKey', T('deploy.net.muipKeyHint'))}</div>
      <div>${field('mysqlPassword', 'deploy.net.mysqlPassword', t('deploy.net.random'))}${hint('mysqlPassword', T('deploy.net.mysqlPasswordHint'))}</div>
      <div>${field('flaskKey', 'deploy.net.flaskKey', t('deploy.net.random'))}${hint('flaskKey', T('deploy.net.flaskKeyHint'))}</div>
    </div>
    ${win ? `<div class="row" style="margin-top:12px"><div><b>${T('deploy.win.autostart')}</b><small>${T('deploy.win.autostartSub')}</small></div><button class="toggle ${f.autostart ? 'on' : ''}" data-act="deployToggle" data-arg="autostart"><span class="knob"></span></button></div>` : ''}
    ${!win ? `<div class="row" style="margin-top:12px"><div><b>${T('deploy.ssh.upgradeForce')}</b><small>${T('deploy.ssh.upgradeForceSub')}</small></div><button class="toggle ${f.upgradeForce ? 'on' : ''}" data-act="deployToggle" data-arg="upgradeForce"><span class="knob"></span></button></div>` : ''}
    <div class="dsum" id="deploy-summary">${deploySummaryHtml(a)}</div>
    <div class="flex between center" style="margin-top:14px">
      <button class="btn ghost" data-act="deployClose">${T('common.cancel')}</button>
      <button class="btn glow" data-act="deployStart">${esc(deployStartText(a))}</button>
    </div></div></div>`;
}

// ── per-version removal overlay ──
function removeOverlay() {
  const r = S.remove;
  const title = T('lib.remove.removing', { id: r.versionId });
  if (r.error || r.done) {
    return `<div class="overlay"><div class="box" style="text-align:center">
      <div class="serif" style="font-weight:700;font-size:19px">${r.error ? T('lib.remove.failed') : T('lib.remove.done', { id: r.versionId })}</div>
      <p style="color:var(--inkSoft);font-size:12.5px;font-weight:600;margin:10px 0 18px;word-break:break-word">${r.error ? esc(r.error) : (r.leftovers && r.leftovers.length ? T('lib.remove.leftovers', { n: r.leftovers.length }) : T('lib.remove.doneNote'))}</p>
      <button class="btn" data-act="removeClose">${T('common.close')}</button></div></div>`;
  }
  return `<div class="overlay"><div class="box">
    <div class="flex center gap" style="margin-bottom:6px">${spinnerHtml()}<div class="serif" style="font-weight:700;font-size:18px">${title}</div></div>
    <div style="color:var(--inkSoft);font-size:12.5px;font-weight:700;margin:2px 0 18px;min-height:36px">${esc(r.msg || '...')}</div>
    <div class="pbar"><i style="width:${Math.round((r.fraction || 0) * 100)}%"></i></div>
  </div></div>`;
}

// Why the job buttons of this server are dead although nothing runs under this launcher's own console: the
// notes of the status strip of the Server Admin area.
function srvBusyNotes() {
  const info = S.srv.info || {};
  // An operation started elsewhere (another computer, another admin): the agent refuses the second
  // one with 409 anyway, so it is more honest to show why the buttons are dead. One whose work ends in the
  // gameplay settings' waits (twStopBusyJob) gets the console's "Stop waiting" beside the note: nothing
  // else ends those waits from here.
  const stopJob = twStopBusyJob();
  const busyStop = stopJob
    ? `<div class="flex gap wrap" style="justify-content:flex-end;margin-top:10px">${twStopButton('tweaksStopBusy', stopJob.kind, stopJob.version, S.twStop.busySent === twStopBusyKey(stopJob))}</div>` : '';
  const busyNote = info.busy && !jobHere()
    ? (AGENT_SELF_JOBS[info.busy.kind]
      ? `<div class="note" style="margin-top:16px">${T('server.busy.self', { job: jobTitle(info.busy.kind).toLowerCase(), version: info.busy.version })}</div>`
      : `<div class="note" style="margin-top:16px">${T('server.busy.other', { job: jobTitle(info.busy.kind).toLowerCase(), version: info.busy.version })}</div>${busyStop}`)
    : '';
  // The job this launcher follows runs on ANOTHER server (the address was changed while it ran): it
  // keeps every button of this one dead, and the console below is that server's log — say so.
  const elsewhereNote = jobElsewhere()
    ? `<div class="note" style="margin-top:16px">${T('server.busy.elsewhere', { job: jobTitleOf(S.srvJob).toLowerCase(), version: S.srvJob.version, host: S.srvJob.host || '—' })}</div>`
    : '';
  return busyNote + elsewhereNote;
}

// ── the Server Admin area: one header, one status strip, six tabs (Game rules is the 'tweaks' screen) ──
// One block of a tab, named for the page tests; a card that draws nothing leaves no block.
const blk = (name, html) => (html ? `<div data-blk="${name}">${html}</div>` : '');
// The agent's address as dialled: the configured host (bracketed when it is an IPv6 literal) and the agent port.
const agentAddr = () => `${S.serverAddr.host.includes(':') ? `[${S.serverAddr.host}]` : S.serverAddr.host}:${S.serverAddr.agentPort}`;
// A page of the Server Admin area: the header with its tab bar, the status strip, the blocks of the tab.
function adminPage(tab, body) {
  return `<div class="pad" data-admin-tab="${tab}">${adminHeader(tab)}${adminStrip()}<div class="admbody">${body}</div></div>`;
}
// The header of every page of the area: its title, the agent it administers, the ONE status read of every
// version ("Check": a read the agent answers while someone else works too, so it is the way out of a busy
// note; dead only under a job this launcher follows on THIS server) and the tab bar. Not sticky: the
// gameplay settings' action bar (.twbar) sticks to the top of .main.
function adminHeader(active) {
  const tab = (id, label) => `<button class="${active === id ? 'on' : ''}" data-act="adminTab" data-arg="${id}">${label}</button>`;
  const on = S.serverConfigured && !!S.serverAddr.host;
  return `<div class="admhead"><h1 class="title">${T('server.admin.title')}</h1>${on ? `<span class="admhost">${esc(agentAddr())}</span>
      <button class="btn ghost admcheck" data-act="checkServers" title="${T('server.admin.checkTitle')}" ${jobHere() ? 'disabled' : ''}>${T('server.card.check')}</button>` : ''}</div>
    <div class="lg-tabs admtabs">${tab('overview', T('server.tab.overview'))}${tab('players', T('server.tab.players'))}${tab('rules', T('server.tab.rules'))}${tab('content', T('server.tab.content'))}${tab('access', T('server.tab.access'))}${tab('agent', T('server.tab.agent'))}</div>`;
}
// The status strip of every page of the area, right under the tab bar: open administration FIRST, the agent
// update SECOND, then the agent's state file, why the status says nothing (no connection, a failed read,
// docker mute on the box), the jobs that keep this server's buttons dead, and the one job console (#srvlog).
// The part above the console is redrawn in place while a field has the caret (syncStrip).
function adminStrip() { return `<div class="admstrip">${stripLive()}${serverConsole()}</div>`; }
function stripLive() {
  return `<div data-srv-live>${openAdminNote()}${agentUpdateNote()}${agentStateNote()}${srvErrorNote()}${busyHeld() ? '' : srvBusyNotes()}</div>`;
}
// The strip's status part brought up to date in place: never the console (pushSrvLine appends its lines) and
// never a field (the strip holds none).
function syncStrip() { const el = document.querySelector('#app [data-srv-live]'); if (el) el.outerHTML = stripLive(); }
// The status held when a job this launcher followed here ended (twAfterJob keeps it as S.tw.settled) still
// names that job as busy until the status is read again: no "started elsewhere" for it.
const busyHeld = () => !!S.srv.info && S.srv.info === S.tw.settled;
// Open administration is on (`openAdmin` of the policy the admin /status embeds, read into S.pub.status like
// the player-commands flag): said FIRST in the strip, with the way out — in the words of the owner, or of a
// guest who entered through the key the agent hands out (S.admin.openKey). The button opens the same confirm
// as the Access tab's toggle.
function openAdminNote() {
  if (!(S.pub.status && S.pub.status.openAdmin === true)) return '';
  const guest = S.admin.openKey;
  return `<div class="banner">${I.warn}<div><b>${guest ? T('server.banner.openAdminGuestTitle') : T('server.banner.openAdminTitle')}</b>
      <p>${guest ? T('server.banner.openAdminGuestBody') : T('server.banner.openAdminBody')}</p>
      <button class="btn ghost danger" style="margin-top:10px;padding:8px 14px;font-size:12px" data-act="openAdminToggle" data-arg="off">${T('server.banner.openAdminClose')}</button></div></div>`;
}
// The agent's own state file (`state` of the admin /status). Unreadable = the agent refuses every change (a stop
// still works) until the file is repaired by hand or reset — the reset is the Agent tab's (agentStateCard), this
// note points there; restored from its backup / reset = automatic provisioning stays off for any version without
// a provisioning record. An agent that sends no `state` gets nothing.
function agentStateNote() {
  const info = S.srv.info || {};
  const ast = info.state && typeof info.state === 'object' ? info.state : null;
  if (!ast || S.srv.state !== 'ok') return '';
  if (ast.ok === false) return `<div class="banner">${I.warn}<div><b>${T('server.agentState.unreadableTitle')}</b>
      <p>${T('server.agentState.unreadableBody', { error: String(ast.error || ''), path: String(ast.path || 'state.json') })}</p>
      ${S.screen === 'server' && adminTabOn() === 'agent' ? '' : `<button class="btn ghost" style="margin-top:10px;padding:8px 14px;font-size:12px" data-act="adminTab" data-arg="agent">${T('server.strip.toAgentTab')}</button>`}</div></div>`;
  if (ast.restoredFromBackup) return `<div class="note" style="margin-top:16px">${T('server.agentState.restored', { at: String(ast.restoredFromBackup.at || '?'), copy: String(ast.restoredFromBackup.corruptCopy || '—') })}</div>`;
  return ast.reset ? `<div class="note" style="margin-top:16px">${T('server.agentState.wasReset', { at: String(ast.reset.at || '?'), copy: String(ast.reset.corruptCopy || '—') })}</div>` : '';
}
// Why the status says nothing: no connection configured (the way out is the start screen), a status read that
// failed (the header's Check reads it again), or docker mute on the box.
function srvErrorNote() {
  if (!S.serverConfigured) return `<div class="note notebtn" style="margin-top:16px"><span>${T('server.agent.notConfigured')} · ${T('server.agent.seeSettings')}</span><button class="btn ghost" data-act="changeMode">${T('common.changeMode')}</button></div>`;
  if (S.srv.state === 'error') return `<div class="note" style="margin-top:16px">${T('server.err.status', { error: S.srv.error })}</div>`;
  const info = S.srv.info || {};
  return info.error ? `<div class="note" style="margin-top:16px">${T('server.err.docker', { error: String(info.error) })}</div>` : '';
}
// .main back at its top — where a job started from a page of the Server Admin area shows its console (the strip).
const mainToTop = () => { const m = document.querySelector('#app .main'); if (m) m.scrollTop = 0; };
function adminServer() {
  const tab = adminTabOn();
  return adminPage(tab, tab === 'players' ? playersTab() : tab === 'content' ? contentTab()
    : tab === 'access' ? accessTab() : tab === 'agent' ? agentTab() : overviewTab());
}
const overviewTab = () => blk('versions', overviewCard());
const playersTab = () => blk('accounts', acctsCard()) + blk('acctnew', acctNewCard()) + blk('copy', acctCopyCard()) + blk('migrate', migrateCard())
  + blk('progress', progressCard()) + blk('policy', policyCard()) + blk('pwverify', pwVerifyCard());
const contentTab = () => blk('packages', packagesCard()) + blk('folders', foldersCard()) + blk('pathfinding', pathfindingCard())
  + blk('hotpatch', hotpatchCard());
const accessTab = () => blk('connection', connectionCard()) + blk('address', publicAddressCard()) + blk('openadmin', openAdminCard())
  + blk('secrets', secretsCard());
const agentTab = () => blk('agent', agentInfoCard()) + blk('settings', agentCfgCard()) + blk('state', agentStateCard())
  + blk('local', localAgentCard()) + blk('install', agentInstallCard()) + blk('logs', logsCard());

function server() { return isAdminMode() ? adminServer() : playerServer(); }

// ── gameplay settings: the Game rules tab (Server Admin mode) ──
// The agent keeps ONE document per server version — resin, the Spiral Abyss line-up, the domains'
// rewards, enemies and enemy level, quest rewards; every key optional, an absent key = the server's
// original value — and applies it with one job. GET /server/tweaks (server.tweaks.get) answers that
// document together with everything it is checked against: the original values, the limits, the ids the
// agent's catalogue lists. The page edits a DRAFT of the document and sends its canonical form (twCanon)
// with one button; nothing reaches the server while the admin edits.
// One shape for every reset: boot, another server (srvRetarget), an agent install (deploy.done). Keyed
// by version id: data = the last answer, error = why the last read failed ('' = it did not), loading,
// seq = the read that owns the slot, drafts = the document as edited (a typed number stays the text of
// its field until Apply), sent = the draft as it was when the page's job started, adopt = the same once
// that job is done: the re-read replaces a draft that still is that one with what the agent stored; up =
// whether the status called the version's stack running when the answer was read (a status that says
// otherwise since means the answer is stale); busy = the status names a job of the version that this
// launcher does not follow (another admin's, one of the agent's own); old = the answer is from before
// the end of a job of its version, or from an earlier visit of the page: it is read again when the
// version is on screen, and what it says of the server's files is not reported meanwhile (twFlux).
// version = the chip picked (null = twCur decides). settled = the status that was held when a job this
// launcher followed here ended (twJobRuns). ui = the cards' own view state (selections, searches) —
// never sent.
function twBlank() { return { version: null, data: {}, error: {}, loading: {}, seq: {}, drafts: {}, sent: {}, adopt: {}, up: {}, busy: {}, old: {}, settled: null, ui: {} }; }
// The document's sections (one card each) and the quest kinds its `quests.multiplier` knows, in the
// order the page lists them — the agent's own names (TWEAK_SECTIONS, TWEAK_QUEST_KINDS). A card's name is
// also what a Revert / a Reset of its files sends.
const TW_SECTIONS = ['resin', 'abyss', 'domains', 'quests'];
const TW_QUEST_KINDS = ['archon', 'story', 'world', 'event', 'commission', 'hangout'];
// The job kinds whose work ends in the gameplay settings' waits for the game server — the agent's own list
// (TWEAKS_STOP_KINDS): a tweaks job, and a start / provision / setup / relocate, whose last step hands the
// stored settings to the server. The job console offers to stop those waits (tweaksStop).
const TW_STOP_KINDS = ['tweaks', 'start', 'provision', 'setup', 'relocate'];
// The agent of this server answered that it cannot stop those waits, and still runs the build it ran then
// (a status that names none — being read again, an error — changes nothing): no control is offered to it.
const twStopRefused = () => {
  const no = S.twStop.no, target = srvTarget();
  if (!hasOwn(no, target)) return false;
  const info = S.srv.info, build = info && !info.public && typeof info.build === 'string' ? info.build : null;
  return build === null || build === no[target];
};
// The job the agent names as busy, when this launcher does not follow it — another admin's, or one this
// launcher started before it was closed — and its work ends in those waits: the stop is offered beside the
// note that says why the buttons are dead (srvBusyNotes).
const twStopBusyJob = () => {
  const b = S.srv.info && !S.srv.info.public ? S.srv.info.busy : null;
  return b && !jobHere() && TW_STOP_KINDS.includes(b.kind) && b.version != null && !twStopRefused() ? b : null;
};
const twStopBusyKey = (b) => `${srvTarget()}|${b.id}`;
// The agent's own words for a refusal record whose settings the game server never judged: the wait for its
// verdict was stopped on request ("Stop waiting"), or the server could not be asked to read them at all —
// that one followed by " (<why>)" (the agent's _TWEAKS_STOP_ASKED and _TWEAKS_UNASKED). Nothing was refused
// then: the note says only that the settings were not applied, and Apply asks the running server's question.
const TW_UNJUDGED = ['the wait for its verdict was stopped on request', 'the game server could not be asked to read the new settings'];
const twUnjudged = (refused) => {
  const why = String((refused && refused.error) || '');
  return TW_UNJUDGED.some((s) => why === s || why.startsWith(s + ' ('));
};
// The versions the page offers: the ones the server has (present in the admin /status) — the
// catalogue's first, then any other the agent names.
function twVersions() {
  const ids = S.versions.map((v) => v.id);
  Object.keys(S.srv.versions || {}).forEach((id) => { if (!ids.includes(id)) ids.push(id); });
  return ids.filter((id) => { const f = srvFacts(id); return !!f && f.present; });
}
// The version on screen: the chip picked while the server still has that version, else the one that
// runs, else the first one it has ('' = none known). Never the Library's selection: which client is
// selected says nothing about which server the admin edits. A status read that failed names no version
// at all: the picked one stays on screen as long as its answer is held, so a missed poll does not take
// the page away from under the admin.
function twCur() {
  const ids = twVersions(), w = S.tw;
  if (w.version && (ids.includes(w.version) || (!ids.length && hasOwn(w.data, w.version)))) return w.version;
  const live = srvLiveId();
  return live && ids.includes(live) ? live : (ids[0] || '');
}
const twDataOf = (v) => { const d = S.tw.data[v]; return d && typeof d === 'object' ? d : null; };
// The answer describes a version the page can edit: an agent with the route, a catalogue, a stack.
const twReady = (v) => { const d = twDataOf(v); return !!d && !d.tooOld && d.available !== false && d.present !== false; };
// The server cannot read its own record of the settings (`recordError` = why): the document it holds and
// what its files are is not known — the answer carries an empty one —, so the page draws no card (an
// edit made against a document that is not the stored one would take the stored settings out at the next
// Apply) and sends nothing.
const twBroken = (v) => { const d = twDataOf(v); return !!d && typeof d.recordError === 'string' && !!d.recordError; };
// An agent that takes over the data files changed outside Relic answers `takenKey` in every answer ("" while it
// has taken none over): its answer says, per card, what it does with the card's data files (twFiles), and it
// applies the settings one at a time — a setting whose file it cannot use is left out (`blocked`), never the
// whole document. An answer without the key is from an agent that refuses a whole document over one such file:
// the page keeps that agent's rules for it (twProblems, twCanStart) and offers nothing it cannot do.
const twKnowsFiles = (d) => !!d && typeof d.takenKey === 'string';
// The takenKey of the answer a request is built on, sent back with every Apply, Revert and Reset — the agent
// refuses a request made before a data file was changed outside Relic; null for an answer without it.
const twTakenKey = (v) => { const d = twDataOf(v); return twKnowsFiles(d) ? d.takenKey : null; };
// What the answer says of the data files of one card (`sections.<card>`): taken = [{file, at: when, undo: a
// Revert has earlier settings to put back, undoOriginal: on the original game's data — false: on an earlier change
// made outside Relic that it had taken over}] — files changed outside Relic that the agent took over: what they hold
// is the base the card's values and settings stand on —; reasons = {file: {why: 'changed' | 'missing' |
// 'refused' | 'level', reason: the agent's words}} — the files of the card it cannot use: a setting that needs
// one cannot be changed —; kept = {file: how many rows changed outside Relic while a multiplier applied to them are
// kept as they are}; blocked = the paths of the settings — stored or in effect (`active`) — that rest on such a
// file: they cannot be changed, and the files keep what they hold for them until the files are put back; dropped =
// {path: {active, tweaks}} — the settings a take-over took out of the documents, with their values before; revert
// / reset = the files a Revert / a Reset of the card acts on, revertMissing / resetMissing = the ones of them it
// cannot act on; levelFiles = the line-up scripts taken over whose domains' enemy level cannot be set. Every list
// is [] and every map {} for an answer without them.
function twFiles(v, section) {
  const d = twDataOf(v), s = (d && d.sections && d.sections[section]) || {};
  const list = (x) => (Array.isArray(x) ? x.filter((f) => typeof f === 'string' && f) : []);
  const map = (x) => (x && typeof x === 'object' && !Array.isArray(x) ? x : {});
  return { taken: (Array.isArray(s.taken) ? s.taken : []).filter((e) => e && typeof e.file === 'string' && e.file), reasons: map(s.reasons),
    kept: map(s.kept), blocked: list(s.blocked), dropped: map(s.dropped), revert: list(s.revertFiles), revertMissing: list(s.revertMissing),
    reset: list(s.resetFiles), resetMissing: list(s.resetMissing), levelFiles: list(s.levelFiles) };
}
// A data file as the page names it: its name, without the folders.
const twFileName = (f) => String(f).split('/').pop();
// A card by its title (a section of the document).
const twCardName = (section) => ({ resin: t('tweaks.resin.title'), abyss: t('tweaks.abyss.title'), domains: t('tweaks.domains.title'),
  quests: t('tweaks.quests.title') }[section] || String(section));
// A list of names in one sentence, parted by `sep` (', '): the first `most` (3) of them, then how many more.
function twNames(names, most, sep) {
  const list = names.filter(Boolean), n = most || 3, by = sep || ', ';
  return list.length > n ? t('tweaks.list.andMore', { names: list.slice(0, n).join(by), n: list.length - n }) : list.join(by);
}
// A job of the version is at work on this server (versionJobRunning). One status says nothing of that:
// the one held when a job this launcher followed here ended — it goes on naming that very job until the
// status is read again.
const twJobRuns = (v) => (jobHere() && String(S.srvJob.version) === String(v))
  || (!!S.srv.info && S.srv.info !== S.tw.settled && versionJobRunning(v));
// What the answer says of the server's FILES — a document that waits, a change without a verdict — is a
// state to report only once no job of the version is at work on them: a job writes them, waits for the
// server's verdict, puts them back. An answer from the middle of such a job, or from before its end,
// tells where the job stood (it is read again once the job is over: `old`).
const twFlux = (v) => twJobRuns(v) || !!S.tw.old[v];
const twCopy = (o) => JSON.parse(JSON.stringify(o == null ? {} : o));
// The document the server holds (what an Apply stored last), as answered.
const twStored = (v) => { const d = twDataOf(v); return d && d.tweaks && typeof d.tweaks === 'object' ? d.tweaks : {}; };
// The draft of a version: a copy of the stored document, made when first asked for; the admin's from then on.
function twDraft(v) {
  const w = S.tw;
  if (!w.drafts[v] || typeof w.drafts[v] !== 'object') w.drafts[v] = twCopy(twStored(v));
  return w.drafts[v];
}

// ── gameplay: the draft, by path ──
// A path names one entry of the document, as a dotted string or a list of segments: 'resin.cap',
// 'abyss.slots.233701003.3001', 'quests.multiplier.archon', 'domains.byId.5006.extra.0.count' (a list
// is walked by index). v = the version, the one on screen when left out.
const twPath = (path) => (Array.isArray(path) ? path.map(String) : String(path).split('.'));
function twAt(doc, path) {
  let o = doc;
  for (const k of twPath(path)) { if (o == null || typeof o !== 'object') return undefined; o = o[k]; }
  return o;
}
// The draft's entry (undefined = the document does not hold it: the server's original value applies).
const twGet = (path, v) => twAt(twDraft(v || twCur()), path);
// Set an entry, making the objects on the way. undefined / null / '' removes it instead — an emptied
// field means "the original".
function twSet(path, value, v) {
  if (value === undefined || value === null || value === '') return twDel(path, v);
  const keys = twPath(path);
  let o = twDraft(v || twCur());
  for (let i = 0; i < keys.length - 1; i++) {
    if (o[keys[i]] == null || typeof o[keys[i]] !== 'object') o[keys[i]] = {};
    o = o[keys[i]];
  }
  o[keys[keys.length - 1]] = value;
}
// Remove an entry of a document (a list entry is cut out of its list) and every object or list left empty
// above it.
function twCut(doc, path) {
  const keys = twPath(path), chain = [doc];
  for (let i = 0; i < keys.length - 1; i++) {
    const next = chain[i][keys[i]];
    if (next == null || typeof next !== 'object') return;
    chain.push(next);
  }
  for (let i = keys.length - 1; i >= 0; i--) {
    const o = chain[i];
    if (Array.isArray(o)) o.splice(Number(keys[i]), 1); else delete o[keys[i]];
    if (Array.isArray(o) ? o.length : Object.keys(o).length) break;
  }
}
// The same for the draft.
const twDel = (path, v) => twCut(twDraft(v || twCur()), path);

// ── gameplay: the canonical document ──
// A whole number as a chip or a field gives it: a number, or a string of digits. undefined = nothing
// (an absent entry, an empty field); NaN = something else was typed.
function twInt(x) {
  if (x === undefined || x === null) return undefined;
  if (typeof x === 'number') return Number.isInteger(x) && x >= 0 ? x : NaN;
  if (typeof x !== 'string') return NaN;
  const s = x.trim();
  if (s === '') return undefined;
  return /^\d{1,9}$/.test(s) ? parseInt(s, 10) : NaN;
}
// The same for the canonical document: the number, undefined for nothing, or the text as it was typed
// when it is no whole number.
const twNum = (x) => { const n = twInt(x); return n === undefined || !isNaN(n) ? n : x; };
// The sections of the document that put monsters into the spawn slots of the server's monster groups,
// each under its `slots`: {group id: {config id: monster id}}.
const TW_SLOT_SECTIONS = ['abyss', 'domains'];
// The domain line-ups of an answer: `lineups` = {monster group id: {dungeons: the domains it serves,
// level: the largest level a spawn slot of its enemies carries, top?: the largest level anything the
// domain places carries — an enemy or an object —, slots: {config id: monster id}, waves?: [[[config id,
// times]]]}}. An agent that can change a domain's enemies always answers the key — empty when its
// catalogue lists no line-up —, so an answer without it is one whose agent cannot (twKnowsLineups): the
// document then takes no enemy of a domain.
const twKnowsLineups = (d) => !!d && !!d.lineups && typeof d.lineups === 'object' && !Array.isArray(d.lineups);
const twLineups = (d) => (twKnowsLineups(d) ? d.lineups : {});
// The range the enemy level of a domain can be set in, [lowest, highest]: the `levels` of the domain's
// entry in the answer, which an agent gives only to a domain whose level it can set. null for every
// other domain: the page draws no level control for it and the document takes none — but for a level a
// domain of a locked line-up still holds (twLevelLocked), which is kept and said in a note until the
// admin takes it out (e = the entry).
const twDomainLevels = (e) => (e && Array.isArray(e.levels) && e.levels.length === 2 && e.levels.every(Number.isInteger) && e.levels[0] <= e.levels[1] ? e.levels : null);
// Whether the answer lets the enemy level of any domain be set.
const twKnowsLevels = (d) => !!d && !!d.domains && typeof d.domains === 'object' && Object.keys(d.domains).some((id) => !!twDomainLevels(d.domains[id]));
// The range of the enemy level over all domains, `limits.domainLevel` = [lowest, highest] — null for an
// answer without it. An agent that can set a domain's enemy level always answers it, whatever its
// catalogue says of the single domains, so an answer without it is one whose agent cannot — the mark
// the line-ups have in their key (twKnowsLineups).
const twDomainLevelLimit = (d) => { const all = d && d.limits ? d.limits.domainLevel : null; return Array.isArray(all) ? all : null; };
// {monster group id: {config id: the original monster id}} of one of those sections, built once per
// answer: the halves of the Abyss chambers, the domain line-ups.
const TW_SLOT_GROUPS = new WeakMap();
function twSlotGroups(d, sec) {
  if (!d || typeof d !== 'object') return {};
  let g = TW_SLOT_GROUPS.get(d);
  if (!g) {
    g = { abyss: {}, domains: {} };
    const chambers = (d.abyss && d.abyss.chambers) || {}, lineups = twLineups(d);
    Object.keys(chambers).forEach((lid) => (chambers[lid].halves || []).forEach((h) => { if (h && h.group != null) g.abyss[h.group] = Object.assign(g.abyss[h.group] || {}, h.slots || {}); }));
    Object.keys(lineups).forEach((gid) => { const lu = lineups[gid]; if (lu && lu.slots && typeof lu.slots === 'object') g.domains[gid] = lu.slots; });
    TW_SLOT_GROUPS.set(d, g);
  }
  return g[sec] || {};
}
// A list of items [{item, count}] with its numbers read (twNum), in its own order.
function twItems(list) {
  return (Array.isArray(list) ? list : []).filter((e) => e && typeof e === 'object').map((e) => {
    const out = {}, count = twNum(e.count), item = twNum(e.item);
    if (count !== undefined) out.count = count;
    if (item !== undefined) out.item = item;
    return out;
  });
}
// Whether a custom reward hands out exactly what the row's original does ([[item, count]], an item may
// sit in two cells of a row).
function twSameReward(items, original) {
  const sum = (pairs) => { const m = {}; pairs.forEach(([i, n]) => { m[i] = (m[i] || 0) + n; }); return m; };
  const a = sum(items.map((e) => [e.item, e.count])), b = sum(Array.isArray(original) ? original : []);
  return Object.keys(a).length === Object.keys(b).length && Object.keys(a).every((i) => a[i] === b[i]);
}
// The canonical form of a document of version v — what "changed" compares and what Apply sends. The
// agent's own rules (normalise_tweaks), against the originals of that version's answer: an entry that
// changes nothing is left out (a value equal to the original, a multiplier of 1, a domain's own value
// equal to what it gets anyway, an empty list or object, a section emptied by that), every number is
// an integer and keys are in sorted order. One rule is the page's own: a quest's custom reward equal to
// the row's original is left out too while its kind has no multiplier (it would change nothing; under
// a multiplier it is what keeps that one quest at its original amounts). A typed value that is no whole
// number stays in as it was typed, so it counts as a change and twProblems names its field. The enemies
// of a domain are kept only for the line-ups and the slots the answer lists: an answer without line-ups
// gives no such key at all, whatever the document holds — its agent would refuse it. A domain's enemy
// level is kept by the same rule: only for a domain the answer gives a range for (twDomainLevels) —
// its agent refuses the key for every other one —, and not when it is the domain's original level. One
// more domain keeps it, as a locked slot is kept: a domain whose line-up script the server does not let
// Relic set the level on (twLevelLocks). Its range rests on that script, so the agent answers none there,
// but a level stored before the script was changed is the admin's to take out (twLockedLevels), never one
// an Apply drops by itself. "The original" is what the answer gives as the base of each value: the original
// game's, or what a file changed outside Relic that the agent took over holds — the agent's normalise_tweaks
// compares with the same (a value equal to it changes nothing, whatever its range). A reward row kept as it
// was changed outside Relic (`kept`) is touched by no multiplier: a custom reward equal to it changes nothing
// either.
function twCanon(v, doc) {
  const d = twDataOf(v) || {}, src = doc && typeof doc === 'object' ? doc : {}, out = {};
  const obj = (o) => (o && typeof o === 'object' && !Array.isArray(o) ? o : {});
  const full = (o) => Object.keys(o).length > 0;
  const most = Number(d.limits && d.limits.multiplierMax) || 10;
  // The spawn slots of a section that hold another monster than the answer's groups list for them;
  // listed = what those groups do not list is left out.
  const slotsOf = (own, groups, listed) => {
    const changed = {};
    Object.keys(obj(own)).sort().forEach((gid) => {
      const kept = {}, mine = obj(own[gid]), original = groups[gid];
      if (listed && !original) return;
      Object.keys(mine).sort().forEach((cfg) => {
        const x = twNum(mine[cfg]);
        if (x !== undefined && !(original && original[cfg] === x) && !(listed && !hasOwn(original, cfg))) kept[cfg] = x;
      });
      if (full(kept)) changed[gid] = kept;
    });
    return changed;
  };

  const abyss = obj(src.abyss), chambers = (d.abyss && d.abyss.chambers) || {};
  const levels = {}, slots = slotsOf(abyss.slots, twSlotGroups(d, 'abyss')), abyssOut = {};
  Object.keys(obj(abyss.levels)).sort().forEach((lid) => {
    const x = twNum(abyss.levels[lid]);
    if (x !== undefined && !(chambers[lid] && chambers[lid].level === x)) levels[lid] = x;
  });
  if (full(levels)) abyssOut.levels = levels;
  if (full(slots)) abyssOut.slots = slots;
  if (full(abyssOut)) out.abyss = abyssOut;

  const dom = obj(src.domains), list = obj(d.domains), ids = Object.keys(list), byId = {}, domOut = {}, levelLocks = twLevelLocks(v);
  const cost = twNum(dom.cost), mult = twNum(dom.multiplier), enemies = slotsOf(dom.slots, twSlotGroups(d, 'domains'), true);
  // One cost for every domain is no change when every domain has that cost already.
  const costKept = cost !== undefined && (!ids.length || ids.some((id) => list[id].cost !== cost));
  const multKept = mult !== undefined && mult !== 1;
  Object.keys(obj(dom.byId)).sort().forEach((id) => {
    const own = obj(dom.byId[id]), e = list[id], entry = {};
    const c = twNum(own.cost), m = twNum(own.multiplier), level = twNum(own.level), extra = twItems(own.extra);
    // What the domain gets anyway: the cost and the multiplier of all domains, the latter never above
    // what its own reward tolerates.
    const top = e ? Math.min(most, Number(e.max) || most) : most;
    if (c !== undefined && c !== (costKept ? cost : e ? e.cost : undefined)) entry.cost = c;
    if (extra.length) entry.extra = extra;
    if (level !== undefined && (twDomainLevels(e) || twLevelLocked(e, levelLocks)) && level !== e.level) entry.level = level;
    if (m !== undefined && m !== Math.min(multKept && typeof mult === 'number' ? mult : 1, top)) entry.multiplier = m;
    if (full(entry)) byId[id] = entry;
  });
  if (full(byId)) domOut.byId = byId;
  if (costKept) domOut.cost = cost;
  if (multKept) domOut.multiplier = mult;
  if (full(enemies)) domOut.slots = enemies;
  if (full(domOut)) out.domains = domOut;

  const quests = obj(src.quests), rewards = (d.quests && d.quests.rewards) || {}, byReward = {}, kinds = {}, questsOut = {};
  Object.keys(obj(quests.multiplier)).sort().forEach((kind) => {
    const x = twNum(quests.multiplier[kind]);
    if (x !== undefined && x !== 1) kinds[kind] = x;
  });
  Object.keys(obj(quests.byReward)).sort().forEach((rid) => {
    const items = twItems(obj(quests.byReward[rid]).items), row = rewards[rid];
    if (!items.length || (row && (row.kept === true || !hasOwn(kinds, row.kind)) && twSameReward(items, row.items))) return;
    byReward[rid] = { items };
  });
  if (full(byReward)) questsOut.byReward = byReward;
  if (full(kinds)) questsOut.multiplier = kinds;
  if (full(questsOut)) out.quests = questsOut;

  const resin = obj(src.resin), was = obj(d.resin), resinOut = {};
  ['cap', 'minutes'].forEach((k) => { const x = twNum(resin[k]); if (x !== undefined && x !== was[k]) resinOut[k] = x; });
  if (full(resinOut)) out.resin = resinOut;
  return out;
}
// The canonical form of the draft / of the stored document, kept until the document or the answer it is
// read against changes: {canon, json}. A keystroke asks for them several times over.
const twCanonMemo = new Map();
function twCanonOf(v, which, doc) {
  const raw = JSON.stringify(doc), d = S.tw.data[v], key = which + '|' + v, m = twCanonMemo.get(key);
  if (m && m.raw === raw && m.data === d) return m;
  const canon = twCanon(v, doc), fresh = { raw, data: d, canon, json: JSON.stringify(canon) };
  twCanonMemo.set(key, fresh);
  return fresh;
}
const twDraftCanon = (v) => twCanonOf(v, 'draft', twDraft(v));
const twStoredCanon = (v) => twCanonOf(v, 'stored', twStored(v));
// The draft differs from what the server holds: there is something to apply.
const twChanged = (v) => twReady(v) && twDraftCanon(v).json !== twStoredCanon(v).json;
// The same for one section ('resin' | 'abyss' | 'domains' | 'quests'): edits of it that are not applied.
function twSectionChanged(section, v) {
  v = v || twCur();
  return twReady(v) && JSON.stringify(twDraftCanon(v).canon[section] || null) !== JSON.stringify(twStoredCanon(v).canon[section] || null);
}
// The draft holds settings in a section: it differs from the original game there, applied or not.
const twSectionSet = (section, v) => { v = v || twCur(); return twReady(v) && !!twDraftCanon(v).canon[section]; };
// An entry of the draft is a setting: it survives the canonical form (a value equal to the original does
// not). An amount of a quest's own reward is one when the reward would not hand out that much of the
// item anyway (twQuestGiven) — the other amounts of such a list are the ones it started from.
function twIsSet(path, v) {
  v = v || twCur();
  if (!twReady(v)) return false;
  const keys = twPath(path);
  if (keys[0] === 'quests' && keys[1] === 'byReward' && keys[3] === 'items' && keys[5] === 'count') {
    const e = twAt(twDraftCanon(v).canon, keys.slice(0, 5));
    return !!e && !twQuestGiven(v, keys[2]).some((g) => g.item === e.item && g.count === e.count);
  }
  return twAt(twDraftCanon(v).canon, path) !== undefined;
}
// The settings of a canonical document, one per path (a list — a reward's items — counts as one).
function twLeaves(doc) {
  const out = {};
  (function walk(o, at) {
    Object.keys(o).forEach((k) => {
      const x = o[k], path = at ? at + '.' + k : k;
      if (x && typeof x === 'object' && !Array.isArray(x)) walk(x, path); else out[path] = JSON.stringify(x);
    });
  })(doc, '');
  return out;
}
// How many settings differ between the draft and the stored document ("N changes not applied yet").
function twChangeCount(v) {
  const a = twLeaves(twDraftCanon(v).canon), b = twLeaves(twStoredCanon(v).canon);
  return Object.keys(a).filter((k) => a[k] !== b[k]).length + Object.keys(b).filter((k) => !hasOwn(a, k)).length;
}

// ── gameplay: the display catalogue ──
const TW_KINDS = ['monsters', 'items', 'domains', 'quests'];
// The display entry of an id — the catalogue's {id, name, icon?, …} plus s, its search key — or, for an
// id the catalogue lacks (or while it is not read), the same shape named "#<id>" without a sprite: a
// caller draws either without asking. kind: 'monsters' | 'items' | 'domains' | 'quests'.
function twEntry(kind, id) {
  const by = S.twData.by[kind], e = by && by[id];
  return e || { id: num(id), name: '#' + id, s: String(id), unknown: true };
}
const twName = (kind, id) => twEntry(kind, id).name;
// Every entry of a kind, in the catalogue's order ([] while it is not read).
const twList = (kind) => S.twData.list[kind] || [];
// tweakdata.get: {status: 'ok', data} | {status: 'missing'} | {status: 'error', error}. Read once; a
// read that did not give a catalogue is tried again the next time the page opens, and the page works
// on ids meanwhile.
async function loadTweakData() {
  const c = S.twData;
  if (c.status === 'ok' || c.status === 'loading') return;
  c.status = 'loading'; c.error = '';
  try {
    const r = await rpc('tweakdata.get', {});
    const data = r && r.status === 'ok' && r.data && typeof r.data === 'object' ? r.data : null;
    if (data) {
      TW_KINDS.forEach((kind) => {
        const list = normList(data[kind], (e) => {
          const id = num(e && e.id);
          if (!id) return null;
          const name = String(e.name || '#' + id);
          return Object.assign({}, e, { id, name, s: searchKey(e.label, name, e.variant, e.family, e.city, id) });
        });
        c.list[kind] = list; c.by[kind] = {};
        list.forEach((e) => { c.by[kind][e.id] = e; });
      });
      c.status = 'ok';
    } else { c.status = r && r.status === 'missing' ? 'missing' : 'error'; c.error = String((r && r.error) || ''); }
  } catch (e) { c.status = 'error'; c.error = e.message || String(e); }
  twRedraw();
}

// ── gameplay: reads ──
// The page after something it shows changed behind it — an answer landed, a read began or failed: drawn
// again, or, while a field has the caret, brought up to date in place (twSync): a render would take the
// caret out of the field being typed in.
function twRedraw() {
  if (S.screen !== 'tweaks') return;
  if (typingNow()) twSync(); else render();
}
// GET /server/tweaks of one version. The last request per version wins, and only under the target it was
// sent to and for the page state it was sent for — the rule of loadHotpatch (srvRetarget / deploy.done
// replace S.tw; the counter is module-wide so a new S.tw never re-issues a number an older request
// holds). A failed read keeps the answer it had (the draft is read against it) and says why; loud = the
// admin pressed Refresh and wants the reason as a toast too.
let twSeq = 0;
async function loadTweaks(id, loud) {
  if (!isAdminMode() || !S.serverConfigured || !id) return;
  const w = S.tw, seq = ++twSeq, target = srvTarget(), during = twJobRuns(id);
  const mine = () => S.tw === w && w.seq[id] === seq && srvTarget() === target;
  w.seq[id] = seq; w.loading[id] = true;
  twRedraw();
  try {
    const d = await rpc('server.tweaks.get', { version: id });
    if (!mine()) return;
    if (!d || typeof d !== 'object') throw new Error(t('tweaks.note.badAnswer'));
    twIngest(id, d);
    w.error[id] = ''; w.up[id] = twUp(id);
    // Asked or answered while a job of the version ran: the answer is from the middle of it (see `old`) —
    // and so is one the agent says it read while the files were being changed (`changing`).
    w.old[id] = during || twJobRuns(id) || d.changing === true;
  } catch (e) {
    if (!mine()) return;
    w.error[id] = e.message || String(e);
    if (loud) showToast(w.error[id], 'bad');
  }
  w.loading[id] = false;
  twRedraw();
}
// What the values of an answer stand on, as one string, built once per answer: the key of the files taken over,
// the files a card cannot use or whose levels it locks, and every value the page reads from a file's base — the
// resin row, the chambers' levels, the spawn slots of the Abyss halves and of the line-ups, each domain's cost,
// enemy level and range and whether its reward row is kept, what each reward row hands out and whether it is
// kept. Two answers with the same string read a draft alike.
const TW_BASES = new WeakMap();
function twBases(d) {
  if (!d || typeof d !== 'object') return '';
  let s = TW_BASES.get(d);
  if (s === undefined) {
    const obj = (o) => (o && typeof o === 'object' ? o : {});
    const pick = (o, f) => Object.keys(obj(o)).sort().map((k) => [k, f(obj(obj(o)[k]))]);
    s = JSON.stringify([d.takenKey === undefined ? null : d.takenKey, d.resin || null, pick(d.sections, (c) => [c.files || null, c.levelFiles || null]),
      pick(d.abyss && d.abyss.chambers, (c) => c.level), twSlotGroups(d, 'abyss'), twSlotGroups(d, 'domains'),
      pick(d.domains, (e) => [e.cost, e.level, e.levels || null, e.kept === true, e.max]),
      pick(d.quests && d.quests.rewards, (r) => [r.items || null, r.kept === true])]);
    TW_BASES.set(d, s);
  }
  return s;
}
// A fresh answer for a version. The draft follows the server while the admin has no edits in it, and
// after the page's own job (adopt) when it still is the draft that job started from: what the agent
// stored is its new starting point. Edits that are not applied — made while the job ran, too — stay. When
// the values of the answer stand on other bases than the ones the edits were made on (a file changed outside
// Relic taken over, a card's files put back), the draft is first put in its canonical form against the answer
// it was made on: a number equal to what a file's base held then leaves it — it meant "what the base holds",
// never a setting of that number. When the stored document changed under the edits (a take-over, another
// admin's Apply, a Revert or a Reset) the draft is then rebased on it (twRebase). An Apply question open over
// the draft is asked again over the new answer (twApplyAskAgain).
function twIngest(v, d) {
  const w = S.tw, before = twDataOf(v);
  const adopt = hasOwn(w.adopt, v) && (!w.drafts[v] || twDraftCanon(v).json === w.adopt[v]);
  const keep = !adopt && !!w.drafts[v] && twChanged(v);
  const stored = (x) => JSON.stringify(x && x.tweaks && typeof x.tweaks === 'object' ? x.tweaks : {});
  if (keep && twBases(before) !== twBases(d)) w.drafts[v] = twCopy(twDraftCanon(v).canon);
  if (keep && stored(before) !== stored(d)) twRebase(v, d);
  w.data[v] = d;
  delete w.adopt[v];
  if (!keep) delete w.drafts[v];
  if (S.confirm && S.confirm.act === 'tweaksApplyGo' && S.confirm.tw === v && twCur() === v) twApplyAskAgain(v);
}
// An Apply question open over the draft of a version, asked again over what the page holds now: it stays as it
// was asked while it says and sends the same — the canonical draft and the takenKey it goes with (`sends`) —,
// else the question of now takes its place, armed again so that it is read before it is answered, or none when
// there is nothing left to apply; a toast says why.
function twApplyAskAgain(v) {
  const held = S.confirm;
  S.confirm = null;
  ACTIONS.tweaksApply();
  const now = S.confirm;
  if (now && now.title === held.title && now.body === held.body && now.sends === held.sends) S.confirm = held;
  else if (now) showToast(t('toast.askChanged'), 'bad');
  else if (!twProblems(v).length) showToast(t('toast.askGone'), 'bad');
}
// The draft of a version rebased on a stored document that changed under its edits (d = the new answer; the
// old one is still the page's when this runs). One setting at a time — a leaf of the canonical documents, a
// list (a reward's items, a domain's extra items) being one: where the draft says what the old stored document
// said (both read against the old answer), the draft takes the new stored document's value — or loses the
// entry, when the new one has none; where it says something else, the admin's own edit, it keeps it as it was
// typed — a setting taken out stays out: it means "what the file's base holds". Returns whether the draft
// changed.
function twRebase(v, d) {
  const draft = S.tw.drafts[v], next = d && d.tweaks && typeof d.tweaks === 'object' ? d.tweaks : {};
  const mine = twLeaves(twCanon(v, draft)), was = twLeaves(twCanon(v, twStored(v))), now = twLeaves(next);
  const same = (x, y) => JSON.stringify(x === undefined ? null : x) === JSON.stringify(y === undefined ? null : y);
  let changed = false;
  new Set(Object.keys(mine).concat(Object.keys(was), Object.keys(now))).forEach((path) => {
    if (mine[path] !== was[path]) return;
    const value = twAt(next, path);
    if (same(value, twAt(draft, path))) return;
    if (value === undefined) twCut(draft, path); else twSet(path, twCopy(value), v);
    changed = true;
  });
  return changed;
}
// Entering the page: the display catalogue (once), the answers and a status read (the page's chips are
// the versions the server has). What the page holds is from an earlier visit — what the server runs and
// holds may have changed —, so every answer counts as old: the version on screen is read again at once,
// another one when its chip is picked. The quiet status read: the cards are drawn at once from the answer
// the page holds, and a field of them may have the caret before the status answers.
function twEnter() {
  loadTweakData();
  const w = S.tw, v = twCur();
  w.busy = {};
  Object.keys(w.data).forEach((id) => { w.old[id] = true; });
  if (v) { if (!w.version) w.version = v; loadTweaks(v); }
  refreshServersQuiet();
}
// The status' word on whether a version's stack runs.
const twUp = (v) => { const f = srvFacts(v); return !!(f && f.up); };
// A status read has just named the server's versions while the page is open: read the one on screen
// when that was not done yet — the first status of a server lands after the page opened, or after the
// server changed — and again when its stack went up or down since its answer was read (whether the
// server runs decides what an Apply does, and a Start applies a document that waited), or when a job of
// it that the status named is over: another admin's Apply stored another document, and an Apply from
// this page sends the WHOLE document — built on the old answer it would take their settings out again.
// (Such a read is sent at once: one still in flight was asked while the job ran, and the newer request
// wins.) A read that failed waits for Refresh. A question the page asked is about the version that was
// on screen: it goes when another one takes its place.
function twOpen() {
  if (S.screen !== 'tweaks' || !isAdminMode() || !S.serverConfigured) return;
  const w = S.tw, b = S.srv.info && S.srv.info.busy;
  const seen = b && b.version != null && !(jobHere() && String(S.srvJob.version) === String(b.version)) ? String(b.version) : '';
  const over = Object.keys(w.busy).filter((id) => id !== seen);
  over.forEach((id) => { delete w.busy[id]; if (hasOwn(w.data, id)) w.old[id] = true; });
  if (seen) w.busy[seen] = true;
  const v = twCur();
  if (S.confirm && S.confirm.tw !== undefined && S.confirm.tw !== v) S.confirm = null;
  if (!v) return;
  if (!w.version) w.version = v;
  if (over.includes(v)) { loadTweaks(v); return; }
  if (w.loading[v] || w.error[v]) return;
  if (!hasOwn(w.data, v) || w.up[v] !== twUp(v) || (w.old[v] && !twJobRuns(v))) loadTweaks(v);
}
// After a job of this server what the page holds of that version is from before it: its own job stored
// a document (a finished one hands the draft over to the re-read, see twIngest), a Start applies one
// that waited, a Stop or a Prepare changes whether the server runs and what its files hold. Read again
// when the version was read before. The status held at this moment still names the job (twJobRuns).
function twAfterJob(j, ok) {
  const v = String(j.version == null ? '' : j.version), w = S.tw;
  if (!v) return;
  if (j.kind === 'tweaks') {
    if (ok && hasOwn(w.sent, v)) w.adopt[v] = w.sent[v];
    delete w.sent[v];
  }
  w.settled = S.srv.info;
  if (hasOwn(w.data, v)) w.old[v] = true;
  if (hasOwn(w.data, v) || w.error[v] || (S.screen === 'tweaks' && twCur() === v)) loadTweaks(v);
}
// What a finished job of the page says (the agent's result: {changed, effect, clamped, blocked?}) — of a Revert
// or a Reset of a card's files (twOp of the job this launcher started), that the files are back first — and how
// many settings the server could not change (a file they need cannot be used: the files keep what they hold for
// them).
function twDoneText(j, res) {
  const r = res || {}, version = j.version, files = j.twOp === 'revert' || j.twOp === 'reset';
  const text = files ? (r.effect === 'live' ? t('tweaks.toast.filesLive', { version }) : r.effect === 'next-start' ? t('tweaks.toast.filesNextStart', { version })
    : t('tweaks.toast.filesBack', { version }))
    : r.effect === 'live' ? t('tweaks.toast.live', { version })
      : r.effect === 'next-start' ? t('tweaks.toast.nextStart', { version })
        : r.effect === 'none' ? t('tweaks.toast.none', { version })
          : t('toast.jobDone', { job: jobTitleOf(j), version });
  const n = Number(r.clamped) || 0, b = Array.isArray(r.blocked) ? r.blocked.length : 0;
  return text + (n > 0 ? ' ' + (n === 1 ? t('tweaks.toast.clamped1') : t('tweaks.toast.clamped', { n })) : '')
    + (b > 0 ? ' ' + (b === 1 ? t('tweaks.toast.blocked1') : t('tweaks.toast.blocked', { n: b })) : '');
}

// ── gameplay: what Apply checks ──
// What a card the server switched off stops. The answer's `sections` names, per card, the data files the
// server does not let Relic rewrite — changed outside Relic and not taken over, missing, or without what
// the card edits — and the agent leaves out only the settings that NEED such a file (an agent that refuses
// a whole document refuses one that needs it): a setting that needs none of them stays in the document and
// in use, behind a card that cannot edit it. What needs which file is a matter of the server's tables,
// told apart by their names: a domain's statue cost and the level of its enemies are DungeonData's — two
// cells of the domain's row —, what a statue drops and what the daily commissions give is DropTreeData's,
// what a quest hands out is RewardData's. The Resin card has one table (ConstValueData); the Abyss card is
// switched off by its level table (TowerLevelData) alone.
// The script of a monster group — a half of an Abyss chamber, a domain's line-up — is no table: named
// among a card's files it takes only its own slots out (twLocked), whether the card is on or off. Any
// other file the page cannot tell — or a card switched off with no file named — counts against every
// setting of that card.
const TW_TABLES = { costs: /(^|\/)DungeonData\.txt$/, drops: /(^|\/)DropTreeData\.txt$/, rewards: /(^|\/)RewardData\.txt$/,
  levels: /(^|\/)TowerLevelData\.txt$/, resin: /(^|\/)ConstValueData\.txt$/ };
// The script of a monster group as `sections.<card>.files` names it: "…_group<group id>.lua".
const TW_GROUP_FILE = /_group(\d+)\.lua$/;
// The tables of a version the server does not let Relic rewrite, {resin, levels, costs, drops, rewards,
// daily}: levels is the Abyss card's (the level of a chamber), costs + drops are the Domain card's —
// costs = DungeonData, the statue costs and the domains' own enemy levels alike —, rewards + daily (the
// drop table again) the Quest card's.
function twOff(v) {
  const s = (twDataOf(v) || {}).sections || {};
  const dead = (name) => !!s[name] && s[name].ok === false;
  const tables = (name, own) => {
    if (!dead(name)) return {};
    const files = (Array.isArray(s[name].files) ? s[name].files : []).map(String).filter((f) => !TW_GROUP_FILE.test(f));
    const stray = !files.length || files.some((f) => !TW_TABLES[own].test(f) && !TW_TABLES.drops.test(f));
    return { own: stray || files.some((f) => TW_TABLES[own].test(f)), drops: stray || files.some((f) => TW_TABLES.drops.test(f)) };
  };
  const dom = tables('domains', 'costs'), quests = tables('quests', 'rewards');
  return { resin: dead('resin'), levels: dead('abyss'), costs: !!dom.own, drops: !!dom.drops, rewards: !!quests.own, daily: !!quests.drops };
}
// The quest kinds an answer has reward rows of, {kind: true}, built once per answer.
const TW_REWARD_KINDS = new WeakMap();
function twRewardKinds(d) {
  let kinds = TW_REWARD_KINDS.get(d);
  if (!kinds) {
    kinds = {};
    const rewards = (d.quests && d.quests.rewards) || {};
    Object.keys(rewards).forEach((rid) => { kinds[rewards[rid].kind] = true; });
    TW_REWARD_KINDS.set(d, kinds);
  }
  return kinds;
}
// Which of the tables twOff names a document NEEDS, {resin, levels, costs, drops, rewards, daily: true} —
// empty when every card is on, or when nothing of the document needs a table a card is off for. doc = a
// canonical document (numbers, no entry that changes nothing). The rules are the agent's own, per file:
// a domain costs what its own entry says, else what the entry of all domains says, and the cost table is
// needed when that is not its original cost — or when its own entry sets another enemy level than its
// original one: the level is a cell of the same table; its multiplier likewise — never above what its
// reward tolerates — and the drop table is needed when it is not 1, or for an extra item; the reward
// table is needed for the multiplier of a quest kind that has reward rows and for a quest's own reward;
// the commissions' multiplier needs the drop table as well (the daily commissions' rewards are rows of
// it). The enemies of an Abyss chamber or of a domain need no table: each needs the script of its own
// monster group (twLockedIn).
function twOffNeeds(v, doc) {
  const off = twOff(v), d = twDataOf(v) || {}, src = doc && typeof doc === 'object' ? doc : {}, needs = {};
  const obj = (o) => (o && typeof o === 'object' && !Array.isArray(o) ? o : null);
  if (off.resin && obj(src.resin)) needs.resin = true;
  if (off.levels && obj(src.abyss) && obj(src.abyss.levels)) needs.levels = true;
  const dom = obj(src.domains);
  if (dom && (off.costs || off.drops)) {
    const list = obj(d.domains) || {}, ids = Object.keys(list), byId = obj(dom.byId) || {}, most = Number(d.limits && d.limits.multiplierMax) || 10;
    const gets = (id, key, original) => (hasOwn(byId[id], key) ? byId[id][key] : hasOwn(dom, key) ? dom[key] : original);
    const any = (key) => hasOwn(dom, key) || Object.keys(byId).some((id) => hasOwn(byId[id], key));
    // (the enemy level has no entry of all domains: a domain's is its own entry's, else its original one)
    const level = (id) => (hasOwn(byId[id], 'level') ? byId[id].level : list[id].level);
    // (an answer that lists no domain cannot say what a setting changes: every one of them counts)
    if (off.costs && (ids.length ? ids.some((id) => gets(id, 'cost', list[id].cost) !== list[id].cost || level(id) !== list[id].level)
      : any('cost') || any('level'))) needs.costs = true;
    if (off.drops && (ids.length ? ids.some((id) => {
      const m = gets(id, 'multiplier', 1), extra = byId[id] && byId[id].extra;
      return (Array.isArray(extra) && extra.length > 0) || typeof m !== 'number' || Math.min(m, most, Number(list[id].max) || most) !== 1;
    }) : any('multiplier') || any('extra'))) needs.drops = true;
  }
  const quests = obj(src.quests);
  if (quests && (off.rewards || off.daily)) {
    const mult = obj(quests.multiplier) || {}, kinds = twRewardKinds(d), known = Object.keys(kinds).length > 0;
    if (off.rewards && (Object.keys(mult).some((kind) => !known || kinds[kind]) || Object.keys(obj(quests.byReward) || {}).length > 0)) needs.rewards = true;
    if (off.daily && hasOwn(mult, 'commission')) needs.daily = true;
  }
  return needs;
}
// The card each of those tables switches off, and whether what a document needs stops a card's settings.
const TW_OFF_CARD = { resin: 'resin', levels: 'abyss', costs: 'domains', drops: 'domains', rewards: 'quests', daily: 'quests' };
const twOffStops = (needs, section) => Object.keys(needs).some((table) => TW_OFF_CARD[table] === section);
// The entries of a document that are tied to the tables of `needs`, as paths of the document, for one
// card: what the card's button takes out of the draft. doc = the draft as it is typed: every entry of
// the kind goes, whatever its value — one that changes nothing beside the others (a domain's own cost
// equal to the cost of all domains) would change something once they are gone. With the cost table the
// domains' own enemy levels go too: they are cells of that table. The Domain card's button also takes
// out the enemies of a line-up whose script is among the card's files (twLockedIn) and the enemy level of
// a domain that spawns from such a line-up (twLockedLevels): with the card off, no other control of the
// page reaches them.
function twOffPaths(v, section, doc, needs) {
  const own = doc && doc[section], out = [];
  if (!own || typeof own !== 'object') return out;
  if (section === 'resin') return needs.resin ? ['resin'] : out;
  if (section === 'abyss') return needs.levels && hasOwn(own, 'levels') ? ['abyss.levels'] : out;
  if (section === 'domains') {
    const byId = own.byId && typeof own.byId === 'object' ? own.byId : {};
    const mine = (key) => Object.keys(byId).filter((id) => hasOwn(byId[id], key)).map((id) => `domains.byId.${id}.${key}`);
    const held = (key) => (hasOwn(own, key) ? ['domains.' + key] : []).concat(mine(key));
    const paths = (needs.costs ? held('cost').concat(mine('level')) : []).concat(needs.drops ? held('multiplier').concat(held('extra')) : [],
      twLockedIn(v, 'domains', doc).map((gid) => `domains.slots.${gid}`), twLockedLevels(v, doc).map((id) => `domains.byId.${id}.level`));
    return paths.filter((path, at) => paths.indexOf(path) === at);
  }
  const mult = own.multiplier && typeof own.multiplier === 'object' ? own.multiplier : {}, kinds = twRewardKinds(twDataOf(v) || {}), known = Object.keys(kinds).length > 0;
  if (needs.rewards) {
    Object.keys(mult).forEach((kind) => { if (!known || kinds[kind]) out.push('quests.multiplier.' + kind); });
    if (hasOwn(own, 'byReward')) out.push('quests.byReward');
  }
  if (needs.daily && hasOwn(mult, 'commission') && !out.includes('quests.multiplier.commission')) out.push('quests.multiplier.commission');
  return out;
}
// The monster groups whose script the server does not let Relic rewrite, {group id: the file}, for one
// of the sections that place monsters: `sections.<section>.files` names each as "…_group<group id>.lua".
// Such a script never switches its card off (only a table does); the slots of the group are shown and
// cannot be changed.
function twLocked(v, sec) {
  const d = twDataOf(v), s = d && d.sections && d.sections[sec], out = {};
  (s && Array.isArray(s.files) ? s.files : []).forEach((f) => {
    const m = TW_GROUP_FILE.exec(String(f));
    if (m) out[m[1]] = String(f);
  });
  return out;
}
// The groups among them that a document changes, [group ids]: the agent does not apply a setting that needs
// such a script (an agent that refuses a whole document over it refuses the document).
function twLockedIn(v, sec, doc) {
  const locked = twLocked(v, sec), own = doc && doc[sec] && doc[sec].slots;
  return Object.keys(own && typeof own === 'object' ? own : {}).filter((gid) => hasOwn(locked, gid));
}
// The line-ups whose domains' enemy level the server does not let Relic set, {group id: the file}: the ones
// whose script it cannot use (twLocked) and the ones whose script it took over with an entry above the level the
// line-up is listed with (`levelFiles` of the Domain card — their slots stay editable: they are no locked group).
function twLevelLocks(v) {
  const out = Object.assign({}, twLocked(v, 'domains'));
  twFiles(v, 'domains').levelFiles.forEach((f) => { const m = TW_GROUP_FILE.exec(f); if (m) out[m[1]] = f; });
  return out;
}
// Whether a domain of the answer (its entry) spawns from a line-up whose script is among `locked`
// (twLevelLocks): the range of its enemy level rests on that script.
const twLevelLocked = (e, locked) => !!e && e.group != null && hasOwn(locked, String(e.group));
// The domains whose enemy level a document sets while the script of their line-up is one the server does
// not let Relic set it on, [dungeon ids]: the agent does not apply such a level, as it does not apply a change
// of a slot of a locked script (an agent that refuses a whole document over it refuses the document).
function twLockedLevels(v, doc) {
  const locked = twLevelLocks(v), list = (twDataOf(v) || {}).domains || {};
  const byId = doc && doc.domains && doc.domains.byId && typeof doc.domains.byId === 'object' ? doc.domains.byId : {};
  return Object.keys(byId).filter((id) => !!byId[id] && hasOwn(byId[id], 'level') && twLevelLocked(list[id], locked));
}
// Whether the server can bring a document into effect at all, as far as the page can tell. An agent that
// applies the settings one at a time (twKnowsFiles) brings in every setting it can use and leaves the others
// out (`blocked`): always. For one that refuses a whole document: nothing of it needs a file the server does
// not let Relic rewrite — a table (twOffNeeds) or the script of a monster group (twLockedIn, and the enemy
// level a domain's line-up script bounds: twLockedLevels). That agent refuses an Apply of a document that
// does, and its step of a Start gives up on one, the whole of it.
function twCanStart(v, doc) {
  if (twKnowsFiles(twDataOf(v))) return true;
  const src = doc && typeof doc === 'object' ? doc : {};
  return !Object.keys(twOffNeeds(v, src)).length && !TW_SLOT_SECTIONS.some((sec) => twLockedIn(v, sec, src).length > 0)
    && !twLockedLevels(v, src).length;
}
// The cards whose settings in a document rest on a data file the server cannot use, [sections] in the page's
// order: what Apply's question names, for an agent that applies the other settings. Such a setting is one the
// agent itself names (`blocked`: a file an editor refuses is known to it alone — stored or in effect) or one the
// page tells by the files a card names (twOffNeeds, twLockedIn, twLockedLevels); a card is named when the
// document holds such a setting, or says something else of it than the settings in effect (`active`) — one
// taken out of the document too: the files keep what they hold for it until they are put back.
function twBlockedCards(v, doc) {
  const d = twDataOf(v) || {}, src = doc && typeof doc === 'object' ? doc : {}, needs = twOffNeeds(v, src);
  const mine = twLeaves(twCanon(v, src)), held = twLeaves(twCanon(v, d.active));
  const under = (path, prefix) => path === prefix || path.startsWith(prefix + '.');
  const touched = (prefix) => Object.keys(mine).some((path) => under(path, prefix))
    || Object.keys(held).some((path) => under(path, prefix) && mine[path] !== held[path]);
  return TW_SECTIONS.filter((section) => twFiles(v, section).blocked.concat(twOffStops(needs, section) ? twOffPaths(v, section, src, needs) : [],
    TW_SLOT_SECTIONS.includes(section) ? twLockedIn(v, section, src).map((gid) => `${section}.slots.${gid}`) : [],
    section === 'domains' ? twLockedLevels(v, src).map((id) => `domains.byId.${id}.level`) : []).some(touched));
}
// The numbers of the draft that the agent would refuse, as [{path, text}] in the order of the page —
// the first one is the toast, and its field gets the caret. The agent's own check is the judge
// (normalise_tweaks answers 400 naming the path); this one spares the round-trip and names the field
// in the page's words. Ranges come from the answer's `limits`, an item's largest count from `items`.
function twProblems(v) {
  const d = twDataOf(v) || {}, doc = twDraftCanon(v).canon, lim = d.limits || {}, out = [];
  const pair = (x, lo, hi) => (Array.isArray(x) && x.length === 2 ? x : [lo, hi]);
  // shift = what the field of the entry adds to it on screen (twTyped): the range is said as it is typed.
  const range = (path, x, [lo, hi], field, shift = 0) => {
    if (typeof x !== 'number' || x < lo || x > hi) out.push({ path, text: t('tweaks.bad.number', { field, min: lo + shift, max: hi + shift }) });
  };
  const most = Number(lim.multiplierMax) || 10;
  const items = (path, list, where) => list.forEach((e, i) => {
    const cap = d.items && d.items[e.item];
    if (typeof e.item !== 'number' || !cap) return out.push({ path: `${path}.${i}.item`, text: t('tweaks.bad.item', { where }) });
    range(`${path}.${i}.count`, e.count, [1, Math.max(1, Number(cap[0]) || 1)], t('tweaks.field.amount', { item: twName('items', e.item), where }));
  });

  // Settings that need a file the server does not let Relic use — a table of a card it switched off
  // (twOffNeeds), the script of a monster group (twLockedIn, and the enemy level a line-up's script bounds:
  // twLockedLevels). An agent that applies the settings one at a time leaves the files as they hold those and
  // applies the rest (twKnowsFiles): no obstacle — Apply's question names their cards (twBlockedCards). An
  // agent that refuses a whole document over such a file would refuse this one: they
  // have to leave the draft first (the card's own button does that). The card's other settings are no
  // obstacle either way.
  const whole = !twKnowsFiles(d);
  const needs = whole ? twOffNeeds(v, doc) : {};
  TW_SECTIONS.forEach((section) => {
    if (twOffStops(needs, section)) out.push({ path: section, text: t('tweaks.bad.sectionOff', { card: twCardName(section) }) });
  });

  const resin = doc.resin || {}, was = d.resin || {};
  if (hasOwn(resin, 'cap')) {
    const [lo, hi] = pair(lim.resinCap, 1, 2000);
    range('resin.cap', resin.cap, [lo, Math.min(hi, Number(was.hardMax) || hi)], t('tweaks.resin.cap'));
  }
  if (hasOwn(resin, 'minutes')) range('resin.minutes', resin.minutes, pair(lim.minutes, 1, 1440), t('tweaks.resin.minutes'));

  const abyss = doc.abyss || {}, chambers = (d.abyss && d.abyss.chambers) || {}, floors = (d.abyss && d.abyss.floors) || {};
  const place = (lid) => { const ch = chambers[lid] || {}, floor = floors[ch.floor] || {}; return { floor: floor.number || '?', chamber: ch.index || '?' }; };
  Object.keys(abyss.levels || {}).forEach((lid) => {
    range(`abyss.levels.${lid}`, abyss.levels[lid], pair(lim.level, 1, 199), t('tweaks.field.abyssLevel', place(lid)), TW_ABYSS_SLOT_LEVEL);
  });
  // Enemies of a monster group whose script the server does not let Relic rewrite: an agent that refuses a
  // whole document over it would refuse this one, so they leave the draft first (the chamber's own button
  // does that).
  const chamberOf = twAbyssFacts(d).lid;
  if (whole) twLockedIn(v, 'abyss', doc).forEach((gid) => out.push({ path: `abyss.slots.${gid}`, text: t('tweaks.bad.abyssScript', place(chamberOf[gid])) }));

  const dom = doc.domains || {};
  if (hasOwn(dom, 'multiplier')) range('domains.multiplier', dom.multiplier, [1, most], t('tweaks.field.domainsMultiplier'));
  if (hasOwn(dom, 'cost')) range('domains.cost', dom.cost, pair(lim.cost, 1, 200), t('tweaks.field.domainsCost'));
  Object.keys(dom.byId || {}).forEach((id) => {
    const own = dom.byId[id], e = (d.domains && d.domains[id]) || {}, domain = twDomainName(v, id);
    if (hasOwn(own, 'multiplier')) range(`domains.byId.${id}.multiplier`, own.multiplier, [1, Math.min(most, Number(e.max) || most)], t('tweaks.field.domainMultiplier', { domain }));
    // (a cost equal to the one its file's base holds is the domain's own cost kept against the cost for all
    //  domains — a value of the base, whatever the range: the agent takes it as it is)
    if (hasOwn(own, 'cost') && own.cost !== e.cost) range(`domains.byId.${id}.cost`, own.cost, pair(lim.cost, 1, 200), t('tweaks.field.domainCost', { domain }));
    items(`domains.byId.${id}.extra`, own.extra || [], domain);
    // The enemy level against the range the answer gives that domain; the document holds the number the
    // field shows, so the range is said as it is.
    const levels = twDomainLevels(e);
    if (hasOwn(own, 'level') && levels) range(`domains.byId.${id}.level`, own.level, levels, t('tweaks.field.domainLevel', { domain }));
  });
  // The enemies of a line-up whose script the server does not let Relic rewrite, like those of an Abyss
  // group, for an agent that refuses a whole document over it: they leave the draft first (the button of
  // the line-up's own note does that). The text names the domain twReveal puts on screen for it.
  if (whole) {
    twLockedIn(v, 'domains', doc).forEach((gid) => {
      const id = twLineupDomain(v, gid);
      out.push({ path: `domains.slots.${gid}`, text: t('tweaks.bad.domainScript', { domain: id ? twDomainName(v, id) : '#' + gid }) });
    });
    // An enemy level whose range rests on such a script: it leaves the draft first (the domain's own note
    // and the line-up's both have the button).
    twLockedLevels(v, doc).forEach((id) => out.push({ path: `domains.byId.${id}.level`, text: t('tweaks.bad.domainLevelScript', { domain: twDomainName(v, id) }) }));
  }

  const quests = doc.quests || {}, rewards = (d.quests && d.quests.rewards) || {};
  Object.keys(quests.multiplier || {}).forEach((kind) => range(`quests.multiplier.${kind}`, quests.multiplier[kind], [1, most], t('tweaks.field.questMultiplier', { kind: twQuestKindName(kind) })));
  Object.keys(quests.byReward || {}).forEach((rid) => {
    const quest = ((rewards[rid] || {}).quests || [])[0];
    items(`quests.byReward.${rid}.items`, quests.byReward[rid].items || [], quest ? twName('quests', quest) : '#' + rid);
  });
  // The agent replaces a limited number of quest rewards in one document.
  const custom = Number(lim.customMax) || 0;
  if (custom && Object.keys(quests.byReward || {}).length > custom) out.push({ path: 'quests.byReward', text: t('tweaks.bad.customMax', { max: custom }) });
  return out;
}
// The stored document is not what the server runs on — it refused it, did not confirm it, its files
// do not hold it while it runs, or the running server has not read its files since they last changed
// (`unread`, said only while it runs): sending it again is something to do although the draft holds no
// edit — an Apply asks the server to read them. (A stopped server takes a waiting document at its next
// Start by itself; and what the files hold is no state while a job of the version is at work on them:
// twFlux.) `pending` is about what the server can bring into effect: a setting it cannot apply (`blocked`, a
// file it needs cannot be used) waits for a Revert or a Reset of its card, never for an Apply, and makes
// nothing pending. For an agent that refuses a whole document, not a document that cannot be sent as it is
// — one that needs a file the server does not let Relic rewrite (twCanStart): the settings that need the
// file have to leave it first, which is an edit.
const twRetry = (v) => {
  const d = twDataOf(v);
  return !!d && (!!d.refused || (!twFlux(v) && (d.unconfirmed === true || (d.pending === true && d.running !== false)
    || (d.unread === true && d.running !== false))))
    && twCanStart(v, twStoredCanon(v).canon);
};
// The card of an entry Apply refuses puts it on screen before the field is looked for — state only, the
// caller draws: an Abyss chamber other than the one the card shows, a domain or a quest reward whose
// editor is not the open one — for the enemies of a line-up the domain the line-up is named by
// (twLineupDomain).
function twReveal(path, v) {
  const keys = twPath(path);
  const lid = keys[0] !== 'abyss' ? '' : keys[1] === 'levels' ? keys[2] : keys[1] === 'slots' ? twAbyssFacts(twDataOf(v)).lid[keys[2]] : '';
  if (lid) twAbyssGo(v, lid);
  if (keys[0] === 'domains' && keys[1] === 'byId' && keys[2]) twDomainGo(v, keys[2]);
  if (keys[0] === 'domains' && keys[1] === 'slots' && keys[2]) {
    const id = twLineupDomain(v, keys[2]);
    if (id) twDomainGo(v, id);
  }
  if (keys[0] === 'quests' && keys[1] === 'byReward' && keys[2]) twQuestGo(v, keys[2]);
}

// ── gameplay: the page ──
// What the page on screen was drawn for. A render draws it for the version twCur() names, from S.tw —
// and a status read or an answer that lands while one of its fields has the caret is not rendered (the
// caret would go): until the next render the page on screen may be another version's than twCur() names
// by then (the one it shows is gone from the server: its folder is being moved, say). What is typed
// into it belongs to the version it SHOWS, and a click on it draws the page anew instead of acting on a
// version the admin is not looking at.
let twDrawnFrom = null;
// The version that page shows ('' = none), null when it is not on screen or was drawn from the documents
// of another server (S.tw was replaced since).
function twDrawn() {
  const page = document.querySelector('#app .twpage');
  return page && twDrawnFrom === S.tw ? String(page.getAttribute('data-tw-v') || '') : null;
}
// The page is on screen and is not the one a render would draw.
const twLeftBehind = () => !!document.querySelector('#app .twpage') && twDrawn() !== twCur();
function tweaks() {
  const v = twCur();
  twDrawnFrom = S.tw;
  const cards = twReady(v) && !twBroken(v) ? tweaksResinCard(v) + tweaksAbyssCard(v) + tweaksDomainsCard(v) + tweaksQuestsCard(v) : '';
  return adminPage('rules', blk('events', eventsCard()) + blk('gameplay', `<div class="twpage" data-tw-v="${esc(v)}">
    <div class="flex between center wrap" style="gap:10px 14px">
      <h2 class="twh">${T('tweaks.title')}</h2>
      <div class="twchips" data-tw-live="chips">${twChips(v)}</div>
    </div>
    <p class="sub" style="margin-bottom:16px">${T('tweaks.sub')}</p>
    ${twBar(v)}${twNotes(v)}${cards}
  </div>`));
}
// The Game rules tab's "Events & data fixes", one row per catalogue version: Re-apply the events (only while the
// version runs — the agent refuses a stopped stack, and every Start applies them anyway) and Apply / Revert the txt
// fixes, each behind its question (eventsServer, txtFixes). A version whose payload ships no event list or no txt
// fixes has nothing for that job to do: its button is dead whatever the stack's state, and its tooltip says why.
// Under the docker fog nothing tells whether a version runs: both are dead (the strip names the error).
function eventsCard() {
  const busy = srvBusy(), fog = srvDockerError();
  const rows = S.versions.map((v) => {
    const f = srvFacts(v.id);
    const noEvents = !!f && f.noEvents, noTxtFixes = !!f && f.noTxtFixes;
    const base = busy || fog || !f || !f.present || !f.bootstrapped, applied = !!f && f.txtFixes === 'applied';
    const words = rowState(v.id, f) || [noEvents ? T('server.rules.noEvents') : '', noTxtFixes ? T('server.rules.noTxtFixes')
      : `${T('server.adv.txtFixes')} ${esc(TXTFIX[f.txtFixes] ? t(TXTFIX[f.txtFixes]) : f.txtFixes)}`].filter(Boolean).join(' · ');
    const evDis = noEvents ? `disabled title="${T('server.adv.eventsNone')}"` : base ? 'disabled' : !f.up ? `disabled title="${T('server.rules.eventsNotRunning')}"` : '';
    const txDis = noTxtFixes ? `disabled title="${T('server.adv.txtNone')}"` : base ? 'disabled' : '';
    return `<div class="line"><div><b>${T('common.versionN', { id: v.id })}</b><small>${words}</small></div>
      <div class="flex gap wrap" style="justify-content:flex-end">
        <button class="btn ghost" style="padding:8px 12px;font-size:12px" data-act="eventsServer" data-arg="${esc(v.id)}" ${evDis}>${T('server.adv.events')}</button>
        <button class="btn ghost" style="padding:8px 12px;font-size:12px" data-act="txtFixes" data-arg="${esc(v.id)}:${applied ? 'revert' : 'apply'}" ${txDis}>${applied ? T('server.adv.txtRevert') : T('server.adv.txtApply')}</button>
      </div></div>`;
  }).join('');
  return `<div class="sect"><h4>${T('server.rules.title')}</h4><p class="hint">${T('server.rules.hint')}</p>${S.serverConfigured ? rows : noServerLine()}</div>`;
}
// The version chips (a dot = edits of that version are not applied) and Refresh.
function twChips(v) {
  const ids = twVersions();
  if (v && !ids.includes(v)) ids.push(v);
  const chips = ids.map((id) => `<button class="lg-chip ${v === id ? 'on' : ''}" data-act="tweaksVersion" data-arg="${esc(id)}">${T('common.versionN', { id })}${
    twChanged(id) ? `<i class="twdot" title="${T('tweaks.chip.unapplied')}"></i>` : ''}</button>`).join('');
  return chips + (v ? `<button class="btn ghost" style="padding:7px 12px;font-size:11.5px" data-act="tweaksLoad" data-arg="${esc(v)}" ${S.tw.loading[v] ? 'disabled' : ''}>${T('tweaks.refresh')}</button>` : '');
}
// Whether the answer lets the page call the server's game data the original: no file of it is named as
// one Relic does not rewrite — a card switched off, a monster group's script left alone: both were
// changed outside Relic, or are missing —, none was taken over from changes made outside Relic, and the
// files hold what is stored (nothing waits, no change is without a verdict) and what the running server
// has read (`unread`).
function twPlain(v) {
  const d = twDataOf(v) || {}, s = d.sections || {};
  return d.pending !== true && d.unconfirmed !== true && d.unread !== true
    && !TW_SECTIONS.some((name) => s[name] && (s[name].ok === false || (Array.isArray(s[name].files) && s[name].files.length > 0)
      || twFiles(v, name).taken.length > 0 || Object.keys(twFiles(v, name).reasons).length > 0));
}
// Whether a data file of the version was taken over from changes made outside Relic: the values the page
// starts from are then what the files hold, not all of them the original game's.
const twAnyTaken = (v) => TW_SECTIONS.some((section) => twFiles(v, section).taken.length > 0);
// Whether a data file of the version is not the original game's as it stands, or holds settings — what a
// Reset of every card puts back (an agent that answers the files a Reset acts on).
const twFilesDiffer = (v) => twKnowsFiles(twDataOf(v)) && TW_SECTIONS.some((section) => twFiles(v, section).reset.length > 0);
// The settings in effect that the stored document does not hold although the files keep them, [paths]: settings
// that rest on a data file the server cannot use (`blocked`) stay as the files hold them until those files are
// put back — taken out of the document, they are not written again then.
function twHeld(v) {
  const d = twDataOf(v) || {}, stored = twLeaves(twStoredCanon(v).canon), active = d.active && typeof d.active === 'object' ? d.active : {};
  if (!twKnowsFiles(d)) return [];
  return TW_SECTIONS.reduce((list, section) => list.concat(twFiles(v, section).blocked
    .filter((path) => twAt(active, path) !== undefined && !Object.keys(stored).some((p) => p === path || p.startsWith(path + '.')))), []);
}
// What "Restore the original game" does on this server: reset = every card's files put back at once, by an agent
// that answers what a Reset of each card acts on (`resetFiles`) and can act on all of it — else the empty
// document, which takes every setting out and leaves a file changed outside Relic with those changes; hand = the
// files changed outside Relic (taken over or not): a reset puts the original over them and keeps a copy of each.
function twRestorePlan(v) {
  const d = twDataOf(v) || {}, s = d.sections || {}, hand = [];
  TW_SECTIONS.forEach((section) => {
    const f = twFiles(v, section);
    f.taken.forEach((e) => { if (!hand.includes(e.file)) hand.push(e.file); });
    Object.keys(f.reasons).forEach((file) => { if ((f.reasons[file] || {}).why === 'changed' && !hand.includes(file)) hand.push(file); });
  });
  const reset = twKnowsFiles(d) && TW_SECTIONS.every((section) => !!s[section] && Array.isArray(s[section].resetFiles) && !twFiles(v, section).resetMissing.length);
  return { reset, hand: hand.sort() };
}
// The action bar: what is pending and the three buttons. Always drawn — it is the page's only way to
// send anything — and it stays in sight while the page scrolls under it (relic.css .twbar).
function twBar(v) {
  // Nothing can be sent while a job runs, nor while the server cannot read its own record of the
  // settings (the agent refuses every document then; the note says so, and the bar says nothing about a
  // document nobody could read). With nothing stored the bar says "the original game" only of a server
  // whose files are known to be it (twPlain); else that nothing is stored, which is all the page knows
  // (Apply may still be live then: it puts the original files back). The settings stored differ from what
  // the files' bases hold: the original game — or, where a file was taken over from changes made outside
  // Relic, what that file holds. With nothing stored, settings the files keep in effect because a data file they
  // need cannot be used (twHeld) are counted: they stay until those files are put back. "Restore the original
  // game" is offered while something is stored, the draft holds edits, or a data file is not the original
  // game's as it stands (twFilesDiffer).
  const ready = twReady(v), broken = twBroken(v), busy = srvBusy() || broken;
  const changed = ready && twChanged(v), n = changed ? twChangeCount(v) : 0;
  const stored = ready ? Object.keys(twLeaves(twStoredCanon(v).canon)).length : 0;
  const mine = jobHere() && S.srvJob.kind === 'tweaks' && String(S.srvJob.version) === String(v);
  const files = ready && twAnyTaken(v), held = ready && !stored ? twHeld(v).length : 0;
  const text = mine ? T('tweaks.bar.applying')
    : !ready ? ''
      : changed ? (n === 1 ? T('tweaks.bar.change1') : T('tweaks.bar.changes', { n }))
        : broken ? ''
          : !stored ? (held ? (held === 1 ? T('tweaks.bar.noneHeld1') : T('tweaks.bar.noneHeld', { n: held }))
            : twPlain(v) ? T('tweaks.bar.original') : T('tweaks.bar.none'))
            : twRetry(v) ? T('tweaks.bar.retry')
              : files ? (stored === 1 ? T('tweaks.bar.storedFiles1') : T('tweaks.bar.storedFiles', { n: stored }))
                : stored === 1 ? T('tweaks.bar.stored1') : T('tweaks.bar.stored', { n: stored });
  return `<div class="twbar">
    <div class="twbar-info">${v ? `<span class="vchip">${T('common.versionN', { id: v })}</span>` : ''}<span class="twbar-text${changed && !mine ? ' chg' : ''}">${text}</span></div>
    <div class="twbar-acts">
      <button class="btn ghost" data-act="tweaksDiscard" ${changed && !mine ? '' : 'disabled'}>${T('tweaks.bar.discard')}</button>
      ${ready && (changed || stored || twFilesDiffer(v)) ? `<button class="btn ghost danger" data-act="tweaksRestore" ${busy ? 'disabled' : ''}>${T('tweaks.bar.restore')}</button>` : ''}
      <button class="btn" data-act="tweaksApply" ${ready && !busy && (changed || twRetry(v)) ? '' : 'disabled'}>${T('tweaks.bar.apply')}</button>
    </div></div>`;
}
// The state of the page, one note each, under the action bar — only what is true. Always the one box
// (empty when there is nothing to say): it is redrawn in place while a field has the caret (twSync).
function twNotes(v) {
  const w = S.tw, d = twDataOf(v), out = [];
  const note = (html) => out.push(`<div class="note">${html}</div>`);
  const noteBtn = (html, act, arg, label) => out.push(`<div class="note twnote"><span>${html}</span><button class="btn ghost" data-act="${act}" data-arg="${esc(arg)}">${label}</button></div>`);
  const wrap = () => `<div class="twnotes" data-tw-notes>${out.join('')}</div>`;
  if (!S.serverConfigured) return wrap();
  if (!v) {
    if (S.srv.state === 'ok') note(T('tweaks.note.noVersion'));
    else if (S.srv.state !== 'error') out.push(`<p class="hint" style="margin:0">${T('common.checking')}</p>`);
    return wrap();
  }
  if (w.error[v]) noteBtn(T('tweaks.note.loadFailed', { version: v, error: w.error[v] }), 'tweaksLoad', v, T('common.retry'));
  if (!d) {
    if (!w.error[v]) out.push(`<p class="hint" style="margin:0">${T('tweaks.note.reading', { version: v })}</p>`);
    return wrap();
  }
  if (d.tooOld) { note(T('tweaks.note.tooOld')); return wrap(); }
  if (d.available === false) { note(T('tweaks.note.unavailable', { version: v })); return wrap(); }
  if (d.present === false) { note(T('tweaks.note.absent', { version: v })); return wrap(); }
  // A record the server cannot read: the one note — the others are about a document, and none is known.
  if (twBroken(v)) note(T('tweaks.note.recordError', { error: d.recordError }));
  else {
    // `pending` = the server's files do not hold the document the next Start brings into effect: the
    // stored one — or, while the server's refusal of it stands, the one it accepted before (`active`).
    // Neither that nor `unconfirmed` is a state while a job of the version is at work (twFlux).
    const settled = !twFlux(v), pending = settled && d.pending === true;
    // The agent was writing the data files while it read them (`changing`): it took nothing over from them —
    // what looks changed outside Relic may be its own write. The answer is read again at the next status read.
    if (d.changing === true) note(T('tweaks.note.changing', { version: v }));
    if (d.refused) {
      const at = Number(d.refused.at) > 0 ? new Date(Number(d.refused.at) * 1000).toLocaleString() : '';
      // The settings were not applied — refused by the server, or left without its verdict (the wait for it
      // stopped on request, a signal that never arrived, a restart while it read them, no verdict in time):
      // the note says only that, and the agent's reason says which. A refusal's reason is the server's own
      // words and may be a long excerpt of its log: the note takes its start.
      const said = String(d.refused.error || '').replace(/\s+/g, ' ').trim().replace(/[.\s]+$/, '');
      const why = said.length > 400 ? said.slice(0, 400) + '…' : said;
      // What became of the settings from before the refusal: in use (the files hold them), or not in the
      // files — and then whether a Start can put them back at all.
      const then = !settled ? '' : !pending ? T('tweaks.note.refusedKept')
        : twCanStart(v, d.active) ? T('tweaks.note.refusedPending', { version: v }) : T('tweaks.note.refusedStuck');
      noteBtn((at ? T('tweaks.note.refusedAt', { time: at, error: why }) : T('tweaks.note.refused', { error: why })) + (then ? ' ' + then : ''),
        'tweaksShowActive', v, settled && !pending ? T('tweaks.note.showActive') : T('tweaks.note.showPrevious'));
    }
    if (settled && d.unconfirmed === true) note(T('tweaks.note.unconfirmed'));
    // The data files changed while a service of the running server that reads them only when asked was not
    // asked since (the agent says it only while the game server runs): an Apply asks it (twRetry), a Start
    // has it read them anyway.
    if (settled && d.unread === true && d.running !== false) note(T('tweaks.note.unread', { version: v }));
    // The stored document waits for the next Start — for an agent that refuses a whole document, unless it
    // needs a file the server does not let Relic rewrite: then no Start applies it, and the settings that
    // need the file have to go first. With nothing stored it is the original game that the files do not hold.
    if (pending && !d.refused) {
      note(!Object.keys(twStoredCanon(v).canon).length ? T('tweaks.note.pendingNone', { version: v })
        : twCanStart(v, d.tweaks) ? T('tweaks.note.pending', { version: v }) : T('tweaks.note.pendingStuck'));
    }
    // Settings that rest on a data file the server cannot use (`blocked`, per card — stored or in effect): they
    // cannot be changed, and the files keep what they hold for them until a Revert or a Reset of their card puts
    // the files back (the card names the files and why) — never a Start, and taking them out of the saved settings
    // does not take them out of the files.
    if (settled && twKnowsFiles(d)) {
      const cards = TW_SECTIONS.filter((section) => twFiles(v, section).blocked.length > 0);
      const n = cards.reduce((sum, section) => sum + twFiles(v, section).blocked.length, 0);
      const names = cards.map(twCardName).join(', ');
      if (n) note(n === 1 ? T('tweaks.note.blocked1', { cards: names }) : T('tweaks.note.blocked', { n, cards: names }));
    }
    // The game server refuses its own data files (`ownRefused`, read from its log): nothing new can be applied
    // while it runs on them — it is waited for to read them again first — and a Start does not get past them.
    // The cards that put their files back are named; the stopped server's note says it in its place.
    const own = twOwnRefused(v);
    if (own) {
      const vars = { version: v, error: own.error, files: own.files.map(twFileName).join(', ') || '—', cards: own.cards.map(twCardName).join(', ') };
      note(d.running === false ? (own.cards.length ? T('tweaks.note.ownRefusedStopped', vars) : T('tweaks.note.ownRefusedStoppedAny', vars))
        : own.cards.length ? T('tweaks.note.ownRefused', vars) : T('tweaks.note.ownRefusedAny', vars));
    } else if (d.running === false) note(T('tweaks.note.stopped', { version: v }));
    if (S.twData.status === 'missing' || S.twData.status === 'error')
      note(S.twData.error ? T('tweaks.note.noNamesWhy', { error: S.twData.error }) : T('tweaks.note.noNames'));
  }
  return wrap();
}
// A card's frame: the title, a mark that says whether the section holds settings and whether they are
// applied, the two buttons that act on the card's data files at once (twFileActs), what the card says of
// those files (twFilesNote), the body. section = the document's section the card edits.
function twCard(section, title, v, body) {
  return `<div class="sect twcard"><div class="twhead"><h4>${title}</h4><span data-tw-live="mark.${section}">${twMark(section, v)}</span>
    <span class="twhead-acts" data-tw-live="files:${section}">${twFileActs(section, v)}</span></div>${twFilesNote(section, v)}${body}</div>`;
}
// The two buttons of a card that act on its data files AT ONCE — never on the draft, unlike the card's own
// resets: "Put back Relic's settings now…" (a Revert, while the answer names files for it: a take-over with
// settings from before it to put back, a file changed outside Relic that was not taken over, a missing one)
// and "Put back the original files now…" (a Reset: every file of the card that is not the original game's as
// it stands, or that a setting of the card writes). Each asks first (tweaksRevert, tweaksReset). Dead while a
// job runs, and while the agent could not act on one of the files — the tooltip says which. Nothing for an
// agent that cannot do either (twKnowsFiles), nor while the record cannot be read.
function twFileActs(section, v) {
  if (!twReady(v) || twBroken(v) || !twKnowsFiles(twDataOf(v))) return '';
  const f = twFiles(v, section), busy = srvBusy();
  const btn = (act, label, tip, files, missing, gone) => (!files.length ? ''
    : `<button class="btn ghost${act === 'tweaksReset' ? ' danger' : ''}" data-act="${act}" data-arg="${section}" title="${esc(missing.length ? gone : tip)}" ${busy || missing.length ? 'disabled' : ''}>${label}</button>`);
  return btn('tweaksRevert', T('tweaks.files.revert'), t('tweaks.files.revertTip'), f.revert, f.revertMissing,
    t('tweaks.files.revertGone', { files: twNames(f.revertMissing.map(twFileName)) }))
    + btn('tweaksReset', T('tweaks.files.reset'), t('tweaks.files.resetTip'), f.reset, f.resetMissing,
      t('tweaks.files.resetGone', { files: twNames(f.resetMissing.map(twFileName)) }));
}
// The local date and time of an agent's time in seconds, as the page's language writes it ('' for none).
const twWhen = (at) => (Number(at) > 0 ? new Date(Number(at) * 1000).toLocaleString() : '');
// The files of a card and why the agent cannot use each, in one sentence: "TowerLevelData.txt — line 3 has 17
// cells where the header has 18 …; …" — the first three, then how many more.
const twReasonList = (reasons) => twNames(Object.keys(reasons).sort().map((file) => t('tweaks.files.reason',
  { file: twFileName(file), reason: String((reasons[file] || {}).reason || '').replace(/\s+/g, ' ').trim().replace(/[.\s]+$/, '') })), 3, '; ');
// What a card says of its data files, under its title (for an agent that answers it): the files changed
// outside Relic that the agent took over — when, which settings of this page gave way to them, the rows it
// keeps as the hand left them — and, for a card that is on, the files it cannot use and why (a card switched
// off says that in place of its controls: twSectionOff). '' when there is nothing to say.
function twFilesNote(section, v) {
  const d = twDataOf(v);
  if (!twReady(v) || twBroken(v) || !twKnowsFiles(d)) return '';
  const f = twFiles(v, section), out = [];
  const said = [];
  if (f.taken.length) {
    said.push(T('tweaks.taken.note', { files: twNames(f.taken.map((e) => (twWhen(e.at) ? t('tweaks.taken.file', { file: twFileName(e.file), time: twWhen(e.at) }) : twFileName(e.file)))) }));
    const gave = Object.keys(f.dropped).sort().map((path) => twDroppedText(v, path, f.dropped[path])).filter(Boolean);
    if (gave.length) said.push(T('tweaks.taken.gaveWay', { list: twNames(gave, 6, '; ') }));
  }
  Object.keys(f.kept).sort().forEach((file) => {
    const n = Number(f.kept[file]) || 0;
    if (n > 0) said.push(n === 1 ? T('tweaks.taken.kept1', { file: twFileName(file) }) : T('tweaks.taken.kept', { n, file: twFileName(file) }));
  });
  if (said.length) out.push(`<div class="note twfiles">${said.join(' ')}</div>`);
  const s = (d.sections || {})[section] || {};
  if (s.ok !== false && Object.keys(f.reasons).length) out.push(`<div class="note twfiles">${T('tweaks.files.cannot', { list: twReasonList(f.reasons) })}</div>`);
  return out.join('');
}
// One setting a take-over took out of the documents (`dropped`: its values before it, {active, tweaks}), in the
// page's words: "Resin cap 300 → the file's 250", a setting that has no counterpart in the file "… (taken out)",
// one the take-over set ("… the file's 30, a setting of its own": a domain's cost kept against the cost for all
// domains).
function twDroppedText(v, path, values) {
  const d = twDataOf(v) || {}, keys = String(path).split('.'), val = values && typeof values === 'object' ? values : {};
  const was = val.tweaks != null ? val.tweaks : val.active;
  // (fmt = how a value reads; null = a list — a reward, extra items — said by its setting alone)
  const say = (setting, now, fmt) => {
    const f = fmt || ((x) => String(x));
    if (was == null) return now == null ? '' : t('tweaks.taken.own', { setting, now: f(now) });
    if (fmt === null) return t('tweaks.taken.outList', { setting });
    return now == null ? t('tweaks.taken.out', { setting, was: f(was) }) : t('tweaks.taken.wasNow', { setting, was: f(was), now: f(now) });
  };
  const times = (x) => '×' + x, mon = (id) => twName('monsters', id);
  const chambers = (d.abyss && d.abyss.chambers) || {}, floors = (d.abyss && d.abyss.floors) || {};
  const place = (lid) => { const ch = chambers[lid] || {}; return { floor: (floors[ch.floor] || {}).number || '?', chamber: ch.index || '?' }; };
  const dom = (d.domains || {})[keys[2]] || {};
  if (keys[0] === 'resin' && keys[1] === 'cap') return say(t('tweaks.resin.cap'), (d.resin || {}).cap);
  if (keys[0] === 'resin' && keys[1] === 'minutes') return say(t('tweaks.resin.minutes'), (d.resin || {}).minutes);
  if (keys[0] === 'abyss' && keys[1] === 'levels') {
    const ch = chambers[keys[2]];
    return say(t('tweaks.field.abyssLevel', place(keys[2])), ch ? ch.level : null, (x) => String(Number(x) + TW_ABYSS_SLOT_LEVEL));
  }
  if (keys[0] === 'abyss' && keys[1] === 'slots') return say(t('tweaks.field.abyssEnemy', place(twAbyssFacts(d).lid[keys[2]])), (twSlotGroups(d, 'abyss')[keys[2]] || {})[keys[3]], mon);
  if (keys[0] === 'domains' && keys[1] === 'slots') {
    const id = twLineupDomain(v, keys[2]);
    return say(t('tweaks.field.domainEnemy', { domain: id ? twDomainName(v, id) : '#' + keys[2] }), (twSlotGroups(d, 'domains')[keys[2]] || {})[keys[3]], mon);
  }
  if (keys[0] === 'domains' && keys[1] === 'cost') return say(t('tweaks.field.domainsCost'), twDomainsCost(v));
  if (keys[0] === 'domains' && keys[1] === 'multiplier') return say(t('tweaks.field.domainsMultiplier'), null, times);
  if (keys[0] === 'domains' && keys[1] === 'byId' && keys[3]) {
    const domain = twDomainName(v, keys[2]);
    if (keys[3] === 'cost') return say(t('tweaks.field.domainCost', { domain }), dom.cost);
    if (keys[3] === 'level') return say(t('tweaks.field.domainLevel', { domain }), dom.level);
    if (keys[3] === 'multiplier') return say(t('tweaks.field.domainMultiplier', { domain }), null, times);
    if (keys[3] === 'extra') return say(t('tweaks.field.domainExtra', { domain }), null, null);
  }
  if (keys[0] === 'quests' && keys[1] === 'multiplier') return say(t('tweaks.field.questMultiplier', { kind: twQuestKindName(keys[2]) }), null, times);
  if (keys[0] === 'quests' && keys[1] === 'byReward') {
    const row = twQuestRows(v).find((r) => r.rid === String(keys[2]));
    return say(t('tweaks.field.questReward', { quest: row ? row.name : '#' + keys[2] }), null, null);
  }
  return say(String(path), null, (x) => (x && typeof x === 'object' ? JSON.stringify(x) : String(x)));
}
// The game server refusing its own data files (`ownRefused`, what its log says of its last load: {error,
// files}): {error: the start of the reason, files, cards: the cards those files belong to} — null for none.
function twOwnRefused(v) {
  const d = twDataOf(v), o = d && d.ownRefused;
  if (!o || typeof o !== 'object') return null;
  const files = (Array.isArray(o.files) ? o.files : []).filter((f) => typeof f === 'string' && f);
  const said = String(o.error || '').replace(/\s+/g, ' ').trim().replace(/[.\s]+$/, '');
  return { error: said.length > 400 ? said.slice(0, 400) + '…' : said, files, cards: TW_SECTIONS.filter((section) => files.some((f) => twCardsOf(v, f).includes(section))) };
}
// The cards a data file belongs to, [sections]: the ones whose part of the answer names it, else by what the
// file is — a monster group's script, one of the tables (the drop table is both the Domain card's and the
// Quest card's).
function twCardsOf(v, file) {
  const d = twDataOf(v) || {};
  const named = TW_SECTIONS.filter((section) => {
    const f = twFiles(v, section), s = (d.sections || {})[section] || {};
    return [].concat(Array.isArray(s.files) ? s.files : [], f.taken.map((e) => e.file), Object.keys(f.reasons), f.reset, f.revert, f.levelFiles).includes(file);
  });
  if (named.length) return named;
  const m = TW_GROUP_FILE.exec(String(file));
  if (m) return [hasOwn(twSlotGroups(d, 'abyss'), m[1]) ? 'abyss' : 'domains'];
  if (TW_TABLES.drops.test(file)) return ['domains', 'quests'];
  if (TW_TABLES.costs.test(file)) return ['domains'];
  if (TW_TABLES.rewards.test(file)) return ['quests'];
  if (TW_TABLES.levels.test(file)) return ['abyss'];
  return TW_TABLES.resin.test(file) ? ['resin'] : [];
}
function twMark(section, v) {
  if (twSectionChanged(section, v)) return `<span class="twmark new">${T('tweaks.mark.unapplied')}</span>`;
  return twSectionSet(section, v) ? `<span class="twmark">${T('tweaks.mark.changed')}</span>` : '';
}
// A card's way back for the whole of its section (the button asks first: tweaksAbyssReset,
// tweaksRewardsReset) — '' while the section holds nothing, and for the Resin card, whose two numbers
// have their "Original" chips.
function twResetBtn(section, v) {
  if (!twSectionSet(section, v)) return '';
  if (section === 'abyss') return `<button class="btn ghost danger" data-act="tweaksAbyssReset">${T('tweaks.abyss.resetAll')}</button>`;
  if (section === 'domains') return `<button class="btn ghost danger" data-act="tweaksRewardsReset" data-arg="domains">${T('tweaks.domains.resetAll')}</button>`;
  return section === 'quests' ? `<button class="btn ghost danger" data-act="tweaksRewardsReset" data-arg="quests">${T('tweaks.quests.resetAll')}</button>` : '';
}
// A card the server switched off (sections.<name>.ok === false): the note that names the files — with the
// agent's reason for each, when it gives them —, in place of the card's controls — '' when the card is on.
// A file that was changed outside Relic and not taken over, or that lacks what the card edits, is never
// rewritten; a setting that needs it cannot be changed — the files keep what they hold for it until they are
// put back (an agent that refuses a whole document refuses one that needs it) —, so the settings of the draft
// that need it (twOffNeeds, and the enemies of a line-up whose script is among the files: twOffPaths) can be
// taken out of the saved settings from here. The card's other settings stay in the
// document and in use: the note says so, and while the draft holds nothing that needs such a file its button
// is the card's own reset — with the controls gone, the one way to take those out. The card's head holds
// the two buttons that put its files back at once (twFileActs).
function twSectionOff(section, v) {
  const d = twDataOf(v), s = d && d.sections && d.sections[section];
  if (!s || s.ok !== false) return '';
  const files = (Array.isArray(s.files) ? s.files : []).filter((f) => typeof f === 'string' && f).join(', ') || '?';
  const reasons = twKnowsFiles(d) ? twFiles(v, section).reasons : {};
  // What the card's settings come to once the button has taken out the ones that need such a file.
  const needs = twOffNeeds(v, twDraftCanon(v).canon), rest = { [section]: twCopy(twDraft(v)[section]) };
  const stuck = twOffPaths(v, section, rest, needs);
  stuck.forEach((path) => twCut(rest, path));
  // (an agent that applies the settings one at a time keeps what the files hold for the ones that need such a
  //  file — whatever the draft says of them — until the card's files are put back: the button takes them out of
  //  the saved settings alone, and the files keep a setting in effect until then)
  const one = twKnowsFiles(d), active = d.active && typeof d.active === 'object' ? d.active : {};
  const text = (Object.keys(reasons).length ? T('tweaks.off.files', { list: twReasonList(reasons) })
    : s.why === 'missing' ? T('tweaks.off.missing', { files }) : T('tweaks.off.changed', { files }))
    + (one ? ' ' + T('tweaks.off.held') : '') + (twCanon(v, rest)[section] ? ' ' + T('tweaks.off.kept') : '')
    + (one && stuck.some((path) => twAt(active, path) !== undefined) ? ' ' + T('tweaks.off.clearHeld') : '');
  const btn = twOffStops(needs, section) || stuck.length ? `<button class="btn ghost" data-act="tweaksClear" data-arg="${section}">${T('tweaks.off.clear')}</button>` : twResetBtn(section, v);
  return btn ? `<div class="note twnote"><span>${text}</span>${btn}</div>` : `<div class="note">${text}</div>`;
}
// What a number field shows: the draft's entry as it was typed, else the original value — a field that
// shows the original is "not changed" (the document has no such key).
const twShown = (path, original, v) => { const x = twGet(path, v); return x === undefined ? original : x; };
// The classes of a number field: a setting (differs from the original), a number Apply would refuse.
function twFieldCls(path, v) {
  return (twIsSet(path, v) ? ' chg' : '') + (twProblems(v).some((p) => p.path === path) ? ' bad' : '');
}
// Preset chips of a number: the original first, then the presets. A click sets the entry (tweaksPreset).
// label(value, isOriginal) = a chip's text (already HTML); left out, the original reads "Original · 160"
// and a preset is its number. clear = the "Original" chip takes the entry out of the draft instead of
// writing the original number into it: every chip of a value a file's base holds does — the entry then
// means "what the base holds", which a file changed outside Relic and taken over, or put back, may change
// before the draft is sent (a number written in would turn into a setting nobody made), and a row's own
// Reset stays live only while the draft holds an entry for the row. tip = the original chip's tooltip (the
// original game's value beside what a file taken over holds).
function twPresetChips(path, original, presets, v, label, clear, tip) {
  const cur = twInt(twGet(path, v)), shown = cur === undefined ? original : cur;
  const text = label || ((value, isOriginal) => (isOriginal ? T('tweaks.chip.original', { value }) : esc(value)));
  const chip = (value, isOriginal) => `<button class="elchip${shown === value ? ' on' : ''}" data-act="tweaksPreset" data-arg="${esc(path + '=' + (isOriginal && clear ? '' : value))}"${
    isOriginal && tip ? ` title="${esc(tip)}"` : ''}>${text(value, isOriginal)}</button>`;
  return chip(original, true) + presets.filter((p) => p !== original).map((p) => chip(p, false)).join('');
}
// Whether the table a value lives in was taken over from changes made outside Relic — 'resin' (the Resin card's
// one table), 'levels' (the Abyss card's chamber table), 'costs' / 'drops' / 'rewards' (told apart by name, as
// twOff tells them): the value is what the file holds, the original game's beside it where they differ.
function twTakenTable(v, table) {
  const taken = (section) => twFiles(v, section).taken.map((e) => e.file).filter((f) => !TW_GROUP_FILE.test(f));
  if (table === 'resin') return taken('resin').length > 0;
  if (table === 'levels') return taken('abyss').length > 0;
  return taken(table === 'rewards' ? 'quests' : 'domains').some((f) => TW_TABLES[table].test(f));
}
// The monster groups whose script was taken over, {group id: true}: their slots hold what the file holds.
function twTakenGroups(v) {
  const out = {};
  TW_SLOT_SECTIONS.forEach((section) => twFiles(v, section).taken.forEach((e) => { const m = TW_GROUP_FILE.exec(e.file); if (m) out[m[1]] = true; }));
  return out;
}
// The label of a value's first chip: "Original · 160", or "In the file · 250" for a value of a file taken over
// (inFile) — the chip that takes the setting out, back to what the file holds. shift = what the chip adds to
// the number it shows (the Abyss level: twAbyssLevelChips).
const twBaseLabel = (inFile, shift) => (value, isOriginal) => {
  const n = isOriginal && shift ? value + shift : value;
  return !isOriginal ? esc(n) : inFile ? T('tweaks.chip.inFile', { value: n }) : T('tweaks.chip.original', { value: n });
};
// The tooltip of that chip: the original game's value, where it is not the one the file holds ('' otherwise).
const twGameTip = (game) => (game === undefined || game === null ? '' : t('tweaks.chip.game', { value: game }));
// The parts of the page a keystroke changes, redrawn in place: [data-tw-live="<name>"] takes
// TW_LIVE[<name>](version) as its HTML ("mark.<section>" = that card's mark). A live block never holds
// the field being typed in.
const TW_LIVE = {
  chips: (v) => twChips(v),
  // A card's two buttons that put its data files back at once: dead while a job runs ("files:<section>").
  files: (v, section) => twFileActs(section, v),
  'resin.cap.chips': (v) => twResinChips(v, 'cap', TW_RESIN_CAPS),
  'resin.minutes.chips': (v) => twResinChips(v, 'minutes', TW_RESIN_MINUTES),
  'resin.full': (v) => twResinFull(v),
  // The Spiral Abyss card: what the enemy level of the chamber on screen shows up in.
  'abyss.rotations': (v) => twAbyssRotations(v, twAbyssView(v)),
  'abyss.floors': (v) => twAbyssFloorChips(v, twAbyssView(v)),
  'abyss.chambers': (v) => twAbyssChamberChips(v, twAbyssView(v)),
  'abyss.level.chips': (v) => twAbyssLevelChips(v, twAbyssView(v).lid),
  'abyss.reset': (v) => twAbyssResetButton(v, twAbyssView(v).lid),
  'abyss.foot': (v) => twAbyssFoot(v),
  // The Domain card: the cost of every domain shows up in its chips and in what "like the others" is
  // for the open domain; a domain's own cost, enemy level and amounts in its row's marks, its Reset, the
  // chips' count — the level in its "Original" chip as well.
  'domains.cost.chips': (v) => twDomainCostChips(v),
  'domains.kinds': (v) => twDomainKinds(v),
  'domains.tags': (v, id) => twDomainTags(v, id),
  'domains.reset': (v, id) => twDomainResetButton(v, id),
  'domains.like': (v, id) => twDomainLike(v, id),
  'domains.level.chips': (v, id) => twDomainLevelChips(v, id),
  'domains.foot': (v) => twDomainsFoot(v),
  // The Quest card: an amount typed into a reward makes it the quest's own, or the original one again.
  'quests.kinds': (v) => twQuestKinds(v),
  'quests.tags': (v, rid) => twQuestTags(v, rid),
  'quests.restore': (v, rid) => twQuestRestoreButton(v, rid),
  'quests.state': (v, rid) => twQuestState(v, rid),
  'quests.foot': (v) => twQuestsFoot(v),
};
// A field of the page (data-tw = its path in the draft of the version the page shows: twDrawn) was typed
// in: the draft takes the text as it is and everything that depends on it follows in place. Never a
// render() — it would take the caret. A field that shows its entry moved by a fixed amount
// (data-tw-shift: the Abyss level) hands over the number moved back; what is no whole number stays as it
// was typed. An amount of a quest's reward is typed into a list the draft may not hold yet (twQuestSeed),
// and may take the list back to what the quest hands out anyway (twQuestTidy). A page that was drawn
// from another server's documents has no draft to write into: the keystroke draws it anew.
function twTyped(el) {
  const v = twDrawn();
  if (!v) { render(); return; }
  const path = el.getAttribute('data-tw'), keys = twPath(path), shift = num(el.getAttribute('data-tw-shift')), n = twInt(el.value);
  const reward = keys[0] === 'quests' && keys[1] === 'byReward' ? keys[2] : '';
  if (reward) twQuestSeed(v, reward);
  twSet(path, shift && typeof n === 'number' && !isNaN(n) ? n - shift : el.value, v);
  if (reward) twQuestTidy(v, reward);
  twSync();
}
// The page on screen brought up to date in place, for the version it shows: the bar and the notes, and
// — while the answer describes a version the page can edit — every [data-tw-live="<name>"] or
// "<name>:<argument>" block (the row or the editor of one entry of a list) and the marks of the number
// fields. A keystroke calls it, and whatever lands while a field has the caret: an answer, a status
// (twRedraw). A block the answer the page holds by then cannot draw — it landed under the cards of
// another one — stays as it is until the next render.
function twSync() {
  const page = document.querySelector('#app .twpage'), v = twDrawn();
  if (!page || v === null) return;
  const bar = page.querySelector('.twbar'), notes = page.querySelector('[data-tw-notes]'), ready = twReady(v);
  if (bar) bar.outerHTML = twBar(v);
  if (notes) notes.outerHTML = twNotes(v);
  page.querySelectorAll('[data-tw-live]').forEach((el) => {
    const name = el.getAttribute('data-tw-live'), at = name.indexOf(':'), key = at < 0 ? name : name.slice(0, at);
    if (!ready && key !== 'chips') return;
    let html = null;
    try { html = name.startsWith('mark.') ? twMark(name.slice(5), v) : TW_LIVE[key] ? TW_LIVE[key](v, at < 0 ? undefined : name.slice(at + 1)) : null; }
    catch (e) { html = null; }
    if (html !== null) el.innerHTML = html;
  });
  if (!ready) return;
  const bad = twProblems(v).map((p) => p.path);
  page.querySelectorAll('[data-tw]').forEach((el) => {
    const path = el.getAttribute('data-tw');
    el.classList.toggle('chg', twIsSet(path, v));
    el.classList.toggle('bad', bad.includes(path));
  });
}
// Stores and applies a document of a version: the page's job — with the takenKey of the answer the document
// was built on (none for an answer that names none).
function twSend(v, doc) {
  S.tw.sent[v] = twDraftCanon(v).json;
  const key = twTakenKey(v);
  runServerJob('tweaks', v, key === null ? { tweaks: doc } : { tweaks: doc, takenKey: key });
}
// Puts the data files of `cards` back at once — op 'revert' | 'reset' — : the same job kind, with the answer's
// takenKey. Nothing of the draft is sent, and the draft is not handed over to the re-read either (no `sent`):
// its edits stay, rebased on what the job leaves (twRebase).
function twSendFiles(v, op, cards) {
  runServerJob('tweaks', v, { [op]: cards.slice(), takenKey: twTakenKey(v) || '' });
}
// The question of a Revert (op 'revert') or a Reset ('reset') of one card's data files: what goes back — Relic's
// settings from before the changes made outside Relic (the settings in effect, while the server's refusal of
// the last ones stands), or the original game with the card's settings taken out —, every file it acts on,
// what the drop table means for the other card that holds settings in it, the copy kept of each file changed
// outside Relic, what the running or the stopped server does, and that the draft's edits stay. Asked again at
// its Yes over the answer as it is then (ask); nothing when the card offers no such button (twFileActs).
function twFilesAsk(section, op) {
  const v = twCur(), d = twDataOf(v), reset = op === 'reset';
  if (!TW_SECTIONS.includes(section) || !twReady(v) || srvBusy() || twBroken(v) || !twKnowsFiles(d)) return;
  const f = twFiles(v, section), files = reset ? f.reset : f.revert;
  if (!files.length || (reset ? f.resetMissing : f.revertMissing).length) return;
  const names = files.map(twFileName).join(', '), card = twCardName(section), hand = twRestorePlan(v).hand;
  // (a Revert builds a file again on the base Relic had built it on before the changes made outside Relic: the
  //  original game's data — or, for a file taken over twice, the earlier change it had taken over, which then comes
  //  back with the settings: `undoOriginal` false)
  const earlier = reset ? [] : f.taken.filter((e) => files.includes(e.file) && e.undo === true && e.undoOriginal === false).map((e) => twFileName(e.file));
  const said = [reset ? t('tweaks.confirm.resetBody', { files: names })
    : earlier.length ? (d.refused ? t('tweaks.confirm.revertBodyRefusedEarlier', { files: names, earlier: earlier.join(', ') })
      : t('tweaks.confirm.revertBodyEarlier', { files: names, earlier: earlier.join(', ') }))
      : d.refused ? t('tweaks.confirm.revertBodyRefused', { files: names }) : t('tweaks.confirm.revertBody', { files: names })];
  // (a file the other card's settings live in too — the drop table: what changed outside Relic in it is undone
  //  for that card as well, when the file holds such a change, and that card's settings are written on it again)
  files.forEach((file) => twCardsOf(v, file).filter((other) => other !== section)
    .forEach((other) => { const vars = { file: twFileName(file), card: twCardName(other) };
      said.push(hand.includes(file) ? t('tweaks.confirm.filesShared', vars) : t('tweaks.confirm.filesSharedPlain', vars)); }));
  if (files.some((file) => hand.includes(file))) said.push(t('tweaks.confirm.filesHand'));
  said.push(d.running === false ? t('tweaks.confirm.filesStopped') : t('tweaks.confirm.filesLive'));
  if (twChanged(v)) said.push(t('tweaks.confirm.filesDraft'));
  S.confirm = { title: reset ? t('tweaks.confirm.resetTitle', { card, id: v }) : t('tweaks.confirm.revertTitle', { card, id: v }),
    body: said.join(' '), yes: reset ? t('tweaks.confirm.resetFilesYes') : t('tweaks.confirm.revertYes'),
    act: reset ? 'tweaksResetGo' : 'tweaksRevertGo', arg: section, ask: reset ? 'tweaksReset' : 'tweaksRevert', at: Date.now(), tw: v };
  render();
}
function twFilesGo(section, op) {
  const v = twCur();
  if (!TW_SECTIONS.includes(section) || !twReady(v) || twBroken(v) || !twKnowsFiles(twDataOf(v))) return render();
  twSendFiles(v, op, [section]);
}

// ── gameplay: resin ──
// The cap resin regenerates up to and the minutes one point takes — one row of the server's own table.
const TW_RESIN_CAPS = [200, 300, 500, 1000];
const TW_RESIN_MINUTES = [4, 2, 1];
// "21 h 20 min" for a number of minutes.
function twDuration(minutes) {
  const d = Math.floor(minutes / 1440), h = Math.floor((minutes % 1440) / 60), m = minutes % 60;
  if (d) return h ? t('tweaks.time.dh', { d, h }) : t('tweaks.time.d', { d });
  if (h) return m ? t('tweaks.time.hm', { h, m }) : t('tweaks.time.h', { h });
  return t('tweaks.time.m', { m });
}
// How long resin takes from empty to the cap with the numbers in the fields, next to the original's
// time when they differ ('' while a field holds no number).
function twResinFull(v) {
  const was = twDataOf(v).resin || {};
  const cap = twInt(twShown('resin.cap', was.cap, v)), minutes = twInt(twShown('resin.minutes', was.minutes, v));
  if (!(cap > 0) || !(minutes > 0) || !(was.cap > 0) || !(was.minutes > 0)) return '';
  const time = twDuration(cap * minutes), original = twDuration(was.cap * was.minutes);
  // (the numbers it compares with are the row's: the original game's — or the file's, taken over)
  return time === original ? T('tweaks.resin.full', { time })
    : twTakenTable(v, 'resin') ? T('tweaks.resin.fullWasFile', { time, was: original }) : T('tweaks.resin.fullWas', { time, was: original });
}
// The chips of one of the resin numbers (key 'cap' | 'minutes'): what the row holds first — "In the file" when
// the table was taken over from changes made outside Relic, the original game's number in its tooltip.
function twResinChips(v, key, presets) {
  const was = twDataOf(v).resin || {}, game = was.game && typeof was.game === 'object' ? was.game[key] : undefined;
  return twPresetChips('resin.' + key, was[key], presets, v, twBaseLabel(twTakenTable(v, 'resin')), true, twGameTip(game));
}
function tweaksResinCard(v) {
  const off = twSectionOff('resin', v), was = twDataOf(v).resin;
  if (off || !was) return twCard('resin', T('tweaks.resin.title'), v, off);
  const row = (key, label, sub, presets) => `<div class="line"><div style="min-width:0"><b>${label}</b><small>${sub}</small></div>
      <div class="twctl"><div class="chips" data-tw-live="resin.${key}.chips">${twResinChips(v, key, presets)}</div>
        <input class="txt twnum${twFieldCls('resin.' + key, v)}" data-tw="resin.${key}" value="${esc(twShown('resin.' + key, was[key], v))}" placeholder="${esc(was[key])}" inputmode="numeric" maxlength="9" autocomplete="off" spellcheck="false"/></div></div>`;
  return twCard('resin', T('tweaks.resin.title'), v, `<p class="hint">${T('tweaks.resin.hint')}</p>
    ${row('cap', T('tweaks.resin.cap'), T('tweaks.resin.capSub', { max: twAmount(was.hardMax) }), TW_RESIN_CAPS)}
    ${row('minutes', T('tweaks.resin.minutes'), T('tweaks.resin.minutesSub'), TW_RESIN_MINUTES)}
    <p class="twfoot" data-tw-live="resin.full">${twResinFull(v)}</p>`);
}

// ── gameplay: spawn slots — what the Abyss and the Domain card share ──
// Both cards put monsters into the spawn slots of a monster group of the server's scripts: a half of an
// Abyss chamber, the line-up of a domain. A group is drawn from {group: its id, slots: {config id: the
// original monster id}, waves: [[[config id, times]]]} — the answer's own words for a half; twLineupOf
// gives a line-up the same shape, with flat: true in place of the waves when the order of its fight is
// not known —, and its changes are '<section>.slots.<group>.<config id>' of the document
// (TW_SLOT_SECTIONS). What is drawn takes the section along in sv, read once per draw: cur / was = the
// slots the canonical draft / the stored document change there, {group: {config id: monster}}; locked =
// twLocked.
// taken = the groups whose script was taken over from changes made outside Relic (twTakenGroups): their slots'
// enemies are what the file holds.
const twSlotView = (v, sec) => ({ v, sec, cur: (twDraftCanon(v).canon[sec] || {}).slots || {}, was: (twStoredCanon(v).canon[sec] || {}).slots || {}, locked: twLocked(v, sec),
  taken: twTakenGroups(v) });
// What a click does there, per section: a tile and "Replace all" open the enemy picker, the button of a
// locked group's note takes the group's changes out of the draft — of a line-up, its slots and the enemy
// level of each domain that spawns from it: the range of that level rests on the same script.
const TW_SLOT_PICK = { abyss: 'tweaksAbyssPick', domains: 'tweaksLineupPick' };
const TW_SLOT_CLEAR = { abyss: 'tweaksAbyssUnlock', domains: 'tweaksLineupUnlock' };
// A spawn slot as a tile: the slot's enemy as the draft has it — with the original one small in its
// corner when it is another. A click opens the enemy picker for the slot ("<at>:<config id>" = the
// section's own words for it); fixed = a slot the page cannot change. times = how often the slot spawns.
// The original of a slot is what its group's script holds — the file's, for a script taken over from
// changes made outside Relic —; the tooltip names the original game's enemy where that is another one
// (`gameSlots` of the group).
function twSlotTile(sv, g, at, cfg, times, fixed) {
  const slotIn = (doc) => (hasOwn(doc[g.group], cfg) ? doc[g.group][cfg] : g.slots[cfg]);
  const now = slotIn(sv.cur), e = twEntry('monsters', now), changed = now !== g.slots[cfg];
  const game = g.gameSlots && typeof g.gameSlots === 'object' && hasOwn(g.gameSlots, cfg) ? g.gameSlots[cfg] : undefined;
  const base = twName('monsters', g.slots[cfg]), inFile = !!sv.taken && hasOwn(sv.taken, String(g.group));
  const tip = twMonsterLine(e) + (changed ? '\n' + (inFile ? t('tweaks.abyss.wasFile', { name: base }) : t('tweaks.abyss.was', { name: base })) : '')
    + (game !== undefined && game !== now ? '\n' + t('tweaks.abyss.game', { name: twName('monsters', game) }) : '');
  const cls = `twmon${changed ? ' chg' : ''}${now !== slotIn(sv.was) ? ' new' : ''}`;
  const body = `${iconTile(e.icon, e.name, '')}${changed ? iconTile(twEntry('monsters', g.slots[cfg]).icon, twName('monsters', g.slots[cfg]), 'was') : ''}${
    times > 1 ? `<span class="x">×${esc(times)}</span>` : ''}<span class="nm">${esc(e.name)}</span>`;
  return fixed ? `<span class="${cls} ro" title="${esc(tip)}">${body}</span>`
    : `<button class="${cls}" data-act="${TW_SLOT_PICK[sv.sec]}" data-arg="${esc(at + ':' + cfg)}" title="${esc(tip)}">${body}</button>`;
}
// The waves of a group in spawn order, each a wrap of tiles under "Wave N" and "Replace all" (the enemy
// picker for every slot of the wave: "<at>:<wave index>"). One slot may spawn in two waves: it is ONE
// entry of the document, so both tiles follow. A flat group — the order its slots spawn in is not known
// — is one block of all of them, by config id.
function twSlotWaves(sv, g, at) {
  const slots = g.slots || {}, fixed = hasOwn(sv.locked, g.group), flat = !!g.flat;
  return (flat ? [Object.keys(slots).map((cfg) => [cfg, 1])] : Array.isArray(g.waves) ? g.waves : []).map((wave, wi) => {
    const list = (Array.isArray(wave) ? wave : []).filter((x) => Array.isArray(x) && hasOwn(slots, x[0]));
    const all = !fixed && list.length > 1 ? `<button class="twab-all" data-act="${TW_SLOT_PICK[sv.sec]}" data-arg="${esc(at + ':' + wi)}">${T('tweaks.abyss.replaceAll')}</button>` : '';
    return `<div class="twab-wave"><div class="twab-wh"><b>${flat ? T('tweaks.slot.all') : T('tweaks.abyss.wave', { n: wi + 1 })}</b>${all}</div>
          <div class="twab-mons">${list.map((x) => twSlotTile(sv, g, at + ':' + wi, String(x[0]), Number(x[1]) || 1, fixed)).join('')}</div></div>`;
  }).join('');
}
// A group whose script the page cannot rewrite: said once above its tiles, with the way out for changes
// the draft still holds there (an agent that refuses a whole document over that script would refuse one
// that needs it; one that applies the settings one at a time leaves the script as it is, whatever the
// saved settings say of it, until the card's files are put back — the button takes them out of the saved
// settings alone, and the note says so). '' for a group the page can change.
function twSlotLockedNote(sv, g) {
  if (!hasOwn(sv.locked, g.group)) return '';
  const levelled = sv.sec !== 'domains' ? [] : (Array.isArray(g.dungeons) ? g.dungeons : [])
    .filter((id) => twAt(twDraftCanon(sv.v).canon, ['domains', 'byId', id, 'level']) !== undefined);
  const held = levelled.length > 0 || Object.keys(g.slots || {}).some((cfg) => hasOwn(sv.cur[g.group], cfg));
  const levels = levelled.length ? ' ' + T('tweaks.domains.lockedLevels', { names: levelled.map((id) => twDomainName(sv.v, id)).join(', ') }) : '';
  const saved = held && twKnowsFiles(twDataOf(sv.v)) ? ' ' + T('tweaks.abyss.lockedHeld') : '';
  // (the agent's reason, when it gives one: missing, changed beyond its spawn slots, …)
  const file = twFileName(sv.locked[g.group]), why = (twFiles(sv.v, sv.sec).reasons[sv.locked[g.group]] || {}).reason;
  const text = why ? T('tweaks.abyss.lockedWhy', { file, reason: String(why).replace(/[.\s]+$/, '') }) : T('tweaks.abyss.locked', { file });
  return `<div class="note${held ? ' twnote' : ''}"><span>${text}${levels}${saved}</span>${
    held ? `<button class="btn ghost" data-act="${TW_SLOT_CLEAR[sv.sec]}" data-arg="${esc(g.group)}">${T('tweaks.abyss.lockedClear')}</button>` : ''}</div>`;
}
// The slots an enemy picker is opened for, from what was clicked: a tile (cfg = its config id) or
// "Replace all" (cfg undefined) of wave wi of group g — null when there is nothing to pick for (a wave
// the group does not have, a locked group). {group, cfgs, one: a single slot, wave: the wave's index,
// null for a flat group: all its slots are the one block}.
function twSlotTarget(v, sec, g, wi, cfg) {
  if (!g || !g.slots || hasOwn(twLocked(v, sec), g.group)) return null;
  const flat = !!g.flat, wave = flat ? Object.keys(g.slots).map((c) => [c]) : (Array.isArray(g.waves) ? g.waves : [])[num(wi)];
  if (!Array.isArray(wave)) return null;
  const cfgs = (cfg === undefined ? wave.filter(Array.isArray).map((x) => String(x[0])) : [cfg]).filter((c) => hasOwn(g.slots, c));
  return cfgs.length ? { group: String(g.group), cfgs, one: cfg !== undefined, wave: flat ? null : num(wi) } : null;
}

// ── gameplay: the Spiral Abyss ──
// The enemies of every chamber and their level. The answer's `abyss` describes the tower: schedules =
// the server's own calendar, one row per rotation ({id, floors: twelve floor ids — the ones every
// rotation has, then its own —, open, close}; `active` = the rotation that is live); floors = {floor id:
// {number, teams, chambers: [level ids]}}; chambers = {level id: {floor, index, level, halves: [{group,
// slots: {config id: monster id}, waves: [[[config id, times]]]}]}}. A half is ONE monster group of the
// server's scripts and a slot one spawn point of it; `monsters` = {id: [tier, boss]} lists what a slot
// may hold. The card edits the draft's `abyss` section — 'abyss.slots.<group>.<config id>' = another
// monster in that slot, 'abyss.levels.<level id>' = the chamber's level — and draws ONE chamber: the
// tower has more than a thousand slots.
// An enemy's level is the chamber's level in the server's table plus the level its spawn slot carries,
// and every Abyss slot carries 1. The page shows and takes the level the game shows (floor 12, chamber
// 3: 100); the document holds the table's number (99).
const TW_ABYSS_SLOT_LEVEL = 1;
const twAbyssOf = (v) => { const d = twDataOf(v); return d && d.abyss && typeof d.abyss === 'object' ? d.abyss : {}; };
// What the card asks of an answer again and again, built once per answer: lid = {monster group: the
// level id of its chamber}, uses = {floor id: how many rotations have it}, every = the floor ids all
// rotations have (the floors no rotation swaps).
const TW_ABYSS_FACTS = new WeakMap();
function twAbyssFacts(d) {
  let f = d && typeof d === 'object' ? TW_ABYSS_FACTS.get(d) : null;
  if (f) return f;
  f = { lid: {}, uses: {}, every: [] };
  if (!d || typeof d !== 'object') return f;
  const a = d.abyss || {}, rotations = Array.isArray(a.schedules) ? a.schedules : [];
  Object.keys(a.chambers || {}).forEach((lid) => (a.chambers[lid].halves || []).forEach((h) => { if (h && h.group != null) f.lid[h.group] = lid; }));
  rotations.forEach((s) => (Array.isArray(s.floors) ? s.floors : []).forEach((id) => { f.uses[id] = (f.uses[id] || 0) + 1; }));
  f.every = Object.keys(f.uses).filter((id) => f.uses[id] === rotations.length);
  TW_ABYSS_FACTS.set(d, f);
  return f;
}
// What the draft changes in the Abyss, by chamber and by floor: chambers = {level id: {slots, level,
// fresh}} and floors = {floor id: {fresh}} for the ones it touches — slots = the spawn slots that hold
// another enemy than the original, level = 1 when the enemy level is another one, fresh = part of it
// is not applied yet (it differs from the stored document: an applied change put back counts too) —
// and the totals: slots, slotChambers (the chambers they are in), levels. Kept until a document changes.
let twAbyssMemo = null;
function twAbyssStates(v) {
  const d = twDataOf(v), draft = twDraftCanon(v).canon, stored = twStoredCanon(v).canon, m = twAbyssMemo;
  if (m && m.d === d && m.draft === draft && m.stored === stored) return m.out;
  const cur = draft.abyss || {}, was = stored.abyss || {}, a = (d && d.abyss) || {};
  const out = { chambers: {}, floors: {}, slots: 0, slotChambers: 0, levels: 0 };
  Object.keys(a.chambers || {}).forEach((lid) => {
    const ch = a.chambers[lid], st = { slots: 0, level: 0, fresh: false };
    const level = (cur.levels || {})[lid];
    if (level !== undefined) st.level = 1;
    if (level !== (was.levels || {})[lid]) st.fresh = true;
    (ch.halves || []).forEach((h) => {
      const mine = (cur.slots || {})[h.group], theirs = (was.slots || {})[h.group];
      if (!mine && !theirs) return;
      Object.keys(h.slots || {}).forEach((cfg) => {
        const x = mine ? mine[cfg] : undefined;
        if (x !== undefined) st.slots++;
        if (x !== (theirs ? theirs[cfg] : undefined)) st.fresh = true;
      });
    });
    if (!st.slots && !st.level && !st.fresh) return;
    out.chambers[lid] = st;
    const floor = out.floors[ch.floor] = out.floors[ch.floor] || { fresh: false };
    if (st.fresh) floor.fresh = true;
    out.slots += st.slots; out.levels += st.level;
    if (st.slots) out.slotChambers++;
  });
  twAbyssMemo = { d, draft, stored, out };
  return out;
}
// What the card shows of a version, as the admin left it (S.tw.ui.abyss[v]): rotation = a schedule id,
// floor = a floor id, number = that floor's number (another rotation shows ITS floor of that number),
// index = the chamber, 1..3. Empty until the card is first drawn (twAbyssView fills it).
function twAbyssUi(v) {
  const ui = S.tw.ui;
  if (!ui.abyss) ui.abyss = {};
  if (!ui.abyss[v]) ui.abyss[v] = { rotation: null, floor: null, number: null, index: null };
  return ui.abyss[v];
}
// The same, resolved against the answer: {rotations, rotation, list: the floor ids on offer, floor,
// lids: the level ids of its chambers, index, lid: the chamber on screen}. A card drawn for the first
// time opens on the rotation that is live and on its first chamber that holds changes — floor 9,
// chamber 1 when none does: the floors a rotation swaps are what a rotation is picked for. From then on
// the card stays where the admin left it, whatever the draft does.
function twAbyssView(v) {
  const a = twAbyssOf(v), ui = twAbyssUi(v), st = twAbyssStates(v), floors = a.floors || {}, chambers = a.chambers || {};
  const rotations = Array.isArray(a.schedules) ? a.schedules : [];
  const same = (x, y) => x != null && y != null && String(x) === String(y);
  const rotation = rotations.find((s) => same(s.id, ui.rotation)) || rotations.find((s) => same(s.id, a.active)) || rotations[0] || null;
  // Without a calendar (the server's own could not be read) every floor of the answer is on offer.
  let list = rotation && Array.isArray(rotation.floors) ? rotation.floors.map(String).filter((id) => hasOwn(floors, id)) : [];
  if (!list.length) list = Object.keys(floors).sort((x, y) => (floors[x].number - floors[y].number) || (x - y));
  const floor = list.includes(ui.floor) ? ui.floor
    : list.find((id) => same(floors[id].number, ui.number)) || list.find((id) => st.floors[id])
      || list.find((id) => floors[id].number === 9) || list[0] || '';
  const lids = floor ? (floors[floor].chambers || []).map(String).filter((lid) => hasOwn(chambers, lid)) : [];
  const index = ui.index >= 1 && ui.index <= lids.length ? ui.index : Math.max(1, lids.findIndex((lid) => st.chambers[lid]) + 1);
  ui.rotation = rotation ? rotation.id : null;
  ui.floor = floor || null; ui.number = floor ? floors[floor].number : null; ui.index = index;
  return { rotations, rotation, list, floor, lids, index, lid: lids[index - 1] || '' };
}
// Puts a chamber on screen: its floor, and a rotation that has that floor when the one shown does not
// — the live one if it does, else the first.
function twAbyssGo(v, lid) {
  const a = twAbyssOf(v), ch = (a.chambers || {})[lid], floor = ch && (a.floors || {})[ch.floor];
  if (!floor) return;
  const id = String(ch.floor), has = (s) => Array.isArray(s.floors) && s.floors.map(String).includes(id);
  const view = twAbyssView(v), ui = twAbyssUi(v);
  if (view.rotation && !has(view.rotation)) {
    const s = view.rotations.find((r) => String(r.id) === String(a.active) && has(r)) || view.rotations.find(has);
    if (s) ui.rotation = s.id;
  }
  ui.floor = id; ui.number = floor.number;
  ui.index = Math.max(1, (floor.chambers || []).map(String).indexOf(String(lid)) + 1);
}
// A rotation's days as one range in the language of the page ("Oct 1 – 16" and the year); the id when
// the row carries no times.
let twDayFormat = null;
function twAbyssRange(s) {
  const from = new Date(Number(s.open) * 1000), to = new Date(Number(s.close) * 1000);
  if (isNaN(from) || isNaN(to)) return '#' + s.id;
  try {
    if (!twDayFormat || twDayFormat.code !== I18N.code) twDayFormat = { code: I18N.code, days: new Intl.DateTimeFormat(I18N.code, { year: 'numeric', month: 'short', day: 'numeric' }) };
    return twDayFormat.days.formatRange(from, to);
  } catch (e) { return from.toLocaleDateString() + ' – ' + to.toLocaleDateString(); }
}
// The dot of a floor / chamber chip that holds changes: the page's "not applied yet" dot, or its quiet
// twin once all of them are applied.
// (file = the Abyss card holds a file taken over from changes made outside Relic: what a change differs from is
//  what the files hold, not all of it the original game)
const twAbyssDot = (st, file) => (st ? `<i class="twdot${st.fresh ? '' : ' set'}" title="${st.fresh ? T('tweaks.chip.unapplied')
  : file ? T('tweaks.chip.changedFile') : T('tweaks.chip.changed')}"></i>` : '');
// The rotation select's rows: the days, "now" on the live one, "changed" where the rotation's OWN floors
// hold changes (the floors every rotation has would mark every row).
function twAbyssRotations(v, view) {
  const a = twAbyssOf(v), st = twAbyssStates(v), every = twAbyssFacts(twDataOf(v)).every;
  return view.rotations.map((s) => {
    const changed = (Array.isArray(s.floors) ? s.floors : []).some((id) => st.floors[id] && !every.includes(String(id)));
    const label = [twAbyssRange(s), String(s.id) === String(a.active) ? t('tweaks.abyss.now') : '', changed ? t('tweaks.mark.changed') : ''].filter(Boolean).join(' · ');
    return `<option value="${esc(s.id)}"${s === view.rotation ? ' selected' : ''}>${esc(label)}</option>`;
  }).join('');
}
// The floor chips of the rotation, by floor number; a rule parts the floors every rotation has from
// the rotation's own.
function twAbyssFloorChips(v, view) {
  const floors = twAbyssOf(v).floors || {}, st = twAbyssStates(v), every = twAbyssFacts(twDataOf(v)).every, file = twFiles(v, 'abyss').taken.length > 0;
  let own = null;
  return view.list.map((id) => {
    const mine = !!view.rotation && !every.includes(id), rule = own === false && mine ? '<i class="twab-sep"></i>' : '';
    own = mine;
    // (without a calendar several floors of one number are on offer: the id tells them apart)
    return `${rule}<button class="elchip${id === view.floor ? ' on' : ''}" data-act="tweaksAbyssFloor" data-arg="${esc(id)}"${view.rotation ? '' : ` title="#${esc(id)}"`}>${esc(floors[id].number)}${twAbyssDot(st.floors[id], file)}</button>`;
  }).join('');
}
function twAbyssChamberChips(v, view) {
  const st = twAbyssStates(v), file = twFiles(v, 'abyss').taken.length > 0;
  return view.lids.map((lid, i) => `<button class="elchip${i + 1 === view.index ? ' on' : ''}" data-act="tweaksAbyssChamber" data-arg="${i + 1}">${i + 1}${twAbyssDot(st.chambers[lid], file)}</button>`).join('');
}
// Beside the rotation select: which floors a rotation does not swap ("Floors 1–8 are the same in every
// rotation"; '' when the calendar has no such floor).
function twAbyssEveryNote(v) {
  const floors = twAbyssOf(v).floors || {};
  const numbers = twAbyssFacts(twDataOf(v)).every.map((id) => (floors[id] || {}).number).filter((n) => n > 0);
  return numbers.length ? T('tweaks.abyss.floorsEvery', { from: Math.min(...numbers), to: Math.max(...numbers) }) : '';
}
// Under the chamber's name: in how many rotations the floor on screen is fought, when it is one of the
// rotation's own and other rotations have it too — a change counts for each of them.
function twAbyssFloorNote(v, view) {
  const facts = twAbyssFacts(twDataOf(v)), n = facts.uses[view.floor] || 1;
  if (!view.rotation || !view.floor || facts.every.includes(view.floor) || n < 2) return '';
  return T('tweaks.abyss.floorShared', { floor: (twAbyssOf(v).floors || {})[view.floor].number, n });
}
// The "Original · 72" chip of the chamber's enemy level, and the chamber's Reset button — live when it
// holds a change.
// (a level of a chamber table taken over is the file's — "In the file · 72" —, the original game's in the tooltip)
const twAbyssLevelChips = (v, lid) => {
  const ch = twAbyssOf(v).chambers[lid], game = Number.isInteger(ch.gameLevel) ? ch.gameLevel + TW_ABYSS_SLOT_LEVEL : undefined;
  return twPresetChips('abyss.levels.' + lid, ch.level, [], v, twBaseLabel(twTakenTable(v, 'levels'), TW_ABYSS_SLOT_LEVEL), true, twGameTip(game));
};
function twAbyssResetButton(v, lid) {
  const st = twAbyssStates(v).chambers[lid];
  return `<button class="btn ghost" data-act="tweaksAbyssResetChamber" data-arg="${esc(lid)}" ${st && (st.slots || st.level) ? '' : 'disabled'}>${T('tweaks.abyss.resetChamber')}</button>`;
}
// The chamber on screen: its enemy level, then one column per half — a half is one monster group — with
// its waves in spawn order, each a wrap of enemy tiles (twSlotWaves).
function twAbyssPanel(v, view) {
  const a = twAbyssOf(v), ch = (a.chambers || {})[view.lid];
  if (!ch) return '';
  const sv = twSlotView(v, 'abyss'), halves = Array.isArray(ch.halves) ? ch.halves : [];
  const half = (h, hi) => `<div class="twab-half">${halves.length > 1 ? `<h5>${hi ? T('tweaks.abyss.half2') : T('tweaks.abyss.half1')}</h5>` : ''}${
    twSlotLockedNote(sv, h)}${twSlotWaves(sv, h, hi)}</div>`;
  const path = 'abyss.levels.' + view.lid, original = ch.level + TW_ABYSS_SLOT_LEVEL, level = twGet(path, v);
  const shown = level === undefined ? original : typeof level === 'number' ? level + TW_ABYSS_SLOT_LEVEL : level;
  const note = twAbyssFloorNote(v, view);
  return `<div class="twab-panel">
    <div class="twab-top">
      <div class="twab-name"><b>${T('tweaks.abyss.where', { floor: ((a.floors || {})[view.floor] || {}).number, chamber: view.index })}</b>${note ? `<small>${note}</small>` : ''}</div>
      <div class="twctl"><span class="twab-lbl">${T('tweaks.abyss.level')}</span>
        <div class="chips" data-tw-live="abyss.level.chips">${twAbyssLevelChips(v, view.lid)}</div>
        <input class="txt twnum${twFieldCls(path, v)}" data-tw="${esc(path)}" data-tw-shift="${TW_ABYSS_SLOT_LEVEL}" value="${esc(shown)}" placeholder="${esc(original)}" inputmode="numeric" maxlength="9" autocomplete="off" spellcheck="false"/>
        <span data-tw-live="abyss.reset">${twAbyssResetButton(v, view.lid)}</span></div>
    </div>
    <div class="twab-halves${halves.length > 1 ? '' : ' one'}">${halves.map(half).join('')}</div>
  </div>`;
}
// The card's last line: how much of the Abyss the draft changes, and the way back for all of it.
function twAbyssFoot(v) {
  const st = twAbyssStates(v), said = [];
  if (st.slots) said.push(st.slots === 1 ? T('tweaks.abyss.count.enemy1') : st.slotChambers === 1 ? T('tweaks.abyss.count.enemies1', { n: st.slots }) : T('tweaks.abyss.count.enemies', { n: st.slots, m: st.slotChambers }));
  if (st.levels) said.push(st.levels === 1 ? T('tweaks.abyss.count.level1') : T('tweaks.abyss.count.levels', { n: st.levels }));
  return `<span>${said.join(' ') || T('tweaks.abyss.count.none')}</span>${twResetBtn('abyss', v)}`;
}
function tweaksAbyssCard(v) {
  const off = twSectionOff('abyss', v), a = twAbyssOf(v);
  if (off || !a.floors || !a.chambers) return twCard('abyss', T('tweaks.abyss.title'), v, off);
  const view = twAbyssView(v);
  return twCard('abyss', T('tweaks.abyss.title'), v, `<p class="hint">${T('tweaks.abyss.hint')}</p>
    <div class="twab-nav">
      ${view.rotation ? `<div class="twab-row"><span class="twab-lbl">${T('tweaks.abyss.rotation')}</span>
        <select class="txt" data-tw-rotation data-tw-live="abyss.rotations">${twAbyssRotations(v, view)}</select><small class="twab-tip">${twAbyssEveryNote(v)}</small></div>`
    : `<div class="note">${T('tweaks.abyss.noCalendar')}</div>`}
      <div class="twab-row"><span class="twab-lbl">${T('tweaks.abyss.floor')}</span><div class="twab-chips" data-tw-live="abyss.floors">${twAbyssFloorChips(v, view)}</div>
        <span class="twab-lbl">${T('tweaks.abyss.chamber')}</span><div class="twab-chips" data-tw-live="abyss.chambers">${twAbyssChamberChips(v, view)}</div></div>
    </div>
    ${twAbyssPanel(v, view)}
    <div class="twab-foot" data-tw-live="abyss.foot">${twAbyssFoot(v)}</div>`);
}

// ── gameplay: the enemy picker ──
// The modal picker over the enemies a spawn slot may hold: the ids of the answer's `monsters` ({id:
// [tier, boss]}: tier 1 = the Abyss of this version already places it, 2 = only other places of the game
// do), named and drawn by the display catalogue. S.picker of this kind adds: v = the version, sec = the
// document's section the slots are in ('abyss' | 'domains'), group + cfgs = the slots that take the pick
// (one tile, or every slot of a wave — of the whole line-up when it is flat), one = a single slot,
// wave = the wave's index (null = a flat line-up), mk = the kind chip ('' = all), all = tier 2 is listed
// too; and, for the heading, where the slots are: lid / half = the Abyss chamber and half, id = the
// domain whose editor the picker was opened from.
const TW_MONSTER_KINDS = ['common', 'elite', 'boss'];
// An enemy in one line, for a tooltip: "Hilichurl Fighter — Hili_None_01 · id 21010201". Many ids share
// a name: the catalogue's label, else its variant, tells them apart.
function twMonsterLine(e) {
  if (e.unknown) return e.name;
  const more = String(e.label || e.variant || '');
  return e.name + (more ? ' — ' + more : '') + ' · ' + t('cmd.idBadge', { id: e.id });
}
// Opens the picker for the slots of p — a twSlotTarget plus v, sec and where the slots are — on the
// enemy the slot holds: its neighbours are its own kind.
function twPickMonster(p) {
  S.picker = Object.assign({ kind: 'monster', q: '', el: '', type: '', r: '', mk: '', all: !!S.tw.ui.monstersAll }, p);
  render();
  const body = document.querySelector('#app .pickbody'), held = body && body.querySelector('.twpick.sel');
  if (held) body.scrollTop += held.getBoundingClientRect().top - body.getBoundingClientRect().top - (body.clientHeight - held.offsetHeight) / 2;
}
// The picker's slots: [{cfg, orig: the original monster, now: the one the draft puts there}].
function twPickSlots(p) {
  const orig = twSlotGroups(twDataOf(p.v), p.sec)[p.group] || {}, mine = ((twDraftCanon(p.v).canon[p.sec] || {}).slots || {})[p.group];
  return p.cfgs.map((cfg) => ({ cfg, orig: orig[cfg], now: hasOwn(mine, cfg) ? mine[cfg] : orig[cfg] }));
}
function twMonsterRows(p) {
  const d = twDataOf(p.v) || {}, mons = d.monsters && typeof d.monsters === 'object' ? d.monsters : {};
  // The enemy the slot holds stays in the list whatever its tier, and so does its original one — the way
  // back (a domain may place an enemy the Abyss does not). An original the answer's `monsters` lacks —
  // it may be placed in no other slot — is listed for its own slot all the same.
  const slots = p.one ? twPickSlots(p) : [], held = slots.map((s) => s.now).concat(slots.map((s) => s.orig));
  const own = slots.map((s) => s.orig).filter((id) => Number.isInteger(id) && id > 0 && !hasOwn(mons, id));
  return Object.keys(mons).concat(own).map((id) => {
    const m = Array.isArray(mons[id]) ? mons[id] : [], e = twEntry('monsters', id);
    return { id: num(id), name: e.name, more: e.unknown ? '' : String(e.label || e.variant || ''), icon: e.icon || '', family: String(e.family || ''),
      kind: m[1] ? 'boss' : TW_MONSTER_KINDS.includes(e.kind) ? e.kind : 'common', tier: m[0] === 1 ? 1 : 2, s: e.s };
  }).filter((r) => (p.all || r.tier === 1 || held.includes(r.id)) && (!p.mk || r.kind === p.mk));
}
// Family by family in the catalogue's order, the enemies without one last; the weapon list's groups and
// rows, so the keystroke filter (filterPicker) works on them as it is.
function twMonsterList(rows, hidden, p) {
  const slots = twPickSlots(p), one = p.one && slots.length === 1 ? slots[0] : null, by = {}, families = [];
  rows.forEach((r) => { if (!by[r.family]) { by[r.family] = []; families.push(r.family); } by[r.family].push(r); });
  families.sort((x, y) => (x === '') - (y === ''));
  return families.map((family) => {
    const items = by[family].map((r) => {
      // (the slot's original enemy is what the game itself places there: never "not tested")
      const own = !!one && r.id === one.orig;
      const tags = (own ? `<span class="twtag">${T('tweaks.pick.monster.tagOriginal')}</span>` : '')
        + (r.tier === 1 || own ? '' : `<span class="twtag warn">${T('tweaks.pick.monster.tagUntested')}</span>`)
        + (r.kind === 'boss' ? `<span class="twtag kind">${T('tweaks.pick.kind.boss')}</span>` : r.kind === 'elite' ? `<span class="twtag kind">${T('tweaks.pick.kind.elite')}</span>` : '');
      return `<button class="wrow twpick${one && r.id === one.now ? ' sel' : ''}${hidden(r)}" data-act="pickMonster" data-arg="${r.id}" data-s="${esc(r.s)}" title="${esc(r.name + (r.more ? ' — ' + r.more : ''))}">
        ${iconTile(r.icon, r.name, '')}<span class="tx"><b>${esc(r.name)}</b><small>${T('cmd.idBadge', { id: r.id })}${esc(r.more ? ' · ' + r.more : '')}</small></span>
        <span class="meta">${tags}</span></button>`;
    }).join('');
    const dead = by[family].every((r) => hidden(r)) ? ' hide' : '';
    return `<div class="wgroup${dead}"><h5>${esc(family || t('cmd.picker.others'))}</h5><div class="wgrid twgrid">${items}</div></div>`;
  }).join('');
}
// The kind chips, the switch that keeps the list to what the Abyss already places and, while it is off,
// the caution about the rest — in the bar, so it is read wherever the list is scrolled to.
function twMonsterChips(p) {
  const chip = (kind, label) => `<button class="elchip${p.mk === kind ? ' on' : ''}" data-act="setPickerMonsterKind" data-arg="${kind}">${label}</button>`;
  return `<div class="chips">${chip('', T('common.all'))}${chip('common', T('tweaks.pick.kind.common'))}${chip('elite', T('tweaks.pick.kind.elite'))}${chip('boss', T('tweaks.pick.kind.boss'))}</div>
    <button class="pickonly" data-act="togglePickerAll"><span class="toggle${p.all ? '' : ' on'}"><span class="knob"></span></span><span>${T('tweaks.pick.monster.only')}</span></button>${
    p.all ? `<div class="note">${T('tweaks.pick.monster.untested')}</div>` : ''}`;
}
// Where the slots of an open picker are, in words, per section: the chamber, half and wave of the Abyss;
// the domain, and the wave when its line-up has waves.
const TW_SLOT_PLACE = {
  abyss: (p) => {
    const a = twAbyssOf(p.v), ch = (a.chambers || {})[p.lid] || {}, floor = (a.floors || {})[ch.floor] || {};
    return [t('tweaks.abyss.where', { floor: floor.number || '?', chamber: (floor.chambers || []).map(String).indexOf(String(p.lid)) + 1 || '?' }),
      (ch.halves || []).length < 2 ? '' : p.half ? t('tweaks.abyss.half2') : t('tweaks.abyss.half1'), t('tweaks.abyss.wave', { n: p.wave + 1 })];
  },
  domains: (p) => [twDomainName(p.v, p.id), p.wave === null ? '' : t('tweaks.abyss.wave', { n: p.wave + 1 })],
};
// The line under the picker's title: where the pick is for, and what it replaces.
function twMonsterWhere(p) {
  const slots = twPickSlots(p), place = TW_SLOT_PLACE[p.sec](p).filter(Boolean).join(' · ');
  return p.one && slots.length ? T('tweaks.pick.monster.forSlot', { place, name: twName('monsters', slots[0].now) })
    : T('tweaks.pick.monster.forWave', { place, n: slots.length });
}
// The pickers of the gameplay settings (S.picker.kind), drawn by the modal picker (pickerOverlay) over what
// a SERVER version holds (S.picker.v) — never over the Library's catalogue. title(p) = the heading,
// where(p) = the line under it, rows(p) = [{…, s: the search key}], list(rows, hidden, p) = the body,
// chips(p) = the bar beside the search field (what must stay in sight goes there: it does not scroll),
// note(p) = a note above the list, scrolling with it, hint(p) = the foot line, placeholder(p) = the
// search field's. All but rows answer HTML. A kind with live: true has too many rows to draw them all
// and hide the ones a search text leaves out: rows(p) answers the rows that match the text, list draws
// some of them, and the list is rebuilt on every keystroke; empty(p) = the line of a list that has no
// row before anything is typed.
const TW_PICK = {
  monster: {
    title: (p) => (p.one ? T('tweaks.pick.monster.title') : p.wave === null ? T('tweaks.pick.monster.titleAll') : T('tweaks.pick.monster.titleWave')),
    where: twMonsterWhere,
    rows: twMonsterRows,
    list: twMonsterList,
    chips: twMonsterChips,
    note: () => '',
    hint: () => T('tweaks.pick.monster.hint'),
    placeholder: () => T('tweaks.pick.search'),
  },
  item: {
    live: true,
    title: () => T('tweaks.pick.item.title'),
    where: twItemWhere,
    rows: twItemRows,
    list: twItemList,
    chips: twItemChips,
    note: twItemNote,
    hint: (p) => (p.use === 'domain' ? T('tweaks.pick.item.hintDomain') : T('tweaks.pick.item.hint')),
    placeholder: () => T('tweaks.pick.search'),
    empty: () => (S.twData.status === 'ok' ? T('tweaks.pick.item.empty') : T('tweaks.pick.item.emptyNoNames')),
  },
};
// The entry of an open picker (S.picker) when it is one of the page's; null for the Game commands kinds.
const twPicker = (p) => (p && hasOwn(TW_PICK, p.kind) ? TW_PICK[p.kind] : null);
// The page's drafts were dropped (another server, a new agent): a picker of the page that is open was
// choosing for one of them.
function twPickerGone() { if (twPicker(S.picker)) S.picker = null; }

// ── gameplay: rewards — what the Domain and the Quest card share ──
// Both cards hold a searched list (the domains, the quests' rewards) whose open row is edited in place,
// and lists of items with an amount each. A list is never drawn whole — a version has more than a
// hundred domains and close to a thousand quest rewards: one page of TW_LIST_ROWS rows at a time. Its
// view is the admin's, per version, in S.tw.ui[card][v]: kind = the chip ('' = all, 'own' = the rows
// with settings of their own), q = the search text, page, open = the row whose editor is open (the Quest
// card adds drawn = {rid, items}: the reward whose editor was drawn last and the items of its rows, in
// their order).
const TW_LIST_ROWS = 12;
function twListUi(card, v) {
  const ui = S.tw.ui;
  if (!ui[card]) ui[card] = {};
  if (!ui[card][v]) ui[card][v] = { kind: '', q: '', page: 0, open: '' };
  return ui[card][v];
}
// The chip of a list as it counts: "own" is all again once no row has settings of its own (own = the
// rows that have).
function twListKind(card, v, own) {
  const ui = twListUi(card, v);
  if (ui.kind === 'own' && !own.length) ui.kind = '';
  return ui.kind;
}
// One page of a list: {rows, from, page, pages}. A page the list no longer has (a search made it
// shorter) falls back into it.
function twPage(rows, ui, size) {
  const pages = Math.max(1, Math.ceil(rows.length / size));
  ui.page = Math.min(Math.max(0, num(ui.page)), pages - 1);
  const from = ui.page * size;
  return { rows: rows.slice(from, from + size), from, page: ui.page, pages };
}
// The line under a list with more rows than one page: which rows are on screen, and the way to the others.
function twPager(card, total, page) {
  if (page.pages < 2) return '';
  const button = (step, title, on) => `<button class="twpg" data-act="tweaksPage" data-arg="${card}:${step}" title="${title}" ${on ? '' : 'disabled'}>${step < 0 ? '‹' : '›'}</button>`;
  return `<div class="twmore"><span>${T('tweaks.list.range', { from: page.from + 1, to: page.from + page.rows.length, n: total })}</span>${
    button(-1, T('tweaks.list.prev'), page.page > 0)}${button(1, T('tweaks.list.next'), page.page < page.pages - 1)}</div>`;
}
// A list with no row to show: nothing matches the search, or the chip has nothing.
const twListNone = (ui) => `<div class="twnone">${ui.q.trim() ? T('tweaks.list.noneFor', { q: ui.q.trim() }) : T('tweaks.list.none')}</div>`;
// The lists of the page by card: what [data-tw-list="<card>"] holds. A list is redrawn in place by its
// search field (twFind) and by nothing else between two renders — the fields of its open editor are
// typed in.
const TW_LISTS = { domains: (v) => twDomainsList(v), quests: (v) => twQuestsList(v) };
// A search field of the page (data-tw-find = its card) was typed in: the list of the version the page
// shows follows in place, from its first page — never a render(), which would take the caret (but for a
// page drawn from another server's documents: see twTyped).
function twFind(el) {
  const card = el.getAttribute('data-tw-find'), v = twDrawn();
  if (!v) { render(); return; }
  if (!hasOwn(TW_LISTS, card) || !twReady(v)) return;
  Object.assign(twListUi(card, v), { q: el.value, page: 0 });
  const box = document.querySelector(`#app .twpage [data-tw-list="${card}"]`);
  if (box) box.innerHTML = TW_LISTS[card](v);
}
// A row of a list was clicked: its editor opens under it — the open one closes — and comes into sight.
function twOpenRow(card, key) {
  const v = twCur();
  if (!twReady(v)) return;
  const ui = twListUi(card, v);
  ui.open = ui.open === String(key) ? '' : String(key);
  render();
  const editor = ui.open && document.querySelector(`#app .twpage [data-tw-list="${card}"] .twed`);
  if (editor) editor.scrollIntoView({ block: 'nearest' });
}
// The item picker for a list of the page: use = 'domain' (the extra items of dungeon `id`) | 'quest'
// (the reward row `id`).
function twPickItem(v, use, id) {
  S.picker = { kind: 'item', q: '', el: '', type: '', r: '', v, use, id: String(id), ig: '' };
  render();
}
// The multipliers a chip offers beside the original ×1.
const TW_MULTIPLIERS = [2, 3, 5, 10];
// The chips of a multiplier of the document: "Original · ×1", the presets, and the draft's own value
// when it is none of them.
function twMultChips(path, v) {
  const cur = twInt(twGet(path, v));
  const presets = TW_MULTIPLIERS.concat(cur > 1 && !TW_MULTIPLIERS.includes(cur) ? [cur] : []).sort((a, b) => a - b);
  return twPresetChips(path, 1, presets, v, (n, original) => (original ? T('tweaks.chip.original', { value: '×' + n }) : '×' + esc(n)));
}
// A number as the page's language writes it: an amount (5,050), an average with `digits` decimals at
// most (0.57).
function twAmount(n, digits) {
  try { return Number(n).toLocaleString(I18N.code, { maximumFractionDigits: digits || 0 }); } catch (e) { return String(n); }
}
// The largest amount of an item the server takes in one place (0 = it does not list the item).
const twItemCap = (v, item) => { const x = ((twDataOf(v) || {}).items || {})[item]; return Array.isArray(x) ? Number(x[0]) || 0 : 0; };
// Resin handed out as an item is the one amount the game itself limits when the reward is claimed: a
// list that holds it says so.
const TW_RESIN_ITEM = 106;
function twResinNote(v, list) {
  if (!list.some((e) => e && twNum(e.item) === TW_RESIN_ITEM)) return '';
  return `<div class="note">${T('tweaks.item.resin', { item: twName('items', TW_RESIN_ITEM), max: twAmount(((twDataOf(v) || {}).resin || {}).hardMax || 2000) })}</div>`;
}
// A character card is one per cell — a second card in the same cell would go to the bag instead of
// the roster, so the agent's catalogue caps it at 1 and the page never offers an amount for it: the
// row draws the amount as text, and a pick gives the caret to nothing. Pinned = the display
// catalogue's group, or the agent's own cap of 1: the catalogue row is the authority on "one per
// cell", and it caps a card the display catalogue files elsewhere (a CB1 card, a Traveler card) like
// any other — and a quest item the server takes one of at most: nothing to type for either. (A
// constellation item is not pinned: each one is a further constellation.)
const twItemPinned = (v, item) => twEntry('items', item).group === 'character' || twItemCap(v, item) === 1;
// A character for the roster note: the display group, or a pinned item the agent's keep list names —
// that list holds the characters and the constellation items (never capped at 1), so cap 1 among them
// is a character card whatever the display catalogue files it under; a quest item capped at 1 is none.
const twItemCharacter = (v, item) => twEntry('items', item).group === 'character' || (twItemCap(v, item) === 1 && twKeep(twDataOf(v)).has(twNum(item)));
// A character in a reward joins the roster the moment the quest completes — said once under a list
// that holds one (an owned character's second card is converted by the game).
function twCharacterNote(v, list) {
  if (!list.some((e) => e && twItemCharacter(v, twNum(e.item)))) return '';
  return `<div class="note">${T('tweaks.quests.characterNote')}</div>`;
}
// One item of a list the page edits (a domain's extra items, a quest's reward): its sprite, its name
// over its id and the largest amount the server takes, the amount field (data-tw = `path`; '' = a list
// that cannot be edited: the amount as text; a pinned item keeps the text in an edited list too, its
// one beside the button) and the button that takes the item out (remove = [action, argument]; null =
// the one item a list must keep).
function twItemRow(v, path, item, count, remove) {
  const e = twEntry('items', item), cap = twItemCap(v, item), pinned = !!path && twItemPinned(v, item);
  const amount = path
    ? `${pinned ? `<span class="n">×${esc(twAmount(count))}</span>`
      : `<input class="txt twnum${twFieldCls(path, v)}" data-tw="${esc(path)}" value="${esc(count === undefined ? '' : count)}" inputmode="numeric" maxlength="9" autocomplete="off" spellcheck="false"/>`}
      <button class="twit-x" ${remove ? `data-act="${remove[0]}" data-arg="${esc(remove[1])}" title="${T('tweaks.item.remove')}"` : `disabled title="${T('tweaks.item.last')}"`}>×</button>`
    : `<span class="n">×${esc(twAmount(count))}</span>`;
  return `<div class="twit">${iconTile(e.icon, e.name, '')}<span class="tx" title="${esc(e.name)}"><b>${esc(e.name)}</b><small>${T('cmd.idBadge', { id: item })}${
    cap && path && !pinned ? ' · ' + T('tweaks.item.upTo', { n: twAmount(cap) }) : ''}</small></span>${amount}</div>`;
}

// ── gameplay: the domains ──
// What the reward statue of a domain gives and what it takes, and the enemies a domain holds and their
// level. The answer's `domains` lists the repeatable domains of the version, {dungeon id: {kind:
// 'artifact' | 'talent' | 'weapon', cost, max: the largest multiplier its reward tolerates, level: the
// enemy level the game shows for it, free: how many more items its reward has room for, own: the items
// its reward row holds itself, preview: [[item id, the average amount per claim]], group: the monster
// group of its line-up (twLineups), when it has one, levels: [lowest, highest] the enemy level can be
// set to, when the agent can set it (twDomainLevels)}}, and `items` = {item id: [the largest amount the
// server takes, 1 = the item may be added to a domain's reward]}. The card edits the draft's `domains`
// section: 'domains.multiplier' and 'domains.cost' for every domain, under 'domains.byId.<dungeon id>'
// one domain's own `multiplier`, `cost`, `extra` items ([{item, count}], always dropped) and `level`,
// and 'domains.slots.<group>.<config id>' = another monster in that spawn slot of a line-up — ONE
// monster group that the weekday variants of one difficulty share, so its changes are no domain's own.
// The domains are a searched list, TW_LIST_ROWS of them on screen; the open one is edited in place.
// A domain's `level` is the level of an enemy on a spawn slot that carries level 1, as nearly all do —
// the number the game shows at the entrance for the original — and the document holds it as the field
// shows it. The server raises everything the domain places with a level of its own by the same amount:
// an enemy on a spawn slot that carries level 4 stands 3 above the level set, and an object likewise
// (the monolith a challenge asks to protect is one). That is why `levels` ends lower for some domains
// than `limits.domainLevel`, the range of all of them: nothing a domain places may pass its last level.
const TW_DOMAIN_KINDS = ['artifact', 'talent', 'weapon'];
const TW_DOMAIN_COSTS = [10, 5, 1];
const TW_ROMAN = ['', 'I', 'II', 'III', 'IV', 'V', 'VI', 'VII', 'VIII', 'IX', 'X'];
// The domains of an answer as the list shows them, built once per answer and display catalogue: {id,
// kind, level, name: the dungeon's own name with its numeral, entrance: the map entrance players know
// ('' = none), off: it cannot be entered in the game (no entrance, or not released), icon: the first
// item of its reward that is no currency, s: the search key}. The kinds in the chips' order, the
// domains nobody can enter last.
const TW_DOMAIN_INDEX = new WeakMap();
function twDomainRows(v) {
  const d = twDataOf(v), names = S.twData.list.domains, m = d ? TW_DOMAIN_INDEX.get(d) : null;
  if (m && m.names === names) return m.rows;
  const list = d && d.domains && typeof d.domains === 'object' ? d.domains : {};
  const rows = Object.keys(list).map((id) => {
    const a = list[id] || {}, e = twEntry('domains', id);
    const name = e.unknown ? e.name : e.name + (TW_ROMAN[e.tier] ? ' ' + TW_ROMAN[e.tier] : '');
    const entrance = e.unknown ? '' : String(e.entrance || '');
    // (a reward the hand changed in a drop table taken over has the original game's preview only: its sprite still names the domain)
    const preview = Array.isArray(a.preview) ? a.preview : Array.isArray(a.gamePreview) ? a.gamePreview : [];
    const gives = preview.map((p) => p[0]).find((item) => !TW_REWARD_VIRTUAL.includes(item));
    return { id, kind: String(a.kind || ''), level: a.level, name, entrance, off: !e.unknown && (!entrance || e.released === false), icon: gives || 0,
      s: searchKey(entrance, name, e.city, id) };
  });
  const order = (r) => { const at = TW_DOMAIN_KINDS.indexOf(r.kind); return at < 0 ? TW_DOMAIN_KINDS.length : at; };
  rows.sort((x, y) => (x.off - y.off) || (order(x) - order(y)) || (x.id - y.id));
  if (d) TW_DOMAIN_INDEX.set(d, { names, rows });
  return rows;
}
// "Midsummer Courtyard — Domain of Blessing: Fires of Purification VI": a domain in the page's texts.
function twDomainName(v, id) {
  const r = twDomainRows(v).find((x) => x.id === String(id));
  return !r ? '#' + id : r.entrance ? r.entrance + ' — ' + r.name : r.name;
}
// The domains a line-up serves, among the ones the answer lists: [dungeon ids].
function twLineupDungeons(v, gid) {
  const d = twDataOf(v), lu = twLineups(d)[gid];
  return (lu && Array.isArray(lu.dungeons) ? lu.dungeons : []).map(String).filter((id) => hasOwn(d.domains, id));
}
// The one of them a line-up is named by and shown under, where the page speaks of the line-up alone: the
// domain whose editor is open when the line-up serves it, else the first one in the order of the list —
// the domains players can enter come first there. '' when the answer lists none of them.
function twLineupDomain(v, gid) {
  const ids = twLineupDungeons(v, gid), open = twListUi('domains', v).open;
  return ids.includes(open) ? open : (twDomainRows(v).find((r) => ids.includes(r.id)) || { id: '' }).id;
}
// The line-up of a domain — the monster group its entry names — as the spawn-slot functions take a
// group: {group, slots, waves, flat: the answer gives it no waves — the order of the fight is not known
// —, dungeons, gameSlots: the original game's enemy of each slot whose script, taken over, holds
// another}. null for a domain without one, and for every domain of an answer that carries no line-ups.
function twLineupOf(v, id) {
  const d = twDataOf(v), a = d && d.domains && d.domains[id], gid = a && a.group != null ? String(a.group) : '';
  const lu = gid ? twLineups(d)[gid] : null;
  return lu && lu.slots && typeof lu.slots === 'object' ? { group: gid, slots: lu.slots, waves: lu.waves, flat: !Array.isArray(lu.waves), dungeons: twLineupDungeons(v, gid),
    gameSlots: lu.gameSlots } : null;
}
// What the draft changes in the line-ups, {group id: {slots: the spawn slots that hold another enemy than
// the original, fresh: part of it is not applied yet (it differs from the stored document: an applied
// change put back counts too)}} for the groups either document touches. Kept until a document changes.
let twLineupMemo = null;
function twLineupStates(v) {
  const d = twDataOf(v), draft = twDraftCanon(v).canon, stored = twStoredCanon(v).canon, m = twLineupMemo;
  if (m && m.d === d && m.draft === draft && m.stored === stored) return m.out;
  const cur = (draft.domains || {}).slots || {}, was = (stored.domains || {}).slots || {}, out = {};
  Object.keys(cur).concat(Object.keys(was).filter((gid) => !hasOwn(cur, gid))).forEach((gid) => {
    out[gid] = { slots: Object.keys(cur[gid] || {}).length, fresh: JSON.stringify(cur[gid] || null) !== JSON.stringify(was[gid] || null) };
  });
  twLineupMemo = { d, draft, stored, out };
  return out;
}
// The settings of a domain's own that the canonical draft — or the stored document — holds (undefined
// = none), and the domains to look at: the ones that have some in either — one whose settings were
// taken out and not applied yet is still one — and the ones whose line-up either document changes.
const twDomainOwn = (v, id, stored) => (((stored ? twStoredCanon(v) : twDraftCanon(v)).canon.domains || {}).byId || {})[id];
function twDomainOwnIds(v) {
  const now = (twDraftCanon(v).canon.domains || {}).byId || {}, was = (twStoredCanon(v).canon.domains || {}).byId || {};
  const ids = Object.keys(now).concat(Object.keys(was).filter((id) => !hasOwn(now, id)));
  Object.keys(twLineupStates(v)).forEach((gid) => twLineupDungeons(v, gid).forEach((id) => { if (!ids.includes(id)) ids.push(id); }));
  return ids;
}
// The cost every domain has in the original game (undefined when they differ).
function twDomainsCost(v) {
  const list = twDataOf(v).domains || {}, costs = Object.keys(list).map((id) => list[id].cost);
  return costs.length && costs.every((c) => c === costs[0]) ? costs[0] : undefined;
}
// What a domain gets while it has no setting of its own: the multiplier of all domains — never above
// what its own reward tolerates (top) — and their cost, as the draft has them.
function twDomainBase(v, id) {
  const d = twDataOf(v), a = d.domains[id] || {}, all = twDraftCanon(v).canon.domains || {};
  const most = Number(d.limits && d.limits.multiplierMax) || 10, top = Math.min(most, Number(a.max) || most);
  return { top, mult: Math.min(typeof all.multiplier === 'number' && all.multiplier >= 1 ? all.multiplier : 1, top), cost: typeof all.cost === 'number' ? all.cost : a.cost };
}
// The small line of a domain's cost: what an empty field means — the cost the draft sets for every domain, else
// the one the domain's row holds — and the original game's cost where the server's file holds another (a
// dungeon table taken over from changes made outside Relic: `gameCost`).
function twDomainLike(v, id) {
  const a = (twDataOf(v).domains || {})[id] || {};
  return T('tweaks.domains.likeCost', { n: twDomainBase(v, id).cost }) + (Number.isInteger(a.gameCost) ? ' ' + T('tweaks.domains.gameCost', { n: a.gameCost }) : '');
}
// How many items can be added to a domain's reward: the agent's limit, and the room the reward has.
function twDomainExtraMax(v, id) {
  const d = twDataOf(v), a = d.domains[id] || {}, most = Number(d.limits && d.limits.extraMax) || 8;
  return Math.max(0, Math.min(most, typeof a.free === 'number' ? a.free : most));
}
// The kind chips of the list; "Own settings" is offered while a domain has some.
function twDomainKinds(v) {
  const own = twDomainOwnIds(v), kind = twListKind('domains', v, own);
  const chip = (id, label) => `<button class="elchip${kind === id ? ' on' : ''}" data-act="tweaksListKind" data-arg="domains:${id}">${label}</button>`;
  return chip('', T('common.all')) + chip('artifact', T('tweaks.domains.kind.artifact')) + chip('talent', T('tweaks.domains.kind.talent')) + chip('weapon', T('tweaks.domains.kind.weapon'))
    + (own.length ? chip('own', T('tweaks.domains.kind.own', { n: own.length })) : '');
}
// The marks of a row: a reward kept as it was changed outside Relic, what the domain has of its own — its
// multiplier, its cost, its extra items, its enemy level ("Lv. 120") —, how many enemies of its line-up are
// changed, and the page's dot while part of that is not applied. The reward of a kept domain is touched by no
// multiplier and no extra item: its own multiplier and extra items are marked as not applying. An enemy level the
// files keep in effect while the draft does not hold it (twLevelInEffect) is marked as one that stays.
function twDomainTags(v, id) {
  const own = twDomainOwn(v, id), tag = (text) => `<span class="twtag">${text}</span>`;
  const lu = twLineupOf(v, id), foes = (lu && twLineupStates(v)[lu.group]) || { slots: 0, fresh: false };
  const isKept = ((twDataOf(v).domains || {})[id] || {}).kept === true;
  const kept = isKept ? `<span class="twtag kind">${T('tweaks.domains.tag.kept')}</span>` : '';
  const drop = (text) => (isKept ? `<span class="twtag na" title="${T('tweaks.domains.tag.notApplied')}">${text}</span>` : tag(text));
  const stays = own && own.level !== undefined ? undefined : twLevelInEffect(v, id);
  return kept + (stays === undefined ? '' : `<span class="twtag kind" title="${T('tweaks.domains.tag.levelHeld')}">${T('tweaks.domains.tag.level', { n: stays })}</span>`)
    + (!own ? '' : (own.multiplier !== undefined ? drop('×' + esc(own.multiplier)) : '')
    + (own.cost !== undefined ? tag(T('tweaks.domains.tag.cost', { n: own.cost })) : '')
    + (own.extra ? drop(own.extra.length === 1 ? T('tweaks.domains.tag.extra1') : T('tweaks.domains.tag.extra', { n: own.extra.length })) : '')
    + (own.level !== undefined ? tag(T('tweaks.domains.tag.level', { n: own.level })) : ''))
    + (!foes.slots ? '' : tag(foes.slots === 1 ? T('tweaks.domains.tag.enemy1') : T('tweaks.domains.tag.enemies', { n: foes.slots })))
    + (foes.fresh || JSON.stringify(own || null) !== JSON.stringify(twDomainOwn(v, id, true) || null) ? `<i class="twdot" title="${T('tweaks.chip.unapplied')}"></i>` : '');
}
// The multiplier chips of one domain: "Like the others" with what that is, then every other multiplier
// its reward tolerates. A domain's own value equal to what it gets anyway is "like the others".
function twDomainMultChips(v, id) {
  const base = twDomainBase(v, id), own = twInt(twGet(['domains', 'byId', id, 'multiplier'], v));
  const cur = own === undefined || own === base.mult ? 0 : own;
  const chip = (n, label) => `<button class="elchip${cur === n ? ' on' : ''}" data-act="tweaksDomainMult" data-arg="${esc(id)}=${n || ''}">${label}</button>`;
  let chips = chip(0, T('tweaks.domains.like', { value: '×' + base.mult }));
  for (let n = 1; n <= base.top; n++) if (n !== base.mult) chips += chip(n, '×' + n);
  return chips;
}
const twDomainResetButton = (v, id) => `<button class="btn ghost" data-act="tweaksDomainReset" data-arg="${esc(id)}" ${twGet(['domains', 'byId', id], v) === undefined ? 'disabled' : ''}>${T('tweaks.domains.reset')}</button>`;
// The main drops of a domain as small tiles, each with its average amount per claim under the
// multiplier the domain has: {mult, tiles, game: the tiles are the original game's drops, unmultiplied — the
// answer gives no preview of a reward the hand changed in a drop table taken over (`gamePreview` in place of
// `preview`)}. A reward kept as it was changed outside Relic (`kept`) is touched by no multiplier.
function twDomainGives(v, id) {
  const a = twDataOf(v).domains[id] || {}, base = twDomainBase(v, id), own = twInt(twGet(['domains', 'byId', id, 'multiplier'], v));
  const game = !Array.isArray(a.preview) && Array.isArray(a.gamePreview);
  const mult = a.kept === true || game ? 1 : own >= 1 ? Math.min(own, base.top) : base.mult;
  const tiles = (game ? a.gamePreview : Array.isArray(a.preview) ? a.preview : []).map(([item, n]) => {
    const e = twEntry('items', item), x = Number(n) * mult;
    return `<span class="twgive" title="${esc(e.name)}">${iconTile(e.icon, e.name, '')}<b>${esc(twAmount(x, x >= 100 ? 0 : x >= 10 ? 1 : 2))}</b></span>`;
  }).join('');
  return { mult, tiles, game };
}
// The "Original · 90" chip of a domain's enemy level: it takes the level out of the domain's own
// settings ("Reset domain" has nothing left to do for a domain that held the level alone).
// (a level of a dungeon table taken over is the file's — "In the file · 90" —, the original game's in the tooltip)
const twDomainLevelChips = (v, id) => {
  const e = twDataOf(v).domains[id];
  return twPresetChips(`domains.byId.${id}.level`, e.level, [], v, twBaseLabel(twTakenTable(v, 'costs')), true, twGameTip(e.gameLevel));
};
// Why the enemy level of a domain ends below the limit of all domains — '' where it does not, and where
// the answer's numbers do not say. What a domain places above the level set reaches the last level
// first: an object, when the line-up's `top` is above the largest level a spawn slot of its enemies
// carries (`level`), else the enemy of such a slot — each as far above the level set as its own level is
// above 1. The sentence is said only while the numbers bear it out: the highest level the field takes
// and that distance add up to the limit.
function twDomainLevelCap(v, id) {
  const d = twDataOf(v), a = d.domains[id] || {}, levels = twDomainLevels(a), all = twDomainLevelLimit(d);
  if (!levels || !all || !(levels[1] < all[1])) return '';
  const lu = twLineups(d)[a.group] || {}, top = Number(lu.top) || 0, level = Number(lu.level) || 0, object = top > level;
  const said = { max: levels[1], n: (object ? top : level) - 1, limit: all[1] };
  if (said.max + said.n !== said.limit) return '';
  return object ? T('tweaks.domains.enemyLevelCapObject', said) : T('tweaks.domains.enemyLevelCapEnemy', said);
}
// The enemy level of the open domain — for a domain the answer gives no range for (twDomainLevels) only
// the note about a level the draft still holds, else '' —: the "Original · 90" chip and the number field, and under the name what stays as it is in the game;
// then one line: why this domain's range ends where it does, when that is below the limit of all
// domains (twDomainLevelCap), and what the game draws above an enemy. The level is the domain's own —
// "Reset domain" takes it out, "Reset enemies" does not —, so the row stands above the rule of the
// enemies, which the line-up may share with other domains.
// The enemy level of a domain that the files keep in effect while it cannot be changed — the agent names it among
// the settings that rest on a file it cannot use (`blocked`) and holds it in `active` —, undefined otherwise.
function twLevelInEffect(v, id) {
  const d = twDataOf(v);
  return twKnowsFiles(d) && twFiles(v, 'domains').blocked.includes(`domains.byId.${id}.level`) ? twAt(d.active, ['domains', 'byId', id, 'level']) : undefined;
}
function twDomainLevel(v, id) {
  const a = twDataOf(v).domains[id] || {};
  if (!twDomainLevels(a)) {
    // (no range: a level the draft still holds is one the script of the line-up no longer bounds —
    //  twCanon keeps only such a one —, said with the way to take it out. An agent that applies the settings
    //  one at a time keeps a level in effect there (`active`) in the dungeon table until the Domain card's
    //  files are put back, whatever the saved settings say: the note says what the file keeps, and that
    //  taking the level out of the saved settings leaves it there until then)
    const held = twAt(twDraftCanon(v).canon, ['domains', 'byId', id, 'level']), now = twLevelInEffect(v, id);
    const drop = `<button class="btn ghost" data-act="tweaksDomainLevelDrop" data-arg="${esc(id)}">${T('tweaks.domains.enemyLevelDrop')}</button>`;
    if (now === undefined) return held === undefined ? '' : `<div class="note twnote"><span>${T('tweaks.domains.enemyLevelLocked', { n: held })}</span>${drop}</div>`;
    if (held === undefined) return `<div class="note">${T('tweaks.domains.enemyLevelHeldOut', { n: now })}</div>`;
    return `<div class="note twnote"><span>${held === now ? T('tweaks.domains.enemyLevelHeld', { n: now }) : T('tweaks.domains.enemyLevelLockedHeld', { n: held, now })}</span>${drop}</div>`;
  }
  const path = `domains.byId.${id}.level`, cap = twDomainLevelCap(v, id);
  // (the entrance shows the client's own data: the original game's level, also where the server's file holds another)
  return `<div class="twed-row"><div class="twed-lbl"><b>${T('tweaks.domains.enemyLevel')}</b><small>${T('tweaks.domains.enemyLevelSub', { n: Number.isInteger(a.gameLevel) ? a.gameLevel : a.level })}</small></div>
      <div class="twctl"><div class="chips" data-tw-live="domains.level.chips:${esc(id)}">${twDomainLevelChips(v, id)}</div>
        <input class="txt twnum${twFieldCls(path, v)}" data-tw="${esc(path)}" value="${esc(twShown(path, a.level, v))}" placeholder="${esc(a.level)}" inputmode="numeric" maxlength="9" autocomplete="off" spellcheck="false"/></div></div>
    <p class="twed-say">${cap ? cap + ' ' : ''}${T('tweaks.domains.enemyLevelShown')}</p>`;
}
// The enemies of the open domain: what stays as it is in the game and what other enemies do to a
// challenge, then the waves of its line-up — or all its slots, when the order of the fight is not known
// — as the Abyss card draws a half's (twSlotWaves). The line-up may serve other domains too: the block
// names them — and says that the enemy level is no part of what they share, where it can be set —, and
// its Reset is the line-up's own — "Reset domain" stays about the domain's own settings. A line-up whose
// script the page cannot rewrite is shown with the note that says so; the rest of the card is not
// touched by that. '' for a domain without a line-up.
function twDomainEnemies(v, id) {
  const lu = twLineupOf(v, id);
  if (!lu) return '';
  const sv = twSlotView(v, 'domains'), rows = twDomainRows(v), mine = rows.find((r) => r.id === String(id)) || {};
  // (a domain behind the same entrance is told apart by its own name)
  const others = lu.dungeons.filter((x) => x !== String(id)).map((x) => { const r = rows.find((y) => y.id === x); return r && r.entrance === mine.entrance ? r.name : twDomainName(v, x); });
  const names = others.join(', ');
  const shared = !others.length ? '' : twDomainLevels(twDataOf(v).domains[id]) ? T('tweaks.domains.enemiesShared', { names }) : T('tweaks.domains.enemiesSharedNoLevel', { names });
  return `<div class="twed-foes">
    <div class="twed-row"><div class="twed-lbl"><b>${T('tweaks.domains.enemies')}</b><small>${T('tweaks.domains.enemiesSub')}</small></div>
      <button class="btn ghost" data-act="tweaksLineupReset" data-arg="${esc(lu.group)}" ${hasOwn(sv.cur, lu.group) ? '' : 'disabled'}>${T('tweaks.domains.enemiesReset')}</button></div>
    <p class="twed-say">${T('tweaks.domains.enemiesCaution')} ${T('tweaks.domains.enemiesBoss')}${lu.flat ? ' ' + T('tweaks.domains.enemiesFlat') : ''}</p>
    ${shared ? `<div class="note">${shared}</div>` : ''}${twSlotLockedNote(sv, lu)}
    <div>${twSlotWaves(sv, lu, lu.group)}</div>
  </div>`;
}
// The open domain, under its row: its own multiplier over what its reward gives with it, its own cost
// (empty = like the others), the items added to its reward, its enemy level and its enemies.
function twDomainEditor(v, r) {
  const id = r.id, path = `domains.byId.${id}`;
  const held = twGet(['domains', 'byId', id, 'extra'], v), extra = Array.isArray(held) ? held : [], most = twDomainExtraMax(v, id), gives = twDomainGives(v, id);
  const items = extra.map((e, i) => twItemRow(v, `${path}.extra.${i}.count`, twNum(e && e.item), e ? e.count : undefined, ['tweaksDomainExtraRemove', id + ':' + i])).join('');
  return `<div class="twed">
    <div class="twed-head"><div class="twed-name"><b>${esc(r.entrance || r.name)}</b><small>${esc([r.entrance ? r.name : '', t('tweaks.domains.level', { n: r.level }), t('cmd.idBadge', { id })].filter(Boolean).join(' · '))}</small></div>
      <span data-tw-live="domains.reset:${esc(id)}">${twDomainResetButton(v, id)}</span>
      <button class="twed-x" data-act="tweaksDomainOpen" data-arg="${esc(id)}" title="${T('common.close')}">×</button></div>
    ${r.off ? `<div class="note">${T('tweaks.domains.closedNote')}</div>` : ''}
    ${(twDataOf(v).domains[id] || {}).kept === true ? `<div class="note">${T('tweaks.domains.keptNote')}</div>` : ''}
    <div class="twed-row"><div class="twed-lbl"><b>${T('tweaks.domains.drops')}</b><small>${gives.game ? T('tweaks.domains.givesGame')
      : gives.mult === 1 ? T('tweaks.domains.givesSub') : T('tweaks.domains.givesSubTimes', { n: gives.mult })}</small></div>
      <div class="twab-chips">${twDomainMultChips(v, id)}</div></div>
    <div class="twgives">${gives.tiles}</div>
    <div class="twed-row"><div class="twed-lbl"><b>${T('tweaks.domains.cost')}</b><small data-tw-live="domains.like:${esc(id)}">${twDomainLike(v, id)}</small></div>
      <input class="txt twnum${twFieldCls(path + '.cost', v)}" data-tw="${esc(path)}.cost" value="${esc(twShown(path + '.cost', '', v))}" inputmode="numeric" maxlength="9" autocomplete="off" spellcheck="false"/></div>
    <div class="twed-row"><div class="twed-lbl"><b>${T('tweaks.domains.extra')}</b><small>${most ? T('tweaks.domains.extraSub') : T('tweaks.domains.extraNoRoom')}</small></div>
      <div class="twed-add"><span>${most ? T('tweaks.item.count', { n: extra.length, max: most }) : ''}</span>
        <button class="btn ghost" data-act="tweaksDomainExtraAdd" data-arg="${esc(id)}" ${extra.length < most ? '' : 'disabled'}>${T('tweaks.item.add')}</button></div></div>
    ${items ? `<div class="twits">${items}</div>` : ''}${twResinNote(v, extra)}${twDomainLevel(v, id)}${twDomainEnemies(v, id)}
  </div>`;
}
// The rows the list shows under its chip and search text.
function twDomainShown(v) {
  const q = twListUi('domains', v).q.trim().toLowerCase(), own = twDomainOwnIds(v), kind = twListKind('domains', v, own);
  return twDomainRows(v).filter((r) => (kind === 'own' ? own.includes(r.id) : !kind || r.kind === kind) && (!q || r.s.includes(q)));
}
// The list: one page of rows in two columns, the open domain's editor under the line of its row.
function twDomainsList(v) {
  const ui = twListUi('domains', v), rows = twDomainShown(v);
  if (!rows.length) return twListNone(ui);
  const page = twPage(rows, ui, TW_LIST_ROWS);
  const cells = page.rows.map((r) => {
    const sub = [r.entrance ? r.name : '', t('tweaks.domains.level', { n: r.level }), r.off ? t('tweaks.domains.closed') : ''].filter(Boolean).join(' · ');
    return `<button class="twrow${r.id === ui.open ? ' on' : ''}${r.off ? ' off' : ''}" data-act="tweaksDomainOpen" data-arg="${esc(r.id)}" title="${esc((r.entrance ? r.entrance + ' — ' : '') + r.name)}">
      ${iconTile(r.icon ? twEntry('items', r.icon).icon : '', r.entrance || r.name, '')}<span class="tx"><b>${esc(r.entrance || r.name)}</b><small>${esc(sub)}</small></span>
      <span class="tags" data-tw-live="domains.tags:${esc(r.id)}">${twDomainTags(v, r.id)}</span></button>`;
  });
  const at = page.rows.findIndex((r) => r.id === ui.open);
  if (at >= 0) cells.splice(Math.min(cells.length, (at | 1) + 1), 0, twDomainEditor(v, page.rows[at]));
  return `<div class="twlist">${cells.join('')}</div>${twPager('domains', rows.length, page)}`;
}
// Puts a domain on screen with its editor open: the whole list, on the page that holds it.
function twDomainGo(v, id) {
  const at = twDomainRows(v).findIndex((r) => r.id === String(id));
  if (at >= 0) Object.assign(twListUi('domains', v), { kind: '', q: '', page: Math.floor(at / TW_LIST_ROWS), open: String(id) });
}
// The chips of the cost of every domain: the original and the presets, or the presets alone on a
// server whose domains do not all cost the same.
function twDomainCostChips(v) {
  const cost = twDomainsCost(v);
  if (cost !== undefined) return twPresetChips('domains.cost', cost, TW_DOMAIN_COSTS, v, twBaseLabel(twTakenTable(v, 'costs')), true);
  const cur = twInt(twGet('domains.cost', v));
  return TW_DOMAIN_COSTS.map((n) => `<button class="elchip${cur === n ? ' on' : ''}" data-act="tweaksPreset" data-arg="domains.cost=${n}">${n}</button>`).join('');
}
// The card's last line: how many domains have settings of their own and how many enemies are changed,
// and the way back for the section.
function twDomainsFoot(v) {
  const n = Object.keys((twDraftCanon(v).canon.domains || {}).byId || {}).length, st = twLineupStates(v);
  const foes = Object.keys(st).reduce((sum, gid) => sum + st[gid].slots, 0);
  const said = (n === 0 ? (foes ? [] : [T('tweaks.domains.count.none')]) : [n === 1 ? T('tweaks.domains.count.own1') : T('tweaks.domains.count.own', { n })])
    .concat(!foes ? [] : [foes === 1 ? T('tweaks.domains.count.enemy1') : T('tweaks.domains.count.enemies', { n: foes })]);
  return `<span>${said.join(' ')}</span>${twResetBtn('domains', v)}`;
}
// The card words itself by what the answer lets it do. With line-ups listed it speaks of a domain's
// enemies too — and of their level, when the answer gives a domain a range for it (twKnowsLevels) —;
// with none — the catalogue has none for this version — it speaks of the rewards alone. What waits for
// an update of the agent is said by one note, and only that: an answer without the line-ups' key
// (twKnowsLineups) lets the page change neither a domain's enemies nor their level; one that lists
// line-ups and names no limit for the level (twDomainLevelLimit) lets it change the enemies and not
// their level. An answer that names the limit and gives no domain a range is its catalogue's or its
// stack's doing — no line-up there says how far a domain can be raised, or none of their scripts is one
// the range can rest on —, which no update changes: the card speaks of the enemies alone and has no note.
function tweaksDomainsCard(v) {
  const off = twSectionOff('domains', v), d = twDataOf(v);
  if (off || !d.domains || typeof d.domains !== 'object') return twCard('domains', T('tweaks.domains.title'), v, off);
  const ui = twListUi('domains', v), cost = twDomainsCost(v), enemies = Object.keys(twLineups(d)).length > 0, levels = twKnowsLevels(d);
  const hint = !enemies ? T('tweaks.domains.hint') : levels ? T('tweaks.domains.hintEnemies') : T('tweaks.domains.hintNoLevel');
  const oneHint = !enemies ? T('tweaks.domains.oneHint') : levels ? T('tweaks.domains.oneHintEnemies') : T('tweaks.domains.oneHintNoLevel');
  const waits = !twKnowsLineups(d) ? T('tweaks.domains.enemiesNeedAgent') : enemies && !levels && !twDomainLevelLimit(d) ? T('tweaks.domains.enemyLevelNeedsAgent') : '';
  return twCard('domains', T('tweaks.domains.title'), v, `<p class="hint">${hint}</p>
    <div class="line"><div style="min-width:0"><b>${T('tweaks.domains.drops')}</b><small>${T('tweaks.domains.dropsSub')}</small></div>
      <div class="twctl"><div class="chips">${twMultChips('domains.multiplier', v)}</div></div></div>
    <div class="line"><div style="min-width:0"><b>${T('tweaks.domains.cost')}</b><small>${T('tweaks.domains.costSub')}</small></div>
      <div class="twctl"><div class="chips" data-tw-live="domains.cost.chips">${twDomainCostChips(v)}</div>
        <input class="txt twnum${twFieldCls('domains.cost', v)}" data-tw="domains.cost" value="${esc(twShown('domains.cost', cost === undefined ? '' : cost, v))}" placeholder="${esc(cost === undefined ? '' : cost)}" inputmode="numeric" maxlength="9" autocomplete="off" spellcheck="false"/></div></div>
    <h5 class="twsub">${T('tweaks.domains.oneTitle')}</h5>
    <p class="hint">${oneHint}</p>${waits ? `<div class="note">${waits}</div>` : ''}
    <div class="twfind"><input class="txt" data-tw-find="domains" value="${esc(ui.q)}" placeholder="${T('tweaks.domains.search')}" autocomplete="off" spellcheck="false"/>
      <div class="twab-chips" data-tw-live="domains.kinds">${twDomainKinds(v)}</div></div>
    <div data-tw-list="domains">${twDomainsList(v)}</div>
    <div class="twab-foot" data-tw-live="domains.foot">${twDomainsFoot(v)}</div>`);
}

// ── gameplay: quest rewards ──
// What quests give. The answer's `quests.rewards` lists the reward rows quests hand out, {reward id:
// {kind: 'archon' | 'story' | 'world' | 'event' | 'commission', quests: [quest ids], edit: 1 = the row
// is one quest's own, items: [[item id, count]] the original reward}}, and `quests.keep` the items a
// multiplier leaves at their count (a character, a constellation item: more of one is not more reward).
// The card edits the draft's `quests` section: 'quests.multiplier.<kind>' multiplies every reward of a
// kind (the server caps an amount at the item's cap), and 'quests.byReward.<reward id>.items' = [{item,
// count}] is the complete reward of one row — exact: no multiplier touches it. A row is listed once per
// quest that hands it out (a quest may hand out several, a row may be shared by several quests and then
// cannot be replaced), TW_LIST_ROWS of them on screen; the open one is edited in place.
// The six amounts a reward row keeps in cells of its own — Primogems, Mora, Adventure EXP, Character
// EXP, Companionship EXP, resin — as the items the server turns them into, and how many other items a
// row has slots for.
const TW_REWARD_VIRTUAL = [201, 202, 102, 101, 105, 106];
const TW_REWARD_SLOTS = 9;
// The names of a quest kind: its quests ("Archon quests"), one of them ("Archon quest"), its chip.
const twQuestKindName = (kind) => ({ archon: t('tweaks.quests.kind.archon'), story: t('tweaks.quests.kind.story'), world: t('tweaks.quests.kind.world'),
  event: t('tweaks.quests.kind.event'), commission: t('tweaks.quests.kind.commission'), hangout: t('tweaks.quests.kind.hangout') }[kind] || String(kind));
const twQuestKindOne = (kind) => ({ archon: t('tweaks.quests.one.archon'), story: t('tweaks.quests.one.story'), world: t('tweaks.quests.one.world'),
  event: t('tweaks.quests.one.event'), commission: t('tweaks.quests.one.commission'), hangout: t('tweaks.quests.one.hangout') }[kind] || String(kind));
const twQuestKindChip = (kind) => ({ archon: t('tweaks.quests.chip.archon'), story: t('tweaks.quests.chip.story'), world: t('tweaks.quests.chip.world'),
  event: t('tweaks.quests.chip.event'), commission: t('tweaks.quests.chip.commission'), hangout: t('tweaks.quests.chip.hangout') }[kind] || String(kind));
// The rewards of an answer as the list shows them, built once per answer and display catalogue: one
// row per quest and reward row it hands out, {key: "<quest id>:<reward id>", quest, rid, name: the
// quest's ("#<id>" when the catalogue does not name it), kind, shared: the row is not one quest's own,
// nth / of: which of the quest's rows, tier: for the reward of a hangout event, how many of its endings it
// asks for (0 = none said), s: the search key}. A hangout event's rewards are rows of its chapter (the id
// they name), in the order of their tiers. The catalogue's order, then the quests it does not name.
const TW_QUEST_INDEX = new WeakMap();
function twQuestRows(v) {
  const d = twDataOf(v), names = S.twData.list.quests, m = d ? TW_QUEST_INDEX.get(d) : null;
  if (m && m.names === names) return m.rows;
  const rewards = (d && d.quests && d.quests.rewards) || {}, of = {}, rows = [];
  Object.keys(rewards).forEach((rid) => (Array.isArray(rewards[rid].quests) ? rewards[rid].quests : []).forEach((q) => { (of[q] = of[q] || []).push(rid); }));
  const add = (q, e) => of[q].forEach((rid, i) => rows.push({ key: q + ':' + rid, quest: num(q), rid, name: e.name, kind: String(rewards[rid].kind || ''), shared: rewards[rid].edit !== 1,
    nth: i + 1, of: of[q].length, tier: Number.isInteger(rewards[rid].tier) && rewards[rid].tier > 0 ? rewards[rid].tier : 0, s: searchKey(e.unknown ? '' : e.name, q, rid) }));
  const named = {};
  (names || []).forEach((e) => { if (hasOwn(of, e.id) && !named[e.id]) { named[e.id] = true; add(e.id, e); } });
  Object.keys(of).filter((q) => !named[q]).forEach((q) => add(q, twEntry('quests', q)));
  if (d) TW_QUEST_INDEX.set(d, { names, rows });
  return rows;
}
// Which of its quest's rewards a row is, in its sub line: the endings a hangout event's reward asks for, else
// "reward n of m" for a quest with several ('' for one with a single reward).
const twQuestNth = (r) => (r.tier > 0 ? (r.tier === 1 ? t('tweaks.quests.tier1') : t('tweaks.quests.tier', { n: r.tier }))
  : r.of > 1 ? t('tweaks.quests.nth', { n: r.nth, of: r.of }) : '');
// The multiplier the draft holds for a quest kind (1 = none).
function twQuestMult(v, kind) {
  const x = twInt(twGet(['quests', 'multiplier', kind], v));
  return x >= 1 ? x : 1;
}
// The items of an answer a multiplier leaves at their count (`quests.keep`), as a set, built once per
// answer.
const TW_KEEP = new WeakMap();
function twKeep(d) {
  let keep = TW_KEEP.get(d);
  if (!keep) {
    keep = new Set(Array.isArray(d.quests && d.quests.keep) ? d.quests.keep : []);
    TW_KEEP.set(d, keep);
  }
  return keep;
}
// What a reward row hands out while the draft has no reward of its own for it, [{item, count}]: the
// original amounts — what the row holds in the file's base, the cells of one item added up — times the
// multiplier of the row's kind, an item never above its cap (nor below its original amount), and an item
// of `quests.keep` at its original amount whatever the multiplier: the agent's own rule for a multiplied
// row. A row kept as it was changed outside Relic (`kept`) is touched by no multiplier.
function twQuestGiven(v, rid) {
  const d = twDataOf(v), row = ((d.quests || {}).rewards || {})[rid] || {}, mult = row.kept === true ? 1 : twQuestMult(v, row.kind), keep = twKeep(d), total = {}, order = [];
  (Array.isArray(row.items) ? row.items : []).forEach(([item, n]) => { if (!hasOwn(total, item)) { total[item] = 0; order.push(item); } total[item] += n; });
  return order.map((item) => {
    const cap = twItemCap(v, item);
    return { item, count: keep.has(item) ? total[item] : Math.min(total[item] * mult, cap ? Math.max(cap, total[item]) : total[item]) };
  });
}
// The reward of a row as its editor lists it: the draft's own list for it, as typed, else what the row
// hands out anyway.
function twQuestItems(v, rid) {
  const own = twGet(['quests', 'byReward', rid, 'items'], v);
  return Array.isArray(own) ? own : twQuestGiven(v, rid);
}
// Editing a reward: the draft takes the list the editor shows before the first change to it (twQuestSeed)
// and lets it go again when the list is back at what the row hands out anyway (twQuestTidy) — a list
// left in the draft would turn into a reward of its own the moment the multiplier of its kind changes.
// A field and the × of a row name an item by its place in the list, and a list let go while its fields
// are typed in leaves its rows on screen: the next list starts in THEIR order (twQuestEditor notes it),
// which may be another one than the row's own (an item taken out and picked again).
function twQuestSeed(v, rid) {
  if (Array.isArray(twGet(['quests', 'byReward', rid, 'items'], v))) return;
  const given = twQuestGiven(v, rid), drawn = twListUi('quests', v).drawn;
  const order = drawn && drawn.rid === String(rid) && drawn.items.length === given.length && given.every((e) => drawn.items.includes(e.item)) ? drawn.items : null;
  twSet(['quests', 'byReward', rid, 'items'], order ? order.map((item) => given.find((e) => e.item === item)) : given, v);
}
function twQuestTidy(v, rid) {
  const own = twGet(['quests', 'byReward', rid, 'items'], v), given = twQuestGiven(v, rid);
  if (Array.isArray(own) && own.length === given.length && twSameReward(twItems(own), given.map((e) => [e.item, e.count]))) twDel(['quests', 'byReward', rid], v);
}
// The reward rows with a reward of their own in the canonical draft or in the stored document.
function twQuestOwnIds(v) {
  const now = (twDraftCanon(v).canon.quests || {}).byReward || {}, was = (twStoredCanon(v).canon.quests || {}).byReward || {};
  return Object.keys(now).concat(Object.keys(was).filter((rid) => !hasOwn(now, rid)));
}
// The kind chips of the list: the kinds the answer has reward rows of (and the one picked, whatever the answer
// has), and "Own reward" while a row has one.
function twQuestKinds(v) {
  const own = twQuestOwnIds(v), kind = twListKind('quests', v, own), has = twRewardKinds(twDataOf(v) || {});
  const chip = (id, label) => `<button class="elchip${kind === id ? ' on' : ''}" data-act="tweaksListKind" data-arg="quests:${id}">${label}</button>`;
  return chip('', T('common.all')) + TW_QUEST_KINDS.filter((k) => has[k] || kind === k).map((k) => chip(k, esc(twQuestKindChip(k)))).join('')
    + (own.length ? chip('own', T('tweaks.quests.chip.own', { n: own.length })) : '');
}
// The marks of a row: a shared row, a row kept as it was changed outside Relic, a reward of its own (and the
// page's dot while that is not applied).
function twQuestTags(v, rid) {
  const now = ((twDraftCanon(v).canon.quests || {}).byReward || {})[rid], was = ((twStoredCanon(v).canon.quests || {}).byReward || {})[rid];
  const row = ((twDataOf(v).quests || {}).rewards || {})[rid] || {};
  return (row.edit !== 1 ? `<span class="twtag kind">${T('tweaks.quests.tag.shared')}</span>` : '')
    + (row.kept === true ? `<span class="twtag kind">${T('tweaks.quests.tag.kept')}</span>` : '') + (now ? `<span class="twtag">${T('tweaks.quests.tag.own')}</span>` : '')
    + (JSON.stringify(now || null) !== JSON.stringify(was || null) ? `<i class="twdot" title="${T('tweaks.chip.unapplied')}"></i>` : '');
}
// The line of the open reward that says what it is: a reward of its own, the original one (times the
// multiplier of its kind), or a row several quests share. A row of a reward table taken over is the one in
// the file: changed outside Relic (it has the original game's reward beside it: `game`), or kept as it was
// changed while a multiplier applied to it (`kept`: no multiplier touches it).
function twQuestState(v, rid) {
  const row = twDataOf(v).quests.rewards[rid], mult = twQuestMult(v, row.kind), kind = twQuestKindName(row.kind);
  const quests = Array.from(new Set(row.quests.map((q) => twName('quests', q)))).join(', ');
  // (what the original game's row hands out, said beside a row the hand changed: its items with their amounts)
  const file = Array.isArray(row.game);
  const game = file ? ' ' + T('tweaks.quests.state.game', { items: row.game.filter(Array.isArray)
    .map(([item, n]) => t('tweaks.quests.state.gameItem', { item: twName('items', item), n: twAmount(n) })).join(', ') || '—' }) : '';
  if (row.edit !== 1) return row.kept === true ? T('tweaks.quests.state.keptShared', { quests }) : T('tweaks.quests.state.shared', { quests }) + game;
  if (twIsSet(['quests', 'byReward', rid], v)) return T('tweaks.quests.state.own');
  if (row.kept === true) return T('tweaks.quests.state.kept');
  return (mult === 1 ? (file ? T('tweaks.quests.state.inFile') : T('tweaks.quests.state.original'))
    : file ? T('tweaks.quests.state.inFileMultiplied', { n: mult, kind }) : T('tweaks.quests.state.multiplied', { n: mult, kind })) + game;
}
const twQuestRestoreButton = (v, rid) => `<button class="btn ghost" data-act="tweaksQuestRestore" data-arg="${esc(rid)}" ${twGet(['quests', 'byReward', rid], v) === undefined ? 'disabled' : ''}>${T('tweaks.quests.restore')}</button>`;
// The open reward, under its row: its items with an amount each — fields for a row that is one quest's
// own, plain amounts for a shared one. The order of its rows is noted for twQuestSeed.
function twQuestEditor(v, r) {
  const rid = r.rid, list = twQuestItems(v, rid), path = `quests.byReward.${rid}.items`;
  twListUi('quests', v).drawn = { rid, items: list.map((e) => twNum(e && e.item)) };
  const others = list.filter((e) => !TW_REWARD_VIRTUAL.includes(twNum(e && e.item))).length, full = list.length >= TW_REWARD_VIRTUAL.length + TW_REWARD_SLOTS;
  const items = list.map((e, i) => twItemRow(v, r.shared ? '' : `${path}.${i}.count`, twNum(e && e.item), e ? e.count : undefined, list.length > 1 ? ['tweaksQuestRemove', rid + ':' + i] : null)).join('');
  const sub = [twQuestKindOne(r.kind), t('cmd.idBadge', { id: r.quest }), twQuestNth(r), t('tweaks.quests.row', { id: rid })].filter(Boolean).join(' · ');
  return `<div class="twed">
    <div class="twed-head"><div class="twed-name"><b>${esc(r.name)}</b><small>${esc(sub)}</small></div>
      ${r.shared ? '' : `<span data-tw-live="quests.restore:${esc(rid)}">${twQuestRestoreButton(v, rid)}</span>`}
      <button class="twed-x" data-act="tweaksQuestOpen" data-arg="${esc(r.key)}" title="${T('common.close')}">×</button></div>
    <p class="twed-say" data-tw-live="quests.state:${esc(rid)}">${twQuestState(v, rid)}</p>
    ${r.shared ? '' : `<div class="twed-row"><div class="twed-lbl"><b>${T('tweaks.quests.reward')}</b><small>${T('tweaks.quests.room', { max: TW_REWARD_SLOTS })}</small></div>
      <div class="twed-add"><span>${T('tweaks.item.count', { n: others, max: TW_REWARD_SLOTS })}</span>
        <button class="btn ghost" data-act="tweaksQuestAdd" data-arg="${esc(rid)}" ${full ? 'disabled' : ''}>${T('tweaks.item.add')}</button></div></div>`}
    <div class="twits">${items}</div>${r.shared ? '' : twResinNote(v, list)}${twCharacterNote(v, list)}
  </div>`;
}
function twQuestShown(v) {
  const q = twListUi('quests', v).q.trim().toLowerCase(), own = twQuestOwnIds(v), kind = twListKind('quests', v, own);
  return twQuestRows(v).filter((r) => (kind === 'own' ? own.includes(r.rid) : !kind || r.kind === kind) && (!q || r.s.includes(q)));
}
function twQuestsList(v) {
  const ui = twListUi('quests', v), rows = twQuestShown(v);
  if (!rows.length) return twListNone(ui);
  const page = twPage(rows, ui, TW_LIST_ROWS);
  const cells = page.rows.map((r) => {
    const sub = [twQuestKindOne(r.kind), t('cmd.idBadge', { id: r.quest }), twQuestNth(r)].filter(Boolean).join(' · ');
    return `<button class="twrow${r.key === ui.open ? ' on' : ''}" data-act="tweaksQuestOpen" data-arg="${esc(r.key)}" title="${esc(r.name)}">
      <span class="tx"><b>${esc(r.name)}</b><small>${esc(sub)}</small></span>
      <span class="tags" data-tw-live="quests.tags:${esc(r.rid)}">${twQuestTags(v, r.rid)}</span></button>`;
  });
  const at = page.rows.findIndex((r) => r.key === ui.open);
  if (at >= 0) cells.splice(Math.min(cells.length, (at | 1) + 1), 0, twQuestEditor(v, page.rows[at]));
  return `<div class="twlist">${cells.join('')}</div>${twPager('quests', rows.length, page)}`;
}
// Puts a reward row on screen with its editor open: the whole list, on the page that holds it.
function twQuestGo(v, rid) {
  const rows = twQuestRows(v), at = rows.findIndex((r) => r.rid === String(rid));
  if (at >= 0) Object.assign(twListUi('quests', v), { kind: '', q: '', page: Math.floor(at / TW_LIST_ROWS), open: rows[at].key });
}
function twQuestsFoot(v) {
  const n = Object.keys((twDraftCanon(v).canon.quests || {}).byReward || {}).length;
  return `<span>${n === 0 ? T('tweaks.quests.count.none') : n === 1 ? T('tweaks.quests.count.own1') : T('tweaks.quests.count.own', { n })}</span>${twResetBtn('quests', v)}`;
}
function tweaksQuestsCard(v) {
  const off = twSectionOff('quests', v), d = twDataOf(v), rewards = d.quests && d.quests.rewards;
  if (off || !rewards || typeof rewards !== 'object') return twCard('quests', T('tweaks.quests.title'), v, off);
  const ui = twListUi('quests', v), rows = {};
  Object.keys(rewards).forEach((rid) => { const k = rewards[rid].kind; rows[k] = (rows[k] || 0) + 1; });
  const line = (kind) => `<div class="line twkind"><div style="min-width:0"><b>${esc(twQuestKindName(kind))}</b><small>${
    kind === 'commission' ? T('tweaks.quests.rewardsDaily', { n: rows[kind] || 0 }) : T('tweaks.quests.rewards', { n: rows[kind] || 0 })}</small></div>
      <div class="twctl"><div class="chips">${twMultChips('quests.multiplier.' + kind, v)}</div></div></div>`;
  // (said of a server that names such items: one that does not multiplies them like any other)
  // One multiplier line per kind the answer has reward rows of — an agent whose answer lists no reward of a kind
  // refuses a multiplier for it —, and one for a kind whose multiplier the draft holds: the way to take it out.
  return twCard('quests', T('tweaks.quests.title'), v, `<p class="hint">${T('tweaks.quests.hint')}${twKeep(d).size ? ' ' + T('tweaks.quests.keepHint') : ''}</p>
    ${TW_QUEST_KINDS.filter((kind) => rows[kind] > 0 || twQuestMult(v, kind) !== 1).map(line).join('')}
    <h5 class="twsub">${T('tweaks.quests.oneTitle')}</h5>
    <p class="hint">${T('tweaks.quests.oneHint')}</p>
    <div class="twfind"><input class="txt" data-tw-find="quests" value="${esc(ui.q)}" placeholder="${T('tweaks.quests.search')}" autocomplete="off" spellcheck="false"/>
      <div class="twab-chips" data-tw-live="quests.kinds">${twQuestKinds(v)}</div></div>
    <div data-tw-list="quests">${twQuestsList(v)}</div>
    <div class="twab-foot" data-tw-live="quests.foot">${twQuestsFoot(v)}</div>`);
}

// ── gameplay: the item picker ──
// The modal picker over the items a reward may hold: the display catalogue's items that the answer's
// `items` lists ({id: [the largest amount the server takes, 1 = it may be added to a domain's reward]})
// — never the other way round: the server lists thousands the catalogue cannot name. S.picker of this
// kind adds: v = the version, use = 'domain' | 'quest', id = the dungeon / the reward row the item is
// for, ig = the group chip ('' = all), r = the rarity chip. The catalogue's groups are the chips; its
// `other` group (food, furnishings, quest items — thousands, mostly without a sprite) is listed only
// under a search text. The list is rebuilt per keystroke (live) and never drawn whole: TW_ITEM_ROWS
// rows at most, TW_ITEM_PEEK of each group while every group is on show.
const TW_ITEM_GROUPS = ['currency', 'wish', 'character', 'constellation', 'exp', 'ascension', 'talent', 'weaponMaterial', 'specialty', 'resource', 'consumable', 'weapon', 'artifact'];
const TW_ITEM_ROWS = 120;
const TW_ITEM_PEEK = 6;
const twItemGroupName = (group) => ({ currency: t('tweaks.pick.item.group.currency'), wish: t('tweaks.pick.item.group.wish'),
  character: t('tweaks.pick.item.group.character'), constellation: t('tweaks.pick.item.group.constellation'), exp: t('tweaks.pick.item.group.exp'),
  ascension: t('tweaks.pick.item.group.ascension'), talent: t('tweaks.pick.item.group.talent'), weaponMaterial: t('tweaks.pick.item.group.weaponMaterial'),
  specialty: t('tweaks.pick.item.group.specialty'), resource: t('tweaks.pick.item.group.resource'), consumable: t('tweaks.pick.item.group.consumable'),
  weapon: t('tweaks.pick.item.group.weapon'), artifact: t('tweaks.pick.item.group.artifact') }[group] || t('tweaks.pick.item.group.other'));
// The items a version's rewards may hold, built once per answer and display catalogue: {id, name,
// group, rarity, icon, cap, drop, s}, group by group in the chips' order (`other` last), the
// catalogue's order inside a group.
const TW_ITEM_POOL = new WeakMap();
function twItemPool(v) {
  const d = twDataOf(v), names = S.twData.list.items, m = d ? TW_ITEM_POOL.get(d) : null;
  if (m && m.names === names) return m.rows;
  const held = d && d.items && typeof d.items === 'object' ? d.items : {}, keep = d ? twKeep(d) : new Set(), by = {};
  (names || []).forEach((e) => {
    const x = held[e.id];
    if (!Array.isArray(x) || !(x[0] >= 1)) return;
    // The Constellations chip lists only what the version's constellations consume — the agent's keep
    // list, built from TalentSkillData per version, names exactly those: a constellation item it lacks
    // (a Traveler memory of an element that version's Traveler cannot take, the Traveler's own Stella
    // Fortuna) is filed under `other`, found by search only.
    const group = !TW_ITEM_GROUPS.includes(e.group) ? 'other' : e.group === 'constellation' && !keep.has(e.id) ? 'other' : e.group;
    (by[group] = by[group] || []).push({ id: e.id, name: e.name, group, rarity: num(e.rarity), icon: e.icon || '', cap: x[0], drop: x[1] === 1, s: e.s });
  });
  const rows = TW_ITEM_GROUPS.concat('other').reduce((all, g) => all.concat(by[g] || []), []);
  if (d) TW_ITEM_POOL.set(d, { names, rows });
  return rows;
}
// The items the list a pick is for cannot take: the ones it holds, and for a domain the ones its reward
// row holds itself (`own`: the server refuses exactly those as an extra item — the multiplier is for
// them). An answer that does not name them gets the wider net of the preview, which also lists what the
// row's sub-rows drop.
function twItemTaken(p) {
  if (p.use !== 'domain') return twQuestItems(p.v, p.id).map((e) => twNum(e && e.item));
  const a = (twDataOf(p.v).domains || {})[p.id] || {}, extra = twGet(['domains', 'byId', p.id, 'extra'], p.v);
  const held = Array.isArray(a.own) ? a.own : (Array.isArray(a.preview) ? a.preview : []).map((x) => x[0]);
  return (Array.isArray(extra) ? extra : []).map((e) => twNum(e && e.item)).concat(held);
}
// A reward that already holds as many items as a row has slots for takes only currencies and EXP.
const twItemScalarsOnly = (p) => p.use === 'quest' && twQuestItems(p.v, p.id).filter((e) => !TW_REWARD_VIRTUAL.includes(twNum(e && e.item))).length >= TW_REWARD_SLOTS;
function twItemRows(p) {
  const q = p.q.trim().toLowerCase(), taken = twItemTaken(p), scalars = twItemScalarsOnly(p);
  const fits = (r) => !taken.includes(r.id) && (p.use !== 'domain' || r.drop) && (!scalars || TW_REWARD_VIRTUAL.includes(r.id));
  const rows = twItemPool(p.v).filter((r) => fits(r) && (!p.r || r.rarity === p.r) && (p.ig ? r.group === p.ig : !!q || r.group !== 'other') && (!q || r.s.includes(q)));
  // An id typed in full that the display catalogue does not name: the server knows the item.
  const x = /^\d+$/.test(q) ? (twDataOf(p.v).items || {})[q] : null, id = num(q);
  if (Array.isArray(x) && x[0] >= 1 && !p.ig && !p.r && twEntry('items', id).unknown) {
    const r = { id, name: '#' + id, group: 'other', rarity: 0, icon: '', cap: x[0], drop: x[1] === 1, s: q };
    if (fits(r)) rows.push(r);
  }
  return rows;
}
function twItemList(rows, hidden, p) {
  const peek = !p.ig && !p.q.trim(), by = {}, groups = [];
  rows.forEach((r) => { if (!by[r.group]) { by[r.group] = []; groups.push(r.group); } by[r.group].push(r); });
  let left = TW_ITEM_ROWS;
  const html = groups.map((g) => {
    const shown = by[g].slice(0, peek ? TW_ITEM_PEEK : left);
    if (!peek) left -= shown.length;
    if (!shown.length) return '';
    const items = shown.map((r) => `<button class="wrow twpick" data-act="pickItem" data-arg="${r.id}" title="${esc(r.name)}">
        ${iconTile(r.icon, r.name, '')}<span class="tx"><b>${esc(r.name)}</b><small>${T('cmd.idBadge', { id: r.id })} · ${T('tweaks.item.upTo', { n: twAmount(r.cap) })}</small></span>
        <span class="meta">${r.rarity ? stars(r.rarity) : ''}</span></button>`).join('');
    const all = peek && by[g].length > shown.length ? `<button class="twab-all" data-act="setPickerItemGroup" data-arg="${g}">${T('tweaks.pick.item.showAll', { n: by[g].length })}</button>` : '';
    return `<div class="wgroup"><h5 class="twgh"><span>${esc(twItemGroupName(g))}</span>${all}</h5><div class="wgrid twgrid">${items}</div></div>`;
  }).join('');
  const more = peek ? 0 : rows.length - (TW_ITEM_ROWS - left);
  return html + (more > 0 ? `<div class="pickmore">${T('tweaks.pick.item.more', { n: twAmount(more) })}</div>` : '');
}
// The rarity chips beside the search field, the group chips on a row of their own.
function twItemChips(p) {
  const chip = (act, value, on, label) => `<button class="elchip${on ? ' on' : ''}" data-act="${act}" data-arg="${esc(value)}">${label}</button>`;
  return `<div class="chips">${chip('setPickerRar', '', !p.r, T('common.all'))}${[5, 4, 3, 2, 1].map((n) => chip('setPickerRar', n, p.r === n, n + '★')).join('')}</div>
    <div class="chips pickgroups">${chip('setPickerItemGroup', '', !p.ig, T('common.all'))}${TW_ITEM_GROUPS.map((g) => chip('setPickerItemGroup', g, p.ig === g, esc(twItemGroupName(g)))).join('')}</div>`;
}
// The line under the picker's title: the domain or the quest the item is for.
function twItemWhere(p) {
  if (p.use === 'domain') return T('tweaks.pick.item.forDomain', { domain: twDomainName(p.v, p.id) });
  const row = twQuestRows(p.v).find((r) => r.rid === String(p.id));
  return T('tweaks.pick.item.forQuest', { quest: row ? row.name : '#' + p.id });
}
function twItemNote(p) {
  return twItemScalarsOnly(p) ? `<div class="note">${T('tweaks.pick.item.scalars', { max: TW_REWARD_SLOTS })}</div>` : '';
}

// ── settings ──
// The theme card is first in Settings; click = apply instantly + save to localStorage.
function themeSect() {
  const chk = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="m5 12 4 4 8-9"/></svg>';
  const card = (id, name, desc, prev) => `<button class="themecard" data-act="setTheme" data-arg="${id}">
      ${S.theme === id ? `<span class="ring"></span><span class="tick">${chk}</span>` : ''}
      <div class="prev ${id}">${prev}</div>
      <div class="body"><b>${name}</b><small>${desc}</small></div>
    </button>`;
  // The previews show their own theme regardless of the active one, so the colours are fixed here.
  const classicPrev = '<svg width="30" height="30" viewBox="0 0 24 24" fill="none" stroke="#c8a04f" stroke-width="1.4"><path d="M12 2 22 12 12 22 2 12z"/><path d="M12 6.5 17.5 12 12 17.5 6.5 12z" fill="#c8a04f" fill-opacity=".25"/></svg>';
  return `<div class="sect">
    <div class="flex between center" style="margin-bottom:4px"><h4 style="margin:0">${T('settings.theme.title')}</h4><span style="color:var(--muted);font-size:11px;font-weight:800">${T('settings.theme.autosave')}</span></div>
    <p class="hint" style="margin:6px 0 14px">${T('settings.theme.hint')}</p>
    <div class="grid2" style="gap:12px">
      ${card('classic', T('settings.theme.classic'), T('settings.theme.classicDesc'), classicPrev)}
      ${card('summer', T('settings.theme.summer'), T('settings.theme.summerDesc'), '<span class="minibuoy"></span>')}
    </div>
  </div>`;
}

// Language row: only drawn when more than one language ships (lang/index.json). The <select> is
// handled by the 'change' listener (data-lang), not by data-model: the switch saves the setting and
// reloads the dictionary before re-rendering.
function languageLine() {
  const langs = I18N.available();
  if (langs.length < 2) return '';
  const cur = (S.settings.language || 'en').toLowerCase();
  const opts = langs.map((l) => `<option value="${esc(l.code)}"${l.code === cur ? ' selected' : ''}>${esc(l.name)}</option>`).join('');
  return `<div class="line"><div><b>${T('settings.app.language')}</b><small>${T('settings.app.languageDesc')}</small></div>
    <select class="txt" style="width:180px" data-lang>${opts}</select></div>`;
}

// Server Admin mode: the server address outside the start screen is shown, never edited — the server you
// administer is chosen there, with its token, or by an agent install (Change mode or server leads back to it).
const addressReadOnly = () => `<div class="roaddr pick">${esc(addrText() || '—')}</div>
  <div class="flex between center" style="gap:12px;margin-top:10px"><p class="hint" style="margin:0">${T('settings.server.adminHint')}</p>
    <button class="btn ghost" style="padding:8px 14px;font-size:12px;flex:none" data-act="changeMode">${T('common.changeMode')}</button></div>`;
function settings() {
  const s = S.settings;
  return `<div class="pad" style="max-width:820px">
    <h1 class="title">${T('settings.title')}</h1><p class="sub">${T('settings.sub')}</p>

    ${themeSect()}

    <div class="sect"><h4>${T('settings.server.title')}</h4>
      <label class="fld" style="margin-top:0">${T('settings.server.addressPort')}</label>
      ${isAdminMode() ? addressReadOnly() : `<input class="txt mono" data-model="addrEdit" value="${esc(addrShown())}" placeholder="${T('login.serverPlaceholder')}" spellcheck="false" autocomplete="off"/>
      <p class="hint" style="margin:10px 0 0">${T('settings.server.addressHint')}</p>`}
    </div>

    <div class="sect"><h4>${T('settings.source.title')}</h4>
      <div class="grid2">${[...(!S.versions.length || S.versions.some(hasCdn) ? [{ id: 'cdn', label: t('wizard.source.cdn') }] : []), ...(S.versions.some((v) => v.hasDrive) ? [{ id: 'drive', label: 'Google Drive' }] : []), ...allMirrors()]
        .map((o) => `<div class="opt ${s.source === o.id ? 'sel' : ''}" data-act="setDefaultSource" data-arg="${esc(o.id)}"><div class="h">${esc(o.label)}</div></div>`).join('')}</div>
      <p class="hint" style="margin:10px 0 0">${T('settings.source.hint')}</p>
    </div>

    <div class="sect"><h4>${T('settings.install.title')}</h4>
      <div class="line"><div><b>${T('settings.install.gentle')}</b><small>${T('settings.install.gentleDesc')}</small></div>${toggle('sgentle', s.gentleExtract !== false)}</div>
      <label class="fld">${T('settings.install.workers')}</label>
      <div class="flex gap center"><input class="txt mono" style="width:90px" data-model="settings.extractWorkers" value="${s.extractWorkers || 0}"/>
        <small style="color:var(--inkSoft);font-size:11.5px;font-weight:600;line-height:1.45">${T('settings.install.workersDesc')}</small></div>
    </div>

    ${S.versions.some((v) => v.hasEnhancements) ? `<div class="sect"><h4>${T('settings.game.title')}</h4>
      <div class="line"><div><b>${T('settings.game.enhancements')}</b><small>${T('settings.game.enhancementsDesc', { versions: S.versions.filter((v) => v.hasEnhancements).map((v) => v.id).join(', ') })}</small></div>${toggle('senh', s.enhancements !== false)}</div>
    </div>` : ''}

    ${gameFilesSect()}

    <div class="sect"><div class="flex between center" style="margin-bottom:8px"><h4 style="margin:0">Fiddler Classic</h4>
      <span style="color:${S.fiddler.installed ? '#4d8a51' : '#b57b2a'};font-size:11.5px;font-weight:800">${S.fiddler.installed ? T('settings.fiddler.installed') : T('settings.fiddler.notInstalled')}</span></div>
      <div class="line"><b>${T('common.decryptHttps')}</b>${toggle('sdecrypt', s.fiddlerDecrypt)}</div>
      <div class="line"><div><b>${T('settings.fiddler.noUac')}</b><small>${S.fiddler.isAdmin
        ? T('settings.fiddler.noUacAdmin')
        : T('settings.fiddler.noUacStandard')} ${T('settings.fiddler.noUacFallback')}</small></div>${toggle('snouac', s.fiddlerNoUac !== false)}</div>
      <div class="line"><div><b>${T('settings.fiddler.cert')}</b><small>${S.fiddler.certTrusted ? T('settings.fiddler.installed') : T('settings.fiddler.certNeeds')}</small></div>
        ${S.fiddler.certTrusted ? `<span style="color:#4d8a51;font-weight:800;font-size:12px">${I.check} ${T('common.yes')}</span>` : `<button class="btn" style="padding:8px 14px;font-size:12px" data-act="trustCert">${T('settings.fiddler.approveCert')}</button>`}</div>
    </div>

    <div class="sect"><h4>${T('settings.app.title')}</h4>
      ${languageLine()}
      <div class="line"><div><b>${T('settings.app.startup')}</b><small>${T('settings.app.startupDesc')}</small></div>${toggle('startup', S.runAtStartup)}</div>
      <div class="line"><div><b>${T('settings.app.background')}</b><small>${T('settings.app.backgroundDesc')}</small></div><span style="color:#4d8a51;font-weight:800;font-size:12px">${I.check} ${T('common.active')}</span></div>
      ${profileLine()}
    </div>

    <div class="flex gap" style="margin-bottom:18px"><button class="btn glow" data-act="saveSettings">${T('settings.save')}</button></div>

    ${aboutSect()}
  </div>`;
}

// Game files: for each installed version whose game folder can hold a file of the player's own in the place
// of one Relic ships (versions[].replacements — what the build's payload says; the page names no file and no
// version), the answer PLAY gives without asking — Ask / Keep mine / Use Relic's, saved at once through
// settings.ownFiles —, and the button that puts Relic's files into every installed version (patch.applyAll).
// Drawn once a version is installed: before that there is no folder to put anything into.
function gameFilesSect() {
  const inst = S.versions.filter((v) => isInstalled(v.id));
  if (!inst.length) return '';
  const rows = inst.filter((v) => Array.isArray(v.replacements) && v.replacements.length).map((v) => {
    const cur = ownChoiceOf(v.id);
    const chip = (c, label) => `<button class="elchip${cur === c ? ' on' : ''}" data-act="setOwnChoice" data-arg="${esc(v.id + ':' + c)}">${label}</button>`;
    const reasons = v.replacements.map((r) => String(r.reason || '')).filter(Boolean).join(' ');
    return `<div class="line"><div><b>${T('settings.files.row', { id: v.id, files: v.replacements.map((r) => r.dst).join(', ') })}</b>${reasons ? `<small>${esc(reasons)}</small>` : ''}</div>
      <div class="chips" style="flex:none;flex-wrap:nowrap">${chip('ask', T('settings.files.ask'))}${chip('keep', T('settings.files.keep'))}${chip('replace', T('settings.files.replace'))}</div></div>`;
  }).join('');
  const busy = !!S.filesApplying;
  return `<div class="sect">
    <div class="flex between center" style="margin-bottom:4px"><h4 style="margin:0">${T('settings.files.title')}</h4>${rows ? `<span style="color:var(--muted);font-size:11px;font-weight:800">${T('settings.files.autosave')}</span>` : ''}</div>
    <p class="hint" style="margin:6px 0 4px">${T('settings.files.hint')}${rows ? ` ${T('settings.files.hintRows')}` : ''}</p>
    ${rows}
    <div class="line"><div><b>${T('settings.files.applyAll')}</b><small>${T('settings.files.applyAllDesc', { suffix: S.ownSuffix })}</small></div>
      <button class="btn" style="padding:8px 14px;font-size:12px;flex:none" data-act="applyAllFiles" ${busy ? 'disabled' : ''}>${busy ? T('settings.files.applying') : T('settings.files.apply')}</button></div>
  </div>`;
}

// "About Relic" — credits for the third-party pieces the launcher ships or relies on (README has the
// full list). Links open in the system browser through the backend (shell.openUrl).
const CREDITS = [
  { name: 'mhynot2 — khang06', note: 'about.credit.mhynot2', url: 'https://github.com/khang06/mhynot2' },
  { name: 'Akebi GC — NctimeAza/AnimeGame-Cheat-3.3 v1.2.3', note: 'about.credit.ee', url: 'https://github.com/NctimeAza/AnimeGame-Cheat-3.3' },
  { name: '2.8 global-metadata.dat patch', note: 'about.credit.metadata', url: '' },
  { nameKey: 'about.credit.loginPatchName', note: 'about.credit.loginPatch', url: '' },
  { name: 'GAA server data fixes — @AZ#7011', note: 'about.credit.gaaFixes', url: '' },
  { name: 'Dimbreath / AnimeGameData', note: 'about.credit.gamedata', url: 'https://gitlab.com/Dimbreath/AnimeGameData' },
  { name: 'Enka.Network', note: 'about.credit.enka', url: 'https://enka.network' },
  { name: 'Project Amber', note: 'about.credit.amber', url: 'https://gi.yatta.moe' },
  { name: 'nanoka.cc', note: 'about.credit.nanoka', url: 'https://nanoka.cc' },
  { name: 'Fiddler Classic — Telerik/Progress', note: 'about.credit.fiddler', url: 'https://www.telerik.com/fiddler/fiddler-classic' },
  { name: 'Cinzel · Mulish · JetBrains Mono', note: 'about.credit.fonts', url: 'https://fonts.google.com' },
];
function aboutSect() {
  // The enhancements credit names the versions this build ships the menu for (versions[].hasEnhancements);
  // a build that ships it for none has nothing of it to credit.
  const ee = S.versions.filter((v) => v.hasEnhancements).map((v) => v.id);
  // A row names its component by its own name (name), or by a description, which is a text of the
  // dictionary (nameKey).
  const rows = CREDITS.filter((c) => c.note !== 'about.credit.ee' || ee.length).map((c) => `<div class="line"><div><b>${c.nameKey ? T(c.nameKey) : esc(c.name)}</b><small>${T(c.note, { versions: ee.join('/') })}</small></div>
    ${c.url ? `<button class="btn ghost" style="padding:7px 12px;font-size:11.5px" data-act="openUrl" data-arg="${esc(c.url)}">${T('about.open')}</button>` : ''}</div>`).join('');
  return `<div class="sect" style="margin-top:18px"><h4>${T('about.title')}</h4>
    <p class="hint">${T('about.intro')}</p>
    <div class="line"><div><b>${T('help.guideTitle')}</b><small>${T('help.guideSub')}</small></div>
      <button class="btn ghost" style="padding:7px 12px;font-size:11.5px" data-act="openGuide">${T('about.open')}</button></div>
    ${rows}
    <p class="hint" style="margin:12px 0 0">${T('about.legal')}</p>
  </div>`;
}

// Which account data the OFFICIAL Genshin client would start on. A session that ended without a
// clean restore leaves a version's profile loaded, and the live client would then read (and
// overwrite) private-server data — so this has to be visible and fixable, not just logged.
function profileLine() {
  const p = S.profile;
  if (!p) return `<div class="line"><div><b>${T('settings.profile.title')}</b><small>${T('common.checking')}</small></div></div>`;
  if (p.liveActive) {
    return `<div class="line"><div><b>${T('settings.profile.title')}</b><small>${T('settings.profile.liveLoaded')}</small></div>
      <span style="color:#4d8a51;font-weight:800;font-size:12px">${I.check} ${T('settings.profile.liveBadge')}</span></div>`;
  }
  const why = p.torn
    ? T('settings.profile.torn')
    : T('settings.profile.versionLoaded', { version: p.active });
  const blocked = p.gameRunning ? ' ' + T('settings.profile.closeGame') : '';
  return `<div class="line"><div><b style="color:#b57b2a">${T('settings.profile.notActive')}</b><small>${why}${blocked}
    ${T('settings.profile.warn')}</small></div>
    <button class="btn" style="padding:8px 14px;font-size:12px" data-act="restoreLive" ${p.gameRunning ? 'disabled' : ''}>${T('settings.profile.restore')}</button></div>`;
}

function toggle(name, on) {
  return `<button class="toggle ${on ? 'on' : ''}" data-act="toggle" data-arg="${name}"><span class="knob"></span></button>`;
}
function toast() {
  // Errors keep the close button: they stay on screen much longer than the rest (see toastMs), so
  // they must be dismissable before their time.
  const close = S.toast.kind === 'bad'
    ? `<button class="toast-x" data-act="toast.close" title="${T('common.close')}">×</button>` : '';
  return `<div class="toast ${S.toast.kind || ''}">${esc(S.toast.msg)}${close}</div>`;
}

// ─────────────────────────── actions ───────────────────────────
function setModel(path, value) {
  const parts = path.split('.');
  let o = S;
  for (let i = 0; i < parts.length - 1; i++) o = o[parts[i]];
  const key = parts[parts.length - 1];
  if (typeof o[key] === 'number') { const n = parseInt(value, 10); o[key] = isNaN(n) ? o[key] : n; }
  else o[key] = value;
}
// Every toast goes away on its own — errors included. Errors
// get a time PROPORTIONAL to the text though: the ones that arrive here are often two-sentence
// instructions about what to do, and 3.2 s would cut them mid-sentence. ~70 ms/character, between 7
// and 15 seconds; the × button stays for whoever wants it sooner, and the messages that really MUST
// be read are windows (S.notice) anyway, not toasts.
function toastMs(msg, kind) {
  if (kind !== 'bad') return 3200;
  return Math.min(15000, Math.max(7000, String(msg || '').length * 70));
}
function showToast(msg, kind) {
  S.toast = { msg, kind };
  render();
  clearTimeout(showToast._t);
  showToast._t = setTimeout(hideToast, toastMs(msg, kind));
}
// Hiding the toast does NOT go through render(): rewriting the whole page moves the focus and the
// caret out of the field the user may be typing in right then — and the error toast leaves on its
// own, exactly in the seconds the user is correcting what was wrong. The toast is a single node,
// drawn last in #app, so it is removed directly; render() stays only as a safety net.
function hideToast() {
  S.toast = null;
  clearTimeout(showToast._t);
  const el = document.querySelector('#app > .toast');
  if (el) el.remove(); else render();
}
function addLog(t, m) { S.log.push({ t, m }); if (S.log.length > 80) S.log.shift(); }

// Dependency-free copy: WebView2 serves the UI from https://relic.app/ (secure context), so
// navigator.clipboard is available — but if policy refuses it, textarea + execCommand remains the
// variant that works anywhere. The failure says right away what had to be typed, so the user is not
// left stuck.
async function copyText(text) {
  try {
    if (navigator.clipboard && navigator.clipboard.writeText) { await navigator.clipboard.writeText(text); return true; }
  } catch (e) { /* fall through to the variant below */ }
  try {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.cssText = 'position:fixed;top:0;left:0;opacity:0';
    document.body.appendChild(ta);
    ta.select();
    const ok = document.execCommand('copy');
    ta.remove();
    return ok;
  } catch (e) { return false; }
}

// The answer to the PLAY question about the game folder's own files: it goes with this PLAY (playGo hands
// it to play.start) and, with "Don't ask again" ticked, is stored for the version (settings.ownFiles) — a
// store that fails costs a toast, never the launch.
function playOwnAnswer(id, choice, dontAsk) {
  S.playOwn = { id, choice };
  if (dontAsk === true) {
    rpc('settings.ownFiles', { versionId: id, choice })
      .then((r) => { if (r && r.ownFiles && typeof r.ownFiles === 'object') S.settings.ownFiles = r.ownFiles; })
      .catch((e) => showToast(e.message, 'bad'));
  }
  ACTIONS.playChecked(id);
}
// The stored answer of a version (Settings → Game files): 'keep', 'replace', or 'ask' when none is stored.
const ownChoiceOf = (id) => {
  const m = S.settings.ownFiles || {};
  const k = Object.keys(m).find((x) => x.toLowerCase() === String(id).toLowerCase());
  const c = k ? m[k] : '';
  return c === 'keep' || c === 'replace' ? c : 'ask';
};
// One version's line of the "Apply Relic's game files" result: its error first, then what was done — a
// version whose plan failed may still have had Relic's file put in the place of the player's.
function applyAllLine(r) {
  const list = (a) => (Array.isArray(a) ? a.filter(Boolean) : []);
  const parts = [];
  if (r.error) parts.push(String(r.error));
  if (list(r.applied).length) parts.push(t('settings.files.result.applied', { files: list(r.applied).join(', ') }));
  if (list(r.ownKept).length) parts.push(t('settings.files.result.ownKept', { files: list(r.ownKept).join(', ') }));
  if (list(r.kept).length) parts.push(t('settings.files.result.kept', { files: list(r.kept).join(', ') }));
  if (list(r.missing).length) parts.push(t('settings.files.result.missing', { files: list(r.missing).join(', ') }));
  return parts.length ? parts.join(' · ') : t('settings.files.result.current');
}

const ACTIONS = {
  'toast.close': () => hideToast(),
  'win.min': () => rpc('window.minimize'),
  'win.close': () => rpc('window.close'),
  go: (arg) => {
    S.picker = null; // navigating away must not leave a modal picker hanging over another screen
    // The 'server' screen entered again from itself in Server Admin mode (its lit sidebar entry, a way to the
    // Overview while the area is open): the tab it is to show, the status, the agent on this PC and the build facts
    // read again, and what no card holds yet (adminAreaLoad) — never again the policy or the Agent settings form a
    // card may hold unsaved edits of.
    if (arg === 'server' && S.screen === 'server' && isAdminMode()) {
      render(); checkServers();
      if (S.serverConfigured) { if (S.localAgent.status) loadLocalAgent(); loadAgentBuild(); }
      adminAreaLoad();
      return;
    }
    // The wizard entered again: what its local folder's check said may be out of date (another import, a removal, a
    // game started from that folder) — it is asked again before it counts.
    if (arg === 'install' && S.screen !== 'install') S.imp.stale = true;
    S.screen = arg; render();
    if (arg === 'install') impEnsure();
    if (arg === 'server' && S.serverConfigured) { checkServers(); if (isAdminMode()) { loadPolicy(); loadLocalAgent(); loadHotpatch(S.hotpatch.version || S.selectedVersionId || (S.versions[0] && S.versions[0].id)); loadAcctTemplatesAll(); loadAgentCfg(); loadAgentBuild(); acctsOpen(); } }
    if (arg === 'commands') loadGameData();
    if (arg === 'tweaks' && S.serverConfigured && isAdminMode()) twEnter();
    if (arg === 'settings') loadProfileStatus();
  },
  // The area's tab bar (adminHeader). Game rules is the 'tweaks' screen; the other five are tabs of the 'server'
  // screen. A tab change touches no status target and no card's state: inside 'server' it only draws another tab —
  // the Players tab reads its account list when nothing is held for the version on screen (acctsOpen); from Game
  // rules it draws the tab and reads what that screen has not read yet (adminAreaLoad) — never again what a card may
  // hold unsaved edits of (the policy, the Agent settings form).
  adminTab: (tab) => {
    if (tab === 'rules') { if (S.screen !== 'tweaks') ACTIONS.go('tweaks'); return; }
    if (!ADMIN_SERVER_TABS.includes(tab)) return;
    S.adminTab = tab;
    if (S.screen === 'server') { render(); acctsOpen(); return; }
    if (S.screen !== 'tweaks') { ACTIONS.go('server'); return; }
    S.picker = null; S.screen = 'server'; render();
    adminAreaLoad();
  },
  // The Library's "Check the server" and its server badge, and an agent install's close while the download it
  // queued runs: the Server page on its Overview (Player mode draws the Server status page).
  serverOverview: () => { S.adminTab = 'overview'; ACTIONS.go('server'); },

  selectVersion: (id) => {
    if (S.installing) return showToast(t('toast.installInProgress'), 'bad');
    if (isInstalled(id)) {
      // (the Library's choice is Game commands' version again: cmdVersionId)
      S.cmdVersion = null;
      S.selectedVersionId = id; rpc('version.select', { id }); render();
      if (S.screen === 'commands') loadGameData();
    }
    else {
      if (S.screen !== 'install') S.imp.stale = true; // (as ACTIONS.go enters the wizard)
      S.wiz.versionId = id; S.installStep = 0; S.screen = 'install'; render(); impEnsure();
    }
  },
  // PLAY of a version whose client is not known to start on this PC's Windows through Relic's launch
  // path (versions[].windows11Unsupported: a Windows 11 PC, a version the build's payload manifest flags)
  // asks first, before anything else is done: Cancel is the default answer, "Play anyway" goes on with
  // the rest of PLAY (playOwnFiles, then playChecked) — the launch is the normal one for this OS.
  play: (id) => {
    if (S.installing) return showToast(t('toast.installInProgress'), 'bad');
    if (S.playing) return showToast(t('toast.alreadyStarting'), 'bad');
    // (the end of the last session still holds the session gate — PLAY is off until it is done)
    if (sessionClosing()) return showToast(t('toast.sessionClosing'), 'bad');
    // (the backend refuses a launch while the files are applied too — the shortcut's launch included)
    if (S.filesApplying) return showToast(t('toast.filesApplying'), 'bad');
    S.playOwn = null; // an answer belongs to the PLAY it was given on
    if (!(versionById(id) || {}).windows11Unsupported) return ACTIONS.playOwnFiles(id);
    S.confirm = {
      title: t('confirm.windows11.title', { id }),
      body: t('confirm.windows11.body', { id }),
      yes: t('confirm.windows11.yes'),
      act: 'playOwnFiles', arg: id,
      focusNo: true,
      at: Date.now(),
    };
    render();
  },
  // A file of the player's own in the game folder, in the place of one Relic ships (play.ownFiles: the
  // backend looks, by the build's payload — the page names no file and no version). Asked after the Windows
  // 11 question and before the server check, only while the version's choice is "ask" (Settings → Game
  // files). Both answers go on with PLAY: the keyboard starts on "Keep mine" (Enter and Esc answer it), and a
  // look that fails asks nothing — the launch keeps the stored choice then. While the backend looks, the
  // pre-check splash is up (as for the server check: the same ownership token, and Hide gives the start up).
  playOwnFiles: async (id) => {
    if (S.installing) return showToast(t('toast.installInProgress'), 'bad');
    if (S.playing) return showToast(t('toast.alreadyStarting'), 'bad');
    if (sessionClosing()) return showToast(t('toast.sessionClosing'), 'bad');
    const tok = {};
    S.playing = { versionId: id, msg: t('play.checkingFiles'), check: true, tok };
    render();
    let r = null;
    try { r = await rpc('play.ownFiles', { versionId: id }); } catch (e) { r = null; }
    if (!S.playing || S.playing.tok !== tok) {
      if (!S.playing) showToast(t('toast.playCancelled'), 'ok');
      return;
    }
    S.playing = null;
    const files = r && Array.isArray(r.files) ? r.files.filter((f) => f && f.dst) : [];
    if (!files.length || r.choice !== 'ask') return ACTIONS.playChecked(id);
    const reasons = files.map((f) => String(f.reason || '')).filter(Boolean).join(' ');
    S.confirm = {
      title: t('confirm.ownFiles.title', { id }),
      body: [t('confirm.ownFiles.found', { id, files: files.map((f) => f.dst).join(', ') }),
        reasons ? t('confirm.ownFiles.reason', { reasons }) : '',
        t('confirm.ownFiles.choice', { kept: files.map((f) => f.kept || f.dst).join(', ') })].filter(Boolean).join(' '),
      yes: t('confirm.ownFiles.yes'), no: t('confirm.ownFiles.no'), danger: false,
      act: 'playOwnReplace', noAct: 'playOwnKeep', arg: id,
      checkbox: { label: t('confirm.ownFiles.dontAsk'), sub: t('confirm.ownFiles.dontAskSub'), checked: false },
      focusNo: true,
      at: Date.now(),
    };
    render();
  },
  playOwnReplace: (id, dontAsk) => playOwnAnswer(id, 'replace', dontAsk),
  playOwnKeep: (id, dontAsk) => playOwnAnswer(id, 'keep', dontAsk),
  // PLAY first goes through a FRESH server check: the polling cache can be half a minute old —
  // exactly the window in which an admin stops the stack. With the version's server stopped (or
  // unknown), the game would start and sit on network errors without any explanation, so the user
  // chooses explicitly. The actual launch is playGo.
  playChecked: async (id) => {
    if (S.installing) return showToast(t('toast.installInProgress'), 'bad');
    if (S.playing) return showToast(t('toast.alreadyStarting'), 'bad');
    if (sessionClosing()) return showToast(t('toast.sessionClosing'), 'bad');
    if (!S.serverConfigured) return ACTIONS.playGo(id);

    // The splash also acts as the modal guard while the check lasts (max ~5s). An ownership token,
    // not versionId: two presses on PLAY for the same version would otherwise be indistinguishable,
    // and the old invocation would adopt the new one's splash and launch twice.
    // check:true keeps the play.done/play.error events of a PREVIOUS session away from this splash —
    // they only close the launch splash, not the pre-check.
    const tok = {};
    S.playing = { versionId: id, msg: t('play.checkingServer'), check: true, tok };
    render();
    const st = await freshServerCheck(5000);
    if (!S.playing || S.playing.tok !== tok) {
      // The splash is no longer ours: "Hide" pressed during the check = giving up (nothing has been
      // launched yet); replaced by a newer invocation = stay quiet, that one speaks.
      if (!S.playing) showToast(t('toast.playCancelled'), 'ok');
      return;
    }
    S.playing = null;

    // Both reply shapes through the cache's own normaliser, and every fact from THIS reply: reading
    // st.versions would miss the player-mode {public, status} reply, which has none (a LIVE server would get
    // "The server is stopped"), and `live` from S.srv may have been superseded by a poll meanwhile.
    const vs = statusVersions(st);
    const entry = vs && vs[id];
    const agentErr = !st || statusUnknownOf(st);
    const down = entry && Array.isArray(entry.servicesDown) ? entry.servicesDown : [];
    // The public snapshot flags `degraded` without naming the services (down stays empty).
    const degraded = down.length > 0 || !!(entry && entry.degraded);
    // Every service is up, yet the game server still loads its data (`loading`, only an answered true):
    // a client that connects is turned away at the door.
    const loading = !!entry && entry.loading === true;
    if (entry && entry.up && !agentErr && !degraded && !loading) return ACTIONS.playGo(id);

    // Stack "up" but with dead game services = precisely the white-screen scenario: the client
    // connects to dispatch, but the dead gameserver leaves it on an empty screen. The agent restarts
    // them on its own (~2 minutes) — better a question now than a game started for nothing.
    if (entry && entry.up && !agentErr && degraded) {
      S.confirm = {
        title: t('confirm.selfRepair.title'),
        body: down.length ? t('confirm.selfRepair.body', { services: down.join(', ') }) : t('confirm.selfRepair.bodyNoList'),
        yes: t('confirm.selfRepair.yes'),
        act: 'playGo', arg: id,
        at: Date.now(),
      };
      return render();
    }

    // Up, healthy and still loading: the question only informs — the player may start the game and wait
    // at its door, or come back in a few minutes.
    if (entry && entry.up && !agentErr && loading) {
      S.confirm = {
        title: t('confirm.serverLoading.title'),
        body: t('confirm.serverLoading.body'),
        yes: t('confirm.serverLoading.yes'),
        act: 'playGo', arg: id,
        at: Date.now(),
      };
      return render();
    }

    // The server does not have this version at all (present:false). Read before the docker-fog test:
    // present is a filesystem fact on the box. "Stopped ... until its server is started" would promise a
    // start that cannot happen; the wording stays neutral (the admin may be extracting the stack now).
    if (entry && entry.present === false) {
      const hosted = Object.keys(vs).filter((k) => vs[k] && vs[k].present !== false).join(', ');
      S.confirm = {
        title: t('confirm.serverDown.titleAbsent', { id }),
        body: hosted ? t('confirm.serverDown.bodyAbsent', { id, hosted }) : t('confirm.serverDown.bodyAbsentNone', { id }),
        yes: t('confirm.serverDown.yes'),
        act: 'playGo', arg: id,
        at: Date.now(),
      };
      return render();
    }

    // The agent answered with the versions it knows and this one is not among them: it cannot say
    // whether a server for it runs, and "stopped ... until its server is started" would name a start
    // this agent cannot make. An answer that lists no version at all, or that names this one with an
    // entry nothing can be read from, says nothing about the list (versionsLack).
    if (!entry && !agentErr && versionsLack(vs, id)) {
      S.confirm = {
        title: t('confirm.serverDown.titleUnlisted', { id }),
        body: t('confirm.serverDown.bodyUnlisted', { id }),
        yes: t('confirm.serverDown.yes'),
        act: 'playGo', arg: id,
        at: Date.now(),
      };
      return render();
    }

    // Three situations, three texts: the server runs ANOTHER version / it is really stopped / unknown
    // (agent down, docker in a fog — a generalised up:false is not an assertion — or an answer that
    // does not name this version in any way).
    const unknown = agentErr || !entry;
    const live = Object.keys(vs || {}).find((k) => vs[k] && vs[k].up) || null;
    const body = unknown
      ? t('confirm.serverDown.bodyUnknown')
      : live && live !== id
        ? t('confirm.serverDown.bodyOther', { live, id })
        : t('confirm.serverDown.bodyDown', { id });
    S.confirm = {
      title: unknown ? t('confirm.serverDown.titleUnknown') : t('confirm.serverDown.titleDown'),
      body,
      yes: t('confirm.serverDown.yes'),
      act: 'playGo', arg: id,
      at: Date.now(),
    };
    render();
  },
  playGo: (id) => {
    if (S.installing) return showToast(t('toast.installInProgress'), 'bad');
    if (S.playing) return showToast(t('toast.alreadyStarting'), 'bad');
    // (a question answered while the last session's end began: its PLAY waits for that end too)
    if (sessionClosing()) return showToast(t('toast.sessionClosing'), 'bad');
    // The answer the question about the game folder's own files got on this PLAY (playOwnAnswer) goes with
    // the launch; without one the version's stored choice applies.
    const own = S.playOwn && S.playOwn.id === id ? S.playOwn.choice : '';
    S.playOwn = null;
    S.session = null; // the notice of the session before this one is the past now
    S.log = [{ t: 'sys', m: t('play.launching', { id }) }];
    // "Starting the game" splash: stays on screen until the client actually appears (play.appeared)
    // or the session fails — the play.log messages flow into it meanwhile.
    S.playing = { versionId: id, msg: '' };
    rpc('play.start', own ? { versionId: id, ownFiles: own } : { versionId: id }).catch((e) => { S.playing = null; showToast(e.message, 'bad'); render(); });
    render();
  },
  noticeOk: () => { S.notice = null; render(); },
  // The notice's one-time password: a refused copy says where the value is — in the window, selectable —, never the
  // value itself (noticeCopyRow's rule): a toast would show it a second time over every screen.
  noticeCopy: async () => {
    const pw = S.notice && S.notice.pick;
    if (!pw) return;
    const ok = await copyText(pw);
    showToast(ok ? t('toast.passwordCopied') : t('toast.passwordCopyFailed'), ok ? 'ok' : 'bad');
  },
  // A row of the notice (the Secrets card's "Show current values"), by its index: the value is taken
  // from the notice itself, never out of an attribute of the page. A refused copy names the ROW, never
  // its value: an error toast stays for seconds over every screen, also after the window is closed —
  // and the value is right there in the window, selectable.
  noticeCopyRow: async (i) => {
    const r = S.notice && Array.isArray(S.notice.rows) ? S.notice.rows[Number(i)] : null;
    if (!r || !r.copy || !r.value) return;
    const ok = await copyText(r.value);
    showToast(ok ? t('secrets.copied') : t('toast.copyFailed', { name: r.label }), ok ? 'ok' : 'bad');
  },
  hidePlaySplash: () => { S.playing = null; render(); },
  // The × of the Library's notice of a session's end (the closing step has none: it is under way).
  sessionNoteClose: () => { if (!sessionClosing()) S.session = null; render(); },
  copyAccount: async (name) => {
    const ok = await copyText(name);
    showToast(ok ? t('toast.accountCopied', { name }) : t('toast.copyFailed', { name }), ok ? 'ok' : 'bad');
  },

  // (a folder of the player's own is checked again as the version picked: impEnsure)
  pickVersion: (id) => { S.wiz.versionId = id; render(); impEnsure(); },
  pickFolder: async () => {
    try { const r = await rpc('pickFolder', { current: S.settings.installRoot }); if (r && r.path) { S.settings.installRoot = r.path; render(); } }
    catch (e) { showToast(e.message, 'bad'); }
  },
  // Browse... of the local folder: the file dialog on GenshinImpact.exe; its folder goes into the field and is checked.
  pickLocalGame: async () => {
    try {
      const r = await rpc('pickFile', { title: t('wizard.pickLocalTitle'), filter: t('wizard.pickLocalFilter'), initialDir: impInitialDir() });
      if (r && r.path) { S.wiz.localPath = pickedGameDir(r.path); S.imp.replace = null; S.imp.hit = null; render(); impCheck(true); }
    } catch (e) { showToast(e.message, 'bad'); }
  },
  // The local folder's check: asked again; the folder added as the version it holds (another of the catalogue); this
  // folder in the place of the one the version is installed at, and that choice taken back.
  impAgain: () => impCheck(true),
  impSwitch: (id) => {
    if (!id || !versionById(id) || S.installing) return;
    S.wiz.versionId = id; S.imp.replace = null;
    render(); impCheck(true);
  },
  impUseInstead: () => {
    const cur = impNow(), v = cur && cur.phase === 'done' ? cur.verdict : null;
    if (!v || v.reason !== 'installedElsewhere' || !v.installedAt) return;
    S.imp.replace = { path: impPath(), versionId: impVersion(), installedAt: v.installedAt };
    impCheck(true);
  },
  impKeepOther: () => { S.imp.replace = null; impCheck(true); },
  // "Find it on this PC": a new search, Stop, a deeper search (it goes on from what the last one left, adding the
  // folders it finds anew), the folded list of the folders that cannot be added.
  findStart: () => findRun(false),
  findDeeper: () => findRun(true),
  findStop: () => findHalt(),
  findOthers: () => { S.find.othersOpen = !S.find.othersOpen; syncFind(); },
  // A folder of the list into the field: as the version its own files (or its config.ini) name — the wizard's version
  // follows —, or as the version chosen at the first step when they name none (its note then stays beside the check).
  findUse: (i) => {
    const h = S.find.hits[Number(i)];
    if (!h || (h.status !== 'use' && h.status !== 'pick') || S.installing) return;
    const as = h.version || impVersion();
    if (!as) return showToast(t('toast.pickVersionFirst'), 'bad');
    S.wiz.localPath = h.dir;
    if (h.version && versionById(h.version)) S.wiz.versionId = h.version;
    S.imp.replace = null;
    S.imp.hit = h.status === 'pick' && typeof h.note === 'string' && h.note ? { dir: h.dir, text: h.note } : null;
    render();
    impCheck(true);
  },
  setTheme: (t) => { applyTheme(t); render(); },
  // wizSourceTouched: a choice made now in the wizard beats the saved preference at the next app.init
  // (re-done at every install.done) — otherwise the choice from the "Source" step would be rewritten
  // under it.
  setSource: (src) => { S.wiz.source = src; S.wizSourceTouched = true; render(); impEnsure(); },
  setDefaultSource: (src) => { S.settings.source = src; render(); },
  wizNext: () => { if (S.installStep === 0 && !S.wiz.versionId) return showToast(t('toast.pickVersionFirst'), 'bad'); S.installStep = Math.min(LAST, S.installStep + 1); render(); impEnsure(); },
  wizPrev: () => { S.installStep = Math.max(0, S.installStep - 1); render(); impEnsure(); },
  wizGoto: (i) => { S.installStep = Math.max(0, Math.min(LAST, parseInt(i, 10) || 0)); render(); impEnsure(); },
  // Voices step: a pack ticked / unticked — never the last ticked one (the game needs a voice language).
  wizVoiceToggle: (lang) => {
    const id = S.wiz.versionId || S.selectedVersionId;
    const ticked = selectedVoicePacks(id);
    const on = ticked.some((p) => p.lang === lang);
    if (on && ticked.length <= 1) return showToast(t('toast.pickVoiceFirst'), 'bad');
    S.wiz.voices[lang] = !on; render();
  },
  startInstall: async () => {
    if (S.installing) return; // a fast double-click must not fire install.start twice
    if (!S.wiz.versionId) return showToast(t('toast.pickVersion'), 'bad');
    const isLocal = S.wiz.source === 'local';
    if (isLocal && !S.wiz.localPath.trim()) return showToast(t('toast.pickFolderFirst'), 'bad');
    // A folder of the player's own is added only once its deep check said ready about what the field names now; the
    // import runs the same check again before its first write.
    const ready = isLocal ? impReady() : null;
    if (isLocal && !ready) { impEnsure(); return showToast(t('toast.importNotReady'), 'bad'); }
    // The folder chosen over the one the version is installed at goes with the request — only while the ready check was
    // made with that choice.
    const replaceAt = ready && ready.replaceInstalled ? impReplaceAt() : '';
    findHalt(); // (the backend stops a search an install would slow down too)
    // "local" is not a DOWNLOAD source — it does not overwrite the cdn/drive preference in settings.
    // (Assigned only after the address below is accepted: a refused address aborts with nothing changed.)
    // Mark installing BEFORE the first await — the await is the double-click window.
    S.activeInstallId = S.wiz.versionId;
    S.activeInstallLocal = isLocal ? S.wiz.localPath.trim() : null;
    S.activeInstallVoices = null;
    // The voice languages ticked at the Voices step (a folder import detects its own on disk). The
    // request is frozen here: Resume replays this very snapshot, whatever the ticks say by then.
    S.activeInstallReq = { type: 'install.start', payload: Object.assign({ versionId: S.activeInstallId, localPath: S.activeInstallLocal || '', voices: isLocal ? [] : selectedVoices(S.activeInstallId) },
      replaceAt ? { replaceInstalledAt: replaceAt } : {}) };
    S.installing = true; S.installDone = false; S.installError = null; S.installPaused = false; S.installStopIntent = null;
    // installPhase MUST reset too: a stale 'Done'/'Extract' from the previous run would gate the
    // Pause/Cancel buttons off until this run's first progress event — which never comes if the
    // connection stalls, leaving a modal overlay with no way out.
    S.installPhase = '';
    S.installFraction = 0; S.installMsg = t('install.preparing');
    resetProgress(); // otherwise the bar would start from where the previous install left it
    render();
    // The address typed in step 3 goes through server.setAddress (parse + TXT ports). A refused one stops
    // here: install.start would template the Fiddler rules for the previous server.
    let moved = false;
    try { moved = await saveAddressEdit(); }
    catch (e) {
      clearActiveInstall();
      showToast(e.message, 'bad');
      return;
    }
    if (!isLocal) S.settings.source = S.wiz.source;
    S.settings.fiddlerDecrypt = S.wiz.decrypt; S.settings.installCert = S.wiz.cert; S.settings.createShortcut = S.wiz.shortcut;
    const saved = await rpc('settings.save', S.settings).catch(() => null);
    // Another server: its status epoch starts now, not at install.done. The backend already holds the
    // new address even when settings.save failed, so re-sync from app.init rather than keep A's label.
    if (moved) applyInit(saved || await rpc('app.init').catch(() => null));
    const req = S.activeInstallReq;
    if (!req || !S.installing) return; // cancelled while the address was being saved — nothing to start
    rpc(req.type, req.payload)
      .catch((e) => { clearActiveInstall(); showToast(e.message, 'bad'); });
  },
  finishInstall: () => {
    const wasError = !!S.installError;
    // No local selection here: install.done → applyInit(d.state) already applied the backend's
    // authoritative selection (selecting wiz.versionId could pick a never-installed version).
    clearActiveInstall();
    S.installDone = false; S.installError = null; S.installPaused = false; S.installStopIntent = null; S.installPhase = '';
    if (!wasError) S.screen = 'library';
    render();
    // (an import that failed: its folder is checked again — the summary says what stands in its way now)
    if (wasError) impEnsure();
  },
  // The end of an install of a version this server has no ready-made login for: the install put away as
  // finishInstall does, then the card that creates an account for it (loginCreate).
  finishInstallLogin: (id) => {
    ACTIONS.finishInstall();
    ACTIONS.loginCreate(id);
  },
  // The way to an in-game login for a version this server has none ready-made for (LOGIN_WAY): the
  // Server page with its account card on that version — the player's own, or in Server Admin mode
  // "Create a player account" on the Players tab, scrolled into sight under the account list. A create already
  // running keeps the version it runs for.
  loginCreate: (id) => {
    if (!id) return;
    if (isAdminMode()) { S.acctNew.version = id; S.adminTab = 'players'; }
    else if (!S.account.running) { S.account.version = id; S.account.error = null; S.account.lines = []; }
    ACTIONS.go('server');
    if (isAdminMode()) acctNewScroll();
  },

  pauseInstall: () => stopInstall('pause'),
  cancelInstall: () => stopInstall('cancel'),
  resumeInstall: () => {
    if (!S.activeInstallId) return ACTIONS.abandonInstall();
    S.installPaused = false; S.installStopIntent = null;
    S.installPhase = ''; // pre-download again — both stop buttons apply until real progress arrives
    S.installMsg = t('install.resuming'); render();
    // The request that started the job is sent again — install.start and install.addVoices both
    // resume for free: partial zips continue via Range, finished ones short-circuit. The snapshot,
    // never a rebuilt payload: the wizard's ticks may have changed since the job paused.
    const req = S.activeInstallReq || { type: 'install.start', payload: { versionId: S.activeInstallId, localPath: S.activeInstallLocal || '' } };
    rpc(req.type, req.payload)
      .catch((e) => { S.installPaused = true; showToast(e.message, 'bad'); render(); });
  },
  abandonInstall: () => {
    // From the paused overlay: the backend already stopped, this only puts the UI away. The partial
    // download stays in the cache, so re-running the wizard resumes instead of starting over.
    clearActiveInstall();
    S.installPaused = false; S.installStopIntent = null; S.installPhase = '';
    showToast(t('toast.installAbandoned'), 'ok');
    render();
  },
  // ── voice languages (Library) ──
  // data-arg is "version|language"; version ids do not contain "|" (the language may hold parentheses).
  addVoice: (arg) => {
    const s = String(arg || ''), i = s.indexOf('|');
    const id = i > 0 ? s.slice(0, i) : '', lang = i > 0 ? s.slice(i + 1) : '';
    if (!id || !lang) return;
    if (S.installing || S.playing || S.remove) return showToast(t('toast.installInProgress'), 'bad');
    const pack = voicesFor(id).find((p) => p.lang === lang);
    const bytes = pack ? Math.max(0, Number(pack.size)) || 0 : 0;
    // The pack is unpacked next to the game files: the archive plus its contents live on the disk at
    // once, so about twice its size must be free during the add (the backend's preflight refuses less).
    S.confirm = {
      title: t('confirm.voices.title', { lang, id }),
      body: t('confirm.voices.body', { gb: gb(bytes), need: gb(bytes * 2) }),
      yes: t('confirm.voices.yes'),
      act: 'addVoiceGo', arg: { id, voices: [lang] }, at: Date.now(),
    };
    render();
  },
  // One language per job (the rpc takes a list; a second Add follows the first). The same overlay,
  // flags and events as a full install — only the words differ (activeInstallVoices).
  addVoiceGo: (a) => {
    if (!a || !a.id || S.installing) return;
    S.activeInstallId = a.id; S.activeInstallLocal = null; S.activeInstallVoices = a.voices.slice();
    S.installing = true; S.installDone = false; S.installError = null; S.installPaused = false; S.installStopIntent = null;
    S.installPhase = ''; S.installFraction = 0; S.installMsg = t('install.preparing');
    resetProgress();
    S.activeInstallReq = { type: 'install.addVoices', payload: { versionId: a.id, voices: a.voices.slice() } };
    render();
    rpc(S.activeInstallReq.type, S.activeInstallReq.payload)
      .catch((e) => { clearActiveInstall(); showToast(e.message, 'bad'); render(); });
  },

  sendCmd: (cat) => guardedSend(() => sendCommand(cat, S.cmd[cat] || '')),
  sendAvatar: () => guardedSend(async () => {
    const a = curAvatar();
    if (!a) return showToast(t('toast.pickAvatarFirst'), 'bad');
    await sendCommand('character', String(a.id));
  }),
  sendWeapon: () => guardedSend(async () => {
    const w = curWeapon();
    if (!w) return showToast(t('toast.pickWeaponFirst'), 'bad');
    const lvl = clampInt(S.sel.weaponLevel, 1, 90, 90);
    const asc = clampInt(S.sel.weaponPromote, 0, 6, 6);
    // Sent as a raw command, not as category "weapon": that branch parses the whole value as one
    // weapon id, so the level and the ascension would never reach the server. The string below is
    // byte-identical to what GmCommands.AddWeapon(id, level, promoteLevel) builds.
    await sendCommand('raw', `equip add ${w.id} ${lvl} ${asc}`);
  }),
  sendSet: () => guardedSend(async () => {
    const st = curSet();
    if (!st) return showToast(t('toast.pickSetFirst'), 'bad');
    const inv = S.sel.artifactMode !== 'equip';
    // Default "equipped directly" mode: equip add <piece> <level>, which puts the pieces on right
    // away, at the chosen level (default +20), on the character controlled in game — the only way to
    // +20 pieces, because "item add" has no level parameter. The DISPLAYED level (+0..+20) leaves
    // from here; the game counts levels from 1 (+20 = 21 on the wire), and the +1 translation is done
    // by the Backend through GmCommands.GiveArtifactPiece — sent raw, "20" gives +19 pieces. "In
    // inventory" mode: item add <piece> 1 — the pieces arrive UNequipped, at +0 (identical to
    // GiveArtifactSetToInventory).
    const lvl = clampInt(S.sel.artifactLevel, 0, setMaxLevel(st), setMaxLevel(st));
    addLog('sys', inv
      ? t('cmd.log.setSendingInv', { set: st.name, n: st.ids.length })
      : t('cmd.log.setSendingEquip', { set: st.name, n: st.ids.length, level: lvl }));
    render();
    // Sequential, not parallel: five concurrent sign+send round-trips through one agent connection
    // would interleave the console log and stress the HTTP/SSH path for no gain.
    for (let i = 0; i < st.ids.length; i++) {
      try {
        if (inv) await sendCommand('raw', `item add ${st.ids[i]} 1`);
        else await sendCommand('artifact', `${st.ids[i]} ${lvl}`);
      }
      catch (e) {
        addLog('bad', t('cmd.log.pieceFailed', { slot: slotName(i), id: st.ids[i], error: e.message }));
        render(); return;
      }
    }
    addLog('ok', inv
      ? t('cmd.log.setDoneInv', { set: st.name, n: st.ids.length })
      : t('cmd.log.setDone', { set: st.name, n: st.ids.length }));
    render();
  }),
  sendBreak: () => guardedSend(async () => {
    const n = clampInt(S.ingame.promote, 0, 6, 6);
    const lvl = PROMOTE_LEVEL_CAP[n];
    // Identical to GmCommands.BreakCurrentAvatar + SetCurrentAvatarLevel: first "break" (raises the
    // cap), then "level" (raises the level up to it) — both on the character controlled in game.
    // The message only says it was SENT: the server answers OK even when the command had no effect
    // (player disconnected), so there is no way to know here that it was applied.
    await sendCommand('raw', `break ${n}`);
    await sendCommand('raw', `level ${lvl}`);
    addLog('sys', t('cmd.log.breakSent', { n, level: lvl }));
    render();
  }),
  sendC6: () => guardedSend(async () => {
    // Identical to GmCommands.UnlockAllConstellations: "talent unlock all" — the server calls the
    // constellation "talent" (procTalent / forceUnlockAllTalent). Like break/level, it works on the
    // character controlled in game, not on the one chosen in the catalogue above.
    await sendCommand('raw', 'talent unlock all');
    addLog('sys', t('cmd.log.c6Sent'));
    render();
  }),
  sendHeroWit: () => guardedSend(async () => {
    // Identical to GmCommands.GiveHeroWit(500): 10M EXP — enough for level 1 → 90.
    await sendCommand('raw', 'item add 104003 500');
    addLog('sys', t('cmd.log.heroWitSent'));
    render();
  }),
  // Toggles on the player's game session: not guaranteed to persist after a relog, hence the Off
  // button too. The strings are identical to GmCommands.StaminaInfinite / WudiAvatar.
  sendStamina: (arg) => guardedSend(async () => {
    const on = arg !== 'off';
    await sendCommand('raw', `stamina infinite ${on ? 'on' : 'off'}`);
    addLog('sys', on ? t('cmd.log.staminaOn') : t('cmd.log.staminaOff'));
    render();
  }),
  sendWudi: (arg) => guardedSend(async () => {
    const on = arg !== 'off';
    await sendCommand('raw', `wudi global avatar ${on ? 'on' : 'off'}`);
    addLog('sys', on ? t('cmd.log.wudiOn') : t('cmd.log.wudiOff'));
    render();
  }),
  sendCostume: (arg) => guardedSend(async () => {
    const c = (S.gd.costumes || []).find((x) => x.id === num(arg));
    if (!c) return;
    // Identical to GmCommands.GiveCostume: a single item add, the server turns it into a costume.
    await sendCommand('raw', `item add ${c.id} 1`);
    addLog('sys', t('cmd.log.costumeSent', { name: c.name, char: c.char }));
    render();
  }),
  sendRaw: () => guardedSend(async () => {
    const v = S.rawCmd.trim();
    if (!v) return showToast(t('toast.writeCommand'), 'bad');
    await sendCommand('raw', v);
  }),

  openPicker: (kind) => { S.picker = { kind, q: '', el: '', type: '', r: '' }; render(); },
  closePicker: () => { S.picker = null; render(); },
  noop: () => {}, // the picker box swallows its own clicks so they never reach the closing backdrop
  setPickerEl: (el) => { S.picker.el = el; render(); },
  setPickerType: (t) => { S.picker.type = t; render(); },
  setPickerRar: (r) => { S.picker.r = r ? parseInt(r, 10) : ''; render(); if ((twPicker(S.picker) || {}).live) pickerTop(); },
  pickAvatar: (id) => { S.sel.avatarId = num(id); S.picker = null; render(); },
  pickWeapon: (id) => { S.sel.weaponId = num(id); S.picker = null; render(); },
  pickSet: (name) => { S.sel.setName = name; S.picker = null; render(); },
  toggleRaw: () => { S.rawOpen = !S.rawOpen; render(); },
  toggleHelp: () => { S.helpOpen = !S.helpOpen; render(); },
  setArtifactMode: (m) => { S.sel.artifactMode = m === 'equip' ? 'equip' : 'inv'; render(); },

  // Generic confirm dialog: Yes runs the remembered action, No only closes. Both ignore clicks inside
  // the arming window (see CONFIRM_ARM_MS) — the dialog simply stays up.
  confirmYes: () => {
    const c = S.confirm;
    if (!c || Date.now() - (c.at || 0) < CONFIRM_ARM_MS) return;
    S.confirm = null;
    // A question worded from the server's status (ask = the action that opened it) is asked again over the current
    // status, and Yes goes on only while that gives the same question. A status that changed under it — the version
    // started or stopped, another job began, the package or the address is another — puts the question the current
    // status gives in its place, armed again so that it is read before it is answered, or none when there is nothing
    // left to ask; the toast says why the Yes did not go on.
    if (c.ask && ACTIONS[c.ask]) {
      ACTIONS[c.ask](c.arg);
      const n = S.confirm;
      if (!n) return showToast(t('toast.askGone'), 'bad');
      const same = n.act === c.act && n.arg === c.arg && n.title === c.title && n.body === c.body && n.yes === c.yes && n.danger === c.danger;
      if (!same) return showToast(t('toast.askChanged'), 'bad');
      S.confirm = null;
    }
    // The dialog's own tick goes along as the second argument (undefined when the dialog had no box —
    // the receivers test `=== true`), the selected option as the third. Both live on S.confirm, never
    // in a global: Esc closes a dialog without clearing one, and the next dialog's box (the
    // removal's "also delete the files") would then open already ticked.
    if (ACTIONS[c.act]) ACTIONS[c.act](c.arg, c.checkbox ? !!c.checkbox.checked : undefined, c.selected, c.hotpatch ? !!c.hotpatch.checked : undefined); else render();
  },
  confirmNo: () => {
    const c = S.confirm;
    if (c && Date.now() - (c.at || 0) < CONFIRM_ARM_MS) return;
    S.confirm = null;
    // A second answer (noAct) goes on like Yes does, with the dialog's tick; a plain No only closes.
    if (c && c.noAct && ACTIONS[c.noAct]) ACTIONS[c.noAct](c.arg, c.checkbox ? !!c.checkbox.checked : undefined, c.selected);
    else render();
  },
  // An option card of a chooser dialog: the body and the Yes label follow it (confirmOverlay). Under
  // the same arming window as the buttons — a double-click on the opener must not pick an option.
  confirmPick: (id) => {
    const c = S.confirm;
    if (!c || !Array.isArray(c.options) || Date.now() - (c.at || 0) < CONFIRM_ARM_MS) return;
    if (c.options.some((o) => o.id === id)) { c.selected = id; render(); }
  },

  checkServers: () => checkServers(),
  startServer: (id) => runServerJob('start', id),
  stopServer: (id) => runServerJob('stop', id),
  // Both go through the Prepare chooser: the default save (a wipe — the "reset"
  // warning is that option's body), the admin's own progress, or the configuration fixes only. The
  // Overview's "Prepare the server" and the Players tab's "Reset / re-apply the progress…" ask the same
  // question; only the job kind differs.
  setupServer: (id) => prepareDialog(id, 'setup'),
  provisionServer: (id) => prepareDialog(id, 'provision'),
  // arg = "kind:version" (version ids hold no ":"); ticked = the pathfinding row (undefined when the
  // dialog had none — only a setup has it — and then nothing is sent: the backend turns an absent
  // field into null, which the agent reads as "not given"; a plain false would switch it OFF).
  // hotpatchTicked = the hotpatch row, sent only when the dialog drew it (the same rule: absent = the agent's default).
  prepareGo: (arg, ticked, selected, hotpatchTicked) => {
    const p = String(arg).split(':');
    const kind = p[0] === 'provision' ? 'provision' : 'setup', id = p[1];
    if (!id) return;
    const extra = { progress: PREPARE_MODES[selected] ? selected : 'default' };
    if (kind === 'setup' && typeof ticked === 'boolean') extra.pathfinding = ticked;
    if (kind === 'setup' && typeof hotpatchTicked === 'boolean') extra.hotpatch = hotpatchTicked;
    runServerJob(kind, id, extra);
  },
  // The ready-made server package of a version the server lacks (the Overview row, Server packages, Server folders),
  // behind its question: the size, the format and the host the agent reported, the folder, the disk check the agent
  // makes before the first byte — with its numbers when the agent reports them — and an archive already complete on the
  // server is unpacked, nothing downloaded. A download the agent would refuse now gets no question: its button is dead
  // and its row says why. The downloads an agent install or "Add version…" queued start without one (chainNext): the
  // admin chose them in the install form or that dialog, which name their sizes. A click on a button an older status
  // drew redraws the page from the status as it is.
  fetchServer: (id) => {
    const f = srvFacts(id), fe = f && f.fetch;
    if (!fetchReady(id)) return render();
    const size = sizeGb(fe.size), part = Math.max(0, Number(fe.part)) || 0, host = String(fe.host || '') || '—';
    const archive = fe.archive === true;
    const disk = isBytes(fe.need) && isBytes(fe.free) ? ' ' + t('confirm.fetch.disk', { need: sizeGb(fe.need), free: sizeGb(fe.free) }) : '';
    S.confirm = {
      title: archive ? t('confirm.fetch.titleArchive', { id }) : t('confirm.fetch.title', { id }),
      body: (archive ? t('confirm.fetch.bodyArchive', { size, dir: f.dir })
        : part > 0 ? t('confirm.fetch.bodyResume', { host, have: sizeGb(part), size, dir: f.dir })
          : t('confirm.fetch.body', { size, kind: fe.kind ? '.' + fe.kind : '—', host, dir: f.dir })) + disk,
      yes: archive ? t('confirm.fetch.yesArchive') : part > 0 ? t('confirm.fetch.yesResume') : t('confirm.fetch.yes'),
      danger: false, act: 'fetchGo', arg: id, ask: 'fetchServer', at: Date.now(),
    };
    render();
  },
  fetchGo: (id) => runServerJob('fetch', id),
  // The download's Stop (the job console, the Server packages row).
  fetchCancel: async (id) => {
    const j = S.srvJob;
    if (!j || !j.running || j.kind !== 'fetch' || j.cancelSent || !jobHere() || String(j.version) !== String(id)) return;
    j.cancelSent = true; render();
    try {
      const r = await rpc('server.fetch.cancel', { version: id });
      // stopping:false = no download of this version runs on the agent any more (ours ended, or went
      // on without us after the link was lost): nothing to wait for — re-read the status.
      if (!(r && r.stopping)) { if (S.srvJob === j) j.cancelSent = false; checkServers(true); }
    } catch (e) { if (S.srvJob === j) j.cancelSent = false; showToast(e.message, 'bad'); render(); }
  },
  // The pathfinding server on / off (the card's toggle) — a job that edits both compose
  // files and, on a running stack, stops / starts the container (enable ends with the 75 s check).
  pathfindingToggle: (id) => {
    const f = srvFacts(id);
    if (!f || f.pathfinding === null || srvBusy()) return;
    const on = !f.pathfinding;
    // Switched on over a package with nothing for the service to load (pfNothingToLoad): said in the
    // question — and where the data the server lacks can be downloaded, when the card offers that.
    const v = versionById(id);
    const empty = on && pfNothingToLoad(f)
      ? ' ' + t('confirm.pathfinding.enableEmpty') + (v && navdataOffered(v, f) ? ' ' + t('server.prepare.pathfindingNavdata') : '') : '';
    S.confirm = {
      title: t(on ? 'confirm.pathfinding.enableTitle' : 'confirm.pathfinding.disableTitle', { id }),
      body: t(on ? 'confirm.pathfinding.enableBody' : 'confirm.pathfinding.disableBody') + empty,
      yes: t(on ? 'confirm.pathfinding.enableYes' : 'confirm.pathfinding.disableYes'),
      act: 'pathfindingGo', arg: `${id}:${on ? 1 : 0}`, at: Date.now(),
    };
    render();
  },
  pathfindingGo: (arg) => { const p = String(arg).split(':'); runServerJob('pathfinding', p[0], { enabled: p[1] === '1' }); },
  // The navmesh archive of a version downloaded by the agent (the pathfinding card's button) — a job.
  // The dialog names the host of the catalogue's url and the size; the backend looks the url up
  // itself, the version is all it is told.
  navmeshFetch: (id) => {
    const v = S.versions.find((x) => String(x.id) === String(id));
    const cat = v && v.navmesh && v.navmesh.url ? v.navmesh : null;
    if (!cat || srvBusy()) return;
    let host = '';
    try { host = new URL(cat.url).host; } catch (e) { host = cat.url; }
    S.confirm = {
      title: t('confirm.navmesh.title', { id }),
      body: t('confirm.navmesh.body', { host, mb: mb(cat.size) }),
      yes: t('confirm.navmesh.yes'),
      act: 'navmeshGo', arg: id, at: Date.now(),
    };
    render();
  },
  navmeshGo: (id) => runServerJob('navmesh', id),
  navmeshCancel: async (id) => {
    const j = S.srvJob;
    if (!j || !j.running || j.kind !== 'navmesh' || j.cancelSent || !jobHere() || String(j.version) !== String(id)) return;
    j.cancelSent = true; render();
    try {
      const r = await rpc('server.navmesh.cancel', { version: id });
      // stopping:false = no download of this version runs on the agent any more: re-read the status.
      if (!(r && r.stopping)) { if (S.srvJob === j) j.cancelSent = false; checkServers(true); }
    } catch (e) { if (S.srvJob === j) j.cancelSent = false; showToast(e.message, 'bad'); render(); }
  },
  // The pathfinding server's own data of a version, downloaded by the agent and added to the stack (the
  // pathfinding card's button) — a job. The archive is the agent's own catalogue entry: the dialog names
  // the host and the size the agent reported, and the backend is told the version alone.
  navdataFetch: (id) => {
    const d = navdataOf(srvFacts(id));
    if (!d || d.tool === null || srvBusy()) return;
    const archive = d.archive === true;
    S.confirm = {
      title: t('confirm.navdata.title', { id }),
      body: archive ? t('confirm.navdata.bodyArchive') : t('confirm.navdata.body', { host: String(d.host || '') || '—', mb: mb(d.size) }),
      yes: archive ? t('confirm.navdata.yesArchive') : t('confirm.navdata.yes'),
      act: 'navdataGo', arg: id, at: Date.now(),
    };
    render();
  },
  navdataGo: (id) => runServerJob('navdata', id),
  // "Stop waiting" in the job console: the gameplay settings' waits of the followed job end at their next
  // look (POST /server/tweaks {version, cancel}). One click, dead until the job changes (twStopSend says
  // what the answer means).
  tweaksStop: async (id) => {
    const j = S.srvJob;
    if (!j || !j.running || !TW_STOP_KINDS.includes(j.kind) || j.tweaksStopSent || twStopRefused()
      || !jobHere() || String(j.version) !== String(id)) return;
    j.tweaksStopSent = true; render();
    if (!(await twStopSend(String(id))) && S.srvJob === j) { j.tweaksStopSent = false; render(); }
  },
  // The same stop for the job the status names as busy when this launcher does not follow it (started by
  // another admin, or by this launcher before it was closed: srvBusyNotes) — by that job's version.
  tweaksStopBusy: async () => {
    const b = twStopBusyJob();
    if (!b || S.twStop.busySent === twStopBusyKey(b)) return;
    const key = twStopBusyKey(b);
    S.twStop.busySent = key; render();
    if (!(await twStopSend(String(b.version))) && S.twStop.busySent === key) { S.twStop.busySent = ''; render(); }
  },
  navdataCancel: async (id) => {
    const j = S.srvJob;
    if (!j || !j.running || j.kind !== 'navdata' || j.cancelSent || !jobHere() || String(j.version) !== String(id)) return;
    j.cancelSent = true; render();
    try {
      const r = await rpc('server.navdata.cancel', { version: id });
      // stopping:false = no download of this version runs on the agent any more: re-read the status.
      if (!(r && r.stopping)) { if (S.srvJob === j) j.cancelSent = false; checkServers(true); }
    } catch (e) { if (S.srvJob === j) j.cancelSent = false; showToast(e.message, 'bad'); render(); }
  },
  // Set the unreadable agent state file aside and start from an empty state. The policy
  // and every per-version record are forgotten (the stacks and the players' progress are not), so
  // it goes through the same "are you sure?" as the progress reset.
  stateResetAsk: () => {
    S.confirm = {
      title: t('confirm.stateReset.title'),
      body: t('confirm.stateReset.body'),
      yes: t('confirm.stateReset.yes'),
      act: 'stateResetGo', arg: '',
      at: Date.now(),
    };
    render();
  },
  stateResetGo: async () => {
    try {
      await rpc('agent.state.reset', {});
      showToast(t('toast.stateReset'), 'ok');
    } catch (e) { showToast(e.message, 'bad'); }
    checkServers(true);
  },
  // Re-apply the events (Game rules), asked first: the agent stops and starts gameserver + multiserver (do_events).
  // Only while the version runs: the agent refuses a stopped stack, and every Start applies them anyway. A click on a
  // button an older status drew redraws the page from the status as it is.
  eventsServer: (id) => {
    const f = srvFacts(id);
    if (srvBusy() || srvDockerError() || !f || !f.present || !f.bootstrapped || !f.up || f.noEvents) return render();
    S.confirm = { title: t('confirm.events.title', { id }), body: t('confirm.events.body'), yes: t('confirm.events.yes'), act: 'eventsGo', arg: id, ask: 'eventsServer', at: Date.now() };
    render();
  },
  eventsGo: (id) => runServerJob('events', id),
  // Re-apply the public address (Access), asked first: what restarts depends on whether the version runs (do_netfix).
  // Nothing to apply without an address, and nothing to say of up or down under the docker fog.
  netfixServer: (id) => {
    const f = srvFacts(id), info = S.srv.info || {}, ip = typeof info.advertisedIp === 'string' ? info.advertisedIp : '';
    if (srvBusy() || srvDockerError() || !f || !f.present || !f.bootstrapped || !ip) return render();
    const live = f.up;
    S.confirm = { title: t('confirm.netfix.title', { id }), body: live ? t('confirm.netfix.bodyLive', { id, ip }) : t('confirm.netfix.body', { id, ip }),
      yes: t('confirm.netfix.yes'), danger: live, act: 'netfixGo', arg: id, ask: 'netfixServer', at: Date.now() };
    render();
  },
  netfixGo: (id) => runServerJob('netfix', id),
  acctCopyOpen: (id) => { S.acctCopy = { version: id, from: '', to: '' }; render(); },
  acctCopyClose: () => { S.acctCopy = null; render(); },
  acctCopyGo: () => {
    const c = S.acctCopy;
    if (!c) return;
    const from = c.from.trim(), to = c.to.trim();
    if (!from || !to) return showToast(t('toast.acctFillBoth'), 'bad');
    if (from.toLowerCase() === to.toLowerCase()) return showToast(t('toast.acctSame'), 'bad');
    // An agent that answered the account list in this session checks the copy first: its end asks with what it
    // found (acctsCopyStart). One without the list would run the check as the copy itself — it gets the question
    // first, and the copy as it always was.
    if (S.accts.supported) return acctsCopyStart('copy', c.version, { from, to }, { text: from }, { text: to }, null, true);
    S.confirm = {
      title: t('confirm.acctCopy.title', { from, to }),
      body: t('confirm.acctCopy.body', { from, to }),
      yes: t('confirm.acctCopy.yes'),
      act: 'acctCopyRun', arg: '',
      at: Date.now(),
    };
    render();
  },
  acctCopyRun: () => {
    const c = S.acctCopy;
    if (!c) return;
    S.acctCopy = null;
    runServerJob('accountcopy', c.version, { from: c.from.trim(), to: c.to.trim() });
  },

  // ── admin: "Accounts on this server" (acctsCard) ──
  // Another version's chip: its list read (the search typed is kept); what was open on the previous one is closed.
  acctsVersion: (id) => {
    const a = S.accts;
    if (!id || id === acctsCur()) return;
    Object.assign(a, { version: id, menu: '', pick: null, panel: null, moreError: '' });
    render(); loadAccts();
  },
  // Refresh: the list read again — also from an agent that answered it has no such route (it may have been updated).
  acctsRefresh: () => { const a = S.accts; a.unsupported = false; a.old = false; loadAccts(); },
  acctsSearch: () => { acctsBlurField(); loadAccts(); },
  acctsClear: () => { S.accts.q = ''; acctsBlurField(); loadAccts(); },
  acctsMore: () => loadAccts(true),
  // A row's Actions: its actions opened or closed; with an editor of the row open, Close closes that editor.
  acctsMenu: (key) => {
    const a = S.accts;
    if (a.panel && a.panel.key === key) { a.panel = null; a.menu = ''; } else { a.menu = a.menu === key ? '' : key; a.panel = null; }
    render();
  },
  acctsPanelClose: () => { S.accts.panel = null; render(); },
  // "Copy progress to…": the row is the source (an account with a character); the list then offers "Copy here" on the
  // rows that can take it, and a field for a new account.
  acctsCopyFrom: (key) => {
    const a = S.accts, r = acctsRow(key), d = acctsHeld();
    if (!r || !d || r.uid === null || r.level === null) return render();
    Object.assign(a, { pick: { version: d.version, from: acctsSnap(r), name: '' }, menu: '', panel: null });
    render();
  },
  acctsPickCancel: () => { S.accts.pick = null; render(); },
  // The target picked: both sides by their game uid — a target that has not played yet by its login —, so a guest, an
  // account a digit-only name would hide and names that differ in case only are named exactly.
  acctsCopyHere: (key) => {
    const p = S.accts.pick, r = acctsRow(key), d = acctsHeld();
    if (!p || !r || !d || d.version !== p.version || !acctsTargetOk(r, { live: acctsUp(d.version), chars: d.characters === 'ok', pres: d.presence === 'ok' })) return render();
    const to = r.uid !== null ? { toUid: r.uid } : { to: r.name };
    acctsCopyStart('copy', p.version, Object.assign({ fromUid: p.from.uid }, to), p.from, acctsSnap(r), null, false);
  },
  // A new account as the target: a name a player could sign up with, never digits alone (a mistyped uid is never a
  // new account). The check says whether the name is new or an account that exists — the question then says so.
  acctsPickNew: () => {
    const p = S.accts.pick;
    if (!p) return;
    const name = String(p.name || '').trim();
    if (!ACCOUNT_NAME_RE.test(name) || /^\d+$/.test(name)) return showToast(t('toast.acctsNewName'), 'bad');
    acctsCopyStart('copy', p.version, { fromUid: p.from.uid, to: name }, p.from, { text: name, asNew: true }, null, false);
  },
  // "Give a starting progress…": the row's account gets a progress template's save (a copy whose source is the
  // template, the signup's way), after its check.
  acctsTemplate: (key) => {
    const a = S.accts, r = acctsRow(key), d = acctsHeld();
    if (!r || !d || (r.uid === null && !r.name)) return render();
    const tpls = acctsTemplates(d.version);
    Object.assign(a, { panel: { kind: 'template', version: d.version, key: r.key, row: acctsSnap(r), tpl: tpls.length ? tpls[0].id : '' }, menu: '', pick: null });
    render();
  },
  acctsTpl: (id) => { const p = S.accts.panel; if (p && p.kind === 'template' && id) { p.tpl = id; render(); } },
  acctsTplGo: () => {
    const p = S.accts.panel;
    if (!p || p.kind !== 'template') return;
    const tp = acctsTemplates(p.version).find((x) => x.id === p.tpl);
    // (the toast's draw puts the editor on a template the stack has: acctsTplPanel)
    if (!tp) return showToast(t('toast.acctsTplChanged', { go: t('accts.tpl.go') }), 'bad');
    const to = p.row.uid !== null ? { toUid: p.row.uid } : p.row.name ? { to: p.row.name } : null;
    if (!to) return render();
    acctsCopyStart('template', p.version, Object.assign({ fromTemplate: tp.id }, to), null, p.row, { id: tp.id, label: tp.label }, false);
  },
  // "Set password…": the existing password job (it needs the version running). A generated password goes to the job
  // and into acctsPwPending only — the window its end opens shows it once.
  acctsPassword: (key) => {
    const a = S.accts, r = acctsRow(key), d = acctsHeld();
    if (!r || !d || !r.name) return render();
    Object.assign(a, { panel: { kind: 'password', version: d.version, key: r.key, row: acctsSnap(r), gen: true, pw: '', needed: false }, menu: '', pick: null });
    render();
  },
  acctsPwMode: (m) => { const p = S.accts.panel; if (!p || p.kind !== 'password') return; p.gen = m === 'gen'; p.pw = ''; render(); },
  acctsPwSet: () => {
    const a = S.accts, p = a.panel;
    if (!p || p.kind !== 'password' || !p.row.name) return;
    if (!acctsUp(p.version)) return showToast(t('accts.pw.notRunning', { version: p.version }), 'bad');
    if (srvBusy()) return showToast(t('toast.jobInProgress'), 'bad');
    const password = p.gen ? acctsNewPassword() : String(p.pw || '');
    if (!ACCOUNT_PASSWORD_RE.test(password)) return showToast(t('toast.accountBadPassword'), 'bad');
    const name = p.row.name;
    p.pw = ''; a.panel = null;
    const job = acctsJob('account.password', p.version, { name, password });
    if (job) acctsPwPending = { job, target: job.target, host: job.host, version: p.version, name, password: p.gen ? password : null };
  },
  // The question after a copy that created an account on a server that verifies passwords: its Yes opens that
  // account's password editor ("version|name"), on the Players tab.
  acctsPwOpenFor: (arg) => {
    const i = String(arg || '').indexOf('|'), version = i > 0 ? String(arg).slice(0, i) : '', name = i > 0 ? String(arg).slice(i + 1) : '';
    if (!version || !name || !isAdminMode()) return render();
    const a = S.accts, r = a.data && a.data.version === version ? a.rows.find((x) => x.name && x.name.toLowerCase() === name.toLowerCase()) : null;
    if (acctsCur() !== version) a.version = version;
    Object.assign(a, { panel: { kind: 'password', version, key: r ? r.key : '', row: r ? acctsSnap(r) : { key: '', name, uid: null }, gen: true, pw: '', needed: true }, menu: '', pick: null });
    if (S.screen === 'server' && adminTabOn() === 'players') { render(); acctsOpen(); return; }
    S.adminTab = 'players'; ACTIONS.go('server');
  },
  // Game commands for this player: the commands go to the row's version (S.cmdVersion — one this PC has not installed
  // too) with the uid filled in. An installed version becomes the Library's selection as well, as the Library makes
  // one (never during an install); one this PC has not installed leaves the Library's selection as it is.
  acctsCommands: (key) => {
    const r = acctsRow(key), d = acctsHeld();
    if (!r || r.uid === null || !d) return;
    S.cmdUid = String(r.uid);
    S.cmdVersion = d.version;
    if (S.selectedVersionId !== d.version && isInstalled(d.version) && !S.installing) { S.selectedVersionId = d.version; rpc('version.select', { id: d.version }).catch(() => {}); }
    ACTIONS.go('commands');
  },
  acctsCopyUid: async (key) => {
    const r = acctsRow(key);
    if (!r || r.uid === null) return;
    const uid = String(r.uid), ok = await copyText(uid);
    showToast(ok ? t('toast.uidCopied', { uid }) : t('toast.copyFailed', { name: uid }), ok ? 'ok' : 'bad');
  },
  // The question's Yes: the copy itself, with the body its check ran with (no dryRun).
  acctsCopyGo: () => {
    const a = S.accts, f = a.flow;
    if (!f || f.step !== 'confirm') return render();
    if (srvBusy()) { a.flow = null; return showToast(t('toast.jobInProgress'), 'bad'); }
    if (f.form) S.acctCopy = null;
    const job = acctsJob('accountcopy', f.version, f.body);
    if (job) Object.assign(f, { step: 'real', job }); else a.flow = null;
  },
  acctsFlowCancel: () => { S.accts.flow = null; render(); },

  // ── admin: "Bring progress from another version" (migrateCard) ──
  // The chips and the options choose what the next check asks; a check held for another choice stays out of sight
  // until the card asks that again (migrateHeld).
  migrateTarget: (id) => { S.migrate.target = id; render(); },
  migrateSource: (id) => { Object.assign(S.migrate, { srcKind: 'version', srcVersion: id }); render(); },
  migrateFolder: () => { S.migrate.srcKind = 'folder'; render(); },
  migrateOpt: (key) => { const m = S.migrate; if (key === 'keepPassword' || key === 'keepUid') { m[key] = !m[key]; render(); } },
  // The check: of every account of the source, or of the accounts chosen on the rows of a check held for the same
  // question — the names they go to included. A job, and it writes nothing to the target.
  migrateCheck: () => {
    const m = S.migrate, n = migrateNow();
    if (!migrateCanCheck(n)) return render();
    if (!migrateHeld(n)) m.choice = Object.create(null);
    Object.assign(m, { target: n.v, srcKind: n.kind, srcVersion: n.src });
    const body = Object.assign(migrateBody(m), { dryRun: true });
    const bad = migrateNamesProblem(body.accounts || []);
    if (bad) return showToast(bad, 'bad');
    Object.assign(m, { check: { job: null, at: Date.now(), req: body, res: null }, run: null, open: Object.create(null) });
    const before = S.srvJob;
    runServerJob('migrate', n.v, body);
    if (S.srvJob === before) m.check = null; else m.check.job = S.srvJob;
  },
  // A row's Move, on and off; on a row the check did not read, its Check next.
  migrateMove: (from) => {
    const c = S.migrate.choice[from], e = migrateEntry(from);
    if (!c || !e) return render();
    c.action = c.action !== 'skip' ? 'skip' : e.why === 'notChecked' ? 'create' : e.verdict;
    render();
  },
  // A row's select: Skip, Another name (the field keeps what was typed) or Overwrite of the account the name met.
  migrateChoose: (from, action) => {
    const c = S.migrate.choice[from], e = migrateEntry(from);
    if (!c || !e) return render();
    if (action === 'overwrite') Object.assign(c, { action, to: e.existing || '' });
    else if (action === 'rename') Object.assign(c, { action, to: c.action === 'rename' ? c.to : '' });
    else Object.assign(c, { action: 'skip', to: '' });
    render();
  },
  migrateDetail: (i) => { const o = S.migrate.open; if (o[i]) delete o[i]; else o[i] = true; render(); },
  // "Move N account(s)…": the question, and nothing else. It holds only for the running target and the source its check
  // ran on, under a held check — the ones on screen, which the card's state is set to before it is read: a chip the
  // status took away leaves another choice there — and for accounts that check read; its Yes asks it again (ask) and
  // goes on with the same words alone.
  migrateGo: () => {
    const m = S.migrate, n = migrateNow();
    if (srvBusy() || !n.v || !acctsUp(n.v) || !migrateHeld(n)) return showToast(t('migrate.changed'), 'bad');
    Object.assign(m, { target: n.v, srcKind: n.kind, srcVersion: n.src });
    if (migrateUnread(m).length) return showToast(t('migrate.checkFirst'), 'bad');
    const list = migrateBody(m).accounts || [];
    if (!list.length) return showToast(t('migrate.nothing'), 'bad');
    const bad = migrateNamesProblem(list);
    if (bad) return showToast(bad, 'bad');
    S.confirm = migrateQuestion(m);
    render();
  },
  // The question's Yes: the move itself, with the choices the question named.
  migrateRun: () => {
    const m = S.migrate, before = S.srvJob;
    m.run = { job: null, res: null };
    runServerJob('migrate', m.target, migrateBody(m));
    if (S.srvJob === before) m.run = null; else m.run.job = S.srvJob;
  },
  // The console's Stop: the running move ends after the account in hand (POST /server/migrate {version, cancel}), a
  // plain call that gets through while the job runs. stopping:false = no move of this version runs on the agent any
  // more: the status is read again. An agent that cannot stop one says so.
  migrateCancel: async (id) => {
    const j = S.srvJob;
    if (!j || !j.running || j.kind !== 'migrate' || j.cancelSent || !jobHere() || String(j.version) !== String(id)) return;
    j.cancelSent = true; render();
    try {
      const r = await rpc('server.migrate.cancel', { version: id });
      if (r && r.unsupported) showToast(t('core.agent.migrateUnsupported'), 'bad');
      if (!(r && r.stopping)) { if (S.srvJob === j) j.cancelSent = false; checkServers(true); }
    } catch (e) { if (S.srvJob === j) j.cancelSent = false; showToast(e.message, 'bad'); render(); }
  },
  // data-arg is "version:action"; version ids do not contain ":". Asked first: on a running version the agent takes
  // the whole stack down and up again (do_txt_fixes); under the docker fog it refuses the job.
  txtFixes: (arg) => {
    const p = String(arg).split(':'), id = p[0], revert = p[1] === 'revert';
    const f = srvFacts(id);
    if (srvBusy() || srvDockerError() || !f || !f.present || !f.bootstrapped || f.noTxtFixes) return render();
    const live = f.up;
    S.confirm = {
      title: revert ? t('confirm.txt.revertTitle', { id }) : t('confirm.txt.applyTitle', { id }),
      body: revert ? (live ? t('confirm.txt.revertBodyLive', { id }) : t('confirm.txt.revertBody', { id }))
        : (live ? t('confirm.txt.applyBodyLive', { id }) : t('confirm.txt.applyBody', { id })),
      yes: revert ? t('server.adv.txtRevert') : t('server.adv.txtApply'),
      danger: live, act: 'txtFixesGo', arg: `${id}:${revert ? 'revert' : 'apply'}`, ask: 'txtFixes', at: Date.now(),
    };
    render();
  },
  txtFixesGo: (arg) => { const p = String(arg).split(':'); runServerJob('txtfixes', p[0], { action: p[1] || 'apply' }); },
  closeSrvLog: () => { S.srvJob = null; render(); },
  // The Agent tab's Logs.
  agentJobsLoad: () => loadAgentJobs(),
  // An operation's "Show the log": its lines read (again at every Show); the same button hides them.
  agentJobOpen: (id) => {
    const k = String(id || ''), j = S.agentJobs;
    if (j.open === k) { j.open = ''; j.log = null; j.logError = ''; render(); return; }
    loadAgentJobLog(k);
  },
  // Relic's own log (relic.log) in the system's viewer; a file that is not there is said.
  relicOpenLog: () => rpc('app.openLog').catch((e) => showToast(e.message, 'bad')),

  toggle: async (name) => {
    if (name === 'startup') {
      const want = !S.runAtStartup;
      try { const r = await rpc('startup.set', { enabled: want }); S.runAtStartup = !!(r && r.runAtStartup); showToast(S.runAtStartup ? t('toast.startupOn') : t('toast.startupOff'), 'ok'); }
      catch (e) { showToast(e.message, 'bad'); }
      render(); return;
    }
    const map = { decrypt: () => S.wiz.decrypt = !S.wiz.decrypt, cert: () => S.wiz.cert = !S.wiz.cert, shortcut: () => S.wiz.shortcut = !S.wiz.shortcut,
      sdecrypt: () => S.settings.fiddlerDecrypt = !S.settings.fiddlerDecrypt,
      snouac: () => S.settings.fiddlerNoUac = S.settings.fiddlerNoUac === false,
      sgentle: () => S.settings.gentleExtract = S.settings.gentleExtract === false,
      senh: () => S.settings.enhancements = S.settings.enhancements === false };
    (map[name] || (() => {}))(); render();
  },

  // The address first: a refused one (the same backend.badAddress text as the start screen) saves nothing.
  // A settings.save failing AFTER the address was accepted re-syncs from app.init: the backend already
  // points at the new server, and the UI must not keep the old label (the failed toggles revert too).
  saveSettings: async () => {
    let moved = false;
    try {
      moved = await saveAddressEdit();
      const r = await rpc('settings.save', S.settings); applyInit(r); showToast(t('toast.settingsSaved'), 'ok');
    } catch (e) {
      if (moved) applyInit(await rpc('app.init').catch(() => null));
      showToast(e.message, 'bad');
    }
  },
  // Language switch: persist the setting, then reload the dictionary and redraw everything in it.
  setLanguage: async (code) => {
    S.settings.language = code;
    await rpc('settings.save', S.settings).catch((e) => showToast(e.message, 'bad'));
    await I18N.init(code);
    render();
  },
  restoreLive: async () => {
    try {
      const r = await rpc('profile.restoreLive');
      showToast((r && r.message) || t('toast.liveRestored'), 'ok');
    } catch (e) { showToast(e.message, 'bad'); }
    loadProfileStatus();
  },

  trustCert: async () => {
    try { const r = await rpc('fiddler.trustCert'); S.fiddler.certTrusted = true; showToast(r && r.already ? t('toast.certAlready') : t('toast.certApproved'), 'ok'); render(); }
    catch (e) { showToast(e.message, 'bad'); }
  },

  // Settings → Game files: a version's answer about a file of the player's own in the place of one Relic
  // ships. arg = "version:choice" (version ids hold no ':'); saved at once (settings.ownFiles), the row
  // follows the backend's map.
  setOwnChoice: async (arg) => {
    const s = String(arg), at = s.lastIndexOf(':');
    const id = s.slice(0, at), choice = s.slice(at + 1);
    if (!id) return;
    try {
      const r = await rpc('settings.ownFiles', { versionId: id, choice });
      if (r && r.ownFiles && typeof r.ownFiles === 'object') S.settings.ownFiles = r.ownFiles;
    } catch (e) { showToast(e.message, 'bad'); }
    render();
  },
  // "Apply Relic's game files to every installed version": asked first (it names the versions and says
  // where the player's own files go), then patch.applyAll — one line per version in the window that follows.
  applyAllFiles: () => {
    if (S.installing || S.remove) return showToast(t('toast.installInProgress'), 'bad');
    if (S.playing) return showToast(t('toast.filesGameStarting'), 'bad');
    if (S.filesApplying) return;
    const ids = S.installed.map((i) => i.id).filter(Boolean);
    if (!ids.length) return showToast(t('settings.files.noneInstalled'), 'bad');
    S.confirm = {
      title: t('confirm.applyAll.title'),
      body: t('confirm.applyAll.body', { versions: ids.join(', '), suffix: S.ownSuffix }),
      yes: t('confirm.applyAll.yes'), danger: false,
      act: 'applyAllFilesGo', arg: '',
      at: Date.now(),
    };
    render();
  },
  applyAllFilesGo: async () => {
    if (S.filesApplying) return;
    S.filesApplying = true; render();
    try {
      const r = await rpc('patch.applyAll');
      const results = r && Array.isArray(r.results) ? r.results.filter((x) => x && x.versionId) : [];
      if (!results.length) showToast(t('settings.files.noneInstalled'), 'bad');
      else S.notice = { title: t('settings.files.resultTitle'), rows: results.map((x) => ({ label: t('settings.files.resultVersion', { id: x.versionId }), value: applyAllLine(x) })) };
    } catch (e) { showToast(e.message, 'bad'); }
    S.filesApplying = false; render();
  },

  // ── start screen / modes ──
  pickMode: (mode) => {
    S.login.panel = mode;
    if (!S.login.address) S.login.address = S.serverAddr.host || ((S.defaults.servers[0] || {}).host) || '';
    render();
  },
  loginTab: (mode) => { S.login.panel = mode; render(); },
  // Leaving the panel / the start screen forgets the entry animations, so they play again next time.
  loginClose: () => { S.login.panel = null; S.login.saving = null; onceForget('login:'); render(); },
  loginChip: (host) => { loginAddressIn(host); S.login.test = null; render(); },
  loginMute: () => {
    S.login.muted = !S.login.muted;
    try { localStorage.setItem('relic-login-muted', S.login.muted ? '1' : '0'); } catch (e) { /* no localStorage */ }
    rpc('login.mute', { muted: S.login.muted }).catch(() => {});
    render();
  },
  loginAnim: () => {
    S.login.animOff = !S.login.animOff;
    try { localStorage.setItem('relic-login-anim', S.login.animOff ? '0' : '1'); } catch (e) { /* no localStorage */ }
    rpc('login.anim', { off: S.login.animOff }).catch(() => {});
    render();
  },
  loginTest: async () => {
    const address = S.login.address.trim(); if (!address) return;
    // Test dials what Enter will dial: the agent port Enter sends goes along (0 = let the backend resolve it).
    const agentPort = loginPortFor(address);
    if (agentPort === false) return showToast(t('login.badAgentPort'), 'bad');
    S.login.testing = true; S.login.test = null; render();
    // A late reply for an address no longer in the field (a chip clicked, the text edited meanwhile)
    // describes another server: dropped, its error toast included.
    try { const r = await rpc('server.test', { address, agentPort: agentPort || 0 }); if (S.login.address.trim() === address) S.login.test = r; }
    catch (e) { if (S.login.address.trim() === address) showToast(e.message, 'bad'); }
    S.login.testing = false; render();
  },
  // The panel's Enter. While its rpcs run, the saved servers' buttons, Save and Enter itself are dead (savedBusy): a
  // Delete of that host's entry clicked meanwhile would race the token the login saves when it is accepted.
  loginEnter: async () => {
    if (!loginCanEnter()) return;
    const address = S.login.address.trim();
    const mode = S.login.panel || 'player';
    const agentPort = loginPortFor(address);
    if (agentPort === false) return showToast(t('login.badAgentPort'), 'bad');
    let warning = null;
    S.login.savedBusy = 'enter'; render();
    try {
      let d;
      if (mode === 'admin') { const r = await rpc('admin.login', { address, token: S.login.token.trim(), agentPort: agentPort || 0 }); d = r && r.state; warning = r && r.warning; }
      else { await rpc('server.setAddress', { address, agentPort: agentPort || 0 }); d = await rpc('mode.set', { mode: 'player' }); }
      S.login.savedBusy = '';
      enterShell(d);
      if (warning) showToast(warning, 'bad');
    } catch (e) { S.login.savedBusy = ''; showToast(e.message, 'bad'); }
  },
  // ── saved servers (loginSavedList) ──
  // Save beside Test: the row for a name opens, prefilled with the host of the address — or, when that host is
  // saved already (saving it again replaces its entry), with that entry's name, none included; a name has 40
  // characters at most. A row already open keeps what was typed in it. The row remembers the host it was opened
  // for (loginSaveGo saves no name for another host).
  loginSave: () => {
    if (!loginCanSave()) return;
    if (!S.login.saving) S.login.saving = loginSavingFor(S.login.address);
    render();
    const f = document.querySelector('input[data-model="login.saving.label"]');
    if (f) { f.focus(); f.select(); }
  },
  loginSaveCancel: () => { S.login.saving = null; render(); },
  // servers.save: the address as typed, the tab's mode, the name and the agent port — the admin tab's typed one, on the
  // player tab the one the host's saved entry has (0 for a host not saved: the backend infers it) — never a token: one
  // reaches server.json only through an admin login, Use the agent on this PC, an open-administration entry or an
  // agent install the agent accepted. An address that names another host than the one the row was opened for saves
  // nothing: the name is filled again for that host and the field takes the caret, to be read before Save.
  loginSaveGo: async () => {
    const sv = S.login.saving, address = S.login.address.trim();
    if (!sv || !address || S.login.savedBusy) return;
    if (hostOf(address) !== sv.host) {
      S.login.saving = loginSavingFor(address);
      render();
      const f = document.querySelector('input[data-model="login.saving.label"]');
      if (f) { f.focus(); f.select(); }
      return;
    }
    const mode = S.login.panel === 'admin' ? 'admin' : 'player';
    const agentPort = mode === 'admin' ? loginAgentPort() : (loginPortFor(address) || 0);
    if (agentPort === false) return showToast(t('login.badAgentPort'), 'bad');
    const label = String(sv.label || '').trim();
    S.login.savedBusy = 'save'; render();
    try {
      const r = await rpc('servers.save', { address, mode, label, agentPort: agentPort || 0 });
      if (r && Array.isArray(r.servers)) S.savedServers = r.servers;
      S.login.saving = null;
      S.login.savedBusy = '';
      // (named as its row names it: one entry per host, so the entry of the address's host is the one saved)
      const saved = savedOfHost(address);
      showToast(t('toast.savedServer', { label: saved ? savedName(saved) : (label || address) }), 'ok');
    } catch (err) { S.login.savedBusy = ''; showToast(err.message, 'bad'); }
  },
  // A row's text: the panel takes its address, its tab and its agent port — nothing connects (the chips' rule).
  savedPick: (id) => {
    const e = savedById(id);
    if (!e || S.login.savedBusy) return;
    loginAddressIn(String(e.address || ''));
    S.login.panel = e.mode === 'admin' ? 'admin' : 'player';
    S.login.agentPort = e.agentPort > 0 ? String(e.agentPort) : '';
    S.login.test = null; S.login.saving = null;
    render();
  },
  // Connect: the start screen's own path with the entry's values (never settings.save) — a player entry
  // writes the host through server.setAddress, then the mode; an admin entry logs in with no typed token,
  // so the token saved for that host (or the build's) is the one used. With none, the admin tab asks for
  // it: the address filled, the token field focused.
  savedConnect: async (id) => {
    const e = savedById(id);
    if (!e || S.login.savedBusy) return;
    const admin = e.mode === 'admin', address = String(e.address || '');
    const agentPort = e.agentPort > 0 && e.agentPort <= 65535 ? e.agentPort : 0;
    S.login.savedBusy = 'connect'; render();
    let d, warning = null;
    try {
      if (admin) { const r = await rpc('admin.login', { address, token: '', agentPort }); d = r && r.state; warning = r && r.warning; }
      else { await rpc('server.setAddress', { address, agentPort }); d = await rpc('mode.set', { mode: 'player' }); }
    } catch (err) {
      S.login.savedBusy = '';
      if (admin && err.message === t('backend.tokenRequired')) {
        S.login.panel = 'admin'; loginAddressIn(address); S.login.agentPort = agentPort ? String(agentPort) : '';
        S.login.test = null; S.login.saving = null;
        showToast(t('toast.savedNeedToken', { host: e.host || hostOf(address) }), 'bad');
        const tk = document.querySelector('input[data-model="login.token"]');
        if (tk) tk.focus();
        return;
      }
      showToast(err.message, 'bad');
      return;
    }
    S.login.savedBusy = '';
    enterShell(d);
    if (warning) showToast(warning, 'bad');
  },
  // Delete: no question — the host's entry and everything Relic keeps for it (its server.json records, the game
  // logins remembered there, its SSH host keys) go; the server itself is not touched. A reply with fileKept: the
  // entry went, but server.json could not be changed — what it keeps for the host is still there, and the toast
  // says so. A refusal reads the list again before it is told.
  savedDelete: async (id) => {
    const e = savedById(id);
    if (!e || S.login.savedBusy) return;
    S.login.savedBusy = 'delete'; render();
    try {
      const r = await rpc('servers.delete', { id: e.id });
      savedReply(r);
      S.login.savedBusy = '';
      if (r && r.fileKept === true) showToast(t('toast.savedServerFileKept', { label: savedName(e) }), 'bad');
      else showToast(t('toast.savedServerDeleted', { label: savedName(e) }), 'ok');
    } catch (err) { await savedReread(); S.login.savedBusy = ''; showToast(err.message, 'bad'); }
  },
  // Delete all: asked once — every entry, and every server server.json keeps a token or key for, each with everything
  // Relic keeps for it (the body says so).
  savedDeleteAllAsk: () => {
    const n = (Array.isArray(S.savedServers) ? S.savedServers : []).length;
    if (!n || S.login.savedBusy) return;
    S.confirm = {
      title: t('confirm.savedDeleteAll.title'),
      body: t('confirm.savedDeleteAll.body', { count: n }),
      yes: t('confirm.savedDeleteAll.yes'),
      act: 'savedDeleteAllGo', arg: '', at: Date.now(),
    };
    render();
  },
  savedDeleteAllGo: async () => {
    if (S.login.savedBusy) return;
    const n = (Array.isArray(S.savedServers) ? S.savedServers : []).length;
    S.login.savedBusy = 'all'; render();
    try {
      const r = await rpc('servers.deleteAll', {});
      savedReply(r);
      S.login.savedBusy = '';
      if (r && r.fileKept === true) showToast(t('toast.savedServersFileKept', { count: n }), 'bad');
      else showToast(t('toast.savedServersDeleted', { count: n }), 'ok');
    } catch (err) { await savedReread(); S.login.savedBusy = ''; showToast(err.message, 'bad'); }
  },
  // The start screen's "Discard it" under a Quick Start run that was stopped half way: one question, then the plan file
  // goes (quickstart.clear — the resume after a restart with it). What the run installed stays, and an interrupted
  // download goes on from the Library or the Server page (the question says so).
  qsDiscardAsk: () => {
    if (!qsKept()) return;
    S.confirm = { title: t('confirm.qsDiscard.title'), body: t('confirm.qsDiscard.body'), yes: t('confirm.qsDiscard.yes'),
      act: 'qsDiscardGo', arg: '', at: Date.now() };
    render();
  },
  qsDiscardGo: async () => {
    // The page's own copy of that run (quickstart.js: S.qs.plan, S.qs.rt) goes first: nothing of it is saved again.
    if (S.qs && S.qs.plan) { S.qs.plan = null; S.qs.rt = null; }
    try {
      const r = await rpc('quickstart.clear', {});
      S.qsPlan = r && r.plan ? r.plan : null;
    } catch (e) { showToast(e.message, 'bad'); }
    render();
  },
  // Back to the start screen (the mode and the server can be changed there). The saved mode is
  // cleared so the next start asks again unless the user enters once more.
  changeMode: async () => {
    try { const d = await rpc('mode.set', { mode: '' }); applyInit(d); } catch (e) { /* ignore */ }
    S.login.panel = S.mode || null; S.login.address = S.serverAddr.host || S.login.address;
    S.screen = 'login'; onceForget('login:'); render();
  },
  openUrl: (url) => rpc('shell.openUrl', { url }).catch((e) => showToast(e.message, 'bad')),
  // The vendor's GIO guide book (PDF), shipped with the build — opened in the system viewer.
  openGuide: () => rpc('help.guide').catch((e) => showToast(e.message, 'bad')),

  // ── per-version removal ──
  removeVersion: (id) => {
    const inst = S.installed.find((i) => i.id === id);
    const imported = !!inst && inst.origin === 'import';
    if (S.installing || S.playing) return showToast(t('toast.installInProgress'), 'bad');
    S.confirm = {
      title: imported ? t('lib.remove.confirmImportTitle', { id }) : t('lib.remove.confirmTitle', { id }),
      body: imported ? t('lib.remove.confirmImportBody', { dir: (inst && inst.gameDir) || '' }) : t('lib.remove.confirmBody', { dir: (inst && inst.gameDir) || '' }),
      yes: imported ? t('lib.remove.confirmImportYes') : t('lib.remove.confirmYes'),
      act: 'removeGo', arg: id, at: Date.now(),
      // Imported folders: the files are NOT deleted unless the user ticks the box (unchecked by default).
      checkbox: imported ? { label: t('lib.remove.alsoDelete'), checked: false } : null,
    };
    render();
  },
  removeGo: async (id, ticked) => {
    const inst = S.installed.find((i) => i.id === id);
    const imported = !!inst && inst.origin === 'import';
    const deleteFiles = imported ? ticked === true : true;
    S.remove = { versionId: id, running: true, msg: t('lib.remove.starting'), fraction: 0, error: null, done: false, leftovers: [] };
    render();
    try { await rpc('version.remove', { versionId: id, deleteFiles }); }
    catch (e) { S.remove.running = false; S.remove.error = e.message; render(); }
  },
  removeClose: () => { S.remove = null; render(); },
  confirmCheckbox: () => { const c = S.confirm; if (!c || !c.checkbox) return; c.checkbox.checked = !c.checkbox.checked; render(); },
  confirmHotpatch: () => { const c = S.confirm; if (!c || !c.hotpatch) return; c.hotpatch.checked = !c.hotpatch.checked; render(); },

  // ── player account ──
  // arg = "version:template" of the card that was DRAWN (template ids are [a-z0-9-], version ids hold
  // no ':'), and the click pins that version: until then the card follows the status poll, and a poll
  // that sees the version down for a moment moves it elsewhere — Create would then send another version's
  // choice.
  accountTemplate: (arg) => {
    const [v, id] = String(arg || '').split(':');
    if (!v || !id) return;
    S.account.version = v; S.account.tplBy[v] = id;
    render();
  },
  // The card's version chooser: the typed name stays, each version keeps its own progress choice
  // (tplBy), and the previous version's log / error go.
  accountVersion: (id) => {
    const ac = S.account;
    if (ac.running || !id) return;
    ac.version = id; ac.error = null; ac.lines = [];
    render();
  },
  // "Create a new account" under the list of this launcher's accounts (several per player, up
  // to the server's maxPerPlayer): the new one joins the list and becomes the login shown in the Library;
  // Cancel folds the form away. account.done closes it for its version. Pinned like any touch of the
  // card: the form must not wander to another version under the player.
  accountAnother: (id) => { if (!id) return; S.account.version = id; S.account.another[id] = true; S.account.error = null; render(); },
  accountAnotherCancel: (id) => {
    const ac = S.account;
    if (ac.running) return;
    delete ac.another[id]; ac.error = null; ac.lines = [];
    render();
  },
  // arg = "version:name" (names hold no ':'): show another of this launcher's accounts as the login the
  // Library and PLAY use for that version. The reply is the init state, whose accounts carry the change.
  accountUse: async (arg) => {
    const s = String(arg || ''), i = s.indexOf(':');
    const version = i > 0 ? s.slice(0, i) : '', name = i > 0 ? s.slice(i + 1) : '';
    if (!version || !name) return;
    try { applyInit(await rpc('account.use', { version, name })); showToast(t('toast.accountUsed', { name, version }), 'ok'); }
    catch (e) { showToast(e.message, 'bad'); }
  },
  accountCreate: async (version) => {
    const ac = S.account;
    if (ac.running || S.acctPending) return;
    // First, before anything is drawn: the form on screen must still be what the server offers. A new
    // policy (another template, signup closed, passwords now verified) can land while the player types
    // — the poll does not redraw over a focused input — and Create would then send the stale choice.
    const offer = acctOffer(version);
    const template = ac.tplBy[version];
    if (acctSig(version) !== ac.shown || !offer || !offer.some((tp) => tp.id === template)) {
      return showToast(t('account.optionsChanged'), 'bad'); // showToast redraws: the card shows the new options
    }
    if (!acctUp(version)) return showToast(t('account.notRunning', { version }), 'bad');
    const limit = acctLimit(version);
    if (limit !== null && acctLive(version).length >= limit) return showToast(t('account.limitReached', { version, limit }), 'bad');
    const name = ac.name.trim();
    if (!ACCOUNT_NAME_RE.test(name)) return showToast(t('toast.accountBadName'), 'bad');
    const pub = S.pub.status;
    const pv = !!(pub && pub.versions && pub.versions[version] && pub.versions[version].passwordVerify);
    if (pv && (ac.password.length < 8 || ac.password.length > 64)) return showToast(t('toast.accountBadPassword'), 'bad');
    // Pinned: the card must not wander to another version (a status poll moving acctVersionCur) while
    // this one's create runs and its result comes back.
    ac.version = version;
    ac.running = true; ac.error = null; ac.lines = []; ac.result = null;
    // name: account.done clears the form only while it still holds this account's name.
    const pend = S.acctPending = { target: srvTarget(), version, name };
    render();
    // remember is left out: the player's own account is remembered on this PC (the backend's default).
    try { await rpc('account.create', { version, name, password: pv ? ac.password : '', template, origin: 'player' }); }
    catch (e) { if (S.acctPending === pend) S.acctPending = null; ac.running = false; ac.error = e.message; render(); }
  },

  // ── admin: create a player account ──
  // Each version keeps its own progress choice (tplBy).
  acctNewVersion: (id) => {
    const a = S.acctNew;
    if (!id) return;
    a.version = id;
    render();
    loadAcctTemplates(id);
  },
  // "version:template" of the card DRAWN, pinning that version — as on the player card (accountTemplate):
  // acctNewCur follows the running version until then, and a poll that sees it down moves the form.
  acctNewTemplate: (arg) => {
    const [v, id] = String(arg || '').split(':');
    if (!v || !id) return;
    S.acctNew.version = v; S.acctNew.tplBy[v] = id;
    render();
  },
  acctNewRemember: () => {
    const a = S.acctNew;
    if (!a.version && a.shownVersion) a.version = a.shownVersion; // a touch of the form pins it
    a.remember = !a.remember;
    render();
  },
  // "Import them now" (beside the card's not-imported note): the template databases of this stack, as
  // a server job of its own — POST /server/templates/ensure stops nothing. Its end re-reads this card's
  // templates (srvJobSettled).
  acctNewImport: (v) => {
    if (!v) return;
    const f = srvFacts(v);
    if (!f || !f.present || !f.up) return showToast(t('server.acctNew.notLive', { version: v }), 'bad');
    if (srvBusy()) return showToast(t('toast.jobInProgress'), 'bad');
    runServerJob('templates.ensure', v);
  },
  acctNewCreate: (version) => {
    const a = S.acctNew;
    const v = version || acctNewCur();
    if (!acctNewAdmin()) return;
    const f = srvFacts(v);
    if (!f || !f.present || !f.up) return showToast(t('server.acctNew.notLive', { version: v }), 'bad');
    const choices = acctNewChoices(v);
    const template = a.tplBy[v];
    if (!choices.list.some((tp) => tp.id === template)) return showToast(t('account.optionsChanged'), 'bad');
    const name = a.name.trim();
    if (!ACCOUNT_NAME_RE.test(name)) return showToast(t('toast.accountBadName'), 'bad');
    // Only what the form showed goes out: without verification there is no field, so nothing typed
    // earlier (under verification) rides along.
    const verify = passwordVerified(v);
    const password = verify ? a.password : '';
    // Typed = it must be valid; empty = the agent generates one — except for the admin's OWN login
    // ("Show it as my login"), which needs one typed: Relic calls that password "the password you
    // chose", and a generated one the admin never typed would be a lie shown under every later PLAY.
    const badPassword = password ? !ACCOUNT_PASSWORD_RE.test(password) : verify && !!a.remember;
    if (badPassword) return showToast(t('toast.accountBadPassword'), 'bad');
    a.version = v;
    if (srvBusy()) return showToast(t('toast.jobInProgress'), 'bad');
    a.pending = { remember: !!a.remember, name };
    // runServerJob keeps kind / version / target on S.srvJob, never the password.
    runServerJob('accountcreate', v, { name, password, template, remember: !!a.remember });
  },
  acctNewCopyPw: async (key) => {
    const c = S.acctNew.created.find((x) => String(x.key) === String(key));
    if (!c || !c.password) return;
    const ok = await copyText(c.password);
    showToast(ok ? t('toast.passwordCopied') : t('toast.copyFailed', { name: c.password }), ok ? 'ok' : 'bad');
  },

  // ── admin: policy ──
  policyLoad: () => loadPolicy(),
  // One-field policy merge, sent the moment it is clicked -- Settings' own Save button writes the
  // LAUNCHER's settings and never talks to the agent. The old value goes back on screen if the agent
  // refuses, so the card never shows a choice the server did not take. The source is the only field this
  // write changes, so of the agent's answer — the whole stored policy — only the source reaches the policy on
  // screen: the Players tab's policy card edits S.policy.data in place until "Apply policy", and the whole answer
  // in its place would put every unapplied edit there back to the server's value without a word. A policy not
  // loaded yet is the answer whole; another server's (a switch while the write ran) is left as it is.
  setHotpatchSource: async (arg) => {
    const pol = S.policy.data, target = srvTarget();
    if (!pol || !HOTPATCH_MIRRORS[arg] || pol.hotpatchSource === arg) return;
    const before = pol.hotpatchSource;
    pol.hotpatchSource = arg; render();
    try {
      const ans = await rpc('account.policy.set', { policy: { hotpatchSource: arg } });
      if (srvTarget() === target) {
        if (S.policy.data) S.policy.data.hotpatchSource = ans && typeof ans.hotpatchSource === 'string' ? ans.hotpatchSource : arg;
        else S.policy.data = ans;
      }
      showToast(t('toast.hotpatchSourceSaved', { source: t(HOTPATCH_MIRRORS[arg].label) }), 'ok');
    } catch (e) {
      if (S.policy.data === pol) pol.hotpatchSource = before;
      showToast(e.message, 'bad');
    }
    render();
  },
  policyToggleSignup: () => { const p = S.policy.data; p.signup = p.signup || { enabled: false, versions: {} }; p.signup.enabled = !p.signup.enabled; render(); },
  policyTogglePlayerCommands: () => { S.policy.data.playerCommands = !S.policy.data.playerCommands; render(); },
  policyToggleTemplate: (arg) => {
    const [v, tp] = String(arg).split(':');
    const p = S.policy.data; p.signup = p.signup || { enabled: false, versions: {} }; p.signup.versions = p.signup.versions || {};
    const pv = p.signup.versions[v] = p.signup.versions[v] || { templates: ['fresh'], maxPerDay: 50 };
    pv.templates = pv.templates || [];
    if (tp === 'fresh') return; // always allowed
    pv.templates = pv.templates.includes(tp) ? pv.templates.filter((x) => x !== tp) : pv.templates.concat([tp]);
    render();
  },
  policySave: async () => {
    try {
      const p = S.policy.data;
      Object.values((p.signup && p.signup.versions) || {}).forEach((pv) => { pv.maxPerDay = clampInt(pv.maxPerDay, 0, 100000, 50); });
      // Never with `openAdmin`: the Open administration card's toggle is the flag's only writer (its
      // own confirm, then a one-field write — openAdminGo), and the agent refuses a full save that
      // names it. A copy loaded before someone else flipped the option would otherwise flip it back —
      // making or deleting the key — with no confirm.
      const policy = Object.assign({}, p); delete policy.openAdmin;
      S.policy.data = await rpc('account.policy.set', { policy });
      showToast(t('toast.policySaved'), 'ok'); render();
    } catch (e) { showToast(e.message, 'bad'); }
  },

  // ── admin: open administration (its card on the Access tab, the status strip's banner) ──
  // The switch asks first: ON says what every player gets, OFF who loses admin mode — this launcher
  // too when its own entry is the open key (S.admin.openKey). The banner's "Close administration…"
  // arrives with 'off'; the card's toggle reads the loaded policy for the direction.
  openAdminToggle: (arg) => {
    const pol = S.policy.data;
    const dir = arg === 'off' || (pol && pol.openAdmin === true) ? 'off' : 'on';
    if (dir === 'on' && !pol) return;
    const self = dir === 'off' && S.admin.openKey;
    S.confirm = dir === 'on'
      ? { title: t('confirm.openAdmin.onTitle'), body: t('confirm.openAdmin.onBody', { host: S.serverAddr.host }), yes: t('confirm.openAdmin.onYes'), act: 'openAdminGo', arg: 'on', at: Date.now() }
      : { title: t('confirm.openAdmin.offTitle'), body: self ? t('confirm.openAdmin.offBodySelf') : t('confirm.openAdmin.offBody'),
        yes: self ? t('confirm.openAdmin.offYesSelf') : t('confirm.openAdmin.offYes'), act: 'openAdminGo', arg: 'off', at: Date.now() };
    render();
  },
  // One-field policy merge, sent the moment "Yes" is pressed (the model: setHotpatchSource): the
  // agent makes the key on ON and deletes it on OFF, and of its answer only the flag reaches the
  // policy on screen — a refusal flips nothing. OFF by a guest admin revokes this launcher's own key:
  // the backend forgets it and puts the mode back (admin.openForget), and the Server page is drawn
  // again in Player mode.
  openAdminGo: async (dir) => {
    const on = dir === 'on';
    let ans;
    try { ans = await rpc('account.policy.set', { policy: { openAdmin: on } }); }
    catch (e) { return showToast(e.message, 'bad'); }
    // The flag is the only field a one-field write can change, so a loaded policy takes just that:
    // the Players tab's policy card edits S.policy.data in place until "Apply policy", and the whole
    // answer in its place would put every unapplied edit (a toggle, a typed limit) back to the
    // server's value without a word. Only a policy not loaded yet is the answer whole.
    if (S.policy.data) S.policy.data.openAdmin = ans && ans.openAdmin; else S.policy.data = ans;
    // The status strip's banner reads the flag of the admin /status (S.pub.status): told here too, so it
    // follows the flip at once and not at the next poll.
    S.pub.status = Object.assign(S.pub.status || {}, { openAdmin: !!(S.policy.data && S.policy.data.openAdmin === true) });
    // An agent without the option answers the policy without the `openAdmin` field: it took nothing,
    // the toggle stays where the truth is, and the toast says what is missing rather than that it is on.
    if (!S.policy.data || typeof S.policy.data.openAdmin !== 'boolean') { render(); return showToast(t('core.agent.openAdminUnsupported'), 'bad'); }
    if (!on && S.admin.openKey) {
      // The backend forgets in memory first and writes its files after, so a forget that fails has
      // still put the mode back: the error is shown (not "back in Player mode") and the mode is read
      // again from the backend (app.init) — when it moved, a status epoch of its own, which reads the
      // Player view's status quietly, the toast staying — so this launcher is in Player mode on the
      // Server page all the same, never drawing admin cards over a key the agent refuses.
      let d;
      try { d = await rpc('admin.openForget'); }
      catch (e) {
        showToast(e.message, 'bad');
        applyInit(await rpc('app.init').catch(() => null));
        S.screen = 'server'; render();
        return;
      }
      applyInit(d);
      S.screen = 'server'; render();
      return showToast(t('toast.openAdminOffSelf'), 'ok');
    }
    showToast(on ? t('toast.openAdminOn') : t('toast.openAdminOff'), 'ok');
  },
  // Player mode → Server Admin mode with the key an open server hands out (admin.openEnter: the key is
  // asked token-less, validated against /status and only then saved for this host like a typed token —
  // flagged as the open key). A token already saved for this host takes the launcher in instead, the
  // open key neither asked nor saved: the reply says so (`openKey` false) and the toast names that
  // entry. No confirm: the banner above the button is the warning. The reply is admin.login's shape;
  // the Server page is drawn again in admin mode, its banner the guest's (the owner's, with the token).
  // The button goes dead in place meanwhile (syncOpenAdminButton), never through render().
  openAdminEnter: async () => {
    if (S.openAdmin.busy) return;
    S.openAdmin.busy = true; syncOpenAdminButton();
    try {
      const r = await rpc('admin.openEnter');
      applyInit(r && r.state);
      S.screen = 'server'; render();
      showToast(t(r && r.openKey === false ? 'toast.openAdminEnteredToken' : 'toast.openAdminEntered', { host: S.serverAddr.host }), 'ok');
    } catch (e) { showToast(e.message, 'bad'); }
    S.openAdmin.busy = false; syncOpenAdminButton();
  },

  // ── admin: secrets ──
  // A tab switch drops the loaded values and what was typed for the other version, and disowns a read
  // still in flight (secretsSeq): its reply is the previous tab's.
  secretsVersion: (id) => { secretsSeq++; S.secrets.version = id; S.secrets.data = null; S.secrets.form = {}; S.secrets.loading = false; render(); },
  // The reply is taken only by the card that asked for it — the ownership test of reloadSecretsQuiet:
  // the same server (srvRetarget makes a new S.secrets), the same version tab, no newer read. A Load of
  // 1.6 answered after a click on the 2.8 tab would otherwise put 1.6's passwords, Copy buttons and all, under
  // 2.8. What was typed stays: Load is also the way back after a failed quiet re-read, and must not cost
  // the admin the values typed and not applied yet (a tab switch, an Apply, a server switch clear them).
  secretsLoad: async (id) => {
    const s = S.secrets, seq = ++secretsSeq; // this read owns the card: a quiet re-read still in flight is dropped
    const mine = () => S.secrets === s && seq === secretsSeq && s.version === id;
    s.version = id; s.loading = true; render();
    try { const d = await rpc('server.secrets.get', { version: id }); if (mine()) s.data = d; }
    catch (e) { if (mine()) showToast(e.message, 'bad'); }
    if (!mine()) return; // dropped: nothing changed, nothing to draw over the card's new owner
    s.loading = false; render();
  },
  // secrets = the version the rows are of: a quiet re-read of it (reloadSecretsQuiet) redraws them
  // while the window is open.
  secretsShow: (id) => {
    const d = S.secrets.data; if (!d) return;
    S.notice = { title: t('secrets.showTitle', { version: id }), rows: secretsRows(d), secrets: id };
    render();
  },
  // 32 characters, the first always a LETTER (a–f, from the first byte's high half): the agent refuses
  // a MySQL root password and a stack internal password that do not start with one
  // (SECRET_LETTER_FIRST_RE), and one generator serves the four fields.
  secretsGen: (key) => {
    const a = new Uint8Array(16); crypto.getRandomValues(a);
    S.secrets.form[key] = 'abcdef'[(a[0] >> 4) % 6] + Array.from(a).map((b) => b.toString(16).padStart(2, '0')).join('').slice(1);
    render();
  },
  // Both read the form through secretsAsked. A value that breaks the letter-first rule (secretsBroken)
  // is refused here, before the question: the agent would fail the job on it after the form had been
  // cleared.
  secretsApply: (id) => {
    const asked = secretsAsked();
    if (!Object.values(asked).some((v) => v.trim())) return showToast(t('toast.secretsNothing'), 'bad');
    const broken = secretsBroken(asked);
    if (broken) return showToast(t(broken), 'bad');
    S.confirm = { title: t('confirm.secrets.title', { id }), body: t('confirm.secrets.body'), yes: t('confirm.secrets.yes'), act: 'secretsApplyGo', arg: id, at: Date.now() };
    render();
  },
  secretsApplyGo: (id) => {
    const asked = secretsAsked();
    if (!Object.values(asked).some((v) => v.trim())) return showToast(t('toast.secretsNothing'), 'bad');
    const broken = secretsBroken(asked);
    if (broken) return showToast(t(broken), 'bad');
    S.secrets.form = {}; S.secrets.data = null; runServerJob('secrets.set', id, asked);
  },

  // ── admin: the hotpatch mirror ──
  hotpatchVersion: (id) => { S.hotpatch.version = id; S.hotpatch.data = null; render(); loadHotpatch(id); },
  hotpatchLoad: (id) => loadHotpatch(id, true),
  // The sizes in these dialogs are the server's own numbers for THIS version (each version's differ), and so is
  // where the files come from: a bundle only when the manifest names one (hotpatchBundleUsable), else file by
  // file from the version's upstreams (hotpatchHosts).
  hotpatchEnable: (id) => {
    const d = hotpatchDataOf(id);
    const p = voicePlan(d);
    const names = (list) => list.map((l) => l.name).join(', ');
    const hosts = hotpatchHosts(d);
    const source = hotpatchBundleUsable(d) ? t('hotpatch.from.bundle', { hosts }) : t('hotpatch.from.files', { hosts });
    let body = t('confirm.hotpatch.enableBody', { mb: mb(d && d.files && d.files.bytes), source });
    // Said BEFORE the fixes are advertised: the voice languages nobody will be served, the selected
    // ones the mirror cannot fetch (on-demand off), and the ones left to the on-demand fallback.
    if (p && p.unserved.length) body += ' ' + t('confirm.hotpatch.voiceEnableWarn', { langs: names(p.unserved) });
    if (p && p.unfetched.length) body += ' ' + t('confirm.hotpatch.voiceEnableUnfetched', { langs: names(p.unfetched) });
    if (p && p.onDemand.length) body += ' ' + t('confirm.hotpatch.voiceEnableOnDemand', { langs: names(p.onDemand), hosts }) + (voiceShortWait(d) ? ' ' + t('hotpatch.voice.shortWait') : '');
    S.confirm = { title: t('confirm.hotpatch.enableTitle', { id }), body, yes: t('confirm.hotpatch.enableYes'), act: 'hotpatchEnableGo', arg: id, at: Date.now() };
    render();
  },
  hotpatchEnableGo: (id) => runServerJob('hotpatch', id, { enabled: true }),
  // Enabled but files are missing on the box: the same enable job fetches only what is missing and
  // restarts nothing when the advertised configuration did not change.
  hotpatchRefetch: (id) => {
    const d = hotpatchDataOf(id), hosts = hotpatchHosts(d);
    const source = hotpatchBundleUsable(d) ? t('hotpatch.from.bundleMost', { hosts }) : t('hotpatch.from.files', { hosts });
    S.confirm = { title: t('confirm.hotpatch.refetchTitle', { id }), body: t('confirm.hotpatch.refetchBody', { source }), yes: t('confirm.hotpatch.refetchYes'), act: 'hotpatchEnableGo', arg: id, at: Date.now() };
    render();
  },
  hotpatchDisable: (id) => {
    const d = hotpatchDataOf(id);
    const p = voicePlan(d);
    // The purge deletes the whole mirrored branch — the voice packs with it (the selection is kept).
    const sizes = { mb: mb(d && d.files && d.files.cachedBytes), gb: gb(p ? p.diskBytes : 0) };
    const label = p && p.diskBytes > 0 ? t('confirm.hotpatch.voiceDisablePurge', sizes) : t('confirm.hotpatch.purge', sizes);
    S.confirm = { title: t('confirm.hotpatch.disableTitle', { id }), body: t('confirm.hotpatch.disableBody'), yes: t('confirm.hotpatch.disableYes'), checkbox: { label, checked: false }, act: 'hotpatchDisableGo', arg: id, at: Date.now() };
    render();
  },
  hotpatchDisableGo: (id, purge) => runServerJob('hotpatch', id, { enabled: false, purge: purge === true }),
  // Voice packs: the ticks are local until the confirmed job saves them on the server.
  hotpatchVoiceToggle: (lang) => {
    const sel = S.hotpatch.voiceSel;
    if (!sel || srvBusy()) return;
    sel[lang] = sel[lang] !== true; render();
  },
  hotpatchVoice: (id) => {
    const d = hotpatchDataOf(id), p = voicePlan(d);
    if (!p) return;
    const names = (list) => list.map((l) => l.name).join(', ');
    // What ONE player of a language downloads is that language's whole set, whatever the server has cached.
    const perLang = p.ticked.map((l) => t('confirm.hotpatch.voiceLang', { name: l.name, gb: gb(l.bytes) })).join(', ');
    // Where the packs to download come from: a language's bundle when the manifest names one for a language
    // that still has packs to fetch, else file by file from the version's upstreams.
    const hosts = hotpatchHosts(d);
    const source = p.ticked.some((l) => l.bytes > l.cachedBytes && hotpatchBundleUsable(d, l.name))
      ? t('hotpatch.from.voiceBundle', { hosts }) : t('hotpatch.from.files', { hosts });
    let body = !p.ticked.length ? t('confirm.hotpatch.voiceNoneBody')
      : p.download > 0 ? t('confirm.hotpatch.voiceBody', { gb: gb(p.download), langs: names(p.ticked), perLang, source })
        : t('confirm.hotpatch.voiceCachedBody', { langs: names(p.ticked), perLang });
    // A version whose game does not wait for a pack fetched on demand (voiceShortWait): said while GBs are still owed.
    if (p.download > 0 && voiceShortWait(d)) body += ' ' + t('hotpatch.voice.shortWait');
    // Unticked with something on disk: complete packs stay there unserved until ticked again, and so does
    // what a stopped run left unfinished. The box below deletes both.
    const kept = p.loose.filter((l) => l.cachedBytes > 0), leftover = p.loose.filter((l) => !(l.cachedBytes > 0));
    if (kept.length) body += ' ' + t('confirm.hotpatch.voiceKeep', { langs: names(kept) });
    if (leftover.length) body += ' ' + t('confirm.hotpatch.voiceLeftover', { langs: names(leftover) });
    // The agent's free-space rule, said before it answers 409. The purge box is never pre-ticked (it
    // deletes) — the admin is told that this run needs it.
    const space = { need: gb(p.download), free: gb(p.free), reserve: gb(p.reserve), langs: names(p.loose), gb: gb(p.looseBytes) };
    if (p.hard) body += ' ' + t('hotpatch.voice.noSpace', space);
    else if (p.needsPurge) body += ' ' + t('confirm.hotpatch.voiceNeedsPurge', space);
    S.confirm = {
      title: t(!p.ticked.length ? 'confirm.hotpatch.voiceNoneTitle' : p.download > 0 ? 'confirm.hotpatch.voiceTitle' : 'confirm.hotpatch.voiceApplyTitle', { id }),
      body, yes: t(p.ticked.length && p.download > 0 ? 'confirm.hotpatch.voiceYes' : 'confirm.hotpatch.voiceApplyYes'),
      checkbox: p.loose.length ? { label: t('confirm.hotpatch.voicePurge', { gb: gb(p.looseBytes) }), checked: false } : null,
      // The list is frozen with the numbers the admin read: a refresh behind the dialog cannot change it.
      act: 'hotpatchVoiceGo', arg: { id, languages: p.ticked.map((l) => l.name) }, at: Date.now(),
    };
    render();
  },
  hotpatchVoiceGo: (a, purge) => runServerJob('hotpatch.voice', a.id, { languages: a.languages, purge: purge === true }),
  // Stop the running voice job of this version (POST {version, cancel:true} — a plain call,
  // not a job, so it gets through while this launcher follows that very job). The job then ends
  // within about one chunk — as server.error "Stopped on request …" when it is ours — and the file it
  // was fetching keeps its .part, which the next run resumes. One click: the button stays dead until
  // the job's state changes (refreshHotpatchAfterJob) or the call failed.
  hotpatchVoiceStop: async (id) => {
    if (S.hotpatch.voiceStop === id || !voiceJobRunning(id)) return;
    const target = srvTarget();
    const release = () => { if (S.hotpatch.voiceStop === id) S.hotpatch.voiceStop = null; };
    S.hotpatch.voiceStop = id; render();
    try {
      const r = await rpc('server.hotpatch.voice.cancel', { version: id });
      if (srvTarget() !== target) return;
      if (r && r.stopping) showToast(t('hotpatch.voice.stopRequested'), 'ok');
      // Nothing to stop: the agent has just said that no voice job of this version runs. A tracked one
      // is therefore over, whatever /status named since — a watchdog job beside it masks its end in
      // trackHotpatchBusy, and the Stop button would stay drawn until that job is over too. Drop it and
      // re-read the card, then the status.
      else {
        const b = S.hotpatch.busy;
        if (b && b.kind === 'hotpatch-voice' && b.version === String(id)) { S.hotpatch.busy = null; refreshHotpatchAfterJob(b.kind, id, true); }
        release(); showToast(t('hotpatch.voice.stopNone'), 'ok'); checkServers(true);
      }
    } catch (e) {
      if (srvTarget() !== target) return;
      release(); showToast(e.message, 'bad');
    }
  },

  // ── admin: gameplay settings (the Game rules tab) ──
  // A version chip: the draft of the version left stays as it is; a version without an answer is read,
  // and so is one whose answer is from before the end of a job of it, or from an earlier visit (`old`).
  tweaksVersion: (id) => {
    S.tw.version = id; render();
    if ((!twDataOf(id) || S.tw.old[id]) && !S.tw.loading[id]) loadTweaks(id);
  },
  tweaksLoad: (id) => loadTweaks(id || twCur(), true),
  // A preset chip, "path=value": that number into the draft; "path=" takes the entry out (the original
  // value, or "like the others" for a domain's own setting).
  tweaksPreset: (arg) => {
    const s = String(arg), at = s.lastIndexOf('=');
    if (at < 1 || !twReady(twCur())) return;
    const n = parseInt(s.slice(at + 1), 10);
    twSet(s.slice(0, at), isNaN(n) ? '' : n);
    render();
  },
  // Takes a section out of the draft. The cards' reset buttons ask first, and their question takes the
  // whole of it, whatever became of the card under it (selected 'all': what those dialogs hand over
  // through confirmYes). The button of a card the server switched off takes what of it needs a file the
  // server does not let Relic rewrite (twOffPaths): the card's other settings stay — the server still
  // applies them.
  tweaksClear: (section, ticked, selected) => {
    const v = twCur(), d = twDataOf(v);
    if (!TW_SECTIONS.includes(section) || !twReady(v)) return;
    if (selected !== 'all' && d.sections && d.sections[section] && d.sections[section].ok === false) {
      twOffPaths(v, section, twDraft(v), twOffNeeds(v, twDraftCanon(v).canon)).forEach((path) => twDel(path, v));
    } else twDel(section, v);
    render();
  },
  // The button of the refusal note: the document the server accepted last (`active`) becomes the draft,
  // in place of the one it refused — "the settings in use" while the server's files hold it, "the
  // previous settings" while they do not.
  tweaksShowActive: (id) => {
    const d = twDataOf(id);
    if (!d) return;
    S.tw.drafts[id] = twCopy(d.active && typeof d.active === 'object' ? d.active : {});
    render();
  },
  // Apply: the typed numbers first (a wrong one is said by a toast that names the field, and the field
  // gets the caret — nothing is sent), then the question, then the job.
  tweaksApply: () => {
    const v = twCur(), d = twDataOf(v);
    if (!twReady(v) || srvBusy() || twBroken(v) || !(twChanged(v) || twRetry(v))) return;
    const bad = twProblems(v)[0];
    if (bad) {
      twReveal(bad.path, v);
      showToast(bad.text, 'bad');
      const field = Array.from(document.querySelectorAll('#app .twpage [data-tw]')).find((el) => el.getAttribute('data-tw') === bad.path);
      if (field) { field.focus(); field.select(); }
      return;
    }
    // What the job does: a stopped server takes the settings at its next Start; a running one is asked to
    // read them at once — but one that refused the last settings is first waited for to read its files
    // again (the agent writes nothing while the server's last load is a failed one). Settings the server
    // never judged — the wait for its verdict was stopped, or it could not be asked (twUnjudged) — were
    // not refused: the running server's question. A game server that refuses its own data files
    // (twOwnRefused) is waited for the same way and still refuses them after it: nothing is written — and
    // a Start does not get past them: both questions say so and name the cards that put the files back.
    // Settings that rest on a file the server cannot use (twBlockedCards) are saved, and the files keep what
    // they hold for them: the question names their cards.
    // (tw = the version the question is about: it goes when another one comes on screen under it — twOpen.
    //  The question is asked again at its Yes over the answer as it is then (ask), and when an answer lands
    //  under it (twApplyAskAgain), which also holds it to what it sends: the canonical draft with the takenKey of
    //  the answer it was built on — sends.)
    const own = twOwnRefused(v), cards = (list) => list.map(twCardName).join(', ');
    const said = own ? { files: own.files.map(twFileName).join(', ') || '—', cards: cards(own.cards) } : null;
    const body = d.running === false ? (!own ? t('tweaks.confirm.applyStopped') : own.cards.length ? t('tweaks.confirm.applyOwnRefusedStopped', said) : t('tweaks.confirm.applyOwnRefusedStoppedAny', said))
      : own ? (own.cards.length ? t('tweaks.confirm.applyOwnRefused', said) : t('tweaks.confirm.applyOwnRefusedAny', said))
        : d.refused && !twUnjudged(d.refused) ? t('tweaks.confirm.applyRefused') : t('tweaks.confirm.applyLive');
    const blocked = twKnowsFiles(d) ? twBlockedCards(v, twDraftCanon(v).canon) : [];
    S.confirm = {
      title: t('tweaks.confirm.applyTitle', { id: v }),
      body: blocked.length ? body + ' ' + t('tweaks.confirm.blocked', { cards: cards(blocked) }) : body,
      yes: t('tweaks.confirm.applyYes'), act: 'tweaksApplyGo', arg: v, ask: 'tweaksApply', at: Date.now(), tw: v,
      sends: twDraftCanon(v).json + '|' + String(twTakenKey(v)),
    };
    render();
  },
  // What is sent is the draft as it is when the question is answered, checked again: a read may have
  // landed behind the dialog.
  tweaksApplyGo: (v) => {
    if (!twReady(v) || twBroken(v) || twProblems(v).length) return render();
    twSend(v, twCopy(twDraftCanon(v).canon));
  },
  tweaksDiscard: () => {
    const v = twCur();
    // Not under the page's own job of that version: its end replaces the draft with what was stored.
    if (!twChanged(v) || (jobHere() && S.srvJob.kind === 'tweaks' && String(S.srvJob.version) === String(v))) return;
    S.confirm = { title: t('tweaks.confirm.discardTitle', { id: v }), body: t('tweaks.confirm.discardBody'), yes: t('tweaks.confirm.discardYes'), act: 'tweaksDiscardGo', arg: v, at: Date.now(), tw: v };
    render();
  },
  tweaksDiscardGo: (v) => { delete S.tw.drafts[v]; render(); },
  // Restore the original game. With an agent that answers what a Reset of a card acts on and can act on all of
  // it (twRestorePlan): every card's data files put back at once — the files changed outside Relic too, a copy
  // of each kept: the question names them —; else the empty document — every setting taken out (every file the
  // settings rewrote goes back to what its base holds), a file changed outside Relic keeping those changes: the
  // question names those too. Asked again at its Yes over the answer as it is then (ask). The draft goes with
  // it: the re-read after the job replaces a draft that is still the one at the click (sent).
  tweaksRestore: () => {
    const v = twCur();
    if (!twReady(v) || srvBusy() || twBroken(v)) return;
    const plan = twRestorePlan(v), files = plan.hand.map(twFileName).join(', ');
    const missing = [];
    TW_SECTIONS.forEach((section) => twFiles(v, section).resetMissing.forEach((f) => { if (!missing.includes(f)) missing.push(f); }));
    // (the empty document leaves a setting that rests on a file the server cannot use as the files hold it, for an
    //  agent that applies the settings one at a time: the cards whose settings in effect stay are named)
    const d = twDataOf(v), active = d.active && typeof d.active === 'object' ? d.active : {};
    const stay = twKnowsFiles(d) ? TW_SECTIONS.filter((section) => twFiles(v, section).blocked.some((path) => twAt(active, path) !== undefined)) : [];
    const body = plan.reset ? t('tweaks.confirm.restoreBodyFiles') + (plan.hand.length ? ' ' + t('tweaks.confirm.restoreHand', { files }) : '')
      : t('tweaks.confirm.restoreBody') + (plan.hand.length ? ' ' + t('tweaks.confirm.restoreKeepsHand', { files, missing: missing.map(twFileName).join(', ') || '—' }) : '')
        + (stay.length ? ' ' + t('tweaks.confirm.restoreBlocked', { cards: stay.map(twCardName).join(', ') }) : '');
    S.confirm = { title: t('tweaks.confirm.restoreTitle', { id: v }), body, yes: t('tweaks.confirm.restoreYes'), act: 'tweaksRestoreGo', arg: v, ask: 'tweaksRestore', at: Date.now(), tw: v };
    render();
  },
  tweaksRestoreGo: (v) => {
    if (!twReady(v) || twBroken(v)) return render();
    if (!twRestorePlan(v).reset) return twSend(v, {});
    S.tw.sent[v] = twDraftCanon(v).json;
    twSendFiles(v, 'reset', TW_SECTIONS);
  },
  // A card's "Put back Relic's settings now…" / "Put back the original files now…" (twFileActs): the question
  // (twFilesAsk), then the job (twFilesGo) — the card's files, at once, never the draft.
  tweaksRevert: (section) => twFilesAsk(section, 'revert'),
  tweaksReset: (section) => twFilesAsk(section, 'reset'),
  tweaksRevertGo: (section) => twFilesGo(section, 'revert'),
  tweaksResetGo: (section) => twFilesGo(section, 'reset'),
  // The Spiral Abyss card: the rotation (its select keeps the keyboard), the floor — from its first
  // chamber —, the chamber.
  tweaksAbyssRotation: (id) => {
    twAbyssUi(twCur()).rotation = id; render();
    const el = document.querySelector('#app .twpage select[data-tw-rotation]');
    if (el) el.focus();
  },
  tweaksAbyssFloor: (id) => {
    const v = twCur(), floor = (twAbyssOf(v).floors || {})[id];
    if (!floor) return;
    Object.assign(twAbyssUi(v), { floor: String(id), number: floor.number, index: 1 });
    render();
  },
  tweaksAbyssChamber: (index) => { twAbyssUi(twCur()).index = num(index); render(); },
  // An enemy tile ("half:wave:config id") or a wave's Replace all ("half:wave") of the chamber on screen:
  // the enemy picker for that slot / for every slot of the wave.
  tweaksAbyssPick: (arg) => {
    const v = twCur();
    if (!twReady(v)) return;
    const [hi, wi, cfg] = String(arg).split(':'), lid = twAbyssView(v).lid, ch = (twAbyssOf(v).chambers || {})[lid];
    const target = twSlotTarget(v, 'abyss', ch && (ch.halves || [])[num(hi)], wi, cfg);
    if (target) twPickMonster(Object.assign(target, { v, sec: 'abyss', lid, half: num(hi) }));
  },
  setPickerMonsterKind: (kind) => { S.picker.mk = kind; render(); },
  // "Only enemies the Abyss already uses", kept for the next slot (until the server changes).
  togglePickerAll: () => { S.picker.all = !S.picker.all; S.tw.ui.monstersAll = S.picker.all; render(); },
  // The pick goes into every slot the picker was opened for, under the section those slots are in; the
  // slot's original enemy takes its entry out of the document instead.
  pickMonster: (id) => {
    const p = S.picker, monster = num(id);
    S.picker = null;
    if (p && p.kind === 'monster' && monster && TW_SLOT_SECTIONS.includes(p.sec) && twReady(p.v) && !hasOwn(twLocked(p.v, p.sec), p.group)) {
      const listed = hasOwn(twDataOf(p.v).monsters, monster);
      twPickSlots(p).forEach((s) => {
        const path = [p.sec, 'slots', p.group, s.cfg];
        if (s.orig === monster) twDel(path, p.v); else if (listed && s.orig !== undefined) twSet(path, monster, p.v);
      });
    }
    render();
  },
  // Reset chamber: its enemy level and every slot of its halves back to the original.
  tweaksAbyssResetChamber: (lid) => {
    const v = twCur(), ch = twReady(v) && (twAbyssOf(v).chambers || {})[lid];
    if (!ch) return;
    twDel(['abyss', 'levels', lid], v);
    (ch.halves || []).forEach((h) => Object.keys(h.slots || {}).forEach((cfg) => twDel(['abyss', 'slots', h.group, cfg], v)));
    render();
  },
  // The changes the draft still holds for a monster group the page cannot rewrite.
  tweaksAbyssUnlock: (group) => { if (twReady(twCur())) { twDel(['abyss', 'slots', group]); render(); } },
  // Reset the whole Abyss: asked first — it can be the work of many chambers.
  tweaksAbyssReset: () => {
    const v = twCur();
    if (!twReady(v) || !twSectionSet('abyss', v)) return;
    S.confirm = { title: t('tweaks.confirm.abyssResetTitle', { id: v }), body: t('tweaks.confirm.abyssResetBody'), yes: t('tweaks.confirm.resetYes'), act: 'tweaksClear', arg: 'abyss', selected: 'all', at: Date.now(), tw: v };
    render();
  },
  // The Domain and the Quest card. Their lists ("card:…"): the chip — from the first page —, and the
  // next / previous page.
  tweaksListKind: (arg) => {
    const [card, kind] = String(arg).split(':'), v = twCur();
    if (!hasOwn(TW_LISTS, card) || !twReady(v)) return;
    Object.assign(twListUi(card, v), { kind: kind || '', page: 0 });
    render();
  },
  tweaksPage: (arg) => {
    const [card, step] = String(arg).split(':'), v = twCur();
    if (!hasOwn(TW_LISTS, card) || !twReady(v)) return;
    twListUi(card, v).page += num(step);
    render();
  },
  // A row of a list opens its editor under it (the open row closes it); the editor comes into sight.
  tweaksDomainOpen: (id) => twOpenRow('domains', id),
  tweaksQuestOpen: (key) => twOpenRow('quests', key),
  // A domain's own multiplier, "<dungeon id>=<n>": "=" alone, or the multiplier it gets anyway, is
  // "like the others" — the entry leaves the draft.
  tweaksDomainMult: (arg) => {
    const [id, value] = String(arg).split('='), v = twCur();
    if (!twReady(v) || !hasOwn(twDataOf(v).domains, id)) return;
    const n = parseInt(value, 10);
    twSet(['domains', 'byId', id, 'multiplier'], isNaN(n) || n === twDomainBase(v, id).mult ? '' : n, v);
    render();
  },
  tweaksDomainReset: (id) => { if (twReady(twCur())) { twDel(['domains', 'byId', id]); render(); } },
  tweaksDomainLevelDrop: (id) => { if (twReady(twCur())) { twDel(['domains', 'byId', id, 'level']); render(); } },
  // An enemy tile ("group:wave:config id") or Replace all ("group:wave") of the open domain's line-up:
  // the enemy picker for that slot / for every slot of the wave — of the line-up, when it has no waves.
  tweaksLineupPick: (arg) => {
    const v = twCur();
    if (!twReady(v)) return;
    const [group, wi, cfg] = String(arg).split(':'), id = twListUi('domains', v).open, lu = twLineupOf(v, id);
    const target = lu && lu.group === group ? twSlotTarget(v, 'domains', lu, wi, cfg) : null;
    if (target) twPickMonster(Object.assign(target, { v, sec: 'domains', id }));
  },
  // Reset enemies: every slot of the line-up back to the original, for every domain it serves.
  tweaksLineupReset: (group) => { if (twReady(twCur())) { twDel(['domains', 'slots', group]); render(); } },
  // The button of the note of a line-up the page cannot rewrite: its slots and the enemy level of every
  // domain it serves out of the draft — the agent refuses both while that script is as it is.
  tweaksLineupUnlock: (group) => {
    const v = twCur();
    if (!twReady(v)) return;
    twDel(['domains', 'slots', group], v);
    twLineupDungeons(v, group).forEach((id) => twDel(['domains', 'byId', id, 'level'], v));
    render();
  },
  // "Add item" of the open domain / the open reward: the item picker for that list.
  tweaksDomainExtraAdd: (id) => {
    const v = twCur();
    if (!twReady(v) || !hasOwn(twDataOf(v).domains, id)) return;
    const held = twGet(['domains', 'byId', id, 'extra'], v);
    if ((Array.isArray(held) ? held.length : 0) < twDomainExtraMax(v, id)) twPickItem(v, 'domain', id);
  },
  tweaksQuestAdd: (rid) => {
    const v = twCur(), row = twReady(v) && ((twDataOf(v).quests || {}).rewards || {})[rid];
    if (row && row.edit === 1 && twQuestItems(v, rid).length < TW_REWARD_VIRTUAL.length + TW_REWARD_SLOTS) twPickItem(v, 'quest', rid);
  },
  setPickerItemGroup: (group) => { S.picker.ig = group; render(); pickerTop(); },
  // The picked item joins the list the picker was opened for, with an amount of 1 — its field gets the
  // caret, selected, so the amount can be typed right away (a pinned item has no field: its one is the
  // amount).
  pickItem: (id) => {
    const p = S.picker, item = num(id);
    S.picker = null;
    let field = '';
    if (p && p.kind === 'item' && item && twReady(p.v) && twItemRows(Object.assign({}, p, { q: String(item), ig: '', r: '' })).some((r) => r.id === item)) {
      const at = p.use === 'domain' ? ['domains', 'byId', p.id, 'extra'] : ['quests', 'byReward', p.id, 'items'];
      if (p.use === 'quest') twQuestSeed(p.v, p.id);
      const list = twGet(at, p.v);
      twSet(at, (Array.isArray(list) ? list : []).concat([{ item, count: 1 }]), p.v);
      if (p.use === 'quest') twQuestTidy(p.v, p.id);
      // (a reward that is the quest's original one again with this item is drawn in the row's own order)
      const rows = p.use === 'quest' ? twQuestItems(p.v, p.id) : twGet(at, p.v), row = (Array.isArray(rows) ? rows : []).findIndex((e) => twNum(e && e.item) === item);
      if (row >= 0 && !twItemPinned(p.v, item)) field = at.concat(row, 'count').join('.');
    }
    render();
    const el = field && Array.from(document.querySelectorAll('#app .twpage [data-tw]')).find((x) => x.getAttribute('data-tw') === field);
    if (el) { el.focus(); el.select(); }
  },
  // The × of an item, "<dungeon id>:<index>" / "<reward id>:<index>". A reward keeps one item at least.
  tweaksDomainExtraRemove: (arg) => {
    const [id, i] = String(arg).split(':');
    if (twReady(twCur())) { twDel(['domains', 'byId', id, 'extra', i]); render(); }
  },
  tweaksQuestRemove: (arg) => {
    const [rid, i] = String(arg).split(':'), v = twCur(), row = twReady(v) && ((twDataOf(v).quests || {}).rewards || {})[rid];
    if (!row || row.edit !== 1 || twQuestItems(v, rid).length < 2) return;
    twQuestSeed(v, rid);
    twDel(['quests', 'byReward', rid, 'items', i], v);
    twQuestTidy(v, rid);
    render();
  },
  // Restore original: the quest's own reward leaves the draft.
  tweaksQuestRestore: (rid) => { if (twReady(twCur())) { twDel(['quests', 'byReward', rid]); render(); } },
  // "Reset all domain settings" / "Reset all quest rewards": asked first, like the Abyss.
  tweaksRewardsReset: (section) => {
    const v = twCur();
    if (!twReady(v) || !twSectionSet(section, v)) return;
    const domains = section === 'domains';
    S.confirm = { title: domains ? t('tweaks.confirm.domainsResetTitle', { id: v }) : t('tweaks.confirm.questsResetTitle', { id: v }),
      body: domains ? t('tweaks.confirm.domainsResetBody') : t('tweaks.confirm.questsResetBody'), yes: t('tweaks.confirm.resetYes'), act: 'tweaksClear', arg: section, selected: 'all', at: Date.now(), tw: v };
    render();
  },

  // ── admin: agent settings ──
  agentCfgLoad: () => loadAgentCfg(),
  agentCfgOpen: () => { S.agentCfg.open = !S.agentCfg.open; render(); },
  agentCfgToggle: (key) => {
    const c = S.agentCfg, s = agentCfgSettings().find((x) => x.key === key);
    if (!s || s.env || !c.data || !c.data.writable || c.saving || c.restarting) return;
    c.form[key] = agentCfgField(s) === '1' ? '0' : '1';
    render();
  },
  // Only the changed fields, as strings ('' = remove the line). The agent validates everything before it
  // writes anything and names the key it refuses (400 / 409), which the toast shows as it is.
  agentCfgSave: async () => {
    const c = S.agentCfg, d = c.data;
    if (!d || d.tooOld || !d.writable || c.saving || c.restarting) return;
    const changed = agentCfgChanged();
    if (!changed.length) return;
    const bad = changed.find((s) => (s.type === 'mib' || s.type === 'seconds') && !/^\d*$/.test(agentCfgField(s).trim()));
    if (bad) return showToast(t('agentCfg.badNumber', { setting: agentCfgLabel(bad.key) }), 'bad');
    const set = {};
    changed.forEach((s) => { set[s.key] = agentCfgField(s).trim(); });
    // A settings read still in flight (the Server page opened again, a folder job settled) holds the
    // file from before this save and must not land over its reply: drop it, and the loading flag it
    // would have cleared.
    agentCfgSeq++; c.loading = false;
    c.saving = true; render();
    try {
      const r = await rpc('agent.config.set', { set });
      if (S.agentCfg !== c) return; // another server now: this reply describes the old one
      agentCfgApply(r, false);
      c.restartRequired = r && Array.isArray(r.restartRequired) ? r.restartRequired : [];
      showToast(t('toast.agentCfgSaved'), 'ok');
      // A live key may show elsewhere (the server name in the status, the local agent's facts).
      refreshServersQuiet();
      if (r && r.local) loadLocalAgent();
    } catch (e) {
      if (S.agentCfg === c) showToast(e.message, 'bad');
    }
    if (S.agentCfg === c) { c.saving = false; render(); }
  },
  // The backend asks the agent to restart and follows it until it answers with a new start time (up to
  // a minute); back:false is not an error — the agent may be slow, or supervised elsewhere. No job may
  // run meanwhile (the agent refuses with 409; the button is dead under srvBusy()).
  agentRestart: async () => {
    const c = S.agentCfg, d = c.data;
    if (!d || !d.restart || c.restarting || srvBusy()) return;
    const local = !!d.local;
    c.restarting = true; render();
    try {
      const r = await rpc('agent.restart');
      if (S.agentCfg !== c) return;
      // back:false carries the backend's own words (where to look on the box) when it has them.
      const slow = !!r && r.back === false;
      showToast(slow ? (typeof r.message === 'string' && r.message ? r.message : t('toast.agentRestartSlow')) : t('toast.agentRestarted'), slow ? 'bad' : 'ok');
    } catch (e) {
      if (S.agentCfg !== c) return;
      showToast(e.message, 'bad');
    }
    c.restarting = false; c.restartRequired = [];
    render();
    // A restarted agent may run with another mirror folder, another public address...: read it all again
    // (the Logs too, when they were read: the new process names none of the old one's operations).
    loadAgentCfg(); checkServers(true);
    if (S.agentJobs.list) loadAgentJobs();
    if (S.hotpatch.data && S.hotpatch.version) loadHotpatch(S.hotpatch.version);
    if (local) loadLocalAgent();
  },
  agentCfgReloc: (id) => {
    const c = S.agentCfg, d = c.data;
    if (!d || d.tooOld || !d.writable || c.restarting || srvBusy()) return;
    const vi = agentCfgVersion(id);
    clearTimeout(relocTimer);
    c.reloc = { version: id, from: vi.dir, dir: '', mode: vi.present ? 'move' : 'repoint', check: null, checking: false, soon: false, seq: 0, at: Date.now() };
    render();
  },
  // "Add version {id}…" (the Overview row and Server packages of a version the agent has no folder for, Server folders'
  // Add…) and "Change folder…" (a configured version whose folder holds no server yet): the folder dialog in its two
  // ways — download the package into the folder, or use a package already extracted there (relocOverlay, flow).
  addVersion: (id) => openFolderFlow(id, 'add'),
  changeFolder: (id) => openFolderFlow(id, 'change'),
  // The option cards, Cancel and Start of the folder dialog ignore the first ~400 ms like the confirm
  // dialog's: the second click of a double-click on "Change..." must not pick an option.
  relocMode: (m) => {
    const r = S.agentCfg.reloc;
    if (!r || Date.now() - (r.at || 0) < CONFIRM_ARM_MS || r.mode === m || !relocModes(r).some((o) => o.id === m && !o.dis)) return;
    r.mode = m;
    render();
    // "remove" needs no folder; the other two re-check the folder already typed under the new mode. The two ways of a
    // flow ask the agent the same re-point: its verdict stands.
    if (!r.flow) relocCheck();
  },
  relocClose: () => {
    const r = S.agentCfg.reloc;
    if (r && Date.now() - (r.at || 0) < CONFIRM_ARM_MS) return;
    clearTimeout(relocTimer); S.agentCfg.reloc = null; render();
  },
  relocCheck: () => relocCheck(),
  relocPick: async () => {
    const r = S.agentCfg.reloc;
    if (!r || r.mode === 'remove') return;
    try {
      const p = await rpc('pickFolder', { current: String(r.dir || '').trim() || r.from || '', title: t('agentCfg.reloc.pickTitle') });
      if (p && p.path && S.agentCfg.reloc === r) { r.dir = p.path; render(); relocCheck(); }
    } catch (e) { showToast(e.message, 'bad'); }
  },
  // The dialog closes and the job's log takes over (the console, the Overview row's WORKING). The
  // check's verdict on the drive lets the console offer Stop from the first second of a cross-drive
  // copy — also for an unknown verdict (null), which the agent copies too; a copy the check did not
  // foresee (a rename refused across mount points) brings Stop with its own log line (server.log). A re-point
  // that takes along what a download left beside the old folder (the check's `carry`) may copy it to another
  // drive: Stop is offered for it too. "Download" of a flow is the re-point, then the download, one after the
  // other — without the download's question: the dialog named its size (chainAdd).
  relocStart: () => {
    const r = S.agentCfg.reloc;
    if (!r || Date.now() - (r.at || 0) < CONFIRM_ARM_MS || !relocStartOk(r)) return;
    const q = relocQuery(r), res = r.check.res;
    const carries = q.mode === 'repoint' && Array.isArray(res.carry) && res.carry.some((e) => e && e.fits);
    const cross = (q.mode === 'move' && res.sameVolume !== true) || carries;
    clearTimeout(relocTimer); S.agentCfg.reloc = null;
    // (the folder as the agent's check spelt it — normalised, absolute —: the one its re-point answers with)
    const dir = typeof res.to === 'string' && res.to.trim() ? res.to : q.dir;
    if (r.flow && r.mode === 'download') { chainAdd({ version: q.version, dir, repoint: true, fetch: true, stop: carries }); return; }
    runServerJob('relocate', q.version, { dir: q.dir, mode: q.mode });
    const j = S.srvJob;
    if (j && j.running && j.kind === 'relocate' && j.version === q.version) { j.crossDrive = cross; render(); }
  },
  // Stop a cross-drive copy (POST /server/relocate {version, cancel:true} — a plain call that gets
  // through while the job runs): the job ends as "Stopped on request", the copy is removed and the old
  // folder stays in use. One click, dead until the job's state changes — fetchCancel's rule.
  relocCancel: async (id) => {
    const j = S.srvJob;
    if (!j || !j.running || j.kind !== 'relocate' || j.cancelSent || !jobHere() || String(j.version) !== String(id)) return;
    j.cancelSent = true; render();
    try {
      const r = await rpc('server.relocate.cancel', { version: id });
      if (!(r && r.stopping)) { if (S.srvJob === j) j.cancelSent = false; checkServers(true); }
    } catch (e) { if (S.srvJob === j) j.cancelSent = false; showToast(e.message, 'bad'); render(); }
  },

  // ── admin: local Windows agent ──
  localAgentStart: async () => { try { await rpc('localagent.start'); showToast(t('toast.localAgentStarted'), 'ok'); } catch (e) { showToast(e.message, 'bad'); } loadLocalAgent(); },
  localAgentStop: async () => { try { await rpc('localagent.stop'); } catch (e) { showToast(e.message, 'bad'); } loadLocalAgent(); },
  localAgentAutostart: async () => { const la = S.localAgent.status; try { await rpc('localagent.autostart', { enabled: !(la && la.autostart) }); } catch (e) { showToast(e.message, 'bad'); } loadLocalAgent(); },
  localAgentFirewall: async () => { try { await rpc('localagent.firewall'); showToast(t('toast.firewallOpened'), 'ok'); } catch (e) { showToast(e.message, 'bad'); } loadLocalAgent(); },
  localAgentOpenLog: () => rpc('localagent.openLog').catch((e) => showToast(e.message, 'bad')),
  // Start screen → Server Admin mode with the agent installed on this PC. Transactional on the backend
  // (health first, a start only when needed — under the Fiddler guard —, the config token validated
  // against /status, and only then the same connection the install saves); the reply is admin.login's
  // shape, so the tail is loginEnter's.
  localAgentEnter: async () => {
    if (S.localAgent.busy) return;
    // No render() here: the button says what it is doing by itself (see syncLocalAgentButtons),
    // and a render() would rebuild the whole start screen under the pointer mid-click.
    S.localAgent.busy = true; syncLocalAgentButtons();
    try {
      const r = await rpc('localagent.enter');
      enterShell(r && r.state);
      showToast(t('toast.localAgentEntered', { host: S.serverAddr.host }), 'ok');
      if (r && r.warning) showToast(r.warning, 'bad');
    } catch (e) { showToast(e.message, 'bad'); }
    // enterShell() has already re-rendered on success (another screen); on a failure the start
    // screen is untouched and only the two buttons go back to normal.
    S.localAgent.busy = false; syncLocalAgentButtons();
  },
  // The confirm names what goes (the agent folder and its size, the state file, the mirrored hotpatch
  // files) and what stays (the docker stacks, the databases, every player's progress). The facts come
  // from one localagent.status read — allowed from the start screen too, the rpc has no admin gate.
  // The only elevated step (the firewall rules: the agent's port, and the game ports' of a server other PCs join —
  // serverRules) is the dialog's checkbox, off by default, offered while either is there.
  localAgentUninstall: async () => {
    if (S.localAgent.busy) return;
    let st;
    try { st = await rpc('localagent.status'); } catch (e) { return showToast(e.message, 'bad'); }
    S.localAgent.status = st;
    const dirs = (st && st.dirs) || {};
    const stacks = S.stacks.map((s) => dirs[s.id]).filter(Boolean).join(', ') || t('confirm.localAgentUninstall.noStacks');
    S.confirm = {
      title: t('confirm.localAgentUninstall.title'),
      body: t('confirm.localAgentUninstall.body', { dir: st.root || '', stacks, size: st.rootBytes == null ? '' : t('confirm.localAgentUninstall.size', { mb: mb(st.rootBytes) }) }),
      yes: t('confirm.localAgentUninstall.yes'),
      act: 'localAgentUninstallGo', arg: null, at: Date.now(),
      checkbox: (st.firewallRule || st.serverRules) ? { label: t('confirm.localAgentUninstall.firewall', { port: st.port }), checked: false } : null,
    };
    render();
  },
  localAgentUninstallGo: async (_, ticked) => {
    S.localAgent.busy = true; render();
    try {
      const r = await rpc('localagent.uninstall', { firewall: ticked === true });
      S.localAgent.status = null;
      if (r && r.state) applyInit(r.state);
      const rep = (r && r.report) || {};
      const left = Array.isArray(rep.leftovers) ? rep.leftovers.length : 0;
      showToast(left ? t('toast.localAgentUninstalledPartial', { n: left }) : t('toast.localAgentUninstalled'), left ? 'bad' : 'ok');
      if (rep.firewallError) showToast(rep.firewallError, 'bad');
      // The firewall rules Relic added that are still there — the agent's port, the game ports' (keptRules: the box left
      // unticked, or a removal Windows did not make) —, each named with the line that removes it, to copy into a
      // terminal opened as administrator.
      const keptRules = Array.isArray(rep.keptRules) ? rep.keptRules.filter((n) => typeof n === 'string' && n.trim()) : [];
      if (keptRules.length) {
        S.notice = { title: t('localAgent.keptRules.title'), body: t('localAgent.keptRules.body'),
          rows: keptRules.map((n) => ({ label: n, value: t('localAgent.keptRules.cmd', { rule: n }), copy: true })) };
      }
      // The launcher was in admin mode on that very agent and has no token left: the backend dropped
      // the mode (like "Change mode or server"), so the shell has nothing to show — back to the panel.
      if (S.mode === '' && S.screen !== 'login') { S.login.panel = 'admin'; S.login.address = S.serverAddr.host || S.login.address; S.screen = 'login'; onceForget('login:'); }
    } catch (e) { showToast(e.message, 'bad'); }
    S.localAgent.busy = false; render();
    if (S.screen === 'server') loadLocalAgent();
    loadAgentBuild();
  },

  // ── agent update ──
  // The status strip's banner and the Agent tab's "This server's agent" card: the agent of the admin server.
  // The one on this PC is updated in place (agentUpdateLocal); a remote one over SSH, whose details the
  // overlay asks for each time — the launcher stores none.
  agentUpdate: () => {
    if (agentUpdateBusy() || srvBusy() || !srvAgentUpdateOffered()) return;
    if (srvAgentIsLocal()) return ACTIONS.agentUpdateLocal();
    S.agentUpdate = { target: 'remote', phase: 'form', lines: [], error: '', warning: '', srvTarget: srvTarget(),
      form: { sshHost: S.serverAddr.host || '', sshPort: 22, sshUser: 'root', sshPassword: '', sshKeyPath: '', sshKeyPassphrase: '', sudoPassword: '', upgradeForce: false } };
    render();
  },
  // The agent on this PC — from the start screen, its card, or the banner: one question, then the
  // overlay follows the run. With the files already the shipped ones only the restart is left.
  agentUpdateLocal: () => {
    if (agentUpdateBusy()) return;
    const restart = !localAgentUpdate();
    S.confirm = { title: t(restart ? 'confirm.agentUpdate.titleRestart' : 'confirm.agentUpdate.title'),
      body: t(restart ? 'confirm.agentUpdate.bodyRestart' : 'confirm.agentUpdate.body'),
      yes: t(restart ? 'confirm.agentUpdate.yesRestart' : 'confirm.agentUpdate.yes'), act: 'agentUpdateLocalGo', arg: null, at: Date.now() };
    render();
  },
  agentUpdateLocalGo: async () => {
    if (agentUpdateBusy()) return render();
    const u = S.agentUpdate = { target: 'local', phase: 'run', lines: [], error: '', warning: '', form: null, srvTarget: srvTarget() };
    S.localAgent.busy = true;
    render();
    // The outcome comes with the agentupdate.done / agentupdate.error event; only a call the backend
    // refuses outright (Fiddler runs, another server operation is in progress) fails here.
    try { await rpc('localagent.update'); }
    catch (e) { if (S.agentUpdate === u) agentUpdateFailed(e.message); }
  },
  agentUpdateStart: async () => {
    const u = S.agentUpdate;
    if (!u || u.target !== 'remote' || u.phase !== 'form') return;
    u.phase = 'run'; u.lines = []; u.error = ''; u.warning = '';
    render();
    try { await rpc('agent.update', u.form); }
    catch (e) { if (S.agentUpdate === u) agentUpdateFailed(e.message); }
  },
  // Back to the SSH details after a failure, keeping what was typed; only the log goes.
  agentUpdateBack: () => {
    const u = S.agentUpdate;
    if (!u || u.target !== 'remote' || u.phase !== 'error') return;
    u.phase = 'form'; u.error = ''; u.lines = [];
    render();
  },
  agentUpdateClose: () => {
    const u = S.agentUpdate;
    if (!u || u.phase === 'run') return;
    S.agentUpdate = null;
    render();
  },
  agentUpdateForce: () => {
    const u = S.agentUpdate;
    if (!u || u.phase !== 'form') return;
    u.form.upgradeForce = !u.form.upgradeForce;
    render();
  },
  agentUpdatePickKey: async () => {
    const u = S.agentUpdate;
    try {
      const r = await rpc('pickFile', { title: t('deploy.ssh.pickKeyTitle'), filter: t('deploy.ssh.pickKeyFilter') });
      if (r && r.path && S.agentUpdate === u && u && u.phase === 'form') { u.form.sshKeyPath = r.path; render(); }
    } catch (e) { showToast(e.message, 'bad'); }
  },

  // ── install-agent overlay ──
  // autoDir = the folder fields the form filled in by itself ({<folder field>: path}) — see deployStack.
  // keepEnvAuto (set by prefillWindowsForm) = the "Keep .env settings" toggle shows what the agent on
  // this PC has, not something the admin chose. keepEnvWait (prefillWindowsForm too) = true while the
  // form waits for that agent's status. The Windows form also holds what it learnt of this PC: stored /
  // kinds = the folders the installed agent's config names and what stands there (prefillWindowsForm),
  // extractor = a 7z tool here (true / false; unknown until answered), checks = this PC's look at each row's
  // folder ({<version>: {dir, res}}, deployCheckSoon).
  deployOpen: () => { S.agentInstall = { open: true, target: 'linux', form: deployDefaultForm('linux'), autoDir: {}, running: false, lines: [], done: false, error: null, result: null }; render(); },
  deployOpenWindows: () => {
    const a = S.agentInstall = { open: true, target: 'windows', form: deployDefaultForm('windows'), autoDir: {}, running: false, lines: [], done: false, error: null, result: null };
    render(); prefillWindowsForm(); deployAskExtractor(a);
  },
  // A folder the form filled in by itself follows the target (the other OS's path would be refused, or
  // land somewhere odd): the new target's conventional path while its row downloads. Anything the admin
  // typed or picked never equals the recorded value and stays as it is. "Keep .env settings" taken from
  // the agent on this PC (keepEnvAuto) is that agent's fact, not another server's: it goes back to "not
  // stated", and the Windows prefill takes it again — Keep too: a kept folder is a fact of the agent on this
  // PC, and on another target its row reads as one the admin has not set: Not now (a download into the kept folder
  // too, unless a folder of the row's own field is there to take it), the choice the Windows prefill sets to Keep again.
  deployTarget: (tg) => {
    const a = S.agentInstall; if (!a) return;
    a.target = tg;
    const auto = a.autoDir || (a.autoDir = {});
    S.stacks.forEach((s) => {
      const fld = s.dirField;
      if (tg !== 'windows' && a.stored && typeof a.stored[s.id] === 'string') {
        const c = a.form[s.stackField];
        if (c === 'keep' || (c === 'download' && !String(a.form[fld] || '').trim())) a.form[s.stackField] = 'later';
      }
      if (!auto[fld] || a.form[fld] !== auto[fld]) { delete auto[fld]; return; }
      if (a.form[s.stackField] !== 'download') { a.form[fld] = ''; delete auto[fld]; }
      else a.form[fld] = auto[fld] = stackFolder(s, tg === 'windows', tg === 'windows');
    });
    if (a.keepEnvAuto) { delete a.form.keepEnv; a.keepEnvAuto = false; }
    a.checks = {};
    render();
    if (tg === 'windows') { prefillWindowsForm(); deployAskExtractor(a); S.stacks.forEach((s) => deployCheckSoon(a, s, true)); }
  },
  // A row's choice (data-arg "<stackField>:<choice>"). Download now fills a blank folder field with the conventional
  // folder of the target — /home/<v>_live on Linux, <the games' folder>\servers\<v>_live on this PC's Windows target
  // (stackFolder; the agent creates it; Browse still picks another) — and records it (autoDir); leaving Download now
  // takes a folder filled that way back, unless the admin changed it. I have it fills nothing: that folder must already
  // hold the extracted package, and a made-up path would only fail the install's check. A kept folder stays as it is
  // whatever is chosen; Download now is taken only for a version with a package (a kept folder: only while it holds no
  // server yet), Keep only for a kept folder.
  deployStack: (arg) => {
    const a = S.agentInstall; if (!a || a.running || a.done || a.error) return;
    const i = String(arg).lastIndexOf(':'), k = String(arg).slice(0, i), choice = String(arg).slice(i + 1);
    const s = S.stacks.find((x) => x.stackField === k);
    if (!s || !STACK_CHOICES.includes(choice)) return;
    const kept = deployKept(a, s);
    const allowed = kept ? choice === 'keep' || (choice === 'download' && deployKeptEmpty(a, s))
      : choice === 'have' || choice === 'later' || (choice === 'download' && !!s.downloadable);
    if (!allowed) return;
    const f = a.form, before = deployChoice(a, s);
    f[k] = choice;
    const now = deployChoice(a, s);
    if (!kept && now !== before) {
      const fld = s.dirField, auto = a.autoDir || (a.autoDir = {});
      if (now === 'download' && !String(f[fld] || '').trim()) f[fld] = auto[fld] = stackFolder(s, a.target === 'windows', a.target === 'windows');
      else if (before === 'download' && auto[fld] && f[fld] === auto[fld]) { f[fld] = ''; delete auto[fld]; }
    }
    deployCheckSoon(a, s, true);
    render();
  },
  deployToggle: (k) => {
    const a = S.agentInstall; if (!a) return;
    const f = a.form; f[k] = !f[k];
    // "Keep .env settings" is the admin's own choice from here on, and an ON always states something.
    // An off states something only on the Windows form, whose toggle speaks for the agent on this PC:
    // there it is sent as false and switches the option off. It is "not stated" again (the field
    // removed) on any other target — that form knows nothing of the box, and off only means the box
    // keeps the value it stores — and on the Windows form while the installed agent's status is still on
    // its way (keepEnvWait): a false left behind would keep the Windows prefill from taking that agent's
    // value and go out as "switch it off".
    if (k === 'keepEnv') {
      a.keepEnvAuto = false;
      if (!f.keepEnv && (a.target !== 'windows' || a.keepEnvWait)) delete f.keepEnv;
    }
    render();
  },
  deployGenToken: () => { const a = new Uint8Array(24); crypto.getRandomValues(a); S.agentInstall.form.token = Array.from(a).map((b) => b.toString(16).padStart(2, '0')).join(''); render(); },
  deployPickKey: async () => {
    try { const r = await rpc('pickFile', { title: t('deploy.ssh.pickKeyTitle'), filter: t('deploy.ssh.pickKeyFilter') }); if (r && r.path) { S.agentInstall.form.sshKeyPath = r.path; render(); } }
    catch (e) { showToast(e.message, 'bad'); }
  },
  // The picker's title follows the row's choice: an extracted package to point at (I have it), or the folder the agent
  // will download and install the server into (Download now). The folder picked is looked at on this PC.
  deployPickDir: async (k) => {
    const a = S.agentInstall; if (!a) return;
    const s = S.stacks.find((x) => x.dirField === k);
    const title = s && deployChoice(a, s) === 'download' ? t('deploy.stacks.pickTitleInstall') : t('deploy.stacks.pickTitle');
    try {
      const r = await rpc('pickFolder', { current: a.form[k] || '', title });
      if (r && r.path && S.agentInstall === a) { a.form[k] = r.path; if (s) deployCheckSoon(a, s, true); render(); }
    } catch (e) { showToast(e.message, 'bad'); }
  },
  deployClose: () => {
    const a = S.agentInstall;
    S.agentInstall = null;
    // Installed from the start screen: the backend already saved the connection and switched to admin
    // mode — closing the summary enters the launcher instead of dropping the user back on the panel.
    if (a && a.done && S.screen === 'login' && S.mode === 'admin' && S.serverConfigured) {
      S.login.panel = null; S.login.token = ''; S.login.agentPort = '';
      // No status reset here: a.done is only set by deploy.done, which already started a fresh read.
      // The jobs this install left (a re-point, a download) run or wait: the Server page is where their log is.
      const chained = (S.fetchChain && S.fetchChain.steps.length) || (jobRunning() && (S.srvJob.kind === 'fetch' || S.srvJob.kind === 'relocate'));
      if (chained) ACTIONS.serverOverview(); else ACTIONS.go('library');
      return;
    }
    render();
  },
  // Back to the form after a failure, keeping everything typed (host, paths, IPs — and the token, so a
  // retry does not mint a second one the box may already be using). Only the log goes.
  deployBack: () => {
    const a = S.agentInstall; if (!a) return;
    a.error = null; a.done = false; a.running = false; a.lines = []; a.result = null;
    render();
  },
  deployStart: async () => {
    const a = S.agentInstall; if (!a) return;
    a.lines = []; a.error = null; a.done = false; a.running = true; render();
    // chain: true = this page runs the jobs deploy.done hands it (`chain`: a re-point, then a download, one after the
    // other — chainNext).
    // keepEnv = "Keep .env settings" as a tri-state: true / false are written (GIO_KEEP_ENV=1 / 0), null
    // leaves the key out, so the box keeps what it stores. The Windows form sends what it states — it is
    // pre-filled from the agent installed here, so its "off" is a real answer (deployToggle leaves none
    // in the form that was made on another target, or before that agent's status arrived); the Linux
    // form knows nothing of the box, so only a ticked toggle is sent. While it is on, the values it
    // leaves as the server has them (DEPLOY_KEPT) go out empty, whatever the form still holds for them.
    const f = a.form, win = a.target === 'windows';
    const keepEnv = win ? (typeof f.keepEnv === 'boolean' ? f.keepEnv : null) : (f.keepEnv === true ? true : null);
    const payload = Object.assign({ target: a.target, chain: true }, f, { keepEnv });
    // One choice per stack under its own payload name (stackField) and the folder it names under its own (dirField):
    // '' for Not now and Keep — a kept folder is the one the target's configuration stores, which the backend reads.
    S.stacks.forEach((s) => { payload[s.stackField] = deployChoice(a, s); payload[s.dirField] = deployDir(a, s); });
    if (keepEnv) DEPLOY_KEPT.forEach((k) => { payload[k] = ''; });
    try { await rpc(win ? 'localagent.install' : 'agent.install', payload); }
    catch (e) { a.running = false; a.error = e.message; render(); }
  },
};

// Defaults of the install-agent form: the address already typed on the start screen for the SSH
// host, the standard listen address, and a fresh token.
// sshPort must be seeded as a NUMBER: setModel only parses an edit when the value it replaces is
// already one, so a '22' string would stay a string all the way to the backend, where a JSON number read
// throws "The requested operation requires an element of type 'Number'" (Backend.Int parses it instead).
// sshHost goes through hostOf(): the start-screen address is documented to allow ":port", and SSH
// must not be pointed at "host:21000".
// Each stack (S.stacks) brings two fields under its own names: its folder (dirField, blank) and its
// choice (stackField, 'later' = Not now: a fresh form sets up no version — the Server page adds versions).
// muipKey empty = the agent picks a random sign key when it prepares the stack.
// mysqlPassword / flaskKey empty = the backend leaves the key out of the config it writes, so a value
// the box already stores is kept; none stored = the agent picks a random one when it prepares a stack
// that still has the vendor's published value. All three are typed here or left empty: never
// pre-filled from the agent already installed (prefillWindowsForm), never logged.
// keepEnv ("Keep .env settings") is not seeded: undefined = not stated, which deployStart sends as null
// (the box keeps the GIO_KEEP_ENV it stores) and which lets the Windows prefill take the installed
// agent's value.
// The conventional folder of a server stack (a row of S.stacks, its folder name <folder>) on a target — one default
// for the install form, Quick Start and the folder dialog: the placeholder of a folder field and the value a blank
// field gets when its row turns to Download now (the download target); never a default for I have it (see deployStack).
// - The Windows agent on THIS PC (local: the install form's Windows target, Quick Start, the folder dialog when the
//   admin server is the agent on this PC): <root>\servers\<folder>, beside the games — root = the games' folder (the
//   one Quick Start's plan names, else RelicSettings.InstallRoot), its trailing separators dropped. Only while that root
//   is printable ASCII on a drive letter with no quote (the install refuses a folder holding ' or "), at most 100
//   characters long and outside Relic's own data folder (…\AppData\Local\Relic, which the uninstall deletes whole —
//   the servers' databases with it); any other root gives <its drive, else C:>:\relic_servers\<folder>.
// - A Windows agent on another PC (the folder dialog of that server): C:\relic_servers\<folder> — never this PC's path.
// - Linux: /home/<folder>.
function stackFolder(s, win, local, root) {
  if (!win) return `/home/${s.folder}`;
  if (!local) return `C:\\relic_servers\\${s.folder}`;
  const given = root == null || String(root).trim() === '' ? S.settings.installRoot : root;
  const r = String(given == null ? '' : given).trim().replace(/\//g, '\\').replace(/\\+$/, '');
  const relicData = /\\AppData\\Local\\Relic(\\|$)/i.test(r);
  if (/^[A-Za-z]:\\[\x20-\x7e]*$/.test(r) && !/['"]/.test(r) && r.length <= 100 && !relicData) return `${r}\\servers\\${s.folder}`;
  const drive = /^([A-Za-z]):/.exec(r);
  return `${drive ? drive[1].toUpperCase() : 'C'}:\\relic_servers\\${s.folder}`;
}
// The choices of a stack row (the form's stackField, the installer's GIO_STACK_<nn>): download the ready-made package
// now, have it (the folder holds the extracted server), later (not now) — and keep, on the Windows form, for a version
// the agent on this PC already has a folder for.
const STACK_CHOICES = ['download', 'have', 'later', 'keep'];
// The folder the agent on this PC already uses for a stack (localagent.status dirs, read by prefillWindowsForm) — '' on
// another target and for a version it has none for. Such a row keeps it: a folder changes only on the Server page.
const deployKept = (a, s) => (a && a.target === 'windows' && a.stored && typeof a.stored[s.id] === 'string' ? a.stored[s.id] : '');
// A kept folder that holds no server yet (absent or empty: localagent.status kinds) can take the download — of a
// version with a package.
const deployKeptEmpty = (a, s) => !!s.downloadable && !!a.kinds && (a.kinds[s.id] === 'absent' || a.kinds[s.id] === 'empty');
// The row's choice as drawn and sent: keep only for a kept folder (kept unless a download into it is chosen and
// possible), download only for a version with a package; anything else is not now.
function deployChoice(a, s) {
  const c = a.form[s.stackField];
  if (deployKept(a, s)) return c === 'download' && deployKeptEmpty(a, s) ? 'download' : 'keep';
  if (c === 'download') return s.downloadable ? 'download' : 'later';
  return c === 'have' ? 'have' : 'later';
}
// The folder a row sends and the summary names: the kept one, else the field; '' for not now and keep (the backend
// takes a kept folder from the configuration the target stores).
function deployDir(a, s) {
  const c = deployChoice(a, s);
  if (c === 'later' || c === 'keep') return '';
  return deployKept(a, s) || String(a.form[s.dirField] || '').trim();
}
// The bytes of the archive a download of the row takes on the install's target: on Windows without a 7z tool the .zip,
// when the package has one — a server without one takes it as well, which the Linux hint names —, else the
// catalogue's archive.
function deployArchive(a, s) {
  const size = Math.max(0, Number(s.size) || 0), zip = Math.max(0, Number(s.zipSize) || 0);
  return a.target === 'windows' && a.extractor === false && zip > 0 ? zip : size;
}
// About what such a download needs free on the folder's drive: the archive, the server once unpacked (the catalogue's
// figure, else three times the archive — the agent's own rule) and the agent's default reserve (4 GiB).
const DEPLOY_RESERVE = 4 * 1024 * 1024 * 1024;
function deployNeed(a, s) {
  const arc = deployArchive(a, s), unpacked = Math.max(0, Number(s.extractedSize) || 0);
  return arc + (unpacked > 0 ? unpacked : 3 * arc) + DEPLOY_RESERVE;
}
// The hint of a Download now row (escaped): the sizes this target will really take. Linux: the catalogue's archive,
// the unpacked size and the disk it needs, and the .zip a server without a 7z tool takes. Windows, by what this PC has
// (localagent.folderCheck / localagent.status `extractor`): a 7z tool — the same; none — the .zip, or, for a package
// that comes as a .7z only, that the install refuses without 7-Zip; not known yet — both archives, the agent taking
// the one this PC can unpack.
function deployDownloadHint(a, s) {
  const size = Math.max(0, Number(s.size) || 0), zip = Math.max(0, Number(s.zipSize) || 0), unpacked = Math.max(0, Number(s.extractedSize) || 0);
  const hint = (arc) => (unpacked > 0 ? T('deploy.stack.downloadHint', { size: sizeGb(arc), unpacked: sizeGb(unpacked), need: sizeGb(arc + unpacked + DEPLOY_RESERVE) })
    : T('deploy.stack.downloadHintNoUnpacked', { size: sizeGb(arc) }));
  if (a.target !== 'windows') return hint(size) + (zip > 0 && s.archive === '7z' ? ' ' + T('deploy.stack.zipFallback', { zipSize: sizeGb(zip) }) : '');
  if (a.extractor === true) return hint(size);
  if (a.extractor === false) return zip > 0 ? hint(zip) + ' ' + T('deploy.stack.zipThisPc', { zipSize: sizeGb(zip) }) : T('deploy.stack.needs7zip');
  return zip > 0 && s.archive === '7z' ? T('deploy.stack.archiveEither', { size: sizeGb(s.size), zipSize: sizeGb(zip) }) : hint(size);
}
// A Download now folder that this PC's check says holds a server already (the Windows form): nothing is downloaded —
// the install uses the server there as it is, as for I have it (LocalAgent.Install skips that download).
function deployHasServer(a, s) {
  const ch = a.target === 'windows' && a.checks && a.checks[s.id];
  return !!ch && deployChoice(a, s) === 'download' && ch.dir === deployDir(a, s) && !!ch.res && ch.res.kind === 'stack';
}
// What this PC's check of a Download now / I have it folder says (the Windows form; escaped): why the agent here could
// not use it, a folder that holds other files (a download refuses it), a server already (nothing is downloaded) or no
// server (I have it), and the free space of its drive — short of what a download needs said in the warning colour (the
// agent's own check, when the download starts, decides).
function deployCheckLine(a, s) {
  if (a.target !== 'windows') return '';
  const c = deployChoice(a, s), dir = deployDir(a, s), ch = a.checks && a.checks[s.id];
  if ((c !== 'download' && c !== 'have') || !dir || !ch || ch.dir !== dir || !ch.res) return '';
  const r = ch.res, line = (html, cls) => `<small class="stkline${cls ? ' ' + cls : ''}">${html}</small>`;
  if (typeof r.problem === 'string' && r.problem.trim()) return line(esc(r.problem), 'bad');
  let out = '';
  const holds = deployHasServer(a, s);
  if (c === 'download' && r.kind === 'other' && !deployKept(a, s)) out += line(T('deploy.stack.checkOther'), 'bad');
  if (holds) out += line(T('deploy.stack.checkHasServer'));
  if (c === 'have' && typeof r.kind === 'string' && r.kind !== 'stack' && r.kind !== 'unknown') out += line(T('deploy.stack.checkNotStack'), 'bad');
  if (isBytes(r.free) && typeof r.drive === 'string' && r.drive) {
    const need = deployNeed(a, s);
    out += c === 'download' && !holds && r.free < need ? line(T('deploy.stack.checkShort', { drive: r.drive, free: sizeGb(r.free), need: sizeGb(need) }), 'warn')
      : line(T('deploy.stack.checkFree', { drive: r.drive, free: sizeGb(r.free) }));
  }
  return out;
}
// The summary above the install button (escaped): what is set up now — downloaded (with the size the target takes),
// used where it is, kept — and what is left for later; the Linux form cannot tell a fresh server from one that already
// has versions (the box keeps the folders it uses), and says so whatever the rows choose.
function deploySummaryHtml(a) {
  const win = a.target === 'windows', now = [], later = [];
  S.stacks.forEach((s) => {
    const c = deployChoice(a, s), kept = deployKept(a, s), dir = deployDir(a, s) || '—';
    if (c === 'download' && !deployHasServer(a, s)) now.push(t('deploy.summary.download', { version: s.id, size: sizeGb(deployArchive(a, s)), dir }));
    else if (c === 'have' || c === 'download') now.push(t('deploy.summary.use', { version: s.id, dir }));
    else if (c === 'keep') now.push(t('deploy.summary.keep', { version: s.id, dir: kept }));
    else later.push(s.id);
  });
  if (!now.length) return win ? T('deploy.summary.none') : T('deploy.summary.noneLinux');
  const rest = later.length ? ' ' + (win ? T('deploy.summary.later', { list: later.join(', ') }) : T('deploy.summary.laterLinux', { list: later.join(', ') }))
    : win ? '' : ' ' + T('deploy.stack.laterLinux');
  return T('deploy.summary.now', { list: now.join('; ') }) + rest;
}
// The install button's label (plain text): which versions it downloads, when it downloads any.
function deployStartText(a) {
  const dl = S.stacks.filter((s) => deployChoice(a, s) === 'download' && !deployHasServer(a, s)).map((s) => s.id);
  return dl.length ? t('deploy.start.withDownload', { list: dl.join(', ') }) : a.target === 'windows' ? t('deploy.start.windows') : t('deploy.start.linux');
}
// The parts of the install form that follow what is typed, drawn again in place while a field has the caret (a render
// would take it): each row's check line and download hint, the summary, the button's label.
function syncDeploy() {
  const a = S.agentInstall;
  if (!a || a.running || a.done || a.error) return;
  if (!typingNow()) { render(); return; }
  S.stacks.forEach((s) => {
    const chk = document.querySelector(`#app [data-dchk="${s.id}"]`); if (chk) chk.innerHTML = deployCheckLine(a, s);
    const hint = document.querySelector(`#app [data-dhint="${s.id}"]`); if (hint) hint.innerHTML = deployDownloadHint(a, s);
  });
  const sum = document.getElementById('deploy-summary'); if (sum) sum.innerHTML = deploySummaryHtml(a);
  const b = document.querySelector('#app [data-act="deployStart"]'); if (b) b.textContent = deployStartText(a);
}
// The Windows form looks at a row's folder on THIS PC (localagent.folderCheck): debounced while it is typed, at once
// when the form filled or picked it. One request per folder; an answer for another folder than the one on screen, or for
// a form that was closed, is dropped. Every answer also says whether this PC has a 7z tool.
const deployCheckTimers = {};
function deployCheckSoon(a, s, now) {
  if (!a || a.target !== 'windows') return;
  const c = deployChoice(a, s), dir = c === 'download' || c === 'have' ? deployDir(a, s) : '';
  a.checks = a.checks || {};
  clearTimeout(deployCheckTimers[s.id]);
  if (!dir) { delete a.checks[s.id]; return; }
  const prev = a.checks[s.id];
  if (prev && prev.dir === dir && prev.res) return;
  a.checks[s.id] = { dir, res: null };
  deployCheckTimers[s.id] = setTimeout(() => deployCheck(a, s, dir), now ? 0 : 600);
}
async function deployCheck(a, s, dir) {
  let res = null;
  try { res = await rpc('localagent.folderCheck', { dir, version: s.id }); } catch (e) { res = null; }
  const ch = a.checks && a.checks[s.id];
  if (S.agentInstall !== a || !ch || ch.dir !== dir) return;
  ch.res = res && typeof res === 'object' ? res : null;
  if (ch.res && typeof ch.res.extractor === 'boolean') a.extractor = ch.res.extractor;
  syncDeploy();
}
// Whether this PC has a 7z tool, asked once when the Windows form opens (a folderCheck without a folder): it decides
// which archive a download takes. Not known until then — the hint names both.
async function deployAskExtractor(a) {
  if (!a || a.target !== 'windows' || a.extractorAsked) return;
  a.extractorAsked = true;
  let r = null;
  try { r = await rpc('localagent.folderCheck', { dir: '', version: '' }); } catch (e) { r = null; }
  if (S.agentInstall !== a || !r || typeof r.extractor !== 'boolean') return;
  a.extractor = r.extractor;
  syncDeploy();
}
// A field of the install form was typed in: a stack's folder is looked at again, and the summary and the button follow.
function deployTyped(key) {
  const a = S.agentInstall, s = a && S.stacks.find((x) => x.dirField === key);
  if (!s) return;
  deployCheckSoon(a, s);
  syncDeploy();
}
// The form fields "Keep .env settings" takes out of the form while it is on — what the agent then
// leaves as each server has it: the bind IP (the .env's OUTER_IP) and the three secrets it would
// otherwise replace (the MySQL root password and the Flask key of that file, the MUIP sign key of
// muipserver's own config). Drawn dead and empty (agentInstallOverlay), sent empty (deployStart).
const DEPLOY_KEPT = ['bindIp', 'muipKey', 'mysqlPassword', 'flaskKey'];
function deployDefaultForm(target) {
  const f = { sshHost: hostOf(S.login.address) || S.serverAddr.host || '', sshPort: 22, sshUser: 'root', sshPassword: '', sshKeyPath: '', sshKeyPassphrase: '', sudoPassword: '' };
  S.stacks.forEach((s) => { f[s.dirField] = ''; f[s.stackField] = 'later'; });
  Object.assign(f, { bindIp: '', advertisedIp: '', advertisedHost: '', listen: '0.0.0.0:18080', token: '', muipHost: '', muipKey: '', mysqlPassword: '', flaskKey: '',
    serverName: '', region: 'dev_docker', autostart: true, upgradeForce: false });
  const a = new Uint8Array(24); crypto.getRandomValues(a); f.token = Array.from(a).map((b) => b.toString(16).padStart(2, '0')).join('');
  if (target === 'windows') { f.sshHost = ''; }
  return f;
}

// The Windows form pre-filled from the agent already installed on this PC (localagent.status: its
// stack folders, its bind IP and MUIP host, its public IP / DNS name, its name and its listen address),
// so a re-install / upgrade does not ask for them again — the form writes GIO_BIND_IP and GIO_MUIP_HOST
// even when blank, and a blank one would drop what the Agent settings card saved. A version that agent
// has a folder for reads Keep with that folder (a re-install never changes a configured folder: it
// changes only on the Server page), Download now beside it while the folder holds no server yet (kinds);
// whether this PC has a 7z tool (extractor) decides which archive a download takes. The other fields:
// blank fields only — never over something the admin typed; the
// listen address only while the form still holds the standard one — and the status is read here when
// the Server page has not loaded it (the start screen). "Keep .env settings" follows the installed
// agent's (keepEnv) the same way: only while the form does not state it, never over the admin's toggle.
// That read takes seconds (interpreter probes, a walk of the agent folder) and the form is live
// meanwhile: keepEnvWait marks it, so an off clicked before the answer is there states nothing
// (deployToggle) and the answer is still taken; an ON clicked meanwhile stays the admin's.
async function prefillWindowsForm() {
  const a = S.agentInstall;
  if (!a || !S.isWindows) return;
  let st = S.localAgent.status;
  if (!st) {
    if (!(S.localAgent.init && S.localAgent.init.installed)) return;
    // One request per form at a time: a call made while it is in flight (a switch to the Linux form
    // and back) leaves the form to the reply already on its way.
    if (a.keepEnvWait) return;
    a.keepEnvWait = true;
    try { st = await rpc('localagent.status'); } catch (e) { return; } finally { a.keepEnvWait = false; }
    if (S.agentInstall !== a) return; // the form was closed meanwhile
    S.localAgent.status = st;
  }
  if (!st || !st.installed) return;
  const f = a.form;
  const blank = (k) => !String(f[k] || '').trim();
  const given = (v) => typeof v === 'string' && v.trim() !== '';
  // The folder the installed agent's config names for each stack (dirs, by version id) and what stands there (kinds).
  const dirs = st.dirs && typeof st.dirs === 'object' ? st.dirs : {}, kinds = st.kinds && typeof st.kinds === 'object' ? st.kinds : {};
  a.stored = {}; a.kinds = {};
  S.stacks.forEach((s) => {
    if (!given(dirs[s.id])) return;
    a.stored[s.id] = dirs[s.id].trim();
    if (typeof kinds[s.id] === 'string') a.kinds[s.id] = kinds[s.id];
    if (!(f[s.stackField] === 'download' && deployKeptEmpty(a, s))) f[s.stackField] = 'keep';
  });
  if (typeof st.extractor === 'boolean') a.extractor = st.extractor;
  // The configured bind IP itself (bindIp); a backend without it: the dialled host, when not loopback.
  if (typeof st.bindIp === 'string') { if (blank('bindIp') && given(st.bindIp)) f.bindIp = st.bindIp.trim(); }
  else if (blank('bindIp') && st.host && st.host !== '127.0.0.1') f.bindIp = String(st.host);
  if (blank('muipHost') && given(st.muipHost)) f.muipHost = st.muipHost.trim();
  if (blank('advertisedIp') && given(st.advertisedIp)) f.advertisedIp = st.advertisedIp.trim();
  if (blank('advertisedHost') && given(st.advertisedHost)) f.advertisedHost = st.advertisedHost.trim();
  if (blank('serverName') && given(st.serverName)) f.serverName = st.serverName.trim();
  if (String(f.listen || '').trim() === '0.0.0.0:18080' && given(st.listen)) f.listen = st.listen.trim();
  // Only for the Windows target: the reply may land after a switch to the Linux form, whose toggle is
  // about another box (keepEnvAuto: deployTarget takes the value back on such a switch). The Windows
  // form has the installed agent's answer from here on and waits for nothing — also when that status
  // came from the Server page's own read while this form's request is still in flight —, so an off made
  // on it is a statement (deployToggle).
  if (a.target === 'windows') {
    a.keepEnvWait = false;
    if (f.keepEnv === undefined && typeof st.keepEnv === 'boolean') { f.keepEnv = st.keepEnv; a.keepEnvAuto = true; }
  }
  // (a folder a row downloads into or uses is looked at on this PC: deployCheckSoon)
  S.stacks.forEach((s) => deployCheckSoon(a, s, true));
  render();
}

// The tail every way into the shell shares (the start screen's Enter, "Use the agent on this PC"):
// forget the panel, apply the reply's init state — another server or mode is a new status epoch,
// started inside applyInit (srvRetarget); re-entering the same one keeps its still-valid status and
// just refreshes it — and open the Library.
function enterShell(d) {
  // An install form opened on the start screen and left there must not follow us into the shell.
  if (S.agentInstall && !S.agentInstall.running) S.agentInstall = null;
  S.login.token = ''; S.login.agentPort = ''; S.login.panel = null; S.login.saving = null; onceForget('login:');
  const moved = applyInit(d);
  ACTIONS.go('library');
  if (!moved) refreshServersQuiet();
}

// ── the jobs an agent install or "Add version…" leaves for the Server page (S.fetchChain) ──
// One job at a time — the agent runs one operation anyway —: a step's re-point first when it has one (POST
// /server/relocate {dir, mode: repoint}), then its download; the next job starts once the one before it ended AND a
// status read names no job running on the server (every status read calls chainNext). A step waits for the version to
// stand as it was made for (chainJudge): a status read after it was queued that names another folder for it ends the
// chain. A chain made for another server is dropped, and so is one whose job failed or was stopped: the version's own
// row — Download, Resume, Add version… — is the way on. A start the agent refused because another operation ran keeps
// its step first: it is tried again at the next status read that names none, CHAIN_BUSY_TRIES times at most.
const CHAIN_BUSY_TRIES = 10;
// How many admin status reads have landed (ingestStatus). A step remembers the count it was last judged at (gen: when it
// was queued, or when its re-point ended), so only a status read after that is taken to say something new about it.
let srvGen = 0;
// AgentInstallPlan.SameFolder: trimmed; on Windows '/' read as '\', the trailing '\' dropped and the letter case
// ignored, a full path on a drive letter first spelled the way the agent answers it (NormalWindowsFolder, Windows'
// own rules: a doubled separator and a "." segment dropped, ".." taking the segment before it, a segment followed
// by a separator losing a lone trailing period, the end of the path its trailing periods and spaces; one holding a
// NUL as it is); elsewhere the trailing '/' dropped, exact; a blank is no folder.
function sameFolder(a, b, win) {
  const x = String(a == null ? '' : a).trim(), y = String(b == null ? '' : b).trim();
  if (!x || !y) return false;
  if (win) {
    const spelled = (d) => {
      const s = d.replace(/\//g, '\\'), m = /^([A-Za-z]:)\\([\s\S]*)$/.exec(s);
      if (!m || s.includes('\0')) return s.replace(/\\+$/, '');
      const segs = m[2].split('\\'), last = segs[segs.length - 1], kept = [];
      segs.forEach((g, i) => {
        if (g === '' || g === '.') return;
        if (g === '..') kept.pop();
        else if (i < segs.length - 1) kept.push(g.replace(/([^.])\.$/, '$1'));
        else if (g.replace(/[. ]+$/, '')) kept.push(g.replace(/[. ]+$/, ''));
      });
      // A path ending in "." or ".." ends in the segment before it, which then loses its trailing periods and spaces.
      if ((last === '.' || last === '..') && kept.length) {
        const h = kept.pop().replace(/[. ]+$/, '');
        if (h) kept.push(h);
      }
      return m[1] + (kept.length ? '\\' + kept.join('\\') : '');
    };
    return spelled(x).toUpperCase() === spelled(y).toUpperCase();
  }
  return x.replace(/\/+$/, '') === y.replace(/\/+$/, '');
}
// The steps of a deploy.done, read safely ({version, dir, repoint, fetch} each); a backend that names only `fetch`
// (the plain downloads) gives one download step per version.
function chainStepsOf(d) {
  if (d && Array.isArray(d.chain)) {
    return d.chain.filter((s) => s && typeof s === 'object' && typeof s.version === 'string' && s.version && (s.repoint === true || s.fetch === true))
      .map((s) => ({ version: s.version, dir: typeof s.dir === 'string' ? s.dir : '', repoint: s.repoint === true, fetch: s.fetch === true, stop: false }));
  }
  return d && Array.isArray(d.fetch) ? d.fetch.filter((v) => v != null && String(v)).map((v) => ({ version: String(v), dir: '', repoint: false, fetch: true, stop: false })) : [];
}
// A step for this server's chain — the one under way takes it last, else it starts one — and the next job, if none runs.
// The step is made for the version as the status on screen names it (from: its folder, '' = none — the folder dialog's
// Start made sure the agent's check names the same: relocStartOk).
function chainAdd(step) {
  const c = S.fetchChain;
  if (step.from === undefined) step.from = chainDirOf(step.version) || '';
  step.gen = srvGen;
  if (c && c.target === srvTarget()) c.steps.push(step);
  else S.fetchChain = { target: srvTarget(), win: srvWindows(), steps: [step], phase: null, busyTries: 0, running: null };
  chainNext();
  render();
}
function chainNext() {
  const c = S.fetchChain;
  if (!c) return;
  if (!c.steps.length || c.target !== srvTarget()) { S.fetchChain = null; return; }
  if (c.running || jobRunning() || !S.serverConfigured || (S.srv.info && S.srv.info.busy)) return;
  const step = c.steps[0];
  if (srvGen > (step.gen || 0) && !chainJudge(c, step)) { chainNext(); return; }
  if (step.repoint && c.phase !== 'fetch') runChained(c, 'relocate', step, { dir: step.dir, mode: 'repoint' });
  else if (step.fetch) runChained(c, 'fetch', step);
  else { c.steps.shift(); c.phase = null; c.busyTries = 0; chainNext(); }
}
// The folder the latest admin status names for a version: '' = none (the agent answers it has no folder for it), the
// folder, or null when that status names neither (no entry, an entry without the facts, no status read).
function chainDirOf(id) {
  const e = S.srv.info && !S.srv.info.public && S.srv.versions ? S.srv.versions[id] : null;
  if (!isVersionEntry(e)) return null;
  if (e.configured === false) return '';
  return typeof e.dir === 'string' && e.dir.trim() ? e.dir.trim() : null;
}
// The head step judged by a status read after it was last judged: its version may no longer stand as the step was made
// for — another admin, another launcher or gio_agent.py --relocate gave it a folder, or a server, meanwhile. Only what
// that status names counts (chainDirOf); a version it says nothing of is left to the agent, which refuses what does not
// fit. A re-point the version has had already (it uses the step's folder) is not made again. A version that uses
// another folder than the step expects — none for an install's step or "Add version…", the one it had for "Change
// folder…" — ends the chain with a word, and so does a server in the folder a change away from it was asked for: the
// version's row is the way on. A download whose folder holds a server by now is not started. True = the step goes on
// as it is; false = the chain changed (the step went, or the chain was dropped).
function chainJudge(c, step) {
  step.gen = srvGen;
  const at = chainDirOf(step.version);
  if (at === null) return true;
  const id = step.version, from = step.from || '', there = S.srv.versions[id].present === true;
  const drop = (text) => { S.fetchChain = null; showToast(text, 'bad'); return false; };
  const done = () => { c.steps.shift(); c.phase = null; c.busyTries = 0; return false; };
  if (step.repoint && c.phase !== 'fetch') {
    if (at && sameFolder(at, step.dir, c.win)) {
      step.at = at;
      if (!step.fetch) return done();
      c.phase = 'fetch';
    } else if (from ? !(at && sameFolder(at, from, c.win)) : at !== '') {
      return drop(step.fetch ? t('server.chain.folderMismatch', { version: id, to: at || '—', dir: step.dir })
        : t('server.chain.folderKept', { version: id, to: at || '—', dir: step.dir }));
    } else if (from && there) return drop(t('server.chain.serverThere', { version: id, dir: at }));
    else return true;
  }
  const want = step.at || step.dir;
  if (!step.fetch || !want || !at) return true;
  if (!sameFolder(at, want, c.win)) return drop(t('server.chain.folderMismatch', { version: id, to: at, dir: want }));
  if (!there) return true;
  showToast(t('server.chain.serverThere', { version: id, dir: at }), 'ok');
  return done();
}
// runServerJob for the first step of the chain. A refusal of the backend's own job slot (backend.server.jobBusy: the
// job before it is still being wound up there) is tried once more after 1.5 s instead of a toast, the console of the
// job before it kept meanwhile; any other refusal drops the chain.
async function runChained(c, kind, step, extra, again) {
  if (S.fetchChain !== c || c.running || jobRunning() || !S.serverConfigured) return;
  const version = step.version, prev = S.srvJob;
  // (prev: the console a start the agent refuses before it wrote anything gives back its place to — chainJobFailed)
  c.running = { kind, version }; c.prev = prev;
  S.srvJob = { kind, version, target: srvTarget(), host: S.serverAddr.host, purge: false, progress: '', enabled: null, lines: [], running: true, error: '',
    crossDrive: kind === 'relocate' && step.stop === true };
  // (a step started by a status read while a field has the caret is drawn once the caret has left it)
  renderOrOwe();
  try { await rpc('server.' + kind, Object.assign({ version }, extra || {})); }
  catch (e) {
    const mine = S.fetchChain === c && !!c.running && c.running.kind === kind;
    if (mine) c.running = null;
    if (mine && !again && e.message === t('backend.server.jobBusy')) {
      if (S.srvJob && S.srvJob.kind === kind && S.srvJob.version === version && !S.srvJob.lines.length) S.srvJob = prev;
      renderOrOwe();
      setTimeout(() => runChained(c, kind, step, extra, true), 1500);
      return;
    }
    if (mine) S.fetchChain = null;
    if (S.srvJob) { S.srvJob.running = false; S.srvJob.error = e.message; }
    srvLine(t('server.log.error', { error: e.message }));
    showToast(e.message, 'bad');
    renderOrOwe();
  }
}
// The job the chain runs is this one (server.done / server.error of it).
const chainRuns = (c, j) => !!c && !!c.running && c.running.kind === j.kind && c.running.version === String(j.version);
// server.done of a job: the chain's own goes on. A re-point whose copy of what followed the version was stopped — by
// this launcher's Stop, or by another's (the agent's "(stopped on request)" lines) — ends the chain: the admin asked
// to stop, and what stayed beside the old folder is in the log. After its re-point the folder the version uses now must
// be the step's — else the download does not start (another folder was set meanwhile) and the chain is dropped with a
// toast; the folder as the agent names it is the one its download is judged by from then on (step.at, chainJudge).
function chainJobDone(j, res) {
  const c = S.fetchChain;
  if (!chainRuns(c, j)) return;
  c.running = null; c.busyTries = 0;
  const step = c.steps[0];
  if (!step) { S.fetchChain = null; return; }
  if (j.kind === 'relocate') {
    if (j.cancelSent || (j.lines || []).some((l) => /\(stopped on request\)/.test(String(l)))) { S.fetchChain = null; return; }
    const to = res && typeof res.to === 'string' ? res.to : '';
    if (!sameFolder(to, step.dir, c.win)) {
      S.fetchChain = null;
      showToast(t('server.chain.folderMismatch', { version: step.version, to: to || '—', dir: step.dir }), 'bad');
      return;
    }
    step.at = to.trim(); step.gen = srvGen;
    if (step.fetch) c.phase = 'fetch'; else { c.steps.shift(); c.phase = null; }
  } else { c.steps.shift(); c.phase = null; }
  chainNext();
}
// server.error of a job: the chain's own drops the chain, unless the agent refused its start because another operation
// ran (its sentence "Another operation is already running…", also inside the launcher's "answered with error 409"):
// the step stays first for the next status read that names no job.
function chainJobFailed(j) {
  const c = S.fetchChain;
  if (!chainRuns(c, j)) return;
  c.running = null;
  if (/\bAnother operation is already running\b/.test(String(j.error || '')) && ++c.busyTries <= CHAIN_BUSY_TRIES) return;
  S.fetchChain = null;
}

// Every way an install job ends for the UI — closed, abandoned, cancelled, refused at start —
// forgets the same facts in one place, so a voices job can never leave its title or its request
// behind for the next install.
function clearActiveInstall() {
  S.installing = false; S.activeInstallId = null; S.activeInstallLocal = null;
  S.activeInstallVoices = null; S.activeInstallReq = null;
}

// ── "Prepare the server" chooser ──
// The three provisioning choices, with their texts (translation keys, translated when drawn). Only
// `default` is a wipe — every account created on the version goes with the import — hence its red Yes.
const PREPARE_MODES = {
  default: { label: 'confirm.prepare.opt.default', sub: 'confirm.prepare.opt.default.sub', body: 'confirm.prepare.body.default', yes: 'confirm.prepare.yes.default', danger: true },
  keep: { label: 'confirm.prepare.opt.keep', sub: 'confirm.prepare.opt.keep.sub', body: 'confirm.prepare.body.keep', yes: 'confirm.prepare.yes.keep', danger: false },
  fixes: { label: 'confirm.prepare.opt.fixes', sub: 'confirm.prepare.opt.fixes.sub', body: 'confirm.prepare.body.fixes', yes: 'confirm.prepare.yes.fixes', danger: false },
};
// kind = 'setup' (the Overview's Prepare the server) | 'provision' (the Players tab's Reset / re-apply the
// progress…). A version whose payload ships no save has no default account to import: the chooser offers
// the other two choices and opens on "keep". The pathfinding row rides on setup alone (the agent applies it BEFORE
// the bootstrap renders the compose file): its default is the file's current state, else the agent's
// default for this stack, else its configured one, else on — and off, whatever those say, for a stack
// with no navmesh file to load: the dialog sends the box as an explicit choice, and ticked it would
// switch on a service that has nothing to load.
// The hotpatch row (the second box) rides on setup too, and only where the agent says the stack's fixes are
// available, on by default and not yet decided on record: a decided stack keeps its choice (the Content tab
// changes it), and a version without a default sends no key, so the agent's own answer stands.
function prepareDialog(id, kind) {
  const f = srvFacts(id);
  const info = S.srv.info || {};
  const hp = f && f.hotpatch;
  const hotpatchBox = kind === 'setup' && !!hp && hp.available === true && hp.defaultEnabled === true && hp.decided === false;
  const modes = Object.keys(PREPARE_MODES).filter((m) => m !== 'default' || !(f && f.noSave));
  const account = catalogueAccountOf(id) || '—';
  const bodyFor = {}, yesFor = {};
  modes.forEach((m) => { bodyFor[m] = t(PREPARE_MODES[m].body); yesFor[m] = t(PREPARE_MODES[m].yes); });
  // The agent replaces the vendor's published MySQL root password / Flask key / stack internal password
  // whenever it prepares a stack that still has one — in EVERY mode, the two whose texts promise the
  // database is not touched included (no data is; the PASSWORDS change): said in each body while the
  // status names one. Not for an agent that keeps .env settings (srvKeepEnv): it replaces none.
  if (!srvKeepEnv() && f && (f.mysqlPassword === 'vendor' || f.flaskKey === 'vendor' || f.internalPassword === 'vendor')) modes.forEach((m) => { bodyFor[m] += ' ' + t('confirm.prepare.vendorSecrets'); });
  // Nothing for the service to load: no navmesh file in the stack's folder (an answered 0 — an agent that
  // does not send the count says nothing) and no bundle the agent counted. Only a counted bundle lifts
  // the rule: this very Prepare installs it. One the agent could not read (null) lifts nothing — the
  // agent's own default for the stack takes it for none too, and the dialog's box goes out as an
  // explicit choice that outranks that default.
  const noNavmesh = pfNothingToLoad(f);
  const pfDefault = f && f.pathfindingDefault !== null ? f.pathfindingDefault : (typeof info.pathfindingDefault === 'boolean' ? info.pathfindingDefault : true);
  const pf = noNavmesh ? false : (f && f.pathfinding !== null ? f.pathfinding : pfDefault);
  // The line under the box of such a stack; it also says where the data the stack lacks can be downloaded
  // when the pathfinding card's data line offers that download (navdataOffered) — not on a server without
  // a tool that unpacks its archive, where that line names what to install and has no button.
  const v = versionById(id);
  const noNavmeshSub = t('server.prepare.pathfindingNoNavmesh')
    + (v && navdataOffered(v, f) ? ' ' + t('server.prepare.pathfindingNavdata') : '');
  S.confirm = {
    title: t('confirm.prepare.title', { id }), wide: true,
    options: modes.map((m) => ({ id: m, label: t(PREPARE_MODES[m].label), sub: t(PREPARE_MODES[m].sub, { account }), danger: PREPARE_MODES[m].danger })),
    selected: modes[0], bodyFor, yesFor,
    checkbox: kind === 'setup' ? { label: t('server.prepare.pathfinding'), sub: noNavmesh ? noNavmeshSub : t('server.prepare.pathfindingSub'), checked: pf } : null,
    // A second box, never the pathfinding row: the confirm dialog carries one checkbox per field. No `ask` on this
    // dialog — the hotpatch question is not asked again at Yes.
    hotpatch: hotpatchBox ? { label: t('server.prepare.hotpatch'), sub: t('server.prepare.hotpatchSub'), checked: true } : null,
    act: 'prepareGo', arg: kind + ':' + id,
    at: Date.now(), // starts the anti-double-click arming window
  };
  render();
}

async function loadPolicy() {
  if (!isAdminMode()) return;
  S.policy.loading = true; render();
  try { S.policy.data = await rpc('account.policy.get'); }
  catch (e) { S.policy.data = null; }
  S.policy.loading = false;
  if (S.screen === 'server' || S.screen === 'settings') render();
}
// What the 'server' screen's cards read, for an entry from Game rules through the tab bar: only what is not loaded
// yet — a card that holds an answer keeps it, with what the admin typed into it — and the status, quietly (no
// "checking" state; the strip and the rows follow the answer).
function adminAreaLoad() {
  if (!S.serverConfigured || !isAdminMode()) return;
  refreshServersQuiet();
  if (!S.policy.data && !S.policy.loading) loadPolicy();
  if (!S.localAgent.status) loadLocalAgent();
  if (!S.hotpatch.data && !S.hotpatch.loading) loadHotpatch(hotpatchCur());
  S.versions.forEach((v) => { if (!S.acctNew.tpl[v.id]) loadAcctTemplates(v.id); });
  if (!S.agentCfg.data && !S.agentCfg.loading) loadAgentCfg();
  acctsOpen();
}

// GET /server/secrets again, without a word, after a job of the version whose values the Secrets card
// holds (srvJobSettled): a Prepare / Start may have replaced the vendor's published MySQL root password,
// Flask key or stack internal password with private ones, and the card — "Show current
// values" and its Copy buttons too — would go on offering a password that no longer works. What the
// admin has typed stays (the form is not touched) and nothing is drawn over a field being typed in. A
// failed read drops the values: the card is back to "Load current values", and Load — which keeps the
// form as well — draws the typed values again with the loaded ones. The last request wins (a Load
// pressed meanwhile is one), and only for the card it was sent for: the same server (srvRetarget makes
// a new S.secrets), the same version tab, values still loaded (an Apply or a tab switch drops them).
let secretsSeq = 0;
async function reloadSecretsQuiet(version) {
  const s = S.secrets, seq = ++secretsSeq, target = srvTarget();
  const mine = () => S.secrets === s && seq === secretsSeq && !!s.data && s.version === version && srvTarget() === target;
  if (!isAdminMode() || !mine()) return;
  let d = null;
  try { d = await rpc('server.secrets.get', { version }); } catch (e) { /* unreachable: d stays null */ }
  if (!mine()) return;
  s.data = d && typeof d === 'object' ? d : null;
  // The "Show current values" window of that version, when it is open, follows — or closes with them.
  if (S.notice && S.notice.secrets === version) { if (s.data) S.notice.rows = secretsRows(s.data); else S.notice = null; }
  if (S.screen === 'server' && !typingNow()) render();
}

// GET /server/templates of one version for the "Create a player account" card: which progress
// templates are imported on that stack (with their labels), and for the others whether the import
// failed or the agent has no save file to import. The last request per version wins, and
// only under the target it was sent to — the rule of loadHotpatch; the counter is module-wide so a
// reset S.acctNew (srvRetarget) can never re-issue a number an older, still in-flight request holds.
// A failed read leaves list null: the card falls back to the admin /status records.
let acctTplSeq = 0;
async function loadAcctTemplates(id) {
  if (!isAdminMode() || !S.serverConfigured || !id) return;
  const a = S.acctNew, seq = ++acctTplSeq, target = srvTarget();
  const mine = () => S.acctNew === a && a.tplSeq[id] === seq && srvTarget() === target;
  a.tplSeq[id] = seq;
  a.tpl[id] = Object.assign({}, a.tpl[id], { loading: true });
  let list = null;
  try {
    const r = await rpc('server.templates', { version: id });
    // imported: the agent's own verdict; one that does not send it is read from the record itself.
    if (r && Array.isArray(r.templates)) list = r.templates.filter((x) => x && typeof x.id === 'string' && x.id).map((x) => ({
      id: x.id, label: typeof x.label === 'string' && x.label ? x.label : x.id,
      imported: typeof x.imported === 'boolean' ? x.imported : !!(x.createdAt && !x.error),
      present: x.present !== false, failed: !!x.error,
    }));
  } catch (e) { /* an older agent, or unreachable: list stays null */ }
  if (!mine()) return;
  a.tpl[id] = { list, loading: false };
  if (S.screen === 'server' && !typingNow()) render();
}
const loadAcctTemplatesAll = () => S.versions.forEach((v) => loadAcctTemplates(v.id));

// GET /server/hotpatch of one version. Silent by default (an agent without the route answers 404 and
// the section simply offers "Load status"); loud = the user pressed the button and wants the reason.
// The last REQUEST wins, and only under the target it was sent to (the rule of the status reads, see
// srvSeq): otherwise the previous server's late reply would be drawn — voice rows, ticks and a live Mirror
// button — under the new server's name and keyed as the new server's, and two replies of one server
// could land out of order. srvRetarget bumps the seq as well, so A → B → A drops A's old reply too.
let hotpatchSeq = 0;
async function loadHotpatch(id, loud) {
  if (!isAdminMode() || !S.serverConfigured || !id) return;
  const seq = ++hotpatchSeq, target = srvTarget();
  const mine = () => seq === hotpatchSeq && srvTarget() === target;
  S.hotpatch.version = id; S.hotpatch.loading = true; if (S.screen === 'server') render();
  try { const d = await rpc('server.hotpatch.get', { version: id }); if (mine()) { S.hotpatch.data = d; syncVoiceSel(id, d, target); } }
  catch (e) { if (mine()) { S.hotpatch.data = null; if (loud) showToast(e.message, 'bad'); } }
  if (!mine()) return; // the newer request owns `loading` and the redraw
  S.hotpatch.loading = false;
  if (S.screen === 'server') render();
}
// While one of the two hotpatch jobs runs, this card is the only place its progress shows as numbers
// ("Files on the server", the per-language "cached x/y"): the job log scrolls past, the counters would
// stand still until the job ended or the admin pressed Refresh. So re-read them on a short interval
// while such a job downloads — quietly: no "checking" state (the Refresh button would blink disabled
// every tick) and no redraw unless the agent's answer actually changed or while the user is typing,
// the rule of refreshServersQuiet. The tick is a no-op the rest of the time, and while the card is not on
// screen (only the Content tab shows its counters): GET /server/hotpatch stats every mirrored file and walks
// every voice-pack folder on the box, which is not something to ask for every few seconds when nothing on
// screen would move. One request at a time (hotpatchQuiet), and the seq/target
// discipline of loadHotpatch applies unchanged — another server's late reply is never drawn here.
const HOTPATCH_POLL_MS = 5000;
let hotpatchQuiet = false;
async function refreshHotpatchQuiet() {
  const id = hotpatchCur();
  if (!isAdminMode() || !S.serverConfigured || !id || S.screen !== 'server' || adminTabOn() !== 'content') return;
  if (hotpatchQuiet || S.hotpatch.loading || !hotpatchJobRunning(id)) return;
  const shown = hotpatchDataOf(id);
  if (!shown || !shown.available) return; // nothing loaded (or no manifest): the card has no numbers to move
  const seq = ++hotpatchSeq, target = srvTarget();
  const before = JSON.stringify(S.hotpatch.data);
  hotpatchQuiet = true;
  try {
    const d = await rpc('server.hotpatch.get', { version: id });
    if (seq !== hotpatchSeq || srvTarget() !== target) return; // a tab switch / a loud Refresh owns the card now
    S.hotpatch.data = d; syncVoiceSel(id, d, target);
  } catch (e) {
    return; // a failed poll asserts nothing: the card keeps the numbers it has, the job log says the rest
  } finally {
    hotpatchQuiet = false;
  }
  if (JSON.stringify(S.hotpatch.data) !== before && S.screen === 'server' && !typingNow()) render();
}
// After a server job the mirror state may have changed (hotpatch itself, but also start / provision /
// netfix re-apply the advertisement): re-read it when the section had been loaded.
// A finished voice job — ours (server.done / server.error) or one /status stopped naming (the agent's
// 'hotpatch-voice': started elsewhere, or ours after the link was lost — trackHotpatchBusy) — re-takes
// the ticks from the server's selection; a failed one of ours keeps them unless the selection changed
// on the server (refused before it was saved — not enough disk, say — the admin unticks one and retries).
// Never a tab switch: a voice job runs for an hour, the admin is likely on the other version's tab by
// then, and loadHotpatch names the tab before its reply is in. That tab's chip reloads it anyway.
function refreshHotpatchAfterJob(kind, version, ok) {
  const h = S.hotpatch;
  const voice = kind === 'hotpatch.voice' || kind === 'hotpatch-voice';
  if (kind !== 'hotpatch' && !voice) { if (h.data && h.version) loadHotpatch(h.version); return; }
  if (voice && h.voiceStop === String(version)) h.voiceStop = null; // that job's state changed: Stop is live again
  if (String(hotpatchCur()) !== String(version)) return;
  if (voice && ok) h.voiceKey = '';
  loadHotpatch(hotpatchCur());
}
// The hotpatch / voice job the admin /status names, remembered so that its END is seen: server.done /
// server.error exist only for a job this launcher started and still follows. Without this the card of
// a job started elsewhere (or gone on after the link was lost) would stay as it was before the job, and its
// Apply would silently drop a language another admin had selected meanwhile (languages[] is a full replace).
function trackHotpatchBusy(busy) {
  const h = S.hotpatch, prev = h.busy, kind = busy && busy.kind;
  if (prev && prev.kind === 'hotpatch-voice' && BESIDE_VOICE_JOBS[kind]) return; // masked, not over
  const now = kind === 'hotpatch' || kind === 'hotpatch-voice'
    ? { kind, version: String(busy.version == null ? '' : busy.version), id: String(busy.id == null ? '' : busy.id) } : null;
  h.busy = now;
  if (!prev || (now && now.kind === prev.kind && now.version === prev.version && now.id === prev.id)) return;
  // A job of ours followed on THIS server: its server.done / server.error re-reads the card. One
  // followed on another server says nothing about this card — its events never re-read it.
  if (jobHere()) return;
  refreshHotpatchAfterJob(prev.kind, prev.version, true);
}

// GET /agent/config for the "Agent settings" card. The last request wins, and only under the target it
// was sent to and for the card object it was sent for (srvRetarget / deploy.done replace it) — the rule
// of loadHotpatch. A background re-read keeps what the admin has typed and not saved (agentCfgApply). A
// failed read drops the data like the other cards (loadPolicy): the card offers Retry with the reason.
let agentCfgSeq = 0;
async function loadAgentCfg() {
  if (!isAdminMode() || !S.serverConfigured) return;
  const c = S.agentCfg, seq = ++agentCfgSeq, target = srvTarget();
  const mine = () => seq === agentCfgSeq && S.agentCfg === c && srvTarget() === target;
  c.loading = true;
  if (S.screen === 'server' && !c.data && !typingNow()) render();
  try {
    const d = await rpc('agent.config.get');
    if (!mine()) return;
    agentCfgApply(d, true);
    c.error = '';
  } catch (e) {
    if (!mine()) return;
    c.data = null; c.form = {}; c.error = e.message || String(e);
  }
  c.loading = false;
  if (S.screen === 'server' && !typingNow()) render();
}
// The agent's settings read names each version's folder as it was at that read (the Server folders card, the folder
// dialog's current folder). A status that names another folder for a version — a relocation made elsewhere: another
// admin, another launcher, gio_agent.py --relocate — has them read again, once for each such difference (the same
// difference seen again reads nothing more). Only on the Server page, over a read that came back with the folders.
function agentCfgResync() {
  const c = S.agentCfg, d = c.data;
  if (S.screen !== 'server' || !d || d.tooOld || !d.versions || typeof d.versions !== 'object' || c.loading || c.restarting) return;
  const diff = Object.keys(S.srv.versions || {}).filter((id) => {
    const e = S.srv.versions[id], cv = d.versions[id];
    if (!isVersionEntry(e) || typeof e.configured !== 'boolean' || !cv || typeof cv !== 'object') return false;
    const now = e.configured && typeof e.dir === 'string' ? e.dir.trim() : '', was = cv.configured && typeof cv.dir === 'string' ? cv.dir.trim() : '';
    return now !== was;
  }).map((id) => `${id}=${S.srv.versions[id].dir || ''}`).join('|');
  if (!diff || diff === c.resynced) return;
  c.resynced = diff;
  loadAgentCfg();
}

async function loadLocalAgent() {
  if (!S.isWindows || !isAdminMode()) return;
  try { S.localAgent.status = await rpc('localagent.status'); }
  catch (e) { S.localAgent.status = null; }
  if (S.screen === 'server') render();
}

function sendCommand(category, value) {
  const version = cmdVersionId() || (S.versions[0] && S.versions[0].id);
  return rpc('command.send', { version, category, value, uid: S.cmdUid.trim() });
}

// Pause and cancel share one backend mechanic (the download is resumable, so "pause" is a cancel
// whose partial files the next start picks up) — the stored intent decides what install.cancelled
// does. The intent doubles as the in-flight flag: it disables both buttons until the event lands.
async function stopInstall(intent) {
  if (!S.installing || S.installPaused || S.installStopIntent || S.installDone || S.installError) return;
  S.installStopIntent = intent; render();
  try {
    const r = await rpc('install.cancel');
    // Nothing was running (the install finished while the click was in flight) — undo the intent;
    // the install.done/install.error that raced us paints the real outcome.
    if (!r || !r.cancelling) { S.installStopIntent = null; render(); }
  } catch (e) { S.installStopIntent = null; showToast(e.message, 'bad'); render(); }
}

// Every send goes through here: the same preconditions, plus an in-flight flag set BEFORE the
// first await — a double-click on "Send set" would otherwise fire ten GM commands.
async function guardedSend(fn) {
  if (!S.serverConfigured) return showToast(t('toast.serverNotConfigured'), 'bad');
  // Player mode: only when the server's policy opens GM commands to players (the backend routes the
  // call through the agent's public endpoint, which refuses otherwise).
  if (!isAdminMode() && !playerCommandsAllowed()) return showToast(t('toast.commandsAdminOnly'), 'bad');
  if (!S.cmdUid.trim()) return showToast(t('toast.enterUid'), 'bad');
  if (S.sending) return showToast(t('toast.alreadySending'), 'bad');
  S.sending = true; render();
  try { await fn(); }
  catch (e) { addLog('bad', t('cmd.log.error', { error: e.message })); }
  finally { S.sending = false; render(); }
}

// Never fatal: an older backend has no profile.status case, and the settings screen must still open.
// The last read asked wins (profSeq) — and so does a status profile.changed carried after it was asked.
let profSeq = 0;
async function loadProfileStatus() {
  const seq = ++profSeq;
  let p = null;
  try { p = await rpc('profile.status'); }
  catch (e) { p = null; }
  if (seq !== profSeq) return;
  S.profile = p;
  if (S.screen === 'settings') renderOrOwe(); // (a session's end reads it again: never under a caret)
}

// Epoch of the status requests: three paths write S.srv from independent rpcs (checkServers,
// freshServerCheck, refreshServersQuiet), and the replies arrive in network order, not request
// order — a stuck poll (60 s HTTP timeout) would otherwise overwrite, minutes later, the fresh data
// of a Check pressed in between. The last REQUEST wins: any reply from a superseded epoch is dropped.
// A change of target (srvRetarget) is an epoch boundary too.
let srvSeq = 0;
const srvSeqCurrent = (seq) => seq === srvSeq;
// The seq of the status request that currently owns S.srv (0 = none). A quiet poll skips only while
// that owner is still CURRENT: a superseded request (another server's, say) never blocks a new one,
// and each request clears the mark only if it is still its own.
let srvPollSeq = 0;
// No retarget before boot's first applyInit has been applied and the first poll issued.
let srvBooted = false;
// What the status describes: the mode (admin /status vs the public snapshot), the server's host and
// agent port, and whether there is anything to ask at all. Tagged on every request at send time; a
// reply that lands under another target is dropped (defence in depth next to the epoch bump).
const srvTarget = () => [S.mode, S.serverAddr.host, S.serverAddr.agentPort, S.serverConfigured].join('|');

// A new target — another host, agent port, mode, or configured/unconfigured — starts a new epoch.
// Resetting S.srv and calling refreshServersQuiet is not enough: that call returns at once while a poll for
// the PREVIOUS server is still in flight and never bumps srvSeq, so that poll's reply would be ingested
// as the new server's status ("Server offline" under the new name until the next 30 s tick).
// The bump drops every in-flight reply;
// 'checking' (not idle) because a read of the new target starts right away.
function srvRetarget() {
  srvSeq++; srvPollSeq = 0;
  S.srv = { state: S.serverConfigured ? 'checking' : 'idle', error: '', versions: {}, info: null };
  S.pub.status = null;
  S.srvAccount = {}; // the previous server's word on the in-game login: not this one's
  // The jobs an install or "Add version…" left for the box we just left (deploy.done, chainAdd). Starting the
  // next one after a switch would point a version of the NEW server at a folder meant for the old one, or download
  // a multi-GB package onto a server that may already have it — and the job the admin is watching is the old box's.
  S.fetchChain = null;
  // Plaintext secrets of the previous server (MySQL root, the shared internal password, Flask): the
  // card is keyed by version only, so without this the next Load-less render shows box A's passwords
  // under box B's name.
  S.secrets = { version: null, data: null, form: {}, loading: false };
  // Likewise the signup card's last result: it belongs to the server it was created on — and so do the
  // admin card's form, its template reads and the session's list of created accounts (passwords
  // included). A create still in flight keeps S.acctPending, so its late events cannot land in these.
  S.account = accountBlank();
  S.acctNew = acctNewBlank();
  // ...and so is the account list, with what was open on it (a copy waiting for its target, an editor, a copy's
  // question still to come) and the questions it asked about that server's accounts.
  S.accts = acctsBlank();
  if (S.confirm && (S.confirm.act === 'acctsCopyGo' || S.confirm.act === 'acctsPwOpenFor')) S.confirm = null;
  // ...and so is a move's check, with its choices and the question asked over them.
  migrateReset();
  // Game commands a row of that list opened act on the Library's version again — the row was the previous server's.
  if (S.cmdVersion) { S.cmdVersion = null; if (S.screen === 'commands') loadGameData(); }
  // The hotpatch card is the previous server's as well — rows, ticks, a live Mirror button. In-flight
  // reads are dropped by the seq; on the Server screen the new target's card is read right away.
  hotpatchSeq++; S.hotpatch = hotpatchBlank();
  // The admin policy belongs to the server it was read from too -- signups, player commands and the
  // hotpatch mirror source: without this the new server would keep showing the old one's choices
  // until the Server page (its only reader) is opened again.
  S.policy = { data: null, loading: false };
  // The agent-settings card — its file, its folders, an open folder dialog — is the previous agent's too.
  agentCfgSeq++; clearTimeout(relocTimer); S.agentCfg = agentCfgBlank();
  // The gameplay settings' answers and drafts are the previous server's documents. While the Game rules tab
  // is open the status read below names this server's versions and the one on screen is read (twOpen).
  twSeq++; S.tw = twBlank(); twPickerGone();
  // The tab of the area, the account-copy form (another server's account names) and the Logs (another agent's
  // operations) are the previous server's too: the new one opens on the Overview.
  S.adminTab = 'overview'; S.acctCopy = null;
  agentJobsSeq++; agentJobLogSeq++; S.agentJobs = agentJobsBlank();
  refreshServersQuiet();
  // Whether the admin server is the agent on this PC is a fact of the new target (agent.build).
  loadAgentBuild();
  if (S.screen === 'server') { loadHotpatch(hotpatchCur()); loadPolicy(); loadAcctTemplatesAll(); loadAgentCfg(); }
}
const typingNow = () => {
  const a = document.activeElement;
  return !!a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA');
};
// render() at once, or — while a field has the caret — the redraw owed until the caret has left it (renderIfOwed).
function renderOrOwe() { if (typingNow()) renderOwed = true; else render(); }
// The owed redraw, once no field has the caret and no click is half done (a render between a mousedown and its
// mouseup takes the button the click lands on). anyFocus = also over a button the focus is on (the quiet status
// read: its own redraws take that focus too); without it only while the focus is on nothing — a button the keyboard
// or a click moved to keeps it until that read comes.
function renderIfOwed(anyFocus) {
  const a = document.activeElement;
  if (renderOwed && !pointerDown && !typingNow() && (anyFocus === true || !a || a === document.body)) render();
}

// One reader for both shapes server.status can answer with: the admin's full /status (versions[id] =
// {up, present, bootstrapped, servicesDown, ...}) or, in player mode, {public:true, status:{...}} —
// the agent's token-less snapshot (versions[id] = {present, up, healthy, defaultAccount, ...}). Both
// land in S.srv.versions in the admin shape, so the badges and cards read one structure; the public
// snapshot is kept whole in S.pub.status (signup policy, player commands, default accounts).
// statusVersions is the pure half, shared with the PLAY pre-check: null for no reply.
// The public snapshot says only healthy:false, never WHICH services are down: that is `degraded` with an
// empty servicesDown — never a placeholder name, which would leak into the texts as "down ((services))".
// Facts are read only from an entry that is an object (isVersionEntry). Anything else an answer holds
// under a version's id — null, a word, a list — is a broken answer about that version: the id stays
// named, with no fact under it, so the version reads as unverified — neither a made-up "present and
// stopped" nor a version the agent does not list.
const isVersionEntry = (e) => !!e && typeof e === 'object' && !Array.isArray(e);
function statusVersions(st) {
  if (!st) return null;
  const list = st.public ? st.status && st.status.versions : st.versions;
  const versions = {};
  Object.keys(isVersionEntry(list) ? list : {}).forEach((id) => {
    const e = list[id];
    if (!isVersionEntry(e)) { versions[id] = null; return; }
    if (!st.public) { versions[id] = e; return; }
    versions[id] = { up: !!e.up, present: e.present !== false, bootstrapped: true, account: e.defaultAccount || '',
      servicesDown: [], degraded: !!(e.up && e.healthy === false), passwordVerify: !!e.passwordVerify, generation: e.generation,
      // The game server is still loading its data — the snapshot says it only while it is true.
      loading: e.loading === true,
      // {enabled, res, data, silence} — the badge on the library card/hero reads .enabled.
      hotpatch: e.hotpatch && typeof e.hotpatch === 'object' ? e.hotpatch : null };
  });
  return versions;
}
// The reply says nothing about what is RUNNING: the admin /status top-level error (docker compose ls
// failed on the box) or its public projection, statusUnknown (a flag, no text). '' = none.
function statusUnknownOf(st) {
  if (!st) return '';
  if (st.public) return st.status && st.status.statusUnknown ? t('server.public.statusUnknown') : '';
  return st.error ? String(st.error) : '';
}
// The server's own word on the pre-made in-game login of every version it HAS (present), kept per
// target in S.srvAccount (accountOf reads it before the catalogue; srvRetarget clears it). Admin
// /status: `account`, but only when the agent says whether the shipped save is in the database
// (`defaultAccount` true/false — null = its state file is unreadable, no answer; an older agent
// sends no flag and names the manifest account whether or not the save is in, so it is not taken as an
// answer either). Public snapshot: `defaultAccount`, '' meaning NONE. A version the server answers it does
// not have (present: false) loses what an earlier answer said about it: no login of a stack that is gone,
// and no advice on getting one (noLoginHere).
function noteServerAccounts(st) {
  if (!st) return;
  const vs = (st.public ? st.status && st.status.versions : st.versions) || {};
  Object.keys(vs).forEach((id) => {
    const e = vs[id];
    if (isVersionEntry(e) && e.present === false) { delete S.srvAccount[id]; return; }
    if (!isVersionEntry(e)) return;
    if (st.public) S.srvAccount[id] = typeof e.defaultAccount === 'string' ? e.defaultAccount : '';
    else if (typeof e.defaultAccount === 'boolean') S.srvAccount[id] = e.defaultAccount && typeof e.account === 'string' ? e.account : '';
  });
}
function ingestStatus(st) {
  noteServerAccounts(st);
  if (st && st.public) {
    const pub = st.status || {};
    S.pub.status = pub;
    S.srv.versions = statusVersions(st);
    // error: the field the admin reply carries, so the srvDockerError guards (badge, pill) apply to a
    // public snapshot whose docker read failed on the box too.
    S.srv.info = { public: true, agent: pub.agent, name: pub.name, playerCommands: !!pub.playerCommands, error: statusUnknownOf(st) };
    qsTickSoon();
    return;
  }
  S.srv.versions = statusVersions(st) || {};
  S.srv.info = st || null;
  // (one more status read the steps of the chain an install or "Add version…" left may be judged by: chainJudge)
  if (st) srvGen++;
  // A folder this status names otherwise than the agent's settings read: a relocation made elsewhere (agentCfgResync).
  if (st) agentCfgResync();
  // (the policy flags both views read from one place: player commands, open administration)
  if (st && st.policy) {
    S.pub.status = Object.assign(S.pub.status || {}, { playerCommands: !!st.policy.playerCommands, openAdmin: !!st.policy.openAdmin });
    // The Open administration card's toggle reads the loaded policy, which only the Server page's
    // loaders refresh: the poll's word on the flag goes into it too, so the toggle and the status strip's
    // banner agree once another admin flipped the option.
    if (S.policy.data && typeof st.policy.openAdmin === 'boolean') S.policy.data.openAdmin = st.policy.openAdmin;
  }
  if (st) trackHotpatchBusy(st.busy);
  // The gameplay settings, while the Game rules tab is open, read the version they show once the status has
  // named it, and again when that version's stack went up or down (twOpen). The account list of the Players tab
  // likewise (acctsOpen).
  if (st) twOpen();
  if (st) acctsOpen();
  // The jobs an install or "Add version…" left wait for a status that names no job running on the server.
  if (st) chainNext();
  // (and so do Quick Start's steps: qsTickSoon)
  qsTickSoon();
}

// silent: the automatic refresh after an operation. There is a single toast, so a generic toast from
// here would erase the message saying WHAT failed; the error stays written on screen anyway.
async function checkServers(silent) {
  if (!S.serverConfigured) return;
  const seq = ++srvSeq; srvPollSeq = seq;
  const target = srvTarget();
  S.srv.state = 'checking'; S.srv.error = '';
  render();
  try {
    const st = await rpc('server.status');
    if (!srvSeqCurrent(seq) || srvTarget() !== target) return;
    ingestStatus(st);
    S.srv.state = 'ok';
  } catch (e) {
    if (!srvSeqCurrent(seq) || srvTarget() !== target) return;
    // Without fresh data nothing can be asserted: the cards go to "unverified", not OFFLINE (an
    // invented OFFLINE would offer Start for a version that may not even exist on the server).
    // S.pub.status is kept: it is the SAME server's last snapshot (a target change drops it in
    // srvRetarget), the cards already gate on S.srv.state, and clearing it on one failed read would take
    // away the Commands tab, the signup card and the shown in-game login until the next poll.
    S.srv.state = 'error'; S.srv.error = e.message; S.srv.versions = {}; S.srv.info = null;
    if (!silent) showToast(t('toast.serverUnreachable', { error: e.message }), 'bad');
  } finally {
    if (srvPollSeq === seq) srvPollSeq = 0;
  }
  render();
}

// The pre-launch check: fresh and with a short timeout. rpc('server.status') can hang for tens of
// seconds on a host that does not answer (the HttpClient timeout is 60s), and the PLAY button must
// not freeze that long — after the timeout it answers null ("unknown"), and the cached state is
// refreshed anyway when the late rpc actually arrives.
function freshServerCheck(timeoutMs) {
  const seq = ++srvSeq; srvPollSeq = seq; // owning it keeps the 30 s poll from superseding the check
  const target = srvTarget();
  const fetchP = rpc('server.status').then((st) => {
    if (srvTarget() !== target) return null; // another server / mode now: this reply describes neither
    if (!srvSeqCurrent(seq)) return st; // the caller gets its reply, the cache stays the newer request's
    ingestStatus(st);
    S.srv.state = 'ok'; S.srv.error = '';
    if (!typingNow()) render(); // a reply arriving after the timeout would have no other render to show it
    return st;
  }).catch((e) => {
    // The failure of an abandoned request asserts nothing — the cache is not cleared for it; only a
    // still-current one becomes the badge's "unverified" state.
    if (srvSeqCurrent(seq) && srvTarget() === target) {
      S.srv.state = 'error'; S.srv.error = e.message; S.srv.versions = {}; S.srv.info = null; // S.pub.status kept, see checkServers
      if (!typingNow()) render();
    }
    return null;
  }).finally(() => { if (srvPollSeq === seq) srvPollSeq = 0; });
  return Promise.race([fetchP, new Promise((res) => setTimeout(() => res(null), timeoutMs))]);
}

// Background refresh for the library badge. Deliberately different from checkServers: no "checking"
// state (the badge would flicker on every poll) and no render unless something actually changed —
// and not even then over an input the user is typing in (the rewrite in render would steal the focus
// and the caret).
const SRV_POLL_MS = 30000;
async function refreshServersQuiet() {
  // Skips only while a CURRENT request (a Check, the PLAY pre-check, another poll) owns S.srv.
  if (!S.serverConfigured || (srvPollSeq && srvSeqCurrent(srvPollSeq))) return;
  const seq = ++srvSeq; srvPollSeq = seq;
  const target = srvTarget();
  // The signup policy and the player-commands flag count as a change too: they live only in the
  // public snapshot, and a new policy that lands without a redraw would leave the account card offering
  // the old choices (accountCreate refuses a stale form, but the card should show the new one). So does
  // the agent's "Keep .env settings" (srvKeepEnv): the vendor notes of the Access tab and of the
  // Secrets card are worded by it. And open administration: the player banner that offers the way in,
  // the admin's status-strip banner — a flip must not wait for a manual Check. And the agent's build
  // identity: the update notes are drawn from it. And the agent's state file and docker's error: the
  // status strip draws both on every tab. And the facts the Overview's line and the Access and Agent tabs
  // draw: the public address (a DNS name, the address it resolves to, its problem, an answer waiting for its
  // confirming lookup — Re-apply is live only with an address) and which agent answers.
  const drawn = () => JSON.stringify([S.srv.state, S.srv.versions, S.srv.info && S.srv.info.busy,
    S.pub.status && [S.pub.status.signup, S.pub.status.playerCommands, S.pub.status.openAdmin], srvKeepEnv(), agentUpdateKey(),
    S.srv.info && [S.srv.info.state, S.srv.info.error, S.srv.info.advertisedIp, S.srv.info.advertisedHost, S.srv.info.advertisedError,
      S.srv.info.advertisedPending, S.srv.info.agent, S.srv.info.platform, S.srv.info.build]]);
  const before = drawn();
  try {
    const st = await rpc('server.status');
    if (!srvSeqCurrent(seq) || srvTarget() !== target) return;
    ingestStatus(st);
    S.srv.state = 'ok'; S.srv.error = '';
  } catch (e) {
    if (!srvSeqCurrent(seq) || srvTarget() !== target) return;
    S.srv.state = 'error'; S.srv.error = e.message; S.srv.versions = {}; S.srv.info = null; // S.pub.status kept, see checkServers
  } finally {
    if (srvPollSeq === seq) srvPollSeq = 0;
  }
  // Nothing changed: only a redraw that a field with the caret put off is made, once no field has it.
  if (drawn() === before) { renderIfOwed(true); return; }
  // (while a field has the caret the status strip and the gameplay settings follow in place: the strip,
  // the settings' notes and their bar say what the status says — a job that started elsewhere, a stack
  // that went down; the rest of the page is redrawn once the caret has left the fields)
  if (typingNow()) { syncStrip(); twSync(); renderOwed = true; } else render();
}

// One operation at a time: the agent refuses the second one with 409, and the buttons are blocked
// anyway while one runs — this catches the double press before the first render. The result does
// not come from the rpc (which answers immediately) but from the server.log / server.done /
// server.error events.
async function runServerJob(kind, version, extra) {
  if (jobRunning()) return showToast(t('toast.jobInProgress'), 'bad');
  if (!S.serverConfigured) return showToast(t('toast.serverNotConfigured'), 'bad');
  // target / host: the server this job runs on (see jobHere). purge: asked for — a Disable job's
  // result says `purged: false` both when it was not asked and when it failed. progress: the Prepare
  // choice a setup / provision job carries (its title says so — jobTitleOf). enabled: which way a
  // hotpatch job goes (true = Enable / Fetch missing, false = Disable) — the card's State line says
  // so from the click on, while the agent still answers with the state from before the run.
  // dryRun / template: an account copy or a move that only checks, and a copy whose source is a progress template (its
  // title says so — jobTitleOf); the request's other fields stay in the request alone. twOp: a gameplay-settings job that
  // puts a card's data files back ('revert' | 'reset'; '' for an Apply) — its outcome says so (twDoneText).
  S.srvJob = { kind, version, target: srvTarget(), host: S.serverAddr.host, purge: !!(extra && extra.purge === true),
    progress: extra && typeof extra.progress === 'string' ? extra.progress : '',
    enabled: extra && typeof extra.enabled === 'boolean' ? extra.enabled : null,
    dryRun: !!(extra && extra.dryRun === true), template: extra && typeof extra.fromTemplate === 'string' ? extra.fromTemplate : '',
    twOp: extra && Array.isArray(extra.revert) ? 'revert' : extra && Array.isArray(extra.reset) ? 'reset' : '',
    lines: [], running: true, error: '' };
  const job = S.srvJob;
  render();
  try {
    await rpc('server.' + kind, Object.assign({ version }, extra || {}));
    // Accepted and running: re-read the hotpatch card NOW rather than leaving it as it was until the
    // next tick — a voice run saves the new selection (the rows' "Selected") in its first seconds.
    if (kind === 'hotpatch' || kind === 'hotpatch.voice') refreshHotpatchQuiet();
  }
  catch (e) {
    // A copy of the account list, or a password it set, that the backend refused before any job existed: no question
    // follows and no password is shown (on('server.error')'s rule for a job that failed).
    if (S.accts.flow && S.accts.flow.job === job) S.accts.flow = null;
    if (acctsPwPending && acctsPwPending.job === job) acctsPwPending = null;
    if (kind === 'migrate') migrateFailed();
    S.srvJob.running = false; S.srvJob.error = e.message;
    srvLine(t('server.log.error', { error: e.message }));
    showToast(e.message, 'bad');
    render();
  }
}

// The events re-create it if the user closed the log in the meantime: an operation running on the
// server must stay visible until it ends. (The events carry no server, so a re-created job is taken
// for the current one's; the job runServerJob made keeps the tag of the server it was started on.)
// A few rpcs are named apart from the kind their events carry (SRVJOB_EVENT_KIND): that job is the
// same one, renamed — it keeps its target and its console instead of being re-created under the
// server on screen when its first event lands.
const SRVJOB_EVENT_KIND = { 'secrets.set': 'secrets', 'templates.ensure': 'templates', 'account.password': 'password' };
function srvJobFor(d) {
  const kind = (d && d.kind) || '', version = (d && d.version) || '';
  if (S.srvJob && S.srvJob.running && S.srvJob.version === version && SRVJOB_EVENT_KIND[S.srvJob.kind] === kind) S.srvJob.kind = kind;
  if (!S.srvJob || S.srvJob.kind !== kind || S.srvJob.version !== version)
    S.srvJob = { kind, version, target: srvTarget(), host: S.serverAddr.host, purge: false, progress: '', enabled: null, lines: [], running: true, error: '' };
  return S.srvJob;
}
// The agent's own result of a finished job. server.done carries the job's last snapshot ({state,
// result, …}); an agent that answers with the result itself is read too.
function jobResultOf(d) {
  const r = d && d.result;
  if (!r || typeof r !== 'object') return null;
  return r.result && typeof r.result === 'object' ? r.result : r;
}

// A bootstrap spits out thousands of lines; keep the tail, like the commands console.
const SRVLOG_MAX = 400;
function srvLine(text) {
  const s = String(text == null ? '' : text);
  if (!S.srvJob) return s;
  S.srvJob.lines.push(s);
  if (S.srvJob.lines.length > SRVLOG_MAX) S.srvJob.lines.shift();
  return s;
}

// A docker image pull sends hundreds of lines per second: a render() per line would rewrite all of
// #app. The new line is appended straight into the console, with the same scroll rule as
// restoreLogScroll — follow the new line only if the user has not scrolled up through the log.
function pushSrvLine(el, text) {
  const pinned = el.scrollTop + el.clientHeight >= el.scrollHeight - 4;
  el.insertAdjacentHTML('beforeend', srvLogLine(text));
  while (el.childElementCount > SRVLOG_MAX) el.removeChild(el.firstElementChild);
  if (pinned) el.scrollTop = el.scrollHeight;
}

// The server address as the user would type it: the host, plus the game port only when it was typed
// explicitly (a TXT or default port is re-resolved at every save, never pinned by the text).
function addrText() {
  const h = S.serverAddr.host || '';
  if (!h || S.serverAddr.source !== 'explicit') return h;
  return (h.includes(':') ? `[${h}]` : h) + ':' + S.serverAddr.port;
}
const addrShown = () => (S.addrEdit != null ? S.addrEdit : addrText());
// Settings and the install wizard save a typed address through the start screen's own path
// (server.setAddress: parse, TXT ports, agent port) and only when it differs from the saved one.
// settings.save never reads the host — written raw there it would be a second host writer that keeps the
// previous server's ports. Throws the backend's badAddress text. true = the address changed. Server Admin
// mode: the server is chosen on the start screen only (Settings and the wizard show it read-only).
async function saveAddressEdit() {
  if (isAdminMode()) { S.addrEdit = null; return false; }
  if (S.addrEdit == null) return false;
  const typed = S.addrEdit.trim();
  if (typed === addrText()) { S.addrEdit = null; return false; }
  await rpc('server.setAddress', { address: typed });
  S.addrEdit = null;
  return true;
}

// Returns true when the status target moved (and a new status epoch was started).
function applyInit(d) {
  if (!d) return false;
  const prevTarget = srvTarget(), prevHost = S.serverAddr.host;
  S.settings = Object.assign(S.settings, d.settings || {});
  S.versions = d.versions || [];
  S.installed = d.installed || [];
  S.selectedVersionId = d.selectedVersionId || (S.versions[0] && S.versions[0].id);
  S.serverConfigured = !!d.serverConfigured;
  S.runAtStartup = !!d.runAtStartup;
  S.os = d.os || S.os;
  S.fiddler = d.fiddler || S.fiddler;
  // Modes / start screen facts (always refreshed: they are backend truth, not form state).
  if (typeof d.mode === 'string') S.mode = d.mode;
  if (d.defaults) S.defaults = { servers: d.defaults.servers || [], mode: d.defaults.mode || '' };
  if (d.serverAddr) S.serverAddr = Object.assign(S.serverAddr, d.serverAddr);
  if (d.admin) {
    S.admin.hasToken = !!d.admin.hasToken; S.admin.tokenScope = d.admin.tokenScope || ''; S.admin.mode = d.admin.mode === 'ssh' ? 'ssh' : 'direct'; S.admin.openKey = !!d.admin.openKey;
    S.admin.tokenHosts = Array.isArray(d.admin.tokenHosts) ? d.admin.tokenHosts.map(String) : [];
  }
  // The start screen's saved servers: every reply that carries the init state carries the list.
  S.savedServers = d.savedServers || [];
  // ...and the Quick Start run this PC keeps (null: none — or one that cannot be used, which the init never fails for).
  if (d.quickstart && typeof d.quickstart === 'object') S.qsPlan = d.quickstart.plan && typeof d.quickstart.plan === 'object' ? d.quickstart.plan : null;
  if (typeof d.isWindows === 'boolean') S.isWindows = d.isWindows;
  // The agent installed on this PC ({installed, host, port, autostart} | null off Windows): every
  // reply that carries the init state refreshes it — an install, an uninstall, a mode change — so
  // the start screen never reads it separately.
  if (d.localAgent !== undefined) S.localAgent.init = d.localAgent;
  if (typeof d.loginMuted === 'boolean' && !S.login._mutedFromStorage) S.login.muted = d.loginMuted;
  if (typeof d.loginAnimOff === 'boolean' && !S.login._animFromStorage) S.login.animOff = d.loginAnimOff;
  if (typeof d.windowHidden === 'boolean') S.login.hidden = d.windowHidden;
  S.accounts = d.accounts || {};
  S.accountLists = d.accountLists && typeof d.accountLists === 'object' ? d.accountLists : {};
  // The last answer of the CURRENT server on the pre-made login, as the backend persisted it (every
  // init-state reply carries it for the server it is for) — the first render's word before a status read.
  S.serverAccounts = d.serverAccounts && typeof d.serverAccounts === 'object' ? d.serverAccounts : {};
  if (Array.isArray(d.voiceLanguages)) S.voiceLanguages = d.voiceLanguages.map(String);
  if (Array.isArray(d.stacks)) S.stacks = d.stacks;
  if (typeof d.ownSuffix === 'string') S.ownSuffix = d.ownSuffix;
  if (!S.wiz.versionId) S.wiz.versionId = S.selectedVersionId;
  // The wizard's source STARTS from the saved preference. Without this it stays on 'cdn' (the initial
  // value in S) after every app start, and startInstall writes it back over the setting — so a plain
  // walk through the steps silently resets the chosen source. Worse: the backend compares the
  // preference with the one noted next to the partial file, and a "change" the user did not make
  // THROWS AWAY their partial of tens of GB. What they already chose in the current session is not
  // overwritten ('local' included).
  if (!S.wizSourceTouched && (S.settings.source === 'drive' || S.settings.source === 'cdn'
      || allMirrors().some((m) => m.id === S.settings.source))) S.wiz.source = S.settings.source;
  // The backend may have changed the selected version under us (install.done); refetch the
  // catalogue if the user is looking at it. loadGameData no-ops when it is already current.
  if (S.screen === 'commands') loadGameData();
  // An address typed in Settings / the wizard and not saved belongs to the previous server once the
  // saved one moved under it (start screen, an agent install).
  if (S.serverAddr.host !== prevHost) S.addrEdit = null;
  // (Quick Start's runner judges its steps again from what this brought: qsTickSoon)
  qsTickSoon();
  // Every path that can move the status target lands here (start screen, Settings, wizard, agent
  // installs, mode changes) — one place, so none can be missed.
  if (srvBooted && srvTarget() !== prevTarget) { srvRetarget(); return true; }
  return false;
}
// Quick Start's runner (quickstart.js: qsTick) judges every step again from the facts — after every init state applied
// (applyInit) and every status read taken in (ingestStatus): once the code that took them in has finished (a microtask
// later), never in the middle of it, and once for several of them in a row. Nothing without quickstart.js.
let qsTickQueued = false;
function qsTickSoon() {
  if (qsTickQueued || typeof qsTick !== 'function') return;
  qsTickQueued = true;
  Promise.resolve().then(() => { qsTickQueued = false; if (typeof qsTick === 'function') qsTick(); }).catch((e) => console.error(e));
}

// ─────────────────────────── events ───────────────────────────
document.addEventListener('click', (e) => {
  const t = e.target.closest('[data-act]');
  if (!t) return;
  const act = t.getAttribute('data-act');
  // While installing / launching the game / asking for confirmation, the overlay is MODAL: only
  // its own controls and the window buttons work. The overlay only blocks pointer hit-testing —
  // Tab+Enter still reaches covered buttons, and Enter on a focused <button> fires a synthesized
  // click that lands here.
  // toast.close always passes: the toast is drawn OVER the overlay (z-index 40 > 20), so a button
  // that is visible and does not react would simply be broken for the user.
  // S.agentInstall / S.remove draw the same modal overlay, so they belong in the same list — without
  // them a Tab+Enter would still reach the buttons behind a running deploy.
  if ((S.installing || S.playing || S.confirm || S.notice || S.agentInstall || S.agentUpdate || S.remove || S.agentCfg.reloc) && !t.closest('.overlay')
    && act !== 'win.min' && act !== 'win.close' && act !== 'toast.close') return;
  // A picker the pointer opened a moment ago is not live yet (pickerArmed): its rows, its chips, its
  // close buttons and its backdrop.
  if (pickerArmed() && t.closest('.overlay')) return;
  // The gameplay settings as a status read left them behind (twLeftBehind): the click draws the page of the
  // version — or the server — that took its place, and does nothing else.
  if (t.closest('.twpage') && twLeftBehind()) { e.preventDefault(); render(); return; }
  const arg = t.getAttribute('data-arg'), open = S.picker, job = S.srvJob;
  if (ACTIONS[act]) { e.preventDefault(); ACTIONS[act](arg); }
  // A click that started a server job here (runServerJob — also through a question's Yes): on a screen of the
  // Server Admin area the page goes up to the status strip, where the job's console is.
  if (S.srvJob !== job && jobHere() && isAdminMode() && (S.screen === 'server' || S.screen === 'tweaks')) mainToTop();
  // (e.detail = the click count of a pointer's click; 0 for the keyboard's)
  if (S.picker && S.picker !== open) S.picker.at = e.detail > 0 ? Date.now() : 0;
});
// A redraw that a field with the caret put off (renderOwed) comes once the caret has left: at the focusout, or —
// when a click took the caret away — after that click (renderIfOwed waits for its mouseup). A mouseup the page never
// gets (the window dragged by its title bar) leaves no button held: the next move without one says so.
let pointerDown = false;
document.addEventListener('mousedown', () => { pointerDown = true; }, true);
// (the wizard's local folder — its check and the search's list — follows in place the same way: syncImp, syncFind)
function flushLiveOwed() { if (impOwed) setTimeout(syncImp, 0); if (findOwed) setTimeout(syncFind, 0); }
document.addEventListener('mouseup', () => { pointerDown = false; if (renderOwed) setTimeout(() => renderIfOwed(), 0); flushLiveOwed(); }, true);
document.addEventListener('mousemove', (e) => { if (pointerDown && !(e.buttons & 1)) { pointerDown = false; flushLiveOwed(); } }, true);
document.addEventListener('focusout', () => { if (renderOwed) setTimeout(() => renderIfOwed(), 0); });
document.addEventListener('input', (e) => {
  // The per-version signup limit cannot use data-model: setModel splits the path on '.', and the key is
  // a version id ("1.6"), so "…versions.1.6.maxPerDay" would walk into undefined and throw on every keystroke
  // — the typed number would never reach the policy that gets saved.
  const pm = e.target.closest('[data-policy-max]');
  if (pm) {
    const d = S.policy && S.policy.data; if (!d) return;
    d.signup = d.signup || { enabled: false, versions: {} };
    d.signup.versions = d.signup.versions || {};
    const id = pm.getAttribute('data-arg');
    const pv = d.signup.versions[id] = d.signup.versions[id] || { templates: ['fresh'], maxPerDay: 50 };
    const n = parseInt(pm.value, 10);
    if (!isNaN(n)) pv.maxPerDay = n;
    return;
  }
  // Accounts one launcher may create on the version (maxPerPlayer) — drawn only when the agent
  // reports the field, so an older agent is never sent a key it would ignore.
  const pp = e.target.closest('[data-policy-maxplayer]');
  if (pp) {
    const d = S.policy && S.policy.data; if (!d || !d.signup || !d.signup.versions) return;
    const pv = d.signup.versions[pp.getAttribute('data-arg')];
    const n = parseInt(pp.value, 10);
    if (pv && !isNaN(n)) pv.maxPerPlayer = n;
    return;
  }
  // A number of the gameplay settings: data-tw is its path in the draft of the version on screen (a version
  // id cannot be a data-model segment here either). The page follows in place — no render.
  const tw = e.target.closest('[data-tw]');
  if (tw) { twTyped(tw); return; }
  // A search field of that page (data-tw-find = the card whose list it searches): the list follows in
  // place. Its text is kept per version, so it is no data-model either.
  const find = e.target.closest('[data-tw-find]');
  if (find) { twFind(find); return; }
  // The new name typed for an account of a move: data-arg is its login, which may hold a dot — no data-model path can
  // name it. No render: the field keeps its caret.
  const moved = e.target.closest('[data-migrate-to]');
  if (moved) { const c = S.migrate.choice[moved.getAttribute('data-arg')]; if (c) c.to = moved.value; return; }
  const t = e.target.closest('[data-model]');
  if (!t) return;
  const path = t.getAttribute('data-model');
  setModel(path, t.value);
  // The first keystroke in an account form pins the version of the card that was DRAWN — no render.
  // Until then the card follows the status poll (acctVersionCur / acctNewCur), and a poll that sees the
  // version down for a moment moves the form, and the progress choice with it, to another version.
  if ((path === 'account.name' || path === 'account.password') && !S.account.version && S.account.shown) S.account.version = S.account.shown.split('|')[0];
  if ((path === 'acctNew.name' || path === 'acctNew.password') && !S.acctNew.version && S.acctNew.shownVersion) S.acctNew.version = S.acctNew.shownVersion;
  if (t.hasAttribute('data-filter')) filterPicker(t.value); // deliberately NOT a render
  if (path.startsWith('login.')) syncLoginCta();           // same: no render, just the button state
  if (path.startsWith('agentCfg.form.')) syncAgentCfgSave(); // same: the Save button and its count
  if (path === 'agentCfg.reloc.dir') relocCheckSoon();      // the agent's dry run, debounced — no render
  if (path === 'migrate.dir') migrateSync();                 // the check's button follows — no render
  if (path === 'wiz.localPath') impTyped();                  // the folder's check, debounced — no render
  if (path.startsWith('agentInstall.form.')) deployTyped(path.slice('agentInstall.form.'.length)); // same: a stack folder looked at, the summary in place
  // A Test result belongs to the address it tested: editing the address drops it — the line only, no
  // render (that would take the caret).
  if (path === 'login.address' && S.login.test) {
    S.login.test = null;
    const line = document.querySelector('.lg-status');
    if (line) line.remove();
  }
});
// Enter in a start-screen field = the CTA (the address alone is enough — Test is optional). A field that
// names its own action (data-enter: the saved server's name) is the handler below's.
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Enter' || S.screen !== 'login') return;
  const t = e.target.closest('input[data-model^="login."]');
  if (!t || t.hasAttribute('data-enter')) return;
  e.preventDefault();
  if (loginCanEnter()) ACTIONS.loginEnter();
});
// Enter in a field that names its action (data-enter) = that action, as its button would run it: the account list's
// search, the copy's new account, the password typed. Not behind a modal overlay, and a job it started takes the page
// up to the strip's console — the click handler's rules.
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Enter' || e.isComposing || !e.target.closest) return;
  const el = e.target.closest('input[data-enter]');
  if (!el) return;
  if ((S.installing || S.playing || S.confirm || S.notice || S.agentInstall || S.agentUpdate || S.remove || S.agentCfg.reloc) && !el.closest('.overlay')) return;
  const act = ACTIONS[el.getAttribute('data-enter')];
  if (!act) return;
  e.preventDefault();
  const job = S.srvJob;
  act(el.getAttribute('data-arg'));
  if (S.srvJob !== job && jobHere() && isAdminMode() && (S.screen === 'server' || S.screen === 'tweaks')) mainToTop();
});
// The language <select> in Settings (see languageLine), the rotation <select> of the gameplay settings'
// Spiral Abyss card and the choice of an account of a move.
document.addEventListener('change', (e) => {
  const choice = e.target.closest('select[data-migrate-choice]');
  if (choice) { ACTIONS.migrateChoose(choice.getAttribute('data-arg'), choice.value); return; }
  const rotation = e.target.closest('select[data-tw-rotation]');
  // (on a page a status read left behind, the pick only draws the page that took its place: twLeftBehind)
  if (rotation) { if (twLeftBehind()) render(); else ACTIONS.tweaksAbyssRotation(rotation.value); return; }
  const el = e.target.closest('select[data-lang]');
  if (!el) return;
  ACTIONS.setLanguage(el.value);
});
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  // Esc on "are you sure?" = Cancel; on a question whose second button is an answer (noAct) = that answer.
  if (S.confirm) {
    const c = S.confirm;
    S.confirm = null;
    if (c.noAct && ACTIONS[c.noAct]) ACTIONS[c.noAct](c.arg, c.checkbox ? !!c.checkbox.checked : undefined, c.selected);
    else render();
    return;
  }
  if (S.notice) { S.notice = null; render(); return; }   // Esc on the notice = Understood
  if (S.agentCfg.reloc) { clearTimeout(relocTimer); S.agentCfg.reloc = null; render(); return; } // = Cancel
  if (S.agentUpdate && S.agentUpdate.phase !== 'run') { S.agentUpdate = null; render(); return; } // = Cancel / Close; never under a run
  if (S.picker && !S.installing) { S.picker = null; render(); }
});
// <img> load failures do not bubble, so this has to be a capture-phase listener. It covers the
// character stage and the picker tiles alike: a splash file missing from the build shows the
// element-tinted initial instead of a broken-image box.
document.addEventListener('error', (e) => {
  const img = e.target;
  if (!img || img.tagName !== 'IMG') return;
  const wrap = img.closest('[data-artwrap]');
  if (wrap) wrap.classList.add('noart');
}, true);
document.addEventListener('mousedown', (e) => {
  const bar = e.target.closest('[data-drag]');
  if (bar && !e.target.closest('button')) rpc('window.drag');
});

// backend events
// Quick Start's runner (quickstart.js) hears the events of the jobs it starts through qsEvent(type, d), each call one
// line guarded by typeof qsEvent === 'function'. The game install's four (install.*), an account's (account.*) and a
// search's (findgames.*) it is handed first in their handlers, and it takes them when it answers true — the job was the
// run's (qsOwnsInstall for the install, and then it takes in the reply's init state itself): the handler leaves them
// to it, overlay and toasts included. The agent installs' (deploy.*) and the agent update's (agentupdate.*) it observes
// in a listener of its own before their handlers, the server jobs' (server.*) in one after theirs: those handlers
// always run.
// The Library changed (a version installed or imported): the local folder's check and the folders the search found
// speak of the Library before it — a choice to use a folder in another's place, and a found folder's warning, went with
// the job that added it.
function libraryChanged() {
  Object.assign(S.imp, { stale: true, replace: null, hit: null });
  if (!findLive()) S.find = findBlank();
}
// The game install Quick Start's run started is the run's to take in (the handlers below leave its events to qsEvent),
// and the Library changes under it all the same: these two, registered before those handlers — while qsOwnsInstall
// still says the install is the run's —, give it what the handlers give one the wizard started (a failed one: the local
// folder checked again once it is in sight, the run's import may have been of that folder).
on('install.done', () => { if (typeof qsOwnsInstall === 'function' && qsOwnsInstall()) libraryChanged(); });
on('install.error', () => { if (typeof qsOwnsInstall === 'function' && qsOwnsInstall()) S.imp.stale = true; });
// A progress event does NOT redraw the overlay if nothing structural changed in it: the full rewrite
// of #app would restart the ring's rotation and the waves from zero at every percent. The text is set in
// place, and the percent is carried by the tween (syncProgress → rAF).
on('install.progress', (d) => {
  if (typeof qsEvent === 'function' && qsEvent('install.progress', d)) return;
  S.installFraction = d.fraction || 0; S.installMsg = d.message || ''; S.installPhase = d.phase;
  if (!S.installing) return;
  if (patchInstallLive()) syncProgress(); else render();
});
on('install.done', (d) => {
  if (typeof qsEvent === 'function' && qsEvent('install.done', d)) return;
  if (d && d.versionId) S.activeInstallId = d.versionId; // backend's id is authoritative
  // A voices job: the languages the backend actually added (d.voices) name the outcome; an empty list
  // (every one was already on disk) keeps the requested names — they ARE part of the version now.
  if (S.activeInstallVoices && d && Array.isArray(d.voices) && d.voices.length) S.activeInstallVoices = d.voices.map(String);
  const voices = S.activeInstallVoices;
  S.installDone = true;
  S.installPaused = false; S.installStopIntent = null; // a stop that raced the finish lost — show done
  if (d && d.state) applyInit(d.state);
  libraryChanged();
  if (S.screen !== 'install') showToast(voices ? t('toast.voicesAdded', { langs: voices.join(', '), id: S.activeInstallId || '' }) : t('toast.installed', { id: S.activeInstallId || '' }), 'ok');
  render();
});
on('install.error', (d) => {
  if (typeof qsEvent === 'function' && qsEvent('install.error', d)) return;
  S.installError = (d && d.message) || t('common.unknownError');
  S.installPaused = false; S.installStopIntent = null; // the error overlay must never hide behind "paused"
  // An import refused (its own check — nothing written) or taken back: the folder is checked again once it is in sight.
  if (S.activeInstallLocal) S.imp.stale = true;
  if (S.screen !== 'install') showToast(t('toast.installFailed', { error: S.installError }), 'bad');
  render();
});
on('install.cancelled', (d) => {
  if (typeof qsEvent === 'function' && qsEvent('install.cancelled', d)) return;
  const paused = S.installStopIntent === 'pause';
  S.installStopIntent = null;
  if (paused) S.installPaused = true; // overlay flips to the paused state, Resume/Give up take over
  else {
    const wasLocal = !!S.activeInstallLocal, wasVoices = !!S.activeInstallVoices;
    clearActiveInstall();
    S.installPaused = false; S.installPhase = '';
    showToast(wasVoices ? t('toast.voicesCancelled') : wasLocal ? t('toast.importCancelled') : t('toast.installCancelled'), 'ok');
  }
  render();
});
// The !S.picker guard: the log sits behind the modal anyway, and a streaming play session must not
// rebuild the tile grid or steal the caret while the user is typing in the picker's search box.
// While the "Starting the game" splash is up, every line also becomes its status.
on('play.log', (d) => {
  addLog('sys', d.text);
  if (S.playing) { S.playing.msg = d.text; render(); return; }
  if (S.screen === 'commands' && !S.picker) render();
});
// The client has actually appeared — the splash did its job; from here the player looks at the game.
// All three bypass the PRE-check splash (check): the events come from a game session, and the
// pre-check is not one — a stray play.done from the session that just ended would otherwise close
// its splash and the launch would look cancelled out of nowhere.
on('play.appeared', (d) => {
  if (!S.playing || S.playing.check) return;
  S.playing = null;
  // d.enhancements: the F1 menu really is in the process (not merely wanted) — the backend says so.
  showToast(d && d.enhancements ? t('toast.gameRunningEe') : t('toast.gameRunning'), 'ok');
  render();
});
// A version's answer about the player's own game files was stored — from Settings, from "Don't ask again"
// on the page's question or the desktop shortcut's (that one may be another Relic process): the Settings
// rows follow the backend's map.
on('settings.ownFiles', (d) => {
  if (!d || !d.ownFiles || typeof d.ownFiles !== 'object') return;
  S.settings.ownFiles = d.ownFiles;
  if (S.screen === 'settings' && !typingNow()) render(); // never under a caret: the next render draws it
});
// The session's client is seen gone: what the session end does next (Fiddler, the official profile) is said at once,
// before its settle window — the "Starting the game" splash went with play.appeared, and play.log reaches only the
// commands console. PLAY stays off until the session end has let go of everything (play.done / play.error /
// play.fiddlerClosed): it holds the session gate until then.
on('play.closing', (d) => {
  if (S.playing && !S.playing.check) S.playing = null;
  sessionSay({ phase: 'closing', kind: 'closing', text: sessionText(d, t('core.play.closing')), versionId: (d && d.versionId) || '', appeared: true });
});
// What the session end did, in the session's own words (the session result): it takes the closing notice's place,
// styled by its kind AND by whether the client was seen at all — appeared false is a launch that never started,
// whatever the kind. The Settings card reads the profile again: the session end's own restore posts no profile.changed.
on('play.done', (d) => {
  if (!(S.playing && S.playing.check)) S.playing = null;
  sessionSay({ phase: 'done', kind: d && typeof d.kind === 'string' ? d.kind : '', text: sessionText(d, t('toast.sessionEnded')),
    versionId: (d && d.versionId) || '', appeared: d && typeof d.appeared === 'boolean' ? d.appeared : null });
  loadProfileStatus();
});
// No play.done follows these two: a session end that failed says so here alone, so the closing notice goes with them.
on('play.error', (d) => {
  if (!(S.playing && S.playing.check)) S.playing = null;
  if (sessionClosing()) S.session = null;
  showToast(t('toast.launchError', { error: (d && d.message) }), 'bad');
  render();
  loadProfileStatus();
});
// Relic closed the game because the user closed Fiddler — a modal window, not a toast: the message
// explaining why the game vanished from the screen must not erase itself after a few seconds. The
// backend has already pulled the app window out of the tray so the dialog is actually seen.
on('play.fiddlerClosed', (d) => {
  if (!(S.playing && S.playing.check)) S.playing = null;
  if (sessionClosing()) S.session = null;
  S.notice = {
    title: t('play.fiddlerClosed.title'),
    body: (d && d.message) || t('play.fiddlerClosed.body'),
  };
  render();
  loadProfileStatus();
});
// The recovery loop put the official profile back after a deferred restore (posted once that pass completed): the
// Settings card turns Live from the status it carries, and its words take the place of the deferred notice — never of a
// session that is ending now, whose notice stays (a toast says this one).
on('profile.changed', (d) => {
  if (!d) return;
  if (d.status && typeof d.status === 'object') { profSeq++; S.profile = d.status; }
  const text = sessionText(d, '');
  if (!text) { if (S.screen === 'settings') renderOrOwe(); return; }
  if (sessionClosing()) { showToast(text, 'ok'); return; }
  sessionSay({ phase: 'profile', kind: typeof d.kind === 'string' ? d.kind : '', text, versionId: '', appeared: null });
});
on('command.log', (d) => { addLog(d.type, d.text); if (S.screen === 'commands' && !S.picker) render(); const el = document.getElementById('cmdlog'); if (el) el.scrollTop = el.scrollHeight; });
on('server.log', (d) => {
  const prev = S.srvJob;
  const j = srvJobFor(d);
  // A new job, or a still-empty console (showing the placeholder), asks for one full draw.
  const fresh = j !== prev || j.lines.length === 0;
  const text = srvLine(d && d.text);
  // A folder move that turns into a copy says so in every case — the planned cross-drive copy, or a
  // rename the system refused after all (a bind mount of the same disk): Stop appears with that line
  // (one full draw), whatever the dialog's check expected, and also on a job re-created from the events.
  const stoppable = j.kind === 'relocate' && j.running && !j.crossDrive && /\bCopying \d+ files\b/.test(text);
  if (stoppable) j.crossDrive = true;
  // The two screens of the Server Admin area draw the job console (the status strip): 'server' and 'tweaks'.
  if ((S.screen !== 'server' && S.screen !== 'tweaks') || S.picker) return;
  const el = fresh || stoppable ? null : document.getElementById('srvlog');
  if (el) pushSrvLine(el, text); else render();
});
on('server.done', (d) => {
  const j = srvJobFor(d); j.running = false;
  // The job is DONE although a purge that was asked for did not delete everything (Windows: a client
  // was being served one of the files): the agent says so in the result only — voice job: the
  // languages in `purgeFailed`; Disable: `purged: false`. There is a single toast, so the warning
  // takes the place of "done" (the job log keeps the agent's own line).
  const res = jobResultOf(d);
  const stuck = j.kind === 'hotpatch.voice' && res && Array.isArray(res.purgeFailed)
    ? res.purgeFailed.filter((n) => typeof n === 'string' && n) : [];
  if (stuck.length) showToast(t('hotpatch.voice.purgeFailed', { langs: stuck.join(', ') }), 'bad');
  else if (j.kind === 'hotpatch' && j.purge && res && res.purged === false) showToast(t('confirm.hotpatch.purgeFailed'), 'bad');
  // An account created from the admin card of THIS server: into its list, with its own toast. One that
  // ran on another server (the address changed meanwhile) has no list to go to (see acctNewMovedDone).
  else if (j.kind === 'accountcreate' && j.target === srvTarget()) acctNewDone(j, res);
  else if (j.kind === 'accountcreate') acctNewMovedDone(j, res);
  // A folder change says where the version lives now ('' = taken off the agent, the files kept). The
  // job is DONE even when the server it stopped did not start again in the new place (restartError —
  // by then the change is made): that is a warning, not a success. restarted:false alone is no failure
  // (the server was not running, or the new folder holds no server — it stays down by design).
  else if (j.kind === 'relocate' && res && typeof res.to === 'string') {
    const noStart = res.to && res.restarted !== true && typeof res.restartError === 'string' && res.restartError.trim() ? res.restartError.trim() : '';
    if (noStart) showToast(t('toast.relocateNotRestarted', { version: j.version, dir: res.to, error: noStart }), 'bad');
    else showToast(res.to ? t('toast.relocateDone', { version: j.version, dir: res.to }) : t('toast.relocateRemoved', { version: j.version }), 'ok');
  }
  // Gameplay settings: whether the running server took them, or they wait for its next Start.
  else if (j.kind === 'tweaks') showToast(twDoneText(j, res), 'ok');
  // A copy the account list (or the copy form) started: its check asks the question, the copy itself says so
  // (acctsCopyDone). A password it set: the password generated for it, shown once (acctsPwDone). A copy's check
  // followed from elsewhere changed nothing — no "done" for it.
  else if (j.kind === 'accountcopy' && S.accts.flow && S.accts.flow.job === j) acctsCopyDone(j, res);
  else if (j.kind === 'password' && acctsPwPending && acctsPwPending.job === j) acctsPwDone(j);
  else if (j.kind === 'accountcopy' && res && res.dryRun === true) showToast(t('toast.acctCopyChecked'), 'ok');
  // A move of accounts, or its check (migrateDone: the result's dryRun says which).
  else if (j.kind === 'migrate') migrateDone(j, res);
  else showToast(t('toast.jobDone', { job: jobTitleOf(j), version: j.version }), 'ok');
  srvJobSettled(j, true);
  // A job of the chain an agent install or "Add version…" left: its next step (chainNext).
  chainJobDone(j, res);
});
on('server.error', (d) => {
  const j = srvJobFor(d); j.running = false;
  j.error = (d && d.message) || t('common.unknownError');
  // A copy of the account list that failed — its check or the copy — asks nothing more (the toast below says the
  // agent's reason); a password that failed has nothing to show.
  if (S.accts.flow && S.accts.flow.job === j) S.accts.flow = null;
  if (acctsPwPending && acctsPwPending.job === j) acctsPwPending = null;
  if (j.kind === 'migrate' && j.target === srvTarget()) migrateFailed();
  // A copy the backend refused because this server's agent has no account list (an agent put back since the list
  // answered here): the card says the agent must be updated, and "Copy by name or uid" goes back to the plain copy and
  // its own question.
  if (j.kind === 'accountcopy' && j.target === srvTarget() && j.error === t('core.agent.accountCopyUnsupported'))
    Object.assign(S.accts, { unsupported: true, supported: false, data: null, rows: [], menu: '', pick: null, panel: null });
  // A job of the chain an install or "Add version…" left (chainJobFailed): a stopped or failed one drops the rest of it —
  // the admin goes on from the version's row, one version at a time. A start the agent refused because another operation
  // ran keeps its step for a later status read, and is no failure to report: the refused start wrote nothing, so the
  // console of the job before it keeps its place, the strip names the operation that runs, and a toast says the step
  // waits. The refusal that ends the waiting is reported like any failure.
  const c = S.fetchChain, chained = chainRuns(c, j);
  chainJobFailed(j);
  if (chained && S.fetchChain === c) {
    if (S.srvJob === j && !j.lines.length) S.srvJob = c.prev && c.prev !== j ? c.prev : null;
    showToast(t('server.chain.waitBusy', { job: jobTitleOf(j), version: j.version }), 'ok');
    srvJobSettled(j);
    return;
  }
  srvLine(t('server.log.error', { error: j.error }));
  // A stopped voice job — or a stopped stack download — ends as an error on the wire: the agent's 409
  // "Stopped on request: …" (kept in the log above). The toast goes by THAT sentence — whoever pressed
  // Stop, this admin or another one — and never by the click: a job that really failed after a Stop
  // click (the disk filled up, the link was lost) must not be reported as a clean stop that "run it
  // again" would continue.
  const stopped = /^Stopped on request\b/.test(j.error);
  // A failed account creation keeps the typed name (a taken name is corrected, not retyped).
  if (j.kind === 'accountcreate' && j.target === srvTarget()) S.acctNew.pending = null;
  if (j.kind === 'hotpatch.voice' && stopped) showToast(t('hotpatch.voice.stopped', { version: j.version }), 'ok');
  else if (j.kind === 'fetch' && stopped) showToast(t('toast.fetchStopped', { version: j.version }), 'ok');
  else if (j.kind === 'navmesh' && stopped) showToast(t('toast.navmeshStopped', { version: j.version }), 'ok');
  else if (j.kind === 'navdata' && stopped) showToast(t('toast.navdataStopped', { version: j.version }), 'ok');
  else if (j.kind === 'relocate' && stopped) showToast(t('toast.relocateStopped', { version: j.version }), 'ok');
  // A gameplay-settings job told to stop waiting ("Stop waiting", from this launcher or another): nothing
  // new is in use — what that means for the files is the agent's sentence, in the log.
  else if (j.kind === 'tweaks' && stopped) showToast(t('toast.tweaksStopped', { version: j.version }), 'ok');
  else showToast(t('toast.jobFailed', { job: jobTitleOf(j), error: j.error }), 'bad');
  srvJobSettled(j); // a failed enable may still have cached files
});
// Quick Start observes a server job's lines and outcome once the handlers above have taken them in (the job, the next
// step of the chain an install left).
on('server.log', (d) => typeof qsEvent === 'function' && qsEvent('server.log', d));
on('server.done', (d) => typeof qsEvent === 'function' && qsEvent('server.done', d));
on('server.error', (d) => typeof qsEvent === 'function' && qsEvent('server.error', d));
// The agent refused this launcher's open key (a 401 on an admin rpc, the status poll included): the
// admin closed open administration. The backend has already forgotten the key and put the mode back
// to player (admin.openForget); the init state it sends is applied here — another status epoch — and
// the reason is a window, not a toast: it must be read. A screen only admin mode has is left for the
// Server page, whose player view says what the server offers now: Game rules always, Game
// commands only where players may not send them — read BEFORE applyInit, whose status epoch empties
// the snapshot that flag is read from (after it the flag always reads as off).
on('admin.revoked', (d) => {
  const commandsStay = playerCommandsAllowed();
  applyInit(d && d.state);
  if (S.screen === 'tweaks' || (S.screen === 'commands' && !commandsStay)) S.screen = 'server';
  S.notice = { title: t('server.player.openAdminRevokedTitle'), body: t('server.player.openAdminRevokedBody', { host: S.serverAddr.host }) };
  render();
});
// After server.done / server.error: the real state comes from the agent, it is not inferred from the
// job's outcome. A job that ran on ANOTHER server (the address was changed meanwhile, see jobHere)
// says nothing about the hotpatch card on screen: only the status is read again, which also brings
// this server's buttons back to life.
function srvJobSettled(j, ok) {
  const here = j.target === srvTarget();
  // Our own hotpatch / voice job: forget what /status said about it, so the read below starts from
  // scratch (a job that goes on without us — the link was lost — is picked up again by that read).
  if (here && (j.kind === 'hotpatch' || j.kind === 'hotpatch.voice')) S.hotpatch.busy = null;
  // The account list of that version is from before the job: read again at the status read below (acctsAfterJob).
  if (here) acctsAfterJob(j);
  checkServers(true);
  if (here) refreshHotpatchAfterJob(j.kind, j.version, ok);
  // The jobs that import (or drop) template databases — a start among them: it imports the ones the
  // stack still lacks — : the account card's choices may have changed. An account creation too: a
  // template_not_ready refusal means the card offered a template the stack no longer has imported —
  // re-read, the card greys it and says why.
  if (here && (j.kind === 'templates' || j.kind === 'start' || j.kind === 'setup' || j.kind === 'provision' || j.kind === 'accountcreate')) loadAcctTemplates(j.version);
  // A folder change — done, failed or stopped — rewrote (or kept) the agent's configuration: the
  // settings card's folders, and on this PC the local agent's facts (its stack folders), are read again. A
  // package download that ended well filled a folder: what the local agent's facts say stands there (the install
  // form's Keep rows offer a download only into a folder that holds no server) is read again too.
  if (here && j.kind === 'relocate') { loadAgentCfg(); loadLocalAgent(); }
  else if (here && j.kind === 'fetch' && ok) loadLocalAgent();
  // The Secrets card's loaded values are from before the job, whatever it was — a Prepare / Start
  // replaces a vendor's published password with a private one: read again, quietly.
  if (here && S.secrets.data && String(S.secrets.version) === String(j.version)) reloadSecretsQuiet(S.secrets.version);
  // The gameplay settings' answer of that version is from before the job too (twAfterJob).
  if (here) twAfterJob(j, ok);
  // The Logs list, when it was read, names the job that just ended as it was then: read again.
  if (here && S.agentJobs.list) loadAgentJobs();
  // The status still held names the job that just ended: drawn again now that twAfterJob keeps it as S.tw.settled,
  // the strip says nothing "started elsewhere" for it (busyHeld) until the status is read again.
  if (here && isAdminMode() && (S.screen === 'server' || S.screen === 'tweaks')) { if (typingNow()) syncStrip(); renderOrOwe(); }
}
// The admin route's result (server.done of an 'accountcreate' job of this server): the session list
// gets the row — the password only when the agent returned one (verification on: typed or generated)
// —, the form clears the name and password for the next one (version and template stay), and a
// "show it as my login" request re-reads the init state, whose accounts the backend has just updated.
// No pending request = the form was reset while the job ran (the admin went to another server and
// came back): what it asked is unknown, and the backend may have filed the login — re-read it too;
// what the form holds now was typed after the reset and stays.
// The account Quick Start's run created (qsRunsJob) is this launcher's own login, with the password the player typed
// there: its row says so — as the Library does (account.pwYours) — instead of drawing it; one the server generated is
// drawn, as the agent returns it only once.
function acctNewDone(j, res) {
  const a = S.acctNew, r = res || {}, pend = a.pending;
  a.pending = null;
  const name = typeof r.name === 'string' && r.name ? r.name : (pend && pend.name) || '';
  const tpl = typeof r.template === 'string' && r.template ? r.template : 'fresh';
  const own = qsRunsJob(j) && !r.passwordGenerated && typeof r.password === 'string' && !!r.password;
  acctNewPush({ version: j.version, name, uid: r.uid, templateLabel: acctNewTplLabel(j.version, tpl),
    password: !own && typeof r.password === 'string' && r.password ? r.password : null, generated: !!r.passwordGenerated, own });
  // The form is cleared only while it still holds the account just created: the admin may already be
  // typing the next one, which the result must not wipe. "Show it as my login" goes back off either
  // way: it is a choice about THIS account — left on, the next one typed (a friend's) would silently
  // replace the login it has just filed.
  if (pend && a.name.trim() === pend.name) { a.name = ''; a.password = ''; }
  a.remember = false;
  showToast(t('toast.adminAccountCreated', { name, version: j.version }), 'ok');
  if (!pend || pend.remember) rpc('app.init').then((d) => { applyInit(d); if (!typingNow()) render(); }).catch(() => {});
}
// An admin-route create that finished on ANOTHER server (the address or the mode changed while it ran):
// its card and list are gone. A password the server GENERATED exists nowhere else — the agent returns
// it once, in this result — so it goes into a modal window that stays until dismissed; anything else is
// the account's own toast. The password is never put in S.srvJob or a log line.
function acctNewMovedDone(j, res) {
  const r = res || {};
  const name = typeof r.name === 'string' && r.name ? r.name : '—';
  const host = j.host || '—';
  if (r.passwordGenerated && typeof r.password === 'string' && r.password) {
    S.notice = { title: t('server.acctNew.movedTitle', { host }), body: t('server.acctNew.movedBody', { host, version: j.version, name }), pick: r.password };
    render();
  } else showToast(t('toast.adminAccountCreated', { name, version: j.version }), 'ok');
}
// The dev autoinstall sends no voices: the backend's default (English) applies; the request is kept
// like a real one so Resume works.
on('dev.autoinstall', (d) => { if (S.installing) return; S.picker = null; S.wiz.versionId = d.versionId; S.activeInstallId = d.versionId; S.activeInstallLocal = null; S.activeInstallVoices = null; S.activeInstallReq = { type: 'install.start', payload: { versionId: d.versionId } }; S.screen = 'install'; S.installStep = LAST; S.installing = true; S.installDone = false; S.installError = null; S.installPaused = false; S.installStopIntent = null; S.installPhase = ''; S.installFraction = 0; S.installMsg = t('install.preparing'); resetProgress(); render(); rpc('install.start', { versionId: d.versionId }).catch((e) => { clearActiveInstall(); showToast(e.message, 'bad'); }); });

// per-version removal
on('remove.progress', (d) => { if (!S.remove) return; S.remove.msg = d.message || ''; S.remove.fraction = d.fraction || 0; render(); });
on('remove.done', (d) => {
  if (d && d.state) applyInit(d.state);
  // (the Library changed: see install.done)
  S.imp.stale = true;
  if (!findLive()) S.find = findBlank();
  if (S.remove) { S.remove.running = false; S.remove.done = true; S.remove.leftovers = (d && d.leftovers) || []; }
  render();
});
on('remove.error', (d) => { if (S.remove) { S.remove.running = false; S.remove.error = (d && d.message) || t('common.unknownError'); } render(); });
// Quick Start observes an agent install's lines and outcome before the handlers below — Quick Start's own install
// draws no overlay — and the handlers go on (the chain of the jobs the install leaves is built there).
on('deploy.log', (d) => typeof qsEvent === 'function' && qsEvent('deploy.log', d));
on('deploy.done', (d) => typeof qsEvent === 'function' && qsEvent('deploy.done', d));
on('deploy.error', (d) => typeof qsEvent === 'function' && qsEvent('deploy.error', d));
// agent installs (remote SSH or this PC) — streamed into the overlay console
on('deploy.log', (d) => {
  const a = S.agentInstall; if (!a) return;
  a.lines.push(String((d && d.text) || '')); if (a.lines.length > 400) a.lines.shift();
  const el = document.getElementById('deploylog');
  if (el) { el.insertAdjacentHTML('beforeend', `<div>${esc(a.lines[a.lines.length - 1])}</div>`); el.scrollTop = el.scrollHeight; } else render();
});
on('deploy.done', (d) => {
  const a = S.agentInstall;
  // Another host / agent port / mode: applyInit already started the new status epoch and its read. The
  // same box (an upgrade) keeps its target, so the reset and the fresh check are done here.
  const moved = !!(d && d.state) && applyInit(d.state);
  if (a) { a.running = false; a.done = true; a.result = d || null; }
  if (!moved) S.srv = { state: 'idle', error: '', versions: {}, info: null };
  S.hotpatch = hotpatchBlank();
  // A new or upgraded agent: another version (one that answers /agent/config), another file, other folders.
  agentCfgSeq++; S.agentCfg = agentCfgBlank();
  // ...another process, whose operations the Logs have not read.
  agentJobsSeq++; agentJobLogSeq++; S.agentJobs = agentJobsBlank();
  // ...and another catalogue of gameplay settings, or the route itself: the gameplay settings start over.
  twSeq++; S.tw = twBlank(); twPickerGone();
  // ...and an account list read again from that agent (which may or may not have the route) — a copy's question asked
  // over the previous one goes with it.
  S.accts = acctsBlank();
  if (S.confirm && S.confirm.act === 'acctsCopyGo') S.confirm = null;
  migrateReset();
  // The jobs the install hands the Server page (`chain`, decided from the agent that answered after it: a re-point
  // first where that agent has no folder for the version, then the download — a backend that sends only `fetch`
  // gives the plain downloads): the agent is up and idle, so the first job starts right away, the next ones one after
  // the other (chainNext); the summary tells the admin where their log is. What the install held back (`held`) is in
  // its summary; the version's row on the Overview is the way on for it.
  const steps = chainStepsOf(d);
  S.fetchChain = steps.length ? { target: srvTarget(), win: !!d && d.target === 'windows', steps, phase: null, busyTries: 0, running: null } : null;
  render(); if (!moved) checkServers(true);
  loadLocalAgent(); loadPolicy(); loadHotpatch(S.selectedVersionId || (S.versions[0] && S.versions[0].id)); loadAgentCfg(); loadAgentBuild();
  // A new or upgraded agent (it may bring the admin route) — and possibly other stacks: re-read the
  // account card's templates.
  loadAcctTemplatesAll();
  chainNext();
});
// The token comes along on a failure too: the installer may already have written it on the box, and it
// is generated in the backend and filtered out of the log — so this is the only place it can be seen.
on('deploy.error', (d) => {
  const a = S.agentInstall;
  if (a) { a.running = false; a.error = (d && d.message) || t('common.unknownError'); a.result = d || null; }
  render();
});
// Quick Start observes the update of the agent on this PC it asked for (no overlay of its own) before the handlers below.
on('agentupdate.log', (d) => typeof qsEvent === 'function' && qsEvent('agentupdate.log', d));
on('agentupdate.done', (d) => typeof qsEvent === 'function' && qsEvent('agentupdate.done', d));
on('agentupdate.error', (d) => typeof qsEvent === 'function' && qsEvent('agentupdate.error', d));
// agent updates (the agent on this PC, or a remote one over SSH) — streamed into the update overlay
on('agentupdate.log', (d) => {
  const u = S.agentUpdate; if (!u) return;
  u.lines.push(String((d && d.text) || '')); if (u.lines.length > 400) u.lines.shift();
  const el = document.getElementById('deploylog');
  if (el) { el.insertAdjacentHTML('beforeend', `<div>${esc(u.lines[u.lines.length - 1])}</div>`); el.scrollTop = el.scrollHeight; } else render();
});
on('agentupdate.done', (d) => {
  const u = S.agentUpdate;
  if (u) { u.phase = 'done'; u.warning = (d && typeof d.warning === 'string' && d.warning) || ''; }
  // The agent these cards were read from was replaced by a run over SSH, and by a run on this PC only
  // when the admin server IS that agent (agent.build's fact: the status cannot say it while the agent
  // restarts). An update of another agent on this PC leaves this server's cards and drafts alone.
  agentUpdateEnded(u, !!u && (u.target === 'remote' || !!(S.agentBuild.local && S.agentBuild.local.adminServer)));
});
on('agentupdate.error', (d) => agentUpdateFailed(d && d.message, !!(d && d.replaced)));
// Account creation through the player route (account.create): the player card's. The backend echoes
// the request's version in every event. A create started under ANOTHER target (the server or the mode
// changed while it ran — S.acctPending remembers where it began) only speaks through a toast: the form
// on screen is the new server's, and its late lines, result or error must never land in it.
const acctMoved = () => !!(S.acctPending && S.acctPending.target !== srvTarget());
// A line goes straight into its console, like server.log / deploy.log: a render() per line would
// rewrite the page over the field being typed in (the next account's name) and take the caret. A full
// draw only when the pane is not on screen, and never over a focused field.
const ACCT_LOG_MAX = 60;
on('account.log', (d) => {
  if (typeof qsEvent === 'function' && qsEvent('account.log', d)) return;
  if (acctMoved()) return;
  const ac = S.account;
  const text = String((d && d.text) || '');
  ac.lines.push(text); if (ac.lines.length > ACCT_LOG_MAX) ac.lines.shift();
  if (S.screen !== 'server') return;
  const el = document.getElementById('acctlog');
  if (el) {
    el.insertAdjacentHTML('beforeend', `<div>${esc(text)}</div>`);
    while (el.childElementCount > ACCT_LOG_MAX) el.removeChild(el.firstElementChild);
    el.scrollTop = el.scrollHeight;
  } else if (!typingNow()) render();
});
on('account.done', (d) => {
  if (typeof qsEvent === 'function' && qsEvent('account.done', d)) return;
  const pend = S.acctPending, moved = acctMoved();
  S.acctPending = null;
  const result = (d && d.result) || {};
  const name = typeof result.name === 'string' && result.name ? result.name : (pend && pend.name) || '';
  const version = (d && d.version) || (pend && pend.version) || '';
  if (!moved) {
    const ac = S.account; ac.running = false; ac.result = d && d.result; ac.lines = [];
    if (pend && ac.name.trim() === pend.name) { ac.name = ''; ac.password = ''; }
    if (version) delete ac.another[version]; // the new login is the remembered one now
  }
  if (d && d.state) applyInit(d.state);
  showToast(t('toast.accountCreated', { name }), 'ok');
  render();
});
on('account.error', (d) => {
  if (typeof qsEvent === 'function' && qsEvent('account.error', d)) return;
  const moved = acctMoved();
  S.acctPending = null;
  const msg = (d && d.message) || t('common.unknownError');
  if (moved) return showToast(t('toast.jobFailed', { job: t('server.job.accountcreate'), error: msg }), 'bad');
  const ac = S.account; ac.running = false; ac.error = msg; render();
});
// The window went to the tray / came back: pause or resume the start-screen media. Nothing to
// redraw — the DOM is untouched, only the play state follows.
// A search of this PC stops when the window goes to the tray: its list is out of sight.
on('window.visibility', (d) => { S.login.hidden = !!(d && d.hidden); LOGIN_MEDIA.sync(); if (S.login.hidden) findHalt(); });
// "Find it on this PC": every event names its search (findEvent drops another search's, and anything after its done) —
// Quick Start's own search (its Game page) is its own.
on('findgames.progress', (d) => { if (typeof qsEvent === 'function' && qsEvent('findgames.progress', d)) return; findEvent('progress', d); });
on('findgames.found', (d) => { if (typeof qsEvent === 'function' && qsEvent('findgames.found', d)) return; findEvent('found', d); });
on('findgames.done', (d) => { if (typeof qsEvent === 'function' && qsEvent('findgames.done', d)) return; findEvent('done', d); });

// ─────────────────────────── boot ───────────────────────────
// No first render() before app.init AND the language have both resolved: the dictionary must be
// loaded before anything is drawn, and the language to load comes from the saved settings.
(async function boot() {
  let init = null;
  try { init = await rpc('app.init'); } catch (e) { /* backend not ready */ }
  try { await I18N.init((init && init.settings && init.settings.language) || 'en'); } catch (e) { /* English stays */ }
  // Every script of the page has run before anything is drawn — quickstart.js, loaded after this file, among them: the
  // run boot may open below is its, and so are the start screen's ways into it.
  if (document.readyState === 'loading') await new Promise((r) => document.addEventListener('DOMContentLoaded', r, { once: true }));
  S.log = [{ t: 'sys', m: t('cmd.console.ready') }];
  // Music preference: localStorage first (instant, survives a reset of state.json), backend second.
  try { const m = localStorage.getItem('relic-login-muted'); if (m === '1' || m === '0') { S.login.muted = m === '1'; S.login._mutedFromStorage = true; } } catch (e) { /* no localStorage */ }
  try { const a = localStorage.getItem('relic-login-anim'); if (a === '1' || a === '0') { S.login.animOff = a === '0'; S.login._animFromStorage = true; } } catch (e) { /* no localStorage */ }
  applyInit(init);
  // Quick Start: a run that is not ended — it runs, or waits for the restart that finishes WSL 2 (the RunOnce value
  // brought this launcher back) — opens its progress page at once and goes on from the facts (qsResume, quickstart.js);
  // a paused one waits for the start screen's Continue, and a done one is never kept.
  const qsPlan = init && init.quickstart && init.quickstart.plan;
  if (qsPlan && (qsPlan.state === 'running' || qsPlan.state === 'waitingRestart') && typeof qsResume === 'function') {
    S.screen = 'quickstart';
    qsResume(qsPlan);
  }
  // Start screen: a returning user (mode chosen + server known; admin also needs a token) lands on
  // the pre-filled panel with a single CTA; a fresh install sees "Which Way?". An admin entry made
  // with the open key is not a token the admin panel can enter with (the backend asks for a typed
  // one): that user comes back through the Player panel, and the Server page offers the way in again
  // while the server is still open.
  const configured = S.mode === 'player' ? !!S.serverAddr.host : S.mode === 'admin' ? !!S.serverAddr.host && S.admin.hasToken && !S.admin.openKey : false;
  if (S.mode && configured) { S.login.panel = S.mode; S.login.address = S.serverAddr.host; }
  else if (S.mode === 'admin' && S.admin.openKey && S.serverAddr.host) { S.login.panel = 'player'; S.login.address = S.serverAddr.host; }
  else if (S.defaults.mode) { S.login.panel = S.defaults.mode; S.login.address = S.serverAddr.host || ((S.defaults.servers[0] || {}).host) || ''; }
  const dev = (location.hash || '').slice(1);
  if (dev) S.screen = dev; // dev aid: start on a given screen
  render();
  if (S.screen === 'commands') loadGameData();
  if (S.screen === 'tweaks') loadTweakData(); // its version is read once the first status names it (twOpen)
  // The "Server: LIVE/STOPPED" badge in the library: one read at start, then every 30 seconds. The
  // status is a pure read (GET /status on the agent) and runs in parallel with any job — but not more
  // often: every call means a `docker compose ls` on the box.
  srvBooted = true;
  refreshServersQuiet();
  setInterval(refreshServersQuiet, SRV_POLL_MS);
  // The agent this launcher ships against the one on this PC: asked once the first screen is drawn (the
  // answer is hashed off the backend's message thread) — the start screen's update row follows in place.
  loadAgentBuild();
  // The "Official in-version fixes (client hotpatch)" card while it is being filled: a much shorter
  // interval than the status poll, but only while a hotpatch job of the shown version is downloading
  // on this server and the admin is looking at that page (refreshHotpatchQuiet returns at once otherwise).
  setInterval(refreshHotpatchQuiet, HOTPATCH_POLL_MS);
  // Check at every start, not only when Settings is opened: if a session ended without restoring the
  // live profile, the OFFICIAL client would launch on private-server data — the user has to learn
  // that BEFORE they open it, not after.
  await loadProfileStatus();
  if (S.profile && !S.profile.liveActive) {
    showToast(S.profile.gameRunning
      ? t('toast.liveNotActiveRunning')
      : t('toast.liveNotActive'), 'bad');
  }
})();
