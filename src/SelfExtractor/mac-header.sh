#!/bin/bash
# DC Tool Kit self-extractor (macOS). build.sh / build.ps1 append the toolkit zip below this script.
# Double-click it in Finder, or run:  bash DC-Tool-Kit-mac.command [/Volumes/STICK] [--nolabel]
# It removes an older toolkit there (keeping ISO/, programs/ and ver.log), extracts everything, then hides programs/, system/, ventoy/ and the icon files
# (chflags hidden sets the FAT/exFAT hidden attribute, so Windows hides them too). When the target is a
# mounted volume it also gets the toolkit icon and is renamed to label= from the packaged autorun.inf.
set -uo pipefail
SELF="$0"
TITLE="DC Tool Kit Installer"
HIDDEN_DIRS=(programs system ventoy)
HIDDEN_FILES=(autorun.inf DCToolKit.ico .VolumeIcon.icns)
RENAME=1
args=()
for a in "$@"; do [[ "$a" == "--nolabel" ]] && RENAME=0 || args+=("$a"); done
set -- ${args[@]+"${args[@]}"}
KEEP_IF_EXISTS=(system/Update/ver.log)
# Old-version cleanup: everything the toolkit has ever put at the drive root, in any layout. Deleted before
# extracting so nothing stale is left; ISO/ and programs/ are never touched and unknown files are never deleted.
OLD_ITEMS=(Tools system ventoy src UpdateToolkit.exe UpdateToolkit.app UpdateToolkit.command ITToolkit.exe
           "BitLocker Key Entry.app" README.md autorun.inf DCToolKit.ico .VolumeIcon.icns
           build.sh build.ps1 build.bat .gitignore .gitattributes)
# Where older layouts kept ver.log (newest first). It records the installed ISO / program versions, so it is
# carried over; without it the updater would download everything again.
VER_LOGS=(system/Update/ver.log src/UpdateToolkit/Update/ver.log src/Scripts/Update/ver.log)
# Markers of an existing toolkit; cleanup only runs when one is present.
MARKERS=(system/Update/update_isos.py src/UpdateToolkit/Update/update_isos.py src/Scripts/Update/update_isos.py
         UpdateToolkit.exe ITToolkit.exe)

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
    /usr/bin/osascript -e "display dialog \"Extract the toolkit to $dest ?\" & return & return & \"An older toolkit there is removed first. Only the ISO and programs folders (and the update log) are kept.\" buttons {\"Cancel\", \"Extract\"} default button \"Extract\" with title \"$TITLE\"" >/dev/null 2>&1 \
        || { say "Cancelled."; exit 0; }
fi
dest="${dest%/}"
[[ -n "$dest" ]] || dest="/"
mkdir -p "$dest" || fail "Cannot create $dest"

clean_old_version() {
    local m found=0 v saved="" item n=0
    for m in "${MARKERS[@]}"; do [[ -e "$dest/$m" ]] && { found=1; break; }; done
    [[ $found -eq 1 ]] || return 0
    [[ -e "$dest/.git" ]] && { say "Skipped cleanup: target is a git checkout."; return 0; }
    for v in "${VER_LOGS[@]}"; do
        if [[ -f "$dest/$v" ]]; then saved="$(mktemp)"; cp "$dest/$v" "$saved"; break; fi
    done
    # Very old layout: programs lived in src/Programs. Move them up instead of deleting them.
    if [[ -d "$dest/src/Programs" && ! -d "$dest/programs" ]]; then mv "$dest/src/Programs" "$dest/programs"; fi
    for item in "${OLD_ITEMS[@]}"; do
        if [[ -e "$dest/$item" || -L "$dest/$item" ]]; then
            /usr/bin/chflags -R nouchg,nohidden "$dest/$item" 2>/dev/null
            rm -rf "$dest/$item" || fail "Could not remove the old $item from $dest"
            n=$((n + 1))
        fi
    done
    if [[ -n "$saved" ]]; then mkdir -p "$dest/system/Update"; mv "$saved" "$dest/system/Update/ver.log"; fi
    [[ $n -gt 0 ]] && say "Removed the old toolkit version ($n items); kept ISO, programs and ver.log."
    return 0
}
clean_old_version

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
chmod +x "$dest"/system/Update/*.py "$dest"/system/Update/*.command 2>/dev/null
/usr/bin/xattr -d com.apple.quarantine "$dest/system/Update/UpdateToolkit.command" 2>/dev/null
chmod 755 "$dest/UpdateToolkit.app/Contents/MacOS/"* 2>/dev/null
/usr/bin/xattr -dr com.apple.quarantine "$dest/UpdateToolkit.app" 2>/dev/null

for d in "${HIDDEN_DIRS[@]}"; do
    [[ -d "$dest/$d" ]] && /usr/bin/chflags hidden "$dest/$d"
done
for f in "${HIDDEN_FILES[@]}"; do
    [[ -f "$dest/$f" ]] && /usr/bin/chflags hidden "$dest/$f"
done

# On FAT/exFAT macOS stores extended attributes in ._* files, which Windows shows. Remove them (the FAT
# hidden attribute is stored natively, so nothing is lost); ._. is kept below for the volume icon.
/usr/bin/find "$dest" -name '._*' -type f -delete 2>/dev/null

# Volume icon + name, only when the target is the root of a mounted volume.
mount_point=$(/usr/sbin/diskutil info "$dest" 2>/dev/null | /usr/bin/awk -F': *' '/Mount Point/ {print $2}')
if [[ -n "$mount_point" && "$mount_point" == "$dest" && "$dest" != "/" ]]; then
    if [[ -f "$dest/.VolumeIcon.icns" ]]; then
        # Custom-icon flag on the volume root (what SetFile -a C does; xattr needs no Xcode tools).
        /usr/bin/xattr -wx com.apple.FinderInfo \
            "0000000000000000040000000000000000000000000000000000000000000000" "$dest" 2>/dev/null \
            || say "Note: could not set the volume icon."
    fi
    label=$(/usr/bin/tr -d '\r' < "$dest/autorun.inf" 2>/dev/null | /usr/bin/awk -F= 'tolower($1)=="label" {print $2; exit}')
    if [[ $RENAME -eq 1 && -n "$label" ]]; then
        fs=$(/usr/sbin/diskutil info "$dest" | /usr/bin/awk -F': *' '/Type \(Bundle\)/ {print $2}')
        [[ "$fs" == msdos* ]] && label="${label:0:11}" || label="${label:0:15}"   # FAT32 11 chars, exFAT 15
        if [[ "$(basename "$dest")" != "$label" ]]; then
            if /usr/sbin/diskutil rename "$dest" "$label" >/dev/null 2>&1; then
                say "Drive renamed to $label."
                dest="/Volumes/$label"
            else
                say "Note: could not rename the drive to $label."
            fi
        fi
    fi
fi

say "Done. programs/, system/ and ventoy/ are hidden (Cmd+Shift+. shows them in Finder)."
say "To download the ISOs and programs, run UpdateToolkit.app (Mac) or UpdateToolkit.exe (Windows) at the stick root."
/usr/bin/osascript -e "display notification \"Extracted to $dest\" with title \"$TITLE\"" >/dev/null 2>&1
[[ -n "${1:-}" ]] || /usr/bin/open "$dest"
exit 0
# The payload zip follows this line. Do not add anything below it.
