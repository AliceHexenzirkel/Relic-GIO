# injector/ — the Win11 injector (launcher.exe + mhynot2.dll)

On Windows 11 the game only starts through `ayy/anime/build/launcher.exe`: it creates `GenshinImpact.exe`
suspended and injects `mhynot2.dll`, a user-mode emulation of the `mhyprot2` anti-cheat driver; with the
in-game enhancements on, Relic hands the same launcher the enhancements DLL as well
(`docs/ENTERTAINMENT-EXPERIENCE.md`). Both files ship in `payload/common/ayy/anime/build/` with a `.bin`
suffix and are put into the game folder by `Install/GamePatcher.cs` according to `payload/manifest.json`.

## Source

| | |
|---|---|
| Upstream | https://github.com/khang06/mhynot2 (author: khang06) |
| Pinned commit | `be25d340b174f0b2d5791d160e485cd04316c4c5`, the `injector/mhynot2` git submodule |
| Licence | The upstream repository publishes no licence file, so its source is not redistributed here: `git submodule update --init --recursive` fetches it from GitHub into `injector/mhynot2/`. Relic ships only the compiled files, credited in `README.md` ("Credits") and in `payload/manifest.json`. |
| minhook | https://github.com/TsudaKageyu/minhook, the submodule's own submodule (`injector/mhynot2/minhook`), BSD-2-Clause (`injector/mhynot2/minhook/LICENSE.txt`) |

The upstream tree is not patched. `injector/CMakeLists.txt` is a wrapper that adds the upstream project
with `add_subdirectory` and sets from outside what Relic needs differently:

- **Console subsystem.** Upstream declares `launcher` a `WIN32` (GUI) executable; the wrapper sets
  `WIN32_EXECUTABLE OFF`. `launcher.cpp` defines `main()` and reports every step of the injection with
  `printf`; a console executable has a standard output Relic captures into its log (`Launch/GameLauncher.cs`:
  "launcher.exe exited N: ..."), a GUI one has none and a failed start would say nothing.
- **minhook as a target.** `dllmain.cpp` asks the linker for `minhook.x64d.lib` by name
  (`#pragma comment(lib, ...)`); minhook is linked as a CMake target instead, so the wrapper drops that
  request with `/NODEFAULTLIB:minhook.x64d.lib`.
- **Policy floor.** minhook asks for CMake 3.0, which CMake 4 refuses; the wrapper sets
  `CMAKE_POLICY_VERSION_MINIMUM 3.5` and `CMP0091 NEW` (one CRT choice for every object) before the
  subdirectory is added.
- **Reproducible output.** `/Brepro` on compiler and linker, as for the enhancements DLL: the same sources
  and toolset give the same bytes, so hashes can be compared across builds.

## Build

In a Visual Studio developer environment (`vcvars64.bat`, or `Launch-VsDevShell.ps1 -Arch amd64`), with
CMake 3.16 or newer and Ninja (both ship with Visual Studio's "C++ CMake tools for Windows" component):

```
git submodule update --init --recursive
cmake -S injector -B injector/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build injector/build
```

The outputs are `injector/build/launcher.exe` and `injector/build/mhynot2.dll`. `build\build_dlls.ps1
-SkipEe` does the same from a plain PowerShell prompt: it finds Visual Studio with vswhere, enters the
vcvars64 environment itself and prints size, sha256, whether a file imports a Visual C++ runtime DLL and
the comparison with the committed payload files. The GitHub workflow `.github/workflows/dlls.yml`
("DLL builds") builds it on every change under `injector/`, offers the two files as the artifact
`injector-static-crt` and fails when one of them imports a Visual C++ runtime DLL.

## Static C runtime

`RELIC_STATIC_CRT` (default `ON`) links the C runtime statically: the files import only `KERNEL32`,
`SHLWAPI`, `USER32` and `ADVAPI32` and carry what they need. That is the build
`payload/common/ayy/anime/build/` holds, and the only kind that may be there: `build\publish.ps1` refuses
a payload `.exe` / `.dll` that imports a Visual C++ runtime DLL, `build\make_release_bundles.py` refuses
such a pair for `relic-mhynot-patch.zip`, the workflow fails on such an output and `build_dlls.ps1
-CopyToPayload` does not copy one. They all apply one name rule, `build\pe_imports.ps1`
(`make_release_bundles.py` runs on Linux too and carries the same expression in Python).

`-DRELIC_STATIC_CRT=OFF` links the DLL runtime (`VCRUNTIME140.dll`, `MSVCP140.dll`, `VCRUNTIME140_1.dll`
and the `api-ms-win-crt-*` set), for a local experiment. Such a pair depends on the Visual C++
redistributable of the player's PC, which Relic neither installs nor asks for, and fails in two ways:

- **No redistributable** (a freshly installed Windows). `launcher.exe` does not start: Windows shows
  "VCRUNTIME140.dll was not found" and the exit code is `0xC0000135`, which relic.log shows as
  `launcher.exe exited -1073741515`. On Windows 11 no client starts; on Windows 10 Relic falls back to the
  plain start, without the in-game enhancements.
- **An old redistributable.** `launcher.exe` starts, injects and exits 0, so relic.log shows a clean
  injected launch. `mhynot2.dll` takes a `std::mutex` at every `DeviceIoControl` of the game process, and
  the lock code is the installed `msvcp140.dll`'s: a mutex that MSVC 14.51 initialises at compile time
  makes `msvcp140.dll` 14.31 fault (a read at address 0), while 14.38 locks it. The process dies at its
  first `DeviceIoControl`. Reproduced with a stand-in process, not in the game.

`dumpbin /dependents <file>` tells which kind a file is; `build_dlls.ps1` reads the import table itself.

The game's own plugins are outside this build: the 1.6 client's `MTBenchmark_Windows.dll` and the 2.8
client's `MiHoYoMTRSDK.dll`, among others, import `VCRUNTIME140.dll` themselves. The 1.6 client of a
player without the redistributable shows "Plugins: Failed to load ... MTBenchmark_Windows.dll with error
'0x7e'" at its start; the Microsoft Visual C++ Redistributable (x64) is what the game expects.

## Updating payload/

Nothing updates `payload/common/ayy/anime/build/` on its own, neither the workflow nor a plain run of
`build_dlls.ps1`: those two files start the game for every player on Windows 11, so a new build is
committed there only after the game was launched with it through Relic. `build\build_dlls.ps1 -SkipEe
-CopyToPayload` copies the build over `launcher.exe.bin` / `mhynot2.dll.bin` and writes their sha256 into
`payload/manifest.json` (`entertainment_experience/tools/payload_manifest.py --set-sha`); `git diff` shows
the change, and `build\publish.ps1` verifies the hashes and the import tables at every ship build.

The launches, with a Relic build published from that payload (`build\publish.ps1` and the installer):
Windows 11 and Windows 10, each with the in-game enhancements on and off (on Windows 10 with them off the
game starts directly and the pair is not used). In every launch through the injector:

- the game folder holds the new files: the sha256 of `ayy\anime\build\launcher.exe` and `mhynot2.dll`
  equal the manifest's;
- relic.log shows `launch method: InjectedLauncher` and no `launcher.exe exited` line. That alone does not
  prove that `mhynot2.dll` loaded: `launcher.exe` exits 0 without looking at what `LoadLibraryA` returned;
- the console window `mhynot2.dll` opens beside the game shows `Mode: Emulator`, eleven `Installed hook
  for ...` lines and `Unlinked from PEB!`, then the game's requests (`IOCTL called ... DrvInit` first),
  and no message box titled `PANIC!!!!!!` appears. This window is the proof that the DLL loaded;
- the client reaches the login door and loads the world; with the enhancements on, F1 opens the menu;
- quitting from the game's menu ends `GenshinImpact.exe` (the console closes with it), and Relic then
  stops Fiddler and restores the profile.
