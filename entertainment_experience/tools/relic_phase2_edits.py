#!/usr/bin/env python3
"""Idempotent phase-2 edits (Relic defaults) on mod/:
  * cheat-library.vcxproj : drop the ProtectionBypass / RSAPatch entries (files deleted), pass RELIC_GAME_VERSION
                            to the resource compiler in every configuration
  * res/res.rc            : ASSEMBLYCHECKSUMS picks assembly_checksum-<ver>.json by RELIC_GAME_VERSION; the
                            localized scam-warning images (res/warnings) are no longer embedded
  * cheat-base CheatManagerBase.cpp/.h : no watermark / first-run scam dialog (About keeps only the credits page)
Safe to re-run.
"""
import os, re

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.normpath(os.path.join(HERE, "..", "mod"))
BS = chr(92)

def rd(p, enc="utf-8"):
    with open(p, "r", encoding=enc, newline="") as f:
        return f.read()

def wr(p, s, enc="utf-8"):
    with open(p, "w", encoding=enc, newline="") as f:
        f.write(s)

def vcxproj():
    p = os.path.join(MOD, "cheat-library", "cheat-library.vcxproj")
    s = rd(p, "utf-8-sig"); nl = "\r\n" if "\r\n" in s else "\n"
    n = 0
    for kind, name in (("ClInclude", "RSAPatch.h"), ("ClInclude", "ProtectionBypass.h"),
                       ("ClCompile", "RSAPatch.cpp"), ("ClCompile", "ProtectionBypass.cpp")):
        line = f'    <{kind} Include="src{BS}user{BS}cheat{BS}misc{BS}{name}" />' + nl
        if line in s:
            s = s.replace(line, "", 1); n += 1
    rc = ("    <ResourceCompile>" + nl +
          "      <PreprocessorDefinitions>RELIC_GAME_VERSION=$(GameVersion);%(PreprocessorDefinitions)</PreprocessorDefinitions>" + nl +
          "    </ResourceCompile>" + nl)
    added = 0
    if "RELIC_GAME_VERSION=$(GameVersion);%(PreprocessorDefinitions)</PreprocessorDefinitions>" + nl + "    </ResourceCompile>" not in s:
        s, added = re.subn(r"  </ItemDefinitionGroup>", lambda m: rc + "  </ItemDefinitionGroup>", s)
    wr(p, s)
    print(f"cheat-library.vcxproj: {n} stale entries removed, ResourceCompile defines added to {added} groups")

def res_rc():
    p = os.path.join(MOD, "cheat-library", "res", "res.rc")
    b = open(p, "rb").read(); nl = b"\r\n" if b"\r\n" in b else b"\n"
    out, dropped, done = [], 0, False
    for ln in b.split(nl):
        if (b"warnings" + BS.encode() * 2) in ln:
            dropped += 1; continue
        if ln.startswith(b"ASSEMBLYCHECKSUMS") and b"#if RELIC_GAME_VERSION" not in b:
            out += [b"#if RELIC_GAME_VERSION == 16",
                    b'ASSEMBLYCHECKSUMS       RCDATA                  "assembly_checksum-16.json"',
                    b"#elif RELIC_GAME_VERSION == 28",
                    b'ASSEMBLYCHECKSUMS       RCDATA                  "assembly_checksum-28.json"',
                    b"#else", ln, b"#endif"]
            done = True; continue
        out.append(ln)
    open(p, "wb").write(nl.join(out))
    print(f"res.rc: warnings images dropped={dropped}, per-version checksum block {'added' if done else 'already there'}")

def cheat_manager():
    cpp = os.path.join(MOD, "cheat-base", "src", "cheat-base", "cheat", "CheatManagerBase.cpp")
    h = os.path.join(MOD, "cheat-base", "src", "cheat-base", "cheat", "CheatManagerBase.h")
    s = rd(cpp); nl = "\r\n" if "\r\n" in s else "\n"
    if "DrawWarning" in s:
        # the whole DrawWarning() definition
        s = re.sub(r"\tvoid CheatManagerBase::DrawWarning\(\)\r?\n\t\{.*?\n\t\}\r?\n\r?\n", "", s, count=1, flags=re.S)
        s = s.replace("#include <user/cheat/misc/About.h>" + nl, "", 1)
        s = s.replace("\t\tauto& about = feature::About::GetInstance();" + nl, "", 1)
        s = re.sub(r"\t\tif \(about\.show && !about\.f_IsFirstTime\)\r?\n\t\t\tDrawWarning\(\);\r?\n\r?\n", "", s, count=1)
        s = re.sub(r"\t\tif \(!about\.m_IsScamWarningShowed && about\.f_IsFirstTime\)\r?\n\t\t\tabout\.ShowInGameScamWarning\(\);\r?\n", "", s, count=1)
        assert "about" not in s.lower() or "DrawWarning" not in s, "CheatManagerBase.cpp: leftover About references"
        wr(cpp, s); print("CheatManagerBase.cpp: watermark + scam dialog removed")
    else:
        print("CheatManagerBase.cpp: already clean")
    hs = rd(h)
    if "void DrawWarning();" in hs:
        hs = re.sub(r"\t\tvoid DrawWarning\(\);\r?\n", "", hs, count=1); wr(h, hs); print("CheatManagerBase.h: DrawWarning removed")

if __name__ == "__main__":
    vcxproj(); res_rc(); cheat_manager()
