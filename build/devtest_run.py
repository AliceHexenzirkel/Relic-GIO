#!/usr/bin/env python3
"""Run a DevTest scenario: one game session with the DevTest build of the enhancements DLL, steps that drive and read
the game through the DevTest channel (build/devtest_driver.py), checks on the answers, and a report.

  python -B build/devtest_run.py <scenario.json> [--out FOLDER] [--dll PATH] [--no-launch] [--keep-game]
         [--build] [--runner EXE] [--window WxH|keep] [--own-files keep|replace]
         [--launch-timeout S] [--exit-timeout S] [--dir FOLDER] [--state FILE] [--output-log FILE]
  python -B build/devtest_run.py --selftest       # everything below on a stand-in for the game: no game, no runner

A run, in order:
  1. preflight: the scenario (includes resolved), the DLL (by default entertainment_experience/mod/bin/v<nn>/
     DevTest-x64/CLibrary.dll: it must carry the DevTest marker), the runner (built when it is missing, or with
     --build: dotnet build entertainment_experience/devtest/runner/DevTestRunner.csproj -c Release, one MSBuild at a
     time), the runner's own preflight (DevTestRunner check: nothing is changed), the agent and its token when the
     scenario sends GM commands;
  2. DevTestRunner play is started detached, without a window: it stages the payload (D:\\relic-devtest\\stage), runs
     Relic's own PlaySession and puts everything back once the game is closed. It and every other program started
     here get this script's environment without the scenario's secrets (RELIC_DEVTEST_*, server.tokenEnv);
  3. the run waits for a fresh status.json that carries the marker and the staged DLL's sha256, then checks that the
     game opened in the window the runner asked for (status.screen, else the size of the back buffer): a game that
     went fullscreen anyway is closed at once, its steps skipped;
  4. the steps run in order, each checked: a step passes when its answer says ok, its outcome is no failure
     (timeout, sceneChanged, noFrame, ...) and every `expect` holds;
  5. quit -- whenever a DevTest build was seen serving the channel, whatever went wrong (a failed step, a Ctrl+C,
     the wrong build) -- unless the scenario quit already, or --keep-game; then the wait for the runner: the live
     profile restored, Fiddler stopped, the release DLL back in the game folder (each a check, and so is a runner
     that has not ended by --exit-timeout);
  6. the report: <out>/report.json + report.html (self-contained: inline styles, links to the files beside it) +
     shots/ + runner.log + devtest.log + board.txt (the diag board since the start) + output_log.tail.txt.
The exit code is the number of failed checks (a run that could not start counts as one). Reports go to
D:\\relic-devtest\\reports\\<scenario>-<yyyymmdd-hhmmss>\\ by default, never into the repository.

A scenario (entertainment_experience/devtest/scenarios/*.json):
  {"name": "...", "version": "2.8",
   "server": {"expectHost": "${RELIC_DEVTEST_HOST}", "agent": "${RELIC_DEVTEST_AGENT}", "tokenEnv": "RELIC_DEVTEST_TOKEN"},
   "runner": {"window": "1600x900", "ownFiles": "keep"},
   "config": {<the dev build's config: a profile tree, or a whole document with "profiles">},
   "session": {"noFaults": true, "maxPresentStall": 2.0},
   "steps": [{"label": "...", "op": "<a DLL op, or gm | measure | sleep>", ...the op's fields...,
              "expect": {"<dotted.path>": <value or matcher>}, "onFail": "stop"|"continue", "answerTimeout": S,
              "always": true|false},
             {"include": "<another scenario file>"}]}
  A failed step stops the steps after it (onFail: stop, the default) but for those with "always": true, which run
  whatever happened before them: the cleanup a scenario owes the server (a GM switch put back). What a scenario
  changes in the game itself, the final quit puts back.
  ${NAME} is an environment variable (host, agent and token are never written into a scenario). A value may also be
  {"$status": "<path>"} (the latest status), {"$answer": "<label>.<path>"} (an earlier step's answer) or
  {"$offset": [dx, dy, dz]} (the latest status position plus an offset; "from": "<label>.<path>" for another base).
  A matcher is a value (equality) or an object of in, min, max, regex, exists, contains, len, eq, not.
  Runner-side ops: gm {msg, uid?} (POST /command to the agent with the bearer token from the environment variable
  tokenEnv names -- never printed, never written), measure {seconds} (the status sampled: present / presentVt /
  handlerRun rates split by focus, the longest in-game presentVt stall), sleep {seconds}.

Standard library only; run it with python -B.
"""
import argparse
import base64
import hashlib
import html
import http.server
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import devtest_driver as dd  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEVTEST_ROOT = r"D:\relic-devtest"
REPORTS_DIR = os.path.join(DEVTEST_ROOT, "reports")
RUNNER_DIR = os.path.join(REPO, "entertainment_experience", "devtest", "runner")
RUNNER_CSPROJ = os.path.join(RUNNER_DIR, "DevTestRunner.csproj")
RUNNER_EXE = os.path.join(RUNNER_DIR, "bin", "Release", "net10.0-windows", "DevTestRunner.exe")
SCENARIOS_DIR = os.path.join(REPO, "entertainment_experience", "devtest", "scenarios")
CREATE_NO_WINDOW = 0x08000000
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_BREAKAWAY_FROM_JOB = 0x01000000

DLL_OPS = ("ping", "status", "wait", "shot", "mark", "quit", "teleport", "camera", "entities", "avatar", "hud", "overlay",
           "feature", "ui.tree", "ui.click", "ui.text", "login", "enter")
RUNNER_OPS = ("gm", "measure", "sleep")
STEP_ONLY = ("label", "expect", "onFail", "answerTimeout", "note", "always")
# The scenario's secrets (the agent token, the test account's password) live in variables of this prefix, and in the
# one server.tokenEnv names: no program started from here gets them.
SECRET_PREFIX = "RELIC_DEVTEST_"
# Outcomes that end an action without what it was for (the DLL answers ok:false with them; an answer that says ok
# beside one of them fails all the same).
FAIL_OUTCOMES = ("timeout", "sceneChanged", "noFrame", "failed", "replaced", "notStarted", "freeCameraOff", "faulted",
                 "servingStopped", "watchdog", "quit", "cancelled", "error")
OPERATORS = ("in", "min", "max", "regex", "exists", "contains", "len", "eq", "not")
REDACTED_KEYS = ("password", "token", "authorization")
ENV_RE = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")
TAIL_BYTES = 256 * 1024


class RunError(Exception):
    """The run cannot go on (before or after the steps)."""


class StepError(Exception):
    """One step could not be carried out."""


class MissingEnv(Exception):
    def __init__(self, name):
        super().__init__("the environment variable {} is not set".format(name))
        self.name = name


class _Missing:
    def __repr__(self):
        return "<missing>"


MISSING = _Missing()


# -- values, paths and matchers ---------------------------------------------------------------------------------------

def expand_env(text, env=None):
    env = os.environ if env is None else env

    def sub(m):
        value = env.get(m.group(1))
        if value is None or value == "":
            raise MissingEnv(m.group(1))
        return value
    return ENV_RE.sub(sub, text)


def get_path(obj, path):
    """A dotted path into nested objects and lists ('pos.0', 'camera.held'); MISSING where it leads nowhere."""
    cur = obj
    for part in (path.split(".") if path else []):
        if isinstance(cur, dict) and part in cur:
            cur = cur[part]
        elif isinstance(cur, list) and re.fullmatch(r"-?\d+", part) and -len(cur) <= int(part) < len(cur):
            cur = cur[int(part)]
        else:
            return MISSING
    return cur


def is_number(x):
    return isinstance(x, (int, float)) and not isinstance(x, bool)


def deep_equal(a, b):
    if isinstance(a, bool) or isinstance(b, bool):
        return isinstance(a, bool) and isinstance(b, bool) and a == b
    if is_number(a) and is_number(b):
        return float(a) == float(b)
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(deep_equal(a[k], b[k]) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(deep_equal(x, y) for x, y in zip(a, b))
    return a == b


def short(value, limit=120):
    if value is MISSING:
        return "missing"
    text = json.dumps(value, ensure_ascii=False, default=str)
    return text if len(text) <= limit else text[:limit] + "..."


def is_matcher(expected):
    return isinstance(expected, dict) and bool(expected) and all(k in OPERATORS for k in expected)


def match(expected, actual):
    """Does `actual` meet `expected`? -> (ok, why). A matcher object applies its operators; any other object is a
    pattern its keys must meet (other keys of `actual` are free); a list is matched element by element; anything else
    is equality (numbers by value, booleans only by booleans)."""
    if is_matcher(expected):
        for op, arg in expected.items():
            ok, why = apply_operator(op, arg, actual)
            if not ok:
                return False, why
        return True, None
    if isinstance(expected, dict):
        if not isinstance(actual, dict):
            return False, "is {}, not an object".format(short(actual))
        for key, sub in expected.items():
            ok, why = match(sub, actual.get(key, MISSING))
            if not ok:
                return False, "{}: {}".format(key, why)
        return True, None
    if isinstance(expected, list):
        if not isinstance(actual, list) or len(actual) != len(expected):
            return False, "is {}, not a list of {}".format(short(actual), len(expected))
        for i, (e, a) in enumerate(zip(expected, actual)):
            ok, why = match(e, a)
            if not ok:
                return False, "[{}]: {}".format(i, why)
        return True, None
    return (True, None) if deep_equal(expected, actual) else (False, "is {}".format(short(actual)))


def apply_operator(op, arg, actual):
    if op == "exists":
        there = actual is not MISSING
        return (there == bool(arg)), ("is missing" if not there else "is there: {}".format(short(actual)))
    if op == "not":
        ok, _ = match(arg, actual)
        return (not ok), "meets {}".format(short(arg))
    if actual is MISSING:
        return False, "is missing"
    if op == "eq":
        return deep_equal(arg, actual), "is {}".format(short(actual))
    if op == "in":
        return any(deep_equal(x, actual) for x in (arg if isinstance(arg, list) else [arg])), "is {}".format(short(actual))
    if op in ("min", "max"):
        if not is_number(actual):
            return False, "is {}, not a number".format(short(actual))
        ok = actual >= arg if op == "min" else actual <= arg
        return ok, "is {}".format(short(actual))
    if op == "regex":
        text = actual if isinstance(actual, str) else json.dumps(actual, ensure_ascii=False)
        return re.search(arg, text) is not None, "is {}".format(short(actual))
    if op == "len":
        if not isinstance(actual, (list, str, dict)):
            return False, "is {}, which has no length".format(short(actual))
        ok, why = match(arg, len(actual))
        return ok, "has length {}".format(len(actual))
    if op == "contains":
        items = arg if isinstance(arg, list) else [arg]
        if isinstance(actual, str):
            missing = [i for i in items if not (isinstance(i, str) and i in actual)]
            return not missing, "lacks {}".format(short(missing))
        if not isinstance(actual, list):
            return False, "is {}, not a list".format(short(actual))
        for item in items:
            if not any(match(item, el)[0] for el in actual):
                return False, "has no element that meets {}".format(short(item))
        return True, None
    return False, "unknown matcher {}".format(op)


def check_expect(expect, answer):
    checks = []
    for path, expected in (expect or {}).items():
        actual = get_path(answer, path)
        ok, why = match(expected, actual)
        checks.append({"path": path, "expected": expected, "actual": None if actual is MISSING else actual,
                       "ok": ok, "why": None if ok else why})
    return checks


def implicit_checks(expect, answer):
    """What every step is held to unless its expect says otherwise: ok, and an outcome that is no failure."""
    checks = []
    expect = expect or {}
    if "ok" not in expect:
        ok = answer.get("ok") is True
        checks.append({"path": "ok", "expected": True, "actual": answer.get("ok"), "ok": ok,
                       "why": None if ok else (answer.get("error") or "is {}".format(short(answer.get("ok", MISSING))))})
    if "outcome" not in expect and answer.get("outcome") in FAIL_OUTCOMES:
        checks.append({"path": "outcome", "expected": {"not": {"in": list(FAIL_OUTCOMES)}}, "actual": answer.get("outcome"),
                       "ok": False, "why": "the action ended with {}".format(answer.get("outcome"))})
    return checks


def redact(obj):
    if isinstance(obj, dict):
        return {k: ("***" if any(r in k.lower() for r in REDACTED_KEYS) else redact(v)) for k, v in obj.items()}
    if isinstance(obj, list):
        return [redact(v) for v in obj]
    return obj


class Context:
    """What a step's values are resolved against: the latest status and the answers of the labelled steps."""

    def __init__(self, chan, output_log=None, slot_output_log=None, output_log_fixed=False):
        self.chan = chan
        self.answers = {}
        self.quit_sent = False
        self.output_log = output_log
        self.slot_output_log = slot_output_log
        self.output_log_fixed = output_log_fixed   # --output-log: that file, whatever the game does

    def status(self):
        st = self.chan.status() if self.chan is not None else None
        st = st or (self.chan.last_status if self.chan is not None else None)
        if st is None:
            raise StepError("there is no status to take a value from")
        return st

    def ref(self, ref):
        """'<label>.<path>' of an earlier answer."""
        label, _, path = str(ref).partition(".")
        if label not in self.answers:
            raise StepError("no step labelled '{}' has answered".format(label))
        value = get_path(self.answers[label], path)
        if value is MISSING:
            raise StepError("the answer of '{}' has no {}".format(label, path))
        return value

    def resolve(self, value):
        if isinstance(value, str):
            return expand_env(value)
        if isinstance(value, list):
            return [self.resolve(v) for v in value]
        if isinstance(value, dict):
            if set(value) == {"$status"}:
                v = get_path(self.status(), value["$status"])
                if v is MISSING:
                    raise StepError("the status has no {}".format(value["$status"]))
                return v
            if set(value) == {"$answer"}:
                return self.ref(value["$answer"])
            if "$offset" in value and set(value) <= {"$offset", "from"}:
                base = self.ref(value["from"]) if "from" in value else get_path(self.status(), "pos")
                off = value["$offset"]
                if not (isinstance(base, list) and len(base) == 3 and all(is_number(v) for v in base)):
                    raise StepError("{} is no position".format(short(base)))
                if not (isinstance(off, list) and len(off) == 3 and all(is_number(v) for v in off)):
                    raise StepError("$offset is [dx, dy, dz]")
                return [round(float(b) + float(o), 3) for b, o in zip(base, off)]
            return {k: self.resolve(v) for k, v in value.items()}
        return value


# -- the scenario ------------------------------------------------------------------------------------------------------

def load_scenario(path, depth=0):
    if depth > 8:
        raise RunError("{}: includes nest deeper than 8".format(path))
    try:
        with open(path, "r", encoding="utf-8") as f:
            sc = json.load(f)
    except (OSError, ValueError) as e:
        raise RunError("{}: {}".format(path, e))
    if not isinstance(sc, dict):
        raise RunError("{}: a scenario is a JSON object".format(path))
    steps = []
    for i, st in enumerate(sc.get("steps") or []):
        if not isinstance(st, dict):
            raise RunError("{}: step {} is not an object".format(path, i + 1))
        if "include" in st:
            inc = os.path.join(os.path.dirname(os.path.abspath(path)), str(st["include"]))
            steps.extend(load_scenario(inc, depth + 1)["steps"])
            continue
        op = st.get("op")
        if not isinstance(op, str) or not op:
            raise RunError("{}: step {} has no op".format(path, i + 1))
        if "always" in st and not isinstance(st["always"], bool):
            raise RunError("{}: step {} has an always that is not true or false".format(path, i + 1))
        steps.append(st)
    sc["steps"] = steps
    if depth == 0:
        for key in ("name", "version"):
            if not isinstance(sc.get(key), str) or not sc[key]:
                raise RunError("{}: a scenario names its {}".format(path, key))
    return sc


def safe_name(text):
    return re.sub(r"[^A-Za-z0-9._-]+", "-", str(text)).strip("-")[:60] or "step"


# -- measurements -------------------------------------------------------------------------------------------------------

class Sampler(threading.Thread):
    """status.json read twice a second for the whole session: what the counters did, and whether the window had the
    focus meanwhile."""

    def __init__(self, status_path, period=0.5):
        super().__init__(daemon=True)
        self.path = status_path
        self.period = period
        self.samples = []
        self.lock = threading.Lock()
        self.stop_event = threading.Event()
        self._last = None

    def run(self):
        while not self.stop_event.wait(self.period):
            try:
                written = os.stat(self.path).st_mtime
            except OSError:
                continue
            if time.time() - written > dd.STATUS_FRESH:
                continue
            st = dd.read_json(self.path)
            if st is None:
                continue
            key = (st.get("seq"), st.get("wallMs"))
            if key == self._last:
                continue
            self._last = key
            c = st.get("counters") if isinstance(st.get("counters"), dict) else {}
            sample = {"t": time.time(), "wallMs": st.get("wallMs") if is_number(st.get("wallMs")) else int(written * 1000),
                      "seq": st.get("seq"), "focused": st.get("focused"), "inGame": st.get("inGame"), "scene": st.get("scene")}
            for k in ("present", "presentVt", "handlerRun", "faults"):
                sample[k] = c.get(k) if is_number(c.get(k)) else None
            with self.lock:
                self.samples.append(sample)

    def window(self, t0, t1=None):
        with self.lock:
            return [s for s in self.samples if s["t"] >= t0 and (t1 is None or s["t"] <= t1)]

    def stop(self):
        self.stop_event.set()
        self.join(5)


def rates(samples):
    """Rates per second split by focus (pairs of samples of one focus state), and the longest stretch in the world
    over which presentVt did not move."""
    groups = {}
    for a, b in zip(samples, samples[1:]):
        dt = (b["wallMs"] - a["wallMs"]) / 1000.0 if is_number(a.get("wallMs")) and is_number(b.get("wallMs")) else 0
        if dt <= 0 or dt > 10 or a.get("focused") != b.get("focused"):
            continue
        g = groups.setdefault("focused" if b.get("focused") else "unfocused",
                              {"seconds": 0.0, "pairs": 0, "present": 0, "presentVt": 0, "handlerRun": 0})
        g["seconds"] += dt
        g["pairs"] += 1
        for k in ("present", "presentVt", "handlerRun"):
            if is_number(a.get(k)) and is_number(b.get(k)) and b[k] >= a[k]:
                g[k] += b[k] - a[k]
    out = {}
    for key, g in groups.items():
        s = g["seconds"]
        out[key] = {"seconds": round(s, 1), "pairs": g["pairs"]}
        for k in ("present", "presentVt", "handlerRun"):
            out[key][k + "PerSec"] = round(g[k] / s, 1) if s > 0 else None
    stall, since, prev = 0.0, None, None
    for smp in samples:
        if smp.get("inGame") is not True or not is_number(smp.get("presentVt")) or not is_number(smp.get("wallMs")):
            since = prev = None
            continue
        if prev is None or smp["presentVt"] > prev["presentVt"]:
            since = smp["wallMs"]
        else:
            stall = max(stall, (smp["wallMs"] - since) / 1000.0)
        prev = smp
    out["maxPresentStall"] = round(stall, 2)
    out["samples"] = len(samples)
    out["inGameSamples"] = sum(1 for s in samples if s.get("inGame") is True)
    out["focusedSamples"] = sum(1 for s in samples if s.get("focused") is True)
    out["unfocusedSamples"] = sum(1 for s in samples if s.get("focused") is False)
    return out


BOARD_RE = re.compile(r"^\[\s*(\d+)\]\s+(.*?)\s*\|\s*fault:\s*(.*?)\s*\|\s*(.*)$")


def parse_board(text):
    lines = []
    for raw in text.splitlines():
        m = BOARD_RE.match(raw.strip())
        if not m:
            continue
        counters = {}
        for kv in m.group(2).split():
            k, _, v = kv.partition("=")
            if re.fullmatch(r"-?\d+", v):
                counters[k] = int(v)
        lines.append({"n": int(m.group(1)), "counters": counters, "fault": m.group(3), "note": m.group(4)})
    return lines


def board_summary(lines):
    """The diag board since the start: the faults of every process, the rates and the longest presentVt stall of
    the last one (a line numbered 0 starts a process; each line is 2 s)."""
    if not lines:
        return {"lines": 0}
    starts = [i for i, l in enumerate(lines) if l["n"] == 0]
    last = lines[starts[-1]:] if starts else lines
    run = longest = 0
    for a, b in zip(last, last[1:]):
        if b["counters"].get("presentVt") == a["counters"].get("presentVt"):
            run += 1
            longest = max(longest, run)
        else:
            run = 0

    def rate(k):
        first, final = last[0]["counters"].get(k), last[-1]["counters"].get(k)
        return round((final - first) / (2.0 * (len(last) - 1)), 1) if len(last) > 1 and first is not None and final is not None else None
    return {"lines": len(lines), "processes": max(1, len(starts)), "faults": max(l["counters"].get("faults", 0) for l in lines),
            "lastFault": lines[-1]["fault"], "presentPerSec": rate("present"), "presentVtPerSec": rate("presentVt"),
            "handlerRunPerSec": rate("handlerRun"), "longestPresentVtStallSeconds": 2 * longest}


# -- files ----------------------------------------------------------------------------------------------------------------

def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def carries_marker(path):
    """The marker as ASCII or UTF-16LE anywhere in the file, read 1 MiB at a time -- the rule of the release gates
    (build/pe_imports.ps1 Test-DevBuildMarker and its twin in entertainment_experience/tools/payload_manifest.py),
    which this copy follows when they change."""
    patterns = (dd.MARKER.encode("ascii"), dd.MARKER.encode("utf-16-le"))
    keep = max(len(p) for p in patterns) - 1
    tail = b""
    with open(path, "rb") as f:
        while True:
            chunk = f.read(1 << 20)
            if not chunk:
                return False
            data = tail + chunk
            if any(p in data for p in patterns):
                return True
            tail = data[-keep:]


def size_of(path):
    try:
        return os.path.getsize(path)
    except OSError:
        return 0


def read_from(path, offset, limit=64 * 1024 * 1024):
    """The bytes a file gained after `offset` (all of it when it is shorter now: replaced meanwhile), at most `limit`."""
    try:
        with open(path, "rb") as f:
            end = f.seek(0, os.SEEK_END)
            start = offset if 0 <= offset <= end else 0
            start = max(start, end - limit)
            f.seek(start)
            return f.read()
    except OSError:
        return b""


def tail_of(path, limit=TAIL_BYTES):
    return read_from(path, size_of(path) - limit if size_of(path) > limit else 0, limit)


def write_atomic(path, data):
    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(data)
    dd.unhindered(lambda: os.replace(tmp, path))


def child_env(token_env=None):
    """This script's environment for a program it starts, without the scenario's secrets: every RELIC_DEVTEST_*
    variable and the one server.tokenEnv names. The runner, the game and Fiddler it starts, and every tool started
    here get none of them -- nor does a crash dump of any of them."""
    drop = str(token_env or "").upper()
    return {k: v for k, v in os.environ.items() if not k.upper().startswith(SECRET_PREFIX) and k.upper() != drop}


def no_window():
    """creationflags for a console program started from here: never a console window of its own (from a parent
    without a console each one would open one, and a new window takes the focus)."""
    return CREATE_NO_WINDOW if os.name == "nt" else 0


def git(*args, env=None):
    """A read-only git question (--no-optional-locks: not even the index's stat cache is refreshed)."""
    try:
        r = subprocess.run(["git", "--no-optional-locks", "-C", REPO] + list(args), capture_output=True, text=True, timeout=60,
                           env=env if env is not None else child_env(), creationflags=no_window())
        return r.stdout.strip() if r.returncode == 0 else None
    except (OSError, subprocess.SubprocessError):
        return None


def installed_launcher_root():
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Uninstall\{B7A6F5E2-9C41-4E7A-9F2D-RELIC0000001}_is1") as k:
            loc, _ = winreg.QueryValueEx(k, "InstallLocation")
            if loc and os.path.isfile(os.path.join(loc, "Relic.exe")):
                return os.path.abspath(loc)
    except (ImportError, OSError):
        pass
    d = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "Relic")
    return d if os.path.isfile(os.path.join(d, "Relic.exe")) else None


def payload_fingerprint(root, version):
    """sha256 of a payload's manifest and of the version's enhancements DLL there (None where a file is missing)."""
    if root is None:
        return None
    out = {}
    manifest = os.path.join(root, "manifest.json")
    out["manifest.json"] = sha256_file(manifest) if os.path.isfile(manifest) else None
    dll = os.path.join(root, version, "ee", "CLibrary.dll.bin")
    out["{}/ee/CLibrary.dll.bin".format(version)] = sha256_file(dll) if os.path.isfile(dll) else None
    return out


def output_log_path():
    return os.path.join(os.environ.get("USERPROFILE", ""), "AppData", "LocalLow", "miHoYo", "Genshin Impact", "output_log.txt")


DEVTEST_STAGE = os.path.join(DEVTEST_ROOT, "stage")


def default_dll(version):
    """The DevTest build of a version: build_ee.ps1 -Config DevTest leaves it in the mod's bin folder, and -StageDev
    D:\\relic-devtest\\stage copies it to <stage>\\<version>\\CLibrary.dll. The newer of the two that are there."""
    candidates = [os.path.join(REPO, "entertainment_experience", "mod", "bin", "v" + version.replace(".", ""), "DevTest-x64", "CLibrary.dll"),
                  os.path.join(DEVTEST_STAGE, version, "CLibrary.dll")]
    there = [p for p in candidates if os.path.isfile(p)]
    return max(there, key=os.path.getmtime) if there else candidates[0]


# -- the agent ------------------------------------------------------------------------------------------------------------

def agent_command(agent, token, version, uid, msg, timeout=20.0):
    """POST /command {version, uid, msg} with the bearer token: the agent signs the GM command on the box. Never
    through a proxy -- Fiddler is the system proxy while a session runs. The token goes into the header and nowhere
    else."""
    base = agent if agent.startswith(("http://", "https://")) else "http://" + agent
    url = base.rstrip("/") + "/command"
    body = json.dumps({"version": version, "uid": uid, "msg": msg}).encode("utf-8")
    req = urllib.request.Request(url, data=body, method="POST",
                                 headers={"Content-Type": "application/json", "Authorization": "Bearer " + token})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(req, timeout=timeout) as r:
            code, text = r.status, r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        code, text = e.code, e.read().decode("utf-8", "replace")
    except (urllib.error.URLError, OSError) as e:
        raise StepError("the agent at {} did not answer: {}".format(url, getattr(e, "reason", e)))
    try:
        obj = json.loads(text)
        if not isinstance(obj, dict):
            obj = {"answer": obj}
    except ValueError:
        obj = {"text": text[:2000]}
    obj.setdefault("ok", False)
    obj["httpStatus"] = code
    return obj


# -- the runner -------------------------------------------------------------------------------------------------------------

# `dotnet run` and `dotnet test` are not among them: they stay up as long as what they run, long after their build.
BUILD_VERBS = ("build", "publish", "msbuild", "pack", "restore", "clean")
# what Get-CimInstance prints for every process that may be a build: <name>|<pid>|<command line>, one line each
PROCESS_QUERY = ("Get-CimInstance Win32_Process -Filter \"Name='MSBuild.exe' OR Name='dotnet.exe' OR Name='cl.exe' OR "
                 "Name='link.exe'\" | ForEach-Object { '{0}|{1}|{2}' -f $_.Name, $_.ProcessId, ($_.CommandLine -replace '[\\r\\n]+', ' ') }")


def build_in(processes):
    """The build at work among (name, pid, command line) rows, or None. A compiler or linker, an MSBuild.exe, a
    dotnet hosting MSBuild.dll or running one of the SDK's build verbs (`dotnet build`, `dotnet publish`, ...). A
    reusable MSBuild node (/nodemode:) is no build by itself: an idle one stays for minutes after its build ended,
    and a busy one has its build's own process beside it."""
    for name, pid, cmdline in processes:
        lname, line = name.lower(), (cmdline or "").lower()
        if lname in ("cl.exe", "link.exe"):
            return "{} (pid {})".format(name, pid)
        if re.search(r"[/-]nodemode:", line):
            continue
        if lname == "msbuild.exe" or "msbuild.dll" in line:
            return "{} (pid {})".format(name, pid)
        if lname == "dotnet.exe":
            m = re.match(r'\s*(?:"[^"]*"|\S+)\s+(\S+)', cmdline or "")
            if m and m.group(1).lower() in BUILD_VERBS:
                return "dotnet {} (pid {})".format(m.group(1).lower(), pid)
    return None


def build_running(env=None):
    """What build is at work on this PC, or None: one build at a time here (the memory is shared with the owner's
    VM). The processes are asked of Windows with their command lines; where that cannot be done, any MSBuild.exe
    counts."""
    try:
        script = base64.b64encode(PROCESS_QUERY.encode("utf-16-le")).decode("ascii")
        r = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", script], capture_output=True,
                           text=True, timeout=60, env=env if env is not None else child_env(), creationflags=no_window())
        if r.returncode == 0:
            rows = [tuple((line.split("|", 2) + ["", ""])[:3]) for line in r.stdout.splitlines() if line.count("|") >= 2]
            return build_in(rows)
    except (OSError, subprocess.SubprocessError):
        pass
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq MSBuild.exe", "/NH"], capture_output=True, text=True, timeout=30,
                             env=env if env is not None else child_env(), creationflags=no_window()).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    return "MSBuild.exe" if "msbuild.exe" in out.lower() else None


def build_runner(say, env):
    deadline = time.time() + 900
    while True:
        busy = build_running(env)
        if busy is None:
            break
        if time.time() > deadline:
            raise RunError("a build has been at work for 15 minutes ({}) -- build the runner when it is done".format(busy))
        say("a build is at work ({}) -- one build at a time on this PC: waiting".format(busy))
        time.sleep(15)
    say("building the runner: dotnet build {} -c Release".format(os.path.relpath(RUNNER_CSPROJ, REPO)))
    r = subprocess.run(["dotnet", "build", RUNNER_CSPROJ, "-c", "Release", "-nologo", "-v:minimal", "-nodeReuse:false"],
                       cwd=REPO, capture_output=True, text=True, env=env, creationflags=no_window())
    if r.returncode != 0:
        raise RunError("the runner did not build:\n" + (r.stdout + r.stderr)[-4000:])


def start_detached(argv, cwd, env):
    """A console program started without a window, in a process group of its own (a Ctrl+C here never reaches it),
    out of this process's job where the job allows it: the runner must finish its session whatever happens here."""
    flags = no_window() | CREATE_NEW_PROCESS_GROUP
    kw = dict(stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, cwd=cwd, close_fds=True, env=env)
    try:
        return subprocess.Popen(argv, creationflags=flags | CREATE_BREAKAWAY_FROM_JOB, **kw)
    except OSError:
        return subprocess.Popen(argv, creationflags=flags, **kw)


# -- the report -------------------------------------------------------------------------------------------------------------

class Report:
    def __init__(self, out, scenario_path, scenario):
        self.out = out
        self.data = {"scenario": scenario.get("name"), "version": scenario.get("version"),
                     "scenarioFile": os.path.abspath(scenario_path) if scenario_path else None,
                     "description": scenario.get("description"), "startedAt": datetime.now().isoformat(timespec="seconds"),
                     "endedAt": None, "build": {}, "dll": {}, "steps": [], "session": [], "measurements": [], "notes": [],
                     "files": {}, "failedChecks": 0}
        self._log = open(os.path.join(out, "run.log"), "a", encoding="utf-8")

    def say(self, text):
        line = "{} {}".format(datetime.now().strftime("%H:%M:%S"), text)
        print(line, flush=True)
        self._log.write(line + "\n")
        self._log.flush()

    def note(self, text):
        self.data["notes"].append(text)
        self.say("note: " + text)

    def session_check(self, name, ok, detail=None):
        self.data["session"].append({"name": name, "ok": bool(ok), "detail": detail})
        self.say("{} {}{}".format("PASS" if ok else "FAIL", name, "" if detail is None else " -- {}".format(detail)))

    def failed(self):
        n = sum(1 for s in self.data["session"] if not s["ok"])
        for st in self.data["steps"]:
            if st["verdict"] == "error":
                n += 1
            n += sum(1 for c in st.get("checks", []) if not c["ok"])
        return n

    def write(self):
        self.data["endedAt"] = datetime.now().isoformat(timespec="seconds")
        self.data["failedChecks"] = self.failed()
        write_atomic(os.path.join(self.out, "report.json"),
                     json.dumps(self.data, indent=2, ensure_ascii=False, default=str).encode("utf-8"))
        write_atomic(os.path.join(self.out, "report.html"), render_html(self.data).encode("utf-8"))

    def close(self):
        self._log.close()


CSS = """
:root{--bg:#f6f7f9;--fg:#1d2330;--muted:#5d6676;--card:#fff;--line:#d9dde5;--pass:#17803d;--fail:#b42318;--skip:#8a6d1d;--code:#eef1f6}
@media (prefers-color-scheme: dark){:root{--bg:#14171d;--fg:#e4e8ef;--muted:#9aa3b2;--card:#1c2028;--line:#323846;--pass:#4ac16f;--fail:#ff6b5e;--skip:#d9b44a;--code:#232834}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif}
main{max-width:1180px;margin:0 auto;padding:20px 16px 48px}h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 10px}
.sub{color:var(--muted);margin:0 0 16px}.card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px 14px;margin:10px 0;overflow-x:auto}
table{border-collapse:collapse;width:100%}td,th{text-align:left;vertical-align:top;padding:6px 8px;border-bottom:1px solid var(--line)}th{color:var(--muted);font-weight:600}
.b{display:inline-block;padding:1px 8px;border-radius:10px;font-size:12px;font-weight:700;color:#fff}.pass{background:var(--pass)}.fail,.error{background:var(--fail)}.skipped{background:var(--skip)}
.big{font-size:18px;font-weight:700}.okc{color:var(--pass)}.badc{color:var(--fail)}code,pre{font:12px/1.4 ui-monospace,Consolas,monospace;background:var(--code);border-radius:4px}
pre{padding:8px;white-space:pre-wrap;word-break:break-word;max-height:360px;overflow:auto;margin:6px 0}code{padding:0 4px}details summary{cursor:pointer;color:var(--muted)}
.shots{display:flex;flex-wrap:wrap;gap:10px;margin-top:6px}.shots a{display:block;width:300px;max-width:100%}.shots img{width:100%;height:auto;border:1px solid var(--line);border-radius:4px;display:block;image-rendering:auto}
.shots span{font-size:12px;color:var(--muted)}ul.checks{margin:4px 0 0;padding-left:18px}
"""


def _e(x):
    return html.escape(str(x), quote=True)


def _json_block(obj):
    return "<pre>{}</pre>".format(_e(json.dumps(obj, indent=2, ensure_ascii=False, default=str)))


def render_html(d):
    failed = d.get("failedChecks", 0)
    verdict = '<span class="b {}">{}</span>'.format("pass" if failed == 0 else "fail",
                                                    "all checks passed" if failed == 0 else "{} failed".format(failed))
    p = ["<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">",
         "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">",
         "<title>DevTest Report</title><style>{}</style></head><body><main>".format(CSS),
         "<h1>{}</h1>".format(_e(d.get("scenario"))),
         "<p class=\"sub\">version {} &middot; {} &ndash; {} &middot; {}</p>".format(
             _e(d.get("version")), _e(d.get("startedAt")), _e(d.get("endedAt")), verdict)]
    if d.get("description"):
        p.append("<p>{}</p>".format(_e(d["description"])))
    rows = [("DevTest DLL", d.get("dll", {}).get("path")), ("its sha256", d.get("dll", {}).get("sha256")),
            ("the running DLL names", d.get("dll", {}).get("statusSha256")), ("repository HEAD", d.get("build", {}).get("gitHead")),
            ("runner exit", (d.get("runner") or {}).get("exit"))]
    p.append("<div class=\"card\"><table>{}</table></div>".format("".join(
        "<tr><th>{}</th><td><code>{}</code></td></tr>".format(_e(k), _e(v)) for k, v in rows if v is not None)))

    p.append("<h2>Steps</h2>")
    for st in d.get("steps", []):
        p.append("<div class=\"card\"><div><span class=\"b {v}\">{v}</span> <b>{n}. {l}</b> <code>{op}</code> "
                 "<span class=\"sub\">{ms} ms</span></div>".format(v=_e(st.get("verdict")), n=st.get("index"), l=_e(st.get("label")),
                                                                    op=_e(st.get("op")), ms=_e(st.get("ms"))))
        if st.get("error"):
            p.append("<p class=\"badc\">{}</p>".format(_e(st["error"])))
        if st.get("checks"):
            p.append("<ul class=\"checks\">")
            for c in st["checks"]:
                p.append("<li class=\"{}\"><code>{}</code> {} {}</li>".format(
                    "okc" if c["ok"] else "badc", _e(c["path"]), _e(short(c["expected"])),
                    "&#10003;" if c["ok"] else "&#10007; " + _e(c.get("why"))))
            p.append("</ul>")
        if st.get("shots"):
            p.append("<div class=\"shots\">")
            for s in st["shots"]:
                p.append("<a href=\"{0}\"><img src=\"{0}\" loading=\"lazy\" alt=\"{1}\"><span>{1}</span></a>".format(
                    _e(s.replace("\\", "/")), _e(os.path.basename(s))))
            p.append("</div>")
        if "request" in st:
            p.append("<details><summary>request</summary>{}</details>".format(_json_block(st["request"])))
        if "answer" in st:
            p.append("<details><summary>answer</summary>{}</details>".format(_json_block(st["answer"])))
        p.append("</div>")

    if d.get("session"):
        p.append("<h2>Session</h2><div class=\"card\"><table>")
        for s in d["session"]:
            p.append("<tr><td><span class=\"b {0}\">{0}</span></td><td>{1}</td><td>{2}</td></tr>".format(
                "pass" if s["ok"] else "fail", _e(s["name"]), _e(s.get("detail") or "")))
        p.append("</table></div>")
    if d.get("measurements"):
        p.append("<h2>Measurements</h2>")
        for m in d["measurements"]:
            p.append("<div class=\"card\"><b>{}</b>{}</div>".format(_e(m.get("label")), _json_block(m.get("result"))))
    if d.get("board"):
        p.append("<h2>Diag board</h2><div class=\"card\">{}</div>".format(_json_block(d["board"])))
    if d.get("notes"):
        p.append("<h2>Notes</h2><div class=\"card\"><ul>{}</ul></div>".format("".join("<li>{}</li>".format(_e(n)) for n in d["notes"])))
    files = d.get("files") or {}
    if files:
        p.append("<h2>Files</h2><div class=\"card\"><ul>{}</ul></div>".format("".join(
            "<li><a href=\"{0}\">{1}</a></li>".format(_e(v.replace("\\", "/")), _e(k)) for k, v in sorted(files.items()))))
    p.append("</main></body></html>")
    return "\n".join(p)


# -- a run ------------------------------------------------------------------------------------------------------------------

def run(args):
    sc = load_scenario(args.scenario)
    name, version = sc["name"], sc["version"]
    out = os.path.abspath(args.out or os.path.join(REPORTS_DIR, "{}-{}".format(safe_name(name), datetime.now().strftime("%Y%m%d-%H%M%S"))))
    if os.path.normcase(out + os.sep).startswith(os.path.normcase(REPO + os.sep)):
        raise RunError("--out {} is inside the repository -- reports go outside it (default {})".format(out, REPORTS_DIR))
    if os.path.isdir(out) and os.listdir(out):
        raise RunError("--out {} is not empty".format(out))
    os.makedirs(os.path.join(out, "shots"), exist_ok=True)
    rep = Report(out, args.scenario, sc)
    try:
        return _run(args, sc, name, version, out, rep)
    except (RunError, dd.DriverError, MissingEnv) as e:
        rep.session_check("the run could start", False, str(e))
        return rep.failed()
    finally:
        try:
            rep.write()
        finally:
            rep.close()
        print("report: {}".format(os.path.join(out, "report.html")))


def _run(args, sc, name, version, out, rep):
    server = sc.get("server") or {}
    runner_cfg = sc.get("runner") or {}
    session_cfg = sc.get("session") or {}
    token_env = server.get("tokenEnv") or "RELIC_DEVTEST_TOKEN"
    env = child_env(token_env)
    steps = sc["steps"]
    has_gm = any(s.get("op") == "gm" for s in steps)
    launch = not args.no_launch
    rep.data["build"] = {"gitHead": git("rev-parse", "HEAD", env=env),
                         "gitDirty": bool(git("status", "--porcelain", "--untracked-files=no", env=env))}
    rep.data["files"]["run log"] = "run.log"
    rep.say("scenario {} ({} steps) for {}{}".format(name, len(steps), version, "" if launch else ", --no-launch"))

    # 1. preflight -------------------------------------------------------------------------------------------------
    unknown = [s["op"] for s in steps if s["op"] not in DLL_OPS + RUNNER_OPS]
    if unknown:
        rep.note("ops this script does not know -- sent to the DLL as they are: {}".format(", ".join(sorted(set(unknown)))))
    if args.dir:
        folder = os.path.abspath(args.dir)
        game = None
    else:
        game = dd.game_dir(version, args.state)
        folder = os.path.join(game, "ayy", "anime", "ee", dd.CHANNEL)
    moddir = os.path.dirname(folder)
    dll = os.path.abspath(args.dll) if args.dll else (default_dll(version) if launch else None)
    dll_sha = None
    if dll:
        if not os.path.isfile(dll):
            raise RunError("{} is not there -- build it: entertainment_experience\\tools\\build_ee.ps1 -Versions {} -Config DevTest".format(
                dll, version.replace(".", "")))
        if not carries_marker(dll):
            raise RunError("{} carries no DevTest marker: it is not a DevTest build".format(dll))
        dll_sha = sha256_file(dll)
        rep.data["dll"] = {"path": dll, "sha256": dll_sha}
        rep.say("DevTest DLL {} (sha256 {})".format(dll, dll_sha))
    expect_host = None
    if launch:
        if not server.get("expectHost"):
            raise RunError("the scenario names no server.expectHost")
        expect_host = expand_env(str(server["expectHost"]))
    agent = None
    if has_gm:
        if not server.get("agent"):
            raise RunError("the scenario sends GM commands but names no server.agent")
        agent = expand_env(str(server["agent"]))
        if not os.environ.get(token_env):
            raise MissingEnv(token_env)

    runner = os.path.abspath(args.runner) if args.runner else RUNNER_EXE
    window = args.window or runner_cfg.get("window") or "1600x900"
    window_size = parse_window(window) if launch else None
    own_files = args.own_files or runner_cfg.get("ownFiles") or "keep"
    play_args = None
    if launch:
        if args.build or not os.path.isfile(runner):
            if args.runner:
                raise RunError("--runner {} is not there".format(runner))
            build_runner(rep.say, env)
        play_args = ["--version", version, "--dll", dll, "--expect-host", expect_host, "--own-files", own_files, "--window", window,
                     "--log", os.path.join(out, "runner.log"), "--result", os.path.join(out, "runner-result.json")]
        if isinstance(sc.get("config"), dict):
            cfg = os.path.join(out, "cfg-devtest.block.json")
            write_atomic(cfg, json.dumps(sc["config"], indent=2).encode("utf-8"))
            play_args += ["--config", cfg]
        check_result = os.path.join(out, "runner-check.json")
        r = subprocess.run([runner, "check"] + [a if a != os.path.join(out, "runner-result.json") else check_result for a in play_args],
                           capture_output=True, text=True, timeout=300, env=env, creationflags=no_window())
        rep.data["files"]["runner check"] = "runner-check.json"
        if r.returncode != 0:
            problems = (dd.read_json(check_result) or {}).get("problems") or [(r.stdout + r.stderr)[-2000:]]
            for prob in problems:
                rep.note("runner check: " + str(prob))
            rep.session_check("runner preflight", False, "DevTestRunner check refused (exit {})".format(r.returncode))
            return rep.failed()
        rep.session_check("runner preflight", True)

    baseline = {"board": size_of(os.path.join(moddir, "relic-diag.txt")), "devtestLog": size_of(os.path.join(folder, "devtest.log")),
                "started": time.time()}
    installed = installed_launcher_root() if launch else None
    installed_before = payload_fingerprint(os.path.join(installed, "payload"), version) if installed else None
    repo_before = (git("status", "--porcelain", "--", "payload", env=env),
                   payload_fingerprint(os.path.join(REPO, "payload"), version)) if launch else None
    game_dll = os.path.join(moddir, "CLibrary.dll")

    os.makedirs(folder, exist_ok=True)
    hold = dd.hold_folder(folder)
    if hold is None:
        raise RunError("another driver is at work on {} -- one at a time".format(folder))
    proc = None
    sampler = None
    chan = dd.Channel(folder)
    slot = dd.profile_id(version, args.state) if game else None
    slot_log = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Relic", "profiles", slot, "LocalLow", "output_log.txt") if slot else None
    ctx = Context(chan, args.output_log or output_log_path(), slot_log, output_log_fixed=bool(args.output_log))
    channel = False      # a DevTest build was seen serving: whatever goes wrong, the game is closed through it
    gone = False         # ... unless it stopped answering
    try:
        try:
            # 2. the runner --------------------------------------------------------------------------------------
            if launch:
                proc = start_detached([runner, "play"] + play_args, REPO, env)
                rep.say("DevTestRunner play started (pid {}): {}".format(proc.pid, os.path.join(out, "runner.log")))
                rep.data["files"]["runner log"] = "runner.log"

            # 3. the DLL up --------------------------------------------------------------------------------------
            st, why, channel = wait_for_dll(chan, proc, dll_sha, args.launch_timeout, rep,
                                            os.path.join(out, "runner-result.json") if launch else None)
            if st is None:
                rep.session_check("the DevTest DLL is up", False, why)
                if channel:
                    rep.note("the steps are skipped; the game is closed through the channel all the same")
                elif proc is not None and proc.poll() is None:
                    rep.note("nothing here can close the game: close it by hand -- the runner then puts the profile, "
                             "Fiddler and the release DLL back")
                skip_steps(steps, rep, "the DevTest DLL is not up")
            else:
                rep.data["dll"]["statusSha256"] = st.get("dllSha256")
                rep.data["firstStatus"] = st
                rep.session_check("the DevTest DLL is up", True, "game version {}, scene {}, focused {}".format(
                    st.get("gameVersion"), st.get("scene"), st.get("focused")))
                sampler = Sampler(chan.status_path)
                sampler.start()
                # the window first: a client that went fullscreen anyway holds the owner's display
                if window_size is not None and window_check(chan, rep, out, window_size) is False:
                    rep.note("the game did not open in the window the runner asked for: the steps are skipped and the game is closed")
                    skip_steps(steps, rep, "the game is not in the window the runner asked for")
                else:
                    # 4. the steps ------------------------------------------------------------------------------
                    interrupted, gone = run_steps(steps, chan, ctx, rep, out, version, agent, token_env, sampler)
                    if interrupted:
                        rep.session_check("the steps ran to their end", False, "interrupted (Ctrl+C)")
                    if gone:
                        channel = False
                        rep.note("the game stopped answering: no quit can reach it")
        except KeyboardInterrupt:
            rep.session_check("the steps ran to their end", False, "interrupted (Ctrl+C)")
        except Exception as e:  # noqa: BLE001 -- a fault of this script: the game is still closed below
            rep.session_check("the steps ran to their end", False, "{}: {}".format(type(e).__name__, e))
        channel = channel or (not gone and serving(chan))    # an interrupt can come before the wait saw the DLL
        # 5. quit -----------------------------------------------------------------------------------------------
        if channel and not ctx.quit_sent and not args.keep_game:
            try:
                grab_output_log(ctx, out, rep)
                chan.call({"op": "quit"})
                ctx.quit_sent = True
                rep.say("quit sent")
            except dd.DriverError as e:
                rep.note("the quit at the end was not answered: {}".format(e))
            except KeyboardInterrupt:
                rep.note("interrupted before the quit was answered: close the game by hand -- the runner then puts "
                         "everything back")
    finally:
        if sampler is not None:
            sampler.stop()
            rep.data["sessionRates"] = rates(sampler.samples)
        if chan.discard_pending():      # one of this run's commands that was cut off before it was taken back
            rep.note("a command left in cmd.json was removed")
        dd.release_folder(folder, hold)

    # 6. the end of the session and the report -------------------------------------------------------------------
    if proc is not None:
        if args.keep_game:
            rep.note("--keep-game: the game stays; the runner (pid {}) puts everything back once it is closed".format(proc.pid))
        else:
            rep.say("waiting for the runner to end (the game closing, the profile and Fiddler, the release DLL)")
            code = None
            try:
                code = proc.wait(timeout=args.exit_timeout)
            except subprocess.TimeoutExpired:
                rep.session_check("the runner ended within {:.0f} s".format(args.exit_timeout), False,
                                  "it is still at work: the game may still be open, the session's profile loaded and Fiddler "
                                  "running -- close the game (the runner then puts everything back), then see runner.log")
            except KeyboardInterrupt:
                rep.session_check("the runner ended", False, "the wait for it was interrupted (Ctrl+C) -- it goes on by "
                                  "itself and puts everything back once the game is closed")
            result = dd.read_json(os.path.join(out, "runner-result.json")) or {}
            rep.data["runner"] = {"exit": code, "result": result}
            rep.data["files"]["runner result"] = "runner-result.json"
            if code is not None:
                rep.session_check("the runner ended cleanly", code == 0, "exit {}{}".format(code, runner_exit_text(code)))
                release = result.get("releaseDll") or {}
                if release.get("shipped") is False:
                    # the payload ships no enhancements DLL for this version: its release state is no DevTest DLL there
                    gone = not (os.path.isfile(game_dll) and carries_marker(game_dll))
                    rep.session_check("the DevTest DLL is out of the game folder (the payload ships none for this version)",
                                      bool(release.get("restored")) and gone, game_dll)
                else:
                    ours_back = os.path.isfile(game_dll) and not carries_marker(game_dll) and (dll_sha is None or sha256_file(game_dll) != dll_sha)
                    rep.session_check("the release DLL is back in the game folder", bool(release.get("restored")) and ours_back,
                                      "{} (the runner: {})".format(game_dll, release.get("sha256")))
                left_after_session(result, rep)
            if installed_before is not None:
                after = payload_fingerprint(os.path.join(installed, "payload"), version)
                rep.session_check("the installed launcher's payload is unchanged", after == installed_before, json.dumps(after))
            repo_after = (git("status", "--porcelain", "--", "payload", env=env), payload_fingerprint(os.path.join(REPO, "payload"), version))
            rep.session_check("the repository's payload is unchanged", repo_after == repo_before,
                              "git status -- payload: {!r}".format(repo_after[0]))

    board_text = read_from(os.path.join(moddir, "relic-diag.txt"), baseline["board"]).decode("ascii", "replace")
    if board_text:
        write_atomic(os.path.join(out, "board.txt"), board_text.encode("ascii", "replace"))
        rep.data["files"]["diag board since the start"] = "board.txt"
    board = board_summary(parse_board(board_text))
    rep.data["board"] = board
    if session_cfg.get("noFaults", True) and board.get("lines"):
        rep.session_check("no fault on the diag board", board.get("faults", 0) == 0,
                          "faults {}, last fault: {}".format(board.get("faults"), board.get("lastFault")))
    stall_limit = session_cfg.get("maxPresentStall", 2.0)
    if stall_limit is not None and rep.data.get("sessionRates", {}).get("inGameSamples"):
        stall = rep.data["sessionRates"]["maxPresentStall"]
        rep.session_check("presentVt never stalled more than {} s in the world".format(stall_limit), stall <= float(stall_limit),
                          "longest stall {} s".format(stall))
    dlog = read_from(os.path.join(folder, "devtest.log"), baseline["devtestLog"])
    if dlog:
        write_atomic(os.path.join(out, "devtest.log"), dlog)
        rep.data["files"]["devtest.log"] = "devtest.log"
    copy_mod_logs(moddir, baseline["started"], out, rep)
    if "output log" not in rep.data["files"]:
        grab_output_log(ctx, out, rep)
    rep.data["files"]["report.json"] = "report.json"
    failed = rep.failed()
    rep.say("{}: {} failed check{}".format(name, failed, "" if failed == 1 else "s"))
    return failed


def copy_mod_logs(moddir, since, out, rep, limit=16 * 1024 * 1024):
    """The mod's own log files (logs/log_<date>_<time>.txt, written when File Logging is on) that the run wrote to:
    copied into mod-logs/, each at most its last `limit` bytes."""
    logs = os.path.join(moddir, "logs")
    try:
        names = sorted(n for n in os.listdir(logs) if n.lower().endswith(".txt"))
    except OSError:
        return
    copied = 0
    for n in names:
        p = os.path.join(logs, n)
        try:
            if os.path.getmtime(p) < since - 1:
                continue
        except OSError:
            continue
        data = tail_of(p, limit)
        if data:
            os.makedirs(os.path.join(out, "mod-logs"), exist_ok=True)
            write_atomic(os.path.join(out, "mod-logs", n), data)
            copied += 1
    if copied:
        rep.data["files"]["the mod's own logs"] = "mod-logs/"


def runner_exit_text(code):
    return {0: "", 1: " (the session failed)", 2: " (refused -- nothing was changed)", 3: " (the release DLL is not back: DevTestRunner restore)",
            4: " (the screen values could not be put back)",
            5: " (the session's profile is still loaded: Relic's recovery puts the live profile back -- then DevTestRunner restore-window)",
            6: " (Fiddler is still running after the session)"}.get(code, "")


def grab_output_log(ctx, out, rep):
    """The tail of the game's own log (read, never written). The live LocalLow holds the session's log only while the
    session's profile is loaded -- while the game still answers; once Relic has put the live profile back it is the
    official client's, so the slot's own copy is read then (Relic saves the slot when the session ends)."""
    path, where = ctx.output_log, "the live LocalLow"
    if not ctx.output_log_fixed and (ctx.chan is None or ctx.chan.status() is None):
        path, where = ctx.slot_output_log, "the profile slot"
    if not path or not os.path.isfile(path):
        return
    data = tail_of(path)
    if data:
        write_atomic(os.path.join(out, "output_log.tail.txt"), data)
        rep.data["files"]["output log"] = "output_log.tail.txt"
        rep.data["outputLogFrom"] = where


def wait_for_dll(chan, proc, dll_sha, timeout, rep, runner_result=None):
    """A fresh status that carries the marker -- and, when the DLL was staged, names its sha256 (the DLL hashes
    itself once it runs, so the first statuses may not name it yet). The runner says when the game is up and whether
    launcher.exe injected the DLLs: a game up without them has no channel to wait for.
    -> (the status or None, why there is none, whether the channel answers: a DevTest build was seen serving, so a
    quit reaches the game whatever else is wrong with it)."""
    deadline = time.time() + timeout
    said = None
    marked_at = None
    while time.time() < deadline:
        if proc is not None and proc.poll() is not None:
            return None, "the runner ended (exit {}{}) before the DLL answered".format(proc.returncode, runner_exit_text(proc.returncode)), False
        early = dd.read_json(runner_result) if runner_result else None
        if early and early.get("phase") == "gameUp" and early.get("enhancementsActive") is False:
            return None, ("the game started without the enhancements (launcher.exe was not used, or it fell back to the plain "
                          "start): there is no DevTest channel -- see runner.log"), False
        st = chan.status()
        if st is not None and st.get("marker") == dd.MARKER:
            got = str(st.get("dllSha256") or "")
            if dll_sha is None or got.lower() == dll_sha.lower():
                return st, None, True
            marked_at = marked_at or time.time()
            if got and time.time() - marked_at > 5:
                return None, "the running DLL names sha256 {}, the staged one is {}".format(got, dll_sha), True
            if time.time() - marked_at > 60:
                return None, "the running DLL named no sha256 within 60 s", True
        elif st is not None and said != "nomarker":
            said = "nomarker"
            rep.note("status.json is fresh but names no DevTest build -- waiting")
        time.sleep(0.5)
    return None, "no fresh status with the marker{} within {:.0f} s".format(" and the staged sha256" if dll_sha else "", timeout), \
        marked_at is not None


def serving(chan):
    """Is a DevTest build serving the channel right now (a fresh status with the marker)?"""
    st = chan.status()
    return st is not None and st.get("marker") == dd.MARKER


def left_after_session(result, rep):
    """What the runner found once the session was over (its result file): the live profile back, Fiddler stopped.
    A session that ended deferred -- a client still running when its settle window ended -- leaves both to Relic's
    recovery."""
    live = result.get("liveProfileBack")
    if isinstance(live, bool):
        rep.session_check("the live profile is back", live, None if live else
                          "the session's profile is still loaded ({}) -- Relic's recovery puts the live profile back once no "
                          "client runs: the installed Relic does (its tray instance, or its next start), or DevTestRunner "
                          "--tray".format(result.get("activeProfile") or "a private slot"))
    elif "liveProfileBack" in result:
        rep.note("the runner could not tell whether the live profile is back: {}".format(result.get("afterSession") or "unknown"))
    fiddler = result.get("fiddlerRunning")
    if isinstance(fiddler, bool):
        rep.session_check("Fiddler is stopped", not fiddler, None if not fiddler else
                          "Fiddler is still running, the system proxy while it runs -- " + (
                              "Relic's recovery stops it with the profile restore" if live is False
                              else "the session did not stop it: close it"))


def parse_window(text):
    """'WxH' -> (W, H); None for 'keep' or anything else."""
    m = re.fullmatch(r"\s*(\d+)[xX](\d+)\s*", str(text or ""))
    return (int(m.group(1)), int(m.group(2))) if m else None


def window_check(chan, rep, out, size):
    """Did the window the runner wrote into the slot's screen values take? What the game reports of its screen
    (status.screen: fullScreen, width, height) -- or, from a build that does not report it, the back buffer a shot
    reads (a client that went fullscreen anyway draws one of the monitor's size). -> True, False, or None when it
    cannot be told."""
    w, h = size
    name = "the game runs in a window of at most {}x{}".format(w, h)
    st = chan.status() or chan.last_status or {}
    screen = st.get("screen") if isinstance(st.get("screen"), dict) else {}
    full, sw, sh = screen.get("fullScreen"), screen.get("width"), screen.get("height")
    if isinstance(full, bool):
        sized = is_number(sw) and is_number(sh)
        ok = not full and (not sized or (sw <= w and sh <= h))
        rep.data["screen"] = screen
        rep.session_check(name, ok, "the game reports fullScreen {}{}".format(
            str(full).lower(), ", {}x{}".format(sw, sh) if sized else ""))
        return ok
    try:
        ans = chan.call({"op": "shot", "overlay": False, "afterFrames": 2, "out": "window-check", "timeout": 20})
    except dd.DriverError as e:
        rep.note("the window could not be checked: {}".format(e))
        return None
    bw, bh = ans.get("width"), ans.get("height")
    if not ans.get("ok") or ans.get("outcome") != "saved" or not (is_number(bw) and is_number(bh)):
        rep.note("the window could not be checked: the shot answered {}".format(ans.get("error") or ans.get("outcome")))
        return None
    png, _, _ = chan.collect_shot(ans, os.path.join(out, "shots"), name="00-window-check")
    ok = bw <= w and bh <= h
    rep.data["screen"] = {"backBuffer": [bw, bh]}
    rep.session_check(name, ok, "the back buffer is {}x{}{}".format(bw, bh, "" if png is None else " ({})".format(
        os.path.relpath(png, out).replace("\\", "/"))))
    return ok


def skip_steps(steps, rep, why):
    """Every step recorded as skipped, none run."""
    for i, step in enumerate(steps):
        rep.data["steps"].append({"index": i + 1, "label": str(step.get("label") or step["op"]), "op": step["op"],
                                  "verdict": "skipped", "checks": [], "ms": 0, "error": why})


def run_steps(steps, chan, ctx, rep, out, version, agent, token_env, sampler):
    """The steps in order. A step that fails stops the steps after it (onFail: stop, the default) but for those marked
    "always": true, which run whatever happened before them -- the cleanup a scenario owes the server (a GM switch
    put back). A Ctrl+C stops the steps the same way; a second one leaves at once. Once the game has stopped
    answering, no step that needs it is tried. A step that faults in this script fails alone. -> (interrupted, the
    game gone)."""
    stopped = interrupted = gone = False
    for i, step in enumerate(steps):
        op = step["op"]
        label = str(step.get("label") or op)
        always = step.get("always") is True
        rec = {"index": i + 1, "label": label, "op": op, "verdict": None, "checks": [], "ms": 0}
        if always:
            rec["always"] = True
        rep.data["steps"].append(rec)
        if stopped and not always:
            rec["verdict"] = "skipped"
            continue
        if gone and op not in RUNNER_OPS:
            rec["verdict"] = "skipped"
            rec["error"] = "the game stopped answering"
            continue
        rep.say("step {}: {}{}".format(i + 1, label, " (always)" if stopped else ""))
        t0 = time.time()
        answer = None
        try:
            request, answer, shots = execute_step(i, step, label, chan, ctx, rep, out, version, agent, token_env, sampler)
            rec["request"] = redact(request)
            rec["answer"] = redact(answer)
            if shots:
                rec["shots"] = shots
            expect = ctx.resolve(step.get("expect") or {})
            rec["checks"] = implicit_checks(expect, answer) + check_expect(expect, answer)
            rec["verdict"] = "pass" if all(c["ok"] for c in rec["checks"]) else "fail"
        except KeyboardInterrupt:
            rec["verdict"] = "error"
            rec["error"] = "interrupted (Ctrl+C)"
            if interrupted:
                raise
            interrupted = True
        except dd.GameGone as e:
            gone = True
            rec["verdict"] = "error"
            rec["error"] = str(e)
        except (dd.DriverError, StepError, MissingEnv, OSError, ValueError) as e:
            rec["verdict"] = "error"
            rec["error"] = str(e)
        except Exception as e:  # noqa: BLE001 -- a fault of this script or a matcher the scenario misuses: this step fails
            rec["verdict"] = "error"
            rec["error"] = "{}: {}".format(type(e).__name__, e)
        rec["ms"] = int((time.time() - t0) * 1000)
        if answer is not None and "label" in step:
            ctx.answers[label] = answer
        for c in rec["checks"]:
            if not c["ok"]:
                rep.say("  FAIL {}: expected {} -- {}".format(c["path"], short(c["expected"]), c.get("why")))
        rep.say("  {} ({} ms){}".format(rec["verdict"], rec["ms"], " -- " + rec["error"] if rec.get("error") else ""))
        if not stopped and interrupted:
            stopped = True
            rep.note("interrupted at step {} ({}) -- the steps after it are skipped but for those marked always".format(i + 1, label))
        elif not stopped and rec["verdict"] in ("fail", "error") and step.get("onFail", "stop") != "continue":
            stopped = True
            rep.note("step {} ({}) failed -- the steps after it are skipped (onFail: stop) but for those marked always".format(i + 1, label))
        rep.write()
    return interrupted, gone


def execute_step(i, step, label, chan, ctx, rep, out, version, agent, token_env, sampler):
    op = step["op"]
    fields = {k: v for k, v in step.items() if k not in STEP_ONLY}
    if op == "sleep":
        secs = float(ctx.resolve(step.get("seconds", 1)))
        sleep_for(secs)
        return fields, {"ok": True, "slept": secs}, None
    if op == "measure":
        secs = float(ctx.resolve(step.get("seconds", 60)))
        t0 = time.time()
        sleep_for(secs)
        result = rates(sampler.window(t0)) if sampler is not None else {"samples": 0}
        result["ok"] = True
        result["seconds"] = secs
        rep.data["measurements"].append({"label": label, "result": result})
        return fields, result, None
    if op == "gm":
        msg = ctx.resolve(step.get("msg"))
        if not isinstance(msg, str) or not msg.strip():
            raise StepError("a gm step needs msg")
        uid = ctx.resolve(step["uid"]) if "uid" in step else ctx.status().get("uid")
        if not is_number(uid) or uid <= 0:
            raise StepError("no uid to send the command for: the status names none -- is the player in the world?")
        token = os.environ.get(token_env)
        if not token:
            raise MissingEnv(token_env)
        ver = str(ctx.resolve(step.get("version", version)))
        answer = agent_command(agent, token, ver, int(uid), msg, timeout=float(step.get("timeout", 20)))
        return {"op": "gm", "version": ver, "uid": int(uid), "msg": msg}, answer, None
    cmd = ctx.resolve(fields)
    if op == "quit":
        grab_output_log(ctx, out, rep)
    answer = chan.call(cmd, timeout=step.get("answerTimeout"))
    shots = None
    if op == "quit" and answer.get("ok"):
        ctx.quit_sent = True
    if op == "shot" and answer.get("ok"):
        stem = "{:02d}-{}".format(i + 1, safe_name(cmd.get("out") or label))
        png, side, meta = chan.collect_shot(answer, os.path.join(out, "shots"), name=stem)
        if png:
            answer["sidecarData"] = meta
            shots = [os.path.relpath(png, out)]
        else:
            answer["collected"] = False
        # the render thread's share of a shot: the copy of the back buffer and the copy of its rows
        if is_number(answer.get("copyUs")) and is_number(answer.get("memcpyUs")):
            answer["copyPlusMemcpyMs"] = round((answer["copyUs"] + answer["memcpyUs"]) / 1000.0, 3)
    return cmd, answer, shots


def sleep_for(seconds):
    end = time.time() + seconds
    while time.time() < end:
        time.sleep(min(0.25, max(0.0, end - time.time())))


# -- the self-test -----------------------------------------------------------------------------------------------------------

# A stand-in for DevTestRunner (run through a .cmd beside it): `check` passes; `play` writes the result file the way the
# runner does, waits until the stand-in for the game stops writing its status (it quit), lingers as long as its
# config says, puts a "release" CLibrary.dll in place and ends with the exit code and the facts its config names.
FAKE_RUNNER = r'''
import json, os, sys, time
here = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(here, "fake_runner.json"), encoding="utf-8") as f:
    cfg = json.load(f)
args = sys.argv[1:]
opts = {args[i][2:]: args[i + 1] for i in range(1, len(args) - 1) if args[i].startswith("--")}


def write(path, obj):
    with open(path + ".tmp", "w", encoding="utf-8") as f:
        json.dump(obj, f)
    os.replace(path + ".tmp", path)


if args[0] == "check":
    sys.exit(0)
seen = sorted(k for k in os.environ if k.upper().startswith("RELIC_DEVTEST_") or k.upper() == cfg["tokenEnv"].upper())
result = {"command": "play", "phase": "gameUp", "enhancementsActive": True, "envSeen": seen}
write(opts["result"], result)
deadline = time.time() + 60
while time.time() < deadline:
    try:
        if time.time() - os.stat(cfg["status"]).st_mtime > 1.0:
            break
    except OSError:
        pass
    time.sleep(0.1)
time.sleep(cfg.get("linger", 0))
if cfg.get("unshipped"):    # a version the payload ships no enhancements DLL for: the runner takes the DevTest DLL out
    if cfg.get("leaveDev"):
        with open(cfg["gameDll"], "wb") as f:
            f.write(b"MZ a DevTest build " + cfg["marker"].encode("ascii"))
    elif os.path.exists(cfg["gameDll"]):
        os.remove(cfg["gameDll"])
    release = {"restored": not cfg.get("leaveDev"), "shipped": False, "removed": not cfg.get("leaveDev")}
else:
    with open(cfg["gameDll"], "wb") as f:
        f.write(b"MZ the release build")
    release = {"restored": True, "sha256": "ef" * 32}
result.update({"sessionOk": True, "releaseDll": release, "liveProfileBack": cfg.get("live", True),
               "activeProfile": "live" if cfg.get("live", True) else "priv28", "fiddlerRunning": cfg.get("fiddler", False),
               "exit": cfg.get("exit", 0)})
write(opts["result"], result)
open(os.path.join(here, "ended-" + cfg["case"]), "w").close()
sys.exit(cfg.get("exit", 0))
'''


class _Agent(http.server.BaseHTTPRequestHandler):
    token = None
    seen = []

    def do_POST(self):  # noqa: N802 -- the http.server name
        body = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        if self.path != "/command":
            return self._send(404, {"error": "not found"})
        if self.headers.get("Authorization") != "Bearer " + str(_Agent.token):
            return self._send(401, {"error": "unauthorized"})
        req = json.loads(body)
        _Agent.seen.append(req)
        return self._send(200, {"ok": True, "response": "fake muip: {}".format(req.get("msg"))})

    def _send(self, code, obj):
        data = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *_):
        pass


def selftest():
    check = dd.Checks()
    root = tempfile.mkdtemp(prefix="relic-devtest-run-")
    print("devtest_run selftest in {}".format(root))
    saved_env = {k: os.environ.get(k) for k in ("RELIC_DEVTEST_AGENT", "RELIC_DEVTEST_TOKEN", "RELIC_DEVTEST_X", "DEVTEST_SELFTEST_KEY")}
    saved_globals = {k: globals()[k] for k in ("installed_launcher_root", "sleep_for")}
    server = None
    try:
        # matchers
        ans = {"ok": True, "scene": 3, "pos": [1.0, 2.0, 3.0], "camera": {"held": False}, "ops": ["ping", "shot"],
               "entities": [{"configId": 21010101, "level": 90, "kind": "monster"}, {"configId": 7, "level": 1}], "name": "LoginMainPage"}
        cases = [({"scene": 3}, True), ({"scene": 3.0}, True), ({"scene": "3"}, False), ({"camera.held": False}, True),
                 ({"camera.held": 0}, False), ({"scene": {"in": [3, 9]}}, True), ({"scene": {"min": 4}}, False),
                 ({"pos.2": {"min": 2.5, "max": 3.5}}, True), ({"name": {"regex": "^Login"}}, True), ({"nope": {"exists": False}}, True),
                 ({"nope": {"exists": True}}, False), ({"ops": {"contains": ["shot"]}}, True), ({"ops": {"contains": ["fly"]}}, False),
                 ({"entities": {"contains": [{"configId": 21010101, "level": {"min": 90}}]}}, True),
                 ({"entities": {"contains": [{"configId": 21010101, "level": {"min": 91}}]}}, False), ({"entities": {"len": {"min": 2}}}, True),
                 ({"pos": [1, 2, 3]}, True), ({"pos": [1, 2]}, False), ({"camera": {"held": False}}, True), ({"camera": {"eq": {}}}, False),
                 ({"scene": {"not": {"in": [0]}}}, True), ({"nope": 1}, False)]
        bad = [c for c in cases if all(x["ok"] for x in check_expect(c[0], ans)) != c[1]]
        check("the expect matchers hold to their rules ({} cases)".format(len(cases)), not bad, bad)
        imp = implicit_checks({}, {"ok": True, "outcome": "timeout"})
        check("a timeout fails a step unless its expect names the outcome", any(not c["ok"] for c in imp)
              and all(c["ok"] for c in implicit_checks({"outcome": "timeout"}, {"ok": True, "outcome": "timeout"})))
        check("an answer that is not ok fails a step", not implicit_checks({}, {"ok": False, "error": "x"})[0]["ok"])
        check("a password or token never reaches the report", redact({"password": "p", "a": [{"token": "t"}]}) == {"password": "***", "a": [{"token": "***"}]})

        # what this script starts: the build it waits for, the environment it hands over
        idle = [("MSBuild.exe", 1, r'"C:\VS\MSBuild\Current\Bin\amd64\MSBuild.exe" /nologo /nodemode:1 /nodeReuse:true'),
                ("dotnet.exe", 2, r'"C:\Program Files\dotnet\dotnet.exe" "C:\Program Files\dotnet\sdk\10.0.100\MSBuild.dll" /nodemode:1'),
                ("dotnet.exe", 3, r'"C:\Program Files\dotnet\dotnet.exe" DevTestRunner.dll selftest'),
                ("dotnet.exe", 7, r'"C:\Program Files\dotnet\dotnet.exe" run --project spikes/Relic.Spikes -- all')]
        check("reusable MSBuild nodes and a program dotnet runs are no build", build_in(idle) is None, build_in(idle))
        check("a dotnet build, an MSBuild.exe of a build and a C++ compiler are",
              build_in(idle + [("dotnet.exe", 4, r'"C:\Program Files\dotnet\dotnet.exe" build x.csproj -c Release')]) == "dotnet build (pid 4)"
              and build_in([("MSBuild.exe", 5, "MSBuild.exe relic-ee.sln -p:Configuration=DevTest")]) == "MSBuild.exe (pid 5)"
              and build_in([("cl.exe", 6, "cl.exe /c x.cpp")]) == "cl.exe (pid 6)")
        os.environ["DEVTEST_SELFTEST_KEY"] = "k"
        os.environ["RELIC_DEVTEST_X"] = "x"
        scrubbed = child_env("DEVTEST_SELFTEST_KEY")
        check("a program started here gets no RELIC_DEVTEST_* variable and not the token's own",
              not any(k.upper().startswith(SECRET_PREFIX) or k.upper() == "DEVTEST_SELFTEST_KEY" for k in scrubbed)
              and scrubbed.get("PATH") == os.environ.get("PATH"))

        # values
        os.environ["RELIC_DEVTEST_X"] = "game.example.com"
        check("${NAME} is an environment variable", expand_env("${RELIC_DEVTEST_X}:21000") == "game.example.com:21000")
        try:
            expand_env("${RELIC_DEVTEST_UNSET_NAME}")
            check("an unset variable is named", False)
        except MissingEnv as e:
            check("an unset variable is named", e.name == "RELIC_DEVTEST_UNSET_NAME")

        class _Chan:
            last_status = {"pos": [10.0, 20.0, 30.0], "uid": 5}

            def status(self):
                return self.last_status
        ctx = Context(_Chan())
        ctx.answers["start"] = {"pos": [1.0, 2.0, 3.0]}
        got = ctx.resolve({"pos": {"$offset": [5, 0, -5]}, "back": {"$answer": "start.pos"}, "uid": {"$status": "uid"},
                           "from": {"$offset": [1, 1, 1], "from": "start.pos"}, "host": "${RELIC_DEVTEST_X}"})
        check("values resolve against the status and earlier answers", got == {"pos": [15.0, 20.0, 25.0], "back": [1.0, 2.0, 3.0], "uid": 5,
                                                                              "from": [2.0, 3.0, 4.0], "host": "game.example.com"}, got)

        # scenarios
        inc = os.path.join(root, "inc.json")
        with open(inc, "w", encoding="utf-8") as f:
            json.dump({"steps": [{"op": "mark", "text": "included"}]}, f)
        top = os.path.join(root, "top.json")
        with open(top, "w", encoding="utf-8") as f:
            json.dump({"name": "t", "version": "2.8", "steps": [{"op": "ping"}, {"include": "inc.json"}, {"op": "quit"}]}, f)
        check("an include splices its steps in place", [s["op"] for s in load_scenario(top)["steps"]] == ["ping", "mark", "quit"])
        shipped = sorted(f for f in os.listdir(SCENARIOS_DIR) if f.endswith(".json")) if os.path.isdir(SCENARIOS_DIR) else []
        check("the shipped scenarios are there", len(shipped) >= 2, shipped)
        for fname in shipped:
            path = os.path.join(SCENARIOS_DIR, fname)
            with open(path, "rb") as f:
                raw = f.read()
            try:
                sc = load_scenario(path)
                ops_ok = all(s["op"] in DLL_OPS + RUNNER_OPS for s in sc["steps"])
                env_only = all(str(v).startswith("${") for k, v in (sc.get("server") or {}).items() if k in ("expectHost", "agent"))
                check("scenario {}: loads, knows its ops, names its server only through the environment".format(fname),
                      ops_ok and env_only and dd.MARKER.encode("ascii") not in raw and not re.search(rb"\b(?:\d{1,3}\.){3}\d{1,3}\b", raw))
            except RunError as e:
                check("scenario {} loads".format(fname), False, e)

        # the diag board and the rates
        board = "\r\n".join(["[   0] present=1 presentVt=0 handlerRun=8 faults=0 | fault: none | x",
                             "[   1] present=121 presentVt=120 handlerRun=5008 faults=0 | fault: none | x",
                             "[   2] present=241 presentVt=240 handlerRun=10008 faults=0 | fault: none | x",
                             "[   3] present=241 presentVt=240 handlerRun=15008 faults=1 | fault: 0xC0000005 in x | x",
                             "[   4] present=361 presentVt=360 handlerRun=20008 faults=1 | fault: 0xC0000005 in x | x"])
        b = board_summary(parse_board(board))
        check("the board names its faults, rates and the longest stall", b["faults"] == 1 and b["presentVtPerSec"] == 45.0
              and b["longestPresentVtStallSeconds"] == 2 and b["processes"] == 1, b)
        samples = [{"t": i, "wallMs": i * 500, "focused": i >= 4, "inGame": True, "present": i * 30, "presentVt": i * 30 if i != 7 else 180,
                    "handlerRun": i * 1000} for i in range(12)]
        samples[8]["presentVt"] = 180
        r = rates(samples)
        check("rates are split by focus", r["unfocused"]["presentVtPerSec"] == 60.0 and r["focused"]["seconds"] == 3.5, r)
        check("the longest presentVt stall in the world is measured", r["maxPresentStall"] == 1.0, r["maxPresentStall"])

        # a whole run against the stand-in, with a stand-in agent
        token = "selftest-token-" + os.urandom(6).hex()
        _Agent.token = token
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), _Agent)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        os.environ["RELIC_DEVTEST_AGENT"] = "http://127.0.0.1:{}".format(server.server_address[1])
        os.environ["RELIC_DEVTEST_TOKEN"] = token
        ee = os.path.join(root, "game", "ayy", "anime", "ee")
        folder = os.path.join(ee, dd.CHANNEL)
        os.makedirs(folder)
        board_file = os.path.join(ee, "relic-diag.txt")
        with open(board_file, "w", encoding="ascii", newline="") as f:
            f.write("[  99] present=5 presentVt=5 handlerRun=5 faults=3 | fault: old | an earlier session\r\n")
        outlog = os.path.join(root, "output_log.txt")
        with open(outlog, "w", encoding="utf-8") as f:
            f.write("Initialize engine version: 2017.4.30f1\n")
        scen = os.path.join(root, "smoke.json")
        with open(scen, "w", encoding="utf-8") as f:
            json.dump({"name": "selftest-smoke", "version": "2.8",
                       "server": {"expectHost": "${RELIC_DEVTEST_X}", "agent": "${RELIC_DEVTEST_AGENT}", "tokenEnv": "RELIC_DEVTEST_TOKEN"},
                       "session": {"noFaults": True, "maxPresentStall": 2.0},
                       "steps": [{"label": "ping", "op": "ping", "expect": {"ops": {"contains": ["shot", "ui.tree"]}}},
                                 {"label": "login screen", "op": "shot", "overlay": False, "out": "login",
                                  "expect": {"outcome": "saved", "width": {"min": 1}, "format": {"in": [27, 28, 29, 87, 88, 90, 91, 92, 93]},
                                             "copyPlusMemcpyMs": {"max": 10}, "sidecarData.formatName": "R8G8B8A8_UNORM",
                                             "sidecar": {"regex": "^shots/"}}},
                                 {"op": "enter"},
                                 {"op": "wait", "until": {"inGame": True}, "timeout": 5},
                                 {"label": "start", "op": "status", "expect": {"uid": {"min": 1}, "scene": 3}},
                                 {"op": "gm", "msg": "wudi global avatar on"},
                                 {"op": "teleport", "pos": {"$offset": [5, 0, 0]}, "mode": "direct"},
                                 {"op": "teleport", "pos": {"$answer": "start.pos"}, "mode": "direct"},
                                 {"op": "entities", "radius": 50, "expect": {"entities": {"contains": [{"configId": 21010101}]}}},
                                 {"op": "login", "account": "tester", "password": "${RELIC_DEVTEST_TOKEN}"},
                                 {"label": "background", "op": "measure", "seconds": 1.2, "expect": {"samples": {"min": 1}}},
                                 {"op": "mark", "text": "end"},
                                 {"op": "quit"}]}, f)
        fake = dd.FakeMod(folder, board=board_file)
        fake.start()
        out1 = os.path.join(root, "report-ok")
        code = main([scen, "--no-launch", "--dir", folder, "--out", out1, "--output-log", outlog])
        fake.stop()
        rep = dd.read_json(os.path.join(out1, "report.json")) or {}
        verdicts = [s.get("verdict") for s in rep.get("steps", [])]
        check("a whole run against the stand-in passes", code == 0 and verdicts == ["pass"] * 13, (code, verdicts))
        check("the GM command reached the agent with the status uid", _Agent.seen and _Agent.seen[-1] == {"version": "2.8", "uid": 10001, "msg": "wudi global avatar on"},
              _Agent.seen)
        check("the shot was collected into the report", os.path.isfile(os.path.join(out1, "shots", "02-login.png")))
        check("the board since the start leaves the earlier session out", rep.get("board", {}).get("faults") == 0 and "an earlier session" not in
              open(os.path.join(out1, "board.txt"), encoding="ascii").read(), rep.get("board"))
        check("the output log tail is in the report", os.path.isfile(os.path.join(out1, "output_log.tail.txt")))
        check("the mod's own log of the run is in the report", os.path.isfile(os.path.join(out1, "mod-logs", "log_fake.txt")))
        check("the report has its page", os.path.isfile(os.path.join(out1, "report.html"))
              and "selftest-smoke" in open(os.path.join(out1, "report.html"), encoding="utf-8").read())
        leaked = []
        for dirpath, _, files in os.walk(out1):
            for fn in files:
                with open(os.path.join(dirpath, fn), "rb") as f:
                    if token.encode("ascii") in f.read():
                        leaked.append(fn)
        check("the agent token is written nowhere in the report", not leaked, leaked)

        fake = dd.FakeMod(os.path.join(root, "game2", "ayy", "anime", "ee", dd.CHANNEL))
        os.makedirs(fake.folder)
        fake.start()
        scen2 = os.path.join(root, "fail.json")
        with open(scen2, "w", encoding="utf-8") as f:
            json.dump({"name": "selftest-fail", "version": "2.8", "steps": [
                {"op": "status", "expect": {"scene": 99}, "onFail": "continue"},
                {"op": "wait", "until": {"inGame": True}, "timeout": 0.3},
                {"op": "ping"}]}, f)
        out2 = os.path.join(root, "report-fail")
        code = main([scen2, "--no-launch", "--dir", fake.folder, "--out", out2, "--output-log", outlog])
        fake.stop()
        rep2 = dd.read_json(os.path.join(out2, "report.json")) or {}
        check("failed checks are the exit code, and a failure stops the steps after it",
              code == 2 and [s.get("verdict") for s in rep2.get("steps", [])] == ["fail", "fail", "skipped"],
              (code, [s.get("verdict") for s in rep2.get("steps", [])]))
        check.raises("a report inside the repository is refused", RunError,
                     lambda: run(argparse.Namespace(scenario=scen2, out=os.path.join(REPO, "x-report"), no_launch=True)))

        # A failed step and a Ctrl+C both stop the steps, but a step marked always still runs, and the game is closed.
        def no_launch_case(case, steps, fake_kwargs=None):
            fk = dd.FakeMod(os.path.join(root, case, "ayy", "anime", "ee", dd.CHANNEL), in_game=True, **(fake_kwargs or {}))
            os.makedirs(fk.folder)
            fk.start()
            sc_path = os.path.join(root, case + ".json")
            with open(sc_path, "w", encoding="utf-8") as f:
                json.dump({"name": "selftest-" + case, "version": "2.8", "server": {"agent": "${RELIC_DEVTEST_AGENT}"},
                           "session": {"noFaults": True}, "steps": steps}, f)
            n_seen = len(_Agent.seen)
            rc = main([sc_path, "--no-launch", "--dir", fk.folder, "--out", os.path.join(root, "report-" + case), "--output-log", outlog])
            fk.stop()
            return rc, dd.read_json(os.path.join(root, "report-" + case, "report.json")) or {}, fk, _Agent.seen[n_seen:]

        code, rep3, fk, seen = no_launch_case("always", [
            {"op": "status", "expect": {"scene": 99}}, {"op": "mark", "text": "never"},
            {"label": "put back", "op": "gm", "msg": "wudi global avatar off", "always": True}])
        check("a step marked always runs after a failure stopped the steps, and the game is closed",
              [s.get("verdict") for s in rep3.get("steps", [])] == ["fail", "skipped", "pass"] and code == 1
              and [s.get("msg") for s in seen] == ["wudi global avatar off"] and [c.get("op") for c in fk.served] == ["status", "quit"],
              (code, [s.get("verdict") for s in rep3.get("steps", [])], seen, [c.get("op") for c in fk.served]))

        def interrupt(_seconds):
            raise KeyboardInterrupt
        globals()["sleep_for"] = interrupt
        try:
            code, rep4, fk, seen = no_launch_case("interrupt", [
                {"op": "sleep", "seconds": 30}, {"op": "mark", "text": "never"},
                {"label": "put back", "op": "gm", "msg": "wudi global avatar off", "always": True}])
        finally:
            globals()["sleep_for"] = saved_globals["sleep_for"]
        failed_names = [s["name"] for s in rep4.get("session", []) if not s["ok"]]
        check("a Ctrl+C fails the run, runs the steps marked always and closes the game",
              code == 2 and [s.get("verdict") for s in rep4.get("steps", [])] == ["error", "skipped", "pass"]
              and failed_names == ["the steps ran to their end"] and [s.get("msg") for s in seen] == ["wudi global avatar off"]
              and [c.get("op") for c in fk.served] == ["quit"], (code, failed_names, [c.get("op") for c in fk.served]))

        # Whole sessions through a stand-in runner: what it gets of the environment, and every way a session ends.
        globals()["installed_launcher_root"] = lambda: None
        with open(os.path.join(root, "fake_runner.py"), "w", encoding="utf-8") as f:
            f.write(FAKE_RUNNER)
        fake_runner = os.path.join(root, "fake_runner.cmd")
        with open(fake_runner, "w", encoding="ascii", newline="\r\n") as f:
            f.write('@"{}" -B "{}" %*\n'.format(sys.executable, os.path.join(root, "fake_runner.py")))
        dev_dll = os.path.join(root, "dev.dll")
        with open(dev_dll, "wb") as f:
            f.write(b"MZ a stand-in " + dd.MARKER.encode("ascii") + b" build")
        dev_sha = sha256_file(dev_dll)
        os.environ["DEVTEST_SELFTEST_KEY"] = token

        def launch_case(case, runner_cfg=None, dll_sha=None, screen="windowed", shot_size=None, steps=None, extra=()):
            ee_dir = os.path.join(root, case, "ayy", "anime", "ee")
            fk = dd.FakeMod(os.path.join(ee_dir, dd.CHANNEL), in_game=True, dll_sha=dll_sha or dev_sha)
            os.makedirs(fk.folder)
            if screen != "windowed":
                fk.screen = screen
            if shot_size:
                fk.shot_size = shot_size
            cfg = dict({"case": case, "tokenEnv": "DEVTEST_SELFTEST_KEY", "status": os.path.join(fk.folder, "status.json"),
                        "gameDll": os.path.join(ee_dir, "CLibrary.dll")}, **(runner_cfg or {}))
            with open(os.path.join(root, "fake_runner.json"), "w", encoding="utf-8") as f:
                json.dump(cfg, f)
            sc_path = os.path.join(root, case + ".json")
            with open(sc_path, "w", encoding="utf-8") as f:
                json.dump({"name": "selftest-" + case, "version": "2.8", "session": {"noFaults": True},
                           "server": {"expectHost": "${RELIC_DEVTEST_X}", "agent": "${RELIC_DEVTEST_AGENT}", "tokenEnv": "DEVTEST_SELFTEST_KEY"},
                           "steps": steps if steps is not None else [{"op": "ping"}, {"op": "gm", "msg": "wudi global avatar on"}]}, f)
            fk.start()
            out_dir = os.path.join(root, "report-" + case)
            rc = main([sc_path, "--dir", fk.folder, "--runner", fake_runner, "--dll", dev_dll, "--out", out_dir,
                       "--output-log", outlog, "--launch-timeout", "30"] + list(extra))
            fk.stop()
            ended = os.path.join(root, "ended-" + case)
            limit = time.time() + 20
            while not os.path.exists(ended) and time.time() < limit:    # the stand-in runner ends by itself
                time.sleep(0.2)
            data = dd.read_json(os.path.join(out_dir, "report.json")) or {}
            return rc, data, fk, {s["name"]: s["ok"] for s in data.get("session", [])}

        code, rep5, fk, checks = launch_case("session")
        result = (rep5.get("runner") or {}).get("result") or {}
        check("a whole session through the runner passes: the window, the release DLL, the live profile, Fiddler",
              code == 0 and checks.get("the game runs in a window of at most 1600x900") and checks.get("the release DLL is back in the game folder")
              and checks.get("the live profile is back") and checks.get("Fiddler is stopped")
              and [c.get("op") for c in fk.served] == ["ping", "quit"], (code, checks, [c.get("op") for c in fk.served]))
        check("the runner gets none of the scenario's secrets (RELIC_DEVTEST_*, server.tokenEnv)", result.get("envSeen") == [], result.get("envSeen"))

        code, rep6, fk, checks = launch_case("wrong-build", dll_sha="cd" * 32)
        check("a DevTest build that is not the staged one fails the run and is closed through the channel",
              code >= 1 and checks.get("the DevTest DLL is up") is False and [c.get("op") for c in fk.served] == ["quit"]
              and checks.get("the runner ended cleanly"), (code, checks, [c.get("op") for c in fk.served]))

        code, rep7, fk, checks = launch_case("stuck", runner_cfg={"linger": 4}, extra=["--exit-timeout", "1"])
        check("a runner that has not ended by --exit-timeout is a failed check", code >= 1 and checks.get("the runner ended within 1 s") is False,
              (code, checks))

        code, rep8, fk, checks = launch_case("fullscreen", screen={"fullScreen": True, "width": 3440, "height": 1440})
        check("a game that went fullscreen anyway fails the run: its steps are skipped and it is closed at once",
              code >= 1 and checks.get("the game runs in a window of at most 1600x900") is False
              and all(s.get("verdict") == "skipped" for s in rep8.get("steps", [])) and [c.get("op") for c in fk.served] == ["quit"],
              (code, checks, [c.get("op") for c in fk.served]))
        code, rep9, fk, checks = launch_case("fullscreen-buffer", screen=None, shot_size=(3440, 1440))
        check("... and so does one whose back buffer is larger than the window, from a build that reports no screen",
              code >= 1 and checks.get("the game runs in a window of at most 1600x900") is False and [c.get("op") for c in fk.served] == ["shot", "quit"]
              and os.path.isfile(os.path.join(root, "report-fullscreen-buffer", "shots", "00-window-check.png"))
              and not os.listdir(os.path.join(fk.folder, "shots")), (code, checks, [c.get("op") for c in fk.served]))

        code, rep10, fk, checks = launch_case("left-loaded", runner_cfg={"live": False, "fiddler": True, "exit": 5})
        check("a session that left its profile loaded and Fiddler running fails those checks",
              code >= 3 and checks.get("the live profile is back") is False and checks.get("Fiddler is stopped") is False
              and checks.get("the runner ended cleanly") is False, (code, checks))

        unshipped = "the DevTest DLL is out of the game folder (the payload ships none for this version)"
        code, rep11, fk, checks = launch_case("unshipped", runner_cfg={"unshipped": True, "marker": dd.MARKER})
        check("a version the payload ships no DLL for passes once the DevTest DLL is out of the game folder",
              code == 0 and checks.get(unshipped) is True and "the release DLL is back in the game folder" not in checks, (code, checks))
        code, rep12, fk, checks = launch_case("unshipped-left", runner_cfg={"unshipped": True, "leaveDev": True, "marker": dd.MARKER})
        check("and fails while the DevTest DLL is still there", code != 0 and checks.get(unshipped) is False, (code, checks))
    finally:
        if server is not None:
            server.shutdown()
        for k, v in saved_env.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v
        globals().update(saved_globals)
        shutil.rmtree(root, ignore_errors=True)
    print("{}/{} checks passed".format(check.n - check.failed, check.n))
    return 0 if check.failed == 0 else 1


def main(argv=None):
    ap = argparse.ArgumentParser(description="Run a DevTest scenario (dev builds only) and write its report.")
    ap.add_argument("scenario", nargs="?", help="the scenario JSON file")
    ap.add_argument("--out", help="the report folder (default {}\\<scenario>-<time>)".format(REPORTS_DIR))
    ap.add_argument("--dll", help="the DevTest CLibrary.dll (default: the newer of entertainment_experience\\mod\\bin\\v<nn>\\DevTest-x64\\"
                                  "CLibrary.dll and {}\\<version>\\CLibrary.dll)".format(DEVTEST_STAGE))
    ap.add_argument("--no-launch", action="store_true", help="the game already runs with the DevTest DLL: no runner")
    ap.add_argument("--keep-game", action="store_true", help="no quit at the end; the runner finishes when the game is closed")
    ap.add_argument("--build", action="store_true", help="build the runner first (it is built anyway when it is missing)")
    ap.add_argument("--runner", help="the DevTestRunner.exe to use")
    ap.add_argument("--window", help="WxH or keep (default: the scenario's runner.window, else 1600x900)")
    ap.add_argument("--own-files", choices=["keep", "replace"], help="the answer about the player's own game files (default keep)")
    ap.add_argument("--launch-timeout", type=float, default=600.0, help="seconds for the DLL to answer after the start")
    ap.add_argument("--exit-timeout", type=float, default=300.0, help="seconds for the runner to end after the quit")
    ap.add_argument("--dir", help="the devtest folder itself (instead of the version's game folder from Relic's state)")
    ap.add_argument("--state", help="Relic's state file (default %%LOCALAPPDATA%%\\Relic\\state.json)")
    ap.add_argument("--output-log", help="the game's output_log.txt (default the live LocalLow one)")
    ap.add_argument("--selftest", action="store_true", help="the run's mechanics on a stand-in for the game")
    args = ap.parse_args(argv)
    if args.selftest:
        return selftest()
    if not args.scenario:
        ap.error("a scenario file is needed")
    try:
        return min(run(args), 250)
    except (RunError, dd.DriverError, MissingEnv) as e:
        print("error: {}".format(e), file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("interrupted -- a runner already started goes on by itself and puts everything back once the game is closed",
              file=sys.stderr)
        return 130


if __name__ == "__main__":
    sys.exit(main())
