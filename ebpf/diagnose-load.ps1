# ELEVATED. Establishes exactly which execution modes this install supports.
$log = Join-Path $PSScriptRoot 'diagnose-load.log'
Start-Transcript -Path $log -Force | Out-Null
$ErrorActionPreference = 'Continue'

$obj = Join-Path $PSScriptRoot 'contoso_sockops.o'

Write-Host "=== eBPF version ==="
(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -EA SilentlyContinue |
    Where-Object { $_.DisplayName -like '*eBPF*' } | Select-Object DisplayName, DisplayVersion | Out-String).Trim()

foreach ($mode in 'jit', 'interpret', 'native') {
    Write-Host "`n=== execution=$mode ==="
    netsh ebpf add program $obj execution=$mode
    Write-Host "exit code: $LASTEXITCODE"
}

Write-Host "`n=== loaded after attempts ==="
netsh ebpf show programs

Write-Host "`n=== test signing / code integrity ==="
bcdedit /enum '{current}' | Select-String -Pattern 'testsigning|nointegritychecks|flightsigning'
Write-Host "(absent = testsigning OFF)"

Write-Host "`n=== is bpf2c present for the native path? ==="
$bpf2c = 'C:\Program Files\ebpf-for-windows\bpf2c.exe'
if (Test-Path $bpf2c) { Write-Host "bpf2c OK: $bpf2c" } else { Write-Host 'bpf2c MISSING' }

Write-Host "`n=== WDK / MSBuild present for building a .sys? ==="
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) { & $vswhere -latest -property displayName } else { Write-Host 'vswhere MISSING (no Visual Studio)' }
if (Test-Path 'C:\Program Files (x86)\Windows Kits\10\build') { Write-Host 'Windows Kits present' } else { Write-Host 'Windows Kits MISSING' }

Stop-Transcript | Out-Null
