using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace CydMonitor;

/// <summary>Where finished datagrams go: a real socket in production, a lossy channel in the simulator.</summary>
internal interface IDatagramSink
{
    void Send(ReadOnlySpan<byte> datagram);
}

/// <summary>
/// The protocol-v5 data plane, minus every socket and clock: packs the encoder's strips
/// into datagrams, paces them out, and turns the device's acknowledgements into
/// "which tiles must be sent again". Time comes in as an argument, so the same code runs
/// against real sockets and, deterministically and fast, against the simulator.
///
/// The idea (docs/protocol-v5-udp.pl.md, section 3): recovery is by *state*, not by bytes.
/// A strip's payload is absolute pixel content, so a lost datagram is never replayed;
/// the tiles it carried are simply marked dirty again and go out in whatever the next
/// frame is, with their current content. That is what removes TCP's head-of-line
/// blocking and its retransmission timeout, and it needs one piece of bookkeeping:
/// for every tile, the last datagram that carried it (<see cref="_carrier"/>). A lost
/// datagram only re-dirties tiles it is still the carrier of; a tile that has since
/// gone out in a newer datagram is that datagram's business.
/// </summary>
internal sealed class UdpEngine
{
    private const int Ring = 256;                                   // datagram records, power of two
    private const int TileCount = Proto.TilesX * Proto.TilesY;      // 300
    private const int TileWords = (TileCount + 63) / 64;            // 5 x 64 bits

    // The device keeps 16 datagram slots (DG_SLOTS in the firmware); staying a few short of
    // that leaves room for the ones already in flight when the device's queue fills.
    private const int MaxCwnd = 12;
    private const double InitialCwnd = 8;
    private const long KeepaliveUs = 500_000;
    private const long HoldUs = 300_000;        // see Pump: how long the first datagram waits for the device's opening acknowledgement
    private const long MinSpacingUs = 700;      // the device's UDP receive queue holds only 6 packets
    private const double MinRate = 150_000, MaxRate = 700_000, InitialRate = 500_000;  // bytes/s
    private const double Burst = 6_000;                              // bytes the token bucket may bank

    // WiFi loses packets for reasons that have nothing to do with congestion (the ICMP probe
    // measured about 1% at 100 packets/s, plus outages of a few hundred ms), so a single
    // loss must not throttle the stream. The sender reacts to the *rate* of loss instead:
    // an exponentially weighted share of datagrams lost, checked four times a second.
    private const double LossAlpha = 0.02, LossHigh = 0.08, LossLow = 0.03;
    private const long RateTickUs = 250_000;

    private enum St : byte { Free, Queued, InFlight, Acked, Lost }

    private struct Dg
    {
        public uint Seq;
        public St State;
        public long SentUs;
    }

    private readonly IDatagramSink _sink;
    private readonly ushort _session;
    private readonly object _lock = new();

    private readonly Dg[] _dg = new Dg[Ring];
    private readonly ulong[] _dgTiles = new ulong[Ring * TileWords];   // tiles each datagram carries
    private readonly uint[] _carrier = new uint[TileCount];            // last datagram to carry each tile
    private readonly bool[] _forced = new bool[TileCount];             // tiles to send again
    private readonly Queue<(byte[] Buf, int Len)> _queue = new();

    // The datagram being assembled by Submit.
    private byte[]? _bBuf;
    private int _bN, _bRects;
    private readonly ulong[] _bTiles = new ulong[TileWords];

    private uint _nextSeq = 1;
    private uint _oldest = 1;               // smallest seq still Queued or InFlight
    private int _inflight;

    private double _cwnd = InitialCwnd;
    private double _rate = InitialRate;
    private double _tokens = Burst;
    private long _lastRefillUs, _nextSendUs, _lastSendUs, _lastAckUs, _lastRateTickUs, _startUs;
    private long _silenceFromUs = -1;       // first datagram sent since the last acknowledgement; -1 = none outstanding
    private long _srtt = 20_000, _rttvar = 10_000;
    private bool _haveRtt;
    private double _lossEwma;

    public UdpEngine(IDatagramSink sink, ushort session, long nowUs)
    {
        _sink = sink;
        _session = session;
        _startUs = _lastRefillUs = _lastSendUs = _lastAckUs = _lastRateTickUs = nowUs;
    }

    /// <summary>Share of recent datagrams that were lost, smoothed; for the log.</summary>
    public double LossShare => _lossEwma;

    // Counters for the log; only ever read for diagnostics.
    public long SentDatagrams, SentBytes, LostDatagrams, SpuriousLosses, ResentTiles, AckCount, AckedDatagrams;
    public ushort PanelFps10, PanelBlitMs;
    public byte PanelFree = 16;
    public long PanelDrops;
    public long SrttUs => _srtt;
    public double Cwnd => _cwnd;
    public double RateBytesPerSec => _rate;

    private int Window => Math.Min((int)_cwnd, PanelFree < 3 && AckCount > 0 ? 4 : MaxCwnd);

    // The device holds an acknowledgement for up to 16 ms (its ACK timer), and a datagram
    // that is settled through the bitmap of a later acknowledgement waits that long on top of
    // the round trip - so the timeout has to include it, or ordinary delayed acknowledgements
    // get mistaken for losses (QUIC's PTO adds max_ack_delay for the same reason).
    private const long AckDelayUs = 16_000;
    private long Rto => Math.Clamp(_srtt + 4 * _rttvar + AckDelayUs + 10_000, 50_000, 300_000);

    // ---------------------------------------------------------------- sending

    /// <summary>True when there is room to encode another frame; otherwise the tiles just stay dirty.</summary>
    public bool CanAccept()
    {
        lock (_lock) return _queue.Count <= 1 && _inflight < Window;
    }

    /// <summary>Hands the encoder every tile a lost datagram was the last carrier of.</summary>
    public int TakeForced(TileEncoder enc)
    {
        int n = 0;
        lock (_lock)
        {
            for (int t = 0; t < TileCount; t++)
            {
                if (!_forced[t]) continue;
                _forced[t] = false;
                enc.ForceTile(t);
                n++;
            }
        }
        return n;
    }

    /// <summary>Packs one encoded frame into datagrams and queues them; <see cref="Pump"/> sends them.</summary>
    public void Submit(ReadOnlySpan<byte> payload, IReadOnlyList<StripRef> strips, bool repaint)
    {
        lock (_lock)
        {
            for (int i = 0; i < strips.Count; i++)
            {
                var s = strips[i];
                int need = Proto.DgRectHeaderBytes + s.Length;
                if (need > Proto.DgMaxBytes - Proto.DgHeaderBytes)
                    throw new InvalidOperationException($"pasek {s.Length} B nie miesci sie w datagramie (SplitLimit?)");

                if (_bBuf is not null && (_bN + need > Proto.DgMaxBytes || _bRects == 255))
                    Flush(frameEnd: false, repaint);
                if (_bBuf is null) Begin();

                var b = _bBuf!.AsSpan(_bN);
                b[0] = (byte)s.Tx; b[1] = (byte)s.Ty; b[2] = (byte)s.Tw; b[3] = s.Mode;
                BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(4, 2), (ushort)s.Length);
                payload.Slice(s.Offset, s.Length).CopyTo(b.Slice(Proto.DgRectHeaderBytes));
                _bN += need;
                _bRects++;

                // This datagram is now the last carrier of every tile the strip covers.
                for (int k = 0; k < s.Tw; k++)
                {
                    int t = s.Ty * Proto.TilesX + s.Tx + k;
                    _carrier[t] = _nextSeq;
                    _bTiles[t >> 6] |= 1UL << (t & 63);
                }
            }
            Flush(frameEnd: true, repaint);
        }
    }

    private void Begin()
    {
        _bBuf = ArrayPool<byte>.Shared.Rent(Proto.DgMaxBytes);
        _bN = Proto.DgHeaderBytes;
        _bRects = 0;
        Array.Clear(_bTiles);
    }

    private void Flush(bool frameEnd, bool repaint)
    {
        if (_bBuf is null) return;
        uint seq = _nextSeq++;
        _bBuf[0] = Proto.DgData;
        _bBuf[1] = (byte)((repaint ? Proto.DgFlagRepaint : 0) | (frameEnd ? Proto.DgFlagFrameEnd : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(_bBuf.AsSpan(2, 2), _session);
        BinaryPrimitives.WriteUInt32LittleEndian(_bBuf.AsSpan(4, 4), seq);
        _bBuf[8] = (byte)_bRects;

        int slot = (int)(seq & (Ring - 1));
        _dg[slot] = new Dg { Seq = seq, State = St.Queued };
        Array.Copy(_bTiles, 0, _dgTiles, slot * TileWords, TileWords);
        _queue.Enqueue((_bBuf, _bN));
        _bBuf = null;
    }

    /// <summary>
    /// Releases queued datagrams that the window, the rate and the minimum spacing allow; runs the loss timers.
    /// Returns true while datagrams are still waiting, so the caller knows whether to come back soon.
    /// </summary>
    public bool Pump(long nowUs)
    {
        lock (_lock)
        {
            // Token bucket.
            _tokens = Math.Min(Burst, _tokens + (nowUs - _lastRefillUs) * _rate / 1e6);
            _lastRefillUs = nowUs;
            if (nowUs - _lastRateTickUs >= RateTickUs)
            {
                _lastRateTickUs = nowUs;
                if (_lossEwma > LossHigh) { _rate = Math.Max(MinRate, _rate * 0.85); _cwnd = Math.Max(6, _cwnd * 0.85); }
                else if (_lossEwma < LossLow) _rate = Math.Min(MaxRate, _rate * 1.10);
            }

            CheckTimeouts(nowUs);

            // The device only starts listening when it has read TRANSPORT over TCP, which can take
            // a while behind a busy or stalled TCP stream, and what arrives before that is ignored
            // - a whole first burst written off as lost, and the loss controller with it. So the
            // first datagram waits for the device's opening acknowledgement (it sends one the moment
            // it has the session), or for HoldUs, in case that acknowledgement never comes.
            if (AckCount == 0 && nowUs - _startUs < HoldUs) return _queue.Count > 0;

            int limit = Window;
            while (_queue.Count > 0 && _inflight < limit && nowUs >= _nextSendUs)
            {
                var (buf, len) = _queue.Peek();
                if (_tokens < len) break;
                _queue.Dequeue();
                _tokens -= len;

                uint seq = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4));
                ref Dg d = ref _dg[(int)(seq & (Ring - 1))];
                d.State = St.InFlight;
                d.SentUs = nowUs;
                _inflight++;
                SentDatagrams++;
                SentBytes += len;
                if (_silenceFromUs < 0) _silenceFromUs = nowUs;
                _sink.Send(buf.AsSpan(0, len));
                ArrayPool<byte>.Shared.Return(buf);
                _lastSendUs = nowUs;
                _nextSendUs = nowUs + MinSpacingUs;
            }

            // Nothing to say for a while: an empty datagram keeps the device's picture of
            // the link alive and doubles as a probe, so a lost tail of a burst is noticed
            // by the timer instead of waiting for the next frame.
            if (_queue.Count == 0 && nowUs - _lastSendUs >= KeepaliveUs && _inflight < limit + 2)
                SendEmpty(nowUs);
            return _queue.Count > 0;
        }
    }

    private void SendEmpty(long nowUs)
    {
        uint seq = _nextSeq++;
        Span<byte> b = stackalloc byte[Proto.DgHeaderBytes];
        b[0] = Proto.DgData;
        b[1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(2, 2), _session);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(4, 4), seq);
        b[8] = 0;

        int slot = (int)(seq & (Ring - 1));
        _dg[slot] = new Dg { Seq = seq, State = St.InFlight, SentUs = nowUs };
        Array.Clear(_dgTiles, slot * TileWords, TileWords);
        _inflight++;
        SentDatagrams++;
        SentBytes += b.Length;
        if (_silenceFromUs < 0) _silenceFromUs = nowUs;
        _sink.Send(b);
        _lastSendUs = nowUs;
    }

    // ---------------------------------------------------------------- acknowledgements

    public void OnAck(ReadOnlySpan<byte> pkt, long nowUs)
    {
        if (pkt.Length < Proto.DgAckBytes || pkt[0] != Proto.DgAck) return;
        if (BinaryPrimitives.ReadUInt16LittleEndian(pkt.Slice(2, 2)) != _session) return;
        uint highest = BinaryPrimitives.ReadUInt32LittleEndian(pkt.Slice(4, 4));
        uint bitmap = BinaryPrimitives.ReadUInt32LittleEndian(pkt.Slice(8, 4));

        lock (_lock)
        {
            AckCount++;
            _lastAckUs = nowUs;
            PanelFps10 = BinaryPrimitives.ReadUInt16LittleEndian(pkt.Slice(12, 2));
            PanelBlitMs = BinaryPrimitives.ReadUInt16LittleEndian(pkt.Slice(14, 2));
            PanelFree = pkt[16];
            PanelDrops += pkt[17];

            if (highest != 0 && Known(highest)) Settle(highest, nowUs, sample: true);
            for (int k = 0; k < 32; k++)
            {
                if ((bitmap >> k & 1) == 0) continue;
                uint s = highest - 1 - (uint)k;
                if (s != 0 && Known(s)) Settle(s, nowUs, sample: false);
            }

            // Packet-threshold loss: an outstanding datagram three or more behind an
            // acknowledged one has been overtaken, not just delayed - but only once it has
            // also been out longer than a round trip, because datagrams sent 0.7 ms apart
            // routinely swap places under jitter (the time threshold of QUIC's detector).
            // The bitmap only reaches 32 back, so older ones are lost as well.
            for (uint s = _oldest; s != _nextSeq && (int)(highest - s) >= 3; s++)
            {
                ref Dg d = ref _dg[s & (Ring - 1)];
                if (d.State == St.InFlight && d.Seq == s && nowUs - d.SentUs >= _srtt + _rttvar) MarkLost(s, nowUs);
            }
            Advance();

            // The silence clock restarts from this acknowledgement if anything is still out.
            _silenceFromUs = _inflight > 0 ? nowUs : -1;
        }
    }

    private bool Known(uint seq) => (int)(_nextSeq - seq) > 0 && (int)(seq - _oldest) >= -Ring + 1 && _dg[seq & (Ring - 1)].Seq == seq;

    private void Settle(uint seq, long nowUs, bool sample)
    {
        ref Dg d = ref _dg[seq & (Ring - 1)];
        switch (d.State)
        {
            case St.InFlight:
                d.State = St.Acked;
                _inflight--;
                AckedDatagrams++;
                _lossEwma -= LossAlpha * _lossEwma;
                if (sample) AddRtt(nowUs - d.SentUs);
                _cwnd = Math.Min(MaxCwnd, _cwnd + 1.0 / _cwnd);
                break;
            case St.Lost:
                // Its tiles were already queued again (a wasted resend, nothing worse), and it
                // was not a loss after all: take it back out of the loss share, so a jittery
                // link cannot throttle the stream through false alarms.
                d.State = St.Acked;
                SpuriousLosses++;
                _lossEwma = Math.Max(0, _lossEwma - LossAlpha * (1 - _lossEwma));
                break;
        }
    }

    private void AddRtt(long r)
    {
        if (!_haveRtt) { _srtt = r; _rttvar = r / 2; _haveRtt = true; return; }
        _rttvar = (3 * _rttvar + Math.Abs(_srtt - r)) / 4;
        _srtt = (7 * _srtt + r) / 8;
    }

    private void CheckTimeouts(long nowUs)
    {
        long rto = Rto;
        for (uint s = _oldest; s != _nextSeq; s++)
        {
            ref Dg d = ref _dg[s & (Ring - 1)];
            if (d.Seq == s && d.State == St.InFlight && nowUs - d.SentUs > rto) MarkLost(s, nowUs);
        }
        Advance();
    }

    private void Advance()
    {
        while (_oldest != _nextSeq && _dg[_oldest & (Ring - 1)].State is St.Acked or St.Lost or St.Free)
            _oldest++;
    }

    private void MarkLost(uint seq, long nowUs)
    {
        ref Dg d = ref _dg[seq & (Ring - 1)];
        if (d.State != St.InFlight) return;
        d.State = St.Lost;
        _inflight--;
        LostDatagrams++;

        int baseWord = (int)(seq & (Ring - 1)) * TileWords;
        for (int w = 0; w < TileWords; w++)
        {
            ulong bits = _dgTiles[baseWord + w];
            while (bits != 0)
            {
                int t = w * 64 + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (_carrier[t] == seq) { _forced[t] = true; ResentTiles++; }
            }
        }

        // No rate cut here: see LossAlpha. The tiles are already marked; the rate reacts to how
        // *often* datagrams are lost, in Pump.
        _lossEwma += LossAlpha * (1 - _lossEwma);
    }

    // ---------------------------------------------------------------- health

    /// <summary>
    /// No acknowledgement for 3 s although something was sent since (a firewall dropping
    /// the device's UDP, or a device that lost the plot): the caller falls back to TCP.
    /// </summary>
    public bool IsFailed(long nowUs)
    {
        lock (_lock)
        {
            if (AckCount == 0) return nowUs - _startUs > 3_000_000;
            return _lastSendUs > _lastAckUs && nowUs - _lastAckUs > 3_000_000;
        }
    }

    /// <summary>
    /// How long the device has been silent although it owes an acknowledgement: time since the
    /// last acknowledgement (or since the first datagram sent after it), 0 when nothing is outstanding.
    /// This is the server's view of a stutter; it spans a whole outage, where the age of a single
    /// datagram is cut short each time the timer declares that datagram lost.
    /// </summary>
    public long AckSilenceUs(long nowUs)
    {
        lock (_lock) return _silenceFromUs < 0 ? 0 : nowUs - _silenceFromUs;
    }

    /// <summary>Age of the oldest datagram nobody has acknowledged yet: the visible stutter, seen from the server.</summary>
    public long OldestInflightAgeUs(long nowUs)
    {
        lock (_lock)
        {
            for (uint s = _oldest; s != _nextSeq; s++)
            {
                ref Dg d = ref _dg[s & (Ring - 1)];
                if (d.Seq == s && d.State == St.InFlight) return nowUs - d.SentUs;
            }
            return 0;
        }
    }

    public int InFlight { get { lock (_lock) return _inflight; } }
    public int Queued { get { lock (_lock) return _queue.Count; } }
}
