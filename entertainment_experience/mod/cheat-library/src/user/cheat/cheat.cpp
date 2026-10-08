#include <pch-il2cpp.h>
#include "cheat.h"

#include <helpers.h>

#include <cheat/events.h>

#include <cheat-base/cheat/misc/Settings.h>

#include <cheat/misc/Hotkeys.h>
#include <cheat/misc/Debug.h>
#include <cheat/misc/About.h>
#include <cheat/misc/sniffer/PacketSniffer.h>
#include <cheat/misc/BlockWindSeed.h>

#include <cheat/player/GodMode.h>
#include <cheat/player/InfiniteStamina.h>
#include <cheat/player/NoCD.h>
#include <cheat/player/NoClip.h>
#include <cheat/player/RapidFire.h>
#include <cheat/player/AutoRun.h>
#include <cheat/player/FallControl.h>

#include <cheat/world/AutoLoot.h>
#include <cheat/world/DialogSkip.h>
#include <cheat/world/DumbEnemies.h>
#include <cheat/world/FreezeEnemies.h>
#include <cheat/world/ElementalSight.h>
#include <cheat/world/KillAura.h>
#include <cheat/world/MobVacuum.h>
#include <cheat/world/AutoTreeFarm.h>
#include <cheat/world/AutoDestroy.h>
#include <cheat/world/FakeTime.h>
#include <cheat/world/AutoSeelie.h>
#include <cheat/world/VacuumLoot.h>
#include <cheat/world/GameSpeed.h>

#include <cheat/teleport/ChestTeleport.h>
#include <cheat/teleport/MapTeleport.h>
#include <cheat/teleport/OculiTeleport.h>
#include <cheat/teleport/CustomTeleports.h>
#include <cheat/teleport/QuestTeleport.h>

#include <cheat/esp/ESP.h>
#include <cheat/imap/InteractiveMap.h>

#if RELIC_GAME_VERSION >= 21
#include <cheat/world/AutoFish.h>
#endif
#if RELIC_GAME_VERSION == 28
#include <cheat/world/NavBake.h>
#endif
#include <cheat/world/AutoCook.h>
#include <cheat/world/AutoChallenge.h>
#include <cheat/world/CustomWeather.h>
#include <cheat/world/OpenTeamImmediately.h>
#include <cheat/world/SkipEnhanceAnimation.h>

#include <cheat/visuals/NoFog.h>
#include <cheat/visuals/FPSUnlock.h>
#include <cheat/visuals/CameraZoom.h>
#include <cheat/visuals/ShowChestIndicator.h>
#include <cheat/visuals/ProfileChanger.h>
#include <cheat/visuals/PaimonFollow.h>
#include <cheat/visuals/HideUI.h>
#include <cheat/visuals/Browser.h>
#include <cheat/visuals/EnablePeeking.h>
#include <cheat/visuals/TextureChanger.h>
#include <cheat/visuals/FreeCamera.h>
#include <cheat/visuals/CameraPath.h>
#include <cheat/visuals/RenderResolution.h>
#include <cheat/visuals/RenderDistance.h>
#include <cheat/visuals/AnimationChanger.h>
#include <cheat/visuals/EmotionChanger.h>
#include <cheat/visuals/ShowSkillCD.h>
#include <cheat/visuals/FlycloakModifier.h>
#include <cheat/visuals/SkinModifier.h>

#include <resource.h>

#include "GenshinCM.h"

namespace cheat 
{
	static void InstallEventHooks();

	void OnLanguageChanged()
	{
		renderer::SetDefaultFont(Translator::GetCurrentFontName());
	}

	void Init()
	{
		config::SetupUpdate(&events::GameUpdateEvent);

		// Relic: ProtectionBypass and RSAPatch are gone - mhynot2 already stands in for the anti-cheat driver and the
		// private server speaks to the stock client (Relic patches the 2.8 metadata on disk instead).

		GenshinCM& manager = GenshinCM::instance();

#define FEAT_INST(name) &feature::##name##::GetInstance()
		manager.AddFeatures({
			FEAT_INST(Language),
			FEAT_INST(Settings),
			FEAT_INST(Hotkeys),
			FEAT_INST(Debug),
			FEAT_INST(About),
			FEAT_INST(PacketSniffer),
			FEAT_INST(BlockWindSeed),

			FEAT_INST(GodMode),
			FEAT_INST(InfiniteStamina),
			FEAT_INST(NoCD),
			FEAT_INST(NoClip),
			FEAT_INST(RapidFire),
			FEAT_INST(AutoRun),
			FEAT_INST(FallControl),

			FEAT_INST(AutoLoot),
			FEAT_INST(AutoTreeFarm),
			FEAT_INST(AutoDestroy),
			FEAT_INST(AutoSeelie),
			FEAT_INST(OpenTeamImmediately),
			FEAT_INST(SkipEnhanceAnimation),
			FEAT_INST(VacuumLoot),
			FEAT_INST(DialogSkip),
			FEAT_INST(DumbEnemies),
			FEAT_INST(FreezeEnemies),
			FEAT_INST(ElementalSight),
			FEAT_INST(KillAura),
			FEAT_INST(AutoChallenge),
			FEAT_INST(MobVacuum),
			FEAT_INST(FakeTime),
			FEAT_INST(GameSpeed),

			FEAT_INST(ChestTeleport),
			FEAT_INST(OculiTeleport),
			FEAT_INST(MapTeleport),
			FEAT_INST(CustomTeleports),
			FEAT_INST(QuestTeleport),

			FEAT_INST(ESP),
			FEAT_INST(InteractiveMap),

#if RELIC_GAME_VERSION >= 21
			FEAT_INST(AutoFish),
#endif
#if RELIC_GAME_VERSION == 28
			FEAT_INST(NavBake),
#endif
			FEAT_INST(AutoCook),

			FEAT_INST(CustomWeather),

			FEAT_INST(FPSUnlock),
			FEAT_INST(ChestIndicator),
			FEAT_INST(ShowSkillCD),
			FEAT_INST(NoFog),
			FEAT_INST(HideUI),
			FEAT_INST(EnablePeeking),
			FEAT_INST(FlycloakModifier),
			FEAT_INST(SkinModifier),
			
			FEAT_INST(ProfileChanger),
			FEAT_INST(PaimonFollow),
			FEAT_INST(Browser),
			FEAT_INST(CameraZoom),
			FEAT_INST(TextureChanger),
			FEAT_INST(CameraPath),      // Relic: BEFORE FreeCamera on purpose - construction order is GameUpdate handler order, so the
			                            // pose it pushes is consumed by the camera update of the same tick (its constructor never touches FreeCamera)
			FEAT_INST(FreeCamera),
			FEAT_INST(RenderResolution),
			FEAT_INST(RenderDistance),
			FEAT_INST(AnimationChanger),
			FEAT_INST(EmotionChanger)
			
			});
#undef FEAT_INST

		manager.SetModuleOrder({
			"About",
			"Player",
			"World",
			"Teleport",
			"ESP",
			"Visuals",
			"Hotkeys",
			"Settings",
			"Debug"
			});


		auto defaultFont = renderer::Font::LoadFontFromResource(IMGUI_FONT, RT_RCDATA, "DefaultFont", renderer::Font::FONT_RANGE_DEFAULT);
		// Relic: only the Latin default font is embedded (the CJK/Cyrillic faces were 16 MB of the DLL).
		renderer::AddFont(defaultFont);

		auto& language = feature::Language::GetInstance();
		Translator::Init(ResourceLoader::Load(R_LANGUAGES, RT_RCDATA));
		Translator::SetLanguage(language.f_Language.value());

		Translator::LanguageChangedEvent += FUNCTION_HANDLER(OnLanguageChanged);
		OnLanguageChanged();

		manager.Init();

		InstallEventHooks();
	}

	static void CheckAccountChanged()
	{
		UPDATE_DELAY(2000U);

		static uint32_t _lastUserID = 0;

		auto playerModule = GET_SINGLETON(MoleMole_PlayerModule);
		if (playerModule == nullptr || playerModule->fields._accountData_k__BackingField == nullptr)
			return;

		auto& accountData = playerModule->fields._accountData_k__BackingField->fields;
		if (_lastUserID != accountData.userId)
			events::AccountChangedEvent(accountData.userId);

		_lastUserID = accountData.userId;
	}

	static void GameManager_Update_Hook(app::GameManager* __this, MethodInfo* method)
	{
		SAFE_BEGIN();
		events::GameUpdateEvent();
		CheckAccountChanged();
		SAFE_EEND();
		
		CALL_ORIGIN(GameManager_Update_Hook, __this, method);
	}

	static void LevelSyncCombatPlugin_RequestSceneEntityMoveReq_Hook(app::LevelSyncCombatPlugin* __this, uint32_t entityId, app::MotionInfo* syncInfo,
		bool isReliable, uint32_t relseq, MethodInfo* method)
	{
		events::MoveSyncEvent(entityId, syncInfo);
		CALL_ORIGIN(LevelSyncCombatPlugin_RequestSceneEntityMoveReq_Hook, __this, entityId, syncInfo, isReliable, relseq, method);
	}

	static void InstallEventHooks() 
	{
		INSTALL_HOOK(app::GameManager_Update, GameManager_Update_Hook);
		INSTALL_HOOK(app::MoleMole_LevelSyncCombatPlugin_RequestSceneEntityMoveReq, LevelSyncCombatPlugin_RequestSceneEntityMoveReq_Hook);
	}

}

