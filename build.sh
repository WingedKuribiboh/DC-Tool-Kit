#!/bin/bash
# Builds the self-extracting toolkit packages (macOS / Linux):
#   dist/DC-Tool-Kit-<version>-windows.exe     double-click on Windows
#   dist/DC-Tool-Kit-<version>-mac.command     double-click on a Mac
# Both extract to the stick you pick, hide programs/, system/ and ventoy/, rename the drive and give it
# the toolkit icon (src/Icon/DCToolKit.ico for Windows, DCToolKit.icns for macOS).
#
# Usage: ./build.sh [--version X] [--label NAME] [--no-compile]
#   --label       drive name set by the installers (default DC-TOOLKIT; FAT32 max 11 chars, exFAT 15)
#   --no-compile  reuse the committed launchers and extractor stub instead of rebuilding them
# Needs mono's C# compiler (brew install mono) and, on a Mac, the Xcode command line tools (xcode-select
# --install): UpdateToolkit.exe and UpdateToolkit.app are not in the repo. They are always built from
# src/UpdateToolkit and src/UpdateToolkitMac into dist/build, then packed at the stick root.
set -euo pipefail
cd "$(dirname "$0")"
ROOT="$PWD"
VERSION="$(date +%Y.%m.%d)"
COMPILE=1
LABEL="DC-TOOLKIT"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --label) LABEL="$2"; shift 2 ;;
        --no-compile) COMPILE=0; shift ;;
        -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done


SFX="src/SelfExtractor"
DIST="$ROOT/dist"
NAME="DC-Tool-Kit-$VERSION"
BUILD="$DIST/build"
rm -rf "$BUILD"; mkdir -p "$BUILD"

command -v csc >/dev/null || { echo "csc not found. Install mono: brew install mono" >&2; exit 1; }
echo "== Compiling"
csc -nologo -target:winexe -platform:anycpu -optimize+ -out:"$BUILD/UpdateToolkit.exe" \
    -win32manifest:src/UpdateToolkit/app.manifest -win32icon:src/Icon/DCToolKit.ico \
    -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
    -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll \
    src/UpdateToolkit/UpdateToolkit.cs
echo "Built dist/build/UpdateToolkit.exe (from src/UpdateToolkit/UpdateToolkit.cs)"
if xcrun --find swiftc >/dev/null 2>&1; then
    src/UpdateToolkitMac/build.sh "$BUILD/UpdateToolkit.app" | sed 's|^Built .*|Built dist/build/UpdateToolkit.app (from src/UpdateToolkitMac)|'
elif [[ "$(uname)" == Darwin ]]; then
    echo "swiftc not found: install the Xcode command line tools (xcode-select --install)." >&2; exit 1
else
    echo "WARNING: not on a Mac - packing without UpdateToolkit.app (Mac users get system/Update/UpdateToolkit.command)." >&2
fi
if [[ $COMPILE -eq 1 ]]; then
    src/Launchers/build.sh
    src/net-user-gui/build.sh
    if [[ "$(uname)" == Darwin ]]; then
        src/BitLockerKeyEntry/build.sh >/dev/null && echo "Built Tools/BitLocker/BitLocker Key Entry.app (from src/BitLockerKeyEntry)"
    fi
    csc -nologo -target:winexe -platform:anycpu -optimize+ -out:"$SFX/SelfExtractor.exe" \
        -win32manifest:"$SFX/app.manifest" -win32icon:src/Icon/DCToolKit.ico \
        -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.IO.Compression.dll \
        "$SFX/SelfExtractor.cs"
    echo "Built $SFX/SelfExtractor.exe"
fi
# mono's compiler can bind to string overloads that exist in mono/.NET Core but not in .NET Framework
# (e.g. Split(char), Trim(char)); on Windows they crash with MissingMethodException. Use char[] instead.
if command -v monodis >/dev/null; then
    bad=$(for f in "$BUILD/UpdateToolkit.exe" "$SFX/SelfExtractor.exe" Tools/*.exe Tools/*/*.exe; do
              monodis "$f" 2>/dev/null | grep -E -o 'string::(Split|Trim|TrimStart|TrimEnd|Contains|StartsWith|EndsWith|Join)\(char[,)][^)]*\)' | sed "s|^|$f: |" || true
          done | sort -u)
    [[ -z "$bad" ]] || { echo "Uses APIs missing from .NET Framework (pass a char[] instead):" >&2; echo "$bad" >&2; exit 1; }
fi
[[ -f "$SFX/SelfExtractor.exe" ]] || { echo "$SFX/SelfExtractor.exe missing; build without --no-compile." >&2; exit 1; }

# Mac apps must have intact executables and signatures, or macOS refuses to open them.
if [[ "$(uname)" == Darwin ]]; then
    for app in "Tools/BitLocker/BitLocker Key Entry.app" "$BUILD/UpdateToolkit.app"; do
        [[ -d "$app" ]] || continue
        /usr/bin/codesign --verify --strict "$app" 2>/dev/null \
            || { echo "$app is damaged or unsigned (codesign --verify failed). Rebuild it on a Mac." >&2; exit 1; }
    done
fi

# Refresh the CA bundle the updater uses for HTTPS (system/Update/cacert.pem); keep the old one if offline.
ca="system/Update/cacert.pem"
if curl -fsSL --max-time 30 -o "$ca.new" https://curl.se/ca/cacert.pem && grep -q "BEGIN CERTIFICATE" "$ca.new"; then
    mv "$ca.new" "$ca"; echo "Refreshed $ca"
else
    rm -f "$ca.new"; echo "Could not refresh $ca; packing the existing one."
fi

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
# Drive-root files: autorun.inf (Windows drive name + icon), DCToolKit.ico, .VolumeIcon.icns (Mac icon).
droot="$DIST/driveroot"
rm -rf "$droot"; mkdir -p "$droot"
cp src/Icon/DCToolKit.ico "$droot/DCToolKit.ico"
cp src/Icon/DCToolKit.icns "$droot/.VolumeIcon.icns"
printf '[autorun]\r\nicon=DCToolKit.ico\r\nlabel=%s\r\n' "$LABEL" > "$droot/autorun.inf"
(cd "$droot" && zip -q -9 "$payload" autorun.inf DCToolKit.ico .VolumeIcon.icns)
# Built above, never taken from the repo.
(cd "$BUILD" && zip -q -9 "$payload" UpdateToolkit.exe)
if [[ -d "$BUILD/UpdateToolkit.app" ]]; then
    (cd "$BUILD" && zip -r -q -9 -y "$payload" UpdateToolkit.app -x '*/._*' '*.DS_Store')
fi
rm -rf "$droot"
echo "Drive name: $LABEL"
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

rm -f "$payload"; rm -rf "$BUILD"
echo "== Done"
ls -lh "$win" "$mac" | awk '{print $5"\t"$NF}'
