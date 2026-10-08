#!/usr/bin/env python3
"""Builds the agent's gameplay catalogue -- agent/payloads/<version>/tweaks.json -- from a vendor server stack.

The "Gameplay" page edits a server's own data through the agent: the resin cap, the Spiral Abyss line-up,
domain rewards, quest rewards. The agent may only touch what it can prove, so everything it needs to know
about the vendor's data ships with it as one generated file per game version:

    files           md5 of the vendor's copy of every file the feature may rewrite (five tables and every
                    Abyss monster-group script); the agent edits nothing else and nothing that differs
    resin           the row and the vendor's values
    monsters        the monsters an Abyss slot may hold (tier 1 = the vendor's own Abyss uses it), with the
                    pose a slot must carry for the monster and the affixes that belong to it;
                    genericAffixes = the ones that stay on a slot
    abyss           floors -> chambers -> halves: the group script of a half, the slots it really spawns,
                    the waves they come in
    domains         the repeatable statue domains: reward row, cost, the largest multiplier the row tolerates
    items           every item a reward may hold: [cap of one cell (a character card: one), 1 = may be
                    added to a statue row]
    quests          the reward rows quests name, their kind and whether one may be rewritten in place;
                    the daily-commission drop rows; the items a multiplier leaves at their count

The input must be the vendor's untouched data: every file is read through the vendor's own md5 list
(build/tweaks_catalog/stack.py), and the facts the agent relies on are proven while building -- a half whose
replayed kills differ from the script's own count, a slot line the editor could not rewrite, a monster whose
pose the server would refuse, a reward row that could not take the largest multiplier, an item offered for a
statue row the server would not load with it each stop the run with the file named.

Usage (from the repo root):
    python build/make_tweaks_catalog.py --stack 1.6=D:/servers/1.6 --stack 2.8=D:/servers/2.8
    python build/make_tweaks_catalog.py --stack ... --out <folder>    # default: agent/payloads
    python build/make_tweaks_catalog.py --stack ... --check           # write nothing; exit 1 when a shipped
                                                                      #   file is not what the stack yields
    python build/make_tweaks_catalog.py --stack ... --notes           # also list the oddities of the data
Exit code: 0 done, 1 a --check found a difference, 2 the stack cannot yield a catalogue (reason on stderr).
"""
import argparse
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tweaks_catalog import abyss, drops, quests  # noqa: E402
from tweaks_catalog.stack import CatalogError, Stack, to_int  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_OUT = os.path.join(REPO, "agent", "payloads")
NAME = "tweaks.json"
SCHEMA = 1

# The tables the feature rewrites; the Abyss group scripts join them in "files".
TABLES = ("ConstValueData", "TowerLevelData", "DungeonData", "DropTreeData", "RewardData")
# CONST_VALUE_RESIN_PARAM: hard max | recovery cap | minutes per point | resin per refill | refill prices
RESIN_ROW = "134"

ODDITIES = (("halvesSharingOneGroup", "chambers whose two halves spawn from one group script"),
            ("slotsInTwoWaves", "slots spawned by two waves of one half"),
            ("slotsNeverSpawned", "slots a script defines and never spawns (not listed)"),
            ("tideNamesUndefinedSlot", "tide lists naming a slot the group does not define"))


def build_resin(stack):
    t = stack.table("ConstValueData")
    hits = [(line, r) for line, r in t.rows if r[t.col("常量名")] == RESIN_ROW]
    if len(hits) != 1:
        raise CatalogError("%s: row %s found %d times" % (t.rel, RESIN_ROW, len(hits)))
    line, r = hits[0]
    where = "%s:%d" % (t.rel, line)
    hard, cap, minutes, refill = (to_int(r[t.col("常量值%d" % k)], where) for k in (1, 2, 3, 4))
    prices = [to_int(p, where) for p in r[t.col("常量值5")].split(",")]
    # the server's own check of the row
    if hard < cap or not minutes or not refill or 0 in prices:
        raise CatalogError("%s: the resin row is not one the server accepts" % where)
    return {"file": t.rel, "row": RESIN_ROW, "cap": cap, "minutes": minutes, "hardMax": hard}


def build(version, root):
    """-> (catalogue, report) of one stack."""
    stack = Stack(version, root)
    abyss_section, scripts, slots, scenes, oddities = abyss.build(stack)
    monsters, generic, monster_report = abyss.build_monsters(stack, slots, scenes)
    resin = build_resin(stack)
    characters = quests.build_characters(stack)
    items = drops.Items(stack, resin["hardMax"], once=characters)
    db = drops.DropDB(stack)
    domains, domain_report = drops.build_domains(stack, db, items)
    rewards, reward_report = quests.build_rewards(stack, items)
    daily = quests.build_daily(stack, db, items)
    keep = quests.build_keep(stack)
    files = {}
    for rel in ["txt/%s.txt" % name for name in TABLES] + scripts:
        files[Stack.path(rel)] = stack.require_live(rel)
    catalogue = {
        "schema": SCHEMA, "version": version, "files": files,
        "resin": resin,
        "monsters": monsters, "genericAffixes": generic,
        "abyss": abyss_section,
        "domains": domains,
        "items": items.section(),
        "quests": {"file": Stack.path("txt/RewardData.txt"), "rewards": rewards, "daily": daily, "keep": keep},
    }
    check(catalogue, characters)
    chambers = abyss_section["chambers"].values()
    report = {
        "revision": stack.revision(), "files": len(files),
        "floors": len(abyss_section["floors"]), "chambers": len(abyss_section["chambers"]),
        "halves": sum(len(c["halves"]) for c in chambers),
        "slots": len(set((h["group"], cfg) for c in chambers for h in c["halves"] for cfg in h["slots"])),
        "monsters": monster_report, "genericAffixes": generic,
        "domains": domain_report, "items": len(catalogue["items"]), "characters": len(characters),
        "itemsUnderStatue": sum(1 for v in catalogue["items"].values() if v[1]),
        "guaranteePieces": len(items.guarantee), "resinCap": items.cap(drops.RESIN),
        "rewards": reward_report, "dailyDrops": len(daily["drops"]), "keep": len(keep),
        "oddities": oddities, "fromBackup": sorted(Stack.path(rel) for rel in stack.from_backup),
    }
    return catalogue, report


def check(cat, characters):
    """The sections must agree with each other: the agent joins them without looking again. `characters` =
    the character cards (quests.build_characters), each capped at one."""
    files, monsters, items = cat["files"], cat["monsters"], cat["items"]
    floors, chambers = cat["abyss"]["floors"], cat["abyss"]["chambers"]
    named = set((cat["resin"]["file"], cat["abyss"]["levelFile"], cat["domains"]["file"],
                 cat["domains"]["dropFile"], cat["quests"]["file"], cat["quests"]["daily"]["file"]))
    for lid, ch in chambers.items():
        floor = floors.get(str(ch["floor"]))
        if floor is None or int(lid) not in floor["chambers"] or len(ch["halves"]) != floor["teams"]:
            raise CatalogError("chamber %s does not fit its floor" % lid)
        for half in ch["halves"]:
            named.add(half["file"])
            spawned = set(str(cfg) for wave in half["waves"] for cfg, _times in wave)
            if spawned != set(half["slots"]):
                raise CatalogError("chamber %s: the waves and the slots of group %d differ" % (lid, half["group"]))
            for cfg, monster in half["slots"].items():
                if monsters.get(str(monster), {}).get("tier") != 1:
                    raise CatalogError("chamber %s: monster %d of slot %s is not a tier 1 monster"
                                       % (lid, monster, cfg))
    if named != set(files):
        raise CatalogError("the files with an md5 are not the files the sections name: %s"
                           % sorted(named ^ set(files))[:5])
    for mid, m in monsters.items():
        if set(m.get("affix", [])) & set(cat["genericAffixes"]):
            raise CatalogError("monster %s owns a generic affix" % mid)
    for did, d in cat["domains"]["list"].items():
        if d["max"] < drops.AGENT_MULTIPLIER_MAX or d["cost"] < 1:
            raise CatalogError("domain %s cannot be edited" % did)
        for item, _expected in d["preview"]:
            if str(item) not in items:
                raise CatalogError("domain %s previews item %d, which has no cap" % (did, item))
    for item in cat["quests"]["daily"]["items"]:
        if str(item) not in items:
            raise CatalogError("the commission item %d has no cap" % item)
    for item in cat["quests"]["keep"]:
        if str(item) not in items:
            raise CatalogError("item %d keeps its count under a multiplier, but has no cap" % item)
    for item in characters:
        if items.get(str(item), [0])[0] != 1:
            raise CatalogError("the character card %d is not capped at one per cell" % item)


def encode(cat):
    """The file's bytes: compact, keys sorted, one LF at the end -- the same input gives the same bytes."""
    text = json.dumps(cat, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
    return (text + "\n").encode("utf-8")


def describe(report, notes):
    m, d, r = report["monsters"], report["domains"], report["rewards"]
    kinds = sorted(k for k in r if k != "editable")
    lines = [
        "data revision %s" % report["revision"],
        "files %d (%d tables + %d group scripts)" % (report["files"], len(TABLES), report["files"] - len(TABLES)),
        "abyss: %d floors, %d chambers, %d halves, %d spawned slots" % (
            report["floors"], report["chambers"], report["halves"], report["slots"]),
        "monsters %d: tier 1 %d (%d bosses), tier 2 %d; %d with a pose, %d with affixes of their own; "
        "%d left out for want of a pose the server accepts" % (
            m["tier1"] + m["tier2"], m["tier1"], m["bosses"], m["tier2"], m["withPose"], m["withAffix"],
            m["noPose"]),
        "affixes of Abyss slots: generic %s; tied to a monster %s" % (report["genericAffixes"], m["specificAffixes"]),
        "domains %d: %s" % (sum(d.values()), ", ".join("%d %s" % (d[k], k) for k in sorted(d))),
        "items %d (%d character cards, one per cell; %d may be added to a statue row; %d guarantee reliquaries "
        "may not); resin cap %d" % (
            report["items"], report["characters"], report["itemsUnderStatue"], report["guaranteePieces"],
            report["resinCap"]),
        "quest rewards %d (%d editable in place): %s" % (
            sum(r[k] for k in kinds), r.get("editable", 0), ", ".join("%d %s" % (r[k], k) for k in kinds)),
        "daily-commission drops %d; %d items keep their count under a multiplier" % (
            report["dailyDrops"], report["keep"]),
    ]
    for key, text in ODDITIES:
        lines.append("%s: %d" % (text, len(report["oddities"][key])))
        if notes:
            lines.extend("    " + x for x in report["oddities"][key])
    if report["fromBackup"]:
        lines.append("read from the .orig copy beside them (the live file is not the vendor's): %s"
                     % ", ".join(report["fromBackup"]))
    return lines


def parse_stacks(values):
    stacks = []
    for value in values:
        version, sep, root = value.partition("=")
        if not sep or not re.match(r"^\d+\.\d+$", version) or not root:
            raise CatalogError("--stack wants <version>=<stack folder>, e.g. 1.6=D:/servers/1.6 (got %r)" % value)
        if version in [v for v, _ in stacks]:
            raise CatalogError("--stack names version %s twice" % version)
        stacks.append((version, root))
    return stacks


def main(argv):
    ap = argparse.ArgumentParser(description="Build agent/payloads/<version>/%s from a vendor server stack." % NAME)
    ap.add_argument("--stack", action="append", required=True, metavar="VERSION=FOLDER",
                    help="a stack to read, e.g. 1.6=D:/servers/1.6 (repeatable)")
    ap.add_argument("--out", default=DEFAULT_OUT, metavar="FOLDER",
                    help="where <version>/%s goes (default: agent/payloads)" % NAME)
    ap.add_argument("--check", action="store_true",
                    help="write nothing; exit 1 when a shipped file differs from what the stack yields")
    ap.add_argument("--notes", action="store_true", help="list the oddities of the vendor data")
    args = ap.parse_args(argv)
    stale = 0
    try:
        for version, root in parse_stacks(args.stack):
            print("== %s  (%s)" % (version, root), flush=True)
            catalogue, report = build(version, root)
            data = encode(catalogue)
            for line in describe(report, args.notes):
                print("   " + line)
            path = os.path.join(args.out, version, NAME).replace(os.sep, "/")
            if args.check:
                try:
                    with open(path, "rb") as f:
                        verdict = "ok" if f.read() == data else "DIFFERS"
                except OSError:
                    verdict = "MISSING"
                stale += verdict != "ok"
                print("%-8s %s  (%d bytes expected)" % (verdict, path, len(data)))
            else:
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, "wb") as f:
                    f.write(data)
                print("-> %s  (%d bytes)" % (path, len(data)))
    except CatalogError as e:
        print("make_tweaks_catalog: %s" % e, file=sys.stderr)
        return 2
    return 1 if stale else 0


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    sys.exit(main(sys.argv[1:]))
