# Builds the self-extracting toolkit packages (Windows):
#   dist\DC-Tool-Kit-<version>-windows.exe     double-click on Windows
#   dist\DC-Tool-Kit-<version>-mac.command     double-click on a Mac
# Both extract to the stick you pick, hide programs\, system\ and ventoy\, rename the drive and give it
# the toolkit icon (src\Icon\DCToolKit.ico for Windows, DCToolKit.icns for macOS).
#
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Version X] [-Label NAME] [-NoCompile]
#   (or double-click build.bat)
#   -Label      drive name set by the installers (default DC-TOOLKIT; FAT32 max 11 chars, exFAT 15)
#   -NoCompile  reuse the committed launchers and extractor stub instead of rebuilding them
# Uses the csc.exe that ships with Windows (.NET Framework 4.x); no SDK needed. UpdateToolkit.exe is not in
# the repo: it is always built from src\UpdateToolkit into dist\build, then packed at the stick root.
# UpdateToolkit.app (the Mac window) needs Swift on a Mac, so a Windows build packs without it; Mac users
# then use system/Update/UpdateToolkit.command. Build on a Mac (build.sh) to include the app.
param(
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd'),
    [string]$Label = 'DC-TOOLKIT',
    [switch]$NoCompile
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$Root = $PSScriptRoot
$Sfx  = Join-Path $Root 'src\SelfExtractor'
$Dist = Join-Path $Root 'dist'
$Name = "DC-Tool-Kit-$Version"
$Build = Join-Path $Dist 'build'
New-Item -ItemType Directory -Force $Dist | Out-Null
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $Build
New-Item -ItemType Directory -Force $Build | Out-Null

function Invoke-Csc([string[]]$CscArgs) {
    & $script:csc @CscArgs
    if ($LASTEXITCODE) { throw "csc failed: $($CscArgs -join ' ')" }
}

$script:csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $script:csc = Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found - .NET Framework 4.x required' }
Write-Host '== Compiling'
Invoke-Csc @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', "/out:$Build\UpdateToolkit.exe",
    "/win32manifest:$Root\src\UpdateToolkit\app.manifest", "/win32icon:$Root\src\Icon\DCToolKit.ico",
    '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
    "$Root\src\UpdateToolkit\UpdateToolkit.cs")
Write-Host 'Built dist\build\UpdateToolkit.exe (from src\UpdateToolkit\UpdateToolkit.cs)'
Write-Warning 'UpdateToolkit.app can only be built on a Mac (build.sh); this package will not include it.'
if (-not $NoCompile) {
    & (Join-Path $Root 'src\Launchers\build.ps1')
    Invoke-Csc @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', "/out:$Root\Tools\NetUserManager.exe",
        "/win32manifest:$Root\src\net-user-gui\app.manifest",
        '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
        "$Root\src\net-user-gui\NetUserManager.cs")
    Write-Host 'Built Tools\NetUserManager.exe (from src\net-user-gui)'
    Invoke-Csc @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', "/out:$Sfx\SelfExtractor.exe",
        "/win32manifest:$Sfx\app.manifest", "/win32icon:$Root\src\Icon\DCToolKit.ico",
        '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.IO.Compression.dll',
        "$Sfx\SelfExtractor.cs")
    Write-Host 'Built src\SelfExtractor\SelfExtractor.exe'
}
if (-not (Test-Path "$Sfx\SelfExtractor.exe")) { throw 'src\SelfExtractor\SelfExtractor.exe missing; build without -NoCompile.' }

# Refresh the CA bundle the updater uses for HTTPS (system\Update\cacert.pem); keep the old one if offline.
$ca = Join-Path $Root 'system\Update\cacert.pem'
try {
    [Net.ServicePointManager]::SecurityProtocol = 'Tls12'
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 30 'https://curl.se/ca/cacert.pem' -OutFile "$ca.new"
    if (Select-String -Quiet 'BEGIN CERTIFICATE' "$ca.new") { Move-Item -Force "$ca.new" $ca; Write-Host "Refreshed $ca" }
} catch { Write-Host "Could not refresh $ca; packing the existing one." }
Remove-Item -ErrorAction SilentlyContinue "$ca.new"

Write-Host '== Packing'
# Same exclusions as build.sh.
$excludeNames = @('.DS_Store', 'Thumbs.db', 'desktop.ini', '.gitkeep', '_geexlab_log.txt', 'key.txt')
$excludeExt   = @('.iso', '.part', '.pdb', '.pyc')
function Test-Excluded([string]$rel) {
    $leaf = Split-Path $rel -Leaf
    if ($excludeNames -contains $leaf -or $leaf.StartsWith('._')) { return $true }
    if ($excludeExt -contains [IO.Path]::GetExtension($leaf).ToLowerInvariant()) { return $true }
    if ($rel -match '(^|/)__pycache__(/|$)' -or $rel -like 'system/Update/.bin*') { return $true }
    return $false
}

$items = Get-Content "$Sfx\payload.txt" | ForEach-Object { ($_ -replace '#.*$', '').Trim() } | Where-Object { $_ }
Add-Type -AssemblyName System.IO.Compression
$payload = Join-Path $Dist "$Name-payload.zip"
Remove-Item -Force -ErrorAction SilentlyContinue $payload
$fs  = [IO.File]::Open($payload, 'Create')
$zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
$count = 0
try {
    foreach ($item in $items) {
        $full = Join-Path $Root $item
        if (-not (Test-Path $full)) { throw "Missing payload item: $item" }
        $entries = @(Get-Item -Force $full)
        if ($entries[0].PSIsContainer) { $entries += Get-ChildItem -Force -Recurse $full }
        foreach ($f in $entries) {
            # Entry names use forward slashes so macOS unzip and the Windows extractor agree.
            $rel = $f.FullName.Substring($Root.Length).TrimStart('\').Replace('\', '/')
            if (Test-Excluded $rel) { continue }
            if ($f.PSIsContainer) { [void]$zip.CreateEntry("$rel/"); continue }
            $e = $zip.CreateEntry($rel, [IO.Compression.CompressionLevel]::Optimal)
            $e.LastWriteTime = $f.LastWriteTime
            $in = $f.OpenRead(); $out = $e.Open()
            try { $in.CopyTo($out) } finally { $out.Dispose(); $in.Dispose() }
            $count++
        }
    }
    # Drive-root files: autorun.inf (Windows drive name + icon), DCToolKit.ico, .VolumeIcon.icns (Mac icon).
    $driveFiles = @{
        'autorun.inf'      = [Text.Encoding]::ASCII.GetBytes("[autorun]`r`nicon=DCToolKit.ico`r`nlabel=$Label`r`n")
        'DCToolKit.ico'    = [IO.File]::ReadAllBytes("$Root\src\Icon\DCToolKit.ico")
        '.VolumeIcon.icns' = [IO.File]::ReadAllBytes("$Root\src\Icon\DCToolKit.icns")
        'UpdateToolkit.exe' = [IO.File]::ReadAllBytes("$Build\UpdateToolkit.exe")   # built above, never taken from the repo
    }
    foreach ($k in $driveFiles.Keys) {
        $out = $zip.CreateEntry($k, [IO.Compression.CompressionLevel]::Optimal).Open()
        try { $out.Write($driveFiles[$k], 0, $driveFiles[$k].Length) } finally { $out.Dispose() }
        $count++
    }
    Write-Host "Drive name: $Label"
} finally { $zip.Dispose(); $fs.Dispose() }
Write-Host ("Payload: {0:N0} MB ({1} files)" -f ((Get-Item $payload).Length / 1MB), $count)

function Join-Files([string]$out, [string[]]$parts, [byte[]]$trailer) {
    $o = [IO.File]::Open($out, 'Create')
    try {
        foreach ($p in $parts) { $i = [IO.File]::OpenRead($p); try { $i.CopyTo($o) } finally { $i.Dispose() } }
        if ($trailer) { $o.Write($trailer, 0, $trailer.Length) }
    } finally { $o.Dispose() }
}

# Windows: [stub exe][zip][int64 LE offset of the zip]["DCTKSFX1"]
$win = Join-Path $Dist "$Name-windows.exe"
$offset = (Get-Item "$Sfx\SelfExtractor.exe").Length
$trailer = [BitConverter]::GetBytes([long]$offset) + [Text.Encoding]::ASCII.GetBytes('DCTKSFX1')
Join-Files $win @("$Sfx\SelfExtractor.exe", $payload) $trailer

# macOS: [bash header][zip]. unzip on the Mac finds the zip after the header.
$mac = Join-Path $Dist "$Name-mac.command"
Join-Files $mac @("$Sfx\mac-header.sh", $payload) $null

Remove-Item $payload
Remove-Item -Recurse -Force $Build
Write-Host '== Done'
Get-Item $win, $mac | ForEach-Object { Write-Host ("{0,8:N0} MB  {1}" -f ($_.Length / 1MB), $_.FullName) }
