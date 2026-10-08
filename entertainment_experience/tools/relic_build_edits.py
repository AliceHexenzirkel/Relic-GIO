#!/usr/bin/env python3
"""One-shot, idempotent edits that turn the upstream akebi-gc build into Relic's multi-version build:
  * relic-ee.sln   : akebi-gc.sln without the injector project
  * cheat-library  : toolset v145, GameVersion property (appdata-$(GameVersion) include + RELIC_GAME_VERSION define),
                     per-version OutDir/IntDir, no injector ProjectReference / CustomBuildStep, Release_WS on C++20,
                     static CRT + /Brepro in Release configs
  * cheat-base     : toolset v145, per-version OutDir/IntDir, static CRT + /Brepro in Release configs
Run from anywhere; paths are relative to this file. Safe to re-run.
"""
import os, re

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.normpath(os.path.join(HERE, "..", "mod"))
LIB = os.path.join(MOD, "cheat-library", "cheat-library.vcxproj")
BASE = os.path.join(MOD, "cheat-base", "cheat-base.vcxproj")
SLN_OLD = os.path.join(MOD, "akebi-gc.sln")
SLN_NEW = os.path.join(MOD, "relic-ee.sln")
BS = chr(92)  # backslash, spelled out so no shell/escaping layer can eat it

def rd(p):
    with open(p, "r", encoding="utf-8-sig", newline="") as f:
        return f.read()

def wr(p, s):
    with open(p, "w", encoding="utf-8", newline="") as f:
        f.write(s)

def edit_sln():
    src = SLN_OLD if os.path.exists(SLN_OLD) else SLN_NEW
    s = rd(src)
    s = re.sub(r'Project\("\{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942\}"\) = "injector".*?EndProject\r?\n', "", s, flags=re.S)
    s = re.sub(r'^\s*\{F578B30C-8DE6-4741-99E4-1D30D2ACDAC4\}\..*\r?\n', "", s, flags=re.M)
    wr(SLN_NEW, "﻿" + s.lstrip("﻿"))
    if os.path.exists(SLN_OLD) and SLN_OLD != SLN_NEW:
        os.remove(SLN_OLD)
    print("sln: injector removed ->", os.path.basename(SLN_NEW))

RELIC_PG = ('  <PropertyGroup Label="Relic">\n'
            '    <!-- Game version this DLL targets: 16 | 28 | 33 (selects src/appdata-$(GameVersion) and RELIC_GAME_VERSION) -->\n'
            '    <GameVersion Condition="\'$(GameVersion)\'==\'\'">33</GameVersion>\n'
            '  </PropertyGroup>\n')

OUT_OLD = "$(SolutionDir)bin" + BS + "$(Configuration)-$(PlatformShortName)" + BS
OUT_NEW = "$(SolutionDir)bin" + BS + "v$(GameVersion)" + BS + "$(Configuration)-$(PlatformShortName)" + BS

def common(s, nl):
    s = s.replace("<PlatformToolset>v143</PlatformToolset>", "<PlatformToolset>v145</PlatformToolset>")
    s = s.replace(OUT_OLD + "</OutDir>", OUT_NEW + "</OutDir>")
    s = s.replace(OUT_OLD + "obj" + BS + "$(ProjectName)" + BS + "</IntDir>", OUT_NEW + "obj" + BS + "$(ProjectName)" + BS + "</IntDir>")
    if 'Label="Relic"' not in s:
        s = re.sub(r'(  <PropertyGroup Label="Globals">.*?</PropertyGroup>\r?\n)',
                   lambda m: m.group(1) + RELIC_PG.replace("\n", nl), s, count=1, flags=re.S)
    return s

def release_blocks(s, fn):
    """apply fn(block_text) to the ItemDefinitionGroup blocks of Release|x64 and Release_WS|x64"""
    pat = r'  <ItemDefinitionGroup Condition="\'\$\(Configuration\)\|\$\(Platform\)\'==\'Release(?:_WS)?\|x64\'">.*?</ItemDefinitionGroup>'
    return re.sub(pat, lambda m: fn(m.group(0)), s, flags=re.S)

def add_static_crt_and_brepro(block, nl, with_link):
    if "<RuntimeLibrary>" not in block:
        block = block.replace("<ConformanceMode>true</ConformanceMode>",
                              "<ConformanceMode>true</ConformanceMode>" + nl + "      <RuntimeLibrary>MultiThreaded</RuntimeLibrary>", 1)
    if "/Brepro" not in block:
        block = block.replace("<ConformanceMode>true</ConformanceMode>",
                              "<ConformanceMode>true</ConformanceMode>" + nl + "      <AdditionalOptions>/Brepro %(AdditionalOptions)</AdditionalOptions>", 1)
        if with_link:
            block = re.sub(r"(<Link>\r?\n)",
                           lambda m: m.group(1) + "      <AdditionalOptions>/Brepro /PDBALTPATH:%_PDB% %(AdditionalOptions)</AdditionalOptions>" + nl,
                           block, count=1)
    return block

def edit_lib():
    s = rd(LIB)
    nl = "\r\n" if "\r\n" in s else "\n"
    s = common(s, nl)
    s = s.replace("$(ProjectDir)src/appdata;", "$(ProjectDir)src/appdata-$(GameVersion);")
    s = s.replace("<PreprocessorDefinitions>_AMD64_;", "<PreprocessorDefinitions>RELIC_GAME_VERSION=$(GameVersion);_AMD64_;")
    s = re.sub(r'\s*<CustomBuildStep>.*?</CustomBuildStep>', "", s, flags=re.S)
    s = re.sub(r'\s*<ProjectReference Include="\.\.[^"]*injector\.vcxproj">.*?</ProjectReference>', "", s, flags=re.S)
    s = s.replace("<LanguageStandard>stdcpp17</LanguageStandard>", "<LanguageStandard>stdcpp20</LanguageStandard>")
    s = release_blocks(s, lambda b: add_static_crt_and_brepro(b, nl, with_link=True))
    wr(LIB, s)
    print("cheat-library.vcxproj edited")

def edit_base():
    s = rd(BASE)
    nl = "\r\n" if "\r\n" in s else "\n"
    s = common(s, nl)
    s = release_blocks(s, lambda b: add_static_crt_and_brepro(b, nl, with_link=False))
    wr(BASE, s)
    print("cheat-base.vcxproj edited")

if __name__ == "__main__":
    edit_sln()
    edit_lib()
    edit_base()
