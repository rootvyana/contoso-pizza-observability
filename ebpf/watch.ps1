# ELEVATED. Decodes flow_map / stats_map into a readable table.
# Struct layouts must stay in sync with contoso_sockops.c:
#   flow_key_t   = local_ip4(4) remote_ip4(4) local_port(2) remote_port(2)   = 12 bytes
#   flow_stats_t = established(8) deleted(8) duration(8) pid(4) family(4) open(4) outbound(4) = 40 bytes
param([switch]$Loop, [int]$IntervalSeconds = 2)

$ErrorActionPreference = 'Stop'
$bpftool = 'C:\Program Files\ebpf-for-windows\bpftool.exe'

function ConvertTo-Bytes($raw) {
    # bpftool emits byte arrays either as hex strings ("0x7f") or as integers.
    ,@($raw | ForEach-Object {
        if ($_ -is [string]) { [Convert]::ToByte($_, 16) } else { [byte]$_ }
    })
}

function Get-MapJson($name) {
    $out = & $bpftool --json map dump name $name 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "bpftool map dump '$name' failed. Elevated? Program loaded? Output: $out"
    }
    ($out | Out-String) | ConvertFrom-Json
}

function Show-Snapshot {
    $flows = Get-MapJson 'flow_map'
    $rows = foreach ($entry in $flows) {
        $k = ConvertTo-Bytes $entry.key
        $v = ConvertTo-Bytes $entry.value
        if ($k.Count -lt 12 -or $v.Count -lt 40) { continue }

        $localIp  = [Net.IPAddress]::new(($k[0..3]))
        $remoteIp = [Net.IPAddress]::new(($k[4..7]))
        $duration = [BitConverter]::ToUInt64($v, 16)
        $pid4     = [BitConverter]::ToUInt32($v, 24)
        $open     = [BitConverter]::ToUInt32($v, 32)
        $outbound = [BitConverter]::ToUInt32($v, 36)

        [pscustomobject]@{
            Local      = "$localIp`:$([BitConverter]::ToUInt16($k, 8))"
            Remote     = "$remoteIp`:$([BitConverter]::ToUInt16($k, 10))"
            # remote_port is the real client port only on inbound (server-side) flows;
            # on an outbound row it is just the server port, so leave it blank.
            ClientPort = if ($outbound -eq 1) { $null } else { [BitConverter]::ToUInt16($k, 10) }
            Dir        = if ($outbound -eq 1) { 'out' } else { 'in' }
            Pid        = $pid4
            State      = if ($open -eq 1) { 'OPEN' } else { 'CLOSED' }
            DurationMs = if ($duration -gt 0) { [math]::Round($duration / 1e6, 3) } else { $null }
        }
    }

    Clear-Host
    Write-Host "ContosoPizza connection flows  --  $(Get-Date -Format 'HH:mm:ss')" -ForegroundColor Cyan
    if ($rows) { $rows | Sort-Object State, ClientPort | Format-Table -AutoSize }
    else { Write-Host '  (no flows recorded yet -- send some traffic)' -ForegroundColor DarkGray }

    $stats = Get-MapJson 'stats_map'
    $labels = @('Established', 'Deleted', 'TotalDurationNs', 'CurrentlyOpen')
    $agg = [ordered]@{}
    for ($i = 0; $i -lt $stats.Count -and $i -lt $labels.Count; $i++) {
        $b = ConvertTo-Bytes $stats[$i].value
        $agg[$labels[$i]] = [BitConverter]::ToUInt64($b, 0)
    }
    if ($agg['Established'] -gt 0 -and $agg['Deleted'] -gt 0) {
        $agg['MeanDurationMs'] = [math]::Round(($agg['TotalDurationNs'] / $agg['Deleted']) / 1e6, 3)
    }
    Write-Host 'Aggregate counters' -ForegroundColor Cyan
    [pscustomobject]$agg | Format-List
}

if ($Loop) { while ($true) { Show-Snapshot; Start-Sleep -Seconds $IntervalSeconds } }
else { Show-Snapshot }
