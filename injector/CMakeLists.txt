# Relic's build of the Win11 injector: launcher.exe + mhynot2.dll from the sources in the injector/mhynot2
# submodule (khang06/mhynot2 with its minhook submodule). The upstream tree is built as it is; everything
# Relic needs differently is set from this file, so the submodule carries no patch.
#
#   cmake -S injector -B injector/build -G Ninja -DCMAKE_BUILD_TYPE=Release
#   cmake --build injector/build
#
# Run inside a Visual Studio developer environment (vcvars64.bat, or Launch-VsDevShell.ps1 -Arch amd64)
# so that cl.exe, link.exe and the Windows SDK are found; build\build_dlls.ps1 does all of this. The two
# outputs land in the build folder itself (Ninja) or in its configuration subfolder (Visual Studio
# generators).
#
# Options:
#   RELIC_STATIC_CRT   ON (default): the C runtime is linked statically, so the files run on a PC without
#                      the Visual C++ redistributable (no VCRUNTIME140.dll import). OFF links the DLL
#                      runtime instead.
cmake_minimum_required(VERSION 3.16)

# minhook declares cmake_minimum_required(VERSION 3.0), which CMake 4 refuses unless this floor is set
# before its directory is added. It and the policy below must come BEFORE project() / add_subdirectory().
if(NOT DEFINED CMAKE_POLICY_VERSION_MINIMUM)
  set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
endif()
# CMP0091 NEW: the CRT is chosen by CMAKE_MSVC_RUNTIME_LIBRARY, not by /MD or /MT baked into
# CMAKE_<LANG>_FLAGS_<CONFIG>. The default makes the subdirectories follow the same rule (minhook asks for
# the policies of 3.0, where CMP0091 does not exist), so every object of the build uses one CRT.
cmake_policy(SET CMP0091 NEW)
set(CMAKE_POLICY_DEFAULT_CMP0091 NEW)

# Checked before project(): a clone without the submodule gets this message, not a compiler-detection
# failure.
set(_relic_mhynot2 "${CMAKE_CURRENT_SOURCE_DIR}/mhynot2")
if(NOT EXISTS "${_relic_mhynot2}/CMakeLists.txt")
  message(FATAL_ERROR
    "injector/mhynot2 is empty: the submodule is not initialised. In the repository root run\n"
    "    git submodule update --init --recursive\n"
    "and configure again.")
endif()
if(NOT EXISTS "${_relic_mhynot2}/minhook/CMakeLists.txt")
  message(FATAL_ERROR
    "injector/mhynot2/minhook is empty: the nested submodule is not initialised. In the repository root run\n"
    "    git submodule update --init --recursive\n"
    "and configure again.")
endif()

project(relic-injector LANGUAGES C CXX)

option(RELIC_STATIC_CRT "Link the C runtime statically (no VCRUNTIME140.dll dependency)" ON)
if(RELIC_STATIC_CRT)
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
else()
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")
endif()

# Both outputs side by side at the top of the build folder (a multi-config generator adds its
# configuration subfolder), where build\build_dlls.ps1 and the CI workflow pick them up.
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}")

if(MSVC)
  # Reproducible binaries: the same sources and toolset give the same bytes, so a hash can be compared
  # across builds (the enhancements DLL is built with the same flag).
  add_compile_options("$<$<COMPILE_LANGUAGE:C,CXX>:/Brepro>")
  add_link_options(/Brepro)
endif()

add_subdirectory(mhynot2)

# Console subsystem: launcher.cpp defines main() and reports every step of the injection with printf, and
# only a console executable shows that output. Upstream declares the target WIN32; the committed
# payload/common/ayy/anime/build/launcher.exe.bin is a console executable.
set_target_properties(launcher PROPERTIES WIN32_EXECUTABLE OFF)

if(MSVC)
  # dllmain.cpp asks the linker for minhook's Debug import library by name (#pragma comment(lib,
  # "minhook.x64d.lib")). minhook is linked as a target of this build instead, so that request has to be
  # dropped, or the link fails looking for a file no Release build produces.
  target_link_options(mhynot2 PRIVATE /NODEFAULTLIB:minhook.x64d.lib)
endif()
