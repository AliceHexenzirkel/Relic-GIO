#!/usr/bin/env python3
"""Inspect, inventory, pack and verify the navmesh files of the GIO pathfindingserver.

  python build/navmesh_tool.py inspect <file.navmesh> [--json]
  python build/navmesh_tool.py inventory <stack dir> [--md5]
  python build/navmesh_tool.py pack <1.6|2.8> <source dir> [--out <dir>] [--vendor-list <md5 list | stack dir>]
                                    [--note <text>] [--force]
  python build/navmesh_tool.py verify <bundle dir>
  python build/navmesh_tool.py md5list <md5 list file | -> <bundle dir>
  python build/navmesh_tool.py fromunity <scene id> <export dir> --out <dir>     # Unity bake -> server files
  python build/navmesh_tool.py regions <file.navmesh>                            # the region rule against a vendor file

The pathfindingserver loads `<stack>/server/res/NavMesh/*.navmesh` -- and only the files named in
`<stack>/server/res/server_res_version_md5_list.txt` (one line per file, `<md5>  ./NavMesh/<name>`).
A bundle is the SMALL set of files that add to or replace the vendor's own set, never the whole set:
`pack` compares every file with the vendor's md5 list and leaves out what the stack already ships byte
for byte. Layout: `<out>/<ver>/navmesh.json` + `<out>/<ver>/NavMesh/<name>.navmesh`; build/publish.ps1
ships it as `agent/payloads/<ver>/navmesh/` and the agent installs it into the stack from there.
docs/NAVMESH.md has the file format and the workflow.

Exit codes: 0 ok; 1 a check failed (a mismatch, an invalid file, an empty or refused pack); 2 usage, or
an input that cannot be read.
"""
import argparse
import hashlib
import json
import math
import os
import re
import shutil
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VERSIONS = ("1.6", "2.8")
DEFAULT_OUT = os.path.join(ROOT, "_bundled_navmesh")
MANIFEST = "navmesh.json"
MANIFEST_FORMAT = 1
NAVMESH_SUBDIR = "NavMesh"

# What the server accepts as a file name -- the agent's grammar, character for character (NAVMESH_NAME_RE and
# NAVMESH_NAME_MAX in agent/gio_agent.py; build/publish.ps1 gates with the same expression): Config::loadNavMeshData
# splits the stem on '_', the first token is 'scene' or 'scenepolygon', the rest are integers (their meaning is in
# classify_name). ASCII digits only, like the agent: `\d` would also take the digits of other scripts, which int()
# reads as numbers.
NAME_RE = re.compile(r"^(?:scene_[0-9]+(?:_[0-9]+)?(?:_-?[0-9]+_-?[0-9]+)?|scenepolygon_[0-9]+_[0-9]+_[0-9]+)\.navmesh\Z")
NAME_MAX = 128
KINDS = ("single", "activity", "block", "activity-block", "polygon")

# Container: int32 scene id, int32 tile count, per tile {int32 size, bytes, pad to 4, 16-byte hash},
# then the 120-byte footer: NavMeshBuildSettings (14 fields), two int32 (off-mesh links, obstacles --
# empty vectors), bounds center + extents, rotation, position, agentTypeID.
TILE_MAGIC = b"VAND"            # the int32 'DNAV' as the bytes lie in the file: 56 41 4E 44
TILE_VERSION = 17
TILE_HEADER = 72                # NavMeshDataHeader; an empty tile is exactly this header
TILE_HASH = 16
SETTINGS_FMT = "<ifffffffifiiii"
SETTINGS_NAMES = ("agentTypeID", "agentRadius", "agentHeight", "agentSlope", "agentClimb", "ledgeDropHeight",
                  "maxJumpAcrossDistance", "minRegionArea", "manualCellSize", "cellSize", "manualTileSize",
                  "tileSize", "accuratePlacement", "generateDetailMap")
TAIL_FMT = "<ii3f3f4f3fi"
FOOTER = struct.calcsize(SETTINGS_FMT) + struct.calcsize(TAIL_FMT)
# The settings of every vendor file that holds a tile. A file WITHOUT a tile is one of two things, told apart by
# its footer (empty_note): Unity's default settings with zero bounds -- nothing was baked for that scene -- or the
# vendor settings with the bounds of its block -- baked, with no walkable surface in it. Another bake is not
# wrong by itself, so a difference is only reported, never refused.
VENDOR_SETTINGS = {"agentTypeID": 0, "agentRadius": 0.25, "agentHeight": 1.6, "agentSlope": 60.0, "agentClimb": 0.4,
                   "ledgeDropHeight": 0.0, "maxJumpAcrossDistance": 0.0, "minRegionArea": 36.0, "manualCellSize": 1,
                   "cellSize": 0.125, "manualTileSize": 1, "tileSize": 128, "accuratePlacement": 0,
                   "generateDetailMap": 1}
# What NavMeshBuildSettings holds before anyone sets it (Unity 2017.4): the footer of a never-baked file.
UNITY_DEFAULT_SETTINGS = {"agentTypeID": 0, "agentRadius": 0.5, "agentHeight": 2.0, "agentSlope": 45.0, "agentClimb": 0.4,
                          "ledgeDropHeight": 0.0, "maxJumpAcrossDistance": 0.0, "minRegionArea": 2.0, "manualCellSize": 0,
                          "cellSize": 1 / 6, "manualTileSize": 0, "tileSize": 256, "accuratePlacement": 0,
                          "generateDetailMap": 0}
BLOCK_METRES = 1024             # a big-world block covers [bx*1024, (bx+1)*1024] on x and z

NAVMESH_DIR = os.path.join("server", "res", NAVMESH_SUBDIR)
MD5_LIST = os.path.join("server", "res", "server_res_version_md5_list.txt")
SCENE_DATA = os.path.join("server", "data", "txt", "SceneData.txt")
# The md5 list the way the agent reads it (navmesh_md5_list_merge in agent/gio_agent.py -- the two must agree byte
# for byte, so every rule of the merge below is that function's): a line names a file only when its first token is
# a 32-digit hex md5 -- `<md5><spaces><path>` --, and its path is compared with a leading `./` dropped and `\` read
# as `/`. Our own lines are `<md5>  ./NavMesh/<name>`, the vendor's spelling and spacing.
MD5_LINE_RE = re.compile(r"^([0-9A-Fa-f]{32})(\s+)(\S.*?)\s*\Z")
LIST_PREFIX = "./NavMesh/"
LIST_KEY = LIST_PREFIX[2:]      # the same path, normalised
# SceneData.txt is read by its header names: the column positions differ between the versions.
COL_ID, COL_TYPE, COL_IGNORE, COL_MODE = "ID", "类型", "是否忽略navmesh", "NavmeshMode"
SCENE_TYPES = {1: "world", 2: "dungeon", 3: "room", 4: "home world", 5: "home room", 6: "activity"}

MB = 1024 * 1024
BIG_BUNDLE = 400 * MB
CHUNK = 1024 * 1024


class NavmeshError(Exception):
    """A name or a file the pathfindingserver would not load the way a bundle needs it."""


def fmt_mb(n):
    return "{:.1f} MB".format(n / MB)


def plural(n, word):
    return "{} {}{}".format(n, word, "" if n == 1 else "s")


def fmt_num(v):
    if isinstance(v, float):
        v = round(v, 4)
        return str(int(v)) if v == int(v) else str(v)
    return str(v)


def fmt_vec(values):
    return "(" + ", ".join(fmt_num(v) for v in values) + ")"


def ranges(ids):
    """1001, 1004-1006, 1008 -- a compact rendering of a sorted id list."""
    out = []
    ids = sorted(ids)
    i = 0
    while i < len(ids):
        j = i
        while j + 1 < len(ids) and ids[j + 1] == ids[j] + 1:
            j += 1
        out.append(str(ids[i]) if j == i else "{}-{}".format(ids[i], ids[j]))
        i = j + 1
    return ", ".join(out) if out else "none"


def read_bytes(path):
    with open(path, "rb") as f:
        return f.read()


def digest(data):
    return hashlib.md5(data).hexdigest(), hashlib.sha256(data).hexdigest()


def md5_of_file(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        while True:
            chunk = f.read(CHUNK)
            if not chunk:
                break
            h.update(chunk)
    return h.hexdigest()


def write_json_durable(path, obj):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(obj, f, indent=2)
        f.write("\n")
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)


def copy_file_durable(src, dst):
    """Copy src to dst in chunks, fsync, and return the md5 of what was written."""
    h = hashlib.md5()
    with open(src, "rb") as fi, open(dst, "wb") as fo:
        while True:
            chunk = fi.read(CHUNK)
            if not chunk:
                break
            h.update(chunk)
            fo.write(chunk)
        fo.flush()
        os.fsync(fo.fileno())
    return h.hexdigest()


# ----------------------------------------------------------------------------------------------- names

def classify_name(name):
    """scene_<id> = single; scene_<id>_<activity>; scene_<id>_<bx>_<by> = block; scene_<id>_<activity>_<bx>_<by>;
    scenepolygon_<id>_<polygon>_<tag> = polygon-mode navmesh (NavmeshMode=1 scenes)."""
    if len(name) > NAME_MAX or not NAME_RE.match(name):
        raise NavmeshError("not a name the server loads: scene_<id>[_<activity>][_<bx>_<by>].navmesh or "
                           "scenepolygon_<id>_<polygon>_<tag>.navmesh (ASCII digits, at most {} characters; only "
                           "block coordinates may be negative)".format(NAME_MAX))
    # The grammar settles the shape: a polygon name has exactly scene, polygon and tag; a scene name has its id,
    # an optional activity and an optional block pair; activity, polygon and tag are never negative.
    head, *nums = name[:-len(".navmesh")].split("_")
    nums = [int(n) for n in nums]
    scene, rest = nums[0], nums[1:]
    info = {"scene": scene, "kind": None, "block": None, "activity": None, "polygon": None, "sceneTagHash": None}
    if head == "scenepolygon":
        info.update(kind="polygon", polygon=rest[0], sceneTagHash=rest[1])
    elif not rest:
        info["kind"] = "single"
    elif len(rest) == 1:
        info.update(kind="activity", activity=rest[0])
    elif len(rest) == 2:
        info.update(kind="block", block=rest)
    else:
        info.update(kind="activity-block", activity=rest[0], block=rest[1:])
    return info


def describe_name(info):
    s = "scene {}".format(info["scene"])
    if info["kind"] == "single":
        return s + ", the whole scene"
    if info["kind"] == "activity":
        return s + ", activity {} variant".format(info["activity"])
    if info["kind"] == "block":
        return s + ", block {}".format(fmt_vec(info["block"]))
    if info["kind"] == "activity-block":
        return s + ", activity {} variant of block {}".format(info["activity"], fmt_vec(info["block"]))
    return s + ", polygon {} with scene tag hash {}".format(info["polygon"], info["sceneTagHash"])


# ------------------------------------------------------------------------------------------- container

def parse_container(data):
    """One .navmesh file. Structural damage raises NavmeshError; what the tiles say lands in 'problems'."""
    mv = memoryview(data)
    n = len(data)
    if n < 8 + FOOTER:
        raise NavmeshError("only {} bytes: shorter than the two header ints plus the {}-byte footer".format(n, FOOTER))
    scene, tile_count = struct.unpack_from("<ii", mv, 0)
    if tile_count < 0:
        raise NavmeshError("negative tile count {}".format(tile_count))
    limit = n - FOOTER
    off = 8
    empty = bad_magic = bad_version = short = 0
    tile_bytes = 0
    xs, ys = [], []
    for i in range(tile_count):
        if off + 4 > limit:
            raise NavmeshError("tile {}: the file ends before its size field (tile count {} too large?)".format(i, tile_count))
        size, = struct.unpack_from("<i", mv, off)
        off += 4
        if size < 0:
            raise NavmeshError("tile {}: negative size {}".format(i, size))
        end = off + size + (-size) % 4 + TILE_HASH
        if end > limit:
            raise NavmeshError("tile {}: {} bytes run past the end of the file".format(i, size))
        if size < TILE_HEADER:
            short += 1
        else:
            if bytes(mv[off:off + 4]) != TILE_MAGIC:
                bad_magic += 1
            version, x, y = struct.unpack_from("<iii", mv, off + 4)
            if version != TILE_VERSION:
                bad_version += 1
            xs.append(x)
            ys.append(y)
            if size == TILE_HEADER:
                empty += 1
        tile_bytes += size
        off = end
    if off != limit:
        raise NavmeshError("{} unexpected bytes between the last tile and the footer".format(limit - off))
    settings = dict(zip(SETTINGS_NAMES, struct.unpack_from(SETTINGS_FMT, mv, off)))
    tail = struct.unpack_from(TAIL_FMT, mv, off + struct.calcsize(SETTINGS_FMT))
    problems = []
    if short:
        problems.append("{} tiles shorter than the {}-byte tile header".format(short, TILE_HEADER))
    if bad_magic:
        problems.append("{} tiles without the DNAV magic".format(bad_magic))
    if bad_version:
        problems.append("{} tiles whose version is not {}".format(bad_version, TILE_VERSION))
    if tail[0] or tail[1]:
        problems.append("off-mesh link / obstacle counts {} / {} but the file carries no such data".format(tail[0], tail[1]))
    return {
        "scene": scene, "tiles": tile_count, "emptyTiles": empty, "tileBytes": tile_bytes,
        "grid": {"x": [min(xs), max(xs)], "y": [min(ys), max(ys)]} if xs else None,
        "settings": {k: (round(v, 6) if isinstance(v, float) else v) for k, v in settings.items()},
        "offMeshLinks": tail[0], "obstacles": tail[1],
        "bounds": {"center": [round(v, 4) for v in tail[2:5]], "extents": [round(v, 4) for v in tail[5:8]]},
        "rotation": [round(v, 4) for v in tail[8:12]], "position": [round(v, 4) for v in tail[12:15]],
        "agentTypeID": tail[15], "problems": problems,
    }


def tiles_per_block(settings):
    """64 with the vendor settings (128 cells x 0.125 m = 16 m tiles over a 1024 m block); None when the
    settings do not divide a block into whole tiles."""
    edge = settings["tileSize"] * settings["cellSize"]
    if edge <= 0:
        return None
    per = BLOCK_METRES / edge
    return int(per) if abs(per - round(per)) < 1e-6 else None


def block_tile_range(block, per):
    return [per * block[0], per * block[0] + per - 1], [per * block[1], per * block[1] + per - 1]


def _same_setting(got, want):
    return abs(got - want) < 1e-5 if isinstance(want, float) else got == want


def vendor_setting_diffs(settings):
    return ["{} {} (vendor {})".format(k, fmt_num(settings[k]), fmt_num(want))
            for k, want in VENDOR_SETTINGS.items() if not _same_setting(settings[k], want)]


def empty_note(c):
    """What the footer of a file without a tile says: baked with nothing walkable in it, or never baked."""
    zero = not any(c["bounds"]["center"] + c["bounds"]["extents"])
    vendor = not vendor_setting_diffs(c["settings"])
    unity = all(_same_setting(c["settings"][k], want) for k, want in UNITY_DEFAULT_SETTINGS.items())
    if vendor and not zero:
        return "no tile, but the vendor's settings and the bounds of a bake: baked, with no walkable surface in it"
    if unity and zero:
        return "no tile, Unity's default settings and zero bounds: nothing was baked for it"
    return "no tile; settings {}, bounds {}".format(
        "the vendor's" if vendor else "Unity's defaults" if unity else "neither the vendor's nor Unity's defaults",
        "zero" if zero else "set")


def examine(path, data=None):
    """Everything the tool knows about one file: name, container, hashes, problems (refused) and
    warnings (reported)."""
    name = os.path.basename(path)
    problems, warnings = [], []
    name_info = None
    try:
        name_info = classify_name(name)
    except NavmeshError as e:
        problems.append("name: {}".format(e))
    if data is None:
        data = read_bytes(path)
    container = None
    try:
        container = parse_container(data)
    except NavmeshError as e:
        problems.append("container: {}".format(e))
    if container:
        problems.extend(container["problems"])
        if name_info and container["scene"] != name_info["scene"]:
            problems.append("the file says scene {}, the name says scene {}".format(container["scene"], name_info["scene"]))
        per = tiles_per_block(container["settings"])
        if name_info and name_info["block"] and container["grid"] and per:
            rx, ry = block_tile_range(name_info["block"], per)
            gx, gy = container["grid"]["x"], container["grid"]["y"]
            if gx[0] < rx[0] or gx[1] > rx[1] or gy[0] < ry[0] or gy[1] > ry[1]:
                warnings.append("tile grid x {}..{}, y {}..{} lies outside block {}'s range x {}..{}, y {}..{}".format(
                    gx[0], gx[1], gy[0], gy[1], fmt_vec(name_info["block"]), rx[0], rx[1], ry[0], ry[1]))
        if not container["tiles"]:
            warnings.append(empty_note(container))
        else:
            diffs = vendor_setting_diffs(container["settings"])
            if diffs:
                warnings.append("build settings differ from the vendor's: " + ", ".join(diffs))
    md5, sha256 = digest(data)
    return {"name": name, "size": len(data), "md5": md5, "sha256": sha256, "nameInfo": name_info,
            "container": container, "problems": problems, "warnings": warnings}


def manifest_entry(ex):
    ni, c = ex["nameInfo"], ex["container"]
    return {"name": ex["name"], "size": ex["size"], "md5": ex["md5"], "sha256": ex["sha256"], "scene": ni["scene"],
            "kind": ni["kind"], "block": ni["block"], "activity": ni["activity"], "polygon": ni["polygon"],
            "sceneTagHash": ni["sceneTagHash"], "tiles": c["tiles"]}


# --------------------------------------------------------------------------------------------- inspect

def cmd_inspect(args):
    path = args.file
    if not os.path.isfile(path):
        print("not a file: {}".format(path), file=sys.stderr)
        return 2
    ex = examine(path)
    ok = not ex["problems"]
    if args.json:
        out = {k: v for k, v in ex.items()}
        out["path"] = os.path.abspath(path)
        out["ok"] = ok
        print(json.dumps(out, indent=2))
        return 0 if ok else 1
    c, ni = ex["container"], ex["nameInfo"]
    print("{}  ({:,} bytes)".format(ex["name"], ex["size"]))
    print("  md5 {}  sha256 {}".format(ex["md5"], ex["sha256"]))
    print("  name: {}".format(describe_name(ni) + " [" + ni["kind"] + "]" if ni else "does not match the grammar"))
    if c:
        print("  container: scene {}, {} tiles ({} empty, {} with geometry), {:,} bytes of tile data".format(
            c["scene"], c["tiles"], c["emptyTiles"], c["tiles"] - c["emptyTiles"], c["tileBytes"]))
        checks = ", ".join(c["problems"]) if c["problems"] else "DNAV version {} on every tile".format(TILE_VERSION)
        grid = "no tile carries a header"
        if c["grid"]:
            grid = "x {}..{}, y {}..{}".format(c["grid"]["x"][0], c["grid"]["x"][1], c["grid"]["y"][0], c["grid"]["y"][1])
            per = tiles_per_block(c["settings"])
            if ni and ni["block"] and per:
                rx, ry = block_tile_range(ni["block"], per)
                grid += " (block {} expects x {}..{}, y {}..{})".format(fmt_vec(ni["block"]), rx[0], rx[1], ry[0], ry[1])
        print("  tiles: {}; grid {}".format(checks, grid))
        print("  settings: " + ", ".join("{} {}".format(k, fmt_num(v)) for k, v in c["settings"].items()))
        print("  off-mesh links {}, obstacles {}".format(c["offMeshLinks"], c["obstacles"]))
        print("  bounds: center {}, extents {}; rotation {}; position {}; agentTypeID {}".format(
            fmt_vec(c["bounds"]["center"]), fmt_vec(c["bounds"]["extents"]), fmt_vec(c["rotation"]),
            fmt_vec(c["position"]), c["agentTypeID"]))
    for w in ex["warnings"]:
        print("  note: {}".format(w))
    if ok:
        print("  checks: ok")
    else:
        for p in ex["problems"]:
            print("  PROBLEM: {}".format(p))
    return 0 if ok else 1


# ------------------------------------------------------------------------------------------- inventory

def decode_list(raw):
    """The bytes of a list as the agent's reader hands them over: utf-8, every undecodable byte replaced."""
    return raw.decode("utf-8", "replace")


def split_lines_keep_ends(text):
    """The lines of a text, each with its own line ending kept ("" on a last line without one). Split on "\\n"
    only -- never on the other characters str.splitlines() breaks at."""
    out, start = [], 0
    while start < len(text):
        i = text.find("\n", start)
        if i < 0:
            out.append(text[start:])
            break
        out.append(text[start:i + 1])
        start = i + 1
    return out


def list_line_path(body):
    """The path a line of the md5 list names, normalised for a comparison (a leading `./` dropped, `\\` read as
    `/`); None for a line that is not `<md5> <path>` (empty, a comment, one token, no md5 in front)."""
    m = MD5_LINE_RE.match(body)
    if not m:
        return None
    p = m.group(3).replace("\\", "/")
    while p.startswith("./"):
        p = p[2:]
    return p


def list_entries(text):
    """[(md5 lower-case, normalised path, the line's text)] of every line that names a file."""
    out = []
    for raw in split_lines_keep_ends(text):
        body = raw.rstrip("\r\n")
        path = list_line_path(body)
        if path is not None:
            out.append((MD5_LINE_RE.match(body).group(1).lower(), path, body))
    return out


def list_navmesh_names(entries):
    """name -> md5 for every entry under NavMesh/ -- the lines the agent's merge replaces by name."""
    return {path[len(LIST_KEY):]: md5 for md5, path, _ in entries if path.startswith(LIST_KEY)}


def read_scene_data(path):
    raw = read_bytes(path)
    text = raw.decode("utf-8-sig", "replace")
    lines = [ln[:-1] if ln.endswith("\r") else ln for ln in text.split("\n")]
    header = lines[0].split("\t") if lines else []
    cols = {name: header.index(name) if name in header else None for name in (COL_ID, COL_TYPE, COL_IGNORE, COL_MODE)}
    rows, short = [], 0
    if cols[COL_ID] is None:
        # Without the ID column no row can be keyed: the caller reports the header, nothing else is read.
        return {"cols": cols, "rows": rows, "short": short, "columns": len(header)}
    needed = [i for i in cols.values() if i is not None]
    for ln in lines[1:]:
        if not ln.strip():
            continue
        f = ln.split("\t")
        if len(f) <= max(needed):
            short += 1
            continue
        try:
            sid = int(f[cols[COL_ID]].strip())
        except ValueError:
            short += 1
            continue
        stype = f[cols[COL_TYPE]].strip() if cols[COL_TYPE] is not None else ""
        ignore = cols[COL_IGNORE] is not None and f[cols[COL_IGNORE]].strip() not in ("", "0")
        mode = f[cols[COL_MODE]].strip() if cols[COL_MODE] is not None else ""
        rows.append({"id": sid, "type": int(stype) if stype.isdigit() else None, "ignore": ignore,
                     "mode": int(mode) if mode.isdigit() else 0})
    return {"cols": cols, "rows": rows, "short": short, "columns": len(header)}


def type_label(t):
    return "type {} ({})".format(t, SCENE_TYPES[t]) if t in SCENE_TYPES else ("type {}".format(t) if t is not None else "no type")


def cmd_inventory(args):
    stack = os.path.abspath(args.stack)
    if not os.path.isdir(stack):
        print("not a folder: {}".format(stack), file=sys.stderr)
        return 2
    print("Stack: {}".format(stack))

    nav_dir = os.path.join(stack, NAVMESH_DIR)
    by_scene, bad_names, total, files = {}, [], 0, []
    if not os.path.isdir(nav_dir):
        print("NavMesh folder: {} -- MISSING (nothing for the pathfindingserver to load)".format(NAVMESH_DIR))
    else:
        files = sorted(f for f in os.listdir(nav_dir) if f.lower().endswith(".navmesh"))
        for f in files:
            total += os.path.getsize(os.path.join(nav_dir, f))
            try:
                info = classify_name(f)
            except NavmeshError:
                bad_names.append(f)
                continue
            s = by_scene.setdefault(info["scene"], {"single": 0, "activity": [], "block": [], "activity-block": {}, "polygon": 0})
            if info["kind"] == "single":
                s["single"] += 1
            elif info["kind"] == "activity":
                s["activity"].append(info["activity"])
            elif info["kind"] == "block":
                s["block"].append(tuple(info["block"]))
            elif info["kind"] == "activity-block":
                s["activity-block"][info["activity"]] = s["activity-block"].get(info["activity"], 0) + 1
            else:
                s["polygon"] += 1
        print("NavMesh folder: {} -- {} files, {:,} bytes ({})".format(NAVMESH_DIR, len(files), total, fmt_mb(total)))
        print("  scenes with navmesh: {}".format(len(by_scene)))
        block_scenes = sorted(sc for sc, s in by_scene.items() if s["block"] or s["activity-block"])
        if block_scenes:
            print("  block scenes (big worlds):")
            for sc in block_scenes:
                s = by_scene[sc]
                parts = ["{} blocks".format(len(s["block"]))]
                if s["activity-block"]:
                    parts.append("activity blocks " + ", ".join("{} x{}".format(a, n) for a, n in sorted(s["activity-block"].items())))
                if s["single"]:
                    parts.append("+ a whole-scene file")
                print("    scene {}: {}".format(sc, ", ".join(parts)))
        singles = sorted(sc for sc, s in by_scene.items() if s["single"] and sc not in block_scenes)
        print("  single-file scenes ({}): {}".format(len(singles), ranges(singles)))
        acts = sorted((sc, a) for sc, s in by_scene.items() for a in s["activity"])
        print("  activity variants of single scenes: {}".format(
            ", ".join("scene {} activity {}".format(sc, a) for sc, a in acts) if acts else "none"))
        polys = sorted(sc for sc, s in by_scene.items() if s["polygon"])
        print("  polygon files (scenepolygon_*): {}".format(
            ", ".join("scene {} x{}".format(sc, by_scene[sc]["polygon"]) for sc in polys) if polys else "none"))
        print("  names outside the grammar (never loaded): {}".format(", ".join(bad_names) if bad_names else "none"))

    sd_path = os.path.join(stack, SCENE_DATA)
    if not os.path.isfile(sd_path):
        print("SceneData.txt: {} -- MISSING".format(SCENE_DATA))
    else:
        sd = read_scene_data(sd_path)
        cols = sd["cols"]
        labels = {COL_ID: "ID", COL_TYPE: "type ({})".format(COL_TYPE), COL_IGNORE: "ignore-navmesh ({})".format(COL_IGNORE)}
        lacking = [labels[c] for c in (COL_ID, COL_TYPE, COL_IGNORE) if cols[c] is None]
        if lacking:
            print("SceneData.txt: {} -- its header ({}) lacks the column{} {}: the scenes are not compared".format(
                SCENE_DATA, plural(sd["columns"], "column"), "" if len(lacking) == 1 else "s", ", ".join(lacking)))
        else:
            print("SceneData.txt: {} -- {} scenes, {} columns (ID at {}, type at {}, ignore-navmesh at {}, NavmeshMode {})".format(
                SCENE_DATA, len(sd["rows"]), sd["columns"], cols[COL_ID], cols[COL_TYPE], cols[COL_IGNORE],
                "at {}".format(cols[COL_MODE]) if cols[COL_MODE] is not None else "absent"))
            if sd["short"]:
                print("  WARNING: {} rows shorter than the header (a short row stops the server)".format(sd["short"]))
            with_nav = set(by_scene)
            in_sd = {r["id"] for r in sd["rows"]}
            have = [r for r in sd["rows"] if r["id"] in with_nav]
            missing = [r for r in sd["rows"] if r["id"] not in with_nav]
            missing_plain = [r for r in missing if not r["ignore"]]
            missing_ign = [r for r in missing if r["ignore"]]
            print("  scenes with a navmesh file: {}".format(len(have)))
            print("  scenes WITHOUT any navmesh file, not ignore-flagged: {}".format(len(missing_plain)))
            by_type = {}
            for r in missing_plain:
                by_type.setdefault(r["type"], []).append(r["id"])
            for t in sorted(by_type, key=lambda v: (v is None, v)):
                print("    {} ({}): {}".format(type_label(t), len(by_type[t]), ranges(by_type[t])))
            print("  ignore-flagged, without a navmesh file ({}): {}".format(len(missing_ign), ranges([r["id"] for r in missing_ign])))
            ign_have = [r["id"] for r in have if r["ignore"]]
            if ign_have:
                print("  ignore-flagged, with a navmesh file anyway ({}): {}".format(len(ign_have), ranges(ign_have)))
            mode1 = [r["id"] for r in sd["rows"] if r["mode"]]
            if cols[COL_MODE] is not None:
                desc = []
                for sc in mode1:
                    s = by_scene.get(sc)
                    desc.append("{} ({} polygon files, {} blocks)".format(sc, s["polygon"] if s else 0, len(s["block"]) if s else 0))
                print("  NavmeshMode=1 (polygon lookup) scenes: {}".format(", ".join(desc) if desc else "none"))
            extra = sorted(with_nav - in_sd)
            extra_files = sum(1 for f in files if f not in bad_names and classify_name(f)["scene"] in set(extra))
            print("  navmesh files for scenes not in SceneData: {} files, {} scenes".format(extra_files, len(extra)))

    list_path = os.path.join(stack, MD5_LIST)
    if not os.path.isfile(list_path):
        print("server_res_version_md5_list.txt: MISSING -- the pathfindingserver loads NOTHING without it")
        return 0
    text = decode_list(read_bytes(list_path))
    entries = list_entries(text)
    listed = list_navmesh_names(entries)
    last = entries[-1][2].split(None, 1)[1] if entries else "(none)"
    print("server_res_version_md5_list.txt: present -- {} lines, {} navmesh lines, {} line endings, last file line {}".format(
        len(split_lines_keep_ends(text)), len(listed), "CRLF" if "\r\n" in text else "LF", last))
    present = set(files)
    absent = sorted(n for n in listed if n not in present)
    unlisted = sorted(n for n in present if n not in listed)
    print("  listed but absent from NavMesh ({}): {}".format(len(absent), ", ".join(absent) if absent else "none"))
    print("  present but not listed, so NOT loaded ({}): {}".format(len(unlisted), ", ".join(unlisted) if unlisted else "none"))
    if args.md5:
        mismatched = []
        for name, md5 in sorted(listed.items()):
            if name in present and md5_of_file(os.path.join(nav_dir, name)) != md5:
                mismatched.append(name)
        print("  md5 of every listed file checked: {} mismatched{}".format(
            len(mismatched), " -- " + ", ".join(mismatched) if mismatched else ""))
    else:
        print("  (--md5 compares every listed md5 with its file)")
    return 0


# ------------------------------------------------------------------------------------------------ pack

def resolve_vendor_list(spec):
    if os.path.isdir(spec):
        for cand in (os.path.join(spec, MD5_LIST), os.path.join(spec, "server_res_version_md5_list.txt")):
            if os.path.isfile(cand):
                return os.path.normpath(cand)
        raise NavmeshError("no server_res_version_md5_list.txt under {}".format(spec))
    if not os.path.isfile(spec):
        raise NavmeshError("not a file: {}".format(spec))
    return os.path.normpath(spec)


def source_files(src):
    """The *.navmesh files directly in src -- or in its NavMesh/ subfolder when src itself holds none."""
    if not os.path.isdir(src):
        raise NavmeshError("not a folder: {}".format(src))
    files = sorted(f for f in os.listdir(src) if f.lower().endswith(".navmesh"))
    nested = os.path.join(src, NAVMESH_SUBDIR)
    if not files and os.path.isdir(nested):
        src = nested
        files = sorted(f for f in os.listdir(src) if f.lower().endswith(".navmesh"))
    return src, files


def cmd_pack(args):
    if args.version not in VERSIONS:
        print("version must be one of {}".format(", ".join(VERSIONS)), file=sys.stderr)
        return 2
    try:
        src, files = source_files(args.source)
        vendor_list = resolve_vendor_list(args.vendor_list) if args.vendor_list else None
    except NavmeshError as e:
        print(str(e), file=sys.stderr)
        return 2
    out_dir = os.path.abspath(args.out)
    ver_dir = os.path.join(out_dir, args.version)
    print("Source: {} -- {}".format(src, plural(len(files), ".navmesh file")))
    if not files:
        print("nothing to pack: the folder holds no .navmesh file", file=sys.stderr)
        return 1
    vendor = {}
    if vendor_list:
        vendor = list_navmesh_names(list_entries(decode_list(read_bytes(vendor_list))))
        print("Vendor list: {} -- {} navmesh entries".format(vendor_list, len(vendor)))
    else:
        print("NOTE: no --vendor-list given: nothing is compared with the set the stack already ships, so every "
              "file goes into the bundle. A bundle should carry only what the stack lacks or what replaces one of its "
              "files -- pass the stack's server_res_version_md5_list.txt (or the stack folder).")

    kept, skipped, failed = [], [], []
    for name in files:
        ex = examine(os.path.join(src, name))
        if ex["problems"]:
            failed.append(ex)
            continue
        if name in vendor and vendor[name] == ex["md5"]:
            skipped.append(name)
            continue
        ex["status"] = "replaces the vendor file" if name in vendor else "new"
        kept.append(ex)

    rows = [("file", "scene", "kind", "tiles", "size", "status")]
    for ex in kept:
        ni = ex["nameInfo"]
        where = ni["kind"] if not ni["block"] else "{} {}".format(ni["kind"], fmt_vec(ni["block"]))
        rows.append((ex["name"], str(ni["scene"]), where, str(ex["container"]["tiles"]), fmt_mb(ex["size"]), ex["status"]))
    widths = [max(len(r[i]) for r in rows) for i in range(len(rows[0]))]
    for r in rows:
        print("  " + "  ".join(c.ljust(widths[i]) for i, c in enumerate(r)).rstrip())
    for ex in kept:
        for w in ex["warnings"]:
            print("  note: {}: {}".format(ex["name"], w))
    if skipped:
        print("  skipped, identical to the vendor's file ({}): {}".format(len(skipped), ", ".join(skipped)))
    for ex in failed:
        for p in ex["problems"]:
            print("  REFUSED: {}: {}".format(ex["name"], p))
    if failed:
        print("{} refused -- nothing written".format(plural(len(failed), "file")), file=sys.stderr)
        return 1
    if not kept:
        print("nothing to bundle: every file is identical to the vendor's -- nothing written", file=sys.stderr)
        return 1
    total = sum(ex["size"] for ex in kept)
    if total > BIG_BUNDLE:
        print("WARNING: {} in total -- a bundle carries only the files the stack does not ship, not the vendor's "
              "whole set".format(fmt_mb(total)))

    if os.path.exists(ver_dir):
        if not args.force:
            print("{} already exists -- --force replaces it (its files and navmesh.json)".format(ver_dir), file=sys.stderr)
            return 1
        shutil.rmtree(ver_dir)
    nav_out = os.path.join(ver_dir, NAVMESH_SUBDIR)
    os.makedirs(nav_out)
    for ex in kept:
        written = copy_file_durable(os.path.join(src, ex["name"]), os.path.join(nav_out, ex["name"]))
        if written != ex["md5"]:
            print("the copy of {} does not match its source (md5 {} vs {})".format(ex["name"], written, ex["md5"]), file=sys.stderr)
            return 1
    manifest = {"format": MANIFEST_FORMAT, "version": args.version,
                "files": sorted((manifest_entry(ex) for ex in kept), key=lambda e: e["name"])}
    if args.note:
        manifest["note"] = args.note
    write_json_durable(os.path.join(ver_dir, MANIFEST), manifest)
    print("Bundle {}: {}, {:,} bytes ({}) -> {}".format(args.version, plural(len(kept), "file"), total, fmt_mb(total), ver_dir))
    return 0


# ---------------------------------------------------------------------------------------------- verify

def load_manifest(ver_dir):
    path = os.path.join(ver_dir, MANIFEST)
    try:
        with open(path, encoding="utf-8") as f:
            m = json.load(f)
    except (OSError, ValueError) as e:
        raise NavmeshError("{}: {}".format(path, e))
    if not isinstance(m, dict) or not isinstance(m.get("files"), list):
        raise NavmeshError("{}: not an object with a 'files' list".format(path))
    return m


def bundle_dirs(path):
    """The version folders to check: the folder itself when it holds navmesh.json, else its children."""
    path = os.path.abspath(path)
    if os.path.isfile(os.path.join(path, MANIFEST)):
        return [path]
    if os.path.isdir(path):
        return sorted(os.path.join(path, d) for d in os.listdir(path) if os.path.isfile(os.path.join(path, d, MANIFEST)))
    return []


def folder_version(ver_dir):
    """The version a folder stands for: its own name, or its parent's when it is the published navmesh/ folder."""
    base = os.path.basename(ver_dir)
    if base in VERSIONS:
        return base
    parent = os.path.basename(os.path.dirname(ver_dir))
    if base == "navmesh" and parent in VERSIONS:
        return parent
    return None


def verify_dir(ver_dir):
    """-> list of problems (empty = ok); prints one line per file."""
    problems = []
    try:
        m = load_manifest(ver_dir)
    except NavmeshError as e:
        return [str(e)]
    if m.get("format") != MANIFEST_FORMAT:
        problems.append("format {!r}: this tool understands format {}".format(m.get("format"), MANIFEST_FORMAT))
    if m.get("version") not in VERSIONS:
        problems.append("version {!r}: not one of {}".format(m.get("version"), ", ".join(VERSIONS)))
    fv = folder_version(ver_dir)
    if fv and m.get("version") != fv:
        problems.append("navmesh.json says version {!r} but the folder is for {}".format(m.get("version"), fv))
    if not m["files"]:
        problems.append("navmesh.json lists no files")
    nav_dir = os.path.join(ver_dir, NAVMESH_SUBDIR)
    seen = set()
    keys = ("name", "size", "md5", "sha256", "scene", "kind", "block", "activity", "polygon", "sceneTagHash", "tiles")
    for e in m["files"]:
        if not isinstance(e, dict) or not isinstance(e.get("name"), str):
            problems.append("an entry without a name")
            continue
        name = e["name"]
        if name in seen:
            problems.append("{}: listed twice".format(name))
            continue
        seen.add(name)
        path = os.path.join(nav_dir, name)
        if not os.path.isfile(path):
            problems.append("{}: listed in navmesh.json but missing from NavMesh/".format(name))
            print("  {}  MISSING".format(name))
            continue
        ex = examine(path)
        mine = list(ex["problems"])
        if not mine:
            got = manifest_entry(ex)
            for k in keys:
                if k not in e:
                    mine.append("{}: missing from the entry".format(k))
                elif e[k] != got[k] and not (k in ("md5", "sha256") and str(e[k]).lower() == got[k]):
                    mine.append("{}: navmesh.json says {!r}, the file says {!r}".format(k, e[k], got[k]))
        print("  {}  {}".format(name, "ok" if not mine else "; ".join(mine)))
        problems.extend("{}: {}".format(name, p) for p in mine)
    if os.path.isdir(nav_dir):
        for f in sorted(os.listdir(nav_dir)):
            if f not in seen:
                problems.append("{}: in NavMesh/ but not in navmesh.json (the agent installs only listed files)".format(f))
    return problems


def cmd_verify(args):
    dirs = bundle_dirs(args.bundle)
    if not dirs:
        print("no navmesh.json in {} or in a folder directly under it".format(args.bundle), file=sys.stderr)
        return 2
    bad = 0
    for d in dirs:
        print("{}:".format(d))
        problems = verify_dir(d)
        for p in problems:
            print("  PROBLEM: {}".format(p))
        bad += len(problems)
    print("{} version folder(s) checked, {} problem(s)".format(len(dirs), bad))
    return 1 if bad else 0


# --------------------------------------------------------------------------------------------- md5list

def merge_md5_list(text, entries):
    """The stack's list after the agent installs the bundle -- the agent's navmesh_md5_list_merge, rule for rule.
    `entries` = [(md5, name)]. A line that already names NavMesh/<name> keeps its place and every byte after its
    md5 (its own spacing and spelling of the path), only the md5 is replaced; the names the list lacks go in
    together, in `entries` order, before the first line that names a file whose path is not under NavMesh/ -- the
    vendor list ends with ./server_res_version.txt and that line stays last (the loader reads the last line twice
    when it names a navmesh) --, else at the end. Every other line keeps its bytes and its position; a comment or a
    blank line is neither a match nor an insertion point; a new line ends the way the text does (CRLF when it holds
    CRLF, else LF); the result ends with exactly one newline (trailing whitespace-only lines go). None or "" = our
    lines alone, LF. Merging a result again changes nothing."""
    text = text or ""
    nl = "\r\n" if "\r\n" in text else "\n"
    lines = split_lines_keep_ends(text)
    while lines and not lines[-1].strip():
        lines.pop()
    want = dict((name, md5) for md5, name in entries)
    out, insert_at, replaced = [], None, set()
    for raw in lines:
        body = raw.rstrip("\r\n")
        end = raw[len(body):]
        path = list_line_path(body)
        if path is not None and path.startswith(LIST_KEY):
            name = path[len(LIST_KEY):]
            if name in want:
                body = want[name] + body[len(MD5_LINE_RE.match(body).group(1)):]
                replaced.add(name)
        elif path is not None and insert_at is None:
            insert_at = len(out)
        out.append(body + end)
    if out and not out[-1].endswith("\n"):
        out[-1] += nl
    new = ["{}  {}{}{}".format(md5, LIST_PREFIX, name, nl) for name, md5 in want.items() if name not in replaced]
    if insert_at is None:
        out.extend(new)
    else:
        out[insert_at:insert_at] = new
    return "".join(out)


def cmd_md5list(args):
    dirs = bundle_dirs(args.bundle)
    if len(dirs) != 1 or not os.path.isfile(os.path.join(dirs[0], MANIFEST)) or dirs[0] != os.path.abspath(args.bundle):
        print("the bundle must be ONE version folder holding navmesh.json (a stack has one list per version)", file=sys.stderr)
        return 2
    try:
        m = load_manifest(dirs[0])
        raw = sys.stdin.buffer.read() if args.list == "-" else read_bytes(args.list)
    except (NavmeshError, OSError) as e:
        print(str(e), file=sys.stderr)
        return 2
    entries = [(str(e["md5"]).lower(), e["name"]) for e in sorted(m["files"], key=lambda e: e["name"])]
    # The agent's bytes exactly: its reader decodes utf-8 with replacement, its writer encodes utf-8 again.
    merged = merge_md5_list(decode_list(raw), entries).encode("utf-8", "surrogateescape")
    sys.stdout.flush()
    sys.stdout.buffer.write(merged)
    sys.stdout.buffer.flush()
    return 0


# ------------------------------------------------------------------------------------------- fromunity
#
# A tile baked by Unity 2017.4 (NavMeshData.m_NavMeshTiles[].m_MeshData, tile version 16) is the server's tile
# (version 17) without its last array: the server appends `polyRegions`, one uint32 per poly, after the bvTree
# (NavMeshTileCarving.cpp PatchMeshTilePointers lays the data out as header, verts, polys, detailMeshes,
# detailVerts, detailTris, bvTree, polyRegions). The game client's own builder writes version 17 with that array
# in place and every entry unset (0xFFFFFFFF). A region id is the connected component of the walkable surface a
# poly belongs to: two polys share an id exactly when the server links them, inside the tile (neighbour entries)
# or across a tile border (NavMesh::ConnectExtLinks, transcribed below). The server refuses a path between two
# polys of one file whose ids differ before it searches, so the ids written here follow its own link rule;
# recomputed from the vendor's files the rule reproduces their partition (`regions` checks that). Ids need to
# agree only inside one file: tiles of different files are joined when they are loaded. The server ignores the
# 16-byte hash stored after every tile.

TILE_HDR = struct.Struct("<4sIiiIiiiiii3f3ff")
TILE_POLY = struct.Struct("<6H6HIBBxx")
TILE_SIDES = {0: (1, 0), 2: (0, 1), 4: (-1, 0), 6: (0, -1)}   # portal side -> the neighbouring tile
UNITY_TILE_VERSION = 16


def al4(n):
    return (n + 3) & ~3


def parse_tile(raw):
    """One tile (version 16 or 17) -> dict(version, x, y, agent, header, verts, polys, body, regions)."""
    h = TILE_HDR.unpack_from(raw, 0)
    if h[0] != TILE_MAGIC:
        raise NavmeshError("tile without the DNAV magic")
    p, v, dm, dv, dt, bv = h[5:11]
    o_p = 72 + al4(12 * v)
    o_end = o_p + al4(32 * p) + al4(12 * dm) + al4(12 * dv) + al4(8 * dt) + al4(16 * bv)
    regions = None
    if h[1] == TILE_VERSION:
        if len(raw) != o_end + al4(4 * p):
            raise NavmeshError("version {} tile of {} bytes, {} expected".format(h[1], len(raw), o_end + al4(4 * p)))
        regions = list(struct.unpack_from("<{}I".format(p), raw, o_end))
    elif h[1] == UNITY_TILE_VERSION:
        if len(raw) != o_end:
            raise NavmeshError("version {} tile of {} bytes, {} expected".format(h[1], len(raw), o_end))
    else:
        raise NavmeshError("tile version {}: only {} (Unity) and {} (the server) are understood".format(h[1], UNITY_TILE_VERSION, TILE_VERSION))
    return {"version": h[1], "x": h[2], "y": h[3], "agent": h[4], "header": h,
            "verts": [struct.unpack_from("<3f", raw, 72 + i * 12) for i in range(v)],
            "polys": [TILE_POLY.unpack_from(raw, o_p + i * 32) for i in range(p)],
            "body": raw[72:o_end], "regions": regions}


VENDOR_AREA = 5   # every poly of the vendor's files: area 5, flags 1 << 5 (a stock bake gives area 0, flags 1)


def tile_with_regions(tile, regions, area=None):
    """The server's tile: the same data with version 17 and the region ids appended; with `area`, every poly's
    area byte and flags (1 << area) set to it, the way the vendor's files carry them."""
    h = list(tile["header"])
    h[1] = TILE_VERSION
    body = bytearray(tile["body"])
    if area is not None:
        o_p = al4(12 * h[6])
        for i in range(h[5]):
            struct.pack_into("<I", body, o_p + i * 32 + 24, 1 << area)
            body[o_p + i * 32 + 29] = area
    out = TILE_HDR.pack(*h) + bytes(body) + struct.pack("<{}I".format(len(regions)), *regions)
    return out + bytes(al4(len(out)) - len(out))


def read_tiles_file(path):
    """A Unity export: int32 count, then per tile int32 size + bytes (build/unity/RelicNavMeshExport.cs)."""
    data = read_bytes(path)
    n, = struct.unpack_from("<i", data, 0)
    off, tiles = 4, []
    for i in range(n):
        if off + 4 > len(data):
            raise NavmeshError("{}: tile {} runs past the end of the file".format(path, i))
        size, = struct.unpack_from("<i", data, off)
        off += 4
        if size < TILE_HEADER or off + size > len(data):
            raise NavmeshError("{}: tile {} has {} bytes".format(path, i, size))
        tiles.append(parse_tile(data[off:off + size]))
        off += size
    return tiles


def read_container_tiles(data):
    """Every tile of a .navmesh file, parsed (the footer is parse_container's business)."""
    scene, n = struct.unpack_from("<ii", data, 0)
    off, tiles = 8, []
    for i in range(n):
        size, = struct.unpack_from("<i", data, off)
        off += 4
        tiles.append(parse_tile(data[off:off + size]))
        off += al4(size) + TILE_HASH
    return scene, tiles


# How the server links two neighbouring tiles (NavMesh::AddTile -> ConnectExtLinks, both directions for each of
# the four sides): a poly edge marked as a portal towards that side is turned into a polyline -- its two ends plus
# the poly's detail vertices that lie on it -- and linked to at most four polys of the other tile whose portal
# edges on the facing side lie on the same boundary line and overlap it in the "slab" sense of Detour: the two
# polylines, as (position along the boundary, height), share a stretch on which their heights cross or come
# within the portal height. The portal height is the surface's cell size.
LINK_EPS = 0.009999999776482582          # float32 0.01: the slab shrink, the boundary-line tolerance, the on-edge distance
LINK_T_MIN = 9.999999747378752e-05       # float32 1e-4: a detail vertex nearer to an end than this is that end
LINK_T_MAX = 0.9998999834060669
LINK_MAX_POINTS = 16
LINK_MAX_POLYS = 4
POLY_DETAIL = struct.Struct("<IIHH")     # vertBase, triBase, vertCount, triCount


def tile_detail(t):
    """(detail meshes, detail vertices) of a parsed tile, read once."""
    d = t.get("detail")
    if d is None:
        h = t["header"]
        p, v, dm, dv = h[5:9]
        body = t["body"]
        o = al4(12 * v) + al4(32 * p)
        meshes = [POLY_DETAIL.unpack_from(body, o + i * 12) for i in range(dm)]
        o += al4(12 * dm)
        d = t["detail"] = (meshes, [struct.unpack_from("<3f", body, o + i * 12) for i in range(dv)])
    return d


def _dist_sq_to_segment(p, a, b):
    dx, dy, dz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    ln = dx * dx + dy * dy + dz * dz
    t = 0.0
    if ln > 0.0:
        t = min(1.0, max(0.0, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy + (p[2] - a[2]) * dz) / ln))
    ex, ey, ez = a[0] + t * dx - p[0], a[1] + t * dy - p[1], a[2] + t * dz - p[2]
    return ex * ex + ey * ey + ez * ez


def edge_polyline(t, ip, k):
    """NavMesh::GetPolyEdgeDetailPoints: edge k of poly ip as points from its first vertex to its second, the
    detail vertices on it included, thinned to 16 by dropping the point that bends the line least. Empty when
    the edge is too short to carry detail points."""
    poly = t["polys"][ip]
    nv = poly[13]
    va = t["verts"][poly[k]]
    vb = t["verts"][poly[k + 1 if k + 1 < nv else 0]]
    meshes, dverts = tile_detail(t)
    base, count = (meshes[ip][0], meshes[ip][2]) if ip < len(meshes) else (0, 0)
    pts = [(0.0, va)]
    if count:
        dx, dz = vb[0] - va[0], vb[2] - va[2]
        len_sq = dx * dx + dz * dz
        if len_sq < LINK_T_MIN:
            return []
        for i in range(count):
            q = dverts[base + i]
            qx, qz = q[0] - va[0], q[2] - va[2]
            tt = min(1.0, max(0.0, (dx * qx + dz * qz) / len_sq))
            if tt < LINK_T_MIN or tt > LINK_T_MAX:
                continue
            ex, ez = dx * tt - qx, dz * tt - qz
            if ex * ex + ez * ez > LINK_EPS * LINK_EPS:
                continue
            pts.append((tt, q))
            if len(pts) == 63:
                break
        pts[1:] = sorted(pts[1:], key=lambda e: e[0])
    pts.append((1.0, vb))
    while len(pts) > LINK_MAX_POINTS:
        best, best_d = -1, None
        for i in range(1, len(pts) - 1):
            d = _dist_sq_to_segment(pts[i][1], pts[i - 1][1], pts[i + 1][1])
            if best_d is None or d < best_d:
                best, best_d = i, d
        del pts[best]
    return [e[1] for e in pts]


def tile_portals(t):
    """side -> [(poly, slabs, boundary coordinate, points)] for every portal edge of a parsed tile, in the
    server's order; slabs = the edge's polyline as (position along the boundary, height), ascending."""
    out = t.get("portals")
    if out is None:
        out = t["portals"] = {side: [] for side in TILE_SIDES}
        for ip, poly in enumerate(t["polys"]):
            for k in range(poly[13]):
                nei = poly[6 + k]
                if not nei & 0x8000 or (nei & 0x7fff) not in out:
                    continue
                side = nei & 0x7fff
                along, across = (2, 0) if side in (0, 4) else (0, 2)    # an x boundary runs along z, a z boundary along x
                pts = edge_polyline(t, ip, k)
                if pts and pts[-1][along] <= pts[0][along]:
                    pts = pts[::-1]
                out[side].append((ip, [(q[along], q[1]) for q in pts], t["verts"][poly[k]][across], len(pts)))
    return out


def _slabs_overlap(a0, a1, b0, b1, height):
    lo = max(a0[0], b0[0]) + LINK_EPS
    hi = min(a1[0], b1[0]) - LINK_EPS
    if lo > hi:
        return False
    ad = (a1[1] - a0[1]) / (a1[0] - a0[0])
    bd = (b1[1] - b0[1]) / (b1[0] - b0[0])
    d_lo = (b0[1] + bd * (lo - b0[0])) - (a0[1] + ad * (lo - a0[0]))
    d_hi = (b0[1] + bd * (hi - b0[0])) - (a0[1] + ad * (hi - a0[0]))
    return d_lo * d_hi < 0.0 or abs(d_lo) <= height or abs(d_hi) <= height


def linked_polys(edge, facing, height):
    """NavMesh::FindConnectingPolys: the polys of the neighbouring tile one portal edge is linked to."""
    _ip, slabs, line, n = edge
    out = []
    if n <= 1:
        return out
    done = -1
    for jp, other, other_line, m in facing:
        if jp == done or m == 0 or abs(line - other_line) > LINK_EPS:
            continue
        if any(_slabs_overlap(slabs[i], slabs[i + 1], other[j], other[j + 1], height)
               for i in range(len(slabs) - 1) for j in range(len(other) - 1)):
            if len(out) < LINK_MAX_POLYS:
                out.append(jp)
            done = jp
    return out


def compute_regions(tiles, portal_height=None):
    """Region ids for every poly of `tiles` (the parsed tiles of one scene): the connected components of the
    server's own links. `portal_height` is the surface's cell size.
    -> ({(x, y, poly index): id}, number of regions); ids are 1-based in order of first appearance."""
    if portal_height is None:
        portal_height = VENDOR_SETTINGS["cellSize"]
    parent = {}

    def find(k):
        while parent[k] != k:
            parent[k] = parent[parent[k]]
            k = parent[k]
        return k

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb

    by_pos = {}
    for t in tiles:
        by_pos[(t["x"], t["y"])] = t
        for i in range(len(t["polys"])):
            parent[(t["x"], t["y"], i)] = (t["x"], t["y"], i)
    for t in tiles:
        for i, poly in enumerate(t["polys"]):
            for k in range(poly[13]):
                nei = poly[6 + k]
                if nei and not nei & 0x8000:
                    union((t["x"], t["y"], i), (t["x"], t["y"], nei - 1))
        for side, edges in tile_portals(t).items():
            dx, dy = TILE_SIDES[side]
            other = by_pos.get((t["x"] + dx, t["y"] + dy))
            if other is None or not edges:
                continue
            facing = tile_portals(other)[(side + 4) % 8]
            if not facing:
                continue
            for edge in edges:
                for j in linked_polys(edge, facing, portal_height):
                    union((t["x"], t["y"], edge[0]), (other["x"], other["y"], j))
    groups = {}
    for k in parent:
        groups.setdefault(find(k), []).append(k)
    labels, nxt = {}, 1
    for members in sorted(groups.values(), key=min):
        for m in members:
            labels[m] = nxt
        nxt += 1
    return labels, nxt - 1


def cmd_regions(args):
    """Recompute the region ids of a vendor file and compare the partition with the ids it carries."""
    try:
        data = read_bytes(args.file)
        scene, tiles = read_container_tiles(data)
        cell = parse_container(data)["settings"]["cellSize"]
    except OSError as e:
        print("{}: {}".format(args.file, e), file=sys.stderr)
        return 2
    except (NavmeshError, struct.error, KeyError) as e:
        print("{}: {}".format(args.file, e), file=sys.stderr)
        return 1
    live = [t for t in tiles if t["polys"]]
    if any(t["regions"] is None for t in live):
        print("{}: not a version {} file, nothing to compare".format(args.file, TILE_VERSION), file=sys.stderr)
        return 1
    labels, count = compute_regions(live, portal_height=cell)
    by_vendor, by_ours = {}, {}
    for t in live:
        for i, r in enumerate(t["regions"]):
            ours = labels[(t["x"], t["y"], i)]
            by_vendor.setdefault(r, set()).add(ours)
            by_ours.setdefault(ours, set()).add(r)
    split = sum(1 for s in by_vendor.values() if len(s) > 1)
    merged = sum(1 for s in by_ours.values() if len(s) > 1)
    print("{}: scene {}, {} tiles with polys; the file's regions {}, recomputed {}; ids of the file the rule splits {}, "
          "components that mix ids of the file {}".format(args.file, scene, len(live), len(by_vendor), count, split, merged))
    return 0 if not (split or merged) else 1


def scene_tag_hash(ids):
    """The client's scene_tag_hash of a polygon: the ids of the active scene tags that belong to the polygon,
    sorted ascending, folded as h = h * 31 + id in 32 bits -- one tag is its own id, none is 0."""
    h = 0
    for i in sorted(ids):
        h = (h * 31 + i) & 0xFFFFFFFF
    return h


def cmd_taghash(args):
    if any(i < 0 for i in args.ids):
        print("scene tag ids are non-negative integers", file=sys.stderr)
        return 2
    print(scene_tag_hash(args.ids))
    return 0


def is_coord(c):
    """A JSON number that is a usable coordinate (an integer too large for a float is none)."""
    return isinstance(c, (int, float)) and not isinstance(c, bool) and -1e9 < c < 1e9


def is_point(v):
    return isinstance(v, list) and len(v) >= 3 and all(is_coord(c) for c in v[:3])


def unit_sidecar(export, stem):
    """<stem>.json of an in-game unit: where its box was and where the player stood. A unit without it cannot be
    placed among the others -- its rim tiles, cut by the box, would pass for the real world."""
    path = os.path.join(export, stem + ".json")
    try:
        with open(path, encoding="utf-8") as f:
            side = json.load(f)
    except OSError:
        raise NavmeshError("unit {}: no readable {}.json beside it (the box and the player's place)".format(stem, stem))
    except ValueError:
        raise NavmeshError("unit {}: {}.json is not JSON".format(stem, stem))

    def vec(name):
        v = side.get(name) if isinstance(side, dict) else None
        if not (isinstance(v, list) and len(v) == 3 and all(is_coord(c) for c in v)):
            raise NavmeshError("unit {}: {}.json carries no usable \"{}\" ([x, y, z])".format(stem, stem, name))
        return [float(c) for c in v]
    return side, vec("center"), vec("extents"), vec("player") if side.get("player") is not None else vec("center")


UNIT_SURFACE_GAP = 8.0      # m2: two bakes of one tile that differ by this much -- and by this share of the
UNIT_SURFACE_SHARE = 0.02   # richer one -- differ by an object, not by how an edge was rounded
UNIT_CELLS = 64             # two bakes are compared from above in cells: this many per tile side (0.25 m)
UNIT_LEVEL = 1.0            # m: heights of one cell closer than this are one surface
UNIT_RIM_SHARE = 0.9        # a patch fills a hole when this share of its rim is the nearest bake's own ground


def tile_surface(t):
    """The walkable surface of a parsed tile in m2, seen from above (a deck over the ground counts twice)."""
    total = 0.0
    verts = t["verts"]
    for poly in t["polys"]:
        a = verts[poly[0]]
        for i in range(1, poly[13] - 1):
            b, c = verts[poly[i]], verts[poly[i + 1]]
            total += abs((b[0] - a[0]) * (c[2] - a[2]) - (c[0] - a[0]) * (b[2] - a[2])) / 2
    return total


def tile_cover(t, edge, n=UNIT_CELLS):
    """(i, j) -> [heights]: the cells of a parsed tile's square whose centre a poly covers, seen from above."""
    x0, z0 = t["x"] * edge, t["y"] * edge
    cell = edge / n
    grid = {}
    verts = t["verts"]
    for poly in t["polys"]:
        a = verts[poly[0]]
        for i in range(1, poly[13] - 1):
            b, c = verts[poly[i]], verts[poly[i + 1]]
            den = (b[2] - c[2]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[2] - c[2])
            if abs(den) < 1e-9:
                continue
            for ci in range(max(0, int((min(a[0], b[0], c[0]) - x0) / cell)), min(n - 1, int((max(a[0], b[0], c[0]) - x0) / cell)) + 1):
                px = x0 + (ci + 0.5) * cell
                for cj in range(max(0, int((min(a[2], b[2], c[2]) - z0) / cell)), min(n - 1, int((max(a[2], b[2], c[2]) - z0) / cell)) + 1):
                    pz = z0 + (cj + 0.5) * cell
                    u = ((b[2] - c[2]) * (px - c[0]) + (c[0] - b[0]) * (pz - c[2])) / den
                    v = ((c[2] - a[2]) * (px - c[0]) + (a[0] - c[0]) * (pz - c[2])) / den
                    if u >= 0.0 and v >= 0.0 and u + v <= 1.0:
                        grid.setdefault((ci, cj), []).append(u * a[1] + v * b[1] + (1.0 - u - v) * c[1])
    return grid


def unit_cover(tiles, key, edge, n=UNIT_CELLS):
    """One unit (position -> parsed tile) seen from above over the 3 x 3 tiles around `key`: ((i, j) -> [heights],
    the cells of the tile at `key` being 0..n-1 on both axes; the offsets of the positions the unit did not bake)."""
    grid, missing = {}, set()
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            t = tiles.get((key[0] + dx, key[1] + dy))
            if t is None:
                missing.add((dx, dy))
                continue
            for (i, j), hs in tile_cover(t, edge, n).items():
                grid[(i + dx * n, j + dy * n)] = hs
    return grid, missing


def filled_holes(near, other, key, edge, n=UNIT_CELLS):
    """m2 of the tile at `key` that `other` has more than `near` (each: position -> parsed tile of one unit) only
    because it lacks an object: surface of `other` without a counterpart at its height in `near`, in a patch whose
    rim is `near`'s own ground at that height -- the hole an object standing in `near`'s bake cuts into ground both
    hold. A patch is followed into the tiles around; one that leaves them, or reaches a position one of the units
    did not bake, is not such a hole: a hole is small, what goes on is surface `near` lacks."""
    gn, miss_n = unit_cover(near, key, edge, n)
    go, miss_o = unit_cover(other, key, edge, n)
    missing = miss_n | miss_o
    extra = {}      # cell -> the heights at which only `other` has surface, one per level
    for k, hs in go.items():
        mine = gn.get(k, ())
        only = []
        for h in hs:
            if all(abs(h - g) > UNIT_LEVEL for g in mine) and all(abs(h - o) > UNIT_LEVEL for o in only):
                only.append(h)
        if only:
            extra[k] = only
    seen = set()
    cells = 0
    for (i0, j0), hs0 in extra.items():
        if not (0 <= i0 < n and 0 <= j0 < n):
            continue
        for h0 in hs0:
            if (i0, j0, h0) in seen:
                continue
            seen.add((i0, j0, h0))
            todo = [(i0, j0, h0)]
            inside = rim = ground = 0
            hole = True
            while todo:
                i, j, h = todo.pop()
                inside += 0 <= i < n and 0 <= j < n
                for di, dj in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)):
                    k = (i + di, j + dj)
                    if not (-n <= k[0] < 2 * n and -n <= k[1] < 2 * n) or (k[0] // n, k[1] // n) in missing:
                        hole = False
                        continue
                    joined = False
                    for h2 in extra.get(k, ()):
                        if abs(h2 - h) <= UNIT_LEVEL:
                            joined = True
                            if (k[0], k[1], h2) not in seen:
                                seen.add((k[0], k[1], h2))
                                todo.append((k[0], k[1], h2))
                    if not joined:
                        rim += 1
                        ground += any(abs(g - h) <= UNIT_LEVEL for g in gn.get(k, ()))
            if hole and rim and ground >= UNIT_RIM_SHARE * rim:
                cells += inside
    return cells * (edge / n) ** 2


def merge_units(named, export, margin, scene=None):
    """The tiles of the units an in-game bake wrote (<stem>.tiles, with <stem>.json naming the box's center and
    extents and where the player stood). A tile is kept only when its cell lies at least `margin` metres inside
    the unit's box -- the builder cuts the geometry at the box, so the tiles at its rim are not the real world --
    and of two units covering the same tile position the one baked nearest to it wins: the geometry around the
    player is the geometry the game had loaded. One exception: where another unit baked a whole object's worth
    of walkable surface more on that tile -- a deck over the ground, ground where the nearest bake has none -- its
    bake is taken: a platform the server had not shown to the nearest unit yet, and a deck that is missing strands
    whatever stands on it. Surface that only fills holes in the nearest bake's ground does not count: there an
    object stands in the nearest bake (a watchtower, a wall) that the other unit's bake lacks. Units baked in
    another scene than `scene` are refused.
    -> (tiles in the vendor's order, [(stem, read, kept)], positions more than one unit holds, notes)"""
    edge = VENDOR_SETTINGS["tileSize"] * VENDOR_SETTINGS["cellSize"]
    best = {}
    whole = {}      # position -> [(walkable m2, stem, tile)] of every unit that holds the tile inside its margin
    seen = {}       # every position any unit baked, cut by its box or not -> the largest surface baked there (m2)
    stats = []
    unsettled, foreign, astray = [], [], []
    for order, (stem, path) in enumerate(sorted(named.items())):
        tiles = read_tiles_file(path)
        side, center, extents, player = unit_sidecar(export, stem)
        if side.get("stable") is False:
            unsettled.append(stem)
        if scene is not None and isinstance(side.get("scene"), int) and side["scene"] != scene:
            foreign.append("{} (scene {})".format(stem, side["scene"]))
        if abs(player[0] - center[0]) > extents[0] or abs(player[2] - center[2]) > extents[2]:
            astray.append(stem)
        box = (center[0] - extents[0] + margin, center[0] + extents[0] - margin,
               center[2] - extents[2] + margin, center[2] + extents[2] - margin)
        kept = 0
        for t in tiles:
            key = (t["x"], t["y"])
            surface = tile_surface(t)
            seen[key] = max(surface, seen.get(key, 0.0))
            x0, z0 = t["x"] * edge, t["y"] * edge
            x1, z1 = x0 + edge, z0 + edge
            if not (box[0] <= x0 and x1 <= box[1] and box[2] <= z0 and z1 <= box[3]):
                continue
            kept += 1
            whole.setdefault(key, []).append((surface, stem, t))
            d = math.hypot((x0 + x1) / 2 - player[0], (z0 + z1) / 2 - player[2])
            if key in best and d >= best[key][0]:
                continue
            best[key] = (d, order, t, stem)
        stats.append((stem, len(tiles), kept))
    if foreign:
        raise NavmeshError("{} baked in another scene than {}: {}".format(
            plural(len(foreign), "unit"), scene, ", ".join(foreign[:12]) + (" ..." if len(foreign) > 12 else "")))
    contested = sum(1 for v in whole.values() if len(v) > 1)
    notes = []
    if astray:
        notes.append("{} baked while the player stood outside its box: {}".format(
            plural(len(astray), "unit"), ", ".join(astray[:12]) + (" ..." if len(astray) > 12 else "")))
    if unsettled:
        notes.append("{} taken before the scene had settled (\"stable\": false): {}".format(
            plural(len(unsettled), "unit"), ", ".join(unsettled[:12]) + (" ..." if len(unsettled) > 12 else "")))
    # a position some unit baked that ends without a tile between two kept ones: a strip no unit's margin reached
    lost = set(seen) - set(best)
    strip = 2 * (int(margin // edge) + 2)     # each of two facing units drops its rim tiles and the tile its box cuts

    def between(k, dx, dy):
        """k lies in a run of positions without a tile along one axis that a kept tile ends on both sides: at most
        `strip` of them baked -- what two units leave whose kept zones face each other without meeting -- and at
        most two that no unit baked (boxes standing that far apart)."""
        baked, bare = 1, 0
        for way in (-1, 1):
            p = (k[0] + way * dx, k[1] + way * dy)
            while p not in best and baked <= strip and bare <= 2:
                if p in seen:
                    baked += 1
                else:
                    bare += 1
                p = (p[0] + way * dx, p[1] + way * dy)
            if p not in best:
                return False
        return baked <= strip and bare <= 2

    gaps = sorted((k for k in lost if between(k, 1, 0) or between(k, 0, 1)), key=lambda k: (-seen[k], k))
    if gaps:
        notes.append("{} baked but kept from no unit, between kept tiles (the zones the units keep do not meet there); "
                     "{:.0f} m2 of baked surface lie on them: {}".format(
                         plural(len(gaps), "tile position"), sum(seen[k] for k in gaps),
                         ", ".join("({}, {}) {:.0f} m2".format(k[0], k[1], seen[k]) for k in gaps[:12]) + (" ..." if len(gaps) > 12 else "")))
    # The exception to "the nearest wins". The surface decides, not the poly count: the same flat ground comes out
    # as one poly or as ten. Both units are named: which of the two bakes misses something is for whoever reads the
    # note to judge from where the tile lies.
    richer, standing = [], []
    loaded = {}

    def unit_tiles(stem):
        """Every tile of a unit by position, the ones its box cuts too (read again: few units are ever asked for)."""
        if stem not in loaded:
            if len(loaded) >= 6:
                del loaded[next(iter(loaded))]
            loaded[stem] = {(u["x"], u["y"]): u for u in read_tiles_file(named[stem])}
        return loaded[stem]

    for key, (d, order, t, stem) in sorted(best.items()):
        mine = next(a for a, st, _t in whole[key] if st == stem)
        most, other, tile = max(whole[key], key=lambda e: e[0])
        need = max(UNIT_SURFACE_GAP, UNIT_SURFACE_SHARE * most)
        if most - mine >= need:
            filled = filled_holes(unit_tiles(stem), unit_tiles(other), key, edge)
            if most - mine - filled < need:
                standing.append("({}, {}) {:.0f} m2 in {}, {:.0f} m2 in {}".format(key[0], key[1], mine, stem, most, other))
                continue
            best[key] = (d, order, tile, other)
            richer.append("({}, {}) {:.0f} m2 from {}, {:.0f} m2 in {}".format(key[0], key[1], mine, stem, most, other))
    if richer:
        notes.append("{} taken from a unit that baked more surface than the nearest one ({:g} m2 or more, ground that "
                     "only fills holes of the nearest bake not counted): {}".format(
                         plural(len(richer), "tile"), UNIT_SURFACE_GAP, "; ".join(richer[:8]) + (" ..." if len(richer) > 8 else "")))
    if standing:
        notes.append("{} kept from the nearest unit although another baked more ground: what it has more only fills holes "
                     "in the nearest bake's ground (an object stands there that the other bake lacks): {}".format(
                         plural(len(standing), "tile"), "; ".join(standing[:8]) + (" ..." if len(standing) > 8 else "")))
    return [v[2] for _, v in sorted(best.items(), key=lambda kv: (kv[0][1], kv[0][0]))], stats, contested, notes


def sweep_gaps(export, named):
    """The stations of every <prefix>sweep.json in the folder (navbake_driver.py sweep writes it) that have no
    unit: [(stem, why)]."""
    gaps = []
    for f in sorted(os.listdir(export)):
        if not f.endswith("sweep.json") or f[:-5] in named:      # <stem>.json beside <stem>.tiles is a unit's sidecar
            continue
        try:
            with open(os.path.join(export, f), encoding="utf-8") as fh:
                sweep = json.load(fh)
            prefix, stations = sweep["prefix"], sweep["stations"]
        except (OSError, ValueError, KeyError, TypeError) as e:
            raise NavmeshError("{}: not a sweep list ({})".format(f, e))
        if not (isinstance(prefix, str) and isinstance(stations, list) and all(is_point(st) for st in stations)):
            raise NavmeshError("{}: not a sweep list (\"prefix\" is a string, \"stations\" a list of [x, y, z])".format(f))
        for i, st in enumerate(stations):
            stem = "{}{:03d}".format(prefix, i)
            if stem not in named:
                gaps.append((stem, "no unit"))
                continue
            try:
                with open(os.path.join(export, stem + ".json"), encoding="utf-8") as fh:
                    got = json.load(fh).get("station")
            except (OSError, ValueError, AttributeError):
                got = None
            if not (is_point(got) and all(abs(float(got[k]) - float(st[k])) < 0.01 for k in range(3))):
                gaps.append((stem, "its unit is of another station"))
    return gaps


def cmd_fromunity(args):
    polygon = args.polygon
    if (polygon is None) != (args.tag is None):
        print("--polygon and --tag go together", file=sys.stderr)
        return 2
    if polygon is not None and not (0 <= polygon < 2 ** 32 and 0 <= args.tag < 2 ** 32):
        print("--polygon and --tag are unsigned 32-bit integers", file=sys.stderr)
        return 2
    if not 0 <= args.scene < 2 ** 31 or not -1 <= args.area <= 31:
        print("the scene id is a non-negative 32-bit integer, --area is 0..31 (-1 keeps the bake's)", file=sys.stderr)
        return 2
    if not (math.isfinite(args.edge_margin) and args.edge_margin >= 0):
        print("--edge-margin is a distance in metres, 0 or more", file=sys.stderr)
        return 2
    export = os.path.abspath(args.export)
    if not os.path.isdir(export):
        print("not a folder: {}".format(export), file=sys.stderr)
        return 2
    meta = {}
    meta_path = os.path.join(export, "export.json")
    if os.path.isfile(meta_path):
        try:
            with open(meta_path, encoding="utf-8") as f:
                meta = json.load(f)
        except (OSError, ValueError) as e:
            print("export.json: {}".format(e), file=sys.stderr)
            return 2
        if not isinstance(meta, dict):
            print("export.json: not a JSON object", file=sys.stderr)
            return 2
        if meta.get("ok") is False:
            print("export.json reports a failed export: {}".format(meta.get("error")), file=sys.stderr)
            return 1
    blocks = {}
    single = None
    named = {}
    for f in sorted(os.listdir(export)):
        m = re.match(r"^block_(-?[0-9]+)_(-?[0-9]+)\.tiles\Z", f)
        if m:
            blocks[(int(m.group(1)), int(m.group(2)))] = os.path.join(export, f)
            continue
        if f == "scene.tiles":
            single = os.path.join(export, f)
            continue
        m = re.match(r"^([A-Za-z0-9_-]+)\.tiles\Z", f)
        if m:
            named[m.group(1)] = os.path.join(export, f)
    kinds = [k for k, v in (("block_<bx>_<by>.tiles", blocks), ("scene.tiles", single), ("in-game units", named)) if v]
    if not kinds:
        print("{} holds no block_<bx>_<by>.tiles, no scene.tiles and no in-game unit".format(export), file=sys.stderr)
        return 1
    if len(kinds) > 1:
        print("{} mixes {}: one export is one of them".format(export, " and ".join(kinds)), file=sys.stderr)
        return 1
    if args.blocks and (polygon is not None or not named):
        print("--blocks splits in-game units into block files; it goes without --polygon", file=sys.stderr)
        return 2
    if named:
        try:
            gaps = sweep_gaps(export, named)
            if gaps and not args.partial:
                print("{} of the sweep in {} without a unit ({}): bake them, or --partial builds the file from what is "
                      "there".format(plural(len(gaps), "station"), export,
                                     ", ".join("{} {}".format(*g) for g in gaps[:10]) + (" ..." if len(gaps) > 10 else "")), file=sys.stderr)
                return 1
            merged, stats, contested, notes = merge_units(named, export, args.edge_margin, args.scene)
        except (NavmeshError, struct.error, OSError, ValueError) as e:
            print(str(e), file=sys.stderr)
            return 1
        for stem, n, kept in stats:
            print("  {}: {} tiles, {} inside the box".format(stem, n, kept))
        if not any(t["polys"] for t in merged):
            print("no tile with geometry lies {:g} m inside the box of any of the {}: nothing to write".format(
                args.edge_margin, plural(len(stats), "unit")), file=sys.stderr)
            return 1
        print("{} units -> {} tile positions ({} covered by more than one unit: the nearest bake kept)".format(
            len(stats), len(merged), contested))
        if gaps:
            print("WARNING: built without {} of the sweep".format(plural(len(gaps), "station")))
        for note in notes:
            print("WARNING: " + note)
        if args.blocks:
            per = tiles_per_block(VENDOR_SETTINGS)
            parsed = {}
            for t in merged:
                parsed.setdefault((t["x"] // per, t["y"] // per), []).append(t)
        else:
            parsed = {None: merged}
        units = [(key, None) for key in parsed]
    else:
        units = sorted(blocks.items()) if blocks else [(None, single)]
        try:
            parsed = {key: read_tiles_file(path) for key, path in units}
        except (NavmeshError, struct.error, OSError) as e:
            print(str(e), file=sys.stderr)
            return 1
    if polygon is not None and not named:
        # A polygon-mode file is ONE file for the whole polygon however the bake was split: every unit's tiles
        # merge. Of two tiles at the same grid position (bakes of overlapping areas) the later unit's is kept --
        # the tile grid is global, so a tile baked from the same loaded geometry is the same whatever the bounds.
        merged, where = [], {}
        for key, _path in units:
            for t in parsed[key]:
                pos = (t["x"], t["y"])
                if pos in where:
                    merged[where[pos]] = t
                else:
                    where[pos] = len(merged)
                    merged.append(t)
        dup = sum(len(tiles) for tiles in parsed.values()) - len(merged)
        if dup:
            print("note: {} tiles at a grid position a later unit also covers were replaced by that unit's".format(dup))
        parsed = {None: merged}
    every = [t for tiles in parsed.values() for t in tiles]
    foreign = [t for t in every if t["agent"] != 0]
    if foreign and not args.any_agent:
        print("{} tiles carry agentTypeId {} -- the server's files use the default agent type 0 (bake with "
              "NavMesh.GetSettingsByID(0)); --any-agent keeps them".format(len(foreign), foreign[0]["agent"]), file=sys.stderr)
        return 1
    live = [t for t in every if t["polys"]]
    labels, count = compute_regions(live)
    out_dir = os.path.abspath(args.out)
    os.makedirs(out_dir, exist_ok=True)
    per = tiles_per_block(VENDOR_SETTINGS)
    written = []
    block_meta = {(b.get("x"), b.get("y")): b for b in meta.get("blocks", []) if isinstance(b, dict)}
    hollow = []
    for key, tiles in parsed.items():
        outside = 0
        if key is not None and named and not any(t["polys"] for t in tiles):
            hollow.append(key)      # a block the units only brushed: tiles without a poly are no file
            continue
        if key is not None:
            rx, ry = block_tile_range(key, per)
            outside = sum(1 for t in tiles if not (rx[0] <= t["x"] <= rx[1] and ry[0] <= t["y"] <= ry[1]))
            name = "scene_{}_{}_{}.navmesh".format(args.scene, key[0], key[1])
            center = [key[0] * BLOCK_METRES + BLOCK_METRES / 2, 500.0, key[1] * BLOCK_METRES + BLOCK_METRES / 2]
            extents = [BLOCK_METRES / 2 + 0.25, 1500.0, BLOCK_METRES / 2 + 0.25]
        else:
            name = ("scenepolygon_{}_{}_{}.navmesh".format(args.scene, polygon, args.tag) if polygon is not None
                    else "scene_{}.navmesh".format(args.scene))
            center, extents = [0.0, 0.0, 0.0], [0.0, 0.0, 0.0]
            solid = [t for t in tiles if t["polys"]]      # an empty tile has no bounds of its own
            if solid:
                lo = [min(t["header"][11 + i] for t in solid) for i in range(3)]
                hi = [max(t["header"][14 + i] for t in solid) for i in range(3)]
                center = [(lo[i] + hi[i]) / 2 for i in range(3)]
                extents = [(hi[i] - lo[i]) / 2 for i in range(3)]
        bm = block_meta.get(key)
        if bm and isinstance(bm.get("center"), list) and isinstance(bm.get("extents"), list):
            center, extents = [float(v) for v in bm["center"]], [float(v) for v in bm["extents"]]
        body = bytearray(struct.pack("<ii", args.scene, len(tiles)))
        for t in tiles:
            regions = [labels[(t["x"], t["y"], i)] for i in range(len(t["polys"]))]
            blob = tile_with_regions(t, regions, None if args.area < 0 else args.area)
            body += struct.pack("<i", len(blob)) + blob
            body += bytes(al4(len(body)) - len(body))
            body += hashlib.md5(blob).digest()
        settings = [VENDOR_SETTINGS[k] for k in SETTINGS_NAMES]
        body += struct.pack(SETTINGS_FMT, *settings)
        body += struct.pack(TAIL_FMT, 0, 0, *center, *extents, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0)
        path = os.path.join(out_dir, name)
        tmp = path + ".tmp"
        with open(tmp, "wb") as f:
            f.write(body)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, path)
        ex = examine(path, bytes(body))
        written.append((name, len(tiles), sum(1 for t in tiles if t["polys"]), len(body), outside, ex))
    print("Scene {}: {} tiles in {} ({} with polys), {} regions".format(
        args.scene, sum(w[1] for w in written), plural(len(written), "file"), len(live), count))
    for key in hollow:
        print("  block ({}, {}): only tiles without geometry, no file".format(*key))
    for name, n, live_n, size, outside, ex in written:
        line = "  {}  {} tiles ({} with polys)  {}".format(name, n, live_n, fmt_mb(size))
        if outside:
            line += "  WARNING: {} tiles outside the block's tile range".format(outside)
        for w in ex["warnings"]:
            line += "  note: " + w
        for p in ex["problems"]:
            line += "  PROBLEM: " + p
        print(line)
    print("-> {} (pack them with: pack <ver> {} --vendor-list <stack dir>)".format(out_dir, out_dir))
    return 1 if any(ex["problems"] for *_, ex in written) else 0


# ------------------------------------------------------------------------------------------------ main

def main(argv=None):
    ap = argparse.ArgumentParser(prog="navmesh_tool.py",
                                 description="Inspect, inventory, pack and verify the navmesh files of the GIO pathfindingserver "
                                             "(docs/NAVMESH.md). A bundle holds only the files that add to or replace the "
                                             "vendor's set, never the whole set.")
    sub = ap.add_subparsers(dest="cmd", metavar="command")
    sub.required = True

    p = sub.add_parser("inspect", help="parse one .navmesh file: scene, tiles, grid, settings, bounds, checks")
    p.add_argument("file")
    p.add_argument("--json", action="store_true", help="machine-readable output")
    p.set_defaults(fn=cmd_inspect)

    p = sub.add_parser("inventory", help="what a stack ships: NavMesh folder by scene, SceneData.txt scenes without a file, the md5 list")
    p.add_argument("stack", help="the stack folder (holds server/res/NavMesh)")
    p.add_argument("--md5", action="store_true", help="also compare every listed md5 with its file (reads the whole set)")
    p.set_defaults(fn=cmd_inventory)

    p = sub.add_parser("pack", help="build <out>/<ver>/navmesh.json + NavMesh/ from a folder of .navmesh files")
    p.add_argument("version", help="1.6 or 2.8")
    p.add_argument("source", help="a folder of .navmesh files (or one holding a NavMesh/ subfolder)")
    p.add_argument("--out", default=DEFAULT_OUT, help="bundle root (default: <repo>/_bundled_navmesh)")
    p.add_argument("--vendor-list", help="the stack's server_res_version_md5_list.txt, or the stack folder: files identical "
                                         "to a listed one are left out, a same-name file with another md5 replaces it")
    p.add_argument("--note", help="free text stored in navmesh.json")
    p.add_argument("--force", action="store_true", help="replace an existing <out>/<ver> folder")
    p.set_defaults(fn=cmd_pack)

    p = sub.add_parser("verify", help="re-check every navmesh.json entry (size, md5, sha256, container) of a bundle")
    p.add_argument("bundle", help="a version folder with navmesh.json, or the folder holding the version folders")
    p.set_defaults(fn=cmd_verify)

    p = sub.add_parser("md5list", help="print the stack's md5 list as it reads after the bundle is installed (byte for byte "
                                        "what the agent writes)")
    p.add_argument("list", help="the stack's current server_res_version_md5_list.txt, or - for stdin (empty = the list the "
                                "agent creates for a stack that has none)")
    p.add_argument("bundle", help="the version folder with navmesh.json")
    p.set_defaults(fn=cmd_md5list)

    p = sub.add_parser("fromunity", help="turn a Unity 2017.4 bake (build/unity/RelicNavMeshExport.cs: block_<bx>_<by>.tiles "
                                          "or scene.tiles + export.json) into the server's scene_<id>[_<bx>_<by>].navmesh files, "
                                          "or with --polygon/--tag into one scenepolygon_<id>_<polygon>_<tag>.navmesh")
    p.add_argument("scene", type=int, help="the scene id the files are for")
    p.add_argument("export", help="the export folder")
    p.add_argument("--out", required=True, help="where the .navmesh files go (a folder for `pack`)")
    p.add_argument("--any-agent", action="store_true", help="accept tiles baked for an agent type other than 0")
    p.add_argument("--polygon", type=int, help="a polygon-mode scene: write ONE scenepolygon file for this polygon id "
                                               "(every unit's tiles merged); goes with --tag")
    p.add_argument("--tag", type=int, help="the scene tag hash of that polygon file (`taghash` computes it from the tag ids)")
    p.add_argument("--area", type=int, default=VENDOR_AREA,
                   help="the area every poly gets (and flags 1 << area), as in the vendor's files (default {}); -1 keeps the bake's".format(VENDOR_AREA))
    p.add_argument("--blocks", action="store_true", help="in-game units: split the merged tiles into scene_<id>_<bx>_<by> block files")
    p.add_argument("--edge-margin", type=float, default=15.0,
                   help="in-game units: drop the tiles that reach within this many metres of a unit's box (default 15: the "
                        "outer ring of 16 m tiles, with a metre to spare for a box that is not on the tile grid)")
    p.add_argument("--partial", action="store_true",
                   help="in-game units: build the file although stations of the folder's <prefix>sweep.json have no unit")
    p.set_defaults(fn=cmd_fromunity)

    p = sub.add_parser("taghash", help="the scene_tag_hash the client sends for a polygon whose active scene tags are these ids")
    p.add_argument("ids", type=int, nargs="+", help="scene tag ids (SceneTagData.txt), any order")
    p.set_defaults(fn=cmd_taghash)

    p = sub.add_parser("regions", help="recompute the region ids of a vendor .navmesh file and compare them with the file's")
    p.add_argument("file")
    p.set_defaults(fn=cmd_regions)

    args = ap.parse_args(argv)
    try:
        # Diagnostics go to stderr; with stdout flushed at every line they keep their place on a pipe too. A
        # console whose code page lacks a character (a column name of SceneData.txt, a file name) gets it escaped
        # instead of a crash.
        sys.stdout.reconfigure(line_buffering=True, errors="backslashreplace")
        sys.stderr.reconfigure(errors="backslashreplace")
    except (AttributeError, ValueError):
        pass
    try:
        return args.fn(args)
    except OSError as e:
        print("cannot read or write: {}".format(e), file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
