@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0read-results.ps1"
echo.
echo Log: %~dp0read-results.log
pause
