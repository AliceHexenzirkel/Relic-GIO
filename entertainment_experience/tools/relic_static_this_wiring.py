#!/usr/bin/env python3
"""Wires up the "static methods take a dummy `this` on 1.6" fix (see gen_static_thunks.py):
  * framework/il2cpp-appdata.h : includes the generated appdata-<ver>/il2cpp-static-thunks.h
  * framework/helpers.h        : RELIC_STATIC_THIS / RELIC_STATIC_THIS_ARG (empty on 2.8/3.3)
  * the three hook handlers on static methods carry the extra parameter through those macros
Idempotent.
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.normpath(os.path.join(HERE, "..", "mod", "cheat-library", "src"))

def rd(p):
    with open(p, "r", encoding="utf-8", newline="") as f:
        return f.read()

def wr(p, s):
    with open(p, "w", encoding="utf-8", newline="") as f:
        f.write(s)

def patch(path, old, new, tag):
    s = rd(path)
    if new in s:
        print("%s: already patched" % tag); return
    assert old in s, "%s: pattern not found" % tag
    wr(path, s.replace(old, new, 1))
    print("%s: patched" % tag)

def appdata_h():
    p = os.path.join(SRC, "framework", "il2cpp-appdata.h")
    s = rd(p)
    nl = "\r\n" if "\r\n" in s else "\n"
    if "il2cpp-static-thunks.h" in s:
        print("il2cpp-appdata.h: already includes the thunks"); return
    anchor = "#undef DO_APP_FUNC" + nl + "#undef DO_APP_FUNC_METHODINFO"
    assert anchor in s
    add = (anchor + nl + nl +
           "// Relic: on game 1.6 (metadata v24) every STATIC method is compiled with a leading dummy `this`," + nl +
           "// so those pointers above are declared as <name>__RAW with the extra parameter. These generated" + nl +
           "// inline wrappers give the call sites back the original 3.3 signature. Empty on 2.8/3.3." + nl +
           "namespace app" + nl + "{" + nl + "\t#include \"il2cpp-static-thunks.h\"" + nl + "}")
    wr(p, s.replace(anchor, add, 1))
    print("il2cpp-appdata.h: thunks included")

def helpers_h():
    p = os.path.join(SRC, "framework", "helpers.h")
    s = rd(p)
    nl = "\r\n" if "\r\n" in s else "\n"
    if "RELIC_STATIC_THIS" in s:
        print("helpers.h: already has the macros"); return
    anchor = "#define IS_SINGLETON_LOADED("
    i = s.index(anchor)
    add = ("// Relic: game 1.6 compiles every STATIC method with a leading dummy `this` (always null). A hook" + nl +
           "// handler for such a method has to carry it as well; on 2.8/3.3 these expand to nothing." + nl +
           "#if RELIC_GAME_VERSION <= 16" + nl +
           "#define RELIC_STATIC_THIS     void* __this_unused," + nl +
           "#define RELIC_STATIC_THIS_ARG __this_unused," + nl +
           "#else" + nl +
           "#define RELIC_STATIC_THIS" + nl +
           "#define RELIC_STATIC_THIS_ARG" + nl +
           "#endif" + nl + nl)
    wr(p, s[:i] + add + s[i:])
    print("helpers.h: RELIC_STATIC_THIS added")

if __name__ == "__main__":
    appdata_h()
    helpers_h()
    U = os.path.join(SRC, "user", "cheat")
    patch(os.path.join(U, "player", "GodMode.h"),
          "\t\tstatic bool Miscs_CheckTargetAttackable_Hook(app::BaseEntity* attacker, app::BaseEntity* target, MethodInfo* method);",
          "\t\tstatic bool Miscs_CheckTargetAttackable_Hook(RELIC_STATIC_THIS app::BaseEntity* attacker, app::BaseEntity* target, MethodInfo* method);",
          "GodMode.h")
    patch(os.path.join(U, "player", "GodMode.cpp"),
          "\tbool GodMode::Miscs_CheckTargetAttackable_Hook(app::BaseEntity* attacker, app::BaseEntity* target, MethodInfo* method)",
          "\tbool GodMode::Miscs_CheckTargetAttackable_Hook(RELIC_STATIC_THIS app::BaseEntity* attacker, app::BaseEntity* target, MethodInfo* method)",
          "GodMode.cpp (signature)")
    patch(os.path.join(U, "player", "GodMode.cpp"),
          "\t\treturn CALL_ORIGIN(Miscs_CheckTargetAttackable_Hook, attacker, target, method);",
          "\t\treturn CALL_ORIGIN(Miscs_CheckTargetAttackable_Hook, RELIC_STATIC_THIS_ARG attacker, target, method);",
          "GodMode.cpp (CALL_ORIGIN)")
    patch(os.path.join(U, "misc", "sniffer", "PacketSniffer.h"),
          "\t\tstatic int32_t KcpNative_kcp_client_send_packet_Hook(void* kcp_client, app::KcpPacket_1* packet, MethodInfo* method);",
          "\t\tstatic int32_t KcpNative_kcp_client_send_packet_Hook(RELIC_STATIC_THIS void* kcp_client, app::KcpPacket_1* packet, MethodInfo* method);",
          "PacketSniffer.h")
    patch(os.path.join(U, "misc", "sniffer", "PacketSniffer.cpp"),
          "\tint32_t PacketSniffer::KcpNative_kcp_client_send_packet_Hook(void* kcp_client, app::KcpPacket_1* packet, MethodInfo* method)",
          "\tint32_t PacketSniffer::KcpNative_kcp_client_send_packet_Hook(RELIC_STATIC_THIS void* kcp_client, app::KcpPacket_1* packet, MethodInfo* method)",
          "PacketSniffer.cpp (signature)")
    patch(os.path.join(U, "misc", "sniffer", "PacketSniffer.cpp"),
          "\t\treturn CALL_ORIGIN(KcpNative_kcp_client_send_packet_Hook, kcp_client, packet, method);",
          "\t\treturn CALL_ORIGIN(KcpNative_kcp_client_send_packet_Hook, RELIC_STATIC_THIS_ARG kcp_client, packet, method);",
          "PacketSniffer.cpp (CALL_ORIGIN)")
    print("done")
