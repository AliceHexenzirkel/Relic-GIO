#!/usr/bin/env python3
"""Idempotent version gates in the feature code for game 1.6 (members / functions that do not exist there):
  * AutoFish (fishing = 2.1): whole feature compiled and registered only for RELIC_GAME_VERSION >= 21
  * filters.cpp FishingPoint: EntityType FishPool only >= 21
  * NoClip.cpp HumanoidMoveFSM._layerMaskScene, AutoLoot.cpp CheckAddItemExceedLimitNotify.msgType_,
    Debug.cpp PlayerLoginReq.string_15 / InteractionManager._isDelayClear: >= 28
  * null guards for direct calls of functions that are 0x0 on 1.6: FreeCamera Miscs_SetUILocalAvatarVisible
    (InteractiveMap's get_miniMapScale needs none: the offset is resolved in offsets-16.overrides.json
     and the minimap scale is read from the field on both versions)
"""
import os, re

HERE = os.path.dirname(os.path.abspath(__file__))
U = os.path.normpath(os.path.join(HERE, "..", "mod", "cheat-library", "src", "user", "cheat"))

def rd(p):
    with open(p, "r", encoding="utf-8", newline="") as f:
        return f.read()

def wr(p, s):
    with open(p, "w", encoding="utf-8", newline="") as f:
        f.write(s)

def patch(rel, old, new, marker=None):
    p = os.path.join(U, *rel.split("/"))
    s = rd(p)
    if (marker or new) in s:
        print(f"{rel}: already patched"); return
    assert old in s, f"{rel}: pattern not found:\n{old}"
    wr(p, s.replace(old, new, 1)); print(f"{rel}: patched")

def nl_of(rel):
    return "\r\n" if "\r\n" in rd(os.path.join(U, *rel.split("/"))) else "\n"

# --- AutoFish: whole .cpp body + registration gated >= 21 ---
rel = "world/AutoFish.cpp"; nl = nl_of(rel)
s = rd(os.path.join(U, "world", "AutoFish.cpp"))
if "#if RELIC_GAME_VERSION >= 21 // Relic: fishing exists since 2.1" not in s:
    s = s.replace("#include <cheat/game/util.h>" + nl, "#include <cheat/game/util.h>" + nl + nl + "#if RELIC_GAME_VERSION >= 21 // Relic: fishing exists since 2.1" + nl, 1)
    s = s.rstrip("\r\n") + nl + "#endif // RELIC_GAME_VERSION >= 21" + nl
    wr(os.path.join(U, "world", "AutoFish.cpp"), s); print("world/AutoFish.cpp: body gated >= 21")
else:
    print("world/AutoFish.cpp: already gated")
rel = "cheat.cpp"; nl = nl_of(rel)
patch(rel, "#include <cheat/world/AutoFish.h>" + nl,
      "#if RELIC_GAME_VERSION >= 21" + nl + "#include <cheat/world/AutoFish.h>" + nl + "#endif" + nl,
      marker="#if RELIC_GAME_VERSION >= 21" + nl + "#include <cheat/world/AutoFish.h>")
patch(rel, "\t\t\tFEAT_INST(AutoFish)," + nl,
      "#if RELIC_GAME_VERSION >= 21" + nl + "\t\t\tFEAT_INST(AutoFish)," + nl + "#endif" + nl,
      marker="#if RELIC_GAME_VERSION >= 21" + nl + "\t\t\tFEAT_INST(AutoFish),")

# --- filters.cpp: FishPool only >= 21 ---
rel = "game/filters.cpp"; nl = nl_of(rel)
patch(rel, '\t\tSimpleFilter FishingPoint = { EntityType__Enum_1::FishPool, "_FishingShoal" };',
      "#if RELIC_GAME_VERSION >= 21" + nl +
      '\t\tSimpleFilter FishingPoint = { EntityType__Enum_1::FishPool, "_FishingShoal" };' + nl +
      "#else" + nl +
      '\t\tSimpleFilter FishingPoint = { EntityType__Enum_1::Gadget, "_FishingShoal" }; // Relic: no fishing before 2.1 (never matches)' + nl +
      "#endif")

# --- NoClip.cpp: _layerMaskScene only >= 28 ---
rel = "player/NoClip.cpp"; nl = nl_of(rel)
patch(rel, "\t\t\tif (!noClip.f_NoAnimation->enabled()) " + nl + "\t\t\t\t__this->fields._layerMaskScene = 2;" + nl + "\t\t\telse" + nl + "\t\t\t\treturn;",
      "\t\t\tif (!noClip.f_NoAnimation->enabled()) " + nl +
      "\t\t\t{" + nl +
      "#if RELIC_GAME_VERSION >= 28 // Relic: the 1.6 HumanoidMoveFSM has no _layerMaskScene" + nl +
      "\t\t\t\t__this->fields._layerMaskScene = 2;" + nl +
      "#endif" + nl +
      "\t\t\t}" + nl +
      "\t\t\telse" + nl + "\t\t\t\treturn;")

# --- AutoLoot.cpp: msgType_ only >= 28 ---
rel = "world/AutoLoot.cpp"; nl = nl_of(rel)
patch(rel, "\t\tif (autoLoot.f_AutoPickup->enabled() && autoLoot.f_AutoDisablePickupWhenAddItemExceedLimit->enabled() &&" + nl +
           "\t\t\t// notify->fields.reason_ != 1103 // ACTION_REASON_HOME_PLANT_BOX_GATHER = 0x44F, alternative to only exclude seed box gather if below condition has problems" + nl +
           "\t\t\tnotify->fields.msgType_ != app::Proto_CheckAddItemExceedLimitNotify_ItemExceedLimitMsgType__Enum::ITEM_EXCEED_LIMIT_MSG_TYPE_TEXT) // exclude if prompt is only text",
      "\t\tif (autoLoot.f_AutoPickup->enabled() && autoLoot.f_AutoDisablePickupWhenAddItemExceedLimit->enabled()" + nl +
      "#if RELIC_GAME_VERSION >= 28 // Relic: the 1.6 notify has no msgType_" + nl +
      "\t\t\t// notify->fields.reason_ != 1103 // ACTION_REASON_HOME_PLANT_BOX_GATHER = 0x44F, alternative to only exclude seed box gather if below condition has problems" + nl +
      "\t\t\t&& notify->fields.msgType_ != app::Proto_CheckAddItemExceedLimitNotify_ItemExceedLimitMsgType__Enum::ITEM_EXCEED_LIMIT_MSG_TYPE_TEXT // exclude if prompt is only text" + nl +
      "#endif" + nl +
      "\t\t\t)")

# --- Debug.cpp: string_15 / _isDelayClear only >= 28 ---
rel = "misc/Debug.cpp"; nl = nl_of(rel)
patch(rel, "        printString(15);" + nl, "#if RELIC_GAME_VERSION >= 28" + nl + "        printString(15);" + nl + "#endif" + nl,
      marker="#if RELIC_GAME_VERSION >= 28" + nl + "        printString(15);")
patch(rel, "        DRAW_BOOL(interactionManager, _isDelayClear);" + nl,
      "#if RELIC_GAME_VERSION >= 28" + nl + "        DRAW_BOOL(interactionManager, _isDelayClear);" + nl + "#endif" + nl,
      marker="#if RELIC_GAME_VERSION >= 28" + nl + "        DRAW_BOOL(interactionManager, _isDelayClear);")

# --- FreeCamera.cpp: null-guard Miscs_SetUILocalAvatarVisible (0x0 on 1.6) ---
rel = "visuals/FreeCamera.cpp"; nl = nl_of(rel)
patch(rel, "\t\t\tapp::Miscs_SetUILocalAvatarVisible(false, nullptr);" + nl + "\t\t\tisVisible = false;",
      "\t\t\tif (app::Miscs_SetUILocalAvatarVisible != nullptr) // Relic: absent on 1.6" + nl +
      "\t\t\t\tapp::Miscs_SetUILocalAvatarVisible(false, nullptr);" + nl + "\t\t\tisVisible = false;")
patch(rel, "\t\t\t\tapp::Miscs_SetUILocalAvatarVisible(true, nullptr);" + nl + "\t\t\t\tisVisible = true;",
      "\t\t\t\tif (app::Miscs_SetUILocalAvatarVisible != nullptr)" + nl +
      "\t\t\t\t\tapp::Miscs_SetUILocalAvatarVisible(true, nullptr);" + nl + "\t\t\t\tisVisible = true;")

# --- InteractiveMap.cpp: get_miniMapScale needs no 1.6 guard ---
# The offset is resolved on 1.6
# (tools/offsets-16.overrides.json -> KCFCJJBPLEN$$MKLHNEKDNCJ 0x008FC2B0), so the call
# needs no gate at all. Nothing to patch here.
print("done")
