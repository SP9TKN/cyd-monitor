using System.Runtime.InteropServices;

namespace CydMonitor;

/// <summary>
/// A stand-in for the desktop that behaves like full-screen video: a smooth,
/// photo-like texture panned a few pixels every frame, so every tile changes
/// every frame and every strip is the kind of content the JPEG path exists
/// for.
///
/// Exists so the device's decode throughput can be measured the same way
/// twice. With a real film, each measurement depended on the film actually
/// playing through the whole window, on the scene, and on the player — and
/// several were lost to a paused or finished video. This produces the same
/// sequence every run and never touches the real desktop.
/// </summary>
internal sealed unsafe class SyntheticSource : IFrameSource
{
    private const int TexW = 1024, TexH = 768;
    private readonly uint[] _tex = new uint[TexW * TexH];
    private readonly uint* _px;
    private int _frame;

    public SyntheticSource()
    {
        _px = (uint*)NativeMemory.Alloc((nuint)(Proto.W * Proto.H * 4));

        // Smooth colour fields plus a fine value-noise texture: enough detail
        // that JPEG has real work in every block (a flat gradient would decode
        // unrealistically fast), smooth enough to read as a photograph rather
        // than as noise.
        var rnd = new Random(7);
        var noise = new float[(TexW / 8 + 2) * (TexH / 8 + 2)];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)rnd.NextDouble();
        int nw = TexW / 8 + 2;

        for (int y = 0; y < TexH; y++)
        {
            for (int x = 0; x < TexW; x++)
            {
                // Bilinear value noise on an 8 px grid.
                float gx = x / 8f, gy = y / 8f;
                int ix = (int)gx, iy = (int)gy;
                float fx = gx - ix, fy = gy - iy;
                float n00 = noise[iy * nw + ix], n10 = noise[iy * nw + ix + 1];
                float n01 = noise[(iy + 1) * nw + ix], n11 = noise[(iy + 1) * nw + ix + 1];
                float n = (n00 * (1 - fx) + n10 * fx) * (1 - fy) + (n01 * (1 - fx) + n11 * fx) * fy;

                double r = 120 + 90 * Math.Sin(x * 0.011) * Math.Cos(y * 0.017) + 60 * (n - 0.5);
                double g = 115 + 80 * Math.Sin((x + y) * 0.009 + 1.0) + 60 * (n - 0.5);
                double b = 110 + 90 * Math.Cos(x * 0.007 - y * 0.013) + 60 * (n - 0.5);
                uint R = (uint)Math.Clamp((int)r, 0, 255);
                uint G = (uint)Math.Clamp((int)g, 0, 255);
                uint B = (uint)Math.Clamp((int)b, 0, 255);
                _tex[y * TexW + x] = 0xFF000000u | (R << 16) | (G << 8) | B;   // BGRA in memory
            }
        }
    }

    public int CursorSize { get; set; }
    public int AcquireTimeoutMs { get; set; }
    public double LastWorkMs { get; private set; }
    public double LastWaitMs => 0;
    public string Kind => "SYNTETYCZNE zrodlo testowe (przesuwana tekstura, caly ekran zmienia sie co klatke)";
    public uint* Pixels => _px;
    public Native.RECT LastSrc { get; } = new() { Left = 0, Top = 0, Right = Proto.W, Bottom = Proto.H };
    public Native.RECT LastDst { get; } = new() { Left = 0, Top = 0, Right = Proto.W, Bottom = Proto.H };

    /// <summary>Always a new frame: the frame loop's own rate cap is what paces it.</summary>
    public bool Capture(Native.RECT monitor, ScaleMode mode)
    {
        var t = System.Diagnostics.Stopwatch.StartNew();
        _frame++;
        // A slow diagonal pan that bounces off the texture's edges — camera
        // motion, the most common kind of full-screen change in a film.
        int spanX = TexW - Proto.W, spanY = TexH - Proto.H;
        int ox = Bounce(_frame * 3, spanX), oy = Bounce(_frame * 2, spanY);
        fixed (uint* tex = _tex)
        {
            for (int y = 0; y < Proto.H; y++)
                Buffer.MemoryCopy(tex + (oy + y) * TexW + ox, _px + y * Proto.W, Proto.W * 4, Proto.W * 4);
        }
        LastWorkMs = t.Elapsed.TotalMilliseconds;
        return true;
    }

    private static int Bounce(int v, int span)
    {
        int p = v % (2 * span);
        return p < span ? p : 2 * span - p;
    }

    public void Dispose() => NativeMemory.Free(_px);
}
