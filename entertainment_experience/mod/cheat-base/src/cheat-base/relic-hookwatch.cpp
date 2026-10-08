#include "pch.h"
#include <cheat-base/relic-hookwatch.h>
#include <cheat-base/relic-diag.h>

#include <windows.h>
#include <cstring>
#include <cstdio>

namespace relic
{
	namespace hookwatch
	{
		// 16 bytes covers every shape Detours writes on x64 (a 5-byte rel32 jump, or a 6-byte indirect jump
		// through an address slot) plus the padding it may rewrite around it.
		static const size_t kPatchSize = 16;
		static const int kMaxPatches = 4;

		struct Patch
		{
			void* target;
			const char* name;
			unsigned char installed[kPatchSize];
		};

		static Patch s_patches[kMaxPatches];
		static int s_count = 0;
		static bool s_started = false;
		static volatile long s_stopped = 0;
		static CRITICAL_SECTION s_lock;

		void stop()
		{
			InterlockedExchange(&s_stopped, 1);
		}

		// Names whoever owns an address, so the board says WHO overwrote the patch rather than just that it
		// happened: a jump into another module is a competing hook, the original prologue is a restore.
		static void describe(const void* address, char* out, size_t size)
		{
			HMODULE module = nullptr;
			if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
				static_cast<LPCSTR>(address), &module) && module != nullptr)
			{
				char path[MAX_PATH] = { 0 };
				if (GetModuleFileNameA(module, path, MAX_PATH) != 0)
				{
					const char* name = strrchr(path, '\\');
					_snprintf_s(out, size, _TRUNCATE, "%s+0x%llX", name == nullptr ? path : name + 1,
						(unsigned long long)((const unsigned char*)address - (const unsigned char*)module));
					return;
				}
			}
			_snprintf_s(out, size, _TRUNCATE, "%p (no module)", address);
		}

		static bool write_bytes(void* target, const unsigned char* bytes, size_t size)
		{
			DWORD previous = 0;
			if (!VirtualProtect(target, size, PAGE_EXECUTE_READWRITE, &previous))
				return false;
			memcpy(target, bytes, size);
			VirtualProtect(target, size, previous, &previous);
			FlushInstructionCache(GetCurrentProcess(), target, size);
			return true;
		}

		int repair_now()
		{
			if (!s_started || s_stopped)
				return 0;

			int repaired = 0;
			EnterCriticalSection(&s_lock);
			for (int i = 0; i < s_count; i++)
			{
				Patch& patch = s_patches[i];
				if (memcmp(patch.target, patch.installed, kPatchSize) == 0)
					continue;

				char note[192];
				const unsigned char* live = static_cast<const unsigned char*>(patch.target);
				char owner[96] = "not a jump";
				if (live[0] == 0xE9)   // jmp rel32: someone else's hook, and this says whose
				{
					int rel = *reinterpret_cast<const int*>(live + 1);
					describe(live + 5 + rel, owner, sizeof(owner));
				}
				_snprintf_s(note, sizeof(note), _TRUNCATE, "%s overwritten: %02X %02X %02X %02X %02X -> %s",
					patch.name, live[0], live[1], live[2], live[3], live[4], owner);
				diag::set_note(note);

				if (write_bytes(patch.target, patch.installed, kPatchSize))
				{
					repaired++;
					diag::tick(diag::RESTORES);
				}
			}
			LeaveCriticalSection(&s_lock);
			return repaired;
		}

		static DWORD WINAPI WatchThread(LPVOID)
		{
			while (true)
			{
				// Fast enough that at most a couple of frames go without the overlay, cheap enough to be free:
				// it is a 16-byte compare per patch.
				Sleep(250);
				repair_now();
			}
			return 0;
		}

		void protect(void* target, const char* name)
		{
			if (target == nullptr)
				return;

			if (!s_started)
			{
				InitializeCriticalSection(&s_lock);
				s_started = true;
			}

			EnterCriticalSection(&s_lock);
			if (s_count < kMaxPatches)
			{
				Patch& patch = s_patches[s_count];
				patch.target = target;
				patch.name = name;
				memcpy(patch.installed, target, kPatchSize);
				s_count++;

				char note[192];
				const unsigned char* live = static_cast<const unsigned char*>(target);
				_snprintf_s(note, sizeof(note), _TRUNCATE, "watching %s at %p: %02X %02X %02X %02X %02X",
					name, target, live[0], live[1], live[2], live[3], live[4]);
				diag::set_note(note);
			}
			bool first = (s_count == 1);
			LeaveCriticalSection(&s_lock);

			if (first)
			{
				HANDLE thread = CreateThread(nullptr, 0, WatchThread, nullptr, 0, nullptr);
				if (thread != nullptr)
					CloseHandle(thread);
			}
		}
	}
}
