using namespace app;
// Relic: game 1.6 UnityPlayer.dll functions located by byte pattern from the 2.8 ones (tools/matcher16.py); none is used by the feature code

DO_APP_FUNC(0x00C9B540, app::Byte__Array*, Unity_RecordUserData, (int32_t nType));  // 1.6: byte pattern (64 bytes, rel32/RIP displacements masked), unique in UnityPlayer.dll (medium) UnityPlayer.dll
DO_APP_FUNC(0x00C3D9C0, Il2CppClass**, GetIl2Classes, ());  // 1.6: byte pattern (64 bytes, rel32/RIP displacements masked), unique in UnityPlayer.dll (medium) UnityPlayer.dll
DO_APP_FUNC(0x0115A790, int, CrashReporter, (__int64 a1, __int64 a2, const char* a3));  // 1.6: byte pattern (64 bytes, rel32/RIP displacements masked), unique in UnityPlayer.dll (medium) UnityPlayer.dll
DO_APP_FUNC(0x0, void, Animator_set_avatar, (Animator* __this, Avatar* value, MethodInfo* method));  // RELIC-TODO-16 pattern of 64 bytes matches 5 places in the 1.6 UnityPlayer.dll