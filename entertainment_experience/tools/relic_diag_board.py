#!/usr/bin/env python3
"""Wires the relic-diag counter board into the overlay path (idempotent).

The board answers one question the log cannot: which stage of a frame stopped running. Every stage bumps an
interlocked counter and a watchdog thread of our own dumps them to `relic-diag.txt` next to the DLL, so it
survives a dead logger, a swallowed fault and a stuck render thread.
"""
import os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.normpath(os.path.join(HERE, "..", "mod"))
BASE = os.path.join(MOD, "cheat-base", "src", "cheat-base")

EDITS = [
    # the Present detour itself
    (os.path.join(BASE, "render", "backend", "dx11-hook.cpp"),
     '#include "dx11-hook.h"',
     '#include "dx11-hook.h"\n#include <cheat-base/relic-diag.h>'),
    (os.path.join(BASE, "render", "backend", "dx11-hook.cpp"),
     "\tstatic BOOL g_bInitialised = false;",
     "\tRELIC_DIAG_TICK(PRESENT);\n\tstatic BOOL g_bInitialised = false;"),

    # the ImGui frame
    (os.path.join(BASE, "render", "renderer.cpp"),
     "\t\tImGui::NewFrame();\n\n\t\tevents::RenderEvent();\n\n\t\tImGui::EndFrame();",
     "\t\tImGui::NewFrame();\n\n\t\tRELIC_DIAG_TICK(DX11_IN);\n\t\tevents::RenderEvent();\n\n\t\tImGui::EndFrame();"),
    (os.path.join(BASE, "render", "renderer.cpp"),
     "\t\tpContext->OMSetRenderTargets(1, &mainRenderTargetView, nullptr);\n"
     "\t\tImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());",
     "\t\tpContext->OMSetRenderTargets(1, &mainRenderTargetView, nullptr);\n"
     "\t\tImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());\n\t\tRELIC_DIAG_TICK(DX11_OUT);"),
    (os.path.join(BASE, "render", "renderer.cpp"),
     '#include "renderer.h"',
     '#include "renderer.h"\n#include <cheat-base/relic-diag.h>'),

    # the cheat manager's own frame
    (os.path.join(BASE, "cheat", "CheatManagerBase.cpp"),
     "#include <cheat-base/relic-guard.h>",
     "#include <cheat-base/relic-guard.h>\n#include <cheat-base/relic-diag.h>"),
    (os.path.join(BASE, "cheat", "CheatManagerBase.cpp"),
     "\t\trenderer::Init(dxVersion);",
     "\t\trelic::diag::start();\n\t\trenderer::Init(dxVersion);"),
    (os.path.join(BASE, "cheat", "CheatManagerBase.cpp"),
     "\t\tauto& settings = feature::Settings::GetInstance();\n\n"
     "\t\t// Relic: the menu key is read FIRST",
     "\t\tRELIC_DIAG_TICK(RENDER_IN);\n"
     "\t\tauto& settings = feature::Settings::GetInstance();\n\n"
     "\t\t// Relic: the menu key is read FIRST"),
    (os.path.join(BASE, "cheat", "CheatManagerBase.cpp"),
     "\t\tif (settings.f_MenuKey.value().IsReleased() && !ImGui::IsAnyItemActive())\n"
     "\t\t\tToggleMenuShow();\n\n"
     "\t\tDrawExternal();",
     "\t\tbool toggleWanted = settings.f_MenuKey.value().IsReleased() && !ImGui::IsAnyItemActive();\n"
     "\t\tRELIC_DIAG_TICK(KEY_SEEN);\n"
     "\t\tif (toggleWanted)\n"
     "\t\t{\n"
     "\t\t\tRELIC_DIAG_TICK(TOGGLE);\n"
     "\t\t\tToggleMenuShow();\n"
     "\t\t}\n\n"
     "\t\tDrawExternal();\n"
     "\t\tRELIC_DIAG_TICK(EXTERNAL_OUT);"),
    (os.path.join(BASE, "cheat", "CheatManagerBase.cpp"),
     "\t\tif (m_IsProfileConfigurationShowed)",
     "\t\tRELIC_DIAG_TICK(MENU_OUT);\n\n\t\tif (m_IsProfileConfigurationShowed)"),
    (os.path.join(BASE, "cheat", "CheatManagerBase.cpp"),
     "\t\tif (settings.f_FpsShow)\n\t\t\tDrawFps();",
     "\t\tif (settings.f_FpsShow)\n\t\t\tDrawFps();\n\n\t\tRELIC_DIAG_TICK(RENDER_OUT);"),

    # every handler of every event
    (os.path.join(BASE, "events", "event.hpp"),
     "#include <cheat-base/relic-guard.h>",
     "#include <cheat-base/relic-guard.h>\n#include <cheat-base/relic-diag.h>"),
    (os.path.join(BASE, "events", "event.hpp"),
     "                    if (handler->relicFaults < relic::kGuardFaultLimit)\n                    {",
     "                    if (handler->relicFaults < relic::kGuardFaultLimit)\n"
     "                    {\n"
     "                        RELIC_DIAG_TICK(HANDLER_RUN);",
     ),
    (os.path.join(BASE, "events", "event.hpp"),
     "                            handler->relicFaults++;\n"
     "                            relic::guard_report_fault(typeid(*handler).name(), relic::g_lastGuardCode,",
     "                            handler->relicFaults++;\n"
     "                            relic::diag::note_fault(typeid(*handler).name(), relic::g_lastGuardCode);\n"
     "                            relic::guard_report_fault(typeid(*handler).name(), relic::g_lastGuardCode,"),
    (os.path.join(BASE, "events", "event.hpp"),
     "                    }\n                }\n                m_eventCore.coreMutex.lock_shared();",
     "                    }\n"
     "                    else\n"
     "                        RELIC_DIAG_TICK(HANDLER_SKIP);\n"
     "                }\n                m_eventCore.coreMutex.lock_shared();"),
]

PROJ = os.path.join(MOD, "cheat-base", "cheat-base.vcxproj")
PROJ_EDITS = [
    ('<ClInclude Include="src\\cheat-base\\relic-guard.h" />',
     '<ClInclude Include="src\\cheat-base\\relic-guard.h" />\n    <ClInclude Include="src\\cheat-base\\relic-diag.h" />'),
    ('<ClCompile Include="src\\cheat-base\\relic-guard.cpp" />',
     '<ClCompile Include="src\\cheat-base\\relic-guard.cpp" />\n    <ClCompile Include="src\\cheat-base\\relic-diag.cpp" />'),
]


def apply(path, old, new):
    with open(path, "r", encoding="utf-8", newline="") as f:
        s = f.read()
    nl = "\r\n" if "\r\n" in s else "\n"
    o, n = old.replace("\n", nl), new.replace("\n", nl)
    if n in s:
        return "already"
    if o not in s:
        return "MISSING: " + old.strip().split(nl)[0][:70]
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(s.replace(o, n, 1))
    return "patched"


if __name__ == "__main__":
    bad = 0
    for path, old, new in EDITS + [(PROJ, o, n) for o, n in PROJ_EDITS]:
        r = apply(path, old, new)
        if r.startswith("MISSING"):
            bad += 1
        print("%-24s %s" % (os.path.basename(path), r))
    sys.exit(1 if bad else 0)
