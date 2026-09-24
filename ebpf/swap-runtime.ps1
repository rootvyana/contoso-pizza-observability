# ELEVATED. Replaces the NativeOnly MSI runtime with the JIT-capable build.
# Reverse with:  cd C:\ebpf-jit-1.5.0; .\setup-ebpf.ps1 -Uninstall
#                msiexec /i C:\ebpf-jit-1.5.0\ROLLBACK-ebpf-for-windows.x64.1.5.0.msi
$log = 'C:\Users\artha\Desktop\ContosoPizza\ebpf\swap-runtime.log'
Start-Transcript -Path $log -Force | Out-Null
$ErrorActionPreference = 'Continue'

# Fail loudly rather than half-running: everything below needs admin.
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "NOT ELEVATED. Right-click swap-runtime.cmd -> Run as administrator." -ForegroundColor Red
    Stop-Transcript | Out-Null
    exit 1
}
Write-Host "Running as $($id.Name), elevated."

$stage = 'C:\ebpf-jit-1.5.0'

Write-Host "=== 1. Uninstall the MSI runtime ==="
$msi = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -EA SilentlyContinue |
    Where-Object { $_.DisplayName -like '*eBPF for Windows*' } | Select-Object -First 1
if ($msi) {
    Write-Host "Found $($msi.DisplayName) $($msi.DisplayVersion), product code $($msi.PSChildName)"
    Start-Process msiexec.exe -ArgumentList "/x $($msi.PSChildName) /qn /norestart" -Wait
    Write-Host "msiexec finished"
} else {
    Write-Host "No MSI registration found; skipping"
}

Write-Host "`n=== 2. Services after uninstall ==="
Get-Service eBPFCore, NetEbpfExt, EbpfSvc -EA SilentlyContinue |
    Select-Object Name, Status | Format-Table -AutoSize

Write-Host "`n=== 3. Install the JIT-capable build ==="
Push-Location $stage
& "$stage\setup-ebpf.ps1"
Pop-Location

Write-Host "`n=== 4. Services after install ==="
Get-Service eBPFCore, NetEbpfExt, EbpfSvc -EA SilentlyContinue |
    Select-Object Name, Status, StartType | Format-Table -AutoSize

Write-Host "`n=== 5. Driver actually in use ==="
foreach ($p in 'C:\Windows\System32\drivers\EbpfCore.sys', 'C:\Windows\System32\EbpfCore.sys') {
    if (Test-Path $p) { Write-Host ("{0} : {1} KB" -f $p, [math]::Round((Get-Item $p).Length/1KB,1)) }
}

Write-Host "`n=== 6. THE TEST: load in JIT mode ==="
$obj = 'C:\Users\artha\Desktop\ContosoPizza\ebpf\contoso_sockops.o'
$netsh = 'netsh'
& $netsh ebpf add program $obj execution=jit
Write-Host "exit code: $LASTEXITCODE"
& $netsh ebpf show programs

Stop-Transcript | Out-Null
