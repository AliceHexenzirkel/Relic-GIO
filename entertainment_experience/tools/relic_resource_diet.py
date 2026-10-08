#!/usr/bin/env python3
"""Idempotent resource diet for mod/cheat-library (Relic): drops the 41 MB HD icon set and the CJK/Cyrillic
fonts from res.rc (the files themselves live in heavy_ref/, gitignored), trims the font loading in
cheat.cpp to the default (Latin) font, and teaches ImageLoader::GetImage to fall back from "HD<name>" to
"<name>" so every ESP / interactive-map code path keeps working with the standard icons.
Paths are relative to this file. Safe to re-run.
"""
import os, re

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.normpath(os.path.join(HERE, "..", "mod"))
RC = os.path.join(MOD, "cheat-library", "res", "res.rc")
CHEAT = os.path.join(MOD, "cheat-library", "src", "user", "cheat", "cheat.cpp")
IMG = os.path.join(MOD, "cheat-base", "src", "cheat-base", "render", "ImageLoader.cpp")
BS = chr(92)

def edit_rc():
    with open(RC, "rb") as f:
        data = f.read()
    nl = b"\r\n" if b"\r\n" in data else b"\n"
    lines = data.split(nl)
    out, dropped = [], 0
    for ln in lines:
        if (b"iconsHD" + BS.encode() * 2) in ln or b"IMGUI_FONT_CHINESE" in ln or b"IMGUI_FONT_CYRILLIC" in ln:
            dropped += 1
            continue
        out.append(ln)
    # collapse runs of >1 blank lines that the removals may have left in the RCDATA block
    cleaned, blank = [], 0
    for ln in out:
        if ln.strip() == b"":
            blank += 1
            if blank > 1:
                continue
        else:
            blank = 0
        cleaned.append(ln)
    with open(RC, "wb") as f:
        f.write(nl.join(cleaned))
    print(f"res.rc: dropped {dropped} lines (iconsHD + CJK/Cyrillic fonts)")

def edit_cheat():
    with open(CHEAT, "r", encoding="utf-8", newline="") as f:
        s = f.read()
    nl = "\r\n" if "\r\n" in s else "\n"
    pat = re.compile(
        r'(\t\tauto defaultFont = renderer::Font::LoadFontFromResource\(IMGUI_FONT, RT_RCDATA, "DefaultFont", renderer::Font::FONT_RANGE_DEFAULT\);\r?\n)'
        r'\t\tauto ChineseFont = .*?\r?\n'
        r'\t\tauto cyrillicFont = .*?\r?\n'
        r'\t\trenderer::AddFont\(defaultFont\);\r?\n'
        r'\t\trenderer::AddFont\(cyrillicFont\);\r?\n'
        r'\t\trenderer::AddFont\(ChineseFont\);\r?\n', re.S)
    new = (r"\1" + "\t\t// Relic: only the Latin default font is embedded (the CJK/Cyrillic faces were 16 MB of the DLL)." + nl +
           "\t\trenderer::AddFont(defaultFont);" + nl)
    s2, n = pat.subn(new, s, count=1)
    if n:
        with open(CHEAT, "w", encoding="utf-8", newline="") as f:
            f.write(s2)
        print("cheat.cpp: font loading trimmed to the default font")
    elif "Relic: only the Latin default font" in s:
        print("cheat.cpp: already trimmed")
    else:
        raise SystemExit("cheat.cpp: font block not found - check the file")

def edit_imageloader():
    with open(IMG, "r", encoding="utf-8", newline="") as f:
        s = f.read()
    nl = "\r\n" if "\r\n" in s else "\n"
    marker = "Relic: the HD icon set is not embedded"
    if marker in s:
        print("ImageLoader.cpp: fallback already present")
        return
    old = ("\tbool loadResult = ResourceLoader::LoadEx(imageName.c_str(), type, pDestination, size);" + nl +
           "\tif (!loadResult)" + nl + "\t{")
    new = ("\tbool loadResult = ResourceLoader::LoadEx(imageName.c_str(), type, pDestination, size);" + nl +
           "\tif (!loadResult && imageName.size() > 2 && imageName.compare(0, 2, \"HD\") == 0)" + nl +
           "\t{" + nl +
           "\t\t// " + marker + " (41 MB); serve the standard icon of the same name instead." + nl +
           "\t\tauto fallback = GetImage(imageName.substr(2), type);" + nl +
           "\t\tif (fallback)" + nl +
           "\t\t\ts_Textures[imageName] = *fallback;" + nl +
           "\t\treturn fallback;" + nl +
           "\t}" + nl +
           "\tif (!loadResult)" + nl + "\t{")
    if old not in s:
        raise SystemExit("ImageLoader.cpp: LoadEx block not found - check the file")
    s = s.replace(old, new, 1)
    with open(IMG, "w", encoding="utf-8", newline="") as f:
        f.write(s)
    print("ImageLoader.cpp: HD -> standard icon fallback added")

if __name__ == "__main__":
    edit_rc()
    edit_cheat()
    edit_imageloader()
