#!/usr/bin/env python3
"""Null-checks the three GET_SINGLETON results the upstream code used unchecked.

On 1.6 (metadata v24) a singleton's MethodInfo slot is filled the first time the GAME itself calls that
getter, so early in a session GET_SINGLETON legitimately answers null — and calling a game method with a null
`this` faults. 3.3 fills those slots up front, which is why upstream could get away with it. Idempotent.
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
U = os.path.normpath(os.path.join(HERE, "..", "mod", "cheat-library", "src", "user", "cheat"))

def patch(rel, old, new, tag):
    p = os.path.join(U, *rel.split("/"))
    with open(p, "r", encoding="utf-8", newline="") as f:
        s = f.read()
    if new.split("\n")[0].strip() and new.replace("\r\n", "\n") in s.replace("\r\n", "\n"):
        print("%s: already patched" % tag)
        return
    nl = "\r\n" if "\r\n" in s else "\n"
    old = old.replace("\n", nl)
    new = new.replace("\n", nl)
    assert old in s, "%s: pattern not found" % tag
    with open(p, "w", encoding="utf-8", newline="") as f:
        f.write(s.replace(old, new, 1))
    print("%s: patched" % tag)

if __name__ == "__main__":
    # FPSUnlock: the loading manager is not there yet during the first frames on 1.6
    patch("visuals/FPSUnlock.cpp",
          "            auto loadingManager = GET_SINGLETON(MoleMole_LoadingManager);\n"
          "            if (!app::MoleMole_LoadingManager_IsLoaded(loadingManager, nullptr))",
          "            auto loadingManager = GET_SINGLETON(MoleMole_LoadingManager);\n"
          "            // Relic: on 1.6 a singleton slot is empty until the game itself has used it once.\n"
          "            if (loadingManager == nullptr)\n"
          "                return;\n"
          "            if (!app::MoleMole_LoadingManager_IsLoaded(loadingManager, nullptr))",
          "FPSUnlock.cpp")

    patch("teleport/MapTeleport.cpp",
          "\t\t\tauto someSingleton = GET_SINGLETON(MoleMole_LoadingManager);\n",
          "\t\t\tauto someSingleton = GET_SINGLETON(MoleMole_LoadingManager);\n"
          "\t\t\tif (someSingleton == nullptr)   // Relic: not resolved yet on 1.6\n"
          "\t\t\t\treturn;\n",
          "MapTeleport.cpp")

    patch("misc/Debug.cpp",
          "        auto singleton = GET_SINGLETON(MoleMole_MapModule);\n",
          "        auto singleton = GET_SINGLETON(MoleMole_MapModule);\n"
          "        if (singleton == nullptr)   // Relic: not resolved yet on 1.6\n"
          "            return;\n",
          "Debug.cpp")
    print("done")
