#!/usr/bin/env python3
"""Reports which appdata entries of a game version are still unresolved (offset 0x0) and which feature
files reference them, so the port can be driven symbol by symbol.

Usage: unresolved_report.py <16|28|33>
"""
import os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.normpath(os.path.join(HERE, "..", "mod"))
USER = os.path.join(MOD, "cheat-library", "src", "user")
FRAMEWORK = os.path.join(MOD, "cheat-library", "src", "framework")

def main():
    if len(sys.argv) != 2:
        print(__doc__); sys.exit(2)
    ver = sys.argv[1]
    appdata = os.path.join(MOD, "cheat-library", "src", f"appdata-{ver}")
    unresolved = []
    for fn in ("il2cpp-functions.h", "il2cpp-types-ptr.h", "il2cpp-unityplayer-functions.h", "il2cpp-api-functions.h"):
        p = os.path.join(appdata, fn)
        if not os.path.exists(p):
            continue
        for ln in open(p, encoding="utf-8", errors="replace"):
            s = ln.strip()
            if s.startswith("//"):
                continue
            m = re.match(r'DO_(APP_FUNC_METHODINFO|APP_FUNC|TYPEDEF|SINGLETONEDEF|API_NO_RETURN|API)\(\s*(0x0+|0)\s*,\s*(?:(?:[^,]+),\s*)?([A-Za-z_]\w*)', s)
            if m:
                unresolved.append((m.group(1), m.group(3), fn))
    # who references them
    sources = {}
    for root, _, files in os.walk(USER):
        for f in files:
            if f.endswith((".cpp", ".h")):
                p = os.path.join(root, f)
                sources[os.path.relpath(p, USER)] = open(p, encoding="utf-8", errors="replace").read()
    api_used = {n for (k, n, _) in unresolved if k.startswith("API")}
    print(f"appdata-{ver}: {len(unresolved)} unresolved entries\n")
    by_feature = {}
    for kind, name, fn in unresolved:
        refs = [s for s, txt in sources.items() if re.search(r'\b' + re.escape(name) + r'\b', txt)]
        if kind.startswith("API") and not refs:
            continue  # the 200+ unused il2cpp API entries are noise
        print(f"  {kind:<20} {name:<70} {fn}")
        for r in refs:
            print(f"      used by {r}")
            by_feature.setdefault(r, []).append(name)
    if by_feature:
        print("\nfeatures blocked (file -> unresolved symbols):")
        for f, names in sorted(by_feature.items()):
            print(f"  {f}: {', '.join(names)}")

if __name__ == "__main__":
    main()
