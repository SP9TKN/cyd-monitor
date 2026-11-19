using System.Runtime.InteropServices;

namespace CydMonitor;

/// <summary>
/// Creates the Windows virtual monitor the panel mirrors, by driving the
/// Parsec Virtual Display Driver.
///
/// Writing an Indirect Display Driver from scratch was never worth it: it is a
/// WDK project that has to be signed before Windows will load it, which on a
/// machine without a code-signing certificate means turning on test signing and
/// rebooting. The Parsec VDD is the same kind of driver, already signed, MIT
/// licensed, and usable without the Parsec application — and it exposes an
/// IOCTL interface, so displays can be created and destroyed programmatically
/// instead of configured by hand in someone else's control panel.
///
/// Source and protocol: https://github.com/nomi-san/parsec-vdd
///
/// The one behaviour worth knowing: an added display is a lease, not a
/// registration. The driver unplugs every display it owns roughly a second
/// after the last ping, so <see cref="Start"/> runs a keepalive for the
/// lifetime of this object. That is a feature rather than a nuisance — if this
/// server crashes, the phantom monitor disappears on its own instead of leaving
/// the user with a desktop extended onto something that no longer exists.
///
/// NOTE: this path is written from the driver's documented interface but has
/// not been exercised here, because installing the driver needs administrator
/// rights that this session does not have. Everything else in the server was
/// tested against a real monitor.
/// </summary>
internal sealed class VirtualDisplay : IDisposable
{
    private static readonly Guid VddInterfaceGuid = new("00b41627-04c4-429e-a26e-0265cf50c8fa");

    private const uint IoctlAdd     = 0x0022e004;
    private const uint IoctlRemove  = 0x0022a008;
    private const uint IoctlUpdate  = 0x0022a00c;   // the keepalive ping
    private const uint IoctlVersion = 0x0022e010;

    private IntPtr _device = INVALID_HANDLE;
    private int _index = -1;
    private CancellationTokenSource? _ping;

    public int Index => _index;
    public bool IsOpen => _device != INVALID_HANDLE;

    /// <summary>True when the driver is installed, regardless of whether a display is added.</summary>
    public static bool IsDriverInstalled() => FindDevicePath() is not null;

    /// <summary>
    /// Sends an explicit remove for every index the driver's protocol allows
    /// (0-15, see parsec-vdd's own "up to 16 displays" limit), regardless of
    /// whether this process ever added them.
    ///
    /// Exists for recovery, not normal operation: a display this process
    /// added is supposed to be removed automatically about a second after its
    /// keepalive stops (see the class remarks), including when the process
    /// dies uncleanly — but that was tested here by force-killing the process
    /// repeatedly during development, and it stopped working: Add kept
    /// reporting success while Windows stopped surfacing any display for it
    /// at all. Sweeping every index clears whatever the driver thinks is
    /// still attached without needing the reboot that would otherwise be the
    /// only other way to reset an IddCx driver's internal state.
    /// </summary>
    public static void RemoveAllIndices()
    {
        string? path = FindDevicePath();
        if (path is null) return;

        IntPtr device = CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
                                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                                    OPEN_EXISTING,
                                    FILE_FLAG_OVERLAPPED | FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
                                    IntPtr.Zero);
        if (device == INVALID_HANDLE) return;

        try
        {
            for (int i = 0; i < 16; i++)
                RemoveIndex(device, i);
        }
        finally
        {
            CloseHandle(device);
        }
    }

    private static void RemoveIndex(IntPtr device, int index)
    {
        var input = new byte[32];
        // Big-endian per the driver's own protocol — see the note in Ioctl().
        input[0] = (byte)((index >> 8) & 0xFF);
        input[1] = (byte)(index & 0xFF);

        IntPtr evt = CreateEventW(IntPtr.Zero, true, false, null);
        if (evt == IntPtr.Zero) return;
        var overlapped = new NativeOverlapped { EventHandle = evt };
        var output = new byte[4];
        try
        {
            GCHandle inPin = GCHandle.Alloc(input, GCHandleType.Pinned);
            GCHandle outPin = GCHandle.Alloc(output, GCHandleType.Pinned);
            try
            {
                DeviceIoControl(device, IoctlRemove,
                               inPin.AddrOfPinnedObject(), (uint)input.Length,
                               outPin.AddrOfPinnedObject(), (uint)output.Length,
                               out _, ref overlapped);
                GetOverlappedResultEx(device, ref overlapped, out _, 500, false);
            }
            finally
            {
                inPin.Free();
                outPin.Free();
            }
        }
        finally
        {
            CloseHandle(evt);
        }
    }

    /// <summary>
    /// Opens the driver and adds one virtual display. Returns false with a
    /// reason rather than throwing, because "the driver isn't installed" is an
    /// ordinary state this program has to explain, not an exceptional one.
    /// </summary>
    public bool Start(out string error)
    {
        error = "";
        string? path = FindDevicePath();
        if (path is null)
        {
            error = "sterownik Parsec VDD nie jest zainstalowany (scripts/install-virtual-display.ps1)";
            return false;
        }

        _device = CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
                              FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                              OPEN_EXISTING,
                              FILE_FLAG_OVERLAPPED | FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH,
                              IntPtr.Zero);
        if (_device == INVALID_HANDLE)
        {
            error = $"nie moge otworzyc sterownika (kod {Marshal.GetLastWin32Error()})";
            return false;
        }

        if (!Ioctl(IoctlVersion, out _))
        {
            error = "sterownik nie odpowiada na zapytanie o wersje";
            Close();
            return false;
        }

        if (!Ioctl(IoctlAdd, out uint added))
        {
            error = "sterownik odmowil dodania ekranu";
            Close();
            return false;
        }
        _index = (int)added;

        // Must be well under the driver's ~1 s timeout. 50 ms leaves room for a
        // scheduling hiccup without ever letting the display drop.
        _ping = new CancellationTokenSource();
        var token = _ping.Token;
        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                Ioctl(IoctlUpdate, out _);
                token.WaitHandle.WaitOne(50);
            }
        })
        {
            IsBackground = true,
            Name = "vdd-keepalive",
        };
        thread.Start();

        return true;
    }

    /// <summary>
    /// The device path of the monitor the driver just created, so the capture
    /// side can target it. Matched by adapter description rather than by index:
    /// Windows assigns \\.\DISPLAYn in its own order, and on a machine with
    /// several monitors the new one is not reliably the last.
    /// </summary>
    public static MonitorTarget? FindCreatedMonitor()
    {
        var all = Monitors.Enumerate();
        return all.FirstOrDefault(m =>
                   m.Description.Contains("parsec", StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m =>
                   m.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m =>
                   m.Description.Contains("idd", StringComparison.OrdinalIgnoreCase));
    }

    private bool Ioctl(uint code, out uint result)
    {
        result = 0;
        // The driver expects a fixed 32-byte input regardless of the command.
        var input = new byte[32];
        if (code == IoctlRemove && _index >= 0)
        {
            // Removal is the one command that carries a payload: the display
            // index as a big-endian 16-bit value, which is the opposite of this
            // machine's byte order and so has to be written out by hand.
            input[0] = (byte)((_index >> 8) & 0xFF);
            input[1] = (byte)(_index & 0xFF);
        }

        IntPtr evt = CreateEventW(IntPtr.Zero, true, false, null);
        if (evt == IntPtr.Zero) return false;

        var overlapped = new NativeOverlapped { EventHandle = evt };
        var output = new byte[4];
        try
        {
            GCHandle inPin = GCHandle.Alloc(input, GCHandleType.Pinned);
            GCHandle outPin = GCHandle.Alloc(output, GCHandleType.Pinned);
            try
            {
                bool ok = DeviceIoControl(_device, code,
                                          inPin.AddrOfPinnedObject(), (uint)input.Length,
                                          outPin.AddrOfPinnedObject(), (uint)output.Length,
                                          out uint _, ref overlapped);
                if (!ok && Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
                if (!GetOverlappedResultEx(_device, ref overlapped, out uint _, 5000, false))
                    return false;
            }
            finally
            {
                inPin.Free();
                outPin.Free();
            }
        }
        finally
        {
            CloseHandle(evt);
        }

        result = BitConverter.ToUInt32(output, 0);
        return true;
    }

    private static string? FindDevicePath()
    {
        var guid = VddInterfaceGuid;
        IntPtr set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero,
                                          DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE) return null;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
            };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, ref data))
                return null;

            SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
            if (needed == 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                // cbSize here is the size of the fixed part of the struct, not
                // of the buffer — 8 on 64-bit Windows. Getting it wrong is the
                // classic SetupAPI failure.
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, needed, out _, IntPtr.Zero))
                    return null;
                return Marshal.PtrToStringUni(buffer + 4);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private void Close()
    {
        if (_device != INVALID_HANDLE)
        {
            CloseHandle(_device);
            _device = INVALID_HANDLE;
        }
    }

    public void Dispose()
    {
        _ping?.Cancel();
        _ping?.Dispose();
        _ping = null;
        if (_device != INVALID_HANDLE && _index >= 0)
        {
            Ioctl(IoctlRemove, out _);
            _index = -1;
        }
        Close();
    }

    // ------------------------------------------------------------------ interop

    private static readonly IntPtr INVALID_HANDLE = new(-1);
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    private const int ERROR_IO_PENDING = 997;
    private const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator,
                                                      IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
                                                           ref Guid interfaceClassGuid, uint memberIndex,
                                                           ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint detailSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string path, uint access, uint share,
                                             IntPtr security, uint disposition,
                                             uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code,
                                               IntPtr inBuffer, uint inSize,
                                               IntPtr outBuffer, uint outSize,
                                               out uint returned, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResultEx(IntPtr file, ref NativeOverlapped overlapped,
                                                     out uint transferred, uint milliseconds, bool alertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
