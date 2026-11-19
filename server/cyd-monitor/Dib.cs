using System.Runtime.InteropServices;

namespace CydMonitor;

/// <summary>
/// A 32-bit top-down device-independent bitmap bundled with its memory DC.
///
/// Both capture paths end in one of these. Holding the final 320x240 frame in a
/// GDI surface rather than a plain array is what lets the pointer be composited
/// with DrawIconEx — Desktop Duplication hands over the cursor as a separate
/// shape and position rather than baked into the image, and reimplementing
/// icon masking and colour blending by hand would be a lot of code to
/// reproduce something GDI already does correctly.
/// </summary>
internal sealed unsafe class Dib : IDisposable
{
    public IntPtr Dc { get; private set; }
    public uint* Bits { get; private set; }
    public int Width { get; }
    public int Height { get; }

    private IntPtr _bmp, _old;

    public Dib(IntPtr referenceDc, int w, int h)
    {
        Width = w;
        Height = h;

        var bmi = new Native.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = w;
        // Negative height asks for a top-down bitmap, so row 0 is the top row
        // and no pixel loop has to walk the buffer in reverse.
        bmi.bmiHeader.biHeight = -h;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB

        Dc = Native.CreateCompatibleDC(referenceDc);
        _bmp = Native.CreateDIBSection(Dc, ref bmi, Native.DIB_RGB_COLORS,
                                       out IntPtr bits, IntPtr.Zero, 0);
        if (_bmp == IntPtr.Zero)
            throw new InvalidOperationException("CreateDIBSection failed");
        Bits = (uint*)bits;
        _old = Native.SelectObject(Dc, _bmp);
    }

    public void Dispose()
    {
        if (Dc != IntPtr.Zero)
        {
            if (_old != IntPtr.Zero) Native.SelectObject(Dc, _old);
            Native.DeleteDC(Dc);
            Dc = IntPtr.Zero;
        }
        if (_bmp != IntPtr.Zero) { Native.DeleteObject(_bmp); _bmp = IntPtr.Zero; }
        Bits = null;
    }
}

/// <summary>
/// Draws the mouse pointer into a finished frame. Shared by both capture paths:
/// neither GDI blits nor Desktop Duplication include it, and a monitor you
/// cannot aim is not a monitor.
/// </summary>
internal static class CursorOverlay
{
    public static void Draw(Dib dib, Native.RECT src, Native.RECT dst, int size)
    {
        if (size <= 0) return;

        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci)) return;
        if ((ci.flags & Native.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        if (ci.ptScreenPos.X < src.Left || ci.ptScreenPos.X >= src.Right ||
            ci.ptScreenPos.Y < src.Top  || ci.ptScreenPos.Y >= src.Bottom) return;

        double fx = dst.Width  / (double)src.Width;
        double fy = dst.Height / (double)src.Height;
        int px = dst.Left + (int)((ci.ptScreenPos.X - src.Left) * fx);
        int py = dst.Top  + (int)((ci.ptScreenPos.Y - src.Top)  * fy);

        // CopyIcon: the handle GetCursorInfo returns belongs to whoever set the
        // cursor and can be freed underneath a draw that is still in progress.
        IntPtr icon = Native.CopyIcon(ci.hCursor);
        if (icon == IntPtr.Zero) return;
        try
        {
            int hx = 0, hy = 0;
            if (Native.GetIconInfo(icon, out var ii))
            {
                if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
                // Scale the hotspot by the same factor as the icon, so the tip
                // that is drawn sits where the click actually lands.
                hx = ii.xHotspot * size / 32;
                hy = ii.yHotspot * size / 32;
            }
            // Drawn at a fixed pixel size, not a scaled one: a 32x32 arrow
            // reduced 6x is three pixels of grey mush.
            Native.DrawIconEx(dib.Dc, px - hx, py - hy, icon,
                              size, size, 0, IntPtr.Zero, Native.DI_NORMAL);
        }
        finally
        {
            Native.DestroyIcon(icon);
        }
    }
}
