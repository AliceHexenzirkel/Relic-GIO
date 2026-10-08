#include "pch.h"
#include <cheat-base/relic-diag.h>
#include <cheat-base/util.h>

#include <windows.h>
#include <cstdio>
#include <cstring>

namespace relic
{
	namespace diag
	{
		volatile long counters[COUNT] = { 0 };

		static char s_lastFault[192] = "none";
		static char s_note[192] = "";
		static CRITICAL_SECTION s_faultLock;
		static bool s_started = false;

		void tick(int stage)
		{
			if (stage >= 0 && stage < COUNT)
				InterlockedIncrement(&counters[stage]);
		}

		void note_fault(const char* what, unsigned code)
		{
			InterlockedIncrement(&counters[FAULTS]);
			if (!s_started)
				return;
			// TryEnter, never Enter: this runs from a frame that has just faulted, and a diagnostic must not
			// be able to block the render thread.
			if (TryEnterCriticalSection(&s_faultLock))
			{
				_snprintf_s(s_lastFault, sizeof(s_lastFault), _TRUNCATE, "0x%08X in %s", code,
					what == nullptr ? "?" : what);
				LeaveCriticalSection(&s_faultLock);
			}
		}

		void set_note(const char* text)
		{
			if (text == nullptr || !s_started)
				return;
			if (TryEnterCriticalSection(&s_faultLock))
			{
				strncpy_s(s_note, sizeof(s_note), text, _TRUNCATE);
				LeaveCriticalSection(&s_faultLock);
			}
		}

		void append_line(const char* text)
		{
			if (text == nullptr)
				return;
			// One WriteFile per line on a FILE_APPEND_DATA handle: Windows appends each write whole at the end of
			// the file, so a line can never be torn by the watchdog's own writes. No lock, no logger.
			static const std::string path = (util::GetCurrentPath() / "relic-diag.txt").string();
			char buffer[1024];
			int n = _snprintf_s(buffer, sizeof(buffer) - 2, _TRUNCATE, "%s", text);
			if (n < 0)
				n = (int)strlen(buffer);   // truncated - the line end below still goes on
			buffer[n++] = '\r';
			buffer[n++] = '\n';
			HANDLE file = CreateFileA(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
				nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
			if (file != INVALID_HANDLE_VALUE)
			{
				DWORD written = 0;
				WriteFile(file, buffer, (DWORD)n, &written, nullptr);
				CloseHandle(file);
			}
		}

		static const char* const kNames[COUNT] = {
			"present", "present1", "presentVt", "restores", "dx11In", "dx11Out", "renderIn", "keySeen",
			"toggle", "externalOut", "menuOut", "renderOut", "handlerRun", "handlerSkip", "faults"
		};

		static DWORD WINAPI WatchdogThread(LPVOID param)
		{
			std::string path = (util::GetCurrentPath() / "relic-diag.txt").string();
			long previous[COUNT] = { 0 };
			int line = 0;

			while (true)
			{
				Sleep(2000);

				char buffer[1024];
				int n = _snprintf_s(buffer, sizeof(buffer), _TRUNCATE, "[%4d] ", line++);
				bool moved = false;
				for (int i = 0; i < COUNT && n > 0; i++)
				{
					long value = counters[i];
					if (value != previous[i])
						moved = true;
					previous[i] = value;
					n += _snprintf_s(buffer + n, sizeof(buffer) - n, _TRUNCATE, "%s=%ld ", kNames[i], value);
				}
				if (TryEnterCriticalSection(&s_faultLock))
				{
					_snprintf_s(buffer + n, sizeof(buffer) - n, _TRUNCATE, "| fault: %s | %s\r\n",
						s_lastFault, s_note);
					LeaveCriticalSection(&s_faultLock);
				}
				else
					_snprintf_s(buffer + n, sizeof(buffer) - n, _TRUNCATE, "| (fault line busy)\r\n");

				// Win32 straight to the file: no C++ objects, no shared mutex with the logger.
				HANDLE file = CreateFileA(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
					nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
				if (file != INVALID_HANDLE_VALUE)
				{
					DWORD written = 0;
					WriteFile(file, buffer, (DWORD)strlen(buffer), &written, nullptr);
					CloseHandle(file);
				}
				if (!moved && line > 4)
					continue;   // keep writing anyway: a frozen board is exactly what we want to see
			}
			return 0;
		}

		void start()
		{
			if (s_started)
				return;
			InitializeCriticalSection(&s_faultLock);
			s_started = true;
			HANDLE thread = CreateThread(nullptr, 0, WatchdogThread, nullptr, 0, nullptr);
			if (thread != nullptr)
				CloseHandle(thread);
		}
	}
}
