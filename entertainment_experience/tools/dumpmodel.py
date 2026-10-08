#!/usr/bin/env python3
"""Shared loader for the Il2CppDumper output of one game version (dumps_ref/<ver>/): parses dump.cs and
script.json into a small Python model and caches it as dumps_ref/<ver>/model.pkl (gitignored scratch,
rebuilt automatically when dump.cs / script.json are newer). Used by transplant.py and gen_types.py.

    from dumpmodel import load
    m = load("28")            # -> DumpModel
    m.types                   # list[TypeDef]   (declaration order == TypeDefIndex order)
    m.by_full["MoleMole.MonoTeamBtn"], m.by_name["MonoTeamBtn"] -> [TypeDef]
    m.methods_by_rva[0x164D930] -> [(TypeDef, Method)]
    m.typeinfo["MoleMole.GameManager"] -> RVA of the Il2CppClass** slot   (script.json ScriptMetadata)
    m.methodinfo["Method$MoleMole.Singleton<X>.get_Instance()"] -> RVA        (script.json ScriptMetadataMethod)

Naming facts (verified against the Akebi 2.8 headers, see dumps_ref/2.8/REPORT.md):
  * script.json "Address" values are already RVAs (Il2CppDumper's GetRVA() subtracts the PE ImageBase,
    0x180000000 for UserAssembly.dll) - they go into DO_APP_FUNC unchanged.
  * dump.cs method names are "Namespace.Outer.Inner$$Method" in script.json; nested types use '.'.
Stdlib only.
"""
import os, re, sys, json, pickle, time

HERE = os.path.dirname(os.path.abspath(__file__))
EE = os.path.normpath(os.path.join(HERE, ".."))
DUMPS = os.path.join(EE, "dumps_ref")

PRIMS = {"void", "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double",
         "char", "string", "object", "decimal"}
OBF_RE = re.compile(r'^[A-Z]{11}$')


def is_obf(name):
    """BeeByte-style identifier: 11 upper-case letters"""
    return bool(OBF_RE.match(name))


class Field:
    __slots__ = ("modifiers", "ctype", "name", "offset", "static", "const", "value")

    def __init__(self, modifiers, ctype, name, offset, static, const, value):
        self.modifiers, self.ctype, self.name, self.offset = modifiers, ctype, name, offset
        self.static, self.const, self.value = static, const, value

    def __repr__(self):
        return f"Field({self.ctype} {self.name} @{self.offset})"


class Param:
    __slots__ = ("ctype", "name", "default", "mod")

    def __init__(self, ctype, name, default=None, mod=None):
        self.ctype, self.name, self.default, self.mod = ctype, name, default, mod

    def __repr__(self):
        return f"{self.mod + ' ' if self.mod else ''}{self.ctype} {self.name}" + (f" = {self.default}" if self.default is not None else "")


class Method:
    __slots__ = ("rva", "slot", "modifiers", "ret", "name", "params", "static", "index")

    def __init__(self, rva, slot, modifiers, ret, name, params, static, index):
        self.rva, self.slot, self.modifiers, self.ret, self.name = rva, slot, modifiers, ret, name
        self.params, self.static, self.index = params, static, index

    def sig(self):
        return f"{'static ' if self.static else ''}{self.ret} {self.name}({', '.join(map(repr, self.params))})"

    def __repr__(self):
        return f"Method({self.rva:#x} {self.sig()})"


class TypeDef:
    __slots__ = ("index", "namespace", "name", "kind", "base", "interfaces", "fields", "methods", "modifiers",
                 "nested_in", "generic", "line")

    def __init__(self, index, namespace, name, kind, base, interfaces, modifiers, line):
        self.index, self.namespace, self.name, self.kind = index, namespace, name, kind
        self.base, self.interfaces, self.modifiers, self.line = base, interfaces, modifiers, line
        self.fields, self.methods = [], []
        self.nested_in = name.rsplit(".", 1)[0] if "." in name else None
        self.generic = "<" in name

    @property
    def short(self):
        """innermost simple name (without generic arity text)"""
        return self.name.rsplit(".", 1)[-1]

    @property
    def full(self):
        return (self.namespace + "." if self.namespace else "") + self.name

    @property
    def obfuscated(self):
        return is_obf(self.short)

    def dump_name(self):
        """the name Il2CppDumper uses in script.json ("Namespace.Outer.Inner")"""
        return self.full

    def instance_fields(self):
        return [f for f in self.fields if not f.static and not f.const]

    def enum_values(self):
        return [(f.name, f.value) for f in self.fields if f.const and f.name != "value__"]

    def __repr__(self):
        return f"TypeDef({self.kind} {self.full} : {self.base} #{self.index})"


class DumpModel:
    def __init__(self, ver):
        self.ver = ver
        self.types = []
        self.by_full = {}
        self.by_name = {}
        self.by_index = {}
        self.methods_by_rva = {}
        self.typeinfo = {}       # "Namespace.Name" (+generic text) -> RVA
        self.methodinfo = {}     # "Method$..." -> RVA
        self.script_methods = {} # "Type$$Method" -> [RVA, ...] in script.json order
        self.script_by_rva = {}
        self.image_base = None

    def index(self):
        self.by_full = {}
        self.by_name = {}
        self.by_index = {}
        self.methods_by_rva = {}
        self.script_by_rva = {}   # RVA -> ["Type$$Method", ...] from script.json (includes generic instantiations)
        for t in self.types:
            self.by_full.setdefault(t.full, []).append(t)
            self.by_name.setdefault(t.short, []).append(t)
            self.by_index[t.index] = t
            for m in t.methods:
                if m.rva:
                    self.methods_by_rva.setdefault(m.rva, []).append((t, m))
        for name, rvas in self.script_methods.items():
            for r in rvas:
                self.script_by_rva.setdefault(r, []).append(name)

    def find(self, full_or_short):
        """[TypeDef] by 'Namespace.Name', 'Name' or 'Outer.Inner'"""
        if full_or_short in self.by_full:
            return self.by_full[full_or_short]
        if full_or_short in self.by_name:
            return self.by_name[full_or_short]
        # nested without namespace
        return [t for t in self.types if t.name == full_or_short]

    def subclasses(self, base_full_or_short):
        return [t for t in self.types if t.base == base_full_or_short]


# ---------------------------------------------------------------- dump.cs parser
NS_RE = re.compile(r'^// Namespace: (.*)$')
TYPE_RE = re.compile(r'^((?:(?:public|private|internal|protected|sealed|abstract|static|readonly|ref)\s+)*)(class|struct|enum|interface)\s+(.+?)(?:\s*:\s*(.+?))?\s*//\s*TypeDefIndex:\s*(\d+)\s*$')
FIELD_RE = re.compile(r'^\t((?:(?:public|private|internal|protected|static|readonly|const|volatile)\s+)*)(.+?)\s+(\S+?)(?:\s*=\s*(.+?))?;(?:\s*//\s*0x([0-9A-Fa-f]+))?\s*$')
RVA_RE = re.compile(r'^\t// RVA: (-?0x[0-9A-Fa-f]+|-1) Offset: (?:-?0x[0-9A-Fa-f]+|-1)(?: VA: 0x[0-9A-Fa-f]+)?(?: Slot: (\d+))?\s*$')
METHOD_RE = re.compile(r'^\t((?:(?:public|private|internal|protected|static|virtual|override|abstract|sealed|extern|unsafe|new)\s+)*)(.+?)\s+(\S+?)\((.*)\)\s*\{\s*\}\s*$')


def split_params(s):
    """split a C# parameter list on top-level commas"""
    out, depth, cur = [], 0, []
    for ch in s:
        if ch == "<":
            depth += 1
        elif ch == ">":
            depth -= 1
        if ch == "," and depth == 0:
            out.append("".join(cur).strip()); cur = []
        else:
            cur.append(ch)
    if "".join(cur).strip():
        out.append("".join(cur).strip())
    return out


def parse_params(s):
    params = []
    for p in split_params(s):
        default = None
        if "=" in p and not p.startswith("="):
            # "Type name = default"
            left, default = p.split("=", 1)
            p, default = left.strip(), default.strip()
        mod = None
        for kw in ("ref ", "out ", "params ", "in "):
            if p.startswith(kw):
                mod, p = kw.strip(), p[len(kw):]
        # split type and name at the last space outside <>
        depth, cut = 0, -1
        for i, ch in enumerate(p):
            if ch == "<":
                depth += 1
            elif ch == ">":
                depth -= 1
            elif ch == " " and depth == 0:
                cut = i
        if cut < 0:
            params.append(Param(p, "", default, mod))
        else:
            params.append(Param(p[:cut].strip(), p[cut + 1:].strip(), default, mod))
    return params


def parse_dump_cs(path):
    types = []
    ns = ""
    cur = None
    section = None
    pending_rva = None
    pending_slot = None
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for lineno, line in enumerate(f, 1):
            line = line.rstrip("\n")
            if not line:
                continue
            if line.startswith("// Namespace: "):
                ns = line[len("// Namespace: "):].strip()
                continue
            if cur is None or line[0] != "\t":
                if line == "{" or line == "}":
                    if line == "}":
                        cur = None; section = None
                    continue
                m = TYPE_RE.match(line)
                if m:
                    mods, kind, name, bases, idx = m.groups()
                    base, ifaces = None, []
                    if bases:
                        parts = split_params(bases)
                        if kind in ("class",) and parts:
                            # first base that is not an interface-looking name; dump.cs lists the base class first
                            base = parts[0]
                            ifaces = parts[1:]
                        else:
                            ifaces = parts
                    cur = TypeDef(int(idx), ns, name, kind, base, ifaces, mods.strip(), lineno)
                    types.append(cur)
                    section = None
                continue
            s = line.strip()
            if s == "// Fields":
                section = "fields"; continue
            if s == "// Properties":
                section = "props"; continue
            if s == "// Methods":
                section = "methods"; continue
            if s.startswith("[") and "// RVA" in s:
                continue  # attribute line
            if section == "fields":
                if s.startswith("["):
                    continue
                m = FIELD_RE.match(line)
                if m:
                    mods, ctype, name, value, off = m.groups()
                    is_static = "static" in mods
                    is_const = "const" in mods
                    cur.fields.append(Field(mods.strip(), ctype, name, int(off, 16) if off else None, is_static, is_const, value))
            elif section == "methods":
                m = RVA_RE.match(line)
                if m:
                    rva_s, slot = m.groups()
                    pending_rva = None if rva_s == "-1" else int(rva_s, 16)
                    pending_slot = int(slot) if slot else None
                    continue
                if s.startswith("["):
                    continue
                m = METHOD_RE.match(line)
                if m:
                    mods, ret, name, params = m.groups()
                    cur.methods.append(Method(pending_rva, pending_slot, mods.strip(), ret, name, parse_params(params),
                                              "static" in mods.split(), len(cur.methods)))
                    pending_rva = pending_slot = None
    return types


# ---------------------------------------------------------------- script.json (regex scan: 240 MB, json.load is too slow/heavy)
PAIR_RE = re.compile(r'"Address":\s*(\d+),\s*"Name":\s*"((?:[^"\\]|\\.)*)"')


def parse_script_json(path):
    with open(path, "r", encoding="utf-8") as f:
        s = f.read()
    i_str = s.find('"ScriptString": [')
    i_meta = s.find('"ScriptMetadata": [')
    i_mm = s.find('"ScriptMetadataMethod": [')
    i_addr = s.find('"Addresses": [')
    methods, typeinfo, methodinfo = {}, {}, {}
    for a, n in PAIR_RE.findall(s[:i_str]):
        methods.setdefault(json.loads('"' + n + '"'), []).append(int(a))
    for a, n in PAIR_RE.findall(s[i_meta:i_mm]):
        n = json.loads('"' + n + '"')
        if n.endswith("_TypeInfo"):
            typeinfo[n[:-len("_TypeInfo")]] = int(a)
    for a, n in PAIR_RE.findall(s[i_mm:i_addr]):
        methodinfo[json.loads('"' + n + '"')] = int(a)
    return methods, typeinfo, methodinfo


def pe_image_base(path):
    import struct
    with open(path, "rb") as f:
        b = f.read(0x400)
    pe = struct.unpack_from("<I", b, 0x3C)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", b, opt)[0]
    return struct.unpack_from("<Q" if magic == 0x20B else "<I", b, opt + 24)[0]


def load(ver, dump_dir=None, quiet=False):
    d = dump_dir or os.path.join(DUMPS, {"28": "2.8", "16": "1.6", "33": "3.3"}.get(str(ver), str(ver)))
    dump_cs = os.path.join(d, "dump.cs")
    script = os.path.join(d, "script.json")
    cache = os.path.join(d, "model.pkl")
    if not os.path.exists(dump_cs) or not os.path.exists(script):
        raise SystemExit(f"dump not found in {d} (run dump_runner.py --ver {ver} first)")
    newest = max(os.path.getmtime(dump_cs), os.path.getmtime(script), os.path.getmtime(__file__))
    if os.path.exists(cache) and os.path.getmtime(cache) >= newest:
        with open(cache, "rb") as f:
            m = pickle.load(f)
        m.index()
        return m
    t0 = time.time()
    if not quiet:
        print(f"[dumpmodel] parsing {dump_cs} ...", file=sys.stderr)
    m = DumpModel(str(ver))
    m.types = parse_dump_cs(dump_cs)
    if not quiet:
        print(f"[dumpmodel] {len(m.types)} types, parsing {script} ...", file=sys.stderr)
    m.script_methods, m.typeinfo, m.methodinfo = parse_script_json(script)
    m.index()
    with open(cache, "wb") as f:
        pickle.dump(m, f, protocol=pickle.HIGHEST_PROTOCOL)
    if not quiet:
        print(f"[dumpmodel] cached -> {cache} ({time.time() - t0:.1f}s)", file=sys.stderr)
    return m


if __name__ == "__main__":
    # import ourselves as a module so the pickled classes are "dumpmodel.X", not "__main__.X"
    sys.path.insert(0, HERE)
    import dumpmodel as _dm
    ver = sys.argv[1] if len(sys.argv) > 1 else "28"
    m = _dm.load(ver)
    nm = sum(len(t.methods) for t in m.types)
    nf = sum(len(t.fields) for t in m.types)
    print(f"types={len(m.types)} methods={nm} fields={nf} script_methods={len(m.script_methods)} typeinfo={len(m.typeinfo)} methodinfo={len(m.methodinfo)}")
    for q in sys.argv[2:]:
        for t in m.find(q):
            print(t)
            for fl in t.fields[:60]:
                print("   ", fl)
            for me in t.methods[:80]:
                print("   ", me)
