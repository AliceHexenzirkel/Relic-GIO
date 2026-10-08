#!/usr/bin/env python3
"""Puts every hook into the game behind the SEH guard (idempotent).

Hooks are called by the game, so they bypass the guards around features, events and rendering. On a backported
build they are the riskiest code in the mod - a field offset reconstructed from a dump that turns out wrong
kills the client outright. Rewrites `HookManager::install(app::X, Y)` into `INSTALL_HOOK(app::X, Y)`, which
attaches a guarded wrapper instead. Only targets in the `app::` namespace are touched: the ntdll hooks in
debugger.cpp and the DXGI hooks are ours, not the game's.
"""
import os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
USER = os.path.normpath(os.path.join(HERE, "..", "mod", "cheat-library", "src", "user"))

CALL = re.compile(r"HookManager::install\(\s*(app::[A-Za-z0-9_]+)\s*,\s*([A-Za-z0-9_:]+)\s*\)")


def main(check_only=False):
    changed = total = 0
    for root, _, files in os.walk(USER):
        for name in sorted(files):
            if not name.endswith(".cpp"):
                continue
            path = os.path.join(root, name)
            with open(path, "r", encoding="utf-8", newline="") as f:
                text = f.read()

            new_text, count = CALL.subn(r"INSTALL_HOOK(\1, \2)", text)
            total += count
            if count and not check_only:
                with open(path, "w", encoding="utf-8", newline="") as f:
                    f.write(new_text)
                changed += 1
                print("  %-46s %d hooks guarded" % (os.path.relpath(path, USER).replace("\\", "/"), count))

    left = 0
    for root, _, files in os.walk(USER):
        for name in files:
            if name.endswith(".cpp"):
                with open(os.path.join(root, name), "r", encoding="utf-8") as f:
                    left += len(CALL.findall(f.read()))

    print("%d hooks rewritten in %d files; %d unguarded game hooks left" % (total, changed, left))
    return 0 if left == 0 else 1


if __name__ == "__main__":
    sys.exit(main("--check" in sys.argv))
