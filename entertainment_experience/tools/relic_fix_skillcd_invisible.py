#!/usr/bin/env python3
"""Applies these fixes for game 1.6 (idempotent):

  ShowSkillCD  - 1.6 has no usable counterpart of the two LCAvatarCombat hooks upstream relies on, so the label
                 never gets a real cooldown and one hook reads a SkillInfo out of a 0x20-byte event object
                 (0xC0000005). On 1.6 the values are read live from LCAvatarCombat._currSkills[1]
                 instead, through the accessor the game's own team button uses.
  UpdateSkillMap - the game assigns _skillDepotConfig only AFTER the per-skill setup calls, on BOTH versions, so
                 dereferencing it in the hook is an access violation on a game thread. Guarded ungated (2.8 fix).
  FreeCamera   - "Make Character invisible" calls MoleMole.Miscs.SetUILocalAvatarVisible, which does not exist in
                 1.6 (added later). 1.6 ships both halves of that method's body as instance methods, so the 1.6
                 branch calls them directly on the local avatar entity.

Every behaviour change is either in appdata-16 (1.6-only by construction) or behind RELIC_GAME_VERSION.
"""
import os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.normpath(os.path.join(HERE, "..", "mod", "cheat-library", "src"))
U = os.path.join(SRC, "user", "cheat")

EDITS = []


def edit(path, old, new, tag):
    EDITS.append((path, old, new, tag))


# ---------------------------------------------------------------- appdata-16: the 1.6 accessor + the wrong RVA
edit(os.path.join(SRC, "appdata-16", "il2cpp-functions.h"),
     "DO_APP_FUNC(0x01673E90, void, MoleMole_LCAvatarCombat_CheckCDTimer, (LCAvatarCombat* __this, LCAvatarCombat_LCAvatarCombat_SkillInfo* info, MethodInfo* method));",
     "// RELIC hand-fix (do not let a regeneration overwrite this): the matcher picked 0x01673E90 =\n"
     "// DIIAFDKMCEE$$JJJBFMBJHPG(HKIEEJMCECJ), which is the melee/ranged targeting-bucket rebuild - HKIEEJMCECJ has\n"
     "// fields only up to 0x1C, so reading SkillInfo.skillIndex at +0x108 and cdTimer at +0x20 was out of object\n"
     "// (the 0xC0000005 in the 1.6 log). The real counterpart of 2.8 AADPEKIMEML @0x026EEF50 is\n"
     "// DIIAFDKMCEE$$LBBIBLMOFFL @0x016749A0: same two callers (the CD tick GGNKBOAJDAH @0x016724A0 and\n"
     "// IPLLMGJNJMA @0x01673810, twins of 2.8 NNFHBMAPCBN / BNIBNADAPEA), same body (cd < 0 guard, then the\n"
     "// ready-notify through this+0x160). 1.6 passes the _skillInfoMap key as a LEADING uint that 2.8 reads out of\n"
     "// the SkillInfo - the extra parameter is deliberate, so any attempt to re-install this hook on 1.6 fails to\n"
     "// compile instead of hooking a differently-shaped method (ShowSkillCD does not install it on 1.6 at all).\n"
     "DO_APP_FUNC(0x016749A0, void, MoleMole_LCAvatarCombat_CheckCDTimer, (LCAvatarCombat* __this, uint32_t skillID, LCAvatarCombat_LCAvatarCombat_SkillInfo* info, MethodInfo* method));  // 1.6: hand-verified DIIAFDKMCEE$$LBBIBLMOFFL",
     "appdata-16 CheckCDTimer RVA")

edit(os.path.join(SRC, "appdata-16", "il2cpp-functions.h"),
     "DO_APP_FUNC(0x0334B490, void, MonoTeamBtn_SetupView,",
     "// RELIC hand-added (1.6): DIIAFDKMCEE$$KCNMJMDPCEI(uint) returns LCAvatarCombat._currSkills[index] - the\n"
     "// 6-element SkillInfo array at +0x1D0, filled by GIABONGEFCF(0..5) during the avatar's skill setup. Index 1\n"
     "// is the elemental skill. Bounds-checked by the game (returns null when the index is out of range), so 1.6\n"
     "// can read the live cooldown without hooking anything. ABI pinned by the game's own caller\n"
     "// MonoTeamBtn$$OIAFAGIHDNN @0x0334A930: (this, index, MethodInfo = null).\n"
     "DO_APP_FUNC(0x016745B0, LCAvatarCombat_LCAvatarCombat_SkillInfo*, MoleMole_LCAvatarCombat_GetSkillInfoByIndex, (LCAvatarCombat* __this, uint32_t index, MethodInfo* method));  // 1.6: hand-verified DIIAFDKMCEE$$KCNMJMDPCEI\n"
     "\n"
     "DO_APP_FUNC(0x0334B490, void, MonoTeamBtn_SetupView,",
     "appdata-16 GetSkillInfoByIndex")

# the two halves of 2.8's Miscs.SetUILocalAvatarVisible, which 1.6 ships as plain instance methods
edit(os.path.join(SRC, "appdata-16", "il2cpp-functions.h"),
     "DO_APP_FUNC(0x0, void, Miscs_SetUILocalAvatarVisible, (bool visible, MethodInfo* method));",
     "DO_APP_FUNC(0x0, void, Miscs_SetUILocalAvatarVisible, (bool visible, MethodInfo* method));",
     "keep the 0x0 Miscs entry")

edit(os.path.join(SRC, "appdata-16", "il2cpp-functions.h"),
     "DO_APP_FUNC(0x016745B0, LCAvatarCombat_LCAvatarCombat_SkillInfo*, MoleMole_LCAvatarCombat_GetSkillInfoByIndex, (LCAvatarCombat* __this, uint32_t index, MethodInfo* method));  // 1.6: hand-verified DIIAFDKMCEE$$KCNMJMDPCEI\n",
     "DO_APP_FUNC(0x016745B0, LCAvatarCombat_LCAvatarCombat_SkillInfo*, MoleMole_LCAvatarCombat_GetSkillInfoByIndex, (LCAvatarCombat* __this, uint32_t index, MethodInfo* method));  // 1.6: hand-verified DIIAFDKMCEE$$KCNMJMDPCEI\n"
     "\n"
     "// RELIC hand-added (1.6): MoleMole.Miscs.SetUILocalAvatarVisible does not exist in 1.6 (it was added later -\n"
     "// the whole 1.6 Miscs class is unobfuscated and has no such member), so the entry above stays 0x0. 2.8's body\n"
     "// is just  localAvatar.GetRendererComponent().SetRendererVisible(v, 14, true)  and 1.6 ships both halves as\n"
     "// ordinary INSTANCE methods (no dummy this, no static thunk). The game itself calls them back to back in\n"
     "// InteractionManager$$ResumeAvatarVisibleSet @0x01CA2810, which also proves MethodInfo may be null.\n"
     "// The 'reason' is a bit index in a SimpleFixedBitStack initialised all-ones (visible = every bit set), so\n"
     "// clearing one bit hides; 14 is the slot 2.8's Miscs uses and no 1.6 game code ever passes it.\n"
     "DO_APP_FUNC(0x00CF5100, /*MoleMole_BaseEntityRendererComponent*/ void*, MoleMole_BaseEntity_GetRendererComponent, (BaseEntity* __this, MethodInfo* method));  // 1.6: hand-verified DNPPAIELOMJ$$MKIHLPLFIBI\n"
     "DO_APP_FUNC(0x030E9970, void, MoleMole_BaseEntityRendererComponent_SetRendererVisible, (/*MoleMole_BaseEntityRendererComponent*/ void* __this, bool visible, int32_t reason, bool immediate, MethodInfo* method));  // 1.6: hand-verified HFCOBKMKNLN$$JLBIFIBAALA\n",
     "appdata-16 renderer visibility pair")

# ---------------------------------------------------------------- appdata-28 mirrors (so a regeneration keeps the names)
edit(os.path.join(SRC, "appdata-28", "il2cpp-functions.h"),
     "DO_APP_FUNC(0x0334B490, void, MonoTeamBtn_SetupView,",
     "DO_APP_FUNC(0x0334B490, void, MonoTeamBtn_SetupView,",
     "appdata-28 anchor probe")

edit(os.path.join(U, "visuals", "ShowSkillCD.cpp"),
     "\t\tINSTALL_HOOK(app::MoleMole_LCAvatarCombat_SetSkillIndex, ShowSkillCD::MoleMole_LCAvatarCombat_SetSkillIndex_Hook);\n"
     "\t\tINSTALL_HOOK(app::MoleMole_LCAvatarCombat_CheckCDTimer, ShowSkillCD::MoleMole_LCAvatarCombat_CheckCDTimer_Hook);\n",
     "#if RELIC_GAME_VERSION > 16\n"
     "\t\tINSTALL_HOOK(app::MoleMole_LCAvatarCombat_SetSkillIndex, ShowSkillCD::MoleMole_LCAvatarCombat_SetSkillIndex_Hook);\n"
     "\t\tINSTALL_HOOK(app::MoleMole_LCAvatarCombat_CheckCDTimer, ShowSkillCD::MoleMole_LCAvatarCombat_CheckCDTimer_Hook);\n"
     "#endif   // Relic 1.6: no LCAvatarCombat hooks - the cooldown is read live in OnGameUpdate (ResolveElementalSkill).\n",
     "ShowSkillCD hooks gated")

# UpdateSkillMap: guard the depot config (BOTH versions - the game assigns it after the setup calls)
edit(os.path.join(U, "visuals", "ShowSkillCD.cpp"),
     "\t\tauto skillDepot = lcCombat->fields._skillDepotConfig->fields;\n"
     "\t\tuint32_t configID = app::MoleMole_SimpleSafeUInt32_get_Value(skillDepot.idRawNum, nullptr);\n"
     "\t\tuint32_t skillID = skillInfo->fields.skillID;\n",
     "\t\t// Relic: the game assigns LCAvatarCombat._skillDepotConfig only AFTER the per-skill setup calls that get\n"
     "\t\t// us here (1.6 GEICCKNPBFM @0x01671FD0 writes it at 0x016723A8; 2.8 MABCBAGMELP @0x026F6090 at\n"
     "\t\t// 0x026F648C), so on BOTH versions the first calls arrive with a null depot config - dereferencing it is\n"
     "\t\t// an access violation on a game thread.\n"
     "\t\tif (lcCombat == nullptr || skillInfo == nullptr || lcCombat->fields._skillDepotConfig == nullptr)\n"
     "\t\t\treturn;\n"
     "\n"
     "\t\tauto skillDepot = lcCombat->fields._skillDepotConfig->fields;\n"
     "\t\tuint32_t configID = app::MoleMole_SimpleSafeUInt32_get_Value(skillDepot.idRawNum, nullptr);\n"
     "\t\tuint32_t skillID = skillInfo->fields.skillID;\n",
     "UpdateSkillMap depot guard")

# the resolver: 1.6 reads the live SkillInfo, 2.8 keeps the hook-fed map
edit(os.path.join(U, "visuals", "ShowSkillCD.cpp"),
     "\tvoid ShowSkillCD::OnGameUpdate()\n",
     "\t// Relic: where the elemental skill's cooldown and id come from. 2.8 collects them in m_skillDataMap through\n"
     "\t// two LCAvatarCombat hooks; 1.6 has no usable counterpart for either (see appdata-16/il2cpp-functions.h), so\n"
     "\t// it reads the live SkillInfo out of LCAvatarCombat._currSkills[1] - the same accessor the game's own team\n"
     "\t// button uses. Returns false only when the button must be dropped (the avatar left the team); haveData ==\n"
     "\t// false means \"nothing to draw this frame\" and must NOT drop the button, because the slot is empty for a\n"
     "\t// few frames after the button appears.\n"
     "\tbool ShowSkillCD::ResolveElementalSkill(app::LCAvatarCombat* lcCombat, uint32_t& skillID, float& cd, bool& haveData)\n"
     "\t{\n"
     "\t\thaveData = false;\n"
     "\t\tauto* skillDepot = lcCombat->fields._skillDepotConfig;\n"
     "\t\tif (skillDepot == nullptr)   // the avatar has been removed from the team\n"
     "\t\t\treturn false;\n"
     "\n"
     "#if RELIC_GAME_VERSION <= 16\n"
     "\t\tauto* info = app::MoleMole_LCAvatarCombat_GetSkillInfoByIndex(lcCombat, 1, nullptr);   // 1 = elemental skill\n"
     "\t\tif (info == nullptr)\n"
     "\t\t\treturn true;\n"
     "\t\tskillID = info->fields.skillID;\n"
     "\t\tcd = app::MoleMole_SafeFloat_get_Value(info->fields.cdTimer, nullptr);\n"
     "#else\n"
     "\t\tuint32_t configID = app::MoleMole_SimpleSafeUInt32_get_Value(skillDepot->fields.idRawNum, nullptr);\n"
     "\t\tauto& skillDataMap = GetInstance().m_skillDataMap;\n"
     "\t\tskillID = skillDataMap[configID].id;\n"
     "\t\tcd = skillDataMap[configID].cd;\n"
     "#endif\n"
     "\t\thaveData = true;\n"
     "\t\treturn true;\n"
     "\t}\n"
     "\n"
     "\tvoid ShowSkillCD::OnGameUpdate()\n",
     "ResolveElementalSkill")

edit(os.path.join(U, "visuals", "ShowSkillCD.cpp"),
     "\t\t\tif (showSkillCD.f_Enabled)\n"
     "\t\t\t{\n"
     "\t\t\t\tauto* lcCombat = teamBtn->fields._lcCombat;\n"
     "\t\t\t\tif (lcCombat != nullptr) // is nullptr when changing teams in abyss\n"
     "\t\t\t\t{\n"
     "\t\t\t\t\tauto* skillDepot = lcCombat->fields._skillDepotConfig;\n"
     "\t\t\t\t\tif (skillDepot != nullptr) // is nullptr when avatar has been removed from team\n"
     "\t\t\t\t\t{\n"
     "\t\t\t\t\t\tuint32_t configID = app::MoleMole_SimpleSafeUInt32_get_Value(skillDepot->fields.idRawNum, nullptr);\n"
     "\t\t\t\t\t\tuint32_t skillID = showSkillCD.m_skillDataMap[configID].id;\n"
     "\n"
     "\t\t\t\t\t\tfloat cd = showSkillCD.m_skillDataMap[configID].cd;\n"
     "\t\t\t\t\t\tint32_t maxCharge",
     "\t\t\tif (showSkillCD.f_Enabled)\n"
     "\t\t\t{\n"
     "\t\t\t\tauto* lcCombat = teamBtn->fields._lcCombat;\n"
     "\t\t\t\tuint32_t skillID = 0;\n"
     "\t\t\t\tfloat cd = 0.0f;\n"
     "\t\t\t\tbool haveData = false;\n"
     "\t\t\t\tif (lcCombat != nullptr) // is nullptr when changing teams in abyss\n"
     "\t\t\t\t{\n"
     "\t\t\t\t\tif (ResolveElementalSkill(lcCombat, skillID, cd, haveData))\n"
     "\t\t\t\t\t{\n"
     "\t\t\t\t\t\tif (!haveData)\n"
     "\t\t\t\t\t\t\tcontinue;   // keep the button registered, there is just nothing to draw yet\n"
     "\n"
     "\t\t\t\t\t\tint32_t maxCharge",
     "OnGameUpdate resolver")

EDITS.append((os.path.join(U, "visuals", "ShowSkillCD.h"),
              "\t\tstatic void UpdateSkillMap(app::LCAvatarCombat* lcCombat, app::LCAvatarCombat_LCAvatarCombat_SkillInfo* skillInfo, float cd);\n",
              "\t\tstatic void UpdateSkillMap(app::LCAvatarCombat* lcCombat, app::LCAvatarCombat_LCAvatarCombat_SkillInfo* skillInfo, float cd);\n"
              "\t\t// false = drop the button (the avatar left the team); haveData = false means nothing to draw yet.\n"
              "\t\tstatic bool ResolveElementalSkill(app::LCAvatarCombat* lcCombat, uint32_t& skillID, float& cd, bool& haveData);\n",
              "ShowSkillCD.h resolver decl"))

EDITS.append((os.path.join(U, "visuals", "ShowSkillCD.h"),
              "\t\tstatic void MoleMole_LCAvatarCombat_SetSkillIndex_Hook(app::LCAvatarCombat* __this, app::LCAvatarCombat_LCAvatarCombat_SkillInfo* skillInfo, int32_t index, int32_t priority, MethodInfo* method);\n"
              "\t\tstatic void MoleMole_LCAvatarCombat_CheckCDTimer_Hook(app::LCAvatarCombat* __this, app::LCAvatarCombat_LCAvatarCombat_SkillInfo* info, MethodInfo* method);\n",
              "#if RELIC_GAME_VERSION > 16   // 1.6 has no usable counterpart for either - see ShowSkillCD.cpp\n"
              "\t\tstatic void MoleMole_LCAvatarCombat_SetSkillIndex_Hook(app::LCAvatarCombat* __this, app::LCAvatarCombat_LCAvatarCombat_SkillInfo* skillInfo, int32_t index, int32_t priority, MethodInfo* method);\n"
              "\t\tstatic void MoleMole_LCAvatarCombat_CheckCDTimer_Hook(app::LCAvatarCombat* __this, app::LCAvatarCombat_LCAvatarCombat_SkillInfo* info, MethodInfo* method);\n"
              "#endif\n",
              "ShowSkillCD.h hooks gated"))


def rd(p):
    with open(p, "r", encoding="utf-8", newline="") as f:
        return f.read()


def wr(p, s):
    with open(p, "w", encoding="utf-8", newline="") as f:
        f.write(s)


def main():
    bad = 0
    for path, old, new, tag in EDITS:
        s = rd(path)
        nl = "\r\n" if s.count("\r\n") * 2 >= s.count("\n") else "\n"
        o, n = old.replace("\n", nl), new.replace("\n", nl)
        if o == n:
            print("%-34s %s" % (tag, "present" if o in s else "MISSING"))
            bad += 0 if o in s else 1
            continue
        if n in s:
            print("%-34s already applied" % tag); continue
        if o not in s:
            print("%-34s ANCHOR MISSING" % tag); bad += 1; continue
        wr(path, s.replace(o, n, 1))
        print("%-34s applied" % tag)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
