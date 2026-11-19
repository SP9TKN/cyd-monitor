// State shared between the network task and the drawing task.
#pragma once
#include <LovyanGFX.hpp>
#include "lgfx_cyd.h"
#include "protocol.h"
#include "settings.h"

// The panel is owned exclusively by the drawing task — see the ownership
// note wherever splash()/tft is touched from elsewhere. This is why there is
// no panel mutex anywhere in this firmware: there is only ever one writer.
extern LGFX_CYD tft;

struct LinkStats {
  volatile uint32_t framesDrawn;
  volatile uint32_t bytesIn;
  volatile uint16_t fps10;      // frames per second x10, smoothed
  volatile uint16_t kbps;
  volatile uint16_t blitMs;     // time the last frame spent being pushed
  volatile int8_t   rssi;
  volatile bool     connected;
};
extern LinkStats g_stats;

// One radio on the scanner page, as MSG_SCAN delivers it.
struct ScanRadio {
  uint8_t state;
  int16_t dbm;
  char    name[SCAN_NAME_BYTES + 1];
  char    freq[SCAN_FREQ_BYTES + 1];
};

// Send an option change upstream. Device-owned options (brightness) are
// applied locally first and mirrored so the server can log them; server-owned
// options (contrast, gamma, scaling, resolution) only take effect when the
// server answers with MSG_OPTACK, so the menu never shows a value that isn't
// real.
void linkSendOpt(uint8_t key, int16_t value);

// Ask the server to forget its reference frame, forcing a full repaint.
// Needed after anything has drawn over the streamed image — the connecting
// splash, a restart notice — because the server's delta is computed against
// what it *believes* is on the panel.
void linkRequestFullRefresh();

// The server's resolution table. Index is OPT_SRC_RES; there is no picker
// for it any more (see the note on settingsStateJson's resLabel), so this is
// purely for reporting the value the server confirms, not for offering a
// choice.
struct SrcRes { uint16_t w, h; };
