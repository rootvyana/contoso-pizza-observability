# Run LATER, once, from a normal (non-elevated) prompt.
# Installs the toolchain needed to compile contoso_sockops.c. Nothing here
# touches the eBPF runtime itself -- that is already installed and running.

$ErrorActionPreference = 'Stop'

# ~2.5 GB. Provides clang, which is the only supported eBPF front end.
winget install --id LLVM.LLVM --source winget --accept-package-agreements --accept-source-agreements

# Headers only (bpf_helpers.h, ebpf_nethooks.h). The binaries are already
# installed under C:\Program Files\ebpf-for-windows.
$repo = Join-Path $PSScriptRoot 'ebpf-for-windows'
if (-not (Test-Path $repo)) {
    git clone --depth 1 https://github.com/microsoft/ebpf-for-windows.git $repo
}

Write-Host ''
Write-Host 'Toolchain ready. Next: .\build.ps1' -ForegroundColor Green
Write-Host 'If clang is not found, open a new terminal so PATH refreshes.' -ForegroundColor Yellow
