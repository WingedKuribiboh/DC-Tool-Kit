# DC Tool Kit

IT toolkit for the DC techs. It lives on a Ventoy multiboot USB stick: boot the stick for installer and rescue ISOs, or open it in Windows for the tools and scripts. This repo is the master copy used to push updates out to every tech's stick.

## Layout

| Folder | Contents |
|---|---|
| `ISO/` | Boot images, in `1-Windows Install`, `2-Linux Install` and `3-Repair`. **Not stored in this repo** (about 30 GB). Fetch them with `Scripts/Update/Update-Isos.cmd`. |
| `Tools/` | Small launcher `.exe` files grouped by job (Storage, Utilities, Malware Scans, Stress Test). Each one opens the real program in `Programs/`. |
| `Programs/` | The actual portable programs and installers. The launchers expect fixed paths in here, so don't rename anything. |
| `Scripts/Windows/` | System report, disk health, network diagnostics, DISM/SFC repair, Windows Update reset. |
| `Scripts/BitLocker/` | `Unlock-And-Disable.ps1`. It prompts for the recovery key at run time. |
| `Scripts/Update/` | Updaters for the ISOs and portable programs (Python, with a bundled runtime). `ver.log` records the versions. |
| `Management/` | Source and builds of the helper apps: `ITToolkit`, `UpdateToolkit` and `LauncherMaker_v2.exe`. |
| `ventoy/` | `ventoy.json` (boot menu names and tips) and the boot menu theme. |

## Using the stick

- **Boot a machine from it:** pick an image from the Ventoy menu.
- **In Windows:** open `Tools\` for the launchers or `Scripts\` for the scripts. Run scripts as administrator.
- **Add an ISO:** put it in the right `ISO\` subfolder, then add an `alias`, `tip` and `class` entry for it in `ventoy\ventoy.json`.
- **Update ISOs:** run `Scripts\Update\Update-Isos.cmd` (add `--list` to only check).
- **Update programs:** run `Scripts\Update\Update-Tools.cmd`. It covers 7-Zip, WinDirStat, Revo, HD Sentinel, Adlice, Malwarebytes and Notepad++. Programs that can't be auto-updated are listed under `[Skipped]` in `Scripts\Update\ver.log`.
- **GUI updater:** `Management\UpdateToolkit\UpdateToolkit.exe` (rebuild with `build.bat` in that folder).

## Getting repo updates onto a stick

0. Install [Git LFS](https://git-lfs.com) once and run `git lfs install`. The special Macrium Reflect installer (`Programs/reflect_setup_free_x64.exe`, 186 MB) is stored with LFS, and without it you get a small text pointer file instead of the installer.
1. Clone this repo, or `git pull` if you already have it.
2. Copy the contents onto the stick's root, over the existing files.
3. Run `Update-Isos.cmd` to download the ISOs, which are not in the repo.

## Contributing

- Make changes on a branch and open a pull request. Don't push to `main` directly.
- Keep the folder and file names that the launchers and updaters rely on (see the notes at the top of `Scripts/Update/update_tools.py`).
- Don't commit ISOs, customer data, passwords or BitLocker recovery keys.
- Files over 100 MB are rejected by GitHub unless they go through Git LFS. Add new ones to `.gitattributes` before committing.

## Safety

- ShredOS erases entire disks. Verify the target before running it.
- DDU (GPU driver removal) should be run in Safe Mode.
- Never leave customer data on a stick. If you copy something off a customer machine, delete it afterward.
