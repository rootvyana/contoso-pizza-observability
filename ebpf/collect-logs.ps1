# ELEVATED. Drives traffic, then collects BOTH instrumentation layers into one report.
$ErrorActionPreference = 'Continue'
# Derived from the script's own location, so a clone works anywhere rather
# than only on the machine this was written on.
$root   = Split-Path $PSScriptRoot -Parent
$ebpf   = "$root\ebpf"
$report = "$ebpf\instrumentation-report.txt"
$applog = "$root\app.log"
$bpftool = 'C:\Program Files\ebpf-for-windows\bpftool.exe'

$out = New-Object System.Collections.Generic.List[string]
function W($s) { $out.Add($s) }

W "ContosoPizza instrumentation report"
W "generated $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
W ("=" * 72)

# Mark where app.log is now, so we only summarise THIS run's telemetry.
$before = if (Test-Path $applog) { (Get-Content $applog).Count } else { 0 }

W ""
W "--- driving traffic ---"
$t = & "$ebpf\generate-traffic.ps1" -Requests 20
W $t
Start-Sleep -Seconds 4

# ---------------- Layer 1: eBPF ----------------
W ""
W ("=" * 72)
W "LAYER 1 - eBPF (kernel): TCP connections"
W ("=" * 72)

$prog = (netsh ebpf show programs) | Select-String -Pattern 'contoso_connection_monitor'
W ("program loaded : " + $(if ($prog) { $prog.Line.Trim() } else { 'NOT LOADED' }))

function Bytes($raw) { ,@($raw | ForEach-Object { if ($_ -is [string]) { [Convert]::ToByte($_,16) } else { [byte]$_ } }) }
function MapJson($n) { try { (& $bpftool --json map dump name $n 2>&1 | Out-String) | ConvertFrom-Json } catch { $null } }

$flows = MapJson 'flow_map'
W ""
W ("{0,-17} {1,-17} {2,-10} {3,-4} {4,-6} {5,-7} {6}" -f 'LOCAL','REMOTE','CLIENTPORT','DIR','PID','STATE','DURATION_MS')
W ("-" * 78)
$n = 0
foreach ($e in @($flows)) {
    $k = Bytes $e.key; $v = Bytes $e.value
    if ($k.Count -lt 12 -or $v.Count -lt 40) { continue }
    $n++
    $dur  = [BitConverter]::ToUInt64($v,16)
    $pid4 = [BitConverter]::ToUInt32($v,24)
    $open = [BitConverter]::ToUInt32($v,32)
    $ob   = [BitConverter]::ToUInt32($v,36)
    W ("{0,-17} {1,-17} {2,-10} {3,-4} {4,-6} {5,-7} {6}" -f `
        "$([Net.IPAddress]::new($k[0..3])):$([BitConverter]::ToUInt16($k,8))",
        "$([Net.IPAddress]::new($k[4..7])):$([BitConverter]::ToUInt16($k,10))",
        $(if ($ob -eq 1) { '' } else { [BitConverter]::ToUInt16($k,10) }),
        $(if ($ob -eq 1) { 'out' } else { 'in' }),
        $pid4,
        $(if ($open -eq 1) { 'OPEN' } else { 'CLOSED' }),
        $(if ($dur -gt 0) { [math]::Round($dur/1e6,1) } else { '-' }))
}
if ($n -eq 0) { W "(no flows)" }

$stats = MapJson 'stats_map'
$labels = @('connections established','connections closed','total open-time (ns)','currently open')
W ""
for ($i=0; $i -lt @($stats).Count -and $i -lt 4; $i++) {
    W ("{0,-26}: {1}" -f $labels[$i], [BitConverter]::ToUInt64((Bytes $stats[$i].value),0))
}

# owning process names
W ""
W "process attribution:"
$pids = @()
foreach ($e in @($flows)) { $v = Bytes $e.value; if ($v.Count -ge 40) { $pids += [BitConverter]::ToUInt32($v,24) } }
foreach ($procId in ($pids | Sort-Object -Unique)) {
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    W ("  pid {0,-6} {1}" -f $procId, $(if ($p) { $p.ProcessName } else { '(exited)' }))
}

# ---------------- Layer 2: OpenTelemetry ----------------
W ""
W ("=" * 72)
W "LAYER 2 - OpenTelemetry (app): HTTP requests"
W ("=" * 72)

$lines = if (Test-Path $applog) { Get-Content $applog } else { @() }
$new = if ($lines.Count -gt $before) { $lines[$before..($lines.Count-1)] } else { @() }

$spans = @()
for ($i=0; $i -lt $new.Count; $i++) {
    if ($new[$i] -match '^Activity\.DisplayName:\s*(.+)$') {
        $s = [ordered]@{ Name = $Matches[1].Trim(); Duration=$null; Route=$null; Status=$null; ClientPort=$null }
        for ($j=$i; $j -lt [math]::Min($i+22,$new.Count); $j++) {
            if ($new[$j] -match '^Activity\.Duration:\s*(.+)$') { $s.Duration = [TimeSpan]::Parse($Matches[1].Trim()).TotalMilliseconds }
            if ($new[$j] -match 'http\.route:\s*(.+)$')                { $s.Route = $Matches[1].Trim() }
            if ($new[$j] -match 'http\.response\.status_code:\s*(\d+)') { $s.Status = $Matches[1] }
            if ($new[$j] -match 'client\.port:\s*(\d+)')                { $s.ClientPort = $Matches[1] }
        }
        $spans += [pscustomobject]$s
    }
}

W ""
W "requests captured this run: $($spans.Count)"
if ($spans.Count -gt 0) {
    W ""
    W ("{0,-24} {1,-8} {2,-7} {3,-9} {4,-9} {5}" -f 'ROUTE','STATUS','COUNT','MIN_MS','MAX_MS','MEAN_MS')
    W ("-" * 70)
    foreach ($g in ($spans | Group-Object Route, Status)) {
        $d = $g.Group.Duration | Where-Object { $_ -ne $null }
        $m = $d | Measure-Object -Minimum -Maximum -Average
        W ("{0,-24} {1,-8} {2,-7} {3,-9} {4,-9} {5}" -f `
            $g.Group[0].Route, $g.Group[0].Status, $g.Count,
            [math]::Round($m.Minimum,3), [math]::Round($m.Maximum,3), [math]::Round($m.Average,3))
    }
    W ""
    W "connections used by these requests (client.port -> request count):"
    foreach ($g in ($spans | Where-Object ClientPort | Group-Object ClientPort | Sort-Object Count -Descending)) {
        W ("  port {0,-8} {1} requests" -f $g.Name, $g.Count)
    }
}

# ---------------- The join ----------------
W ""
W ("=" * 72)
W "THE JOIN - same connections, both layers"
W ("=" * 72)
W ""
$ebpfPorts = @()
foreach ($e in @($flows)) {
    $k = Bytes $e.key; $v = Bytes $e.value
    if ($k.Count -ge 12 -and $v.Count -ge 40 -and [BitConverter]::ToUInt32($v,36) -eq 0) {
        $ebpfPorts += [BitConverter]::ToUInt16($k,10)
    }
}
W ("{0,-12} {1,-14} {2,-16} {3}" -f 'CLIENTPORT','HTTP_REQUESTS','CONN_OPEN_MS','INTERPRETATION')
W ("-" * 74)
foreach ($p in ($ebpfPorts | Sort-Object -Unique)) {
    $reqs = @($spans | Where-Object { $_.ClientPort -eq "$p" }).Count
    $dur = $null
    foreach ($e in @($flows)) {
        $k = Bytes $e.key; $v = Bytes $e.value
        if ($k.Count -ge 12 -and $v.Count -ge 40 -and [BitConverter]::ToUInt16($k,10) -eq $p -and [BitConverter]::ToUInt32($v,36) -eq 0) {
            $d = [BitConverter]::ToUInt64($v,16); if ($d -gt 0) { $dur = [math]::Round($d/1e6,1) }
        }
    }
    $note = if ($reqs -gt 1) { "$reqs requests reused 1 socket (keep-alive)" } elseif ($reqs -eq 1) { 'single request on socket' } else { 'connection with no completed request' }
    W ("{0,-12} {1,-14} {2,-16} {3}" -f $p, $reqs, $(if ($dur) { $dur } else { 'still open' }), $note)
}

$out | Set-Content -Path $report -Encoding UTF8
Write-Host "report written to $report"
