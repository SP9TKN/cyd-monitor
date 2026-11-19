# Protokół v5: kanał danych UDP odporny na straty (projekt)

Status: projekt do zatwierdzenia, nic z tego nie jest jeszcze zaimplementowane.
Dotyczy kopii projektu (`cyd-monitor-udp`); produkcyjny `cyd-monitor` pozostaje na v4.

## 0. Streszczenie

Dziś obraz idzie do panelu jednym strumieniem TCP. Pomiary z 21.09 pokazują, że
zacinanie obrazu (ok. 2 razy na minutę, średnio 0,9 s) to nie błąd obciążeniowy
kodu, tylko **krótkie przerwy łącza do panelu** (0,2–0,9 s, sonda 100 pakietów/s:
każda luka ≥200 ms dała przerwę strumienia), które stos TCP panelu wzmacnia
średnio 1,9×: okno wynosi zaledwie 5760 B (4 segmenty), więc po przerwie strumień
czeka na timeout retransmisji zamiast ruszyć od razu.

Proponowane rozwiązanie: dane pikseli idą przez **UDP**, a niezawodność wynika ze
**stanu ekranu**, nie z bajtów. Zgubiony datagram nie jest retransmitowany bajt w
bajt; serwer po prostu wysyła ponownie **aktualną zawartość** kafli, które ten
datagram niósł. Po powrocie łącza obraz rusza natychmiast, a przerwy krótsze niż
ok. 250 ms przestają być widoczne. TCP zostaje jako kanał sterowania (opcje, dotyk,
tekst, tablice JPEG), więc nic, co dziś działa, nie znika.

**Uczciwie o zysku:** UDP nie usuwa samych przerw łącza. Szacunek z danych: ok.
połowa łącznego czasu zamrożenia (7,5 s -> ok. 3,8 s na 5 min), ale większość
dłuższych zacięć zostaje (10 -> ok. 7–8). Największy zysk daje połączenie z
usunięciem przyczyny przerw łącza (sekcje 1.2 i 13).

## 1. Punkt wyjścia: co zmierzono

Detektor przerw w serwerze (`[dip]`) zapisuje każdy moment, gdy panel przez ≥250 ms nie
potwierdza klatki, razem ze stanem TCP w tej chwili.

| Obserwacja (21.09, 18:20–19:52, 190 przerw) | Wniosek |
|---|---|
| ok. 2 przerw/min, średnio 0,9 s, ok. 2,8% czasu | to jest widoczne zacinanie |
| częstość taka sama przy 150 kB/s i przy 40 kB/s, przy 15 i 30 fps, przy `InFlight` 3 i 6, bez nakładki statystyk, bez serwera www | nie zależy od obciążenia ani od ilości danych |
| pętla klatek serwera stoi ≤63 ms (dwa wyjątki: 234 i 437 ms) | serwer nie zwalnia |
| brak okresowości, przerwy grupują się po 2–3 | zachowanie jak przy zakłóceniach łącza |
| w ok. połowie przerw TCP musiał retransmitować po timeoucie, w drugiej połowie potwierdzenie przyszło po 0,35–1,15 s bez retransmisji, w ok. 15% okno panelu spadło do 16 B | trzy odmiany tego samego: krótka przerwa łącza plus okno 4 segmentów |
| sonda 100 pakietów/s: każda luka ≥200 ms w odpowiedziach (8 z 8) dała przerwę TCP | przerwy łącza są prawdziwe (sekcja 1.1); wcześniejszy wniosek z pingów 5 Hz, że „łącze żyje w 56 z 62 przerw”, był błędem niedopróbkowania |
| rdzeń 0 panelu ok. 40%, rdzeń 1 ok. 9%, wolny RAM ≥92 kB | zasoby nie są wąskim gardłem |

Konfiguracja stosu w firmware (`sdkconfig`, wkompilowana w prekompilowane biblioteki
Arduino): `TCP_WND_DEFAULT=5760`, `TCP_MSS=1436`, `TCP_RECVMBOX_SIZE=6`,
`UDP_RECVMBOX_SIZE=6`. Przy 4 segmentach w oknie odzyskiwanie po utracie przez
szybką retransmisję (3 duplikaty ACK) jest niemożliwe, więc zostaje timeout.

### 1.1 Wyniki sondy 100 pakietów/s (21.09, 20:06–20:11)

Do panelu szło 100 pakietów ICMP na sekundę po 600 B (rozmiar zbliżony do datagramu
UDP), przez 5 minut, podczas normalnej pracy strumienia. To dokładnie ten kanał, który
zobaczyłby UDP. Wcześniejsze pingi 5 Hz były **za rzadkie** i dawały błędny obraz
(„ping przechodzi w większości przerw”); ta sonda go prostuje.

| Pomiar (30 000 pakietów) | Wynik |
|---|---|
| zgubione (brak odpowiedzi w 400 ms) | 413 = 1,38% (przy obciążeniu sondą i strumieniem naraz) |
| RTT | mediana 7 ms, p95 19, p99 55, p99,9 178, maks. 428 ms |
| serie kolejnych strat | 81: 28 po 1 pakiecie, 33 po 2, 8 po 3–5, 8 po 6–20, 4 dłuższe niż 20 (najdłuższa 87 pakietów, ok. 870 ms) |
| odpowiedzi w zmienionej kolejności | 102 (0,3%) |
| luki w odpowiedziach ≥60 ms | 48 w 5 min (9,6/min): 18 po 60–100 ms, 22 po 100–200, 5 po 200–400, 3 powyżej 400 |
| przerwy TCP w tym samym oknie | 10; w 9 z nich sonda zobaczyła lukę ≥100 ms, w 7 lukę >200 ms |
| luki radia ≥200 ms, które nie dały przerwy TCP | 0 z 8 |
| średnia przerwa TCP vs średnia luka radia w tym samym oknie | 799 ms vs 413 ms, czyli TCP wydłuża ją ok. **1,9×** |

Wniosek: **główną przyczyną są prawdziwe krótkie przerwy łącza do panelu** (0,2–0,9 s,
ok. 1,6 na minutę), a nie pojedyncze zgubione pakiety. Z tych danych nie da się
rozdzielić, czy to radio (zakłócenia 2,4 GHz, router), czy sam panel (sterownik WiFi
ESP32); ICMP obsługuje ten sam stos i to samo radio. Wcześniejsze testy (przerwy
niezależne od ilości danych, klatek i obciążenia) wskazują raczej na łącze niż na
obciążenie panelu.

### 1.2 Co da UDP, a czego nie da

UDP usuwa **wzmocnienie** przez TCP, nie samą przerwę łącza:

| | TCP (dziś) | UDP (v5, szacunek z tych danych) |
|---|---|---|
| przerwa łącza 100–200 ms | rzadko widoczna (2 z 22 dały przerwę TCP ≥250 ms) | niewidoczna |
| przerwa łącza 200–900 ms | zamrożenie ok. 1,2–3× dłuższe (0,4–1,6 s) | zamrożenie ok. tyle, co przerwa + kilkadziesiąt ms |
| łączny czas zamrożenia w 5 min | ok. 7,5 s | ok. 3,8 s (przerwy ≥250 ms wg sondy + ok. 50 ms każda) |
| liczba widocznych zacięć w 5 min | 10 | ok. 7–8 (zostają przerwy ≥250 ms) |

Czyli w przybliżeniu **połowa czasu zamrożenia zniknie, ale większość dłuższych
zacięć zostanie**, bo to sama przerwa łącza. Największy zysk da połączenie obu
działań: usunięcie przyczyny przerw łącza (sekcja 13) i skrócenie ich skutku (ten
projekt). To szacunek na jednej sondzie z 5 minut, do potwierdzenia symulatorem (sekcja 11).

## 2. Cele i nie-cele

Cele:
- Krótka przerwa łącza (do ok. 250 ms) i utrata pojedynczych pakietów są
  niewidoczne; dłuższa przerwa trwa tyle, co sama przerwa, bez dokładania timeoutu TCP.
- Po powrocie łącza obraz wraca natychmiast (bez wykładniczego wydłużania timeoutu).
- Pełna zgodność z v4 w obie strony: stary panel z nowym serwerem i nowy panel ze
  starym serwerem działają jak dziś.
- Kodowanie kafli (`ENC_SOLID/RAW/RLE/PAL1/PAL4/JPEG`) i dekoder zostają bez zmian.

Nie-cele (na później):
- Szyfrowanie i uwierzytelnianie (jak w v4: sieć lokalna, zaufana).
- Przeniesienie dotyku i opcji na UDP (są rzadkie i małe, TCP wystarczy).
- Korekcja błędów (FEC); patrz sekcja 13.

## 3. Idea: niezawodność przez stan, nie przez bajty

Kodowanie v4 jest różnicowe tylko w tym sensie, że serwer wysyła **te kafle, które się
zmieniły** względem `prev` (tego, co panel ma dostać). Sama treść każdego paska jest
**absolutna** (SOLID, RLE, PAL, RAW, JPEG kodują piksele, nie różnicę). To kluczowe:
zgubiony pasek można zastąpić dowolnym późniejszym paskiem tych samych kafli.

Zatem:
1. Serwer prowadzi rejestr: dla każdego z 300 kafli numer ostatniego datagramu,
   który go niósł, i czy panel go potwierdził.
2. Gdy datagram zostaje uznany za zgubiony, kafle, dla których był **ostatnim**
   nośnikiem, dostają flagę `ForceTile`. Następna klatka koduje je z bieżącego
   `cur`, nawet jeśli `cur == prev`.
3. Kafel, który w międzyczasie poszedł w nowszym datagramie, nie jest wysyłany
   ponownie: ważny jest los nowszego datagramu.
4. Panel przy każdym kaflu pamięta numer datagramu, z którego go narysował, i
   odrzuca spóźnione starsze dane (kolejność UDP nie jest gwarantowana).

Skutek: nie ma blokowania kolejki (head-of-line blocking) ani timeoutu retransmisji
opartego o RTT. Wykrycie straty zajmuje 3 następne potwierdzenia (ok. 30–60 ms) lub
krótki timer (40–80 ms), a naprawa to jeden datagram z aktualną treścią. Przy
dłuższej przerwie łącza okno się zamyka (brak potwierdzeń), a po jej końcu pierwszy
ACK od razu odblokowuje wysyłkę; kafle zmienione w międzyczasie idą w jednej serii z
bieżącą treścią, bez odtwarzania starych pakietów.

## 4. Architektura

```
              TCP 7788 (bez zmian)  HELLO, CONFIG, OPT, OPTACK, TEXT, JPEGTABLES,
   PC  <────────────────────────────> TOUCH, DIAG, PING + NOWE: UDPHELLO, TRANSPORT
 serwer
              UDP 7789 (nowe)       DATA  serwer -> panel  (prostokąty kafli)
   PC  <────────────────────────────> ACK   panel -> serwer  (co ~16 ms)
```

- TCP jest jednocześnie kanałem życia sesji: zamknięcie TCP kończy sesję, jak dziś.
- Dane pikseli przechodzą na UDP po udanej negocjacji (sekcja 5). Do tego czasu
  i przy każdym problemie obowiązuje v4 (dane w `MSG_FRAME` przez TCP).
- Pozostaje rozgłaszanie UDP 7787 do wykrywania serwera (bez zmian).

## 5. Negocjacja i zgodność z v4

Wersja w `HELLO` zostaje **4**: serwer v4 odrzuca każdą inną. Możliwości ogłasza
się dodatkowymi wiadomościami, wysyłanymi tylko wtedy, gdy druga strona zadeklarowała,
że je rozumie (v4 zamyka sesję po nieznanej wiadomości, więc nigdy nie wolno wysłać
niczego nowego „w ciemno").

1. Panel wysyła `HELLO` (v4) jak dziś.
2. Serwer v5 odpowiada `CONFIG` z `flags` bit0 = 1 (dziś to bajt zawsze 0, którego
   stare firmware'y nie czytają). Serwer v4 zostawia 0.
3. Panel v5, widząc bit0, otwiera gniazdo UDP i wysyła `MSG_UDPHELLO (0x07)`:
   `u16 udpPort`. Stary panel nie widzi bitu, więc nic nie wysyła.
4. Serwer odpowiada `MSG_TRANSPORT (0x86)`: `u8 mode (1=udp)`, `u16 session`,
   `u16 serverUdpPort`. Od tej chwili serwer nie wysyła `MSG_FRAME` w tej sesji.
5. Panel wysyła pierwszy `ACK` (pusty) na `serverIp:serverUdpPort`; serwer poznaje
   adres nadawcy i uznaje ścieżkę za otwartą.
6. Dowolna strona może wrócić do TCP: serwer wysyła `MSG_TRANSPORT (mode=0)`
   (np. gdy ACK-i milczą >3 s przy żywym TCP: zapora blokuje UDP) i robi pełne
   odświeżenie. Faza 2.

Macierz zgodności:

| Serwer \ Panel | v4 | v5 |
|---|---|---|
| **v4** (produkcja) | jak dziś | panel nie widzi bitu, zostaje na TCP |
| **v5** | brak `UDPHELLO`, zostaje na TCP | UDP |

## 6. Format na drucie (liczby little-endian)

Datagram mieści się w jednym pakiecie IP bez fragmentacji: nagłówki UDP+IP zabierają
28 B z MTU 1500, przyjmujemy **≤1400 B** danych.

### 6.1 DATA (serwer -> panel)

| Bajty | Pole |
|---|---|
| 1 | `type = 0xD1` |
| 1 | `flags` (bit0: trwa pełne odświeżenie) |
| 2 | `session` |
| 4 | `seq` (rośnie o 1 na każdy wysłany datagram, także puste) |
| 1 | `rectCount` |
| ... | `rectCount` prostokątów |

Prostokąt: `u8 tx` (0–19), `u8 ty` (0–14), `u8 tw` (1–20 kafli), `u8 mode` (`ENC_*`),
`u16 len`, `payload[len]`. Wysokość to zawsze jeden kafel (16 px); szerokość kafla
16 px. Nagłówek prostokąta ma 6 B (w v4 nagłówek paska ma 13 B).

Pusty datagram (`rectCount = 0`) służy jako keepalive i **sonda ogona**: wymusza ACK,
gdy końcówka serii mogła zaginąć.

### 6.2 ACK (panel -> serwer)

| Bajty | Pole |
|---|---|
| 1 | `type = 0xD2` |
| 1 | `flags` |
| 2 | `session` |
| 4 | `highest`: najwyższy `seq`, jaki panel w pełni zastosował |
| 4 | `bitmap`: bit k = datagram `highest-1-k` zastosowany |
| 2 | `fps10` (jak dziś w `MSG_ACK`) |
| 2 | `blitMs` |
| 1 | `freeSlots`: wolne bufory strip na panelu |
| 1 | `rxDrops`: datagramy odrzucone z braku bufora od ostatniego ACK |

18 B. Wysyłany co ok. 16 ms (jeśli było cokolwiek nowego), co 4 zastosowane
datagramy albo **natychmiast**, gdy przyjdzie datagram z luką w numeracji
(przyspiesza wykrycie straty). Utrata ACK nie szkodzi: bitmapa 32 datagramów daje
redundancję.

Datagram „zastosowany" znaczy: wszystkie jego prostokąty zostały zdekodowane i
zakolejkowane do rysowania (albo świadomie pominięte jako nieaktualne). Brak wolnego
bufora, brak tablic JPEG lub błąd dekodowania oznacza **brak potwierdzenia**, więc
serwer ponowi kafle. To naturalne wywieranie presji zwrotnej.

## 7. Serwer

### 7.1 Zmiany w `TileEncoder`

- Wejście `forceTiles` (bitmapa 300 kafli) łączone z `_dirty`: kafel wymuszony jest
  emitowany nawet przy `cur == prev`.
- Limit `MaxRectBytes ≈ 1380`: run kafli, którego zakodowany rozmiar go przekracza,
  jest dzielony na pół (rekurencyjnie), aż się zmieści. Wymaga rozdzielenia
  `EmitStrip` na czyste kodowanie i zatwierdzenie (`prev`), żeby można było kodować
  próbnie. Wąskie paski JPEG (co najmniej 1 MCU = kafel) są dopuszczalne.
- Wynik jako lista prostokątów `(tx, ty, tw, offset, len)` obok obecnego bufora.
- Dotychczasowe `MaxFrameBytes`, `_forceResumeRow`, tryb wideo bez zmian.

### 7.2 Nowe elementy

- `UdpSender`: gniazdo, kolejka z pacingiem, pakowanie prostokątów do datagramów
  (zachłannie do 1400 B), numeracja, rejestr datagramów (pierścień 256: czas
  wysłania i bitmapa kafli).
- Rejestr kafli: `LastSeq[300]`, `Acked[300]`, `ForceTile[300]`.
- Odbiór ACK w osobnym wątku na porcie 7789; mapowanie na sesję po `session` i adresie.
- Estymator RTT (SRTT, RTTVAR jak w RFC 6298, z granicami 8–200 ms).

### 7.3 Wykrywanie straty

Datagram `s` jest uznany za zgubiony, gdy:
- panel potwierdził datagram `≥ s+3`, a `s` nie (próg kolejności), albo
- od wysłania minęło więcej niż `max(40 ms, SRTT + 4·RTTVAR + 10 ms)` bez ACK
  (ogon serii).

Dla każdego kafla z `s`, który ma `LastSeq == s`, ustawiana jest `ForceTile`.
Kafel z nowszym nośnikiem jest pomijany. Redukcja okna raz na epizod strat.

### 7.4 Sterowanie przepływem i pacing

UDP nie ma sterowania przepływem, więc jest ono w aplikacji:
- w locie co najwyżej `min(cwnd, freeSlots z ostatniego ACK)` datagramów;
- `cwnd` start 16, +1 na RTT bez strat, ×0,7 po epizodzie strat, zakres 4–64;
- limit szybkości (token bucket) start 500 kB/s, +5% na czystą sekundę, ×0,8 po
  stracie, zakres 60–700 kB/s;
- minimalny odstęp między datagramami ok. 0,7 ms (zegar 1 ms jest już włączony w
  serwerze przez `timeBeginPeriod`), bo skrzynka odbiorcza UDP panelu mieści
  tylko 6 pakietów i nie wolno zasypać jej serią.

Gdy okno lub limit są pełne, pętla klatek **nie koduje** (jak dziś przy zamkniętej
bramce): kafle po prostu zostają brudne, więc nic nie ginie.

### 7.5 Pozostałe

- Zamiast bramki `InFlight` (w klatkach) działa okno w datagramach; `[dip]` zastępuje
  metryka wieku najstarszego nieaktualnego kafla (sekcja 10).
- Co 1 s wymuszane odświeżenie jednego, rotującego wiersza kafli (pełny obieg co
  15 s), jako tania polisa na rozjazd, którego rejestr nie wykrył.
- Pełne odświeżenie przy starcie sesji (`forceFull`) idzie tym samym torem, z pacingiem.
- Na starcie sesji i przy zmianie parametrów obrazu bez zmian: `forceFull`.

## 8. Panel

### 8.1 Odbiór

Zadanie `udpRxTask` (rdzeń 0, priorytet wyższy niż `netTask`, niższy niż WiFi i
`tcpip`), gniazdo lwIP (`recvfrom`, blokujące z limitem 50 ms):

1. sprawdź `type`, `session`, adres nadawcy;
2. dla każdego prostokąta: pobierz wolny bufor DMA z `freeQ` **bez czekania**;
   zdekoduj istniejącym `decodeStrip`; zakolejkuj `JOB_STRIP` do `drawQ`;
3. jeśli któryś prostokąt nie ma bufora, tablic JPEG lub się nie dekoduje: nie
   potwierdzaj tego datagramu (już zakolejkowane prostokąty mogą zostać narysowane:
   ponowienie z większym `seq` jest idempotentne);
4. zaktualizuj `highest` i `bitmap`, wyślij `ACK` według reguł z 6.2.

Nieaktualność: `tileSeq[300]` (u32). Prostokąt jest stosowany tylko wtedy, gdy jego
`seq` jest większy od `tileSeq` **wszystkich** pokrytych kafli; wtedy `tileSeq` jest
podnoszone. Inaczej pomijany bez śladu (uznany za zastosowany).

### 8.2 Rysowanie

`drawTask` bez zmian w logice rysowania pasków. Po prostokątach każdego datagramu
`udpRxTask` dokłada znacznik końca porcji, żeby `drawTask` zrobił `waitDMA/endWrite`
i zwolnił bufory jak dziś po `JOB_FRAME_END`. W trybie UDP potwierdzenia wysyła
`udpRxTask`, a nie `drawTask`.

### 8.3 Pamięć

- bufor odbioru 1500 B, `tileSeq` 1,2 KB;
- rekomendowane `CYD_STRIP_BUFFERS` 4 -> 6 (+20 KB; wolny RAM ≥92 kB, więc zostaje ≥72 kB
  dla dynamicznych buforów WiFi);
- brak dużego pierścienia: dekodowanie w miejscu, nadmiar jest odrzucany, a serwer
  ponawia.

### 8.4 Co zostaje

`netTask` (TCP) bez zmian, z tą różnicą, że po `TRANSPORT(udp)` nie oczekuje
`MSG_FRAME`. Ustawienia, panel www, dotyk, OPT, `MSG_JPEGTABLES` i `MSG_TEXT` bez zmian.

## 9. Przypadki brzegowe

| Sytuacja | Zachowanie |
|---|---|
| duplikat lub nakładanie się prostokątów | idempotentne dzięki `tileSeq` |
| zmiana kolejności | starsze dane odrzucone przez `tileSeq` |
| utrata ACK | bitmapa 32 datagramów; >32 bez ACK = starsze uznane za zgubione, kafle wysłane ponownie |
| fałszywa strata (ACK spóźniony) | zbędne ponowienie kafli, nieszkodliwe |
| zapora blokuje UDP (ACK nie wracają) | okno się zamyka; faza 2: po 3 s powrót do TCP i wpis w logu z podpowiedzią |
| brak tablic JPEG w chwili prostokąta JPEG | brak potwierdzenia; ponowienie po dotarciu tablic (TCP) |
| restart panelu lub serwera | zamknięcie TCP kończy sesję; nowa sesja, nowy `session` |
| przerwa radiowa 0,3–1 s | okno się zamyka, po powrocie łącza ACK wraca od razu i kafle są ponawiane (bez wykładniczego backoffu TCP) |
| MTU | datagram ≤1400 B, nigdy nie fragmentowany |
| dotyk i opcje w trakcie UDP | idą TCP; rzadkie zacięcie do 1 s możliwe (jak dziś) |

## 10. Obserwowalność

Serwer (linie w `log.txt`, jak dotychczas):
- co 10 s: `[udp] wyslano N dgram (X kB/s), strat Y (Z%), ponowien kafli K, srtt A ms, cwnd C, limit R kB/s`;
- `[stale] T ms`: gdy najstarszy brudny kafel czeka na potwierdzenie >250 ms. To
  jedyna metryka, która mierzy widoczne zacinanie i zastępuje `[dip]`.

Panel (`/api/state`): `rxDgrams`, `rxDrops` (brak bufora), `rxStale` (odrzucone jako
nieaktualne), `maxRxGapMs` (najdłuższa przerwa między datagramami).

## 11. Plan testów

1. **Symulator na PC** (`--udpsim`, bez sprzętu). Enkoder + `UdpSender` + kanał
   ze stratami + model panelu w C# (istniejący `SelfTest.Decode`, `tileSeq`,
   generowanie ACK). Kanał: strata losowa 0,1–0,3%, epizody grupowe (Gilbert-Elliott),
   opóźnienie 5–10 ms z jitterem, przemieszanie 0,5%, epizody wyłączenia 300–1000 ms
   raz na minutę. Sprawdzane: (a) zbieżność: po zatrzymaniu źródła ekran panelu jest
   równy źródłu w ≤200 ms plus długość epizodu; (b) brak trwałego rozjazdu w 10 min
   losowej treści; (c) opóźnienie nieaktualnych kafli (p99); (d) przepustowość i
   narzut ponowień.
2. **Kalibracja modelu TCP** w tym samym symulatorze (okno 4 segmentów, RTO 300 ms,
   ta sama strata) tak, żeby odtwarzał zmierzone ok. 2 przerw/min, śr. 0,9 s. Dopiero
   skalibrowany model pozwala uczciwie porównać: ile przerw zniknie po przejściu na UDP.
   To jest **bramka decyzyjna**: jeśli symulator nie pokaże wyraźnej poprawy, nie
   ruszamy firmware'u.
3. **Sprzęt, etap A**: firmware v5 z produkcyjnym serwerem v4 (test zgodności
   wstecznej: ma działać identycznie jak dziś).
4. **Sprzęt, etap B**: serwer v5 z kopii, UDP włączone. Porównanie `[stale]` z dzisiejszym
   `[dip]` w tych samych porach dnia, z równoległym pomiarem pingów.

## 12. Ryzyka i niewiadome

- **Największe ryzyko: to nie transport jest głównym problemem.** Sonda (1.1) pokazuje,
  że przyczyną są przerwy samego łącza; v5 skraca ich widoczny skutek mniej więcej
  o połowę, ale ich nie usuwa. Jeśli celem jest „bez zacinania”, samo v5 nie wystarczy.
- **Nieznane źródło przerw łącza**: radio (zakłócenia 2,4 GHz, router) czy sam
  panel (sterownik WiFi ESP32). ICMP tego nie rozdzieli. Rozróżni to test na
  drugim urządzeniu 2,4 GHz albo zmiana kanału routera i ponowna sonda.
- **Odbiór UDP na ESP32 pod obciążeniem.** Skrzynka lwIP ma 6 miejsc; ratuje ją pacing
  serwera i szybki `udpRxTask`. Wymaga weryfikacji na sprzęcie (`rxDrops`).
- **Uczciwość wobec sąsiadów na 2,4 GHz.** UDP nie ma wbudowanej kontroli zatłoczenia;
  dlatego AIMD i limit szybkości.
- **Narzut kompresji z podziału pasków**: prognoza +5–15% bajtów, do zmierzenia w
  symulatorze na realnych zrzutach.
- **Zapora Windows**: przychodzący UDP 7789. Pierwszy komunikat zapory dla aplikacji
  obejmuje oba protokoły w sieci prywatnej; do opisania w rozwiązywaniu problemów i w
  `-Zapora`.
- **Złożoność i regresje**: dlatego v4 zostaje nienaruszone, a v5 domyślnie wyłączone
  (flaga `--udp` lub `udp` w ustawieniach) do czasu wyników etapu B.

## 13. Rozważone alternatywy

| Opcja | Ocena |
|---|---|
| Większe okno TCP przez własną budowę bibliotek Arduino (`TCP_WND` ≥ 16 KB) | Usuwa główną przyczynę wzmocnienia (szybka retransmisja zaczyna działać). Bez zmiany protokołu, ale ciężki łańcuch narzędzi (przebudowa `esp32-arduino-libs`) i kruche utrzymanie. Może być uzupełnieniem, nie zamiennikiem. |
| Mniej danych (jakość, fps, wyostrzanie) | Zmierzone: nie zmienia częstości przerw. |
| Kanał routera 11 -> 6, szerokość 20 MHz (na 2,4 GHz sąsiad DomowyInternet jest na kanale 10 z sygnałem 80%) | Działa na **przyczynę** przerw łącza. Odwracalne, zajmuje minutę, a sondę 100 pakietów/s (`icmp100.ps1`) da się powtórzyć jako pomiar przed i po. **Zalecane jako pierwszy krok**, niezależnie od v5. |
| FEC (np. 1 datagram parzystości na 4) | Odzyskuje pojedyncze straty bez czekania, kosztem +25% bajtów. Do rozważenia po wynikach etapu B. |
| Własny ARQ na TCP | Nie pomaga: przyczyną jest okno i RTO w stosie panelu. |
| QUIC/SCTP | Brak stosów na ESP32. |

## 14. Etapy

| Etap | Zakres | Wynik |
|---|---|---|
| F0 | Symulator, kalibracja modelu TCP, decyzja go/no-go | liczby przed dotknięciem firmware'u |
| F1 | Serwer: enkoder (limit, wymuszanie kafli, lista prostokątów), `UdpSender`, rejestr, negocjacja, `[stale]` | serwer v5 testowany symulatorem |
| F2 | Firmware: `udpRxTask`, negocjacja, ACK, `tileSeq`, statystyki; uruchomienie na sprzęcie z zapasowym v4 | etap A (zgodność wsteczna) |
| F3 | A/B na sprzęcie, strojenie pacingu i timerów, powrót do TCP, dokumentacja, paczka dla kolegi | decyzja o domyślnym UDP |

Wielkości (względne, bez gwarancji terminów): F0 mała, F1 średnia, F2 średnia do
dużej (debugowanie sprzętu), F3 mała do średniej.

## 15. Decyzje do podjęcia

0. **Kolejność prac.** Rekomendacja po danych z sondy: najpierw zmiana kanału routera
   i ponowna sonda (minuty, odwracalne, uderza w przyczynę), równolegle F0 (symulator
   z kalibracją), która da liczby przed jakąkolwiek zmianą firmware'u. Pełne F1–F3
   ma sens, jeśli po zmianie kanału zostaną przerwy ≥250 ms albo jeśli i tak chcemy
   skrócić ich skutek.
1. Czy do testów na sprzęcie wolno wgrać firmware v5 do produkcyjnego panelu (z
   automatycznym zapasowym v4, więc produkcyjny serwer nadal działa)? Alternatywa:
   osobna płytka.
2. Czy zostawiamy dotyk i opcje na TCP (zalecane)?
3. Czy v5 domyślnie wyłączone do czasu wyników etapu B (zalecane)?
4. Czy równolegle zmieniamy kanał routera, żeby oddzielić udział radia od udziału
   transportu?

## 16. Stan wdrożenia (21.09.2026)

Wszystko w kopii `cyd-monitor-udp`; produkcyjny projekt i jego serwer nie były ruszane.

**Serwer (C#), gotowe i przetestowane bez sprzętu**

| Element | Plik | Uwagi |
|---|---|---|
| kodowanie na datagramy, rejestr nośników kafli, ACK, wykrywanie strat, pacing | `UdpEngine.cs` | bez gniazd i zegara: czas jest argumentem |
| gniazdo UDP, wątek odbioru ACK, wątek pacingu (`timeBeginPeriod(1)`) | `UdpLink.cs` | `SIO_UDP_CONNRESET` wyłączony, żeby ICMP „port unreachable” nie zabijał odbioru |
| enkoder: `ForceTile`, `ForceRow`, `SplitLimit`, `MaxRunTiles`, `GetStrips` | `TileEncoder.cs` | dzielenie pasków >1385 B rekurencyjnie na pół |
| negocjacja i przełączanie transportu | `DeviceSession.cs` | patrz niżej |
| symulator kanału ze stratami (`--udpsim`) | `UdpSim.cs` | 3 scenariusze, wszystkie OK |
| ten sam tor przez prawdziwe gniazda na pętli zwrotnej (`--udploop`) | `UdpSim.cs` | panel gubi 2%: zbieżność 0 z 300 kafli niezgodnych |

Przełączanie w `DeviceSession`: `CONFIG.flags` bit 0 tylko przy `--udp`; po `UDPHELLO` czytnik
TCP otwiera gniazdo i odkłada gotowy `UdpLink`; dopiero pętla klatek, **między dwiema klatkami**,
przełącza się (`ActivateUdp`): wysyła `TRANSPORT`, ustawia limity enkodera, wymusza pełne
odświeżenie. Dzięki temu wszystkie klatki TCP wysłane wcześniej poprzedzają `TRANSPORT` na
drucie, a ustawienia enkodera zmienia wyłącznie wątek, który go używa. Powrót na TCP
(`FallBackToTcp`) po 3 s bez ACK przy żywym TCP. Po TCP zostają: `HELLO`, `CONFIG`, `OPT`,
`OPTACK`, `TEXT`, `JPEGTABLES`, dotyk, `DIAG`, oraz **zerowe klatki-keepalive co 1,5 s**
(4-sekundowy limit odczytu panelu dotyczy TCP).

Zmierzone różnice względem projektu:

- Metryka `[stale]` liczy **ciszę ACK** (`AckSilenceUs`: czas od ostatniego ACK, albo od
  pierwszego datagramu wysłanego po nim), a nie wiek najstarszego datagramu. Wiek pojedynczego
  datagramu jest ucinany przy każdym uznaniu go za zgubiony, więc nie obejmowałby całej przerwy;
  cisza ACK odpowiada temu, co mierzył `[dip]` w TCP (klatka bez potwierdzenia >250 ms).
- Okno: `cwnd` 8..12 (pierścień panelu ma 16), tempo 150..700 kB/s sterowane **udziałem strat**
  (EWMA), nie pojedynczą stratą: przy 1% strat WiFi pojedyncze cięcie zwijało tempo.
- Ramka na UDP ≤16 kB (`UdpFrameBytes`), run kafli ≤10 (bufor dekodowania na panelu).

**Firmware (C++), napisane, kompilacja: patrz niżej**

| Element | Miejsce |
|---|---|
| stałe protokołu (`MSG_UDPHELLO`, `MSG_TRANSPORT`, `DG_*`) | `protocol.h` (wersja zostaje 4) |
| `CYD_NO_UDP` (obraz zachowujący się jak v4; `build-firmware.ps1 -NoUdp`) | `config.h` |
| `udpRxTask` (rdzeń 0, priorytet 5): `recvfrom` z limitem 10 ms, ACK co 4 datagramy / 16 ms / od razu przy luce | `cyd-monitor.ino` |
| pierścień 16 × 1400 B (22 kB) + kolejka wolnych slotów | `cyd-monitor.ino` |
| `JOB_DGRAM` w tej samej `drawQ` co paski TCP, więc kolejność rysowania jest zachowana | `cyd-monitor.ino` |
| `drawDatagram`: dekodowanie do `jpgOut[]`, `tileSeq[300]`, rysowanie tylko świeżych kafli | `cyd-monitor.ino` |
| `MSG_CONFIG` (flaga) → `MSG_UDPHELLO`; `MSG_TRANSPORT` | `handleServerMessage` |
| pola w `/api/state`: `udp`, `udpReady`, `rxDgrams`, `rxDrops`, `rxStale`, `maxRxGapMs` | `settingsStateJson` |

Decyzje przy pisaniu firmware'u, których nie było w projekcie:

- ACK potwierdza **przyjęcie do pierścienia**, nie narysowanie (jak w symulatorze). Brak slotu =
  brak potwierdzenia i `rxDrops`; serwer wtedy ponawia kafle z bieżącego obrazu.
- Każdy `JOB_DGRAM` niesie numer sesji. `drawTask` odrzuca datagramy sesji, która się skończyła,
  bo ich numeracja (np. 5000) przebiłaby numerację nowej sesji (od 1) i wszystkie nowe datagramy
  zostałyby odrzucone jako nieaktualne. `tileSeq` jest zerowany przy pierwszym datagramie nowej sesji.
- Ramka na UDP zamyka się flagą `DG_FLAG_FRAME_END`; jeśli ten datagram zaginie, zapis SPI zamyka
  się sam po 5 ms bezczynności kolejki (inaczej dotyk stałby zablokowany).
- Prostokąt częściowo nieaktualny jest rysowany wierszami (tylko świeże kafle); zdarza się rzadko.
- Nagłówek JPEG składany jest w osobnym buforze `drawTask`, nie w `scratch` netTask (na styku
  transportów netTask może jeszcze odbierać ramkę TCP).
