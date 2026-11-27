#!/bin/bash
# Rebuilds every Tools/ launcher listed in launchers.txt (macOS/Linux; needs mono: brew install mono).
# Each launcher gets the icon named in the third column (from src/Icons), or the toolkit icon if blank.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tools="$here/../../Tools"
icons="$here/../Icons"
default_icon="$here/../Icon/DCToolKit.ico"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
trim() { echo "$1" | sed 's/^ *//; s/ *$//'; }
n=0
while IFS='|' read -r name target icon; do
    name="$(trim "$name")"; target="$(trim "$target")"; icon="$(trim "${icon:-}")"
    [[ -z "$name" || "$name" == \#* ]] && continue
    out="$tools/${name//\\//}"
    if [[ -n "$icon" ]]; then
        ico="$icons/$icon"
        [[ -f "$ico" ]] || { echo "Icon not found for $name: src/Icons/$icon" >&2; exit 1; }
    else
        ico="$default_icon"
    fi
    mkdir -p "$(dirname "$out")"
    esc="${target//\\/\\\\}"; esc="${esc//&/\\&}"
    sed "s|@TARGET@|$esc|" "$here/Launcher.cs" > "$tmp/Launcher.cs"
    csc -nologo -target:winexe -platform:anycpu -optimize+ -out:"$out" -win32icon:"$ico" \
        -r:System.Windows.Forms.dll "$tmp/Launcher.cs" >/dev/null
    echo "  ${name}  ->  ${target}  [${icon:-DCToolKit.ico}]"; n=$((n+1))
done < "$here/launchers.txt"
echo "Built $n launchers in Tools/"
