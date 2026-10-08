#include "pch-il2cpp.h"
#include "RapidFire.h"

#include <helpers.h>
#include <cheat/events.h>
#include <cheat/game/EntityManager.h>
#include <cheat/game/util.h>
#include <cheat/game/filters.h>
#include <cheat-base/relic-guard.h>
#include <cheat-base/relic-diag.h>
#include <cheat-base/util.h>

#include <atomic>
#include <mutex>

namespace cheat::feature
{
	//static void LCBaseCombat_DoHitEntity_Hook(app::LCBaseCombat* __this, uint32_t targetID, app::AttackResult* attackResult, bool ignoreCheckCanBeHitInMP, MethodInfo* method);
	static void VCAnimatorEvent_HandleProcessItem_Hook(app::MoleMole_VCAnimatorEvent* __this,
		app::MoleMole_VCAnimatorEvent_MoleMole_VCAnimatorEvent_AnimatorEventPatternProcessItem* processItem,
		app::AnimatorStateInfo processStateInfo, app::MoleMole_VCAnimatorEvent_MoleMole_VCAnimatorEvent_TriggerMode__Enum mode, MethodInfo* method);
	static void LCBaseCombat_FireBeingHitEvent_Hook(app::LCBaseCombat* __this, uint32_t attackeeRuntimeID, app::AttackResult* attackResult, MethodInfo* method);

	static int32_t attackTags[] = {
		1638193991, // Normal and Charged
		1498431743, // Plunge
		-584054938, 0, // Skill, Burst and Charged Bow Release
		// Relic: the game's own avatar tag table (Animator.StringToHash of every AVATAR_* state tag, built at
		// startup by DCBBFCGFEHN @0x029837F0 on 1.6 and DNDKKFCIBMN @0x02184290 on 2.8) has two more attack tags
		// than the three above. The hashes are CRC32 of the tag name, so they are the same on every version.
		222085953,  // AVATAR_NORMAL_ATTACK
		379396322,  // AVATAR_ATTACK
	};

	// Relic: names for the diagnostics, from the same tag table (only the avatar states worth telling apart).
	struct AvatarTagName { int32_t hash; const char* name; };
	static const AvatarTagName kAvatarTagNames[] = {
		{ 0, "(no tag)" },
		{ 1638193991, "MOVE_ATTACK" }, { 1498431743, "AIR_ATTACK" }, { -584054938, "SKILL" },
		{ 222085953, "NORMAL_ATTACK" }, { 379396322, "ATTACK" },
		{ 37905186, "IDLE" }, { 1408740450, "WALK" }, { -1925294579, "RUN" }, { -1092790162, "SPRINT" },
		{ 1286963291, "RUN_TO_WALK" }, { -723052833, "WALK_TO_IDLE" }, { 405789846, "SPRINT_TO_RUN" },
		{ -2105432459, "RUN_TO_SPRINT" }, { 1695168612, "SPRINT_TO_IDLE" }, { -45810410, "BACK_STEP" },
		{ 2044055585, "JUMP" }, { -1919073153, "DROP_DOWN" }, { -1791079439, "DROP_DOWN_LIT" },
		{ 1197889276, "FALL_ON_GROUND" }, { -198959937, "FALL_ON_GROUND_RUN" }, { -1879888740, "FALL_ON_GROUND_SPRINT" },
		{ -559631333, "CLIMB" }, { -209827127, "CLIMB_IDLE" }, { -1997932086, "CLIMB_JUMP" },
		{ -1899859586, "FLY" }, { 543215210, "FLY_START" }, { 295658738, "SWIM_IDLE" }, { 579200671, "SWIM_MOVE" },
		{ 1456803047, "SWIM_DASH" }, { 1704435325, "TURN_DIR" }, { 1965333223, "RUNNING_TURN" },
		{ -1683381331, "PUT_AWAY" }, { 1278862852, "PUT_AWAY_OVER" }, { -1294924809, "HIT_AIR" },
		{ 309714134, "HIT_H" }, { 353900751, "HIT_L" }, { -466612966, "DIE" }, { 1541947483, "COMMON_NEED_SYNC_ANI" },
	};

	static const char* AvatarTagName(int32_t tag)
	{
		for (const auto& entry : kAvatarTagNames)
			if (entry.hash == tag)
				return entry.name;
		return nullptr;
	}

	// "NAME=count" per tag (the raw hash for one the table does not name), in hash order.
	static std::string FormatAvatarTags(const std::map<int32_t, uint32_t>& tags, const char* separator)
	{
		std::string out;
		for (const auto& [tag, count] : tags)
		{
			char entry[64];
			const char* name = AvatarTagName(tag);
			if (name != nullptr)
				_snprintf_s(entry, sizeof(entry), _TRUNCATE, "%s%s=%u", out.empty() ? "" : separator, name, count);
			else
				_snprintf_s(entry, sizeof(entry), _TRUNCATE, "%s#%d=%u", out.empty() ? "" : separator, tag, count);
			out += entry;
		}
		return out;
	}

	static std::map<RapidFire::ElementType, app::ElementType__Enum> elementType
	{
		{ RapidFire::ElementType::None, app::ElementType__Enum::None },
		{ RapidFire::ElementType::Pyro, app::ElementType__Enum::Fire },
		{ RapidFire::ElementType::Hydro, app::ElementType__Enum::Water },
		{ RapidFire::ElementType::Dendro, app::ElementType__Enum::Grass },
		{ RapidFire::ElementType::Electro, app::ElementType__Enum::Electric },
		{ RapidFire::ElementType::Cryo, app::ElementType__Enum::Ice },
		{ RapidFire::ElementType::Frozen, app::ElementType__Enum::Frozen },
		{ RapidFire::ElementType::Anemo, app::ElementType__Enum::Wind },
		{ RapidFire::ElementType::Geo, app::ElementType__Enum::Rock },
		{ RapidFire::ElementType::AntiFire, app::ElementType__Enum::AntiFire },
		{ RapidFire::ElementType::VehicleMuteIce, app::ElementType__Enum::VehicleMuteIce },
		{ RapidFire::ElementType::Unknown, app::ElementType__Enum::COUNT }
	};

	// Relic: what the two attack hooks really saw this session. Every Attack Effects mode is a hook body the game
	// may never reach (a hotfix that replaced the caller), a gate that may never open (the entity checks) or a
	// write the game may undo (animator speed, a hit the server does not count) - and from the outside all of
	// those look the same: the mode does nothing. The counters say which one it is. The game thread writes them, the
	// menu reads them from the render thread, hence the atomics; the strings are behind the mutex.
	namespace
	{
		struct AttackDiag
		{
			std::atomic<uint32_t> animCalls{ 0 };       // HandleProcessItem calls, every entity
			std::atomic<uint32_t> animAvatar{ 0 };      // ... on the active character's own animator
			std::atomic<uint32_t> animAttack{ 0 };      // ... in a state whose tag is one of attackTags
			std::atomic<uint32_t> speedSet{ 0 };        // Attack Speed wrote the multiplier
			std::atomic<uint32_t> speedRestored{ 0 };   // ... and put the normal speed back
			std::atomic<uint32_t> speedFixups{ 0 };     // a frame started with the speed changed back; re-applied
			std::atomic<float> speedAfterSet{ -1.0f };  // Animator.speed read back right after our write
			std::atomic<float> speedAtUpdate{ -1.0f };  // ... and at the start of the next frame, before a re-apply

			std::atomic<uint32_t> hitCalls{ 0 };        // FireBeingHitEvent calls, every attacker
			std::atomic<uint32_t> hitByAvatar{ 0 };     // ... that passed the "our character's attack" gate
			std::atomic<uint32_t> hitTarget{ 0 };       // ... on a target the filters accept
			std::atomic<uint32_t> hitExtra{ 0 };        // extra FireBeingHitEvent calls made by Repeat
			std::atomic<float> lastDamage{ -1.0f };     // the game's own damage number of the last hit
			std::atomic<float> lastSent{ -1.0f };       // ... and what the hit carried after the mod

			std::mutex lock;                            // everything below
			std::map<int32_t, uint32_t> avatarTags;     // m_Tag -> times seen, the character's own items only
			std::string lastProbe;                      // the last HP probe, as written to relic-diag.txt
			std::string hotfix;                         // 1.6: which methods on these paths a hotfix replaced
		};
		AttackDiag s_diag;

		// One hit at a time is followed: its target's HP when the hit was handed to the game, and again once the
		// queued event, the server round trip and the HP notify are long done. Game thread only.
		struct HitProbe
		{
			uint32_t id = 0;
			float hpBefore = 0.0f;
			float gameDamage = 0.0f;   // what the game computed for one hit
			float sent = 0.0f;         // what one hit carried after the mod
			int hits = 1;              // how many times it went out
			const char* method = "";
			int64_t due = 0;
		};
		HitProbe s_probe;
		int s_probesWritten = 0;
		constexpr int kProbeLineLimit = 40;      // per session in relic-diag.txt; the menu always shows the last
		constexpr int64_t kProbeDelayMs = 1500;

		// Attack Speed state. Game thread only.
		uint32_t s_fastAvatarID = 0;             // the character whose animator we sped up
		int64_t s_lastAttackMs = 0;              // last time one of its animator items was in an attack state
		// The hook runs once per animator item, per layer: a non-attack item of another layer in the same frame
		// would put the speed straight back (upstream reset on the first such call). Only a character that has
		// not been in an attack state for this long is back to normal.
		constexpr int64_t kAttackSpeedHoldMs = 250;
	}

	RapidFire::RapidFire() : Feature(),
		NFP(f_Enabled, "RapidFire", "Attack Effects", false),
		NFP(f_MultiHit, "RapidFire", "Multi-Hit", false),
		NF(f_Multiplier, "RapidFire", 2),
		NF(f_OnePunch, "RapidFire", false),
		NFP(f_Randomize, "RapidFire", "Randomize", false),
		NF(f_minMultiplier, "RapidFire", 1),
		NF(f_maxMultiplier, "RapidFire", 3),
		NFP(f_MultiTarget, "RapidFire", "Multi-Target", false),
		NF(f_MultiTargetRadius, "RapidFire", 20.0f),
		NFP(f_MultiAnimation, "RapidFire", "Multi-Animation", false),
		NF(f_AnimationMultiplier, "RapidFire", 100),
		NF(f_AnimationState, "RapidFire", 0.5f),
		NFP(f_AttackSpeed, "RapidFire", "Attack speed", false),
		NF(f_SpeedMultiplier, "RapidFire", 1.5f),
		NFP(f_CustomElement, "RapidFire", "CustomElement", false),
		NF(f_ElementType, "RapidFire", RapidFire::ElementType::Electro),
		NF(f_isRandType, "RapodFire", false),
		NF(f_HeadShot, "RapodFire", false),
		animationCounter(1)
	{
		// INSTALL_HOOK(app::MoleMole_LCBaseCombat_DoHitEntity, LCBaseCombat_DoHitEntity_Hook); -- Looks like FireBeingHitEvent is superior to this.
		INSTALL_HOOK(app::MoleMole_VCAnimatorEvent_HandleProcessItem, VCAnimatorEvent_HandleProcessItem_Hook);
		INSTALL_HOOK(app::MoleMole_LCBaseCombat_FireBeingHitEvent, LCBaseCombat_FireBeingHitEvent_Hook);
		events::GameUpdateEvent += MY_METHOD_HANDLER(RapidFire::OnGameUpdate);
	}

	const FeatureGUIInfo& RapidFire::GetGUIInfo() const
	{
		TRANSLATED_GROUP_INFO("Attack Effects", "Player");
		return info;
	}

	void RapidFire::DrawMain()
	{
		ConfigWidget(_TR("Enabled"), f_Enabled, _TR("Enables attack multipliers. Need to choose a mode to work."));
		ImGui::SameLine();
		ImGui::TextColored(ImColor(255, 165, 0, 255), _TR("Choose any or both modes below."));
		// Relic: the switch alone changes nothing, and the modes under it are just as quiet without it.
		if (f_Enabled->enabled() && !f_MultiHit->enabled() && !f_MultiTarget->enabled())
			ImGui::TextColored(ImColor(255, 165, 0, 255), _TR("Nothing is multiplied yet: switch on Multi-Hit Mode and/or Multi-Target."));
		else if (!f_Enabled->enabled() && (f_MultiHit->enabled() || f_MultiTarget->enabled()))
			ImGui::TextColored(ImColor(255, 165, 0, 255), _TR("Multi-Hit and Multi-Target only work while Enabled is on."));

		ConfigWidget(_TR("Multi-Hit Mode"), f_MultiHit, _TR("Enables multi-hit.\n" \
			"Multiplies your attack count.\n" \
			"This is not well tested, and can be detected by anticheat.\n" \
			"Not recommended to be used with main accounts or used with high values.\n"));

		ImGui::Indent();

		ConfigWidget(_TR("One-Punch Mode"), f_OnePunch, _TR("Calculate how many attacks needed to kill an enemy based on their HP\n" \
			"and uses that to set the multiplier accordingly.\n" \
			"May be safer, but multiplier calculation may not be on-point."));

		ConfigWidget(_TR("Randomize Multiplier"), f_Randomize, _TR("Randomize multiplier between min and max multiplier."));
		ImGui::SameLine();
		ImGui::TextColored(ImColor(255, 165, 0, 255), _TR("This will override One-Punch Mode!"));

		if (!f_OnePunch) {
			if (!f_Randomize)
			{
				ConfigWidget(_TR("Multiplier"), f_Multiplier, 1, 2, 1000, _TR("Attack count multiplier."));
			}
			else
			{
				ConfigWidget(_TR("Min Multiplier"), f_minMultiplier, 1, 1, 1000, _TR("Attack count minimum multiplier."));
				ConfigWidget(_TR("Max Multiplier"), f_maxMultiplier, 1, 2, 1000, _TR("Attack count maximum multiplier."));
			}
		}

		ImGui::Unindent();

		ConfigWidget(_TR("Multi-Target"), f_MultiTarget, _TR("Enables multi-target attacks within specified radius of target.\n" \
			"All valid targets around initial target will be hit based on setting.\n" \
			"Damage numbers will only appear on initial target but all valid targets are damaged.\n" \
			"If multi-hit is off and there are still multiple numbers on a single target, check the Entity Manager in the Debug section to see if there are invisible entities.\n" \
			"This can cause EXTREME lag and quick bans if used with multi-hit. You are warned.")
		);

		ImGui::Indent();
		ConfigWidget(_TR("Radius (m)"), f_MultiTargetRadius, 0.1f, 5.0f, 50.0f, _TR("Radius to check for valid targets."));
		ImGui::Unindent();

		ConfigWidget(_TR("Multi-Animation"), f_MultiAnimation, _TR("Enables multi-animation attacks.\n" \
			"Do keep in mind that the character's audio will also be spammed."));
		ConfigWidget(_TR("Animation Multiplier"), f_AnimationMultiplier, 1, 1, 150, _TR("Configure to how many times it will update the animation state.\n" \
			"Results can vary alongside Animation State"));
		ConfigWidget(_TR("Animation State"), f_AnimationState, 0.01f, 0.f, 2.f, _TR("Animation state to replay.\n"\
			"Results can vary alongside Animation Multiplier"));
		ConfigWidget(_TR("Attack Speed"), f_AttackSpeed, _TR("Enables fast animation attacks.\n"));
		ConfigWidget(_TR("Speed Multiplier"), f_SpeedMultiplier, 0.1f, 1.0f, 5.0f, _TR("Attack speed multiplier."));

		ConfigWidget(_TR("Custom Element"), f_CustomElement, _TR("Allows you to customize the element type of damage.\n" \
			"This may not work for some characters. \n" \
			"(If you want to completely close this function, you need to reload the scene, e.g. entering the dungeon or re-entering the game)"));
		ImGui::Indent();
		ConfigWidget(_TR("RandomElementType"), f_isRandType, _TR("Random element type (excluding physics)"));
		ConfigWidget(_TR("ElementType"), f_ElementType, _TR("ElementTypes"));
		ImGui::Unindent();
		ConfigWidget(_TR("Auto weakspot"), f_HeadShot, _TR("Only bow character."));

		DrawDiagnostics();
	}

	bool RapidFire::NeedStatusDraw() const
	{
		return (f_Enabled->enabled() && (f_MultiHit->enabled() || f_MultiTarget->enabled())) || f_MultiAnimation->enabled() || f_AttackSpeed->enabled() || f_CustomElement->enabled();
	}

	void RapidFire::DrawStatus()
	{
		if (f_Enabled->enabled())
		{
			ImGui::Text(_TR("Attack Effects"));
			if (f_MultiHit->enabled())
			{
				if (f_Randomize->enabled())
					ImGui::Text("%s [%d|%d]", _TR("Multi-Hit Random"), f_minMultiplier.value(), f_maxMultiplier.value());
				else if (f_OnePunch)
					ImGui::Text(_TR("Multi-Hit [OnePunch]"));
				else
					ImGui::Text("%s [%d]", _TR("Multi-Hit"), f_Multiplier.value());
			}
			if (f_MultiTarget->enabled())
				ImGui::Text("%s [%.01fm]", _TR("Multi-Target"), f_MultiTargetRadius.value());
		}

		if (f_MultiAnimation->enabled())
			ImGui::Text("%s [%d|%0.2f]", _TR("Multi-Animation"), f_AnimationMultiplier.value(), f_AnimationState.value());

		if (f_AttackSpeed->enabled())
			ImGui::Text("%s [%0.1f]", _TR("Attack Speed"), f_SpeedMultiplier.value());

		if (f_CustomElement->enabled())
			ImGui::Text("%s", _TR("Custom Element"));
	}

	RapidFire& RapidFire::GetInstance()
	{
		static RapidFire instance;
		return instance;
	}

	// Relic: current HP of an entity, every hop checked - a target can be unloaded between a hit and its probe.
	static bool ReadHp(game::Entity* entity, float& hp)
	{
		hp = -1.0f;
		if (entity == nullptr || !entity->isLoaded())
			return false;
		relic::Try([entity, &hp]()
		{
			auto* combat = entity->combat();
			if (combat == nullptr)
				return;
			auto* prop = combat->fields._combatProperty_k__BackingField;
			if (prop == nullptr)
				return;
			hp = app::MoleMole_SafeFloat_get_Value(prop->fields.HP, nullptr);
		});
		return hp >= 0.0f;
	}

	int RapidFire::CalcCountToKill(float attackDamage, uint32_t targetID)
	{
		if (attackDamage == 0)
			return f_Multiplier;

		auto& manager = game::EntityManager::instance();
		auto targetEntity = manager.entity(targetID);
		if (targetEntity == nullptr)
			return f_Multiplier;

		float HP = 0.0f;
		if (!ReadHp(targetEntity, HP))   // Relic: was an unchecked _combatProperty dereference
			return f_Multiplier;
		int attackCount = (int)ceil(HP / attackDamage);
		return std::clamp(attackCount, 1, 200);
	}

	// Relic: the multiplier the sliders ask for, Randomize included - shared by both methods.
	int RapidFire::GetMultiplier()
	{
		if (!f_Randomize->enabled())
			return f_Multiplier;
		int lo = f_minMultiplier.value();
		int hi = f_maxMultiplier.value();
		if (lo >= hi)
			return lo;
		return rand() % (hi - lo + 1) + lo;   // Relic: upstream's `% (hi - lo)` could never reach hi
	}

	int RapidFire::GetAttackCount(app::LCBaseCombat* combat, uint32_t targetID, app::AttackResult* attackResult)
	{
		if (!f_MultiHit)
			return 1;

		auto& manager = game::EntityManager::instance();
		auto targetEntity = manager.entity(targetID);
		if (targetEntity == nullptr)   // Relic: was dereferenced unchecked
			return 1;
		auto baseCombat = targetEntity->combat();
		if (baseCombat == nullptr)
			return 1;

		if (f_Randomize->enabled())
			return GetMultiplier();

		int countOfAttacks = f_Multiplier;
		if (f_OnePunch)
		{
			app::MoleMole_Formula_CalcAttackResult(combat->fields._combatProperty_k__BackingField,
				baseCombat->fields._combatProperty_k__BackingField,
				attackResult, manager.avatar()->raw(), targetEntity->raw(), nullptr);
			countOfAttacks = CalcCountToKill(attackResult->fields.damage, targetID);
		}

		return countOfAttacks;
	}

	bool IsAvatarOwner(game::Entity entity)
	{
		auto& manager = game::EntityManager::instance();
		auto avatarID = manager.avatar()->runtimeID();

		while (entity.isGadget())
		{
			game::Entity temp = entity;
			entity = game::Entity(app::MoleMole_GadgetEntity_GetOwnerEntity(reinterpret_cast<app::GadgetEntity*>(entity.raw()), nullptr));
			if (entity.runtimeID() == avatarID)
				return true;
		}

		return false;

	}

	bool IsAttackByAvatar(game::Entity& attacker)
	{
		if (attacker.raw() == nullptr)
			return false;

		auto& manager = game::EntityManager::instance();
		auto avatarID = manager.avatar()->runtimeID();
		auto attackerID = attacker.runtimeID();

		return attackerID == avatarID || IsAvatarOwner(attacker) || attacker.type() == app::EntityType__Enum_1::Bullet || attacker.type() == app::EntityType__Enum_1::Field;
	}

	bool IsConfigByAvatar(game::Entity& attacker)
	{
		if (attacker.raw() == nullptr)
			return false;

		auto& manager = game::EntityManager::instance();
		auto avatarRaw = manager.avatar()->raw();
		if (avatarRaw == nullptr)   // Relic: no character yet (loading screen) - was dereferenced unchecked
			return false;
		auto avatarID = avatarRaw->fields._configID_k__BackingField;
		auto attackerID = attacker.raw()->fields._configID_k__BackingField;
		// LOG_DEBUG("configID = %d", attackerID);
		// Taiga#5555: IDs can be found in ConfigAbility_Avatar_*.json or GadgetExcelConfigData.json
		bool bulletID = attackerID >= 40000160 && attackerID <= 41079999;

		return avatarID == attackerID || bulletID || attacker.type() == app::EntityType__Enum_1::Bullet;
	}

	bool IsValidByFilter(game::Entity* entity)
	{
		if (game::filters::combined::OrganicTargets.IsValid(entity) ||
			game::filters::monster::SentryTurrets.IsValid(entity) ||
			game::filters::combined::Ores.IsValid(entity) ||
			game::filters::puzzle::Geogranum.IsValid(entity) ||
			game::filters::puzzle::LargeRockPile.IsValid(entity) ||
			game::filters::puzzle::SmallRockPile.IsValid(entity))
			return true;
		return false;
	}

	app::ElementType__Enum GetElementType()
	{
		RapidFire& rapidFire = RapidFire::GetInstance();
		int randNum = std::rand() % 11;
		if (!rapidFire.f_isRandType)
		return elementType.at(rapidFire.f_ElementType.value());
		else switch (randNum)
		{
		default:return app::ElementType__Enum::None;
		case 0:return app::ElementType__Enum::AntiFire;
		case 1:return app::ElementType__Enum::COUNT;
		case 2:return app::ElementType__Enum::Electric;
		case 3:return app::ElementType__Enum::Fire;
		case 4:return app::ElementType__Enum::Frozen;
		case 5:return app::ElementType__Enum::Grass;
		case 6:return app::ElementType__Enum::Ice;
		case 7:return app::ElementType__Enum::Rock;
		case 8:return app::ElementType__Enum::VehicleMuteIce;
		case 9:return app::ElementType__Enum::Water;
		case 10:return app::ElementType__Enum::Wind;
			break;
		}
	}

	// Relic: starts following one hit (see HitProbe); a probe already running keeps its hit.
	static void StartHitProbe(game::Entity* target, float gameDamage, float sent, int hits, const char* method)
	{
		if (s_probe.id != 0 || target == nullptr)
			return;
		float hp = 0.0f;
		if (!ReadHp(target, hp))
			return;
		s_probe.id = target->runtimeID();
		s_probe.hpBefore = hp;
		s_probe.gameDamage = gameDamage;
		s_probe.sent = sent;
		s_probe.hits = hits;
		s_probe.method = method;
		s_probe.due = util::GetCurrentTimeMillisec() + kProbeDelayMs;
	}

	// Raises when any entity do hit event.
	// Just recall attack few times (regulating by combatProp)
	// It's not tested well, so, I think, anticheat can detect it.
	/*static void LCBaseCombat_DoHitEntity_Hook(app::LCBaseCombat* __this, uint32_t targetID, app::AttackResult* attackResult, bool ignoreCheckCanBeHitInMP, MethodInfo* method)
	{
		auto attacker = game::Entity(__this->fields._._._entity);
		RapidFire& rapidFire = RapidFire::GetInstance();
		if (!IsConfigByAvatar(attacker) || !IsAttackByAvatar(attacker) || !rapidFire.f_Enabled)
			return CALL_ORIGIN(LCBaseCombat_DoHitEntity_Hook, __this, targetID, attackResult, ignoreCheckCanBeHitInMP, method);

		auto& manager = game::EntityManager::instance();
		auto originalTarget = manager.entity(targetID);

		if (!IsValidByFilter(originalTarget))
			return CALL_ORIGIN(LCBaseCombat_DoHitEntity_Hook, __this, targetID, attackResult, ignoreCheckCanBeHitInMP, method);

		std::vector<cheat::game::Entity*> validEntities;
		validEntities.push_back(originalTarget);

		if (rapidFire.f_MultiTarget)
		{
			auto filteredEntities = manager.entities();
			for (const auto& entity : filteredEntities) {
				auto distance = originalTarget->distance(entity);

				if (entity->runtimeID() == manager.avatar()->runtimeID())
					continue;

				if (entity->runtimeID() == targetID)
					continue;

				if (distance > rapidFire.f_MultiTargetRadius)
					continue;

				if (!IsValidByFilter(entity))
					continue;

				validEntities.push_back(entity);
			}
		}

		for (const auto& entity : validEntities) {

			if (rapidFire.f_MultiHit) {
				int attackCount = rapidFire.GetAttackCount(__this, entity->runtimeID(), attackResult);
				for (int i = 0; i < attackCount; i++)
					app::MoleMole_LCBaseCombat_FireBeingHitEvent(__this, entity->runtimeID(), attackResult, method);
			}
			else CALL_ORIGIN(LCBaseCombat_DoHitEntity_Hook, __this, entity->runtimeID(), attackResult, ignoreCheckCanBeHitInMP, method);
		}

		CALL_ORIGIN(LCBaseCombat_DoHitEntity_Hook, __this, targetID, attackResult, ignoreCheckCanBeHitInMP, method);
	}*/

	static void LCBaseCombat_FireBeingHitEvent_Hook(app::LCBaseCombat* __this, uint32_t attackeeRuntimeID, app::AttackResult* attackResult, MethodInfo* method)
	{
		auto attacker = game::Entity(__this->fields._._._entity);
		RapidFire& rapidFire = RapidFire::GetInstance();
		s_diag.hitCalls++;
#if RELIC_GAME_VERSION <= 16   // RELIC-DIAG16
		LOG_DEBUG("[rf16] hit this=%p attacker=%u cfg=%u type=%d | avatar=%u | attackee=%u dmg=%.1f | enabled=%d cfgByAvatar=%d byAvatar=%d",
			__this, attacker.runtimeID(), attacker.raw() ? attacker.raw()->fields._configID_k__BackingField : 0u, (int)attacker.type(),
			game::EntityManager::instance().avatar()->runtimeID(), attackeeRuntimeID, attackResult ? attackResult->fields.damage : -1.0f,
			rapidFire.f_Enabled->enabled() ? 1 : 0, IsConfigByAvatar(attacker) ? 1 : 0, IsAttackByAvatar(attacker) ? 1 : 0);
#endif

		// Relic: Custom Element and Auto weakspot are menu entries of their own, but upstream only applies them
		// while the Enabled switch is on too. Here they work on their own; the multipliers still need Enabled.
		bool wantsAny = rapidFire.f_Enabled->enabled() || rapidFire.f_HeadShot.value() || rapidFire.f_CustomElement->enabled();
		if (attackResult == nullptr || !wantsAny || !IsConfigByAvatar(attacker) || !IsAttackByAvatar(attacker))
			return CALL_ORIGIN(LCBaseCombat_FireBeingHitEvent_Hook, __this, attackeeRuntimeID, attackResult, method);
		s_diag.hitByAvatar++;

		auto& manager = game::EntityManager::instance();
		auto originalTarget = manager.entity(attackeeRuntimeID);
#if RELIC_GAME_VERSION <= 16   // RELIC-DIAG16
		LOG_DEBUG("[rf16] target=%p type=%d name='%s' filterOK=%d", originalTarget, originalTarget ? (int)originalTarget->type() : -1,
			originalTarget ? originalTarget->name().c_str() : "", IsValidByFilter(originalTarget) ? 1 : 0);
#endif

		if (rapidFire.f_HeadShot)
			attackResult->fields.hitPosType = app::HitBoxType__Enum::Head;
		// Relic: null check - an attack without an attacker property is a real thing (weather and gadget hits).
		if (rapidFire.f_CustomElement->enabled() && attackResult->fields._attackerAttackProperty != nullptr)
			attackResult->fields._attackerAttackProperty->fields._elementType = GetElementType();

		if (!rapidFire.f_Enabled->enabled() || originalTarget == nullptr || !IsValidByFilter(originalTarget))
			return CALL_ORIGIN(LCBaseCombat_FireBeingHitEvent_Hook, __this, attackeeRuntimeID, attackResult, method);
		s_diag.hitTarget++;

		std::vector<cheat::game::Entity*> validEntities;
		validEntities.push_back(originalTarget);


		if (rapidFire.f_MultiTarget->enabled())
		{
			auto filteredEntities = manager.entities();
			for (const auto& entity : filteredEntities) {
				auto distance = originalTarget->distance(entity);

				if (entity->runtimeID() == manager.avatar()->runtimeID())
					continue;

				if (entity->runtimeID() == attackeeRuntimeID)
					continue;

				if (distance > rapidFire.f_MultiTargetRadius)
					continue;

				if (!IsValidByFilter(entity))
					continue;

				validEntities.push_back(entity);
			}
		}

		bool multiHit = rapidFire.f_MultiHit->enabled();
		float gameDamage = attackResult->fields.damage;
		s_diag.lastDamage = gameDamage;

		// Relic: the hit is repeated, never its damage number raised. A GIO server computes the damage of every hit
		// itself and ignores the client's number: a multiplied number takes only the game's own damage off the target,
		// hit for hit, while every repeated hit counts. The client would still show the big number.
		int firstCount = 1;
		for (const auto& entity : validEntities) {
			int attackCount = multiHit ? rapidFire.GetAttackCount(__this, entity->runtimeID(), attackResult) : 1;
#if RELIC_GAME_VERSION <= 16   // RELIC-DIAG16
			LOG_DEBUG("[rf16] fire x%d at %u (multiHit=%d onePunch=%d randomize=%d mult=%d min=%d max=%d combat=%p)", attackCount, entity->runtimeID(),
				rapidFire.f_MultiHit->enabled() ? 1 : 0, rapidFire.f_OnePunch.value() ? 1 : 0, rapidFire.f_Randomize->enabled() ? 1 : 0,
				rapidFire.f_Multiplier.value(), rapidFire.f_minMultiplier.value(), rapidFire.f_maxMultiplier.value(), entity->combat());
#endif
			if (entity == originalTarget)
				firstCount = attackCount;
			if (attackCount > 1)
				s_diag.hitExtra += static_cast<uint32_t>(attackCount - 1);
			for (int i = 0; i < attackCount; i++)
				CALL_ORIGIN(LCBaseCombat_FireBeingHitEvent_Hook, __this, entity->runtimeID(), attackResult, method);
		}
		s_diag.lastSent = attackResult->fields.damage;
		if (multiHit)
			StartHitProbe(originalTarget, gameDamage, attackResult->fields.damage, firstCount, "repeat");
	}

	// Relic: a Unity object whose native side is gone keeps a live managed wrapper, and calling into it raises a
	// managed exception instead of returning - the idiom ESPRender/HideUI use for the same reason.
	static bool IsAnimatorAlive(app::Animator* animator)
	{
		return animator != nullptr && animator->fields._._._.m_CachedPtr != nullptr;
	}

	// Relic: guarded on their own, so a dead animator costs this one write - never the game's own
	// HandleProcessItem, which the hook guard would drop together with a faulting hook body.
	static bool SetAnimatorSpeed(app::Animator* animator, float speed)
	{
		if (!IsAnimatorAlive(animator))
			return false;
		return relic::Try([animator, speed]() { app::Animator_set_speed(animator, speed, nullptr); });
	}

	static float GetAnimatorSpeed(app::Animator* animator)
	{
		float speed = -1.0f;
		if (IsAnimatorAlive(animator))
			relic::Try([animator, &speed]() { speed = app::Animator_get_speed(animator, nullptr); });
		return speed;
	}

	// Relic: back to the game's normal speed. 1.0 is what the game's own reset writes (BaseEntity
	// DNPPAIELOMJ$$JGFGDBHDMEG @0x00CEA280 on 1.6) and what upstream's m_SpeedMultiplier restore amounted to.
	static void RestoreAttackSpeed()
	{
		if (s_fastAvatarID == 0)
			return;
		auto* entity = game::EntityManager::instance().entity(s_fastAvatarID);
		if (entity != nullptr && SetAnimatorSpeed(entity->animator(), 1.0f))
			s_diag.speedRestored++;
		s_fastAvatarID = 0;
	}

	static void NoteAvatarTag(int32_t tag)
	{
		std::lock_guard<std::mutex> guard(s_diag.lock);
		if (s_diag.avatarTags.size() < 64 || s_diag.avatarTags.count(tag) != 0)
			s_diag.avatarTags[tag]++;
	}

	static void VCAnimatorEvent_HandleProcessItem_Hook(app::MoleMole_VCAnimatorEvent* __this,
		app::MoleMole_VCAnimatorEvent_MoleMole_VCAnimatorEvent_AnimatorEventPatternProcessItem* processItem,
		app::AnimatorStateInfo processStateInfo, app::MoleMole_VCAnimatorEvent_MoleMole_VCAnimatorEvent_TriggerMode__Enum mode, MethodInfo* method)
	{
		auto attacker = game::Entity(__this->fields._._._entity);
		RapidFire& rapidFire = RapidFire::GetInstance();
		s_diag.animCalls++;
		bool isAttackAnimation = std::any_of(std::begin(attackTags), std::end(attackTags),
			[&](int32_t tag) { return processStateInfo.m_Tag == tag; });
		bool isAttacking = IsAttackByAvatar(attacker) && isAttackAnimation;

		// Relic: Attack Speed is about the active character's own animator. Upstream also sped up the animator of
		// whatever else passed IsAttackByAvatar (bullets, fields, owned gadgets) and reset it on the next item
		// that was not an attack - on 1.6 that could be another layer of the same animator in the same frame.
		uint32_t avatarID = game::EntityManager::instance().avatar()->runtimeID();
		bool isAvatar = avatarID != 0 && attacker.raw() != nullptr && attacker.runtimeID() == avatarID;
		int64_t now = 0;
		if (isAvatar)
		{
			s_diag.animAvatar++;
			NoteAvatarTag(processStateInfo.m_Tag);
			if (isAttackAnimation)
			{
				s_diag.animAttack++;
				now = util::GetCurrentTimeMillisec();
				s_lastAttackMs = now;
			}
		}

		if (rapidFire.f_MultiAnimation->enabled() && isAttacking)
		{
			// Set counter back to 1 when any new attack animation is invoked
			if (processStateInfo.m_NormalizedTime <= 0.01f)
				rapidFire.animationCounter = 1;

			if (rapidFire.animationCounter <= rapidFire.f_AnimationMultiplier)
			{
				// Can be configured up to 1.0 but 0.1 to 0.9 you can barely notice the difference
				// So 0 - 0.2 is enough.
				processItem->fields.lastTime = (rapidFire.f_AnimationState / 10);
				rapidFire.animationCounter++;
			}
		}

		if (rapidFire.f_AttackSpeed->enabled() && isAvatar && isAttackAnimation)
		{
			if (!isinf(processStateInfo.m_Length))
			{
				auto* animator = attacker.animator();
				if (SetAnimatorSpeed(animator, rapidFire.f_SpeedMultiplier))
				{
					s_fastAvatarID = avatarID;
					s_diag.speedSet++;
					s_diag.speedAfterSet = GetAnimatorSpeed(animator);
				}
			}
		}
		else if (s_fastAvatarID != 0)
		{
			if (now == 0)
				now = util::GetCurrentTimeMillisec();
			if (!rapidFire.f_AttackSpeed->enabled() || now - s_lastAttackMs > kAttackSpeedHoldMs)
				RestoreAttackSpeed();
		}

		CALL_ORIGIN(VCAnimatorEvent_HandleProcessItem_Hook, __this, processItem, processStateInfo, mode, method);
	}

#if RELIC_GAME_VERSION <= 16
	// Relic: which of the methods these features run through a 1.6 hotfix has replaced. A hotpatched 1.6 client
	// (res 3557509 / silence data 3266913 - what Relic's hotpatch mirror serves) carries InjectFix patches and a
	// Lua block, and every game method starts with both checks: `if (Class.static[off] != null)` -> the xLua
	// hotfix instead, `if (IFix.WrappersManagerImpl.IsPatched(id))` -> the InjectFix one instead. A replaced
	// method no longer runs the code our hooks and offsets were read from. The three numbers per method are read
	// straight out of each method's own prologue - `tools/hotfix_sites.py sites 16 <rva...>` prints this table -
	// its X_TypeInfo slot, the static DelegateBridge field its xLua check reads, and the id its IsPatched check
	// passes; `hotfix_sites.py ids 16 <hex...>` names the patched ids the probe lists. The class name stored in
	// the Il2CppClass is compared first, so a wrong slot reports "not initialised" instead of a verdict.
	namespace
	{
		struct HotfixSite
		{
			const char* name;
			const char* klass;        // the dump name the Il2CppClass behind the slot must carry
			uint32_t typeInfoRva;
			uint32_t luaOffset;       // offset of the method's DelegateBridge in the class statics
			int32_t ifixId;
		};

		const HotfixSite kHotfixSites[] = {
			// the attacker's side
			{ "DoHitEntity",              "PMBPGLIKLGN", 0x08FB3348, 0x070, 0x2AED },
			{ "Formula.CalcAttackResult", "CIDPEKCCCNL", 0x08FF8740, 0x000, 0x2AF4 },
			{ "Formula.Calc (core)",      "CIDPEKCCCNL", 0x08FF8740, 0x010, 0x2AFA },
			{ "FireBeingHitEvent",        "PMBPGLIKLGN", 0x08FB3348, 0x140, 0x2B2D },
			{ "FireBeingHitEvent(evt)",   "PMBPGLIKLGN", 0x08FB3348, 0x150, 0x1844 },
			{ "EvtBeingHit.Init",         "JMCBAOLGCNG", 0x08FF2800, 0x020, 0x2B2E },
			{ "EvtBeingHit.attackResult", "JMCBAOLGCNG", 0x08FF2800, 0x048, 0x1828 },
			{ "BaseEntity.FireEvent",     "DNPPAIELOMJ", 0x08FACF00, 0x350, 0x180F },
			{ "EventManager.FireEvent",   "CADNPCCFBBB", 0x09089620, 0x088, 0x1810 },
			// the dispatch and the target's side
			{ "EventManager.Dispatch",    "CADNPCCFBBB", 0x09089620, 0x058, 0x1812 },
			{ "EventManager.ToTarget",    "CADNPCCFBBB", 0x09089620, 0x068, 0x1814 },
			{ "EventManager.ToListeners", "CADNPCCFBBB", 0x09089620, 0x080, 0x1833 },
			{ "BaseEntity.HandleEvent",   "DNPPAIELOMJ", 0x08FACF00, 0x320, 0x1815 },
			{ "BaseEntity.OnBeingHit",    "DNPPAIELOMJ", 0x08FACF00, 0x330, 0x1823 },
			{ "LCBaseCombat.PreBeingHit", "PMBPGLIKLGN", 0x08FB3348, 0x060, 0x1826 },
			{ "Formula.ElementFactor",    "CIDPEKCCCNL", 0x08FF8740, 0x008, 0x1829 },   // damage *= (1-reduction)(1+amplify)
			{ "LCBaseCombat.OnEvent",     "PMBPGLIKLGN", 0x08FB3348, 0x048, 0x2888 },
			{ "OnBeingHit",               "PMBPGLIKLGN", 0x08FB3348, 0x2D8, 0x28A0 },
			{ "LCBaseCombat.OnResolved",  "PMBPGLIKLGN", 0x08FB3348, 0x058, 0x2AE7 },
			{ "OnBeingHitResolved",       "PMBPGLIKLGN", 0x08FB3348, 0x2E0, 0x2B59 },
			{ "AttackResult.IsRejected",  "HEBLMKMEBIF", 0x08FFF8C8, 0x060, 0x1842 },
			// what goes to the server
			{ "CombatSync.SyncEvent",     "JGHJJNAECLA", 0x09039B90, 0x248, 0x1836 },
			{ "CombatSync.SyncBeingHit",  "JGHJJNAECLA", 0x09039B90, 0x268, 0x1841 },
			{ "CombatInvoke.AddBeingHit", "IPCPELCOPAM", 0x08F8C110, 0x038, 0x1852 },
			// Attack Speed / Multi-Animation
			{ "AnimatorEvent.LateTick",   "AJJDAJJHKNL", 0x08F87F08, 0x018, 0x3782 },
			{ "AnimatorEvent.TickLayer",  "AJJDAJJHKNL", 0x08F87F08, 0x068, 0x3783 },
			{ "AnimatorEvent.TickItem",   "AJJDAJJHKNL", 0x08F87F08, 0x080, 0x378C },
			{ "HandleProcessItem",        "AJJDAJJHKNL", 0x08F87F08, 0x078, 0x378D },
			{ "BaseEntity.SetTimeScale",  "DNPPAIELOMJ", 0x08FACF00, 0x468, 0x1743 },
			{ "BaseEntity.AnimatorSpeed", "DNPPAIELOMJ", 0x08FACF00, 0xD80, 0x1754 },
		};

		// IFix.ILFixDynamicMethodWrapper_TypeInfo: IsPatched @0x01F706B0 reads it at +0x24; its static field 0
		// is the wrapper array IsPatched indexes (length at +0x18, items at +0x20).
		constexpr uint32_t kIFixWrapperTypeInfoRva = 0x09016BB0;
		constexpr uint32_t kStaticFieldsOffset = 0xA0;   // Il2CppClass::static_fields as the game's own code reads it

		bool PlausiblePointer(const void* p)
		{
			auto v = reinterpret_cast<uintptr_t>(p);
			return v > 0x10000 && v < 0x00007FFFFFFFFFFFull && (v & 7) == 0;
		}

		// The Il2CppClass behind a TypeInfo slot, or null while the game has not initialised that slot yet.
		void* ClassAt(uint32_t typeInfoRva, const char* expectedName, bool& nameOk)
		{
			nameOk = false;
			void* klass = *reinterpret_cast<void**>(il2cppi_get_base_address() + typeInfoRva);
			if (!PlausiblePointer(klass))
				return nullptr;
			const char* name = *reinterpret_cast<const char**>(reinterpret_cast<char*>(klass) + 0x10);   // Il2CppClass::name
			nameOk = name != nullptr && expectedName != nullptr && strcmp(name, expectedName) == 0;
			return klass;
		}

		// Builds the one-line summary; false while the slots are not initialised yet (the game fills them the
		// first time it runs each method, so this is only complete once the character has fought something).
		bool ProbeHotfixes(std::string& out)
		{
			bool complete = true;
			std::string replaced;
			std::string unknown;
			int ifixCount = -1;
			std::string ifixIds;
			void** ifixItems = nullptr;   // the wrapper array IsPatched indexes: items[id] != null = patched
			uint32_t ifixLength = 0;

			relic::Try([&]()
			{
				bool nameOk = false;
				void* wrapperClass = ClassAt(kIFixWrapperTypeInfoRva, "ILFixDynamicMethodWrapper", nameOk);
				if (wrapperClass == nullptr || !nameOk)
					return;
				char* statics = *reinterpret_cast<char**>(reinterpret_cast<char*>(wrapperClass) + kStaticFieldsOffset);
				if (!PlausiblePointer(statics))
					return;
				char* array = *reinterpret_cast<char**>(statics);
				if (array == nullptr)
				{
					ifixCount = 0;   // no patch loaded at all
					return;
				}
				if (!PlausiblePointer(array))
					return;
				uint32_t length = *reinterpret_cast<uint32_t*>(array + 0x18);
				void** items = reinterpret_cast<void**>(array + 0x20);
				if (length > 0x100000)
					return;   // not an array after all
				ifixCount = 0;
				for (uint32_t i = 0; i < length; i++)
				{
					if (items[i] == nullptr)
						continue;
					ifixCount++;
					if (ifixCount <= 60)
					{
						char id[16];
						_snprintf_s(id, sizeof(id), _TRUNCATE, "%s%X", ifixIds.empty() ? "" : ",", i);
						ifixIds += id;
					}
				}
				ifixItems = items;
				ifixLength = length;
			});

			for (const auto& site : kHotfixSites)
			{
				bool lua = false;
				bool known = false;
				relic::Try([&]()
				{
					bool nameOk = false;
					void* klass = ClassAt(site.typeInfoRva, site.klass, nameOk);
					if (klass == nullptr || !nameOk)
						return;
					char* statics = *reinterpret_cast<char**>(reinterpret_cast<char*>(klass) + kStaticFieldsOffset);
					if (!PlausiblePointer(statics))
						return;
					lua = *reinterpret_cast<void**>(statics + site.luaOffset) != nullptr;
					known = true;
				});
				bool ifix = false;
				if (ifixItems != nullptr && site.ifixId >= 0 && static_cast<uint32_t>(site.ifixId) < ifixLength)
					relic::Try([&]() { ifix = ifixItems[site.ifixId] != nullptr; });
				if (!known)
				{
					complete = false;
					unknown += unknown.empty() ? "" : ", ";
					unknown += site.name;
				}
				if (lua || ifix)
				{
					replaced += replaced.empty() ? "" : ", ";
					replaced += site.name;
					replaced += lua && ifix ? " (Lua+IFix)" : lua ? " (Lua)" : " (IFix)";
				}
			}

			char head[160];
			if (ifixCount < 0)
				_snprintf_s(head, sizeof(head), _TRUNCATE, "InjectFix: not readable");
			else
				_snprintf_s(head, sizeof(head), _TRUNCATE, "InjectFix: %d methods patched", ifixCount);
			out = "[atk] hotfix: ";
			out += head;
			if (!ifixIds.empty())
				out += " (ids " + ifixIds + (ifixCount > 60 ? ",..." : "") + ")";
			out += " | on the attack paths: ";
			out += replaced.empty() ? "nothing replaced" : replaced;
			if (!unknown.empty())
				out += " | not initialised yet: " + unknown;
			return complete && ifixCount >= 0;
		}
	}
#endif

	void RapidFire::OnGameUpdate()
	{
		int64_t now = util::GetCurrentTimeMillisec();

		// Relic: Attack Speed. The hook writes the speed from the animator-event LateTick, i.e. after this frame's
		// animation step; if anything in between puts the speed back, the next step never sees ours. So it is
		// re-asserted here, at the start of the frame, for as long as the character is attacking - and the value
		// found here says whether something did put it back (speedFixups).
		if (s_fastAvatarID != 0)
		{
			auto* avatar = game::EntityManager::instance().avatar();
			if (!f_AttackSpeed->enabled() || now - s_lastAttackMs > kAttackSpeedHoldMs || avatar->runtimeID() != s_fastAvatarID)
				RestoreAttackSpeed();
			else
			{
				auto* animator = avatar->animator();
				float current = GetAnimatorSpeed(animator);
				s_diag.speedAtUpdate = current;
				float wanted = f_SpeedMultiplier.value();
				if (current >= 0.0f && fabsf(current - wanted) > 0.001f && SetAnimatorSpeed(animator, wanted))
					s_diag.speedFixups++;
			}
		}

		// Relic: the pending HP probe.
		if (s_probe.id != 0 && now >= s_probe.due)
		{
			float hpNow = -1.0f;
			bool present = ReadHp(game::EntityManager::instance().entity(s_probe.id), hpNow);
			float expected = s_probe.sent * static_cast<float>(s_probe.hits);
			char line[400];
			if (present)
				_snprintf_s(line, sizeof(line), _TRUNCATE,
					"[atk] hit probe (%s): game damage %.0f, sent %.0f x%d = %.0f expected | target HP %.0f -> %.0f (%.1f s later): lost %.0f",
					s_probe.method, s_probe.gameDamage, s_probe.sent, s_probe.hits, expected, s_probe.hpBefore, hpNow,
					kProbeDelayMs / 1000.0f, s_probe.hpBefore - hpNow);
			else
				_snprintf_s(line, sizeof(line), _TRUNCATE,
					"[atk] hit probe (%s): game damage %.0f, sent %.0f x%d = %.0f expected | target HP %.0f -> gone (killed or unloaded)",
					s_probe.method, s_probe.gameDamage, s_probe.sent, s_probe.hits, expected, s_probe.hpBefore);
			{
				std::lock_guard<std::mutex> guard(s_diag.lock);
				s_diag.lastProbe = line;
			}
			if (s_probesWritten < kProbeLineLimit)
			{
				s_probesWritten++;
				relic::diag::append_line(line);
				LOG_DEBUG("%s", line);
			}
			s_probe.id = 0;
		}

		// Relic: a counter line for relic-diag.txt every 2 s, but only when the character did something the
		// counters are about - the per-entity animator counter moves every frame and would fill the file.
		static int64_t s_nextLine = 0;
		static uint64_t s_lastKey = 0;
		static size_t s_tagsWritten = 0;
		if (now >= s_nextLine)
		{
			s_nextLine = now + 2000;
			uint64_t key = (uint64_t)s_diag.animAttack.load() * 1000003ull + s_diag.speedSet.load() * 10007ull
				+ s_diag.speedFixups.load() * 101ull + s_diag.hitByAvatar.load() * 7919ull + s_diag.hitExtra.load() * 31ull
				+ s_diag.hitTarget.load() * 65537ull;
			if (key != s_lastKey)
			{
				s_lastKey = key;
				char line[400];
				_snprintf_s(line, sizeof(line), _TRUNCATE,
					"[atk] anim: calls=%u avatar=%u attack=%u | speed: set=%u fixups=%u restored=%u readback=%.2f atFrameStart=%.2f"
					" | hits: calls=%u ours=%u onTarget=%u extra=%u lastDamage=%.0f sent=%.0f",
					s_diag.animCalls.load(), s_diag.animAvatar.load(), s_diag.animAttack.load(), s_diag.speedSet.load(),
					s_diag.speedFixups.load(), s_diag.speedRestored.load(), s_diag.speedAfterSet.load(), s_diag.speedAtUpdate.load(),
					s_diag.hitCalls.load(), s_diag.hitByAvatar.load(), s_diag.hitTarget.load(), s_diag.hitExtra.load(),
					s_diag.lastDamage.load(), s_diag.lastSent.load());
				relic::diag::append_line(line);
			}

			// The character's own animator tags, whenever a new one shows up - this is what says whether the
			// attack tags above are the ones this game version really uses.
			std::string tags;
			size_t tagCount = 0;
			{
				std::lock_guard<std::mutex> guard(s_diag.lock);
				tagCount = s_diag.avatarTags.size();
				if (tagCount != s_tagsWritten)
					tags = FormatAvatarTags(s_diag.avatarTags, " ");
			}
			if (tagCount != s_tagsWritten && !tags.empty())
			{
				s_tagsWritten = tagCount;
				relic::diag::append_line(("[atk] avatar animator tags: " + tags).c_str());
			}
		}

#if RELIC_GAME_VERSION <= 16
		// Relic: the hotfix probe, every 10 s until every slot answered, then once a minute; written when it changes.
		static int64_t s_nextHotfix = 0;
		static std::string s_lastHotfix;
		if (now >= s_nextHotfix)
		{
			std::string summary;
			bool complete = ProbeHotfixes(summary);
			s_nextHotfix = now + (complete ? 60000 : 10000);
			if (summary != s_lastHotfix)
			{
				s_lastHotfix = summary;
				{
					std::lock_guard<std::mutex> guard(s_diag.lock);
					s_diag.hotfix = summary;
				}
				relic::diag::append_line(summary.c_str());
				LOG_DEBUG("%s", summary.c_str());
			}
		}
#endif
	}

	void RapidFire::DrawDiagnostics()
	{
		if (!ImGui::CollapsingHeader(_TR("Diagnostics")))
			return;

		ImGui::TextWrapped("%s", _TR("What the attack hooks saw since the game started. Every line is also written to "
			"relic-diag.txt next to the mod, so a session can be read back afterwards."));
		ImGui::Text("%s: %u / %u / %u", _TR("Animator events (all / your character / in an attack)"),
			s_diag.animCalls.load(), s_diag.animAvatar.load(), s_diag.animAttack.load());
		ImGui::Text("%s: %u, %s %u, %s %u", _TR("Attack Speed writes"), s_diag.speedSet.load(),
			_TR("put back by the game"), s_diag.speedFixups.load(), _TR("restored"), s_diag.speedRestored.load());
		ImGui::Text("%s: %.2f / %.2f", _TR("Animator speed (after our write / at the next frame)"),
			s_diag.speedAfterSet.load(), s_diag.speedAtUpdate.load());
		ImGui::Text("%s: %u / %u / %u", _TR("Hits (all / yours / on a valid target)"),
			s_diag.hitCalls.load(), s_diag.hitByAvatar.load(), s_diag.hitTarget.load());
		ImGui::Text("%s: %u", _TR("Repeated copies"), s_diag.hitExtra.load());
		ImGui::Text("%s: %.0f -> %.0f", _TR("Last hit damage (game / sent)"), s_diag.lastDamage.load(), s_diag.lastSent.load());

		std::string probe, hotfix, tags;
		{
			std::lock_guard<std::mutex> guard(s_diag.lock);
			probe = s_diag.lastProbe;
			hotfix = s_diag.hotfix;
			tags = FormatAvatarTags(s_diag.avatarTags, "  ");
		}
		if (!tags.empty())
			ImGui::TextWrapped("%s: %s", _TR("Your character's animator tags"), tags.c_str());
		if (!probe.empty())
			ImGui::TextWrapped("%s", probe.c_str());
		if (!hotfix.empty())
			ImGui::TextWrapped("%s", hotfix.c_str());
	}
}
