#!/usr/bin/env python3
"""Fixes the calling convention of STATIC game methods for a version whose IL2CPP compiles them with a
leading dummy `this` (game 1.6 / metadata v24 does; 2.8 and 3.3 do not).

Evidence (1.6, `MoleMole.ActorUtils::GetAvatarPos`, a static method):
    xor r8d,r8d          ; r8  = MethodInfo (3rd argument)
    lea rcx,[rsp+0x40]   ; rcx = hidden return buffer (Vector3)
    xor edx,edx          ; rdx = the dummy `this`, always null
    call ActorUtils_GetAvatarPos
while 2.8 calls the same method with (return buffer, MethodInfo) only. Declaring the 3.3 signature on 1.6
therefore shifts every argument one register to the left: the callee reads our first argument as `this`,
our MethodInfo as the first real argument, and so on. It crashes the game on the first singleton lookup
(`Singleton<T>::get_Instance` reads `method->klass` from rdx) and would silently corrupt the rest.

What this does, for every declaration in appdata-<ver>/il2cpp-functions.h whose dump signature has exactly
one more parameter than ours (a leading `Il2CppObject* __this`):
  * hook targets keep their name and simply gain the parameter — the hook handler in the feature code has
    to gain it too (the tool lists them; they are gated with `#if RELIC_GAME_VERSION`);
  * every other function is renamed to `<name>__RAW` (with the extra parameter) and gets an inline wrapper
    with the ORIGINAL 3.3 signature in appdata-<ver>/il2cpp-static-thunks.h, so no call site changes.
The wrappers header is included from framework/il2cpp-appdata.h; empty stubs are written for the versions
that need no thunks.

  gen_static_thunks.py --ver 16 [--dump <dir>] [--check]
"""
import json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
EE = os.path.normpath(os.path.join(HERE, ".."))
MOD = os.path.join(EE, "mod")
DUMPS = {"16": os.path.join(EE, "dumps_ref", "1.6", "greenxemotion"),
         "28": os.path.join(EE, "dumps_ref", "2.8")}
DECL = re.compile(r"^(?P<indent>\s*)DO_APP_FUNC\(\s*(?P<rva>0x[0-9A-Fa-f]+)\s*,\s*(?P<ret>.+?),\s*(?P<name>[A-Za-z_]\w*)\s*,\s*\((?P<params>.*?)\)\s*\);(?P<tail>.*)$")

def split_params(s):
    if not s.strip():
        return []
    out, depth, cur = [], 0, ""
    for ch in s:
        if ch in "(<":
            depth += 1
        elif ch in ")>":
            depth -= 1
        if ch == "," and depth == 0:
            out.append(cur.strip()); cur = ""
        else:
            cur += ch
    if cur.strip():
        out.append(cur.strip())
    return out

def param_name(p, i):
    """the declared name of a parameter, or a synthetic one"""
    clean = re.sub(r"/\*.*?\*/", " ", p).strip()
    m = re.search(r"([A-Za-z_]\w*)\s*$", clean)
    if m and m.group(1) not in ("void", "unsigned", "int", "float", "bool", "char", "short", "long"):
        return m.group(1), p
    name = "p%d" % i
    return name, p.rstrip() + " " + name

def hooked_names():
    names = set()
    src = os.path.join(MOD, "cheat-library", "src", "user")
    for root, _, files in os.walk(src):
        for f in files:
            if f.endswith((".cpp", ".h")):
                t = open(os.path.join(root, f), encoding="utf-8", errors="replace").read()
                # INSTALL_HOOK is the guarded wrapper every feature uses (relic_guard_hooks.py rewrote the raw calls)
                for m in re.finditer(r"(?:HookManager::install(?:Guarded)?|INSTALL_HOOK)\(\s*app::(\w+)", t):
                    names.add(m.group(1))
    return names

def dump_params(dumpdir):
    d = json.load(open(os.path.join(dumpdir, "script.json"), encoding="utf-8"))
    out = {}
    for m in d["ScriptMethod"]:
        if m["Address"] in out:
            continue
        sig = m["Signature"]
        inner = sig[sig.index("(") + 1: sig.rindex(")")]
        out[m["Address"]] = split_params(inner)
    return out

def main():
    a = sys.argv[1:]
    if "--ver" not in a:
        print(__doc__)
        return 2
    ver = a[a.index("--ver") + 1]
    dumpdir = a[a.index("--dump") + 1] if "--dump" in a else DUMPS[ver]
    check = "--check" in a
    appdata = os.path.join(MOD, "cheat-library", "src", "appdata-" + ver)
    hdr = os.path.join(appdata, "il2cpp-functions.h")
    dparams = dump_params(dumpdir)
    hooks = hooked_names()

    lines = open(hdr, encoding="utf-8", errors="surrogateescape", newline="").read().split("\n")
    nl = "\r\n" if lines and lines[0].endswith("\r") else "\n"
    if nl == "\r\n":
        lines = [l[:-1] if l.endswith("\r") else l for l in lines]

    thunks, hooked_statics, changed, already = [], [], 0, 0
    for i, ln in enumerate(lines):
        m = DECL.match(ln)
        if not m:
            continue
        name = m.group("name")
        if name.endswith("__RAW"):
            already += 1
            continue
        rva = int(m.group("rva"), 16)
        if rva == 0:
            continue
        ours = split_params(m.group("params"))
        theirs = dparams.get(rva)
        if not theirs or len(theirs) != len(ours) + 1 or "__this" not in theirs[0]:
            continue
        # this declaration is a static method that needs the dummy `this`
        if name in hooks:
            hooked_statics.append(name)
            newparams = "void* __this_unused, " + m.group("params") if ours else "void* __this_unused"
            lines[i] = "%sDO_APP_FUNC(%s, %s, %s, (%s));%s" % (
                m.group("indent"), m.group("rva"), m.group("ret"), name, newparams,
                m.group("tail") + "  // Relic 1.6: static -> leading dummy `this` (the hook handler carries it too)")
            changed += 1
            continue
        named = [param_name(p, k) for k, p in enumerate(ours)]
        decl_params = ", ".join(p for _, p in named)
        args = ", ".join(n for n, _ in named)
        newparams = ("void* __this_unused, " + decl_params) if ours else "void* __this_unused"
        lines[i] = "%sDO_APP_FUNC(%s, %s, %s__RAW, (%s));%s" % (
            m.group("indent"), m.group("rva"), m.group("ret"), name, newparams, m.group("tail"))
        ret = m.group("ret").strip()
        body = "%s__RAW(nullptr%s);" % (name, (", " + args) if args else "")
        thunks.append("    inline %s %s(%s) { %s%s }" % (ret, name, decl_params, "" if ret == "void" else "return ", body))
        changed += 1

    print("appdata-%s: %d static declarations fixed (%d already done), %d inline wrappers, %d hooked statics: %s"
          % (ver, changed, already, len(thunks), len(hooked_statics), ", ".join(hooked_statics) or "-"))
    if check:
        return 0
    if changed:
        open(hdr, "w", encoding="utf-8", errors="surrogateescape", newline="").write(nl.join(lines))
    # the wrappers header (always written, so every version has the file the framework includes)
    for v in ("16", "28", "33"):
        p = os.path.join(MOD, "cheat-library", "src", "appdata-" + v, "il2cpp-static-thunks.h")
        if not os.path.isdir(os.path.dirname(p)):
            continue
        if v == ver and thunks:
            content = ["// GENERATED by tools/gen_static_thunks.py - do not edit by hand.",
                       "// Game %s compiles static methods with a leading dummy `this`; the raw pointers in" % v,
                       "// il2cpp-functions.h carry it, these wrappers keep the 3.3 call sites unchanged.",
                       "", "namespace app", "{"] + thunks + ["}", ""]
            open(p, "w", encoding="utf-8", newline="\n").write("\n".join(content))
            print("   wrote", os.path.relpath(p, EE))
        elif not os.path.exists(p):
            open(p, "w", encoding="utf-8", newline="\n").write(
                "// GENERATED by tools/gen_static_thunks.py - no static-call thunks are needed for this game version.\n")
            print("   wrote empty stub", os.path.relpath(p, EE))
    return 0

if __name__ == "__main__":
    sys.exit(main())
