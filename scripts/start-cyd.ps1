# Starts the CYD Monitor server (the "service") now, hidden and independent of this window.
# Does nothing when it is already running. Stop it with stop-cyd.ps1. For autostart after a
# Windows restart use start-cyd-watchdog.ps1.

. "$PSScriptRoot\cyd-common.ps1"

if (-not (Test-Path $cydExe)) {
    Write-Error "Brak $cydExe`nZbuduj serwer: dotnet build server\cyd-monitor -c Release"
    exit 1
}

$running = Get-CydServer
if ($running) {
    Write-Host "Serwer CYD Monitor juz dziala (PID $($running.Id))."
    exit 0
}

$id = Start-CydServer
if (-not $id) {
    Write-Error 'Nie udalo sie uruchomic serwera (Win32_Process.Create).'
    exit 1
}

# A server that cannot bind its port (or lacks the .NET runtime) exits within a moment.
Start-Sleep -Seconds 3
if (-not (Get-Process -Id $id -ErrorAction SilentlyContinue)) {
    Write-Error "Serwer zakonczyl sie zaraz po starcie - zajrzyj do $cydLogDir\log.txt"
    exit 1
}
Write-Host "Serwer CYD Monitor uruchomiony (PID $id). Panel polaczy sie sam. Log: $cydLogDir\log.txt"

if (Get-ScheduledTask -TaskName 'CYD-Watchdog' -ErrorAction SilentlyContinue) {
    Write-Host "Watchdog CYD jest wlaczony - bedzie pilnowal serwera."
}
exit 0
