// Persisted user settings. These survive reboots in NVS so the monitor comes
// back up looking exactly as it was left — a display that forgets its own
// brightness every power cut is not a monitor.
#pragma once
#include <Preferences.h>
#include "protocol.h"

struct Settings {
  uint8_t  brightness = 160;  // backlight PWM duty, applied locally
  int16_t  contrast   = 0;    // server-side LUT, -64..64
  int16_t  gamma      = 100;  // server-side LUT exponent x100
  uint8_t  sharpen    = 0;    // server-side unsharp-mask amount, 0..100
  uint8_t  scaleMode  = 0;    // 0 fit, 1 stretch, 2 one-to-one pan
  uint8_t  srcRes     = 1;    // index into the server's resolution table
  uint8_t  dither     = 0;   // see the note on Options.Dither in the server
  uint8_t  stats      = 0;
  uint8_t  scanFlip   = 1;    // scanner page touch Y upside down (the first panel needed it)
  // LovyanGFX rotation register (0-7, not just 0-3 — bits 0-1 pick the
  // quarter-turn, bit 2 is a flip flag). Panel_LCD::getMadCtl()'s table shows
  // exactly four values keep the panel in landscape: 1 (MV|MX, mirrored),
  // 3 (MV|MY, mirrored the other axis), 5 (MV only, no mirror) and 7
  // (MV|MX|MY, both — a plain 180deg turn from 5). Which one is right side up
  // and unmirrored depends on how a given board's ribbon cable was soldered
  // and varies unit to unit, which is why this is a persisted setting rather
  // than a constant — and, now that the on-panel menu that used to offer
  // this as an "Obrot" item is gone, only reachable through
  // CYD_FORCE_ROTATION (config.h) and a reflash. Touch does not need a
  // matching independent setting: LovyanGFX's Panel_Device::convertRawXY()
  // derives the touch transform from this same rotation value, so a display
  // fix here re-aligns touch automatically.
  uint8_t  rotation   = 1;

  // Touch calibration. Stored as the four raw ADC bounds LovyanGFX wants
  // rather than an affine matrix, so a bad calibration can be reasoned
  // about by looking at four numbers. There is no calibration UI any more
  // (it lived in the removed on-panel menu, alongside "Obrot"), so these
  // just persist whatever a unit was calibrated to before that menu existed
  // — the lgfx_cyd.h defaults if it never was.
  uint16_t txMin = 300, txMax = 3900, tyMin = 220, tyMax = 3800;
  bool     calibrated = false;

  char ssid[33] = {0};
  char pass[65] = {0};
  char host[41] = {0};  // empty means "find the server over UDP broadcast"
};

extern Settings g_set;

inline void settingsLoad() {
  Preferences p;
  if (!p.begin("cydmon", true)) return;
  g_set.brightness = p.getUChar("bri", g_set.brightness);
  g_set.contrast   = p.getShort("con", g_set.contrast);
  g_set.gamma      = p.getShort("gam", g_set.gamma);
  g_set.sharpen    = p.getUChar("shp", g_set.sharpen);
  g_set.scaleMode  = p.getUChar("scl", g_set.scaleMode);
  g_set.srcRes     = p.getUChar("res", g_set.srcRes);
  g_set.dither     = p.getUChar("dit", g_set.dither);
  g_set.stats      = p.getUChar("sta", g_set.stats);
  g_set.scanFlip   = p.getUChar("sfy", g_set.scanFlip);
  g_set.rotation   = p.getUChar("rot", g_set.rotation);
  g_set.txMin      = p.getUShort("txn", g_set.txMin);
  g_set.txMax      = p.getUShort("txx", g_set.txMax);
  g_set.tyMin      = p.getUShort("tyn", g_set.tyMin);
  g_set.tyMax      = p.getUShort("tyx", g_set.tyMax);
  g_set.calibrated = p.getBool("cal", false);
  p.getString("ssid", g_set.ssid, sizeof(g_set.ssid));
  p.getString("pass", g_set.pass, sizeof(g_set.pass));
  p.getString("host", g_set.host, sizeof(g_set.host));
  p.end();
}

inline void settingsSave() {
  Preferences p;
  if (!p.begin("cydmon", false)) return;
  p.putUChar("bri", g_set.brightness);
  p.putShort("con", g_set.contrast);
  p.putShort("gam", g_set.gamma);
  p.putUChar("shp", g_set.sharpen);
  p.putUChar("scl", g_set.scaleMode);
  p.putUChar("res", g_set.srcRes);
  p.putUChar("dit", g_set.dither);
  p.putUChar("sta", g_set.stats);
  p.putUChar("sfy", g_set.scanFlip);
  p.putUChar("rot", g_set.rotation);
  p.putUShort("txn", g_set.txMin);
  p.putUShort("txx", g_set.txMax);
  p.putUShort("tyn", g_set.tyMin);
  p.putUShort("tyx", g_set.tyMax);
  p.putBool("cal", g_set.calibrated);
  p.end();
}

inline void settingsSaveWifi(const char* ssid, const char* pass, const char* host) {
  Preferences p;
  if (!p.begin("cydmon", false)) return;
  p.putString("ssid", ssid);
  p.putString("pass", pass);
  p.putString("host", host);
  p.end();
}

// Wipes the WiFi credentials and the manual server address, so the next boot
// falls into the provisioning portal instead of retrying whatever was typed
// in wrong. Exists because a mistyped server address had no other way to be
// corrected: WiFi itself keeps connecting fine, so the normal
// "hold BOOT after a WiFi failure" path into provisioning never triggers, and
// there was previously no menu option that touched these two fields at all.
inline void settingsResetNetwork() {
  Preferences p;
  if (!p.begin("cydmon", false)) return;
  p.putString("ssid", "");
  p.putString("pass", "");
  p.putString("host", "");
  p.end();
}
