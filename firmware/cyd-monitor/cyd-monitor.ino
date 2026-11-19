// CYD wireless monitor — device side.
//
// Receives differential frames from server/cyd-monitor over TCP and blits
// them to the panel, reports touch back for panning/zooming the view, and
// hosts its own settings web page for brightness/contrast/scaling/WiFi.
//
// The work is split across both ESP32 cores on purpose. A worst-case frame
// (everything on screen changed) is roughly 40 KB after encoding, which is
// ~27 ms of WiFi at a realistic 12 Mbit/s, while pushing 320x240 over SPI at
// 80 MHz costs 15.4 ms. Done sequentially that is 42 ms — 24 fps, under
// target. Overlapping them hides the SPI time inside the network time and
// lands around 37 fps, which is why netTask and drawTask exist rather than
// one loop doing both.

#include <WiFi.h>
#include <WiFiUdp.h>
#include <WebServer.h>
#include <DNSServer.h>
#include <esp_heap_caps.h>
#include <esp_system.h>
#include <lwip/sockets.h>
#include <JPEGDEC.h>

#include "config.h"
#include "protocol.h"
#include "settings.h"
#include "link.h"

LGFX_CYD  tft;
// drawTask only: decodes ENC_JPEG strips into jpgOut[] (see drawTask).
//
// JPEGDEC rather than LovyanGFX's built-in drawJpg(), which is an older
// TJpgDec that decodes Huffman codes a bit at a time and emits RGB888 for a
// second conversion pass. Measured with a full-screen moving test image that
// arrives entirely as JPEG, that decoder held the panel to 12.8 fps with the
// server waiting on its acks 55-58% of the time. JPEGDEC uses table-driven
// Huffman decoding and writes big-endian RGB565 — the panel's own order —
// straight into the output buffer. Static because its state is ~17 KB.
static JPEGDEC jpegDec;
static int jpegNoDraw(JPEGDRAW*) { return 1; }   // unused: output goes to a framebuffer
Settings  g_set;
LinkStats g_stats;

// Mirrors the server's table; OPT_SRC_RES is an index into this. All 4:3, so
// the downscale to 320x240 never has to letterbox, and all integer multiples
// of 320x240 where possible so the server's box filter lands on exact pixel
// groups instead of interpolating between them.
const SrcRes RES_TABLE[] = {
  {320, 240}, {640, 480}, {800, 600}, {1024, 768}, {1280, 960},
};
const uint8_t RES_TABLE_LEN = sizeof(RES_TABLE) / sizeof(RES_TABLE[0]);

static WiFiClient client;
static QueueHandle_t     freeQ;       // indices of staging buffers not in use
static QueueHandle_t     drawQ;       // strips waiting for the panel
static uint8_t*          pool[CYD_STRIP_BUFFERS];
static uint8_t*          scratch;     // compressed payload, before expansion
// Two DMA-capable buffers JPEG strips are decoded into, used alternately so
// one can be decoded while the other is still being clocked out to the panel.
static uint16_t*         jpgOut[2];
static uint8_t           jpgOutNext = 0;

#define JOB_STRIP     0
#define JOB_FRAME_END 1
#define JOB_JPEG      2
#define JOB_DGRAM     3   // one UDP datagram waiting in dgRing[slot], see the v5 data plane below

struct StripJob {
  uint16_t x, y, w, h;   // JOB_DGRAM: x is the session the datagram belongs to
  uint8_t  kind;
  int8_t   slot;    // index into pool[] (JOB_DGRAM: into dgRing), -1 for frame markers
  uint32_t seq;
  uint16_t off, len;  // JOB_JPEG only: the assembled JPEG within pool[slot]; JOB_DGRAM: datagram length in len
};

// The DQT+DHT segments from the latest MSG_JPEGTABLES. Written and read by
// netTask alone — it rebuilds each strip's header as the strip arrives — so
// a table change can never race a strip that is being assembled.
static uint8_t  g_jpgTables[JPEG_HDR_RESERVE - 2 - 19 - 14];
static uint16_t g_jpgTablesLen = 0;

#if !CYD_NO_UDP
// ---- Protocol v5 data plane (docs/protocol-v5-udp.pl.md) -----------------------
//
// udpRxTask receives datagrams into dgRing, hands each to drawTask as a
// JOB_DGRAM through the same drawQ the TCP strips use (so the two can never be
// drawn out of order), and sends the ACKs. drawTask decodes and pushes. It never
// waits for a free slot: a datagram that finds the ring full is simply dropped,
// unacknowledged, and the server sends those tiles again from its current picture.
static uint8_t*          dgRing;                      // DG_SLOTS x DG_MAX_BYTES, one allocation
static QueueHandle_t     dgFreeQ;                     // indices of free datagram slots
static int               udpSock = -1;
// The seq of the datagram each tile was last drawn from. drawTask only; netTask asks
// for it to be cleared (g_tileSeqReset) when a new UDP session starts.
static uint32_t          tileSeq[TILES_X * TILES_Y];
static volatile bool     g_tileSeqReset = false;
// Socket bound and ring allocated: this panel can offer UDP. Set by udpRxTask.
static volatile bool     g_udpReady = false;
// 0 = pixels arrive over TCP and datagrams are ignored. Written by netTask when it
// reads MSG_TRANSPORT, read by udpRxTask and drawTask.
static volatile uint16_t g_udpSession = 0;
static volatile uint32_t g_udpServerIp = 0;           // network byte order
static volatile uint16_t g_udpServerPort = 0;
// For /api/state and the server's log.
static volatile uint32_t g_rxDgrams = 0, g_rxDrops = 0, g_rxStale = 0, g_rxMaxGapMs = 0;
#endif

// Shows the server's connect-time status line on the connecting splash
// rather than only in the serial log. Written by netTask (MSG_TEXT), drawn by
// drawTask, because the panel is drawTask's alone — see the ownership note
// in link.h. Only acted on before the first real frame: past that point the
// picture owns the screen, and a text message arriving later (there's at
// least one more, after a resolution change) stays serial-only exactly as
// before, rather than punching a splash over live content.
static volatile bool g_showServerText = false;
// Set by the settings page, applied by drawTask — see its idle branch. Not
// applied from the web handler directly: tft.setRotation() touches the same
// SPI/DMA state a strip push does, and drawTask is this panel's only writer
// (see the ownership note in link.h).
static volatile bool g_rotationPending = false;
// Latest wifi_err_reason_t from the STA-disconnected event, forwarded to the
// server via MSG_DIAG on the next reconnect — see sendDiag().
static volatile uint8_t g_lastWifiReason = 0;

// Sentinel MSG_DIAG reason, outside wifi_err_reason_t's real range, meaning
// "no disconnect event fired at all, yet sessions kept dying within seconds"
// (see the note by shortSessionStreak in netTask).
static const uint8_t REASON_SHORT_SESSIONS = 250;
// Same channel, carrying why the previous session ended (a DROP_* code).
static const uint8_t REASON_LINK_DROP = 251;
static char          g_serverTextL1[129] = {0};
static char          g_serverTextL2[129] = {0};

// Set by the settings web server (running on the Arduino loop task) after a
// WiFi/network change that needs a reboot to take effect, acted on by
// drawTask. The restart itself, and the splash announcing it, can't happen
// where the change is saved: that call site is not drawTask, and drawTask is
// the panel's only writer (see the ownership note in link.h) — a queued
// strip mid-DMA-transfer when ESP.restart() cuts power to the bus is exactly
// the kind of thing that rule exists to avoid.
static volatile bool g_restartPending = false;
static char          g_restartMsg[64] = {0};

// The scanner page: a screen of its own, drawn here from MSG_SCAN, that replaces the
// mirrored picture until the user leaves it (see README.md). Flipped by a
// double tap, /api/set or MSG_SCANPOP through g_scanToggle, and only ever by drawTask, which
// owns the panel. While it is open the server holds the stream, and whatever was already
// on its way is thrown away unread.
static volatile bool     g_scanMode = false;
static volatile bool     g_scanToggle = false;
// The page opened by itself for a transmission, so the end of it closes the page again.
static volatile bool     g_scanAuto = false;
static volatile uint8_t  g_scanFlags = 0;
// Written by netTask, read by drawTask; the lock covers one small copy.
static ScanRadio         g_scanIn[SCAN_RADIOS];
static portMUX_TYPE      g_scanMux = portMUX_INITIALIZER_UNLOCKED;
static volatile uint32_t g_scanRxMs = 0;
static volatile uint16_t g_touchX = 0, g_touchY = 0;   // last press, for /api/state
// Where the settings page finds the scanner's own page: the PC this panel is connected to.
static char              g_serverIp[16] = {0};

// ---------------------------------------------------------------- uplink

// Only netTask ever touches the socket. Everyone else — drawTask's per-frame
// ACK, touch events, the settings page on the Arduino loop task — queues a
// message here and netTask writes it between reads (see flushUplink).
//
// It used to be written directly from whichever task had something to say,
// which put drawTask's write() on core 1 against netTask's read() on core 0
// on the same WiFiClient — a class with no locking of its own — and
// NetworkClient::write() answers any send error other than EAGAIN by calling
// stop() on itself. Under full-screen motion the inbound stream holds most
// of lwIP's buffers, a small ACK write fails for want of one, and the socket
// gets closed out from under the reader with unread data still queued: a
// reset, which the server logged as the connection being torn down by this
// side every few seconds for as long as a video played. A low-rate stream
// that barely writes upstream never gets anywhere near that.
//
// Every message is small and disposable (a missed ACK is covered by the
// next one), so a full queue drops rather than blocks.
struct UpMsg { uint8_t len; uint8_t data[12]; };
static QueueHandle_t upQ;

static void txRaw(const uint8_t* p, size_t n) {
  if (!upQ || n > sizeof(UpMsg::data)) return;
  UpMsg m;
  m.len = (uint8_t)n;
  memcpy(m.data, p, n);
  xQueueSend(upQ, &m, 0);
}

// Why the last session ended, reported to the server after the next connect
// (MSG_DIAG with REASON_LINK_DROP) so the cause of a drop lands in its log
// instead of only on a serial port nobody is watching.
enum : uint8_t {
  DROP_NONE = 0, DROP_READ_TIMEOUT, DROP_PEER_CLOSED, DROP_BAD_STRIP,
  DROP_BAD_TYPE, DROP_BAD_TEXT, DROP_WRITE_FAILED, DROP_BAD_TABLES,
};
static volatile uint8_t g_dropReason = DROP_NONE;

// netTask only. Sends everything queued so far; false means the socket
// refused a write and the session is over.
static bool flushUplink() {
  UpMsg m;
  while (xQueueReceive(upQ, &m, 0) == pdTRUE) {
    if (client.write(m.data, m.len) != m.len) {
      g_dropReason = DROP_WRITE_FAILED;
      return false;
    }
  }
  return true;
}

void linkSendOpt(uint8_t key, int16_t value) {
  uint8_t m[4] = { MSG_OPT, key, (uint8_t)(value & 0xFF), (uint8_t)((value >> 8) & 0xFF) };
  txRaw(m, sizeof(m));
}

void linkRequestFullRefresh() { linkSendOpt(OPT_REFRESH, 1); }

static void sendHello() {
  uint8_t m[11];
  size_t i = 0;
  m[i++] = MSG_HELLO;
  m[i++] = PROTO_VERSION;
  m[i++] = CYD_W & 0xFF;  m[i++] = CYD_W >> 8;
  m[i++] = CYD_H & 0xFF;  m[i++] = CYD_H >> 8;
  uint32_t heap = ESP.getFreeHeap();
  m[i++] = heap & 0xFF;         m[i++] = (heap >> 8) & 0xFF;
  m[i++] = (heap >> 16) & 0xFF; m[i++] = (heap >> 24) & 0xFF;
  // Read once per boot, not cached, so it still reflects a crash that
  // happened after the very first connection of this boot — the wire is the
  // only channel a watchdog reset actually reaches, since whatever it
  // interrupted never got to say anything about it.
  m[i++] = (uint8_t)esp_reset_reason();
  // Written directly rather than queued: this runs on netTask, which owns the
  // socket, and HELLO has to beat anything drawTask may already have queued
  // for the connection that just ended.
  client.write(m, i);
  // Replay everything the user has already chosen, so a reconnect or a server
  // restart restores their picture instead of silently reverting to defaults.
  linkSendOpt(OPT_CONTRAST,   g_set.contrast);
  linkSendOpt(OPT_GAMMA,      g_set.gamma);
  linkSendOpt(OPT_SHARPEN,    g_set.sharpen);
  linkSendOpt(OPT_SCALE_MODE, g_set.scaleMode);
  linkSendOpt(OPT_SRC_RES,    g_set.srcRes);
  linkSendOpt(OPT_DITHER,     g_set.dither);
  // A reconnect with the scanner page open: the new session knows nothing of it, and
  // would never close a page the previous one had opened.
  if (g_scanMode && g_scanAuto) g_scanToggle = true;
  else if (g_scanMode) linkSendOpt(OPT_SCANNER, 1);
}

// See the note on MSG_DIAG in protocol.h: reports a WiFi outage the server
// otherwise has no visibility into at all, since a station that's off the
// air can't tell anyone why in real time — this is only ever sent after the
// fact, once reconnected.
static void sendDiag(uint8_t wifiReason, uint32_t downtimeMs) {
  uint8_t m[6];
  size_t i = 0;
  m[i++] = MSG_DIAG;
  m[i++] = wifiReason;
  m[i++] = downtimeMs & 0xFF;         m[i++] = (downtimeMs >> 8) & 0xFF;
  m[i++] = (downtimeMs >> 16) & 0xFF; m[i++] = (downtimeMs >> 24) & 0xFF;
  txRaw(m, i);
}

static void sendAck(uint32_t seq, uint16_t blitMs, uint16_t fps10) {
  uint8_t m[9];
  size_t i = 0;
  m[i++] = MSG_ACK;
  m[i++] = seq & 0xFF;          m[i++] = (seq >> 8) & 0xFF;
  m[i++] = (seq >> 16) & 0xFF;  m[i++] = (seq >> 24) & 0xFF;
  m[i++] = blitMs & 0xFF;       m[i++] = blitMs >> 8;
  m[i++] = fps10 & 0xFF;        m[i++] = fps10 >> 8;
  txRaw(m, i);
}

static void sendTouch(uint8_t phase, uint16_t x, uint16_t y, uint16_t pressure) {
  uint8_t m[8];
  size_t i = 0;
  m[i++] = MSG_TOUCH;
  m[i++] = phase;
  m[i++] = x & 0xFF; m[i++] = x >> 8;
  m[i++] = y & 0xFF; m[i++] = y >> 8;
  m[i++] = pressure & 0xFF; m[i++] = pressure >> 8;
  txRaw(m, i);
}

static void sendScanCmd(uint8_t cmd) {
  const uint8_t m[2] = { MSG_SCANCMD, cmd };
  txRaw(m, sizeof(m));
}

// ---------------------------------------------------------------- decoding

// Expands one strip payload into a DMA staging buffer. Colours are copied as
// opaque 16-bit tokens — see the endianness note in protocol.h — so there is
// no per-pixel arithmetic here, only moves.
static void decodeStrip(uint8_t mode, const uint8_t* src, uint32_t len,
                        uint8_t* dst, uint32_t px) {
  uint16_t* out = (uint16_t*)dst;
  switch (mode) {
    case ENC_SOLID: {
      if (len < 2) break;
      uint16_t c;
      memcpy(&c, src, 2);
      for (uint32_t i = 0; i < px; i++) out[i] = c;
      break;
    }
    case ENC_RLE: {
      uint32_t i = 0, p = 0;
      while (i + 3 <= len && p < px) {
        uint32_t n = src[i];
        uint16_t c;
        memcpy(&c, src + i + 1, 2);
        i += 3;
        if (n > px - p) n = px - p;
        while (n--) out[p++] = c;
      }
      while (p < px) out[p++] = 0;
      break;
    }
    case ENC_PAL1: {
      if (len < 4) break;
      uint16_t pal[2];
      memcpy(pal, src, 4);
      const uint8_t* bits = src + 4;
      for (uint32_t p = 0; p < px; p++)
        out[p] = pal[(bits[p >> 3] >> (7 - (p & 7))) & 1];
      break;
    }
    case ENC_PAL4: {
      if (len < 1) break;
      uint8_t n = src[0];
      if (n == 0 || n > 16 || len < 1u + (uint32_t)n * 2u) break;
      uint16_t pal[16];
      memcpy(pal, src + 1, (size_t)n * 2);
      const uint8_t* nib = src + 1 + n * 2;
      for (uint32_t p = 0; p < px; p++) {
        uint8_t b = nib[p >> 1];
        out[p] = pal[(p & 1) ? (b & 0x0F) : (b >> 4)];
      }
      break;
    }
    default:
      // Unknown encoding: leave the buffer alone rather than blit noise.
      break;
  }
}

// ---------------------------------------------------------------- network task

static bool readExact(uint8_t* dst, size_t n) {
  size_t got = 0;
  uint32_t deadline = millis() + 4000;
  while (got < n) {
    // Every read is also the uplink's chance to go out — see txRaw. Doing it
    // here rather than only between messages keeps ACK latency down while a
    // large frame is still arriving.
    if (!flushUplink()) return false;
    int r = client.read(dst + got, n - got);
    if (r > 0) {
      got += r;
      g_stats.bytesIn += r;
      deadline = millis() + 4000;
      continue;
    }
    if (!client.connected()) { g_dropReason = DROP_PEER_CLOSED; return false; }
    if ((int32_t)(millis() - deadline) > 0) { g_dropReason = DROP_READ_TIMEOUT; return false; }
    vTaskDelay(1);
  }
  return true;
}

static uint16_t rdU16(const uint8_t* p) { return (uint16_t)(p[0] | (p[1] << 8)); }

static uint32_t rdU32(const uint8_t* p) {
  return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
         ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

// Last time a skipped strip asked the server for a repair, so that request
// can be rate limited — see the note at the bottom of handleFrame().
static uint32_t lastRefreshRequest = 0;

static bool handleFrame() {
  uint8_t hdr[10];
  if (!readExact(hdr, sizeof(hdr))) return false;
  uint32_t seq    = rdU32(hdr);
  uint16_t strips = rdU16(hdr + 4);
  // Set when any strip in this frame gets skipped below (buffer exhaustion).
  // The server updates its own idea of "what the panel is showing" the
  // instant it puts a strip on the wire — see the note on prev[] in
  // TileEncoder.EmitStrip — so a strip the device then fails to draw leaves
  // the server believing something the screen does not actually show. That
  // mismatch does not self-correct: the server only re-sends a tile once its
  // *source* content changes again, so a skipped tile can stay visibly wrong
  // indefinitely on an otherwise-static desktop. Asking for one full refresh
  // after such a frame is what turns "wrong forever" into "wrong for at most
  // one extra frame".
  bool skipped = false;

  for (uint16_t s = 0; s < strips; s++) {
    uint8_t sh[STRIP_HDR_BYTES];
    if (!readExact(sh, sizeof(sh))) return false;
    uint16_t x = rdU16(sh), y = rdU16(sh + 2), w = rdU16(sh + 4), h = rdU16(sh + 6);
    uint8_t  mode = sh[8];
    uint32_t len  = rdU32(sh + 9);

    uint32_t px = (uint32_t)w * h;
    if (px == 0 || px > MAX_STRIP_PX || len > MAX_STRIP_BYTES ||
        (mode == ENC_JPEG && len > JPEG_MAX_SCAN) ||
        (uint32_t)x + w > CYD_W || (uint32_t)y + h > CYD_H) {
      // A malformed header means stream sync is lost. There is no safe way to
      // skip forward through an unknown payload length, so drop the link and
      // resynchronise on reconnect.
      g_dropReason = DROP_BAD_STRIP;
      return false;
    }

    // Blocks until the drawing task hands a buffer back. This is the primary
    // flow-control mechanism: normally the panel drains buffers faster than
    // this wait ever matters, and the socket's own receive window is what
    // throttles the server if it ever doesn't.
    //
    // Measured in real use to occasionally run out anyway: a busy real
    // desktop dirties far more of the screen per frame than the calmer
    // content used in development, and drawTask can fall behind for longer
    // than 3 seconds under that load. The first version of this function
    // dropped the whole connection when that happened — reasonable-looking on
    // paper, since a lost buffer looks like the same kind of desync a bad
    // header is — but reconnecting doesn't fix a slow panel, it just repeats
    // the same overload every few seconds forever. The wire format is still
    // perfectly in sync at this point (we know exactly how many bytes this
    // strip's payload is), so those bytes can simply be read and thrown away:
    // one strip's worth of the picture goes stale until the next frame that
    // happens to touch it, instead of the whole link resetting.
    // 300ms rather than the original 3000ms: now that a timeout only costs
    // one stale strip instead of the connection, there is no reason to make
    // the network task sit idle for seconds at a time under sustained
    // overload — better to skip a handful of strips quickly and stay
    // responsive to whatever the panel catches up to next.
    int8_t slot = -1;
    if (xQueueReceive(freeQ, &slot, pdMS_TO_TICKS(300)) != pdTRUE) {
      if (!readExact(scratch, len)) return false;
      skipped = true;
      continue;
    }

    if (mode == ENC_JPEG) {
      // The scan lands after JPEG_HDR_RESERVE bytes of headroom, so the full
      // header can be written in front of it and drawTask handed one
      // contiguous JPEG. Decoding stays off this task on purpose: tried here,
      // netTask could no longer receive while it decoded or decode while it
      // waited on the network, and full-screen video fell from ~20 fps to
      // ~11 with the server stalled on the panel's acks two thirds of the
      // time. Receiving on core 0 while core 1 decodes is the overlap that
      // matters.
      uint8_t* b = pool[slot];
      if (!readExact(b + JPEG_HDR_RESERVE, len)) {
        xQueueSend(freeQ, &slot, 0);
        return false;
      }
      if (g_jpgTablesLen == 0) {
        // The server sends tables before its first JPEG strip, so this is
        // not expected — but a strip that can't be decoded is still better
        // skipped (and repaired by the refresh that follows) than drawn.
        xQueueSend(freeQ, &slot, 0);
        skipped = true;
        continue;
      }
      uint16_t hdrLen = 2 + g_jpgTablesLen + 19 + 14;
      uint8_t* hp = b + JPEG_HDR_RESERVE - hdrLen;
      *hp++ = 0xFF; *hp++ = 0xD8;                                   // SOI
      memcpy(hp, g_jpgTables, g_jpgTablesLen); hp += g_jpgTablesLen;  // DQT, DHT
      const uint8_t sof[19] = {                                     // SOF0, 4:2:0
        0xFF, 0xC0, 0x00, 17, 8, (uint8_t)(h >> 8), (uint8_t)h, (uint8_t)(w >> 8), (uint8_t)w,
        3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 };
      memcpy(hp, sof, sizeof(sof)); hp += sizeof(sof);
      const uint8_t sos[14] = {                                     // SOS
        0xFF, 0xDA, 0x00, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0 };
      memcpy(hp, sos, sizeof(sos));
      b[JPEG_HDR_RESERVE + len]     = 0xFF;                         // EOI
      b[JPEG_HDR_RESERVE + len + 1] = 0xD9;

      StripJob job = { x, y, w, h, JOB_JPEG, slot, seq,
                       (uint16_t)(JPEG_HDR_RESERVE - hdrLen), (uint16_t)(hdrLen + len + 2) };
      xQueueSend(drawQ, &job, portMAX_DELAY);
      continue;
    }

    if (mode == ENC_RAW) {
      if (len != px * 2 || !readExact(pool[slot], len)) {
        xQueueSend(freeQ, &slot, 0);
        return false;
      }
    } else {
      if (!readExact(scratch, len)) {
        xQueueSend(freeQ, &slot, 0);
        return false;
      }
      decodeStrip(mode, scratch, len, pool[slot], px);
    }

    StripJob job = { x, y, w, h, JOB_STRIP, slot, seq };
    xQueueSend(drawQ, &job, portMAX_DELAY);
  }

  StripJob end = { 0, 0, 0, 0, JOB_FRAME_END, -1, seq };
  xQueueSend(drawQ, &end, portMAX_DELAY);

  // Asking for a full refresh after a skipped strip repairs the picture (see
  // the note on skipped above), but a full refresh is the *largest* frame
  // there is — and strips get skipped precisely when the link or the panel
  // is already behind. Unthrottled, that is a positive feedback loop:
  // congestion causes a skip, the skip demands a full screen, the full
  // screen deepens the congestion. Measured as exactly that: sessions dying
  // in seconds with the server logging a reset from this side, faster and
  // faster until the WiFi stack itself wedged. One repair request every few
  // seconds still fixes a stale tile quickly in human terms, while leaving
  // the link room to recover in between.
  if (skipped) {
    uint32_t now = millis();
    if (now - lastRefreshRequest > 3000) {
      lastRefreshRequest = now;
      linkRequestFullRefresh();
    }
  }
  return true;
}

static bool handleServerMessage() {
  uint8_t type;
  if (!readExact(&type, 1)) return false;
  switch (type) {
    case MSG_FRAME:
      return handleFrame();

    case MSG_CONFIG: {
      uint8_t b[7];
      if (!readExact(b, sizeof(b))) return false;
#if !CYD_NO_UDP
      // A v5 server sets flags bit 0 (b[6]); a v4 server leaves it 0, and then nothing
      // new is ever sent to it.
      if ((b[6] & CONFIG_FLAG_UDP) && g_udpReady) {
        const uint8_t m[3] = { MSG_UDPHELLO, UDP_DEVICE_PORT & 0xFF, UDP_DEVICE_PORT >> 8 };
        txRaw(m, sizeof(m));
      }
#endif
      return true;
    }

    case MSG_TRANSPORT: {
      uint8_t b[5];
      if (!readExact(b, sizeof(b))) return false;
#if !CYD_NO_UDP
      if (b[0] == 1 && g_udpReady) {
        g_udpServerIp   = (uint32_t)client.remoteIP();
        g_udpServerPort = rdU16(b + 3);
        g_rxMaxGapMs    = 0;
        // Cleared before the session id is published: drawTask clears tileSeq when the
        // first datagram of the new session reaches it, and must find this flag set.
        g_tileSeqReset  = true;
        __sync_synchronize();
        g_udpSession    = rdU16(b + 1);
        Serial.printf("[udp] pixels over UDP, session %u\n", (unsigned)g_udpSession);
      } else {
        g_udpSession = 0;
        Serial.println("[udp] pixels over TCP");
      }
#endif
      return true;
    }

    case MSG_OPTACK: {
      uint8_t b[3];
      if (!readExact(b, sizeof(b))) return false;
      int16_t v = (int16_t)rdU16(b + 1);
      // The server reports the value it actually applied, which is not always
      // what was asked for — a resolution the driver refuses, a contrast
      // clamped to range. Storing the confirmed value keeps the settings
      // page from displaying a change that never took effect.
      switch (b[0]) {
        case OPT_CONTRAST:   g_set.contrast  = v;           break;
        case OPT_GAMMA:      g_set.gamma     = v;           break;
        case OPT_SHARPEN:    g_set.sharpen   = (uint8_t)v;  break;
        case OPT_SCALE_MODE: g_set.scaleMode = (uint8_t)v;  break;
        case OPT_SRC_RES:    g_set.srcRes    = (uint8_t)v;  break;
        case OPT_DITHER:     g_set.dither    = (uint8_t)v;  break;
      }
      return true;
    }

    case MSG_JPEGTABLES: {
      uint8_t b[2];
      if (!readExact(b, sizeof(b))) return false;
      uint16_t n = rdU16(b);
      if (n > sizeof(g_jpgTables)) { g_dropReason = DROP_BAD_TABLES; return false; }
      if (!readExact(g_jpgTables, n)) return false;
      g_jpgTablesLen = n;
      return true;
    }

    case MSG_SCAN: {
      uint8_t b[1 + SCAN_RADIOS * SCAN_REC_BYTES];
      if (!readExact(b, sizeof(b))) return false;
      ScanRadio in[SCAN_RADIOS];
      for (int i = 0; i < SCAN_RADIOS; i++) {
        const uint8_t* r = b + 1 + i * SCAN_REC_BYTES;
        in[i].state = r[0];
        in[i].dbm   = (int16_t)rdU16(r + 1);
        memcpy(in[i].name, r + 3, SCAN_NAME_BYTES);
        in[i].name[SCAN_NAME_BYTES] = 0;
        memcpy(in[i].freq, r + 3 + SCAN_NAME_BYTES, SCAN_FREQ_BYTES);
        in[i].freq[SCAN_FREQ_BYTES] = 0;
      }
      portENTER_CRITICAL(&g_scanMux);
      memcpy(g_scanIn, in, sizeof(in));
      portEXIT_CRITICAL(&g_scanMux);
      g_scanFlags = b[0];
      g_scanRxMs = millis();
      return true;
    }

    case MSG_SCANPOP: {
      uint8_t open;
      if (!readExact(&open, 1)) return false;
      if (open && !g_scanMode) {
        g_scanAuto = true;
        g_scanToggle = true;
      } else if (!open && g_scanMode && g_scanAuto) {
        g_scanToggle = true;
      }
      return true;
    }

    case MSG_TEXT: {
      uint8_t b[2];
      if (!readExact(b, sizeof(b))) return false;
      uint16_t n = rdU16(b);
      if (n > 256) { g_dropReason = DROP_BAD_TEXT; return false; }
      static char msg[257];
      if (!readExact((uint8_t*)msg, n)) return false;
      msg[n] = 0;
      Serial.printf("[server] %s\n", msg);
      if (g_stats.framesDrawn == 0) {
        // '|' splits into the two lines DeviceSession.RunAsync composed —
        // see the note there. No '|' just means the whole thing on line 1.
        char* bar = strchr(msg, '|');
        if (bar) {
          *bar = 0;
          snprintf(g_serverTextL1, sizeof(g_serverTextL1), "%s", msg);
          snprintf(g_serverTextL2, sizeof(g_serverTextL2), "%s", bar + 1);
        } else {
          snprintf(g_serverTextL1, sizeof(g_serverTextL1), "%s", msg);
          g_serverTextL2[0] = 0;
        }
        g_showServerText = true;
      }
      return true;
    }

    default:
      g_dropReason = DROP_BAD_TYPE;
      return false;  // desynchronised — reconnect
  }
}

// Finds the server by shouting on the LAN broadcast address rather than using
// mDNS. Advertising a service from Windows needs Bonjour installed, and the
// host's DHCP lease moves around; a short broadcast the server answers
// directly has no dependencies on either side.
static bool discoverServer(IPAddress& out) {
  WiFiUDP udp;
  if (!udp.begin(0)) return false;
  const char* probe = "CYDMON?";
  for (int attempt = 0; attempt < 6; attempt++) {
    udp.beginPacket(IPAddress(255, 255, 255, 255), PROTO_PORT - 1);
    udp.write((const uint8_t*)probe, strlen(probe));
    udp.endPacket();
    uint32_t until = millis() + 500;
    while ((int32_t)(millis() - until) < 0) {
      if (udp.parsePacket() > 0) {
        char buf[32] = {0};
        int n = udp.read(buf, sizeof(buf) - 1);
        if (n >= 7 && strncmp(buf, "CYDMON!", 7) == 0) {
          out = udp.remoteIP();
          udp.stop();
          return true;
        }
      }
      vTaskDelay(pdMS_TO_TICKS(10));
    }
  }
  udp.stop();
  return false;
}

static void netTask(void*) {
  // How long the station has been out of WL_CONNECTED, and when it was last
  // kicked with an explicit reconnect() — see the note by wifiDownSince
  // below on why this exists instead of just waiting on the core's own
  // auto-reconnect.
  uint32_t wifiDownSince = 0;
  uint32_t lastKick = 0;
  // Set when WiFi comes back after measurable downtime; consumed by sending
  // one MSG_DIAG right after the next successful HELLO, which is how that
  // downtime (and the reason g_lastWifiReason recorded for it) reaches the
  // server's own log instead of only a serial monitor nobody has plugged in.
  uint32_t pendingDiagMs = 0;

  // Purely observational: sessions dying within seconds of connecting, over
  // and over, while WiFi.status() still reports WL_CONNECTED, is the
  // signature of the feedback loop handleFrame() used to drive (a skipped
  // strip demanding a full-screen repaint on an already-congested link).
  // Reported rather than acted on — an earlier version forced a WiFi
  // disassociate+rejoin here, on the theory that the access point was at
  // fault, which measurement did not support: the same network carries
  // other ESP32s without trouble, and they differ only in never sustaining
  // this kind of throughput. If this still fires, the loop is not the whole
  // story and the log will say so.
  IPAddress lastGoodHost;
  bool      haveLastGoodHost = false;
  uint32_t sessionStartMs = 0;
  uint8_t  lastDropReason = DROP_NONE;   // sent as MSG_DIAG after the next connect
  int      shortSessionStreak = 0;
  const uint32_t ShortSessionMs = 8000;
  const int ReportStreak = 3;
  bool     pendingRejoinDiag = false;

  for (;;) {
    if (WiFi.status() != WL_CONNECTED) {
      if (g_stats.connected) {
        Serial.println("WiFi stracone");
      }
      g_stats.connected = false;
      uint32_t now = millis();
      if (wifiDownSince == 0) wifiDownSince = now;
      // WiFi.begin() enables the core's own auto-reconnect, but it has been
      // observed (see the disconnect-reason log from the WiFiEvent handler
      // in setup()) to sometimes sit disassociated for a long time — many
      // minutes — before that kicks back in on its own, which is what turns
      // one dropped beacon into an extended black screen. Nudging it
      // ourselves every few seconds costs nothing while it's already
      // reconnecting (WiFi.reconnect() on an already-connecting station is
      // a no-op) and bounds the worst case to that same few seconds instead
      // of however long the core feels like waiting.
      if (now - lastKick > 4000) {
        lastKick = now;
        Serial.printf("WiFi polaczenie utracone od %lu ms, wznawiam\n", now - wifiDownSince);
        WiFi.reconnect();
      }
      vTaskDelay(pdMS_TO_TICKS(500));
      continue;
    }
    if (wifiDownSince != 0) {
      pendingDiagMs = millis() - wifiDownSince;
      Serial.printf("WiFi odzyskane po %lu ms\n", pendingDiagMs);
      wifiDownSince = 0;
    }

    IPAddress host;
    if (strlen(g_set.host) > 0) {
      if (!host.fromString(g_set.host)) { vTaskDelay(pdMS_TO_TICKS(2000)); continue; }
    } else if (haveLastGoodHost) {
      // Straight back to whoever answered last time. Discovery costs up to
      // six broadcast rounds of 500 ms before it even reports failure, and
      // it was being paid on every single reconnect — measured as a 23
      // second gap between a dropped link and the next session, with the
      // panel holding a stale frame the whole time. The server's address
      // almost never changes between two reconnects seconds apart, so
      // guessing it and falling back on failure is strictly faster.
      host = lastGoodHost;
    } else if (!discoverServer(host)) {
      vTaskDelay(pdMS_TO_TICKS(1000));
      continue;
    }

    Serial.printf("connecting to %s:%d\n", host.toString().c_str(), PROTO_PORT);
    if (!client.connect(host, PROTO_PORT, 3000)) {
      // The remembered address is only ever a guess; one failure is enough
      // to stop trusting it and go back to asking the network.
      haveLastGoodHost = false;
      vTaskDelay(pdMS_TO_TICKS(1000));
      continue;
    }
    lastGoodHost = host;
    haveLastGoodHost = true;
    snprintf(g_serverIp, sizeof(g_serverIp), "%s", host.toString().c_str());
    // Frames are already coalesced into one write per frame on the server, so
    // Nagle can only ever add latency here.
    client.setNoDelay(true);
    // Whatever was queued for the previous session — ACKs for frames that
    // connection carried, a touch in flight — means nothing to this one, and
    // HELLO has to be the first thing the server reads.
    xQueueReset(upQ);
    g_stats.connected = true;
    sessionStartMs = millis();
    sendHello();
    if (lastDropReason != DROP_NONE) {
      sendDiag(REASON_LINK_DROP, lastDropReason);
      lastDropReason = DROP_NONE;
    }
    if (pendingDiagMs > 0) {
      sendDiag(g_lastWifiReason, pendingDiagMs);
      pendingDiagMs = 0;
    }
    if (pendingRejoinDiag) {
      sendDiag(REASON_SHORT_SESSIONS, ReportStreak);
      pendingRejoinDiag = false;
    }
    linkRequestFullRefresh();

    g_dropReason = DROP_NONE;
    while (client.connected()) {
      if (!handleServerMessage()) break;
    }
    // The loop can also end on connected() itself going false between two
    // messages, which is the peer closing just as much as a failed read is.
    lastDropReason = g_dropReason != DROP_NONE ? g_dropReason : DROP_PEER_CLOSED;
    client.stop();
    g_stats.connected = false;
#if !CYD_NO_UDP
    g_udpSession = 0;   // the datagrams of this session mean nothing to the next one
#endif
    Serial.printf("link dropped, reason %u\n", lastDropReason);

    uint32_t sessionMs = millis() - sessionStartMs;
    if (sessionMs < ShortSessionMs) {
      if (++shortSessionStreak >= ReportStreak) {
        Serial.printf("%d polaczen pod rzad krotszych niz %lu ms mimo WiFi.status()==CONNECTED\n",
                      shortSessionStreak, ShortSessionMs);
        shortSessionStreak = 0;
        pendingRejoinDiag = true;
      }
    } else {
      shortSessionStreak = 0;
    }
  }
}

// ---------------------------------------------------------------- UDP receive task

#if !CYD_NO_UDP
static void wrU16(uint8_t* p, uint16_t v) { p[0] = v & 0xFF; p[1] = v >> 8; }
static void wrU32(uint8_t* p, uint32_t v) {
  p[0] = v & 0xFF; p[1] = (v >> 8) & 0xFF; p[2] = (v >> 16) & 0xFF; p[3] = (v >> 24) & 0xFF;
}

// Runs from the same socket DATA arrives on, so that a firewall between the two
// sees this as the reply to the server's datagrams.
static void udpSendAck(uint16_t session, uint32_t highest, uint32_t bitmap, uint8_t drops) {
  uint8_t a[DG_ACK_BYTES];
  a[0] = DG_ACK;
  a[1] = 0;
  wrU16(a + 2, session);
  wrU32(a + 4, highest);
  wrU32(a + 8, bitmap);
  wrU16(a + 12, g_stats.fps10);
  wrU16(a + 14, g_stats.blitMs);
  a[16] = (uint8_t)uxQueueMessagesWaiting(dgFreeQ);
  a[17] = drops;
  sockaddr_in to = {};
  to.sin_family      = AF_INET;
  to.sin_port        = htons(g_udpServerPort);
  to.sin_addr.s_addr = g_udpServerIp;
  sendto(udpSock, a, sizeof(a), 0, (sockaddr*)&to, sizeof(to));
}

// Receives datagrams and acknowledges them; never waits on anything but the socket.
//
// "Acknowledged" means the datagram is in the ring and will be drawn (or is
// known stale by then). A datagram that finds no free slot is *not* acknowledged
// - the server then treats it as lost and re-sends its tiles - which is also the
// back-pressure: the server sees freeSlots fall and slows down before that happens.
static void udpRxTask(void*) {
  static uint8_t rx[1500];

  udpSock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
  bool ok = udpSock >= 0;
  if (ok) {
    sockaddr_in me = {};
    me.sin_family      = AF_INET;
    me.sin_port        = htons(UDP_DEVICE_PORT);
    me.sin_addr.s_addr = htonl(INADDR_ANY);
    // Woken by a datagram at once, or after 10 ms to run the ACK timer.
    timeval tv = { 0, 10000 };
    ok = bind(udpSock, (sockaddr*)&me, sizeof(me)) == 0 &&
         setsockopt(udpSock, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv)) == 0;
  }
  if (!ok) {
    Serial.println("[udp] nie mozna otworzyc gniazda - zostaje TCP");
    if (udpSock >= 0) lwip_close(udpSock);
    vTaskDelete(nullptr);
    return;
  }
  g_udpReady = true;

  uint16_t session = 0;
  bool     any = false, gap = false;
  uint32_t highest = 0, bitmap = 0, lastAck = 0, lastRx = 0;
  uint8_t  pending = 0, drops = 0;

  for (;;) {
    sockaddr_in from;
    socklen_t   fromLen = sizeof(from);
    int n = recvfrom(udpSock, rx, sizeof(rx), 0, (sockaddr*)&from, &fromLen);
    uint32_t now = millis();

    uint16_t cur = g_udpSession;
    if (cur != session) {            // a new session, or none: the old numbering means nothing
      session = cur;
      any = gap = false;
      highest = bitmap = lastRx = 0;
      pending = drops = 0;
      // Opening acknowledgement (highest 0: nothing received yet). It tells the server this side
      // is listening now - the server holds its first datagram for it, because whatever arrives
      // before MSG_TRANSPORT has been read is ignored.
      if (session != 0) udpSendAck(session, 0, 0, 0);
    }

    if (session != 0 && n >= DG_HDR_BYTES && rx[0] == DG_DATA &&
        rdU16(rx + 2) == session && from.sin_addr.s_addr == g_udpServerIp) {
      uint32_t seq = rdU32(rx + 4);
      g_rxDgrams = g_rxDgrams + 1;   // single writer, so a plain read-then-store
      g_stats.bytesIn += n;
      if (lastRx != 0) {
        uint32_t d = now - lastRx;
        if (d > g_rxMaxGapMs) g_rxMaxGapMs = d;
      }
      lastRx = now;

      bool dup = any && (seq == highest ||
                         ((int32_t)(highest - seq) > 0 && highest - seq - 1 < 32 &&
                          ((bitmap >> (highest - seq - 1)) & 1)));
      bool take = !dup;

      if (take && rx[8] != 0) {
        int8_t slot = -1;
        if (xQueueReceive(dgFreeQ, &slot, 0) != pdTRUE) {
          take = false;
        } else {
          memcpy(dgRing + (size_t)slot * DG_MAX_BYTES, rx, n);
          StripJob job = { session, 0, 0, 0, JOB_DGRAM, slot, seq, 0, (uint16_t)n };
          if (xQueueSend(drawQ, &job, 0) != pdTRUE) {
            xQueueSend(dgFreeQ, &slot, 0);
            take = false;
          }
        }
        if (!take) {
          g_rxDrops = g_rxDrops + 1;
          if (drops < 255) drops++;
        }
      }

      if (take) {
        if (!any) {
          highest = seq; bitmap = 0; any = true;
        } else if ((int32_t)(seq - highest) > 0) {
          uint32_t shift = seq - highest;
          if (shift > 1) gap = true;
          bitmap = shift > 32 ? 0 : ((shift == 32 ? 0 : bitmap << shift) | (1u << (shift - 1)));
          highest = seq;
        } else {
          uint32_t pos = highest - seq - 1;
          if (pos < 32) bitmap |= 1u << pos;
        }
        pending++;
      }
    }

    // A hole in the numbering is answered at once, so the server learns of a loss
    // in a round trip instead of after its timer; otherwise every 4th datagram or
    // 16 ms, whichever comes first.
    if (any && (gap || pending >= 4 || ((pending > 0 || drops > 0) && now - lastAck >= 16))) {
      udpSendAck(session, highest, bitmap, drops);
      pending = 0; gap = false; drops = 0; lastAck = now;
    }
  }
}
#endif

// ---------------------------------------------------------------- scanner page

// The whole glass, edge to edge: one block per radio, name above frequency and
// level, over one bar of buttons that act on both radios. The mirror's stats line
// stays off this page. Everything here runs on drawTask.
#define SCAN_BLOCK_H   96
#define SCAN_BAR_Y     192
#define SCAN_BTN_H     (CYD_H - SCAN_BAR_Y)
#define SCAN_BTN_W     79
#define SCAN_BTN_STEP  80
#define SCAN_MARGIN    2
#define SCAN_BTN_COUNT 4
#define SCAN_BTN_MUTE  2       // its place in the bar
#define SCAN_BTN_GREY  0x6B4D
#define SCAN_DIM_GREY  0x8410  // status text, while nobody is on the air
#define SCAN_NAME_H    64      // the name's row, within a block
#define SCAN_LINE_Y    79      // the centre of the row below it
#define SCAN_TRACK_Y   88      // the sweep bar, along the bottom of the block
#define SCAN_TRACK_H   6
#define SCAN_OFF_RED   0x7000  // block background: this radio's sound is off
#define SCAN_OFF_FREQ  0xFC10  // plain red would vanish on that background
#define SCAN_OFF_GREY  0xC618  // and so would the status grey
#define SCAN_SLIDER_W  60
#define SCAN_SLIDER_STEP 4
#define SCAN_STALE_MS  3000    // the server sends once a second at the very least

static const char* const SCAN_BTN_TEXT[SCAN_BTN_COUNT] = { "START", "STOP", "MUTE", "NEXT" };
static const uint8_t SCAN_BTN_CMD[SCAN_BTN_COUNT] = { SCAN_CMD_START, SCAN_CMD_STOP, SCAN_CMD_MUTE, SCAN_CMD_NEXT };

// A six-letter callsign already fills the width at 72 px, so a larger name can only
// grow upwards: every name is stretched to the same height and fitted to the width,
// which is why a short one gets wider and a long one narrower. Past the narrow limit
// the strokes get too thin, and status text falls back to a smaller font.
static const struct { const lgfx::IFont* font; uint8_t capH; float tall; } SCAN_NAME_FONTS[] = {
  { &fonts::DejaVu72, 51, 1.2f }, { &fonts::FreeSansBold18pt7b, 25, 1.0f },
  { &fonts::FreeSansBold12pt7b, 17, 1.0f }, { &fonts::FreeSansBold9pt7b, 13, 1.0f },
};
#define SCAN_NAME_WIDEST  1.25f
#define SCAN_NAME_NARROWEST 0.6f

static ScanRadio g_scanShown[SCAN_RADIOS];
static bool      g_scanSoundOffShown[SCAN_RADIOS];
static bool      g_scanRepaint = false;
static bool      g_scanMutedShown = false;
static int8_t    g_scanBtn = -1;   // the button under the finger
// The sweep bars share one position, so the two always move as one.
static int16_t   g_scanSlider = 0;       // left edge of the bars
static int8_t    g_scanSliderDir = 1;

static int16_t scanSliderNext() {
  int16_t next = g_scanSlider + g_scanSliderDir * SCAN_SLIDER_STEP;
  if (next < 0 || next > CYD_W - SCAN_SLIDER_W) {
    g_scanSliderDir = -g_scanSliderDir;
    next = g_scanSlider + g_scanSliderDir * SCAN_SLIDER_STEP;
  }
  return next;
}

// One step of a bar: only the strips it leaves and enters change, so it cannot flicker.
static void scanSlide(int16_t y0, int16_t x, int16_t next, bool whole, uint16_t bg) {
  int16_t y = y0 + SCAN_TRACK_Y;
  if (whole) {
    tft.fillRect(0, y, CYD_W, SCAN_TRACK_H, bg);
    tft.fillRect(next, y, SCAN_SLIDER_W, SCAN_TRACK_H, TFT_CYAN);
  } else if (next > x) {
    tft.fillRect(x, y, SCAN_SLIDER_STEP, SCAN_TRACK_H, bg);
    tft.fillRect(x + SCAN_SLIDER_W, y, SCAN_SLIDER_STEP, SCAN_TRACK_H, TFT_CYAN);
  } else {
    tft.fillRect(next + SCAN_SLIDER_W, y, SCAN_SLIDER_STEP, SCAN_TRACK_H, bg);
    tft.fillRect(next, y, SCAN_SLIDER_STEP, SCAN_TRACK_H, TFT_CYAN);
  }
}

static void scanDrawButton(int b, bool pressed) {
  int16_t x = b * SCAN_BTN_STEP + (b == SCAN_BTN_COUNT - 1 ? 1 : 0);
  bool muted = b == SCAN_BTN_MUTE && g_scanMutedShown;
  uint16_t bg = pressed ? (uint16_t)TFT_LIGHTGREY : muted ? (uint16_t)SCAN_OFF_RED : (uint16_t)SCAN_BTN_GREY;
  tft.fillRoundRect(x, SCAN_BAR_Y, SCAN_BTN_W, SCAN_BTN_H, 6, bg);
  tft.setFont(&fonts::FreeSansBold9pt7b);
  tft.setTextPadding(0);
  tft.setTextDatum(textdatum_t::middle_center);
  tft.setTextColor(pressed ? (uint16_t)TFT_BLACK : (uint16_t)TFT_WHITE, bg);
  tft.drawString(SCAN_BTN_TEXT[b], x + SCAN_BTN_W / 2, SCAN_BAR_Y + SCAN_BTN_H / 2);
}

static void scanDrawRadio(int i, const ScanRadio& r, bool soundOff, int16_t barNext) {
  ScanRadio& s = g_scanShown[i];
  int16_t y0 = i * SCAN_BLOCK_H;
  bool hold = r.state == SCAN_HOLD;
  uint16_t bg  = soundOff ? (uint16_t)SCAN_OFF_RED : (uint16_t)TFT_BLACK;
  uint16_t dim = soundOff ? (uint16_t)SCAN_OFF_GREY : (uint16_t)SCAN_DIM_GREY;
  bool recolour = g_scanRepaint || soundOff != g_scanSoundOffShown[i];
  bool all = recolour || s.state != r.state;

  // Round letters overshoot the capital height, and more so stretched, and text has no
  // background to wipe it later: anything reaching the line between the blocks stays there.
  tft.setClipRect(0, y0, CYD_W, SCAN_BLOCK_H - 1);

  // The one mark of a silenced radio, so it has to be seen from across the room: the
  // whole block changes colour. The last row is the line between the two blocks.
  if (recolour) {
    g_scanSoundOffShown[i] = soundOff;
    tft.fillRect(0, y0, CYD_W, SCAN_BLOCK_H - 1, bg);
  }

  // The bar has its own row, which nothing else clears.
  if (s.state == SCAN_RUNNING && r.state != SCAN_RUNNING)
    tft.fillRect(0, y0 + SCAN_TRACK_Y, CYD_W, SCAN_TRACK_H, bg);

  tft.setFont(&fonts::FreeSansBold12pt7b);
  if (all || strcmp(s.freq, r.freq) != 0) {
    tft.fillRect(0, y0 + SCAN_NAME_H, 206, 31, bg);
    tft.setTextPadding(0);
    tft.setTextDatum(textdatum_t::middle_left);
    tft.setTextColor(!hold ? dim : soundOff ? (uint16_t)SCAN_OFF_FREQ : (uint16_t)TFT_RED, bg);
    tft.drawString(r.freq, SCAN_MARGIN, y0 + SCAN_LINE_Y);
  }
  if (all || s.dbm != r.dbm) {
    // Moves several times a second, so painted over rather than cleared first.
    char txt[12] = "";
    if (hold) snprintf(txt, sizeof(txt), "%d dBm", (int)r.dbm);
    tft.setTextDatum(textdatum_t::middle_right);
    tft.setTextColor(TFT_GREEN, bg);
    tft.setTextPadding(110);
    tft.drawString(txt, CYD_W - SCAN_MARGIN, y0 + SCAN_LINE_Y);
    tft.setTextPadding(0);
  }
  // Last, because J and Q hang below the line and the clearing above would cut them off.
  if (all || strcmp(s.name, r.name) != 0) {
    // With nothing on the row below, a status keeps the full size and hangs into it. A
    // channel shares that row with its frequency, so a name that hangs stays natural size.
    bool hangs = hold && strpbrk(r.name, "JQ") != nullptr;
    tft.fillRect(0, y0, CYD_W, hold ? SCAN_NAME_H : SCAN_TRACK_Y, bg);
    tft.setTextPadding(0);
    tft.setTextDatum(textdatum_t::baseline_center);
    // No background: a font this tall has a cell that reaches into the row below.
    tft.setTextColor(hold ? (uint16_t)TFT_WHITE : dim);
    float wide = 1, tall = 1;
    int capH = 0;
    for (const auto& f : SCAN_NAME_FONTS) {
      tft.setFont(f.font);
      tft.setTextSize(1);
      wide = (float)(CYD_W - 2 * SCAN_MARGIN) / tft.textWidth(r.name);
      tall = hangs ? 1.0f : f.tall;
      capH = f.capH * tall;
      if (wide >= SCAN_NAME_NARROWEST) break;
    }
    tft.setTextSize(wide < SCAN_NAME_WIDEST ? wide : SCAN_NAME_WIDEST, tall);
    tft.drawString(r.name, CYD_W / 2, y0 + 1 + (hangs ? capH : (SCAN_NAME_H + capH) / 2));
    tft.setTextSize(1);
  }
  if (r.state == SCAN_RUNNING) scanSlide(y0, g_scanSlider, barNext, all, bg);
  tft.clearClipRect();
  s = r;
}

static void scanDrawUpdate() {
  ScanRadio cur[SCAN_RADIOS];
  portENTER_CRITICAL(&g_scanMux);
  memcpy(cur, g_scanIn, sizeof(cur));
  portEXIT_CRITICAL(&g_scanMux);

  if (millis() - g_scanRxMs > SCAN_STALE_MS) {
    // A page that opened by itself is not worth keeping when nothing will close it.
    if (g_scanAuto) g_scanToggle = true;
    for (int i = 0; i < SCAN_RADIOS; i++) {
      cur[i].state = SCAN_OFFLINE;
      snprintf(cur[i].name, sizeof(cur[i].name), "%s", g_stats.connected ? "CZEKAM NA DANE" : "BRAK SERWERA");
      cur[i].freq[0] = 0;
    }
  }
  int16_t barNext = scanSliderNext();
  uint8_t flags = g_scanFlags;
  for (int i = 0; i < SCAN_RADIOS; i++)
    scanDrawRadio(i, cur[i], (flags & (SCAN_FLAG_RADIO0_OFF << i)) != 0, barNext);
  g_scanSlider = barNext;

  bool muted = (flags & SCAN_FLAG_MUTED) != 0;
  if (muted != g_scanMutedShown) {
    g_scanMutedShown = muted;
    if (g_scanBtn != SCAN_BTN_MUTE) scanDrawButton(SCAN_BTN_MUTE, false);
  }
  g_scanRepaint = false;
}

static void scanPaintAll() {
  tft.fillScreen(TFT_BLACK);
  tft.drawFastHLine(0, SCAN_BLOCK_H - 1, CYD_W, SCAN_BTN_GREY);
  tft.drawFastHLine(0, 2 * SCAN_BLOCK_H - 1, CYD_W, SCAN_BTN_GREY);
  g_scanMutedShown = (g_scanFlags & SCAN_FLAG_MUTED) != 0;
  for (int b = 0; b < SCAN_BTN_COUNT; b++) scanDrawButton(b, false);
  g_scanRepaint = true;
  scanDrawUpdate();
}

static void scanEnter() {
  g_scanMode = true;
  g_scanRxMs = millis();     // a moment's grace for the first MSG_SCAN
  linkSendOpt(OPT_SCANNER, 1);
  scanPaintAll();
}

static void scanLeave() {
  g_scanMode = false;
  g_scanAuto = false;
  g_scanBtn = -1;
  tft.setFont(&fonts::Font0);
  tft.setTextPadding(0);
  tft.setTextSize(1);
  tft.fillScreen(TFT_BLACK);
  // OPT_SCANNER 0 is also the server's cue to repaint everything.
  linkSendOpt(OPT_SCANNER, 0);
}

// The first panel's touch reports Y upside down: measured, the bottom bar reads about 38.
// Not every unit does, so it is a setting (on by default, as that panel needs it).
// The mirror only ever uses the movement of a finger, so nothing else has to know.
static uint16_t scanFlipY(uint16_t rawY) {
  if (!g_set.scanFlip) return rawY < CYD_H ? rawY : CYD_H - 1;
  return rawY < CYD_H ? CYD_H - 1 - rawY : 0;
}

// A single tap on a radio's block turns that radio's sound on or off.
static void scanBlockTap(uint16_t rawY) {
  int radio = scanFlipY(rawY) / SCAN_BLOCK_H;
  if (radio < SCAN_RADIOS) sendScanCmd(SCAN_CMD_SOUND + radio);
}

// A press on a button lights it; the release sends it. Returns whether it was on one.
static bool scanTouchDown(uint16_t x, uint16_t rawY) {
  uint16_t y = scanFlipY(rawY);
  if (y < SCAN_BAR_Y - 16) return false;
  int b = x / SCAN_BTN_STEP;
  if (b >= SCAN_BTN_COUNT) b = SCAN_BTN_COUNT - 1;
  g_scanBtn = b;
  scanDrawButton(b, true);
  return true;
}

static void scanTouchUp() {
  int b = g_scanBtn;
  g_scanBtn = -1;
  scanDrawButton(b, false);
  sendScanCmd(SCAN_BTN_CMD[b]);
}

// ---------------------------------------------------------------- touch

#define TOUCH_TAP_SLOP_PX 24
#define TOUCH_TAP_MAX_MS  400   // longer than this is a press, not a tap
#define TOUCH_PAIR_MIN_MS 80    // a shorter gap is the resistive panel losing contact
#define TOUCH_PAIR_MAX_MS 400

static bool     tDown = false;
static uint16_t tLastX = 0, tLastY = 0;
static uint16_t tDownX = 0, tDownY = 0;
static uint32_t tDownMs = 0;
static bool     tHaveTap = false;       // a first tap is waiting for its pair
static uint16_t tTapX = 0, tTapY = 0;
static uint32_t tTapMs = 0;

static bool nearPx(uint16_t a, uint16_t b) {
  return abs((int)a - (int)b) <= TOUCH_TAP_SLOP_PX;
}

// A double tap flips between the mirror and the scanner page, from either side, so
// it is judged here rather than on the server, which has no say once the page is up.
// Only a genuine tap can pair up: a brush across the glass while picking the panel
// up must not change the screen. On the scanner page the buttons stay buttons, or
// NEXT pressed twice in a hurry would leave the page.
static void noteRelease(uint16_t x, uint16_t y) {
  uint32_t now = millis();
  bool tap = nearPx(x, tDownX) && nearPx(y, tDownY) && now - tDownMs <= TOUCH_TAP_MAX_MS;
  if (!tap) { tHaveTap = false; return; }
  if (tHaveTap && now - tTapMs >= TOUCH_PAIR_MIN_MS && now - tTapMs <= TOUCH_PAIR_MAX_MS &&
      nearPx(x, tTapX) && nearPx(y, tTapY)) {
    tHaveTap = false;
    g_scanToggle = true;
    return;
  }
  tHaveTap = true;
  tTapX = x; tTapY = y; tTapMs = now;
}

static void pollTouch() {
  uint16_t x, y;
  bool down = tft.getTouch(&x, &y);

  // A tap on the page only means something once a second one can no longer follow it.
  if (g_scanMode && tHaveTap && !down && !tDown && millis() - tTapMs > TOUCH_PAIR_MAX_MS) {
    tHaveTap = false;
    scanBlockTap(tTapY);
  }

  if (down && !tDown) {
    tDown = true;
    tLastX = x; tLastY = y;
    tDownX = x; tDownY = y;
    tDownMs = millis();
    g_touchX = x; g_touchY = y;
    if (!g_scanMode) sendTouch(TOUCH_DOWN, x, y, 1);
    else if (scanTouchDown(x, y)) tHaveTap = false;
  } else if (down && tDown) {
    if (x != tLastX || y != tLastY) {
      tLastX = x; tLastY = y;
      if (!g_scanMode) sendTouch(TOUCH_MOVE, x, y, 1);
    }
  } else if (!down && tDown) {
    tDown = false;
    if (g_scanBtn >= 0) {
      scanTouchUp();
    } else {
      if (!g_scanMode) sendTouch(TOUCH_UP, tLastX, tLastY, 0);
      noteRelease(tLastX, tLastY);
    }
  }
}

// ---------------------------------------------------------------- stats overlay

// Cached once at WiFi connect (see setup()) rather than called fresh from
// drawStats() every frame: WiFi.localIP().toString() allocates a String each
// time, and the address never changes for the life of a connection.
static char g_ipStr[16] = {0};

static void drawStats() {
  // Four fields across the full width, each at a fixed column and padded to a
  // fixed width with room to spare. With one free-running line, every change
  // in a number's length slid everything after it sideways — the line visibly
  // jumped with the data — and a line that got shorter left the tail of the
  // previous one on screen (the IP at the end read ".1222" for ".122"). A
  // field that is always the same number of characters in a monospaced font
  // repaints exactly its own box every time, so neither can happen.
  char f[4][20];
  snprintf(f[0], sizeof(f[0]), "%3u.%u fps", g_stats.fps10 / 10, g_stats.fps10 % 10); // " 21.5 fps"
  snprintf(f[1], sizeof(f[1]), "%4u kB/s", g_stats.kbps);                             // "  98 kB/s"
  snprintf(f[2], sizeof(f[2]), "%4d dBm", (int)g_stats.rssi);                         // " -52 dBm"
  snprintf(f[3], sizeof(f[3]), "%15s", g_ipStr);          // right-aligned, fits any IPv4
  // 6 px per character at text size 1: 9, 9, 8 and 15 characters wide.
  static const int16_t col[4] = { 2, 82, 162, 228 };
  tft.setTextColor(TFT_GREEN, TFT_BLACK);
  tft.setTextSize(1);
  tft.setTextDatum(textdatum_t::bottom_left);
  for (int i = 0; i < 4; i++) tft.drawString(f[i], col[i], CYD_H - 1);
}

// ---------------------------------------------------------------- draw task

static void releaseBuf(int8_t slot) { xQueueSend(freeQ, &slot, 0); }

// The connect-time status splash clears the whole screen, so it is only
// allowed before the first frame has been drawn. It used to be drawn at the
// first idle moment instead — which, once the initial repaint arrived faster
// than that, was *after* it: the finished picture got wiped to black, and
// since the server believed it was showing, those areas stayed black until
// something in them happened to change.
static void showServerTextIfFirst() {
  if (!g_showServerText) return;
  g_showServerText = false;
  if (g_stats.framesDrawn != 0) return;
  splash("Polaczono", g_serverTextL1, g_serverTextL2[0] ? g_serverTextL2 : nullptr, 1);
}

#if !CYD_NO_UDP
// drawTask only. Decodes and pushes the rectangles of one datagram; returns true
// when it closes a frame (DG_FLAG_FRAME_END).
//
// Every rectangle is decoded into jpgOut[] - the alternating DMA buffers the JPEG
// path already uses - so the previous rectangle clocks out while this one decodes.
// A rectangle that covers a tile a *newer* datagram already drew is not pushed over
// it: after a loss and a resend, or a reordering, the old datagram can turn up
// late, and its content is by then out of date. When only some of its tiles are
// stale, the fresh ones go out row by row (they are not contiguous in the decode
// buffer); that is rare, so it is not worth a second buffer layout.
static bool drawDatagram(const uint8_t* d, uint16_t n, uint32_t seq, bool& inFrame, uint32_t& frameT0) {
  // Header + scan + EOI of one ENC_JPEG rectangle, assembled where drawTask alone
  // uses it. It cannot share netTask's `scratch`: a TCP frame may still be arriving
  // there at the moment the transport switches.
  static uint8_t jpgBuf[JPEG_HDR_RESERVE + DG_MAX_BYTES + 2];

  uint8_t  rects = d[8];
  uint16_t p = DG_HDR_BYTES;
  for (uint8_t r = 0; r < rects; r++) {
    if (p + DG_RECT_HDR_BYTES > n) break;
    uint8_t  tx = d[p], ty = d[p + 1], tw = d[p + 2], mode = d[p + 3];
    uint16_t len = rdU16(d + p + 4);
    p += DG_RECT_HDR_BYTES;
    // A rectangle that does not fit its own datagram says the rest cannot be trusted.
    if (tw == 0 || tx + tw > TILES_X || ty >= TILES_Y || p + len > n) break;
    const uint8_t* src = d + p;
    p += len;

    uint16_t t0 = ty * TILES_X + tx;
    uint32_t fresh = 0;
    for (uint8_t i = 0; i < tw; i++)
      if ((int32_t)(seq - tileSeq[t0 + i]) > 0) fresh |= 1u << i;
    if (!fresh) { g_rxStale = g_rxStale + 1; continue; }

    uint16_t  w   = tw * TILE_W;
    uint32_t  px  = (uint32_t)w * TILE_H;
    uint16_t* out = jpgOut[jpgOutNext];
    bool      ok  = false;

    if (mode == ENC_JPEG) {
      if (g_jpgTablesLen == 0 || len == 0 || len > DG_MAX_BYTES) continue;
      uint16_t hdrLen = 2 + g_jpgTablesLen + 19 + 14;
      uint8_t* hp = jpgBuf + JPEG_HDR_RESERVE - hdrLen;
      *hp++ = 0xFF; *hp++ = 0xD8;                                     // SOI
      memcpy(hp, g_jpgTables, g_jpgTablesLen); hp += g_jpgTablesLen;  // DQT, DHT
      const uint8_t sof[19] = {                                       // SOF0, 4:2:0
        0xFF, 0xC0, 0x00, 17, 8, (uint8_t)(TILE_H >> 8), (uint8_t)TILE_H, (uint8_t)(w >> 8), (uint8_t)w,
        3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 };
      memcpy(hp, sof, sizeof(sof)); hp += sizeof(sof);
      const uint8_t sos[14] = {                                       // SOS
        0xFF, 0xDA, 0x00, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0 };
      memcpy(hp, sos, sizeof(sos));
      memcpy(jpgBuf + JPEG_HDR_RESERVE, src, len);
      jpgBuf[JPEG_HDR_RESERVE + len]     = 0xFF;                      // EOI
      jpgBuf[JPEG_HDR_RESERVE + len + 1] = 0xD9;
      if (jpegDec.openRAM(jpgBuf + JPEG_HDR_RESERVE - hdrLen, hdrLen + len + 2, jpegNoDraw)) {
        jpegDec.setPixelType(RGB565_BIG_ENDIAN);
        jpegDec.setFramebuffer(out);
        ok = jpegDec.decode(0, 0, 0) != 0;
        jpegDec.close();
      }
    } else if (mode == ENC_RAW) {
      if (len == px * 2) { memcpy(out, src, len); ok = true; }
    } else if (mode <= ENC_PAL4) {
      decodeStrip(mode, src, len, (uint8_t*)out, px);
      ok = true;
    }
    // Anything undecodable is left off the screen and off tileSeq, so the datagram's
    // resend (or the server's periodic sweep) can still deliver the tiles.
    if (!ok) continue;
    jpgOutNext ^= 1;

    if (!inFrame) {
      tft.startWrite();
      inFrame = true;
      frameT0 = millis();
    }
    uint16_t x0 = tx * TILE_W, y0 = ty * TILE_H;
    if (fresh == ((1u << tw) - 1)) {
      tft.pushImageDMA(x0, y0, w, TILE_H, (lgfx::swap565_t*)out);
    } else {
      for (uint8_t a = 0; a < tw;) {
        if (!((fresh >> a) & 1)) { a++; continue; }
        uint8_t b = a;
        while (b < tw && ((fresh >> b) & 1)) b++;
        for (uint8_t row = 0; row < TILE_H; row++)
          tft.pushImageDMA(x0 + a * TILE_W, y0 + row, (b - a) * TILE_W, 1,
                           (lgfx::swap565_t*)(out + (uint32_t)row * w + a * TILE_W));
        a = b;
      }
    }
    for (uint8_t i = 0; i < tw; i++)
      if ((fresh >> i) & 1) tileSeq[t0 + i] = seq;
  }
  return (d[1] & DG_FLAG_FRAME_END) != 0;
}
#endif

static void drawTask(void*) {
  int8_t   pending   = -1;    // buffer whose DMA transfer may still be running
  bool     inFrame   = false;
  uint32_t frameT0   = 0;
  uint32_t lastBytes = 0, lastRate = 0;
  uint32_t framesThisWindow = 0;

  // The end of a frame, whichever transport delivered it: close the SPI write,
  // count the frame, and (over TCP) acknowledge it. Over UDP the ACKs are
  // udpRxTask's, per datagram, and say nothing about drawing.
  auto closeFrame = [&](uint32_t ackSeq, bool tcpAck) {
    if (inFrame) {
      tft.waitDMA();
      tft.endWrite();
      inFrame = false;
    }
    if (pending >= 0) { releaseBuf(pending); pending = -1; }

    uint32_t now    = millis();
    uint16_t blitMs = (uint16_t)(now - frameT0);
    framesThisWindow++;
    // volatile++ is a read-modify-write the compiler is (rightly) unwilling to
    // treat as atomic; framesDrawn has exactly one writer, so a plain
    // read-then-store says the same thing without the deprecation warning.
    g_stats.framesDrawn = g_stats.framesDrawn + 1;
    g_stats.blitMs = blitMs;
    g_stats.rssi   = WiFi.RSSI();
    // Counted over the same one second window as kbps below, rather than
    // averaged from each frame's arrival gap. The gap form read high — and
    // the reason is arithmetic, not measurement error: 1000/dt is convex, so
    // averaging it across uneven gaps overstates the rate. Frames arrive
    // clumped whenever TCP delivers a burst, and a single 20 ms gap in an
    // otherwise 10 fps stream contributes "50 fps" to that average. The
    // panel ended up claiming a higher rate than the server said it sent,
    // which is impossible and made the number useless for judging whether a
    // send-rate cap was taking effect.
    if (now - lastRate >= 1000) {
      uint32_t elapsed = now - lastRate;
      g_stats.fps10 = (uint16_t)((framesThisWindow * 10000UL + elapsed / 2) / elapsed);
      g_stats.kbps = (uint16_t)((g_stats.bytesIn - lastBytes) / 1024);
      framesThisWindow = 0;
      lastBytes = g_stats.bytesIn;
      lastRate  = now;
    }

    if (g_set.stats && !g_scanMode) drawStats();
    if (tcpAck) sendAck(ackSeq, blitMs, g_stats.fps10);

    pollTouch();
  };

  for (;;) {
    if (g_scanToggle) {
      g_scanToggle = false;
      if (inFrame) {
        tft.waitDMA();
        tft.endWrite();
        inFrame = false;
      }
      if (pending >= 0) { releaseBuf(pending); pending = -1; }
      tHaveTap = false;   // a tap from before the switch must not act on the new screen
      if (g_scanMode) scanLeave(); else scanEnter();
    }

    StripJob job;
    // Over UDP a frame's last datagram may be lost, and nothing else will then close
    // the SPI write: wait only briefly while one is open. Otherwise the wait is what
    // paces touch on a static screen, and a quick tap must not fall between two looks.
#if !CYD_NO_UDP
    TickType_t waitTicks = (inFrame && g_udpSession != 0) ? pdMS_TO_TICKS(5) : pdMS_TO_TICKS(25);
#else
    TickType_t waitTicks = pdMS_TO_TICKS(25);
#endif
    if (xQueueReceive(drawQ, &job, waitTicks) != pdTRUE) {
#if !CYD_NO_UDP
      if (inFrame && g_udpSession != 0) {
        tft.waitDMA();
        tft.endWrite();
        inFrame = false;
        if (pending >= 0) { releaseBuf(pending); pending = -1; }
        continue;
      }
#endif
      // Idle. Keep servicing touch so the desktop stays usable on a static
      // screen.
      if (!inFrame) {
        pollTouch();
        if (g_scanMode) scanDrawUpdate();
        else showServerTextIfFirst();
        if (g_rotationPending) {
          g_rotationPending = false;
          tft.setRotation(g_set.rotation);
          // Touch realigns itself off the same rotation (LovyanGFX derives
          // its transform from it), but nothing repaints on its own: the
          // pixels already on the glass are now addressed through a flipped
          // window, so left where they are they'd just look wrong in the new
          // orientation rather than corrupted. Blank until the refresh below
          // repaints the picture properly.
          tft.fillScreen(TFT_BLACK);
          if (g_scanMode) scanPaintAll();
          else linkRequestFullRefresh();
        }
        if (g_restartPending) {
          splash("Zapisano", g_restartMsg, "Restart...", 1);
          delay(400);
          ESP.restart();
        }
      }
      continue;
    }

    // The scanner page owns the screen. Pixels still on their way when it opened go
    // back unread; frame ends are still acknowledged, or flow control would wedge.
    if (g_scanMode) {
      if (job.kind == JOB_FRAME_END) closeFrame(job.seq, true);
#if !CYD_NO_UDP
      else if (job.kind == JOB_DGRAM) xQueueSend(dgFreeQ, &job.slot, 0);
#endif
      else releaseBuf(job.slot);
      continue;
    }

    // Checked before the first strip is drawn, not only when idle: netTask
    // raises the flag before it queues any part of the first frame (the text
    // precedes frames on the wire), so this is the moment the splash can go
    // up underneath the picture instead of on top of it.
    if (!inFrame) showServerTextIfFirst();

    if (job.kind == JOB_STRIP) {
      if (!inFrame) {
        tft.startWrite();
        inFrame = true;
        frameT0 = millis();
      }
      tft.pushImageDMA(job.x, job.y, job.w, job.h, (lgfx::swap565_t*)pool[job.slot]);
      // pushImageDMA cannot begin a new transfer until the previous one has
      // drained off the SPI peripheral, so the moment this returns, the buffer
      // from the previous call is provably free. Releasing one behind like
      // this is what keeps the pipeline overlapped; waiting on our own DMA
      // here would serialise SPI against the network again and cost ~13 fps.
      if (pending >= 0) releaseBuf(pending);
      pending = job.slot;
      continue;
    }

    if (job.kind == JOB_JPEG) {
      if (!inFrame) {
        tft.startWrite();
        inFrame = true;
        frameT0 = millis();
      }
      // Decode into memory, then DMA the result — rather than drawJpg()
      // straight to the panel, which writes each 16x16 block through its own
      // address window and waits on every one. That wait sat on this core
      // between decodes; now the previous strip clocks out underneath the
      // decode of this one.
      //
      // The two output buffers alternate, and reusing one is safe: after it
      // was last pushed, the other one was pushed too, and pushImageDMA
      // doesn't return until the transfer before it has drained.
      uint16_t* out = jpgOut[jpgOutNext];
      jpgOutNext ^= 1;
      if (jpegDec.openRAM(pool[job.slot] + job.off, job.len, jpegNoDraw)) {
        // Both after openRAM, which resets them. Big-endian RGB565 is the
        // panel-native order raw strips arrive in, so the result goes out
        // unconverted; with a framebuffer set, rows land in it directly at
        // the image's own width — exactly one strip.
        jpegDec.setPixelType(RGB565_BIG_ENDIAN);
        jpegDec.setFramebuffer(out);
        jpegDec.decode(0, 0, 0);
        jpegDec.close();
      }
      releaseBuf(job.slot);   // the compressed copy is finished with
      tft.pushImageDMA(job.x, job.y, job.w, job.h, (lgfx::swap565_t*)out);
      if (pending >= 0) { releaseBuf(pending); pending = -1; }
      continue;
    }

#if !CYD_NO_UDP
    if (job.kind == JOB_DGRAM) {
      const uint8_t* d = dgRing + (size_t)job.slot * DG_MAX_BYTES;
      // A datagram of a session that has ended (the transport went back to TCP, or
      // the connection dropped and came back) must not touch tileSeq: its numbering
      // would outrank the new session's and get every new datagram thrown away as stale.
      if (job.x == g_udpSession) {
        if (g_tileSeqReset) {
          g_tileSeqReset = false;
          memset(tileSeq, 0, sizeof(tileSeq));
        }
        bool frameEnd = drawDatagram(d, job.len, job.seq, inFrame, frameT0);
        xQueueSend(dgFreeQ, &job.slot, 0);
        if (frameEnd) closeFrame(job.seq, false);
      } else {
        xQueueSend(dgFreeQ, &job.slot, 0);
      }
      continue;
    }
#endif

    // Frame boundary.
    closeFrame(job.seq, true);
  }
}

// ---------------------------------------------------------------- provisioning

// l2Size lets one caller (the IP address shown while waiting to find the
// server) render line 2 bigger than the usual small print, without changing
// every other splash() call's layout — no default argument, since Arduino's
// auto-generated prototype for a function defined further down the .ino
// doesn't reliably carry one through.
static void splash(const char* l1, const char* l2, const char* l3, uint8_t l2Size) {
  tft.fillScreen(TFT_BLACK);
  tft.setTextColor(TFT_WHITE, TFT_BLACK);
  tft.setTextDatum(textdatum_t::middle_center);
  tft.setTextSize(2);
  tft.drawString(l1, CYD_W / 2, 80);
  tft.setTextSize(l2Size);
  if (l2) tft.drawString(l2, CYD_W / 2, 125);
  tft.setTextSize(1);
  if (l3) tft.drawString(l3, CYD_W / 2, 145);
}

static WebServer portal(80);
static DNSServer dnsServer;

static const char PORTAL_HTML[] PROGMEM =
  "<!doctype html><meta charset=utf-8>"
  "<meta name=viewport content='width=device-width,initial-scale=1'>"
  "<title>CYD Monitor</title>"
  "<style>body{font:16px system-ui;margin:0;padding:24px;background:#111;color:#eee}"
  "h1{font-size:20px}label{display:block;margin:14px 0 4px}"
  "input{width:100%;padding:10px;border-radius:8px;border:1px solid #444;"
  "background:#1c1c1c;color:#eee;box-sizing:border-box}"
  "button{margin-top:20px;width:100%;padding:12px;border:0;border-radius:8px;"
  "background:#f5c518;font-weight:600}p{color:#999;font-size:13px}</style>"
  "<h1>CYD Monitor &mdash; konfiguracja</h1><form method=POST action=/save>"
  "<label>Sie&#263; WiFi (SSID)</label><input name=ssid required>"
  "<label>Has&#322;o</label><input name=pass type=password>"
  "<label>IP serwera <span style=color:#777>(puste = szukaj automatycznie)</span></label>"
  "<input name=host placeholder=192.168.1.10>"
  "<button type=submit>Zapisz i uruchom ponownie</button></form>"
  "<p>Po zapisaniu monitor po&#322;&#261;czy si&#281; z sieci&#261; "
  "i sam znajdzie serwer w LAN.</p>";

static void runProvisioning() {
  WiFi.mode(WIFI_AP);
  WiFi.softAP("CYD-Monitor-Setup");
  delay(300);
  splash("Konfiguracja", "WiFi: CYD-Monitor-Setup", "http://192.168.4.1", 1);

  dnsServer.start(53, "*", WiFi.softAPIP());
  portal.onNotFound([]() { portal.send_P(200, "text/html", PORTAL_HTML); });
  portal.on("/", []() { portal.send_P(200, "text/html", PORTAL_HTML); });
  portal.on("/save", HTTP_POST, []() {
    settingsSaveWifi(portal.arg("ssid").c_str(), portal.arg("pass").c_str(),
                     portal.arg("host").c_str());
    portal.send(200, "text/html",
                "<meta charset=utf-8><body style='font:16px system-ui;"
                "background:#111;color:#eee;padding:24px'>Zapisano. Restart...");
    delay(400);
    ESP.restart();
  });
  portal.begin();

  for (;;) {
    dnsServer.processNextRequest();
    portal.handleClient();
    delay(2);
  }
}

// ---------------------------------------------------------------- setup

static void applyTouchCalibration() {
  // LovyanGFX keeps the raw ADC bounds in the touch config, so a stored
  // calibration is applied by rewriting those four numbers rather than by
  // transforming coordinates after every read.
  auto* t = tft.touch();
  if (!t) return;
  auto c = t->config();
  c.x_min = g_set.txMin; c.x_max = g_set.txMax;
  c.y_min = g_set.tyMin; c.y_max = g_set.tyMax;
  t->config(c);
}

// Only these four rotation register values leave the panel in landscape;
// see the table in Settings::rotation. The values in between are PORTRAIT
// (240x320).
static bool rotationIsLandscape(uint8_t r) {
  return r == 1 || r == 3 || r == 5 || r == 7;
}

// ---------------------------------------------------------------- settings web server
//
// User-requested: the configuration page lives here, on the panel, served
// continuously once WiFi is up — not on the Windows server. Every control
// below applies its change through the same paths a touch-driven menu used
// to: linkSendOpt() for anything the server owns (contrast, gamma, scaling),
// and directly through g_set/tft for anything the device owns outright
// (brightness, the stats overlay, WiFi) — this page is what replaced that
// on-panel menu once every setting it carried had an equivalent here.
//
// Served from the same WebServer instance runProvisioning() uses for the
// captive portal. The two never run in the same boot: provisioning only
// starts when there is no WiFi to be up yet, and this only starts once WiFi
// is confirmed connected, so reusing the object and port 80 is safe.

// Load of each core, 0-100, from how much of the last second its idle task
// did NOT get to run. Measured this way rather than by counting idle-hook
// calls because a hook that never yields would stop the core from waiting for
// interrupts between them — trading a measurement for constant full-power
// idling. The run-time counters used here are compiled into this core
// (CONFIG_FREERTOS_GENERATE_RUN_TIME_STATS) and cost nothing between reads.
static volatile uint8_t g_cpuLoad[2] = {0, 0};

static void sampleCpuLoad() {
  static uint32_t lastMs = 0, lastTotal = 0;
  static uint32_t lastIdle[2] = {0, 0};
  static TaskStatus_t tasks[32];
  uint32_t now = millis();
  if (now - lastMs < 1000) return;
  lastMs = now;

  uint32_t total = 0;
  UBaseType_t n = uxTaskGetSystemState(tasks, 32, &total);
  uint32_t idle[2] = { lastIdle[0], lastIdle[1] };
  for (UBaseType_t i = 0; i < n; i++) {
    if (strcmp(tasks[i].pcTaskName, "IDLE0") == 0) idle[0] = tasks[i].ulRunTimeCounter;
    else if (strcmp(tasks[i].pcTaskName, "IDLE1") == 0) idle[1] = tasks[i].ulRunTimeCounter;
  }
  uint32_t dTotal = total - lastTotal;   // unsigned: survives the counter wrapping
  if (lastTotal != 0 && dTotal > 0) {
    for (int c = 0; c < 2; c++) {
      int load = 100 - (int)(100ULL * (uint32_t)(idle[c] - lastIdle[c]) / dTotal);
      g_cpuLoad[c] = (uint8_t)constrain(load, 0, 100);
    }
  }
  lastTotal = total;
  lastIdle[0] = idle[0];
  lastIdle[1] = idle[1];
}

static String settingsStateJson() {
  uint8_t r = g_set.srcRes < RES_TABLE_LEN ? g_set.srcRes : 0;

  // resLabel is informational only — reports whatever resolution the server
  // is actually mirroring. There's no picker for it here (or anywhere else,
  // now that the on-panel menu's old "Zrodlo" row is gone with it): the
  // server refuses to change a real monitor's mode at all (see the note on
  // ApplyResolution in DeviceSession.cs), so a control offering to change it
  // would just be a choice with no effect.
#if !CYD_NO_UDP
  // The v5 data plane, as the panel sees it: whether pixels come over UDP now, datagrams
  // received, dropped for want of a free slot, ignored as stale (a newer datagram had
  // already drawn the tile), and the longest gap between two datagrams this session.
  char udp[160];
  snprintf(udp, sizeof(udp),
    ",\"udp\":%s,\"udpReady\":%s,\"rxDgrams\":%lu,\"rxDrops\":%lu,\"rxStale\":%lu,\"maxRxGapMs\":%lu",
    g_udpSession != 0 ? "true" : "false", g_udpReady ? "true" : "false",
    (unsigned long)g_rxDgrams, (unsigned long)g_rxDrops, (unsigned long)g_rxStale,
    (unsigned long)g_rxMaxGapMs);
#else
  const char* udp = "";
#endif
  char buf[800];
  snprintf(buf, sizeof(buf),
    "{\"connected\":%s,\"contrast\":%d,\"gamma\":%d,\"sharpen\":%u,\"dither\":%s,\"scale\":%u,"
    "\"resLabel\":\"%ux%u\",\"brightnessPercent\":%u,\"stats\":%s,\"scanFlip\":%s,"
    "\"deviceFps\":%.1f,\"rssi\":%d,\"kbps\":%u,\"rotation\":%u,"
    "\"cpu0\":%u,\"cpu1\":%u,\"heapFree\":%u,\"heapMin\":%u,\"heapBlock\":%u,\"uptime\":%lu,"
    "\"scan\":%s,\"touchX\":%u,\"touchY\":%u,\"serverIp\":\"%s\"%s}",
    g_stats.connected ? "true" : "false",
    g_set.contrast, g_set.gamma, g_set.sharpen, g_set.dither ? "true" : "false", g_set.scaleMode,
    RES_TABLE[r].w, RES_TABLE[r].h,
    (unsigned)((g_set.brightness * 100u + 127u) / 255u), g_set.stats ? "true" : "false",
    g_set.scanFlip ? "true" : "false",
    g_stats.fps10 / 10.0, (int)g_stats.rssi, g_stats.kbps, g_set.rotation,
    (unsigned)g_cpuLoad[0], (unsigned)g_cpuLoad[1], (unsigned)ESP.getFreeHeap(),
    (unsigned)ESP.getMinFreeHeap(), (unsigned)ESP.getMaxAllocHeap(), (unsigned long)(millis() / 1000),
    g_scanMode ? "true" : "false", (unsigned)g_touchX, (unsigned)g_touchY, g_serverIp,
    udp);
  return String(buf);
}

static void handleApiState() {
  portal.send(200, "application/json", settingsStateJson());
}

static void handleApiSet() {
  String key = portal.arg("key");
  long value = portal.arg("value").toInt();
  // See wireRange() in SETTINGS_HTML: a drag's intermediate 'input' ticks
  // pass commit=0 so the value only goes out over the link, not to flash —
  // settingsSave() below does ~13 NVS writes, and each one briefly halts
  // both cores while the flash cache is suspended for the erase/write, which
  // at drag-tick frequency was stalling the network task often enough to
  // read as random disconnects.
  bool commit = portal.arg("commit") != "0";

  // Same ranges the server's own OPT_* handling clamps to (see HandleOption
  // in DeviceSession.cs), so a malformed request here can't send a value
  // further out of range than a well-behaved client ever would.
  if (key == "contrast") {
    value = constrain(value, -64, 64);
    linkSendOpt(OPT_CONTRAST, (int16_t)value);
    g_set.contrast = (int16_t)value;   // optimistic; OPTACK is authoritative
  } else if (key == "gamma") {
    value = constrain(value, 50, 300);
    linkSendOpt(OPT_GAMMA, (int16_t)value);
    g_set.gamma = (int16_t)value;
  } else if (key == "sharpen") {
    value = constrain(value, 0, 100);
    linkSendOpt(OPT_SHARPEN, (int16_t)value);
    g_set.sharpen = (uint8_t)value;
  } else if (key == "dither") {
    value = value ? 1 : 0;
    linkSendOpt(OPT_DITHER, (int16_t)value);
    g_set.dither = (uint8_t)value;
  } else if (key == "scale") {
    value = constrain(value, 0, 2);
    linkSendOpt(OPT_SCALE_MODE, (int16_t)value);
    g_set.scaleMode = (uint8_t)value;
  } else if (key == "brightness") {
    // Device-owned: applied directly, no server round trip needed at all.
    uint8_t v = (uint8_t)constrain((value * 255 + 50) / 100, 5, 255);
    g_set.brightness = v;
    tft.setBrightness(v);            // backlight PWM only — not the SPI/DMA
                                      // path drawTask owns, so safe from here
  } else if (key == "refresh") {
    linkRequestFullRefresh();
  } else if (key == "rotation") {
    // Only a 180deg turn is offered, not 90deg CW/CCW, because it's all this
    // panel's rotation register can do while staying in landscape (see the
    // note on Settings::rotation): {1,3,5,7} differ only by which axes are
    // mirrored, and mirroring both axes together is what a 180deg turn is.
    // Bit 2 is exactly that flip, so the four values form two pairs under it,
    // 1<->3 and 5<->7 — and the pair a unit is in is the one that was
    // established as un-mirrored for its cabling, which this must not leave.
    // -180 and +180 are the same physical turn; they map to the two members
    // of the current pair (bit 2 clear / set) so the menu can show which one
    // is active, which a one-shot "flip" could not.
    uint8_t next = (value < 0) ? (g_set.rotation & ~2) : (g_set.rotation | 2);
    if (next != g_set.rotation) {
      g_set.rotation = next;
      g_rotationPending = true;
    }
  } else if (key == "stats") {
    g_set.stats = value ? 1 : 0;     // purely local overlay — see drawStats()
  } else if (key == "scanflip") {
    g_set.scanFlip = value ? 1 : 0;  // read on every touch of the scanner page
  } else if (key == "scan") {
    // The same flip as a double tap, for testing without a finger on the glass.
    // Nothing to persist: the panel always starts on the mirror.
    if ((value != 0) != g_scanMode) g_scanToggle = true;
    portal.send(200, "application/json", settingsStateJson());
    return;
  } else {
    portal.send(400, "application/json", "{\"ok\":false,\"error\":\"nieznany klucz\"}");
    return;
  }
  if (commit) settingsSave();
  portal.send(200, "application/json", settingsStateJson());
}

static void handleApiWifi() {
  String ssid = portal.arg("ssid");
  if (ssid.length() == 0) {
    portal.send(400, "application/json", "{\"ok\":false,\"error\":\"SSID nie moze byc puste\"}");
    return;
  }
  settingsSaveWifi(ssid.c_str(), portal.arg("pass").c_str(), portal.arg("host").c_str());
  snprintf(g_restartMsg, sizeof(g_restartMsg), "Nowa siec WiFi");
  g_restartPending = true;
  portal.send(200, "application/json", "{\"ok\":true}");
}

static void handleApiResetNet() {
  settingsResetNetwork();
  snprintf(g_restartMsg, sizeof(g_restartMsg), "Siec zresetowana");
  g_restartPending = true;
  portal.send(200, "application/json", "{\"ok\":true}");
}

static const char SETTINGS_HTML[] PROGMEM = R"HTML(
<!doctype html><html lang=pl><head><meta charset=utf-8>
<meta name=viewport content="width=device-width, initial-scale=1">
<title>CYD Monitor</title>
<style>
body{font:15px system-ui,sans-serif;background:#111;color:#eee;margin:0;padding:20px;max-width:480px}
h1{font-size:18px;margin:0 0 4px}
h2{font-size:15px;margin-top:28px}
.sub{color:#999;font-size:13px;margin-bottom:18px}
.row{margin:16px 0}
.row label{display:flex;justify-content:space-between;margin-bottom:6px}
.row .val{color:#f5c518;font-variant-numeric:tabular-nums}
input[type=range]{width:100%}
select,button,input[type=text],input[type=password]{width:100%;padding:10px;border-radius:8px;
  border:1px solid #444;background:#1c1c1c;color:#eee;font-size:15px;box-sizing:border-box}
button{background:#f5c518;color:#111;font-weight:600;border:0;margin-top:4px}
.danger{background:#772222;color:#eee}
.link{display:block;padding:12px;border-radius:8px;background:#f5c518;color:#111;font-weight:600;text-align:center;text-decoration:none;margin-bottom:16px}
.toggle{display:flex;align-items:center;justify-content:space-between}
.status{padding:10px 12px;border-radius:8px;background:#1c1c1c;font-size:13px;margin-bottom:16px;line-height:1.6}
.dot{display:inline-block;width:8px;height:8px;border-radius:50%;margin-right:6px}
.on{background:#3fb950}.off{background:#f85149}
.note{color:#888;font-size:12px;margin-top:24px;line-height:1.5}
</style></head><body>
<a id=skaner class=link style="display:none">Skaner SDRoxide</a>
<h1>CYD Monitor</h1>
<div class=sub>Ustawienia obrazu</div>
<div class=status id=status>-</div>

<div class=row><label>Kontrast <span class=val id=v-contrast></span></label>
<input type=range id=contrast min=-64 max=64 step=4></div>
<div class=row><label>Gamma <span class=val id=v-gamma></span></label>
<input type=range id=gamma min=50 max=300 step=5></div>
<div class=row><label>Jasnosc <span class=val id=v-bright></span></label>
<input type=range id=brightness min=0 max=100 step=1></div>
<div class=row><label>Wyostrzenie <span class=val id=v-sharpen></span></label>
<input type=range id=sharpen min=0 max=100 step=5></div>
<div class=row><label>Skalowanie</label><select id=scale>
<option value=0>dopasuj</option><option value=1>rozciagnij</option>
<option value=2>1:1 (podaza za kursorem)</option></select></div>
<div class=row><label>Obrot ekranu</label><select id=rotation>
<option value=-180>-180&deg;</option><option value=180>+180&deg;</option></select></div>
<div class="row toggle"><label style="margin:0">Dithering</label>
<input type=checkbox id=dither style="width:20px;height:20px"></div>
<div class="row toggle"><label style="margin:0">Statystyki na ekranie</label>
<input type=checkbox id=stats style="width:20px;height:20px"></div>
<div class="row toggle"><label style="margin:0">Odwroc dotyk skanera (gdy przyciski na dole reaguja na dotyk u gory)</label>
<input type=checkbox id=scanflip style="width:20px;height:20px"></div>
<div class=row><button id=refresh>Wymus pelne odswiezenie</button></div>

<h2>Siec WiFi</h2>
<div class=sub>Zapisuje i uruchamia panel ponownie w nowej sieci</div>
<div class=row><label>SSID</label><input type=text id=wssid autocomplete=off></div>
<div class=row><label>Haslo</label><input type=password id=wpass autocomplete=off></div>
<div class=row><label>IP serwera <span style="color:#777">(puste = szukaj automatycznie)</span></label>
<input type=text id=whost placeholder=192.168.1.10 autocomplete=off></div>
<div class=row><button id=wsave>Zapisz i uruchom ponownie</button></div>
<div id=wmsg style="font-size:13px;color:#f5c518;min-height:18px"></div>
<div class=row><button id=resetnet class=danger>Resetuj siec (wroc do konfiguracji)</button></div>

<script>
const $ = id => document.getElementById(id);
let applying = false;

async function refreshState() {
  const r = await fetch('/api/state');
  const s = await r.json();
  $('status').innerHTML = `<span class="dot ${s.connected ? 'on' : 'off'}"></span>` +
    (s.connected ? `polaczono z serwerem &middot; ${s.deviceFps} fps &middot; ${s.rssi} dBm &middot; ${s.kbps} kB/s`
                 : 'serwer niepodlaczony');
  if (!applying) {
    $('contrast').value = s.contrast;
    $('gamma').value = s.gamma;
    $('brightness').value = s.brightnessPercent;
    $('sharpen').value = s.sharpen;
    $('dither').checked = s.dither;
    $('stats').checked = s.stats;
    $('scanflip').checked = s.scanFlip;
    $('scale').value = s.scale;
    $('rotation').value = (s.rotation & 2) ? 180 : -180;
  }
  $('v-contrast').textContent = (s.contrast > 0 ? '+' : '') + s.contrast;
  $('v-gamma').textContent = (s.gamma / 100).toFixed(2);
  $('v-bright').textContent = s.brightnessPercent;
  $('v-sharpen').textContent = s.sharpen;
  if (s.serverIp) {
    $('skaner').href = `http://${s.serverIp}:7793/`;
    $('skaner').style.display = 'block';
  }
}

async function setOpt(key, value, commit = true) {
  await fetch('/api/set', {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: `key=${encodeURIComponent(key)}&value=${encodeURIComponent(value)}` +
          (commit ? '' : '&commit=0'),
  });
  if (!applying) refreshState();
}

// Keeps 'applying' true for the whole drag gesture, not just one round trip,
// so refreshState()'s periodic poll can't land mid-drag and snap the handle
// back to a stale value.
//
// 'input' fires on every pixel the handle moves — tens of times a second on
// a real drag — so those go over the link live (commit=0, the picture must
// track the finger) but skip the NVS write: flashing every field to the
// settings partition that often was pausing both cores long enough, often
// enough, to look exactly like the network dying and the picture skipping —
// which is what it actually was. Only 'change' (the drag ending) commits.
function wireRange(id, key) {
  const el = $(id);
  el.addEventListener('pointerdown', () => { applying = true; });
  el.addEventListener('input', e => setOpt(key, e.target.value, false));
  el.addEventListener('change', () => { applying = false; setOpt(key, el.value, true); });
}

wireRange('contrast', 'contrast');
wireRange('gamma', 'gamma');
wireRange('brightness', 'brightness');
wireRange('sharpen', 'sharpen');
$('dither').addEventListener('change', e => setOpt('dither', e.target.checked ? 1 : 0));
$('stats').addEventListener('change', e => setOpt('stats', e.target.checked ? 1 : 0));
$('scanflip').addEventListener('change', e => setOpt('scanflip', e.target.checked ? 1 : 0));
$('scale').addEventListener('change', e => setOpt('scale', e.target.value));
$('refresh').addEventListener('click', () => setOpt('refresh', 1));
$('rotation').addEventListener('change', e => setOpt('rotation', e.target.value));

$('wsave').addEventListener('click', async () => {
  const ssid = $('wssid').value.trim();
  const msg = $('wmsg');
  if (!ssid) { msg.textContent = 'SSID nie moze byc puste'; return; }
  msg.textContent = 'Zapisuje i uruchamiam ponownie...';
  const body = `ssid=${encodeURIComponent(ssid)}&pass=${encodeURIComponent($('wpass').value)}` +
               `&host=${encodeURIComponent($('whost').value.trim())}`;
  try {
    const r = await fetch('/api/wifi', { method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body });
    const j = await r.json();
    msg.textContent = j.ok ? 'Zapisano. Restart...' : (j.error || 'Blad');
  } catch { msg.textContent = 'Panel juz sie restartuje...'; }
});

$('resetnet').addEventListener('click', async () => {
  if (!confirm('Wyczyscic siec WiFi i wrocic do konfiguracji?')) return;
  try { await fetch('/api/resetnet', { method: 'POST' }); } catch {}
  $('wmsg').textContent = 'Restart...';
});

refreshState();
setInterval(refreshState, 1500);
</script>
</body></html>
)HTML";

static void startSettingsWeb() {
  portal.on("/", []() { portal.send_P(200, "text/html", SETTINGS_HTML); });
  portal.on("/api/state", handleApiState);
  portal.on("/api/set", HTTP_POST, handleApiSet);
  portal.on("/api/wifi", HTTP_POST, handleApiWifi);
  portal.on("/api/resetnet", HTTP_POST, handleApiResetNet);
  portal.onNotFound([]() { portal.send(404, "text/plain", "not found"); });
  portal.begin();
}

void setup() {
  Serial.begin(CYD_SERIAL_BAUD);
  settingsLoad();

  // Build-time rotation override, applied before the guard below so an
  // out-of-range value passed by mistake still gets caught there.
  if (CYD_FORCE_ROTATION >= 0 && g_set.rotation != (uint8_t)CYD_FORCE_ROTATION) {
    Serial.printf("[panel] wymuszam obrot %d (bylo %u)\n",
                  (int)CYD_FORCE_ROTATION, g_set.rotation);
    g_set.rotation = (uint8_t)CYD_FORCE_ROTATION;
    settingsSave();
  }

  // The wire format is permanently 320x240 (see protocol.h), so a portrait
  // rotation is never a valid state for this firmware, only a broken one:
  // LovyanGFX then reports a 240-wide canvas and silently clips every strip
  // the server sends with x >= 240. Measured on a real unit with the
  // --test alignment pattern: exactly three quarters of the picture drew,
  // the remaining quarter of the glass was never written at all, and the
  // clipped region kept stale pixels from whatever had been there before —
  // which reads as "the image doesn't fill the screen", "it sits off to one
  // side" and "it doesn't refresh properly" all at once. Nothing in the
  // menu can produce this (applyDelta cycles ROT_VALUES only), but a stale
  // NVS value from an older build can, and did.
  if (!rotationIsLandscape(g_set.rotation)) {
    Serial.printf("[panel] zapisany obrot %u to orientacja pionowa - wracam do 1\n",
                  g_set.rotation);
    g_set.rotation = 1;
    settingsSave();
  }

  // ONE-SHOT: user-requested horizontal mirror flip. Toggles only the
  // horizontal-mirror bit of the rotation register (the 5<->1, 3<->7
  // pairing in ROT_VALUES/getMadCtl's table), leaving whatever vertical
  // orientation was already calibrated untouched. Gated on its own NVS
  // flag rather than running unconditionally, because applying this on
  // every boot would flip the image back and forth on every power cycle
  // instead of settling once.
  {
    Preferences mirrorOnce;
    if (mirrorOnce.begin("cydmon", false)) {
      if (!mirrorOnce.getBool("mirXdone", false)) {
        static const uint8_t MX_TOGGLE[8] = { 0, 5, 2, 7, 4, 1, 6, 3 };
        g_set.rotation = MX_TOGGLE[g_set.rotation & 7];
        settingsSave();
        mirrorOnce.putBool("mirXdone", true);
      }
      mirrorOnce.end();
    }
  }

  tft.init();
  // 320x240 landscape. Which of the two landscape values (1 or 3) is right
  // side up depends on how this specific unit's ribbon cable was soldered at
  // the factory and varies between otherwise-identical boards, so it is a
  // persisted setting (menu: "Obrot") rather than a constant — see the note
  // on Settings::rotation.
  tft.setRotation(g_set.rotation);
  // Print what the rotation actually produced rather than what it was meant
  // to: a canvas that isn't CYD_W x CYD_H means strips will be clipped, and
  // that is far easier to read here than to infer from a photo of the glass.
  Serial.printf("[panel] obrot=%u  plotno=%dx%d (oczekiwane %dx%d)%s\n",
                g_set.rotation, (int)tft.width(), (int)tft.height(), CYD_W, CYD_H,
                (tft.width() == CYD_W && tft.height() == CYD_H) ? "" : "  <-- ZLE");
  tft.setSwapBytes(false);   // wire data is already panel-native, see protocol.h
  tft.setBrightness(g_set.brightness);
  applyTouchCalibration();
  tft.fillScreen(TFT_BLACK);

  pinMode(0, INPUT_PULLUP);  // BOOT button, held down if WiFi fails below to force provisioning

  if (strlen(g_set.ssid) == 0) runProvisioning();

  splash("CYD Monitor", "Laczenie z WiFi...", g_set.ssid, 1);
  WiFi.mode(WIFI_STA);
  // Modem sleep parks the radio between beacons and adds 100+ ms of latency
  // to whichever frame arrives next, which reads as a periodic stutter no
  // amount of encoder tuning can fix.
  WiFi.setSleep(false);
  // Every previous drop only ever showed up here as "the link died"; nothing
  // recorded *why* the station itself left the AP. The reason code is the
  // one piece of evidence that tells a flaky router apart from an AP that's
  // deliberately booting the station (power save timeout, DHCP NAK, low
  // signal) — logged rather than acted on, so it costs nothing if it never
  // fires again.
  WiFi.onEvent([](WiFiEvent_t event, WiFiEventInfo_t info) {
    if (event == ARDUINO_EVENT_WIFI_STA_DISCONNECTED) {
      g_lastWifiReason = info.wifi_sta_disconnected.reason;
      Serial.printf("WiFi STA disconnected, reason=%d\n", g_lastWifiReason);
    }
  });
  WiFi.begin(g_set.ssid, g_set.pass);
  uint32_t t0 = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - t0 < 20000) delay(200);
  if (WiFi.status() != WL_CONNECTED) {
    splash("Brak WiFi", "Przytrzymaj BOOT = ustawienia", "restart za 8 s", 1);
    uint32_t hold = millis();
    while (millis() - hold < 8000) {
      if (digitalRead(0) == LOW) runProvisioning();
      delay(20);
    }
    ESP.restart();
  }

  snprintf(g_ipStr, sizeof(g_ipStr), "%s", WiFi.localIP().toString().c_str());
  splash("CYD Monitor", g_ipStr, nullptr, 2);

  // Up from here regardless of whether the PC server is ever found — the
  // settings page (contrast, gamma, brightness, WiFi, ...) is this panel's
  // own, not something the server hosts, so it has no dependency on that
  // connection existing.
#if !CYD_NO_WEB
  startSettingsWeb();
#endif

  upQ   = xQueueCreate(16, sizeof(UpMsg));
  freeQ = xQueueCreate(CYD_STRIP_BUFFERS, sizeof(int8_t));
#if !CYD_NO_UDP
  // Room for every datagram slot as well: udpRxTask must never find the queue full
  // while a slot is free, or it would drop what it has room for.
  drawQ = xQueueCreate(CYD_STRIP_BUFFERS + 2 + DG_SLOTS, sizeof(StripJob));
#else
  drawQ = xQueueCreate(CYD_STRIP_BUFFERS + 2, sizeof(StripJob));
#endif
  scratch = (uint8_t*)malloc(MAX_STRIP_BYTES);
  if (!scratch) { splash("Brak RAM", "scratch", nullptr, 1); for (;;) delay(1000); }
  for (int i = 0; i < 2; i++) {
    jpgOut[i] = (uint16_t*)heap_caps_malloc(MAX_STRIP_BYTES, MALLOC_CAP_DMA);
    if (!jpgOut[i]) { splash("Brak RAM", "bufory JPEG", nullptr, 1); for (;;) delay(1000); }
  }
  for (int i = 0; i < CYD_STRIP_BUFFERS; i++) {
    // MALLOC_CAP_DMA is mandatory: the SPI peripheral reads these buffers
    // directly and ESP32 DMA cannot reach every heap region.
    pool[i] = (uint8_t*)heap_caps_malloc(MAX_STRIP_BYTES, MALLOC_CAP_DMA);
    if (!pool[i]) {
      splash("Brak RAM", "Zmniejsz CYD_STRIP_BUFFERS", nullptr, 1);
      for (;;) delay(1000);
    }
    int8_t slot = i;
    xQueueSend(freeQ, &slot, 0);
  }

  // netTask on core 0 next to the WiFi stack so received data never has to
  // cross cores; drawTask on core 1 with the SPI peripheral.
  xTaskCreatePinnedToCore(netTask,  "net",  6144, nullptr, 3, nullptr, 0);
  // 6 KB: drawTask runs the JPEG decoder (JOB_JPEG).
  xTaskCreatePinnedToCore(drawTask, "draw", 6144, nullptr, 4, nullptr, 1);

#if !CYD_NO_UDP
  // The v5 data plane. Without the ring or the task nothing new is ever offered to
  // the server, and this behaves as a plain v4 panel.
  dgRing  = (uint8_t*)malloc((size_t)DG_SLOTS * DG_MAX_BYTES);
  dgFreeQ = xQueueCreate(DG_SLOTS, sizeof(int8_t));
  if (dgRing && dgFreeQ) {
    for (int i = 0; i < DG_SLOTS; i++) {
      int8_t slot = i;
      xQueueSend(dgFreeQ, &slot, 0);
    }
    // Core 0 next to the WiFi stack, above netTask: a datagram should leave the lwIP
    // mailbox (which holds only a handful) as soon as it arrives.
    xTaskCreatePinnedToCore(udpRxTask, "udp", 4096, nullptr, 5, nullptr, 0);
  } else {
    Serial.println("[udp] brak RAM na bufory datagramow - zostaje TCP");
  }
#endif
}

void loop() {
  // Three real jobs live in their own pinned tasks; this one — the settings
  // web server — runs here on the default Arduino task instead, since it is
  // occasional and latency-insensitive compared to netTask/drawTask and
  // doesn't earn a dedicated core.
  sampleCpuLoad();
#if !CYD_NO_WEB
  portal.handleClient();
#endif
  delay(20);
}
