#!/usr/bin/env python3
"""Proves which version's server reads which version's saves, and writes agent/payloads/<version>/save_schema.json.

A player's progress is protobuf. The rows of hk4e_db_user hold serialized messages: PlayerDataBin, with
PlayerExtraBinData and PlayerBeforeLoginBinData beside it, in t_player_data_N; BlockBin in t_block_data_N,
whose group values are serialized GroupBin messages; HomeDataBin in t_home_data_N. Every gameserver carries
the descriptors of those messages inside its own binary, serialized by protoc. This tool reads them from
there -- never from a .proto file, never from a running server -- with its own strict decoder of
descriptor.proto, and from them:

    save_schema.json  per version: the save closure (every message and enum the stored messages reach, with
                      field numbers, names, types, labels, packing, oneofs, presence and defaults); every
                      64-bit integer field by path with its class (CLASS64: the guid paths are the ones a copy
                      to another uid rewrites, and no other 64-bit path may be touched); "guids", those guid
                      paths by encoding -- packed lists, unpacked lists, single values (a single value is a
                      varint or a fixed64: the type is the one of the path's int64 entry, ItemBin.guid being
                      fixed64; a reader takes a packed list unpacked too, as protobuf requires); the uid-bound
                      paths (32-bit player uids, ids whose low word is a uid, cursors into per-uid queues, data
                      waiting in one); and the fingerprints of the descriptor files of the closure: file,
                      offset, size and sha256 inside server/gameserver/gameserver, with the file's syntax. All
                      of them matching is an O(1) content identity of the gameserver build; dbgate is held to
                      the gameserver when the schema is built, never by --identify
    pair proofs       for every directed pair of the stacks given, old -> new. A save of the old server
                      means the same to the new one when every field of the old closure is still at its
                      path with the same number, name, type, label, packing, oneof, presence, default and
                      classes (its CLASS64 class, its uid-bound classes), every message and enum keeps the
                      syntax of its file, every enum value keeps its number and name, and a field that is
                      gone is one of DROPPABLE (a copy drops it); and when the new server's installed tables
                      define every id of the blocking categories that a save of the old one can hold
                      (ID_CATEGORIES)
    saves blocks      what each target's manifest gets: {"schema", "clientPrefixes", "acceptFrom":
                      {"<source>": {"drop": [...]}}}. A source is listed only when its pair proof passes; a
                      downgrade fails it and is never listed. Nothing compares version numbers.
    id sweep          for every pair that passes, old != new: the ids of every table the old stack holds
                      that the new one's tables lack, and the gacha schedule ids of the old database the new
                      one lacks -- a report for the transforms of a migration, never part of the proof

A stack's installed tables are read from server/data/txt (or from the one nested data folder's txt), a table's
vendor original from <name>.orig where the agent keeps one beside it. A table the version's payload manifest
puts in place is the payload's alone when its stage is config, and otherwise the vendor's or the payload's (a
txt fix can be reverted): the ids a save can hold are those of either, the ids a target defines those of both.

The dbgate binary of a stack must carry byte-identical copies of the descriptor files it shares with the
gameserver, the file of PlayerDataBin among them, and no descriptor under a name of the other binary's files
that does not decode: the two processes read and write the same rows. When a stack's dbgate does not, no
schema of the run is written, that stack is left out of the pair proofs and no saves block is printed.

Usage (from the repo root):
    python -B build/save_compat.py --stack 1.6=D:/servers/1.6 --stack 2.8=D:/servers/2.8 --stack 3.2=D:/servers/3.2
        ... --out-dir <folder>   where <version>/save_schema.json goes (default: agent/payloads)
        ... --payloads <folder>  the payload folder whose manifests say which tables a version replaces
                                 (default: agent/payloads; only read)
        ... --check              write nothing; exit 1 when a schema in --out-dir is not what its stack
                                 yields, a schema there belongs to no stack of the run, or a manifest beside
                                 a schema holds a saves block that is not the printed one -- the saves blocks
                                 name every stack of the run, so --check is given every stack
    python -B build/save_compat.py --saves <dump.sql>... [--stack ... | --out-dir <folder>]
                                 decode the saves of SQL dumps with every version's schema (built from the
                                 stacks when --stack is given, else read from --out-dir); writes nothing
    python -B build/save_compat.py --identify <stack folder> [--out-dir <folder>]
                                 which schema's fingerprints the folder's gameserver matches
    python -B build/save_compat.py --selftest
                                 synthetic descriptors and binaries in a temporary folder; touches nothing else
The binaries are read in windows, never whole; the tables line by line. Every stack is read before anything
is written.

Exit code: 0 done; 1 a --check difference, a dbgate whose descriptors are not the gameserver's, a dump with a
save that does not decode cleanly on the version its client string names (on any version given, for a save
no version claims) or that two versions claim, an --identify that does not match exactly one schema, a
failed self-test; 2 a stack, a dump, a binary or a schema that cannot be read, a dump without a save row,
two stacks that bind one client version, or a closure the tables of this tool do not classify (reason on
stderr).
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
import zlib

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_OUT = os.path.join(REPO, "agent", "payloads")
SCHEMA_NAME = "save_schema.json"
MANIFEST_NAME = "manifest.json"
FORMAT = 1

GAMESERVER = "server/gameserver/gameserver"
DBGATE = "server/dbgate/dbgate"
DATA_SQL = "data.sql.tmpl"
DATA_DIR = "server/data"
# A vendor archive may hold the data one folder deeper (server/data/<name>-output_<n>-server-data/); the agent
# moves it up, and the tables are read from whichever of the two a stack holds.
_NESTED_DATA = re.compile(r"-output_\d+-server-data$")

# The messages a save is stored as: (root, full type, table, column, required). GroupBin is stored inside
# BlockBin (NESTED); PlayerBeforeLoginBinData exists only where its server has the message.
ROOTS = (
    ("PlayerDataBin", ".proto.PlayerDataBin", "t_player_data", "bin_data", True),
    ("PlayerExtraBinData", ".proto.PlayerExtraBinData", "t_player_data", "extra_bin_data", True),
    ("PlayerBeforeLoginBinData", ".proto.PlayerBeforeLoginBinData", "t_player_data", "before_login_bin_data",
     False),
    ("BlockBin", ".proto.BlockBin", "t_block_data", "bin_data", True),
    ("GroupBin", ".proto.GroupBin", None, None, True),
    ("HomeDataBin", ".proto.HomeDataBin", "t_home_data", "bin_data", True),
)
ROOT_ORDER = dict((r[0], i) for i, r in enumerate(ROOTS))

# Every bytes field of the closure, by (message, field): the root whose serialized message its bytes hold.
# A bytes field this table does not name stops the run: it could hide anything.
NESTED = {
    (".proto.BlockBin.GroupMapEntry", "value"): "GroupBin",
}

# A field a source's save may hold that the target's message does not have, by path: a copy to that target
# drops it. An entry is pinned by what the field is on the source side -- (message, name, type, label) -- so
# another field at that path is not covered by it.
DROPPABLE = {
    "PlayerDataBin.6.5.1.2.2": (
        ".proto.DungeonRecordBin", "group_map", "message", "repeated",
        "dungeon_bin.record_bin.dungeon_daily_map.value.group_map: the per-group state of a daily dungeon "
        "record, which a target whose record has no such field cannot hold"),
}

# Every 64-bit integer field of the save closures, by (message, field) -> class. A 64-bit field this table does
# not name stops the run, so a version that adds one forces a decision. CLASSES says what each class means.
CLASS64 = dict(
    [((m, f), "guid") for m, f in (
        (".proto.AvatarBin", "guid"),
        (".proto.ItemBin", "guid"),
        (".proto.FormalAvatarBin", "equip_guid_list"),
        (".proto.PlayerAvatarCompBin", "cur_avatar_guid"),
        (".proto.PlayerAvatarCompBin", "choose_avatar_guid"),
        (".proto.PlayerAvatarCompBin", "temp_avatar_guid_list"),
        (".proto.AvatarTeamBin", "avatar_guid_list"),
        (".proto.AvatarTeamBin", "last_cur_avatar_guid"),
        (".proto.TowerTeamBin", "avatar_guid_list"),
        # tower_bin.cur_level_record.avatar_record_map: a record per avatar, keyed by its guid
        (".proto.TowerCurLevelRecordBin.AvatarRecordMapEntry", "key"),
        # the avatars a buff was added to
        (".proto.AvatarBuffBin", "onadded_avatars_list"),
        # an offline message that deletes one of the player's equips
        (".proto.DelEquipOp", "guid"),
        (".proto.DragonSpineScheduleBin", "activity_weapon_guid"),
        # event teams: avatar_id names a trial avatar (is_trial) or one of the player's avatars by guid; a
        # trial id has a zero high word, so a rewrite that only takes values of the source uid passes it by
        (".proto.SumoAvatarBin", "avatar_id"),
        (".proto.SumoAvatarBin", "in_dungeon_guid"),
        (".proto.CrystalLinkAvatarBin", "avatar_id"),
        (".proto.CrystalLinkAvatarBin", "in_dungeon_guid"),
        (".proto.InstableSprayAvatarInfoBin", "avatar_id"),
        (".proto.InstableSprayScheduleBin", "in_dungeon_guid_list"),
        (".proto.RoguelikeMiscBin", "noheal_guid_list"),
        (".proto.RoguelikeMiscBin", "add_talent_guid_list"),
        (".proto.RogueTeamBin", "onstage_avatar_guid_list"),
        (".proto.RogueTeamBin", "backstage_avatar_guid_list"),
        (".proto.PotionScheduleBin", "in_dungeon_avatar_guid_list"),
        (".proto.MuqadasPotionScheduleBin", "in_dungeon_avatar_guid_list"),
        (".proto.CharAmusementScheduleBin", "in_dungeon_guid_list"),
        (".proto.RogueDiaryProgressBin", "onstage_guid_list"),
        (".proto.RogueDiaryProgressBin", "optional_tired_guid_list"),
        (".proto.RogueDiaryAvtarBin", "guid"),
    )]
    + [((m, f), "ugc") for m, f in (
        # (which content << 32) | the uid of the account that made it -- the gameserver's own
        # getNextCustomDungeonGuid and genUgcGuid build them so --, hence uid-bound too (class uidLow)
        (".proto.MusicGameUgcSaveBin", "ugc_guid"),
        (".proto.MusicGameUgcSaveBin", "import_from_ugc_guid"),
        (".proto.MusicGameUgcSearchBin", "ugc_guid"),
        (".proto.MusicGameUgcSearchBin", "import_from_ugc_guid"),
        (".proto.MusicGameUgcPlayBin", "ugc_guid"),
        (".proto.CustomDungeonBriefBin", "dungeon_guid"),
        (".proto.OfficialCustomDungeonBin", "dungeon_guid"),
        (".proto.CustomDungeonBattleRecord", "dungeon_guid"),
        (".proto.PlayerCustomDungeonCompBin", "store_dungeon_list"),
        (".proto.PlayerCustomDungeonCompBin", "last_recommend_list"),
    )]
    + [((m, f), "time") for m, f in (
        # milliseconds since the epoch in the shipped saves: a high word of a few hundred, the size of a small uid
        (".proto.PlayerAvatarCompBin", "last_change_avatar_time"),
        (".proto.SceneBin", "last_spring_recover_time_ms"),
        (".proto.WidgetCoolDownInfoBin", "last_use_time"),
        (".proto.ShareCDInfoBin", "last_use_time"),
        # play time or scene time in milliseconds
        (".proto.PlayerBasicCompBin", "player_time_ms"),
        (".proto.PlayerItemCompBin.ItemCdMapEntry", "value"),
        (".proto.SceneWeatherInfoBin", "next_climate_time"),
        (".proto.HomeBasicCompBin", "home_time_ms"),
    )]
    + [((m, f), "count") for m, f in (
        (".proto.PlayerBasicCompBin", "trans_no_count"),
        (".proto.PlayerBasicCompBin", "player_stat_id"),
        (".proto.RoguelikeDungeonScheduleBin", "trans_no_count"),
        (".proto.BartenderScheduleBin", "trans_no_count"),
        (".proto.HomeBasicCompBin", "trans_no_count"),
        (".proto.HomeBasicCompBin", "exp"),
        (".proto.HomeBasicCompSnapshotBin", "exp"),
    )]
    + [((m, f), "transaction") for m, f in (
        # (unix seconds << 32) | counter in the shipped saves
        (".proto.AvatarExpeditionBin", "transaction"),
    )]
    + [((m, f), "key") for m, f in (
        # the key of a mail waiting in the save to be added to the mailbox; the whole map is uid-bound
        (".proto.PlayerMailCompBin.WaitAddMailMapEntry", "key"),
    )])

# Every field whose name says uid (and not guid), by (message, field): True = a player uid, False = an id of
# something else. A uid-named field this table does not name stops the run. UID_EXTRA names player uids whose
# field name does not say so.
UID_FIELDS = dict(
    [((m, f), False) for m, f in (
        (".proto.AvatarBuffBin", "buff_uid"),
        (".proto.PlayerAvatarCompBin", "last_server_buff_uid"),
    )]
    + [((m, f), True) for m, f in (
        (".proto.PlayerSceneCompBin", "cur_scene_owner_uid"),
        (".proto.PlayerSocialCompBin", "recent_mp_player_uid_list"),
        (".proto.MpPlayBin", "reward_remain_uid_list"),
        (".proto.MpPlayBin.UidWorldLevelBin", "uid"),
        (".proto.BossChestBin", "remain_uid_list"),
        (".proto.BossChestBin", "qualify_uid_list"),
        (".proto.BlossomChestBin", "qualify_uid_list"),
        (".proto.BlossomChestBin", "remain_uid_list"),
        (".proto.GadgetGeneralRewardBin", "remain_uid_list"),
        (".proto.BossDieInfoBin", "surround_uid_list"),
        (".proto.HomeGroupPlayerInfoBin", "uid"),
        (".proto.FurnitureMakeHelpBin", "uid"),
        (".proto.FurnitureMakeBeHelpedBin", "uid"),
        (".proto.BlessingActivityGivePicOp", "source_uid"),
        (".proto.BlessingActivityGivePicOp", "target_uid"),
        (".proto.ExpeditionActivityAssistOp", "target_uid"),
        (".proto.ExpeditionActivityAssistOp", "assist_uid"),
        (".proto.AddFriendOp", "target_uid"),
        (".proto.DelFriendOp", "target_uid"),
        (".proto.AddFriendAskOp", "target_uid"),
        (".proto.DelFriendAskOp", "target_uid"),
        (".proto.CrucibleBattleUidBin", "uid"),
        (".proto.BlessingScanEntityData", "uid"),
        (".proto.BlessingRecvPicData", "uid"),
        (".proto.ExpeditionPathDataBin", "assist_uid"),
        (".proto.ExpeditionAssistRecordBin", "target_uid"),
        (".proto.ChannellerSlabAssistBin", "uid"),
        (".proto.PlantFlowerRecvFlowerDataBin", "uid"),
        (".proto.WinterCampRecvItemBin", "uid"),
        (".proto.ActivityRecvGiftBin", "uid"),
    )])
UID_EXTRA = {
    # social_bin.friend_remark_name_map: the remark a player gave a friend, keyed by the friend's uid
    (".proto.PlayerSocialCompBin.FriendRemarkNameMapEntry", "key"),
}
# Fields bound to the uid by what the server keeps outside the save, by (message, field) -> class.
UID_BOUND = {
    (".proto.PlayerOfflineMsgCompBin", "index"): "cursor",
    (".proto.HomeOfflineMsgCompBin", "index"): "cursor",
    (".proto.PlayerOfflineOpBin", "index_map"): "cursor",
    (".proto.PlayerMailCompBin", "has_take_attachment_mail_id_list"): "cursor",
    (".proto.PlayerOfflineMsgCompBin", "next_offline_msg_list"): "pending",
    (".proto.HomeOfflineMsgCompBin", "next_offline_msg_list"): "pending",
    (".proto.PlayerMailCompBin", "wait_add_mail_map"): "pending",
    (".proto.PlayerMailCompBin", "new_wait_add_mail_map"): "pending",
}
CLASSES = {
    "guid": "the guid of one of the player's own objects, (uid << 32) | sequence: a copy to another uid rewrites "
            "the values whose high word is the source uid",
    "ugc": "the id of user-generated content (a custom dungeon, a music-game level): the uid of the account that "
           "made it in the low 32 bits, which of its contents in the high 32; the content lives outside the save, "
           "under this id: never rewritten",
    "time": "a time in milliseconds -- since the epoch, or of play or scene time: never rewritten (the high word "
            "of an epoch time, a few hundred, looks like a small uid)",
    "count": "a counter or a running number: never rewritten",
    "transaction": "a transaction id, (unix seconds << 32) | counter: never rewritten",
    "key": "the key of an entry waiting in a per-uid queue: never rewritten",
    "uid": "a 32-bit player uid: the player's own or another player's",
    "uidLow": "a 64-bit id whose low 32 bits are the uid of the account that made what it names (the player's own "
              "content or another account's): that content lives outside the save under this id, and a save "
              "moved to another uid still names it under the old one",
    "cursor": "a position in a per-uid queue or mailbox the server keeps outside the save",
    "pending": "data waiting in the save to be delivered to the uid (offline messages, mails not yet in the "
               "mailbox)",
}

# The ids a save holds that a target must define, or the account is refused: the server cannot load an
# avatar, an item or a scene it has no row of. (category, source side, target side); a side is (table,
# rule): "rows" = the first cell of every row, "depots" = the skill depots the avatar table names (its
# DEPOT_COLUMNS) that the version's own depot table defines -- the depots a save of it can hold.
ID_CATEGORIES = (
    ("avatar", ("AvatarData", "rows"), ("AvatarData", "rows")),
    ("skillDepot", ("AvatarData", "depots"), ("AvatarSkillDepotData", "rows")),
    ("material", ("MaterialData", "rows"), ("MaterialData", "rows")),
    ("weapon", ("WeaponData", "rows"), ("WeaponData", "rows")),
    ("reliquary", ("ReliquaryData", "rows"), ("ReliquaryData", "rows")),
    ("furniture", ("FurnitureExcelData", "rows"), ("FurnitureExcelData", "rows")),
    ("reliquaryMainProp", ("ReliquaryMainData", "rows"), ("ReliquaryMainData", "rows")),
    ("reliquaryAffix", ("ReliquaryAffixData", "rows"), ("ReliquaryAffixData", "rows")),
    ("scene", ("SceneData", "rows"), ("SceneData", "rows")),
)
DEPOT_TABLE = "AvatarSkillDepotData"
DEPOT_COLUMNS = ("技能库ID", "候选技能库ID")

# The client versions a server binds (data.sql.tmpl, t_bind_config.client_version) are the starts of the
# client_app_version its saves carry: "OSRELWin2.8.0" -> "OSRELWin2.8.0_R...".
BIND_TABLE = "t_bind_config"
BIND_COLUMN = "client_version"
_CLIENT_VERSION = re.compile(r"^[A-Za-z]+[0-9][0-9A-Za-z.]*$")
# The gacha schedules of a stack's database (data.sql.tmpl): a save's gacha records name them by id.
GACHA_TABLE = "t_gacha_schedule_config"
GACHA_COLUMN = "schedule_id"
# Where a save names the client that wrote it last, by field names.
CLIENT_FIELD = "PlayerDataBin.basic_bin.client_app_version"

# The binaries are read in windows of WINDOW bytes, never whole. A descriptor whose name ends in a window is
# decoded from that window and the LOOKAHEAD bytes after it (the LOOKBACK bytes before the window hold the
# tag and the length of its name), or -- larger than that -- from a read of its own of up to SPAN_MAX bytes.
WINDOW = 64 << 20
LOOKAHEAD = 2 << 20
LOOKBACK = 256
SPAN_MAX = 64 << 20
# A fingerprinted span is hashed in reads of HASH_CHUNK bytes.
HASH_CHUNK = 1 << 20
_PROTO = b".proto"
_NAME = re.compile(rb"[A-Za-z0-9_][A-Za-z0-9_./-]*\.proto")

TYPES = {1: "double", 2: "float", 3: "int64", 4: "uint64", 5: "int32", 6: "fixed64", 7: "fixed32", 8: "bool",
         9: "string", 10: "group", 11: "message", 12: "bytes", 13: "uint32", 14: "enum", 15: "sfixed32",
         16: "sfixed64", 17: "sint32", 18: "sint64"}
LABELS = {1: "optional", 2: "required", 3: "repeated"}
WIRE = {"int32": 0, "int64": 0, "uint32": 0, "uint64": 0, "sint32": 0, "sint64": 0, "bool": 0, "enum": 0,
        "fixed64": 1, "sfixed64": 1, "double": 1, "fixed32": 5, "sfixed32": 5, "float": 5,
        "string": 2, "bytes": 2, "message": 2}
PACKABLE = frozenset(t for t, w in WIRE.items() if w != 2)
INT64 = frozenset(("int64", "uint64", "sint64", "fixed64", "sfixed64"))
_UID_NAME = re.compile(r"uid", re.I)
_GUID_NAME = re.compile(r"guid", re.I)


class CompatError(Exception):
    """The run cannot go on: a stack, a dump or a schema cannot be read, or a closure is not classified. The
    message names the file and the reason."""


# ---------------------------------------------------------------------------------------------------------
# descriptor.proto, decoded strictly: every field a message carries must be one descriptor.proto defines,
# with its wire type, in field-number order, a singular field at most once; strings must be UTF-8.

class _Bad(Exception):
    """Bytes that are not a descriptor."""


class _Short(Exception):
    """The bytes end before the field does."""


_S, _I, _M, _X = "str", "int", "msg", "skip"


def _f(key, kind, sub=None, rep=False):
    return key, kind, sub, rep


_OPTION = _f("uninterpreted_option", _X, rep=True)
SPECS = {
    "FileDescriptorProto": {
        1: _f("name", _S), 2: _f("package", _S), 3: _f("dependency", _S, rep=True),
        4: _f("message_type", _M, "DescriptorProto", True), 5: _f("enum_type", _M, "EnumDescriptorProto", True),
        6: _f("service", _M, "ServiceDescriptorProto", True), 7: _f("extension", _M, "FieldDescriptorProto", True),
        8: _f("options", _M, "FileOptions"), 9: _f("source_code_info", _X),
        10: _f("public_dependency", _I, rep=True), 11: _f("weak_dependency", _I, rep=True), 12: _f("syntax", _S)},
    "DescriptorProto": {
        1: _f("name", _S), 2: _f("field", _M, "FieldDescriptorProto", True),
        3: _f("nested_type", _M, "DescriptorProto", True), 4: _f("enum_type", _M, "EnumDescriptorProto", True),
        5: _f("extension_range", _M, "ExtensionRange", True), 6: _f("extension", _M, "FieldDescriptorProto", True),
        7: _f("options", _M, "MessageOptions"), 8: _f("oneof_decl", _M, "OneofDescriptorProto", True),
        9: _f("reserved_range", _M, "ReservedRange", True), 10: _f("reserved_name", _S, rep=True)},
    "ExtensionRange": {1: _f("start", _I), 2: _f("end", _I), 3: _f("options", _X)},
    "ReservedRange": {1: _f("start", _I), 2: _f("end", _I)},
    "FieldDescriptorProto": {
        1: _f("name", _S), 2: _f("extendee", _S), 3: _f("number", _I), 4: _f("label", _I), 5: _f("type", _I),
        6: _f("type_name", _S), 7: _f("default_value", _S), 8: _f("options", _M, "FieldOptions"),
        9: _f("oneof_index", _I), 10: _f("json_name", _S), 17: _f("proto3_optional", _I)},
    "OneofDescriptorProto": {1: _f("name", _S), 2: _f("options", _M, "OneofOptions")},
    "EnumDescriptorProto": {
        1: _f("name", _S), 2: _f("value", _M, "EnumValueDescriptorProto", True),
        3: _f("options", _M, "EnumOptions"), 4: _f("reserved_range", _M, "ReservedRange", True),
        5: _f("reserved_name", _S, rep=True)},
    "EnumValueDescriptorProto": {1: _f("name", _S), 2: _f("number", _I), 3: _f("options", _M, "EnumValueOptions")},
    "ServiceDescriptorProto": {
        1: _f("name", _S), 2: _f("method", _M, "MethodDescriptorProto", True), 3: _f("options", _M, "ServiceOptions")},
    "MethodDescriptorProto": {
        1: _f("name", _S), 2: _f("input_type", _S), 3: _f("output_type", _S), 4: _f("options", _M, "MethodOptions"),
        5: _f("client_streaming", _I), 6: _f("server_streaming", _I)},
    "FileOptions": {
        1: _f("java_package", _S), 8: _f("java_outer_classname", _S), 9: _f("optimize_for", _I),
        10: _f("java_multiple_files", _I), 11: _f("go_package", _S), 16: _f("cc_generic_services", _I),
        17: _f("java_generic_services", _I), 18: _f("py_generic_services", _I),
        20: _f("java_generate_equals_and_hash", _I), 23: _f("deprecated", _I), 27: _f("java_string_check_utf8", _I),
        31: _f("cc_enable_arenas", _I), 36: _f("objc_class_prefix", _S), 37: _f("csharp_namespace", _S),
        39: _f("swift_prefix", _S), 40: _f("php_class_prefix", _S), 41: _f("php_namespace", _S),
        42: _f("php_generic_services", _I), 44: _f("php_metadata_namespace", _S), 45: _f("ruby_package", _S),
        999: _OPTION},
    "MessageOptions": {
        1: _f("message_set_wire_format", _I), 2: _f("no_standard_descriptor_accessor", _I), 3: _f("deprecated", _I),
        7: _f("map_entry", _I), 999: _OPTION},
    "FieldOptions": {
        1: _f("ctype", _I), 2: _f("packed", _I), 3: _f("deprecated", _I), 5: _f("lazy", _I), 6: _f("jstype", _I),
        10: _f("weak", _I), 999: _OPTION},
    "OneofOptions": {999: _OPTION},
    "EnumOptions": {2: _f("allow_alias", _I), 3: _f("deprecated", _I), 999: _OPTION},
    "EnumValueOptions": {1: _f("deprecated", _I), 999: _OPTION},
    "ServiceOptions": {33: _f("deprecated", _I), 999: _OPTION},
    "MethodOptions": {33: _f("deprecated", _I), 34: _f("idempotency_level", _I), 999: _OPTION},
}


def _varint(buf, i, end):
    """-> (value, next offset). _Short when the bytes end first, _Bad for a varint longer than 64 bits."""
    value = shift = 0
    while True:
        if i >= end:
            raise _Short()
        c = buf[i]
        i += 1
        value |= (c & 0x7F) << shift
        if not c & 0x80:
            if value >> 64:
                raise _Bad("a varint longer than 64 bits")
            return value, i
        shift += 7
        if shift > 63:
            raise _Bad("a varint longer than 64 bits")


def _field(buf, i, end, what, last):
    """Reads one field of a descriptor.proto message `what` -> (number, key, rep, value, next offset)."""
    at = i
    tag, i = _varint(buf, i, end)
    num, wt = tag >> 3, tag & 7
    entry = SPECS[what].get(num)
    if entry is None:
        raise _Bad("%s: field %d (at +%d) is not one descriptor.proto defines" % (what, num, at))
    key, kind, sub, rep = entry
    if wt != (0 if kind == _I else 2):
        raise _Bad("%s.%s: wire type %d" % (what, key, wt))
    if num < last or (num == last and not rep):
        raise _Bad("%s.%s: out of field order" % (what, key))
    if wt == 0:
        value, i = _varint(buf, i, end)
        if kind == _I and value >> 63:
            value -= 1 << 64
        return num, key, rep, value, i
    size, i = _varint(buf, i, end)
    if size > end - i:
        raise _Short()
    start, i = i, i + size
    if kind == _S:
        try:
            value = bytes(buf[start:i]).decode("utf-8")
        except UnicodeDecodeError:
            raise _Bad("%s.%s: not UTF-8" % (what, key))
    elif kind == _M:
        value = _message(buf, start, i, sub)
    else:
        value = None
    return num, key, rep, value, i


def _message(buf, i, end, what):
    """A length-delimited descriptor.proto message -> {key: value, or [values] for a repeated field}."""
    out, last = {}, 0
    try:
        while i < end:
            last, key, rep, value, i = _field(buf, i, end, what, last)
            if rep:
                out.setdefault(key, []).append(value)
            else:
                out[key] = value
    except _Short:
        raise _Bad("%s: a field runs past the end of its message" % what)
    return out


class _NeedMore(Exception):
    """The window ends inside the descriptor; more of the file follows."""


def _read_file_descriptor(buf, start, limit, eof):
    """A FileDescriptorProto from `start` up to the NUL that ends it -> (fields, size without the NUL). The
    descriptors a protoc-generated binary embeds are string literals: the NUL ends one exactly. _NeedMore when
    `buf` ends first and the file goes on (`eof` false); _Bad for anything else."""
    out, last, i = {}, 0, start
    while True:
        if i >= limit:
            if eof:
                raise _Bad("the file ends before the NUL after the descriptor")
            raise _NeedMore()
        if buf[i] == 0:
            return out, i - start
        try:
            last, key, rep, value, i = _field(buf, i, limit, "FileDescriptorProto", last)
        except _Short:
            if eof:
                raise _Bad("the file ends inside the descriptor")
            raise _NeedMore()
        if rep:
            out.setdefault(key, []).append(value)
        else:
            out[key] = value


def _convert_field(f, oneofs, syntax, where):
    for key in ("name", "number", "type"):
        if key not in f:
            raise _Bad("%s: a field without %s" % (where, key))
    if f.get("extendee") is not None:
        raise _Bad("%s.%s: an extension where a field belongs" % (where, f["name"]))
    ftype = TYPES.get(f["type"])
    label = LABELS.get(f.get("label", 1))
    if ftype is None or label is None:
        raise _Bad("%s.%s: type %r, label %r" % (where, f["name"], f["type"], f.get("label")))
    if not 1 <= f["number"] <= (1 << 29) - 1:
        raise _Bad("%s.%s: field number %d" % (where, f["name"], f["number"]))
    out = {"name": f["name"], "n": f["number"], "type": ftype, "label": label}
    if ftype in ("message", "enum", "group"):
        name = f.get("type_name") or ""
        if not name.startswith("."):
            raise _Bad("%s.%s: type name %r is not fully qualified" % (where, f["name"], name))
        out["typeName"] = name
    elif f.get("type_name") is not None:
        raise _Bad("%s.%s: a type name on a %s field" % (where, f["name"], ftype))
    option = (f.get("options") or {}).get("packed")
    if option is not None and (label != "repeated" or ftype not in PACKABLE):
        raise _Bad("%s.%s: packed on a field that cannot be packed" % (where, f["name"]))
    if label == "repeated" and ftype in PACKABLE and (bool(option) if option is not None else syntax == "proto3"):
        out["packed"] = True
    if f.get("oneof_index") is not None:
        if not 0 <= f["oneof_index"] < len(oneofs):
            raise _Bad("%s.%s: oneof index %d" % (where, f["name"], f["oneof_index"]))
        out["oneof"] = oneofs[f["oneof_index"]]
    if f.get("proto3_optional"):
        out["proto3Optional"] = True
    if f.get("default_value") is not None:
        out["default"] = f["default_value"]
    return out


def _convert_enum(e, where):
    if "name" not in e:
        raise _Bad("%s: an enum without a name" % where)
    values = []
    for v in e.get("value", []):
        if "name" not in v or "number" not in v:
            raise _Bad("%s.%s: a value without a name or a number" % (where, e["name"]))
        values.append([v["number"], v["name"]])
    return {"name": e["name"], "values": values}


def _convert_message(m, syntax, where):
    if "name" not in m:
        raise _Bad("%s: a message without a name" % where)
    here = "%s.%s" % (where, m["name"])
    oneofs = []
    for o in m.get("oneof_decl", []):
        if "name" not in o:
            raise _Bad("%s: a oneof without a name" % here)
        oneofs.append(o["name"])
    fields = [_convert_field(f, oneofs, syntax, here) for f in m.get("field", [])]
    numbers = [f["n"] for f in fields]
    if len(set(numbers)) != len(numbers) or len(set(f["name"] for f in fields)) != len(fields):
        raise _Bad("%s: two fields share a number or a name" % here)
    return {"name": m["name"], "fields": fields, "oneofs": oneofs,
            "mapEntry": bool((m.get("options") or {}).get("map_entry")),
            "nested": [_convert_message(n, syntax, here) for n in m.get("nested_type", [])],
            "enums": [_convert_enum(e, here) for e in m.get("enum_type", [])]}


def _convert_file(d, offset, size, digest):
    syntax = d.get("syntax", "proto2")
    if syntax not in ("proto2", "proto3"):
        raise _Bad("syntax %r" % syntax)
    where = d["name"]
    return {"name": d["name"], "package": d.get("package", ""), "syntax": syntax,
            "dependency": d.get("dependency", []),
            "messages": [_convert_message(m, syntax, where) for m in d.get("message_type", [])],
            "enums": [_convert_enum(e, where) for e in d.get("enum_type", [])],
            "offset": offset, "size": size, "sha256": digest}


# ---------------------------------------------------------------------------------------------------------
# Finding the descriptors in a binary

def decode_descriptor(buf, start, limit, eof):
    """The descriptor at `start` of `buf` -> its file dict (offset relative to `buf`). _Bad / _NeedMore."""
    d, size = _read_file_descriptor(buf, start, limit, eof)
    if "name" not in d:
        raise _Bad("no name")
    digest = hashlib.sha256(bytes(buf[start:start + size])).hexdigest()
    return _convert_file(d, start, size, digest)


def scan_descriptors(path, window=None, lookahead=None, span_max=None):
    """Every FileDescriptorProto a binary embeds -> ({file name: file dict with its absolute offset, size and
    sha256}, stats). The binary is read in windows (WINDOW), never whole. A descriptor is found by its first
    field -- 0x0A, the length of its name, a name ending in ".proto" -- and kept when the strict decoder reads
    it whole up to its NUL and it defines something. One name at two offsets with the same bytes keeps the
    lower offset; with other bytes it is an error."""
    window = window or WINDOW
    lookahead = LOOKAHEAD if lookahead is None else lookahead
    span_max = span_max or SPAN_MAX
    try:
        size = os.path.getsize(path)
        fh = open(path, "rb")
    except OSError as e:
        raise CompatError("%s: cannot be read (%s)" % (path, e))
    found, rejected = {}, {}
    hits = tried = 0
    tried_at = set()
    rereads = []
    with fh:
        base = 0
        while base < size:
            lo = max(0, base - LOOKBACK)
            hi = min(size, base + window + lookahead)
            fh.seek(lo)
            # the previous window is let go before the next is read: one window in memory at a time
            buf = None
            buf = fh.read(hi - lo)
            if len(buf) != hi - lo:
                raise CompatError("%s: read %d bytes at %d, wanted %d" % (path, len(buf), lo, hi - lo))
            stop = min(base + window, size) - lo
            pos = base - lo
            while True:
                p = buf.find(_PROTO, pos)
                if p < 0 or p >= stop:
                    break
                pos = p + 1
                hits += 1
                name_end = p + len(_PROTO)
                s = max(0, name_end - 2 - 127)
                while True:
                    s = buf.find(b"\n", s, name_end - 2 - 7 + 1)
                    if s < 0:
                        break
                    length = name_end - s - 2
                    at = lo + s
                    if buf[s + 1] == length and _NAME.fullmatch(buf, s + 2, name_end) and at not in tried_at:
                        tried_at.add(at)
                        tried += 1
                        f = _candidate(fh, buf, s, lo, size, span_max, rejected, rereads)
                        if f is not None:
                            found.setdefault(f["name"], []).append(f)
                    s += 1
            base += window
    files = {}
    for name, copies in sorted(found.items()):
        digests = set(c["sha256"] for c in copies)
        if len(digests) > 1:
            raise CompatError("%s: two different descriptors named %s, at %s" % (
                path, name, ", ".join("%d (%d bytes)" % (c["offset"], c["size"]) for c in copies)))
        first = min(copies, key=lambda c: c["offset"])
        first["copies"] = len(copies)
        files[name] = first
    stats = {"size": size, "hits": hits, "candidates": tried, "files": len(files), "rereads": len(rereads),
             "messages": sum(len(f["messages"]) for f in files.values()),
             "duplicates": sum(f["copies"] - 1 for f in files.values()),
             "rejected": dict((k, v) for k, v in rejected.items() if k not in files)}
    return files, stats


def _candidate(fh, buf, s, lo, size, span_max, rejected, rereads):
    """Decodes the candidate at buf[s] (absolute lo + s) -> its file dict, or None when it is no descriptor
    that defines something. A candidate that runs past the window is read again on its own (its offset goes
    into `rereads`)."""
    at = lo + s
    try:
        try:
            f = decode_descriptor(buf, s, len(buf), lo + len(buf) >= size)
        except _NeedMore:
            rereads.append(at)
            span = len(buf) - s
            while True:
                span = min(span * 4, span_max, size - at)
                fh.seek(at)
                part = fh.read(span)
                try:
                    f = decode_descriptor(part, 0, len(part), at + len(part) >= size or span >= span_max)
                    break
                except _NeedMore:
                    pass
        f["offset"] = at
    except _Bad as e:
        name = bytes(buf[s + 2:s + 2 + buf[s + 1]]).decode("ascii", "replace")
        rejected.setdefault(name, []).append("%d: %s" % (at, e))
        return None
    if not (f["messages"] or f["enums"] or f["package"] or f["dependency"]):
        return None
    return f


# ---------------------------------------------------------------------------------------------------------
# The save closure and its schema

class Registry(object):
    """Every message and enum of a set of descriptor files, by full name -> (definition, file, syntax)."""

    def __init__(self, files):
        self.messages, self.enums = {}, {}
        for fname in sorted(files):
            f = files[fname]
            prefix = "." + f["package"] if f["package"] else ""
            for m in f["messages"]:
                self._message(m, prefix, fname, f["syntax"])
            for e in f["enums"]:
                self._enum(e, prefix, fname)

    def _taken(self, fq, fname):
        other = self.messages.get(fq) or self.enums.get(fq)
        if other:
            raise CompatError("%s is defined in %s and in %s" % (fq, other[1], fname))

    def _message(self, m, prefix, fname, syntax):
        fq = prefix + "." + m["name"]
        self._taken(fq, fname)
        self.messages[fq] = (m, fname, syntax)
        for n in m["nested"]:
            self._message(n, fq, fname, syntax)
        for e in m["enums"]:
            self._enum(e, fq, fname)

    def _enum(self, e, prefix, fname):
        fq = prefix + "." + e["name"]
        self._taken(fq, fname)
        self.enums[fq] = (e, fname)


def _path_key(path):
    root, _, rest = path.partition(".")
    return ROOT_ORDER.get(root, len(ROOT_ORDER)), root, tuple(int(x) for x in rest.split(".") if x)


def build_schema(version, files, binary=GAMESERVER):
    """The descriptor files of a gameserver -> (schema, report). CompatError when a required root is missing,
    a type cannot be resolved, a type contains itself, or a field the closure holds is one the tables of this
    tool do not classify (a 64-bit integer, a uid-named field, a bytes field)."""
    reg = Registry(files)
    roots = []
    for root, fq, table, column, required in ROOTS:
        if fq in reg.messages:
            roots.append((root, fq, table, column))
        elif required:
            raise CompatError("%s: the save message %s is not among the %d descriptor files of the binary" % (
                version, fq, len(files)))
    root_names = set(r[0] for r in roots)
    messages, enums = {}, {}
    int64, uid_bound, nested = [], [], []
    open64, open_uid = set(), set()
    paths = [0]

    def walk(t, root, path, names, stack):
        if t in stack:
            raise CompatError("%s: %s contains itself (%s)" % (version, t, path))
        if t not in reg.messages:
            raise CompatError("%s: %s (at %s) is not among the descriptors" % (version, t, path))
        m, fname, _syntax = reg.messages[t]
        messages[t] = (m, fname)
        for f in sorted(m["fields"], key=lambda x: x["n"]):
            p = "%s.%d" % (path, f["n"])
            named = "%s.%s" % (names, f["name"])
            key = (t, f["name"])
            ref = "%s.%s" % (t, f["name"])
            paths[0] += 1
            entry = dict((k, f[k]) for k in ("type", "label", "typeName", "packed") if k in f)
            entry.update({"path": p, "names": named, "field": ref})
            if f["type"] == "group":
                raise CompatError("%s: %s is a group, which this tool does not read" % (version, p))
            if f["type"] in INT64:
                if key in CLASS64:
                    int64.append(dict(entry, **{"class": CLASS64[key]}))
                    if CLASS64[key] == "ugc":
                        uid_bound.append(dict(entry, **{"class": "uidLow"}))
                else:
                    open64.add("%s (%s %s at %s)" % (ref, f["label"], f["type"], p))
            uid_named = _UID_NAME.search(f["name"]) and not _GUID_NAME.search(f["name"])
            if f["type"] == "message":
                # a message named like a uid holds fields of its own, classified where they are; a map named
                # like one is keyed by what its name says, so the key of its entries must be classified
                inner = reg.messages.get(f["typeName"])
                map_key = (f["typeName"], "key")
                if (uid_named and inner is not None and inner[0]["mapEntry"] and map_key not in UID_FIELDS
                        and map_key not in UID_EXTRA):
                    open_uid.add("%s.key (the key of the map %s at %s.1)" % (f["typeName"], ref, p))
            elif uid_named or key in UID_FIELDS or key in UID_EXTRA:
                if key in UID_EXTRA or UID_FIELDS.get(key):
                    uid_bound.append(dict(entry, **{"class": "uid"}))
                elif key not in UID_FIELDS:
                    open_uid.add("%s (%s %s at %s)" % (ref, f["label"], f["type"], p))
            if key in UID_BOUND:
                uid_bound.append(dict(entry, **{"class": UID_BOUND[key]}))
            if f["type"] == "message":
                walk(f["typeName"], root, p, named, stack + (t,))
            elif f["type"] == "enum":
                if f["typeName"] not in reg.enums:
                    raise CompatError("%s: enum %s (at %s) is not among the descriptors" % (version, f["typeName"], p))
                enums[f["typeName"]] = reg.enums[f["typeName"]]
            elif f["type"] == "bytes":
                inner = NESTED.get(key)
                if inner is None:
                    open64.add("%s (bytes at %s: what it holds is not named in NESTED)" % (ref, p))
                elif inner not in root_names:
                    raise CompatError("%s: %s holds %s, which is not among the descriptors" % (version, p, inner))
                else:
                    nested.append({"path": p, "field": ref, "message": t, "n": f["n"], "root": inner,
                                   "type": dict((r[0], r[1]) for r in roots)[inner]})

    for root, fq, _table, _column in roots:
        walk(fq, root, root, root, ())
    if open64 or open_uid:
        raise CompatError("%s: the closure holds fields the tables of this tool do not classify -- add each to "
                          "CLASS64 / NESTED or UID_FIELDS:\n    %s" % (
                              version, "\n    ".join(sorted(open64) + sorted(open_uid))))
    if "GroupBin" in root_names and not any(n["root"] == "GroupBin" for n in nested):
        raise CompatError("%s: GroupBin is not held by any bytes field of the closure" % version)
    int64.sort(key=lambda e: _path_key(e["path"]))
    uid_bound.sort(key=lambda e: (_path_key(e["path"]), e["class"]))
    nested.sort(key=lambda e: _path_key(e["path"]))
    guids = {"packed": [], "repeated": [], "scalar": []}
    for e in int64:
        if e["class"] == "guid":
            kind = "scalar" if e["label"] != "repeated" else "packed" if e.get("packed") else "repeated"
            guids[kind].append(e["path"])
    used_files = sorted(set(fname for _m, fname in messages.values()) | set(fname for _e, fname in enums.values()))
    schema = {
        "format": FORMAT,
        "version": version,
        "binary": binary,
        "fingerprints": [{"file": n, "offset": files[n]["offset"], "size": files[n]["size"],
                          "sha256": files[n]["sha256"], "syntax": files[n]["syntax"]} for n in used_files],
        "roots": [{"root": r, "type": fq, "table": table, "column": column} if table else {"root": r, "type": fq}
                  for r, fq, table, column in roots],
        "nested": nested,
        "messages": dict((fq, _schema_message(m, fname)) for fq, (m, fname) in messages.items()),
        "enums": dict((fq, {"file": fname, "values": sorted(e["values"])}) for fq, (e, fname) in enums.items()),
        "int64": int64,
        "guids": guids,
        "uidBound": uid_bound,
        "classes": dict((c, CLASSES[c]) for c in sorted(set(e["class"] for e in int64 + uid_bound))),
    }
    report = {"messages": len(messages), "enums": len(enums), "paths": paths[0], "roots": [r[0] for r in roots],
              "files": used_files}
    return schema, report


def _schema_message(m, fname):
    out = {"file": fname,
           "fields": [dict((k, f[k]) for k in sorted(f)) for f in sorted(m["fields"], key=lambda x: x["n"])]}
    if m["mapEntry"]:
        out["mapEntry"] = True
    return out


def encode(schema):
    """The file's bytes: keys sorted; a top-level object or list one entry per line, each entry compact; LF,
    one at the end. The same schema gives the same bytes."""
    def compact(v):
        return json.dumps(v, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)

    lines = ["{"]
    keys = sorted(schema)
    for i, key in enumerate(keys):
        value, tail = schema[key], "," if i < len(keys) - 1 else ""
        name = json.dumps(key, ensure_ascii=False)
        if isinstance(value, dict) and value:
            lines.append(" %s: {" % name)
            sub = sorted(value)
            lines.extend("  %s: %s%s" % (json.dumps(k, ensure_ascii=False), compact(value[k]),
                                         "," if j < len(sub) - 1 else "") for j, k in enumerate(sub))
            lines.append(" }" + tail)
        elif isinstance(value, list) and value:
            lines.append(" %s: [" % name)
            lines.extend("  %s%s" % (compact(v), "," if j < len(value) - 1 else "") for j, v in enumerate(value))
            lines.append(" ]" + tail)
        else:
            lines.append(" %s: %s%s" % (name, compact(value), tail))
    lines.append("}")
    return ("\n".join(lines) + "\n").encode("utf-8")


def put(path, data):
    """Writes a file so that it appears whole: beside itself first, then renamed over the file."""
    part = path + ".part"
    try:
        with open(part, "wb") as f:
            f.write(data)
        os.replace(part, path)
    finally:
        if os.path.exists(part):
            os.remove(part)


_HEX64 = re.compile(r"^[0-9a-f]{64}$")


def _is_int(value, low, high=None):
    return type(value) is int and value >= low and (high is None or value <= high)


def _strs(entry, *keys):
    return isinstance(entry, dict) and all(isinstance(entry.get(k), str) and entry.get(k) for k in keys)


def schema_problem(schema):
    """Why a save schema cannot be used; None when it can. Every part this tool reads is checked: the parts
    present with their kinds, every field of every message, the roots, the nested roots, the classified paths
    and the fingerprints (each a span of at most SPAN_MAX bytes)."""
    if not isinstance(schema, dict) or schema.get("format") != FORMAT:
        return "not a save schema of format %d" % FORMAT
    for key, kind in (("version", str), ("binary", str), ("messages", dict), ("enums", dict), ("roots", list),
                      ("nested", list), ("int64", list), ("guids", dict), ("uidBound", list),
                      ("fingerprints", list), ("classes", dict)):
        if not isinstance(schema.get(key), kind):
            return "%s is missing or not a %s" % (key, "list" if kind is list else "object" if kind is dict
                                                   else "string")
    messages, enums = schema["messages"], schema["enums"]
    for fq, m in messages.items():
        if not isinstance(m, dict) or not isinstance(m.get("fields"), list) or not isinstance(m.get("file"), str):
            return "message %s: no fields or no file" % fq
        for f in m["fields"]:
            if not (_strs(f, "name", "type", "label") and _is_int(f.get("n"), 1, (1 << 29) - 1)
                    and f["type"] in WIRE and f["label"] in LABELS.values()):
                return "message %s: a field that is not one (%r)" % (fq, f)
            if f["type"] in ("message", "enum") and f.get("typeName") not in (messages if f["type"] == "message"
                                                                               else enums):
                return "message %s: field %s names a type the schema has not" % (fq, f["name"])
    for fq, e in enums.items():
        if not isinstance(e, dict) or not isinstance(e.get("file"), str) or not isinstance(e.get("values"), list) \
                or not all(isinstance(v, list) and len(v) == 2 and type(v[0]) is int and isinstance(v[1], str)
                           for v in e["values"]):
            return "enum %s: no file, or values that are not [number, name] pairs" % fq
    for r in schema["roots"]:
        if not _strs(r, "root", "type") or r["type"] not in messages:
            return "a root that is not one (%r)" % (r,)
    for n in schema["nested"]:
        if not (_strs(n, "path", "message", "root", "type") and _is_int(n.get("n"), 1)
                and n["message"] in messages and n["type"] in messages):
            return "a nested root that is not one (%r)" % (n,)
    for e in schema["int64"]:
        if not _strs(e, "path", "field", "class", "type") or e["type"] not in INT64:
            return "an int64 entry that is not one (%r)" % (e,)
    for e in schema["uidBound"]:
        if not _strs(e, "path", "class"):
            return "a uidBound entry that is not one (%r)" % (e,)
    for kind in ("packed", "repeated", "scalar"):
        paths = schema["guids"].get(kind)
        if not isinstance(paths, list) or not all(isinstance(p, str) and p for p in paths):
            return "guids.%s is not a list of paths" % kind
    if not schema["fingerprints"]:
        return "no fingerprint"
    for fp in schema["fingerprints"]:
        if not (_strs(fp, "file", "sha256", "syntax") and _is_int(fp.get("offset"), 0)
                and _is_int(fp.get("size"), 1, SPAN_MAX) and _HEX64.match(fp["sha256"])
                and fp["syntax"] in ("proto2", "proto3")):
            return "a fingerprint that is not one (%r)" % (fp,)
    return None


def load_schema(path):
    try:
        with open(path, "rb") as f:
            schema = json.loads(f.read().decode("utf-8"))
    except (OSError, ValueError) as e:
        raise CompatError("%s: cannot be read (%s)" % (path, e))
    problem = schema_problem(schema)
    if problem:
        raise CompatError("%s: %s" % (path, problem))
    return schema


# ---------------------------------------------------------------------------------------------------------
# dbgate and identity

def compare_dbgate(gs_files, db_files, schema, gs_rejected=None, db_rejected=None):
    """The descriptor files dbgate shares with the gameserver -> (common names, problems, notes). A problem: a
    shared file whose bytes differ; a descriptor under the name of one binary's file that the other binary
    holds and that does not decode there (`*_rejected`: {name: [reasons]} of the scans, names that never
    decoded); a dbgate without the file of PlayerDataBin. A note: a file of the closure dbgate does not carry
    under any form."""
    problems, notes = [], []
    gs_rejected, db_rejected = gs_rejected or {}, db_rejected or {}
    common = sorted(set(gs_files) & set(db_files))
    closure = set(fp["file"] for fp in schema["fingerprints"])
    for name in common:
        a, b = gs_files[name], db_files[name]
        if (a["sha256"], a["size"]) != (b["sha256"], b["size"]):
            problems.append("%s%s: gameserver %d bytes sha256 %s, dbgate %d bytes sha256 %s" % (
                name, " (of the save closure)" if name in closure else "", a["size"], a["sha256"][:16], b["size"],
                b["sha256"][:16]))
    for name, why, held in ([(n, w, "dbgate") for n, w in db_rejected.items() if n in gs_files]
                            + [(n, w, "the gameserver") for n, w in gs_rejected.items() if n in db_files]):
        problems.append("%s%s: %s holds a descriptor of that name that does not decode (%s)" % (
            name, " (of the save closure)" if name in closure else "", held, "; ".join(why[:2])))
    problems.sort()
    player = schema["messages"][".proto.PlayerDataBin"]["file"]
    if player not in db_files and player not in db_rejected:
        problems.append("%s (the file of PlayerDataBin) is not in dbgate" % player)
    for name in sorted(closure - set(db_files) - set(db_rejected)):
        notes.append("%s is not in dbgate" % name)
    return common, problems, notes


def fingerprint_problems(schema, binary, chunk=None):
    """A schema's fingerprints held against a binary: [] when every descriptor span it names is there with its
    sha256. Reads only those spans, each in reads of at most `chunk` (HASH_CHUNK) bytes. CompatError when the
    binary cannot be read."""
    chunk = chunk or HASH_CHUNK
    problems = []
    try:
        size = os.path.getsize(binary)
        fh = open(binary, "rb")
    except OSError as e:
        raise CompatError("%s: cannot be read (%s)" % (binary, e))
    with fh:
        for fp in schema.get("fingerprints") or []:
            if fp["offset"] + fp["size"] > size:
                problems.append("%s@%d+%d: past the end of the binary (%d bytes)" % (
                    fp["file"], fp["offset"], fp["size"], size))
                continue
            fh.seek(fp["offset"])
            h, left = hashlib.sha256(), fp["size"]
            while left > 0:
                part = fh.read(min(chunk, left))
                if not part:
                    break
                h.update(part)
                left -= len(part)
            digest = h.hexdigest()
            if digest != fp["sha256"]:
                problems.append("%s@%d+%d: sha256 %s, the schema names %s" % (
                    fp["file"], fp["offset"], fp["size"], digest[:16], fp["sha256"][:16]))
    if not schema.get("fingerprints"):
        problems.append("the schema names no fingerprint")
    return problems


# ---------------------------------------------------------------------------------------------------------
# The pair proof

def _syntax_of(schema):
    return dict((fp["file"], fp.get("syntax")) for fp in schema["fingerprints"])


def _classes_by_path(schema):
    """({path: CLASS64 class}, {path: sorted uid-bound classes}) of a schema."""
    c64 = dict((e["path"], e["class"]) for e in schema["int64"])
    bound = {}
    for e in schema["uidBound"]:
        bound.setdefault(e["path"], []).append(e["class"])
    return c64, dict((p, sorted(cs)) for p, cs in bound.items())


def prove_format(a, b, droppable=None):
    """Whether a save of schema `a` means the same to the server of schema `b`. Fields are paired by path
    from each root, so a renamed type is still compared; a path both have keeps its classes. -> {"ok",
    "failures": [(kind, path, detail)], "drops": [paths], "typePairs", "added", "removed", "renamedTypes"};
    "added" / "removed" count fields per distinct pair of types, the way a field appears once in its
    message."""
    droppable = DROPPABLE if droppable is None else droppable
    am, bm = a["messages"], b["messages"]
    syn_a, syn_b = _syntax_of(a), _syntax_of(b)
    (c64_a, bound_a), (c64_b, bound_b) = _classes_by_path(a), _classes_by_path(b)
    failures, drops = [], set()
    pairs, added, removed, renamed, enum_pairs = set(), set(), set(), set(), set()

    def walk(ta, tb, path):
        ma, mb = am[ta], bm[tb]
        if (ta, tb) not in pairs and syn_a.get(ma["file"]) != syn_b.get(mb["file"]):
            # proto2 and proto3 differ in presence, defaults and enum closedness
            failures.append(("syntax-changed", path, "%s (%s) -> %s (%s)" % (
                ta, syn_a.get(ma["file"]), tb, syn_b.get(mb["file"]))))
        pairs.add((ta, tb))
        if ta != tb:
            renamed.add((ta, tb))
        if bool(ma.get("mapEntry")) != bool(mb.get("mapEntry")):
            failures.append(("map-entry-changed", path, "%s -> %s" % (ta, tb)))
        by_number = dict((g["n"], g) for g in mb["fields"])
        by_name = dict((g["name"], g["n"]) for g in mb["fields"])
        old = set()
        for f in ma["fields"]:
            old.add(f["n"])
            p = "%s.%d" % (path, f["n"])
            g = by_number.get(f["n"])
            if g is None:
                removed.add((ta, tb, f["n"]))
                pin = droppable.get(p)
                if f["name"] in by_name:
                    failures.append(("renumbered", p, "%s.%s: %d -> %d" % (ta, f["name"], f["n"], by_name[f["name"]])))
                elif pin and pin[:4] == (ta, f["name"], f["type"], f["label"]):
                    drops.add(p)
                else:
                    failures.append(("removed", p, "%s.%s (%s %s%s)" % (
                        ta, f["name"], f["label"], f["type"], " " + f["typeName"] if "typeName" in f else "")))
                continue
            for attr, kind in (("name", "renamed"), ("type", "retyped"), ("label", "label-changed"),
                               ("packed", "packing-changed"), ("oneof", "oneof-changed"),
                               ("proto3Optional", "presence-changed"), ("default", "default-changed")):
                if f.get(attr) != g.get(attr):
                    failures.append((kind, p, "%s.%s: %s -> %s" % (ta, f["name"], f.get(attr), g.get(attr))))
            for kind, ca, cb in (("class-changed", c64_a.get(p), c64_b.get(p)),
                                 ("uid-class-changed", bound_a.get(p), bound_b.get(p))):
                if ca != cb:
                    failures.append((kind, p, "%s.%s: %s -> %s" % (ta, f["name"], ca, cb)))
            if f["type"] == g["type"] == "message":
                walk(f["typeName"], g["typeName"], p)
            elif f["type"] == g["type"] == "enum":
                enum(f["typeName"], g["typeName"], p)
        for g in mb["fields"]:
            if g["n"] not in old:
                added.add((ta, tb, g["n"]))

    def enum(ea, eb, path):
        if (ea, eb) in enum_pairs:
            return
        enum_pairs.add((ea, eb))
        fa, fb = a["enums"][ea]["file"], b["enums"][eb]["file"]
        if syn_a.get(fa) != syn_b.get(fb):
            failures.append(("syntax-changed", path, "%s (%s) -> %s (%s)" % (ea, syn_a.get(fa), eb, syn_b.get(fb))))
        new = dict((n, name) for n, name in b["enums"][eb]["values"])
        for n, name in a["enums"][ea]["values"]:
            if n not in new:
                failures.append(("enum-value-removed", path, "%s %s = %d" % (ea, name, n)))
            elif new[n] != name:
                failures.append(("enum-value-renamed", path, "%s %d: %s -> %s" % (ea, n, name, new[n])))

    b_roots = dict((r["root"], r["type"]) for r in b["roots"])
    for r in a["roots"]:
        if r["root"] not in b_roots:
            failures.append(("root-missing", r["root"], "%s is not a message of the target" % r["type"]))
            continue
        walk(r["type"], b_roots[r["root"]], r["root"])
    b_nested = set((n["path"], n["root"]) for n in b["nested"])
    for n in a["nested"]:
        if (n["path"], n["root"]) not in b_nested and n["root"] in b_roots:
            failures.append(("nested-changed", n["path"], "holds %s on the source, not on the target" % n["root"]))
    return {"ok": not failures, "failures": failures, "drops": sorted(drops, key=_path_key),
            "typePairs": len(pairs), "added": len(added), "removed": len(removed), "renamedTypes": len(renamed)}


def prove_ids(source, target):
    """{category: missing ids} of the categories whose ids `target` does not all define; {} = a superset."""
    missing = {}
    for category, _src, _tgt in ID_CATEGORIES:
        gone = source[category]["source"] - target[category]["target"]
        if gone:
            missing[category] = sorted(gone)
    return missing


# ---------------------------------------------------------------------------------------------------------
# The tables of a stack

def txt_folder(root):
    """The folder of a stack's txt tables: server/data/txt, or the one nested data folder's txt."""
    flat = os.path.join(root, DATA_DIR, "txt")
    if os.path.isdir(flat):
        return flat
    try:
        inside = sorted(os.listdir(os.path.join(root, DATA_DIR)))
    except OSError:
        inside = []
    nested = [n for n in inside if _NESTED_DATA.search(n) and os.path.isdir(os.path.join(root, DATA_DIR, n, "txt"))]
    if len(nested) == 1:
        return os.path.join(root, DATA_DIR, nested[0], "txt")
    raise CompatError("%s: no %s/txt folder (nor one nested data folder holding one)" % (root, DATA_DIR))


def effective_stage(entry):
    """The stage of a payload manifest's files[] entry, as the agent reads it: `save` when it names none, and
    a `save` entry flagged `tsv` is a server data file, installed like `config`."""
    stage = entry.get("stage", "save")
    return "config" if stage == "save" and entry.get("tsv") else stage


def payload_tables(payloads, version):
    """{table file name: (path, stage)} of the txt tables the version's payload manifest puts into its
    stacks. A `config` table is in place in every stack of the version; a table of any other stage only in
    some (a txt fix can be reverted, a bootstrap file replaces only the contents it names, a save file goes in
    only with the shipped progress)."""
    path = os.path.join(payloads, version, MANIFEST_NAME)
    if not os.path.isfile(path):
        return {}
    try:
        with open(path, "rb") as f:
            manifest = json.loads(f.read().decode("utf-8"))
    except (OSError, ValueError) as e:
        raise CompatError("%s: cannot be read (%s)" % (path, e))
    out = {}
    for entry in manifest.get("files") or []:
        if not isinstance(entry, dict):
            continue
        dst = str(entry.get("dst") or "")
        folder, _, name = dst.rpartition("/")
        if folder == DATA_DIR + "/txt" and name.endswith(".txt") and entry.get("src"):
            out[name] = (os.path.join(payloads, version, *str(entry["src"]).split("/")), effective_stage(entry))
    return out


def table_variants(folder, replaced, name):
    """The forms of one table a stack may run -> [(path, what)]: the vendor's (its <name>.orig where the agent
    keeps one), the payload's, or both; [] when the stack has neither."""
    vendor = os.path.join(folder, name)
    if os.path.isfile(vendor + ".orig"):
        vendor += ".orig"
    elif not os.path.isfile(vendor):
        vendor = None
    if name in replaced:
        path, stage = replaced[name]
        if stage == "config" or vendor is None:
            return [(path, "payload (%s)" % stage)]
        return [(vendor, "vendor"), (path, "payload (%s)" % stage)]
    return [(vendor, "vendor")] if vendor else []


def stack_tables(root, version, payloads):
    """{table file name: its variants} of every txt table a stack holds or its payload puts in place."""
    folder = txt_folder(root)
    replaced = payload_tables(payloads, version)
    try:
        names = set(n for n in os.listdir(folder) if n.endswith(".txt"))
    except OSError as e:
        raise CompatError("%s: cannot be listed (%s)" % (folder, e))
    out = {}
    for name in sorted(names | set(replaced)):
        variants = table_variants(folder, replaced, name)
        if variants:
            out[name] = variants
    return out


def _table_lines(path):
    """(header cells as text, iterator of row cell lists as bytes): the file is read line by line."""
    try:
        fh = open(path, "rb")
    except OSError as e:
        raise CompatError("%s: cannot be read (%s)" % (path, e))
    first = fh.readline()
    if first.startswith(b"\xef\xbb\xbf"):
        first = first[3:]
    try:
        header = first.rstrip(b"\r\n").decode("utf-8").split("\t")
    except UnicodeDecodeError:
        fh.close()
        raise CompatError("%s: the header is not UTF-8" % path)

    def rows():
        with fh:
            for line in fh:
                yield line.rstrip(b"\r\n").split(b"\t")
    return header, rows()


_ID = re.compile(rb"-?[0-9]+")


def table_ids(path):
    """The ids of a table: the first cell of every row that holds a number."""
    _header, rows = _table_lines(path)
    return set(int(cells[0]) for cells in rows if _ID.fullmatch(cells[0].strip()))


def depot_ids(path):
    """The skill depots the avatar table names: its DEPOT_COLUMNS (one id; a comma-separated list)."""
    header, rows = _table_lines(path)
    cols = []
    for name in DEPOT_COLUMNS:
        if name not in header:
            for _ in rows:
                pass
            raise CompatError("%s: no column %s" % (path, name))
        cols.append(header.index(name))
    ids = set()
    for line, cells in enumerate(rows, 2):
        if not _ID.fullmatch(cells[0].strip()):
            continue
        for c in cols:
            cell = cells[c].strip() if c < len(cells) else b""
            for part in cell.split(b",") if cell else []:
                if not _ID.fullmatch(part.strip()):
                    raise CompatError("%s:%d: %r is not a list of ids" % (path, line, cell))
                ids.add(int(part))
    return ids


def _union(sets):
    out = set()
    for s in sets:
        out |= s
    return out


def _common(sets):
    """The ids every one of the sets holds (none for no set)."""
    sets = list(sets)
    out = set(sets[0]) if sets else set()
    for s in sets[1:]:
        out &= s
    return out


def read_ids(root, version, payloads):
    """{category: {"source": ids a save of this version can hold, "target": ids this version defines}, plus
    "_files": {table: [(path, what)]} the forms read}. A table is read in every form a stack of the version
    may run (table_variants): a save can hold the ids of any of them, the version defines those of all."""
    folder = txt_folder(root)
    replaced = payload_tables(payloads, version)
    cache, files = {}, {}

    def variants(table):
        if table not in files:
            files[table] = table_variants(folder, replaced, table + ".txt")
            if not files[table]:
                raise CompatError("%s: no table %s.txt" % (folder, table))
        return files[table]

    def rows(table):
        if table not in cache:
            cache[table] = [table_ids(p) for p, _what in variants(table)]
        return cache[table]

    out = {}
    for category, (src_table, src_rule), (tgt_table, _rule) in ID_CATEGORIES:
        if src_rule == "rows":
            source = _union(rows(src_table))
        else:
            source = _union(depot_ids(p) for p, _what in variants(src_table)) & _union(rows(DEPOT_TABLE))
        out[category] = {"source": source, "target": _common(rows(tgt_table))}
    out["_files"] = files
    return out


def sweep_ids(tables, gacha, pairs, log):
    """The id sweep, a report: for each (source, target) of `pairs`, every table both stacks hold whose ids in
    any form of the source's are not in every form of the target's, the tables only the source holds, and
    the gacha schedule ids the target's database lacks. `tables` = {version: stack_tables(...)}, `gacha` =
    {version: schedule ids}. One table is read at a time; a table that cannot be read is named."""
    log("== id sweep (a report for the transforms of a migration; the blocking categories are the proof)")
    if not pairs:
        log("   no pair passes between two versions")
        return
    gone = dict((pair, []) for pair in pairs)
    only = dict((pair, []) for pair in pairs)
    versions = sorted(set(v for pair in pairs for v in pair))
    for name in sorted(set(n for v in versions for n in tables[v])):
        ids = {}
        for v in versions:
            if name in tables[v]:
                try:
                    sets = [table_ids(p) for p, _what in tables[v][name]]
                except CompatError as e:
                    log("   note: %s" % e)
                    continue
                ids[v] = (_union(sets), _common(sets))
        for s, t in pairs:
            if s in ids and t not in ids and name not in tables[t]:
                only[(s, t)].append(name[:-len(".txt")])
            elif s in ids and t in ids and ids[s][0] - ids[t][1]:
                gone[(s, t)].append((name[:-len(".txt")], sorted(ids[s][0] - ids[t][1])))
    for s, t in pairs:
        log("%s -> %s: %d table(s) with ids the target's tables lack, %d table(s) the target has not" % (
            s, t, len(gone[(s, t)]), len(only[(s, t)])))
        for name, missing in gone[(s, t)]:
            log("    %s %d: %s%s" % (name, len(missing), ", ".join(str(x) for x in missing[:8]),
                                     " ..." if len(missing) > 8 else ""))
        if only[(s, t)]:
            log("    only the source has: %s" % ", ".join(only[(s, t)]))
        lacks = sorted(gacha[s] - gacha[t])
        log("    gacha schedules (%s.%s): the target lacks %s" % (
            GACHA_TABLE, GACHA_COLUMN, ", ".join(str(x) for x in lacks) if lacks else "none"))


# ---------------------------------------------------------------------------------------------------------
# SQL: the client versions a stack binds, and the rows of a dump

def _insert_re(names=None):
    """An INSERT [IGNORE] or REPLACE ... INTO statement up to its VALUES: the table (group 1 quoted, group 2
    not; a database name before it or not) and the column list (group 3, when there is one). `names` limits
    it to those tables (bytes)."""
    quoted = rb"[^`]+" if names is None else b"|".join(re.escape(n) for n in names)
    bare = rb"[A-Za-z0-9_$]+" if names is None else quoted
    return re.compile(
        rb"(?:INSERT(?:\s+(?:LOW_PRIORITY|DELAYED|HIGH_PRIORITY))?(?:\s+IGNORE)?|REPLACE(?:\s+(?:LOW_PRIORITY|DELAYED))?)"
        rb"\s+INTO\s+(?:(?:`[^`]+`|[A-Za-z0-9_$]+)\.)?(?:`(" + quoted + rb")`|(" + bare + rb")(?![A-Za-z0-9_$]))"
        rb"\s*(?:\(([^)]*)\))?\s*VALUES?\s*", re.I)


_INSERT = _insert_re()
_CREATE = re.compile(rb"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?:`[^`]+`|[A-Za-z0-9_$]+)\.)?"
                     rb"(?:`([^`]+)`|([A-Za-z0-9_$]+))\s*\((.*?)\n\)", re.I | re.S)
# The lines of a CREATE TABLE body that are no column.
_NOT_COLUMN = frozenset((b"PRIMARY", b"KEY", b"UNIQUE", b"INDEX", b"CONSTRAINT", b"FULLTEXT", b"SPATIAL",
                         b"FOREIGN", b"CHECK", b"PERIOD"))
_ESCAPES = {ord("0"): b"\x00", ord("b"): b"\x08", ord("n"): b"\n", ord("r"): b"\r", ord("t"): b"\t",
            ord("Z"): b"\x1a"}
_HEX = re.compile(rb"0[xX]([0-9a-fA-F]*)|[xX]'([0-9a-fA-F]*)'")
_CALL = re.compile(rb"([A-Za-z_][A-Za-z0-9_]*)\s*\(")
_TOKEN = re.compile(rb"[^,)\s]+")


def _skip_space(data, i):
    while i < len(data) and data[i] in b" \t\r\n":
        i += 1
    return i


def _sql_string(data, i, where):
    """A quoted string from after its opening quote -> (bytes, next offset)."""
    out = bytearray()
    while True:
        q = data.find(b"'", i)
        b = data.find(b"\\", i)
        if q < 0:
            raise CompatError("%s: a string that does not end" % where)
        if 0 <= b < q:
            out += data[i:b]
            if b + 1 >= len(data):
                raise CompatError("%s: a string that does not end" % where)
            c = data[b + 1]
            # \% and \_ keep their backslash; every other escaped character stands for itself
            out += _ESCAPES.get(c, b"\\" + bytes((c,)) if c in b"%_" else bytes((c,)))
            i = b + 2
            continue
        out += data[i:q]
        if data[q + 1:q + 2] == b"'":
            out += b"'"
            i = q + 2
            continue
        return bytes(out), q + 1


def _sql_value(data, i, where):
    if data.startswith(b"'", i):
        return _sql_string(data, i + 1, where)
    if data.startswith(b"_binary", i):
        j = _skip_space(data, i + 7)
        if data.startswith(b"'", j):
            return _sql_string(data, j + 1, where)
    m = _HEX.match(data, i)
    if m and len(m.group(1) if m.group(1) is not None else m.group(2)) % 2 == 0:
        return bytes.fromhex((m.group(1) if m.group(1) is not None else m.group(2)).decode("ascii")), m.end()
    if data.startswith(b"NULL", i) and not data[i + 4:i + 5].isalnum():
        return None, i + 4
    m = _CALL.match(data, i)
    if m:
        # a function call: its arguments read as values; UNHEX of one hex string gives its bytes, any other
        # call its text
        args, j = [], _skip_space(data, m.end())
        while not data.startswith(b")", j):
            value, j = _sql_value(data, j, where)
            args.append(value)
            j = _skip_space(data, j)
            if data.startswith(b",", j):
                j = _skip_space(data, j + 1)
            elif not data.startswith(b")", j):
                raise CompatError("%s: a function call that does not continue at byte %d" % (where, j))
        if m.group(1).upper() == b"UNHEX" and len(args) == 1 and isinstance(args[0], bytes):
            try:
                return bytes.fromhex(args[0].decode("ascii")), j + 1
            except ValueError:
                raise CompatError("%s: UNHEX of a string that is not hex at byte %d" % (where, i))
        return data[i:j + 1].decode("utf-8", "replace"), j + 1
    m = _TOKEN.match(data, i)
    if not m:
        raise CompatError("%s: a value that cannot be read at byte %d" % (where, i))
    return m.group(0).decode("ascii", "replace"), m.end()


def sql_rows(data, where, tables=None):
    """Every row of every INSERT [IGNORE] / REPLACE INTO ... VALUES of SQL text (bytes) -> (table, {column:
    value}); a table name may be quoted or not. A string is bytes (backslash escapes and doubled quotes
    undone), a hex literal bytes, NULL None, a number its text. A statement without a column list takes the
    columns of the dump's CREATE TABLE. `tables` limits the statements read to those tables: the others are not
    parsed."""
    columns = {}
    for m in _CREATE.finditer(data):
        cols = []
        for line in m.group(3).split(b"\n"):
            line = line.strip()
            if line.startswith(b"`"):
                cols.append(line.split(b"`")[1].decode("utf-8", "replace"))
            elif line:
                word = re.match(rb"[A-Za-z0-9_$]+", line)
                if word and word.group(0).upper() not in _NOT_COLUMN:
                    cols.append(word.group(0).decode("utf-8", "replace"))
        columns[(m.group(1) or m.group(2)).decode("utf-8", "replace")] = cols
    insert = _INSERT if tables is None else _insert_re([t.encode("utf-8") for t in tables])
    pos = 0
    while True:
        m = insert.search(data, pos)
        if not m:
            return
        table = (m.group(1) or m.group(2)).decode("utf-8", "replace")
        if m.group(3) is not None:
            cols = [c.strip().strip(b"`").decode("utf-8", "replace") for c in m.group(3).split(b",")]
        elif table in columns:
            cols = columns[table]
        else:
            raise CompatError("%s: INSERT INTO %s names no columns and no CREATE TABLE of it precedes" % (where, table))
        here = "%s (INSERT INTO %s)" % (where, table)
        i = m.end()
        while True:
            i = _skip_space(data, i)
            if not data.startswith(b"(", i):
                raise CompatError("%s: a row does not start with ( at byte %d" % (here, i))
            i += 1
            values = []
            while True:
                i = _skip_space(data, i)
                value, i = _sql_value(data, i, here)
                values.append(value)
                i = _skip_space(data, i)
                if data.startswith(b",", i):
                    i += 1
                    continue
                if data.startswith(b")", i):
                    i += 1
                    break
                raise CompatError("%s: a row that does not continue at byte %d" % (here, i))
            if len(values) != len(cols):
                raise CompatError("%s: a row of %d values for %d columns" % (here, len(values), len(cols)))
            if tables is None or table in tables:
                yield table, dict(zip(cols, values))
            i = _skip_space(data, i)
            if data.startswith(b",", i):
                i += 1
                continue
            if data.startswith(b";", i):
                pos = i + 1
                break
            raise CompatError("%s: a statement that does not end at byte %d" % (here, i))


def stack_sql(root):
    """What the stack's database is made with (data.sql.tmpl) -> (client prefixes, gacha schedule ids). A
    prefix is a client version it binds (t_bind_config.client_version) with the "_" that follows it in a
    save's client_app_version, sorted; the schedule ids are those of t_gacha_schedule_config."""
    path = os.path.join(root, DATA_SQL)
    try:
        with open(path, "rb") as f:
            data = f.read()
    except OSError as e:
        raise CompatError("%s: cannot be read (%s)" % (path, e))
    out, schedules = set(), set()
    for table, row in sql_rows(data, path, tables=(BIND_TABLE, GACHA_TABLE)):
        column = BIND_COLUMN if table == BIND_TABLE else GACHA_COLUMN
        value = row.get(column)
        text = value.decode("utf-8", "replace") if isinstance(value, bytes) else value
        if table == BIND_TABLE:
            if not isinstance(text, str) or not _CLIENT_VERSION.match(text):
                raise CompatError("%s: %s.%s holds %r, not a client version" % (path, table, column, value))
            out.add(text + "_")
        else:
            if not isinstance(text, str) or not re.match(r"^[0-9]+$", text):
                raise CompatError("%s: %s.%s holds %r, not an id" % (path, table, column, value))
            schedules.add(int(text))
    if not out:
        raise CompatError("%s: no %s row names a client version" % (path, BIND_TABLE))
    return sorted(out), schedules


# ---------------------------------------------------------------------------------------------------------
# Decoding saves

class _Undecodable(Exception):
    pass


class SaveWalk(object):
    """Walks serialized saves with one schema: counts the fields read, the fields the schema does not know
    (by path and wire type) and the fields whose wire type does not fit the schema's type; collects the high
    word of every 64-bit value whose class is not guid."""

    def __init__(self, schema):
        self.fields = dict((fq, dict((f["n"], f) for f in m["fields"])) for fq, m in schema["messages"].items())
        self.nested = dict(((n["message"], n["n"]), (n["root"], n["type"])) for n in schema["nested"])
        self.class64 = dict((e["field"], e["class"]) for e in schema["int64"])
        self.count = 0
        self.unknown, self.mismatch, self.high = {}, {}, {}
        self.capture = {}

    def _tick(self, table, key):
        table[key] = table.get(key, 0) + 1

    def walk(self, buf, mtype, names, path):
        fields = self.fields[mtype]
        i, end = 0, len(buf)
        try:
            while i < end:
                tag, i = _varint(buf, i, end)
                num, wt = tag >> 3, tag & 7
                if num == 0:
                    raise _Undecodable("field number 0 at %s" % path)
                if wt == 0:
                    value, i = _varint(buf, i, end)
                elif wt == 1:
                    if i + 8 > end:
                        raise _Short()
                    value, i = int.from_bytes(buf[i:i + 8], "little"), i + 8
                elif wt == 5:
                    if i + 4 > end:
                        raise _Short()
                    value, i = int.from_bytes(buf[i:i + 4], "little"), i + 4
                elif wt == 2:
                    size, i = _varint(buf, i, end)
                    if size > end - i:
                        raise _Short()
                    value, i = buf[i:i + size], i + size
                else:
                    raise _Undecodable("wire type %d at %s" % (wt, path))
                self.count += 1
                f = fields.get(num)
                p = "%s.%d" % (path, num)
                if f is None:
                    self._tick(self.unknown, (p, "%s.%d" % (names, num), wt))
                    continue
                named = "%s.%s" % (names, f["name"])
                ftype = f["type"]
                if wt == WIRE[ftype]:
                    if ftype == "message":
                        self.walk(value, f["typeName"], named, p)
                    elif (mtype, num) in self.nested:
                        root, inner = self.nested[(mtype, num)]
                        self.walk(value, inner, root, root)
                    elif ftype in INT64:
                        self._high(mtype, f, value)
                    elif named in self.capture:
                        self.capture[named] = bytes(value).decode("utf-8", "replace")
                elif wt == 2 and f["label"] == "repeated" and ftype in PACKABLE:
                    for v in self._packed(value, ftype, p):
                        if ftype in INT64:
                            self._high(mtype, f, v)
                else:
                    self._tick(self.mismatch, (p, named, wt, ftype))
        except (_Short, _Bad) as e:
            raise _Undecodable("%s at %s" % (str(e) or "the bytes end inside a field", path))

    def _packed(self, data, ftype, path):
        if WIRE[ftype] == 0:
            out, i = [], 0
            while i < len(data):
                v, i = _varint(data, i, len(data))
                out.append(v)
            return out
        width = 8 if WIRE[ftype] == 1 else 4
        if len(data) % width:
            raise _Undecodable("a packed list of %d bytes at %s" % (len(data), path))
        return [int.from_bytes(data[j:j + width], "little") for j in range(0, len(data), width)]

    def _high(self, mtype, f, value):
        cls = self.class64.get("%s.%s" % (mtype, f["name"]))
        if f["type"] == "sint64":
            value = (value >> 1) ^ -(value & 1)
        if cls != "guid" and value >> 32:
            self._tick(self.high, (cls, value >> 32 & 0xFFFFFFFF))


def _unwrap(blob):
    """A stored blob -> its message bytes: "ZLIB" + a zlib stream, or the bytes themselves."""
    if blob[:4] == b"ZLIB":
        try:
            return zlib.decompress(blob[4:])
        except zlib.error as e:
            raise _Undecodable("ZLIB blob: %s" % e)
    return blob


_SHARD = re.compile(r"^(t_player_data|t_block_data|t_home_data)_[0-9]+$")


def dump_rows(path):
    """The save rows of a dump -> [(table base, uid, {column: value})]. CompatError for a dump without a
    t_player_data_N row: there is no save in it to decode."""
    try:
        with open(path, "rb") as f:
            data = f.read()
    except OSError as e:
        raise CompatError("%s: cannot be read (%s)" % (path, e))
    rows = []
    for table, row in sql_rows(data, path):
        m = _SHARD.match(table)
        if m:
            rows.append((m.group(1), str(row.get("uid")), row))
    if not any(base == "t_player_data" for base, _uid, _row in rows):
        raise CompatError("%s: no save in it -- no row of a t_player_data_N table" % path)
    return rows


def decode_dump(path, schemas, prefixes):
    """Every save row of a dump walked with every schema -> (lines, failed). A uid is claimed by the
    version whose client prefixes its save's client_app_version (CLIENT_FIELD) starts with; on that version
    every row of the uid must walk clean: no unknown field, no mismatch, nothing undecodable and no blob of a
    root the schema lacks. Two versions claiming one uid fail it; a uid no version claims must walk clean on
    one of the versions given at least."""
    rows = dump_rows(path)
    lines, failed = [], False
    kinds = {}
    for base, _uid, row in rows:
        kinds[base] = kinds.get(base, 0) + 1
    lines.append("rows: %s" % ", ".join("%s %d" % (k, kinds[k]) for k in sorted(kinds)))
    clients, results = {}, {}
    for version in sorted(schemas):
        schema = schemas[version]
        w = SaveWalk(schema)
        roots = dict((r["root"], r) for r in schema["roots"])
        by_column = dict(((r.get("table"), r.get("column")), r["root"]) for r in schema["roots"] if r.get("table"))
        undecodable, missing_root = [], {}
        per_uid = {}  # uid -> [unknown fields, mismatches, rows of a root the schema has not, undecodable rows]
        for base, uid, row in rows:
            tally = per_uid.setdefault(uid, [0, 0, 0, 0])
            for root, _fq, table, column, _required in ROOTS:
                if table != base or not isinstance(row.get(column), (bytes, bytearray)) or not row.get(column):
                    continue
                if (table, column) not in by_column:
                    missing_root[root] = missing_root.get(root, 0) + 1
                    tally[2] += 1
                    continue
                if root == "PlayerDataBin":
                    w.capture = {CLIENT_FIELD: None}
                unknown, mismatch = sum(w.unknown.values()), sum(w.mismatch.values())
                try:
                    w.walk(_unwrap(bytes(row[column])), roots[root]["type"], root, root)
                except _Undecodable as e:
                    undecodable.append("uid %s %s.%s: %s" % (uid, table, column, e))
                    tally[3] += 1
                if root == "PlayerDataBin" and w.capture.get(CLIENT_FIELD):
                    clients.setdefault(uid, w.capture[CLIENT_FIELD])
                w.capture = {}
                tally[0] += sum(w.unknown.values()) - unknown
                tally[1] += sum(w.mismatch.values()) - mismatch
        results[version] = (w, undecodable, missing_root, per_uid)
    claims = {}
    for uid, client in sorted(clients.items()):
        claimed = sorted(v for v, pre in prefixes.items() if any(client.startswith(x) for x in pre))
        claims[uid] = claimed
        lines.append("uid %s: client %s -> %s" % (uid, client, ", ".join(claimed) if claimed else
                                                  "claimed by no version given"))
    if not any(prefixes.get(v) for v in schemas):
        lines.append("no client prefixes known: each uid must walk clean on one of the versions given")
    for version in sorted(results):
        w, undecodable, missing_root, per_uid = results[version]
        own = sorted(uid for uid, c in claims.items() if c == [version])
        lines.append("on %s%s: fields %d, unknown %d kind(s) (%d fields), mismatches %d kind(s) (%d fields)%s" % (
            version, " (own version of uid %s)" % ", ".join(own) if own else "", w.count, len(w.unknown),
            sum(w.unknown.values()), len(w.mismatch), sum(w.mismatch.values()),
            "; rows of a root it has not: %s" % ", ".join("%s %d" % kv for kv in sorted(missing_root.items()))
            if missing_root else ""))
        for (p, named, wt), n in sorted(w.unknown.items(), key=lambda kv: (_path_key(kv[0][0]), kv[0][2]))[:12]:
            lines.append("    UNKNOWN %s [%s] wire %d x%d" % (named, p, wt, n))
        if len(w.unknown) > 12:
            lines.append("    ... %d more unknown kinds" % (len(w.unknown) - 12))
        for (p, named, wt, ftype), n in sorted(w.mismatch.items())[:12]:
            lines.append("    MISMATCH %s [%s] wire %d for %s x%d" % (named, p, wt, ftype, n))
        for text in undecodable[:5]:
            lines.append("    UNDECODABLE %s" % text)
        if w.high:
            by_class = {}
            for (cls, high), n in w.high.items():
                by_class.setdefault(cls, {})[high] = n
            lines.append("    64-bit values off the guid paths whose high word is not 0 (a copy from a source uid "
                         "equal to one must not touch them): %s" % "; ".join(
                             "%s %s" % (cls, ", ".join("%d x%d" % (h, n) for h, n in sorted(highs.items())[:6]))
                             for cls, highs in sorted(by_class.items())))
    for uid in sorted(set(uid for _base, uid, _row in rows)):
        claimed = claims.get(uid, [])
        clean = [v for v in sorted(results) if not any(results[v][3].get(uid, ()))]
        if len(claimed) > 1:
            failed = True
            lines.append("FAIL uid %s: two versions claim its client string (%s)" % (uid, ", ".join(claimed)))
        elif claimed and claimed[0] not in clean:
            failed = True
            lines.append("FAIL uid %s does not decode cleanly on %s, the version its client names" % (
                uid, claimed[0]))
        elif not claimed and not clean:
            failed = True
            lines.append("FAIL uid %s, claimed by no version, decodes cleanly on none of %s" % (
                uid, ", ".join(sorted(results))))
        elif not claimed:
            lines.append("uid %s, claimed by no version, decodes cleanly on %s" % (uid, ", ".join(clean)))
    return lines, failed


# ---------------------------------------------------------------------------------------------------------
# The run

def parse_stacks(values):
    stacks = []
    for value in values or []:
        version, sep, root = value.partition("=")
        if not sep or not re.match(r"^[0-9]+\.[0-9]+$", version) or not root:
            raise CompatError("--stack wants <version>=<stack folder>, e.g. 2.8=D:/servers/2.8 (got %r)" % value)
        if version in [v for v, _ in stacks]:
            raise CompatError("--stack names version %s twice" % version)
        stacks.append((version, root))
    return stacks


def build_stack(version, root, payloads, log):
    """One stack -> {"schema", "data", "prefixes", "gacha", "ids", "tables", "dbgateOk"}; prints what it
    found. Writes nothing."""
    gs_path = os.path.join(root, *GAMESERVER.split("/"))
    db_path = os.path.join(root, *DBGATE.split("/"))
    gs_files, gs_stats = scan_descriptors(gs_path)
    try:
        schema, report = build_schema(version, gs_files)
    except CompatError as e:
        rejected = sorted(gs_stats["rejected"].items())
        if not rejected:
            raise
        raise CompatError("%s\n  descriptor names found in %s whose bytes did not decode:\n    %s" % (
            e, gs_path, "\n    ".join("%s @ %s" % (name, "; ".join(why[:2])) for name, why in rejected[:12])))
    log("   gameserver: %d bytes, %d descriptor files (%d top-level messages, %d duplicate copies)" % (
        gs_stats["size"], gs_stats["files"], gs_stats["messages"], gs_stats["duplicates"]))
    log("   save closure: %d messages, %d enums, %d field paths; roots %s" % (
        report["messages"], report["enums"], report["paths"], ", ".join(report["roots"])))
    for fp in schema["fingerprints"]:
        log("   fingerprint %s@%#x+%d sha256 %s" % (fp["file"], fp["offset"], fp["size"], fp["sha256"]))
    db_files, db_stats = scan_descriptors(db_path)
    common, problems, notes = compare_dbgate(gs_files, db_files, schema, gs_stats["rejected"], db_stats["rejected"])
    log("   dbgate: %d bytes, %d descriptor files, %d shared with the gameserver: %s" % (
        db_stats["size"], db_stats["files"], len(common), "identical" if not problems else "NOT THE GAMESERVER'S"))
    for text in problems:
        log("   DBGATE %s" % text)
    for text in notes:
        log("   note: %s" % text)
    counts = {}
    for e in schema["int64"]:
        counts[e["class"]] = counts.get(e["class"], 0) + 1
    g = schema["guids"]
    types = dict((e["path"], e["type"]) for e in schema["int64"])
    log("   64-bit paths %d: %s; guids packed %d, repeated %d, scalar %d (fixed64 %d)" % (
        len(schema["int64"]), ", ".join("%s %d" % kv for kv in sorted(counts.items())), len(g["packed"]),
        len(g["repeated"]), len(g["scalar"]), sum(types[p] == "fixed64" for p in g["scalar"])))
    ucounts = {}
    for e in schema["uidBound"]:
        ucounts[e["class"]] = ucounts.get(e["class"], 0) + 1
    log("   uid-bound paths %d: %s" % (len(schema["uidBound"]),
                                       ", ".join("%s %d" % kv for kv in sorted(ucounts.items()))))
    prefixes, gacha = stack_sql(root)
    log("   client prefixes (%s): %s" % (DATA_SQL, ", ".join(prefixes)))
    log("   gacha schedules (%s): %s" % (DATA_SQL, ", ".join(str(x) for x in sorted(gacha)) or "none"))
    ids = read_ids(root, version, payloads)
    log("   ids: %s" % ", ".join("%s %d/%d" % (c, len(ids[c]["source"]), len(ids[c]["target"]))
                                 for c, _s, _t in ID_CATEGORIES))
    tables = stack_tables(root, version, payloads)
    log("   tables: %d; read in more than the vendor's form: %s" % (len(tables), ", ".join(
        "%s (%s)" % (name[:-len(".txt")], " or ".join(what for _p, what in forms))
        for name, forms in sorted(tables.items()) if [w for _p, w in forms] != ["vendor"]) or "none"))
    return {"schema": schema, "data": encode(schema), "prefixes": prefixes, "gacha": gacha, "ids": ids,
            "tables": tables, "dbgateOk": not problems}


def saves_blocks(built, proofs):
    """{target: the saves block of its manifest}: every source whose pair proof passes, itself included."""
    blocks = {}
    for target in sorted(built):
        accept = {}
        for source in sorted(built):
            result = proofs[(source, target)]
            if result["ok"]:
                accept[source] = {"drop": result["drops"]}
        blocks[target] = {"schema": SCHEMA_NAME, "clientPrefixes": built[target]["prefixes"], "acceptFrom": accept}
    return blocks


def prove_pairs(built, log):
    proofs = {}
    log("== pair proofs (a save of the source read by the target's server)")
    for source in sorted(built):
        for target in sorted(built):
            fmt = prove_format(built[source]["schema"], built[target]["schema"])
            missing = prove_ids(built[source]["ids"], built[target]["ids"])
            ok = fmt["ok"] and not missing
            proofs[(source, target)] = {"ok": ok, "drops": fmt["drops"], "format": fmt, "ids": missing}
            kinds = {}
            for kind, _p, _d in fmt["failures"]:
                kinds[kind] = kinds.get(kind, 0) + 1
            log("%s -> %s: %s  type pairs %d, fields added %d, removed %d%s%s; %s" % (
                source, target, "PASS" if ok else "FAIL", fmt["typePairs"], fmt["added"], fmt["removed"],
                "; drop %s" % ", ".join(fmt["drops"]) if fmt["drops"] else "",
                "; format: %s" % ", ".join("%s %d" % kv for kv in sorted(kinds.items())) if kinds else "",
                "ids missing on the target: %s" % ", ".join("%s %d" % (c, len(v)) for c, v in sorted(missing.items()))
                if missing else "the target defines every id of the %d categories" % len(ID_CATEGORIES)))
            for p in fmt["drops"]:
                log("    drop %s: %s" % (p, DROPPABLE[p][4]))
            for kind, p, detail in fmt["failures"][:6]:
                log("    %s %s: %s" % (kind, p, detail))
            if len(fmt["failures"]) > 6:
                log("    ... %d more" % (len(fmt["failures"]) - 6))
            for c, v in sorted(missing.items()):
                log("    %s: %s%s" % (c, ", ".join(str(x) for x in v[:10]), " ..." if len(v) > 10 else ""))
    return proofs


def unused_entries(built, proofs):
    """The entries of the tool's tables that no stack of the run has: worth a look, not an error."""
    seen = set((fq, f["name"]) for item in built.values() for fq, m in item["schema"]["messages"].items()
               for f in m["fields"])
    drops = set(p for r in proofs.values() for p in r["drops"])
    out = []
    for name, table in (("CLASS64", CLASS64), ("UID_FIELDS", UID_FIELDS), ("UID_EXTRA", UID_EXTRA),
                        ("UID_BOUND", UID_BOUND), ("NESTED", NESTED)):
        out.extend("%s %s.%s" % (name, m, f) for m, f in sorted(set(table) - seen))
    out.extend("DROPPABLE %s (no pair of the run drops it)" % p for p in sorted(set(DROPPABLE) - drops))
    return out


def schema_versions(out_dir):
    """The versions that have a <version>/save_schema.json in a folder, sorted."""
    try:
        names = sorted(os.listdir(out_dir))
    except OSError as e:
        raise CompatError("%s: cannot be listed (%s)" % (out_dir, e))
    return [n for n in names if re.match(r"^[0-9]+\.[0-9]+$", n)
            and os.path.isfile(os.path.join(out_dir, n, SCHEMA_NAME))]


def load_schemas(out_dir):
    return dict((v, load_schema(os.path.join(out_dir, v, SCHEMA_NAME))) for v in schema_versions(out_dir))


def manifest_block(out_dir, version):
    """The saves block of the manifest beside a schema -> (state, block): state "none" (no manifest),
    "absent" (a manifest without one) or "present"."""
    path = os.path.join(out_dir, version, MANIFEST_NAME)
    if not os.path.isfile(path):
        return "none", None
    try:
        with open(path, "rb") as f:
            manifest = json.loads(f.read().decode("utf-8"))
    except (OSError, ValueError) as e:
        raise CompatError("%s: cannot be read (%s)" % (path, e))
    if not isinstance(manifest, dict):
        raise CompatError("%s: not a manifest (no JSON object)" % path)
    if "saves" not in manifest:
        return "absent", None
    return "present", manifest["saves"]


def main(argv, out=None):
    out = out or sys.stdout

    def log(text=""):
        out.write(text + "\n")
        out.flush()

    ap = argparse.ArgumentParser(description="Prove save compatibility between server versions and write "
                                             "<version>/%s." % SCHEMA_NAME)
    ap.add_argument("--stack", action="append", default=[], metavar="VERSION=FOLDER",
                    help="a server stack to read, e.g. 2.8=D:/servers/2.8 (repeatable)")
    ap.add_argument("--out-dir", default=DEFAULT_OUT, metavar="FOLDER",
                    help="where <version>/%s goes (default: agent/payloads)" % SCHEMA_NAME)
    ap.add_argument("--payloads", default=DEFAULT_OUT, metavar="FOLDER",
                    help="the payload folder whose manifests name the tables a version replaces (default: "
                         "agent/payloads; only read)")
    ap.add_argument("--check", action="store_true",
                    help="write nothing; exit 1 when a schema in --out-dir is not what its stack yields or "
                         "belongs to no stack given (give every stack)")
    ap.add_argument("--saves", nargs="+", metavar="DUMP", help="decode the saves of SQL dumps; writes nothing")
    ap.add_argument("--identify", metavar="FOLDER",
                    help="which schema of --out-dir a stack folder's gameserver matches")
    ap.add_argument("--selftest", action="store_true", help="run the self-test in a temporary folder")
    args = ap.parse_args(argv)
    if args.selftest:
        return selftest(log)
    try:
        if args.identify:
            return identify(args.identify, load_schemas(args.out_dir), log)
        stacks = parse_stacks(args.stack)
        if not stacks and not args.saves:
            raise CompatError("--stack is needed (or --saves, --identify, --selftest)")
        built, stale, bad = {}, 0, 0
        # every stack is read, and the stacks held against each other, before anything is written
        for version, root in stacks:
            log("== %s  (%s)" % (version, root))
            built[version] = build_stack(version, root, args.payloads, log)
        prefixes = dict((v, item["prefixes"]) for v, item in built.items())
        owners = {}
        for v, pre in prefixes.items():
            for x in pre:
                owners.setdefault(x, []).append(v)
        shared = sorted(x for x, vs in owners.items() if len(vs) > 1)
        if shared:
            raise CompatError("client prefixes bound by more than one stack: %s" % ", ".join(shared))
        failing = sorted(v for v, item in built.items() if not item["dbgateOk"])
        bad += len(failing)
        good = dict((v, item) for v, item in built.items() if item["dbgateOk"])
        if built:
            if failing:
                log("== left out of the pair proofs: %s (the dbgate does not carry the gameserver's descriptors)"
                    % ", ".join(failing))
            proofs = prove_pairs(good, log)
            if failing:
                log("== saves blocks: none printed while the dbgate of %s does not carry its gameserver's "
                    "descriptors" % ", ".join(failing))
            else:
                blocks = saves_blocks(good, proofs)
                log("== saves blocks (agent/payloads/<version>/%s)" % MANIFEST_NAME)
                for version in sorted(blocks):
                    log("%s  \"saves\": %s" % (version, json.dumps(blocks[version], sort_keys=True)))
                    state, block = manifest_block(args.out_dir, version)
                    if state == "present":
                        same = block == blocks[version]
                        stale += args.check and not same
                        log("    the manifest beside the schema: %s" % (
                            "the same block" if same else "ANOTHER BLOCK"))
                    elif state == "absent":
                        log("    the manifest beside the schema holds no saves block")
            sweep_ids(dict((v, item["tables"]) for v, item in good.items()),
                      dict((v, item["gacha"]) for v, item in good.items()),
                      [(s, t) for (s, t), r in sorted(proofs.items()) if s != t and r["ok"]], log)
            for text in unused_entries(good, proofs):
                log("note: no stack of this run has %s" % text)
        if built and (args.check or not args.saves):
            log("== schemas (%s)" % args.out_dir)
            for version in sorted(built):
                item = built[version]
                path = os.path.join(args.out_dir, version, SCHEMA_NAME)
                if args.check:
                    try:
                        with open(path, "rb") as f:
                            verdict = "ok" if f.read() == item["data"] else "DIFFERS"
                    except OSError:
                        verdict = "MISSING"
                    stale += verdict != "ok"
                    log("%-8s %s  (%d bytes expected)" % (verdict, path, len(item["data"])))
                elif failing:
                    log("NOT WRITTEN %s  (%s)" % (path, "its dbgate does not carry the gameserver's descriptors"
                                                  if version in failing else "nothing of a run is written while "
                                                  "a stack of it fails"))
                else:
                    try:
                        os.makedirs(os.path.dirname(path), exist_ok=True)
                        put(path, item["data"])
                    except OSError as e:
                        raise CompatError("%s: cannot be written (%s)" % (path, e))
                    log("-> %s  (%d bytes, sha256 %s)" % (
                        path, len(item["data"]), hashlib.sha256(item["data"]).hexdigest()))
            try:
                others = [v for v in schema_versions(args.out_dir) if v not in built]
            except CompatError:
                others = []
            for version in others:
                path = os.path.join(args.out_dir, version, SCHEMA_NAME)
                if args.check:
                    stale += 1
                    log("STALE    %s  (no --stack of this run names %s)" % (path, version))
                else:
                    log("note: %s belongs to no stack of this run" % path)
        if args.saves:
            schemas = dict((v, item["schema"]) for v, item in built.items()) or load_schemas(args.out_dir)
            if not schemas:
                raise CompatError("no schema: give --stack, or an --out-dir that holds <version>/%s" % SCHEMA_NAME)
            if not prefixes:
                for version in schemas:
                    state, block = manifest_block(args.out_dir, version)
                    if state == "present" and isinstance(block, dict):
                        prefixes[version] = list(block.get("clientPrefixes") or [])
            failed = 0
            for dump in args.saves:
                log("== %s" % dump)
                lines, fail = decode_dump(dump, schemas, prefixes)
                for line in lines:
                    log("   " + line)
                failed += fail
            bad += failed
    except CompatError as e:
        sys.stderr.write("save_compat: %s\n" % e)
        return 2
    return 1 if stale or bad else 0


def identify(folder, schemas, log):
    """Which schema's fingerprints the gameserver of a stack folder matches: 0 for exactly one, 1 for none or
    more; CompatError when the binary cannot be read."""
    binary = os.path.join(folder, *GAMESERVER.split("/"))
    if not schemas:
        raise CompatError("no schema to identify a build by")
    matches = []
    for version in sorted(schemas):
        problems = fingerprint_problems(schemas[version], binary)
        log("%s: %s" % (version, "MATCH" if not problems else "no -- " + "; ".join(problems[:3])))
        if not problems:
            matches.append(version)
    log("%s: %s" % (binary, "the server build of %s" % matches[0] if len(matches) == 1 else
                    "matched by %s" % ", ".join(matches) if matches else "a server build no schema knows"))
    return 0 if len(matches) == 1 else 1


# ---------------------------------------------------------------------------------------------------------
# Self-test: synthetic descriptors, binaries, stacks and dumps in a temporary folder

def _e_varint(v):
    v &= (1 << 64) - 1
    out = bytearray()
    while True:
        b = v & 0x7F
        v >>= 7
        if v:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def _e_int(n, v):
    return _e_varint(n << 3) + _e_varint(v)


def _e_len(n, payload):
    return _e_varint(n << 3 | 2) + _e_varint(len(payload)) + payload


def _e_str(n, text):
    return _e_len(n, text.encode("utf-8"))


def _e_packed(n, values):
    return _e_len(n, b"".join(_e_varint(v) for v in values))


_TYPE_NUMBERS = dict((v, k) for k, v in TYPES.items())
_LABEL_NUMBERS = dict((v, k) for k, v in LABELS.items())


def _syn_field(name, n, ftype, label="optional", type_name=None, packed=None, oneof=None, default=None,
               optional=None):
    return {"name": name, "n": n, "type": ftype, "label": label, "typeName": type_name, "packed": packed,
            "oneof": oneof, "default": default, "optional": optional}


def _syn_spec():
    """The synthetic save closure the cases start from: messages by full name ({"fields", "map", "oneofs"}),
    enums by full name, and the top-level types of each descriptor file. Its names are ones the tables of
    this tool classify, and DungeonRecordBin.group_map sits at the DROPPABLE path."""
    F = _syn_field
    messages = {
        ".proto.PlayerDataBin": {"fields": [
            F("basic_bin", 1, "message", type_name=".proto.PlayerBasicCompBin"),
            F("avatar_bin", 2, "message", type_name=".proto.PlayerAvatarCompBin"),
            F("dungeon_bin", 6, "message", type_name=".proto.PlayerDungeonCompBin"),
            F("mail_bin", 11, "message", type_name=".proto.PlayerMailCompBin"),
            F("social_bin", 28, "message", type_name=".proto.PlayerSocialCompBin"),
            F("custom_dungeon_bin", 58, "message", type_name=".proto.PlayerCustomDungeonCompBin")]},
        ".proto.PlayerSocialCompBin": {"fields": [
            F("friend_remark_name_map", 14, "message", "repeated",
              ".proto.PlayerSocialCompBin.FriendRemarkNameMapEntry")]},
        ".proto.PlayerSocialCompBin.FriendRemarkNameMapEntry": {"map": True, "fields": [
            F("key", 1, "uint32"), F("value", 2, "string")]},
        ".proto.PlayerCustomDungeonCompBin": {"fields": [F("store_dungeon_list", 3, "uint64", "repeated")]},
        ".proto.PlayerBasicCompBin": {"fields": [
            F("level", 1, "uint32"), F("player_time_ms", 12, "uint64"), F("client_app_version", 19, "string")]},
        ".proto.PlayerAvatarCompBin": {"fields": [
            F("avatar_list", 1, "message", "repeated", ".proto.AvatarBin"), F("cur_avatar_guid", 2, "uint64"),
            F("temp_avatar_guid_list", 17, "uint64", "repeated")]},
        ".proto.AvatarBin": {"fields": [
            F("avatar_id", 1, "uint32"), F("guid", 3, "uint64"),
            F("life_state", 5, "enum", type_name=".proto.LifeState")]},
        ".proto.PlayerDungeonCompBin": {"fields": [
            F("record_bin", 5, "message", type_name=".proto.PlayerDungeonRecordBin")]},
        ".proto.PlayerDungeonRecordBin": {"fields": [
            F("dungeon_daily_map", 1, "message", "repeated", ".proto.PlayerDungeonRecordBin.DungeonDailyMapEntry")]},
        ".proto.PlayerDungeonRecordBin.DungeonDailyMapEntry": {"map": True, "fields": [
            F("key", 1, "uint32"), F("value", 2, "message", type_name=".proto.DungeonRecordBin")]},
        ".proto.DungeonRecordBin": {"fields": [
            F("enter_count", 1, "uint32"),
            F("group_map", 2, "message", "repeated", ".proto.DungeonRecordBin.GroupMapEntry")]},
        ".proto.DungeonRecordBin.GroupMapEntry": {"map": True, "fields": [
            F("key", 1, "uint32"), F("value", 2, "uint32")]},
        ".proto.PlayerMailCompBin": {"fields": [F("has_take_attachment_mail_id_list", 1, "uint32", "repeated")]},
        ".proto.PlayerExtraBinData": {"oneofs": ["detail"], "fields": [
            F("extra_count", 1, "uint32"), F("note_id", 2, "uint32", oneof=0), F("note_text", 3, "string", oneof=0)]},
        ".proto.HomeDataBin": {"fields": [F("basic_bin", 1, "message", type_name=".proto.HomeBasicCompBin")]},
        ".proto.HomeBasicCompBin": {"fields": [F("exp", 4, "uint64")]},
        ".proto.BlockBin": {"fields": [F("group_map", 1, "message", "repeated", ".proto.BlockBin.GroupMapEntry")]},
        ".proto.BlockBin.GroupMapEntry": {"map": True, "fields": [F("key", 1, "uint32"), F("value", 2, "bytes")]},
        ".proto.GroupBin": {"fields": [
            F("target_suite", 1, "uint32"),
            F("boss_die_info_map", 13, "message", "repeated", ".proto.GroupBin.BossDieInfoMapEntry")]},
        ".proto.GroupBin.BossDieInfoMapEntry": {"map": True, "fields": [
            F("key", 1, "uint32"), F("value", 2, "message", type_name=".proto.BossDieInfoBin")]},
        ".proto.BossDieInfoBin": {"fields": [F("surround_uid_list", 1, "uint32", "repeated")]},
    }
    files = {
        "define.proto": [".proto.LifeState"],
        "server_only/bin.server.proto": [
            ".proto.PlayerDataBin", ".proto.PlayerBasicCompBin", ".proto.PlayerAvatarCompBin", ".proto.AvatarBin",
            ".proto.PlayerDungeonCompBin", ".proto.PlayerDungeonRecordBin", ".proto.DungeonRecordBin",
            ".proto.PlayerMailCompBin", ".proto.PlayerSocialCompBin", ".proto.PlayerCustomDungeonCompBin",
            ".proto.PlayerExtraBinData", ".proto.HomeDataBin", ".proto.HomeBasicCompBin"],
        "server_only/bin.block.proto": [".proto.BlockBin", ".proto.GroupBin", ".proto.BossDieInfoBin"],
    }
    enums = {".proto.LifeState": [["LIFE_NONE", 0], ["LIFE_ALIVE", 1], ["LIFE_DEAD", 2]]}
    return {"messages": messages, "enums": enums, "files": files}


def _syn_descriptors(spec):
    """{file name: serialized FileDescriptorProto} of a synthetic spec, fields in the order protoc writes them."""
    messages, enums = spec["messages"], spec["enums"]

    def field(f):
        out = _e_str(1, f["name"]) + _e_int(3, f["n"]) + _e_int(4, _LABEL_NUMBERS[f["label"]])
        out += _e_int(5, _TYPE_NUMBERS[f["type"]])
        if f.get("typeName"):
            out += _e_str(6, f["typeName"])
        if f.get("default") is not None:
            out += _e_str(7, f["default"])
        if f.get("packed") is not None:
            out += _e_len(8, _e_int(2, 1 if f["packed"] else 0))
        if f.get("oneof") is not None:
            out += _e_int(9, f["oneof"])
        if f.get("optional"):
            out += _e_int(17, 1)
        return out

    def message(fq):
        m = messages[fq]
        out = _e_str(1, fq.rsplit(".", 1)[1])
        for f in m["fields"]:
            out += _e_len(2, field(f))
        for child in sorted(x for x in messages if x.rsplit(".", 1)[0] == fq):
            out += _e_len(3, message(child))
        for child in sorted(x for x in enums if x.rsplit(".", 1)[0] == fq):
            out += _e_len(4, enum(child))
        if m.get("map"):
            out += _e_len(7, _e_int(7, 1))
        for o in m.get("oneofs", []):
            out += _e_len(8, _e_str(1, o))
        return out

    def enum(fq):
        return _e_str(1, fq.rsplit(".", 1)[1]) + b"".join(
            _e_len(2, _e_str(1, name) + _e_int(2, number)) for name, number in enums[fq])

    out = {}
    for fname, tops in sorted(spec["files"].items()):
        body = _e_str(1, fname) + _e_str(2, "proto")
        body += b"".join(_e_len(4, message(fq)) for fq in tops if fq in messages)
        body += b"".join(_e_len(5, enum(fq)) for fq in tops if fq in enums)
        # protoc names the syntax of a proto3 file and leaves it out for proto2
        out[fname] = body + (_e_str(12, "proto3") if spec.get("syntax", {}).get(fname, "proto3") == "proto3"
                             else b"")
    return out


_FILLER = bytes(range(256)) * 6


def _syn_binary(parts, gap=0):
    """An ELF stand-in: filler, then each (name, descriptor) with its NUL and more filler -> (bytes, {name:
    offset}). The filler holds no ".proto"."""
    out = bytearray(b"\x7fELF" + _FILLER)
    offsets = {}
    for name, data in parts:
        offsets[name] = len(out)
        out += data + b"\x00" + _FILLER[:700 + 131 * len(offsets) + gap]
    return bytes(out), offsets


def _syn_schema(version, spec):
    files = {}
    for name, data in _syn_descriptors(spec).items():
        files[name] = decode_descriptor(data + b"\x00", 0, len(data) + 1, True)
    return build_schema(version, files)[0]


def _syn_edit(spec, fq, fn):
    import copy
    out = copy.deepcopy(spec)
    fn(out["messages"][fq])
    return out


def _syn_field_edit(spec, fq, field_name, **changes):
    def fn(m):
        for f in m["fields"]:
            if f["name"] == field_name:
                f.update(changes)
    return _syn_edit(spec, fq, fn)


def _syn_drop_field(spec, fq, name):
    return _syn_edit(spec, fq, lambda m: m.__setitem__("fields", [f for f in m["fields"] if f["name"] != name]))


def _syn_add_field(spec, fq, field):
    return _syn_edit(spec, fq, lambda m: m["fields"].append(field))


_TABLE_HEADER = {"AvatarData": "ID\t名字\t技能库ID\t候选技能库ID"}


def _syn_tables(folder, ids, depots):
    """The txt tables of the id proof: `ids` = {table: [ids]}; `depots` = {avatar: (depot, [candidates])}."""
    os.makedirs(folder, exist_ok=True)
    for table in set(t for _c, (a, _r), (b, _r2) in ID_CATEGORIES for t in (a, b)):
        with open(os.path.join(folder, table + ".txt"), "wb") as f:
            if table == "AvatarData":
                f.write((_TABLE_HEADER[table] + "\r\n").encode("utf-8"))
                for avatar in ids[table]:
                    depot, cands = depots.get(avatar, ("", []))
                    f.write(("%d\tname\t%s\t%s\r\n" % (avatar, depot, ",".join(str(c) for c in cands))).encode("utf-8"))
            else:
                f.write("ID\t名字\n".encode("utf-8"))
                for i in ids[table]:
                    f.write(("%d\tx\n" % i).encode("utf-8"))


def _syn_stack(root, spec, client, ids, depots, db_parts=None, gs_parts=None, gacha=(22,)):
    """A synthetic stack folder: gameserver and dbgate stand-ins (dbgate holds the same descriptors at other
    offsets unless `db_parts` says otherwise), data.sql.tmpl binding `client` with the gacha schedules
    `gacha`, and the tables."""
    descs = sorted(_syn_descriptors(spec).items())
    gs, offsets = _syn_binary(gs_parts if gs_parts is not None else descs)
    db, _ = _syn_binary(db_parts if db_parts is not None else list(reversed(descs)), gap=97)
    for rel, data in ((GAMESERVER, gs), (DBGATE, db)):
        path = os.path.join(root, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data)
    with open(os.path.join(root, DATA_SQL), "wb") as f:
        f.write(b"-- synthetic\nINSERT INTO `t_region_config` (`id`, `key`, `note`) VALUES\n"
                b"(1,\tUNHEX('00ff'),\t'a ''quoted'' (note), with commas'),\n(2, CONCAT('x', 'y'), NULL);\n"
                b"INSERT INTO `t_bind_config` (`id`, `client_version`, `region_name`, `channel_id`) VALUES\n"
                b"(1,\t'OS" + client.encode() + b"',\t'dev_docker',\t'1'),\n(2, 'CN" + client.encode()
                + b"', 'dev_docker', '1');\n"
                b"INSERT INTO `t_gacha_schedule_config` (`schedule_id`, `gacha_type`, `title_textmap`) VALUES\n"
                + b",\n".join(b"(%d, 301, '{\"x\": 1}')" % g for g in gacha) + b";\n")
    _syn_tables(os.path.join(root, DATA_DIR, "txt"), ids, depots)
    return gs, offsets


_SYN_IDS_1 = {"AvatarData": [1001, 1002], "AvatarSkillDepotData": [101, 201, 202, 299], "MaterialData": [1, 2],
              "WeaponData": [11], "ReliquaryData": [21], "FurnitureExcelData": [31], "ReliquaryMainData": [41],
              "ReliquaryAffixData": [51], "SceneData": [3, 4]}
_SYN_DEPOTS_1 = {1001: (101, []), 1002: (201, [201, 202])}
_SYN_IDS_2 = dict(_SYN_IDS_1, AvatarData=[1001, 1002, 1003], AvatarSkillDepotData=[101, 201, 202, 301],
                  MaterialData=[1, 2, 3], SceneData=[3, 4, 5])
_SYN_DEPOTS_2 = dict(_SYN_DEPOTS_1)
_SYN_DEPOTS_2[1003] = (301, [])


def _syn_spec_2():
    """The newer synthetic version: a field more, DungeonRecordBin.group_map gone, PlayerBeforeLoginBinData."""
    spec = _syn_add_field(_syn_spec(), ".proto.PlayerBasicCompBin", _syn_field("new_flag", 20, "bool"))
    spec = _syn_drop_field(spec, ".proto.DungeonRecordBin", "group_map")
    spec["messages"][".proto.PlayerBeforeLoginBinData"] = {"fields": [
        _syn_field("finish_quest_res_collection", 1, "uint32", "repeated")]}
    spec["files"]["server_only/bin.server.proto"].append(".proto.PlayerBeforeLoginBinData")
    return spec


def _syn_save(client, level_field=None, wrong_wire=False, high=400, truncate=0):
    """A PlayerDataBin of the synthetic closure (wrapped in ZLIB) with a guid, a time whose high word is `high`,
    a dungeon record with a group map entry; `level_field` adds a field number the older closure does not
    know; `wrong_wire` writes AvatarBin.guid length-delimited; `truncate` cuts that many bytes off the end of
    the message."""
    guid = (1 << 32) | 5
    avatar = _e_int(1, 1001) + (_e_len(3, b"\x01") if wrong_wire else _e_int(3, guid)) + _e_int(5, 1)
    basic = _e_int(1, 5) + _e_int(12, (high << 32) | 77) + _e_str(19, client)
    if level_field:
        basic += _e_int(level_field, 1)
    record = _e_int(1, 1) + _e_len(2, _e_int(1, 9) + _e_int(2, 1))
    dungeon = _e_len(5, _e_len(1, _e_int(1, 7) + _e_len(2, record)))
    payload = (_e_len(1, basic) + _e_len(2, _e_len(1, avatar) + _e_int(2, guid) + _e_packed(17, [guid]))
               + _e_len(6, dungeon) + _e_len(11, _e_packed(1, [3, 4])))
    return b"ZLIB" + zlib.compress(payload[:len(payload) - truncate])


def _syn_dump(path, player, block=True, home=True, before_login=None):
    group = _e_int(1, 2) + _e_len(13, _e_int(1, 1) + _e_len(2, _e_packed(1, [1, 2])))
    block_bin = _e_len(1, _e_int(1, 133) + _e_len(2, group))
    text = (b"CREATE TABLE `t_block_data_1` (\n  `uid` int(10) NOT NULL,\n  `block_id` int(10) NOT NULL,\n"
            b"  `bin_data` mediumblob NOT NULL,\n  `last_save_time` timestamp NOT NULL\n) ENGINE=InnoDB;\n"
            b"INSERT INTO `t_player_data_1` (`uid`, `nickname`, `bin_data`, `extra_bin_data`, `before_login_bin_data`)"
            b" VALUES\n(1, 'it''s \\'me\\' \\\\ \\n', 0x" + player.hex().encode() + b", 0x"
            + _e_int(1, 3).hex().encode() + b", "
            + (b"0x" + before_login.hex().encode() if before_login else b"NULL") + b");\n")
    if block:
        text += (b"INSERT INTO `t_block_data_1` VALUES\n(1,1004,0x" + block_bin.hex().encode()
                 + b",'2024-01-01 00:00:00');\n")
    if home:
        text += (b"INSERT INTO `t_home_data_1` (`uid`, `bin_data`) VALUES (1, _binary '"
                 + _e_len(1, _e_int(4, 10)).replace(b"\\", b"\\\\").replace(b"'", b"\\'") + b"');\n")
    with open(path, "wb") as f:
        f.write(text)


def selftest(log):
    """The cases below, each a line; exit 1 when one fails. Everything is made and removed in a temporary
    folder."""
    import io
    import contextlib
    results = []

    def check(name, ok, detail=""):
        results.append(bool(ok))
        log("%s %s%s" % ("ok  " if ok else "FAIL", name, "" if ok else " -- " + str(detail)[:300]))

    def quiet_main(argv):
        buf, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stderr(err):
            code = main(argv, out=buf)
        return code, buf.getvalue(), err.getvalue()

    def raises(fn, text):
        try:
            fn()
        except CompatError as e:
            return text in str(e), str(e)
        return False, "no error"

    tmp = tempfile.mkdtemp(prefix="save_compat_selftest_")
    try:
        _selftest_decoder(tmp, check, raises)
        _selftest_schema(check, raises)
        _selftest_pairs(check)
        _selftest_runs(tmp, check, quiet_main)
    except Exception as e:  # noqa: BLE001 -- a crash is a failed self-test, with its reason
        import traceback
        check("the self-test runs to its end", False, "".join(traceback.format_exception_only(type(e), e)).strip()
              + " @ " + traceback.format_exc().strip().splitlines()[-3].strip())
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    failed = results.count(False)
    log("selftest: %d checks, %d failed" % (len(results), failed))
    return 1 if failed else 0


def _selftest_decoder(tmp, check, raises):
    spec = _syn_spec()
    descs = sorted(_syn_descriptors(spec).items())
    gs, offsets = _syn_binary(descs)
    path = os.path.join(tmp, "gs.bin")
    with open(path, "wb") as f:
        f.write(gs)
    want = dict((n, (offsets[n], len(d), hashlib.sha256(d).hexdigest())) for n, d in descs)
    for window, lookahead in ((WINDOW, LOOKAHEAD), (4096, 16), (1500, 6)):
        files, stats = scan_descriptors(path, window=window, lookahead=lookahead)
        got = dict((n, (f["offset"], f["size"], f["sha256"])) for n, f in files.items())
        check("every descriptor found with its offset, size and sha256 (window %d, %d read again on their own)" % (
            window, stats["rereads"]), got == want and (window > 1500 or stats["rereads"] > 0),
              "%s != %s" % (got, want))
    files, _stats = scan_descriptors(path)
    server = files["server_only/bin.server.proto"]
    extra = [m for m in server["messages"] if m["name"] == "PlayerExtraBinData"][0]
    avatar_comp = [m for m in server["messages"] if m["name"] == "PlayerAvatarCompBin"][0]
    record = [m for m in server["messages"] if m["name"] == "PlayerDungeonRecordBin"][0]
    check("a repeated scalar of proto3 is packed, a oneof is named, a map entry is marked",
          [f.get("packed") for f in avatar_comp["fields"]] == [None, None, True]
          and [f.get("oneof") for f in extra["fields"]] == [None, "detail", "detail"]
          and record["nested"][0]["mapEntry"], (avatar_comp["fields"], extra["fields"]))

    def variant(name, data, tail=b"\x00"):
        p = os.path.join(tmp, name)
        with open(p, "wb") as f:
            f.write(_FILLER + data + tail + _FILLER)
        return scan_descriptors(p)[0]
    body = dict(descs)["define.proto"]
    check("a descriptor not followed by its NUL is not taken", "define.proto" not in variant("nonul", body, b"\x01"))
    check("a descriptor with a field descriptor.proto does not define is not taken",
          "define.proto" not in variant("unknown", body[:-len(_e_str(12, "proto3"))] + _e_int(13, 1)
                                        + _e_str(12, "proto3")))
    check("a descriptor whose fields are out of order is not taken",
          "define.proto" not in variant("order", _e_str(1, "define.proto") + _e_str(12, "proto3") + _e_str(2, "proto")))
    twice = os.path.join(tmp, "twice.bin")
    with open(twice, "wb") as f:
        f.write(_FILLER + body + b"\x00" + _FILLER + body + b"\x00" + _FILLER)
    files2 = scan_descriptors(twice)[0]
    check("one descriptor twice: the lower offset, two copies",
          files2["define.proto"]["offset"] == len(_FILLER) and files2["define.proto"]["copies"] == 2,
          files2.get("define.proto", {}).get("offset"))
    other = _syn_descriptors(dict(spec, enums={".proto.LifeState": [["LIFE_NONE", 0]]}))["define.proto"]
    clash = os.path.join(tmp, "clash.bin")
    with open(clash, "wb") as f:
        f.write(_FILLER + body + b"\x00" + _FILLER + other + b"\x00" + _FILLER)
    ok, detail = raises(lambda: scan_descriptors(clash), "two different descriptors named define.proto")
    check("two different descriptors of one name stop the run", ok, detail)
    dup = dict(files)
    dup["copy.proto"] = dict(files["define.proto"], name="copy.proto")
    ok, detail = raises(lambda: Registry(dup), "is defined in")
    check("a type two files define stops the run", ok, detail)
    # the tag and the length of a name in the window before the one its ".proto" ends in (LOOKBACK)
    straddle = os.path.join(tmp, "straddle.bin")
    with open(straddle, "wb") as f:
        f.write(b"\x7fELF" + (_FILLER * 3)[:4096 - 5 - 4] + body + b"\x00" + _FILLER)
    files3, stats3 = scan_descriptors(straddle, window=4096, lookahead=16)
    check("a descriptor whose tag ends one window and whose name ends the next is found",
          files3.get("define.proto", {}).get("offset") == 4096 - 5 and not stats3["rejected"],
          (files3.get("define.proto", {}).get("offset"), stats3))
    proto2 = dict(spec, syntax={"server_only/bin.block.proto": "proto2"})
    two = dict((n, decode_descriptor(d + b"\x00", 0, len(d) + 1, True)) for n, d in _syn_descriptors(proto2).items())
    boss = [m for m in two["server_only/bin.block.proto"]["messages"] if m["name"] == "BossDieInfoBin"][0]
    check("a proto2 file names no syntax, and its repeated scalar is not packed",
          two["server_only/bin.block.proto"]["syntax"] == "proto2" and "packed" not in boss["fields"][0],
          boss["fields"])


def _selftest_schema(check, raises):
    spec = _syn_spec()
    schema = _syn_schema("1.0", spec)
    classes = dict((e["path"], e["class"]) for e in schema["int64"])
    check("the 64-bit paths carry their classes",
          classes == {"PlayerDataBin.1.12": "time", "PlayerDataBin.2.1.3": "guid", "PlayerDataBin.2.2": "guid",
                      "PlayerDataBin.2.17": "guid", "PlayerDataBin.58.3": "ugc", "HomeDataBin.1.4": "count"}, classes)
    check("the guid paths by encoding, a ugc list not among them",
          schema["guids"] == {"packed": ["PlayerDataBin.2.17"], "repeated": [],
                              "scalar": ["PlayerDataBin.2.1.3", "PlayerDataBin.2.2"]}, schema["guids"])
    bound = sorted((e["path"], e["class"]) for e in schema["uidBound"])
    check("the uid-bound paths: a uid list, a cursor, a map keyed by uid (UID_EXTRA), a ugc list's low words",
          bound == [("GroupBin.13.2.1", "uid"), ("PlayerDataBin.11.1", "cursor"), ("PlayerDataBin.28.14.1", "uid"),
                    ("PlayerDataBin.58.3", "uidLow")], bound)
    check("the classes the schema uses are explained", sorted(schema["classes"]) == [
        "count", "cursor", "guid", "time", "ugc", "uid", "uidLow"], sorted(schema["classes"]))
    unpacked = _syn_schema("1.0", _syn_field_edit(spec, ".proto.PlayerAvatarCompBin", "temp_avatar_guid_list",
                                                  packed=False))
    check("a guid list that is not packed is a repeated one", unpacked["guids"] == {
        "packed": [], "repeated": ["PlayerDataBin.2.17"], "scalar": ["PlayerDataBin.2.1.3", "PlayerDataBin.2.2"]},
        unpacked["guids"])
    uid_map = _syn_add_field(spec, ".proto.PlayerBasicCompBin", _syn_field(
        "helper_uid_map", 32, "message", "repeated", ".proto.PlayerBasicCompBin.HelperUidMapEntry"))
    uid_map["messages"][".proto.PlayerBasicCompBin.HelperUidMapEntry"] = {"map": True, "fields": [
        _syn_field("key", 1, "uint32"), _syn_field("value", 2, "uint32")]}
    ok, detail = raises(lambda: _syn_schema("1.0", uid_map), ".proto.PlayerBasicCompBin.HelperUidMapEntry.key")
    check("a map named like a uid whose key the tables do not classify stops the run", ok, detail)
    UID_FIELDS[(".proto.PlayerBasicCompBin.HelperUidMapEntry", "key")] = True
    try:
        mapped = _syn_schema("1.0", uid_map)
    finally:
        del UID_FIELDS[(".proto.PlayerBasicCompBin.HelperUidMapEntry", "key")]
    check("the key of such a map, classified as a uid, is a uid path",
          ("PlayerDataBin.1.32.1", "uid") in [(e["path"], e["class"]) for e in mapped["uidBound"]],
          mapped["uidBound"])
    check("GroupBin is nested in BlockBin.1.2",
          [(n["path"], n["root"]) for n in schema["nested"]] == [("BlockBin.1.2", "GroupBin")], schema["nested"])
    check("the roots present, PlayerBeforeLoginBinData left out",
          [r["root"] for r in schema["roots"]] == ["PlayerDataBin", "PlayerExtraBinData", "BlockBin", "GroupBin",
                                                    "HomeDataBin"], schema["roots"])
    check("a fingerprint per file of the closure, with its syntax",
          [(f["file"], f["syntax"]) for f in schema["fingerprints"]] == [(n, "proto3") for n in sorted(spec["files"])],
          schema["fingerprints"])
    data = encode(schema)
    check("encoding is stable and reads back", data == encode(_syn_schema("1.0", spec))
          and json.loads(data.decode("utf-8")) == schema and data.endswith(b"}\n") and b"\r" not in data)

    def reverse(value):
        if isinstance(value, dict):
            return dict((k, reverse(value[k])) for k in reversed(list(value)))
        if isinstance(value, list):
            return [reverse(v) for v in value]
        return value
    check("the bytes do not depend on the order the schema's objects were built in", encode(reverse(schema)) == data)
    unsorted = []

    def hook(pairs):
        keys = [k for k, _v in pairs]
        if keys != sorted(keys):
            unsorted.append(keys)
        return dict(pairs)
    json.loads(data.decode("utf-8"), object_pairs_hook=hook)
    check("every object of the file has its keys sorted", not unsorted, unsorted[:2])
    check("a schema built here is one schema_problem takes", schema_problem(json.loads(data.decode("utf-8"))) is None,
          schema_problem(json.loads(data.decode("utf-8"))))
    for name, edit, text in (
            ("without its nested roots", lambda s: s.pop("nested"), "nested is missing"),
            ("with a fingerprint longer than SPAN_MAX", lambda s: s["fingerprints"][0].update(size=SPAN_MAX + 1),
             "a fingerprint that is not one"),
            ("with a fingerprint whose sha256 is not one", lambda s: s["fingerprints"][0].update(sha256="x" * 64),
             "a fingerprint that is not one"),
            ("with a field of no type", lambda s: s["messages"][".proto.AvatarBin"]["fields"][0].pop("type"),
             "a field that is not one")):
        bad = json.loads(data.decode("utf-8"))
        edit(bad)
        check("a schema %s is refused" % name, text in (schema_problem(bad) or ""), schema_problem(bad))
    ok, detail = raises(lambda: _syn_schema("1.0", _syn_add_field(spec, ".proto.AvatarBin", _syn_field(
        "mystery_value", 9, "uint64"))), ".proto.AvatarBin.mystery_value")
    check("a 64-bit field the tables do not classify stops the run", ok, detail)
    ok, detail = raises(lambda: _syn_schema("1.0", _syn_add_field(spec, ".proto.PlayerBasicCompBin", _syn_field(
        "helper_uid", 30, "uint32"))), ".proto.PlayerBasicCompBin.helper_uid")
    check("a uid-named field the tables do not classify stops the run", ok, detail)
    ok, detail = raises(lambda: _syn_schema("1.0", _syn_add_field(spec, ".proto.PlayerBasicCompBin", _syn_field(
        "blob", 31, "bytes"))), "not named in NESTED")
    check("a bytes field NESTED does not name stops the run", ok, detail)
    no_home = _syn_spec()
    del no_home["messages"][".proto.HomeDataBin"]
    no_home["files"]["server_only/bin.server.proto"].remove(".proto.HomeDataBin")
    ok, detail = raises(lambda: _syn_schema("1.0", no_home), ".proto.HomeDataBin is not among")
    check("a required root missing stops the run", ok, detail)
    loop = _syn_add_field(spec, ".proto.AvatarBin", _syn_field("inner", 40, "message", type_name=".proto.AvatarBin"))
    ok, detail = raises(lambda: _syn_schema("1.0", loop), "contains itself")
    check("a type that contains itself stops the run", ok, detail)


def _selftest_pairs(check):
    base = _syn_schema("1.0", _syn_spec())
    spec = _syn_spec()

    def proof(new_spec, old=None):
        return prove_format(old or base, _syn_schema("2.0", new_spec))

    def kinds(r):
        return sorted(set(k for k, _p, _d in r["failures"]))
    r = prove_format(base, base)
    check("a schema against itself passes", r["ok"] and not r["drops"], r)
    added = _syn_add_field(spec, ".proto.PlayerBasicCompBin", _syn_field("new_flag", 20, "bool"))
    r = proof(added)
    check("a field added passes", r["ok"] and r["added"] == 1, r)
    r = prove_format(_syn_schema("2.0", added), base)
    check("the other way (a downgrade) fails: the field is removed", not r["ok"] and kinds(r) == ["removed"]
          and r["failures"][0][1] == "PlayerDataBin.1.20", r["failures"])
    r = proof(_syn_drop_field(spec, ".proto.PlayerBasicCompBin", "level"))
    check("a field removed outside DROPPABLE fails", not r["ok"] and kinds(r) == ["removed"], r["failures"])
    r = proof(_syn_drop_field(spec, ".proto.DungeonRecordBin", "group_map"))
    check("a field removed at its DROPPABLE path passes and is dropped",
          r["ok"] and r["drops"] == ["PlayerDataBin.6.5.1.2.2"], r)
    other = _syn_field_edit(spec, ".proto.DungeonRecordBin", "group_map", name="group_list", type="uint32",
                            typeName=None)
    r = prove_format(_syn_schema("1.0", other), _syn_schema("2.0", _syn_drop_field(other, ".proto.DungeonRecordBin",
                                                                                     "group_list")))
    check("another field at a DROPPABLE path is not dropped", not r["ok"] and kinds(r) == ["removed"], r["failures"])
    for name, fq, field, changes, kind in (
            ("retyped (uint32 -> int32)", ".proto.AvatarBin", "avatar_id", dict(type="int32"), "retyped"),
            ("retyped (uint32 -> sint32)", ".proto.AvatarBin", "avatar_id", dict(type="sint32"), "retyped"),
            ("retyped (uint64 -> fixed64)", ".proto.PlayerBasicCompBin", "player_time_ms", dict(type="fixed64"),
             "retyped"),
            ("label changed (optional -> repeated)", ".proto.AvatarBin", "avatar_id", dict(label="repeated"),
             "label-changed")):
        r = proof(_syn_field_edit(spec, fq, field, **changes))
        check("a field %s fails" % name, not r["ok"] and kind in kinds(r), r["failures"])
    r = proof(_syn_field_edit(spec, ".proto.PlayerBasicCompBin", "level", n=7))
    check("a field renumbered fails", not r["ok"] and kinds(r) == ["renumbered"], r["failures"])
    r = proof(_syn_field_edit(spec, ".proto.PlayerBasicCompBin", "level", name="player_level"))
    check("a field renamed fails", not r["ok"] and kinds(r) == ["renamed"], r["failures"])
    r = proof(_syn_field_edit(spec, ".proto.PlayerAvatarCompBin", "temp_avatar_guid_list", packed=False))
    check("a list whose packing changes fails", not r["ok"] and kinds(r) == ["packing-changed"], r["failures"])
    r = proof(_syn_field_edit(spec, ".proto.PlayerExtraBinData", "note_id", oneof=None))
    check("a field moved out of its oneof fails", not r["ok"] and kinds(r) == ["oneof-changed"], r["failures"])
    r = proof(_syn_edit(spec, ".proto.DungeonRecordBin.GroupMapEntry", lambda m: m.__setitem__("map", False)))
    check("a map that becomes a list fails", not r["ok"] and kinds(r) == ["map-entry-changed"], r["failures"])
    r = proof(_syn_drop_field(spec, ".proto.BossDieInfoBin", "surround_uid_list"))
    check("a field removed inside the nested GroupBin fails",
          not r["ok"] and [(k, p) for k, p, _d in r["failures"]] == [("removed", "GroupBin.13.2.1")], r["failures"])
    r = proof(_syn_field_edit(spec, ".proto.GroupBin", "target_suite", type="int32"))
    check("a field retyped inside the nested GroupBin fails",
          not r["ok"] and [(k, p) for k, p, _d in r["failures"]] == [("retyped", "GroupBin.1")], r["failures"])
    r = proof(_syn_field_edit(spec, ".proto.PlayerBasicCompBin", "level", optional=True))
    check("a field that gains explicit presence (proto3 optional) fails",
          not r["ok"] and kinds(r) == ["presence-changed"], r["failures"])
    r = proof(_syn_field_edit(spec, ".proto.PlayerBasicCompBin", "level", default="5"))
    check("a field that gains a default fails", not r["ok"] and kinds(r) == ["default-changed"], r["failures"])
    r = proof(dict(spec, syntax={"server_only/bin.block.proto": "proto2"}))
    check("a file of the closure whose syntax changes fails",
          not r["ok"] and "syntax-changed" in kinds(r)
          and ("syntax-changed", "BlockBin") in [(k, p) for k, p, _d in r["failures"]], r["failures"])
    r = proof(dict(spec, syntax={"define.proto": "proto2"}))
    check("an enum whose file's syntax changes fails",
          not r["ok"] and kinds(r) == ["syntax-changed"] and r["failures"][0][1] == "PlayerDataBin.2.1.5",
          r["failures"])
    import copy
    edited = copy.deepcopy(base)
    [e for e in edited["uidBound"] if e["path"] == "PlayerDataBin.11.1"][0]["class"] = "pending"
    r = prove_format(base, edited)
    check("a path whose uid-bound class changes fails",
          not r["ok"] and [(k, p) for k, p, _d in r["failures"]] == [("uid-class-changed", "PlayerDataBin.11.1")],
          r["failures"])
    edited = copy.deepcopy(base)
    edited["nested"][0]["root"] = "HomeDataBin"
    r = prove_format(base, edited)
    check("a bytes field that holds another root on the target fails", not r["ok"] and kinds(r) == ["nested-changed"],
          r["failures"])
    for name, values, ok, kind in (
            ("an enum value removed", [["LIFE_NONE", 0], ["LIFE_ALIVE", 1]], False, "enum-value-removed"),
            ("an enum value renamed", [["LIFE_NONE", 0], ["LIFE_ALIVE", 1], ["LIFE_GONE", 2]], False,
             "enum-value-renamed"),
            ("an enum value added", [["LIFE_NONE", 0], ["LIFE_ALIVE", 1], ["LIFE_DEAD", 2], ["LIFE_X", 3]], True,
             None)):
        s = copy.deepcopy(spec)
        s["enums"][".proto.LifeState"] = values
        r = proof(s)
        check("%s %s" % (name, "passes" if ok else "fails"), r["ok"] == ok and (ok or kinds(r) == [kind]),
              r["failures"])
    s = copy.deepcopy(spec)
    s["messages"][".proto.HomeBasicBin"] = s["messages"].pop(".proto.HomeBasicCompBin")
    tops = s["files"]["server_only/bin.server.proto"]
    tops[tops.index(".proto.HomeBasicCompBin")] = ".proto.HomeBasicBin"
    s["messages"][".proto.HomeDataBin"]["fields"][0]["typeName"] = ".proto.HomeBasicBin"
    # the 64-bit field of the renamed type, classified under its new name for this case: as the old type's
    # field is, then otherwise
    for cls, ok in (("count", True), ("time", False)):
        CLASS64[(".proto.HomeBasicBin", "exp")] = cls
        try:
            r = proof(s)
        finally:
            del CLASS64[(".proto.HomeBasicBin", "exp")]
        if ok:
            check("a renamed type with the same fields passes", r["ok"] and r["renamedTypes"] == 1, r)
        else:
            check("a path whose CLASS64 class changes fails (a renamed type classified otherwise)",
                  not r["ok"] and [(k, p) for k, p, _d in r["failures"]] == [("class-changed", "HomeDataBin.1.4")],
                  r["failures"])
    new = _syn_schema("2.0", _syn_spec_2())
    r = prove_format(new, base)
    check("a root the target lacks fails", not r["ok"] and "root-missing" in kinds(r), r["failures"])
    ids_old = {"avatar": {"source": {1, 2}, "target": {1, 2}}}
    ids_new = {"avatar": {"source": {1, 2, 3}, "target": {1, 2, 3}}}
    saved = ID_CATEGORIES
    try:
        globals()["ID_CATEGORIES"] = (("avatar", ("AvatarData", "rows"), ("AvatarData", "rows")),)
        check("ids: a superset passes, a missing id fails",
              prove_ids(ids_old, ids_new) == {} and prove_ids(ids_new, ids_old) == {"avatar": [3]})
    finally:
        globals()["ID_CATEGORIES"] = saved
    third = _syn_add_field(_syn_spec_2(), ".proto.AvatarBin", _syn_field("level", 9, "uint32"))
    built = {"1.0": {"schema": base, "prefixes": ["OSRELWin1.0.0_"]},
             "2.0": {"schema": new, "prefixes": ["OSRELWin2.0.0_"]},
             "3.0": {"schema": _syn_schema("3.0", third), "prefixes": ["OSRELWin3.0.0_"]}}
    full = dict((k, {"source": set(), "target": set()}) for k, _a, _b in ID_CATEGORIES)
    for item in built.values():
        item["ids"] = full
    out = []
    blocks = saves_blocks(built, prove_pairs(built, out.append))
    check("acceptFrom lists only the sources whose proof passes, a downgrade never",
          dict((v, sorted(b["acceptFrom"])) for v, b in blocks.items())
          == {"1.0": ["1.0"], "2.0": ["1.0", "2.0"], "3.0": ["1.0", "2.0", "3.0"]}
          and blocks["3.0"]["acceptFrom"]["1.0"] == {"drop": ["PlayerDataBin.6.5.1.2.2"]}
          and blocks["3.0"]["acceptFrom"]["2.0"] == {"drop": []}, blocks)


def _selftest_runs(tmp, check, quiet_main):
    import subprocess
    spec1, spec2 = _syn_spec(), _syn_spec_2()
    s1, s2 = os.path.join(tmp, "s1"), os.path.join(tmp, "s2")
    gs1, offsets1 = _syn_stack(s1, spec1, "RELWin1.0.0", _SYN_IDS_1, _SYN_DEPOTS_1, gacha=(22, 44))
    _syn_stack(s2, spec2, "RELWin2.0.0", _SYN_IDS_2, _SYN_DEPOTS_2, gacha=(22, 86))
    out, payloads = os.path.join(tmp, "out"), os.path.join(tmp, "payloads")
    os.makedirs(payloads)
    argv = ["--stack", "1.0=" + s1, "--stack", "2.0=" + s2, "--out-dir", out, "--payloads", payloads]
    code, text, err = quiet_main(argv)
    check("a run over two stacks", code == 0, err or text[-400:])
    check("its saves blocks: 2.0 accepts 1.0 with the drop, 1.0 accepts only itself",
          '1.0  "saves": {"acceptFrom": {"1.0": {"drop": []}}' in text
          and '2.0  "saves": {"acceptFrom": {"1.0": {"drop": ["PlayerDataBin.6.5.1.2.2"]}, "2.0": {"drop": []}}' in text
          and '"clientPrefixes": ["CNRELWin1.0.0_", "OSRELWin1.0.0_"]' in text, text[-800:])
    check("the skill depot a 1.0 avatar cannot hold is not asked of 2.0", "1.0 -> 2.0: PASS" in text, text[-1500:])
    check("the id sweep names the depot and the gacha schedule 2.0 lacks, for the pair that passes only",
          "1.0 -> 2.0: 1 table(s) with ids the target's tables lack, 0 table(s) the target has not" in text
          and "    AvatarSkillDepotData 1: 299" in text and "the target lacks 44" in text and "2.0 -> 1.0: 1 table" not in text,
          text[text.find("== id sweep"):][:600])

    def digests(folder):
        sums = {}
        for d, _s, names in os.walk(folder):
            for n in names:
                with open(os.path.join(d, n), "rb") as f:
                    sums[os.path.relpath(os.path.join(d, n), folder)] = hashlib.sha256(f.read()).hexdigest()
        return sums
    first = digests(out)
    code, _text, err = quiet_main(argv)
    check("a second run writes the same bytes", code == 0 and digests(out) == first and len(first) == 2, err)
    # another process, another hash seed, the stacks given the other way round
    other = os.path.join(tmp, "out-other")
    env = dict(os.environ, PYTHONHASHSEED="4321")
    r = subprocess.run([sys.executable, "-B", os.path.abspath(__file__), "--stack", "2.0=" + s2, "--stack",
                        "1.0=" + s1, "--out-dir", other, "--payloads", payloads], env=env, stdout=subprocess.PIPE,
                       stderr=subprocess.PIPE, timeout=300)
    check("a run in another process (hash seed 4321, stacks the other way round) writes the same bytes",
          r.returncode == 0 and digests(other) == first, r.stderr.decode("utf-8", "replace")[-300:])
    code, text, err = quiet_main(argv + ["--check"])
    check("--check of what the run wrote", code == 0 and text.count("ok       ") == 2, err or text[-300:])
    stale_dir = os.path.join(tmp, "out-stale")
    shutil.copytree(out, stale_dir)
    shutil.copytree(os.path.join(stale_dir, "1.0"), os.path.join(stale_dir, "9.9"))
    code, text, err = quiet_main(["--stack", "1.0=" + s1, "--stack", "2.0=" + s2, "--out-dir", stale_dir,
                                  "--payloads", payloads, "--check"])
    check("--check of a folder holding the schema of a version no stack names: exit 1",
          code == 1 and "STALE" in text and text.count("ok       ") == 2, err or text[-300:])
    copy_dir = os.path.join(tmp, "out-copy")
    shutil.copytree(out, copy_dir)
    victim = os.path.join(copy_dir, "2.0", SCHEMA_NAME)
    with open(victim, "rb") as f:
        data = bytearray(f.read())
    data[len(data) // 2] ^= 0x01
    with open(victim, "wb") as f:
        f.write(bytes(data))
    code, text, err = quiet_main(["--stack", "1.0=" + s1, "--stack", "2.0=" + s2, "--out-dir", copy_dir, "--payloads",
                                  payloads, "--check"])
    check("--check of a schema with one byte flipped: exit 1", code == 1 and "DIFFERS" in text, err or text[-300:])
    with open(os.path.join(out, "2.0", MANIFEST_NAME), "w") as f:
        json.dump({"saves": {"schema": SCHEMA_NAME, "clientPrefixes": ["OSRELWin2.0.0_"], "acceptFrom": {}}}, f)
    code, text, err = quiet_main(argv + ["--check"])
    check("--check of a manifest whose saves block is another: exit 1", code == 1 and "ANOTHER BLOCK" in text,
          err or text[-300:])
    os.remove(os.path.join(out, "2.0", MANIFEST_NAME))
    # dbgate
    bad_db = os.path.join(tmp, "s1-db")
    edited = dict(_syn_descriptors(_syn_field_edit(spec1, ".proto.PlayerBasicCompBin", "level", name="levet")))
    _syn_stack(bad_db, spec1, "RELWin1.0.0", _SYN_IDS_1, _SYN_DEPOTS_1, db_parts=sorted(edited.items()))
    code, text, err = quiet_main(["--stack", "1.0=" + bad_db, "--out-dir", os.path.join(tmp, "o2"), "--payloads",
                                  payloads])
    check("a dbgate whose copy of a shared descriptor differs: exit 1, no schema written",
          code == 1 and "DBGATE server_only/bin.server.proto (of the save closure)" in text
          and not os.path.exists(os.path.join(tmp, "o2", "1.0", SCHEMA_NAME)), err or text[-400:])
    no_player = os.path.join(tmp, "s1-db2")
    descs = _syn_descriptors(spec1)
    _syn_stack(no_player, spec1, "RELWin1.0.0", _SYN_IDS_1, _SYN_DEPOTS_1,
               db_parts=[("define.proto", descs["define.proto"])])
    code, text, err = quiet_main(["--stack", "1.0=" + no_player, "--out-dir", os.path.join(tmp, "o3"), "--payloads",
                                  payloads])
    check("a dbgate without the file of PlayerDataBin: exit 1", code == 1 and "is not in dbgate" in text,
          err or text[-400:])
    no_block = os.path.join(tmp, "s1-db3")
    _syn_stack(no_block, spec1, "RELWin1.0.0", _SYN_IDS_1, _SYN_DEPOTS_1,
               db_parts=[(n, d) for n, d in sorted(descs.items()) if n != "server_only/bin.block.proto"])
    code, text, err = quiet_main(["--stack", "1.0=" + no_block, "--out-dir", os.path.join(tmp, "o4"), "--payloads",
                                  payloads])
    check("a dbgate that lacks a file of the closure but shares the rest unchanged: a note, exit 0",
          code == 0 and "note: server_only/bin.block.proto is not in dbgate" in text and "identical" in text,
          err or text[-400:])
    for name, target in (("a file of the closure", "server_only/bin.block.proto"), ("define.proto", "define.proto")):
        broken = os.path.join(tmp, "s1-db-" + target.replace("/", "-"))
        # a field descriptor.proto does not define: the strict decoder does not take the copy
        _syn_stack(broken, spec1, "RELWin1.0.0", _SYN_IDS_1, _SYN_DEPOTS_1,
                   db_parts=[(n, d + _e_int(13, 1) if n == target else d) for n, d in sorted(descs.items())])
        o_broken = os.path.join(tmp, "o-" + target.replace("/", "-"))
        code, text, err = quiet_main(["--stack", "1.0=" + broken, "--out-dir", o_broken, "--payloads", payloads])
        check("a dbgate whose copy of %s does not decode: exit 1, no schema written" % name,
              code == 1 and "DBGATE %s" % target in text and "dbgate holds a descriptor of that name that does not "
              "decode" in text and not os.path.exists(o_broken), err or text[-400:])
    misc = dict(spec1, files=dict(spec1["files"], **{"cmd/cmd_misc.proto": [".proto.PingReq"]}),
                messages=dict(spec1["messages"], **{".proto.PingReq": {"fields": [_syn_field("seq", 1, "uint32")]}}))
    misc_db = dict(_syn_descriptors(_syn_field_edit(misc, ".proto.PingReq", "seq", name="sequence")))
    outside = os.path.join(tmp, "s1-db4")
    _syn_stack(outside, misc, "RELWin1.0.0", _SYN_IDS_1, _SYN_DEPOTS_1, db_parts=sorted(misc_db.items()))
    code, text, err = quiet_main(["--stack", "1.0=" + outside, "--out-dir", os.path.join(tmp, "o4b"), "--payloads",
                                  payloads])
    check("a dbgate whose copy of a shared file outside the closure differs: exit 1",
          code == 1 and "DBGATE cmd/cmd_misc.proto: gameserver" in text, err or text[-400:])
    broken_gs = os.path.join(tmp, "s1-gs-misc")
    _syn_stack(broken_gs, misc, "RELWin1.0.0", _SYN_IDS_1, _SYN_DEPOTS_1,
               gs_parts=[(n, d + _e_int(13, 1) if n == "cmd/cmd_misc.proto" else d)
                         for n, d in sorted(_syn_descriptors(misc).items())])
    code, text, err = quiet_main(["--stack", "1.0=" + broken_gs, "--out-dir", os.path.join(tmp, "o4c"), "--payloads",
                                  payloads])
    check("a gameserver whose copy of a file dbgate holds does not decode: exit 1",
          code == 1 and "DBGATE cmd/cmd_misc.proto: the gameserver holds a descriptor of that name that does not "
          "decode" in text, err or text[-400:])
    twin = os.path.join(tmp, "s3")
    _syn_stack(twin, spec2, "RELWin1.0.0", _SYN_IDS_2, _SYN_DEPOTS_2)
    code, text, err = quiet_main(["--stack", "1.0=" + s1, "--stack", "2.0=" + twin, "--out-dir",
                                  os.path.join(tmp, "o5"), "--payloads", payloads])
    check("two stacks that bind one client version: exit 2, nothing written",
          code == 2 and "more than one stack" in err and not os.path.exists(os.path.join(tmp, "o5")), err)
    no_sql = os.path.join(tmp, "s2-nosql")
    shutil.copytree(s2, no_sql)
    os.remove(os.path.join(no_sql, DATA_SQL))
    code, text, err = quiet_main(["--stack", "1.0=" + s1, "--stack", "2.0=" + no_sql, "--out-dir",
                                  os.path.join(tmp, "o6"), "--payloads", payloads])
    check("a stack read after a good one cannot be read: exit 2, the good one's schema not written either",
          code == 2 and DATA_SQL in err and not os.path.exists(os.path.join(tmp, "o6")), err)
    code, text, err = quiet_main(["--stack", "1.0=" + bad_db, "--stack", "2.0=" + s2, "--out-dir",
                                  os.path.join(tmp, "o7"), "--payloads", payloads])
    check("a stack whose dbgate fails beside a good one: exit 1, nothing written, no saves block printed",
          code == 1 and not os.path.exists(os.path.join(tmp, "o7")) and '"saves":' not in text
          and "none printed" in text and "left out of the pair proofs: 1.0" in text and "2.0 -> 2.0: PASS" in text,
          err or text[-600:])
    # identity
    code, text, err = quiet_main(["--identify", s1, "--out-dir", out])
    check("--identify names the one schema the gameserver matches", code == 0 and "the server build of 1.0" in text,
          err or text)
    schema1 = load_schema(os.path.join(out, "1.0", SCHEMA_NAME))
    spot = schema1["fingerprints"][0]
    for name, at, matches in (("inside", spot["offset"] + spot["size"] // 2, False), ("outside", 2, True)):
        flipped = os.path.join(tmp, "flip-" + name)
        os.makedirs(os.path.join(flipped, "server", "gameserver"))
        data = bytearray(gs1)
        data[at] ^= 0x01
        with open(os.path.join(flipped, *GAMESERVER.split("/")), "wb") as f:
            f.write(bytes(data))
        problems = fingerprint_problems(schema1, os.path.join(flipped, *GAMESERVER.split("/")))
        code, text, err = quiet_main(["--identify", flipped, "--out-dir", out])
        if matches:
            check("a byte flipped outside the fingerprinted spans still matches", not problems and code == 0, problems)
        else:
            check("a byte flipped inside a fingerprinted span is a reported mismatch",
                  problems and spot["file"] in problems[0] and code == 1 and "a server build no schema knows" in text,
                  problems or text)
            check("the spans hashed in reads of 7 bytes say the same",
                  fingerprint_problems(schema1, os.path.join(flipped, *GAMESERVER.split("/")), chunk=7) == problems
                  and fingerprint_problems(schema1, os.path.join(s1, *GAMESERVER.split("/")), chunk=7) == [])
    two = os.path.join(tmp, "out-two")
    shutil.copytree(out, two)
    shutil.copytree(os.path.join(two, "1.0"), os.path.join(two, "1.1"))
    code, text, err = quiet_main(["--identify", s1, "--out-dir", two])
    check("--identify with two schemas that match: exit 1", code == 1 and "matched by 1.0, 1.1" in text, err or text)
    code, text, err = quiet_main(["--identify", os.path.join(tmp, "nowhere"), "--out-dir", out])
    check("--identify of a folder without a gameserver: exit 2", code == 2 and "cannot be read" in err, err or text)
    for name, edit, reason in (
            ("without its nested roots", lambda s: s.pop("nested"), "nested is missing"),
            ("naming a span longer than SPAN_MAX", lambda s: s["fingerprints"][0].update(size=SPAN_MAX + 1),
             "a fingerprint that is not one")):
        bad_dir = os.path.join(tmp, "out-bad-" + name.split()[0])
        os.makedirs(os.path.join(bad_dir, "1.0"))
        damaged = load_schema(os.path.join(out, "1.0", SCHEMA_NAME))
        edit(damaged)
        with open(os.path.join(bad_dir, "1.0", SCHEMA_NAME), "wb") as f:
            f.write(encode(damaged))
        code, text, err = quiet_main(["--identify", s1, "--out-dir", bad_dir])
        check("a schema file %s: exit 2 with the reason" % name, code == 2 and reason in err, err or text)
    # the tables a payload puts in place: config = the payload's alone; any other stage = the vendor's or the
    # payload's (a save can hold the ids of either, the version defines those of both)
    tpay = os.path.join(tmp, "payloads-tables")
    os.makedirs(os.path.join(tpay, "1.0", "txt"))

    def payload_table(entry, avatars):
        with open(os.path.join(tpay, "1.0", MANIFEST_NAME), "w") as f:
            json.dump({"files": [dict(entry, src="txt/AvatarData.txt", dst=DATA_DIR + "/txt/AvatarData.txt")]}, f)
        _syn_tables(os.path.join(tmp, "patched"), dict(_SYN_IDS_1, AvatarData=avatars), _SYN_DEPOTS_1)
        shutil.copy(os.path.join(tmp, "patched", "AvatarData.txt"), os.path.join(tpay, "1.0", "txt", "AvatarData.txt"))
    for entry in ({"stage": "config", "tsv": True}, {"tsv": True}):
        payload_table(entry, [1001, 1002, 1009])
        ids = read_ids(s1, "1.0", tpay)
        check("a table a %s entry puts in place is the payload's alone" % (entry.get("stage") or "save+tsv"),
              ids["avatar"] == {"source": {1001, 1002, 1009}, "target": {1001, 1002, 1009}}
              and [w for _p, w in ids["_files"]["AvatarData"]] == ["payload (config)"]
              and ids["skillDepot"]["source"] == {101, 201, 202}, (ids["avatar"], ids["_files"]["AvatarData"]))
    for entry in ({"stage": "txt_fix", "tsv": True}, {}):
        payload_table(entry, [1001, 1009])
        ids = read_ids(s1, "1.0", tpay)
        check("a table a %s entry puts in place: the source holds the ids of either form, the target those of both"
              % (entry.get("stage") or "save"),
              ids["avatar"] == {"source": {1001, 1002, 1009}, "target": {1001}}
              and [w for _p, w in ids["_files"]["AvatarData"]][0] == "vendor", ids["avatar"])
    tstack = os.path.join(tmp, "s1-orig")
    shutil.copytree(s1, tstack)
    folder = os.path.join(tstack, DATA_DIR, "txt")
    _syn_tables(os.path.join(tmp, "vendor-orig"), dict(_SYN_IDS_1, AvatarData=[1001, 1002, 1005]), _SYN_DEPOTS_1)
    shutil.copy(os.path.join(tmp, "vendor-orig", "AvatarData.txt"), os.path.join(folder, "AvatarData.txt.orig"))
    shutil.copy(os.path.join(tpay, "1.0", "txt", "AvatarData.txt"), os.path.join(folder, "AvatarData.txt"))
    payload_table({"stage": "txt_fix", "tsv": True}, [1001, 1009])
    ids = read_ids(tstack, "1.0", tpay)
    check("the vendor's form of a table is its <name>.orig where the agent keeps one",
          ids["avatar"] == {"source": {1001, 1002, 1005, 1009}, "target": {1001}}
          and ids["_files"]["AvatarData"][0][0].endswith("AvatarData.txt.orig"), (ids["avatar"], ids["_files"]))
    code, text, err = quiet_main(["--stack", "1.0=" + s1, "--stack", "2.0=" + s2, "--out-dir", os.path.join(tmp, "o8"),
                                  "--payloads", tpay])
    check("an avatar a 1.0 txt fix adds and 2.0 lacks fails 1.0 -> 2.0",
          "1.0 -> 2.0: FAIL" in text and "avatar: 1009" in text and code == 0, err or text[-600:])
    nested_root = os.path.join(tmp, "nested-stack")
    _syn_tables(os.path.join(nested_root, DATA_DIR, "x-output_7-server-data", "txt"), _SYN_IDS_1, _SYN_DEPOTS_1)
    check("tables of a data folder nested one level deeper are found",
          read_ids(nested_root, "1.0", payloads)["avatar"]["target"] == {1001, 1002})
    # SQL
    sql = (b"CREATE TABLE `t` (\n  `a` int,\n  `b` blob,\n  `c` text\n);\n"
           b"INSERT INTO `t` VALUES (1, 0x00ff, 'x\\'y''z\\\\\\n\\0\\Z'), (-2.5, _binary 'q', NULL),"
           b" (3, X'4142', 'a\\%b\\_c');\n"
           b"INSERT INTO `u` (`k`) VALUES (UNHEX('4142')), (CONCAT('a', ')'));\n")
    rows = list(sql_rows(sql, "sql"))
    check("SQL rows: strings with escapes, hex, _binary, NULL, numbers, UNHEX, a call",
          rows == [("t", {"a": "1", "b": b"\x00\xff", "c": b"x'y'z\\\n\x00\x1a"}),
                   ("t", {"a": "-2.5", "b": b"q", "c": None}),
                   ("t", {"a": "3", "b": b"AB", "c": b"a\\%b\\_c"}),
                   ("u", {"k": b"AB"}), ("u", {"k": "CONCAT('a', ')')"})], rows)
    check("a filtered read takes only the rows of its tables", list(sql_rows(sql, "sql", tables=("u",))) == rows[3:])
    sql2 = (b"CREATE TABLE t2 (\n  a int NOT NULL,\n  b blob,\n  PRIMARY KEY (a),\n  KEY k (b)\n) ENGINE=InnoDB;\n"
            b"INSERT IGNORE INTO t2 VALUES (1, 'x');\n"
            b"REPLACE INTO `db`.`t2` (`a`, `b`) VALUES (2, 'y');\n"
            b"insert into db.t2 (a, b) values (3, 'z');\n"
            b"INSERT INTO t20 (a) VALUES (9);\n")
    rows2 = list(sql_rows(sql2, "sql2"))
    check("SQL rows of INSERT IGNORE, REPLACE, names without quotes or with a database, a CREATE TABLE without "
          "quotes", rows2 == [("t2", {"a": "1", "b": b"x"}), ("t2", {"a": "2", "b": b"y"}),
                              ("t2", {"a": "3", "b": b"z"}), ("t20", {"a": "9"})], rows2)
    check("a filtered read of an unquoted name skips a table whose name only starts with it",
          list(sql_rows(sql2, "sql2", tables=("t2",))) == rows2[:3])
    check("the client prefixes and the gacha schedules of a stack",
          stack_sql(s1) == (["CNRELWin1.0.0_", "OSRELWin1.0.0_"], {22, 44}), stack_sql(s1))
    # saves
    schemas = {"1.0": _syn_schema("1.0", spec1), "2.0": _syn_schema("2.0", spec2)}
    prefixes = {"1.0": ["OSRELWin1.0.0_"], "2.0": ["OSRELWin2.0.0_"]}
    dump = os.path.join(tmp, "dump.sql")
    _syn_dump(dump, _syn_save("OSRELWin1.0.0_R1_S2_D3"))
    lines, failed = decode_dump(dump, schemas, prefixes)
    text = "\n".join(lines)
    check("a dump decodes cleanly on its own version, the dropped field is unknown on the newer one",
          not failed and "uid 1: client OSRELWin1.0.0_R1_S2_D3 -> 1.0" in text
          and "on 1.0 (own version of uid 1): fields 33, unknown 0 kind(s) (0 fields), mismatches 0" in text
          and "on 2.0: fields 31, unknown 1 kind(s) (1 fields), mismatches 0" in text
          and "[PlayerDataBin.6.5.1.2.2] wire 2 x1" in text and "time 400 x1" in text, text)
    _syn_dump(dump, _syn_save("OSRELWin1.0.0_R1", level_field=20))
    lines, failed = decode_dump(dump, schemas, prefixes)
    check("a save holding a field its own version does not know fails", failed, "\n".join(lines))
    _syn_dump(dump, _syn_save("OSRELWin1.0.0_R1", wrong_wire=True))
    lines, failed = decode_dump(dump, schemas, prefixes)
    check("a wire type that does not fit is a mismatch",
          failed and "MISMATCH PlayerDataBin.avatar_bin.avatar_list.guid [PlayerDataBin.2.1.3] wire 2 for uint64 x1"
          in "\n".join(lines), "\n".join(lines))
    _syn_dump(dump, _syn_save("OSRELWin1.0.0_R1"))
    code, text, err = quiet_main(["--saves", dump, "--out-dir", out])
    check("--saves from the schemas of --out-dir, no prefixes known: decoded, nothing claimed",
          code == 0 and "claimed by no version given" in text, err or text)
    with open(os.path.join(out, "1.0", MANIFEST_NAME), "w") as f:
        json.dump({"saves": {"schema": SCHEMA_NAME, "clientPrefixes": ["OSRELWin1.0.0_"], "acceptFrom": {}}}, f)
    code, text, err = quiet_main(["--saves", dump, "--out-dir", out])
    check("--saves takes the client prefixes of a manifest beside a schema",
          code == 0 and "-> 1.0" in text and "own version of uid 1" in text, err or text)
    _syn_dump(dump, _syn_save("OSRELWin1.0.0_R1", level_field=20))
    code, text, err = quiet_main(["--saves", dump, "--out-dir", out])
    check("--saves: exit 1 for a save that is not clean on its own version", code == 1, err or text)
    for name, save, before, claims, want in (
            ("a save that does not decode on its own version", _syn_save("OSRELWin1.0.0_R1", truncate=2), None,
             prefixes, "FAIL uid 1 does not decode cleanly on 1.0"),
            ("a blob of a root its own version has not", _syn_save("OSRELWin1.0.0_R1"), _e_int(1, 7), prefixes,
             "FAIL uid 1 does not decode cleanly on 1.0"),
            ("a save two versions claim", _syn_save("OSRELWin1.0.0_R1"), None,
             {"1.0": ["OSRELWin1.0.0_"], "2.0": ["OSRELWin1."]}, "FAIL uid 1: two versions claim"),
            ("a save no version claims that no version reads cleanly", _syn_save("OSRELWin1.0.0_R1", level_field=21),
             None, {}, "FAIL uid 1, claimed by no version, decodes cleanly on none of 1.0, 2.0")):
        _syn_dump(dump, save, before_login=before)
        lines, failed = decode_dump(dump, schemas, claims)
        check("--saves fails %s" % name, failed and want in "\n".join(lines), "\n".join(lines))
    check("--saves says when no client prefixes are known", "no client prefixes known" in "\n".join(lines),
          "\n".join(lines))
    _syn_dump(dump, _syn_save("OSRELWin1.0.0_R1"))
    with open(dump, "rb") as f:
        data = f.read()
    for name, old, new in (("REPLACE INTO, the name unquoted", b"INSERT INTO `t_player_data_1`",
                            b"REPLACE INTO t_player_data_1"),
                           ("INSERT IGNORE INTO", b"INSERT INTO `t_player_data_1`",
                            b"INSERT IGNORE INTO `t_player_data_1`")):
        other_dump = os.path.join(tmp, "dump-%s.sql" % name.split()[0].lower())
        with open(other_dump, "wb") as f:
            f.write(data.replace(old, new))
        lines, failed = decode_dump(other_dump, schemas, prefixes)
        check("a dump written with %s is read" % name, not failed and "own version of uid 1" in "\n".join(lines),
              "\n".join(lines))
    empty = os.path.join(tmp, "dump-empty.sql")
    with open(empty, "wb") as f:
        f.write(b"INSERT INTO `t_home_data_1` (`uid`, `bin_data`) VALUES (1, 0x0a00);\n")
    code, text, err = quiet_main(["--saves", empty, "--out-dir", out])
    check("--saves of a dump without a save row: exit 2", code == 2 and "no save in it" in err, err or text)


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    sys.exit(main(sys.argv[1:]))
