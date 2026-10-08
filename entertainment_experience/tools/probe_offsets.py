#!/usr/bin/env python3
"""Prints where the C++ headers of one game version actually place the members of a struct, next to the
offset the dump says. Builds a tiny standalone program against the appdata headers (no cheat-base needed),
so it is the compiler - not a size table - that measures the layout.

    python probe_offsets.py 16 AttackResult__Fields [MoreStruct__Fields ...]
    python probe_offsets.py 16 --all            # every struct that carries dump offsets

RELIC_EE_SRC=<dir> points it at another copy of mod/cheat-library/src (e.g. a scratch copy).

Output lines:  <struct>::<member>  cpp=0x..  dump=0x..  [MISMATCH (delta)]
The dump offset is absolute within the object, i.e. 0x10 (klass + monitor) ahead of the offset inside a
__Fields struct, so the probe adds 0x10 to offsetof for those. Plain value types (no __Fields suffix, e.g.
MapModule_ScenePointData) have 0-based dump offsets and are reported without the 0x10.
"""
import os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.environ.get("RELIC_EE_SRC") or os.path.normpath(os.path.join(HERE, "..", "mod", "cheat-library", "src"))
VCVARS = r"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
OUT = os.path.join(os.environ.get("TEMP", "."), "relic-ee-probe")

STRUCT = re.compile(r"struct(?:\s+__declspec\(align\(\d+\)\))?\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{")
MEMBER = re.compile(
    r"^\s*(?:\[\[[^\]]*\]\]\s*)?(?:struct\s+|union\s+|enum\s+)?[A-Za-z_][A-Za-z0-9_:<>, ]*?[\s\*]+"
    r"([A-Za-z_][A-Za-z0-9_]*)\s*(?:\[[^\]]*\])?\s*;\s*(?://\s*(0x[0-9A-Fa-f]+))?")


def headers(version):
    directory = os.path.join(ROOT, "appdata-%s" % version)
    return [os.path.join(directory, n) for n in sorted(os.listdir(directory))
            if n.startswith("il2cpp-types") and n.endswith(".h") and "asserts" not in n]


def members_of(version, wanted):
    """struct -> [(member, dump offset or None)], in declaration order; wanted=None means every struct."""
    found = {}
    for path in headers(version):
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            lines = f.read().split("\n")
        cur, depth = None, 0
        for line in lines:
            if cur is None:
                m = STRUCT.search(line)
                if m and (wanted is None or m.group(1) in wanted):
                    cur, depth = m.group(1), line.count("{") - line.count("}")
                    found.setdefault(cur, [])
                continue
            depth += line.count("{") - line.count("}")
            if depth <= 0:
                cur = None
                continue
            m = MEMBER.match(line)
            if m and depth == 1:
                found[cur].append((m.group(1), int(m.group(2), 16) if m.group(2) else None))
    if wanted is None:   # --all: only structs that carry at least one dump offset
        found = {k: v for k, v in found.items() if any(o is not None for _, o in v)}
    return found


def main(version, args):
    wanted = None if args == ["--all"] else set(args)
    found = members_of(version, wanted)
    os.makedirs(OUT, exist_ok=True)
    src = os.path.join(OUT, "probe_%s.cpp" % version)
    with open(src, "w", encoding="utf-8") as f:
        f.write('#include <cstdio>\n#include <cstddef>\n#include <cstdint>\n#include "il2cpp-appdata.h"\n')
        f.write("int main() {\n")
        for struct, members in found.items():
            base = 0x10 if struct.endswith("__Fields") else 0
            f.write('    printf("== %s sizeof=0x%%zX\\n", sizeof(app::%s));\n' % (struct, struct))
            for member, dump in members:
                f.write('    printf("%s::%s cpp=0x%%zX dump=%s\\n", offsetof(app::%s, %s) + 0x%X);\n'
                        % (struct, member, "0x%X" % dump if dump is not None else "?", struct, member, base))
        f.write("    return 0;\n}\n")

    exe = os.path.join(OUT, "probe_%s.exe" % version)
    obj = os.path.join(OUT, "probe_%s.obj" % version)
    cmd = ('call "%s" >nul && cl /nologo /std:c++20 /EHsc /W0 /I"%s" /I"%s" "%s" /Fe:"%s" /Fo:"%s"'
           % (VCVARS, os.path.join(ROOT, "appdata-%s" % version), os.path.join(ROOT, "framework"), src, exe, obj))
    r = subprocess.run(cmd, shell=True, capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-6000:], r.stderr[-2000:])
        sys.exit(1)
    out = subprocess.run([exe], capture_output=True, text=True).stdout
    mismatches = 0
    for line in out.splitlines():
        m = re.match(r"(\S+) cpp=(0x[0-9A-F]+) dump=(0x[0-9A-F]+|\?)", line)
        if m and m.group(3) != "?" and int(m.group(2), 16) != int(m.group(3), 16):
            line += "   MISMATCH (delta %+d)" % (int(m.group(2), 16) - int(m.group(3), 16))
            mismatches += 1
        print(line)
    print("-- %d mismatched member(s)" % mismatches)


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    main(sys.argv[1], sys.argv[2:])
