<#
.SYNOPSIS
  Installs the virtual display driver that cyd-monitor --virtual needs.

.DESCRIPTION
  Windows has no built-in way to create a monitor that does not physically
  exist, so showing "a second screen" rather than a copy of the first one needs
  an Indirect Display Driver. This installs the Parsec Virtual Display Driver
  (https://github.com/nomi-san/parsec-vdd, MIT), which is already signed —
  writing one from scratch would mean a WDK project plus either a code-signing
  certificate or permanently enabling test signing.

  WHICH FILE GETS RUN, AND WHY IT MATTERS

  That project publishes two downloads, and they are not equally trustworthy:

    ParsecVDisplay-setup.exe     NSIS wrapper        NOT signed
    ParsecVDisplay-portable.zip  contains:
      parsec-vdd-<ver>.exe       Parsec's driver     signed: Parsec Cloud, Inc.
      ParsecVDisplay.exe         third-party GUI     NOT signed

  This script takes the portable archive and runs only parsec-vdd-<ver>.exe,
  after checking both that its Authenticode signature is valid and that the
  signer really is Parsec Cloud. Pinning the publisher matters: "has some valid
  signature" is a much weaker statement than "is signed by the party whose
  driver we meant to install", and a driver runs inside the kernel's trust
  boundary.

  The unsigned GUI is never executed and never installed. It is not needed:
  cyd-monitor speaks to the driver directly over its IOCTL interface, and
  creates the monitor only while the server is running. When the server stops,
  the driver unplugs the display about a second later, so no phantom monitor is
  ever left behind.

  Run this once, as administrator. It is deliberately separate from the server
  rather than something the server does on its own, because installing a driver
  is not a thing a program should do behind your back.

.PARAMETER Force
  Skip the confirmation prompt.

.PARAMETER Uninstall
  Show how to remove it again.
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

# The organisation the driver must be signed by. Anything else is refused, even
# if its signature is perfectly valid.
$ExpectedSigner = 'Parsec Cloud'

if ($Uninstall) {
    Write-Host "Odinstalowanie: Ustawienia > Aplikacje > 'Parsec Virtual Display' > Odinstaluj"
    Write-Host "Albo: pnputil /enum-drivers | findstr /i parsec   a nastepnie"
    Write-Host "      pnputil /delete-driver oemXX.inf /uninstall /force"
    exit 0
}

$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "Ten skrypt musi dzialac jako administrator." -ForegroundColor Red
    Write-Host ""
    Write-Host "Kliknij prawym na PowerShell > 'Uruchom jako administrator', potem:"
    Write-Host "  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -ForegroundColor Yellow
    exit 1
}

function Get-VddDevice {
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -match 'Parsec Virtual Display|Virtual Display Adapter' }
}

$existing = Get-VddDevice
if ($existing) {
    Write-Host "Sterownik juz jest zainstalowany:" -ForegroundColor Green
    $existing | Select-Object Status, FriendlyName | Format-Table -AutoSize
    Write-Host "Mozesz uruchomic:  cyd-monitor --virtual"
    exit 0
}

Write-Host "Zainstaluje sterownik wirtualnego monitora (Parsec VDD, licencja MIT)." -ForegroundColor Cyan
Write-Host "Zrodlo : https://github.com/nomi-san/parsec-vdd/releases"
Write-Host "Wymagam podpisu: $ExpectedSigner"
Write-Host ""
if (-not $Force) {
    $answer = Read-Host "Kontynuowac? [t/N]"
    if ($answer -notmatch '^[tTyY]') { Write-Host "Przerwane."; exit 0 }
}

$tmp = Join-Path $env:TEMP 'cyd-vdd'
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $tmp | Out-Null

try {
    Write-Host "Pobieram informacje o najnowszym wydaniu..." -ForegroundColor Cyan
    # Resolved at run time rather than hard-coded, so this does not rot the
    # moment the project tags a new version.
    $headers = @{ 'User-Agent' = 'cyd-monitor-installer' }
    $release = Invoke-RestMethod -Headers $headers `
        -Uri 'https://api.github.com/repos/nomi-san/parsec-vdd/releases/latest'

    # The portable archive, not the setup executable: the archive lets us reach
    # Parsec's own signed installer, while the setup wrapper is unsigned and
    # would have to be trusted blind.
    $asset = $release.assets | Where-Object { $_.name -like '*portable*.zip' } | Select-Object -First 1
    if (-not $asset) { throw "nie znalazlem pakietu portable w wydaniu $($release.tag_name)" }

    $zip = Join-Path $tmp $asset.name
    Write-Host "Pobieram $($asset.name) ($([math]::Round($asset.size / 1MB, 2)) MB)..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers $headers

    Expand-Archive -Path $zip -DestinationPath "$tmp\x" -Force
    $driver = Get-ChildItem "$tmp\x" -Recurse -Filter 'parsec-vdd*.exe' | Select-Object -First 1
    if (-not $driver) { throw "w pakiecie nie ma instalatora sterownika parsec-vdd*.exe" }

    $sig = Get-AuthenticodeSignature $driver.FullName
    Write-Host ""
    Write-Host "Plik   : $($driver.Name)"
    Write-Host "Podpis : $($sig.Status)" -ForegroundColor $(if ($sig.Status -eq 'Valid') { 'Green' } else { 'Red' })
    if ($sig.SignerCertificate) {
        Write-Host "Podpisal: $($sig.SignerCertificate.Subject)"
    }

    if ($sig.Status -ne 'Valid') {
        throw "podpis nieprawidlowy ($($sig.Status)) - nic nie instaluje"
    }
    if ($sig.SignerCertificate.Subject -notmatch [regex]::Escape($ExpectedSigner)) {
        throw "podpisane przez kogos innego niz '$ExpectedSigner' - nic nie instaluje"
    }

    Write-Host ""
    Write-Host "Instaluje sam sterownik (aplikacja GUI z pakietu jest pomijana)..." -ForegroundColor Cyan
    # NSIS installer, confirmed by the Nullsoft markers in the binary, so /S is
    # the silent switch.
    $proc = Start-Process -FilePath $driver.FullName -ArgumentList '/S' -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "instalator zwrocil kod $($proc.ExitCode)" }

    Start-Sleep -Seconds 3
    $now = Get-VddDevice
    if ($now) {
        Write-Host ""
        Write-Host "Gotowe:" -ForegroundColor Green
        $now | Select-Object Status, FriendlyName | Format-Table -AutoSize
        Write-Host "Teraz mozesz uruchomic serwer:" -ForegroundColor Green
        Write-Host "  dotnet run --project server\cyd-monitor -c Release -- --virtual" -ForegroundColor Yellow
    } else {
        Write-Host ""
        Write-Host "Instalator zakonczyl sie bez bledu, ale nie widze urzadzenia." -ForegroundColor Yellow
        Write-Host "Sprawdz Menedzer urzadzen > Karty graficzne. Moze byc potrzebny restart."
        exit 2
    }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
