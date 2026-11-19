// Wire format shared between the Windows capture server (server/cyd-monitor,
// C#) and this firmware. Both sides must be changed together — PROTO_VERSION
// is checked during the handshake and a mismatch aborts the session rather
// than rendering garbage.
//
// Transport is a single plain TCP stream. Scalar integers are little-endian;
// colours are not, for the reason given below. TCP was
// chosen over UDP deliberately: the frame encoding is *differential* (every
// strip is a delta against the last frame the device confirmed), so a silently
// dropped packet doesn't cost one frame, it corrupts the screen permanently
// until the next full refresh. Retransmission is cheaper than periodic
// full-screen resyncs at 30 fps.
//
// Protocol v5 (docs/protocol-v5-udp.pl.md) adds an optional UDP data plane on top
// of that, negotiated over the same TCP session, that gets around the price of
// TCP's retransmission on WiFi (a lost segment freezes the whole picture for a
// retransmission timeout). The wire version stays 4: everything new is an extra
// message that is only ever sent to a peer that said it understands it.
#pragma once
#include <stdint.h>

// Colour byte order: every 16-bit colour on the wire — ENC_SOLID's fill,
// ENC_RLE's run colours, the ENC_PAL* palettes and ENC_RAW's pixels — is
// big-endian RGB565, which is the order the panel's own SPI stream wants.
// The server pays for the swap while it is already assembling bytes, which
// costs it nothing measurable; in exchange the firmware never interprets a
// colour at all. It shuffles opaque 16-bit tokens into a DMA buffer and hands
// that straight to the panel, so a strip goes out with zero per-pixel work.
// Flipping this to little-endian would force either a full-buffer byte swap
// or a per-pixel conversion on the ESP32 for every frame.

#define PROTO_VERSION 4
#define PROTO_PORT    7788

// Screen geometry of the ESP32-2432S028R panel in landscape.
#define CYD_W 320
#define CYD_H 240

// Change detection granularity. 16x16 (20x15 = 300 tiles) is a deliberate
// trade: 32x32 halves the per-tile bookkeeping but a single blinking text
// caret or a clock digit then dirties 4x the pixels, and on this link bytes
// are far scarcer than CPU cycles on either side.
#define TILE_W 16
#define TILE_H 16
#define TILES_X (CYD_W / TILE_W)
#define TILES_Y (CYD_H / TILE_H)

// Dirty tiles are coalesced horizontally into strips before encoding, so the
// widest possible strip is a full screen row of tiles. That bound sizes the
// firmware's staging buffers.
#define MAX_STRIP_PX    (CYD_W * TILE_H)
#define MAX_STRIP_BYTES (MAX_STRIP_PX * 2)

// ---------------------------------------------------------------- device -> server
// u8 proto, u16 w, u16 h, u32 heapFree, u8 resetReason. The last byte is
// esp_reset_reason_t (esp_system.h) from *this* boot — sent on literally
// every connection, cold boot or reconnect alike, so a panic/watchdog/
// brownout reset shows up in the server's log the very next time the panel
// talks to it, with no serial monitor needed and no dependency on the
// crash itself having been graceful enough to report anything.
#define MSG_HELLO 0x01
#define MSG_ACK   0x02  // u32 seq, u16 blitMs, u16 fps10
#define MSG_TOUCH 0x03  // u8 phase, u16 x, u16 y, u16 pressure
#define MSG_OPT   0x04  // u8 key, i16 value   (OSD changed a setting)
#define MSG_PING  0x05
// Sent right after MSG_HELLO on a reconnect that followed measurable WiFi
// downtime, so the one machine that can see *why* the station left the AP
// (the ESP32's own event callback) gets that reason into the server's log
// automatically, instead of needing a USB serial monitor tethered to a
// device that normally has nothing plugged into it. wifi_err_reason_t codes
// (esp_wifi_types.h) travel as a raw byte; the server prints them.
#define MSG_DIAG  0x06  // u8 wifiReason, u32 downtimeMs
// u16 udpPort. Sent only in answer to a MSG_CONFIG whose flags carry
// CONFIG_FLAG_UDP: a v4 server closes the session on a type it does not know.
#define MSG_UDPHELLO 0x07
// u8 cmd (SCAN_CMD_*). A button on the scanner page. Only ever sent while that page
// is open, which an older server can never have asked for.
#define MSG_SCANCMD 0x08

// ---------------------------------------------------------------- server -> device
#define MSG_CONFIG 0x81  // u16 srcW, u16 srcH, u8 tileW, u8 tileH, u8 flags
#define MSG_FRAME  0x82  // u32 seq, u16 stripCount, u32 payloadBytes, strips[]
#define MSG_OPTACK 0x83  // u8 key, i16 value
#define MSG_TEXT   0x84  // u16 len, utf8[len]  (status line rendered by the OSD)
// The DQT and DHT segments (markers included) that every ENC_JPEG strip in
// this session was coded with. Sent before the first such strip and again
// whenever they change; the device keeps the latest copy and rebuilds a full
// JPEG header in front of each scan, so no strip pays ~590 bytes for it.
#define MSG_JPEGTABLES 0x85  // u16 len, bytes[len]
// u8 mode (0 = pixels over TCP, 1 = pixels over UDP), u16 session, u16
// serverUdpPort. Sent only to a device that sent MSG_UDPHELLO. TCP is ordered,
// so every MSG_FRAME the server wrote before it has been read by the time the
// device sees this.
#define MSG_TRANSPORT 0x86
// u8 flags (SCAN_FLAG_*), then SCAN_RADIOS records of SCAN_REC_BYTES: what the scanner
// page shows. Sent only between OPT_SCANNER 1 and OPT_SCANNER 0, at least once a
// second, which is also how the page tells a dead server from a quiet band.
#define MSG_SCAN 0x87
// u8 open. Somebody is transmitting (1), or stopped (0): open the scanner page by
// itself over the mirror, and close it again if it was this that opened it.
#define MSG_SCANPOP 0x88

// MSG_CONFIG flags, bit 0: the server can carry pixels over UDP.
#define CONFIG_FLAG_UDP 0x01

// One radio on the scanner page: u8 state, i16 dBm, name, frequency. Both strings
// are ASCII, NUL padded, and formatted by the server so the firmware only draws.
#define SCAN_RADIOS     2
#define SCAN_NAME_BYTES 32
#define SCAN_FREQ_BYTES 16
#define SCAN_REC_BYTES  (1 + 2 + SCAN_NAME_BYTES + SCAN_FREQ_BYTES)
#define SCAN_OFFLINE 0   // not sweeping and nobody on the air; name says why
#define SCAN_RUNNING 1   // sweeping; the page animates a bar under the name
#define SCAN_HOLD    2   // stopped on a channel with a transmission on it

#define SCAN_FLAG_MUTED      0x01   // the sound card Oxide plays through is muted
#define SCAN_FLAG_RADIO0_OFF 0x02   // Oxide's own mute on radio 0; the next bit is radio 1

// START, STOP and NEXT act on both radios; NEXT only moves one that has stopped.
#define SCAN_CMD_START 1
#define SCAN_CMD_STOP  2
#define SCAN_CMD_NEXT  3
#define SCAN_CMD_MUTE  4
#define SCAN_CMD_SOUND 5   // plus the radio: turn that radio's own sound on or off

// ---------------------------------------------------------------- UDP data plane
// The device listens for DATA on UDP_DEVICE_PORT and sends ACK from that same
// port to the server's port, so a firewall sees ACK as the reply to DATA.
#define UDP_DEVICE_PORT (PROTO_PORT + 2)

#define DG_DATA 0xD1            // server -> device
#define DG_ACK  0xD2            // device -> server
#define DG_FLAG_REPAINT   0x01  // part of a full repaint (informational)
#define DG_FLAG_FRAME_END 0x02  // last datagram of an encoded frame: close the write, count a frame
// DATA: u8 type, u8 flags, u16 session, u32 seq, u8 rectCount, rects[]
//   rect: u8 tx, u8 ty, u8 tw, u8 mode, u16 len, payload[len]
//   One tile row high (16 px), tw tiles wide, payload as ENC_* for tw*16 x 16 px.
//   Payload bytes are the same as a v4 strip's; only the 13 byte strip header
//   became these 6. A datagram with rectCount 0 is a keepalive / tail probe.
// ACK:  u8 type, u8 flags, u16 session, u32 highest, u32 bitmap (bit k = seq
//   highest-1-k received), u16 fps10, u16 blitMs, u8 freeSlots, u8 rxDrops.
// A rectangle is *absolute* pixels, never a difference, so a lost datagram is
// never replayed: the server marks the tiles it carried dirty and sends their
// current content in some later datagram. The device therefore keeps, per tile,
// the seq of the datagram it last drew from and ignores anything older.
#define DG_HDR_BYTES      9
#define DG_RECT_HDR_BYTES 6
#define DG_MAX_BYTES      1400
#define DG_ACK_BYTES      18
// Datagrams the device holds between its receive task and its drawing task.
#define DG_SLOTS          16

// Touch phases.
#define TOUCH_DOWN 0
#define TOUCH_MOVE 1
#define TOUCH_UP   2

// Option keys. Values the device owns are applied locally *and* mirrored to
// the server so it can persist them; values the server owns (anything that
// changes how pixels are produced) round-trip through MSG_OPTACK so the OSD
// only ever displays state that actually took effect.
#define OPT_BRIGHTNESS 1  // 5..255   device-side: backlight PWM duty
#define OPT_CONTRAST   2  // -64..64  server-side: LUT applied before quantise
#define OPT_GAMMA      3  // 50..300  server-side: LUT exponent, x100
#define OPT_SCALE_MODE 4  // 0=fit (letterbox), 1=stretch, 2=1:1 pan
#define OPT_SRC_RES    5  // index into RES_TABLE, server calls ChangeDisplaySettings
#define OPT_DITHER     6  // 0=off, 1=ordered 4x4 Bayer
#define OPT_PAUSE      7  // 1 to hold the stream while something else draws on the panel directly
#define OPT_REFRESH    8  // write-only: invalidate the server's reference frame
#define OPT_STATS      9  // 0=off, 1=overlay fps/kbps in the corner
#define OPT_SHARPEN   10  // 0..100  server-side: unsharp-mask amount, see FrameProcessor.cs
#define OPT_SCANNER   11  // 1 = scanner page open: hold the stream and start sending MSG_SCAN

// Per-strip payload encodings. The encoder tries every applicable mode and
// emits the smallest; the decoder must implement all of them.
#define ENC_SOLID 0  // u16 colour                          — flat fill
#define ENC_RAW   1  // u16 px[w*h]                          — incompressible
#define ENC_RLE   2  // (u8 count, u16 colour)*              — runs
#define ENC_PAL1  3  // u16 pal[2], then 1bpp MSB-first      — text/mono edges
#define ENC_PAL4  4  // u8 n, u16 pal[n<=16], then 4bpp      — flat UI chrome
#define ENC_JPEG  5  // baseline 4:2:0 entropy-coded scan only — video, photos

// Room an ENC_JPEG strip needs in front of its scan for the rebuilt header
// (SOI + tables + SOF0 + SOS, ~590 bytes) and after it for EOI, inside a
// staging buffer of MAX_STRIP_BYTES. The encoder never sends a longer scan.
#define JPEG_HDR_RESERVE 640
#define JPEG_MAX_SCAN    (MAX_STRIP_BYTES - JPEG_HDR_RESERVE - 2)

// Strip header, as it appears on the wire ahead of each payload.
// u16 x, u16 y, u16 w, u16 h, u8 mode, u32 len  =  2+2+2+2+1+4
#define STRIP_HDR_BYTES 13
