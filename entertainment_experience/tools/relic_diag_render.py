#!/usr/bin/env python3
"""Adds (--on) or removes (--off) throttled diagnostics along the overlay path, to find where the menu is lost
on a game version: the DX11 present hook, the render event and the menu toggle each log once every ~600 frames.
"""
import os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
BASE = os.path.normpath(os.path.join(HERE, "..", "mod", "cheat-base", "src", "cheat-base"))
MARK = "// RELIC-DIAG"

SNIPPETS = [
    (os.path.join(BASE, "render", "renderer.cpp"),
     "\t\tevents::RenderEvent();",
     "\t\t{   " + MARK + "\n"
     "\t\t\tstatic int s_relicFrames = 0;\n"
     "\t\t\tif ((s_relicFrames++ % 600) == 0)\n"
     "\t\t\t\tLOG_INFO(\"RELIC-DIAG present hook: frame %d, about to raise RenderEvent\", s_relicFrames);\n"
     "\t\t}\n"
     "\t\tevents::RenderEvent();"),
    (os.path.join(BASE, "cheat", "CheatManagerBase.cpp"),
     "\t\tauto& settings = feature::Settings::GetInstance();\n\n\t\t// Relic: the menu key is read FIRST",
     "\t\tauto& settings = feature::Settings::GetInstance();\n\n"
     "\t\t{   " + MARK + "\n"
     "\t\t\tstatic int s_relicRenders = 0;\n"
     "\t\t\tif ((s_relicRenders++ % 600) == 0)\n"
     "\t\t\t\tLOG_INFO(\"RELIC-DIAG OnRender: call %d, menu shown=%d, features=%d\", s_relicRenders,\n"
     "\t\t\t\t\t(int)s_IsMenuShowed, (int)m_Features.size());\n"
     "\t\t}\n"
     "\t\t// Relic: the menu key is read FIRST"),
    (os.path.join(BASE, "cheat", "CheatManagerBase.cpp"),
     "\t\tif (settings.f_MenuKey.value().IsReleased() && !ImGui::IsAnyItemActive())\n\t\t\tToggleMenuShow();",
     "\t\tif (settings.f_MenuKey.value().IsReleased() && !ImGui::IsAnyItemActive())\n"
     "\t\t{\n"
     "\t\t\tLOG_INFO(\"RELIC-DIAG menu key released -> toggling (was %d)\", (int)s_IsMenuShowed);   " + MARK + "\n"
     "\t\t\tToggleMenuShow();\n"
     "\t\t}"),
]

def rd(p):
    with open(p, "r", encoding="utf-8", newline="") as f:
        return f.read()

def wr(p, s):
    with open(p, "w", encoding="utf-8", newline="") as f:
        f.write(s)

def on():
    for path, old, new in SNIPPETS:
        s = rd(path)
        nl = "\r\n" if "\r\n" in s else "\n"
        o = old.replace("\n", nl)
        n = new.replace("\n", nl)
        if n in s:
            print(os.path.basename(path), "- already instrumented"); continue
        assert o in s, "%s: pattern not found:\n%s" % (path, old[:80])
        wr(path, s.replace(o, n, 1))
        print(os.path.basename(path), "- diagnostics added")

def off():
    for path in {p for p, _, _ in SNIPPETS}:
        s = rd(path)
        nl = "\r\n" if "\r\n" in s else "\n"
        lines = s.split(nl)
        out, drop_block = [], 0
        i = 0
        while i < len(lines):
            l = lines[i]
            if MARK in l and l.strip().startswith("{"):
                depth = 0
                while i < len(lines):
                    depth += lines[i].count("{") - lines[i].count("}")
                    i += 1
                    if depth <= 0:
                        break
                continue
            if MARK in l and "LOG_INFO" in l:
                i += 1
                continue
            out.append(l)
            i += 1
        s2 = nl.join(out)
        # collapse the leftover braces of the toggle block
        s2 = s2.replace("\t\t{" + nl + "\t\t\tToggleMenuShow();" + nl + "\t\t}", "\t\t\tToggleMenuShow();")
        if s2 != s:
            wr(path, s2); print(os.path.basename(path), "- diagnostics removed")
        else:
            print(os.path.basename(path), "- nothing to remove")

if __name__ == "__main__":
    if "--off" in sys.argv:
        off()
    else:
        on()
