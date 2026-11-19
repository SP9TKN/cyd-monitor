namespace CydMonitor;

/// <summary>
/// Mirror of firmware/cyd-monitor/protocol.h. The two files are the same
/// contract written twice, so any change here needs the matching change there
/// and a bump of <see cref="Version"/>; the handshake rejects a mismatch
/// rather than letting the device render a misparsed stream.
/// </summary>
internal static class Proto
{
    public const byte Version = 4;
    public const int  Port    = 7788;
    public const int  DiscoveryPort = Port - 1;

    public const int W = 320;
    public const int H = 240;

    /// <summary>
    /// Rows reserved at the bottom of the panel in Stretch mode, so the
    /// stats overlay (fps/kB/s/dBm/IP, drawStats() in the firmware) has a
    /// clear strip to sit on instead of directly over whatever desktop
    /// content Stretch would otherwise fill that row with. The server can't
    /// tell whether the overlay is actually on — that toggle is device-local
    /// and never reported upstream — so the margin is unconditional in
    /// Stretch; it costs a thin unused strip when the overlay is off, which
    /// is cheaper than text becoming unreadable over a busy desktop when
    /// it's on. Fit/OneToOne don't need this: neither ever guarantees the
    /// bottom row is desktop content in the first place (Fit letterboxes,
    /// OneToOne is a small tracking crop), so only Stretch (the one mode
    /// that fills every row on purpose) gets carved into.
    /// </summary>
    public const int StatsMarginPx = 12;

    public const int TileW  = 16;
    public const int TileH  = 16;
    public const int TilesX = W / TileW;   // 20
    public const int TilesY = H / TileH;   // 15

    // device -> server
    public const byte MsgHello = 0x01;
    public const byte MsgAck   = 0x02;
    public const byte MsgTouch = 0x03;
    public const byte MsgOpt   = 0x04;
    public const byte MsgPing  = 0x05;
    public const byte MsgDiag  = 0x06;
    /// <summary>u16 udpPort. Only ever sent after a CONFIG carrying <see cref="ConfigFlagUdp"/>: a v4 server closes the session on a message type it does not know.</summary>
    public const byte MsgUdpHello = 0x07;
    /// <summary>u8 cmd (ScanCmd*). A button on the scanner page.</summary>
    public const byte MsgScanCmd = 0x08;

    // server -> device
    public const byte MsgConfig = 0x81;
    public const byte MsgFrame  = 0x82;
    public const byte MsgOptAck = 0x83;
    public const byte MsgText   = 0x84;
    public const byte MsgJpegTables = 0x85;
    /// <summary>u8 mode (0 = pixels over TCP, 1 = pixels over UDP), u16 session, u16 serverUdpPort. Only sent to a device that sent <see cref="MsgUdpHello"/>.</summary>
    public const byte MsgTransport = 0x86;
    /// <summary>u8 flags, then <see cref="ScanRadios"/> records of <see cref="ScanRecBytes"/>. Sent only between OPT_SCANNER 1 and 0.</summary>
    public const byte MsgScan = 0x87;
    /// <summary>u8 open: a transmission started (1) or ended (0), so the panel opens or closes the scanner page by itself.</summary>
    public const byte MsgScanPop = 0x88;

    /// <summary>CONFIG flag bit 0: this server can carry pixels over UDP (protocol v5 data plane).</summary>
    public const byte ConfigFlagUdp = 0x01;

    public const int ScanRadios    = 2;
    public const int ScanNameBytes = 32;
    public const int ScanFreqBytes = 16;
    /// <summary>u8 state, i16 dBm, name, frequency; both strings ASCII, NUL padded.</summary>
    public const int ScanRecBytes  = 1 + 2 + ScanNameBytes + ScanFreqBytes;
    public const byte ScanOffline = 0;
    public const byte ScanRunning = 1;
    public const byte ScanHold    = 2;
    public const byte ScanFlagMuted = 0x01;
    /// <summary>Oxide's own mute on radio 0; the next bit is radio 1.</summary>
    public const byte ScanFlagRadio0Off = 0x02;
    public const byte ScanCmdStart = 1;
    public const byte ScanCmdStop  = 2;
    public const byte ScanCmdNext  = 3;
    public const byte ScanCmdMute  = 4;
    /// <summary>Plus the radio: turn that radio's own sound on or off.</summary>
    public const byte ScanCmdSound = 5;

    // UDP data plane, see docs/protocol-v5-udp.pl.md. The server listens for
    // acknowledgements on UdpDataPort; the device listens on UdpDevicePort.
    public const int UdpDataPort   = Port + 1;
    public const int UdpDevicePort = Port + 2;

    public const byte DgData = 0xD1;
    public const byte DgAck  = 0xD2;
    public const byte DgFlagRepaint  = 0x01;
    public const byte DgFlagFrameEnd = 0x02;
    /// <summary>type, flags, u16 session, u32 seq, u8 rectCount.</summary>
    public const int DgHeaderBytes = 9;
    /// <summary>u8 tx, u8 ty, u8 tw, u8 mode, u16 len.</summary>
    public const int DgRectHeaderBytes = 6;
    /// <summary>Largest datagram: 1500 MTU minus IP and UDP headers, with margin, so it is never fragmented.</summary>
    public const int DgMaxBytes = 1400;
    /// <summary>type, flags, u16 session, u32 highest, u32 bitmap, u16 fps10, u16 blitMs, u8 freeSlots, u8 rxDrops.</summary>
    public const int DgAckBytes = 18;

    public const byte TouchDown = 0;
    public const byte TouchMove = 1;
    public const byte TouchUp   = 2;

    public const byte OptBrightness = 1;
    public const byte OptContrast   = 2;
    public const byte OptGamma      = 3;
    public const byte OptScaleMode  = 4;
    public const byte OptSrcRes     = 5;
    public const byte OptDither     = 6;
    public const byte OptPause      = 7;
    public const byte OptRefresh    = 8;
    public const byte OptStats      = 9;
    public const byte OptSharpen    = 10;
    public const byte OptScanner    = 11;

    /// <summary>u16 x, u16 y, u16 w, u16 h, u8 mode, u32 len.</summary>
    public const int StripHeaderBytes = 13;   // 2+2+2+2+1+4

    public const byte EncSolid = 0;
    public const byte EncRaw   = 1;
    public const byte EncRle   = 2;
    public const byte EncPal1  = 3;
    public const byte EncPal4  = 4;
    public const byte EncJpeg  = 5;

    /// <summary>Longest ENC_JPEG scan the device can rebuild a header around; mirrors JPEG_MAX_SCAN.</summary>
    public const int JpegMaxScan = W * TileH * 2 - 640 - 2;

    /// <summary>
    /// Resolutions offered to the device's menu, and the order it indexes
    /// them by. All 4:3 so the downscale to 320x240 never letterboxes, and
    /// preferring exact integer multiples of 320x240 so the box filter
    /// averages whole pixel groups instead of interpolating across them.
    /// </summary>
    public static readonly (int W, int H)[] ResTable =
    [
        (320, 240), (640, 480), (800, 600), (1024, 768), (1280, 960),
    ];
}

internal enum ScaleMode : byte
{
    /// <summary>Whole source, aspect preserved, black bars if it doesn't divide evenly.</summary>
    Fit = 0,
    /// <summary>Whole source stretched to fill 320x240 regardless of aspect.</summary>
    Stretch = 1,
    /// <summary>No scaling: a 320x240 window of the source, panning with the cursor.</summary>
    OneToOne = 2,
}
