#!/bin/bash
# UpdateToolkit (macOS, Terminal) - lives in system/Update on the stick. Double-click it in Finder.
# UpdateToolkit.app at the stick root is the windowed version and uses this script to find Python.
# Runs the same updaters as Windows (system/Update/update_isos.py and update_tools.py) and shares ver.log.
#
#   UpdateToolkit.command                  menu
#   UpdateToolkit.command all|isos|tools   start right away; extra options go to the updater:
#                                          --list (check only), --force, or entry names (e.g. ubuntu)
#   UpdateToolkit.command --print-python   print the Python to use (fetching it if needed) and exit;
#                                          used by UpdateToolkit.app, the windowed version
#
# Python: uses python3 (3.8+) from Homebrew, python.org or the Xcode tools if present. Otherwise it
# downloads a portable Python (python-build-standalone, ~25 MB, checksum-verified) into
# ~/Library/Caches/DCToolKit once. It is not put on the stick: exFAT cannot hold its symlinks.
set -uo pipefail
UPD="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$UPD/../.." && pwd)"                 # this script is in <stick>/system/Update
PBS_TAG="20261124"
PBS_PY="3.12.16"
CACHE="$HOME/Library/Caches/DCToolKit"

finish() {
    # macOS writes ._* files for extended attributes on FAT/exFAT; Windows shows them. ._. keeps the drive icon.
    /usr/bin/find "$ROOT" -name '._*' -type f ! -path "$ROOT/._." -delete 2>/dev/null
    echo
    read -r -p "Press Return to close this window." _ || true
    exit "${1:-0}"
}

[[ -f "$UPD/update_isos.py" ]] || { echo "Cannot find update_isos.py next to this script ($UPD)."; finish 1; }

py_ok() { "$1" -c 'import sys, ssl; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; }

find_python() {
    local c
    for c in /opt/homebrew/bin/python3 /usr/local/bin/python3 \
             /Library/Frameworks/Python.framework/Versions/Current/bin/python3 \
             "$CACHE/python/bin/python3"; do
        [[ -x "$c" ]] && py_ok "$c" && { echo "$c"; return 0; }
    done
    # /usr/bin/python3 is only a stub (it pops up an install dialog) unless the Xcode tools are installed.
    if /usr/bin/xcode-select -p >/dev/null 2>&1 && py_ok /usr/bin/python3; then
        echo /usr/bin/python3; return 0
    fi
    return 1
}

fetch_python() {
    local arch asset base tmp want got
    case "$(uname -m)" in arm64) arch=aarch64 ;; x86_64) arch=x86_64 ;; *) echo "Unsupported CPU $(uname -m)" >&2; return 1 ;; esac
    asset="cpython-${PBS_PY}+${PBS_TAG}-${arch}-apple-darwin-install_only.tar.gz"
    base="https://github.com/astral-sh/python-build-standalone/releases/download/${PBS_TAG}"
    echo "No Python 3 found. Downloading a portable Python (~25 MB) into $CACHE ..." >&2
    tmp="$(mktemp -d)"
    local bar=-sS; [[ -t 2 || -n "${DCTK_PROGRESS:-}" ]] && bar=--progress-bar
    if ! /usr/bin/curl -fL $bar -o "$tmp/py.tgz" "$base/$asset" \
       || ! /usr/bin/curl -fsSL -o "$tmp/SHA256SUMS" "$base/SHA256SUMS"; then
        rm -rf "$tmp"; echo "Download failed. Install Python 3 from python.org and try again." >&2; return 1
    fi
    want="$(/usr/bin/awk -v n="$asset" '$2 == n {print $1}' "$tmp/SHA256SUMS")"
    got="$(/usr/bin/shasum -a 256 "$tmp/py.tgz" | /usr/bin/awk '{print $1}')"
    if [[ -z "$want" || "$want" != "$got" ]]; then
        rm -rf "$tmp"; echo "Checksum mismatch for the Python download; not using it." >&2; return 1
    fi
    rm -rf "$CACHE/python"; mkdir -p "$CACHE"
    /usr/bin/tar -xzf "$tmp/py.tgz" -C "$CACHE" && rm -rf "$tmp"
    py_ok "$CACHE/python/bin/python3" || { echo "The downloaded Python does not run." >&2; return 1; }
    echo "$CACHE/python/bin/python3"
}

if [[ "${1:-}" == "--print-python" ]]; then
    find_python || fetch_python
    exit $?
fi

PY="$(find_python || fetch_python)" || finish 1

run() {   # run <isos|tools> [args...]
    local script="update_$1.py"; shift
    echo; echo "=== $( [[ $script == update_isos.py ]] && echo ISOs || echo Programs ) ==="
    # caffeinate keeps the Mac awake during long downloads.
    /usr/bin/caffeinate -i "$PY" -I "$UPD/$script" --root "$ROOT" "$@"
}

mode="${1:-}"; [[ $# -gt 0 ]] && shift
if [[ -z "$mode" ]]; then
    clear
    echo "DC Tool Kit updater (macOS)  -  drive: $ROOT"
    echo "Python: $PY"
    echo
    echo "  1) Update everything (ISOs + programs)"
    echo "  2) Update ISOs"
    echo "  3) Update programs"
    echo "  4) Check only - show what is out of date, download nothing"
    echo "  q) Quit"
    echo
    read -r -p "Choose [1-4, q]: " choice
    case "$choice" in
        1) mode=all ;; 2) mode=isos ;; 3) mode=tools ;;
        4) mode=all; set -- --list ;;
        *) exit 0 ;;
    esac
fi

rc=0
case "$mode" in
    all)   run isos "$@" || rc=1; run tools "$@" || rc=1 ;;
    isos)  run isos "$@" || rc=1 ;;
    tools) run tools "$@" || rc=1 ;;
    *) echo "Usage: $(basename "$0") [all|isos|tools] [--list] [--force] [entry ...]"; rc=2 ;;
esac
echo; echo "Versions are logged in system/Update/ver.log (shared with the Windows updater)."
finish $rc
