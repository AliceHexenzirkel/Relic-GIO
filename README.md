# Relic — launcher for classic Genshin Impact versions on your own private server

Relic is a Windows desktop launcher for a private **Genshin Impact / hk4e** server ("GIO" — the
docker-compose server stacks for versions **1.6**, **2.8** and **3.2**). It downloads and installs a classic
client, keeps that version's account data isolated from the official client, configures Fiddler to
redirect the game to your server, launches game + Fiddler together (and closes Fiddler with the game),
and — for server admins — installs and drives the server agent, provisions the Golden Apple
Archipelago save, and sends GM commands.

It is a personal preservation project (replaying a past limited-time event with friends): the client
comes from the **official CDN** or a public mirror (the Internet Archive — the only host of the 3.2 client), and
everything else runs on your own machine and your own server. Not affiliated with HoYoverse / miHoYo.

## Downloads

Every release (the *Releases* page) carries up to six assets, built by GitHub Actions from the tag
(`.github/workflows/build.yml`; `SHA256SUMS.txt` and the same table in the release body list their
hashes). The two navmesh ones are there only when the version catalogue names the archive
([docs/NAVMESH.md](docs/NAVMESH.md), *Shipping the bundle*):

| Asset | Take it when |
|---|---|
| `RelicSetup.exe` | You play, or you administer a server — the launcher installer (per user, no administrator rights). |
| `RelicSetup-navmesh.exe` | You administer a server and run its pathfinding server: the same build with the 2.8 navmesh bundle inside, installed into the stack by the agent at *Prepare the server* / *Start*. Not needed by players. |
| `relic-navmesh-2.8.zip` | You already have the launcher and want the navmesh files for the 2.8 archipelago and quest domains: drop it into the agent's payloads folder (adopted at its next start) or press *Download the navmesh* on the Pathfinding server card ([docs/NAVMESH.md](docs/NAVMESH.md)). |
| `relic-server-fixes-1.6.zip` / `relic-server-fixes-2.8.zip` | You run a GIO server stack by hand, without the launcher or the agent, and want the repaired server data and the ready-made saves. 3.2 has none: its payload ships no save, and the agent puts its few files in place itself. |
| `relic-mhynot-patch.zip` | You start the 1.6 or 2.8 game by hand on Windows 11 (the injector pair + a `Run.bat` for the game folder) or need the patched `global-metadata.dat` of the 2.8 client. Relic applies both itself. The 3.2 login patch is not in it: Relic writes those bytes into your own copy of `UserAssembly.dll`. |

The draft release also waits for the job `agent-selftest` of the same workflow: the agent's own `--selftest` on Linux
(ubuntu-24.04) under Python 3.9 — the oldest a server box may have — and 3.12, run plain, then with a box's settings
exported over stand-in stack folders it must leave unchanged, then with the vendor's published values and with refused
values in the secret keys. It needs no docker and no secret. Every install and upgrade of a box runs that selftest
first and stops when it fails, which is why the release waits for it: an agent whose selftest fails is never released.

**Applying the fixes without the launcher.** Each `relic-server-fixes-<ver>.zip` holds the server data
files at their stack paths (`server/data/txt/`), the save (`sdk/data/sdk.db`, `redis/dump.rdb`), the
player database before and after the quest (`account_before_the_quest/`, `account_after_the_quest/`) and
a `readme.txt` with the steps in order — stop, copy, import, the events SQL, start, the account to log in
with. `python build/make_release_bundles.py` builds the same zips locally.

**Visual C++ runtime.** Relic and the files it puts into the game folder need nothing installed. The
classic clients' own plugins use the Microsoft Visual C++ Redistributable (x64): on a Windows without it
(a freshly installed one) the 1.6 client shows "Plugins: Failed to load … MTBenchmark_Windows.dll … with
error '0x7e'" at its start. Install [vc_redist.x64.exe](https://aka.ms/vs/17/release/vc_redist.x64.exe)
from Microsoft and start the game again.

## Two modes

The start screen asks **Which Way?** — and above its two buttons offers **Quick Start**, which sets everything up for
a newcomer ([below](#quick-start)).

- **Player mode** — enter the server address (`host`, `host:port`, or a DNS name whose ports are
  published in a TXT record), install a version with the voice languages you want (English, Chinese,
  Japanese, Korean — more can be added later from the Library), check the server status, play. Unless
  the server admin turned it off, create your own in-game account from the launcher with a starting
  progress (new, or — where the version ships a save, 1.6 and 2.8 — before / after the GAA quest). No token, no
  server secrets.
- **Server Admin mode** — everything above plus the server console: connect with the agent token, use
  the agent already installed on this PC, or install the agent on a fresh box (Linux over SSH with a
  password or key — sudo is fine — or this Windows PC with Docker Desktop; a box without the vendor
  server packages gets them downloaded from the Internet Archive), start/stop/switch versions,
  prepare the server (import the shipped save, keep your own progress or apply the fixes only; the
  pathfinding server on or off), see every account of a version — its character, adventure rank and whether
  it is online — and copy a progress onto it, give it a starting progress or set its password, manage the
  player account policy and progress templates, rotate the server secrets, GM commands, change the game's
  own rules (the resin cap, the Spiral Abyss and domain enemies, what domains, quests and hangout events
  reward), change the agent's own settings and restart it, move a server to another folder or drive.
  Password verification stays an agent API call (no launcher switch).

**Saved servers.** Both panels of the start screen keep a list of the servers you use. **Save**, beside *Test*, keeps
the address in the box with a name (up to 40 characters; for a server not saved yet it starts as its host name, for a
saved one as the name it has) and the panel's mode — Player or Server Admin — with the agent port typed on the Server
Admin panel, or on the Player panel the one the server's saved entry has. A name row opened for one server saves
nothing once the address names another: it starts again with that server's name. A row shows the name, the address
with its mode, *token saved* when Relic keeps an agent token for that server and *in use* for the server and mode the
launcher is set to. A click on a row fills the panel in without
connecting; **Connect** enters at once, as the panel's own button does: a Player row with its address, a Server Admin
row with the agent token Relic saved for that server or the one built into the launcher — without either, the Server
Admin panel opens with the address filled in and asks for the token. A saved server holds an address, a mode and a name,
never a token. Relic keeps the agent tokens apart, one per server, in `%LOCALAPPDATA%\Relic\secrets\server.json`
(protected with DPAPI for your Windows account), and saves one only once its agent accepted it — at an admin login,
*Use the agent on this PC*, an agent install, or *Enter admin mode* on a server open to everyone (the key it hands
out) —, so a login to another server keeps the first one's token. **Delete** forgets, without asking, everything Relic
keeps for that server on this PC: the row, its token or key, the in-game logins Relic remembers there and any SSH host
key pinned for the box. **Delete all** asks once, then does the same for every saved server and for every server Relic
keeps a token or key for, saved or not. Nothing changes on the servers — the accounts made there stay, and still count
toward this launcher's account limit on each —, the server the launcher is set to stays, and so does a token built into
the launcher; when Server Admin mode loses the token of its server that way, the start screen asks again which way to
go. When another program holds `server.json` open at that moment, the toast says so: Relic forgets for the session, but
a token or key saved there may come back at its next start — save the server again and delete it once more. Relic keeps
at most 50 saved servers.

**Going back to an earlier build.** A Relic build that keeps a single token in `server.json` reads that file's list as
one record without a server or a token: it asks for the agent token again, and the first token or key it saves (an admin
login, *Use the agent on this PC*, an agent install, *Enter admin mode*) replaces the whole list — the tokens of the
other servers are gone. Such a build also drops the saved servers and any pinned SSH host keys from `state.json` the
first time it saves that file: back on this build, the list of saved servers is empty and a Linux box whose SSH key was
pinned is seen for the first time again.

**Version 3.2** is downloaded from the Internet Archive alone (the catalogue names no official CDN for it). Its client logs in to a
private server only with a community patch, which Relic writes into the unmodified `UserAssembly.dll` of the 3.2.0
build when it is installed or imported (a folder that holds another build of 3.2 is refused). Relic also puts a build of
RSAPatch in place of the game's `mhypbase.dll`, with which the client starts on Windows 10 and Windows 11 — only over
the original file (kept as `mhypbase.dll.relic-orig`) or over an earlier build of its own. A `mhypbase.dll` of the
player's own is replaced only when the player chooses Relic's file: PLAY asks, and the choice can be changed in Settings.
A 3.2 server has no ready-made save: players start new characters.

Mode is a UX gate, not a security boundary — the agent's bearer token is. **Never bake a token into a
build you hand to players.**

## Quick Start

**Quick Start** — highlighted at the top of the start screen, and *New here? Quick Start* under each panel — sets up
everything a newcomer needs: it asks a few questions, shows what it is going to do, then does it on one progress
screen, the game and the server side by side. It ends in Server Admin mode on the Server page when it set a server up
or you gave a server's agent token, in Player mode on the Library otherwise, and adds that server to the saved servers
(named *This PC* for a server only this PC plays on, else by its host); a run that did not reach its server ends on the
start screen and saves none. Its last page says what got done — *All set*, or what is still missing.

**What it asks.** *Express*, preselected, asks only what has no safe answer of its own; *Custom* shows every setting.
- **The game**: one or more versions — each is a game of its own, with a server of its own —, each downloaded or
  *I already have it* (*Choose GenshinImpact.exe…*, or *Find it on this PC*; the copy is checked the way *Local folder*
  checks it, below), with its voice languages (English preselected). A version already in the Library stays as it is.
  Custom adds the games folder, the download host, the in-game enhancements, HTTPS decryption, the certificate and the
  desktop shortcut; Express takes them as Settings hold them.
- **The server** — no answer is preselected:
  - *Yes — I have its address*: the address, and *I am this server's admin* for its agent token;
  - *No — set one up on this PC*: Relic looks at the PC first (Windows, virtualization, WSL 2, Docker Desktop, Python,
    a 7z extractor, an agent already here, the memory) and says so when no server can run here — an ARM processor,
    virtualization off in the firmware, a Windows older than Docker Desktop needs (Windows 10 build 19045, Windows 11
    build 22631), less than 8 GB of memory, a restart Windows still waits for before WSL or Docker Desktop can be
    installed. Express sets the server up for this PC alone; Custom asks who plays on it (only you, other PCs on your
    network, players on the Internet), the server versions with their folders, starting progress and pathfinding, the
    agent port and *Keep .env settings*;
  - *No — set one up on a Linux server*: its address, the SSH port and user, a password or a key file. Before it logs
    in, Relic shows the key the server introduced itself with and asks *Is this your server?* — from then on it
    refuses any other key at that address and port. It asks for sudo's password only when sudo wants one, and whether
    people outside your network join (preselected from the address you typed: *No* for a private one, *Yes* for a
    public address or a name).
- **Your account**, one per version: its name taken from your Windows user name, a starting progress (the first the
  version offers), a password only where the server checks passwords. None is made where this launcher already has
  one, nor on a server whose agent does not answer (you create it later); Custom can leave a version out.
- **The summary**: what is downloaded and where, the disk space each drive needs and has, the memory, the network and
  what Windows will ask. When Quick Start installs Docker Desktop, *I accept the Docker Subscription Service Agreement*
  must be ticked (*Read it* opens the agreement; Docker Desktop is free for personal use, education, non-commercial
  open source projects and small businesses).

**What it installs, and where** — only what the summary named. Every step is judged again from what the PC and the
server report, also when a run is taken up again, so nothing done is done twice and what no longer holds (Docker
Desktop closed, the agent stopped) is done again:
- the game, as the install wizard installs it — the client and its voice packs in the games folder (by default
  `%USERPROFILE%\Relic Games`), then Fiddler with its certificate at the end of the first install (Windows asks you
  once to trust the certificate, unless it is trusted already or Custom leaves it out);
- for a server on this PC:
  - Python's embeddable package (about 13 MB) when the PC has no Python 3.10 or later, and 7-Zip's `7zr.exe` (0.6 MB)
    when a package needs a 7z extractor the PC lacks, into `%LOCALAPPDATA%\Relic\tools\` — pinned by version, size
    and sha256 in `config\setup-tools.json`, for the agent alone: nothing on PATH, no registry entry;
  - WSL 2, turned on (`wsl --install --no-distribution`) or updated (`wsl --update`) when Docker Desktop needs it;
  - Docker Desktop for your user (in `%LOCALAPPDATA%\Programs\DockerDesktop`; an installer of about 650 MB, run only
    once Windows has confirmed it is signed by Docker Inc, its licence accepted only by your tick), then started;
  - the server agent, as *Install the agent → This Windows PC* installs it (`%LOCALAPPDATA%\Relic\agent\`, started
    with Windows unless Custom turns that off), listening on this PC alone for a server only you play on;
  - the server packages of your versions in `<games folder>\servers\<version>_live` — in
    `<drive>:\relic_servers\<version>_live` when the games folder's path holds a character outside plain ASCII or a
    quote, is longer than 100 characters or lies inside Relic's own data folder (`%LOCALAPPDATA%\Relic`, which Relic's
    uninstall deletes) —, each downloaded (a folder that already holds that version's server is used as it is),
    prepared and started in turn: one server runs at a time, and the last stays running;
- for a Linux server: the agent, installed over SSH as the install form does it (Docker, Python and a 7z tool are
  added on the box when missing) with a new token — other launchers that administer an agent already there must log
  in again —, and the packages in `/home/<version>_live` (a folder the box already uses stays), prepared and started;
- your accounts.

When a version's server does not fit in the memory Docker may use (WSL gives it half of the PC's unless
`%UserProfile%\.wslconfig` says otherwise), Quick Start turns that version's pathfinding server off; when it still
does not fit, the summary offers *Let Docker use up to N GB* — written into `.wslconfig`, for every WSL Linux of the
PC from WSL's next start — as long as Windows keeps 4 GB beside the game client, and otherwise waits for your tick that
it may go on all the same.

**What Windows asks, and a restart.** Quick Start itself asks for administrator rights only to turn WSL 2 on or to
update it: one Windows prompt you click yourself, named in the summary — none on a PC whose WSL 2 is ready. In Custom, a
server other PCs join adds one more, for its firewall rules (the agent's port, TCP 21000 and UDP 21081). The game's
install asks what the install wizard asks, and the summary names it too: Windows' question to trust Fiddler's
certificate (no administrator rights involved) and — only when Windows does not let Fiddler install for your user
alone, because of a policy or a security program — one prompt for Fiddler's installer. Turning WSL 2 on needs a
restart: **Restart Windows now** pauses the game download, stops a server download, saves the run and restarts Windows
at once — Windows asks the open programs to close, so one with unsaved work can hold the restart, and nothing is closed
by force (a restart held that way is offered again, the downloads going on meanwhile); **Later** leaves the restart to
you. At the next sign-in Relic opens by itself (the value `RelicQuickStart` under
`HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce`, armed while a run goes on) and Quick Start goes on where it
was — as it does whenever Relic starts during a run, after a power cut too. A password or a token typed for the run is
not kept: a step that needs it again asks for it.

**Stopping.** *Stop Quick Start* pauses the game download and stops a server download — both go on later from where
they were — and lets a Prepare or Start that runs finish; Relic then shows the start screen, or the Library when it is
in Server Admin mode (*Change mode or server* leads to the start screen from there). *Continue Quick Start* on the start
screen picks the run up again; *Discard it* forgets it (what is installed stays).

**What it never does.**
- Write a password, a key passphrase, sudo's password, an agent token or an account password into its plan
  (`%LOCALAPPDATA%\Relic\quickstart.json`) or a log.
- Put the shipped save — it replaces a server's accounts — on any server but one it downloaded in that run and the
  agent reports not prepared yet: every other server is prepared keeping its database, and one already prepared is
  only started.
- Change an agent already on this PC — its folders and network settings stay as they are, and only a version it has no
  folder for is given one —, but for pointing it at Relic's `7zr.exe` when it has no extractor of its own; an agent
  that is not the build this launcher ships is replaced by the shipped one, unless Custom keeps it.
- Install Docker Desktop for all users, run `wsl --shutdown`, open ports on your router (no UPnP: the summary names the
  ports to forward — TCP 21000, UDP 21081 and the agent's TCP port) or set up a DDNS name.
- Open anything to other computers for a server Express sets up on this PC.

**Players on the Internet.** For a server they reach from outside, the public address is learned where the server is
(on the Linux box, or on this PC for a Windows server) unless you typed the server by that address: three address
services are asked, and two must agree (`api.ipify.org`, `ipv4.icanhazip.com`, `checkip.amazonaws.com`); this PC then
checks that TCP 21000 and the agent's port answer there. UDP 21081 is not tested, and the result says so.

Relic's own uninstall leaves Docker Desktop, WSL and the memory line of `.wslconfig` with your user, and the games'
`servers` folder with the servers' data; its report names Docker Desktop, the memory line and the folder.

## A game you already have

*Install a version* → **Local folder** adds a copy of the game that is already on this PC: nothing is downloaded or
copied, the game stays where it is, and Relic puts the few files it needs into its folder.

- **Browse…** picks the game's `GenshinImpact.exe`. The field also takes the folder that holds it, or the exe's path as
  Explorer's *Copy as path* gives it (quotes included).
- **The folder is checked while you type**: a quick look first, then a deep check that reads Relic's files there the way
  the import does. **START** is available once the check says the folder is ready to add. The import runs the same check
  again before its first write, and a folder it refuses is left exactly as it was.
- The check offers the one way on that its answer has: a folder of another version Relic offers gets **Add it as version
  X**, a second copy of a version that is already installed gets **Use this folder instead** (the Library then uses this
  copy; the other folder stays on disk as it is). A folder Windows protects — anything under *Program Files* — is
  refused: copy the game into a folder of your own. Relic never asks for administrator rights.
- **Find it on this PC** looks in Relic's own folders, at the running games and at the programs Windows remembers
  starting, then through this PC's drives — each physical disk on its own, down to eight folders deep; *Search deeper*
  goes on from there. It only reads. *Stop* ends it at once, and it also stops when you leave that step, start an install
  or send the window to the tray. A folder it finds goes into the field with one click; one that cannot be added (a
  version Relic does not offer) is listed apart, greyed.

## Build (developer machine: Windows, .NET 10 SDK, Python 3)

```powershell
dotnet run --project app/Relic.App                      # run the app
dotnet run --project spikes/Relic.Spikes -- all          # the mechanics test suite
python build/check_i18n.py                              # validate translations, regenerate ui/lang/en.js

.\build\publish.ps1 `                                    # self-contained single-file win-x64 build
   --default-server=game.example.com `                   #   (optional) one default server
   --default-servers="EU=eu.example.com;LAN=192.0.2.10" ` #   (optional) several, offered as quick picks
   --default-mode=player `                               #   (optional) player | admin
   --theme=summer `                                      #   (optional) summer | classic
   --drive                                               #   keep Google Drive ids in the shipped catalogue
# admin builds only:  --token=<agent token>   (default: contents of config\agent.token if present)
#   --navmesh=<dir>   (default: _bundled_navmesh\) ships the OPTIONAL pathfindingserver navmesh bundle, see below
& "C:\Program Files (x86)\Inno Setup 6\iscc.exe" installer\relic.iss   # -> installer\Output\RelicSetup.exe

.\build\build_dlls.ps1 [-Versions 16,28] [-CopyToPayload]   # native components: in-game enhancements DLL + Win11 injector (needs VS Build Tools, CMake, Ninja)
```

**Optional navmesh bundle.** The server stacks ship the pathfinding server's navmesh set of an older game
version; files that add to or replace it (never the whole set) can be shipped in `_bundled_navmesh\<ver>\`
(`navmesh.json` + `NavMesh\*.navmesh`, made by `python build\navmesh_tool.py pack`; new files come from a Unity 2017.4
bake converted by its `fromunity` command, see `build\unity\`). `publish.ps1` copies the
folder into `agent\payloads\<ver>\navmesh\` when it exists, verified by size + sha256, and the agent installs it
into every stack at *Prepare the server* / *Start*. A missing folder is normal; GitHub Actions can fetch a zip
of it from the repository variable `RELIC_NAVMESH_URL`. Facts, format and workflow: [docs/NAVMESH.md](docs/NAVMESH.md).

**Native components** are not built by `publish.ps1`: `build\build_dlls.ps1` builds the enhancements DLL per game
version and the Win11 injector (its source is the git submodule `injector/mhynot2`; run
`git submodule update --init --recursive` once), prints the prerequisites it misses, the sizes and sha256 of the
outputs and whether they match the committed `payload\` files; the GitHub Actions workflow *DLL builds*
(`.github/workflows/dlls.yml`) builds the same and uploads the binaries as artifacts. The committed `payload\`
binaries change only by hand, after an in-game test.

The catalogue of downloadable versions is `config/versions.json` (gitignored; copy
`config/versions.sample.json`). Each version lists the `client` archive and one `voices.<Lang>` archive
per spoken language (`English(US)`, `Chinese`, `Japanese`, `Korean` — keyed exactly as the game names
the folder; the legacy `audio` key is still read as `voices.English(US)`), every host with its own byte
count. The install wizard downloads the voice languages you tick; the Library adds another one later
into the installed game. Google Drive ids (`versions[].client.driveId` / `voices.<Lang>.driveId`) are
optional: empty hides the Drive source; `publish.ps1` strips them unless you pass `--drive`.

## Server side

The agent (`agent/gio_agent.py`, Python 3 stdlib only) runs next to the docker-compose stacks and is
the only thing the launcher talks to. Install it from the launcher (Quick Start, the start screen, or Server Admin mode
→ *Server* → *Agent* → *Install the agent on a new server…*) or by hand: copy `agent/` to the box and run
`sudo bash install_agent.sh`.
A fresh Ubuntu/Debian or Alma/RHEL box is fine: the script installs docker (+ compose v2) and a
python3 the agent can run on when they are missing — the agent needs Docker Compose v2.1.1 or later, as it hands
every password to a container in the docker CLI's environment, never on a command line
([docs/SERVER-DEPLOY.md](docs/SERVER-DEPLOY.md) §4); run again on a box that has the agent, it is the
upgrade (config and token kept, previous version backed up — [docs/AGENT-UPGRADE.md](docs/AGENT-UPGRADE.md)).
The launcher says when one is due: **Agent update available** appears wherever the agent in use is not
the one that launcher ships — on the start screen for the agent on this PC, in the status strip of every
Server tab for any server — and **Update now** does it: in place for the agent on this PC (its configuration
is not touched, a failed update is undone), over SSH with that same installer for a Linux box.
Two ways to get the vendor server packages onto the box: **already extracted** (*I have it* and its folder in
the install form, `h` at the installer's question), or **downloaded by the agent** — choose *Download now* for a
version in the form (or `d` at `install_agent.sh`'s question), or add it later from the Server page — and
the agent fetches the ready-made 1.6 / 2.8 / 3.2 stack from the Internet Archive (1.9 / 2.5 / 2.4 GB — 3.2 also as a
4.2 GB .zip, which the agent unpacks without any tool — resumable,
md5-verified), extracts it and installs it into the folder you named; a 7z extractor is installed on a
Linux box when missing, a Windows PC needs 7-Zip (or a `tar.exe` that reads 7z). *Not now* (`l`) leaves a version
for later: the Server page shows it NOT SET UP, with *Add version <v>…*. A version the server already has keeps its
folder through every re-install and upgrade — a folder changes only through the launcher's Server page (*Server
folders* below, *Add version…*, *Change folder…*) or `gio_agent.py --relocate` on the box. The vendor's GIO guide
book ships with the launcher (*Guide (PDF)* in the install form, the admin start screen and About).

- [docs/SERVER-DEPLOY.md](docs/SERVER-DEPLOY.md) — what the agent does on the box, stack layout, provisioning
- [docs/AGENT-API.md](docs/AGENT-API.md) — the HTTP contract (admin + public endpoints)
- [docs/AGENT-DIRECT.md](docs/AGENT-DIRECT.md) — token handling and the direct access mode (SSH tunnel = legacy)
- [docs/AGENT-WINDOWS.md](docs/AGENT-WINDOWS.md) — running the agent and the server on a Windows PC
- [docs/PLAYER-ACCOUNTS.md](docs/PLAYER-ACCOUNTS.md) — self-service accounts (on by default), templates, policy, the
  admin's list of every account and what it does, password verification (API)
- [docs/HOTFIX-1.6.md](docs/HOTFIX-1.6.md) — **Official in-version fixes** for 1.6, 2.8 and 3.2: the agent mirrors miHoYo's
  client hotpatch outputs (~127 MB for 1.6, ~208 MB for 2.8 — fetched as one archive.org bundle when it can, file by
  file from the version's upstream otherwise; ~294 MB for 3.2, file by file from a public archive.org copy of the
  official files until its bundle is published, the official CDN holding none of them — every file verified against
  the official checksums; whether the bundles come first is the admin's choice, under *Mirror source* in that same
  card, Internet Archive by default) and the dispatch advertises them, so every
  client fetches them once at login, the way the official client does (Kazuha/Klee texts, Kaboomball icons,
  Harpastum cooldown, Hu Tao EN audio on 1.6); one toggle in Server Admin mode. The fixes cover every language; for players
  whose game voice language (Japanese / Korean / Chinese Windows) is not installed, the admin can also mirror that
  language's original voice packs (3–10 GB per language, one bundle per language; on 2.8 and 3.2 before players need
  them) — a language the player installed from the launcher never needs the mirror
- **Server preparation**: *Prepare the server* asks which progress to install — the default account with the
  ready-made save, keep / bring your own progress (nothing imported), or the fixes only; a version whose payload ships
  no save (3.2) offers the last two — and whether to run the
  `pathfindingserver` (monster navigation, ~4 GB of RAM of its own; off on a small box, switchable later on the Server
  page's Content tab). The vendor's published MUIP sign key is replaced by yours (`GIO_MUIP_KEY`) or a random one the
  first time a stack is prepared, so only your agent can send GM commands
- **Gameplay settings** (Server page, *Game rules* tab): the resin cap and its regeneration, the Spiral Abyss enemies
  and their level, the enemies a domain spawns and their level, what the domains hand out and cost at the statue, what
  the quests and the hangout events reward. The agent writes its settings into the server's own data files only while
  the game server runs, and the game server's own loader judges them — no restart, nobody disconnected, a refusal puts
  the previous files back; on a stopped server the settings wait for the next Start, and the agent only puts files back
  (the vendor's originals, or — after an apply that was cut off — what a file taken over held before it). A data file
  edited by hand on the box is taken over: the change stays and the settings apply on top of it. A file Relic cannot
  take over holds back only the settings that need it, and every card can put its files back at once — *Put back
  Relic's settings now…* or *Put back the original files now…*: the vendor's originals ship with the agent, and a copy
  of the hand-changed file is kept in the stack's `.relic-tweaks/hand/` ([docs/AGENT-API.md](docs/AGENT-API.md),
  *Gameplay settings*)
- **Navmesh files for the pathfinding server**: the packages carry a navmesh set of an older game version (the 1.6
  archipelago is in it; the 2.8 archipelago and later scenes are not). When the launcher ships a navmesh bundle
  (`agent/payloads/<ver>/navmesh/`), the agent copies its files into `server/res/NavMesh/` and registers them in the
  server's md5 list at every *Prepare the server* / *Start*; the pathfinding server reads them at its next boot
  ([docs/NAVMESH.md](docs/NAVMESH.md))
- **The pathfinding server's own data**: a 2.8 stack unpacked from a package without its `server/res` data (no NavMesh
  files, no md5 list — the package the agent downloads itself has them; a stack that lacks only the files Relic bakes
  is offered them the same way while no navmesh archive reaches it otherwise) gets it from the *Pathfinding server* card:
  *Download the pathfinding data* has the agent download the archive the payload's catalogue names
  (`agent/payloads/2.8/navdata.json`, 643 MB from the Internet Archive; unpacking it takes a 7z extractor on the box) and
  add the files the stack lacks — a file already there is never replaced — and restarts that version's pathfinding
  server alone when it runs
- **The published passwords do not stay on a prepared server**: the archives also ship a published
  MySQL root password and Flask secret key in `.env`, and one published *internal* password shared by redis and the
  game's database accounts (`hk4e_work`, `hk4e_readonly`). The agent replaces the root password and the Flask key — at
  *Prepare the server* or at the next *Start* (the Flask key: the Start of a stopped server) — with yours
  (`GIO_MYSQL_ROOT_PASSWORD`, `GIO_FLASK_SECRET_KEY`: two optional fields of the install form and two installer prompts)
  or with random ones, and the internal password with a random one at *Prepare the server* or the *Start* of a stopped
  server (the database first, then every file that names it, then redis, restarted once — choose your own on the
  *Server secrets* card); read them under *Server secrets → Show current values* or in the stack's `creds.txt`. A value
  that is already private is never touched, and an archive re-extracted over the stack does not lock the agent out (it
  keeps its own record of the accepted root password, and puts the stack's own internal password back into the
  templates). A server whose `.env` you set up yourself can be left exactly as it is: **Keep .env settings**
  (an installer question, a toggle of the install form, `GIO_KEEP_ENV`) switches these automatic replacements —
  and the `OUTER_IP` repair — off. Keep adminer / phpMyAdmin (8085 / 8087) off the Internet all the same. The install form's **?** beside
  *Advertised IP (WAN)* explains what goes there (your public IP, also behind a router / NAT)
- **Agent settings**: the *Agent settings* card (Server page, Agent tab) edits the agent's own configuration file on
  the box (23 settings — addresses, folders, disk reserves, download options; comments and other lines kept) and
  restarts the agent when a setting needs it; *Server folders* (Content tab) moves a server to another folder or
  drive (stopped, renamed or copied and verified, started again) or points the agent at another folder (a partial
  download beside a folder that holds no server goes along), and *Add version…* sets up a version the agent has no
  folder for (a folder, then the download, or a package already there). HTTPS
  downloads are fully verified without Python 3.13's extra-strict checks (which broke archive.org downloads on
  Windows); a download whose checksum is known in advance survives a certificate that cannot be verified. A Windows
  re-install of the local agent — the way to change its configuration, not to update it —
  keeps every server folder it has and every setting its form does not ask for, and pre-fills the ones it does ask
  for (only the token must be pasted). The form's default folder for a server on this PC is the games folder's
  `servers\<version>_live` — `%USERPROFILE%\Relic Games\servers\<version>_live` unless you chose another games folder;
  `<drive>:\relic_servers\<version>_live` when the games folder's path holds a character outside plain ASCII or a
  quote, is longer than 100 characters or lies inside `%LOCALAPPDATA%\Relic` —, the one Quick Start takes too; a
  Windows agent on another PC gets `C:\relic_servers\<version>_live`. The hotpatch mirror is served without a token, so
  its folder must be one of its own (no drive root, agent or server folder), and a server re-pointed at a stack Relic
  never provisioned is never given the default save automatically
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/derisk/IMPLEMENTATION-NOTES.md](docs/derisk/IMPLEMENTATION-NOTES.md) — design decisions and ground truth

## Repo layout

```
app/Relic.App/        .NET 10 + WebView2 host (window, JS<->C# bridge) + ui/ front-end (plain HTML/CSS/JS)
app/Relic.Core/       backend: isolation, launch, download, fiddler, server agent client, install/play
spikes/Relic.Spikes/  console runner that validates the risky mechanics (the test suite)
agent/                the server agent (gio_agent.py), installers, provisioning payloads
payload/              files Relic copies into the game folder (2.8 metadata patch, the bytes of the 3.2 login patch, Win11 injector, in-game enhancements DLL)
entertainment_experience/  the optional in-game enhancements menu (Akebi GC backport, Apache-2.0) — built separately into payload/<ver>/ee/
injector/             the Win11 injector: CMake wrapper + README around the mhynot2 submodule (built by build/build_dlls.ps1)
_bundled_navmesh/     OPTIONAL, usually absent: navmesh files for the pathfinding server that publish.ps1 ships (docs/NAVMESH.md)
config/               versions.sample.json, gamedata.json
installer/            Inno Setup script + language files
build/                publish.ps1, build_dlls.ps1, navmesh_tool.py, check_i18n.py, catalogue/icon tools
docs/                 architecture, server deployment, API, test checklist
```

## Translations

The UI and the C# side read flat key/value files from `app/Relic.App/ui/lang/`. English
(`en.json`) is the source of truth and is also embedded in the app as a fallback. To add a language:
copy `en.json` to `<code>.json`, translate the values (keep `{placeholders}`), add
`{ "code": "<code>", "name": "<Name>" }` to `lang/index.json`, run `python build/check_i18n.py`.
The original Romanian strings are archived in `lang/ro.json.bak` (not shipped); rename it to
`ro.json` and list it in `index.json` to bring Romanian back. The installer's texts live in
`installer/lang/en.iss` (Romanian archived in `installer/lang/ro.iss`).

## Security notes

- The agent token is a bearer secret: `config/agent.token`, `config/versions.json`, `.env`, SSH
  keys and every `*_ref/` folder are gitignored — keep them out of the repo and out of player builds.
  If a token ever appeared in a commit, rotate it (`install_agent.sh` accepts a new `GIO_AGENT_TOKEN`).
- The Fiddler root certificate is trusted only through the standard Windows consent dialog, never
  silently; only the game's own domains are decrypted.
- Relic runs without administrator rights. The only exceptions are explicit, optional steps, each behind a UAC
  prompt you click yourself: the local Windows agent's firewall rules — added with *Windows Firewall → Open port* on
  the *Agent on this PC* card, or by Quick Start's firewall step for a server other PCs join, and removed with *Also
  remove the Windows Firewall rules…* at its uninstall — and Quick Start turning WSL 2 on, or updating it, for Docker
  Desktop, a prompt its summary names before it starts. Docker Desktop itself is installed for your user, without
  administrator rights, and so is Fiddler: only when Windows does not let Fiddler install for your user alone (a policy
  or a security program) does its installer ask once, through the same kind of prompt.

## Credits & third-party components

- **mhynot2** by khang06 — https://github.com/khang06/mhynot2. The shipped `payload/common/ayy/anime/build/launcher.exe`
  and `mhynot2.dll` are built from it (the launcher as a console-subsystem executable, both with the static
  C runtime, so they need no Visual C++ redistributable);
  on Windows 11 the game only starts through this injector. Its source is the git submodule `injector/mhynot2`
  (a pinned commit, fetched at build time, not redistributed here); minhook (BSD-2-Clause) comes with it.
- **Akebi GC** — NctimeAza/AnimeGame-Cheat-3.3 v1.2.3, Apache-2.0 — https://github.com/NctimeAza/AnimeGame-Cheat-3.3.
  The optional **in-game enhancements menu (F1)** is a modified backport to game 1.6/2.8 kept in
  `entertainment_experience/` (its `LICENSE`, `NOTICE` and `VENDOR.md` record every change); it ships as
  `payload/<ver>/ee/CLibrary.dll.bin` and is injected by mhynot2's launcher after `mhynot2.dll` when the
  "In-game enhancements" setting is on (default; off = the plain launch). Its vendored dependencies, each
  under its own licence in `entertainment_experience/mod/cheat-base/vendor/`: Microsoft Detours, Dear ImGui
  (CallowBlack fork) + imgui-notify + Font Awesome, fmt, nlohmann/json, magic_enum, simpleIni, stb, WinReg.
- **2.8 `global-metadata.dat` patch** — a community patch without which the 2.8 client does not start
  (`payload/2.8/global-metadata.dat`). Author/source: TODO.
- **3.2 login patch** — a community patch without which the 3.2 client cannot log in to a private server: 687 bytes of
  `GenshinImpact_Data/Native/UserAssembly.dll` (`payload/3.2/UserAssembly.span.bin`), which Relic writes only into the
  unmodified file of the 3.2.0 build in the player's own game folder. Its author is not known.
- **RSAPatch** by 34736384 — https://github.com/34736384/RSAPatch. The shipped `payload/3.2/mhypbase.dll.bin` is
  built from it (the git submodule `rsapatch/RSAPatch` through the CMake wrapper in `rsapatch/`, with the dynamic C
  runtime, which it needs); Relic puts it in place of the 3.2 game's own `mhypbase.dll` — only over the original
  file, kept beside it as `mhypbase.dll.relic-orig`, or over an earlier build of its own.
- **GAA server-side data fixes** (the `NewActivityCondData.txt` family) — thanks to @AZ#7011.
- **The GIO server stacks** (docker-compose 1.6 / 2.8 / 3.2 packages) — their authors.
- **Dimbreath/AnimeGameData** (game data used by `build/make_catalog.py`), **GrasscutterCommandGenerator**
  (GM command reference).
- **Enka.Network** (UI sprites: weapons, artifacts, reward items), **Project Amber** — https://gi.yatta.moe
  (enemy sprites), **nanoka.cc** (the currency, resin and wish sprites Enka only carries at 64 px); all
  fetched by `build/make_catalog.py`. The picture of version 3.2 (`ui/assets/hero-3.2.webp`) and the splash tiles
  of the eight 3.2 characters (`ui/assets/splash/10000067.webp` … `10000074.webp`, rendered by
  `build/make_splashes.py`) are made from the wish art Enka.Network carries.
- **Fiddler Classic** by Telerik/Progress (installed on the player's machine).
- **Cinzel, Mulish, JetBrains Mono** (Google Fonts, OFL).
- Character art and game assets are © HoYoverse/miHoYo, used for identification only.

See also the *About* card in Settings inside the app.
