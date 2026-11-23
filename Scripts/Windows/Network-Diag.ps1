<#
  Network-Diag.ps1 - walks gateway -> external IP -> DNS -> HTTPS so you can see where connectivity breaks.
#>
$gw = (Get-NetRoute -DestinationPrefix 0.0.0.0/0 | Sort-Object RouteMetric | Select-Object -First 1).NextHop
$tests = @(
    @{ Name = 'Default gateway';        Host = $gw },
    @{ Name = 'Internet by IP (1.1.1.1)'; Host = '1.1.1.1' },
    @{ Name = 'Internet by IP (8.8.8.8)'; Host = '8.8.8.8' }
)
foreach ($t in $tests) {
    $ok = if ($t.Host) { Test-Connection $t.Host -Count 2 -Quiet } else { $false }
    "{0,-28} {1,-16} {2}" -f $t.Name, $t.Host, $(if ($ok) { 'OK' } else { 'FAIL' })
}
try { Resolve-DnsName example.com -ErrorAction Stop | Out-Null; "{0,-28} {1}" -f 'DNS resolution', 'OK' } catch { "{0,-28} {1}" -f 'DNS resolution', 'FAIL' }
try { $s = (Invoke-WebRequest https://example.com -UseBasicParsing -TimeoutSec 10).StatusCode; "{0,-28} {1}" -f 'HTTPS (example.com)', "OK ($s)" } catch { "{0,-28} {1}" -f 'HTTPS (example.com)', 'FAIL' }
""
Get-NetIPConfiguration | Format-List InterfaceAlias, IPv4Address, IPv4DefaultGateway, DNSServer
