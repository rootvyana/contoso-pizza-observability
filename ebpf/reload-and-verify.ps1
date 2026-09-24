# ELEVATED. Unloads the old program, loads the corrected one, re-runs the test.
$log = 'C:\Users\artha\Desktop\ContosoPizza\ebpf\reload-and-verify.log'
Start-Transcript -Path $log -Force | Out-Null
$ErrorActionPreference = 'Continue'
$bpftool = 'C:\Program Files\ebpf-for-windows\bpftool.exe'
$root = 'C:\Users\artha\Desktop\ContosoPizza\ebpf'

Write-Host "=== unload existing ==="
$existing = (netsh ebpf show programs) | Select-String -Pattern 'contoso_connection_monitor'
if ($existing) {
    $progId = ($existing.Line.Trim() -split '\s+')[0]
    Write-Host "deleting program id $progId"
    netsh ebpf delete program $progId
} else {
    Write-Host "(none loaded)"
}

Write-Host "`n=== load corrected build ==="
netsh ebpf add program "$root\contoso_sockops.o" execution=jit
Write-Host "exit code: $LASTEXITCODE"
netsh ebpf show programs

Write-Host "`n=== drive 10 requests over 127.0.0.1 ==="
& "$root\generate-traffic.ps1" -Requests 10
Start-Sleep -Seconds 2

Write-Host "`n=== decoded ==="
& "$root\watch.ps1"

Write-Host "`n=== cross-check: real TCP connections to 5176 right now ==="
Get-NetTCPConnection -LocalPort 5176 -ErrorAction SilentlyContinue |
    Where-Object { $_.State -ne 'Listen' } |
    Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,State,OwningProcess |
    Format-Table -AutoSize

Stop-Transcript | Out-Null
