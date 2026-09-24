@echo off
REM No elevation needed: the collector never touches the kernel.
REM Set these before the first run, or put them in appsettings.json.
REM   set CONTOSO_COLLECTOR_ENROLLMENT_TOKEN=...
REM   set CONTOSO_PROBE_KEY=...
REM   set Cloud__Endpoint=https://your-api-host
cd /d "%~dp0ContosoPizza.Collector"
dotnet run -c Release
pause
