// Build-time tunables. Everything a user is expected to change during normal
// operation lives in the settings web page instead (see startSettingsWeb in
// the .ino) and is persisted to NVS; this file is for things that must be
// fixed at compile time — including, now that there is no on-panel menu left
// to reach it from, the panel rotation below.
#pragma once

// Set to 1 for late "2432S028Rv3" boards, which fit an ST7789 instead of the
// ILI9341. Symptom of getting this wrong: inverted colours, or a picture
// offset by a few pixels. Selected by the arduino-cli build script via
// -DCYD_PANEL_ST7789=1, so don't hard-code it here.
#ifndef CYD_PANEL_ST7789
#define CYD_PANEL_ST7789 0
#endif

// Panel SPI write clock. 80 MHz is required to fit a worst-case full-screen
// repaint inside a 30 fps budget; drop to 65 or 55 MHz if the panel shows
// sparkle or torn pixels.
#ifndef CYD_SPI_HZ
#define CYD_SPI_HZ 80000000
#endif

// Forces the panel rotation register to a specific value at boot, overriding
// whatever is stored in NVS, and persists it. -1 (the default) leaves the
// stored setting alone.
//
// This is now the *only* way to change it: the on-panel menu that used to
// offer an "Obrot" item was removed once every other setting it carried
// moved to the settings web page, and rotation has no such remote path (a
// wrong rotation could make the panel unreadable, so fixing it needs a
// build-time flag and a reflash rather than a touch gesture that might not
// even land on-screen right now). Valid values are the four landscape ones:
// 1, 3, 5, 7 (see the table on Settings::rotation); 5 and 1 differ by a
// horizontal mirror, as do 7 and 3.
#ifndef CYD_FORCE_ROTATION
#define CYD_FORCE_ROTATION -1
#endif


// How many strip staging buffers to keep in flight between the network task
// and the drawing task. Two is the minimum for DMA double-buffering; four
// lets the network task run ahead while the panel is busy, which is what
// actually absorbs WiFi jitter. Each costs MAX_STRIP_BYTES (10 KB).
#define CYD_STRIP_BUFFERS 4

// Serial console baud. Only used for diagnostics — the picture never goes
// over the wire, see README.md for why.
#define CYD_SERIAL_BAUD 115200

// Diagnostics: 1 leaves the settings web server out entirely (no page, no /api/state) to
// see whether it disturbs the stream. The first-run provisioning portal is a separate
// thing and stays. Selected by build-firmware.ps1 -NoWeb.
#ifndef CYD_NO_WEB
#define CYD_NO_WEB 0
#endif

// Leaves the protocol-v5 UDP data plane out entirely: the image then behaves exactly
// like a v4 firmware (never sends MSG_UDPHELLO, allocates no datagram ring). For A/B
// measurements and as a known-good fallback. Selected by build-firmware.ps1 -NoUdp.
#ifndef CYD_NO_UDP
#define CYD_NO_UDP 0
#endif
