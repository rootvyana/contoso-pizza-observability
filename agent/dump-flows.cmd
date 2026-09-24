@echo off
REM Right-click -> Run as administrator.
REM One-shot read of the kernel maps, straight to the console.
cd /d "%~dp0ContosoPizza.Probe"
dotnet run -c Release -- --dump
echo.
pause
