#!/usr/bin/env python3
"""
Update-Tools  --  module 2 of the IT Toolkit update utility.

Keeps the portable programs in Programs\\ current. The launchers in Tools\\*\\*.exe
point at fixed paths inside Programs\\, so every update is unpacked/renamed to the
exact same layout and the launchers keep working.

    Programs\\7-Zip\\                7-Zip          github.com/ip7z/7zip (x64 installer, unpacked)
    Programs\\WinDirStat\\           WinDirStat     github.com/windirstat/windirstat (portable zip)
    Programs\\Revo Uninstaller\\     Revo Free      revouninstaller.com (portable zip)
    Programs\\hdsentinel\\           HD Sentinel Pro  hdsentinel.com (portable zip; license kept)
    Programs\\RogueKiller.exe       Adlice Protect  adlice.com (portable x64, Win10+ build)
    Programs\\MBSetup.exe           Malwarebytes   malwarebytes.com (online installer)
    Programs\\Notepad++\\            Notepad++      github.com/notepad-plus-plus (portable zip)
    Programs\\Autoruns\\, Sysinternals Suite\\   download.sysinternals.com (zips)
    Programs\\Everything\\, PuTTY\\, Prime95\\ vendor portable zips
    Programs\\Rufus\\Rufus.exe, adwcleaner.exe, Samsung_Magician_Installer.exe   vendor downloads, renamed to the launcher names

Versions are logged in ver.log under [Tools]. Everything else found in Programs\\ is
listed under [Skipped] in the same file, for future reference.

Usage:
    python update_tools.py --list         show status, download nothing
    python update_tools.py                update whatever is out of date
    python update_tools.py 7zip notepadpp only those entries
    python update_tools.py --force revo   reinstall even if current

Existing files are overwritten by newer ones, never deleted, and user data
(HD Sentinel's HDSData\\ incl. license, Revo settings) is never overwritten.
"""

import argparse
import fnmatch
import os
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))   # -I mode does not add the script dir
from updlib import *                                             # noqa: F401,F403
from updlib import _now, _human, _progress, _progress_end, _default_opener

SECTION = "Tools"
SKIPPED = "Skipped"
PROGRAMS = "Programs"
BIN_DIR = os.path.join(SCRIPT_DIR, ".bin")        # helper binaries fetched on demand


# ----------------------------------------------------------------------------
# GitHub helper
# ----------------------------------------------------------------------------

def gh_latest(repo):
    rel = http_json("https://api.github.com/repos/%s/releases/latest" % repo)
    return rel, rel["assets"]


def gh_asset(assets, pattern):
    for a in assets:
        if re.fullmatch(pattern, a["name"]):
            digest = a.get("digest") or ""
            return a, (digest.split(":", 1)[1] if digest.startswith("sha256:") else None)
    raise UpdateError("no release asset matching %s" % pattern)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


def redirect_target(url):
    """Return the Location a URL redirects to, without following it."""
    op = urllib.request.build_opener(NoRedirect)
    op.addheaders = [("User-Agent", UA)]
    try:
        op.open(url, timeout=TIMEOUT)
    except urllib.error.HTTPError as e:
        loc = e.headers.get("Location")
        if loc:
            return urllib.parse.urljoin(url, loc)
        raise UpdateError("HTTP %s from %s" % (e.code, url))
    raise UpdateError("expected a redirect from " + url)


# ----------------------------------------------------------------------------
# Resolvers: -> {version, url, sha256, size, ...extras}
# ----------------------------------------------------------------------------

def latest_7zip():
    rel, assets = gh_latest("ip7z/7zip")
    a, sha = gh_asset(assets, r"7z\d+-x64\.exe")
    out = {"version": rel["tag_name"], "url": a["browser_download_url"], "sha256": sha, "size": a["size"]}
    try:
        m, _ = gh_asset(assets, r"7z\d+-mac\.tar\.xz")        # for unpacking on macOS
        out["mac_tool_url"] = m["browser_download_url"]
    except UpdateError:
        pass
    try:
        msi, _ = gh_asset(assets, r"7z\d+-x64\.msi")           # Windows fallback
        out["msi_url"] = msi["browser_download_url"]
    except UpdateError:
        pass
    return out


def latest_windirstat():
    rel, assets = gh_latest("windirstat/windirstat")
    a, sha = gh_asset(assets, r"WinDirStat\.zip")
    ver = re.sub(r"^.*?v", "", rel["tag_name"])
    return {"version": ver, "url": a["browser_download_url"], "sha256": sha, "size": a["size"]}


def latest_notepadpp():
    rel, assets = gh_latest("notepad-plus-plus/notepad-plus-plus")
    a, sha = gh_asset(assets, r"npp\.[\d.]+\.portable\.x64\.zip")
    return {"version": rel["tag_name"].lstrip("v"), "url": a["browser_download_url"],
            "sha256": sha, "size": a["size"]}


def latest_revo():
    page = http_get("https://www.revouninstaller.com/start-freeware-download-portable/")
    m = re.search(r'https://revouninstaller\.b-cdn\.net/(ruf\d+)/RevoUninstaller_Portable\.zip', page)
    if not m:
        raise UpdateError("Revo portable download link not found")
    size, lastmod, _, _ = http_head(m.group(0))
    return {"version": "%s (%s)" % (m.group(1), lastmod or "?"), "url": m.group(0),
            "sha256": None, "size": size}


def latest_hdsentinel():
    url = "https://www.hdsentinel.com/hdsentinel_pro_portable.zip"
    size, lastmod, _, final = http_head(url)
    return {"version": "portable zip, modified %s" % (lastmod or "?"), "url": final,
            "sha256": None, "size": size}


def latest_adlice():
    loc = redirect_target("https://download.adlice.com/api/?action=download&app=roguekiller&type=x64&os=win10")
    m = re.search(r"path=([\d.]+)", loc)
    if not m:
        raise UpdateError("could not read Adlice version from " + loc)
    size, _, _, _ = http_head(loc)
    return {"version": m.group(1), "url": loc, "sha256": None, "size": size}


def latest_malwarebytes():
    loc = redirect_target("https://downloads.malwarebytes.com/file/mb-windows")
    size, lastmod, _, final = http_head(loc)
    return {"version": "installer, modified %s" % (lastmod or "?"), "url": final,
            "sha256": None, "size": size}


def _zip_by_lastmod(url, label):
    size, lastmod, _, final = http_head(url)
    return {"version": "%s, modified %s" % (label, lastmod or "?"), "url": final, "sha256": None, "size": size}


def latest_autoruns():
    return _zip_by_lastmod("https://download.sysinternals.com/files/Autoruns.zip", "zip")


def latest_sysinternals():
    return _zip_by_lastmod("https://download.sysinternals.com/files/SysinternalsSuite.zip", "suite zip")


def latest_everything():
    page = http_get("https://www.voidtools.com/downloads/")
    vers = set(re.findall(r'Everything-([\d.]+)\.x64\.zip', page))
    if not vers:
        raise UpdateError("Everything x64 zip not found on voidtools.com")
    v = max(vers, key=lambda x: [int(p) for p in x.split(".")])
    url = "https://www.voidtools.com/Everything-%s.x64.zip" % v
    size, _, _, final = http_head(url)
    return {"version": v, "url": final, "sha256": None, "size": size}


def latest_prime95():
    page = http_get("https://www.mersenne.org/download/")
    m = re.search(r'https://download\.mersenne\.ca/gimps/v\d+/([\d.]+)/p95v\d+(b\d+)?\.win64\.zip', page)
    if not m:
        raise UpdateError("Prime95 win64 zip not found on mersenne.org")
    size, _, _, final = http_head(m.group(0))
    return {"version": m.group(1) + (m.group(2) or ""), "url": final, "sha256": None, "size": size}


def latest_putty():
    loc = redirect_target("https://the.earth.li/~sgtatham/putty/latest/w64/putty.zip")
    m = re.search(r"/putty/([\d.]+)/", loc)
    if not m:
        raise UpdateError("could not read PuTTY version from " + loc)
    size, _, _, _ = http_head(loc)
    return {"version": m.group(1), "url": loc, "sha256": None, "size": size}


def latest_rufus():
    rel, assets = gh_latest("pbatard/rufus")
    a, sha = gh_asset(assets, r"rufus-[\d.]+\.exe")
    return {"version": rel["tag_name"].lstrip("v"), "url": a["browser_download_url"],
            "sha256": sha, "size": a["size"]}


def latest_adwcleaner():
    loc = redirect_target("https://downloads.malwarebytes.com/file/adwcleaner")
    size, lastmod, _, final = http_head(loc)
    return {"version": "modified %s" % (lastmod or "?"), "url": final, "sha256": None, "size": size}


def latest_samsung_magician():
    page = http_get("https://semiconductor.samsung.com/consumer-storage/support/tools/")
    found = set(re.findall(r'https://download\.semiconductor\.samsung\.com/resources/software-resources/'
                           r'Samsung_Magician_Installer_Official_([\d.]+)\.exe', page))
    if not found:
        raise UpdateError("Samsung Magician installer link not found")
    v = max(found, key=lambda x: [int(p) for p in x.split(".")])
    url = ("https://download.semiconductor.samsung.com/resources/software-resources/"
           "Samsung_Magician_Installer_Official_%s.exe" % v)
    size, _, _, final = http_head(url)
    return {"version": v, "url": final, "sha256": None, "size": size}


# ----------------------------------------------------------------------------
# Catalog.  dest is relative to Programs\ and must never change (launchers use it).
#   kind   zip | zip_strip (zip with one wrapper folder) | nsis | file
#   check  file that must exist afterwards (what the Tools launcher starts)
#   keep   globs (relative to dest) that are never overwritten when present
#   adopt_by_size  untracked local file with the same size as latest = already current
# ----------------------------------------------------------------------------

CATALOG = [
    {"key": "7zip", "name": "7-Zip", "dest": "7-Zip", "kind": "nsis", "check": "7zFM.exe",
     "resolve": latest_7zip},
    {"key": "windirstat", "name": "WinDirStat", "dest": "WinDirStat", "kind": "zip",
     "check": os.path.join("x64", "WinDirStat.exe"), "resolve": latest_windirstat},
    {"key": "revo", "name": "Revo Uninstaller", "dest": "Revo Uninstaller", "kind": "zip_strip",
     "check": "RevoUPort.exe", "keep": ["settings.ini", "ctrlbars.dat", "un_report.dat"],
     "resolve": latest_revo},
    {"key": "hdsentinel", "name": "HD Sentinel Pro", "dest": "hdsentinel", "kind": "zip",
     "check": "HDSentinel.exe", "keep": ["HDSData/*"], "resolve": latest_hdsentinel},
    {"key": "adlice", "name": "Adlice Protect", "dest": "RogueKiller.exe", "kind": "file",
     "check": "", "resolve": latest_adlice},
    {"key": "malwarebytes", "name": "Malwarebytes Setup", "dest": "MBSetup.exe", "kind": "file",
     "check": "", "adopt_by_size": True, "resolve": latest_malwarebytes},
    {"key": "autoruns", "name": "Autoruns", "dest": "Autoruns", "kind": "zip",
     "check": "Autoruns.exe", "resolve": latest_autoruns},
    {"key": "sysinternals", "name": "Sysinternals Suite", "dest": "Sysinternals Suite", "kind": "zip",
     "check": "procexp.exe", "resolve": latest_sysinternals},
    {"key": "everything", "name": "Everything", "dest": "Everything", "kind": "zip",
     "check": "Everything.exe", "resolve": latest_everything},
    {"key": "prime95", "name": "Prime95", "dest": "Prime95", "kind": "zip", "check": "prime95.exe",
     "keep": ["prime.txt", "results.txt", "local.txt", "worktodo.txt", "prime.log"],
     "resolve": latest_prime95},
    {"key": "putty", "name": "PuTTY", "dest": "PuTTY", "kind": "zip", "check": "putty.exe",
     "resolve": latest_putty},
    {"key": "rufus", "name": "Rufus", "dest": os.path.join("Rufus", "Rufus.exe"), "kind": "file",
     "check": "", "adopt_by_size": True, "resolve": latest_rufus},
    {"key": "adwcleaner", "name": "AdwCleaner", "dest": "adwcleaner.exe", "kind": "file",
     "check": "", "adopt_by_size": True, "resolve": latest_adwcleaner},
    {"key": "samsung", "name": "Samsung Magician", "dest": "Samsung_Magician_Installer.exe", "kind": "file",
     "check": "", "adopt_by_size": True, "resolve": latest_samsung_magician},
    {"key": "notepadpp", "name": "Notepad++", "dest": "Notepad++", "kind": "zip",
     "check": "notepad++.exe", "resolve": latest_notepadpp},
]


# ----------------------------------------------------------------------------
# Unpacking
# ----------------------------------------------------------------------------

def _run(cmd):
    r = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if r.returncode != 0:
        raise UpdateError("%s failed: %s" % (os.path.basename(cmd[0]),
                                             r.stdout.decode("utf-8", "replace")[-300:]))


def find_7z(root, latest):
    """An extractor that understands NSIS installers: PATH, the stick's own 7z.exe, or a fetched 7zz (macOS)."""
    for n in ("7zz", "7z", "7zr"):
        p = shutil.which(n)
        if p:
            return p
    own = os.path.join(root, PROGRAMS, "7-Zip", "7z.exe")
    if os.path.isfile(own) and os.name == "nt":
        return own
    cached = os.path.join(BIN_DIR, "7zz")
    if os.path.isfile(cached):
        return cached
    if sys.platform == "darwin" and latest.get("mac_tool_url"):
        os.makedirs(BIN_DIR, exist_ok=True)
        tarball = os.path.join(BIN_DIR, "7z-mac.tar.xz")
        print("            fetching 7-Zip's own macOS extractor (one time)")
        download(latest["mac_tool_url"], tarball)
        with tarfile.open(tarball) as t:
            member = next(m for m in t.getmembers() if os.path.basename(m.name) == "7zz")
            member.name = "7zz"
            t.extract(member, BIN_DIR)
        os.remove(tarball)
        os.chmod(cached, os.stat(cached).st_mode | stat.S_IXUSR)
        return cached
    return None


def unpack(entry, archive, tmp, root, latest):
    """Unpack archive into tmp; return the directory whose contents belong in dest."""
    kind = entry["kind"]
    if kind in ("zip", "zip_strip"):
        try:
            with zipfile.ZipFile(archive) as z:
                z.extractall(tmp)
        except NotImplementedError:                    # e.g. Deflate64 (Revo): Python can't, 7-Zip can
            ex = find_7z(root, latest)
            if not ex:
                raise UpdateError("zip uses a compression Python lacks and no 7-Zip is available")
            shutil.rmtree(tmp, ignore_errors=True)
            os.makedirs(tmp)
            _run([ex, "x", "-y", "-o" + tmp, archive])
        if kind == "zip_strip":
            subs = [d for d in os.listdir(tmp) if os.path.isdir(os.path.join(tmp, d))]
            if len(subs) != 1 or len(os.listdir(tmp)) != 1:
                raise UpdateError("expected one wrapper folder in the zip, found %s" % os.listdir(tmp))
            return os.path.join(tmp, subs[0])
        return tmp
    if kind == "nsis":
        ex = find_7z(root, latest)
        if ex:
            _run([ex, "x", "-y", "-o" + tmp, archive])
        elif os.name == "nt" and latest.get("msi_url"):
            msi = archive + ".msi"
            download(latest["msi_url"], msi)
            _run(["msiexec", "/a", msi, "/qn", "TARGETDIR=" + tmp])    # unpack only, no install
            os.remove(msi)
        else:
            raise UpdateError("no extractor available (install 7-Zip or run on Windows/macOS)")
        for dp, dn, fn in os.walk(tmp):                                # locate the program root
            if entry["check"] in fn:
                return dp
        raise UpdateError("%s not found in unpacked installer" % entry["check"])
    raise UpdateError("unknown kind " + kind)


JUNK = {"uninstall.exe", "$pluginsdir", "$_outdir"}


def merge(src, dst, keep):
    """Copy src tree over dst (overwrite, never delete). Returns count of files written."""
    n = 0
    for dp, dn, fn in os.walk(src):
        dn[:] = [d for d in dn if d.lower() not in JUNK and not d.startswith("$")]
        for f in fn:
            if f.lower() in JUNK or f.startswith("._"):
                continue
            rel = os.path.relpath(os.path.join(dp, f), src)
            target = os.path.join(dst, rel)
            relslash = rel.replace(os.sep, "/")
            if os.path.exists(target) and any(fnmatch.fnmatch(relslash, g) for g in keep):
                continue
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copy2(os.path.join(dp, f), target)
            n += 1
    return n


# ----------------------------------------------------------------------------
# Per-entry logic
# ----------------------------------------------------------------------------

def update_entry(entry, root, state, force=False, dry=False):
    key, name = entry["key"], entry["name"]
    dest = os.path.join(root, PROGRAMS, entry["dest"])
    try:
        latest = entry["resolve"]()
    except (UpdateError, KeyError) as e:
        print("[%-12s] %-20s ERROR resolving latest: %s" % (key, name, e))
        return False

    rec = state.get(key)
    exists = os.path.exists(dest)
    have = rec["version"] if rec else ("untracked" if exists else "-")
    print("[%-12s] %-20s logged: %s | latest: %s" % (key, name, have, latest["version"]))

    if (exists and not rec and entry.get("adopt_by_size") and latest.get("size")
            and os.path.isfile(dest) and os.path.getsize(dest) == latest["size"]):
        print("               existing file matches latest -> logged")     # factual, so logged even on --list
        state[key] = {"version": latest["version"], "date": _now()}
        save_section(SECTION, state)
        return True
    if rec and rec["version"] == latest["version"] and exists and not force:
        print("               up to date")
        return True
    if dry:
        print("               would download %s" % latest["url"].split("?")[0])
        return True

    programs = os.path.join(root, PROGRAMS)
    work = tempfile.mkdtemp(prefix=".upd_%s_" % key, dir=programs)
    try:
        archive = os.path.join(work, "download.bin")
        download(latest["url"], archive, latest.get("size"))
        if latest.get("sha256"):
            if sha256_file(archive, "verifying sha256") != latest["sha256"]:
                raise UpdateError("checksum mismatch; download discarded")
            print("               sha256 OK")
        elif latest.get("size") and os.path.getsize(archive) != latest["size"]:
            raise UpdateError("size mismatch; download discarded")
        else:
            print("               (vendor publishes no checksum; size checked only)")

        if entry["kind"] == "file":
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            os.replace(archive, dest)
        else:
            unpacked = os.path.join(work, "unpacked")
            os.makedirs(unpacked)
            src = unpack(entry, archive, unpacked, root, latest)
            os.makedirs(dest, exist_ok=True)
            n = merge(src, dest, entry.get("keep", []))
            print("               unpacked %d files into Programs\\%s" % (n, entry["dest"]))
            chk = os.path.join(dest, entry["check"])
            if not os.path.isfile(chk):
                raise UpdateError("launcher target missing after update: " + chk)
    except Exception as e:                             # one bad entry must not stop the rest
        print("               FAILED: %s: %s" % (type(e).__name__, e))
        return False
    finally:
        shutil.rmtree(work, ignore_errors=True)

    state[key] = {"version": latest["version"], "date": _now()}
    save_section(SECTION, state)
    print("               updated -> Programs\\%s" % entry["dest"])
    return True


# Why each unmanaged item is not auto-updated (shown in ver.log for future reference).
SKIP_REASONS = {
    "rkill.exe": "left out by choice",
    "reflect_setup_free_x64.exe": "left out by choice",
    "jrt.exe": "end of life; Malwarebytes retired Junkware Removal Tool",
    "ddu": "no direct download link (site hides it behind scripts)",
    "furmark": "no direct download link (site hides it behind scripts)",
    "pw-free-online.exe": "MiniTool online installer; no stable version info",
}


def write_skipped(root):
    """[Skipped]: everything in Programs\\ that this module does not manage."""
    managed = {e["dest"].replace("\\", "/").split("/")[0].lower() for e in CATALOG}
    old = load_section(SKIPPED)
    new = {}
    pdir = os.path.join(root, PROGRAMS)
    for n in sorted(os.listdir(pdir), key=str.lower):
        if n.startswith((".", "._")) or n.lower() in managed:
            continue
        kind = "folder" if os.path.isdir(os.path.join(pdir, n)) else "file"
        why = SKIP_REASONS.get(n.lower())
        new[n] = {"version": kind + (" - " + why if why else ""),
                  "date": old.get(n, {}).get("date") or _now()}
    save_section(SKIPPED, new)
    return list(new)


# ----------------------------------------------------------------------------

def main(argv=None):
    ap = argparse.ArgumentParser(description="Update the portable programs in Programs\\ on the IT Toolkit stick.")
    ap.add_argument("only", nargs="*", help="entries to process (default: all). Keys: " +
                    ", ".join(e["key"] for e in CATALOG))
    ap.add_argument("--root", default=DEFAULT_ROOT, help="toolkit drive root (default: %(default)s)")
    ap.add_argument("--list", "--dry-run", "-n", dest="dry", action="store_true",
                    help="only report status; download nothing")
    ap.add_argument("--machine", action="store_true", help=argparse.SUPPRESS)   # GUI progress protocol
    ap.add_argument("--force", "-f", action="store_true", help="reinstall even if up to date")
    args = ap.parse_args(argv)

    keys = {e["key"] for e in CATALOG}
    bad = [k for k in args.only if k not in keys]
    if bad:
        ap.error("unknown entries: %s (valid: %s)" % (", ".join(bad), ", ".join(sorted(keys))))
    if not os.path.isdir(os.path.join(args.root, PROGRAMS)):
        ap.error("no Programs folder under %s (use --root)" % args.root)

    state = load_section(SECTION)
    todo = [e for e in CATALOG if not args.only or e["key"] in args.only]
    import updlib
    updlib.MACHINE = args.machine
    results = {}
    for i, e in enumerate(todo, 1):
        updlib.emit("ENTRY", i, len(todo), e["key"], e["name"])
        results[e["key"]] = update_entry(e, args.root, state, args.force, args.dry)
    skipped = write_skipped(args.root)
    failed = [k for k, ok in results.items() if not ok]
    print("\nDone. %d ok, %d failed%s. %d other items in Programs\\ listed under [Skipped] in ver.log."
          % (len(results) - len(failed), len(failed), (": " + ", ".join(failed)) if failed else "", len(skipped)))
    updlib.emit("DONE", len(results) - len(failed), len(failed))
    return 1 if failed else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\nInterrupted.")
        sys.exit(130)
