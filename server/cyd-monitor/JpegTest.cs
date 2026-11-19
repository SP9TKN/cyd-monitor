namespace CydMonitor;

/// <summary>
/// Checks <see cref="JpegEncoder"/> against a decoder it shares nothing with.
///
/// Writes a synthetic 320x48 image — pure colour bars, a smooth gradient and
/// a photo-like texture, one 16 px strip each — as a JPEG the way the device
/// would assemble it, plus the exact lossless reference as a BMP. Any other
/// decoder can then be pointed at the pair; a swapped chroma channel or a
/// broken Huffman table shows up as wrong bar colours or an unreadable file
/// on the PC, instead of as a garbled panel after a flash cycle.
/// </summary>
internal static class JpegTest
{
    public static int Run(string dir, int quality)
    {
        const int W = Proto.W, H = 48;

        var rgb = new byte[W * H * 3];
        var rnd = new Random(12345);

        (int r, int g, int b)[] bars =
        [
            (255, 0, 0), (0, 255, 0), (0, 0, 255), (0, 255, 255),
            (255, 0, 255), (255, 255, 0), (255, 255, 255), (0, 0, 0),
        ];

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int r, g, b;
                if (y < 16)
                {
                    (r, g, b) = bars[x * bars.Length / W];
                }
                else if (y < 32)
                {
                    r = x * 255 / (W - 1);
                    g = (y - 16) * 255 / 15;
                    b = 255 - r;
                }
                else
                {
                    double s = Math.Sin(x * 0.21) * Math.Cos(y * 0.37);
                    r = Math.Clamp((int)(128 + 90 * s) + rnd.Next(-25, 26), 0, 255);
                    g = Math.Clamp((int)(110 + 70 * Math.Sin(x * 0.05 + y * 0.3)) + rnd.Next(-25, 26), 0, 255);
                    b = Math.Clamp((int)(90 + 80 * Math.Cos(x * 0.11)) + rnd.Next(-25, 26), 0, 255);
                }

                // Full 8-bit colour, which is what the encoder is fed in the
                // live path too (FrameProcessor.SoftRgb) — and so also the
                // reference a decoded strip is compared against.
                int o = (y * W + x) * 3;
                rgb[o]     = (byte)r;
                rgb[o + 1] = (byte)g;
                rgb[o + 2] = (byte)b;
            }
        }

        var enc = new JpegEncoder(quality);
        var buf = new byte[256 * 1024];

        Console.WriteLine($"jakosc {enc.Quality}, tablice (wysylane raz na sesje): {enc.Tables.Length} B");
        string[] names = ["paski kolorow", "gradient", "tekstura jak film"];
        Directory.CreateDirectory(dir);
        for (int s = 0; s < 3; s++)
        {
            int n = enc.EncodeScan(rgb, W * 3, 0, s * 16, W, 16, buf, 0, buf.Length);
            Console.WriteLine($"  pasek {s} ({names[s],-18}): JPEG {n,5} B   RAW {W * 16 * 2} B   ({100.0 * n / (W * 16 * 2):F1}%)");
            // Standalone, exactly as the device will see it: one strip per
            // JPEG, so no decoder can smooth chroma across a strip boundary.
            File.WriteAllBytes(Path.Combine(dir, $"jpegtest-s{s}.jpg"), enc.Wrap(W, 16, buf.AsSpan(0, n)));
        }

        int len = enc.EncodeScan(rgb, W * 3, 0, 0, W, H, buf, 0, buf.Length);
        byte[] jpg = enc.Wrap(W, H, buf.AsSpan(0, len));
        Directory.CreateDirectory(dir);
        string jpgPath = Path.Combine(dir, "jpegtest.jpg");
        string bmpPath = Path.Combine(dir, "jpegtest-ref.bmp");
        File.WriteAllBytes(jpgPath, jpg);
        WriteBmp(bmpPath, W, H, rgb);
        Console.WriteLine($"zapisano {jpgPath} ({jpg.Length} B) i {bmpPath}");
        return 0;
    }

    private static void WriteBmp(string path, int w, int h, byte[] rgb)
    {
        int row = (w * 3 + 3) & ~3;
        var f = new byte[54 + row * h];
        void U32(int at, int v) { BitConverter.TryWriteBytes(f.AsSpan(at, 4), v); }
        void U16(int at, short v) { BitConverter.TryWriteBytes(f.AsSpan(at, 2), v); }
        f[0] = (byte)'B'; f[1] = (byte)'M';
        U32(2, f.Length); U32(10, 54); U32(14, 40); U32(18, w); U32(22, h);
        U16(26, 1); U16(28, 24); U32(34, row * h);
        for (int y = 0; y < h; y++)
        {
            int dst = 54 + (h - 1 - y) * row;    // BMP rows run bottom-up
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 3;
                f[dst + x * 3]     = rgb[s + 2];  // and pixels are BGR
                f[dst + x * 3 + 1] = rgb[s + 1];
                f[dst + x * 3 + 2] = rgb[s];
            }
        }
        File.WriteAllBytes(path, f);
    }
}
