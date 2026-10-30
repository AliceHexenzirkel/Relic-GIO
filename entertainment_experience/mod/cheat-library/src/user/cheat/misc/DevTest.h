#pragma once

#if RELIC_DEVTEST
#include <il2cpp-appdata.h>

#include <cheat-base/cheat/Feature.h>
#include <cheat-base/config/config.h>

#include <cstdint>
#include <filesystem>
#include <map>
#include <mutex>
#include <string>

#include <nlohmann/json.hpp>

namespace cheat::feature
{
	// The dev-only test bed. It exists only in a DevTest build (the DevTest|x64 configuration, RELIC_DEVTEST; the
	// binary carries the DEVTESTMARKER resource every release gate refuses) and is never part of a release. A
	// driver script on this PC writes commands into <mod folder>/devtest/cmd.json and reads the answers there:
	// status, waits, screenshots of the back buffer, teleports, the free camera, entity lists, the game's UI tree
	// and its buttons, the SDK login, transient feature switches and a clean quit - all in-process, through the
	// game's own code, with no input of any kind and no window touched, so it works with the game in the
	// background. See the .cpp for the protocol and docs/ENTERTAINMENT-EXPERIENCE.md ("DevTest").
	class DevTest : public Feature
	{
	public:
		config::Field<bool> f_Enabled;   // serve devtest/cmd.json

		static DevTest& GetInstance();

		const FeatureGUIInfo& GetGUIInfo() const override;
		void DrawMain() override;

		void OnGameUpdate();
		void OnWatchdog();

	private:
		DevTest();

		// game thread
		void Poll();
		void NoteStaleCommand();
		void ServeCommands();
		void Execute(const nlohmann::json& cmd);
		nlohmann::json StatusFields();
		void WriteStatus();
		void Answer(int64_t id, const nlohmann::json& answer);
		void FlushAnswers();
		void AnswerFinished();
		void RestoreAll(const char* why);
		void Note(const std::string& text);

		std::filesystem::path m_Dir;
		int64_t m_LastPollMs = 0;
		bool m_Serving = false;
		int64_t m_LastCommandId = 0;
		std::filesystem::file_time_type m_ReportedStamp{};
		std::map<int64_t, std::string> m_PendingAnswers;   // results/<id>.json text until the file has it
		bool m_PendingLogged = false;
		int64_t m_Seq = 0;
		int64_t m_Ticks = 0;
		int m_Silence = 0;                                 // GameUpdate ticks since OnGameUpdate last ran (OnWatchdog)

		std::mutex m_NoteMutex;                            // DrawMain's copy of the state, nothing else
		std::string m_Note;
	};
}
#endif
