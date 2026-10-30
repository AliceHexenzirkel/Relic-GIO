// Relic: declarations for game 3.2 that the upstream 3.2 types (il2cpp-types.h, ee9258a) lack and that the 0x0
// entries of il2cpp-functions.h / il2cpp-types-ptr.h name. Declarations only, never a layout: no 3.2 code
// dereferences one of them (the users are compiled out for 3.2), and a layout guessed from another game version
// would read the wrong field without any build error.
#pragma once

#if !defined(_GHIDRA_) && !defined(_IDA_)
namespace app {
#endif

    struct GadgetModule;
    struct LevelModule;
    struct List_1_System_String_;
    struct Proto_SceneEntityAppearNotify;
    struct RSA;

    struct Proto_SceneAvatarInfo__Class;
    struct Proto_SceneGadgetInfo__Class;
    struct Proto_SceneMonsterInfo__Class;
    struct Proto_SceneNpcInfo__Class;

    enum class GeneralDialogContext_GeneralDialogType__Enum : int32_t;
    enum class SystemLanguage__Enum : int32_t;
    enum class UIShowPriority__Enum : int32_t;

#if !defined(_GHIDRA_) && !defined(_IDA_)
}
#endif
