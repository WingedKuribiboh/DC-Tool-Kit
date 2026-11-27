#!/bin/bash
# Builds Tools/NetUserManager.exe (macOS/Linux; needs mono: brew install mono). The top-level build.sh runs this.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$here/../../Tools/NetUserManager.exe"
csc -nologo -target:winexe -platform:anycpu -optimize+ -out:"$out" \
    -win32manifest:"$here/app.manifest" \
    -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
    "$here/NetUserManager.cs"
echo "Built Tools/NetUserManager.exe (from src/net-user-gui)"
