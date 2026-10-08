#!/usr/bin/env python3
"""Module checksums the mod uses to recognise "its" game build (PatternScanner::ComputeChecksum: the file read
in 64-bit little-endian words, summed modulo 2^64, partial tail word zero-padded).

  checksum.py --game-dir "D:\\relic\\Genshin 2.8" --ver 28 --label OSRELWin2.8.0
      -> prints both sums and writes mod/cheat-library/res/assembly_checksum-28.json
         ({"global": {"game_version": <label>, "modules": {UnityPlayer.dll, UserAssembly.dll}}})
  checksum.py <file>                       -> prints the word-sum of one file
  checksum.py --compare <file> <expected>  -> exit 0 when the sum equals <expected>

res/res.rc embeds assembly_checksum-<ver>.json as ASSEMBLYCHECKSUMS (selected by RELIC_GAME_VERSION);
il2cpp-init.cpp refuses to hook a client whose modules do not match.
"""
import json, os, struct, sys

HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.normpath(os.path.join(HERE, "..", "mod", "cheat-library", "res"))
MASK = (1 << 64) - 1

def wordsum(path):
    total = 0
    with open(path, "rb") as f:
        while True:
            chunk = f.read(1 << 20)
            if not chunk:
                break
            n = len(chunk) // 8
            for (w,) in struct.iter_unpack("<Q", chunk[: n * 8]):
                total = (total + w) & MASK
            rem = chunk[n * 8:]
            if rem:
                total = (total + int.from_bytes(rem, "little")) & MASK
    return total

def main(argv):
    if len(argv) == 1 and not argv[0].startswith("--"):
        print(wordsum(argv[0])); return 0
    if len(argv) == 3 and argv[0] == "--compare":
        got = wordsum(argv[1]); ok = got == int(argv[2])
        print(f"{got} {'==' if ok else '!='} {argv[2]}"); return 0 if ok else 1
    if "--game-dir" in argv and "--ver" in argv:
        gd = argv[argv.index("--game-dir") + 1]; ver = argv[argv.index("--ver") + 1]
        label = argv[argv.index("--label") + 1] if "--label" in argv else f"OSRELWin{ver[0]}.{ver[1:]}.0"
        mods = {
            "UnityPlayer.dll": os.path.join(gd, "UnityPlayer.dll"),
            "UserAssembly.dll": os.path.join(gd, "GenshinImpact_Data", "Native", "UserAssembly.dll"),
        }
        out = {"global": {"game_version": label, "modules": {}}}
        for name, p in mods.items():
            s = wordsum(p); out["global"]["modules"][name] = {"checksum": s, "timestamp": 0}
            print(f"{name}: {s}")
        dst = os.path.join(RES, f"assembly_checksum-{ver}.json")
        with open(dst, "w", encoding="utf-8", newline="\n") as f:
            json.dump(out, f, indent=2); f.write("\n")
        print("written", dst); return 0
    print(__doc__); return 2

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
