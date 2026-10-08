#!/usr/bin/env python3
"""Drops inherited-layout members whose base class contributes no fields (idempotent).

The generated __Fields structs open with the base class's own __Fields, which is how the game lays a class
out - except when the base declares no instance fields. In C++ that struct is not zero-sized: it is one byte,
and `__declspec(align(8))` rounds it up to eight, so every field of the derived class ends up eight bytes past
where the game keeps it. Nothing catches that: the code compiles, runs, and eventually reads a pointer out of
the middle of a Vector3 - which is precisely how map teleport would kill the 1.6 client.

Emptiness is transitive: a base whose only member is another empty base is itself empty, and each level in
such a chain adds its own eight bytes. The set is therefore closed to a fixed point before anything is
removed. A base with real fields is left alone - `fields._.x` keeps working for those.

    python fix_empty_base_fields.py 16 [28]
"""
import os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.normpath(os.path.join(HERE, "..", "mod", "cheat-library", "src"))

OPEN = re.compile(r"struct\s+(?:__declspec\(align\(\d+\)\)\s+)?([A-Za-z_][A-Za-z0-9_]*__Fields)\s*\{")
BASE = re.compile(r"^[ \t]*(?:\[\[[^\]]*\]\]\s*)?struct ([A-Za-z_][A-Za-z0-9_]*__Fields) _;[ \t]*$")


def parse(text):
    """struct name -> its member lines at depth 1."""
    structs, name, depth, members = {}, None, 0, []
    for line in text.split("\n"):
        stripped = line.rstrip("\r")
        if name is None:
            m = OPEN.search(stripped)
            if m:
                name, depth, members = m.group(1), stripped.count("{") - stripped.count("}"), []
            continue

        depth += stripped.count("{") - stripped.count("}")
        if depth <= 0:
            structs[name] = members
            name = None
            continue
        if depth == 1 and stripped.strip():
            members.append(stripped)
    return structs


def empty_set(structs):
    """Structs that contribute no bytes, closed transitively over inherited-layout members."""
    empty = set()
    while True:
        grown = False
        for name, members in structs.items():
            if name in empty:
                continue
            bases = [BASE.match(m) for m in members]
            if all(b is not None and b.group(1) in empty for b in bases):
                empty.add(name)
                grown = True
        if not grown:
            return empty


def main(version):
    directory = os.path.join(SRC, "appdata-%s" % version)
    names = [n for n in sorted(os.listdir(directory))
             if n.startswith("il2cpp-types") and n.endswith(".h") and "asserts" not in n]

    structs = {}
    for name in names:
        with open(os.path.join(directory, name), "r", encoding="utf-8", errors="replace") as f:
            structs.update(parse(f.read()))

    empty = empty_set(structs)
    print("  %d of %d __Fields structs contribute no bytes" % (len(empty), len(structs)))

    for name in names:
        path = os.path.join(directory, name)
        with open(path, "r", encoding="utf-8", newline="") as f:
            text = f.read()

        def drop(match):
            return "" if match.group(1) in empty else match.group(0)

        # The whole line including its terminator: the generated headers mix line endings.
        new_text, _ = re.subn(r"[ \t]*(?:\[\[[^\]]*\]\][ \t]*)?struct ([A-Za-z_][A-Za-z0-9_]*__Fields) _;[ \t]*\r?\n",
                              drop, text)
        if new_text != text:
            count = text.count("__Fields _;") - new_text.count("__Fields _;")
            with open(path, "w", encoding="utf-8", newline="") as f:
                f.write(new_text)
            print("  %-28s %d empty inherited-layout member(s) removed" % (name, count))


if __name__ == "__main__":
    for version in (sys.argv[1:] or ["16"]):
        print("appdata-%s:" % version)
        main(version)
