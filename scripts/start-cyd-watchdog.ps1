# Turns the CYD watchdog on for good: registers the "CYD-Watchdog" scheduled task (logon trigger
# + a 5-minute supervisor repeat, same scheme as APRS-Watchdog) and starts it now. From then on
# the server comes up after every Windows logon and is brought back if it dies.
# Safe to run repeatedly - the task is re-registered and a running watchdog is left alone.
# Undo with stop-cyd-watchdog.ps1.
#
# The task runs as the logged-on user (Desktop Duplication needs the interactive session, so it
# cannot be a service or SYSTEM), at Normal priority (the Task Scheduler default is BelowNormal,
# which starves the frame loop under load) and with no execution time limit (the default would
# kill it after 72 hours).
#
# -DryRun only builds and validates the task definition; registers nothing, starts nothing.
[CmdletBinding()]
param([switch]$DryRun)

$taskName = 'CYD-Watchdog'
$watchdog = Join-Path $PSScriptRoot 'cyd-watchdog.ps1'
$logPath  = Join-Path $env:LOCALAPPDATA 'cyd-monitor\cyd-watchdog.log'

# A path separator or quote must precede the name: a plain substring also matches this very
# script (start-cyd-watchdog.ps1) and stop-cyd-watchdog.ps1.
function Get-WatchdogProcesses {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '[\\"]cyd-watchdog\.ps1' -and $_.CommandLine -notmatch '-Once' }
}

if (-not (Test-Path $watchdog)) {
    Write-Error "Brak pliku $watchdog"
    exit 1
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$userSid  = $identity.User.Value
$userName = $identity.Name
$taskArgs = [Security.SecurityElement]::Escape("-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$watchdog`"")
$now      = Get-Date -Format "yyyy-MM-dd'T'HH:mm:ssK"

$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Pilnuje, zeby serwer CYD Monitor byl zawsze uruchomiony (start: start-cyd-watchdog, stop: stop-cyd-watchdog).</Description>
    <URI>\$taskName</URI>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <UserId>$userSid</UserId>
      <LogonType>InteractiveToken</LogonType>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Hidden>true</Hidden>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Priority>4</Priority>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
  </Settings>
  <Triggers>
    <LogonTrigger>
      <Repetition>
        <Interval>PT5M</Interval>
        <StopAtDurationEnd>true</StopAtDurationEnd>
      </Repetition>
      <UserId>$userName</UserId>
    </LogonTrigger>
    <TimeTrigger>
      <StartBoundary>$now</StartBoundary>
      <Repetition>
        <Interval>PT5M</Interval>
        <StopAtDurationEnd>true</StopAtDurationEnd>
      </Repetition>
    </TimeTrigger>
  </Triggers>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>$taskArgs</Arguments>
    </Exec>
  </Actions>
</Task>
"@

if ($DryRun) {
    try { [void][xml]$xml } catch { Write-Error "Definicja zadania nie jest poprawnym XML: $($_.Exception.Message)"; exit 1 }
    Write-Host "DryRun: definicja zadania '$taskName' jest poprawnym XML; nic nie zarejestrowano."
    exit 0
}

try {
    Register-ScheduledTask -TaskName $taskName -Xml $xml -Force -ErrorAction Stop | Out-Null
} catch {
    Write-Error "Nie udalo sie zarejestrowac zadania '$taskName': $($_.Exception.Message)`nUruchom start-cyd-watchdog jako administrator."
    exit 1
}
Write-Host "Zadanie '$taskName' zarejestrowane (start przy logowaniu + kontrola co 5 min)."

if (Get-WatchdogProcesses) {
    Write-Host "Watchdog juz dziala - zostawiam."
} else {
    Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 10 -and -not (Get-WatchdogProcesses)) { Start-Sleep -Milliseconds 300 }
}

if (Get-WatchdogProcesses) {
    Write-Host "Watchdog dziala (sprawdza serwer CYD co 30 s; pierwsze sprawdzenie po ok. 20 s). Log: $logPath"
    exit 0
}
Write-Error "Zadanie uruchomione, ale proces watchdoga sie nie pojawil - zajrzyj do $logPath"
exit 1
