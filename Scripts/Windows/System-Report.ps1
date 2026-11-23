<#
  System-Report.ps1 - one-page hardware/OS/software inventory for the machine it is run on.
  Run as admin. Output: <drive>\Reports\<COMPUTERNAME>_<date>\system-report.txt
#>
$out = Join-Path $PSScriptRoot "..\..\Reports\$($env:COMPUTERNAME)_$(Get-Date -Format yyyyMMdd-HHmm)"
New-Item -ItemType Directory -Force $out | Out-Null
$file = Join-Path $out 'system-report.txt'

& {
    "=== SYSTEM ==="
    Get-CimInstance Win32_ComputerSystem | Format-List Manufacturer, Model, Name, Domain, TotalPhysicalMemory
    Get-CimInstance Win32_BIOS | Format-List SMBIOSBIOSVersion, SerialNumber, ReleaseDate
    Get-CimInstance Win32_OperatingSystem | Format-List Caption, Version, BuildNumber, InstallDate, LastBootUpTime
    "=== CPU / GPU ==="
    Get-CimInstance Win32_Processor | Format-List Name, NumberOfCores, NumberOfLogicalProcessors
    Get-CimInstance Win32_VideoController | Format-List Name, DriverVersion
    "=== DISKS ==="
    Get-PhysicalDisk | Format-Table FriendlyName, MediaType, BusType, @{n='GB';e={[math]::Round($_.Size/1GB)}}, HealthStatus, OperationalStatus -AutoSize
    Get-Volume | Where-Object DriveLetter | Format-Table DriveLetter, FileSystemLabel, FileSystem, @{n='SizeGB';e={[math]::Round($_.Size/1GB,1)}}, @{n='FreeGB';e={[math]::Round($_.SizeRemaining/1GB,1)}} -AutoSize
    "=== NETWORK ==="
    Get-NetIPConfiguration | Format-List InterfaceAlias, IPv4Address, IPv4DefaultGateway, DNSServer
    "=== SECURITY ==="
    try { Get-BitLockerVolume | Format-Table MountPoint, VolumeStatus, ProtectionStatus, EncryptionPercentage -AutoSize } catch { "BitLocker query failed: $_" }
    try { Get-MpComputerStatus | Format-List AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureLastUpdated } catch { "Defender query failed: $_" }
    try { "Secure Boot: " + (Confirm-SecureBootUEFI) } catch { "Secure Boot: not available (legacy BIOS or no access)" }
    "=== INSTALLED SOFTWARE ==="
    Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object DisplayName | Sort-Object DisplayName | Format-Table DisplayName, DisplayVersion, Publisher -AutoSize
} 2>&1 | Out-String -Width 220 | Set-Content -Path $file -Encoding UTF8

Write-Host "Report saved to $file" -ForegroundColor Green
