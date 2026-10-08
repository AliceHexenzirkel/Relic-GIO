#!/usr/bin/env python3
"""disasm.py - every instruction of one UserAssembly method, annotated, straight from the shipped binary.

    python tools/disasm.py 16 0x29664E0
    python tools/disasm.py 28 MEOCOPFMBIL.POFANHJLEHA
    python tools/disasm.py 16 SECTR_StreamingProfile.GetLodRatio --dll <UserAssembly.dll>
    python tools/disasm.py 16 0x29664E0 --end 0x2966600     # an explicit range instead of "to the next method"

Where callscan.py lists only the calls a method makes, this prints the whole linear sweep - so a reader can
see which FIELD OFFSETS a function touches (`mov [rcx+0x20], eax` = SECTR_LayerConfig.loadSize) and what it
compares against. Annotations, per instruction:
  * direct call / jmp targets are named through dump.cs (the dumpnames.py cache);
  * RIP-relative memory operands are resolved against the metadata-usage slots recorded in the cached
    codexref xrefs (dumps_ref/<ver>/xrefs.pkl): string literals, `X_TypeInfo`, `Method$X` - so a
    `mov rcx,[rip+...]` reads as the class or literal it loads;
  * `[reg+disp]` operands are left as-is: the displacement IS the field offset to look up in dump.cs.
Stdlib + capstone. The DLL comes from --dll, else %LOCALAPPDATA%\\Relic\\state.json, else RELIC_EE_GAME_<ver>.
"""
import argparse, os, pickle, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from codexref import PE, CACHE_DIR  # noqa: E402
from dumpnames import load_names, resolve  # noqa: E402
import callscan  # noqa: E402

try:
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP
except ImportError:  # pragma: no cover
    sys.exit("disasm.py needs capstone: pip install capstone")


def load_slots(ver):
    """slot rva -> text, from the cached xrefs (empty when the cache is missing)"""
    p = os.path.join(CACHE_DIR[ver], "xrefs.pkl")
    try:
        with open(p, "rb") as f:
            x = pickle.load(f)
    except (OSError, pickle.PickleError):
        return {}
    slots = {}
    for k, v in x.strings.items():
        slots[k] = "str " + repr(v)[:80]
    for k, v in x.meta.items():
        slots[k] = v
    for k, v in x.metamethod.items():
        slots[k] = v
    return slots


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("ver", choices=("16", "28"))
    ap.add_argument("what", help="RVA (0x...) or Class.Method (namespace optional)")
    ap.add_argument("--dll")
    ap.add_argument("--end", help="stop RVA (default: the next method start)")
    ap.add_argument("--max", type=int, default=4000, help="instruction cap")
    a = ap.parse_args()

    names = load_names(a.ver)
    rva = resolve(names, a.what)
    pe = PE(callscan.resolve_dll(a.ver, a.dll))
    sorted_rvas = sorted(names)
    import bisect
    i = bisect.bisect_right(sorted_rvas, rva)
    end = int(a.end, 16) if a.end else min(sorted_rvas[i] if i < len(sorted_rvas) else rva + 0x6000, rva + 0x6000)
    code = pe.read(rva, end - rva)
    if not code:
        sys.exit(f"RVA 0x{rva:X} is not inside a section of {pe.path}")
    slots = load_slots(a.ver)

    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True
    print(f"# {names.get(rva, '?')} @0x{rva:X}  [0x{rva:X}..0x{end:X})  {pe.path}")
    n = 0
    for insn in md.disasm(code, rva):
        n += 1
        if n > a.max:
            print("# ... capped")
            break
        note = ""
        for op in insn.operands:
            if op.type == X86_OP_IMM and insn.mnemonic in ("call", "jmp") or (insn.mnemonic.startswith("j") and op.type == X86_OP_IMM):
                tgt = op.imm
                if tgt in names:
                    note = "-> " + names[tgt]
                elif not (rva <= tgt < end):
                    note = f"-> 0x{tgt:X}"
            elif op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP:
                slot = insn.address + insn.size + op.mem.disp
                if slot in slots:
                    note = "; " + slots[slot]
                elif slot in names:
                    note = "; &" + names[slot]
                else:
                    note = f"; [0x{slot:X}]"
        print(f"  +0x{insn.address - rva:04X}  {insn.mnemonic:8} {insn.op_str:44} {note}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
