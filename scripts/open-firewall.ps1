# Lets the CYD panel and phones reach the server through Windows Firewall: TCP 7788 (control),
# UDP 7787 (discovery), UDP 7789/7790 (pixels) and TCP 7793 (scanner web page), all scoped to this
# program and to the local network only. Needs administrator rights; the .cmd wrapper asks for them.

. "$PSScriptRoot\cyd-common.ps1"

$name = 'CYD Monitor'
Get-NetFirewallRule -DisplayName "$name*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule

foreach ($proto in 'TCP', 'UDP') {
    New-NetFirewallRule -DisplayName "$name ($proto)" -Direction Inbound -Action Allow `
        -Program $cydExe -Protocol $proto -RemoteAddress LocalSubnet -Profile Any | Out-Null
}
Write-Host "Zapora: dodano reguly '$name' dla $cydExe (tylko siec lokalna)." -ForegroundColor Green
