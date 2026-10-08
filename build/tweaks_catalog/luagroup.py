"""Reader of the server's scene group scripts (lua/scene/<id>/scene<id>_group<gid>.lua).

It does NOT run Lua. A group script is a list of top-level table assignments (monsters, gadgets, triggers,
variables, suites, init_config) followed by the trigger functions; the reader tokenises the text, turns the
tables into Python values and cuts out every top-level function with its ScriptLib calls and its literal
comparisons -- which is all the trigger code of a Spiral Abyss scene consists of (see towersim).

    g = parse_text(text)
    g["tables"]["monsters"]              list of dicts, each with "__line" (1-based line of the entry's "{")
    g["functions"]["action_EVENT_X"]     {"line", "calls": [{"fn", "line", "args"}], "compares": [...]}

An expression the reader does not model (a call, arithmetic) is kept as Raw source text instead of failing:
the caller decides what an unreadable value means.
"""
import re

KEYWORDS_OPEN = {"function", "if", "do", "repeat"}
KEYWORDS_CLOSE = {"end", "until"}

_NUM = re.compile(r"0[xX][0-9a-fA-F]+|\d+\.?\d*(?:[eE][+-]?\d+)?|\.\d+(?:[eE][+-]?\d+)?")
_NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
_LONG_OPEN = re.compile(r"\[(=*)\[")
_OPS3 = ("...",)
_OPS2 = ("..", "==", "~=", "<=", ">=", "//", "::", "<<", ">>")
_CMP = r"(~=|==|>=|<=|<|>)"


class Tok(object):
    __slots__ = ("kind", "val", "line", "pos", "end")

    def __init__(self, kind, val, line, pos, end):
        self.kind = kind    # name | number | string | op | comment
        self.val = val
        self.line = line    # 1-based
        self.pos = pos      # character offsets in the text
        self.end = end


def tokenize(text):
    toks = []
    i = 0
    n = len(text)
    line = 1
    while i < n:
        c = text[i]
        if c == "\n":
            line += 1
            i += 1
            continue
        if c in " \t\r":
            i += 1
            continue
        if c == "-" and text.startswith("--", i):
            m = _LONG_OPEN.match(text, i + 2)
            if m:
                close = "]" + m.group(1) + "]"
                j = text.find(close, m.end())
                j = n if j < 0 else j + len(close)
            else:
                j = text.find("\n", i)
                j = n if j < 0 else j
            toks.append(Tok("comment", text[i:j].rstrip("\r"), line, i, j))
            line += text.count("\n", i, j)
            i = j
            continue
        if c == "[":
            m = _LONG_OPEN.match(text, i)
            if m:
                close = "]" + m.group(1) + "]"
                j = text.find(close, m.end())
                j = n if j < 0 else j
                toks.append(Tok("string", text[m.end():j], line, i, j + len(close)))
                line += text.count("\n", i, j)
                i = j + len(close)
                continue
        if c in "\"'":
            j = i + 1
            buf = []
            while j < n and text[j] != c:
                if text[j] == "\\" and j + 1 < n:
                    buf.append(text[j + 1])
                    j += 2
                    continue
                if text[j] == "\n":
                    break
                buf.append(text[j])
                j += 1
            toks.append(Tok("string", "".join(buf), line, i, j + 1))
            i = j + 1
            continue
        m = _NUM.match(text, i)
        if m and (c.isdigit() or (c == "." and i + 1 < n and text[i + 1].isdigit())):
            toks.append(Tok("number", m.group(0), line, i, m.end()))
            i = m.end()
            continue
        m = _NAME.match(text, i)
        if m:
            toks.append(Tok("name", m.group(0), line, i, m.end()))
            i = m.end()
            continue
        if text[i:i + 3] in _OPS3:
            toks.append(Tok("op", text[i:i + 3], line, i, i + 3))
            i += 3
            continue
        if text[i:i + 2] in _OPS2:
            toks.append(Tok("op", text[i:i + 2], line, i, i + 2))
            i += 2
            continue
        toks.append(Tok("op", c, line, i, i + 1))
        i += 1
    return toks


def _num(s):
    if s[:2] in ("0x", "0X"):
        return int(s, 16)
    try:
        return int(s)
    except ValueError:
        return float(s)


class Raw(object):
    """An expression the reader does not model (a call, arithmetic, ...): kept as source text."""

    def __init__(self, text, line):
        self.text = text
        self.line = line

    def __repr__(self):
        return "Raw(%r)" % self.text


class Ident(str):
    """A (dotted) identifier used as a value: EventType.EVENT_X, GadgetState.GearStart."""


class LuaList(list):
    def __init__(self, items, line):
        list.__init__(self, items)
        self.line = line


class _Parser(object):
    def __init__(self, text, toks):
        self.text = text
        self.toks = toks
        self.i = 0

    def peek(self, k=0):
        j = self.i + k
        return self.toks[j] if 0 <= j < len(self.toks) else None

    def is_op(self, v, k=0):
        t = self.peek(k)
        return t is not None and t.kind == "op" and t.val == v

    def is_name(self, v=None, k=0):
        t = self.peek(k)
        return t is not None and t.kind == "name" and (v is None or t.val == v)

    def parse_exp(self, stop=(",", ";", "}")):
        """Reads ONE value; falls back to Raw (the source text up to the next separator at depth 0)."""
        start = self.i
        t = self.peek()
        if t is None:
            return None
        val = None
        ok = False
        if t.kind == "op" and t.val == "{":
            val = self.parse_table()
            ok = True
        elif t.kind == "number":
            self.i += 1
            val = _num(t.val)
            ok = True
        elif t.kind == "op" and t.val == "-" and self.peek(1) is not None and self.peek(1).kind == "number":
            self.i += 2
            val = -_num(self.peek(-1).val)
            ok = True
        elif t.kind == "string":
            self.i += 1
            val = t.val
            ok = True
        elif t.kind == "name" and t.val in ("true", "false", "nil"):
            self.i += 1
            val = {"true": True, "false": False, "nil": None}[t.val]
            ok = True
        elif t.kind == "name" and t.val != "function":
            parts = [t.val]
            self.i += 1
            while self.is_op(".") and self.is_name(None, 1):
                parts.append(self.peek(1).val)
                self.i += 2
            val = Ident(".".join(parts))
            ok = True
        nxt = self.peek()
        # a plain value is followed by a separator, a closing bracket or the next statement
        if ok and (nxt is None or nxt.kind != "op" or nxt.val in stop or nxt.val == ")"):
            return val
        # not a plain value: swallow up to the separator at depth 0
        self.i = start
        depth = 0
        first = self.peek()
        while self.peek() is not None:
            t = self.peek()
            if t.kind == "op":
                if t.val in ("{", "(", "["):
                    depth += 1
                elif t.val in ("}", ")", "]"):
                    if depth == 0:
                        break
                    depth -= 1
                elif depth == 0 and t.val in stop:
                    break
            self.i += 1
        last = self.toks[self.i - 1] if self.i > start else first
        return Raw(self.text[first.pos:last.end], first.line)

    def parse_table(self):
        open_tok = self.peek()
        self.i += 1
        arr = []
        dct = {}
        while True:
            t = self.peek()
            if t is None:
                break
            if t.kind == "op" and t.val == "}":
                self.i += 1
                break
            if t.kind == "op" and t.val in (",", ";"):
                self.i += 1
                continue
            if t.kind == "name" and self.is_op("=", 1):
                self.i += 2
                dct[t.val] = self.parse_exp()
                continue
            if t.kind == "op" and t.val == "[":
                # [exp] = exp
                self.i += 1
                k = self.parse_exp(stop=("]",))
                if self.is_op("]"):
                    self.i += 1
                if self.is_op("="):
                    self.i += 1
                dct[k.text if isinstance(k, Raw) else k] = self.parse_exp()
                continue
            arr.append(self.parse_exp())
        if dct:
            if arr:
                dct["__array"] = arr
            dct["__line"] = open_tok.line
            return dct
        return LuaList(arr, open_tok.line)


def plain(v):
    """A parsed value without the reader's bookkeeping."""
    if isinstance(v, Raw):
        return {"__raw": v.text}
    if isinstance(v, Ident):
        return str(v)
    if isinstance(v, dict):
        return {str(k): plain(x) for k, x in v.items()}
    if isinstance(v, list):
        return [plain(x) for x in v]
    return v


def unread(g):
    """What parse_text could not turn into a value: [(1-based line, source text)] of the top-level
    statements it skipped and of every Raw expression in a table or a call."""
    out = list(g["unknown"])

    def walk(v, line):
        if isinstance(v, Raw):
            out.append((v.line, v.text))
        elif isinstance(v, dict):
            if "__raw" in v:
                out.append((line, v["__raw"]))
            for x in v.values():
                walk(x, line)
        elif isinstance(v, list):
            for x in v:
                walk(x, line)

    walk(g["tables"], None)
    for f in g["functions"].values():
        for c in f["calls"]:
            walk(c["args"], c["line"])
    return out


def _strip_comments(text, toks_all):
    """The same text with every comment replaced by spaces (the line structure is kept)."""
    out = list(text)
    for t in toks_all:
        if t.kind == "comment":
            for k in range(t.pos, t.end):
                if out[k] not in "\r\n":
                    out[k] = " "
    return "".join(out)


def _split_args(text, toks, lo, hi):
    """toks[lo:hi] = the tokens between a call's parentheses -> the parsed arguments."""
    cuts = []
    depth = 0
    start = lo
    for k in range(lo, hi):
        t = toks[k]
        if t.kind == "op":
            if t.val in ("{", "(", "["):
                depth += 1
            elif t.val in ("}", ")", "]"):
                depth -= 1
            elif t.val == "," and depth == 0:
                cuts.append((start, k))
                start = k + 1
    if hi > lo:
        cuts.append((start, hi))
    args = []
    for a, b in cuts:
        if b <= a:
            args.append(None)
            continue
        sub = _Parser(text, toks[a:b])
        val = sub.parse_exp(stop=(",",))
        if sub.i != b - a:
            val = Raw(text[toks[a].pos:toks[b - 1].end], toks[a].line)
        args.append(plain(val))
    return args


def _function_info(name, text, code, toks, lo, hi, fstart, fend):
    calls = []
    k = lo
    while k < hi:
        t = toks[k]
        if (t.kind == "name" and t.val == "ScriptLib" and k + 3 < hi and toks[k + 1].kind == "op"
                and toks[k + 1].val == "." and toks[k + 2].kind == "name" and toks[k + 3].kind == "op"
                and toks[k + 3].val == "("):
            depth = 0
            j = k + 3
            while j < hi:
                tj = toks[j]
                if tj.kind == "op":
                    if tj.val in ("(", "{", "["):
                        depth += 1
                    elif tj.val in (")", "}", "]"):
                        depth -= 1
                        if depth == 0:
                            break
                j += 1
            calls.append({"fn": toks[k + 2].val, "line": t.line, "args": _split_args(text, toks, k + 4, j)})
            k = j + 1
            continue
        k += 1
    # the literal comparisons of the body, read from the code with its comments blanked
    body = code[fstart.pos:fend.end]
    base_line = fstart.line
    compares = []

    def line_of(off):
        return base_line + body.count("\n", 0, off)

    for m in re.finditer(r"(-?\d+)\s*" + _CMP + r"\s*evt\.(param\d|source_eid|target_eid|source_name|uid)", body):
        compares.append({"kind": "evt", "field": m.group(3), "op": m.group(2), "value": int(m.group(1)),
                         "line": line_of(m.start())})
    for m in re.finditer(r"evt\.(param\d|source_eid|target_eid|uid)\s*" + _CMP + r"\s*(-?\d+)", body):
        compares.append({"kind": "evt", "field": m.group(1), "op": m.group(2), "value": int(m.group(3)),
                         "line": line_of(m.start())})
    for m in re.finditer(r"evt\.source_name\s*" + _CMP + r"\s*\"([^\"]*)\"", body):
        compares.append({"kind": "evt", "field": "source_name", "op": m.group(1), "value": m.group(2),
                         "line": line_of(m.start())})
    for m in re.finditer(r"\"([^\"]*)\"\s*" + _CMP + r"\s*evt\.source_name", body):
        compares.append({"kind": "evt", "field": "source_name", "op": m.group(2), "value": m.group(1),
                         "line": line_of(m.start())})
    for m in re.finditer(r"ScriptLib\.(GetGroupMonsterCount(?:ByGroupId)?)\(\s*context\s*(?:,\s*(\d+)\s*)?\)\s*"
                         + _CMP + r"\s*(-?\d+)", body):
        compares.append({"kind": "monster_count", "fn": m.group(1),
                         "group": int(m.group(2)) if m.group(2) else None, "op": m.group(3),
                         "value": int(m.group(4)), "line": line_of(m.start())})
    for m in re.finditer(r"ScriptLib\.(GetGroupVariableValue(?:ByGroup)?)\(\s*context\s*,\s*\"([^\"]+)\"\s*"
                         r"(?:,\s*(\d+)\s*)?\)\s*" + _CMP + r"\s*(-?\d+)", body):
        compares.append({"kind": "variable", "fn": m.group(1), "name": m.group(2),
                         "group": int(m.group(3)) if m.group(3) else None, "op": m.group(4),
                         "value": int(m.group(5)), "line": line_of(m.start())})
    return {"name": name, "line": fstart.line, "end_line": fend.line, "calls": calls, "compares": compares}


def parse_text(text):
    toks_all = tokenize(text)
    toks = [t for t in toks_all if t.kind != "comment"]
    code = _strip_comments(text, toks_all)
    p = _Parser(text, toks)
    tables = {}
    functions = {}
    unknown = []
    while p.peek() is not None:
        t = p.peek()
        is_local = False
        if t.kind == "name" and t.val == "local":
            is_local = True
            p.i += 1
            t = p.peek()
            if t is None:
                break
        if t.kind == "name" and t.val == "function":
            # function a.b:c(args) ... end
            fstart = t
            p.i += 1
            parts = []
            while p.peek() is not None and not p.is_op("("):
                parts.append(p.peek().val)
                p.i += 1
            name = "".join(parts)
            depth = 1
            body_lo = p.i
            while p.peek() is not None and depth > 0:
                tt = p.peek()
                if tt.kind == "name":
                    if tt.val in KEYWORDS_OPEN:
                        depth += 1
                    elif tt.val in KEYWORDS_CLOSE:
                        depth -= 1
                p.i += 1
            functions[name] = _function_info(name, text, code, toks, body_lo, p.i, fstart, toks[p.i - 1])
            continue
        if t.kind == "name" and p.is_op("=", 1):
            p.i += 2
            val = p.parse_exp(stop=(",", ";", "}"))
            if not is_local:
                tables[t.val] = val
            continue
        if t.kind == "name" and t.val == "require":
            # require "X"   |   require("X")
            k = 2 if p.is_op("(", 1) else 1
            s = p.peek(k)
            if s is not None and s.kind == "string":
                p.i += k + 1
                if k == 2 and p.is_op(")"):
                    p.i += 1
                continue
        unknown.append((t.line, t.val))
        p.i += 1
    return {"tables": tables, "functions": functions, "unknown": unknown}
