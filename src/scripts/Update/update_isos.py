#!/usr/bin/env python3
"""
Update-Isos  --  module 1 of the IT Toolkit update utility.

Downloads the newest release of every ISO on the Ventoy stick from the vendor's
official site and saves it under the stable filename that ventoy\\ventoy.json
expects, so aliases/tips/classes never need to change.

    ISO\\1-Windows Install\\Win11_x64.iso        Microsoft (Fido-style session API)
    ISO\\1-Windows Install\\Win11_arm.iso        Microsoft (Fido-style session API)
    ISO\\2-Linux Install\\ubuntu.iso             releases.ubuntu.com (latest LTS desktop)
    ISO\\2-Linux Install\\Fedora.iso             fedoraproject.org (KDE Plasma live, latest GA)
    ISO\\3-Repair\\HBCD_PE_x64.iso               hirensbootcd.org
    ISO\\3-Repair\\shredos.iso                   github.com/PartialVolume/shredos.x86_64
    ISO\\3-Repair\\Memtest86plus.iso             memtest.org (x86_64 ISO)
    ISO\\3-Repair\\systemrescue.iso              system-rescue.org

Usage:
    python update_isos.py --list                 show what is on disk vs. latest online
    python update_isos.py                        update everything that is out of date
    python update_isos.py ubuntu shredos         update only these entries
    python update_isos.py --force win11_x64      re-download even if current
    python update_isos.py --dry-run              same as --list, never downloads

Needs Python 3.8+ and nothing else (standard library only).
Versions are logged to src\\scripts\\Update\\ver.log, section [ISO], and compared with the
latest online version on the next run.
Downloads go to "<name>.part" first, are checksum-verified where the vendor
publishes a checksum, and only then replace the old ISO.
"""

import argparse
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
import uuid
import zipfile
from http.cookiejar import CookieJar

# ----------------------------------------------------------------------------
# Paths / constants
# ----------------------------------------------------------------------------
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))   # -I mode does not add the script dir
from updlib import *                                             # noqa: F401,F403
from updlib import _opener, _now, _human, _progress, _progress_end   # underscore names are not star-exported

WIN_DIR = os.path.join("ISO", "1-Windows Install")
LIN_DIR = os.path.join("ISO", "2-Linux Install")
REP_DIR = os.path.join("ISO", "3-Repair")
SECTION = "ISO"


def load_state():
    return load_section(SECTION)


def save_state(state):
    save_section(SECTION, state)


# ----------------------------------------------------------------------------
# Latest-release resolvers.  Each returns a dict:
#   version   human string; compared against state to decide "up to date"
#   url       direct download URL
#   sha256    expected hash of the *downloaded file* (or None)
#   size      expected size in bytes (or None)
#   extract   (optional) name of a member to pull out if the download is a zip
#   headers   (optional) extra request headers for the download
# ----------------------------------------------------------------------------

def latest_ubuntu():
    meta = http_get("https://changelogs.ubuntu.com/meta-release-lts")
    vers = []
    for block in meta.split("\n\n"):
        m = re.search(r"^Version:\s*([\d.]+)", block, re.M)
        if m and re.search(r"^Supported:\s*1", block, re.M):
            vers.append(m.group(1))
    if not vers:
        raise UpdateError("no supported Ubuntu LTS found")
    newest = max(vers, key=lambda v: [int(x) for x in v.split(".")])
    series = ".".join(newest.split(".")[:2])
    base = "https://releases.ubuntu.com/%s/" % series
    sums = parse_sums(http_get(base + "SHA256SUMS"))
    isos = [f for f in sums if re.fullmatch(r"ubuntu-[\d.]+-desktop-amd64\.iso", f)]
    if not isos:
        raise UpdateError("no desktop ISO listed in " + base)
    name = max(isos, key=lambda f: [int(x) for x in re.findall(r"\d+", f.split("-")[1])])
    version = name.split("-")[1]
    return {"version": version, "url": base + name, "sha256": sums[name]}


def latest_fedora():
    data = http_json("https://fedoraproject.org/releases.json")
    best = None
    for r in data:
        link = r.get("link", "")
        ver = r.get("version", "")
        if (r.get("arch") == "x86_64" and link.endswith(".iso")
                and "/KDE/x86_64/iso/Fedora-KDE-Desktop-Live" in link
                and re.fullmatch(r"\d+", ver)):          # skips "45 Beta"
            if best is None or int(ver) > int(best["version"]):
                best = r
    if not best:
        raise UpdateError("Fedora KDE live ISO not found in releases.json")
    return {"version": best["version"], "url": best["link"],
            "sha256": best.get("sha256"), "size": int(best["size"]) if best.get("size") else None}


def latest_shredos():
    rel = http_json("https://api.github.com/repos/PartialVolume/shredos.x86_64/releases/latest")
    for a in rel.get("assets", []):
        # the full x86-64 ISO: not lite, not plus-partition, not 32-bit
        if re.fullmatch(r"shredos-.*_x86-64_v[\d.]+_\d{8}\.iso", a["name"]):
            digest = (a.get("digest") or "")
            return {"version": rel["tag_name"] + " / " + a["name"],
                    "url": a["browser_download_url"], "size": a["size"],
                    "sha256": digest.split(":", 1)[1] if digest.startswith("sha256:") else None}
    raise UpdateError("ShredOS x86-64 ISO asset not found in latest release")


def latest_memtest():
    page = http_get("https://memtest.org/")
    vers = set(re.findall(r'/download/v([\d.]+)/', page))
    if not vers:
        raise UpdateError("could not find Memtest86+ version on memtest.org")
    ver = max(vers, key=lambda v: [int(x) for x in v.split(".")])
    base = "https://memtest.org/download/v%s/" % ver
    zipname = "mt86plus_%s_x86_64.iso.zip" % ver
    sums = parse_sums(http_get(base + "sha256sum.txt").replace("v%s/" % ver, ""))
    return {"version": ver, "url": base + zipname, "sha256": sums.get(zipname),
            "extract": True}                    # zip holds a single .iso (named "memtest.iso")


def latest_systemrescue():
    page = http_get("https://www.system-rescue.org/Download/")
    m = re.search(r'https://fastly-cdn\.system-rescue\.org/releases/([\d.]+)/(systemrescue-[\d.]+-amd64\.iso)', page)
    if not m:
        raise UpdateError("SystemRescue ISO link not found on download page")
    ver, name = m.group(1), m.group(2)
    base = "https://fastly-cdn.system-rescue.org/releases/%s/" % ver
    sums = parse_sums(http_get(base + name + ".sha256"))
    return {"version": ver, "url": base + name, "sha256": sums.get(name)}


def latest_hbcd():
    page = http_get("https://www.hirensbootcd.org/download/")
    m = re.search(r"Hiren.s BootCD PE x64 \(v([\d.]+)\)", page)
    ver = m.group(1) if m else "unknown"
    size, lastmod, etag, final = http_head("https://www.hirensbootcd.org/files/HBCD_PE_x64.iso")
    # no published checksum; the mirror's size+Last-Modified identify the build
    return {"version": "%s (%s)" % (ver, lastmod or "?"), "url": final, "sha256": None, "size": size}


# --- Windows 11 (Microsoft software-download-connector, same flow as Fido) ---

MS_ORG_ID = "y6jn8c31"
MS_PROFILE = "606624d44113"
MS_INSTANCE = "560dc9f3-1aa5-4a2f-b63c-9e18f8d0e175"
MS_FALLBACK_EDITIONS = (3813, 3816)       # "Windows 11 Home/Pro/Edu" per Fido; refreshed below
_ms_cache = {}


def _win11_edition_ids():
    """Product edition IDs rotate each release; Fido tracks them, so read them from there."""
    try:
        src = http_get("https://raw.githubusercontent.com/pbatard/Fido/master/Fido.ps1")
        m = re.search(r'"Windows 11 Home/Pro/Edu",\s*@\((\d+),\s*(\d+)\)', src)
        if m:
            return (int(m.group(1)), int(m.group(2)))
    except UpdateError:
        pass
    return MS_FALLBACK_EDITIONS


def _ms_links():
    """Return {arch_type: url}, arch_type 1=x64 2=ARM64, for English (US). Cached per run."""
    if "links" in _ms_cache:
        return _ms_cache["links"]
    links = {}
    for edition in _win11_edition_ids():
        op = _opener(cookies=True)
        sid = str(uuid.uuid4())
        # Microsoft's anti-abuse dance: whitelist the session id first.
        http_get("https://vlscppe.microsoft.com/tags?org_id=%s&session_id=%s" % (MS_ORG_ID, sid), opener=op)
        js = http_get("https://ov-df.microsoft.com/mdt.js?instanceId=%s&PageId=si&session_id=%s" % (MS_INSTANCE, sid), opener=op)
        w = re.search(r"[?&]w=([A-F0-9]+)", js)
        rt = re.search(r'rticks="\+?(\d+)', js)
        if not (w and rt):
            raise UpdateError("Microsoft changed its session protocol (ov-df); update the Windows resolver")
        http_get("https://ov-df.microsoft.com/?session_id=%s&CustomerId=%s&PageId=si&w=%s&mdt=%d&rticks=%s"
                 % (sid, MS_INSTANCE, w.group(1), int(time.time() * 1000), rt.group(1)), opener=op)
        sku_url = ("https://www.microsoft.com/software-download-connector/api/getskuinformationbyproductedition"
                   "?profile=%s&productEditionId=%d&SKU=undefined&friendlyFileName=undefined&Locale=en-US&sessionID=%s"
                   % (MS_PROFILE, edition, sid))
        skus = None
        for attempt in range(3):
            r = http_json(sku_url, opener=op)
            if r and not r.get("Errors") and r.get("Skus"):
                skus = r["Skus"]
                break
            time.sleep(2)
        if not skus:
            raise UpdateError("Microsoft returned no SKUs for edition %d" % edition)
        sku = next((s for s in skus if s.get("Language") == "English"), None)
        if not sku:
            raise UpdateError("no English (US) Windows 11 SKU offered")
        dl_url = ("https://www.microsoft.com/software-download-connector/api/GetProductDownloadLinksBySku"
                  "?profile=%s&productEditionId=undefined&SKU=%s&friendlyFileName=undefined&Locale=en-US&sessionID=%s"
                  % (MS_PROFILE, sku["Id"], sid))
        r = http_json(dl_url, opener=op,
                      headers={"Referer": "https://www.microsoft.com/software-download/windows11"})
        if r.get("Errors"):
            err = r["Errors"][0]
            if err.get("Type") == 9:
                raise UpdateError("Microsoft has temporarily blocked this IP (error 715-123130). "
                                  "Wait ~24h or try another network.")
            raise UpdateError("Microsoft: " + str(err.get("Value")))
        for opt in r.get("ProductDownloadOptions", []):
            links[opt["DownloadType"]] = opt["Uri"]
    if not links:
        raise UpdateError("Microsoft returned no download links")
    _ms_cache["links"] = links
    return links


def _latest_win11(arch_type):
    url = _ms_links().get(arch_type)
    if not url:
        raise UpdateError("Microsoft offered no download for this architecture")
    path = urllib.parse.urlparse(url).path
    name = os.path.basename(path)                  # e.g. Win11_25H2_English_x64_v2.iso
    size, _, _, _ = http_head(url)
    return {"version": name, "url": url, "sha256": None, "size": size}


def latest_win11_x64():
    return _latest_win11(1)


def latest_win11_arm():
    return _latest_win11(2)


# ----------------------------------------------------------------------------
# Catalog: key -> where it lives (never rename these; ventoy.json points at them)
# ----------------------------------------------------------------------------

CATALOG = [
    {"key": "win11_x64", "name": "Windows 11 x64",   "path": os.path.join(WIN_DIR, "Win11_x64.iso"),       "resolve": latest_win11_x64},
    {"key": "win11_arm", "name": "Windows 11 ARM64", "path": os.path.join(WIN_DIR, "Win11_arm.iso"),       "resolve": latest_win11_arm},
    {"key": "ubuntu",    "name": "Ubuntu Desktop LTS", "path": os.path.join(LIN_DIR, "ubuntu.iso"),        "resolve": latest_ubuntu},
    {"key": "fedora",    "name": "Fedora KDE Live",  "path": os.path.join(LIN_DIR, "Fedora.iso"),          "resolve": latest_fedora},
    {"key": "hbcd",      "name": "Hiren's BootCD PE", "path": os.path.join(REP_DIR, "HBCD_PE_x64.iso"),    "resolve": latest_hbcd},
    {"key": "shredos",   "name": "ShredOS",          "path": os.path.join(REP_DIR, "shredos.iso"),         "resolve": latest_shredos},
    {"key": "memtest",   "name": "Memtest86+",       "path": os.path.join(REP_DIR, "Memtest86plus.iso"),   "resolve": latest_memtest},
    {"key": "sysrescue", "name": "SystemRescue",     "path": os.path.join(REP_DIR, "systemrescue.iso"),    "resolve": latest_systemrescue},
]


# ----------------------------------------------------------------------------
# Per-entry logic
# ----------------------------------------------------------------------------

def local_info(path):
    return os.path.getsize(path) if os.path.isfile(path) else None


def check_entry(entry, root, state):
    """Return (status, latest). status: missing | current | outdated | unknown."""
    latest = entry["resolve"]()
    path = os.path.join(root, entry["path"])
    size = local_info(path)
    rec = state.get(entry["key"])
    if size is None:
        return "missing", latest
    if rec and rec.get("version") == latest["version"]:
        return "current", latest
    if rec is None:
        # Never tracked: adopt if the file provably matches the latest release.
        if latest.get("size") and latest["size"] == size and not latest.get("extract") and latest.get("sha256") is None:
            return "adopt", latest
        if latest.get("sha256") and (latest.get("size") in (None, size)) and not latest.get("extract"):
            return "hash", latest
        if latest.get("extract"):
            return "hash", latest                # zip hash can't match an iso; handled by caller
        return "unknown", latest
    return "outdated", latest


def update_entry(entry, root, state, force=False, dry=False):
    key, name = entry["key"], entry["name"]
    path = os.path.join(root, entry["path"])
    try:
        status, latest = check_entry(entry, root, state)
    except UpdateError as e:
        print("[%-9s] %-20s ERROR resolving latest: %s" % (key, name, e))
        return False

    rec = state.get(key) or {}
    have = rec.get("version", "untracked" if os.path.isfile(path) else "-")
    print("[%-9s] %-20s on disk: %s | latest: %s" % (key, name, have, latest["version"]))

    if status in ("adopt", "hash"):
        # Decide whether the existing file already IS the latest release.
        if status == "hash" and latest.get("sha256") and not latest.get("extract"):
            if dry:
                print("            untracked file, would hash to compare (%s)" % status)
                return True
            if sha256_file(path, "hashing existing") == latest["sha256"]:
                status = "adopt"
        if status == "adopt":
            print("            existing file matches latest -> recorded as current")   # factual, so logged even on --list
            state[key] = {"version": latest["version"], "date": _now()}
            save_state(state)
            return True
        status = "outdated"
    if status == "unknown":
        status = "outdated"

    if status == "current" and not force:
        print("            up to date")
        return True
    if dry:
        print("            would download %s" % latest["url"].split("?")[0])
        return True

    os.makedirs(os.path.dirname(path), exist_ok=True)
    part = path + ".part"
    need = (latest.get("size") or 0)
    free = shutil.disk_usage(os.path.dirname(path)).free
    already = os.path.getsize(part) if os.path.exists(part) else 0
    if need and free + already < need + (64 << 20):
        print("            NOT ENOUGH SPACE: need %s, free %s" % (_human(need), _human(free)))
        return False

    try:
        dl_part = part + ".dl" if latest.get("extract") else part
        download(latest["url"], dl_part, latest.get("size"), latest.get("headers"))
        if latest.get("sha256"):
            got = sha256_file(dl_part, "verifying sha256")
            if got != latest["sha256"]:
                os.remove(dl_part)
                raise UpdateError("checksum mismatch (got %s, want %s); partial file deleted" % (got, latest["sha256"]))
            print("            sha256 OK")
        else:
            print("            (vendor publishes no checksum; size checked only)")
        if latest.get("extract"):
            with zipfile.ZipFile(dl_part) as z:
                member = next(n for n in z.namelist() if n.lower().endswith(".iso"))
                with z.open(member) as src, open(part, "wb") as dst:
                    shutil.copyfileobj(src, dst, 1 << 20)
            os.remove(dl_part)
        os.replace(part, path)                   # atomic swap; old ISO is replaced only now
    except (UpdateError, OSError, zipfile.BadZipFile, StopIteration) as e:
        print("            FAILED: %s" % e)
        return False
    state[key] = {"version": latest["version"], "date": _now()}
    save_state(state)
    print("            updated -> %s" % entry["path"])
    return True


# ----------------------------------------------------------------------------

def main(argv=None):
    ap = argparse.ArgumentParser(description="Download the newest ISOs for the IT Toolkit Ventoy stick.")
    ap.add_argument("only", nargs="*", help="entries to process (default: all). Keys: " +
                    ", ".join(e["key"] for e in CATALOG))
    ap.add_argument("--root", default=DEFAULT_ROOT, help="toolkit drive root (default: %(default)s)")
    ap.add_argument("--list", "--dry-run", "-n", dest="dry", action="store_true",
                    help="only report status; download nothing")
    ap.add_argument("--machine", action="store_true", help=argparse.SUPPRESS)   # GUI progress protocol
    ap.add_argument("--force", "-f", action="store_true", help="re-download even if up to date")
    args = ap.parse_args(argv)

    keys = {e["key"] for e in CATALOG}
    bad = [k for k in args.only if k not in keys]
    if bad:
        ap.error("unknown entries: %s (valid: %s)" % (", ".join(bad), ", ".join(sorted(keys))))
    if not os.path.isdir(os.path.join(args.root, "ISO")):
        ap.error("no ISO folder under %s (use --root)" % args.root)

    state = load_state()
    todo = [e for e in CATALOG if not args.only or e["key"] in args.only]
    import updlib
    updlib.MACHINE = args.machine
    results = {}
    for i, e in enumerate(todo, 1):
        updlib.emit("ENTRY", i, len(todo), e["key"], e["name"])
        results[e["key"]] = update_entry(e, args.root, state, args.force, args.dry)
    failed = [k for k, ok in results.items() if not ok]
    print("\nDone. %d ok, %d failed%s" % (len(results) - len(failed), len(failed),
                                          (": " + ", ".join(failed)) if failed else ""))
    updlib.emit("DONE", len(results) - len(failed), len(failed))
    return 1 if failed else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\nInterrupted. Partial downloads (*.part) resume on the next run.")
        sys.exit(130)
