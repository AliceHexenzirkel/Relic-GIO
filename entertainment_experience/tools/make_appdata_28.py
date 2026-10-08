#!/usr/bin/env python3
"""Builds mod/cheat-library/src/appdata-28/ (game 2.8 OS) from the last upstream 2.8 headers in
ref/akebi-gc-2.8/ and the 3.3 headers in src/appdata-33/:

  * il2cpp-functions.h          2.8 file + every DO_APP_FUNC the 3.3 feature code needs that 2.8 never
                                declared, appended with offset 0x0 and a 'RELIC-TODO-28' marker (0x0 resolves
                                to a null pointer at runtime - see il2cpp-init.cpp - so HookManager skips the
                                hook instead of crashing). Known renames are emitted as aliases with the 2.8
                                offset of the old name.
  * il2cpp-types-ptr.h          2.8 file + the 3.3 DO_TYPEDEFs it lacks (0x0 placeholders).
  * il2cpp-api-functions.h      2.8's 3-arg DO_API(r, n, p) + il2cpp-api-functions-ptr.h merged into the
                                single-offset 4-arg shape DO_API(offset, r, n, p) the Relic framework uses.
  * il2cpp-types.h, il2cpp-metadata-version.h, il2cpp-unityplayer-functions.h   copied.

Idempotent. Re-run after editing ref/ or appdata-33; then fill the 0x0 offsets with tools/transplant.py.
"""
import os, re, shutil

HERE = os.path.dirname(os.path.abspath(__file__))
EE = os.path.normpath(os.path.join(HERE, ".."))
REF = os.path.join(EE, "ref", "akebi-gc-2.8", "cheat-library", "src", "appdata")
A33 = os.path.join(EE, "mod", "cheat-library", "src", "appdata-33")
OUT = os.path.join(EE, "mod", "cheat-library", "src", "appdata-28")

# 3.3 name -> 2.8 name of the same function (the call sites are identical in the 2.8 feature code)
ALIASES = {
    "MoleMole_TimeUtil_get_LocalNowMsTimeStamp": "MoleMole_TimeUtil_get_NowTimeStamp",
    "MoleMole_CookingQtePageContext_CloseItemGotPanel": "CookingQtePageContext_CloseItemGotPanel",
}
MARK = "RELIC-TODO-28"

FUNC_RE = re.compile(r'^\s*DO_APP_FUNC\(\s*(0x[0-9A-Fa-f]+)\s*,\s*(.+?),\s*([A-Za-z_]\w*)\s*,\s*(\(.*)$')
MI_RE = re.compile(r'^\s*DO_APP_FUNC_METHODINFO\(\s*(0x[0-9A-Fa-f]+)\s*,\s*([A-Za-z_]\w*)\s*\)')
TD_RE = re.compile(r'^\s*DO_(TYPEDEF|SINGLETONEDEF)\(\s*(0x[0-9A-Fa-f]+)\s*,\s*([A-Za-z_]\w*)\s*\)')

def read(p):
    with open(p, "r", encoding="utf-8", errors="surrogateescape", newline="") as f:
        return f.read()

def write(p, s):
    with open(p, "w", encoding="utf-8", errors="surrogateescape", newline="") as f:
        f.write(s)

def nl_of(s):
    return "\r\n" if "\r\n" in s else "\n"

def active_funcs(text):
    """name -> (offset, full line) for the active (non-comment) DO_APP_FUNC / METHODINFO lines"""
    d = {}
    for ln in text.splitlines():
        if ln.strip().startswith("//"):
            continue
        m = FUNC_RE.match(ln)
        if m:
            d[m.group(3)] = (m.group(1), ln.rstrip("\r\n")); continue
        m = MI_RE.match(ln)
        if m:
            d[m.group(2)] = (m.group(1), ln.rstrip("\r\n"))
    return d

def build_functions():
    t28 = read(os.path.join(REF, "il2cpp-functions.h")); nl = nl_of(t28)
    t33 = read(os.path.join(A33, "il2cpp-functions.h"))
    f28, f33 = active_funcs(t28), active_funcs(t33)
    missing = [n for n in f33 if n not in f28]
    block = [nl, "// ---- Relic backport: declared by the 3.3 feature code, absent from the upstream 2.8 headers ----",
             "// 0x0 = not resolved yet for game 2.8 (becomes a null pointer; HookManager skips such hooks).",
             "// Fill with tools/transplant.py (dump) / Release_WS scanner / manual xref, then drop the marker."]
    n_alias = n_todo = 0
    for n in missing:
        off, line = f33[n]
        if n in ALIASES and ALIASES[n] in f28:
            off28 = f28[ALIASES[n]][0]
            line = line.replace(off, off28, 1) + f"  // 2.8 name: {ALIASES[n]}"
            n_alias += 1
        else:
            line = line.replace(off, "0x0", 1) + f"  // {MARK}"
            n_todo += 1
        block.append(line)
    out = t28.rstrip("\r\n") + nl + nl.join(block) + nl
    write(os.path.join(OUT, "il2cpp-functions.h"), out)
    print(f"il2cpp-functions.h: {len(f28)} from 2.8, +{n_alias} aliases, +{n_todo} placeholders ({MARK})")
    return [n for n in missing if not (n in ALIASES and ALIASES[n] in f28)]

def build_types_ptr():
    t28 = read(os.path.join(REF, "il2cpp-types-ptr.h")); nl = nl_of(t28)
    t33 = read(os.path.join(A33, "il2cpp-types-ptr.h"))
    have = {m.group(3) for m in (TD_RE.match(l) for l in t28.splitlines()) if m}
    add = []
    for ln in t33.splitlines():
        m = TD_RE.match(ln)
        if m and m.group(3) not in have:
            add.append(f"DO_{m.group(1)}(0x0, {m.group(3)});  // {MARK}")
    out = t28.rstrip("\r\n") + nl
    if add:
        out += nl + "// ---- Relic backport: TypeInfo slots the 3.3 code needs, not in the 2.8 headers (0x0 = unresolved) ----" + nl + nl.join(add) + nl
    write(os.path.join(OUT, "il2cpp-types-ptr.h"), out)
    print(f"il2cpp-types-ptr.h: {len(have)} from 2.8, +{len(add)} placeholders")

def build_api():
    t = read(os.path.join(REF, "il2cpp-api-functions.h")); nl = nl_of(t)
    ptr = read(os.path.join(REF, "il2cpp-api-functions-ptr.h"))
    offs = {m.group(1): m.group(2) for m in re.finditer(r'#define\s+(\w+)_ptr\s+(0x[0-9A-Fa-f]+)', ptr)}
    out, n, n0 = [], 0, 0
    for ln in t.splitlines():
        m = re.match(r'^(\s*)DO_API(_NO_RETURN)?\(\s*(.+?),\s*([A-Za-z_]\w*)\s*,\s*(\(.*)$', ln)
        if m and not ln.strip().startswith("//"):
            off = offs.get(m.group(4), "0x0"); n += 1; n0 += off == "0x0"
            ln = f"{m.group(1)}DO_API{m.group(2) or ''}({off}, {m.group(3)}, {m.group(4)}, {m.group(5)}"
        elif "#define DO_API_NO_RETURN(r, n, p) DO_API(r,n,p)" in ln:
            ln = "#define DO_API_NO_RETURN(o, r, n, p) DO_API(o,r,n,p)"
        out.append(ln)
    write(os.path.join(OUT, "il2cpp-api-functions.h"), nl.join(out) + nl)
    print(f"il2cpp-api-functions.h: {n} DO_API converted to single-offset ({n0} without an offset)")

def main():
    os.makedirs(OUT, exist_ok=True)
    for name in ("il2cpp-types.h", "il2cpp-metadata-version.h", "il2cpp-unityplayer-functions.h"):
        shutil.copy2(os.path.join(REF, name), os.path.join(OUT, name)); print(f"{name}: copied from 2.8")
    todo = build_functions()
    build_types_ptr()
    build_api()
    print(f"unresolved for 2.8 ({len(todo)}):")
    for n in todo:
        print("  " + n)

if __name__ == "__main__":
    main()
