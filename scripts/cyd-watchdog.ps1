param(
    [switch]$Once,     # run a single check pass and exit (diagnostics, no startup grace)
    [switch]$DryRun    # log what would be started, start nothing
)

# CYD Monitor watchdog: every $checkSec seconds it checks that cyd-monitor.exe is running and
# launches it when it is not (after a Windows restart, or after it was killed).
#
# It also keeps Ulanzi Studio linked to the Dial: when the Dial wakes, Studio can fail its
# handshake ("HidDeviceAuth failed") and then waits for the next wake, with every button dead.
# Only a restart of Studio links it again. See Test-StudioLinkLost.
#
# Run by the "CYD-Watchdog" scheduled task (registered by start-cyd-watchdog.ps1, removed by
# stop-cyd-watchdog.ps1). A second copy exits at once on the mutex.
#
# The server is launched through WMI (see Start-CydServer), so stopping the task or this script
# does not take the server down with it - same reasoning as aprs-watchdog.ps1.

. "$PSScriptRoot\cyd-common.ps1"

$logPath      = Join-Path $cydLogDir 'cyd-watchdog.log'
$checkSec     = 30   # how often the server is checked
$studioSec    = 5    # how often the Dial's link is checked: the user is waiting with a finger on it
$studioExe    = 'C:\Program Files (x86)\Ulanzi Studio\UlanziDeck.exe'
$studioLogDir = Join-Path $env:LOCALAPPDATA 'UlanziStudio'
$studioLinkGraceSec = 5     # a healthy link follows the find within a second
$studioGapMinSec    = 60    # between restarts, doubled while they do not bring the link back
$studioGapMaxSec    = 900
$studioGapSec       = $studioGapMinSec
$studioNextRestart  = Get-Date
# Studio has its own Run entry at logon: give it the time to come up by itself.
$studioStartAfter   = (Get-Date).AddSeconds(90)
# The panel reconnects the moment the port opens, and a session that starts before DXGI knows
# the display falls back to slow GDI capture for its whole life ("Desktop Duplication
# niedostepne ... przechodze na GDI" in the server log). Let logon settle first.
$startupGrace = 20

function Write-WatchdogLog {
    param([string]$Text)
    try {
        New-Item -ItemType Directory -Force $cydLogDir | Out-Null
        if ((Test-Path $logPath) -and (Get-Item $logPath).Length -gt 1MB) {
            Move-Item $logPath "$logPath.old" -Force
        }
        "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $Text" | Out-File -FilePath $logPath -Append -Encoding UTF8
    } catch {}
}

function Invoke-Check {
    if (Get-CydServer) { return }
    if (-not (Test-Path $cydExe)) {
        Write-WatchdogLog "BLAD: brak $cydExe - zbuduj: dotnet build server\cyd-monitor -c Release"
        return
    }
    if ($DryRun) {
        Write-WatchdogLog 'serwer nie dziala - DryRun, nie uruchamiam'
        return
    }
    Write-WatchdogLog 'serwer nie dziala - uruchamiam'
    $id = Start-CydServer
    if (-not $id) {
        Write-WatchdogLog 'BLAD: Win32_Process.Create nie uruchomil serwera'
        return
    }
    # A server that dies right after launch (busy port, missing runtime) would otherwise only
    # show up as the same "nie dziala" line again 30 s later, with no hint why.
    Start-Sleep -Seconds 3
    if (Get-Process -Id $id -ErrorAction SilentlyContinue) {
        Write-WatchdogLog "serwer uruchomiony (PID $id)"
    } else {
        Write-WatchdogLog "BLAD: serwer (PID $id) zakonczyl sie zaraz po starcie - zajrzyj do $cydLogDir\log.txt"
    }
}

# The end of Studio's newest log, shared with Studio, which keeps it open.
function Read-StudioLogTail {
    $log = Get-ChildItem -Path $studioLogDir -Filter '*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { return '' }
    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $stream = [IO.File]::Open($log.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
    try {
        $count = [int][Math]::Min($stream.Length, 32768)
        [void]$stream.Seek(-$count, [IO.SeekOrigin]::End)
        $bytes = New-Object byte[] $count
        $read = $stream.Read($bytes, 0, $count)
        [Text.Encoding]::UTF8.GetString($bytes, 0, $read)
    } finally { $stream.Dispose() }
}

# True when the Dial has shown up and Studio has not linked it: a failed handshake with no link
# after it, or a find after an unlink with no link after it. A find on a linked Dial happens
# too, and an unlink alone is a Dial asleep; neither is a fault. The grace lets a link in progress
# finish, also when Studio tries again by itself.
function Test-StudioLinkLost {
    param([string]$Text, [datetime]$Now)
    $ordinal = [StringComparison]::Ordinal
    $link   = $Text.LastIndexOf('##DeviceLink', $ordinal)
    $unlink = $Text.LastIndexOf('##DeviceUnlink', $ordinal)
    $fail   = $Text.LastIndexOf('HidDeviceAuth failed', $ordinal)
    $found  = $Text.LastIndexOf('find path:', $ordinal)
    if (-not ($fail -gt $link -or ($unlink -gt $link -and $found -gt $unlink))) { return $false }
    $last = [Math]::Max($fail, $found)
    $lineStart = $Text.LastIndexOf("`n", $last, $last + 1, $ordinal) + 1
    $stamp = [regex]::Match($Text.Substring($lineStart), '^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)')
    if (-not $stamp.Success) { return $true }
    $at = [datetime]::ParseExact($stamp.Groups[1].Value, 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
    return ($Now - $at).TotalSeconds -ge $studioLinkGraceSec
}

# Through WMI like the server, with the same switch as Studio's own Run entry.
function Start-UlanziStudio {
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine      = '"' + $studioExe + '" --autorun'
        CurrentDirectory = Split-Path $studioExe
    }
    if ($r.ReturnValue -ne 0) { return 0 }
    [int]$r.ProcessId
}

function Invoke-StudioCheck {
    if (-not (Test-Path $studioExe)) { return }
    $running = Get-Process -Name UlanziDeck -ErrorAction SilentlyContinue
    $now = Get-Date
    if (-not $running) {
        if ($now -lt $studioStartAfter) { return }
        $why = 'Ulanzi Studio nie dziala'
    } else {
        $text = Read-StudioLogTail
        if (-not (Test-StudioLinkLost -Text $text -Now $now)) {
            if ($text.Contains('##DeviceLink')) { $script:studioGapSec = $studioGapMinSec }
            return
        }
        $why = 'Ulanzi Studio nie polaczylo sie z pokretlem'
    }
    if ($now -lt $script:studioNextRestart) { return }
    $script:studioNextRestart = $now.AddSeconds($script:studioGapSec)
    $script:studioGapSec = [Math]::Min($script:studioGapSec * 2, $studioGapMaxSec)
    if ($DryRun) {
        Write-WatchdogLog "$why - DryRun, nie ruszam"
        return
    }
    Write-WatchdogLog "$why - uruchamiam je od nowa"
    if ($running) {
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        $gone = [Diagnostics.Stopwatch]::StartNew()
        while ($gone.Elapsed.TotalSeconds -lt 10 -and (Get-Process -Name UlanziDeck -ErrorAction SilentlyContinue)) {
            Start-Sleep -Milliseconds 300
        }
    }
    $id = Start-UlanziStudio
    if ($id) { Write-WatchdogLog "Ulanzi Studio uruchomione (PID $id)" }
    else { Write-WatchdogLog 'BLAD: Win32_Process.Create nie uruchomil Ulanzi Studio' }
}

# Lets `. .\cyd-watchdog.ps1` load the functions without running the loop (tests).
if ($MyInvocation.InvocationName -eq '.') { return }

# Single instance: a second copy (manual run next to the task) exits at once.
# -Once is a diagnostic pass and may run next to the real watchdog.
if (-not $Once) {
    $createdNew = $false
    $mutex = New-Object System.Threading.Mutex($true, 'Local\CYD-Watchdog', [ref]$createdNew)   # held until the process ends
    if (-not $createdNew) { exit 0 }
}

# Task Scheduler may start us BelowNormal; keep the watchdog itself at Normal so the check
# never starves under load. (The server gets Normal from WMI regardless.)
try { (Get-Process -Id $PID).PriorityClass = 'Normal' } catch {}

Write-WatchdogLog "cyd-watchdog.ps1 wystartowal (PID $PID), sprawdzanie co $checkSec s$(if ($DryRun) { ', DryRun' })$(if ($Once) { ', Once' })"

if (-not $Once) { Start-Sleep -Seconds $startupGrace }

$heartbeatDue = (Get-Date).AddHours(6)
$serverCheckDue = Get-Date
while ($true) {
    try {
        if ((Get-Date) -ge $serverCheckDue) {
            $serverCheckDue = (Get-Date).AddSeconds($checkSec)
            Invoke-Check
        }
        Invoke-StudioCheck
        if ((Get-Date) -ge $heartbeatDue) {
            $srv = Get-CydServer
            Write-WatchdogLog "dziala, serwer: $(if ($srv) { "PID $($srv.Id)" } else { 'brak' })"
            $heartbeatDue = (Get-Date).AddHours(6)
        }
    } catch {
        Write-WatchdogLog "BLAD petli: $($_.Exception.Message)"
    }
    if ($Once) { break }
    Start-Sleep -Seconds $studioSec
}
