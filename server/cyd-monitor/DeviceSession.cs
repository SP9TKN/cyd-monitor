using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CydMonitor;

/// <summary>
/// One connected panel: captures, encodes and sends frames, and applies what
/// comes back.
/// </summary>
internal sealed class DeviceSession : IDisposable
{
    private readonly TcpClient     _client;
    private readonly NetworkStream _stream;
    private readonly Options       _opt;
    private readonly IFrameSource  _source;
    private readonly FrameProcessor _proc  = new();
    private readonly TileEncoder   _enc    = new();

    private readonly ushort[] _cur  = new ushort[Proto.W * Proto.H];
    private readonly ushort[] _prev = new ushort[Proto.W * Proto.H];
    private readonly byte[]   _tx   = new byte[11 + 256 * 1024];

    // Holds _cur after the pan/zoom crop below.
    private readonly ushort[] _panZoomed = new ushort[Proto.W * Proto.H];
    private readonly byte[]   _panZoomedRgb = new byte[Proto.W * Proto.H * 3];   // same crop, pre-sharpen 8-bit, for JPEG

    // User-requested: touch no longer clicks into Windows at all (see
    // HandleTouch) — instead a one-finger drag pans, and a press held still
    // steps through ZoomLevels. This is a genuine gesture layer, not a setting,
    // because the touch hardware (XPT2046, resistive) only ever reports one
    // contact point: a real two-finger pinch is not something this panel can
    // physically sense, which is why zoom is a discrete step instead. A
    // double-tap used to do it; that gesture now belongs to the panel itself,
    // which opens the scanner page with it (see pollTouch in the firmware).
    // The crop+rescale happens on the already-320x240 _cur buffer
    // (see ApplyPanZoom) rather than by asking the capture backends for a
    // different source rectangle, so it costs no protocol or firmware
    // change, at the price of zooming into pixels DXGI already downscaled
    // once — a software zoom, not a true optical one.
    private static readonly double[] ZoomLevels = [1.0, 2.0, 4.0];
    private readonly object _panLock = new();
    private double _panX = Proto.W / 2.0, _panY = Proto.H / 2.0;   // center of current crop, in _cur's own 320x240 space
    private int _zoomIdx;

    // Drag-in-progress state, touch thread only (ReadLoopAsync), so these
    // need no locking themselves — only the _panX/_panY they write back go
    // through _panLock.
    private bool _dragging;
    private int _dragStartPx, _dragStartPy;
    private double _dragStartPanX, _dragStartPanY;

    // Long-press detection for the zoom step, touch thread only.
    private long _touchDownMs;
    private const int LongPressMs = 700;
    private const int TapSlopPx = 24;

    // The scanner page's data, and what opens that page over the picture.
    private ScannerBridge? _scanner;

    private MonitorTarget _monitor;
    private int _resIdx;

    // Written by the inbound reader, read every iteration of the frame loop.
    // Without volatile the JIT is free to hoist these reads out of that loop,
    // at which point a setting changed from the panel menu would never take
    // effect -- and a skipped frame would never be retried.
    private volatile ScaleMode _scale;
    private volatile bool _paused;
    private volatile bool _forceFull = true;
    private bool _wasBlack;   // frame loop only; edge-detects the probe above
    private int _refreshRequests;   // OPT_REFRESH received since the last stats line
    private volatile uint _ackedSeq;
    private volatile uint _deviceFps10;
    private uint _sentSeq;   // frame loop only
    // Set by the inbound reader when the OSD releases the stream, consumed by
    // the frame loop on its own thread — see the note where it's read.
    // Opening the OSD discards whatever strips were already queued on the
    // device (osdPauseStream(true) in the firmware), including their
    // JOB_FRAME_END markers, so those frames' acks never arrive. If two or
    // more were in flight when that happened, _sentSeq - _ackedSeq is left
    // permanently >= 2 and the flow-control check below would block every
    // future frame for the rest of the connection.
    private volatile bool _resumeAckReset;

    // LastSrc/LastDst are written by the frame loop and read by the inbound
    // reader when mapping a touch. A RECT is bigger than a word, so an
    // unsynchronised read can tear and land a tap in the wrong place; both
    // sides go through this lock instead.
    private readonly object _geomLock = new();
    private Native.RECT _geomSrc, _geomDst;

    private readonly CancellationTokenSource _cts = new();

    // Protocol v5 data plane (docs/protocol-v5-udp.pl.md). The reader thread builds the link when the
    // panel offers UDP (UDPHELLO) and parks it in _udpOffer; the frame loop takes it up between two
    // frames, so that every TCP frame sent before the switch precedes the TRANSPORT message, and
    // the encoder's settings are only ever changed by the thread that runs it.
    private volatile UdpLink? _udpOffer;
    private UdpLink? _udp;                     // set by the frame loop when it takes the offer up, then read by the probe loop
    private volatile bool _udpActive;          // pixels go over UDP while true
    private readonly List<StripRef> _strips = new();
    private const int UdpFrameBytes = 16 * 1024;   // the largest frame on the UDP path; the rate control paces the rest

    // Session-health diagnostics (see ProbeLoopAsync): when the panel last said anything, when
    // the server last got bytes onto the wire, and when the frame loop last went round. All
    // Environment.TickCount64, written by the thread that owns the event and only ever read
    // for the log, so plain volatile writes are enough.
    private long _lastRxTick, _lastTxTick, _loopTick;
    // Touch from the panel forces a full repaint on every move (see HandleTouch), which is by far
    // the biggest thing a user can do to the link - so the log says when the last one was.
    private long _lastTouchTick, _touchTotal;
    private readonly long _sessionStartTick = Environment.TickCount64;
    // The last few TCP readings (one per 0.5 s), dumped when the session ends.
    private readonly (long At, string Text)[] _tcpRing = new (long, string)[10];
    private int _tcpRingPos;
    private const long StallLogMs = 2500;   // silence from the panel worth a line of its own
    // When each of the last 64 frames was written, by sequence number: the age of the oldest
    // frame the panel has not acknowledged is what a stutter looks like from the server.
    private readonly long[] _sendTick = new long[64];
    private const long DipMs = 250;   // an unacknowledged frame this old is a visible stutter

    public DeviceSession(TcpClient client, MonitorTarget monitor, Options opt)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        _monitor = monitor;
        _source = opt.Synthetic ? new SyntheticSource() : FrameSourceFactory.Create(monitor.Device, opt.ForceGdi);
        _opt = opt;

        _scale = opt.Scale;
        _proc.Contrast = opt.Contrast;
        _proc.Gamma = opt.Gamma;
        _proc.Sharpen = opt.Sharpen;
        _proc.Dither = opt.Dither;
        _source.CursorSize = opt.CursorSize;
        // Let the capture wait for the compositor's next present for most of
        // a send interval, instead of looking for 10 ms on a fixed grid. A
        // video's frames don't arrive exactly on that grid: measured with a
        // film playing, the desktop presented 22-26 times a second while the
        // loop sent 19-22 — two presents landing in one interval lost one,
        // an interval with none cost a "nothing new". Waiting instead catches
        // each present as it happens, so the send rate follows the source up
        // to the configured cap. Still returns within an interval on a static
        // desktop, so the keepalive and pause checks stay prompt.
        _source.AcquireTimeoutMs = Math.Max(10, (int)(1000.0 / _opt.TargetFps) - 3);
        _enc.MaxFrameBytes = _opt.FrameKb * 1024;
        if (_opt.JpegQuality > 0) _enc.Jpeg = new JpegEncoder(_opt.JpegQuality);
        _resIdx = NearestResIndex(Monitors.CurrentResolution(_monitor.Device));
    }

    public async Task RunAsync()
    {
        if (!await HandshakeAsync()) return;

        SendText($"{_monitor.Device} {_monitor.Bounds.Width}x{_monitor.Bounds.Height} -> 320x240");
        Console.WriteLine($"  przechwytywanie: {_source.Kind}");

        long start = Environment.TickCount64;
        Volatile.Write(ref _lastRxTick, start);
        Volatile.Write(ref _lastTxTick, start);
        Volatile.Write(ref _loopTick, start);

        _scanner = new ScannerBridge(TrySend);
        ScannerWeb.PanelUp(this, ((IPEndPoint)_client.Client.RemoteEndPoint!).Address);
        var reader = Task.Run(() => ReadLoopAsync(_cts.Token));
        var frames = Task.Run(() => FrameLoop(_cts.Token));
        var probe  = Task.Run(() => ProbeLoopAsync(_cts.Token));
        var finished = await Task.WhenAny(reader, frames);
        // TODO(diagnostics): temporary, tracking down an unexplained reconnect
        // loop — remove once root-caused.
        Console.WriteLine($"\n  [diag] sesja konczy sie bo: {(finished == reader ? "ReadLoop" : "FrameLoop")}");
        DumpSessionEnd();
        _cts.Cancel();
        await Task.WhenAll(
            reader.ContinueWith(_ => { }, TaskScheduler.Default),
            frames.ContinueWith(_ => { }, TaskScheduler.Default),
            probe.ContinueWith(_ => { }, TaskScheduler.Default));
    }

    /// <summary>
    /// Watches for the panel going quiet. The panel acknowledges every frame, keepalives
    /// included, so it is heard from at least every ~1.5 s; silence past
    /// <see cref="StallLogMs"/> is a stall in one direction or the other. Each stall is logged
    /// as it happens - with what the server was doing and what TCP saw - instead of only
    /// after the fact, when the session is already gone. Also keeps the ring of TCP readings
    /// that <see cref="DumpSessionEnd"/> prints.
    /// </summary>
    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        bool stalled = false;
        int tick = 0;
        // The stretch being tracked while the oldest unacknowledged frame is DipMs old or more.
        bool inDip = false;
        long dipStart = 0, maxLoopAge = 0;
        uint dipFrames = 0, minWnd = 0, maxFlight = 0, maxRtt = 0;
        TcpSnapshot s0 = default;
        // The same stretch when pixels go over UDP: counters at its start, to print what it cost.
        long u0Lost = 0, u0Sent = 0, u0Resent = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
                long now = Environment.TickCount64;
                bool haveTcp = TcpProbe.TryReadSnapshot(_client.Client, out var snap);
                string tcp = haveTcp ? snap.ToString() : "brak danych TCP";
                if (++tick % 5 == 0)   // the session-end ring keeps one reading per 0.5 s
                    lock (_tcpRing) _tcpRing[_tcpRingPos++ % _tcpRing.Length] = (now, tcp);

                // A stutter as the server sees it: the panel acknowledges each frame within a
                // few ms when all is well, so a frame that stays unacknowledged for DipMs is the
                // picture standing still. What TCP did meanwhile says where the time went - bytes
                // still in flight, retransmissions or timeouts point at the link; a window of
                // zero means the panel stopped reading; a healthy TCP with the acknowledgement
                // still missing means the panel's own code was slow to draw and answer.
                //
                // Over UDP there is no frame to wait for; the same picture-standing-still is the
                // panel staying silent while it owes an acknowledgement (UdpEngine.AckSilenceUs).
                UdpEngine? ue = _udpActive ? _udp?.Engine : null;
                uint outstanding = _sentSeq - _ackedSeq;
                long age = outstanding is > 0 and < 64
                    ? now - Volatile.Read(ref _sendTick[(_ackedSeq + 1) & 63])
                    : 0;
                if (ue is not null)
                {
                    outstanding = (uint)ue.InFlight;
                    age = ue.AckSilenceUs(UdpLink.NowUs()) / 1000;
                    if (tick % 300 == 0) Console.WriteLine($"\n  [udp] {UdpStatText(ue)}");
                }
                if (age >= DipMs)
                {
                    if (!inDip)
                    {
                        inDip = true;
                        dipStart = now - age;
                        s0 = snap;
                        minWnd = snap.SndWnd; maxFlight = snap.InFlight; maxRtt = snap.RttMs;
                        maxLoopAge = 0; dipFrames = outstanding;
                        if (ue is not null) { u0Lost = ue.LostDatagrams; u0Sent = ue.SentDatagrams; u0Resent = ue.ResentTiles; }
                    }
                    if (haveTcp)
                    {
                        minWnd = Math.Min(minWnd, snap.SndWnd);
                        maxFlight = Math.Max(maxFlight, snap.InFlight);
                        maxRtt = Math.Max(maxRtt, snap.RttMs);
                    }
                    maxLoopAge = Math.Max(maxLoopAge, now - Volatile.Read(ref _loopTick));
                    dipFrames = Math.Max(dipFrames, outstanding);
                }
                else if (inDip)
                {
                    inDip = false;
                    if (ue is not null)
                    {
                        Console.WriteLine($"\n  [stale] {now - dipStart} ms bez potwierdzenia panelu; datagramow w locie {dipFrames}; " +
                                          $"UDP: wyslano +{ue.SentDatagrams - u0Sent}, uznano za zgubione +{ue.LostDatagrams - u0Lost}, " +
                                          $"kafli do ponowienia +{ue.ResentTiles - u0Resent}, rtt {ue.SrttUs / 1000.0:F0} ms; " +
                                          $"petla klatek max {maxLoopAge} ms");
                    }
                    else
                    {
                        string delta = haveTcp
                            ? $"retrans +{snap.Retrans - s0.Retrans} B, timeouty +{snap.Timeouts - s0.Timeouts}, dup +{snap.DupAcks - s0.DupAcks}"
                            : "retrans ?";
                        Console.WriteLine($"\n  [dip] {now - dipStart} ms bez potwierdzenia panelu; w locie ramek {dipFrames}; " +
                                          $"TCP: okno panelu min {minWnd}, bajtow w locie max {maxFlight}, {delta}, rtt max {maxRtt} ms; " +
                                          $"petla klatek max {maxLoopAge} ms");
                    }
                }

                long quiet = now - Volatile.Read(ref _lastRxTick);
                if (!stalled && quiet >= StallLogMs)
                {
                    stalled = true;
                    Console.WriteLine($"\n  [diag] panel milczy od {quiet} ms; {StateText(now)}; TCP: {tcp}");
                }
                else if (stalled && quiet < StallLogMs)
                {
                    stalled = false;
                    Console.WriteLine("\n  [diag] panel znow odpowiada");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is ObjectDisposedException or SocketException) { }
    }

    /// <summary>The data plane in one line, for the stats line and the periodic [udp] summary.</summary>
    private static string UdpStatText(UdpEngine e) =>
        $"udp: {e.SentBytes / 1024} kB, strat {e.LostDatagrams}/{e.SentDatagrams} ({100 * e.LossShare:F1}% teraz), " +
        $"ponowione kafle {e.ResentTiles}, rtt {e.SrttUs / 1000.0:F0} ms, tempo {e.RateBytesPerSec / 1024:F0} kB/s, okno {e.Cwnd:F0}, " +
        $"panel: wolnych {e.PanelFree}, odrzucil {e.PanelDrops}, blit {e.PanelBlitMs} ms";

    private string StateText(long now) =>
        $"petla klatek {now - Volatile.Read(ref _loopTick)} ms temu, ostatni zapis {now - Volatile.Read(ref _lastTxTick)} ms temu, " +
        $"w locie {_sentSeq - _ackedSeq} (wyslano {_sentSeq}, potwierdzono {_ackedSeq}); " +
        $"dotyk: {(Volatile.Read(ref _touchTotal) == 0 ? "brak" : $"ostatni {now - Volatile.Read(ref _lastTouchTick)} ms temu, lacznie {Volatile.Read(ref _touchTotal)}")}";

    /// <summary>What the last seconds looked like, printed when the session ends.</summary>
    private void DumpSessionEnd()
    {
        long now = Environment.TickCount64;
        TcpProbe.TryRead(_client.Client, out string tcpNow);
        var sb = new StringBuilder();
        sb.Append($"\n  [diag] koniec sesji po {(now - _sessionStartTick) / 1000} s; cisza od panelu {now - Volatile.Read(ref _lastRxTick)} ms; ");
        sb.Append(StateText(now));
        sb.Append($"\n         TCP teraz: {tcpNow}");
        lock (_tcpRing)
        {
            for (int k = 0; k < _tcpRing.Length; k++)
            {
                var (at, text) = _tcpRing[(_tcpRingPos + k) % _tcpRing.Length];
                if (text is null) continue;
                sb.Append($"\n         {(at - now) / 1000.0,5:F1} s  {text}");
            }
        }
        Console.WriteLine(sb.ToString());
    }

    // ---------------------------------------------------------------- handshake

    private async Task<bool> HandshakeAsync()
    {
        var hdr = new byte[1];
        await _stream.ReadExactlyAsync(hdr, _cts.Token);
        if (hdr[0] != Proto.MsgHello)
        {
            Console.WriteLine("  odrzucone: pierwsza wiadomosc to nie HELLO");
            return false;
        }
        var body = new byte[10];
        await _stream.ReadExactlyAsync(body, _cts.Token);

        byte ver = body[0];
        int w = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1, 2));
        int h = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(3, 2));
        uint heap = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5, 4));
        byte resetReason = body[9];

        if (ver != Proto.Version)
        {
            // Refusing beats rendering: a device built against a different wire
            // format would misparse strip headers and paint noise, which looks
            // like a hardware fault rather than a version skew.
            Console.WriteLine($"  odrzucone: protokol urzadzenia v{ver}, serwer v{Proto.Version} — przeflashuj");
            return false;
        }
        if (w != Proto.W || h != Proto.H)
        {
            Console.WriteLine($"  odrzucone: panel {w}x{h}, oczekiwano {Proto.W}x{Proto.H}");
            return false;
        }

        Console.WriteLine($"  panel {w}x{h}, wolny RAM {heap / 1024} kB, protokol v{ver}, start: {ResetReasonText(resetReason)}");

        var cfg = new byte[8];
        cfg[0] = Proto.MsgConfig;
        BinaryPrimitives.WriteUInt16LittleEndian(cfg.AsSpan(1, 2), (ushort)_monitor.Bounds.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(cfg.AsSpan(3, 2), (ushort)_monitor.Bounds.Height);
        cfg[5] = Proto.TileW;
        cfg[6] = Proto.TileH;
        // Offering UDP is what makes a v5 panel send UDPHELLO; a v4 panel ignores the byte.
        cfg[7] = _opt.Udp ? Proto.ConfigFlagUdp : (byte)0;
        lock (_tx) _stream.Write(cfg);

        // Before any frame, so the first ENC_JPEG strip never reaches a device
        // that has no tables to rebuild its header from.
        if (_enc.Jpeg is { } jpeg)
        {
            var t = new byte[3 + jpeg.Tables.Length];
            t[0] = Proto.MsgJpegTables;
            BinaryPrimitives.WriteUInt16LittleEndian(t.AsSpan(1, 2), (ushort)jpeg.Tables.Length);
            jpeg.Tables.CopyTo(t, 3);
            lock (_tx) _stream.Write(t);
            Console.WriteLine($"  JPEG dla filmu/zdjec: jakosc {jpeg.Quality}");
        }
        return true;
    }

    // ---------------------------------------------------------------- UDP data plane

    /// <summary>The panel asked for UDP (reader thread): open the socket and park the link for the frame loop.</summary>
    private void OfferUdp(int panelPort)
    {
        if (!_opt.Udp || _udpOffer is not null || _udpActive) return;
        int serverPort = _opt.Port + 1;
        try
        {
            var ip = ((IPEndPoint)_client.Client.RemoteEndPoint!).Address;
            _udpOffer = new UdpLink(ip, panelPort, serverPort, (ushort)Random.Shared.Next(1, 65536));
        }
        catch (SocketException e)
        {
            Console.WriteLine($"\n  [udp] nie moge otworzyc gniazda UDP {serverPort}: {e.Message} - dane zostaja na TCP");
            SendTransport(0, 0, 0);
        }
    }

    /// <summary>Frame loop: start sending pixels over the parked link. TCP frames already written come first on the wire, so the panel has them before it hears TRANSPORT.</summary>
    private void ActivateUdp()
    {
        var link = _udpOffer!;
        _udpOffer = null;
        _udp = link;
        _enc.SplitLimit = Proto.DgMaxBytes - Proto.DgHeaderBytes - Proto.DgRectHeaderBytes;
        _enc.MaxRunTiles = 10;
        _enc.MaxFrameBytes = Math.Min(_opt.FrameKb * 1024, UdpFrameBytes);
        _forceFull = true;   // whatever TCP left on the panel, the UDP stream starts from a full picture
        SendTransport(1, link.Session, _opt.Port + 1);
        link.Start();
        _udpActive = true;
        Console.WriteLine($"\n  [udp] piksele przez UDP: serwer :{link.LocalPort}, panel {link.PanelEndPoint}, sesja {link.Session}");
    }

    /// <summary>Frame loop: no acknowledgements for seconds, so pixels go back over TCP for the rest of this connection.</summary>
    private void FallBackToTcp(string why)
    {
        _udpActive = false;
        _enc.SplitLimit = 0;
        _enc.MaxRunTiles = Proto.TilesX;
        _enc.MaxFrameBytes = _opt.FrameKb * 1024;
        _forceFull = true;   // anything still in flight over UDP is written off
        SendTransport(0, 0, 0);
        var link = _udp;
        _udp = null;
        link?.Dispose();
        Console.WriteLine($"\n  [udp] {why} - wracam na TCP");
    }

    private void SendTransport(byte mode, ushort session, int serverPort)
    {
        var m = new byte[6];
        m[0] = Proto.MsgTransport;
        m[1] = mode;
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(2, 2), session);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(4, 2), (ushort)serverPort);
        TrySend(m);
    }

    // ---------------------------------------------------------------- frame loop

    // unsafe only here: the capture surface is handed over as a raw pointer, and
    // `await` is not allowed in an unsafe context, so the class itself must not be.
    private unsafe void FrameLoop(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double frameMs = 1000.0 / _opt.TargetFps;
        double next = 0;

        // Going quiet for 4s trips the device's own inbound-read timeout (see
        // readExact in the firmware), which tears the TCP link down and
        // reconnects — forcing a full repaint that, if it lands mid-frame, is
        // the most likely source of the tearing/"lines" seen after a
        // reconnect. Both a static desktop and the OSD being held open to
        // browse the menu go quiet for far longer than that, so a periodic
        // zero-strip frame keeps the socket active without touching the
        // picture: the firmware's handleFrame() already handles strips=0 by
        // doing nothing but acking, since it was written to tolerate a frame
        // whose encode happened to find no dirty tiles.
        const double IdleKeepaliveMs = 1500;
        const int HeldPollMs = 20;
        double lastSentMs = sw.Elapsed.TotalMilliseconds;

        double lastSweepMs = 0;   // UDP only, see the sweep below
        int    sweepRow = 0;

        long statAt = 0;
        int  statFrames = 0;
        long statBytes = 0;
        long statStrips = 0;
        long statJpeg = 0;
        // What the frame rate is actually waiting on, per stats window: time
        // spent held at the flow-control gate (the panel hasn't acked yet —
        // its buffers are full or it is still drawing), and captures that
        // found nothing new (the source itself presented no new image).
        double gateMs = 0, gateSince = -1;
        int statNoChange = 0;
        // How often the desktop itself was redrawn, from DXGI's own count —
        // the ceiling on distinct frames there is to send, whatever our rate.
        int presentsAt = 0;
        int PresentsDelta()
        {
            if (_source is not DuplicationSource d) return 0;
            int n = d.PresentsSeen - presentsAt;
            presentsAt = d.PresentsSeen;
            return n;
        }
        double worstMs = 0;

        bool Send(int stripCount, int len, double at)
        {
            _tx[0] = Proto.MsgFrame;
            uint seq = ++_sentSeq;
            Volatile.Write(ref _sendTick[seq & 63], Environment.TickCount64);
            BinaryPrimitives.WriteUInt32LittleEndian(_tx.AsSpan(1, 4), seq);
            BinaryPrimitives.WriteUInt16LittleEndian(_tx.AsSpan(5, 2), (ushort)stripCount);
            BinaryPrimitives.WriteUInt32LittleEndian(_tx.AsSpan(7, 4), (uint)len);
            if (len > 0) _enc.Payload.CopyTo(_tx.AsSpan(11));

            try
            {
                // One write per frame. Split writes would let Nagle interleave
                // a partial frame with the next one's header and stall for an
                // ACK in the middle of a picture.
                long w0 = Environment.TickCount64;
                lock (_tx) _stream.Write(_tx, 0, 11 + len);
                long w1 = Environment.TickCount64;
                Volatile.Write(ref _lastTxTick, w1);
                // A write that blocks means the kernel could not take the bytes: the send
                // buffer is full because the panel is not acknowledging (or not reading).
                if (w1 - w0 >= 1000)
                    Console.WriteLine($"\n  [diag] zapis {11 + len} B do panelu trwal {w1 - w0} ms; w locie {_sentSeq - _ackedSeq}");
                lastSentMs = at;
                return true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // TODO(diagnostics): temporary, tracking down an unexplained
                // reconnect loop — remove once root-caused.
                Console.WriteLine($"\n  [diag] FrameLoop zapis nie powiodl sie: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        while (!ct.IsCancellationRequested && _client.Connected)
        {
            Volatile.Write(ref _loopTick, Environment.TickCount64);

            // The transport only ever changes here, between two frames.
            if (_udpOffer is not null && !_udpActive) ActivateUdp();
            else if (_udpActive && _udp!.Engine.IsFailed(UdpLink.NowUs()))
                FallBackToTcp("brak potwierdzen panelu przez 3 s");
            bool udp = _udpActive;
            if (_udp is { } link) link.Idle = _paused;

            double now = sw.Elapsed.TotalMilliseconds;

            // With the stream held there is nothing to pace, so this comes ahead of the schedule.
            // A repaint left owing rows when the hold began stays owing, and the rule below would
            // keep the deadline at "now": a loop going flat out to send nothing, one whole core
            // for as long as the scanner page is open.
            if (_paused)
            {
                // Nothing is being captured, but the device's timeout doesn't
                // know that — it only sees a socket that stopped talking.
                if (now - lastSentMs >= IdleKeepaliveMs && !Send(0, 0, now)) break;
                Thread.Sleep(HeldPollMs);
                next = sw.Elapsed.TotalMilliseconds;
                continue;
            }

            if (now < next)
            {
                // Sleep in whole milliseconds and spin the remainder: Thread
                // Sleep's resolution is a scheduler tick, and rounding a 33 ms
                // period up to the next tick is what turns a steady 30 fps into
                // a visibly uneven 25.
                int ms = (int)(next - now) - 1;
                if (ms > 0) Thread.Sleep(ms);
                while (sw.Elapsed.TotalMilliseconds < next) Thread.SpinWait(60);
                now = sw.Elapsed.TotalMilliseconds;
            }
            // A repaint still owing rows goes out as fast as the device will
            // ack it, rather than one 16 KB piece per send interval. Flow
            // control still applies (see the _sentSeq/_ackedSeq gate below),
            // so this can't outrun the panel — it only stops the server
            // idling between pieces of a picture the user is waiting to see.
            //
            // Otherwise the next deadline follows the schedule, not the moment
            // this iteration happened to wake. Counting from "now" added every
            // wake-up's lateness to the period that followed it, and with the
            // default ~15.6 ms scheduler tick a Sleep(24) can return at 31:
            // 25 fps asked for, ~21.7 measured, panel and link both idle. A
            // late frame is now made up on the next one; only when a whole
            // interval has been lost does the schedule restart from here, so
            // a stall can't turn into a burst of frames back to back.
            if (_enc.RepaintPending) next = now;
            else
            {
                next = Math.Max(next, now - frameMs) + frameMs;
            }

            // The OSD closing (or any setting that invalidates the reference
            // frame) can leave frames that were already in flight when it
            // opened permanently un-acked — see the note next to
            // _resumeAckReset. Catching up here, on the thread that owns
            // _sentSeq, is what keeps flow control below from wedging shut
            // for the rest of the connection.
            if (_resumeAckReset)
            {
                _resumeAckReset = false;
                _ackedSeq = _sentSeq;
            }

            // Flow control: at most _opt.InFlight frames awaiting an ack. The
            // window bounds throughput at (frames in flight x bytes per frame)
            // per round trip, and that round trip is this link's real limit —
            // not a steady figure but one that swings from ~90 ms to 445+ ms
            // under load. A depth chosen on a calm measurement (2, then 3,
            // neither moving the needle at the time) later measured stuck
            // waiting on the panel's own ack 88-93% of the time, on a desktop
            // that was barely changing at all: the round trip had grown past
            // what that depth could ride out. 6 rides out the variance seen
            // since; it costs latency on a touchscreen if that round trip
            // stays long, but a slow, laggy picture beats one that mostly
            // isn't arriving. Skipping without encoding leaves _prev
            // untouched, so the next frame's diff simply covers both changes
            // — no resync needed.
            //
            // Over UDP the gate is the engine's: room in its window and no backlog of datagrams
            // still waiting for their turn on the wire. Tiles that do not fit simply stay dirty.
            if (udp ? !_udp!.Engine.CanAccept() : _sentSeq - _ackedSeq >= (uint)_opt.InFlight)
            {
                if (gateSince < 0) gateSince = now;
                // Held shut this long, send a keepalive anyway. Every other
                // branch here has one — paused, no picture, nothing changed —
                // this one didn't, on the reasoning that an ack was always
                // close behind. It measured otherwise: touch and drag load
                // the device's own uplink (its acks share one small queue
                // with its touch reports — see upQ in the firmware), and a
                // round trip that spikes past a few hundred ms empties this
                // gate slower than assumed. Either holds it shut past 4 s,
                // and with no send of any kind in this branch, that silence
                // was indistinguishable from a dead link to the device's own
                // 4 s read timeout, which then closed the connection outright
                // — measured as sessions dying exactly there whenever the
                // user was actively touching the panel.
                if (now - lastSentMs >= IdleKeepaliveMs && !Send(0, 0, now)) break;
                // Re-check in a moment rather than after another full send
                // interval. The ack that clears this gate lands at some
                // arbitrary point inside that interval, and sleeping through
                // the remainder of it before even looking cost most of the
                // gap between the configured rate and the rate actually
                // achieved — 25 fps asked for, 18 delivered, with the panel
                // drawing every frame it was sent and idle most of the time.
                next = sw.Elapsed.TotalMilliseconds + 2;
                continue;
            }

            double t0 = sw.Elapsed.TotalMilliseconds;

            if (!_source.Capture(_monitor.Bounds, _scale))
            {
                // No picture at all — duplication lost to a locked screen or a
                // mode switch, or none delivered yet this session. The device
                // still needs to hear from us: this path used to skip the
                // keepalive entirely, so a locked screen went silent and the
                // panel's 4 s read timeout closed the link, over and over —
                // measured as sessions ending exactly 4 s after they began.
                if (now - lastSentMs >= IdleKeepaliveMs && !Send(0, 0, now)) break;
                // Come back soon, not after a whole send interval — but not
                // instantly either: a lost duplication fails without waiting
                // on the acquire timeout, and retrying every millisecond
                // would just burn a core until the screen is unlocked.
                next = sw.Elapsed.TotalMilliseconds + 5;
                continue;
            }
            lock (_geomLock)
            {
                _geomSrc = _source.LastSrc;
                _geomDst = _source.LastDst;
            }

            _proc.Process(_source.Pixels, _cur);

            // A wholly black capture is indistinguishable from a real black
            // desktop in the stream, but it reaches the panel as a
            // full-screen change — and a full-screen change is delivered in
            // 16 KB pieces, top row band first, so it arrives looking like a
            // black bar wiping down the screen. Worth knowing whether that is
            // the desktop actually going black or the capture handing back an
            // empty frame, and the two are only separable here.
            bool allBlack = true;
            for (int i = 0; i < _cur.Length; i += 97)   // sparse probe; a real
            {                                           // frame fails it fast
                if (_cur[i] != 0) { allBlack = false; break; }
            }
            if (allBlack && !_wasBlack)
                Console.WriteLine($"\n  [diag] przechwycono calkowicie czarna klatke");
            _wasBlack = allBlack;

            byte[] softRgb = ApplyPanZoom(_cur, _panZoomed, _proc.SoftRgb, _panZoomedRgb);

            if (gateSince >= 0) { gateMs += now - gateSince; gateSince = -1; }

            bool full = _forceFull;
            _forceFull = false;
            if (udp)
            {
                // Tiles whose datagram was lost are re-encoded from what is on screen now.
                _udp!.Engine.TakeForced(_enc);
                // And one row of tiles a second, whatever else is going on (a full cycle in 15 s):
                // the panel acknowledges a datagram once it holds it, not once it has drawn it, so
                // a rectangle it could not decode would otherwise stay wrong until the source
                // happens to change there. Cheap insurance against a divergence nothing else noticed.
                if (now - lastSweepMs >= 1000)
                {
                    lastSweepMs = now;
                    _enc.ForceRow(sweepRow);
                    sweepRow = (sweepRow + 1) % Proto.TilesY;
                }
            }
            _enc.Encode(_panZoomed, _prev, full, softRgb);

            if (_enc.StripCount == 0)
            {
                statNoChange++;
                // Nothing changed. Still worth a zero-strip keepalive once
                // the idle interval above has passed.
                if (now - lastSentMs >= IdleKeepaliveMs && !Send(0, 0, now)) break;
                continue;
            }

            int len = _enc.PayloadLength;
            if (udp)
            {
                _enc.GetStrips(_strips);
                _udp!.Engine.Submit(_enc.Payload, _strips, _enc.RepaintPending);
                // TCP carries only its own keepalive now (the panel's read timeout is still 4 s),
                // and the send time it resets is the one the keepalive check above looks at.
                if (now - lastSentMs >= IdleKeepaliveMs && !Send(0, 0, now)) break;
            }
            else if (!Send(_enc.StripCount, len, now)) break;

            double dt = sw.Elapsed.TotalMilliseconds - t0;
            if (dt > worstMs) worstMs = dt;
            statFrames++;
            statBytes += (udp ? 0 : 11) + len;
            statStrips += _enc.StripCount;
            statJpeg += _enc.JpegCount;

            long elapsed = (long)sw.Elapsed.TotalMilliseconds;
            if (elapsed - statAt >= 1000)
            {
                double secs = (elapsed - statAt) / 1000.0;
                Console.Write($"\r  {statFrames / secs,5:F1} fps wys.  " +
                              $"{_deviceFps10 / 10.0,5:F1} fps na panelu  " +
                              $"{statBytes / secs / 1024,6:F0} kB/s  " +
                              $"{statStrips / Math.Max(1, statFrames),4} paskow/klatke  " +
                              $"jpeg {100 * statJpeg / Math.Max(1, statStrips),3}%{(_enc.VideoMode ? " [film]" : "       ")}  " +
                              $"max {worstMs,4:F1} ms  " +
                              $"czeka na panel {100 * gateMs / (secs * 1000),3:F0}%  " +
                              $"bez zmian {statNoChange / secs,4:F1}/s  " +
                              $"Windows rysuje {PresentsDelta() / secs,4:F1}/s  " +
                              $"panel gubil {Interlocked.Exchange(ref _refreshRequests, 0)}" +
                              (udp ? $"  {UdpStatText(_udp!.Engine)}   " : "   "));
                statAt = elapsed;
                statFrames = 0;
                statBytes = 0;
                statStrips = 0;
                statJpeg = 0;
                worstMs = 0;
                gateMs = 0;
                statNoChange = 0;
            }
        }
    }

    // ---------------------------------------------------------------- inbound

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var one = new byte[1];
        var buf = new byte[16];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(one, ct);
                Volatile.Write(ref _lastRxTick, Environment.TickCount64);
                switch (one[0])
                {
                    case Proto.MsgAck:
                        await _stream.ReadExactlyAsync(buf.AsMemory(0, 8), ct);
                        uint acked = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
                        // An ack for a frame this session never sent is left
                        // over from the device's previous connection (it was
                        // still draining frames when the link dropped). Taken
                        // at face value it would put _ackedSeq ahead of
                        // _sentSeq, and the unsigned in-flight count in the
                        // frame loop would wrap to billions — flow control
                        // shut for good, the server silent, the panel's 4 s
                        // read timeout ending the session.
                        if (acked <= _sentSeq) _ackedSeq = acked;
                        _deviceFps10 = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(6, 2));
                        break;

                    case Proto.MsgTouch:
                        await _stream.ReadExactlyAsync(buf.AsMemory(0, 7), ct);
                        HandleTouch(buf[0],
                                    BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(1, 2)),
                                    BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(3, 2)));
                        break;

                    case Proto.MsgOpt:
                        await _stream.ReadExactlyAsync(buf.AsMemory(0, 3), ct);
                        HandleOption(buf[0], BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(1, 2)));
                        break;

                    case Proto.MsgPing:
                        break;

                    case Proto.MsgUdpHello:
                        await _stream.ReadExactlyAsync(buf.AsMemory(0, 2), ct);
                        OfferUdp(BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0, 2)));
                        break;

                    case Proto.MsgScanCmd:
                        await _stream.ReadExactlyAsync(buf.AsMemory(0, 1), ct);
                        _scanner?.Command(buf[0]);
                        break;

                    case Proto.MsgDiag:
                        await _stream.ReadExactlyAsync(buf.AsMemory(0, 5), ct);
                        byte reason = buf[0];
                        uint diagValue = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(1, 4));
                        if (reason == LinkDropReason)
                        {
                            Console.WriteLine($"  panel: poprzednia sesja zakonczona, powod: {DropReasonText((byte)diagValue)}");
                        }
                        else if (reason == ShortSessionsReason)
                        {
                            Console.WriteLine($"  panel: {diagValue} sesji pod rzad umarlo w kilka sekund, mimo ze WiFi.status() caly czas " +
                                               "pokazywal 'connected'");
                        }
                        else
                        {
                            Console.WriteLine($"  panel: WiFi bylo offline {diagValue / 1000.0:0.0}s (powod: {WifiReasonText(reason)})");
                        }
                        break;

                    default:
                        Console.WriteLine($"\n  nieznana wiadomosc 0x{one[0]:X2} — zamykam sesje");
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException)
        {
            // TODO(diagnostics): temporary, tracking down an unexplained
            // reconnect loop — remove once root-caused.
            Console.WriteLine($"\n  [diag] ReadLoop zakonczyl sie: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// User-requested: touch drives the panel's own view, not Windows. A
    /// one-finger drag pans; a press held still for <see cref="LongPressMs"/>
    /// steps through <see cref="ZoomLevels"/> on release — the closest a
    /// single-contact resistive touch panel can get to a pinch gesture,
    /// which it cannot physically sense at all. No input is
    /// injected into Windows, so the server registers no touch device (one
    /// made Windows play device connect/disconnect sounds for nothing).
    /// </summary>
    private void HandleTouch(byte phase, int x, int y)
    {
        Interlocked.Increment(ref _touchTotal);
        Volatile.Write(ref _lastTouchTick, Environment.TickCount64);
        switch (phase)
        {
            case Proto.TouchDown:
                _dragging = true;
                _dragStartPx = x;
                _dragStartPy = y;
                _touchDownMs = Environment.TickCount64;
                lock (_panLock) { _dragStartPanX = _panX; _dragStartPanY = _panY; }
                break;

            case Proto.TouchMove:
                if (!_dragging) break;
                double zoom = ZoomLevels[_zoomIdx];
                // Panel pixels moved -> _cur-space pixels moved, at the
                // current crop scale; content-follows-finger, like scrolling
                // a map, so the crop centre moves the opposite way.
                double dx = (x - _dragStartPx) / zoom;
                double dy = (y - _dragStartPy) / zoom;
                lock (_panLock)
                {
                    _panX = _dragStartPanX - dx;
                    _panY = _dragStartPanY - dy;
                }
                _forceFull = true;   // the crop moved; every tile is a new comparison
                break;

            default:   // TouchUp
                _dragging = false;
                // Only a press that stayed put counts: incidental contact while
                // picking up or photographing the panel must not jump the zoom,
                // and the crop that produces reads as "the screen is not where
                // I wanted it", not as a gesture.
                bool stayed = Math.Abs(x - _dragStartPx) <= TapSlopPx && Math.Abs(y - _dragStartPy) <= TapSlopPx;
                if (stayed && Environment.TickCount64 - _touchDownMs >= LongPressMs)
                {
                    _zoomIdx = (_zoomIdx + 1) % ZoomLevels.Length;
                    _forceFull = true;
                }
                break;
        }
    }

    /// <summary>
    /// Crops <paramref name="src"/> (320x240) to the current pan/zoom window
    /// and nearest-neighbour-scales it back up to fill <paramref
    /// name="dst"/> (320x240). At ZoomLevels[0] == 1.0 the crop is the whole
    /// frame and this is the identity — no behaviour change until the user
    /// actually drags or long-presses.
    /// </summary>
    /// <returns>
    /// The 8-bit frame the JPEG path should code from: <paramref
    /// name="srcRgb"/> itself when there is no zoom, otherwise <paramref
    /// name="dstRgb"/> cropped with exactly the same mapping as the RGB565
    /// frame — both come out of one loop so they can never disagree about
    /// which pixel went where.
    /// </returns>
    private byte[] ApplyPanZoom(ushort[] src, ushort[] dst, byte[] srcRgb, byte[] dstRgb)
    {
        double zoom;
        double panX, panY;
        lock (_panLock) { zoom = ZoomLevels[_zoomIdx]; panX = _panX; panY = _panY; }

        if (zoom == 1.0) { Array.Copy(src, dst, src.Length); return srcRgb; }

        double cropW = Proto.W / zoom, cropH = Proto.H / zoom;
        double left = Math.Clamp(panX - cropW / 2.0, 0, Proto.W - cropW);
        double top  = Math.Clamp(panY - cropH / 2.0, 0, Proto.H - cropH);

        for (int oy = 0; oy < Proto.H; oy++)
        {
            int sy = Math.Clamp((int)(top + oy * cropH / Proto.H), 0, Proto.H - 1);
            int rowOut = oy * Proto.W;
            int rowIn = sy * Proto.W;
            for (int ox = 0; ox < Proto.W; ox++)
            {
                int sx = Math.Clamp((int)(left + ox * cropW / Proto.W), 0, Proto.W - 1);
                dst[rowOut + ox] = src[rowIn + sx];
                int o = (rowOut + ox) * 3, i = (rowIn + sx) * 3;
                dstRgb[o] = srcRgb[i]; dstRgb[o + 1] = srcRgb[i + 1]; dstRgb[o + 2] = srcRgb[i + 2];
            }
        }

        // Keep the pan centre consistent with the clamp above, so a drag
        // that pushed against an edge doesn't leave HandleTouch's drag-start
        // snapshot pointing somewhere the crop already moved back from.
        lock (_panLock) { _panX = left + cropW / 2.0; _panY = top + cropH / 2.0; }
        return dstRgb;
    }

    private void HandleOption(byte key, short value)
    {
        // Every setting below invalidates the reference frame, and the device
        // replays all of them the moment it connects (see sendHello in the
        // firmware) so a server restart can't leave the picture on defaults.
        // On a reconnect those replayed values are, by definition, the ones
        // already in effect — so acting on them restarted the initial repaint
        // from the top six times over while its lower rows were still being
        // delivered, which is what the panel showed as a picture assembling
        // itself in pieces. A setting that isn't changing isn't a change.
        bool unchanged = key switch
        {
            Proto.OptContrast  => value == _proc.Contrast,
            Proto.OptGamma     => value == _proc.Gamma,
            Proto.OptSharpen   => value == _proc.Sharpen,
            Proto.OptDither    => (value != 0) == _proc.Dither,
            Proto.OptScaleMode => (ScaleMode)Math.Clamp((int)value, 0, 2) == _scale,
            Proto.OptSrcRes    => value == _resIdx,
            _ => false,
        };
        if (unchanged)
        {
            // Still confirmed, so the settings page doesn't sit waiting on a
            // reply that the value it just sent is the value in force.
            Confirm(key, value);
            return;
        }

        switch (key)
        {
            case Proto.OptBrightness:
                // Applied by the device's own backlight PWM; recorded here only
                // so it can be persisted and restored.
                _opt.Brightness = value;
                _opt.Save();
                return;

            case Proto.OptContrast:
                _proc.Contrast = value;
                _opt.Contrast = _proc.Contrast;
                Confirm(key, (short)_proc.Contrast);
                break;

            case Proto.OptGamma:
                _proc.Gamma = value;
                _opt.Gamma = _proc.Gamma;
                Confirm(key, (short)_proc.Gamma);
                break;

            case Proto.OptSharpen:
                _proc.Sharpen = value;
                _opt.Sharpen = _proc.Sharpen;
                Confirm(key, (short)_proc.Sharpen);
                break;

            case Proto.OptDither:
                _proc.Dither = value != 0;
                _opt.Dither = _proc.Dither;
                Confirm(key, (short)(_proc.Dither ? 1 : 0));
                break;

            case Proto.OptScaleMode:
                _scale = (ScaleMode)Math.Clamp((int)value, 0, 2);
                _opt.Scale = _scale;
                Confirm(key, (short)_scale);
                break;

            case Proto.OptSrcRes:
                ApplyResolution(value);
                return;   // ApplyResolution confirms and saves itself

            case Proto.OptPause:
                _paused = value != 0;
                if (!_paused) { _forceFull = true; _resumeAckReset = true; }
                return;

            case Proto.OptScanner:
                // The page covers the picture, so there is nothing to capture until it closes.
                _paused = value != 0;
                _scanner?.PageOpened(_paused);
                ScannerWeb.PanelPage(_paused);
                if (!_paused) { _forceFull = true; _resumeAckReset = true; }
                return;

            case Proto.OptRefresh:
                // Besides once per connect, the panel only asks for this after
                // it had to throw strips away for want of a free buffer — so
                // the count is a direct reading of its buffers overflowing.
                Interlocked.Increment(ref _refreshRequests);
                _forceFull = true;
                return;

            default:
                return;
        }

        // Anything that changes the transfer curve or the framing invalidates
        // every tile, so the reference frame has to go — otherwise only the
        // parts of the desktop that happen to change afterwards would pick up
        // the new setting, leaving the screen half-adjusted.
        _forceFull = true;
        _opt.Save();
    }

    private void ApplyResolution(int index)
    {
        index = Math.Clamp(index, 0, Proto.ResTable.Length - 1);

        if (!_opt.Virtual)
        {
            // Refuse to touch the display mode of a real monitor. This exists
            // because of a real incident: mirroring the user's actual primary
            // screen (--monitor, not --virtual) still ran this same
            // negotiation on every reconnect, and each one called
            // ChangeDisplaySettingsEx on the monitor the user was actively
            // working on — repeatedly resizing their real desktop out from
            // under them. Changing resolution only makes sense for a display
            // that exists solely to be mirrored; a real monitor's mode is the
            // user's to set, in Windows' own display settings, never ours.
            _resIdx = NearestResIndex((_monitor.Bounds.Width, _monitor.Bounds.Height));
            Confirm(Proto.OptSrcRes, (short)_resIdx);
            return;
        }

        var (w, h) = Proto.ResTable[index];
        var (gotW, gotH) = Monitors.SetResolution(_monitor.Device, w, h);

        // Re-read the monitor rectangle: changing a mode moves the desktop
        // origin of every monitor to its right, so the old bounds would have us
        // capturing the wrong region.
        var refreshed = Monitors.Enumerate().FirstOrDefault(m => m.Device == _monitor.Device);
        if (refreshed is not null) _monitor = refreshed;

        _resIdx = NearestResIndex((gotW, gotH));
        _opt.ResIndex = _resIdx;
        _opt.Save();
        _forceFull = true;

        if (gotW != w || gotH != h)
            Console.WriteLine($"\n  sterownik odmowil {w}x{h}, zostalo {gotW}x{gotH}");

        // Confirm the index matching what is really in force, not what was
        // asked for, so the device's menu shows the truth.
        Confirm(Proto.OptSrcRes, (short)_resIdx);
        SendText($"{gotW}x{gotH} -> 320x240");
    }

    // Mirrors REASON_SHORT_SESSIONS in the firmware — outside wifi_err_reason_t's
    // real range, so it can never collide with an actual disconnect reason.
    private const byte ShortSessionsReason = 250;
    // Mirrors REASON_LINK_DROP; the value is one of the firmware's DROP_* codes.
    private const byte LinkDropReason = 251;

    private static string DropReasonText(byte code) => code switch
    {
        1 => "brak danych od serwera przez 4 s (timeout odczytu)",
        2 => "gniazdo zamkniete (serwer lub siec)",
        3 => "bledny naglowek paska (utrata synchronizacji strumienia)",
        4 => "nieznany typ wiadomosci (utrata synchronizacji strumienia)",
        5 => "za dlugi komunikat tekstowy",
        6 => "zapis do gniazda sie nie powiodl",
        7 => "za duze tablice JPEG",
        _ => $"kod {code}",
    };

    // esp_wifi_types.h's wifi_err_reason_t, the subset actually worth naming
    // for a station that keeps leaving the AP — everything else still prints
    // fine as a bare number.
    private static string WifiReasonText(byte reason) => reason switch
    {
        2   => "AUTH_EXPIRE (AP wygasil uwierzytelnienie)",
        3   => "AUTH_LEAVE",
        4   => "ASSOC_EXPIRE (AP wygasil skojarzenie — typowe przy braku ruchu)",
        5   => "ASSOC_TOOMANY (AP odrzucil, za duzo klientow)",
        6   => "NOT_AUTHED",
        7   => "NOT_ASSOCED",
        8   => "ASSOC_LEAVE (stacja/AP sama zakonczyla polaczenie)",
        15  => "4WAY_HANDSHAKE_TIMEOUT (zle haslo albo slaby sygnal)",
        200 => "BEACON_TIMEOUT (AP przestal byc slyszalny — typowe dla hotspotu z telefonu ktory usypia)",
        201 => "NO_AP_FOUND (siec zniknela — AP wylaczony albo poza zasiegiem)",
        202 => "AUTH_FAIL",
        203 => "ASSOC_FAIL",
        204 => "HANDSHAKE_TIMEOUT",
        _   => $"kod {reason}",
    };

    // esp_system.h's esp_reset_reason_t. ESP_RST_POWERON / ESP_RST_SW are the
    // two boring, expected ones (cold power-up; our own ESP.restart() after a
    // settings/WiFi save) — everything else is the device itself telling us
    // it crashed, and how, which used to only ever reach a serial monitor
    // nobody had plugged in.
    private static string ResetReasonText(byte reason) => reason switch
    {
        0  => "nieznany",
        1  => "wlaczenie zasilania",
        2  => "reset pinem zewnetrznym",
        3  => "restart programowy (np. zapis ustawien/WiFi)",
        4  => "PANIC / wyjatek",
        5  => "watchdog przerwan (INT_WDT)",
        6  => "watchdog zadania (TASK_WDT)",
        7  => "inny watchdog",
        8  => "wybudzenie z deep sleep",
        9  => "brownout (spadek zasilania)",
        10 => "reset przez SDIO",
        _  => $"kod {reason}",
    };

    private static int NearestResIndex((int W, int H) size)
    {
        int best = 0;
        long bestErr = long.MaxValue;
        for (int i = 0; i < Proto.ResTable.Length; i++)
        {
            long err = Math.Abs((long)Proto.ResTable[i].W - size.W)
                     + Math.Abs((long)Proto.ResTable[i].H - size.H);
            if (err < bestErr) { bestErr = err; best = i; }
        }
        return best;
    }

    private void Confirm(byte key, short value)
    {
        var m = new byte[4];
        m[0] = Proto.MsgOptAck;
        m[1] = key;
        BinaryPrimitives.WriteInt16LittleEndian(m.AsSpan(2, 2), value);
        TrySend(m);
    }

    private void SendText(string s)
    {
        byte[] body = Encoding.UTF8.GetBytes(s);
        if (body.Length > 256) body = body[..256];
        var m = new byte[3 + body.Length];
        m[0] = Proto.MsgText;
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(1, 2), (ushort)body.Length);
        body.CopyTo(m, 3);
        TrySend(m);
    }

    private void TrySend(byte[] m)
    {
        try { lock (_tx) _stream.Write(m); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        ScannerWeb.PanelDown(this);
        _scanner?.Dispose();
        _udpOffer?.Dispose();
        _udp?.Dispose();
        _source.Dispose();
        _stream.Dispose();
        _client.Dispose();
        _cts.Dispose();
    }
}
