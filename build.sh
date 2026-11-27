#!/bin/bash
# Builds the self-extracting toolkit packages (macOS / Linux):
#   dist/DC-Tool-Kit-<version>-windows.exe     double-click on Windows
#   dist/DC-Tool-Kit-<version>-mac.command     double-click on a Mac
# Both extract to the stick you pick and hide programs/ and system/.
#
# Usage: ./build.sh [--version X] [--no-compile]
#   --no-compile  use the committed exes instead of recompiling (no mono needed)
# Compiling needs mono's C# compiler: brew install mono
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$PWD"
VERSION="$(date +%Y.%m.%d)"
COMPILE=1
while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --no-compile) COMPILE=0; shift ;;
        -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

SFX="src/SelfExtractor"
DIST="$ROOT/dist"
NAME="DC-Tool-Kit-$VERSION"
mkdir -p "$DIST"

if [[ $COMPILE -eq 1 ]]; then
    command -v csc >/dev/null || { echo "csc not found. Install mono (brew install mono) or use --no-compile." >&2; exit 1; }
    echo "== Compiling"
    src/Launchers/build.sh
    csc -nologo -target:winexe -platform:anycpu -optimize+ -out:UpdateToolkit.exe \
        -win32manifest:src/UpdateToolkit/app.manifest \
        -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
        -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll \
        src/UpdateToolkit/UpdateToolkit.cs
    echo "Built UpdateToolkit.exe"
    csc -nologo -target:winexe -platform:anycpu -optimize+ -out:"$SFX/SelfExtractor.exe" \
        -win32manifest:"$SFX/app.manifest" \
        -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.IO.Compression.dll \
        "$SFX/SelfExtractor.cs"
    echo "Built $SFX/SelfExtractor.exe"
fi
[[ -f "$SFX/SelfExtractor.exe" ]] || { echo "$SFX/SelfExtractor.exe missing; build without --no-compile." >&2; exit 1; }

echo "== Packing"
items=()
while IFS= read -r line; do
    line="${line%%#*}"; line="$(echo "$line" | sed 's/^ *//; s/ *$//')"
    [[ -z "$line" ]] && continue
    [[ -e "$line" ]] || { echo "Missing payload item: $line" >&2; exit 1; }
    items+=("$line")
done < "$SFX/payload.txt"

payload="$DIST/$NAME-payload.zip"
rm -f "$payload"
zip -r -q -9 -y "$payload" "${items[@]}" \
    -x '*.DS_Store' '*/._*' '._*' '*/__pycache__/*' '*.pyc' '*.iso' '*.part' '*.pdb' \
       '*/Thumbs.db' '*/desktop.ini' '*/.gitkeep' 'system/Update/.bin/*' '*/_geexlab_log.txt' 'key.txt'
echo "Payload: $(du -h "$payload" | cut -f1)  ($(unzip -Z1 "$payload" | wc -l | tr -d ' ') entries)"

# Windows: [stub exe][zip][int64 LE offset of the zip]["DCTKSFX1"]
win="$DIST/$NAME-windows.exe"
offset=$(stat -f%z "$SFX/SelfExtractor.exe" 2>/dev/null || stat -c%s "$SFX/SelfExtractor.exe")
{ cat "$SFX/SelfExtractor.exe" "$payload"; perl -e 'print pack("Q<", $ARGV[0]), "DCTKSFX1"' "$offset"; } > "$win"

# macOS: [bash header][zip], offsets fixed with zip -A so unzip reads it cleanly.
mac="$DIST/$NAME-mac.command"
cat "$SFX/mac-header.sh" "$payload" > "$mac"
zip -A -q "$mac" 2>/dev/null || true
chmod 755 "$mac"

rm -f "$payload"
echo "== Done"
ls -lh "$win" "$mac" | awk '{print $5"\t"$NF}'
