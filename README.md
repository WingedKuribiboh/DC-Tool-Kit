# DC Tool Kit

IT toolkit for the DC techs. It lives on a Ventoy multiboot USB stick: boot the stick for installer and rescue ISOs, or open it in Windows for the tools and scripts. This repo is the master copy used to push updates out to every tech's stick.

## Layout

```
DC-Tool-Kit/
├── ISO/                  Boot images (not in the repo; see below)
├── Tools/                Launchers, grouped by job
│   ├── Malware Scans/
│   ├── Storage/
│   ├── Stress Test/
│   └── Utilities/
├── src/
│   ├── programs/         The real programs the launchers open
│   ├── scripts/
│   │   ├── Windows/      Diagnostics and repair scripts
│   │   ├── BitLocker/    Unlock-And-Disable.ps1
│   │   └── Update/       ISO and program updaters, bundled Python, ver.log
│   └── Management/       Helper apps: ITToolkit, UpdateToolkit, LauncherMaker_v2.exe
└── ventoy/               ventoy.json and the boot menu theme
```

| Folder | Contents |
|---|---|
| `ISO/` | Boot images, in `1-Windows Install`, `2-Linux Install` and `3-Repair`. **Not stored in this repo** (about 30 GB). Fetch them with `src/scripts/Update/Update-Isos.cmd`. Ventoy only scans this folder (`VTOY_DEFAULT_SEARCH_ROOT`). |
| `Tools/` | Small launcher `.exe` files (about 5 KB each, made with `LauncherMaker_v2.exe`). Each one opens the real program at a fixed path, so don't rename anything. |
| `src/programs/` | The portable programs and installers that the launchers open. Only some of them are committed (DDU, FurMark, JRT, rkill, MiniTool Partition Wizard). The rest are downloaded by `Update-Tools.cmd`. |
| `src/scripts/Windows/` | `System-Report.ps1`, `Disk-Health.ps1`, `Network-Diag.ps1`, `Repair-Windows.cmd` (DISM/SFC) and `Reset-WindowsUpdate.cmd`. |
| `src/scripts/BitLocker/` | `Unlock-And-Disable.ps1`. Reads the recovery password from `key.txt` next to the script and wipes `key.txt` when it finishes. |
| `src/scripts/Update/` | `update_isos.py` and `update_tools.py` (plus `updlib.py`), their `.cmd` wrappers, a bundled embeddable Python 3.12 in `python/`, and `ver.log`, which records installed versions. |
| `src/Management/` | Source (C#) and builds of `ITToolkit` and `UpdateToolkit`, plus `LauncherMaker_v2.exe` for making new launchers. Each app has a `build.bat` that uses the `csc.exe` built into Windows, so no SDK is needed. |
| `ventoy/` | `ventoy.json` (menu names, tips and icon classes) and the `bsol` theme. **This folder must stay at the root of the stick**, or Ventoy won't load the config. |

## Boot menu

| Menu entry | File |
|---|---|
| Windows 11 x64 | `ISO/1-Windows Install/Win11_x64.iso` |
| Windows 11 ARM64 (Snapdragon only) | `ISO/1-Windows Install/Win11_arm.iso` |
| Ubuntu Desktop (live / install) | `ISO/2-Linux Install/ubuntu.iso` |
| Fedora Workstation (live / install) | `ISO/2-Linux Install/Fedora.iso` |
| Hiren's BootCD PE | `ISO/3-Repair/HBCD_PE_x64.iso` |
| ShredOS (secure disk wipe) | `ISO/3-Repair/shredos.iso` |
| Memtest86+ | `ISO/3-Repair/Memtest86plus.iso` |
| SystemRescue (no alias yet, shows as the file name) | `ISO/3-Repair/systemrescue.iso` |

The updater always saves each image under the same file name, so the names, tips and icons in `ventoy.json` keep working after an update.

## Using the stick

- **Boot a machine from it:** pick an image from the Ventoy menu. Each entry shows a tip at the bottom of the screen.
- **In Windows:** open `Tools\` for the programs or `src\scripts\` for the scripts. Run scripts as administrator.
- **Add an ISO:** put it in the right `ISO\` subfolder, then add an `alias`, `tip` and `class` entry for it in `ventoy\ventoy.json`.
- **Update ISOs:** run `src\scripts\Update\Update-Isos.cmd` (add `--list` to only check, or name entries such as `ubuntu shredos` to update only those).
- **Update programs:** run `src\scripts\Update\Update-Tools.cmd` (it takes the same options). Programs that can't be auto-updated are listed under `[Skipped]` in `ver.log`.
- **GUI updater:** `src\Management\UpdateToolkit\UpdateToolkit.exe`. It runs the same two scripts and shows their progress.

Both `.cmd` updaters use an installed Python 3.8+ if one exists. Otherwise they fall back to the bundled one in `src\scripts\Update\python\`.

## Getting repo updates onto a stick

1. Clone this repo, or `git pull` if you already have it.
2. Copy the **contents** of the repo onto the stick's data partition root, over the existing files. `ventoy/` and `ISO/` have to end up at the top level of the stick.
3. Run `Update-Tools.cmd` to fetch the programs that aren't in the repo.
4. Run `Update-Isos.cmd` to download the ISOs.

## Contributing

- Make changes on a branch and open a pull request. Don't push to `main` directly.
- Keep the folder and file names that the launchers, `ventoy.json` and the updaters rely on (see the notes at the top of `src/scripts/Update/update_tools.py` and `update_isos.py`).
- Don't commit ISOs, customer data, passwords or BitLocker recovery keys. Check `git status` before committing anything from `src/scripts/data/` or a `key.txt`.
- Files over 100 MB are rejected by GitHub unless they go through Git LFS. Add new ones to `.gitattributes` before committing.

## Safety

- ShredOS erases entire disks. Verify the target before running it.
- Run DDU (GPU driver removal) in Safe Mode.
- Never leave customer data or recovery keys on a stick. If you copy something off a customer machine, delete it afterward.
