#!/usr/bin/env python3
"""Drive the dev-only DevTest feature of the enhancements DLL from outside the game.

The DevTest build of the DLL (built only by entertainment_experience/tools/build_ee.ps1 -Config DevTest, never
shipped: it carries a marker every release gate refuses) serves one command at a time through files in a folder next
to it, <GameDir>\\ayy\\anime\\ee\\devtest\\:

  cmd.json            written here: {"id": <integer, not 0>, "op": "<name>", ...}; taken back once it is answered
  results/<id>.json   that command's answer: {"id", "op", "ok", "state": "running"|"done", "outcome"?, "error"?, ...},
                      replaced whole while a long action runs; deleted here once its final state is read
  results/0.json      what was wrong with a cmd.json that is not a command
  status.json         twice a second: the marker, the build, focus, screen, scene, position, uid, camera, busy, counters
  shots/<stem>.png    a picture of the game's back buffer, with its sidecar <stem>.json (moved out of the game folder
                      when it is collected: shot --collect, and every shot of a scenario run)
  devtest.log         the feature's own lines
  driver.lock         one driver per folder

  python -B build/devtest_driver.py [--version 2.8 | --dir <devtest folder>] <command> ...
    status [--ask]                           status.json as the game last wrote it (--ask: the status command too)
    ping                                     the ops this build serves
    wait [--in-game] [--scene N] [--scene-not N] [--uid-set] [--page NAME] [--frames N] [--seconds S] [--moved M] [--timeout S]
    shot [--overlay] [--hud keep|hide] [--after-frames N] [--out STEM] [--timeout S] [--collect FOLDER]
    mark TEXT                                a line in devtest.log and the diag board
    teleport X Y Z [--mode auto|direct|map] [--arrive M] [--timeout S] [--hold S]
    camera set X Y Z [--pitch P] [--yaw Y] [--roll R] [--fov F] [--drive 0|1] [--carry 0|1]
    camera move X Y Z [--pitch P] [--yaw Y] [--roll R] [--fov F] [--seconds S] [--ease linear|in|out|inOut] [--drive 0|1] [--carry 0|1]
    camera path KEYS.json [--loop]           KEYS.json = [{"pos": [x, y, z], "pitch", "yaw", "roll", "fov", "seconds", "hold", "ease"}, ...]
                                             (a looping path answers once it runs: camera release ends it)
    camera release
    entities [--radius M] [--kinds monster,gadget,avatar,npc] [--limit N]
    avatar                                   the current character and the team
    hud --visible 0|1                        the game's own UI
    overlay [--quiet 0|1] [--menu hide|show] the mod's overlay
    feature --section S --name N --value JSON | feature --restore
    ui-tree [--root PATH] [--depth N] [--active-only] [--no-texts] [--limit N]
    ui-click PATH [--via onClick|submit]
    login --account NAME [--password TEXT | --password-env VAR] [--timeout S]
    enter [--button bg|start] [--timeout S]
    quit                                     the game quits the way its own menu does (Application.Quit)
    raw '<json>' [--timeout S]               any command, as it is
    selftest  (or --selftest)                the channel against a stand-in for the game (a thread): no game needed

The game folder of a version is read from Relic's state file (%LOCALAPPDATA%\\Relic\\state.json), which this script
only reads. Before relying on an op, ask `ping` which ops the build serves: there is no protocol number. Standard
library only; run it with python -B.
"""
import argparse
import json
import math
import os
import shutil
import struct
import sys
import tempfile
import threading
import time
import zlib

# The marker of a DevTest build, put together from two parts so that no file of the repository carries it whole.
MARKER = "".join(("RELIC-DEV", "TEST-BUILD"))
CHANNEL = "devtest"
LOCK_NAME = "driver.lock"
STATUS_FRESH = 3.0       # seconds: the game rewrites status.json every 500 ms while its game thread runs
ALIVE_WAIT = 20.0        # a loading screen can hold the game thread for seconds before a command is written
LONG_PATIENCE = 30.0     # seconds without a fresh status before the wait for a long action calls the game gone
ANSWER_TIMEOUT = 30.0    # seconds for the first answer of any command
LONG_MARGIN = 30.0       # beyond an op's own timeout, for its final answer
COMMAND_MAX = 1 << 20  # bytes: the game refuses a larger cmd.json (results/0.json)
# The time limit the game itself gives a long action when its command names none (the DevTest feature's defaults).
DEFAULT_LONG = {"wait": 60.0, "shot": 10.0, "teleport": 60.0, "login": 60.0, "enter": 180.0}
CAMERA_START = 15.0      # seconds the free camera has to start in before the game ends a camera action (notStarted)
CAMERA_KEY_SECONDS = 2.0  # a path key's own default travel time


class DriverError(Exception):
    pass


class GameGone(DriverError):
    """The game stopped writing its status: nothing more can be asked of it."""


def unhindered(fn, tries=40, pause=0.025):
    """Replace or remove a file the game may have open for a moment (Windows refuses meanwhile): try again shortly."""
    for i in range(tries):
        try:
            return fn()
        except PermissionError:
            if i == tries - 1:
                raise
            time.sleep(pause)


def read_json(path):
    """A JSON object from a file, or None when the file is not there, is being replaced or does not parse yet."""
    try:
        with open(path, "r", encoding="utf-8") as f:
            text = f.read()
    except OSError:
        return None
    if not text.strip():
        return None
    try:
        obj = json.loads(text)
    except ValueError:
        return None
    return obj if isinstance(obj, dict) else None


def write_json_atomic(path, obj, indent=None):
    tmp = "{}.{}.{}.tmp".format(path, os.getpid(), threading.get_ident())
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(obj, f, indent=indent)
    unhindered(lambda: os.replace(tmp, path))


def state_path():
    return os.path.join(os.environ.get("LOCALAPPDATA", ""), "Relic", "state.json")


def game_dir(version, state_file=None):
    """The game folder Relic installed <version> in, from its state file (read, never written)."""
    path = state_file or state_path()
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            state = json.load(f)
    except (OSError, ValueError) as e:
        raise DriverError("Relic's state file {} cannot be read: {}".format(path, e))
    for inst in state.get("Installed") or []:
        if isinstance(inst, dict) and str(inst.get("Id", "")).lower() == str(version).lower():
            d = inst.get("GameDir")
            if isinstance(d, str) and d:
                return d
    raise DriverError("version {} is not installed in Relic ({})".format(version, path))


def profile_id(version, state_file=None):
    """The profile slot Relic keeps <version>'s registry key and LocalLow folder in (None when it names none)."""
    path = state_file or state_path()
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            state = json.load(f)
    except (OSError, ValueError):
        return None
    for inst in state.get("Installed") or []:
        if isinstance(inst, dict) and str(inst.get("Id", "")).lower() == str(version).lower():
            pid = inst.get("ProfileId")
            return pid if isinstance(pid, str) and pid and not any(c in pid for c in "\\/.") else None
    return None


def mod_dir(version, state_file=None):
    return os.path.join(game_dir(version, state_file), "ayy", "anime", "ee")


def channel_dir(version, state_file=None):
    return os.path.join(mod_dir(version, state_file), CHANNEL)


def hold_folder(folder):
    """One driver at a time on a folder: two would replace each other's commands. The hold is a locked file the
    system lets go of when this process ends, however it ends. -> the open file (keep it), or None when another
    driver holds the folder."""
    try:
        f = open(os.path.join(folder, LOCK_NAME), "a+b")
    except OSError as e:
        raise DriverError("{}: {}".format(os.path.join(folder, LOCK_NAME), e))
    try:
        if os.name == "nt":
            import msvcrt
            f.seek(0)
            msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(f.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        f.close()
        return None
    return f


def release_folder(folder, hold):
    if hold is None:
        return
    hold.close()
    if os.name == "nt":     # a lock file another driver has open cannot be removed: its own stays
        try:
            os.remove(os.path.join(folder, LOCK_NAME))
        except OSError:
            pass


def horizontal(a, b):
    return math.hypot(float(a[0]) - float(b[0]), float(a[2]) - float(b[2]))


def _seconds(value, default):
    return float(value) if isinstance(value, (int, float)) and not isinstance(value, bool) and value >= 0 else default


def loops(cmd):
    """A camera path that runs until `camera release`: it has no final answer to wait for."""
    path = cmd.get("path") if cmd.get("op") == "camera" else None
    return isinstance(path, dict) and path.get("loop") is True


def final_wait(cmd):
    """How long the final answer of a long action may take: the command's own `timeout` (the limit the game gives
    the action), else the game's default for the op. A camera action has no limit in the game: it takes its move's
    or its path's seconds (twice over -- the game counts them by frames, at most 0.1 s a frame) after the free camera
    started, which may take CAMERA_START. None for a looping path."""
    own = cmd.get("timeout")
    if isinstance(own, (int, float)) and not isinstance(own, bool):
        return float(own)
    if cmd.get("op") != "camera":
        return DEFAULT_LONG.get(cmd.get("op"), 60.0)
    if loops(cmd):
        return None
    travel = 0.0
    if isinstance(cmd.get("move"), dict):
        travel = _seconds(cmd["move"].get("seconds"), 0.0)
    elif isinstance(cmd.get("path"), dict) and isinstance(cmd["path"].get("keys"), list):
        for key in cmd["path"]["keys"]:
            if isinstance(key, dict):
                travel += _seconds(key.get("seconds"), CAMERA_KEY_SECONDS) + _seconds(key.get("hold"), 0.0)
    return CAMERA_START + 2.0 * travel


class Channel:
    """The command channel of the DevTest feature: one id per command, each answer in a file of its own."""

    def __init__(self, folder):
        self.folder = folder
        self.cmd_path = os.path.join(folder, "cmd.json")
        self.status_path = os.path.join(folder, "status.json")
        self.results_dir = os.path.join(folder, "results")
        self._next_id = int(time.time() * 1000) % 1000000000 * 10
        self._stamp = 0
        self.fresh = STATUS_FRESH   # seconds within which a status counts as the game's current one
        self.status_time = 0.0      # when the status last returned was written
        self.last_status = None
        self._ops = None

    def result_path(self, cmd_id):
        return os.path.join(self.results_dir, "{}.json".format(int(cmd_id)))

    def status(self, max_age=None):
        """status.json, when the game wrote it within max_age seconds (by default the channel's `fresh`). The file's
        time is asked of the handle that read it: the game puts a new file in its place twice a second, and a second
        look by name can fall between two of them."""
        if max_age is None:
            max_age = self.fresh
        try:
            with open(self.status_path, "r", encoding="utf-8") as f:
                text = f.read()
                written = os.fstat(f.fileno()).st_mtime
        except OSError:
            return None
        if time.time() - written > max_age or not text.strip():
            return None
        try:
            st = json.loads(text)
        except ValueError:
            return None
        if not isinstance(st, dict):
            return None
        self.status_time = written
        self.last_status = st
        return st

    def require_alive(self, wait=ALIVE_WAIT):
        deadline = time.time() + wait
        while True:
            st = self.status()
            if st is not None:
                return st
            if time.time() >= deadline:
                raise GameGone("no fresh status.json in {} -- is the game running with the DevTest build of the "
                               "enhancements DLL?".format(self.folder))
            time.sleep(0.25)

    def wait_status(self, accept, timeout, patience=LONG_PATIENCE, step=0.25):
        """The first fresh status `accept(status)` takes. The game is called gone when no fresh status comes for
        `patience` seconds."""
        deadline = time.time() + timeout
        silent = None
        while True:
            now = time.time()
            st = self.status()
            if st is None:
                silent = silent or now
                if now - silent >= patience:
                    raise GameGone("the game stopped writing status.json ({:.0f} s)".format(now - silent))
            else:
                silent = None
                if accept(st):
                    return st
            if now >= deadline:
                raise DriverError("no status within {:.0f} s was what the wait asked for".format(timeout))
            time.sleep(step)

    def _write_command(self, cmd):
        tmp = "{}.{}.tmp".format(self.cmd_path, os.getpid())
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(cmd, f)
            unhindered(lambda: os.replace(tmp, self.cmd_path))
        except OSError as e:
            raise DriverError("cmd.json cannot be written: {}".format(e))
        # A time stamp of its own for every command: a build that reads cmd.json only when its stamp has moved must
        # never find two commands under one, and the clock that stamps files moves in steps.
        self._stamp = max(time.time_ns(), self._stamp + 1000000)
        os.utime(self.cmd_path, ns=(self._stamp, self._stamp))

    def _take_back(self, cmd_id):
        """Remove cmd.json while it still holds this command -- another driver's is not this one's to remove."""
        own = read_json(self.cmd_path)
        if isinstance(own, dict) and own.get("id") == cmd_id:
            try:
                unhindered(lambda: os.remove(self.cmd_path))
            except OSError:
                pass

    def _forget(self, cmd_id):
        try:
            unhindered(lambda: os.remove(self.result_path(cmd_id)))
        except OSError:
            pass

    def zero_since(self, since_ns):
        """results/0.json when the game wrote it at or after `since_ns`: what it found wrong with a cmd.json."""
        path = self.result_path(0)
        try:
            if os.stat(path).st_mtime_ns < since_ns:
                return None
        except OSError:
            return None
        return read_json(path) or {"error": "results/0.json does not parse"}

    def send(self, cmd, timeout=ANSWER_TIMEOUT):
        """Write one command and wait for the first answer under its id (state running or done). Answered, refused,
        given up on or interrupted, the command is taken back: a file found by a game that starts later is not a
        command, what was reported as failed must not be carried out afterwards, and a command that carries a
        password does not stay in the game folder."""
        self.require_alive()
        self._next_id += 1
        cmd = dict(cmd)
        cmd["id"] = self._next_id
        if len(json.dumps(cmd).encode("utf-8")) > COMMAND_MAX:
            raise DriverError("command {} ({}) is larger than the {} bytes a command may be".format(cmd["id"], cmd.get("op"), COMMAND_MAX))
        sent_ns = time.time_ns() - 1000000
        try:
            self._write_command(cmd)
            deadline = time.time() + timeout
            while time.time() < deadline:
                res = read_json(self.result_path(cmd["id"]))
                if isinstance(res, dict) and res.get("id") == cmd["id"]:
                    return res
                zero = self.zero_since(sent_ns)
                if zero is not None:
                    raise DriverError("the game could not take command {} ({}): {}".format(
                        cmd["id"], cmd.get("op"), zero.get("error")))
                time.sleep(0.1)
            raise DriverError("no answer to command {} ({}) within {:.0f} s".format(cmd["id"], cmd.get("op"), timeout))
        finally:
            self._take_back(cmd["id"])

    def discard_pending(self):
        """Remove a cmd.json that is still there (a driver that was cut off before it took its command back): only
        while this driver holds the folder, so it can only be one of its own."""
        try:
            unhindered(lambda: os.remove(self.cmd_path))
            return True
        except OSError:
            return False

    def answer(self, cmd_id, timeout, patience=LONG_PATIENCE):
        """The final answer of a command whose first answer said `running`. The file is deleted once read."""
        deadline = time.time() + timeout
        silent = None
        while True:
            now = time.time()
            res = read_json(self.result_path(cmd_id))
            if isinstance(res, dict) and res.get("id") == cmd_id and res.get("state") != "running":
                self._forget(cmd_id)
                return res
            if self.status() is None:
                silent = silent or now
                if now - silent >= patience:
                    raise GameGone("the game stopped writing status.json while command {} ran".format(cmd_id))
            else:
                silent = None
            if now >= deadline:
                raise DriverError("command {} did not finish within {:.0f} s".format(cmd_id, timeout))
            time.sleep(0.2)

    def call(self, cmd, timeout=None, answer_timeout=ANSWER_TIMEOUT, patience=LONG_PATIENCE):
        """One command to its final answer. `timeout` bounds the wait for the final answer of a long action (by
        default final_wait(cmd) plus a margin: the game ends the action itself). What the first answer of a long
        action said and its final answer does not repeat (a teleport's `via`) stays in the answer returned; the
        final answer wins where both speak. A looping camera path has no final answer: its first one is returned."""
        first = self.send(cmd, timeout=answer_timeout)
        if first.get("state") != "running":
            self._forget(first["id"])
            return first
        if loops(cmd):
            return first
        if timeout is None:
            timeout = final_wait(cmd) + LONG_MARGIN
        merged = dict(first)
        merged.update(self.answer(first["id"], timeout=timeout, patience=patience))
        return merged

    def ops(self, refresh=False):
        """The ops the running build serves (`ping`)."""
        if self._ops is None or refresh:
            res = self.call({"op": "ping"})
            ops = res.get("ops")
            if not res.get("ok", False) or not isinstance(ops, list):
                raise DriverError("ping did not list the ops: {}".format(res.get("error") or res))
            self._ops = [str(o) for o in ops]
        return self._ops

    def require_ops(self, *names):
        missing = [n for n in names if n not in self.ops()]
        if missing:
            raise DriverError("this build of the DevTest feature does not serve {} (it serves {})".format(
                ", ".join(missing), ", ".join(self.ops())))

    def shot_files(self, answer):
        """The picture and the sidecar a shot answer names, as paths (either may be None)."""
        f = answer.get("file")
        if not isinstance(f, str) or not f:
            return None, None
        png = f if os.path.isabs(f) else os.path.join(self.folder, f)
        if not os.path.isfile(png) and not os.path.isabs(f):
            alt = os.path.join(self.folder, "shots", f)
            if os.path.isfile(alt):
                png = alt
        named = answer.get("sidecar")
        if isinstance(named, str) and named:
            side = named if os.path.isabs(named) else os.path.join(self.folder, named)
        else:
            side = os.path.splitext(png)[0] + ".json"
        return (png if os.path.isfile(png) else None), (side if os.path.isfile(side) else None)

    def collect_shot(self, answer, dest_dir, name=None):
        """Move the picture and its sidecar out of the channel folder -> (png, sidecar, sidecar object). Copied first,
        then removed from the game folder, where a picture of 5 to 20 MB would otherwise stay for good; one the
        system will not let go of stays there."""
        png, side = self.shot_files(answer)
        if png is None:
            return None, None, None
        os.makedirs(dest_dir, exist_ok=True)
        stem = name or os.path.splitext(os.path.basename(png))[0]
        out_png = os.path.join(dest_dir, stem + ".png")
        shutil.copy2(png, out_png)
        meta = out_side = None
        if side is not None:
            out_side = os.path.join(dest_dir, stem + ".json")
            shutil.copy2(side, out_side)
            meta = read_json(out_side)
        for src in (png, side):
            if src is not None:
                try:
                    unhindered(lambda: os.remove(src))
                except OSError:
                    pass
        return out_png, out_side, meta


# -- the stand-in for the game used by the self-tests ---------------------------------------------------------------

def tiny_png(width=4, height=2):
    """A valid PNG of a few pixels (8-bit RGB, filter 0 on every row), made with zlib alone."""
    rows = [b"\x00" + bytes(v for x in range(width) for v in (x * 60 % 256, y * 120 % 256, 200)) for y in range(height)]
    raw = b"".join(rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


class FakeMod(threading.Thread):
    """A stand-in for the DevTest feature in a folder: the channel's rules as they are stated (served once by id,
    the id recorded before the op runs, a cmd.json found when serving begins noted and never run, per-id answers,
    long actions answered `running` then `done`, results/0.json for what is not a command), answered by a thread.
    Only for the self-tests -- it is no model of the game."""

    OPS = ["ping", "status", "wait", "shot", "mark", "quit", "teleport", "camera", "entities", "avatar", "hud",
           "overlay", "feature", "ui.tree", "ui.click", "ui.text", "login", "enter"]

    def __init__(self, folder, period=0.05, in_game=False, dll_sha="ab" * 32, board=None):
        super().__init__(daemon=True)
        self.folder = folder
        self.period = period
        self.stop_event = threading.Event()
        self.stall = False
        # what the game says of its screen (status.screen; None: a build that does not say), and the size of the back
        # buffer a shot reads
        self.screen = {"fullScreen": False, "width": 4, "height": 2}
        self.shot_size = (4, 2)
        self.refuse_op = None       # an op the stand-in answers in results/0.json instead of serving it
        self.quit_at = None
        self.last_id = None
        self.noted = None
        self.served = []
        self.zero_stamps = set()
        self.jobs = {}
        self.in_game = in_game
        self.scene = 3 if in_game else 0
        self.pos = [100.0, 200.0, 300.0]
        self.uid = 10001 if in_game else 0
        self.focused = False
        self.hud = True
        self.quiet = False
        self.camera_held = False
        self.features = {}
        self.dll_sha = dll_sha
        self.board = board
        self.seq = 0
        self.counters = {"present": 0, "presentVt": 0, "handlerRun": 0, "faults": 0}
        self.log_lines = []

    def run(self):
        os.makedirs(os.path.join(self.folder, "results"), exist_ok=True)
        os.makedirs(os.path.join(self.folder, "shots"), exist_ok=True)
        stale = read_json(os.path.join(self.folder, "cmd.json"))
        if isinstance(stale, dict) and isinstance(stale.get("id"), int):
            self.last_id = self.noted = stale["id"]
        while not self.stop_event.is_set():
            if self.quit_at is not None and time.time() >= self.quit_at:
                break
            if not self.stall:
                self.seq += 1
                for k, d in (("present", 3), ("presentVt", 3), ("handlerRun", 70)):
                    self.counters[k] += d
                if self.board and self.seq % 10 == 0:
                    with open(self.board, "a", encoding="ascii", newline="") as f:
                        f.write("[{:4d}] present={} present1=0 presentVt={} handlerRun={} faults=0 | fault: none | fake\r\n".format(
                            self.seq // 10, self.counters["present"], self.counters["presentVt"], self.counters["handlerRun"]))
                self._status()
                self._serve()
                self._progress()
            time.sleep(self.period)

    def stop(self):
        self.stop_event.set()
        self.join(5)

    def _status(self):
        st = {"marker": MARKER, "gameVersion": 28, "dllSha256": self.dll_sha, "seq": self.seq,
              "wallMs": int(time.time() * 1000), "tick": int(time.monotonic() * 1000), "gameTicks": self.seq,
              "focused": self.focused, "runInBackground": True, "inGame": self.in_game, "scene": self.scene,
              "mapScene": self.scene, "pos": list(self.pos), "uid": self.uid, "avatarConfigId": 10000007 if self.in_game else 0,
              "camera": {"held": self.camera_held, "running": self.camera_held}, "hudHidden": not self.hud,
              "overlayQuiet": self.quiet, "menu": False, "busy": [{"id": i, "op": j["op"]} for i, j in self.jobs.items()],
              "lastCommandId": self.last_id, "counters": dict(self.counters)}
        if self.screen is not None:
            st["screen"] = dict(self.screen)
        try:
            write_json_atomic(os.path.join(self.folder, "status.json"), st)
        except OSError:
            pass    # the next one comes 50 ms later

    def _answer(self, res):
        try:
            write_json_atomic(os.path.join(self.folder, "results", "{}.json".format(res["id"])), res, indent=2)
        except OSError:
            pass

    def _zero(self, stamp, error):
        if stamp in self.zero_stamps:
            return
        self.zero_stamps.add(stamp)
        self._answer({"id": 0, "ok": False, "error": error})

    def _serve(self):
        path = os.path.join(self.folder, "cmd.json")
        try:
            st = os.stat(path)
            if st.st_size > COMMAND_MAX:
                self._zero(st.st_mtime_ns, "cmd.json is larger than a command can be")
                return
            with open(path, "r", encoding="utf-8") as f:
                text = f.read()
        except OSError:
            return
        stamp = st.st_mtime_ns
        try:
            cmd = json.loads(text)
        except ValueError:
            self._zero(stamp, "cmd.json does not parse")    # read again at the next poll; reported once per stamp
            return
        cid = cmd.get("id") if isinstance(cmd, dict) else None
        if not isinstance(cid, int) or isinstance(cid, bool) or cid == 0:
            self._zero(stamp, "a command is an object with an integer id other than 0 and an op")
            return
        if self.refuse_op is not None and cmd.get("op") == self.refuse_op:
            # refused by what it holds, under every time stamp it gets -- as the game refuses an oversized one
            self._zero(stamp, "the game takes no {} command (a test)".format(self.refuse_op))
            return
        if cid == self.last_id:
            return
        self.last_id = cid      # recorded before the op runs: a command that fails is never run again
        self.served.append(cmd)
        op = cmd.get("op")
        try:
            res = self._execute(cmd) if isinstance(op, str) else {"ok": False, "error": "op: a string", "state": "done"}
        except Exception as e:  # noqa: BLE001 -- a fault answers ok:false, as the feature does
            res = {"ok": False, "error": "the op faulted: {}".format(e), "state": "done"}
        res.setdefault("ok", True)
        res.update({"id": cid, "op": op if isinstance(op, str) else ""})
        self._answer(res)

    def _long(self, cmd, after, final, until=None, timeout=None):
        self.jobs[cmd["id"]] = {"op": cmd["op"], "due": time.time() + after, "final": final, "until": until,
                                "limit": time.time() + (timeout if timeout is not None else 60.0)}
        return {"state": "running"}

    def _progress(self):
        for cid in list(self.jobs):
            job = self.jobs[cid]
            now = time.time()
            final = None
            if job["until"] is not None:
                if now >= job["due"] and job["until"]():
                    final = dict(job["final"], outcome="met")
                elif now >= job["limit"]:
                    final = dict(job["final"], outcome="timeout")
            elif now >= job["due"]:
                final = job["final"]() if callable(job["final"]) else dict(job["final"])
            if final is not None:
                del self.jobs[cid]
                final.setdefault("ok", True)
                final.update({"id": cid, "op": job["op"], "state": "done"})
                self._answer(final)

    def _execute(self, cmd):
        op = cmd["op"]
        if op == "ping":
            return {"ops": list(self.OPS), "state": "done"}
        if op == "status":
            return {"state": "done", "inGame": self.in_game, "scene": self.scene, "uid": self.uid, "pos": list(self.pos),
                    "marker": MARKER, "gameVersion": 28, "dllSha256": self.dll_sha}
        if op == "mark":
            self.log_lines.append(str(cmd.get("text", "")))
            if self.board:   # the mod's own file log beside the board, as File Logging writes it
                logs = os.path.join(os.path.dirname(self.board), "logs")
                os.makedirs(logs, exist_ok=True)
                with open(os.path.join(logs, "log_fake.txt"), "a", encoding="utf-8", newline="") as f:
                    f.write("[devtest] mark {}: {}\r\n".format(cmd["id"], cmd.get("text", "")))
            return {"state": "done", "text": str(cmd.get("text", ""))}
        if op == "quit":
            self.quit_at = time.time() + 0.3
            return {"state": "done"}
        if op == "wait":
            until = cmd.get("until") or {}
            secs = float(until.get("seconds", 0) or 0)

            def met():
                if until.get("inGame") is True and not self.in_game:
                    return False
                if "scene" in until and self.scene != until["scene"]:
                    return False
                if "sceneNot" in until and self.scene == until["sceneNot"]:
                    return False
                if until.get("uidSet") and not self.uid:
                    return False
                return True
            return self._long(cmd, secs, {}, until=met, timeout=float(cmd.get("timeout", 60)))
        if op == "shot":
            stem = str(cmd.get("out") or "shot_{}".format(cmd["id"]))

            def done():
                shots = os.path.join(self.folder, "shots")
                data = tiny_png()
                width, height = self.shot_size
                with open(os.path.join(shots, stem + ".png"), "wb") as f:
                    f.write(data)
                side = {"id": cmd["id"], "width": width, "height": height, "format": 28, "formatName": "R8G8B8A8_UNORM",
                        "overlay": bool(cmd.get("overlay")), "scene": self.scene, "pos": list(self.pos), "copyUs": 900, "memcpyUs": 400}
                write_json_atomic(os.path.join(shots, stem + ".json"), side)
                return {"outcome": "saved", "file": "shots/{}.png".format(stem), "sidecar": "shots/{}.json".format(stem),
                        "width": width, "height": height, "format": 28, "samples": 1, "copyUs": 900, "mapWaitFrames": 1,
                        "memcpyUs": 400, "encodeMs": 3, "bytes": len(data), "overlay": bool(cmd.get("overlay"))}
            res = self._long(cmd, 0.2, done)
            res.update({"file": "shots/{}.png".format(stem), "overlay": bool(cmd.get("overlay"))})
            return res
        if op == "teleport":
            target = [float(v) for v in cmd.get("pos") or []]
            if len(target) != 3:
                return {"ok": False, "error": "teleport needs pos: [x, y, z]", "state": "done"}
            if not self.in_game:
                return {"ok": False, "error": "no avatar - is the scene loaded?", "state": "done"}
            dist = math.dist(self.pos, target)
            via = "map" if cmd.get("mode") == "map" or (cmd.get("mode") != "direct" and dist > 60) else "direct"

            def arrive():
                self.pos = target
                return {"via": via, "outcome": "arrived", "pos": list(target), "distance": 0.0, "reapplied": 0, "leftTicks": 0}
            res = self._long(cmd, 0.2, arrive)
            res.update({"via": via, "distance": dist})
            return res
        if op == "camera":
            if cmd.get("release"):
                released = self.camera_held
                self.camera_held = False
                for cid in [c for c, j in self.jobs.items() if j["op"] == "camera"]:
                    del self.jobs[cid]
                    self._answer({"id": cid, "op": "camera", "ok": False, "state": "done", "outcome": "released"})
                return {"state": "done", "held": False, "released": released}
            if "set" in cmd:
                self.camera_held = True
                res = self._long(cmd, 0.05, {"outcome": "set", "action": "set", "pose": cmd["set"]})
                res["action"] = "set"
                return res
            if "move" in cmd or "path" in cmd:
                self.camera_held = True
                kind = "move" if "move" in cmd else "path"
                if loops(cmd):      # runs until it is released
                    self.jobs[cmd["id"]] = {"op": "camera", "due": float("inf"), "final": {}, "until": None, "limit": float("inf")}
                    return {"state": "running", "action": kind}
                secs = float((cmd.get("move") or {}).get("seconds", 1.0)) * 0.05
                res = self._long(cmd, secs, {"outcome": "arrived" if kind == "move" else "finished", "action": kind})
                res["action"] = kind
                return res
            return {"ok": False, "error": "camera: set, move, path or release", "state": "done"}
        if op == "entities":
            ents = [{"runtimeId": 16777217 + i, "configId": 21010101 + i, "kind": "monster", "name": "fake",
                     "pos": [self.pos[0] + i, self.pos[1], self.pos[2]], "dist": float(i), "hp": 100.0, "maxHp": 100.0,
                     "level": 90} for i in range(3)]
            return {"state": "done", "entities": ents[:int(cmd.get("limit", 200))]}
        if op == "avatar":
            return {"state": "done", "configId": 10000007, "team": [10000007, 10000021]}
        if op == "hud":
            was = self.hud
            self.hud = bool(cmd.get("visible", True))
            return {"state": "done", "was": was, "now": self.hud}
        if op == "overlay":
            if "quiet" in cmd:
                self.quiet = bool(cmd["quiet"])
            return {"state": "done", "quiet": self.quiet, "menu": False}
        if op == "feature":
            if cmd.get("restore"):
                restored = len(self.features)
                self.features.clear()
                return {"state": "done", "restored": restored}
            key = "{}::{}".format(cmd.get("section"), cmd.get("name"))
            old = self.features.get(key, False)
            self.features[key] = cmd.get("value")
            return {"state": "done", "old": old, "new": cmd.get("value")}
        if op == "ui.tree":
            return {"state": "done", "nodes": [{"path": "/Canvas/Pages/LoginMainPage", "active": True, "button": False, "text": "Click to enter"},
                                               {"path": "/Canvas/Pages/LoginMainPage/bgBtn", "active": True, "button": True, "text": None}]}
        if op == "ui.click":
            return {"state": "done", "invoked": True, "path": cmd.get("path")}
        if op == "ui.text":
            return {"state": "done", "set": True, "path": cmd.get("path")}
        if op == "login":
            def logged():
                self.uid = 10001
                return {"state": "done", "uid": self.uid}
            return self._long(cmd, 0.2, logged)
        if op == "enter":
            def entered():
                self.in_game, self.scene = True, 3
                self.uid = self.uid or 10001
                return {"state": "done", "inGame": True}
            return self._long(cmd, 0.2, entered)
        return {"ok": False, "error": "unknown op", "ops": list(self.OPS), "state": "done"}


# -- the command line ------------------------------------------------------------------------------------------------

def print_json(obj):
    print(json.dumps(obj, indent=2, ensure_ascii=False))


def cmd_status(chan, args):
    st = chan.status(max_age=1e9)
    if st is None:
        raise DriverError("no status.json in {}".format(chan.folder))
    age = time.time() - chan.status_time
    print_json(st)
    print("written {:.1f} s ago{}{}".format(age, "" if age <= STATUS_FRESH else " -- the game is not writing it now",
                                         "" if st.get("marker") == MARKER else " -- it names no DevTest build"))
    if args.ask:
        if args.held_by_another:
            print("(another driver holds the folder: the status command is not sent)")
        else:
            print_json(chan.call({"op": "status"}))
    return 0


def _final(res):
    print_json(res)
    ok = res.get("ok", False)
    return 0 if ok and res.get("outcome") not in ("timeout", "sceneChanged", "noFrame", "failed", "replaced") else 1


def cmd_ping(chan, args):
    return _final(chan.call({"op": "ping"}))


def cmd_wait(chan, args):
    until = {}
    if args.in_game:
        until["inGame"] = True
    if args.scene is not None:
        until["scene"] = args.scene
    if args.scene_not is not None:
        until["sceneNot"] = args.scene_not
    if args.uid_set:
        until["uidSet"] = True
    if args.page:
        until["page"] = args.page
    if args.frames is not None:
        until["frames"] = args.frames
    if args.seconds is not None:
        until["seconds"] = args.seconds
    if args.moved is not None:
        until["moved"] = args.moved
    return _final(chan.call({"op": "wait", "until": until, "timeout": args.timeout}))


def cmd_shot(chan, args):
    cmd = {"op": "shot", "overlay": bool(args.overlay), "hud": args.hud, "afterFrames": args.after_frames, "timeout": args.timeout}
    if args.out:
        cmd["out"] = args.out
    res = chan.call(cmd)
    code = _final(res)
    if args.collect and res.get("ok"):
        png, side, _ = chan.collect_shot(res, args.collect)
        print("collected: {}{}".format(png, " + " + side if side else ""))
    return code


def cmd_mark(chan, args):
    return _final(chan.call({"op": "mark", "text": " ".join(args.text)}))


def cmd_teleport(chan, args):
    cmd = {"op": "teleport", "pos": [args.x, args.y, args.z], "mode": args.mode, "arrive": args.arrive, "timeout": args.timeout}
    if args.hold is not None:
        cmd["hold"] = args.hold
    return _final(chan.call(cmd))


def _pose(args):
    pose = {"pos": [args.x, args.y, args.z]}
    for k in ("pitch", "yaw", "roll", "fov"):
        v = getattr(args, k)
        if v is not None:
            pose[k] = v
    return pose


def cmd_camera(chan, args):
    cmd = {"op": "camera"}
    if args.action == "release":
        cmd["release"] = True
    elif args.action == "path":
        try:
            with open(args.x, "r", encoding="utf-8") as f:
                keys = json.load(f)
        except (OSError, ValueError) as e:
            raise DriverError("{}: {}".format(args.x, e))
        cmd["path"] = {"keys": keys.get("keys") if isinstance(keys, dict) else keys, "loop": bool(args.loop)}
    else:
        if args.y is None or args.z is None:
            raise DriverError("camera {} needs X Y Z".format(args.action))
        args.x, args.y, args.z = float(args.x), float(args.y), float(args.z)
        pose = _pose(args)
        if args.action == "set":
            cmd["set"] = pose
        else:
            cmd["move"] = {"to": pose, "seconds": args.seconds, "ease": args.ease}
    if args.drive is not None:
        cmd["drive"] = bool(args.drive)
    if args.carry is not None:
        cmd["carry"] = bool(args.carry)
    code = _final(chan.call(cmd))
    if loops(cmd) and code == 0:
        print("the path loops until it is released: devtest_driver.py camera release")
    return code


def cmd_entities(chan, args):
    return _final(chan.call({"op": "entities", "radius": args.radius, "kinds": [k for k in args.kinds.split(",") if k],
                             "limit": args.limit}))


def cmd_avatar(chan, args):
    return _final(chan.call({"op": "avatar"}))


def cmd_hud(chan, args):
    return _final(chan.call({"op": "hud", "visible": bool(args.visible)}))


def cmd_overlay(chan, args):
    cmd = {"op": "overlay"}
    if args.quiet is not None:
        cmd["quiet"] = bool(args.quiet)
    if args.menu:
        cmd["menu"] = args.menu
    return _final(chan.call(cmd))


def cmd_feature(chan, args):
    if args.restore:
        return _final(chan.call({"op": "feature", "restore": True}))
    if not args.section or not args.name or args.value is None:
        raise DriverError("feature needs --section, --name and --value (or --restore)")
    try:
        value = json.loads(args.value)
    except ValueError:
        raise DriverError("--value is JSON (true, 3, 1.5, \"text\")")
    return _final(chan.call({"op": "feature", "section": args.section, "name": args.name, "value": value}))


def cmd_ui_tree(chan, args):
    return _final(chan.call({"op": "ui.tree", "root": args.root, "depth": args.depth, "activeOnly": bool(args.active_only),
                             "texts": not args.no_texts, "limit": args.limit}))


def cmd_ui_click(chan, args):
    cmd = {"op": "ui.click", "path": args.path}
    if args.via:
        cmd["via"] = args.via
    return _final(chan.call(cmd))


def cmd_ui_text(chan, args):
    return _final(chan.call({"op": "ui.text", "path": args.path, "text": args.text}))


def cmd_login(chan, args):
    password = args.password
    if args.password_env:
        password = os.environ.get(args.password_env)
        if password is None:
            raise DriverError("${} is not set".format(args.password_env))
    cmd = {"op": "login", "account": args.account}
    if password is not None:
        cmd["password"] = password
    if args.timeout is not None:    # the game's own limit for the action; the wait for it follows
        cmd["timeout"] = args.timeout
    if args.if_shown:
        cmd["ifShown"] = True
    return _final(chan.call(cmd))


def cmd_enter(chan, args):
    cmd = {"op": "enter"}
    if args.button:
        cmd["button"] = args.button
    if args.timeout is not None:
        cmd["timeout"] = args.timeout
    return _final(chan.call(cmd))


def cmd_quit(chan, args):
    return _final(chan.call({"op": "quit"}))


def cmd_raw(chan, args):
    try:
        cmd = json.loads(args.json)
    except ValueError as e:
        raise DriverError("not JSON: {}".format(e))
    if not isinstance(cmd, dict) or not isinstance(cmd.get("op"), str):
        raise DriverError("a command is a JSON object with an op")
    cmd.pop("id", None)
    return _final(chan.call(cmd, timeout=args.timeout))


# -- the self-test ---------------------------------------------------------------------------------------------------

class Checks:
    def __init__(self):
        self.n = 0
        self.failed = 0

    def __call__(self, name, ok, detail=None):
        self.n += 1
        if not ok:
            self.failed += 1
        print("  [{}] {}{}".format("ok" if ok else "FAIL", name, "" if ok or detail is None else " -- {}".format(detail)))
        return ok

    def raises(self, name, exc, fn):
        try:
            fn()
        except exc:
            return self(name, True)
        except Exception as e:  # noqa: BLE001
            return self(name, False, "{}: {}".format(type(e).__name__, e))
        return self(name, False, "nothing raised")


def selftest():
    check = Checks()
    root = tempfile.mkdtemp(prefix="relic-devtest-driver-")
    print("devtest_driver selftest in {}".format(root))
    try:
        folder = os.path.join(root, "ee", CHANNEL)
        os.makedirs(folder)
        check("the marker is put together from two parts", MARKER.startswith("RELIC-") and len(MARKER) == 19)
        with open(os.path.abspath(__file__), "rb") as f:
            check("this script does not carry the marker whole", MARKER.encode("ascii") not in f.read())

        # A cmd.json the game finds when it starts serving is noted and never run.
        with open(os.path.join(folder, "cmd.json"), "w", encoding="utf-8") as f:
            json.dump({"id": 7, "op": "quit"}, f)
        fake = FakeMod(folder)
        fake.start()
        chan = Channel(folder)
        st = chan.require_alive(wait=5)
        check("a fresh status names the DevTest build", st.get("marker") == MARKER and st.get("gameVersion") == 28)
        check("the command found at the start is noted, not run", fake.noted == 7 and not fake.served and fake.quit_at is None)

        ops = chan.ops()
        check("ping lists the ops", "shot" in ops and "ui.tree" in ops, ops)
        check("the answer file of a finished command is deleted", not os.listdir(os.path.join(folder, "results")),
              os.listdir(os.path.join(folder, "results")))
        check("cmd.json is taken back once answered", not os.path.exists(chan.cmd_path))
        chan.require_ops("wait", "shot")
        check.raises("an op the build does not serve is named", DriverError, lambda: chan.require_ops("fly"))

        res = chan.call({"op": "wait", "until": {"seconds": 0.3}, "timeout": 5})
        check("a long action answers running, then done under its id", res.get("state") == "done" and res.get("outcome") == "met", res)
        res = chan.call({"op": "wait", "until": {"inGame": True}, "timeout": 0.5})
        check("a wait that is not met ends with outcome timeout", res.get("outcome") == "timeout", res)

        res = chan.call({"op": "shot", "out": "login", "overlay": False})
        out = os.path.join(root, "collected")
        png, side, meta = chan.collect_shot(res, out)
        with open(png, "rb") as f:
            head = f.read(8)
        check("a shot's picture and sidecar are collected", head == b"\x89PNG\r\n\x1a\n" and meta and meta.get("width") == 4, (png, side, meta))
        check("... and leave the game folder", not os.listdir(os.path.join(folder, "shots")), os.listdir(os.path.join(folder, "shots")))

        check("the wait for a long action is the limit the game gives it",
              final_wait({"op": "enter"}) == 180 and final_wait({"op": "login"}) == 60 and final_wait({"op": "login", "timeout": 90}) == 90
              and final_wait({"op": "shot"}) == 10 and final_wait({"op": "teleport", "timeout": 120}) == 120)
        check("a camera move or path is waited for as long as it takes",
              final_wait({"op": "camera", "move": {"to": {}, "seconds": 200}}) == CAMERA_START + 400
              and final_wait({"op": "camera", "path": {"keys": [{"seconds": 10, "hold": 5}, {}]}}) == CAMERA_START + 2 * (15 + CAMERA_KEY_SECONDS)
              and final_wait({"op": "camera", "set": {"pos": [0, 0, 0]}}) == CAMERA_START
              and final_wait({"op": "camera", "path": {"keys": [{}], "loop": True}}) is None)
        res = chan.call({"op": "camera", "path": {"keys": [{"pos": [0, 0, 0], "seconds": 1}], "loop": True}})
        check("a looping camera path answers once it runs", res.get("ok") is True and res.get("state") == "running", res)
        res = chan.call({"op": "camera", "release": True})
        check("... and camera release ends it", res.get("released") is True and res.get("state") == "done", res)

        class _Cut(Channel):
            def zero_since(self, since_ns):
                raise KeyboardInterrupt
        try:
            _Cut(folder).send({"op": "mark", "text": "cut short"}, timeout=5)
            check("a command whose wait is interrupted is taken back", False, "it was answered")
        except KeyboardInterrupt:
            check("a command whose wait is interrupted is taken back", not os.path.exists(chan.cmd_path))
        write_json_atomic(chan.cmd_path, {"id": fake.last_id, "op": "login", "password": "x"})   # served already: not run again
        check("a command left behind is discarded", chan.discard_pending() and not os.path.exists(chan.cmd_path))

        res = chan.call({"op": "fly"})
        check("an unknown op is answered ok:false with the op list", res.get("ok") is False and "ping" in (res.get("ops") or []), res)

        n = len(fake.served)
        last = fake.last_id
        write_json_atomic(chan.cmd_path, {"id": last, "op": "mark", "text": "again"})
        time.sleep(0.3)
        check("a command is served once by id", len(fake.served) == n and "again" not in fake.log_lines)
        chan._take_back(last)

        since = time.time_ns() - 1000000
        with open(chan.cmd_path, "w", encoding="utf-8") as f:
            f.write('{"op": "status"')
        time.sleep(0.4)
        zero = chan.zero_since(since)
        check("a cmd.json that is no command is answered in results/0.json", zero is not None and zero.get("ok") is False, zero)
        os.remove(chan.cmd_path)
        fake.refuse_op = "refused"
        began = time.time()
        try:
            chan.send({"op": "refused"}, timeout=10)
            check("a command the game refuses fails at once, naming why", False, "it was answered")
        except DriverError as e:
            check("a command the game refuses fails at once, naming why", "a test" in str(e) and time.time() - began < 5, e)
        check("... and it is taken back", not os.path.exists(chan.cmd_path))
        res = chan.call({"op": 5})
        check("an op that is no string is served and refused", res.get("ok") is False, res)
        check.raises("a command larger than the game takes is not written", DriverError,
                     lambda: chan.send({"op": "mark", "text": "x" * (COMMAND_MAX + 1)}))

        hold = hold_folder(folder)
        second = hold_folder(folder)
        check("one driver per folder", hold is not None and second is None)
        release_folder(folder, hold)
        again = hold_folder(folder)
        check("the folder is free once the driver lets go", again is not None)
        release_folder(folder, again)

        res = chan.call({"op": "teleport", "pos": [110.0, 200.0, 300.0], "mode": "direct"})
        check("a teleport is refused before the world is there", res.get("ok") is False, res)
        chan.call({"op": "enter"})
        st = chan.wait_status(lambda s: s.get("inGame") is True, timeout=5)
        check("the status follows an action's effect", st.get("scene") == 3 and st.get("uid") == 10001, st)
        res = chan.call({"op": "teleport", "pos": [110.0, 200.0, 300.0], "mode": "direct"})
        st = chan.wait_status(lambda s: s.get("lastCommandId") == res["id"], timeout=5)
        check("a teleport ends where it was sent", res.get("via") == "direct" and horizontal(st["pos"], [110, 0, 300]) < 0.01, res)

        a = Channel(folder)
        time.sleep(0.01)
        b = Channel(folder)
        check("the ids of a later channel come after an earlier one's", b._next_id > a._next_id)

        # A game thread that stops writing its status: a wait for a long action calls the game gone after its
        # patience, and so does a wait for a status.
        quick = Channel(folder)
        quick.fresh = 0.3
        first = quick.send({"op": "wait", "until": {"seconds": 30}, "timeout": 60})
        fake.stall = True
        check.raises("a game that stalls during a long action is called gone", GameGone,
                     lambda: quick.answer(first["id"], timeout=20, patience=0.6))
        check.raises("a game that stalls is called gone by a status wait", GameGone,
                     lambda: quick.wait_status(lambda s: False, timeout=20, patience=0.6))
        fake.stall = False
        chan.require_alive(wait=5)
        fake.quit_at = time.time()
        time.sleep(0.3)
        gone = Channel(folder)
        gone.fresh = 0.3
        check.raises("a game that quit is gone", GameGone, lambda: gone.require_alive(wait=1.0))
        fake.stop()

        state = os.path.join(root, "state.json")
        with open(state, "w", encoding="utf-8-sig") as f:
            json.dump({"Installed": [{"Id": "2.8", "GameDir": r"X:\games\2.8"}]}, f)
        check("a version's devtest folder is found in Relic's state file",
              channel_dir("2.8", state) == os.path.join(r"X:\games\2.8", "ayy", "anime", "ee", CHANNEL))
        check.raises("a version that is not installed is named", DriverError, lambda: game_dir("9.9", state))
    finally:
        shutil.rmtree(root, ignore_errors=True)
    print("{}/{} checks passed".format(check.n - check.failed, check.n))
    return 0 if check.failed == 0 else 1


def main(argv=None):
    ap = argparse.ArgumentParser(description="Drive the DevTest feature of the enhancements DLL (dev builds only).")
    ap.add_argument("--version", default="2.8", help="the installed version whose game folder holds the channel (default 2.8)")
    ap.add_argument("--dir", help="the devtest folder itself (instead of --version)")
    ap.add_argument("--state", help="Relic's state file (default %%LOCALAPPDATA%%\\Relic\\state.json)")
    ap.add_argument("--selftest", action="store_true", help="the channel against a stand-in for the game (as the selftest command)")
    sub = ap.add_subparsers(dest="cmd", metavar="command")

    p = sub.add_parser("status", help="status.json as the game last wrote it")
    p.add_argument("--ask", action="store_true", help="send the status command too")
    p.set_defaults(fn=cmd_status)
    p = sub.add_parser("ping", help="the ops this build serves")
    p.set_defaults(fn=cmd_ping)

    p = sub.add_parser("wait", help="wait in the game for a condition")
    p.add_argument("--in-game", action="store_true")
    p.add_argument("--scene", type=int)
    p.add_argument("--scene-not", type=int)
    p.add_argument("--uid-set", action="store_true")
    p.add_argument("--page")
    p.add_argument("--frames", type=int)
    p.add_argument("--seconds", type=float)
    p.add_argument("--moved", type=float)
    p.add_argument("--timeout", type=float, default=60.0)
    p.set_defaults(fn=cmd_wait)

    p = sub.add_parser("shot", help="a picture of the game's back buffer")
    p.add_argument("--overlay", action="store_true", help="with the mod's overlay (default: without)")
    p.add_argument("--hud", choices=["keep", "hide"], default="keep")
    p.add_argument("--after-frames", type=int, default=2)
    p.add_argument("--out", help="file stem under shots/")
    p.add_argument("--timeout", type=float, default=10.0)
    p.add_argument("--collect", metavar="FOLDER", help="copy the picture and its sidecar there")
    p.set_defaults(fn=cmd_shot)

    p = sub.add_parser("mark", help="a line in devtest.log and the diag board")
    p.add_argument("text", nargs="+")
    p.set_defaults(fn=cmd_mark)

    p = sub.add_parser("teleport", help="move the character")
    p.add_argument("x", type=float)
    p.add_argument("y", type=float)
    p.add_argument("z", type=float)
    p.add_argument("--mode", choices=["auto", "direct", "map"], default="auto")
    p.add_argument("--arrive", type=float, default=12.0)
    p.add_argument("--timeout", type=float, default=60.0)
    p.add_argument("--hold", type=float)
    p.set_defaults(fn=cmd_teleport)

    p = sub.add_parser("camera", help="the free camera: set, move, path or release")
    p.add_argument("action", choices=["set", "move", "path", "release"])
    p.add_argument("x", nargs="?", help="X (set/move) or the keys file (path)")
    p.add_argument("y", nargs="?", type=float)
    p.add_argument("z", nargs="?", type=float)
    p.add_argument("--pitch", type=float)
    p.add_argument("--yaw", type=float)
    p.add_argument("--roll", type=float)
    p.add_argument("--fov", type=float)
    p.add_argument("--seconds", type=float, default=3.0)
    p.add_argument("--ease", choices=["linear", "in", "out", "inOut"], default="inOut")
    p.add_argument("--loop", action="store_true")
    p.add_argument("--drive", type=int, choices=[0, 1])
    p.add_argument("--carry", type=int, choices=[0, 1])
    p.set_defaults(fn=cmd_camera)

    p = sub.add_parser("entities", help="the entities around the character")
    p.add_argument("--radius", type=float, default=100.0)
    p.add_argument("--kinds", default="monster,gadget,avatar,npc")
    p.add_argument("--limit", type=int, default=200)
    p.set_defaults(fn=cmd_entities)
    p = sub.add_parser("avatar", help="the current character and the team")
    p.set_defaults(fn=cmd_avatar)
    p = sub.add_parser("hud", help="the game's own UI on or off")
    p.add_argument("--visible", type=int, choices=[0, 1], required=True)
    p.set_defaults(fn=cmd_hud)
    p = sub.add_parser("overlay", help="the mod's overlay")
    p.add_argument("--quiet", type=int, choices=[0, 1])
    p.add_argument("--menu", choices=["hide", "show"])
    p.set_defaults(fn=cmd_overlay)
    p = sub.add_parser("feature", help="switch a feature of the mod for this session (never saved)")
    p.add_argument("--section")
    p.add_argument("--name")
    p.add_argument("--value", help="JSON")
    p.add_argument("--restore", action="store_true", help="put back every switch this session made")
    p.set_defaults(fn=cmd_feature)
    p = sub.add_parser("ui-tree", help="the game's UI objects under a path")
    p.add_argument("--root", default="/Canvas/Pages")
    p.add_argument("--depth", type=int, default=6)
    p.add_argument("--active-only", action="store_true")
    p.add_argument("--no-texts", action="store_true", help="names only, no texts")
    p.add_argument("--limit", type=int, default=2000)
    p.set_defaults(fn=cmd_ui_tree)
    p = sub.add_parser("ui-click", help="invoke a button of the game's UI")
    p.add_argument("path")
    p.add_argument("--via", choices=["onClick", "submit"], help="the button's click event (default) or Button.OnSubmit")
    p.set_defaults(fn=cmd_ui_click)
    p = sub.add_parser("ui-text", help="set the text of an InputField of the game's UI")
    p.add_argument("path")
    p.add_argument("text")
    p.set_defaults(fn=cmd_ui_text)
    p = sub.add_parser("login", help="the SDK's login form (where the build can reach it)")
    p.add_argument("--account", required=True)
    p.add_argument("--password")
    p.add_argument("--password-env", metavar="VAR", help="read the password from this environment variable")
    p.add_argument("--timeout", type=float, help="seconds the game gives the form to close (default {:.0f})".format(DEFAULT_LONG["login"]))
    p.add_argument("--if-shown", action="store_true", help="no form shown (the SDK logged in by itself with the account it remembers) = ok, outcome notShown")
    p.set_defaults(fn=cmd_login)
    p = sub.add_parser("enter", help="the door page's 'click to enter'")
    p.add_argument("--button", choices=["bg", "start"], help="start (pressStartBtn: 'click to begin') or bg (bgBtn); default: the first that can be pressed, start first")
    p.add_argument("--timeout", type=float, help="seconds the game gives the world to load (default {:.0f})".format(DEFAULT_LONG["enter"]))
    p.set_defaults(fn=cmd_enter)
    p = sub.add_parser("quit", help="the game quits as its own menu does")
    p.set_defaults(fn=cmd_quit)
    p = sub.add_parser("raw", help="any command as JSON")
    p.add_argument("json")
    p.add_argument("--timeout", type=float)
    p.set_defaults(fn=cmd_raw)
    p = sub.add_parser("selftest", help="the channel against a stand-in for the game; no game needed")
    p.set_defaults(fn=None)

    args = ap.parse_args(argv)
    if args.selftest or args.cmd == "selftest":
        return selftest()
    if args.cmd is None:
        ap.error("a command is needed (or --selftest)")
    hold = None
    folder = None
    try:
        folder = os.path.abspath(args.dir) if args.dir else channel_dir(args.version, args.state)
        if not os.path.isdir(folder):
            raise DriverError("no devtest folder at {} -- is the game running with the DevTest build?".format(folder))
        chan = Channel(folder)
        args.held_by_another = False
        hold = hold_folder(folder)
        if hold is None:
            if args.fn is not cmd_status:
                raise DriverError("another driver is at work on {} -- one at a time".format(folder))
            args.held_by_another = True
        return args.fn(chan, args)
    except DriverError as e:
        print("error: {}".format(e), file=sys.stderr)
        return 1
    finally:
        if folder is not None:
            release_folder(folder, hold)


if __name__ == "__main__":
    sys.exit(main())
