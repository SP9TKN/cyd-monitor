using System.Buffers.Binary;
using System.Diagnostics;

namespace CydMonitor;

/// <summary>
/// Verifies the wire format against a reference decoder and measures what the
/// link will actually have to carry.
///
/// Both halves matter. The codec is lossless by construction, so any
/// discrepancy between what the encoder emits and what the device's decoder
/// reconstructs is a bug that shows up on hardware as corrupted rectangles —
/// the single most tedious class of fault to chase with a flash cycle in the
/// loop. And the frame-rate target is a claim about bytes per frame, which
/// depends entirely on what is on the user's screen; measuring it on the real
/// desktop is the only honest way to state it.
///
/// <see cref="Decode"/> is a deliberate line-by-line twin of decodeStrip() in
/// the firmware. It is not shared code and cannot be — the point is to catch a
/// divergence between two independent implementations of the same spec.
/// </summary>
internal static class SelfTest
{
    public static unsafe int Run(Options opt, int frames)
    {
        var target = Monitors.Enumerate().FirstOrDefault(m => m.Primary)
                  ?? Monitors.Enumerate().FirstOrDefault();
        if (target is null) { Console.Error.WriteLine("brak monitora"); return 1; }

        Console.WriteLine($"zrodlo   : {target}");
        Console.WriteLine($"skalowanie: {opt.Scale}, kontrast {opt.Contrast}, gamma {opt.Gamma}, " +
                          $"dithering {(opt.Dither ? "wl." : "wyl.")}");
        Console.WriteLine();

        using var src = FrameSourceFactory.Create(target.Device, opt.ForceGdi);
        src.CursorSize = opt.CursorSize;
        Console.WriteLine($"przechwytywanie: {src.Kind}");
        var proc = new FrameProcessor
        {
            Contrast = opt.Contrast, Gamma = opt.Gamma, Dither = opt.Dither,
        };
        var enc = new TileEncoder();

        var cur   = new ushort[Proto.W * Proto.H];
        var prev   = new ushort[Proto.W * Proto.H];
        var shadow = new ushort[Proto.W * Proto.H];   // what the device would hold

        long[] modeBytes = new long[5];
        long[] modeCount = new long[5];
        long totalBytes = 0, totalStrips = 0;
        double capMs = 0, procMs = 0, encMs = 0, worstFrameMs = 0, waitMs = 0;
        long fullBytes = 0;
        int mismatches = 0;
        var sw = new Stopwatch();

        // Desktop Duplication only produces a frame when something actually
        // changed, so a benchmark on an idle desktop would measure nothing.
        // Waiting a little, and repainting this progress line each pass, gives
        // the compositor real work to hand over — scrolling text is also a fair
        // stand-in for the kind of churn a terminal or log window produces.
        src.AcquireTimeoutMs = 200;
        int captured = 0, misses = 0;

        for (int f = 0; captured < frames && f < frames * 6; f++)
        {
            Console.Write($"\rpomiar: klatka {captured + 1}/{frames}   ");

            sw.Restart();
            if (!src.Capture(target.Bounds, opt.Scale)) { misses++; continue; }
            bool full = captured == 0;   // first pass is the worst case: everything dirty
            captured++;
            double tCap = sw.Elapsed.TotalMilliseconds;
            waitMs += src.LastWaitMs;

            proc.Process(src.Pixels, cur);
            double tProc = sw.Elapsed.TotalMilliseconds;

            enc.Encode(cur, prev, full, proc.SoftRgb);
            double tEnc = sw.Elapsed.TotalMilliseconds;

            capMs  += src.LastWorkMs;
            procMs += tProc - tCap;
            double frameWork = src.LastWorkMs + (tEnc - tCap);
            encMs  += tEnc - tProc;
            if (frameWork > worstFrameMs) worstFrameMs = frameWork;

            totalBytes  += enc.PayloadLength;
            totalStrips += enc.StripCount;
            if (full) fullBytes = enc.PayloadLength;

            // Replay the frame exactly as the firmware would, into a shadow
            // buffer that starts life matching what the device has already been
            // sent, and check the result pixel for pixel.
            mismatches += Replay(enc.Payload, enc.StripCount, shadow, modeBytes, modeCount);
            for (int i = 0; i < cur.Length; i++)
            {
                if (shadow[i] != cur[i]) { mismatches++; break; }
            }

        }

        Console.WriteLine();
        int n = Math.Max(1, captured);
        Console.WriteLine($"klatek zmierzonych: {captured}  (przebiegow bez nowej klatki: {misses})");
        Console.WriteLine($"niezgodnosci   : {mismatches}  {(mismatches == 0 ? "(kodek OK)" : "*** BLAD ***")}");
        Console.WriteLine();
        Console.WriteLine($"pelne odswiezenie (najgorszy przypadek):");
        Console.WriteLine($"  raw RGB565   : {Proto.W * Proto.H * 2 / 1024.0,8:F1} kB");
        Console.WriteLine($"  po kodowaniu : {fullBytes / 1024.0,8:F1} kB   " +
                          $"({(double)fullBytes / (Proto.W * Proto.H * 2) * 100:F1}% oryginalu)");
        Console.WriteLine();
        Console.WriteLine($"czasy serwera na klatke (srednio):");
        Console.WriteLine($"  przechwycenie+skalowanie : {capMs / n,6:F2} ms  (praca GPU+odczyt)");
        Console.WriteLine($"  czekanie na nowa klatke  : {waitMs / n,6:F2} ms  (pulpit bezczynny, nie liczy sie do budzetu)");
        Console.WriteLine($"  kontrast/gamma/dithering : {procMs / n,6:F2} ms");
        Console.WriteLine($"  kodowanie roznicowe      : {encMs / n,6:F2} ms");
        Console.WriteLine($"  najgorsza klatka         : {worstFrameMs,6:F2} ms  " +
                          $"(budzet dla 30 fps: 33.3 ms)");
        Console.WriteLine();
        Console.WriteLine($"ruch w sieci:");
        Console.WriteLine($"  srednio      : {totalBytes / (double)n / 1024,8:F1} kB/klatke, " +
                          $"{totalStrips / (double)n,5:F1} paskow");
        Console.WriteLine($"  przy 30 fps  : {totalBytes / (double)n * 30 / 1024,8:F0} kB/s " +
                          $"= {totalBytes / (double)n * 30 * 8 / 1e6,5:F1} Mbit/s");
        Console.WriteLine();
        Console.WriteLine("wybor trybu kodowania (udzial w bajtach):");
        string[] names = ["SOLID", "RAW", "RLE", "PAL1", "PAL4"];
        for (int m = 0; m < 5; m++)
        {
            if (modeCount[m] == 0) continue;
            Console.WriteLine($"  {names[m],-6} {modeCount[m],6} paskow  {modeBytes[m] / 1024.0,8:F1} kB  " +
                              $"{(totalBytes > 0 ? modeBytes[m] * 100.0 / totalBytes : 0),5:F1}%");
        }
        Console.WriteLine();

        // Translate measured bytes into the number that was actually promised.
        double avg = totalBytes / (double)n;
        Console.WriteLine("pulap klatek wynikajacy z lacza (samo przeslanie danych):");
        foreach (double mbit in new[] { 6.0, 12.0, 20.0 })
        {
            double fpsAvg  = avg > 0 ? mbit * 1e6 / 8 / avg : 999;
            double fpsFull = fullBytes > 0 ? mbit * 1e6 / 8 / fullBytes : 999;
            Console.WriteLine($"  {mbit,4:F0} Mbit/s : {Math.Min(fpsAvg, 999),5:F0} fps typowo, " +
                              $"{Math.Min(fpsFull, 999),5:F0} fps przy pelnym odswiezeniu");
        }

        return mismatches == 0 ? 0 : 1;
    }

    private static int Replay(ReadOnlySpan<byte> payload, int stripCount, ushort[] screen,
                              long[] modeBytes, long[] modeCount)
    {
        var tile = new ushort[Proto.W * Proto.TileH];
        int p = 0;
        int errors = 0;

        for (int s = 0; s < stripCount; s++)
        {
            if (p + Proto.StripHeaderBytes > payload.Length) { errors++; break; }
            int x = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(p, 2));
            int y = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(p + 2, 2));
            int w = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(p + 4, 2));
            int h = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(p + 6, 2));
            byte mode = payload[p + 8];
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(p + 9, 4));
            p += Proto.StripHeaderBytes;

            if (mode > 4 || p + len > payload.Length || w * h > tile.Length ||
                x + w > Proto.W || y + h > Proto.H)
            {
                errors++;
                break;
            }

            // The firmware's own bounds checks, mirrored: a strip that fails
            // them there drops the link, so one that fails here is a bug the
            // hardware would turn into a disconnect loop.
            Decode(mode, payload.Slice(p, len), tile, w * h);
            modeBytes[mode] += len + Proto.StripHeaderBytes;
            modeCount[mode]++;
            p += len;

            for (int row = 0; row < h; row++)
                Array.Copy(tile, row * w, screen, (y + row) * Proto.W + x, w);
        }
        return errors;
    }

    /// <summary>
    /// Independent reimplementation of the firmware's decodeStrip(). Kept
    /// separate on purpose — sharing it with the encoder would make this test
    /// agree with itself no matter what the spec says.
    /// </summary>
    internal static void Decode(byte mode, ReadOnlySpan<byte> src, ushort[] outp, int px)
    {
        switch (mode)
        {
            case Proto.EncSolid:
            {
                ushort c = BinaryPrimitives.ReadUInt16LittleEndian(src);
                for (int i = 0; i < px; i++) outp[i] = c;
                break;
            }
            case Proto.EncRaw:
            {
                for (int i = 0; i < px; i++)
                    outp[i] = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 2, 2));
                break;
            }
            case Proto.EncRle:
            {
                int i = 0, o = 0;
                while (i + 3 <= src.Length && o < px)
                {
                    int n = src[i];
                    ushort c = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i + 1, 2));
                    i += 3;
                    if (n > px - o) n = px - o;
                    while (n-- > 0) outp[o++] = c;
                }
                while (o < px) outp[o++] = 0;
                break;
            }
            case Proto.EncPal1:
            {
                ushort p0 = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(0, 2));
                ushort p1 = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(2, 2));
                var bits = src.Slice(4);
                for (int i = 0; i < px; i++)
                    outp[i] = ((bits[i >> 3] >> (7 - (i & 7))) & 1) != 0 ? p1 : p0;
                break;
            }
            case Proto.EncPal4:
            {
                int n = src[0];
                var pal = new ushort[16];
                for (int k = 0; k < n; k++)
                    pal[k] = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(1 + k * 2, 2));
                var nib = src.Slice(1 + n * 2);
                for (int i = 0; i < px; i++)
                {
                    byte b = nib[i >> 1];
                    outp[i] = pal[(i & 1) != 0 ? (b & 0x0F) : (b >> 4)];
                }
                break;
            }
        }
    }
}
