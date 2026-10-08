"""A vendor server stack as the catalogue generator reads it: nothing but proven vendor bytes.

The vendor ships server/data/server_data_version_md5_list.txt ("<md5>  ./<path>" for every txt table, Lua
script and json config under server/data). It is the generator's index AND its proof: a file is found
through the list, never by listing a folder, and its bytes are used only when their md5 is the listed one.
So a stray file is never seen, a missing or edited file stops the run, and the catalogue is a pure function
of the vendor's data.
"""
import hashlib
import json
import os
import re

DATA_DIR = "server/data"
MD5_LIST = "server_data_version_md5_list.txt"
# The agent keeps the stack's own copy of a table it replaces as <file>.orig; it is accepted in the
# table's place only when it has the listed md5, and never for a file the catalogue lets the agent rewrite.
BACKUP_SUFFIX = ".orig"
# A vendor archive may hold the data one folder deeper (server/data/<name>-output_<n>-server-data/); the
# agent moves it up before anything reads it, so the catalogue always names the flat paths.
_NESTED_DATA = re.compile(r"-output_\d+-server-data$")

_LIST_LINE = re.compile(r"^([0-9a-f]{32})  \./(.+)$")
_INT = re.compile(r"[0-9]+")
# The vendor's json configs are written for a reader that takes comments and a comma in front of a closing
# bracket. Both are removed outside of strings (a string is matched first and kept as it is).
_JSON_COMMENT = re.compile(r'"(?:[^"\\]|\\.)*"|//[^\n]*|/\*.*?\*/', re.S)
_JSON_LAST_COMMA = re.compile(r'"(?:[^"\\]|\\.)*"|,(?=\s*[}\]])', re.S)


class CatalogError(Exception):
    """The stack cannot yield a catalogue: it is not the vendor's data, or the data lacks the shape the
    feature relies on. The message names the file / row."""


def is_int(cell):
    return _INT.fullmatch(cell) is not None


def to_int(cell, what):
    """A cell that must hold a plain non-negative integer."""
    cell = cell.strip()
    if not is_int(cell):
        raise CatalogError("%s: %r is not a number" % (what, cell))
    return int(cell)


class Table(object):
    """One txt table: `header` (cells of line 1) and `rows` = [(1-based line number, cells)]; `rel` is its
    stack-relative path."""

    def __init__(self, rel, header, rows):
        self.rel = rel
        self.header = header
        self.rows = rows
        self._index = {}
        for i, name in enumerate(header):
            self._index.setdefault(name, i)

    def has(self, name):
        return name in self._index

    def col(self, name):
        """Index of the column with exactly this header; the server finds its columns by name too."""
        if name not in self._index:
            raise CatalogError("%s: no column %s" % (self.rel, name))
        return self._index[name]


class Stack(object):
    def __init__(self, version, root):
        self.version = version
        self.root = root.replace("\\", "/").rstrip("/")
        self.data = self._data_folder()
        self.from_backup = set()
        self._tables = {}
        self.listed = self._read_list()

    def _data_folder(self):
        flat = self.root + "/" + DATA_DIR
        if os.path.isfile(flat + "/" + MD5_LIST):
            return flat
        try:
            nested = [n for n in sorted(os.listdir(flat))
                      if _NESTED_DATA.search(n) and os.path.isfile("%s/%s/%s" % (flat, n, MD5_LIST))]
        except OSError:
            nested = []
        if len(nested) != 1:
            raise CatalogError("%s/%s: not found -- not a vendor stack folder: %s" % (DATA_DIR, MD5_LIST, self.root))
        return flat + "/" + nested[0]

    def _read_list(self):
        try:
            with open(self.data + "/" + MD5_LIST, "rb") as f:
                text = f.read().decode("utf-8")
        except (OSError, UnicodeDecodeError) as e:
            raise CatalogError("%s/%s: cannot be read (%s)" % (DATA_DIR, MD5_LIST, e))
        listed = {}
        for n, line in enumerate(text.split("\n"), 1):
            line = line.rstrip("\r")
            if line == "":
                continue
            m = _LIST_LINE.match(line)
            if not m:
                raise CatalogError("%s/%s:%d: not a '<md5>  ./<path>' line" % (DATA_DIR, MD5_LIST, n))
            if m.group(2) in listed:
                raise CatalogError("%s/%s:%d: %s is listed twice" % (DATA_DIR, MD5_LIST, n, m.group(2)))
            listed[m.group(2)] = m.group(1)
        if not listed:
            raise CatalogError("%s/%s: lists no file" % (DATA_DIR, MD5_LIST))
        return listed

    # ---- names ----------------------------------------------------------------------------
    @staticmethod
    def path(rel):
        """Data-relative name ("txt/RewardData.txt") -> the stack-relative path the catalogue carries."""
        return DATA_DIR + "/" + rel

    def has(self, rel):
        return rel in self.listed

    def md5(self, rel):
        if rel not in self.listed:
            raise CatalogError("%s: not in the vendor's md5 list" % self.path(rel))
        return self.listed[rel]

    def names(self, prefix):
        """Every listed file below a folder ("txt/", "lua/scene/33701/"), sorted."""
        return sorted(rel for rel in self.listed if rel.startswith(prefix))

    def table_names(self):
        """The name of every listed txt table, sorted."""
        return [rel[4:-4] for rel in self.names("txt/") if rel.endswith(".txt") and "/" not in rel[4:]]

    # ---- bytes ----------------------------------------------------------------------------
    def read(self, rel):
        """The vendor's bytes of one listed file; CatalogError when the stack does not hold them."""
        want = self.md5(rel)
        full = self.data + "/" + rel
        raw = self._bytes(full)
        if raw is None:
            raise CatalogError("%s: listed by the vendor but missing" % self.path(rel))
        got = hashlib.md5(raw).hexdigest()
        if got != want:
            raw = self._bytes(full + BACKUP_SUFFIX)
            if raw is None or hashlib.md5(raw).hexdigest() != want:
                raise CatalogError("%s: not the vendor's file (md5 %s, the vendor's list says %s)"
                                   % (self.path(rel), got, want))
            self.from_backup.add(rel)
        return raw

    @staticmethod
    def _bytes(full):
        try:
            with open(full, "rb") as f:
                return f.read()
        except OSError:
            return None

    def require_live(self, rel):
        """-> the md5 of a file the catalogue lets the agent rewrite. The file itself must be the vendor's:
        the agent takes what it finds on a box for the base of its edits and compares it with this md5."""
        self.read(rel)
        if rel in self.from_backup:
            raise CatalogError("%s: not the vendor's file (only its %s copy is)"
                               % (self.path(rel), BACKUP_SUFFIX))
        return self.listed[rel]

    def _text(self, rel):
        try:
            return self.read(rel).decode("utf-8")
        except UnicodeDecodeError as e:
            raise CatalogError("%s: not UTF-8 (%s)" % (self.path(rel), e))

    def revision(self):
        """The vendor's revision number of the data (server_data_version.txt), for the build log."""
        return self._text("server_data_version.txt").strip() if self.has("server_data_version.txt") else "?"

    def json(self, rel):
        """One listed json config ("json/monster/<name>.json") as an object."""
        raw = self.read(rel)
        try:
            text = raw.decode("utf-8-sig")
            for strip in (_JSON_COMMENT, _JSON_LAST_COMMA):
                text = strip.sub(lambda m: m.group(0) if m.group(0).startswith('"') else "", text)
            value = json.loads(text, strict=False)
        except ValueError as e:
            raise CatalogError("%s: not a json config the generator can read (%s)" % (self.path(rel), e))
        if not isinstance(value, dict):
            raise CatalogError("%s: not a json object" % self.path(rel))
        return value

    # ---- tables ---------------------------------------------------------------------------
    def table(self, name, keep=True):
        """txt/<name>.txt as a Table. The loader's own rules are enforced: UTF-8, a bare empty line is
        skipped, every other line has exactly the header's cell count. `keep` = False parses the table
        for one pass over it without holding on to it."""
        rel = "txt/%s.txt" % name
        if rel in self._tables:
            return self._tables[rel]
        header = None
        rows = []
        for n, line in enumerate(self._text(rel).split("\n"), 1):
            if line == "":
                continue
            cells = line.rstrip("\r").split("\t")
            if header is None:
                header = cells
            elif len(cells) != len(header):
                raise CatalogError("%s:%d: %d cells, the header has %d"
                                   % (self.path(rel), n, len(cells), len(header)))
            else:
                rows.append((n, cells))
        if header is None:
            raise CatalogError("%s: empty" % self.path(rel))
        table = Table(self.path(rel), header, rows)
        if keep:
            self._tables[rel] = table
        return table

    def header(self, name):
        """The header cells of txt/<name>.txt."""
        rel = "txt/%s.txt" % name
        if rel in self._tables:
            return self._tables[rel].header
        return self._text(rel).split("\n", 1)[0].rstrip("\r").split("\t")
