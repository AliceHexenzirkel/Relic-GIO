#pragma once
#include <cheat-base/cheat/Feature.h>
#include <cheat-base/config/config.h>

namespace cheat::feature
{
	// Relic: "Render Distance" - pushes the game's own streaming / LOD radius, grass draw distance and shadow
	// distance out by one multiplier, past the "Highest" environment-detail preset. Default off. See the .cpp
	// for what each lever is and why Unity's lodBias is not one of them.
	class RenderDistance : public Feature
	{
	public:
		config::Field<bool> f_Enabled;
		config::Field<float> f_Multiplier;
		config::Field<bool> f_World;     // SECTR streaming profile: LOD switch distances + every layer's load radius
		config::Field<bool> f_Hlod;      // the three HLOD ratios of the profile (impostor -> real geometry)
		config::Field<bool> f_Grass;     // MiHoYoGrassGlobalConfigurator: view range + per-LOD distances
		config::Field<float> f_GrassReach;   // SECTR_LayerConfig.loadSize of the TerrainGrass layer, x this
#if RELIC_GAME_VERSION > 16
		config::Field<float> f_GrassView;    // 2.8: the grass view range's own multiplier (the GPU-driven grass of
		                                     // this build culls into a fixed per-frame budget; see the .cpp, lever B)
#endif
		config::Field<bool> f_Shadows;   // QualitySettings.shadowDistance

		static RenderDistance& GetInstance();

		const FeatureGUIInfo& GetGUIInfo() const override;
		void DrawMain() override;

		bool NeedStatusDraw() const override;
		void DrawStatus() override;

		void OnGameUpdate();

	private:
		RenderDistance();
	};
}
