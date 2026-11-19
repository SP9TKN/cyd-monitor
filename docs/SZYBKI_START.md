# CYD-Monitor — szybki start

> 🇬🇧 English version: [QUICKSTART.md](QUICKSTART.md)

Dla kogoś, kto nigdy nie wgrywał programu do ESP32. Około 15 minut.

## Co trzeba mieć

- Komputer z **Windows 10 albo 11, 64-bit**.
- Płytkę **ESP32-2432S028R** („Cheap Yellow Display”, ekran 2,8") i **kabel USB z liniami danych** (nie sam „do
  ładowania”).
- **WiFi 2,4 GHz** w domu (panel nie obsługuje 5 GHz). Komputer, panel i telefon muszą być w tej samej sieci.
- Opcjonalnie: [SDR Oxide — wersja SP9TKN](https://github.com/SP9TKN/sdroxide-brown) do strony skanera i pokrętło
  Ulanzi D100H.

## 1. Pobierz

Z **[Releases](https://github.com/SP9TKN/cyd-monitor/releases)** weź:

- firmware: **`…_ILI9341_…_flash_at_0x0.bin`** (większość płytek) albo **`…_ST7789_…_flash_at_0x0.bin`** (nowsze płytki
  „v3” — wybierz, gdy po wgraniu pierwszego kolory są odwrócone albo obraz przesunięty),
- serwer: **`cyd-monitor-server_…_win-x64.zip`**.

## 2. Wgraj program do panelu (w przeglądarce, bez instalowania)

1. Podłącz płytkę do komputera kablem USB. Windows zwykle sam instaluje sterownik CH340.
2. Otwórz **https://espressif.github.io/esptool-js/** w Chrome albo Edge.
3. *Baudrate* ustaw na 921600, kliknij **Connect** i wybierz port płytki (*USB-SERIAL CH340* albo *CP210x*).
   Gdy nie łączy: przytrzymaj przycisk **BOOT** na płytce, kliknij Connect i puść BOOT, gdy się połączy.
4. *Flash Address* **0x0**, wybierz pobrany plik `.bin`, kliknij **Program**. Trwa 30–60 sekund.
5. Naciśnij **RST** na płytce (albo odłącz i podłącz kabel).

Zamiast przeglądarki, z Pythonem: `python -m pip install esptool`, potem
`python -m esptool --port COMx --baud 921600 write_flash 0x0 <plik>.bin`.

## 3. WiFi panelu

1. Po wgraniu panel wystawia własną sieć WiFi **`CYD-Monitor-Setup`**.
2. Połącz się z nią telefonem i otwórz **http://192.168.4.1**.
3. Wpisz nazwę i hasło swojej sieci WiFi. Pole „IP serwera” zostaw puste — panel sam znajdzie komputer.
4. Zapisz. Panel uruchomi się ponownie i przez chwilę pokaże swój adres IP — zapisz go (to strona ustawień panelu).

Od teraz panel może być zasilany z dowolnej ładowarki USB, nie musi stać przy komputerze.

## 4. Serwer na komputerze

1. Kliknij pobrany zip prawym przyciskiem → **Właściwości** → zaznacz **Odblokuj** → OK. (Inaczej Windows będzie
   straszył przy każdym pliku.)
2. Rozpakuj do stałego miejsca, najlepiej `C:\CYD-Monitor` — nie zostawiaj w „Pobranych”: autostart zapamięta to
   miejsce.
3. Kliknij dwukrotnie **`scripts\enable-service.cmd`** i potwierdź okno administratora. Skrypt:
   - dodaje reguły zapory Windows dla serwera, tylko dla sieci lokalnej,
   - uruchamia serwer w tle (bez okna),
   - rejestruje autostart (zadanie *CYD-Watchdog*), więc serwer wstaje sam po każdym restarcie.
4. W ciągu kilku sekund panel pokaże obraz komputera.

Gdy Windows pokaże „System Windows ochronił ten komputer”: **Więcej informacji** → **Uruchom mimo to**.
Żeby tylko spróbować, bez autostartu: kliknij dwukrotnie `cyd-monitor.exe` (okno konsoli; Ctrl+C zatrzymuje) i pozwól
na dostęp do sieci prywatnej, gdy zapyta zapora.

## 5. Na co dzień

- **Gesty na panelu:** przeciągnięcie = przesuwanie, przytrzymanie 0,7 s = zoom 1× → 2× → 4×, podwójne stuknięcie =
  lustro ↔ strona skanera.
- **Ustawienia panelu:** `http://<IP panelu>` z telefonu albo komputera — kontrast, gamma, jasność, wyostrzenie,
  skalowanie, obrót, WiFi. „Statystyki na ekranie” pokazują w rogu fps, kB/s, sygnał i IP panelu.
- **Strona www skanera** (z SDR Oxide): `http://<IP komputera>:7793` albo żółty przycisk **Skaner SDRoxide** na górze
  strony ustawień panelu. Dodaj ją do ekranu głównego telefonu.

## 6. Opcja: panel jako osobny drugi monitor

Domyślnie panel pokazuje główny ekran. Żeby był osobnym małym monitorem:

1. Jako administrator: `powershell -ExecutionPolicy Bypass -File scripts\install-virtual-display.ps1` — instaluje
   podpisany sterownik [Parsec VDD](https://github.com/nomi-san/parsec-vdd).
2. Zatrzymaj usługę: `scripts\stop-cyd-watchdog.cmd`, potem `scripts\stop-cyd.cmd`.
3. W konsoli w folderze programu: `cyd-monitor.exe --virtual`. Windows dostanie nowy monitor; wybór zapisuje się
   w `%LOCALAPPDATA%\cyd-monitor\settings.json`. Zatrzymaj Ctrl+C.
4. Uruchom jeszcze raz `scripts\enable-service.cmd`.

Powrót do lustra głównego ekranu: te same kroki z `cyd-monitor.exe --monitor 1`.

## 7. Opcja: SDR Oxide i pokrętło Ulanzi

- Strona skanera czyta wbudowane serwery SDR Oxide: **rigctld** na porcie **4532** (SDR 2M) i **4533** (SDR 70CM) oraz
  **TCI** na **50001** / **50002** dla widma i dźwięku na stronie www (Oxide: Ustawienia → Servers, dla każdego radia).
  Kompletną paczkę skanera z tymi ustawieniami, dwoma odbiornikami i 715 kanałami opisuje
  [wersja SP9TKN SDR Oxide](https://github.com/SP9TKN/sdroxide-brown).
- Pokrętło Ulanzi D100H: [ulanzi-plugin/README.pl.md](../ulanzi-plugin/README.pl.md).

## Rozwiązywanie problemów

| Co widać | Co zrobić |
|---|---|
| Panel ma WiFi, ale nie pokazuje obrazu | Uruchom ponownie `scripts\enable-service.cmd` (gdy serwer działa, powie to). Komputer i panel w tej samej sieci? Sieć „dla gości” w routerze często blokuje urządzenia między sobą. Obejście: wpisz IP komputera w polu „IP serwera” na stronie ustawień panelu. |
| `BRAK SERWERA` na stronie skanera | serwer nie działa albo panel go nie widzi — patrz wyżej |
| `OXIDE WYLACZONY` | SDR Oxide jest zamknięty — naciśnij START na panelu albo uruchom Oxide |
| Odwrócone kolory albo przesunięty obraz | wgraj drugi wariant firmware (ST7789 ↔ ILI9341) |
| Panel „iskrzy” albo ma przekłamane piksele | ten egzemplarz nie wytrzymuje szybkiego zegara wyświetlacza — zbuduj firmware z `-SpiHz 65000000` ([README](../README.pl.md#uruchomienie-ze-źródeł)) |
| „Nieznane urządzenie USB” przy podłączaniu panelu | kabel bez linii danych, luźne gniazdo albo hub USB — inny kabel i port bezpośrednio w komputerze |
| Przyciski na dole strony skanera reagują na dotyk u góry | przełącz „Odwróć dotyk skanera” na stronie ustawień panelu |

Logi (wpisz w pasku adresu Eksploratora): `%LOCALAPPDATA%\cyd-monitor\log.txt` (serwer),
`%LOCALAPPDATA%\cyd-monitor\cyd-watchdog.log` (autostart), `%LOCALAPPDATA%\cyd-monitor\oxide.log` (Oxide uruchomiony
z panelu).

## Odinstalowanie

1. Uruchom `scripts\disable-service.cmd`.
2. Usuń folder programu i `%LOCALAPPDATA%\cyd-monitor`.
3. Reguły zapory: jako administrator `Get-NetFirewallRule -DisplayName 'CYD Monitor*' | Remove-NetFirewallRule`
   (albo „Zapora Windows Defender z zabezpieczeniami zaawansowanymi” → Reguły przychodzące → usuń „CYD Monitor (TCP)”
   i „(UDP)”).

## Porty

TCP 7788 — sterowanie panelem; UDP 7787 — wykrywanie; UDP 7789/7790 — obraz; TCP 7792 — most pokrętła Ulanzi (tylko
ten komputer); TCP 7793 — strona www skanera. Na routerze nie trzeba niczego otwierać — **nie przekierowuj portów**:
strony nie mają hasła. Dostęp spoza domu najlepiej przez VPN, np. Tailscale.
