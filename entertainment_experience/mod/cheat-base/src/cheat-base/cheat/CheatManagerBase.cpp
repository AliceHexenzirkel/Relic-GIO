#include <pch.h>
#include <cheat-base/relic-guard.h>
#include <cheat-base/relic-diag.h>
#include "CheatManagerBase.h"

#include <misc/cpp/imgui_stdlib.h>

#include <cheat-base/render/renderer.h>
#include <cheat-base/render/gui-util.h>
#include <cheat-base/cheat/misc/Settings.h>
#include <cheat-base/Translator.h>

#include <imgui_internal.h>

#include <cheat-base/render/ImageLoader.cpp>
namespace cheat
{

	void CheatManagerBase::Init(renderer::DXVersion dxVersion)
	{
		relic::diag::start();
		renderer::Init(dxVersion);

		events::RenderEvent += MY_METHOD_HANDLER(CheatManagerBase::OnRender);
		events::KeyUpEvent += MY_METHOD_HANDLER(CheatManagerBase::OnKeyUp);
		events::WndProcEvent += MY_METHOD_HANDLER(CheatManagerBase::OnWndProc);
	}

	CheatManagerBase::CheatManagerBase():
		NF(f_SelectedModule, "General", 0),
		NF(f_ModuleListWidth, "General", 175.0f),
		m_SelectedSection(nullptr),
		m_IsBlockingInput(true),
		m_IsPrevCursorActive(false)
	{
	}

	void CheatManagerBase::DrawExternal() const
	{
		for (auto& feature : m_Features)
		{
			ImGui::PushID(&feature);
			// Relic: one feature with wrong offsets must not take the whole overlay (and the game) down;
			// after a few faults it stops being drawn at all so it costs nothing per frame.
			static std::map<const void*, int> s_faults;
			int& faults = s_faults[feature];
			if (faults < relic::kGuardFaultLimit && relic::Try([&]() { feature->DrawExternal(); }))
				faults = 0;   // it works again: an early fault was transient
			else if (faults < relic::kGuardFaultLimit)
			{
				faults++;
				relic::guard_report_fault(feature->GetGUIInfo().moduleRepr.c_str(), relic::g_lastGuardCode,
					faults, faults >= relic::kGuardFaultLimit);
			}
			ImGui::PopID();
		}
	}

	std::string CheatManagerBase::GetModuleRepr(const std::string& name)
	{
		auto& sections = m_FeatureMap.at(name);
		if (sections.empty())
			return {};

		for (auto& [_, features] : sections)
		{
			if (features.empty())
				continue;

			return (*features.begin())->GetGUIInfo().moduleRepr;
		}
		return {};
	}

	void CheatManagerBase::DrawSectionSelection(const std::string& name, const std::string& translatedName, std::string*& currentModule, std::string& targetModule, size_t index)
	{
		bool is_module_selected = (&targetModule == currentModule) && m_SelectedSection == &name;
		ImGuiTreeNodeFlags node_flags = ImGuiTreeNodeFlags_Leaf;
		if (is_module_selected)
			node_flags |= ImGuiTreeNodeFlags_Selected;
		bool node_open = ImGui::TreeNodeEx(&name, node_flags, translatedName.c_str());

		if (ImGui::IsItemClicked() && !ImGui::IsItemToggledOpen())
		{
			currentModule = &targetModule;
			f_SelectedModule = index;
			m_SelectedSection = &name;
		}

		if (node_open)
			ImGui::TreePop();
	}

	void CheatManagerBase::DrawMenu()
	{
		if (m_ModuleOrder.empty())
			return;

		if (f_SelectedModule >= m_ModuleOrder.size())
			f_SelectedModule = static_cast<size_t>(0);

		static std::string* current = &m_ModuleOrder[f_SelectedModule];

		if (m_SelectedSection != nullptr && !m_FeatureMap[*current].contains(*m_SelectedSection))
			m_SelectedSection = nullptr;

		ImGui::SetNextWindowSize(ImVec2(600, 300), ImGuiCond_FirstUseEver);

		if (!ImGui::Begin(Translator::RuntimeTranslate("Relic enhancements").c_str(), nullptr, ImGuiWindowFlags_NoCollapse))
		{
			ImGui::End();
			return;
		}

		float width = f_ModuleListWidth;
		float dummyWidth = 1000.0f;
		if (ImGui::Splitter(true, 6.0f, &width, &dummyWidth, 175.0f, 8, -FLT_MIN))
			f_ModuleListWidth = width;

		ImGui::PushStyleVar(ImGuiStyleVar_ChildRounding, 5.0f);

		ImGui::BeginChild("ChildL", ImVec2(f_ModuleListWidth, -FLT_MIN), true);

		//if (ImGui::Checkbox(_TR("Block key/mouse"), &m_IsBlockingInput))
		//{
		//	renderer::SetInputLock(this, m_IsBlockingInput);
		//}

		size_t index = 0;
		for (auto& moduleName : m_ModuleOrder)
		{
			bool is_module_selected = (current == &moduleName) && m_SelectedSection == nullptr;
			ImGuiTreeNodeFlags node_flags = ImGuiTreeNodeFlags_DefaultOpen |
				ImGuiTreeNodeFlags_OpenOnArrow | ImGuiTreeNodeFlags_OpenOnDoubleClick;

			auto& sections = m_FeatureMap[moduleName];
			if (sections.size() == 1)
				node_flags = ImGuiTreeNodeFlags_Leaf;

			if (is_module_selected)
				node_flags |= ImGuiTreeNodeFlags_Selected;

			bool node_open = ImGui::TreeNodeEx(GetModuleRepr(moduleName).c_str(), node_flags);
			if (ImGui::IsItemClicked() && !ImGui::IsItemToggledOpen())
			{
				current = &moduleName;
				f_SelectedModule = index;
				m_SelectedSection = nullptr;
			}

			if (node_open)
			{
				if (sections.size() > 1)
				{
					const std::string* emptyString = nullptr;

					for (auto& [sectionName, features] : sections)
					{
						if (sectionName.empty())
						{
							emptyString = &sectionName;
							continue;
						}

						DrawSectionSelection(sectionName, Translator::RuntimeTranslate(sectionName),
							current, moduleName, index);
					}

					if (emptyString != nullptr)
						DrawSectionSelection(*emptyString, _TR("Other"),
							current, moduleName, index);
				}

				ImGui::TreePop();
			}

			index++;
		}
#ifdef _DEBUG
		ImGui::NewLine();
		static bool _showMetricsWindow = false;
		if (ImGui::Button("Open Imgui Debug Tool"))
			_showMetricsWindow = !_showMetricsWindow;
		if (_showMetricsWindow)
			ImGui::ShowMetricsWindow(&_showMetricsWindow);
#endif // _DEBUG
		ImGui::EndChild();

		ImGui::SameLine();

		ImGui::BeginGroup();

		DrawProfileLine();

		ImGuiWindowFlags window_flags = ImGuiWindowFlags_None;
		ImGui::BeginChild("ChildR", ImVec2(0, -FLT_MIN), true, window_flags);

		auto& sections = m_FeatureMap[*current];
		if (m_SelectedSection == nullptr)
		{
			if (sections.contains(std::string()))
				DrawMenuSection(std::string(), sections[""], false);

			for (auto& [sectionName, features] : sections)
			{
				if (sectionName.empty())
					continue;

				DrawMenuSection(sectionName, features, true);
			}
		}
		else
		{
			auto& features = sections.at(*m_SelectedSection);
			DrawMenuSection(*m_SelectedSection, features, false);
		}

		ImGui::EndChild();
		ImGui::PopStyleVar();

		ImGui::EndGroup();

		ImGui::End();
	}

	void CheatManagerBase::DrawMenuSection(const std::string& sectionName, const std::vector<Feature*>& features, bool needGroup) const
	{
		if (needGroup && !sectionName.empty())
		{
			if (features.empty())
				return;

			auto feature = *features.begin();
			ImGui::BeginGroupPanel(feature->GetGUIInfo().groupRepr.c_str());
		}

		for (auto& feature : features)
		{
			ImGui::PushID(&feature);
			feature->DrawMain();
			ImGui::PopID();
		}

		if (needGroup && !sectionName.empty())
			ImGui::EndGroupPanel();
	}

	void CheatManagerBase::DrawProfileGlobalActivities()
	{
		if (ImGui::Button(_TR("Add new profile")))
		{
			std::unordered_set<std::string> profileNameSet = { config::GetProfiles().begin(), config::GetProfiles().end() };
			size_t index = 0;
			std::string name {};
			do 
			{
				index++;
				std::string newName = fmt::format("Profile #{}", index);
				if (profileNameSet.count(newName) == 0)
					name = newName;

			} while (name.empty());

			config::CreateProfile(name, false);
		}
	}

	void CheatManagerBase::DrawProfileEntryActivities(const std::string& profileName)
	{
		bool isPopupOpen = ImGui::IsRenamePopupOpened();

		if (isPopupOpen)
			ImGui::BeginDisabled();

		if (ImGui::SmallButton(_TR("Rnm")))
			ImGui::OpenRenamePopup(profileName);
		if (ImGui::IsItemHovered())
			ImGui::SetTooltip(_TR("Rename"));

		if (isPopupOpen)
			ImGui::EndDisabled();

		std::string nameBuffer;
		if (ImGui::DrawRenamePopup(nameBuffer))
		{
			config::RenameProfile(profileName, nameBuffer);
		}

		ImGui::SameLine();

		if (ImGui::SmallButton(_TR("Del")))
			config::RemoveProfile(profileName);
		if (ImGui::IsItemHovered())
			ImGui::SetTooltip(_TR("Delete"));

		ImGui::SameLine();

		if (ImGui::SmallButton(_TR("Dupe")))
			config::DuplicateProfile(profileName);
		if (ImGui::IsItemHovered())
			ImGui::SetTooltip(_TR("Duplicate Profile"));
	}

	void CheatManagerBase::DrawProfileEntry(const std::string& profileName)
	{
		ImGui::Text(profileName.c_str());
	}

	void CheatManagerBase::DrawProfileTableHeader()
	{
		ImGui::TableSetupColumn(_TR("Name"));
	}

	int CheatManagerBase::GetProfileTableColumnCount()
	{
		return 1;
	}

	void CheatManagerBase::DrawProfileConfiguration()
	{
		static ImGuiTableFlags flags =
			ImGuiTableFlags_Resizable | ImGuiTableFlags_Reorderable | ImGuiTableFlags_Hideable
			| ImGuiTableFlags_RowBg | ImGuiTableFlags_BordersOuter | ImGuiTableFlags_BordersV | ImGuiTableFlags_NoBordersInBody
			| ImGuiTableFlags_ScrollY;
		if (ImGui::BeginTable("ProfileTable", GetProfileTableColumnCount() + 1, flags,
			ImVec2(0.0f, ImGui::GetTextLineHeightWithSpacing() * 10), 0.0f))
		{
			DrawProfileTableHeader();
			ImGui::TableSetupColumn(_TR("Actions"));
			ImGui::TableSetupScrollFreeze(0, 1);
			ImGui::TableHeadersRow();

			// Copy profiles names
			auto profiles = config::GetProfiles();
			for (auto& profile : profiles)
			{
				ImGui::TableNextRow();
				ImGui::TableNextColumn();

				ImGui::PushID(profile.c_str());
				DrawProfileEntry(profile);
				ImGui::TableNextColumn();

				DrawProfileEntryActivities(profile);
				ImGui::PopID();
			}

			ImGui::EndTable();
		}

		DrawProfileGlobalActivities();
	}

	void CheatManagerBase::DrawProfileLine()
	{
		RELIC_DIAG_TICK(MENU_OUT);

		if (m_IsProfileConfigurationShowed)
			ImGui::BeginDisabled();

		bool buttonPressed = ImGui::Button(_TR("Configure..."));

		if (m_IsProfileConfigurationShowed)
			ImGui::EndDisabled();

		if (buttonPressed)
			m_IsProfileConfigurationShowed = !m_IsProfileConfigurationShowed;

		ImGui::SameLine();

		auto& profiles = config::GetProfiles();
		auto& currentProfile = config::CurrentProfileName();

		constexpr float width = 200.0f;
		ImGui::SetNextItemWidth(width);
		if (ImGui::BeginCombo(_TR("Profile"), currentProfile.c_str()))
		{
			for (auto& name : profiles)
			{
				bool is_selected = (currentProfile == name);
				if (ImGui::Selectable(name.c_str(), is_selected))
					config::ChangeProfile(name);

				if (ImGui::IsItemHovered() && CalcWidth(name) > width)
					ShowHelpText(name.c_str());

				if (is_selected)
					ImGui::SetItemDefaultFocus();
			}
			ImGui::EndCombo();
		}
	}

	void CheatManagerBase::DrawStatus() const
	{
		// Drawing status window
		ImGuiWindowFlags flags = ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoBackground |
			ImGuiWindowFlags_NoFocusOnAppearing | ImGuiWindowFlags_NoBringToFrontOnFocus |
			ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoCollapse;

		auto& settings = feature::Settings::GetInstance();
		if (!settings.f_StatusMove)
			flags |= ImGuiWindowFlags_NoInputs | ImGuiWindowFlags_NoMove;

		ImGui::Begin(_TR("Cheat status"), nullptr, flags);

		static ImGuiTableFlags tabFlags = ImGuiTableFlags_Borders | ImGuiTableFlags_RowBg;

		if (ImGui::BeginTable("activesTable", 1, tabFlags))
		{
			ImGui::TableSetupColumn(_TR("Active features"));
			ImGui::TableHeadersRow();

			int row = 0;

			for (auto& feature : m_Features)
			{
				if (feature->NeedStatusDraw())
				{
					ImGui::TableNextRow();
					ImGui::TableSetColumnIndex(0);

					feature->DrawStatus();

					//ImU32 row_bg_color = ImGui::GetColorU32(
					//	ImVec4(0.2f + row * 0.1f, 0.1f + row * 0.05f, 0.1f + row * 0.03f, 0.85f));
					//ImGui::TableSetBgColor(ImGuiTableBgTarget_RowBg0, row_bg_color);
					row++;
				}
			}
			ImGui::EndTable();
		}

		ImGui::End();
	}

	void CheatManagerBase::DrawInfo()
	{
		auto& settings = feature::Settings::GetInstance();

		// Drawing status window
		ImGuiWindowFlags flags = ImGuiWindowFlags_NoDecoration |
			ImGuiWindowFlags_NoFocusOnAppearing | ImGuiWindowFlags_NoBringToFrontOnFocus |
			ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoCollapse;

		if (!settings.f_InfoMove)
			flags |= ImGuiWindowFlags_NoInputs | ImGuiWindowFlags_NoMove;

		auto checkLambda = [](const Feature* feat) { return feat->NeedInfoDraw(); };
		bool showAny = std::any_of(m_Features.begin(), m_Features.end(), checkLambda);
		if (!showAny && !settings.f_StatusMove)
			return;

		//ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0.04f, 0.05f, 0.05f, 0.90f));
		ImGui::Begin(_TR("Info window"), nullptr, flags);
		//ImGui::PopStyleColor();

		if (!showAny)
		{
			ImGui::Text(_TR("Nothing here"));
			ImGui::End();
			return;
		}

		for (auto& moduleName : m_ModuleOrder)
		{
			auto& sections = m_FeatureMap[moduleName];
			bool moduleShowAny = std::any_of(sections.begin(), sections.end(),
				[](const auto& iter)
				{
					return std::any_of(iter.second.begin(), iter.second.end(),
						[](const auto feat)
						{
							return feat->NeedInfoDraw();
						});
				}
			);
			if (!moduleShowAny)
				continue;

			ImGui::BeginGroupPanel(Translator::RuntimeTranslate(moduleName).c_str());

			for (auto& [sectionName, features] : sections)
			{
				for (auto& feature : features)
				{
					if (!feature->NeedInfoDraw())
						continue;

					ImGui::PushID(&feature);
					feature->DrawInfo();
					ImGui::PopID();
				}
			}

			ImGui::EndGroupPanel();
		}

		ImGui::End();
	}

	void CheatManagerBase::DrawFps()
	{
		auto& settings = feature::Settings::GetInstance();

		ImGuiWindowFlags flags = ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoFocusOnAppearing
			| ImGuiWindowFlags_NoBringToFrontOnFocus | ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_AlwaysAutoResize;

		if (!settings.f_FpsMove)
			flags |= ImGuiWindowFlags_NoInputs | ImGuiWindowFlags_NoMove;

		if (ImGui::Begin("FPS", nullptr, flags))
		{
			ImGui::Text("FPS: %.1f", ImGui::GetIO().Framerate);
			ImGui::End();
		}
	}

	void CheatManagerBase::DrawNotifications()
	{
		ImGui::RenderNotifications();
	}

	
	void CheatManagerBase::OnRender()
	{
		RELIC_DIAG_TICK(RENDER_IN);
		auto& settings = feature::Settings::GetInstance();

		// Relic: the menu key is read FIRST and the drawing below is guarded, so nothing a feature does can
		// take the menu away from the player — on a backported game version a wrong offset can fault anywhere.
		bool toggleWanted = settings.f_MenuKey.value().IsReleased() && !ImGui::IsAnyItemActive();
		RELIC_DIAG_TICK(KEY_SEEN);
		if (toggleWanted)
		{
			RELIC_DIAG_TICK(TOGGLE);
			ToggleMenuShow();
		}
		// Relic: a feature asked for the menu to close (RequestHideMenu) - served here, on the render thread.
		if (s_HideMenuWanted)
		{
			s_HideMenuWanted = false;
			if (s_IsMenuShowed)
				ToggleMenuShow();
		}

		DrawExternal();
		RELIC_DIAG_TICK(EXTERNAL_OUT);

		if (s_IsMenuShowed)
		{
			static int s_menuFaults = 0;
			if (s_menuFaults < relic::kGuardFaultLimit && relic::Try([&]() { DrawMenu(); }))
				s_menuFaults = 0;
			else if (s_menuFaults < relic::kGuardFaultLimit)
			{
				s_menuFaults++;
				relic::guard_report_fault("Drawing the menu", relic::g_lastGuardCode, s_menuFaults,
					s_menuFaults >= relic::kGuardFaultLimit);
			}
		}

		if (m_IsProfileConfigurationShowed)
		{
			ImGui::SetNextWindowSize({ 0, ImGui::GetTextLineHeightWithSpacing() * 11 }, ImGuiCond_FirstUseEver);
			if (ImGui::Begin(_TR("Config profile configuration"), &m_IsProfileConfigurationShowed))
				DrawProfileConfiguration();

			ImGui::End();
		}

		// Relic: nothing of the mod's own overlays while a camera path plays (see SetOverlayQuiet).
		const bool quiet = s_OverlayQuiet;

		if (settings.f_StatusShow && !quiet)
			DrawStatus();

		if (settings.f_InfoShow && !quiet)
			DrawInfo();

		if (settings.f_FpsShow && !quiet)
			DrawFps();

		RELIC_DIAG_TICK(RENDER_OUT);


		if (settings.f_NotificationsShow && !quiet)
			DrawNotifications();

		if (settings.f_ShowStyleEditor)
			ImGui::ShowStyleEditor();
	}

	void CheatManagerBase::CheckToggles(short key) const
	{
		if (s_IsMenuShowed || renderer::IsInputLocked())
			return;

		auto& settings = feature::Settings::GetInstance();
		if (!settings.f_HotkeysEnabled)
			return;

		for (auto& field : config::GetFields<TranslatedHotkey>())
		{
			auto& toggle = field.value();
			if (toggle.value().IsPressed(key))
			{
				toggle.set_enabled(!toggle.enabled());
				field.FireChanged();

				std::string title = fmt::format("{}: {}", field->name(), (toggle.enabled() ? _TR("Enabled") : _TR("Disabled")));
				ImGuiToast toast(ImGuiToastType_None, settings.f_NotificationsDelay);
				toast.set_title(title.c_str());
				ImGui::InsertNotification(toast);
			}
		}
	}

	bool menuToggled = false;

	void CheatManagerBase::ToggleMenuShow()
	{
		s_IsMenuShowed = !s_IsMenuShowed;
		renderer::SetInputLock(this, s_IsMenuShowed && m_IsBlockingInput);
		menuToggled = true;
	}

	void CheatManagerBase::OnKeyUp(short key, bool& cancelled)
	{
		auto& settings = feature::Settings::GetInstance();
		if (!settings.f_MenuKey.value().IsPressed(key))
		{
			CheckToggles(key);
			return;
		}
	}

	void CheatManagerBase::OnWndProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam, bool& canceled)
	{
		if (!menuToggled)
			return;

		menuToggled = false;

		if (s_IsMenuShowed)
		{
			m_IsPrevCursorActive = CursorGetVisibility();
			if (!m_IsPrevCursorActive)
				CursorSetVisibility(true);
		}
		else if (!m_IsPrevCursorActive)
			CursorSetVisibility(false);
	}

	bool CheatManagerBase::IsMenuShowed()
	{
		return s_IsMenuShowed;
	}

	void CheatManagerBase::SetOverlayQuiet(bool quiet)
	{
		s_OverlayQuiet = quiet;
	}

	bool CheatManagerBase::IsOverlayQuiet()
	{
		return s_OverlayQuiet;
	}

	void CheatManagerBase::RequestHideMenu()
	{
		s_HideMenuWanted = true;
	}

	void CheatManagerBase::PushFeature(Feature* feature)
	{
		m_Features.push_back(feature);

		auto& info = feature->GetGUIInfo();
		if (m_FeatureMap.count(info.moduleKey) == 0)
		{
			m_FeatureMap[info.moduleKey] = {};
			m_ModuleOrder.push_back(info.moduleKey);
		}

		auto& sectionMap = m_FeatureMap[info.moduleKey];
		std::string sectionName = info.groupKey;
		if (sectionMap.count(sectionName) == 0)
			sectionMap[sectionName] = {};

		auto& featureList = sectionMap[sectionName];
		featureList.push_back(feature);
	}

	void CheatManagerBase::AddFeature(Feature* feature)
	{
		PushFeature(feature);
	}

	void CheatManagerBase::AddFeatures(std::vector<Feature*> features)
	{
		for (auto& feature : features)
		{
			PushFeature(feature);
		}
	}

	void CheatManagerBase::SetModuleOrder(std::vector<std::string> moduleOrder)
	{
		std::unordered_set<std::string> moduleSet;
		moduleSet.insert(m_ModuleOrder.begin(), m_ModuleOrder.end());

		m_ModuleOrder.clear();

		for (auto& moduleName : moduleOrder)
		{
			if (m_FeatureMap.count(moduleName) == 0)
				continue;

			m_ModuleOrder.push_back(moduleName);
			moduleSet.erase(moduleName);
		}

		for (auto& moduleName : moduleSet)
		{
			m_ModuleOrder.push_back(moduleName);
		}
	}
}
