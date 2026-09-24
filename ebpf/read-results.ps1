# ELEVATED. The program is already loaded; this drives traffic and reads the maps.
$log = Join-Path $PSScriptRoot 'read-results.log'
Start-Transcript -Path $log -Force | Out-Null
$ErrorActionPreference = 'Continue'
$bpftool = 'C:\Program Files\ebpf-for-windows\bpftool.exe'

Write-Host "=== loaded programs ==="
netsh ebpf show programs

Write-Host "`n=== driving 12 requests over 127.0.0.1 ==="
& (Join-Path $PSScriptRoot 'generate-traffic.ps1') -Requests 12
Start-Sleep -Seconds 2

Write-Host "`n=== raw flow_map ==="
& $bpftool map dump name flow_map
Write-Host "`n=== raw stats_map ==="
& $bpftool map dump name stats_map

Write-Host "`n=== decoded ==="
& (Join-Path $PSScriptRoot 'watch.ps1')

Write-Host "`n=== VERDICT ==="
$dump = (& $bpftool --json map dump name flow_map 2>&1 | Out-String)
$parsed = $null
try { $parsed = $dump | ConvertFrom-Json } catch { }
if ($parsed -and @($parsed).Count -gt 0) {
    Write-Host "LOOPBACK VISIBLE: sockops fires on 127.0.0.1 ($(@($parsed).Count) flows recorded)"
} else {
    Write-Host "LOOPBACK NOT VISIBLE: no flows recorded despite successful requests"
}
Stop-Transcript | Out-Null
