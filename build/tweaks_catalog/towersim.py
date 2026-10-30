"""Symbolic run of one scene's group scripts: a Spiral Abyss chamber, a statue domain.

The trigger code of the Abyss scenes is generated: every condition is a conjunction of 'literal ~= X ->
return false' guards and every action is an unconditional list of ScriptLib calls. That makes a small
interpreter enough: start each half the way its worktop does, kill whatever is alive (oldest first) and replay
the monster / tide / challenge events through the triggers that are loaded. The result is the order in which
the slots (config ids) really spawn, how often, what made them spawn, and whether the number of kills equals
the count the script itself hands to ActiveChallenge -- the proof the catalogue is built on.

Engine rules read out of the gameserver itself:
  * a trigger fires once unless its trigger_count says otherwise;
  * AutoMonsterTide(tide id, group, config list, total, min alive, max alive) refills only while alive < min,
    up to max, until `total` were spawned; it takes the list in order from a wrapping cursor and skips a
    config whose monster is alive; a config the group does not define ends that refill;
  * EVENT_MONSTER_TIDE_DIE carries the number of monsters of that tide that died so far in param1 and the tide
    id as its source;
  * the kill challenge counts every dead monster whose group is the challenge's group and is won when the
    count reaches the target.
The usual rules of these scripts, taken as given (the kill counts would not add up without them):
  * a trigger is live only while a loaded suite lists its name; trigger_count 0 = every time;
  * ANY_MONSTER_LIVE / ANY_MONSTER_DIE carry the config id in param1 and reach only the monster's own group;
    ANY_MONSTER_DIE sees the group's alive count after the death.

A run is a proof only for a scene that stays inside what is modelled here. The Abyss scenes are held to it by
their kill counts and by unreplayed(); a scene whose scripts are not all of that one kind (a statue domain) is
asked through closure(), which names everything a loaded trigger does that the run would not follow.
"""
import collections
import re

KILL_CHALLENGE_ARGS = {
    # ChallengeType -> positions (after context) of the group and the kill count
    "2": {"group": 3, "count": 4},     # ActiveChallenge(ctx, index, id, seconds, group, count, 0)
    "4": {"group": 2, "count": 3},     # ActiveChallenge(ctx, index, id, group, count, two times)
    "5": {"group": 3, "count": 4},     # ActiveChallenge(ctx, index, id, seconds, group, count, 0)
    "10": {"group": 2, "count": 3},    # ActiveChallenge(ctx, index, id, group, count, guarded gadget, 0)
}

# The events the run fires (Sim.spawn, load_suite, kill_one, run) -> the fields of `evt` it fills in; a
# start is fired with the gadget (param1) and the option (param2) its own condition asks for. A condition
# that reads another field would be answered by the run with "does not hold".
MODELLED_EVENTS = {
    "EVENT_ANY_MONSTER_LIVE": ("param1",),
    "EVENT_ANY_MONSTER_DIE": ("param1",),
    "EVENT_MONSTER_TIDE_DIE": ("param1",),
    "EVENT_GADGET_CREATE": ("param1",),
    "EVENT_CHALLENGE_SUCCESS": ("param1", "param2"),
    "EVENT_SELECT_OPTION": ("param1", "param2"),
}
# The events that end a fight: the challenge is lost, or the dungeon is settled after the challenge is won.
# Whatever their actions do happens after everything a run is asked about.
END_EVENTS = ("EVENT_CHALLENGE_FAIL", "EVENT_DUNGEON_SETTLE")
# ScriptLib functions that answer with a number about the scene -- a monster count, a group variable, the
# entities inside a region -- where the others report a failure. A condition asks them; an action that
# makes a call depend on one branches.
GETTER_CALLS = (
    "GetGroupMonsterCount", "GetGroupMonsterCountByGroupId",
    "GetGroupVariableValue", "GetGroupVariableValueByGroup",
    "GetRegionEntityCount",
)
# ScriptLib functions that create and remove no monster, load no suite, start no challenge and set no group
# variable: the log, gadget states, worktop options, the skill switch and the getters.
NEUTRAL_CALLS = (
    "PrintContextLog",
    "SetGadgetStateByConfigId", "SetGroupGadgetStateByConfigId",
    "SetWorktopOptions", "SetWorktopOptionsByGroupId", "DelWorktopOption", "DelWorktopOptionByGroupId",
    "SetIsAllowUseSkill",
) + GETTER_CALLS
# The ScriptLib functions Sim.run_action acts on (it steps over every other call) -> what it reads of the
# arguments after context, place by place: "n" a number, "s" a string, "N" a list of numbers, a tuple of
# names a table with those fields as numbers. A kill challenge's group and count come on top
# (KILL_CHALLENGE_ARGS).
MODELLED_CALLS = {
    "AutoMonsterTide": ("n", "n", "N", "n", "n", "n"),
    "CreateMonster": (("config_id",),),
    "AddExtraGroupSuite": ("n", "n"),
    "RemoveExtraGroupSuite": ("n", "n"),
    "RefreshGroup": (("group_id", "suite"),),
    "GoBackGroupSuite": ("n",),
    "ActiveChallenge": ("n", "n"),
    "ChangeGroupVariableValue": ("s", "n"),
    "SetGroupVariableValue": ("s", "n"),
    "SetGroupVariableValueByGroup": ("s", "n", "n"),
}
# A group timer leads to nothing but the EVENT_TIMER_EVENT triggers: its calls are as neutral as those are.
TIMER_EVENT = "EVENT_TIMER_EVENT"
TIMER_CALLS = ("CreateGroupTimerEvent", "CancelGroupTimerEvent")
# The event of a won challenge: whatever the challenge counts is dead by then.
CHALLENGE_SUCCESS = "EVENT_CHALLENGE_SUCCESS"
# An action as Sim.run_action plays it -- the straight list of its ScriptLib calls -- in the words of
# luagroup's `shape`. Generated action code is a list of four statements and ends in ACTION_RESULT: a call;
# a call whose failure is logged and returned; the time limit of a challenge, read from a group variable
# and refused when it is missing or negative; a function called by its name with context and numbers (one
# of a required module: closure refuses a function of the script itself). An `if` of these holds the log
# and the return alone, and the call whose failure it tests is no getter: beside that time limit, no call
# of such an action depends on the state of the scene. Any other statement -- another `if`, an `else`, a
# loop, another local, an `and` / `or` between calls -- is not what the run plays.
ACTION_SHAPE = re.compile(
    r"\( context , evt \) (?:"
    r"@\w+ "
    r"|if 0 ~= @(?!(?:%s) )\w+ then (?:@PrintContextLog )?return - 1 end "
    r"|local tpl_time = @GetGroupVariableValueByGroup if tpl_time == nil or tpl_time < \d+ then "
    r"(?:@PrintContextLog )?return - 1 elseif tpl_time < \d+ then tpl_time = \d+ end "
    r"|[A-Za-z_]\w* \( context(?: , \d+)* \) "
    r")*" % "|".join(GETTER_CALLS))
ACTION_RESULT = "return 0"
# A condition as Sim.cond_ok evaluates it, in the same words: one guard per `if`, each answering false, and
# true once none did.
CONDITION_SHAPE = re.compile(
    r"\( context , evt \) "
    r"(?:if (?:(?:- )?\d+ ~= evt \. \w+|evt \. \w+ ~= (?:- )?\d+|@\w+ ~= (?:- )?\d+) then return false end )*"
    r"return true")


def event_name(trig):
    """A trigger's event without the enum's name: "EVENT_ANY_MONSTER_DIE"."""
    return str(trig.get("event") or "").rpartition(".")[2]


class Sim(object):
    def __init__(self, groups, challenge_types, max_events=20000):
        """groups: {group id: parsed script (luagroup.parse_text)}; challenge_types: {challenge id text:
        ChallengeType text} of DungeonChallengeData."""
        self.groups = groups
        self.ctype = challenge_types
        self.vars = {}
        self.active_suites = {}
        self.fired = collections.Counter()
        self.alive = []           # [{"group", "cfg", "tide", "seq"}]
        self.tides = {}           # (group, tide id) -> dict
        self.challenges = {}      # index -> dict
        self.started = []         # every challenge in the order it was started (an index may be started again)
        self.queue = collections.deque()
        self.spawns = []
        self.kills = []
        self.notes = []
        self.tide_failures = []
        self.half = 0
        self.seq = 0
        self.events = 0
        self.max_events = max_events
        self.trig = {}
        for gid, g in self.groups.items():
            t = g["tables"]
            self.vars[gid] = {v["name"]: v.get("value", 0) for v in t.get("variables", [])}
            self.trig[gid] = list(t.get("triggers", []))
            self.active_suites[gid] = []

    # ---- helpers --------------------------------------------------------------------------
    def suites(self, gid):
        return self.groups[gid]["tables"].get("suites", [])

    def live_trigger_names(self, gid):
        names = set()
        su = self.suites(gid)
        for s in self.active_suites[gid]:
            if 1 <= s <= len(su):
                names.update(su[s - 1].get("triggers", []))
        return names

    def count(self, gid):
        return len([m for m in self.alive if m["group"] == gid])

    def spawn(self, gid, cfg, cause, tide=None):
        self.seq += 1
        self.alive.append({"group": gid, "cfg": cfg, "tide": tide, "seq": self.seq})
        self.spawns.append({"seq": self.seq, "half": self.half, "group": gid, "config_id": cfg, "cause": cause})
        self.queue.append(("EVENT_ANY_MONSTER_LIVE", gid, {"param1": cfg}))

    def load_suite(self, gid, s, cause):
        su = self.suites(gid)
        if not (1 <= s <= len(su)):
            self.notes.append("suite %s of group %s does not exist" % (s, gid))
            return
        if s not in self.active_suites[gid]:
            self.active_suites[gid].append(s)
        alive_cfg = set(m["cfg"] for m in self.alive if m["group"] == gid)
        for cfg in su[s - 1].get("monsters", []):
            if cfg in alive_cfg:
                continue
            self.spawn(gid, cfg, cause)
        for cfg in su[s - 1].get("gadgets", []):
            self.queue.append(("EVENT_GADGET_CREATE", gid, {"param1": cfg}))

    def refresh(self, gid, s, cause, won=False):
        """won: the action being run belongs to an EVENT_CHALLENGE_SUCCESS trigger."""
        if gid not in self.groups:
            self.notes.append("RefreshGroup of a group outside the scene: %s" % gid)
            return
        self.alive = [m for m in self.alive if m["group"] != gid]
        for k in [k for k in self.tides if k[0] == gid]:
            del self.tides[k]
        self.active_suites[gid] = []
        t = self.groups[gid]["tables"]
        self.vars[gid] = {v["name"]: v.get("value", 0) for v in t.get("variables", [])}
        for k in [k for k in self.fired if k[0] == gid]:
            del self.fired[k]
        if s == 0 and won and not t.get("monsters"):
            # suite 0 of a group that can spawn nothing, asked for once the challenge is won: the group is
            # unloaded. Anywhere else load_suite notes that it cannot follow.
            return
        self.load_suite(gid, s, cause)

    def tide_fill(self, tide):
        cfgs = tide["cfgs"]
        n = len(cfgs)
        if n == 0 or tide["min"] <= tide["alive"]:
            return
        gid = tide["group"]
        defined = set(m["config_id"] for m in self.groups[gid]["tables"].get("monsters", []))
        for _ in range(100):
            if tide["max"] <= tide["alive"] or tide["spawned"] >= tide["total"]:
                return
            alive_cfg = set(m["cfg"] for m in self.alive if m["group"] == gid)
            chosen = None
            for b in range(n):
                idx = (b + tide["cursor"]) % n
                if cfgs[idx] in alive_cfg:
                    continue
                chosen = cfgs[idx]
                tide["cursor"] = (idx + 1) % n
                break
            if chosen is None:
                self.notes.append("tide %s of group %s: no free config to spawn" % (tide["id"], gid))
                return
            if chosen not in defined:
                # the engine's createMonster fails: the cursor has moved on, this refill ends here
                self.tide_failures.append({"group": gid, "tide_id": tide["id"], "config_id": chosen,
                                           "half": self.half})
                return
            tide["spawned"] += 1
            tide["alive"] += 1
            self.spawn(gid, chosen, tide["cause"], tide=(gid, tide["id"]))

    # ---- conditions / actions -------------------------------------------------------------
    def cond_ok(self, gid, fn, evt):
        f = self.groups[gid]["functions"].get(fn)
        if f is None:
            return True
        for c in f["compares"]:
            if c["op"] != "~=":
                continue
            if c["kind"] == "evt":
                if evt.get(c["field"]) != c["value"]:
                    return False
            elif c["kind"] == "monster_count":
                if self.count(c["group"] or gid) != c["value"]:
                    return False
            elif c["kind"] == "variable":
                if self.vars.get(c["group"] or gid, {}).get(c["name"]) != c["value"]:
                    return False
        return True

    def run_action(self, gid, trig, evt):
        f = self.groups[gid]["functions"].get(trig.get("action") or "")
        if f is None:
            return
        won = event_name(trig) == CHALLENGE_SUCCESS
        for c in f["calls"]:
            fn = c["fn"]
            if fn not in MODELLED_CALLS:
                continue
            a = c["args"][1:]
            cause = {"trigger": trig["name"], "line": c["line"]}
            if fn == "AutoMonsterTide":
                if a[1] not in self.groups:
                    self.notes.append("tide into a group outside the scene: %s" % a[1])
                    continue
                tide = {"id": a[0], "group": a[1], "cfgs": list(a[2]), "total": a[3], "min": a[4], "max": a[5],
                        "spawned": 0, "alive": 0, "dead": 0, "cursor": 0,
                        "cause": dict(cause, kind="tide", tide_id=a[0])}
                self.tides[(a[1], a[0])] = tide
                self.tide_fill(tide)
            elif fn == "CreateMonster":
                self.spawn(gid, a[0].get("config_id"), dict(cause, kind="create"))
            elif fn == "AddExtraGroupSuite":
                if a[0] in self.groups:
                    self.load_suite(a[0], a[1], dict(cause, kind="suite", suite=a[1]))
            elif fn == "RemoveExtraGroupSuite":
                if a[0] in self.groups and a[1] in self.active_suites[a[0]]:
                    self.active_suites[a[0]].remove(a[1])
                    su = self.suites(a[0])
                    gone = set(su[a[1] - 1].get("monsters", [])) if 1 <= a[1] <= len(su) else set()
                    self.alive = [m for m in self.alive if not (m["group"] == a[0] and m["cfg"] in gone)]
            elif fn == "RefreshGroup":
                self.refresh(a[0].get("group_id"), a[0].get("suite"),
                             dict(cause, kind="suite", suite=a[0].get("suite")), won)
            elif fn == "GoBackGroupSuite":
                if a[0] in self.groups:
                    self.refresh(a[0], self.groups[a[0]]["tables"].get("init_config", {}).get("suite", 1),
                                 dict(cause, kind="suite"))
            elif fn == "ActiveChallenge":
                idx, cid = a[0], a[1]
                lay = KILL_CHALLENGE_ARGS.get(self.ctype.get(str(cid)))
                ch = {"index": idx, "challenge_id": cid, "activated_by_group": gid, "kills": 0, "done": False,
                      "half": self.half}
                if not lay:
                    self.notes.append("challenge %s has an unknown type %s" % (cid, self.ctype.get(str(cid))))
                elif all(at < len(a) and _reads("n", a[at]) for at in lay.values()):
                    ch["group"] = a[lay["group"]]
                    ch["count"] = a[lay["count"]]
                else:
                    self.notes.append("challenge %s is started with arguments the run cannot read" % cid)
                self.challenges[idx] = ch
                self.started.append(ch)
            elif fn == "ChangeGroupVariableValue":
                self.vars[gid][a[0]] = self.vars[gid].get(a[0], 0) + a[1]
            elif fn == "SetGroupVariableValue":
                self.vars[gid][a[0]] = a[1]
            elif fn == "SetGroupVariableValueByGroup":
                self.vars.setdefault(a[2], {})[a[0]] = a[1] if isinstance(a[1], int) else 0

    def dispatch(self, event, gid, evt):
        if gid not in self.groups:
            return
        live = self.live_trigger_names(gid)
        for trig in self.trig[gid]:
            if trig.get("name") not in live:
                continue
            if str(trig.get("event")) != "EventType." + event:
                continue
            src = trig.get("source") or ""
            if src != "" and "source" in evt and str(evt["source"]) != src:
                continue
            tc = trig.get("trigger_count", 1)
            key = (gid, trig["name"])
            if tc != 0 and self.fired[key] >= tc:
                continue
            if not self.cond_ok(gid, trig.get("condition") or "", evt):
                continue
            self.fired[key] += 1
            self.run_action(gid, trig, evt)

    def drain(self):
        while self.queue:
            self.events += 1
            if self.events > self.max_events:
                self.notes.append("event budget exhausted")
                self.queue.clear()
                return
            event, gid, evt = self.queue.popleft()
            if event == "EVENT_CHALLENGE_SUCCESS":
                for g in list(self.groups):
                    self.dispatch(event, g, evt)
            else:
                self.dispatch(event, gid, evt)

    def kill_one(self):
        m = self.alive.pop(0)
        self.kills.append({"half": self.half, "group": m["group"], "config_id": m["cfg"], "seq": m["seq"]})
        self.queue.append(("EVENT_ANY_MONSTER_DIE", m["group"], {"param1": m["cfg"]}))
        if m["tide"] is not None and m["tide"] in self.tides:
            tide = self.tides[m["tide"]]
            tide["alive"] -= 1
            tide["dead"] += 1
            self.queue.append(("EVENT_MONSTER_TIDE_DIE", m["group"], {"param1": tide["dead"], "source": tide["id"]}))
            self.tide_fill(tide)
        for ch in self.challenges.values():
            if not ch["done"] and ch.get("group") == m["group"]:
                ch["kills"] += 1
                if ch["kills"] == ch.get("count"):
                    ch["done"] = True
                    self.queue.append(("EVENT_CHALLENGE_SUCCESS", ch["activated_by_group"],
                                       {"source": ch["index"], "param1": ch["index"], "param2": 100}))

    # ---- the run --------------------------------------------------------------------------
    def run(self):
        """-> the number of halves played: each worktop option starts one."""
        for gid, g in self.groups.items():
            s = g["tables"].get("init_config", {}).get("suite", 1)
            self.load_suite(gid, s, {"kind": "suite", "suite": s, "trigger": None, "line": None})
        self.drain()
        used = set()
        for _ in range(6):
            cand = None
            for gid in self.groups:
                live = self.live_trigger_names(gid)
                for trig in self.trig[gid]:
                    if trig.get("name") not in live or str(trig.get("event")) != "EventType.EVENT_SELECT_OPTION":
                        continue
                    if (gid, trig["name"]) in used:
                        continue
                    f = self.groups[gid]["functions"].get(trig.get("condition") or "")
                    evt = {}
                    for c in (f["compares"] if f else []):
                        if c["kind"] == "evt":
                            evt[c["field"]] = c["value"]
                    if self.cond_ok(gid, trig.get("condition") or "", evt):
                        cand = (gid, trig, evt)
                        break
                if cand:
                    break
            if not cand:
                break
            gid, trig, evt = cand
            used.add((gid, trig["name"]))
            self.half += 1
            self.run_action(gid, trig, evt)
            self.drain()
            guard = 0
            while self.alive and guard < 2000:
                guard += 1
                self.kill_one()
                self.drain()
        return self.half


def loaded_triggers(groups):
    """[(group id, trigger)] of the triggers a scene can load -- a suite of their group lists them --, the
    groups in their order, the triggers in script order."""
    out = []
    for gid, g in groups.items():
        tables = g["tables"]
        named = set()
        for suite in tables.get("suites", []):
            named.update(suite.get("triggers", []))
        out.extend((gid, trig) for trig in tables.get("triggers", []) if trig.get("name") in named)
    return out


def _reads(what, value):
    """Whether a call argument is what the run reads at its place (the codes of MODELLED_CALLS)."""
    if what == "n":
        return isinstance(value, int) and not isinstance(value, bool)
    if what == "s":
        return isinstance(value, str)
    if what == "N":
        return isinstance(value, list) and all(_reads("n", v) for v in value)
    return isinstance(value, dict) and all(_reads("n", value.get(field)) for field in what)


def unreplayed(groups):
    """What a run of these groups would miss for certain: [reason], one per call of a function the run acts
    on (MODELLED_CALLS) in the action of a loaded trigger whose event the run never fires. The events that
    end a fight are left out."""
    reasons = []
    for gid, trig in loaded_triggers(groups):
        event = event_name(trig)
        if event in MODELLED_EVENTS or event in END_EVENTS:
            continue
        f = groups[gid]["functions"].get(trig.get("action") or "")
        for c in (f["calls"] if f else []):
            if c["fn"] in MODELLED_CALLS:
                reasons.append("trigger %s of group %s calls %s on %s, which the run does not fire"
                               % (trig.get("name"), gid, c["fn"], event))
    return reasons


def closure(groups):
    """Why a run of these groups would be no proof: [reason], empty when every trigger a suite can load
    stays inside what the run models. Per loaded trigger, the events that end a fight left out:
      * its action is a function of its own script;
      * an event the run does not fire: the action and the condition hold neutral calls only;
      * an event the run fires: the action holds calls the run acts on, with arguments it can read, and
        neutral ones -- also KillEntityByConfigId of a config id that is no monster of the scene, and
        KillGroupEntity once the challenge is won --, and it is the straight list of them the run plays
        (ACTION_SHAPE); the condition, a function of the script too, is what Sim.cond_ok evaluates
        (CONDITION_SHAPE): every `if` one `~=` guard the script reader recognises and nothing else, on a
        field of evt the run fills in.
    A group timer's calls are neutral while every loaded EVENT_TIMER_EVENT action holds nothing but neutral
    and timer calls. Every function of a script is the action or the condition of one of its triggers, and
    those of a loaded trigger name no function of the script: the reader lists the ScriptLib calls of a
    function, not what a function it calls does. And a group starts with the suite its init_config names:
    one that picks at random among several suites starts with whichever."""
    loaded = [(gid, trig, event_name(trig)) for gid, trig in loaded_triggers(groups)]
    monsters = set()
    for g in groups.values():
        table = g["tables"].get("monsters", [])
        for m in (table.values() if isinstance(table, dict) else table):
            if isinstance(m, dict):
                monsters.add(m.get("config_id"))

    def function(gid, name):
        return groups[gid]["functions"].get(name or "")

    neutral = set(NEUTRAL_CALLS)
    timers = [function(gid, trig.get("action")) for gid, trig, event in loaded if event == TIMER_EVENT]
    if all(c["fn"] in NEUTRAL_CALLS or c["fn"] in TIMER_CALLS for f in timers if f for c in f["calls"]):
        neutral.update(TIMER_CALLS)

    reasons = []
    for gid, g in groups.items():
        tables = g["tables"]
        if (tables.get("init_config", {}).get("rand_suite", False) is not False
                and len(tables.get("suites", [])) > 1):
            reasons.append("group %s starts with a suite picked at random" % gid)
        named = set()
        for trig in tables.get("triggers", []):
            named.update((trig.get("action"), trig.get("condition")))
        for name in sorted(set(g["functions"]) - named):
            reasons.append("function %s of group %s belongs to no trigger" % (name, gid))
    for gid, trig, event in loaded:
        if event in END_EVENTS:
            continue
        where = "trigger %s of group %s" % (trig.get("name"), gid)
        action = function(gid, trig.get("action"))
        condition = function(gid, trig.get("condition"))
        if action is None and trig.get("action"):
            reasons.append("%s: its action %s is not in the script" % (where, trig["action"]))
        for kind, f in (("action", action), ("condition", condition)):
            for name in sorted(set(groups[gid]["functions"]).intersection(f["shape"].split() if f else ())):
                reasons.append("%s: its %s names the function %s of the script" % (where, kind, name))
        if event not in MODELLED_EVENTS:
            for fn in sorted(set(c["fn"] for f in (action, condition) if f for c in f["calls"]
                                 if c["fn"] not in neutral)):
                reasons.append("%s calls %s on %s, which the run does not fire" % (where, fn, event))
            continue
        for c in (action["calls"] if action else []):
            fn, a = c["fn"], c["args"][1:]
            if fn in MODELLED_CALLS:
                reads = MODELLED_CALLS[fn]
                if len(a) < len(reads) or not all(_reads(what, v) for what, v in zip(reads, a)):
                    reasons.append("%s calls %s with arguments the run cannot read" % (where, fn))
                continue
            if fn in neutral:
                continue
            if fn == "KillGroupEntity" and event == CHALLENGE_SUCCESS:
                continue
            if (fn == "KillEntityByConfigId" and a and isinstance(a[0], dict)
                    and isinstance(a[0].get("config_id"), int) and a[0]["config_id"] not in monsters):
                continue
            reasons.append("%s calls %s, which the run does not model" % (where, fn))
        if action:
            played = ACTION_SHAPE.match(action["shape"])
            rest = action["shape"][played.end() if played else 0:]
            if rest != ACTION_RESULT:
                reasons.append("%s: its action is not the straight list of calls the run plays, from '%s' on"
                               % (where, rest[:60]))
        if not trig.get("condition"):
            continue
        if condition is None:
            reasons.append("%s: its condition %s is not in the script" % (where, trig["condition"]))
            continue
        guards = [c for c in condition["compares"] if c["op"] == "~="]
        if not (len(guards) == len(condition["compares"]) == condition["returns_false"]
                and sorted(c["text"] for c in guards) == sorted(condition["ifs"])
                and CONDITION_SHAPE.fullmatch(condition["shape"])):
            reasons.append("%s: its condition is not one '~=' guard per 'if', each answering false, and true "
                           "at the end" % where)
        for field in sorted(set(c["field"] for c in guards
                                if c["kind"] == "evt" and c["field"] not in MODELLED_EVENTS[event])):
            reasons.append("%s: its condition reads evt.%s, which the run does not fill in" % (where, field))
    return reasons
