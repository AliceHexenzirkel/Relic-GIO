#!/usr/bin/env python3
"""native_xref.py - strings, xrefs, callers and annotated disassembly for a NATIVE x64 PE (UnityPlayer.dll).

    python tools/native_xref.py strings <dll> <substring>              # every string holding <substring>, with RVA
    python tools/native_xref.py xref    <dll> <0xRVA | "string">        # functions that reference that RVA (LEA/MOV rip-rel)
    python tools/native_xref.py callers <dll> <0xRVA>                   # direct call/jmp sites into the function at RVA
    python tools/native_xref.py func    <dll> <0xRVA> [--max N]         # annotated disassembly of the function CONTAINING RVA
    python tools/native_xref.py funcs   <dll> <0xRVA> ...               # bounds of the functions containing each RVA

The counterpart of disasm.py / callscan.py for the engine side: UserAssembly has dump.cs, UnityPlayer.dll has
nothing but strings, RTTI and the exception directory. Function bounds come from `.pdata` (RUNTIME_FUNCTION
begin/end - MSVC emits one for every non-leaf function, chained entries are merged by begin address), so a
reference found anywhere in .text can be attributed to a function and that function disassembled whole.
Annotations, per instruction: rip-relative operands that land on a printable string (`str "..."`), on a
function start (`-> func_XXXX`), or on a data slot (`-> data_XXXX`); direct call/jmp targets as `func_XXXX`,
with the export name when there is one. Immediates are printed as capstone prints them - a `cmp eax, 0x400`
next to an overflow string IS the capacity.

Stdlib + capstone + numpy.
"""
import argparse
import bisect
import os
import re
import struct
import sys

try:
    import numpy as np
except ImportError:  # pragma: no cover
    sys.exit("native_xref.py needs numpy")
try:
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP
except ImportError:  # pragma: no cover
    sys.exit("native_xref.py needs capstone: pip install capstone")


class PE:
    def __init__(self, path):
        with open(path, "rb") as f:
            self.b = f.read()
        b = self.b
        if b[:2] != b"MZ":
            raise SystemExit("not a PE file")
        pe = struct.unpack_from("<I", b, 0x3C)[0]
        if b[pe:pe + 4] != b"PE\0\0":
            raise SystemExit("bad PE signature")
        machine, nsec, _, _, _, opt_size, _ = struct.unpack_from("<HHIIIHH", b, pe + 4)
        opt = pe + 24
        magic = struct.unpack_from("<H", b, opt)[0]
        if magic != 0x20B:
            raise SystemExit("not a PE32+ (x64) image")
        self.image_base = struct.unpack_from("<Q", b, opt + 24)[0]
        ndirs = struct.unpack_from("<I", b, opt + 108)[0]
        self.dirs = [struct.unpack_from("<II", b, opt + 112 + 8 * i) for i in range(ndirs)]
        self.sections = []
        sec = opt + opt_size
        for i in range(nsec):
            name = b[sec + 40 * i: sec + 40 * i + 8].rstrip(b"\0").decode("ascii", "replace")
            vsize, vaddr, rsize, raw, _, _, _, _, chars = struct.unpack_from("<IIIIIIHHI", b, sec + 40 * i + 8)
            self.sections.append((name, vsize, vaddr, rsize, raw, chars))
        self.text = [s for s in self.sections if s[5] & 0x20000000]  # IMAGE_SCN_MEM_EXECUTE
        self._funcs = None
        self._exports = None

    def rva_to_off(self, rva):
        for (name, vsize, vaddr, rsize, raw, _) in self.sections:
            if vaddr <= rva < vaddr + max(vsize, rsize):
                off = rva - vaddr + raw
                return off if off < len(self.b) else None
        return None

    def section_of(self, rva):
        for s in self.sections:
            if s[2] <= rva < s[2] + max(s[1], s[3]):
                return s
        return None

    def read_cstr(self, rva, limit=512):
        off = self.rva_to_off(rva)
        if off is None:
            return None
        end = self.b.find(b"\0", off, off + limit)
        if end == -1:
            return None
        raw = self.b[off:end]
        if len(raw) < 3 or any(c < 0x20 or c > 0x7E for c in raw):
            return None
        return raw.decode("ascii")

    # ---- exception directory = function bounds ----
    def functions(self):
        """sorted list of (begin, end) from .pdata, chained entries merged"""
        if self._funcs is not None:
            return self._funcs
        rva, size = self.dirs[3] if len(self.dirs) > 3 else (0, 0)
        out = []
        if rva and size:
            off = self.rva_to_off(rva)
            n = size // 12
            starts = {}
            for i in range(n):
                begin, end, unwind = struct.unpack_from("<III", self.b, off + 12 * i)
                if begin == 0:
                    continue
                # chained unwind info (flag 4 in the first byte's high bits) = a fragment of another function
                uoff = self.rva_to_off(unwind)
                flags = (self.b[uoff] >> 3) if uoff is not None else 0
                if flags & 4:
                    # the parent RUNTIME_FUNCTION follows the unwind codes; merge the range into it
                    ver = self.b[uoff] & 7
                    codes = self.b[uoff + 2]
                    poff = uoff + 4 + ((codes + 1) & ~1) * 2
                    pbegin, pend, _ = struct.unpack_from("<III", self.b, poff)
                    starts.setdefault(pbegin, [pbegin, pend])
                    cur = starts[pbegin]
                    cur[0] = min(cur[0], begin)
                    cur[1] = max(cur[1], end)
                    continue
                cur = starts.setdefault(begin, [begin, end])
                cur[1] = max(cur[1], end)
            out = sorted((b, e) for b, e in starts.values())
        self._funcs = out
        self._starts = [b for b, _ in out]
        self._start_set = set(self._starts)
        return out

    def func_containing(self, rva):
        self.functions()
        i = bisect.bisect_right(self._starts, rva) - 1
        if i < 0:
            return None
        b, e = self._funcs[i]
        return (b, e) if b <= rva < e else None

    def is_func_start(self, rva):
        self.functions()
        return rva in self._start_set

    # ---- exports ----
    def exports(self):
        if self._exports is not None:
            return self._exports
        out = {}
        rva, size = self.dirs[0] if self.dirs else (0, 0)
        if rva and size:
            off = self.rva_to_off(rva)
            (_, _, _, _, _, _, nfunc, nnames, addr_funcs, addr_names, addr_ords) = struct.unpack_from("<IIHHIIIIIII", self.b, off)
            fo, no, oo = self.rva_to_off(addr_funcs), self.rva_to_off(addr_names), self.rva_to_off(addr_ords)
            for i in range(nnames):
                name_rva = struct.unpack_from("<I", self.b, no + 4 * i)[0]
                ordi = struct.unpack_from("<H", self.b, oo + 2 * i)[0]
                frva = struct.unpack_from("<I", self.b, fo + 4 * ordi)[0]
                out[frva] = self.read_cstr(name_rva) or "?"
        self._exports = out
        return out


# ---- scanning -------------------------------------------------------------------------------------------

def text_arrays(pe):
    """per executable section: (vaddr, numpy uint8 array of its raw bytes)"""
    out = []
    for (name, vsize, vaddr, rsize, raw, _) in pe.text:
        arr = np.frombuffer(pe.b, dtype=np.uint8, count=min(rsize, len(pe.b) - raw), offset=raw)
        out.append((vaddr, arr))
    return out


def rip_candidates(pe, target):
    """byte offsets (rva) where a disp32 could be a rip-relative reference to `target`: the disp ends the
    instruction (LEA/MOV/CMP reg), or is followed by an imm8 / imm32 (CMP/MOV/TEST mem, imm)."""
    hits = []
    for vaddr, arr in text_arrays(pe):
        n = len(arr) - 4
        if n <= 0:
            continue
        # overlapping int32 view: disp at every byte offset
        d = arr[0:n].astype(np.int64) | (arr[1:n + 1].astype(np.int64) << 8) | (arr[2:n + 2].astype(np.int64) << 16) | (arr[3:n + 3].astype(np.int64) << 24)
        d = np.where(d >= 1 << 31, d - (1 << 32), d)
        idx = np.arange(n, dtype=np.int64) + vaddr
        for tail in (4, 5, 8):  # instruction end = disp end (+0), +imm8, +imm32
            m = np.nonzero(idx + tail + d == target)[0]
            for i in m:
                hits.append(int(idx[i]))
    return sorted(set(hits))


def call_sites(pe, target):
    """rvas of E8/E9 rel32 instructions whose target is `target`"""
    hits = []
    for vaddr, arr in text_arrays(pe):
        n = len(arr) - 5
        if n <= 0:
            continue
        op = arr[0:n]
        d = arr[1:n + 1].astype(np.int64) | (arr[2:n + 2].astype(np.int64) << 8) | (arr[3:n + 3].astype(np.int64) << 16) | (arr[4:n + 4].astype(np.int64) << 24)
        d = np.where(d >= 1 << 31, d - (1 << 32), d)
        idx = np.arange(n, dtype=np.int64) + vaddr
        m = np.nonzero(((op == 0xE8) | (op == 0xE9)) & (idx + 5 + d == target))[0]
        for i in m:
            hits.append((int(idx[i]), "call" if op[i] == 0xE8 else "jmp"))
    return sorted(set(hits))


def disassembler():
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True
    return md


def annotate(pe, insn):
    notes = []
    for op in insn.operands:
        if op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP:
            tgt = insn.address + insn.size + op.mem.disp
            s = pe.read_cstr(tgt)
            if s is not None:
                notes.append('str "%s"' % s[:90])
            elif pe.is_func_start(tgt):
                notes.append("-> func_%X" % tgt)
            else:
                sec = pe.section_of(tgt)
                notes.append("-> %s_%X" % ((sec[0].strip(".") if sec else "rva"), tgt))
        elif op.type == X86_OP_IMM and insn.mnemonic in ("call", "jmp") :
            tgt = op.imm
            name = pe.exports().get(tgt)
            f = pe.func_containing(tgt)
            if name:
                notes.append("func_%X (%s)" % (tgt, name))
            elif f and f[0] == tgt:
                notes.append("func_%X" % tgt)
            elif f:
                notes.append("into func_%X+%X" % (f[0], tgt - f[0]))
            else:
                notes.append("rva_%X" % tgt)
    return notes


def disasm_func(pe, rva, max_insn=6000, out=sys.stdout):
    f = pe.func_containing(rva)
    if f is None:
        # no .pdata entry (leaf function): sweep from rva until a ret / int3 padding
        begin, end = rva, rva + 0x2000
        out.write("; no .pdata entry contains 0x%X - sweeping from it\n" % rva)
    else:
        begin, end = f
        out.write("; func_%X  [0x%X - 0x%X)  %d bytes%s\n" % (begin, begin, end, end - begin,
                  "  export " + pe.exports()[begin] if begin in pe.exports() else ""))
    off = pe.rva_to_off(begin)
    code = pe.b[off: off + (end - begin)]
    md = disassembler()
    n = 0
    for insn in md.disasm(code, begin):
        notes = annotate(pe, insn)
        mark = "  <==" if insn.address <= rva < insn.address + insn.size and f is not None and begin != rva else ""
        out.write("  %08X  %-8s %-40s%s%s\n" % (insn.address, insn.mnemonic, insn.op_str,
                  ("  ; " + ", ".join(notes)) if notes else "", mark))
        n += 1
        if n >= max_insn:
            out.write("  ... (cap %d)\n" % max_insn)
            break
        if f is None and insn.mnemonic == "ret":
            break


def confirm_ref(pe, site, target):
    """disassemble a little before `site` and return the instruction that actually references `target`"""
    f = pe.func_containing(site)
    begin = f[0] if f else max(site - 64, 0)
    off = pe.rva_to_off(begin)
    code = pe.b[off: off + (site - begin) + 16]
    md = disassembler()
    for insn in md.disasm(code, begin):
        if insn.address > site:
            break
        for op in insn.operands:
            if op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP:
                if insn.address + insn.size + op.mem.disp == target and insn.address <= site < insn.address + insn.size:
                    return insn
    return None


def find_strings(pe, needle):
    out = []
    for m in re.finditer(rb"[\x20-\x7e]{4,300}", pe.b):
        s = m.group().decode()
        if needle in s:
            # file offset -> rva
            for (name, vsize, vaddr, rsize, raw, _) in pe.sections:
                if raw <= m.start() < raw + rsize:
                    out.append((vaddr + m.start() - raw, s))
                    break
    return out


def resolve_target(pe, what):
    if what.lower().startswith("0x"):
        return int(what, 16)
    found = [(r, s) for r, s in find_strings(pe, what) if s == what] or find_strings(pe, what)
    if not found:
        raise SystemExit("string not found: %r" % what)
    if len(found) > 1:
        sys.stderr.write("note: %d strings match, using the first exact/containing one:\n" % len(found))
        for r, s in found[:10]:
            sys.stderr.write("  0x%X %s\n" % (r, s))
    return found[0][0]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cmd", choices=("strings", "xref", "callers", "func", "funcs"))
    ap.add_argument("dll")
    ap.add_argument("what", nargs="+")
    ap.add_argument("--max", type=int, default=6000)
    a = ap.parse_args()
    pe = PE(a.dll)

    if a.cmd == "strings":
        for rva, s in find_strings(pe, " ".join(a.what)):
            print("0x%X  %s" % (rva, s))
        return

    if a.cmd == "funcs":
        for w in a.what:
            rva = int(w, 16)
            f = pe.func_containing(rva)
            print("0x%X -> %s" % (rva, ("func_%X [0x%X-0x%X)" % (f[0], f[0], f[1])) if f else "no .pdata entry"))
        return

    if a.cmd == "func":
        disasm_func(pe, int(a.what[0], 16), a.max)
        return

    if a.cmd == "xref":
        target = resolve_target(pe, " ".join(a.what))
        print("; xrefs to 0x%X  %s" % (target, ('"%s"' % pe.read_cstr(target)) if pe.read_cstr(target) else ""))
        byfunc = {}
        for site in rip_candidates(pe, target):
            insn = confirm_ref(pe, site, target)
            if insn is None:
                continue
            f = pe.func_containing(insn.address)
            key = f[0] if f else None
            byfunc.setdefault(key, []).append(insn)
        for key, insns in sorted(byfunc.items(), key=lambda kv: (kv[0] is None, kv[0] or 0)):
            head = ("func_%X [0x%X-0x%X)" % (key, key, pe.func_containing(key)[1])) if key is not None else "(no .pdata function)"
            print(head)
            for insn in insns:
                print("    %08X  %s %s" % (insn.address, insn.mnemonic, insn.op_str))
        if not byfunc:
            print("(none)")
        return

    if a.cmd == "callers":
        target = int(a.what[0], 16)
        f = pe.func_containing(target)
        print("; callers of 0x%X%s" % (target, (" (func_%X)" % f[0]) if f else ""))
        byfunc = {}
        for site, kind in call_sites(pe, target):
            cf = pe.func_containing(site)
            byfunc.setdefault(cf[0] if cf else None, []).append((site, kind))
        for key, sites in sorted(byfunc.items(), key=lambda kv: (kv[0] is None, kv[0] or 0)):
            head = ("func_%X [0x%X-0x%X)" % (key, key, pe.func_containing(key)[1])) if key is not None else "(no .pdata function)"
            print(head)
            for site, kind in sites:
                print("    %08X  %s" % (site, kind))
        if not byfunc:
            print("(none)")


if __name__ == "__main__":
    main()
