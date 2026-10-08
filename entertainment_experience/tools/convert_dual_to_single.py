#!/usr/bin/env python3
"""Convert Akebi's 3.3-era dual-offset appdata headers (OS_OFFSET, CN_OFFSET, ...) into the single-offset
shape Relic builds with (OFFSET, ...). Keeps column 1 = the global (OS) offset; Relic ships OS clients only.

Usage: convert_dual_to_single.py <src appdata dir> <dst appdata dir>
Every other line (comments, #pragma, types) is copied verbatim. Prints per-file conversion counts.
"""
import os, re, sys, shutil

MACROS = ("DO_API", "DO_APP_FUNC_METHODINFO", "DO_APP_FUNC", "DO_TYPEDEF", "DO_SINGLETONEDEF")
# (//)?MACRO( <hex> , <hex> ,   ->  (//)?MACRO( <hex> ,
PAT = re.compile(r"^(\s*(?://\s*)?)(" + "|".join(MACROS) + r")\(\s*(0x[0-9A-Fa-f]+|0)\s*,\s*(0x[0-9A-Fa-f]+|0)\s*,")

def convert_file(src, dst):
    n = 0
    out = []
    with open(src, "r", encoding="utf-8", errors="surrogateescape", newline="") as f:
        for line in f:
            m = PAT.match(line)
            if m:
                line = f"{m.group(1)}{m.group(2)}({m.group(3)}," + line[m.end():]
                n += 1
            out.append(line)
    with open(dst, "w", encoding="utf-8", errors="surrogateescape", newline="") as f:
        f.writelines(out)
    return n

def main():
    if len(sys.argv) != 3:
        print(__doc__); sys.exit(2)
    srcdir, dstdir = sys.argv[1], sys.argv[2]
    os.makedirs(dstdir, exist_ok=True)
    total = 0
    for name in sorted(os.listdir(srcdir)):
        s = os.path.join(srcdir, name); d = os.path.join(dstdir, name)
        if not os.path.isfile(s):
            continue
        if name.endswith(".h") and name.startswith("il2cpp-") and name not in ("il2cpp-types.h", "il2cpp-metadata-version.h"):
            n = convert_file(s, d); total += n
            print(f"  {name}: {n} macro lines converted")
        else:
            shutil.copy2(s, d); print(f"  {name}: copied")
    print(f"done: {total} lines")

if __name__ == "__main__":
    main()
