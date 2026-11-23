<#
  Disk-Health.ps1 - quick SMART-style health and reliability counters for all physical disks.
  Run as admin. For deep analysis use Tools\Storage\hdsentinel_pro_portable.
#>
Get-PhysicalDisk | ForEach-Object {
    $d = $_
    $r = $d | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Disk        = $d.FriendlyName
        Type        = "$($d.MediaType)/$($d.BusType)"
        SizeGB      = [math]::Round($d.Size / 1GB)
        Health      = $d.HealthStatus
        TempC       = $r.Temperature
        WearPct     = $r.Wear
        PowerOnHrs  = $r.PowerOnHours
        ReadErrors  = $r.ReadErrorsTotal
        WriteErrors = $r.WriteErrorsTotal
    }
} | Format-Table -AutoSize

Write-Host "Anything other than Health=Healthy, or growing error counts, means back up and replace the drive." -ForegroundColor Yellow
