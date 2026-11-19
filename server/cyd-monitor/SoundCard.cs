using System.Runtime.InteropServices;

namespace CydMonitor;

/// <summary>
/// The mute of one Windows output, the same switch as the speaker icon in the
/// volume mixer, found by the name SDR Oxide stores for its audio output. It
/// silences everything on that card and nothing on any other.
/// </summary>
internal sealed class SoundCard
{
    private readonly object _lock = new();
    private IAudioEndpointVolume? _endpoint;
    private string _name = "";

    /// <summary>Null when no output has that name.</summary>
    public bool? IsMuted(string name)
    {
        lock (_lock)
        {
            try
            {
                var ep = Endpoint(name);
                if (ep is null) return null;
                ep.GetMute(out bool muted);
                return muted;
            }
            catch (COMException) { _endpoint = null; return null; }
        }
    }

    public bool Toggle(string name)
    {
        lock (_lock)
        {
            try
            {
                var ep = Endpoint(name);
                if (ep is null) return false;
                ep.GetMute(out bool muted);
                var context = Guid.Empty;
                ep.SetMute(!muted, ref context);
                return true;
            }
            catch (COMException) { _endpoint = null; return false; }
        }
    }

    /// <summary>
    /// The mute of one program's sound on that output: its own slider in the volume mixer.
    /// Oxide silent in the house while the rest of the PC's sound plays on. Null when the
    /// program has no sound there (not running, or not playing yet).
    /// </summary>
    public bool? IsAppMuted(string name, string process)
    {
        lock (_lock)
        {
            try
            {
                bool? muted = null;
                foreach (var v in AppVolumes(name, process))
                {
                    v.GetMute(out bool m);
                    muted = (muted ?? true) && m;
                }
                return muted;
            }
            catch (COMException) { return null; }
        }
    }

    /// <summary>False when the program has no sound on that output to mute.</summary>
    public bool SetAppMute(string name, string process, bool mute)
    {
        lock (_lock)
        {
            try
            {
                bool any = false;
                foreach (var v in AppVolumes(name, process))
                {
                    var context = Guid.Empty;
                    v.SetMute(mute, ref context);
                    any = true;
                }
                return any;
            }
            catch (COMException) { return false; }
        }
    }

    /// <summary>Every session of that program on that output: Oxide opens one per radio.</summary>
    private static List<ISimpleAudioVolume> AppVolumes(string name, string process)
    {
        var found = new List<ISimpleAudioVolume>();
        var pids = System.Diagnostics.Process.GetProcessesByName(process).Select(p => { using (p) return (uint)p.Id; }).ToHashSet();
        if (pids.Count == 0 || Device(name) is not { } device) return found;
        var iid = typeof(IAudioSessionManager2).GUID;
        device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object manager);
        ((IAudioSessionManager2)manager).GetSessionEnumerator(out var sessions);
        sessions.GetCount(out int count);
        for (int i = 0; i < count; i++)
        {
            sessions.GetSession(i, out object session);
            if (session is IAudioSessionControl2 control && control.GetProcessId(out uint pid) == 0 && pids.Contains(pid))
                found.Add((ISimpleAudioVolume)session);
        }
        return found;
    }

    private IAudioEndpointVolume? Endpoint(string name)
    {
        if (_endpoint is not null && name == _name) return _endpoint;
        _endpoint = null;
        _name = name;
        if (Device(name) is not { } device) return null;
        var iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object volume);
        return _endpoint = (IAudioEndpointVolume)volume;
    }

    private static IMMDevice? Device(string name)
    {
        var devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        devices.EnumAudioEndpoints(EDataFlowRender, DeviceStateActive, out var list);
        list.GetCount(out int count);
        for (int i = 0; i < count; i++)
        {
            list.Item(i, out var device);
            if (FriendlyName(device) == name) return device;
        }
        // No such card, or none named: Oxide then plays on the Windows default, and so is this.
        return devices.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out var fallback) == 0 ? fallback : null;
    }

    private static string FriendlyName(IMMDevice device)
    {
        device.OpenPropertyStore(StgmRead, out var store);
        var key = new PropertyKey { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };
        store.GetValue(ref key, out var value);
        try { return Marshal.PtrToStringUni(value.Pointer) ?? ""; }
        finally { PropVariantClear(ref value); }
    }

    private const int EDataFlowRender = 0, ERoleMultimedia = 1, DeviceStateActive = 1, ClsCtxAll = 23, StgmRead = 0;

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public int PropertyId; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant { [FieldOffset(0)] public short Type; [FieldOffset(8)] public IntPtr Pointer; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        void GetCount(out int count);
        void Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(int access, out IPropertyStore store);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    // The eleven methods ahead of SetMute hold their vtable slots; order is the contract.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify();
        void UnregisterControlChangeNotify();
        void GetChannelCount();
        void SetMasterVolumeLevel();
        void SetMasterVolumeLevelScalar();
        void GetMasterVolumeLevel();
        void GetMasterVolumeLevelScalar();
        void SetChannelVolumeLevel();
        void SetChannelVolumeLevelScalar();
        void GetChannelVolumeLevel();
        void GetChannelVolumeLevelScalar();
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // IAudioSessionManager's two methods hold their slots ahead of the enumerator.
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        void GetAudioSessionControl();
        void GetSimpleAudioVolume();
        void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    // IAudioSessionControl's nine methods, then two identifiers, hold their slots.
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        void GetState();
        void GetDisplayName();
        void SetDisplayName();
        void GetIconPath();
        void SetIconPath();
        void GetGroupingParam();
        void SetGroupingParam();
        void RegisterAudioSessionNotification();
        void UnregisterAudioSessionNotification();
        void GetSessionIdentifier();
        void GetSessionInstanceIdentifier();
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, ref Guid eventContext);
        void GetMasterVolume(out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
