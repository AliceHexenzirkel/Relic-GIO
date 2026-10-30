# Relic's build of the RSA key patch: RSAPatch.dll from the sources in the rsapatch/RSAPatch submodule
# (34736384/RSAPatch). Renamed to mhypbase.dll in the game folder, it replaces the game's RSA keys so the
# client can log in to a private server. The upstream tree is built as it is; the static CRT is set from
# this file, so the submodule carries no patch.
#
#   cmake -S rsapatch -B rsapatch/build -G Ninja -DCMAKE_BUILD_TYPE=Release
#   cmake --build rsapatch/build
#
# Run inside a Visual Studio developer environment (vcvars64.bat, or Launch-VsDevShell.ps1 -Arch amd64)
# so that cl.exe, link.exe, ml64.exe (MASM) and the Windows SDK are found; build\build_dlls.ps1 does all
# of this. The output lands in the build folder itself (Ninja) or in its configuration subfolder (Visual
# Studio generators).
#
# Options:
#   RELIC_STATIC_CRT   OFF (default): the C runtime is linked dynamically, because RSAPatch uses C++
#                      standard library calls (std::filesystem, std::string) in a TLS callback that runs
#                      before the static CRT would be initialised — the DLL runtime must already be loaded
#                      when that callback fires. ON links the CRT statically but the DLL will not work.
cmake_minimum_required(VERSION 3.16)

if(NOT DEFINED CMAKE_POLICY_VERSION_MINIMUM)
  set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
endif()
cmake_policy(SET CMP0091 NEW)
set(CMAKE_POLICY_DEFAULT_CMP0091 NEW)

set(_relic_rsapatch_src "${CMAKE_CURRENT_SOURCE_DIR}/RSAPatch/RSAPatch")
if(NOT EXISTS "${_relic_rsapatch_src}/dllmain.cpp")
  message(FATAL_ERROR
    "rsapatch/RSAPatch is empty: the submodule is not initialised. In the repository root run\n"
    "    git submodule update --init --recursive\n"
    "and configure again.")
endif()

project(relic-rsapatch LANGUAGES CXX ASM_MASM)

option(RELIC_STATIC_CRT "Link the C runtime statically (no VCRUNTIME140.dll dependency)" OFF)
if(RELIC_STATIC_CRT)
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
else()
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")
endif()

set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}")

if(MSVC)
  add_compile_options("$<$<COMPILE_LANGUAGE:CXX>:/Brepro>")
  add_link_options(/Brepro)
endif()

add_library(RSAPatch SHARED
    "${_relic_rsapatch_src}/dllmain.cpp"
    "${_relic_rsapatch_src}/exports.cpp"
    "${_relic_rsapatch_src}/Utils.cpp"
    "${_relic_rsapatch_src}/version.asm"
)

target_include_directories(RSAPatch PRIVATE "${_relic_rsapatch_src}")
target_link_libraries(RSAPatch PRIVATE "${_relic_rsapatch_src}/detours.lib")
target_link_options(RSAPatch PRIVATE "/DEF:${_relic_rsapatch_src}/Exports.def")
target_compile_features(RSAPatch PRIVATE cxx_std_17)
