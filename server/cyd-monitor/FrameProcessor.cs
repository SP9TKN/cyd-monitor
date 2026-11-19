namespace CydMonitor;

/// <summary>
/// Turns captured BGRA into the panel's pixel format, applying the picture
/// controls on the way.
///
/// This is where "contrast" and "gamma" actually happen. The ILI9341 has no
/// contrast register — unlike a desktop monitor there is no hardware control to
/// forward a DDC/CI request to — so the only place the transfer curve can be
/// changed is while the pixels are still 8 bits per channel, here. Brightness
/// is the opposite case and is handled on the device instead, by the backlight
/// PWM: dimming with light loses no colour resolution, whereas scaling the
/// pixel values would throw away the low end of every channel.
///
/// Output is stored byte-swapped: each ushort holds an RGB565 value whose
/// halves are already in the order the panel's SPI stream wants. Writing such
/// a value out little-endian therefore emits panel-native bytes, and a run of
/// them can be memcpy'd to the wire, which is what lets the firmware DMA
/// straight from the socket buffer without touching a single pixel. See the
/// endianness note in protocol.h.
/// </summary>
internal sealed unsafe class FrameProcessor
{
    // Ordered 4x4 Bayer. Quantising 24-bit colour to 5/6/5 throws away 3 bits
    // of red and blue, which shows up as hard contour rings across any gradient
    // — a desktop wallpaper or a window's title bar makes them obvious.
    // Perturbing each pixel by a repeating sub-threshold pattern converts those
    // rings into a fine texture the eye integrates away at this pixel density.
    private static readonly int[] Bayer =
    [
         0,  8,  2, 10,
        12,  4, 14,  6,
         3, 11,  1,  9,
        15,  7, 13,  5,
    ];

    private readonly byte[] _lut = new byte[256];
    private int _contrast;
    private int _gamma = 100;
    private int _sharpen;

    // Holds R,G,B (in that order per pixel) after the contrast/gamma LUT but
    // before sharpening or dithering — the intermediate a sharpen pass needs
    // to read neighbouring pixels from, which the single-pass-per-pixel loop
    // below can't do directly against the source (that's packed BGRA, and by
    // the time a neighbour is read it may not have been LUT'd yet).
    private readonly byte[] _lutRgb = new byte[Proto.W * Proto.H * 3];

    public bool Dither { get; set; } = true;

    /// <summary>
    /// The last frame after contrast/gamma but before sharpening, dithering or
    /// quantisation to RGB565: 8 bits per channel, R,G,B, <see
    /// cref="Proto.W"/>*3 bytes per row. This is what JPEG strips are coded
    /// from. Sharpening is for text on a small screen; applied ahead of JPEG
    /// it adds halos the codec can't represent at a sane quality, and they
    /// come out as ringing around every edge of a moving picture.
    /// </summary>
    public byte[] SoftRgb => _lutRgb;

    public int Contrast
    {
        get => _contrast;
        set { _contrast = Math.Clamp(value, -64, 64); BuildLut(); }
    }

    public int Gamma
    {
        get => _gamma;
        set { _gamma = Math.Clamp(value, 50, 300); BuildLut(); }
    }

    /// <summary>
    /// 0..100. Unsharp masking: each pixel is pushed further away from a
    /// blur of its four neighbours, by this much. Exists because the GPU
    /// downscale (mip chain + trilinear, see DuplicationSource) is a smooth
    /// filter chosen specifically to avoid aliasing on a 6x+ reduction —
    /// which costs some perceived sharpness that a cheap desktop monitor's
    /// own scaler would usually claw back with exactly this kind of filter.
    /// 0 skips the second pass entirely rather than running it with a
    /// no-op amount, so leaving this off costs nothing.
    /// </summary>
    public int Sharpen
    {
        get => _sharpen;
        set => _sharpen = Math.Clamp(value, 0, 100);
    }

    public FrameProcessor() => BuildLut();

    // Rebuilt from the inbound reader thread while the frame loop may be
    // reading it. A torn read costs at most one frame drawn with a mix of the
    // old and new curve, and every setting that calls this also forces a full
    // repaint immediately afterwards, so the mixed frame is overwritten before
    // anyone can see it. Locking the hot loop against a 256-byte table rebuild
    // would cost more than the defect.
    private void BuildLut()
    {
        // The usual photo-editing contrast curve: pivot around mid-grey and
        // scale the distance from it. The -255..255 form is what the factor
        // expression below is defined for, so the protocol's -64..64 (a range
        // that fits a menu with a sane step size) is widened by 4.
        double c = Math.Clamp(_contrast * 4, -255, 255);
        double factor = (259.0 * (c + 255.0)) / (255.0 * (259.0 - c));
        double exp = 100.0 / _gamma;

        for (int i = 0; i < 256; i++)
        {
            double v = factor * (i - 128.0) + 128.0;
            v = Math.Clamp(v, 0, 255) / 255.0;
            v = Math.Pow(v, exp);
            _lut[i] = (byte)Math.Clamp((int)Math.Round(v * 255.0), 0, 255);
        }
    }

    /// <param name="src">BGRA, <see cref="Proto.W"/> x <see cref="Proto.H"/>, top row first.</param>
    /// <param name="dst">Receives byte-swapped RGB565, same dimensions.</param>
    public void Process(uint* src, ushort[] dst)
    {
        const int W = Proto.W, H = Proto.H;

        // Pass 1: contrast/gamma only, into the 8-bit-per-channel
        // intermediate. Split out from the original single-pass loop
        // because pass 2 (sharpen) needs a pixel's already-LUT'd neighbours,
        // which aren't available yet while still walking the raw BGRA source.
        fixed (byte* lut = _lut)
        fixed (byte* rgb = _lutRgb)
        {
            for (int y = 0; y < H; y++)
            {
                uint* sRow = src + y * W;
                byte* rgbRow = rgb + y * W * 3;
                for (int x = 0; x < W; x++)
                {
                    uint p = sRow[x];
                    int o = x * 3;
                    rgbRow[o]     = lut[(int)((p >> 16) & 0xFF)];   // R
                    rgbRow[o + 1] = lut[(int)((p >> 8) & 0xFF)];    // G
                    rgbRow[o + 2] = lut[(int)(p & 0xFF)];           // B
                }
            }
        }

        // Pass 2: sharpen (if on), dither (if on), quantise to RGB565.
        fixed (ushort* dp = dst)
        fixed (byte* rgb = _lutRgb)
        fixed (int* bayer = Bayer)
        {
            bool dither = Dither;
            // User-requested, twice now: first 3x over the original 0..1
            // mapping, then still more on top of that — 4.5x total, so 100
            // means amount=4.5 (each pixel pushed four and a half times its
            // own distance from the local blur).
            double amount = _sharpen / 100.0 * 4.5;
            bool sharpen = _sharpen > 0;

            for (int y = 0; y < H; y++)
            {
                byte* rgbRow = rgb + y * W * 3;
                byte* rgbUp = rgb + Math.Max(0, y - 1) * W * 3;
                byte* rgbDown = rgb + Math.Min(H - 1, y + 1) * W * 3;
                ushort* dRow = dp + y * W;
                int* bRow = bayer + ((y & 3) << 2);

                for (int x = 0; x < W; x++)
                {
                    int o = x * 3;
                    int r = rgbRow[o], g = rgbRow[o + 1], b = rgbRow[o + 2];

                    if (sharpen)
                    {
                        int xl = Math.Max(0, x - 1) * 3, xr = Math.Min(W - 1, x + 1) * 3;
                        // Cheap 4-neighbour (not full 3x3) blur estimate —
                        // plenty for an amount that's tuned by eye on a
                        // 320x240 panel, and a quarter of the reads.
                        int rB = (rgbRow[xl] + rgbRow[xr] + rgbUp[o] + rgbDown[o]) >> 2;
                        int gB = (rgbRow[xl + 1] + rgbRow[xr + 1] + rgbUp[o + 1] + rgbDown[o + 1]) >> 2;
                        int bB = (rgbRow[xl + 2] + rgbRow[xr + 2] + rgbUp[o + 2] + rgbDown[o + 2]) >> 2;
                        r = (int)Math.Clamp(r + amount * (r - rB), 0, 255);
                        g = (int)Math.Clamp(g + amount * (g - gB), 0, 255);
                        b = (int)Math.Clamp(b + amount * (b - bB), 0, 255);
                    }

                    int r5, g6, b5;
                    if (dither)
                    {
                        int t = bRow[x & 3];
                        // Red and blue lose 3 bits, green loses 2, so the
                        // perturbation each channel needs differs: up to 7 for
                        // the 5-bit channels, up to 3 for the 6-bit one.
                        r5 = Math.Min(31, (r + (t >> 1)) >> 3);
                        g6 = Math.Min(63, (g + (t >> 2)) >> 2);
                        b5 = Math.Min(31, (b + (t >> 1)) >> 3);
                    }
                    else
                    {
                        r5 = r >> 3;
                        g6 = g >> 2;
                        b5 = b >> 3;
                    }

                    int c565 = (r5 << 11) | (g6 << 5) | b5;
                    dRow[x] = (ushort)(((c565 & 0xFF) << 8) | (c565 >> 8));
                }
            }
        }
    }
}
