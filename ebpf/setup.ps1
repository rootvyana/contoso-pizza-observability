# Run once, from a normal (non-elevated) prompt.
#
# Installs the toolchain needed to compile contoso_sockops.c. It does NOT install the
# eBPF for Windows runtime -- that is a prerequisite, it needs elevation, and which
# build you install decides whether an unsigned program can load at all. See
# ebpf/README.md "0. The runtime".

$ErrorActionPreference = 'Stop'

# Say so up front rather than letting build.ps1 succeed and load.ps1 fail with an
# errno the caller has to go and look up.
$core = Get-Service eBPFCore -ErrorAction SilentlyContinue
if (-not $core) {
    Write-Host ''
    Write-Host 'eBPF for Windows does not appear to be installed.' -ForegroundColor Yellow
    Write-Host 'This script installs the build toolchain only. Install the runtime from'
    Write-Host '  https://github.com/microsoft/ebpf-for-windows/releases  (v1.5.0)'
    Write-Host 'and read ebpf/README.md "0. The runtime" first -- the MSI cannot load an'
    Write-Host 'unsigned .o, which is what build.ps1 produces.' -ForegroundColor Yellow
    Write-Host ''
} elseif ($core.Status -ne 'Running') {
    Write-Host "eBPFCore is installed but $($core.Status). Start it before load.ps1." -ForegroundColor Yellow
}

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
