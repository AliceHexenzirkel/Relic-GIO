#!/usr/bin/env python3
"""Applies hand-decided offsets (tools/offsets-<ver>.overrides.json) to appdata-<ver>/il2cpp-functions.h and
il2cpp-types-ptr.h — for the symbols transplant.py could not decide on its own (ambiguous same-signature pairs,
structural guesses). Each entry: {"NAME": {"rva": "0x...", "note": "why / how verified"}}. Only lines whose offset is
0x0 (or carry the RELIC-TODO-<ver> marker) are touched; the note is written as a trailing comment so the header
itself says what is still UNVERIFIED. Idempotent.

  apply_overrides.py --ver 28
"""
import json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.normpath(os.path.join(HERE, "..", "mod"))

def main():
    if len(sys.argv) != 3 or sys.argv[1] != "--ver":
        print(__doc__); return 2
    ver = sys.argv[2]
    ov_path = os.path.join(HERE, f"offsets-{ver}.overrides.json")
    with open(ov_path, encoding="utf-8") as f:
        overrides = json.load(f)
    appdata = os.path.join(MOD, "cheat-library", "src", f"appdata-{ver}")
    marker = f"RELIC-TODO-{ver}"
    applied, skipped = [], []
    for fn in ("il2cpp-functions.h", "il2cpp-types-ptr.h", "il2cpp-unityplayer-functions.h"):
        p = os.path.join(appdata, fn)
        if not os.path.exists(p):
            continue
        with open(p, "r", encoding="utf-8", errors="surrogateescape", newline="") as f:
            text = f.read()
        nl = "\r\n" if "\r\n" in text else "\n"
        out = []
        for line in text.split(nl):
            m = re.match(r'^(\s*DO_(?:APP_FUNC_METHODINFO|APP_FUNC|TYPEDEF|SINGLETONEDEF)\()\s*(0x0+|0)\s*,(\s*(?:[^,]+,)?\s*)([A-Za-z_]\w*)(\s*[,)].*)$', line)
            if m and m.group(4) in overrides:
                name = m.group(4); o = overrides[name]
                body = re.sub(r"\s*//\s*" + re.escape(marker) + r".*$", "", m.group(5))
                line = f"{m.group(1)}{o['rva']},{m.group(3)}{name}{body}  // {ver}: OVERRIDE {o.get('note','')}".rstrip()
                applied.append(name)
            out.append(line)
        with open(p, "w", encoding="utf-8", errors="surrogateescape", newline="") as f:
            f.write(nl.join(out))
    for name in overrides:
        if name not in applied:
            skipped.append(name)
    print(f"applied {len(applied)}: {', '.join(applied)}")
    if skipped:
        print(f"not applied (already resolved or not found): {', '.join(skipped)}")
    return 0

if __name__ == "__main__":
    sys.exit(main())
