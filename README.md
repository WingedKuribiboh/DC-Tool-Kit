# IT Toolkit Drive

Ventoy multiboot stick. Boot it for installers/rescue ISOs; open it in Windows for tools and scripts.

## Building the installer packages
The repo is packed into two self-extracting installers in `dist\`. Each one asks for the target (the Ventoy stick root), extracts the toolkit and hides `programs\` and `system\`.

| Package | Run it on | How |
|---|---|---|
| `DC-Tool-Kit-<version>-windows.exe` | Windows | Double-click, pick the stick, click Extract. Silent: `DC-Tool-Kit-<version>-windows.exe /target:E:\ /silent` |
| `DC-Tool-Kit-<version>-mac.command` | macOS | Double-click, pick the stick. Or in Terminal: `bash DC-Tool-Kit-<version>-mac.command /Volumes/STICK` |

Build on a Mac (or Linux):
1. Install mono once for the C# compiler: `brew install mono`.
2. From the repo root run `./build.sh`. Options: `--version 1.2.0` (default: today's date), `--no-compile` (skip compiling and use the committed exes; mono not needed).

Build on Windows:
1. Double-click `build.bat`, or run `powershell -ExecutionPolicy Bypass -File build.ps1`. Options: `-Version 1.2.0`, `-NoCompile`. It uses the `csc.exe` that ships with Windows (.NET Framework 4.x); no SDK needed.

Both scripts do the same steps and both produce both packages:
1. Rebuild the `Tools\` launchers from `src\Launchers\launchers.txt`, `UpdateToolkit.exe` (repo root) and the Windows extractor stub `src\SelfExtractor\SelfExtractor.exe`.
2. Zip everything listed in `src\SelfExtractor\payload.txt` (`UpdateToolkit.exe`, `README.md`, `ISO`, `Tools`, `programs`, `system`, `ventoy`). ISOs, `.DS_Store`/`._*` files, `__pycache__`, `*.pdb`, logs and `key.txt` are left out; the empty `ISO\` folders are kept.
3. Windows package = `SelfExtractor.exe` + zip + a 16-byte trailer (zip offset + `DCTKSFX1`). Mac package = `src\SelfExtractor\mac-header.sh` + zip.

Notes:
- Re-extracting onto a stick that already has the toolkit overwrites the toolkit files but keeps `system\Update\ver.log` and anything the updater downloaded.
- Hidden folders: Windows sets the Hidden attribute; the Mac sets `chflags hidden`, which on a FAT/exFAT stick is the same Hidden attribute, so Windows hides them too. Show them with *View > Hidden items* (Windows) or Cmd+Shift+. (Finder).
- The packages are unsigned. Windows SmartScreen: *More info > Run anyway*. macOS: if the `.command` was downloaded, right-click > Open (or `xattr -d com.apple.quarantine <file>`); if it lost its execute bit, run it with `bash` as above.
- `dist\` is git-ignored. The Windows package is about the size of `programs\` (Macrium alone is ~180 MB).

## Getting the programs and ISOs
The ISOs and most portable programs are **not stored in this repo**. They are downloaded by the update tool:

- Run `UpdateToolkit.exe all` at the stick root (GUI), or the scripts directly: `system\Update\Update-Isos.cmd` for the ISOs and `system\Update\Update-Tools.cmd` for the programs.
- Run it after extracting a package (or copying the repo) to the stick, before using the `Tools\` launchers (a launcher shows "Program not found" until its program is in `programs\`).
- Exceptions that the updater does not download: DDU, FurMark, JRT, MiniTool Partition Wizard and rkill are kept in the repo; Macrium Reflect (`programs\reflect_setup_x64.exe`, over GitHub's 100 MB limit and git-ignored) must be added by hand before building.

## Layout
`programs\` and `system\` are hidden on the stick.

| Path | Contents |
|---|---|
| `UpdateToolkit.exe` | GUI updater for ISOs and programs (see below) |
| `ISO\1-Windows Install` | `Win11_x64.iso`, `Win11_arm.iso` (ARM64 = Snapdragon only) |
| `ISO\2-Linux Install` | `ubuntu.iso`, `Fedora.iso` (live/install) |
| `ISO\3-Repair` | Hiren's BootCD PE (`HBCD_PE_x64.iso`), Memtest86+, SystemRescue, ShredOS (secure wipe - destructive) |
| `Tools\` | Click-to-run launchers and tools (see below) |
| `programs\` (hidden) | The actual portable programs the launchers point to |
| `system\` (hidden) | `Update\` (ISO / tool updater scripts, portable Python, `ver.log`) and `LauncherMaker_v2.exe` |
| `ventoy\ventoy.json` | Boot menu names, tooltips, theme (`ventoy\theme\bsol`) |
| `Reports` | Output from scripts, one folder per machine |
| `key.txt` (drive root, created on demand) | BitLocker recovery key hand-off file; wiped after use |
| `src\` (repo only, not packaged) | Source and build scripts for the tools, plus dev-only tools (`ITToolkit`, `EFI-Breaker`) |
| `build.sh`, `build.ps1`, `build.bat` (repo only) | Package builders (see above) |

### Tools
Each launcher `.exe` in `Tools\` is a small stub that starts a program in `programs\` at a fixed path (it finds the toolkit root by walking up to the folder that contains `programs\`), so keep that layout intact.

| Folder | Contents |
|---|---|
| `Tools\` | **UefiRebuilder.exe** - rebuilds the EFI system partition / boot files (real tool, not a stub) |
| `Tools\BitLocker` | **BitLocker Unlock.exe** - GUI to unlock with a recovery password and decrypt (real tool). **BitLocker Key Entry.app** - macOS app that asks for the recovery password and writes `key.txt` to the stick |
| `Tools\Storage` | Hard Disk Sentinel, Macrium Reflect (installer), MiniTool Partition Wizard (installer), Rufus, Samsung Magician (installer), WinDirStat |
| `Tools\Malware Scans` | Adlice Protect (RogueKiller), AdwCleaner, Autoruns, Junkware Removal Tool, Malwarebytes (installer), Revo Uninstaller, rkill |
| `Tools\Stress Test` | FurMark, FurMark (GUI), Prime95 |
| `Tools\Utilities` | 7-Zip File Manager, Display Driver Uninstaller (run in Safe Mode), Everything, Notepad++, PuTTY |

`programs\` also holds the full Sysinternals Suite (no launcher; run from there).

## Source (`src\`)
- **Launchers** - `Launcher.cs` + `launchers.txt` (launcher name | target under `programs\`). To add or change a launcher, edit `launchers.txt` and run `src/Launchers/build.sh` (Mac, mono) or `src\Launchers\build.bat` (Windows); `build.sh`/`build.ps1` also do this. Replaces `system\LauncherMaker_v2.exe`, which is kept for reference.
- **SelfExtractor** - Windows extractor stub (`SelfExtractor.cs`), Mac extractor header (`mac-header.sh`) and the package list (`payload.txt`).
- **UpdateToolkit** (`UpdateToolkit.cs`; `build.bat` writes `UpdateToolkit.exe` to the repo root) - GUI with progress bars for the two updaters in `system\Update`. Args: `all|isos|tools` to start immediately, `/check` (download nothing), `/force`, `/root:X:\`.
- **BitLockerTool** - source for `Tools\BitLocker\BitLocker Unlock.exe` (its `build.bat` writes there) and the script version `Unlock-And-Disable.ps1`.
- **BitLockerKeyEntry** - `launcher.c` + `build.sh` (Mac with Xcode command line tools): rebuilds the main executable of `Tools/BitLocker/BitLocker Key Entry.app` and ad-hoc signs it. The app's logic is `Contents/Resources/main.sh` inside the bundle.
- **UefiRebuilder** - SDK-style project (`dotnet build -c Release src/UefiRebuilder`, works on Windows or Mac); copy the built exe to `Tools\UefiRebuilder.exe`.
- **ITToolkit** (`ITToolkit\ITToolkit.exe`, `build.bat`) - DISM / SFC / CHKDSK repair, disk health, network diagnostics, UEFI/boot tools and system info. Not in the packages; copy it to the stick by hand if needed. Command line: `/target`, `/source` (e.g. `wim:E:\sources\install.wim:1`), `/scratch`, `/run:<id>[,<id>]`, `/log`, `/exit`, `/list`, `/help`.
- **EFI-Breaker** - deliberately breaks UEFI boot on a test machine/VM to practise repairs. Dev/test only; not in the packages.

`build.bat` files use the `csc.exe` that ships with Windows (.NET Framework 4.x, no SDK).

## Use
- Boot: pick the stick in the BIOS/UEFI boot menu; ISOs are listed by folder.
- Adding an ISO: drop it in the right `ISO\` subfolder, then add an `alias` and `tip` entry for it in `ventoy\ventoy.json`.
- Keep ISOs current: `system\Update\Update-Isos.cmd` (`--list` to only check, or name one entry, e.g. `ubuntu`). Covers Windows 11, Ubuntu, Fedora, Hiren's, ShredOS, Memtest86+, SystemRescue; keeps the filenames above. Uses an installed Python (3.8+) or the portable one in `system\Update\python`.
- Keep portable programs current: `system\Update\Update-Tools.cmd` (same options). Updates 7-Zip, WinDirStat, Revo, HD Sentinel, Adlice, Malwarebytes, Notepad++, AdwCleaner, Sysinternals, Everything, PuTTY, Prime95, Rufus, Samsung Magician in place. DDU, FurMark, JRT, MiniTool, Macrium and rkill are not auto-updated.
- Versions are tracked in `system\Update\ver.log` (`[ISO]`, `[Tools]`, `[Skipped]`).
- BitLocker: on a Mac, plug in the stick and open `Tools/BitLocker/BitLocker Key Entry.app`. It asks for the 48-digit recovery password (dashes optional) and checks it, then opens a Save dialog already pointed at the drive root with `key.txt` filled in - click Save. It then offers to eject the stick. (The Save dialog is needed because macOS privacy rules block an unsigned app on a USB drive from writing to that drive directly, even with Full Disk Access; files picked in a Save dialog are allowed.) On Windows, paste the password into `key.txt` at the drive root (e.g. `E:\key.txt`) by hand instead. Then run `Tools\BitLocker\BitLocker Unlock.exe` on the locked machine. `key.txt` is overwritten and emptied right after the unlock attempt.
  - The app is unsigned (ad-hoc). Run from the stick it opens normally; if it was downloaded (e.g. a GitHub zip) macOS blocks it - right-click > Open, or run `xattr -dr com.apple.quarantine "BitLocker Key Entry.app"`. The Mac package clears this flag for you.

## Rules
- No customer data, passwords or BitLocker keys on this drive. If you must copy something off a customer machine, delete it afterward.
- ShredOS erases entire disks. Verify the target.
- `Tools\` launchers depend on exact paths in `programs\`; the updaters preserve them, so don't rename folders there.

## Housekeeping
- `.DS_Store`, `*.pdb`, `dist\` and FurMark's `_geexlab_log.txt` are git-ignored; delete them if they show up.
- macOS leaves `._*` files and `.Spotlight-V100`, `.fseventsd`, `.Trashes` on the stick. Ventoy ignores `._*` (`VTOY_FILT_DOT_UNDERSCORE_FILE`); they are safe to delete.
- `src\UpdateToolkit\error.txt` is a stale log from a bad `/root` argument and can be deleted.
