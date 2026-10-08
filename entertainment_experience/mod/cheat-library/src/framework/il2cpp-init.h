// Generated C++ file by Il2CppInspector - http://www.djkaty.com - https://github.com/djkaty
// IL2CPP application initializer
//
// Relic: one DLL per game version, global clients only — the CHINA branch and the interactive
// "select your version" console prompt are gone. A client whose module checksums do not match the
// build's assembly_checksum.json is simply refused (init_il2cpp() returns false, nothing is hooked).

#pragma once

#include <string>

enum class LGameVersion
{
	NONE,
	GLOBAL
};

extern LGameVersion _gameVersion;

// IL2CPP application initializer. Resolves every app/API/TypeInfo pointer for the running client.
// Returns false when the client is not the build this DLL was made for: the caller must not start
// the cheat (every function pointer is still null).
bool init_il2cpp();
