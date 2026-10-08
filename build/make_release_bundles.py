#!/usr/bin/env python3
"""Builds the release bundles that go beside the installers on a GitHub release.

    python build/make_release_bundles.py [--out dist] [--navmesh-zip <zip>] [--extra <file>]...
                                         [--versions 1.6 2.8]

Standard library only, Python 3.9 or newer, Windows or Linux (the workflow .github/workflows/build.yml
runs it on ubuntu-latest). Everything is read from the repository: agent/payloads/<ver>/ with its
manifest.json, payload/ with its manifest.json, so the bundles carry exactly what the launcher and the
agent ship, verified the same way.

What it writes into --out (default: <repo>/dist, gitignored):

  relic-server-fixes-<ver>.zip   the repaired server data files + the ready-made saves of a version, laid
                                 out for someone who applies them BY HAND to a GIO server stack, without
                                 the launcher or the agent (the readme.txt inside gives the steps):
                                   readme.txt
                                   server/data/txt/<file>              every manifest file of stage
                                                                       txt_fix or config, at its dst
                                   sdk/data/sdk.db, redis/dump.rdb     the save-stage files at their dst
                                   account_before_the_quest/hk4e_db_user.sql + readme.txt   the manifest's
                                                                       mysql dump (the default save)
                                   account_after_the_quest/hk4e_db_user.sql + readme.txt    the post-quest
                                                                       template dump
                                   events.sql                          the event schedule statements, to
                                                                       pipe into the mysql client
  relic-mhynot-patch.zip         the Windows 11 injector pair (ayy/anime/build/launcher.exe + mhynot2.dll,
                                 from payload/common/*.bin, sha256-verified against payload/manifest.json and
                                 refused when a file imports a Visual C++ runtime DLL),
                                 Run.bat at the root (extract into the game folder, beside
                                 GenshinImpact.exe), README.txt, and 2.8-only/global-metadata.dat + README.txt
                                 (the patched metadata the 2.8 client needs to start at all)
  relic-navmesh-2.8.zip          with --navmesh-zip: the navmesh archive, verified (it must hold
                                 2.8/navmesh.json, and every file the manifest lists with the listed size,
                                 md5 and sha256) and copied under the release asset name
  <name>                         with --extra: a file built elsewhere (RelicSetup.exe,
                                 RelicSetup-navmesh.exe) copied in so the sums and the notes cover it
  SHA256SUMS.txt                 sha256sum format, every asset of --out
  release-notes.md               a markdown table of every asset: name, size, sha256, what it is for

Exit code 0 = every bundle written and verified; 1 = a verification failed (a payload hash, a missing
manifest file, a navmesh zip that does not hold what it claims) -- nothing half-written is left behind
for that asset; 2 = usage. The zips are reproducible across Windows and Linux: fixed entry timestamps,
sorted entries, deflate, Unix as the creating system -- a local run gives the hashes of the release's
SHA256SUMS.txt.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import re
import shutil
import struct
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
AGENT_PAYLOADS = REPO / "agent" / "payloads"
CLIENT_PAYLOAD = REPO / "payload"
VERSIONS = ("1.6", "2.8")

# A fixed timestamp makes the zips reproducible (the same inputs give the same bytes).
ZIP_TIME = (1980, 1, 1, 0, 0, 0)

NAVMESH_ASSET = "relic-navmesh-2.8.zip"
MHYNOT_ASSET = "relic-mhynot-patch.zip"
# The pathfindingserver's own name grammar (NAVMESH_NAME_RE in agent/gio_agent.py).
NAVMESH_NAME_RE = re.compile(r"^(?:scene_\d+(?:_\d+)?(?:_-?\d+_-?\d+)?|scenepolygon_\d+_\d+_\d+)\.navmesh\Z")
NAVMESH_NAME_MAX = 128

# One line per release asset, for release-notes.md.
ASSET_NOTES = {
    "RelicSetup.exe": "The launcher installer (per user, no administrator rights). The one to take.",
    "RelicSetup-navmesh.exe": "The same build with the 2.8 navmesh bundle inside, for server admins who run "
                              "the pathfinding server: the agent installs the files into the stack at "
                              "*Prepare the server* / *Start*.",
    NAVMESH_ASSET: "The navmesh archive alone (2.8: the archipelago and the quest domains). Put it into the "
                   "agent's payloads folder and the agent adopts it, or let the launcher download it from the "
                   "Pathfinding server card.",
    "relic-server-fixes-1.6.zip": "The repaired 1.6 server data + the ready-made saves, for applying by hand "
                                  "to a GIO 1.6 stack without the launcher or the agent (readme.txt inside).",
    "relic-server-fixes-2.8.zip": "The repaired 2.8 server data + the ready-made saves, for applying by hand "
                                  "to a GIO 2.8 stack without the launcher or the agent (readme.txt inside).",
    MHYNOT_ASSET: "The Windows 11 injector (launcher.exe + mhynot2.dll) with a Run.bat for the game folder, "
                  "and the patched global-metadata.dat the 2.8 client needs. Relic applies both itself.",
}


class BundleError(Exception):
    """A verification failed; the asset is not written."""


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def read_json(path: Path) -> dict:
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError) as e:
        raise BundleError("%s: %s" % (path, e))


def mb(n: int) -> str:
    return "%.1f MB" % (n / 1048576.0)


def check_md5(path: Path, want, what: str):
    """The file exists and its md5 is the one the manifest records."""
    got = hashlib.md5(path.read_bytes()).hexdigest() if path.is_file() else None
    if got != str(want or "").lower():
        raise BundleError("%s: %s has md5 %s, the manifest expects %s" % (what, path, got, want))


# ---------------------------------------------------------------- zip writing

class Zip:
    """A zip written in one go: entries collected, sorted by name, fixed timestamps, deflate."""

    def __init__(self, path: Path):
        self.path = path
        self.entries: dict[str, bytes | Path] = {}

    def add_file(self, name: str, src: Path):
        if not src.is_file():
            raise BundleError("%s: missing file %s" % (self.path.name, src))
        self._put(name, src)

    def add_text(self, name: str, text: str, crlf: bool = False):
        if crlf:
            text = text.replace("\r\n", "\n").replace("\n", "\r\n")
        self._put(name, text.encode("utf-8"))

    def _put(self, name: str, data):
        name = name.replace("\\", "/")
        if name in self.entries:
            raise BundleError("%s: %s would be written twice" % (self.path.name, name))
        self.entries[name] = data

    def write(self) -> Path:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_name(self.path.name + ".part")
        try:
            with zipfile.ZipFile(tmp, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
                for name in sorted(self.entries):
                    info = zipfile.ZipInfo(name, date_time=ZIP_TIME)
                    info.compress_type = zipfile.ZIP_DEFLATED
                    # Unix as the creating system on every platform: the one header byte zipfile would
                    # otherwise set per OS, and the only way extractors honour the 0644 mode below.
                    info.create_system = 3
                    info.external_attr = 0o644 << 16
                    data = self.entries[name]
                    if isinstance(data, Path):
                        with open(data, "rb") as f, z.open(info, "w") as out:
                            shutil.copyfileobj(f, out, 1 << 20)
                    else:
                        z.writestr(info, data)
            tmp.replace(self.path)
        except BaseException:
            tmp.unlink(missing_ok=True)
            raise
        return self.path


# ---------------------------------------------------------------- server fixes

def _about(note: str) -> str:
    """The sentences of the manifest note up to and including the one naming the source: what the save
    is and where the fixes come from, without the technical record that follows."""
    parts = re.split(r"(?<=[.!?])\s+", (note or "").strip())
    out = []
    for p in parts:
        out.append(p)
        if p.startswith("Source"):
            break
    return " ".join(out)


def _password_line(password: str) -> str:
    if not password or password.strip().lower() == "any":
        return "Password: any -- the server does not verify it unless password verification was switched on"
    return "Password: %s (the server does not verify it unless password verification was switched on)" % password


def _events_sql(man: dict) -> tuple[list[str], list[str]]:
    """The manifest's own statements verbatim, then the ones the agent derives from its events block
    (apply_events / events_open_sql in agent/gio_agent.py): the archipelago events opened until
    openUntil -- a begin_time in the future or parked in the closed window becomes yesterday, a running
    event keeps its own, so its event-day counter goes on --, everything else parked in the closed
    window. The agent re-bases a re-opened event onto the running events' start instead of yesterday;
    by hand, on a stack set up from scratch, yesterday is what that comes to."""
    verbatim = [s for s in man.get("sql_events", []) if isinstance(s, str)]
    derived = []
    ev = man.get("events") or {}
    gaa = sorted(str(k) for k in (ev.get("gaa") or {}))
    if gaa:
        open_until = ev.get("openUntil", "2050-01-01 00:00:00")
        closed = ev.get("closedWindow") or ["1998-06-09 10:00:00", "1998-06-28 03:59:59"]
        ids = ", ".join(gaa)
        yesterday = "DATE_SUB(NOW(), INTERVAL 1 DAY)"
        if not any(s.strip().upper().startswith("USE ") for s in verbatim):
            derived.append("USE hk4e_db_config")
        derived.append("UPDATE t_activity_schedule_config SET begin_time = CASE WHEN begin_time > NOW() THEN %s "
                       "WHEN begin_time <= '%s' THEN %s ELSE begin_time END, end_time = '%s' WHERE schedule_id IN (%s)"
                       % (yesterday, closed[1], yesterday, open_until, ids))
        derived.append("UPDATE t_activity_schedule_config SET begin_time = '%s', end_time = '%s' "
                       "WHERE schedule_id NOT IN (%s)" % (closed[0], closed[1], ids))
    return verbatim, derived


def _event_lines(man: dict) -> list[str]:
    ev = man.get("events") or {}
    lines = []
    for title, key in (("Stay open", "gaa"), ("Closed (parked in the window)", "others")):
        items = ev.get(key) or {}
        if items:
            lines.append("     %s:" % title)
            for k in sorted(items, key=str):
                lines.append("       %s  %s" % (k, items[k]))
    return lines


def server_fixes_readme(man: dict, txt_names: list[str], save_names: list[str]) -> str:
    ver = man["version"]
    title = man.get("title", "")
    account = man.get("account", "")
    verbatim, derived = _events_sql(man)
    L = []
    L.append("Relic -- server fixes and ready-made saves for Genshin Impact %s (%s)" % (ver, title))
    L.append("=" * len(L[-1]))
    L.append("")
    L.append(_about(man.get("note", "")))
    L.append("")
    L.append("The Relic launcher's server agent applies everything in this archive by itself (Server Admin")
    L.append("mode -> Prepare the server). These steps are for a GIO %s server stack that is run by hand," % ver)
    L.append("without the agent. Every path below is relative to the stack folder (the one that holds")
    L.append("docker-compose.yml). Back up every file you replace first.")
    L.append("")
    L.append("What is in this archive")
    L.append("-----------------------")
    for n in txt_names:
        L.append("  server/data/txt/%-32s repaired server data table (TAB-separated)" % n)
    for n in save_names:
        L.append("  %-48s the ready-made save" % n)
    L.append("  %-48s the player database right before the quest" % "account_before_the_quest/hk4e_db_user.sql")
    L.append("  %-48s the same account after finishing the quest" % "account_after_the_quest/hk4e_db_user.sql")
    L.append("  %-48s the event schedule statements of step 5" % "events.sql")
    L.append("")
    L.append("Steps, in this order")
    L.append("--------------------")
    L.append("1. Stop the server: the stack's stop script, or in the stack folder")
    L.append("       docker compose down --remove-orphans")
    L.append("")
    L.append("2. Server data: copy every file of server/data/txt/ over the stack's server/data/txt/<same name>")
    L.append("   (keep the originals aside). They are TAB-separated tables -- copy them whole, never save them")
    L.append("   from a word processor.")
    L.append("")
    L.append("3. The save: copy sdk/data/sdk.db over the stack's sdk/data/sdk.db and redis/dump.rdb over the")
    L.append("   stack's redis/dump.rdb (keep the originals aside). The account of the player database lives")
    L.append("   in sdk.db, so the two go together.")
    L.append("")
    L.append("4. The player database: pick ONE of the two account folders (before or after the quest), then")
    L.append("   import its hk4e_db_user.sql into the database hk4e_db_user, replacing what is there.")
    L.append("   Start only the database (and its web front end) first:")
    L.append("       docker compose up -d mysql phpmyadmin")
    L.append("   a) phpMyAdmin (http://<server>:8087, user root, the MYSQL_ROOT_PASSWORD of the stack's .env):")
    L.append("      open hk4e_db_user, Check all -> Drop (yes), then Import the .sql file.")
    L.append("   b) the mysql command line inside the container (the password is the same one; docker")
    L.append("      compose runs in the stack folder, so give the .sql path as it lies on your disk):")
    L.append("       docker compose exec -T mysql mysql -uroot -p\"<root password>\" \\")
    L.append("         -e \"DROP DATABASE IF EXISTS hk4e_db_user; CREATE DATABASE hk4e_db_user;\"")
    L.append("       docker compose exec -T mysql mysql -uroot -p\"<root password>\" hk4e_db_user \\")
    L.append("         < account_before_the_quest/hk4e_db_user.sql")
    L.append("")
    L.append("5. The events: run events.sql (at the root of this archive) on the database hk4e_db_config --")
    L.append("   phpMyAdmin -> hk4e_db_config -> Import (or SQL, pasted), or the command line:")
    L.append("       docker compose exec -T mysql mysql -uroot -p\"<root password>\" < events.sql")
    L.append("   The statements, as the agent runs them (its only difference: an event it re-opens on a")
    L.append("   server that already ran others joins their start date instead of yesterday):")
    L.append("")
    for s in verbatim + derived:
        L.append("       %s;" % s)
    L.append("")
    L.append("   What they do: the archipelago events stay open (until %s), every other event is pushed"
             % ((man.get("events") or {}).get("openUntil", "2050-01-01 00:00:00")[:10]))
    L.append("   into a closed window in the past -- a server set up by its own bootstrap starts EVERY event,")
    if ver == "2.8":
        L.append("   and the GAA2 unlock quest only starts once the 1.6 events it refers to have ended.")
    else:
        L.append("   events of other versions included.")
    L.extend(_event_lines(man))
    L.append("")
    L.append("6. Start the server: the stack's start script, or")
    L.append("       docker compose up -d")
    L.append("   and wait until every service is up (the game server takes a minute or two).")
    L.append("")
    L.append("7. Log in with the account below. The quest is ready to start (or, with the \"after\" folder, done).")
    L.append("       Account:  %s" % account)
    L.append("       %s" % _password_line(man.get("password", "")))
    L.append("")
    L.append("Credits")
    L.append("-------")
    L.append("The repaired data files and the saves grew out of the community's GAA %s fixes" % ver
             + (" (thanks @AZ#7011)." if "AZ#7011" in (man.get("note") or "") else "."))
    L.append("Relic -- the launcher and the server agent these files are taken from -- is on the release page")
    L.append("this archive was downloaded from.")
    L.append("")
    return "\n".join(L)


def account_readme(man: dict, when: str) -> str:
    return "\n".join([
        "The player database (hk4e_db_user) %s." % when,
        "Account: %s" % man.get("account", ""),
        _password_line(man.get("password", "")),
        "Import it as described in the readme.txt at the root of this archive (step 4). It belongs with",
        "the sdk/data/sdk.db and redis/dump.rdb of this archive: the account is registered there.",
        "",
    ])


def build_server_fixes(ver: str, out: Path) -> Path:
    base = AGENT_PAYLOADS / ver
    man = read_json(base / "manifest.json")
    if str(man.get("version")) != ver:
        raise BundleError("%s/manifest.json says version %r" % (ver, man.get("version")))
    z = Zip(out / ("relic-server-fixes-%s.zip" % ver))
    txt_names, save_names = [], []
    for e in man.get("files", []):
        stage = e.get("stage")
        if stage == "save" and e.get("tsv"):
            stage = "config"           # the agent's effective_stage
        dst = str(e["dst"]).replace("\\", "/")
        if stage in ("txt_fix", "config"):
            if not dst.startswith("server/data/txt/"):
                raise BundleError("%s: %s is a %s file outside server/data/txt/" % (ver, dst, stage))
            z.add_file(dst, base / e["src"])
            txt_names.append(dst[len("server/data/txt/"):])
        elif stage == "save":
            z.add_file(dst, base / e["src"])
            save_names.append(dst)
        else:
            raise BundleError("%s: unknown stage %r for %s" % (ver, stage, dst))
    if not txt_names or not save_names:
        raise BundleError("%s: the manifest lists no %s files" % (ver, "data" if not txt_names else "save"))
    mysql = [m for m in man.get("mysql", []) if m.get("db") == "hk4e_db_user"]
    if len(mysql) != 1:
        raise BundleError("%s: expected exactly one hk4e_db_user dump in manifest mysql[]" % ver)
    pre_sql = base / mysql[0]["src"]
    # The same dump is the pre-gaa template, whose entry carries its md5.
    for t in man.get("templates", []):
        if t.get("id") == "pre-gaa" and str(t.get("sql", "")).replace("\\", "/") == str(mysql[0]["src"]).replace("\\", "/"):
            check_md5(pre_sql, t.get("md5"), ver)
            break
    else:
        raise BundleError("%s: the manifest has no pre-gaa template for %s" % (ver, mysql[0]["src"]))
    z.add_file("account_before_the_quest/hk4e_db_user.sql", pre_sql)
    z.add_text("account_before_the_quest/readme.txt", account_readme(man, "right before the quest starts"))
    post = [t for t in man.get("templates", []) if t.get("id") == "post-gaa"]
    if len(post) != 1:
        raise BundleError("%s: the manifest has no post-gaa template" % ver)
    post_sql = base / post[0]["sql"]
    check_md5(post_sql, post[0].get("md5"), ver)
    z.add_file("account_after_the_quest/hk4e_db_user.sql", post_sql)
    z.add_text("account_after_the_quest/readme.txt", account_readme(man, "after the quest was finished"))
    verbatim, derived = _events_sql(man)
    z.add_text("events.sql", "\n".join("%s;" % s for s in verbatim + derived) + "\n")
    z.add_text("readme.txt", server_fixes_readme(man, txt_names, save_names), crlf=True)
    return z.write()


# ---------------------------------------------------------------- mhynot patch

RUN_BAT = """@echo off
rem Starts the game through the Windows 11 injector: launcher.exe creates GenshinImpact.exe and loads
rem mhynot2.dll in place of the anti-cheat driver. Keep this file beside GenshinImpact.exe.
setlocal
set "GAME=%~dp0"
if "%GAME:~-1%"=="\\" set "GAME=%GAME:~0,-1%"
if not exist "%GAME%\\GenshinImpact.exe" (
  echo GenshinImpact.exe was not found beside this file. Extract the archive into the game folder.
  pause
  exit /b 1
)
if not exist "%GAME%\\ayy\\anime\\build\\launcher.exe" (
  echo ayy\\anime\\build\\launcher.exe is missing. Extract the whole archive into the game folder.
  pause
  exit /b 1
)
cd /d "%GAME%\\ayy\\anime"
.\\build\\launcher.exe "%GAME%" "%GAME%\\ayy\\anime\\build\\mhynot2.dll"
if errorlevel 1 (
  echo The injector reported an error ^(exit code %errorlevel%^). See the messages above.
  pause
)
endlocal
"""

MHYNOT_README = """Relic -- the Windows 11 injector for the classic Genshin Impact clients (1.6 / 2.8)
================================================================================

What it is
----------
On Windows 11 the game's own anti-cheat driver (mhyprot2) does not load for these classic clients, so
GenshinImpact.exe never gets past its start. launcher.exe starts the game with mhynot2.dll in the
driver's place: a user-mode emulation of the driver. Windows 10 starts the game directly and does not
need this; Run.bat works there as well.

What the game needs
-------------------
launcher.exe and mhynot2.dll need nothing installed. The game's own plugins use the Microsoft Visual C++
Redistributable (x64): on a Windows without it the 1.6 client reports "Plugins: Failed to load ...
MTBenchmark_Windows.dll with error '0x7e'" at its start. Install the redistributable from Microsoft and
start the game again.

Credits
-------
mhynot2 by khang06 -- https://github.com/khang06/mhynot2 . launcher.exe and mhynot2.dll are built from it
(the launcher as a console program, so that its messages can be read). minhook by Tsuda Kageyu
(BSD-2-Clause) is linked inside mhynot2.dll. The upstream repository publishes no licence file, so its
source is not redistributed; the Relic repository fetches it as a git submodule (injector/mhynot2) and
ships only these compiled files, credited in its README ("Credits") and payload/manifest.json.

How to use
----------
1. Extract this archive into the game folder -- the folder that holds GenshinImpact.exe -- so that
   Run.bat lies beside it and ayy\\anime\\build\\ holds launcher.exe and mhynot2.dll.
2. Start the game with Run.bat (double-click; from a console you see the injector's messages).
   Fiddler (or whatever redirects the game to your private server) must be running as usual.
3. For the 2.8 client, apply the patch in 2.8-only\\ first (its README.txt) -- without it the 2.8
   client does not start at all.

The Relic launcher does all of this itself: it puts these files into the game folder when a version is
installed and checks them before every launch, and on Windows 11 it starts the game through them.
"""

METADATA_README = """The 2.8 client does not start without this patched file.

Copy global-metadata.dat over
    <game folder>\\{dst}
and keep the original aside (for example as global-metadata.dat.orig). The game folder is the one that
holds GenshinImpact.exe. Only the 2.8 client needs it; leave the 1.6 client alone.

The Relic launcher applies this patch itself when 2.8 is installed and restores it before every launch
(the original is kept as global-metadata.dat.relic-orig).
"""


# The Visual C++ runtime DLLs, which are not a part of Windows: the name rule of build/pe_imports.ps1
# (Test-VcRuntimeDllName), in Python because this script runs on Linux too -- the two expressions change
# together. The README of the bundle says the injector pair needs nothing installed.
VC_RUNTIME_RE = re.compile(
    r"^(vcruntime|msvcp|msvcr|concrt|vccorlib|vcomp|vcamp|mfcm?|atl|libomp)(70|71|80|90|100|110|120|140)"
    r"(?!_win|_clr|_1_clr)|^ucrtbased\.dll$", re.I)


def pe_import_names(path: Path) -> list[str]:
    """The DLL names of a PE file's import directory, read without loading the file."""
    b = path.read_bytes()
    try:
        pe = struct.unpack_from("<i", b, 0x3C)[0]
        if b[:2] != b"MZ" or pe < 0 or b[pe:pe + 4] != b"PE\0\0":
            raise ValueError("no PE header")
        nsec = struct.unpack_from("<H", b, pe + 6)[0]
        opt = pe + 24
        sec0 = opt + struct.unpack_from("<H", b, pe + 20)[0]
        dirs = opt + (112 if struct.unpack_from("<H", b, opt)[0] == 0x20B else 96)   # PE32+ / PE32
        sections = [struct.unpack_from("<IIII", b, sec0 + 40 * i + 8) for i in range(nsec)]

        def offset(rva: int) -> int:
            for vsize, vaddr, rsize, raw in sections:
                if vaddr <= rva < vaddr + max(vsize, rsize):
                    return rva - vaddr + raw
            raise ValueError("rva outside every section")

        names = []
        rva = struct.unpack_from("<I", b, dirs + 8)[0]                               # directory 1 = imports
        d = offset(rva) if rva else -1
        while d >= 0:
            name_rva = struct.unpack_from("<I", b, d + 12)[0]
            if not name_rva:
                break
            o = offset(name_rva)
            names.append(b[o:b.index(b"\0", o)].decode("ascii", "replace"))
            d += 20
        return names
    except (ValueError, struct.error) as e:
        raise BundleError("%s: not a readable PE file (%s)" % (path, e))


def build_mhynot_patch(out: Path) -> Path:
    man = read_json(CLIENT_PAYLOAD / "manifest.json")
    z = Zip(out / MHYNOT_ASSET)

    def take(entry: dict, name: str):
        src = CLIENT_PAYLOAD / entry["src"]
        if not src.is_file():
            raise BundleError("payload/manifest.json: missing %s" % src)
        got = sha256_file(src)
        if got != str(entry.get("sha256", "")).lower():
            raise BundleError("%s: sha256 %s, payload/manifest.json expects %s" % (src, got, entry.get("sha256")))
        z.add_file(name, src)

    common = {str(e.get("dst")).replace("\\", "/"): e for e in man.get("common", [])}
    for dst in ("ayy/anime/build/launcher.exe", "ayy/anime/build/mhynot2.dll"):
        if dst not in common:
            raise BundleError("payload/manifest.json: no common entry for %s" % dst)
        take(common[dst], dst)
        vc = [n for n in pe_import_names(CLIENT_PAYLOAD / common[dst]["src"]) if VC_RUNTIME_RE.match(n)]
        if vc:
            raise BundleError("payload/%s imports %s: the injector pair must not depend on the Visual C++ "
                              "redistributable (build/build_dlls.ps1 links it with the static C runtime)"
                              % (common[dst]["src"], ", ".join(vc)))
    meta = [e for e in man.get("versions", {}).get("2.8", [])
            if str(e.get("dst", "")).replace("\\", "/").endswith("/global-metadata.dat")]
    if len(meta) != 1:
        raise BundleError("payload/manifest.json: expected one 2.8 global-metadata.dat entry")
    take(meta[0], "2.8-only/global-metadata.dat")
    dst_win = str(meta[0]["dst"]).replace("/", "\\")
    z.add_text("2.8-only/README.txt", METADATA_README.replace("{dst}", dst_win), crlf=True)
    z.add_text("README.txt", MHYNOT_README, crlf=True)
    z.add_text("Run.bat", RUN_BAT, crlf=True)
    return z.write()


# ---------------------------------------------------------------- navmesh zip

def verify_navmesh_zip(path: Path, ver: str = "2.8") -> int:
    """The zip holds <ver>/navmesh.json (format 1, that version) and every listed file with the listed
    size, md5 and sha256 and a name of the server's grammar. Returns the file count."""
    if not path.is_file():
        raise BundleError("navmesh zip: %s is not a file" % path)
    try:
        zf = zipfile.ZipFile(path)
    except zipfile.BadZipFile as e:
        raise BundleError("navmesh zip: %s: %s" % (path, e))
    with zf:
        names = set(zf.namelist())
        manifest_name = "%s/navmesh.json" % ver
        if manifest_name not in names:
            raise BundleError("navmesh zip: %s holds no %s (entries: %s)" % (
                path.name, manifest_name, ", ".join(sorted(names)[:5]) or "none"))
        try:
            m = json.loads(zf.read(manifest_name).decode("utf-8"))
        except (ValueError, UnicodeDecodeError) as e:
            raise BundleError("navmesh zip: %s is not valid JSON: %s" % (manifest_name, e))
        if not isinstance(m, dict):
            raise BundleError("navmesh zip: %s is not a JSON object" % manifest_name)
        if m.get("format") != 1 or str(m.get("version")) != ver:
            raise BundleError("navmesh zip: %s has format %r / version %r, expected 1 / %s" % (
                manifest_name, m.get("format"), m.get("version"), ver))
        files = m.get("files") or []
        if not files:
            raise BundleError("navmesh zip: %s lists no files" % manifest_name)
        listed = set()
        for i, e in enumerate(files):
            # The entry's fields, read once: a manifest that is not what the build tool writes (an entry
            # that is no object, a size that is no integer) is an ERROR line, never a traceback.
            try:
                name = str(e["name"])
                size = int(e["size"])
                want_sha = str(e["sha256"]).lower()
                want_md5 = str(e["md5"]).lower()
            except (TypeError, ValueError, KeyError, AttributeError):
                raise BundleError("navmesh zip: entry %d of %s is malformed (%r)" % (i, manifest_name, e))
            if len(name) > NAVMESH_NAME_MAX or not NAVMESH_NAME_RE.match(name):
                raise BundleError("navmesh zip: %r is not a file name the pathfindingserver loads" % name)
            member = "%s/NavMesh/%s" % (ver, name)
            if member not in names:
                raise BundleError("navmesh zip: %s is listed but missing from the zip" % member)
            listed.add(member)
            info = zf.getinfo(member)
            if info.file_size != size:
                raise BundleError("navmesh zip: %s is %d bytes, navmesh.json expects %d" % (
                    member, info.file_size, size))
            h256, hmd5 = hashlib.sha256(), hashlib.md5()
            with zf.open(member) as f:
                for chunk in iter(lambda: f.read(1 << 20), b""):
                    h256.update(chunk)
                    hmd5.update(chunk)
            if h256.hexdigest() != want_sha:
                raise BundleError("navmesh zip: %s has sha256 %s, navmesh.json expects %s" % (
                    member, h256.hexdigest(), want_sha))
            if hmd5.hexdigest() != want_md5:
                raise BundleError("navmesh zip: %s has md5 %s, navmesh.json expects %s" % (
                    member, hmd5.hexdigest(), want_md5))
        stray = sorted(n for n in names if n.endswith(".navmesh") and n not in listed)
        if stray:
            raise BundleError("navmesh zip: %d .navmesh file(s) not listed in %s: %s" % (
                len(stray), manifest_name, ", ".join(stray[:5])))
        return len(files)


def take_navmesh_zip(src: Path, out: Path) -> Path:
    n = verify_navmesh_zip(src)
    dst = out / NAVMESH_ASSET
    out.mkdir(parents=True, exist_ok=True)
    if src.resolve() != dst.resolve():
        shutil.copyfile(src, dst)
    print("  %s: %d navmesh file(s) verified" % (NAVMESH_ASSET, n))
    return dst


def take_extra(src: Path, out: Path) -> Path:
    if not src.is_file():
        raise BundleError("--extra: %s is not a file" % src)
    dst = out / src.name
    out.mkdir(parents=True, exist_ok=True)
    if src.resolve() != dst.resolve():
        shutil.copyfile(src, dst)
    return dst


# ---------------------------------------------------------------- sums + notes

def write_sums_and_notes(out: Path, assets: list[Path]):
    rows = []
    for p in sorted(assets, key=lambda p: p.name.lower()):
        rows.append((p.name, p.stat().st_size, sha256_file(p)))
    with open(out / "SHA256SUMS.txt", "w", encoding="utf-8", newline="\n") as f:
        for name, _, sha in rows:
            f.write("%s  %s\n" % (sha, name))
    with open(out / "release-notes.md", "w", encoding="utf-8", newline="\n") as f:
        f.write("| Asset | Size | SHA-256 | What it is for |\n|---|---|---|---|\n")
        for name, size, sha in rows:
            note = ASSET_NOTES.get(name, "")
            f.write("| `%s` | %s | `%s` | %s |\n" % (name, mb(size), sha, note))
        f.write("\n`SHA256SUMS.txt` lists the same hashes in `sha256sum -c` format.\n")
    return rows


# ---------------------------------------------------------------- main

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Builds the release bundles (see the module docstring).")
    ap.add_argument("--out", default=str(REPO / "dist"), help="output folder (default: <repo>/dist)")
    ap.add_argument("--navmesh-zip", help="the navmesh archive to verify and attach as %s" % NAVMESH_ASSET)
    ap.add_argument("--extra", action="append", default=[],
                    help="a file built elsewhere to copy in and list (RelicSetup.exe, ...); repeatable")
    ap.add_argument("--versions", nargs="+", default=list(VERSIONS), help="server versions (default: 1.6 2.8)")
    ap.add_argument("--no-sums", action="store_true", help="do not write SHA256SUMS.txt / release-notes.md")
    a = ap.parse_args(argv)
    out = Path(a.out).resolve()
    assets: list[Path] = []
    try:
        for ver in a.versions:
            if ver not in VERSIONS:
                raise BundleError("unknown version %r (known: %s)" % (ver, ", ".join(VERSIONS)))
            p = build_server_fixes(ver, out)
            print("  %s: %s" % (p.name, mb(p.stat().st_size)))
            assets.append(p)
        p = build_mhynot_patch(out)
        print("  %s: %s" % (p.name, mb(p.stat().st_size)))
        assets.append(p)
        if a.navmesh_zip:
            assets.append(take_navmesh_zip(Path(a.navmesh_zip), out))
        for x in a.extra:
            p = take_extra(Path(x), out)
            print("  %s: %s (extra)" % (p.name, mb(p.stat().st_size)))
            assets.append(p)
        if not a.no_sums:
            rows = write_sums_and_notes(out, assets)
            for name, size, sha in rows:
                print("  %-28s %10s  %s" % (name, mb(size), sha))
            print("  SHA256SUMS.txt + release-notes.md written")
    except BundleError as e:
        print("ERROR: %s" % e, file=sys.stderr)
        return 1
    print("OK: %d asset(s) in %s" % (len(assets), out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
