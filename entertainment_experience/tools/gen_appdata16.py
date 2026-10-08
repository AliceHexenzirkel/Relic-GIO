#!/usr/bin/env python3
"""Emits mod/cheat-library/src/appdata-16/ (game 1.6 OS) from the 2.8 -> 1.6 match produced by tools/matcher16.py
(dumps_ref/1.6/match16.json + classmap.json) and the 1.6 IL2CPP dump (dumps_ref/1.6/greenxemotion/):

    gen_appdata16.py [--dump16 DIR] [--build-log MSBUILD.log] [--fresh] [--verbose]

  il2cpp-functions.h              every DO_APP_FUNC / DO_APP_FUNC_METHODINFO of appdata-28 with the 1.6 RVA and a
                                  `// 1.6: <rule> (<confidence>) <1.6 dump name> | <note>` comment; unmatched -> 0x0 +
                                  `// RELIC-TODO-16 <reason>` (a 0x0 offset is a null pointer at run time: HookManager
                                  skips the hook; direct callers must be gated)
  il2cpp-types-ptr.h              DO_TYPEDEF slots from the 1.6 TypeInfo metadata-usage slots
  il2cpp-api-functions.h          il2cpp_* API from the 1.6 PE export table (0x0 where 1.6 does not export the entry)
  il2cpp-unityplayer-functions.h  4 entries located by byte pattern in the 1.6 UnityPlayer.dll (0x0 otherwise)
  il2cpp-metadata-version.h       24 (Unity 2017.4 / IL2CPP metadata v24)
  il2cpp-types.h                  the IL2CPP runtime prelude of appdata-28 with Il2CppClass / Il2CppClass_0 / Il2CppClass_1 /
                                  MethodInfo replaced by the 1.6 (metadata v24.0) layouts - 1.6 has `static_fields` at 0xA0 and
                                  `cctor_finished` at 0xBC (checked against the il2cpp_codegen code in UserAssembly.dll),
                                  2.8 at 0xB8/0xE0 - plus the System basics (Object/Type/String/Byte__Array...) copied from
                                  appdata-28, then `#include "il2cpp-types-relic.h"`
  il2cpp-types-relic.h            every application type the appdata-28 headers define, regenerated from the 1.6 dump with
                                  the gen_types.py machinery (Il2CppInspector shapes, 2.8/3.3 field names transplanted onto
                                  the obfuscated 1.6 fields by type-sequence alignment, padding from the dump offsets).
                                  A type whose class could not be matched keeps the 2.8 layout flagged
                                  RELIC-LAYOUT-UNVERIFIED-16 when the feature code dereferences it (or uses it by value),
                                  else only an opaque forward declaration. BCL generic instantiations (Dictionary_2_*,
                                  Nullable_1_*, LinkedList*) are copied from appdata-28 (same mscorlib, version-independent).
  tools/offsets-16.overrides.json skeleton for hand decisions (apply_overrides.py --ver 16), created if missing

Also: dumps_ref/1.6/gen16.json (what was regenerated / copied / forward-declared / missing fields, for the report).
Build-log iteration like gen_types.py: `--build-log` adds the types MSBuild complains about (C2027/C2065/...) to the
requests; requests accumulate in the generated header. Idempotent, deterministic, stdlib only.
"""
import argparse, collections, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from dumpmodel import load, is_obf  # noqa: E402
from transplant import HeaderTypes, parse_header, read, write, SRC, DUMPS, norm  # noqa: E402
import gen_types  # noqa: E402
from gen_types import Gen, Emitted, BUILTIN_NAMES  # noqa: E402
from codexref import DEFAULT_DUMP_DIR  # noqa: E402

A28 = os.path.join(SRC, "appdata-28")
A16 = os.path.join(SRC, "appdata-16")
USER = os.path.join(SRC, "user")
OUT = os.path.join(DUMPS, "1.6")
MATCH = os.path.join(OUT, "match16.json")
MARK = "RELIC-TODO-16"
UNVERIFIED = "RELIC-LAYOUT-UNVERIFIED-16"
BASICS = {"Object", "Type", "RuntimeTypeHandle", "Char", "Encoding", "String", "IFormatProvider", "Byte"}   # base names of the System block copied verbatim
SUFFIX_RE = re.compile(r'^(.*?)(__Fields|__VTable|__StaticFields|__Class|__Boxed|__Array|__Enum__Boxed|__Enum)$')
BCL_GENERIC = re.compile(r'^(Dictionary_2_|Nullable_1_|LinkedList_1_|LinkedListNode_1_|HashSet_1_|KeyValuePair_2_|Queue_1_|Stack_1_)')
def base_of_name(n):
    """strip the Il2CppInspector suffixes repeatedly: Char__Array__VTable -> Char"""
    while True:
        m = SUFFIX_RE.match(n)
        if not m or not m.group(2):
            return n
        n = m.group(1)


OLD_COMMENT = re.compile(r'\s*//\s*(?:2\.8:|28:\s*OVERRIDE|RELIC-TODO-28|2\.8 name:).*$')

IL2CPPCLASS_16 = '''typedef struct Il2CppClass
{
    const Il2CppImage* image;
    void* gc_desc;
    const char* name;
    const char* namespaze;
    const Il2CppType* byval_arg;
    const Il2CppType* this_arg;
    Il2CppClass* element_class;
    Il2CppClass* castClass;
    Il2CppClass* declaringType;
    Il2CppClass* parent;
    Il2CppGenericClass* generic_class;
    const Il2CppTypeDefinition* typeDefinition;
    const Il2CppInteropData* interopData;
    FieldInfo* fields;
    const EventInfo* events;
    const PropertyInfo* properties;
    const MethodInfo** methods;
    Il2CppClass** nestedTypes;
    Il2CppClass** implementedInterfaces;
    Il2CppRuntimeInterfaceOffsetPair* interfaceOffsets;
    void* static_fields;
    const Il2CppRGCTXData* rgctx_data;
    struct Il2CppClass** typeHierarchy;
    uint32_t cctor_started;
    uint32_t cctor_finished;
    __declspec(align(8)) size_t cctor_thread;
    GenericContainerIndex genericContainerIndex;
    CustomAttributeIndex customAttributeIndex;
    uint32_t instance_size;
    uint32_t actualSize;
    uint32_t element_size;
    int32_t native_size;
    uint32_t static_fields_size;
    uint32_t thread_static_fields_size;
    int32_t thread_static_fields_offset;
    uint32_t flags;
    uint32_t token;
    uint16_t method_count;
    uint16_t property_count;
    uint16_t field_count;
    uint16_t event_count;
    uint16_t nested_type_count;
    uint16_t vtable_count;
    uint16_t interfaces_count;
    uint16_t interface_offsets_count;
    uint8_t typeHierarchyDepth;
    uint8_t genericRecursionDepth;
    uint8_t rank;
    uint8_t minimumAlignment;
    uint8_t packingSize;
    uint8_t initialized_and_no_error : 1;
    uint8_t valuetype : 1;
    uint8_t initialized : 1;
    uint8_t enumtype : 1;
    uint8_t is_generic : 1;
    uint8_t has_references : 1;
    uint8_t init_pending : 1;
    uint8_t size_inited : 1;
    uint8_t has_finalize : 1;
    uint8_t has_cctor : 1;
    uint8_t is_blittable : 1;
    uint8_t is_import_or_windows_runtime : 1;
    uint8_t is_vtable_initialized : 1;
    uint8_t has_initialization_error : 1;
    VirtualInvokeData vtable[32];
} Il2CppClass;'''

IL2CPPCLASS_0_16 = '''typedef struct Il2CppClass_0 {
    const Il2CppImage* image;
    void* gc_desc;
    const char* name;
    const char* namespaze;
    const Il2CppType* byval_arg;
    const Il2CppType* this_arg;
    Il2CppClass* element_class;
    Il2CppClass* castClass;
    Il2CppClass* declaringType;
    Il2CppClass* parent;
    Il2CppGenericClass* generic_class;
    const Il2CppTypeDefinition* typeDefinition;
    const Il2CppInteropData* interopData;
    FieldInfo* fields;
    const EventInfo* events;
    const PropertyInfo* properties;
    const MethodInfo** methods;
    Il2CppClass** nestedTypes;
    Il2CppClass** implementedInterfaces;
} Il2CppClass_0;'''

IL2CPPCLASS_1_16 = '''typedef struct Il2CppClass_1 {
    struct Il2CppClass** typeHierarchy;
    uint32_t cctor_started;
    uint32_t cctor_finished;
#ifdef IS_32BIT
    uint32_t cctor_thread;
#else
    __declspec(align(8)) size_t cctor_thread;
#endif
    GenericContainerIndex genericContainerIndex;
    CustomAttributeIndex customAttributeIndex;
    uint32_t instance_size;
    uint32_t actualSize;
    uint32_t element_size;
    int32_t native_size;
    uint32_t static_fields_size;
    uint32_t thread_static_fields_size;
    int32_t thread_static_fields_offset;
    uint32_t flags;
    uint32_t token;
    uint16_t method_count;
    uint16_t property_count;
    uint16_t field_count;
    uint16_t event_count;
    uint16_t nested_type_count;
    uint16_t vtable_count;
    uint16_t interfaces_count;
    uint16_t interface_offsets_count;
    uint8_t typeHierarchyDepth;
    uint8_t genericRecursionDepth;
    uint8_t rank;
    uint8_t minimumAlignment;
    uint8_t packingSize;
    uint8_t initialized_and_no_error : 1;
    uint8_t valuetype : 1;
    uint8_t initialized : 1;
    uint8_t enumtype : 1;
    uint8_t is_generic : 1;
    uint8_t has_references : 1;
    uint8_t init_pending : 1;
    uint8_t size_inited : 1;
    uint8_t has_finalize : 1;
    uint8_t has_cctor : 1;
    uint8_t is_blittable : 1;
    uint8_t is_import_or_windows_runtime : 1;
    uint8_t is_vtable_initialized : 1;
    uint8_t has_initialization_error : 1;
} Il2CppClass_1;'''

METHODINFO_16 = '''typedef struct MethodInfo
{
    Il2CppMethodPointer methodPointer;
    InvokerMethod invoker_method;
    const char* name;
    Il2CppClass* klass;
    const Il2CppType* return_type;
    const ParameterInfo* parameters;
    union
    {
        const Il2CppRGCTXData* rgctx_data;
        const Il2CppMethodDefinition* methodDefinition;
    } Il2CppVariant;
    union
    {
        const Il2CppGenericMethod* genericMethod;
        const Il2CppGenericContainer* genericContainer;
    };
    CustomAttributeIndex customAttributeIndex;
    uint32_t token;
    uint16_t flags;
    uint16_t iflags;
    uint16_t slot;
    uint8_t parameters_count;
    uint8_t is_generic : 1;
    uint8_t is_inflated : 1;
    uint8_t wrapper_type : 1;
    uint8_t is_marshaled_from_native : 1;
} MethodInfo;'''


def nl_of(s):
    return "\r\n" if "\r\n" in s else "\n"


def fmt_comment(row):
    if row.get("rva16"):
        c = f"// 1.6: {row.get('rule')} ({row.get('conf')}) {row.get('dump16') or row.get('slot16') or ''}"
        if row.get("note"):
            c += " | " + str(row["note"]).replace("\n", " ")
        return c
    reason = row.get("reason") or row.get("status") or "not matched"
    if reason == "unresolved-28":
        reason = "0x0 in appdata-28 as well (never resolved for 2.8)"
    return f"// {MARK} {reason}"


# ---------------------------------------------------------------- headers 1-5
def gen_functions(match):
    text = read(os.path.join(A28, "il2cpp-functions.h"))
    nl = nl_of(text)
    out = text.split(nl)
    funcs = match["functions"]
    n_ok = n_todo = 0
    for hl in parse_header(text):
        if hl.kind not in ("func", "mi"):
            continue
        row = funcs.get(hl.name, {})
        rva = row.get("rva16") or 0
        m = hl.m
        new_off = f"0x{rva:08X}" if rva else "0x0"
        if hl.kind == "func":
            body = f"{m.group(1)}{new_off}{m.group(3)}{m.group(4)}{m.group(5)}{m.group(6)}{m.group(7)}{m.group(8)}{m.group(9)}"
        else:
            body = f"{m.group(1)}{new_off}{m.group(3)}{m.group(4)}{m.group(5)}"
        tail = OLD_COMMENT.sub("", hl.tail).rstrip()
        if hl.offset == 0 and not row:
            row = {"status": "unresolved-28"}
        out[hl.idx] = body + (tail + " " if tail.strip() else "  ") + fmt_comment(row)
        if rva:
            n_ok += 1
        else:
            n_todo += 1
    head = ("// Relic: game 1.6 (OS) offsets generated by tools/gen_appdata16.py from dumps_ref/1.6/match16.json (tools/matcher16.py)" + nl +
            "// -- every line carries its matching rule and confidence; 0x0 = RELIC-TODO-16 (null pointer at run time). Do not edit by hand:" + nl +
            "//    hand decisions go to tools/offsets-16.overrides.json (apply_overrides.py --ver 16)." + nl)
    write(os.path.join(A16, "il2cpp-functions.h"), out[0] + nl + head + nl.join(out[1:]))
    print(f"il2cpp-functions.h: {n_ok} resolved, {n_todo} RELIC-TODO-16")
    return n_ok, n_todo


def gen_types_ptr(match):
    text = read(os.path.join(A28, "il2cpp-types-ptr.h"))
    nl = nl_of(text)
    out = text.split(nl)
    funcs = match["functions"]
    n_ok = n_todo = 0
    for hl in parse_header(text):
        if hl.kind != "td":
            continue
        row = funcs.get(hl.name, {})
        rva = row.get("rva16") or 0
        m = hl.m
        body = f"{m.group(1)}{'0x%08X' % rva if rva else '0x0'}{m.group(3)}{m.group(4)}{m.group(5)}"
        tail = OLD_COMMENT.sub("", hl.tail).rstrip()
        out[hl.idx] = body + (tail + " " if tail.strip() else "  ") + fmt_comment(row)
        n_ok += bool(rva); n_todo += not rva
    head = "// Relic: game 1.6 TypeInfo slots generated by tools/gen_appdata16.py (tools/matcher16.py class map -> 1.6 script.json ScriptMetadata)" + nl
    write(os.path.join(A16, "il2cpp-types-ptr.h"), out[0] + nl + head + nl.join(out[1:]))
    print(f"il2cpp-types-ptr.h: {n_ok} resolved, {n_todo} RELIC-TODO-16")


def gen_api(match):
    text = read(os.path.join(A28, "il2cpp-api-functions.h"))
    nl = nl_of(text)
    out = []
    n_ok = n_no = 0
    for ln in text.split(nl):
        m = re.match(r'^(\s*DO_API(?:_NO_RETURN)?\(\s*)(0x[0-9A-Fa-f]+)(\s*,\s*.+?,\s*)([A-Za-z_]\w*)(\s*,.*\)\s*;)(.*)$', ln)
        if m and not ln.strip().startswith("//"):
            name = m.group(4)
            row = match["api"].get(name, {})
            rva = row.get("rva16") or 0
            tail = re.sub(r'\s*//.*$', '', m.group(6)).rstrip()
            if rva:
                ln = f"{m.group(1)}0x{rva:08X}{m.group(3)}{name}{m.group(5)}{tail}  // 1.6: export table"
                n_ok += 1
            else:
                ln = f"{m.group(1)}0x0{m.group(3)}{name}{m.group(5)}{tail}  // {MARK} not exported by the 1.6 UserAssembly.dll"
                n_no += 1
        out.append(ln)
    out.insert(1, f"// Relic: game 1.6 il2cpp API RVAs from the PE export table of UserAssembly.dll (tools/matcher16.py; {n_ok} exported, {n_no} not)")
    write(os.path.join(A16, "il2cpp-api-functions.h"), nl.join(out))
    print(f"il2cpp-api-functions.h: {n_ok} from the export table, {n_no} not exported (0x0)")


def gen_unity(match):
    text = read(os.path.join(A28, "il2cpp-unityplayer-functions.h"))
    nl = nl_of(text)
    out = []
    for ln in text.split(nl):
        m = re.match(r'^(\s*DO_APP_FUNC\(\s*)(0x[0-9A-Fa-f]+)(\s*,\s*.+?,\s*)([A-Za-z_]\w*)(\s*,.*\)\s*;)(.*)$', ln)
        if m and not ln.strip().startswith("//"):
            row = match["unity"].get(m.group(4), {})
            rva = row.get("rva16") or 0
            if rva:
                ln = f"{m.group(1)}0x{rva:08X}{m.group(3)}{m.group(4)}{m.group(5)}  // 1.6: {row.get('rule')} ({row.get('conf')}) UnityPlayer.dll"
            else:
                ln = f"{m.group(1)}0x0{m.group(3)}{m.group(4)}{m.group(5)}  // {MARK} {row.get('reason', 'not located')}"
        out.append(ln)
    out.insert(1, "// Relic: game 1.6 UnityPlayer.dll functions located by byte pattern from the 2.8 ones (tools/matcher16.py); none is used by the feature code")
    write(os.path.join(A16, "il2cpp-unityplayer-functions.h"), nl.join(out))


def gen_metadata_version():
    text = read(os.path.join(A28, "il2cpp-metadata-version.h"))
    text = text.replace("#define __IL2CPP_METADATA_VERSION 245", "#define __IL2CPP_METADATA_VERSION 24")
    text = text.replace("// Target Unity version: 2019.4.21 - 2019.4.24", "// Target Unity version: 2017.4.30 (game 1.6, IL2CPP metadata v24.0)")
    write(os.path.join(A16, "il2cpp-metadata-version.h"), text)


def gen_types_h():
    """prelude (2.8, runtime structs replaced by the 1.6 layouts) + System basics block + include of the generated types"""
    text = read(os.path.join(A28, "il2cpp-types.h"))
    nl = nl_of(text)
    i_ns = text.find("namespace app {")
    i_end = text.find("#endif", i_ns) + len("#endif")
    prelude = text[:i_end]
    body = text[i_end:]
    # replace the runtime structs
    def repl(txt, name, new):
        rx = re.compile(r'typedef struct ' + re.escape(name) + r'\s*\{.*?\n\} ' + re.escape(name) + r';', re.S)
        m = rx.search(txt)
        if not m:
            raise SystemExit(f"prelude: typedef struct {name} not found in appdata-28/il2cpp-types.h")
        return txt[:m.start()] + new.replace("\n", nl) + txt[m.end():]
    prelude = repl(prelude, "Il2CppClass", IL2CPPCLASS_16)
    prelude = repl(prelude, "Il2CppClass_0", IL2CPPCLASS_0_16)
    prelude = repl(prelude, "Il2CppClass_1", IL2CPPCLASS_1_16)
    prelude = repl(prelude, "MethodInfo", METHODINFO_16)
    prelude = prelude.replace("// Target Unity version: 2019.4.21 - 2019.4.24",
                              "// Target Unity version: 2017.4.30 (game 1.6) - Relic: IL2CPP runtime prelude of appdata-28 with Il2CppClass / Il2CppClass_0 /" + nl +
                              "// Il2CppClass_1 / MethodInfo replaced by the metadata-v24.0 layouts the 1.6 client uses (static_fields @0xA0, cctor_finished @0xBC," + nl +
                              "// vtable @0x110 - verified against il2cpp_codegen code in the 1.6 UserAssembly.dll); generated by tools/gen_appdata16.py", 1)
    # basics block: from the banner to the first non-basic definition
    lines = body.split(nl)
    cut = None
    for i, ln in enumerate(lines):
        m = re.match(r'^\s*(?:struct|enum class|union)\s+(?:__declspec\(align\(\d+\)\)\s+)?([A-Za-z_]\w*)\s*(?::\s*\w+\s*)?[{;]', ln)
        if m:
            if base_of_name(m.group(1)) not in BASICS:
                cut = i
                break
    basics = nl.join(lines[:cut]).rstrip() + nl
    out = (prelude + basics + nl +
           "    // ---- everything else: regenerated from the 1.6 dump by tools/gen_appdata16.py (il2cpp-types-relic.h) ----" + nl + nl +
           "#if !defined(_GHIDRA_) && !defined(_IDA_)" + nl + "}" + nl + "#endif" + nl + nl +
           "// Relic: the application types (generated from the 1.6 dump; see the header of that file)" + nl +
           '#include "il2cpp-types-relic.h"' + nl)
    write(os.path.join(A16, "il2cpp-types.h"), out)
    print(f"il2cpp-types.h: prelude ({len(prelude.splitlines())} lines, 1.6 Il2CppClass/MethodInfo) + basics ({cut} lines) + include")


# ---------------------------------------------------------------- which 2.8 header types does the feature code dereference?
def feature_usage():
    fields, names, members = set(), set(), set()
    for root, _, files in os.walk(USER):
        for f in files:
            if f.endswith((".cpp", ".h")):
                t = read(os.path.join(root, f))
                fields.update(re.findall(r'(?:->|\.)fields\.([A-Za-z_]\w*)', t))
                fields.update(re.findall(r'(?:->|\.)fields\._\.([A-Za-z_]\w*)', t))
                names.update(re.findall(r'\bapp::([A-Za-z_]\w*)', t))
                members.update(re.findall(r'(?:->|\.)([A-Za-z_]\w*)\b(?!\s*\()', t))
    return fields, names, members


# ---------------------------------------------------------------- the type generator (gen_types.Gen driven for 1.6)
class Gen16(Gen):
    def __init__(self, dump_dir, merged28_path, deref, byvalue, anchors, m28, vtables=None, verbose=False):
        super().__init__("16", dump_dir, verbose)
        self.vtables = vtables or {}
        self.h33 = HeaderTypes(merged28_path)                    # the name / fallback source is appdata-28 (types.h + relic.h)
        self.h33_enum_base = {m.group(1): m.group(2) for m in re.finditer(r'enum class\s+([A-Za-z_]\w*)__Enum\s*:\s*(\w+)', self.h33.text)}
        self.deref = deref
        self.byvalue = byvalue
        self.anchors = anchors     # 2.8 dump full name -> {"i16", "anchors": [[off28, off16, name28, name16, how], ...]}
        self.m28 = m28             # the 2.8 dump model (header field index <-> 2.8 dump field offset)
        self.anchors_by_i16 = {v["i16"]: v for v in anchors.values()}
        self.copied = []       # (name, reason) copied from appdata-28
        self.opaque = []       # names left as forward declarations (unmatched, never dereferenced)
        self.regen = []        # names regenerated from the 1.6 dump
        self.anchored = []     # (struct, 2.8 field name, 1.6 field name, how) alignments forced by getter anchors / unique identities
        self._ctx = None
        txt = self.h33.text
        for m in re.finditer(r'^\s*enum\s+([A-Za-z_]\w*?)(__Enum)?\s*\{(.*?)\};', txt, re.S | re.M):
            vals = {a: int(b, 0) for a, b in re.findall(r'([A-Za-z_]\w*)\s*=\s*(0x[0-9A-Fa-f]+|-?\d+)', m.group(3))}
            if m.group(2):
                self.h33.enums.setdefault(m.group(1), vals)
            else:
                self.h33.enums_raw.setdefault(m.group(1), vals)
        self.wrapper_fields = {m.group(1): m.group(2) for m in re.finditer(r'struct\s+([A-Za-z_]\w*)\s*\{\s*struct\s+[A-Za-z_]\w*__Class\*\s*klass;\s*MonitorData\*\s*monitor;\s*struct\s+([A-Za-z_]\w*)__Fields\s+fields;\s*\}', txt)}
        self.h33_norm = {}
        for n in self.h33.fields:
            self.h33_norm.setdefault(n.replace("MoleMole_", ""), n)
        self.plain_fields = {}
        for m in re.finditer(r'^\s*struct\s+(?:__declspec\(align\(\d+\)\)\s+)?([A-Za-z_]\w*)\s*\{(.*?)^\s*\};', txt, re.S | re.M):
            n, body = m.group(1), m.group(2)
            if SUFFIX_RE.match(n) or "MonitorData* monitor;" in body or "VirtualInvokeData" in body:
                continue
            flds = []
            for ln in body.splitlines():
                ln = ln.split("//")[0].strip()
                mm = re.match(r'^(?:const\s+)?(.+?)\s+([A-Za-z_]\w*)(\[\d+\])?;$', ln)
                if mm:
                    flds.append((mm.group(1).strip(), mm.group(2)))
            if flds:
                self.plain_fields[n] = flds

    # ---- alignment of the 2.8 header fields onto the 1.6 dump fields: getter anchors + unique identities + sequence
    def gen_class(self, name, td, depth):
        src = self.h33_fields_name(name)
        injected = False
        if src and src != name and name not in self.h33.fields:
            self.h33.fields[name] = self.h33.fields.get(src) or self.plain_fields.get(src) or []
            injected = True
        elif src == name and name not in self.h33.fields and name in self.plain_fields:
            self.h33.fields[name] = self.plain_fields[name]
            injected = True
        h33name = name if name in self.h33.fields else None
        self._ctx = (name, td, h33name)
        try:
            return super().gen_class(name, td, depth)
        finally:
            self._ctx = None
            if injected:
                del self.h33.fields[name]

    def base_name(self, td, depth):
        if td.kind == "class" and td.base == "Object" and td.namespace and td.namespace != "System":
            same_ns = [t for t in self.model.find("Object") if t.namespace == td.namespace]
            if len(same_ns) == 1:
                bt = same_ns[0]
                name = self.reverse.get(bt.index) or self.alias_existing(bt) or self.insp_name(bt)
                self.ensure(name, True, bt, depth + 1)
                if name in self.done or self.exists(name):
                    return name
        return super().base_name(td, depth)

    @staticmethod
    def loose(name):
        n = name
        for pre in ("MoleMole_Config_", "MoleMole_", "Config_"):
            if n.startswith(pre):
                n = n[len(pre):]
        return n

    def h33_fields_name(self, name):
        """the appdata-28 struct that carries the field names of `name`: X__Fields, a MoleMole_-insensitive spelling, the
        __Fields struct the X wrapper embeds (obfuscated 2.6-era names), or the plain value struct X"""
        for c in (name, "MoleMole_" + name, name[len("MoleMole_"):] if name.startswith("MoleMole_") else None):
            if c and c in self.h33.fields:
                return c
        k = name.replace("MoleMole_", "")
        c = self.h33_norm.get(k)
        if c:
            return c
        c = self.wrapper_fields.get(name) or self.wrapper_fields.get("MoleMole_" + name)
        if c and c in self.h33.fields:
            return c
        if name in self.plain_fields:
            return name
        return None

    def align_names(self, toks33, toks28):
        name_of = super().align_names(toks33, toks28)
        if not self._ctx:
            return name_of
        name, td, h33name = self._ctx
        if not h33name:
            return name_of
        fields33 = self.h33.own_fields(h33name)
        own16 = sorted([f for f in td.instance_fields() if f.offset is not None], key=lambda f: f.offset)
        if len(fields33) != len(toks33) or len(own16) != len(toks28):
            return name_of
        forced = {}
        # (a) getter anchors: offset28 -> header index needs the 2.8 dump type behind the header struct
        anc = self.anchors_by_i16.get(td.index)
        if anc is not None:
            td28 = self.m28.by_index.get(anc["i28"])
            own28 = sorted([f for f in td28.instance_fields() if f.offset is not None], key=lambda f: f.offset) if td28 is not None else []
            if len(own28) == len(fields33):
                off_to_i = {f.offset: i for i, f in enumerate(own28)}
                off_to_j = {f.offset: j for j, f in enumerate(own16)}
                for off28, off16, n28, n16, how in anc["anchors"]:
                    i, j = off_to_i.get(off28), off_to_j.get(off16)
                    if i is not None and j is not None and self.tok33(fields33[i][0]).split(":")[0] == toks28[j].split(":")[0]:
                        forced[j] = (i, "getter anchor: " + how)
            else:
                self.say(f"  {name}: header has {len(fields33)} fields, 2.8 dump type {len(own28)} - getter anchors not applied")
        # (b) unique identities: a header field whose type maps (classmap) to exactly the type of exactly one 1.6 field
        id33 = collections.defaultdict(list)
        for i, (c, n) in enumerate(fields33):
            base = c.replace("const ", "").replace("struct ", "").replace("enum ", "").strip().rstrip("*").strip()
            base = re.sub(r'__Enum$', '', base)
            tdx = self.classmap.get(base) or self.classmap.get("MoleMole_" + base) or (self.classmap.get(base[len("MoleMole_"):]) if base.startswith("MoleMole_") else None)
            if tdx is not None:
                id33[tdx.index].append(i)
        id16 = collections.defaultdict(list)
        for j, f in enumerate(own16):
            k = self.d.type_kind(f.ctype)
            if k[1] is not None and k[0] in ("class", "enum", "vstruct"):
                id16[k[1].index].append(j)
        for idx, js in id16.items():
            is_ = id33.get(idx, [])
            if len(js) == 1 and len(is_) == 1 and js[0] not in forced:
                forced[js[0]] = (is_[0], "unique identity")
        # (c) readable 1.6 field names equal to a header field name (BeeByte left them alone in both versions)
        def canon(n):
            m = re.match(r'^<(.+)>k__BackingField$', n)
            if m:
                return "_" + m.group(1) + "_k__BackingField"
            return n
        hdr_names = {}
        for i, (c, n) in enumerate(fields33):
            hdr_names.setdefault(canon(n), []).append(i)
        for j, f in enumerate(own16):
            if j in forced:
                continue
            cn = canon(f.name)
            core = re.sub(r'^_(.+)_k__BackingField$', r'\1', cn)
            if is_obf(core):
                continue
            is_ = hdr_names.get(cn, [])
            if len(is_) == 1 and is_[0] not in {i for i, h in forced.values()}:
                forced[j] = (is_[0], "readable name")
        if not forced:
            return name_of
        # apply: forced pairs win; any other alignment using the same i or j is dropped
        used_i = {i for i, how in forced.values()}
        out = {j: i for j, i in name_of.items() if j not in forced and i not in used_i}
        for j, (i, how) in forced.items():
            out[j] = i
            if name_of.get(j) != i:
                self.anchored.append((name, fields33[i][1], own16[j].name, how))
        return out

    def ctype(self, cs):
        ctext, deps = super().ctype(cs)
        return re.sub(r'(__Enum_\d+)__Enum\b', r'\1', ctext), deps

    def transfer_type(self, ctext, f, ctype33):
        r = super().transfer_type(ctext, f, ctype33)
        if r[0] != ctext:
            return r
        k33 = gen_types.header_ctype_kind(ctype33)
        k16 = self.d.type_kind(f.ctype)
        if ctext in ("intptr_t", "uintptr_t") and ctype33.replace(" ", "") == "void*":
            return "void*", f"  // {ctext} in the 1.6 dump", []
        if k33[0] == "vstruct" and k16[0] == "vstruct" and ctext.startswith("struct ") and k33[1] and k33[1] != ctext[len("struct "):] \
                and k16[1] is not None and k16[1].obfuscated and k33[1] in self.plain_fields:
            # an obfuscated 1.6 value struct whose shape equals the appdata-28 plain struct: use that name (generated from the
            # 1.6 dump under the header name when not defined yet; the copied appdata-28 definition is layout-identical otherwise)
            REF = {"class", "ptr", "list", "dict", "delegate", "generic", "array", "rep", "map", "bytes", "object", "voidptr"}
            ht = [("ref" if t in REF else t) for t in (gen_types.Gen.tok33(c) for c, n in self.plain_fields[k33[1]])]
            dt = [("ref" if t in REF else t) for t in (self.tok28(f2.ctype) for f2 in sorted(k16[1].instance_fields(), key=lambda x: (x.offset is None, x.offset or 0)))]
            if ht == dt:
                if k33[1] not in self.done and not self.exists(k33[1]):
                    self.reverse[k16[1].index] = k33[1]
                    self.classmap[k33[1]] = k16[1]
                    self.ensure(k33[1], True, k16[1])
                if k33[1] in self.done or self.exists(k33[1]):
                    return f"struct {k33[1]}", f"  // type {ctext[len('struct '):]} in the 1.6 dump (same shape as appdata-28 {k33[1]})", [(k33[1], True, k16[1])]
        if k33[0] == "vstruct" and k16[0] == "vstruct" and ctext.startswith("struct ") and k33[1] and k33[1] != ctext[len("struct "):]:
            cur = ctext[len("struct "):]
            if k33[1] in self.done or self.exists(k33[1]) or k33[1] in self.h33.structs:
                self.ensure(k33[1], True)
                s16 = self.struct_size(cur)[0] if (cur in self.done or self.exists(cur)) else None
                s33 = self.struct_size(k33[1])[0] if (k33[1] in self.done or self.exists(k33[1])) else None
                if s16 is not None and s33 is not None and s16 == s33:
                    return f"struct {k33[1]}", f"  // type {cur} in the 1.6 dump (same size)", [(k33[1], True, None)]
        return r

    LABELS = (("2.8 dump ", "1.6 dump "), ("2.8 dump\n", "1.6 dump\n"), ("appdata-33 ", "appdata-28 "), ("appdata-33\n", "appdata-28\n"),
              ("from appdata-33", "from appdata-28"), ("// 2.8: ", "// 2.8 name: "), ("absent in 2.8", "absent in 1.6"),
              ("the 2.8 type", "the 1.6 type"), ("not located in the 2.8 dump", "not matched in the 1.6 dump"),
              ("NOT located in the 2.8 dump", "NOT matched in the 1.6 dump"), ("found in the 2.8 dump", "found in the 1.6 dump"),
              ("2.8 obfuscated", "1.6 obfuscated"))

    def emit(self, e):
        e.text = re.sub(r'(__Enum_\d+)__Enum\b', r'\1', e.text)
        # vtable slot names resolved by the matcher (the 2.8 header's named slots -> the 1.6 slot of the matched method)
        if e.name.endswith("__Class"):
            base = e.name[:-len("__Class")]
            vt = self.vtables.get(base) or self.vtables.get("MoleMole_" + base) or (self.vtables.get(base[len("MoleMole_"):]) if base.startswith("MoleMole_") else None)
            if vt:
                names = {int(k): v for k, v in vt["names"].items()}
                def renumber(m):
                    body = m.group(2)
                    lines = body.split("\n")
                    k = 0
                    out = []
                    for ln in lines:
                        mm = re.match(r'^(\s*VirtualInvokeData\s+)([^;]+);(.*)$', ln)
                        if mm:
                            if k in names:
                                ln = f"{mm.group(1)}{names[k][0]};  // 1.6 slot {k}: {names[k][1]} (was {mm.group(2).strip()})"
                            k += 1
                        out.append(ln)
                    return m.group(1) + "\n".join(out) + m.group(3)
                e.text = re.sub(r'(struct\s+' + re.escape(base) + r'__VTable\s*\{\n)(.*?)(\n\s*\};)', renumber, e.text, count=1, flags=re.S)
        # vtable slot names must be identifiers (explicit interface implementations carry dots, generics carry <>)
        e.text = re.sub(r'(VirtualInvokeData\s+)([^;\n]+?)(;)', lambda m: m.group(1) + re.sub(r'[^A-Za-z0-9_]', '_', m.group(2).strip()) + m.group(3), e.text)
        # backing fields of obfuscated properties keep the dump spelling <X>k__BackingField: Inspector spelling is _X_k__BackingField
        e.text = re.sub(r'<([A-Za-z_]\w*)>k__BackingField', r'_\1_k__BackingField', e.text)
        e.text = e.text.replace("  // 2.8: ", "  // 1.6 dump name: ")
        # duplicate member names inside one struct body (a misaligned name) -> keep the first, suffix the others
        def dedupe(m):
            body = m.group(2)
            seen = collections.Counter()
            out = []
            for ln in body.split("\n"):
                mm = re.match(r'^(\s*(?:const\s+)?(?:struct\s+|enum\s+)?[A-Za-z_][\w:<>,\* ]*?[\*\s]\s*)([A-Za-z_]\w*)(\s*(?:\[\d+\])?;.*)$', ln)
                if mm and not ln.strip().startswith("//"):
                    nm = mm.group(2)
                    seen[nm] += 1
                    if seen[nm] > 1:
                        ln = f"{mm.group(1)}{nm}_dup{seen[nm] - 1}{mm.group(3)}  // DUPLICATE member name after alignment - renamed"
                out.append(ln)
            return m.group(1) + "\n".join(out) + m.group(3)
        e.text = re.sub(r'(struct\s+(?:__declspec\(align\(\d+\)\)\s+)?[A-Za-z_]\w*\s*\{\n)(.*?)(\n\s*\};)', dedupe, e.text, flags=re.S)
        for a, b in self.LABELS:
            e.text = e.text.replace(a, b)
            e.how = e.how.replace(a, b)
        if "FALLBACK" in e.how:
            e.text = e.text.replace("FALLBACK appdata-28", f"FALLBACK appdata-28 {UNVERIFIED}", 1)
            e.how = e.how.replace("FALLBACK appdata-28", f"FALLBACK appdata-28 {UNVERIFIED}", 1)
            self.copied.append((e.name, e.how))
        elif e.name not in self.done:
            self.regen.append((e.name, e.how))
        super().emit(e)

    def ensure(self, name, complete=True, td=None, depth=0):
        if name in BUILTIN_NAMES or name in self.done or self.exists(name):
            return
        mm = re.match(r'^Nullable_1_(.+)_$', name)
        if mm and complete:
            self.gen_nullable(name, mm.group(1), depth)
            return
        if td is None and name not in self.classmap and not self.d.keys.get(norm(name)):
            idx = next((i for i, n in self.reverse.items() if n == name), None)
            if idx is not None:
                td = self.model.by_index.get(idx)
            else:
                ln = self.loose(name)
                hits = [t for k, t in self.classmap.items() if self.loose(k) == ln]
                if len({t.index for t in hits}) == 1:
                    td = hits[0]
        # BCL generic instantiations: same mscorlib in both clients -> the appdata-28 definition is the layout
        if BCL_GENERIC.match(name):
            if complete:
                self.copy_family(name, "BCL generic instantiation (same mscorlib in 1.6 and 2.8; layout version-independent)", flag=False, depth=depth)
            else:
                self.forward.add(name)
            return
        return super().ensure(name, complete, td, depth)

    @staticmethod
    def tok33(ctype):
        if re.search(r'__Enum_\d+$', ctype.replace("struct ", "").strip()):
            return "enum"
        return Gen.tok33(ctype)

    def gen_struct_33(self, name, depth):
        """unmatched class: opaque when nothing dereferences it, else the 2.8 layout flagged"""
        if name not in self.deref and name not in self.byvalue and name + "__Fields" not in self.byvalue:
            self.forward.add(name)
            if name not in [n for n, _ in self.opaque]:
                self.opaque.append((name, "not matched in the 1.6 dump; no feature code dereferences it -> forward declaration only"))
            self.say(f"  {name}: unmatched, not dereferenced -> opaque forward declaration")
            return
        self.copy_family(name, f"NOT matched in the 1.6 dump; the feature code dereferences it - {UNVERIFIED}, do not rely on this layout", flag=True, depth=depth)

    def copy_family(self, name, why, flag, depth):
        """copy every appdata-28 definition of the family (X__Fields, X, X__VTable, X__StaticFields, X__Class, X__Boxed)"""
        chunks = []
        for suffix in ("__VTable", "__StaticFields", "__Fields", "", "__Class", "__Boxed"):
            m = re.search(r'^(\s*struct\s+(?:__declspec\(align\(\d+\)\)\s+)?' + re.escape(name + suffix) + r'\s*\{.*?^\s*\};)', self.h33.text, re.S | re.M)
            if m:
                chunks.append(m.group(1))
        if not chunks:
            self.forward.add(name)
            return
        text = "\n".join(chunks)
        deps = set(re.findall(r'struct\s+([A-Za-z_]\w*)', text)) | set(re.findall(r'\b([A-Za-z_]\w*)__Enum\b', text))
        for dep in sorted(deps):
            if dep in (name, name + "__Fields", name + "__Class", name + "__VTable", name + "__StaticFields", name + "__Boxed") or dep.startswith("__declspec"):
                continue
            if dep.endswith("__Fields"):
                self.ensure(dep[:-len("__Fields")], True, None, depth + 1)
            elif dep.endswith("__Class"):
                self.forward.add(dep)
            elif dep.endswith("__Array"):
                self.ensure(dep, True, None, depth + 1)
            else:
                val = re.search(r'struct\s+' + re.escape(dep) + r'\s+[A-Za-z_]\w*(\[\d+\])?\s*;', text) is not None or (dep + "__Enum") in text
                self.ensure(dep, bool(val), None, depth + 1)
        if not any(c.lstrip().startswith(f"struct {name}__Class") for c in chunks):
            self.forward.add(name + "__Class")
        how = ("copied from appdata-28: " + why) if not flag else ("FALLBACK appdata-28 " + UNVERIFIED + ": " + why)
        e = Emitted(name, f"    // {name}: {how}\n" + text.rstrip() + "\n", how, kind="struct")
        self.copied.append((name, how))
        self.done[e.name] = e
        for c in chunks:
            m = re.match(r'\s*struct\s+(?:__declspec\(align\(\d+\)\)\s+)?([A-Za-z_]\w*)', c)
            if m and m.group(1) != name:
                self.done.setdefault(m.group(1), e)
        self.out.append(e)
        self.forward.discard(e.name)

    def gen_nullable(self, name, arg, depth):
        """System.Nullable<T>: { T value; bool has_value; } (mscorlib layout, version-independent)"""
        prim = gen_types.ELEM_PRIM_C.get(gen_types.ELEM_SHORT.get(arg, arg))
        if prim:
            vt = prim
        else:
            self.ensure(arg, True)
            e = self.done.get(arg)
            if (e is not None and e.kind == "enum") or arg in self.hcur.enums or (arg not in self.done and (arg in self.h33.enums)):
                vt = arg + "__Enum"
            elif e is None and not self.exists(arg):
                # the argument type could not be generated: keep the size right with the appdata-28 spelling when it exists
                alt = next((c for c in (arg, arg[len("MoleMole_"):] if arg.startswith("MoleMole_") else "MoleMole_" + arg) if c in self.h33.structs or c in self.h33.enums), None)
                if alt and alt in self.h33.enums:
                    self.ensure(alt, True)
                    vt = alt + "__Enum"
                elif alt:
                    self.ensure(alt, True)
                    vt = f"struct {alt}"
                else:
                    vt = f"struct {arg}"
            else:
                vt = f"struct {arg}"
        txt = f"    struct {name} {{\n        {vt} value;\n        bool has_value;\n    }};\n"
        # Relic: record the size the compiler will give `{ T value; bool has_value; }` - round_up(sizeof(T) + 1,
        # alignof(T)). Without it struct_size() answers 0 for every Nullable member and the generator pads
        # the whole gap after it (547 shifted fields on 1.6). An unknown T is never treated as empty.
        vsize, valign = self.csize(vt)
        if vsize == 0:
            vsize, valign = 8, 8
        nsize = (vsize + 1 + valign - 1) // valign * valign
        e = Emitted(name, f"    // {name}: System.Nullable<{arg}> (mscorlib layout)\n" + txt, "Nullable<T> standard layout",
                    size=nsize, align=valign, kind="vstruct")
        self.done[e.name] = e
        self.out.append(e)
        self.forward.discard(e.name)

    def ensure_boxed(self, name):
        """X__Boxed = the boxed value type (klass, monitor, X fields) - emitted after X"""
        base = name[:-len("__Boxed")]
        if name in self.done or self.exists(name):
            return
        self.ensure(base, True)
        if base in self.done or self.exists(base):
            txt = "    struct %s {\n        struct %s__Class* klass;\n        MonitorData* monitor;\n        struct %s fields;\n    };\n" % (name, base, base)
            self.forward.add(base + "__Class")
            e = Emitted(name, "    // %s: boxed %s (Il2CppInspector shape)\n" % (name, base) + txt, "boxed value type")
            self.done[e.name] = e
            self.out.append(e)
            self.forward.discard(e.name)
        else:
            self.forward.add(name)

    def gen_enum(self, name, td):
        """1.6 enum + every appdata-28 member name that names an existing value but spells it differently (alias members)"""
        super().gen_enum(name, td)
        e = self.done.get(name)
        h33 = self.h33.enums.get(name) or self.h33.enums.get(name[len("MoleMole_"):] if name.startswith("MoleMole_") else "MoleMole_" + name) or self.h33.enums_raw.get(name)
        if e is None or not h33:
            return
        have = {m.group(1): int(m.group(2), 16) for m in re.finditer(r'^\s+([A-Za-z_]\w*)\s*=\s*(0x[0-9A-Fa-f]+),', e.text, re.M)}
        by_val = {}
        for k, v in have.items():
            by_val.setdefault(v & 0xFFFFFFFF, k)
        extra = []
        for k, v in h33.items():
            if k in have:
                continue
            if (v & 0xFFFFFFFF) in by_val:
                extra.append(f"        {k} = 0x{v & 0xFFFFFFFF:08X},  // alias: appdata-28 spelling of {by_val[v & 0xFFFFFFFF]}")
        if extra:
            e.text = re.sub(r'\n(\s*\};)', "\n" + "\n".join(extra) + r"\n\1", e.text, count=1)
            self.missing = [x for x in self.missing if not (x[0] == name + "__Enum" and all(k in x[1].split(", ") for k in h33 if k not in have and (h33[k] & 0xFFFFFFFF) in by_val) and len(x[1].split(", ")) == len([k for k in h33 if k not in have and (h33[k] & 0xFFFFFFFF) in by_val]))]

    def gen_enum_33(self, name):
        m = re.search(r'^(\s*enum class\s+' + re.escape(name) + r'__Enum\s*:\s*\w+\s*\{.*?\};)', self.h33.text, re.S | re.M)
        if not m:
            self.forward.add(name)
            return
        how = f"FALLBACK appdata-28 {UNVERIFIED}: enum not matched in the 1.6 dump; values unverified"
        e = Emitted(name, f"    // {name}__Enum: {how}\n" + m.group(1).rstrip() + "\n", how, size=4, align=4, kind="enum")
        self.copied.append((name + "__Enum", how))
        self.done[e.name] = e
        self.out.append(e)
        self.forward.discard(e.name)


def universe_names(merged_text):
    """ordered base names of every definition in the 2.8 app namespace (types.h after the basics + relic.h)"""
    out = collections.OrderedDict()
    for m in re.finditer(r'^\s*(struct|enum class|enum|union)\s+(?:__declspec\(align\(\d+\)\)\s+)?([A-Za-z_]\w*)\s*(?::\s*\w+\s*)?([{;])', merged_text, re.M):
        if m.group(3) == ";":
            continue
        name = m.group(2)
        mm = SUFFIX_RE.match(name)
        base, suf = (mm.group(1), mm.group(2)) if mm else (name, "")
        out.setdefault(base, set()).add(suf or "(self)")
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dump16", help="1.6 dump dir (default dumps_ref/1.6/greenxemotion)")
    ap.add_argument("--build-log", help="MSBuild log to harvest undeclared identifiers / undefined types from")
    ap.add_argument("--fresh", action="store_true", help="do not carry over the requests recorded in the existing generated header")
    ap.add_argument("--verbose", "-v", action="store_true")
    a = ap.parse_args()
    if not os.path.exists(MATCH):
        raise SystemExit(f"{MATCH} missing - run tools/matcher16.py first")
    match = json.load(open(MATCH, encoding="utf-8"))
    dump16 = a.dump16 or DEFAULT_DUMP_DIR["16"]
    os.makedirs(A16, exist_ok=True)
    # 1-5
    gen_functions(match)
    gen_types_ptr(match)
    gen_api(match)
    gen_unity(match)
    gen_metadata_version()
    gen_types_h()
    # overrides skeleton
    ov = os.path.join(HERE, "offsets-16.overrides.json")
    if not os.path.exists(ov):
        write(ov, json.dumps({"_comment": "Game 1.6 (OS) hand decisions for symbols tools/matcher16.py left at 0x0 or matched with low confidence "
                                          "(see dumps_ref/1.6/unresolved.txt and REPORT-matching.md). Entries: {\"NAME\": {\"rva\": \"0x...\", \"note\": \"why / how verified\"}}. "
                                          "Applied by tools/apply_overrides.py --ver 16 onto appdata-16/il2cpp-functions.h, il2cpp-types-ptr.h and il2cpp-unityplayer-functions.h "
                                          "(only lines whose offset is 0x0). Re-run after gen_appdata16.py, which rewrites those headers from match16.json."},
                             indent=2) + "\n")
        print(f"offsets-16.overrides.json: skeleton written ({ov})")
    # 6: types
    merged = os.path.join(OUT, "appdata28-types-merged.h")
    t28 = read(os.path.join(A28, "il2cpp-types.h"))
    i_ns = t28.find("namespace app {")
    write(merged, t28[i_ns:] + "\n" + read(os.path.join(A28, "il2cpp-types-relic.h")))
    names = universe_names(read(merged))
    fields_used, names_used, members_used = feature_usage()
    h28 = HeaderTypes(merged)
    deref = set()
    for sname, flds in h28.fields.items():
        own = [n for c, n in flds if n != "_"]
        if any(n in fields_used for n in own):
            deref.add(sname)
    # plain value structs (no __Fields): dereferenced when one of their member names is accessed anywhere in the feature code
    for m in re.finditer(r'^\s*struct\s+(?:__declspec\(align\(\d+\)\)\s+)?([A-Za-z_]\w*)\s*\{(.*?)^\s*\};', read(merged), re.S | re.M):
        n, body = m.group(1), m.group(2)
        if SUFFIX_RE.match(n) or "MonitorData* monitor;" in body or "VirtualInvokeData" in body:
            continue
        mem = re.findall(r'^\s*(?:const\s+)?.+?\s+([A-Za-z_]\w*)(?:\[\d+\])?;', body, re.M)
        if n in names_used and any(x in members_used for x in mem):
            deref.add(n)
    # structs used by value anywhere in the 2.8 header text or the DO_ lines
    mtext = read(merged)
    byvalue = set()
    for tname, member in re.findall(r'struct\s+([A-Za-z_]\w*)\s+([A-Za-z_]\w*)(?:\[\d+\])?\s*;', mtext):
        if member in ("fields", "_") or tname.endswith("__Class") or tname.endswith("__VTable"):
            continue
        byvalue.add(tname)
    byvalue |= {n[:-len("__Fields")] for n in re.findall(r'struct\s+([A-Za-z_]\w*__Fields)\s+_\s*;', mtext)}
    for fn in ("il2cpp-functions.h",):
        for ln in read(os.path.join(A28, fn)).splitlines():
            for t in re.findall(r'\b([A-Za-z_]\w*)\s+[A-Za-z_]\w*\s*[,)]', ln):
                byvalue.add(t)
    anchors_path = os.path.join(OUT, "fieldanchors.json")
    anchors = json.load(open(anchors_path, encoding="utf-8")) if os.path.exists(anchors_path) else {}
    m28 = load("28", quiet=True)
    g = Gen16(dump16, merged, deref, byvalue, anchors, m28, match.get("vtables", {}), a.verbose)
    complete = [n for n in names if n not in BASICS]
    # keep every appdata-28 family member request (X__Class / X__Boxed / X__Array are requested through their suffixes)
    requests = []
    for base, sufs in names.items():
        if base in BASICS:
            continue
        requests.append(base)
        for suf in sorted(sufs):
            if suf in ("__Class", "__Array", "__Boxed"):
                requests.append(base + suf)
    declared = []
    for m in re.finditer(r'^\s*struct\s+([A-Za-z_]\w*)\s*;', read(merged), re.M):
        if m.group(1) not in names and m.group(1) not in BASICS:
            declared.append(m.group(1))
    log_missing = []
    if a.build_log:
        c2, d2, log_missing = g.auto_requests(a.build_log)
        requests += [x for x in c2 if x not in requests]
        declared += d2
    prev = os.path.join(A16, "il2cpp-types-relic.h")
    if os.path.exists(prev) and not a.fresh:
        head = read(prev)[:40000]
        m = re.search(r'^// requested complete: (.*)$', head, re.M)
        if m:
            requests += [x.strip() for x in m.group(1).split(",") if x.strip() and x.strip() not in requests]
        m = re.search(r'^// requested declared: (.*)$', head, re.M)
        if m:
            declared += [x.strip() for x in m.group(1).split(",") if x.strip()]
    seen = set()
    requests = [x for x in requests if not (x in seen or seen.add(x))]
    declared = sorted({d for d in declared if d not in seen})
    for name in requests:
        if name.endswith("__Boxed"):
            g.ensure_boxed(name)
        else:
            g.ensure(name, True)
    for name in declared:
        g.ensure(name, False)
    # write the generated header (same shape as gen_types.run, 1.6 wording)
    out_path = os.path.join(A16, "il2cpp-types-relic.h")
    lines = [
        "// generated by tools/gen_appdata16.py from the 1.6 dump - every application type the appdata-28 headers define, with 1.6 layouts",
        "// regenerate: python tools/gen_appdata16.py [--build-log <msbuild log>]   (after matcher16.py; do not edit by hand)",
        f"// requested complete: {', '.join(requests)}",
        f"// requested declared: {', '.join(declared)}",
        "// provenance per type: \"1.6 dump <class>\" (fields/offsets from the 1.6 dump, names aligned with appdata-28 where readable),",
        f"//   \"FALLBACK appdata-28 {UNVERIFIED}\" (NOT matched in the 1.6 dump: the 2.8 layout, compile aid only - never rely on it on 1.6),",
        "//   \"copied from appdata-28: BCL generic\" (mscorlib generic instantiations, version-independent),",
        "//   forward declarations for pointer-only use and for unmatched classes nothing dereferences. MISSING = a 2.8/3.3 field the 1.6 type does not have.",
        "#pragma once",
        "",
        "#if !defined(_GHIDRA_) && !defined(_IDA_)",
        "namespace app {",
        "#endif",
        "",
        "    // ---- forward declarations (pointer-only use / opaque unmatched types) ----",
    ]
    fwd = sorted(n for n in g.forward if n not in g.done and not g.exists(n) and n not in BUILTIN_NAMES)
    for n in fwd:
        lines.append(f"    struct {n};")
    lines.append("")
    lines.append("    // ---- definitions (dependency order) ----")
    for e in g.out:
        lines.append(e.text.rstrip("\n"))
        lines.append("")
    if g.missing or log_missing:
        lines.append("    // ---- MISSING in 1.6 (present in appdata-28; feature code reading them needs #if RELIC_GAME_VERSION >= 28) ----")
        for s_, f_, t_ in g.missing:
            lines.append(f"    //   {s_}: {f_} ({t_})")
        for s_, f_ in log_missing:
            lines.append(f"    //   {s_}: {f_} (from the build log)")
        lines.append("")
    lines += ["#if !defined(_GHIDRA_) && !defined(_IDA_)", "}", "#endif", ""]
    write(out_path, "\n".join(lines))
    # report data
    rep = {"regenerated": g.regen, "copied": g.copied, "opaque": g.opaque, "forward": fwd, "anchored": g.anchored,
           "missing": [list(x) for x in g.missing] + [[s_, f_, "build log"] for s_, f_ in log_missing],
           "deref_structs": sorted(deref), "requests": requests, "declared": declared,
           "copied_dereferenced": sorted(n for n, how in g.copied if (n in deref or n + "__Fields" in deref) and UNVERIFIED in how)}
    with open(os.path.join(OUT, "gen16.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(rep, f, indent=1)
    print(f"il2cpp-types-relic.h: {len(g.out)} definitions ({len(g.regen)} regenerated from the 1.6 dump, {len(g.copied)} copied from appdata-28 "
          f"[{sum(1 for n, h in g.copied if UNVERIFIED in h)} flagged {UNVERIFIED}], {len(g.opaque)} opaque unmatched), {len(fwd)} forward declarations; "
          f"{len(g.missing)} MISSING field reports -> {out_path}")
    print(f"   {len(g.anchored)} field alignments forced by getter anchors / unique identities")
    if g.missing:
        print("   MISSING (2.8/3.3 fields the 1.6 type lacks):")
        for s_, f_, t_ in g.missing[:60]:
            print(f"     {s_}.{f_} ({t_})")
    if a.verbose and g.notes:
        print("\n".join(g.notes))


if __name__ == "__main__":
    main()
