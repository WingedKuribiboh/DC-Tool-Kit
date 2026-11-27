#!/bin/bash
# Rebuilds the launcher of "BitLocker Key Entry.app" at the stick root and ad-hoc signs the bundle.
# Edit the app's logic in Contents/Resources/main.sh, then run this (on a Mac with Xcode CLT).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
app="$here/../../BitLocker Key Entry.app"
exe="$app/Contents/MacOS/BitLocker Key Entry"
/usr/bin/xcrun clang -O2 -arch arm64 -arch x86_64 -mmacosx-version-min=10.13 \
    -o "$exe" "$here/launcher.c"
/bin/chmod 755 "$exe" "$app/Contents/Resources/main.sh"
# exFAT keeps xattrs in ._ files; codesign treats them as unsealed content, so drop them.
/usr/bin/find "$app" -name '._*' -delete
/usr/bin/codesign --force --sign - "$app"
/usr/bin/find "$app" -name '._*' -delete
/usr/bin/codesign --verify --strict --verbose "$app"
