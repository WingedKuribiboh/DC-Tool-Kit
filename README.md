# IT Toolkit Drive

Ventoy multiboot stick. Boot it for installers/rescue ISOs; open it in Windows for tools and scripts.

## Layout
| Path | Contents |
|---|---|
| `ISO\1-Windows Install` | `Win11_x64.iso`, `Win11_arm.iso` (ARM64 = Snapdragon only) |
| `ISO\2-Linux Install` | `ubuntu.iso`, `Fedora.iso` (live/install) |
| `ISO\3-Repair` | Hiren's BootCD PE (`HBCD_PE_x64.iso`), Memtest86+, SystemRescue, ShredOS (secure wipe - destructive) |
| `Tools\` | Click-to-run launchers (see below) |
| `src\Programs` | The actual portable programs the launchers point to |
| `src\Scripts` | `BitLocker\` (unlock script) and `Update\` (ISO / tool updaters) |
| `src\ITToolkit`, `src\UpdateToolkit`, `src\BitLockerTool` | C# source + `build.bat` for the GUI tools (`src\LauncherMaker_v2.exe` makes the launchers) |
| `Reports` | Output from scripts, one folder per machine |
| `ventoy\ventoy.json` | Boot menu names, tooltips, theme (`ventoy\theme\bsol`) |
| `key.txt` (drive root, created on demand) | BitLocker recovery key hand-off file; wiped after use |

### Tools (launchers)
Each `.exe` in `Tools\` is a small stub that starts a program in `src\Programs\` at a fixed path, so keep that layout intact.

| Folder | Launchers |
|---|---|
| `Tools\BitLocker` | **BitLocker Unlock.exe** - GUI to unlock with a recovery password and decrypt (real tool, not a stub) |
| `Tools\Storage` | Hard Disk Sentinel, Macrium Reflect (installer), MiniTool Partition Wizard (installer), Rufus, Samsung Magician (installer), WinDirStat |
| `Tools\Malware Scans` | Adlice Protect (RogueKiller), AdwCleaner, Autoruns, Junkware Removal Tool, Malwarebytes (installer), Revo Uninstaller, rkill |
| `Tools\Stress Test` | FurMark, FurMark (GUI), Prime95 |
| `Tools\Utilities` | 7-Zip File Manager, Display Driver Uninstaller (run in Safe Mode), Everything, Notepad++, PuTTY |

`src\Programs` also holds the full Sysinternals Suite (no launcher; run from there).

## GUI tools (`src\`)
All three build with the `csc.exe` that ships with Windows (.NET Framework 4.x, no SDK): run `build.bat` in the tool's folder.

- **ITToolkit** (`ITToolkit\ITToolkit.exe`) - DISM / SFC / CHKDSK repair, disk health, network diagnostics, UEFI/boot (ESP) tools and system info. Works on the running OS or an offline Windows drive (`/target:D:`). Auto-elevates. Command line: `/target`, `/source` (e.g. `wim:E:\sources\install.wim:1`), `/scratch`, `/run:<id>[,<id>]`, `/log`, `/exit`, `/list` (shows tool ids), `/help`. Example: `ITToolkit.exe /target:D: /run:full-repair /log:E:\repair.log /exit`
- **UpdateToolkit** (`UpdateToolkit\UpdateToolkit.exe`) - GUI with progress bars for the two updaters below. Args: `all|isos|tools` to start immediately, `/check` (download nothing), `/force`, `/root:X:\`.
- **BitLockerTool** - source for `Tools\BitLocker\BitLocker Unlock.exe` (its `build.bat` writes there).
- **LauncherMaker_v2.exe** (`src\`) - generates the stub launchers in `Tools\`.

## Use
- Boot: pick the stick in the BIOS/UEFI boot menu; ISOs are listed by folder.
- Adding an ISO: drop it in the right `ISO\` subfolder, then add an `alias` and `tip` entry for it in `ventoy\ventoy.json`.
- Keep ISOs current: `src\Scripts\Update\Update-Isos.cmd` (`--list` to only check, or name one entry, e.g. `ubuntu`). Covers Windows 11, Ubuntu, Fedora, Hiren's, ShredOS, Memtest86+, SystemRescue; keeps the filenames above. Uses an installed Python (3.8+) or downloads a portable one into `src\Scripts\Update\python` on first run.
- Keep portable programs current: `src\Scripts\Update\Update-Tools.cmd` (same options). Updates 7-Zip, WinDirStat, Revo, HD Sentinel, Adlice, Malwarebytes, Notepad++, AdwCleaner, Sysinternals, Everything, PuTTY, Prime95, Rufus, Samsung Magician in place. DDU, FurMark, JRT, MiniTool, Macrium and rkill are not auto-updated.
- Versions are tracked in `src\Scripts\Update\ver.log` (`[ISO]`, `[Tools]`, `[Skipped]`).
- BitLocker: on another machine paste the 48-digit recovery password into `key.txt` at the drive root (e.g. `E:\key.txt`), then run `Tools\BitLocker\BitLocker Unlock.exe` (or `src\Scripts\BitLocker\Unlock-And-Disable.ps1 -Drive C:` as admin) on the locked machine. `key.txt` is overwritten and emptied right after the unlock attempt.

## Rules
- No customer data, passwords or BitLocker keys on this drive. If you must copy something off a customer machine, delete it afterward.
- ShredOS erases entire disks. Verify the target.
- `Tools\` launchers depend on exact paths in `src\Programs`; the updaters preserve them, so don't rename folders there.

## Housekeeping
- macOS leaves `._*` files and `.Spotlight-V100`, `.fseventsd`, `.Trashes` on the stick. Ventoy ignores `._*` (`VTOY_FILT_DOT_UNDERSCORE_FILE`); they are safe to delete.
- `src\UpdateToolkit\error.txt` is a stale log from a bad `/root` argument and can be deleted.
