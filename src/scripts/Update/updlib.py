"""
updlib -- shared helpers for the toolkit update modules (update_isos.py, update_tools.py).
HTTP, resumable download with progress, sha256, and the shared ver.log
(one [Section] per module; each module only rewrites its own section).
"""

import hashlib
import json
import os
import re
import shutil
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from http.cookiejar import CookieJar

# ----------------------------------------------------------------------------
# Paths / constants
# ----------------------------------------------------------------------------

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DEFAULT_ROOT = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "..", ".."))   # src\scripts\Update -> drive root

# Machine-readable mode (--machine): used by the GUI. Besides normal text lines the modules print
#   @@ENTRY|index|count|key|name      an entry is starting
#   @@PROG|label|done|total           byte progress (total 0 = unknown)
#   @@DONE|ok|failed                  module finished
MACHINE = False


def emit(*parts):
    if MACHINE:
        sys.stdout.write("@@" + "|".join(str(p) for p in parts) + "\n")
        sys.stdout.flush()
LOG_PATH = os.path.join(SCRIPT_DIR, "ver.log")   # one [Section] per module: ISO, Tools, Skipped
UA = "Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0"
TIMEOUT = 60



class UpdateError(Exception):
    pass


# ----------------------------------------------------------------------------
# HTTP helpers
# ----------------------------------------------------------------------------

def _opener(cookies=False):
    handlers = []
    if cookies:
        handlers.append(urllib.request.HTTPCookieProcessor(CookieJar()))
    op = urllib.request.build_opener(*handlers)
    op.addheaders = [("User-Agent", UA)]
    return op


_default_opener = _opener()


def http_get(url, headers=None, opener=None, binary=False):
    req = urllib.request.Request(url, headers=headers or {})
    try:
        with (opener or _default_opener).open(req, timeout=TIMEOUT) as r:
            data = r.read()
    except urllib.error.HTTPError as e:
        raise UpdateError("HTTP %s for %s" % (e.code, url))
    except urllib.error.URLError as e:
        raise UpdateError("%s (%s)" % (e.reason, url))
    return data if binary else data.decode("utf-8", "replace")


def http_json(url, **kw):
    return json.loads(http_get(url, **kw))


def http_head(url, opener=None):
    """Return (size, last_modified, etag, final_url) without downloading."""
    req = urllib.request.Request(url, method="HEAD")
    try:
        with (opener or _default_opener).open(req, timeout=TIMEOUT) as r:
            size = r.headers.get("Content-Length")
            return (int(size) if size else None, r.headers.get("Last-Modified"),
                    r.headers.get("ETag"), r.geturl())
    except (urllib.error.URLError, ValueError) as e:
        raise UpdateError("HEAD failed for %s: %s" % (url, e))


def parse_sums(text):
    """'<hash>  [*]<file>' lines -> {file: hash}"""
    out = {}
    for line in text.splitlines():
        m = re.match(r"^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$", line)
        if m:
            out[m.group(2)] = m.group(1).lower()
    return out



# ----------------------------------------------------------------------------
# State (what version each file on the stick is)
# ----------------------------------------------------------------------------

def _read_log():
    """ver.log -> {section: {key: (version, date)}}; other sections are preserved on write."""
    out, cur = {}, None
    try:
        with open(LOG_PATH, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                m = re.fullmatch(r"\[(.+)\]", line)
                if m:
                    cur = out.setdefault(m.group(1), {})
                elif cur is not None and "=" in line:
                    k, v = line.split("=", 1)
                    ver, _, date = v.partition(" ; ")
                    cur[k.strip()] = (ver.strip(), date.strip())
    except OSError:
        pass
    return out


def load_section(section):
    """Last logged version per key: {key: {"version": str, "date": str}}."""
    return {k: {"version": v, "date": d} for k, (v, d) in _read_log().get(section, {}).items()}


def save_section(section, state):
    log = _read_log()
    log[section] = {k: (r["version"], r.get("date", "")) for k, r in state.items()}
    with open(LOG_PATH, "w", encoding="utf-8", newline="\r\n") as f:
        f.write("# Toolkit version log. Format:  key = version ; date logged\n")
        f.write("# Written by the update utility; compared against the latest online version.\n")
        for sec, entries in log.items():
            f.write("\n[%s]\n" % sec)
            for k, (v, d) in sorted(entries.items()):
                f.write("%s = %s%s\n" % (k, v, (" ; " + d) if d else ""))


def _now():
    return time.strftime("%Y-%m-%d %H:%M")


def sha256_file(path, label=""):
    h = hashlib.sha256()
    total = os.path.getsize(path)
    done = 0
    with open(path, "rb") as f:
        while True:
            b = f.read(1 << 20)
            if not b:
                break
            h.update(b)
            done += len(b)
            _progress(label or "hashing", done, total)
    _progress_end()
    return h.hexdigest()


# ----------------------------------------------------------------------------
# Download
# ----------------------------------------------------------------------------

def _human(n):
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return "%.1f %s" % (n, unit) if unit != "B" else "%d B" % n
        n /= 1024.0


_last_draw = [0.0]


def _progress(label, done, total):
    if MACHINE:
        now = time.time()
        if now - _last_draw[0] >= 0.2 or (total and done >= total):
            _last_draw[0] = now
            emit("PROG", label, done, total or 0)
        return
    now = time.time()
    if now - _last_draw[0] < 0.25 and done != total:
        return
    _last_draw[0] = now
    pct = (" %5.1f%%" % (100.0 * done / total)) if total else ""
    sys.stdout.write("\r  %s: %s%s%s   " % (label, _human(done), (" / " + _human(total)) if total else "", pct))
    sys.stdout.flush()


def _progress_end():
    if MACHINE:
        return
    sys.stdout.write("\n")
    sys.stdout.flush()


def download(url, part_path, expected_size=None, headers=None):
    """Download with resume into part_path. Returns when complete."""
    existing = os.path.getsize(part_path) if os.path.exists(part_path) else 0
    hdrs = dict(headers or {})
    if existing:
        hdrs["Range"] = "bytes=%d-" % existing
    req = urllib.request.Request(url, headers=hdrs)
    try:
        resp = _default_opener.open(req, timeout=TIMEOUT)
    except urllib.error.HTTPError as e:
        if e.code == 416 and existing:           # .part already complete
            return
        raise UpdateError("HTTP %s downloading %s" % (e.code, url))
    except urllib.error.URLError as e:
        raise UpdateError("%s downloading %s" % (e.reason, url))
    with resp:
        mode = "ab"
        if existing and resp.status != 206:      # server ignored Range: start over
            existing, mode = 0, "wb"
        length = resp.headers.get("Content-Length")
        total = (int(length) + existing) if length else expected_size
        done = existing
        with open(part_path, mode) as f:
            while True:
                chunk = resp.read(1 << 20)
                if not chunk:
                    break
                f.write(chunk)
                done += len(chunk)
                _progress("downloading", done, total)
        _progress_end()
        if total and done < total:
            raise UpdateError("connection dropped at %s of %s; re-run to resume" % (_human(done), _human(total)))


