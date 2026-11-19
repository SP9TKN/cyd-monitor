# CYD-Monitor — quick start

> 🇵🇱 Polish version: [SZYBKI_START.md](SZYBKI_START.md)

Written for someone who has never flashed an ESP32. About 15 minutes. The panel's screens and web pages are in Polish;
the few words you need are translated below.

## What you need

- A PC with **Windows 10 or 11, 64-bit**.
- An **ESP32-2432S028R** board ("Cheap Yellow Display", 2.8") and a **USB cable with data lines** (a charge-only cable
  will not work).
- **2.4 GHz Wi-Fi** at home (the panel cannot use 5 GHz). The PC, the panel and a phone must be on the same network.
- Optional: [SDR Oxide — SP9TKN build](https://github.com/SP9TKN/sdroxide-brown) for the scanner page, and an
  Ulanzi D100H dial.

## 1. Download

From **[Releases](https://github.com/SP9TKN/cyd-monitor/releases)** take:

- the firmware: **`…_ILI9341_…_flash_at_0x0.bin`** (most boards) or **`…_ST7789_…_flash_at_0x0.bin`** (newer "v3"
  boards — pick it if the first one shows inverted colours or a shifted picture),
- the server: **`cyd-monitor-server_…_win-x64.zip`**.

## 2. Flash the panel (in the browser, nothing to install)

1. Connect the board to the PC with the USB cable. Windows normally installs the CH340 USB-serial driver by itself.
2. Open **https://espressif.github.io/esptool-js/** in Chrome or Edge.
3. Set *Baudrate* to 921600, click **Connect** and choose the board's port (*USB-SERIAL CH340* or *CP210x*).
   If it does not connect: hold the **BOOT** button on the board, click Connect, and release BOOT once it connects.
4. *Flash Address* **0x0**, choose the downloaded `.bin` file, click **Program**. It takes 30–60 seconds.
5. Press **RST** on the board (or unplug and plug it back).

With Python instead: `python -m pip install esptool`, then
`python -m esptool --port COMx --baud 921600 write_flash 0x0 <file>.bin`.

## 3. Wi-Fi on the panel

1. After flashing, the panel opens its own Wi-Fi network **`CYD-Monitor-Setup`**.
2. Join it with a phone and open **http://192.168.4.1**.
3. Enter your Wi-Fi name (*Sieć WiFi*) and password (*Hasło*). Leave the server IP (*IP serwera*) empty — the panel
   finds the PC by itself.
4. Save. The panel restarts and shows its IP address for a moment — note it down (it is the panel's settings page).

From now on the panel can be powered from any USB charger; it does not have to sit next to the PC.

## 4. The server on the PC

1. Right-click the downloaded zip → **Properties** → tick **Unblock** → OK. (Otherwise Windows warns about every file.)
2. Unpack it to a permanent place, e.g. `C:\CYD-Monitor` — not *Downloads*: the autostart remembers the location.
3. Double-click **`scripts\enable-service.cmd`** and confirm the administrator prompt. It:
   - adds Windows Firewall rules for the server, limited to the local network,
   - starts the server in the background (no window),
   - registers the autostart (the scheduled task *CYD-Watchdog*), so the server comes back after every restart.
4. Within a few seconds the panel shows your screen.

If Windows shows *"Windows protected your PC"*: **More info** → **Run anyway**.
Just to try it without autostart: double-click `cyd-monitor.exe` (a console window; Ctrl+C stops it) and allow it on
private networks when the firewall asks.

## 5. Everyday use

- **Gestures on the panel:** drag = pan, hold 0.7 s = zoom 1× → 2× → 4×, double tap = mirror ↔ scanner page.
- **Panel settings:** `http://<panel IP>` from a phone or the PC — contrast, gamma, brightness, sharpening, scaling,
  rotation, Wi-Fi. *Statystyki na ekranie* shows fps, kB/s, the signal and the panel's IP in the corner.
- **Scanner web page** (with SDR Oxide): `http://<PC IP>:7793`, or the yellow **Skaner SDRoxide** button at the top of
  the panel's settings page. Add it to the phone's home screen.

## 6. Optional: the panel as a separate second monitor

By default the panel mirrors the main screen. To have it as its own small monitor:

1. As administrator: `powershell -ExecutionPolicy Bypass -File scripts\install-virtual-display.ps1` — installs the
   signed [Parsec VDD](https://github.com/nomi-san/parsec-vdd) driver.
2. Stop the service: `scripts\stop-cyd-watchdog.cmd`, then `scripts\stop-cyd.cmd`.
3. In a console in the folder: `cyd-monitor.exe --virtual`. Windows gets a new monitor; the choice is saved in
   `%LOCALAPPDATA%\cyd-monitor\settings.json`. Stop it with Ctrl+C.
4. Run `scripts\enable-service.cmd` again.

Back to mirroring the main screen: the same steps with `cyd-monitor.exe --monitor 1`.

## 7. Optional: SDR Oxide and the Ulanzi dial

- The scanner page reads SDR Oxide's built-in servers: **rigctld** on port **4532** (SDR 2M) and **4533** (SDR 70CM),
  and **TCI** on **50001** / **50002** for the web page's spectrum and audio (Oxide: Settings → Servers, for each
  radio). The complete scanner package with these settings, two receivers and 715 channels is described in the
  [SP9TKN build of SDR Oxide](https://github.com/SP9TKN/sdroxide-brown).
- The Ulanzi D100H dial: [ulanzi-plugin/README.md](../ulanzi-plugin/README.md).

## Troubleshooting

| What you see | What to do |
|---|---|
| The panel has Wi-Fi but no picture | Run `scripts\enable-service.cmd` again (it says if the server already runs). Are the PC and the panel on the same network? A router's "guest" network often blocks devices from each other. Workaround: enter the PC's IP in *IP serwera* on the panel's settings page. |
| `BRAK SERWERA` (no server) on the scanner page | the server is not running or the panel cannot reach it — see above |
| `OXIDE WYLACZONY` (Oxide is off) | SDR Oxide is closed — press START on the panel or start Oxide |
| Inverted colours or a shifted picture | flash the other firmware variant (ST7789 ↔ ILI9341) |
| The panel sparkles or shows wrong pixels | this unit cannot take the fast display clock — build the firmware with `-SpiHz 65000000` ([README](../README.md#running-from-source)) |
| *Unknown USB device* when connecting the panel | a cable without data lines, a loose socket or a USB hub — try another cable and a port directly in the PC |
| The bottom buttons on the scanner page react to a touch at the top | toggle *Odwróć dotyk skanera* on the panel's settings page |

Logs (paste into the Explorer address bar): `%LOCALAPPDATA%\cyd-monitor\log.txt` (server),
`%LOCALAPPDATA%\cyd-monitor\cyd-watchdog.log` (autostart), `%LOCALAPPDATA%\cyd-monitor\oxide.log` (Oxide started from
the panel).

## Uninstall

1. Run `scripts\disable-service.cmd`.
2. Delete the program folder and `%LOCALAPPDATA%\cyd-monitor`.
3. Firewall rules: as administrator, `Get-NetFirewallRule -DisplayName 'CYD Monitor*' | Remove-NetFirewallRule`
   (or *Windows Defender Firewall with Advanced Security* → Inbound Rules → delete *CYD Monitor (TCP)* and *(UDP)*).

## Ports

TCP 7788 — panel control; UDP 7787 — discovery; UDP 7789/7790 — picture; TCP 7792 — Ulanzi dial bridge (localhost
only); TCP 7793 — scanner web page. No port needs to be opened on the router — **do not forward any**: the pages have
no password. For access from outside use a VPN such as Tailscale.
