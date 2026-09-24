# ELEVATED. End-to-end verification of the on-prem half: probe -> collector.
#
# Runs every check in dependency order and stops at the first one that matters,
# so a failure points at one cause rather than a pile of symptoms.
#
#   1. elevation
#   2. eBPF program loaded          (does not survive reboot)
#   3. probe interop resolves       (--exports)
#   4. kernel maps readable         (--dump)
#   5. collector reachable and authenticated to the cloud  (--diagnose)
#   6. probe can reach the collector (--check)
#   7. live traffic produces flows

$ErrorActionPreference = 'Continue'
$root      = Split-Path $PSScriptRoot -Parent
$probe     = Join-Path $PSScriptRoot 'ContosoPizza.Probe'
$collector = Join-Path $PSScriptRoot 'ContosoPizza.Collector'
$fail      = 0

function Step($n, $text) { Write-Host "`n[$n] $text" -ForegroundColor Cyan }
function Ok($text)       { Write-Host "    OK    $text" -ForegroundColor Green }
function Bad($text)      { Write-Host "    FAIL  $text" -ForegroundColor Red; $script:fail++ }
function Info($text)     { Write-Host "    ...   $text" -ForegroundColor DarkGray }

Write-Host "ContosoPizza fleet telemetry verification" -ForegroundColor White
Write-Host ("=" * 62)

# ---- 1. elevation ----------------------------------------------------------
Step 1 'Elevation'
$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Ok 'running as administrator'
} else {
    Bad 'NOT elevated. Right-click verify.cmd -> Run as administrator.'
    Write-Host "`nThe probe cannot read the kernel maps without this." -ForegroundColor Red
    exit 1
}

# ---- 2. eBPF program -------------------------------------------------------
Step 2 'eBPF program loaded'
$programs = netsh ebpf show programs 2>&1 | Out-String
if ($programs -match 'contoso_connection_monitor') {
    $line = ($programs -split "`n" | Where-Object { $_ -match 'contoso_connection_monitor' })[0].Trim()
    Ok $line
} else {
    Bad 'contoso_connection_monitor is not loaded.'
    Info 'The program does not survive a reboot. Load it with:'
    Info "    cd $root\ebpf; .\load.ps1"
    Write-Host "`nStopping: the probe has nothing to read." -ForegroundColor Red
    exit 1
}

# ---- 3. native interop -----------------------------------------------------
Step 3 'EbpfApi.dll interop'
Push-Location $probe
$exports = dotnet run -- --exports 2>&1 | Out-String
Pop-Location
if ($exports -match 'All required exports resolved') {
    Ok 'all 7 exports resolved'
} else {
    Bad 'export resolution failed'
    $exports -split "`n" | Where-Object { $_ -match 'MISS|FAILED' } | ForEach-Object { Info $_.Trim() }
    exit 1
}

# ---- 4. read the maps ------------------------------------------------------
Step 4 'Reading the kernel maps'
Push-Location $probe
$dump = dotnet run -- --dump 2>&1 | Out-String
Pop-Location

if ($dump -match 'FAILED') {
    Bad 'could not read the maps'
    $dump -split "`n" | Where-Object { $_ -match 'FAILED' } | ForEach-Object { Info $_.Trim() }
    exit 1
}
if ($dump -match 'Attached to flow_map') {
    Ok 'attached to flow_map and stats_map'
    ($dump -split "`n" | Where-Object { $_ -match 'flow_map\s+:' }) | ForEach-Object { Info $_.Trim() }
} else {
    Bad 'did not attach to the maps'
    Write-Host $dump
    exit 1
}

# ---- 5. the collector's link to the cloud ----------------------------------
Step 5 'Collector -> loveheartbeat'
Push-Location $collector
$diagnose = dotnet run -- --diagnose 2>&1 | Out-String
Pop-Location
if ($diagnose -match 'All checks passed') {
    Ok 'authenticated and reachable'
    ($diagnose -split "`n" | Where-Object { $_ -match 'collector id' }) | ForEach-Object { Info $_.Trim() }
} else {
    Bad 'the collector could not authenticate to the cloud'
    $diagnose -split "`n" |
        Where-Object { $_ -match 'FAILED|not set|enrolled|->' } |
        ForEach-Object { Info $_.Trim() }
    Info 'Continuing: the probe -> collector hop can still be checked.'
}

# ---- 6. the probe's link to the collector ----------------------------------
Step 6 'Probe -> collector'
Push-Location $probe
$check = dotnet run -- --check 2>&1 | Out-String
Pop-Location
if ($check -match 'PASS') {
    Ok 'the collector accepted this probe'
} else {
    Bad 'the probe cannot reach its collector'
    $check -split "`n" | Where-Object { $_ -match 'FAIL|collector' } | ForEach-Object { Info $_.Trim() }
    Info 'Is the collector running? Does CONTOSO_PROBE_KEY match on both sides?'
}

# ---- 7. live traffic -------------------------------------------------------
Step 7 'Live traffic produces flows'
$app = Get-Process ContosoPizza -ErrorAction SilentlyContinue
if ($app) {
    Ok "app running as PID $($app.Id)"
    Info 'driving 10 requests'
    & (Join-Path $root 'ebpf\generate-traffic.ps1') -Requests 10 | Out-Null
    Start-Sleep -Seconds 3

    Push-Location $probe
    $after = dotnet run -- --dump 2>&1 | Out-String
    Pop-Location

    Write-Host ''
    $after -split "`n" |
        Where-Object { $_ -match '127\.0\.0\.1|LOCAL|^-{10}|established|closed|currently|flow_map' } |
        ForEach-Object { Write-Host "    $($_.TrimEnd())" }

    if ($after -match '127\.0\.0\.1:5176') {
        Ok 'flows observed for the app listener'
    } else {
        Bad 'no flows for port 5176 after driving traffic'
        Info 'A previous --dump evicts closed flows, so run traffic again if this looks empty.'
    }
} else {
    Info 'app not running; skipped'
    Info "Start it with:  cd $root; dotnet run --launch-profile http"
}

# ---- summary ---------------------------------------------------------------
Write-Host "`n$('=' * 62)"
if ($fail -eq 0) {
    Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green
    Write-Host ''
    Write-Host 'Next: leave the collector running and start the probe.' -ForegroundColor White
    Write-Host '    .\run-collector.cmd      (no elevation)'
    Write-Host '    .\run-probe.cmd          (as administrator)'
} else {
    Write-Host "$fail CHECK(S) FAILED" -ForegroundColor Red
}
Write-Host ''
