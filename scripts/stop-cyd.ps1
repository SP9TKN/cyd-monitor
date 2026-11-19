# Stops the CYD Monitor server (the "service"). The panel keeps its last picture and reconnects
# by itself once a server is started again (start-cyd.ps1).
#
# While the watchdog is on, it starts the server again within ~30 s - run stop-cyd-watchdog.ps1
# first to keep it stopped. (This script does not switch the watchdog off by itself, the same
# way stop-aprs.ps1 leaves the APRS watchdog alone.)
#
# Also run this before rebuilding: Windows will not overwrite a running cyd-monitor.exe.

. "$PSScriptRoot\cyd-common.ps1"

$servers = @(Get-CydServer)
if ($servers.Count -eq 0) {
    Write-Host 'Serwer CYD Monitor nie byl uruchomiony.'
} else {
    foreach ($p in $servers) {
        Stop-Process -Id $p.Id -Force
        [void](Wait-Process -Id $p.Id -Timeout 5 -ErrorAction SilentlyContinue)
        Write-Host "Zatrzymano serwer CYD Monitor (PID $($p.Id))."
    }
}

if (Get-ScheduledTask -TaskName 'CYD-Watchdog' -ErrorAction SilentlyContinue) {
    Write-Host ''
    Write-Host 'UWAGA: watchdog CYD jest wlaczony - uruchomi serwer ponownie w ciagu ok. 30 s.' -ForegroundColor Yellow
    Write-Host 'Zeby zatrzymac na stale, najpierw uruchom stop-cyd-watchdog.'                   -ForegroundColor Yellow
}
exit 0
