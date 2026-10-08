#!/usr/bin/env python3
"""Small helper around payload/manifest.json (the file build/publish.ps1 verifies):

  payload_manifest.py --set-sha <src> <sha256>   write the hash of an existing entry (matched by its "src")
  payload_manifest.py --verify                   recompute every entry's sha256 from payload/<src> and report
  payload_manifest.py --list                     print src -> dst (sha, flags) for every entry

Edits are targeted text substitutions on the entry's own "sha256" value, so the hand-written notes,
key order and formatting of the manifest survive untouched. Exit code 1 when an entry is missing or a
hash does not match.
"""
import hashlib, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
MANIFEST = os.path.join(REPO, "payload", "manifest.json")

def load():
    with open(MANIFEST, "r", encoding="utf-8") as f:
        return f.read()

def entries(text):
    data = json.loads(text)
    out = list(data.get("common", []))
    for ver, lst in data.get("versions", {}).items():
        out.extend(lst)
    return out

def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

def set_sha(src, sha):
    text = load()
    if not any(e.get("src") == src for e in entries(text)):
        print(f"manifest: no entry with src={src!r} - add it first (see docs/ENTERTAINMENT-EXPERIENCE.md)")
        return 1
    # find the object containing "src": "<src>" and replace its sha256 value
    pat = re.compile(r'("src"\s*:\s*"' + re.escape(src) + r'".*?"sha256"\s*:\s*")([0-9a-fA-F]*)(")', re.S)
    new, n = pat.subn(lambda m: m.group(1) + sha.lower() + m.group(3), text, count=1)
    if n != 1:
        print(f"manifest: entry {src!r} has no sha256 field to update")
        return 1
    with open(MANIFEST, "w", encoding="utf-8", newline="") as f:
        f.write(new)
    print(f"manifest: {src} sha256 = {sha.lower()}")
    return 0

def verify():
    text = load(); bad = 0
    for e in entries(text):
        p = os.path.join(REPO, "payload", e["src"].replace("/", os.sep))
        if not os.path.exists(p):
            print(f"MISSING  {e['src']}"); bad += 1; continue
        got = sha256_of(p)
        ok = e.get("sha256", "").lower() == got
        print(f"{'ok     ' if ok else 'MISMATCH'}  {e['src']}  {got}")
        bad += 0 if ok else 1
    return 1 if bad else 0

def listing():
    for e in entries(load()):
        flags = ",".join(k for k in ("required", "backup", "inject") if e.get(k))
        print(f"{e['src']} -> {e['dst']}  [{e.get('sha256','')[:12]}...] {flags}")
    return 0

if __name__ == "__main__":
    a = sys.argv[1:]
    if len(a) == 3 and a[0] == "--set-sha":
        sys.exit(set_sha(a[1], a[2]))
    if a == ["--verify"]:
        sys.exit(verify())
    if a == ["--list"]:
        sys.exit(listing())
    print(__doc__); sys.exit(2)
