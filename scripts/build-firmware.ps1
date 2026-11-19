<#
.SYNOPSIS
  Compiles and uploads the CYD monitor firmware.

.DESCRIPTION
  Wraps arduino-cli so the board options, the library path and the panel
  variant are not something to remember. The toolchain lives under
  %LOCALAPPDATA%\cyd-arduino rather than in the project folder on purpose: it
  is roughly 1.5 GB of compilers and SDKs shared by every sketch, not part of
  this project's own source.

.PARAMETER Port
  Serial port of the board, e.g. COM7. Auto-detected when omitted by looking
  for a USB-serial bridge; pass it explicitly if more than one is attached.

.PARAMETER St7789
  Build for late "2432S028Rv3" boards, which fit an ST7789 instead of the
  ILI9341. Symptom of needing this: inverted colours or an offset picture.

.PARAMETER SpiHz
  Panel SPI clock. 80 MHz is needed to fit a worst-case full-screen repaint
  inside the frame budget; drop to 65000000 or 55000000 if the panel sparkles.

.PARAMETER Rotation
  Forces the panel rotation register and persists it, overriding whatever the
  board has stored. One of 1, 3, 5, 7 (the four landscape values); -1, the
  default, leaves the stored setting alone. 5 and 1 differ by a horizontal
  mirror, as do 7 and 3. Useful when the touchscreen is hard to reach the
  on-panel "Obrot" menu with.

.PARAMETER NoWeb
  Diagnostics: leave the panel's settings web server out of the firmware (no page, no
  /api/state), to see whether it disturbs the stream. Flash a normal build afterwards.

.PARAMETER NoUdp
  Leave the protocol-v5 UDP data plane out of the firmware: the panel then behaves exactly
  like a v4 one (never offers UDP, allocates no datagram ring). For A/B measurements and as
  a known-good fallback image.

.PARAMETER CompileOnly
  Build without touching the board.
#>
[CmdletBinding()]
param(
    [string]$Port,
    [switch]$St7789,
    [int]$SpiHz = 80000000,
    [ValidateSet(-1, 1, 3, 5, 7)]
    [int]$Rotation = -1,
    [switch]$NoWeb,
    [switch]$NoUdp,
    [switch]$CompileOnly
)

$ErrorActionPreference = 'Stop'

$repo    = Split-Path -Parent $PSScriptRoot
$acli    = Join-Path $repo 'tools\arduino-cli.exe'
$config  = Join-Path $env:LOCALAPPDATA 'cyd-arduino\arduino-cli.yaml'
$sketch  = Join-Path $repo 'firmware\cyd-monitor'
$fqbn    = 'esp32:esp32:esp32'

if (-not (Test-Path $acli))   { throw "brak arduino-cli: $acli" }
if (-not (Test-Path $config)) { throw "brak konfiguracji arduino-cli: $config" }

# Board options, all of which matter:
#   PSRAM=disabled  - the ESP32-WROOM-32 on this board has none
#   PartitionScheme=huge_app - WiFi + WebServer + LovyanGFX overflow the
#                              default 1.2 MB app partition
#   CPUFreq=240     - the SPI clock divider and the frame budget both assume it
#   FlashMode=dio / FlashFreq=80 - what these modules are wired for
$options = 'PSRAM=disabled,PartitionScheme=huge_app,CPUFreq=240,FlashMode=dio,FlashFreq=80,UploadSpeed=921600'

$panel = if ($St7789) { 1 } else { 0 }
# Quoting matters here: arduino-cli passes this string to the compiler, and an
# unquoted -D with a comma would be split as another board option.
$flags = "-DCYD_PANEL_ST7789=$panel -DCYD_SPI_HZ=$SpiHz -DCYD_FORCE_ROTATION=$Rotation -DCYD_NO_WEB=$([int]$NoWeb.IsPresent) -DCYD_NO_UDP=$([int]$NoUdp.IsPresent)"

Write-Host "kompilacja firmware..." -ForegroundColor Cyan
Write-Host "  panel : $(if ($St7789) { 'ST7789 (v3)' } else { 'ILI9341' })"
if ($NoWeb) { Write-Host "  www   : WYLACZONE (-NoWeb, tylko do testu)" -ForegroundColor Yellow }
if ($NoUdp) { Write-Host "  udp   : WYLACZONE (-NoUdp, zachowuje sie jak v4)" -ForegroundColor Yellow }
Write-Host "  SPI   : $($SpiHz / 1000000) MHz"

& $acli --config-file $config compile `
    --fqbn "${fqbn}:$options" `
    --build-property "compiler.cpp.extra_flags=$flags" `
    --warnings default `
    $sketch
if ($LASTEXITCODE -ne 0) { throw "kompilacja nie powiodla sie" }

if ($CompileOnly) {
    Write-Host "gotowe (bez wgrywania)" -ForegroundColor Green
    exit 0
}

if (-not $Port) {
    # Match on the bridge chips these boards actually use rather than on any
    # COM port, so an unrelated serial device is never flashed by accident.
    $candidates = Get-PnpDevice -PresentOnly -Class Ports -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -match 'CH340|CH910[0-9]|CP210|CH343' }
    if (-not $candidates) { throw "nie znalazlem plytki - podaj -Port COMx" }
    if ($candidates.Count -gt 1) {
        Write-Host "kilka kandydatow:" -ForegroundColor Yellow
        $candidates | ForEach-Object { Write-Host "  $($_.FriendlyName)" }
        throw "podaj -Port COMx"
    }
    if ($candidates.FriendlyName -match '\((COM\d+)\)') { $Port = $Matches[1] }
    else { throw "nie umiem odczytac numeru portu - podaj -Port COMx" }
    Write-Host "wykryty port: $Port" -ForegroundColor Cyan
}

Write-Host "wgrywanie na $Port..." -ForegroundColor Cyan
& $acli --config-file $config upload -p $Port --fqbn "${fqbn}:$options" $sketch
if ($LASTEXITCODE -ne 0) {
    throw "wgrywanie nie powiodlo sie - jesli plytka nie wchodzi w tryb bootloadera, " +
          "przytrzymaj BOOT podczas startu wgrywania"
}

Write-Host "gotowe. Przy pierwszym uruchomieniu panel wystawi siec WiFi " -ForegroundColor Green -NoNewline
Write-Host "CYD-Monitor-Setup" -ForegroundColor Yellow
Write-Host "  polacz sie z nia i otworz http://192.168.4.1 aby podac dane swojej sieci."
