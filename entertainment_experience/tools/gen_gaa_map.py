#!/usr/bin/env python3
"""gen_gaa_map.py - the interactive map's Golden Apple Archipelago datasets, generated from the GIO
private server's own scene scripts (read-only): 1.6 "Midsummer Island Adventure" (scene 4) and
2.8 "Summertime Odyssey" (scene 9).

Both clients turn a world position into a map ("level") position with MoleMole.Miscs.GenLevelPos,
which is Vector2(-world.Z, world.X) and reads no scene data, so the server's gadget and monster
coordinates convert offline. The emitted files are therefore in level coordinates and must be loaded
WITHOUT ApplyScaling (that step exists to fit the official-map coordinates of the Akebi datasets).

Stdlib only, Python 3.8+.

  python gen_gaa_map.py emit  --out DIR      # + the two slim files res.rc embeds (written into res/)
  python gen_gaa_map.py build [--out DIR]    # long form (world + level), Akebi cross-check, report
  python gen_gaa_map.py survey [--out DIR]   # per-id inventory of every scene entity (TSV)

Server data (per version; the defaults are the owner's local copies of the two vendor archives):
  1.6 scene 4 : --lua16 D:/servere_test/1.6_live/server/data/lua/scene/4   --txt16 <same stack>/data/txt
  2.8 scene 9 : --lua28 D:/servere_test/2.8_live/server/data/2.8_live-output_9464149-server-data/lua/scene/9
                --txt28 <same>/txt
"""
import argparse
import collections
import glob
import json
import math
import os
import re
import sys

# ============================================================================
# Lua table-literal evaluator (subset of Lua 5.x sufficient for hk4e group scripts)
# ============================================================================


class Sym(str):
    """Unresolved identifier / expression kept symbolically (EventType.EVENT_ANY, call results...)."""


class LTable(dict):
    """Lua table: positional fields get integer keys 1..n."""

    def arr(self):
        out, i = [], 1
        while i in self:
            out.append(self[i])
            i += 1
        return out

    def vals(self):
        """Every integer-keyed entry, in key order.

        hk4e group scripts write an entity list either positionally or with explicit keys
        (`[20002] = { config_id = 20002, ... }`), and 91 of the 859 scene-9 groups use the keyed form.
        arr() stops at the first gap, so it silently returns nothing for those - always read entity
        lists through this.
        """
        return [self[k] for k in sorted(k for k in self if isinstance(k, int))]


class ParseError(Exception):
    pass


KEYWORDS = {'and', 'break', 'do', 'else', 'elseif', 'end', 'false', 'for', 'function', 'goto', 'if', 'in',
            'local', 'nil', 'not', 'or', 'repeat', 'return', 'then', 'true', 'until', 'while'}

TOKEN_RE = re.compile(r'''
   (?P<ws>\s+)
 | (?P<lcomment>--\[(?P<leq>=*)\[.*?\](?P=leq)\])
 | (?P<comment>--[^\n]*)
 | (?P<lstring>\[(?P<seq>=*)\[.*?\](?P=seq)\])
 | (?P<string>"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])*')
 | (?P<number>0[xX][0-9a-fA-F]+|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)
 | (?P<name>[A-Za-z_][A-Za-z_0-9]*)
 | (?P<op>\.\.\.|\.\.|==|~=|<=|>=|<<|>>|//|::|[-+*/%^\#<>=(){}\[\];:,.&|~])
 | (?P<bad>.)
''', re.S | re.X)


class Tok:
    __slots__ = ('kind', 'val', 'pos')

    def __init__(self, kind, val, pos):
        self.kind, self.val, self.pos = kind, val, pos


def _unescape(s):
    body = s[1:-1]
    out, i = [], 0
    while i < len(body):
        c = body[i]
        if c == '\\' and i + 1 < len(body):
            n = body[i + 1]
            m = {'n': '\n', 't': '\t', 'r': '\r', '\\': '\\', '"': '"', "'": "'", '0': '\0'}
            if n.isdigit():
                j = i + 1
                while j < len(body) and j < i + 4 and body[j].isdigit():
                    j += 1
                out.append(chr(int(body[i + 1:j])))
                i = j
                continue
            out.append(m.get(n, n))
            i += 2
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def tokenize(text):
    toks, warnings = [], []
    for m in TOKEN_RE.finditer(text):
        k = m.lastgroup
        if k in ('ws', 'comment', 'lcomment'):
            continue
        v = m.group(k)
        if k == 'bad':
            warnings.append('bad char %r at %d' % (v, m.start()))
            continue
        if k == 'string':
            v = _unescape(v)
            k = 'string'
        elif k == 'lstring':
            eq = len(m.group('seq'))
            v = v[2 + eq:-(2 + eq)]
            k = 'string'
        elif k == 'number':
            if v.lower().startswith('0x'):
                v = int(v, 16)
            elif re.fullmatch(r'\d+', v):
                v = int(v)
            else:
                v = float(v)
        toks.append(Tok(k, v, m.start()))
    return toks, warnings


BINPRI = {'or': (1, 1), 'and': (2, 2),
          '<': (3, 3), '>': (3, 3), '<=': (3, 3), '>=': (3, 3), '~=': (3, 3), '==': (3, 3),
          '|': (4, 4), '~': (5, 5), '&': (6, 6), '<<': (7, 7), '>>': (7, 7),
          '..': (9, 8), '+': (10, 10), '-': (10, 10),
          '*': (11, 11), '/': (11, 11), '//': (11, 11), '%': (11, 11), '^': (14, 13)}
UNARY_PRI = 12


class LuaChunk:
    """Executes the declarative part of a Lua chunk: assignments of literal tables/values.
    Function bodies and control blocks are skipped (their text spans are kept in code_spans)."""

    def __init__(self, text, name='?'):
        self.text = text
        self.name = name
        self.toks, self.warnings = tokenize(text)
        self.i = 0
        self.env = {}
        self.code_spans = []
        self.requires = []

    # ---- token helpers
    def eof(self):
        return self.i >= len(self.toks)

    def peek(self, k=0):
        j = self.i + k
        return self.toks[j] if j < len(self.toks) else Tok('eof', None, len(self.text))

    def next(self):
        t = self.peek()
        self.i += 1
        return t

    def is_op(self, v, k=0):
        t = self.peek(k)
        return t.kind == 'op' and t.val == v

    def is_kw(self, v, k=0):
        t = self.peek(k)
        return t.kind == 'name' and t.val == v

    def expect_op(self, v):
        t = self.next()
        if not (t.kind == 'op' and t.val == v):
            raise ParseError('expected %r got %r at line %d' % (v, t.val, self.line(t.pos)))

    def expect_name(self):
        t = self.next()
        if t.kind != 'name' or t.val in KEYWORDS:
            raise ParseError('expected name got %r at line %d' % (t.val, self.line(t.pos)))
        return t.val

    def line(self, pos):
        return self.text.count('\n', 0, pos) + 1

    def at_line_start(self, t):
        return t.pos == 0 or self.text[t.pos - 1] == '\n'

    # ---- statements
    def run(self):
        while not self.eof():
            start = self.i
            try:
                self.statement()
            except ParseError as e:
                self.warnings.append('%s: %s' % (self.name, e))
                self.i = max(self.i, start + 1)
                while not self.eof() and not self.at_line_start(self.peek()):
                    self.i += 1
        return self

    def statement(self):
        t = self.peek()
        if t.kind == 'op' and t.val == ';':
            self.i += 1
            return
        if t.kind == 'name' and t.val == 'local':
            self.i += 1
            if self.is_kw('function'):
                self.skip_block()
                return
            names = [self.expect_name()]
            while self.is_op(','):
                self.i += 1
                names.append(self.expect_name())
            if self.is_op('='):
                self.i += 1
                vals = self.explist()
                for n, v in zip(names, vals):
                    self.env[n] = v
            return
        if t.kind == 'name' and t.val in ('function', 'if', 'do', 'while', 'for', 'repeat'):
            self.skip_block()
            return
        if t.kind == 'name' and t.val == 'return':
            self.i = len(self.toks)
            return
        if t.kind == 'name' and t.val not in KEYWORDS:
            self.name_statement()
            return
        raise ParseError('unexpected token %r at line %d' % (t.val, self.line(t.pos)))

    def name_statement(self):
        first = self.next()
        path = [first.val]
        called = False
        while True:
            if self.is_op('.'):
                self.i += 1
                path.append(self.expect_name())
            elif self.is_op('['):
                self.i += 1
                k = self.expr()
                self.expect_op(']')
                path.append(k)
            elif self.is_op(':'):
                self.i += 1
                self.expect_name()
                self.call_args()
                called = True
            elif self.is_op('(') or self.is_op('{') or self.peek().kind == 'string':
                if path == ['require'] and self.peek().kind == 'string':
                    self.requires.append(self.peek().val)
                self.call_args()
                called = True
            else:
                break
        if self.is_op('=') or self.is_op(','):
            targets = [path]
            while self.is_op(','):
                self.i += 1
                p = [self.expect_name()]
                while self.is_op('.'):
                    self.i += 1
                    p.append(self.expect_name())
                targets.append(p)
            self.expect_op('=')
            vals = self.explist()
            for p, v in zip(targets, vals):
                self.assign(p, v)
        elif not called:
            raise ParseError('dangling expression %r at line %d' % (first.val, self.line(first.pos)))

    def assign(self, path, v):
        if len(path) == 1:
            self.env[path[0]] = v
            return
        cur = self.env.get(path[0])
        for k in path[1:-1]:
            if not isinstance(cur, dict):
                return
            cur = cur.get(k)
        if isinstance(cur, dict):
            cur[path[-1]] = v

    def skip_block(self):
        """Skip a function/if/do/while/for/repeat block starting at the current token."""
        start_pos = self.peek().pos
        depth = 0
        while not self.eof():
            t = self.next()
            if t.kind != 'name':
                continue
            if t.val in ('function', 'if', 'do', 'repeat'):
                depth += 1
            elif t.val in ('end', 'until'):
                depth -= 1
                if depth <= 0:
                    if t.val == 'until':
                        self.expr()
                    break
        self.code_spans.append((start_pos, self.peek().pos))

    # ---- expressions
    def explist(self):
        vals = [self.expr()]
        while self.is_op(','):
            self.i += 1
            vals.append(self.expr())
        return vals

    def expr(self):
        return self.subexpr(0)

    def subexpr(self, limit):
        t = self.peek()
        if (t.kind == 'name' and t.val == 'not') or (t.kind == 'op' and t.val in ('-', '#', '~')):
            self.i += 1
            v = self.subexpr(UNARY_PRI)
            if t.val == '-' and isinstance(v, (int, float)) and not isinstance(v, bool):
                v = -v
            elif t.val == 'not':
                v = (v is None or v is False)
            elif t.val == '#' and isinstance(v, LTable):
                v = len(v.arr())
            else:
                v = Sym('%s(%s)' % (t.val, v))
        else:
            v = self.simpleexp()
        while True:
            t = self.peek()
            op = t.val if (t.kind == 'op' or (t.kind == 'name' and t.val in ('and', 'or'))) else None
            if op not in BINPRI or BINPRI[op][0] <= limit:
                break
            self.i += 1
            r = self.subexpr(BINPRI[op][1])
            v = self.binop(op, v, r)
        return v

    @staticmethod
    def binop(op, a, b):
        num = lambda x: isinstance(x, (int, float)) and not isinstance(x, bool)
        if num(a) and num(b):
            try:
                if op == '+': return a + b
                if op == '-': return a - b
                if op == '*': return a * b
                if op == '/': return a / b
                if op == '//': return a // b
                if op == '%': return a % b
                if op == '^': return a ** b
            except ZeroDivisionError:
                return Sym('nan')
        if op == '..' and isinstance(a, (str, int, float)) and isinstance(b, (str, int, float)):
            return '%s%s' % (a, b)
        if op == 'and':
            return b if (a is not None and a is not False) else a
        if op == 'or':
            return a if (a is not None and a is not False) else b
        return Sym('(%s %s %s)' % (a, op, b))

    def simpleexp(self):
        t = self.peek()
        if t.kind == 'number':
            self.i += 1
            return t.val
        if t.kind == 'string':
            self.i += 1
            return t.val
        if t.kind == 'name':
            if t.val == 'nil':
                self.i += 1
                return None
            if t.val == 'true':
                self.i += 1
                return True
            if t.val == 'false':
                self.i += 1
                return False
            if t.val == 'function':
                self.skip_block()
                return Sym('function')
        if t.kind == 'op' and t.val == '...':
            self.i += 1
            return Sym('...')
        if t.kind == 'op' and t.val == '{':
            return self.table()
        return self.suffixedexp()

    def primaryexp(self):
        t = self.next()
        if t.kind == 'name' and t.val not in KEYWORDS:
            return self.env[t.val] if t.val in self.env else Sym(t.val)
        if t.kind == 'op' and t.val == '(':
            v = self.expr()
            self.expect_op(')')
            return v
        raise ParseError('unexpected %r at line %d' % (t.val, self.line(t.pos)))

    def suffixedexp(self):
        v = self.primaryexp()
        while True:
            if self.is_op('.'):
                self.i += 1
                v = self.index(v, self.expect_name())
            elif self.is_op('['):
                self.i += 1
                k = self.expr()
                self.expect_op(']')
                v = self.index(v, k)
            elif self.is_op(':'):
                self.i += 1
                self.expect_name()
                self.call_args()
                v = Sym('call')
            elif self.is_op('(') or self.is_op('{') or self.peek().kind == 'string':
                self.call_args()
                v = Sym('call')
            else:
                return v

    @staticmethod
    def index(v, k):
        if isinstance(k, float) and k.is_integer():
            k = int(k)
        if isinstance(v, dict):
            return v.get(k)
        return Sym('%s.%s' % (v, k))

    def call_args(self):
        if self.peek().kind == 'string':
            self.i += 1
            return
        if self.is_op('{'):
            self.table()
            return
        self.expect_op('(')
        if not self.is_op(')'):
            self.explist()
        self.expect_op(')')

    def table(self):
        self.expect_op('{')
        t, n = LTable(), 1
        while not self.is_op('}'):
            if self.eof():
                raise ParseError('unterminated table')
            if self.is_op('['):
                self.i += 1
                k = self.expr()
                self.expect_op(']')
                self.expect_op('=')
                if isinstance(k, float) and k.is_integer():
                    k = int(k)
                t[k] = self.expr()
            elif self.peek().kind == 'name' and self.peek().val not in KEYWORDS and self.is_op('=', 1):
                k = self.next().val
                self.i += 1
                t[k] = self.expr()
            else:
                t[n] = self.expr()
                n += 1
            if self.is_op(',') or self.is_op(';'):
                self.i += 1
            elif not self.is_op('}'):
                raise ParseError('expected , or } in table at line %d' % self.line(self.peek().pos))
        self.i += 1
        return t

    def code_text(self):
        return '\n'.join(self.text[a:b] for a, b in self.code_spans)


def read_text(path):
    with open(path, 'rb') as f:
        raw = f.read()
    return raw.decode('utf-8-sig', errors='replace')


def lua_file(path):
    return LuaChunk(read_text(path), os.path.basename(path)).run()


# ============================================================================
# Excel (TSV) tables
# ============================================================================

def read_tsv(path):
    rows = []
    text = read_text(path)
    lines = text.split('\n')
    header = lines[0].rstrip('\r').split('\t')
    for ln in lines[1:]:
        ln = ln.rstrip('\r')
        if not ln:
            continue
        rows.append(ln.split('\t'))
    return header, rows


def col(header, *names):
    for n in names:
        if n in header:
            return header.index(n)
    return None


ENTITY_TYPE = {0: 'None', 1: 'Avatar', 2: 'Monster', 3: 'Bullet', 4: 'AttackPhyisicalUnit', 5: 'AOE', 6: 'Camera',
               7: 'EnviroArea', 8: 'Equip', 9: 'MonsterEquip', 10: 'Grass', 11: 'Level', 12: 'NPC',
               13: 'TransPointFirst', 14: 'TransPointFirstGadget', 15: 'TransPointSecond',
               16: 'TransPointSecondGadget', 17: 'DropItem', 18: 'Field', 19: 'Gadget', 20: 'Water',
               21: 'GatherPoint', 22: 'GatherObject', 23: 'AirflowField', 24: 'SpeedupField', 25: 'Gear',
               26: 'Chest', 27: 'EnergyBall', 28: 'ElemCrystal', 29: 'Timeline', 30: 'Worktop', 31: 'Team',
               32: 'Platform', 33: 'AmberWind', 34: 'EnvAnimal', 35: 'SealGadget', 36: 'Tree', 37: 'Bush',
               38: 'QuestGadget', 39: 'Lightning', 40: 'RewardPoint', 41: 'RewardStatue', 42: 'MPLevel',
               43: 'WindSeed', 44: 'MpPlayRewardPoint', 45: 'ViewPoint', 46: 'RemoteAvatar',
               47: 'GeneralRewardPoint', 48: 'PlayTeam', 49: 'OfferingGadget', 50: 'EyePoint', 51: 'MiracleRing',
               52: 'Foundation', 53: 'WidgetGadget', 54: 'Vehicle', 55: 'DangerZone', 56: 'EchoShell',
               57: 'HomeGatherObject', 58: 'Projector', 59: 'Screen', 60: 'CustomTile', 61: 'FishPool',
               62: 'FishRod', 63: 'CustomGadget', 64: 'RoguelikeOperatorGadget', 65: 'ActivityInteractGadget',
               66: 'SubEquip', 67: 'UIInteractGadget', 99: 'PlaceHolder'}


def load_gadget_table(txt_dir):
    out = {}
    for p in sorted(glob.glob(os.path.join(txt_dir, 'GadgetData_*.txt'))):
        header, rows = read_tsv(p)
        ci, cn, ct, cj = col(header, 'ID'), col(header, '名称$text_name_Name'), col(header, '类型'), col(header, 'JSON名称')
        for r in rows:
            if not r or not r[ci].strip().isdigit():
                continue
            gid = int(r[ci])
            typ = r[ct].strip() if ct is not None and ct < len(r) else ''
            out[gid] = {
                'name_cn': r[cn] if cn is not None and cn < len(r) else '',
                'type': int(typ) if typ.isdigit() else None,
                'type_name': ENTITY_TYPE.get(int(typ), typ) if typ.isdigit() else typ,
                'json': r[cj] if cj is not None and cj < len(r) else '',
                'table': os.path.basename(p),
            }
    return out


def load_monster_table(txt_dir):
    out = {}
    p = os.path.join(txt_dir, 'MonsterData.txt')
    if not os.path.exists(p):
        return out
    header, rows = read_tsv(p)
    ci, cn, cp, ct, cc = (col(header, 'ID'), col(header, '名称$text_name_Name'), col(header, 'Prefab路径'),
                          col(header, '类型'), col(header, '战斗Config'))
    for r in rows:
        if not r or not r[ci].strip().isdigit():
            continue
        prefab = r[cp] if cp is not None and cp < len(r) else ''
        out[int(r[ci])] = {
            'name_cn': r[cn] if cn is not None and cn < len(r) else '',
            'prefab': prefab.split('/')[-1],
            'type': r[ct] if ct is not None and ct < len(r) else '',
            'combat': r[cc] if cc is not None and cc < len(r) else '',
        }
    return out


def load_npc_table(txt_dir):
    out = {}
    p = os.path.join(txt_dir, 'NpcData.txt')
    if not os.path.exists(p):
        return out
    header, rows = read_tsv(p)
    ci, cn, cj = col(header, 'ID'), col(header, '名称$text_name_Name'), col(header, 'JSON名称')
    for r in rows:
        if not r or not r[ci].strip().isdigit():
            continue
        out[int(r[ci])] = {'name_cn': r[cn] if cn is not None and cn < len(r) else '',
                           'json': r[cj] if cj is not None and cj < len(r) else ''}
    return out


# ============================================================================
# Scene loading
# ============================================================================

KIND_ID = {'gadgets': 'gadget_id', 'monsters': 'monster_id', 'npcs': 'npc_id'}
CREATE_RE = re.compile(r'Create(Gadget|Monster)\s*\(\s*context\s*,\s*\{\s*config_id\s*=\s*([A-Za-z_][\w.]*|\d+)')


def pos3(v):
    if isinstance(v, dict):
        return tuple(float(v.get(k) or 0.0) if isinstance(v.get(k), (int, float)) else 0.0 for k in ('x', 'y', 'z'))
    return (0.0, 0.0, 0.0)


def plain(v):
    """LTable/Sym -> JSON-friendly."""
    if isinstance(v, LTable):
        a = v.arr()
        if len(a) == len(v):
            return [plain(x) for x in a]
        return {str(k): plain(x) for k, x in v.items()}
    if isinstance(v, dict):
        return {str(k): plain(x) for k, x in v.items()}
    if isinstance(v, Sym):
        return str(v)
    return v


def suite_list(env):
    """Return list of suites as dicts {monsters:set, gadgets:set, npcs:set, rand_weight}."""
    out = []
    src = env.get('suites')
    disk = False
    if not isinstance(src, dict):
        src = env.get('suite_disk')
        disk = True
    if not isinstance(src, dict):
        return out, None
    items = src.arr() if isinstance(src, LTable) else []
    if not items:  # keyed [n] = {...} with gaps
        items = [src[k] for k in sorted(k for k in src if isinstance(k, int))]
    for s in items:
        d = {'monsters': set(), 'gadgets': set(), 'npcs': set(), 'rand_weight': None}
        if isinstance(s, dict):
            for kind in ('monsters', 'gadgets', 'npcs'):
                lst = s.get(kind)
                if isinstance(lst, LTable):
                    for e in lst.vals():
                        if isinstance(e, dict):
                            e = e.get('config_id')
                        if isinstance(e, (int, float)):
                            d[kind].add(int(e))
            d['rand_weight'] = s.get('rand_weight')
        out.append(d)
    return out, ('suite_disk' if disk else 'suites')


def resolve_dotted(env, name):
    cur = env
    for part in name.split('.'):
        if not isinstance(cur, dict):
            return None
        cur = cur.get(part)
    return cur


def load_scene(ver, scene, lua_dir, txt_dir):
    blocks_env = lua_file(os.path.join(lua_dir, 'scene%d.lua' % scene)).env
    block_ids = [int(b) for b in blocks_env['blocks'].arr()]
    group_meta = {}
    for b in block_ids:
        env = lua_file(os.path.join(lua_dir, 'scene%d_block%d.lua' % (scene, b))).env
        for g in env['groups'].arr():
            m = plain(g)
            m['block'] = b
            group_meta[int(g['id'])] = m
    files = {}
    for p in glob.glob(os.path.join(lua_dir, 'scene%d_group*.lua' % scene)):
        mm = re.search(r'_group(\d+)\.lua$', p)
        if mm:
            files[int(mm.group(1))] = p
    warnings = []
    groups = {}
    for gid in sorted(set(group_meta) | set(files)):
        meta = group_meta.get(gid)
        path = files.get(gid)
        g = {'group_id': gid, 'meta': meta, 'file': os.path.basename(path) if path else None,
             'in_block': meta is not None, 'entities': [], 'garbage': collections.Counter(),
             'flags': {}}
        groups[gid] = g
        if not path:
            continue
        ch = lua_file(path)
        warnings += ch.warnings
        env = ch.env
        suites, suite_src = suite_list(env)
        init = env.get('init_config') if isinstance(env.get('init_config'), dict) else {}
        init_suite = init.get('suite', 1) if isinstance(init.get('suite', 1), int) else 1
        rand_suite = bool(init.get('rand_suite'))
        code = ch.code_text()
        created = set()
        for kind, ref in CREATE_RE.findall(code):
            if ref.isdigit():
                created.add(int(ref))
            else:
                v = resolve_dotted(env, ref)
                if isinstance(v, int):
                    created.add(v)
        code_ints = set(int(x) for x in re.findall(r'(?<![\w.])(\d{4,9})(?![\w.])', code))
        # triggers -> quest / activity gating hints
        quests, events = set(), collections.Counter()
        trig = env.get('triggers')
        if isinstance(trig, LTable):
            for t in trig.arr():
                if not isinstance(t, dict):
                    continue
                ev = str(t.get('event'))
                events[ev.replace('EventType.', '')] += 1
                if 'QUEST' in ev and t.get('source'):
                    quests.add(str(t.get('source')))
        garb = env.get('garbages')
        garbage_ids = collections.defaultdict(set)
        if isinstance(garb, dict):
            for kind in ('gadgets', 'monsters', 'npcs', 'regions', 'triggers'):
                lst = garb.get(kind)
                if isinstance(lst, LTable):
                    g['garbage'][kind] = len(lst.vals())
                    for e in lst.vals():
                        if isinstance(e, dict) and isinstance(e.get('config_id'), int):
                            garbage_ids[kind].add(e['config_id'])
        g['flags'] = {
            'suite_source': suite_src, 'suite_count': len(suites), 'init_suite': init_suite,
            'end_suite': init.get('end_suite'), 'rand_suite': rand_suite,
            'requires': ch.requires, 'quest_triggers': sorted(quests),
            'events': dict(events),
            'add_extra_suite_in_code': bool(re.search(r'AddExtraGroupSuite|RefreshGroup', code)),
            'code_mentions_activity': bool(re.search(r'Activity', code)),
        }
        for kind, idkey in KIND_ID.items():
            lst = env.get(kind)
            if not isinstance(lst, LTable):
                continue
            for e in lst.vals():
                if not isinstance(e, dict) or not isinstance(e.get('config_id'), int):
                    continue
                cid = e['config_id']
                eid = e.get(idkey)
                in_suites = [i + 1 for i, s in enumerate(suites) if cid in s[kind]]
                if init_suite in in_suites:
                    membership = 'init'
                elif in_suites:
                    membership = 'extra'
                elif cid in created:
                    membership = 'script'
                else:
                    membership = 'none'
                rec = {
                    'kind': kind[:-1], 'config_id': cid, 'id': int(eid) if isinstance(eid, (int, float)) else None,
                    'pos': pos3(e.get('pos')), 'rot_y': pos3(e.get('rot'))[1],
                    'suites': in_suites, 'membership': membership,
                    'code_ref': cid in code_ints, 'in_garbage_too': cid in garbage_ids.get(kind, ()),
                    'extra': {k: plain(v) for k, v in e.items() if k not in ('config_id', idkey, 'pos', 'rot')},
                }
                g['entities'].append(rec)
    return {'ver': ver, 'scene': scene, 'blocks': block_ids, 'groups': groups, 'warnings': warnings}


def load_points(lua_dir, scene):
    p = os.path.join(lua_dir, 'scene%d_point.json' % scene)
    with open(p, encoding='utf-8') as f:
        d = json.load(f)
    return d.get('points', {})


# ============================================================================
# Defaults
# ============================================================================

HERE = os.path.dirname(os.path.abspath(__file__))

DEF = {
    '1.6': dict(scene=4, lua=r'D:/servere_test/1.6_live/server/data/lua/scene/4',
                txt=r'D:/servere_test/1.6_live/server/data/txt',
                txt_fallback=os.path.normpath(os.path.join(
                    HERE, '..', '..', 'server_client_fixes_ref', 'fix_1.6_windows11_only', 'mhynot', 'txt'))),
    '2.8': dict(scene=9, lua=r'D:/servere_test/2.8_live/server/data/2.8_live-output_9464149-server-data/lua/scene/9',
                txt=r'D:/servere_test/2.8_live/server/data/2.8_live-output_9464149-server-data/txt',
                txt_fallback=None),
}


def apply_args(args):
    """Where the server data lives is machine-specific; the CLI overrides win over the defaults."""
    for ver, lua_opt, txt_opt in (('1.6', 'lua16', 'txt16'), ('2.8', 'lua28', 'txt28')):
        if getattr(args, lua_opt, None):
            DEF[ver]['lua'] = getattr(args, lua_opt)
        if getattr(args, txt_opt, None):
            DEF[ver]['txt'] = getattr(args, txt_opt)


def resolve_txt(cfg):
    if os.path.exists(os.path.join(cfg['txt'], 'GadgetData_Level.txt')):
        return cfg['txt']
    if cfg.get('txt_fallback') and os.path.exists(os.path.join(cfg['txt_fallback'], 'GadgetData_Level.txt')):
        return cfg['txt_fallback']
    raise SystemExit('GadgetData tables not found for %s' % cfg)


# ============================================================================
# survey
# ============================================================================

def cmd_survey(args):
    os.makedirs(args.out, exist_ok=True)
    for ver in ('1.6', '2.8'):
        cfg = DEF[ver]
        txt = resolve_txt(cfg)
        gt, mt, nt = load_gadget_table(txt), load_monster_table(txt), load_npc_table(txt)
        sc = load_scene(ver, cfg['scene'], cfg['lua'], txt)
        agg = collections.OrderedDict()
        for g in sc['groups'].values():
            for e in g['entities']:
                key = (e['kind'], e['id'])
                a = agg.setdefault(key, collections.Counter())
                a['total'] += 1
                a[e['membership']] += 1
                if g['meta'] is None:
                    a['orphan_group'] += 1
        lines = ['kind\tid\ttotal\tinit\textra\tscript\tnone\torphan\ttype\tjson_or_prefab\tcombat\tname_cn']
        for (kind, eid), a in sorted(agg.items(), key=lambda kv: (kv[0][0], kv[0][1] or 0)):
            if kind == 'gadget':
                info = gt.get(eid, {})
                t, j, c, n = info.get('type_name', '?'), info.get('json', '?'), '', info.get('name_cn', '?')
            elif kind == 'monster':
                info = mt.get(eid, {})
                t, j, c, n = info.get('type', '?'), info.get('prefab', '?'), info.get('combat', ''), info.get('name_cn', '?')
            else:
                info = nt.get(eid, {})
                t, j, c, n = 'NPC', info.get('json', '?'), '', info.get('name_cn', '?')
            lines.append('\t'.join(str(x) for x in (kind, eid, a['total'], a['init'], a['extra'], a['script'], a['none'],
                                                       a['orphan_group'], t, j, c, n)))
        tag = ver.replace('.', '')
        with open(os.path.join(args.out, 'survey_%s_entities.tsv' % tag), 'w', encoding='utf-8', newline='\n') as f:
            f.write('\n'.join(lines) + '\n')
        gl = ['group_id\tblock\tarea\tin_block\tbusiness\trefresh_id\tvision_type\textra_meta\tsuites\tinit\trand\trequires\tquests\tgarbage\tn_gadgets\tn_monsters']
        for gid, g in sorted(sc['groups'].items()):
            m = g['meta'] or {}
            fl = g['flags']
            extra_meta = {k: v for k, v in m.items() if k not in ('id', 'area', 'pos', 'dynamic_load', 'is_replaceable',
                                                                   'business', 'refresh_id', 'vision_type', 'block')}
            gl.append('\t'.join(str(x) for x in (
                gid, m.get('block'), m.get('area'), g['in_block'], json.dumps(m.get('business')), m.get('refresh_id'),
                m.get('vision_type'), json.dumps(extra_meta), fl.get('suite_count'), fl.get('init_suite'),
                fl.get('rand_suite'), ','.join(fl.get('requires', [])), ','.join(fl.get('quest_triggers', [])),
                json.dumps(dict(g['garbage'])),
                sum(1 for e in g['entities'] if e['kind'] == 'gadget'),
                sum(1 for e in g['entities'] if e['kind'] == 'monster'))))
        with open(os.path.join(args.out, 'survey_%s_groups.tsv' % tag), 'w', encoding='utf-8', newline='\n') as f:
            f.write('\n'.join(gl) + '\n')
        n_ent = sum(len(g['entities']) for g in sc['groups'].values())
        print('%s scene %d: groups=%d (files=%d, listed=%d) entities=%d parse_warnings=%d txt=%s' % (
            ver, cfg['scene'], len(sc['groups']), sum(1 for g in sc['groups'].values() if g['file']),
            sum(1 for g in sc['groups'].values() if g['in_block']), n_ent, len(sc['warnings']), txt))
        for w in sc['warnings'][:15]:
            print('   warn:', w)


# ============================================================================
# build
# ============================================================================

RES_DIR = os.path.normpath(os.path.join(HERE, '..', 'mod', 'cheat-library', 'res'))
EXISTING_MAPS = ['map_teyvat.json', 'map_enkanomiya.json', 'map_undeground_mines.json', 'map_golden_apple_archipelago.json']
# What res.rc embeds, per game version (level coordinates, slim + minified - see write_slim)
RES_NAME = {'1.6': 'map_gaa16_scene4.json', '2.8': 'map_gaa28_scene9.json'}
CUSTOM_POINT_START = 1000000          # InteractiveMap.cpp f_CustomPointIndex default
SCENE_ID_BASE = {4: 40000000, 9: 90000000}
SCENE_POINT_SLOT = 9                  # base + 9_000_000 + scene point id (scene<id>_point.json)
NEW_LABEL_ID_START = 500              # labels not present in any shipped map_*.json
# FROZEN label ids for clear_names absent from every shipped map (never renumber: cfg.json completions use them)
NEW_LABEL_IDS = {
    'SearchPoint': 500, 'SeaRewardCrate': 501, 'PhantasmalConchSlot': 503, 'DreamPortal': 504,
    'ConstellationMechanism': 505, 'StarlightCoalescence': 506, 'TheRavenForum': 507, 'DreamForm': 508,
    'SeelieLamp': 509, 'RifthoundWhelp': 510, 'Rifthound': 511, 'Cicin': 512, 'OceanidBoar': 513,
    'OceanidCrab': 514, 'OceanidCrane': 515, 'OceanidFinch': 516, 'OceanidFrog': 517, 'OceanidSquirrel': 518,
    'OceanidWigeon': 519, 'OceanidFalcon': 520,
}

# category name -> id used by the shipped datasets
CATEGORIES = [(1, 'Waypoints'), (4, 'Oculi'), (13, 'Open-World Chests'), (113, 'Investigation'), (14, 'Puzzles'),
              (10, 'Local Specialties'), (60, 'Materials'), (11, 'Ores'), (50, 'Enemies (Common)'),
              (12, 'Enemies (Elite)'), (131, 'Enemies (Boss)'), (273, 'Animals')]

# clear_name -> (display name, category, membership policy)
#   policy 'collect' = entity in the init suite, in any other suite, or created by the group's own script
#   policy 'spawn'   = entity in the init suite only (kept for experiments; every label uses 'collect' -
#                      extra-suite monsters stay in, tagged by the per-point 'suite' key)
#   policy None      = not a group entity (scene point json)
LABELS = collections.OrderedDict([
    ('TeleportWaypoint', ('Teleport Waypoint', 'Waypoints', None)),
    ('WaveriderWaypointCannotTeleport', ('Waverider Waypoint (Cannot Teleport)', 'Waypoints', None)),
    ('Domain', ('Domain', 'Waypoints', None)),
    ('EchoingConch', ('Echoing Conch', 'Oculi', 'collect')),
    ('ImagingConch', ('Phantasmal Conch', 'Oculi', 'collect')),
    ('CommonChest', ('Common Chest', 'Open-World Chests', 'collect')),
    ('ExquisiteChest', ('Exquisite Chest', 'Open-World Chests', 'collect')),
    ('PreciousChest', ('Precious Chest', 'Open-World Chests', 'collect')),
    ('LuxuriousChest', ('Luxurious Chest', 'Open-World Chests', 'collect')),
    ('RemarkableChest', ('Remarkable Chest', 'Open-World Chests', 'collect')),
    ('SearchPoint', ('Investigation Spot', 'Investigation', 'collect')),
    ('WoodenCrate', ('Floating Wooden Crate', 'Investigation', 'collect')),
    ('SeaRewardCrate', ('Sea Reward Crate', 'Investigation', 'collect')),
    ('TimeTrialChallenge', ('Time Trial Challenge', 'Puzzles', 'collect')),
    ('BloattyFloatty', ('Bloatty Floatty', 'Puzzles', 'collect')),
    ('MistBubble', ('Suspicious Bubbles', 'Puzzles', 'collect')),
    ('PhantasmalConchSlot', ('Phantasmal Conch Slot', 'Puzzles', 'collect')),
    ('DreamPortal', ('Dream Portal', 'Puzzles', 'collect')),
    ('ConstellationMechanism', ('Constellation Mechanism', 'Puzzles', 'collect')),
    ('StarlightCoalescence', ('Catchable Paper Star', 'Puzzles', 'collect')),
    ('TheRavenForum', ('Crow Statue', 'Puzzles', 'collect')),
    ('DreamForm', ('Animal Seelie', 'Puzzles', 'collect')),
    ('Seelie', ('Seelie', 'Puzzles', 'collect')),
    ('SeelieLamp', ('Seelie Lamp', 'Puzzles', 'collect')),
    ('ElectroSeelie', ('Electro Seelie', 'Puzzles', 'collect')),
    ('ElementalMonument', ('Elemental Monument', 'Puzzles', 'collect')),
    ('PressurePlate', ('Pressure Plate', 'Puzzles', 'collect')),
    ('WindmillMechanism', ('Windmill Mechanism', 'Puzzles', 'collect')),
    ('TorchPuzzle', ('Torch Puzzle', 'Puzzles', 'collect')),
    ('SeaGanoderma', ('Sea Ganoderma', 'Local Specialties', 'collect')),
    ('NakuWeed', ('Naku Weed', 'Local Specialties', 'collect')),
    ('Starconch', ('Starconch', 'Local Specialties', 'collect')),
    ('Qingxin', ('Qingxin', 'Local Specialties', 'collect')),
    ('JueyunChili', ('Jueyun Chili', 'Local Specialties', 'collect')),
    ('Violetgrass', ('Violetgrass', 'Local Specialties', 'collect')),
    ('PhilanemoMushroom', ('Philanemo Mushroom', 'Local Specialties', 'collect')),
    ('Valberry', ('Valberry', 'Local Specialties', 'collect')),
    ('CorLapis', ('Cor Lapis', 'Local Specialties', 'collect')),
    ('DandelionSeed', ('Dandelion Seed', 'Local Specialties', 'collect')),
    ('Mint', ('Mint', 'Materials', 'collect')),
    ('SweetFlower', ('Sweet Flower', 'Materials', 'collect')),
    ('Berry', ('Berry', 'Materials', 'collect')),
    ('BirdEgg', ('Bird Egg', 'Materials', 'collect')),
    ('Mushroom', ('Mushroom', 'Materials', 'collect')),
    ('Carrot', ('Carrot', 'Materials', 'collect')),
    ('Radish', ('Radish', 'Materials', 'collect')),
    ('Apple', ('Apple', 'Materials', 'collect')),
    ('Sunsettia', ('Sunsettia', 'Materials', 'collect')),
    ('LavenderMelon', ('Lavender Melon', 'Materials', 'collect')),
    ('Seagrass', ('Seagrass', 'Materials', 'collect')),
    ('FlamingFlowerStamen', ('Flaming Flower Stamen', 'Materials', 'collect')),
    ('MistFlowerCorolla', ('Mist Flower Corolla', 'Materials', 'collect')),
    ('ElectroCrystal', ('Electro Crystal', 'Materials', 'collect')),
    ('Fowl', ('Fowl', 'Materials', 'collect')),
    ('IronChunk', ('Iron Chunk', 'Ores', 'collect')),
    ('WhiteIronChunk', ('White Iron Chunk', 'Ores', 'collect')),
    ('CrystalChunk', ('Crystal Chunk', 'Ores', 'collect')),
    ('AmethystLump', ('Amethyst Lump', 'Ores', 'collect')),
    ('Hilichurl', ('Hilichurl', 'Enemies (Common)', 'collect')),
    ('HilichurlShooter', ('Hilichurl Shooter', 'Enemies (Common)', 'collect')),
    ('Samachurl', ('Samachurl', 'Enemies (Common)', 'collect')),
    ('Slime', ('Slime', 'Enemies (Common)', 'collect')),
    ('Specter', ('Specter', 'Enemies (Common)', 'collect')),
    ('Whopperflower', ('Whopperflower', 'Enemies (Common)', 'collect')),
    ('FatuiSkirmisher', ('Fatui Skirmisher', 'Enemies (Common)', 'collect')),
    ('Mitachurl', ('Mitachurl', 'Enemies (Elite)', 'collect')),
    ('HilichurlChieftain', ('Lawachurl', 'Enemies (Elite)', 'collect')),
    ('AbyssMage', ('Abyss Mage', 'Enemies (Elite)', 'collect')),
    ('RifthoundWhelp', ('Rifthound Whelp', 'Enemies (Elite)', 'collect')),
    ('Rifthound', ('Rifthound', 'Enemies (Elite)', 'collect')),
    ('FatuiCicinMage', ('Fatui Cicin Mage', 'Enemies (Elite)', 'collect')),
    ('Cicin', ('Cicin', 'Enemies (Elite)', 'collect')),
    ('RuinGuard', ('Ruin Guard', 'Enemies (Elite)', 'collect')),
    ('GeovishapHatchling', ('Geovishap Hatchling', 'Enemies (Elite)', 'collect')),
    ('Geovishap', ('Geovishap', 'Enemies (Elite)', 'collect')),
    ('EyeoftheStorm', ('Eye of the Storm', 'Enemies (Elite)', 'collect')),
    ('OceanidBoar', ('Oceanid Boar', 'Enemies (Boss)', 'collect')),
    ('OceanidCrab', ('Oceanid Crab', 'Enemies (Boss)', 'collect')),
    ('OceanidCrane', ('Oceanid Crane', 'Enemies (Boss)', 'collect')),
    ('OceanidFinch', ('Oceanid Finch', 'Enemies (Boss)', 'collect')),
    ('OceanidFrog', ('Oceanid Frog', 'Enemies (Boss)', 'collect')),
    ('OceanidSquirrel', ('Oceanid Squirrel', 'Enemies (Boss)', 'collect')),
    ('OceanidWigeon', ('Oceanid Wigeon', 'Enemies (Boss)', 'collect')),
    ('OceanidFalcon', ('Oceanid Falcon', 'Enemies (Boss)', 'collect')),
    ('MaguuKenki', ('Maguu Kenki (Puppet General)', 'Enemies (Boss)', 'collect')),
    ('OceanCrab', ('Ocean Crab', 'Animals', 'collect')),
])

GATHER_ITEM_LABEL = {
    101001: 'IronChunk', 101002: 'WhiteIronChunk', 101003: 'CrystalChunk', 101008: 'AmethystLump',
    100016: 'Mint', 100011: 'Mushroom', 100013: 'Carrot', 100012: 'SweetFlower', 100014: 'Radish', 100001: 'Apple',
    100002: 'Sunsettia', 100022: 'Valberry', 100025: 'PhilanemoMushroom', 100027: 'JueyunChili', 100034: 'Violetgrass',
    100031: 'Qingxin', 100033: 'Starconch', 101206: 'SeaGanoderma', 101211: 'LavenderMelon', 101210: 'Seagrass',
    101205: 'NakuWeed', 100051: 'Berry', 100062: 'BirdEgg', 100053: 'FlamingFlowerStamen', 100054: 'ElectroCrystal',
    100052: 'MistFlowerCorolla', 100057: 'DandelionSeed', 100058: 'CorLapis', 100064: 'Fowl',
    101908: 'StarlightCoalescence',
}
CHEST_TIER = {'1': 'CommonChest', '2': 'ExquisiteChest', '4': 'PreciousChest', '5': 'LuxuriousChest', '6': 'RemarkableChest'}


def load_gather(txt_dir):
    h, rows = read_tsv(os.path.join(txt_dir, 'GatherData.txt'))
    by_point, by_gadget = {}, {}
    for r in rows:
        if len(r) < 5 or not r[0].strip().isdigit():
            continue
        gad = int(r[3]) if r[3].strip().isdigit() else None
        item = int(r[4]) if r[4].strip().isdigit() else None
        by_point.setdefault(int(r[0]), (gad, item))
        if gad:
            by_gadget.setdefault(gad, item)
    return by_point, by_gadget


def classify_gadget(e, gi, gather_by_point, gather_by_gadget):
    """-> (clear_name, note) or (None, None)."""
    j = gi.get('json', '') or ''
    t = gi.get('type_name')
    gid = e['id']
    if gid == 70500000:  # GatherPoint: point_type -> GatherData -> item
        pt = e['extra'].get('point_type')
        gad, item = gather_by_point.get(pt, (None, None))
        lab = GATHER_ITEM_LABEL.get(item)
        return (lab, 'gather point_type %s -> gadget %s item %s' % (pt, gad, item)) if lab else (None, None)
    if t == 'GatherObject' and gid in gather_by_gadget:
        lab = GATHER_ITEM_LABEL.get(gather_by_gadget[gid])
        return (lab, 'direct gather object') if lab else (None, None)
    if t == 'EchoShell':
        if 'Dreamconch' in j:
            return 'ImagingConch', None
        if 'Echoconch' in j:
            return 'EchoingConch', None
    if t == 'Chest':
        # Sealed (Locked), in-rock (Rock), bramble- and ice-bound chests are ordinary chests of their tier once
        # freed - the 2.8 archipelago has one in-rock chest (70210063).
        m = re.search(r'SceneObj_(?:Essence)?Chest_(?:Default|Locked|Rock|Bramble|Frozen)_Lv(\d)', j)
        if m and m.group(1) in CHEST_TIER:
            return CHEST_TIER[m.group(1)], None
        # The music-thorn chests carry the drop tags of the plain tiers: _01 "puzzle, low" like a Lv1 chest,
        # _02 "puzzle, mid" like a Lv2 one (2.8, eight of each).
        if 'MusicThornTreasurebox_01' in j:
            return 'CommonChest', 'music-thorn chest LV1'
        if 'MusicThornTreasurebox_02' in j:
            return 'ExquisiteChest', 'music-thorn chest LV2'
        if 'ReflectChest' in j:
            return 'CommonChest', 'reflection chest (common)'
        if j == 'SearchPoint_OnWater':
            return 'WoodenCrate', None
        if j == 'SearchPoint':
            return 'SearchPoint', None
        return None, None
    if 'Rewardcrate' in j:
        return 'SeaRewardCrate', None
    if 'Challengestarter_0' in j and 'MichiaeMatsuri' not in j:
        return 'TimeTrialChallenge', None
    if 'BoatRaceStart_01' in j:
        return 'TimeTrialChallenge', 'waverider challenge'
    if 'Balloon_Tree' in j:
        return 'BloattyFloatty', None
    if 'Suspiciousbubbles' in j:
        return 'MistBubble', None
    if 'Dreamconch_Slot' in j or 'Dreamconch_OnWater_Slot' in j:
        return 'PhantasmalConchSlot', None
    if 'DreamPortal' in j:
        return 'DreamPortal', None
    if 'ConstellationMachine' in j:
        return 'ConstellationMechanism', None
    if 'NightCrowStatue_01' in j:
        return 'TheRavenForum', None
    if 'AnimalSeelie' in j:
        return 'DreamForm', None
    if 'Platform_Seelie' in j:
        return 'Seelie', None
    if 'Lamp_Post' in j:
        return 'SeelieLamp', None
    if 'ElectricSeelie' in j:
        return 'ElectroSeelie', None
    if t == 'Gear' and re.search(r'Operator_(Fire|Ice|Wind|Water|Rock|Electric)Tablet', j):
        return 'ElementalMonument', None
    if 'Gravity_Board' in j:
        return 'PressurePlate', None
    if 'Gear_Windmill' in j:
        return 'WindmillMechanism', None
    if 'TorchPuzzle_Base' in j:
        return 'TorchPuzzle', None
    return None, None


def classify_monster(mi):
    p, cn = mi.get('prefab', '') or '', mi.get('name_cn', '') or ''
    rules = [('_Slime_', 'Slime'), ('_Shaman_', 'Samachurl'), ('_Abyss_', 'AbyssMage'), ('_Sylph_', 'Specter'),
             ('_Hound_Kanis', 'RifthoundWhelp'), ('_Hound_Riftstalker', 'Rifthound'),
             ('_Skirmisher_', 'FatuiSkirmisher'), ('_Fatuus_Summoner', 'FatuiCicinMage'), ('_Cicin_', 'Cicin'),
             ('_Defender_', 'RuinGuard'), ('_Wyrm_Rock', 'GeovishapHatchling'), ('_Drake_Rock', 'Geovishap'),
             ('_Mimik_', 'Whopperflower'), ('_Elemental_Wind', 'EyeoftheStorm'), ('_Oceanid_Boar', 'OceanidBoar'), ('_Oceanid_Crab', 'OceanidCrab'), ('_Oceanid_Crane', 'OceanidCrane'),
             ('_Oceanid_Tit', 'OceanidFinch'), ('_Oceanid_Frog', 'OceanidFrog'), ('_Oceanid_Squirrel', 'OceanidSquirrel'),
             ('_Oceanid_Wigeon', 'OceanidWigeon'), ('_Oceanid_Falcon', 'OceanidFalcon'),
             ('_Samurai_Ningyo', 'MaguuKenki'), ('Animal_Crab_01_03', 'OceanCrab')]
    if '_Hili_' in p:
        return 'HilichurlShooter' if ('射手' in cn or '箭' in cn) else 'Hilichurl'
    if '_Brute_' in p:
        return 'HilichurlChieftain' if cn.endswith('王') else 'Mitachurl'
    for sub, lab in rules:
        if sub in p:
            return lab
    return None


def group_flags(g):
    m, fl, out = g['meta'] or {}, g['flags'], []
    if fl.get('quest_triggers'):
        out.append('quest:' + ','.join(fl['quest_triggers']))
    if m.get('related_level_tag_series_list'):
        out.append('level_tag_series:' + ','.join(str(x) for x in m['related_level_tag_series_list']))
    bus = m.get('business') or {}
    if bus.get('sub_type'):
        out.append('business_sub_type:%s' % bus['sub_type'])
    for r in fl.get('requires') or []:
        out.append('script:' + r)
    if fl.get('rand_suite'):
        out.append('rand_suite')
    if not g['in_block']:
        out.append('not_listed_in_block')
    return out


def point_id(scene, group_id, config_id):
    block_no = (group_id // 1000) % 1000
    group_no = group_id % 1000
    if not (1 <= block_no <= 8 and 0 <= config_id < 1000000 and config_id // 1000 == group_no):
        return None
    return SCENE_ID_BASE[scene] + block_no * 1000000 + config_id


def existing_label_ids():
    ids, all_point_ids = {}, set()
    for fn in EXISTING_MAPS:
        p = os.path.join(RES_DIR, fn)
        if not os.path.exists(p):
            continue
        with open(p, encoding='utf-8') as f:
            d = json.load(f)
        for lid, lab in d['labels'].items():
            ids.setdefault(lab['clear_name'], int(lid))
            for pt in lab['points']:
                all_point_ids.add(int(pt['id']))
    return ids, all_point_ids


def dist2(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def build_version(ver, args, label_ids, existing_pids, report):
    cfg = DEF[ver]
    scene = cfg['scene']
    txt = resolve_txt(cfg)
    gt, mt = load_gadget_table(txt), load_monster_table(txt)
    g_by_point, g_by_gadget = load_gather(txt)
    sc = load_scene(ver, scene, cfg['lua'], txt)
    tag = ver.replace('.', '')

    raw = collections.defaultdict(list)       # clear_name -> candidate records
    all_entities = []                          # for cross-check: (x, z, desc)
    id_violations = []
    for gid, g in sorted(sc['groups'].items()):
        ents = {e['config_id']: e for e in g['entities']}
        for e in g['entities']:
            x, y, z = e['pos']
            if e['kind'] == 'gadget':
                gi = gt.get(e['id'], {})
                lab, note = classify_gadget(e, gi, g_by_point, g_by_gadget)
                desc = 'gadget %s %s/%s' % (e['id'], gi.get('json'), gi.get('type_name'))
                if e['id'] == 70500000:
                    pt = e['extra'].get('point_type')
                    desc += ' point_type=%s item=%s' % (pt, g_by_point.get(pt, (None, None))[1])
            elif e['kind'] == 'monster':
                mi = mt.get(e['id'], {})
                lab, note = classify_monster(mi), None
                desc = 'monster %s %s' % (e['id'], mi.get('prefab'))
            else:
                lab, note, desc = None, None, 'npc %s' % e['id']
            all_entities.append((x, z, '%s [g%s c%s %s]' % (desc, gid, e['config_id'], e['membership'])))
            if not lab:
                continue
            policy = LABELS[lab][2]
            allowed = {'collect': ('init', 'extra', 'script'), 'spawn': ('init',)}[policy]
            pid = point_id(scene, gid, e['config_id'])
            if pid is None:
                id_violations.append((gid, e['config_id']))
            rec = {'label': lab, 'group': g, 'e': e, 'x': x, 'z': z, 'id': pid, 'note': note,
                   'allowed': e['membership'] in allowed}
            if e['id'] == 70500000 and e['extra'].get('owner') in ents:
                rec['owner'] = ents[e['extra']['owner']]
            raw[lab].append(rec)

    labels_out = collections.OrderedDict()
    stats = collections.OrderedDict()

    def add_point(lab, pid, x, z, extra):
        L = labels_out.setdefault(lab, [])
        pt = collections.OrderedDict([('id', pid), ('x_pos', round(x, 3)), ('y_pos', round(z, 3))])
        pt.update(extra)
        L.append(pt)

    # ---- group entities
    for lab, recs in raw.items():
        st = stats.setdefault(lab, {'raw': 0, 'kept_entities': 0, 'points': 0, 'membership': collections.Counter(),
                                    'dropped_membership': collections.Counter(), 'ids': collections.Counter(),
                                    'flags': collections.Counter(), 'notes': collections.Counter()})
        st['raw'] = len(recs)
        kept = [r for r in recs if r['allowed']]
        for r in recs:
            (st['membership'] if r['allowed'] else st['dropped_membership'])[r['e']['membership']] += 1
        st['kept_entities'] = len(kept)
        if lab in ('EchoingConch', 'ImagingConch'):
            by_shell = collections.OrderedDict()
            for r in kept:
                args_ = r['e']['extra'].get('arguments') or [None]
                by_shell.setdefault(args_[0], []).append(r)
            for shell, rs in by_shell.items():
                # primary placement: the EchoConch_Quest group (discovery site) if any, else the lowest group id
                rs.sort(key=lambda r: (0 if any('EchoConch_Quest' in q for q in r['group']['flags']['requires']) else 1,
                                       r['group']['group_id'], r['e']['config_id']))
                p = rs[0]
                alts = []
                for r in rs[1:]:
                    alts.append({'group_id': r['group']['group_id'], 'config_id': r['e']['config_id'],
                                 'x': round(r['x'], 3), 'z': round(r['z'], 3)})
                extra = collections.OrderedDict([('group_id', p['group']['group_id']), ('config_id', p['e']['config_id']),
                                                 ('gadget_id', p['e']['id']), ('shell_id', shell),
                                                 ('suite', p['e']['membership'])])
                fl = group_flags(p['group'])
                if fl:
                    extra['flags'] = fl
                if alts:
                    extra['alt_placements'] = alts
                add_point(lab, p['id'], p['x'], p['z'], extra)
                st['ids'][p['e']['id']] += 1
                for f in fl:
                    st['flags'][f.split(':')[0]] += 1
            continue
        if raw[lab] and raw[lab][0]['e']['id'] == 70500000 or any(r['e']['id'] == 70500000 for r in kept):
            clusters = collections.OrderedDict()
            for r in kept:
                if 'owner' in r:
                    key = (r['group']['group_id'], r['owner']['config_id'])
                else:
                    key = (r['group']['group_id'], r['e']['config_id'])
                clusters.setdefault(key, []).append(r)
            for (gid, cid), rs in clusters.items():
                own = rs[0].get('owner')
                if own is not None:
                    x, z = own['pos'][0], own['pos'][2]
                    pid = point_id(scene, gid, own['config_id'])
                    owner_gadget = own['id']
                else:
                    x, z = sum(r['x'] for r in rs) / len(rs), sum(r['z'] for r in rs) / len(rs)
                    pid = rs[0]['id']
                    owner_gadget = None
                extra = collections.OrderedDict([('group_id', gid), ('config_id', cid),
                                                 ('gadget_id', rs[0]['e']['id']),
                                                 ('gather_points', len(rs)), ('suite', rs[0]['e']['membership'])])
                if owner_gadget is not None:
                    extra['owner_gadget_id'] = owner_gadget
                if rs[0]['e']['id'] == 70500000:
                    extra['point_type'] = rs[0]['e']['extra'].get('point_type')
                fl = group_flags(rs[0]['group'])
                if fl:
                    extra['flags'] = fl
                add_point(lab, pid, x, z, extra)
                for r in rs:
                    st['ids'][r['e']['id'] if r['e']['id'] != 70500000 else 'gather:%s' % r['e']['extra'].get('point_type')] += 1
                for f in fl:
                    st['flags'][f.split(':')[0]] += 1
            continue
        # One physical object is often declared several times - once per suite, or once per gadget state (a
        # challenge starter appears four times on the same spot, an apple twice). Stacked icons are the small
        # half of the problem: FindNearestPoint skips completed points, so only one of a stack can ever be
        # completed and the label's counter can never be cleared. Collapse same-entity records within a metre.
        collapsed, dupes = [], 0
        for r in sorted(kept, key=lambda r: r['id']):
            twin = next((q for q in collapsed if q['e']['id'] == r['e']['id']
                         and dist2((q['x'], q['z']), (r['x'], r['z'])) <= 1.0), None)
            if twin is None:
                collapsed.append(r)
            else:
                dupes += 1
        if dupes:
            st['notes']['%d duplicate placements collapsed' % dupes] += 1

        for r in collapsed:
            e, g = r['e'], r['group']
            extra = collections.OrderedDict([('group_id', g['group_id']), ('config_id', e['config_id']),
                                             ('%s_id' % e['kind'], e['id']), ('suite', e['membership'])])
            if e['kind'] == 'gadget' and LABELS[lab][1] == 'Open-World Chests' and not e['extra'].get('explore'):
                extra['no_exploration'] = True
            fl = group_flags(g)
            if fl:
                extra['flags'] = fl
            if r['note']:
                extra['note'] = r['note']
                st['notes'][r['note']] += 1
            add_point(lab, r['id'], r['x'], r['z'], extra)
            st['ids'][e['id']] += 1
            for f in fl:
                st['flags'][f.split(':')[0]] += 1

    # ---- scene points (teleports, waverider summon points, dungeon entries)
    pts = load_points(cfg['lua'], scene)
    domains = []
    for k, v in sorted(pts.items(), key=lambda kv: int(kv[0])):
        pid = SCENE_ID_BASE[scene] + SCENE_POINT_SLOT * 1000000 + int(k)
        x, z = v['pos']['x'], v['pos']['z']
        base = collections.OrderedDict([('scene_point_id', int(k)), ('gadget_id', v.get('gadgetId')), ('area_id', v.get('areaId'))])
        if v['$type'] == 'SceneTransPoint' and v.get('pointType') == 'TransPointNormal' and v.get('mapVisibility') != 'Never':
            add_point('TeleportWaypoint', pid, x, z, base)
        elif v['$type'] == 'SceneVehicleSummonPoint':
            add_point('WaveriderWaypointCannotTeleport', pid, x, z, base)
        elif v['$type'] == 'DungeonEntry' and v.get('mapVisibility') != 'Never':
            for d in domains:
                if dist2((d['x'], d['z']), (x, z)) < 3.0:
                    d['ids'].append(int(k))
                    d['dungeons'].update(v.get('dungeonIds') or [])
                    break
            else:
                domains.append({'x': x, 'z': z, 'ids': [int(k)], 'dungeons': set(v.get('dungeonIds') or []),
                                'title': v.get('titleTextID'), 'area': v.get('areaId'), 'gadget': v.get('gadgetId')})
    for d in domains:
        pid = SCENE_ID_BASE[scene] + SCENE_POINT_SLOT * 1000000 + min(d['ids'])
        add_point('Domain', pid, d['x'], d['z'], collections.OrderedDict([
            ('scene_point_ids', d['ids']), ('dungeon_ids', sorted(d['dungeons'])), ('title_text_id', d['title']),
            ('gadget_id', d['gadget']), ('area_id', d['area'])]))
    for lab in ('TeleportWaypoint', 'WaveriderWaypointCannotTeleport', 'Domain'):
        if lab in labels_out:
            stats.setdefault(lab, {'raw': len(labels_out[lab]), 'kept_entities': len(labels_out[lab]), 'points': 0,
                                   'membership': collections.Counter({'scene_point_json': len(labels_out[lab])}),
                                   'dropped_membership': collections.Counter(), 'ids': collections.Counter(),
                                   'flags': collections.Counter(), 'notes': collections.Counter()})

    # ---- assemble in LABELS order, assign label ids
    new_label_ids = {}
    for lab in LABELS:  # frozen ids: saved completions are keyed scene -> label id -> point id
        if lab not in label_ids:
            if lab not in NEW_LABEL_IDS:
                raise SystemExit('label %s is not in any shipped map and has no frozen id in NEW_LABEL_IDS' % lab)
            new_label_ids[lab] = NEW_LABEL_IDS[lab]
    assert len(set(NEW_LABEL_IDS.values())) == len(NEW_LABEL_IDS)
    assert min(NEW_LABEL_IDS.values()) > max(label_ids.values())
    out_labels = collections.OrderedDict()
    for lab, (name, cat, _) in LABELS.items():
        if lab not in labels_out:
            continue
        lid = label_ids.get(lab, new_label_ids.get(lab))
        points = sorted(labels_out[lab], key=lambda p: p['id'])
        out_labels[str(lid)] = collections.OrderedDict([('name', name), ('clear_name', lab), ('points', points)])
        stats[lab]['points'] = len(points)
        stats[lab]['label_id'] = lid
    cats = []
    for cid, cname in CATEGORIES:
        children = [int(lid) for lid, L in out_labels.items() if LABELS[L['clear_name']][1] == cname]
        if children:
            cats.append(collections.OrderedDict([('id', cid), ('name', cname), ('children', children)]))

    # ---- id checks
    all_ids = [p['id'] for L in out_labels.values() for p in L['points']]
    dup = [i for i, c in collections.Counter(all_ids).items() if c > 1]
    none_ids = sum(1 for i in all_ids if i is None)
    clash_existing = sorted(set(all_ids) & existing_pids)
    in_custom = [i for i in all_ids if i is not None and CUSTOM_POINT_START <= i < SCENE_ID_BASE[scene]]

    # Not warnings in a report nobody reads: in the loader a duplicate id silently overwrites the other point
    # (points[id] = ...), and a null id throws inside the feature's constructor, which runs on the injection
    # thread with nothing to catch it - the game would die at startup. Fail the generation instead.
    if dup or none_ids:
        raise SystemExit('%s scene %d: %d duplicate point ids %s, %d unresolved - dataset NOT written'
                         % (ver, scene, len(dup), dup[:5], none_ids))

    meta = collections.OrderedDict([
        ('generator', 'gaa_extract.py build'),
        ('game_version', ver), ('scene_id', scene),
        ('coordinates', 'world: x_pos = world X, y_pos = world Z (Miscs.GenLevelPos: level = (-Z, X))'),
        ('point_id_scheme', '%d + block_no*1e6 + config_id (group entities; block_no = (group_id//1000)%%1000, '
                            'config_id//1000 == group_id%%1000); %d + 9e6 + scene_point_id (scene%d_point.json)'
         % (SCENE_ID_BASE[scene], SCENE_ID_BASE[scene], scene)),
        ('suite', 'init = in init_config.suite; extra = only in another suite (added by triggers); '
                  'script = in no suite but created by ScriptLib.CreateGadget/CreateMonster in the group code; '
                  'entities of the garbages block are never read'),
        ('gather_clusters', 'GatherPoint (70500000) entries sharing an owner gadget collapse to one point at the owner '
                            'position: config_id/id = the owner, gather_points = how many were merged'),
        ('flags', 'quest:<quest ids of EVENT_QUEST_* triggers>, level_tag_series:<LevelTagGroupsData id>, '
                  'business_sub_type:<n>, script:<require>, rand_suite, not_listed_in_block'),
        ('conchs', 'one point per shell id (arguments[0]); primary placement = the EchoConch_Quest group if any, else '
                   'the lowest group id; other placements in alt_placements'),
        ('sources', {'lua': cfg['lua'], 'txt': txt}),
    ])
    world = collections.OrderedDict([('_meta', meta), ('labels', out_labels), ('categories', cats)])
    level_labels = collections.OrderedDict()
    for lid, L in out_labels.items():
        pts2 = []
        for p in L['points']:
            q = collections.OrderedDict(p)
            q['x_pos'], q['y_pos'] = round(-p['y_pos'], 3), round(p['x_pos'], 3)
            pts2.append(q)
        level_labels[lid] = collections.OrderedDict([('name', L['name']), ('clear_name', L['clear_name']), ('points', pts2)])
    meta2 = collections.OrderedDict(meta)
    meta2['coordinates'] = 'level: x_pos = -world Z, y_pos = world X (what Miscs.GenLevelPos returns; load without ApplyScaling)'
    level = collections.OrderedDict([('_meta', meta2), ('labels', level_labels), ('categories', cats)])

    os.makedirs(args.out, exist_ok=True)
    fw = os.path.join(args.out, 'map_golden_apple_archipelago_%s_scene%d_world.json' % (tag, scene))
    fl_ = os.path.join(args.out, 'map_golden_apple_archipelago_%s_scene%d_level.json' % (tag, scene))
    for path, obj in ((fw, world), (fl_, level)):
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            json.dump(obj, f, ensure_ascii=False, indent=1)
    out_files = [fw, fl_]
    if getattr(args, 'emit', False):
        out_files.append(write_slim(level, ver))

    # ---- report
    R = report
    R.append('=' * 100)
    R.append('%s scene %d  groups=%d entities=%d parse_warnings=%d' % (
        ver, scene, len(sc['groups']), sum(len(g['entities']) for g in sc['groups'].values()), len(sc['warnings'])))
    garb = collections.Counter()
    for g in sc['groups'].values():
        garb.update(g['garbage'])
    R.append('garbages excluded (entries per kind): %s' % dict(garb))
    R.append('output: %s' % fw)
    R.append('output: %s' % fl_)
    R.append('point ids: n=%d unique=%d dup=%s unresolved=%d min=%s max=%s clash_with_shipped_maps=%d in_custom_range=%d'
             % (len(all_ids), len(set(all_ids)), dup[:5], none_ids, min(i for i in all_ids if i is not None),
                max(i for i in all_ids if i is not None), len(clash_existing), len(in_custom)))
    R.append('config_id pattern violations among labelled entities: %d %s' % (len(id_violations), id_violations[:10]))
    R.append('')
    R.append('%-32s %5s %6s %6s %6s  %-40s %-28s %s' % ('clear_name', 'label', 'points', 'kept', 'raw', 'membership kept', 'dropped', 'ids / flags'))
    for lab in LABELS:
        if lab not in stats:
            continue
        s = stats[lab]
        R.append('%-32s %5s %6d %6d %6d  %-40s %-28s %s | flags %s%s' % (
            lab, s.get('label_id'), s['points'], s['kept_entities'], s['raw'], dict(s['membership']),
            dict(s['dropped_membership']), dict(s['ids']), dict(s['flags']),
            (' | notes %s' % dict(s['notes'])) if s['notes'] else ''))
    return {'world': world, 'all_entities': all_entities, 'out_labels': out_labels, 'files': out_files}


def slim(level):
    """Only what InteractiveMap's loader reads (ParsePointData / LoadLabelData / LoadCategoriaData).

    Everything else the build writes - _meta, group_id/config_id, suite, flags, alt_placements - is for
    review and regeneration, and would be dead weight inside the DLL: it is ~5x the size of the data
    the game uses.
    """
    labels = collections.OrderedDict()
    for lid, L in level['labels'].items():
        pts = [collections.OrderedDict([('id', p['id']), ('x_pos', p['x_pos']), ('y_pos', p['y_pos'])])
               for p in L['points']]
        labels[lid] = collections.OrderedDict([('name', L['name']), ('clear_name', L['clear_name']), ('points', pts)])
    return collections.OrderedDict([('labels', labels), ('categories', level['categories'])])


def write_slim(level, ver):
    path = os.path.join(RES_DIR, RES_NAME[ver])
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(slim(level), f, ensure_ascii=False, separators=(',', ':'))
    return path


def akebi_crosscheck(res28, args, report):
    """Compare the shipped Akebi scene-9 dataset (runtime-scaled, then level->world) with the generated 2.8 one."""
    p = os.path.join(RES_DIR, 'map_golden_apple_archipelago.json')
    with open(p, encoding='utf-8') as f:
        ak = json.load(f)
    by = {L['clear_name']: L for L in ak['labels'].values()}

    def first(cn):
        return min(by[cn]['points'], key=lambda q: q['id'])
    # InteractiveMap::ApplyScaling, scene 9 anchors
    p1, p2 = first('PaleRedCrab'), first('GoldenCrab')
    n1, n2 = (-396.38, -253.75), (145.89, 215.34)
    sx = (n2[0] - n1[0]) / (p2['x_pos'] - p1['x_pos'])
    ox = n1[0] - p1['x_pos'] * sx
    sy = (n2[1] - n1[1]) / (p2['y_pos'] - p1['y_pos'])
    oy = n1[1] - p1['y_pos'] * sy

    gen = {}
    for lid, L in res28['out_labels'].items():
        gen[L['clear_name']] = [(q['x_pos'], q['y_pos'], q['id']) for q in L['points']]
    ents = res28['all_entities']
    rows = ['akebi_label\takebi_point_id\tworld_x\tworld_z\tnearest_same_label_id\tdist\tnearest_any_entity\tdist_any']
    migration = {}
    summary = []
    for lid, L in sorted(ak['labels'].items(), key=lambda kv: int(kv[0])):
        cn = L['clear_name']
        cand = gen.get(cn, [])
        within = collections.Counter()
        used = set()
        for q in sorted(L['points'], key=lambda q: q['id']):
            lx, ly = q['x_pos'] * sx + ox, q['y_pos'] * sy + oy
            wx, wz = ly, -lx
            best = min(cand, key=lambda c: dist2((wx, wz), c[:2])) if cand else None
            d = dist2((wx, wz), best[:2]) if best else float('inf')
            ba = min(ents, key=lambda c: dist2((wx, wz), c[:2]))
            da = dist2((wx, wz), ba[:2])
            for thr in (5, 10, 20):
                if d <= thr:
                    within[thr] += 1
            if best and d <= 15 and best[2] not in used:
                migration[str(q['id'])] = best[2]
                used.add(best[2])
            rows.append('%s\t%d\t%.1f\t%.1f\t%s\t%.1f\t%s\t%.1f' % (cn, q['id'], wx, wz, best[2] if best else '', d, ba[2], da))
        summary.append('%-34s akebi=%3d generated=%4d  matched<=5m=%3d <=10m=%3d <=20m=%3d' % (
            cn, len(L['points']), len(cand), within[5], within[10], within[20]))
    tsv = os.path.join(args.out, 'akebi_crosscheck_28.tsv')
    with open(tsv, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(rows) + '\n')
    mig = os.path.join(args.out, 'akebi_point_id_migration_28.json')
    with open(mig, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(collections.OrderedDict(sorted(migration.items(), key=lambda kv: int(kv[0]))), f, indent=1)
    report.append('=' * 100)
    report.append('Akebi scene-9 dataset vs generated 2.8 (Akebi raw -> ApplyScaling(PaleRedCrab,GoldenCrab) -> level -> world x=level.y, z=-level.x)')
    report.append('scale=(%.5f, %.5f) offset=(%.3f, %.3f)' % (sx, sy, ox, oy))
    report.extend(summary)
    report.append('per-point detail: %s ; id migration map (<=15 m, same clear_name): %s (%d entries)' % (tsv, mig, len(migration)))
    return [tsv, mig]


def cmd_build(args):
    label_ids, existing_pids = existing_label_ids()
    report = []
    res = {}
    files = []
    for ver in ('1.6', '2.8'):
        res[ver] = build_version(ver, args, label_ids, existing_pids, report)
        files += res[ver]['files']
    files += akebi_crosscheck(res['2.8'], args, report)
    rp = os.path.join(args.out, 'build_report.txt')
    with open(rp, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(report) + '\n')
    print('\n'.join(report))
    print('wrote:', rp, *files, sep='\n  ')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('cmd', choices=['survey', 'build', 'emit'])
    ap.add_argument('--out', default=os.path.join(HERE, 'out'))
    ap.add_argument('--lua16'), ap.add_argument('--txt16')
    ap.add_argument('--lua28'), ap.add_argument('--txt28')
    args = ap.parse_args()
    apply_args(args)
    args.emit = args.cmd == 'emit'
    if args.cmd == 'survey':
        cmd_survey(args)
    else:
        cmd_build(args)


if __name__ == '__main__':
    main()
