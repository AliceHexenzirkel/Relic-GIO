#!/usr/bin/env python3
"""Tiny dependency-free PE reader (x64) used by the enhancements tooling:

  pe_info.py <file.dll|exe> [--imports] [--exports] [--json]

Prints the PE timestamp / linker version, the imported DLL names (to prove a static-CRT build has no
VCRUNTIME/MSVCP dependency) and, with --exports, the export table as `name rva` lines — the latter is how
resolve_exports.py reads the il2cpp_* API RVAs straight out of UserAssembly.dll.
"""
import json, struct, sys

def rva_to_off(rva, sections):
    for (name, vsize, vaddr, rsize, raw) in sections:
        if vaddr <= rva < vaddr + max(vsize, rsize):
            return rva - vaddr + raw
    return None

def read_cstr(buf, off, limit=4096):
    end = buf.find(b"\0", off, off + limit)
    return buf[off:end if end != -1 else off + limit].decode("ascii", "replace")

def parse(path):
    with open(path, "rb") as f:
        b = f.read()
    if b[:2] != b"MZ":
        raise SystemExit("not a PE file")
    pe = struct.unpack_from("<I", b, 0x3C)[0]
    if b[pe:pe + 4] != b"PE\0\0":
        raise SystemExit("bad PE signature")
    machine, nsec, tstamp, _, _, opt_size, _ = struct.unpack_from("<HHIIIHH", b, pe + 4)
    opt = pe + 24
    magic = struct.unpack_from("<H", b, opt)[0]
    is64 = magic == 0x20B
    linker_major, linker_minor = b[opt + 2], b[opt + 3]
    image_base = struct.unpack_from("<Q" if is64 else "<I", b, opt + 24)[0]
    dd_off = opt + (112 if is64 else 96)
    dirs = [struct.unpack_from("<II", b, dd_off + 8 * i) for i in range(16)]
    sec_off = opt + opt_size
    sections = []
    for i in range(nsec):
        s = sec_off + 40