# VENDOR.md — provenance of everything under `entertainment_experience/`

Imported 2026-08-23. Nothing here is built by `build/publish.ps1`; the per-version DLLs are built by
`tools/build_ee.ps1` and committed as `payload/<ver>/ee/CLibrary.dll.bin` at milestones only.

## `mod/` — Akebi GC, as published by NctimeAza (Apache-2.0)

| | |
|---|---|
| Upstream | https://github.com/NctimeAza/AnimeGame-Cheat-3.3 |
| Ref | tag `v1.2.3` = commit `f5ab4e79b05e374911fb66483706504476713783` (2023-01-05, author Callow) |
| Licence | Apache License 2.0 (`mod/LICENSE`, unmodified copy also at `../LICENSE`) |
| Origin | A verbatim re-publication of **Akebi-GC** (`Akebi-Group/Akebi-GC`, DMCA 2022-09-21; `Taiga74164/Akebi-GC`, DMCA 2022-11-14, HTTP 451 today), itself grown from **CCGenshin** by Callow (CallowBlack). The tags of the NctimeAza repo carry the real upstream history; its `master` is an unrelated 4-commit re-import — do not use it. |
| Pristine import | git tag `ee-vendor-v1.2.3` on this repository marks the import commit; `git diff ee-vendor-v1.2.3 -- entertainment_experience/mod` is the exact list of Relic's changes (Apache §4(b)). |

### Deliberately NOT imported
- `.git/`, `.gitmodules` — the seven git submodules are vendored as pruned snapshots (below).
- `injector/` — never used by Relic: the game is started by mhynot2's `launcher.exe`, which injects several DLLs itself. The upstream injector also demands `RequireAdministrator`.
- `.github/` — the CI workflow downloads and runs an unpinned remote EXE. The repository's own workflow (`.github/workflows/dlls.yml`, "DLL builds") builds the DLL from the vendored tree with MSBuild alone.
- Heavy assets, kept locally in `heavy_ref/` (gitignored by the repo-wide `*_ref/` rule): `cheat-library/res/iconsHD/` (41 MB HD icon set), `cheat-library/res/NotoSansSC-Medium.otf` (8.5 MB), `cheat-library/res/MiSans-Regular.ttf` (7.9 MB), `cheat-library/res/ubuntu_bold.ttf`. `res/res.rc` no longer references them (phase-2 resource diet; `tools/relic_resource_diet.py`), so a clean clone builds without `heavy_ref/`. Exception since 2026-09-15: `tools/make_hd_icons.py` regenerates a 128 px subset of `iconsHD/` (only what the build uses) into the tracked `res/iconsHD/`, so a clean clone still builds without `heavy_ref/`.
- Upstream `mod/.gitignore` lines `debugger.cpp`, `ModelChanger.h`, `ModelChanger.cpp` were removed: they hid the tracked file `cheat-library/src/user/cheat/debugger.cpp`.

### Third-party code under `mod/cheat-base/vendor/` (snapshots at the SHAs the v1.2.3 superproject pinned)

| Dir | Upstream | Pinned commit | Licence | What is vendored |
|---|---|---|---|---|
| `imgui/` | `CallowBlack/imgui-brightness-fix` (fork of ocornut/imgui) | `47fb633e73f69e1ba57cef16cd077ddc4d97f0ee` — **repository deleted**; vendored from the surviving snapshot https://github.com/keleearth/imgui-brightness-fix @ `9362f724` ("Initial commit", 2023-12-27; `IMGUI_VERSION "1.88 WIP"`, `IMGUI_VERSION_NUM 18721`). The pin was set in Akebi on 2022-05-26 and never moved again, so the snapshot is believed identical; the compile of phase 1 is the check. | MIT (`imgui/LICENSE.txt`) | core `imgui*.cpp/h`, `imconfig.h`, `imstb_*.h`, `backends/imgui_impl_{dx11,dx12,win32}.*`, `misc/cpp/imgui_stdlib.*` |
| `fmt/` | https://github.com/fmtlib/fmt | `86e27ccb41c2708e17ba264359223ef664d9bb78` | MIT (`fmt/LICENSE.rst`) | `include/`, `src/format.cc`, `src/os.cc` |
| `json/` | https://github.com/nlohmann/json | `a94430615d8360272151f602b8c9eeb58509ecde` | MIT (`json/LICENSE.MIT`) | `single_include/nlohmann/json.hpp` |
| `magic_enum/` | https://github.com/Neargye/magic_enum | `d1ccd22c853ac3afaf7a0f1c86019ea641e777c5` | MIT | `include/` |
| `simpleIni/` | https://github.com/brofield/simpleini | `9b3ed7ec815997bc8c5b9edf140d6bde653e1458` | MIT (`LICENCE.txt`) | `SimpleIni.h`, `ConvertUTF.h/.c` |
| `stb/` | https://github.com/nothings/stb | `af1a5bc352164740c1cc1354942b1c6b72eacb8a` | MIT / Unlicense | `stb_image.h` |
| `WinReg/` | https://github.com/GiovanniDicanio/WinReg | `a4907f31deaca15ca27cc41e5506f54e9f05d3a4` | MIT | `WinReg/WinReg.hpp` |
| `detours/` | Microsoft Detours (prebuilt `detours-x64.lib` + headers, as shipped in-tree upstream) | n/a | MIT | unchanged |
| `imgui-notify-v2/` | patrickcjk/imgui-notify (in-tree upstream) + Font Awesome 5 Free (`fa_solid_900.h`: SIL OFL 1.1 font, icons CC BY 4.0) | n/a | MIT / OFL / CC BY 4.0 | unchanged |

Fonts still embedded from `mod/cheat-library/res/`: Ruda-Bold / Ruda-ExtraBold (SIL OFL 1.1).
Game-derived data in `mod/cheat-library/res/` (icons, map JSON, `signatures.json`, the generated `src/appdata/*.h`)
and in `ref/` is exactly what HoYoverse's takedown notices targeted — this repository is private; do not publish it.

## `ref/akebi-gc-2.8/` — last upstream state on game 2.8 (source only, Apache-2.0)

`A-Archives-and-Forks/Akebi-GC` @ `1b9791470720fc4415d31268e822bafe355ec8b9` (2022-08-16, "Added Crash Report blocker"; first
parent of the 3.0 merge `6efec31bd`). Kept: `cheat-library/src/{appdata,framework,user}`, `cheat-base/src`, the two vcxproj,
`res/assembly_checksum.json` (game 2.8 OS: UnityPlayer `4999961552328781053`, UserAssembly `807890720029543258` — verified identical
on the client Relic installs), `res/signatures.json`, README, LICENSE, `.gitmodules`. Older tag with the same target: `gmh5225/Genshin-Akebi-GC@v0.8`.

## Gitignored working material (`*_ref/`)
- `heavy_ref/res/` — the assets listed above.
- `dumps_ref/` — `Taiga74164-Akebi-GC_-_2022-09-30_02-58-16.bundle` (archive.org, full Akebi git history up to game 3.0,
  https://archive.org/details/github.com-Taiga74164-Akebi-GC_-_2022-09-30_02-58-16), IL2CPP dumps, extracted client files.

## Changes made by Relic (running log — Apache §4(b))
- 2026-08-23 import: exclusions above; `mod/.gitignore` minus three lines. No source change yet.
- 2026-08-23 phase 1 (build): `akebi-gc.sln` → `relic-ee.sln` (injector project and the x86 solution configs removed,
  scanner config renamed `Release_WS`); `cheat-library.vcxproj` / `cheat-base.vcxproj`: toolset v145, `GameVersion`
  property (16|28|33) → `src/appdata-$(GameVersion)` include dir, `RELIC_GAME_VERSION` define (also passed to rc.exe),
  per-version `bin$(GameVersion)\…` output, post-build injector launch and injector `ProjectReference` removed,
  Release_WS on C++20 with the missing `cheat-library\src` include dir and the sniffer files no longer excluded, static
  CRT (`/MT`) + `/Brepro` in Release configs; `src/appdata/` (dual OS/CN offsets) → `src/appdata-33/` single-offset
  (`tools/convert_dual_to_single.py`); framework `il2cpp-appdata.h` / `il2cpp-init.{h,cpp}`: single-offset macros,
  `LGameVersion {NONE, GLOBAL}`, offset 0x0 = null pointer, `init_il2cpp()` returns false on a checksum mismatch instead
  of the console prompt (`UserSelectGameVersion` deleted), `main.cpp` stops when it returns false; `HookManager::install`
  skips null targets; C++20 conformance fixes (`config/internal/FieldSerialize.h` EqualExists via requires-expression,
  `Toggle::operator==` and `AccountConfig::operator==` const). `src/appdata-28/` generated by `tools/make_appdata_28.py`.
- 2026-08-23 phase 2 (Relic defaults): `ProtectionBypass` and `RSAPatch` deleted (cheat.cpp, vcxproj); `About` reduced
  to a credits page (no scam-warning dialog, no watermark; `CheatManagerBase::DrawWarning` removed); `Settings.cpp`
  `f_ConsoleLogging` default false; `res.rc`: no `iconsHD`, no MiSans/ubuntu fonts (`cheat.cpp` loads the default font
  only; `ImageLoader::GetImage` falls back from `HD<name>` to `<name>`), no `warnings/` images, `ASSEMBLYCHECKSUMS`
  picks `assembly_checksum-<ver>.json` by `RELIC_GAME_VERSION` (`tools/checksum.py` writes them).
- 2026-08-23 phase 4 (2.8 offsets): `src/appdata-28/` filled from the 2.8 IL2CPP dump (`tools/dump_runner.py` → R0xdeadc0de
  Il2CppDumper-Genshin on the unpatched 2.8 metadata; `tools/dumpmodel.py` + `tools/transplant.py` resolve the 3.3-only
  symbols by name / structural class identification / signature; `tools/gen_types.py` emits `il2cpp-types-relic.h` with the
  Proto_*/UI types 2.8's header lacked, included from the end of `il2cpp-types.h`; `tools/apply_overrides.py` +
  `offsets-28.overrides.json` = the hand-picked halves of ambiguous same-signature pairs, marked UNVERIFIED). 5 symbols stay
  0x0 on 2.8 (Wanderer stamina hook, LuaShell notify hook, the 3.3 team-countdown coroutine that does not exist in 2.8, two
  enhance-animation functions) — hooks on them are skipped, the one direct call is null-guarded (`SkipEnhanceAnimation.cpp`);
  `game/util.cpp` + `misc/Debug.cpp` cast `ConfigScenePoint::klass` to the generated `__Class` (2.8 declares it `void*`).
- 2026-08-23 exit-crash fix (`tools/relic_exit_fix.py`): the global events (`events::KeyUpEvent/WndProcEvent/RenderEvent`,
  `cheat::events::GameUpdateEvent/AccountChangedEvent/MoveSyncEvent`, `config::ProfileChanged`, the DX11/DX12 backend events) are
  heap-allocated references that are never destructed — at process exit the CRT destroyed them before the `Hotkey` fields of the
  feature singletons and `~Hotkey` hit a pure virtual `removeHandler` (WER BEX64 c0000409/7 in CLibrary.dll on every game exit);
  `dllmain.cpp` additionally arms purecall/terminate/invalid-parameter handlers on DLL_PROCESS_DETACH (process termination) that
  end the process via `ntdll!NtTerminateProcess` instead of `abort()`.
- 2026-08-23 phase 5 (1.6 offsets): `src/appdata-16/` generated from the 1.6 IL2CPP dump by the structural 2.8→1.6 matcher
  (`tools/codexref.py` PE/xref scan, `tools/matcher16.py`, `tools/gen_appdata16.py`; 262/281 functions, 24/24 MethodInfo/TypeInfo,
  773 types regenerated, prelude swapped for the 1.6 `Il2CppClass` layout); feature code gated for 1.6 by `tools/relic_gate_16.py`
  (AutoFish/FishPool `>= 21`; `_layerMaskScene`, `msgType_`, two Debug fields `>= 28`; null guards for `Miscs_SetUILocalAvatarVisible`
  and `get_miniMapScale`). Fishing and the 3.3-only coroutine stay unresolved on 1.6 by design.
- 2026-08-23 phase 5b (1.6 static-method ABI, the first 1.6 crash): game 1.6 compiles every STATIC method with a
  leading dummy `this` (always null) — verified in the game's own call sites, e.g. `ActorUtils::GetAvatarPos` is called
  as (return buffer, null, MethodInfo) on 1.6 but (return buffer, MethodInfo) on 2.8. With the 3.3 signatures every
  argument landed one register too far left; `Singleton<T>::get_Instance` read `method->klass` out of rdx and the game
  died on the first singleton lookup (0xC0000005 writing klass+0x105, right at the first F1-menu frame).
  `tools/verify_abi.py` (call-site evidence, 2.8 as control) found it, `tools/gen_static_thunks.py` fixes it: the 66
  static declarations in `appdata-16` became `<name>__RAW` with the extra parameter plus 63 generated inline wrappers
  (`appdata-16/il2cpp-static-thunks.h`, included from `framework/il2cpp-appdata.h`) so no call site changed; the 3
  hooked statics (`Miscs_CheckTargetAttackable`, `Kcp_KcpNative_kcp_client_send_packet`, `Lua_xlua_pushasciistring`)
  carry it through the `RELIC_STATIC_THIS` / `RELIC_STATIC_THIS_ARG` macros. Noted for later: the generated 1.6/2.8
  `Il2CppClass` prelude puts `static_fields` 8 bytes early (the game uses 0xA8 / 0xC0), but `vtable` — the only field
  the mod actually reads — is correct in both (0x110 / 0x130, checked against the games' virtual-call sites).
- 2026-08-23 crash containment (`cheat-base/relic-guard.h` + `tools/relic_add_guards.py`): a heuristic port faults where
  an offset or a struct field is wrong, so a fault must not be fatal. `relic::Guard` runs a piece of work under SEH,
  logs the first fault of that call site and carries on. Applied per feature in `CheatManagerBase::DrawExternal`, per
  handler in the event dispatch (`events/event.hpp` — one broken feature no longer aborts GameUpdate for every frame,
  which is what made the mod look dead after the first crash) and around the map-click teleport path. First 1.6 field
  fault found this way: `MapTeleport::ScreenToMapPosition` handed Unity a map object whose native side was gone
  (`RectTransformUtility::ScreenPointToLocalPointInRectangle` read null+0x24).
- 2026-08-23 1.6 singleton timing: on metadata v24 a singleton's MethodInfo slot is only filled once the GAME has
  called that getter, so `GET_SINGLETON` legitimately answers null during the first frames (3.3 fills them up front,
  which is why upstream calls it unchecked). Null checks added where the result was used directly (FPSUnlock,
  MapTeleport, Debug — `tools/relic_singleton_null_checks.py`), and the guard now counts CONSECUTIVE faults with a
  limit of 20, so a transient early fault no longer switches a working feature off for the session (that is what made
  FPS Unlock look permanently broken on the first 1.6 run).
- 2026-08-23 overlay hook (the 1.6 "menu never appears" / "menu flickers"): the D3D11 overlay is a code patch in
  dxgi.dll's `Present`, and on this game something restores the original bytes within a frame of it being applied.
  Upstream never noticed because deleting the mod and putting it back changes WHEN the patch goes in — which is
  exactly the "sometimes you have to reinstall Akebi" folklore. Diagnosed with `cheat-base/relic-diag.h/.cpp`, a
  counter board written by a thread of our own into `relic-diag.txt` next to the DLL: it needs neither the logger
  (whose mutex a swallowed fault can hold) nor a live render thread, and it showed `present=1` for a whole session
  while the rest of the mod ran 395k handler calls. Fixed by moving the overlay off dxgi's code entirely
  (`render/backend/dx11-hook.cpp`): on the first Present the swap chain object is handed a private copy of its own
  vtable with our function in slot 8, so nothing in dxgi.dll is modified and there is nothing left to restore.
  `cheat-base/relic-hookwatch.h/.cpp` (remember the installed bytes, put them back when they change) was the step
  before that; it now only covers the bootstrap patch and switches itself off once the vtable hook is in, because
  fighting over the bytes cost more frames than it won. `Present1` is hooked as well for flip-model swap chains,
  with a depth guard so one frame cannot draw twice. Also: after a guarded fault the ImGui frame is recovered
  (`ErrorCheckEndFrameRecover`) — an unwind that never happened can leave a window or a style var pushed, and the
  leftovers accumulate until nothing draws at all.
- 2026-08-23 hooks are guarded (the 1.6 map-teleport crash): a hook is called by the GAME, so it bypasses every
  guard around features, events and rendering — and 83 of the mod's 84 hooks had none. `HookManager::install` now
  has `INSTALL_HOOK` (`relic::HookGuard`, `tools/relic_guard_hooks.py` rewrote all 84 call sites): the body runs
  under SEH, a faulting call is dropped rather than re-run through the origin, and a hook that keeps faulting turns
  into a plain pass-through so the feature behaves as if switched off. The null-target warning names the hook now.
- 2026-08-23 field offsets are checked at compile time (`tools/gen_field_asserts.py` →
  `appdata-<ver>/il2cpp-types-asserts.h`, included from one TU): every generated member is pinned to the offset the
  metadata dump reports for it. Nothing had ever checked that the C++ struct agrees with the game, and it often did
  not: the generator writes a class's inherited layout as a leading `struct Base__Fields _;`, but a base with no
  instance fields is an EMPTY struct — one byte in C++, rounded up to eight by `__declspec(align(8))` — so every
  field of the derived class sat 8 bytes late. Emptiness is transitive (a base whose only member is an empty base),
  which `tools/fix_empty_base_fields.py` closes to a fixed point before removing the members. That is what killed
  the 1.6 client on map teleport: `MapModule::_scenePointDics` was read from 0x18 instead of 0x10, i.e. out of the
  middle of the `_bigworldPos` Vector3 — the faulting address in the dump, 0x436A67A84506A8E1, decodes to the two
  floats 2154.6 / 234.4. The remaining mismatches are listed per version in `field-offsets.known-bad.txt`
  (`tools/collect_known_bad.ps1` collects them; the compiler stops after 100 errors, so it takes several passes):
  538 fields on 1.6 (mostly `VCHumanoidMove`, `VCAnimatorMove`, `LCBaseIntee`, `AttackResult` — the movement and
  combat tier), 62 on 2.8 (all `Proto_*`, the packet types ESP reads). The list may only shrink: anything new is a
  build error.
- 2026-08-23 the 1.6 Dictionary (map teleport "no unlocked waypoint" on every click): game 1.6 is Unity 2017.4 with
  the LEGACY Mono corlib (.NET 3.5 profile), and its `System.Collections.Generic.Dictionary<K,V>` is the old Mono
  implementation - `table / linkSlots / keySlots / valueSlots / touchedSlots / count@0x38` - not the referencesource
  one (`buckets / entries / count@0x20`) that 2.8+ (Unity 2019.4, .NET 4.x corlib) ships and that `UniDict` in
  `framework/helpers.h` mirrored. Verified on the 1.6 binary (`get_Count` = `mov eax,[rcx+0x38]`; the game's own
  `Enumerator.MoveNext` walks 8-byte `{HashCode, Next}` link slots up to `touchedSlots` and takes the ones with the
  sign bit set). With the 2.8 layout every `pairs()` on 1.6 was empty, so `GetUnlockedWaypoints` had nothing to
  pick from (no fault, no log - the probe/asserts only audit appdata, not the BCL mirrors). `UniDict` is now
  per-version (`#if RELIC_GAME_VERSION <= 16`) with `static_assert`s on both layouts and an `#error` when the macro
  is missing. Same change fixes `GetWaypointPosition` (mark clicks), Debug > DrawWaypoints and AutoTreeFarm.
- 2026-08-23 struct layouts measured by the compiler, not a size table (`tools/probe_offsets.py`,
  `tools/fix_layout_pads.py`): the 563 mismatched members of appdata-16 had three root causes - (A) the generator
  sized every by-value `Nullable<T>` / FALLBACK-copied struct as 0 bytes and padded the whole gap after it
  (547 members: `AttackResult` +8/+64, `BaseComponent` +16 and through it every `LC*`/`VC*` component -
  `LCBaseCombat._combatProperty` was read at +0x138 = the `onHPChanged` delegate, which is why One-Punch computed
  with garbage), (B) a class `__Fields` struct was sized without the `__declspec(align(8))` rounding (6 members,
  `AttackLanded`/`EvtCrash`), (C) `Color32` emitted as 5 sequential members for an explicit-layout 4-byte struct
  (10 members). `fix_layout_pads.py` recomputes every `_padXX` filler from compiler-measured sizes to a fixed point
  (28 deletions on 1.6, 7 on 2.8 - all 62 entries of the 2.8 known-bad list were class (B) in the `Proto_*`
  packet structs ESP/AnimationChanger/AutoLoot read); `Color32` is now `__declspec(align(4))` `r,g,b,a` +
  `rgba()`. Both versions: **0 mismatches**, `field-offsets.known-bad.txt` empty, every member asserted
  (`gen_field_asserts.py` skips CRT macro names such as `_stat`). The generator carries the root fixes too
  (`gen_types.struct_size` measures an emitted struct from its own text and rounds class sizes to 8;
  `gen_appdata16.gen_nullable` records the Nullable size) - not re-run; the headers were repaired in place.
- 2026-08-23 one-session 1.6 diagnostics (`tools/relic_diag16.py`, `--off` to strip): `[wp16]` lines in
  `game/util.cpp` (dictionary read / per-waypoint filter verdict / result count) and `[rf16]` lines in
  `player/RapidFire.cpp` (hook entered / target name + filter verdict / fire count), all under
  `#if RELIC_GAME_VERSION <= 16`. Note for the RapidFire test: the saved 1.6 profile had Multi-Hit + Randomize with
  the defaults min 1 / max 3, i.e. `rand() % 2 + 1` = 1 or 2 hits - at best one extra damage number on half the
  hits; One-Punch was never switched on in that profile, and `SpeedMultiplier` was 5.0 with the Attack-speed
  toggle off. Test with Randomize OFF and Multiplier 5 for an unambiguous result.
- 2026-08-23 ESP no longer talks to a dead camera: a Unity object whose native side is gone keeps a live managed
  wrapper, and calling into it raises a MANAGED exception instead of returning null. `ESPRender`'s `s_Camera` is
  refreshed once a second, so every frame between a scene change and the next refresh threw inside
  `Camera.WorldToScreenPoint` and was logged by the feature's own `SAFE_*` handler - ~100 lines per loading screen
  (`ESPRender.cpp:405 Exception 0xe06d7363`), for work that was wasted anyway. Every read of the camera now goes
  through `IsCameraAlive()` (`fields._._._.m_CachedPtr`, the same idiom HideUI/PaimonFollow already use). Version
  independent: the Camera -> Behaviour -> Component -> Object chain is identical in appdata-16 and appdata-28.
- 2026-08-23 log timestamps: the logger stamped lines in UTC and built the file name from `tm_mon` unincremented,
  so an August session was written as `log_<year>-07-<day>` three hours off. Now local time with a 1-based month.
- 2026-08-23 ShowSkillCD on 1.6 ("Ready 0/0" and four logged access violations): upstream feeds its cooldown map from
  two `LCAvatarCombat` hooks, and 1.6 has no usable counterpart for either. `CheckCDTimer` had been matched to
  `DIIAFDKMCEE$$JJJBFMBJHPG` @0x01673E90 - the melee/ranged targeting-bucket rebuild, whose parameter object ends at
  0x1C, so the hook read `skillIndex` at +0x108 and a 16-byte `SafeFloat` at +0x20 OUT OF OBJECT on every call; the
  real counterpart (`LBBIBLMOFFL` @0x016749A0, same two callers and body shape as 2.8's) takes the skill id as an
  extra LEADING parameter that 2.8 reads out of the SkillInfo, so it cannot carry the upstream hook at all. Its
  declaration is corrected anyway - with the extra parameter - so re-installing it on 1.6 now fails to COMPILE
  instead of hooking a differently-shaped method. Both hooks are `#if RELIC_GAME_VERSION > 16` and 1.6 reads the
  live value instead: `ResolveElementalSkill` asks `LCAvatarCombat._currSkills[1]` through
  `DIIAFDKMCEE$$KCNMJMDPCEI(uint)` @0x016745B0 - the bounds-checked accessor the game's own team button uses
  (`MonoTeamBtn$$OIAFAGIHDNN` calls it as `(this, index, null)`). An empty slot means "nothing to draw yet", NOT
  "drop the button" - the slot is empty for a few frames after the button appears.
  Separately and UNGATED (a 2.8/3.3 fix too): `UpdateSkillMap` dereferenced `_skillDepotConfig` unchecked, and BOTH
  versions assign that field only AFTER the per-skill setup calls that reach the hook (1.6 `GEICCKNPBFM` writes it
  at 0x016723A8, 2.8 `MABCBAGMELP` at 0x026F648C) - i.e. 2.8 has been eating the same guarded faults silently.
- 2026-08-23 "Make Character invisible" on 1.6: `MoleMole.Miscs.SetUILocalAvatarVisible` was added after 1.6 (the
  whole 1.6 `Miscs` class is unobfuscated and has no such member), so the offset was 0x0 and the toggle did nothing.
  2.8's method is only `localAvatar.GetRendererComponent().SetRendererVisible(visible, 14, true)`, and 1.6 ships
  both halves as ordinary INSTANCE methods (`DNPPAIELOMJ$$MKIHLPLFIBI` @0x00CF5100, `HFCOBKMKNLN$$JLBIFIBAALA`
  @0x030E9970) - the game itself calls them back to back in `InteractionManager$$ResumeAvatarVisibleSet`, which also
  shows MethodInfo may be null. The `reason` is a bit index in a bit stack that starts all-ones (visible = every bit
  set); 14 is the slot 2.8 reserves for this and no 1.6 code path passes it, so nothing else can clear or restore it
  behind us. The 1.6 branch remembers the hidden avatar by RUNTIME ID, never by pointer: the game recycles entity
  objects (that is what `EntityManager`'s destroy hook is for), and a stale pointer would fault inside
  `FreeCamera::OnGameUpdate` - after 20 such faults the guard would switch the whole FreeCamera handler off, free
  camera included. Usage note unchanged from upstream: the option applies while FreeCamera's own `Enable` is on.
  New symbols are mirrored into `appdata-28` (unused there) because `gen_appdata16.py` generates appdata-16 FROM
  appdata-28 line by line and would otherwise drop a 1.6-only declaration on the next regeneration.
- 2026-08-23 CustomWeather: `EnviroSky.ChangeWeather` returns whether the client actually HAS that weather asset and
  the answer was thrown away, so a weather this build does not ship failed invisibly and was retried ten times a
  second. Logged once per change (`[weather] ChangeWeather('<path>') -> 0|1`) - one session now says exactly which of
  the eight paths 1.6 ships. The lightning branch itself is untouched: 1.6 DOES have the lightning gadget, and the
  claim that its `RainHeavy` asset is 2.x-only turned out to be unproven, so the gate stays until the log settles it.
- 2026-08-23 the 1.6 free camera no longer eats the grass: enabling it made every blade of terrain grass (and
  anything else bound to the camera GameObject) vanish for the whole session, and the user reported it as a
  regression - it is not, it is upstream Akebi's design finally running. Detail grass is a native subsystem
  (`UnityEngine.MiHoYoVegetationManager`, dump.cs:395514) whose entire managed lifecycle is
  `MoleMole.MonoMiHoYoVegetationManager` (dump.cs:1704006), a singleton MonoBehaviour that rides on
  `/EntityRoot/MainCamera(Clone)` and drives the system from its own activation edges: `OnEnable` @0x0271F350
  fetches the camera from the global camera accessor @0x026E4DC0 and calls `Intial(ref camera)` @0x05F91CA0,
  `OnDisable` @0x0271F230 calls `Shutdown` @0x05F91EE0 and nulls the field. `Shutdown` is a PROCESS-WIDE
  switch, not a per-camera one - UnityPlayer's `Internal_Intial` @0x00F97D10 only takes the camera's instance
  id and the callee (`LocalWind_Init` @0xF8DE10) never reads it. `FreeCamera` therefore killed the grass
  twice per enable: `Object.Instantiate` hands the clone a COPY of the singleton, whose `Awake` @0x0271F100
  (`if (instance != null) Destroy(this)`) destroys the duplicate and the dying copy's `OnDisable` calls
  `Shutdown`; and `GameObject.set_active(mainCam, false)` runs `OnDisable` on the real one as well. Nothing
  calls `Intial` again until the real camera is reactivated - which is exactly why the grass came back the
  instant the toggle went off. Fixed by removing the clone on 1.6 entirely (`#if RELIC_GAME_VERSION <= 16`,
  upstream path kept verbatim under `#else` so the live-tested 2.8 build does not change shape): the free
  camera now drives the GAME's camera. The pose is applied by `miHoYoCamera.CameraStateMgr.FlushStateData`
  @0x067F2160 (dump.cs:655845) - it runs the state position through `WorldShiftManager.GetRelativePosition`
  @0x042DA330 and calls `Transform.set_position` @0x0537C810, then the rotation - and it is the last writer of
  the frame, because `Flush` @0x067F2640 runs `FlushPostProcesserInternal` BEFORE it and `PostFlushTop`
  @0x067F2AB0 after it writes no transform. So the game computes and applies its own camera as usual and a
  POSTFIX hook overwrites the result in that same call (postfix also means a fault in our code cannot cost the
  game its camera update - `HookGuard` never re-runs the origin). The same pose is written from `OnGameUpdate`
  as well, so the feature still moves the view if `FlushStateData` ever turns out not to be the live path in
  some scene, and a one-shot `[freecam]` (then `[freecam16]`) log line says which of the two paths actually ran. Nothing is
  cloned, deactivated or destroyed any more, so no per-camera or per-GameObject system can notice the free
  camera. One new symbol, `miHoYoCamera_CameraStateMgr_FlushStateData` (1.6 0x067F2160), mirrored into
  `appdata-28` (0x041A33F0, unused there) because `gen_appdata16.py` generates appdata-16 FROM appdata-28;
  the 2.8 DLL was rebuilt for that declaration alone so source and shipped binary stay reproducible.
  The camera owner, for later work: the level camera plugin `OHLKPGALLOI` (dump.cs:845489) holds `Camera`
  @0x118, `CinemachineBrain` @0x120, `CameraStateMgr` @0x128 and a `CameraStateData` @0x940. CONFIRMED in game on 1.6
  (2026-08-23 23:10, `[freecam16] CameraStateMgr.FlushStateData reached` in the session log): the grass stays and
  the camera flies. 2.8 was tested too and does NOT lose its grass under the upstream clone path, so 2.8 stays on
  it - whatever binds the vegetation manager to the camera GameObject on 1.6 is not the arrangement 2.8 ships.
- 2026-08-24 CustomWeather lightning on 1.6: the toggle could never have done anything. The user's session log
  settled the first half - `[weather] ChangeWeather('Data/Environment/Weather/BigWorld/Weather_Dq_Tabeisha_Rain_Heavy')
  -> 0` on 1.6 (seven times) against `-> 1` on 2.8; `Dq` is 稻妻/Daoqi = Inazuma, which shipped in game 2.0, so the
  asset simply is not in a 1.6 client (`Weather_ClearSky`, `_Cloudy`, `_Foggy`, `_Storm` all answer 1). The lightning
  branch is gated on the mod's dropdown being `RainHeavy`, i.e. on the one selection that cannot apply - a guaranteed
  no-op. The enum was never the bug: `EntityType::Lightning` is 0x27 in appdata-16 and appdata-28 alike. What 1.6 DOES
  have is the whole chain: `EnviroWeatherPreset.hasLightning` (dump.cs:1942700) and `thunderSettings`,
  `EnviroSky.ComputeLightningBornPos` / `FireStormEffect` (dump.cs:1941884, :1941892), and the gadget itself - server
  excel `GadgetData_Level.txt` row 70000009 `Storm_Lightning`, entityType 39, described 雷雨天的闪电. The weather-kind
  enum is byte-identical on both versions (`ClearSky, Cloudy, Foggy, Rain, Snow, Storm, Skill`). So the feature is
  reachable on 1.6; what is NOT known is which 1.6 weather has `hasLightning` set, and that cannot be answered
  statically - the presets live inside the mhy-encrypted `blk` bundles and no weather name except `Weather_ClearSky`
  appears anywhere in the binary. Changes: the lightning branch is UNGATED on 1.6 (`#if RELIC_GAME_VERSION <= 16`) -
  it only MOVES `EntityType::Lightning` entities the weather already spawned, so with none present it does nothing,
  and whichever 1.6 weather produces them now works without guessing its asset name; 2.8 keeps `== RainHeavy`
  verbatim. Plus a one-session probe behind Debug > Weather assets: it walks a candidate list one entry at a time
  (~1.5 s each so the weather actually applies), logs `[weather] probe '<path>' -> 0|1`, then reads the applied
  `EnviroWeatherPreset` by raw offset and logs its path, kind and **hasLightning** - `EnviroSky.Weather` 1.6 +0x98 /
  2.8 +0xA8, its preset slots +0x10/+0x18 on both, `type` +0x18 and `path` +0x30 on both, `hasLightning` 1.6 +0xF1A /
  2.8 +0x13D1 - every read under `relic::Try` so a wrong offset costs a silent skip instead of counting towards the
  feature's guard. It also logs `EnviroSky.GetCurWeatherList` (new symbol, 1.6 0x0284C390 / 2.8 0x0277DA90, mirrored
  into appdata-28 as always), which answers what the CURRENT AREA offers in short form (`BigWorld/Weather_X`, prepend
  `Data/Environment/Weather/`) - a hint, not an inventory, since it reads only the active EnviroProfile. Restores the
  player's weather when it finishes. Also, all version-neutral: the `weather` map lookup went from `at()` to `find()`
  plus a clamp in the ctor (cfg.json stores the enum as a raw integer, and a throw inside OnGameUpdate is a guard
  fault - 20 of them switch the feature off for the session); the lightning-entity query was hoisted out of the
  per-monster loop (it was a full entity walk per monster at 10 Hz) and now logs `[weather] lightning entities: N`
  when the count changes, which is what separates "this weather makes no lightning" from "the targeting is broken";
  and a missing asset is now said out loud in the UI instead of being retried ten times a second in silence.
  UNKNOWN until a session runs the probe: whether any 1.6 weather sets `hasLightning`, whether `Weather_Storm` is the
  thunderstorm or the Stormterror wind storm, and whether the GIO 1.6 server acts on the client's create-gadget
  notify for 70000009 (the entity is server-spawned: 1.6 `EPADPPNFBIL` @0x18D4870 -> `MEKBPGAAKJE` @0x14B7880).
- 2026-08-24 CustomWeather lightning on 1.6, part two - the probe answered and the approach changed. The user ran
  Debug > Weather assets on 1.6: `Weather_Storm` applies and its preset reports `kind=5 hasLightning=1` - the ONLY
  1.6 weather that carries lightning. Every other one reports 0, including `Weather_Rain_Heavy`, which does exist on
  1.6 (`kind=3`) unlike the Inazuma `Weather_Dq_Tabeisha_Rain_Heavy` the map pointed at; `GetCurWeatherList` also
  named two paths nobody knew, `Weather_Rain_Light` and `Weather_Snow_Heavy` (still unprobed). But with Storm applied
  and the toggle on, `[weather] lightning entities: 0` in two separate sessions and the count never moved: the
  upstream approach only MOVES `EntityType::Lightning` entities, the bolt is the server-spawned gadget 70000009, and
  GIO never spawns it. Note there is no evidence the entity route ever worked on 2.8 either - the 2.8 logs predate
  the counter. Also ruled out, by disassembly rather than inference: the mod's 10 Hz `ChangeWeather` is NOT
  suppressing ambient lightning - `ChangeWeather` @0x028433F0 compares the requested preset against the applied one
  (`Object.op_Equality` @0x02843655) and returns true without touching the thunder accumulator, so re-applying the
  same weather is a pure no-op. So on 1.6 the feature now DRAWS the bolt itself with
  `MoleMole.EnviroSky.FireStormEffect` (1.6 0x028496A0, mirrored into appdata-28 at 0x0277C150). Read from its whole
  body: `born = pos + Vector3.up * height` (get_up @0x05382E40, op_Multiply @0x053831B0, op_Addition @0x05382F20)
  then `EENGMANLKGL` @0x02847350 spawns the preset's lightning / cloud / impact prefabs off `EnviroSky.levelEntity` -
  cloud at born, impact at pos, bolt between. So the float is a HEIGHT in world units, not a strength or a radius,
  and the call needs no server, no gadget and no entity. Two consequences documented in the tooltip and the code:
  the entity count stays 0 even on complete success, and the bolt is VISUAL - in vanilla the damage comes from
  `ElementAbility_Storm_Lightning` on the gadget, not from this call. The position is ABSOLUTE (world-shift
  converted), not relative: FireStormEffect performs no conversion of its own, and the game's own driver
  `DEPJHAPCIBL` @0x02844A10 raycasts in Unity space then converts BOTH endpoints with
  `WorldShiftManager.GetAbsolutePosition` (@0x0284502D, @0x02845068) immediately before calling the same spawner;
  a second caller @0x00C1C960 does the same. The abs and rel values are both logged on the first five strikes, so
  one session settles it permanently if the bolt ever lands a world-shift away. Preconditions, all read off the
  disassembly: `EnviroSky.Weather` +0x98 and its applied preset +0x18 are dereferenced UNCHECKED (a null there is a
  managed NullReferenceException, so the mod checks them itself and refuses); `hasLightning` +0xF1A,
  `thunderSettings` +0xF20, `levelEntity` +0x478 and a Unity-alive `CameraTrans` +0x48 only make it a silent no-op.
  `[weather] storm readiness: ...` names which one closed, and the bolt height defaults to the preset's own
  `lightningBeginHeight` (thunderSettings +0x38, an inline Config struct at +0x18 whose +0x20 is the field; the
  game's driver confirms it: `addss [rbx+0x38]`, clamp to `[rbx+0x3C]`). Strikes are rate limited (one per
  `f_LightningDelay`, default 600 ms, round-robin over the monsters within 30 m) because each call restarts the
  full-screen flash; height and delay are sliders since no shipped magnitude exists in the binary. The upstream
  entity-moving loop is kept and now runs only when there is something to move. 1.6's `RainHeavy` map entry was
  repointed to `Weather_Rain_Heavy` in the same pass - one menu entry that was a guaranteed no-op. All of it is
  `#if RELIC_GAME_VERSION <= 16`; 2.8 keeps the upstream branch verbatim and only gains the two declarations, and
  was rebuilt so source and shipped binary stay reproducible. UNKNOWN until a session: whether 1.6's Storm preset
  actually carries a non-null `EnviroThunderSettings` with non-empty prefab strings (the `thunder=` field answers
  it), whether ambient 1.6 lightning works at all (never observed - the counter was blind to it), and whether the
  pooled event the spawner fires has listeners that generate energy (treat "purely cosmetic" as unverified).
- 2026-08-24 lightning that hurts (1.6): the user confirmed in game that the FireStormEffect bolts land on the
  enemies, and asked for damage. `FireStormEffect` cannot provide it - it only spawns VFX prefabs - so the strike now
  optionally raises the game's own FALL-CRASH event on the same target in the same tick, off by default. The sequence
  is the one the game's own velocity detector `DNBGMBAPINL$$CNJGADBJJME` @0x01970630 runs (second producer
  `HBFKKCMPNOG$$OJDGFACDKIL` @0x036AA7E0): `EventHelper.Allocate<EvtCrash>` @0x0303C370 with the MethodInfo slot
  0x08FE8850 (`Method$EPLPMHCDMGN.HEPNAMDNOHJ<MFKLHADCFAO>()`, so the pooled object is the right TYPE by
  construction) -> `EvtCrash.Init` @0x023EEC30 -> `EventManager.FireEvent` @0x01718600. `EventManager_FireEvent` was
  only an `M-pos (medium)` match with a runner-up at 0.52; it was upgraded by hand: dump.cs:1343008 is the only
  `(BaseEvent, bool)` method on `CADNPCCFBBB : InLevelManager`, its body is a structural twin of the 2.8 one
  @0x016C3F50, and the singleton the mod passes is literally `Singleton<CADNPCCFBBB>.get_Instance()`, so `this` and
  the code cannot disagree. The damage arithmetic is exact rather than upstream KillAura's fudge ladder: the consumer
  `AEOLLPABFCP$$LEACKKAHJNI` @0x00A5E890 computes `Max(0, maxHp * (a1 - a2/(a3 + velChange)))` where a1..a3 come from
  ConstValue id 92, and 1.6's shipped row 92 is `10 | 0.4 | 4.2 | 2 | "1,0.9,1"` (the initializer parses four floats
  then splits element 4 on ',', which pins the row to the code), so the mod inverts it directly.
  What this is NOT, and the tooltip says all of it: `EvtCrash` has three fields (velChange 0x30, maxHp 0x34,
  hitPos 0x38) and no element, no attacker and no `AbilityIdentifier` - so it is a plain white FALL-damage number,
  not Electro; no aura, no reaction, no crit, credited to nobody, and useless against shields or invincible/HP-locked
  enemies. REJECTED for now, deliberately: the faithful route (`AttackResult.CreateElementAttack` @0x021D77D0 ->
  `FireBeingHitEvent` @0x017A14A0) would need mod-obtained attacker combat, an unpaired target id AND a fabricated
  `AttackResult` whose sub-pointers ~40 `EvtBeingHit` consumers dereference deep in game code - three untested things
  at once, and `LCBaseCombat_DoHitEntity` @0x0179FCB0 is only an `M-fp (medium)` match (fp 0.32, runner-up 0.52).
  Known hazard, and the reason for default-off: `immediately=false` makes FireEvent ENQUEUE (tail-jump to
  `KCCCFKBGCEB` @0x01717190), so the handler runs on the next EventManager tick, OUTSIDE the mod's per-handler SEH
  guard - a fault there is a hard crash. Do not "fix" that by passing `immediately=true`: nothing in the game
  re-enters the ability system mid-Update. The pooled event is recycled by the dispatcher (`GNMJCKLPHJA`
  @0x01716D10), so it is fire-and-forget - never touched after the call. Diagnostics: the strike line gained
  `dmg=off|fired|refused hp=<before>`, and a paired probe logs `[weather] crash damage id=U wanted=... hpBefore=...
  hpAfter=... delta=...` 400 ms later (once the queue has drained), which is what separates "fired but no-op"
  from "fired and applied" and tests the ConstValue constants directly. Also noted while reading: DO NOT call
  `MoleMole_FixedBoolStack_get_value` @0x047B6BA0 on 1.6 - its own resolution comment shows it matched
  `SoapMethodAttribute$$get_UseAttribute`, an mscorlib attribute getter, which is why the invincible/HP-lock
  precondition is not checked here. UNKNOWN until a session: whether the crash ability action is even bound on 1.6
  monsters (ability configs live in the encrypted `.blk`; failure mode is a silent no-op, and the `delta=` line
  answers it), and whether GIO keeps the HP change. Two pre-existing bugs found in passing and NOT fixed here:
  `KillAura.cpp:172/:197` dereference `combat` and `_combatProperty_k__BackingField` unchecked after a cross-frame
  dequeue, and `RapidFire.cpp:384` writes `_attackerAttackProperty->fields._elementType` unchecked into a SHARED,
  long-lived `ConfigAttackProperty`, permanently recolouring that ability for the session.
- 2026-08-24 Electro lightning (1.6), and the crash route confirmed. The user's 12:23 session proved the previous
  build end to end: `[weather] crash damage id=... wanted=500.0 hpBefore=15265.4 hpAfter=14765.4 delta=500.0` - the
  ConstValue-92 inverse is exact and GIO KEEPS the HP change. The same log also settled the coordinate space for
  good (`abs=(2443.4,207.8,-1227.7) rel=(-31.5,207.8,7.1)` with the bolts visibly landing, so absolute was right)
  and reported `hasLightning=1 thunder=1 level=1 camera=1` for Weather_Storm.
  The user then asked for the damage to be Electro. Two things in the user's own logs de-risked that decisively:
  `LCBaseCombat.FireBeingHitEvent` @0x017A14A0 carried only an `M-pos (medium)` matcher score, but RapidFire hooks
  it and 1500+ `[rf16] hit` lines show `attacker` == the avatar's runtime id, a real `attackee` and
  `attackResult->fields.damage` = 143.2 - which proves BOTH the function identity and the appdata-16 `AttackResult`
  layout. So Electro is now an opt-in sub-toggle of "Damage enemies" (default off; plain fall damage stays the
  default and the whole thing is `#if RELIC_GAME_VERSION <= 16`).
  Nothing is fabricated: `MoleMole.AttackResult.CreateElementAttack` (1.6 0x021D77D0 = dump.cs:1322727
  `HEBLMKMEBIF$$FMFJMOFFGGO(PNLBEJDMOBA, float)`, mirrored into appdata-28 at 0x02C2D990) is the game's OWN factory
  - pool-allocate -> `Reset()` @0x021D67C0 -> `mov [this+0x118], ebx` -> `movss [this+0x11C], xmm6` - and the game's
  Cryo weather mixin calls it at 0x02C302DC with (Ice, 80.0f), which also pins the 1.6 dummy-`this` ABI (element in
  EDX, durability in XMM2). It is STATIC, so it gets the usual `__RAW` + one hand-added inline thunk.
  Why the element sticks with nothing shared touched: `get_ElementType` @0x021D74E0 reads modifiedAttackProperty
  (+0x110) -> _attackerAttackProperty (+0x108) -> _origElementType (+0x118), and the factory leaves the first two
  null - so the element rides on this one attack and the shared `ConfigAttackProperty` that RapidFire.cpp:384
  corrupts is never written. The one field the factory leaves null that the pipeline needs is
  `defenseCombatProperty` (+0x18): the damage handler `PMBPGLIKLGN$$BLKPKLBJACI` @0x0179B9D0 opens with
  `mov rax,[ar+0x18]; test; je <ret>; cmp qword [rax+0x4C0],0; je <ret>` (a null there is a silent no-hit) and the
  visual combat component `FNNDKCJBJDB$$KNDJNEFFICJ` @0x018DD7C0 reads the same field at 0x018DD84B and THROWS on
  null. Both are answered by filling it, and `attackerCombatProperty`, from the two live entities. All 43 consumers
  of `EvtBeingHit.get_attackResult` @0x023673E0 were audited: every other pointer field the factory leaves null is
  read behind a real guard. Lifetime is exact - `EvtBeingHit.Init` @0x02367510 Retains and the event's recycle
  Releases (-> Reset + `ObjectPoolUtility.Deallocate`) - so the mod never Retains, never Releases and never touches
  the object after the call.
  The fail-safe that makes this shippable: before firing ANYTHING the code reads `_origElementType` and
  `_origElementDurability` back off the object the factory returned and compares them with what it asked for. A
  wrong offset therefore costs one `[weather] electro refused: factory answered ...` line and disables the mode for
  the session, instead of a hit in the dark. A refusal also falls back to the proven crash event, logged as
  `dmg=fired/electro->crash`, so the bolt never silently stops hurting. The `electro fire` line is written BEFORE
  the call because the dispatch is queued: if a consumer takes the client down, the last line names the entity.
  REJECTED: route B (capture and replay a live AttackResult) - the object is pooled AND refcounted, so a stashed
  pointer is Reset and re-issued within frames. `DoHitEntity` @0x0179FCB0 stays unused: it runs the Formula over
  the object and would overwrite `damage`.
  RESIDUAL RISK, stated plainly and the reason for default-off: `attackerHitPattern` (+0x138) is left null and one
  ability mixin, `DILBMMOJMCL::IGPIONLLBFA` @0x0167D920, throws on it. It is gated by an attack-type filter (we
  present type 3 Default) and by `remoteState != IsForwarded` (ours is Local, so that gate is open), and whether any
  1.6 monster carries that mixin lives in the encrypted `.blk` - not statically decidable. Every vanilla local
  EvtBeingHit carries a non-null +0x138 (the game's own fill `OPEAHKNKNFO` @0x021DB6D0 asserts it), so this state
  is genuinely novel. Hardening if a session faults: cache a `ConfigHitPattern*` from RapidFire's existing hook
  when the attacker is the avatar and write it into +0x138 (a config object, long-lived and non-pooled).
  Fallback if GIO rejects the hit instead: `LCBaseCombat::JPJJPOMKPIE(uint, ElementType, float, ref)` @0x017A37A0 ->
  `NDAJMJPGIHI` @0x017A6C50 is the game's own aura-only, synchronous element API (the other branch of the same Cryo
  mixin) - pair it with the existing EvtCrash for HP. Unverified; prove it with a read-only hook first.
  Still not fixed, and now doubly worth doing: `RapidFire.cpp:384` should call `AttackResult.set_ElementType`
  @0x021D9620 (2.8: `CHFCCNINPPO::NEHEDEKLFDE` @0x02C2EC40), which lazily allocates a per-attack override via
  `OPFNKNNHNII` @0x021DB9F0, instead of writing through `_attackerAttackProperty` into shared config.
- 2026-09-07 "Drive the Game Camera": the 1.6 real-camera drive is now an OPTION on 2.8 (`f_DriveGameCamera`,
  default off = upstream's clone, which 2.8 was tested on). Motivation from the owner's 2.8 sessions: HP bars and
  everything else that reads or faces the main camera keep looking at the deactivated real camera under the
  clone and follow the free camera only when the real camera is the one moved. The `#if RELIC_GAME_VERSION <= 16`
  gate around the real path is gone: `CameraStateMgr_FlushStateData_Hook`, `RelicDriveRealCam` and
  `RelicReleaseRealCam` compile on both versions, the hook is installed on both (inert until `relicCamDrive`), the
  2.8 clone block moved verbatim into `RelicRunCloneCam()`, and `OnGameUpdate` dispatches on `RelicUseRealCam()`
  (1.6: always true). Flipping the option while the free camera is on hands the absolute pose + FOV across
  (`relicHandoff*`, consumed by the other path's seed block; `targetPosition` and the rotation globals are shared
  and carry over, so a move still easing keeps easing) and the view does not jump; `RelicStopCamera()` stands
  down whichever path owns the camera (`relicRealCamActive`); `RelicDropDeadCameras()` (m_CachedPtr on mainCam,
  the clone and upstream's cached damageOverlay) runs first thing in `OnGameUpdate`, ahead of its early return,
  and ends a clone session whose original died (a clone can outlive it: Instantiate puts it in the active scene),
  and `DisableFreeCam` resets its state before touching the objects, through liveness-checked locals, so a
  camera the level swap destroyed cannot make the same call fault frame after frame until the twenty-fault guard
  silences the handler. The hook stands down on its own when the driven GameObject is dead or `f_Enabled` goes
  off; a third GameUpdate handler (`OnCameraWatchdog`, own fault budget, like the carry's) tears everything
  down - camera, clone, hook, blocked input - once `OnGameUpdate` has been silent for 120 ticks; and the hook
  logs once if `CameraStateMgr._camera` (+0x50 on both versions) is not the camera we drive. Both paths now lerp
  from the absolute pose they wrote last frame instead of reading the transform back and absorbing the
  difference as an "external move": an `Object.Instantiate`'d clone is not a `WorldShiftManager` agent, so a
  re-base left it 1024 units off in the old basis and the absorption turned that into a permanent jump of the
  view - reachable exactly when the character is carried on the 2.8 default path. Proof
  that the postfix is still the last camera writer on 2.8 came from a new tool, `tools/callscan.py` (capstone
  linear sweep of one method from the shipped `UserAssembly.dll`, calls resolved by name through `dump.cs`,
  `--callers` = a vectorised rel32 xref scan): `Flush` @0x041A3D10 -> `FlushStateInternal` ->
  `FlushPostProcesserInternal` -> `FlushStateData` @0x041A33F0 (`Transform.set_position` @0x057BAB40,
  `set_rotation`, `Camera.set_nearClipPlane`, `set_fieldOfView` - the only writer in the chain) -> `PostFlushTop`
  @0x041A4250 (writes nothing), tail-called from `DENIELOJNDA.LDNPLGOKICG` @0x0188C460 out of the scheduler task
  `IGFMLMOGDGA.Execute` @0x01C94630 - the same shape as 1.6's `OHLKPGALLOI.IIJLFEAMJKH` / `ADFCEEEBALI.Execute`.
  The `[freecam16]` log tag is now `[freecam]`. The 3.3 reference build had been broken since the August work
  (`gen_field_asserts.py` writes `il2cpp-types-asserts.h` for 1.6/2.8 only; `EnviroSky_GetCurWeatherList`,
  `PostProcessLayer_{set,get}_innerResolutionScale` and now `CameraStateMgr_FlushStateData` were never declared
  for 3.3): `appdata-33` received 0x0 stubs for those four (`installGuarded` skips a null target and the weather
  listing null-checks its pointer, so the features are inert there) and an empty asserts header, and
  `build_ee.ps1 -Versions 33` compiles again. NOT yet tested in game on 2.8.
- 2026-09-09 **Render Distance** (`visuals/RenderDistance.{h,cpp}`, registered in `cheat.cpp` and the vcxproj; a new
  Visuals group, default off, one 1.0–4.0 multiplier + World / Grass / Shadows sub-toggles). Not an Akebi feature.
  Unity's `QualitySettings.set_lodBias` and `Camera.layerCullDistances` are stripped from both shipped builds, so it
  drives the game's own systems, all located in the 1.6 + 2.8 dumps and confirmed with `tools/callscan.py` /
  `codexref` xrefs on the shipped binaries: (a) the live `SECTR_StreamingProfile` — the ratios the "Environment
  Detail" preset scales; its six `Get*Ratio()` getters read the field × preset and each has exactly one run-time
  caller in the streamer / LOD evaluator — poked by offset (`0x2C..0x50`, identical on both versions; `0x60`
  `grassLayerLoadRatio` on 2.8 only) through the obfuscated holder's static getter (1.6 `JBPNDKLFIFM.CKNBJAPIHJE`
  `0x0362BBA0`, 2.8 `KIFKAIFDIIC.CHMOJAOKJAD` `0x0362AF90`); (b) `EnviroSky.mihoyoGrassConfig` (1.6 `+0x4C8`, 2.8
  `+0x668`) → `MiHoYoGrassGlobalConfigurator` view range + LOD0..3 distance sets (the icalls the game's quality
  applier and its `MonoInLevelDebugGrassViewRangeDialog` use); (c) a hook on the static
  `QualitySettings.set_shadowDistance` (1.6 `0x052CAAC0`, 2.8 `0x058BBB20`; no getter survives). A 3 Hz game-thread
  watchdog captures each lever's baseline, detects the game re-applying its own values against the read-back of our
  last write, and restores on switch-off; each lever runs its own 5-strike SEH guard (the event guard's counter
  resets on every successful throttled tick, so it could never trip). New appdata entries in all three headers
  (`0x0` on 3.3), the 1.6 getter as `__RAW` + thunk, the hooked static keeping its name with the dummy `this`,
  evidence in `tools/offsets-16.overrides.json`. `gen_static_thunks.py` / `unresolved_hooks.py` now also recognise
  `INSTALL_HOOK(app::X, …)` (they only knew the raw `HookManager::install`, so no guarded hook was seen).
  NOT yet tested in game on either version.
- 2026-09-09 (later) **Render Distance: the grass streaming box.** First 1.6 test: props/terrain widened, grass did
  not — one river bank had grass at a distance where the other had none, and it filled in only when the character
  walked closer. Cause, from the shipped binaries (new `tools/disasm.py` / `callers.py` / `dumpnames.py`): every SECTR
  layer loader recomputes `effective = base × layerLoadRatio × Random.Range(0.9, 1.1)` on each check, EXCEPT two
  loader classes that copy base → effective unchanged (1.6 `HLMOPKELOPF`/`EICJDGOHKHL.CPLMMNMLPIC`, 2.8
  `EHGBBNEKMCE`/`NNKPFAFJFNI.MNPKJCKDJHA`) — the TerrainGrass layer is one of them, so lever A never reached it; the
  base box is copied from `SECTR_LayerConfig.loadSize/loadHeight` only at level load (the game's own
  `MonoInLevelDebugGrassViewRangeDialog` reloads the scene to apply a new load size). `RenderDistance.cpp` now walks
  the streaming manager's loader lists (`Singleton<DKKCBPEDPHF>` 1.6 / `Singleton<DPGBNEHAGDM>` 2.8 — new
  `DO_APP_FUNC_METHODINFO` slot in all three appdata headers, `tools/offsets-16.overrides.json`), picks the loaders
  whose descriptor type is TerrainGrass (8; 2.8 also HeightTerrainGrass 18) and scales their base + effective box
  (1.6 `0x38`/`0xE8`, 2.8 `0x30`/`0xE0`) with the same baseline/read-back/restore machinery and its own 5-strike
  guard; the loader table is logged once per level. Docs §4.1 and §8.3 updated. NOT yet tested in game.
- 2026-09-09 (corrected by the second 1.6 test, same day) **Render Distance: grass reach.** The 1.6 log disproved
  the "ratio-less grass loader" theory: the streaming manager's loader lists are a churning queue of per-sector
  JOBS (~100 alive, each ~1 s), the TerrainGrass jobs DO apply `layerLoadRatio` (base 80 m → effective 176–193 m at
  4×), and the true cause is the grass layer's tiny `SECTR_LayerConfig.loadSize` (80 m, vs 128–6000 m for every
  other layer; × the effective ratio 0.55 = profile 2.2 × preset 0.25 → ~44 m in stock, below the 120 m grass view range; the World lever at 4× does reach it: 8.8 × 0.25 = 2.2). Every new job copies loadSize at
  creation, so the fix is the game's own grass-debug-dialog write: `RenderDistance.cpp` now scales the TerrainGrass
  entry's `loadSize` in the live `SECTR_SceneSplitterConfig.layerConfigs` (getter 1.6 `JBPNDKLFIFM.HAAKNBJALEP`
  `0x0362D790` → `__RAW` + thunk, 2.8 `KIFKAIFDIIC.PKEAKDGLCKO` `0x03631310`; `0x0` on 3.3) by a new *Grass reach*
  slider (1–8, default 3.0), baseline/read-back/restore as the other levers; the per-job poke, the streaming-manager
  singleton slot and the per-job table (29 k log lines in 20 min) are gone; the layer-config table is logged once
  per config instead. Docs §4.1 corrected. NOT yet tested in game.
- 2026-09-09 (third 1.6 test) **Render Distance: per-layer streaming radius + job-queue probe.** Grass reach alone did
  not change the pop-in (the per-level dictionary holds the SAME `SECTR_LayerConfig` objects — `OICEICPMMLN.OAFMCJKHPCK`
  `0xEF44B0` — so the poke did reach new jobs). The layer table showed why: the global `layerLoadRatio` ×4 turned
  the terrain-sized layers (Terrain/Persistence 6000 m, BigStones 4096 m, 512–1024 m sectors) into 13 km boxes =
  thousands of sectors, saturating the streamer's queue while TerrainGrass (`sortOrder` 17 of 20) waited. The World
  lever no longer scales `layerLoadRatio`/`layerActiveRatio`; it scales each layer's `loadSize` ×m up to a cap of
  2400 (Persistence, Navmesh, Collider, ReflectionProbe and layers already above the cap untouched), Grass = TerrainGrass
  ×m×reach. A read-only probe (the streaming manager singleton slot, back in all three headers) logs the job counts
  per layer type every ~10 s and the first grass job's box. The grass view-range lever now keeps one baseline per
  array (the game re-applies `SetViewRange` alone on level load; the LOD sets were being multiplied twice).
  NOT yet tested in game.
- 2026-09-09 (fourth build, after the 1.6 + 2.8 tests of the third) **Render Distance: the level's layer tables and the
  shadow cascade split.** The probe settled the grass question: every grass job still copied `loadSize` 80 while the
  getter's `SECTR_LayerConfig` held our 2560 — the level resolves a job's layer through its OWN name→config dictionary,
  built once when the level is created (`OICEICPMMLN.OAFMCJKHPCK` `0xEF44B0` / 2.8 `NJPNGIPLALM.PLDOADEOPIG`
  `0x34A33C0`) from the splitter-config instance of that moment, and that instance is not the one the getter hands out
  later (the boot-time asset goes with its bundle; the getter reloads when its static is Unity-null). The third entry's
  "the dictionary holds the SAME objects" was true of the fill and wrong about the instance. Fix: after every loadSize
  write the feature re-runs the level's table build on the current level (`DKKCBPEDPHF.CJHCHHKIHMP` `0x147EC80` /
  `DPGBNEHAGDM.GECOHOBKIBI` `0x11B7700`), exactly the game's own step. Second finding, from the 2.8 report "the ground
  flickers, usually when it gets dark": ×4 on `shadowDistance` alone quadruples the metres per shadow-map texel of
  every cascade (the split is a fraction of the distance) — shadow acne crawling with the sun. New hook on the static
  `QualitySettings.set_shadowCascade4Split` (1.6 `0x052CAA20`, 2.8 `0x058BBAE0`, dummy-`this` shape like the
  distance setter) divides the split by the same multiplier: cascades 0–2 keep their metres, only the last stretches.
  Also corrected: 2.8's `EnviroSky` writes the shadow pair on its mobile path only. NOT yet tested in game.
- 2026-09-09 (fifth build, after the 2.8 test of the fourth: "grass flashes half-transparent") **Render Distance: the
  radii are baked when the level is built.** The rebuilt tables changed nothing — the probe still showed every grass
  job at base 80 and only 8–9 grass sectors loaded: a streaming job is a PER-SECTOR object of the level's sector
  tree, it copies its layer's `loadSize` once when the tree is built, and the candidate search only tests each job's
  stored box (1.6 `OICEICPMMLN.LMFFBMOHEKN` `0xEF3BF0`). Writing the config in-level reaches nothing. The flashes
  follow from that: the grass view range (480 m) reached past the real load radius (~180 m), so the streamer's ±10 %
  jitter band, which in stock flips sectors invisibly beyond the 120 m view range, flipped them in plain sight. Now
  the layer lever runs from the login screen on (the getter loads the BigWorld config on first use and the level
  creation reuses it), the by-path config loader that every level creation calls first is hooked (1.6
  `JBPNDKLFIFM.KJLFHILAMPB` `0x362E9C0`, 2.8 `KIFKAIFDIIC.FOEPCDHKLJB` `0x362C310`) so a freshly loaded config gets the
  radii before the tree is built, the probe reports grass jobs count/min/max base, and while a level's grass
  sectors are stale the grass view range is held at the game's (orange hint: relog). The cascade split is read once
  through `get_shadowCascade4Split` (1.6 `0x052CA400` __RAW + thunk, 2.8 `0x058BB940`) since the fourth test showed
  the hook alone never sees the split in a running session. NOT yet tested in game.
- 2026-09-09 (sixth build, after the 2.8 test of the fifth: "grass has gone entirely, even around me")
  **Render Distance: the radii are planned in sector units, and the probe was lying.** Two findings. (1) The probe
  capped each job list at 512 entries, so its totals were pinned at ~520 and its "no grass job" was partly its own
  truncation — raised to 4096, each list's size and a TRUNCATED marker are now logged. (2) The visual regression is
  real and reproducible: a TerrainGrass `loadSize` of 960 m (7.5 of its own 128 m sectors, against a stock 0.63)
  present when the level was built makes the layer stop streaming ALTOGETHER — no grass anywhere, not even at the
  character. Grass is now capped at 2 sectors (256 m). More generally the flat ×m on every layer was wrong: cost is
  `(2·loadSize·ratio / sectorMetres)²`, so ×4 costs 256× more on MiniModel (32 m sectors) than on HugeModel (512 m),
  and the tail of the sortOrder (grass 18, water 19, fog 20, probes 21) is what starves — the fifth-build probe
  showed type-2 models taking +95 sectors while grass, fog, water and probes lost theirs. Now every layer is capped
  at 6 of its own sectors, layers the game already sends ≥5 sectors out are left alone, and one common factor pulls
  all growth back until the config's projected cost fits 1.5× the level's own (grass exempt, served first). The
  grass view range is clamped to the radius its sectors actually reach, which replaces the fifth build's
  hold-it-at-stock hack. NOT yet tested in game.
- 2026-09-09 (seventh build, after the 2.8 test of the sixth: grass loads now - 52 sectors at 256 m against 8 at 80 -
  "but it still flickers, and the flicker stops when I raise the camera above the character")
  **Render Distance: the flicker is the shadow map's far cascade.** Camera PITCH cannot affect streaming (it is
  anchored on the avatar), so the flicker was never a streaming problem. Unity spreads four cascades of one shadow
  map over the whole shadow distance: the fourth build's split division keeps cascades 0-2 at their stock metres,
  but the last one then covers 0.125 x 3200 = 400 m out to 3200 m where the game gives it 400-800 m - a seventh of
  the texel density, so the distant ground crawls between lit and shadowed, worst at low sun, and only in the half
  of the frame that looks towards the horizon. The shadow lever is now capped at 1.5x however high the multiplier
  goes. The other thing that can change the distant image rather than just extend it - the profile's three HLOD
  ratios, which decide when a sector drops its impostor for real geometry - was split out of the World lever into
  its own nested toggle, so if anything still shimmers at distance it can be ruled out without another build. The
  sixth build's own results, from the same log: the job probe reports 2233 jobs across lists of 1204/1027/2, which
  buries the "~520 sector pool" reading for good (it was the old 512-per-list truncation), and the grass layer
  streams 52 sectors with base 256 m, effective 577 m. NOT yet tested in game.
- 2026-09-12 **Interactive map: 1.6 repaired, both Golden Apple Archipelago datasets regenerated, conches auto-complete.**
  `imap/InteractiveMap.{cpp,h}`: the interact hook now calls the origin FIRST (the hook guard drops a faulting
  handler's origin call, which would have cost the game the whole interaction), skips responses with `retcode_ != 0`,
  honours "Detect gathered items", and handles `InteractEchoShell` — conches are matched by the response's own
  `gadgetId_` (70380274/70500036 → EchoingConch, 70500033/70500053 → ImagingConch) rather than by an entity filter,
  because the conch entity is usually gone by the time the response arrives and a filter would also hand those labels
  to `CheckObjects`' auto-fix. The map's own windows and their click-blocking rects exist only while the feature is
  enabled (a focused ImGui window takes the GLOBAL input lock, which made the game map ignore pan/zoom/M/Esc), and the
  disabled path releases that lock; `DrawMain` gained the Enabled checkbox the feature never had; `DrawFilters` returns
  instead of default-inserting an empty scene; `ApplyScaling` skips a scene without both reference labels instead of
  dereferencing what `std::map::operator[]` inserts; `CheckObjects` only moves a point that is more than a metre off,
  so the generated datasets are not pinned by auto-fix; the minimap scale goes through the game's own getter on both
  versions (it is resolved on 1.6 now, which is why the old code guarded it away) - and it must stay that way: the
  compiler places `_miniMapScale` at object offset 0x4F0 in appdata-28 while the 2.8 getter reads `[ctx+0x500]`, so
  that generated layout is 16 bytes short at this field and a direct read would return the wrong float;
  `LevelToMapScreenPos` takes the screen size instead of calling into the game twice per point per frame. `cheat-base/render/backend/dx11-hook.cpp`: the present-depth guard is held across the chained call by an RAII
  counter — released before it, the overlay (every feature's `DrawExternal` plus the menu) was drawn twice per
  presented frame. `res/res.rc` + `LoadScenesData`: datasets are version-gated, so 1.6 no longer carries Enkanomiya,
  the Chasm and the 2.8 archipelago (-0.5 MB) and each version loads only the scenes its world has. New
  `tools/gen_gaa_map.py` generates the two archipelago datasets from the GIO server's own scene scripts; new icons
  `ImagingConch` (the game's `UI_ItemIcon_101935` — upstream shipped HoYoLAB's Echoing Conch image for both conches),
  `SeaRewardCrate`, `PhantasmalConchSlot`, `DreamPortal` (HoYoLAB map art of the same object class) and
  `ConstellationMechanism` (composed from HoYoLAB star art — the only derived icon; no authentic art exists).
  `tools/relic_gate_16.py` lost the `get_miniMapScale` null-guard step (the offset is resolved now).
- 2026-09-15 **Interactive map: settings that work, Show HD icons restored.**
  `imap/InteractiveMap.cpp`: every float setting of the feature was a `DragFloat` whose drag speed was the config step
  (0.01 px per pixel dragged for *Icon size*, so the slider looked dead); they are now `SliderFloat`s with fixed ranges
  and `ImGuiSliderFlags_AlwaysClamp` (`ConfigSlider`; icon size 8-96, minimap icon size 8-64, transparencies 0-1).
  *Dynamic size* scaled icons by map-view-rect / window width with no bound, which at normal zoom shrank them to a few
  pixels whatever *Icon size* said; the factor is clamped to 0.5-2 and the tooltip says what it does. The map, filter
  and materials windows get a first-use size so labels are not clipped, and a material with no art draws a checkbox
  instead of an empty 50x50 box. *Show HD icons* is back: one `LabelIcon()` lookup serves the filter rows (normal),
  the map and minimap (HD when the switch is on) and the materials window (HD only), and folds Unicode Roman numerals
  to ASCII first - Akebi's `SealLocation` + U+2160 clear name could never match an rc.exe resource name, so those 16
  Enkanomiya points never had an icon. New `tools/make_hd_icons.py` writes `res/iconsHD/` (569 icons at 128 px,
  10.9 MB - the upstream set was 41 MB at 256 px and mostly unused), the `res.rc` block between its markers and the
  project's iconsHD items (71 stale entries dropped, 5 missing added). Art comes from `tools/hd_icon_overrides/` first
  (`SOURCES.md` there): the correct `ImagingConch` (upstream's HD file is a byte copy of EchoingConch), HD art for
  `SeaRewardCrate`/`PhantasmalConchSlot`/`DreamPortal`, and game monster icons for the three 2.8 labels that had no
  icon at all - `AbyssHerald` (Abyss Herald: Wicked Torrents), `BathysmalVishap` (Bolteater) and `TheBlackSerpents`
  (Shadowy Husk: Line Breaker, the art HoYoLAB's own label uses) - each also added as a 30x30 `res/icons/` entry.
  `ConstellationMechanism` has no HD art (its icon is composed) and falls back to the normal icon.
  `cheat-base/render/ImageLoader.cpp`: only the fallback comment changed.
- 2026-09-23 **Free Camera Path** (`visuals/CameraPath.{h,cpp}`, registered in `cheat.cpp` BEFORE `FreeCamera` and in the
  vcxproj/filters; group "Free Camera Path" in Visuals - the groups of a tab are a `std::map`, so that name sorts it right
  under "Free Camera"). Keyframed camera moves for recording: the free camera's pose is captured as keyframes (hotkey `K`
  or the panel; `Home` = go to the first, `Delete` = remove the last, `P` = play / stop; all `Field<Hotkey>` and inert
  while the menu is shown, like the manager's own toggles), each keyframe carries a hold, the time of the move to the
  next one and that move's easing (linear / in / out / in & out); Play carries the camera through them - straight or on a
  centripetal Catmull-Rom curve re-timed by a 32-sample arc-length table (even speed along a bend), the angles the short
  way round per axis and, on the curve, through a Fritsch-Butland monotone cubic in time (no overshoot at a keyframe);
  "Stop at every keyframe" off makes the keyframes without a hold one continuous run eased only at its ends; Once / Loop
  (the last keyframe's time is the return) / Back and forth; a lead-in from wherever the camera is; a countdown drawn big
  on screen; playback speed; an optional game-time clock (slow motion slows the camera); Hide UI switched on for the take
  and restored; the mod's own overlays (status / info / FPS / notifications) hidden for the take and the menu closed when
  the move starts (new `CheatManagerBase::SetOverlayQuiet` / `RequestHideMenu`, the second served on the render thread);
  an on-screen preview of the keyframes and the curve while nothing plays; named JSON files under `campaths/` (temp +
  rename, the shot's playback settings inside a named save) plus a debounced `_autosave.json`; "Even out times"
  (durations proportional to the chords); an orbit generator (a looped circle of keyframes around the point the camera
  looks at); a numeric editor for the selected keyframe. The feature never writes a transform: it hands one absolute
  pose per frame to the free camera through a new pose API (`FreeCamera::IsRunning / GetPose / PushPose / CurrentCamera /
  IsCarryHeld`; `RelicTakePathPose` seats the targets AND the current rotation on the pushed pose in BOTH camera paths,
  skipping the input block and the smoothing for that frame, so the player's own input continues from the last pose
  without a snap; `f_FOV.value()` follows silently and is committed once when the take ends; every camera teardown
  clears the pending pose). Thread rules: il2cpp only on the game thread and never under `m_Lock` (a structured
  exception does not unwind a `lock_guard`; a lock leaked on the game thread would hang the render thread = the Present
  hook), `DrawMain` draws from a copy and writes its edits back under a version check, the projection of the preview
  writes into a pre-sized buffer under `relic::Try`. Recovery: a push is consumed once, so a silenced handler hands the
  camera back on its own; `Post(Stop)` is served on the calling thread; a second GameUpdate handler (`OnPathWatchdog`,
  own fault budget) ends a take whose player has been silent for 120 ticks; a scene change ends a take; with the
  character carried, Go-to flies (1 s) and the lead-in is at least 1 s, because a jump trips the carry's per-frame
  backstop and leaves the character behind. No new hooks or offsets: every binding used is a high-confidence one on
  1.6 (`Time_get_timeScale`, `WorldShiftManager_GetRelativePosition`, `Camera_WorldToScreenPoint`,
  `Camera_get_pixelWidth/Height`, `Screen_get_width/height`; `LoadingManager_IsLoaded` deliberately not used - the
  camera's own liveness covers a load). Builds clean for 16 / 28 / 33. NOT yet tested in game.
  Review round one (seven lenses - threads, the camera coupling, the maths, the panel, the files, the game versions, the
  owner's scenarios - and two refuters per finding) confirmed 32 defects, all fixed: a pose was still pushed while the
  camera was not seated and the scene sampled only once a second, so a scene change mid-take seated the NEW scene's
  camera on the OLD scene's coordinates (now: nothing is pushed while the camera is unseated, the scene is sampled every
  tick of a take, every stop revokes a pending pose - `FreeCamera::DropPose` - and `RelicDropDeadCameras` clears it;
  `RelicStopCamera` deliberately keeps it, so the "Drive the Game Camera" handoff no longer repeats and skips a frame);
  `ForwardOrder` classed the 2-keyframe Back-and-forth return as the loop's wrap (wrong time and ease, zero tangents);
  the exact last pose was never pushed when the final keyframe had no hold (one frame short); two keyframes captured on
  either side of straight down interpolated as a full spin (angles now run between the NEAREST Euler spellings); the
  countdown ran on the playback clock (Speed 0.25 = a 12 s countdown, a paused game = never; now real seconds); a jump
  Go-to / a 0 s lead-in with the character carried tripped the carry's backstop and left it behind (now a flight sized
  by the distance at 600 units/s, at least 1 s, started once the camera is seated); "Aim along the path" flew the lead-in
  to the recorded angles and then snapped to the direction of travel (now the lead-in aims where the first move goes and
  a hold turns towards the next move); a pan in place inside a continuous run stopped dead (a pan is a run of its own);
  asymmetric eases were not mirrored on the return leg; the panel's selection drifted on delete/insert and a stale index
  was written back; the keyframe table and the file row overflowed the 420 px pane; non-finite editor values, a short
  write replacing a good file, ANSI file names, silent truncation of a large file, a named save not reaching the
  autosave, '_' names never listed, stale preview dots through a loading screen, a raw (non-canonical) lead-in pose, a
  missing roll term in the lead-in test, blurry 16-px countdown digits scaled 6x, no feedback for hotkeys with the menu
  closed (toasts, the manager's idiom), and several tooltips that promised what the code did not do.
  Review round two (four lenses over the fixes, two refuters per finding) confirmed 21 more, all fixed: the flying
  Go-to's final push and the plain Go-to snap could still reach an unseated camera (a Go-to is now a take of its own
  that waits for the camera, snaps or flies once it is there, and every stop revokes a pending push); the carry-safe
  flight was sized in real seconds while the lead-in runs on the path clock (now sized by `Speed × time scale`, and
  the raw frame delta is capped at a 30 fps frame while the character is carried, since the carry's backstop is per
  frame); "Aim along the path" mixed an alt-spelling roll with canonical pitch/yaw in three places (the angles take the
  canonical keyframes under aim, the hold's arrival aim is canonicalised); every pushed pose is now re-spelled to the
  free camera's own continuous angles (a take no longer left the camera folded - inverted mouse pitch, a flipping
  Reset roll); the rewind of a multi-segment run used the wrong segment's ease; a hold-less Loop braked once per lap
  (a closed run plays at even pace); the forced Hide UI was written to the config (a game closed mid-take started
  with the UI hidden); the autosave was withheld for the whole take (flushed when Play is served); an aborted Go-to
  reported "at keyframe N"; the toast was a change detector (now the request's own output; the hotkey Stop toasts
  too); the countdown font token was never given its size (`CreateFontToken` ignores it - the ESP's idiom);
  the Save guard was byte-exact on a case-insensitive file system (the file system is asked); the playback settings
  were editable mid-take; and the inherited race on `ImGui::InsertNotification` (a `push_back` from the window or
  game thread under the render thread's index loop) is closed in `imgui-notify-v2/imgui_notify.h`: inserts are
  staged behind a mutex and taken in at the top of `RenderNotifications`.
- 2026-09-29 **Interactive map: marking keys that work with the map hidden, visible automatic marks, "Only where you
  have been".** From a player's suggestion: a key to mark a chest as done, a key to show / hide the map, and marking
  that works in both states, so the world can be explored blind without missing anything. `imap/InteractiveMap.{cpp,h}`
  and the new header-only `imap/MapProgress.h` (the pure rules, so they can be exercised outside the game). The keys
  existed upstream, but nobody could find them: *Complete nearest point* / *Revert latest completion* were drawn only in
  the map's own window, which exists only while the map is shown, and they read the avatar from the window thread and
  gave no sign of what they did. Now the feature has its own "Interactive Map" group in F1 > World (it was a bare
  "Enabled" among World's ungrouped switches): *Show on the map* (the old Enabled, same field), three keys - *Show /
  hide* (new `f_ToggleKey`), *Mark as done* and *Undo last mark* (the upstream fields), all unbound: no default keys, the
  players pick theirs -, *Only what you track*, *Search range*, *Mark chests, oculi and conches automatically*
  (upstream's "Detect gathered items") and *Show a message for automatic marks* (new, on). The keys only post a request
  on the window thread (inert while the menu is shown or owns the input and while hotkeys are off in Settings - the
  camera path's rule); a GameUpdate handler of their own serves it. A key that can type (a letter, a digit,
  punctuation, space, the numpad; not with Ctrl / Alt) is ignored while the cursor is out - the chat or a game menu -
  except on the big map; that is why the toggle is a key of the feature and not f_Enabled's own hotkey, whose manager
  path has no such guard. Mark takes the nearest point of the tracked filters in range, found-once points first (the
  categories Local Specialties, Ores, Materials, Inventory / Materials, Wood, Fishing, Animals and Enemies* come back,
  so they are taken only when nothing else is in range). When the nearest is a point marked less than 60 s ago it
  answers "is marked already" instead of marking the next one - most often the chest the automatic detection has just
  counted, and marking the next one would mark something the player may never have found - and a second press within
  6 s moves on. Undo takes back the latest mark, whatever made it, and the next Mark skips that point once. Every press
  answers with a toast (the camera path's idiom, at least 2.5 s on screen): marked / already marked / nothing within N m
  (pointing at Filters when no filter is on) / no map data here / unmarked / shown / hidden. Automatic marks: the gather
  and conch paths of the interact hook take the nearest point WHATEVER its state and leave it alone when it is marked
  already - upstream took the nearest unmarked one, so a chest marked by hand before it was opened handed its mark to an
  unopened chest of the same kind within 20 m -, say "found X - n / N done", and log an opened chest that matched no
  label (entity gone, a kind the filters do not know), so the first test in game shows at once whether the detection
  works; OnItemGathered no longer hands a null entity to the filters. *Only where you have been* (off by default):
  while on, the ground the avatar walks over is sampled every 500 ms on the render thread into 25 m cells per scene; a
  byte grid per dataset scene (the extent of its points plus 225 m) marks every cell within the reveal radius (80 m by
  default, 25-200) of an explored one, and the map and the minimap skip unfinished points outside it - finished ones
  follow Show completed as before. The ground is saved next to the completed points and in their scope
  (`explored_ground`: `{"v":1,"cell":25,"scenes":{"4":"x,y;..."}}`), at most every 30 s, when the option is switched off
  and on Forget; an account / profile switch repositions the field under a lock and flags a reload, and a save checks
  the flag under the same lock, so the ground walked on one account never lands in another's section. Fixed on the way:
  CompletePoint logged the avatar's distance - a call into the game - under m_UserDataMutex, where a fault the handler
  guard swallows would have left the mutex locked for good. Builds clean for 16 / 28 / 33; the pure rules pass a
  standalone MSVC harness (/W4 /WX); NOT yet tested in game.
- 2026-09-29 (later) **Interactive map: opened sealed / buried chests are marked, and no longer mark a neighbour.**
  The owner's 2.8 test in Teyvat: sealed and buried chests were not marked when opened. Cause: the Teyvat data (Akebi's,
  from HoYoLAB) lists a sealed, a buried or a rock-covered chest ONLY under its marker label - Sealed Chest (136),
  Buried Chest (68), Large / Small Rock Pile (98 / 17), median 40-55 m from the nearest Common..Remarkable point, so not
  duplicates - and those labels have no entity filter, while the automatic marking looked only at the rarity label the
  chest's filter matched. So the marker stayed, and when another chest of that rarity lay within 20 m, THAT chest was
  marked: the test's cfg.json shows Common Chest 2830 marked 8 s before the player marked Sealed Chest 2777 by key,
  16.6 m away. CheckObjects did the same in its own way: "Fix item positions" dragged a Common Chest point 8 m onto a
  sealed chest and "Detect new items" added a Common Chest on top of another. Now an opened chest goes through
  `OnChestOpened` + `mapprogress::ChestPicker`: the nearest point among its rarity label and the chest markers (20 m, the
  detection range), a chest whose prefab says it was sealed ("_Locked" / "_Locker") taking a Sealed Chest marker within
  10 m first, and - for a chest whose name says nothing usable - any chest point within 6 m; the winner is taken
  whatever its state. CheckObjects leaves alone a chest entity that a marker claims (a Sealed Chest marker within 10 m of
  a sealed-looking chest, or a marker nearer than any point of its rarity). The 2.8 archipelago had two gaps of its own:
  its music-thorn chests ("...MusicThornTreasurebox_0N" - lower-case b, so the filters' "TreasureBox" never matched) and
  reflection chests were in the data but could never be detected (the 6 m fallback covers them), and 8 chests were
  missing from the data: `tools/gen_gaa_map.py` now also classifies SceneObj_(Essence)Chest_Rock / Bramble / Frozen_LvN
  (one in-rock chest, 70210063) and MusicThornTreasurebox_01 (seven, Common by their "puzzle, low" drop tag like a Lv1
  chest); `res/map_gaa28_scene9.json` regenerated - CommonChest 94 -> 102, nothing else changed; the 1.6 file came out
  byte-identical (the generator reproduced both files exactly from the local server data first). Builds clean for
  16 / 28 / 33; the new rules pass the standalone harness; NOT yet tested in game.
- 2026-09-29 **Attack Effects / Kill Aura on 1.6: "Multiply the damage", a sturdier Attack Speed, and a diagnostics
  section that says which part fails.** The owner's report: on 1.6, One-Punch and the multiplier do nothing, Attack Speed
  does nothing either (all three work on 2.8, where the saved profile has Multi-Hit + One-Punch + Attack Speed on), and
  Kill Aura works only with a mode switched on. Statically everything on the 1.6 path checks out and nothing on it has
  changed since the 2026-08-23 "One-Punch works" test: both hook targets, the struct layouts, the avatar tags (the three
  upstream tag hashes are CRC32 of AVATAR_MOVE_ATTACK / AVATAR_AIR_ATTACK / AVATAR_SKILL, all present in 1.6's own
  animator tag table DCBBFCGFEHN @0x029837F0). What HAS changed is the client: the 1.6.1 install is fully hotpatched now
  (res 3557509, data 3526661, silence 3266913 - the InjectFix/Lua payload Relic's hotpatch mirror serves), the August
  test predates it, and every method on both paths opens with an xLua check (`static[off] != null`) and an InjectFix one
  (`IsPatched(id)`). Also read off the binary: the 1.6 damage is computed on the attacker's side only (Formula
  KAFOKIHGDFN's single caller is DoHitEntity), the target's side scales it by the reaction factor in place
  (CIDPEKCCCNL$$CKGHIGANAIK @0x02899F90: damage *= (1 - reduction)(1 + amplify)), the dispatcher (GNMJCKLPHJA
  @0x01716D10) syncs each local-origin event, and the being-hit sync (JGHJJNAECLA$$NOFKBDNHOCF @0x00D686A0) drops a hit
  whose rejectState carries HasAttackLanded - no client-side de-duplication of repeated hits was found, so whether
  repeated hits count is up to the server. Changes (`player/RapidFire.{h,cpp}`, `world/KillAura.cpp`,
  `cheat-base/relic-diag.{h,cpp}`):
  * Multi-Hit gets a **Method**: "Repeat the hit" (upstream) or "Multiply the damage" (new, the 1.6 default; 2.8 keeps
    Repeat) - the hit goes out once with `damage` (and `damageShield`) times the multiplier, One-Punch = the target's HP
    x1.05 + 1, and with Multi-Target every copy shares the one AttackResult, so all targets get the largest value.
  * Attack Speed: only the active character's own animator (upstream also sped up bullets / owned gadgets), the speed goes
    back to 1.0 only after 250 ms without an attack state (upstream reset it on the first non-attack item, which can be
    another layer of the same animator in the same frame), re-asserted at the start of each frame from GameUpdate while
    attacking (the hook writes from the animator-event LateTick, after the frame's animation step), dead animators
    skipped (m_CachedPtr), every write under its own relic::Try so a fault never drops the game's HandleProcessItem.
    AVATAR_NORMAL_ATTACK (222085953) and AVATAR_ATTACK (379396322) joined the attack tags (both versions' tables have them).
  * Custom Element and Auto weakspot no longer need the Enabled switch; Randomize can reach its max (`% (hi - lo + 1)`);
    null checks where upstream dereferenced a missing target / avatar / attacker property; orange hints when Enabled is on
    with no mode (and the reverse), same for Kill Aura; Kill Aura skips a queued monster whose combat is gone.
  * **Diagnostics** (collapsible, bottom of Attack Effects, both versions) + `[atk]` lines in `relic-diag.txt` through the
    new `relic::diag::append_line` (no logger, works with file logging off): animator items seen / the character's / in an
    attack tag, speed writes / put back by the game / restored, the speed read back after our write and at the next frame
    start, hits seen / ours / on a valid target / repeated copies / multiplied, the character's animator tags by name, and
    an **HP probe** - one hit at a time, the target's HP when the hit is handed over and 1.5 s later against what was sent.
    1.6 only: a **hotfix probe** - the InjectFix wrapper array read directly (ILFixDynamicMethodWrapper_TypeInfo @0x09016BB0,
    static field 0, length +0x18, items +0x20: every patched id, mapped offline with the id map from the IsPatched call sites)
    and, for 30 methods on the hit / sync / animator paths, both checks (the class name behind each TypeInfo slot is
    compared first, so a wrong slot reports "not initialised" instead of a verdict).
  Builds clean for 16 / 28; NOT yet tested in game - the next 1.6 session's `relic-diag.txt` answers which of the
  hypotheses holds (hotfix replaced a method / tags / speed put back / server ignores repeated or multiplied hits).
- 2026-09-29 (later) **What the 1.6 test of the diagnostics build settled; "Multiply the damage" out; Infinite Stamina can no
  longer strand the client at 0.** The owner's 1.6 session (local Windows agent stack) answered every open question from
  `relic-diag.txt`: the hotfix probe read **InjectFix: 0 methods patched, nothing replaced on the attack paths** - the
  hotpatch hypothesis was wrong; **Attack Speed works** (1159 writes, `fixups=0`, animator speed read back 5.00 after the write
  and at the next frame start) - the character's animator runs several items a frame (its tags include AVATAR_RUN_TO_IDLE
  = #486559515, 1007 times, interleaved with MOVE_ATTACK), so upstream's reset-on-the-first-non-attack-item flip-flopped the
  speed inside one LateTick; the 250 ms hold is the fix; the extra tags never showed up; **Repeat the hit works** (x60:
  2745 -> 0; One-Punch x19: 1084 -> 0); **Multiply the damage does not** (sent 16563 / 14056 / 12859, the target lost
  792 / 1140 / 742 - exactly the game's own damage, hit for hit: a GIO server computes every hit itself and ignores the
  client's number), so that method is removed again (RapidFire keeps upstream's single method; the diagnostics stay).
  The same session reported stamina stuck at 0 with Infinite Stamina OFF. The player's save written at logout
  (`t_player_data_1.bin_data`, PlayerDataBin.basic_bin: persist_stamina_limit 70, cur_persist_stamina 170 - the field
  layout read from the protobuf descriptor embedded in the gameserver ELF) said FULL, so the 0 was client-side, and the
  1.6 gameserver (shipped with full debug info) shows why it could not heal: stamina is server-owned - `Avatar::setIsInDash`
  raises a sprint flag on MotionDash / MotionDangerDash (and a fast MotionJump), `Avatar::procStaminaInTimer` drains every
  200 ms while it is set and also for MotionFly / MotionSwimDash / MotionSkiffDash, recovers (25/s from AvatarData) only
  after a delay since the last cost, `FightHandler::onEvtCostStaminaNotify` ignores client-reported costs - and
  `PlayerBasicComp::changeCurStamina` notifies the client only when the value CHANGES. Upstream's normal mode BLOCKED the
  stamina props behind an order-dependent window (a TEMPORARY update opened it, a MAX update closed it); the owner's
  profile had it on at login, a stale 0 got through, and a client that believes it has 0 never sprints, climbs or charges,
  so the full server never had a reason to send another value - a deadlock until relog. `player/InfiniteStamina.{h,cpp}`:
  nothing is blocked any more; in normal mode the CURRENT stamina the server reports is raised to the maximum it last
  reported (only ever leaving the client showing MORE than the server, which the next real change corrects); on 1.6 the
  packet-replacement mode (the one that keeps the server from spending stamina) is the default, and an orange line says
  what normal mode really does; every stamina prop the server sends is written to `relic-diag.txt` as `[sta]` (server value
  -> what the client got, the mode; the first 60 of a session, then one per 30 s). Builds clean for 16 / 28; the stamina
  change is NOT yet tested in game (a relog clears the owner's current stuck 0 by itself - the login sends the real 170).
