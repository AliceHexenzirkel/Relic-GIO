#pragma once
#include <atomic>

#include <cheat-base/cheat/Feature.h>
#include <cheat-base/config/config.h>

namespace cheat::feature
{
	class CustomWeather : public Feature
	{
	public:

		enum class WeatherType
		{
			ClearSky,
			Cloudy,
			Foggy,
			Storm,
			RainHeavy,
			FountainRain,
			SnowLight,
			EastCoast,
		};

		config::Field<TranslatedHotkey> f_Enabled;
		config::Field<TranslatedHotkey> f_Lightning;
		config::Field<config::Enum<WeatherType>> f_WeatherType;
		config::Field<int> f_LightningDelay;    // ms between two strikes (1.6 path only)
		config::Field<int> f_LightningHeight;   // bolt height in world units, 0 = the weather's own value
		config::Field<bool> f_LightningDamage;         // 1.6 only: make the bolt hurt (fall damage, see the tooltip)
		config::Field<float> f_LightningDamageValue;   // damage one bolt deals, flat
		config::Field<bool> f_LightningElectro;        // 1.6 only: a real Electro hit instead of fall damage
		config::Field<float> f_LightningElementGauge;  // Electro gauge one bolt applies (the game uses 80)

		static CustomWeather& GetInstance();

		// Relic: which weather assets a client ships is per game version and the names exist only inside the
		// encrypted bundles, so the only cheap answer is to ask the client. Driven from the Debug feature;
		// the work itself runs on the game thread in OnGameUpdate - never call il2cpp from the render thread.
		void StartAssetProbe();
		bool AssetProbeRunning() const;

		const FeatureGUIInfo& GetGUIInfo() const override;
		void DrawMain() override;
		bool NeedStatusDraw() const override;
		void DrawStatus() override;

	private:
		// What the client answered for the path we last handed ChangeWeather. Missing = this build does not
		// ship that weather asset, which is why the feature could look dead with nothing in the log.
		enum class Availability { Unknown, Present, Missing };

		// What FireStormEffect would do if it were called right now, so the panel can steer the user without
		// hardcoding a weather name. NoLightning = the applied preset has hasLightning clear (the one thing
		// the player can fix by picking another weather); Blocked = one of the game's own preconditions is
		// not met, and the readiness log line names which.
		enum class LightningState { Unknown, NoLightning, Blocked, Armed };

		void OnGameUpdate();
		bool RunAssetProbe(void* Enviro);
		void StrikeNearbyMonsters(void* Enviro);
		CustomWeather();

		std::atomic<Availability> m_Availability{ Availability::Unknown };
		std::atomic<LightningState> m_LightningState{ LightningState::Unknown };
		std::atomic<int> m_ProbeIndex{ -1 };            // -1 = idle, else the tick counter of a running probe
		WeatherType m_ProbeRestore = WeatherType::ClearSky;
	};
}