@echo off
REM Right-click -> Run as administrator.
REM Reading eBPF maps needs the same rights bpftool does.
cd /d "%~dp0ContosoPizza.Probe"
dotnet run -c Release
pause
