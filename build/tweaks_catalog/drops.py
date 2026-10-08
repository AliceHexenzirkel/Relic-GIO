"""The drop tables as the gameserver reads them: statue domains, their bounds and previews, the item caps.

DropTreeData.txt and DropLeafData.txt are one id space of drop nodes. A node has numbered child slots (id,
count, weight); a child id that is another node is rolled `count` times, any other id is an item. 随机方式 0
picks ONE child by weight, 1 tests EVERY child with weight / 10000. A count cell is 'N', 'A;B' (uniform) or a
decimal 'N.F' (N, or N + 1 with the fraction as its chance); a child with an empty or 0 id / weight does not
exist.

A repeatable domain = a DungeonData row of 类型 3 whose 神像奖励 names a DropTreeData row (the statue root),
rolled once per claim -- twice with condensed resin or a double-drop privilege.

What the server asks of a drop row when it loads the tables is modelled here, because the agent may only
write a row the server will load: the bounds (DropDB.root_fits), where an item may lie (Items: the ground
rule) and the guarantee rule of the servers that have ReliquaryPoolData.txt (DropDB.guarantee_problem).

The cap of a character card (the `once` ids of Items, from quests.build_characters) is one per cell,
below what the server's load check would take: the server uses ONE card of a cell the moment the row is
granted -- the character joins the roster, or, owned already, is converted into its Stella Fortuna and
starglitter -- and every further card of that cell lands in the bag as an item nobody wants there.
"""
import collections
import struct
from decimal import Decimal

from .stack import CatalogError, Stack, to_int

DAILY_FIGHT = "3"
STATUE_SOURCE_TYPE = "27"
RESIN = 106
# The item category (数值用类型) in a domain's reward that names its kind, and the 细分类型 a DungeonData
# row with that column must then carry.
KIND_BY_CATEGORY = (("圣遗物", "artifact", "1"), ("天赋书", "talent", "2"), ("武器突破材料", "weapon", "3"))

# Bounds the server enforces on a drop row (load check) and on one claim (runtime).
MAX_NODE_ROLLS = 99          # a child that is a node: count <= 99
MAX_RANDOM_NUM = 1000.0      # theoretical maximum number of random rolls of one tree
MAX_QUEUE_POPS = 999         # entries one claim may process; the rest is silently dropped
# The server's one count check behind a reward cell and a drop child alike: mora, any other virtual item,
# a weapon / artifact; a material or a piece of furniture is capped by its own 堆叠上限 (stack limit).
CAP_MORA = 9999999
CAP_VIRTUAL = 9999
CAP_EQUIP = 10
MORA = 202
ITEM_VIRTUAL, ITEM_MATERIAL, ITEM_RELIQUARY, ITEM_WEAPON, ITEM_FURNITURE = 1, 2, 3, 4, 6
ITEM_TABLES = ("MaterialData", "WeaponData", "ReliquaryData", "FurnitureExcelData")
# The guarantee rule (a server that loads ReliquaryPoolData.txt has it): a drop row without a history limit
# (an empty 历史产出次数上限) that can yield "guarantee" reliquaries -- 阶数 above 4, a 主属性库ID holding a
# main prop ReliquaryPoolData lists, a set with a 保底库ID -- must be able to yield all five piece types
# (圣遗物类别), else the server refuses the tables.
GUARANTEE_TABLE = "ReliquaryPoolData"
GUARANTEE_RANK_ABOVE = 4
PIECE_TYPES = frozenset((1, 2, 3, 4, 5))

PREVIEW_MAX = 10
# What the agent may ask of a root row: the largest multiplier plus the largest number of extra items.
AGENT_MULTIPLIER_MAX = 10
AGENT_EXTRA_MAX = 8


def _f32(x):
    return struct.unpack("<f", struct.pack("<f", x))[0]


class Count(object):
    """One 数量区间 cell, parsed the way the server does (single-precision for the decimal form)."""
    __slots__ = ("raw", "lo", "hi", "exp")

    def __init__(self, raw):
        self.raw = raw
        if "." in raw:
            f = _f32(float(raw))
            self.lo = int(f)
            self.hi = self.lo + 1
            chance = int(_f32(_f32(f - _f32(float(self.lo))) * _f32(10000.0)))
            self.exp = self.lo + chance / 10000.0
        else:
            parts = [int(p) for p in raw.split(";") if p != ""]
            if not 1 <= len(parts) <= 2:
                raise ValueError(raw)
            self.lo, self.hi = parts[0], parts[-1]
            self.exp = (self.lo + self.hi) / 2.0


def scale_count(raw, m):
    """A count cell multiplied by the integer m. A product that is integral is written WITHOUT a '.',
    because the decimal form always means 'N or N + 1'."""
    if "." in raw:
        v = Decimal(raw) * m
        if v == v.to_integral_value():
            return str(int(v))
        return ("%.4f" % v).rstrip("0").rstrip(".")
    return ";".join(str(int(p) * m) for p in raw.split(";"))


class Node(object):
    __slots__ = ("id", "table", "line", "mode", "ground", "source", "limited", "children")


def _guarantee_pieces(stack):
    """{reliquary id: piece type} of the reliquaries the guarantee rule is about; empty on a stack whose
    server does not have the rule."""
    if not stack.has("txt/%s.txt" % GUARANTEE_TABLE):
        return {}
    main = stack.table("ReliquaryMainData")
    depot_of = dict((r[main.col("主属性ID")].strip(), r[main.col("主属性库ID")].strip()) for _, r in main.rows)
    pool = stack.table(GUARANTEE_TABLE)
    depots = set()
    for line, r in pool.rows:
        prop = r[pool.col("主属性ID")].strip()
        if prop not in depot_of:
            raise CatalogError("%s:%d: the main prop %s is not one of ReliquaryMainData" % (pool.rel, line, prop))
        depots.add(depot_of[prop])
    sets = stack.table("ReliquarySetData")
    c_set, c_guarantee = sets.col("套装ID"), sets.col("保底库ID")
    guaranteed = set(r[c_set].strip() for _, r in sets.rows if r[c_guarantee].strip() not in ("", "0"))
    t = stack.table("ReliquaryData")
    c_rank, c_depot, c_of_set = t.col("阶数"), t.col("主属性库ID"), t.col("套装ID")
    out = {}
    for line, r in t.rows:
        where = "%s:%d" % (t.rel, line)
        if (r[c_rank].strip() and to_int(r[c_rank], where) > GUARANTEE_RANK_ABOVE
                and r[c_depot].strip() in depots and r[c_of_set].strip() in guaranteed):
            out[to_int(r[t.col("ID")], where)] = to_int(r[t.col("圣遗物类别")], where)
    return out


class Items(object):
    """Every item the server's item tables define: its cap and whether it may hang under a statue root.
    `resin_max` = the hard maximum of the resin row (no player ever holds more resin than that); `once` =
    the character cards, one per cell (the module docstring)."""

    def __init__(self, stack, resin_max, once=()):
        self.resin_max = resin_max
        self.once = frozenset(once)
        self.guarantee = _guarantee_pieces(stack)
        self.info = collections.OrderedDict()
        gadgets = set()
        for name in stack.table_names():
            if name.startswith("GadgetData_"):
                t = stack.table(name)
                gadgets.update(r[t.col("ID")].strip() for _, r in t.rows)
        for name in ITEM_TABLES:
            t = stack.table(name)
            c_stack = t.col("堆叠上限") if t.has("堆叠上限") else None
            c_bag = t.col("自动进包") if t.has("自动进包") else None
            for line, r in t.rows:
                where = "%s:%d" % (t.rel, line)
                iid = to_int(r[t.col("ID")], where)
                if iid in self.info:
                    raise CatalogError("%s: item %d is defined twice" % (where, iid))
                kind = to_int(r[t.col("类型")], where)
                limit = r[c_stack].strip() if c_stack is not None else ""
                gadget = r[t.col("物件ID")].strip()
                # A statue root drops to the ground, where an item needs a way to exist: it is virtual, goes
                # straight into the bag, or has a gadget of its own. What the server asks of furniture
                # there is not established, so furniture is never offered under a statue root.
                ground = (kind == ITEM_VIRTUAL
                          or (kind == ITEM_MATERIAL and c_bag is not None and r[c_bag] == "1")
                          or (kind != ITEM_FURNITURE and gadget not in ("", "0") and gadget in gadgets))
                self.info[iid] = {"type": kind, "category": r[t.col("数值用类型")],
                                  "stack": to_int(limit, where) if limit else 0, "ground": ground}

    def cap(self, iid):
        """The largest count the server accepts for the item in one reward cell or one drop child (the
        same check serves both); 0 = the item cannot be given at all. Resin is held to the hard maximum of
        the resin row on top of that: an amount that takes a player above it is not handed out at all, so
        a larger count could never be delivered. A character card is held to one: the server uses one
        card of a cell and the rest would land in the bag."""
        d = self.info.get(iid)
        if d is None:
            return 0
        if iid in self.once:
            return 1
        if d["type"] == ITEM_VIRTUAL:
            if iid == RESIN:
                return min(CAP_VIRTUAL, self.resin_max)
            return CAP_MORA if iid == MORA else CAP_VIRTUAL
        if d["type"] in (ITEM_MATERIAL, ITEM_FURNITURE):
            return d["stack"]
        if d["type"] in (ITEM_RELIQUARY, ITEM_WEAPON):
            return CAP_EQUIP
        return 0

    def offered(self, iid):
        """May the item be added to a statue root as an extra item: it has a way to exist on the ground, and
        it is no guarantee reliquary. ONE such piece under a row that does not yield all five piece types
        makes the server refuse the tables, so these pieces are not offered at all -- not even for the rows
        that do yield all five."""
        return self.info[iid]["ground"] and iid not in self.guarantee

    def section(self):
        """The "items" section: {item id: [cap, 1 when it may be added to a statue root]}."""
        out = {}
        for iid in self.info:
            cap = self.cap(iid)
            if cap >= 1:
                out[str(iid)] = [cap, 1 if self.offered(iid) else 0]
        return out


class DropDB(object):
    def __init__(self, stack):
        self.nodes = {}
        for name in ("DropTreeData", "DropLeafData"):
            t = stack.table(name)
            slots = []
            k = 1
            while t.has("子掉落%dID" % k):
                slots.append((k, t.col("子掉落%dID" % k), t.col("子掉落%d数量区间" % k),
                              t.col("子掉落%d权重" % k)))
                k += 1
            c_id, c_mode = t.col("掉落ID"), t.col("随机方式")
            c_ground, c_source = t.col("是否掉落地面"), t.col("产出来源类型")
            c_history = t.col("历史产出次数上限")
            for line, r in t.rows:
                where = "%s:%d" % (t.rel, line)
                n = Node()
                n.id, n.table, n.line = to_int(r[c_id], where), name, line
                n.mode, n.ground, n.source = r[c_mode], r[c_ground], r[c_source]
                n.limited = r[c_history].strip() != ""
                n.children = []
                for k, ci, cc, cw in slots:
                    if r[ci] in ("", "0") or r[cw] in ("", "0"):
                        continue
                    try:
                        n.children.append((k, to_int(r[ci], where), Count(r[cc]), to_int(r[cw], where)))
                    except ValueError:
                        raise CatalogError("%s: child %d has the count %r" % (where, k, r[cc]))
                if n.id in self.nodes:
                    raise CatalogError("%s: drop id %d is defined twice" % (where, n.id))
                self.nodes[n.id] = n
        self._yields = {}
        self._random = {}
        self._pops = {}
        self._pieces = {}

    def reach_items(self, root):
        """Every item id a root can yield."""
        seen, items, queue = set(), set(), [root]
        while queue:
            i = queue.pop()
            if i not in self.nodes:
                items.add(i)
            elif i not in seen:
                seen.add(i)
                queue.extend(c[1] for c in self.nodes[i].children)
        return items

    def yields(self, i):
        """{item id: expected count} of ONE roll of id i (an item yields itself once)."""
        if i not in self.nodes:
            return {i: 1.0}
        if i in self._yields:
            return self._yields[i]
        n = self.nodes[i]
        out = collections.OrderedDict()
        total = float(sum(c[3] for c in n.children))
        for _slot, cid, count, weight in n.children:
            p = min(weight, 10000) / 10000.0 if n.mode == "1" else weight / total
            for item, e in self.yields(cid).items():
                out[item] = out.get(item, 0.0) + p * count.exp * e
        self._yields[i] = out
        return out

    def max_random_num(self, i):
        """The server's 理论最大随机次数 of a node (its load check refuses a tree above 1000)."""
        if i not in self.nodes:
            return 0.0
        if i not in self._random:
            n = self.nodes[i]
            if n.mode == "0":
                v = max([self.max_random_num(c[1]) * c[2].hi for c in n.children] or [0.0]) + 1.0
            else:
                v = sum(1.0 + self.max_random_num(c[1]) * c[2].hi for c in n.children)
            self._random[i] = v
        return self._random[i]

    def max_pops(self, i):
        """Worst-case number of queue entries one roll of id i processes (an item = 1)."""
        if i not in self.nodes:
            return 1
        if i not in self._pops:
            n = self.nodes[i]
            each = [c[2].hi * self.max_pops(c[1]) if c[1] in self.nodes else 1 for c in n.children]
            self._pops[i] = 1 + ((max(each) if each else 0) if n.mode == "0" else sum(each))
        return self._pops[i]

    def root_fits(self, root, m, items, extra=0):
        """Does the root row keep every server bound with all its child counts multiplied by m and `extra`
        more item children -- even when the claim rolls it twice?"""
        rolls = float(extra)
        pops = 1 + extra
        for _slot, cid, count, _weight in self.nodes[root].children:
            hi = Count(scale_count(count.raw, m)).hi
            if cid in self.nodes:
                if hi > MAX_NODE_ROLLS:
                    return False
                pops += hi * self.max_pops(cid)
            else:
                if cid in items.info and hi > items.cap(cid):
                    return False
                pops += 1
            rolls += 1.0 + self.max_random_num(cid) * hi
        return rolls <= MAX_RANDOM_NUM and 2 * pops <= MAX_QUEUE_POPS

    def max_multiplier(self, root, items):
        """The largest integer m the root row tolerates (see root_fits)."""
        m = 0
        while m < 199 and self.root_fits(root, m + 1, items):
            m += 1
        return m

    def guarantee_types(self, i, items):
        """The piece types of the guarantee reliquaries id i can yield (an item yields itself). Remembered
        per id: one DropDB is asked with one Items."""
        if i not in self.nodes:
            return frozenset((items.guarantee[i],)) if i in items.guarantee else frozenset()
        if i not in self._pieces:
            types = set()
            for _slot, cid, _count, _weight in self.nodes[i].children:
                types |= self.guarantee_types(cid, items)
            self._pieces[i] = frozenset(types)
        return self._pieces[i]

    def guarantee_problem(self, i, items, extra=()):
        """The guarantee rule on drop row i -- with the item ids `extra` as further children of it: None when
        the row passes, else the sorted piece types it would yield guarantee reliquaries of (some, not all
        five). A row with a history limit is exempt."""
        if self.nodes[i].limited:
            return None
        types = self.guarantee_types(i, items)
        more = [items.guarantee[x] for x in extra if x in items.guarantee]
        if more:
            types = types.union(more)
        return sorted(types) if types and types != PIECE_TYPES else None

    def extra_problem(self, root, iid, items):
        """Why the server would refuse the statue root with item `iid` as one more child, whatever its count
        up to the item's cap -- None when it loads the row. (The bounds of the row are root_fits'.)"""
        d = items.info.get(iid)
        if d is None or items.cap(iid) < 1:
            return "the item has no cap"
        if self.nodes[root].ground == "1" and not d["ground"]:
            return "the item has no way to exist on the ground"
        types = self.guarantee_problem(root, items, (iid,))
        if types:
            return "the row would yield guarantee reliquaries of the piece types %s only" % types
        return None


def _prove_vendor_rows(db, items):
    """The guarantee rule as modelled here must hold on every row of the vendor's own tables -- the server
    loads them -- and it must be about something: where there are guarantee reliquaries, rows yield them."""
    yielding = 0
    for i in sorted(db.nodes):
        yielding += 1 if db.guarantee_types(i, items) else 0
        types = db.guarantee_problem(i, items)
        if types:
            node = db.nodes[i]
            raise CatalogError("%s:%d: drop %d has no history limit and can yield guarantee reliquaries of "
                               "the piece types %s only -- the guarantee rule as the generator models it "
                               "does not hold on the vendor's own row"
                               % (Stack.path("txt/%s.txt" % node.table), node.line, i, types))
    if items.guarantee and not yielding:
        raise CatalogError("%s: no drop row yields one of the %d guarantee reliquaries -- the guarantee rule "
                           "as the generator models it is about nothing"
                           % (Stack.path("txt/DropTreeData.txt"), len(items.guarantee)))


def _prove_extras(db, items, domains):
    """What the agent's validation of an extra item rests on: ANY single item the catalogue offers (drop 1)
    added to ANY listed statue root, with the row's own counts at the largest multiplier, gives a row the
    server loads."""
    offered = sorted(int(iid) for iid, (_cap, drop) in items.section().items() if drop == 1)
    where = Stack.path("txt/DropTreeData.txt")
    for dungeon in sorted(domains, key=int):
        root = domains[dungeon]["root"]
        node = db.nodes[root]
        if not db.root_fits(root, AGENT_MULTIPLIER_MAX, items, 1):
            raise CatalogError("%s:%d: the statue root %d does not tolerate x%d with one extra item"
                               % (where, node.line, root, AGENT_MULTIPLIER_MAX))
        held = set(cid for _slot, cid, _count, _weight in node.children)
        for iid in offered:
            if iid in held:
                continue    # the agent refuses an extra item the row already holds
            why = db.extra_problem(root, iid, items)
            if why:
                raise CatalogError("%s:%d: item %d is offered as an extra item, but not under the statue "
                                   "root %d of dungeon %s: %s" % (where, node.line, iid, root, dungeon, why))


def build_domains(stack, db, items):
    """-> (the "domains" section, report dict)."""
    _prove_vendor_rows(db, items)
    t = stack.table("DungeonData")
    c_id, c_type, c_statue = t.col("ID"), t.col("类型"), t.col("神像奖励")
    c_item, c_cost, c_level = t.col("开启神像消耗道具"), t.col("消耗数量"), t.col("显示等级")
    c_sub = t.col("细分类型") if t.has("细分类型") else None
    out = {}
    report = collections.Counter()
    for line, r in t.rows:
        where = "%s:%d" % (t.rel, line)
        if r[c_type] != DAILY_FIGHT or r[c_statue] in ("", "0"):
            continue
        dungeon = to_int(r[c_id], where)
        root = to_int(r[c_statue], where)
        node = db.nodes.get(root)
        if node is None or node.table != "DropTreeData":
            raise CatalogError("%s: the statue reward %d of dungeon %d is not a DropTreeData row"
                               % (where, root, dungeon))
        if (node.mode, node.ground, node.source) != ("1", "1", STATUE_SOURCE_TYPE):
            raise CatalogError("%s:%d: the statue root %d does not have the shape of a statue root"
                               % (Stack.path("txt/DropTreeData.txt"), node.line, root))
        if to_int(r[c_item], where) != RESIN:
            raise CatalogError("%s: the statue of dungeon %d does not cost resin" % (where, dungeon))
        categories = set(items.info[i]["category"] for i in db.reach_items(root) if i in items.info)
        kinds = [(kind, sub) for cat, kind, sub in KIND_BY_CATEGORY if cat in categories]
        if not kinds:
            raise CatalogError("%s: the reward of dungeon %d is neither artifacts, talent books nor weapon "
                               "materials" % (where, dungeon))
        kind, sub = kinds[0]
        if c_sub is not None and r[c_sub] != sub:
            raise CatalogError("%s: dungeon %d rewards %s but its sub type is %s"
                               % (where, dungeon, kind, r[c_sub]))
        for _slot, cid, _count, _weight in node.children:
            if cid not in db.nodes and items.cap(cid) < 1:
                raise CatalogError("%s:%d: the statue root %d holds item %d, which has no cap"
                                   % (Stack.path("txt/DropTreeData.txt"), node.line, root, cid))
        limit = db.max_multiplier(root, items)
        if (limit < AGENT_MULTIPLIER_MAX
                or not db.root_fits(root, AGENT_MULTIPLIER_MAX, items, AGENT_EXTRA_MAX)):
            raise CatalogError("%s: the statue root %d of dungeon %d does not tolerate x%d with %d extra "
                               "items" % (where, root, dungeon, AGENT_MULTIPLIER_MAX, AGENT_EXTRA_MAX))
        # the preview: the root's own currency children first, then the items a claim yields most of
        expected = dict((i, round(e, 4)) for i, e in db.yields(root).items())
        currency = [cid for _slot, cid, _count, _weight in node.children
                    if cid in items.info and items.info[cid]["type"] == ITEM_VIRTUAL]
        rest = sorted((i for i in expected if i not in currency), key=lambda i: (-expected[i], i))
        out[str(dungeon)] = {"kind": kind, "root": root, "cost": to_int(r[c_cost], where), "max": limit,
                             "level": to_int(r[c_level], where),
                             "preview": [[i, float(expected[i])] for i in (currency + rest)[:PREVIEW_MAX]]}
        report[kind] += 1
    roots = collections.Counter(d["root"] for d in out.values())
    shared = sorted(root for root, n in roots.items() if n > 1)
    if shared:
        raise CatalogError("%s: the statue roots %s belong to several dungeons" % (t.rel, shared))
    _prove_extras(db, items, out)
    section = {"file": Stack.path("txt/DungeonData.txt"), "dropFile": Stack.path("txt/DropTreeData.txt"),
               "list": out}
    return section, dict(report)
