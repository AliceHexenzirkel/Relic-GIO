#!/usr/bin/env python3
"""Idempotent fix for the exit-time crash (WER: BEX64 in CLibrary.dll, c0000409/7 = abort() from _purecall): at
process exit the CRT destroys the DLL's statics in an order where the global events (events::KeyUpEvent, ...)
die before the Hotkey fields of the feature singletons; ~Hotkey then calls the virtual removeHandler() on a
destroyed TEvent -> pure virtual call -> abort().

  1. the global events become immortal: `TEvent<> X = *new TEvent<>()` references (never destructed)
     - cheat-base/src/cheat-base/globals.{h,cpp}        KeyUpEvent, WndProcEvent, RenderEvent
     - cheat-library/src/user/cheat/events.{h,cpp}      GameUpdateEvent, AccountChangedEvent, MoveSyncEvent
     - cheat-base/src/cheat-base/config/Config.{h,cpp}  ProfileChanged
     - render/backend/dx11-hook.h, dx12-hook.h          the inline static events of the backends
  2. dllmain.cpp: on DLL_PROCESS_DETACH while the process is terminating, the purecall / terminate /
     invalid-parameter handlers end the process quietly through ntdll!NtTerminateProcess (kernelbase
     TerminateProcess is hooked to FALSE by mhynot2) instead of abort() + a WER crash report.
"""
import os, re

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.normpath(os.path.join(HERE, "..", "mod"))

def rd(p):
    with open(p, "r", encoding="utf-8", newline="") as f:
        return f.read()

def wr(p, s):
    with open(p, "w", encoding="utf-8", newline="") as f:
        f.write(s)

def immortal_pair(hdr, cpp, names):
    """names: list of (type, name). header: `extern T name;` -> `extern T& name;`; cpp: `T name{};`/`T name;` -> `T& name = *new T();`"""
    h = rd(hdr); c = rd(cpp); nl = "\r\n" if "\r\n" in c else "\n"; changed = 0
    for t, n in names:
        pat_h = re.compile(r"extern\s+" + re.escape(t) + r"\s+" + re.escape(n) + r"\s*;")
        if pat_h.search(h):
            h = pat_h.sub(f"extern {t}& {n};", h, count=1); changed += 1
        pat_c = re.compile(r"^(\s*)" + re.escape(t) + r"\s+" + re.escape(n) + r"\s*(\{\s*\})?\s*;", re.M)
        if pat_c.search(c):
            c = pat_c.sub(lambda m: f"{m.group(1)}{t}& {n} = *new {t}();  // Relic: immortal (never destructed at process exit)", c, count=1); changed += 1
    wr(hdr, h); wr(cpp, c)
    print(f"{os.path.basename(hdr)}/{os.path.basename(cpp)}: {changed} declarations made immortal")

def inline_static(hdrpath, names):
    s = rd(hdrpath); changed = 0
    for t, n in names:
        pat = re.compile(r"inline\s+static\s+" + re.escape(t) + r"\s+" + re.escape(n) + r"\s*\{\s*\}\s*;")
        if pat.search(s):
            s = pat.sub(f"inline static {t}& {n} = *new {t}();  // Relic: immortal", s, count=1); changed += 1
    wr(hdrpath, s); print(f"{os.path.basename(hdrpath)}: {changed} inline static events made immortal")

def dllmain():
    p = os.path.join(MOD, "cheat-library", "src", "framework", "dllmain.cpp")
    s = rd(p)
    if "RelicQuietExit" in s:
        print("dllmain.cpp: already patched"); return
    nl = "\r\n" if "\r\n" in s else "\n"
    new = nl.join([
        "// Generated C++ file by Il2CppInspector - http://www.djkaty.com - https://github.com/djkaty",
        "// DLL entry point",
        "",
        "#define WIN32_LEAN_AND_MEAN",
        "#include <windows.h>",
        "#include <cstdlib>",
        "#include <exception>",
        "",
        "#include <il2cpp-init.h>",
        "#include <main.h>",
        "",
        "// Relic: the game never FreeLibrary()s us - the only detach we see is process termination, where the CRT",
        "// still runs our static destructors in an order nobody controls. Whatever goes wrong in there must not turn",
        "// into abort() + a Windows Error Reporting crash of GenshinImpact.exe: end the process quietly instead.",
        "// ntdll is used because mhynot2 hooks kernelbase!TerminateProcess to return FALSE.",
        "typedef LONG(NTAPI* NtTerminateProcess_t)(HANDLE, LONG);",
        "static void RelicQuietExit()",
        "{",
        "    HMODULE ntdll = GetModuleHandleW(L\"ntdll.dll\");",
        "    NtTerminateProcess_t nt = ntdll ? (NtTerminateProcess_t)GetProcAddress(ntdll, \"NtTerminateProcess\") : nullptr;",
        "    if (nt)",
        "        nt((HANDLE)-1, 0);",
        "    TerminateProcess(GetCurrentProcess(), 0);",
        "    ExitThread(0);",
        "}",
        "",
        "static void ArmQuietExit()",
        "{",
        "    _set_purecall_handler([] { RelicQuietExit(); });",
        "    std::set_terminate([] { RelicQuietExit(); });",
        "    _set_invalid_parameter_handler([](const wchar_t*, const wchar_t*, const wchar_t*, unsigned, uintptr_t) { RelicQuietExit(); });",
        "    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);",
        "}",
        "",
        "// DLL entry point",
        "BOOL WINAPI DllMain( HMODULE hModule,",
        "                       DWORD  ul_reason_for_call,",
        "                       LPVOID lpReserved",
        "                     )",
        "{",
        "    switch (ul_reason_for_call)",
        "    {",
        "    case DLL_PROCESS_ATTACH:",
        "        CreateThread(NULL, 0, (LPTHREAD_START_ROUTINE) Run, new HMODULE(hModule), 0, NULL);",
        "        break;",
        "    case DLL_THREAD_ATTACH:",
        "    case DLL_THREAD_DETACH:",
        "        break;",
        "    case DLL_PROCESS_DETACH:",
        "        if (lpReserved != nullptr) // process termination (not FreeLibrary): static destructors follow this call",
        "            ArmQuietExit();",
        "        break;",
        "    }",
        "    return TRUE;",
        "}",
        ""])
    wr(p, new); print("dllmain.cpp: quiet-exit handlers armed on process termination")

if __name__ == "__main__":
    base = os.path.join(MOD, "cheat-base", "src", "cheat-base")
    immortal_pair(os.path.join(base, "globals.h"), os.path.join(base, "globals.cpp"),
                  [("TCancelableEvent<short>", "KeyUpEvent"),
                   ("TCancelableEvent<HWND, UINT, WPARAM, LPARAM>", "WndProcEvent"),
                   ("TEvent<>", "RenderEvent")])
    user = os.path.join(MOD, "cheat-library", "src", "user", "cheat")
    immortal_pair(os.path.join(user, "events.h"), os.path.join(user, "events.cpp"),
                  [("TEvent<>", "GameUpdateEvent"),
                   ("TEvent<uint32_t>", "AccountChangedEvent"),
                   ("TEvent<uint32_t, app::MotionInfo*>", "MoveSyncEvent")])
    immortal_pair(os.path.join(base, "config", "Config.h"), os.path.join(base, "config", "Config.cpp"),
                  [("TEvent<>", "ProfileChanged")])
    inline_static(os.path.join(base, "render", "backend", "dx11-hook.h"),
                  [("TEvent<ID3D11DeviceContext*>", "RenderEvent"),
                   ("TEvent<HWND, ID3D11Device*, ID3D11DeviceContext*, IDXGISwapChain*>", "InitializeEvent"),
                   ("TEvent<>", "FailedEvent")])
    inline_static(os.path.join(base, "render", "backend", "dx12-hook.h"),
                  [("TEvent<>", "PreRenderEvent"),
                   ("TEvent<ID3D12GraphicsCommandList*>", "PostRenderEvent"),
                   ("TEvent<HWND, ID3D12Device*, UINT, ID3D12DescriptorHeap*>", "InitializeEvent")])
    dllmain()
