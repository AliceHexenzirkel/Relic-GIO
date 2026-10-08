#include "pch-il2cpp.h"
#include "About.h"

#include <cheat/game/util.h>

namespace cheat::feature
{
	About::About() : Feature()
	{
	}

	const FeatureGUIInfo& About::GetGUIInfo() const
	{
		TRANSLATED_MODULE_INFO("About");
		return info;
	}

	void About::DrawMain()
	{
		std::optional<ImageLoader::GIFData*> gif = ImageLoader::GetGIF("ANIM_AKEBIBOUNCE");
		if (gif)
		{
			gif.value()->render(ImVec2(ImGui::GetWindowSize().x / 3.5f, ImGui::GetWindowSize().y / 2.5f));
		}

		ImGui::PushTextWrapPos(ImGui::GetCursorPos().x + ImGui::GetWindowSize().x);

		ImGui::TextColored(ImColor(28, 202, 214, 255), "Relic in-game enhancements");
		ImGui::TextWrapped("A build of Akebi GC for the classic client versions Relic installs (1.6, 2.8). "
			"Press F1 to open or close this menu. Everything here is client-side and only affects your own game.");
		ImGui::Spacing();

		ImGui::TextColored(ImColor(28, 202, 214, 255), "Based on Akebi GC (Apache-2.0)");
		ImGui::Text("Founder:");
		ImGui::SameLine();
		ImGui::TextColored(ImColor(0, 102, 255, 255), "Callow");

		ImGui::Text("Main developer and updater:");
		ImGui::SameLine();
		ImGui::TextColored(ImColor(0, 102, 255, 255), "Taiga74164");

		ImGui::Text("Main contributors:");
		ImGui::TextColored(ImColor(0, 102, 255, 255), "RyujinZX, WitchGod, m0nkrel, harlanx, andiabrudan, hellomykami, NctimeAza, FawazTakhji, RedDango, RainAfterDark");

		ImGui::Text("Source of this build:");
		TextURL("NctimeAza/AnimeGame-Cheat-3.3 v1.2.3", "https://github.com/NctimeAza/AnimeGame-Cheat-3.3", true, false);

		ImGui::Text("Third-party components: Dear ImGui, Microsoft Detours, {fmt}, nlohmann/json, magic_enum, SimpleIni, stb_image, WinReg, imgui-notify, Font Awesome, Ruda font.");
		ImGui::PopTextWrapPos();
	}

	About& About::GetInstance()
	{
		static About instance;
		return instance;
	}
}
