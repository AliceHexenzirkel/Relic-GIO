#!/usr/bin/env python3
"""Poor man's crash triage for CLibrary.dll without WinDbg: reads a Windows minidump (%LOCALAPPDATA%\\CrashDumps\\
GenshinImpact.exe.<pid>.dmp, or the WER .dmp), prints the exception record, the faulting thread's registers and a
return-address scan of its stack — every 8-byte value that points into CLibrary.dll (or any other module) is printed
with the nearest symbol from the linker map of the matching build (mod/bin/v<ver>/Release-x64/CLibrary.map). It is a
stack SCAN (like WinDbg's `dps rsp`), not a true unwind: stale frames may show up, but the chain from the crash site
to the feature that caused it is normally obvious.

  minidump_stackscan.py <dump.dmp> [--map <CLibrary.map>] [--ver 28] [--max 60]
"""
import os, re, struct, sys

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.normpath(os.path.join(HERE, "..", "mod"))

STREAMS = {3: "ThreadList", 4: "ModuleList", 5: "MemoryList", 6: "Exception", 7: "SystemInfo", 9: "Memory64List"}

def u32(b, o): return struct.unpack_from("<I", b, o)[0]
def u64(b, o): return struct.unpack_from("<Q", b, o)[0]

def read_string(b, rva):
    n = u32(b, rva)
    return b[rva + 4: rva + 4 + n].decode("utf-16-le", errors="replace")

def load_map(path):
    syms = []
    for ln in open(path, encoding="utf-8", errors="replace"):
        m = re.match(r"\s*(\d{4}):([0-9A-Fa-f]{8})\s+(\S+)\s+([0-9A-Fa-f]{16})\s+(?:f\s+)?(?:i\s+)?(\S+)?", ln)
        if m:
            syms.append((int(m.group(4), 16) - 0x180000000, m.group(3), m.group(5) or ""))
    syms.sort()
    return syms

def nearest(syms, rva):
    lo, hi = 0, len(syms)
    while lo < hi:
        mid = (lo + hi) // 2
        if syms[mid][0] <= rva: lo = mid + 1
        else: hi = mid
    if lo == 0: return None
    return syms[lo - 1]

def undecorate(name):
    # cheap readability for MSVC-decorated names: ?Func@Class@ns@@... -> ns::Class::Func
    m = re.match(r"\?([^@]+)@([^@]*)@?([^@]*)@?", name)
    if not m or not name.startswith("?"):
        return name
    parts = [p for p in (m.group(3), m.group(2), m.group(1)) if p]
    return "::".join(parts)

def main():
    a = sys.argv[1:]
    if not a or a[0].startswith("--"):
        print(__doc__); return 2
    dump = a[0]
    ver = a[a.index("--ver") + 1] if "--ver" in a else "28"
    mappath = a[a.index("--map") + 1] if "--map" in a else os.path.join(MOD, "bin", f"v{ver}", "Release-x64", "CLibrary.map")
    maxn = int(a[a.index("--max") + 1]) if "--max" in a else 60
    b = open(dump, "rb").read()
    assert b[:4] == b"MDMP", "not a minidump"
    nstreams, dirrva = u32(b, 8), u32(b, 12)
    streams = {}
    for i in range(nstreams):
        t, size, rva = struct.unpack_from("<III", b, dirrva + 12 * i)
        streams.setdefault(t, []).append((size, rva))
    # modules
    mods = []
    size, rva = streams[4][0]
    n = u32(b, rva)
    for i in range(n):
        o = rva + 4 + 108 * i
        base, img = u64(b, o), u32(b, o + 8)
        name = read_string(b, u32(b, o + 20))
        mods.append((base, img, name))
    def modof(addr):
        for base, img, name in mods:
            if base <= addr < base + img:
                return name, addr - base
        return None, None
    syms = load_map(mappath) if os.path.exists(mappath) else []
    cl = [m for m in mods if m[2].lower().endswith("clibrary.dll")]
    print(f"dump: {dump}\nmodules: {len(mods)}; CLibrary.dll: {[hex(m[0]) for m in cl]}  map: {mappath if syms else '(none)'}")
    # exception
    exc = streams.get(6)
    if exc:
        size, rva = exc[0]
        tid = u32(b, rva); code = u32(b, rva + 8); addr = u64(b, rva + 24); nparams = u32(b, rva + 32)
        params = [u64(b, rva + 40 + 8 * i) for i in range(min(nparams, 15))]
        mname, moff = modof(addr)
        print(f"exception: thread {tid}  code 0x{code:08X}  address 0x{addr:X} = {mname}+0x{moff:X}" if mname else f"exception: thread {tid} code 0x{code:08X} address 0x{addr:X}")
        if params: print("  params:", ", ".join(hex(p) for p in params))
        ctx_size, ctx_rva = u32(b, rva + 40 + 8 * 15), u32(b, rva + 40 + 8 * 15 + 4)
        ctx = b[ctx_rva: ctx_rva + ctx_size]
        rsp, rip = u64(ctx, 0x98), u64(ctx, 0xF8)
        rbp = u64(ctx, 0xA0)
        m2, o2 = modof(rip)
        print(f"  rip 0x{rip:X} = {m2}+0x{o2:X}  rsp 0x{rsp:X}  rbp 0x{rbp:X}")
        if m2 and m2.lower().endswith("clibrary.dll") and syms:
            s = nearest(syms, o2); print(f"  rip symbol: {undecorate(s[1])}  ({s[2]}) +0x{o2 - s[0]:X}")
    else:
        tid = None; rsp = None
    # faulting thread stack
    size, rva = streams[3][0]
    n = u32(b, rva)
    for i in range(n):
        o = rva + 4 + 48 * i
        t = u32(b, o)
        if tid is not None and t != tid: continue
        sstart, ssize, srva = u64(b, o + 24), u32(b, o + 32), u32(b, o + 36)  # MINIDUMP_THREAD: Teb @16, Stack.StartOfMemoryRange @24, Memory{DataSize,Rva} @32/36
        stack = b[srva: srva + ssize]
        print(f"thread {t}: stack 0x{sstart:X}..0x{sstart + ssize:X} ({ssize} bytes)")
        start = max(0, (rsp - sstart)) if rsp else 0
        start -= start % 8
        printed = 0
        for off in range(start, len(stack) - 7, 8):
            v = u64(stack, off)
            mname, moff = modof(v)
            if not mname: continue
            short = os.path.basename(mname)
            if short.lower() == "clibrary.dll" and syms:
                s = nearest(syms, moff)
                label = f"{undecorate(s[1])} +0x{moff - s[0]:X}  [{s[2]}]"
            else:
                label = ""
            print(f"  [rsp+0x{off - start:05X}] 0x{v:016X}  {short}+0x{moff:X}  {label}")
            printed += 1
            if printed >= maxn: break
        break
    return 0

if __name__ == "__main__":
    sys.exit(main())
