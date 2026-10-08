"""Symbolic run of one Spiral Abyss scene's group scripts.

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
"""
import collections

KILL_CHALLENGE_ARGS = {
    # ChallengeType -> positions (after context) of the group and the kill count
    "2": {"group": 3, "count": 4},     # ActiveChallenge(ctx, index, id, seconds, group, count, 0)
    "10": {"group": 2, "count": 3},    # ActiveChallenge(ctx, index, id, group, count, guarded gadget, 0)
}


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

    def refresh(self, gid, s, cause):
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
        for c in f["calls"]:
            fn = c["fn"]
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
                             dict(cause, kind="suite", suite=a[0].get("suite")))
            elif fn == "GoBackGroupSuite":
                if a[0] in self.groups:
                    self.refresh(a[0], self.groups[a[0]]["tables"].get("init_config", {}).get("suite", 1),
                                 dict(cause, kind="suite"))
            elif fn == "ActiveChallenge":
                idx, cid = a[0], a[1]
                lay = KILL_CHALLENGE_ARGS.get(self.ctype.get(str(cid)))
                ch = {"index": idx, "challenge_id": cid, "activated_by_group": gid, "kills": 0, "done": False,
                      "half": self.half}
                if lay:
                    ch["group"] = a[lay["group"]]
                    ch["count"] = a[lay["count"]]
                else:
                    self.notes.append("challenge %s has an unknown type %s" % (cid, self.ctype.get(str(cid))))
                self.challenges[idx] = ch
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
