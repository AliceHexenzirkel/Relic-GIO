#!/usr/bin/env python3
"""Structural 2.8 -> 1.6 matcher for the Relic enhancements offsets (phase 5, game 1.6).

    matcher16.py [--dll28 ...] [--dll16 ...] [--unity28 ...] [--unity16 ...] [--dump28 dumps_ref/2.8] [--dump16 dumps_ref/1.6/greenxemotion] [-v]

Every symbol of mod/cheat-library/src/appdata-28/ (DO_APP_FUNC / DO_APP_FUNC_METHODINFO / DO_TYPEDEF with a 2.8 RVA) is looked up in
the 2.8 IL2CPP dump (obfuscated class + method), fingerprinted, and searched for in the 1.6 dump. Both clients are BeeByte-obfuscated
with DIFFERENT random names, so the match is structural. Every hit carries the rule that produced it and a confidence; ties are reported
with their candidates, never guessed silently.

Inventory (appdata-28 Il2CppInspector type name -> 2.8 dump type, class Inventory): transplant.py's class map (function RVAs, slots),
readable names (+ the `_N` duplicate rule, kind-checked against what the header declares), same obfuscated name in the 2.8 dump, enum
member names, wrapper <-> fields-struct links (`struct X { klass; monitor; struct Y__Fields fields; }`), field / base / generic-argument
usage links from already identified structs (shape-checked), the class part of function names, plain-struct fingerprints (nested types
of the mapped outer), transplant.py's field fingerprint as the last resort.

Class rules (2.8 dump type -> 1.6 dump type, iterated to a fixed point, identities learned sharpen the next round):
  C-name      readable full name identical in both dumps, unique on both sides
  C-nested    readable nested type of a matched outer type
  C-enum      enum with identical / best-Jaccard readable member names
  C-fp        code + structural fingerprint inside a structural pool (Singleton<X> classes / proto message family / nested types of the
              matched outer / global by size): string literals the class's methods load (RIP-relative loads of the ScriptString slots),
              readable call targets (E8 rel32), matched obfuscated callees and CALLERS, TypeInfo / MethodInfo slot refs, readable
              method/field names, base class, proto field numbers (sequential ones only - 2.8 packets carry shuffled numbers), generic
              instantiation arguments, field-type sequence and method-signature multiset (identity-aware bonus/penalty)
  C-usage     the same fingerprint inside the usage pool: types referenced (fields, generic args, oneof accessors, parameters/returns)
              by the counterparts of the matched classes that reference the 2.8 type the same way - tried first, the most specific pool
  C-base      base class of a matched pair (shape-checked)
  C-field     aligned field type of a matched pair (fields aligned by type-token sequence, unanimous votes only, shape-checked)
  C-generic   generic argument of an aligned field
  C-param     parameter/return type of a matched method pair (unanimous votes only)
  C-param-joint  joint decision: the parameter type whose class score AND whose method's fingerprint both win
Method rules (inside the matched class):
  M-name      readable method name (+ compatible signature; `medium` when a parameter identity differs, the API changed its type)
  M-name-arity / M-sig-prefix  readable name / unique signature with FEWER parameters in 1.6 (compatible prefix; the x64 ABI ignores the
              extra trailing arguments the 3.3 feature code passes) - `low`, ARITY note
  M-sig       the only method of the matched class with a compatible signature
  M-sig-ret   same parameters, only the return kind differs (void in 1.6) - `low`, RETURN note
  M-fp        compatible signature + code fingerprint (strings, readable calls, matched callers/callees, metadata refs, masked machine-code
              similarity, size) with a margin over the runner-up
  M-fp+param  joint decision with an unmatched parameter type
  M-pos       compatible signature + declaration-order alignment of the two method lists (SequenceMatcher)
  M-pair-order  a twin pair (two identical-signature methods on both sides) mapped by declaration order - `low`
  M-family    the best candidates are identical bodies (masked machine code >= 0.98): the best-scored one taken - `low`
  M-getter-field  trivial getter (`return this.field`) matched through the field it reads (aligned field offsets)
  M-generic   generic instantiation: owner class + method (by instantiation-argument sets) + type arguments, body in script.json
              (parameter count checked), incl. generic classes (Singleton<X>.get_Instance shared body)
Identity semantics: an unmatched 2.8 type is a wildcard; an unmatched 1.6 type is INCOMPATIBLE with a 2.8 type that already has its
counterpart (1:1) - when that leaves nothing, a lenient pass runs and the result is flagged IDENTITY-CONFLICT (`low`).
Slots: Singleton<X>.get_Instance / generic MethodInfo slots and TypeInfo slots through the class map; il2cpp API by export name;
UnityPlayer.dll functions by masked byte pattern of the 2.8 function.
Extras written for the generator: field anchors (trivial getters and single-field reads of matched method pairs pin (offset28, offset16)
pairs so gen_appdata16.py aligns those fields without guessing among same-typed neighbours) and vtable slot names (the named slots of the
appdata-28 X__VTable structs mapped to the 1.6 slot of the matched method).

The RVA math is the one the 2.8 tools verified: script.json Address is already the RVA (ImageBase 0x180000000); checked against
MoleMole.GameManager$$Update = 0x1CC5520 in the 1.6 dump (and 0x164D930 in the 2.8 one) before anything is written.

Outputs (dumps_ref/1.6/): match16.json (classes, functions, slots, api, unity, vtables, inventory, stats), classmap.json (Inspector type
name -> 1.6 dump type; consumed by gen_appdata16.py / gen_types.py), fieldanchors.json, unresolved.txt (reasons + candidates),
matcher16.log. Nothing under mod/ is touched - gen_appdata16.py writes appdata-16 from match16.json. Stdlib only; deterministic;
idempotent (cached dump models + xref indexes: dumps_ref/<ver>/model.pkl, xrefs.pkl). ~2 min with warm caches.
"""
import argparse, collections, difflib, json, os, re, struct, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from dumpmodel import load, is_obf, split_params  # noqa: E402
from transplant import (HeaderTypes, Dump, Transplant, parse_header, parse_decl_params, parse_decl_ret, header_ctype_kind,  # noqa: E402
                        similarity, generalize, norm, read, SRC, DUMPS)
from codexref import CodeXref, DEFAULT_DLL, DEFAULT_UNITY, DEFAULT_DUMP_DIR, PE  # noqa: E402

OUT_DIR = os.path.join(DUMPS, "1.6")
# Il2CppClass internals that `mov reg,[reg+disp]` reads hit (static_fields / typeHierarchy / cctor / flags) per version
RUNTIME_OFFS_28 = {0xB8, 0xC8, 0xD0, 0xD8, 0xDC, 0xE0, 0xE8, 0x12A, 0x12B}
RUNTIME_OFFS_16 = {0xA0, 0xA8, 0xB0, 0xB8, 0xBC, 0xC0, 0x109, 0x10A}
NOISE_CALL = re.compile(r'^(IFix\.|XLua\.DelegateBridge\$\$__Gen|.*\$\$__Gen_Wrap_\d+$|.*\$\$__Gen_Delegate_Imp\d+$)')
SUFFIX_RE = re.compile(r'^(.*?)(__Fields|__VTable|__StaticFields|__Class|__Boxed|__Array|__Enum__Boxed|__Enum)$')
GENERIC_HDR = re.compile(r'^(List_1_|Dictionary_2_|Nullable_1_|LinkedList_1_|LinkedListNode_1_|Tuple_\d_|Action_\d_|Func_\d_|HashSet_1_|Google_Protobuf_|UniRx_)')

W_STR, W_CALL, W_CALL_ID, W_CALL_CLS, W_CALL_SHARED, W_TI, W_MM, W_MMC = 3.0, 2.0, 2.5, 0.7, 0.3, 1.0, 1.0, 0.5
W_RNAME, W_RFIELD, W_BASE, W_PROTO, W_NESTED, W_ENUMMEM = 1.5, 1.0, 2.0, 1.5, 1.0, 1.0
CS_KEYWORD_TYPES = {"int", "uint", "byte", "sbyte", "short", "ushort", "long", "ulong", "float", "double", "bool", "char", "string", "object", "void", "decimal"}


def readable_type(td):
    return not is_obf(td.short) and not any(is_obf(seg.split("<")[0]) for seg in td.name.split("."))


def strip_generic(s):
    """'MoleMole.Singleton<X>' -> 'MoleMole.Singleton'; nested '<' handled"""
    out, depth = [], 0
    for ch in s:
        if ch == "<":
            depth += 1
        elif ch == ">":
            depth -= 1
        elif depth == 0:
            out.append(ch)
    return "".join(out)


def generic_args(s):
    """'Class$$Method<A, B>' -> ['A', 'B'] (outermost) or []"""
    i = s.find("<")
    if i < 0 or not s.endswith(">"):
        return []
    return [a.strip() for a in split_params(s[i + 1:-1])]


def wjaccard(a, b):
    """weighted Jaccard of two {key: weight} dicts"""
    if not a or not b:
        return 0.0
    inter = 0.0
    union = 0.0
    for k, w in a.items():
        if k in b:
            inter += min(w, b[k])
            union += max(w, b[k])
        else:
            union += w
    for k, w in b.items():
        if k not in a:
            union += w
    return inter / union if union else 0.0


def counter_sim(a, b):
    """bag similarity of two Counters"""
    if not a or not b:
        return 0.0
    return 2 * sum((a & b).values()) / (sum(a.values()) + sum(b.values()))


# ---------------------------------------------------------------- one game version
class Side:
    def __init__(self, ver, model, xref):
        self.ver, self.m, self.x = ver, model, xref
        self.d = Dump(model)
        self.nested = collections.defaultdict(list)
        for t in model.types:
            if t.nested_in is not None:
                self.nested[(t.namespace, t.nested_in)].append(t)
        self.by_full_ng = collections.defaultdict(list)   # full name without generic text -> [TypeDef]
        for t in model.types:
            self.by_full_ng[strip_generic(t.full)].append(t)
        self.singletons = set(self.d.singletons)          # TypeDef indexes
        self.proto = set(self.d.proto_family)
        self.uniq = {}                                      # rva -> single script name or None
        for rva, names in model.script_by_rva.items():
            self.uniq[rva] = names[0] if len(names) == 1 else None
        self.method_owner = {}                              # rva -> (td, me) when exactly one dump method has that RVA
        for rva, hits in model.methods_by_rva.items():
            if len(hits) == 1:
                self.method_owner[rva] = hits[0]
        self.enum_members = {}                              # index -> frozenset of readable member names
        for t in model.types:
            if t.kind == "enum":
                self.enum_members[t.index] = frozenset(k for k, v in t.enum_values() if not is_obf(k))
        self.fp_cache = {}
        self.cfp_cache = {}
        self.sig_cache = {}
        self.find_cache = {}
        self.cs_cache = {}
        self.callee_cache = {}
        self.meta_cache = {}
        self.top_by_kind_ns = collections.defaultdict(list)   # (kind, namespace) -> top-level types, declaration order
        self.rev_calls = collections.defaultdict(list)        # target rva -> [caller function starts]
        for f, tgts in xref.calls.items():
            for t in tgts:
                self.rev_calls[t].append(f)
        self.bodies_of_type = {}                              # TypeDef index -> [rva] (methods + generic instantiation bodies)
        self.inst_by_owner = collections.defaultdict(set)     # generic-stripped owner name -> {instantiation body rva}
        for n, rl in model.script_methods.items():
            if "<" in n and "$$" in n:
                self.inst_by_owner[strip_generic(n.split("$$", 1)[0])].update(rl)
        for t in model.types:
            if t.nested_in is None:
                self.top_by_kind_ns[(t.kind, t.namespace)].append(t)

    def bodies(self, td):
        """code addresses of a type: its methods plus the instantiation bodies of its generic methods / generic self"""
        c = self.bodies_of_type.get(td.index)
        if c is not None:
            return c
        out = {me.rva for me in td.methods if me.rva is not None}
        if td.generic or any("<" in me.name for me in td.methods):
            out.update(self.inst_by_owner.get(strip_generic(td.full), ()))
        r = sorted(out)
        self.bodies_of_type[td.index] = r
        return r

    def find_type(self, name):
        """dump type by 'Namespace.Name' / 'Name' / generic-stripped; [] if none (dictionary lookups only)"""
        c = self.find_cache.get(name)
        if c is not None:
            return c
        m = self.m
        tds = m.by_full.get(name) or m.by_name.get(name)
        if not tds:
            ng = strip_generic(name)
            tds = self.by_full_ng.get(ng) or m.by_full.get(ng)
            if not tds and "." in ng:
                short = ng.rsplit(".", 1)[-1]
                tds = [t for t in m.by_name.get(short, ()) if strip_generic(t.full) == ng]
        tds = tds or []
        self.find_cache[name] = tds
        return tds

    def type_of_cs(self, cs):
        """C# type text -> TypeDef or None (generic args stripped, arrays stripped)"""
        c = self.cs_cache.get(cs, False)
        if c is not False:
            return c
        orig = cs
        cs = cs.strip()
        for kw in ("ref ", "out ", "in ", "params "):
            if cs.startswith(kw):
                cs = cs[len(kw):]
        cs = cs.rstrip("*")
        while cs.endswith("[]"):
            cs = cs[:-2]
        k = self.d.type_kind(cs)
        if k[1] is not None:
            r = k[1]
        else:
            tds = self.find_type(cs)
            r = tds[0] if len(tds) == 1 else None
        self.cs_cache[orig] = r
        return r

    def callee_info(self, rva):
        """identity-independent description of a call target: ('R', key) readable method | ('S', key|None) shared body |
        ('O', class TypeDef|None, method base name) obfuscated | ('N',) nothing usable"""
        c = self.callee_cache.get(rva)
        if c is not None:
            return c
        names = self.m.script_by_rva.get(rva)
        if not names:
            r = ("N",)
        elif len(names) > 1:
            r = ("N",)
            if len(names) <= 4000:
                mnames = {strip_generic(n.split("$$", 1)[1]) if "$$" in n else n for n in names[:64]}
                if len(mnames) == 1:
                    mn = next(iter(mnames))
                    if not is_obf(mn) and not NOISE_CALL.match(names[0]):
                        r = ("S", "MS:" + mn)
        else:
            name = names[0]
            if NOISE_CALL.match(name) or "$$" not in name:
                r = ("N",)
            else:
                cls, meth = name.split("$$", 1)
                meth_b, cls_b = strip_generic(meth), strip_generic(cls)
                ctd = self.find_type(cls_b)
                ctd = ctd[0] if len(ctd) == 1 else None
                if ctd is not None and readable_type(ctd) and not is_obf(meth_b):
                    r = ("R", "M:" + cls_b + "$$" + meth_b)
                else:
                    r = ("O", ctd, meth_b)
        self.callee_cache[rva] = r
        return r

    def meta_info(self, kind, name):
        """identity-independent parse of a metadata slot name"""
        c = self.meta_cache.get(name)
        if c is not None:
            return c
        r = ("N",)
        if kind == "TypeInfo":
            tn = name[:-len("_TypeInfo")]
            if tn.endswith("[]") or "<" in tn:
                if not any(is_obf(seg) for seg in re.split(r'[.<>, \[\]]', tn) if seg):
                    r = ("TI", "TI:" + tn)
                else:
                    tds = self.find_type(strip_generic(tn.rstrip("[]")))
                    r = ("TIG", tds[0]) if len(tds) == 1 else ("N",)
            else:
                tds = self.find_type(tn)
                td = tds[0] if len(tds) == 1 else None
                if td is None:
                    r = ("TI", "TI:" + tn) if not any(is_obf(seg) for seg in tn.split(".")) else ("N",)
                else:
                    r = ("TIT", td)
        elif kind == "MethodInfo":
            body = name[len("Method$"):] if name.startswith("Method$") else name
            if body.endswith("()"):
                body = body[:-2]
            depth, cut = 0, -1
            for i, ch in enumerate(body):
                if ch == "<":
                    depth += 1
                elif ch == ">":
                    depth -= 1
                elif ch == "." and depth == 0:
                    cut = i
            if cut > 0:
                cls, meth = body[:cut], body[cut + 1:]
                cls_b, meth_b = strip_generic(cls), strip_generic(meth)
                tds = self.find_type(cls_b)
                ctd = tds[0] if len(tds) == 1 else None
                if ctd is not None and readable_type(ctd) and not is_obf(meth_b):
                    r = ("MM", "MM:" + cls_b + "$$" + meth_b)
                elif ctd is None and not any(is_obf(seg) for seg in re.split(r'[.<>, ]', body) if seg):
                    r = ("MM", "MM:" + body)
                else:
                    gargs = []
                    for ga in generic_args(cls) + generic_args(meth):
                        g = self.find_type(ga)
                        if len(g) == 1:
                            gargs.append(g[0])
                    r = ("MMO", ctd, meth_b, tuple(gargs))
        self.meta_cache[name] = r
        return r


# ---------------------------------------------------------------- the matcher
class Matcher:
    def __init__(self, s28, s16, verbose=False):
        self.a, self.b = s28, s16
        self.verbose = verbose
        self.c28 = {}   # index28 -> dict(i16, rule, score, conf, cands, note)
        self.c16 = {}   # index16 -> index28
        self.m28 = {}   # rva28 -> dict(rva16, rule, score, conf, cands, dump16, note)
        self.m16 = {}   # rva16 -> rva28
        self.fails = {} # index28 -> last fp_match failure details
        self.log = []
        self.round = 0
        self.code_sim = None   # callable(rva28, rva16) -> 0..1 masked machine-code similarity (set by the Runner when both DLLs are readable)
        self.code_sim_pair = None  # callable(rva16a, rva16b) -> similarity of two 1.6 bodies
        self.getter_offset = None  # callable(side, rva) -> [this+disp] offset read by a trivial getter, or None

    def say(self, *a):
        s = " ".join(str(x) for x in a)
        self.log.append(s)
        if self.verbose:
            print(s)

    # ---- identity keys
    def tkey(self, side, td):
        if td is None:
            return None
        if readable_type(td):
            return "T:" + td.full
        if side is self.a:
            return ("I:%d" % td.index) if td.index in self.c28 else ("A:%d" % td.index)   # A: 2.8 type not matched yet
        i28 = self.c16.get(td.index)
        return ("I:%d" % i28) if i28 is not None else ("U:%d" % td.index)                 # U: 1.6 type not matched yet

    def is_known16(self, td):
        return readable_type(td) or td.index in self.c16

    def callee_keys(self, side, rva):
        """keys describing a direct call target (managed method rva) - comparable across versions"""
        info = side.callee_info(rva)
        k = info[0]
        if k == "N":
            return ()
        if k == "R":
            return ((info[1], W_CALL),)
        if k == "S":
            return ((info[1], W_CALL_SHARED),)
        ctd, meth_b = info[1], info[2]
        out = []
        if side is self.a:
            out.append(("MI:%d" % rva, W_CALL_ID))
        else:
            r28 = self.m16.get(rva)
            if r28 is not None:
                out.append(("MI:%d" % r28, W_CALL_ID))
        ck = self.tkey(side, ctd) if ctd is not None else None
        if ck and ck[:2] not in ("U:", "A:"):
            out.append(("MC:" + ck + "$$" + (meth_b if not is_obf(meth_b) else "?"), W_CALL_CLS))
        return tuple(out)

    def meta_keys(self, side, kind, name):
        info = side.meta_info(kind, name)
        k = info[0]
        if k == "N":
            return ()
        if k in ("TI", "MM"):
            return ((info[1], W_TI if k == "TI" else W_MM),)
        if k == "TIT":
            tk = self.tkey(side, info[1])
            return (("TI:" + tk, W_TI),) if tk and tk[:2] not in ("U:", "A:") else ()
        if k == "TIG":
            tk = self.tkey(side, info[1])
            return (("TIG:" + tk, W_TI * 0.7),) if tk and tk[:2] not in ("U:", "A:") else ()
        if k == "MMO":
            ctd, meth_b, gargs = info[1], info[2], info[3]
            out = []
            ck = self.tkey(side, ctd) if ctd is not None else None
            if ck and ck[:2] not in ("U:", "A:"):
                out.append(("MMC:" + ck + "$$" + (meth_b if not is_obf(meth_b) else "?"), W_MMC))
            for g in gargs:
                gk = self.tkey(side, g)
                if gk and gk[:2] not in ("U:", "A:"):
                    out.append(("MMA:" + gk, W_MMC))
            return tuple(out)
        return ()

    # ---- fingerprints
    def method_fp(self, side, me):
        """{key: weight} for one method (by rva)"""
        rva = me.rva
        if rva is None:
            return {}
        ck = (rva, self.round)
        c = side.fp_cache.get(ck)
        if c is not None:
            return c
        fp = {}
        for s in side.x.strings_of(rva):
            fp["S:" + s] = W_STR
        for kind, name in side.x.meta_of(rva):
            for k, w in self.meta_keys(side, kind, name):
                fp[k] = max(fp.get(k, 0), w)
        for t in side.x.calls_of(rva):
            for k, w in self.callee_keys(side, t):
                fp[k] = max(fp.get(k, 0), w)
        # matched callers of this method
        for c in side.rev_calls.get(rva, ()):
            if side is self.a:
                if c in self.m28 and self.m28[c].get("rva16") is not None:
                    fp["CALLER:%d" % c] = 2.0
            else:
                c28 = self.m16.get(c)
                if c28 is not None:
                    fp["CALLER:%d" % c28] = 2.0
        side.fp_cache[ck] = fp
        return fp

    def sig_shape(self, side, me):
        """(static, ret-kind, (param kinds...)) with kinds comparable across versions (identity keys when known)"""
        key = (id(me), self.round)
        c = side.sig_cache.get(key)
        if c is not None:
            return c
        def kind_of(cs):
            k = side.d.type_kind(cs)
            if k[0] == "prim":
                return "p:" + k[2]
            if k[0] in ("class", "enum", "vstruct") and k[1] is not None:
                tk = self.tkey(side, k[1])
                return k[0] + ":" + tk if tk else k[0]
            if k[0] == "rep":
                return "rep:" + str(k[2])
            return k[0]
        shape = (me.static, kind_of(me.ret), tuple((kind_of(p.ctype), p.mod) for p in me.params))
        side.sig_cache[key] = shape
        return shape

    @staticmethod
    def kinds_compat(k28, k16, lenient=False):
        """generalized kinds equal; identities equal when both known (lenient: an unmatched 1.6 type is compatible with anything)"""
        if k28 == k16:
            return True
        g28, g16 = k28.split(":")[0], k16.split(":")[0]
        if g28 != g16:
            # class-ish kinds are interchangeable at the kind level
            cls_like = {"class", "list", "dict", "delegate", "generic", "array", "rep", "map", "bytes", "ptr", "object"}
            return g28 in cls_like and g16 in cls_like
        if g28 == "p" or g28 == "rep":
            return False
        # same kind. Identities: T:/I: are known on both sides -> must be equal; A: (2.8 type not matched yet) is a wildcard;
        # U: (1.6 type not matched yet) is compatible only with a 2.8 wildcard - a 2.8 type that IS matched has its
        # counterpart, so a different, unmatched 1.6 type cannot be it
        i28 = k28[len(g28) + 1:] if ":" in k28 else ""
        i16 = k16[len(g16) + 1:] if ":" in k16 else ""
        if not i28 or not i16:
            return True
        if i28.startswith("A:"):
            return True
        if i16.startswith("U:"):
            return lenient
        return i28 == i16

    def sig_compat(self, me28, me16, lenient=False):
        s28 = self.sig_shape(self.a, me28)
        s16 = self.sig_shape(self.b, me16)
        if s28[0] != s16[0] or len(s28[2]) != len(s16[2]):
            return False
        if not self.kinds_compat(s28[1], s16[1], lenient):
            return False
        for (k1, m1), (k2, m2) in zip(s28[2], s16[2]):
            if (m1 or "") != (m2 or "") or not self.kinds_compat(k1, k2, lenient):
                return False
        return True

    def sig_token(self, side, me):
        """generalized signature string (no identities) for multiset similarity and sequence alignment"""
        def g(cs):
            k = side.d.type_kind(cs)
            return ("p:" + k[2]) if k[0] == "prim" else ("rep" if k[0] == "rep" else k[0])
        return ("s" if me.static else "i") + " " + g(me.ret) + "(" + ",".join(g(p.ctype) for p in me.params) + ")"

    def id_sig_token(self, side, me):
        """signature string with cross-version identities for class/enum/struct parameters (when known)"""
        def g(cs):
            k = side.d.type_kind(cs)
            if k[0] == "prim":
                return "p:" + k[2]
            if k[0] == "rep":
                return "rep:" + str(k[2])
            if k[0] in ("class", "enum", "vstruct") and k[1] is not None:
                tk = self.tkey(side, k[1])
                if tk and tk[:2] not in ("U:", "A:"):
                    return k[0] + ":" + tk
            return k[0]
        return ("s" if me.static else "i") + " " + g(me.ret) + "(" + ",".join(g(p.ctype) for p in me.params) + ")"

    def class_fp(self, side, td):
        ck = (td.index, self.round)
        c = side.cfp_cache.get(ck)
        if c is not None:
            return c
        fp = {}
        for me in td.methods:
            for k, w in self.method_fp(side, me).items():
                fp[k] = max(fp.get(k, 0), w)
            if not is_obf(me.name) and me.name not in (".ctor", ".cctor"):
                fp["R:" + strip_generic(me.name)] = W_RNAME
        for f in td.fields:
            if not is_obf(f.name) and not f.const and f.name != "value__":
                nm = re.sub(r'^<(.+)>k__BackingField$', r'\1', f.name)
                if not is_obf(nm):
                    fp["RF:" + nm] = W_RFIELD
        if td.base:
            btd = side.type_of_cs(td.base)
            bk = self.tkey(side, btd) if btd is not None else ("T:" + td.base if not is_obf(td.base.rsplit(".", 1)[-1]) else None)
            if bk and bk[:2] not in ("U:", "A:"):
                fp["B:" + bk] = W_BASE
        for it in td.interfaces:
            if not is_obf(it.split("<")[0].rsplit(".", 1)[-1]):
                fp["IF:" + strip_generic(it)] = 0.5
        if td.index in side.proto:
            psig = self.proto_sig(side, td)
            nums = [int(n) for n, k in psig]
            if nums and nums == sorted(nums) and nums[0] == 1 and nums[-1] <= len(nums) + 3:
                for num, kind in psig:
                    fp[f"PFK:{num}:{kind}"] = W_PROTO
        for nt in side.nested.get((td.namespace, td.name), ()):
            if not is_obf(nt.short):
                fp["N:" + nt.short] = W_NESTED
        if any("<" in me.name for me in td.methods):
            for ga in self.generic_inst_args(side, td):
                gk = self.tkey(side, ga)
                if gk and gk[:2] not in ("U:", "A:"):
                    fp["GA:" + gk] = 0.6
        # matched callers of this type's code (who calls it is as stable as what it calls)
        for rva in side.bodies(td):
            for c in side.rev_calls.get(rva, ()):
                if side is self.a:
                    if c in self.m28 and self.m28[c].get("rva16") is not None:
                        fp["CALLER:%d" % c] = 1.0
                else:
                    c28 = self.m16.get(c)
                    if c28 is not None:
                        fp["CALLER:%d" % c28] = 1.0
        if td.kind == "enum":
            for k, v in td.enum_values():
                if not is_obf(k):
                    fp["E:" + k + "=" + str(v)] = W_ENUMMEM
                    fp["EN:" + k] = W_ENUMMEM * 0.5
        side.cfp_cache[ck] = fp
        return fp

    def generic_inst_args(self, side, td):
        """TypeDefs used as type arguments of the generic methods of td (MethodInfo slot names + script.json names)"""
        ck = ("ga", td.index)
        c = side.cfp_cache.get(ck)
        if c is not None:
            return c
        out = {}
        pref = "Method$" + td.full + "."
        for k in side.m.methodinfo:
            if k.startswith(pref):
                for ga in generic_args(k[len(pref):].split("(")[0]):
                    t = side.type_of_cs(ga)
                    if t is not None:
                        out[t.index] = t
        pref2 = td.full + "$$"
        for k in side.m.script_methods:
            if k.startswith(pref2) and "<" in k:
                for ga in generic_args(k[len(pref2):]):
                    t = side.type_of_cs(ga)
                    if t is not None:
                        out[t.index] = t
        r = list(out.values())
        side.cfp_cache[ck] = r
        return r

    @staticmethod
    def proto_sig(side, td):
        """[(field number, generalized kind of the instance field that follows the const)] in declaration order"""
        out, pending = [], None
        for f in td.fields:
            if f.const and f.ctype == "int" and f.value is not None and re.match(r'^-?\d+$', f.value):
                pending = f.value
                continue
            if f.static:
                continue
            if pending is not None:
                k = side.d.type_kind(f.ctype)
                kind = ("p:" + k[2]) if k[0] == "prim" else (("rep:" + str(k[2])) if k[0] == "rep" else k[0])
                out.append((pending, kind))
                pending = None
        return out

    def id_field_tokens(self, side, td):
        """field tokens with cross-version identities (matched / readable types) instead of dump indexes"""
        toks = []
        for f in sorted(td.instance_fields(), key=lambda f: (f.offset is None, f.offset or 0)):
            k = side.d.type_kind(f.ctype)
            if k[0] == "prim":
                toks.append(k[2])
            elif k[0] == "rep":
                t = "rep:" + str(k[2])
                if k[2] == "class" and k[1] is not None:
                    tk = self.tkey(side, k[1])
                    if tk and tk[:2] not in ("U:", "A:"):
                        t += ":" + tk
                toks.append(t)
            elif k[0] in ("class", "enum", "vstruct") and k[1] is not None:
                tk = self.tkey(side, k[1])
                toks.append(k[0] + ((":" + tk) if tk and tk[:2] not in ("U:", "A:") else ""))
            else:
                toks.append(k[0])
        return toks

    def class_struct_sim(self, td28, td16):
        """structural part: field-type sequence + method-signature multiset + counts. Base similarities use generalized
        kinds (an identity unknown on one side is neutral); identities known on both sides add a bonus when they agree and
        a penalty (fields) when they disagree."""
        g28 = generalize(self.a.d.field_tokens(td28))
        g16 = generalize(self.b.d.field_tokens(td16))
        fs = similarity(g28, g16) if (g28 or g16) else (1.0 if td28.kind == td16.kind else 0.0)
        if g28 and g16:
            i28 = self.id_field_tokens(self.a, td28)
            i16 = self.id_field_tokens(self.b, td16)
            if len(i28) == len(g28) and len(i16) == len(g16):
                agree = disagree = 0
                sm = difflib.SequenceMatcher(None, g28, g16, autojunk=False)
                for tag, a1, a2, b1, b2 in sm.get_opcodes():
                    if tag != "equal":
                        continue
                    for k in range(a2 - a1):
                        x, y = i28[a1 + k], i16[b1 + k]
                        if ":I:" in x or ":T:" in x:
                            if x == y:
                                agree += 1
                            elif ":I:" in y or ":T:" in y:
                                disagree += 1
                fs = max(0.0, min(1.05, fs + 0.15 * (agree - disagree) / max(len(g28), 1)))
        c28 = collections.Counter(self.sig_token(self.a, me) for me in td28.methods)
        c16 = collections.Counter(self.sig_token(self.b, me) for me in td16.methods)
        ms = counter_sim(c28, c16) if (c28 or c16) else 1.0
        if c28 and c16:
            id28 = collections.Counter(t for t in (self.id_sig_token(self.a, me) for me in td28.methods) if ":I:" in t or ":T:" in t)
            if id28:
                id16 = collections.Counter(t for t in (self.id_sig_token(self.b, me) for me in td16.methods) if ":I:" in t or ":T:" in t)
                agree = sum((id28 & id16).values())
                ms = min(1.05, ms + 0.15 * agree / max(sum(id28.values()), 1))
        n28, n16 = len(td28.methods), len(td16.methods)
        cnt = 1.0 - abs(n28 - n16) / max(n28, n16, 1)
        return fs, ms, cnt

    def class_score(self, td28, td16):
        fp28 = self.class_fp(self.a, td28)
        fp16 = self.class_fp(self.b, td16)
        wj = wjaccard(fp28, fp16)
        shared = sum(1 for k in fp28 if k in fp16 and (k[:2] in ("S:", "M:", "R:", "E:") or k[:3] in ("MI:", "MM:", "TI:") or k[:4] == "PFK:"))
        fs, ms, cnt = self.class_struct_sim(td28, td16)
        if td28.kind == "enum":
            score = 0.75 * wj + 0.25 * cnt
        else:
            score = 0.55 * wj + 0.2 * fs + 0.15 * ms + 0.1 * cnt
        return score, {"wj": round(wj, 3), "fields": round(fs, 3), "sigs": round(ms, 3), "count": round(cnt, 3), "shared": shared}

    # ---- class matching primitives
    def match_class(self, i28, i16, rule, score=None, conf="high", cands=None, note=""):
        if i28 in self.c28:
            old = self.c28[i28]["i16"]
            if old != i16:
                self.say(f"  conflict: 2.8 #{i28} {self.a.m.by_index[i28].full} already -> #{old}, ignoring {rule} -> #{i16}")
            return False
        if i16 in self.c16:
            self.say(f"  conflict: 1.6 #{i16} {self.b.m.by_index[i16].full} already taken by 2.8 #{self.c16[i16]}, ignoring {rule} for #{i28}")
            return False
        self.c28[i28] = {"i16": i16, "rule": rule, "score": score, "conf": conf, "cands": cands or [], "note": note, "round": self.round}
        self.c16[i16] = i28
        if self.verbose:
            print(f"  class {self.a.m.by_index[i28].full:40} -> {self.b.m.by_index[i16].full:40} [{rule}{'' if score is None else ' %.2f' % score}] {note}")
        return True

    def mapped16(self, td28):
        r = self.c28.get(td28.index)
        return self.b.m.by_index[r["i16"]] if r else None

    def pool_for(self, td28):
        """candidate 1.6 types for an obfuscated 2.8 type"""
        b = self.b
        kind = td28.kind
        if td28.nested_in is not None:
            outer = self.a.m.find(td28.nested_in)
            outer = [o for o in outer if o.namespace == td28.namespace]
            o16 = self.mapped16(outer[0]) if len(outer) == 1 else None
            if o16 is not None:
                pool = [t for t in b.nested.get((o16.namespace, o16.name), ()) if t.kind == kind and t.index not in self.c16]
                return pool, f"nested types of {o16.full}"
            return [], "outer type not matched"
        if td28.index in self.a.singletons:
            pool = [b.m.by_index[i] for i in sorted(b.singletons) if i not in self.c16 and b.m.by_index[i].kind == kind]
            return pool, "Singleton<X> classes"
        if td28.index in self.a.proto:
            pool = [b.m.by_index[i] for i in sorted(b.proto) if i not in self.c16]
            return pool, "proto message family"
        if kind == "enum":
            pool = [t for t in b.top_by_kind_ns.get(("enum", td28.namespace), ()) if t.index not in self.c16]
            return pool, "enums"
        n = len(td28.methods)
        lo, hi = n // 2 - 3, n * 2 + 3
        pool = [t for t in b.top_by_kind_ns.get((kind, td28.namespace), ()) if t.index not in self.c16
                and lo <= len(t.methods) <= hi and t.index not in b.proto and (t.index in b.singletons) == (td28.index in self.a.singletons)]
        # base identity prunes when the base is known on both sides
        btd = self.a.type_of_cs(td28.base) if td28.base else None
        bk = self.tkey(self.a, btd) if btd is not None else None
        if bk and (bk.startswith("T:") or btd.index in self.c28):
            b16 = self.mapped16(btd) if btd.index in self.c28 else None
            want = b16.full if b16 is not None else btd.full
            pool2 = [t for t in pool if t.base and strip_generic(t.base.rsplit(".", 1)[-1]) == strip_generic(want.rsplit(".", 1)[-1])]
            if pool2:
                return pool2, f"classes with base {want} ({len(pool2)})"
        return pool, f"global ({len(pool)} classes of similar size)"

    def usage_pool(self, td28):
        """1.6 types referenced (fields, generic arguments, proto oneof accessors, method parameters/returns) by the
        counterparts of the matched 2.8 types that reference td28 the same way"""
        refs = set()
        for i28, r in self.c28.items():
            c28 = self.a.m.by_index[i28]
            c16 = self.b.m.by_index[r["i16"]]
            if td28.index in self.referenced(self.a, c28):
                refs |= self.referenced(self.b, c16)
        pool = [self.b.m.by_index[i] for i in sorted(refs) if i not in self.c16 and self.b.m.by_index[i].kind == td28.kind]
        return pool

    def referenced(self, side, td):
        ck = ("ref", td.index)
        c = side.cfp_cache.get(ck)
        if c is not None:
            return c
        out = set()
        for f in td.instance_fields():
            t = side.type_of_cs(f.ctype)
            if t is not None:
                out.add(t.index)
            for ga in generic_args(f.ctype):
                t = side.type_of_cs(ga)
                if t is not None:
                    out.add(t.index)
        if td.index in side.proto:
            for t in side.d.oneof_members(td):
                out.add(t.index)
        for me in td.methods:
            if me.rva is None:
                continue
            for p in me.params:
                t = side.type_of_cs(p.ctype)
                if t is not None:
                    out.add(t.index)
            t = side.type_of_cs(me.ret)
            if t is not None:
                out.add(t.index)
        out.discard(td.index)
        side.cfp_cache[ck] = out
        return out

    def fp_match(self, td28, min_score=0.30, min_margin=0.10, pool=None, poolname=None, allow_shape=True):
        """fingerprint match of one 2.8 type -> (td16 or None, details). Two stages: the cheap weighted Jaccard of the
        code/name fingerprints over the whole pool, then the structural similarity for a shortlist (top fingerprint hits +
        the closest field/method-count neighbours, so a class without any version-stable token still gets a structural try)."""
        if pool is None:
            pool, poolname = self.pool_for(td28)
        if not pool:
            return None, {"reason": f"no candidates ({poolname})", "cands": []}
        fp28 = self.class_fp(self.a, td28)
        pre = sorted(((wjaccard(fp28, self.class_fp(self.b, t)), t) for t in pool), key=lambda x: (-x[0], x[1].index))
        short = [t for wj, t in pre[:12] if wj > 0]
        nf = len(td28.instance_fields())
        nm = len(td28.methods)
        strong = sum(w for k, w in fp28.items() if k[:2] in ("S:", "M:", "E:") or k[:3] in ("MI:", "MM:", "TI:", "PF:"))
        if len(short) < 3 or strong < 6 or td28.kind != "class":
            near = sorted((t for t in pool if abs(len(t.instance_fields()) - nf) <= max(2, nf // 4) and abs(len(t.methods) - nm) <= max(3, nm // 3)),
                          key=lambda t: (abs(len(t.instance_fields()) - nf) + abs(len(t.methods) - nm) / 4.0, t.index))[:60]
            seen = {t.index for t in short}
            short += [t for t in near if t.index not in seen]
        if not short:
            short = [t for wj, t in pre[:5]]
        scored = []
        for t in short:
            sc, det = self.class_score(td28, t)
            scored.append((sc, t, det))
        scored.sort(key=lambda x: (-x[0], x[1].index))
        top = scored[0]
        second = scored[1][0] if len(scored) > 1 else 0.0
        cands = [(t.full, round(sc, 3), det) for sc, t, det in scored[:4]]
        margin = top[0] - second
        strong_shape = allow_shape and top[2]["fields"] >= 0.95 and top[2]["sigs"] >= 0.8 and margin >= 0.07
        if top[0] >= min_score and (margin >= min_margin or (top[0] >= 0.6 and margin >= 0.05) or strong_shape):
            conf = "high" if (top[0] >= 0.5 and margin >= 0.2 and top[2]["shared"] >= 3) else ("medium" if margin >= 0.1 or strong_shape else "low")
            return top[1], {"score": top[0], "conf": conf, "cands": cands, "pool": poolname, "det": top[2]}
        return None, {"reason": f"no clear winner in {poolname}: best {top[0]:.2f} ({top[1].full}) vs {second:.2f}", "cands": cands}

    # ---- rule C-name
    def match_readable(self, required):
        n = 0
        for i28 in sorted(required):
            if i28 in self.c28:
                continue
            td = self.a.m.by_index[i28]
            if not readable_type(td):
                continue
            hits = [t for t in self.b.find_type(td.full) if t.full == td.full and t.kind == td.kind and t.index not in self.c16]
            if len(hits) == 1:
                n += self.match_class(i28, hits[0].index, "C-name", 1.0, "high")
            elif len(hits) > 1:
                # same readable full name several times (rare: duplicate images); pick by shape
                scored = sorted(((self.class_score(td, t)[0], t) for t in hits), key=lambda x: (-x[0], x[1].index))
                if len(scored) == 1 or scored[0][0] - scored[1][0] >= 0.1:
                    n += self.match_class(i28, scored[0][1].index, "C-name", scored[0][0], "medium", [(t.full, round(s, 3)) for s, t in scored], "duplicate readable name, best shape")
                else:
                    self.say(f"  {td.full}: {len(hits)} readable 1.6 types with the same name and shape - left unmatched")
        return n

    # ---- propagation rules
    def propagate(self):
        n = 0
        votes = collections.defaultdict(collections.Counter)
        reasons = {}
        # C-nested (readable) + C-base + C-field for every matched pair
        for i28, r in list(self.c28.items()):
            td28, td16 = self.a.m.by_index[i28], self.b.m.by_index[r["i16"]]
            # nested readable
            for nt in self.a.nested.get((td28.namespace, td28.name), ()):
                if nt.index in self.c28 or is_obf(nt.short):
                    continue
                hits = [t for t in self.b.nested.get((td16.namespace, td16.name), ()) if t.short == nt.short and t.kind == nt.kind and t.index not in self.c16]
                if len(hits) == 1:
                    n += self.match_class(nt.index, hits[0].index, "C-nested", 1.0, "high", note=f"nested in {td28.full} -> {td16.full}")
            # base
            b28 = self.a.type_of_cs(td28.base) if td28.base else None
            b16 = self.b.type_of_cs(td16.base) if td16.base else None
            if b28 is not None and b16 is not None and b28.index not in self.c28 and b16.index not in self.c16 and b28.kind == b16.kind:
                if not readable_type(b28) and not readable_type(b16):
                    votes[b28.index][b16.index] += 2
                    reasons[(b28.index, b16.index)] = f"C-base of {td28.full} -> {td16.full}"
            # fields aligned by generalized tokens
            f28 = [f for f in td28.instance_fields()]
            f16 = [f for f in td16.instance_fields()]
            t28 = generalize(self.a.d.field_tokens(td28))
            t16 = generalize(self.b.d.field_tokens(td16))
            if f28 and f16 and len(t28) == len(f28) and len(t16) == len(f16):
                sm = difflib.SequenceMatcher(None, t28, t16, autojunk=False)
                fs28 = sorted(f28, key=lambda f: (f.offset is None, f.offset or 0))
                fs16 = sorted(f16, key=lambda f: (f.offset is None, f.offset or 0))
                for tag, i1, i2, j1, j2 in sm.get_opcodes():
                    if tag != "equal":
                        continue
                    for k in range(i2 - i1):
                        fa, fb = fs28[i1 + k], fs16[j1 + k]
                        ta, tb = self.a.type_of_cs(fa.ctype), self.b.type_of_cs(fb.ctype)
                        self._vote(votes, reasons, ta, tb, f"C-field {td28.full}.{fa.name} <-> {td16.full}.{fb.name}", 1)
                        # generic arguments (List<X> <-> List<Y>, EntityHandle<X> ...)
                        ga, gb = generic_args(fa.ctype), generic_args(fb.ctype)
                        if len(ga) == len(gb):
                            for xa, xb in zip(ga, gb):
                                self._vote(votes, reasons, self.a.type_of_cs(xa), self.b.type_of_cs(xb), f"C-generic arg of {td28.full}.{fa.name}", 1)
        # C-param from matched methods
        for rva28, r in list(self.m28.items()):
            if r.get("rva16") is None:
                continue
            o28 = self.a.method_owner.get(rva28)
            o16 = self.b.method_owner.get(r["rva16"])
            if not o28 or not o16:
                continue
            me28, me16 = o28[1], o16[1]
            if len(me28.params) != len(me16.params):
                continue
            self._vote(votes, reasons, self.a.type_of_cs(me28.ret), self.b.type_of_cs(me16.ret), f"C-param return of {o28[0].full}.{me28.name}", 1)
            for pa, pb in zip(me28.params, me16.params):
                self._vote(votes, reasons, self.a.type_of_cs(pa.ctype), self.b.type_of_cs(pb.ctype), f"C-param {o28[0].full}.{me28.name}({pa.name})", 1)
        # resolve unanimous votes with a shape check
        taken16 = collections.Counter()
        for i28, c in votes.items():
            if len(c) == 1:
                taken16[next(iter(c))] += 1
        for i28, c in sorted(votes.items()):
            if i28 in self.c28 or len(c) != 1:
                if len(c) > 1:
                    self.say(f"  votes for 2.8 #{i28} {self.a.m.by_index[i28].full} split: {[(self.b.m.by_index[k].full, v) for k, v in c.most_common(3)]}")
                continue
            i16, nv = next(iter(c.items()))
            if i16 in self.c16 or taken16[i16] > 1:
                continue
            td28, td16 = self.a.m.by_index[i28], self.b.m.by_index[i16]
            sc, det = self.class_score(td28, td16)
            # shape sanity: enums by members when readable, classes by field/sig similarity
            ok = False
            if td28.kind == "enum":
                m28, m16 = self.a.enum_members.get(i28, frozenset()), self.b.enum_members.get(i16, frozenset())
                if m28 and m16:
                    j = len(m28 & m16) / max(len(m28 | m16), 1)
                    ok = j >= 0.5
                else:
                    ok = abs(len(td28.enum_values()) - len(td16.enum_values())) <= max(2, len(td28.enum_values()) // 3)
            else:
                ok = det["fields"] >= 0.5 or (det["fields"] >= 0.35 and det["sigs"] >= 0.5) or det["wj"] >= 0.3
            rule = reasons[(i28, i16)].split(" ")[0]
            if ok:
                n += self.match_class(i28, i16, rule, sc, "high" if (nv >= 2 or det["fields"] >= 0.8) else "medium", note=reasons[(i28, i16)] + f" (votes {nv})")
            else:
                self.say(f"  vote rejected by shape: {td28.full} -> {td16.full} ({reasons[(i28, i16)]}; fields {det['fields']:.2f} sigs {det['sigs']:.2f} wj {det['wj']:.2f})")
        return n

    def _vote(self, votes, reasons, ta, tb, why, w):
        if ta is None or tb is None or ta.kind != tb.kind:
            return
        if ta.index in self.c28 or tb.index in self.c16:
            return
        if readable_type(ta) or readable_type(tb):
            if readable_type(ta) and readable_type(tb) and ta.full == tb.full:
                votes[ta.index][tb.index] += w
                reasons.setdefault((ta.index, tb.index), why)
            return
        votes[ta.index][tb.index] += w
        reasons.setdefault((ta.index, tb.index), why)

    # ---- enum matching by members (C-enum)
    def match_enums(self, required):
        n = 0
        by_members = collections.defaultdict(list)
        for i16, mem in self.b.enum_members.items():
            if mem:
                by_members[mem].append(i16)
        for i28 in sorted(required):
            td = self.a.m.by_index[i28]
            if i28 in self.c28 or td.kind != "enum":
                continue
            mem = self.a.enum_members.get(i28, frozenset())
            if len(mem) < 2:
                continue
            exact = [i for i in by_members.get(mem, []) if i not in self.c16 and self.b.m.by_index[i].namespace == td.namespace]
            if len(exact) == 1:
                n += self.match_class(i28, exact[0], "C-enum", 1.0, "high", note=f"{len(mem)} identical readable members")
                continue
            td16, det = self.fp_match(td, min_score=0.4, min_margin=0.1)
            if td16 is not None and det["det"]["wj"] >= 0.4:
                n += self.match_class(i28, td16.index, "C-enum", det["score"], det["conf"], det["cands"], f"members Jaccard (pool {det['pool']})")
        return n

    # ---- method matching
    def align_methods(self, td28, td16):
        """2.8 method index -> 1.6 method index for methods inside equal blocks of the signature-token sequences"""
        a = [self.sig_token(self.a, me) + ("#" + me.name if not is_obf(me.name) else "") for me in td28.methods]
        b = [self.sig_token(self.b, me) + ("#" + me.name if not is_obf(me.name) else "") for me in td16.methods]
        sm = difflib.SequenceMatcher(None, a, b, autojunk=False)
        out = {}
        for tag, i1, i2, j1, j2 in sm.get_opcodes():
            if tag == "equal":
                for k in range(i2 - i1):
                    out[i1 + k] = j1 + k
        return out, sm.ratio()

    def anchors(self, me28, me16):
        """number of parameter positions whose class/enum/struct identity is known on both sides and equal"""
        s28, s16 = self.sig_shape(self.a, me28), self.sig_shape(self.b, me16)
        n = 0
        for i in range(min(len(s28[2]), len(s16[2]))):
            k28, k16 = s28[2][i][0], s16[2][i][0]
            if ":" in k28 and k28.split(":", 1)[0] in ("class", "enum", "vstruct") and k28 == k16 and k28.split(":")[1] in ("I", "T"):
                n += 1
        if ":" in s28[1] and s28[1].split(":", 1)[0] in ("class", "enum", "vstruct") and s28[1] == s16[1] and s28[1].split(":")[1] in ("I", "T"):
            n += 1
        return n

    def prefix_compat(self, me28, me16):
        """me16 has fewer parameters and they are a compatible prefix of me28's (return kinds compatible)"""
        if me16.static != me28.static or len(me16.params) >= len(me28.params) or not me16.params:
            return False
        s28, s16 = self.sig_shape(self.a, me28), self.sig_shape(self.b, me16)
        if not self.kinds_compat(s28[1], s16[1]):
            return False
        return all(self.kinds_compat(s28[2][i][0], s16[2][i][0]) and (s28[2][i][1] or "") == (s16[2][i][1] or "") for i in range(len(me16.params)))

    def match_method(self, td28, me28, td16, want_name=None):
        """-> dict(rva16, dump16, rule, score, conf, cands, note) or dict(reason, cands)"""
        rname = not is_obf(me28.name)
        keeps_names = any(not is_obf(me.name) and me.name not in (".ctor", ".cctor") for me in td16.methods)
        same = [me for me in td16.methods if me.rva is not None and self.sig_compat(me28, me)]
        lenient_note = ""
        if not same:
            # the strict identity rule left nothing: a 2.8 parameter type is matched to X16 but every same-shaped 1.6
            # method takes an unmatched type - either that class match is wrong or the API changed; try leniently, flagged
            same = [me for me in td16.methods if me.rva is not None and self.sig_compat(me28, me, lenient=True)]
            if same:
                lenient_note = "IDENTITY-CONFLICT: a matched 2.8 parameter type is not the type the 1.6 method takes (check the class match); "
        # readable names first
        if rname:
            named = [me for me in same if me.name == me28.name]
            if len(named) == 1:
                me = named[0]
                return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-name", "score": 1.0, "conf": "high", "cands": [], "note": "", "me": me}
            if len(named) > 1:
                return self.score_candidates(td28, me28, td16, named, rname)
            named16 = [me for me in td16.methods if me.name == me28.name and me.rva is not None]
            if named16:
                # same readable name and arity, generalized kinds equal, but a parameter/return identity differs (the API
                # changed its parameter type between the versions): the 1.6 method of that name IS the counterpart
                same_name = [me for me in named16 if me.static == me28.static and len(me.params) == len(me28.params) and self.sig_token(self.b, me) == self.sig_token(self.a, me28)]
                if len(same_name) == 1:
                    me = same_name[0]
                    s28, s16 = self.sig_shape(self.a, me28), self.sig_shape(self.b, me)
                    diffs = [f"{p28.ctype}->{p16.ctype}" for i, (p28, p16) in enumerate(zip(me28.params, me.params)) if not self.kinds_compat(s28[2][i][0], s16[2][i][0])]
                    return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-name", "score": 0.8, "conf": "medium", "cands": [],
                            "note": f"same name and arity; parameter type identity differs in 1.6 ({', '.join(diffs) or 'return'}): {me.sig()}", "me": me}
                pre = [me for me in named16 if me.static == me28.static and len(me.params) <= len(me28.params) and
                       all(self.kinds_compat(self.sig_shape(self.a, me28)[2][i][0], self.sig_shape(self.b, me)[2][i][0]) for i in range(len(me.params)))]
                if len(pre) == 1:
                    me = pre[0]
                    ret_note = "" if self.kinds_compat(self.sig_shape(self.a, me28)[1], self.sig_shape(self.b, me)[1]) else f"; RETURN differs ({me.ret} in 1.6)"
                    return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-name-arity", "score": 0.6, "conf": "low", "cands": [],
                            "note": f"ARITY: 1.6 has {len(me.params)} parameter(s) vs {len(me28.params)} in 2.8 ({me.sig()}); trailing arguments are ignored by the x64 ABI{ret_note}", "me": me}
                return {"reason": f"{len(named16)} method(s) named {me28.name} in {td16.full} but none with a compatible (prefix) signature: {[m.sig() for m in named16][:4]}", "cands": []}
            if keeps_names:
                # the 1.6 class keeps readable names and this one is missing: only a strong code fingerprint may still match it
                fp28 = self.method_fp(self.a, me28)
                best = sorted(((wjaccard(fp28, self.method_fp(self.b, me)), me) for me in same), key=lambda x: (-x[0], x[1].index))
                if not best or best[0][0] < 0.45:
                    return {"reason": f"readable method {me28.name} does not exist in the 1.6 class {td16.full} (its other readable names survive) - absent or renamed in 1.6",
                            "cands": [(f"{td16.full}$${me.name}", hex(me.rva), round(wj, 3)) for wj, me in best[:4]]}
        # shorter 1.6 signatures that are a compatible prefix: kept when anchored by an identity-matched parameter, or long enough
        pre = []
        for me in td16.methods:
            if me.rva is None or not self.prefix_compat(me28, me):
                continue
            if self.anchors(me28, me) >= 1 or len(me.params) >= max(3, int(len(me28.params) * 0.6)):
                pre.append(me)
        # identity anchoring decides between same-arity candidates and anchored shorter ones
        best_same = max((self.anchors(me28, me) for me in same), default=-1)
        best_pre = max((self.anchors(me28, me) for me in pre), default=-1)
        if pre and best_pre > best_same:
            cands = pre
        elif same:
            cands = same + [me for me in pre if self.anchors(me28, me) >= best_same and best_same > 0]
        else:
            cands = pre
        if not cands:
            # relaxed: generalized kinds only (an identity on one side may still be wrong/unmatched), same count/static
            relaxed = [me for me in td16.methods if me.rva is not None and me.static == me28.static and len(me.params) == len(me28.params)
                       and self.sig_token(self.b, me) == self.sig_token(self.a, me28)]
            if rname:
                relaxed = [me for me in relaxed if me.name == me28.name]
            if len(relaxed) == 1:
                me = relaxed[0]
                return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-name" if rname else "M-sig", "score": 0.5, "conf": "low",
                        "cands": [], "note": f"only method with that {'name and ' if rname else ''}arity in {td16.full} (parameter kinds not all comparable yet)", "me": me}
            # same arity, every parameter compatible, only the return kind differs (e.g. void instead of the context object)
            s28 = self.sig_shape(self.a, me28)
            retd = []
            for me in td16.methods:
                if me.rva is None or me.static != me28.static or len(me.params) != len(me28.params):
                    continue
                s16 = self.sig_shape(self.b, me)
                if all(self.kinds_compat(s28[2][i][0], s16[2][i][0]) and (s28[2][i][1] or "") == (s16[2][i][1] or "") for i in range(len(me.params))):
                    retd.append(me)
            if len(retd) == 1 and len(me28.params) >= 2:
                me = retd[0]
                wj = wjaccard(self.method_fp(self.a, me28), self.method_fp(self.b, me))
                return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-sig-ret", "score": 0.4 + wj, "conf": "low", "cands": [],
                        "note": f"RETURN differs: 1.6 {me.sig()} (2.8 returned {me28.ret}); the caller must not use the return value; fp {wj:.2f}", "me": me}
            return {"reason": f"no method of {td16.full} has a compatible signature ({me28.sig()})", "cands": []}
        res = self.score_candidates(td28, me28, td16, cands, rname)
        if "rva16" in res and lenient_note and res["me"] in same:
            res["note"] = lenient_note + res.get("note", "")
            res["conf"] = "low"
        if "rva16" in res and len(res["me"].params) != len(me28.params):
            res["note"] = f"ARITY: 1.6 has {len(res['me'].params)} parameter(s) vs {len(me28.params)} in 2.8 ({res['me'].sig()}); trailing arguments are ignored by the x64 ABI; " + res.get("note", "")
            res["conf"] = "low" if res.get("conf") in ("medium", "high") and res["rule"] != "M-sig" else res.get("conf")
            res["rule"] = res["rule"] + "-prefix"
        return res

    def score_candidates(self, td28, me28, td16, cands, rname):
        taken = [me for me in cands if me.rva in self.m16 and self.m16[me.rva] != me28.rva]
        if taken and len(taken) < len(cands):
            cands = [me for me in cands if me not in taken]
        fp28 = self.method_fp(self.a, me28)
        pos, ratio = self.align_methods(td28, td16)
        j_aligned = pos.get(me28.index)
        scored = []
        for me in cands:
            fp16 = self.method_fp(self.b, me)
            wj = wjaccard(fp28, fp16)
            shared = sum(1 for k in fp28 if k in fp16 and (k[:2] in ("S:", "M:") or k.startswith("MI:")))
            bonus = 0.0
            if rname and me.name == me28.name:
                bonus += 0.3
            if j_aligned is not None and j_aligned == me.index:
                bonus += 0.25
            bonus += 0.3 * self.anchors(me28, me)
            pn = sum(1 for p, q in zip(me28.params, me.params) if p.name and p.name == q.name and not is_obf(p.name))
            bonus += 0.05 * pn
            s28, s16 = self.a.x.size_of(me28.rva), self.b.x.size_of(me.rva)
            if s28 and s16:
                bonus += 0.1 * (1 - min(1.0, abs(s28 - s16) / max(s28, s16)))
            # machine-code similarity (same compiler, same IL -> near-identical bytes apart from addresses): the tie-breaker
            # that sees the constants the metadata does not (e.g. ms vs s timestamp getters)
            if self.code_sim is not None and s28 and s16 and s28 <= 4096 and s16 <= 4096:
                cs = self.code_sim(me28.rva, me.rva)
                bonus += 0.4 * cs
            scored.append((wj + bonus, wj, shared, me, bonus))
        scored.sort(key=lambda x: (-x[0], x[3].index))
        top = scored[0]
        second = scored[1][0] if len(scored) > 1 else 0.0
        cl = [(f"{td16.full}$${me.name}", hex(me.rva), round(sc, 3), round(wj, 3), shared, me.sig()) for sc, wj, shared, me, b in scored[:6]]
        if len(scored) == 1:
            me = top[3]
            conf = "high" if (top[1] >= 0.3 and top[2] >= 2) or ratio >= 0.8 else "medium"
            return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-sig", "score": top[0], "conf": conf, "cands": cl,
                    "note": f"only compatible signature in {td16.full}; fp {top[1]:.2f}, {top[2]} shared; method lists align {ratio:.2f}", "me": me}
        margin = top[0] - second
        if top[1] >= 0.2 and top[2] >= 2 and (margin >= 0.15 or (margin >= 0.12 and top[1] >= 0.25 and top[1] >= 2 * scored[1][1])):
            me = top[3]
            return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-fp", "score": top[0], "conf": "high" if (top[2] >= 3 and margin >= 0.3) else "medium",
                    "cands": cl, "note": f"fp {top[1]:.2f} ({top[2]} shared strings/calls), runner-up {second:.2f}", "me": me}
        if j_aligned is not None and top[3].index == j_aligned and margin >= 0.1:
            me = top[3]
            return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-pos", "score": top[0], "conf": "medium" if ratio >= 0.7 else "low",
                    "cands": cl, "note": f"declaration-order alignment (lists align {ratio:.2f}); fp {top[1]:.2f}; runner-up {second:.2f}", "me": me}
        if margin >= 0.25 and top[0] >= 0.4:
            me = top[3]
            return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-fp", "score": top[0], "conf": "low",
                    "cands": cl, "note": f"weak fp {top[1]:.2f}, margin {margin:.2f}", "me": me}
        # a twin pair (two 2.8 methods with this very signature, two best-anchored 1.6 candidates): map by declaration order
        twins28 = [me for me in td28.methods if me.rva is not None and self.id_sig_token(self.a, me) == self.id_sig_token(self.a, me28)]
        top_anchor = max(self.anchors(me28, x[3]) for x in scored)
        best2 = [x for x in scored if self.anchors(me28, x[3]) == top_anchor]
        if len(twins28) == 2 and len(best2) == 2 and me28 in twins28:
            k = twins28.index(me28)
            order16 = sorted((x[3] for x in best2), key=lambda m: m.index)
            me = order16[k]
            sc = next(x for x in scored if x[3] is me)
            return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-pair-order", "score": sc[0], "conf": "low", "cands": cl,
                    "note": f"twin pair ({twins28[0].name}/{twins28[1].name} in 2.8, {order16[0].name}/{order16[1].name} in 1.6) mapped by declaration order; fp {sc[1]:.2f}", "me": me}
        # joint decision with an unmatched obfuscated parameter type: the candidates' parameter types at that position
        # form a pool; the candidate whose parameter type is also the best class match for the 2.8 parameter type wins
        # when both views agree and the combined margin is clear
        joint = self.joint_param_decision(td28, me28, td16, scored)
        if joint is not None:
            return joint
        # trivial getters (`return this.field`): the field offset the code reads, mapped through the aligned field lists
        if self.getter_offset is not None and self.a.x.size_of(me28.rva) <= 192 and not me28.params:
            off28 = self.getter_offset(self.a, me28.rva)
            if off28 is not None:
                fmap = self.field_offset_map(td28, td16)
                want = fmap.get(off28)
                if want is not None:
                    hits = [x for x in scored if self.b.x.size_of(x[3].rva) <= 192 and self.getter_offset(self.b, x[3].rva) == want]
                    if len(hits) == 1:
                        me = hits[0][3]
                        return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-getter-field", "score": hits[0][0], "conf": "medium", "cands": cl,
                                "note": f"trivial getter: reads field at 0x{off28:X} in 2.8 = 0x{want:X} in 1.6 (aligned field lists); {len(scored)} same-shape candidates", "me": me}
        # an equivalent family: the close candidates are near-identical bodies (same class, same shape, masked code alike) -
        # the timestamp/getter flavours; the top one is taken, the family is listed
        if self.code_sim_pair is not None:
            # (a) the close candidates are identical bodies; (b) the best-fingerprint candidates are identical bodies and the
            # total-score leader only leads through the position bonus - direct evidence (fingerprint) beats position
            best_wj = max(x[1] for x in scored)
            groups = [[x for x in scored if top[0] - x[0] <= 0.08]]
            if best_wj - top[1] >= 0.1:
                groups.append([x for x in scored if best_wj - x[1] <= 0.02])
            for family in groups:
                if len(family) < 2:
                    continue
                sims = [self.code_sim_pair(x[3].rva, y[3].rva) for i, x in enumerate(family) for y in family[i + 1:]]
                if sims and min(sims) >= 0.98:
                    fam = sorted(family, key=lambda x: (-x[0], x[3].index))
                    me = fam[0][3]
                    return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-family", "score": fam[0][0], "conf": "low", "cands": cl,
                            "note": f"equivalent family: {[x[3].name for x in family]} are identical bodies (masked code similarity >= {min(sims):.2f}); the best-scored one taken", "me": me}
        return {"reason": f"{len(scored)} compatible signatures in {td16.full}, no clear winner (best {top[0]:.2f} vs {second:.2f})", "cands": cl}

    def field_offset_map(self, td28, td16):
        """2.8 instance-field offset -> 1.6 offset for fields aligned by their generalized type-token sequences"""
        f28 = sorted(td28.instance_fields(), key=lambda f: (f.offset is None, f.offset or 0))
        f16 = sorted(td16.instance_fields(), key=lambda f: (f.offset is None, f.offset or 0))
        t28 = generalize(self.a.d.field_tokens(td28))
        t16 = generalize(self.b.d.field_tokens(td16))
        out = {}
        if len(t28) != len(f28) or len(t16) != len(f16):
            return out
        sm = difflib.SequenceMatcher(None, t28, t16, autojunk=False)
        for tag, i1, i2, j1, j2 in sm.get_opcodes():
            if tag == "equal":
                for k in range(i2 - i1):
                    if f28[i1 + k].offset is not None and f16[j1 + k].offset is not None:
                        out[f28[i1 + k].offset] = f16[j1 + k].offset
        return out

    def joint_param_decision(self, td28, me28, td16, scored):
        for i, p28 in enumerate(me28.params):
            t28 = self.a.type_of_cs(p28.ctype)
            if t28 is None or readable_type(t28) or t28.index in self.c28:
                continue
            pool = {}
            for sc, wj, shared, me, b in scored:
                t16 = self.b.type_of_cs(me.params[i].ctype)
                if t16 is not None and t16.index not in self.c16 and t16.kind == t28.kind:
                    pool.setdefault(t16.index, t16)
            if len(pool) < 2:
                continue
            cscore = {}
            for idx, t16 in pool.items():
                cscore[idx] = self.class_score(t28, t16)[0]
            combined = []
            for sc, wj, shared, me, b in scored:
                t16 = self.b.type_of_cs(me.params[i].ctype)
                if t16 is None or t16.index not in cscore:
                    continue
                combined.append((cscore[t16.index] + sc, cscore[t16.index], sc, me, t16))
            combined.sort(key=lambda x: (-x[0], x[3].index))
            if len(combined) < 2:
                continue
            top, second = combined[0], combined[1]
            best_cls = max(cscore.values())
            if top[1] >= 0.35 and top[1] == best_cls and top[0] - second[0] >= 0.12 and top[2] >= second[2]:
                me, t16 = top[3], top[4]
                self.match_class(t28.index, t16.index, "C-param-joint", top[1], "medium", [(self.b.m.by_index[k].full, round(v, 3)) for k, v in sorted(cscore.items(), key=lambda x: -x[1])[:4]],
                                 note=f"parameter {i} of {td28.full}.{me28.name} <-> {td16.full}.{me.name}; class score {top[1]:.2f}, method fp {top[2]:.2f}")
                return {"rva16": me.rva, "dump16": f"{td16.full}$${me.name}", "rule": "M-fp+param", "score": top[0], "conf": "medium",
                        "cands": [(f"{td16.full}$${x[3].name}", hex(x[3].rva), round(x[0], 3), round(x[1], 3), round(x[2], 3), x[3].sig()) for x in combined[:6]],
                        "note": f"joint decision: parameter type {t28.full} -> {t16.full} (class score {top[1]:.2f}) + method fp {top[2]:.2f}; runner-up {second[0]:.2f}", "me": me}
        return None

    def record_method(self, rva28, res, name=None):
        if "rva16" in res:
            prev = self.m28.get(rva28)
            if prev and prev.get("rva16") == res["rva16"]:
                return False
            self.m28[rva28] = {k: v for k, v in res.items() if k != "me"}
            self.m16[res["rva16"]] = rva28
            if self.verbose:
                print(f"  method {name or hex(rva28)} -> {res['dump16']} {res['rva16']:#x} [{res['rule']} {res.get('conf')}] {res.get('note', '')}")
            return True
        self.m28[rva28] = {k: v for k, v in res.items() if k != "me"}
        return False


# ---------------------------------------------------------------- header inventory (2.8 Inspector names -> 2.8 dump types)
class Inventory:
    """Inspector type names used by appdata-28 (types.h + relic.h + function lines) -> 2.8 dump TypeDef"""
    def __init__(self, side28, verbose=False):
        self.s = side28
        self.verbose = verbose
        self.tp = Transplant("28", None, verbose=False)
        self.h28 = HeaderTypes(os.path.join(SRC, "appdata-28", "il2cpp-types.h"))
        self.hrelic = HeaderTypes(os.path.join(SRC, "appdata-28", "il2cpp-types-relic.h"))
        self.f_lines = parse_header(read(os.path.join(SRC, "appdata-28", "il2cpp-functions.h")))
        self.p_lines = parse_header(read(os.path.join(SRC, "appdata-28", "il2cpp-types-ptr.h")))
        self.tp.learn(self.f_lines, self.p_lines)
        self.names = self.header_names()
        # wrapper X { klass; monitor; struct Y__Fields fields; } -> Y (the 2.6-era obfuscated names of the fields structs)
        self.wrapper_fields = {}
        for h in (self.h28, self.hrelic):
            for m in re.finditer(r'struct\s+([A-Za-z_]\w*)\s*\{\s*struct\s+[A-Za-z_]\w*__Class\*\s*klass;\s*MonitorData\*\s*monitor;\s*struct\s+([A-Za-z_]\w*)__Fields\s+fields;\s*\}', h.text):
                if m.group(1) != m.group(2):
                    self.wrapper_fields[m.group(1)] = m.group(2)
        self.plain = {}     # plain `struct X { ... }` (value types / Il2CppInspector value structs) -> [(ctype, name)]
        for h in (self.h28, self.hrelic):
            for m in re.finditer(r'^\s*struct\s+(?:__declspec\(align\(\d+\)\)\s+)?([A-Za-z_]\w*)\s*\{(.*?)^\s*\};', h.text, re.S | re.M):
                n, body = m.group(1), m.group(2)
                if SUFFIX_RE.match(n) or "MonitorData* monitor;" in body or "VirtualInvokeData" in body or n in self.plain:
                    continue
                flds = []
                for ln in body.splitlines():
                    ln = ln.split("//")[0].strip()
                    mm = re.match(r'^(?:const\s+)?(.+?)\s+([A-Za-z_]\w*)(\[\d+\])?;$', ln)
                    if mm:
                        flds.append((mm.group(1).strip(), mm.group(2)))
                if flds:
                    self.plain[n] = flds
        self.map = {}       # Inspector base name -> (TypeDef, how)
        self.unres = {}     # name -> reason
        self.log = []

    def say(self, *a):
        s = " ".join(str(x) for x in a)
        self.log.append(s)
        if self.verbose:
            print(s)

    def header_names(self):
        """ordered base names of every definition in appdata-28 il2cpp-types.h (namespace app part) + il2cpp-types-relic.h"""
        txt = self.h28.text
        i = txt.find("namespace app {")
        body = txt[i:] + "\n" + self.hrelic.text
        defs = re.findall(r'^\s*(struct|enum class|union)\s+(?:__declspec\(align\(\d+\)\)\s+)?([A-Za-z_]\w*)\s*(?::\s*\w+\s*)?([{;])', body, re.M)
        out = collections.OrderedDict()
        for kind, n, br in defs:
            if br == ";":
                continue
            m = SUFFIX_RE.match(n)
            base, suf = (m.group(1), m.group(2)) if m else (n, "")
            out.setdefault(base, set()).add(suf or "(self)")
        return out

    def fields_of(self, name):
        for h in (self.h28, self.hrelic):
            if name in h.fields:
                return h, h.own_fields(name), h.base_of(name)
        y = self.wrapper_fields.get(name)
        if y:
            for h in (self.h28, self.hrelic):
                if y in h.fields:
                    return h, h.own_fields(y), h.base_of(y)
        return None, [], None

    def resolve_all(self):
        s = self.s
        # 1. transplant's class map (functions / slots learned from appdata-28) - seeded once; later ensure_class() calls of
        #    the fallbacks add entries there that were rejected by the kind check and must not come back
        if not getattr(self, "_seeded", False):
            self._seeded = True
            for k, (td, how) in self.tp.classmap.items():
                self.map[k] = (td, "appdata-28 " + how)
        changed = True
        rounds = 0
        while changed and rounds < 6:
            changed = False
            rounds += 1
            for base in list(self.names):
                if base in self.map:
                    continue
                r = self.resolve_one(base)
                if r is not None:
                    self.map[base] = r
                    changed = True
            # the fields struct a resolved wrapper embeds is the same type
            for x, y in self.wrapper_fields.items():
                if x in self.map and y not in self.map:
                    self.map[y] = (self.map[x][0], f"fields struct embedded by the wrapper {x}")
                    changed = True
                elif y in self.map and x not in self.map:
                    self.map[x] = (self.map[y][0], f"wrapper of the fields struct {y}")
                    changed = True
            changed = self.propagate() or changed
        for base in self.names:
            if base not in self.map and base not in self.unres:
                self.unres[base] = "not identified in the 2.8 dump (no readable name, no usage link, no fingerprint)"
        return self.map

    def learn_from_functions(self, func_info):
        """the class part of an Il2CppInspector function name whose 2.8 method is known by RVA names that class:
        MoleMole_Config_CookRecipeExcelConfig_CheckCookFoodMaxNum -> the header struct CookRecipeExcelConfig"""
        n = 0
        for name, info in func_info.items():
            td = info.get("td28")
            if td is None:
                continue
            for i in range(len(name) - 1, 0, -1):
                if name[i] != "_":
                    continue
                prefix = name[:i]
                lp = self.loose(prefix)
                for hn in self.names:
                    if hn in self.map:
                        continue
                    if self.loose(hn) == lp:
                        self.map[hn] = (td, f"class of the function {name} (2.8 RVA)")
                        n += 1
                if lp and any(self.loose(hn) == lp for hn in self.names):
                    break
        return n

    def lookup_insp(self, name):
        """a header name that is not itself a header struct (param type etc.)"""
        if name in self.map:
            return self.map[name][0]
        r = self.resolve_one(name)
        if r is not None:
            self.map[name] = r
            return r[0]
        return None

    @staticmethod
    def loose(name):
        """Inspector spelling variants: MoleMole_Config_CookRecipeExcelConfig ~ CookRecipeExcelConfig"""
        n = name
        for pre in ("MoleMole_Config_", "MoleMole_", "Config_"):
            if n.startswith(pre):
                n = n[len(pre):]
        return n

    def resolve_one(self, base):
        s = self.s
        for cand in (base, "MoleMole_" + base, base[len("MoleMole_"):] if base.startswith("MoleMole_") else None):
            if cand and cand in self.map:
                return (self.map[cand][0], "alias of " + cand)
        lb = self.loose(base)
        for k in self.map:
            if k != base and self.loose(k) == lb:
                return (self.map[k][0], "alias of " + k)
        if GENERIC_HDR.match(base):
            return None
        # what kind of type the header declares: X__Fields -> class; plain `struct X {` -> value type; enum class -> enum
        kind_want = None
        if any(base in h.fields for h in (self.h28, self.hrelic)):
            kind_want = "class"
        elif any(base in h.enums or base in h.enums_raw for h in (self.h28, self.hrelic)):
            kind_want = "enum"
        elif any(base in h.exact for h in (self.h28, self.hrelic)):
            kind_want = "struct"
        def kind_ok(t):
            return kind_want is None or t.kind == kind_want or (kind_want == "struct" and t.kind == "class" and not t.instance_fields())
        # readable
        cands = sorted((t for t in s.d.keys.get(norm(base), ()) if kind_ok(t)), key=lambda t: t.index)
        readable = [t for t in cands if not t.obfuscated]
        mm = re.match(r'^(.*?)_(\d+)$', base)
        if not cands and mm:
            c2 = [t for t in sorted(s.d.keys.get(norm(mm.group(1)), ()), key=lambda t: t.index) if not t.obfuscated]
            k = int(mm.group(2))
            if k < len(c2) and kind_ok(c2[k]):
                return (c2[k], f"readable name, Il2CppInspector duplicate #{k}")
        if len(readable) == 1:
            return (readable[0], "readable name")
        if len(readable) > 1:
            # prefer the namespace the base name spells (MoleMole_X) / System for bare names
            ns = base.split("_")[0] if "_" in base else None
            pref = [t for t in readable if (ns and t.namespace.replace(".", "_").startswith(ns)) or (not ns and t.namespace in ("System", "UnityEngine"))]
            if len(pref) == 1:
                return (pref[0], "readable name (namespace preference)")
            # the first definition (lowest TypeDefIndex) is Il2CppInspector's un-suffixed one
            if not mm:
                return (readable[0], f"readable name, first of {len(readable)} duplicates ({[t.full for t in readable[:4]]})")
            return None
        obf = [t for t in cands if t.obfuscated]
        if len(obf) == 1:
            return (obf[0], "same obfuscated name in the 2.8 dump")
        if len(obf) > 1:
            self.unres[base] = f"{len(obf)} dump types share the obfuscated name"
            return None
        # enum by readable members
        for h in (self.h28, self.hrelic):
            mem = h.enums.get(base) or h.enums_raw.get(base)
            if mem:
                want = frozenset(k for k in mem if not is_obf(k))
                if len(want) >= 2:
                    best = []
                    for t in s.m.types:
                        if t.kind != "enum":
                            continue
                        have = s.enum_members.get(t.index, frozenset())
                        if not have:
                            continue
                        j = len(want & have) / max(len(want | have), 1)
                        if j >= 0.6:
                            best.append((j, t))
                    best.sort(key=lambda x: (-x[0], x[1].index))
                    if len(best) == 1 or (len(best) > 1 and best[0][0] - best[1][0] >= 0.15):
                        return (best[0][1], f"enum members Jaccard {best[0][0]:.2f}")
                    if best:
                        self.unres[base] = f"enum members ambiguous: {[(t.full, round(j, 2)) for j, t in best[:4]]}"
                break
        # plain value struct of the header: field-token fingerprint, inside the nested types of the mapped outer (name prefix)
        # when there is one, else among all value types with the same field count
        if base in self.plain:
            htoks = []
            for ctype, fname in self.plain[base]:
                k = header_ctype_kind(ctype)
                htoks.append(k[1] if k[0] == "prim" else ("class" if k[0] == "voidptr" else k[0]))
            pool, poolname = None, ""
            for k in sorted(self.map, key=len, reverse=True):
                kk = k[len("MoleMole_"):] if k.startswith("MoleMole_") else k
                if base.startswith(k + "_") or base.startswith(kk + "_") or base.startswith("MoleMole_" + kk + "_"):
                    outer = self.map[k][0]
                    pool = [t for t in s.m.types if t.nested_in == outer.name and t.namespace == outer.namespace and t.kind == "struct"]
                    poolname = f"nested in {outer.full}"
                    break
            if pool is None:
                pool = [t for t in s.m.types if t.kind == "struct" and abs(len(t.instance_fields()) - len(htoks)) <= 1]
                poolname = "value types"
            scored = sorted(((similarity(htoks, generalize(s.d.field_tokens(t))), t) for t in pool if t.instance_fields()), key=lambda x: (-x[0], x[1].index))
            if scored and scored[0][0] >= 0.9 and (len(scored) == 1 or scored[0][0] - scored[1][0] >= 0.1):
                return (scored[0][1], f"plain struct fingerprint {scored[0][0]:.2f} ({poolname})")
            if scored and scored[0][0] >= 0.9:
                self.unres[base] = f"plain struct fingerprint ambiguous ({poolname}): {[(t.full, round(sc, 2)) for sc, t in scored[:4]]}"
                return None
        # last resort: transplant.py's field-shape fingerprint of the appdata-28 struct against the 2.8 dump (what gen_types
        # used to identify ConfigScenePoint & co. for the 2.8 header)
        if base in self.h28.fields or base in self.hrelic.fields or base in self.h28.structs or base in self.hrelic.structs:
            try:
                td = self.tp.ensure_class(base)
            except Exception:
                td = None
            if td is not None and kind_ok(td):
                how = self.tp.classmap.get(base, (None, "fingerprint"))[1]
                return (td, "transplant " + how)
            if td is not None:
                self.unres[base] = f"transplant identified {td.full} ({td.kind}) but the header declares a {kind_want}"
        return None

    def propagate(self):
        """field / base links from identified header structs to their 2.8 dump types"""
        changed = False
        votes = collections.defaultdict(collections.Counter)
        why = {}
        for name, (td, how) in list(self.map.items()):
            h, own, hbase = self.fields_of(name)
            if h is None:
                continue
            # base
            if hbase and hbase not in self.map and td.base:
                btd = self.s.type_of_cs(td.base)
                if btd is not None:
                    votes[hbase][btd.index] += 2
                    why.setdefault((hbase, btd.index), f"base of {name} ({td.full})")
            # fields
            htoks = []
            for ctype, fname in own:
                k = header_ctype_kind(ctype)
                htoks.append(k[1] if k[0] == "prim" else ("class" if k[0] == "voidptr" else k[0]))
            dfields = sorted(td.instance_fields(), key=lambda f: (f.offset is None, f.offset or 0))
            dtoks = generalize(self.s.d.field_tokens(td))
            if not htoks or len(dtoks) != len(dfields):
                continue
            sm = difflib.SequenceMatcher(None, htoks, dtoks, autojunk=False)
            for tag, i1, i2, j1, j2 in sm.get_opcodes():
                if tag != "equal":
                    continue
                for k in range(i2 - i1):
                    ctype, fname = own[i1 + k]
                    hk = header_ctype_kind(ctype)
                    if hk[0] in ("class", "enum", "vstruct") and hk[1] and hk[1] not in self.map and not GENERIC_HDR.match(hk[1]):
                        dtd = self.s.type_of_cs(dfields[j1 + k].ctype)
                        if dtd is not None and ((dtd.kind == "enum") == (hk[0] == "enum")):
                            votes[hk[1]][dtd.index] += 1
                            why.setdefault((hk[1], dtd.index), f"field {name}.{fname} ({td.full}.{dfields[j1 + k].name})")
                    if hk[0] == "rep" and hk[1].startswith("class:"):
                        inner = hk[1][len("class:"):]
                        ga = generic_args(dfields[j1 + k].ctype)
                        if inner not in self.map and len(ga) == 1:
                            dtd = self.s.type_of_cs(ga[0])
                            if dtd is not None:
                                votes[inner][dtd.index] += 1
                                why.setdefault((inner, dtd.index), f"repeated field element {name}.{fname}")
        for hname, c in votes.items():
            if hname in self.map or len(c) != 1:
                if len(c) > 1:
                    self.say(f"  inventory votes split for {hname}: {[(self.s.m.by_index[i].full, v) for i, v in c.most_common(3)]}")
                continue
            i, nv = next(iter(c.items()))
            td = self.s.m.by_index[i]
            # shape check against the header struct when it has fields
            h, own, hbase = self.fields_of(hname)
            ok = True
            sim = None
            if own:
                htoks = self.tp.header_field_tokens(h, hname if hname in h.fields else self.wrapper_fields.get(hname, hname))
                sim = similarity(generalize(htoks), generalize(self.s.d.field_tokens(td)))
                # the appdata-28 header carries 2.6-era layouts for these structs: a usage link (field / base / wrapper)
                # is strong evidence by itself, the shape only has to be plausible
                ok = sim >= 0.35
            if ok:
                self.map[hname] = (td, why[(hname, i)] + f" (votes {nv}" + (f", shape {sim:.2f}" if sim is not None else "") + ")")
                changed = True
            else:
                self.unres[hname] = f"usage link to {td.full} rejected by field shape"
        return changed


# ---------------------------------------------------------------- driver
class Runner:
    def __init__(self, args):
        self.args = args
        t0 = time.time()
        dump28 = args.dump28 or DEFAULT_DUMP_DIR["28"]
        dump16 = args.dump16 or DEFAULT_DUMP_DIR["16"]
        self.m28 = load("28", dump28, quiet=not args.verbose)
        self.m16 = load("16", dump16, quiet=not args.verbose)
        self.x28 = CodeXref.load("28", args.dll28, os.path.join(dump28, "script.json"), quiet=not args.verbose)
        self.x16 = CodeXref.load("16", args.dll16, os.path.join(dump16, "script.json"), quiet=not args.verbose)
        self.s28 = Side("28", self.m28, self.x28)
        self.s16 = Side("16", self.m16, self.x16)
        self.M = Matcher(self.s28, self.s16, args.verbose)
        self.generic_method_map = {}   # (owner28 index, 2.8 generic method name) -> 1.6 generic method name
        self._code_sim_cache = {}
        self.pe28 = self.pe16 = None
        d28 = args.dll28 or self.x28.dll_path or DEFAULT_DLL["28"]
        d16 = args.dll16 or self.x16.dll_path or DEFAULT_DLL["16"]
        if d28 and d16 and os.path.exists(d28) and os.path.exists(d16):
            self.pe28, self.pe16 = PE(d28), PE(d16)
            self.M.code_sim = self.code_sim
            self.M.code_sim_pair = self.code_sim_pair
            self.M.getter_offset = self.getter_offset
        else:
            print("   note: UserAssembly.dll files not readable - machine-code similarity disabled (xref caches still used)")
        print(f"== matcher16: 2.8 {len(self.m28.types)} types / 1.6 {len(self.m16.types)} types; proto family 2.8 {len(self.s28.proto)} / 1.6 {len(self.s16.proto)}; "
              f"singletons {len(self.s28.singletons)} / {len(self.s16.singletons)} ({time.time() - t0:.1f}s)")
        self.acceptance()
        self.inv = Inventory(self.s28, args.verbose)

    def code_sim(self, rva28, rva16):
        key = (rva28, rva16)
        c = self._code_sim_cache.get(key)
        if c is not None:
            return c
        n28 = min(self.x28.size_of(rva28) or 0, 4096)
        n16 = min(self.x16.size_of(rva16) or 0, 4096)
        if not n28 or not n16:
            return 0.0
        a = self.mask_bytes(self.pe28.read(rva28, n28))
        b = self.mask_bytes(self.pe16.read(rva16, n16))
        r = difflib.SequenceMatcher(None, a, b, autojunk=False).ratio() if a and b else 0.0
        self._code_sim_cache[key] = r
        return r

    def code_sim_pair(self, rva_a, rva_b):
        na = min(self.x16.size_of(rva_a) or 0, 4096)
        nb = min(self.x16.size_of(rva_b) or 0, 4096)
        if not na or not nb:
            return 0.0
        a = self.mask_bytes(self.pe16.read(rva_a, na))
        b = self.mask_bytes(self.pe16.read(rva_b, nb))
        return difflib.SequenceMatcher(None, a, b, autojunk=False).ratio()

    READ_RE = re.compile(rb'(?:[\x40-\x4f])?(?:\x8b|\x0f\xb6|\x0f\xb7|\x3b|\x39|\x89|\xf3\x0f\x10|\xf2\x0f\x10)(?:([\x40-\x7f])(.)|([\x80-\xbf])(....))', re.S)

    def field_reads(self, pe, x, rva):
        """displacements of [reg+disp] memory operands in a method (mov/cmp/movss forms, no SIB) - candidate field offsets"""
        n = min(x.size_of(rva) or 0, 4096)
        if not n or pe is None:
            return {}
        b = pe.read(rva, n)
        c = collections.Counter()
        for m in self.READ_RE.finditer(b):
            if m.group(1) is not None:
                if (m.group(1)[0] & 7) == 4:
                    continue
                c[m.group(2)[0]] += 1
            else:
                if (m.group(3)[0] & 7) == 4:
                    continue
                d = struct.unpack("<i", m.group(4))[0]
                if 0 <= d < 0x1000:
                    c[d] += 1
        return c

    GETTER_RE = re.compile(rb'(?:\xf3\x0f\x10|\xf2\x0f\x10|\x48\x8b|\x8b|\x0f\xb6|\x0f\xb7|\x0f\xbe|\x0f\xbf|\x48\x63)(?:([\x41\x49\x51\x59\x61\x69\x71\x79])(.)|([\x81\x89\x91\x99\xa1\xa9\xb1\xb9])(....))', re.S)

    def getter_offset(self, side, rva):
        """the [rcx+disp] (this-relative) field a trivial getter reads: the first this-relative load in its code"""
        pe = self.pe28 if side is self.s28 else self.pe16
        n = side.x.size_of(rva)
        if not pe or not n or n > 192:
            return None
        b = pe.read(rva, n)
        m = self.GETTER_RE.search(b)
        if not m:
            return None
        if m.group(1) is not None:
            return m.group(2)[0]
        return struct.unpack("<i", m.group(4))[0]

    @staticmethod
    def mask_bytes(b):
        """machine code with rel32 / RIP-relative displacements zeroed (same logic as mask_pattern)"""
        out = bytearray(b)
        i, n = 0, len(b)
        while i < n:
            c = b[i]
            if c in (0xE8, 0xE9) and i + 4 < n:
                out[i + 1:i + 5] = b"\x00\x00\x00\x00"; i += 5; continue
            if c in (0x8B, 0x8D, 0x89, 0x3B, 0x39, 0x88, 0x8A, 0xFF, 0x63, 0x85, 0x84, 0x2B, 0x03, 0x33, 0x0B, 0x23) and i + 5 < n and (b[i + 1] & 0xC7) == 0x05:
                out[i + 2:i + 6] = b"\x00\x00\x00\x00"; i += 6; continue
            if c in (0x80, 0x83, 0xC6) and i + 6 < n and (b[i + 1] & 0xC7) == 0x05:
                out[i + 2:i + 6] = b"\x00\x00\x00\x00"; i += 7; continue
            if c == 0x0F and i + 6 < n and (b[i + 2] & 0xC7) == 0x05:
                out[i + 3:i + 7] = b"\x00\x00\x00\x00"; i += 7; continue
            i += 1
        return bytes(out)

    def acceptance(self):
        hits = self.m16.methods_by_rva.get(0x1CC5520, [])
        names = [t.full + "$$" + me.name for t, me in hits]
        print(f"   acceptance: 1.6 RVA 0x1CC5520 -> {names}")
        if "MoleMole.GameManager$$Update" not in names:
            raise SystemExit("acceptance failed: the 1.6 RVA convention is not what the report says; refusing to continue")
        hits = self.m28.methods_by_rva.get(0x164D930, [])
        if not any(t.full == "MoleMole.GameManager" and me.name == "Update" for t, me in hits):
            raise SystemExit("acceptance failed for the 2.8 dump (GameManager_Update 0x164D930)")
        print("   acceptance: script.json Address == RVA confirmed on both dumps")

    # ---- which 2.8 types / methods must be matched
    def collect_targets(self):
        inv = self.inv
        inv.resolve_all()
        self.targets = []       # (HeaderLine, kind info)
        required = set()
        self.func_info = {}     # Inspector name -> dict(rva28, td28, me28, generic, names...)
        f_lines, p_lines = inv.f_lines, inv.p_lines
        mi_by_rva = {v: k for k, v in self.m28.methodinfo.items()}
        ti_by_rva = {v: k for k, v in self.m28.typeinfo.items()}
        for hl in f_lines:
            info = {"kind": hl.kind, "rva28": hl.offset, "name": hl.name, "line": hl}
            if hl.offset == 0:
                info["status"] = "unresolved-28"
                self.func_info[hl.name] = info
                continue
            if hl.kind == "mi":
                info["slot28"] = mi_by_rva.get(hl.offset)
                self.func_info[hl.name] = info
                if info["slot28"]:
                    for ga in generic_args(info["slot28"].split("(")[0]):
                        for t in self.s28.find_type(ga):
                            required.add(t.index)
                    cls = info["slot28"][len("Method$"):]
                    cls = cls.split("(")[0]
                    # owner class
                    depth, cut = 0, -1
                    for i, ch in enumerate(cls):
                        if ch == "<":
                            depth += 1
                        elif ch == ">":
                            depth -= 1
                        elif ch == "." and depth == 0:
                            cut = i
                    owner = strip_generic(cls[:cut]) if cut > 0 else None
                    if owner:
                        for t in self.s28.find_type(owner):
                            required.add(t.index)
                continue
            hits = self.m28.methods_by_rva.get(hl.offset, [])
            if len(hits) > 1:
                # folded bodies (identical code shared by several methods): prefer the hit the Inspector name speaks about
                nm = hl.name.lower()
                good = [(t, me) for t, me in hits if nm.endswith("_" + me.name.lower()) or re.sub(r'_\d+$', '', nm).endswith("_" + me.name.lower())]
                if good:
                    good2 = [(t, me) for t, me in good if any(nm.startswith(norm(k)) for k in Dump.insp_keys(t))]
                    hits = good2 or good
            if len(hits) >= 1:
                td, me = hits[0]
                info.update({"td28": td, "me28": me, "dump28": f"{td.full}$${me.name}", "folded": [f"{t.full}$${m.name}" for t, m in hits[1:]]})
                required.add(td.index)
                for p in me.params:
                    t = self.s28.type_of_cs(p.ctype)
                    if t is not None:
                        required.add(t.index)
                t = self.s28.type_of_cs(me.ret)
                if t is not None:
                    required.add(t.index)
            else:
                names = self.m28.script_by_rva.get(hl.offset, [])
                info["generic_names"] = names
                if names:
                    owners = {strip_generic(n.split("$$")[0]) for n in names}
                    info["generic_owner"] = sorted(owners)
                    for o in owners:
                        for t in self.s28.find_type(o):
                            required.add(t.index)
                    # type args mentioned by the Inspector name's declaration (hint) are not needed for the shared body
                else:
                    info["status"] = "rva not in the 2.8 dump"
            self.func_info[hl.name] = info
        for hl in p_lines:
            info = {"kind": "td", "rva28": hl.offset, "name": hl.name, "line": hl}
            if hl.offset:
                tn = ti_by_rva.get(hl.offset)
                info["typeinfo28"] = tn
                if tn and tn in CS_KEYWORD_TYPES:
                    info["keyword"] = tn
                elif tn:
                    for t in self.s28.find_type(tn):
                        required.add(t.index)
                        info["td28"] = t
            self.func_info[hl.name] = info
        # functions whose class is known by RVA name header structs (MoleMole_Config_X_Method -> X)
        n_fn = inv.learn_from_functions(self.func_info)
        if n_fn:
            inv.resolve_all()
        # header struct types
        for name, (td, how) in inv.map.items():
            required.add(td.index)
        # bases (transitive) and by-value field types of the required types
        frontier = list(required)
        seen = set(required)
        while frontier:
            nxt = []
            for i in frontier:
                td = self.m28.by_index[i]
                if td.base:
                    b = self.s28.type_of_cs(td.base)
                    if b is not None and b.index not in seen and not readable_type(b):
                        seen.add(b.index); nxt.append(b.index)
                for f in td.instance_fields():
                    k = self.s28.d.type_kind(f.ctype)
                    if k[0] in ("enum", "vstruct") and k[1] is not None and k[1].index not in seen and not readable_type(k[1]):
                        seen.add(k[1].index); nxt.append(k[1].index)
            frontier = nxt
        self.required = seen
        print(f"   inventory: {len(inv.names)} header type names -> {len(inv.map)} identified in the 2.8 dump, {len(inv.unres)} not; "
              f"{sum(1 for i in self.func_info.values() if i.get('td28') is not None or i.get('generic_names'))} function/slot lines with a 2.8 dump entry; "
              f"{len(self.required)} 2.8 types to match (incl. bases / by-value field types)")

    # ---- main loop
    def run(self):
        self.collect_targets()
        M = self.M
        req = self.required
        t0 = time.time()
        M.round = 0
        n = M.match_readable(req)
        print(f"   round 0: {n} classes by readable name")
        for rnd in range(1, 9):
            M.round = rnd
            nn = 0
            nn += M.match_enums(req)
            # fingerprint the required obfuscated types, strongest pools first
            todo = [i for i in sorted(req) if i not in M.c28 and not readable_type(self.m28.by_index[i])]
            order = sorted(todo, key=lambda i: (0 if i in self.s28.singletons else (1 if self.m28.by_index[i].nested_in is None and i not in self.s28.proto else 2), i))
            for i in order:
                td = self.m28.by_index[i]
                if td.kind == "enum":
                    continue
                is_proto = i in self.s28.proto
                # the usage pool (types referenced the same way by the counterparts of matched classes) is the most
                # specific evidence and is tried first; the structural pools (singletons / proto family / global) next
                upool = M.usage_pool(td)
                td16u, detu = (None, {})
                if upool:
                    td16u, detu = M.fp_match(td, pool=upool, poolname=f"types referenced by the counterparts of the matched classes that reference it ({len(upool)})")
                td16g, detg = M.fp_match(td, min_score=0.42 if is_proto else 0.30, allow_shape=not is_proto)
                if td16g is not None and is_proto and detg["det"]["fields"] < 0.6:
                    td16g, detg = None, {"reason": f"proto pool winner {td16g.full} rejected: field shape {detg['det']['fields']:.2f} < 0.6", "cands": detg.get("cands", [])}
                td16, det, rule = None, {}, None
                if td16u is not None:
                    td16, det, rule = td16u, detu, "C-usage"
                    if td16g is not None and td16g.index != td16u.index:
                        det = dict(det, det=dict(det["det"], conflict=f"structural pool preferred {td16g.full} ({detg['score']:.2f})"))
                        det["conf"] = "medium" if det["conf"] == "high" else "low"
                elif td16g is not None:
                    td16, det, rule = td16g, detg, "C-fp"
                else:
                    det = {"reason": (detg.get("reason", "") + ((" | usage pool: " + detu.get("reason", "")) if upool else "")), "cands": detu.get("cands", []) + detg.get("cands", [])}
                if td16 is not None:
                    nn += M.match_class(i, td16.index, rule, det["score"], det["conf"], det["cands"], f"pool: {det['pool']}; {det['det']}")
                else:
                    M.fails[i] = det
            nn += M.propagate()
            nm = self.match_functions()
            print(f"   round {rnd}: +{nn} classes, +{nm} methods ({len(M.c28)} classes, {sum(1 for v in M.m28.values() if v.get('rva16'))} methods matched; {time.time() - t0:.0f}s)")
            if nn == 0 and nm == 0:
                break
        self.resolve_slots()
        self.resolve_api()
        self.resolve_unity()
        self.field_anchors()
        self.vtable_names()
        self.write_outputs()

    def match_functions(self):
        M = self.M
        n = 0
        for name, info in self.func_info.items():
            if info["kind"] != "func" or info.get("status"):
                continue
            rva28 = info["rva28"]
            if rva28 in M.m28 and M.m28[rva28].get("rva16") is not None:
                continue
            if info.get("td28") is not None:
                td28, me28 = info["td28"], info["me28"]
                td16 = M.mapped16(td28)
                if td16 is None:
                    M.m28[rva28] = {"reason": f"class {td28.full} not matched in 1.6", "cands": []}
                    continue
                res = M.match_method(td28, me28, td16)
                if "rva16" in res and res["rva16"] in M.m16 and M.m16[res["rva16"]] != rva28:
                    other = M.m16[res["rva16"]]
                    # the same 1.6 body for two 2.8 methods: allowed only when the 2.8 RVAs are folded too
                    if self.m28.methods_by_rva.get(other) and self.m28.methods_by_rva.get(rva28) and other != rva28:
                        res = {"reason": f"1.6 candidate {res['dump16']} already matched to 2.8 {other:#x}", "cands": res.get("cands", [])}
                if M.record_method(rva28, res, name):
                    n += 1
            elif info.get("generic_names"):
                res = self.match_generic(info)
                if M.record_method(rva28, res, name):
                    n += 1
        return n

    def match_generic(self, info):
        """a shared generic body: owner class + method + type args -> the 1.6 instantiation(s)"""
        M = self.M
        names = info["generic_names"]
        owners = info["generic_owner"]
        if len(owners) != 1:
            return {"reason": f"shared body of {len(names)} methods across {len(owners)} classes ({owners[:3]})", "cands": []}
        owner28 = self.s28.find_type(owners[0])
        owner28 = owner28[0] if len(owner28) == 1 else None
        if owner28 is None:
            return {"reason": f"generic owner {owners[0]} not found as a type", "cands": []}
        o16 = M.mapped16(owner28)
        if o16 is None and readable_type(owner28):
            hits = [t for t in self.s16.find_type(owner28.full) if t.full == owner28.full]
            o16 = hits[0] if len(hits) == 1 else None
        if o16 is None:
            return {"reason": f"generic owner {owner28.full} not matched in 1.6", "cands": []}
        meths = sorted({strip_generic(n.split("$$", 1)[1]) for n in names})
        # the parameter count of the 2.8 body (generic definition in dump.cs, else the script.json signature)
        np28 = None
        gdef28 = [me for me in owner28.methods if strip_generic(me.name) in meths]
        if gdef28:
            np28 = len(gdef28[0].params)
        if info["rva28"] in self.x28.gen_nparams:
            np28 = self.x28.gen_nparams[info["rva28"]]
        results = {}
        for mname in meths:
            if not is_obf(mname):
                cands16 = [me for me in o16.methods if strip_generic(me.name) == mname]
                name16 = mname
            else:
                g = self.match_generic_method(owner28, mname)
                if not g or "name16" not in g:
                    results[mname] = {"reason": g.get("reason", "generic method not matched") if g else "generic method not matched", "cands": g.get("cands", []) if g else []}
                    continue
                name16 = g["name16"]
                cands16 = [me for me in o16.methods if strip_generic(me.name) == name16]
            if not cands16:
                results[mname] = {"reason": f"no method {name16}<> in {o16.full}", "cands": []}
                continue
            # collect the 1.6 instantiation bodies of that method name, filtered by parameter count
            rvas = collections.Counter()
            inst_names = {}
            want_ng = f"{strip_generic(o16.full)}$${name16}"
            owner_head = strip_generic(o16.full).split("<")[0]
            for n, rl in self.m16.script_methods.items():
                if "<" in n and n.startswith(owner_head) and strip_generic(n) == want_ng:
                    for r in rl:
                        if np28 is not None and r in self.x16.gen_nparams and self.x16.gen_nparams[r] != np28:
                            continue
                        rvas[r] += 1
                        inst_names.setdefault(r, n)
            if not rvas:
                results[mname] = {"reason": f"no instantiation of {o16.full}.{name16}<> with {np28} parameter(s) has code in 1.6", "cands": []}
                continue
            if len(rvas) == 1:
                r, cnt = next(iter(rvas.items()))
                results[mname] = {"rva16": r, "dump16": inst_names[r] + (f" (+{cnt - 1} instantiations sharing the body)" if cnt > 1 else ""), "rule": "M-generic", "score": 1.0,
                                  "conf": "high", "cands": [], "note": f"shared generic body of {o16.full}.{name16}<T>"}
                continue
            want_args = set()
            for n in names:
                for ga in generic_args(n.split("$$", 1)[1]):
                    t = self.s28.type_of_cs(ga)
                    t16 = M.mapped16(t) if t is not None else None
                    if t16 is not None:
                        want_args.add(t16.full); want_args.add(t16.name)
                    elif t is not None and readable_type(t):
                        want_args.add(t.full); want_args.add(t.name)
            picked = [(r, n) for r, n in inst_names.items() if any(a in generic_args(n.split("$$", 1)[1]) for a in want_args)]
            if len({r for r, n in picked}) == 1:
                r, n = picked[0]
                results[mname] = {"rva16": r, "dump16": n, "rule": "M-generic", "score": 0.9, "conf": "medium", "cands": [(inst_names[x], hex(x), c) for x, c in rvas.most_common(6)],
                                  "note": f"{len(rvas)} bodies; the instantiation with the mapped type argument"}
            else:
                results[mname] = {"reason": f"{len(rvas)} different bodies for {o16.full}.{name16}<..> and the type argument could not be mapped",
                                  "cands": [(inst_names[x], hex(x), c) for x, c in rvas.most_common(8)]}
        ok = {k: v for k, v in results.items() if "rva16" in v}
        if not ok:
            r0 = next(iter(results.values()))
            return {"reason": "; ".join(f"{k}: {v.get('reason')}" for k, v in results.items()), "cands": r0.get("cands", [])}
        if len({v["rva16"] for v in ok.values()}) == 1 or len(ok) == 1:
            v = next(iter(ok.values()))
            if len(meths) > 1:
                v = dict(v, note=v.get("note", "") + f"; 2.8 body shared by {meths}")
            return v
        # several methods share the 2.8 body but have different bodies in 1.6: prefer the one named like the Inspector entry
        tail = norm(re.sub(r'_\d+$', '', info["name"]).rsplit("_", 1)[-1])
        pref = [v for k, v in ok.items() if norm(k) == tail]
        if len(pref) == 1:
            return dict(pref[0], conf="medium", note=pref[0].get("note", "") + f"; 2.8 body shared by {meths}, picked {tail} by name")
        return {"reason": f"2.8 body shared by {meths} which have different bodies in 1.6: {[(k, hex(v['rva16'])) for k, v in ok.items()]}", "cands": []}

    def match_generic_method(self, owner28, meth28):
        """an obfuscated generic method of a matched owner -> its 1.6 name, by the set of instantiation type arguments
        (MethodInfo slot names + script.json instantiation names carry the real arguments) mapped through the class map,
        with the declaration order of the owner's generic methods as tie-breaker"""
        M = self.M
        key = (owner28.index, meth28)
        if key in self.generic_method_map:
            return {"name16": self.generic_method_map[key]}
        o16 = M.mapped16(owner28)
        if o16 is None:
            return {"reason": "owner not matched"}
        def inst_args(side, owner, mname):
            out = set()
            pref1 = f"Method${owner.full}.{mname}<"
            for k in side.m.methodinfo:
                if k.startswith(pref1):
                    for ga in generic_args(k[len(pref1) - 1:].split("(")[0]):
                        out.add(ga)
            pref2 = f"{owner.full}$${mname}<"
            for k in side.m.script_methods:
                if k.startswith(pref2):
                    for ga in generic_args(k.split("$$", 1)[1]):
                        out.add(ga)
            return out
        args28 = set()
        for ga in inst_args(self.s28, owner28, meth28):
            t = self.s28.type_of_cs(ga)
            if t is None:
                continue
            t16 = M.mapped16(t)
            if t16 is not None:
                args28.add(t16.index)
            elif readable_type(t):
                h = [x for x in self.s16.find_type(t.full) if x.full == t.full]
                if len(h) == 1:
                    args28.add(h[0].index)
        g28 = [me for me in owner28.methods if strip_generic(me.name) == meth28]
        g28_list = [me for me in owner28.methods if "<" in me.name]
        g16_list = [me for me in o16.methods if "<" in me.name]
        def gshape(side, m):
            targs = set(generic_args(m.name))
            return (m.static, "T" if m.ret in targs else side.d.type_kind(m.ret)[0], tuple("T" if p.ctype in targs else side.d.type_kind(p.ctype)[0] for p in m.params))
        cands = [me for me in g16_list if g28 and gshape(self.s16, me) == gshape(self.s28, g28[0])]
        if not cands:
            return {"reason": f"no generic method of {o16.full} has the shape of {owner28.full}.{meth28}<T>"}
        scored = []
        for me in cands:
            n16 = strip_generic(me.name)
            a16 = set()
            for ga in inst_args(self.s16, o16, n16):
                t = self.s16.type_of_cs(ga)
                if t is not None:
                    a16.add(t.index)
            j = len(args28 & a16) / max(len(args28 | a16), 1) if (args28 or a16) else 0.0
            # declaration-order tie-breaker among the owner's generic methods
            pos28 = next((i for i, m in enumerate(g28_list) if m is g28[0]), -1)
            pos16 = next((i for i, m in enumerate(g16_list) if m is me), -1)
            posb = 0.3 if (pos28 >= 0 and pos28 == pos16) else 0.0
            scored.append((j + posb, j, len(args28 & a16), me, n16))
        scored.sort(key=lambda x: (-x[0], x[3].index))
        top = scored[0]
        second = scored[1][0] if len(scored) > 1 else 0.0
        cl = [(f"{o16.full}.{n16}<T>", round(sc, 3), round(j, 3), k) for sc, j, k, me, n16 in scored[:6]]
        if len(scored) == 1 or (top[2] >= 1 and top[0] - second >= 0.2) or (top[2] >= 2 and top[0] - second >= 0.1):
            self.generic_method_map[key] = top[4]
            return {"name16": top[4], "note": f"instantiation arguments: {top[2]} shared (Jaccard {top[1]:.2f}); runner-up {second:.2f}", "cands": cl}
        return {"reason": f"generic method ambiguous among {len(scored)} candidates (best {top[0]:.2f} vs {second:.2f})", "cands": cl}

    # ---- field anchors: trivial getters (`return this.field`) matched across the versions pin field pairs
    def field_anchors(self):
        """for every matched class that appdata-28 declares a struct for: match its trivial getters (tiny methods reading
        [this+disp]) by readable name / callers / fingerprint; each matched getter pair anchors (offset28, offset16) so the
        generator can align the fields it reads without guessing among same-typed neighbours"""
        M = self.M
        self.anchors = {}
        if self.pe28 is None or self.pe16 is None:
            print("   field anchors: skipped (UserAssembly.dll files not readable)")
            return
        n_pairs = n_anchors = 0
        wanted = {td.index for td, how in self.inv.map.values()}
        for i28 in sorted(wanted):
            r = M.c28.get(i28)
            if not r:
                continue
            td28, td16 = self.m28.by_index[i28], self.m16.by_index[r["i16"]]
            g28 = [(me, self.getter_offset(self.s28, me.rva)) for me in td28.methods if me.rva and not me.static and not me.params and self.x28.size_of(me.rva) <= 32]
            g28 = [(me, off) for me, off in g28 if off is not None and off >= 0x10]
            g16 = [(me, self.getter_offset(self.s16, me.rva)) for me in td16.methods if me.rva and not me.static and not me.params and self.x16.size_of(me.rva) <= 32]
            g16 = [(me, off) for me, off in g16 if off is not None and off >= 0x10]
            if not g28 or not g16:
                continue
            f28 = {f.offset: f for f in td28.instance_fields() if f.offset is not None}
            f16 = {f.offset: f for f in td16.instance_fields() if f.offset is not None}
            pairs = []
            used16 = set()
            for me28, off28 in g28:
                if off28 not in f28:
                    continue
                cands = [(me, off) for me, off in g16 if off in f16 and off not in used16 and M.sig_compat(me28, me, lenient=True)]
                if not cands:
                    continue
                best = None
                if not is_obf(me28.name):
                    named = [(me, off) for me, off in cands if me.name == me28.name]
                    if len(named) == 1:
                        best = (named[0], "getter name")
                if best is None:
                    fp28 = M.method_fp(self.s28, me28)
                    if not fp28:
                        continue
                    sc = sorted(((wjaccard(fp28, M.method_fp(self.s16, me)), me, off) for me, off in cands), key=lambda x: (-x[0], x[1].index))
                    if sc[0][0] >= 0.3 and (len(sc) == 1 or sc[0][0] - sc[1][0] >= 0.15):
                        best = ((sc[0][1], sc[0][2]), f"getter callers/fp {sc[0][0]:.2f}")
                if best is None:
                    continue
                (me16, off16), how = best
                # the field kinds must agree
                k28 = self.s28.d.type_kind(f28[off28].ctype)
                k16 = self.s16.d.type_kind(f16[off16].ctype)
                if k28[0] != k16[0] and not ({k28[0], k16[0]} <= {"class", "list", "dict", "generic", "array", "rep", "delegate"}):
                    continue
                if k28[0] == "prim" and k28[2] != k16[2]:
                    continue
                used16.add(off16)
                pairs.append([off28, off16, f28[off28].name, f16[off16].name, how])
            if pairs:
                self.anchors[td28.full] = {"d16": td16.full, "i28": td28.index, "i16": td16.index, "anchors": pairs}
                n_pairs += 1
                n_anchors += len(pairs)
        # second source: the field reads of matched method pairs of the same class pair (a method that reads exactly one
        # own field on both sides anchors that field; several reads anchor when the kinds pair up uniquely)
        n_read = 0
        for rva28, r in M.m28.items():
            if r.get("rva16") is None:
                continue
            o28, o16 = self.s28.method_owner.get(rva28), self.s16.method_owner.get(r["rva16"])
            if not o28 or not o16:
                continue
            td28, me28 = o28
            td16, me16 = o16
            cr = M.c28.get(td28.index)
            if not cr or cr["i16"] != td16.index or me28.static:
                continue
            f28 = {f.offset: f for f in td28.instance_fields() if f.offset is not None}
            f16 = {f.offset: f for f in td16.instance_fields() if f.offset is not None}
            if not f28 or not f16:
                continue
            # displacements that are Il2CppClass internals (static_fields / cctor / flags) look like field reads: skip them
            r28 = {d: c for d, c in self.field_reads(self.pe28, self.x28, rva28).items() if d in f28 and d not in RUNTIME_OFFS_28}
            r16 = {d: c for d, c in self.field_reads(self.pe16, self.x16, r["rva16"]).items() if d in f16 and d not in RUNTIME_OFFS_16}
            if not r28 or not r16:
                continue
            def kind_of(side, f):
                k = side.d.type_kind(f.ctype)
                return ("p:" + k[2]) if k[0] == "prim" else k[0]
            kinds28 = collections.defaultdict(list)
            for d in r28:
                kinds28[kind_of(self.s28, f28[d])].append(d)
            kinds16 = collections.defaultdict(list)
            for d in r16:
                kinds16[kind_of(self.s16, f16[d])].append(d)
            entry = self.anchors.setdefault(td28.full, {"d16": td16.full, "i28": td28.index, "i16": td16.index, "anchors": []})
            have28 = {a[0] for a in entry["anchors"]}
            have16 = {a[1] for a in entry["anchors"]}
            for k, ds in kinds28.items():
                es = kinds16.get(k, [])
                if len(ds) == 1 and len(es) == 1 and ds[0] not in have28 and es[0] not in have16:
                    entry["anchors"].append([ds[0], es[0], f28[ds[0]].name, f16[es[0]].name, f"field read by {me28.name} <-> {me16.name}"])
                    have28.add(ds[0]); have16.add(es[0]); n_read += 1
        self.anchors = {k: v for k, v in self.anchors.items() if v["anchors"]}
        n_anchors += n_read
        n_pairs = len(self.anchors)
        with open(os.path.join(OUT_DIR, "fieldanchors.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(self.anchors, f, indent=1)
        print(f"   field anchors: {n_anchors} getter-anchored field pairs in {n_pairs} classes -> dumps_ref/1.6/fieldanchors.json")

    # ---- vtable slot names: the named slots of the appdata-28 X__VTable structs -> the 1.6 slot of the matched method
    def vtable_slots(self, side, td):
        """slot -> (TypeDef, Method) with the base chain walked (overrides replace)"""
        chain, cur, seen = [], td, set()
        while cur is not None and cur.index not in seen:
            seen.add(cur.index)
            chain.append(cur)
            if not cur.base or cur.base in ("object", "Object", "System.Object", "ValueType", "Enum"):
                b = side.m.by_full.get("System.Object") if cur.kind == "class" else None
                cur = b[0] if b and b[0].index not in seen else None
            else:
                cur = side.type_of_cs(cur.base)
        slots = {}
        for t in reversed(chain):
            for me in t.methods:
                if me.slot is not None:
                    slots[me.slot] = (t, me)
        return slots

    def vtable_names(self):
        """named slots of the appdata-28 X__VTable structs -> 1.6 slots. A header slot is anchored through the 2.8 METHOD it
        names (readable name, or the `// 2.8: OBF` comment gen_types wrote), never through its slot number (the header's
        slot order is the 2.6/3.3 one); that method is found in the 1.6 vtable by name or by the method matcher."""
        M = self.M
        self.vtables = {}
        vt_lines = {}
        for path in (os.path.join(SRC, "appdata-28", "il2cpp-types.h"), os.path.join(SRC, "appdata-28", "il2cpp-types-relic.h")):
            txt = read(path)
            for m in re.finditer(r'^\s*struct\s+([A-Za-z_]\w*)__VTable\s*\{(.*?)^\s*\};', txt, re.S | re.M):
                rows = []
                for ln in m.group(2).splitlines():
                    mm = re.match(r'\s*VirtualInvokeData\s+([A-Za-z_]\w*)\s*;(?:\s*//\s*2\.8:\s*([A-Z]{11}))?', ln)
                    if mm:
                        rows.append((mm.group(1), mm.group(2)))
                vt_lines[m.group(1)] = rows
        n = 0
        for insp, rows in sorted(vt_lines.items()):
            if insp not in self.inv.map or all(nm.startswith("_slot") for nm, obf in rows):
                continue
            td28 = self.inv.map[insp][0]
            r = M.c28.get(td28.index)
            if not r:
                continue
            td16 = self.m16.by_index[r["i16"]]
            s28 = self.vtable_slots(self.s28, td28)
            s16 = self.vtable_slots(self.s16, td16)
            rev16 = {}
            for slot, (t, me) in s16.items():
                if me.rva is not None:
                    rev16.setdefault(me.rva, slot)
            out = {}
            for slot, (nm, obf) in enumerate(rows):
                if nm.startswith("_slot") or is_obf(nm):
                    continue
                base_nm = re.sub(r'_\d+$', '', nm)
                target = obf or base_nm
                c28 = [(sl, tm) for sl, tm in s28.items() if tm[1].name == target]
                if len(c28) > 1 and slot in [sl for sl, tm in c28]:
                    c28 = [(sl, tm) for sl, tm in c28 if sl == slot]
                if len(c28) != 1:
                    continue
                sl28, (t28, me28) = c28[0]
                if not is_obf(target):
                    hit = [sl for sl, (t, me) in s16.items() if me.name == target]
                    if len(hit) == 1:
                        out[hit[0]] = [nm, "readable name"]
                    continue
                if me28.rva is None:
                    continue
                owner16 = td16 if t28 is td28 else M.mapped16(t28)
                if owner16 is None:
                    continue
                res = M.match_method(t28, me28, owner16)
                if "rva16" in res and res["rva16"] in rev16 and res.get("conf") in ("high", "medium"):
                    out[rev16[res["rva16"]]] = [nm, f"{res['rule']} ({res.get('conf')}) {res.get('dump16')}"]
            if out:
                self.vtables[insp] = {"d28": td28.full, "d16": td16.full, "slots16": len(s16), "names": {str(k): v for k, v in sorted(out.items())}}
                n += len(out)
        print(f"   vtable slots: {n} named slots mapped in {len(self.vtables)} classes")

    # ---- slots
    def resolve_slots(self):
        M = self.M
        self.slots = {}
        for name, info in self.func_info.items():
            if info["kind"] == "mi":
                if not info.get("slot28"):
                    self.slots[name] = {"reason": "2.8 MethodInfo slot not in the 2.8 dump" if info["rva28"] else "unresolved in 2.8 (0x0)"}
                    continue
                s28 = info["slot28"]
                body = s28[len("Method$"):]
                body = body[:-2] if body.endswith("()") else body
                depth, cut = 0, -1
                for i, ch in enumerate(body):
                    if ch == "<":
                        depth += 1
                    elif ch == ">":
                        depth -= 1
                    elif ch == "." and depth == 0:
                        cut = i
                cls, meth = body[:cut], body[cut + 1:]
                cls_b, meth_b = strip_generic(cls), strip_generic(meth)
                o28 = self.s28.find_type(cls_b)
                o28 = o28[0] if len(o28) == 1 else None
                if o28 is None:
                    self.slots[name] = {"reason": f"owner {cls_b} of {s28} not a type"}
                    continue
                o16 = o28 if readable_type(o28) and self.s16.find_type(o28.full) else M.mapped16(o28)
                if o16 is None:
                    self.slots[name] = {"reason": f"owner {o28.full} not matched"}
                    continue
                o16 = self.s16.find_type(o16.full)[0] if readable_type(o16) and self.s16.find_type(o16.full) else o16
                # map type args
                def map_args(args):
                    out = []
                    for ga in args:
                        t = self.s28.type_of_cs(ga)
                        if t is None:
                            out.append(ga if not is_obf(ga) else None)
                        elif readable_type(t):
                            out.append(t.full)
                        else:
                            t16 = M.mapped16(t)
                            out.append(t16.full if t16 is not None else None)
                    return out
                def short_args(args):
                    out = []
                    for a in args:
                        tds = self.s16.find_type(a)
                        out.append(tds[0].name if len(tds) == 1 else a.rsplit(".", 1)[-1])
                    return out
                cargs = map_args(generic_args(cls))
                margs = map_args(generic_args(meth))
                if any(a is None for a in cargs + margs):
                    self.slots[name] = {"reason": f"type argument of {s28} not matched", "cands": []}
                    continue
                cargs, margs = short_args(cargs), short_args(margs)
                # method name in 1.6
                if is_obf(meth_b):
                    # generic method of an obfuscated owner: matched by its instantiation type-argument set (match_generic_method)
                    m16name = self.generic_method_map.get((o28.index, meth_b))
                    if m16name is None:
                        g16 = self.match_generic_method(o28, meth_b)
                        m16name = g16["name16"] if g16 and "name16" in g16 else None
                    if m16name is None:
                        self.slots[name] = {"reason": f"generic method {meth_b} of {o28.full} not matched (no instantiation-argument evidence)"}
                        continue
                else:
                    m16name = meth_b
                o16name = strip_generic(o16.full) if not cargs else f"{strip_generic(o16.full)}<{', '.join(cargs)}>"
                want = f"Method${o16name}.{m16name}" + (f"<{', '.join(margs)}>" if margs else "") + "()"
                rva = self.m16.methodinfo.get(want)
                if rva is None:
                    # tolerate spacing differences / full vs short generic argument names
                    norm_want = re.sub(r'\s+', '', want)
                    for k, v in self.m16.methodinfo.items():
                        if k.startswith("Method$" + strip_generic(o16.full).split("<")[0]) and re.sub(r'\s+', '', k) == norm_want:
                            rva = v
                            break
                if rva is None:
                    self.slots[name] = {"reason": f"1.6 MethodInfo slot {want} not in script.json", "cands": []}
                else:
                    self.slots[name] = {"rva16": rva, "slot16": want, "rule": "slot via class map", "conf": "high"}
            elif info["kind"] == "td":
                if info.get("keyword"):
                    rva = self.m16.typeinfo.get(info["keyword"])
                    self.slots[name] = {"rva16": rva, "slot16": info["keyword"] + "_TypeInfo", "rule": "TypeInfo slot (C# keyword type)", "conf": "high"} if rva else {"reason": f"no {info['keyword']}_TypeInfo slot in 1.6"}
                    continue
                if not info.get("td28"):
                    self.slots[name] = {"reason": "2.8 TypeInfo slot not in the 2.8 dump" if info["rva28"] else "unresolved in 2.8 (0x0)"}
                    continue
                t28 = info["td28"]
                t16 = M.mapped16(t28)
                if t16 is None:
                    if readable_type(t28):
                        hits = [t for t in self.s16.find_type(t28.full) if t.full == t28.full]
                        t16 = hits[0] if len(hits) == 1 else None
                if t16 is None:
                    self.slots[name] = {"reason": f"type {t28.full} not matched", "cands": []}
                    continue
                rva = self.m16.typeinfo.get(t16.full)
                if rva is None:
                    self.slots[name] = {"reason": f"{t16.full} has no TypeInfo slot in the 1.6 script.json"}
                else:
                    self.slots[name] = {"rva16": rva, "slot16": t16.full + "_TypeInfo", "rule": "TypeInfo slot via class map", "conf": M.c28.get(t28.index, {}).get("conf", "high")}

    # ---- il2cpp API by export name
    def resolve_api(self):
        text = read(os.path.join(SRC, "appdata-28", "il2cpp-api-functions.h"))
        self.api = collections.OrderedDict()
        for ln in text.splitlines():
            m = re.match(r'^\s*DO_API(?:_NO_RETURN)?\(\s*(0x[0-9A-Fa-f]+)\s*,\s*(.+?),\s*([A-Za-z_]\w*)\s*,', ln)
            if m and not ln.strip().startswith("//"):
                name = m.group(3)
                rva = self.x16.exports.get(name)
                self.api[name] = {"rva28": int(m.group(1), 16), "rva16": rva, "rule": "export table" if rva else None}
        n = sum(1 for v in self.api.values() if v["rva16"])
        print(f"   il2cpp API: {n}/{len(self.api)} DO_API entries resolved from the 1.6 export table ({len(self.x16.exports)} exports)")

    # ---- UnityPlayer functions by byte pattern
    def resolve_unity(self):
        self.unity = collections.OrderedDict()
        text = read(os.path.join(SRC, "appdata-28", "il2cpp-unityplayer-functions.h"))
        entries = []
        for ln in text.splitlines():
            m = re.match(r'^\s*DO_APP_FUNC\(\s*(0x[0-9A-Fa-f]+)\s*,\s*(.+?),\s*([A-Za-z_]\w*)\s*,', ln)
            if m and not ln.strip().startswith("//"):
                entries.append((m.group(3), int(m.group(1), 16)))
        u28 = self.args.unity28 or DEFAULT_UNITY["28"]
        u16 = self.args.unity16 or DEFAULT_UNITY["16"]
        if not (os.path.exists(u28) and os.path.exists(u16)):
            for name, rva in entries:
                self.unity[name] = {"rva28": rva, "rva16": None, "reason": "UnityPlayer.dll not available for pattern search"}
            return
        p28, p16 = PE(u28), PE(u16)
        code16 = [(s, p16.b[s[4]:s[4] + min(s[1], s[3])]) for s in p16.code_sections()]
        for name, rva in entries:
            res = {"rva28": rva, "rva16": None}
            body = p28.read(rva, 96)
            if len(body) < 32:
                res["reason"] = "2.8 function bytes not readable"
                self.unity[name] = res
                continue
            found = None
            for length in (64, 48, 40, 32, 24):
                pat = self.mask_pattern(body[:length])
                rx = re.compile(pat, re.S)
                hits = []
                for s, data in code16:
                    for m in rx.finditer(data):
                        hits.append(s[2] + m.start())
                        if len(hits) > 4:
                            break
                if len(hits) == 1:
                    found = (hits[0], length)
                    break
                if len(hits) > 1:
                    res["reason"] = f"pattern of {length} bytes matches {len(hits)} places in the 1.6 UnityPlayer.dll"
                    # keep trying longer? lengths descend; a shorter one will match even more
                    break
            if found:
                res.update({"rva16": found[0], "rule": f"byte pattern ({found[1]} bytes, rel32/RIP displacements masked), unique in UnityPlayer.dll", "conf": "medium"})
            else:
                res.setdefault("reason", "no unique byte-pattern hit in the 1.6 UnityPlayer.dll")
            self.unity[name] = res
        n = sum(1 for v in self.unity.values() if v["rva16"])
        print(f"   UnityPlayer: {n}/{len(self.unity)} functions located by byte pattern")

    @staticmethod
    def mask_pattern(b):
        """regex bytes pattern: literal bytes, with the 4 bytes after E8/E9 (rel32) and after RIP-relative ModRM masked"""
        out = []
        i = 0
        n = len(b)
        while i < n:
            c = b[i]
            out.append(re.escape(bytes([c])))
            if c in (0xE8, 0xE9) and i + 4 < n:
                out.append(b"...."); i += 5; continue
            if c in (0x8B, 0x8D, 0x89, 0x3B, 0x39, 0x88, 0x8A, 0xFF, 0x63, 0x85, 0x84, 0x2B, 0x03, 0x33, 0x0B, 0x23) and i + 1 < n and (b[i + 1] & 0xC7) == 0x05 and i + 5 < n:
                out.append(re.escape(bytes([b[i + 1]])) + b"...."); i += 6; continue
            if c in (0x80, 0x83, 0xC6) and i + 1 < n and (b[i + 1] & 0xC7) == 0x05 and i + 6 < n:
                out.append(re.escape(bytes([b[i + 1]])) + b"...." + re.escape(bytes([b[i + 6]]))); i += 7; continue
            if c == 0x0F and i + 2 < n and (b[i + 2] & 0xC7) == 0x05 and i + 6 < n:
                out.append(re.escape(bytes([b[i + 1], b[i + 2]])) + b"...."); i += 7; continue
            i += 1
        return b"".join(out)

    # ---- outputs
    def write_outputs(self):
        M = self.M
        os.makedirs(OUT_DIR, exist_ok=True)
        classes = {}
        for i28, r in M.c28.items():
            td28, td16 = self.m28.by_index[i28], self.m16.by_index[r["i16"]]
            classes[td28.full] = {"i28": i28, "i16": r["i16"], "d16": td16.full, "kind": td28.kind, "rule": r["rule"], "score": None if r["score"] is None else round(r["score"], 3),
                                  "conf": r["conf"], "note": r.get("note", ""), "cands": r.get("cands", [])[:4], "round": r.get("round")}
        unmatched_classes = {}
        for i in sorted(self.required):
            if i in M.c28:
                continue
            td = self.m28.by_index[i]
            det = M.fails.get(i, {})
            unmatched_classes[td.full] = {"i28": i, "kind": td.kind, "reason": det.get("reason", "not attempted (readable name absent in 1.6 or no pool)"), "cands": det.get("cands", [])[:4],
                                          "nmethods": len(td.methods), "nfields": len(td.instance_fields())}
        # Inspector name -> 1.6 type (classmap.json for gen_types / gen_appdata16)
        classmap = {}
        insp_rows = []
        for name, (td28, how) in sorted(self.inv.map.items()):
            r = M.c28.get(td28.index)
            td16 = self.m16.by_index[r["i16"]] if r else None
            insp_rows.append({"insp": name, "d28": td28.full, "how28": how, "d16": td16.full if td16 else None, "rule": r["rule"] if r else None, "conf": r["conf"] if r else None})
            if td16 is not None:
                classmap[name] = {"dump": td16.full, "kind": td16.kind, "index": td16.index, "how": f"{r['rule']} ({r['conf']}) via 2.8 {td28.full} [{how}]"}
        functions = {}
        for name, info in self.func_info.items():
            row = {"kind": info["kind"], "rva28": info["rva28"]}
            if info["kind"] == "func":
                row["dump28"] = info.get("dump28") or (info.get("generic_names") or [None])[0]
                if info.get("folded"):
                    row["folded28"] = info["folded"][:4]
                if info.get("status"):
                    row["status"] = info["status"]
                r = M.m28.get(info["rva28"]) if info["rva28"] else None
                if r and r.get("rva16") is not None:
                    row.update({"rva16": r["rva16"], "dump16": r["dump16"], "rule": r["rule"], "score": round(r.get("score") or 0, 3), "conf": r.get("conf"), "note": r.get("note", ""), "cands": r.get("cands", [])[:6]})
                    td28 = info.get("td28")
                    if td28 is not None:
                        cr = M.c28.get(td28.index)
                        row["class_rule"] = f"{cr['rule']} ({cr['conf']})" if cr else None
                elif r:
                    row.update({"rva16": None, "reason": r.get("reason"), "cands": r.get("cands", [])[:6]})
                    td28 = info.get("td28")
                    if td28 is not None and td28.index in M.c28:
                        cr = M.c28[td28.index]
                        row["class_rule"] = f"{cr['rule']} ({cr['conf']})"
                else:
                    row.update({"rva16": None, "reason": info.get("status") or "not attempted"})
            else:
                s = self.slots.get(name, {})
                row.update({"rva16": s.get("rva16"), "slot28": info.get("slot28") or info.get("typeinfo28"), "slot16": s.get("slot16"), "rule": s.get("rule"), "conf": s.get("conf"), "reason": s.get("reason")})
            functions[name] = row
        out = {"ver": "16", "base": "28", "dll16": self.x16.dll_path, "dll28": self.x28.dll_path,
               "classes": classes, "unmatched_classes": unmatched_classes, "inspector": insp_rows, "inventory_unresolved": self.inv.unres,
               "functions": functions, "api": self.api, "unity": self.unity, "exports16": self.x16.exports, "vtables": getattr(self, "vtables", {}),
               "stats": self.stats(classes, functions)}
        with open(os.path.join(OUT_DIR, "match16.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(out, f, indent=1, sort_keys=False, default=str)
        with open(os.path.join(OUT_DIR, "classmap.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump({"ver": "16", "proto_base": self.s16.d.proto_base, "classmap": classmap, "candidates": {}, "paramdict": {}}, f, indent=1, sort_keys=True)
        # unresolved.txt
        lines = []
        for name, row in functions.items():
            if row.get("rva16") is None:
                reason = row.get("reason") or row.get("status") or "unresolved"
                lines.append(f"{name}\t{reason}")
                for c in row.get("cands", [])[:6]:
                    lines.append(f"\tcandidate {c}")
        with open(os.path.join(OUT_DIR, "unresolved.txt"), "w", encoding="utf-8", newline="\n") as f:
            f.write(f"# appdata-16 entries matcher16.py could not resolve ({len([1 for l in lines if not l.startswith(chr(9))])}); reasons + candidates\n")
            f.write("\n".join(lines) + ("\n" if lines else ""))
        with open(os.path.join(OUT_DIR, "matcher16.log"), "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(M.log + self.inv.log))
        st = out["stats"]
        print(f"== classes: {st['classes_matched']} matched / {st['classes_required']} required; functions: {st['funcs_resolved']}/{st['funcs_total']} "
              f"(+{st['funcs_unresolved_28']} already 0x0 in 2.8); slots: {st['slots_resolved']}/{st['slots_total']}; api {st['api_resolved']}/{st['api_total']}; unity {st['unity_resolved']}/4")
        print(f"   rules: {st['class_rules']} | {st['func_rules']}")
        print(f"   -> {os.path.join(OUT_DIR, 'match16.json')}, classmap.json, unresolved.txt")

    def stats(self, classes, functions):
        cr = collections.Counter(v["rule"] for v in classes.values())
        fr = collections.Counter(v.get("rule") for v in functions.values() if v["kind"] == "func" and v.get("rva16") is not None)
        funcs = [v for v in functions.values() if v["kind"] == "func"]
        slots = [v for v in functions.values() if v["kind"] != "func"]
        return {"classes_matched": len(classes), "classes_required": len(self.required), "class_rules": dict(cr),
                "funcs_total": len(funcs), "funcs_resolved": sum(1 for v in funcs if v.get("rva16") is not None), "funcs_unresolved_28": sum(1 for v in funcs if not v["rva28"]),
                "func_rules": dict(fr), "slots_total": len(slots), "slots_resolved": sum(1 for v in slots if v.get("rva16") is not None),
                "api_total": len(self.api), "api_resolved": sum(1 for v in self.api.values() if v["rva16"]),
                "unity_resolved": sum(1 for v in self.unity.values() if v["rva16"])}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dll28", help=f"2.8 UserAssembly.dll (default {DEFAULT_DLL['28']}); only needed until dumps_ref/2.8/xrefs.pkl exists")
    ap.add_argument("--dll16", help=f"1.6 UserAssembly.dll (default {DEFAULT_DLL['16']})")
    ap.add_argument("--unity28", help="2.8 UnityPlayer.dll (pattern source for il2cpp-unityplayer-functions.h)")
    ap.add_argument("--unity16", help="1.6 UnityPlayer.dll")
    ap.add_argument("--dump28", help="2.8 dump dir (default dumps_ref/2.8)")
    ap.add_argument("--dump16", help="1.6 dump dir (default dumps_ref/1.6/greenxemotion)")
    ap.add_argument("--verbose", "-v", action="store_true")
    a = ap.parse_args()
    Runner(a).run()


if __name__ == "__main__":
    main()
