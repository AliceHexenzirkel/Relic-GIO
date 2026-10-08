#!/usr/bin/env python3
"""dumpnames.py - the `rva -> Namespace.Class.Method` map of a dump.cs, cached.

    from dumpnames import load_names
    names = load_names("16")        # dict rva -> name, ~260k entries; parses dump.cs once (~30 s) then reads
                                    # dumps_ref/<ver>/names.pkl (invalidated by the dump's mtime)

callscan.parse_dump does the parsing; this only adds the cache so the tools that resolve names per
invocation (callscan, callers, disasm) do not each spend half a minute on the same 100 MB file.
"""
import os, pickle, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from codexref import DEFAULT_DUMP_DIR, CACHE_DIR  # noqa: E402


def dump_path(ver):
    return os.path.join(DEFAULT_DUMP_DIR[ver], "dump.cs")


def load_names(ver, quiet=True):
    import callscan
    ver = str(ver)
    dump = dump_path(ver)
    cache = os.path.join(CACHE_DIR[ver], "names.pkl")
    try:
        if os.path.getmtime(cache) >= os.path.getmtime(dump):
            with open(cache, "rb") as f:
                return pickle.load(f)
    except (OSError, ValueError, pickle.PickleError):
        pass
    if not quiet:
        print(f"# parsing {dump} ...", file=sys.stderr)
    names = callscan.parse_dump(dump)
    try:
        with open(cache, "wb") as f:
            pickle.dump(names, f, protocol=pickle.HIGHEST_PROTOCOL)
    except OSError:
        pass
    return names


def by_name(names):
    """name -> rva (first occurrence)"""
    out = {}
    for rva, n in names.items():
        out.setdefault(n, rva)
    return out


def resolve(names, token):
    """'0x1234' or 'Class.Method' (namespace optional, matched by suffix) -> rva"""
    t = token.strip()
    if t.lower().startswith("0x"):
        return int(t, 16)
    bn = by_name(names)
    if t in bn:
        return bn[t]
    hits = sorted({rva for rva, n in names.items() if n == t or n.endswith("." + t)})
    if len(hits) == 1:
        return hits[0]
    if not hits:
        raise SystemExit(f"no method named {t!r} in the {len(names)}-entry dump")
    raise SystemExit(f"{t!r} is ambiguous: " + ", ".join(f"0x{h:X} {names[h]}" for h in hits[:12]))
