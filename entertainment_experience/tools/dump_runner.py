#!/usr/bin/env python3
"""Produces the IL2CPP dump of one Genshin client under dumps_ref/<ver>/ with a Genshin-aware Il2CppDumper:

    dump_runner.py --ver 28 --assembly "D:\\...\\UserAssembly.dll" --metadata "D:\\...\\global-metadata.dat.relic-orig"
                   [--tool-dir dumps_ref/tools] [--force] [--skip-build]

What it does (idempotent - every step is skipped when its result already exists, --force redoes the dump):
  1. git clone https://github.com/R0xdeadc0de/Il2CppDumper-Genshin and check out the pinned commit
     731c4022a15b126da993e54a9f7d7e2c8a06c1af (the metadata-2.7+ family: forced "27.1" metadata, AES round keys
     read from the file's own 0x4000 trailer, custom string-literal decryption).
  2. dotnet build (the installed .NET SDK; the csproj targets net472;net5.0 which the 8/10 SDKs cannot build, so the
     build is retargeted from the command line: -p:TargetFrameworks=net8.0 -p:AllowUnsafeBlocks=true; the WinForms
     file dialog is #if NETFRAMEWORK only, so the CLI build has no UI). Output: <tool-dir>/Il2CppDumper-Genshin/out-net8/.
  3. config.json of the build output: RequireAnyKey=false (no "Press any key" prompt), everything else as shipped
     (DumpMethod/DumpField/DumpProperty/DumpAttribute/DumpFieldOffset/DumpMethodOffset/DumpTypeDefIndex/
     GenerateDummyDll/GenerateStruct/DummyDllAddToken=true, ForceIl2CppVersion=false).
  4. Il2CppDumper.exe <UserAssembly.dll> <global-metadata.dat> <outdir>  with stdin closed (< NUL) so that any
     interactive prompt fails loudly instead of hanging. The 2.8 client needs no prompt: the PE section search
     finds CodeRegistration/MetadataRegistration by itself (CodeRegistration 0x1883583AC0-ish VA, see the log);
     the custom PE loader / manual registration inputs are not needed.
  5. Validation: dump.cs has MoleMole.* classes and readable Unity names; script.json has ScriptMethod entries
     with addresses and ScriptMetadata *_TypeInfo entries; stringliteral.json decodes as UTF-8 without U+FFFD.
     The log goes to dumps_ref/<ver>/dump_runner.log.

Address convention (verified): script.json "Address" = RVA (GetRVA() = VA - ImageBase); the PE ImageBase of the 2.8
UserAssembly.dll is 0x180000000. transplant.py uses the values unchanged.
Stdlib only.
"""
import argparse, json, os, re, shutil, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
EE = os.path.normpath(os.path.join(HERE, ".."))
DUMPS = os.path.join(EE, "dumps_ref")
REPO = "https://github.com/R0xdeadc0de/Il2CppDumper-Genshin"
COMMIT = "731c4022a15b126da993e54a9f7d7e2c8a06c1af"
VER_DIR = {"28": "2.8", "16": "1.6", "33": "3.3"}


def run(cmd, cwd=None, log=None, stdin_null=False, check=True):
    line = " ".join(f'"{c}"' if " " in c else c for c in cmd)
    msg = f"$ {line}"
    print(msg)
    if log:
        log.write(msg + "\n")
    p = subprocess.run(cmd, cwd=cwd, stdin=subprocess.DEVNULL if stdin_null else None,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
    if log:
        log.write(p.stdout + "\n")
    if p.returncode != 0 and check:
        print(p.stdout)
        raise SystemExit(f"command failed ({p.returncode}): {line}")
    return p.stdout


def ensure_tool(tool_dir, skip_build, log):
    src = os.path.join(tool_dir, "Il2CppDumper-Genshin")
    if not os.path.isdir(os.path.join(src, ".git")):
        os.makedirs(tool_dir, exist_ok=True)
        run(["git", "clone", "-q", REPO, src], log=log)
    head = run(["git", "rev-parse", "HEAD"], cwd=src, log=log).strip()
    if head != COMMIT:
        run(["git", "fetch", "-q", "origin"], cwd=src, log=log, check=False)
        run(["git", "checkout", "-q", COMMIT], cwd=src, log=log)
    out = os.path.join(src, "out-net8")
    exe = os.path.join(out, "Il2CppDumper.exe")
    if not os.path.exists(exe) or not skip_build:
        if not os.path.exists(exe):
            run(["dotnet", "build", os.path.join(src, "Il2CppDumper", "Il2CppDumper.csproj"), "-c", "Release",
                 "-p:TargetFrameworks=net8.0", "-p:TargetFramework=net8.0", "-p:AllowUnsafeBlocks=true",
                 "-o", out, "-nologo", "-v:minimal"], log=log)
    cfg_path = os.path.join(out, "config.json")
    cfg = json.load(open(cfg_path, encoding="utf-8-sig"))
    if cfg.get("RequireAnyKey", True):
        cfg["RequireAnyKey"] = False
        json.dump(cfg, open(cfg_path, "w", encoding="utf-8"), indent=2)
    return exe


def validate(outdir, log):
    problems = []
    dump_cs = os.path.join(outdir, "dump.cs")
    with open(dump_cs, "r", encoding="utf-8", errors="replace") as f:
        head = f.read()
    n_mole = len(re.findall(r'^// Namespace: MoleMole$', head, re.M))
    n_cls = len(re.findall(r'^(?:public|private|internal) (?:sealed |abstract |static )*class ', head, re.M))
    if n_mole < 100:
        problems.append(f"dump.cs: only {n_mole} MoleMole namespace blocks")
    if not re.search(r'^public class GameManager\b', head, re.M):
        problems.append("dump.cs: MoleMole.GameManager not found")
    n_obf = len(re.findall(r'^(?:public|private|internal) (?:sealed |abstract |static )*class [A-Z]{11}\b', head, re.M))
    print(f"validation: {n_cls} classes, {n_obf} with a BeeByte-obfuscated name, {n_mole} MoleMole namespace blocks")
    sj = os.path.join(outdir, "script.json")
    with open(sj, "r", encoding="utf-8") as f:
        s = f.read(4 * 1024 * 1024)
    if '"ScriptMethod"' not in s or not re.search(r'"Address":\s*\d+,\s*"Name":\s*"[^"]+\$\$', s):
        problems.append("script.json: no ScriptMethod entries with addresses")
    sl = os.path.join(outdir, "stringliteral.json")
    with open(sl, "r", encoding="utf-8") as f:
        lit = f.read()
    bad = lit.count("\ufffd")
    n_lit = lit.count('"address"')
    if n_lit < 1000 or bad > n_lit // 100:
        problems.append(f"stringliteral.json: {n_lit} literals, {bad} replacement chars (mojibake?)")
    for fn in ("il2cpp.h", "DummyDll"):
        if not os.path.exists(os.path.join(outdir, fn)):
            problems.append(f"missing {fn}")
    summary = f"validation: dump.cs classes={n_cls} (obfuscated {n_obf}) MoleMole blocks={n_mole}, string literals={n_lit} (bad={bad})"
    print(summary)
    log.write(summary + "\n")
    for p in problems:
        print("PROBLEM:", p)
        log.write("PROBLEM: " + p + "\n")
    return not problems


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ver", required=True, choices=sorted(VER_DIR))
    ap.add_argument("--assembly", required=True, help="UserAssembly.dll of the client")
    ap.add_argument("--metadata", required=True, help="global-metadata.dat (the unpatched .relic-orig for 2.8)")
    ap.add_argument("--tool-dir", default=os.path.join(DUMPS, "tools"))
    ap.add_argument("--force", action="store_true", help="re-run the dump even if dumps_ref/<ver>/dump.cs exists")
    ap.add_argument("--skip-build", action="store_true", help="do not rebuild the dumper when the exe exists")
    a = ap.parse_args()
    outdir = os.path.join(DUMPS, VER_DIR[a.ver])
    os.makedirs(outdir, exist_ok=True)
    for p in (a.assembly, a.metadata):
        if not os.path.isfile(p):
            raise SystemExit(f"not found: {p}")
    with open(os.path.join(outdir, "dump_runner.log"), "a", encoding="utf-8") as log:
        log.write(f"\n==== {time.strftime('%Y-%m-%d %H:%M:%S')} dump_runner --ver {a.ver}\n")
        exe = ensure_tool(a.tool_dir, a.skip_build, log)
        done = all(os.path.exists(os.path.join(outdir, f)) for f in ("dump.cs", "script.json", "il2cpp.h", "stringliteral.json"))
        if done and not a.force:
            print(f"dump already present in {outdir} (use --force to redo)")
        else:
            t0 = time.time()
            out = run([exe, a.assembly, a.metadata, outdir], cwd=os.path.dirname(exe), log=log, stdin_null=True)
            print(out)
            if "Done!" not in out or "ERROR" in out:
                raise SystemExit("dumper did not finish cleanly - see dump_runner.log")
            print(f"dump finished in {time.time() - t0:.0f}s -> {outdir}")
        ok = validate(outdir, log)
        # sizes for the report
        for f in sorted(os.listdir(outdir)):
            p = os.path.join(outdir, f)
            if os.path.isfile(p):
                print(f"  {f:<24} {os.path.getsize(p):>12,} bytes")
        sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
