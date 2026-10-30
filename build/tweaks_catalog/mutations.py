"""The generator's proofs, held against data that breaks them: make_tweaks_catalog.py --mutations.

A proof that never fails on the vendor's data says little about what it would refuse. Each case here edits a
file or two of the stack IN MEMORY (Overlay: nothing is written, nothing outside this process sees it), runs
the piece of the generator that owns the proof -- lineups.build for the dungeons of one statue scene,
abyss.chamber for one chamber, quests.build_rewards for the reward rows of the quests and of the hangout
events -- and states what has to come of it:

    STOP   the build stops with a CatalogError that names the file (for an Abyss chamber: the chamber and
           its scene)
    FLAT   the scene is listed without waves, for the stated reason
    WAVES  the scene stays listed with waves: the edit is one the generator is to take -- the control of a
           neighbouring case, which shows that case refuses its edit and not everything like it. A control
           of the levels also states what the line-up's `level` and `top` have to be, and that nothing
           else of the line-up differs
    OK     the same for an Abyss chamber; for the reward rows, what the edit makes of the row a case
           watches -- its kind, the quests that name it, whether it may be rewritten, its tier -- and that
           the other rows are as they were, or which differ

A case chooses what it edits by CONTENT -- the first scene listed with waves that serves one dungeon and
starts its own kill challenge, the first scene several dungeons share, the first script that refreshes a
group to suite 0, the first hangout chapter with two tiers ... -- never by a scene, group, dungeon or chapter
id, so the same cases run on any stack. One that finds nothing to edit on a stack is skipped with the reason
and never counts as caught.
"""
import collections
import re

from . import abyss
from . import lineups
from . import luagroup
from . import quests
from . import towersim
from .stack import CatalogError, Stack

# What became of a case -> the word for it in the closing count.
CAUGHT, KEPT, SKIPPED, MISSED = "caught", "kept", "skipped", "MISSED"
VERDICTS = collections.OrderedDict((
    (CAUGHT, "caught"), (KEPT, "kept (edits the generator is to take)"),
    (SKIPPED, "skipped (nothing to edit on this stack)"), (MISSED, "not as stated")))

DUNGEONS = "txt/DungeonData.txt"
# The tables the cases of the reward rows edit, and the column CoopPointData names its rows by.
REWARD_ROWS = "txt/RewardData.txt"
MAIN_QUESTS = "txt/%s.txt" % quests.MAIN_QUEST_TABLES[0]
COOP_REWARDS = "txt/%s.txt" % quests.COOP_REWARDS
COOP_CHAPTERS = "txt/%s.txt" % quests.COOP_CHAPTERS
COOP_POINTS = "txt/%s.txt" % quests.COOP_POINTS
POINT_ID = "节点ID"
NO_MD5 = "0" * 32
# The line about a case shows this much of what came of it.
SHOWN = 240

# One monster entry up to its closing bracket, and the bracket with what follows on the line.
ENTRY_LINE = rb"(\{ config_id = \d+, monster_id = \d+, [^\n]*?)( \},?\n)"
# One gadget entry up to its level.
GADGET = rb"\{ config_id = \d+, gadget_id = \d+, [^\n]*?level = \d+"
VISION = rb", vision_level = VisionLevelType.VISION_LEVEL_NORMAL"
CREATE = b"ScriptLib.CreateMonster(context, { config_id = %d, delay_time = 0 })"
# The kill challenge a script starts for a group, written the generated way: seconds, group, count.
KILL_CHALLENGE = rb"ScriptLib\.ActiveChallenge\(context, \d+, \d+, \d+, %d, \d+, 0\)"
CHALLENGE_HEAD = rb"(ScriptLib\.ActiveChallenge\(context, \d+, \d+, \d+)"
# A suite loaded by a call that stands alone on its line: (the line break and the indent, the call).
PLAIN_SUITE = rb"(\n[ \t]+)(ScriptLib\.AddExtraGroupSuite\(context, \d+, \d+\))"
DIE_CONDITION = rb"function (condition_EVENT_ANY_MONSTER_DIE_\d+)\(context, evt\)\n(.*?)\nend"
ACTION = rb"function (action_\w+)\(context, evt\)"
TIDE = rb"(AutoMonsterTide\(context, \d+, \d+, \{)([^}]*)(\}, )(\d+)"


class NoTarget(Exception):
    """The stack holds nothing this case could edit; the text says what was looked for."""


def _lf(raw):
    """-> (the bytes with LF line ends, whether every line of them ended in CRLF)."""
    crlf = raw.count(b"\r\n")
    if crlf and crlf == raw.count(b"\n") == raw.count(b"\r"):
        return raw.replace(b"\r\n", b"\n"), True
    return raw, False


class Overlay(Stack):
    """A stack whose files read as edited: `edits` {data-relative name: function(bytes) -> bytes} of listed
    files, `extra` {name: bytes} of files the vendor's list does not hold. A Lua script is handed to its
    edit with LF line ends and gets its own back, so one edit fits a CRLF and an LF stack. `unread` = the
    names nothing has asked for."""

    def __init__(self, base, edits=None, extra=None):
        self.version, self.root, self.data = base.version, base.root, base.data
        self.base = base
        self.edits = dict(edits or {})
        self.extra = dict(extra or {})
        self.from_backup = set()
        self.listed = dict(base.listed)
        self.listed.update((rel, NO_MD5) for rel in self.extra)
        self._tables = dict((rel, t) for rel, t in base._tables.items() if rel not in self.edits)
        self.unread = set(self.edits) | set(self.extra)

    def read(self, rel):
        self.unread.discard(rel)
        if rel in self.extra:
            return self.extra[rel]
        raw = self.base.read(rel)
        if rel not in self.edits:
            return raw
        if not rel.endswith(".lua"):
            return self.edits[rel](raw)
        code, crlf = _lf(raw)
        code = self.edits[rel](code)
        return code.replace(b"\n", b"\r\n") if crlf else code


# ---- edits --------------------------------------------------------------------------------------------------
def sub(pattern, repl, count=1, flags=0):
    """An edit: a regex replacement, which has to apply."""
    def edit(raw):
        out, n = re.subn(pattern, repl, raw, count=count, flags=flags)
        if not n:
            raise NoTarget("the file holds no %s" % pattern.decode("utf-8", "replace"))
        return out
    return edit


def chain(*edits):
    def edit(raw):
        for e in edits:
            raw = e(raw)
        return raw
    return edit


def add(text):
    """An edit: more code after the last line."""
    return lambda raw: raw + b"\n" + text + b"\n"


def cell(row_id, column, value, key="ID"):
    """An edit of a txt table: one cell of the first row whose cell `key` -- the column a table names its rows
    by: ID, RewardID, 父任务ID, ... -- is `row_id`."""
    def edit(raw):
        lines = raw.split(b"\n")
        header = lines[0].rstrip(b"\r").split(b"\t")
        if key.encode("utf-8") not in header or column.encode("utf-8") not in header:
            raise NoTarget("the table has no column %s or %s" % (key, column))
        by, at = header.index(key.encode("utf-8")), header.index(column.encode("utf-8"))
        for i, line in enumerate(lines[1:], 1):
            cells = line.rstrip(b"\r").split(b"\t")
            if len(cells) == len(header) and cells[by] == row_id.encode("utf-8"):
                cells[at] = value.encode("utf-8")
                lines[i] = b"\t".join(cells) + (b"\r" if line.endswith(b"\r") else b"")
                return b"\n".join(lines)
        raise NoTarget("the table holds no row %s" % row_id)
    return edit


def line_cell(line_no, column, value):
    """An edit of a txt table: one cell of the row on this line (1-based, as Table.rows numbers them)."""
    def edit(raw):
        lines = raw.split(b"\n")
        header = lines[0].rstrip(b"\r").split(b"\t")
        at = header.index(column.encode("utf-8")) if column.encode("utf-8") in header else -1
        cells = lines[line_no - 1].rstrip(b"\r").split(b"\t") if 1 < line_no <= len(lines) else []
        if at < 0 or len(cells) != len(header):
            raise NoTarget("the table has no column %s or no row on line %d" % (column, line_no))
        cells[at] = value.encode("utf-8")
        lines[line_no - 1] = b"\t".join(cells) + (b"\r" if lines[line_no - 1].endswith(b"\r") else b"")
        return b"\n".join(lines)
    return edit


def curve_gone(level, curve):
    """An edit of a curve table: the row of this level without this curve."""
    def edit(raw):
        lines = raw.split(b"\n")
        header = lines[0].rstrip(b"\r").split(b"\t")
        key = header.index("等级".encode("utf-8"))
        kinds = [at for at, name in enumerate(header) if name.endswith("类型".encode("utf-8"))]
        for i, line in enumerate(lines[1:], 1):
            cells = line.rstrip(b"\r").split(b"\t")
            held = [at for at in kinds if len(cells) == len(header) and cells[at] == curve.encode()]
            if held and cells[key] == b"%d" % level:
                cells[held[0]] = b""
                lines[i] = b"\t".join(cells) + (b"\r" if line.endswith(b"\r") else b"")
                return b"\n".join(lines)
        raise NoTarget("the table holds no curve %s on level %d" % (curve, level))
    return edit


def first_in(function, code):
    """An edit: code in front of everything else in a function; `function` is a pattern for its name."""
    return sub(rb"(function " + function + rb"\(context, evt\)\n)", lambda m: m.group(1) + b"\t" + code + b"\n")


def behind_suite(code):
    """An edit: a statement behind the first suite a plain call loads."""
    return sub(PLAIN_SUITE, lambda m: m.group(0) + m.group(1) + code)


def in_function(name, edit):
    """An edit of one function's text, from its first line to its `end`."""
    def apply(raw):
        m = re.search(rb"function " + name + rb"\(context, evt\)\n.*?\nend", raw, flags=re.S)
        if not m:
            raise NoTarget("the script holds no function %s" % name.decode())
        return raw[:m.start()] + edit(m.group(0)) + raw[m.end():]
    return apply


def swap(old, new):
    """An edit of a text: its first `old`, which has to be there, becomes `new`."""
    def edit(text):
        if old not in text:
            raise NoTarget("the function holds no %s" % old.decode("utf-8", "replace"))
        return text.replace(old, new, 1)
    return edit


def one_more_suite(cfg):
    """An edit: a suite after the last one (the numbers of the others stay) that names one monster slot and
    that nothing loads."""
    def edit(raw):
        start = raw.find(b"\nsuites = {")
        end = raw.find(b"\n}", start)
        if start < 0 or end < 0:
            raise NoTarget("the script holds no suites table")
        suite = (b",\n\t{\n\t\tmonsters = { %d },\n\t\tgadgets = { },\n\t\tregions = { },\n"
                 b"\t\ttriggers = { },\n\t\trand_weight = 100\n\t}" % cfg)
        return raw[:end] + suite + raw[end:]
    return edit


def one_more_trigger(event, source, body, config_id):
    """An edit: one more trigger, named by every suite that names a trigger, whose action is `body`."""
    name = b"%s_X" % event[len(b"EVENT_"):]
    return chain(
        sub(rb"(\ntriggers = \{\n)", rb'\1\t{ config_id = %d, name = "%s", event = EventType.%s, source = "%s", '
            rb'condition = "", action = "action_%s_X" },\n' % (config_id, name, event, source, event)),
        sub(rb'(triggers = \{ )"', rb'\1"%s", "' % name, count=0),
        add(b"function action_%s_X(context, evt)\n\t%s\n\treturn 0\nend" % (event, body)))


def module(body):
    """The text of a module with one function of this body."""
    return b"function f(context)\n\t" + body + b"\n\treturn 0\nend\n"


def swap_answers(text):
    """A condition whose first guard answers true and which ends in false."""
    text = text.replace(b"return false", b"return true", 1)
    head, _sep, tail = text.rpartition(b"return true")
    return head + b"return false" + tail


def nest(text):
    """A condition with its second guard inside the first and a `return true` inside, as many `return
    false` as before."""
    heads = re.findall(rb"if (.+?) then", text)
    return (text.split(b"\n", 1)[0] + b"\n\tif %s then\n\t\tif %s then\n\t\t\treturn false\n\t\tend\n"
            b"\t\treturn true\n\tend\n\treturn false\nend" % (heads[0], heads[1]))


def first_entry(code):
    """The first monster entry of a script, without the comma behind it."""
    return re.search(ENTRY_LINE, code).group(1) + b" }"


def spare_config(*codes):
    """A config id nothing in these scripts carries."""
    return max(int(cfg) for code in codes for cfg in re.findall(rb"config_id = (\d+)", code)) + 1


def renumbered(entry, cfg):
    """A monster entry under another config id."""
    return re.sub(rb"config_id = \d+", b"config_id = %d" % cfg, entry, count=1)


def commented_out(entry):
    """An edit: one more entry in the monsters table, inside a long comment."""
    return sub(rb"(\nmonsters = \{\n)", lambda m: m.group(1) + b"\t--[[\n\t" + entry + b",\n\t--]]\n")


def read_two_ways(change):
    """An edit: the first monster entry goes inside a long comment, where only the line-by-line reading
    takes it, and comes again as `change` makes it behind a long comment that closes on the same line,
    where only the script reader takes it -- as many entries either way, one of them not the same."""
    return sub(ENTRY_LINE, lambda m: b"--[[\n\t" + m.group(1) + m.group(2) + b"\t--]]\n\t--[[ ]] "
               + change(m.group(1)) + m.group(2))


def one_level_up(entry):
    """A monster entry with a level one above its own."""
    return re.sub(rb"level = (\d+)", lambda m: b"level = %d" % (int(m.group(1)) + 1), entry, count=1)


def at_level(level, of=b"monster"):
    """An edit: the first entry of a monster -- of a gadget, `of` naming it -- carries this level: a number,
    or the bytes to write in its place."""
    written = level if isinstance(level, bytes) else b"%d" % level
    return sub(rb"(" + of + rb"_id = \d+, [^\n]*?level = )\d+", lambda m: m.group(1) + written)


def without_level(of):
    """An edit: the first entry of a monster or of a gadget carries no level."""
    return sub(rb"(" + of + rb"_id = \d+, [^\n]*?), level = \d+", lambda m: m.group(1))


# ---- the unchanged stack ------------------------------------------------------------------------------------
class Bench(object):
    """The unchanged stack and what the cases choose their targets from. `check(line-ups, domain list)` is
    the generator's own check of a line-up section (it raises CatalogError); `items` = the item caps of the
    unchanged stack (drops.Items) the reward rows are built with -- no case edits a table they are read from."""

    def __init__(self, base, domain_list, check, items=None):
        self.base = base
        self.domains = domain_list
        self.check = check
        self.items = items
        self.lineups, _scripts, self.slots, _report = lineups.build(base, domain_list)
        self.table = base.table("DungeonData")
        c_id, c_scene = self.table.col("ID"), self.table.col("场景ID")
        self.row = collections.OrderedDict((r[c_id].strip(), r) for _line, r in self.table.rows)
        self.dungeons = collections.OrderedDict()   # scene id -> the listed domains it serves, in table order
        for dungeon, r in self.row.items():
            if dungeon in domain_list:
                self.dungeons.setdefault(int(r[c_scene]), []).append(dungeon)
        self.group_of = dict((int(self.row[str(e["dungeons"][0])][c_scene]), int(gid))
                             for gid, e in self.lineups.items())
        challenges = base.table("DungeonChallengeData")
        self.challenge_types = collections.OrderedDict(sorted(
            ((r[challenges.col("ID")].strip(), r[challenges.col("ChallengeType")].strip())
             for _line, r in challenges.rows), key=lambda x: int(x[0])))
        monsters = base.table("MonsterData")
        self.spare_monster = max(int(r[monsters.col("ID")]) for _line, r in monsters.rows) + 1
        self._codes = {}
        self._scenes = {}
        self.found = {}

    def code(self, rel):
        """A listed script of the unchanged stack with LF line ends, as an edit gets it."""
        if rel not in self._codes:
            self._codes[rel] = _lf(self.base.read(rel))[0]
        return self._codes[rel]

    def parsed(self, rel):
        return luagroup.parse_text(self.code(rel).decode("utf-8"))

    def scene(self, scene_id):
        if scene_id not in self._scenes:
            self._scenes[scene_id] = Scene(self, scene_id)
        return self._scenes[scene_id]

    def replayed(self):
        """The statue scenes the unchanged stack lists with waves, by ascending id."""
        for scene_id in sorted(self.dungeons):
            if "waves" in self.lineups[str(self.group_of[scene_id])]:
                yield self.scene(scene_id)

    def flat(self):
        for scene_id in sorted(self.dungeons):
            if "waves" not in self.lineups[str(self.group_of[scene_id])]:
                yield self.scene(scene_id)

    def revise(self, dungeon):
        """The enemy level cell of a dungeon row."""
        return int(self.row[dungeon][self.table.col("等级修正")])

    def cell(self, name, row_id, column):
        """One cell of the row with this ID in a txt table of the unchanged stack."""
        t = self.base.table(name)
        for _line, r in t.rows:
            if r[t.col("ID")].strip() == str(row_id):
                return r[t.col(column)]
        raise NoTarget("%s holds no row %s" % (t.rel, row_id))

    def spare_module(self, n=0):
        """A module name the vendor's list does not hold."""
        for i in range(1000):
            name = "Relic%s%d" % ("ABC"[n], i)
            if not self.base.has("%s%s.lua" % (lineups.MODULES, name)):
                return name
        raise NoTarget("no module name is free")

    def chambers(self):
        """[(level id, scene id, teams)] of the Abyss chambers, in table order."""
        levels, floors = self.base.table("TowerLevelData"), self.base.table("TowerFloorData")
        teams = dict((r[floors.col("关卡组ID")].strip(), int(r[floors.col("编队数量")])) for _line, r in floors.rows)
        c_scene = self.table.col("场景ID")
        return [(int(r[levels.col("关卡ID")]), int(self.row[r[levels.col("地城ID")].strip()][c_scene]),
                 teams[r[levels.col("组ID")].strip()]) for _line, r in levels.rows]


class Scene(object):
    """One statue scene of the unchanged stack: what a case reads of it, and its line-up on an edited stack."""

    def __init__(self, bench, scene_id):
        self.bench = bench
        self.id = scene_id
        self.what = "scene %d" % scene_id
        self.gid = bench.group_of[scene_id]
        self.lineup = bench.lineups[str(self.gid)]
        self.dungeons = bench.dungeons[scene_id]
        stem = "%s%d/scene%d" % (lineups.SCENES, scene_id, scene_id)
        self.scene_rel = stem + ".lua"
        self.block_rel = "%s_block%d.lua" % (stem, bench.parsed(self.scene_rel)["tables"]["blocks"][0])
        listed = sorted(g["id"] for g in bench.parsed(self.block_rel)["tables"]["groups"])
        self.rels = collections.OrderedDict((gid, "%s_group%d.lua" % (stem, gid)) for gid in listed)
        self.rel = self.rels[self.gid]
        self.code = bench.code(self.rel)
        self.groups = collections.OrderedDict((gid, bench.parsed(rel)) for gid, rel in self.rels.items())

    def need(self, value, what):
        if not value:
            raise NoTarget("%s has no %s" % (self.what, what))
        return value

    @property
    def other(self):
        """Another group the block lists."""
        return self.need([gid for gid in self.rels if gid != self.gid], "second group")[0]

    @property
    def entry(self):
        return first_entry(self.code)

    @property
    def monster(self):
        return int(re.search(rb"monster_id = (\d+)", self.entry).group(1))

    @property
    def curve(self):
        """The first growth curve of that monster."""
        table, cells, _held = lineups.GROWTH["monsters"]
        return self.bench.cell(table, self.monster, cells[0])

    @property
    def slot(self):
        """A slot the line-up lists."""
        return min(int(cfg) for cfg in self.lineup["slots"])

    @property
    def last(self):
        """The last slot of the last wave."""
        return self.lineup["waves"][-1][-1][0]

    @property
    def shown(self):
        """The level its first dungeon is shown with."""
        return self.bench.domains[self.dungeons[0]]["level"]

    @property
    def ceiling(self):
        """The largest level an entry of the scene may carry while that dungeon keeps the level it is shown
        with: the entry stands at the dungeon's enemy level cell -- one less than that level -- plus its
        own, the last level with a growth curve at the most. One more, and the level is none the domain can
        be set to. Stated from the levels themselves, never through lineups.level_range: the cases at this
        boundary hold that function."""
        return abyss.MONSTER_LEVELS[-1] + 1 - self.shown

    @property
    def higher(self):
        """A level above every level an entry of the scene carries, and below that ceiling."""
        return self.need(self.lineup["top"] + 1 < self.ceiling and self.lineup["top"] + 1,
                         "room for an entry above its highest")

    @property
    def spare(self):
        return spare_config(*(self.bench.code(rel) for rel in self.rels.values()))

    @property
    def spare_group(self):
        """A group id whose script the vendor's list does not hold."""
        stem = self.scene_rel[:-len(".lua")]
        return next(gid for gid in range(max(self.rels) + 1, max(self.rels) + 1000)
                    if not self.bench.base.has("%s_group%d.lua" % (stem, gid)))

    @property
    def starts(self):
        """[(group id, trigger)] of the worktop options a suite can load."""
        return [(gid, trig) for gid, trig in towersim.loaded_triggers(self.groups)
                if towersim.event_name(trig) == "EVENT_SELECT_OPTION"]

    @property
    def two_guards(self):
        """The name of a condition on a monster's death that holds two guards."""
        return self.need([m.group(1) for m in re.finditer(DIE_CONDITION, self.code, flags=re.S)
                          if len(re.findall(rb"if .+? then", m.group(2))) == 2],
                         "condition on a monster's death with two guards")[0]

    def run(self, edits=None, extra=None, domains=None):
        """The line-up of this scene on the stack as edited -> "STOP <error>", "FLAT <n> slots: <reason>" or
        "WAVES <n> waves, <m> slots". `domains` changes the domain list handed to the build."""
        return self.built(edits, extra, domains)[0]

    def levels(self, edits):
        """run, and behind its outcome what the edit made of the line-up's two levels: "; level <n>, top
        <m>, the rest of the line-up as before" -- or the keys of the line-up that differ beside them."""
        outcome, entry = self.built(edits)
        if entry is None:
            return outcome
        differs = sorted(key for key in set(entry) | set(self.lineup)
                         if key not in ("level", "top") and entry.get(key) != self.lineup.get(key))
        return "%s; level %s, top %s, %s" % (
            outcome, entry.get("level"), entry.get("top"),
            "and another %s" % ", ".join(differs) if differs else "the rest of the line-up as before")

    def built(self, edits=None, extra=None, domains=None):
        """-> (the outcome as run words it, the line-up of this scene on the stack as edited -- None for a
        stack that yields none)."""
        bench = self.bench
        stack = Overlay(bench.base, edits, extra)
        domain_list = dict((d, dict(bench.domains[d])) for d in self.dungeons)
        if domains:
            domains(domain_list)
        try:
            found, _scripts, _slots, report = lineups.build(stack, domain_list)
            bench.check(found, domain_list)
        except CatalogError as e:
            return "STOP %s" % e, None
        except NoTarget:
            raise
        except Exception as e:  # noqa: BLE001 -- no proof of the generator: its build would end in a traceback
            return "CRASH %s: %s" % (type(e).__name__, e), None
        if stack.unread:
            return "UNREAD the build never asked for %s" % ", ".join(sorted(stack.unread)), None
        entry = found.get(str(self.gid))
        if entry is None:
            return "LOST the edited stack has no line-up of group %d" % self.gid, None
        if "waves" in entry:
            return "WAVES %d waves, %d slots" % (len(entry["waves"]), len(entry["slots"])), entry
        return "FLAT %d slots: %s" % (len(entry["slots"]), report["flatScenes"][0].split(": ", 1)[1]), entry


class Chamber(object):
    """One Abyss chamber of the unchanged stack, with the group script a case edits."""

    def __init__(self, bench, level_id, scene_id, teams, rel):
        self.bench = bench
        self.level, self.scene, self.teams = level_id, scene_id, teams
        self.what = "chamber %d" % level_id
        self.rel = rel
        self.code = bench.code(rel)

    @property
    def slot(self):
        """A slot the script defines."""
        return int(re.search(rb"config_id = (\d+)", first_entry(self.code)).group(1))

    def run(self, edits):
        """The chamber on the stack as edited -> "STOP <error>" or "OK <what it yields>"."""
        stack = Overlay(self.bench.base, edits)
        report = collections.OrderedDict((key, []) for key in abyss.REPORT)
        try:
            halves, slots = abyss.chamber(stack, self.level, self.scene, self.teams, self.bench.challenge_types,
                                          set(), report)
        except CatalogError as e:
            return "STOP %s" % e
        except NoTarget:
            raise
        except Exception as e:  # noqa: BLE001 -- as in Scene.run
            return "CRASH %s: %s" % (type(e).__name__, e)
        if stack.unread:
            return "UNREAD the build never asked for %s" % ", ".join(sorted(stack.unread))
        return "OK %d halves, %d slots, %d with a vision_level" % (
            len(halves), sum(len(h["slots"]) for h in halves), sum(1 for s in slots if s["vision"]))


class Pick(object):
    """A scene with what a case found in it."""

    def __init__(self, scene, **found):
        self.scene = scene
        self.what = scene.what
        self.__dict__.update(found)


class Rewards(object):
    """The reward rows of the unchanged stack as quests.build_rewards lists them, and one hangout chapter: the
    first in CoopRewardData's order with two tiers or more whose reward rows are each named by one tier and by
    nothing else -- read off the tables here, not off the rows the build lists, so that a case can state what
    the build has to make of them. What a case reads of the stack around it is found by content (each property
    raises NoTarget when the stack holds none)."""

    def __init__(self, bench):
        if bench.items is None:
            raise NoTarget("the cases were handed no item caps to build reward rows with")
        self.bench = bench
        base = bench.base
        self.listed, _report = quests.build_rewards(base, bench.items)
        coop = base.table(quests.COOP_REWARDS)
        c_id, c_reward, c_chapter = (coop.col(name) for name in (
            quests.COOP_REWARD_ID, quests.COOP_REWARD_COLUMN, quests.COOP_CHAPTER_COLUMN))
        c_tier = coop.col(quests.COOP_CONDITION[2])
        chapters = collections.OrderedDict()    # chapter id -> [(tier, coop row id, reward id)], by tier
        tiers_of = collections.Counter()        # reward id -> the tiers that name it
        for _line, r in coop.rows:
            chapters.setdefault(r[c_chapter].strip(), []).append((int(r[c_tier]), r[c_id].strip(), int(r[c_reward])))
            tiers_of[int(r[c_reward])] += 1
        elsewhere = self._named_elsewhere()
        for chapter, tiers in chapters.items():
            tiers.sort()
            if len(tiers) > 1 and all(tiers_of[rid] == 1 and rid not in elsewhere for _tier, _coop, rid in tiers):
                break
        else:
            raise NoTarget("no hangout chapter has two tiers or more whose reward rows nothing else names")
        self.chapters, self.chapter, self.tiers = chapters, chapter, tiers
        self.endings = quests._hangout_endings(base).get(int(chapter), 0)
        self.what = "chapter %s" % chapter
        self._found = {}

    def _named_elsewhere(self):
        """Every id written in a column whose header says Reward / 奖励 of a table other than RewardData, the
        columns the reward rows are listed by left out."""
        base, found = self.bench.base, set()
        for name in base.table_names():
            if name == "RewardData":
                continue
            labelled = [ci for ci, h in enumerate(base.header(name))
                        if ("reward" in h.lower() or "奖励" in h or (name, h) in quests.EXTRA_REFERENCE_COLUMNS)
                        and (name, h) != (quests.COOP_REWARDS, quests.COOP_REWARD_COLUMN)
                        and not (name in quests.MAIN_QUEST_TABLES and h == quests.REWARD_COLUMN)]
            if labelled:
                t = base.table(name, keep=False)
                found.update(int(tok) for _line, r in t.rows for ci in labelled
                             for tok in quests._TOKEN.findall(r[ci]))
        return found

    def coop(self, tier):
        """The CoopRewardData row id of a tier of the chapter."""
        return [coop for n, coop, _rid in self.tiers if n == tier][0]

    def reward(self, tier):
        """The reward row a tier of the chapter grants."""
        return [rid for n, _coop, rid in self.tiers if n == tier][0]

    def _once(self, name, find):
        if name not in self._found:
            try:
                self._found[name] = find()
            except NoTarget as e:
                self._found[name] = e
        if isinstance(self._found[name], NoTarget):
            raise self._found[name]
        return self._found[name]

    @property
    def other_chapter(self):
        """Another chapter of CoopRewardData."""
        others = [c for c in self.chapters if c != self.chapter]
        if not others:
            raise NoTarget("CoopRewardData names one chapter")
        return others[0]

    @property
    def spare_reward(self):
        """A reward id RewardData does not define."""
        t = self.bench.base.table("RewardData")
        return max(int(r[t.col("RewardID")]) for _line, r in t.rows) + 1

    @property
    def spare_chapter(self):
        """A chapter id CoopChapterData does not define and no main quest carries."""
        def find():
            base = self.bench.base
            t = base.table(quests.COOP_CHAPTERS)
            taken = set(int(r[t.col(quests.COOP_CHAPTER_ID)]) for _line, r in t.rows)
            for name in quests.MAIN_QUEST_TABLES:
                m = base.table(name)
                taken.update(int(r[m.col("父任务ID")]) for _line, r in m.rows)
            return max(taken) + 1
        return self._once("spare_chapter", find)

    @property
    def middle_point(self):
        """A story point of the chapter that is no ending (its 节点ID)."""
        def find():
            t = self.bench.base.table(quests.COOP_POINTS)
            for _line, r in t.rows:
                if (r[t.col(quests.COOP_POINT_CHAPTER)].strip() == self.chapter
                        and r[t.col(quests.COOP_POINT_TYPE)].strip() != quests.COOP_POINT_END):
                    return r[t.col(POINT_ID)].strip()
            raise NoTarget("chapter %s has no story point but its endings" % self.chapter)
        return self._once("middle_point", find)

    @property
    def main_quest(self):
        """(the line of the first main quest whose reward cell names a reward row, its kind) in MAIN_QUESTS."""
        def find():
            base = self.bench.base
            rewards = base.table("RewardData")
            row_ids = set(r[rewards.col("RewardID")].strip() for _line, r in rewards.rows)
            t = base.table(quests.MAIN_QUEST_TABLES[0])
            for line, r in t.rows:
                kind = quests.KIND_BY_TYPE.get(r[t.col("任务类型")].strip())
                if kind and set(quests._TOKEN.findall(r[t.col(quests.REWARD_COLUMN)])) & row_ids:
                    return line, kind
            raise NoTarget("no main quest names a reward row")
        return self._once("main_quest", find)

    @property
    def first_slot(self):
        """(item, its cap) of the first item slot of tier 1's reward row, which has a second slot as well."""
        def find():
            t = self.bench.base.table("RewardData")
            for _line, r in t.rows:
                if r[t.col("RewardID")].strip() == str(self.reward(1)):
                    first, second = (r[t.col("Reward道具%dID" % k)].strip() for k in (1, 2))
                    if first in ("", "0") or second in ("", "0"):
                        break
                    return int(first), self.bench.items.cap(int(first))
            raise NoTarget("the reward row %d does not hold two item slots" % self.reward(1))
        return self._once("first_slot", find)

    @property
    def reference(self):
        """(the data-relative name of a table, a column of it the build counts as naming reward rows, the line of
        a row of it whose cell there names none of the listed rows): the first such column of the tables
        other than RewardData and the tables the rows are listed by."""
        def find():
            base = self.bench.base
            rewards = base.table("RewardData")
            row_ids = set(int(r[rewards.col("RewardID")]) for _line, r in rewards.rows)
            for name in base.table_names():
                if name in ("RewardData", quests.COOP_REWARDS) or name in quests.MAIN_QUEST_TABLES:
                    continue
                labelled = [(ci, h) for ci, h in enumerate(base.header(name))
                            if "reward" in h.lower() or "奖励" in h or (name, h) in quests.EXTRA_REFERENCE_COLUMNS]
                if not labelled:
                    continue
                t = base.table(name, keep=False)
                for ci, header in labelled:
                    if not any(int(tok) in row_ids for _line, r in t.rows for tok in quests._TOKEN.findall(r[ci])):
                        continue
                    for line, r in t.rows:
                        if not any(tok in self.listed for tok in quests._TOKEN.findall(r[ci])):
                            return "txt/%s.txt" % name, header, line
            raise NoTarget("no other table has a column that names reward rows")
        return self._once("reference", find)

    @staticmethod
    def entry(e):
        """A row of the "rewards" map in words."""
        if e is None:
            return "not listed"
        return "kind %s, quests %s, edit %d, %s" % (e["kind"], e["quests"], e["edit"],
                                                     "tier %d" % e["tier"] if "tier" in e else "no tier")

    def run(self, edits, watch=None):
        """The reward rows on the stack as edited -> "STOP <error>" or "OK <n> hangout rows, <m> editable", and
        for a `watch`ed reward row its entry and whether the other rows are as they were."""
        stack = Overlay(self.bench.base, edits)
        try:
            listed, _report = quests.build_rewards(stack, self.bench.items)
        except CatalogError as e:
            return "STOP %s" % e
        except NoTarget:
            raise
        except Exception as e:  # noqa: BLE001 -- as in Scene.run
            return "CRASH %s: %s" % (type(e).__name__, e)
        if stack.unread:
            return "UNREAD the build never asked for %s" % ", ".join(sorted(stack.unread))
        rows = [e for e in listed.values() if e["kind"] == quests.HANGOUT]
        outcome = "OK %d hangout rows, %d editable" % (len(rows), sum(e["edit"] for e in rows))
        if watch is None:
            return outcome
        differ = sorted((rid for rid in set(listed) | set(self.listed)
                         if rid != str(watch) and listed.get(rid) != self.listed.get(rid)), key=int)
        return "%s; reward %d: %s; %s" % (outcome, watch, self.entry(listed.get(str(watch))),
                                         "the other rows that differ: %s" % ", ".join(differ[:5]) if differ
                                         else "the other rows as before")


# ---- targets: each the first of its kind on the stack -------------------------------------------------------
def target(find):
    """A target is looked for once per stack; one that is not there stays so, with its reason."""
    def get(bench):
        if find not in bench.found:
            try:
                bench.found[find] = find(bench)
            except NoTarget as e:
                bench.found[find] = e
        if isinstance(bench.found[find], NoTarget):
            raise bench.found[find]
        return bench.found[find]
    return get


@target
def solo(b):
    """A scene listed with waves that serves one dungeon; its line-up script starts its own kill challenge,
    loads a suite by a plain call and has a condition on a monster's death."""
    for s in b.replayed():
        if (len(s.dungeons) == 1 and re.search(KILL_CHALLENGE % s.gid, s.code) and re.search(PLAIN_SUITE, s.code)
                and re.search(DIE_CONDITION, s.code, flags=re.S)):
            return s
    raise NoTarget("no scene with waves serves one dungeon with a kill challenge of its own group, a suite "
                   "loaded by a plain call and a condition on a monster's death")


@target
def shared(b):
    for s in b.replayed():
        if len(s.dungeons) > 1:
            return s
    raise NoTarget("no scene with waves serves several dungeons")


@target
def two_starts(b):
    for s in b.replayed():
        if len(s.starts) == 2:
            return s
    raise NoTarget("no scene with waves has two worktop options")


@target
def worktop(b):
    """The scene of `solo`, with a group beside the line-up whose worktop option a suite can load."""
    s = solo(b)
    return Pick(s, group=s.need([gid for gid, _trig in s.starts if gid != s.gid],
                                "worktop option outside the line-up group")[0])


@target
def tide(b):
    """A scene with waves in which a script opens a tide into the line-up group."""
    for s in b.replayed():
        for rel in s.rels.values():
            if re.search(rb"AutoMonsterTide\(context, \d+, %d, \{[^}]*\}, \d+, \d+, \d+\)" % s.gid, b.code(rel)):
                return Pick(s, rel=rel)
    raise NoTarget("no scene with waves opens a tide into its line-up group")


@target
def unlisted(b):
    """A scene with waves whose line-up script defines a slot the line-up does not list."""
    for s in b.replayed():
        left = [x["cfg"] for x in b.slots if x["group"] == s.gid and not x["listed"]]
        if left:
            return Pick(s, slot=left[0])
    raise NoTarget("every slot of every scene with waves is listed")


@target
def respawning(b):
    """A scene listed flat whose line-up script creates a slot's monster again when it dies and has triggers
    on the change of a variable: its fight goes on until a variable says otherwise."""
    for s in b.flat():
        functions = s.groups[s.gid]["functions"]
        for gid, trig in towersim.loaded_triggers(s.groups):
            condition = functions.get(trig.get("condition") or "")
            action = functions.get(trig.get("action") or "")
            if gid != s.gid or towersim.event_name(trig) != "EVENT_ANY_MONSTER_DIE" or not condition or not action:
                continue
            died = set(c["value"] for c in condition["compares"] if c["kind"] == "evt" and c["field"] == "param1")
            again = set(c["args"][1].get("config_id") for c in action["calls"]
                        if c["fn"] == "CreateMonster" and len(c["args"]) > 1 and isinstance(c["args"][1], dict))
            if died & again and b"EventType.EVENT_VARIABLE_CHANGE" in s.code:
                return s
    raise NoTarget("no flat scene creates a slot's monster again when it dies")


@target
def suite_zero(b):
    for s in b.replayed():
        if re.search(rb"suite = 0 \}\)", s.code):
            return s
    raise NoTarget("no line-up script of a scene with waves refreshes a group to suite 0")


@target
def region(b):
    """A scene with waves in which a script with several actions has a condition on entering a region."""
    for s in b.replayed():
        for rel in s.rels.values():
            m = re.search(rb"function (condition_EVENT_ENTER_REGION_\d+)\(context, evt\)\n", b.code(rel))
            actions = re.findall(ACTION, b.code(rel))
            if m and len(actions) > 1:
                mine = m.group(1).replace(b"condition_", b"action_")
                return Pick(s, rel=rel, condition=m.group(1), action=mine,
                            other=[name for name in actions if name != mine][0])
    raise NoTarget("no scene with waves has a condition on entering a region beside another action")


@target
def one_suite(b):
    """A scene with waves in which a group of one suite does not pick its suite at random."""
    for s in b.replayed():
        for gid, g in s.groups.items():
            tables = g["tables"]
            if (len(tables.get("suites", [])) == 1 and tables.get("init_config", {}).get("rand_suite") is False
                    and b"rand_suite = false" in b.code(s.rels[gid])):
                return Pick(s, rel=s.rels[gid])
    raise NoTarget("no scene with waves has a one-suite group without rand_suite")


@target
def foreign_challenge(b):
    """solo, and a challenge of a kind no statue domain runs."""
    for challenge, kind in b.challenge_types.items():
        if kind not in lineups.CHALLENGE_TYPES:
            return Pick(solo(b), challenge=challenge, kind=kind)
    raise NoTarget("every challenge is of a kind a statue domain runs")


@target
def other_pass(b):
    """solo, and a way to pass a dungeon that is not the end of a challenge alone."""
    passes = b.base.table("DungeonPassData")
    kinds = [i for i, name in enumerate(passes.header) if lineups.PASS_TYPE.match(name)]
    for _line, r in passes.rows:
        if any(r[i] not in ("", lineups.PASS_FINISH_CHALLENGE) for i in kinds):
            return Pick(solo(b), row=r[passes.col("ID")].strip(),
                        spare=str(max(int(x[passes.col("ID")]) for _l, x in passes.rows) + 1))
    raise NoTarget("every pass condition is the end of a challenge")


@target
def stranger(b):
    """solo, and a dungeon that is no listed domain."""
    for dungeon in b.row:
        if dungeon not in b.domains:
            return Pick(solo(b), dungeon=dungeon)
    raise NoTarget("every dungeon is a listed domain")


@target
def beside(b):
    """A scene with waves in which a group beside the line-up places a gadget, and that group's script."""
    for s in b.replayed():
        for gid, rel in s.rels.items():
            if gid != s.gid and re.search(GADGET, b.code(rel)):
                return Pick(s, rel=rel)
    raise NoTarget("no scene with waves places a gadget outside its line-up group")


@target
def own_gadget(b):
    """A scene with waves whose line-up group places a gadget."""
    for s in b.replayed():
        if re.search(GADGET, s.code):
            return s
    raise NoTarget("no line-up group of a scene with waves places a gadget")


@target
def grown(b):
    """A scene that places a gadget that names growth curves: the gadget, and the first of its curves."""
    table, cells, _curves = lineups.GROWTH["gadgets"]
    props = b.base.table(table)
    curve = dict((r[props.col("ID")].strip(), r[props.col(cells[0])]) for _line, r in props.rows)
    for scene_id in sorted(b.dungeons):
        s = b.scene(scene_id)
        for g in s.groups.values():
            for entry in g["tables"].get("gadgets", []):
                if str(entry.get("gadget_id")) in curve:
                    return Pick(s, gadget=entry["gadget_id"], curve=curve[str(entry["gadget_id"])])
    raise NoTarget("no statue scene places a gadget that names growth curves")


def _chamber(b, wanted, what):
    """The first chamber with a group script that holds every one of `wanted`."""
    for level_id, scene_id, teams in b.chambers():
        prefix = "%s%d/" % (lineups.SCENES, scene_id)
        for rel in b.base.names(prefix):
            if re.match(r"scene%d_group\d+\.lua$" % scene_id, rel[len(prefix):]) and all(
                    re.search(w, b.code(rel)) for w in wanted):
                return Chamber(b, level_id, scene_id, teams, rel)
    raise NoTarget("no Abyss script %s" % what)


@target
def chamber(b):
    return _chamber(b, (ENTRY_LINE,), "has a monster entry")


@target
def tide_chamber(b):
    return _chamber(b, (rb"EventType\.EVENT_MONSTER_TIDE_DIE", rb"EventType\.EVENT_ANY_MONSTER_DIE"),
                    "has triggers on a tide's deaths and on a monster's death")


@target
def die_chamber(b):
    return _chamber(b, (rb"ScriptLib\.AddExtraGroupSuite", rb"EventType\.EVENT_ANY_MONSTER_DIE",
                        rb"\{ config_id = \d+, monster_id"),
                    "loads a suite and has a trigger on a monster's death")


@target
def challenge_chamber(b):
    return _chamber(b, (rb"ScriptLib\.ActiveChallenge\(context, \d+, \d+, \w+, \d+, \d+, 0\)",),
                    "starts a kill challenge")


# ---- the cases ----------------------------------------------------------------------------------------------
CASES = []


def case(name, where, expect, play):
    """name: what is done to the data. where: the target. expect: (kind, text the outcome has to hold), or a
    function of the target that gives it. play: the target -> the outcome."""
    CASES.append((name, where, expect, play))


def stop(text):
    return "STOP", text


def flat(text):
    return "FLAT", text


WAVES = ("WAVES", "")
NOT_EDITABLE = stop("not written the way the slot editor expects")
TWO_READINGS = stop("reads differently line by line and as a script")
NAMES_MONSTER = lambda s: stop("names the monster %d outside a monsters table" % s.monster)  # noqa: E731
NOT_PLAYED = flat("its action is not the straight list of calls the run plays")
NOT_GUARDS = flat("its condition is not one '~=' guard per 'if'")
UNREADABLE_CHALLENGE = flat("is started with arguments the run cannot read")
LEVEL = b"level = %d" % abyss.SLOT_LEVEL

# the slot pattern, on an Abyss script
case("an Abyss entry that ends in a vision_level", chamber, ("OK", "1 with a vision_level"),
     lambda c: c.run({c.rel: sub(ENTRY_LINE, rb"\1" + VISION + rb"\2")}))
case("an Abyss entry whose vision_level is no VisionLevelType name", chamber, NOT_EDITABLE,
     lambda c: c.run({c.rel: sub(ENTRY_LINE, rb"\1, vision_level = 2\2")}))
case("an Abyss entry with a field the slot editor does not know", chamber, NOT_EDITABLE,
     lambda c: c.run({c.rel: sub(ENTRY_LINE, rb"\1, drop_id = 5\2")}))
case("an Abyss slot with a level of its own", chamber, stop("carries the level %d" % (abyss.SLOT_LEVEL + 2)),
     lambda c: c.run({c.rel: sub(rb"(monster_id = \d+, [^\n]*?)" + LEVEL,
                                 rb"\g<1>level = %d" % (abyss.SLOT_LEVEL + 2))}))
case("an Abyss entry inside a long comment", chamber, TWO_READINGS,
     lambda c: c.run({c.rel: commented_out(renumbered(first_entry(c.code), spare_config(c.code)))}))

# a trigger the run never fires does what the run keeps count of, in an Abyss chamber
case("the triggers on a tide's deaths of an Abyss chamber moved to an event the run never fires", tide_chamber,
     stop("on EVENT_MONSTER_TIDE_OVER, which the run does not fire"),
     lambda c: c.run({c.rel: sub(rb"EventType\.EVENT_MONSTER_TIDE_DIE", b"EventType.EVENT_MONSTER_TIDE_OVER",
                                 count=0)}))
case("the triggers on a monster's death of an Abyss chamber moved to the change of a variable", die_chamber,
     stop("on EVENT_VARIABLE_CHANGE, which the run does not fire"),
     lambda c: c.run({c.rel: sub(rb"EventType\.EVENT_ANY_MONSTER_DIE", b"EventType.EVENT_VARIABLE_CHANGE",
                                 count=0)}))
case("one more loaded trigger of an Abyss chamber creates a monster when a variable changes", die_chamber,
     stop("calls CreateMonster on EVENT_VARIABLE_CHANGE, which the run does not fire"),
     lambda c: c.run({c.rel: one_more_trigger(b"EVENT_VARIABLE_CHANGE", b"", CREATE % c.slot, spare_config(c.code))}))
case("the kill challenge of an Abyss chamber started without its group and count", challenge_chamber,
     stop("the scripts cannot be replayed: challenge"),
     lambda c: c.run({c.rel: sub(rb"(ScriptLib\.ActiveChallenge\(context, \d+, \d+, \w+), \d+, \d+, 0\)", rb"\1)")}))

# the scene, its block and its one monster group
case("the scene script names two blocks", solo, stop("does not name exactly one block"),
     lambda s: s.run({s.scene_rel: sub(rb"blocks = \{ (\d+) \}", rb"blocks = { \1, \1 }")}))
case("the block lists a group the vendor's list does not hold", solo, stop("not in the vendor's md5 list"),
     lambda s: s.run({s.block_rel: sub(
         rb"(groups = \{\n)",
         rb"\1\t{ id = %d, refresh_id = 1, pos = { x = 0.000, y = 0.000, z = 0.000 } },\n" % s.spare_group)}))
case("the block lists a group twice", solo, stop("does not list its groups by id, each once"),
     lambda s: s.run({s.block_rel: sub(rb"(groups = \{\n)(\t\{ id = \d+[^\n]*\n)", rb"\1\2\2")}))
case("a listed group holds an expression the script reader cannot read", solo, stop("the script reader cannot read"),
     lambda s: s.run({s.rels[s.other]: sub(rb"(\n)init_config = \{", rb"\1extra = { a = 1 + 2 }\1init_config = {")}))
case("a second group of the block has a monster", solo, stop("2 of the groups the block lists have monsters"),
     lambda s: s.run({s.rels[s.other]: sub(rb"(monsters = \{\n)", lambda m: m.group(1) + b"\t" + s.entry + b"\n")}))
case("no group of the block has a monster", solo, stop("0 of the groups the block lists have monsters"),
     lambda s: s.run({s.rel: sub(rb"monsters = \{\n.*?\n\}", b"monsters = {\n}", flags=re.S)}))

# the entries of the line-up script
case("a line-up entry with a field the slot editor does not know", solo, NOT_EDITABLE,
     lambda s: s.run({s.rel: sub(ENTRY_LINE, rb"\1, drop_id = 5\2")}))
case("a line-up entry with its vision_level in front of the other optional fields", solo, NOT_EDITABLE,
     lambda s: s.run({s.rel: sub(rb"(level = \d+)(, (?:affix|isElite|pose_id))", rb"\1" + VISION + rb"\2")}))
case("a line-up entry written twice", solo, stop("appears twice"),
     lambda s: s.run({s.rel: sub(rb"(monsters = \{\n)", lambda m: m.group(1) + b"\t" + s.entry + b",\n")}))
case("a line-up entry inside a long comment", solo, TWO_READINGS,
     lambda s: s.run({s.rel: commented_out(renumbered(s.entry, s.spare))}))
case("a line-up entry the script reader gives another level than its line does", solo, TWO_READINGS,
     lambda s: s.run({s.rel: read_two_ways(one_level_up)}))
case("a line-up entry that ends in a vision_level for the script reader alone", solo, TWO_READINGS,
     lambda s: s.run({s.rel: read_two_ways(lambda entry: entry + VISION)}))
case("a line-up entry with a level of its own and one that ends in a vision_level", solo, WAVES,
     lambda s: s.run({s.rel: chain(sub(rb"(monster_id = \d+, [^\n]*?)" + LEVEL,
                                       rb"\g<1>level = %d" % (abyss.SLOT_LEVEL + 3)),
                                   sub(ENTRY_LINE, rb"\1" + VISION + rb"\2"))}))
case("a line-up slot whose monster MonsterData does not hold", solo, stop("is not in MonsterData"),
     lambda s: s.run({s.rel: sub(rb"monster_id = \d+", b"monster_id = %d" % s.bench.spare_monster)}))

# a monster named outside the monsters table
case("a monster id in a function of the line-up script", solo, NAMES_MONSTER,
     lambda s: s.run({s.rel: first_in(rb"action_\w+", b"local wanted = %d" % s.monster)}))
case("a monster id in another group of the scene", solo, NAMES_MONSTER,
     lambda s: s.run({s.rels[s.other]: add(b"wanted = %d" % s.monster)}))
case("a monster id in the block script", solo, NAMES_MONSTER,
     lambda s: s.run({s.block_rel: add(b"wanted = %d" % s.monster)}))
case("a monster id in the scene script", solo, NAMES_MONSTER,
     lambda s: s.run({s.scene_rel: add(b"wanted = %d" % s.monster)}))
case("a monster id written in hexadecimal", solo, NAMES_MONSTER,
     lambda s: s.run({s.rel: add(b"wanted = 0x%X" % s.monster)}))
case("a monster id written as a float", solo, NAMES_MONSTER,
     lambda s: s.run({s.rel: add(b"wanted = %d.0" % s.monster)}))
case("a monster id written with an exponent", solo, NAMES_MONSTER,
     lambda s: s.run({s.rel: add(b"wanted = %d.%de1" % (s.monster // 10, s.monster % 10))}))
case("a monster id with a fraction behind it", solo, WAVES,
     lambda s: s.run({s.rel: add(b"wanted = %d.5" % s.monster)}))
case("a monster id in a comment", solo, WAVES,
     lambda s: s.run({s.rel: add(b"-- monster %d" % s.monster)}))


def required(s, text, rel=None, n=0):
    """(edits, extra) of a module with this text, required by a script of the scene (its line-up script)."""
    name = s.bench.spare_module(n)
    return ({rel or s.rel: add(b'require "%s"' % name.encode())},
            {} if text is None else {"%s%s.lua" % (lineups.MODULES, name): text})


def nested(s):
    """(edits, extra): another group requires a module that requires one with a monster in its text."""
    outer, inner = s.bench.spare_module(0), s.bench.spare_module(1)
    return ({s.rels[s.other]: add(b'require("%s")' % outer.encode())},
            {"%s%s.lua" % (lineups.MODULES, outer): b'require "%s"\n' % inner.encode(),
             "%s%s.lua" % (lineups.MODULES, inner): b"local MONSTER = 1\n"})


SPAWNS = module(CREATE % 1)
SETS_VARIABLE = module(b'ScriptLib.SetGroupVariableValue(context, "Key", 3)')

# the modules a script requires
case("a required module that speaks of monsters", solo, stop("speaks of monsters"),
     lambda s: s.run(*required(s, module(b"-- wakes the Monster"))))
case("a required module the vendor's list does not hold", solo, stop("is not a module of the vendor's list"),
     lambda s: s.run(*required(s, None)))
case("a module required by a required module speaks of monsters", solo, stop("speaks of monsters"),
     lambda s: s.run(*nested(s)))
case("a require that does not name its module", solo, stop("the script reader cannot read: require"),
     lambda s: s.run({s.rel: add(b"require(name)")}))
case("the scene script requires a module that speaks of monsters", solo, stop("speaks of monsters"),
     lambda s: s.run(*required(s, SPAWNS, s.scene_rel)))
case("the block script requires a module that speaks of monsters", solo, stop("speaks of monsters"),
     lambda s: s.run(*required(s, SPAWNS, s.block_rel)))
case("the scene script requires a module the vendor's list does not hold", solo,
     stop("is not a module of the vendor's list"), lambda s: s.run(*required(s, None, s.scene_rel)))
case("the block script requires a module that sets a group variable", solo,
     flat("a required module calls SetGroupVariableValue"), lambda s: s.run(*required(s, SETS_VARIABLE, s.block_rel)))
case("a required module that loads a suite", solo, flat("a required module calls AddExtraGroupSuite"),
     lambda s: s.run(*required(s, module(b"ScriptLib.AddExtraGroupSuite(context, 1, 2)"))))
case("a required module that starts a challenge", solo, flat("a required module calls StartChallenge"),
     lambda s: s.run(*required(s, module(b"ScriptLib.StartChallenge(context, 2, 999, {})"))))
case("a required module that kills a slot of the line-up", solo, flat("a required module calls KillEntityByConfigId"),
     lambda s: s.run(*required(s, module(b"ScriptLib.KillEntityByConfigId(context, { config_id = %d })" % s.slot))))
case("a required module that kills the group and removes an entity", solo,
     flat("a required module calls KillGroupEntity"),
     lambda s: s.run(*required(s, module(
         b"ScriptLib.KillGroupEntity(context, { group_id = %d, kill_policy = GroupKillPolicy.GROUP_KILL_ALL })\n"
         b"\tScriptLib.RemoveEntityByConfigId(context, %d, EntityType.GADGET, 1)" % (s.gid, s.gid)))))
case("a required module that sets a group variable", solo, flat("a required module calls SetGroupVariableValue"),
     lambda s: s.run(*required(s, SETS_VARIABLE)))
case("a required module that takes ScriptLib under another name", solo, flat("a required module calls ScriptLib"),
     lambda s: s.run(*required(s, b"local lib = ScriptLib\n" + module(b"lib.AddExtraGroupSuite(context, 1, 2)"))))
case("a required module of every call a module may make whose name holds no monster", solo, WAVES,
     lambda s: s.run(*required(s, module(b"\n\t".join(
         b"ScriptLib.%s(context)" % fn.encode()
         for fn in towersim.NEUTRAL_CALLS + towersim.TIMER_CALLS + lineups.MODULE_CALLS
         if "monster" not in fn.lower())))))


# the levels of what a scene places: the largest is the line-up's `top`
def keeps(level, top):
    """What an edit has to leave of a line-up: these two levels, and the rest of it as it was."""
    return "WAVES", "; level %d, top %d, the rest of the line-up as before" % (level, top)


def out_of_reach(s):
    """The stop for a scene one of whose entries carries the level above the scene's ceiling: the levels
    left to its domain begin at the lowest enemy level cell + 1 and end one below the level the domain is
    shown with. Stated from the levels themselves, as the ceiling is."""
    return stop("is shown with level %d, outside the %d..%d its enemy level can be set to"
                % (s.shown, abyss.LEVEL_CELLS[0] + 1, s.shown - 1))


# The stop for a scene one of whose entries carries the last level with a growth curve: the least enemy
# level cell already puts it past the curves.
NO_ROOM = stop("carries the level %d, which leaves its domains no enemy level" % abyss.MONSTER_LEVELS[-1])


def no_level(written):
    return stop("carries the level %s, an entry has a whole level of %d or more" % (written, abyss.SLOT_LEVEL))


def stray_level(table):
    return stop("the %s table holds a level that is not the level of an entry" % table)


case("a gadget beside the line-up group with a level above every other of the scene", beside,
     lambda p: keeps(p.scene.lineup["level"], p.scene.higher),
     lambda p: p.scene.levels({p.rel: at_level(p.scene.higher, b"gadget")}))
case("a gadget of the line-up group with a level above every other of the scene", own_gadget,
     lambda s: keeps(s.lineup["level"], s.higher), lambda s: s.levels({s.rel: at_level(s.higher, b"gadget")}))
case("a line-up entry with a level above every other of the scene", solo,
     lambda s: keeps(s.higher, s.higher), lambda s: s.levels({s.rel: at_level(s.higher)}))
case("a gadget whose level leaves the domain no enemy level above its own", beside,
     lambda p: keeps(p.scene.lineup["level"], p.scene.ceiling),
     lambda p: p.scene.levels({p.rel: at_level(p.scene.ceiling, b"gadget")}))
case("a gadget whose level puts the domain's own enemy level out of reach", beside,
     lambda p: out_of_reach(p.scene), lambda p: p.scene.run({p.rel: at_level(p.scene.ceiling + 1, b"gadget")}))
case("a line-up entry whose level leaves the domain no enemy level above its own", solo,
     lambda s: keeps(s.ceiling, s.ceiling), lambda s: s.levels({s.rel: at_level(s.ceiling)}))
case("a line-up entry whose level puts the domain's own enemy level out of reach", solo, out_of_reach,
     lambda s: s.run({s.rel: at_level(s.ceiling + 1)}))
case("a gadget whose level leaves the domains of its scene no enemy level at all", beside, NO_ROOM,
     lambda p: p.scene.run({p.rel: at_level(abyss.MONSTER_LEVELS[-1], b"gadget")}))
case("a gadget entry whose level is written under a text key", beside,
     lambda p: keeps(p.scene.lineup["level"], p.scene.lineup["top"]),
     lambda p: p.scene.levels({p.rel: sub(rb"(gadget_id = \d+, [^\n]*?)level = (\d+)", rb'\1["level"] = \2')}))

# a level that is none, an entry without one
case("a line-up entry with a level below the one an entry has at the least", solo, no_level(abyss.SLOT_LEVEL - 1),
     lambda s: s.run({s.rel: at_level(abyss.SLOT_LEVEL - 1)}))
case("a line-up entry with a level that is not a whole number", solo, no_level("2.5"),
     lambda s: s.run({s.rel: at_level(b"2.5")}))
case("a line-up entry without a level", solo, stop("of the monsters table carries no level"),
     lambda s: s.run({s.rel: without_level(b"monster")}))
case("a gadget entry with a level below the one an entry has at the least", beside,
     no_level(abyss.SLOT_LEVEL - 1), lambda p: p.scene.run({p.rel: at_level(abyss.SLOT_LEVEL - 1, b"gadget")}))
case("a gadget entry with a level that is not a whole number", beside, no_level("2.5"),
     lambda p: p.scene.run({p.rel: at_level(b"2.5", b"gadget")}))
case("a gadget entry with a level below zero", beside, no_level("-2"),
     lambda p: p.scene.run({p.rel: at_level(b"-2", b"gadget")}))
case("a gadget entry whose level is a name", beside, no_level("HARD"),
     lambda p: p.scene.run({p.rel: at_level(b"HARD", b"gadget")}))
case("a gadget entry whose level is a text", beside, no_level('"3"'),
     lambda p: p.scene.run({p.rel: at_level(b'"3"', b"gadget")}))
case("a gadget entry whose level is true", beside, no_level("true"),
     lambda p: p.scene.run({p.rel: at_level(b"true", b"gadget")}))
case("a gadget entry whose level is nil", beside, no_level("nil"),
     lambda p: p.scene.run({p.rel: at_level(b"nil", b"gadget")}))
case("a gadget entry whose level is a table", beside, no_level("{ ... }"),
     lambda p: p.scene.run({p.rel: at_level(b"{ 3 }", b"gadget")}))
case("a gadget entry without a level", beside, stop("of the gadgets table carries no level"),
     lambda p: p.scene.run({p.rel: without_level(b"gadget")}))
case("a gadget entry that names no gadget", beside, stop("names no gadget by a number"),
     lambda p: p.scene.run({p.rel: sub(rb"gadget_id = \d+, ", b"")}))
NOT_BY_NAME = stop("of the gadgets table has a field that is not written by its name")
case("a gadget entry with a field whose name is put together", beside, NOT_BY_NAME,
     lambda p: p.scene.run({p.rel: sub(rb"(gadget_id = \d+, )", rb'\1["le" .. "vel"] = 9, ')}))
case("a gadget entry with a field named by a variable", beside, NOT_BY_NAME,
     lambda p: p.scene.run({p.rel: sub(rb"(gadget_id = \d+, )", rb"\1[KEY] = 9, ")}))

# a level outside the entries of the monsters and the gadgets table
case("an entry of the triggers table with a level", solo, stray_level("triggers"),
     lambda s: s.run({s.rel: sub(rb"(\ntriggers = \{\n\t\{ config_id = \d+)", rb"\1, level = 2")}))
case("an entry of a table of another name with a level", solo, stray_level("extras"),
     lambda s: s.run({s.rels[s.other]: add(b"extras = {\n\t{ config_id = 1, gadget_id = 2, level = 2 }\n}")}))
case("a level among the fields of a group's init_config", solo, stray_level("init_config"),
     lambda s: s.run({s.rel: sub(rb"(\ninit_config = \{\n)", rb"\1\tlevel = 2,\n")}))
case("a level inside the position of a gadget entry", beside, stray_level("gadgets"),
     lambda p: p.scene.run({p.rel: sub(rb"(gadget_id = \d+, pos = \{ )", rb"\1level = 2, ")}))
case("a gadgets table with a level beside its entries", beside, stop("the gadgets table is not a list of entries"),
     lambda p: p.scene.run({p.rel: sub(rb"(\ngadgets = \{\n)", rb"\1\tlevel = 2,\n")}))
case("a gadgets table with a number among its entries", beside,
     stop("the gadgets table holds something that is no entry"),
     lambda p: p.scene.run({p.rel: sub(rb"(\ngadgets = \{\n)", rb"\1\t5,\n")}))

# a level given anywhere else in the code of a script
WRITES_LEVEL = stop("the script writes the name %s" % lineups.LEVEL)
SETS_LEVEL = module(b"local level = 30")
case("a gadget created with a level in a function of the line-up script", solo, WRITES_LEVEL,
     lambda s: s.run({s.rel: first_in(rb"action_\w+", b"ScriptLib.CreateGadget(context, { config_id = %d, "
                                                      b"level = 30 })" % s.spare)}))
case("a level set in a function of another group", solo, WRITES_LEVEL,
     lambda s: s.run({s.rels[s.other]: add(b"function LF_set(context)\n\tgadgets[1].level = 30\n\treturn 0\nend")}))
case("a level set in a function through a text key", solo, WRITES_LEVEL,
     lambda s: s.run({s.rel: add(b'function LF_set(context)\n\tmonsters[1]["level"] = 30\n\treturn 0\nend')}))
case("a level in a local table of a group script", solo, WRITES_LEVEL,
     lambda s: s.run({s.rel: add(b"local more = { { config_id = 1, gadget_id = 2, level = 30 } }")}))
case("a level in the block script", solo, WRITES_LEVEL, lambda s: s.run({s.block_rel: add(b"level = 30")}))
case("a level in the scene script", solo, WRITES_LEVEL, lambda s: s.run({s.scene_rel: add(b"level = 30")}))
case("a required module that sets a level", solo, WRITES_LEVEL, lambda s: s.run(*required(s, SETS_LEVEL)))
case("the block script requires a module that sets a level", solo, WRITES_LEVEL,
     lambda s: s.run(*required(s, SETS_LEVEL, s.block_rel)))
case("a call whose name speaks of a level", solo, stop("speaks of a level by the name SetMonsterLevel"),
     lambda s: s.run({s.rel: behind_suite(b"ScriptLib.SetMonsterLevel(context, 30)")}))
case("a required module with a function whose name speaks of a level", solo,
     stop("speaks of a level by the name AdjustLevels"),
     lambda s: s.run(*required(s, b"function AdjustLevels(context)\n\treturn 0\nend\n")))
case("the word level in a comment and inside a logged text", solo, WAVES,
     lambda s: s.run({s.rel: chain(add(b"-- level = 30"),
                                   behind_suite(b'ScriptLib.PrintContextLog(context, "the level of the fight")'))}))
case("a required module with the word level in a comment and inside a logged text", solo, WAVES,
     lambda s: s.run(*required(s, module(b"-- sets no level\n"
                                         b'\tScriptLib.PrintContextLog(context, "level reached")'))))

# a level behind a spelling or a statement the script reader does not take in as Lua does
MISREAD = stop("writes a backslash where the script reader does not read it as Lua does")
SWALLOWED = stop("the script reader cannot read")
case("a second level of a gadget entry under a key spelled with an escape", beside, MISREAD,
     lambda p: p.scene.run({p.rel: sub(rb"gadget_id = \d+, [^\n]*?level = \d+",
                                       lambda m: m.group(0) + b', ["le\\118el"] = 30')}))
case("a required module that sets a level under a key spelled with an escape", solo, MISREAD,
     lambda s: s.run(*required(s, module(b'local t = {}\n\tt["\\x6cevel"] = 30'))))
case("an escaped quote and backslash in a logged text, a backslash in long brackets and in a comment", solo, WAVES,
     lambda s: s.run({s.rel: chain(
         add(b"note = [[a \\ as itself]]\n-- a \\n in a comment"),
         behind_suite(b'ScriptLib.PrintContextLog(context, "a \\"quoted\\" text and a \\\\ in it")'))}))
case("a top-level local given an expression, and a level set through the key it makes", beside, SWALLOWED,
     lambda p: p.scene.run({p.rel: add(b'local k = "le" .. "vel" gadgets[1][k] = 30')}))
case("a top-level local given a call that sets a level", beside, SWALLOWED,
     lambda p: p.scene.run({p.rel: add(b'local _ = rawset(gadgets[1], "le" .. "vel", 30)')}))
case("top-level locals given a number, a text and a table", beside, WAVES,
     lambda p: p.scene.run({p.rel: add(b'local n = 5\nlocal s = "five"\nlocal t = { 5, "five" }')}))


# the growth curves of what a scene places
def growth(of):
    """(the data-relative table that names the growth curves of the monsters or of the gadgets, the cell of
    the first of them, the data-relative table that holds a curve level by level) -- lineups.GROWTH."""
    table, cells, held = lineups.GROWTH[of]
    return "txt/%s.txt" % table, cells[0], "txt/%s.txt" % held


LAST_LEVEL = abyss.MONSTER_LEVELS[-1]
LAST_ROW = sub(rb"\n%d\t[^\n]*" % LAST_LEVEL, b"")
NO_GROWTH = stop("has no growth curve for every level")
for _of in sorted(lineups.GROWTH):
    case("the curve table of the %s without its last level" % _of, solo,
         stop("%s.txt: no row for level %d" % (lineups.GROWTH[_of][2], LAST_LEVEL)),
         lambda s, of=_of: s.run({growth(of)[2]: LAST_ROW}))
case("a line-up monster that names no growth curve", solo, NO_GROWTH,
     lambda s: s.run({growth("monsters")[0]: cell(str(s.monster), growth("monsters")[1], "")}))
case("a growth curve of a line-up monster that ends one level early", solo, NO_GROWTH,
     lambda s: s.run({growth("monsters")[2]: curve_gone(LAST_LEVEL, s.curve)}))
case("a growth curve of a line-up monster that is missing below the levels an enemy stands at", solo, WAVES,
     lambda s: s.run({growth("monsters")[2]: curve_gone(abyss.MONSTER_LEVELS[0] - 1, s.curve)}))
case("a gadget of a scene that names no growth curve", grown, NO_GROWTH,
     lambda p: p.scene.run({growth("gadgets")[0]: cell(str(p.gadget), growth("gadgets")[1], "")}))
case("a growth curve of a gadget of a scene that ends one level early", grown, NO_GROWTH,
     lambda p: p.scene.run({growth("gadgets")[2]: curve_gone(LAST_LEVEL, p.curve)}))

# the dungeon rows
case("a dynamic difficulty on the dungeon row", solo, stop("has a dynamic difficulty"),
     lambda s: s.run({DUNGEONS: cell(s.dungeons[0], "动态难度", "1")}))
case("a dungeon passed by something else than the end of a challenge", other_pass,
     stop("is not passed by finishing a challenge alone"),
     lambda p: p.scene.run({DUNGEONS: cell(p.scene.dungeons[0], "通关条件", p.row)}))
case("a dungeon whose pass condition does not exist", other_pass,
     stop("is not passed by finishing a challenge alone"),
     lambda p: p.scene.run({DUNGEONS: cell(p.scene.dungeons[0], "通关条件", p.spare)}))
case("a dungeon shown with another level than its enemy level cell + 1", solo, stop("its enemy level cell holds"),
     lambda s: s.run({DUNGEONS: cell(s.dungeons[0], "等级修正", str(s.bench.revise(s.dungeons[0]) + 1))}))
case("a dungeon the list of the domains gives another level than its row shows", solo,
     stop("the list of the domains gives it"),
     lambda s: s.run(domains=lambda dl: dl[s.dungeons[0]].update(level=s.shown + 1)))
case("a dungeon with the enemy level cell 0", solo,
     stop("is outside %d..%d" % (abyss.LEVEL_CELLS[0], abyss.LEVEL_CELLS[-1])),
     lambda s: s.run({DUNGEONS: chain(cell(s.dungeons[0], "等级修正", "0"), cell(s.dungeons[0], "显示等级", "1"))},
                     domains=lambda dl: dl[s.dungeons[0]].update(level=1)))


def other_level(s):
    """The second dungeon of a shared scene with an enemy level cell of its own, shown as its row asks."""
    dungeon = s.dungeons[1]
    revise = s.bench.revise(dungeon) + 1
    return s.run({DUNGEONS: chain(cell(dungeon, "等级修正", str(revise)), cell(dungeon, "显示等级", str(revise + 1)))},
                 domains=lambda dl: dl[dungeon].update(level=revise + 1))


case("two dungeons of one scene with different enemy level cells", shared,
     stop("serves dungeons with the enemy level cells"), other_level)
case("another dungeon row names the scene of a statue domain", stranger, stop("the scene of a statue domain"),
     lambda p: p.scene.run({DUNGEONS: cell(p.dungeon, "场景ID", str(p.scene.id))}))
case("a listed domain without a dungeon row", solo, stop("are not one row each"),
     lambda s: s.run(domains=lambda dl: dl.update({str(max(int(d) for d in s.bench.row) + 1): {"level": 1}})))

# the challenge
START = rb"(ScriptLib\.ActiveChallenge\(context, \d+, )\d+"
case("a challenge of a kind no statue domain runs", foreign_challenge,
     lambda p: stop("starts challenge %s of type %s" % (p.challenge, p.kind)),
     lambda p: p.scene.run({p.scene.rel: sub(START, lambda m: m.group(1) + p.challenge.encode())}))
case("a challenge DungeonChallengeData does not hold", solo, stop("of type None"),
     lambda s: s.run({s.rel: sub(START, lambda m: m.group(1) + b"%d" % (
         max(int(c) for c in s.bench.challenge_types) + 1))}))
case("the kill count of the challenge raised by one", solo, stop("the replay kills"),
     lambda s: s.run({s.rel: sub(CHALLENGE_HEAD + rb", (\d+), (\d+)",
                                 lambda m: m.group(1) + b", %s, %d" % (m.group(2), int(m.group(3)) + 1))}))
case("the challenge counts another group", solo, lambda s: stop("the challenge counts group %d" % s.other),
     lambda s: s.run({s.rel: sub(CHALLENGE_HEAD + rb", \d+", lambda m: m.group(1) + b", %d" % s.other)}))
case("no trigger starts a challenge", solo, stop("no kill challenge is started"),
     lambda s: s.run({s.rel: sub(rb"ScriptLib\.ActiveChallenge\(context, [^\n]*?\) then",
                                 rb'ScriptLib.PrintContextLog(context, "x") then')}))
case("a second challenge started beside the first, on the same index", solo, flat("2 challenges are started"),
     lambda s: s.run({s.rel: behind_suite(re.search(KILL_CHALLENGE % s.gid, s.code).group(0))}))
case("the kill challenge started without its group and count", solo, UNREADABLE_CHALLENGE,
     lambda s: s.run({s.rel: sub(CHALLENGE_HEAD + rb", \d+, \d+, 0\)", rb"\1)")}))
case("the kill challenge started with a name for its group", solo, UNREADABLE_CHALLENGE,
     lambda s: s.run({s.rel: sub(CHALLENGE_HEAD + rb", \d+", rb"\1, base_info.group_id")}))
case("the kill challenge started with a name for its count", solo, UNREADABLE_CHALLENGE,
     lambda s: s.run({s.rel: sub(CHALLENGE_HEAD + rb", (\d+), \d+", rb"\1, \2, defs.count")}))
case("a call the run does not model where the challenge is started", solo,
     flat("calls StartChallenge, which the run does not model"),
     lambda s: s.run({s.rel: sub(rb"ScriptLib\.ActiveChallenge\(context, (\d+), (\d+), [^\n]*?\) then",
                                 rb"ScriptLib.StartChallenge(context, \1, \2, {1, 2, 3}) then")}))


# what spawns, and when
def into_first_suite(s):
    """An edit: the last slot of the last wave is also listed by the suite the line-up group starts with."""
    start = s.groups[s.gid]["tables"]["init_config"]["suite"]

    def edit(raw):
        at = raw.index(b"\nsuites = {")
        for _ in range(start):
            at = raw.index(b"monsters = {", at + 1)
        return raw[:at] + b"monsters = { %d," % s.last + raw[at + len(b"monsters = {"):]
    return edit


def tide_of_its_own(s):
    """Each of the two starts in turn opens a tide of one slot: the one that spawned nothing makes two
    starts that spawn."""
    tide_call = b"ScriptLib.AutoMonsterTide(context, 9, %d, {%d}, 1, 1, 1)" % (s.gid, s.slot)
    outcomes = [s.run({s.rels[gid]: first_in(trig["action"].encode(), tide_call)}) for gid, trig in s.starts]
    return ([o for o in outcomes if "starts spawn monsters" in o] + outcomes)[0]


case("the suite a line-up group starts with lists a monster", solo,
     lambda s: stop("slot %d of group %d spawns before the challenge is started" % (s.last, s.gid)),
     lambda s: s.run({s.rel: into_first_suite(s)}))
case("the start that spawns nothing opens a tide of its own", two_starts, stop("2 starts spawn monsters"),
     tide_of_its_own)
case("a start outside the line-up group creates a monster of its own group", worktop,
     lambda p: stop("the groups [%d] spawn monsters beside the line-up" % p.group),
     lambda p: p.scene.run({p.scene.rels[p.group]: first_in(rb"action_EVENT_SELECT_OPTION_\d+", CREATE % 1)}))
case("a tide total raised by one", tide, stop("the replay kills"),
     lambda p: p.scene.run({p.rel: sub(TIDE, lambda m: m.group(1) + m.group(2) + m.group(3)
                                       + b"%d" % (int(m.group(4)) + 1))}))
case("a tide list that names a slot the group does not define", tide,
     lambda p: stop("the scripts name slot %d, which the group does not define" % p.scene.spare),
     lambda p: p.scene.run({p.rel: sub(TIDE, lambda m: m.group(1) + b"%d," % p.scene.spare + m.group(2)
                                       + m.group(3) + m.group(4))}))
case("a suite nothing loads names a slot the run never spawns", unlisted,
     lambda p: flat("the run never spawns the slots [%d], which the scripts name" % p.slot),
     lambda p: p.scene.run({p.scene.rel: one_more_suite(p.slot)}))
case("a script that creates its slots again until a variable changes, the triggers on that change moved to the "
     "event that ends a fight", respawning, stop("the waves do not add up"),
     lambda s: s.run({s.rel: sub(rb"EventType\.EVENT_VARIABLE_CHANGE", b"EventType.EVENT_DUNGEON_SETTLE", count=0)}))
case("suite 0 asked for by triggers that are not the success of the challenge", suite_zero, flat("suite 0 of group"),
     lambda s: s.run({s.rel: sub(rb"event = EventType\.EVENT_CHALLENGE_SUCCESS",
                                 b"event = EventType.EVENT_ANY_MONSTER_DIE", count=0)}))
case("suite 0 of the line-up group itself, asked for on success", suite_zero,
     lambda s: flat("suite 0 of group %d does not exist" % s.gid),
     lambda s: s.run({s.rel: sub(rb"(group_id = )\d+(, suite = 0 \}\))", lambda m: m.group(1) + b"%d" % s.gid
                                 + m.group(2))}))
case("a line-up group that picks its first suite at random", solo,
     lambda s: flat("group %d starts with a suite picked at random" % s.gid),
     lambda s: s.run({s.rel: sub(rb"rand_suite = false", b"rand_suite = true")}))
case("a group of one suite that picks it at random", one_suite, WAVES,
     lambda p: p.scene.run({p.rel: sub(rb"rand_suite = false", b"rand_suite = true")}))

# a trigger the run does not fire, a call it does not model
case("the triggers on a monster's death moved to the change of a variable", solo,
     flat("calls AddExtraGroupSuite on EVENT_VARIABLE_CHANGE, which the run does not fire"),
     lambda s: s.run({s.rel: sub(rb"EventType\.EVENT_ANY_MONSTER_DIE", b"EventType.EVENT_VARIABLE_CHANGE", count=0)}))
case("a condition on an event the run does not fire loads a suite", region,
     flat("calls AddExtraGroupSuite on EVENT_ENTER_REGION, which the run does not fire"),
     lambda p: p.scene.run({p.rel: first_in(p.condition, b"ScriptLib.AddExtraGroupSuite(context, %d, 2)"
                                            % p.scene.gid)}))
case("an action on an event the run does not fire calls another action of its script", region,
     lambda p: flat("its action names the function %s of the script" % p.other.decode()),
     lambda p: p.scene.run({p.rel: first_in(p.action, p.other + b"(context, evt)")}))
case("a function no trigger names", solo, flat("belongs to no trigger"),
     lambda s: s.run({s.rel: add(b"function LF_more(context)\n\t" + CREATE % 1 + b"\n\treturn 0\nend")}))
case("a trigger whose action the script does not define", solo, flat("is not in the script"),
     lambda s: s.run({s.rel: sub(rb'(event = EventType\.EVENT_ANY_MONSTER_DIE[^\n]*action = ")(\w+)"',
                                 rb'\1\2_gone"')}))
case("a suite loaded into a group given by a name", solo,
     flat("calls AddExtraGroupSuite with arguments the run cannot read"),
     lambda s: s.run({s.rel: sub(PLAIN_SUITE, lambda m: m.group(1) + re.sub(
         rb"\(context, \d+,", b"(context, base_info.group_id,", m.group(2)))}))
case("a group refreshed without a suite", solo, flat("calls RefreshGroup with arguments the run cannot read"),
     lambda s: s.run({s.rel: behind_suite(b"ScriptLib.RefreshGroup(context, { group_id = %d })" % s.other)}))
case("a slot of the line-up killed by its config id", solo,
     flat("calls KillEntityByConfigId, which the run does not model"),
     lambda s: s.run({s.rel: behind_suite(b"ScriptLib.KillEntityByConfigId(context, { config_id = %d })" % s.slot)}))
case("an entity that is no monster killed by its config id", solo, WAVES,
     lambda s: s.run({s.rel: behind_suite(b"ScriptLib.KillEntityByConfigId(context, { config_id = %d })" % s.spare)}))
case("a timer whose trigger creates a monster", solo,
     flat("calls CreateGroupTimerEvent, which the run does not model"),
     lambda s: s.run({s.rel: chain(
         behind_suite(b'ScriptLib.CreateGroupTimerEvent(context, %d, "t", 5)' % s.gid),
         one_more_trigger(b"EVENT_TIMER_EVENT", b"t", CREATE % s.slot, s.spare))}))


# an action that is not the straight list of calls the run plays
def time_limit(s, test=b"tpl_time < 0", refused=rb"\1\treturn -1"):
    """The time limit of a challenge as the generated code reads it, in front of the call."""
    return (rb'\1local tpl_time = ScriptLib.GetGroupVariableValueByGroup(context, "Key", %d)\1if tpl_time == nil or '
            % s.gid + test + rb' then\1\tScriptLib.PrintContextLog(context, "x")' + refused
            + rb"\1elseif tpl_time < 1 then\1\ttpl_time = 0\1end\1\2")


for _name, _expect, _repl in (
        ("an action that branches on a variable", NOT_PLAYED,
         rb'\1if ScriptLib.GetGroupVariableValue(context, "Key") == 0 then\1\t\2\1end'),
        ("an action with a loop", NOT_PLAYED, rb"\1for i = 1, 2 do\1\t\2\1end"),
        ("a call that depends on a getter written like a failure test", NOT_PLAYED,
         rb'\1if 0 ~= ScriptLib.GetGroupVariableValue(context, "Key") then\1\t\2\1end'),
        ("a return in front of the calls that depends on a getter", NOT_PLAYED,
         rb"\1if 0 ~= ScriptLib.GetGroupMonsterCount(context) then\1\treturn 0\1end\1\2"),
        ("a failure test with a second test behind it", NOT_PLAYED,
         rb'\1if 0 ~= ScriptLib.PrintContextLog(context, "x") and evt.param1 == math.floor(4001) then\1\t\2\1end'),
        ("a failure test whose else branch makes the call", NOT_PLAYED,
         rb'\1if 0 ~= ScriptLib.PrintContextLog(context, "x") then\1\treturn -1\1else\1\t\2\1end'),
        ("the call inside the branch of a failure", NOT_PLAYED,
         rb'\1if 0 ~= ScriptLib.PrintContextLog(context, "x") then\1\t\2\1\treturn -1\1end'),
        ("an 'and' between a getter and the call", NOT_PLAYED,
         rb'\1local _ = ScriptLib.GetGroupVariableValue(context, "Key") ~= 0 and \2'),
        ("a block that returns in front of the call", NOT_PLAYED, rb"\1do return 0 end\1\2"),
        ("a failure branch that returns 0", NOT_PLAYED,
         rb'\1if 0 ~= ScriptLib.PrintContextLog(context, "x") then\1\treturn 0\1end\1\2'),
        ("a failure logged with a text that holds a bracket and a keyword", WAVES,
         rb'\1if 0 ~= \2 then\1\tScriptLib.PrintContextLog(context, "no ) end if")\1\treturn -1\1end'),
        ("a getter whose answer is dropped", WAVES, rb'\1ScriptLib.GetGroupVariableValue(context, "Key")\1\2'),
        ("a function of a module called with context and a number", WAVES, rb"\1SomeModuleFunction(context, 0)\1\2"),
):
    case(_name, solo, _expect, lambda s, repl=_repl: s.run({s.rel: sub(PLAIN_SUITE, repl)}))
case("the time limit of the generated code in front of the call", solo, WAVES,
     lambda s: s.run({s.rel: sub(PLAIN_SUITE, time_limit(s))}))
case("that time limit with another test", solo, NOT_PLAYED,
     lambda s: s.run({s.rel: sub(PLAIN_SUITE, time_limit(s, test=b"tpl_time > 3"))}))
case("that time limit with the call inside its branch", solo, NOT_PLAYED,
     lambda s: s.run({s.rel: sub(PLAIN_SUITE, time_limit(s, refused=rb"\1\t\2\1\treturn -1"))}))
case("an action that calls another action of its script", solo,
     lambda s: flat("its action names the function %s of the script" % re.findall(ACTION, s.code)[-1].decode()),
     lambda s: s.run({s.rel: sub(PLAIN_SUITE, rb"\1" + re.findall(ACTION, s.code)[-1] + rb"(context, 0)\1\2")}))

# a condition that is not what the run evaluates
ON_DEATH = rb"condition_EVENT_ANY_MONSTER_DIE_\d+"
case("a condition with a comparison the run does not evaluate", solo, NOT_GUARDS,
     lambda s: s.run({s.rel: first_in(ON_DEATH, b"if evt.param1 == evt.param2 then\n\t\treturn false\n\tend")}))
case("a condition with two guards under one if", solo, NOT_GUARDS,
     lambda s: s.run({s.rel: first_in(ON_DEATH, b"if 1 ~= evt.param1 and 2 ~= evt.param1 then\n\t\treturn false\n"
                                                b"\tend")}))
case("a condition with an elseif that answers true", solo, NOT_GUARDS,
     lambda s: s.run({s.rel: first_in(ON_DEATH, b"if 1 ~= evt.param1 then\n\t\treturn false\n"
                                                b"\telseif 2 ~= evt.param1 then\n\t\treturn true\n\tend")}))
case("a condition that reads a field of evt the run does not fill in", solo, flat("its condition reads evt.param3"),
     lambda s: s.run({s.rel: first_in(ON_DEATH, b"if 1 ~= evt.param3 then\n\t\treturn false\n\tend")}))
case("a condition whose first guard answers true and which ends in false", solo, NOT_GUARDS,
     lambda s: s.run({s.rel: in_function(s.two_guards, swap_answers)}))
case("a condition with its two guards nested and a return true inside", solo, NOT_GUARDS,
     lambda s: s.run({s.rel: in_function(s.two_guards, nest)}))
case("a condition that sets a group variable in front of its guards", solo, NOT_GUARDS,
     lambda s: s.run({s.rel: first_in(s.two_guards, b'ScriptLib.SetGroupVariableValue(context, "Key", 0)')}))
case("a guard against a number written in hexadecimal", solo, NOT_GUARDS,
     lambda s: s.run({s.rel: in_function(s.two_guards, swap(b"~= 0 then", b"~= 0x1 then"))}))
case("a guard with a second test behind an 'or'", solo, NOT_GUARDS,
     lambda s: s.run({s.rel: in_function(s.two_guards, swap(b"~= 0 then", b"~= 0 or evt.param1 == 5 then"))}))
case("a guard that is commented out", solo, WAVES,
     lambda s: s.run({s.rel: first_in(s.two_guards, b"-- if 9 ~= evt.param1 then return false end")}))


# the reward rows of the quests and of the hangout events
@target
def hangout(b):
    return Rewards(b)


def watched(rid, entry, rest="the other rows as before"):
    """What an edit the generator is to take has to leave of the reward rows: the watched row as Rewards.entry
    words it, the other rows as they were -- or the ones that differ."""
    return "OK", "; reward %d: %s; %s" % (rid, entry, rest)


CONDITION, COUNTED, TIER = quests.COOP_CONDITION
case("a hangout reward that names a reward RewardData does not define", hangout,
     lambda h: stop("names the reward %d, which RewardData does not define" % h.spare_reward),
     lambda h: h.run({COOP_REWARDS: cell(h.coop(1), quests.COOP_REWARD_COLUMN, str(h.spare_reward))}))
case("a hangout reward claimed on another condition than the number of its chapter's endings reached", hangout,
     stop("is claimed on the condition '5'"), lambda h: h.run({COOP_REWARDS: cell(h.coop(1), CONDITION, "5")}))
case("a hangout reward that counts the endings of another chapter", hangout,
     lambda h: stop("of chapter %s counts the endings of chapter %s" % (h.chapter, h.other_chapter)),
     lambda h: h.run({COOP_REWARDS: cell(h.coop(1), COUNTED, h.other_chapter)}))
case("a hangout chapter CoopChapterData does not define", hangout,
     lambda h: stop("belongs to chapter %s, which CoopChapterData does not define" % h.chapter),
     lambda h: h.run({COOP_CHAPTERS: cell(h.chapter, quests.COOP_CHAPTER_ID, str(h.spare_chapter),
                                          key=quests.COOP_CHAPTER_ID)}))
case("a hangout chapter whose id a main quest carries as well", hangout,
     lambda h: stop("belongs to chapter %s, which is the id of a main quest too" % h.chapter),
     lambda h: h.run({MAIN_QUESTS: line_cell(h.main_quest[0], "父任务ID", h.chapter)}))
case("a hangout reward that asks for more endings than its chapter has", hangout,
     lambda h: stop("is tier %d of chapter %s, which has %d endings" % (h.endings + 1, h.chapter, h.endings)),
     lambda h: h.run({COOP_REWARDS: cell(h.coop(h.endings), TIER, str(h.endings + 1))}))
case("a hangout reward that asks for no ending", hangout, lambda h: stop("is tier 0 of chapter %s" % h.chapter),
     lambda h: h.run({COOP_REWARDS: cell(h.coop(1), TIER, "0")}))
case("two hangout rewards of a chapter on one tier", hangout,
     lambda h: stop("is tier 1 of chapter %s, and so is the reward on line" % h.chapter),
     lambda h: h.run({COOP_REWARDS: cell(h.coop(2), TIER, "1")}))
case("one ending more in a chapter than its hangout rewards ask for", hangout,
     lambda h: stop("chapter %s has %d endings, and no hangout reward of it is tier %d"
                    % (h.chapter, h.endings + 1, h.endings + 1)),
     lambda h: h.run({COOP_POINTS: cell(h.middle_point, quests.COOP_POINT_TYPE, quests.COOP_POINT_END,
                                        key=POINT_ID)}))
case("a hangout reward row that holds more of an item than its cap", hangout,
     lambda h: stop("the reward %d holds %d of item %d, its cap is %d"
                    % (h.reward(1), h.first_slot[1] + 1, h.first_slot[0], h.first_slot[1])),
     lambda h: h.run({REWARD_ROWS: cell(str(h.reward(1)), "Reward道具1数量", str(h.first_slot[1] + 1),
                                        key="RewardID")}))
case("a hangout reward row that holds an item twice", hangout,
     lambda h: stop("the reward %d is empty or holds an item twice" % h.reward(1)),
     lambda h: h.run({REWARD_ROWS: cell(str(h.reward(1)), "Reward道具2ID", str(h.first_slot[0]), key="RewardID")}))
case("a hangout reward row a main quest names as well", hangout,
     lambda h: stop("the reward %d is used by quests of the kinds %s"
                    % (h.reward(1), ", ".join(sorted((quests.HANGOUT, h.main_quest[1]))))),
     lambda h: h.run({MAIN_QUESTS: line_cell(h.main_quest[0], quests.REWARD_COLUMN, str(h.reward(1)))}))
# who else names a hangout reward row: the column CoopRewardData names its rows by is not counted again,
# every other column whose header says Reward / 奖励 is, a column whose header names no reward is not
case("a hangout reward row named by the reward column of another table", hangout,
     lambda h: watched(h.reward(1), "kind hangout, quests [%s], edit 0, tier 1" % h.chapter),
     lambda h: h.run({h.reference[0]: line_cell(h.reference[2], h.reference[1], str(h.reward(1)))},
                     watch=h.reward(1)))
case("a hangout reward row named by a second tier of its chapter", hangout,
     lambda h: watched(h.reward(1), "kind hangout, quests [%s], edit 0, no tier" % h.chapter,
                       "the other rows that differ: %d" % h.reward(2)),
     lambda h: h.run({COOP_REWARDS: cell(h.coop(2), quests.COOP_REWARD_COLUMN, str(h.reward(1)))},
                     watch=h.reward(1)))
case("a hangout reward row's id in a column whose header names no reward", hangout,
     lambda h: watched(h.reward(1), "kind hangout, quests [%s], edit 1, tier 1" % h.chapter),
     lambda h: h.run({COOP_POINTS: cell(h.middle_point, "完成任务", str(h.reward(1)), key=POINT_ID)},
                     watch=h.reward(1)))


def names_file(outcome):
    """A stop has to say where: a file of the stack, or -- an Abyss chamber -- the chamber with its scene."""
    return outcome.startswith(("STOP " + Stack.path(""), "STOP chamber "))


def run(base, domain_list, check, items=None):
    """Plays every case against the unchanged stack `base`, whose listed domains are domain_list ->
    (verdict, one line about the case) each, in the order of CASES. `check` and `items` as Bench takes them;
    without `items` the cases of the reward rows are skipped."""
    bench = Bench(base, domain_list, check, items)
    for name, where, expect, play in CASES:
        try:
            found = where(bench)
            kind, text = expect(found) if callable(expect) else expect
            outcome = play(found)
        except NoTarget as e:
            yield SKIPPED, "%s -- %s" % (name, e)
            continue
        line = "%s [%s]: %s" % (name, found.what, outcome[:SHOWN])
        if not outcome.startswith(kind + " ") or text not in outcome:
            yield MISSED, "%s  -- stated: %s %s" % (line, kind, text)
        elif kind == "STOP" and not names_file(outcome):
            yield MISSED, "%s  -- a stop has to name the file" % line
        else:
            yield (CAUGHT if kind in ("STOP", "FLAT") else KEPT), line
