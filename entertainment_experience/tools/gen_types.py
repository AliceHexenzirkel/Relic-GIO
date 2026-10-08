#!/usr/bin/env python3
"""Generates src/appdata-<ver>/il2cpp-types-relic.h: the Il2CppInspector-shaped C++ definitions of the types the 3.3
feature code needs but the upstream appdata-<ver>/il2cpp-types.h lacks, taken from the IL2CPP dump of that version
(dumps_ref/<ver>/ via dumpmodel.py) and the type identification of transplant.py (dumps_ref/<ver>/classmap.json):

    gen_types.py --ver 28 --types Proto_SceneEntityInfo,MonoTeamBtn,...   explicit list (full definitions)
    gen_types.py --ver 28 --auto [--build-log FILE]                         every type the DO_APP_FUNC / DO_TYPEDEF
                                  lines of appdata-<ver> mention and types.h does not define: by-value uses (enums,
                                  structs) get a full definition, pointer-only uses a forward declaration; with a build
                                  log, "use of undefined type 'X'" (C2027) requests a full definition and undeclared
                                  identifiers (C2061/C2065/C3861) a declaration; C2039 "'f': is not a member" lines are
                                  reported as MISSING fields (the tool cannot invent them). Combine with --types.
    Run transplant.py first (it writes classmap.json); idempotent; appends
    `#include "il2cpp-types-relic.h"` once at the end of appdata-<ver>/il2cpp-types.h (after the namespace block;
    the generated file opens `namespace app` itself). make_appdata_28.py re-copies il2cpp-types.h from ref/, so rerun
    gen_types.py after it.

Shapes (exactly what the hand-curated Akebi headers use):
    struct X__Fields { struct Base__Fields _; <fields in dump order, // 0xNN dump offset> };
    struct X { struct X__Class* klass; MonitorData* monitor; struct X__Fields fields; };   struct X__Class;  (forward)
    enum class X__Enum : int32_t { Name = 0x..., };
    List<T> -> struct List_1_T_ { ... struct T__Array* _items; int32_t _size; int32_t _version; } + struct T__Array
    RepeatedField<T> (the obfuscated protobuf collection generic) -> Google_Protobuf_Collections_Repeated{Message|
    Primitive}Field_1_T_ with the 2.8 layout (the 3.3 names).
Provenance and caveats are written next to every definition:
  * "2.8 dump" - fields and offsets from the dump. When the appdata-33 header has the same struct, its readable field
    names are transplanted onto the obfuscated 2.8 fields by aligning the two field-type sequences (equal blocks only;
    the 2.8 obfuscated name is kept in a comment; fields that exist only in 3.3 are listed as MISSING so the owner can
    gate the feature code with #if RELIC_GAME_VERSION >= 33). Enum member names likewise (values must be identical);
    nested obfuscated types take the appdata-33 nested name when one nested type of the same outer matches.
  * "FALLBACK appdata-33" - the type could not be located in the 2.8 dump (obfuscated, no fingerprint); the 3.3
    definition is copied so that the feature code compiles - the hooks that would dereference it are 0x0 on 2.8,
    i.e. never installed, so nothing reads the wrong layout at run time. Do not trust these layouts.
  * forward declarations for everything only used through a pointer.
Padding: field offsets are the dump's; where the natural C++ layout falls short of the next dump offset an explicit
uint8_t _padNN[] member is inserted, so the generated structs match the game's layout exactly.
Stdlib only; deterministic.
"""
import argparse, collections, difflib, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from dumpmodel import load, is_obf, split_params  # noqa: E402
from transplant import HeaderTypes, Dump, Transplant, parse_header, header_ctype_kind, generalize, similarity, norm, \
    read, write, CS_PRIM, VER_DIR, SRC, DUMPS  # noqa: E402

PRIM_SIZE = {"bool": 1, "uint8_t": 1, "int8_t": 1, "int16_t": 2, "uint16_t": 2, "int32_t": 4, "uint32_t": 4, "float": 4,
             "int64_t": 8, "uint64_t": 8, "double": 8, "intptr_t": 8, "uintptr_t": 8, "char": 1}
CS_ENUM_BASE = {"int": "int32_t", "uint": "uint32_t", "byte": "uint8_t", "sbyte": "int8_t", "short": "int16_t",
                "ushort": "uint16_t", "long": "int64_t", "ulong": "uint64_t"}
# Il2CppInspector spelling of primitive generic arguments / array elements
CS_PRIM_INSP = {"int": "System_Int32", "uint": "System_UInt32", "long": "System_Int64", "ulong": "System_UInt64",
                "string": "System_String", "bool": "System_Boolean", "float": "System_Single", "double": "System_Double",
                "byte": "System_Byte", "short": "System_Int16", "ushort": "System_UInt16", "object": "System_Object",
                "sbyte": "System_SByte"}
ELEM_SHORT = {"System_Int32": "Int32", "System_UInt32": "UInt32", "System_Int64": "Int64", "System_UInt64": "UInt64",
              "System_String": "String", "System_Boolean": "Boolean", "System_Single": "Single", "System_Double": "Double",
              "System_Byte": "Byte", "System_Int16": "Int16", "System_UInt16": "UInt16", "System_Object": "Object", "System_SByte": "SByte"}
ELEM_PRIM_C = {"Int32": "int32_t", "UInt32": "uint32_t", "Int64": "int64_t", "UInt64": "uint64_t", "Single": "float",
               "Double": "double", "Boolean": "bool", "Byte": "uint8_t", "Int16": "int16_t", "UInt16": "uint16_t", "SByte": "int8_t"}
BUILTIN_NAMES = {"String", "Object", "MethodInfo", "MonitorData", "Il2CppClass", "Il2CppObject", "Il2CppArrayBounds", "Type",
                 "void", "Il2CppString", "Il2CppArray", "Il2CppDelegate", "Il2CppType", "Il2CppImage", "Il2CppException"}
NO_BASE = {"object", "Object", "System.Object", "ValueType", "Enum", "MulticastDelegate", "Delegate", "Attribute"}


class Emitted:
    def __init__(self, name, text, how, size=None, align=None, kind="struct"):
        self.name, self.text, self.how, self.size, self.align, self.kind = name, text, how, size, align, kind


class Gen:
    def __init__(self, ver, dump_dir=None, verbose=False):
        self.ver = ver
        self.verbose = verbose
        self.appdata = os.path.join(SRC, f"appdata-{ver}")
        self.model = load(ver, dump_dir, quiet=not verbose)
        self.d = Dump(self.model)
        self.hcur = HeaderTypes(os.path.join(self.appdata, "il2cpp-types.h"))
        self.h33 = HeaderTypes(os.path.join(SRC, "appdata-33", "il2cpp-types.h"))
        cm_path = os.path.join(DUMPS, VER_DIR.get(ver, ver), "classmap.json")
        if not os.path.exists(cm_path):
            raise SystemExit(f"{cm_path} missing - run transplant.py --ver {ver} first")
        cm = json.load(open(cm_path, encoding="utf-8"))
        self.classmap = {}        # Inspector name -> TypeDef
        self.reverse = {}         # TypeDef index -> Inspector name
        for k, v in cm["classmap"].items():
            td = self.model.by_index.get(v["index"])
            if td is not None:
                self.classmap[k] = td
                self.reverse.setdefault(td.index, k)
        self.proto_base = cm.get("proto_base")
        # the identification machinery of transplant.py (fingerprints, oneof rule, Mono view rule) for types the class
        # map does not have yet (types only the feature code dereferences)
        self.tp = Transplant(ver, dump_dir, verbose=False)
        fl = parse_header(read(os.path.join(self.appdata, "il2cpp-functions.h")))
        pl = parse_header(read(os.path.join(self.appdata, "il2cpp-types-ptr.h")))
        self.tp.learn(fl, pl)
        for k, (td, how) in self.tp.classmap.items():
            self.classmap.setdefault(k, td)
            self.reverse.setdefault(td.index, k)
        self.out = []             # [Emitted] in dependency order
        self.done = {}            # name -> Emitted
        self.forward = set()      # struct names to forward-declare
        self.missing = []         # (struct, 3.3 field, type)
        self.notes = []
        self.size_cache = {}
        self.generic_roles = self.detect_generic_roles()
        self.hdr_enum_base = {m.group(1): m.group(2) for m in re.finditer(r'enum class\s+([A-Za-z_]\w*)__Enum\s*:\s*(\w+)', self.hcur.text)}
        self.h33_enum_base = {m.group(1): m.group(2) for m in re.finditer(r'enum class\s+([A-Za-z_]\w*)__Enum\s*:\s*(\w+)', self.h33.text)}

    def say(self, *a):
        s = " ".join(str(x) for x in a)
        self.notes.append(s)
        if self.verbose:
            print(s)

    # ---------------------------------------------------------------- naming
    def exists(self, name):
        """usable as written by the feature code: the wrapper/value struct `struct X {` or `enum X__Enum` is defined by the
        current il2cpp-types.h, or it was generated"""
        return name in self.done or name in self.hcur.exact or name in self.hcur.enums or name in self.hcur.enums_raw

    def fields_only(self, name):
        """the header has X__Fields but no `struct X {` wrapper (the 2.6-era wrapper kept an obfuscated name)"""
        return name not in self.hcur.exact and (name + "__Fields") in self.hcur.exact

    def known_anywhere(self, name):
        return self.exists(name) or name in self.h33.structs or name in self.h33.enums or name in self.h33.fields

    def insp_name(self, td):
        """Inspector name for a dump type: class map, readable name, or the obfuscated name itself"""
        if td.index in self.reverse:
            name = self.reverse[td.index]
            # the class map learned the name from function names (MoleMole_LCAvatarCombat_*); the header may define
            # the struct without the namespace (LCAvatarCombat) - prefer the spelling that exists
            if not self.exists(name):
                alt = name[len("MoleMole_"):] if name.startswith("MoleMole_") else "MoleMole_" + name
                if self.exists(alt):
                    self.reverse[td.index] = alt
                    return alt
            return name
        short = re.sub(r'`\d+', '', td.short)
        ns = td.namespace.replace(".", "_") + "_" if td.namespace else ""
        if "." in td.name:
            parts = [re.sub(r'`\d+', '', p) for p in td.name.split(".")]
            outer = self.model.find(".".join(parts[:-1]))
            outer = [t for t in outer if t.namespace == td.namespace]
            oname = self.insp_name(outer[0]) if outer else ns + "_".join(parts[:-1])
            # an obfuscated nested type of a mapped outer: take the appdata-33 nested name when exactly one fits
            if is_obf(short) and outer and (outer[0].index in self.reverse or not outer[0].obfuscated):
                n33 = self.nested_name_from_33(oname, td)
                if n33:
                    self.reverse[td.index] = n33
                    return n33
            for cand in (f"{oname}_{oname}_{short}", f"{oname}_{short}"):
                if self.known_anywhere(cand):
                    self.reverse[td.index] = cand
                    return cand
            name = f"{oname}_{oname}_{short}"
            self.reverse[td.index] = name
            return name
        for cand in (short, ns + short):
            if cand in self.hcur.structs or cand in self.hcur.enums:
                self.reverse[td.index] = cand
                return cand
        for cand in (short, ns + short):
            if cand in self.h33.structs or cand in self.h33.enums:
                self.reverse[td.index] = cand
                return cand
        self.reverse[td.index] = short
        return short

    def ancestor_prefixes(self, td):
        """Inspector names of the enclosing types (innermost first) that are mapped or readable -> candidate prefixes"""
        out = []
        parts = td.name.split(".")
        for n in range(len(parts) - 1, 0, -1):
            outer = [t for t in self.model.find(".".join(parts[:n])) if t.namespace == td.namespace]
            if not outer:
                continue
            o = outer[0]
            if o.index in self.reverse:
                out.append(self.reverse[o.index])
            elif not o.obfuscated:
                out.append(self.insp_name(o))
        res = []
        for p in out:
            res.append(p + "_")
            res.append(("MoleMole_" + p + "_") if not p.startswith("MoleMole_") else p[len("MoleMole_"):] + "_")
        return tuple(res)

    def nested_name_from_33(self, outer_insp, td):
        """the appdata-33 nested type of `outer_insp` (or any mapped ancestor) whose shape matches the obfuscated nested dump type"""
        prefixes = (outer_insp + "_", ("MoleMole_" + outer_insp + "_") if not outer_insp.startswith("MoleMole_") else outer_insp[len("MoleMole_"):] + "_") + self.ancestor_prefixes(td)
        if td.kind == "enum":
            vals = set(int(f.value, 0) for f in td.fields if f.const and f.name != "value__" and f.value and re.match(r'^-?\d+$', f.value))
            scored = []
            for n, vs in self.h33.enums.items():
                if not n.startswith(prefixes):
                    continue
                v33 = set(vs.values())
                j = len(vals & v33) / max(len(vals | v33), 1)
                if j >= 0.6:
                    scored.append((j, n))
            scored.sort(key=lambda x: (-x[0], x[1]))
            if len(scored) == 1 or (len(scored) > 1 and scored[0][0] - scored[1][0] >= 0.1):
                return scored[0][1]
            return None
        dt = generalize(self.d.field_tokens(td))
        hits = []
        for sname in self.h33.fields:
            if not sname.startswith(prefixes):
                continue
            ht = []
            for c, n in self.h33.own_fields(sname):
                k = header_ctype_kind(c)
                ht.append(k[1] if k[0] == "prim" else k[0])
            if ht and abs(len(ht) - len(dt)) <= max(1, len(dt) // 4):
                s = similarity(ht, dt)
                if s >= 0.9:
                    hits.append((s, sname))
        hits.sort(key=lambda x: (-x[0], x[1]))
        if len(hits) == 1 or (len(hits) > 1 and hits[0][0] - hits[1][0] >= 0.05):
            return hits[0][1]
        return None

    def detect_generic_roles(self):
        roles = collections.defaultdict(collections.Counter)
        for idx in self.d.proto_family:
            t = self.model.by_index[idx]
            for f in t.instance_fields():
                if "<" in f.ctype:
                    g = f.ctype[:f.ctype.index("<")]
                    if is_obf(g.rsplit(".", 1)[-1]):
                        args = split_params(f.ctype[f.ctype.index("<") + 1:-1])
                        if len(args) == 1:
                            k = self.d.type_kind(args[0])
                            roles[g]["prim" if k[0] in ("prim", "string") else "class"] += 1
                        elif len(args) == 2:
                            roles[g]["map"] += 1
        out = {}
        for g, c in roles.items():
            top = c.most_common(1)[0][0]
            out[g] = {"class": "Google_Protobuf_Collections_RepeatedMessageField", "prim": "Google_Protobuf_Collections_RepeatedPrimitiveField",
                      "map": "Google_Protobuf_Collections_MapField"}[top]
        return out

    def generic_arg_insp(self, cs):
        cs = cs.strip()
        if cs in CS_PRIM_INSP:
            return CS_PRIM_INSP[cs]
        tds = self.model.find(cs) or (self.model.find(cs.rsplit(".", 1)[-1]) if "." in cs else [])
        if tds:
            td = tds[0]
            if td.index in self.reverse:
                return self.reverse[td.index]
            short = re.sub(r'`\d+', '', td.short)
            ns = td.namespace.replace(".", "_") + "_" if td.namespace else ""
            if ns and ns + short in self.h33.text:
                return ns + short
            return self.insp_name(td)
        return re.sub(r'[^A-Za-z0-9_]', '_', cs)

    # ---------------------------------------------------------------- C# field type -> (C text, deps)
    def ctype(self, cs):
        """-> (c_text, [(name, complete?, TypeDef-or-None)])"""
        cs = cs.strip()
        if cs.startswith("ref ") or cs.startswith("out "):
            cs = cs[4:]
        if cs in CS_PRIM:
            return CS_PRIM[cs], []
        if cs == "string":
            return "struct String*", []
        if cs == "object":
            return "struct Object*", []
        if cs.endswith("[]"):
            ename = self.array_elem_name(cs[:-2])
            return f"struct {ename}__Array*", [(ename + "__Array", True, None)]
        if cs.endswith("*"):
            inner, deps = self.ctype(cs[:-1])
            return inner + "*", []
        if "<" in cs:
            g = cs[:cs.index("<")]
            args = split_params(cs[cs.index("<") + 1:-1])
            gs = g.rsplit(".", 1)[-1]
            argn = "_".join(self.generic_arg_insp(a) for a in args) + "_"
            if gs == "List" and len(args) == 1:
                name = f"List_1_{argn}"
                return f"struct {name}*", [(name, True, None)]
            if is_obf(gs) and g in self.generic_roles:
                name = f"{self.generic_roles[g]}_{len(args)}_{argn}"
                return f"struct {name}*", [(name, True, None)]
            if gs == "Nullable":
                name = f"Nullable_1_{argn}"
                return f"struct {name}", [(name, True, None)]
            base = gs if not is_obf(gs) else g.replace(".", "_")
            name = f"{base}_{len(args)}_{argn}"
            return f"struct {name}*", [(name, False, None)]
        tds = self.model.find(cs)
        if not tds and "." in cs:
            tds = self.model.find(cs.rsplit(".", 1)[-1])
        if not tds:
            return "struct Object*", []
        td = tds[0] if len(tds) == 1 else (self.classmap.get(cs) or tds[0])
        name = self.insp_name(td)
        if td.kind == "enum":
            return f"{name}__Enum", [(name, True, td)]
        if td.kind == "struct":
            return f"struct {name}", [(name, True, td)]
        return f"struct {name}*", [(name, False, td)]

    def array_elem_name(self, elem):
        if elem in CS_PRIM_INSP:
            return ELEM_SHORT[CS_PRIM_INSP[elem]]
        tds = self.model.find(elem)
        if tds:
            return self.insp_name(tds[0])
        return re.sub(r'[^A-Za-z0-9_]', '_', elem)

    # ---------------------------------------------------------------- sizes
    def csize(self, ctext):
        t = ctext.replace("const ", "").strip()
        if t.endswith("*"):
            return 8, 8
        if t in PRIM_SIZE:
            return PRIM_SIZE[t], PRIM_SIZE[t]
        if t.endswith("__Enum"):
            en = t[:-len("__Enum")]
            e = self.done.get(en)
            if e is not None and e.size:
                return e.size, e.size
            base = self.hdr_enum_base.get(en) or self.h33_enum_base.get(en)
            return PRIM_SIZE.get(base, 4), PRIM_SIZE.get(base, 4)
        if t.startswith("struct "):
            return self.struct_size(t[len("struct "):].strip())
        return 8, 8

    def struct_size(self, name):
        if name in self.size_cache:
            return self.size_cache[name]
        self.size_cache[name] = (8, 8)   # recursion guard
        key = name[:-len("__Fields")] if name.endswith("__Fields") else name
        e = self.done.get(key)
        if e is not None and e.size is not None:
            r = (e.size, e.align)
        else:
            flds = self.hcur.fields.get(key) if name.endswith("__Fields") else None
            if flds is None:
                # Relic: a struct this generator emitted without recording a size (Nullable<T>, a copied
                # FALLBACK struct) is measured from its own text first - the previous header only knows
                # the types it already had. Falling through to (0, 1) here would size every Nullable
                # member as empty and pad the whole gap after it, shifting 547 fields on 1.6.
                pat = r'^\s*struct\s+(?:__declspec\(align\(\d+\)\)\s+)?' + re.escape(name) + r'\s*\{(.*?)^\s*\};'
                m = (re.search(pat, e.text, re.S | re.M) if e is not None else None) or re.search(pat, self.hcur.text, re.S | re.M)
                body = m.group(1) if m else ""
                flds = []
                for ln in body.splitlines():
                    ln = ln.split("//")[0].strip()
                    mm = re.match(r'^(?:const\s+)?(.+?)\s+([A-Za-z_]\w*)(\[(\d+)\])?;$', ln)
                    if mm:
                        flds.append((mm.group(1).strip() + (f"[{mm.group(4)}]" if mm.group(4) else ""), mm.group(2)))
            off, al = 0, 1
            for ctype, fname in flds:
                mult = 1
                mm = re.match(r'^(.*)\[(\d+)\]$', ctype)
                if mm:
                    ctype, mult = mm.group(1), int(mm.group(2))
                s, a = self.csize(ctype)
                off = (off + a - 1) // a * a + s * mult
                al = max(al, a)
            if flds:
                # a class's __Fields struct is emitted __declspec(align(8)), so its sizeof rounds to 8
                # even when every member is smaller - IL2CPP lays the derived class out from that size
                if name.endswith("__Fields"):
                    al = max(al, 8)
                off = (off + al - 1) // al * al
            r = (off, al) if flds else (0, 1)
        self.size_cache[name] = r
        return r

    # ---------------------------------------------------------------- generation
    def ensure(self, name, complete=True, td=None, depth=0):
        if name in BUILTIN_NAMES or name in self.done or self.exists(name):
            return
        if depth > 12:
            self.forward.add(name)
            return
        mm = re.match(r'^(.+)__Class$', name)
        if mm:
            if complete:
                self.gen_meta(mm.group(1), depth)
            else:
                self.forward.add(name)
            return
        if self.fields_only(name):
            self.gen_wrapper_only(name)
            return
        if name in self.hcur.enums_raw or name in self.h33.enums_raw:
            if name not in self.hcur.enums_raw:
                self.gen_enum_33_raw(name)
            return
        mm = re.match(r'^List_1_(.+)_$', name)
        if mm:
            if complete:
                self.gen_list(name, mm.group(1), depth)
            else:
                self.forward.add(name)
            return
        mm = re.match(r'^(.+)__Array$', name)
        if mm:
            if complete:
                self.gen_array(name, mm.group(1), depth)
            else:
                self.forward.add(name)
            return
        mm = re.match(r'^Google_Protobuf_Collections_(RepeatedMessageField|RepeatedPrimitiveField|MapField)_(\d)_(.+)_$', name)
        if mm:
            if complete and mm.group(1) != "MapField":
                self.gen_repeated(name, mm.group(1), mm.group(3), depth)
            else:
                self.forward.add(name)
            return
        if td is None:
            td = self.classmap.get(name)
            if td is None and name.startswith("MoleMole_"):
                td = self.classmap.get(name[len("MoleMole_"):])
            if td is None:
                td = self.classmap.get("MoleMole_" + name)
        if td is None:
            cands = sorted(self.d.keys.get(norm(name), ()), key=lambda t: t.index)
            cands = [t for t in cands if not t.obfuscated] or cands
            if len(cands) == 1:
                td = cands[0]
            elif len(cands) > 1:
                pref = [t for t in cands if name == (t.namespace.replace(".", "_") + "_" if t.namespace else "") + re.sub(r'`\d+', '', t.short)]
                if len(pref) == 1:
                    td = pref[0]
                else:
                    self.say(f"  {name}: {len(cands)} dump types share the name ({[t.full for t in cands][:6]}) - forward declaration only")
        if td is None and complete and not cands:
            # obfuscated: let transplant's identification (fingerprints / oneof / Mono view rule) try
            td = self.tp.ensure_class(name)
            if td is not None:
                self.classmap[name] = td
                self.reverse.setdefault(td.index, name)
                self.say(f"  {name}: identified by transplant: {td.full} [{self.tp.classmap[name][1]}]")
        if td is not None:
            if td.kind == "enum":
                self.gen_enum(name, td)
            elif td.kind == "struct":
                self.gen_class(name, td, depth)            # value types are always complete
            elif td.kind == "class" and complete:
                self.gen_class(name, td, depth)
            else:
                self.forward.add(name)
            return
        if name in self.h33.enums:
            # an obfuscated 2.8 enum whose members kept their names (BeeByte leaves many enum members alone)
            etd = self.find_enum_by_members(name, self.h33.enums[name])
            if etd is not None:
                self.classmap[name] = etd
                self.reverse.setdefault(etd.index, name)
                self.gen_enum(name, etd)
                return
            self.gen_enum_33(name)
            return
        if not complete:
            self.forward.add(name)
            return
        if name in self.h33.fields or name in self.h33.structs:
            self.gen_struct_33(name, depth)
            return
        self.say(f"  {name}: not in the dump (no class map entry / readable name) and not in appdata-33 - forward declaration only")
        self.forward.add(name)

    def gen_enum(self, name, td):
        base_cs = next((f.ctype for f in td.fields if f.name == "value__"), "int")
        base = CS_ENUM_BASE.get(base_cs, "int32_t")
        members = []
        for f in td.fields:
            if f.const and f.name != "value__" and f.value is not None:
                try:
                    members.append((f.name, int(f.value, 0)))
                except ValueError:
                    pass
        h33 = self.h33.enums.get(name) or self.h33.enums.get(name[len("MoleMole_"):] if name.startswith("MoleMole_") else "MoleMole_" + name)
        by_val = {}
        if h33:
            for k, v in h33.items():
                by_val.setdefault(v, k)
        lines = [f"    enum class {name}__Enum : {base} {{"]
        renamed, seen = 0, set()
        for mname, val in members:
            out_name, note = mname, ""
            if is_obf(mname) and by_val.get(val) and by_val[val] not in seen:
                out_name, note = by_val[val], f"  // 2.8: {mname} (name from appdata-33, same value)"
                renamed += 1
            if out_name in seen:
                out_name = f"{out_name}_{val}"
            seen.add(out_name)
            lines.append(f"        {out_name} = 0x{val & 0xFFFFFFFF:08X},{note}")
        lines.append("    };")
        miss = [k for k, v in (h33 or {}).items() if v not in {x[1] for x in members}]
        how = f"2.8 dump {td.full} ({len(members)} members" + (f", {renamed} names from appdata-33" if renamed else "") + ")"
        if miss:
            how += f"; appdata-33 members absent in 2.8: {miss}"
            self.missing.append((name + "__Enum", ", ".join(miss), "enum member"))
        self.emit(Emitted(name, f"    // {name}__Enum: {how}\n" + "\n".join(lines) + "\n", how, size=PRIM_SIZE.get(base, 4), align=PRIM_SIZE.get(base, 4), kind="enum"))

    def gen_enum_33(self, name):
        m = re.search(r'^(\s*enum class\s+' + re.escape(name) + r'__Enum\s*:\s*\w+\s*\{.*?\};)', self.h33.text, re.S | re.M)
        self.say(f"  {name}__Enum: FALLBACK copied from appdata-33 (not located in the 2.8 dump)")
        self.emit(Emitted(name, f"    // {name}__Enum: FALLBACK appdata-33 definition (not located in the 2.8 dump; values unverified)\n" + m.group(1).rstrip() + "\n",
                          "FALLBACK appdata-33", size=4, align=4, kind="enum"))

    def gen_enum_33_raw(self, name):
        """enums whose 3.3 name has no __Enum suffix (GadgetType_Enum)"""
        m = re.search(r'^(\s*enum class\s+' + re.escape(name) + r'\s*:\s*\w+\s*\{.*?\};)', self.h33.text, re.S | re.M)
        if not m:
            self.forward.add(name)
            return
        etd = self.find_enum_by_members(name, self.h33.enums_raw[name])
        if etd is not None:
            members = {f.name: int(f.value, 0) for f in etd.fields if f.const and f.name != "value__" and f.value and re.match(r'^-?\d+$', f.value)}
            lines = [f"    enum class {name} : int32_t {{"] + [f"        {k} = 0x{v & 0xFFFFFFFF:08X}," for k, v in members.items()] + ["    };"]
            miss = [k for k in self.h33.enums_raw[name] if k not in members]
            how = f"2.8 dump {etd.full} ({len(members)} members, matched by member names)" + (f"; appdata-33 members absent in 2.8: {miss}" if miss else "")
            if miss:
                self.missing.append((name, ", ".join(miss), "enum member"))
            self.emit(Emitted(name, f"    // {name}: {how}\n" + "\n".join(lines) + "\n", how, size=4, align=4, kind="enum"))
            return
        self.say(f"  {name}: FALLBACK copied from appdata-33 (not located in the 2.8 dump)")
        self.emit(Emitted(name, f"    // {name}: FALLBACK appdata-33 definition (not located in the 2.8 dump; values unverified)\n" + m.group(1).rstrip() + "\n",
                          "FALLBACK appdata-33", size=4, align=4, kind="enum"))

    def find_enum_by_members(self, name, members33):
        """the dump enum whose (readable) member names best match the appdata-33 members (Jaccard >= 0.7, unique best)"""
        want = {k for k in members33 if not is_obf(k)}
        if len(want) < 3:
            return None
        scored = []
        for t in self.model.types:
            if t.kind != "enum":
                continue
            have = {f.name for f in t.fields if f.const and f.name != "value__"}
            if not have or abs(len(have) - len(members33)) > max(3, len(members33) // 2):
                continue
            j = len(want & have) / max(len(want | have), 1)
            if j >= 0.7:
                scored.append((j, t))
        scored.sort(key=lambda x: (-x[0], x[1].index))
        if len(scored) == 1 or (len(scored) > 1 and scored[0][0] - scored[1][0] >= 0.1):
            self.say(f"  {name}: enum matched by member names -> {scored[0][1].full} ({scored[0][0]:.2f})")
            return scored[0][1]
        return None

    def gen_wrapper_only(self, name):
        """the header has X__Fields (named by Il2CppInspector) but the wrapper struct kept an obfuscated 2.6-era name"""
        text = f"    struct {name} {{\n        struct {name}__Class* klass;\n        MonitorData* monitor;\n        struct {name}__Fields fields;\n    }};\n"
        self.forward.add(name + "__Class")
        self.emit(Emitted(name, f"    // {name}: wrapper for the existing {name}__Fields of this header (the header's own wrapper has an obfuscated name)\n" + text, "wrapper only"))

    def vtable_slots(self, td):
        """slot -> Method for a dump class, inherited slots included (base chain walked first, overrides replace)"""
        chain = []
        cur = td
        seen = set()
        while cur is not None and cur.index not in seen:
            seen.add(cur.index)
            chain.append(cur)
            if not cur.base or cur.base in NO_BASE:
                base = self.model.find("System.Object") if cur.base in ("object", "Object", "System.Object") or cur.kind == "class" else []
                cur = base[0] if base and base[0].index not in seen else None
            else:
                b = self.model.find(cur.base)
                if not b and "." in cur.base:
                    b = self.model.find(cur.base.rsplit(".", 1)[-1])
                cur = (b[0] if len(b) == 1 else next((x for x in b if x.namespace == cur.namespace), b[0])) if b else None
        slots = {}
        for t in reversed(chain):
            for me in t.methods:
                if me.slot is not None:
                    slots[me.slot] = (t, me)
        return slots

    def gen_meta(self, name, depth):
        """X__VTable (2.8 slot order), X__StaticFields and X__Class for a type the feature code uses through klass->"""
        td = self.classmap.get(name) or self.classmap.get("MoleMole_" + name)
        if td is None:
            cands = [t for t in self.d.keys.get(norm(name), ()) if not t.obfuscated]
            td = cands[0] if len(cands) == 1 else None
        if td is None:
            td = self.tp.ensure_class(name)
            if td is not None:
                self.classmap[name] = td
                self.reverse.setdefault(td.index, name)
        if td is None:
            self.say(f"  {name}__Class: type not identified in the dump - forward declaration only (klass->vtable use will not compile)")
            self.forward.add(name + "__Class")
            return
        if not (self.exists(name) or name in self.done):
            self.ensure(name, True, td, depth + 1)
        slots = self.vtable_slots(td)
        nslots = (max(slots) + 1) if slots else 0
        names33 = self.h33.vtables.get(name) or self.h33.vtables.get("MoleMole_" + name) or []
        # anchors: readable 2.8 names equal (modulo the _N overload suffix) to the 3.3 name at the same slot
        anchors = sum(1 for s, (t, me) in slots.items() if s < len(names33) and not is_obf(me.name) and re.sub(r'_\d+$', '', names33[s]) == me.name)
        by_slot = anchors >= 2 and abs(len(names33) - nslots) <= 2
        used = collections.Counter()
        vt_lines, notes = [], []
        for s in range(nslots):
            if s not in slots:
                vt_lines.append(f"        VirtualInvokeData _slot{s};")
                continue
            t, me = slots[s]
            mname, note = me.name, ""
            if is_obf(mname) and by_slot and s < len(names33):
                mname, note = names33[s], f"  // 2.8: {me.name} (name by slot from appdata-33 {name}__VTable: {anchors} anchors)"
            elif is_obf(mname):
                note = "  // obfuscated"
            base = mname
            if used[base]:
                mname = f"{base}_{used[base]}"
            used[base] += 1
            vt_lines.append(f"        VirtualInvokeData {mname};{note}")
        statics = [f for f in td.fields if f.static and not f.const]
        sf_lines = []
        for f in statics:
            ctext, deps = self.ctype(f.ctype)
            for dn, comp, dtd in deps:
                self.ensure(dn, comp, dtd, depth + 1)
            sf_lines.append(f"        {ctext} {f.name};" + (f" // 0x{f.offset:X}" if f.offset is not None else ""))
        out = [f"    // {name}__Class / __VTable / __StaticFields: 2.8 dump {td.full} ({nslots} vtable slots" + (f", obfuscated names taken by slot from appdata-33" if by_slot else "") + ")"]
        out.append(f"    struct {name}__VTable {{\n" + "\n".join(vt_lines) + ("\n" if vt_lines else "") + "    };")
        if sf_lines:
            out.append(f"    struct {name}__StaticFields {{\n" + "\n".join(sf_lines) + "\n    };")
        out.append(f"    struct {name}__Class {{\n        Il2CppClass_0 _0;\n        Il2CppRuntimeInterfaceOffsetPair* interfaceOffsets;\n"
                   + (f"        struct {name}__StaticFields* static_fields;\n" if sf_lines else "        void* static_fields;\n")
                   + f"        const Il2CppRGCTXData* rgctx_data;\n        Il2CppClass_1 _1;\n        struct {name}__VTable vtable;\n    }};")
        self.forward.discard(name + "__Class")
        self.emit(Emitted(name + "__Class", "\n".join(out) + "\n", f"2.8 dump {td.full} vtable ({nslots} slots)"))

    def gen_struct_33(self, name, depth):
        chunks = []
        for suffix in ("__Fields", ""):
            m = re.search(r'^(\s*struct\s+(?:__declspec\(align\(\d+\)\)\s+)?' + re.escape(name + suffix) + r'\s*\{.*?^\s*\};)', self.h33.text, re.S | re.M)
            if m:
                chunks.append(m.group(1))
        if not chunks:
            self.forward.add(name)
            return
        text = "\n".join(chunks)
        deps = set(re.findall(r'struct\s+([A-Za-z_]\w*)', text)) | set(re.findall(r'\b([A-Za-z_]\w*)__Enum\b', text))
        for dep in sorted(deps):
            if dep in (name, name + "__Fields", name + "__Class") or dep.startswith("__declspec"):
                continue
            if dep.endswith("__Fields"):
                self.ensure(dep[:-len("__Fields")], True, None, depth + 1)
            elif dep.endswith("__Class"):
                self.forward.add(dep)
            elif dep.endswith("__Array"):
                self.ensure(dep, True, None, depth + 1)
            else:
                val = re.search(r'struct\s+' + re.escape(dep) + r'\s+[A-Za-z_]\w*\s*;', text) is not None or (dep + "__Enum") in text
                self.ensure(dep, bool(val), None, depth + 1)
        self.forward.add(name + "__Class")
        self.say(f"  {name}: FALLBACK copied from appdata-33 (not located in the 2.8 dump)")
        how = "FALLBACK appdata-33 definition (NOT located in the 2.8 dump; layout unverified - compile aid only)"
        self.emit(Emitted(name, f"    // {name}: {how}\n" + text.rstrip() + "\n", how, kind="struct"))

    def gen_list(self, name, arg, depth):
        elem = ELEM_SHORT.get(arg, arg)
        self.ensure(elem + "__Array", True, None, depth + 1)
        fields = f"    struct __declspec(align(8)) {name}__Fields {{\n        struct {elem}__Array* _items;\n        int32_t _size;\n        int32_t _version;\n    }};\n"
        wrap = f"    struct {name} {{\n        struct {name}__Class* klass;\n        MonitorData* monitor;\n        struct {name}__Fields fields;\n    }};\n"
        self.forward.add(name + "__Class")
        self.emit(Emitted(name, f"    // {name}: System.Collections.Generic.List<{arg}> (standard layout)\n" + fields + wrap, "List<T> standard layout", size=16, align=8))

    def gen_array(self, name, elem, depth):
        prim = ELEM_PRIM_C.get(elem)
        if prim:
            vec = f"{prim} vector[32];"
        else:
            td = self.classmap.get(elem)
            if td is None:
                cands = [t for t in self.d.keys.get(norm(elem), ()) if not t.obfuscated]
                td = cands[0] if len(cands) == 1 else None
            if td is None:
                idx = next((i for i, n in self.reverse.items() if n == elem), None)
                td = self.model.by_index.get(idx) if idx is not None else None
            if td is not None and td.kind == "struct":
                self.ensure(elem, True, td, depth + 1)
                vec = f"struct {elem} vector[32];"
            elif td is not None and td.kind == "enum":
                self.ensure(elem, True, td, depth + 1)
                vec = f"{elem}__Enum vector[32];"
            else:
                self.ensure(elem, False, td, depth + 1)
                vec = f"struct {elem}* vector[32];"
        text = (f"    struct {name} {{\n        void* klass;\n        MonitorData* monitor;\n        Il2CppArrayBounds* bounds;\n"
                f"        il2cpp_array_size_t max_length;\n        {vec}\n    }};\n")
        self.emit(Emitted(name, f"    // {name}: array of {elem}\n" + text, "array", kind="struct"))

    def gen_repeated(self, name, role, arg, depth):
        g = next((g for g, r in self.generic_roles.items() if r.endswith(role)), None)
        gdef = next((t for t in self.model.types if g and t.generic and t.name.startswith(g + "<") and not t.namespace), None) if g else None
        cs_arg = {v: k for k, v in CS_PRIM_INSP.items()}.get(arg, arg)
        h33name = next((s for s in self.h33.fields if s.startswith(f"Google_Protobuf_Collections_{role}_1_")), None)
        lines = [f"    struct __declspec(align(8)) {name}__Fields {{"]
        if gdef is not None:
            tparam = gdef.name[gdef.name.index("<") + 1:-1]
            own = sorted([f for f in gdef.instance_fields() if f.offset is not None], key=lambda f: f.offset)
            names33 = [n for c, n in self.h33.own_fields(h33name)] if h33name else []
            toks33 = [("prim" if header_ctype_kind(c)[0] == "prim" else header_ctype_kind(c)[0]) for c, n in self.h33.own_fields(h33name)] if h33name else []
            rendered, toks28 = [], []
            for f in own:
                cs = re.sub(r'\b' + re.escape(tparam) + r'\b', cs_arg, f.ctype) if tparam else f.ctype
                ctext, deps = self.ctype(cs)
                for dn, comp, dtd in deps:
                    self.ensure(dn, comp, dtd, depth + 1)
                rendered.append((ctext, f))
                k = self.d.type_kind(cs)
                toks28.append("prim" if k[0] == "prim" else k[0])
            sm = difflib.SequenceMatcher(None, toks33, toks28, autojunk=False)
            name_of = {}
            for tag, i1, i2, j1, j2 in sm.get_opcodes():
                if tag == "equal":
                    for k in range(i2 - i1):
                        name_of[j1 + k] = names33[i1 + k]
            offs_known = any(f.offset for _, f in rendered)   # open generic definitions carry no offsets in dump.cs
            for j, (ctext, f) in enumerate(rendered):
                fname, note = f.name, ""
                if j in name_of and not is_obf(name_of[j]):
                    if fname != name_of[j]:
                        note = f"  // 2.8: {fname}"
                    fname = name_of[j]
                off = f" // 0x{f.offset:X}" if offs_known else " // (generic definition: dump order, natural layout)"
                lines.append(f"        {ctext} {fname};{off}{note}")
            how = f"2.8 dump {gdef.full} with T={cs_arg}" + (f", names from appdata-33 {h33name}" if h33name else "")
        else:
            for c, n in (self.h33.own_fields(h33name) if h33name else []):
                c2 = c.replace(h33name[len(f"Google_Protobuf_Collections_{role}_1_"):-1], arg) if h33name else c
                lines.append(f"        {c2} {n};")
                for dep in re.findall(r'struct\s+([A-Za-z_]\w*)', c2):
                    self.ensure(dep, False, None, depth + 1)
            how = "FALLBACK appdata-33 layout (collection generic not found in the 2.8 dump)"
        lines.append("    };")
        wrap = f"    struct {name} {{\n        struct {name}__Class* klass;\n        MonitorData* monitor;\n        struct {name}__Fields fields;\n    }};\n"
        self.forward.add(name + "__Class")
        self.emit(Emitted(name, f"    // {name}: {how}\n" + "\n".join(lines) + "\n" + wrap, how))

    def base_name(self, td, depth):
        """Inspector name of the base class to use as `struct Base__Fields _;` (None when the base is Object)"""
        if td.kind != "class" or not td.base or td.base in NO_BASE:
            return None
        if td.base == "MonoBehaviour" or td.base.endswith(".MonoBehaviour"):
            return "MonoBehaviour" if self.exists("MonoBehaviour") else None
        btd = self.model.find(td.base)
        if not btd and "." in td.base:
            btd = self.model.find(td.base.rsplit(".", 1)[-1])
        if not btd:
            return None
        bt = btd[0] if len(btd) == 1 else next((t for t in btd if t.namespace == td.namespace), btd[0])
        if bt.index in self.reverse:
            name = self.reverse[bt.index]
        else:
            name = self.alias_existing(bt) or self.insp_name(bt)
        self.ensure(name, True, bt, depth + 1)
        if name in self.done or self.exists(name):
            return name
        return None

    @staticmethod
    def tok33(ctype):
        k = header_ctype_kind(ctype)
        if k[0] == "prim":
            return k[1]
        if k[0] == "rep":
            return "rep:" + ("class" if k[1].startswith("class:") else k[1])
        return "class" if k[0] == "voidptr" else k[0]

    def tok28(self, cs):
        k = self.d.type_kind(cs)
        if k[0] == "prim":
            return k[2]
        if k[0] == "rep":
            return "rep:" + k[2]
        return k[0]

    def align_names(self, toks33, toks28):
        """2.8 field index -> 3.3 field index: equal blocks of the type-token sequences, then the left-over fields
        whose token is unique on both sides (proto field order differs between versions)"""
        name_of = {}
        sm = difflib.SequenceMatcher(None, toks33, toks28, autojunk=False)
        for tag, i1, i2, j1, j2 in sm.get_opcodes():
            if tag == "equal":
                for k in range(i2 - i1):
                    name_of[j1 + k] = i1 + k
        left33 = [i for i in range(len(toks33)) if i not in name_of.values()]
        left28 = [j for j in range(len(toks28)) if j not in name_of]
        c33 = collections.Counter(toks33[i] for i in left33)
        c28 = collections.Counter(toks28[j] for j in left28)
        for j in left28:
            t = toks28[j]
            if c28[t] == 1 and c33.get(t) == 1:
                name_of[j] = next(i for i in left33 if toks33[i] == t)
        return name_of

    def transfer_type(self, ctext, f, ctype33):
        """aligned field: adopt the appdata-33 type spelling when it is safe (same kind; enum values compatible;
        pointer to a struct the header knows; value struct of the same size) -> (ctext, note, deps)"""
        k33 = header_ctype_kind(ctype33)
        k28 = self.d.type_kind(f.ctype)
        if k33[0] == "enum" and k28[0] == "enum" and k28[1] is not None and ctext != k33[1] + "__Enum":
            y = k33[1]
            vals28 = {int(x.value, 0) for x in k28[1].fields if x.const and x.name != "value__" and x.value and re.match(r'^-?\d+$', x.value)}
            ref = self.hcur.enums.get(y) or self.h33.enums.get(y)
            if ref:
                v = set(ref.values())
                j = len(vals28 & v) / max(len(vals28 | v), 1)
                if j >= 0.6:
                    if y not in self.hcur.enums and y not in self.done:
                        self.reverse[k28[1].index] = y
                        self.gen_enum(y, k28[1])
                    elif y in self.hcur.enums:
                        self.reverse.setdefault(k28[1].index, y)
                    return y + "__Enum", f"  // type {ctext} in the dump", []
            return ctext, "", []
        if k33[0] == "class" and k33[1] and k28[0] in ("class",) and ctext.startswith("struct ") and ctext.endswith("*"):
            y = k33[1]
            cur = ctext[len("struct "):-1]
            if cur == y:
                return ctext, "", []
            td28 = k28[1]
            if td28 is None or td28.obfuscated or norm(re.sub(r'_\d+$', '', y)) == norm(td28.short) or (td28 is not None and norm(y) == norm(td28.short)):
                if td28 is not None and td28.obfuscated and y in self.hcur.fields:
                    # an obfuscated 2.8 class that the header defines under a readable name: check the shape before adopting
                    if self.alias_existing(td28) != y:
                        return ctext, "", []
                if y in self.hcur.structs or y in self.hcur.fields or y in self.h33.structs or y in self.h33.fields or td28 is None or td28.obfuscated:
                    if td28 is not None:
                        self.reverse.setdefault(td28.index, y)
                    return f"struct {y}*", f"  // type {cur} in the dump", [(y, False, td28)]
            return ctext, "", []
        if k33[0] == "vstruct" and k28[0] == "vstruct" and ctext.startswith("struct ") and k33[1] in self.hcur.structs:
            cur = ctext[len("struct "):]
            if cur != k33[1] and k28[1] is not None and k28[1].obfuscated:
                s28 = self.struct_size(cur) if cur in self.done else None
                s33 = self.struct_size(k33[1])
                if s28 is None or s28 == s33:
                    return f"struct {k33[1]}", f"  // type {cur} in the dump", [(k33[1], True, None)]
        return ctext, "", []

    def gen_class(self, name, td, depth):
        base_insp = self.base_name(td, depth)
        own = sorted([f for f in td.instance_fields() if f.offset is not None], key=lambda f: f.offset)
        h33name = next((c for c in (name, "MoleMole_" + name, name[len("MoleMole_"):] if name.startswith("MoleMole_") else None) if c and c in self.h33.fields), None)
        fields33 = self.h33.own_fields(h33name) if h33name else []
        names33 = [n for c, n in fields33]
        toks33 = [self.tok33(c) for c, n in fields33]
        rendered = [(self.ctype(f.ctype), f) for f in own]
        toks28 = [self.tok28(f.ctype) for f in own]
        name_of = self.align_names(toks33, toks28) if h33name else {}
        if h33name:
            used33 = set(name_of.values())
            final_names = {names33[name_of[j]] if j in name_of else f.name for j, f in enumerate(own)}
            for i, n in enumerate(names33):
                if i not in used33 and not is_obf(n) and n not in final_names:
                    self.missing.append((name, n, fields33[i][0]))
        # final field texts: aligned fields may adopt the 3.3 type spelling; then make the dependencies available
        final = []
        for j, ((ctext, deps), f) in enumerate(rendered):
            note = ""
            if j in name_of:
                ctext2, note, deps2 = self.transfer_type(ctext, f, fields33[name_of[j]][0])
                if ctext2 != ctext:
                    ctext, deps = ctext2, deps2
            for dn, comp, dtd in deps:
                self.ensure(dn, comp, dtd, depth + 1)
            final.append((ctext, f, note))
        rendered = final
        lines = []
        base_size = 0
        if base_insp:
            base_size, _ = self.struct_size(base_insp + "__Fields")
            lines.append(f"        struct {base_insp}__Fields _;")
        cur = base_size
        start = 0x10 if td.kind == "class" else 0
        npad = 0
        maxal = 8 if base_insp else 1
        for j, (ctext, f, tnote) in enumerate(rendered):
            s, a = self.csize(ctext)
            maxal = max(maxal, a)
            want = f.offset - start
            nat = (cur + a - 1) // a * a
            if want > nat:
                lines.append(f"        uint8_t _pad{f.offset:X}[{want - nat}];")
                cur = want
                npad += 1
            elif want < nat and j > 0:
                self.say(f"  {name}: field {f.name} at 0x{f.offset:X} lies before the natural offset 0x{nat + start:X} - check the layout")
            fname, note = f.name, ""
            if j in name_of and not is_obf(names33[name_of[j]]):
                if names33[name_of[j]] != fname:
                    note = f"  // 2.8: {fname}"
                fname = names33[name_of[j]]
            elif is_obf(fname) and h33name:
                note = "  // not in appdata-33"
            lines.append(f"        {ctext} {fname}; // 0x{f.offset:X}{note}{tnote}")
            cur = max(cur, want) + s
        # a class (emitted __declspec(align(8))) is sized to a multiple of 8 - that is the size the compiler
        # gives the __Fields struct and the size IL2CPP starts a derived class from; a value type keeps its own
        al8 = maxal if td.kind == "struct" else max(maxal, 8)
        size = (cur + al8 - 1) // al8 * al8 if (own or base_insp) else 0
        how = f"2.8 dump {td.full}" + (f", field names from appdata-33 {h33name}" if h33name and name_of else "") + (f", {npad} padding member(s)" if npad else "")
        if td.kind == "struct":
            body = f"    struct {name} {{\n" + "\n".join(lines) + ("\n" if lines else "") + "    };\n"
            self.emit(Emitted(name, f"    // {name}: {how} (value type)\n" + body, how, size=size, align=maxal, kind="vstruct"))
            return
        fields_txt = f"    struct __declspec(align(8)) {name}__Fields {{\n" + "\n".join(lines) + ("\n" if lines else "") + "    };\n"
        wrap = f"    struct {name} {{\n        struct {name}__Class* klass;\n        MonitorData* monitor;\n        struct {name}__Fields fields;\n    }};\n"
        self.forward.add(name + "__Class")
        self.emit(Emitted(name, f"    // {name}: {how}\n" + fields_txt + wrap, how, size=size, align=max(maxal, 8), kind="struct"))

    def alias_existing(self, td):
        """an obfuscated dump class whose field shape equals a struct the current header (or appdata-33) defines:
        use that readable name (current header: reuse the definition; appdata-33: name only, body from the dump)"""
        if not td.obfuscated:
            return None
        dt = generalize(self.d.field_tokens(td))
        if not dt:
            return None
        for hdr, tag in ((self.hcur, "cur"), (self.h33, "33")):
            best = []
            for sname in hdr.fields:
                own = hdr.own_fields(sname)
                if abs(len(own) - len(dt)) > max(1, len(dt) // 4):
                    continue
                ht = []
                for c, n in own:
                    k = header_ctype_kind(c)
                    ht.append(k[1] if k[0] == "prim" else k[0])
                s = similarity(ht, dt)
                if s >= 0.9:
                    best.append((s, sname))
            best.sort(key=lambda x: (-x[0], x[1]))
            if len(best) == 1 or (len(best) > 1 and best[0][0] - best[1][0] >= 0.05):
                self.reverse[td.index] = best[0][1]
                self.say(f"  {td.full}: same field shape as {best[0][1]}__Fields of appdata-{tag} ({best[0][0]:.2f}) - named so")
                return best[0][1]
        return None

    def emit(self, e):
        if e.name in self.done:
            return
        self.done[e.name] = e
        self.out.append(e)
        self.forward.discard(e.name)

    # ---------------------------------------------------------------- driver
    def auto_requests(self, build_log=None):
        """-> ([complete names], [declared-only names], [missing-field reports])"""
        complete, declared, missing = set(), set(), []
        for fn in ("il2cpp-functions.h", "il2cpp-types-ptr.h"):
            p = os.path.join(self.appdata, fn)
            if not os.path.exists(p):
                continue
            for ln in read(p).splitlines():
                s = ln.strip()
                if s.startswith("//"):
                    continue
                m = re.match(r'^DO_APP_FUNC\(\s*0x[0-9A-Fa-f]+\s*,\s*(.+?),\s*([A-Za-z_]\w*)\s*,\s*\((.*)\)\s*\)\s*;', s)
                if m:
                    types = [re.sub(r'/\*.*?\*/', ' ', m.group(1)).strip()]
                    for p_ in split_params(re.sub(r'/\*.*?\*/', ' ', m.group(3))):
                        toks = p_.strip().rsplit(None, 1)
                        if len(toks) == 2:
                            types.append(toks[0].strip())
                    for t in types:
                        t = t.replace("const ", "").replace("struct ", "").strip()
                        ptr = t.endswith("*")
                        ident = t.rstrip("*").strip()
                        if not re.match(r'^[A-Za-z_]\w*$', ident):
                            continue
                        if ident.endswith("__Enum"):
                            complete.add(ident[:-len("__Enum")])
                        elif ptr:
                            declared.add(ident)
                        else:
                            complete.add(ident)
                    continue
                m = re.match(r'^DO_(?:TYPEDEF|SINGLETONEDEF)\(\s*0x[0-9A-Fa-f]+\s*,\s*([A-Za-z_]\w*)\s*\)', s)
                if m:
                    declared.add(m.group(1))
        if build_log and os.path.exists(build_log):
            txt = read(build_log)
            def known(n):
                """only identifiers that are types somewhere (appdata-33, the dump) - cascaded errors name members/functions"""
                base = re.sub(r'__(Enum|Fields|Class)$', '', n)
                return (base in self.h33.structs or base in self.h33.enums or n in self.h33.enums_raw or base in self.h33.fields
                        or base in self.classmap or bool(self.d.keys.get(norm(base))))
            for m in re.finditer(r"error C2027: use of undefined type '(?:app::)?([A-Za-z_]\w*)'", txt):
                n = m.group(1)
                if n.endswith("__Class"):
                    complete.add(n)
                elif known(n):
                    complete.add(n[:-len("__Fields")] if n.endswith("__Fields") else n)
            for m in re.finditer(r"error C(?:2061|2065|3861|2079|4430): (?:syntax error: identifier |)'(?:app::)?([A-Za-z_]\w*)'", txt):
                n = m.group(1)
                if not known(n):
                    continue
                if n.endswith("__Enum"):
                    complete.add(n[:-len("__Enum")])
                elif n.endswith("__Fields"):
                    complete.add(n[:-len("__Fields")])
                elif n.endswith("__Class"):
                    complete.add(n)
                elif n in self.h33.enums_raw:
                    complete.add(n)
                else:
                    declared.add(n)
            for m in re.finditer(r"error C2039: '([A-Za-z_]\w*)': is not a member of '(?:app::)?([A-Za-z_]\w*)'", txt):
                if m.group(2) == "app":
                    n = m.group(1)          # a type name missing from the namespace, not a field
                    if known(n):
                        complete.add(n[:-len("__Enum")] if n.endswith("__Enum") else n)
                else:
                    missing.append((m.group(2), m.group(1)))
            # klass->vtable use on a type whose X__Class the header leaves incomplete: the source line names the type
            for m in re.finditer(r"^\s*(.*?)\((\d+)(?:,\d+)?\): error C2227: left of '->vtable'", txt, re.M):
                try:
                    src = read(m.group(1)).splitlines()[int(m.group(2)) - 1]
                except (OSError, IndexError):
                    continue
                for ident in re.findall(r'app::([A-Za-z_]\w*)\s*\*', src):
                    if known(ident):
                        complete.add(ident + "__Class")
        skip = set(PRIM_SIZE) | {"void", "MethodInfo", "Il2CppClass"} | BUILTIN_NAMES
        def keep(n):
            base = n
            return n not in skip and not self.exists(base) and base not in self.hcur.enums
        complete = sorted(n for n in complete if keep(n))
        declared = sorted(n for n in declared if keep(n) and n not in complete)
        return complete, declared, missing

    def run(self, complete, declared, dry_run=False, missing_from_log=()):
        for name in complete:
            self.ensure(name, True)
        for name in declared:
            self.ensure(name, False)
        out_path = os.path.join(self.appdata, "il2cpp-types-relic.h")
        vd = VER_DIR.get(self.ver, self.ver)
        lines = [
            f"// generated by tools/gen_types.py from the {vd} dump - types the 3.3 feature code needs that the upstream {vd} il2cpp-types.h lacks",
            f"// regenerate: python tools/gen_types.py --ver {self.ver} --auto [--build-log <msbuild log>] [--types ...]   (after transplant.py; do not edit by hand)",
            f"// requested complete: {', '.join(complete)}",
            f"// requested declared: {', '.join(declared)}",
            "// provenance per type: \"2.8 dump\" (fields/offsets from the dump, names aligned with appdata-33 where readable),",
            "//   \"FALLBACK appdata-33\" (NOT located in the dump: compile aid only, never dereference on this version),",
            "//   forward declarations for pointer-only use. MISSING = a 3.3 field/member the 2.8 type does not have.",
            "#pragma once",
            "",
            "#if !defined(_GHIDRA_) && !defined(_IDA_)",
            "namespace app {",
            "#endif",
            "",
            "    // ---- forward declarations (pointer-only use) ----",
        ]
        fwd = sorted(n for n in self.forward if n not in self.done and not self.exists(n) and n not in BUILTIN_NAMES)
        for n in fwd:
            lines.append(f"    struct {n};")
        lines.append("")
        lines.append("    // ---- definitions (dependency order) ----")
        for e in self.out:
            lines.append(e.text.rstrip("\n"))
            lines.append("")
        if self.missing or missing_from_log:
            lines.append("    // ---- MISSING in this version (present in appdata-33; feature code reading them needs #if RELIC_GAME_VERSION >= 33) ----")
            for s, f, t in self.missing:
                lines.append(f"    //   {s}: {f} ({t})")
            for s, f in missing_from_log:
                lines.append(f"    //   {s}: {f} (from the build log)")
            lines.append("")
        lines += ["#if !defined(_GHIDRA_) && !defined(_IDA_)", "}", "#endif", ""]
        text = "\n".join(lines)
        if not dry_run:
            write(out_path, text)
            self.append_include()
        else:
            preview = os.path.join(DUMPS, vd, "il2cpp-types-relic.preview.h")
            write(preview, text)
            print(f"  dry run: preview written to {preview}")
        print(f"== gen_types --ver {self.ver}: {len(complete)} complete + {len(declared)} declared requested -> {len(self.out)} definitions, {len(fwd)} forward declarations -> {out_path}")
        for e in self.out:
            print(f"  {e.name:<70} {e.how[:120]}")
        if fwd:
            print(f"  forward: {', '.join(fwd)}")
        if self.missing or missing_from_log:
            print("== MISSING in this version (3.3 fields/members the 2.8 type lacks):")
            for s, f, t in self.missing:
                print(f"  {s}.{f}  ({t})")
            for s, f in missing_from_log:
                print(f"  {s}.{f}  (build log)")
        if self.verbose and self.notes:
            print("== notes:")
            print("\n".join(self.notes))
        return text

    def append_include(self):
        p = os.path.join(self.appdata, "il2cpp-types.h")
        t = read(p)
        inc = '#include "il2cpp-types-relic.h"'
        if inc in t:
            return
        nl = "\r\n" if "\r\n" in t else "\n"
        t = t.rstrip("\r\n") + nl + nl + "// Relic: types the 3.3 feature code needs that this version's header lacks (generated by tools/gen_types.py)" + nl + inc + nl
        write(p, t)
        print(f"  appended {inc} to {p}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ver", required=True)
    ap.add_argument("--dump")
    ap.add_argument("--types", help="comma-separated Il2CppInspector type names (full definitions)")
    ap.add_argument("--auto", action="store_true", help="types the DO_ lines of appdata-<ver> mention and il2cpp-types.h lacks")
    ap.add_argument("--build-log", help="MSBuild log to harvest undeclared identifiers / undefined types from (with --auto)")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--fresh", action="store_true", help="do not carry over the requests recorded in the existing generated header")
    ap.add_argument("--verbose", "-v", action="store_true")
    a = ap.parse_args()
    g = Gen(a.ver, a.dump, a.verbose)
    complete, declared, missing = [], [], []
    if a.auto:
        complete, declared, missing = g.auto_requests(a.build_log)
    # accumulate: what the existing generated header was asked for stays requested (build-log iterations add, never drop)
    prev = os.path.join(g.appdata, "il2cpp-types-relic.h")
    if os.path.exists(prev) and not a.fresh:
        head = read(prev)[:20000]
        m = re.search(r'^// requested complete: (.*)$', head, re.M)
        if m:
            complete += [x.strip() for x in m.group(1).split(",") if x.strip()]
        m = re.search(r'^// requested declared: (.*)$', head, re.M)
        if m:
            declared += [x.strip() for x in m.group(1).split(",") if x.strip()]
    if a.types:
        complete += [x.strip() for x in a.types.split(",") if x.strip()]
    seen = set()
    complete = [x for x in complete if not (x in seen or seen.add(x))]
    declared = sorted({d for d in declared if d not in seen})
    if not complete and not declared:
        raise SystemExit("nothing requested (--types and/or --auto)")
    g.run(complete, declared, a.dry_run, missing)


if __name__ == "__main__":
    main()
