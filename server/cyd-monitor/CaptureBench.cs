using System.Diagnostics;


namespace CydMonitor;

/// <summary>
/// Times the candidate ways of getting a scaled copy of the desktop, because
/// the first honest measurement of this pipeline showed capture — not WiFi, not
/// encoding — eating the entire 33 ms frame budget on its own.
///
/// The variables are which stretch mode GDI uses and whether the source of the
/// stretch is the screen DC or a plain memory copy of it. Reading from a screen
/// DC crosses into the display driver on every sampled pixel, so a filter that
/// reads each source pixel once (HALFTONE) can cost wildly more there than the
/// same filter reading from ordinary RAM.
/// </summary>
internal static class CaptureBench
{
    private const int COLORONCOLOR = 3;

    public static unsafe int Run(int iterations)
    {
        var target = Monitors.Enumerate().FirstOrDefault(m => m.Primary);
        if (target is null) { Console.Error.WriteLine("brak monitora"); return 1; }
        var mon = target.Bounds;
        int sw = mon.Width, sh = mon.Height;

        Console.WriteLine($"zrodlo: {sw}x{sh} -> {Proto.W}x{Proto.H}, {iterations} powtorzen\n");

        IntPtr screen = Native.GetDC(IntPtr.Zero);
        var small = new Dib(screen, Proto.W, Proto.H);
        var full  = new Dib(screen, sw, sh);

        try
        {
            // Does the cost scale with pixel count, or is it a fixed charge per
            // read from the screen DC? If a 1x1 grab costs the same as a full
            // 1920x1080 one then no scaling strategy can help, because GDI
            // screen reads are synchronised to something — and the only route
            // to 30 fps is a different capture API altogether.
            Time("BitBlt ekran->320x240 (wycinek, bez skalowania)", iterations, () =>
            {
                Native.BitBlt(small.Dc, 0, 0, Proto.W, Proto.H,
                              screen, mon.Left, mon.Top, Native.SRCCOPY);
            });

            Time("BitBlt ekran->1 piksel", iterations, () =>
            {
                Native.BitBlt(small.Dc, 0, 0, 1, 1, screen, mon.Left, mon.Top, Native.SRCCOPY);
            });

            Time("StretchBlt ekran->320x240, HALFTONE", iterations, () =>
            {
                Native.SetStretchBltMode(small.Dc, Native.HALFTONE);
                Native.SetBrushOrgEx(small.Dc, 0, 0, IntPtr.Zero);
                Native.StretchBlt(small.Dc, 0, 0, Proto.W, Proto.H,
                                  screen, mon.Left, mon.Top, sw, sh, Native.SRCCOPY);
            });

            Time("StretchBlt ekran->320x240, COLORONCOLOR", iterations, () =>
            {
                Native.SetStretchBltMode(small.Dc, COLORONCOLOR);
                Native.StretchBlt(small.Dc, 0, 0, Proto.W, Proto.H,
                                  screen, mon.Left, mon.Top, sw, sh, Native.SRCCOPY);
            });

            Time("BitBlt ekran->pamiec (pelna rozdzielczosc)", iterations, () =>
            {
                Native.BitBlt(full.Dc, 0, 0, sw, sh, screen, mon.Left, mon.Top, Native.SRCCOPY);
            });

            Time("  + StretchBlt pamiec->320x240, HALFTONE", iterations, () =>
            {
                Native.BitBlt(full.Dc, 0, 0, sw, sh, screen, mon.Left, mon.Top, Native.SRCCOPY);
                Native.SetStretchBltMode(small.Dc, Native.HALFTONE);
                Native.SetBrushOrgEx(small.Dc, 0, 0, IntPtr.Zero);
                Native.StretchBlt(small.Dc, 0, 0, Proto.W, Proto.H,
                                  full.Dc, 0, 0, sw, sh, Native.SRCCOPY);
            });

            Time("  + StretchBlt pamiec->320x240, COLORONCOLOR", iterations, () =>
            {
                Native.BitBlt(full.Dc, 0, 0, sw, sh, screen, mon.Left, mon.Top, Native.SRCCOPY);
                Native.SetStretchBltMode(small.Dc, COLORONCOLOR);
                Native.StretchBlt(small.Dc, 0, 0, Proto.W, Proto.H,
                                  full.Dc, 0, 0, sw, sh, Native.SRCCOPY);
            });

            Time("  + wlasny filtr skrzynkowy w C#", iterations, () =>
            {
                Native.BitBlt(full.Dc, 0, 0, sw, sh, screen, mon.Left, mon.Top, Native.SRCCOPY);
                BoxDownscale(full.Bits, sw, sh, small.Bits, Proto.W, Proto.H);
            });

            // Screen reads are vsync-locked, so what matters is not throughput
            // but whether a read finishes inside a single 16.7 ms refresh
            // interval or spills into the next one. That threshold depends on
            // how many pixels the source monitor has, which is exactly the
            // dial the device's menu turns — so find where it sits.
            Console.WriteLine("\nkoszt odczytu zaleznie od rozdzielczosci zrodla:");
            foreach (var (rw, rh) in Proto.ResTable.Concat([(1920, 1080)]))
            {
                if (rw > sw || rh > sh) continue;
                Time($"  zrodlo {rw}x{rh} -> 320x240 (HALFTONE)", iterations, () =>
                {
                    Native.SetStretchBltMode(small.Dc, Native.HALFTONE);
                    Native.SetBrushOrgEx(small.Dc, 0, 0, IntPtr.Zero);
                    Native.StretchBlt(small.Dc, 0, 0, Proto.W, Proto.H,
                                      screen, mon.Left, mon.Top, rw, rh, Native.SRCCOPY);
                });
            }

            Console.WriteLine($"\nbudzet na klatke przy 30 fps: 33.3 ms " +
                              $"(kodowanie + konwersja zabieraja ~1.4 ms)");
            Console.WriteLine("okres odswiezania ekranu zrodlowego: 16.7 ms przy 60 Hz");
        }
        finally
        {
            small.Dispose();
            full.Dispose();
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
        return 0;
    }

    private static void Time(string label, int n, Action body)
    {
        body();   // warm up: first call pays for driver-side setup
        var sw = Stopwatch.StartNew();
        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            double t0 = sw.Elapsed.TotalMilliseconds;
            body();
            double dt = sw.Elapsed.TotalMilliseconds - t0;
            if (dt > worst) worst = dt;
        }
        double avg = sw.Elapsed.TotalMilliseconds / n;
        Console.WriteLine($"{label,-48} {avg,7:F2} ms sr.  {worst,7:F2} ms max  " +
                          $"{(avg > 0 ? 1000 / avg : 0),6:F0} fps");
    }

    /// <summary>
    /// Area-average downscale done here rather than by GDI. Each destination
    /// pixel averages the whole source block that maps onto it, which is the
    /// same filter HALFTONE is meant to apply — the question the benchmark
    /// answers is whether doing it over ordinary RAM beats asking the display
    /// driver to do it.
    /// </summary>
    internal static unsafe void BoxDownscale(uint* src, int sw, int sh, uint* dst, int dw, int dh)
    {
        for (int dy = 0; dy < dh; dy++)
        {
            int y0 = dy * sh / dh;
            int y1 = Math.Max(y0 + 1, (dy + 1) * sh / dh);
            uint* dRow = dst + dy * dw;

            for (int dx = 0; dx < dw; dx++)
            {
                int x0 = dx * sw / dw;
                int x1 = Math.Max(x0 + 1, (dx + 1) * sw / dw);

                uint rs = 0, gs = 0, bs = 0, n = 0;
                for (int y = y0; y < y1; y++)
                {
                    uint* sRow = src + y * sw;
                    for (int x = x0; x < x1; x++)
                    {
                        uint p = sRow[x];
                        bs += p & 0xFF;
                        gs += (p >> 8) & 0xFF;
                        rs += (p >> 16) & 0xFF;
                        n++;
                    }
                }
                if (n == 0) n = 1;
                dRow[dx] = 0xFF000000u | ((rs / n) << 16) | ((gs / n) << 8) | (bs / n);
            }
        }
    }

}
