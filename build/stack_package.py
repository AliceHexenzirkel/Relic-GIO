#!/usr/bin/env python3
"""Reads a server package archive and prints or checks its catalogue (agent/payloads/<version>/stack.json).

    python build/stack_package.py info <archive> [--json]
    python build/stack_package.py catalog --version V --url U <archive> [--alt-url U2 --alt <archive2>]
                                          [--note <text>]
    python build/stack_package.py check <stack.json> <archive> [<archive2>]

A server package is a vendor's all-in-one docker stack as ONE archive (.7z or .zip) on the Internet
Archive. The agent downloads it by the catalogue of its version: url + size + md5 of the archive,
extractedSize (the bytes of the unpacked tree, for the disk check), topDir (the one top-level folder,
renamed onto the stack folder) and strays (what else lies at the top and is dropped). "alt" names the
same tree packed in the other format, for a box without a 7z extractor. Every number of a catalogue
comes out of this tool, and `info` shows what a package holds before it is catalogued.

  info      size, md5, entries (files + directory entries), extractedSize, topDir, strays, the
            compression methods with their counts, and whether the folders a stack needs although
            nothing is in them are kept (server/res/NavMesh, server/res/SVO). Blocking: no single
            top-level folder that holds docker-compose.yml.tmpl, a top folder or a stray that is not a
            plain name, encrypted members, symbolic links, and for a zip what the agent's own reader
            refuses it for, on every box or where it unpacks the tree on Windows: a method zipfile
            cannot read (one member of each method is opened), a member of compressed patched data, a
            member name the agent refuses or Windows cannot hold, members that cannot lie in one tree
            (a path that is a file for one and a folder for another, a file named `.` or without a
            name of its own, a part of more than 255 bytes, two paths that differ in case only), and a longest member path -- or a deepest folder -- for which no
            stack folder of a Windows box is short enough. Printed too: whether the zip needs ZIP64,
            and the length of its longest member path with how long that leaves the path of the stack
            folder on Windows (the tree is unpacked beside it, into .relic-extract-<its name>; --json
            names the path itself). Hygiene: the rows of every table of sdk/data/sdk.db (a package
            ships an empty database), an sdk/data/config.json (rendered from its template when a stack
            is set up, so one in a package is a leftover), whether sdk/Dockerfile pins flask and
            werkzeug, and the md5 of the database and of the Dockerfile -- both read in memory.
  catalog   prints the package's stack.json in the format of the shipped catalogues (one-space indent,
            CRLF, no line break after the closing brace; a console gets plain lines). Nothing is
            written: redirect the output. The alternate is the same package in the other format: it
            must hold the same tree (top folder, strays, every folder, every file path with its size
            and with the CRC-32 its listing gives -- a file of another content at the same size is
            another tree). Each url is an https://archive.org/download/ address whose extension is the
            archive's own format.
  check     compares a catalogue with the local copies of the archives it names: size and md5 of each,
            extractedSize, topDir and strays; an archive is matched to the catalogue's url or to an
            "alt" entry by its format, and two archives are held to the same tree as in `catalog`. A
            catalogue is checked whole: an entry no local archive was given for is named, and the run
            ends with 1 whatever the others showed.

A .zip is read with zipfile. A .7z is read through a 7-Zip command-line tool (7z, 7zz, 7za or 7zr on
PATH, else 7-Zip's own folder on Windows): `l -slt` for the listing, `e -so` for a member. Standard
library only, Python 3.9 or newer, no network access.

Exit codes: 0 fine -- hygiene findings are printed and do not change it; 1 a blocking finding, named:
an archive the agent could not take, an alternate with another tree, a url of another host or format,
a catalogue that does not agree with an archive or names one that was not given; 2 usage (an archive
where `check` expects the catalogue is one), a file that cannot be read, or no 7-Zip tool for a .7z.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import sqlite3
import struct
import subprocess
import sys
import urllib.parse
import zipfile
import zlib

LABEL = "Internet Archive"
# The host every shipped catalogue names (the agent's selftest holds them to it).
URL_PREFIX = "https://archive.org/download/"
KINDS = ("7z", "zip")
COMPOSE_TEMPLATE = "docker-compose.yml.tmpl"   # the file that makes a top-level folder the stack
# Folders a stack's services are given although the package has nothing to put into them: an archiver
# that stores no directory entries loses them.
STACK_FOLDERS = ("server/res/NavMesh", "server/res/SVO")
SDK_DB = "sdk/data/sdk.db"
SDK_CONFIG = "sdk/data/config.json"
SDK_DOCKERFILE = "sdk/Dockerfile"
PINNED = ("flask", "werkzeug")                 # what the sdk image is built from, by version
MEMBER_READ_MAX = 64 << 20                     # a member read in memory is small or is not read
CATALOG_MAX = 1 << 20                          # a catalogue is a few hundred bytes; more than this is not one
SHOWN_MAX = 10                                 # names quoted per finding
VERSION_RE = re.compile(r"^[0-9]+\.[0-9]+\Z")

# What validate_stack_catalog (agent/gio_agent.py) accepts as topDir and as a stray: one plain path component.
PLAIN_NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._\-]{0,127}$")
MD5_RE = re.compile(r"^[0-9a-fA-F]{32}$")
DRIVE_RE = re.compile(r"^[A-Za-z]:")
# What zip_refusal (agent/gio_agent.py) refuses a zip for besides a member's name -- the flags of a member
# zipfile opens for no compression method: bit 0 and bit 6 (encrypted; bit 6 is strong encryption) and bit 5
# (compressed patched data).
ZIP_FLAGS_ENCRYPTED = 0x1 | 0x40
ZIP_FLAG_PATCHED = 0x20
# ...and what it refuses only where it unpacks the tree on Windows: a name the system cannot hold
# (windows_name_problem), two paths that differ in case alone, and a path Windows' own file calls refuse --
# WINDOWS_PATH_MAX characters or more for any member, WINDOWS_FOLDER_MAX or more for a folder. A package is
# made for every box, so all of it is put to it here. The values are the agent's own.
WINDOWS_PATH_MAX = 260
WINDOWS_FOLDER_MAX = WINDOWS_PATH_MAX - 12
WINDOWS_NAME_CHARS = '<>:"|?*'
# The bytes one component of a path may have on a Linux file system; the agent's own value.
PART_BYTES_MAX = 255
WINDOWS_DEVICE_RE = re.compile(r"^(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\..*)?\Z", re.IGNORECASE)
# The agent unpacks a package beside the stack folder, into a folder named by this prefix and the stack
# folder's own name (extract_tmp in agent/gio_agent.py): that folder's path is this much longer than the
# stack folder's -- whose shortest, on Windows, is a drive's root and a name of one character.
UNPACK_PREFIX = ".relic-extract-"
WINDOWS_STACK_MIN = len("C:\\x")

ZIP_METHOD_NAMES = {0: "Store", 1: "Shrink", 6: "Implode", 8: "Deflate", 9: "Deflate64", 12: "BZip2", 14: "LZMA",
                    93: "Zstandard", 95: "XZ", 98: "PPMd"}
# The methods the zipfile of every Python an agent runs under can read. Another one may open here and still
# not open on a box: the interpreters differ in what they carry.
ZIP_METHODS_EVERYWHERE = (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED, zipfile.ZIP_BZIP2, zipfile.ZIP_LZMA)

SEVENZIP_NAMES = ("7z", "7zz", "7za", "7zr")
SEVENZIP_LIST_TIMEOUT = 600
SEVENZIP_MEMBER_TIMEOUT = 1800                 # a member of a solid archive is reached through its whole block
PROPERTY_RE = re.compile(r"^([A-Za-z][A-Za-z0-9 ]*?) =(?: (.*))?$")
MODE_RE = re.compile(r"^[a-z\-][rwxsStT\-]{9}$")
CRC_RE = re.compile(r"^[0-9A-Fa-f]{8}$")


class PackageError(Exception):
    """A blocking finding that ends the run: an archive or a catalogue that cannot be used as it is."""


class ToolMissing(Exception):
    """A .7z is read through a 7-Zip command-line tool, and there is none."""


class MemberUnreadable(Exception):
    """One member could not be read in memory; the reason is the message."""


def num(n):
    return "{:,}".format(n)


def count(n, noun):
    """A number with its noun: `1 file`, `33,470 files`."""
    return "%s %s%s" % (num(n), noun, "" if n == 1 else "s")


def shown(names, quoted=True):
    """Up to SHOWN_MAX names, quoted, with the count of the rest -- or as many reasons, each as it reads."""
    names = list(names)
    text = ", ".join(repr(n) for n in names[:SHOWN_MAX]) if quoted else "; ".join(names[:SHOWN_MAX])
    return text + (" and %d more" % (len(names) - SHOWN_MAX) if len(names) > SHOWN_MAX else "")


def md5_file(path):
    """The md5 of a file, streamed: a package is gigabytes."""
    if sys.stderr.isatty():
        print("md5 of %s (%s bytes) ..." % (path, num(os.path.getsize(path))), file=sys.stderr)
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def member_name_problem(name):
    """Why the agent refuses a zip that holds a member of this name -- None, or the reason. The rule of
    zip_member_problem (agent/gio_agent.py), test for test: an absolute path, `..`, a backslash, a
    drive letter, a control character. It is put to the name as the archive STORES it
    (ZipInfo.orig_filename): zipfile hands out a name it has tidied for the OS it runs on -- a backslash
    reads as `/` on Windows and stays one on Linux --, so the tidied name would pass here a zip that a box
    refuses whole."""
    if name.startswith("/"):
        return "an absolute path"
    if "\\" in name:
        return "a backslash"
    if ".." in name:
        return "`..`"
    if DRIVE_RE.match(name):
        return "a drive letter"
    if any(ord(c) < 32 or c == "\x7f" for c in name):
        return "a control character"
    return None


def windows_name_problem(name):
    """Why a box that runs Windows cannot hold a file under this member name -- None, or the reason. The
    rule of windows_name_problem (agent/gio_agent.py), test for test: a path component with one of the
    characters < > : " | ? *, one that ends in a dot or a blank, one that is a device (CON, NUL, COM1 ...).
    The agent puts it to a zip only where it unpacks the tree on Windows and refuses the whole archive."""
    for part in name.split("/"):
        if any(c in WINDOWS_NAME_CHARS for c in part):
            return "one of the characters < > : \" | ? *"
        if part.endswith((".", " ")) and part != ".":
            return "a name that ends in a dot or a blank"
        if WINDOWS_DEVICE_RE.match(part):
            return "a device name"
    return None


def tree_problem(tree, name, fold):
    """Why a member cannot lie in ONE tree with the members listed before it, or in a tree at all -- None, or
    the reason. The rule of zip_tree_problem (agent/gio_agent.py), test for test: a file whose name has no
    component but `.` or whose last component is `.`, a component of more than PART_BYTES_MAX bytes, a path
    that is a file for one member and a folder for another -- a directory entry, or the folder another member
    lies in --, and, with `fold`, two paths that differ in case alone. The agent refuses the whole archive for
    all but the last on every box and for the last where it unpacks the tree on Windows. `tree` = {a path, as the
    box tells paths apart: (is a folder, the path as written, the member that brought it)}, filled as the
    listing is walked; the same file stored twice is no clash here (the listing names it: Package.repeated)."""
    parts = [part for part in name.split("/") if part]
    if not name.endswith("/") and not [part for part in parts if part != "."]:
        return "a file without a name of its own (%r)" % name
    if parts and parts[-1] == "." and not name.endswith("/"):
        return "a file named `.` (%s)" % name
    if any(len(part.encode("utf-8")) > PART_BYTES_MAX for part in parts):
        return "a member name with a part of more than %d bytes (%s)" % (PART_BYTES_MAX, name)
    path, last = "", len(parts) - 1
    for n, part in enumerate(parts):
        if part == ".":
            continue
        path = path + "/" + part if path else part
        folder = n < last or name.endswith("/")
        key = path.upper() if fold else path
        was = tree.setdefault(key, (folder, path, name))
        if was[0] != folder:
            return "a path that is a file in one member and a folder in another (%s and %s)" % (was[2], name)
        if was[1] != path:
            return "two paths that differ in case only (%s and %s)" % (was[2], name)
    return None


def counted_twice(text):
    """How many characters of a path Windows counts as two: its path limits are in UTF-16 units, and a
    character outside the Basic Multilingual Plane takes a pair of them. The agent's _counted_twice."""
    return 0 if text.isascii() else sum(1 for c in text if ord(c) > 0xFFFF)


def windows_length(text):
    """The length of a path as Windows counts it."""
    return len(text) + counted_twice(text)


# ------------------------------------------------------------------------------------------- the archive

class Package:
    """One archive as its listing describes it. Paths use `/` and carry no trailing one."""

    def __init__(self, path, kind):
        self.path = path
        self.kind = kind            # "7z" | "zip"
        self.size = os.path.getsize(path)
        self.md5 = None
        self.entries = 0            # members of the listing: files + directory entries
        self.dirs = set()           # folders stored as a member of their own
        self.files = {}             # path -> bytes
        self.crcs = {}              # path -> the file's CRC-32, where the listing gives one
        self.members = {}           # path -> the member as the archive's reader names it
        self.repeated = []          # paths stored more than once
        self.top = None
        self.strays = []
        self.kept = {}              # a folder of STACK_FOLDERS -> None (not in the archive) or the files below it
        self.detail = {}            # what only this archive format tells
        self.sdk = None             # the hygiene facts, when they were asked for
        self.blocking = []
        self.findings = []

    def add(self, name, size, member, is_dir, crc=None):
        self.entries += 1
        name = name.rstrip("/")
        if is_dir:
            self.dirs.add(name)
        elif name in self.files:
            self.repeated.append(name)
        else:
            self.files[name] = size
            self.members[name] = member
            if crc is not None:
                self.crcs[name] = crc

    @property
    def extracted(self):
        return sum(self.files.values())

    def folders(self):
        """Every folder of the unpacked tree: the stored ones and the parents of everything."""
        out = set()

        def add(name):
            while name and name not in out:
                out.add(name)
                name = name.rpartition("/")[0]

        for d in self.dirs:
            add(d)
        for f in self.files:
            add(f.rpartition("/")[0])
        return out


def archive_kind(path):
    """"7z" or "zip", by what the file IS; its name and its catalogue address are held to that (name_problem,
    url_kind): the extension is all a reader of the catalogue has to tell the format by."""
    with open(path, "rb") as f:
        head = f.read(8)
    if head.startswith(b"7z\xbc\xaf\x27\x1c"):
        return "7z"
    if zipfile.is_zipfile(path):
        return "zip"
    if head.startswith(b"Rar!"):
        raise PackageError("%s is a RAR archive: a server package is a .7z or a .zip" % path)
    raise PackageError("%s is neither a 7z nor a zip archive" % path)


def name_problem(path, kind):
    ext = os.path.splitext(path)[1].lower()
    if ext != "." + kind:
        return ("the file name ends in %r but the file is a %s archive: a package carries the extension of its "
                "format" % (ext, kind))
    return None


# ----------------------------------------------------------------------------------------------- zip

def zip_extra_fields(extra):
    """(header id, data length) of every block of a member's extra field."""
    pos = 0
    while pos + 4 <= len(extra):
        ident, length = struct.unpack_from("<HH", extra, pos)
        yield ident, length
        pos += 4 + length


def zip_zip64(path, infos):
    """(needed, written). needed: a value of the zip lives only in its ZIP64 structures -- a member whose
    ZIP64 extra field holds a size or an offset, or an end-of-central-directory record with a count, a size or
    an offset at the limit of its field. written: the structures are there at all -- an archiver may write the
    ZIP64 end record with its locator, or reserve an empty ZIP64 extra field for a large file, although every
    value fits the plain fields; such a zip reads like a plain one."""
    reserved = [length for info in infos for ident, length in zip_extra_fields(info.extra) if ident == 0x0001]
    with open(path, "rb") as f:
        size = f.seek(0, os.SEEK_END)
        span = min(size, 22 + 0xFFFF + 20)     # the end record, the longest comment, the locator
        f.seek(size - span)
        tail = f.read(span)
    end = tail.rfind(b"PK\x05\x06")
    if end < 0 or end + 22 > len(tail):
        return any(reserved), bool(reserved)
    # the end record: two disk numbers and two member counts (16 bits), the directory's size and offset (32 bits)
    record = struct.unpack_from("<HHHHII", tail, end + 4)
    needed = any(reserved) or 0xFFFF in record[:4] or 0xFFFFFFFF in record[4:]
    return needed, bool(reserved) or (end >= 20 and tail[end - 20:end - 16] == b"PK\x06\x07")


def zip_method_problem(zf, info):
    """Why this Python's zipfile cannot read a member packed like `info` -- None when it can. Opening one is
    the test: zipfile refuses a method it does not know (NotImplementedError) or lacks the module for
    (RuntimeError) before the first byte. A member that is damaged ends the run: nothing is said about a
    method then."""
    try:
        with zf.open(info) as src:
            src.read(1)
    except (NotImplementedError, RuntimeError) as e:
        return str(e) or type(e).__name__
    except (zipfile.BadZipFile, zlib.error, EOFError) as e:
        raise PackageError("%s: the member %s does not open (%s)" % (zf.filename, info.orig_filename, e))
    return None


def zip_sealed(info):
    """A member zipfile opens for no compression method: an encrypted one, one of compressed patched data."""
    return bool(info.flag_bits & (ZIP_FLAGS_ENCRYPTED | ZIP_FLAG_PATCHED))


def list_zip(pkg, zf):
    infos = zf.infolist()
    methods = {}       # compress_type -> [files, a member to open]
    refused, windows, encrypted, patched, links = [], [], [], [], []
    clashes, folds = [], []         # what tree_problem says: on every box / only where case tells no names apart
    exact, folded = {}, {}          # the tree it is asked about, told apart by case and not
    longest = deepest = ""          # the member path and the folder path Windows counts the most characters in
    for info in infos:
        # A member is told by the name the archive STORES, as the agent judges and writes it. The one zipfile
        # hands out is tidied -- for the system it runs on, and by a second name an extra field may carry.
        stored = info.orig_filename
        is_dir = stored.endswith("/")
        if member_name_problem(stored):
            refused.append(stored)
        elif windows_name_problem(stored):
            windows.append(stored)
        clash, fold = tree_problem(exact, stored, False), tree_problem(folded, stored, True)
        if clash:
            clashes.append(clash)
        elif fold:
            folds.append(fold)
        if windows_length(stored.rstrip("/")) > windows_length(longest):
            longest = stored.rstrip("/")
        folder = stored.rstrip("/") if stored.endswith("/") else stored.rpartition("/")[0]
        if windows_length(folder) > windows_length(deepest):
            deepest = folder
        if info.flag_bits & ZIP_FLAGS_ENCRYPTED:
            encrypted.append(stored)
        if info.flag_bits & ZIP_FLAG_PATCHED:
            patched.append(stored)
        if info.external_attr >> 28 == 0xA:    # a Unix mode whose file type is "symbolic link"
            links.append(stored)
        pkg.add(stored, info.file_size, info, is_dir, info.CRC)
        if is_dir:
            continue
        slot = methods.setdefault(info.compress_type, [0, None])
        slot[0] += 1
        if slot[1] is None or (zip_sealed(slot[1]) and not zip_sealed(info)):
            slot[1] = info      # a sealed member tells nothing about its method: it does not open either way
    named, unreadable = {}, []
    for method in sorted(methods, key=lambda m: -methods[m][0]):
        files, probe = methods[method]
        name = ZIP_METHOD_NAMES.get(method, "method %d" % method)
        named[name] = files
        why = None if zip_sealed(probe) else zip_method_problem(zf, probe)
        if why is None and method not in ZIP_METHODS_EVERYWHERE:
            why = "not a method every Python's zipfile reads"
        if why:
            unreadable.append(name)
            pkg.blocking.append("%s (%s) cannot be read by zipfile (%s): pack with Deflate"
                                % (name, count(files, "file"), why))
    if refused:
        pkg.blocking.append("%s the agent refuses (%s): %s"
                            % (count(len(refused), "member name"),
                               ", ".join(sorted({member_name_problem(n) for n in refused})), shown(refused)))
    if clashes:
        pkg.blocking.append("%s the agent refuses: %s" % (count(len(clashes), "member"), shown(clashes, False)))
    if windows:
        pkg.blocking.append("%s a box that runs Windows cannot hold (%s): %s"
                            % (count(len(windows), "member name"),
                               ", ".join(sorted({windows_name_problem(n) for n in windows})), shown(windows)))
    if folds:
        pkg.blocking.append("%s a box that runs Windows cannot hold beside another: %s"
                            % (count(len(folds), "member"), shown(folds, False)))
    if encrypted:
        pkg.blocking.append("%s: %s" % (count(len(encrypted), "encrypted member"), shown(encrypted)))
    if patched:
        pkg.blocking.append("%s of compressed patched data: %s" % (count(len(patched), "member"), shown(patched)))
    if links:
        pkg.blocking.append("%s: %s" % (count(len(links), "symbolic link"), shown(links)))
    # On Windows the agent refuses a zip one of whose members would lie at a path Windows' own file calls
    # refuse -- the folder it unpacks into, a separator, the member's own path: WINDOWS_PATH_MAX characters or
    # more for any member, WINDOWS_FOLDER_MAX or more for a folder (a directory entry, the folder a file lies
    # in), counted as Windows counts them. Each of the two leaves the path of the folder it unpacks into so
    # many characters; the stack folder, which an admin chooses, may have the smaller of them less the length
    # of UNPACK_PREFIX.
    room = WINDOWS_PATH_MAX - 2 - len(longest) - counted_twice(longest)
    folder_room = WINDOWS_FOLDER_MAX - 2 - len(deepest) - counted_twice(deepest)
    deep = bool(deepest) and folder_room < room
    if deep:
        room = folder_room
    stack_room = room - len(UNPACK_PREFIX)
    if infos and stack_room < WINDOWS_STACK_MIN:
        held = deepest if deep else longest
        pkg.blocking.append("%s has %s (%s): on a box that runs Windows no stack folder is short enough for the "
                            "package to be unpacked beside it"
                            % ("the deepest folder" if deep else "the longest member path",
                               count(windows_length(held), "character"), held))
    needed, written = zip_zip64(pkg.path, infos)
    pkg.detail = {"methods": named, "unreadableMethods": unreadable, "zip64": needed, "zip64Written": written,
                  "encrypted": len(encrypted), "patched": len(patched), "symlinks": len(links),
                  "refusedNames": refused, "windowsNames": windows, "treeClashes": clashes, "caseClashes": folds,
                  "longestPath": {"length": windows_length(longest), "name": longest,
                                  "folderLength": windows_length(deepest), "folder": deepest,
                                  "windowsFolderMax": max(room, 0) if infos else None,
                                  "windowsStackFolderMax": max(stack_room, 0) if infos else None}}


def zip_member(zf, info):
    try:
        with zf.open(info) as src:
            data = src.read(info.file_size + 1)
    except (zipfile.BadZipFile, zlib.error, RuntimeError, NotImplementedError, EOFError, OSError) as e:
        raise MemberUnreadable(str(e) or type(e).__name__)
    if len(data) != info.file_size:
        raise MemberUnreadable("it holds another number of bytes than its header says")
    return data


# ------------------------------------------------------------------------------------------------ 7z

def find_7zip():
    """A 7-Zip command-line tool: on PATH, else in 7-Zip's own folder on Windows. None when there is none."""
    for name in SEVENZIP_NAMES:
        hit = shutil.which(name)
        if hit:
            return hit
    if os.name == "nt":
        homes = [os.environ.get("ProgramFiles", r"C:\Program Files"),
                 os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")]
        if os.environ.get("LOCALAPPDATA"):
            homes.append(os.path.join(os.environ["LOCALAPPDATA"], "Programs"))
        for home in homes:
            tool = os.path.join(home, "7-Zip", "7z.exe")
            if os.path.isfile(tool):
                return tool
    return None


def run_7zip(tool, command, switches, archive, members=(), timeout=SEVENZIP_LIST_TIMEOUT):
    """One run of the tool. `-p-` hands it a password, so an encrypted archive fails at once instead of asking
    for one at the console; `--` ends the switches, whatever the names after it begin with."""
    argv = [tool, command, "-p-"] + list(switches) + ["--", archive] + list(members)
    try:
        return subprocess.run(argv, stdin=subprocess.DEVNULL, capture_output=True, timeout=timeout)
    except (OSError, subprocess.SubprocessError) as e:
        raise PackageError("%s could not be run: %s" % (tool, e))


def sevenzip_said(r):
    """The end of what the tool printed, for an error text."""
    text = (r.stderr or b"").decode("utf-8", "replace") + "\n" + (r.stdout or b"")[-2048:].decode("utf-8", "replace")
    lines = [l.strip() for l in text.replace("\r", "").split("\n") if l.strip()]
    return " | ".join(lines[-3:])


def parse_7zip_listing(text):
    """(the archive's properties, one dict per member) of `7z l -slt`: `Key = Value` lines in blocks, the
    archive's own block in front of the dashed line, one block per member after it."""
    head, _, body = text.replace("\r\n", "\n").partition("\n----------\n")

    def properties(block):
        out = {}
        for line in block.split("\n"):
            m = PROPERTY_RE.match(line)
            if m:
                out.setdefault(m.group(1), m.group(2) or "")
        return out

    members = [properties(b) for b in re.split(r"\n[ \t]*\n", body)]
    return properties(head), [m for m in members if "Path" in m]


def sevenzip_entry_type(props):
    """(is a folder, is a symbolic link) of a listed member. `Attributes` holds the Windows attribute
    letters (D = directory, L = reparse point) and, for an archive made on a Unix system, the mode string
    after them (`D_ drwxr-xr-x`, `A_ lrwxrwxrwx`)."""
    words = props.get("Attributes", "").split()
    mode = words[-1] if words and MODE_RE.match(words[-1]) else ""
    letters = words[0] if words and words[0] != mode else ""
    is_dir = props.get("Folder") == "+" or "D" in letters or mode.startswith("d")
    is_link = bool(props.get("Symbolic Link")) or "L" in letters or mode.startswith("l")
    return is_dir, is_link


def list_7z(pkg, tool):
    # 7-Zip writes names in the console's code page on Windows unless told otherwise.
    r = run_7zip(tool, "l", ["-slt"] + (["-sccUTF-8"] if os.name == "nt" else []), pkg.path)
    if r.returncode != 0:
        raise PackageError("%s cannot be listed (%s exit %d: %s)" % (pkg.path, os.path.basename(tool), r.returncode,
                                                                    sevenzip_said(r)))
    archive, members = parse_7zip_listing(r.stdout.decode("utf-8", "replace"))
    if archive.get("Type", "7z") != "7z":
        pkg.blocking.append("7-Zip reads it as a %s archive, not as 7z" % archive["Type"])
    methods, encrypted, links = {}, [], []
    for props in members:
        # the tool prints a member's path with the separator of the OS it runs on
        name = props["Path"].replace(os.sep, "/")
        is_dir, is_link = sevenzip_entry_type(props)
        try:
            size = int(props.get("Size") or 0)
        except ValueError:
            raise PackageError("%s: the listing gives no size for %s" % (pkg.path, name))
        # a folder and a file of no bytes are listed with an empty CRC
        crc = props.get("CRC", "")
        pkg.add(name, size, props["Path"], is_dir, int(crc, 16) if CRC_RE.match(crc) else None)
        if props.get("Encrypted") == "+":
            encrypted.append(name)
        if is_link:
            links.append(name)
        if not is_dir and props.get("Method"):
            methods[props["Method"]] = methods.get(props["Method"], 0) + 1
    if encrypted:
        pkg.blocking.append("%s: %s" % (count(len(encrypted), "encrypted member"), shown(encrypted)))
    if links:
        pkg.blocking.append("%s: %s" % (count(len(links), "symbolic link"), shown(links)))
    blocks = archive.get("Blocks", "")
    pkg.detail = {"methods": dict(sorted(methods.items(), key=lambda kv: -kv[1])),
                  "archiveMethod": archive.get("Method", ""), "solid": archive.get("Solid") == "+",
                  "blocks": int(blocks) if blocks.isdigit() else 0,
                  "encrypted": len(encrypted), "symlinks": len(links)}


def sevenzip_member(tool, archive, member, size):
    r = run_7zip(tool, "e", ["-so"], archive, [member], SEVENZIP_MEMBER_TIMEOUT)
    if r.returncode != 0:
        raise MemberUnreadable("%s exit %d: %s" % (os.path.basename(tool), r.returncode, sevenzip_said(r)))
    if len(r.stdout) != size:
        raise MemberUnreadable("the tool gave %s bytes, the listing says %s" % (num(len(r.stdout)), num(size)))
    return r.stdout


# ------------------------------------------------------------------------------ what the listing means

def layout(pkg):
    """topDir, strays and the kept folders, from the listing; what the agent could not take is blocking."""
    if not pkg.files and not pkg.dirs:
        pkg.blocking.append("the archive holds nothing")
        return
    if pkg.repeated:
        pkg.blocking.append("%s stored more than once: %s" % (count(len(pkg.repeated), "path"), shown(pkg.repeated)))
    tops = sorted({name.split("/", 1)[0] for name in list(pkg.files) + list(pkg.dirs)})
    holders = [t for t in tops if t + "/" + COMPOSE_TEMPLATE in pkg.files]
    one = "the stack must be ONE top-level folder, which the agent renames onto the stack folder"
    if COMPOSE_TEMPLATE in pkg.files:
        pkg.blocking.append("%s lies at the root of the archive: %s" % (COMPOSE_TEMPLATE, one))
    elif not holders:
        pkg.blocking.append("no top-level folder holds %s (top level: %s): %s" % (COMPOSE_TEMPLATE, shown(tops), one))
    elif len(holders) > 1:
        pkg.blocking.append("%s hold %s (%s): %s" % (count(len(holders), "top-level folder"), COMPOSE_TEMPLATE,
                                                     shown(holders), one))
    if len(holders) != 1:
        return
    pkg.top = holders[0]
    pkg.strays = [t for t in tops if t != pkg.top]
    if not PLAIN_NAME_RE.match(pkg.top) or pkg.top.startswith("."):
        pkg.blocking.append("the top folder %r is not a plain name (letters, digits, '.', '_', '-'; the agent's "
                            "catalogue check refuses it)" % pkg.top)
    odd = [s for s in pkg.strays if not PLAIN_NAME_RE.match(s)]
    if odd:
        pkg.blocking.append("top-level entries beside the stack that are not plain names (the agent's catalogue "
                            "check refuses them as strays): %s" % shown(odd))
    folders = pkg.folders()
    for rel in STACK_FOLDERS:
        path = pkg.top + "/" + rel
        pkg.kept[rel] = sum(1 for f in pkg.files if f.startswith(path + "/")) if path in folders else None
        if pkg.kept[rel] is None:
            pkg.findings.append("%s is not in the archive (a stack whose services are given that folder needs "
                                "it kept, empty or not)" % rel)


def sqlite_rows(data):
    """{table: rows} of an SQLite database held in memory, in the order the tables were created."""
    if not data:
        # deserialize answers no bytes with a MemoryError, not with an error of sqlite3
        raise MemberUnreadable("a file of no bytes, not a database")
    con = sqlite3.connect(":memory:")
    try:
        if not hasattr(con, "deserialize"):
            raise MemberUnreadable("this Python's sqlite3 opens no database from memory (Python 3.11 does)")
        if data[18:20] == b"\x02\x02":
            # A write-ahead-log database does not open from memory; the two format bytes of the header say
            # which journal the file uses, and its own pages read the same either way.
            data = data[:18] + b"\x01\x01" + data[20:]
        con.deserialize(data)
        tables = [r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type = 'table' "
                                            "AND name NOT LIKE 'sqlite_%' ORDER BY rowid")]
        return {t: con.execute('SELECT COUNT(*) FROM "%s"' % t.replace('"', '""')).fetchone()[0] for t in tables}
    except sqlite3.Error as e:
        raise MemberUnreadable("not a readable SQLite database (%s)" % e)
    finally:
        con.close()


def dockerfile_pins(text):
    """{package: the version the Dockerfile pins it to, or None} for PINNED; comment lines do not count."""
    code = "\n".join(l for l in text.splitlines() if not l.lstrip().startswith("#"))
    out = {}
    for package in PINNED:
        m = re.search(r"(?i)(?<![A-Za-z0-9_.\-])%s\s*==\s*([A-Za-z0-9_.!+*\-]+)" % re.escape(package), code)
        out[package] = m.group(1) if m else None
    return out


def look_at_sdk(pkg, read):
    """The hygiene facts of the sdk folder; `read(path)` gives a member's bytes or raises MemberUnreadable."""
    if pkg.top is None:
        return

    def member(rel):
        path = pkg.top + "/" + rel
        if path not in pkg.files:
            return {"present": False}
        fact = {"present": True, "size": pkg.files[path]}
        if rel == SDK_CONFIG:
            return fact                                    # only its being there matters
        try:
            if fact["size"] > MEMBER_READ_MAX:
                raise MemberUnreadable("%s bytes are too many to read in memory" % num(fact["size"]))
            data = read(path)
        except MemberUnreadable as e:
            fact["problem"] = str(e)
            pkg.findings.append("%s could not be read: %s" % (rel, e))
            return fact
        fact["md5"] = hashlib.md5(data).hexdigest()
        fact["data"] = data
        return fact

    db, config, dockerfile = member(SDK_DB), member(SDK_CONFIG), member(SDK_DOCKERFILE)
    if not db["present"]:
        pkg.findings.append("%s is not in the archive" % SDK_DB)
    elif "data" in db:
        try:
            db["rows"] = sqlite_rows(db.pop("data"))
        except MemberUnreadable as e:
            db["problem"] = str(e)
            pkg.findings.append("%s: %s" % (SDK_DB, e))
        else:
            filled = ["%s %s" % (t, num(n)) for t, n in db["rows"].items() if n]
            if filled:
                pkg.findings.append("%s is not empty (%s): a package ships a database without accounts or tokens"
                                    % (SDK_DB, ", ".join(filled)))
    if config["present"]:
        pkg.findings.append("%s is in the archive: a rendered file, written from its template when a stack is "
                            "set up" % SDK_CONFIG)
    if not dockerfile["present"]:
        pkg.findings.append("%s is not in the archive" % SDK_DOCKERFILE)
    elif "data" in dockerfile:
        dockerfile["pins"] = dockerfile_pins(dockerfile.pop("data").decode("utf-8", "replace"))
        loose = [p for p, v in dockerfile["pins"].items() if v is None]
        if loose:
            pkg.findings.append("%s does not pin %s: the image is built from whatever version is current that day"
                                % (SDK_DOCKERFILE, " and ".join(loose)))
    pkg.sdk = {"db": db, "config": config, "dockerfile": dockerfile}


def read_package(path, hygiene=False):
    """The archive's listing and what follows from it -- everything but the md5, which is the slow part and
    is asked for once the quick checks are through."""
    kind = archive_kind(path)
    pkg = Package(path, kind)
    problem = name_problem(path, kind)
    if problem:
        pkg.blocking.append(problem)
    if kind == "zip":
        try:
            zf = zipfile.ZipFile(path)
        except (zipfile.BadZipFile, ValueError, EOFError, NotImplementedError) as e:
            # (NotImplementedError: a member that asks for a newer zip version than zipfile reads)
            raise PackageError("%s cannot be read as a zip (%s)" % (path, e))
        with zf:
            list_zip(pkg, zf)
            layout(pkg)
            if hygiene:
                look_at_sdk(pkg, lambda name: zip_member(zf, pkg.members[name]))
    else:
        tool = find_7zip()
        if tool is None:
            raise ToolMissing("%s is a .7z and no 7-Zip command-line tool was found (%s on PATH, or 7-Zip in its "
                              "own folder on Windows): install 7-Zip, then run this again"
                              % (path, ", ".join(SEVENZIP_NAMES)))
        list_7z(pkg, tool)
        layout(pkg)
        if hygiene:
            look_at_sdk(pkg, lambda name: sevenzip_member(tool, path, pkg.members[name], pkg.files[name]))
    return pkg


def tree_differences(a, b):
    """Why two archives do not unpack to the same tree -- [] when they do: the same top folder, the same
    strays, the same files with the same sizes and the same content, the same folders. Content is the CRC-32
    each listing carries for a file, so no member is unpacked: a file replaced by one of the same length (a
    database emptied of its rows keeps its pages) is a difference like any other."""
    out = []
    if a.top != b.top:
        out.append("the top folder is %r in one and %r in the other" % (a.top, b.top))
    if a.strays != b.strays:
        out.append("the strays are %r in one and %r in the other" % (a.strays, b.strays))
    for one, other in ((a, b), (b, a)):
        only = sorted(set(one.files) - set(other.files))
        if only:
            out.append("%s only in %s: %s" % (count(len(only), "file"), os.path.basename(one.path), shown(only)))
    sizes = sorted(n for n in a.files if n in b.files and a.files[n] != b.files[n])
    if sizes:
        out.append("%s of another size: %s" % (count(len(sizes), "file"), shown(sizes)))
    content = sorted(n for n in a.files if b.files.get(n) == a.files[n]
                     and n in a.crcs and n in b.crcs and a.crcs[n] != b.crcs[n])
    if content:
        out.append("%s of another content (the same size, another CRC-32): %s"
                   % (count(len(content), "file"), shown(content)))
    for one, other in ((a, b), (b, a)):
        only = sorted(one.folders() - other.folders())
        if only:
            out.append("%s only in %s: %s" % (count(len(only), "folder"), os.path.basename(one.path), shown(only)))
    return out


def content_unproven(a, b):
    """The files two archives hold at the same size whose content tree_differences could not compare: a
    listing gives no CRC-32 for them. A file of no bytes needs none."""
    return sorted(n for n, size in a.files.items()
                  if size and b.files.get(n) == size and not (n in a.crcs and n in b.crcs))


def same_tree_said(a, b):
    """One line for two archives tree_differences found nothing in: what was held equal, and what could not
    be."""
    files = sum(1 for n in a.files if n in b.files)
    unproven = content_unproven(a, b)
    if not unproven:
        return "the two archives hold the same tree: %s, each of the same size and CRC-32" % count(files, "file")
    return ("the two archives hold the same tree by path and size (%s); the content of %s was NOT compared, a "
            "listing gives no CRC-32 for it: %s" % (count(files, "file"), count(len(unproven), "file"),
                                                    shown(unproven)))


# ------------------------------------------------------------------------------------------ the catalogue

def url_kind(url):
    """The archive format a catalogue url names by its extension ("7z", "zip"), or None."""
    ext = os.path.splitext(urllib.parse.urlsplit(url).path)[1].lower()
    return ext[1:] if ext[1:] in KINDS else None


def url_problem(url):
    """Why a catalogue may not name this address -- None, or the reason. The url rules of the agent's
    validate_stack_catalog, the host the agent's selftest holds every shipped catalogue to, and an extension
    that names the archive's format."""
    if not isinstance(url, str) or not url.startswith(URL_PREFIX):
        return "must be an %s<item>/<file> address" % URL_PREFIX
    if any(c in url for c in " \t\r\n\"'\\"):
        return "holds a blank, a quote or a backslash"
    parts = urllib.parse.urlsplit(url)
    if parts.query or parts.fragment or not re.match(r"^[^/]+/[^/].*$", url[len(URL_PREFIX):]):
        return "must be an %s<item>/<file> address" % URL_PREFIX
    if url_kind(url) is None:
        return "must end in .7z or .zip"
    return None


def catalog_problems(cat):
    """Why the agent would not take this catalogue -- [] when it would: validate_stack_catalog's rules, the
    generic ones of the agent's selftest (label, host, an unpacked tree larger than its archive), and the same
    for every "alt" entry."""
    if not isinstance(cat, dict):
        return ["not a JSON object"]
    out = []
    if not isinstance(cat.get("version"), str) or not VERSION_RE.match(cat["version"]):
        out.append("version must be a game version (<digits>.<digits>)")
    if cat.get("label") != LABEL:
        out.append("label must be %r" % LABEL)
    extracted = cat.get("extractedSize")
    if isinstance(extracted, bool) or not isinstance(extracted, int) or extracted <= 0:
        out.append("extractedSize must be a positive integer (bytes)")
        extracted = None
    top = cat.get("topDir")
    if not isinstance(top, str) or not PLAIN_NAME_RE.match(top) or top.startswith("."):
        out.append("topDir must be one plain folder name")
    strays = cat.get("strays", [])
    if not isinstance(strays, list) or not all(isinstance(s, str) and PLAIN_NAME_RE.match(s) for s in strays):
        out.append("strays must be a list of plain file names")
    alts = cat.get("alt", [])
    if not isinstance(alts, list) or not all(isinstance(a, dict) for a in alts):
        out.append("alt must be a list of {url, size, md5} objects")
        alts = []
    for what, entry in [("url", cat)] + [("alt[%d].url" % i, a) for i, a in enumerate(alts)]:
        problem = url_problem(entry.get("url"))
        if problem:
            out.append("%s %s" % (what, problem))
        size = entry.get("size")
        if isinstance(size, bool) or not isinstance(size, int) or size <= 0:
            out.append("the size beside %s must be a positive integer (bytes)" % what)
        elif extracted is not None and extracted <= size:
            out.append("extractedSize must be larger than the size beside %s" % what)
        if not isinstance(entry.get("md5"), str) or not MD5_RE.match(entry["md5"]):
            out.append("the md5 beside %s must be 32 hex digits" % what)
    kinds = [url_kind(e["url"]) for e in [cat] + alts if isinstance(e.get("url"), str) and url_kind(e["url"])]
    if len(kinds) != len(set(kinds)):
        out.append("two entries name the same format: an alternate is the package in the OTHER format")
    return out


def build_catalog(version, url, pkg, alt_url=None, alt=None, note=None):
    """The catalogue of a package, keys in the order of the shipped ones."""
    cat = {"version": version}
    if note:
        cat["_note"] = note
    cat.update({"label": LABEL, "url": url, "size": pkg.size, "md5": pkg.md5})
    if alt is not None:
        cat["alt"] = [{"url": alt_url, "size": alt.size, "md5": alt.md5}]
    cat.update({"extractedSize": pkg.extracted, "topDir": pkg.top, "strays": list(pkg.strays)})
    return cat


def catalog_bytes(cat):
    """A catalogue as the shipped stack.json files are written: one-space indent, CRLF, no line break after
    the closing brace."""
    return json.dumps(cat, indent=1, ensure_ascii=False).replace("\n", "\r\n").encode("utf-8")


# ----------------------------------------------------------------------------------------------- commands

def info_dict(pkg):
    out = {"path": pkg.path, "type": pkg.kind, "size": pkg.size, "md5": pkg.md5, "entries": pkg.entries,
           "directoryEntries": len(pkg.dirs), "files": len(pkg.files), "extractedSize": pkg.extracted,
           "topDir": pkg.top, "strays": pkg.strays,
           "folders": {rel: {"kept": n is not None, "files": n or 0} for rel, n in pkg.kept.items()},
           pkg.kind: pkg.detail}
    if pkg.sdk is not None:
        out["sdk"] = pkg.sdk
    out["blocking"] = pkg.blocking
    out["findings"] = pkg.findings
    return out


def print_info(pkg):
    def row(key, value):
        print("  %-22s %s" % (key, value))

    print(pkg.path)
    row("type", pkg.kind)
    row("size", "%s bytes" % num(pkg.size))
    row("md5", pkg.md5)
    row("entries", "%s = %s files + %s directory entries" % (num(pkg.entries), num(len(pkg.files)), num(len(pkg.dirs))))
    row("extractedSize", "%s bytes" % num(pkg.extracted))
    row("topDir", pkg.top if pkg.top is not None else "-")
    row("strays", ", ".join(pkg.strays) or "none")
    for rel, n in pkg.kept.items():
        row(rel, "NOT in the archive" if n is None else "kept, empty" if n == 0 else "kept, %s" % count(n, "file"))
    d = pkg.detail
    row("methods", ", ".join("%s %s" % (m, num(n)) for m, n in d["methods"].items()) or "-")
    if pkg.kind == "zip":
        row("zipfile reads them", "yes" if not d["unreadableMethods"] else "NOT " + ", ".join(d["unreadableMethods"]))
        row("ZIP64", "yes" if d["zip64"] else "no" if not d["zip64Written"] else
            "no (ZIP64 records are written, every size, offset and count fits the plain fields)")
        row("refused names", num(len(d["refusedNames"])))
        row("names Windows refuses", num(len(d["windowsNames"])))
        row("not in one tree", "%s members, and %s paths that differ in case only"
            % (num(len(d["treeClashes"])), num(len(d["caseClashes"]))))
        row("patched-data members", num(d["patched"]))
        longest = d["longestPath"]
        if longest["windowsStackFolderMax"] is None:
            row("longest path", "-")
        else:
            row("longest path", "%s, its deepest folder %s -- on Windows %s"
                % (count(longest["length"], "character"), num(longest["folderLength"]),
                   "no stack folder is short enough for the package to be unpacked beside it"
                   if longest["windowsStackFolderMax"] < WINDOWS_STACK_MIN else
                   "the stack folder's path may have %s at most (the tree is unpacked beside it, into %s<its name>)"
                   % (count(longest["windowsStackFolderMax"], "character"), UNPACK_PREFIX)))
    else:
        row("archive", "%s, %s, %s" % (d["archiveMethod"] or "-", "solid" if d["solid"] else "not solid",
                                       count(d["blocks"], "block")))
    row("encrypted members", num(d["encrypted"]))
    row("symbolic links", num(d["symlinks"]))
    if pkg.sdk is not None:
        db, config, dockerfile = pkg.sdk["db"], pkg.sdk["config"], pkg.sdk["dockerfile"]
        if not db["present"]:
            row(SDK_DB, "NOT in the archive")
        else:
            rows = db.get("rows")
            row(SDK_DB, "%s bytes, md5 %s" % (num(db["size"]), db.get("md5", "-")))
            row("  rows", db.get("problem") if rows is None else
                ", ".join("%s %s" % (t, num(n)) for t, n in rows.items()) or "no tables")
        row(SDK_CONFIG, "in the archive (%s bytes)" % num(config["size"]) if config["present"]
            else "not in the archive")
        if not dockerfile["present"]:
            row(SDK_DOCKERFILE, "NOT in the archive")
        else:
            row(SDK_DOCKERFILE, "%s bytes, md5 %s" % (num(dockerfile["size"]), dockerfile.get("md5", "-")))
            pins = dockerfile.get("pins")
            row("  pins", dockerfile.get("problem") if pins is None else
                ", ".join("%s==%s" % (p, v) if v else "%s NOT pinned" % p for p, v in pins.items()))
    print("blocking:" if pkg.blocking else "blocking: nothing")
    for b in pkg.blocking:
        print("  - %s" % b)
    print("hygiene:" if pkg.findings else "hygiene: nothing found")
    for f in pkg.findings:
        print("  - %s" % f)


def blocked(problems):
    sys.stdout.flush()      # what was printed so far stays in front of the verdict when both go to one place
    for p in problems:
        print("ERROR: %s" % p, file=sys.stderr)
    return 1


def cmd_info(args):
    pkg = read_package(args.archive, hygiene=True)
    pkg.md5 = md5_file(pkg.path)
    if args.json:
        print(json.dumps(info_dict(pkg), indent=1))
    else:
        print_info(pkg)
    return 1 if pkg.blocking else 0


def cmd_catalog(args):
    if bool(args.alt) != bool(args.alt_url):
        print("--alt and --alt-url go together: the alternate archive and its address", file=sys.stderr)
        return 2
    if not VERSION_RE.match(args.version):
        print("--version must be a game version (<digits>.<digits>), not %r" % args.version, file=sys.stderr)
        return 2
    given = [("--url", args.url, args.archive)] + ([("--alt-url", args.alt_url, args.alt)] if args.alt else [])
    problems = []
    for flag, url, path in given:
        problem = url_problem(url)
        if problem:
            problems.append("%s %s (%s)" % (flag, problem, url))
        elif url_kind(url) != archive_kind(path):
            problems.append("%s ends in .%s but %s is a %s archive: the extension of the address names the format"
                            % (flag, url_kind(url), path, archive_kind(path)))
    if problems:
        return blocked(problems)
    packages = [read_package(path) for _, _, path in given]
    for pkg in packages:
        problems += ["%s: %s" % (pkg.path, b) for b in pkg.blocking]
    if len(packages) == 2:
        first, second = packages
        if first.kind == second.kind:
            problems.append("both archives are .%s: the alternate is the package in the OTHER format" % first.kind)
        problems += ["the two archives do not hold the same tree: %s" % d for d in tree_differences(first, second)]
    if problems:
        return blocked(problems)
    if len(packages) == 2:
        print(same_tree_said(*packages), file=sys.stderr)
    for pkg in packages:
        pkg.md5 = md5_file(pkg.path)
    cat = build_catalog(args.version, args.url, packages[0], args.alt_url, packages[1] if len(packages) == 2 else None,
                        args.note)
    problems = catalog_problems(cat)
    if problems:
        return blocked("the catalogue of this package: %s" % p for p in problems)
    if sys.stdout.isatty():
        print(json.dumps(cat, indent=1, ensure_ascii=False))
    else:
        # as bytes, so that a redirect gives the file itself whatever the console's line ends and code page
        sys.stdout.flush()
        sys.stdout.buffer.write(catalog_bytes(cat))
        sys.stdout.buffer.flush()
    return 0


def cmd_check(args):
    # The catalogue is read up to a bound and looked at before it is parsed: an archive given in its place
    # (the arguments in the other order) is gigabytes, and is told by its first bytes whatever its size.
    with open(args.catalog, "rb") as f:
        raw = f.read(CATALOG_MAX + 1)
    not_one = ("an archive" if raw.startswith((b"PK", b"7z\xbc\xaf\x27\x1c", b"Rar!")) else
               "more than %s bytes" % num(CATALOG_MAX) if len(raw) > CATALOG_MAX else None)
    if not_one:
        print("%s is not a catalogue (%s): the catalogue comes first -- check <stack.json> <archive> [<archive2>]"
              % (args.catalog, not_one), file=sys.stderr)
        return 2
    try:
        cat = json.loads(raw.decode("utf-8"))
    except ValueError as e:
        return blocked(["%s is not JSON (%s)" % (args.catalog, e)])
    problems = ["%s: %s" % (args.catalog, p) for p in catalog_problems(cat)]
    if problems:
        return blocked(problems)
    entries = [("url", cat)] + [("alt[%d]" % i, a) for i, a in enumerate(cat.get("alt", []))]
    packages, differs = [], 0

    def compare(what, want, got, show=lambda v: v):
        nonlocal differs
        if want == got:
            print("  %-14s ok       %s" % (what, show(got)))
        else:
            differs += 1
            print("  %-14s DIFFERS  catalogue %s, archive %s" % (what, show(want), show(got)))

    for path in [args.archive] + ([args.archive2] if args.archive2 else []):
        pkg = read_package(path)
        match = [(what, e) for what, e in entries if url_kind(e["url"]) == pkg.kind]
        if not match:
            return blocked(["%s is a %s archive and the catalogue has no .%s entry left to compare it with (left: %s)"
                            % (path, pkg.kind, pkg.kind, ", ".join(e["url"] for _, e in entries) or "none")])
        what, entry = match[0]
        entries.remove(match[0])
        packages.append(pkg)
        print("%s  <->  %s %s" % (path, what, entry["url"]))
        for b in pkg.blocking:
            differs += 1
            print("  BLOCKING       %s" % b)
        compare("size", entry["size"], pkg.size, num)
        if entry["size"] == pkg.size:
            compare("md5", entry["md5"].lower(), md5_file(path))
        else:
            print("  %-14s not read (the size already differs)" % "md5")
        compare("extractedSize", cat["extractedSize"], pkg.extracted, num)
        compare("topDir", cat["topDir"], pkg.top)
        compare("strays", sorted(cat.get("strays", [])), sorted(pkg.strays), lambda v: ", ".join(v) or "none")
    if len(packages) == 2:
        other = tree_differences(*packages)
        for d in other:
            differs += 1
            print("the two archives do not hold the same tree: %s" % d)
        if not other:
            print(same_tree_said(*packages))
    for what, entry in entries:
        print("not checked (no local archive given): %s %s" % (what, entry["url"]))
    problems = []
    if differs:
        problems.append("%s does not agree: %s (above)" % (args.catalog, count(differs, "difference")))
    if entries:
        # An entry nothing was held against may carry any size or md5: a part does not prove the catalogue.
        problems.append("%s is not checked whole: no local archive was given for %s -- a catalogue agrees when "
                        "every archive it names does" % (args.catalog, ", ".join("%s %s" % (what, entry["url"])
                                                                                 for what, entry in entries)))
    if problems:
        return blocked(problems)
    print("OK: %s agrees with %s" % (args.catalog, count(len(packages), "archive")))
    return 0


# --------------------------------------------------------------------------------------------------- main

def main(argv=None):
    ap = argparse.ArgumentParser(prog="stack_package.py",
                                 description="Reads a server package archive (.7z or .zip) and prints or checks its "
                                             "catalogue, agent/payloads/<version>/stack.json (see the module "
                                             "docstring).")
    sub = ap.add_subparsers(dest="cmd", metavar="command")
    sub.required = True

    p = sub.add_parser("info", help="what the archive is and holds: size, md5, entries, extractedSize, topDir, strays, "
                                    "what the agent could not take, the hygiene of its sdk folder")
    p.add_argument("archive", help="the .7z or .zip")
    p.add_argument("--json", action="store_true", help="machine-readable output")
    p.set_defaults(fn=cmd_info)

    p = sub.add_parser("catalog", help="print the stack.json of a package (nothing is written)")
    p.add_argument("archive", help="the archive the catalogue's url names")
    p.add_argument("--version", required=True, help="the game version of the stack, e.g. 1.6")
    p.add_argument("--url", required=True, help="where the archive is published (%s<item>/<file>)" % URL_PREFIX)
    p.add_argument("--alt", metavar="ARCHIVE2", help="the same package in the other format (goes with --alt-url)")
    p.add_argument("--alt-url", help="where the alternate archive is published")
    p.add_argument("--note", help="free text stored as _note")
    p.set_defaults(fn=cmd_catalog)

    p = sub.add_parser("check", help="compare a stack.json with the local archives it names")
    p.add_argument("catalog", metavar="stack.json")
    p.add_argument("archive", help="a local copy of an archive the catalogue names")
    p.add_argument("archive2", nargs="?", help="a local copy of the other one")
    p.set_defaults(fn=cmd_check)

    args = ap.parse_args(argv)
    try:
        # A console whose code page lacks a character of a member name gets it escaped instead of a crash.
        sys.stdout.reconfigure(errors="backslashreplace")
        sys.stderr.reconfigure(errors="backslashreplace")
    except (AttributeError, ValueError):
        pass
    try:
        return args.fn(args)
    except PackageError as e:
        return blocked([str(e)])
    except ToolMissing as e:
        print(str(e), file=sys.stderr)
        return 2
    except OSError as e:
        print("cannot read: %s" % e, file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
