#!/bin/bash
# BitLocker Key Entry (macOS) - asks for a 48-digit BitLocker recovery password and writes it to
# key.txt at the root of the toolkit stick this app runs from. "BitLocker Unlock.exe" (Windows)
# reads key.txt on the locked machine and wipes it after the unlock step.
#
# Plain bash + osascript, run by the small compiled launcher in Contents/MacOS (see
# src/BitLockerKeyEntry: a script as the main executable gets blocked by macOS privacy controls).
# Source of truth is this file; keep it LF line endings (see .gitattributes).

TITLE="BitLocker Key Entry"

# Run from a temp copy so nothing on the stick stays open (lets us eject it at the end).
if [ -z "$BLKE_COPY" ]; then
    case "$0" in /*) self="$0" ;; *) self="$PWD/$0" ;; esac
    tmp=$(/usr/bin/mktemp -t blke) && /bin/cp "$self" "$tmp" && /bin/chmod 700 "$tmp" &&
        BLKE_COPY="$tmp" BLKE_SELF="$self" exec /bin/bash "$tmp" "$@"
    BLKE_SELF="$self"   # copy failed: carry on in place
fi
[ -n "$BLKE_COPY" ] && trap '/bin/rm -f "$BLKE_COPY"' EXIT
cd / || exit 1

# ---- dialogs ----

# msg ICON TEXT BUTTON...  -> prints the button clicked; non-zero exit if closed/cancelled.
msg() {
    local icon="$1"; shift
    /usr/bin/osascript \
        -e 'on run argv' \
        -e 'activate' \
        -e 'set b to items 4 thru -1 of argv' \
        -e 'if item 3 of argv is "stop" then' \
        -e 'return button returned of (display dialog (item 1 of argv) with title (item 2 of argv) buttons b default button (last item of b) with icon stop)' \
        -e 'else if item 3 of argv is "caution" then' \
        -e 'return button returned of (display dialog (item 1 of argv) with title (item 2 of argv) buttons b default button (last item of b) with icon caution)' \
        -e 'else' \
        -e 'return button returned of (display dialog (item 1 of argv) with title (item 2 of argv) buttons b default button (last item of b) with icon note)' \
        -e 'end if' \
        -e 'end run' \
        "$1" "$TITLE" "$icon" "${@:2}" 2>/dev/null
}

# ask_key PROMPT  (previous input comes in via BLKE_DEFAULT, not argv, so it stays out of ps)
ask_key() {
    /usr/bin/osascript \
        -e 'on run argv' \
        -e 'activate' \
        -e 'set d to system attribute "BLKE_DEFAULT"' \
        -e 'return text returned of (display dialog (item 1 of argv) default answer d with title (item 2 of argv) buttons {"Cancel", "Save"} default button "Save" cancel button "Cancel" with icon note)' \
        -e 'end run' \
        "$1" "$TITLE" 2>/dev/null
}

choose_drive() {
    /usr/bin/osascript \
        -e 'activate' \
        -e 'POSIX path of (choose folder with prompt "Select the toolkit USB drive:" default location (POSIX file "/Volumes"))' 2>/dev/null
}

# ---- find the stick ----

# /Volumes/NAME/anything -> /Volumes/NAME
vol_root() {
    case "$1" in
        /Volumes/?*) local rest="${1#/Volumes/}"; echo "/Volumes/${rest%%/*}" ;;
        *) return 1 ;;
    esac
}

root=$(vol_root "$BLKE_SELF")
while [ -z "$root" ] || [ ! -d "$root" ]; do
    c=$(msg caution "This app is not running from a USB drive. Select the toolkit drive to write key.txt to." "Quit" "Select Drive") || exit 0
    [ "$c" = "Select Drive" ] || exit 0
    picked=$(choose_drive) || exit 0
    root=$(vol_root "$picked")
    [ -z "$root" ] && { msg stop "Pick a drive under /Volumes (the USB stick), not a folder on this Mac." "OK" >/dev/null || exit 0; }
done
volname="${root#/Volumes/}"
keyfile="$root/key.txt"

# Turns a failed write's error text into advice. Don't trust [ -w ]: macOS privacy controls
# (Removable Volumes) can make access() say no even though the drive is writable, or vice versa.
write_help() {
    case "$1" in
        *"Operation not permitted"*)
            echo "macOS is blocking this app from the drive. Open System Settings > Privacy & Security > Files and Folders, find BitLocker Key Entry and turn on Removable Volumes (or add it under Full Disk Access), then open it again." ;;
        *"Read-only file system"*)
            echo "macOS mounted the drive read-only. Run First Aid on it in Disk Utility, or eject and re-plug it. If it is NTFS, macOS can only read it." ;;
        *"Permission denied"*)
            echo "No permission to write there. If key.txt is marked read-only, clear that on Windows (Properties) or delete key.txt and try again." ;;
        *) echo "Check the drive is plugged in and not full." ;;
    esac
}

# Prove we can write before asking for 48 digits.
probe="$root/.blke-write-test"
if ! werr=$( { : > "$probe"; } 2>&1 ); then
    msg stop "Can't write to \"$volname\".

macOS said: ${werr##*: }

$(write_help "$werr")" "Quit" >/dev/null
    exit 1
fi
/bin/rm -f "$probe" "$root/._.blke-write-test" 2>/dev/null

if [ -s "$keyfile" ]; then
    c=$(msg caution "key.txt on \"$volname\" already has something in it. Replace it?" "Quit" "Replace") || exit 0
    [ "$c" = "Replace" ] || exit 0
fi

# ---- get and check the key ----

# Same rules as BitLocker Unlock.exe: 8 groups of 6 digits, each divisible by 11 and below 720896.
# Sets ERR (empty if good) and KEY (the dashed 48-digit form, empty if bad).
check_key() {
    local digits i g n
    KEY=""; ERR=""
    digits=$(printf '%s' "$1" | /usr/bin/tr -cd '0-9')
    if [ ${#digits} -eq 0 ]; then ERR="Nothing entered."; return; fi
    if [ ${#digits} -ne 48 ]; then ERR="That has ${#digits} digits. A recovery password has 48 (8 groups of 6)."; return; fi
    for i in 0 1 2 3 4 5 6 7; do
        g="${digits:$((i * 6)):6}"
        n=$((10#$g))
        if [ $((n % 11)) -ne 0 ] || [ "$n" -ge 720896 ]; then
            ERR="Group $((i + 1)) ($g) is not valid. Check for a typo."
            KEY=""
            return
        fi
        KEY="${KEY}${KEY:+-}$g"
    done
}

prompt="Enter the 48-digit BitLocker recovery password.

It will be saved to key.txt on \"$volname\". Dashes and spaces are optional."
BLKE_DEFAULT=""
export BLKE_DEFAULT
while :; do
    raw=$(ask_key "$prompt") || exit 0
    check_key "$raw"
    [ -z "$ERR" ] && break
    BLKE_DEFAULT="$raw"
    prompt="$ERR

Enter the 48-digit BitLocker recovery password:"
done
BLKE_DEFAULT=""
raw=""

# ---- write it ----

umask 077
# A Windows read-only attribute shows up on macOS as the locked (uchg) flag; clear it.
[ -e "$keyfile" ] && /usr/bin/chflags nouchg "$keyfile" 2>/dev/null
if ! werr=$( { printf '%s\r\n' "$KEY" > "$keyfile"; } 2>&1 ); then
    KEY=""
    msg stop "Could not write key.txt to \"$volname\".

macOS said: ${werr##*: }

$(write_help "$werr")" "Quit" >/dev/null
    exit 1
fi
/bin/sync
/bin/rm -f "$root/._key.txt" 2>/dev/null

saved=$(/usr/bin/tr -d '\r\n' < "$keyfile" 2>/dev/null)
tail6="${KEY: -6}"
if [ "$saved" != "$KEY" ]; then
    KEY=""; saved=""
    msg stop "key.txt was written but reads back differently. Check the stick and try again." "Quit" >/dev/null
    exit 1
fi
KEY=""; saved=""

choice=$(msg note "Saved key.txt to \"$volname\" (ends in $tail6).

Plug the stick into the locked PC and run Tools/BitLocker/BitLocker Unlock.exe. It wipes key.txt after unlocking." "Done" "Eject Drive") || exit 0

if [ "$choice" = "Eject Drive" ]; then
    if out=$(/usr/sbin/diskutil eject "$root" 2>&1); then
        msg note "\"$volname\" ejected. You can unplug it." "OK" >/dev/null
    else
        msg caution "Could not eject \"$volname\":

$out

Close any windows using the drive and eject it from Finder." "OK" >/dev/null
    fi
fi
exit 0
