<#
.SYNOPSIS
  Summarises the stutters a server log recorded in a time window, for the TCP-versus-UDP comparison.

.DESCRIPTION
  Both servers log the same kind of event: "[dip] N ms bez potwierdzenia panelu" (protocol v4, TCP:
  a frame the panel did not acknowledge for 250 ms or more) and "[stale] N ms bez potwierdzenia panelu"
  (protocol v5, UDP: the panel silent for 250 ms or more while it owed an acknowledgement). Each is the
  picture standing still, seen from the server, so their counts and total durations compare directly.

  Each log line that carries a timestamp is followed by its event line; the timestamp on the line
  before an event is that event's *end*.

.PARAMETER Log
  Log to read. Default: the production server's %LOCALAPPDATA%\cyd-monitor\log.txt.

.PARAMETER Since, Until
  Window (local time). Default: the last hour.

.EXAMPLE
  .\ab-summary.ps1 -Since '2026-09-21 21:30' -Until '2026-09-21 22:30'
  .\ab-summary.ps1 -Log "$env:LOCALAPPDATA\cyd-monitor-udp\log.txt" -Since '2026-09-21 22:40'
#>
[CmdletBinding()]
param(
    [string]$Log = (Join-Path $env:LOCALAPPDATA 'cyd-monitor\log.txt'),
    [datetime]$Since = (Get-Date).AddHours(-1),
    [datetime]$Until = (Get-Date)
)

if (-not (Test-Path $Log)) { throw "brak logu: $Log" }

$stamp = $null
$events = New-Object System.Collections.Generic.List[object]
# The running server keeps its log open, so it has to be opened with sharing allowed.
$fs = [System.IO.File]::Open($Log, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
$sr = New-Object System.IO.StreamReader($fs)
try {
    while ($null -ne ($line = $sr.ReadLine())) {
        if ($line -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\b') {
            $stamp = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', $null)
            continue
        }
        if ($null -eq $stamp -or $stamp -lt $Since -or $stamp -gt $Until) { continue }
        if ($line -match '\[(dip|stale)\]\s+(\d+) ms') {
            $events.Add([pscustomobject]@{ Kind = $Matches[1]; Ms = [int]$Matches[2]; At = $stamp })
        }
    }
} finally { $sr.Dispose() }

$minutes = [math]::Max(1e-9, ($Until - $Since).TotalMinutes)
Write-Host ("log   : {0}" -f $Log)
Write-Host ("okno  : {0:yyyy-MM-dd HH:mm:ss} .. {1:yyyy-MM-dd HH:mm:ss}  ({2:F1} min)" -f $Since, $Until, $minutes)

if ($events.Count -eq 0) {
    Write-Host 'zdarzen: 0'
    return
}

foreach ($g in ($events | Group-Object Kind)) {
    $ms = @($g.Group | ForEach-Object Ms | Sort-Object)
    $total = ($ms | Measure-Object -Sum).Sum
    $p50 = $ms[[int][math]::Floor(($ms.Count - 1) * 0.5)]
    $p90 = $ms[[int][math]::Floor(($ms.Count - 1) * 0.9)]
    Write-Host ("[{0}] zdarzen {1}  ({2:F2}/min)   lacznie {3:F1} s = {4:F2}% okna   mediana {5} ms   p90 {6} ms   najdluzsze {7} ms   >=1 s: {8}" -f `
        $g.Name, $ms.Count, ($ms.Count / $minutes), ($total / 1000.0), (100.0 * $total / 1000.0 / 60.0 / $minutes),
        $p50, $p90, $ms[-1], @($ms | Where-Object { $_ -ge 1000 }).Count)
}
