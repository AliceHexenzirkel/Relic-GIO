#!/usr/bin/env python3
"""Checks every appdata-<ver> function declaration against the way the GAME itself calls that address.

Why: a matched RVA can still be called with a different argument layout than the 3.3 declaration says. The
managed signature does not show it — IL2CPP's shared generic code adds hidden parameters, and 1.6 (metadata
v24) passes them differently from 2.8. For example: `Singleton<T>::get_Instance()` is parameterless in
both versions, but the 1.6 shared body takes (rcx = unused/null, rdx = MethodInfo*) while 2.8 takes
(rcx = MethodInfo*). Calling it with the 3.3 signature puts the MethodInfo in rcx, the callee reads garbage
from rdx and the game crashes inside the class initialiser (0xC0000005 writing klass+0x105).

Method: the game passes a MethodInfo* by loading a metadata-usage slot (`mov reg,[rip+slot]`, slots from the
dump's ScriptMetadataMethod list). For every call site of one of our functions we look back a few
instructions for such a load and record which argument register received it (rcx=0, rdx=1, r8=2, r9=3). The
dominant register is the real position of the MethodInfo parameter and is compared with our declaration.
The scan is a heuristic — a load can belong to a neighbouring call — so a verdict needs >=5 samples and
>=70% dominance, and `--control <ver>` suppresses every pattern that behaves the same way in a version known
to work (2.8), leaving only the genuine per-version differences.

  verify_abi.py --ver 16 --control 28
  verify_abi.py --ver 28 [--assembly <UserAssembly.dll>] [--dump <dumps_ref/2.8>]
"""
import json, os, re, struct, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
EE = os.path.normpath(os.path.join(HERE, ".."))
DEFAULTS = {
    "16": (r"D:\relic\1.6_game\GenshinImpact_Data\Native\UserAssembly.dll", os.path.join(EE, "dumps_ref", "1.6", "greenxemotion")),
    "28": (r"D:\relic\Genshin 2.8\GenshinImpact_Data\Native\UserAssembly.dll", os.path.join(EE, "dumps_ref", "2.8")),
}
REGS = {0: "rcx", 1: "rdx", 2: "r8", 3: "r9"}
CALL = bytes([0xE8])
MOVS = {b"\x48\x8b\x0d": 0, b"\x48\x8b\x15": 1, b"\x4c\x8b\x05": 2, b"\x4c\x8b\x0d": 3}

def regname(i):
    return REGS.get(i, "arg#%d (stack)" % i)

def pe_sections(path):
    b = open(path, "rb").read()
    pe = struct.unpack_from("<I", b, 0x3C)[0]
    nsec = struct.unpack_from("<H", b, pe + 6)[0]
    opt = struct.unpack_from("<H", b, pe + 20)[0]
    off = pe + 24 + opt
    secs = []
    for i in range(nsec):
        name = b[off + 40 * i:off + 40 * i + 8].rstrip(b"\0").decode()
        vs, va, rs, ro = struct.unpack_from("<IIII", b, off + 40 * i + 8)
        secs.append((name, va, vs, ro, rs))
    return b, secs

def parse_decls(path):
    out = {}
    for i, ln in enumerate(open(path, encoding="utf-8", errors="replace"), 1):
        s = ln.strip()
        if s.startswith("//") or not s.startswith("DO_APP_FUNC("):
            continue
        m = re.match(r"DO_APP_FUNC\(\s*(0x[0-9A-Fa-f]+)\s*,\s*(.+?),\s*([A-Za-z_]\w*)\s*,\s*\((.*?)\)\s*\);", s)
        if not m:
            continue
        rva = int(m.group(1), 16)
        if rva == 0:
            continue
        params = [p.strip() for p in re.split(r",(?![^()]*\))", m.group(4))] if m.group(4).strip() else []
        idx = None
        for k, p in enumerate(params):
            if re.search(r"\bMethodInfo\s*\*", p):
                idx = k
                break
        out[m.group(3)] = (rva, idx, len(params), i)
    return out

def analyse(ver, asm=None, dumpdir=None):
    d_asm, d_dump = DEFAULTS[ver]
    asm = asm or d_asm
    dumpdir = dumpdir or d_dump
    hdr = os.path.join(EE, "mod", "cheat-library", "src", "appdata-" + ver, "il2cpp-functions.h")
    decls = parse_decls(hdr)
    d = json.load(open(os.path.join(dumpdir, "script.json"), encoding="utf-8"))
    slots = set(e["Address"] for e in d.get("ScriptMetadataMethod", []))
    b, secs = pe_sections(asm)
    targets = set(v[0] for v in decls.values())
    hits = collections.defaultdict(collections.Counter)
    calls = collections.Counter()
    for sname, va, vs, ro, rs in secs:
        if sname not in (".text", "il2cpp"):
            continue
        code = b[ro:ro + rs]
        i, n = 0, len(code)
        while True:
            j = code.find(CALL, i)
            if j < 0 or j + 5 > n:
                break
            i = j + 1
            rel = struct.unpack_from("<i", code, j + 1)[0]
            tgt = va + j + 5 + rel
            if tgt not in targets:
                continue
            calls[tgt] += 1
            lo = max(0, j - 0x40)
            win = code[lo:j]
            for enc, reg in MOVS.items():
                k = win.rfind(enc)
                while k >= 0:
                    if k + 7 <= len(win):
                        r2 = struct.unpack_from("<i", win, k + 3)[0]
                        if va + lo + k + 7 + r2 in slots:
                            hits[tgt][reg] += 1
                            break
                    k = win.rfind(enc, 0, k)
    print("appdata-%s: %d resolved declarations, %d metadata slots, %d call sites seen"
          % (ver, len(decls), len(slots), sum(calls.values())))
    return decls, hits, calls

def verdict(idx, h):
    if not h or idx is None or idx > 3:
        return None
    total = sum(h.values())
    reg, cnt = h.most_common(1)[0]
    if total < 5 or cnt * 10 < total * 7:
        return None
    return True if reg == idx else reg

def main():
    a = sys.argv[1:]
    if "--ver" not in a:
        print(__doc__)
        return 2
    ver = a[a.index("--ver") + 1]
    control = a[a.index("--control") + 1] if "--control" in a else None
    asm = a[a.index("--assembly") + 1] if "--assembly" in a else None
    dumpdir = a[a.index("--dump") + 1] if "--dump" in a else None
    decls, hits, calls = analyse(ver, asm, dumpdir)
    cdecls = chits = None
    if control:
        cdecls, chits, _ = analyse(control)
        print("(control appdata-%s: a shift seen there too is an artefact of the scan, not a real difference)" % control)
    bad = ok = noev = 0
    lines = []
    for name in sorted(decls, key=lambda n: decls[n][3]):
        rva, idx, nparams, line = decls[name]
        v = verdict(idx, hits.get(rva))
        if v is None:
            noev += 1
            continue
        if v is True:
            ok += 1
            continue
        if cdecls and name in cdecls:
            cv = verdict(cdecls[name][1], chits.get(cdecls[name][0]))
            if cv is not None and cv is not True:
                noev += 1
                continue
        bad += 1
        ev = dict((regname(k), c) for k, c in hits[rva].items())
        lines.append("  MISMATCH  %s  (line %d, 0x%08X)  declared as argument #%d (%s), the game passes the MethodInfo as #%d (%s)   evidence=%s"
                     % (name, line, rva, idx, regname(idx), v, regname(v), ev))
    print("")
    print("verified: %d agree, %d MISMATCH, %d inconclusive" % (ok, bad, noev))
    for l in lines:
        print(l)
    return 1 if bad else 0

if __name__ == "__main__":
    sys.exit(main())
