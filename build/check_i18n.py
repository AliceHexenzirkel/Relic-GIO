#!/usr/bin/env python3
"""i18n housekeeping for the Relic UI + Core.

  python build/check_i18n.py            # validate + regenerate ui/lang/en.js from en.json
  python build/check_i18n.py --merge    # also merge ui/lang/fragments/*.en.json / *.ro.json into
                                        #   en.json / ro.json.bak first (duplicate keys must agree)
  python build/check_i18n.py --strict   # exit 1 on any warning (used by publish.ps1)

Checks: en.json parses and is flat {key: string}; every t('...') / T('...') / tf('...') literal key
in ui/*.js and every L.T("...") literal key in app/**/*.cs exists in en.json; lang/index.json lists
'en', every listed file exists, no '.bak' code; other languages have no keys unknown to en.json;
ui/lang/en.js matches en.json (it is regenerated here).
"""
import glob
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LANG = os.path.join(ROOT, "app", "Relic.App", "ui", "lang")
UI = os.path.join(ROOT, "app", "Relic.App", "ui")
APP = os.path.join(ROOT, "app")

strict = "--strict" in sys.argv
merge = "--merge" in sys.argv
warnings = []
errors = []


def warn(msg):
    warnings.append(msg)


def err(msg):
    errors.append(msg)


def load_flat(path):
    with open(path, encoding="utf-8-sig") as f:
        data = json.load(f)
    if not isinstance(data, dict):
        raise ValueError(f"{path}: root must be an object")
    for k, v in data.items():
        if not isinstance(k, str) or not isinstance(v, str):
            raise ValueError(f"{path}: key {k!r} must map to a string")
    return data


def dump_flat(path, data):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(dict(sorted(data.items())), f, ensure_ascii=False, indent=2)
        f.write("\n")


def do_merge():
    frag_dir = os.path.join(LANG, "fragments")
    if not os.path.isdir(frag_dir):
        return
    for lang, target in (("en", os.path.join(LANG, "en.json")), ("ro", os.path.join(LANG, "ro.json.bak"))):
        merged = load_flat(target) if os.path.exists(target) else {}
        for frag in sorted(glob.glob(os.path.join(frag_dir, f"*.{lang}.json"))):
            part = load_flat(frag)
            for k, v in part.items():
                if k in merged and merged[k] != v:
                    err(f"merge {lang}: key {k!r} differs between {os.path.basename(frag)} and the merged file")
                merged[k] = v
        dump_flat(target, merged)
        print(f"merged {lang}: {len(merged)} keys -> {os.path.relpath(target, ROOT)}")


def main():
    if merge:
        do_merge()
    en_path = os.path.join(LANG, "en.json")
    try:
        en = load_flat(en_path)
    except Exception as e:  # noqa: BLE001
        print(f"ERROR: {e}")
        return 1

    # index.json
    idx_path = os.path.join(LANG, "index.json")
    try:
        with open(idx_path, encoding="utf-8-sig") as f:
            idx = json.load(f)
        codes = [e.get("code") for e in idx if isinstance(e, dict)]
        if "en" not in codes:
            err("index.json: 'en' missing")
        for c in codes:
            if not isinstance(c, str) or c.endswith(".bak"):
                err(f"index.json: invalid code {c!r}")
            elif not os.path.exists(os.path.join(LANG, c + ".json")):
                err(f"index.json: lang/{c}.json does not exist")
    except Exception as e:  # noqa: BLE001
        err(f"index.json: {e}")

    # other languages
    for path in glob.glob(os.path.join(LANG, "*.json")):
        name = os.path.basename(path)
        if name in ("en.json", "index.json"):
            continue
        try:
            other = load_flat(path)
        except Exception as e:  # noqa: BLE001
            err(f"{name}: {e}")
            continue
        unknown = sorted(k for k in other if k not in en)
        missing = sorted(k for k in en if k not in other)
        if unknown:
            err(f"{name}: {len(unknown)} keys unknown to en.json, e.g. {unknown[:5]}")
        if missing:
            warn(f"{name}: {len(missing)} keys missing (fall back to English), e.g. {missing[:5]}")

    # keys used by the JS and C# code
    used = set()
    js_re = re.compile(r"""\b(?:t|T|tf|tk)\(\s*(['"])([A-Za-z0-9_.\-]+)\1""")
    for path in glob.glob(os.path.join(UI, "*.js")):
        if os.path.basename(path) in ("en.js", "i18n.js"):
            continue
        with open(path, encoding="utf-8-sig") as f:
            for m in js_re.finditer(f.read()):
                used.add(m.group(2))
    cs_re = re.compile(r"""\bL\.T\(\s*"([A-Za-z0-9_.\-]+)\"""")
    for path in glob.glob(os.path.join(APP, "**", "*.cs"), recursive=True):
        if os.sep + "obj" + os.sep in path or os.sep + "bin" + os.sep in path:
            continue
        with open(path, encoding="utf-8-sig") as f:
            for m in cs_re.finditer(f.read()):
                used.add(m.group(1))
    missing_keys = sorted(k for k in used if k not in en)
    if missing_keys:
        err(f"{len(missing_keys)} keys used in code are missing from en.json: {missing_keys[:20]}")

    # regenerate en.js (embedded English for the UI)
    en_js = os.path.join(LANG, "en.js")
    body = "// GENERATED by build/check_i18n.py from en.json - do not edit by hand.\n" \
           "window.RELIC_EN = " + json.dumps(dict(sorted(en.items())), ensure_ascii=False, indent=1) + ";\n"
    old = open(en_js, encoding="utf-8-sig").read() if os.path.exists(en_js) else None
    if old != body:
        with open(en_js, "w", encoding="utf-8", newline="\n") as f:
            f.write(body)
        print(f"regenerated lang/en.js ({len(en)} keys)")

    for w in warnings:
        print("WARN:", w)
    for e in errors:
        print("ERROR:", e)
    print(f"i18n: {len(en)} English keys, {len(used)} keys referenced in code, {len(warnings)} warnings, {len(errors)} errors")
    if errors or (strict and warnings):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
