# Builds the self-extracting toolkit packages (Windows):
#   dist\DC-Tool-Kit-<version>-windows.exe     double-click on Windows
#   dist\DC-Tool-Kit-<version>-mac.command     double-click on a Mac
# Both extract to the stick you pick and hide programs\ and system\.
#
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Version X] [-NoCompile]
#   (or double-click build.bat)
#   -NoCompile  use the committed exes instead of recompiling
# Compiling uses the csc.exe that ships with Windows (.NET Framework 4.x); no SDK needed.
param(
    [string]$Version = (Get-Date -Format 'yyyy.MM.dd'),
    [switch]$NoCompile
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$Root = $PSScriptRoot
$Sfx  = Join-Path $Root 'src\SelfExtractor'
$Dist = Join-Path $Root 'dist'
$Name = "DC-Tool-Kit-$Version"
New-Item -ItemType Directory -Force $Dist | Out-Null

function Invoke-Csc([string[]]$CscArgs) {
    & $script:csc @CscArgs
    if ($LASTEXITCODE) { throw "csc failed: $($CscArgs -join ' ')" }
}

if (-not $NoCompile) {
    $script:csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { $script:csc = Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    if (-not (Test-Path $csc)) { throw 'csc.exe not found - .NET Framework 4.x required (or use -NoCompile)' }
    Write-Host '== Compiling'
    & (Join-Path $Root 'src\Launchers\build.ps1')
    Invoke-Csc @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', "/out:$Root\UpdateToolkit.exe",
        "/win32manifest:$Root\src\UpdateToolkit\app.manifest",
        '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
        '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
        "$Root\src\UpdateToolkit\UpdateToolkit.cs")
    Write-Host 'Built UpdateToolkit.exe'
    Invoke-Csc @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', "/out:$Sfx\SelfExtractor.exe",
        "/win32manifest:$Sfx\app.manifest",
        '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.IO.Compression.dll',
        "$Sfx\SelfExtractor.cs")
    Write-Host 'Built src\SelfExtractor\SelfExtractor.exe'
}
if (-not (Test-Path "$Sfx\SelfExtractor.exe")) { throw 'src\SelfExtractor\SelfExtractor.exe missing; build without -NoCompile.' }

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
Write-Host '== Done'
Get-Item $win, $mac | ForEach-Object { Write-Host ("{0,8:N0} MB  {1}" -f ($_.Length / 1MB), $_.FullName) }
