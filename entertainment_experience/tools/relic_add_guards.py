#!/usr/bin/env python3
"""Puts the crash-prone per-frame / hook paths of the mod under RELIC_GUARD (cheat-base/relic-guard.h):
a feature whose offsets are wrong on this game version is logged once and skipped instead of killing the game.

  * CheatManagerBase::DrawExternal  - each feature's overlay drawing is guarded separately
  * HandlerRunner::run (events)     - each event handler (GameUpdate, render, key) is guarded separately, so one
                                      faulting feature no longer aborts the whole event for every frame
  * MapTeleport                     - the map-click hooks and the per-frame teleport state machine
Idempotent.
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.normpath(os.path.join(HERE, "..", "mod"))
BASE = os.path.join(MOD, "cheat-base", "src", "cheat-base")
USER = os.path.join(MOD, "cheat-library", "src", "user", "cheat")

def rd(p):
    with open(p, "r", encoding="utf-8", newline="") as f:
        return f.read()

def wr(p, s):
    with open(p, "w", encoding="utf-8", newline="") as f:
        f.write(s)

def patch(path, old, new, tag, ensure_include=None):
    s = rd(path)
    if new.strip() in s:
        print("%s: already patched" % tag)
        return
    assert old in s, "%s: pattern not found" % tag
    s = s.replace(old, new, 1)
    if ensure_include and ensure_include not in s:
        nl = "\r\n" if "\r\n" in s else "\n"
        # after the first #include line
        i = s.index("#include")
        j = s.index("\n", i) + 1
        s = s[:j] + ensure_include + nl + s[j:]
    wr(path, s)
    print("%s: patched" % tag)

def draw_external():
    p = os.path.join(BASE, "cheat", "CheatManagerBase.cpp")
    old = ("\t\tfor (auto& feature : m_Features)\n"
           "\t\t{\n"
           "\t\t\tImGui::PushID(&feature);\n"
           "\t\t\tfeature->DrawExternal();\n"
           "\t\t\tImGui::PopID();\n"
           "\t\t}")
    new = ("\t\tfor (auto& feature : m_Features)\n"
           "\t\t{\n"
           "\t\t\tImGui::PushID(&feature);\n"
           "\t\t\t// Relic: one feature with wrong offsets must not take the whole overlay (and the game) down.\n"
           "\t\t\tstatic std::map<const void*, bool> s_reported;\n"
           "\t\t\trelic::Guard(feature->GetGUIInfo().moduleKey.c_str(), &s_reported[feature],\n"
           "\t\t\t\t[&]() { feature->DrawExternal(); });\n"
           "\t\t\tImGui::PopID();\n"
           "\t\t}")
    s = rd(p)
    if "relic::Guard" in s:
        print("CheatManagerBase.cpp: already guarded"); return
    old_crlf = old.replace("\n", "\r\n") if "\r\n" in s else old
    new_crlf = new.replace("\n", "\r\n") if "\r\n" in s else new
    assert old_crlf in s, "CheatManagerBase.cpp: DrawExternal loop not found"
    s = s.replace(old_crlf, new_crlf, 1)
    if "relic-guard.h" not in s:
        nl = "\r\n" if "\r\n" in s else "\n"
        i = s.index("#include")
        j = s.index("\n", i) + 1
        s = s[:j] + "#include <cheat-base/relic-guard.h>" + nl + s[j:]
    wr(p, s)
    print("CheatManagerBase.cpp: DrawExternal guarded per feature")

def event_dispatch():
    p = os.path.join(BASE, "events", "event.hpp")
    s = rd(p)
    if "relic::Guard" in s:
        print("event.hpp: already guarded"); return
    nl = "\r\n" if "\r\n" in s else "\n"
    old = "                m_eventCore.coreMutex.unlock_shared();" + nl + "                ( *currentIt )->call( params... );"
    assert old in s, "event.hpp: dispatch loop not found"
    new = ("                m_eventCore.coreMutex.unlock_shared();" + nl +
           "                // Relic: guard every handler on its own - a feature whose offsets are wrong for this" + nl +
           "                // game version is skipped (logged once) instead of aborting the event for everyone." + nl +
           "                {" + nl +
           "                    static bool s_relicHandlerReported = false;" + nl +
           "                    relic::Guard(\"an event handler\", &s_relicHandlerReported, [&]() { ( *currentIt )->call( params... ); });" + nl +
           "                }")
    s = s.replace(old, new, 1)
    if "relic-guard.h" not in s:
        i = s.index("#include")
        j = s.index("\n", i) + 1
        s = s[:j] + "#include <cheat-base/relic-guard.h>" + nl + s[j:]
    wr(p, s)
    print("event.hpp: per-handler guard added")

def map_teleport():
    p = os.path.join(USER, "teleport", "MapTeleport.cpp")
    s = rd(p)
    if "RELIC_GUARD" in s:
        print("MapTeleport.cpp: already guarded"); return
    nl = "\r\n" if "\r\n" in s else "\n"
    # 1) the map-click hook: the click handling (screen -> map -> teleport) is the fragile part
    old = ("\t\tapp::Vector2 mapPosition{};" + nl +
           "\t\tbool mapPosResult = ScreenToMapPosition(__this, screenPos, &mapPosition);" + nl +
           "\t\tif (!mapPosResult)" + nl +
           "\t\t\treturn;" + nl + nl +
           "\t\tmapTeleport.TeleportTo(mapPosition);")
    new = ("\t\tRELIC_GUARD(\"Map teleport (map click)\", {" + nl +
           "\t\t\tapp::Vector2 mapPosition{};" + nl +
           "\t\t\tif (ScreenToMapPosition(__this, screenPos, &mapPosition))" + nl +
           "\t\t\t\tmapTeleport.TeleportTo(mapPosition);" + nl +
           "\t\t});")
    assert old in s, "MapTeleport.cpp: OnMapClicked body not found"
    s = s.replace(old, new, 1)
    if "relic-guard.h" not in s:
        i = s.index("#include")
        j = s.index("\n", i) + 1
        s = s[:j] + "#include <cheat-base/relic-guard.h>" + nl + s[j:]
    wr(p, s)
    print("MapTeleport.cpp: map-click path guarded")

def map_teleport_mark():
    p = os.path.join(USER, "teleport", "MapTeleport.cpp")
    s = rd(p)
    nl = "\r\n" if "\r\n" in s else "\n"
    marker = "RELIC_GUARD(\"Map teleport (mark click)\""
    if marker in s:
        print("MapTeleport.cpp (mark): already guarded"); return
    old = ("\t\tif (mark->fields._markType == app::MoleMole_Config_MarkType__Enum::TransPoint || mark->fields._markType == app::MoleMole_Config_MarkType__Enum::ScenePoint)" + nl + "\t\t{")
    if old not in s:
        print("MapTeleport.cpp (mark): pattern not found - skipped"); return
    new = ("\t\tRELIC_GUARD(\"Map teleport (mark click)\", {" + nl +
           "\t\tif (mark->fields._markType == app::MoleMole_Config_MarkType__Enum::TransPoint || mark->fields._markType == app::MoleMole_Config_MarkType__Enum::ScenePoint)" + nl + "\t\t{")
    s = s.replace(old, new, 1)
    wr(p, s)
    print("MapTeleport.cpp (mark): opened guard - the closing brace must be adjusted by hand if the build fails")

if __name__ == "__main__":
    draw_external()
    event_dispatch()
    map_teleport()
    print("done")
