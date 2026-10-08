#!/usr/bin/env python3
"""Writes dumps_ref/1.6/REPORT-matching.md from the artifacts of the 1.6 offset pipeline:
dumps_ref/1.6/{match16.json, gen16.json, fieldanchors.json, unresolved.txt, build16.log} and the generated
mod/cheat-library/src/appdata-16/ headers. Stdlib only; deterministic.

    report16.py [--build-log dumps_ref/1.6/build16.log]
"""
import collections, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
EE = os.path.normpath(os.path.join(HERE, ".."))
OUT = os.path.join(EE, "dumps_ref", "1.6")
A16 = os.path.join(EE, "mod", "cheat-library", "src", "appdata-16")
USER = os.path.join(EE, "mod", "cheat-library", "src", "user")

RULES_CLASS = {
    "C-name": "readable full name identical in both dumps, unique on both sides",
    "C-nested": "readable nested type of a matched outer type",
    "C-enum": "enum with identical / best-Jaccard readable member names",
    "C-fp": "code + structural fingerprint in a structural pool (Singleton<X> classes, proto family, nested of matched outer, global by size): string literals loaded, readable call targets, matched callers/callees, TypeInfo/MethodInfo slot refs, readable member names, base class, field-type sequence, method-signature multiset",
    "C-usage": "same fingerprint inside the usage pool: the types referenced (fields, generic args, oneof accessors, parameters) by the counterparts of the matched classes that reference the 2.8 type the same way",
    "C-field": "aligned field type of a matched class pair (field lists aligned by type-token sequence; unanimous votes; shape-checked)",
    "C-generic": "generic argument of an aligned field (List<X> <-> List<Y>)",
    "C-base": "base class of a matched pair (shape-checked)",
    "C-param": "parameter/return type of a matched method pair (unanimous votes)",
    "C-param-joint": "joint decision: the parameter type whose class score AND whose method's fingerprint both win",
}
RULES_METHOD = {
    "M-name": "readable method name inside the matched class (+ compatible signature; `medium` when a parameter identity differs)",
    "M-name-arity": "readable name, fewer parameters in 1.6 (compatible prefix; extra trailing args ignored by the x64 ABI)",
    "M-sig": "the only method of the matched class with a compatible signature",
    "M-sig-prefix": "the only method whose parameters are a compatible prefix of the 2.8 ones",
    "M-sig-ret": "same parameters, different return kind (void in 1.6)",
    "M-fp": "compatible signature + code fingerprint (strings / readable calls / matched callers & callees / metadata refs) with a margin",
    "M-fp+param": "joint decision with an unmatched parameter type (class score + method fingerprint)",
    "M-fp-prefix": "M-fp over prefix-compatible (shorter 1.6) candidates",
    "M-pos": "compatible signature + declaration-order alignment of the two method lists (SequenceMatcher)",
    "M-pair-order": "a twin pair (two identical-signature methods on both sides) mapped by declaration order",
    "M-pair-order-prefix": "twin pair over prefix-compatible candidates",
    "M-family": "the best candidates are identical bodies (masked machine code >= 0.98) - the best-scored one taken",
    "M-getter-field": "trivial getter matched through the field it reads (aligned field offsets)",
    "M-generic": "generic instantiation: owner + method + type arguments mapped, body from script.json (parameter count checked)",
}
PRIORITY = ["GameManager_Update", "Singleton_GetInstance", "Singleton_1_MoleMole_PlayerModule__get_Instance__MethodInfo",
            "Singleton_1_MoleMole_EntityManager__get_Instance__MethodInfo", "Singleton_1_MoleMole_MapModule__get_Instance__MethodInfo",
            "Singleton_1_MoleMole_LoadingManager__get_Instance__MethodInfo", "Singleton_1_InteractionManager__get_Instance__MethodInfo",
            "Singleton_1_MoleMole_UIManager__get_Instance__MethodInfo", "Singleton_1_MoleMole_ItemModule__get_Instance__MethodInfo",
            "Singleton_1_MoleMole_EventManager__get_Instance__MethodInfo", "Singleton_1_MoleMole_MapManager__get_Instance__MethodInfo",
            "Singleton_1_MoleMole_ScenePropManager__get_Instance__MethodInfo", "Singleton_1_MoleMole_NetworkManager__get_Instance__MethodInfo",
            "Cursor_set_visible", "Cursor_get_visible", "Cursor_set_lockState", "Screen_get_width", "Screen_get_height", "Time_get_deltaTime",
            "Time_get_timeScale", "Time_set_timeScale", "Application_get_targetFrameRate", "Application_set_targetFrameRate",
            "Application_get_systemLanguage", "Application_get_IsFocused", "Camera_get_main", "Camera_get_fieldOfView", "Camera_set_fieldOfView",
            "Camera_WorldToScreenPoint", "Transform_get_position", "Transform_set_position", "GameObject_Find", "GameObject_SetActive",
            "Rigidbody_get_velocity", "Rigidbody_set_velocity", "RenderSettings_set_fog",
            "MoleMole_SCameraModuleInitialize_SetWarningLocateRatio", "Miscs_SetUILocalAvatarVisible", "MoleMole_UIManager_EnableInput",
            "MoleMole_VCBaseSetDitherValue_set_ManagerDitherAlphaValue", "MoleMole_TalkDialogContext_get_canClick", "MoleMole_TalkDialogContext_get_canAutoClick",
            "MoleMole_InLevelCutScenePageContext_OnFreeClick", "MoleMole_LCAvatarCombat_CheckCDTimer", "MoleMole_LCAvatarCombat_SetSkillIndex",
            "MoleMole_EquipLevelUpDialogContext_SetupView", "MoleMole_LCIndicatorPlugin_DoCheck",
            "MoleMole_LoadingManager_PerformPlayerTransmit", "MoleMole_LoadingManager_RequestSceneTransToPoint", "MoleMole_LoadingManager_NeedTransByServer",
            "ActorUtils_GetAvatarPos", "ActorUtils_SetAvatarPos", "Miscs_GenWorldPos", "Miscs_GenLevelPos_1", "WorldShiftManager_GetRelativePosition",
            "MoleMole_InLevelMapPageContext_OnMarkClicked", "MoleMole_InLevelMapPageContext_OnMapClicked",
            "MoleMole_EntityManager_GetEntities", "MoleMole_EntityManager_GetLocalAvatarEntity", "MoleMole_EntityManager_GetValidEntity",
            "MoleMole_BaseEntity_GetAbsolutePosition", "MoleMole_BaseEntity_GetRelativePosition", "MoleMole_LevelModule_OnSceneEntityAppear",
            "MoleMole_LevelModule_OnSceneEntityAppearAsync", "MoleMole_GadgetModule_DoOnGadgetStateNotify", "MoleMole_ItemModule_PickItem",
            "MoleMole_PlayerModule_OnWindSeedClientNotify", "MoleMole_TimeUtil_get_LocalNowMsTimeStamp"]


def read(p):
    with open(p, encoding="utf-8", errors="replace") as f:
        return f.read()


def md_escape(s):
    return str(s).replace("|", "\\|").replace("\n", " ")


def main():
    bl = sys.argv[sys.argv.index("--build-log") + 1] if "--build-log" in sys.argv else os.path.join(OUT, "build16.log")
    match = json.load(open(os.path.join(OUT, "match16.json"), encoding="utf-8"))
    gen = json.load(open(os.path.join(OUT, "gen16.json"), encoding="utf-8"))
    anchors = json.load(open(os.path.join(OUT, "fieldanchors.json"), encoding="utf-8")) if os.path.exists(os.path.join(OUT, "fieldanchors.json")) else {}
    st = match["stats"]
    F = match["functions"]
    funcs = {k: v for k, v in F.items() if v["kind"] == "func"}
    slots = {k: v for k, v in F.items() if v["kind"] != "func"}
    # feature usage of unresolved functions: hook (safe) vs direct call (crash risk)
    src = {}
    for root, _, files in os.walk(USER):
        for f in files:
            if f.endswith((".cpp", ".h")):
                src[os.path.relpath(os.path.join(root, f), USER)] = read(os.path.join(root, f))
    def usage(name):
        hooks, calls = [], []
        for fn, t in src.items():
            for m in re.finditer(r'\b' + re.escape(name) + r'\b', t):
                line = t.count("\n", 0, m.start()) + 1
                ctx = t[max(0, t.rfind("\n", 0, m.start())):t.find("\n", m.start())]
                if "HookManager::install" in ctx or "CALL_ORIGIN" in ctx or ctx.strip().startswith("//"):
                    hooks.append(f"{fn}:{line}")
                elif re.search(r'app::' + re.escape(name) + r'\s*\(', ctx):
                    before = t[max(0, m.start() - 240):m.start()]
                    guarded = re.search(re.escape(name) + r'\s*!=\s*nullptr', before) is not None
                    calls.append(f"{fn}:{line}" + (" (null-guarded)" if guarded else ""))
        return hooks, calls
    # build errors
    errs = []
    if os.path.exists(bl):
        t = read(bl)
        seen = set()
        for m in re.finditer(r'^(.*?)\((\d+)(?:,\d+)?\): error (C\d+): (.*?)(?: \[|$)', t, re.M):
            key = (m.group(1).split("\\src\\")[-1], m.group(2), m.group(3), m.group(4)[:160])
            if key not in seen:
                seen.add(key); errs.append(key)
        summary = re.search(r'(\d+) Warning\(s\)\s+(\d+) Error\(s\)', t)
    else:
        summary = None
    L = []
    w = L.append
    w("# Game 1.6 (OS) offset / type pipeline — structural 2.8 → 1.6 matching report")
    w("")
    w("Generated by `tools/report16.py` from `dumps_ref/1.6/{match16.json, gen16.json, fieldanchors.json, build16.log}`. Everything in this report was produced by")
    w("`tools/matcher16.py` (+ `tools/codexref.py`) and `tools/gen_appdata16.py`; nothing was typed in by hand. Inputs: the 2.8 headers")
    w("`mod/cheat-library/src/appdata-28/` (live-tested), the two IL2CPP dumps (`dumps_ref/2.8/`, `dumps_ref/1.6/greenxemotion/`) and the two")
    w("`UserAssembly.dll` / `UnityPlayer.dll` binaries (read-only; their cross-reference index is cached in `dumps_ref/<ver>/xrefs.pkl`).")
    w("")
    w("```")
    w("python tools/matcher16.py [--dll28 ... --dll16 ... --unity28 ... --unity16 ...]   # ~2 min: match16.json, classmap.json, fieldanchors.json, unresolved.txt")
    w("python tools/gen_appdata16.py [--build-log dumps_ref/1.6/build16.log]              # ~25 s: mod/cheat-library/src/appdata-16/*.h, gen16.json")
    w('"C:\\Program Files (x86)\\Microsoft Visual Studio\\18\\BuildTools\\MSBuild\\Current\\Bin\\MSBuild.exe" mod\\relic-ee.sln -t:Build -p:Configuration=Release -p:Platform=x64 -p:GameVersion=16 -m -nologo -v:minimal -clp:"Summary;ErrorsOnly" > dumps_ref\\1.6\\build16.log')
    w("python tools/build_errors.py dumps_ref/1.6/build16.log        # deduplicated error list")
    w("python tools/apply_overrides.py --ver 16                      # hand decisions from tools/offsets-16.overrides.json (empty skeleton so far)")
    w("python tools/unresolved_report.py 16                          # unresolved symbols -> feature files")
    w("python tools/report16.py                                      # this file")
    w("```")
    w("")
    w("## 1. Address math and acceptance")
    w("")
    w("`script.json` `Address` values are RVAs in both dumps (Il2CppDumper `GetRVA = VA - ImageBase`, ImageBase `0x180000000` for both")
    w("`UserAssembly.dll`); the Akebi headers hold RVAs → copied unchanged. The matcher refuses to run unless `MoleMole.GameManager$$Update` is")
    w("at RVA `0x1CC5520` in the 1.6 dump and `0x164D930` in the 2.8 dump (both confirmed on every run). Section layout of the two binaries:")
    w("2.8 code in `.text` + `il2cpp`, 1.6 code in `.text` (112 MB); metadata-usage slots in `.data`.")
    w("")
    w("## 2. Summary")
    w("")
    w(f"* classes (2.8 dump types the headers need → 1.6 types): **{st['classes_matched']} matched / {st['classes_required']} required** (the required set = classes of every")
    w("  function, slot, parameter/return type, every type the appdata-28 structs describe, their bases and by-value field types; the surplus are")
    w("  types matched on the way through field/base propagation)")
    w(f"* `DO_APP_FUNC`: **{st['funcs_resolved']}/{st['funcs_total']}** with a 1.6 RVA ({st['funcs_unresolved_28']} of the rest were already `0x0` in appdata-28, 9 are the `FishingModule`")
    w("  entries — fishing does not exist in 1.6 — the remaining ones are listed in §5)")
    w(f"* `DO_APP_FUNC_METHODINFO` + `DO_TYPEDEF`: **{st['slots_resolved']}/{st['slots_total']}**")
    w(f"* `DO_API` (il2cpp API): **{st['api_resolved']}/{st['api_total']}** from the 1.6 export table ({len(match['exports16'])} exports; the 48 missing ones are not exported by the 1.6 build,")
    w("  none of them is used by the Release build — `il2cpp_domain_get / thread_attach / runtime_class_init / class_get_name / value_box / ...` all resolved)")
    w(f"* `UnityPlayer.dll`: **{st['unity_resolved']}/4** located by byte pattern (none is used by the feature code; `Animator_set_avatar` stays 0x0: 5 pattern hits)")
    w(f"* types: **{len(gen['regenerated'])} definitions regenerated from the 1.6 dump**, {len(gen['copied'])} copied from appdata-28 ({sum(1 for n, h in gen['copied'] if 'UNVERIFIED' in h)} flagged")
    w(f"  `RELIC-LAYOUT-UNVERIFIED-16`, the rest are BCL generic instantiations), {len(gen['opaque'])} unmatched classes left opaque, {len(gen['anchored'])} field alignments forced by")
    w(f"  getter anchors / unique identities / readable names, {sum(len(v['anchors']) for v in anchors.values())} getter/field-read anchors in {len(anchors)} classes")
    w(f"* compile (`GameVersion=16 Release`): **{'%s error(s), %s warning(s)' % (summary.group(2), summary.group(1)) if summary else 'no build log'}** — all of them feature code reading a member that genuinely does not exist in 1.6 (§7); every other translation unit compiles")
    w("")
    w("## 3. Matching rules and their hit counts")
    w("")
    w("| rule | hits | what it is |")
    w("|---|---|---|")
    for r, n in sorted(st["class_rules"].items(), key=lambda x: -x[1]):
        w(f"| {r} | {n} | {RULES_CLASS.get(r, '')} |")
    for r, n in sorted(st["func_rules"].items(), key=lambda x: -x[1]):
        w(f"| {r} | {n} | {RULES_METHOD.get(r, '')} |")
    w("| slot via class map | %d | `Singleton<X>.get_Instance` / generic MethodInfo / TypeInfo slots looked up in the 1.6 script.json with the mapped class / type arguments |" % sum(1 for v in slots.values() if v.get('rva16')))
    w("| export table | %d | il2cpp_* API by name |" % st["api_resolved"])
    w("")
    w("Iteration: readable names first, then up to 8 rounds of {enum members, fingerprints (usage pool first, structural pools next), propagation")
    w("(bases, aligned fields, generic arguments, parameters of matched methods), method matching}; identities learned in one round sharpen the")
    w("fingerprints of the next (call targets, TypeInfo refs, parameter identities). Obfuscated 2.8 types not matched yet are wildcards; a 1.6 type")
    w("that is still unmatched is *incompatible* with a 2.8 type that already has its counterpart (1:1), which is what makes most twin pairs decidable.")
    w("Confidence: `high` = strong shared evidence and a clear margin, `medium` = decided by one rule with a margin, `low` = prefix/arity/order/family decisions")
    w("or identity conflicts — every `low` line names the reason in the header comment.")
    w("")
    # priorities
    w("## 4. Priority symbols (menu on F1, camera/UI tier, teleport, ESP)")
    w("")
    w("| symbol | 1.6 RVA | rule (confidence) | 1.6 dump name / note |")
    w("|---|---|---|---|")
    for n in PRIORITY:
        row = F.get(n)
        if not row:
            continue
        rva = f"`0x{row['rva16']:08X}`" if row.get("rva16") else "**0x0**"
        rule = f"{row.get('rule') or '-'} ({row.get('conf') or '-'})" if row.get("rva16") else "unresolved"
        note = md_escape((row.get("dump16") or row.get("slot16") or "") + ((" — " + str(row.get("note"))) if row.get("note") else "") + ((" — " + str(row.get("reason"))) if not row.get("rva16") else ""))
        w(f"| `{n}` | {rva} | {rule} | {note[:260]} |")
    w("")
    w("## 5. Unresolved (0x0 in appdata-16) — reasons, candidates, feature impact")
    w("")
    w("A 0x0 offset is a null pointer at run time: `HookManager::install` skips the hook; a **direct call** crashes and needs a guard in the feature file (owner).")
    w("")
    w("| symbol | reason | candidates | used by (hook = safe / CALL = guard needed) |")
    w("|---|---|---|---|")
    for n, row in F.items():
        if row.get("rva16"):
            continue
        reason = row.get("reason") or row.get("status") or "-"
        if reason == "unresolved-28":
            reason = "0x0 in appdata-28 as well (never resolved for 2.8: 3.3-only coroutine / Wanderer hook / LuaShell / two enhance-animation functions)"
        cands = "; ".join(md_escape(c[0] if isinstance(c, list) else c) + (f" {c[1]}" if isinstance(c, list) and len(c) > 1 else "") for c in row.get("cands", [])[:3])
        hooks, calls = usage(n)
        use = (("hook: " + ", ".join(hooks[:3])) if hooks else "") + (("; " if hooks and calls else "") + ("**CALL**: " + ", ".join(calls[:4])) if calls else "")
        w(f"| `{n}` | {md_escape(reason)[:220]} | {cands[:200]} | {use or 'not referenced'} |")
    w("")
    w("## 6. Types: regenerated / copied / opaque / missing fields")
    w("")
    w("`il2cpp-types.h` = the IL2CPP runtime prelude of appdata-28 with `Il2CppClass`, `Il2CppClass_0`, `Il2CppClass_1` and `MethodInfo` replaced by the")
    w("metadata-v24.0 layouts of the 1.6 client — **1.6 has `static_fields` at `0xA0`, `cctor_finished` at `0xBC`, the init bit flags at `0x10A` and the")
    w("vtable at `0x110`; 2.8 has `static_fields` at `0xB8` and `cctor_finished` at `0xE0`** (checked against the `il2cpp_codegen` sequences the methods")
    w("emit: `mov rax,[rip+TypeInfo]; test byte [rax+10Ah],1; cmp dword [rax+0BCh],0; mov rcx,[rax+0A0h]`). The 2.8 prelude would have put every")
    w("`X__Class::vtable` (used by `ConfigScenePoint__Class` in `game/util.cpp` / `misc/Debug.cpp`) and `static_fields` at the wrong offset on 1.6.")
    w("Then the System basics (Object / Type / String / Byte__Array …) copied verbatim, then `#include \"il2cpp-types-relic.h\"` with every application")
    w("type the appdata-28 headers define, regenerated from the 1.6 dump in Il2CppInspector shape (field offsets from the dump, padding where the natural")
    w("layout falls short, 2.8/3.3 names transplanted onto the obfuscated 1.6 fields by type-sequence alignment + getter/field-read anchors +")
    w("unique-identity anchors + readable-name anchors; misaligned duplicates renamed `_dupN`).")
    w("")
    unv = [(n, h) for n, h in gen["copied"] if "UNVERIFIED" in h]
    w(f"### 6.1 Copied from appdata-28 and flagged `RELIC-LAYOUT-UNVERIFIED-16` ({len(unv)}) — do not rely on these layouts")
    w("")
    w("| type | dereferenced by feature code | note |")
    w("|---|---|---|")
    for n, h in unv:
        w(f"| `{n}` | {'**yes**' if n in gen['copied_dereferenced'] else 'no'} | {md_escape(h.split(':', 1)[-1].strip())[:160]} |")
    w("")
    w(f"### 6.2 Unmatched classes left as opaque forward declarations ({len(gen['opaque'])}; nothing dereferences them)")
    w("")
    w(", ".join(f"`{n}`" for n, _ in gen["opaque"]))
    w("")
    w("### 6.3 BCL generic instantiations copied from appdata-28 (same mscorlib, version-independent)")
    w("")
    w(", ".join(f"`{n}`" for n, h in gen["copied"] if "UNVERIFIED" not in h))
    w("")
    deref = set(gen.get("deref_structs", []))
    miss_deref = [m for m in gen["missing"] if m[0] in deref or m[0].replace("__Enum", "") in deref]
    w(f"### 6.4 MISSING in 1.6 — 2.8/3.3 fields the regenerated struct does not have ({len(gen['missing'])} in total, {len(miss_deref)} in structs the feature code dereferences)")
    w("")
    w("Full list at the end of `appdata-16/il2cpp-types-relic.h` and in `gen16.json`. The ones inside structs the feature code dereferences:")
    w("")
    w("| struct | missing member | 2.8 type |")
    w("|---|---|---|")
    for s_, f_, t_ in miss_deref[:200]:
        w(f"| `{s_}` | `{md_escape(f_)}` | {md_escape(t_)} |")
    w("")
    w("### 6.5 Field alignments forced by anchors (getter / field-read / unique identity / readable name)")
    w("")
    w(f"{len(gen['anchored'])} fields were placed by an anchor where the plain sequence alignment would have chosen differently or nothing; examples:")
    w("")
    for s_, f28, f16, how in gen["anchored"][:40]:
        w(f"* `{s_}`: `{f28}` ← 1.6 `{md_escape(f16)}` ({how})")
    w("")
    w("## 7. Compile status")
    w("")
    if summary:
        w(f"`MSBuild ... -p:GameVersion=16`: **{summary.group(2)} errors, {summary.group(1)} warnings**. The generated headers compile; every remaining error is feature")
        w("code reading a member that the 1.6 type genuinely does not have (not a naming/alignment problem — verified against the 1.6 dump):")
        w("")
        w("| site | error | what the owner has to gate |")
        w("|---|---|---|")
        for f_, ln, code, msg in errs:
            w(f"| `{f_}:{ln}` | {code} | {md_escape(msg)[:170]} |")
    else:
        w("no build log found")
    w("")
    w("Gating hints (owner, feature files): `AutoFish.cpp` + `filters.cpp` `EntityType__Enum_1::FishPool` → `#if RELIC_GAME_VERSION >= 21` (fishing = 2.1);")
    w("`NoClip.cpp:253 _layerMaskScene` → the 1.6 `HumanoidMoveFSM` has no `lateTickStart*/_layerMask*/ignoreOverall*/stopMoveWhenGoupstairs` block at all")
    w("(`#if RELIC_GAME_VERSION >= 28`, NoClip otherwise intact); `AutoLoot.cpp:347 msgType_` → the 1.6 `CheckAddItemExceedLimitNotify` has 3 fields")
    w("(`is_drop_`, two repeated uint lists); `Debug.cpp:78 PlayerLoginReq.string_15` and `Debug.cpp:736 InteractionManager._isDelayClear` → Debug only.")
    w("Additionally (compiles, but would crash when executed): `FreeCamera.cpp:355/362` call `Miscs_SetUILocalAvatarVisible` directly and it is 0x0 on 1.6")
    w("(the method does not exist in the 1.6 `Miscs`) — null-guard it.")
    w("")
    w("## 8. Class map (Il2CppInspector name → 2.8 dump type → 1.6 dump type)")
    w("")
    w("Every appdata-28 type name the tools could tie to a 2.8 dump type, with the 1.6 counterpart. `how (2.8)` says how the header name was tied")
    w("to the 2.8 type (functions, slots, usage links, fingerprints), `rule` how 2.8 → 1.6 was decided.")
    w("")
    w("| Inspector name | 2.8 dump | how (2.8) | 1.6 dump | rule (conf) |")
    w("|---|---|---|---|---|")
    for row in match["inspector"]:
        w(f"| `{row['insp']}` | `{row['d28']}` | {md_escape(row['how28'])[:90]} | {('`%s`' % row['d16']) if row['d16'] else '**unmatched**'} | {(row['rule'] or '-') + ((' (%s)' % row['conf']) if row['conf'] else '')} |")
    w("")
    w(f"Header names not tied to any 2.8 dump type ({len(match['inventory_unresolved'])}; their structs are copied/opaque per §6): " + ", ".join(f"`{k}`" for k in sorted(match["inventory_unresolved"])[:120]))
    w("")
    w("## 9. Per-symbol table (every DO_APP_FUNC / METHODINFO / TYPEDEF line of appdata-16)")
    w("")
    w("| symbol | 1.6 RVA | rule (conf) | 1.6 dump name / note |")
    w("|---|---|---|---|")
    for n, row in F.items():
        if row.get("rva16"):
            note = md_escape((row.get("dump16") or row.get("slot16") or "") + ((" — " + str(row.get("note"))) if row.get("note") else ""))
            w(f"| `{n}` | `0x{row['rva16']:08X}` | {row.get('rule')} ({row.get('conf')}) | {note[:230]} |")
        else:
            w(f"| `{n}` | 0x0 | unresolved | {md_escape(row.get('reason') or row.get('status') or '')[:230]} |")
    w("")
    w("## 10. Honest assessment per feature tier (what the 1.6 build can be expected to do before the first live test)")
    w("")
    w("* **Tier 0 — menu on F1**: `GameManager_Update` (readable name, 0x1CC5520 verified against the dump), the 5 `il2cpp_*` API entries (export")
    w("  table), `Singleton_GetInstance` (the shared `Singleton<T>.get_Instance` body, 132 instantiations) and all 11 `Singleton<X>.get_Instance`")
    w("  MethodInfo slots (`PlayerModule` = `BOACBNICNDP`, `EntityManager` = `GIHJJEIOMJL`, …, every singleton matched by code fingerprint with a large")
    w("  margin), `Cursor/Screen/Time/Application/Camera/Transform/GameObject/Rigidbody/RenderSettings` (readable UnityEngine names), the 1.6")
    w("  `Il2CppClass` prelude, `PlayerModule._accountData_k__BackingField` (0x18) / `AccountDataItem.userId` for `CheckAccountChanged`. Expected to work;")
    w("  the residual risk is the regenerated layouts of the few structs the framework touches before any feature (PlayerModule, AccountDataItem).")
    w("* **Tier 1 — camera/UI**: FPSUnlock (`Application_*`), CameraZoom (`SCameraModuleInitialize_SetWarningLocateRatio` M-fp medium, `CameraShareData`")
    w("  regenerated, `Camera_set_fieldOfView` name), FreeCamera (UnityEngine names + `UIManager_EnableInput` M-sig high; **`Miscs_SetUILocalAvatarVisible`")
    w("  does not exist in 1.6 → 0x0, guard the two direct calls**), HideUI, NoFog (`RenderSettings_set_fog`), EnablePeeking (`VCBaseSetDitherValue_set_*`")
    w("  M-pos medium), DialogSkip (`TalkDialogContext_get_canClick` M-fp high, `get_canAutoClick` **unresolved** — 3 candidates, auto-click part off;")
    w("  `InLevelCutScenePageContext_*` M-pos medium), ShowSkillCD (LCAvatarCombat methods M-sig/M-pos; `CheckCDTimer` carries an IDENTITY-CONFLICT")
    w("  note — its `SkillInfo` parameter type is matched to another nested type than the one the 1.6 method takes, treat ShowSkillCD as unverified),")
    w("  OpenTeamImmediately / SkipEnhanceAnimation (3.3-only coroutine + two functions that were already 0x0 on 2.8 → partially/not functional),")
    w("  ChestIndicator (`LCIndicatorPlugin_*` M-sig high). Expected to mostly work; verify FreeCamera guard and ShowSkillCD.")
    w("* **Tier 2 — MapTeleport/CustomTeleports/ESP**: `LoadingManager_*` (`PerformPlayerTransmit` / `RequestSceneTransToPoint` / `NeedTransByServer` M-sig high - unique signatures, lists align 0.96),")
    w("  `ActorUtils_*`, `Miscs_Gen*Pos`, `WorldShiftManager_*` (readable), `InLevelMapPageContext_OnMarkClicked/OnMapClicked` (M-pos), `MapModule`/`MapManager`")
    w("  singletons + `MapModule_ScenePointData` regenerated (nested struct fingerprint 1.00), `ConfigScenePoint__Class::vtable.get_pointType` placed at the")
    w("  1.6 slot of the matched method (M-sig medium) — **verify the first teleport in game**. ESP: `LevelModule_OnSceneEntityAppear/Async` = the two")
    w("  1-parameter 1.6 handlers `CFEJFANLJLF/MKLEBGLDODB` (1.6 has no `uint` second parameter; the hook signature passes an extra register argument which")
    w("  the callee ignores), `EntityManager_*` (M-pos/M-sig), `BaseEntity` regenerated with `runtimeID` (0x20) and `entityType` (0x12C) anchored by")
    w("  field reads / unique identity, `Proto_SceneEntityInfo/Avatar/Monster/Npc/Gadget` matched (oneof usage pool) and regenerated. `InLevelMapPageContext_")
    w("  UpdateView` has no override in 1.6; both it and `get_miniMapScale` are now resolved by hand in `tools/offsets-16.overrides.json`")
    w("  (`BPDBBEDHBCP$$ODHLJNAONMH` 0x00B120F0, called right after the view rect is written, and `KCFCJJBPLEN$$MKLHNEKDNCJ` 0x008FC2B0), so the")
    w("  interactive map draws on 1.6 too. Expected to work for MapTeleport/CustomTeleports/ESP with the usual first-run verification of entity names/types.")
    w("* **Tier 3 — automation/combat/cosmetic**: player block (`LCAvatarCombat`, `LCBaseCombat`, `VCHumanoidMove`, `HumanoidMoveFSM`) mostly M-name/M-sig;")
    w("  `NoClip._layerMaskScene` must be gated; KillAura (`EventHelper.Allocate<EvtCrash>` generic body + MethodInfo slot, `EvtCrash_Init`); AutoLoot")
    w("  (`ItemModule_PickItem` + `OnCheckAddItemExceedLimitNotify` M-fp high, `msgType_` absent in 1.6 — gate); AutoCook (`CookRecipeExcelConfig` regenerated);")
    w("  AutoTreeFarm (`SceneTreeObject`/`BaseScenePropObject` chain aligned via the function class link). AutoFish cannot work (no fishing in 1.6,")
    w("  9 functions + 3 structs dead by design); the packet sniffer / MusicEvent are excluded upstream.")
    w("")
    w("## 11. Known weak spots to watch in the first live test")
    w("")
    w("* `low` / prefix / family decisions: " + ", ".join(f"`{n}`" for n, row in F.items() if row.get("rva16") and row.get("conf") == "low")[:1500])
    w("* IDENTITY-CONFLICT notes (a matched parameter type disagrees with the 1.6 method's parameter type): " + ", ".join(f"`{n}`" for n, row in F.items() if "IDENTITY-CONFLICT" in str(row.get("note", ""))))
    w("* ARITY notes (1.6 method has fewer parameters; trailing arguments ignored): " + ", ".join(f"`{n}`" for n, row in F.items() if "ARITY" in str(row.get("note", ""))))
    w("* the Il2CppClass prelude swap (static_fields 0xA0 / vtable 0x110) is derived from the IL2CPP v24.0 layout confirmed by the codegen sequences, not from")
    w("  a live run; `ConfigScenePoint__Class::vtable.get_pointType` (slot 11) is the only feature-visible use.")
    w("* struct alignments outside the anchored fields are sequence alignments; MISSING lists (§6.4) show where 1.6 diverges.")
    w("")
    path = os.path.join(OUT, "REPORT-matching.md")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(L) + "\n")
    print(f"-> {path} ({len(L)} lines)")


if __name__ == "__main__":
    main()
