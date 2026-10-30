#!/usr/bin/env python3
"""Builds the agent's gameplay catalogue -- agent/payloads/<version>/tweaks.json -- from a vendor server stack.

The "Gameplay" page edits a server's own data through the agent: the resin cap, the Spiral Abyss line-up,
the enemies of a domain, domain rewards, quest rewards. The agent may only touch what it can prove, so
everything it needs to know about the vendor's data ships with it as one generated file per game version:

    files           md5 of the vendor's copy of every file the feature may rewrite (five tables and every
                    monster-group script of the Abyss and of the domain line-ups); the agent edits nothing
                    else and nothing that differs
    resin           the row and the vendor's values
    monsters        the monsters a spawn slot may hold (tier 1 = the vendor's own Abyss uses it), with the
                    pose a slot must carry for the monster and the affixes that belong to it;
                    genericAffixes = the ones that stay on a slot
    abyss           floors -> chambers -> halves: the group script of a half, the slots it really spawns,
                    the waves they come in
    domains         the repeatable statue domains: reward row, cost, the largest multiplier the row tolerates,
                    the level the domain is shown with (its enemy level cell + 1);
                    lineups = the monster group a domain's fight spawns from, one per statue scene (the
                    weekday variants of one difficulty share it): its script, the slots the fight can spawn
                    and -- only where the order of the fight is proven -- the waves they come in; level =
                    the largest level one of its entries carries; top = the largest level anything the
                    scene places carries, a monster of that group or a gadget of any group the scene loads
                    (the game adds a domain's enemy level cell to all of them and the growth curves end,
                    so top bounds the enemy level of the line-up's domains)
    items           every item a reward may hold: [cap of one cell (a character card: one), 1 = may be
                    added to a statue row]
    quests          the reward rows quests and hangout events name: their kind, the quests that name them
                    (a hangout row: its chapter, and as `tier` the number of the chapter's endings it asks
                    for) and whether one may be rewritten in place; the daily-commission drop rows; the
                    items a multiplier leaves at their count

Beside it goes agent/payloads/<version>/tweaks-originals.tar.gz: the vendor's bytes of every file of `files`,
for the agent to put a file back to the original whatever became of the stack's own copy (ORIGINALS). It is
written only when its members -- their names and md5, in their order, in the form encode_originals gives them
-- are not those of the file already there, or that file decompressed is not the very tar encode_originals
writes for them: the compressed bytes differ between the zlib builds of two platforms, and the build identity
of the agent hashes them.

The input must be the vendor's untouched data: every file is read through the vendor's md5 list
(build/tweaks_catalog/stack.py) -- the stack's own server/data/server_data_version_md5_list.txt, or, for a
stack whose vendor ships none, the list of the vendor's data archive (VENDOR_DATA, --vendor-data). And the
facts the agent relies on are proven while building -- a half whose replayed kills differ from the script's
own count, a slot line the editor could not rewrite, a statue scene whose scripts name a monster anywhere
but in the slot lines of its one monster group or give a level anywhere but in the entries of a monsters or
a gadgets table, a monster of such a scene -- or a gadget of it that names growth curves -- one of whose
curves is missing on a level an enemy level can put it at, a monster whose pose the server would refuse, a
reward row that could not take the largest multiplier, an item offered for a statue row the server would
not load with it, a hangout reward not claimed on the number of its own chapter's endings reached, the tiers
of a hangout chapter that are not 1 up to its number of endings, each once, each stop the run with the file
named.

Usage (from the repo root):
    python build/make_tweaks_catalog.py --stack 1.6=D:/servers/1.6 --stack 2.8=D:/servers/2.8
    python build/make_tweaks_catalog.py --stack 3.2=D:/servers/3.2 --vendor-data 3.2=D:/data/welink_3.2_qa
                                                                      # a stack whose vendor ships no md5
                                                                      #   list: the folder the vendor's data
                                                                      #   archive was extracted into
    python build/make_tweaks_catalog.py --stack ... --out <folder>    # default: agent/payloads
    python build/make_tweaks_catalog.py --stack ... --check           # write nothing; exit 1 when a shipped
                                                                      #   file is not what the stack yields:
                                                                      #   the catalogue byte for byte, the
                                                                      #   archive of the originals member by
                                                                      #   member
    python build/make_tweaks_catalog.py --stack ... --notes           # also list the oddities of the data
    python build/make_tweaks_catalog.py --stack ... --mutations       # write nothing; hold the proofs
                                                                      #   against copies of the stack's
                                                                      #   files -- and of the vendor data's
                                                                      #   md5 list, and of the archive of the
                                                                      #   originals -- edited in memory, exit
                                                                      #   1 when an edit is not caught
Exit code: 0 done, 1 a --check found a difference or a --mutations case was not caught, 2 the stack cannot
yield a catalogue, a --vendor-data folder is not vendor data the generator knows, or a catalogue cannot be
written (reason on stderr).
"""
import argparse
import gzip
import hashlib
import io
import itertools
import json
import os
import re
import sys
import tarfile
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tweaks_catalog import abyss, drops, lineups, mutations, quests  # noqa: E402
from tweaks_catalog.stack import CatalogError, Stack, data_without_list, is_int, to_int  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_OUT = os.path.join(REPO, "agent", "payloads")
NAME = "tweaks.json"
SCHEMA = 1
# The vendor's bytes of every file of the catalogue, beside it: a gzip-compressed POSIX tar whose members are
# exactly the paths of `files`, sorted, each a regular file with the mode MEMBER_MODE, the time 0, the owner and
# group 0 without names, and the md5 the catalogue pins; the gzip header holds the time 0 and no file name.
# gzip, never xz: zlib is in every Python the agent runs on, the lzma module is optional. And never a .zip: a zip
# in a version folder of the payloads is a navmesh bundle to the agent.
ORIGINALS = "tweaks-originals.tar.gz"
MEMBER_MODE = 0o644
# What the reader of a shipped archive takes for one it cannot read: a damaged stream or tar, a cut file.
_ARCHIVE_ERRORS = (OSError, EOFError, zlib.error, tarfile.TarError)

# The tables the feature rewrites; the group scripts of the Abyss and of the line-ups join them in "files".
TABLES = ("ConstValueData", "TowerLevelData", "DungeonData", "DropTreeData", "RewardData")
# CONST_VALUE_RESIN_PARAM: hard max | recovery cap | minutes per point | resin per refill | refill prices
RESIN_ROW = "134"

# Vendor data the generator knows, for a stack whose vendor ships no md5 list of its own: the archive the vendor
# published, keyed by its md5 -- name, size and top folder say which download a folder must come from; "list" is
# the sha256 of the md5 list of its files (LIST_FORM), the proof the generator takes in place of the stack's own
# list; "files" / "bytes" say the same in numbers, for the error about a folder that is not this data.
VENDOR_DATA = {
    "737f850ec9d1dc8fec1a4c239a149cf3": {
        "name": "welink_3.2_qa.7z", "size": 17466687, "top": "welink_3.2_qa", "files": 33381, "bytes": 268098330,
        "list": "fbdc4ec6bdbecbbf5658114bd4abacfeb6951dfb29235f50ae4dac31acf34e23"},
}
# LIST_FORM: one line "<md5>  ./<path>" per file below the archive's top folder -- the md5 in lower-case hex, two
# spaces, "./", the path with "/" between its parts --, the lines in the code-point order of the paths, an LF after
# every line, the last one included, UTF-8. The list is derived from the folder at every run and never written.

# derive_vendor_list hashes a file this many bytes at a time.
_CHUNK = 1 << 20

# The agent's script and the line that sets the largest request body it reads. The Gameplay page sends its
# whole document in one request: one that changes every slot the catalogue lists and sets the enemy level of
# every domain with a line-up has to fit BODY_SHARE times over.
AGENT_SCRIPT = "agent/gio_agent.py"
_BODY_LIMIT = re.compile(r"(?m)^MAX_BODY = (\d+)(?: (<<|\*) (\d+))?[ \t]*(?:#.*)?$")
BODY_SHARE = 4
# The agent takes an id written as the key of an object -- a group, a spawn slot -- with this many digits
# at most.
ID_DIGITS = 18

ODDITIES = (("halvesSharingOneGroup", "chambers whose two halves spawn from one group script"),
            ("slotsInTwoWaves", "Abyss slots spawned by two waves of one half"),
            ("slotsNeverSpawned", "Abyss slots a script defines and never spawns (not listed)"),
            ("tideNamesUndefinedSlot", "Abyss tide lists naming a slot the group does not define"))
# The same for the domain line-ups: the report lists of lineups.build, and the listed slots whose own
# monster is none of "monsters" -- such a slot is listed and edited like any other; its monster is only
# not one that may be placed in another slot.
LINEUP_ODDITIES = (("flatScenes", "domain scenes listed without waves"),
                   ("slotsNotListed", "line-up slots a script defines and the line-up does not list"),
                   ("wavesSharingOneSlot", "line-up slots spawned by two waves"),
                   ("slotLevels", "line-up slots whose own level is not %d" % abyss.SLOT_LEVEL),
                   ("visionLevels", "line-up slots that carry a vision_level"),
                   ("topLevels", "domain scenes in which a monster or a gadget carries a level above %d"
                    % abyss.SLOT_LEVEL),
                   ("monstersNotOffered", "line-up slots whose monster is not one of the catalogue's monsters"))


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


def build(version, root, vendor=None):
    """-> (catalogue, report, originals) of one stack: originals = the members of ORIGINALS (originals()).
    `vendor` = what vendor_data gives, for a stack that ships no md5 list of its own."""
    stack = Stack(version, root, *(vendor[:2] if vendor else ()))
    abyss_section, scripts, slots, scenes, oddities = abyss.build(stack)
    resin = build_resin(stack)
    characters = quests.build_characters(stack)
    items = drops.Items(stack, resin["hardMax"], once=characters)
    db = drops.DropDB(stack)
    domains, domain_report = drops.build_domains(stack, db, items)
    domains["lineups"], lineup_scripts, lineup_slots, lineup_oddities = lineups.build(stack, domains["list"])
    monsters, generic, monster_report = abyss.build_monsters(stack, slots, scenes, lineup_slots)
    rewards, reward_report = quests.build_rewards(stack, items)
    daily = quests.build_daily(stack, db, items)
    keep = quests.build_keep(stack)
    files = {}
    for rel in ["txt/%s.txt" % name for name in TABLES] + scripts + lineup_scripts:
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
    listed = domains["lineups"].values()
    lineup_oddities["monstersNotOffered"] = [
        "scene %d: slot %d of group %d (monster %d)" % (s["scene"], s["cfg"], s["group"], s["monster"])
        for s in lineup_slots if s["listed"] and str(s["monster"]) not in monsters]
    report = {
        "revision": stack.revision(), "proof": stack.proof, "files": len(files), "scripts": len(scripts),
        "lineupScripts": len(lineup_scripts),
        "floors": len(abyss_section["floors"]), "chambers": len(abyss_section["chambers"]),
        "halves": sum(len(c["halves"]) for c in chambers),
        "slots": len(set((h["group"], cfg) for c in chambers for h in c["halves"] for cfg in h["slots"])),
        "monsters": monster_report, "genericAffixes": generic,
        "domains": domain_report, "items": len(catalogue["items"]), "characters": len(characters),
        "lineups": len(domains["lineups"]), "lineupsWithWaves": sum(1 for e in listed if "waves" in e),
        "lineupDungeons": sum(len(e["dungeons"]) for e in listed),
        "lineupSlots": sum(len(e["slots"]) for e in listed),
        "levelEnds": sorted(lineups.level_range(e["top"])[1] for e in listed),
        "widestDocument": len(widest_document(catalogue)), "bodyLimit": agent_body_limit(),
        "itemsUnderStatue": sum(1 for v in catalogue["items"].values() if v[1]),
        "guaranteePieces": len(items.guarantee), "resinCap": items.cap(drops.RESIN),
        "rewards": reward_report, "dailyDrops": len(daily["drops"]), "keep": len(keep),
        "oddities": oddities, "lineupOddities": lineup_oddities,
        "fromBackup": sorted(Stack.path(rel) for rel in stack.from_backup),
    }
    return catalogue, report, originals(stack, files)


def originals(stack, files):
    """-> [(path, bytes)]: the vendor's copy of every file of the catalogue (`files`: {path: md5}), sorted by
    path -- the members of ORIGINALS. Each read through the stack's proof again: the live file itself, with the
    md5 the catalogue pins."""
    members = []
    for path in sorted(files):
        rel = path[len(Stack.path("")):]
        raw = stack.read(rel)
        if rel in stack.from_backup or hashlib.md5(raw).hexdigest() != files[path]:
            raise CatalogError("%s: not the file whose md5 the catalogue pins" % path)
        members.append((path, raw))
    return members


def _write_originals_tar(fileobj, members):
    """The POSIX tar of ORIGINALS for these members [(path, bytes)], in their order, written into `fileobj`: each a
    regular file with the mode MEMBER_MODE, the time 0, the owner and group 0 without names."""
    with tarfile.open(fileobj=fileobj, mode="w", format=tarfile.PAX_FORMAT) as tar:
        for path, raw in members:
            info = tarfile.TarInfo(path)
            info.type = tarfile.REGTYPE
            info.size, info.mode, info.mtime = len(raw), MEMBER_MODE, 0
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            tar.addfile(info, io.BytesIO(raw))


def encode_originals(members):
    """The bytes of ORIGINALS for these members [(path, bytes)], in their order. The output of one zlib build is
    the same for the same members; another zlib build may compress them to other bytes (originals_state)."""
    buf = io.BytesIO()
    with gzip.GzipFile(fileobj=buf, mode="wb", mtime=0, filename="") as gz:
        _write_originals_tar(gz, members)
    return buf.getvalue()


def originals_tar(members):
    """What ORIGINALS holds uncompressed for these members: the tar encode_originals compresses -- the same bytes on
    every platform (only the compression depends on the zlib build)."""
    buf = io.BytesIO()
    _write_originals_tar(buf, members)
    return buf.getvalue()


def read_originals(raw):
    """The bytes of an ORIGINALS file -> ([(member name, md5)] in their order, [what of its form is not the form
    encode_originals gives]). What the gzip or the tar reader raises is the caller's (_ARCHIVE_ERRORS)."""
    problems = []
    # the gzip header: magic, deflate, no flag (no file name, no extra field, no comment), the time 0
    if len(raw) < 10 or raw[:3] != b"\x1f\x8b\x08":
        problems.append("not a gzip file of deflate data")
    elif raw[3] != 0:
        problems.append("its gzip header carries the flags %d (a file name, a comment, an extra field)" % raw[3])
    elif raw[4:8] != b"\0\0\0\0":
        problems.append("its gzip header carries a time")
    members = []
    with tarfile.open(fileobj=io.BytesIO(raw), mode="r:gz") as tar:
        for info in tar:
            if not info.isreg():
                problems.append("%s is not a regular file" % info.name)
                continue
            shape = (info.mode, info.mtime, info.uid, info.gid, info.uname, info.gname)
            if shape != (MEMBER_MODE, 0, 0, 0, "", ""):
                problems.append("%s has the mode, time, owner and group %r" % (info.name, shape))
            f = tar.extractfile(info)
            members.append((info.name, hashlib.md5(f.read()).hexdigest()))
    return members, problems


def originals_state(path, files):
    """Whether the file at `path` is the ORIGINALS of these members -- files = [(path, bytes)], sorted
    (originals_judge) -- (True, what it holds) or (False, why not)."""
    try:
        with open(path, "rb") as f:
            raw = f.read()
    except FileNotFoundError:
        return False, "missing"
    except OSError as e:
        return False, "cannot be read (%s)" % e
    return originals_judge(raw, files)


def originals_judge(raw, files):
    """Whether these bytes are the ORIGINALS of these members -- files = [(path, bytes)], sorted -- judged by what
    they hold, never by their compressed form: the gzip header, the members (names, md5s, form) and, decompressed
    whole -- every gzip member, its CRC and length checked, nothing after it --, the very tar encode_originals
    writes for them (a pax or GNU header, a member after the end of the archive, a second tar are not). (True,
    what they hold) or (False, why not). Pure."""
    want = [(member, hashlib.md5(data).hexdigest()) for member, data in files]
    try:
        members, problems = read_originals(raw)
    except _ARCHIVE_ERRORS as e:
        return False, "cannot be read as a gzip-compressed tar (%s)" % e
    if problems:
        return False, "%s of its form, the first: %s" % (_count(len(problems), "problem"), problems[0])
    if members != want:
        names, wanted = [m[0] for m in members], [w[0] for w in want]
        if names != wanted:
            gone, more = sorted(set(wanted) - set(names)), sorted(set(names) - set(wanted))
            return False, ("its members are not the catalogue's files: %d missing%s, %d more%s%s" % (
                len(gone), " (%s)" % gone[0] if gone else "", len(more), " (%s)" % more[0] if more else "",
                "" if gone or more else ", the same names in another order or twice"))
        other = [name for (name, md5), (_name, want_md5) in zip(members, want) if md5 != want_md5]
        return False, "%s not the vendor's file, the first: %s" % (
            _count(len(other), "member is", "members are"), other[0])
    try:
        whole = gzip.decompress(raw)
    except _ARCHIVE_ERRORS as e:
        return False, "cannot be read as one gzip stream (%s)" % e
    if whole != originals_tar(files):
        return False, ("decompressed, it is not the tar of those members as encode_originals writes it (%d bytes for "
                       "%d): a header of another form, a member after the end of the archive, a second archive"
                       % (len(whole), len(originals_tar(files))))
    return True, "%d members, each the vendor's file the catalogue pins, %d bytes" % (len(members), len(raw))


def originals_cases(files):
    """--mutations for ORIGINALS: copies of the archive the generator writes for these members, changed in memory,
    against originals_judge -- each one it must refuse, and the same members compressed at another level, which it
    must take (two zlib builds differ in exactly that way) -> [(case, as stated)]."""
    good, plain = encode_originals(files), originals_tar(files)
    extra = io.BytesIO()
    _write_originals_tar(extra, [("server/data/txt/Extra.txt", b"x\r\n")])

    def changed(at, data=good):
        out = bytearray(data)
        out[at] ^= 0xFF
        return bytes(out)

    def form(fmt, pax=None):
        buf = io.BytesIO()
        with gzip.GzipFile(fileobj=buf, mode="wb", mtime=0, filename="") as gz:
            with tarfile.open(fileobj=gz, mode="w", format=fmt) as tar:
                for n, (path, raw) in enumerate(files):
                    info = tarfile.TarInfo(path)
                    info.size, info.mode, info.mtime, info.uname, info.gname = len(raw), MEMBER_MODE, 0, "", ""
                    if pax and n == 0:
                        info.pax_headers = dict(pax)
                    tar.addfile(info, io.BytesIO(raw))
        return buf.getvalue()

    cases = [("the same members compressed at another level", gzip.compress(plain, compresslevel=1, mtime=0), True),
             ("a time in the gzip header", gzip.compress(plain, mtime=5), False),
             ("bytes after the gzip stream", good + b"garbage", False),
             ("a second gzip stream holding another tar", good + gzip.compress(extra.getvalue(), mtime=0), False),
             ("the CRC of the gzip trailer changed", changed(-8), False),
             ("the length of the gzip trailer changed", changed(-1), False),
             ("a member after the end of the archive", gzip.compress(plain + extra.getvalue(), mtime=0), False),
             ("a pax header on the first member", form(tarfile.PAX_FORMAT, {"comment": "x"}), False),
             ("the GNU tar format", form(tarfile.GNU_FORMAT), False)]
    return [(case, originals_judge(data, files)[0] == taken) for case, data, taken in cases]


def _count(n, one, more=None):
    """"1 problem" / "2 problems"."""
    return "%d %s" % (n, one if n == 1 else more or one + "s")


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
    check_lineups(cat)
    named.update(lineup["file"] for lineup in cat["domains"]["lineups"].values())
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
    size, limit = len(widest_document(cat)), agent_body_limit()
    if size * BODY_SHARE >= limit:
        raise CatalogError("a document that changes every listed slot and sets the enemy level of every domain "
                           "takes %d bytes: more than 1/%d of the %d the agent reads of a request (MAX_BODY in %s)"
                           % (size, BODY_SHARE, limit, AGENT_SCRIPT))


def _whole(value, least=0):
    return isinstance(value, int) and not isinstance(value, bool) and value >= least


def _plain_id(text):
    """A key written as the agent takes an id: decimal digits, ID_DIGITS at most, no sign, no leading zero."""
    return isinstance(text, str) and is_int(text) and len(text) <= ID_DIGITS and str(int(text)) == text


def check_lineups(cat):
    """The domain line-ups, held to what the agent asks of them before it uses a catalogue: a script that
    is one of `files`; a group and a script that belong to each other alone, the groups of the Abyss counted
    with the line-ups (a document names a slot by group id and config id); a level of 1 or more; dungeons
    that are listed domains, each in this line-up alone; at least one slot, each with a monster; waves --
    where there are any -- of slots the line-up lists, each spawned once or more.

    And to what the generator promises on top: every listed domain has a line-up; the waves of a line-up
    spawn exactly its slots; a line-up has a `top`, a whole number that is its `level` at the least (the
    monsters of the line-up are among what its scene places); and the level each of its domains is shown
    with -- the vendor's own enemy level -- is one of those the domain can be set to (lineups.level_range of
    that `top`: the enemy level cell is 1 at the least, and the entry that carries `top` stays on the last
    level the growth curves are proven for). So the range the agent offers for a domain is never empty,
    the original is inside it, and the highest entry of the scene stands on the curves as the data comes."""
    files, domains = cat["files"], cat["domains"]
    taken = {}
    for lid, ch in cat["abyss"]["chambers"].items():
        for half in ch["halves"]:
            taken[half["group"]] = taken[half["file"]] = "chamber %s" % lid
    lineup_of = {}
    for gid, lineup in domains["lineups"].items():
        where = "%s (the line-up of group %s)" % (lineup.get("file"), gid)
        if lineup.get("file") not in files:
            raise CatalogError("%s: the script has no md5 among the files" % where)
        if not _plain_id(gid) or int(gid) < 1:
            raise CatalogError("%s: the group id is not a plain number" % where)
        other = taken.get(int(gid)) or taken.get(lineup["file"])
        if other:
            raise CatalogError("%s: the group or its script belongs to %s as well" % (where, other))
        taken[int(gid)] = taken[lineup["file"]] = "the line-up of group %s" % gid
        if not _whole(lineup.get("level"), 1):
            raise CatalogError("%s: the largest level its entries carry is %r, a slot has a level of 1 or more"
                               % (where, lineup.get("level")))
        top = lineup.get("top")
        if not _whole(top, lineup["level"]):
            raise CatalogError("%s: the largest level anything of its scene carries is %r, its own entries "
                               "carry up to %d" % (where, top, lineup["level"]))
        lowest, highest = lineups.level_range(top)
        if highest < lowest:
            raise CatalogError("%s: an entry of the scene carries the level %d, which leaves its domains no enemy "
                               "level" % (where, top))
        dungeons = lineup.get("dungeons")
        if not isinstance(dungeons, list) or not dungeons or not all(_whole(d) for d in dungeons):
            raise CatalogError("%s: the line-up names no dungeon" % where)
        for dungeon in dungeons:
            if str(dungeon) not in domains["list"]:
                raise CatalogError("%s: dungeon %d is not a listed domain" % (where, dungeon))
            if dungeon in lineup_of:
                raise CatalogError("%s: dungeon %d is named twice or has the line-up of group %s as well"
                                   % (where, dungeon, lineup_of[dungeon]))
            lineup_of[dungeon] = gid
            shown = domains["list"][str(dungeon)]["level"]
            if not lowest <= shown <= highest:
                raise CatalogError("%s: dungeon %d is shown with level %d, outside the %d..%d its enemy level "
                                   "can be set to while an entry of the scene carries the level %d"
                                   % (where, dungeon, shown, lowest, highest, top))
        slots = lineup.get("slots")
        if (not isinstance(slots, dict) or not slots
                or not all(_plain_id(cfg) and _whole(monster, 1) for cfg, monster in slots.items())):
            raise CatalogError("%s: the line-up lists no slot, or a slot without a monster" % where)
        if "waves" not in lineup:
            continue
        waves = lineup["waves"]
        if (not isinstance(waves, list) or not waves
                or not all(isinstance(wave, list) and wave
                           and all(isinstance(s, list) and len(s) == 2 and _whole(s[0]) and _whole(s[1], 1)
                                   for s in wave) for wave in waves)):
            raise CatalogError("%s: the waves are not lists of [config id, times], each with a slot" % where)
        if set(str(cfg) for wave in waves for cfg, _times in wave) != set(slots):
            raise CatalogError("%s: the waves and the slots differ" % where)
    without = sorted(set(domains["list"]) - set(str(dungeon) for dungeon in lineup_of), key=int)
    if without:
        raise CatalogError("%s: the domains %s have no line-up" % (domains["file"], ", ".join(without[:5])))


def widest_document(cat):
    """The request that changes every slot the catalogue lists -- of the Abyss and of the line-ups -- and
    sets the enemy level of every domain with a line-up, as the bytes of POST /server/tweaks: each slot
    given the monster id that takes the most digits, each domain the highest level its line-up leaves it
    (lineups.level_range; no level of the range takes more digits)."""
    monster = int(max(cat["monsters"], key=len))
    tower = {}
    for ch in cat["abyss"]["chambers"].values():
        for half in ch["halves"]:
            tower.setdefault(str(half["group"]), {}).update((cfg, monster) for cfg in half["slots"])
    arenas = dict((gid, dict((cfg, monster) for cfg in lineup["slots"]))
                  for gid, lineup in cat["domains"]["lineups"].items())
    levels = dict((str(dungeon), {"level": lineups.level_range(lineup["top"])[1]})
                  for lineup in cat["domains"]["lineups"].values() for dungeon in lineup["dungeons"])
    document = {"abyss": {"slots": tower}, "domains": {"slots": arenas, "byId": levels}}
    return json.dumps({"version": cat["version"], "tweaks": document}).encode("utf-8")


def agent_body_limit():
    """The largest request body the agent reads, in bytes: what its script gives MAX_BODY. The line is read
    as text -- the generator never runs the agent."""
    path = os.path.join(REPO, *AGENT_SCRIPT.split("/"))
    try:
        with open(path, "r", encoding="utf-8") as f:
            found = _BODY_LIMIT.findall(f.read())
    except (OSError, UnicodeDecodeError) as e:
        raise CatalogError("%s: cannot be read (%s)" % (AGENT_SCRIPT, e))
    if len(found) != 1:
        raise CatalogError("%s: MAX_BODY is not set once, as a number, a shift or a product" % AGENT_SCRIPT)
    left, op, right = found[0]
    if op == "<<":
        return int(left) << int(right)
    return int(left) * int(right) if op else int(left)


def encode(cat):
    """The file's bytes: compact, keys sorted, one LF at the end -- the same input gives the same bytes."""
    text = json.dumps(cat, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
    return (text + "\n").encode("utf-8")


def put(path, data):
    """Writes a catalogue or its archive of the originals so that it appears whole: beside itself first, then
    renamed over the file. Whoever reads the shipped file meanwhile -- the agent's self-test does -- finds the
    previous one or this one."""
    part = path + ".part"
    try:
        with open(part, "wb") as f:
            f.write(data)
        os.replace(part, path)
    finally:
        if os.path.exists(part):
            os.remove(part)


def describe(report, notes):
    m, d, r = report["monsters"], report["domains"], report["rewards"]
    kinds = sorted(k for k in r if k != "editable")
    # the enemy level a domain takes: up to the end of the curves, or -- its scene holding an entry above
    # the usual slot level -- that much less
    lowest, full = lineups.level_range(abyss.SLOT_LEVEL)
    sooner = [end for end in report["levelEnds"] if end < full]
    lines = [
        "data revision %s -- every file proven against %s" % (
            report["revision"] if report["revision"] != "?" else "none listed", report["proof"]),
        "files %d (%d tables + %d Abyss group scripts + %d line-up scripts)" % (
            report["files"], len(TABLES), report["scripts"], report["lineupScripts"]),
        "abyss: %d floors, %d chambers, %d halves, %d spawned slots" % (
            report["floors"], report["chambers"], report["halves"], report["slots"]),
        "monsters %d: tier 1 %d (%d bosses), tier 2 %d; %d with a pose, %d with affixes of their own; "
        "%d left out for want of a pose the server accepts" % (
            m["tier1"] + m["tier2"], m["tier1"], m["bosses"], m["tier2"], m["withPose"], m["withAffix"],
            m["noPose"]),
        "affixes of Abyss and line-up slots: generic %s; tied to a monster %s" % (
            report["genericAffixes"], m["specificAffixes"]),
        "domains %d: %s" % (sum(d.values()), ", ".join("%d %s" % (d[k], k) for k in sorted(d))),
        "domain line-ups %d (%d with waves, %d flat) for %d domains, %d listed slots" % (
            report["lineups"], report["lineupsWithWaves"], report["lineups"] - report["lineupsWithWaves"],
            report["lineupDungeons"], report["lineupSlots"]),
        "the enemy level of a domain: %d..%d; %s" % (
            lowest, full,
            "%d of the %d line-ups hold an entry above level %d and end sooner, the first at %d" % (
                len(sooner), report["lineups"], abyss.SLOT_LEVEL, min(sooner)) if sooner
            else "no line-up holds an entry above level %d" % abyss.SLOT_LEVEL),
        "a document that changes every listed slot and sets every enemy level: %d bytes (the agent reads %d of "
        "a request)" % (report["widestDocument"], report["bodyLimit"]),
        "items %d (%d character cards, one per cell; %d may be added to a statue row; %d guarantee reliquaries "
        "may not); resin cap %d" % (
            report["items"], report["characters"], report["itemsUnderStatue"], report["guaranteePieces"],
            report["resinCap"]),
        "quest rewards %d (%d editable in place): %s" % (
            sum(r[k] for k in kinds), r.get("editable", 0), ", ".join("%d %s" % (r[k], k) for k in kinds)),
        "daily-commission drops %d; %d items keep their count under a multiplier" % (
            report["dailyDrops"], report["keep"]),
    ]
    for texts, found in ((ODDITIES, report["oddities"]), (LINEUP_ODDITIES, report["lineupOddities"])):
        for key, text in texts:
            lines.append("%s: %d" % (text, len(found[key])))
            if notes:
                lines.extend("    " + x for x in found[key])
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


def parse_vendor_data(values, stacks):
    """The --vendor-data values -> {version: folder}: each names a version one --stack names, and once."""
    named = set(version for version, _root in stacks)
    vendors = {}
    for value in values:
        version, sep, folder = value.partition("=")
        if not sep or not re.match(r"^\d+\.\d+$", version) or not folder:
            raise CatalogError("--vendor-data wants <version>=<folder>, e.g. 3.2=D:/data/welink_3.2_qa (got %r)"
                               % value)
        if version in vendors:
            raise CatalogError("--vendor-data names version %s twice" % version)
        if version not in named:
            raise CatalogError("--vendor-data names version %s, which no --stack names" % version)
        vendors[version] = folder
    return vendors


def derive_vendor_list(folder):
    """-> (the md5 list of every file below `folder`, in LIST_FORM; how many files; how many bytes). Links to
    folders are not followed; a folder or a file that cannot be read stops the run."""
    def unlisted(e):
        raise CatalogError("%s: cannot be listed (%s)" % (e.filename or folder, e))

    entries, size = [], 0
    for top, _folders, names in os.walk(folder, onerror=unlisted):
        for name in names:
            full = os.path.join(top, name)
            digest = hashlib.md5()
            try:
                with open(full, "rb") as f:
                    for block in iter(lambda: f.read(_CHUNK), b""):
                        digest.update(block)
                        size += len(block)
            except OSError as e:
                raise CatalogError("%s: cannot be read (%s)" % (full, e))
            entries.append((os.path.relpath(full, folder).replace(os.sep, "/"), digest.hexdigest()))
    entries.sort()
    return "".join("%s  ./%s\n" % (md5, rel) for rel, md5 in entries), len(entries), size


def known_vendor_data(text, files, size, where):
    """The VENDOR_DATA entry that pins an md5 list -> (its key -- the archive's md5 --, the entry). `text` = the
    list in LIST_FORM, `files` / `size` = how many files and bytes it was derived from, `where` names the list in
    an error. CatalogError when no entry pins the list, or when the entry that does gives other numbers."""
    try:
        digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
    except UnicodeEncodeError:
        raise CatalogError("%s: a file name is not UTF-8 -- not vendor data the generator knows" % where)
    for md5, known in sorted(VENDOR_DATA.items()):
        if known["list"] != digest:
            continue
        if (files, size) != (known["files"], known["bytes"]):
            raise CatalogError("VENDOR_DATA %s: says %d files, %d bytes -- the data its list pins holds %d files, "
                               "%d bytes" % (md5, known["files"], known["bytes"], files, size))
        return md5, known
    raise CatalogError("%s: not vendor data the generator knows -- %d files, %d bytes, md5 list sha256 %s (a file "
                       "added to or changed in the folder after the extraction makes it another list); it knows: %s"
                       % (where, files, size, digest, "; ".join(
                           "%s (archive md5 %s, top folder %s: %d files, %d bytes, md5 list sha256 %s)"
                           % (known["name"], md5, known["top"], known["files"], known["bytes"], known["list"])
                           for md5, known in sorted(VENDOR_DATA.items()))))


def vendor_data(version, folder):
    """The vendor data extracted into `folder` -> (its md5 list, the proof that list is, how many files and
    how many bytes the list was derived from); Stack takes the first two. The folder is the archive's top
    folder, or the folder the archive was extracted into when that holds the top folder alone. CatalogError
    when the list is none VENDOR_DATA pins."""
    where = "--vendor-data %s=%s" % (version, folder)
    root = folder.replace("\\", "/").rstrip("/") or folder
    try:
        inside = os.listdir(root)
    except OSError as e:
        raise CatalogError("%s: not a folder that can be read (%s)" % (where, e))
    if (len(inside) == 1 and inside[0] in set(known["top"] for known in VENDOR_DATA.values())
            and os.path.isdir(root + "/" + inside[0])):
        root += "/" + inside[0]
    print("   deriving the md5 list of %s ..." % root, flush=True)
    text, files, size = derive_vendor_list(root)
    md5, known = known_vendor_data(text, files, size, where)
    return (text, "the md5 list of the vendor data %s (archive md5 %s, %d files, %d bytes; list sha256 %s) derived "
                  "from %s" % (known["name"], md5, files, size, known["list"], root), files, size)


def vendor_cases(version, root, catalogue, vendor):
    """The proof that takes the place of a stack's own md5 list, held against edits of the vendor data's md5
    list made in memory -> (verdict, one line about the case) each, as mutations.run gives them. The edited
    line is the one of the file the quest rewards are read from, a file the agent may rewrite: a list that
    names another md5 for it than the pinned list is not vendor data the generator knows; a VENDOR_DATA entry
    whose numbers are not its data's stops the run; a stack whose copy of the file is not the one the list
    names stops the build at that file. The run itself is their control: vendor_data took the list as
    derived, and build read every file through it."""
    text, proof, files, size = vendor
    where = "the vendor data's md5 list"
    rel = catalogue["quests"]["file"][len(Stack.path("")):]
    lines = text.split("\n")
    at = [i for i, line in enumerate(lines) if line[32:] == "  ./" + rel]
    if len(at) != 1:
        raise CatalogError("%s: names %s %d times" % (where, rel, len(at)))
    md5 = lines[at[0]][:32]
    lines[at[0]] = md5[:-1] + ("1" if md5[-1] == "0" else "0") + lines[at[0]][32:]
    other = "\n".join(lines)
    for name, what, stated, play in (
            ("a file of the vendor data changed after the extraction", rel,
             "%s: not vendor data the generator knows -- %d files, %d bytes" % (where, files, size),
             lambda: known_vendor_data(other, files, size, where)),
            ("a VENDOR_DATA entry whose byte count is not its data's", "VENDOR_DATA",
             "says %d files, %d bytes -- the data its list pins holds %d files, %d bytes"
             % (files, size, files, size + 1),
             lambda: known_vendor_data(text, files, size + 1, where)),
            ("a stack whose file is not the one the vendor data's md5 list names", Stack.path(rel),
             "%s: not the vendor's file" % Stack.path(rel),
             lambda: build(version, root, (other, proof, files, size)))):
        try:
            play()
            outcome = "OK the generator took it"
        except CatalogError as e:
            outcome = "STOP %s" % e
        line = "%s [%s]: %s" % (name, what, outcome[:mutations.SHOWN])
        if outcome.startswith("STOP ") and stated in outcome:
            yield mutations.CAUGHT, line
        else:
            yield mutations.MISSED, "%s  -- stated: STOP %s" % (line, stated)


def mutate(version, root, catalogue, vendor=None):
    """Plays the cases against one stack, a line each -> how many were not caught: for a stack read through
    the vendor data's md5 list the cases of that proof first (vendor_cases), then those of
    tweaks_catalog.mutations. A line-up a case yields is held to check_lineups as well, as the line-ups of its
    own domains."""
    def verify(found, domain_list):
        check_lineups(dict(catalogue, domains=dict(catalogue["domains"], list=domain_list, lineups=found)))

    counts = dict.fromkeys(mutations.VERDICTS, 0)
    stack = Stack(version, root, *(vendor[:2] if vendor else ()))
    items = drops.Items(stack, catalogue["resin"]["hardMax"], once=quests.build_characters(stack))
    cases = mutations.run(stack, catalogue["domains"]["list"], verify, items)
    if vendor:
        cases = itertools.chain(vendor_cases(version, root, catalogue, vendor), cases)
    for verdict, line in cases:
        counts[verdict] += 1
        print("%-8s %s" % (verdict, line), flush=True)
    print("mutations of %s: %d cases -- %s" % (version, sum(counts.values()), ", ".join(
        "%d %s" % (counts[verdict], mutations.VERDICTS[verdict]) for verdict in mutations.VERDICTS)))
    return counts[mutations.MISSED]


def main(argv):
    ap = argparse.ArgumentParser(description="Build agent/payloads/<version>/%s from a vendor server stack." % NAME)
    ap.add_argument("--stack", action="append", required=True, metavar="VERSION=FOLDER",
                    help="a stack to read, e.g. 1.6=D:/servers/1.6 (repeatable)")
    ap.add_argument("--vendor-data", action="append", default=[], metavar="VERSION=FOLDER",
                    help="the folder the data archive of a stack's vendor was extracted into, for a stack that "
                         "ships no md5 list (repeatable)")
    ap.add_argument("--out", default=DEFAULT_OUT, metavar="FOLDER",
                    help="where <version>/%s goes (default: agent/payloads)" % NAME)
    ap.add_argument("--check", action="store_true",
                    help="write nothing; exit 1 when a shipped file differs from what the stack yields")
    ap.add_argument("--notes", action="store_true", help="list the oddities of the vendor data")
    ap.add_argument("--mutations", action="store_true",
                    help="write nothing; hold the generator's proofs against copies of the stack's files -- and "
                         "of the vendor data's md5 list -- edited in memory, exit 1 when an edit is not caught")
    args = ap.parse_args(argv)
    stale = missed = 0
    try:
        stacks = parse_stacks(args.stack)
        vendors = parse_vendor_data(args.vendor_data, stacks)
        for version, root in stacks:
            print("== %s  (%s)" % (version, root), flush=True)
            vendor = None
            if version in vendors:
                # the stack first: deriving the list of its vendor data reads every file of that data
                data_without_list(root)
                vendor = vendor_data(version, vendors[version])
            catalogue, report, members = build(version, root, vendor)
            data = encode(catalogue)
            for line in describe(report, args.notes):
                print("   " + line)
            print("   originals: %d files, %d bytes (%s)" % (len(members), sum(len(raw) for _path, raw in members),
                                                            ORIGINALS))
            path = os.path.join(args.out, version, NAME).replace(os.sep, "/")
            archive = os.path.join(args.out, version, ORIGINALS).replace(os.sep, "/")
            if args.check:
                try:
                    with open(path, "rb") as f:
                        verdict = "ok" if f.read() == data else "DIFFERS"
                except OSError:
                    verdict = "MISSING"
                stale += verdict != "ok"
                print("%-8s %s  (%d bytes expected)" % (verdict, path, len(data)))
                same, what = originals_state(archive, members)
                verdict = "ok" if same else "MISSING" if what == "missing" else "DIFFERS"
                stale += not same
                print("%-8s %s  (%s)" % (verdict, archive, what))
            elif not args.mutations:
                try:
                    os.makedirs(os.path.dirname(path), exist_ok=True)
                    put(path, data)
                except OSError as e:
                    raise CatalogError("%s: cannot be written (%s)" % (path, e))
                print("-> %s  (%d bytes)" % (path, len(data)))
                same, what = originals_state(archive, members)
                if same:
                    print("kept     %s  (%s)" % (archive, what))
                else:
                    packed = encode_originals(members)
                    try:
                        put(archive, packed)
                    except OSError as e:
                        raise CatalogError("%s: cannot be written (%s)" % (archive, e))
                    same, now = originals_state(archive, members)
                    if not same:
                        raise CatalogError("%s: the archive just written does not read back as written (%s)"
                                           % (archive, now))
                    print("-> %s  (%s; the file there was: %s)" % (archive, now, what))
            if args.mutations:
                missed += mutate(version, root, catalogue, vendor)
                cases = originals_cases(members)
                for case, stated in cases:
                    if not stated:
                        print("NOT AS STATED  %s: %s" % (ORIGINALS, case))
                wrong = sum(1 for _case, stated in cases if not stated)
                print("archive of the originals of %s: %d cases -- a copy compressed at another level to be taken, "
                      "every other one refused -- %d not as stated" % (version, len(cases), wrong))
                missed += wrong
    except CatalogError as e:
        print("make_tweaks_catalog: %s" % e, file=sys.stderr)
        return 2
    return 1 if stale or missed else 0


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    sys.exit(main(sys.argv[1:]))
