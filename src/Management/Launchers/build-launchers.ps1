<#
  build-launchers.ps1 - rebuilds every launcher in Tools\ from launchers.csv.
  Uses the same launcher code as LauncherMaker_v2.exe and the csc.exe that ships with Windows.

  launchers.csv columns:
    Launcher  path of the launcher, relative to Tools\
    Target    program to open, relative to the drive root (works on any drive letter)
    Icon      .ico file in icons\ to give the launcher
  To add a launcher, add a row (and its icon) and run Build-Launchers.cmd.
#>
$ErrorActionPreference = 'Stop'
$here  = $PSScriptRoot
$root  = (Resolve-Path (Join-Path $here '..\..\..')).Path
$tools = Join-Path $root 'Tools'

$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found - .NET Framework 4.x required' }

$template = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
static class Launcher {
    [STAThread] static void Main() {
        try {
            string root = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
            string target = Path.GetFullPath(Path.Combine(root, @"__REL__"));
            ProcessStartInfo psi = new ProcessStartInfo(target);
            psi.WorkingDirectory = Path.GetDirectoryName(target);
            psi.UseShellExecute = true;
            Process.Start(psi);
        } catch (Exception e) { MessageBox.Show(e.Message, "Launcher"); }
    }
}
'@

$tmp = Join-Path $env:TEMP ('launchers-' + [guid]::NewGuid())
New-Item $tmp -ItemType Directory | Out-Null
$ok = 0; $failed = 0
try {
    foreach ($row in Import-Csv (Join-Path $here 'launchers.csv')) {
        $out = Join-Path $tools $row.Launcher
        $src = Join-Path $tmp 'Launcher.cs'
        $template.Replace('__REL__', $row.Target.Replace('"', '""')) | Set-Content $src -Encoding UTF8
        New-Item (Split-Path $out) -ItemType Directory -Force | Out-Null
        $cscArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', "/out:$out", '/r:System.dll', '/r:System.Windows.Forms.dll')
        $ico = Join-Path $here ('icons\' + $row.Icon)
        if ($row.Icon -and (Test-Path $ico)) { $cscArgs += "/win32icon:$ico" }
        & $csc @args $src
        if ($LASTEXITCODE -eq 0) { Write-Host "OK     $($row.Launcher) -> $($row.Target)"; $ok++ }
        else { Write-Host "FAILED $($row.Launcher)" -ForegroundColor Red; $failed++ }
    }
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "`n$ok built, $failed failed."
if ($failed) { exit 1 }
