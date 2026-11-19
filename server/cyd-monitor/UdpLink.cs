using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CydMonitor;

/// <summary>
/// The real-socket half of the protocol-v5 data plane: one UDP socket towards the panel, a
/// thread that hands its acknowledgements to <see cref="UdpEngine"/>, and a thread that lets the
/// engine release paced datagrams. All the protocol logic lives in the engine; this class only
/// owns the socket, the clock and the two threads.
/// </summary>
internal sealed class UdpLink : IDatagramSink, IDisposable
{
    // Winsock reports an ICMP "port unreachable" (the panel not listening yet, or gone) as an
    // error on the *next receive* of the same socket, which would kill the receive loop for a
    // condition that means nothing here. This ioctl turns that behaviour off.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private readonly Socket _sock;
    private readonly IPEndPoint _panel;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _rx, _pump;
    private bool _timerRaised;
    private const int IdlePumpMs = 20;

    /// <summary>The stream is held: only an occasional keepalive is due, so the pump need not wake a thousand times a second.</summary>
    public volatile bool Idle;

    public UdpEngine Engine { get; }
    public IPEndPoint PanelEndPoint => _panel;
    public ushort Session { get; }
    public int LocalPort { get; }
    public long SendErrors;

    /// <summary>Microseconds on a monotonic clock; the only clock <see cref="UdpEngine"/> ever sees.</summary>
    public static long NowUs() => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));

    public UdpLink(IPAddress panelIp, int panelPort, int localPort, ushort session)
    {
        _panel = new IPEndPoint(panelIp, panelPort);
        Session = session;

        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _sock.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            _sock.Bind(new IPEndPoint(IPAddress.Any, localPort));
            // The default send buffer is plenty for a few kB/s of pacing, but say so: a burst
            // of a whole frame must never be dropped by the local stack.
            _sock.SendBufferSize = 64 * 1024;
            _sock.ReceiveBufferSize = 16 * 1024;
            LocalPort = ((IPEndPoint)_sock.LocalEndPoint!).Port;
        }
        catch
        {
            _sock.Dispose();
            throw;
        }

        Engine = new UdpEngine(this, session, NowUs());
    }

    public void Start()
    {
        // Windows' timer tick is ~15.6 ms unless a process asks for finer; pacing datagrams 0.7 ms
        // apart and running a loss timer measured in tens of ms needs 1 ms.
        try { timeBeginPeriod(1); _timerRaised = true; } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }

        _rx = new Thread(ReceiveLoop) { IsBackground = true, Name = "udp-rx", Priority = ThreadPriority.AboveNormal };
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "udp-pump", Priority = ThreadPriority.AboveNormal };
        _rx.Start();
        _pump.Start();
    }

    public void Send(ReadOnlySpan<byte> datagram)
    {
        try { _sock.SendTo(datagram, SocketFlags.None, _panel); }
        catch (SocketException) { Interlocked.Increment(ref SendErrors); }   // e.g. no route for a moment; the engine sees it as a loss
        catch (ObjectDisposedException) { }
    }

    private void ReceiveLoop()
    {
        var buf = new byte[512];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                int n = _sock.ReceiveFrom(buf, ref from);
                // Only the panel this session is talking to; anything else on the port is noise.
                if (((IPEndPoint)from).Address.Equals(_panel.Address))
                    Engine.OnAck(buf.AsSpan(0, n), NowUs());
            }
            catch (SocketException) when (!_cts.IsCancellationRequested) { Thread.Sleep(1); }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private void PumpLoop()
    {
        long lastSend = 0;
        while (!_cts.IsCancellationRequested)
        {
            long before = Engine.SentDatagrams;
            long now = NowUs();
            bool more = Engine.Pump(now);
            if (Engine.SentDatagrams != before) lastSend = now;

            if (!more) Thread.Sleep(Idle ? IdlePumpMs : 1);
            // Datagrams are waiting on the 0.7 ms spacing or the token bucket: the next one is due
            // within a millisecond, so spin instead of sleeping through it.
            else if (now - lastSend < 2_000) Thread.SpinWait(40);
            // Waiting on the window instead: only an acknowledgement or a timeout frees it, and
            // spinning through an outage would burn a core for nothing.
            else Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _sock.Dispose();   // unblocks ReceiveFrom
        _rx?.Join(500);
        _pump?.Join(500);
        if (_timerRaised) { try { timeEndPeriod(1); } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { } }
        _cts.Dispose();
    }
}
