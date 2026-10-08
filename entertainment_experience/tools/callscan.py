#!/usr/bin/env python3
"""callscan.py - list the calls a UserAssembly method makes, BY NAME, straight from the shipped binary.

    python tools/callscan.py 28 miHoYoCamera.CameraStateMgr.Flush --depth 2
    python tools/callscan.py 16 0x067F2160
    python tools/callscan.py 28 --callers miHoYoCamera.CameraStateMgr.Flush      # whole-binary xref scan (slow-ish)

Linear-sweep capstone disassembly of one method - from its dump.cs RVA up to the next method's RVA - printing every
direct `call` / tail `jmp` target resolved through dump.cs, and every indirect call. `--depth N` follows the direct
targets that live in the same namespace as the root method. Targets that write a Transform or a Camera are flagged
[WRITE]: this is the tool that proves which method is the frame's LAST writer of the camera transform, i.e. where a
postfix hook may safely overwrite the camera pose (see FreeCamera.cpp, "drive the game camera").

Stdlib + capstone (`pip install capstone`). The dump dirs come from codexref.DEFAULT_DUMP_DIR; the DLL comes from
`--dll`, else from %LOCALAPPDATA%\\Relic\\state.json (the launcher's install list), else from RELIC_EE_GAME_<ver>.
"""
import argparse
import json
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from codexref import DEFAULT_DUMP_DIR  # noqa: E402

try:
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    from capstone.x86 import X86_OP_IMM
except ImportError:  # pragma: no cover
    sys.exit("callscan.py needs capstone: pip install capstone")

RVA_RE = re.compile(r"^(\|-)?RVA: (0x[0-9A-Fa-f]+)")
NS_RE = re.compile(r"^// Namespace: ?(.*)$")
TYPE_RE = re.compile(r"^(?:public |internal |private |protected )?(?:sealed |static |abstract |readonly )*"
                     r"(class|struct|interface|enum) ([^ :/]+)")
METHOD_RE = re.compile(r"([~\w.`<>]+)\s*\(")

# Anything that moves a Transform or re-configures a Camera counts as a camera write.
WRITE_NAMES = {
    "UnityEngine.Transform." + m for m in (
        "set_position", "set_localPosition", "set_rotation", "set_localRotation", "set_eulerAngles",
        "set_localEulerAngles", "set_forward", "set_up", "set_right", "SetPositionAndRotation", "LookAt",
        "Rotate", "Translate", "set_parent", "SetParent", "set_localScale")
} | {
    "UnityEngine.Camera." + m for m in (
        "set_fieldOfView", "set_nearClipPlane", "set_farClipPlane", "set_orthographicSize", "set_rect",
        "set_targetTexture", "CopyFrom", "set_enabled", "set_cullingMask")
}


def parse_dump(path):
    """rva -> 'Namespace.Class.Method' for every method (and generic instance) in dump.cs."""
    names = {}
    ns = ""
    cls = ""
    pending = None
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            s = line.strip()
            if not s:
                continue
            m = NS_RE.match(s)
            if m:
                ns = m.group(1).strip()
                continue
            m = TYPE_RE.match(s)
            if m and "TypeDefIndex" in s:
                cls = (ns + "." if ns else "") + m.group(2)
                pending = None
                continue
            if s.startswith("["):          # attribute line - its RVA is the attribute generator, not a method
                continue
            if s.startswith("// RVA:") or s.startswith("|-RVA:"):
                m = RVA_RE.match(s.lstrip("/ "))
                if m:
                    rva = int(m.group(2), 16)
                    if rva > 0:
                        if m.group(1):          # generic instance of the pending method
                            if pending:
                                names.setdefault(rva, pending + "<inst>")
                        else:
                            pending = ("__rva__", rva)
                continue
            if isinstance(pending, tuple) and "(" in s and not s.startswith("//"):
                m = METHOD_RE.search(s)
                if m:
                    full = cls + "." + m.group(1)
                    names.setdefault(pending[1], full)
                    pending = full
                    continue
            if isinstance(pending, tuple):
                pending = None
    return names


def pe_sections(data):
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    if data[e_lfanew:e_lfanew + 4] != b"PE\0\0":
        raise ValueError("not a PE file")
    nsec = struct.unpack_from("<H", data, e_lfanew + 6)[0]
    opt_size = struct.unpack_from("<H", data, e_lfanew + 20)[0]
    sec_off = e_lfanew + 24 + opt_size
    secs = []
    for i in range(nsec):
        off = sec_off + i * 40
        vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, off + 8)
        secs.append((vaddr, vsize, rawptr, rawsize))
    return secs


def rva_to_off(secs, rva):
    for va, vs, rp, rs in secs:
        if va <= rva < va + max(vs, rs):
            return rva - va + rp
    return None


def resolve_dll(ver, explicit):
    if explicit:
        return explicit
    env = os.environ.get("RELIC_EE_GAME_" + ver)
    if env:
        return os.path.join(env, "GenshinImpact_Data", "Native", "UserAssembly.dll")
    state = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Relic", "state.json")
    want = {"16": "1.6", "28": "2.8"}[ver]
    try:
        with open(state, encoding="utf-8") as f:
            for inst in json.load(f).get("Installed", []):
                if inst.get("Id") == want:
                    return os.path.join(inst["GameDir"], "GenshinImpact_Data", "Native", "UserAssembly.dll")
    except (OSError, ValueError, KeyError):
        pass
    sys.exit(f"cannot find the {want} game folder - pass --dll <UserAssembly.dll> or set RELIC_EE_GAME_{ver}")


class Scanner:
    def __init__(self, data, secs, names):
        self.data = data
        self.secs = secs
        self.names = names
        self.by_name = {}
        for rva, n in names.items():
            self.by_name.setdefault(n, rva)
        self.sorted_rvas = sorted(names)
        self.md = Cs(CS_ARCH_X86, CS_MODE_64)
        self.md.detail = True

    def method_end(self, rva, cap=0x6000):
        import bisect
        i = bisect.bisect_right(self.sorted_rvas, rva)
        nxt = self.sorted_rvas[i] if i < len(self.sorted_rvas) else rva + cap
        return min(nxt, rva + cap)

    def calls(self, rva):
        """[(offset_in_method, kind, target_rva_or_None, text)] for one method."""
        start, end = rva, self.method_end(rva)
        off = rva_to_off(self.secs, start)
        if off is None:
            raise ValueError(f"RVA 0x{rva:X} is not inside a section")
        code = self.data[off:off + (end - start)]
        out = []
        for insn in self.md.disasm(code, start):
            if insn.mnemonic not in ("call", "jmp"):
                continue
            ops = insn.operands
            if ops and ops[0].type == X86_OP_IMM:
                tgt = ops[0].imm
                if insn.mnemonic == "jmp" and start <= tgt < end:
                    continue                      # a local branch, not a tail call
                out.append((insn.address - start, insn.mnemonic, tgt, insn.op_str))
            else:
                out.append((insn.address - start, insn.mnemonic + " (indirect)", None, insn.op_str))
        return out

    def name(self, rva):
        return self.names.get(rva, f"0x{rva:X}")

    def dump(self, rva, depth, seen=None, indent=""):
        seen = seen if seen is not None else set()
        seen.add(rva)
        root_ns = self.name(rva).rsplit(".", 2)[0]
        print(f"{indent}{self.name(rva)} @0x{rva:X}  [0x{rva:X}..0x{self.method_end(rva):X})")
        for off, kind, tgt, text in self.calls(rva):
            if tgt is None:
                print(f"{indent}  +0x{off:04X}: {kind:16} {text}")
                continue
            n = self.name(tgt)
            flag = "  [WRITE]" if n in WRITE_NAMES else ""
            print(f"{indent}  +0x{off:04X}: {kind:16} {n}{flag}")
            if depth > 0 and tgt not in seen and n.startswith(root_ns + ".") and n not in WRITE_NAMES:
                self.dump(tgt, depth - 1, seen, indent + "    ")

    def callers(self, target):
        """Whole-binary scan for direct calls/jumps to `target` (rel32 only). Returns [(caller_rva, name)]."""
        import numpy as np
        found = []
        for va, vs, rp, rs in self.secs:
            if rs < 6:
                continue
            arr = np.frombuffer(self.data, dtype=np.uint8, count=rs, offset=rp)
            pos = np.flatnonzero((arr[:-5] == 0xE8) | (arr[:-5] == 0xE9))
            if pos.size == 0:
                continue
            # rel32 = target - (insn_addr + 5) for call/jmp E8/E9 - vectorised, the .text is ~170 MB
            rel = (arr[pos + 1].astype(np.uint32) | (arr[pos + 2].astype(np.uint32) << 8)
                   | (arr[pos + 3].astype(np.uint32) << 16) | (arr[pos + 4].astype(np.uint32) << 24)).astype(np.int32)
            dst = (va + pos + 5).astype(np.int64) + rel.astype(np.int64)
            for p in pos[dst == target]:
                caller = va + int(p)
                found.append((caller, self.owner(caller)))
        return found

    def owner(self, rva):
        import bisect
        i = bisect.bisect_right(self.sorted_rvas, rva) - 1
        if i < 0:
            return "?"
        start = self.sorted_rvas[i]
        return f"{self.names[start]}+0x{rva - start:X}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ver", choices=("16", "28"))
    ap.add_argument("method", help="Namespace.Class.Method or 0xRVA")
    ap.add_argument("--depth", type=int, default=1)
    ap.add_argument("--dll")
    ap.add_argument("--callers", action="store_true", help="find who calls the method instead")
    a = ap.parse_args()

    dump = os.path.join(DEFAULT_DUMP_DIR[a.ver], "dump.cs")
    names = parse_dump(dump)
    dll = resolve_dll(a.ver, a.dll)
    with open(dll, "rb") as f:
        data = f.read()
    sc = Scanner(data, pe_sections(data), names)

    if a.method.lower().startswith("0x"):
        rva = int(a.method, 16)
    else:
        rva = sc.by_name.get(a.method)
        if rva is None:
            cands = [n for n in sc.by_name if n.endswith("." + a.method.rsplit(".", 1)[-1])
                     and a.method.rsplit(".", 1)[0] in n][:10]
            sys.exit(f"unknown method {a.method}; near misses: {cands}")

    print(f"# {os.path.basename(dll)}  dump={dump}  methods={len(names)}")
    if a.callers:
        for caller, owner in sc.callers(rva):
            print(f"  0x{caller:X}  {owner}")
        return
    sc.dump(rva, a.depth)


if __name__ == "__main__":
    main()
