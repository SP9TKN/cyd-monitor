# Turns the CYD Monitor "service" off: removes the watchdog task first (otherwise it would
# start the server again within ~30 s) and then stops the server.
# Undo with enable-service.ps1. Removing the task may need administrator rights.

& "$PSScriptRoot\stop-cyd-watchdog.ps1"
$watchdogOk = $LASTEXITCODE -eq 0
& "$PSScriptRoot\stop-cyd.ps1"
$serverOk = $LASTEXITCODE -eq 0

Write-Host ''
if ($serverOk -and $watchdogOk) {
    Write-Host 'Usluga CYD Monitor WYLACZONA (nie dziala i nie wstanie po restarcie).' -ForegroundColor Green
    exit 0
}
Write-Host 'Usluga CYD Monitor nie wylaczyla sie w calosci - patrz komunikaty wyzej.' -ForegroundColor Red
exit 1
