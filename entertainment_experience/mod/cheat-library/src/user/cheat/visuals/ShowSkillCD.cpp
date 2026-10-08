#include "pch-il2cpp.h"
#include "ShowSkillCD.h"

#include <helpers.h>
#include <cheat/events.h>

namespace cheat::feature
{
	ShowSkillCD::ShowSkillCD() : Feature(),
		NF(f_Enabled, "Visuals::ShowSkillCD", false)
	{
		cheat::events::GameUpdateEvent += MY_METHOD_HANDLER(ShowSkillCD::OnGameUpdate);

#if RELIC_GAME_VERSION > 16
		INSTALL_HOOK(app::MoleMole_LCAvatarCombat_SetSkillIndex, ShowSkillCD::MoleMole_LCAvatarCombat_SetSkillIndex_Hook);
		INSTALL_HOOK(app::MoleMole_LCAvatarCombat_CheckCDTimer, ShowSkillCD::MoleMole_LCAvatarCombat_CheckCDTimer_Hook);
#endif   // Relic 1.6: no LCAvatarCombat hooks - the cooldown is read live in OnGameUpdate (ResolveElementalSkill).
		INSTALL_HOOK(app::MonoTeamBtn_SetupView, ShowSkillCD::MonoTeamBtn_SetupView_Hook);
	}

	const FeatureGUIInfo& ShowSkillCD::GetGUIInfo() const
	{
		TRANSLATED_MODULE_INFO("Visuals");
		return info;
	}

	void ShowSkillCD::DrawMain()
	{
		ConfigWidget(_TR("Show Skill Cooldowns"), f_Enabled, _TR("Show skill cooldowns and charges besides team buttons."));
	}

	bool ShowSkillCD::NeedStatusDraw() const
	{
		return f_Enabled;
	}

	void ShowSkillCD::DrawStatus()
	{
		ImGui::Text(_TR("Show Skill CDs"));
	}

	ShowSkillCD& ShowSkillCD::GetInstance()
	{
		static ShowSkillCD instance;
		return instance;
	}

	void ShowSkillCD::SetShowKey(void* key, bool show)
	{
		auto* component = reinterpret_cast<app::Component_1*>(key);
		auto* gameObject = app::Component_1_get_gameObject(component, nullptr);
		app::GameObject_set_active(gameObject, show, nullptr);
	}

	void ShowSkillCD::UpdateSkillMap(app::LCAvatarCombat* lcCombat, app::LCAvatarCombat_LCAvatarCombat_SkillInfo* skillInfo, float cd)
	{
		// Relic: the game assigns LCAvatarCombat._skillDepotConfig only AFTER the per-skill setup calls that get
		// us here (1.6 GEICCKNPBFM @0x01671FD0 writes it at 0x016723A8; 2.8 MABCBAGMELP @0x026F6090 at
		// 0x026F648C), so on BOTH versions the first calls arrive with a null depot config - dereferencing it is
		// an access violation on a game thread.
		if (lcCombat == nullptr || skillInfo == nullptr || lcCombat->fields._skillDepotConfig == nullptr)
			return;

		auto skillDepot = lcCombat->fields._skillDepotConfig->fields;
		uint32_t configID = app::MoleMole_SimpleSafeUInt32_get_Value(skillDepot.idRawNum, nullptr);
		uint32_t skillID = skillInfo->fields.skillID;

		auto& showSkillCD = GetInstance();
		showSkillCD.m_skillDataMap[configID].id = skillID;
		showSkillCD.m_skillDataMap[configID].cd = cd;
	}

	// Relic: where the elemental skill's cooldown and id come from. 2.8 collects them in m_skillDataMap through
	// two LCAvatarCombat hooks; 1.6 has no usable counterpart for either (see appdata-16/il2cpp-functions.h), so
	// it reads the live SkillInfo out of LCAvatarCombat._currSkills[1] - the same accessor the game's own team
	// button uses. Returns false only when the button must be dropped (the avatar left the team); haveData ==
	// false means "nothing to draw this frame" and must NOT drop the button, because the slot is empty for a
	// few frames after the button appears.
	bool ShowSkillCD::ResolveElementalSkill(app::LCAvatarCombat* lcCombat, uint32_t& skillID, float& cd, bool& haveData)
	{
		haveData = false;
		auto* skillDepot = lcCombat->fields._skillDepotConfig;
		if (skillDepot == nullptr)   // the avatar has been removed from the team
			return false;

#if RELIC_GAME_VERSION <= 16
		auto* info = app::MoleMole_LCAvatarCombat_GetSkillInfoByIndex(lcCombat, 1, nullptr);   // 1 = elemental skill
		if (info == nullptr)
			return true;
		skillID = info->fields.skillID;
		cd = app::MoleMole_SafeFloat_get_Value(info->fields.cdTimer, nullptr);
#if RELIC_GAME_VERSION <= 16   // RELIC-DIAG16
		{
			uint32_t depotID = app::MoleMole_SimpleSafeUInt32_get_Value(skillDepot->fields.idRawNum, nullptr);
			auto& slot = GetInstance().m_skillDataMap[depotID];
			if (slot.id != skillID)
			{
				slot.id = skillID;
				LOG_DEBUG("[skillcd16] depot=%u skillID=%u cd=%.2f max=%d curr=%d", depotID, skillID, cd,
					app::MoleMole_LCAvatarCombat_GetSkillMaxChargesCount(lcCombat, skillID, nullptr),
					app::MoleMole_LCAvatarCombat_GetSkillCurrentChargesCount(lcCombat, skillID, nullptr));
			}
		}
#endif
#else
		uint32_t configID = app::MoleMole_SimpleSafeUInt32_get_Value(skillDepot->fields.idRawNum, nullptr);
		auto& skillDataMap = GetInstance().m_skillDataMap;
		skillID = skillDataMap[configID].id;
		cd = skillDataMap[configID].cd;
#endif
		haveData = true;
		return true;
	}

	void ShowSkillCD::OnGameUpdate()
	{
		auto& showSkillCD = GetInstance();
		bool isPS4Layout = app::MoleMole_UIManager_IsPS4Layout(nullptr);

		for (auto it = showSkillCD.m_teamBtnMap.begin(), next_it = it; it != showSkillCD.m_teamBtnMap.end(); it = next_it)
		{
			++next_it;
			uint64_t guid = it->first;
			auto* teamBtn = it->second;
			if (teamBtn->fields._._._._._.m_CachedPtr == nullptr || 
				guid != teamBtn->fields._guid) // clean-up and prevent dupes from updating
			{
				showSkillCD.m_teamBtnMap.erase(it);
				continue;
			}

			SetShowKey(teamBtn->fields._pcKeyGrp, showSkillCD.f_Enabled ? true : !isPS4Layout);
			SetShowKey(app::MonoTeamBtn_get_PS4KeyIcon(teamBtn, nullptr), showSkillCD.f_Enabled ? false : isPS4Layout);

			if (showSkillCD.f_Enabled)
			{
				auto* lcCombat = teamBtn->fields._lcCombat;
				uint32_t skillID = 0;
				float cd = 0.0f;
				bool haveData = false;
				if (lcCombat != nullptr) // is nullptr when changing teams in abyss
				{
					if (ResolveElementalSkill(lcCombat, skillID, cd, haveData))
					{
						if (!haveData)
							continue;   // keep the button registered, there is just nothing to draw yet

						int32_t maxCharge = app::MoleMole_LCAvatarCombat_GetSkillMaxChargesCount(lcCombat, skillID, nullptr);
						int32_t currCharge = app::MoleMole_LCAvatarCombat_GetSkillCurrentChargesCount(lcCombat, skillID, nullptr);

						bool ready = cd <= 0;
						std::string color = ready ? "green" : currCharge > 0 ? "olive" : "maroon";
						std::string cdStr = ready ? "Ready" : std::format("{:.1f}s", cd);
						std::string finalStr = std::format("<color={}>{}\n{:3}/{:<3}</color>", color, cdStr, currCharge, maxCharge);

						app::MonoTeamBtn_set_PCKey(teamBtn, string_to_il2cppi(finalStr), nullptr);
						continue;
					}
				}
				showSkillCD.m_teamBtnMap.erase(it);
			}
			else if (isPS4Layout) // minimize PCKey "blinking" effect in controller UI when feature is disabled
				app::MonoTeamBtn_set_PCKey(teamBtn, nullptr, nullptr);
		}
	}

#if RELIC_GAME_VERSION > 16   // 1.6 installs neither hook - the values are read live in ResolveElementalSkill
	void ShowSkillCD::MoleMole_LCAvatarCombat_SetSkillIndex_Hook(app::LCAvatarCombat* __this, app::LCAvatarCombat_LCAvatarCombat_SkillInfo* skillInfo, int32_t index, int32_t priority, MethodInfo* method)
	{
		CALL_ORIGIN(MoleMole_LCAvatarCombat_SetSkillIndex_Hook, __this, skillInfo, index, priority, method);

		if (index == 1) // elemental skill index
			UpdateSkillMap(__this, skillInfo, 0);
	}

	void ShowSkillCD::MoleMole_LCAvatarCombat_CheckCDTimer_Hook(app::LCAvatarCombat* __this, app::LCAvatarCombat_LCAvatarCombat_SkillInfo* info, MethodInfo* method)
	{
		CALL_ORIGIN(MoleMole_LCAvatarCombat_CheckCDTimer_Hook, __this, info, method);

		if (info->fields.skillIndex == 1) // elemental skill index
		{
			float cd = app::MoleMole_SafeFloat_get_Value(info->fields.cdTimer, nullptr);
			UpdateSkillMap(__this, info, cd);
		}	
	}
#endif

	void ShowSkillCD::MonoTeamBtn_SetupView_Hook(app::MonoTeamBtn* __this, uint64_t guid, MethodInfo* method)
	{
		CALL_ORIGIN(MonoTeamBtn_SetupView_Hook, __this, guid, method);

		auto& showSkillCD = GetInstance();
		showSkillCD.m_teamBtnMap[guid] = __this;
	}
}