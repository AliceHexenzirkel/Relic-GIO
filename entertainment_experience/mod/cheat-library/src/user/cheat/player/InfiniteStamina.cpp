#include "pch-il2cpp.h"
#include "InfiniteStamina.h"

#include <helpers.h>
#include <cheat/events.h>
#include <cheat/game/EntityManager.h>
#include <cheat-base/relic-diag.h>
#include <cheat-base/util.h>

namespace cheat::feature
{
	InfiniteStamina::InfiniteStamina() : Feature(),
		NFP(f_Enabled, "InfiniteStamina", "Infinite Stamina", false),
#if RELIC_GAME_VERSION <= 16
		// Relic: on 1.6 the server spends the stamina itself, from the movement states it is sent (see OnPropertySet), so
		// hiding those states is the only mode that keeps it from running out.
		NF(f_PacketReplacement, "InfiniteStamina", true)
#else
		NF(f_PacketReplacement, "InfiniteStamina", false)
#endif
	{
		INSTALL_HOOK(app::MoleMole_DataItem_HandleNormalProp, DataItem_HandleNormalProp_Hook);
		INSTALL_HOOK(app::VCHumanoidMove_MNKKEGMDFFO, VCHumanoidMove_MNKKEGMDFFO_Hook);
		events::MoveSyncEvent += MY_METHOD_HANDLER(InfiniteStamina::OnMoveSync);
	}

	const FeatureGUIInfo& InfiniteStamina::GetGUIInfo() const
	{
		TRANSLATED_GROUP_INFO("Infinite Stamina", "Player");
		return info;
	}

	void InfiniteStamina::DrawMain()
	{
		ConfigWidget(_TR("Enabled"), f_Enabled, _TR("Enables infinite stamina option."));

		ConfigWidget(_TR("Move Sync Packet Replacement"), f_PacketReplacement,
			_TR("This mode prevents sending server packets with stamina cost actions,\n"
				"e.g. swim, climb, sprint, etc.\n"
				"NOTE: This is may be more safe than the standard method. More testing is needed."));
		// Relic: what the two modes really do on a server that keeps its own stamina.
		if (f_Enabled->enabled() && !f_PacketReplacement)
			ImGui::TextColored(ImColor(255, 165, 0, 255), _TR("Without Move Sync Packet Replacement only the stamina you SEE stays full: "
				"the server keeps spending its own, so whatever the server decides by stamina (swimming, for one) still runs out. "
				"Switch it on for real infinite stamina."));
	}

	bool InfiniteStamina::NeedStatusDraw() const
	{
		return f_Enabled->enabled();
	}

	void InfiniteStamina::DrawStatus()
	{
		ImGui::Text("%s [%s]", _TR("Inf. Stamina"), f_PacketReplacement ? _TR("Packet") : _TR("Normal"));
	}

	InfiniteStamina& InfiniteStamina::GetInstance()
	{
		static InfiniteStamina instance;
		return instance;
	}

	// Infinite stamina offline mode.
	// Note. Changes received from the server (not sure about this for current time),
	//       that means that server know our stamina, and changes it in client can be detected.
	// Not working for water because server sending drown action when your stamina down to zero. (Also guess for now)
	//
	// Relic: upstream BLOCKED the server's stamina updates here, behind an open/close window keyed on the order the three
	// properties arrive in (a TEMPORARY update opened it, a MAX update closed it). Blocking leaves the client holding a
	// stale value, and on game 1.6 that strands it at 0 for good (the save holds a full bar while the client shows 0,
	// even with the feature switched off): the GIO server owns stamina - it spends it from the movement states
	// it is sent (the 1.6 gameserver's Avatar::setIsInDash raises a sprint flag on MotionDash / MotionDangerDash and
	// Avatar::procStaminaInTimer drains every 200 ms while it is set, besides gliding, fast swimming and the skiff) - and it
	// only sends the value when it CHANGES. A client that believes it has 0 never sprints, climbs or charges again, so
	// nothing changes and the stale 0 stays until a relog. So nothing is blocked here: while the normal mode is on, the
	// CURRENT stamina the server reports is raised to the maximum it last reported. That can only leave the client
	// showing MORE than the server, never the deadlock above, and the next real change the server sends puts it right.
	int64_t InfiniteStamina::OnPropertySet(app::PropType__Enum propType, int64_t value)
	{
		using PT = app::PropType__Enum;
		static int64_t s_maxStamina = 0;   // the server's own maximum, in the same hundredths it sends the current value in
		static int64_t s_lastWritten = 0;
		static int s_linesWritten = 0;

		if (propType == PT::PROP_MAX_STAMINA)
			s_maxStamina = value;

		int64_t result = value;
		if (propType == PT::PROP_CUR_PERSIST_STAMINA && f_Enabled->enabled() && !f_PacketReplacement
			&& s_maxStamina > 0 && value < s_maxStamina)
			result = s_maxStamina;

		// Relic: one line per stamina update in relic-diag.txt (the first 60 of a session, then one every 30 s): what the
		// server said and what the client was given - the two numbers that explain a stamina bar stuck at 0.
		if (propType == PT::PROP_MAX_STAMINA || propType == PT::PROP_CUR_PERSIST_STAMINA || propType == PT::PROP_CUR_TEMPORARY_STAMINA)
		{
			int64_t now = util::GetCurrentTimeMillisec();
			if (s_linesWritten < 60 || now - s_lastWritten >= 30000)
			{
				s_linesWritten++;
				s_lastWritten = now;
				char line[200];
				_snprintf_s(line, sizeof(line), _TRUNCATE, "[sta] server %s = %.2f -> client %.2f (infinite: %s)",
					propType == PT::PROP_MAX_STAMINA ? "max" : propType == PT::PROP_CUR_PERSIST_STAMINA ? "current" : "temporary",
					value / 100.0, result / 100.0,
					!f_Enabled->enabled() ? "off" : f_PacketReplacement ? "packet" : "normal");
				relic::diag::append_line(line);
			}
		}
		return result;
	}

	// Infinite stamina packet mode.
	// Note. Blocking packets with movement information, to prevent ability server to know stamina info.
	//       But server may see incorrect movements. What mode safer don't tested.
	void InfiniteStamina::OnMoveSync(uint32_t entityId, app::MotionInfo* syncInfo)
	{
		static bool afterDash = false;

		auto& manager = game::EntityManager::instance();
		auto entity = manager.entity(entityId);
		if (entity->type() == app::EntityType__Enum_1::Vehicle || entity->isAvatar())
		{
			// LOG_DEBUG("Movement packet: %s", magic_enum::enum_name(syncInfo->fields.motionState).data());
			if (f_Enabled->enabled() && f_PacketReplacement)
			{
				auto state = syncInfo->fields.motionState;
				switch (state)
				{
				case app::MotionState__Enum::MotionDash:
				case app::MotionState__Enum::MotionClimb:
				case app::MotionState__Enum::MotionClimbJump:
				case app::MotionState__Enum::MotionStandbyToClimb:
				case app::MotionState__Enum::MotionSwimDash:
				case app::MotionState__Enum::MotionSwimIdle:
				case app::MotionState__Enum::MotionSwimMove:
				case app::MotionState__Enum::MotionSwimJump:
				case app::MotionState__Enum::MotionFly:
				case app::MotionState__Enum::MotionFight:
				case app::MotionState__Enum::MotionDashBeforeShake:
				case app::MotionState__Enum::MotionDangerDash:
					syncInfo->fields.motionState = app::MotionState__Enum::MotionRun;
					break;
				case app::MotionState__Enum::MotionJump:
					if (afterDash)
						syncInfo->fields.motionState = app::MotionState__Enum::MotionRun;
					break;
				case app::MotionState__Enum::MotionSkiffDash:
				case app::MotionState__Enum::MotionSkiffPoweredDash:
					syncInfo->fields.motionState = app::MotionState__Enum::MotionSkiffNormal;
					break;
				}
				if (state != app::MotionState__Enum::MotionJump && state != app::MotionState__Enum::MotionFallOnGround)
					afterDash = state == app::MotionState__Enum::MotionDash;
			}
		}
	}

	void InfiniteStamina::DataItem_HandleNormalProp_Hook(app::DataItem* __this, uint32_t type, int64_t value, app::DataPropOp__Enum state, MethodInfo* method)
	{
		auto& infiniteStamina = GetInstance();

		auto propType = static_cast<app::PropType__Enum>(type);
		value = infiniteStamina.OnPropertySet(propType, value);   // Relic: adjusted, never dropped - see OnPropertySet
		CALL_ORIGIN(DataItem_HandleNormalProp_Hook, __this, type, value, state, method);
	}

	// Infinite Stamina for Wanderer alone.
	void InfiniteStamina::VCHumanoidMove_MNKKEGMDFFO_Hook(app::VCHumanoidMove* __this, float JJJEOEHLNGP, MethodInfo* method)
	{
		auto& infiniteStamina = GetInstance();

		if (infiniteStamina.f_Enabled || infiniteStamina.f_PacketReplacement)
			JJJEOEHLNGP = 0.f;

		CALL_ORIGIN(VCHumanoidMove_MNKKEGMDFFO_Hook, __this, JJJEOEHLNGP, method);
	}
}

