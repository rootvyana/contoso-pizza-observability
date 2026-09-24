# Compiles contoso_sockops.c to eBPF bytecode. Non-elevated is fine.
$ErrorActionPreference = 'Stop'

$clang = Get-Command clang -ErrorAction SilentlyContinue
if (-not $clang) {
    $fallback = 'C:\Program Files\LLVM\bin\clang.exe'
    if (Test-Path $fallback) { $clang = $fallback } else { throw 'clang not found. Run .\setup.ps1 first.' }
} else {
    $clang = $clang.Source
}

$repo = Join-Path $PSScriptRoot 'ebpf-for-windows'
if (-not (Test-Path $repo)) { throw "Headers missing at $repo. Run .\setup.ps1 first." }

$src = Join-Path $PSScriptRoot 'contoso_sockops.c'
$out = Join-Path $PSScriptRoot 'contoso_sockops.o'

# -target bpf is what makes clang emit eBPF bytecode rather than x64.
& $clang -target bpf -O2 -g -Werror `
    -I "$repo\include" `
    -c $src -o $out

if ($LASTEXITCODE -ne 0) { throw "clang failed with exit code $LASTEXITCODE" }
Write-Host "Built $out" -ForegroundColor Green
