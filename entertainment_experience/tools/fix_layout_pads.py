#!/usr/bin/env python3
"""Makes the generated appdata headers of one game version place every struct member on the offset the game's
metadata dump reports, by resizing / removing / inserting the `uint8_t _padXX[N];` filler members only.

    python fix_layout_pads.py 16 [--work DIR] [--apply] [--max-iter 12] [--report FILE]

  * copies mod/cheat-library/src/{appdata-<ver>,framework} to a work dir (default %TEMP%\\relic-ee-fixpads-<ver>),
    points the probe at it (RELIC_EE_SRC) - the repo tree is NEVER written unless --apply is given, in which case
    the fixed il2cpp-types*.h are copied back over mod/cheat-library/src/appdata-<ver>/ at the end;
  * builds the same tiny standalone program probe_offsets.py builds, extended with sizeof/alignof per member, so the
    COMPILER - not a size table - measures every member;
  * walks every struct that carries dump offsets (`// 0xNN` comments) and recomputes each filler from the measured end
    of the previous member to the dump offset of the next one. Real members are never reordered, renamed or retyped;
  * iterates to a fixed point: fillers change struct sizes, struct sizes change base-struct (`_`) sizes and embedded
    value-struct sizes, which changes the gaps in every user of them;
  * what fillers cannot fix is REPORTED, never guessed:
      TOO-LARGE   the member's measured size runs past the dump offset of the next member (the declared type is wrong);
      MISALIGNED  the dump offset is not a multiple of the measured alignof of the declared type (wrong type, or the
                  game packs the member tighter than the C++ type allows);
      BASE-TAIL   the derived struct's first own member lies inside the base struct's tail padding (IL2CPP lays child
                  fields out from the parent's *actual* size, C++ from sizeof(base)) - needs a flattened base, not a pad.

Dump offsets of `__Fields` members are absolute (klass + monitor = 0x10 ahead of offsetof); plain value types are 0-based,
exactly as probe_offsets.py treats them. Every edit is a single line: the filler line is rewritten, deleted or a new one
is inserted right before the member that needs it (named `_pad<dump offset of that member>`, as the generator does).
"""
import argparse, os, re, shutil, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_SRC = os.path.normpath(os.path.join(HERE, "..", "mod", "cheat-library", "src"))
VCVARS = r"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat"

STRUCT = re.compile(r"struct(?:\s+__declspec\(align\(\d+\)\))?\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{")
MEMBER = re.compile(
    r"^(\s*)((?:\[\[[^\]]*\]\]\s*)?(?:struct\s+|union\s+|enum\s+)?[A-Za-z_][A-Za-z0-9_:<>, ]*?[\s\*]+)"
    r"([A-Za-z_][A-Za-z0-9_]*)\s*(\[[^\]]*\])?\s*;\s*(?://\s*(0x[0-9A-Fa-f]+))?")
PAD = re.compile(r"^(\s*)uint8_t\s+(_pad[0-9A-Fa-f]+)\s*\[\s*(\d+)\s*\]\s*;")


class Member:
    __slots__ = ("name", "ctype", "array", "dump", "is_pad", "line", "indent", "cpp", "size", "align")

    def __init__(self, name, ctype, array, dump, is_pad, line, indent):
        self.name, self.ctype, self.array, self.dump, self.is_pad, self.line, self.indent = name, ctype, array, dump, is_pad, line, indent
        self.cpp = self.size = self.align = None


class Struct:
    __slots__ = ("name", "path", "line", "members", "sizeof", "alignof")

    def __init__(self, name, path, line):
        self.name, self.path, self.line, self.members = name, path, line, []
        self.sizeof = self.alignof = None


def read_lines(path):
    """(lines without line terminators, the file's dominant line terminator) - CRLF headers stay CRLF.
    The generated headers can carry a few lines of the other kind (appdata-28/il2cpp-types-relic.h has 33 CRLF
    among 1400 LF lines), so the split accepts either and the majority terminator is used when writing back."""
    with open(path, "rb") as f:
        raw = f.read()
    crlf, lf = raw.count(b"\r\n"), raw.count(b"\n")
    eol = "\r\n" if crlf * 2 >= lf else "\n"
    text = raw.decode("utf-8", errors="replace")
    return text.replace("\r\n", "\n").split("\n"), eol


def write_lines(path, lines, eol):
    with open(path, "wb") as f:
        f.write(eol.join(lines).encode("utf-8"))


def header_paths(appdata):
    return [os.path.join(appdata, n) for n in sorted(os.listdir(appdata))
            if n.startswith("il2cpp-types") and n.endswith(".h") and "asserts" not in n]


def parse(appdata):
    """every struct of the il2cpp-types*.h headers with at least one dump-offset member, members in declaration order"""
    structs = []
    for path in header_paths(appdata):
        lines, _ = read_lines(path)
        cur, depth = None, 0
        for i, line in enumerate(lines):
            if cur is None:
                m = STRUCT.search(line)
                if m:
                    cur, depth = Struct(m.group(1), path, i), line.count("{") - line.count("}")
                continue
            depth += line.count("{") - line.count("}")
            if depth <= 0:
                if any(mb.dump is not None for mb in cur.members):
                    structs.append(cur)
                cur = None
                continue
            if depth != 1:
                continue
            p = PAD.match(line)
            if p:
                cur.members.append(Member(p.group(2), "uint8_t", "[%s]" % p.group(3), None, True, i, p.group(1)))
                continue
            m = MEMBER.match(line)
            if m:
                cur.members.append(Member(m.group(3), m.group(2).strip(), m.group(4) or "", int(m.group(5), 16) if m.group(5) else None,
                                          False, i, m.group(1)))
    # a struct name defined twice (cannot happen in a compiling header) - keep the first
    seen, out = set(), []
    for s in structs:
        if s.name not in seen:
            seen.add(s.name)
            out.append(s)
    return out


def run_probe(work, version, structs):
    """compile + run the measuring program; fills Member.cpp/size/align and Struct.sizeof/alignof"""
    src = os.path.join(work, "probe_%s.cpp" % version)
    exe = os.path.join(work, "probe_%s.exe" % version)
    obj = os.path.join(work, "probe_%s.obj" % version)
    with open(src, "w", encoding="utf-8") as f:
        f.write('#include <cstdio>\n#include <cstddef>\n#include <cstdint>\n#include "il2cpp-appdata.h"\n')
        f.write("int main() {\n")
        for s in structs:
            base = 0x10 if s.name.endswith("__Fields") else 0
            f.write('    printf("== %s sizeof=0x%%zX alignof=0x%%zX\\n", sizeof(app::%s), alignof(app::%s));\n' % (s.name, s.name, s.name))
            for mb in s.members:
                f.write('    printf("%s::%s cpp=0x%%zX size=0x%%zX align=0x%%zX\\n", offsetof(app::%s, %s) + 0x%X, '
                        'sizeof(app::%s::%s), alignof(decltype(app::%s::%s)));\n'
                        % (s.name, mb.name, s.name, mb.name, base, s.name, mb.name, s.name, mb.name))
        f.write("    return 0;\n}\n")
    appdata = os.path.join(os.environ["RELIC_EE_SRC"], "appdata-%s" % version)
    framework = os.path.join(os.environ["RELIC_EE_SRC"], "framework")
    cmd = ('call "%s" >nul && cl /nologo /std:c++20 /EHsc /W0 /I"%s" /I"%s" "%s" /Fe:"%s" /Fo:"%s"'
           % (VCVARS, appdata, framework, src, exe, obj))
    r = subprocess.run(cmd, shell=True, capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-6000:], r.stderr[-2000:])
        sys.exit(1)
    out = subprocess.run([exe], capture_output=True, text=True).stdout
    by_name = {s.name: s for s in structs}
    cur = None
    for line in out.splitlines():
        m = re.match(r"== (\S+) sizeof=(0x[0-9A-F]+) alignof=(0x[0-9A-F]+)", line)
        if m:
            cur = by_name[m.group(1)]
            cur.sizeof, cur.alignof = int(m.group(2), 16), int(m.group(3), 16)
            members = {mb.name: mb for mb in cur.members}
            continue
        m = re.match(r"(\S+)::(\S+) cpp=(0x[0-9A-F]+) size=(0x[0-9A-F]+) align=(0x[0-9A-F]+)", line)
        if m:
            mb = members[m.group(2)]
            mb.cpp, mb.size, mb.align = int(m.group(3), 16), int(m.group(4), 16), int(m.group(5), 16)


def align_up(x, a):
    return (x + a - 1) // a * a


def plan(structs):
    """per struct: the filler edits that put every member on its dump offset given the measured sizes/alignments of
    the real members. Returns (edits, problems, mismatches): edits = [(path, line, kind, text)] with kind in
    set/delete/insert-before; problems = [(class, struct, member, detail)]; mismatches = members off today."""
    edits, problems, mismatches = [], [], 0
    for s in structs:
        base = 0x10 if s.name.endswith("__Fields") else 0
        end = base           # predicted end of the previous real member, absolute (dump) coordinates
        prev = None          # the previous real member (None at the start of the struct)
        prev_at = base       # where the planner placed it (== its dump offset unless it could not be placed)
        pads = []            # the filler member(s) seen since the previous real member
        for mb in s.members:
            if mb.is_pad:
                pads.append(mb)
                continue
            if mb.dump is not None and mb.cpp != mb.dump:
                mismatches += 1
            if mb.dump is None:      # base `_` or a member without dump offset: trust the measurement, keep fillers
                end = mb.cpp + mb.size
                prev, prev_at, pads = mb, mb.cpp, []
                continue
            D, a, sz = mb.dump, mb.align, mb.size
            nat = align_up(end, a)
            if D % a != 0:
                problems.append(("MISALIGNED", s.name, mb.name,
                                 "dump 0x%X is not a multiple of alignof(%s%s)=%d (measured size 0x%X)" % (D, mb.ctype, mb.array, a, sz)))
            if D < nat:
                if D < end and prev is not None:
                    # the previous member runs past this one's dump offset: its declared type is too big (TOO-LARGE), or it
                    # is the base `_` (BASE-TAIL), or it was itself pushed off its dump offset by an earlier problem (CASCADE)
                    if prev.dump is not None and prev_at != prev.dump:
                        kind = "CASCADE"
                    else:
                        kind = "BASE-TAIL" if prev.name == "_" else "TOO-LARGE"
                    problems.append((kind, s.name, prev.name,
                                     "declared %s%s measures 0x%X bytes at 0x%X (ends 0x%X) but the next member %s sits at dump 0x%X: gap is 0x%X"
                                     % (prev.ctype, prev.array, prev.size, prev_at, end, mb.name, D, D - prev_at)))
                # a filler can only push a member later; drop the fillers so the member sits as early as C++ allows
                for pd in pads:
                    edits.append((s.path, pd.line, "delete", None))
                end = nat + sz
                prev, prev_at, pads = mb, nat, []
                continue
            # D >= nat: a filler of D - end bytes lands the member on D; none when the natural alignment does already
            want = 0 if nat == D else D - end
            if pads:
                if want == 0:
                    for pd in pads:
                        edits.append((s.path, pd.line, "delete", None))
                else:
                    if pads[0].size != want:
                        edits.append((s.path, pads[0].line, "set", "%suint8_t %s[%d];" % (pads[0].indent, pads[0].name, want)))
                    for pd in pads[1:]:
                        edits.append((s.path, pd.line, "delete", None))
            elif want:
                edits.append((s.path, mb.line, "insert-before", "%suint8_t _pad%X[%d];" % (mb.indent, D, want)))
            end = align_up(D, a) + sz
            prev, prev_at, pads = mb, align_up(D, a), []
    return edits, problems, mismatches


def apply_edits(edits):
    by_path = {}
    for path, line, kind, text in edits:
        by_path.setdefault(path, []).append((line, kind, text))
    changed = 0
    for path, items in by_path.items():
        lines, eol = read_lines(path)
        # apply bottom-up so line numbers stay valid
        for line, kind, text in sorted(items, key=lambda t: t[0], reverse=True):
            if kind == "set":
                lines[line] = text
            elif kind == "delete":
                del lines[line]
            else:
                lines.insert(line, text)
            changed += 1
        write_lines(path, lines, eol)
    return changed


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("version")
    ap.add_argument("--src", default=REPO_SRC, help="mod/cheat-library/src to copy from")
    ap.add_argument("--work", default=None, help="scratch copy (default %%TEMP%%\\relic-ee-fixpads-<ver>)")
    ap.add_argument("--reuse", action="store_true", help="do not re-copy; continue on the work dir as it is")
    ap.add_argument("--apply", action="store_true", help="copy the fixed il2cpp-types*.h back into --src/appdata-<ver>")
    ap.add_argument("--max-iter", type=int, default=12)
    ap.add_argument("--report", default=None, help="write the problem list + per-iteration summary here")
    a = ap.parse_args()
    ver = a.version
    work = a.work or os.path.join(os.environ.get("TEMP", "."), "relic-ee-fixpads-%s" % ver)
    src_app = os.path.join(work, "src", "appdata-%s" % ver)
    if not a.reuse:
        if os.path.isdir(os.path.join(work, "src")):
            shutil.rmtree(os.path.join(work, "src"))
        os.makedirs(os.path.join(work, "src"))
        shutil.copytree(os.path.join(a.src, "appdata-%s" % ver), src_app)
        shutil.copytree(os.path.join(a.src, "framework"), os.path.join(work, "src", "framework"))
    os.environ["RELIC_EE_SRC"] = os.path.join(work, "src")
    log = []
    total_edits = 0
    problems = []
    for it in range(1, a.max_iter + 1):
        structs = parse(src_app)
        run_probe(work, ver, structs)
        edits, problems, mismatches = plan(structs)
        msg = "iteration %d: %d struct(s), %d mismatched member(s), %d filler edit(s), %d problem(s)" % (
            it, len(structs), mismatches, len(edits), len(problems))
        print(msg)
        log.append(msg)
        if not edits:
            break
        total_edits += apply_edits(edits)
    # final measurement
    structs = parse(src_app)
    run_probe(work, ver, structs)
    _, problems, mismatches = plan(structs)
    remaining = [(s.name, mb.name, mb.cpp, mb.dump, mb.cpp - mb.dump) for s in structs for mb in s.members
                 if mb.dump is not None and mb.cpp != mb.dump]
    summary = "done: %d filler edit(s) in total, %d mismatched member(s) remain in %d struct(s), %d problem(s)" % (
        total_edits, mismatches, len({r[0] for r in remaining}), len(problems))
    print(summary)
    log.append(summary)
    for p in problems:
        line = "  %-10s %s::%s  %s" % p
        print(line)
        log.append(line)
    if remaining:
        log.append("remaining mismatches:")
        for r in remaining:
            log.append("  %s::%s cpp=0x%X dump=0x%X (delta %+d)" % r)
    if a.report:
        with open(a.report, "w", encoding="utf-8") as f:
            f.write("\n".join(log) + "\n")
    if a.apply:
        for n in os.listdir(src_app):
            if n.startswith("il2cpp-types") and n.endswith(".h") and "asserts" not in n:
                shutil.copy2(os.path.join(src_app, n), os.path.join(a.src, "appdata-%s" % ver, n))
        print("applied to", os.path.join(a.src, "appdata-%s" % ver))
    print("work dir:", work)


if __name__ == "__main__":
    main()
