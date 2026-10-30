#include "pch-il2cpp.h"
#include "DevTestChannel.h"

#if RELIC_DEVTEST

#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <fstream>

namespace cheat::feature::devtest
{
	std::string Utf8(const std::filesystem::path& p)
	{
		auto s = p.u8string();
		return std::string(s.begin(), s.end());
	}

	std::string Brief(const std::string& text)
	{
		return text.size() <= TEXT_MAX ? text : text.substr(0, TEXT_MAX) + "...";
	}

	std::string Dump(const nlohmann::json& j, int indent)
	{
		return j.dump(indent, ' ', false, nlohmann::json::error_handler_t::replace);
	}

	bool WriteFileAtomic(const std::filesystem::path& path, const std::string& bytes, std::string& error)
	{
		std::filesystem::path tmp = path;
		tmp += ".tmp";
		std::error_code ec, ignored;
		{
			std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
			if (!out)
			{
				error = "cannot write " + Utf8(tmp);
				return false;
			}
			out.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
			out.close();   // the last buffer goes out here, and a full disk may show only here
			if (!out)
			{
				std::filesystem::remove(tmp, ignored);
				error = "write failed: " + Utf8(tmp);
				return false;
			}
		}
		std::filesystem::rename(tmp, path, ec);
		if (ec.value() == ERROR_ACCESS_DENIED)
		{
			// The file is read-only, or a process has it open. Removing it first gets past a read-only file and
			// past a reader that shares the deletion; a reader that does not refuses both, and the file stays as
			// it is. Any other failure is not about the file in the way, which then must not be removed.
			std::filesystem::remove(path, ignored);
			std::filesystem::rename(tmp, path, ec);
		}
		if (ec)
		{
			error = fmt::format("cannot replace {}: error {} ({})", Utf8(path), ec.value(), ec.message());
			std::filesystem::remove(tmp, ignored);
			return false;
		}
		return true;
	}

	int64_t NowMs()
	{
		return static_cast<int64_t>(GetTickCount64());
	}

	int64_t AwakeMs()
	{
		ULONGLONG ticks = 0;   // of 100 ns
		if (!QueryUnbiasedInterruptTime(&ticks))
			return NowMs();
		return static_cast<int64_t>(ticks / 10000);
	}

	int64_t WallMs()
	{
		FILETIME ft{};
		GetSystemTimeAsFileTime(&ft);
		ULARGE_INTEGER t{};
		t.LowPart = ft.dwLowDateTime;
		t.HighPart = ft.dwHighDateTime;
		// 100 ns since 1601-01-01; 11644473600 s lie between that day and 1970-01-01
		return static_cast<int64_t>(t.QuadPart / 10000) - 11644473600000LL;
	}

	VecField ReadVec(const nlohmann::json& j, const char* key, float out[3])
	{
		auto it = j.find(key);
		if (it == j.end())
			return VecField::Absent;
		if (!it->is_array() || it->size() != 3)
			return VecField::Bad;
		for (size_t i = 0; i < 3; i++)
		{
			const nlohmann::json& v = (*it)[i];
			if (!v.is_number())
				return VecField::Bad;
			double d = v.get<double>();
			if (!(std::fabs(d) <= 1.0e30))
				return VecField::Bad;
			out[i] = static_cast<float>(d);
		}
		return VecField::Good;
	}

	bool InWorld(const float p[3])
	{
		for (int i = 0; i < 3; i++)
			if (!(std::fabs(p[i]) <= WORLD_MAX))   // a NaN fails the comparison too
				return false;
		return true;
	}

	bool SafeStem(const std::string& s)
	{
		if (s.empty() || s.size() > 64)
			return false;
		std::string upper;
		for (char c : s)
		{
			bool lower = c >= 'a' && c <= 'z';
			if (!(lower || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
				return false;
			upper.push_back(lower ? static_cast<char>(c - 'a' + 'A') : c);
		}
		if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL")
			return false;
		if (upper.size() == 4 && (upper.compare(0, 3, "COM") == 0 || upper.compare(0, 3, "LPT") == 0) && upper[3] >= '0' && upper[3] <= '9')
			return false;
		return true;
	}

	CommandFile ReadCommand(const std::filesystem::path& path, nlohmann::json& cmd, std::string& problem)
	{
		problem.clear();
		std::error_code ec;
		uintmax_t size = std::filesystem::file_size(path, ec);
		if (ec)
			return CommandFile::Later;
		if (size > COMMAND_MAX)
		{
			problem = "cmd.json is larger than a command can be";
			return CommandFile::Refused;
		}
		std::string text;
		{
			std::ifstream in(path, std::ios::binary);
			if (!in)
				return CommandFile::Later;
			text.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
		}
		if (text.empty())
			return CommandFile::Later;
		try
		{
			cmd = nlohmann::json::parse(text);
		}
		catch (const std::exception& e)
		{
			problem = Brief(std::string("cmd.json: ") + e.what());
			return CommandFile::Later;
		}
		if (!cmd.is_object() || !cmd.contains("id") || !cmd["id"].is_number_integer() || cmd["id"].get<int64_t>() == 0)
		{
			problem = "a command is an object with an integer id other than 0 and an op";
			return CommandFile::Refused;
		}
		return CommandFile::Ready;
	}

	namespace
	{
		wchar_t g_LogPath[1024] = {};   // set once, before the other threads start; read-only afterwards
	}

	void SetLogPath(const std::filesystem::path& path)
	{
		wcsncpy_s(g_LogPath, path.wstring().c_str(), _TRUNCATE);
	}

	void LogLine(const char* thread, const char* format, ...)
	{
		if (g_LogPath[0] == 0)
			return;
		char buffer[1024];
		SYSTEMTIME t{};
		GetLocalTime(&t);
		int n = _snprintf_s(buffer, sizeof(buffer) - 2, _TRUNCATE, "%04u-%02u-%02u %02u:%02u:%02u.%03u [%s] ",
			t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, thread == nullptr ? "?" : thread);
		if (n < 0)
			n = static_cast<int>(strlen(buffer));
		va_list args;
		va_start(args, format);
		int m = _vsnprintf_s(buffer + n, sizeof(buffer) - 2 - n, _TRUNCATE, format, args);
		va_end(args);
		n = m < 0 ? static_cast<int>(strlen(buffer)) : n + m;   // a cut line still ends with its line break
		buffer[n++] = '\r';
		buffer[n++] = '\n';
		HANDLE file = CreateFileW(g_LogPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
			nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
		if (file != INVALID_HANDLE_VALUE)
		{
			DWORD written = 0;
			WriteFile(file, buffer, static_cast<DWORD>(n), &written, nullptr);
			CloseHandle(file);
		}
	}
}
#endif
