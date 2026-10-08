"""Quest reward rows and the daily-commission drops.

A quest's reward is a RewardData.txt row named by the parent quest's cell 任务奖励RewardID (a ',' / ';' list; 0 =
none) in MainQuestData.txt or MainQuestData_Exported.txt -- the server loads both. A row may be rewritten in
place only when exactly one quest names it and nothing else in the data does; "anything else" is found the
way the tables name their references: every column of every other table whose header says Reward / 奖励 and
that holds at least one RewardData id.

Daily commissions do not use RewardData: DailyTaskRewardData names one DropTreeData row per adventure-rank
bracket, DailyTaskLevelData the bonus for all four commissions.

A reward multiplier is meant to hand out more of what a quest gives. More of two kinds of item would be
something else -- a second copy of a character, further levels of a constellation -- so those keep their
count: the "keep" list (build_keep). A character card is held to ONE per reward cell on top of that
(build_characters names them, drops.Items caps them): the server uses the first card of a cell the moment
the row is granted, and every further card of that cell would land in the bag as an item.
"""
import collections
import re

from .stack import CatalogError, Stack, is_int, to_int

MAIN_QUEST_TABLES = ("MainQuestData", "MainQuestData_Exported")
REWARD_COLUMN = "任务奖励RewardID"
# 任务类型 (the server's QuestType; an empty cell reads as 0) -> the kind a multiplier is set for
KIND_BY_TYPE = {"": "archon", "0": "archon", "2": "story", "3": "event", "5": "commission", "7": "world"}
# columns that hold RewardData ids although their header does not say so
EXTRA_REFERENCE_COLUMNS = {("ActivitySalesmanData", "奖池")}
# scalar cells of a reward row = virtual items the server merges into the row's item list
SCALAR_ITEMS = (("原石", 201), ("摩拉", 202), ("冒险阅历", 102), ("角色经验", 101), ("好感经验", 105),
                ("树脂", 106))
REWARD_SLOTS = 9
# the currency children every commission drop holds: adventure EXP, companionship EXP, primogems, mora
DAILY_ITEMS = (102, 105, 201, 202)
# MaterialData: use slot N of a row is [使用]N操作 (the operation) with [使用]N参数1.. (its parameters). The
# operation 3 hands out the character its first parameter names (an AvatarData row).
USE_OPERATION = re.compile(r"^\[使用\](\d+)操作$")
USE_PARAMETER = "[使用]%s参数1"
USE_GAINS_AVATAR = "3"
# 数值用类型 of a MaterialData row: what the designers class the item as
CLASS_AVATAR, CLASS_CONSTELLATION = "角色", "命之座"

_TOKEN = re.compile(r"\d+")
_SPLIT = re.compile(r"[,;]")


def _reward_cells(table, cells, where):
    """[(item id, count)] of one reward row: the scalars that are set, then the used slots."""
    out = [(item, to_int(cells[table.col(name)], where)) for name, item in SCALAR_ITEMS
           if cells[table.col(name)].strip() not in ("", "0")]
    for k in range(1, REWARD_SLOTS + 1):
        item = cells[table.col("Reward道具%dID" % k)].strip()
        if item not in ("", "0"):
            out.append((to_int(item, where), to_int(cells[table.col("Reward道具%d数量" % k)], where)))
    return out


def build_rewards(stack, items):
    """-> (the "rewards" map of the "quests" section, report dict)."""
    rewards = stack.table("RewardData")
    row_of = {}
    for line, r in rewards.rows:
        rid = to_int(r[rewards.col("RewardID")], "%s:%d" % (rewards.rel, line))
        if rid in row_of:
            raise CatalogError("%s:%d: reward %d is defined twice" % (rewards.rel, line, rid))
        row_of[rid] = (line, r)

    quests = collections.OrderedDict()      # reward id -> [(quest id, kind)]
    for name in MAIN_QUEST_TABLES:
        t = stack.table(name)
        for line, r in t.rows:
            where = "%s:%d" % (t.rel, line)
            quest = to_int(r[t.col("父任务ID")], where)
            for token in _SPLIT.split(r[t.col(REWARD_COLUMN)]):
                if token.strip() == "":
                    continue
                rid = to_int(token, where)
                if rid == 0:
                    continue
                if rid not in row_of:
                    raise CatalogError("%s: quest %d names the reward %d, which RewardData does not define"
                                       % (where, quest, rid))
                kind = KIND_BY_TYPE.get(r[t.col("任务类型")].strip())
                if kind is None:
                    raise CatalogError("%s: quest %d has the type %s, which has no reward multiplier"
                                       % (where, quest, r[t.col("任务类型")]))
                quests.setdefault(rid, []).append((quest, kind))

    # who else names a reward id
    others = collections.Counter()
    for name in stack.table_names():
        if name == "RewardData":
            continue
        columns = [ci for ci, header in enumerate(stack.header(name))
                   if not (name in MAIN_QUEST_TABLES and header == REWARD_COLUMN)
                   and ("reward" in header.lower() or "奖励" in header
                        or (name, header) in EXTRA_REFERENCE_COLUMNS)]
        if not columns:
            continue
        t = stack.table(name, keep=False)
        for ci in columns:
            if not any(int(tok) in row_of for _, r in t.rows for tok in _TOKEN.findall(r[ci])):
                continue
            for _, r in t.rows:
                for token in _SPLIT.split(r[ci]):
                    token = token.strip()
                    if is_int(token) and int(token) in quests:
                        others[int(token)] += 1

    out = {}
    report = collections.Counter()
    for rid in sorted(quests):
        users = quests[rid]
        kinds = sorted(set(kind for _, kind in users))
        if len(kinds) != 1:
            raise CatalogError("%s: the reward %d is used by quests of the kinds %s; a row has one multiplier"
                               % (rewards.rel, rid, ", ".join(kinds)))
        # what a multiplier relies on: every count of the row has a cap, sits within it, and no item is
        # split over two cells
        line, cells = row_of[rid]
        where = "%s:%d" % (rewards.rel, line)
        held = _reward_cells(rewards, cells, where)
        for item, count in held:
            if not 1 <= count <= items.cap(item):
                raise CatalogError("%s: the reward %d holds %d of item %d, its cap is %d"
                                   % (where, rid, count, item, items.cap(item)))
        if not held or len(set(item for item, _ in held)) != len(held):
            raise CatalogError("%s: the reward %d is empty or holds an item twice" % (where, rid))
        quest_ids = sorted(set(q for q, _ in users))
        edit = 1 if len(quest_ids) == 1 and not others[rid] else 0
        out[str(rid)] = {"kind": kinds[0], "quests": quest_ids, "edit": edit}
        report[kinds[0]] += 1
        report["editable"] += edit
    return out, dict(report)


def build_daily(stack, db, items):
    """-> the "daily" entry of the "quests" section: the drop rows a commission multiplier rewrites."""
    roots = set()
    t = stack.table("DailyTaskRewardData")
    brackets = [c for c in t.header if re.match(r"^\[冒险等级奖励\]\d+DropID$", c)]
    if not brackets:
        raise CatalogError("%s: no bracket drop column" % t.rel)
    for line, r in t.rows:
        roots.update(to_int(r[t.col(c)], "%s:%d" % (t.rel, line)) for c in brackets)
    t = stack.table("DailyTaskLevelData")
    for line, r in t.rows:
        roots.add(to_int(r[t.col("积分DropId")], "%s:%d" % (t.rel, line)))
    where = Stack.path("txt/DropTreeData.txt")
    for root in sorted(roots):
        node = db.nodes.get(root)
        if node is None or node.table != "DropTreeData":
            raise CatalogError("%s: the commission drop %d is not one of its rows" % (where, root))
        plain = sorted(cid for _slot, cid, count, weight in node.children
                       if cid in DAILY_ITEMS and is_int(count.raw) and weight == 10000
                       and 1 <= count.lo <= items.cap(cid))
        if node.mode != "1" or plain != list(DAILY_ITEMS):
            raise CatalogError("%s:%d: the commission drop %d does not hold the four currency children as "
                               "plain counts" % (where, node.line, root))
    return {"file": where, "drops": sorted(roots), "items": list(DAILY_ITEMS)}


def build_characters(stack):
    """-> the character cards, sorted: every MaterialData row with a use slot whose operation is 3 -- it
    hands out the AvatarData row its first parameter names. Proven while building: every such parameter
    is an AvatarData row, and the designers class every such item as 角色 (数值用类型)."""
    materials = stack.table("MaterialData")
    c_id, c_class = materials.col("ID"), materials.col("数值用类型")
    uses = []
    for c_operation, name in enumerate(materials.header):
        slot = USE_OPERATION.match(name)
        if slot:
            uses.append((c_operation, materials.col(USE_PARAMETER % slot.group(1))))
    if not uses:
        raise CatalogError("%s: no use slot" % materials.rel)
    avatars = stack.table("AvatarData")
    avatar_ids = set(r[avatars.col("ID")].strip() for _, r in avatars.rows)
    characters = set()
    for line, r in materials.rows:
        where = "%s:%d" % (materials.rel, line)
        item = to_int(r[c_id], where)
        for c_operation, c_parameter in uses:
            if r[c_operation].strip() != USE_GAINS_AVATAR:
                continue
            if r[c_parameter].strip() not in avatar_ids or r[c_class] != CLASS_AVATAR:
                raise CatalogError("%s: item %d has the use operation %s, but its parameter %r is no character "
                                   "or its class is not %s" % (where, item, USE_GAINS_AVATAR, r[c_parameter],
                                                              CLASS_AVATAR))
            characters.add(item)
    return sorted(characters)


def build_keep(stack):
    """-> the "keep" list of the "quests" section: the items a reward multiplier leaves at their count.

    The rule, read off the tables the server acts on:
      - a character: a card build_characters names;
      - a constellation item: a MaterialData row that TalentSkillData names as 激活消耗主材料ID, the material
        one activation of a constellation level consumes.
    Proven while building: every such material is a MaterialData row, and the designers class it as 命之座
    (数值用类型)."""
    keep = set(build_characters(stack))
    materials = stack.table("MaterialData")
    c_id, c_class = materials.col("ID"), materials.col("数值用类型")
    classed = {}
    for line, r in materials.rows:
        classed[to_int(r[c_id], "%s:%d" % (materials.rel, line))] = r[c_class]
    talents = stack.table("TalentSkillData")
    c_material = talents.col("激活消耗主材料ID")
    for line, r in talents.rows:
        if r[c_material].strip() in ("", "0"):
            continue
        where = "%s:%d" % (talents.rel, line)
        item = to_int(r[c_material], where)
        if classed.get(item) != CLASS_CONSTELLATION:
            raise CatalogError("%s: the material %d of a constellation level is not a MaterialData row of the "
                               "class %s" % (where, item, CLASS_CONSTELLATION))
        keep.add(item)
    return sorted(keep)
