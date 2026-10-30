#pragma once

#if RELIC_DEVTEST
#include <cstdint>
#include <filesystem>
#include <stdexcept>
#include <string>

#include <nlohmann/json.hpp>
#include <fmt/format.h>

// The file protocol of the dev-only test bed (DevTest.cpp): the helpers NavBake's command channel uses, copied
// rather than shared - NavBake is a shipped feature of the 2.8 build and keeps its own copy.
namespace cheat::feature::devtest
{
	constexpr uintmax_t COMMAND_MAX = 1 << 20;   // bytes; a command is a few hundred
	constexpr size_t TEXT_MAX = 300;             // characters of outside text a log line or an answer carries
	constexpr float WORLD_MAX = 100000.0f;       // no scene reaches 100 km from its origin

	std::string Utf8(const std::filesystem::path& p);

	// Outside text - a parse error that quotes the file, a game string - cut to what a log line and an answer carry.
	std::string Brief(const std::string& text);

	// The text of a JSON file; a string that is not UTF-8 is written with replacement characters.
	std::string Dump(const nlohmann::json& j, int indent = -1);

	// A whole-or-nothing write: <path>.tmp, then a rename over the file.
	bool WriteFileAtomic(const std::filesystem::path& path, const std::string& bytes, std::string& error);

	int64_t NowMs();     // milliseconds since boot (GetTickCount64)
	int64_t AwakeMs();   // milliseconds the system has been awake: the clock every time-out runs on
	int64_t WallMs();    // milliseconds since 1970-01-01 UTC

	enum class VecField { Absent, Good, Bad };

	// [x, y, z] under `key`: three numbers a float holds.
	VecField ReadVec(const nlohmann::json& j, const char* key, float out[3]);

	// A place a scene can have.
	bool InWorld(const float p[3]);

	// A field of a command, or `fallback` when the command does not carry it. A value of another type is refused
	// by the key's name, which the JSON library's own message leaves out.
	template <class T>
	T Arg(const nlohmann::json& cmd, const char* key, const T& fallback)
	{
		auto it = cmd.find(key);
		if (it == cmd.end() || it->is_null())
			return fallback;
		try
		{
			return it->get<T>();
		}
		catch (const nlohmann::json::exception& e)
		{
			throw std::invalid_argument(fmt::format("{}: {}", key, e.what()));
		}
	}

	// A file stem a command may name: letters, digits, '_' and '-', at most 64, none of the names Windows keeps
	// for its devices whatever extension follows them.
	bool SafeStem(const std::string& s);

	enum class CommandFile { Ready, Later, Refused };

	// cmd.json as a poll finds it. Ready: a command - an object with an integer id other than 0. Later: nothing
	// whole to read yet (`problem` carries the parser's words when there is text). Refused: a whole document
	// that is not a command.
	CommandFile ReadCommand(const std::filesystem::path& path, nlohmann::json& cmd, std::string& problem);

	// devtest.log: one line per call, appended whole with a single WriteFile on a FILE_APPEND_DATA handle - from any
	// thread but the render thread, with no lock and without the mod's logger (whose event is not safe to fire
	// from two threads at once). SetLogPath is called once, before any other thread can log.
	void SetLogPath(const std::filesystem::path& path);
	void LogLine(const char* thread, _Printf_format_string_ const char* format, ...);
}
#endif
