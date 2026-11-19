// LovyanGFX device definition for the "Cheap Yellow Display"
// (ESP32-2432S028R): a 2.8" 320x240 panel plus an XPT2046 resistive
// touch controller on one board.
//
// Why LovyanGFX and not TFT_eSPI: on this board the panel and the touch
// controller sit on two *different* SPI buses. The panel is wired to the
// HSPI pins (14/13/12) while the XPT2046 has its own set (25/32/39/33).
// TFT_eSPI's built-in touch support assumes the touch chip shares the
// display's bus, so driving this board with it means bolting on a second
// library and hand-managing bus ownership between them. LovyanGFX takes a
// separate `spi_host` per device, so the split is just configuration —
// and its Bus_SPI exposes DMA pushes (pushImageDMA), which is what makes
// the 30 fps target reachable at all.
#pragma once

#define LGFX_USE_V1
#include <LovyanGFX.hpp>
#include "config.h"

class LGFX_CYD : public lgfx::LGFX_Device {
#if CYD_PANEL_ST7789
  // Late "2432S028Rv3" boards ship an ST7789 in place of the ILI9341.
  lgfx::Panel_ST7789 _panel;
#else
  lgfx::Panel_ILI9341 _panel;
#endif
  lgfx::Bus_SPI       _bus;
  lgfx::Light_PWM     _light;
  lgfx::Touch_XPT2046 _touch;

public:
  LGFX_CYD() {
    {
      auto c = _bus.config();
      // SPI2_HOST/SPI3_HOST rather than the older HSPI_HOST/VSPI_HOST
      // aliases — the latter were dropped in ESP-IDF 5, which the current
      // arduino-esp32 core is built on.
      c.spi_host   = SPI2_HOST;
      c.spi_mode   = 0;
      // The ILI9341 datasheet rates writes at 10 MHz; in practice these
      // panels clock far past that, and we need to, because a full 320x240
      // RGB565 frame is 1.23 Mbit — at 40 MHz that alone is 31 ms and 30 fps
      // is already out of reach before a single byte of WiFi. At 80 MHz the
      // same frame is 15.4 ms. Some units are wired badly enough to show
      // sparkle at 80; CYD_SPI_HZ in config.h is the dial to back off.
      c.freq_write = CYD_SPI_HZ;
      c.freq_read  = 16000000;
      c.spi_3wire  = false;
      c.use_lock   = true;
      c.dma_channel = SPI_DMA_CH_AUTO;  // required for pushImageDMA
      c.pin_sclk   = 14;
      c.pin_mosi   = 13;
      c.pin_miso   = 12;
      c.pin_dc     = 2;
      _bus.config(c);
      _panel.setBus(&_bus);
    }
    {
      auto c = _panel.config();
      c.pin_cs           = 15;
      c.pin_rst         = -1;  // tied to the ESP32's own reset on this board
      c.pin_busy        = -1;
      c.panel_width      = 240;  // physical portrait dimensions; setRotation
      c.panel_height     = 320;  // turns it into the 320x240 we stream
      c.offset_x         = 0;
      c.offset_y         = 0;
      c.offset_rotation  = 0;
      c.dummy_read_pixel = 8;
      c.dummy_read_bits  = 1;
      c.readable         = true;
      // Decoupled from CYD_PANEL_ST7789 on purpose. The panel *controller*
      // class and colour inversion are independent facts about a given
      // module, and this board measured as needing both non-defaults at
      // once: ST7789's addressing (panel_width/height, offsets, GRAM
      // window) is what actually gets the full 320x240 onto the glass —
      // confirmed by A/B: the ILI9341 class left a quarter of the screen
      // permanently unpainted regardless of every other setting tried,
      // switching class alone fixed it — while ST7789's *default* invert=true
      // is wrong for this specific panel (it read as a photo negative
      // against real desktop content). So: use ST7789's addressing, keep
      // inversion off.
      c.invert           = false;
      // RGB, not BGR — in theory. The --test pattern's red field (0xB800 on
      // the wire) came out blue under the ILI9341 class with this at false,
      // which is what this flag is documented to control; flipping it to
      // true and switching to the ST7789 class at the same time still came
      // out blue on real desktop content, which shouldn't happen if this
      // flag were the actual lever for this panel/class combination. Back
      // to false while that gets re-isolated — see the note above about not
      // testing two changed variables in one flash again.
      // additionally inverted the whole image (reported as "looks like a
      // negative" once seen against real desktop content, not just the
      // test pattern's flat fields) — so invert stays tied to the panel
      // variant above, and this is the actual colour fix, independent of it.
      c.rgb_order        = false;
      c.dlen_16bit       = false;
      // Nothing else lives on SPI2 — the SD slot is on SPI3 — so LovyanGFX
      // can hold the bus across a whole frame instead of re-acquiring it per
      // strip. Setting this true costs measurable throughput for no reason.
      c.bus_shared       = false;
      _panel.config(c);
    }
    {
      auto c = _light.config();
      c.pin_bl      = 21;
      c.invert      = false;
      // Above ~20 kHz the backlight is silent and invisible to a camera;
      // 44.1 kHz also keeps it clear of the audio band, since GPIO26 on this
      // board drives a speaker amp that picks up switching noise.
      c.freq        = 44100;
      c.pwm_channel = 7;
      _light.config(c);
      _panel.setLight(&_light);
    }
    {
      auto c = _touch.config();
      // Raw ADC bounds, replaced at runtime by the stored calibration if the
      // user has run one. These defaults are close enough that the
      // calibration UI itself is reliably tappable on an uncalibrated unit.
      c.x_min      = 300;
      c.x_max      = 3900;
      c.y_min      = 220;
      c.y_max      = 3800;
      c.pin_int    = 36;   // input-only pin, fine for an IRQ
      c.bus_shared = false;
      c.offset_rotation = 0;
      c.spi_host   = SPI3_HOST;  // the whole point: its own bus
      c.freq       = 1000000;
      c.pin_sclk   = 25;
      c.pin_mosi   = 32;
      c.pin_miso   = 39;  // input-only pin, fine for MISO
      c.pin_cs     = 33;
      _touch.config(c);
      _panel.setTouch(&_touch);
    }
    setPanel(&_panel);
  }
};
