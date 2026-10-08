#pragma once

#include <cheat-base/cheat/Feature.h>
#include <cheat-base/config/config.h>
#include <cheat/game/Entity.h>
#include <cheat/game/IEntityFilter.h>

#include <atomic>
#include <mutex>
#include <unordered_set>

#include "MapProgress.h"

namespace cheat::feature 
{

	class InteractiveMap : public Feature
    {
	public:
		enum class SaveAttachType
		{
			Account,
			Profile,
			Global
		};

		config::Field<TranslatedHotkey> f_Enabled;
		config::Field<bool> f_ShowMaterialsWindow;
		config::Field<bool> f_SeparatedWindows;
		config::Field<bool> f_CompletionLogShow;

		config::Field<config::Enum<SaveAttachType>> f_STFixedPoints;
		config::Field<config::Enum<SaveAttachType>> f_STCustomPoints;
		config::Field<config::Enum<SaveAttachType>> f_STCompletedPoints;

		config::Field<float> f_IconSize;
		config::Field<float> f_MinimapIconSize;
		config::Field<bool> f_DynamicSize;
		config::Field<bool> f_ShowHDIcons;
		
		config::Field<bool> f_ShowCompleted;
		config::Field<float> f_CompletePointTransparency;
		config::Field<bool> f_ShowInCompleted;
		config::Field<float> f_InCompletePointTransparency;

		config::Field<bool> f_AutoDetectNewItems;
		config::Field<bool> f_AutoFixItemPositions;
		config::Field<bool> f_ObjectCheckOnlyShowed;
		config::Field<float> f_ObjectDetectRange;
		config::Field<int> f_CheckObjectsDelay;

		config::Field<bool> f_AutoDetectGatheredItems;
		config::Field<float> f_GatheredItemsDetectRange;
		
		config::Field<Hotkey> f_CompleteNearestPoint;
		config::Field<Hotkey> f_RevertLatestCompletion;
		config::Field<bool> f_CompleteOnlyViewed;
		config::Field<float> f_PointFindRange;
		
		// Relic: show / hide the map from the keyboard (marking - by key or automatic - keeps working while it is
		// hidden), a message for what the automatic detection marks, and "Only where you have been".
		config::Field<Hotkey> f_ToggleKey;
		config::Field<bool> f_NotifyAutoMarks;
		config::Field<bool> f_ExploredOnly;
		config::Field<float> f_ExploredRadius;

		static InteractiveMap& GetInstance();

		const FeatureGUIInfo& GetGUIInfo() const override;
		void DrawMain() override;

		void DrawExternal() override;

		struct PointData
		{
			uint32_t id;

			uint32_t sceneID;
			uint32_t labelID;

			app::Vector2 levelPosition;

			bool completed;
			int64_t completeTimestamp;

			bool custom;
			int64_t creationTimestamp;

			bool fixed;
			app::Vector2 originPosition;
		};

		// std::optional<PointData> GetSelectedPoint();
		PointData* GetHoveredPoint();

		std::vector<PointData*> GetEntityPoints(game::Entity* entity, bool completed = false, uint32_t sceneID = 0);
		PointData* FindNearestPoint(const app::Vector2& levelPosition, float range = 0.0f, bool onlyShowed = true, bool completed = false, uint32_t sceneID = 0);
		PointData* FindEntityPoint(game::Entity* entity, float range = 0.0f, uint32_t sceneID = 0);

		bool CompletePoint(PointData* pointData);     // Relic: true when this call marked it (false: it already was)
		void UncompletePoint(PointData* pointData);
		PointData* RevertLatestPointCompleting();      // Relic: the point it took back, or nullptr

		void FixPointPosition(PointData* pointData, app::Vector2 fixedPosition);
		void UnfixPoitnPosition(PointData* pointData);

		void AddCustomPoint(uint32_t sceneID, uint32_t labelID, app::Vector2 levelPosition);
		void RemoveCustomPoint(PointData* pointData);

	private:

		InteractiveMap();

		struct ScallingInput
		{
			app::Vector2 normal1;
			app::Vector2 normal2;
			app::Vector2 scalled1;
			app::Vector2 scalled2;
		};

		// Relic: what a label's points stand for when a chest is opened (see mapprogress::ChestPicker).
		enum class ChestKind : uint8_t
		{
			None,
			Rarity,         // Common ... Remarkable Chest: matched by the chest's own filter
			Marker,         // Buried Chest, Large / Small Rock Pile: a chest listed only there, no filter
			SealedMarker    // Sealed Chest: the same, and the one a chest that was sealed takes first
		};

		struct LabelData
		{
			uint32_t id;
			uint32_t sceneID;

			std::string name;
			std::string clearName;
			config::Field<bool> enabled;

			std::map<uint32_t, PointData> points;
			uint32_t completedCount;

			game::IEntityFilter* filter;
			bool supportGatherDetect;
			bool respawns = false;   // Relic: listed in a category of things that come back (plants, ores, animals, enemies)
			ChestKind chestKind = ChestKind::None;   // Relic
		};

		struct CategoryData
		{
			std::string name;
			std::vector<LabelData*> children;
		};

		struct SceneData
		{
			std::map<uint32_t, LabelData> labels;
			std::map<std::string, LabelData*> nameToLabel;
			std::vector<CategoryData> categories;
		};

		struct MaterialData
		{
			uint32_t id;
			std::string name;
			std::string clearName;
			std::vector<uint32_t> filter;
			config::Field<bool> selected;
		};

		struct MaterialCategoryData
		{
			uint32_t id;
			std::string name;
			std::vector<MaterialData*> children;
		};

		struct MaterialFilterData
		{
			std::map<uint32_t, MaterialData> materials;
			std::vector<MaterialCategoryData> categories;
		};

		std::map<uint32_t, SceneData> m_ScenesData;
		std::map<std::string, MaterialFilterData> m_MaterialData;

		std::mutex m_UserDataMutex; // Support multithread
		config::Field<nlohmann::json> f_CustomPointsJson;
		config::Field<nlohmann::json> f_FixedPointsJson;
		config::Field<nlohmann::json> f_CompletedPointsJson;
		config::Field<nlohmann::json> f_ExploredJson;   // Relic: "Only where you have been" - saved with the completed points
		
		config::Field<uint32_t> f_CustomPointIndex; // Stores last index for new custom points
		config::Field<uint32_t> f_LastUserID;

		std::unordered_set<PointData*> m_CustomPoints;
		std::unordered_set<PointData*> m_FixedPoints;
		std::list<PointData*> m_CompletedPoints;

		std::mutex m_PointMutex;
		// PointData* m_SelectedPoint;
		PointData* m_HoveredPoint;

		std::string m_SearchText;
	
		// Parsing map data
		PointData ParsePointData(const nlohmann::json& data);
		void LoadLabelData(const nlohmann::json& data, uint32_t sceneID, uint32_t labelID);
		void LoadCategoriaData(const nlohmann::json& data, uint32_t sceneID);
		void LoadSceneData(const nlohmann::json& data, uint32_t sceneID);
		void LoadScenesData();

		// Parsing ascension materials data
		void LoadMaterialFilterData(const nlohmann::json& data, std::string type);
		void LoadMaterialFilterData();

		
		void ApplySceneScalling(uint32_t sceneId, const ScallingInput& input);
		void ApplyScaling();

		void InitializeEntityFilter(game::IEntityFilter* filter, const std::string& clearName);
		void InitializeEntityFilters();

		void InitializeGatherDetectItems();

		// Loading user data
		using ResetElementFunc = bool (InteractiveMap::*)(LabelData* labelData, PointData* point);
		using LoadElementFunc = void (InteractiveMap::*)(LabelData* labelData, const nlohmann::json& data);
		using SaveElementFunc = void (InteractiveMap::*)(nlohmann::json& jObject, PointData* point);

		void ResetUserData(ResetElementFunc func);
		void LoadUserData(const nlohmann::json& data, LoadElementFunc func);
		void SaveUserData(nlohmann::json& data, SaveElementFunc func);

		void LoadCompletedPointData(LabelData* labelData, const nlohmann::json& data);
		void SaveCompletedPointData(nlohmann::json& jObject, PointData* point);
		bool ResetCompletedPointData(LabelData* label, PointData* point);
		void ReorderCompletedPointDataByTimestamp();
		
		void LoadCustomPointData(LabelData* labelData, const nlohmann::json& data);
		void SaveCustomPointData(nlohmann::json& jObject, PointData* point);
		bool ResetCustomPointData(LabelData* label, PointData* point);

		void LoadFixedPointData(LabelData* labelData, const nlohmann::json& data);
		void SaveFixedPointData(nlohmann::json& jObject, PointData* point);
		bool ResetFixedPointData(LabelData* label, PointData* point);

		void LoadCompletedPoints();
		void SaveCompletedPoints();
		void ResetCompletedPoints();

		void LoadCustomPoints();
		void SaveCustomPoints();
		void ResetCustomPoints();

		void LoadFixedPoints();
		void SaveFixedPoints();
		void ResetFixedPoints();

		void CreateUserDataField(const char* name, config::Field<nlohmann::json>& field, SaveAttachType saveType);
		void UpdateUserDataField(config::Field<nlohmann::json>& field, SaveAttachType saveType, bool move = false);
		std::string GetUserDataFieldSection(SaveAttachType saveType);

		void OnConfigProfileChanged();
		void OnAccountChanged(uint32_t userID);

		// Drawing
		void DrawMenu();
		void DrawMarkingKeys();
		void DrawExplorationSettings();
		void DrawMaterialFilters();
		void DrawMaterialFilterCategories(MaterialCategoryData& category, std::string type);
		void DrawMaterialFilter(MaterialData* material, std::string type);
		void DrawMaterials(uint32_t sceneID);
		void DrawFilters(const bool searchFixed = true);
		void DrawFilter(LabelData& label);

		void DrawPoint(const PointData& pointData, const ImVec2& screenPosition, float radius, float radiusSquared, ImTextureID texture, bool selectable = true);
		void DrawPoints();

		void DrawMinimapPoints();
		
		// Block interact
		void OnWndProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam, bool& cancelled);

		// Detecting stuff
		void OnGameUpdate();
		void CheckObjects();
		void OnItemGathered(game::Entity* entity, bool openedChest);
		void OnChestOpened(game::Entity* entity);
		void OnConchGathered(uint32_t gadgetId, game::Entity* entity);
		bool ClaimedByChestMarker(const SceneData& scene, game::Entity* entity, float nearestOwn);

		// Relic: keyboard marking. The keys fire on the window thread and only post a request; the game thread
		// serves it in OnKeysUpdate, the only one of the three that calls into the game for it.
		enum KeyRequest : int { KeyToggle = 1, KeyMark = 2, KeyUndo = 4 };
		std::atomic<int> m_KeyRequests{ 0 };
		PointData* m_SkipPoint = nullptr;     // game thread: the point the last undo took back, which the next Mark...
		int64_t m_SkipUntilMs = 0;            // ...before this skips once
		int64_t m_PassRecentUntilMs = 0;      // game thread: a Mark before this looks past the points marked a moment ago

		static bool HotkeysArmed();
		void OnToggleKey();
		void OnMarkKey();
		void OnUndoKey();
		void OnKeysUpdate();
		void ServeToggleKey();
		void ServeMarkKey();
		void ServeUndoKey();
		void NotifyAutoMark(const PointData* point);

		const LabelData* LabelOf(const PointData* point) const;
		std::string PointName(const PointData* point) const;
		std::string ProgressText(const PointData* point) const;

		// Relic: "Only where you have been". All of it lives on the render thread (DrawExternal and the menu) except
		// an account / profile switch, which repositions the field under the lock and asks for a reload.
		std::mutex m_ExploreFieldLock;                                // f_ExploredJson against its repositioning
		std::atomic<bool> m_ExploreReload{ false };                   // the field now belongs to another owner: reload, never save
		std::map<uint32_t, std::unordered_set<int64_t>> m_Explored;   // scene -> explored cells
		std::map<uint32_t, mapprogress::ExploreGrid> m_ExploreGrids;  // scene -> revealed cells (the scenes with a dataset)
		bool m_ExploreDirty = false;
		bool m_ExploreWasOn = false;
		int64_t m_ExploreSavedMs = 0;

		void InitExploreGrids();
		void ExploreTick();
		void ExploreSample();
		void LoadExplored();
		void SaveExplored();
		void RebuildExploreGrids();
		void ForgetExplored(uint32_t sceneID);
		void RepositionExploredField(SaveAttachType saveType, bool move);
		const mapprogress::ExploreGrid* ExploreGridFor(uint32_t sceneID) const;
		size_t ExploredCells(uint32_t sceneID) const;

		// Utility
		static PointData* FindNearestPoint(const LabelData& label, const app::Vector2& levelPosition, float range = 0.0f, bool completed = false);
		std::vector<InteractiveMap::LabelData*> FindLabelsByClearName(const std::string& clearName);

		// Hooks
		static void GadgetModule_OnGadgetInteractRsp_Hook(void* __this, app::GadgetInteractRsp* notify, MethodInfo* method);
		static void InLevelMapPageContext_UpdateView_Hook(app::InLevelMapPageContext* __this, MethodInfo* method);
		static void InLevelMapPageContext_ZoomMap_Hook(app::InLevelMapPageContext* __this, float value, MethodInfo* method);
		static void MonoMiniMap_Update_Hook(app::MonoMiniMap* __this, MethodInfo* method);
	};
}

