#pragma once
#include <cheat-base/cheat/Feature.h>
#include <cheat-base/config/config.h>

namespace cheat::feature
{
	// Relic: the credits page only. Upstream also showed a localized "scam warning" dialog on first run
	// and a watermark for 60 s after start; both are gone (see VENDOR.md, "Changes made by Relic").
	class About : public Feature
	{
	public:
		static About& GetInstance();

		const FeatureGUIInfo& GetGUIInfo() const override;
		void DrawMain() override;
	private:
		About();
	};
}
