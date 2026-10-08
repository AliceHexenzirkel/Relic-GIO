// Generated C++ file by Il2CppInspector - http://www.djkaty.com - https://github.com/djkaty
// IL2CPP application initializer
//
// Relic: single-offset macros (see il2cpp-appdata.h), no CN column, no interactive version prompt.
// Upstream behaviour on a checksum mismatch was to open a console and read the version from stdin —
// unacceptable for a DLL Relic injects for players. Now: log, return false, hook nothing.

#include "pch-il2cpp.h"

#include "il2cpp-init.h"
#include "helpers.h"

#include <cheat/ILPatternScanner.h>

#include <windows.h>

// Relic: pins every generated struct member to the offset the game's metadata dump reports for it, so a
// layout that drifts stops the build instead of handing a feature a pointer read out of a neighbouring
// field. Included from exactly one translation unit - the assertions are free at run time but not at
// compile time. Regenerate with tools/gen_field_asserts.py after touching the appdata headers.
#include "il2cpp-types-asserts.h"

// IL2CPP APIs
#define DO_API(OFFSET, RETURN_T, NAME, PARAMS) RETURN_T (*NAME) PARAMS
#include "il2cpp-api-functions.h"
#undef DO_API

// Application-specific functions
#define DO_APP_FUNC(OFFSET, RETURN_T, NAME, PARAMS) RETURN_T (*NAME) PARAMS
#define DO_APP_FUNC_METHODINFO(OFFSET, NAME) struct MethodInfo ** NAME
namespace app
{
#include "il2cpp-functions.h"
#include "il2cpp-unityplayer-functions.h"
}
#undef DO_APP_FUNC
#undef DO_APP_FUNC_METHODINFO

// TypeInfo pointers
#define DO_TYPEDEF(OFFSET, NAME) NAME ## __Class** NAME ## __TypeInfo
#define DO_SINGLETONEDEF(OFFSET, NAME) Singleton_1__Class** NAME ## __TypeInfo
namespace app
{
#include "il2cpp-types-ptr.h"
#include <resource.h>
}
#undef DO_TYPEDEF
#undef DO_SINGLETONEDEF

LGameVersion _gameVersion = LGameVersion::NONE;

static void init_static_offsets()
{
	// Relic: an offset of 0 means "not resolved for this game build" -> null pointer, never module base.
	// Get base address of IL2CPP module
	uintptr_t baseAddress = il2cppi_get_base_address();

	// Define IL2CPP API function addresses
	#define DO_API(OFFSET, RETURN_T, NAME, PARAMS) NAME = (OFFSET) ? (RETURN_T (*) PARAMS)(baseAddress + (OFFSET)) : nullptr
	#include "il2cpp-api-functions.h"
	#undef DO_API

	// Define function addresses
	#define DO_APP_FUNC(OFFSET, RETURN_T, NAME, PARAMS) NAME = (OFFSET) ? (RETURN_T (*) PARAMS)(baseAddress + (OFFSET)) : nullptr
	#define DO_APP_FUNC_METHODINFO(OFFSET, NAME) NAME = (OFFSET) ? (struct MethodInfo **)(baseAddress + (OFFSET)) : nullptr
	#include "il2cpp-functions.h"
	#undef DO_APP_FUNC
	#undef DO_APP_FUNC_METHODINFO

	// Define TypeInfo variables
	#define DO_SINGLETONEDEF(OFFSET, NAME) NAME ## __TypeInfo = (OFFSET) ? (Singleton_1__Class**) (baseAddress + (OFFSET)) : nullptr
	#define DO_TYPEDEF(OFFSET, NAME) NAME ## __TypeInfo = (OFFSET) ? (NAME ## __Class**) (baseAddress + (OFFSET)) : nullptr
	#include "il2cpp-types-ptr.h"
	#undef DO_TYPEDEF
	#undef DO_SINGLETONEDEF

	uintptr_t unityPlayerAddress = il2cppi_get_unity_address();
	// Define UnityPlayer functions
	#define DO_APP_FUNC(OFFSET, RETURN_T, NAME, PARAMS) NAME = (OFFSET) ? (RETURN_T (*) PARAMS)(unityPlayerAddress + (OFFSET)) : nullptr
	#define DO_APP_FUNC_METHODINFO(OFFSET, NAME) NAME = (OFFSET) ? (struct MethodInfo **)(unityPlayerAddress + (OFFSET)) : nullptr
	#include "il2cpp-unityplayer-functions.h"
	#undef DO_APP_FUNC
	#undef DO_APP_FUNC_METHODINFO
}

#ifdef _PATTERN_SCANNER
// Release_WS: resolve by signature / live metadata first, fall back to the static offset. This is the
// offset-discovery build used while porting to another game version (see tools/ and the docs).
static void init_scanned_offsets()
{
	// Get base address of IL2CPP module
	uintptr_t baseAddress = il2cppi_get_base_address();

#define SELECT_OR(container, type, val, def) { auto value = val; if (value == 0) container = (type)(def); else container = (type)val; }

	static config::Field<nlohmann::json> offsetDataField = config::CreateField<nlohmann::json>("OffsetData", "OffsetData", "PatternScanner", true, nlohmann::json::object());

	std::string signatures = ResourceLoader::Load("Signatures", RT_RCDATA);

	auto scanner = ILPatternScanner();
	scanner.ParseSignatureFile(signatures);
	scanner.LoadJson(offsetDataField);

	using namespace app;

	// Define IL2CPP API function addresses
	#define DO_API(OFFSET, RETURN_T, NAME, PARAMS) SELECT_OR(NAME, RETURN_T (*) PARAMS, scanner.SearchAPI(#NAME), ((OFFSET) ? baseAddress + (OFFSET) : 0))
	#include "il2cpp-api-functions.h"
	#undef DO_API

	il2cpp_thread_attach(il2cpp_domain_get());

	// Define function addresses
	#define DO_APP_FUNC(OFFSET, RETURN_T, NAME, PARAMS) SELECT_OR(NAME, RETURN_T (*) PARAMS, scanner.Search("UserAssembly.dll", #NAME), ((OFFSET) ? baseAddress + (OFFSET) : 0))
	#define DO_APP_FUNC_METHODINFO(OFFSET, NAME) SELECT_OR(NAME, struct MethodInfo **, scanner.SearchMethodInfo(#NAME), ((OFFSET) ? baseAddress + (OFFSET) : 0))
	#include "il2cpp-functions.h"
	#undef DO_APP_FUNC
	#undef DO_APP_FUNC_METHODINFO

	// Define TypeInfo variables
	#define DO_SINGLETONEDEF(OFFSET, NAME) SELECT_OR(NAME ## __TypeInfo, Singleton_1__Class**, scanner.SearchTypeInfo(#NAME), ((OFFSET) ? baseAddress + (OFFSET) : 0))
	#define DO_TYPEDEF(OFFSET, NAME) SELECT_OR(NAME ## __TypeInfo, NAME ## __Class**, scanner.SearchTypeInfo(#NAME), ((OFFSET) ? baseAddress + (OFFSET) : 0))
	#include "il2cpp-types-ptr.h"
	#undef DO_TYPEDEF
	#undef DO_SINGLETONEDEF

	uintptr_t unityPlayerAddress = il2cppi_get_unity_address();
	// Define UnityPlayer functions
	#define DO_APP_FUNC(OFFSET, RETURN_T, NAME, PARAMS) SELECT_OR(NAME, RETURN_T (*) PARAMS, scanner.Search("UnityPlayer.dll", #NAME), ((OFFSET) ? unityPlayerAddress + (OFFSET) : 0))
	#define DO_APP_FUNC_METHODINFO(OFFSET, NAME) SELECT_OR(NAME, struct MethodInfo **, scanner.SearchMethodInfo(#NAME), ((OFFSET) ? unityPlayerAddress + (OFFSET) : 0))
	#include "il2cpp-unityplayer-functions.h"
	#undef DO_APP_FUNC
	#undef DO_APP_FUNC_METHODINFO

	if (scanner.IsUpdated())
	{
		scanner.SaveJson(offsetDataField);
		offsetDataField.FireChanged();

		LOG_INFO("Seems like some offsets was found for a first time. Recommend to restart game for correct cheat and game work.");
	}

#undef SELECT_OR
}
#endif

// The build's res/assembly_checksum.json describes exactly one client ("global": game_version + the
// 64-bit word-sum checksum of UserAssembly.dll and UnityPlayer.dll, see PatternScanner::IsValidModuleHash).
// Anything else is "not our client".
static LGameVersion GetGameVersion()
{
	std::string targetChecksumsRaw = ResourceLoader::Load("AssemblyChecksums", RT_RCDATA);
	nlohmann::json targetChecksums = nlohmann::json::parse(targetChecksumsRaw, nullptr, false);
	if (targetChecksums.is_discarded())
	{
		LOG_ERROR("Failed to parse assembly checksum data.");
		return LGameVersion::NONE;
	}

	static config::Field<nlohmann::json> checksumTimestamps =
		config::CreateField<nlohmann::json>("m_CheckSumTimestamp", "PatternScanner", true, nlohmann::json::object());

	if (!targetChecksums.contains("global"))
	{
		LOG_ERROR("Assembly checksum info is corrupted. Not found the key: global.");
		return LGameVersion::NONE;
	}

	PatternScanner scanner;
	nlohmann::json lVersionChecksum = targetChecksums.at("global");
	std::string version = lVersionChecksum.at("game_version");

	for (auto& [moduleName, checksumData] : lVersionChecksum.at("modules").items())
	{
		if (!scanner.IsValidModuleHash(moduleName, checksumData))
		{
			LOG_ERROR("Module %s does not match the %s build this library was made for.", moduleName.c_str(), version.c_str());
			return LGameVersion::NONE;
		}

		checksumTimestamps.value()[moduleName] = scanner.GetModuleTimestamp(moduleName);
	}

	checksumTimestamps.FireChanged();

	LOG_INFO("Detected version: %s.", version.c_str());
	return LGameVersion::GLOBAL;
}

// IL2CPP application initializer
bool init_il2cpp()
{
	_gameVersion = GetGameVersion();
	if (_gameVersion == LGameVersion::NONE)
	{
		LOG_ERROR("This library was built for another game build (module checksums do not match) - nothing is hooked.");
		return false;
	}

#ifdef _PATTERN_SCANNER
	init_scanned_offsets();
#else
	init_static_offsets();
#endif
	return true;
}
