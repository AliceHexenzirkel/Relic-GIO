#!/usr/bin/env python3
"""Summarises the compiler errors of an MSBuild log (deduplicated, grouped), for the appdata-16 build-log iteration:

    build_errors.py dumps_ref/1.6/build16.log [--max N]
"""
import collections, re, sys


def main():
    if len(sys.argv) < 2:
        print(__doc__); return 2
    path = sys.argv[1]
    mx = int(sys.argv[sys.argv.index("--max") + 1]) if "--max" in sys.argv else 80
    t = open(path, encoding="utf-8", errors="replace").read()
    errs = collections.OrderedDict()
    for m in re.finditer(r'^(.*?)\((\d+)(?:,\d+)?\): error (C\d+|LNK\d+|MSB\d+): (.*?)(?: \[|$)', t, re.M):
        f = m.group(1).split("\\src\\")[-1]
        key = (f, m.group(2), m.group(3), m.group(4)[:170])
        errs[key] = errs.get(key, 0) + 1
    codes = collections.Counter(k[2] for k in errs)
    print(f"{len(errs)} distinct errors: {dict(codes)}")
    for (f, ln, code, msg), n in list(errs.items())[:mx]:
        print(f"  {f}:{ln} {code} {msg}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
