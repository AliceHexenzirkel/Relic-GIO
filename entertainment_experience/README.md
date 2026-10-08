# entertainment_experience — the in-game enhancements menu (Akebi GC backport)

A client-side mod menu (ImGui overlay, **F1** in game) that Relic can inject into the private-server clients it
launches: `CLibrary.dll`, one build per game version, shipped as `payload/<ver>/ee/CLibrary.dll.bin` and injected by the
same mhynot2 `launcher.exe` that already starts the game (`launcher.exe <GameDir> mhynot2.dll CLibrary.dll`).
Upstream targets game 3.3; this tree backports it to **2.8** and **1.6**. Full reference:
[docs/ENTERTAINMENT-EXPERIENCE.md](../docs/ENTERTAINMENT-EXPERIENCE.md). Provenance and licences: [VENDOR.md](VENDOR.md).

```
mod/                 upstream v1.2.3 + Relic changes (relic-ee.sln, cheat-base/, cheat-library/ with src/appdata-{16,28,33})
ref/akebi-gc-2.8/    last upstream source state on game 2.8 (offsets + features), read-only reference
tools/               checksum / dump / transplant / build scripts (build_ee.ps1)
heavy_ref/ dumps_ref/   gitignored: HD icons + CJK fonts, history bundle, IL2CPP dumps, client extracts
```

Build (phase 1 onwards): `tools\build_ee.ps1 -Versions 28` → `mod\bin\v28\Release-x64\CLibrary.dll` → `payload\2.8\ee\`.
Toolchain: VS Build Tools 2026 (MSVC 14.51, toolset v145), Windows SDK 10.0.26100, MSBuild 18, Python 3.

Status (2026-08-23): phases 0–3 done (vendoring, multi-version build — `GameVersion=33` builds Release + Release_WS —,
Relic defaults, Relic-side integration with spikes); phase 4 (2.8 backport: dump → transplant → types → live test) in
progress; phase 5 (1.6) and 6 (polish/ship) pending. Details: docs/ENTERTAINMENT-EXPERIENCE.md §11.
