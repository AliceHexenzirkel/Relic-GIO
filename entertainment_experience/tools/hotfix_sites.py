#!/usr/bin/env python3
"""hotfix_sites.py - the xLua / InjectFix hotfix checks every method of a UserAssembly build opens with.

    python tools/hotfix_sites.py sites 16 0x017A14A0 0x035AD380 ...   # TypeInfo slot, xLua offset, IFix id per method
    python tools/hotfix_sites.py map 16                              # build dumps_ref/1.6/ifix_ids.json (id -> method)
    python tools/hotfix_sites.py ids 16 2B2D 378D 1829               # name the ids a [atk] hotfix line reported (hex)

Every il2cpp method of this game starts with two hotfix checks before its own body:

    mov rax, [rip + X_TypeInfo] ; mov rax, [rax + 0xA0] ; mov rcx, [rax + OFF] ; test rcx, rcx ; jne <xLua bridge>
    mov edx, ID ; call IFix.WrappersManagerImpl.IsPatched ; test al, al ; jne <InjectFix wrapper>

A method whose DelegateBridge static field (OFF) is set, or whose IFix id is patched, no longer runs the native body
the mod's hooks and offsets were read from. `sites` prints the three numbers RapidFire's 1.6 hotfix probe needs per
method (its kHotfixSites table is exactly this output); `map` scans every caller of IsPatched once (~115k methods on
1.6, a few minutes) and caches id -> method, which is what turns the patched-id list a game session reports into names.
Stdlib + capstone. The DLL comes from %LOCALAPPDATA%\\Relic\\state.json (the launcher's install list).
"""
import argparse, json, os, pickle, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from codexref import PE, CACHE_DIR  # noqa: E402
from dumpnames import load_names  # noqa: E402

try:
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
except ImportError:  # pragma: no cover
    sys.exit("hotfix_sites.py needs capstone: pip install capstone")

# IFix.WrappersManagerImpl.IsPatched per build (dump.cs: `public static bool IsPatched(int id)`)
IS_PATCHED = {"16": 0x01F706B0}


def game_dll(ver):
    st = json.load(open(os.path.expandvars(r"%LOCALAPPDATA%\Relic\state.json"), encoding="utf-8-sig"))
    want = {"16": "1.6", "28": "2.8"}[ver]
    for inst in st.get("Installed", []):
        if inst.get("Id") == want:
            return os.path.join(inst["GameDir"], "GenshinImpact_Data", "Native", "UserAssembly.dll")
    sys.exit(f"no {want} install in state.json")


def prologue(pe, md, rva, is_patched):
    """(typeinfo slot rva, xLua static offset, ifix id) of one method, None where the pattern is absent"""
    code = pe.read(rva, 0x200)
    slot_of = {}      # register -> TypeInfo slot it was loaded from
    statics_of = {}   # register -> TypeInfo slot whose static_fields it holds
    slot = lua = ifix = last_edx = None
    for ins in md.disasm(code, rva):
        s = ins.op_str
        if ins.mnemonic == "mov":
            m = re.match(r"(\w+), qword ptr \[rip \+ (0x[0-9a-f]+)\]$", s)
            if m:
                slot_of[m.group(1)] = ins.address + ins.size + int(m.group(2), 16)
                continue
            m = re.match(r"(\w+), qword ptr \[(\w+) \+ 0xa0\]$", s)
            if m and m.group(2) in slot_of:
                statics_of[m.group(1)] = slot_of[m.group(2)]
                continue
            m = re.match(r"(\w+), qword ptr \[(\w+)(?: \+ (0x[0-9a-f]+|\d+))?\]$", s)
            if m and m.group(2) in statics_of and lua is None:
                lua = int(m.group(3), 0) if m.group(3) else 0
                slot = statics_of[m.group(2)]
            m = re.match(r"edx, (0x[0-9a-f]+|\d+)$", s)
            if m:
                last_edx = int(m.group(1), 0)
        if ins.mnemonic == "call" and s.startswith("0x") and int(s, 16) == is_patched:
            ifix = last_edx
            break
    return slot, lua, ifix


def cmd_sites(ver, rvas):
    pe = PE(game_dll(ver))
    names = load_names(ver)
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    for tok in rvas:
        rva = int(tok, 16)
        slot, lua, ifix = prologue(pe, md, rva, IS_PATCHED[ver])
        fmt = lambda v, w: "?" if v is None else f"0x{v:0{w}X}"
        print(f"0x{rva:08X} {names.get(rva, '?'):42} TypeInfo {fmt(slot, 8)}  xLua +{fmt(lua, 3)}  IFix {fmt(ifix, 4)}")


def cmd_map(ver):
    pe = PE(game_dll(ver))
    names = load_names(ver)
    x = pickle.load(open(os.path.join(CACHE_DIR[ver], "xrefs.pkl"), "rb"))
    isp = IS_PATCHED[ver]
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    idmap = {}
    for f, targets in x.calls.items():
        if isp not in targets:
            continue
        try:
            _, _, ifix = prologue(pe, md, f, isp)
        except Exception:
            continue
        if ifix is not None:
            idmap.setdefault(ifix, []).append([f"0x{f:X}", names.get(f, "?")])
    out = os.path.join(CACHE_DIR[ver], "ifix_ids.json")
    with open(out, "w", encoding="utf-8") as fh:
        json.dump({f"{k:X}": v for k, v in sorted(idmap.items())}, fh, indent=0)
    print(f"{len(idmap)} ids -> {out}")


def cmd_ids(ver, ids):
    path = os.path.join(CACHE_DIR[ver], "ifix_ids.json")
    if not os.path.exists(path):
        sys.exit(f"{path} missing - run: hotfix_sites.py map {ver}")
    idmap = json.load(open(path, encoding="utf-8"))
    for tok in ids:
        key = f"{int(tok, 16):X}"
        hits = idmap.get(key)
        print(f"{key:>6}  " + ("; ".join(f"{n} @{r}" for r, n in hits) if hits else "(no method with this id)"))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("sites"); p.add_argument("ver", choices=("16",)); p.add_argument("rva", nargs="+")
    p = sub.add_parser("map"); p.add_argument("ver", choices=("16",))
    p = sub.add_parser("ids"); p.add_argument("ver", choices=("16",)); p.add_argument("id", nargs="+")
    a = ap.parse_args()
    if a.cmd == "sites":
        cmd_sites(a.ver, a.rva)
    elif a.cmd == "map":
        cmd_map(a.ver)
    else:
        cmd_ids(a.ver, a.id)


if __name__ == "__main__":
    sys.exit(main())
