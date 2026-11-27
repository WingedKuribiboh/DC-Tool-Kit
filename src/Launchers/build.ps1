# Rebuilds every Tools\ launcher listed in launchers.txt using the C# compiler that ships with Windows.
# Each launcher gets the icon named in the third column (from src\Icons), or the toolkit icon if blank.
# Run via build.bat (double-click) or: powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found - .NET Framework 4.x required' }

$tools = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\Tools'))
$icons = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Icons'))
$defaultIcon = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Icon\DCToolKit.ico'))
$src   = Get-Content -Raw (Join-Path $PSScriptRoot 'Launcher.cs')
$tmp   = Join-Path $env:TEMP 'toolkit-launcher.cs'
$n = 0
foreach ($line in Get-Content (Join-Path $PSScriptRoot 'launchers.txt')) {
    if ($line -match '^\s*#' -or $line -notmatch '\|') { continue }
    $parts = $line.Split([char[]]'|', 3) | ForEach-Object { $_.Trim() }
    $name, $target = $parts[0], $parts[1]
    $icon = if ($parts.Count -gt 2) { $parts[2] } else { '' }
    if ($icon) {
        $ico = Join-Path $icons $icon
        if (-not (Test-Path $ico)) { throw "Icon not found for ${name}: src\Icons\$icon" }
    } else { $ico = $defaultIcon }
    $out = Join-Path $tools $name
    New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
    Set-Content -Encoding UTF8 $tmp $src.Replace('@TARGET@', $target)
    & $csc /nologo /target:winexe /platform:anycpu /optimize+ "/out:$out" "/win32icon:$ico" /r:System.Windows.Forms.dll $tmp | Out-Null
    if ($LASTEXITCODE) { throw "BUILD FAILED: $name" }
    $label = if ($icon) { $icon } else { 'DCToolKit.ico' }
    Write-Host "  $name  ->  $target  [$label]"
    $n++
}
Remove-Item $tmp
Write-Host "Built $n launchers in $tools"
