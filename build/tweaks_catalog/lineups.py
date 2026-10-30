"""The line-ups of the statue domains: the monster of every spawn slot a domain's fight uses.

Chain: DungeonData (the row of a listed domain: 场景ID) -> lua/scene/<scene>/scene<scene>.lua (`blocks`) ->
scene<scene>_block<block>.lua (`groups`) -> scene<scene>_group<gid>.lua. The block decides which groups a scene
loads; a scene folder may hold group scripts no block lists, so the folder is never listed. A statue scene
loads ONE group with monsters -- its line-up -- and belongs to one domain or to the weekday variants of one
difficulty, which share the line-up and differ in their reward.

What makes a slot safe to edit is proven for every scene, and a scene where it does not hold stops the build:
the scripts name spawn slots, groups, counts and variables, never a monster, and the monster entries are
written the way the slot editor rewrites them (_scene, _rows, _challenges).

The waves are another matter. They come from a symbolic run of the scene (towersim), and a run is a proof of
the order only for a scene that stays inside what the run models AND whose replayed kills are the count the
script itself gives its challenge. The count alone proves nothing: a script that respawns slots until a
variable changes reaches it on a path the server never takes. A scene outside that is listed FLAT -- every
slot its scripts name, no waves -- with the reason in the report.

The enemy level of a domain rests on its line-up too. The game adds ONE number -- the 等级修正 of the
dungeon's row -- to the level of everything a scene places with one: the monsters of the line-up and the
gadgets of every group the scene loads, the monolith a challenge guards among them. Their growth curves are
held level by level up to a last one, and an entry put past the end of its curves stands in the scene
without health. So a line-up carries `top`, the largest level any of those entries is written with, and its
domains take an enemy level only as far as that entry stays on the levels the curves are proven for
(level_range). What `top` rests on is proven for every scene as well: only the monsters and the gadgets
table of a group script hold entries with a level, and each of their entries has one (_levelled); nothing
else speaks of a level in the scripts that are read for a scene -- the scene script, its block script, the
group scripts the block lists and the modules they require (_only_levels) --, none of them spelling a
text with an escape the script reader would take for another text (_spelled_out); every monster, and
every gadget that names growth curves, has them on every level an enemy level can put it at (_stunted).
"""
import collections
import re

from . import abyss
from . import luagroup
from . import towersim
from .stack import CatalogError, Stack, to_int

SCENES = "lua/scene/"
MODULES = "lua/common/"
# The kinds of challenge a statue domain runs (DungeonChallengeData ChallengeType): 2 kill count in time,
# 4 time fly, 5 kill count fast, 9 guard, 10 kill count while guarding. Their arguments are a group, a count,
# seconds, a gadget -- never a monster.
CHALLENGE_TYPES = ("2", "4", "5", "9", "10")
# The ScriptLib functions that start one: (context, index, challenge id, ...).
CHALLENGE_CALLS = ("ActiveChallenge", "StartChallenge")
# The DungeonPassData condition type of a statue domain: its parameter is the index of a challenge of the
# scene. Other types name a monster or a group the dungeon is passed by.
PASS_FINISH_CHALLENGE = "14"
PASS_TYPE = re.compile(r"^\[条件\]\d+类型$")
# The events that reach only the group of the monster they are about (towersim): a trigger on one of them
# in a group that defines no monster never fires.
OWN_MONSTER_EVENTS = ("EVENT_ANY_MONSTER_LIVE", "EVENT_ANY_MONSTER_DIE")
# What a required module may call beside the functions the run counts as neutral and the group timers
# (towersim.NEUTRAL_CALLS, TIMER_CALLS). A module's code is not replayed, and it adds triggers of its own
# to the group that requires it, so whatever it calls must be unable to reach a monster, a suite, a
# challenge or a group variable in any order: the temp values of a group (no trigger function the run
# accepts reads one), time axes (they lead to EVENT_TIME_AXIS_PASS alone, an event the run does not fire),
# and reads of a gadget, the players and the clock.
MODULE_CALLS = (
    "ChangeGroupTempValue", "GetGroupTempValue", "SetGroupTempValue",
    "InitTimeAxis", "EndTimeAxis",
    "GetGadgetStateByConfigId", "GetSceneUidList", "IsPlayerAllAvatarDie", "GetServerTime",
)
# The tables of a group script whose entries stand in a scene at a level, each with what an entry of it
# places: a monster, a gadget (an object of the scene) -- named by the field <that>_id. The enemy level cell
# of the dungeon row is added to the level of every one of them.
LEVELLED = collections.OrderedDict((("monsters", "monster"), ("gadgets", "gadget")))
LEVEL = "level"
# A field of such an entry as the script reader gives it when the script writes the field's name, bare or as
# a text in brackets. A name the reader leaves a field of its own (the line of the entry, its values
# without a name) has the same shape.
FIELD_NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
# The names that hold the word and say nothing about the level an entry stands at: the vision_level of an
# entry and its value, VisionLevelType.VISION_LEVEL_<...>.
VISION_NAMES = re.compile(r"vision_level|VisionLevelType|VISION_LEVEL_\w+")
# The escapes of a quoted text that the script reader reads as Lua does: a quote, a backslash.
READ_ESCAPE = re.compile(r"\\[\"'\\]")
# The table that names the growth curves of what a table of LEVELLED places, the cells that name them and
# the table that holds a curve level by level. GadgetPropData names the growth curves of the gadgets that
# have any; a gadget without a row names none, so there is no curve to hold it to -- its level counts for
# the `top` of its scene all the same.
GROWTH = {
    "monsters": ("MonsterData", abyss.GROWTH_CURVES, abyss.MONSTER_CURVES),
    "gadgets": ("GadgetPropData", ("血量成长曲线", "攻击成长曲线", "防御成长曲线"), "GadgetCurveData"),
}

# The report lists of build(): the scenes listed without waves, each with the reason; the slots a script
# defines and its line-up does not list; the slots two waves spawn; the slots whose own level is not the
# usual one; the slots that carry a vision_level; the scenes in which an entry -- a monster or a gadget --
# carries a level above the usual one, with the enemy level their domains end at.
REPORT = ("flatScenes", "slotsNotListed", "wavesSharingOneSlot", "slotLevels", "visionLevels", "topLevels")


def _script(stack, rel):
    """(bytes, text, parsed script) of one vendor-listed script the reader takes in completely: what its
    code spells with a backslash reads as it does to Lua (_spelled_out), and every top-level statement of
    it is read, whatever it gives a table or a local (luagroup.unread)."""
    raw = stack.read(rel)
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as e:
        raise CatalogError("%s: not UTF-8 (%s)" % (Stack.path(rel), e))
    _spelled_out(text, Stack.path(rel))
    parsed = luagroup.parse_text(text)
    skipped = luagroup.unread(parsed)
    if skipped:
        raise CatalogError("%s:%s: the script reader cannot read: %s"
                           % (Stack.path(rel), skipped[0][0], skipped[0][1][:100]))
    return raw, text, parsed


def _code(text):
    return [t for t in luagroup.tokenize(text) if t.kind != "comment"]


def _is_op(tok, val):
    return tok is not None and tok.kind == "op" and tok.val == val


def _spelled_out(text, where):
    """Whatever a script's code spells with a backslash is to the script reader what it is to Lua. The
    reader drops the backslash of an escape in a quoted text and keeps the character behind it -- right for
    an escaped quote or backslash (READ_ESCAPE) and for nothing else: spelled with any other escape, a text
    is another one to the reader than to Lua, and the name of a field, of a module or of a function could
    hide behind it. A text in long brackets holds a backslash as itself either way."""
    for t in _code(text):
        written = text[t.pos:t.end]
        if "\\" not in written or (t.kind == "string" and written.startswith("[")):
            continue
        if t.kind != "string" or "\\" in READ_ESCAPE.sub("", written):
            raise CatalogError("%s:%d: the script writes a backslash where the script reader does not read "
                               "it as Lua does" % (where, t.line))


def _integers(text, without=None):
    """The whole numbers a script's code writes, in any spelling the script reader takes (decimal,
    hexadecimal, a float without a fraction). `without` names a top-level table whose content is left out."""
    toks = _code(text)
    found = set()
    depth = 0
    i = 0
    while i < len(toks):
        t = toks[i]
        if (without and depth == 0 and t.kind == "name" and t.val == without and i + 2 < len(toks)
                and _is_op(toks[i + 1], "=") and _is_op(toks[i + 2], "{")):
            inner = 0
            i += 2
            while i < len(toks):
                if _is_op(toks[i], "{"):
                    inner += 1
                elif _is_op(toks[i], "}"):
                    inner -= 1
                    if inner == 0:
                        break
                i += 1
        elif _is_op(t, "{"):
            depth += 1
        elif _is_op(t, "}"):
            depth -= 1
        elif t.kind == "number":
            value = luagroup.number(t.val)
            if isinstance(value, int) or value.is_integer():
                found.add(int(value))
        i += 1
    return found


def _only_levels(text, where, carried=0):
    """Nothing in a script gives a level but the entries that carry one: its code writes the name `level`
    -- the name, or a string of that text, which names a field just as well -- once for each of the
    `carried` entries the script was read with (none for a script that is no group script) and not once
    more, and it holds the word in no other name. A function that set or asked a level would be such a
    name; the names of an entry's vision_level are none (VISION_NAMES). What is held is what the code
    writes: a name a function puts together while it runs is not seen."""
    written = 0
    for t in _code(text):
        if t.kind in ("name", "string") and t.val == LEVEL:
            written += 1
        elif t.kind == "name" and LEVEL in t.val.lower() and not VISION_NAMES.fullmatch(t.val):
            raise CatalogError("%s:%d: the script speaks of a level by the name %s" % (where, t.line, t.val))
    if written != carried:
        raise CatalogError("%s: the script writes the name %s %s, %s"
                           % (where, LEVEL, "once" if written == 1 else "%d times" % written,
                              "the entries of its %s tables carry %d" % (" and ".join(LEVELLED), carried)
                              if carried else "and it places nothing that carries one"))


def _requires(text, where):
    """The modules a script pulls in: the names of its `require` statements."""
    toks = _code(text)
    names = []
    for i, t in enumerate(toks):
        if t.kind != "name" or t.val != "require":
            continue
        arg = toks[i + 1] if i + 1 < len(toks) else None
        if _is_op(arg, "("):
            arg = toks[i + 2] if i + 2 < len(toks) else None
        if arg is None or arg.kind != "string":
            raise CatalogError("%s:%d: a require that does not name its module" % (where, t.line))
        names.append(arg.val)
    return names


def _modules(stack, text, where, seen):
    """The modules a script requires, and theirs: each must be a vendor-listed module that spells nothing the
    script reader would misread (_spelled_out) and speaks neither of monsters nor of a level -- what a
    line-up spawns, and at which level anything of the scene stands, is then decided by the scene's own
    scripts alone. -> the ScriptLib functions the modules call; "ScriptLib" itself stands for a use that
    names none (an alias, an index)."""
    called = set()
    for name in _requires(text, where):
        rel = "%s%s.lua" % (MODULES, name)
        if rel in seen:
            continue
        seen.add(rel)
        if not stack.has(rel):
            raise CatalogError("%s: requires %s, which is not a module of the vendor's list" % (where, name))
        raw = stack.read(rel)
        if b"monster" in raw.lower():
            raise CatalogError("%s: requires %s, and %s speaks of monsters" % (where, name, Stack.path(rel)))
        try:
            module = raw.decode("utf-8")
        except UnicodeDecodeError as e:
            raise CatalogError("%s: not UTF-8 (%s)" % (Stack.path(rel), e))
        _spelled_out(module, Stack.path(rel))
        _only_levels(module, Stack.path(rel))
        toks = _code(module)
        for i, t in enumerate(toks):
            if t.kind == "name" and t.val == "ScriptLib":
                member = toks[i + 2] if i + 2 < len(toks) and _is_op(toks[i + 1], ".") else None
                called.add(member.val if member is not None and member.kind == "name" else t.val)
        called |= _modules(stack, module, Stack.path(rel), seen)
    return called


def _rows(stack, domain_list):
    """{scene id: [dungeon id]} of the listed domains, in table order. A row must be what the line-up and
    the enemy level rest on: no dynamic difficulty (the row's own 等级修正 is the one number the game adds to
    the level of every entry of the scene, a monster's and a gadget's alike -- which is why a line-up has a
    `top`), passed by finishing a challenge and nothing else (no condition names a monster), shown with the
    level 等级修正 + 1 -- the level the list of the domains gives it, and the level a slot of
    abyss.SLOT_LEVEL stands at --, an 等级修正 of abyss.LEVEL_CELLS, and one 等级修正 for all rows of a
    scene. No other dungeon uses the scene."""
    t = stack.table("DungeonData")
    c_id, c_scene, c_dynamic, c_pass = t.col("ID"), t.col("场景ID"), t.col("动态难度"), t.col("通关条件")
    c_shown, c_revise = t.col("显示等级"), t.col("等级修正")
    passes = stack.table("DungeonPassData")
    pass_types = [i for i, name in enumerate(passes.header) if PASS_TYPE.match(name)]
    pass_row = dict((r[passes.col("ID")].strip(), r) for _, r in passes.rows)
    scenes = collections.OrderedDict()
    revise_of = {}
    for line, r in t.rows:
        where = "%s:%d" % (t.rel, line)
        dungeon = r[c_id].strip()
        if dungeon not in domain_list:
            continue
        scene = to_int(r[c_scene], where)
        if r[c_dynamic].strip() != "":
            raise CatalogError("%s: dungeon %s has a dynamic difficulty" % (where, dungeon))
        how = pass_row.get(r[c_pass].strip())
        kinds = [how[i] for i in pass_types if how[i] != ""] if how else []
        if not kinds or any(k != PASS_FINISH_CHALLENGE for k in kinds):
            raise CatalogError("%s: dungeon %s is not passed by finishing a challenge alone (pass condition "
                               "%s)" % (where, dungeon, r[c_pass]))
        shown, revise = to_int(r[c_shown], where), to_int(r[c_revise], where)
        if shown != revise + 1:
            raise CatalogError("%s: dungeon %s is shown with level %d, its enemy level cell holds %d"
                               % (where, dungeon, shown, revise))
        if shown != domain_list[dungeon]["level"]:
            raise CatalogError("%s: dungeon %s is shown with level %d, the list of the domains gives it %s"
                               % (where, dungeon, shown, domain_list[dungeon]["level"]))
        if revise not in abyss.LEVEL_CELLS:
            raise CatalogError("%s: the enemy level cell %d of dungeon %s is outside %d..%d"
                               % (where, revise, dungeon, abyss.LEVEL_CELLS[0], abyss.LEVEL_CELLS[-1]))
        if revise_of.setdefault(scene, revise) != revise:
            raise CatalogError("%s: scene %d serves dungeons with the enemy level cells %d and %d"
                               % (where, scene, revise_of[scene], revise))
        scenes.setdefault(scene, []).append(int(dungeon))
    found = set(str(d) for dungeons in scenes.values() for d in dungeons)
    if len(found) != sum(len(dungeons) for dungeons in scenes.values()) or found != set(domain_list):
        raise CatalogError("%s: the listed domains are not one row each: %s"
                           % (t.rel, sorted(set(domain_list) ^ found, key=int)[:5]))
    for line, r in t.rows:
        if r[c_id].strip() not in domain_list and r[c_scene].strip().isdigit() and int(r[c_scene]) in scenes:
            raise CatalogError("%s:%d: dungeon %s uses scene %s, the scene of a statue domain"
                               % (t.rel, line, r[c_id], r[c_scene]))
    return scenes


def level_range(top):
    """(lowest, highest) enemy level a domain takes whose line-up has this `top`. The enemy level of a domain
    is the level it is shown with: its enemy level cell + 1, the level a slot of abyss.SLOT_LEVEL stands at.
    The cell is one of abyss.LEVEL_CELLS, and the entry that carries `top` stands at cell + top: the last
    level of abyss.MONSTER_LEVELS at the most."""
    return abyss.LEVEL_CELLS[0] + 1, abyss.MONSTER_LEVELS[-1] + 1 - top


def _carries(value, field):
    """The line of the first table inside a parsed value that has this field; None when none has."""
    if isinstance(value, dict):
        if field in value:
            return value.get("__line", 0)
        value = list(value.values())
    if isinstance(value, list):
        for inner in value:
            line = _carries(inner, field)
            if line is not None:
                return line
    return None


def _as_written(value):
    """A parsed value in the words of an error: a text in quotes, a name or a number as it stands, true,
    false and nil as a script writes them, a table by its brackets."""
    if isinstance(value, bool):
        return "true" if value else "false"
    if value is None:
        return "nil"
    if isinstance(value, (dict, list)):
        return "{ ... }"
    if isinstance(value, str) and not isinstance(value, luagroup.Ident):
        return '"%s"' % value
    return luagroup.plain(value)


def _levelled(parsed, gid, rel):
    """What one group script places with a level -> [{"table", "group", "rel", "cfg", "id", "level"}] in
    script order: the entries of its monsters and of its gadgets table (LEVELLED), "id" = the monster or
    the gadget. Every entry of the two names what it places by a number and carries a level, a whole number
    of abyss.SLOT_LEVEL or more -- on the least enemy level cell it then stands at the lowest level the
    growth curves are proven for --, and nothing else in the script's tables carries one. The fields of
    such an entry are written by their names: one given by an expression could be the level under another
    spelling."""
    where = Stack.path(rel)
    placed = []
    beside = []     # what the tables hold apart from the levels of those entries
    for name, value in parsed["tables"].items():
        if name not in LEVELLED:
            beside.append((name, value))
            continue
        if not isinstance(value, list):
            raise CatalogError("%s: the %s table is not a list of entries" % (where, name))
        for entry in value:
            if not isinstance(entry, dict):
                raise CatalogError("%s: the %s table holds something that is no entry" % (where, name))
            at = "%s:%d" % (where, entry.get("__line", 0))
            computed = [key for key in entry if isinstance(key, luagroup.Ident) or not (
                isinstance(key, str) and FIELD_NAME.fullmatch(key))]
            if computed:
                raise CatalogError("%s: an entry of the %s table has a field that is not written by its name: "
                                   "%s" % (at, name, computed[0]))
            placing, level = entry.get("%s_id" % LEVELLED[name]), entry.get(LEVEL)
            if isinstance(placing, bool) or not isinstance(placing, int):
                raise CatalogError("%s: an entry of the %s table names no %s by a number"
                                   % (at, name, LEVELLED[name]))
            if LEVEL not in entry:
                raise CatalogError("%s: the entry %s of the %s table carries no level"
                                   % (at, entry.get("config_id"), name))
            if isinstance(level, bool) or not isinstance(level, int) or level < abyss.SLOT_LEVEL:
                raise CatalogError("%s: the entry %s of the %s table carries the level %s, an entry has a whole "
                                   "level of %d or more"
                                   % (at, entry.get("config_id"), name, _as_written(level), abyss.SLOT_LEVEL))
            placed.append({"table": name, "group": gid, "rel": rel, "cfg": entry.get("config_id"),
                           "id": placing, "level": level})
            beside.extend((name, inner) for key, inner in entry.items() if key != LEVEL)
    for name, value in beside:
        line = _carries(value, LEVEL)
        if line is not None:
            raise CatalogError("%s:%d: the %s table holds a level that is not the level of an entry of the %s "
                               "tables" % (where, line, name, " and ".join(LEVELLED)))
    return placed


def _stunted(stack):
    """{table of LEVELLED: the ids no entry of it may name}: a monster, or a gadget that names growth curves,
    one of whose curves the curve table lacks on a level of abyss.MONSTER_LEVELS (GROWTH). Those are the
    levels an enemy level can put an entry of a scene at (level_range), and what stands at a level without
    its curve is refused by the server or has no health."""
    stunted = {}
    for name, (table, cells, curve_table) in GROWTH.items():
        curves = abyss.curves_at_every_level(stack, curve_table)
        t = stack.table(table)
        growth = [t.col(cell) for cell in cells]
        stunted[name] = set(to_int(r[t.col("ID")], "%s:%d" % (t.rel, line)) for line, r in t.rows
                            if not all(r[i] in curves for i in growth))
    return stunted


def _scene(stack, scene_id, monster_ids):
    """-> (group id of the line-up, its data-relative script, its strict slots, {group id: parsed script} of
    every group the scene loads, the ScriptLib functions its modules call, everything the scene places with
    a level: the entries of _levelled, group by group).

    The scene script names one block and the block names the groups; exactly one of them has monsters, and
    every entry of that group holds a monster of MonsterData. No script of the scene -- scene, block, group
    -- names a monster outside that group's monsters table, and whatever module one of them requires is
    held to _modules.

    The level of whatever stands in the scene is the level of an entry of a group's monsters or gadgets
    table (_levelled), and nothing else gives one: a group script writes the name once per such entry, the
    scene script, the block script and the modules not at all (_only_levels). The largest of those levels
    is the `top` of the line-up -- the entry an enemy level carries off the growth curves first. Those are
    the scripts read for a scene; the script of dummy points a scene script may name beside its block --
    positions under a name, nothing that stands at a level -- is not among them."""
    folder = "%s%d/" % (SCENES, scene_id)
    rel = "%sscene%d.lua" % (folder, scene_id)
    _raw, text, parsed = _script(stack, rel)
    blocks = parsed["tables"].get("blocks")
    if not isinstance(blocks, list) or len(blocks) != 1 or not isinstance(blocks[0], int):
        raise CatalogError("%s: the scene does not name exactly one block" % Stack.path(rel))
    _only_levels(text, Stack.path(rel))
    texts = {rel: text}
    block = "%sscene%d_block%d.lua" % (folder, scene_id, blocks[0])
    _raw, text, parsed = _script(stack, block)
    texts[block] = text
    listed = parsed["tables"].get("groups")
    if (not isinstance(listed, list) or not listed
            or not all(isinstance(g, dict) and isinstance(g.get("id"), int) for g in listed)
            or len(set(g["id"] for g in listed)) != len(listed)):
        raise CatalogError("%s: the block does not list its groups by id, each once" % Stack.path(block))
    _only_levels(text, Stack.path(block))
    groups = collections.OrderedDict()
    lineup = []
    placed = []
    for gid in sorted(g["id"] for g in listed):
        rel = "%sscene%d_group%d.lua" % (folder, scene_id, gid)
        raw, text, parsed = _script(stack, rel)
        texts[rel] = text
        groups[gid] = parsed
        entries = _levelled(parsed, gid, rel)
        _only_levels(text, Stack.path(rel), len(entries))
        placed.extend(entries)
        if parsed["tables"].get("monsters"):
            lineup.append((gid, rel, raw))
    if len(lineup) != 1:
        raise CatalogError("%s: %d of the groups the block lists have monsters (%s), a statue scene has one"
                           % (Stack.path(block), len(lineup),
                              ", ".join(str(gid) for gid, _rel, _raw in lineup) or "none"))
    gid, rel, raw = lineup[0]
    slots = abyss.parse_slots(raw, Stack.path(rel), slot_level=None)
    if not abyss.same_as_reader(slots, groups[gid]["tables"]["monsters"]):
        raise CatalogError("%s: the monsters table reads differently line by line and as a script"
                           % Stack.path(rel))
    for s in slots:
        if s["monster"] not in monster_ids:
            raise CatalogError("%s: monster %d of slot %d is not in MonsterData"
                               % (Stack.path(rel), s["monster"], s["cfg"]))
    called = set()
    seen = set()
    for name in sorted(texts):
        named = _integers(texts[name], "monsters" if name == rel else None) & monster_ids
        if named:
            raise CatalogError("%s: the script names the monster %d outside a monsters table"
                               % (Stack.path(name), min(named)))
        called |= _modules(stack, texts[name], Stack.path(name), seen)
    return gid, rel, slots, groups, called, placed


def _fires(groups, gid, trig):
    """False for a trigger that cannot fire: its event reaches only the group of the monster it is about,
    and its own group defines none."""
    return (towersim.event_name(trig) not in OWN_MONSTER_EVENTS
            or bool(groups[gid]["tables"].get("monsters")))


def _challenges(groups, challenge_types, where):
    """Every challenge a loaded trigger starts -- one that can fire -- must be of a kind in CHALLENGE_TYPES."""
    for gid, trig in towersim.loaded_triggers(groups):
        f = groups[gid]["functions"].get(trig.get("action") or "")
        for c in (f["calls"] if f and _fires(groups, gid, trig) else []):
            if c["fn"] not in CHALLENGE_CALLS:
                continue
            challenge = c["args"][2] if len(c["args"]) > 2 else None
            kind = challenge_types.get(str(challenge))
            if kind not in CHALLENGE_TYPES:
                raise CatalogError("%s: trigger %s of group %d starts challenge %s of type %s, a statue domain "
                                   "runs the types %s" % (where, trig.get("name"), gid, challenge, kind,
                                                          ", ".join(CHALLENGE_TYPES)))


def _named(groups, gid, defined, where):
    """The slots of the line-up the scene's scripts name, in the order they are named: in a suite of the
    group, in the literal list of an AutoMonsterTide aimed at the group, in a literal CreateMonster of the
    group -- the calls of loaded triggers only. Each must be a slot the group defines."""
    named = collections.OrderedDict()
    for suite in groups[gid]["tables"].get("suites", []):
        for cfg in suite.get("monsters", []):
            named[cfg] = True
    for caller, trig in towersim.loaded_triggers(groups):
        f = groups[caller]["functions"].get(trig.get("action") or "")
        for c in (f["calls"] if f else []):
            a = c["args"][1:]
            if c["fn"] == "AutoMonsterTide" and len(a) > 2 and a[1] == gid and isinstance(a[2], list):
                for cfg in a[2]:
                    named[cfg] = True
            elif c["fn"] == "CreateMonster" and caller == gid and a and isinstance(a[0], dict):
                named[a[0].get("config_id")] = True
    for cfg in named:
        if cfg not in defined:
            raise CatalogError("%s: the scripts name slot %s, which the group does not define" % (where, cfg))
    return list(named)


def _waves(groups, gid, challenge_types, named, called, where):
    """The waves of a line-up, in the order of their first spawn -> ([[[config id, times], ...], ...], None)
    when the run of the scene is a proof of them, else (None, the reason it is not).

    Not a proof, and no fault of the data: a trigger does something the run does not model
    (towersim.closure), a required module calls a function outside the ones known to leave a fight alone
    (MODULE_CALLS), the run meets something it cannot follow, more than one challenge is started, or the
    run never spawns a slot the scripts name. A run that IS inside the model has to add up -- one start
    spawns, nothing spawns before it, every monster is of the line-up group, one kill challenge of that
    group is started and won, the replayed kills are its count and the sum of the waves -- and one that
    does not stops the build."""
    reasons = towersim.closure(groups)
    harmless = set(towersim.NEUTRAL_CALLS + towersim.TIMER_CALLS + MODULE_CALLS)
    reasons.extend("a required module calls %s, which the run does not follow" % fn
                   for fn in sorted(called - harmless))
    if reasons:
        return None, "; ".join(reasons)
    sim = towersim.Sim(groups, challenge_types)
    sim.run()
    if sim.notes:
        return None, "; ".join(sorted(set(sim.notes)))
    stray = [s for s in sim.spawns if s["half"] == 0]
    if stray:
        raise CatalogError("%s: slot %s of group %s spawns before the challenge is started"
                           % (where, stray[0]["config_id"], stray[0]["group"]))
    starts = sorted(set(s["half"] for s in sim.spawns))
    if len(starts) != 1:
        raise CatalogError("%s: %d starts spawn monsters, a statue domain has one" % (where, len(starts)))
    foreign = sorted(set(s["group"] for s in sim.spawns if s["group"] != gid))
    if foreign:
        raise CatalogError("%s: the groups %s spawn monsters beside the line-up" % (where, foreign))
    if len(sim.started) > 1:
        return None, "%d challenges are started (%s)" % (
            len(sim.started), ", ".join(str(ch["challenge_id"]) for ch in sim.started))
    if not sim.started or "count" not in sim.started[0]:
        raise CatalogError("%s: no kill challenge is started" % where)
    ch = sim.started[0]
    if ch["group"] != gid:
        raise CatalogError("%s: the challenge counts group %s, the monsters are of group %d"
                           % (where, ch["group"], gid))
    kills = len([k for k in sim.kills if k["group"] == gid])
    if not ch["done"] or kills != ch["count"]:
        raise CatalogError("%s: the replay kills %d monsters, the script's own challenge counts %s"
                           % (where, kills, ch["count"]))
    waves = collections.OrderedDict()
    for s in sim.spawns:
        c = s["cause"]
        key = (c["kind"], s["group"], c.get("tide_id"), c.get("suite"), c["trigger"],
               c["line"] if c["kind"] != "create" else None)
        wave = waves.setdefault(key, collections.OrderedDict())
        wave[s["config_id"]] = wave.get(s["config_id"], 0) + 1
    if sum(sum(w.values()) for w in waves.values()) != ch["count"]:
        raise CatalogError("%s: the waves do not add up to the %s kills of the challenge" % (where, ch["count"]))
    spawned = set(cfg for w in waves.values() for cfg in w)
    missing = [cfg for cfg in named if cfg not in spawned]
    if missing:
        return None, "the run never spawns the slots %s, which the scripts name" % sorted(missing)
    return [[[cfg, n] for cfg, n in w.items()] for w in waves.values()], None


def build(stack, domain_list):
    """domain_list = the "list" of the domains section ({dungeon id: {"level": shown level, ...}}).

    -> (the line-ups {group id: {"dungeons", "file", "level", "top", "slots", "waves"?}}, the data-relative
    group scripts they name, every slot those scripts define (with "group", "scene" and "listed"), report
    lists).

    "level" is the largest level an entry of the script carries: a monster's. "top" is the largest level
    anything the scene places carries -- a monster of the line-up or a gadget of any group the scene loads
    --, so "level" at the least. The game adds the enemy level cell of a dungeon to all of them at once and
    the growth curves end, which makes "top" the bound of the enemy level of the line-up's domains
    (level_range); every monster counted for it, and every gadget that names growth curves, is proven to
    have them on every level it can be put at (_stunted). "waves" is there only for a scene whose run is a
    proof; "slots" then holds the slots the run spawns, and otherwise the slots the scripts name. A slot a
    script defines and the line-up does not list is in the report, and so is a scene whose "top" is above
    abyss.SLOT_LEVEL."""
    challenges = stack.table("DungeonChallengeData")
    challenge_types = dict((r[challenges.col("ID")].strip(), r[challenges.col("ChallengeType")].strip())
                           for _, r in challenges.rows)
    monsters = stack.table("MonsterData")
    monster_ids = set(to_int(r[monsters.col("ID")], "%s:%d" % (monsters.rel, line))
                      for line, r in monsters.rows)
    stunted = _stunted(stack)
    report = collections.OrderedDict((key, []) for key in REPORT)
    lineups = {}
    files = []
    every_slot = []
    for scene_id, dungeons in _rows(stack, domain_list).items():
        gid, rel, slots, groups, called, placed = _scene(stack, scene_id, monster_ids)
        where = Stack.path(rel)
        if str(gid) in lineups:
            raise CatalogError("%s: group %d is the line-up of two scenes" % (where, gid))
        for e in placed:
            if e["id"] in stunted[e["table"]]:
                raise CatalogError("%s: the %s %d (config id %s) has no growth curve for every level an enemy "
                                   "level can put it at, %d..%d"
                                   % (Stack.path(e["rel"]), LEVELLED[e["table"]], e["id"], e["cfg"],
                                      abyss.MONSTER_LEVELS[0], abyss.MONSTER_LEVELS[-1]))
        top = max(e["level"] for e in placed)
        _challenges(groups, challenge_types, where)
        monster_of = collections.OrderedDict((s["cfg"], s["monster"]) for s in slots)
        named = _named(groups, gid, monster_of, where)
        waves, reason = _waves(groups, gid, challenge_types, named, called, where)
        tag = "scene %d" % scene_id
        if waves is None:
            listed = set(named)
            report["flatScenes"].append("%s (group %d, dungeons %s): %s"
                                        % (tag, gid, ", ".join(str(d) for d in sorted(dungeons)), reason))
        else:
            listed = set(cfg for wave in waves for cfg, _times in wave)
            times = collections.Counter(cfg for wave in waves for cfg, _times in wave)
            for cfg in sorted(c for c, n in times.items() if n > 1):
                report["wavesSharingOneSlot"].append("%s: slot %d of group %d" % (tag, cfg, gid))
        if not listed <= set(monster_of):
            raise CatalogError("%s: slot %s spawns but the group does not define it"
                               % (where, sorted(listed - set(monster_of))[0]))
        if not listed:
            raise CatalogError("%s: the line-up lists no slot" % where)
        entry = {"dungeons": sorted(dungeons), "file": where, "level": max(s["level"] for s in slots),
                 "top": top,
                 "slots": dict((str(cfg), monster) for cfg, monster in monster_of.items() if cfg in listed)}
        if waves is not None:
            entry["waves"] = waves
        lineups[str(gid)] = entry
        files.append(rel)
        if top > abyss.SLOT_LEVEL:
            highest = [e for e in placed if e["level"] == top]
            first = highest[0]
            report["topLevels"].append(
                "%s (group %d, dungeons %s): the %s %d (config id %s) of group %d carries the level %d%s; "
                "the enemy level goes up to %d"
                % (tag, gid, ", ".join(str(d) for d in sorted(dungeons)), LEVELLED[first["table"]], first["id"],
                   first["cfg"], first["group"], top,
                   ", and so do %d more entries of the scene" % (len(highest) - 1) if len(highest) > 1 else "",
                   level_range(top)[1]))
        for s in slots:
            every_slot.append(dict(s, group=gid, scene=scene_id, listed=s["cfg"] in listed))
            if s["cfg"] not in listed:
                report["slotsNotListed"].append("%s: slot %d of group %d (monster %d)"
                                                % (tag, s["cfg"], gid, s["monster"]))
            if s["level"] != abyss.SLOT_LEVEL:
                report["slotLevels"].append("%s: slot %d of group %d carries the level %d"
                                            % (tag, s["cfg"], gid, s["level"]))
            if s["vision"]:
                report["visionLevels"].append("%s: slot %d of group %d" % (tag, s["cfg"], gid))
    return lineups, sorted(files), every_slot, report
