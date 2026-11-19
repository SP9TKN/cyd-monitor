using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CydMonitor;

/// <summary>
/// What the Windows TCP stack itself sees on the panel's connection (SIO_TCP_INFO, Windows 10
/// 1703+). It is the only view of a stalled session from below the application: whether
/// packets are being lost on the way (retransmitted bytes, timeout episodes, a large RTT),
/// or the panel simply stopped reading (the window it advertises falls to zero), or nothing is
/// wrong at the TCP level at all. Diagnostics only - any failure just means "no data".
/// </summary>
internal static class TcpProbe
{
    private const int SioTcpInfo = unchecked((int)0xD8000027);

    // mstcpip.h TCP_INFO_v0; Sequential gives the same natural alignment as the C header.
    [StructLayout(LayoutKind.Sequential)]
    private struct TcpInfoV0
    {
        public int    State;
        public uint   Mss;
        public ulong  ConnectionTimeMs;
        public byte   TimestampsEnabled;
        public uint   RttUs;
        public uint   MinRttUs;
        public uint   BytesInFlight;
        public uint   Cwnd;
        public uint   SndWnd;
        public uint   RcvWnd;
        public uint   RcvBuf;
        public ulong  BytesOut;
        public ulong  BytesIn;
        public uint   BytesReordered;
        public uint   BytesRetrans;
        public uint   FastRetrans;
        public uint   DupAcksIn;
        public uint   TimeoutEpisodes;
        public byte   SynRetrans;
    }

    /// <summary>One reading, already formatted for the log.</summary>
    public static bool TryRead(Socket? socket, out string text)
    {
        bool ok = TryReadSnapshot(socket, out var snap);
        text = ok ? snap.ToString() : "brak danych TCP";
        return ok;
    }

    public static bool TryReadSnapshot(Socket? socket, out TcpSnapshot snap)
    {
        snap = default;
        if (socket is null) return false;
        try
        {
            var input = BitConverter.GetBytes(0);   // TCP_INFO version 0
            var output = new byte[Marshal.SizeOf<TcpInfoV0>()];
            socket.IOControl((IOControlCode)SioTcpInfo, input, output);
            var i = MemoryMarshal.Read<TcpInfoV0>(output);
            snap = new TcpSnapshot(i.RttUs / 1000, i.SndWnd, i.BytesInFlight, i.BytesRetrans, i.TimeoutEpisodes, i.DupAcksIn);
            return true;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
/// okno = what the panel advertises (0 = it is not reading), w locie = sent but not yet
/// acknowledged at the TCP level, retrans = bytes sent again, to = retransmission-timeout
/// episodes, dup = duplicate acks (a segment went missing).
/// </summary>
internal readonly record struct TcpSnapshot(uint RttMs, uint SndWnd, uint InFlight, uint Retrans, uint Timeouts, uint DupAcks)
{
    public override string ToString() =>
        $"rtt {RttMs}ms okno {SndWnd} w locie {InFlight} retrans {Retrans}B to {Timeouts} dup {DupAcks}";
}
