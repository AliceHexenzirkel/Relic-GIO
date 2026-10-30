"""
Builds the authoritative per-version command catalogue for Relic (characters, 4/5-star weapons,
4/5-star artifact sets) straight from the game's own ExcelBinOutput, plus the matching UI icons.

Why this exists: hand-typed id tables drift and silently send the wrong item (e.g. 23300 typed
in as the artifact example -- that is a ONE-star flower, not a 5-star piece). Everything
here is derived from the real game data instead.

Version accuracy comes from Dimbreath/AnimeGameData's history: each supported version is pinned to
the commit that actually carries that build's data, so "what existed in 1.6" is a fact, not a guess.
    1.6 -> 72c9112a7c5e  "game_1.5.1_1.6.0_diff"
    2.8 -> d56ed231c451  "OSRELWin2.8.0_R8078355_S8017153_D8078038"
    3.2 -> e7c944395d00  "OSRELWin3.2.0_R11078128_S10998085_D11077560"

Icons come from Enka's UI mirror (the game's own UI_* sprites); the game's local .blk archives are
MiHoYo-encrypted and not extractable.

The same run writes config/tweakdata.json, the display catalogue of the Gameplay page: every enemy,
reward item, resin domain and quest of the supported versions by id -- a quest there is a main quest or
the chapter of a hangout event, the id the reward tiers of that event are listed by --, with its English
name and its sprite name, plus the sprites of the enemies and of the reward palette. Enka serves no enemy
icon and only a 64 px copy of a few item icons, so those sprites come from a chain of hosts
(SPRITE_CHAIN).

Usage (from the repo root):
    python build/make_catalog.py                 # data + icons
    python build/make_catalog.py --no-icons      # data only (fast)

Every table and every sprite download is kept under build/_gamedata_cache: with the cache and the
icon folder in place a run needs no network. A version's folder there names the pin its tables were
read at (pin.txt, check_cache_pin): after a change of that pin in VERSIONS, delete the folder.
"""
import http.client
import io
import json
import os
import re
import shutil
import sys
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(REPO, "build", "_gamedata_cache")
SPRITE_CACHE = os.path.join(CACHE, "sprites")
ICON_DIR = os.path.join(REPO, "app", "Relic.App", "ui", "assets", "icons")
OUT_JSON = os.path.join(REPO, "config", "gamedata.generated.json")
TWEAK_JSON = os.path.join(REPO, "config", "tweakdata.json")

RAW = "https://gitlab.com/Dimbreath/AnimeGameData/-/raw/{commit}/{path}"
ICON_URL = "https://enka.network/ui/{icon}.png"
UA = {"User-Agent": "Relic-launcher/1.0"}

# Sprite hosts of the Gameplay catalogue, per sprite family in order of preference. Enemy icons are
# not on Enka at all, and a few item icons (Primogem, the Fates, Resin, ...) are there only at
# 64 px: the first host whose copy is at least SPRITE_PX wide is used, else the largest copy found.
SPRITE_HOSTS = {
    "enka": "https://enka.network/ui/{icon}.png",
    "nanoka": "https://static.nanoka.cc/assets/gi/{icon}.webp",
    "yatta": "https://gi.yatta.moe/assets/UI/{icon}.png",
    "yatta_monster": "https://gi.yatta.moe/assets/UI/monster/{icon}.png",
    "lunaris_monster": "https://api.lunaris.moe/data/assets/monstericon/{icon}.webp",
}
SPRITE_CHAIN = {
    "monster": ["yatta_monster", "nanoka", "lunaris_monster"],
    "item": ["enka", "nanoka", "yatta"],
}
SPRITE_PX = 128

# Ordered oldest -> newest; an item's "since" is the FIRST version it appears in.
VERSIONS = [("1.6", "72c9112a7c5e"), ("2.8", "d56ed231c451"), ("3.2", "e7c944395d00")]

NEEDED = [
    "ExcelBinOutput/WeaponExcelConfigData.json",
    "ExcelBinOutput/ReliquaryExcelConfigData.json",
    "ExcelBinOutput/ReliquarySetExcelConfigData.json",
    "ExcelBinOutput/EquipAffixExcelConfigData.json",
    "ExcelBinOutput/AvatarExcelConfigData.json",
    "TextMap/TextMapEN.json",
]

# What only the Gameplay catalogue reads (it also uses the weapon, reliquary and text tables above).
TWEAK_NEEDED = [
    "ExcelBinOutput/MonsterExcelConfigData.json",
    "ExcelBinOutput/MonsterDescribeExcelConfigData.json",
    "ExcelBinOutput/AnimalDescribeExcelConfigData.json",
    "ExcelBinOutput/AnimalCodexExcelConfigData.json",
    "ExcelBinOutput/ManualTextMapConfigData.json",
    "ExcelBinOutput/MaterialExcelConfigData.json",
    "ExcelBinOutput/HomeWorldFurnitureExcelConfigData.json",
    "ExcelBinOutput/DungeonExcelConfigData.json",
    "ExcelBinOutput/DailyDungeonConfigData.json",
    "ExcelBinOutput/CityConfigData.json",
    "ExcelBinOutput/MainQuestExcelConfigData.json",
    "ExcelBinOutput/ChapterExcelConfigData.json",
    "ExcelBinOutput/CoopRewardExcelConfigData.json",
]

# The hangout events. The agent's catalogue lists a reward tier of one under its chapter, so the page names
# such a row by the chapter's row of the chapter table: "<chapter number>: <chapter title>", the game's own texts
# ("Hangout Event: Barbara - Act I: Wellspring of Healing"). That table holds the chapters of the story quests as
# well, whose ids main quests carry too; of its ids only the chapters a hangout reward names are a quest's
# (hangout_chapters).
CHAPTER_TABLE = "ChapterExcelConfigData.json"
COOP_REWARD_TABLE = "CoopRewardExcelConfigData.json"

# The entrance points of the world map, for the name of the place a domain is entered from
# ("Midsummer Courtyard"). Only a full build carries the file -- the 1.6 commit is a diff without
# BinOutput --, and a build may obfuscate its keys: the points object, an entrance's title text, its
# dungeon list and its rows of the daily rotation. The dumps refresh BinOutput only now and then: 2.8
# and 3.2 carry the file of 2.7 unchanged, so 3.2 reads the file of the next build that refreshed it
# (its revision numbers follow 3.2's) -- names of places, resolved against 3.2's own tables: an
# entrance whose dungeon list names a dungeon 3.2 lacks is left out. "commit" None = the version's
# pin (VERSIONS).
SCENE_POINTS = {
    "2.8": {"path": "BinOutput/Scene/Point/scene3_point.json", "commit": None, "points": "points",
            "title": "PFMFPKFDNEK", "dungeons": "JHHFPGJNMIN", "rotation": "OIBKFJNBLHO"},
    "3.2": {"path": "BinOutput/Scene/Point/scene3_point.json", "commit": "1a6597f5a673", "points": "FFPNEODINCK",
            "title": "titleTextID", "dungeons": "dungeonIds", "rotation": "dungeonRandomList"},
}

# The sections of the Gameplay catalogue and, for each, the tables whose ids decide an entry's
# "since" (section_ids: of the chapter table, the hangout chapters alone).
TWEAK_ID_TABLES = {
    "monsters": ["MonsterExcelConfigData.json"],
    "items": ["MaterialExcelConfigData.json", "WeaponExcelConfigData.json",
              "ReliquaryExcelConfigData.json", "HomeWorldFurnitureExcelConfigData.json"],
    "domains": ["DungeonExcelConfigData.json"],
    "quests": ["MainQuestExcelConfigData.json", CHAPTER_TABLE],
}
TWEAK_SECTIONS = tuple(TWEAK_ID_TABLES)

WEAPON_TYPE = {
    "WEAPON_SWORD_ONE_HAND": "Sword",
    "WEAPON_CLAYMORE": "Claymore",
    "WEAPON_POLE": "Polearm",
    "WEAPON_CATALYST": "Catalyst",
    "WEAPON_BOW": "Bow",
}
SLOT = {
    "EQUIP_BRACER": "flower",
    "EQUIP_NECKLACE": "plume",
    "EQUIP_SHOES": "sands",
    "EQUIP_RING": "goblet",
    "EQUIP_DRESS": "circlet",
}
SLOT_ORDER = ["flower", "plume", "sands", "goblet", "circlet"]
# The main-stat depot every piece of a slot rolls from when nothing narrows it (build_version).
STANDARD_DEPOT = {
    "EQUIP_BRACER": 4000,
    "EQUIP_NECKLACE": 2000,
    "EQUIP_SHOES": 1000,
    "EQUIP_RING": 5000,
    "EQUIP_DRESS": 3000,
}

# The low-tier family (setId < 14000) also carries the 3-star-max sets (Adventurer, Lucky Dog,
# Traveling Doctor). Their rank-4 rows exist in the tables but never drop in game at that rarity,
# so the 4-star picker lists only the sets a player actually knows as 4-star.
SETS_3STAR_MAX = {10010, 10011, 10013}

# Quest-only souvenirs the icon heuristic cannot catch (they own their icon): 11420 is "Prized
# Isshin Blade", the shattered story form of Kagotsurube Isshin (11416, which IS offered) — no
# passive, never a player weapon, would just read as a wrong entry in the picker.
WEAPONS_QUEST_ONLY = {11420}

# The reward palette of the Gameplay page: the item classes whose sprites are fetched here. The
# currencies are virtual items (a reward row holds them as item ids like any other); a character
# card shows the square icon of the avatar it hands out (character_icon), a constellation item the
# game's own sprite. Everything outside the palette is listed as "weapon", "artifact" or "other"; of
# those only the pieces of the 3-star sets and the Traveler's two cards get a sprite here
# (tweak_items).
CURRENCY = {201, 202, 203, 204, 101, 102, 105, 106}
WISH = {221, 222, 223, 224}
# The type line of a MATERIAL_AVATAR_MATERIAL row -> its palette class. The text map writes a line the way
# its build does: the Sumeru talent books of 3.2 say "Talent Level-Up Materials". A line this table does not
# know files its rows under "other" -- no sprite, no chip --, so the run names every such line
# (unknown_material_lines).
AVATAR_MATERIAL_GROUP = {
    "Character Level-Up Material": "ascension",
    "Talent Level-Up Material": "talent",
    "Talent Level-Up Materials": "talent",
    "Weapon Ascension Material": "weaponMaterial",
    "Refinement Material": "weaponMaterial",
}
PALETTE_GROUPS = {"currency", "wish", "character", "constellation", "exp", "ascension", "talent",
                  "weaponMaterial", "specialty", "resource", "consumable"}
# A character card is the item the game turns into the avatar: card 1000 + n hands out avatar
# 10000000 + n, and the card's own art is that avatar's icon in its tall "_Card" form.
CARD_ID_BASE, AVATAR_ID_BASE = 1000, 10000000
CARD_ART_SUFFIX = "_Card"
GAIN_AVATAR = "ITEM_USE_GAIN_AVATAR"
# The identity of the protagonist's avatar rows (the twins): the enum's first value, which the dumps
# write by leaving the field out; every other avatar says AVATAR_IDENTITY_NORMAL.
PROTAGONIST = "AVATAR_IDENTITY_MASTER"

# MainQuest type codes; a row without one is an Archon Quest (the dumps leave out an enum's first
# value). They are the server's quest types 0 / 2 / 7 / 3 / 5 in that order.
QUEST_TYPE = {"AQ": "archon", "LQ": "story", "WQ": "world", "EQ": "event", "IQ": "commission"}

# A domain's reward class is the dungeon sub type; a build that predates that column tells it by
# the name every domain of a class starts with.
DOMAIN_KIND = {
    "DUNGEON_SUB_RELIQUARY": "artifact",
    "DUNGEON_SUB_TALENT": "talent",
    "DUNGEON_SUB_WEAPON": "weapon",
}
DOMAIN_KIND_BY_NAME = (
    ("Domain of Blessing", "artifact"),
    ("Domain of Mastery", "talent"),
    ("Domain of Forgery", "weapon"),
)
TIER_NUMERAL = re.compile(r"\s+([IVX]+)$")
UNTRANSLATED = re.compile(f"[{chr(0x3400)}-{chr(0x9fff)}]")  # CJK ideographs


def get(d, *names, default=None):
    """Field lookup tolerant of the dumps' casing drift (1.6-era PascalCase vs newer camelCase)."""
    for n in names:
        if n in d:
            return d[n]
        alt = n[0].lower() + n[1:]
        if alt in d:
            return d[alt]
    return default


_tables = {}  # parsed once per run: both catalogues read the text map, the largest table
_pins_checked = set()  # the versions whose cache folder was held to their pin in this run
PIN_FILE = "pin.txt"


def check_cache_pin(version):
    """CACHE/<version>/pin.txt names the pin the folder's own files were read at (a folder without it is
    taken for the pin's, and the file is written). A folder of another pin stops the run: its tables would
    pass for the new pin's."""
    if version in _pins_checked:
        return
    pin, folder = dict(VERSIONS)[version], os.path.join(CACHE, version)
    marker = os.path.join(folder, PIN_FILE)
    try:
        with open(marker, encoding="utf-8") as fh:
            kept = fh.read().strip()
    except FileNotFoundError:
        os.makedirs(folder, exist_ok=True)
        with open(marker, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(pin + "\n")
        kept = pin
    if kept != pin:
        raise SystemExit(f"!! {version}: {folder} holds the tables of {kept}, not of the pin {pin} -- delete the "
                         "folder after a change of the pin")
    _pins_checked.add(version)


def fetch(version, commit, path):
    """A file of the dumps at a commit, parsed, kept under CACHE/<version>/. A file read at a commit
    other than the version's pin (SCENE_POINTS) is kept as <commit>_<file name>, so it never passes for
    a file of the pin; the pin's own files are kept by their name, in a folder held to the pin
    (check_cache_pin)."""
    check_cache_pin(version)
    name = os.path.basename(path)
    if commit != dict(VERSIONS).get(version):
        name = f"{commit}_{name}"
    local = os.path.join(CACHE, version, name)
    if local in _tables:
        return _tables[local]
    if not os.path.exists(local):
        os.makedirs(os.path.dirname(local), exist_ok=True)
        url = RAW.format(commit=commit, path=path)
        print(f"  [{version}] downloading {os.path.basename(path)}"
              + ("" if name == os.path.basename(path) else f" at {commit}") + " ...", flush=True)
        # Through a temp file: a download that broke off must not pass for the table on the next run.
        req = urllib.request.Request(url, headers=UA)
        with urllib.request.urlopen(req, timeout=180) as r, open(local + ".part", "wb") as fh:
            shutil.copyfileobj(r, fh)
        os.replace(local + ".part", local)
    with open(local, encoding="utf-8") as fh:
        _tables[local] = json.load(fh)
    return _tables[local]


def build_version(version, commit):
    """Everything that exists in this version's data, keyed for later 'since' resolution."""
    data = {os.path.basename(p): fetch(version, commit, p) for p in NEEDED}
    tm = data["TextMapEN.json"]

    def name_of(h):
        return tm.get(str(h), "").strip()

    # ── weapons: 4-star and 5-star ──────────────────────────────────────────
    # The tables also carry unreleased beta weapons ("One Side", "Deicide", "Mirror Breaker", the
    # duplicate "Primordial Jade *" line). They give themselves away by reusing a generic low-rarity
    # placeholder sprite (UI_EquipIcon_Sword_Blunt, ..._Bow_Hunters): a weapon that actually shipped
    # owns its icon outright, so an icon shared by two or more entries means placeholder art.
    icon_users = {}
    for w in data["WeaponExcelConfigData.json"]:
        ic = get(w, "Icon")
        if ic:
            icon_users[ic] = icon_users.get(ic, 0) + 1

    weapons = {}
    for w in data["WeaponExcelConfigData.json"]:
        rank = get(w, "RankLevel")
        if rank not in (4, 5):
            continue
        wid = get(w, "Id")
        nm = name_of(get(w, "NameTextMapHash"))
        icon = get(w, "Icon")
        if not wid or wid in WEAPONS_QUEST_ONLY or not nm or not icon or icon_users.get(icon, 0) > 1:
            continue
        weapons[wid] = {
            "id": wid,
            "name": nm,
            "rarity": rank,
            "type": WEAPON_TYPE.get(get(w, "WeaponType"), "Sword"),
            "icon": icon,
        }

    # ── artifacts: sets at the rarity they really drop, one canonical piece per slot ──
    # 5-star sets are the setId >= 14000 family; the classic 4-star sets (Sojourner, Berserker,
    # Instructor, ...) are the setId < 14000 family read at rank 4 — their tables DO define 5-star
    # rows, but those never drop in game at that rarity, and vice-versa the 14000+ sets' rank-4
    # variants would only duplicate every 5-star set, so each family is read at its native rank.
    pieces = [
        r for r in data["ReliquaryExcelConfigData.json"]
        if (get(r, "RankLevel") == 5 and (get(r, "SetId") or 0) >= 14000)
        or (get(r, "RankLevel") == 4 and 0 < (get(r, "SetId") or 0) < 14000
            and get(r, "SetId") not in SETS_3STAR_MAX)
    ]
    set_affix = {
        get(s, "SetId"): get(s, "EquipAffixId")
        for s in data["ReliquarySetExcelConfigData.json"]
        if get(s, "SetId")
    }
    affix_name = {}
    for a in data["EquipAffixExcelConfigData.json"]:
        aid = get(a, "Id")
        if aid not in affix_name:
            nm = name_of(get(a, "NameTextMapHash"))
            if nm:
                affix_name[aid] = nm

    by_set, rank = {}, {}
    for p in pieces:
        sid = get(p, "SetId")
        slot = SLOT.get(get(p, "EquipType"))
        if not sid or not slot:
            continue
        # Several ids share a (set, slot): they differ in the main-stat depot, the initial sub-stat
        # count (the trailing digit) and the sub-stat pool. The variant a set's piece drops with wins:
        # the slot's own main-stat depot (a piece with another depot rolls its main stat from a
        # narrower pool), the most starting sub-stats, then the highest id -- the ids the curated
        # config/gamedata.json ships (e.g. Blizzard Strayer flower = 71544, not 23454).
        pid = get(p, "Id")
        key = (get(p, "MainPropDepotId") == STANDARD_DEPOT[get(p, "EquipType")],
               get(p, "AppendPropNum") or 0, pid)
        if key > rank.get((sid, slot), (False, -1, -1)):
            rank[(sid, slot)] = key
            by_set.setdefault(sid, {})[slot] = {"slot": slot, "id": pid, "icon": get(p, "Icon")}

    artifacts = {}
    for sid, slots in by_set.items():
        if len(slots) < 5:
            continue  # incomplete set in this build -- skip rather than send a broken set
        nm = affix_name.get(set_affix.get(sid), "")
        if not nm:
            continue
        artifacts[sid] = {
            "setId": sid,
            "name": nm,
            "rarity": 5 if sid >= 14000 else 4,
            "pieces": [slots[s] for s in SLOT_ORDER],
        }

    # ── avatars: playable roster ────────────────────────────────────────────
    avatars = {}
    for a in data["AvatarExcelConfigData.json"]:
        aid = get(a, "Id")
        if not aid or not (10000002 <= aid <= 10000100):
            continue
        nm = name_of(get(a, "NameTextMapHash"))
        if not nm or nm.lower() in ("", "test"):
            continue
        quality = get(a, "QualityType") or ""
        avatars[aid] = {
            "id": aid,
            "name": nm,
            "rarity": 5 if "ORANGE" in str(quality) else 4,
            "weapon": WEAPON_TYPE.get(get(a, "WeaponType"), ""),
        }

    return {"weapons": weapons, "artifacts": artifacts, "avatars": avatars}


def merge_versions(snapshots):
    """Fold per-version snapshots into flat lists tagged with the version each item first appeared in."""
    out = {"avatars": {}, "weapons": {}, "artifactSets": {}}
    for version, snap in snapshots:
        for wid, w in snap["weapons"].items():
            if wid not in out["weapons"]:
                out["weapons"][wid] = dict(w, since=version)
        for sid, s in snap["artifacts"].items():
            if sid not in out["artifactSets"]:
                out["artifactSets"][sid] = dict(s, since=version)
        for aid, a in snap["avatars"].items():
            if aid not in out["avatars"]:
                out["avatars"][aid] = dict(a, since=version)
    return out


def download_icons(names):
    os.makedirs(ICON_DIR, exist_ok=True)
    try:
        from PIL import Image
    except ImportError:
        print("!! Pillow is missing -- skipping the icons (pip install pillow)")
        return 0

    def one(icon):
        dest = os.path.join(ICON_DIR, f"{icon}.webp")
        if os.path.exists(dest):
            return True
        tmp = dest + ".png"
        try:
            req = urllib.request.Request(ICON_URL.format(icon=icon), headers=UA)
            with urllib.request.urlopen(req, timeout=40) as r, open(tmp, "wb") as fh:
                fh.write(r.read())
            im = Image.open(tmp).convert("RGBA")
            im.thumbnail((128, 128), Image.LANCZOS)
            # Flatten onto nothing: keep alpha, WebP handles it and the UI draws its own frame.
            im.save(dest, "WEBP", quality=88, method=6)
            return True
        except (urllib.error.URLError, urllib.error.HTTPError, OSError):
            return False
        finally:
            if os.path.exists(tmp):
                os.remove(tmp)

    with ThreadPoolExecutor(max_workers=12) as ex:
        results = list(ex.map(one, sorted(names)))
    return sum(1 for r in results if r)


# ── Gameplay catalogue (config/tweakdata.json) ──────────────────────────────
# Display data only: the page joins it by id with what the server's agent reports, so an id listed
# here says nothing about what a server accepts.

def roman(numeral):
    """Value of a roman numeral written with I, V and X."""
    values = {"I": 1, "V": 5, "X": 10}
    total = 0
    for here, after in zip(numeral, numeral[1:] + " "):
        total += -values[here] if values.get(after, 0) > values[here] else values[here]
    return total


def ui_texts(data, name_of):
    """The interface's own texts by their key ("UI_CODEX_ANIMAL_CATEGORY_FATUI" -> "Fatui")."""
    return {get(r, "TextMapId"): name_of(get(r, "TextMapContentTextMapHash"))
            for r in data["ManualTextMapConfigData.json"]}


def tweak_monsters(data, name_of):
    """Every MonsterExcel row -> ({id: entry}, {sprite name: family}).

    name = what the game's archive shows for the row (its describe id leads to the enemy archive,
    for wildlife to the animal one), else the row's own text, else the internal name. Many ids share
    one archive name; the row's own text, kept as "label" when it says something else, and the
    internal "variant" tell them apart ("Hilichurl (Mechanicus)", "Brute_None_Axe_AttackEnhance").
    """
    describe = {get(d, "Id"): d for d in data["MonsterDescribeExcelConfigData.json"]}
    animal = {get(d, "Id"): d for d in data["AnimalDescribeExcelConfigData.json"]}
    manual = ui_texts(data, name_of)
    # The archive category of a describe id in the game's own words ("Hilichurls", "Fatui", ...).
    # A codex row without a sub type is an elemental lifeform: the dumps leave out an enum's first
    # value.
    family = {}
    for c in data["AnimalCodexExcelConfigData.json"]:
        sub = get(c, "SubType", default="CODEX_SUBTYPE_ELEMENTAL").replace("CODEX_SUBTYPE_", "")
        text = manual.get("UI_CODEX_ANIMAL_CATEGORY_" + sub, "")
        if text:
            family[get(c, "DescribeId")] = text

    rows, sprites = {}, {}
    for m in data["MonsterExcelConfigData.json"]:
        mid, did = get(m, "Id"), get(m, "DescribeId")
        d = describe.get(did) or animal.get(did)
        own = name_of(get(m, "NameTextMapHash"))
        variant = get(m, "MonsterName") or ""
        name = (name_of(get(d, "NameTextMapHash")) if d else "") or own or variant
        if not mid or not name:
            continue
        row = {"id": mid, "name": name}
        if own and own != name:
            row["label"] = own
        row["variant"] = variant
        row["kind"] = ("boss" if get(m, "Type") == "MONSTER_BOSS"
                       else "elite" if get(m, "SecurityLevel") == "ELITE" else "common")
        if did in family:
            row["family"] = family[did]
        # Sprites: the icon of the enemy archive entry. Wildlife (the animal archive) is listed by
        # name only, except an animal that fights: among the wildlife rows only those carry combat
        # music.
        icon = get(describe.get(did, {}), "Icon")
        if not icon and get(m, "CombatBGMLevel") and name_of(get(animal.get(did, {}), "NameTextMapHash")):
            icon = get(animal[did], "Icon")
        if icon:
            row["icon"] = icon
            sprites[icon] = "monster"
        rows[mid] = row
    return rows, sprites


def item_group(m, type_desc):
    """Palette class of a material, from the game's own classes: its material type and the type
    line of its tooltip."""
    if get(m, "Id") in CURRENCY:
        return "currency"
    if get(m, "Id") in WISH:
        return "wish"
    kind = get(m, "MaterialType", default="")
    if kind in ("MATERIAL_EXP_FRUIT", "MATERIAL_WEAPON_EXP_STONE", "MATERIAL_RELIQUARY_MATERIAL"):
        return "exp"
    if kind == "MATERIAL_AVATAR":
        return "character"
    # A constellation item is told by the type line the player reads on it, not by its id: the 1100
    # range would leave out the Traveler's own (911..917, "Memory of ..."), and a later dump may file
    # something else under MATERIAL_TALENT.
    if kind == "MATERIAL_TALENT" and type_desc == "Activates Constellation":
        return "constellation"
    if kind == "MATERIAL_AVATAR_MATERIAL":
        return AVATAR_MATERIAL_GROUP.get(type_desc, "other")
    if kind == "MATERIAL_EXCHANGE" and "pecialty" in type_desc:
        return "specialty"
    if kind == "MATERIAL_WOOD" or (kind == "MATERIAL_EXCHANGE" and type_desc in ("Forging Ore", "Material")):
        return "resource"
    if (kind in ("MATERIAL_CONSUME", "MATERIAL_CONSUME_BATCH_USE") and type_desc == "Consumable"
            and (get(m, "RankLevel") or 0) >= 3):
        return "consumable"
    return "other"


def unknown_material_lines(data, name_of):
    """The type lines of the named MATERIAL_AVATAR_MATERIAL rows that AVATAR_MATERIAL_GROUP does not know
    -> {line: number of rows}: such a row is filed as "other" (item_group)."""
    lines = {}
    for m in data["MaterialExcelConfigData.json"]:
        if get(m, "MaterialType", default="") != "MATERIAL_AVATAR_MATERIAL" or not name_of(get(m, "NameTextMapHash")):
            continue
        line = name_of(get(m, "TypeDescTextMapHash"))
        if line not in AVATAR_MATERIAL_GROUP:
            lines[line] = lines.get(line, 0) + 1
    return lines


def stars(row):
    """Rarity of an item row, 0 when the game gives it none. The card of a collab character
    carries 105: the mark of a special five-star on top of its stars."""
    return (get(row, "RankLevel") or 0) % 100


def character_icon(m, own, avatar_icon):
    """The sprite of a character card: the square icon of the avatar it hands out, because the card's
    own art is the tall card ("UI_AvatarIcon_<Name>_Card") and the page's tile is square. The pairing
    is proven per row -- the card's id is the avatar's (1000 + n <-> 10000000 + n), its use hands out
    exactly that avatar, and its own art is that icon's "_Card" -- and a row that fails it answers
    None: never a wrong sprite, and no place among the characters (tweak_items)."""
    aid = AVATAR_ID_BASE + (get(m, "Id") - CARD_ID_BASE)
    icon = avatar_icon.get(aid)
    uses = get(m, "ItemUse", "UseParam") or []
    first = uses[0] if uses else {}
    params = get(first, "UseParam") or []
    if (icon and get(first, "UseOp") == GAIN_AVATAR and params and params[0] == str(aid)
            and own == icon + CARD_ART_SUFFIX):
        return icon
    return None


def tweak_items(data, name_of):
    """Every named material, weapon, artifact piece and furnishing -> ({id: entry}, {sprite name: family}).

    These are the ids a reward row or a domain drop can hold. Sprites are fetched for the palette --
    a character card's is the avatar's square icon (character_icon), the Traveler's two cards keep
    theirs outside it; weapons and artifact pieces show the sprites the command catalogue ships, and
    the pieces of the three 3-star sets get theirs here: the low domain tiers and the early quests
    hand those out.
    """
    avatars = data["AvatarExcelConfigData.json"]
    avatar_icon = {get(a, "Id"): get(a, "IconName") for a in avatars}
    # The cards of the twins, by the identity of their avatar rows (card 1000 + n <-> avatar
    # 10000000 + n): told apart below.
    traveler_cards = {CARD_ID_BASE + (get(a, "Id") - AVATAR_ID_BASE) for a in avatars
                      if get(a, "AvatarIdentityType", default=PROTAGONIST) == PROTAGONIST}
    rows, sprites = {}, {}
    for m in data["MaterialExcelConfigData.json"]:
        name = name_of(get(m, "NameTextMapHash"))
        if not get(m, "Id") or not name:
            continue
        group = item_group(m, name_of(get(m, "TypeDescTextMapHash")))
        icon = get(m, "Icon") or ""
        portrait = False  # a card listed outside the palette whose sprite is fetched all the same
        if group == "character":
            # A card the pairing does not prove -- the second "... for CB1" cards of a few avatars, 30xx
            # ids with no sprite on any host, two of them named like the real card -- is no choice to
            # offer among the characters: it keeps its own art and is found by search alone, like any
            # other oddity (the agent's catalogue still caps it at one, as the server reads it).
            proven = character_icon(m, icon, avatar_icon)
            if proven is None:
                group = "other"
            else:
                icon = proven
                # The Traveler's two cards pass the proof and are no choice either, portrait kept:
                # given to the owner of that twin the card converts to a Stella Fortuna no
                # constellation consumes, given to the owner of the other twin it adds a second
                # protagonist -- a trap either way. Search finds them, like the cards above.
                if get(m, "Id") in traveler_cards:
                    group, portrait = "other", True
        rows[get(m, "Id")] = {"id": get(m, "Id"), "name": name, "rarity": stars(m),
                              "group": group, "icon": icon}
        if icon and (group in PALETTE_GROUPS or portrait):
            sprites[icon] = "item"
    for table, group in (("WeaponExcelConfigData.json", "weapon"),
                         ("ReliquaryExcelConfigData.json", "artifact"),
                         ("HomeWorldFurnitureExcelConfigData.json", "other")):
        for r in data[table]:
            name = name_of(get(r, "NameTextMapHash"))
            if not get(r, "Id") or not name:
                continue
            icon = get(r, "Icon") or ""
            rows[get(r, "Id")] = {"id": get(r, "Id"), "name": name, "rarity": stars(r),
                                  "group": group, "icon": icon}
            if get(r, "SetId") in SETS_3STAR_MAX and icon:
                sprites[icon] = "item"
    return rows, sprites


def domain_entrances(data, name_of, points, keys):
    """{dungeon id: the name of the map entrance it is entered from}, out of the scene's entrance points.

    An entrance lists its dungeons itself (an artifact domain: one per difficulty) or names rows of
    the daily rotation (a talent or weapon domain: one set of difficulties per weekday). keys = the
    names the file gives the entrance's title text, its dungeon list and its rotation rows
    (SCENE_POINTS). A point whose keys do not hold what they should -- dungeon ids, rotation rows of
    this version's own tables -- is left out.
    """
    if keys is None:
        return {}
    key_title, key_dungeons, key_rotation = keys
    texts = ui_texts(data, name_of)
    dungeons = {get(d, "Id") for d in data["DungeonExcelConfigData.json"]}
    rotation = {get(r, "Id"): r for r in data["DailyDungeonConfigData.json"]}
    days = ("Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday")

    def ids(value, known):
        ok = isinstance(value, list) and all(isinstance(i, int) and i in known for i in value)
        return value if ok else None

    entrance = {}
    for p in points.values():
        title = p.get(key_title)
        title = texts.get(title, "") if isinstance(title, str) else ""
        direct, rows = ids(p.get(key_dungeons, []), dungeons), ids(p.get(key_rotation, []), rotation)
        if p.get("$type") != "DungeonEntry" or not title or direct is None or rows is None:
            continue
        for row in rows:
            direct = direct + [i for day in days for i in get(rotation[row], day) or []]
        for i in direct:
            entrance[i] = title
    return entrance


def tweak_domains(data, name_of, points, keys):
    """The resin domains -> {id: entry}: every DUNGEON_DAILY_FIGHT row with a statue reward.

    The name ends in the difficulty as a roman numeral ("... Fires of Purification VI"): it becomes
    "tier" and leaves the name; a name without one takes its tier from the level order of the rows
    that share it. "entrance" is there for a domain the map has an entrance to (points + keys:
    domain_entrances); a row the game never opened is flagged released = false.
    """
    city = {get(c, "CityId"): name_of(get(c, "CityNameTextMapHash")) for c in data["CityConfigData.json"]}
    entrance = domain_entrances(data, name_of, points, keys)
    rows = {}
    for d in data["DungeonExcelConfigData.json"]:
        full = name_of(get(d, "NameTextMapHash"))
        if get(d, "Type") != "DUNGEON_DAILY_FIGHT" or not get(d, "StatueDrop") or not full:
            continue
        numeral = TIER_NUMERAL.search(full)
        row = {"id": get(d, "Id"), "name": full[:numeral.start()] if numeral else full}
        if row["id"] in entrance:
            row["entrance"] = entrance[row["id"]]
        row["tier"] = roman(numeral.group(1)) if numeral else 0
        kind = DOMAIN_KIND.get(get(d, "SubType")) or next(
            (k for prefix, k in DOMAIN_KIND_BY_NAME if full.startswith(prefix)), None)
        if kind:
            row["kind"] = kind
        row["level"] = get(d, "ShowLevel") or 0
        if city.get(get(d, "CityID")):
            row["city"] = city[get(d, "CityID")]
        if get(d, "StateType") != "DUNGEON_STATE_RELEASE":
            row["released"] = False
        rows[row["id"]] = row
    for row in rows.values():
        if not row["tier"]:
            levels = sorted({r["level"] for r in rows.values() if r["name"] == row["name"]})
            row["tier"] = levels.index(row["level"]) + 1
    return rows


def tweak_quests(data, name_of):
    """Every MainQuest row with a title -> {id: entry}."""
    rows = {}
    for q in data["MainQuestExcelConfigData.json"]:
        title = name_of(get(q, "TitleTextMapHash"))
        if not get(q, "Id") or not title:
            continue
        row = {"id": get(q, "Id"), "name": title}
        kind = QUEST_TYPE.get(get(q, "Type", default="AQ"))
        if kind:
            row["type"] = kind
        rows[row["id"]] = row
    return rows


def hangout_chapters(data):
    """The ids of the chapters the hangout rewards name (a reward row of the coop reward table is one tier of
    one chapter)."""
    return {get(r, "ChapterId") for r in data[COOP_REWARD_TABLE]} - {None, 0}


def tweak_hangouts(data, name_of):
    """The chapter of every hangout reward -> {id: entry}: name = "<chapter number>: <chapter title>" as the
    chapter table writes them, type "hangout". A chapter without both texts has no entry."""
    chapters = {get(c, "Id"): c for c in data[CHAPTER_TABLE]}
    rows = {}
    for cid in sorted(hangout_chapters(data)):
        c = chapters.get(cid) or {}
        number, title = name_of(get(c, "ChapterNumTextMapHash")), name_of(get(c, "ChapterTitleTextMapHash"))
        if number and title:
            rows[cid] = {"id": cid, "name": f"{number}: {title}", "type": "hangout"}
    return rows


def section_ids(data, section):
    """Every id the tables of a section carry (TWEAK_ID_TABLES): of the chapter table the hangout chapters
    alone -- its other ids are the story chapters', and main quests carry some of those ids as well."""
    ids = set()
    for table in TWEAK_ID_TABLES[section]:
        carried = {get(r, "Id") for r in data[table]}
        ids |= (carried & hangout_chapters(data)) if table == CHAPTER_TABLE else carried
    return ids


def build_tweak_version(version, commit):
    """What the Gameplay page shows by name in this version: the entries of each section by id,
    every id its tables carry, and the sprites to fetch."""
    data = {os.path.basename(p): fetch(version, commit, p) for p in NEEDED + TWEAK_NEEDED}
    tm = data["TextMapEN.json"]

    def name_of(h):
        # A text the English map still holds in Chinese belongs to a test row the game never
        # translated (a handful of "(test) ..." quests): it counts as no text.
        text = tm.get(str(h), "").strip()
        return "" if UNTRANSLATED.search(text) else text

    spec = SCENE_POINTS.get(version)
    points, keys = {}, None
    if spec:
        at = spec["commit"] or commit
        doc = fetch(version, at, spec["path"])
        points = doc.get(spec["points"]) if isinstance(doc, dict) else None
        if not isinstance(points, dict):
            raise SystemExit(f"!! {version}: {spec['path']} at {at} holds no object {spec['points']}")
        keys = (spec["title"], spec["dungeons"], spec["rotation"])
    monsters, monster_sprites = tweak_monsters(data, name_of)
    items, item_sprites = tweak_items(data, name_of)
    hangouts = tweak_hangouts(data, name_of)
    return {
        "monsters": monsters,
        "items": items,
        "domains": tweak_domains(data, name_of, points, keys),
        "quests": {**tweak_quests(data, name_of), **hangouts},
        "ids": {sec: section_ids(data, sec) for sec in TWEAK_ID_TABLES},
        "sprites": {**monster_sprites, **item_sprites},
        "materialLines": unknown_material_lines(data, name_of),
        # (check_hangouts: a hangout chapter is listed among the quests by its id)
        "hangoutChapters": hangout_chapters(data),
        "hangoutRows": set(hangouts),
        "mainQuests": {get(q, "Id") for q in data["MainQuestExcelConfigData.json"]},
    }


def merge_tweak_versions(snapshots):
    """Fold the per-version snapshots into one list per section, sorted by id, and one sprite list.

    since = the first version whose tables carry the id; city = the region most versions name for the
    row, a tie to the newest: a domain does not move, and a build that files one under another region
    (3.2 puts Cecilia Garden's domains in Liyue) is outvoted by the others; every other field is the
    newest version's row: later builds fix typos, and only they have the domain sub type.
    """
    merged, sprites = {}, {}
    for sec in TWEAK_SECTIONS:
        rows, since, cities = {}, {}, {}
        for version, snap in snapshots:
            for i in snap["ids"][sec]:
                since.setdefault(i, version)
            for i, row in snap[sec].items():
                if "city" in row:
                    cities.setdefault(i, []).append(row["city"])
            rows.update(snap[sec])
        merged[sec] = [dict(rows[i], since=since[i], **({"city": most_named(cities[i])} if i in cities else {}))
                       for i in sorted(rows)]
    for _, snap in snapshots:
        sprites.update(snap["sprites"])
    return merged, sprites


def most_named(values):
    """The value named most often in a list ordered oldest -> newest; a tie goes to the newest of the tied."""
    top = max(values.count(v) for v in values)
    return next(v for v in reversed(values) if values.count(v) == top)


def fetch_sprite(host, icon):
    """One host's copy of a sprite -> its bytes, or None when the host does not have it.

    Downloads are kept under SPRITE_CACHE and a 404 is remembered there as well; only a host that
    could not be reached is asked again by the next run.
    """
    url = SPRITE_HOSTS[host].format(icon=icon)
    local = os.path.join(SPRITE_CACHE, host, icon + os.path.splitext(url)[1])
    miss = os.path.join(SPRITE_CACHE, host, icon + ".miss")
    if os.path.exists(local):
        with open(local, "rb") as fh:
            return fh.read()
    if os.path.exists(miss):
        return None
    os.makedirs(os.path.dirname(local), exist_ok=True)
    body = None
    for _ in range(2):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=40) as r:
                body = r.read()
            break
        except urllib.error.HTTPError as e:
            if e.code == 404:
                open(miss, "w").close()
                return None
        except (urllib.error.URLError, http.client.HTTPException, OSError):
            pass
    if body is None or not body.startswith((b"\x89PNG", b"RIFF")):
        return None  # unreachable, or an error page in place of the picture
    with open(local + ".part", "wb") as fh:
        fh.write(body)
    os.replace(local + ".part", local)
    return body


def download_sprites(wanted):
    """wanted = {sprite name: family} -> {sprite name: the host it came from | "kept" | "missing"}.

    Same sprite format as download_icons. A sprite that already has its file is kept as it is.
    """
    os.makedirs(ICON_DIR, exist_ok=True)
    try:
        from PIL import Image
    except ImportError:
        print("!! Pillow is missing -- skipping the sprites (pip install pillow)")
        return {}

    def one(item):
        icon, family = item
        dest = os.path.join(ICON_DIR, f"{icon}.webp")
        if os.path.exists(dest):
            return icon, "kept"
        best = None
        for host in SPRITE_CHAIN[family]:
            raw = fetch_sprite(host, icon)
            if raw is None:
                continue
            try:
                im = Image.open(io.BytesIO(raw))
                im.load()
            except (OSError, SyntaxError, ValueError):
                continue  # a damaged picture
            if best is None or max(im.size) > max(best[1].size):
                best = (host, im)
            if max(im.size) >= SPRITE_PX:
                break
        if best is None:
            return icon, "missing"
        host, im = best
        im = im.convert("RGBA")
        im.thumbnail((SPRITE_PX, SPRITE_PX), Image.LANCZOS)
        # Encoded in memory first: nothing half-written is left in the folder the launcher ships.
        out = io.BytesIO()
        im.save(out, "WEBP", quality=88, method=6)
        with open(dest, "wb") as fh:
            fh.write(out.getvalue())
        return icon, host

    with ThreadPoolExecutor(max_workers=8) as ex:
        return dict(ex.map(one, sorted(wanted.items())))


def write_tweakdata(catalog):
    """One entry per line: half the size of an indented dump, and a diff shows the entries that changed."""
    parts = []
    for key, value in catalog.items():
        if key in TWEAK_SECTIONS and value:
            body = ",\n".join("  " + json.dumps(row, ensure_ascii=False) for row in value)
            parts.append(f" {json.dumps(key)}: [\n{body}\n ]")
        else:
            parts.append(f" {json.dumps(key)}: {json.dumps(value, ensure_ascii=False)}")
    os.makedirs(os.path.dirname(TWEAK_JSON), exist_ok=True)
    with open(TWEAK_JSON, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("{\n" + ",\n".join(parts) + "\n}\n")


def check_entrances(snapshots):
    """Stops the build when a version of SCENE_POINTS resolves no domain entrance, or when a domain an
    earlier version names an entrance for -- and which this version still has -- is left without one,
    a later version without a scene-point file of its own (no row in SCENE_POINTS) included: the newest
    version's row wins (merge_tweak_versions), so such a row would drop the name on the page of every
    version. A version before the first one that names entrances is not looked at. Returns the line that
    says what was found."""
    first, notes = {}, []
    for v, snap in snapshots:
        if v not in SCENE_POINTS and not first:
            continue
        named = {i for i, row in snap["domains"].items() if "entrance" in row}
        if v in SCENE_POINTS and not named:
            raise SystemExit(f"!! {v}: the scene-point file names no domain entrance -- its keys "
                             "(SCENE_POINTS) do not fit the file")
        lost = {}
        for i, earlier in first.items():
            if i in snap["domains"] and i not in named:
                lost.setdefault(earlier, []).append(i)
        if lost:
            raise SystemExit("\n".join(
                [f"!! {v}: {len(ids)} domains lose the entrance {earlier} names: "
                 + ", ".join(str(i) for i in sorted(ids)) for earlier, ids in sorted(lost.items())]
                + ([] if v in SCENE_POINTS else [f"!! {v} has no row in SCENE_POINTS: its rows name no entrance"])))
        for i in named:
            first.setdefault(i, v)
        notes.append(f"{v} {len(named)} domains")
    return "  entrances: " + (", ".join(notes) + ", none lost" if notes else "no version reads scene points")


def check_hangouts(snapshots):
    """Stops the build when the id of a hangout chapter is the id of a main quest -- in any version: the page
    lists a reward row by the id of its quest or of its chapter, and the catalogue keeps one row per id. Returns
    the line that says what was found: the chapters per version, and how many of them have a name."""
    chapters = set().union(*(snap["hangoutChapters"] for _v, snap in snapshots))
    quests = set().union(*(snap["mainQuests"] for _v, snap in snapshots))
    clash = sorted(chapters & quests)
    if clash:
        raise SystemExit("!! the hangout chapters " + ", ".join(str(i) for i in clash) + " carry the id of a main "
                         "quest as well -- a reward row is listed by the id of its quest or of its chapter")
    return "  hangout chapters: " + ", ".join(
        f"{v} {len(snap['hangoutChapters'])} ({len(snap['hangoutRows'])} named)" for v, snap in snapshots
    ) + ", none with the id of a main quest"


def read_previous_tweakdata():
    """The catalogue as it is before this run writes it ({} when there is none or it cannot be read)."""
    try:
        with open(TWEAK_JSON, encoding="utf-8") as fh:
            previous = json.load(fh)
    except (OSError, ValueError):
        return {}
    return previous if isinstance(previous, dict) else {}


def report_older_ids(previous, merged):
    """Lines that say what the newest version's rows change for the ids the previous catalogue
    already had: their names, then their other fields (name, icon and since left out)."""
    def rest(row):
        return {k: v for k, v in row.items() if k not in ("name", "icon", "since")}

    lines = []
    for sec in TWEAK_SECTIONS:
        old = {r["id"]: r for r in previous.get(sec) or [] if isinstance(r, dict) and "id" in r}
        renamed, changed = [], []
        for row in merged[sec]:
            before = old.get(row["id"])
            if before is None:
                continue
            if before.get("name") != row.get("name"):
                renamed.append(f"{row['id']} {before.get('name')!r} -> {row.get('name')!r}")
            a, b = rest(before), rest(row)
            if a != b:
                changed.append(f"{row['id']} ({', '.join(sorted(k for k in a.keys() | b.keys() if a.get(k) != b.get(k)))})")
        lines.append(f"   {sec}: names changed for {len(renamed)} older ids"
                     + (": " + "; ".join(renamed[:10]) + ("; ..." if len(renamed) > 10 else "") if renamed else ""))
        lines.append(f"   {sec}: other fields changed for {len(changed)} older ids"
                     + (", e.g. " + ", ".join(changed[:10]) if changed else ""))
    return lines


def agent_catalogue_ids(cat):
    """The ids of each section that the agent's catalogue of the Gameplay settings
    (agent/payloads/<v>/tweaks.json) hands the page: every monster it offers or a spawn slot holds,
    every reward item, every domain, every quest of a quest reward."""
    def ints(values):
        return {int(i) for i in values if str(i).isdigit()}

    abyss, domains, quests = cat.get("abyss") or {}, cat.get("domains") or {}, cat.get("quests") or {}
    monsters = ints(cat.get("monsters") or {})
    for chamber in (abyss.get("chambers") or {}).values():
        for half in chamber.get("halves") or []:
            monsters |= ints((half.get("slots") or {}).values())
    for lineup in (domains.get("lineups") or {}).values():
        monsters |= ints((lineup.get("slots") or {}).values())
    return {
        "monsters": monsters,
        "items": ints(cat.get("items") or {}),
        "domains": ints(domains.get("list") or {}),
        "quests": ints(q for r in (quests.get("rewards") or {}).values() for q in r.get("quests") or []),
    }


def report_agent_ids(merged):
    """Lines that name, per version, the ids of the agent's catalogue the display catalogue has no
    row for: the page shows such an id as "#<id>"."""
    have = {sec: {r["id"] for r in merged[sec]} for sec in TWEAK_SECTIONS}
    lines = []
    for v, _ in VERSIONS:
        path = os.path.join(REPO, "agent", "payloads", v, "tweaks.json")
        try:
            with open(path, encoding="utf-8") as fh:
                ids = agent_catalogue_ids(json.load(fh))
        except FileNotFoundError:
            lines.append(f"   {v}: no agent catalogue")
            continue
        except (OSError, ValueError, AttributeError) as e:
            lines.append(f"   {v}: the agent catalogue cannot be read ({e})")
            continue
        for sec in TWEAK_SECTIONS:
            missing = sorted(ids[sec] - have[sec])
            lines.append(f"   {v} {sec}: {len(missing)} of {len(ids[sec])} ids without a display row"
                         + (", e.g. " + ", ".join(str(i) for i in missing[:10]) if missing else ""))
    return lines


def build_tweakdata(want_icons):
    print("\n== Gameplay catalogue: enemies, reward items, domains, quests ==")
    snapshots = [(v, build_tweak_version(v, c)) for v, c in VERSIONS]
    for v, s in snapshots:
        print(f"  {v}: " + ", ".join(f"{len(s[sec])} {sec}" for sec in TWEAK_SECTIONS))
    print(check_entrances(snapshots))
    print(check_hangouts(snapshots))
    unknown = [f"{v} {line!r} ({n} items)" for v, s in snapshots for line, n in sorted(s["materialLines"].items())]
    print("  material type lines: " + ("every one known" if not unknown else
          "NOT in AVATAR_MATERIAL_GROUP (filed as other, no sprite): " + "; ".join(unknown)))
    merged, sprites = merge_tweak_versions(snapshots)

    if want_icons:
        print(f"\n== sprites: {len(sprites)} for the Gameplay catalogue ==")
        result = download_sprites(sprites)
        # Per host: the sprites this run made from that host's copy.
        for status in sorted(set(result.values())):
            print(f"   {status}: {sum(1 for s in result.values() if s == status)}")
        missing = sorted(icon for icon, status in result.items() if status == "missing")
        if missing:
            print("   on no host: " + ", ".join(missing))

    # An entry names its sprite only while the file is there; the page draws a placeholder tile for
    # the rest.
    have = set()
    if os.path.isdir(ICON_DIR):
        have = {f[:-5] for f in os.listdir(ICON_DIR) if f.endswith(".webp")}
    for sec in TWEAK_SECTIONS:
        for row in merged[sec]:
            if row.get("icon") not in have:
                row.pop("icon", None)

    catalog = {
        "_comment": (
            "Generated by build/make_catalog.py — do not edit. Display data of the Gameplay page: "
            "names and sprite names by id. 'since' = the first supported version whose game data "
            "has the id; a domain's 'city' = the region most of those versions name; every other field "
            "is the newest version's."
        ),
        "versionOrder": [v for v, _ in VERSIONS],
        **merged,
    }
    previous = read_previous_tweakdata()
    write_tweakdata(catalog)
    print(f"\n-> {TWEAK_JSON}  ({os.path.getsize(TWEAK_JSON) / 1024:.0f} KB)")
    for sec in TWEAK_SECTIONS:
        rows = merged[sec]
        note = [f"{sum(1 for r in rows if r['since'] == v)} since {v}" for v, _ in VERSIONS]
        if any("icon" in r for r in rows):
            note.append(f"{sum(1 for r in rows if 'icon' in r)} with a sprite")
        print(f"   {len(rows)} {sec} ({', '.join(note)})")
    print("\n== against the previous catalogue: the ids it already had ==")
    for line in report_older_ids(previous, merged):
        print(line)
    print("\n== the agent's catalogues (agent/payloads/<v>/tweaks.json): ids the page would show as #<id> ==")
    for line in report_agent_ids(merged):
        print(line)


def main():
    want_icons = "--no-icons" not in sys.argv
    print("== Relic catalogue: authoritative data from ExcelBinOutput ==")
    snapshots = [(v, build_version(v, c)) for v, c in VERSIONS]
    for v, s in snapshots:
        print(f"  {v}: {len(s['avatars'])} characters, {len(s['weapons'])} 4-5* weapons, {len(s['artifacts'])} 4-5* sets")

    merged = merge_versions(snapshots)
    catalog = {
        "_comment": (
            "GENERATED by build/make_catalog.py from the game's real ExcelBinOutput. "
            "Do not edit by hand -- re-run the script. 'since' = the first supported version "
            "the item appears in; the UI shows only what is available for the selected version."
        ),
        "versionOrder": [v for v, _ in VERSIONS],
        "avatars": sorted(merged["avatars"].values(), key=lambda x: x["id"]),
        "weapons": sorted(merged["weapons"].values(), key=lambda x: (x["type"], x["id"])),
        "artifactSets": sorted(merged["artifactSets"].values(), key=lambda x: x["name"]),
    }

    os.makedirs(os.path.dirname(OUT_JSON), exist_ok=True)
    with open(OUT_JSON, "w", encoding="utf-8") as fh:
        json.dump(catalog, fh, ensure_ascii=False, indent=2)
    print(f"\n-> {OUT_JSON}")
    print(f"   {len(catalog['avatars'])} characters, {len(catalog['weapons'])} 4-5* weapons, "
          f"{len(catalog['artifactSets'])} 4-5* sets")

    if want_icons:
        icons = {w["icon"] for w in catalog["weapons"] if w.get("icon")}
        for s in catalog["artifactSets"]:
            icons |= {p["icon"] for p in s["pieces"] if p.get("icon")}
        print(f"\n== icons: {len(icons)} to download ==")
        ok = download_icons(icons)
        have = {f[:-5] for f in os.listdir(ICON_DIR) if f.endswith(".webp")}
        total = sum(
            os.path.getsize(os.path.join(ICON_DIR, f)) for f in os.listdir(ICON_DIR)
        ) / 1_048_576
        print(f"   {ok}/{len(icons)} downloaded -> {ICON_DIR}  ({total:.1f} MB)")

        # Anything with no art is beta content that never shipped (e.g. the "Glacier and Snowfield"
        # set, which exists in the data but has no icon on any mirror). Drop it rather than let the
        # UI render a broken tile for an item the player can never legitimately see.
        dropped_w = [w["name"] for w in catalog["weapons"] if w.get("icon") not in have]
        dropped_s = [s["name"] for s in catalog["artifactSets"]
                     if any(p.get("icon") not in have for p in s["pieces"])]
        catalog["weapons"] = [w for w in catalog["weapons"] if w.get("icon") in have]
        catalog["artifactSets"] = [s for s in catalog["artifactSets"]
                                   if all(p.get("icon") in have for p in s["pieces"])]
        if dropped_w or dropped_s:
            print("   dropped (no art, unreleased content): "
                  + ", ".join(dropped_w + dropped_s))
        with open(OUT_JSON, "w", encoding="utf-8") as fh:
            json.dump(catalog, fh, ensure_ascii=False, indent=2)
        print(f"   final catalogue: {len(catalog['weapons'])} 4-5* weapons, "
              f"{len(catalog['artifactSets'])} 4-5* sets")

    # After the icons above: the Gameplay catalogue reuses the weapon and artifact sprites.
    build_tweakdata(want_icons)


if __name__ == "__main__":
    main()
