#!/usr/bin/env python3
"""Lists the hooks a game version cannot install because the function's offset is 0x0 in its appdata.

The mod logs "Hook target is null" without naming anything, so a feature that is simply never called looks
exactly like a feature that does not work. This pairs every HookManager::install target in the user code with
its offset and prints the dead ones, with the feature file that wanted them.

    python unresolved_hooks.py 16 [28 33]
"""
import os, re, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
LIB = os.path.normpath(os.path.join(HERE, "..", "mod", "cheat-library", "src"))

# INSTALL_HOOK is the guarded wrapper every feature uses (relic_guard_hooks.py rewrote the raw calls)
INSTALL = re.compile(r"(?:HookManager::install(?:Guarded)?|INSTALL_HOOK)\s*\(\s*app::([A-Za-z0-9_]+)\s*,")
OFFSET = re.compile(r"^\s*DO_APP_FUNC\s*\(\s*(0x[0-9A-Fa-f]+)\s*,\s*[^,]+,\s*([A-Za-z0-9_]+)\s*,", re.M)


def hook_sites():
    sites = collections.defaultdict(list)
    for root, _, files in os.walk(os.path.join(LIB, "user")):
        for name in files:
            if not name.endswith((".cpp", ".h")):
                continue
            path = os.path.join(root, name)
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                for target in INSTALL.findall(f.read()):
                    sites[target].append(os.path.relpath(path, LIB).replace("\\", "/"))
    return sites


def offsets(version):
    table = {}
    directory = os.path.join(LIB, "appdata-%s" % version)
    for name in os.listdir(directory):
        if not name.endswith(".h"):
            continue
        with open(os.path.join(directory, name), "r", encoding="utf-8", errors="replace") as f:
            for offset, func in OFFSET.findall(f.read()):
                table[func] = offset
    return table


def main(versions):
    sites = hook_sites()
    for version in versions:
        table = offsets(version)
        dead, missing = [], []
        for target, users in sorted(sites.items()):
            offset = table.get(target)
            if offset is None:
                missing.append((target, users))
            elif int(offset, 16) == 0:
                dead.append((target, users))

        print("=== %s: %d hook targets, %d unresolved (0x0), %d not declared ===" %
              (version, len(sites), len(dead), len(missing)))
        for target, users in dead:
            print("  DEAD     %-58s %s" % (target, ", ".join(sorted(set(users)))))
        for target, users in missing:
            print("  MISSING  %-58s %s" % (target, ", ".join(sorted(set(users)))))
        print()


if __name__ == "__main__":
    main(sys.argv[1:] or ["16"])
