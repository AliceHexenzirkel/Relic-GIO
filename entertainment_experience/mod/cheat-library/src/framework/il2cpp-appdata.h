// Generated C++ file by Il2CppInspector - http://www.djkaty.com - https://github.com/djkaty
// IL2CPP application data
//
// Relic: single-offset macros. Upstream 3.3 carried (OS_OFFSET, CN_OFFSET, ...) pairs; Relic ships global
// clients only and builds one DLL per game version, so every appdata header holds ONE offset per entry
// (src/appdata-<ver>/, selected by the GameVersion MSBuild property). See il2cpp-init.cpp.

#pragma once

// Relic: game 1.6 (metadata v24) compiles every STATIC method with a leading dummy `this` (always null).
// A hook handler for such a method has to carry it too; on 2.8/3.3 these expand to nothing.
#if RELIC_GAME_VERSION <= 16
#define RELIC_STATIC_THIS     void* __this_unused,
#define RELIC_STATIC_THIS_ARG __this_unused,
#else
#define RELIC_STATIC_THIS
#define RELIC_STATIC_THIS_ARG
#endif

#include <cstdint>

// Application-specific types
#include "il2cpp-types.h"

// IL2CPP APIs
#define DO_API(OFFSET, RETURN_T, NAME, PARAMS) extern RETURN_T (*NAME) PARAMS
#include "il2cpp-api-functions.h"
#undef DO_API

// Application-specific functions
#define DO_APP_FUNC(OFFSET, RETURN_T, NAME, PARAMS) extern RETURN_T (*NAME) PARAMS
#define DO_APP_FUNC_METHODINFO(OFFSET, NAME) extern struct MethodInfo ** NAME
namespace app
{
	#include "il2cpp-functions.h"
	#include "il2cpp-unityplayer-functions.h"
}
#undef DO_APP_FUNC
#undef DO_APP_FUNC_METHODINFO

// Relic: on game 1.6 (metadata v24) every STATIC method is compiled with a leading dummy `this`,
// so those pointers above are declared as <name>__RAW with the extra parameter. These generated
// inline wrappers give the call sites back the original 3.3 signature. Empty on 2.8/3.3.
#include "il2cpp-static-thunks.h"

// TypeInfo pointers
#define DO_TYPEDEF(OFFSET, NAME) extern NAME ## __Class** NAME ## __TypeInfo
#define DO_SINGLETONEDEF(OFFSET, NAME) extern Singleton_1__Class** NAME ## __TypeInfo
namespace app
{
	#include "il2cpp-types-ptr.h"
}
#undef DO_TYPEDEF
#undef DO_SINGLETONEDEF
