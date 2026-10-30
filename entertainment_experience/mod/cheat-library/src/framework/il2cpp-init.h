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

// The module check of GetGameVersion (il2cpp-init.cpp): a module matches when its word sum is the entry's
// "checksum". The 3.2 client is known in two forms: UserAssembly.dll as Relic leaves it on disk (its login byte
// patch applied - the entry's "checksum") and as the game ships it (listed under "alsoAccepted"). The bytes the
// patch changes lie inside one function this build neither calls nor hooks, so either form is the build it was
// made for.
#if RELIC_GAME_VERSION == 32
#include <cheat-base/Logger.h>

template <class Scanner, class Json>
bool RelicModuleMatches(Scanner& scanner, const std::string& moduleName, const Json& checksumData)
{
	if (scanner.IsValidModuleHash(moduleName, checksumData))
		return true;
	if (!checksumData.contains("alsoAccepted"))
		return false;
	for (const auto& checksum : checksumData.at("alsoAccepted"))
	{
		Json other = { { "checksum", checksum }, { "timestamp", 0 } };
		if (scanner.IsValidModuleHash(moduleName, other))
		{
			LOG_INFO("Module %s is the other form this build accepts (%s).", moduleName.c_str(), checksum.dump().c_str());
			return true;
		}
	}
	return false;
}

#define RELIC_MODULE_MATCHES(scanner, moduleName, checksumData) RelicModuleMatches(scanner, moduleName, checksumData)
#else
#define RELIC_MODULE_MATCHES(scanner, moduleName, checksumData) scanner.IsValidModuleHash(moduleName, checksumData)
#endif
