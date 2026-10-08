#!/usr/bin/env python3
"""Fills the 0x0 placeholders of src/appdata-<ver>/il2cpp-functions.h and il2cpp-types-ptr.h from the IL2CPP dump
of that game version (dumps_ref/<ver>/, produced by dump_runner.py), in place and reproducibly:

    transplant.py --ver 28 [--dump dumps_ref/2.8] [--dry-run] [--verbose] [--only NAME,...]

Inputs : the DO_APP_FUNC / DO_APP_FUNC_METHODINFO / DO_TYPEDEF lines whose offset is 0x0 (marker "RELIC-TODO-<ver>"),
         the already-resolved lines of the same headers (they teach the tool how the obfuscated dump maps onto the
         Il2CppInspector names), src/appdata-28 + appdata-33 il2cpp-types.h (struct shapes used as fingerprints).
Outputs: the two headers rewritten (offset filled, marker replaced by "// <ver>: from dump (<how>) <dump name>"),
         dumps_ref/<ver>/unresolved.txt, dumps_ref/<ver>/classmap.json (Il2CppInspector type name -> dump type, consumed
         by gen_types.py), a report on stdout.

Address math: script.json "Address" is already the RVA (Il2CppDumper GetRVA = VA - PE ImageBase 0x180000000), the
header offsets are RVAs too -> copied unchanged. Acceptance test run on every invocation: every non-zero RVA already in
the header must be a method entry of the dump, and every readable name must resolve to the very same RVA
(GameManager_Update 0x0164D930, ...). A mismatch aborts.

How a name is resolved (in this order, the first unambiguous hit wins; everything is reported):
  1. by name   - Il2CppInspector "Class_Method[_N]" against "Namespace.Class$$Method" (namespace optional, nested
                 types, _N overload index = N-th same-named method in declaration order; a trailing _N on the class
                 is Il2CppInspector's duplicate-name disambiguator, e.g. Object_1). Also inside an obfuscated class
                 once the class is known (virtual overrides such as SetupView/ClearView keep readable names).
  2. by signature inside a known class - the 2.8 metadata is BeeByte-obfuscated (most MoleMole class AND method names
                 are 11-letter tokens). The class is known from: the resolved functions of the same header (RVA ->
                 dump class), Singleton<X>.get_Instance MethodInfo slots, TypeInfo slots, readable names, the
                 "XxxContext owns a MonoXxx view" rule, field-shape fingerprints of the appdata-33 / 28 struct against
                 candidate classes (proto messages = subclasses of the obfuscated MessageBase, singletons, nested
                 types, or the parameter types of the candidate methods), the proto "oneof member" rule, and the
                 parameter types of functions resolved earlier in the same run. Inside the class the method must match
                 the 3.3 declaration: static-ness, return kind, parameter count and kinds (primitive exact, string,
                 enum, value struct, class; identity of classes/enums that are themselves mapped), with bonuses for
                 parameter names (direct or through the obfuscation dictionary learned from the resolved functions:
                 BeeByte renames identical identifiers identically within a build). One best candidate -> resolved
                 "by signature"; several with the same best score -> unresolved, candidates listed.
  3. by global shape search - class unknown, signature distinctive enough (>= 3 parameters or an enum/struct/string
                 among >= 2): unique match over all classes; for long signatures (>= 6 parameters) one trailing
                 parameter added after 2.8 is tolerated.
Nothing is guessed: every line written carries its provenance, and unresolved.txt says why (not found / obfuscated
without a unique signature match / ambiguous with the candidate list / not applicable).
Stdlib only; deterministic.
"""
import argparse, collections, difflib, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from dumpmodel import load, is_obf, split_params  # noqa: E402

EE = os.path.normpath(os.path.join(HERE, ".."))
SRC = os.path.join(EE, "mod", "cheat-library", "src")
DUMPS = os.path.join(EE, "dumps_ref")
VER_DIR = {"28": "2.8", "16": "1.6", "33": "3.3"}

FUNC_RE = re.compile(r'^(\s*DO_APP_FUNC\(\s*)(0x[0-9A-Fa-f]+)(\s*,\s*)(.+?)(,\s*)([A-Za-z_]\w*)(\s*,\s*)(\(.*\))(\s*\)\s*;)(.*)$')
MI_RE = re.compile(r'^(\s*DO_APP_FUNC_METHODINFO\(\s*)(0x[0-9A-Fa-f]+)(\s*,\s*)([A-Za-z_]\w*)(\s*\)\s*;)(.*)$')
TD_RE = re.compile(r'^(\s*DO_(?:TYPEDEF|SINGLETONEDEF)\(\s*)(0x[0-9A-Fa-f]+)(\s*,\s*)([A-Za-z_]\w*)(\s*\)\s*;)(.*)$')

C_PRIMS = {"bool", "int8_t", "uint8_t", "int16_t", "uint16_t", "int32_t", "uint32_t", "int64_t", "uint64_t", "float",
           "double", "intptr_t", "uintptr_t", "void", "char"}
CS_PRIM = {"bool": "bool", "byte": "uint8_t", "sbyte": "int8_t", "short": "int16_t", "ushort": "uint16_t", "int": "int32_t",
           "uint": "uint32_t", "long": "int64_t", "ulong": "uint64_t", "float": "float", "double": "double",
           "char": "uint16_t", "void": "void", "IntPtr": "intptr_t", "UIntPtr": "uintptr_t"}
INSP_PRIM_ARG = {"System_UInt32": "uint32_t", "System_Int32": "int32_t", "System_UInt64": "uint64_t", "System_Int64": "int64_t",
                 "System_String": "string", "System_Boolean": "bool", "System_Single": "float", "System_Double": "double",
                 "System_Byte": "uint8_t", "System_UInt16": "uint16_t", "System_Int16": "int16_t", "Boolean": "bool",
                 "UInt32": "uint32_t", "Int32": "int32_t", "String": "string", "UInt64": "uint64_t", "Single": "float"}


def norm(s):
    return re.sub(r'[^A-Za-z0-9]', '', s).lower()


def read(p):
    with open(p, "r", encoding="utf-8", errors="surrogateescape", newline="") as f:
        return f.read()


def write(p, s):
    with open(p, "w", encoding="utf-8", errors="surrogateescape", newline="") as f:
        f.write(s)


# ---------------------------------------------------------------- Il2CppInspector headers (types.h) as fingerprint source
class HeaderTypes:
    """struct / enum knowledge from an il2cpp-types.h (Il2CppInspector shape)"""
    def __init__(self, path):
        self.path = path
        self.text = read(path) if os.path.exists(path) else ""
        self.fields = {}     # struct name -> [(ctype, fname)]  (the X__Fields body, incl. the base "_" line)
        self.structs = set() # every struct name declared (without __Fields/__Class suffixes)
        self.enums = {}      # enum name (without __Enum) -> {member: value}
        for m in re.finditer(r'^\s*struct\s+(?:__declspec\(align\(\d+\)\)\s+)?([A-Za-z_]\w*?)(__Fields|__Class|__StaticFields|__VTable|__Array|__Boxed)?\s*\{(.*?)^\s*\};', self.text, re.S | re.M):
            name, suffix, body = m.group(1), m.group(2), m.group(3)
            self.structs.add(name)
            if suffix == "__Fields":
                flds = []
                for ln in body.splitlines():
                    ln = ln.split("//")[0].strip()
                    mm = re.match(r'^(?:const\s+)?(.+?)\s+([A-Za-z_]\w*)(\[\d+\])?;$', ln)
                    if mm:
                        flds.append((mm.group(1).strip(), mm.group(2)))
                self.fields[name] = flds
        self.enums_raw = {}  # enums whose name does not end with __Enum (GadgetType_Enum ...) -> {member: value}
        for m in re.finditer(r'^\s*enum class\s+([A-Za-z_]\w*?)(__Enum)?\s*:\s*\w+\s*\{(.*?)\};', self.text, re.S | re.M):
            vals = {}
            for a, b in re.findall(r'([A-Za-z_]\w*)\s*=\s*(0x[0-9A-Fa-f]+|-?\d+)', m.group(3)):
                vals[a] = int(b, 0)
            if m.group(2):
                self.enums[m.group(1)] = vals
            else:
                self.enums_raw[m.group(1)] = vals
        self.exact = set()   # names declared exactly as `struct X {` / `struct X;` (wrappers, value structs, X__Fields, X__Class ...)
        for m in re.finditer(r'^\s*struct\s+(?:__declspec\(align\(\d+\)\)\s+)?([A-Za-z_]\w*)\s*[{;]', self.text, re.M):
            self.structs.add(m.group(1))
            self.exact.add(m.group(1))
        self.vtables = {}    # X -> [VirtualInvokeData member names in slot order]
        for m in re.finditer(r'^\s*struct\s+([A-Za-z_]\w*)__VTable\s*\{(.*?)^\s*\};', self.text, re.S | re.M):
            self.vtables[m.group(1)] = re.findall(r'VirtualInvokeData\s+([A-Za-z_]\w*)\s*;', m.group(2))

    def base_of(self, name):
        f = self.fields.get(name)
        if f and f[0][1] == "_" and f[0][0].startswith("struct ") and f[0][0].endswith("__Fields"):
            return f[0][0][len("struct "):-len("__Fields")]
        return None

    def own_fields(self, name):
        f = self.fields.get(name) or []
        return [x for x in f if not (x[1] == "_" and x[0].endswith("__Fields"))]


# ---------------------------------------------------------------- kinds / shapes
def header_ctype_kind(ctype, hint=None):
    """Il2CppInspector C type (as written in DO_APP_FUNC / __Fields) -> (kind, identity-name-or-None)"""
    t = ctype.replace("const ", "").replace("struct ", "").replace("enum ", "").strip()
    if t.endswith("__Enum"):
        return ("enum", t[:-len("__Enum")])
    if t in C_PRIMS:
        return ("prim", t)
    if t in ("String*", "Il2CppString*"):
        return ("string", None)
    if t in ("Object*", "Il2CppObject*"):
        return ("object", None)
    if t.endswith("**"):
        return ("ptr", None)
    if t.endswith("*"):
        base = t[:-1].strip()
        if base == "void":
            return ("class", hint) if hint else ("voidptr", None)
        if "RepeatedMessageField_1_" in base or "RepeatedPrimitiveField_1_" in base or "RepeatedField_1_" in base:
            arg = re.sub(r'^.*Field_1_', '', base).rstrip("_")
            return ("rep", INSP_PRIM_ARG.get(arg, "class:" + arg))
        if "MapField_2_" in base:
            return ("map", None)
        if base.endswith("ByteString"):
            return ("bytes", None)
        if base.startswith("List_1_") or base.startswith("System_Collections_Generic_List_1"):
            return ("list", None)
        if base.startswith("Dictionary_2_") or base.startswith("System_Collections_Generic_Dictionary_2"):
            return ("dict", None)
        if re.match(r'^(Action|Func|UnityAction|Predicate|Comparison)(_\d+_|$)', base):
            return ("delegate", None)
        return ("class", base)
    return ("vstruct", t)


class Dump:
    """wraps the DumpModel with kind lookups and Inspector-style keys"""
    def __init__(self, model):
        self.m = model
        self.keys = collections.defaultdict(set)  # norm(Inspector key) -> {TypeDef}
        for t in model.types:
            for k in self.insp_keys(t):
                self.keys[norm(k)].add(t)
        cnt = collections.Counter(t.base for t in model.types if t.base)
        self.proto_base = None
        for base, n in cnt.most_common(20):
            if is_obf(base.rsplit(".", 1)[-1]) and n > 500:
                self.proto_base = base
                break
        self.proto_family = set(t.index for t in model.types if t.base == self.proto_base) if self.proto_base else set()
        self.singletons = {}
        for name in model.methodinfo:
            mm = re.match(r'^Method\$MoleMole\.Singleton<(.+)>\.get_Instance\(\)$', name)
            if mm:
                for t in model.find(mm.group(1)):
                    self.singletons[t.index] = t
        self.kind_cache = {}

    @staticmethod
    def insp_keys(t):
        short = t.short
        base = re.sub(r'`\d+', '', short)
        keys = {base}
        ns = t.namespace.replace(".", "_") + "_" if t.namespace else ""
        if ns:
            keys.add(ns + base)
        if "." in t.name:
            parts = [re.sub(r'`\d+', '', p) for p in t.name.split(".")]
            keys.add("_".join(parts))
            keys.add(ns + "_".join(parts))
            if len(parts) == 2:
                keys.add(parts[0] + "_" + parts[0] + "_" + parts[1])          # Il2CppInspector's doubled nested form
                keys.add(ns + parts[0] + "_" + ns + parts[0] + "_" + parts[1])
        return keys

    def is_proto(self, td):
        return td is not None and td.index in self.proto_family

    def type_kind(self, cs_type):
        """C# type text from dump.cs -> (kind, identity TypeDef-or-None, extra)"""
        if cs_type in self.kind_cache:
            return self.kind_cache[cs_type]
        r = self._type_kind(cs_type)
        self.kind_cache[cs_type] = r
        return r

    def _type_kind(self, cs):
        cs = cs.strip()
        if cs.startswith("ref ") or cs.startswith("out "):
            cs = cs[4:]
        if cs in CS_PRIM:
            return ("prim", None, CS_PRIM[cs])
        if cs == "string":
            return ("string", None, None)
        if cs == "object":
            return ("object", None, None)
        if cs.endswith("[]"):
            return ("array", None, None)
        if cs.endswith("*"):
            return ("ptr", None, None)
        if "<" in cs:
            g = cs[:cs.index("<")]
            args = split_params(cs[cs.index("<") + 1:-1])
            gs = g.rsplit(".", 1)[-1]
            if gs in ("List", "IList", "HashSet", "Queue", "Stack", "LinkedList", "IEnumerable", "ICollection", "IReadOnlyList"):
                return ("list", None, None)
            if gs in ("Dictionary", "IDictionary", "SortedDictionary"):
                return ("dict", None, None)
            if gs in ("Action", "Func", "UnityAction", "Predicate", "Comparison", "EventHandler"):
                return ("delegate", None, None)
            if gs in ("Nullable", "KeyValuePair", "ValueTuple"):
                return ("vstruct", None, None)
            if is_obf(gs):
                if len(args) == 1:
                    k = self.type_kind(args[0])
                    if k[0] == "prim":
                        return ("rep", None, k[2])
                    if k[0] == "string":
                        return ("rep", None, "string")
                    return ("rep", k[1], "class")
                if len(args) == 2:
                    return ("map", None, None)
            return ("generic", None, None)
        if cs == "ByteString":
            return ("bytes", None, None)
        tds = self.m.find(cs)
        if not tds and "." in cs:
            tds = self.m.find(cs.rsplit(".", 1)[-1])
        if not tds:
            return ("class", None, None)
        kinds = collections.Counter(t.kind for t in tds)
        kind = kinds.most_common(1)[0][0]
        ident = tds[0] if len(tds) == 1 else None
        if kind == "enum":
            return ("enum", ident, None)
        if kind == "struct":
            return ("vstruct", ident, None)
        return ("class", ident, None)

    def field_tokens(self, t):
        toks = []
        for f in sorted(t.instance_fields(), key=lambda f: (f.offset is None, f.offset or 0)):
            k = self.type_kind(f.ctype)
            if k[0] == "prim":
                toks.append(k[2])
            elif k[0] == "rep":
                toks.append("rep:" + k[2] + (f":{k[1].index}" if k[2] == "class" and k[1] is not None else ""))
            elif k[0] in ("class", "enum", "vstruct") and k[1] is not None:
                toks.append(f"{k[0]}:{k[1].index}")
            else:
                toks.append(k[0])
        return toks

    def oneof_members(self, td):
        """proto message: the message types returned by its parameterless accessors that are not plain fields
        (= the members of its oneof groups)"""
        field_types = set()
        for f in td.instance_fields():
            k = self.type_kind(f.ctype)
            if k[1] is not None:
                field_types.add(k[1].index)
        out = []
        for me in td.methods:
            if me.static or me.params or me.rva is None:
                continue
            k = self.type_kind(me.ret)
            if k[0] == "class" and k[1] is not None and self.is_proto(k[1]) and k[1].index != td.index and k[1].index not in field_types:
                if k[1] not in out:
                    out.append(k[1])
        return out


def generalize(toks):
    return [t.split(":")[0] + (":" + t.split(":")[1] if t.startswith("rep:") else "") for t in toks]


def similarity(a, b):
    """0..1: order-aware ratio, with a bag (order-insensitive) fallback; identities count extra"""
    if not a or not b:
        return 0.0
    seq = difflib.SequenceMatcher(None, a, b, autojunk=False).ratio()
    ga, gb = generalize(a), generalize(b)
    seq_g = difflib.SequenceMatcher(None, ga, gb, autojunk=False).ratio()
    ca, cb = collections.Counter(a), collections.Counter(b)
    bag = 2 * sum((ca & cb).values()) / (len(a) + len(b))
    cga, cgb = collections.Counter(ga), collections.Counter(gb)
    bag_g = 2 * sum((cga & cgb).values()) / (len(a) + len(b))
    ident = sum(1 for x in (ca & cb).elements() if ":" in x and not x.startswith("rep:") or x.count(":") == 2)
    return max(seq, seq_g * 0.97, bag * 0.95, bag_g * 0.9) + 0.03 * ident / max(len(a), 1)


# ---------------------------------------------------------------- header parsing
class HeaderLine:
    def __init__(self, kind, idx, m, line):
        self.kind, self.idx, self.m, self.line = kind, idx, m, line
        self.offset = int(m.group(2), 16)
        if kind == "func":
            self.ret, self.name, self.params, self.tail = m.group(4).strip(), m.group(6), m.group(8), m.group(10)
        else:
            self.name, self.tail = m.group(4), m.group(6)
            self.ret = self.params = None
        self.unresolved = self.offset == 0


def parse_header(text):
    out = []
    for i, ln in enumerate(text.splitlines()):
        if ln.strip().startswith("//"):
            continue
        m = FUNC_RE.match(ln)
        if m:
            out.append(HeaderLine("func", i, m, ln)); continue
        m = MI_RE.match(ln)
        if m:
            out.append(HeaderLine("mi", i, m, ln)); continue
        m = TD_RE.match(ln)
        if m:
            out.append(HeaderLine("td", i, m, ln)); continue
    return out


def parse_decl_params(ptext):
    """'(LCAvatarCombat* __this, uint32_t skillID, MethodInfo* method)' -> (is_static, [(ctype, name, hint)])"""
    inner = ptext.strip()
    if inner.startswith("("):
        inner = inner[1:]
    if inner.endswith(")"):
        inner = inner[:-1]
    parts, depth, cur = [], 0, []
    for ch in inner:
        if ch in "(<":
            depth += 1
        elif ch in ")>":
            depth -= 1
        if ch == "," and depth == 0:
            parts.append("".join(cur)); cur = []
        else:
            cur.append(ch)
    if "".join(cur).strip():
        parts.append("".join(cur))
    out, is_static = [], True
    for p in parts:
        hint = None
        cm = re.search(r'/\*\s*([A-Za-z_]\w*)\s*\*?\s*\*/', p)
        if cm:
            hint = cm.group(1)
        p = re.sub(r'/\*.*?\*/', ' ', p).strip()
        if not p:
            continue
        toks = p.rsplit(None, 1)
        if len(toks) == 2:
            ctype, name = toks[0].strip(), toks[1].strip()
        else:
            ctype, name = p, ""
        if name == "__this":
            is_static = False
            continue
        if ctype.replace(" ", "") == "MethodInfo*" or name == "method":
            continue
        out.append((ctype, name, hint))
    return is_static, out


def parse_decl_ret(ret):
    hint = None
    cm = re.search(r'/\*\s*([A-Za-z_]\w*)\s*\*?\s*\*/', ret)
    if cm:
        hint = cm.group(1)
    return re.sub(r'/\*.*?\*/', ' ', ret).strip(), hint


# ---------------------------------------------------------------- the resolver
class Transplant:
    def __init__(self, ver, dump_dir, verbose=False):
        self.ver = ver
        self.verbose = verbose
        self.appdata = os.path.join(SRC, f"appdata-{ver}")
        self.model = load(ver, dump_dir, quiet=not verbose)
        self.d = Dump(self.model)
        self.h28 = HeaderTypes(os.path.join(SRC, "appdata-28", "il2cpp-types.h"))
        self.h33 = HeaderTypes(os.path.join(SRC, "appdata-33", "il2cpp-types.h"))
        if ver == "28":
            self.hcur = self.h28
        elif ver == "33":
            self.hcur = self.h33
        else:
            self.hcur = HeaderTypes(os.path.join(self.appdata, "il2cpp-types.h"))
        self.classmap = {}        # Inspector type name -> (TypeDef, how)
        self.classmap_cands = {}  # Inspector type name -> [TypeDef] (inconclusive)
        self.paramdict = collections.defaultdict(collections.Counter)
        self.log = []
        self.known_class_names = set(self.h28.structs) | set(self.h33.structs) | set(self.h28.enums) | set(self.h33.enums)
        self.known_class_names |= {"MoleMole_" + n for n in list(self.known_class_names)}
        self.identifying = set()

    def say(self, *a):
        s = " ".join(str(x) for x in a)
        self.log.append(s)
        if self.verbose:
            print(s)

    # ---- classmap helpers
    def map_type(self, insp, td, how):
        if insp in self.classmap:
            if self.classmap[insp][0].index != td.index:
                self.say(f"  classmap conflict for {insp}: {self.classmap[insp][0].full} ({self.classmap[insp][1]}) vs {td.full} ({how}) - keeping the first")
            return
        self.classmap[insp] = (td, how)
        self.d.keys[norm(insp)].add(td)
        self.say(f"  classmap {insp} -> {td.full}  [{how}]")

    def mapped(self, insp):
        v = self.classmap.get(insp)
        if v:
            return v[0]
        if insp and not insp.startswith("MoleMole_"):
            v = self.classmap.get("MoleMole_" + insp)
            if v:
                return v[0]
        if insp and insp.startswith("MoleMole_"):
            v = self.classmap.get(insp[len("MoleMole_"):])
            if v:
                return v[0]
        return None

    def class_candidates(self, part):
        td = self.mapped(part)
        if td is not None:
            return [td]
        res = set(self.d.keys.get(norm(part), ()))
        if not res:
            mm = re.match(r'^(.*?)_(\d+)$', part)
            if mm:
                res = set(self.d.keys.get(norm(mm.group(1)), ()))
        if not res and part.startswith("MoleMole_"):
            res = set(self.d.keys.get(norm(part[len("MoleMole_"):]), ()))
        return sorted(res, key=lambda t: t.index)

    # ---- stage 0: acceptance test on the already resolved lines
    def acceptance(self, lines):
        n_total = n_hit = n_name = 0
        misses, mism = [], []
        for hl in lines:
            if hl.kind != "func" or hl.offset == 0:
                continue
            n_total += 1
            hits = self.model.methods_by_rva.get(hl.offset, [])
            if not hits and hl.offset not in self.model.script_by_rva:
                misses.append(hl.name)
                continue
            n_hit += 1
            r = self.resolve_by_name(hl.name, hl.ret, hl.params)
            if r and r.get("rva") is not None:
                n_name += 1
                if r["rva"] != hl.offset:
                    mism.append((hl.name, hl.offset, r["rva"], r["dump"]))
        for hl in lines:
            if hl.name in ("GameManager_Update", "MoleMole_LoadingManager_PerformPlayerTransmit") and hl.kind == "func" and hl.offset:
                hits = self.model.methods_by_rva.get(hl.offset, [])
                print(f"  acceptance: {hl.name} {hl.offset:#010x} -> {[t.full + '$$' + me.name for t, me in hits] or self.model.script_by_rva.get(hl.offset)}")
        print(f"  acceptance: {n_hit}/{n_total} header RVAs are dump method entries (dump.cs methods or script.json generic instantiations); {n_name} readable names re-resolved by name, {len(mism)} mismatches")
        for x in mism:
            print(f"    MISMATCH {x[0]}: header {x[1]:#x} dump {x[2]:#x} ({x[3]})")
        if misses:
            print(f"    not dump entries: {misses}")
        if misses or mism:
            raise SystemExit("acceptance test failed - the RVA convention or the dump is wrong; refusing to write")

    # ---- stage 1: learn the class map from the resolved lines
    def learn(self, lines, ptr_lines):
        split_names = sorted(self.known_class_names, key=len, reverse=True)
        votes = collections.defaultdict(collections.Counter)
        for hl in lines:
            if hl.kind != "func" or not hl.offset:
                continue
            hits = self.model.methods_by_rva.get(hl.offset, [])
            if not hits:
                continue
            cls_part = None
            for kn in split_names:
                if hl.name.startswith(kn + "_") and len(hl.name) > len(kn) + 1:
                    cls_part = kn
                    break
            if cls_part is None:
                for t, me in hits:
                    if hl.name.endswith("_" + me.name):
                        cls_part = hl.name[: -len(me.name) - 1]
                        break
            if cls_part is None:
                continue
            if len(hits) > 1:
                good = [t for t, me in hits if norm(cls_part) in {norm(k) for k in Dump.insp_keys(t)}
                        or norm(cls_part).replace("molemole", "", 1) in {norm(k) for k in Dump.insp_keys(t)}]
                if len(good) == 1:
                    votes[cls_part][good[0].index] += 1
                continue
            votes[cls_part][hits[0][0].index] += 1
            is_static, hp = parse_decl_params(hl.params)
            me = hits[0][1]
            if len(hp) == len(me.params):
                for (ct, hn, _), dp in zip(hp, me.params):
                    if hn and dp.name and hn != dp.name:
                        self.paramdict[hn][dp.name] += 1
        for cls_part, c in sorted(votes.items()):
            if len(c) == 1 or c.most_common(1)[0][1] > sum(c.values()) / 2:
                td = self.model.by_index[c.most_common(1)[0][0]]
                self.map_type(cls_part, td, f"resolved functions ({sum(c.values())})")
            else:
                self.classmap_cands[cls_part] = [self.model.by_index[i] for i, _ in c.most_common()]
        # identities carried by the parameter/return types of every resolved function (so that a re-run on an already
        # resolved header knows exactly what the run that resolved it knew - the class map must be reproducible)
        for hl in lines:
            if hl.kind != "func" or not hl.offset:
                continue
            hits = self.model.methods_by_rva.get(hl.offset, [])
            if len(hits) != 1:
                continue
            is_static, hp = parse_decl_params(hl.params)
            hret = parse_decl_ret(hl.ret)
            me = hits[0][1]
            if me.static == is_static and len(me.params) in (len(hp), len(hp) - 1):
                self.learn_identities(hp[:len(me.params)], hret, [me], hl.name)
        mi_by_rva = {v: k for k, v in self.model.methodinfo.items()}
        for hl in lines:
            if hl.kind != "mi" or not hl.offset:
                continue
            name = mi_by_rva.get(hl.offset)
            mm = re.match(r'^Singleton_1_(.+?)__get_Instance__MethodInfo$', hl.name)
            if name and mm:
                mm2 = re.match(r'^Method\$MoleMole\.Singleton<(.+)>\.get_Instance\(\)$', name)
                if mm2:
                    for td in self.model.find(mm2.group(1)):
                        self.map_type(mm.group(1), td, "Singleton<X>.get_Instance MethodInfo slot")
        ti_by_rva = {v: k for k, v in self.model.typeinfo.items()}
        for hl in ptr_lines:
            if hl.kind != "td" or not hl.offset:
                continue
            name = ti_by_rva.get(hl.offset)
            if name:
                for td in self.model.find(name):
                    self.map_type(hl.name, td, "TypeInfo slot")

    def paramname_match(self, hn, dn):
        if not hn or not dn:
            return False
        if hn == dn:
            return True
        c = self.paramdict.get(hn)
        return bool(c and dn in c)

    # ---- class identification for one Inspector type name (lazy, memoised in classmap)
    def ensure_class(self, insp, depth=0, pool=None):
        td = self.mapped(insp)
        if td is not None:
            return td
        if depth > 3 or insp in self.identifying:
            return None
        self.identifying.add(insp)
        try:
            return self._ensure_class(insp, depth, pool)
        finally:
            self.identifying.discard(insp)

    def _ensure_class(self, insp, depth, pool):
        cands = self.class_candidates(insp)
        if len(cands) == 1 and not cands[0].obfuscated:
            self.map_type(insp, cands[0], "readable name")
            return cands[0]
        if len(cands) > 1 and not any(t.obfuscated for t in cands):
            return None   # several readable types share the name: the method lookup disambiguates
        base = insp[len("MoleMole_"):] if insp.startswith("MoleMole_") else insp
        # XxxContext <-> MonoXxx view rule
        mm = re.match(r'^(.*?)Context$', base)
        if mm and pool is None:
            stem = mm.group(1)
            want = norm("Mono" + stem)
            owners = [t for t in self.model.types if any(norm(f.ctype.rsplit(".", 1)[-1]) == want for f in t.instance_fields())]
            if len(owners) == 1:
                self.map_type(insp, owners[0], f"owns a Mono{stem} view field")
                return owners[0]
            if len(owners) > 1:
                self.say(f"  {insp}: {len(owners)} classes own a Mono{stem} field: {[t.full for t in owners]}")
        # proto oneof member rule: an identified message whose oneof enum names this type
        if insp.startswith("Proto_") and pool is None:
            r = self.oneof_rule(insp)
            if r is not None:
                return r
        # fingerprint against a header struct
        for hdr, tag in ((self.hcur, self.ver), (self.h33, "33"), (self.h28, "28")):
            sname = None
            for cand in (insp, base, "MoleMole_" + base):
                if cand in hdr.fields:
                    sname = cand
                    break
            if not sname:
                continue
            return self.identify_by_fingerprint(insp, hdr, sname, tag, depth, pool)
        return None

    def oneof_rule(self, insp):
        for k, (ctd, how) in list(self.classmap.items()):
            if not k.startswith("Proto_") or not self.d.is_proto(ctd):
                continue
            for hdr in (self.h33, self.h28):
                if k not in hdr.fields:
                    continue
                for ctype, fname in hdr.own_fields(k):
                    if ctype.endswith("OneofCase__Enum"):
                        members = hdr.enums.get(ctype[:-len("__Enum")], {})
                        for mem in members:
                            if mem != "None" and len(mem) >= 3 and mem in insp.replace("Proto_", ""):
                                pool = [t for t in self.d.oneof_members(ctd) if t.index not in {v[0].index for v in self.classmap.values()}]
                                if len(pool) == 1:
                                    self.map_type(insp, pool[0], f"oneof member '{mem}' of {k} ({ctd.full}); the only unmapped member type")
                                    return pool[0]
                                if pool:
                                    r = self.identify_by_fingerprint(insp, hdr, insp, "33" if hdr is self.h33 else "28", 1, pool,
                                                                     poolname=f"oneof members of {k} ({ctd.full})")
                                    if r is not None:
                                        return r
        return None

    def identify_by_fingerprint(self, insp, hdr, sname, tag, depth, pool=None, poolname=None):
        own = hdr.own_fields(sname)
        if not own:
            return None
        for ctype, _ in own:
            k = header_ctype_kind(ctype)
            ident = k[1] if k[0] in ("class", "enum") else (k[1][len("class:"):] if k[0] == "rep" and k[1].startswith("class:") else None)
            if ident and ident.startswith("Proto_") and self.mapped(ident) is None and depth < 2:
                self.ensure_class(ident, depth + 1)
        htoks = self.header_field_tokens(hdr, sname)
        base = hdr.base_of(sname)
        if pool is not None and poolname is None:
            poolname = f"the parameter types of the {len(pool)} candidate methods"
        if pool is None:
            if insp.startswith("Proto_") or (base and "MessageBase" in base):
                pool = [self.model.by_index[i] for i in sorted(self.d.proto_family)]
                poolname = f"proto family ({len(pool)})"
            else:
                outer = None
                for k in sorted(self.classmap, key=len, reverse=True):
                    kk = k[len("MoleMole_"):] if k.startswith("MoleMole_") else k
                    if insp.startswith(k + "_") or insp.startswith(kk + "_") or insp.startswith("MoleMole_" + kk + "_"):
                        outer = self.classmap[k][0]
                        break
                if outer is not None:
                    pool = [t for t in self.model.types if t.nested_in == outer.name and t.namespace == outer.namespace]
                    poolname = f"types nested in {outer.full} ({len(pool)})"
                else:
                    pool = sorted(self.d.singletons.values(), key=lambda t: t.index)
                    pool2 = [t for t in self.model.types if abs(len(t.instance_fields()) - len(htoks)) <= max(3, len(htoks) // 3)]
                    pool = sorted({t.index: t for t in pool + pool2}.values(), key=lambda t: t.index)
                    poolname = f"singletons + classes with a similar field count ({len(pool)})"
        scored = []
        for t in pool:
            if t.kind not in ("class", "struct"):
                continue
            dt = self.d.field_tokens(t)
            if not dt:
                continue
            if abs(len(dt) - len(htoks)) > max(3, len(htoks) // 2):
                continue
            scored.append((similarity(htoks, dt), t))
        scored.sort(key=lambda x: (-x[0], x[1].index))
        top = scored[:3]
        desc = ", ".join(f"{t.full}={s:.2f}" for s, t in top)
        if scored and scored[0][0] >= 0.75 and (len(scored) == 1 or scored[0][0] - scored[1][0] >= 0.08 or (scored[0][0] >= 0.98 and scored[1][0] < 0.98)):
            self.map_type(insp, scored[0][1], f"field fingerprint vs appdata-{tag} {sname} ({scored[0][0]:.2f}; pool: {poolname}; next: {desc})")
            return scored[0][1]
        self.say(f"  {insp}: fingerprint vs appdata-{tag} {sname} inconclusive (pool: {poolname}): {desc}")
        if insp not in self.classmap_cands:
            self.classmap_cands[insp] = [t for s, t in top if s >= 0.6]
        return None

    def header_field_tokens(self, hdr, struct_name):
        toks = []
        for ctype, fname in hdr.own_fields(struct_name):
            k = header_ctype_kind(ctype)
            if k[0] == "prim":
                toks.append(k[1])
            elif k[0] == "rep":
                if k[1].startswith("class:"):
                    td = self.mapped(k[1][len("class:"):])
                    toks.append("rep:class" + (f":{td.index}" if td is not None else ""))
                else:
                    toks.append("rep:" + k[1])
            elif k[0] in ("class", "enum", "vstruct") and k[1] and self.mapped(k[1]) is not None:
                toks.append(f"{k[0]}:{self.mapped(k[1]).index}")
            elif k[0] == "voidptr":
                toks.append("class")
            else:
                toks.append(k[0])
        return toks

    # ---- signature matching
    def shape_score(self, hl_static, hret, hparams, me, td):
        if me.static != hl_static or len(me.params) != len(hparams):
            return None
        score = 0
        rk = self.d.type_kind(me.ret)
        hk = header_ctype_kind(hret[0], hret[1])
        if not self.kind_compat(hk, rk):
            return None
        b = self.identity_bonus(hk, rk)
        if b is None:
            return None
        score += b
        for (ct, hn, hint), dp in zip(hparams, me.params):
            hk = header_ctype_kind(ct, hint)
            dk = self.d.type_kind(dp.ctype)
            if dp.mod in ("ref", "out"):
                if not ct.endswith("*"):
                    return None
                score += 10
                continue
            if not self.kind_compat(hk, dk):
                return None
            score += 10
            b = self.identity_bonus(hk, dk)
            if b is None:
                return None
            score += b
            if self.paramname_match(hn, dp.name):
                score += 3
        return score

    @staticmethod
    def kind_compat(hk, dk):
        h, d = hk[0], dk[0]
        if h == "prim":
            return d == "prim" and hk[1] == dk[2]
        if h == "string":
            return d == "string"
        if h == "object":
            return d == "object"
        if h == "enum":
            return d == "enum"
        if h == "vstruct":
            return d == "vstruct"
        if h == "voidptr":
            return d in ("class", "list", "dict", "delegate", "generic", "array", "rep", "map", "bytes", "object", "ptr", "vstruct") or (d == "prim" and dk[2] in ("intptr_t", "uintptr_t"))
        if h == "class":
            return d in ("class", "list", "dict", "delegate", "generic", "array", "rep", "map", "bytes", "ptr")
        if h == "list":
            return d in ("list", "class", "generic")
        if h == "dict":
            return d in ("dict", "class", "generic")
        if h == "delegate":
            return d in ("delegate", "class")
        if h == "rep":
            return d in ("rep", "class", "generic")
        if h == "ptr":
            return d in ("ptr", "class", "array")
        return h == d

    def identity_bonus(self, hk, dk):
        """+4 when a mapped header class/enum is exactly the dump type; None when mapped but different; +2 readable equal"""
        if hk[0] in ("class", "enum", "vstruct") and hk[1]:
            name = hk[1]
            td = self.mapped(name)
            if td is not None:
                if dk[1] is not None and dk[1].index == td.index:
                    return 4
                if dk[1] is not None:
                    return None
                return 0
            if dk[1] is not None and not dk[1].obfuscated and norm(dk[1].short) in (norm(name), norm(re.sub(r'^MoleMole_', '', name))):
                return 2
        return 0

    def learn_identities(self, hparams, hret, cands, how):
        """all candidates agree on a dump type for a header param whose identity is still unmapped -> map it"""
        if not cands:
            return
        cols = list(zip(*[[dk for dk in ([self.d.type_kind(me.ret)] + [self.d.type_kind(p.ctype) for p in me.params])] for me in cands]))
        hk_all = [header_ctype_kind(hret[0], hret[1])] + [header_ctype_kind(ct, hint) for ct, hn, hint in hparams]
        for hk, col in zip(hk_all, cols):
            if hk[0] in ("class", "enum") and hk[1] and self.mapped(hk[1]) is None:
                tds = {dk[1].index for dk in col if dk[1] is not None}
                if len(tds) == 1 and all(dk[1] is not None for dk in col):
                    td = self.model.by_index[next(iter(tds))]
                    if td.kind == "enum" and hk[0] == "enum" or td.kind != "enum" and hk[0] == "class":
                        self.map_type(hk[1], td, f"parameter/return type of {how}")

    # ---- resolve by name
    def resolve_by_name(self, name, ret, params, within=None):
        hl_static, hparams = parse_decl_params(params)
        hret = parse_decl_ret(ret)
        results = []
        for i in range(len(name) - 1, 0, -1):
            if name[i] != "_":
                continue
            cls_part, meth_part = name[:i], name[i + 1:]
            if not meth_part or meth_part.startswith("_"):
                continue
            mm = re.match(r'^(.*?)_(\d+)$', meth_part)
            names_to_try = [(meth_part, 0)] + ([(mm.group(1), int(mm.group(2)))] if mm else [])
            cands = [within] if within is not None else self.class_candidates(cls_part)
            if within is not None and norm(cls_part) not in {norm(k) for k in list(Dump.insp_keys(within)) + [k for k, v in self.classmap.items() if v[0] is within]}:
                continue
            for td in cands:
                for mname, ov in names_to_try:
                    group = [me for me in td.methods if me.name == mname]
                    ci = False
                    if not group:
                        group = [me for me in td.methods if me.name.lower() == mname.lower()]
                        ci = True
                        if not group:
                            continue
                    if ov >= len(group):
                        continue
                    me = group[ov]
                    note = "case-insensitive" if ci else ""
                    sc = self.shape_score(hl_static, hret, hparams, me, td)
                    if sc is None:
                        alt = [(self.shape_score(hl_static, hret, hparams, x, td), x) for x in group]
                        alt = [(s, x) for s, x in alt if s is not None]
                        if len(alt) == 1:
                            me, sc = alt[0][1], alt[0][0]
                            note = (note + ", " if note else "") + "overload picked by signature"
                        else:
                            continue
                    if me.rva is None:
                        continue
                    results.append({"rva": me.rva, "dump": f"{td.full}$${me.name}", "td": td, "me": me, "score": sc,
                                    "how": "by name" + (f" ({note})" if note else ""), "cls": cls_part})
            if results:
                break
        if not results:
            return None
        results.sort(key=lambda r: (("case" in r["how"]), r["td"].obfuscated, -r["score"], r["td"].index))
        best = results[0]
        others = [r for r in results if r["rva"] != best["rva"]]
        if others and ("case" in best["how"]) == ("case" in others[0]["how"]) and others[0]["td"].obfuscated == best["td"].obfuscated and others[0]["score"] >= best["score"]:
            return {"ambiguous": [dict(r, sig=r["me"].sig()) for r in results], "reason": "several readable classes/methods fit the name equally well"}
        return best

    # ---- class part of an Inspector function name
    def split_class(self, name):
        """-> (cls_part, meth_part, TypeDef) using the class map (identifying the class on the way), else (None, None, None)"""
        for i in range(len(name) - 1, 0, -1):
            if name[i] != "_":
                continue
            cp = name[:i]
            if cp.endswith("_"):
                continue
            td = self.mapped(cp)
            if td is None and (cp in self.known_class_names or cp.startswith("MoleMole_") or cp.startswith("Proto_") or len(self.class_candidates(cp)) == 1):
                td = self.ensure_class(cp)
            if td is not None:
                return cp, name[i + 1:], td
        return None, None, None

    # ---- resolve one DO_APP_FUNC
    def resolve_func(self, hl):
        hl_static, hparams = parse_decl_params(hl.params)
        hret = parse_decl_ret(hl.ret)
        # identify the types the declaration mentions (fingerprints, oneof, ...) before scoring
        for ct, hn, hint in hparams + [(hret[0], "", hret[1])]:
            k = header_ctype_kind(ct, hint)
            if k[0] in ("class", "enum") and k[1] and self.mapped(k[1]) is None and (k[1] in self.known_class_names or k[1].startswith("Proto_") or k[1].startswith("MoleMole_")):
                self.ensure_class(k[1])
        r = self.resolve_by_name(hl.name, hl.ret, hl.params)
        if r and "rva" in r:
            return r
        name = hl.name
        # nested iterator: Outer_Method_c_Iterator0__MoveNext / Outer_Method_d__N__MoveNext
        mm = re.match(r'^(.+)_([A-Za-z][A-Za-z0-9]*)_(?:c_Iterator\d+|d__\d+)_+(\w+)$', name)
        if mm:
            outer = self.ensure_class(mm.group(1))
            if outer is None:
                return {"reason": f"outer class {mm.group(1)} not identified in the dump"}
            iters = [t for t in self.model.types if t.nested_in == outer.name and t.namespace == outer.namespace
                     and ("<" in t.short or "Iterator" in t.short or "d__" in t.short) and not t.short.startswith("<>")]
            if not iters:
                return {"reason": f"{outer.full} ({mm.group(1)}) has no compiler-generated iterator/state-machine types at all in this version: the coroutine {mm.group(2)} does not exist here"}
            want = [t for t in iters if mm.group(2).lower() in t.short.lower()]
            if len(want) == 1:
                iters = want
            else:
                # fingerprint the iterator's fields against the appdata-33 struct of the iterator
                for hdr in (self.h33, self.h28):
                    sn = next((s for s in hdr.fields if s.endswith("_" + mm.group(2) + "_Iterator") or s.endswith("_" + mm.group(2) + "_c_Iterator0")), None)
                    if sn:
                        htoks = self.header_field_tokens(hdr, sn)
                        sc = sorted(((similarity(htoks, self.d.field_tokens(t)), t) for t in iters), key=lambda x: (-x[0], x[1].index))
                        best = [t for s, t in sc if s >= 0.99]
                        if len(best) == 1:
                            iters = best
                        break
            return self.pick(name, hl_static, hret, hparams, [(t, me) for t in iters for me in t.methods if me.name == mm.group(3)],
                             how=f"nested iterator of {outer.full}")
        cls_part, meth_part, td = self.split_class(name)
        if td is None:
            nonprim = sum(1 for ct, _, _ in hparams if header_ctype_kind(ct)[0] != "prim")
            if len(hparams) >= 3 or (len(hparams) >= 2 and nonprim >= 1):
                cands = [(t, me) for t in self.model.types for me in t.methods if len(me.params) == len(hparams) and me.static == hl_static and me.rva]
                r = self.pick(name, hl_static, hret, hparams, cands, how="global shape search", margin=4)
                if (not r or "rva" not in r) and len(hparams) >= 6:
                    # one trailing parameter added after this version is tolerated
                    cands = [(t, me) for t in self.model.types for me in t.methods if len(me.params) == len(hparams) - 1 and me.static == hl_static and me.rva]
                    r = self.pick(name, hl_static, hret, hparams[:-1], cands, how=f"global shape search, {len(hparams) - 1} of {len(hparams)} parameters (the last one does not exist in this version)", margin=4)
                if r and "rva" in r:
                    cp = None
                    for i in range(len(name) - 1, 0, -1):
                        if name[i] == "_" and (name[:i] in self.known_class_names or name[:i].startswith("MoleMole_")):
                            cp = name[:i]
                            break
                    if cp:
                        self.map_type(cp, r["td"], f"global shape search for {name}")
                return r
            return {"reason": "class not identified in the dump (obfuscated) and the signature is not distinctive enough for a global search"}
        # class known: by name inside the class first (readable virtual overrides etc.)
        r = self.resolve_by_name(name, hl.ret, hl.params, within=td)
        if r and "rva" in r:
            r["how"] = r["how"].replace("by name", f"by name in {td.full}")
            return r
        ov = None
        mm = re.match(r'^(.*?)_(\d+)$', meth_part)
        if mm:
            ov = int(mm.group(2))
        cands = [(td, me) for me in td.methods if me.rva]
        r = self.pick(name, hl_static, hret, hparams, cands, how=f"signature in {td.full}", overload=ov)
        if r and "ambiguous" in r and ov is not None:
            # _N: the method must be the N-th of a same-named overload group
            r2 = self.pick(name, hl_static, hret, hparams, cands, how=f"signature in {td.full}, overload #{ov}", overload=ov, require_overload=True)
            if r2 and "rva" in r2:
                return r2
        return r

    def pick(self, name, hl_static, hret, hparams, cands, how, margin=0, overload=None, require_overload=False):
        scored = []
        for td, me in cands:
            if me.rva is None:
                continue
            sc = self.shape_score(hl_static, hret, hparams, me, td)
            if sc is None:
                continue
            if require_overload and overload is not None:
                group = [x for x in td.methods if x.name == me.name]
                if len(group) <= overload or group[overload] is not me:
                    continue
            scored.append((sc, td, me))
        if not scored:
            return {"reason": f"no method with a compatible signature ({how})"}
        # identities from the declaration narrow the candidates when every candidate carries that parameter
        self.learn_identities(hparams, hret, [me for sc, td, me in scored], name)
        scored = self.narrow_by_param_types(hparams, hret, scored, hl_static)
        scored.sort(key=lambda x: (-x[0], x[1].index, x[2].index))
        best = scored[0]
        ties = [x for x in scored if x[0] == best[0] and x[2].rva != best[2].rva]
        close = [x for x in scored[1:] if best[0] - x[0] < margin and x[2].rva != best[2].rva]
        if ties or close:
            n = (len(ties) + 1) if ties else (len(close) + 1)
            return {"ambiguous": [{"rva": me.rva, "dump": f"{td.full}$${me.name}", "sig": me.sig(), "score": sc, "td": td, "me": me} for sc, td, me in scored[:10]],
                    "reason": f"{n} candidates with the same best signature score ({how})"}
        sc, td, me = best
        return {"rva": me.rva, "dump": f"{td.full}$${me.name}", "td": td, "me": me, "score": sc, "how": how}

    def narrow_by_param_types(self, hparams, hret, scored, hl_static):
        """ambiguous candidates: identify an unmapped Proto_/class parameter type among the candidates' own parameter
        types (fingerprint restricted to that pool), then re-score so the identity bonus separates them"""
        if len({me.rva for sc, td, me in scored}) <= 1:
            return scored
        changed = False
        for i, (ct, hn, hint) in enumerate(hparams):
            hk = header_ctype_kind(ct, hint)
            if hk[0] != "class" or not hk[1] or self.mapped(hk[1]) is not None:
                continue
            pool = {}
            for sc, td, me in scored:
                dk = self.d.type_kind(me.params[i].ctype)
                if dk[1] is not None:
                    pool[dk[1].index] = dk[1]
            if len(pool) < 2:
                continue
            tdp = self.ensure_class(hk[1], pool=sorted(pool.values(), key=lambda t: t.index))
            if tdp is not None:
                changed = True
        if not changed:
            return scored
        out = []
        for sc, td, me in scored:
            sc2 = self.shape_score(hl_static, hret, hparams, me, td)
            if sc2 is not None:
                out.append((sc2, td, me))
        return out or scored

    # ---- typedef
    def resolve_typedef(self, name):
        td = self.ensure_class(name)
        if td is None:
            cands = self.class_candidates(name)
            if len(cands) == 1:
                td = cands[0]
        if td is None:
            c = self.classmap_cands.get(name)
            return {"reason": "type not identified in the dump" + (f"; fingerprint candidates: {[t.full for t in c]}" if c else "")}
        rva = self.model.typeinfo.get(td.full)
        if rva is None:
            return {"reason": f"type {td.full} identified but it has no TypeInfo metadata-usage slot in script.json"}
        return {"rva": rva, "dump": td.full + "_TypeInfo", "td": td, "how": f"TypeInfo slot; type by {self.classmap.get(name, (None, 'readable name'))[1]}"}

    # ---- main
    def run(self, dry_run=False, only=None):
        f_path = os.path.join(self.appdata, "il2cpp-functions.h")
        p_path = os.path.join(self.appdata, "il2cpp-types-ptr.h")
        f_text, p_text = read(f_path), read(p_path)
        f_lines, p_lines = parse_header(f_text), parse_header(p_text)
        print(f"== transplant --ver {self.ver}: dump {self.model.ver} ({len(self.model.types)} types), headers {self.appdata}")
        print(f"   proto message family: base {self.d.proto_base} ({len(self.d.proto_family)} messages); {len(self.d.singletons)} Singleton<X> classes")
        self.acceptance(f_lines)
        self.learn(f_lines, p_lines)
        print(f"   class map after learning: {len(self.classmap)} entries; parameter-name dictionary: {len(self.paramdict)} names")
        resolved, unresolved = [], []
        f_out = f_text.splitlines(keepends=True)
        todo = [hl for hl in f_lines if hl.unresolved and (not only or hl.name in only)]
        # two passes: identities learned while resolving one function help the others
        results = {}
        for pas in (1, 2):
            for hl in todo:
                if hl.name in results and "rva" in results[hl.name]:
                    continue
                if hl.kind == "func":
                    results[hl.name] = self.resolve_func(hl) or {"reason": "not found in the dump"}
                else:
                    results[hl.name] = {"reason": "MethodInfo slot lookup by name is not implemented"}
        for hl in todo:
            self.apply(hl, results[hl.name], f_out, resolved, unresolved)
        p_out = p_text.splitlines(keepends=True)
        for hl in p_lines:
            if not hl.unresolved or (only and hl.name not in only):
                continue
            self.apply(hl, self.resolve_typedef(hl.name), p_out, resolved, unresolved)
        if not dry_run:
            write(f_path, "".join(f_out))
            write(p_path, "".join(p_out))
        self.report(resolved, unresolved, dry_run)
        return resolved, unresolved

    def apply(self, hl, r, out, resolved, unresolved):
        line = out[hl.idx]
        nl = "\r\n" if line.endswith("\r\n") else ("\n" if line.endswith("\n") else "")
        if r and "rva" in r:
            how = r.get("how", "")
            extra = f" ({r['me'].sig()})" if r.get("me") is not None and "by name" not in how else ""
            m = hl.m
            new_off = f"0x{r['rva']:08X}"
            if hl.kind == "func":
                body2 = f"{m.group(1)}{new_off}{m.group(3)}{m.group(4)}{m.group(5)}{m.group(6)}{m.group(7)}{m.group(8)}{m.group(9)}"
            else:
                body2 = f"{m.group(1)}{new_off}{m.group(3)}{m.group(4)}{m.group(5)}"
            tail = re.sub(r'//\s*RELIC-TODO-\d+\s*', '', hl.tail).rstrip()
            comment = f"  // {VER_DIR.get(self.ver, self.ver)}: from dump ({how}) {r['dump']}{extra}"
            out[hl.idx] = body2 + (tail + " " if tail.strip() else "") + comment.replace("\n", " ") + nl
            resolved.append((hl.name, r["rva"], how, r["dump"], extra))
        else:
            reason = (r or {}).get("reason", "not found in the dump")
            cands = (r or {}).get("ambiguous", [])
            unresolved.append((hl.name, reason, [(c["rva"], c["dump"], c.get("sig", ""), c.get("score")) for c in cands]))

    def report(self, resolved, unresolved, dry_run):
        print(f"\n== resolved ({len(resolved)}):")
        for name, rva, how, dump, extra in resolved:
            print(f"  {name:<72} 0x{rva:08X}  {how}: {dump}{extra}")
        print(f"\n== unresolved ({len(unresolved)}):")
        lines = []
        for name, reason, cands in unresolved:
            print(f"  {name:<72} {reason}")
            lines.append(f"{name}\t{reason}")
            for rva, dump, sig, sc in cands:
                print(f"      candidate 0x{rva:08X} {dump}  {sig}  score={sc}")
                lines.append(f"\tcandidate 0x{rva:08X} {dump}  {sig}  score={sc}")
        outdir = os.path.join(DUMPS, VER_DIR.get(self.ver, self.ver))
        os.makedirs(outdir, exist_ok=True)
        with open(os.path.join(outdir, "unresolved.txt"), "w", encoding="utf-8", newline="\n") as f:
            f.write(f"# unresolved appdata-{self.ver} entries after transplant.py ({len(unresolved)}); reasons + candidates (RVA, dump name, signature, score)\n")
            f.write("\n".join(lines) + ("\n" if lines else ""))
        cm = {k: {"dump": v[0].full, "kind": v[0].kind, "index": v[0].index, "how": v[1]} for k, v in sorted(self.classmap.items())}
        cands = {k: [t.full for t in v] for k, v in sorted(self.classmap_cands.items()) if k not in self.classmap}
        with open(os.path.join(outdir, "classmap.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump({"ver": self.ver, "proto_base": self.d.proto_base, "classmap": cm, "candidates": cands,
                       "paramdict": {k: dict(v) for k, v in sorted(self.paramdict.items())}}, f, indent=1, sort_keys=True)
        print(f"\n== class map: {len(cm)} entries -> {os.path.join(outdir, 'classmap.json')}; unresolved list -> {os.path.join(outdir, 'unresolved.txt')}"
              + ("  (dry run: headers not written)" if dry_run else "  (headers rewritten in place)"))
        if self.verbose:
            print("\n== log:")
            print("\n".join(self.log))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ver", required=True)
    ap.add_argument("--dump", help="dump directory (default dumps_ref/<ver>)")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--verbose", "-v", action="store_true")
    ap.add_argument("--only", help="comma-separated names to process")
    a = ap.parse_args()
    t = Transplant(a.ver, a.dump, a.verbose)
    t.run(a.dry_run, set(a.only.split(",")) if a.only else None)


if __name__ == "__main__":
    main()
