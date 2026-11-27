#!/bin/bash
# Rebuilds every Tools/ launcher listed in launchers.txt (macOS/Linux; needs mono: brew install mono).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tools="$here/../../Tools"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
n=0
while IFS='|' read -r name target; do
    name="$(echo "$name" | sed 's/^ *//; s/ *$//')"; target="$(echo "$target" | sed 's/^ *//; s/ *$//')"
    [[ -z "$name" || "$name" == \#* ]] && continue
    out="$tools/${name//\\//}"
    mkdir -p "$(dirname "$out")"
    esc="${target//\\/\\\\}"; esc="${esc//&/\\&}"
    sed "s|@TARGET@|$esc|" "$here/Launcher.cs" > "$tmp/Launcher.cs"
    csc -nologo -target:winexe -platform:anycpu -optimize+ -out:"$out" \
        -r:System.Windows.Forms.dll "$tmp/Launcher.cs" >/dev/null
    echo "  ${name}  ->  ${target}"; n=$((n+1))
done < "$here/launchers.txt"
echo "Built $n launchers in Tools/"
