#Requires -Version 5.1
<#
    EFI Breaker - deliberately damages a UEFI boot setup so you can practise / test repairs.
    FOR TEST MACHINES AND VMs ONLY. Every break action asks for confirmation first.

    Levels:
      L1  Delete the BCD store (falls back to deleting the {default} loader entry if the store is locked)
      L2  Rename the boot manager files (bootmgfw.efi, bootx64.efi, bootaa64.efi -> *.bak)
      L3  Remove Windows Boot Manager from the firmware boot order
      L4  Quick-format the selected partition (FAT32, empty)
      L5  Delete the selected partition

    Fixes:
      Undo L2 / Undo L3, Recreate EFI partition, Repair with bcdboot
#>

# ---------- Environment / self-elevate ----------
# WinPE (Hiren's BootCD PE etc.) always runs as SYSTEM and sets this key.
$script:IsPE = Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Control\MiniNT'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $script:IsPE -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -STA -File `"$PSCommandPath`""
    exit
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName Microsoft.VisualBasic
[System.Windows.Forms.Application]::EnableVisualStyles()

$EspGuid = '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
$script:Partitions = @()

# ---------- Terminal output ----------
function Write-Term {
    param([string]$Text, [string]$Color = 'Gainsboro')
    $term.SelectionStart = $term.TextLength
    $term.SelectionLength = 0
    $term.SelectionColor = [System.Drawing.Color]::FromName($Color)
    $term.AppendText($Text + [Environment]::NewLine)
    $term.ScrollToCaret()
    [System.Windows.Forms.Application]::DoEvents()
}
function Write-Step([string]$t) { Write-Term ''; Write-Term ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $t) 'Cyan' }
function Write-Ok([string]$t)   { Write-Term "  OK   $t" 'LimeGreen' }
function Write-Bad([string]$t)  { Write-Term "  ERR  $t" 'Tomato' }
function Write-Note([string]$t) { Write-Term "  ..   $t" 'Khaki' }

function Invoke-Exe {
    param([string]$Exe, [string[]]$Arguments)
    Write-Term ("  > {0} {1}" -f $Exe, ($Arguments -join ' ')) 'Yellow'
    $out = & $Exe @Arguments 2>&1
    $code = $LASTEXITCODE
    foreach ($line in $out) { if ("$line".Trim()) { Write-Term ("    " + "$line".TrimEnd()) } }
    if ($code -eq 0) { Write-Ok "exit code 0" } else { Write-Bad "exit code $code" }
    return $code
}

function Invoke-DiskPart {
    param([string[]]$Lines)
    $file = Join-Path $env:TEMP ("efibreaker_{0}.txt" -f [guid]::NewGuid())
    Set-Content -Path $file -Value $Lines -Encoding ASCII
    Write-Term "  > diskpart" 'Yellow'
    foreach ($l in $Lines) { Write-Term "      $l" 'Yellow' }
    $out = & diskpart.exe /s $file 2>&1
    $code = $LASTEXITCODE
    Remove-Item $file -Force -ErrorAction SilentlyContinue
    foreach ($line in $out) {
        $s = "$line".TrimEnd()
        if ($s.Trim() -and $s -notmatch '^\s*(Microsoft DiskPart|Copyright|On computer)') { Write-Term "    $s" }
    }
    if ($code -eq 0) { Write-Ok "diskpart finished" } else { Write-Bad "diskpart exit code $code" }
    return $code
}

# Runs a diskpart script without echoing to the terminal and returns its output lines.
function Get-DiskPartOutput {
    param([string[]]$Lines)
    $file = Join-Path $env:TEMP ("efibreaker_{0}.txt" -f [guid]::NewGuid())
    Set-Content -Path $file -Value $Lines -Encoding ASCII
    $out = & diskpart.exe /s $file 2>&1
    Remove-Item $file -Force -ErrorAction SilentlyContinue
    return @($out | ForEach-Object { "$_" })
}

# ---------- Partition helpers ----------
# Fallback for PE builds without the Storage cmdlets: parse diskpart's own listing.
function Get-PartitionListDiskPart {
    $list = @()
    $disks = @()
    foreach ($line in (Get-DiskPartOutput @('list disk'))) {
        if ($line -match '^\s*\*?\s*Disk\s+(\d+)\s+') { $disks += [int]$Matches[1] }
    }
    foreach ($d in $disks) {
        foreach ($line in (Get-DiskPartOutput @("select disk $d", 'list partition'))) {
            if ($line -match '^\s*\*?\s*Partition\s+(\d+)\s+(.+?)\s+(\d+)\s+(KB|MB|GB|TB)') {
                $num = [int]$Matches[1]; $type = $Matches[2].Trim()
                $mb = [double]$Matches[3] * @{ KB = 1 / 1024; MB = 1; GB = 1024; TB = 1048576 }[$Matches[4]]
                $isEsp = ($type -eq 'System')
                $kind = if ($isEsp) { '** EFI SYSTEM **' } else { $type }
                $text = "Disk {0}  Part {1}  | {2,-16} | {3,8:N0} MB |" -f $d, $num, $kind, $mb
                $list += [pscustomobject]@{ Disk = $d; Part = $num; IsEsp = $isEsp; Letter = ''; Text = $text }
            }
        }
    }
    return $list
}

function Get-PartitionList {
    $list = @()
    $parts = $null
    if (Get-Command Get-Partition -ErrorAction SilentlyContinue) {
        try { $parts = @(Get-Partition -ErrorAction Stop | Sort-Object DiskNumber, PartitionNumber) } catch { $parts = $null }
    }
    if (-not $parts -or $parts.Count -eq 0) {
        Write-Note "Storage cmdlets unavailable - reading partitions with diskpart instead."
        return Get-PartitionListDiskPart
    }
    foreach ($p in $parts) {
        $vol = $null
        try { $vol = $p | Get-Volume -ErrorAction Stop } catch {}
        $isEsp  = ("$($p.GptType)" -eq $EspGuid)
        $letter = if ("$($p.DriveLetter)" -match '^[A-Za-z]$') { "$($p.DriveLetter)".ToUpper() } else { '' }
        $fs     = if ($vol -and $vol.FileSystem) { $vol.FileSystem } else { '-' }
        $label  = if ($vol -and $vol.FileSystemLabel) { $vol.FileSystemLabel } else { '' }
        $kind   = if ($isEsp) { '** EFI SYSTEM **' } else { "$($p.Type)" }
        $lt     = if ($letter) { "${letter}:" } else { '  ' }
        $text = "Disk {0}  Part {1}  | {2,-16} | {3,8:N0} MB | {4,-5} | {5} {6}" -f `
            $p.DiskNumber, $p.PartitionNumber, $kind, ($p.Size / 1MB), $fs, $lt, $label
        $list += [pscustomobject]@{
            Disk = $p.DiskNumber; Part = $p.PartitionNumber; IsEsp = $isEsp; Letter = $letter; Text = $text
        }
    }
    return $list
}

function Update-PartitionList {
    $prev = Get-Selected -Quiet
    $script:Partitions = @(Get-PartitionList)
    $cbPart.Items.Clear()
    foreach ($p in $script:Partitions) { [void]$cbPart.Items.Add($p.Text) }
    $idx = -1
    if ($prev) {
        for ($i = 0; $i -lt $script:Partitions.Count; $i++) {
            if ($script:Partitions[$i].Disk -eq $prev.Disk -and $script:Partitions[$i].Part -eq $prev.Part) { $idx = $i }
        }
    }
    if ($idx -lt 0) {
        for ($i = 0; $i -lt $script:Partitions.Count; $i++) { if ($script:Partitions[$i].IsEsp) { $idx = $i; break } }
    }
    if ($idx -lt 0 -and $script:Partitions.Count) { $idx = 0 }
    $cbPart.SelectedIndex = $idx
}

# Finds installed copies of Windows on every drive (skipping the PE's own X:\Windows).
function Update-WindowsList {
    $current = $cbWin.Text
    $cbWin.Items.Clear()
    foreach ($d in [System.IO.DriveInfo]::GetDrives()) {
        $root = $d.Name.Substring(0, 2)
        if ($script:IsPE -and $root -eq $env:SystemDrive) { continue }
        $w = "$root\Windows"
        if (Test-Path -LiteralPath "$w\System32\config\SYSTEM") { [void]$cbWin.Items.Add($w) }
    }
    if ($current -and $cbWin.Items.Contains($current)) { $cbWin.Text = $current }
    elseif ($cbWin.Items.Count) { $cbWin.SelectedIndex = 0 }
    else { $cbWin.Text = '' }
}

function Get-Selected {
    param([switch]$Quiet)
    if (-not $cbPart -or $cbPart.SelectedIndex -lt 0 -or $cbPart.SelectedIndex -ge $script:Partitions.Count) {
        if (-not $Quiet) { Write-Bad "No partition selected." }
        return $null
    }
    return $script:Partitions[$cbPart.SelectedIndex]
}

function Get-MountLetter {
    $l = $txtLetter.Text.Trim().TrimEnd(':').ToUpper()
    if ($l -notmatch '^[D-Z]$') { Write-Bad "Mount letter must be a single letter D-Z."; return $null }
    return $l
}

# Returns the drive letter the selected partition is reachable at, mounting it if needed.
function Mount-Target {
    param($Sel)
    if ($Sel.Letter) { return $Sel.Letter }
    $L = Get-MountLetter
    if (-not $L) { return $null }
    if (Test-Path "${L}:\") { Write-Bad "${L}: is already in use by another drive. Pick a different mount letter."; return $null }
    Write-Note "Mounting Disk $($Sel.Disk) Part $($Sel.Part) as ${L}:"
    [void](Invoke-DiskPart @("select disk $($Sel.Disk)", "select partition $($Sel.Part)", "assign letter=$L"))
    Start-Sleep -Milliseconds 800
    Update-PartitionList
    if (Test-Path "${L}:\") { return $L }
    Write-Bad "Mount failed - ${L}: is not reachable."
    return $null
}

function Confirm-Danger {
    param($Sel, [string]$Action, [switch]$TypeToConfirm)
    $msg = "$Action`n`nTarget: Disk $($Sel.Disk), Partition $($Sel.Part)`n$($Sel.Text)`n"
    if (-not $Sel.IsEsp) {
        $msg += "`nWARNING: this is NOT an EFI System partition!`n"
    }
    $msg += "`nOnly do this on a test machine or VM. Continue?"
    $r = [System.Windows.Forms.MessageBox]::Show($msg, 'EFI Breaker - confirm', 'YesNo', 'Warning')
    if ($r -ne 'Yes') { Write-Note "Cancelled."; return $false }
    if ($TypeToConfirm -or -not $Sel.IsEsp) {
        $typed = [Microsoft.VisualBasic.Interaction]::InputBox("Type BREAK to confirm.", 'EFI Breaker - final confirm', '')
        if ($typed -cne 'BREAK') { Write-Note "Cancelled (confirmation text did not match)."; return $false }
    }
    return $true
}

$BootFiles = @('EFI\Microsoft\Boot\bootmgfw.efi', 'EFI\Boot\bootx64.efi', 'EFI\Boot\bootaa64.efi')

# ---------- Actions ----------
function Do-Mount {
    Write-Step "Mount selected partition"
    $sel = Get-Selected; if (-not $sel) { return }
    $L = Mount-Target $sel
    if ($L) {
        Write-Ok "Partition is at ${L}:"
        Get-ChildItem "${L}:\" -Recurse -Force -Depth 2 -ErrorAction SilentlyContinue |
            Select-Object -First 40 | ForEach-Object { Write-Term ("    " + $_.FullName) }
    }
}

function Do-Unmount {
    Write-Step "Remove drive letter from selected partition"
    $sel = Get-Selected; if (-not $sel) { return }
    if (-not $sel.Letter) { Write-Note "Partition has no drive letter."; return }
    [void](Invoke-DiskPart @("select disk $($sel.Disk)", "select partition $($sel.Part)", "remove letter=$($sel.Letter)"))
    Update-PartitionList
}

function Do-L1 {
    Write-Step "L1 - Delete BCD store"
    $sel = Get-Selected; if (-not $sel) { return }
    if (-not (Confirm-Danger $sel "L1: Delete the BCD boot configuration store.")) { return }
    $L = Mount-Target $sel; if (-not $L) { return }
    $bcd = "${L}:\EFI\Microsoft\Boot\BCD"
    if (-not (Test-Path -LiteralPath $bcd)) { Write-Bad "$bcd not found (already deleted?)"; return }
    try {
        Remove-Item -LiteralPath $bcd -Force -ErrorAction Stop
        Remove-Item -LiteralPath "$bcd.LOG", "$bcd.LOG1", "$bcd.LOG2" -Force -ErrorAction SilentlyContinue
        Write-Ok "Deleted $bcd"
    } catch {
        Write-Bad "Could not delete the store: $($_.Exception.Message)"
        Write-Note "The store is locked (in use by the running Windows). Deleting its {default} OS loader entry instead."
        [void](Invoke-Exe 'bcdedit.exe' @('/store', $bcd, '/delete', '{default}', '/f'))
    }
    Write-Note "Expected on reboot: 'Your PC needs to be repaired' / 0xc000000f-style error."
}

function Do-L2 {
    Write-Step "L2 - Rename boot manager files"
    $sel = Get-Selected; if (-not $sel) { return }
    if (-not (Confirm-Danger $sel "L2: Rename bootmgfw.efi and the fallback bootx64/bootaa64.efi to .bak")) { return }
    $L = Mount-Target $sel; if (-not $L) { return }
    $any = $false
    foreach ($f in $BootFiles) {
        $path = "${L}:\$f"
        if (Test-Path -LiteralPath $path) {
            try { Rename-Item -LiteralPath $path -NewName ((Split-Path $path -Leaf) + '.bak') -Force -ErrorAction Stop; Write-Ok "Renamed $path -> .bak"; $any = $true }
            catch { Write-Bad "$path : $($_.Exception.Message)" }
        } else { Write-Note "Not present: $path" }
    }
    if (-not $any) { Write-Bad "No boot files were renamed." }
    Write-Note "Expected on reboot: 'No bootable device' or the PC drops to the firmware boot menu."
}

function Do-UndoL2 {
    Write-Step "Undo L2 - restore renamed boot files"
    $sel = Get-Selected; if (-not $sel) { return }
    $L = Mount-Target $sel; if (-not $L) { return }
    foreach ($f in $BootFiles) {
        $bak = "${L}:\$f.bak"
        if (Test-Path -LiteralPath $bak) {
            try { Rename-Item -LiteralPath $bak -NewName (Split-Path "${L}:\$f" -Leaf) -Force -ErrorAction Stop; Write-Ok "Restored ${L}:\$f" }
            catch { Write-Bad "$bak : $($_.Exception.Message)" }
        }
    }
}

function Do-L3 {
    Write-Step "L3 - Remove Windows Boot Manager from firmware boot order"
    $sel = Get-Selected; if (-not $sel) { return }
    $r = [System.Windows.Forms.MessageBox]::Show(
        "L3: Remove 'Windows Boot Manager' from THIS PC's firmware (NVRAM) boot order.`n`nThis affects the machine you are running on, not the selected partition. Continue?",
        'EFI Breaker - confirm', 'YesNo', 'Warning')
    if ($r -ne 'Yes') { Write-Note "Cancelled."; return }
    [void](Invoke-Exe 'bcdedit.exe' @('/set', '{fwbootmgr}', 'displayorder', '{bootmgr}', '/remove'))
    [void](Invoke-Exe 'bcdedit.exe' @('/enum', 'firmware'))
    Write-Note "Some firmware re-adds the entry automatically at boot by scanning the disk - that is normal."
}

function Do-UndoL3 {
    Write-Step "Undo L3 - put Windows Boot Manager first in firmware boot order"
    [void](Invoke-Exe 'bcdedit.exe' @('/set', '{fwbootmgr}', 'displayorder', '{bootmgr}', '/addfirst'))
}

function Do-L4 {
    Write-Step "L4 - Format partition"
    $sel = Get-Selected; if (-not $sel) { return }
    if (-not (Confirm-Danger $sel "L4: Quick-format this partition as empty FAT32. All boot files on it are erased." -TypeToConfirm)) { return }
    $lines = @("select disk $($sel.Disk)", "select partition $($sel.Part)", "format fs=fat32 quick label=SYSTEM override")
    $code = Invoke-DiskPart $lines
    if ($code -ne 0) { Write-Note "Windows usually refuses to format the ESP it booted from. Run this tool from a PE environment or target a second disk / VM disk." }
    Update-PartitionList
}

function Do-L5 {
    Write-Step "L5 - Delete partition"
    $sel = Get-Selected; if (-not $sel) { return }
    if (-not (Confirm-Danger $sel "L5: DELETE this partition entirely." -TypeToConfirm)) { return }
    $code = Invoke-DiskPart @("select disk $($sel.Disk)", "select partition $($sel.Part)", "delete partition override")
    if ($code -ne 0) { Write-Note "Windows refuses to delete the ESP it booted from. Run this tool from a PE environment or target a second disk / VM disk." }
    Update-PartitionList
}

function Do-Recreate {
    Write-Step "Recreate EFI System partition"
    $sel = Get-Selected; if (-not $sel) { return }
    $L = Get-MountLetter; if (-not $L) { return }
    $size = $txtSize.Text.Trim()
    if ($size -notmatch '^\d+$' -or [int]$size -lt 100) { Write-Bad "EFI size must be a number of MB, at least 100."; return }
    $r = [System.Windows.Forms.MessageBox]::Show(
        "Create a new $size MB EFI System partition in the free space on Disk $($sel.Disk) and mount it as ${L}:?`n`n(Use this after L5. The disk needs at least $size MB unallocated.)",
        'EFI Breaker - confirm', 'YesNo', 'Question')
    if ($r -ne 'Yes') { Write-Note "Cancelled."; return }
    [void](Invoke-DiskPart @("select disk $($sel.Disk)", "create partition efi size=$size", "format fs=fat32 quick label=SYSTEM", "assign letter=$L"))
    Update-PartitionList
    Write-Note "Now press 'Repair (bcdboot)' to put the boot files back."
}

function Do-Repair {
    Write-Step "Repair - rebuild boot files with bcdboot"
    $sel = Get-Selected; if (-not $sel) { return }
    $win = $cbWin.Text.Trim().TrimEnd('\')
    if (-not $win -or -not (Test-Path -LiteralPath (Join-Path $win 'System32\config\SYSTEM'))) { Write-Bad "'$win' does not look like an installed Windows folder. Press Refresh or type the path."; return }
    if ($script:IsPE -and $win.StartsWith($env:SystemDrive, [StringComparison]::OrdinalIgnoreCase)) { Write-Bad "$win is the PE environment itself, not the installed Windows."; return }
    $L = Mount-Target $sel; if (-not $L) { return }
    $code = Invoke-Exe 'bcdboot.exe' @($win, '/s', "${L}:", '/f', 'UEFI')
    if ($code -eq 0) {
        Write-Ok "Boot files, BCD and firmware entry rebuilt. Restart to confirm it boots."
    }
}

# ---------- UI ----------
$bg      = [System.Drawing.Color]::FromArgb(30, 30, 34)
$panelBg = [System.Drawing.Color]::FromArgb(42, 42, 48)
$fg      = [System.Drawing.Color]::Gainsboro
$mono    = New-Object System.Drawing.Font('Consolas', 9.5)
$ui      = New-Object System.Drawing.Font('Segoe UI', 9)
$uiBold  = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Bold)

$form = New-Object System.Windows.Forms.Form
$form.Text = 'EFI Breaker - UEFI boot damage / repair test tool  (TEST MACHINES ONLY)'
$form.Size = New-Object System.Drawing.Size(1040, 760)
$form.MinimumSize = New-Object System.Drawing.Size(1040, 560)
$form.StartPosition = 'CenterScreen'
$form.BackColor = $bg
$form.ForeColor = $fg
$form.Font = $ui

$tip = New-Object System.Windows.Forms.ToolTip
$tip.AutoPopDelay = 15000

function New-Label($text, $x, $y, $w = 120) {
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $text; $l.Location = New-Object System.Drawing.Point($x, ($y + 4)); $l.Size = New-Object System.Drawing.Size($w, 20)
    $form.Controls.Add($l); return $l
}
function New-Button($text, $x, $y, $w, $h, $color, $onClick, $tipText) {
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $text
    $b.Location = New-Object System.Drawing.Point($x, $y)
    $b.Size = New-Object System.Drawing.Size($w, $h)
    $b.FlatStyle = 'Flat'
    $b.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(80, 80, 90)
    $b.BackColor = $color
    $b.ForeColor = [System.Drawing.Color]::White
    $b.Font = $uiBold
    $b.Tag = $onClick
    $b.Add_Click({
        $form.UseWaitCursor = $true
        try { & $this.Tag } catch { Write-Bad $_.Exception.Message }
        finally { $form.UseWaitCursor = $false }
    })
    if ($tipText) { $tip.SetToolTip($b, $tipText) }
    $form.Controls.Add($b); return $b
}

# Row 1: partition picker
[void](New-Label 'Target partition:' 12 14)
$cbPart = New-Object System.Windows.Forms.ComboBox
$cbPart.DropDownStyle = 'DropDownList'
$cbPart.Font = $mono
$cbPart.Location = New-Object System.Drawing.Point(132, 12)
$cbPart.Size = New-Object System.Drawing.Size(760, 24)
$cbPart.BackColor = $panelBg; $cbPart.ForeColor = $fg
$cbPart.Anchor = 'Top,Left,Right'
$form.Controls.Add($cbPart)
$btnRefresh = New-Button 'Refresh' 902 10 110 28 ([System.Drawing.Color]::FromArgb(70, 70, 80)) { Write-Step 'Refresh partitions and Windows installs'; Update-PartitionList; Update-WindowsList; Write-Ok "$($script:Partitions.Count) partitions, $($cbWin.Items.Count) Windows install(s) found" } 'Re-read the disks and look for installed Windows.'
$btnRefresh.Anchor = 'Top,Right'

# Row 2: mount letter, windows folder, efi size
[void](New-Label 'Mount letter:' 12 50)
$txtLetter = New-Object System.Windows.Forms.TextBox
$txtLetter.Text = 'S'; $txtLetter.MaxLength = 2
$txtLetter.Location = New-Object System.Drawing.Point(132, 49); $txtLetter.Size = New-Object System.Drawing.Size(40, 24)
$txtLetter.BackColor = $panelBg; $txtLetter.ForeColor = $fg; $txtLetter.Font = $mono
$form.Controls.Add($txtLetter)
$tip.SetToolTip($txtLetter, 'Letter used when the selected partition has no drive letter (the ESP normally has none).')

[void](New-Button 'Mount' 182 47 80 28 ([System.Drawing.Color]::FromArgb(70, 70, 80)) { Do-Mount } 'Give the selected partition a drive letter and list its files.')
[void](New-Button 'Unmount' 268 47 80 28 ([System.Drawing.Color]::FromArgb(70, 70, 80)) { Do-Unmount } 'Remove the drive letter from the selected partition.')

[void](New-Label 'Windows folder:' 370 50 100)
$cbWin = New-Object System.Windows.Forms.ComboBox
$cbWin.DropDownStyle = 'DropDown'
$cbWin.Location = New-Object System.Drawing.Point(472, 49); $cbWin.Size = New-Object System.Drawing.Size(180, 24)
$cbWin.BackColor = $panelBg; $cbWin.ForeColor = $fg; $cbWin.Font = $mono
$form.Controls.Add($cbWin)
$tip.SetToolTip($cbWin, 'Installed Windows that bcdboot copies boot files from. Auto-detected; you can also type a path.')

[void](New-Label 'EFI size (MB):' 670 50 90)
$txtSize = New-Object System.Windows.Forms.TextBox
$txtSize.Text = '260'
$txtSize.Location = New-Object System.Drawing.Point(762, 49); $txtSize.Size = New-Object System.Drawing.Size(60, 24)
$txtSize.BackColor = $panelBg; $txtSize.ForeColor = $fg; $txtSize.Font = $mono
$form.Controls.Add($txtSize)

# Row 3: break buttons
$lblBreak = New-Label 'BREAK' 12 88 200
$lblBreak.ForeColor = [System.Drawing.Color]::Tomato; $lblBreak.Font = $uiBold
$bw = 196; $bh = 54; $by = 112
[void](New-Button "L1  Delete BCD`n(boot config store)" 12 $by $bw $bh ([System.Drawing.Color]::FromArgb(150, 110, 20)) { Do-L1 } 'Deletes EFI\Microsoft\Boot\BCD. Boot error: 0xc000000f / "needs to be repaired".')
[void](New-Button "L2  Rename boot files`n(bootmgfw / bootx64)" (12 + 1 * ($bw + 4)) $by $bw $bh ([System.Drawing.Color]::FromArgb(170, 90, 20)) { Do-L2 } 'Renames the boot manager and fallback loader to .bak. Result: no bootable device.')
[void](New-Button "L3  Remove firmware`nboot entry" (12 + 2 * ($bw + 4)) $by $bw $bh ([System.Drawing.Color]::FromArgb(175, 70, 25)) { Do-L3 } 'Removes Windows Boot Manager from this PC''s NVRAM boot order.')
[void](New-Button "L4  Format partition`n(empty FAT32)" (12 + 3 * ($bw + 4)) $by $bw $bh ([System.Drawing.Color]::FromArgb(170, 45, 35)) { Do-L4 } 'Quick-formats the selected partition. Requires typing BREAK.')
[void](New-Button "L5  DELETE partition`n(entirely)" (12 + 4 * ($bw + 4)) $by $bw $bh ([System.Drawing.Color]::FromArgb(140, 20, 30)) { Do-L5 } 'Deletes the selected partition. Requires typing BREAK.')

# Row 4: fix buttons
$lblFix = New-Label 'FIX' 12 176 200
$lblFix.ForeColor = [System.Drawing.Color]::LimeGreen; $lblFix.Font = $uiBold
$fy = 200
[void](New-Button "Undo L2`n(restore .bak files)" 12 $fy $bw $bh ([System.Drawing.Color]::FromArgb(40, 90, 120)) { Do-UndoL2 } 'Renames the .bak files back.')
[void](New-Button "Undo L3`n(boot order: add first)" (12 + 1 * ($bw + 4)) $fy $bw $bh ([System.Drawing.Color]::FromArgb(40, 90, 120)) { Do-UndoL3 } 'Puts Windows Boot Manager back at the top of the firmware boot order.')
[void](New-Button "Recreate EFI partition`n(after L5)" (12 + 2 * ($bw + 4)) $fy $bw $bh ([System.Drawing.Color]::FromArgb(40, 90, 120)) { Do-Recreate } 'Creates a new EFI System partition in the free space on the selected partition''s disk.')
[void](New-Button "REPAIR  (bcdboot)`nfixes L1 - L4" (12 + 3 * ($bw + 4)) $fy $bw $bh ([System.Drawing.Color]::FromArgb(30, 120, 60)) { Do-Repair } 'bcdboot <Windows folder> /s <letter>: /f UEFI - rebuilds files, BCD and firmware entry.')
[void](New-Button "Clear terminal" (12 + 4 * ($bw + 4)) $fy $bw $bh ([System.Drawing.Color]::FromArgb(70, 70, 80)) { $term.Clear() } $null)

# Terminal
$term = New-Object System.Windows.Forms.RichTextBox
$term.Location = New-Object System.Drawing.Point(12, 266)
$term.Size = New-Object System.Drawing.Size(1000, 440)
$term.Anchor = 'Top,Bottom,Left,Right'
$term.BackColor = [System.Drawing.Color]::FromArgb(12, 12, 12)
$term.ForeColor = $fg
$term.Font = $mono
$term.ReadOnly = $true
$term.BorderStyle = 'None'
$term.WordWrap = $false
$form.Controls.Add($term)

$form.Add_Shown({
    Write-Term 'EFI Breaker - for test machines and VMs only.' 'Tomato'
    $envName = if ($script:IsPE) { "WinPE (e.g. Hiren's BootCD PE) - running from $env:SystemDrive" } else { "full Windows on $env:COMPUTERNAME" }
    Write-Term "Environment: $envName" 'Gray'
    Write-Step 'Reading partitions'
    Update-PartitionList
    Update-WindowsList
    Write-Ok "$($script:Partitions.Count) partitions found. The EFI System partition is pre-selected."
    if ($cbWin.Items.Count) { Write-Ok ("Windows install(s) found: " + (@($cbWin.Items) -join ', ')) }
    else { Write-Note "No installed Windows found yet - Repair needs one. Press Refresh after mounting, or type the path." }
})

[void]$form.ShowDialog()
