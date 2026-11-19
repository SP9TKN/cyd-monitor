# Turns the CYD watchdog off for good: removes the "CYD-Watchdog" scheduled task (so neither the
# logon trigger nor the 5-minute supervisor can bring it back) and kills every running copy.
# The CYD server itself is NOT touched - stop it with stop-cyd.ps1.
# Undo with start-cyd-watchdog.ps1.

$taskName = 'CYD-Watchdog'

# A path separator or quote must precede the name: a plain substring also matches this very
# script (stop-cyd-watchdog.ps1), which would then kill itself.
function Get-WatchdogProcesses {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '[\\"]cyd-watchdog\.ps1' }
}

$failed = $false

# Task first, process second: otherwise the supervisor trigger could relaunch what we just killed.
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    try {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
        Write-Host "Zadanie '$taskName' usuniete - watchdog nie wroci przy logowaniu."
    } catch {
        Write-Error "Nie udalo sie usunac zadania '$taskName': $($_.Exception.Message)`nUruchom stop-cyd-watchdog jako administrator."
        $failed = $true
    }
} else {
    Write-Host "Zadania '$taskName' nie ma - nic do usuniecia."
}

# A few rounds, in case a copy was starting up while we were killing.
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 10) {
    $procs = @(Get-WatchdogProcesses)
    if (-not $procs) { break }
    foreach ($p in $procs) {
        Write-Host "Zatrzymuje watchdoga (PID $($p.ProcessId))..."
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 500
}

if (Get-WatchdogProcesses) {
    Write-Error "Watchdog nadal dziala - nie udalo sie go zatrzymac."
    $failed = $true
} else {
    Write-Host "Watchdog zatrzymany."
}

$srv = Get-Process -Name cyd-monitor -ErrorAction SilentlyContinue
if ($srv) { Write-Host "Serwer CYD Monitor dziala dalej (PID $($srv.Id)) - zatrzymasz go przez stop-cyd." }

exit ([int]$failed)
