// Generated C++ file by Il2CppInspector - http://www.djkaty.com - https://github.com/djkaty
// Custom injected code entry point

#pragma once
#include <Windows.h>

// Custom injected code entry point
void Run(HMODULE* hModule);

#if RELIC_DEVTEST
// The configuration file next to the DLL. A DevTest build (the dev-only test bed) reads and writes a file of its
// own, so a test session never rewrites the player's cfg.json.
#define RELIC_CONFIG_FILE "cfg-devtest.json"
#else
#define RELIC_CONFIG_FILE "cfg.json"
#endif