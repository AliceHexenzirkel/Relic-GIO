"""The Spiral Abyss section of the catalogue and the monsters a slot may hold.

Chain: TowerFloorData (关卡组ID) -> TowerLevelData (地城ID, 怪物等级) -> DungeonData (场景ID) ->
lua/scene/<scene>/scene<scene>_group<gid>.lua. Every half of a chamber spawns from ONE group script, whose
`monsters = { ... }` table holds one spawn slot per line. Which slots a half really spawns, in which order and
how often comes from a symbolic run of the scene (towersim); the run is trusted only because it reproduces the
kill count the script itself passes to ActiveChallenge -- a half where it does not stops the build.
"""
import collections
import re

from . import luagroup
from . import towersim
from .stack import CatalogError, Stack, to_int

# One monster entry, exactly as every Abyss group script writes it. The agent replaces the monster of a slot
# by rewriting this one line, so a listed script whose entries are written any other way stops the build.
ENTRY = re.compile(
    rb"^(?P<ind>[ \t]*)\{ config_id = (?P<cfg>\d+), monster_id = (?P<mid>\d+), "
    rb"pos = \{ x = [-0-9.]+, y = [-0-9.]+, z = [-0-9.]+ \}, "
    rb"rot = \{ x = [-0-9.]+, y = [-0-9.]+, z = [-0-9.]+ \}, "
    rb"level = (?P<lvl>\d+)"
    rb"(?P<wander>, disableWander = true)?"
    rb"(?:, affix = \{ (?P<affix>\d+(?:, \d+)*) \})?"
    rb"(?P<elite>, isElite = true)?"
    rb"(?:, pose_id = (?P<pose>\d+))?"
    rb" \}(?P<comma>,?)(?P<eol>\r?)$")
_MONSTERS_OPEN = re.compile(rb"(?m)^monsters = \{\r?\n")
_BLOCK_CLOSE = re.compile(rb"(?m)^\}")

TOWER_DUNGEON_TYPE = "6"

# The looser reading used for the scenes outside the Abyss, whose scripts are not all written alike.
_ANY_GROUP_FILE = re.compile(r"^lua/scene/(\d+)/[^/]*_group[^/]*\.lua$")
_LOOSE_MONSTERS = re.compile(r"(?m)^monsters\s*=\s*\{(.*?)^\}", re.S)
_LOOSE_GADGETS = re.compile(r"(?m)^gadgets\s*=\s*\{(.*?)^\}", re.S)
_LOOSE_ENTRY = re.compile(r"\{\s*config_id\s*=\s*(\d+)\s*,\s*monster_id\s*=\s*(\d+)\s*,([^\n]*)")
_LOOSE_GADGET = re.compile(r"\{\s*config_id\s*=\s*\d+\s*,\s*gadget_id\s*=\s*(\d+)")
_LOOSE_AFFIX = re.compile(r"affix\s*=\s*\{([^}]*)\}")
_LOOSE_POSE = re.compile(r"pose_id\s*=\s*(\d+)")
_LONG_COMMENT = re.compile(r"--\[(=*)\[.*?\]\1\]", re.S)
_LINE_COMMENT = re.compile(r"--[^\n]*")

MONSTER_ORDINARY = "1"
MONSTER_BOSS = "2"
GENERAL_SKILL_PREFIX = "GeneralSkill_"
GENERIC_AFFIX_MIN_FAMILIES = 3
# A chamber's enemy level cell (TowerLevelData 怪物等级) may be set to 1..199; a slot's monster gets that
# plus the level its own script entry carries, so it needs a growth curve for every level up to 200.
LEVEL_CELLS = range(1, 200)
SLOT_LEVEL = 1
MONSTER_LEVELS = range(1 + SLOT_LEVEL, LEVEL_CELLS[-1] + SLOT_LEVEL + 1)
GROWTH_CURVES = ("[属性成长]1曲线", "[属性成长]2曲线", "[属性成长]3曲线")
# A monster's combat config (MonsterData 战斗Config) is json/monster/<name>.json. Its "initialPoses" object names
# the poses the monster may be spawned in, {pose name: {"initialPoseID": id, ...}}; "Default" is the one it
# takes when nothing else is asked for. An entry of a group script without a pose_id asks for pose 0.
MONSTER_CONFIGS = "json/monster/"
POSE_DEFAULT = "Default"
NO_POSE = 0


def parse_slots(raw, where):
    """The monster entries of one group script, read with the strict pattern:
    [{"cfg", "monster", "affix": [ids], "elite": bool, "pose": id or None}], in file order."""
    m = _MONSTERS_OPEN.search(raw)
    if not m:
        raise CatalogError("%s: no monsters table" % where)
    end = _BLOCK_CLOSE.search(raw, m.end())
    if not end:
        raise CatalogError("%s: the monsters table is not closed" % where)
    slots = []
    seen = set()
    for line in raw[m.end():end.start()].split(b"\n"):
        text = line.strip()
        if not text or text.startswith(b"--"):
            continue
        e = ENTRY.match(line)
        if not e:
            raise CatalogError("%s: a monster entry is not written the way the slot editor expects: %s"
                               % (where, text[:100].decode("utf-8", "replace")))
        cfg = int(e.group("cfg"))
        if cfg in seen:
            raise CatalogError("%s: config_id %d appears twice in the monsters table" % (where, cfg))
        if int(e.group("lvl")) != SLOT_LEVEL:
            raise CatalogError("%s: slot %d carries the level %s, the Abyss slots carry %d"
                               % (where, cfg, e.group("lvl").decode(), SLOT_LEVEL))
        seen.add(cfg)
        slots.append({"cfg": cfg, "monster": int(e.group("mid")),
                      "affix": [int(x) for x in e.group("affix").split(b", ")] if e.group("affix") else [],
                      "elite": bool(e.group("elite")),
                      "pose": int(e.group("pose")) if e.group("pose") else None})
    return slots


def _same_as_reader(slots, table):
    """The strict line reading and the script reader must see the same slots: the run uses the reader's."""
    if len(slots) != len(table):
        return False
    for s, t in zip(slots, table):
        if not isinstance(t, dict):
            return False
        if (s["cfg"], s["monster"]) != (t.get("config_id"), t.get("monster_id")):
            return False
        if s["affix"] != list(t.get("affix", [])) or s["elite"] != bool(t.get("isElite")):
            return False
        if s["pose"] != t.get("pose_id"):
            return False
    return True


def _scene(stack, scene_id):
    """{group id: (data-relative file, parsed script, strict slots)} of one Abyss scene, by ascending id."""
    prefix = "lua/scene/%d/" % scene_id
    name = re.compile(r"^scene%d_group(\d+)\.lua$" % scene_id)
    found = {}
    for rel in stack.names(prefix):
        m = name.match(rel[len(prefix):])
        if m:
            found[int(m.group(1))] = rel
    if not found:
        raise CatalogError("%s: no group script" % Stack.path(prefix))
    out = collections.OrderedDict()
    for gid in sorted(found):
        rel = found[gid]
        raw = stack.read(rel)
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError as e:
            raise CatalogError("%s: not UTF-8 (%s)" % (Stack.path(rel), e))
        parsed = luagroup.parse_text(text)
        skipped = luagroup.unread(parsed)
        if skipped:
            raise CatalogError("%s:%s: the script reader cannot read: %s"
                               % (Stack.path(rel), skipped[0][0], skipped[0][1][:100]))
        slots = parse_slots(raw, Stack.path(rel))
        if not _same_as_reader(slots, parsed["tables"].get("monsters", [])):
            raise CatalogError("%s: the monsters table reads differently line by line and as a script"
                               % Stack.path(rel))
        out[gid] = (rel, parsed, slots)
    return out


def _halves(stack, level_id, scene_id, teams, challenge_types, files, report):
    """The halves of one chamber + every slot its scene defines (with "group" and "spawned"); the group
    scripts the halves name are added to `files`."""
    scene = _scene(stack, scene_id)
    where = "chamber %d (scene %d)" % (level_id, scene_id)
    sim = towersim.Sim(collections.OrderedDict((gid, v[1]) for gid, v in scene.items()), challenge_types)
    played = sim.run()
    if sim.notes:
        raise CatalogError("%s: the scripts cannot be replayed: %s" % (where, "; ".join(sim.notes)))
    if played != teams:
        raise CatalogError("%s: %d halves can be started, its floor has %d teams" % (where, played, teams))
    stray = [s for s in sim.spawns if s["half"] == 0]
    if stray:
        raise CatalogError("%s: slot %d of group %d spawns before a half is started"
                           % (where, stray[0]["config_id"], stray[0]["group"]))
    halves = []
    spawned = set()
    for half in range(1, played + 1):
        tag = "%s half %d" % (where, half)
        challenges = [c for c in sim.challenges.values() if c["half"] == half]
        if len(challenges) != 1 or "count" not in challenges[0]:
            raise CatalogError("%s: %d kill challenges are started, expected one" % (tag, len(challenges)))
        ch = challenges[0]
        kills = len([k for k in sim.kills if k["half"] == half and k["group"] == ch["group"]])
        if not ch["done"] or kills != ch["count"]:
            raise CatalogError("%s: the replay kills %d monsters of group %s, the script's own challenge "
                               "counts %s" % (tag, kills, ch["group"], ch["count"]))
        # waves in the order of their first spawn; inside a wave the slots in spawn order
        waves = collections.OrderedDict()
        for s in sim.spawns:
            if s["half"] != half:
                continue
            c = s["cause"]
            key = (c["kind"], s["group"], c.get("tide_id"), c.get("suite"), c["trigger"],
                   c["line"] if c["kind"] != "create" else None)
            wave = waves.setdefault(key, collections.OrderedDict())
            wave[s["config_id"]] = wave.get(s["config_id"], 0) + 1
        groups = sorted(set(key[1] for key in waves))
        if len(groups) != 1 or groups[0] != ch["group"]:
            raise CatalogError("%s: monsters spawn from groups %s, the challenge counts group %s"
                               % (tag, groups, ch["group"]))
        gid = groups[0]
        rel, _parsed, slots = scene[gid]
        monster_of = dict((s["cfg"], s["monster"]) for s in slots)
        half_slots = {}
        times = collections.Counter()
        for wave in waves.values():
            for cfg, n in wave.items():
                if cfg not in monster_of:
                    raise CatalogError("%s: slot %s spawns but %s does not define it"
                                       % (tag, cfg, Stack.path(rel)))
                half_slots[str(cfg)] = monster_of[cfg]
                times[cfg] += 1
                spawned.add((gid, cfg))
        if sum(sum(w.values()) for w in waves.values()) != ch["count"]:
            raise CatalogError("%s: the waves do not add up to the %s kills of the challenge"
                               % (tag, ch["count"]))
        halves.append({"group": gid, "file": Stack.path(rel), "slots": half_slots,
                       "waves": [[[cfg, n] for cfg, n in w.items()] for w in waves.values()]})
        files.add(rel)
        for cfg in sorted(c for c, n in times.items() if n > 1):
            report["slotsInTwoWaves"].append("chamber %d half %d: slot %d of group %d"
                                             % (level_id, half, cfg, gid))
        for f in sim.tide_failures:
            if f["half"] == half:
                report["tideNamesUndefinedSlot"].append(
                    "chamber %d half %d: tide %s of group %d names slot %d"
                    % (level_id, half, f["tide_id"], f["group"], f["config_id"]))
    by_group = collections.Counter(h["group"] for h in halves)
    for gid in sorted(g for g, n in by_group.items() if n > 1):
        report["halvesSharingOneGroup"].append("chamber %d: group %d" % (level_id, gid))
    every = []
    for gid, (rel, _parsed, slots) in scene.items():
        for s in slots:
            every.append(dict(s, group=gid, spawned=(gid, s["cfg"]) in spawned))
            if (gid, s["cfg"]) not in spawned:
                report["slotsNeverSpawned"].append("chamber %d: slot %d of group %d (monster %d)"
                                                   % (level_id, s["cfg"], gid, s["monster"]))
    return halves, every


def build(stack):
    """-> (the "abyss" section, the data-relative group scripts it names, every slot of every Abyss scene,
    the set of Abyss scene ids, report lists)."""
    levels = stack.table("TowerLevelData")
    floors = stack.table("TowerFloorData")
    dungeons = stack.table("DungeonData")
    challenges = stack.table("DungeonChallengeData")
    if not stack.has("txt/TowerScheduleData.txt"):
        raise CatalogError("%s: not in the vendor's md5 list" % Stack.path("txt/TowerScheduleData.txt"))
    challenge_types = dict((r[challenges.col("ID")].strip(), r[challenges.col("ChallengeType")].strip())
                           for _, r in challenges.rows)
    dungeon_row = dict((r[dungeons.col("ID")].strip(), r) for _, r in dungeons.rows)
    report = collections.OrderedDict((k, []) for k in ("halvesSharingOneGroup", "slotsInTwoWaves",
                                                       "slotsNeverSpawned", "tideNamesUndefinedSlot"))

    floor_of_group = {}
    out_floors = {}
    for line, r in floors.rows:
        where = "%s:%d" % (floors.rel, line)
        fid = to_int(r[floors.col("层ID")], where)
        group = to_int(r[floors.col("关卡组ID")], where)
        if group in floor_of_group or str(fid) in out_floors:
            raise CatalogError("%s: floor %d or its level group %d appears twice" % (where, fid, group))
        floor_of_group[group] = fid
        out_floors[str(fid)] = {"number": to_int(r[floors.col("层")], where),
                                "teams": to_int(r[floors.col("编队数量")], where), "chambers": []}

    out_chambers = {}
    files = set()
    every_slot = []
    scenes = set()
    order = collections.defaultdict(list)
    for line, r in levels.rows:
        where = "%s:%d" % (levels.rel, line)
        lid = to_int(r[levels.col("关卡ID")], where)
        group = to_int(r[levels.col("组ID")], where)
        index = to_int(r[levels.col("组内序号")], where)
        if str(lid) in out_chambers:
            raise CatalogError("%s: level %d appears twice" % (where, lid))
        if group not in floor_of_group:
            raise CatalogError("%s: no floor uses level group %d" % (where, group))
        fid = floor_of_group[group]
        level = to_int(r[levels.col("怪物等级")], where)
        if level not in LEVEL_CELLS:
            raise CatalogError("%s: the enemy level %d is outside %d..%d"
                               % (where, level, LEVEL_CELLS[0], LEVEL_CELLS[-1]))
        d = dungeon_row.get(r[levels.col("地城ID")].strip())
        if d is None or d[dungeons.col("类型")] != TOWER_DUNGEON_TYPE:
            raise CatalogError("%s: dungeon %s is not a tower dungeon" % (where, r[levels.col("地城ID")]))
        scene_id = to_int(d[dungeons.col("场景ID")], where)
        if scene_id in scenes:
            raise CatalogError("%s: scene %d already belongs to another chamber" % (where, scene_id))
        scenes.add(scene_id)
        halves, slots = _halves(stack, lid, scene_id, out_floors[str(fid)]["teams"], challenge_types, files,
                                report)
        for h in halves:
            if not h["slots"]:
                raise CatalogError("%s: a half spawns no slot" % where)
        every_slot.extend(dict(s, scene=scene_id) for s in slots)
        out_chambers[str(lid)] = {"floor": fid, "index": index, "level": level, "halves": halves}
        order[fid].append((index, lid))
    for fid, lst in order.items():
        if len(set(i for i, _ in lst)) != len(lst):
            raise CatalogError("%s: floor %d has two chambers with one index" % (levels.rel, fid))
        out_floors[str(fid)]["chambers"] = [lid for _, lid in sorted(lst)]
    for fid, f in out_floors.items():
        if not f["chambers"]:
            raise CatalogError("%s: floor %s has no chamber" % (floors.rel, fid))

    # a tweak names a slot by (group id, config id): one group id must mean one script
    script_of = {}
    for ch in out_chambers.values():
        for h in ch["halves"]:
            if script_of.setdefault(h["group"], h["file"]) != h["file"]:
                raise CatalogError("group %d is defined by two scripts: %s and %s"
                                   % (h["group"], script_of[h["group"]], h["file"]))
    section = {"levelFile": Stack.path("txt/TowerLevelData.txt"),
               "scheduleFile": Stack.path("txt/TowerScheduleData.txt"),
               "floors": out_floors, "chambers": out_chambers}
    return section, sorted(files), every_slot, scenes, report


def _scan_other_scenes(stack, abyss_scenes):
    """What the scripts of every scene outside the Abyss place: per monster id the number of group scripts
    that place it, the gadget ids present in ALL of them and how often each pose is asked for it ({pose id:
    entries}, 0 = an entry without a pose_id), and per affix id the monsters that carry it."""
    groups = collections.Counter()
    common_gadgets = {}
    affix_monsters = collections.defaultdict(set)
    poses = collections.defaultdict(collections.Counter)
    for rel in stack.names("lua/scene/"):
        m = _ANY_GROUP_FILE.match(rel)
        if not m or int(m.group(1)) in abyss_scenes:
            continue
        text = stack.read(rel).decode("utf-8", "replace")
        if "monster_id" not in text:
            continue
        text = _LINE_COMMENT.sub("", _LONG_COMMENT.sub("", text))
        block = _LOOSE_MONSTERS.search(text)
        if not block:
            continue
        entries = _LOOSE_ENTRY.findall(block.group(1))
        if not entries:
            continue
        gblock = _LOOSE_GADGETS.search(text)
        gadgets = frozenset(int(x) for x in _LOOSE_GADGET.findall(gblock.group(1))) if gblock else frozenset()
        here = set()
        for _cfg, mid, rest in entries:
            mid = int(mid)
            here.add(mid)
            aff = _LOOSE_AFFIX.search(rest)
            if aff:
                for a in re.findall(r"\d+", aff.group(1)):
                    affix_monsters[int(a)].add(mid)
            pose = _LOOSE_POSE.search(rest)
            poses[mid][int(pose.group(1)) if pose else NO_POSE] += 1
        for mid in here:
            groups[mid] += 1
            common_gadgets[mid] = gadgets if mid not in common_gadgets else common_gadgets[mid] & gadgets
    return groups, common_gadgets, affix_monsters, poses


def _initial_poses(stack, config):
    """{pose name: initialPoseID} of the combat config json/monster/<config>.json: the poses its monsters
    may be spawned in. Empty for a config that names none."""
    rel = "%s%s.json" % (MONSTER_CONFIGS, config)
    poses = stack.json(rel).get("initialPoses", {})
    if not isinstance(poses, dict) or not all(
            isinstance(pose, dict) and isinstance(pose.get("initialPoseID"), int)
            and not isinstance(pose["initialPoseID"], bool) and pose["initialPoseID"] >= 0
            for pose in poses.values()):
        raise CatalogError("%s: initialPoses is not a set of named poses with an initialPoseID each"
                           % Stack.path(rel))
    return dict((name, pose["initialPoseID"]) for name, pose in poses.items())


def _pose(poses, slots, spawned):
    """The pose a spawn slot gets with one monster -> (True, pose id -- None for no pose_id at all) or
    (False, None) when no pose the server accepts can be established.

    poses = {pose name: initialPoseID} of the monster's combat config (None / empty: the server knows no
    pose of it and accepts no entry), slots = the monster's vendor Abyss slots, spawned = {pose id: entries}
    of how the vendor's scene scripts spawn the monsters of that combat config. In this order:
      1. the pose every one of its Abyss slots carries, when they all carry one and the same;
      2. no pose, when 0 is one of the config's poses;
      3. the config's "Default" pose -- taken only when the vendor's own scripts spawn a monster of that
         config in that pose somewhere.
    Only a pose that is one of the config's counts."""
    if not poses:
        return False, None
    allowed = set(poses.values())
    carried = set(s["pose"] for s in slots)
    if len(carried) == 1 and None not in carried and carried <= allowed:
        return True, carried.pop()
    if NO_POSE in allowed:
        return True, None
    default = poses.get(POSE_DEFAULT)
    if default is None or not spawned.get(default):
        return False, None
    return True, default


def _curves_at_every_level(stack):
    """The growth-curve ids MonsterCurveData defines on every level a slot can reach. The server refuses a
    slot whose monster has no curve at the slot's level."""
    t = stack.table("MonsterCurveData")
    columns = [i for i, name in enumerate(t.header) if re.match(r"^\[曲线\]\d+类型$", name)]
    levels = {}
    for line, r in t.rows:
        levels[to_int(r[t.col("等级")], "%s:%d" % (t.rel, line))] = set(r[i] for i in columns if r[i] != "")
    missing = [n for n in MONSTER_LEVELS if n not in levels]
    if missing:
        raise CatalogError("%s: no row for level %d" % (t.rel, missing[0]))
    return set.intersection(*(levels[n] for n in MONSTER_LEVELS))


def build_monsters(stack, every_slot, abyss_scenes):
    """-> (the "monsters" section, the "genericAffixes" list, report dict).

    tier 1 = a vendor Abyss script places it. tier 2 = an ordinary monster another scene script places, with a
    combat config on the server, no server script, growth curves for every level, and not one that every
    script placing it surrounds with the same gadgets (a sign that it depends on them). It must also be a
    fighter: the huntable animals (boars, birds, cats ...) are "ordinary" rows as well, and one that never
    engages or runs off would leave a half without its last kill. What tells them apart is 战斗音乐 (battle
    music): set on every monster the vendor's Abyss uses, empty on the animals that do not fight.

    An affix carried by an Abyss slot is GENERIC -- kept on a slot whose monster is replaced -- when it is
    one of the game's general skills (ability name GeneralSkill_*), or when the scripts of this version put
    it on monsters of at least three monster families (family = the first four digits of the monster id).
    Every other affix belongs to the monster it sits on: it is listed with a monster when all of that
    monster's Abyss slots carry it, and dropped from a slot whose monster changes.

    A monster comes with the POSE a slot must carry for it. The 2.8 gameserver compares the pose_id of every
    monster entry of a group script -- 0 for an entry without one -- with the initialPoseID values of the
    monster's json combat config and refuses the script over a pose that is not one of them (the 1.6 one has
    no such check, but its data has the same shape, so both versions follow the one rule: _pose). A monster
    for which no pose can be established that way is left out; for a tier 1 monster that stops the build,
    an Abyss slot holds it. The section ends with its proof: the pose the agent will write for a monster
    (its `pose`, else 0) is one of that monster's initialPoseID values."""
    table = stack.table("MonsterData")
    c_id, c_type, c_music = table.col("ID"), table.col("类型"), table.col("战斗音乐")
    c_script, c_config = table.col("服务器脚本"), table.col("战斗Config")
    rows = collections.OrderedDict()
    for line, r in table.rows:
        mid = to_int(r[c_id], "%s:%d" % (table.rel, line))
        if mid in rows:
            raise CatalogError("%s:%d: monster %d is defined twice" % (table.rel, line, mid))
        rows[mid] = r
    affixes = stack.table("MonsterAffixData")
    ability = dict((to_int(r[affixes.col("ID")], "%s:%d" % (affixes.rel, line)), r[affixes.col("AbilityName")])
                   for line, r in affixes.rows)
    configs = set()
    for rel in stack.names(MONSTER_CONFIGS):
        name = rel[len(MONSTER_CONFIGS):]
        if "/" not in name and name.endswith(".json"):
            configs.add(name[:-5])

    other_groups, common_gadgets, affix_monsters, placed = _scan_other_scenes(stack, abyss_scenes)
    tower = collections.OrderedDict()
    for s in every_slot:
        tower.setdefault(s["monster"], []).append(s)
        placed[s["monster"]][s["pose"] or NO_POSE] += 1
        for a in s["affix"]:
            affix_monsters[a].add(s["monster"])
    # a pose belongs to the combat config: how the vendor's scripts spawn the monsters that share one
    spawned = collections.defaultdict(collections.Counter)
    for mid, counts in placed.items():
        if mid in rows:
            spawned[rows[mid][c_config]].update(counts)
    curves = _curves_at_every_level(stack)
    growth = [table.col(name) for name in GROWTH_CURVES]

    def grows(r):
        return all(r[i] in curves for i in growth)

    read = {}

    def poses_of(mid):
        """{pose name: initialPoseID} of a monster's combat config; None when the server has no such config."""
        config = rows[mid][c_config]
        if config not in read:
            read[config] = _initial_poses(stack, config) if config in configs else None
        return read[config]

    for mid in tower:
        if mid not in rows:
            raise CatalogError("%s: monster %d of an Abyss slot is not listed" % (table.rel, mid))
        if not grows(rows[mid]):
            raise CatalogError("%s: monster %d of an Abyss slot has no growth curve for every level"
                               % (table.rel, mid))

    used = sorted(set(a for s in every_slot for a in s["affix"]))
    generic = []
    for a in used:
        families = set(mid // 10000 for mid in affix_monsters[a])
        if (ability.get(a, "").startswith(GENERAL_SKILL_PREFIX)
                or len(families) >= GENERIC_AFFIX_MIN_FAMILIES):
            generic.append(a)

    monsters = {}
    report = {"tier1": 0, "tier2": 0, "bosses": 0, "withPose": 0, "withAffix": 0, "noPose": 0,
              "specificAffixes": [a for a in used if a not in generic]}
    for mid, r in rows.items():
        entry = None
        slots = tower.get(mid, [])
        if slots:
            entry = {"tier": 1, "boss": 1 if r[c_type] == MONSTER_BOSS else 0}
            own = [a for a in used if a not in generic and all(a in s["affix"] for s in slots)]
            if own:
                entry["affix"] = own
        elif (r[c_type] == MONSTER_ORDINARY and r[c_music].strip() and other_groups.get(mid, 0) > 0
              and r[c_config] in configs and not r[c_script].strip() and grows(r)
              and not (other_groups[mid] >= 2 and common_gadgets[mid])):
            entry = {"tier": 2, "boss": 0}
        if entry is None:
            continue
        known, pose = _pose(poses_of(mid), slots, spawned[r[c_config]])
        if not known:
            if slots:
                raise CatalogError("%s: no pose the server accepts can be established for monster %d of an "
                                   "Abyss slot (combat config %s)" % (table.rel, mid, r[c_config]))
            report["noPose"] += 1
            continue
        if pose is not None:
            entry["pose"] = pose
        monsters[str(mid)] = entry
        report["tier%d" % entry["tier"]] += 1
        report["bosses"] += entry["boss"]
        report["withPose"] += 1 if "pose" in entry else 0
        report["withAffix"] += 1 if "affix" in entry else 0
    # the proof: whatever pose the agent writes for a listed monster is one the server accepts for it
    for mid, entry in monsters.items():
        allowed = sorted(set((poses_of(int(mid)) or {}).values()))
        if entry.get("pose", NO_POSE) not in allowed:
            raise CatalogError("%s: monster %s would be written with the pose %d, the poses of its combat "
                               "config are %s" % (table.rel, mid, entry.get("pose", NO_POSE), allowed))
    return monsters, generic, report
