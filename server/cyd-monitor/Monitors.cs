using System.Runtime.InteropServices;

namespace CydMonitor;

internal sealed record MonitorTarget(
    int Index,
    string Device,        // e.g. \\.\DISPLAY2
    string Description,   // adapter name reported by the driver
    Native.RECT Bounds,
    bool Primary)
{
    public override string ToString() =>
        $"#{Index}  {Device,-16} {Bounds.Width}x{Bounds.Height} @ {Bounds.Left},{Bounds.Top}" +
        (Primary ? "  [glowny]" : "") + $"  {Description}";
}

internal static class Monitors
{
    public static List<MonitorTarget> Enumerate()
    {
        var found = new List<(IntPtr h, Native.RECT r)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr h, IntPtr _, ref Native.RECT r, IntPtr _) => { found.Add((h, r)); return true; },
            IntPtr.Zero);

        var list = new List<MonitorTarget>();
        for (int i = 0; i < found.Count; i++)
        {
            var mi = new Native.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<Native.MONITORINFOEX>(),
                szDevice = string.Empty,
            };
            if (!Native.GetMonitorInfo(found[i].h, ref mi)) continue;

            string desc = DescribeAdapter(mi.szDevice);
            list.Add(new MonitorTarget(i + 1, mi.szDevice.TrimEnd('\0'), desc,
                                       mi.rcMonitor, (mi.dwFlags & 1) != 0));
        }
        // Stable order across runs, so "--monitor 2" keeps meaning the same
        // screen after a reboot.
        list.Sort((a, b) => string.CompareOrdinal(a.Device, b.Device));
        for (int i = 0; i < list.Count; i++) list[i] = list[i] with { Index = i + 1 };
        return list;
    }

    private static string DescribeAdapter(string deviceName)
    {
        var dd = new Native.DISPLAY_DEVICE
        {
            cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>(),
            DeviceName = string.Empty,
            DeviceString = string.Empty,
            DeviceID = string.Empty,
            DeviceKey = string.Empty,
        };
        for (uint n = 0; Native.EnumDisplayDevices(null, n, ref dd, 0); n++)
        {
            if (dd.DeviceName.TrimEnd('\0') == deviceName.TrimEnd('\0'))
                return dd.DeviceString.TrimEnd('\0');
            dd.cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>();
        }
        return "?";
    }

    /// <summary>
    /// Resolves the target named on the command line: an index, a device path,
    /// or a substring of the adapter description (so "virtual" finds the
    /// virtual display driver's output without needing to know its number).
    /// </summary>
    public static MonitorTarget? Resolve(string spec)
    {
        var all = Enumerate();
        if (all.Count == 0) return null;
        if (int.TryParse(spec, out int idx))
            return all.FirstOrDefault(m => m.Index == idx);

        return all.FirstOrDefault(m =>
                   m.Device.Contains(spec, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m =>
                   m.Description.Contains(spec, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Changes one monitor's resolution. This is how the device's menu can
    /// trade desktop area against legibility at runtime: the physical panel is
    /// always 320x240, so the only real dial is how much desktop gets squeezed
    /// into it.
    /// </summary>
    /// <returns>The size actually in force afterwards, which is not always the
    /// size requested — a driver may refuse a mode, and the menu must show what
    /// happened rather than what was asked.</returns>
    public static (int W, int H) SetResolution(string device, int w, int h)
    {
        if (!TryEnumCurrentSettings(device, out var dm))
            return (0, 0);

        // Snap to a mode the driver actually enumerates before asking for it.
        // Found the hard way: a real monitor (this machine's Intel adapter)
        // accepts an arbitrary custom size like 640x480 outside its enumerated
        // list, but the Parsec virtual display driver has a fixed EDID preset
        // table (720p/900p/1200p-class sizes) and silently ignores a request
        // for anything else — ChangeDisplaySettingsEx returns without error,
        // CurrentResolution reads back unchanged, and the caller would report
        // success at a resolution nothing actually changed to. Asking for the
        // nearest size the device itself lists means the call either lands
        // exactly or degrades to a real, working mode instead of a silent
        // no-op.
        var modes = AvailableModes(device);
        if (modes.Count > 0 && !modes.Contains((w, h)))
        {
            (w, h) = modes
                .OrderBy(m => Math.Abs((long)m.W * m.H - (long)w * h))
                .ThenBy(m => Math.Abs(m.W - w) + Math.Abs(m.H - h))
                .First();
        }

        if (dm.dmPelsWidth == (uint)w && dm.dmPelsHeight == (uint)h)
            return (w, h);

        dm.dmPelsWidth  = (uint)w;
        dm.dmPelsHeight = (uint)h;
        dm.dmFields     = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT;

        Native.ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero,
                                       Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
        return CurrentResolution(device);
    }

    public static (int W, int H) CurrentResolution(string device)
    {
        if (!TryEnumCurrentSettings(device, out var dm)) return (0, 0);
        return ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight);
    }

    /// <summary>
    /// EnumDisplaySettings for a just-created virtual monitor observed to fail
    /// transiently: a full end-to-end test (create the display, immediately
    /// negotiate a resolution for it) returned "device unreadable" on one call
    /// moments after an earlier call against the very same device name had
    /// succeeded. Nothing else about the device changed in between — the most
    /// likely explanation is the driver settling its own topology right after
    /// attach — so a few retries a beat apart resolve it, whereas failing
    /// immediately would report a driver refusal that never actually happened.
    /// Real, persistent unavailability (a genuinely wrong device name) still
    /// fails the same way after retrying, just slower.
    /// </summary>
    private static bool TryEnumCurrentSettings(string device, out Native.DEVMODE dm)
    {
        for (int attempt = 0; ; attempt++)
        {
            dm = new Native.DEVMODE
            {
                dmDeviceName = string.Empty,
                dmFormName = string.Empty,
                dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>(),
            };
            if (Native.EnumDisplaySettings(device, Native.ENUM_CURRENT_SETTINGS, ref dm))
                return true;
            if (attempt >= 4) return false;
            Thread.Sleep(150);
        }
    }

    /// <summary>Modes the driver will actually accept, deduplicated.</summary>
    public static List<(int W, int H)> AvailableModes(string device)
    {
        var modes = new HashSet<(int, int)>();
        var dm = new Native.DEVMODE
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>(),
        };
        for (int i = 0; Native.EnumDisplaySettings(device, i, ref dm); i++)
        {
            if (dm.dmBitsPerPel >= 24)
                modes.Add(((int)dm.dmPelsWidth, (int)dm.dmPelsHeight));
            dm.dmSize = (ushort)Marshal.SizeOf<Native.DEVMODE>();
        }
        var list = modes.ToList();
        list.Sort((a, b) => (a.Item1 * a.Item2).CompareTo(b.Item1 * b.Item2));
        return list;
    }
}
