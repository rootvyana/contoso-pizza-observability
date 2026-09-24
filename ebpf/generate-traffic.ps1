# Non-elevated. Drives load through the API so the maps have something in them.
param([int]$Requests = 25, [string]$BaseUrl = 'http://127.0.0.1:5176')

$ok = 0; $failed = 0
1..$Requests | ForEach-Object {
    try {
        Invoke-WebRequest "$BaseUrl/weatherforecast" -UseBasicParsing -TimeoutSec 5 | Out-Null
        $ok++
    } catch { $failed++ }
    Start-Sleep -Milliseconds 120
}
Write-Host "$ok succeeded, $failed failed against $BaseUrl" -ForegroundColor Green
