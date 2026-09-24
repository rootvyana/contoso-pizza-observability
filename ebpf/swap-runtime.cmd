@echo off
REM Right-click this file -> "Run as administrator"
setlocal
echo Running eBPF runtime swap...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0swap-runtime.ps1"
echo.
echo Exit code: %ERRORLEVEL%
echo Log: %~dp0swap-runtime.log
pause
