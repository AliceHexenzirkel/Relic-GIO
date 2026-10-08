#!/usr/bin/env python3
"""callers.py - who calls a method (direct E8/E9 sites), from the cached codexref xrefs.

    python tools/callers.py 16 0x103FD20 [0x... ...]        # by RVA
    python tools/callers.py 28 SECTR_StreamingProfile.GetLodRatio --depth 2
    python tools/callers.py 16 MiHoYoVegetationManager.SetRuntimeQualityLevel --strings

Prints the callers up to --depth levels (default 1, callers of callers with 2). Indirect calls (virtual /
delegate / interface dispatch) are invisible to this - a method with 0 callers here is usually called
virtually; look at its class's vtable slot in dump.cs. --strings also lists the string literals and
metadata (TypeInfo / MethodInfo) each caller references, which is how an obfuscated caller is identified.
"""
import argparse, os, pickle, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from codexref import CACHE_DIR  # noqa: E402
from dumpnames import load_names, resolve  # noqa: E402


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("ver", choices=("16", "28"))
    ap.add_argument("what", nargs="+", help="RVAs (0x...) or Class.Method names")
    ap.add_argument("--depth", type=int, default=1)
    ap.add_argument("--strings", action="store_true", help="list the literals / metadata each caller references")
    ap.add_argument("--limit", type=int, default=40, help="callers shown per level")
    a = ap.parse_args()

    names = load_names(a.ver)
    with open(os.path.join(CACHE_DIR[a.ver], "xrefs.pkl"), "rb") as f:
        x = pickle.load(f)
    rev = {}
    for src, targets in x.calls.items():
        for t in targets:
            rev.setdefault(t, set()).add(src)

    def refs(rva):
        out = x.strings_of(rva)[:8]
        out += [n for _, n in x.meta_of(rva)][:8]
        return out

    def show(rva, depth, seen):
        callers = sorted(rev.get(rva, ()))
        pad = "  " * depth
        print(f"{pad}0x{rva:X} {names.get(rva, '?')}  callers={len(callers)}")
        if a.strings and depth == 0:
            for r in refs(rva):
                print(f"{pad}    ref {r}")
        if depth >= a.depth:
            return
        for c in callers[:a.limit]:
            if c in seen:
                print(f"{pad}  <- 0x{c:X} {names.get(c, '?')} (seen)")
                continue
            seen.add(c)
            show(c, depth + 1, seen)
            if a.strings:
                for r in refs(c):
                    print(f"{pad}      ref {r}")
        if len(callers) > a.limit:
            print(f"{pad}  ... +{len(callers) - a.limit} more")

    for w in a.what:
        show(resolve(names, w), 0, set())
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
