# The loopback experiment, start to finish. Runs ELEVATED, logs everything.
# Question it answers: does the sockops hook fire for traffic on 127.0.0.1,
# or does loopback bypass the WFP ALE layers the hook sits on?
$ErrorActionPreference = 'Continue'
$log = Join-Path $PSScriptRoot 'loopback-test.log'
Start-Transcript -Path $log -Force | Out-Null

$verdict = 'INCONCLUSIVE'
try {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Not elevated.'
    }
    Write-Host "Running as $($id.Name), elevated."

    $obj = Join-Path $PSScriptRoot 'contoso_sockops.o'
    if (-not (Test-Path $obj)) { throw "$obj not found -- build first." }

    Write-Host "`n=== 1. Baseline: programs already loaded ==="
    netsh ebpf show programs

    Write-Host "`n=== 2. Load (JIT) ==="
    netsh ebpf add program $obj execution=jit
    if ($LASTEXITCODE -ne 0) { throw "load failed with exit code $LASTEXITCODE" }
    netsh ebpf show programs

    Write-Host "`n=== 3. Confirm app is listening on IPv4 loopback ==="
    Get-NetTCPConnection -State Listen -LocalPort 5176 -ErrorAction SilentlyContinue |
        Select-Object LocalAddress, LocalPort, OwningProcess | Format-Table -AutoSize

    Write-Host "`n=== 4. Drive traffic over 127.0.0.1 (IPv4 forced) ==="
    & (Join-Path $PSScriptRoot 'generate-traffic.ps1') -Requests 20 -BaseUrl 'http://127.0.0.1:5176'
    Start-Sleep -Seconds 2

    Write-Host "`n=== 5. Raw map contents ==="
    $bpftool = 'C:\Program Files\ebpf-for-windows\bpftool.exe'
    Write-Host '--- flow_map ---';  & $bpftool map dump name flow_map
    Write-Host '--- stats_map ---'; & $bpftool map dump name stats_map

    Write-Host "`n=== 6. Decoded ==="
    & (Join-Path $PSScriptRoot 'watch.ps1')

    # Verdict: did anything at all land in the maps?
    $dump = (& $bpftool --json map dump name flow_map 2>&1 | Out-String)
    $parsed = $null
    try { $parsed = $dump | ConvertFrom-Json } catch { }
    if ($parsed -and @($parsed).Count -gt 0) {
        $verdict = 'LOOPBACK VISIBLE -- sockops fires on 127.0.0.1'
    } else {
        $verdict = 'LOOPBACK NOT VISIBLE -- rebind to 0.0.0.0 and retry over the LAN address'
    }
}
catch {
    $verdict = "FAILED: $($_.Exception.Message)"
    Write-Host $verdict -ForegroundColor Red
}

Write-Host "`n=== VERDICT ===`n$verdict"
Stop-Transcript | Out-Null
