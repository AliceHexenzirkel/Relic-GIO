// Generated C++ file by Il2CppInspector - http://www.djkaty.com - https://github.com/djkaty
// DLL entry point

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdlib>
#include <exception>

#include <il2cpp-init.h>
#include <main.h>

// Relic: the game never FreeLibrary()s us - the only detach we see is process termination, where the CRT
// still runs our static destructors in an order nobody controls. Whatever goes wrong in there must not turn
// into abort() + a Windows Error Reporting crash of GenshinImpact.exe: end the process quietly instead.
// ntdll is used because mhynot2 hooks kernelbase!TerminateProcess to return FALSE.
typedef LONG(NTAPI* NtTerminateProcess_t)(HANDLE, LONG);
static void RelicQuietExit()
{
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    NtTerminateProcess_t nt = ntdll ? (NtTerminateProcess_t)GetProcAddress(ntdll, "NtTerminateProcess") : nullptr;
    if (nt)
        nt((HANDLE)-1, 0);
    TerminateProcess(GetCurrentProcess(), 0);
    ExitThread(0);
}

static void ArmQuietExit()
{
    _set_purecall_handler([] { RelicQuietExit(); });
    std::set_terminate([] { RelicQuietExit(); });
    _set_invalid_parameter_handler([](const wchar_t*, const wchar_t*, const wchar_t*, unsigned, uintptr_t) { RelicQuietExit(); });
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
}

// DLL entry point
BOOL WINAPI DllMain( HMODULE hModule,
                       DWORD  ul_reason_for_call,
                       LPVOID lpReserved
                     )
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        CreateThread(NULL, 0, (LPTHREAD_START_ROUTINE) Run, new HMODULE(hModule), 0, NULL);
        break;
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
        break;
    case DLL_PROCESS_DETACH:
        if (lpReserved != nullptr) // process termination (not FreeLibrary): static destructors follow this call
            ArmQuietExit();
        break;
    }
    return TRUE;
}
