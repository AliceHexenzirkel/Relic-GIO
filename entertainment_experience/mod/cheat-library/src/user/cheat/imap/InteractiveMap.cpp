#include "pch-il2cpp.h"
#include "InteractiveMap.h"

#include <helpers.h>
#include <cheat/game/EntityManager.h>
#include <cheat/game/util.h>
#include <cheat-base/render/renderer.h>
#include <cheat-base/relic-guard.h>
#include <cheat/game/filters.h>
#include <cheat/events.h>
#include <cheat/game/CacheFilterExecutor.h>
#include <cheat/GenshinCM.h>
#include <set>
#include <algorithm>   // std::clamp for the dynamic icon size
#include <cfloat>      // FLT_MAX for the explore grids' bounds (Windows.h's max macro rules numeric_limits out)
#include <cmath>
#include <cstring>     // std::strlen in LabelIcon
#include <optional>


#define IMGUI_DEFINE_MATH_OPERATORS
#include "imgui_internal.h"
#include <misc/cpp/imgui_stdlib.h>

#include "cheat-base/cheat/CheatManagerBase.h"

namespace cheat::feature
{

	InteractiveMap::InteractiveMap() : Feature(),
		NFP(f_Enabled, "InteractiveMap", "Interactive map", false),
		NF(f_SeparatedWindows, "InteractiveMap", true),
		NF(f_ShowMaterialsWindow, "InteractiveMap", false),
		NF(f_CompletionLogShow, "InteractiveMap", false),

		NFS(f_STFixedPoints, "InteractiveMap", SaveAttachType::Global),
		NFS(f_STCustomPoints, "InteractiveMap", SaveAttachType::Global),
		NFS(f_STCompletedPoints, "InteractiveMap", SaveAttachType::Account),

		NF(f_IconSize, "InteractiveMap", 20.0f),
		NF(f_MinimapIconSize, "InteractiveMap", 14.0f),
		NF(f_DynamicSize, "InteractiveMap", false),
		NF(f_ShowHDIcons, "InteractiveMap", false),

		NF(f_ShowCompleted, "InteractiveMap", false),
		NF(f_CompletePointTransparency, "InteractiveMap", 0.5f),
		NF(f_ShowInCompleted, "InteractiveMap", true),
		NF(f_InCompletePointTransparency, "InteractiveMap", 1.0f),

		NF(f_AutoDetectNewItems, "InteractiveMap", true),
		NF(f_AutoFixItemPositions, "InteractiveMap", true),
		NF(f_ObjectCheckOnlyShowed, "InteractMap", true),
		NF(f_ObjectDetectRange, "InteractiveMap", 20.0f),
		NF(f_CheckObjectsDelay, "InteractiveMap", 2000),

		NF(f_AutoDetectGatheredItems, "InteractiveMap", true),
		NF(f_GatheredItemsDetectRange, "InteractiveMap", 20.0f),

		NF(f_CompleteNearestPoint, "InteractiveMap", Hotkey()),
		NF(f_RevertLatestCompletion, "InteractiveMap", Hotkey()),
		NF(f_CompleteOnlyViewed, "InteractiveMap", true),
		NF(f_PointFindRange, "InteractiveMap", 30.0f),

		NF(f_ToggleKey, "InteractiveMap", Hotkey()),   // Relic: no default keys - the players pick theirs in the F1 panel
		NF(f_NotifyAutoMarks, "InteractiveMap", true),
		NF(f_ExploredOnly, "InteractiveMap", false),
		NF(f_ExploredRadius, "InteractiveMap", 80.0f),

		NFS(f_CustomPointIndex, "InteractiveMap", 1000000),
		NFS(f_LastUserID, "InteractiveMap", 0),

		m_HoveredPoint(nullptr)
	{
		// Initializing
		LoadScenesData();
		LoadMaterialFilterData();
		ApplyScaling();

		// --Loading user data
		CreateUserDataField("custom_points", f_CustomPointsJson, f_STCustomPoints.value());
		CreateUserDataField("completed_points", f_CompletedPointsJson, f_STCompletedPoints.value());
		CreateUserDataField("fixed_points", f_FixedPointsJson, f_STFixedPoints.value());
		// Relic: the explored ground belongs with the completed points - same scope, moved and switched with them.
		CreateUserDataField("explored_ground", f_ExploredJson, f_STCompletedPoints.value());

		LoadCustomPoints();
		LoadCompletedPoints();
		LoadFixedPoints();

		InitExploreGrids();   // after the points are in place: a grid spans its dataset's extent
		LoadExplored();
		RebuildExploreGrids();

		InitializeEntityFilters();
		InitializeGatherDetectItems();

		// Eventing
		cheat::events::GameUpdateEvent += MY_METHOD_HANDLER(InteractiveMap::OnGameUpdate);
		::events::WndProcEvent += MY_METHOD_HANDLER(InteractiveMap::OnWndProc);

		cheat::events::AccountChangedEvent += MY_METHOD_HANDLER(InteractiveMap::OnAccountChanged);
		config::ProfileChanged += MY_METHOD_HANDLER(InteractiveMap::OnConfigProfileChanged);

		// Relic: the keys only post a request here, on the window thread; OnKeysUpdate serves it on the game thread
		// (upstream read the avatar's position from the window thread, and the press gave no sign of what it did).
		// A handler of its own, so a fault there cannot silence OnGameUpdate's CheckObjects, nor the other way round.
		f_ToggleKey.value().PressedEvent += MY_METHOD_HANDLER(InteractiveMap::OnToggleKey);
		f_CompleteNearestPoint.value().PressedEvent += MY_METHOD_HANDLER(InteractiveMap::OnMarkKey);
		f_RevertLatestCompletion.value().PressedEvent += MY_METHOD_HANDLER(InteractiveMap::OnUndoKey);
		cheat::events::GameUpdateEvent += MY_METHOD_HANDLER(InteractiveMap::OnKeysUpdate);

		// Hooking
		INSTALL_HOOK(app::MonoMiniMap_Update, InteractiveMap::MonoMiniMap_Update_Hook);
		INSTALL_HOOK(app::MoleMole_GadgetModule_OnGadgetInteractRsp, InteractiveMap::GadgetModule_OnGadgetInteractRsp_Hook);
		INSTALL_HOOK(app::MoleMole_InLevelMapPageContext_UpdateView, InteractiveMap::InLevelMapPageContext_UpdateView_Hook);
		INSTALL_HOOK(app::MoleMole_InLevelMapPageContext_ZoomMap, InteractiveMap::InLevelMapPageContext_ZoomMap_Hook);
	}

	const FeatureGUIInfo& InteractiveMap::GetGUIInfo() const
	{
		// Relic: a group of its own - without one the panel's controls sit loose among World's ungrouped switches.
		TRANSLATED_GROUP_INFO("Interactive Map", "World");
		return info;
	}

	void InteractiveMap::DrawMain()
	{
		// Relic: upstream draws nothing here, so the only way to find this feature was to open the in-game map
		// and notice the windows that appear beside it - and it starts switched off. The switch belongs in the menu,
		// and so do the keys: upstream showed them only in the map's own window, which exists only while the map is
		// shown - the one state a player who explores with the map hidden never sees.
		ConfigWidget(_TR("Show on the map"), f_Enabled,
			_TR("The icons on the map and the minimap, and the map's own windows.\n"
				"Off: nothing is drawn, but marking - by key or automatic - keeps working."));
		ImGui::Spacing();
		DrawMarkingKeys();
		ConfigWidget(_TR("Mark chests, oculi and conches automatically"), f_AutoDetectGatheredItems,
			_TR("Marks a chest when you open it and an oculus or a conch when you pick it up - also while the map is hidden.\n"
				"The kinds with a blue line in the filter list support this."));
		ConfigWidget(_TR("Show a message for automatic marks"), f_NotifyAutoMarks);
		ImGui::Spacing();
		DrawExplorationSettings();
		ImGui::Spacing();
		ImGui::TextWrapped("%s", _TR("Open the in-game map to pick what is tracked: the filter windows appear next to it."));
		ImGui::TextWrapped("%s", _TR("To explore without spoilers: bind the keys above, hide the map and press Mark at whatever you finish - "
			"opened chests and collected oculi count by themselves. Show the map again to see what you missed."));
	}

	// Relic: upstream draws every bounded number as a DragFloat with a 0.01 "speed", which needs roughly
	// 9600 pixels of dragging to cross a 4..100 range - it reads as a control that does nothing. A slider
	// jumps to where you click and still allows ctrl+click to type an exact value.
	static bool ConfigSlider(const char* label, config::Field<float>& field, float min, float max,
		const char* format = "%.1f", const char* desc = nullptr)
	{
		bool changed = ImGui::SliderFloat(label, &field.value(), min, max, format, ImGuiSliderFlags_AlwaysClamp);
		if (changed)
			field.FireChanged();
		if (desc != nullptr) { ImGui::SameLine(); HelpMarker(desc); }
		return changed;
	}

	// Relic: icon resources are named after the clear name, and rc.exe only accepts ASCII identifiers - an Akebi
	// clear name with a Unicode Roman numeral ("SealLocation\xE2\x85\xA0") could never find its icon. Fold those
	// to ASCII first (tools/make_hd_icons.py names the HD resources the same way). With `hd` set, ImageLoader
	// falls back to the normal icon when a label has no HD version.
	static std::optional<ImageLoader::ImageData> LabelIcon(const std::string& clearName, bool hd)
	{
		static const std::pair<const char*, const char*> kRoman[] = {
			{ "\xE2\x85\xA0", "I" }, { "\xE2\x85\xA1", "II" }, { "\xE2\x85\xA2", "III" },
			{ "\xE2\x85\xA3", "IV" }, { "\xE2\x85\xA4", "V" },
		};
		std::string name = clearName;
		for (const auto& [from, to] : kRoman)
			for (size_t at = name.find(from); at != std::string::npos; at = name.find(from, at))
				name.replace(at, std::strlen(from), to);
		return ImageLoader::GetImage(hd ? "HD" + name : name);
	}

	// Relic: the marking keys and their options - drawn in the F1 panel and in the map's own window alike.
	void InteractiveMap::DrawMarkingKeys()
	{
		ConfigWidget(_TR("Show / hide"), f_ToggleKey, true,
			_TR("Shows or hides the map's icons and windows.\nMarking - by key or automatic - keeps working while the map is hidden."));
		ConfigWidget(_TR("Mark as done"), f_CompleteNearestPoint, true,
			_TR("Marks the nearest thing you track as done: a chest, an oculus, a puzzle, a challenge...\n"
				"Plants, ores, animals and enemies come back, so they are picked only when nothing else is in range.\n"
				"When the nearest one was marked a moment ago the key says so - press it again to mark the next one."));
		ConfigWidget(_TR("Undo last mark"), f_RevertLatestCompletion, true,
			_TR("Takes back the latest mark, whatever made it.\n"
				"The next Mark then skips that point once, so a wrong pick is fixed with Undo, then Mark."));
		ConfigWidget(_TR("Only what you track"), f_CompleteOnlyViewed,
			_TR("Mark only points of the filters you have switched on (what the map shows).\nOff: any point in the map data."));
		ConfigSlider(_TR("Search range (m)"), f_PointFindRange, 0.0f, 200.0f, "%.0f",
			_TR("How far from your character the point may be. 0 = no limit."));
		ImGui::TextDisabled("%s", _TR("A letter key does nothing while the chat or a game menu is open - except on the map."));
	}

	// Relic: "Only where you have been" - drawn in the F1 panel and in the map's own window alike (render thread).
	void InteractiveMap::DrawExplorationSettings()
	{
		ConfigWidget(_TR("Only where you have been"), f_ExploredOnly,
			_TR("While this is on, the map remembers the ground your character walks over (saved with the completed points).\n"
				"When the map is shown it draws unfinished points only near that ground, so what you have not explored yet\n"
				"stays a surprise. Finished points follow 'Show completed' as before."));
		ConfigSlider(_TR("Reveal radius (m)"), f_ExploredRadius, mapprogress::kExploreMinRadius, mapprogress::kExploreMaxRadius, "%.0f",
			_TR("How far from the ground you walked an unfinished point is still shown."));

		const uint32_t sceneID = game::GetCurrentPlayerSceneID();
		if (m_ExploreGrids.count(sceneID) == 0)
		{
			ImGui::TextDisabled("%s", _TR("The map has no data for this place."));
			return;
		}

		ImGui::Text("%s %.2f km\xC2\xB2", _TR("Explored here:"), mapprogress::ExploredKm2(ExploredCells(sceneID)));
		ImGui::SameLine();
		if (ImGui::SmallButton(_TR("Forget...")))
			ImGui::OpenPopup("ForgetExploredGround");
		if (ImGui::BeginPopup("ForgetExploredGround"))
		{
			ImGui::TextUnformatted(_TR("Forget the ground you explored in this area?"));
			if (ImGui::Button(_TR("Forget it")))
			{
				ForgetExplored(sceneID);
				ImGui::CloseCurrentPopup();
			}
			ImGui::SameLine();
			if (ImGui::Button(_TR("Cancel")))
				ImGui::CloseCurrentPopup();
			ImGui::EndPopup();
		}
	}

	void InteractiveMap::DrawMenu()
	{
		ImGui::BeginGroupPanel(Translator::RuntimeTranslate("General").c_str());
		{
			ConfigWidget(_TR("Show on the map"), f_Enabled);
			ConfigWidget(_TR("Separated windows"), f_SeparatedWindows, _TR("Config and filters will be in separate windows."));
			if (ConfigWidget(_TR("Save completed points"), f_STCompletedPoints, _TR("Save scope for completed items.\n"
				"The ground 'Only where you have been' remembers moves with them.")))
			{
				UpdateUserDataField(f_CompletedPointsJson, f_STCompletedPoints.value(), true);
				SaveExplored();   // what is in memory goes with the field
				RepositionExploredField(f_STCompletedPoints.value(), true);
			}
		}
		ImGui::EndGroupPanel();

		ImGui::BeginGroupPanel(Translator::RuntimeTranslate("Icon view").c_str());
		{
			ConfigSlider(_TR("Icon size"), f_IconSize, 8.0f, 96.0f);
			ConfigSlider(_TR("Minimap icon size"), f_MinimapIconSize, 8.0f, 64.0f);
			ConfigWidget(_TR("Dynamic size"), f_DynamicSize, _TR("Grow and shrink the map icons with the zoom level.\nKept between half and double the size above, so zooming out cannot shrink them away.\nMinimap icons are not affected."));
			ConfigWidget(_TR("Show HD icons"), f_ShowHDIcons, _TR("Use the high-resolution icons: sharper when the icons are drawn large.\nIcons without an HD version keep the normal one."));
		}
		ImGui::EndGroupPanel();

		ImGui::BeginGroupPanel(Translator::RuntimeTranslate("In/Completed icon view").c_str());
		{
			ConfigWidget(_TR("Show completed"), f_ShowCompleted, _TR("Show completed points."));
			ConfigSlider(_TR("Completed point transparency"), f_CompletePointTransparency, 0.0f, 1.0f, "%.2f", _TR("Completed points transparency."));
			ConfigWidget(_TR("Show in-completed"), f_ShowInCompleted, _TR("Show in-completed points."));
			ConfigSlider(_TR("In-completed point transparency"), f_InCompletePointTransparency, 0.0f, 1.0f, "%.2f", _TR("In-completed points transparency."));
		}
		ImGui::EndGroupPanel();

		ImGui::BeginGroupPanel(Translator::RuntimeTranslate("Item adjusting").c_str());
		{
			ConfigWidget(_TR("Fix item positions"), f_AutoFixItemPositions, _TR("Do fix positions to nearest to point.\n"
				"Only items with green line support this function."));

			ConfigWidget(_TR("Detect new items"), f_AutoDetectNewItems, _TR("Enables detecting items what are not in interactive map data.\n"
				"Only items with green line support this function."));

			ConfigWidget(_TR("Detect only showed"), f_ObjectCheckOnlyShowed, _TR("Detect objects only for showed filters."));

			ConfigSlider(_TR("Detect range"), f_ObjectDetectRange, 5.0f, 30.0f, "%.1f",
				_TR("Fix positions: Only if item was found in this range about entity position,\n\t its position will be fixed.\n"
				"New item detecting: Only if item not found in this range about entity position,\n\t it be detected as new."
			));

			ConfigWidget(_TR("Detect delay (ms)"), f_CheckObjectsDelay, 10, 100, 100000, _TR("Adjusting items is power consumption operation.\n"
				"So rescanning will happen with specified delay."));
		}
		ImGui::EndGroupPanel();

		ImGui::BeginGroupPanel(Translator::RuntimeTranslate("Gather detecting").c_str());
		{
			ConfigWidget(_TR("Detect gathered items"), f_AutoDetectGatheredItems, _TR("Enables detecting gathered items.\n"
				"It works only items what will be gathered after enabling this function.\n"
				"Only items with blue line support this function."));

			ConfigSlider(_TR("Detect range"), f_GatheredItemsDetectRange, 5.0f, 30.0f, "%.1f",
				_TR("When entity was gathered finding nearest point in this range."));

			ConfigWidget(_TR("Show a message for automatic marks"), f_NotifyAutoMarks);
		}
		ImGui::EndGroupPanel();

		ImGui::BeginGroupPanel(Translator::RuntimeTranslate("Marking").c_str());
		{
			DrawMarkingKeys();
		}
		ImGui::EndGroupPanel();

		ImGui::BeginGroupPanel(Translator::RuntimeTranslate("Exploration").c_str());
		{
			DrawExplorationSettings();
		}
		ImGui::EndGroupPanel();
	}

	void InteractiveMap::DrawMaterialFilters()
	{
		ImGui::BeginTabBar("#TypesTabs", ImGuiTabBarFlags_None);
		for (auto& [type, data] : m_MaterialData)
		{
			if (ImGui::BeginTabItem(Translator::RuntimeTranslate(util::MakeCapital(type)).c_str()))
			{
				for (auto& category : data.categories)
					DrawMaterialFilterCategories(category, type);

				ImGui::EndTabItem();
			}
		}

		ImGui::EndTabBar();
	}

	void InteractiveMap::DrawMaterialFilterCategories(MaterialCategoryData& category, std::string type)
	{
		bool checked = std::all_of(category.children.begin(), category.children.end(), [](MaterialData* matData) {  return matData->selected; });
		bool changed = false;
		if (ImGui::BeginSelectableGroupPanel(Translator::RuntimeTranslate(category.name).c_str(), checked, changed, true))
		{
			int columns = 3;
			if (ImGui::BeginTable(category.name.c_str(), columns))
			{
				uint32_t i = 0;
				for (auto& child : category.children)
				{
					if (i % columns == 0)
					{
						ImGui::TableNextRow();
						ImGui::TableSetColumnIndex(0);
					}
					else ImGui::TableNextColumn();

					ImGui::PushID(child);
					DrawMaterialFilter(child, type);
					ImGui::PopID();
					i++;
				}
				ImGui::EndTable();
			}
		}
		ImGui::EndSelectableGroupPanel();

		if (changed)
		{
			for (const auto& material : category.children)
			{
				material->selected.value() = checked;
				material->selected.FireChanged();
			}
		}
	}

	void InteractiveMap::DrawMaterialFilter(MaterialData* material, std::string type)
	{
		ImGuiWindow* window = ImGui::GetCurrentWindow();
		if (window->SkipItems)
			return;

		const ImGuiStyle& style = ImGui::GetStyle();

		// Relic: the character/weapon art exists only in the HD icon set. When a name has no art, draw a plain
		// checkbox instead of an empty 50x50 art box with the name clipped outside the window.
		auto art = LabelIcon(material->clearName, true);   // the materials window only has HD art
		if (!art)
		{
			bool selected = material->selected;
			if (ImGui::Checkbox(Translator::RuntimeTranslate(material->name).c_str(), &selected))
				material->selected = selected;
			return;
		}

		// Image Box
		ImVec2 box_sz = ImVec2(50, 50);
		ImVec2 pos_min = ImGui::GetCursorScreenPos();
		ImVec2 pos_max = pos_min + box_sz;

		// Text
		const ImVec2 textSize = ImGui::CalcTextSize(Translator::RuntimeTranslate(material->name).c_str(), nullptr, true);
		ImVec2 textPos = ImVec2(pos_max.x + style.FramePadding.x, pos_min.y + (box_sz.y / 2) - (textSize.y / 2));

		// Widget
		ImGui::InvisibleButton(("##" + material->clearName).c_str(), box_sz, ImGuiButtonFlags_MouseButtonLeft);
		bool itemHovered = ImGui::IsItemHovered();
		bool itemClicked = ImGui::IsItemActive() && ImGui::IsItemClicked(0);

		if(material->selected)
			window->DrawList->AddRectFilled(pos_min, pos_max, ImGui::GetColorU32(ImGuiCol_CheckMark), 10.f);

		auto image = art;
		if (image)
			window->DrawList->AddImageRounded(image->textureID, pos_min, pos_max, ImVec2(0.0f, 0.0f), ImVec2(1.0f, 1.0f), IM_COL32_WHITE, 10.f);
		else
			window->DrawList->AddRectFilled(pos_min, pos_max, ImGui::GetColorU32(ImGuiCol_FrameBg), 10.f);

		window->DrawList->AddText(textPos, ImGui::GetColorU32(ImGuiCol_Text), Translator::RuntimeTranslate(material->name).c_str());

		if (itemClicked)
			material->selected = !material->selected;

		if(material->selected || itemHovered)
			window->DrawList->AddRect(pos_min, pos_max, ImGui::GetColorU32(ImGuiCol_CheckMark), 10.f);

		// Center checkmark
		//auto check_sz = box_sz.x / 4;
		//if (material->selected)
		//{
		//	window->DrawList->AddCircleFilled(pos_min + (box_sz / 2), check_sz * 0.75, ImGui::GetColorU32(ImGuiCol_CheckMark), 20);
		//	ImGui::RenderCheckMark(window->DrawList, pos_min + ((box_sz - ImVec2(check_sz, check_sz)) / 2), ImGui::GetColorU32(ImGuiCol_FrameBgActive), check_sz);
		//}
	}

	void InteractiveMap::DrawMaterials(uint32_t sceneID)
	{
		auto& labels = m_ScenesData[sceneID].labels;
		std::map<std::string, std::set<LabelData*>> materialLabels = { {"character", {}}, {"weapon", {}} };
		for (auto& [type, list] : materialLabels)
		{
			for (auto& [charID, character] : m_MaterialData[type].materials)
				if (character.selected)
					for (auto materialID : character.filter)
						if(labels.count(materialID) > 0) // Depends on sceneID
							list.insert(&labels[materialID]);
		}

		for (auto& [type, materials] : materialLabels)
		{
			if (materials.empty())
				continue;

			bool checked = std::all_of(materials.begin(), materials.end(), [](const LabelData* label) { return label->enabled; });
			bool changed = false;

			if (ImGui::BeginSelectableGroupPanel(Translator::RuntimeTranslate(util::MakeCapital(type) + " Filters").c_str(), checked, changed, true))
			{
				if (ImGui::BeginTable(("##" + util::MakeCapital(type) + "Table").c_str(), 3))
				{
					for (const auto& label : materials)
					{
						ImGui::TableNextColumn();
						ImGui::PushID(label);
						DrawFilter(*label);
						ImGui::PopID();
					}
					ImGui::EndTable();
				}
			}
			ImGui::EndSelectableGroupPanel();

			if (changed)
			{
				for (const auto& label : materials)
				{
					label->enabled = checked;
				}
			}
		}
	}

	void InteractiveMap::DrawFilters(const bool searchFixed)
	{
		const auto sceneID = game::GetCurrentMapSceneID();
		if (m_ScenesData.count(sceneID) == 0)
		{
			// Relic: return instead of falling through - every m_ScenesData[sceneID] below would insert an
			// empty scene, after which the map counts as supported-but-empty and the warning never shows again.
			ImGui::Text(_TR("Sorry. Current scene is not supported."));
			return;
		}

		ImGui::BeginGroupPanel(Translator::RuntimeTranslate("Ascension Materials Filter").c_str());
		{
			ConfigWidget(_TR("Show Ascension Materials"), f_ShowMaterialsWindow, _TR("Open ascension materials filter window"));
			DrawMaterials(sceneID);
		}
		ImGui::EndGroupPanel();

		// Relic: with "Only where you have been" on, say how much of this area counts as seen - an empty map right after
		// switching it on is the option working, not the map failing.
		if (f_ExploredOnly && m_ExploreGrids.count(sceneID) > 0)
		{
			const size_t cells = ExploredCells(sceneID);
			if (cells == 0)
				ImGui::TextWrapped("%s", _TR("Only where you have been: nothing explored here yet - unfinished points show up once you have walked near them."));
			else
				ImGui::TextWrapped("%s %.2f km\xC2\xB2", _TR("Only where you have been - explored here:"), mapprogress::ExploredKm2(cells));
		}

		ImGui::InputText(_TR("Search"), &m_SearchText); ImGui::SameLine();
		HelpMarker(
			_TR("This page following with filters for items.\n"
			"Items what was activated will be appear on mini/global map. (Obviously)\n"
			"Each filter have options, you can access to it by clicking RMB on filter.\n"
			"Filters can be marked with colored lines,\n"
			"\tthey indicate that filter support some features. (Hover it)\n"
			"Thats all for now. Happy using ^)"
		));

		if (searchFixed)
			ImGui::BeginChild("FiltersList", ImVec2(-1, 0), false, ImGuiWindowFlags_NoBackground);

		auto& categories = m_ScenesData[sceneID].categories;
		for (auto& [categoryName, labels] : categories)
		{
			std::vector<LabelData*> validLabels;

			if (m_SearchText.empty())
			{
				validLabels = labels;
			}
			else
			{
				for (auto& label : labels)
				{
					std::string name = label->name;
					std::transform(name.begin(), name.end(), name.begin(), ::tolower);
					std::string search = m_SearchText;
					std::transform(search.begin(), search.end(), search.begin(), ::tolower);
					if (name.find(search) != std::string::npos)
						validLabels.push_back(label);
				}
			}

			if (validLabels.empty())
				continue;

			bool checked = std::all_of(validLabels.begin(), validLabels.end(), [](const LabelData* label) { return label->enabled; });
			bool changed = false;

			if (ImGui::BeginSelectableGroupPanel(Translator::RuntimeTranslate(categoryName).c_str(), checked, changed, true))
			{
				if (ImGui::BeginTable("MarkFilters", 2))
				{
					for (const auto& label : validLabels)
					{
						ImGui::TableNextColumn();
						ImGui::PushID(label);
						DrawFilter(*label);
						ImGui::PopID();
					}
					ImGui::EndTable();
				}
			}
			ImGui::EndSelectableGroupPanel();

			if (changed)
			{
				for (const auto& label : validLabels)
				{
					label->enabled = checked;
				}
			}
			
		}

		if (searchFixed)
			ImGui::EndChild();
	}

	// Modified ImGui::CheckBox
	void InteractiveMap::DrawFilter(LabelData& label)
	{
		ImGuiWindow* window = ImGui::GetCurrentWindow();
		if (window->SkipItems)
			return;

		ImGuiContext& g = *GImGui;
		const ImGuiStyle& style = g.Style;
		const ImGuiID id = window->GetID(&label);
		const ImVec2 label_size = ImGui::CalcTextSize(Translator::RuntimeTranslate(label.name).c_str(), nullptr, true);

		const float square_sz = ImGui::GetFrameHeight();
		const float image_sz = square_sz;

		const bool haveFilter = label.filter != nullptr;
		const bool haveGatherDetect = label.supportGatherDetect;

		float markWidth = 5.0f;
		float marksSize = 0.0f;
		float halfSpacing = style.ItemInnerSpacing.x / 2;

		if (haveFilter || haveGatherDetect)
			marksSize += halfSpacing;

		if (haveFilter)
			marksSize += halfSpacing + markWidth;

		if (haveGatherDetect)
			marksSize += halfSpacing + markWidth;

		std::string progress_text = fmt::format("{}/{}", label.completedCount, label.points.size());
		const ImVec2 progress_text_size = ImGui::CalcTextSize(progress_text.c_str());

		const ImVec2 pos = window->DC.CursorPos;
		const ImRect total_bb(pos,
			pos + ImVec2(square_sz + style.ItemInnerSpacing.x + image_sz + marksSize + style.ItemInnerSpacing.x + progress_text_size.x + style.ItemInnerSpacing.x +
				(label_size.x > 0.0f ? style.ItemInnerSpacing.x + label_size.x : 0.0f),
				label_size.y + style.FramePadding.y * 2.0f));
		ImGui::ItemSize(total_bb, style.FramePadding.y);

		if (!ImGui::ItemAdd(total_bb, id))
		{
			IMGUI_TEST_ENGINE_ITEM_INFO(id, label, g.LastItemData.StatusFlags | ImGuiItemStatusFlags_Checkable | (*v ? ImGuiItemStatusFlags_Checked : 0));
			return;
		}

		bool hovered, held;
		bool pressed = ImGui::ButtonBehavior(total_bb, id, &hovered, &held);
		if (pressed)
		{
			label.enabled = !label.enabled;

			ImGui::MarkItemEdited(id);
		}

		const ImRect check_bb(pos, pos + ImVec2(square_sz, square_sz));
		ImGui::RenderNavHighlight(total_bb, id);
		ImGui::RenderFrame(check_bb.Min, check_bb.Max, ImGui::GetColorU32((held && hovered) ? ImGuiCol_FrameBgActive : hovered ? ImGuiCol_FrameBgHovered : ImGuiCol_FrameBg), true, style.FrameRounding);
		ImU32 check_col = ImGui::GetColorU32(ImGuiCol_CheckMark);
		bool mixed_value = (g.LastItemData.InFlags & ImGuiItemFlags_MixedValue) != 0;
		if (mixed_value)
		{
			// Undocumented tristate/mixed/indeterminate checkbox (#2644)
			// This may seem awkwardly designed because the aim is to make ImGuiItemFlags_MixedValue supported by all widgets (not just checkbox)
			ImVec2 pad(ImMax(1.0f, IM_FLOOR(square_sz / 3.6f)), ImMax(1.0f, IM_FLOOR(square_sz / 3.6f)));
			window->DrawList->AddRectFilled(check_bb.Min + pad, check_bb.Max - pad, check_col, style.FrameRounding);
		}
		else if (label.enabled.value())
		{
			const float pad = ImMax(1.0f, IM_FLOOR(square_sz / 6.0f));
			ImGui::RenderCheckMark(window->DrawList, check_bb.Min + ImVec2(pad, pad), check_col, square_sz - pad * 2.0f);
		}

		// --
		const ImVec2 image_pos(check_bb.Max.x + style.ItemInnerSpacing.x, check_bb.Min.y);
		const ImRect image_bb(image_pos, image_pos + ImVec2(image_sz, image_sz));

		auto image = LabelIcon(label.clearName, false);
		if (image)
		{
			window->DrawList->AddImageRounded(image->textureID, image_bb.Min, image_bb.Max,
				ImVec2(0.0f, 0.0f), ImVec2(1.0f, 1.0f), ImColor(255, 255, 255), image_sz / 4);
		}
		// --

		// --
		float cursorX = image_bb.Max.x;
		bool markHovered = false;
		if (marksSize != 0.0f)
		{
			ImVec2 mark_pos = ImVec2(cursorX + halfSpacing, image_bb.Min.y + style.FramePadding.y);
			ImVec2 mark_size = ImVec2(markWidth, image_bb.Max.y - image_bb.Min.y - 2 * style.FramePadding.y);
			
			if (haveFilter)
			{ 
				ImRect mark_bb = { mark_pos, mark_pos + mark_size };
				if (ImGui::IsMouseHoveringRect(mark_bb.Min, mark_bb.Max))
				{
					markHovered = true;
					ShowHelpText(Translator::RuntimeTranslate("New items detect supported").c_str());
				}

				ImGui::RenderFrame(mark_bb.Min, mark_bb.Max, ImColor(0.0f, 1.0f, 0.0f), false, 3.0f);
				mark_pos.x += markWidth + halfSpacing;
			}

			if (haveGatherDetect)
			{
				ImRect mark_bb = { mark_pos, mark_pos + mark_size };
				if (ImGui::IsMouseHoveringRect(mark_bb.Min, mark_bb.Max))
				{
					markHovered = true;
					ShowHelpText(Translator::RuntimeTranslate("Gather detect supported").c_str());
				}

				ImGui::RenderFrame(mark_bb.Min, mark_bb.Max, ImColor(0.0f, 0.0f, 1.0f), false, 3.0f);
				mark_pos.x += markWidth + halfSpacing;
			}

			cursorX = mark_pos.x;
		}
		// --
		
		// --
		ImVec2 label_progress_pos = ImVec2(cursorX + style.ItemInnerSpacing.x, image_bb.Min.y + style.FramePadding.y);
		ImGui::RenderText(label_progress_pos, progress_text.c_str());

		cursorX += style.ItemInnerSpacing.x + progress_text_size.x;
		// --		

		ImVec2 label_pos = ImVec2(cursorX + style.ItemInnerSpacing.x, image_bb.Min.y + style.FramePadding.y);
		if (g.LogEnabled)
			ImGui::LogRenderedText(&label_pos, mixed_value ? "[~]" : label.enabled.value() ? "[x]" : "[ ]");
		if (label_size.x > 0.0f)
			ImGui::RenderText(label_pos, Translator::RuntimeTranslate(label.name).c_str());

		if (!markHovered && ImGui::IsItemHovered())
			ShowHelpText(Translator::RuntimeTranslate(label.name).c_str());

		// -- Filter options
		if (ImGui::IsItemClicked(ImGuiMouseButton_Right))
			ImGui::OpenPopup("Filter options");

		if (ImGui::BeginPopup("Filter options", ImGuiWindowFlags_AlwaysAutoResize))
		{
			if (ImGui::Button(Translator::RuntimeTranslate("Drop progress").c_str()))
			{
				for (auto& [pointID, point] : label.points)
				{
					if (point.completed)
						UncompletePoint(&point);
				}
				ImGui::CloseCurrentPopup();
			}
			if (ImGui::Button(Translator::RuntimeTranslate("Complete progress").c_str()))
			{
				for (auto& [pointID, point] : label.points)
				{
					CompletePoint(&point);
				}
				ImGui::CloseCurrentPopup();
			}

			ImGui::EndPopup();
		}
		// --

		IMGUI_TEST_ENGINE_ITEM_INFO(id, label, g.LastItemData.StatusFlags | ImGuiItemStatusFlags_Checkable | (*v ? ImGuiItemStatusFlags_Checked : 0));
		return;
	}

	InteractiveMap& InteractiveMap::GetInstance()
	{
		static InteractiveMap instance;
		return instance;
	}

	InteractiveMap::PointData* InteractiveMap::GetHoveredPoint()
	{
		std::lock_guard<std::mutex> _guard(m_PointMutex);
		return m_HoveredPoint;
	}


	std::vector<InteractiveMap::PointData*> InteractiveMap::GetEntityPoints(game::Entity* entity, bool completed /*= false*/, uint32_t sceneID /*= 0*/)
	{
		sceneID = sceneID == 0 ? game::GetCurrentPlayerSceneID() : sceneID;
		if (m_ScenesData.count(sceneID) == 0)
			return {};

		auto& labels = m_ScenesData[sceneID].labels;

		std::vector<PointData*> points;
		static game::CacheFilterExecutor filterExecutor(2000U);
		for (auto& [labelID, label] : labels)
		{
			if (label.filter == nullptr)
				continue;

			if (!filterExecutor.ApplyFilter(entity, label.filter))
				continue;

			if (completed)
				points.reserve(label.points.size());

			for (auto& [pointID, point] : label.points)
			{
				if (completed || !point.completed)
					points.push_back(&point);
			}
			break;
		}
		return points;
	}

	InteractiveMap::PointData* InteractiveMap::FindNearestPoint(const app::Vector2& levelPosition, float range, bool onlyShowed, bool completed, uint32_t sceneID)
	{
		sceneID = sceneID == 0 ? game::GetCurrentPlayerSceneID() : sceneID;
		if (m_ScenesData.count(sceneID) == 0)
			return nullptr;

		auto& labels = m_ScenesData[sceneID].labels;
		
		PointData* minDistancePoint = nullptr;
		float minDistance = 0;
		for (auto& [labelID, label] : labels)
		{
			if (onlyShowed && !label.enabled)
				continue;

			PointData* nearestLabelPoint = FindNearestPoint(label, levelPosition, range, completed);
			if (nearestLabelPoint == nullptr)
				continue;

			float distance = app::Vector2_Distance(levelPosition, nearestLabelPoint->levelPosition, nullptr);
			if (distance < minDistance || minDistancePoint == nullptr)
			{
				minDistance = distance;
				minDistancePoint = nearestLabelPoint;
			}
		}

		if (minDistancePoint == nullptr || (range > 0 && minDistance > range))
			return nullptr;

		return minDistancePoint;
	}

	InteractiveMap::PointData* InteractiveMap::FindNearestPoint(const LabelData& label, const app::Vector2& levelPosition, float range /*= 0.0f*/, bool completed /*= false*/)
	{
		PointData* minDistancePoint = nullptr;
		float minDistance = 0;
		for (auto& [pointID, point] : label.points)
		{
			if (!completed && point.completed)
				continue;

			float distance = app::Vector2_Distance(levelPosition, point.levelPosition, nullptr);
			if (distance < minDistance || minDistancePoint == nullptr)
			{
				minDistance = distance;
				minDistancePoint = const_cast<PointData*>(&point);
			}
		}

		if (minDistancePoint == nullptr || (range > 0 && minDistance > range))
			return nullptr;

		return minDistancePoint;
	}

	InteractiveMap::PointData* InteractiveMap::FindEntityPoint(game::Entity* entity, float range /*= 20.0f*/, uint32_t sceneID /*= 0*/)
	{
		sceneID = sceneID == 0 ? game::GetCurrentPlayerSceneID() : sceneID;
		if (m_ScenesData.count(sceneID) == 0)
			return nullptr;

		auto& labels = m_ScenesData[sceneID].labels;
		for (auto& [labelID, label] : labels)
		{
			if (label.filter == nullptr)
				continue;

			if (!label.filter->IsValid(entity))
				continue;

			auto nearestPoint = FindNearestPoint(label, entity->levelPosition(), range, false);
			if (nearestPoint == nullptr)
				return nullptr;

			return nearestPoint;
		}

		return nullptr;
	}


	bool InteractiveMap::CompletePoint(PointData* pointData)
	{
		// Relic: nothing in here calls into the game. Upstream logged the avatar's distance under this lock, and a
		// fault the handler guard swallows at that point would never release m_UserDataMutex: the next completion
		// (the interact hook, on the game thread) would then block the game for good.
		std::lock_guard _userDataLock(m_UserDataMutex);

		if (std::find_if(m_CompletedPoints.begin(), m_CompletedPoints.end(), [=](PointData* data) { return pointData->id == data->id; }) != std::end(m_CompletedPoints))
			return false;

		pointData->completed = true;
		pointData->completeTimestamp = util::GetCurrentTimeMillisec();
		m_ScenesData[pointData->sceneID].labels[pointData->labelID].completedCount++;
		m_CompletedPoints.push_back(pointData);
		
		SaveCompletedPoints();
		LOG_INFO("Completed point %u (scene %u, label %u).", pointData->id, pointData->sceneID, pointData->labelID);
		return true;
	}

	void InteractiveMap::UncompletePoint(PointData* pointData)
	{
		std::lock_guard _userDataLock(m_UserDataMutex);

        auto pointDataIterator = std::find_if(m_CompletedPoints.begin(), m_CompletedPoints.end(), [=](PointData* data) { return pointData->id == data->id; });
        if (pointDataIterator == m_CompletedPoints.end())
			return;

		pointData->completed = false;
		pointData->completeTimestamp = 0;
		m_ScenesData[pointData->sceneID].labels[pointData->labelID].completedCount--;
		m_CompletedPoints.erase(pointDataIterator);

		SaveCompletedPoints();
	}

	InteractiveMap::PointData* InteractiveMap::RevertLatestPointCompleting()
	{
		std::lock_guard _userDataLock(m_UserDataMutex);
		if (m_CompletedPoints.empty())
			return nullptr;

        auto pointDataIterator = --m_CompletedPoints.end();
        PointData* pointData = *pointDataIterator;
		pointData->completed = false;
		pointData->completeTimestamp = 0;
		m_ScenesData[pointData->sceneID].labels[pointData->labelID].completedCount--;
		m_CompletedPoints.erase(pointDataIterator);

		SaveCompletedPoints();
		return pointData;
	}

	void InteractiveMap::FixPointPosition(PointData* pointData, app::Vector2 fixedPosition)
	{
		std::lock_guard _userDataLock(m_UserDataMutex);
		if (!pointData->fixed)
		{
			pointData->fixed = true;
			pointData->originPosition = pointData->levelPosition;
			m_FixedPoints.insert(pointData);
		}

		pointData->levelPosition = fixedPosition;
		SaveFixedPoints();
	}

	void InteractiveMap::UnfixPoitnPosition(PointData* pointData)
	{
		std::lock_guard _userDataLock(m_UserDataMutex);
		if (!pointData->fixed)
			return;

		pointData->fixed = false;
		pointData->levelPosition = pointData->originPosition;
		m_FixedPoints.erase(pointData);

		SaveFixedPoints();
	}

	void InteractiveMap::AddCustomPoint(uint32_t sceneID, uint32_t labelID, app::Vector2 levelPosition)
	{
		std::lock_guard<std::mutex> _userDataLock(m_UserDataMutex);
		if (m_ScenesData.count(sceneID) == 0)
			return;

		auto& sceneData = m_ScenesData[sceneID];
		if (sceneData.labels.count(labelID) == 0)
			return;

		auto& points = sceneData.labels[labelID].points;

		// TODO: Fix uint32_t overflow.
		// Callow: I think that will never happen, but who knows, who knows...
		while (points.count(f_CustomPointIndex) > 0)
			f_CustomPointIndex = f_CustomPointIndex + 1;

		auto& newPoint = points[f_CustomPointIndex];
		newPoint.id = f_CustomPointIndex;
		newPoint.custom = true;
		newPoint.creationTimestamp = util::GetCurrentTimeMillisec();
		newPoint.labelID = labelID;
		newPoint.sceneID = sceneID;
		newPoint.levelPosition = levelPosition;
		m_CustomPoints.insert(&newPoint);

		f_CustomPointIndex = f_CustomPointIndex + 1;
		SaveCustomPoints();
	}

	void InteractiveMap::RemoveCustomPoint(PointData* pointData)
	{
		std::lock_guard _userDataLock(m_UserDataMutex);
		if (m_CustomPoints.empty())
			return;

		m_CustomPoints.erase(pointData);
		m_ScenesData[pointData->sceneID].labels[pointData->labelID].points.erase(pointData->id);
		SaveCustomPoints();
	}

	void InteractiveMap::OnGameUpdate()
	{
		// Relic: the big map and the filter list key on MapManager.mapSceneID while completion, the minimap
		// and the hotkey key on PlayerModule.curSceneID. They are the same number in the open world but not
		// for a map layer (the Chasm is a layer of scene 3), so log the pair whenever it changes: that is
		// what says under which id a new dataset has to be registered.
		static uint32_t s_lastPlayerScene = UINT32_MAX;
		static uint32_t s_lastMapScene = UINT32_MAX;
		auto playerScene = f_Enabled->enabled() ? game::GetCurrentPlayerSceneID() : 0;   // two singleton lookups; not worth it while off
		auto mapScene = f_Enabled->enabled() ? game::GetCurrentMapSceneID() : 0;
		if (f_Enabled->enabled() && (playerScene != s_lastPlayerScene || mapScene != s_lastMapScene))
		{
			s_lastPlayerScene = playerScene;
			s_lastMapScene = mapScene;
			LOG_DEBUG("[imap] scene: player %u (dataset %s), map %u (dataset %s)",
				playerScene, m_ScenesData.count(playerScene) > 0 ? "yes" : "no",
				mapScene, m_ScenesData.count(mapScene) > 0 ? "yes" : "no");
		}

		CheckObjects(); // Calling it from game update thread to avoid screen freezes
	}

	// For now this use straightforward method
	// More advanced method description here: https://github.com/CallowBlack/genshin-cheat/issues/176
	void InteractiveMap::CheckObjects()
	{
		UPDATE_DELAY(f_CheckObjectsDelay);

		if (!f_AutoFixItemPositions && !f_AutoDetectNewItems)
			return;

		auto sceneID = game::GetCurrentPlayerSceneID();
		if (m_ScenesData.count(sceneID) == 0)
			return;

		auto& labels = m_ScenesData[sceneID].labels;
		static game::CacheFilterExecutor filterExecutor(2000U);
		//std::lock_guard<std::mutex> _userDataLock(m_UserDataMutex);

		auto& manager = game::EntityManager::instance();

		std::vector<std::pair<LabelData*, std::unordered_set<game::Entity*>>> supportedEntities;
		for (auto& [labelID, label] : labels)
		{
			if (label.filter == nullptr)
				continue;

			if (f_ObjectCheckOnlyShowed && !label.enabled)
				continue;

			auto& [entityId, entities] = supportedEntities.emplace_back(&label, std::unordered_set<game::Entity*>{});
			for (auto& entity : manager.entities())
			{
				if (filterExecutor.ApplyFilter(entity, label.filter))
					entities.insert(entity);
			}

			if (entities.empty())
				supportedEntities.pop_back();
		}

		for (auto& [label, entities] : supportedEntities)
		{
			std::unordered_set<PointData*> pointsSet;
			for (auto& [pointID, point] : label->points)
				pointsSet.insert(&point);
			
			for (auto& entity : entities)
			{
				PointData* nearestPoint = nullptr;
				float minDistance = 0.0f;
				for (auto& point : pointsSet)
				{
					auto distance = entity->distance(point->levelPosition);
					if (nearestPoint == nullptr || distance < minDistance)
					{
						nearestPoint = point;
						minDistance = distance;
					}
				}

				if (nearestPoint == nullptr)
					break;

				// Relic: a chest that belongs to a chest marker - a sealed, buried or rock-covered chest, which the
				// Teyvat data lists only there - is neither a new chest to add nor a reason to move another chest's
				// point onto it (else a Common Chest point is dragged onto a sealed chest metres away, or
				// a Common Chest added on top of one). Its rarity's points stay free for their own chests.
				if (label->chestKind == ChestKind::Rarity && ClaimedByChestMarker(m_ScenesData[sceneID], entity, minDistance))
					continue;

				if (minDistance > f_ObjectDetectRange)
				{
					if (f_AutoDetectNewItems)
						AddCustomPoint(nearestPoint->sceneID, nearestPoint->labelID, entity->levelPosition());

					continue;
				}

				// Relic: only move a point that is actually wrong. The server-generated datasets put every
				// point exactly where the entity is, and a "fixed" point keeps the player's coordinate for
				// good - a regenerated dataset could never correct it again.
				if (f_AutoFixItemPositions && !nearestPoint->fixed && minDistance > 1.0f)
					FixPointPosition(nearestPoint, entity->levelPosition());

				pointsSet.erase(nearestPoint);
			}
		}
	}

	// Relic: the archipelago conches, by gadget id. The response is matched on its own gadgetId_ rather
	// than on an entity filter, because the conch entity is often gone by the time the response arrives
	// (the 1.6 ones are one-off) and because attaching a filter would also hand these labels to
	// CheckObjects, whose auto-fix and auto-add would then rewrite the 8/32-conch accounting.
	static const char* ConchLabelForGadget(uint32_t gadgetId)
	{
		switch (gadgetId)
		{
		case 70380274:   // "Echoing Conch" (SceneObj_LostParadise_Echoconch), 1.6 scene 4 x32
		case 70500036:   // the same conch re-added for 2.8 scene 9 x8
			return "EchoingConch";
		case 70500033:   // "Imaging Conch" / Phantasmal Conch (Dreamconch_01), 2.8
		case 70500053:   // ... and its on-water variant (Dreamconch_OnWater_01)
			return "ImagingConch";
		default:
			return nullptr;
		}
	}

	void InteractiveMap::GadgetModule_OnGadgetInteractRsp_Hook(void* __this, app::GadgetInteractRsp* notify, MethodInfo* method)
	{
		// The game's own handling goes first: everything below belongs to this feature, and the hook guard
		// skips the origin when a handler faults - which would cost the game the whole interaction.
		CALL_ORIGIN(GadgetModule_OnGadgetInteractRsp_Hook, __this, notify, method);

		auto& interactiveMap = GetInstance();
		if (!interactiveMap.f_AutoDetectGatheredItems)
			return;

		if (notify->fields.retcode_ != 0)   // a refused interaction (locked chest, no authority in co-op) completes nothing
			return;

		const auto interactType = notify->fields.interactType_;
		const bool isConch = interactType == app::InteractType__Enum::InteractEchoShell;

		// Conches are the one interaction the 1.6 client answers on InterOpStart; everything else is reported
		// when it finishes. CompletePoint ignores a point that is already completed, so a Start+Finish pair is free.
		if (!isConch && notify->fields.opType_ != app::InterOpType__Enum::InterOpType__Enum_InterOpFinish)
			return;

		auto entity = game::EntityManager::instance().entity(notify->fields.gadgetEntityId_);
		if (isConch)
			interactiveMap.OnConchGathered(notify->fields.gadgetId_, entity);
		else if (interactType == app::InteractType__Enum::InteractGather ||
			interactType == app::InteractType__Enum::InteractOpenChest)
			interactiveMap.OnItemGathered(entity, interactType == app::InteractType__Enum::InteractOpenChest);
	}

	void InteractiveMap::OnConchGathered(uint32_t gadgetId, game::Entity* entity)
	{
		const char* clearName = ConchLabelForGadget(gadgetId);
		if (clearName == nullptr)
		{
			LOG_DEBUG("Conch interaction with gadget %u, which maps to no label - nothing completed.", gadgetId);
			return;
		}

		auto sceneID = game::GetCurrentPlayerSceneID();
		if (m_ScenesData.count(sceneID) == 0)
			return;

		auto& nameToLabel = m_ScenesData[sceneID].nameToLabel;
		if (nameToLabel.count(clearName) == 0 || nameToLabel[clearName] == nullptr)
			return;

		// The conch's own position when it is still there, the avatar's otherwise (it is within arm's reach
		// of what was just picked up).
		app::Vector2 position{};
		if (entity != nullptr && entity->raw() != nullptr)
			position = entity->levelPosition();
		else
		{
			auto avatar = game::EntityManager::instance().avatar();
			if (avatar == nullptr || avatar->raw() == nullptr)
				return;
			position = avatar->levelPosition();
		}

		// Relic: the nearest point whatever its state. A conch already marked (by the key, before it was picked up)
		// is this one - passing over it would hand its mark to another conch in range, one the player never found.
		auto nearestPoint = FindNearestPoint(*nameToLabel[clearName], position, f_GatheredItemsDetectRange, true);
		if (nearestPoint == nullptr)
		{
			LOG_INFO("Conch %u at %.1f, %.1f: no point within %.0f.", gadgetId,
				position.x, position.y, f_GatheredItemsDetectRange.value());
			return;
		}
		if (nearestPoint->completed)
		{
			LOG_DEBUG("Conch %u: its point %u is marked already.", gadgetId, nearestPoint->id);
			return;
		}

		if (CompletePoint(nearestPoint))
			NotifyAutoMark(nearestPoint);
	}

	void InteractiveMap::OnItemGathered(game::Entity* entity, bool openedChest)
	{
		if (openedChest)
		{
			OnChestOpened(entity);   // Relic: chests have rules of their own (the chest markers, the sealed look)
			return;
		}

		auto sceneID = game::GetCurrentPlayerSceneID();
		if (m_ScenesData.count(sceneID) == 0)
			return;

		if (entity == nullptr)   // Relic: the entity manager itself is gone (a scene transition) - the filters would dereference it
			return;

		auto& labels = m_ScenesData[sceneID].labels;
		for (auto& [labelID, label] : labels)
		{
			if (!label.supportGatherDetect || label.filter == nullptr)
				continue;

			if (!label.filter->IsValid(entity))
				continue;

			// Relic: the nearest point whatever its state, for the reason OnConchGathered gives: an oculus marked by
			// the key before it was picked up must not hand its mark to another one within range.
			auto nearestPoint = FindNearestPoint(label, entity->levelPosition(), f_GatheredItemsDetectRange, true);
			if (nearestPoint == nullptr)
			{
				LOG_INFO("%s: no point within %.0f of it - nothing marked.", label.clearName.c_str(), f_GatheredItemsDetectRange.value());
				return;
			}
			if (nearestPoint->completed)
				return;   // this one, marked already
			if (CompletePoint(nearestPoint))
				NotifyAutoMark(nearestPoint);
			return;
		}
	}

	// Relic: a chest's prefab names say when it was a sealed one - "..._TreasureBox02_Locked", "..._TreasureBox_01_Locker"
	// (the gadgets "SceneObj_Chest_Locked_LvN"); a chest opens with the same entity once its seal is gone.
	static bool LooksSealed(game::Entity* entity)
	{
		return entity != nullptr && entity->raw() != nullptr && entity->name().find("Lock") != std::string::npos;
	}

	// Relic: an opened chest marks the chest point nearest to it (mapprogress::ChestPicker): a point of its own rarity or
	// a chest marker - Sealed / Buried Chest, Large / Small Rock Pile, the only place the Teyvat data lists those chests -
	// and a chest that was sealed takes a Sealed Chest marker first. Upstream looks at the rarity label alone, so
	// opening a sealed or buried chest leaves its marker unmarked and, worse, marks another chest of its rarity within
	// 20 m when there is one. Chests whose name says nothing usable - the 2.8 music-thorn and reflection chests (no
	// "TreasureBox" in it), a prefab numbered for another tier - fall back to any chest point within a few metres.
	void InteractiveMap::OnChestOpened(game::Entity* entity)
	{
		const auto sceneID = game::GetCurrentPlayerSceneID();
		auto sceneIt = m_ScenesData.find(sceneID);
		if (sceneIt == m_ScenesData.end())
			return;
		auto& labels = sceneIt->second.labels;

		// Where the chest is: its own position or, when its entity is already gone, the avatar's - at arm's length
		// of it, so only the close last-resort candidates are looked at then.
		const bool known = entity != nullptr && entity->raw() != nullptr;
		app::Vector2 position{};
		if (known)
			position = entity->levelPosition();
		else
		{
			auto avatar = game::EntityManager::instance().avatar();
			if (avatar == nullptr || avatar->raw() == nullptr)
				return;
			position = avatar->levelPosition();
		}

		const bool sealed = known && LooksSealed(entity);
		const LabelData* rarity = nullptr;
		if (known)
		{
			for (auto& [labelID, label] : labels)
			{
				if (label.chestKind == ChestKind::Rarity && label.filter != nullptr && label.filter->IsValid(entity))
				{
					rarity = &label;
					break;
				}
			}
		}

		using Picker = mapprogress::ChestPicker<PointData>;
		Picker picker(f_GatheredItemsDetectRange);
		for (auto& [labelID, label] : labels)
		{
			if (label.chestKind == ChestKind::None)
				continue;
			for (auto& [pointID, point] : label.points)
			{
				const float dx = point.levelPosition.x - position.x;
				const float dy = point.levelPosition.y - position.y;
				const float distance = std::sqrt(dx * dx + dy * dy);
				if (!known)
					picker.Offer(&point, distance, Picker::Any);
				else if (label.chestKind == ChestKind::Rarity)
					picker.Offer(&point, distance, &label == rarity ? Picker::Own : Picker::Any);
				else
				{
					if (sealed && label.chestKind == ChestKind::SealedMarker)
						picker.Offer(&point, distance, Picker::Sealed);
					picker.Offer(&point, distance, Picker::Own);
				}
			}
		}

		PointData* point = picker.point();
		if (point == nullptr)
		{
			// Said out loud, so a test in game shows at once which chests the automatic marking misses.
			LOG_INFO("An opened chest (%s%s) matched no chest point within %.0f m - nothing marked.",
				known ? entity->name().c_str() : "entity already gone", rarity != nullptr ? "" : ", no rarity filter matched",
				f_GatheredItemsDetectRange.value());
			return;
		}
		if (point->completed)
			return;   // this one, marked already: its mark must not pass on to a neighbour
		if (CompletePoint(point))
			NotifyAutoMark(point);
	}

	bool InteractiveMap::ClaimedByChestMarker(const SceneData& scene, game::Entity* entity, float nearestOwn)
	{
		const auto position = entity->levelPosition();
		float nearestMarker = FLT_MAX;
		float nearestSealedMarker = FLT_MAX;
		for (auto& [labelID, label] : scene.labels)
		{
			if (label.chestKind != ChestKind::Marker && label.chestKind != ChestKind::SealedMarker)
				continue;
			for (auto& [pointID, point] : label.points)
			{
				const float dx = point.levelPosition.x - position.x;
				const float dy = point.levelPosition.y - position.y;
				const float distance = std::sqrt(dx * dx + dy * dy);
				nearestMarker = distance < nearestMarker ? distance : nearestMarker;
				if (label.chestKind == ChestKind::SealedMarker)
					nearestSealedMarker = distance < nearestSealedMarker ? distance : nearestSealedMarker;
			}
		}
		if (nearestMarker == FLT_MAX)
			return false;   // a dataset without chest markers (the archipelagos): nothing to claim
		return mapprogress::ClaimedByChestMarker(nearestMarker, nearestSealedMarker, LooksSealed(entity), nearestOwn, f_ObjectDetectRange);
	}
	void InteractiveMap::ResetUserData(ResetElementFunc func)
	{
		for (auto& [sceneID, scene] : m_ScenesData)
		{
			for (auto& [labelID, label] : scene.labels)
			{
				std::vector<uint32_t> toRemovePoints;
				for (auto& [pointID, point] : label.points)
				{
					bool needToRemove = (this->*func)(&label, &point);
					if (needToRemove)
						toRemovePoints.push_back(pointID);
				}

				for (auto& pointID : toRemovePoints)
				{
					label.points.erase(pointID);
				}
			}
		}
	}

	bool InteractiveMap::ResetCompletedPointData(LabelData* label, PointData* point)
	{
		if (!point->completed)
			return false;

		point->completed = false;
		point->completeTimestamp = 0;

		label->completedCount--;
		return false;
	}

	bool InteractiveMap::ResetCustomPointData(LabelData* label, PointData* point)
	{
		if (!point->custom)
			return false;

		return true;
	}

	bool InteractiveMap::ResetFixedPointData(LabelData* label, PointData* point)
	{
		if (!point->fixed)
			return false;

		point->levelPosition = point->originPosition;

		point->fixed = false;
		point->originPosition = {};

		return false;
	}

	void InteractiveMap::LoadUserData(const nlohmann::json& data, LoadElementFunc func)
	{
		for (auto& [cSceneID, jLabels] : data.items())
		{
			auto sceneID = std::stoul(cSceneID);
			if (m_ScenesData.count(sceneID) == 0)
			{
				LOG_WARNING("Scene %u don't exist. Maybe map data was updated.", sceneID);
				continue;
			}

			auto& labels = m_ScenesData[sceneID].labels;
			for (auto& [cLabelID, jLabelUserData] : jLabels.items())
			{
				auto labelID = std::stoul(cLabelID);
				if (labels.count(labelID) == 0)
				{
					LOG_WARNING("Label %u:%u don't exist. Maybe data was .", sceneID, labelID);
					continue;
				}

				auto& label = labels[labelID];

				auto& elements = jLabelUserData;
				for (auto& element : elements)
					(this->*func)(&label, element);
			}
		}
	}

	void InteractiveMap::LoadCustomPointData(LabelData* labelData, const nlohmann::json& data)
	{
		auto customPoint = ParsePointData(data);
		if (labelData->points.count(customPoint.id) > 0)
		{
			LOG_ERROR("Failed to load custom point for label `%u:%s` with position %.1f, %.1f. ID already exist.",
				labelData->sceneID, labelData->name.c_str(), customPoint.levelPosition.x, customPoint.levelPosition.y);
			return;
		}
		
		auto& newPointEntry = labelData->points[customPoint.id];
		newPointEntry = customPoint;
		newPointEntry.sceneID = labelData->sceneID;
		newPointEntry.labelID = labelData->id;
		newPointEntry.custom = true;
		newPointEntry.creationTimestamp = data["creation_timestamp"];

		m_CustomPoints.insert(&newPointEntry);
	}

	void InteractiveMap::LoadCompletedPointData(LabelData* labelData, const nlohmann::json& data)
	{
		auto& points = labelData->points;
		auto pointID = data["point_id"].get<uint32_t>();

		if (points.count(pointID) == 0)
		{
			LOG_WARNING("Point %u don't exist. Maybe data was updated.", pointID);
			return;
		}

		auto& point = points[pointID];
		if (std::find_if(m_CompletedPoints.begin(), m_CompletedPoints.end(), [=](PointData* data) { return point.id == data->id; }) != std::end(m_CompletedPoints))
		{
			LOG_WARNING("Completed point %u duplicate.", pointID);
			return;
		}

		point.completed = true;
		point.completeTimestamp = data["complete_timestamp"];
		labelData->completedCount++;

		m_CompletedPoints.push_back(&point);
	}

	void InteractiveMap::LoadFixedPointData(LabelData* labelData, const nlohmann::json& data)
	{
		auto& points = labelData->points;
		auto pointID = data["point_id"].get<uint32_t>();

		if (points.count(pointID) == 0)
		{
			LOG_WARNING("Point %u don't exist. Maybe data was updated.", pointID);
			return;
		}

		auto& point = points[pointID];
		if (m_FixedPoints.count(&point) > 0)
		{
			LOG_WARNING("Fixed point %u duplicate.", pointID);
			return;
		}

		point.fixed = true;
		point.originPosition = point.levelPosition;
		point.levelPosition = { data["x_pos"], data["y_pos"] };

		m_FixedPoints.insert(&point);
	}

	void InteractiveMap::SaveUserData(nlohmann::json& data, SaveElementFunc func)
	{
		nlohmann::json jRoot = {};

		for (auto& [sceneID, scene] : m_ScenesData)
		{
			auto cSceneID = std::to_string(sceneID);
			jRoot[cSceneID] = nlohmann::json::object();

			auto& jLabels = jRoot[cSceneID];
			for (auto& [labelID, label] : scene.labels)
			{
				auto cLabelID = std::to_string(labelID);

				auto& container = jLabels[cLabelID];
				container = nlohmann::json::array();

				for (auto& [pointID, point] : label.points)
					(this->*func)(container, &point);
				
				if (container.empty())
					jLabels.erase(cLabelID);
			}

			if (jLabels.empty())
				jRoot.erase(cSceneID);
		}

		data = jRoot;
	}

	void InteractiveMap::SaveCustomPointData(nlohmann::json& jObject, PointData* point)
	{
		if (!point->custom)
			return;

		auto jPoint = nlohmann::json::object();
		jPoint["id"] = point->id;
		jPoint["x_pos"] = point->levelPosition.x;
		jPoint["y_pos"] = point->levelPosition.y;
		jPoint["creation_timestamp"] = point->creationTimestamp;
		jObject.push_back(jPoint);
	}

	void InteractiveMap::SaveCompletedPointData(nlohmann::json& jObject, PointData* point)
	{
		if (!point->completed)
			return;

		auto jPoint = nlohmann::json::object();
		jPoint["point_id"] = point->id;
		jPoint["complete_timestamp"] = point->completeTimestamp;
		jObject.push_back(jPoint);
	}

	void InteractiveMap::SaveFixedPointData(nlohmann::json& jObject, PointData* point)
	{
		if (!point->fixed)
			return;

		jObject.push_back(
			{
				{ "point_id", point->id },
				{ "x_pos", point->levelPosition.x },
				{ "y_pos", point->levelPosition.y }
			}
		);
	}

	void InteractiveMap::LoadCompletedPoints()
	{
		LoadUserData(f_CompletedPointsJson, &InteractiveMap::LoadCompletedPointData);
		ReorderCompletedPointDataByTimestamp();
	}

	void InteractiveMap::SaveCompletedPoints()
	{
		SaveUserData(f_CompletedPointsJson, &InteractiveMap::SaveCompletedPointData);
		f_CompletedPointsJson.FireChanged();
	}

	void InteractiveMap::ResetCompletedPoints()
	{
		ResetUserData(&InteractiveMap::ResetCompletedPointData);
		m_CompletedPoints.clear();
	}

    void InteractiveMap::ReorderCompletedPointDataByTimestamp()
    {
        m_CompletedPoints.sort([](PointData* a, PointData* b) { return a->completeTimestamp < b->completeTimestamp; });
    }

	void InteractiveMap::LoadCustomPoints()
	{
		LoadUserData(f_CustomPointsJson, &InteractiveMap::LoadCustomPointData);
	}

	void InteractiveMap::SaveCustomPoints()
	{
		SaveUserData(f_CustomPointsJson, &InteractiveMap::SaveCustomPointData);
		f_CustomPointsJson.FireChanged();
	}

	void InteractiveMap::ResetCustomPoints()
	{
		ResetUserData(&InteractiveMap::ResetCustomPointData);
		m_CustomPoints.clear();
	}

	void InteractiveMap::LoadFixedPoints()
	{
		LoadUserData(f_FixedPointsJson, &InteractiveMap::LoadFixedPointData);
	}

	void InteractiveMap::SaveFixedPoints()
	{
		SaveUserData(f_FixedPointsJson, &InteractiveMap::SaveFixedPointData);
		f_FixedPointsJson.FireChanged();
	}

	void InteractiveMap::ResetFixedPoints()
	{
		ResetUserData(&InteractiveMap::ResetFixedPointData);
		m_FixedPoints.clear();
	}

	void InteractiveMap::CreateUserDataField(const char* name, config::Field<nlohmann::json>& field, SaveAttachType saveType)
	{
		auto sectionName = GetUserDataFieldSection(saveType);
		field = config::CreateField<nlohmann::json>(name, sectionName, saveType != SaveAttachType::Profile, nlohmann::json::object());
	}

	void InteractiveMap::UpdateUserDataField(config::Field<nlohmann::json>& field, SaveAttachType saveType, bool move)
	{
		auto newSectionName = GetUserDataFieldSection(saveType);
		if (move)
			field.move(newSectionName, saveType != SaveAttachType::Profile);
		else
			field.repos(newSectionName, saveType != SaveAttachType::Profile);
	}

	std::string InteractiveMap::GetUserDataFieldSection(SaveAttachType saveType)
	{
		switch(saveType)
		{
		case SaveAttachType::Account:
			return fmt::format("InteractiveMap::accounts::{}", f_LastUserID.value());
		case SaveAttachType::Profile:
		case SaveAttachType::Global:
		default:
			return "InteractiveMap";
		}
	}

#define RESET_IF(name, type) if (f_ST##name##Points.value() == type) { UpdateUserDataField(f_##name##PointsJson, f_ST##name##Points.value()); Reset##name##Points(); }
#define LOAD_IF(name, type) if (f_ST##name##Points.value() == type) { Load##name##Points(); }

	void InteractiveMap::OnConfigProfileChanged()
	{
		// TO DO: Fix the problem when customPoints is account but completed points for profile

		RESET_IF(Completed, SaveAttachType::Profile);
		RESET_IF(Fixed, SaveAttachType::Profile);
		RESET_IF(Custom, SaveAttachType::Profile);

		LOAD_IF(Custom, SaveAttachType::Profile);
		LOAD_IF(Fixed, SaveAttachType::Profile);
		LOAD_IF(Completed, SaveAttachType::Profile);

		// Relic: the explored ground follows the completed points. Only the field is repositioned here (this need not
		// be the render thread); DrawExternal then reloads what belongs to the new profile.
		if (f_STCompletedPoints.value() == SaveAttachType::Profile)
			RepositionExploredField(SaveAttachType::Profile, false);
	}

	void InteractiveMap::OnAccountChanged(uint32_t userID)
	{
		if (userID == 0 || f_LastUserID == userID)
			return;

		f_LastUserID = userID;

		// TO DO: Fix the problem when customPoints is account but completed points for profile
		RESET_IF(Completed, SaveAttachType::Account);
		RESET_IF(Fixed, SaveAttachType::Account);
		RESET_IF(Custom, SaveAttachType::Account);

		LOAD_IF(Custom, SaveAttachType::Account);
		LOAD_IF(Fixed, SaveAttachType::Account);
		LOAD_IF(Completed, SaveAttachType::Account);

		// Relic: as in OnConfigProfileChanged - the explored ground of the new account is reloaded by DrawExternal.
		if (f_STCompletedPoints.value() == SaveAttachType::Account)
			RepositionExploredField(SaveAttachType::Account, false);
	}

#undef RESET_IF
#undef LOAD_IF
	InteractiveMap::PointData InteractiveMap::ParsePointData(const nlohmann::json& data)
	{
		PointData newPoint {};
		newPoint.id = data["id"];
		newPoint.levelPosition = { data["x_pos"], data["y_pos"] };
		return newPoint;
	}

	void InteractiveMap::LoadLabelData(const nlohmann::json& data, uint32_t sceneID, uint32_t labelID)
	{
		auto& sceneData = m_ScenesData[sceneID];
		auto& labelEntry = sceneData.labels[labelID];

		labelEntry.id = labelID;
		labelEntry.sceneID = sceneID;
        labelEntry.name = data["name"];
        labelEntry.clearName = data["clear_name"];
        // Relic: what an opened chest may mark (mapprogress::ChestPicker).
        labelEntry.chestKind = mapprogress::IsRarityChestLabel(labelEntry.clearName) ? ChestKind::Rarity
            : mapprogress::IsSealedChestLabel(labelEntry.clearName) ? ChestKind::SealedMarker
            : mapprogress::IsChestMarkerLabel(labelEntry.clearName) ? ChestKind::Marker : ChestKind::None;
        labelEntry.enabled = config::CreateField<bool>(labelEntry.clearName,
			fmt::format("InteractiveMap::Filters::Scene{}", sceneID), false, false);

        for (auto& pointJsonData : data["points"])
        {
			PointData pdata = ParsePointData(pointJsonData);
			pdata.labelID = labelID;
			pdata.sceneID = sceneID;

			labelEntry.points[pdata.id] = pdata;
        }

        sceneData.nameToLabel[labelEntry.clearName] = &labelEntry;
	}

	void InteractiveMap::LoadCategoriaData(const nlohmann::json& data, uint32_t sceneID)
	{
        auto& sceneData = m_ScenesData[sceneID];
        auto& labels = sceneData.labels;
        auto& categories = sceneData.categories;
        
        categories.push_back({});
        auto& newCategory = categories.back();
        
        // Relic: what comes back (plants, ores, animals, enemies) is taken by the mark key only when nothing else is near.
        const bool respawns = mapprogress::IsRespawningCategory(data["name"].get<std::string>());

        auto& children = newCategory.children;
        for (auto& child : data["children"])
        {
            if (labels.count(child) > 0)
            {
                auto& label = labels[child];
                label.respawns = label.respawns || respawns;
                children.push_back(&label);
            }
        }

        if (children.size() == 0)
        {
            categories.pop_back();
            return;
        }

        newCategory.name = data["name"];
	}

	void InteractiveMap::LoadSceneData(const nlohmann::json& data, uint32_t sceneID)
	{
		for (auto& [labelID, labelData] : data["labels"].items())
		{
			LoadLabelData(labelData, sceneID, std::stoul(labelID));
		}

        for (auto& categorie : data["categories"])
        {
            LoadCategoriaData(categorie, sceneID);
        }
	}

	void InteractiveMap::LoadScenesData()
	{
        LoadSceneData(nlohmann::json::parse(ResourceLoader::Load("MapTeyvatData", RT_RCDATA)), 3);
#if RELIC_GAME_VERSION != 16   // Relic: no Enkanomiya (2.4) and no Chasm (2.6) in the 1.6 world - res.rc does not embed them either
        LoadSceneData(nlohmann::json::parse(ResourceLoader::Load("MapEnkanomiyaData", RT_RCDATA)), 5);
        LoadSceneData(nlohmann::json::parse(ResourceLoader::Load("MapUndegroundMinesData", RT_RCDATA)), 6);
#endif
#if RELIC_GAME_VERSION == 16   // Relic: "Midsummer Island Adventure" - its own world, scene 4
        LoadSceneData(nlohmann::json::parse(ResourceLoader::Load("MapGaa16Scene4Data", RT_RCDATA)), 4);
#elif RELIC_GAME_VERSION == 28 // Relic: "Summertime Odyssey" - scene 9, regenerated from the 2.8 server scripts
        LoadSceneData(nlohmann::json::parse(ResourceLoader::Load("MapGaa28Scene9Data", RT_RCDATA)), 9);
#else
        LoadSceneData(nlohmann::json::parse(ResourceLoader::Load("MapGoldenAppleArchipelagoData", RT_RCDATA)), 9);
#endif

        LOG_INFO("Interactive map data loaded successfully.");
    }

	void InteractiveMap::LoadMaterialFilterData(const nlohmann::json& data, std::string type)
	{
		auto& materials = m_MaterialData[type].materials;
		for (auto& [filterID, filterData] : data[type].items())
		{
			auto& materialEntry = materials[std::stoul(filterID)];

			materialEntry.id = std::stoul(filterID);
			materialEntry.name = filterData["name"];
			materialEntry.clearName = filterData["clear_name"];
			materialEntry.filter = filterData["materials"].get<std::vector<uint32_t>>();
			materialEntry.selected = config::CreateField<bool>(materialEntry.clearName,
				"InteractiveMap::Materials::" + util::MakeCapital(type) + "{}", false, false);
		}

		auto& categories = m_MaterialData[type].categories;
		for (auto& category : data[type + "_types"])
		{
			categories.push_back({});
			auto& newCategory = categories.back();

			newCategory.id = std::stoul(category["id"].get<std::string>());
			newCategory.name = category["name"];
			auto& children = newCategory.children;
			for (auto& child : category["children"])
			{
				if (materials.count(child) > 0)
					children.push_back(&materials[child]);
			}

			if (children.size() == 0)
			{
				categories.pop_back();
				return;
			}
		}
	}

	void InteractiveMap::LoadMaterialFilterData()
	{
		auto data = nlohmann::json::parse(ResourceLoader::Load("AscensionMaterialsData", RT_RCDATA));
		LoadMaterialFilterData(data, "character");
		LoadMaterialFilterData(data, "weapon");
	}

    struct ScalingData
    {
        float scale;
        float offset;
    };

    ScalingData ComputeScaling(app::Vector2 normal, app::Vector2 scaled)
    {
		// Just the equation system: 
		//	s[0] * scale + offset = n[0]
		//	s[1] * scale + offset = n[1]
		// Where: s = scaled, n = normal

        ScalingData scalingData {};
        scalingData.scale = (normal.y - normal.x) / (scaled.y - scaled.x);
        scalingData.offset = normal.x - scaled.x * scalingData.scale;
        
        return scalingData;
    }


	void InteractiveMap::ApplySceneScalling(uint32_t sceneId, const ScallingInput& input)
    {
		ScalingData xScale = ComputeScaling({ input.normal1.x, input.normal2.x }, { input.scalled1.x, input.scalled2.x });
		ScalingData yScale = ComputeScaling({ input.normal1.y, input.normal2.y }, { input.scalled1.y, input.scalled2.y });

		app::Vector2 scale = { xScale.scale, yScale.scale };
		app::Vector2 offset = { xScale.offset, yScale.offset };

		LOG_DEBUG("Position scaling for scene %u: scale %0.3f %0.3f, offset %0.3f %0.3f", sceneId, scale.x, scale.y, offset.x, offset.y);
		auto& sceneData = m_ScenesData[sceneId];
		for (auto& [labelID, labelData] : sceneData.labels)
		{
			for (auto& [pointID, point] : labelData.points)
			{
				point.levelPosition = point.levelPosition * scale + offset;
			}
		}
		
    }

	void InteractiveMap::ApplyScaling()
	{
		// Relic: a dataset without both reference labels is left as it is instead of dereferencing what
		// std::map::operator[] inserts for a missing name (a null LabelData*, i.e. a crash at injection).
		// The generated archipelago datasets are already in level coordinates and must not be scaled.
#define APPLY_SCENE_OFFSETS(sceneID, name1, normal1x, normal1y, name2, normal2x, normal2y) {\
			auto sceneIt = m_ScenesData.find(sceneID); \
			if (sceneIt == m_ScenesData.end()) \
			{ \
				LOG_WARNING("Scene %u was not loaded - nothing to scale.", (uint32_t)sceneID); \
			} \
			else { \
			auto& sceneLabels = sceneIt->second.nameToLabel; \
			if (sceneLabels.count(name1) == 0 || sceneLabels.count(name2) == 0 || \
				sceneLabels[name1] == nullptr || sceneLabels[name2] == nullptr || \
				sceneLabels[name1]->points.empty() || sceneLabels[name2]->points.empty()) \
			{ \
				LOG_WARNING("Scene %u has no scaling reference (%s / %s) - its points are used as they are.", \
					(uint32_t)sceneID, name1, name2); \
			} \
			else \
			{ \
				app::Vector2 NormalPos1 = { normal1x, normal1y }; \
				app::Vector2 NormalPos2 = { normal2x, normal2y }; \
				app::Vector2 ScalledPos1 = sceneLabels[name1]->points.begin()->second.levelPosition; \
				app::Vector2 ScalledPos2 = sceneLabels[name2]->points.begin()->second.levelPosition; \
				ApplySceneScalling(sceneID, {NormalPos1, NormalPos2, ScalledPos1, ScalledPos2}); \
			} } \
		}

		// For find scaling we need two objects' correct & scaled coordinates
		// Better find objects with one point on map
		APPLY_SCENE_OFFSETS(3,
			"AnemoHypostasis", 1301.2f, 2908.4f,
			"ElectroHypostasis", 1942.3f, 1308.9f);

#if RELIC_GAME_VERSION != 16
		APPLY_SCENE_OFFSETS(5,
			"RuinHunter", -54.4f, -53.7f,
			"RuinGrader", 428.9f, 505.0f);

		APPLY_SCENE_OFFSETS(6,
			"Medaka", -649.27f, 776.9f,
			"SweetFlowerMedaka", -720.16f, 513.55f);
#endif

#if RELIC_GAME_VERSION != 16 && RELIC_GAME_VERSION != 28   // only the 3.3 reference build loads Akebi's archipelago file
    	APPLY_SCENE_OFFSETS(9,
			"PaleRedCrab", -396.38f, -253.75f,
			"GoldenCrab", 145.89f, 215.34f);
#endif
#undef APPLY_SCENE_OFFSETS

	}

	static bool IsMapActive()
	{
		auto uimanager = GET_SINGLETON(MoleMole_UIManager);
		if (uimanager == nullptr)
			return false;

		return app::MoleMole_UIManager_HasEnableMapCamera(uimanager, nullptr);
	}

	static app::Rect s_MapViewRect = { 0, 0, 1, 1 };
	void InteractiveMap::InLevelMapPageContext_UpdateView_Hook(app::InLevelMapPageContext* __this, MethodInfo* method)
	{
		CALL_ORIGIN(InLevelMapPageContext_UpdateView_Hook, __this, method);
		s_MapViewRect = __this->fields._mapViewRect;
	}

	// Relic: the screen size is passed in - otherwise it is two calls into the game per point per frame
	// (thousands of them with a large label enabled), although the caller already knows it.
	static ImVec2 LevelToMapScreenPos(const app::Vector2& levelPosition, const ImVec2& screenSize)
	{
		if (s_MapViewRect.m_Width == 0 || s_MapViewRect.m_Height == 0)
			return {};

		ImVec2 screenPosition;

		// Got position from 0 to 1
		screenPosition.x = (levelPosition.x - s_MapViewRect.m_XMin) / s_MapViewRect.m_Width;
		screenPosition.y = (levelPosition.y - s_MapViewRect.m_YMin) / s_MapViewRect.m_Height;

		// Scaling to screen position
		screenPosition.x = screenPosition.x * screenSize.x;
		screenPosition.y = (1.0f - screenPosition.y) * screenSize.y;

		return screenPosition;
	}

	static std::mutex _windowRectsMutex;
	static std::vector<ImRect> _windowRects;

	static void AddWindowRect()
	{
		_windowRects.push_back(
			{
				ImGui::GetWindowPos(),
				ImGui::GetWindowPos() + ImGui::GetWindowSize()
			}
		);
	}

	static app::MonoMiniMap* _monoMiniMap;
	void InteractiveMap::MonoMiniMap_Update_Hook(app::MonoMiniMap* __this, MethodInfo* method)
	{
		_monoMiniMap = __this;
		CALL_ORIGIN(MonoMiniMap_Update_Hook, __this, method);
	}

	static bool IsMiniMapActive()
	{
		if (_monoMiniMap == nullptr)
			return false;

		SAFE_BEGIN();
		// Fix Exception in Console, when loading ptr null | RyujinZX#7832
		// Relic: inside the guard - a stale MonoMiniMap pointer faults on this read too, and this runs on the
		// render thread where a fault costs the whole overlay.
		if (_monoMiniMap->fields._._._._.m_CachedPtr == 0) {
			_monoMiniMap = nullptr;
			return false;
		}
		return app::Behaviour_get_isActiveAndEnabled(reinterpret_cast<app::Behaviour*>(_monoMiniMap), nullptr);
		SAFE_ERROR();
		_monoMiniMap = nullptr;
		return false;
		SAFE_END();
	}

	static float GetMinimapLevelDistance()
	{
		if (_monoMiniMap == nullptr)
			return {};

		return _monoMiniMap->fields._areaMinDistance;
	}

	static void MapToggled(bool showed)
	{
		auto& cheatManager = GenshinCM::instance();
		bool isCursorVisible = cheatManager.CursorGetVisibility();
		if ((showed && !isCursorVisible) || (!showed && isCursorVisible && !cheatManager.IsMenuShowed()))
			cheatManager.CursorSetVisibility(showed);
	}

	// ---- Keyboard marking (Relic) ---------------------------------------------------------------------------------
	//
	// Three keys: show / hide the map, mark the nearest thing as done, undo the last mark. They fire on the window
	// thread whatever has the keyboard, so they follow the manager's rule for feature toggles - inert while the menu
	// is shown or owns the input, and while hotkeys are off in Settings - and only post a request. OnKeysUpdate
	// serves it on the game thread, the only place of the three that calls into the game for it (the avatar, the
	// cursor, the map page). The toggle is a key of this feature rather than f_Enabled's own hotkey because the
	// manager's toggle keys have no chat guard: a player who binds a letter would switch the map while typing.

	bool InteractiveMap::HotkeysArmed()
	{
		if (CheatManagerBase::IsMenuShowed() || renderer::IsInputLocked())
			return false;
		return feature::Settings::GetInstance().f_HotkeysEnabled;
	}

	void InteractiveMap::OnToggleKey()
	{
		if (HotkeysArmed())
			m_KeyRequests.fetch_or(KeyToggle);
	}

	void InteractiveMap::OnMarkKey()
	{
		if (HotkeysArmed())
			m_KeyRequests.fetch_or(KeyMark);
	}

	void InteractiveMap::OnUndoKey()
	{
		if (HotkeysArmed())
			m_KeyRequests.fetch_or(KeyUndo);
	}

	// The keys' and the automatic marks' feedback: the camera path's idiom (never with the menu open, only while
	// notifications are on), kept up long enough to read a name and a count - the manager's default is half a second.
	static void MapToast(const std::string& title, const std::string& content = {})
	{
		if (title.empty() || CheatManagerBase::IsMenuShowed())
			return;
		auto& settings = feature::Settings::GetInstance();
		if (!settings.f_NotificationsShow)
			return;
		const int delay = settings.f_NotificationsDelay.value();
		ImGuiToast toast(ImGuiToastType_None, delay > 2500 ? delay : 2500);
		toast.set_title("%s: %s", _TR("Interactive map"), title.c_str());
		if (!content.empty())
			toast.set_content("%s", content.c_str());
		ImGui::InsertNotification(toast);   // staged behind a lock: safe from the game thread
	}

	const InteractiveMap::LabelData* InteractiveMap::LabelOf(const PointData* point) const
	{
		auto sceneIt = m_ScenesData.find(point->sceneID);
		if (sceneIt == m_ScenesData.end())
			return nullptr;
		auto labelIt = sceneIt->second.labels.find(point->labelID);
		return labelIt == sceneIt->second.labels.end() ? nullptr : &labelIt->second;
	}

	std::string InteractiveMap::PointName(const PointData* point) const
	{
		auto label = LabelOf(point);
		return label != nullptr ? Translator::RuntimeTranslate(label->name) : std::string(_TR("a point"));
	}

	std::string InteractiveMap::ProgressText(const PointData* point) const
	{
		auto label = LabelOf(point);
		if (label == nullptr)
			return {};
		return fmt::format("{} / {} {}", label->completedCount, label->points.size(), _TR("done"));
	}

	void InteractiveMap::NotifyAutoMark(const PointData* point)
	{
		if (f_NotifyAutoMarks)
			MapToast(fmt::format("{} {}", _TR("found"), PointName(point)), ProgressText(point));
	}

	void InteractiveMap::OnKeysUpdate()
	{
		const int requests = m_KeyRequests.exchange(0);
		if (requests == 0)
			return;

		// A key the player could be typing - a letter, a digit... - does nothing while the cursor is out (the chat or
		// a game menu is open), except on the big map, where the keys are meant to work.
		const bool cursorOut = GenshinCM::instance().CursorGetVisibility() && !IsMapActive();
		auto typing = [cursorOut](config::Field<Hotkey>& key, const char* what)
		{
			if (!cursorOut || !mapprogress::IsTypableCombo(key.value().GetKeys()))
				return false;
			LOG_DEBUG("[imap] %s key ignored: the cursor is out (the chat or a game menu is open).", what);
			return true;
		};

		if ((requests & KeyToggle) != 0 && !typing(f_ToggleKey, "show / hide"))
			ServeToggleKey();
		if ((requests & KeyUndo) != 0 && !typing(f_RevertLatestCompletion, "undo"))
			ServeUndoKey();
		if ((requests & KeyMark) != 0 && !typing(f_CompleteNearestPoint, "mark"))
			ServeMarkKey();
	}

	void InteractiveMap::ServeToggleKey()
	{
		auto& toggle = f_Enabled.value();
		toggle.set_enabled(!toggle.enabled());
		f_Enabled.FireChanged();

		if (toggle.enabled())
			MapToast(_TR("shown"));
		else if (!f_CompleteNearestPoint.value().IsEmpty())
			MapToast(_TR("hidden"), fmt::format("{} ({})", _TR("Marking still works"), std::string(f_CompleteNearestPoint.value())));
		else
			MapToast(_TR("hidden"), f_AutoDetectGatheredItems ? std::string(_TR("Opened chests and collected oculi are still marked.")) : std::string());
	}

	void InteractiveMap::ServeMarkKey()
	{
		auto avatar = game::EntityManager::instance().avatar();
		if (avatar == nullptr || avatar->raw() == nullptr)
			return;   // a loading screen: there is no "here" yet (upstream searched around 0,0 then)

		const auto sceneID = game::GetCurrentPlayerSceneID();
		auto sceneIt = m_ScenesData.find(sceneID);
		if (sceneIt == m_ScenesData.end())
		{
			MapToast(_TR("no map data for this place"));
			return;
		}

		const auto position = avatar->levelPosition();
		const int64_t now = util::GetCurrentTimeMillisec();
		const bool passRecent = now < m_PassRecentUntilMs;
		PointData* skip = now < m_SkipUntilMs ? m_SkipPoint : nullptr;
		m_PassRecentUntilMs = 0;   // both last one press, whatever it finds
		m_SkipPoint = nullptr;
		m_SkipUntilMs = 0;

		const float range = f_PointFindRange;
		const bool onlyTracked = f_CompleteOnlyViewed;
		mapprogress::MarkPicker<PointData> picker(now, range, passRecent, skip);
		bool anyTracked = false;
		for (auto& [labelID, label] : sceneIt->second.labels)
		{
			if (onlyTracked && !label.enabled)
				continue;
			anyTracked = true;
			for (auto& [pointID, point] : label.points)
			{
				const float dx = point.levelPosition.x - position.x;
				const float dy = point.levelPosition.y - position.y;
				picker.Offer(&point, std::sqrt(dx * dx + dy * dy), point.completed, point.completeTimestamp, label.respawns);
			}
		}

		PointData* point = picker.point();
		if (point == nullptr)
		{
			const std::string title = range > 0.f ? fmt::format("{} {:.0f} m", _TR("nothing to mark within"), range) : std::string(_TR("nothing to mark"));
			MapToast(title, anyTracked ? std::string() : std::string(_TR("No filter is on here: open the in-game map and pick what to track in Filters.")));
			return;
		}

		if (picker.alreadyDone())
		{
			// The nearest one was marked a moment ago - most often the chest the automatic detection has just counted.
			// Marking the next one without being asked would mark something the player may never have found.
			m_PassRecentUntilMs = now + mapprogress::kPassRecentMs;
			MapToast(fmt::format("{} {} ({:.0f} m)", PointName(point), _TR("is marked already"), picker.distance()),
				fmt::format("{} {} {}", _TR("Press"), std::string(f_CompleteNearestPoint.value()), _TR("again to mark the next one.")));
			return;
		}

		if (!CompletePoint(point))
			return;   // marked in between by another thread (the interact hook, a right-click on the map)
		MapToast(fmt::format("{} {} ({:.0f} m)", _TR("marked"), PointName(point), picker.distance()), ProgressText(point));
	}

	void InteractiveMap::ServeUndoKey()
	{
		PointData* point = RevertLatestPointCompleting();
		if (point == nullptr)
		{
			MapToast(_TR("nothing to undo"));
			return;
		}

		// The next Mark skips it once, so a wrong pick is fixed with Undo, then Mark.
		m_SkipPoint = point;
		m_SkipUntilMs = util::GetCurrentTimeMillisec() + mapprogress::kSkipAfterUndoMs;
		m_PassRecentUntilMs = 0;
		MapToast(fmt::format("{} {}", _TR("unmarked"), PointName(point)), ProgressText(point));
	}

	// ---- Only where you have been (Relic) -------------------------------------------------------------------------
	//
	// While the option is on, the ground the character walks over is remembered as 25 m cells per scene, and the map
	// and the minimap draw an unfinished point only near a remembered cell (mapprogress::ExploreGrid). All of it runs
	// on the render thread - DrawExternal samples the avatar every half second, the menu edits the options, the draw
	// loops read the grids - so it needs no lock of its own. The one exception is an account / profile switch, which
	// may arrive on another thread: it repositions the field under m_ExploreFieldLock and raises m_ExploreReload, and
	// the next frame reloads. A save checks the flag under the same lock, so ground walked on one account is never
	// written into another one's section.

	void InteractiveMap::InitExploreGrids()
	{
		for (auto& [sceneID, scene] : m_ScenesData)
		{
			float minX = FLT_MAX, minY = FLT_MAX, maxX = -FLT_MAX, maxY = -FLT_MAX;
			for (auto& [labelID, label] : scene.labels)
			{
				for (auto& [pointID, point] : label.points)
				{
					const auto& p = point.levelPosition;
					if (!std::isfinite(p.x) || !std::isfinite(p.y))
						continue;
					minX = p.x < minX ? p.x : minX;
					minY = p.y < minY ? p.y : minY;
					maxX = p.x > maxX ? p.x : maxX;
					maxY = p.y > maxY ? p.y : maxY;
				}
			}
			if (minX > maxX)
				continue;   // a scene without points has nothing to reveal

			auto& grid = m_ExploreGrids[sceneID];
			grid.SetBounds(minX, minY, maxX, maxY);
			LOG_DEBUG("[imap] explore grid of scene %u: %zu x %zu cells.", sceneID, grid.Width(), grid.Height());
		}
	}

	const mapprogress::ExploreGrid* InteractiveMap::ExploreGridFor(uint32_t sceneID) const
	{
		if (!f_ExploredOnly)
			return nullptr;
		auto it = m_ExploreGrids.find(sceneID);
		return it != m_ExploreGrids.end() && it->second.Valid() ? &it->second : nullptr;
	}

	size_t InteractiveMap::ExploredCells(uint32_t sceneID) const
	{
		auto it = m_Explored.find(sceneID);
		return it != m_Explored.end() ? it->second.size() : 0;
	}

	void InteractiveMap::RebuildExploreGrids()
	{
		static const std::unordered_set<int64_t> kNothing;
		const float radius = mapprogress::ClampRadius(f_ExploredRadius);
		for (auto& [sceneID, grid] : m_ExploreGrids)
		{
			auto it = m_Explored.find(sceneID);
			grid.Rebuild(it != m_Explored.end() ? it->second : kNothing, radius);
		}
	}

	void InteractiveMap::LoadExplored()
	{
		nlohmann::json data;
		{
			std::lock_guard lock(m_ExploreFieldLock);
			data = f_ExploredJson.value();
		}

		m_Explored.clear();
		if (!data.is_object() || !data.contains("scenes"))
			return;

		try
		{
			if (data.value("cell", 0.0) != static_cast<double>(mapprogress::kExploreCell))
			{
				LOG_WARNING("[imap] The saved explored ground uses another cell size - it is not loaded.");
				return;
			}
			size_t total = 0;
			for (auto& [scene, cells] : data["scenes"].items())
			{
				if (cells.is_string())
					total += mapprogress::DecodeCells(cells.get<std::string>(), m_Explored[static_cast<uint32_t>(std::stoul(scene))]);
			}
			LOG_DEBUG("[imap] explored ground loaded: %zu cells.", total);
		}
		catch (const std::exception& e)
		{
			LOG_WARNING("[imap] The saved explored ground could not be read (%s) - starting empty.", e.what());
			m_Explored.clear();
		}
	}

	void InteractiveMap::SaveExplored()
	{
		nlohmann::json scenes = nlohmann::json::object();
		for (auto& [sceneID, cells] : m_Explored)
		{
			if (!cells.empty())
				scenes[std::to_string(sceneID)] = mapprogress::EncodeCells(cells);
		}

		// Nothing explored anywhere: the field's default, which the config leaves out of the file.
		nlohmann::json data = nlohmann::json::object();
		if (!scenes.empty())
			data = { { "v", 1 }, { "cell", mapprogress::kExploreCell }, { "scenes", std::move(scenes) } };

		{
			std::lock_guard lock(m_ExploreFieldLock);
			if (m_ExploreReload)
				return;   // the field belongs to another account / profile by now: this ground must not land there
			f_ExploredJson.value() = std::move(data);
			f_ExploredJson.FireChanged();
		}
		m_ExploreDirty = false;
		m_ExploreSavedMs = util::GetCurrentTimeMillisec();
	}

	void InteractiveMap::RepositionExploredField(SaveAttachType saveType, bool move)
	{
		std::lock_guard lock(m_ExploreFieldLock);
		UpdateUserDataField(f_ExploredJson, saveType, move);
		if (!move)
			m_ExploreReload = true;   // a switch: what is in memory belongs to the previous owner
	}

	void InteractiveMap::ForgetExplored(uint32_t sceneID)
	{
		m_Explored.erase(sceneID);
		RebuildExploreGrids();
		SaveExplored();
	}

	void InteractiveMap::ExploreSample()
	{
		UPDATE_DELAY(500);

		// The game is read under a guard of its own: a fault here (an avatar torn down by a scene change) must not
		// count against the map's drawing, which shares DrawExternal's fault budget. Only plain values cross it.
		uint32_t sceneID = 0;
		bool placed = false;
		app::Vector2 position{};
		const bool read = relic::Try([&]()
		{
			sceneID = game::GetCurrentPlayerSceneID();
			auto avatar = game::EntityManager::instance().avatar();
			if (avatar == nullptr || avatar->raw() == nullptr)
				return;   // a loading screen
			position = avatar->levelPosition();
			placed = true;
		});
		if (!read || !placed || !std::isfinite(position.x) || !std::isfinite(position.y))
			return;

		auto gridIt = m_ExploreGrids.find(sceneID);
		if (gridIt == m_ExploreGrids.end())
			return;   // a place without map data (a domain, the teapot): nothing to remember

		const int64_t cell = mapprogress::CellKeyAt(position.x, position.y);
		if (m_Explored[sceneID].insert(cell).second)
		{
			gridIt->second.Add(cell);
			m_ExploreDirty = true;
		}
	}

	void InteractiveMap::ExploreTick()
	{
		if (m_ExploreReload.exchange(false))
		{
			// An account / profile switch: what was in memory belonged to the previous owner and is dropped unsaved
			// (at most the last half minute of walking).
			LoadExplored();
			RebuildExploreGrids();
			m_ExploreDirty = false;
		}

		if (!f_ExploredOnly)
		{
			if (m_ExploreWasOn && m_ExploreDirty)
				SaveExplored();   // just switched off: keep what was walked
			m_ExploreWasOn = false;
			return;
		}
		m_ExploreWasOn = true;

		// A new reveal radius (the slider, a profile switch): every grid is rebuilt - a matter of milliseconds.
		const float radius = mapprogress::ClampRadius(f_ExploredRadius);
		for (auto& [sceneID, grid] : m_ExploreGrids)
		{
			if (grid.Radius() != radius)
			{
				RebuildExploreGrids();
				break;
			}
		}

		ExploreSample();

		if (m_ExploreDirty && util::GetCurrentTimeMillisec() - m_ExploreSavedMs > 30000)
			SaveExplored();
	}

	void InteractiveMap::DrawExternal()
	{
		ExploreTick();   // Relic: "Only where you have been" is owned by the render thread, so it is served here

		if (IsMiniMapActive() && f_Enabled->enabled())
			DrawMinimapPoints();

		static bool _lastMapActive = false;
		bool mapActive = IsMapActive();

		if (mapActive != _lastMapActive)
		{
			MapToggled(mapActive);
			
			if (!mapActive)
				renderer::SetInputLock(this, false);
		}

		_lastMapActive = mapActive;

		if (!mapActive)
            return;

		// Relic: with the feature switched off, draw nothing and register no window rect. A registered rect
		// swallows the game map's clicks and scroll wheel, and a focused ImGui window locks the game's input
		// altogether - the map then ignores pan, zoom, M and Esc, which reads exactly like a broken map.
		if (!f_Enabled->enabled())
		{
			// Release first: the lock below is taken whenever an ImGui item has focus, and switching the
			// feature off in that same frame would otherwise strand it. While it is held the game sees NO
			// input at all - not even Esc or M to close the map - and the hotkey that would switch the
			// feature back on is itself suppressed while the lock is held.
			renderer::SetInputLock(this, false);

			std::lock_guard _rectGuard(_windowRectsMutex);
			_windowRects.clear();
			return;
		}

		// If any InputText is focused, the game will not respond any keyboard input.
		auto ctx = ImGui::GetCurrentContext();
		if (ctx->IO.WantCaptureKeyboard && !renderer::IsInputLocked())
			renderer::SetInputLock(this, true);
		else if (!ctx->IO.WantCaptureKeyboard && renderer::IsInputLocked())
			renderer::SetInputLock(this, false);

		auto mapManager = GET_SINGLETON(MoleMole_MapManager);
		if (mapManager == nullptr)
			return;

		// Draw windows
		{
			std::lock_guard _rectGuard(_windowRectsMutex);
			
			_windowRects.clear();

			ImGui::SetNextWindowSize(ImVec2(540.0f, 0.0f), ImGuiCond_FirstUseEver);   // Relic: wide enough for the labels
			bool menuOpened = ImGui::Begin(Translator::RuntimeTranslate("Interactive map").c_str(), nullptr, ImGuiWindowFlags_NoFocusOnAppearing);
			AddWindowRect();

			if (menuOpened)
			{
				DrawMenu();

				if (!f_SeparatedWindows)
				{
					ImGui::Spacing();
					DrawFilters(false);
				}
			}
			ImGui::End();

			if (f_SeparatedWindows)
			{
				ImGui::SetNextWindowSize(ImVec2(640.0f, 700.0f), ImGuiCond_FirstUseEver);
				bool filtersOpened = ImGui::Begin(Translator::RuntimeTranslate("Filters").c_str(), nullptr, ImGuiWindowFlags_NoFocusOnAppearing);
				AddWindowRect();

				if (filtersOpened)
					DrawFilters();

				ImGui::End();
			}

			if (f_ShowMaterialsWindow)
			{
				ImGui::SetNextWindowSize(ImVec2(560.0f, 620.0f), ImGuiCond_FirstUseEver);
				bool materialsOpened = ImGui::Begin(Translator::RuntimeTranslate("Ascension Materials Filter").c_str(), nullptr, ImGuiWindowFlags_NoFocusOnAppearing);
				AddWindowRect();

				if (materialsOpened)
					DrawMaterialFilters();

				ImGui::End();
			}
		}

        DrawPoints();
	}

	static bool IsRectInScreen(const ImRect& rect, const ImVec2& screenSize)
	{
		return rect.Min.x < screenSize.x && rect.Min.y < screenSize.y &&
			rect.Max.x > 0 && rect.Max.y > 0;
	}
	
	static void RenderPointCircle(const ImVec2& position, ImTextureID textureID, float transparency, float radius, bool isCustom = false)
	{
		auto& settings = feature::Settings::GetInstance();
		radius *= static_cast<float>(settings.f_FontSize) / 16.0f;

		ImVec2 imageStartPos = position - radius;
		ImVec2 imageEndPos = position + radius;

		auto draw = ImGui::GetBackgroundDrawList();
		draw->AddCircleFilled(position, radius, ImColor(0.23f, 0.26f, 0.32f, transparency));

		if (textureID)
		{
			draw->AddImageRounded(textureID, imageStartPos + 2.0f, imageEndPos - 2.0f,
				ImVec2(0, 0), ImVec2(1, 1), ImColor(1.0f, 1.0f, 1.0f, transparency), radius);
		}

		draw->AddCircle(position, radius, isCustom ? ImColor(0.11f, 0.69f, 0.11f, transparency) : ImColor(0.91f, 0.68f, 0.36f, transparency));
	}

	void InteractiveMap::DrawPoint(const PointData& pointData, const ImVec2& screenPosition, float radius, float radiusSquared, ImTextureID texture, bool selectable)
	{
		if (pointData.completed && !f_ShowCompleted || !f_ShowInCompleted && !pointData.completed)
			return;
		
		float transparency = pointData.completed ? f_CompletePointTransparency : f_InCompletePointTransparency;

		if (/* m_SelectedPoint == nullptr && */!selectable || m_HoveredPoint != nullptr)
		{
			RenderPointCircle(screenPosition, texture, transparency, radius, pointData.custom);
			return;
		}

		ImVec2 mousePos = ImGui::GetMousePos();
		ImVec2 diffSize = screenPosition - mousePos;
		if (diffSize.x * diffSize.x + diffSize.y * diffSize.y > radiusSquared)
		{
			RenderPointCircle(screenPosition, texture, transparency, radius, pointData.custom);
			return;
		}

		m_HoveredPoint = const_cast<PointData*>(&pointData);
		radius *= 1.2f;

		RenderPointCircle(screenPosition, texture, transparency, radius, pointData.custom);

		if (ImGui::IsMouseClicked(ImGuiMouseButton_Right))
		{
			if (pointData.completed)
				UncompletePoint(m_HoveredPoint);
			else
				CompletePoint(m_HoveredPoint);
		}	
	}

    void InteractiveMap::DrawPoints()
	{
		static const float relativeSizeX = 821.0f;

		auto sceneID = game::GetCurrentMapSceneID();
		if (m_ScenesData.count(sceneID) == 0)
			return;

		ImVec2 screenSize = { static_cast<float>(app::Screen_get_width(nullptr)),
			static_cast<float>(app::Screen_get_height(nullptr)) };

		
		// Relic: the zoom factor is clamped - unbounded, a zoomed-out map multiplies the chosen size by ~0.07,
		// every icon collapses to a couple of pixels and the size slider looks like it does nothing.
		float zoomFactor = 1.0f;
		if (f_DynamicSize && s_MapViewRect.m_Width > 0.0f)
			zoomFactor = std::clamp(relativeSizeX / s_MapViewRect.m_Width, 0.5f, 2.0f);
		auto iconSize = f_IconSize * zoomFactor;
		auto radius = iconSize / 2;
		auto radiusSquared = radius * radius;

		std::lock_guard<std::mutex> _guard(m_PointMutex);
		// m_SelectedPoint = nullptr;
		m_HoveredPoint = nullptr;

		const auto exploreGrid = ExploreGridFor(sceneID);   // Relic: set only with "Only where you have been" on

		auto& labels = m_ScenesData[sceneID].labels;
		for (auto& [labelID, label] : labels)
		{
			if (!label.enabled)
				continue;

			auto image = LabelIcon(label.clearName, f_ShowHDIcons);
			for (auto& [pointID, point] : label.points)
			{
				if (exploreGrid != nullptr && !point.completed && !exploreGrid->IsRevealed(point.levelPosition.x, point.levelPosition.y))
					continue;   // not near any ground walked yet: still a surprise

				auto screenPosition = LevelToMapScreenPos(point.levelPosition, screenSize);

				ImRect imageRect = { screenPosition - radius, screenPosition + radius };
				if (!IsRectInScreen(imageRect, screenSize))
					continue;

				//ImGui::PushID(&point);
				DrawPoint(point, screenPosition, radius, radiusSquared, image ? image->textureID : nullptr);
				//ImGui::PopID();
			}
		}
	}

	struct ImCircle
	{
		ImVec2 center;
		float radius;

		bool Contains(const ImCircle& b)
		{
			if (b.radius > radius)
				return false;

			auto diff = b.center - center;
			auto distanceSqrd = std::pow(diff.x, 2) + std::pow(diff.y, 2);
			auto radiusDiffSqrd = std::pow(radius - b.radius, 2);
			return radiusDiffSqrd > distanceSqrd;
		}
	};

	static ImCircle GetMinimapCircle()
	{
		static app::Rect mapRect = {};
		if (_monoMiniMap == nullptr)
			return {};

		UPDATE_DELAY_VAR(ImCircle, _miniMapCircle, 2000);

		auto uiManager = GET_SINGLETON(MoleMole_UIManager);
		if (uiManager == nullptr || uiManager->fields._sceneCanvas == nullptr || uiManager->fields._uiCamera == nullptr)
			return {};

		auto back = _monoMiniMap->fields._grpMapBack;
		if (back == nullptr)
			return {};

		auto mapPos = app::Transform_get_position(reinterpret_cast<app::Transform*>(back), nullptr);
		auto center = app::Camera_WorldToScreenPoint(uiManager->fields._uiCamera, mapPos, nullptr);
		center.y = static_cast<float>(app::Screen_get_height(nullptr)) - center.y;
	 
		if (mapRect.m_Width == 0)
			mapRect = app::RectTransform_get_rect(back, nullptr);

		float scaleFactor = app::Canvas_get_scaleFactor(uiManager->fields._sceneCanvas, nullptr);
		if (scaleFactor != 0)
			_miniMapCircle = {
				ImVec2(center.x, center.y),
				(mapRect.m_Width * scaleFactor) / 2
			};

		return _miniMapCircle;
	}

	// The context pointer handed to the getter below.
	static_assert(offsetof(app::MonoMiniMap, fields.context) == 0xA8, "MonoMiniMap::context moved");

	static float GetMinimapScale()
	{
		if (_monoMiniMap == nullptr || _monoMiniMap->fields.context == nullptr
			|| app::MoleMole_InLevelMainPageContext_get_miniMapScale == nullptr)
			return 1.0f;

		// Relic: read the scale through the game's own getter on both versions - on 1.6 it is resolved by
		// offsets-16.overrides.json. Do NOT replace this with
		// `context->fields._miniMapScale`: the compiler puts that member at object offset 0x4F0 in appdata-28
		// while the 2.8 getter reads [ctx+0x500], so the generated 2.8 layout is 16 bytes short at this field
		// and a direct read would hand back the wrong float.
		return app::MoleMole_InLevelMainPageContext_get_miniMapScale(_monoMiniMap->fields.context, nullptr);
	}

	static float GetMinimapRotation()
	{
		if (_monoMiniMap == nullptr)
			return {};

		auto back = _monoMiniMap->fields._grpMiniBackRotate;
		if (back == nullptr)
			return {};

		auto rotation = app::Transform_get_rotation(reinterpret_cast<app::Transform*>(back), nullptr);

		app::Quaternion__Boxed boxed = { nullptr, nullptr, rotation };
		return app::Quaternion_ToEulerAngles(rotation, nullptr).z;
	}

	void InteractiveMap::DrawMinimapPoints()
	{
		// Found by hands. Only in Teyvat (3rd scene), need also test another scenes.
		static const float minimapAreaLevelRadius = 175.0f;
		constexpr float TWO_PI = 2 * 3.14159265f;

		auto sceneID = game::GetCurrentPlayerSceneID();
		if (m_ScenesData.count(sceneID) == 0)
			return;

		auto rotation = GetMinimapRotation();
		ImVec2 rotationMult = ImVec2(1.0f, 0.0f);
		if (rotation != 0)
		{
			auto rad = TWO_PI - rotation;// ((360.0f - rotation) * PI) / 180.0f;
			rotationMult = { sin(rad), cos(rad) };
		}

		ImCircle minimapCircle = GetMinimapCircle();
		auto avatarLevelPos = game::EntityManager::instance().avatar()->levelPosition();
		auto scale = minimapCircle.radius * GetMinimapScale() / minimapAreaLevelRadius;
		
		auto iconRadius = f_MinimapIconSize / 2;

		const auto exploreGrid = ExploreGridFor(sceneID);   // Relic: set only with "Only where you have been" on

		auto& labels = m_ScenesData[sceneID].labels;
		for (auto& [labelID, label] : labels)
		{
			if (!label.enabled)
				continue;

			auto image = LabelIcon(label.clearName, f_ShowHDIcons);
			for (auto& [pointID, point] : label.points)
			{
				if (exploreGrid != nullptr && !point.completed && !exploreGrid->IsRevealed(point.levelPosition.x, point.levelPosition.y))
					continue;

				ImVec2 positionDiff = { point.levelPosition.x - avatarLevelPos.x, avatarLevelPos.y - point.levelPosition.y };
				positionDiff = positionDiff * scale;
				if (rotation != 0.0f)
				{
					positionDiff = {
						positionDiff.x * rotationMult.y - positionDiff.y * rotationMult.x,
						positionDiff.x * rotationMult.x + positionDiff.y * rotationMult.y
					};
				}


				ImVec2 screenPos = minimapCircle.center + positionDiff;
				if (!minimapCircle.Contains({ screenPos, iconRadius }))
					continue;

				//ImGui::PushID(&point);
				DrawPoint(point, screenPos, iconRadius, 0.0f, image ? image->textureID : nullptr, false);
				//ImGui::PopID();
			}
		}
	}

	// Blocking interacts when cursor on window

	static ImVec2 _lastMousePosition = {};
	static bool MouseInIMapWindow()
	{
		std::lock_guard _rectGuard(_windowRectsMutex);

		for (auto& rect : _windowRects)
		{
			if (rect.Contains(_lastMousePosition))
				return true;
		}
		return false;
	}

	void InteractiveMap::InLevelMapPageContext_ZoomMap_Hook(app::InLevelMapPageContext* __this, float value, MethodInfo* method)
	{
		if (MouseInIMapWindow())
			return;

		return CALL_ORIGIN(InLevelMapPageContext_ZoomMap_Hook, __this, value, method);
	}

	void InteractiveMap::OnWndProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam, bool& cancelled)
	{
		if (!IsMapActive())
			return;

		POINT mPos;
		GetCursorPos(&mPos);
		ScreenToClient(hWnd, &mPos);
		ImVec2 cursorPos = { static_cast<float>(mPos.x), static_cast<float>(mPos.y) };
		_lastMousePosition = cursorPos;

		if (!MouseInIMapWindow())
			return;

		switch (uMsg)
		{
		case WM_MOUSEWHEEL:
		case WM_LBUTTONDBLCLK:
		case WM_LBUTTONDOWN:
			cancelled = true;
			break;
		default:
			break;
		}
	}

	std::vector<InteractiveMap::LabelData*> InteractiveMap::FindLabelsByClearName(const std::string& clearName)
	{
		std::vector<InteractiveMap::LabelData*> labels;
		for (auto& [sceneID, sceneData] : m_ScenesData)
		{
			if (sceneData.nameToLabel.count(clearName) > 0)
				labels.push_back(sceneData.nameToLabel[clearName]);
		}
		return labels;
	}

	void InteractiveMap::InitializeEntityFilter(game::IEntityFilter* filter, const std::string& clearName)
	{
		auto labels = FindLabelsByClearName(clearName);
		if (labels.size() == 0)
		{
			LOG_DEBUG("Not found filter for item '%s'", clearName.c_str());
			return;
		}

		for (auto& label : labels)
		{
			label->filter = filter;
		}		
	}

	void InteractiveMap::InitializeEntityFilters()
	{
#define INIT_FILTER(category, filterName) InitializeEntityFilter(&game::filters::##category##::##filterName, #filterName)

		INIT_FILTER(collection, Book);
		INIT_FILTER(collection, Viewpoint);
		INIT_FILTER(collection, RadiantSpincrystal);
		//INIT_FILTER(collection, BookPage);
		//INIT_FILTER(collection, QuestInteract);
		INIT_FILTER(chest, CommonChest);
		INIT_FILTER(chest, ExquisiteChest);
		INIT_FILTER(chest, PreciousChest);
		INIT_FILTER(chest, LuxuriousChest);
		INIT_FILTER(chest, RemarkableChest);
		INIT_FILTER(featured, Anemoculus);
		INIT_FILTER(featured, CrimsonAgate);
		INIT_FILTER(featured, Dendroculus);
		INIT_FILTER(featured, Electroculus);
		//INIT_FILTER(featured, Electrogranum);
		INIT_FILTER(featured, Geoculus);
		INIT_FILTER(featured, KeySigil);
		INIT_FILTER(featured, Lumenspar);
		//INIT_FILTER(featured, ShrineOfDepth);
		//INIT_FILTER(featured, TimeTrialChallenge);
		//INIT_FILTER(guide, CampfireTorch);
		//INIT_FILTER(guide, MysteriousCarvings);
		//INIT_FILTER(guide, PhaseGate);
		//INIT_FILTER(guide, Pot);
		//INIT_FILTER(guide, RuinBrazier);
		//INIT_FILTER(guide, Stormstone);
		//INIT_FILTER(living, BirdEgg);
		//INIT_FILTER(living, ButterflyWings);
		//INIT_FILTER(living, Crab);
		//INIT_FILTER(living, CrystalCore);
		//INIT_FILTER(living, Fish);
		//INIT_FILTER(living, Frog);
		//INIT_FILTER(living, LizardTail);
		//INIT_FILTER(living, LuminescentSpine);
		//INIT_FILTER(living, Onikabuto);
		//INIT_FILTER(living, Starconch);
		//INIT_FILTER(living, UnagiMeat);
		INIT_FILTER(mineral, AmethystLump);
		INIT_FILTER(mineral, ArchaicStone);
		INIT_FILTER(mineral, CorLapis);
		INIT_FILTER(mineral, CrystalChunk);
		INIT_FILTER(mineral, CrystalMarrow);
		INIT_FILTER(mineral, ElectroCrystal);
		INIT_FILTER(mineral, IronChunk);
		INIT_FILTER(mineral, NoctilucousJade);
		INIT_FILTER(mineral, MagicalCrystalChunk);
		INIT_FILTER(mineral, Starsilver);
		INIT_FILTER(mineral, WhiteIronChunk);
		//INIT_FILTER(monster, AbyssMage);
		//INIT_FILTER(monster, FatuiAgent);
		//INIT_FILTER(monster, FatuiCicinMage);
		//INIT_FILTER(monster, FatuiMirrorMaiden);
		//INIT_FILTER(monster, FatuiSkirmisher);
		//INIT_FILTER(monster, Geovishap);
		//INIT_FILTER(monster, GeovishapHatchling);
		//INIT_FILTER(monster, Hilichurl);
		//INIT_FILTER(monster, Mitachurl);
		//INIT_FILTER(monster, Nobushi);
		//INIT_FILTER(monster, RuinGuard);
		//INIT_FILTER(monster, RuinHunter);
		//INIT_FILTER(monster, RuinSentinel);
		//INIT_FILTER(monster, Samachurl);
		//INIT_FILTER(monster, Slime);
		//INIT_FILTER(monster, Specter);
		//INIT_FILTER(monster, TreasureHoarder);
		//INIT_FILTER(monster, UnusualHilichurl);
		//INIT_FILTER(monster, Whopperflower);
		//INIT_FILTER(monster, WolvesOfTheRift);
		INIT_FILTER(plant, AmakumoFruit);
		INIT_FILTER(plant, Apple);
		INIT_FILTER(plant, BambooShoot);
		INIT_FILTER(plant, Berry);
		INIT_FILTER(plant, CallaLily);
		INIT_FILTER(plant, Carrot);
		INIT_FILTER(plant, Cecilia);
		INIT_FILTER(plant, DandelionSeed);
		INIT_FILTER(plant, Dendrobium);
		INIT_FILTER(plant, FlamingFlowerStamen);
		INIT_FILTER(plant, FluorescentFungus);
		INIT_FILTER(plant, GlazeLily);
		INIT_FILTER(plant, HarraFruit);
		INIT_FILTER(plant, Horsetail);
		INIT_FILTER(plant, JueyunChili);
		INIT_FILTER(plant, KalpalataLotus);
		INIT_FILTER(plant, LavenderMelon);
		INIT_FILTER(plant, LotusHead);
		INIT_FILTER(plant, Matsutake);
		INIT_FILTER(plant, Mint);
		INIT_FILTER(plant, MistFlowerCorolla);
		INIT_FILTER(plant, Mushroom);
		INIT_FILTER(plant, NakuWeed);
		INIT_FILTER(plant, NilotpalaLotus);
		INIT_FILTER(plant, Padisarah);
		INIT_FILTER(plant, PhilanemoMushroom);
		INIT_FILTER(plant, Pinecone);
		INIT_FILTER(plant, Qingxin);
		INIT_FILTER(plant, Radish);
		INIT_FILTER(plant, RukkhashavaMushroom);
		INIT_FILTER(plant, SakuraBloom);
		INIT_FILTER(plant, SangoPearl);
		INIT_FILTER(plant, SeaGanoderma);
		INIT_FILTER(plant, Seagrass);
		INIT_FILTER(plant, SilkFlower);
		INIT_FILTER(plant, SmallLampGrass);
		INIT_FILTER(plant, Snapdragon);
		INIT_FILTER(plant, SumeruRose);
		INIT_FILTER(plant, Sunsettia);
		INIT_FILTER(plant, SweetFlower);
		INIT_FILTER(plant, Valberry);
		INIT_FILTER(plant, Violetgrass);
		INIT_FILTER(plant, Viparyas);
		INIT_FILTER(plant, WindwheelAster);
		INIT_FILTER(plant, Wolfhook);
		INIT_FILTER(plant, ZaytunPeach);
		//INIT_FILTER(puzzle, AncientRime);
		//INIT_FILTER(puzzle, BakeDanuki);
		//INIT_FILTER(puzzle, BloattyFloatty);
		//INIT_FILTER(puzzle, CubeDevices);
		//INIT_FILTER(puzzle, EightStoneTablets);
		//INIT_FILTER(puzzle, ElectricConduction);
		//INIT_FILTER(puzzle, ElectroSeelie);
		//INIT_FILTER(puzzle, ElementalMonument);
		//INIT_FILTER(puzzle, FloatingAnemoSlime);
		//INIT_FILTER(puzzle, Geogranum);
		//INIT_FILTER(puzzle, GeoPuzzle);
		//INIT_FILTER(puzzle, LargeRockPile);
		//INIT_FILTER(puzzle, LightUpTilePuzzle);
		//INIT_FILTER(puzzle, LightningStrikeProbe);
		//INIT_FILTER(puzzle, MistBubble);
		//INIT_FILTER(puzzle, PirateHelm);
		//INIT_FILTER(puzzle, PressurePlate);
		//INIT_FILTER(puzzle, SeelieLamp);
		//INIT_FILTER(puzzle, Seelie);
		//INIT_FILTER(puzzle, SmallRockPile);
		//INIT_FILTER(puzzle, StormBarrier);
		//INIT_FILTER(puzzle, SwordHilt);
		//INIT_FILTER(puzzle, TorchPuzzle);
		//INIT_FILTER(puzzle, UniqueRocks);
		//INIT_FILTER(puzzle, WindmillMechanism);

#undef  INIT_FILTER
	}

	void InteractiveMap::InitializeGatherDetectItems()
	{
#define INIT_DETECT_ITEM(name) \
			for (auto& label : FindLabelsByClearName(#name)) \
			{ \
				label->supportGatherDetect = true; \
			} \
		
		INIT_DETECT_ITEM(CommonChest);
		INIT_DETECT_ITEM(ExquisiteChest);
		INIT_DETECT_ITEM(PreciousChest);
		INIT_DETECT_ITEM(LuxuriousChest);
		INIT_DETECT_ITEM(RemarkableChest);

		INIT_DETECT_ITEM(Anemoculus);
		INIT_DETECT_ITEM(CrimsonAgate);
		INIT_DETECT_ITEM(Dendroculus);
		INIT_DETECT_ITEM(Electroculus);
		INIT_DETECT_ITEM(Geoculus);
		INIT_DETECT_ITEM(KeySigil);
		INIT_DETECT_ITEM(Lumenspar);

#undef INIT_DETECT_ITEM
	}
}
