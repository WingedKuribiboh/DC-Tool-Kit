#!/bin/bash
# DC Tool Kit self-extractor (macOS). build.sh / build.ps1 append the toolkit zip below this script.
# Double-click it in Finder, or run:  bash DC-Tool-Kit-mac.command [/Volumes/STICK]
# It extracts everything to the chosen folder, then hides programs/ and system/
# (chflags hidden sets the FAT/exFAT hidden attribute, so Windows hides them too).
set -uo pipefail
SELF="$0"
TITLE="DC Tool Kit Installer"
HIDDEN_DIRS=(programs system)
KEEP_IF_EXISTS=(system/Update/ver.log)

say() { echo "$*"; }
fail() {
    echo "ERROR: $*" >&2
    /usr/bin/osascript -e "display alert \"$TITLE\" message \"$*\" as critical" >/dev/null 2>&1
    exit 1
}

dest="${1:-}"
if [[ -z "$dest" ]]; then
    # First mounted external volume as the default, else /Volumes.
    default="/Volumes"
    for v in /Volumes/*; do
        [[ -d "$v" && "$v" != "/Volumes/Macintosh HD"* && ! -L "$v" ]] && { default="$v"; break; }
    done
    dest=$(/usr/bin/osascript <<OSA 2>/dev/null
POSIX path of (choose folder with prompt "Extract the DC Tool Kit to (pick the root of the Ventoy USB stick):" default location (POSIX file "$default"))
OSA
) || { say "Cancelled."; exit 0; }
    /usr/bin/osascript -e "display dialog \"Extract the toolkit to $dest ?\" & return & return & \"Existing toolkit files there are overwritten.\" buttons {\"Cancel\", \"Extract\"} default button \"Extract\" with title \"$TITLE\"" >/dev/null 2>&1 \
        || { say "Cancelled."; exit 0; }
fi
dest="${dest%/}"
[[ -n "$dest" ]] || dest="/"
mkdir -p "$dest" || fail "Cannot create $dest"

excludes=()
for k in "${KEEP_IF_EXISTS[@]}"; do [[ -e "$dest/$k" ]] && excludes+=(-x "$k"); done

say "Extracting the DC Tool Kit to $dest ..."
# The zip starts after this script; unzip finds it and warns about the leading bytes (exit code 1).
/usr/bin/unzip -o -q "$SELF" -d "$dest" ${excludes[@]+"${excludes[@]}"}
rc=$?
[[ $rc -le 1 ]] || fail "unzip failed (exit code $rc). Is there enough free space on $dest?"

# Zips made on Windows carry no Unix permissions: make the Mac app runnable again.
app="$dest/Tools/BitLocker/BitLocker Key Entry.app"
if [[ -d "$app" ]]; then
    chmod 755 "$app/Contents/MacOS/"* "$app/Contents/Resources/"*.sh 2>/dev/null
    /usr/bin/xattr -dr com.apple.quarantine "$app" 2>/dev/null
fi
chmod +x "$dest"/system/Update/*.py 2>/dev/null

for d in "${HIDDEN_DIRS[@]}"; do
    [[ -d "$dest/$d" ]] && /usr/bin/chflags hidden "$dest/$d"
done

say "Done. programs/ and system/ are hidden (Cmd+Shift+. shows them in Finder)."
say "On Windows, run UpdateToolkit.exe at the stick root to download the ISOs and programs."
/usr/bin/osascript -e "display notification \"Extracted to $dest\" with title \"$TITLE\"" >/dev/null 2>&1
[[ -n "${1:-}" ]] || /usr/bin/open "$dest"
exit 0
# The payload zip follows this line. Do not add anything below it.
