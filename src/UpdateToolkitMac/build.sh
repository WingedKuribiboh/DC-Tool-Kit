#!/bin/bash
# Builds UpdateToolkit.app (the windowed Mac updater): universal (Apple silicon + Intel), macOS 11+, ad-hoc
# signed, with the toolkit icon. Needs the Xcode command line tools (xcode-select --install).
# Usage: build.sh [output.app]   (default: dist/build/UpdateToolkit.app; the top-level build.sh packs it)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
app="${1:-$repo/dist/build/UpdateToolkit.app}"
mkdir -p "$(dirname "$app")"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
for arch in arm64 x86_64; do
    /usr/bin/xcrun swiftc -O -swift-version 5 -target "$arch-apple-macos11.0" \
        -o "$tmp/UpdateToolkit-$arch" "$here/main.swift"
done
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
/usr/bin/lipo -create -output "$app/Contents/MacOS/UpdateToolkit" "$tmp"/UpdateToolkit-*
cp "$here/Info.plist" "$app/Contents/Info.plist"
cp "$repo/src/Icon/DCToolKit.icns" "$app/Contents/Resources/AppIcon.icns"
/usr/bin/find "$app" -name '._*' -delete
/usr/bin/codesign --force --sign - "$app"
/usr/bin/codesign --verify --strict "$app"
echo "Built $app"
