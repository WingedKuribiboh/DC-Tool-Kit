<#
  Unlock-And-Disable.ps1 - unlock a BitLocker volume with its recovery password, then decrypt it.
  Run as admin.

  Workflow: on another machine, paste the 48-digit recovery password into key.txt (next to this script),
  then run this on the locked machine. key.txt is wiped when the script finishes, pass or fail.
  Usage: .\Unlock-And-Disable.ps1 [-Drive C:]
#>
param([string]$Drive = 'C:')

$keyFile = Join-Path $PSScriptRoot 'key.txt'
if (-not (Test-Path $keyFile)) { New-Item $keyFile -ItemType File | Out-Null }

try {
    $key = (Get-Content $keyFile -Raw -ErrorAction SilentlyContinue)
    $key = if ($key) { $key.Trim() } else { '' }
    if ($key -notmatch '^(\d{6}-){7}\d{6}$') {
        Write-Warning "key.txt is empty or not a valid recovery password (8 groups of 6 digits, dashes included)."
        return
    }

    manage-bde -unlock $Drive -RecoveryPassword $key
    if ($LASTEXITCODE -ne 0) { Write-Warning "Unlock failed (or volume was already unlocked). Check the key."; }

    Read-Host "Press Enter to START DECRYPTION of $Drive (Ctrl+C to abort)" | Out-Null
    manage-bde -off $Drive
    manage-bde -status $Drive
}
finally {
    # Overwrite, then truncate. Flash storage can't guarantee old blocks are gone, so keep the drive physically controlled.
    if (Test-Path $keyFile) {
        $len = (Get-Item $keyFile).Length
        if ($len -gt 0) { [IO.File]::WriteAllBytes($keyFile, (New-Object byte[] $len)) }
        Clear-Content $keyFile
        Write-Host "key.txt cleared." -ForegroundColor Green
    }
}
