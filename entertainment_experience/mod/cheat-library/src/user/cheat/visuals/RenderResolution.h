#pragma once
#include <cheat-base/cheat/Feature.h>
#include <cheat-base/config/config.h>

namespace cheat::feature
{
	class RenderResolution : public Feature
	{
	public:
		config::Field<bool> f_Enabled;
		config::Field<float> f_Scale;
		config::Field<bool> f_AllowExtreme;

		static RenderResolution& GetInstance();

		const FeatureGUIInfo& GetGUIInfo() const override;
		void DrawMain() override;

		bool NeedStatusDraw() const override;
		void DrawStatus() override;

		void OnGameUpdate();

	private:
		RenderResolution();
	};
}
