using System.Buffers.Binary;
using System.Diagnostics;

namespace CydMonitor;

/// <summary>
/// Runs the whole v5 data plane - encoder, <see cref="UdpEngine"/>, a lossy channel and a
/// model of the panel's receive path - on a virtual clock, with no sockets and no hardware.
///
/// It answers the question the design rests on: after datagrams are lost, delayed,
/// reordered or swallowed by a radio outage, does the panel's screen still converge to the
/// source, and what does the viewer see on the way? The channel models what the
/// 100 packets/s ICMP probe measured on the real link: random loss, bursts, and outages of
/// a few hundred milliseconds.
///
/// Two measures stand in for "stutter". A *freeze* is a stretch of 250 ms or more in which
/// the panel's picture did not change at all while the source was moving - the same thing
/// the server's [dip] detector reports on the real system. The *age* of the picture is, at
/// each instant, how long the most out-of-date tile has been showing a version the source
/// has already replaced, found by looking the panel's tile up in the source's own recent
/// history (comparing against the current source would only measure motion).
///
/// The panel model mirrors the firmware's rules on purpose (a 16-slot receive ring, a
/// per-tile sequence stamp so late data never overwrites newer, acknowledgements every
/// 16 ms or on a gap), and decodes with the same independent reference decoder the codec
/// self-test uses. It is a twin of the firmware, not shared code.
/// </summary>
internal static class UdpSim
{
    public static int Run(int seconds)
    {
        Console.WriteLine($"symulacja UDP v5: {seconds} s wirtualnego czasu na scenariusz\n");
        int fails = 0;
        // Limits are what the protocol controls, not what the channel takes away: with no radio
        // outage no freeze may reach 250 ms and the oldest stale tile stays within a few frame
        // times; with outages a freeze may exceed the longest outage only by the recovery time.
        fails += Scenario("czysty kanal",                        seconds, lossGood: 0.0,   burst: false, outages: false, jitterMs: 2,  seed: 1, extraFreezeMs: 250, maxP99AgeMs: 250);
        fails += Scenario("strata 1% + jitter/przemieszanie",    seconds, lossGood: 0.01,  burst: false, outages: false, jitterMs: 12, seed: 2, extraFreezeMs: 250, maxP99AgeMs: 350);
        fails += Scenario("serie strat + wylaczenia 300-900 ms", seconds, lossGood: 0.003, burst: true,  outages: true,  jitterMs: 6,  seed: 3, extraFreezeMs: 400, maxP99AgeMs: double.MaxValue);
        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "WSZYSTKIE SCENARIUSZE OK" : $"*** {fails} SCENARIUSZY NIEUDANYCH ***");
        return fails == 0 ? 0 : 1;
    }

    private const int W = Proto.W, H = Proto.H, Tiles = Proto.TilesX * Proto.TilesY;

    /// <summary>
    /// The same data plane over real sockets on the loopback interface: <see cref="UdpLink"/>
    /// (socket, receive thread, pacing thread) on one side, a small panel in a thread on the
    /// other that drops a share of what it receives, stamps tiles like the firmware and acknowledges
    /// like it. Real time, not virtual, so it is short; what it adds over <see cref="Run"/> is the
    /// socket code and the threads, which the simulator does not touch.
    /// </summary>
    public static int RunLoopback(int seconds)
    {
        const int serverPort = 17789, panelPort = 17790;
        const double dropShare = 0.02;
        ushort session = 9;
        Console.WriteLine($"petla zwrotna UDP v5: {seconds} s czasu rzeczywistego, panel gubi {dropShare * 100:F0}% odebranych datagramow\n");

        using var panelSock = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        panelSock.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, panelPort));
        panelSock.ReceiveTimeout = 10;

        using var link = new UdpLink(System.Net.IPAddress.Loopback, panelPort, serverPort, session);
        var engine = link.Engine;

        var screen = new ushort[W * H];
        var tileSeq = new uint[Tiles];
        var tmp = new ushort[W * Proto.TileH];
        long rx = 0, dropped = 0, stale = 0;
        var stop = new CancellationTokenSource();

        var panel = new Thread(() =>
        {
            var buf = new byte[1500];
            var rng = new Random(5);
            var server = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, serverPort);
            System.Net.EndPoint from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
            uint highest = 0, bitmap = 0; bool any = false, gap = false; int pending = 0;
            var lastAck = Stopwatch.StartNew();
            while (!stop.IsCancellationRequested)
            {
                int n = 0;
                try { n = panelSock.ReceiveFrom(buf, ref from); }
                catch (System.Net.Sockets.SocketException) { }
                if (n >= Proto.DgHeaderBytes && buf[0] == Proto.DgData)
                {
                    if (rng.NextDouble() < dropShare) { dropped++; }
                    else
                    {
                        rx++;
                        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4));
                        int p = Proto.DgHeaderBytes;
                        for (int r = 0; r < buf[8]; r++)
                        {
                            int tx = buf[p], ty = buf[p + 1], tw = buf[p + 2], mode = buf[p + 3];
                            int len = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p + 4, 2));
                            p += Proto.DgRectHeaderBytes;
                            int w = tw * Proto.TileW;
                            SelfTest.Decode((byte)mode, buf.AsSpan(p, len), tmp, w * Proto.TileH);
                            p += len;
                            for (int i = 0; i < tw; i++)
                            {
                                int t = ty * Proto.TilesX + tx + i;
                                if ((int)(seq - tileSeq[t]) <= 0) { stale++; continue; }
                                tileSeq[t] = seq;
                                for (int y = 0; y < Proto.TileH; y++)
                                    Array.Copy(tmp, y * w + i * Proto.TileW, screen, (ty * Proto.TileH + y) * W + (tx + i) * Proto.TileW, Proto.TileW);
                            }
                        }
                        if (!any) { highest = seq; bitmap = 0; any = true; }
                        else if ((int)(seq - highest) > 0)
                        {
                            uint shift = seq - highest;
                            if (shift > 1) gap = true;
                            bitmap = shift > 32 ? 0 : ((shift == 32 ? 0 : bitmap << (int)shift) | (1u << (int)(shift - 1)));
                            highest = seq;
                        }
                        else { uint pos = highest - seq - 1; if (pos < 32) bitmap |= 1u << (int)pos; }
                        pending++;
                    }
                }
                if (any && (gap || pending >= 4 || (pending > 0 && lastAck.ElapsedMilliseconds >= 16)))
                {
                    var a = new byte[Proto.DgAckBytes];
                    a[0] = Proto.DgAck;
                    BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(2, 2), session);
                    BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4, 4), highest);
                    BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(8, 4), bitmap);
                    BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(12, 2), 300);
                    a[16] = 16;
                    panelSock.SendTo(a, server);
                    pending = 0; gap = false; lastAck.Restart();
                }
            }
        }) { IsBackground = true };
        panel.Start();
        link.Start();

        var enc = new TileEncoder { SplitLimit = Proto.DgMaxBytes - Proto.DgHeaderBytes - Proto.DgRectHeaderBytes, MaxRunTiles = 10, MaxFrameBytes = 16 * 1024 };
        var source = new SimSource(4);
        var cur = source.Cur;
        var prev = new ushort[W * H];
        var soft = new byte[W * H * 3];
        var strips = new List<StripRef>();
        bool first = true;

        var sw = Stopwatch.StartNew();
        long nextFrame = 0;
        long moveUntilMs = seconds * 1000L, endMs = moveUntilMs + 3000;
        while (sw.ElapsedMilliseconds < endMs)
        {
            long ms = sw.ElapsedMilliseconds;
            if (ms >= nextFrame)
            {
                nextFrame += 33;
                if (ms < moveUntilMs) source.Step();
                if (engine.CanAccept())
                {
                    engine.TakeForced(enc);
                    enc.Encode(cur, prev, first, soft);
                    first = false;
                    enc.GetStrips(strips);
                    if (strips.Count > 0) engine.Submit(enc.Payload, strips, repaint: enc.RepaintPending);
                }
            }
            Thread.Sleep(1);
        }
        // Let the last acknowledgements and resends land.
        Thread.Sleep(500);
        stop.Cancel();
        panel.Join(1000);

        int stuck = 0;
        for (int t = 0; t < Tiles; t++)
        {
            int x0 = t % Proto.TilesX * Proto.TileW, y0 = t / Proto.TilesX * Proto.TileH;
            bool same = true;
            for (int y = 0; y < Proto.TileH && same; y++)
                for (int x = 0; x < Proto.TileW; x++)
                    if (screen[(y0 + y) * W + x0 + x] != cur[(y0 + y) * W + x0 + x]) { same = false; break; }
            if (!same) stuck++;
        }

        bool ok = stuck == 0 && engine.SentDatagrams > 100 && engine.AckCount > 10 && dropped > 0 && engine.LostDatagrams > 0;
        Console.WriteLine($"[{(ok ? "OK " : "BLAD")}] petla zwrotna");
        Console.WriteLine($"       zbieznosc: kafli niezgodnych {stuck} z {Tiles}");
        Console.WriteLine($"       wyslano {engine.SentDatagrams} datagramow ({engine.SentBytes / 1024.0:F0} kB), panel odebral {rx}, zgubil {dropped}, nieaktualnych kafli {stale}");
        Console.WriteLine($"       serwer: potwierdzen {engine.AckCount}, zgubionych {engine.LostDatagrams} (falszywych {engine.SpuriousLosses}), kafli ponowionych {engine.ResentTiles}, " +
                          $"rtt {engine.SrttUs / 1000.0:F1} ms, cwnd {engine.Cwnd:F1}, tempo {engine.RateBytesPerSec / 1024:F0} kB/s, w locie {engine.InFlight}, w kolejce {engine.Queued}, bledow wysylania {link.SendErrors}");
        return ok ? 0 : 1;
    }

    private static int Scenario(string name, int seconds, double lossGood, bool burst, bool outages, int jitterMs, int seed,
                                double extraFreezeMs, double maxP99AgeMs)
    {
        long endUs = seconds * 1_000_000L;
        long settleUs = 4_000_000;                       // source held still at the end, to check convergence
        long warmupUs = 1_000_000;                       // the first repaint is not what is being judged

        var channel = new SimChannel(seed, lossGood, burst, outages, jitterMs);
        var panel = new PanelModel(channel);
        var engine = new UdpEngine(new ToPanel(channel), session: 7, nowUs: 0);
        var enc = new TileEncoder { SplitLimit = Proto.DgMaxBytes - Proto.DgHeaderBytes - Proto.DgRectHeaderBytes, MaxRunTiles = 10, MaxFrameBytes = 12 * 1024 };
        var source = new SimSource(seed);
        var cur = source.Cur;
        var prev = new ushort[W * H];
        var soft = new byte[W * H * 3];
        var strips = new List<StripRef>();
        var history = new TileHistory();

        long nextFrame = 0, lastSample = 0;
        bool first = true;
        var ages = new List<double>();
        long over250 = 0;

        var sw = Stopwatch.StartNew();
        for (long now = 0; now < endUs + settleUs; now += 250)
        {
            bool moving = now < endUs;
            panel.Measuring = moving && now >= warmupUs;

            if (now >= nextFrame)
            {
                nextFrame += 33_333;
                if (moving) source.Step();
                history.Update(cur, now);
                if (engine.CanAccept())
                {
                    engine.TakeForced(enc);
                    enc.Encode(cur, prev, first, soft);
                    first = false;
                    enc.GetStrips(strips);
                    if (strips.Count > 0) engine.Submit(enc.Payload, strips, repaint: enc.RepaintPending);
                    if (enc.RepaintPending) nextFrame = now;     // owed rows go out without waiting a frame period
                }
            }

            engine.Pump(now);
            channel.Deliver(now, panel, engine);
            panel.Tick(now);

            if (panel.Measuring && now - lastSample >= 10_000)
            {
                lastSample = now;
                long maxAge = 0;
                for (int t = 0; t < Tiles; t++)
                {
                    long a = history.AgeOf(t, panel.TileHash(t), now);
                    if (a > maxAge) maxAge = a;
                }
                ages.Add(maxAge / 1000.0);
                if (maxAge > 250_000) over250++;
            }
        }

        // Convergence: the source held still for the last few seconds, so nothing may differ.
        int stuck = 0;
        for (int t = 0; t < Tiles; t++) if (!panel.TileEquals(cur, t)) stuck++;

        ages.Sort();
        double P(double q) => ages.Count == 0 ? 0 : ages[Math.Min(ages.Count - 1, (int)(q * ages.Count))];
        double maxFreeze = panel.MaxFreezeUs / 1000.0;
        double maxOutage = channel.MaxOutageUs / 1000.0;
        double freezeLimit = maxOutage + extraFreezeMs;
        bool ok = stuck == 0 && maxFreeze <= freezeLimit && P(0.99) <= maxP99AgeMs;

        Console.WriteLine($"[{(ok ? "OK " : "BLAD")}] {name}");
        Console.WriteLine($"       zbieznosc po {settleUs / 1_000_000} s bez zmian: kafli niezgodnych {stuck} z {Tiles}");
        Console.WriteLine($"       wylaczenia radia: {channel.Outages}, lacznie {channel.OutageTotalUs / 1000.0:F0} ms, najdluzsze {maxOutage:F0} ms");
        Console.WriteLine($"       zamrozenia ekranu (>=250 ms bez zmiany obrazu): {panel.Freezes}, lacznie {panel.FreezeTotalUs / 1000.0:F0} ms, najdluzsze {maxFreeze:F0} ms (limit {freezeLimit:F0} = najdluzsze wylaczenie + {extraFreezeMs:F0})");
        Console.WriteLine($"       wiek najstarszego nieaktualnego kafla: mediana {P(0.5):F0} ms, p95 {P(0.95):F0} ms, p99 {P(0.99):F0} ms (limit {maxP99AgeMs:F0}), max {(ages.Count == 0 ? 0 : ages[^1]):F0} ms; " +
                          $"powyzej 250 ms przez {100.0 * over250 / Math.Max(1, ages.Count):F1}% czasu");
        Console.WriteLine($"       wyslano {engine.SentDatagrams} datagramow ({engine.SentBytes / 1024.0:F0} kB, {engine.SentBytes / 1024.0 / seconds:F0} kB/s), " +
                          $"zgubionych wg serwera {engine.LostDatagrams} ({100.0 * engine.LostDatagrams / Math.Max(1, engine.SentDatagrams):F2}%, falszywych {engine.SpuriousLosses}), kafli ponowionych {engine.ResentTiles}");
        Console.WriteLine($"       kanal zgubil {channel.DroppedToPanel} do panelu i {channel.DroppedToServer} ACK; panel odrzucil {panel.RingDrops} (pelny pierscien), " +
                          $"nieaktualnych prostokatow {panel.StaleRects}; srtt {engine.SrttUs / 1000.0:F1} ms, cwnd {engine.Cwnd:F1}, limit {engine.RateBytesPerSec / 1024:F0} kB/s, udzial strat {engine.LossShare * 100:F1}%; " +
                          $"symulacja trwala {sw.Elapsed.TotalSeconds:F1} s");
        Console.WriteLine();
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- pieces

    private sealed class ToPanel : IDatagramSink
    {
        private readonly SimChannel _ch;
        public ToPanel(SimChannel ch) => _ch = ch;
        public void Send(ReadOnlySpan<byte> d) => _ch.SendToPanel(d.ToArray());
    }

    /// <summary>Two directions, one radio: outages and loss bursts hit both.</summary>
    private sealed class SimChannel
    {
        private readonly Random _rng;
        private readonly double _lossGood;
        private readonly bool _burst, _outages;
        private readonly int _jitterUs;
        private bool _bad;
        private long _outageUntil, _nextOutage;
        private long _now;
        private readonly PriorityQueue<byte[], long> _toPanel = new(), _toServer = new();
        public long DroppedToPanel, DroppedToServer, Outages, OutageTotalUs, MaxOutageUs;

        public SimChannel(int seed, double lossGood, bool burst, bool outages, int jitterMs)
        {
            _rng = new Random(seed * 7919);
            _lossGood = lossGood; _burst = burst; _outages = outages; _jitterUs = jitterMs * 1000;
            _nextOutage = 8_000_000;
        }

        private bool Lost()
        {
            if (_outages && _now >= _nextOutage)
            {
                long len = 300_000 + (long)(_rng.NextDouble() * 600_000);
                _outageUntil = _now + len;
                _nextOutage = _outageUntil + (long)(-Math.Log(1 - _rng.NextDouble()) * 12_000_000);
                Outages++; OutageTotalUs += len; if (len > MaxOutageUs) MaxOutageUs = len;
            }
            if (_now < _outageUntil) return true;
            if (_burst)
            {
                // Gilbert-Elliott: short bad spells with heavy loss.
                if (_bad) { if (_rng.NextDouble() < 0.25) _bad = false; }
                else if (_rng.NextDouble() < 0.002) _bad = true;
                if (_bad) return _rng.NextDouble() < 0.6;
            }
            return _rng.NextDouble() < _lossGood;
        }

        private long DeliverAt() => _now + 4_000 + (long)(_rng.NextDouble() * _jitterUs);

        public void SendToPanel(byte[] d) { if (Lost()) { DroppedToPanel++; return; } _toPanel.Enqueue(d, DeliverAt()); }
        public void SendToServer(byte[] d) { if (Lost()) { DroppedToServer++; return; } _toServer.Enqueue(d, DeliverAt()); }

        public void Deliver(long now, PanelModel panel, UdpEngine engine)
        {
            _now = now;
            while (_toPanel.TryPeek(out var d, out long at) && at <= now) { _toPanel.Dequeue(); panel.OnDatagram(d, now); }
            while (_toServer.TryPeek(out var a, out long at2) && at2 <= now) { _toServer.Dequeue(); engine.OnAck(a, now); }
        }
    }

    /// <summary>The firmware's receive path: ring of 16 slots, drawTask service time, per-tile sequence stamps, ACK rules.</summary>
    private sealed class PanelModel
    {
        private const int Slots = 16;
        private readonly SimChannel _ch;
        private readonly ushort[] _screen = new ushort[W * H];
        private readonly uint[] _tileSeq = new uint[Tiles];
        private readonly Queue<(byte[] Data, uint Seq)> _ring = new();
        private readonly ushort[] _tmp = new ushort[W * Proto.TileH];
        private long _busyUntil, _lastChange;
        private uint _highest; private ulong _bitmap; private bool _any;
        private int _pending; private long _lastAck; private bool _gap;
        private byte _drops;
        public long RingDrops, StaleRects, Freezes, FreezeTotalUs, MaxFreezeUs;
        public bool Measuring;

        public PanelModel(SimChannel ch) => _ch = ch;

        public bool TileEquals(ushort[] src, int t)
        {
            int x0 = t % Proto.TilesX * Proto.TileW, y0 = t / Proto.TilesX * Proto.TileH;
            for (int y = 0; y < Proto.TileH; y++)
                for (int x = 0; x < Proto.TileW; x++)
                    if (_screen[(y0 + y) * W + x0 + x] != src[(y0 + y) * W + x0 + x]) return false;
            return true;
        }

        public uint TileHash(int t) => TileHistory.Hash(_screen, t);

        public void OnDatagram(byte[] d, long now)
        {
            if (d.Length < Proto.DgHeaderBytes || d[0] != Proto.DgData) return;
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4, 4));
            if (_ring.Count >= Slots) { RingDrops++; _drops = (byte)Math.Min(255, _drops + 1); return; }   // not acknowledged: the server sends the tiles again

            if (d[8] != 0) _ring.Enqueue((d, seq));
            if (!_any) { _highest = seq; _bitmap = 0; _any = true; }
            else if ((int)(seq - _highest) > 0)
            {
                uint shift = seq - _highest;
                if (shift > 1) _gap = true;
                _bitmap = shift > 32 ? 0 : (_bitmap << (int)shift) | (1UL << (int)(shift - 1));
                _highest = seq;
            }
            else
            {
                uint pos = _highest - seq - 1;
                if (pos < 32) _bitmap |= 1UL << (int)pos;
            }
            _pending++;
            MaybeAck(now);
        }

        private void MaybeAck(long now)
        {
            if (!_any) return;
            if (!(_gap || _pending >= 4 || (_pending > 0 && now - _lastAck >= 16_000))) return;
            var a = new byte[Proto.DgAckBytes];
            a[0] = Proto.DgAck;
            BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(2, 2), 7);
            BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4, 4), _highest);
            BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(8, 4), (uint)_bitmap);
            BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(12, 2), 300);
            a[16] = (byte)(Slots - _ring.Count);
            a[17] = _drops;
            _ch.SendToServer(a);
            _pending = 0; _gap = false; _drops = 0; _lastAck = now;
        }

        /// <summary>The drawTask: one datagram per service time, plus the ACK timer.</summary>
        public void Tick(long now)
        {
            while (_ring.Count > 0 && now >= _busyUntil)
            {
                var (d, seq) = _ring.Dequeue();
                if (Apply(d, seq)) NoteChange(now);
                _busyUntil = now + 1_500 + 300 * d[8];      // ~1.5 ms plus decode/draw per rectangle
            }
            MaybeAck(now);
        }

        private void NoteChange(long now)
        {
            if (Measuring && _lastChange != 0 && now - _lastChange >= 250_000)
            {
                long gap = now - _lastChange;
                Freezes++; FreezeTotalUs += gap;
                if (gap > MaxFreezeUs) MaxFreezeUs = gap;
            }
            _lastChange = now;
        }

        private bool Apply(byte[] d, uint seq)
        {
            bool changed = false;
            int n = d[8], p = Proto.DgHeaderBytes;
            for (int r = 0; r < n; r++)
            {
                int tx = d[p], ty = d[p + 1], tw = d[p + 2], mode = d[p + 3];
                int len = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 4, 2));
                p += Proto.DgRectHeaderBytes;
                int w = tw * Proto.TileW;

                int fresh = 0;
                for (int i = 0; i < tw; i++) if ((int)(seq - _tileSeq[ty * Proto.TilesX + tx + i]) > 0) fresh++;
                if (fresh == 0) { StaleRects++; p += len; continue; }

                SelfTest.Decode((byte)mode, d.AsSpan(p, len), _tmp, w * Proto.TileH);
                p += len;
                for (int i = 0; i < tw; i++)
                {
                    int t = ty * Proto.TilesX + tx + i;
                    if ((int)(seq - _tileSeq[t]) <= 0) continue;          // a newer datagram already drew this tile
                    _tileSeq[t] = seq;
                    changed = true;
                    for (int y = 0; y < Proto.TileH; y++)
                        Array.Copy(_tmp, y * w + i * Proto.TileW, _screen, (ty * Proto.TileH + y) * W + (tx + i) * Proto.TileW, Proto.TileW);
                }
            }
            return changed;
        }
    }

    /// <summary>
    /// The source's last few versions of every tile, with the time each appeared. Looking the
    /// panel's tile up here says how long it has been showing something the source already
    /// replaced, which is what a viewer perceives as lag, independent of how fast the source moves.
    /// </summary>
    private sealed class TileHistory
    {
        private const int Depth = 16;
        private readonly uint[] _hash = new uint[Tiles * Depth];
        private readonly long[] _time = new long[Tiles * Depth];
        private readonly int[] _head = new int[Tiles];
        private readonly int[] _count = new int[Tiles];

        public static uint Hash(ushort[] px, int t)
        {
            int x0 = t % Proto.TilesX * Proto.TileW, y0 = t / Proto.TilesX * Proto.TileH;
            uint h = 2166136261;
            for (int y = 0; y < Proto.TileH; y++)
            {
                int i = (y0 + y) * W + x0;
                for (int x = 0; x < Proto.TileW; x++) h = (h ^ px[i + x]) * 16777619;
            }
            return h;
        }

        public void Update(ushort[] cur, long now)
        {
            for (int t = 0; t < Tiles; t++)
            {
                uint h = Hash(cur, t);
                if (_count[t] > 0 && _hash[t * Depth + _head[t]] == h) continue;
                _head[t] = (_head[t] + 1) % Depth;
                _hash[t * Depth + _head[t]] = h;
                _time[t * Depth + _head[t]] = now;
                if (_count[t] < Depth) _count[t]++;
            }
        }

        /// <summary>Microseconds the panel's version of tile t has been out of date (0 = current).</summary>
        public long AgeOf(int t, uint panelHash, long now)
        {
            int c = _count[t];
            if (c == 0) return 0;
            for (int k = 0; k < c; k++)
            {
                if (_hash[t * Depth + (_head[t] - k + Depth) % Depth] != panelHash) continue;
                if (k == 0) return 0;
                return now - _time[t * Depth + (_head[t] - k + 1 + Depth) % Depth];   // when its successor appeared
            }
            return now - _time[t * Depth + (_head[t] - c + 1 + Depth) % Depth];       // older than the history
        }
    }

    /// <summary>A desktop-like source: static chrome, a scrolling waterfall, flickering tiles, a slow clock.</summary>
    private sealed class SimSource
    {
        private readonly ushort[] _cur = new ushort[W * H];
        private readonly Random _r;
        private readonly ushort[] _pal = new ushort[32];
        private int _frame;
        public ushort[] Cur => _cur;

        public SimSource(int seed)
        {
            _r = new Random(seed);
            for (int i = 0; i < _pal.Length; i++) _pal[i] = (ushort)_r.Next(1, 65535);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    _cur[y * W + x] = y < 24 ? (ushort)0x2945 : y >= 216 ? (ushort)0x1082 : (ushort)0x0000;
            // text-like block: two colours in glyph-sized cells
            for (int cy = 3; cy < 8; cy++)
                for (int cx = 0; cx < 40; cx++)
                    if (_r.Next(3) == 0)
                        for (int y = 0; y < 12; y++)
                            for (int x = 0; x < 6; x++)
                                if (_r.Next(2) == 0) _cur[(cy * 14 + y) * W + cx * 8 + x] = 0xFFFF;
        }

        public void Step()
        {
            _frame++;
            // waterfall: rows 120..215 scroll up, a new line of slowly varying colours enters at the bottom
            Array.Copy(_cur, 121 * W, _cur, 120 * W, 95 * W);
            for (int x = 0; x < W; x++)
                _cur[215 * W + x] = _pal[(x / 5 + _frame / 3 + _r.Next(2)) % _pal.Length];
            // flicker: a handful of tiles change to two-colour patterns
            for (int k = 0; k < 5; k++)
            {
                int tx = _r.Next(Proto.TilesX), ty = _r.Next(3, 7);
                ushort a = _pal[_r.Next(_pal.Length)], b = _pal[_r.Next(_pal.Length)];
                for (int y = 0; y < Proto.TileH; y++)
                    for (int x = 0; x < Proto.TileW; x++)
                        _cur[(ty * Proto.TileH + y) * W + tx * Proto.TileW + x] = ((x ^ y) & 1) == 0 ? a : b;
            }
            // clock: one tile that changes every 30 frames
            if (_frame % 30 == 0)
                for (int y = 0; y < Proto.TileH; y++)
                    for (int x = 0; x < Proto.TileW; x++)
                        _cur[y * W + 18 * Proto.TileW + x] = (ushort)(_frame * 31 + x + y);
        }
    }
}
