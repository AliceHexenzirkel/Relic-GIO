#!/usr/bin/env python3
"""Adds (default) or removes (--off) the one-session 1.6 diagnostics for map teleport and RapidFire.

Every line is gated on RELIC_GAME_VERSION <= 16 and tagged RELIC-DIAG16, so the 2.8 build is untouched and
`--off` strips them again once the session has answered the question. The lines are a decision table:
they tell apart "dictionary read works / waypoint filter rejects / singleton null" for the
teleport and "hook not entered / filter rejects the target by name / fires N times" for RapidFire.
"""
import os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
U = os.path.normpath(os.path.join(HERE, "..", "mod", "cheat-library", "src", "user", "cheat"))
TAG = "RELIC-DIAG16"

EDITS = [
    # --- teleport: game/util.cpp GetUnlockedWaypoints ---
    (os.path.join(U, "game", "util.cpp"),
     "\t\tauto waypointGroups = TO_UNI_DICT(mapModule->fields._scenePointDics, uint32_t, UniDict<uint32_t COMMA app::MapModule_ScenePointData>*);\n"
     "\t\tfor (const auto& [sceneId, waypoints] : waypointGroups->pairs())\n"
     "\t\t{\n"
     "\t\t\tif (sceneId != targetSceneId)\n"
     "\t\t\t\tcontinue;\n",
     "\t\tauto waypointGroups = TO_UNI_DICT(mapModule->fields._scenePointDics, uint32_t, UniDict<uint32_t COMMA app::MapModule_ScenePointData>*);\n"
     "#if RELIC_GAME_VERSION <= 16   // " + TAG + "\n"
     "\t\tLOG_DEBUG(\"[wp16] mapModule=%p dict=%p touched=%d count=%d groups=%zu target=%u\", mapModule, waypointGroups,\n"
     "\t\t\twaypointGroups ? waypointGroups->touchedSlots : -1, waypointGroups ? waypointGroups->count : -1,\n"
     "\t\t\twaypointGroups ? waypointGroups->pairs().size() : (size_t)0, targetSceneId);\n"
     "\t\tint relicDiagShown = 0;\n"
     "#endif\n"
     "\t\tfor (const auto& [sceneId, waypoints] : waypointGroups->pairs())\n"
     "\t\t{\n"
     "\t\t\tif (sceneId != targetSceneId)\n"
     "\t\t\t\tcontinue;\n"),
    (os.path.join(U, "game", "util.cpp"),
     "\t\t\t\tbool isAreaUnlocked = app::MoleMole_MapModule_IsAreaUnlock(mapModule, sceneId, areaId, nullptr);\n\n"
     "\t\t\t\tif (waypoint.isUnlocked && isAreaUnlocked",
     "\t\t\t\tbool isAreaUnlocked = app::MoleMole_MapModule_IsAreaUnlock(mapModule, sceneId, areaId, nullptr);\n"
     "#if RELIC_GAME_VERSION <= 16   // " + TAG + "\n"
     "\t\t\t\tif (relicDiagShown++ < 40)\n"
     "\t\t\t\t\tLOG_DEBUG(\"[wp16] %u/%u cfg=%p unlocked=%d area=%u areaUnlocked=%d cfgUnlocked=%d groupLimit=%d hidden=%d tp=%d pos=(%.0f,%.0f,%.0f)\",\n"
     "\t\t\t\t\t\tsceneId, waypointId, waypoint.config, (int)waypoint.isUnlocked, (unsigned)areaId, (int)isAreaUnlocked,\n"
     "\t\t\t\t\t\t(int)config._unlocked, (int)waypoint.isGroupLimit, (int)waypoint.isModelHidden,\n"
     "\t\t\t\t\t\t(int)IsWaypointTeleportable(waypoint.config), config._tranPos.x, config._tranPos.y, config._tranPos.z);\n"
     "#endif\n\n"
     "\t\t\t\tif (waypoint.isUnlocked && isAreaUnlocked"),
    (os.path.join(U, "game", "util.cpp"),
     "\t\t\t\t\tresult.push_back(WaypointInfo{ sceneId, waypointId, waypoint.config->fields._tranPos, (app::MapModule_ScenePointData*)&waypoint });\n"
     "\t\t\t}\n"
     "\t\t}\n"
     "\t\treturn result;\n",
     "\t\t\t\t\tresult.push_back(WaypointInfo{ sceneId, waypointId, waypoint.config->fields._tranPos, (app::MapModule_ScenePointData*)&waypoint });\n"
     "\t\t\t}\n"
     "\t\t}\n"
     "#if RELIC_GAME_VERSION <= 16   // " + TAG + "\n"
     "\t\tLOG_DEBUG(\"[wp16] result=%zu\", result.size());\n"
     "#endif\n"
     "\t\treturn result;\n"),

    # --- RapidFire: player/RapidFire.cpp ---
    (os.path.join(U, "player", "RapidFire.cpp"),
     "\t\tauto attacker = game::Entity(__this->fields._._._entity);\n"
     "\t\tRapidFire& rapidFire = RapidFire::GetInstance();\n\n"
     "\t\tif (!IsConfigByAvatar(attacker) || !IsAttackByAvatar(attacker) || !rapidFire.f_Enabled)\n",
     "\t\tauto attacker = game::Entity(__this->fields._._._entity);\n"
     "\t\tRapidFire& rapidFire = RapidFire::GetInstance();\n"
     "#if RELIC_GAME_VERSION <= 16   // " + TAG + "\n"
     "\t\tLOG_DEBUG(\"[rf16] hit this=%p attacker=%u cfg=%u type=%d | avatar=%u | attackee=%u dmg=%.1f | enabled=%d cfgByAvatar=%d byAvatar=%d\",\n"
     "\t\t\t__this, attacker.runtimeID(), attacker.raw() ? attacker.raw()->fields._configID_k__BackingField : 0u, (int)attacker.type(),\n"
     "\t\t\tgame::EntityManager::instance().avatar()->runtimeID(), attackeeRuntimeID, attackResult ? attackResult->fields.damage : -1.0f,\n"
     "\t\t\trapidFire.f_Enabled->enabled() ? 1 : 0, IsConfigByAvatar(attacker) ? 1 : 0, IsAttackByAvatar(attacker) ? 1 : 0);\n"
     "#endif\n\n"
     "\t\tif (!IsConfigByAvatar(attacker) || !IsAttackByAvatar(attacker) || !rapidFire.f_Enabled)\n"),
    (os.path.join(U, "player", "RapidFire.cpp"),
     "\t\tauto originalTarget = manager.entity(attackeeRuntimeID);\n",
     "\t\tauto originalTarget = manager.entity(attackeeRuntimeID);\n"
     "#if RELIC_GAME_VERSION <= 16   // " + TAG + "\n"
     "\t\tLOG_DEBUG(\"[rf16] target=%p type=%d name='%s' filterOK=%d\", originalTarget, originalTarget ? (int)originalTarget->type() : -1,\n"
     "\t\t\toriginalTarget ? originalTarget->name().c_str() : \"\", IsValidByFilter(originalTarget) ? 1 : 0);\n"
     "#endif\n"),
    (os.path.join(U, "player", "RapidFire.cpp"),
     "\t\t\tint attackCount = rapidFire.f_MultiHit->enabled() ? rapidFire.GetAttackCount(__this, entity->runtimeID(), attackResult) : 1;\n",
     "\t\t\tint attackCount = rapidFire.f_MultiHit->enabled() ? rapidFire.GetAttackCount(__this, entity->runtimeID(), attackResult) : 1;\n"
     "#if RELIC_GAME_VERSION <= 16   // " + TAG + "\n"
     "\t\t\tLOG_DEBUG(\"[rf16] fire x%d at %u (multiHit=%d onePunch=%d randomize=%d mult=%d min=%d max=%d combat=%p)\", attackCount, entity->runtimeID(),\n"
     "\t\t\t\trapidFire.f_MultiHit->enabled() ? 1 : 0, rapidFire.f_OnePunch.value() ? 1 : 0, rapidFire.f_Randomize->enabled() ? 1 : 0,\n"
     "\t\t\t\trapidFire.f_Multiplier.value(), rapidFire.f_minMultiplier.value(), rapidFire.f_maxMultiplier.value(), entity->combat());\n"
     "#endif\n"),
]


def rd(p):
    with open(p, "r", encoding="utf-8", newline="") as f:
        return f.read()


def wr(p, s):
    with open(p, "w", encoding="utf-8", newline="") as f:
        f.write(s)


def on():
    bad = 0
    for path, old, new in EDITS:
        s = rd(path)
        nl = "\r\n" if s.count("\r\n") * 2 >= s.count("\n") else "\n"
        o, n = old.replace("\n", nl), new.replace("\n", nl)
        if n in s:
            print("%-16s already on" % os.path.basename(path)); continue
        if o not in s:
            print("%-16s ANCHOR MISSING: %s" % (os.path.basename(path), old.strip().split("\n")[0][:70])); bad += 1; continue
        wr(path, s.replace(o, n, 1))
        print("%-16s diagnostics added" % os.path.basename(path))
    return bad


def off():
    for path in sorted({p for p, _, _ in EDITS}):
        s = rd(path)
        nl = "\r\n" if s.count("\r\n") * 2 >= s.count("\n") else "\n"
        lines, out, i, removed = s.split(nl), [], 0, 0
        while i < len(lines):
            if TAG in lines[i] and lines[i].startswith("#if RELIC_GAME_VERSION <= 16"):
                while i < len(lines) and lines[i].strip() != "#endif":
                    i += 1
                i += 1   # the #endif
                removed += 1
                continue
            out.append(lines[i]); i += 1
        if removed:
            wr(path, nl.join(out)); print("%-16s %d diagnostic block(s) removed" % (os.path.basename(path), removed))
        else:
            print("%-16s nothing to remove" % os.path.basename(path))


if __name__ == "__main__":
    sys.exit(off() if "--off" in sys.argv else on())
