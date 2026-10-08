#!/usr/bin/env python3
"""Drive the in-game "Navmesh Bake" feature of the enhancements DLL from outside the game.

The feature (entertainment_experience/mod/cheat-library/src/user/cheat/world/NavBake.cpp) serves one command at a
time through files next to the mod: cmd.json (written here), result.json (its answer, same id), status.json (the
player's position and scene, twice a second). This script writes the commands, waits for the answers and collects
the bake outputs (<stem>.tiles + <stem>.json) into a folder that build/navmesh_tool.py fromunity turns into the
server's files.

  python build/navbake_driver.py status                              # where the player is
  python build/navbake_driver.py layers                              # the game's layer names (for --layers)
  python build/navbake_driver.py navkey                              # the (polygon, tag hash) pair the client last sent
  python build/navbake_driver.py polygons [--collect <folder>]       # the client's polygon table (ids, outlines, tags, valid hashes)
  python build/navbake_driver.py teleport X Y Z [--mode auto|direct|map]
  python build/navbake_driver.py bake [--center X Y Z] [--size 256] [--height 600] [--out stem] [--layers MASK] [--cull 0] [--sync]
  python build/navbake_driver.py sweep <stations.json> --out <folder> [--size 320] [--height 600] [--settle 5] [--nudge 20] [--hold 0]
      stations.json = [[x, y, z], ...] or {"stations": [...], "size": .., "height": ..}: teleport to each, wait
      for the player to be there, step aside and back (the server then shows what it spawns there), bake the box
      around the STATION again and again until two bakes in a row are the same (the streaming has settled:
      nothing more is being loaded), copy the unit files to --out. The folder also gets <prefix>sweep.json, the
      list of stations `fromunity` checks the units against.
  python build/navbake_driver.py follow --out <folder> [--scene N] [--spacing 48]
      someone plays, the script bakes: wherever the player comes to, the nearest point of a 48 m lattice gets one
      unit (until two bakes in a row are the same). For what a sweep cannot reach -- a domain whose rooms load
      only as its story is played through. Ctrl+C ends it; run again, it goes on where it stopped.
  python build/navbake_driver.py collect --out <folder>              # copy every unit (.tiles + .json) out of navbake/

Standard library only; the game must run with the mod loaded and "Serve commands" ticked (World > Navmesh Bake).
One driver works on a navbake folder at a time (driver.lock there); `status` beside a running one only reads.
"""
import argparse
import hashlib
import json
import math
import os
import shutil
import sys
import time

DEFAULT_DIR = r"D:\relic\Genshin 2.8\ayy\anime\ee\navbake"
# Terrain (8), SceneProp (11), ScenePropIgnoreCamera (21): the layers the game's own bake collects. Every layer
# (-1) also bakes the capsules of characters and monsters into the mesh.
GAME_LAYERS = (1 << 8) | (1 << 11) | (1 << 21)
# What the builder collects in the box: the physics colliders (what the game itself walks on), or the render meshes
# -- only the readable ones reach the bake.
GEOMETRY_COLLIDERS = 1
GEOMETRY_RENDER_MESHES = 0
HOLD_DROP = 3.0     # metres below its station at which a held player is put back on it
CHANNEL_STEMS = ("cmd", "result", "status")     # the channel's own files (and polygons_<scene>): never a unit's name
STATUS_PATIENCE = 10.0  # seconds without a readable, fresh status before a bake's wait calls the game gone
LOCK_NAME = "driver.lock"


class DriverError(Exception):
    pass


class GameGone(DriverError):
    """The mod stopped writing its status, or the player is no longer in the scene being baked: nothing more of
    this sweep can be done."""


class Mod:
    """The command channel: one id per command, answers matched by id."""

    def __init__(self, folder):
        self.folder = folder
        self.cmd_path = os.path.join(folder, "cmd.json")
        self.result_path = os.path.join(folder, "result.json")
        self.status_path = os.path.join(folder, "status.json")
        self._next_id = int(time.time() * 1000) % 1000000000 * 10
        self._stamp = 0
        self.status_time = 0.0      # when the status last returned was written

    def _read_json(self, path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                text = f.read()
        except OSError:
            return None
        if not text.strip():
            return None
        try:
            return json.loads(text)
        except ValueError:
            return None

    def status(self, max_age=3.0):
        """status.json, if the mod wrote it recently (the game is up and the feature is on). The file's time is
        asked of the handle that read it: the mod puts a new file in its place twice a second, and a second look by
        name can fall between two of them."""
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
        return st

    def require_alive(self, wait=20.0):
        """A fresh status.json; the game thread may stall for seconds (a loading screen, a big bake), so it is
        waited for before the game is declared gone."""
        deadline = time.time() + wait
        while True:
            st = self.status(max_age=3.0)
            if st is not None:
                return st
            if time.time() >= deadline:
                raise GameGone("no fresh status.json in {} -- is the game running with the mod loaded and "
                               "'Serve commands' ticked (World > Navmesh Bake)?".format(self.folder))
            time.sleep(0.5)

    def send(self, cmd, timeout=15.0):
        """Write one command, wait for result.json carrying its id. Answered or given up on, the command is taken
        back: a file found by a game that starts later is not a command, and what was reported as failed must not
        be carried out afterwards."""
        self.require_alive()
        self._next_id += 1
        cmd = dict(cmd)
        cmd["id"] = self._next_id
        tmp = "{}.{}.tmp".format(self.cmd_path, os.getpid())
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(cmd, f)
            unhindered(lambda: os.replace(tmp, self.cmd_path))
        except OSError as e:
            raise DriverError("cmd.json cannot be written: {}".format(e))
        # A time stamp of its own for every command: a build of the mod that reads cmd.json only when its stamp has
        # moved must never find two commands under one, and the clock that stamps files moves in steps.
        self._stamp = max(time.time_ns(), self._stamp + 1000000)
        os.utime(self.cmd_path, ns=(self._stamp, self._stamp))
        deadline = time.time() + timeout
        while time.time() < deadline:
            res = self._read_json(self.result_path)
            if isinstance(res, dict) and res.get("id") == cmd["id"]:
                self._take_back(cmd["id"])
                return res
            time.sleep(0.2)
        self._take_back(cmd["id"])
        raise DriverError("no answer to command {} ({}) within {:.0f} s".format(cmd["id"], cmd.get("op"), timeout))

    def _take_back(self, cmd_id):
        """Remove cmd.json while it still holds this command -- another driver's is not this one's to remove."""
        own = self._read_json(self.cmd_path)
        if isinstance(own, dict) and own.get("id") == cmd_id:
            try:
                unhindered(lambda: os.remove(self.cmd_path))
            except OSError:
                pass

    def wait_bake(self, bake_id, timeout=900.0, stem=None):
        """The final answer of a bake that is running. Where result.json no longer holds it -- another command's
        answer took its place -- the unit's own sidecar tells how the bake ended; a mod that is idle again without
        either has ended the bake without a unit."""
        began = time.time()
        deadline = began + timeout
        silent = idle = None
        while time.time() < deadline:
            res = self._read_json(self.result_path)
            if isinstance(res, dict) and res.get("id") == bake_id and res.get("state") == "done":
                return res
            st = self.status(max_age=30.0)
            now = time.time()
            if st is None:
                # one look that fails is a file caught between two writes; the game is gone when they all fail
                silent = silent or now
                if now - silent >= STATUS_PATIENCE:
                    raise GameGone("the mod stopped answering while bake {} ran".format(bake_id))
            else:
                silent = None
                # a status written after the wait began that says "not busy": the bake has ended. Its answer is
                # given two more seconds to appear (the mod writes it again when the file was held by a reader).
                if st.get("busy") is False and self.status_time > began:
                    idle = idle or now
                    if now - idle >= 2.0:
                        side = self._read_json(os.path.join(self.folder, stem + ".json")) if stem else None
                        if isinstance(side, dict) and side.get("id") == bake_id and os.path.isfile(os.path.join(self.folder, stem + ".tiles")):
                            return dict(side, ok=True, op="bake", state="done", file=stem + ".tiles")
                        raise DriverError("bake {} has ended, but its answer is not in result.json any more and it left "
                                          "no unit (another command's answer took its place?)".format(bake_id))
                else:
                    idle = None
            time.sleep(0.5)
        raise DriverError("bake {} did not finish within {:.0f} s".format(bake_id, timeout))

    def bake(self, center=None, extents=None, out=None, layers=GAME_LAYERS, cull=0, sync=False, timeout=900.0, quiet=False,
             geometry=GEOMETRY_COLLIDERS):
        cmd = {"op": "bake", "layerMask": layers, "cull": cull, "sync": bool(sync), "geometry": int(geometry)}
        if center is not None:
            cmd["center"] = [float(v) for v in center]
        if extents is not None:
            cmd["extents"] = [float(v) for v in extents]
        if out:
            if out.lower() in CHANNEL_STEMS or out.lower().startswith("polygons_"):
                raise DriverError("a unit cannot be named {}: cmd, result, status and polygons_<scene> are the channel's own files".format(out))
            cmd["out"] = out
        # the synchronous builder answers only when it is through: its first answer is the bake's whole time
        first = self.send(cmd, timeout=max(60.0, timeout) if sync else 60.0)
        if not first.get("ok", False):
            raise DriverError("bake refused: {}".format(first.get("error")))
        res = first
        if first.get("state") != "done":
            if not quiet:
                print("  bake {} running: {} sources".format(first["id"], first.get("sources")))
            res = self.wait_bake(first["id"], timeout=timeout, stem=out or "bake_{}".format(first["id"]))
        if res.get("ok", False) and res.get("file"):
            # what the tiles are, for telling two bakes of one box apart (the mod writes this stem again only at
            # this driver's next bake of it)
            res["digest"] = unit_digest(self.folder, os.path.splitext(str(res["file"]))[0])
        return res

    def teleport(self, pos, mode="auto", arrive=12.0, timeout=60.0, avatar_wait=0.0):
        """Teleport and wait until the player stands within `arrive` metres (x/z) of the target. A character
        that is not there at the moment -- brought back after a fall, hidden by a cutscene -- is waited for
        `avatar_wait` seconds."""
        until = time.time() + avatar_wait
        while True:
            res = self.send({"op": "teleport", "pos": [float(v) for v in pos], "mode": mode}, timeout=20.0)
            if res.get("ok", False):
                break
            if "no avatar" not in str(res.get("error")) or time.time() >= until:
                raise DriverError("teleport refused: {}".format(res.get("error")))
            time.sleep(2.0)
        answered = time.time()
        deadline = answered + timeout
        last = None
        while time.time() < deadline:
            st = self.status(max_age=5.0)
            # Only a status written after the teleport was served tells where the player is now: the mod writes
            # its status first and serves the command in the same turn, so the one beside the answer still shows
            # the place left. The status names the command last served; one that names none is judged by its time.
            if st is not None and isinstance(st.get("pos"), list) and (
                    st.get("lastCommandId") == res.get("id") if "lastCommandId" in st else self.status_time > answered):
                last = st["pos"]
                if horizontal(last, pos) <= arrive:
                    return res, last
            time.sleep(0.5)
        raise DriverError("the player did not arrive at {} (last seen {})".format(fmt3(pos), fmt3(last) if last else "nowhere"))


def unhindered(fn, tries=40, pause=0.025):
    """Replace or remove a file the mod may have open for a moment (Windows refuses meanwhile): try again shortly."""
    for i in range(tries):
        try:
            return fn()
        except PermissionError:
            if i == tries - 1:
                raise
            time.sleep(pause)


def hold_folder(folder):
    """One driver at a time on a folder: two would replace each other's command and take the other's answer for a
    missing one. The hold is a locked file, which the system lets go of when this process ends, however it ends.
    -> the open file (keep it), or None when another driver holds the folder."""
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


def horizontal(a, b):
    return math.hypot(a[0] - b[0], a[2] - b[2])


def fmt3(v):
    return "({:.1f}, {:.1f}, {:.1f})".format(*[float(x) for x in v])


def copy_unit(folder, stem, out_dir, extra=None):
    """Copy one unit out of the mod's folder: the tiles first, the sidecar last and in one step -- a unit counts
    as collected only once its sidecar is there. `extra` is merged into the sidecar."""
    os.makedirs(out_dir, exist_ok=True)
    tiles = os.path.join(folder, stem + ".tiles")
    side = os.path.join(folder, stem + ".json")
    if not os.path.isfile(tiles) or not os.path.isfile(side):
        raise DriverError("the mod wrote no {}.tiles / .json".format(stem))
    try:
        with open(side, encoding="utf-8") as f:
            meta = json.load(f)
    except (OSError, ValueError) as e:
        raise DriverError("{}.json is not readable: {}".format(stem, e))
    if extra:
        meta.update(extra)
    shutil.copy2(tiles, os.path.join(out_dir, stem + ".tiles"))
    write_json(os.path.join(out_dir, stem + ".json"), meta)
    return [stem + ".tiles", stem + ".json"]


def write_json(path, obj):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(obj, f, indent=2)
    os.replace(tmp, path)


def same_station(a, b):
    return isinstance(a, list) and len(a) >= 3 and all(abs(float(a[i]) - float(b[i])) < 0.01 for i in range(3))


def unit_done(out_dir, stem, station):
    """A station is baked when its tiles are there and its sidecar names this very station."""
    if not os.path.isfile(os.path.join(out_dir, stem + ".tiles")):
        return False
    try:
        with open(os.path.join(out_dir, stem + ".json"), encoding="utf-8") as f:
            meta = json.load(f)
    except (OSError, ValueError):
        return False
    return isinstance(meta, dict) and same_station(meta.get("station"), station)


def unit_digest(folder, stem):
    """md5 of the tiles the bake of a moment ago wrote into the mod's folder (None when they cannot be read)."""
    try:
        with open(os.path.join(folder, stem + ".tiles"), "rb") as f:
            return hashlib.md5(f.read()).hexdigest()
    except OSError:
        return None


def same_bake(a, b):
    """Two bakes of one box gave the same tiles: the bytes decide -- an object that moved leaves every count as it
    was --, the counts alone where a file could not be read."""
    if a is None or any(a.get(k) != b.get(k) for k in ("tiles", "bytes", "sources")):
        return False
    return a.get("digest") is None or b.get("digest") is None or a["digest"] == b["digest"]


def cmd_status(mod, args):
    st = mod.require_alive()
    print(json.dumps(st, indent=2))
    if args.held_by_another:
        print("another navbake_driver is at work on this folder: no command was sent")
        return 0
    res = mod.send({"op": "status"})
    print(json.dumps(res, indent=2))
    return 0


def cmd_navkey(mod, args):
    res = mod.send({"op": "navkey"})
    if not res.get("ok", False):
        raise DriverError(res.get("error", "navkey failed"))
    print(json.dumps(res.get("navkey"), indent=2))
    return 0


def cmd_polygons(mod, args):
    res = mod.send({"op": "polygons"}, timeout=30.0)
    if not res.get("ok", False):
        raise DriverError(res.get("error", "polygons failed"))
    print("scene {}: {} polygons -> navbake/{}".format(res.get("scene"), res.get("count"), res.get("file")))
    for p in res.get("polygons", []):
        print("  polygon {}: {} points, tags {}, {} valid hashes".format(p.get("id"), p.get("points"), p.get("tags"), p.get("hashes")))
    if args.collect:
        os.makedirs(args.collect, exist_ok=True)
        shutil.copy2(os.path.join(mod.folder, res["file"]), os.path.join(args.collect, res["file"]))
        print("copied to", os.path.join(args.collect, res["file"]))
    return 0


def cmd_layers(mod, args):
    res = mod.send({"op": "layers"})
    if not res.get("ok", False):
        raise DriverError(res.get("error", "layers failed"))
    for i, name in enumerate(res.get("layers", [])):
        if name:
            print("{:>2}  {}".format(i, name))
    return 0


def cmd_teleport(mod, args):
    res, pos = mod.teleport([args.x, args.y, args.z], mode=args.mode, arrive=args.arrive, timeout=args.timeout)
    print("teleported via {} -> player at {}".format(res.get("via"), fmt3(pos)))
    return 0


def cmd_bake(mod, args):
    center = args.center
    extents = [args.size / 2.0, args.height / 2.0, args.size / 2.0]
    t0 = time.time()
    res = mod.bake(center=center, extents=extents, out=args.out, layers=args.layers, cull=args.cull, sync=args.sync, timeout=args.timeout,
                   geometry=args.geometry)
    print(json.dumps(res, indent=2))
    if not res.get("ok", False):
        return 1
    print("{} tiles, {} KB, {} sources, {:.1f} s".format(res.get("tiles"), int(res.get("bytes", 0)) // 1024, res.get("sources"), time.time() - t0))
    if args.collect:
        print("copied:", copy_unit(mod.folder, os.path.splitext(res["file"])[0], args.collect))
    return 0


def in_scene(status, scene, known=False):
    """The status, once it is known to be of the scene the sweep is for: a player thrown out of a domain, or
    back in the world after a failed entry, would otherwise have another scene's geometry baked under this
    one's stems. A status the mod wrote while it could not look the player up names no scene: that is not another
    scene -- and, where the scene has to be `known`, not this one either."""
    if scene is None:
        return status
    if status.get("scene") is None:
        if known:
            raise DriverError("the scene is not known at the moment (the answer names none)")
        return status
    if status.get("scene") != scene:
        raise GameGone("the player is in scene {}, the sweep is for scene {}".format(status.get("scene"), scene))
    return status


def placed(mod, wait=10.0):
    """A fresh status that carries the player's place. The mod writes one without it while its look-up of the
    player fails (a loading screen, a cutscene); that is waited out for a while before it counts as a failure."""
    until = time.time() + wait
    while True:
        st = mod.require_alive()
        if isinstance(st.get("pos"), list):
            return st
        if time.time() >= until:
            raise DriverError("no position in the status for {:.0f} s (a loading screen?)".format(wait))
        time.sleep(0.5)


def rest(mod, args, st, seconds):
    """Wait. With --hold the player is kept on the station meanwhile: put back whenever they have dropped under
    it -- nothing to stand on there (a place only a flying monster keeps, a room that has not loaded), and a long
    fall ends the visit to a domain."""
    if not (args.hold > 0 and args.mode == "direct"):
        time.sleep(seconds)
        return
    until = time.time() + seconds
    while time.time() < until:
        time.sleep(0.4)
        now = mod.status(max_age=5.0)
        if now is not None and isinstance(now.get("pos"), list) and now["pos"][1] < st[1] - HOLD_DROP:
            mod.teleport(st[:3], mode="direct", arrive=args.arrive, timeout=args.teleport_timeout)


def bake_station(mod, args, st, stem, size, height):
    """Teleport to one station and bake its box until the result stops changing.
    -> (the last bake's answer, the navkey the client reported, bakes run, settled)"""
    in_scene(mod.require_alive(), args.scene)
    if args.walk > 0 and args.mode == "direct":
        # one position set per step along the straight line, so that every move the server hears of is a short one
        here = (mod.require_alive().get("pos") or st)[:3]
        steps = int(math.dist(here, st[:3]) // args.walk)
        for i in range(1, steps + 1):
            mod.teleport([here[k] + (st[k] - here[k]) * i / (steps + 1) for k in range(3)], mode="direct",
                         arrive=args.arrive + args.walk, timeout=args.teleport_timeout, avatar_wait=args.avatar_wait)
    res, pos = mod.teleport(st[:3], mode=args.mode, arrive=args.arrive, timeout=args.teleport_timeout,
                            avatar_wait=args.avatar_wait)
    print("  at {} via {}".format(fmt3(pos), res.get("via")))
    if args.mode == "direct":
        if args.nudge > 0:
            # The server does not take a position set far from the last one for the player's place: what it spawns
            # around a station (camp platforms, lookouts -- colliders the mesh must hold) appears only once a
            # short move from there has reached it. Two steps aside and back are such moves.
            for dx, dz in ((args.nudge, 0.0), (0.0, args.nudge), (0.0, 0.0)):
                time.sleep(1.5)
                mod.teleport([st[0] + dx, st[1], st[2] + dz], mode="direct", arrive=args.arrive + args.nudge,
                             timeout=args.teleport_timeout)
        # where the level around the station is not loaded yet the player falls through it: put back until it is
        until = time.time() + args.hold
        while time.time() < until:
            time.sleep(0.4)
            mod.teleport(st[:3], mode="direct", arrive=args.arrive, timeout=args.teleport_timeout)
    rest(mod, args, st, args.settle)
    # The box is the station's on x and z, whatever the player does meanwhile, and keeps the height the player has
    # come down to by the first bake (a station is set above the ground; the box must reach the ground under it):
    # one box for all the bakes of the station, so the same loaded geometry gives the same bytes -- which is what
    # tells a settled scene from one still loading.
    center = None
    extents = [size / 2.0, height / 2.0, size / 2.0]
    deadline = time.time() + args.settle_limit
    prev, same, bakes = None, 1, 0
    while True:
        now = in_scene(placed(mod), args.scene)
        if horizontal(now["pos"], st) > args.arrive:
            raise DriverError("the player was moved away while settling: at {} instead of {}".format(fmt3(now["pos"]), fmt3(st)))
        if args.hold > 0 and args.mode == "direct" and now["pos"][1] < st[1] - HOLD_DROP:
            mod.teleport(st[:3], mode="direct", arrive=args.arrive, timeout=args.teleport_timeout)
            now["pos"] = [float(v) for v in st[:3]]
        if center is None:
            center = [float(st[0]), float(now["pos"][1]), float(st[2])]
        key = {"polygonId": now.get("polygonId"), "tagHash": now.get("tagHash")}
        bake = mod.bake(center=now["pos"] if args.center_on_player else center, extents=extents, out=stem,
                        layers=args.layers, cull=args.cull, timeout=args.timeout, quiet=True, geometry=args.geometry)
        if not bake.get("ok", False):
            raise DriverError(bake.get("error", "bake failed"))
        in_scene(bake if "scene" in bake else now, args.scene, known=True)   # the scene the bake itself ran in
        bakes += 1
        same = same + 1 if same_bake(prev, bake) else 1
        # an empty result is waited on to the limit: nothing loaded yet looks the same twice in a row
        if same >= args.stable and (int(bake.get("tiles") or 0) > 0 or args.stable <= 1):
            return bake, key, bakes, True
        if time.time() >= deadline:
            return bake, key, bakes, args.stable <= 1
        prev = bake
        rest(mod, args, st, args.settle_step)


def cmd_sweep(mod, args):
    with open(args.stations, encoding="utf-8") as f:
        spec = json.load(f)
    if isinstance(spec, dict):
        stations = spec.get("stations", [])
        size = float(spec.get("size", args.size))
        height = float(spec.get("height", args.height))
    else:
        stations, size, height = spec, args.size, args.height
    if not stations:
        raise DriverError("no stations in {}".format(args.stations))
    for i, st in enumerate(stations):
        if not isinstance(st, list) or len(st) < 3:
            raise DriverError("station {} is not [x, y, z]".format(i))
    os.makedirs(args.out, exist_ok=True)
    # what this folder is meant to hold: `fromunity` refuses to build a file from fewer units
    write_json(os.path.join(args.out, args.prefix + "sweep.json"),
               {"prefix": args.prefix, "stations": [[float(v) for v in st[:3]] for st in stations], "size": size,
                "height": height, "layers": args.layers, "cull": args.cull, "geometry": args.geometry})
    log_path = os.path.join(args.out, "sweep.log")
    done = 0
    failed = []
    for i, st in enumerate(stations):
        stem = "{}{:03d}".format(args.prefix, i)
        if unit_done(args.out, stem, st) and not args.redo:
            print("[{}/{}] {} already baked".format(i + 1, len(stations), stem))
            done += 1
            continue
        print("[{}/{}] {} -> {}".format(i + 1, len(stations), stem, fmt3(st)))
        try:
            bake, key, bakes, stable = bake_station(mod, args, st, stem, size, height)
            copy_unit(mod.folder, stem, args.out,
                      {"navkey": key, "station": [float(v) for v in st[:3]], "bakes": bakes, "stable": stable})
            line = "{} ok: {} tiles, {} KB, {} sources, {} ms, player {}, polygon {} hash {}, {}".format(
                stem, bake.get("tiles"), int(bake.get("bytes", 0)) // 1024, bake.get("sources"), bake.get("elapsedMs"),
                fmt3(bake.get("player", st)), key["polygonId"], key["tagHash"],
                "{} bakes".format(bakes) if stable else "NOT STABLE after {} bakes".format(bakes))
            done += 1
        except DriverError as e:
            line = "{} FAILED: {}".format(stem, e)
            failed.append(stem)
            gone = isinstance(e, GameGone)
        else:
            gone = False
        print("  " + line)
        with open(log_path, "a", encoding="utf-8") as f:
            f.write(time.strftime("%H:%M:%S ") + line + "\n")
        if gone or (failed and args.stop_on_error):
            break
    print("sweep: {} of {} stations baked{}".format(done, len(stations), ", failed: " + ", ".join(failed) if failed else ""))
    return 1 if failed else 0


def cmd_follow(mod, args):
    """Bake around a player who plays: wherever they come to, the nearest point of a lattice gets one unit. For
    the places a sweep cannot reach -- a domain whose rooms load only as its story is played through."""
    os.makedirs(args.out, exist_ok=True)
    extents = [args.size / 2.0, args.height / 2.0, args.size / 2.0]

    def cell(pos):
        return (round(pos[0] / args.spacing), round(pos[1] / args.level), round(pos[2] / args.spacing))

    done, taken, tries = set(), 0, {}
    for f in sorted(os.listdir(args.out)):       # a session that is taken up again keeps what it has
        if f.startswith(args.prefix) and f.endswith(".json") and os.path.isfile(os.path.join(args.out, f[:-5] + ".tiles")):
            try:
                with open(os.path.join(args.out, f), encoding="utf-8") as fh:
                    side = json.load(fh)
            except (OSError, ValueError):
                continue
            if isinstance(side.get("station"), list) and side.get("stable") is not False:
                done.add(cell(side["station"]))
            taken = max(taken, int(f[len(args.prefix):-5]) + 1 if f[len(args.prefix):-5].isdigit() else 0)
    print("following the player: one unit per {:g} m (Ctrl+C ends); {} places baked so far".format(args.spacing, len(done)))
    waiting = None
    seen_scene, moved_from, moved_at = None, None, 0.0
    try:
        while True:
            st = mod.status(max_age=5.0)
            if st is not None and isinstance(st.get("pos"), list) and st.get("scene") != seen_scene:
                # The status names a new scene a moment before it carries the player's place in it: the place it
                # shows then is still the one in the scene left. Nothing is baked until the player has been moved
                # -- after every change of scene, those seen while waiting for --scene among them.
                seen_scene, moved_from, moved_at = st.get("scene"), list(st["pos"]), time.time()
            why = None
            if st is None or not isinstance(st.get("pos"), list):
                why = "no fresh status (a loading screen, or the game is gone)"
            elif args.scene is not None and st.get("scene") != args.scene:
                why = "the player is in scene {}, not {}".format(st.get("scene"), args.scene)
            if why:
                if why != waiting:
                    print("waiting: " + why)
                    waiting = why
                time.sleep(1.0)
                continue
            waiting = None
            if moved_from is not None:
                if math.dist(st["pos"], moved_from) < 20.0 and time.time() - moved_at < 15.0:
                    time.sleep(0.5)
                    continue
                moved_from = None
            key = cell(st["pos"])
            if key in done:
                time.sleep(0.5)
                continue
            # the box is the lattice point's, at the height the player has now: one box for all its bakes
            center = [key[0] * args.spacing, float(st["pos"][1]), key[2] * args.spacing]
            stem = "{}{:03d}".format(args.prefix, taken)
            deadline = time.time() + args.settle_limit
            prev, same, bakes, bake, stable, failure = None, 1, 0, None, False, None
            scene = st.get("scene")     # the place's scene: every bake of the unit is of it
            try:
                while True:
                    now = placed(mod)
                    if now.get("scene") != scene or horizontal(now["pos"], center) > args.size / 2.0:
                        # A player who has gone on takes the streaming along, and what the box holds from here on
                        # is not the place's any more: its unit is what the bakes so far gave.
                        if not bakes:
                            raise DriverError("the player left the place before it was baked")
                        break
                    answer = mod.bake(center=center, extents=extents, out=stem, layers=args.layers, cull=args.cull,
                                      timeout=args.timeout, quiet=True, geometry=args.geometry)
                    if not answer.get("ok", False):
                        raise DriverError(answer.get("error", "bake failed"))
                    if answer.get("scene", scene) != scene or (
                            isinstance(answer.get("player"), list) and horizontal(answer["player"], center) > args.size / 2.0):
                        # the bake itself ran when the player was gone: its tiles took the place of the ones before
                        # in the mod's folder, the copy made of those stands
                        if not bakes:
                            raise DriverError("the player stood outside the box, or in another scene, when it was baked")
                        break
                    bake = answer
                    bakes += 1
                    same = same + 1 if same_bake(prev, bake) else 1
                    stable = same >= args.stable and int(bake.get("tiles") or 0) > 0
                    # copied after every bake: whatever comes next, the place keeps the last bake made in it
                    copy_unit(mod.folder, stem, args.out,
                              {"navkey": {"polygonId": now.get("polygonId"), "tagHash": now.get("tagHash")},
                               "station": center, "bakes": bakes, "stable": stable})
                    if stable or time.time() >= deadline:
                        break
                    prev = bake
                    time.sleep(args.settle_step)
            except DriverError as e:
                failure = e
            if not bakes:
                print(("waiting: {}" if isinstance(failure, GameGone) else stem + " failed: {}").format(failure))
                time.sleep(2.0)
                continue
            taken += 1
            tries[key] = tries.get(key, 0) + 1
            if stable or tries[key] >= 3:
                done.add(key)       # a place taken before it settled is baked again, up to three times
            print("{} at {}: {} tiles, {} KB, {} sources, {}{}".format(
                stem, fmt3(center), bake.get("tiles"), int(bake.get("bytes", 0)) // 1024, bake.get("sources"),
                "{} bakes".format(bakes) if stable else "not settled after {} bakes".format(bakes),
                "" if failure is None else " (then: {})".format(failure)))
            if failure is not None:
                time.sleep(2.0)
    except KeyboardInterrupt:
        pass
    print("{} places baked into {}".format(len(done), args.out))
    return 0


def cmd_collect(mod, args):
    os.makedirs(args.out, exist_ok=True)
    n = 0
    for f in sorted(os.listdir(mod.folder)):
        if f.endswith(".tiles") and os.path.isfile(os.path.join(mod.folder, f[:-6] + ".json")):
            copy_unit(mod.folder, f[:-6], args.out)
            n += 1
    print("{} units copied to {}".format(n, args.out))
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--dir", default=DEFAULT_DIR, help="the mod's navbake folder (default: {})".format(DEFAULT_DIR))
    sub = ap.add_subparsers(dest="cmd", metavar="command")
    sub.required = True

    p = sub.add_parser("status", help="status.json + a status command")
    p.set_defaults(fn=cmd_status)

    p = sub.add_parser("layers", help="the game's layer names")
    p.set_defaults(fn=cmd_layers)

    p = sub.add_parser("navkey", help="the (polygon id, scene tag hash) pair the client last sent, and the active scene tags")
    p.set_defaults(fn=cmd_navkey)

    p = sub.add_parser("polygons", help="dump the client's polygon table of the current polygon-mode scene to navbake/polygons_<scene>.json")
    p.add_argument("--collect", metavar="FOLDER", help="copy the dump there")
    p.set_defaults(fn=cmd_polygons)

    p = sub.add_parser("teleport", help="move the player")
    p.add_argument("x", type=float)
    p.add_argument("y", type=float)
    p.add_argument("z", type=float)
    p.add_argument("--mode", choices=["auto", "direct", "map"], default="auto")
    p.add_argument("--arrive", type=float, default=12.0, help="metres (x/z) that count as arrived")
    p.add_argument("--timeout", type=float, default=60.0)
    p.set_defaults(fn=cmd_teleport)

    p = sub.add_parser("bake", help="bake one box")
    p.add_argument("--center", type=float, nargs=3, metavar=("X", "Y", "Z"), help="default: the player")
    p.add_argument("--size", type=float, default=256.0, help="metres on x and z")
    p.add_argument("--height", type=float, default=600.0, help="metres on y")
    p.add_argument("--out", help="file stem under navbake/ (default: bake_<id>)")
    p.add_argument("--layers", type=int, default=GAME_LAYERS,
                   help="layer mask (default {}: Terrain, SceneProp, ScenePropIgnoreCamera; -1 = every layer)".format(GAME_LAYERS))
    p.add_argument("--cull", type=int, default=0, help="the builder's cullingAreaMask")
    p.add_argument("--geometry", type=int, choices=(GEOMETRY_COLLIDERS, GEOMETRY_RENDER_MESHES), default=GEOMETRY_COLLIDERS,
                   help="what the builder collects: 1 = the physics colliders (default), 0 = the render meshes (the readable ones)")
    p.add_argument("--sync", action="store_true", help="the synchronous builder (freezes the game while it runs)")
    p.add_argument("--timeout", type=float, default=900.0)
    p.add_argument("--collect", metavar="FOLDER", help="copy the unit files there when done")
    p.set_defaults(fn=cmd_bake)

    p = sub.add_parser("sweep", help="teleport to every station and bake around it")
    p.add_argument("stations", help="a JSON file: [[x, y, z], ...] or {\"stations\": [...], \"size\": .., \"height\": ..}")
    p.add_argument("--out", required=True, help="where the unit files are collected")
    p.add_argument("--prefix", default="st", help="unit stems: <prefix><index> (default st)")
    p.add_argument("--size", type=float, default=320.0)
    p.add_argument("--height", type=float, default=600.0)
    p.add_argument("--settle", type=float, default=5.0, help="seconds after arriving, before the first bake")
    p.add_argument("--stable", type=int, default=2,
                   help="bake a station until this many bakes in a row are the same (default 2; 1 = one bake, whatever is loaded)")
    p.add_argument("--settle-step", type=float, default=2.5, help="seconds between two bakes of a station")
    p.add_argument("--settle-limit", type=float, default=60.0,
                   help="seconds after which a station that keeps changing (or stays empty) is taken as it is and marked not stable")
    p.add_argument("--nudge", type=float, default=20.0,
                   help="--mode direct: after arriving step this many metres aside twice and come back, so that the server "
                        "takes the new position and shows what it spawns there (default 20; 0 = no steps)")
    p.add_argument("--scene", type=int,
                   help="the scene id the stations are of: the sweep stops as soon as the player is in another scene "
                        "(default: no check)")
    p.add_argument("--walk", type=float, default=0.0,
                   help="--mode direct: reach a station in steps of this many metres instead of one jump (0 = one jump); "
                        "for a scene whose server pulls a player back after a far jump")
    p.add_argument("--hold", type=float, default=0.0,
                   help="--mode direct: for this many seconds after arriving keep putting the player back on the station "
                        "(a level that is not loaded yet lets the player fall through), and after that whenever they "
                        "have dropped under it, for as long as the station is baked")
    p.add_argument("--avatar-wait", type=float, default=0.0,
                   help="seconds to wait for a character that is not there when a station is to be reached (brought "
                        "back after a fall, hidden by a cutscene) before the station fails")
    p.add_argument("--mode", choices=["auto", "direct", "map"], default="auto")
    p.add_argument("--arrive", type=float, default=12.0)
    p.add_argument("--teleport-timeout", type=float, default=60.0)
    p.add_argument("--layers", type=int, default=GAME_LAYERS)
    p.add_argument("--cull", type=int, default=0)
    p.add_argument("--geometry", type=int, choices=(GEOMETRY_COLLIDERS, GEOMETRY_RENDER_MESHES), default=GEOMETRY_COLLIDERS,
                   help="1 = the physics colliders (default), 0 = the render meshes")
    p.add_argument("--timeout", type=float, default=900.0)
    p.add_argument("--redo", action="store_true", help="bake again where a unit exists")
    p.add_argument("--stop-on-error", action="store_true")
    p.add_argument("--center-on-player", action="store_true",
                   help="bake around where the player stands instead of around the station (the units then differ from bake to bake)")
    p.set_defaults(fn=cmd_sweep)

    p = sub.add_parser("follow", help="bake around a player who plays: one unit per lattice point they come near")
    p.add_argument("--out", required=True, help="where the unit files are collected")
    p.add_argument("--prefix", default="f", help="unit stems: <prefix><number> (default f)")
    p.add_argument("--scene", type=int, help="bake only while the player is in this scene")
    p.add_argument("--spacing", type=float, default=48.0, help="metres between two lattice points on x and z (default 48)")
    p.add_argument("--level", type=float, default=40.0, help="metres of height that make a place a new one (default 40)")
    p.add_argument("--size", type=float, default=192.0)
    p.add_argument("--height", type=float, default=700.0)
    p.add_argument("--stable", type=int, default=2, help="bake a place until this many bakes in a row are the same")
    p.add_argument("--settle-step", type=float, default=2.5)
    p.add_argument("--settle-limit", type=float, default=30.0,
                   help="seconds after which a place that keeps changing is taken as it is and baked again on the next visit")
    p.add_argument("--layers", type=int, default=GAME_LAYERS)
    p.add_argument("--cull", type=int, default=0)
    p.add_argument("--geometry", type=int, choices=(GEOMETRY_COLLIDERS, GEOMETRY_RENDER_MESHES), default=GEOMETRY_COLLIDERS,
                   help="1 = the physics colliders (default), 0 = the render meshes")
    p.add_argument("--timeout", type=float, default=900.0)
    p.set_defaults(fn=cmd_follow)

    p = sub.add_parser("collect", help="copy every unit out of navbake/")
    p.add_argument("--out", required=True)
    p.set_defaults(fn=cmd_collect)

    args = ap.parse_args(argv)
    mod = Mod(os.path.abspath(args.dir))
    hold = None
    try:
        args.held_by_another = False
        if args.fn is not cmd_collect and os.path.isdir(mod.folder):
            hold = hold_folder(mod.folder)
            if hold is None:
                if args.fn is not cmd_status:
                    raise DriverError("another navbake_driver is at work on {} -- one at a time: a second one would "
                                      "replace its commands".format(mod.folder))
                args.held_by_another = True
        return args.fn(mod, args)
    except DriverError as e:
        print("error: {}".format(e), file=sys.stderr)
        return 1
    finally:
        if hold is not None:
            hold.close()
            if os.name == "nt":     # where a file that is open elsewhere cannot be removed: another driver's stays
                try:
                    os.remove(os.path.join(mod.folder, LOCK_NAME))
                except OSError:
                    pass


if __name__ == "__main__":
    sys.exit(main())
