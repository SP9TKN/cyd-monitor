namespace CydMonitor;

/// <summary>
/// GDI fallback capture, used when Desktop Duplication is unavailable.
///
/// Kept because duplication can be refused outright — a session where no
/// adapter will hand over the output, an RDP console — and a monitor that
/// refuses to start is worse than a slow one. It is genuinely slow, though, and
/// not by a margin that tuning closes: reads from a display DC are synchronised
/// to the monitor's refresh, so a full-screen grab costs two 16.7 ms intervals
/// whatever filter or staging buffer is used. That is the entire 30 fps budget.
/// Expect roughly 25-30 fps from this path with nothing to spare, and see
/// <see cref="DuplicationSource"/> for the one that meets the target.
/// </summary>
internal sealed unsafe class ScreenSource : IFrameSource
{
    private readonly IntPtr _screenDc;
    private readonly Dib _out;

    public int CursorSize { get; set; } = 20;
    public string Kind => "GDI StretchBlt (awaryjne, ograniczone przez vsync)";
    /// <summary>Ignored: a GDI screen read always blocks for the refresh interval anyway.</summary>
    public int AcquireTimeoutMs { get; set; }
    /// <summary>A GDI screen read blocks for the refresh interval inside BitBlt,
    /// so there is no wait to separate out — the whole call counts as work.</summary>
    public double LastWorkMs { get; private set; }
    public double LastWaitMs => 0;

    public Native.RECT LastSrc { get; private set; }
    public Native.RECT LastDst { get; private set; }
    public uint* Pixels => _out.Bits;

    public ScreenSource()
    {
        _screenDc = Native.GetDC(IntPtr.Zero);
        _out = new Dib(_screenDc, Proto.W, Proto.H);

        Native.SetStretchBltMode(_out.Dc, Native.HALFTONE);
        // HALFTONE is documented as requiring the brush origin to be reset
        // after it is selected, or its dithering drifts across the image.
        Native.SetBrushOrgEx(_out.Dc, 0, 0, IntPtr.Zero);
    }

    public bool Capture(Native.RECT monitor, ScaleMode mode)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Native.RECT src, dst;

        if (mode == ScaleMode.OneToOne)
        {
            // A true-scale window of the desktop, following the pointer.
            // Anchoring it to a fixed corner would make the mode useless the
            // moment anything happened elsewhere on screen.
            var ci = new Native.CURSORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.CURSORINFO>(),
            };
            int cx = monitor.Left + monitor.Width / 2;
            int cy = monitor.Top + monitor.Height / 2;
            if (Native.GetCursorInfo(ref ci) &&
                ci.ptScreenPos.X >= monitor.Left && ci.ptScreenPos.X < monitor.Right &&
                ci.ptScreenPos.Y >= monitor.Top && ci.ptScreenPos.Y < monitor.Bottom)
            {
                cx = ci.ptScreenPos.X;
                cy = ci.ptScreenPos.Y;
            }
            int left = Math.Clamp(cx - Proto.W / 2, monitor.Left,
                                  Math.Max(monitor.Left, monitor.Right - Proto.W));
            int top = Math.Clamp(cy - Proto.H / 2, monitor.Top,
                                 Math.Max(monitor.Top, monitor.Bottom - Proto.H));
            int w = Math.Min(Proto.W, monitor.Width), h = Math.Min(Proto.H, monitor.Height);
            src = new Native.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h };
            dst = new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
        }
        else
        {
            src = monitor;
            if (mode == ScaleMode.Stretch || monitor.Width * Proto.H == monitor.Height * Proto.W)
            {
                // See the note on Proto.StatsMarginPx for why this row band
                // is carved out unconditionally rather than only when the
                // overlay is actually on.
                dst = new Native.RECT { Left = 0, Top = 0, Right = Proto.W, Bottom = Proto.H - Proto.StatsMarginPx };
            }
            else
            {
                int dw, dh;
                if (monitor.Width * Proto.H > monitor.Height * Proto.W)
                {
                    dw = Proto.W;
                    dh = Math.Max(1, (int)Math.Round(Proto.W * (double)monitor.Height / monitor.Width));
                }
                else
                {
                    dh = Proto.H;
                    dw = Math.Max(1, (int)Math.Round(Proto.H * (double)monitor.Width / monitor.Height));
                }
                int ox = (Proto.W - dw) / 2, oy = (Proto.H - dh) / 2;
                dst = new Native.RECT { Left = ox, Top = oy, Right = ox + dw, Bottom = oy + dh };
            }
        }

        // Paint the letterbox bars only when their geometry changes. Every frame
        // would be harmless to look at but would make those tiles appear changed
        // to the differ, and there is no reason to transmit black repeatedly.
        if (!SameRect(dst, LastDst))
            for (int i = 0; i < Proto.W * Proto.H; i++) _out.Bits[i] = 0xFF000000;

        bool ok = src.Width == dst.Width && src.Height == dst.Height
            ? Native.BitBlt(_out.Dc, dst.Left, dst.Top, dst.Width, dst.Height,
                            _screenDc, src.Left, src.Top, Native.SRCCOPY)
            : Native.StretchBlt(_out.Dc, dst.Left, dst.Top, dst.Width, dst.Height,
                                _screenDc, src.Left, src.Top, src.Width, src.Height,
                                Native.SRCCOPY);

        LastSrc = src;
        LastDst = dst;
        if (ok) CursorOverlay.Draw(_out, src, dst, CursorSize);
        LastWorkMs = clock.Elapsed.TotalMilliseconds;
        return ok;
    }

    private static bool SameRect(Native.RECT a, Native.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    public void Dispose()
    {
        _out.Dispose();
        if (_screenDc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
