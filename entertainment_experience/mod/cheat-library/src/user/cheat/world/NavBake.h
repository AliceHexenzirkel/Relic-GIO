#pragma once
#include <il2cpp-appdata.h>

#include <cheat-base/cheat/Feature.h>
#include <cheat-base/config/config.h>

#include <atomic>
#include <cstdint>
#include <filesystem>
#include <mutex>
#include <string>
#include <vector>

#if RELIC_GAME_VERSION == 28
namespace cheat::feature
{
	// Relic: "Navmesh Bake" - bakes a Unity navmesh from the colliders the game has loaded around the player and
	// writes the tiles to a file, so the private server's pathfindingserver can be given navmesh data for a scene
	// the vendor's package lacks (the 2.8 archipelago). The game's own navmesh builder does the work, with the
	// settings the vendor's files carry; what comes out are tiles of the server's own version (17) whose
	// per-polygon region ids the builder leaves unset - build/navmesh_tool.py fromunity computes them when it
	// turns the tiles into the server's files. See the .cpp.
	//
	// Two ways in: the hotkey bakes the box around the player; a driver script drops commands into
	// <mod folder>/navbake/cmd.json (status, layers, navkey, polygons, teleport, bake, peek) and reads the answers
	// next to it, so a whole scene can be walked and baked without anyone at the keyboard.
	class NavBake : public Feature
	{
	public:
		// The layers the game's own navmesh bake collects its colliders from: Terrain (8), SceneProp (11) and
		// ScenePropIgnoreCamera (21). Every other layer adds colliders that are not ground - the capsules of
		// characters and monsters among them.
		static constexpr int GAME_BAKE_LAYERS = (1 << 8) | (1 << 11) | (1 << 21);

		config::Field<bool> f_Enabled;          // serve navbake/cmd.json
		config::Field<Hotkey> f_BakeKey;        // bake the box around the player now
		config::Field<float> f_BoxSize;         // metres on x and z, the box the key bakes
		config::Field<float> f_BoxHeight;       // metres on y
		config::Field<int> f_LayerMask;         // the colliders' layers the key bakes (-1 = every layer)
		config::Field<int> f_CullMask;          // the builder's cullingAreaMask argument

		static NavBake& GetInstance();

		const FeatureGUIInfo& GetGUIInfo() const override;
		void DrawMain() override;

		bool NeedStatusDraw() const override;
		void DrawStatus() override;

		void OnGameUpdate();

		// One bake: a world-space box and where its tiles go. Served on the game thread.
		struct BakeRequest
		{
			int64_t id = 0;
			float center[3] = { 0, 0, 0 };
			float extents[3] = { 0, 0, 0 };
			int layerMask = GAME_BAKE_LAYERS;
			int cullMask = 0;
			int geometry = 1;   // NavMeshCollectGeometry: 1 = the physics colliders, 0 = the render meshes
			bool sync = false;
			bool key = false;   // started by the key: no command waits for its end
			std::string out;    // file stem under navbake/
		};

	private:
		NavBake();
		void OnBakeKey();

		// game thread
		void ServeHotkey();
		void NoteStaleCommand();
		void ServeCommands();
		void WriteStatus();
		void PollBake();
		bool StartBake(const BakeRequest& req, std::string& error);
		void FinishBake(bool ok, const std::string& error);
		void Execute(const nlohmann::json& cmd);
		void WriteResult(const nlohmann::json& result);
		void FlushResult();
		void Note(std::string text);

		struct Inflight;
		struct BakeReport;
		void DiscardBake(Inflight& f);
		void ReleaseBake(Inflight& f);
		void SaveBake(Inflight& f, BakeReport& report);

		std::filesystem::path m_Dir;
		std::atomic<bool> m_KeyPressed{ false };
		int64_t m_LastPollMs = 0;
		int m_ManualNext = 1;                                // where the search for a free manual_<n> stem starts

		// the command file and its answer (game thread only)
		bool m_Serving = false;                              // set by the first poll that finds the option on
		int64_t m_LastCommandId = 0;                         // the command last served, or the one found when serving began
		std::filesystem::file_time_type m_ReportedStamp{};   // cmd.json's time stamp when its problem was last reported
		std::string m_PendingResult;                         // result.json's text until the file has it
		bool m_PendingResultLogged = false;

		// the bake in flight (game thread only)
		Inflight* m_Inflight = nullptr;
		// the last bake's NavMeshData, kept for diagnostics until the next bake ends (game thread only)
		Il2CppObject* m_LastData = nullptr;
		uint32_t m_LastDataHandle = 0;

		std::mutex m_NoteMutex;
		std::string m_LastNote;
		std::atomic<bool> m_Busy{ false };
	};
}
#endif
