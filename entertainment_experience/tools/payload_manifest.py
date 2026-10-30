#!/usr/bin/env python3
"""Small helper around payload/manifest.json (the file build/publish.ps1 verifies):

  payload_manifest.py --set-sha <src> <sha256>   write the hash of an existing entry (matched by its "src")
  payload_manifest.py --verify                   recompute every entry's sha256 from payload/<src> and report
  payload_manifest.py --list                     print src -> dst (kind, sha, flags) for every entry
  payload_manifest.py --prove <game dir> [<version>]
                                                 prove the "bytepatch" entries (of <version>, else of every
                                                 version) on an unmodified copy of the game; nothing is written

  --payload <dir>, before any of them, works on <dir>/manifest.json instead of the repository's payload/.

A plain entry that carries "sha256Before" REPLACES a file of the game: the launcher writes it only over
that original (kept beside it, "backup": true) or over an earlier payload file of its own, whose hashes
"sha256Earlier" lists; any other content is the player's own file and stays. --set-sha on such an entry
moves the hash it replaces into "sha256Earlier", so the players who have the earlier build get the new
one. --verify checks the shape: two different sha256 values, "backup" true, "sha256Earlier" a list of
other sha256 values.

An entry of kind "bytepatch" ships only the bytes Relic writes into a file the player's game folder
already holds: payload/<src> is that span ("length" bytes, hash "spanSha256") and "sha256" is the hash
of the WHOLE game file with the span in it, which only a copy of the game can confirm. --verify checks
the span file; --prove reads <game dir>/<dst> once and takes three hashes in that one pass: the file as
it is, its own bytes of [offset, offset + length), and the file with the span file's bytes in their
place. The launcher writes only into a file the entry's "sha256Before" and "spanSha256Before" name, so
an entry is proved by the UNMODIFIED file alone: its three hashes must be "sha256Before",
"spanSha256Before" and "sha256". A copy that already carries the patch confirms "sha256" and
"spanSha256" only, and does not prove the entry.

A DevTest build of the enhancements DLL - the dev-only test bed, never shipped - carries a marker text
(the rule of Test-DevBuildMarker in build/pe_imports.ps1: its ASCII or UTF-16LE form anywhere in the
file). --set-sha refuses a payload file that carries it and writes nothing; --verify reports such a
file as DEV BUILD.

Edits are targeted text substitutions on the entry's own "sha256" value, so the hand-written notes,
key order and formatting of the manifest survive untouched. Exit code 1 when an entry is missing, a
hash does not match, a "kind" is one the launcher does not know, a payload file is a dev build or an
entry is not proved.
"""
import hashlib, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
PAYLOAD = os.path.join(REPO, "payload")
BYTEPATCH = "bytepatch"
COPY = "copy"  # the word the listing prints for a plain entry; never a value of "kind"

def manifest_path():
    return os.path.join(PAYLOAD, "manifest.json")

def load():
    with open(manifest_path(), "r", encoding="utf-8") as f:
        return f.read()

def versioned(text):
    """(version, entry) for every entry; the common ones carry version ""."""
    data = json.loads(text)
    out = [("", e) for e in data.get("common", [])]
    for ver, lst in data.get("versions", {}).items():
        out.extend((ver, e) for e in lst)
    return out

def entries(text):
    return [e for _, e in versioned(text)]

def payload_file(e):
    return os.path.join(PAYLOAD, e["src"].replace("/", os.sep))

def kind_of(e):
    """How the launcher applies the entry: COPY (no "kind", or an empty one), BYTEPATCH, or None for any
    other value of "kind" - the launcher uses nothing of a manifest that holds such an entry."""
    kind = e.get("kind")
    if kind is None or kind == "":
        return COPY
    return BYTEPATCH if kind == BYTEPATCH else None

def unknown_kinds(listed):
    """Prints every entry whose "kind" the launcher does not know and returns how many there are."""
    odd = [e for e in listed if kind_of(e) is None]
    for e in odd:
        print(f"UNKNOWN KIND  {e.get('src')}  {e.get('kind')!r}")
    return len(odd)

def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

# The marker of a DevTest build (Test-DevBuildMarker in build/pe_imports.ps1 holds the same rule: the two
# change together). Joined at run time, so neither this file nor its compiled form carries it whole.
DEV_BUILD_MARKER = "".join(("RELIC-DEV", "TEST-BUILD"))
DEV_BUILD_FORMS = (DEV_BUILD_MARKER.encode("ascii"), DEV_BUILD_MARKER.encode("utf-16-le"))

def carries_dev_marker(path):
    """True when the file holds the dev build marker as ASCII or as UTF-16LE text, at any offset. Read in
    1 MiB chunks; the end of each chunk is kept, so a marker that runs across two chunks is found too."""
    keep = max(len(form) for form in DEV_BUILD_FORMS) - 1
    tail = b""
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            window = tail + chunk
            if any(form in window for form in DEV_BUILD_FORMS):
                return True
            tail = window[-keep:]
    return False

SHA256_RE = re.compile(r"^[0-9a-fA-F]{64}$")

def is_replacement(e):
    """A plain entry that names the original file of the game it takes the place of."""
    return kind_of(e) == COPY and bool(e.get("sha256Before"))

def replacement_problem(e):
    """What is wrong with the shape of a replacement entry, or None."""
    sha, before = str(e.get("sha256", "")), str(e.get("sha256Before", ""))
    if not SHA256_RE.match(sha) or not SHA256_RE.match(before):
        return "\"sha256\" and \"sha256Before\" must be sha256 values (64 hex digits)"
    if sha.lower() == before.lower():
        return "\"sha256Before\" and \"sha256\" are the same - the entry changes nothing"
    if e.get("backup") is not True:
        return "\"backup\" must be true - the original file of the game is kept beside the replacement"
    earlier = e.get("sha256Earlier", [])
    if not isinstance(earlier, list) or any(not isinstance(h, str) or not SHA256_RE.match(h)
                                            or h.lower() in (sha.lower(), before.lower()) for h in earlier):
        return "\"sha256Earlier\" must be a list of the sha256 values of earlier payload files of this entry"
    return None

def object_spans(text):
    """(start, end) of every JSON object in `text`, end exclusive; braces inside strings do not count."""
    spans, stack, i, n = [], [], 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            i += 1
            while i < n and text[i] != '"':
                i += 2 if text[i] == "\\" else 1
        elif c == "{":
            stack.append(i)
        elif c == "}" and stack:
            spans.append((stack.pop(), i + 1))
        i += 1
    return spans

def set_sha(src, sha):
    text = load()
    found = [e for e in entries(text) if e.get("src") == src]
    if not found:
        print(f"manifest: no entry with src={src!r} - add it first (see docs/ENTERTAINMENT-EXPERIENCE.md)")
        return 1
    if os.path.isfile(payload_file(found[0])) and carries_dev_marker(payload_file(found[0])):
        print(f"manifest: payload/{src} is a DevTest build (it carries the marker of one) - only a Release build"
              " goes into the payload; nothing written")
        return 1
    other = [e for e in found if kind_of(e) != COPY]
    if other:
        print(f"manifest: entry {src!r} has \"kind\": {other[0].get('kind')!r} - only the sha256 of a plain entry is the hash of payload/{src}; edit it by hand")
        return 1
    sha = sha.lower()
    # The entry is the innermost object holding "src": "<src>"; only its text is edited.
    at = re.search(r'"src"\s*:\s*"' + re.escape(src) + r'"', text)
    spans = [s for s in object_spans(text) if at and s[0] < at.start() < s[1]]
    if not spans:
        print(f"manifest: entry {src!r} could not be found in the text")
        return 1
    start, end = min(spans, key=lambda s: s[1] - s[0])
    body = text[start:end]
    field = re.search(r'"sha256"\s*:\s*"([0-9a-fA-F]*)"', body)
    if not field:
        print(f"manifest: entry {src!r} has no sha256 field to update")
        return 1
    old = field.group(1).lower()
    body = body[:field.start(1)] + sha + body[field.end(1):]
    note = ""
    if is_replacement(found[0]) and old and old != sha:
        # The hash it replaces becomes an earlier payload file of the entry: a player whose game holds
        # that build gets the new one, and the player's own file is still told apart from both.
        listed = re.search(r'"sha256Earlier"\s*:\s*\[([^\]]*)\]', body)
        if listed:
            known = [h.lower() for h in re.findall(r'"([0-9a-fA-F]+)"', listed.group(1))]
            if old not in known:
                inner = listed.group(1).strip()
                body = body[:listed.start(1)] + (inner + ", " if inner else "") + f'"{old}"' + body[listed.end(1):]
        else:
            key = re.search(r'"sha256"\s*:', body)
            line_start = body.rfind("\n", 0, key.start()) + 1
            lead = body[line_start:key.start()]
            sep = ",\n" + lead if lead.strip() == "" else ", "
            body = body[:key.start()] + f'"sha256Earlier": ["{old}"]' + sep + body[key.start():]
        note = f" (the earlier {old} kept in sha256Earlier)"
    new = text[:start] + body + text[end:]
    edited = [e for e in entries(new) if e.get("src") == src]
    if len(edited) != 1 or str(edited[0].get("sha256", "")).lower() != sha:
        print(f"manifest: the edit of {src!r} did not give the entry back as expected - nothing written")
        return 1
    with open(manifest_path(), "w", encoding="utf-8", newline="") as f:
        f.write(new)
    print(f"manifest: {src} sha256 = {sha}{note}")
    return 0

def verify():
    listed = entries(load()); bad = unknown_kinds(listed)
    for e in listed:
        kind = kind_of(e)
        if kind is None:
            continue
        p = payload_file(e)
        if not os.path.isfile(p):
            print(f"MISSING  {e['src']}"); bad += 1; continue
        got = sha256_of(p)
        if kind == BYTEPATCH:
            # The payload file is the span; the entry's "sha256" belongs to the patched game file (--prove).
            size = os.path.getsize(p)
            ok = str(e.get("spanSha256", "")).lower() == got and size == e.get("length")
            print(f"{'ok     ' if ok else 'MISMATCH'}  {e['src']}  {got}  (bytepatch span, {size} of {e.get('length')} bytes)")
        else:
            ok = e.get("sha256", "").lower() == got
            print(f"{'ok     ' if ok else 'MISMATCH'}  {e['src']}  {got}")
            problem = replacement_problem(e) if is_replacement(e) else None
            if problem:
                print(f"BAD ENTRY  {e['src']}  replacement: {problem}")
                ok = False
        if carries_dev_marker(p):
            print(f"DEV BUILD  {e['src']}  carries the marker of a DevTest build, which never ships")
            ok = False
        bad += 0 if ok else 1
    return 1 if bad else 0

def listing():
    for e in entries(load()):
        kind = kind_of(e)
        flags = ",".join([kind or f"unknown kind {e.get('kind')!r}"] + (["replaces"] if is_replacement(e) else [])
                         + [k for k in ("required", "backup", "inject") if e.get(k)])
        where = f" @{e.get('offset')}+{e.get('length')} of {e.get('size')}" if kind == BYTEPATCH else ""
        print(f"{e['src']} -> {e['dst']}{where}  [{e.get('sha256','')[:12]}...] {flags}")
    return 0

def prove(game_dir, version=None):
    """Read-only. For each bytepatch entry, one pass over the game's own file gives three hashes: the
    file as it is, its span, and the file with the span file in the place of that span. Proved = the
    unmodified file: the three are "sha256Before", "spanSha256Before" and "sha256"."""
    listed = versioned(load())
    bad = unknown_kinds([e for _, e in listed])
    todo = [(ver, e) for ver, e in listed
            if kind_of(e) == BYTEPATCH and (version is None or ver == version)]
    if not todo:
        print("no bytepatch entry" + (f" for version {version}" if version else "") + " - nothing to prove")
        return 1 if bad else 0
    for ver, e in todo:
        label = f"{ver or 'common'}: {e['dst']}"
        target = os.path.join(game_dir, e["dst"].replace("/", os.sep))
        size, offset, length = e.get("size"), e.get("offset"), e.get("length")
        if not all(isinstance(n, int) and not isinstance(n, bool) for n in (size, offset, length)) \
                or size <= 0 or offset < 0 or length <= 0 or offset + length > size:
            print(f"BAD ENTRY  {label}  offset {offset!r} + length {length!r} is not a span inside size {size!r}"); bad += 1; continue
        want = {k: str(e.get(k, "")).lower() for k in ("sha256", "sha256Before", "spanSha256", "spanSha256Before")}
        if want["sha256"] == want["sha256Before"] or want["spanSha256"] == want["spanSha256Before"]:
            print(f"BAD ENTRY  {label}  a \"before\" hash is the same as its \"after\" - the entry changes nothing"); bad += 1; continue
        if not os.path.isfile(payload_file(e)):
            print(f"MISSING  {e['src']}"); bad += 1; continue
        with open(payload_file(e), "rb") as f:
            span = f.read()
        if len(span) != length or hashlib.sha256(span).hexdigest() != want["spanSha256"]:
            print(f"MISMATCH  {e['src']}  is not the {length} bytes spanSha256 names"); bad += 1; continue
        if not os.path.isfile(target):
            print(f"NO FILE  {label}  ({target})"); bad += 1; continue
        if os.path.getsize(target) != size:
            print(f"OTHER SIZE  {label}  {os.path.getsize(target)} bytes, the entry says {size}"); bad += 1; continue
        as_is, own_span, patched = hashlib.sha256(), hashlib.sha256(), hashlib.sha256()
        with open(target, "rb") as f:
            at = 0
            for chunk in iter(lambda: f.read(1 << 20), b""):
                as_is.update(chunk)
                lo, hi = max(offset, at), min(offset + length, at + len(chunk))
                if lo < hi:  # the part of the span this chunk holds: hashed alone, then replaced by the span file's
                    own_span.update(chunk[lo - at:hi - at])
                    chunk = chunk[:lo - at] + span[lo - offset:hi - offset] + chunk[hi - at:]
                patched.update(chunk)
                at += len(chunk)
        def names(got, *keys):  # which of the entry's hashes `got` is; else the one the unmodified file has to give
            hit = [k for k in keys if want[k] == got]
            return f"= {hit[0]}" if hit else f"the entry says {keys[0]} {want[keys[0]]}"
        whole, part, result = as_is.hexdigest(), own_span.hexdigest(), patched.hexdigest()
        original = whole == want["sha256Before"] and part == want["spanSha256Before"]
        carries = whole == want["sha256"] and part == want["spanSha256"]
        ok = original and result == want["sha256"] and at == size
        print(f"{'proved  ' if ok else 'PATCHED ' if carries else 'MISMATCH'}  {label}")
        print(f"          as it is:      {whole}  ({names(whole, 'sha256Before', 'sha256')})")
        print(f"          its span:      {part}  ({names(part, 'spanSha256Before', 'spanSha256')})")
        print(f"          with the span: {result}  ({names(result, 'sha256')})")
        if carries:
            print("          this copy already carries the patch: sha256Before and spanSha256Before, which the launcher"
                  " asks of every file it writes into, are not confirmed - prove against the unmodified file")
        elif not ok:
            print("          the unmodified file gives = sha256Before, = spanSha256Before and = sha256")
        bad += 0 if ok else 1
    return 1 if bad else 0

if __name__ == "__main__":
    a = sys.argv[1:]
    if len(a) >= 2 and a[0] == "--payload":
        PAYLOAD = os.path.abspath(a[1])
        a = a[2:]
    if len(a) == 3 and a[0] == "--set-sha":
        sys.exit(set_sha(a[1], a[2]))
    if a == ["--verify"]:
        sys.exit(verify())
    if a == ["--list"]:
        sys.exit(listing())
    if len(a) in (2, 3) and a[0] == "--prove":
        sys.exit(prove(*a[1:]))
    print(__doc__); sys.exit(2)
