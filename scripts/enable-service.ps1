# Turns the CYD Monitor "service" on in one go: starts the server now and registers the
# watchdog task, so it also comes back after every Windows restart.
# Undo with disable-service.ps1. Registering the task may need administrator rights.

# The panel and the phone reach the server over the home network: firewall rules first, so
# Windows does not ask (or silently refuse) when the server starts listening.
& "$PSScriptRoot\open-firewall.ps1"
& "$PSScriptRoot\start-cyd.ps1"
$serverOk = $LASTEXITCODE -eq 0
& "$PSScriptRoot\start-cyd-watchdog.ps1"
$watchdogOk = $LASTEXITCODE -eq 0

Write-Host ''
if ($serverOk -and $watchdogOk) {
    Write-Host 'Usluga CYD Monitor WLACZONA (dziala teraz i wstaje sama po restarcie).' -ForegroundColor Green
    exit 0
}
Write-Host 'Usluga CYD Monitor nie wlaczyla sie w calosci - patrz komunikaty wyzej.' -ForegroundColor Red
exit 1
