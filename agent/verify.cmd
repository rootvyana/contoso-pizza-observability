@echo off
REM Right-click -> Run as administrator.
REM End-to-end on-prem verification: eBPF program, app, OTLP endpoint,
REM native interop, kernel map read, and live flows.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify.ps1"
echo.
pause
