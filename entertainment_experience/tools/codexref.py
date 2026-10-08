#!/usr/bin/env python3
"""Code-level cross references of one game build's UserAssembly.dll, as fingerprint material for the
2.8 -> 1.6 structural matcher (tools/matcher16.py). Stdlib only; cached as dumps_ref/<ver>/xrefs.pkl.

    from codexref import CodeXref
    x = CodeXref.load("28", dll_path, script_json_path)   # first run scans the DLL (~10 s), later runs read the cache
    x.strings_of(rva)   -> [literal value, ...]   string literals the method loads (metadata-usage slots)
    x.meta_of(rva)      -> [("TypeInfo"|"MethodInfo"|"Field"|..., name), ...]
    x.calls_of(rva)     -> [target rva, ...]      direct E8/E9 targets that are managed method starts
    x.size_of(rva)      -> bytes to the next function start
    x.exports           -> {name: rva}             PE export table (il2cpp_* API)

How: the PE is parsed with struct (sections, export directory); every executable section is scanned with a few
byte regexes for RIP-relative memory operands (`[REX] opcode ModRM(mod=00, rm=101) disp32` - mov/lea/cmp/test/
and the grp1/grp11 forms with an immediate) and for E8/E9 rel32. A hit is kept only when its target is a
metadata-usage slot listed in script.json (ScriptString / ScriptMetadata / ScriptMetadataMethod) or a managed
function start (script.json Addresses[]). Sites are attributed to the enclosing function by bisecting the sorted
function starts. Without a full x86-64 decoder a few false positives are possible, but they have to land exactly
on a known slot, which makes them rare and harmless for fingerprinting.

Address math: script.json addresses are RVAs (Il2CppDumper GetRVA = VA - ImageBase); section VirtualAddress
values are RVAs too, so site RVA = section VA + offset in the section data. Both builds have ImageBase
0x180000000 (2.8: code in `.text` + `il2cpp`; 1.6: code in `.text`).
"""
import bisect, json, os, pickle, re, struct, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
EE = os.path.normpath(os.path.join(HERE, ".."))
DUMPS = os.path.join(EE, "dumps_ref")

# default inputs of the two builds this tree targets (override with --dll / --script on the command line of the tools)
DEFAULT_DLL = {
    "28": r"D:\relic\Genshin 2.8\GenshinImpact_Data\Native\UserAssembly.dll",
    "16": r"D:\relic\1.6_game\GenshinImpact_Data\Native\UserAssembly.dll",
}
DEFAULT_UNITY = {
    "28": r"D:\relic\Genshin 2.8\UnityPlayer.dll",
    "16": r"D:\relic\1.6_game\UnityPlayer.dll",
}
DEFAULT_DUMP_DIR = {
    "28": os.path.join(DUMPS, "2.8"),
    "16": os.path.join(DUMPS, "1.6", "greenxemotion"),
}
CACHE_DIR = {"28": os.path.join(DUMPS, "2.8"), "16": os.path.join(DUMPS, "1.6")}

IMAGE_SCN_MEM_EXECUTE = 0x20000000


class PE:
    """minimal x64 PE reader: sections, image base, export table"""
    def __init__(self, path):
        self.path = path
        with open(path, "rb") as f:
            self.b = f.read()
        b = self.b
        if b[:2] != b"MZ":
            raise ValueError(f"{path}: not a PE file")
        pe = struct.unpack_from("<I", b, 0x3C)[0]
        if b[pe:pe + 4] != b"PE\0\0":
            raise ValueError(f"{path}: bad PE signature")
        machine, nsec, tstamp, _, _, opt_size, _ = struct.unpack_from("<HHIIIHH", b, pe + 4)
        opt = pe + 24
        magic = struct.unpack_from("<H", b, opt)[0]
        self.is64 = magic == 0x20B
        self.timestamp = tstamp
        self.image_base = struct.unpack_from("<Q" if self.is64 else "<I", b, opt + 24)[0]
        dd_off = opt + (112 if self.is64 else 96)
        self.dirs = [struct.unpack_from("<II", b, dd_off + 8 * i) for i in range(16)]
        sec_off = opt + opt_size
        self.sections = []
        for i in range(nsec):
            s = sec_off + 40 * i
            name = b[s:s + 8].rstrip(b"\0").decode("ascii", "replace")
            vsize, vaddr, rsize, raw, _, _, _, _, chars = struct.unpack_from("<IIIIIIHHI", b, s + 8)
            self.sections.append((name, vsize, vaddr, rsize, raw, chars))

    def rva_to_off(self, rva):
        for name, vsize, vaddr, rsize, raw, chars in self.sections:
            if vaddr <= rva < vaddr + max(vsize, rsize):
                return rva - vaddr + raw
        return None

    def read(self, rva, n):
        off = self.rva_to_off(rva)
        if off is None:
            return b""
        return self.b[off:off + n]

    def section_of(self, rva):
        for s in self.sections:
            if s[2] <= rva < s[2] + max(s[1], s[3]):
                return s
        return None

    def code_sections(self):
        return [s for s in self.sections if s[5] & IMAGE_SCN_MEM_EXECUTE]

    def exports(self):
        """{name: rva} (forwarders skipped)"""
        rva, size = self.dirs[0]
        if not rva:
            return {}
        off = self.rva_to_off(rva)
        (chars, tstamp, maj, mino, name_rva, base, n_funcs, n_names, addr_funcs, addr_names, addr_ords) = struct.unpack_from("<IIHHIIIIIII", self.b, off)
        funcs = struct.unpack_from(f"<{n_funcs}I", self.b, self.rva_to_off(addr_funcs))
        names = struct.unpack_from(f"<{n_names}I", self.b, self.rva_to_off(addr_names))
        ords = struct.unpack_from(f"<{n_names}H", self.b, self.rva_to_off(addr_ords))
        out = {}
        for nrva, o in zip(names, ords):
            noff = self.rva_to_off(nrva)
            end = self.b.find(b"\0", noff, noff + 512)
            name = self.b[noff:end].decode("ascii", "replace")
            frva = funcs[o]
            if rva <= frva < rva + size:
                continue  # forwarder
            out[name] = frva
        return out


# ---------------------------------------------------------------- script.json (regex scan, json.load is too heavy)
PAIR_VALUE = re.compile(r'"Address":\s*(\d+),\s*"Value":\s*"((?:[^"\\]|\\.)*)"')
PAIR_NAME = re.compile(r'"Address":\s*(\d+),\s*"Name":\s*"((?:[^"\\]|\\.)*)"')
TRIPLE_GENERIC = re.compile(r'"Address":\s*(\d+),\s*"Name":\s*"((?:[^"\\]|\\.)*<(?:[^"\\]|\\.)*)",\s*"Signature":\s*"((?:[^"\\]|\\.)*)"')


def parse_script_slots(path):
    """-> (strings {slot rva: value}, metadata {slot rva: name}, metadata_methods {slot rva: name}, sorted function starts,
    generic bodies {rva: parameter count without __this/MethodInfo})"""
    with open(path, "r", encoding="utf-8") as f:
        s = f.read()
    i_str = s.find('"ScriptString": [')
    gen_np = {}
    for a, n, sig in TRIPLE_GENERIC.findall(s[:i_str]):
        params = sig[sig.find("(") + 1:sig.rfind(")")]
        parts = [x.strip() for x in params.split(",") if x.strip()]
        parts = [x for x in parts if not x.endswith("__this") and not x.endswith("* method")]
        gen_np[int(a)] = len(parts)
    i_meta = s.find('"ScriptMetadata": [')
    i_mm = s.find('"ScriptMetadataMethod": [')
    i_addr = s.find('"Addresses": [')
    strings = {int(a): json.loads('"' + v + '"') for a, v in PAIR_VALUE.findall(s[i_str:i_meta])}
    meta = {int(a): json.loads('"' + n + '"') for a, n in PAIR_NAME.findall(s[i_meta:i_mm])}
    mm = {int(a): json.loads('"' + n + '"') for a, n in PAIR_NAME.findall(s[i_mm:i_addr])}
    addrs = sorted(set(int(x) for x in re.findall(r'\d+', s[i_addr + len('"Addresses": ['):])))
    return strings, meta, mm, addrs, gen_np


# ---------------------------------------------------------------- the scanner
MODRM_RIP = b'[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]'
# opcode byte classes: mov/lea/cmp/test/arith r,m forms + grp1 (80/81/83) + grp11 (C6/C7) + FF (call/jmp/inc m) + 63 (movsxd)
OPCODES = (b'[\x8b\x8d\x89\x3b\x39\x63\x85\x84\x88\x8a\x86\x87\x0b\x09\x23\x21\x33\x31\x2b\x29\x03\x01\x13\x11\x1b\x19\x3a\x38'
           b'\xff\x80\x81\x83\xc6\xc7]')
PAT_RIP = re.compile(rb'(?:[\x40-\x4f])?' + OPCODES + MODRM_RIP + rb'(....)', re.S)
PAT_RIP_0F = re.compile(rb'(?:[\x66\xf2\xf3])?(?:[\x40-\x4f])?\x0f[\x10\x11\x28\x29\x2e\x2f\xb6\xb7\xbe\xbf\xaf\x6e\x7e\xd6\x57\x58\x59\x5c\x5e\x2a\x2c\x2d\x5a\x51]' + MODRM_RIP + rb'(....)', re.S)
PAT_CALL = re.compile(rb'[\xe8\xe9](....)', re.S)
IMM_AFTER = {0x80: 1, 0x83: 1, 0xC6: 1, 0x81: 4, 0xC7: 4}


class CodeXref:
    def __init__(self, ver):
        self.ver = ver
        self.image_base = None
        self.sections = []
        self.exports = {}
        self.strings = {}      # slot rva -> literal
        self.meta = {}         # slot rva -> metadata name (X_TypeInfo, ...)
        self.metamethod = {}   # slot rva -> Method$... name
        self.addrs = []        # sorted function starts (RVA)
        self.gen_nparams = {}  # rva of a generic instantiation body -> parameter count
        self.refs = {}         # func start -> [(kind, slot rva)]   kind: 's' string, 'm' metadata, 'mi' metadata method
        self.calls = {}        # func start -> [target rva]
        self.dll_path = None
        self.script_path = None

    # ---- queries
    def func_of(self, rva):
        i = bisect.bisect_right(self.addrs, rva) - 1
        return self.addrs[i] if i >= 0 else None

    def size_of(self, rva):
        i = bisect.bisect_left(self.addrs, rva)
        if i < len(self.addrs) and self.addrs[i] == rva:
            return (self.addrs[i + 1] - rva) if i + 1 < len(self.addrs) else 0
        return 0

    def strings_of(self, rva):
        return [self.strings[t] for k, t in self.refs.get(rva, ()) if k == "s"]

    def meta_of(self, rva):
        out = []
        for k, t in self.refs.get(rva, ()):
            if k == "m":
                n = self.meta[t]
                kind = "TypeInfo" if n.endswith("_TypeInfo") else "Meta"
                out.append((kind, n))
            elif k == "mi":
                out.append(("MethodInfo", self.metamethod[t]))
        return out

    def calls_of(self, rva):
        return list(self.calls.get(rva, ()))

    # ---- build / cache
    @classmethod
    def load(cls, ver, dll_path=None, script_path=None, cache_dir=None, quiet=False, force=False):
        ver = str(ver)
        dll_path = dll_path or DEFAULT_DLL.get(ver)
        script_path = script_path or os.path.join(DEFAULT_DUMP_DIR.get(ver, os.path.join(DUMPS, ver)), "script.json")
        cache_dir = cache_dir or CACHE_DIR.get(ver, os.path.join(DUMPS, ver))
        cache = os.path.join(cache_dir, "xrefs.pkl")
        if os.path.exists(cache) and not force:
            try:
                with open(cache, "rb") as f:
                    x = pickle.load(f)
                if x.ver == ver and (not os.path.exists(script_path) or os.path.getmtime(cache) >= os.path.getmtime(script_path)):
                    return x
            except Exception:
                pass
        if not dll_path or not os.path.exists(dll_path):
            raise SystemExit(f"UserAssembly.dll for {ver} not found ({dll_path}); pass --dll{ver}")
        if not os.path.exists(script_path):
            raise SystemExit(f"script.json for {ver} not found ({script_path})")
        x = cls(ver)
        x.build(dll_path, script_path, quiet)
        os.makedirs(cache_dir, exist_ok=True)
        with open(cache, "wb") as f:
            pickle.dump(x, f, protocol=pickle.HIGHEST_PROTOCOL)
        return x

    def build(self, dll_path, script_path, quiet=False):
        t0 = time.time()
        self.dll_path, self.script_path = dll_path, script_path
        pe = PE(dll_path)
        self.image_base = pe.image_base
        self.sections = pe.sections
        self.exports = pe.exports()
        self.strings, self.meta, self.metamethod, self.addrs, self.gen_nparams = parse_script_slots(script_path)
        targets = {}
        for a in self.strings:
            targets[a] = "s"
        for a in self.meta:
            targets[a] = "m"
        for a in self.metamethod:
            targets[a] = "mi"
        addrs, addr_set = self.addrs, set(self.addrs)
        refs, calls = {}, {}
        n_rip = n_hit = n_call = 0
        for name, vsize, vaddr, rsize, raw, chars in pe.code_sections():
            data = pe.b[raw:raw + min(vsize, rsize)]
            for pat, has_imm in ((PAT_RIP, True), (PAT_RIP_0F, False)):
                for m in pat.finditer(data):
                    n_rip += 1
                    p = m.start(1)
                    imm = IMM_AFTER.get(data[p - 2], 0) if has_imm else 0
                    disp = struct.unpack_from("<i", data, p)[0]
                    tgt = vaddr + m.end() + imm + disp
                    k = targets.get(tgt)
                    if k is None:
                        continue
                    i = bisect.bisect_right(addrs, vaddr + m.start()) - 1
                    if i < 0:
                        continue
                    refs.setdefault(addrs[i], []).append((k, tgt))
                    n_hit += 1
            for m in PAT_CALL.finditer(data):
                disp = struct.unpack_from("<i", data, m.start(1))[0]
                tgt = vaddr + m.end() + disp
                if tgt in addr_set:
                    i = bisect.bisect_right(addrs, vaddr + m.start()) - 1
                    if i < 0:
                        continue
                    f = addrs[i]
                    if tgt != f:
                        calls.setdefault(f, []).append(tgt)
                        n_call += 1
        self.refs, self.calls = refs, calls
        if not quiet:
            print(f"[codexref {self.ver}] {os.path.basename(dll_path)}: {len(self.addrs)} functions, {len(self.strings)} string slots, "
                  f"{len(self.meta)} metadata slots, {len(self.metamethod)} MethodInfo slots, {len(self.exports)} exports; "
                  f"{n_hit} slot refs in {len(refs)} functions, {n_call} calls in {len(calls)} functions ({time.time() - t0:.1f}s)", file=sys.stderr)


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ver", required=True)
    ap.add_argument("--dll")
    ap.add_argument("--script")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("rvas", nargs="*", help="hex RVAs to describe")
    a = ap.parse_args()
    x = CodeXref.load(a.ver, a.dll, a.script, force=a.force)
    print(f"ver {x.ver}: {len(x.addrs)} functions, {len(x.exports)} exports, image base {x.image_base:#x}")
    for r in a.rvas:
        rva = int(r, 16)
        print(f"{rva:#x}: size {x.size_of(rva)}")
        print("  strings:", x.strings_of(rva))
        print("  meta:", x.meta_of(rva)[:20])
        print("  calls:", [hex(c) for c in x.calls_of(rva)][:20])
