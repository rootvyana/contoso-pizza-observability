# MUST run from an ELEVATED PowerShell prompt.
# Loads the program in JIT mode -- no driver signing, no test-signing reboot.
$ErrorActionPreference = 'Stop'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Not elevated. Re-open PowerShell as Administrator and run this again.'
}

$obj = Join-Path $PSScriptRoot 'contoso_sockops.o'
if (-not (Test-Path $obj)) { throw "$obj not found. Run .\build.ps1 first." }

netsh ebpf add program $obj execution=jit
if ($LASTEXITCODE -ne 0) { throw "netsh ebpf add program failed ($LASTEXITCODE)" }

Write-Host ''
Write-Host 'Loaded programs:' -ForegroundColor Green
netsh ebpf show programs
Write-Host ''
Write-Host 'Now generate traffic, then run .\watch.ps1' -ForegroundColor Cyan
