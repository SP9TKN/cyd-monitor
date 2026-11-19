using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace CydMonitor;

/// <summary>
/// Feeds the panel's scanner page from SDR Oxide, and opens that page over the
/// mirror while somebody is transmitting.
///
/// Everything comes from Oxide's built-in Hamlib rigctld servers, one per radio:
/// frequency, mode and level are polled, because TCI reports no level and rigctld
/// answers all three in under a millisecond. It runs for the whole session, since
/// a transmission has to be noticed while the mirror is on screen.
///
/// Neither protocol says what the scanner is doing, so that is read from the
/// numbers. A sweep moves on every ~150 ms, so a frequency that stays put is a stop.
/// A stop alone is not a transmission, because a scanner switched off stays put too:
/// that takes a level over the squelch Oxide itself uses. Buttons go through
/// <see cref="OxideUi"/>, since no protocol reaches the scanner.
/// </summary>
internal sealed partial class ScannerBridge : IDisposable
{
    internal static readonly int[] RigPorts = [4532, 4533];
    private const int PollMs = 100;
    // Twice a channel's listen time plus a poll: a sweep can't look like this.
    private const int HoldMs = 400;
    private const int LevelEveryMs = 250;
    private const int SendEveryMs = 1000;
    private const int NoticeMs = 4000;
    private const int MuteEveryMs = 500;
    // How long the panel says the program is on its way.
    private const int LaunchMs = 30000;
    // A second STOP sooner than this is the touch bouncing, not a request to close the program.
    private const int StopAgainMs = 1000;
    // Speech dips under the squelch between words. The scanner's own resume grace
    // ends a real stop sooner than this; only a scanner that is off ever reaches it.
    private const int TxDropMs = 3000;
    // A frozen Oxide answers with the same frequency and the same level, read after read.
    // A live one moves the level now and then even on an empty channel, so this long with
    // neither moving goes in the log, to be laid next to Oxide's own.
    private const int StillLogMs = 15_000;
    // The page stays this long after the last transmission, so a conversation doesn't
    // repaint the whole mirror between every two overs.
    private const int PopLingerMs = 1000;
    // Oxide's S-meter scale: S9 = -73 dBm, and STRENGTH is dB relative to S9.
    private const int S9Dbm = -73;

    private sealed class RadioState
    {
        public long Hz, SinceMs, AboveMs;
        public bool Held, Online, Transmitting, SoundOff;
        public long StillHz, StillSinceMs;
        public int StillStrength;
        public bool StillLogged;
        public volatile bool ToggleSound;
        public string Name = "", Freq = "";
        public int Dbm;
        public string Notice = "";
        public long NoticeUntil;
    }

    private readonly Action<byte[]> _send;
    private readonly RadioState[] _radios = [new(), new()];
    private readonly OxideConfig _config = new();
    private readonly SoundCard _card = new();
    private readonly Channel<byte> _cmds = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _poll, _worker;

    private readonly object _popLock = new();
    private bool _pageOpen, _popped, _dismissed;
    private long _lastTxMs = -PopLingerMs;
    private long _launchUntil, _lastStopMs = -StopAgainMs;
    private volatile bool _resend;
    private volatile bool _muted;

    public ScannerBridge(Action<byte[]> send)
    {
        _send = send;
        _poll = Task.Factory.StartNew(PollLoop, TaskCreationOptions.LongRunning);
        _worker = Task.Run(WorkerLoop);
    }

    /// <summary>
    /// Reader thread: the panel reports its page. The page going away while it was
    /// this class that opened it means the user closed it, and it stays closed
    /// until the transmission is over.
    /// </summary>
    public void PageOpened(bool open)
    {
        lock (_popLock)
        {
            _pageOpen = open;
            if (!open && _popped) { _popped = false; _dismissed = true; }
        }
        _resend = true;
    }

    /// <summary>Reader thread: returns at once, the press happens on the worker.</summary>
    public void Command(byte cmd) => _cmds.Writer.TryWrite(cmd);

    private void PollLoop()
    {
        var rigs = RigPorts.Select(p => new RigLink(p)).ToArray();
        byte[]? last = null;
        long lastSent = 0, lastMute = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                long now = Environment.TickCount64;
                for (int i = 0; i < _radios.Length; i++) Update(i, rigs[i], now);
                UpdatePopup(now);

                if (now - lastMute >= MuteEveryMs)
                {
                    lastMute = now;
                    _muted = _card.IsMuted(_config.AudioOutput()) == true;
                }

                bool open;
                lock (_popLock) open = _pageOpen;
                if (open)
                {
                    byte[] msg = Build(now);
                    if (_resend || last is null || Differs(msg, last, levelToo: false) || now - lastSent >= SendEveryMs ||
                        (now - lastSent >= LevelEveryMs && Differs(msg, last, levelToo: true)))
                    {
                        _resend = false;
                        _send(msg);
                        last = msg;
                        lastSent = now;
                    }
                }
                _cts.Token.WaitHandle.WaitOne(PollMs);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The mirror must outlive anything that goes wrong here.
            Console.WriteLine($"\n  [skaner] petla odczytu zakonczona: {e.GetType().Name}: {e.Message}");
        }
    }

    private void Update(int i, RigLink rig, long now)
    {
        var r = _radios[i];
        if (!rig.TryRead(out long hz, out string mode, out int strength, out bool soundOff))
        {
            r.Online = r.Held = r.Transmitting = false;
            r.Hz = 0;
            return;
        }
        r.Online = true;
        r.Dbm = S9Dbm + strength;
        r.SoundOff = soundOff;
        if (hz != r.StillHz || strength != r.StillStrength)
        {
            if (r.StillLogged)
                Console.WriteLine($"\n  [skaner] radio {i + 1}: ruszylo po {(now - r.StillSinceMs) / 1000} s bez zmian");
            r.StillHz = hz;
            r.StillStrength = strength;
            r.StillSinceMs = now;
            r.StillLogged = false;
        }
        else if (!r.StillLogged && now - r.StillSinceMs >= StillLogMs)
        {
            r.StillLogged = true;
            Console.WriteLine($"\n  [skaner] radio {i + 1}: {FormatMhz(hz)} i poziom stoja bez zmian od {StillLogMs / 1000} s");
        }
        // Here, because this is the thread that talks to rigctld. It is Oxide's own mute, so
        // the radio's MUTE button follows; a read lags the change by a moment.
        if (r.ToggleSound)
        {
            r.ToggleSound = false;
            rig.SetMute(!soundOff);
        }
        if (hz != r.Hz)
        {
            r.Hz = hz;
            r.SinceMs = now;
            r.Held = r.Transmitting = false;
        }
        if (!r.Held && now - r.SinceMs >= HoldMs)
        {
            r.Held = true;
            r.Name = _config.ChannelName(i, hz, rig.ReadMemory());
            r.Freq = FormatMhz(hz);
        }
        if (!r.Held) return;
        if (r.Dbm >= _config.SquelchDbm(i, mode))
        {
            r.AboveMs = now;
            r.Transmitting = true;
        }
        else if (now - r.AboveMs >= TxDropMs)
        {
            r.Transmitting = false;
        }
    }

    private void UpdatePopup(long now)
    {
        bool tx = _radios.Any(r => r.Transmitting);
        if (tx) _lastTxMs = now;
        bool wanted = now - _lastTxMs < PopLingerMs;
        byte? say = null;
        lock (_popLock)
        {
            if (wanted)
            {
                if (!_popped && !_pageOpen && !_dismissed) { _popped = true; say = 1; }
            }
            else
            {
                if (_popped) { _popped = false; say = 0; }
                _dismissed = false;
            }
        }
        if (say is { } open) _send([Proto.MsgScanPop, open]);
    }

    private byte[] Build(long now)
    {
        var m = new byte[2 + Proto.ScanRadios * Proto.ScanRecBytes];
        m[0] = Proto.MsgScan;
        m[1] = _muted ? Proto.ScanFlagMuted : (byte)0;
        for (int i = 0; i < _radios.Length; i++)
            if (_radios[i].SoundOff) m[1] |= (byte)(Proto.ScanFlagRadio0Off << i);
        for (int i = 0; i < _radios.Length; i++)
        {
            var r = _radios[i];
            var rec = m.AsSpan(2 + i * Proto.ScanRecBytes, Proto.ScanRecBytes);
            bool notice = now < r.NoticeUntil;
            // Standing still with nobody on the air is a scanner switched off, not a sweep.
            rec[0] = notice || !r.Online ? Proto.ScanOffline
                   : r.Transmitting ? Proto.ScanHold
                   : r.Held ? Proto.ScanOffline : Proto.ScanRunning;
            BinaryPrimitives.WriteInt16LittleEndian(rec.Slice(1, 2), (short)(r.Transmitting ? r.Dbm : 0));
            string name = notice ? r.Notice
                        : !r.Online ? (_config.IsOff(i) ? "RADIO WYLACZONE"
                                       : now < Volatile.Read(ref _launchUntil) ? "URUCHAMIAM OXIDE" : "OXIDE WYLACZONY")
                        : r.Transmitting ? r.Name
                        : r.Held ? r.Name : "SKANUJE";
            string freq = !notice && r.Held ? r.Freq : "";
            Encoding.ASCII.GetBytes(name.AsSpan(0, Math.Min(name.Length, Proto.ScanNameBytes - 1)), rec.Slice(3, Proto.ScanNameBytes));
            Encoding.ASCII.GetBytes(freq.AsSpan(0, Math.Min(freq.Length, Proto.ScanFreqBytes - 1)), rec.Slice(3 + Proto.ScanNameBytes, Proto.ScanFreqBytes));
        }
        return m;
    }

    /// <summary>True when the messages differ anywhere but in the two dBm fields, or anywhere at all with <paramref name="levelToo"/>.</summary>
    private static bool Differs(byte[] a, byte[] b, bool levelToo)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            int inRec = (i - 2) % Proto.ScanRecBytes;
            if (levelToo || inRec is not (1 or 2)) return true;
        }
        return false;
    }

    private async Task WorkerLoop()
    {
        try
        {
            await foreach (byte cmd in _cmds.Reader.ReadAllAsync(_cts.Token))
            {
                Console.WriteLine($"[skaner] przycisk {cmd} z panelu");
                if (cmd >= Proto.ScanCmdSound && cmd < Proto.ScanCmdSound + Proto.ScanRadios)
                {
                    _radios[cmd - Proto.ScanCmdSound].ToggleSound = true;
                    continue;
                }
                if (cmd == Proto.ScanCmdMute)
                {
                    string card = _config.AudioOutput();
                    if (_card.Toggle(card)) _muted = _card.IsMuted(card) == true;
                    else Console.WriteLine($"\n  [skaner] nie ma karty dzwiekowej '{card}'");
                    _resend = true;
                    continue;
                }
                // START with no program brings the program up; the next one starts the scan.
                if (cmd == Proto.ScanCmdStart && !OxideUi.IsRunning())
                {
                    Console.WriteLine("[skaner] uruchamiam Oxide");
                    try
                    {
                        OxideUi.StartApp();
                        Volatile.Write(ref _launchUntil, Environment.TickCount64 + LaunchMs);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                        Console.WriteLine($"[skaner] nie uruchomilem Oxide: {e.GetType().Name}: {e.Message}");
                    }
                    continue;
                }

                var targets = new List<(int, long)>();
                for (int i = 0; i < _radios.Length; i++)
                {
                    var r = _radios[i];
                    if (r.Online && (cmd != Proto.ScanCmdNext || r.Held)) targets.Add((i, Volatile.Read(ref r.Hz)));
                }
                bool nothingToPress = targets.Count > 0 && PressAll(cmd, targets);

                // STOP with every scanner already off closes the program.
                if (cmd == Proto.ScanCmdStop)
                {
                    long now = Environment.TickCount64;
                    if (nothingToPress && now - _lastStopMs >= StopAgainMs)
                    {
                        Console.WriteLine("[skaner] zamykam Oxide");
                        OxideUi.CloseApp();
                    }
                    _lastStopMs = now;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// START, STOP or NEXT straight to the radio's own scanner over rigctld (latka_skan.py), so
    /// nothing is clicked. The radios this Oxide can't drive so are handed back for the GUI;
    /// <paramref name="pressed"/> says whether anything changed.
    /// </summary>
    internal static List<(int Radio, long Hz)> PressByRig(byte cmd, List<(int Radio, long Hz)> targets, ref bool pressed)
    {
        var left = new List<(int Radio, long Hz)>();
        foreach (var t in targets)
        {
            var rig = new RigLink(RigPorts[t.Radio]);
            // An Oxide that can't say whether it scans can't be told either: the GUI it is.
            if (rig.ReadScanning() is not { } on)
            {
                left.Add(t);
                continue;
            }
            string? what = cmd switch
            {
                Proto.ScanCmdStart => on ? null : "MEM",
                Proto.ScanCmdStop => on ? "STOP" : null,
                // A stopped scanner (a channel clicked, a frequency set) is started and moved on
                // at once, or it would only stop again on the busy channel it stands on.
                Proto.ScanCmdNext => on ? "NEXT" : "MEM+NEXT",
                OxideUi.ScanToggle => on ? "STOP" : "MEM",
                _ => "?",
            };
            if (what is null) continue;
            bool sent = what != "?" && what.Split('+').All(rig.TryScan);
            // Every command in the log, so a press that seems to do nothing can be traced.
            Console.WriteLine($"[skaner] radio {t.Radio}, komenda {cmd}: skaner {(on ? "chodzi" : "stoi")}, rigctld g {what}{(sent ? "" : " NIE PRZESZLO")}");
            if (!sent) left.Add(t);
            else pressed = true;
        }
        return left;
    }

    /// <summary>True when every radio was already in the state asked for and nothing went wrong.</summary>
    private bool PressAll(byte cmd, List<(int Radio, long Hz)> targets)
    {
        bool pressed = false;
        targets = PressByRig(cmd, targets, ref pressed);
        if (targets.Count == 0) return !pressed;
        List<(int Radio, string Problem)> problems;
        bool clicked = false;
        try { problems = OxideUi.Press(cmd, targets, out clicked); }
        catch (Exception e)
        {
            // Already tried several times over. Nothing on the panel: another press is all
            // anyone can do about it, and the log has the reason.
            Console.WriteLine($"[skaner] przycisk {cmd} nie przeszedl: {e.GetType().Name}: {e.Message}");
            return false;
        }
        foreach (var (radio, problem) in problems)
        {
            Console.WriteLine($"[skaner] radio {radio}, komenda {cmd}: {problem}");
            _radios[radio].Notice = problem;
            _radios[radio].NoticeUntil = Environment.TickCount64 + NoticeMs;
        }
        return !pressed && !clicked && problems.Count == 0;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll([_poll, _worker], 1500); } catch (AggregateException) { }
        _cts.Dispose();
    }

    /// <summary>"145.725 MHz", with more digits only where the channel has them: "438.9375 MHz".</summary>
    internal static string FormatMhz(long hz)
    {
        string frac = (hz % 1_000_000).ToString("D6").TrimEnd('0').PadRight(3, '0');
        return $"{hz / 1_000_000}.{frac} MHz";
    }

    /// <summary>--skaner-test and --skaner-okno: what the panel would get, with the page open or with the mirror on, for a look without a panel.</summary>
    internal static int RunConsole(int seconds, bool pageOpen)
    {
        string lastLine = "";
        void Show(byte[] m)
        {
            if (m[0] == Proto.MsgScanPop)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.f} OKNO {(m[1] != 0 ? "WYSKAKUJE" : "ZNIKA")}");
                return;
            }
            var sb = new StringBuilder($"flagi={m[1]} ");
            for (int i = 0; i < Proto.ScanRadios; i++)
            {
                var rec = m.AsSpan(2 + i * Proto.ScanRecBytes, Proto.ScanRecBytes);
                string name = Encoding.ASCII.GetString(rec.Slice(3, Proto.ScanNameBytes)).TrimEnd('\0');
                string freq = Encoding.ASCII.GetString(rec.Slice(3 + Proto.ScanNameBytes, Proto.ScanFreqBytes)).TrimEnd('\0');
                sb.Append($"[{i}] st={rec[0]} {name} | {freq} | {BinaryPrimitives.ReadInt16LittleEndian(rec.Slice(1, 2))} dBm   ");
            }
            string line = sb.ToString();
            if (line != lastLine) Console.WriteLine($"{DateTime.Now:HH:mm:ss.f} {line}");
            lastLine = line;
        }
        using (var bridge = new ScannerBridge(Show))
        {
            bridge.PageOpened(pageOpen);
            Thread.Sleep(seconds * 1000);
        }
        return 0;
    }

    /// <summary>
    /// One rigctld, asked and hung up on every poll. Oxide serves a connection from a thread
    /// spinning on its socket, a full core for as long as it stays open, so none may stay
    /// open between polls. Oxide can be closed and reopened at will.
    /// </summary>
    internal sealed class RigLink(int port)
    {
        private long _retryAt;
        private static readonly byte[] Ask = "f\nm\nl STRENGTH\nu MUTE\n"u8.ToArray();

        public bool TryRead(out long hz, out string mode, out int strength, out bool muted)
        {
            hz = 0;
            mode = "";
            strength = 0;
            muted = false;
            try
            {
                using var c = Connect();
                if (c is null) return false;
                c.GetStream().Write(Ask);
                using var input = new StreamReader(c.GetStream(), Encoding.ASCII);
                string? f = input.ReadLine(), m = input.ReadLine(), passband = input.ReadLine(), s = input.ReadLine(), u = input.ReadLine();
                if (long.TryParse(f, out hz) && passband is not null && int.TryParse(s, out strength))
                {
                    mode = m ?? "";
                    muted = u == "1";
                    return true;
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
            return false;
        }

        public void SetMute(bool mute)
        {
            try
            {
                using var c = Connect();
                if (c is null) return;
                c.GetStream().Write(mute ? "U MUTE 1\n"u8 : "U MUTE 0\n"u8);
                using var input = new StreamReader(c.GetStream(), Encoding.ASCII);
                if (input.ReadLine() is not "RPRT 0") Console.WriteLine($"[skaner] port {port}: Oxide nie przyjal zmiany dzwieku");
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
        }

        private static readonly byte[] AskAll = "f\nm\nl STRENGTH\nu MUTE\nl AF\ne\nl SQL\n"u8.ToArray();

        /// <summary>
        /// What the web page shows. The last two answers only a patched Oxide can give, so they may be
        /// missing; squelch stays null then, which is how the page knows it can't set it either.
        /// </summary>
        public bool TryReadAll(out long hz, out string mode, out int strength, out bool muted, out double volume, out int memory, out int bandwidth, out double? squelch)
        {
            hz = 0;
            mode = "";
            strength = 0;
            muted = false;
            volume = 0;
            memory = 0;
            bandwidth = 0;
            squelch = null;
            try
            {
                using var c = Connect();
                if (c is null) return false;
                c.GetStream().Write(AskAll);
                using var input = new StreamReader(c.GetStream(), Encoding.ASCII);
                string? f = input.ReadLine(), m = input.ReadLine(), passband = input.ReadLine(), s = input.ReadLine(), u = input.ReadLine(), a = input.ReadLine();
                if (!long.TryParse(f, out hz) || passband is null || !int.TryParse(s, out strength)) return false;
                mode = m ?? "";
                muted = u == "1";
                _ = int.TryParse(passband, out bandwidth);
                double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out volume);
                try
                {
                    _ = int.TryParse(input.ReadLine(), out memory);
                    if (double.TryParse(input.ReadLine(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sql)) squelch = sql;
                }
                catch (IOException) { }
                return true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { return false; }
        }

        public void SetVolume(double volume) => Say(FormattableString.Invariant($"L AF {volume:0.###}"));

        public void SetSquelch(double db) => Say(FormattableString.Invariant($"L SQL {db:0.#}"));

        /// <summary>Takes effect until the scanner stops on a channel, which brings its own filter.</summary>
        public void SetBandwidth(string mode, int hz) => Say($"M {mode} {hz}");

        /// <summary>Oxide ends a scan on this radio when told a frequency.</summary>
        public void SetFrequency(long hz) => Say($"F {hz}");

        /// <summary>A recall if this Oxide knows the command; false if it doesn't, and nothing has changed.</summary>
        public bool TryRecall(int id) => Say($"E {id}") is "RPRT 0";

        /// <summary>Oxide ends a scan on this radio when told a frequency.</summary>
        public void TuneTo(long hz, string mode)
        {
            Say($"F {hz}");
            Say($"M {mode} 0");
        }

        /// <summary>Where the radio is now, 0 when it can't be asked.</summary>
        public long ReadHz() => long.TryParse(Say("f"), out long hz) ? hz : 0;

        /// <summary>
        /// Moves the radio by whole steps, landing on a multiple of the step as Oxide's own tuning
        /// does. Oxide ends a scan on that radio, and only there, when told a frequency.
        /// </summary>
        public void Tune(int ticks, long stepHz)
        {
            long hz = ReadHz();
            if (hz == 0) return;
            long from = (ticks > 0 ? hz : hz + stepHz - 1) / stepHz * stepHz;
            Say($"F {from + ticks * stepHz}");
        }

        /// <summary>The memory channel last recalled or scanned here, 0 when there is none or it can't be asked.</summary>
        public int ReadMemory() => int.TryParse(Say("e"), out int id) && id > 0 ? id : 0;

        /// <summary>Whether the radio's own scanner runs; null when this Oxide can't say (no latka_skan.py).</summary>
        public bool? ReadScanning() => Say("u SCAN") switch { "1" => true, "0" => false, _ => null };

        /// <summary>MEM (start), STOP or NEXT to the radio's own scanner; false when this Oxide can't.</summary>
        public bool TryScan(string what) => Say($"g {what} 0") is "RPRT 0";

        /// <summary>Memory <paramref name="id"/> out of the memory scan or back in (latka_pomin.py); false when this Oxide can't.</summary>
        public bool TryScanSkip(int id, bool skip) => Say($"g {(skip ? "EXCL" : "INCL")} {id}") is "RPRT 0";

        /// <summary>
        /// Recalls a channel with all it stores: filter, shift and tone as well. Only the patched
        /// Oxide knows this command (latka_pamiec.py). A recall ends a scan on this radio.
        /// </summary>
        public void Recall(int id)
        {
            string? answer = Say($"E {id}");
            if (answer is not "RPRT 0") Console.WriteLine($"[dial] port {port}: Oxide nie przywolal kanalu {id}: {answer}");
        }

        private string? Say(string line)
        {
            try
            {
                using var c = Connect();
                if (c is null) return null;
                c.GetStream().Write(Encoding.ASCII.GetBytes(line + "\n"));
                using var input = new StreamReader(c.GetStream(), Encoding.ASCII);
                return input.ReadLine();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { return null; }
        }

        private TcpClient? Connect()
        {
            if (Environment.TickCount64 < _retryAt) return null;
            var c = new TcpClient { NoDelay = true };
            try
            {
                if (c.ConnectAsync("127.0.0.1", port).Wait(300))
                {
                    c.ReceiveTimeout = 500;
                    c.SendTimeout = 500;
                    return c;
                }
            }
            catch (AggregateException) { }
            c.Dispose();
            _retryAt = Environment.TickCount64 + 2000;
            return null;
        }
    }

    /// <summary>
    /// What the bridge needs from Oxide's own settings: channel names, the squelch that
    /// makes a level a transmission, and the sound card. All files Oxide writes as it
    /// runs, so they are looked at again every few seconds.
    /// </summary>
    internal sealed partial class OxideConfig
    {
        private static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sdroxide", "sdroxide", "config");
        private static readonly string[] RadioDirs = [Root, Path.Combine(Root, "radio-1")];
        private const int ReloadMs = 5000;
        // For a mode with no saved squelch; Oxide's own settings cover every mode scanned here.
        private const int DefaultSquelchDbm = -100;

        private readonly record struct Mem(int Id, long Hz, int Folder, string Name);
        private List<Mem> _mems = [];
        private readonly HashSet<int>[] _folders = [[], []];
        private readonly Dictionary<string, double>[] _squelch = [[], []];
        private double _calDb;
        private readonly double[] _tunerDb = new double[RadioDirs.Length];
        private readonly bool[] _off = new bool[RadioDirs.Length];
        private string _audio = "";
        private DateTime _stamp;
        private long _checkedAt = -ReloadMs;

        /// <summary>
        /// The channel the radio stands on, "VFO" off every channel. Oxide's own word comes first,
        /// as frequencies are shared, but it keeps the last channel after a hand has tuned away,
        /// so it counts only on that channel's frequency.
        /// </summary>
        public string ChannelName(int radio, long hz, int memory = 0)
        {
            Reload();
            Mem? any = null, folder = null;
            foreach (var m in _mems)
            {
                if (Math.Abs(m.Hz - hz) > 100) continue;
                if (m.Id == memory) return m.Name;
                if (_folders[radio].Contains(m.Folder)) folder ??= m;
                any ??= m;
            }
            // Off every memory the frequency is the name, as on the web page.
            return (folder ?? any)?.Name ?? ScannerBridge.FormatMhz(hz).Replace(" MHz", "");
        }

        /// <summary>
        /// The channel so many places from where this radio is, 0 with no channels. The list is the
        /// folders its scanner has selected (none selected is every channel) in stored order, skipped
        /// channels too, and it wraps round. Tuned off the list, the first press lands on an end.
        /// </summary>
        public int MemoryFrom(int radio, int current, long hz, int by)
        {
            Reload();
            var list = _mems.FindAll(m => _folders[radio].Count == 0 || _folders[radio].Contains(m.Folder));
            if (list.Count == 0) return 0;
            int at = list.FindIndex(m => m.Id == current);
            if (at < 0) at = list.FindIndex(m => Math.Abs(m.Hz - hz) <= 100);
            if (at < 0) at = by > 0 ? -1 : 0;
            return list[((at + by) % list.Count + list.Count) % list.Count].Id;
        }

        public int SquelchDbm(int radio, string rigMode)
        {
            Reload();
            string mode = rigMode switch { "FM" => "Nfm", "WFM" => "Wfm", "AM" => "Am", "AMS" => "Sam", _ => "" };
            // The squelch is a dBFS rail at the converter. The meter is the level at the antenna: that
            // minus the tuner's gain (since Oxide 2.2, for an RTL-SDR too), plus the S-meter's offset.
            return _squelch[radio].TryGetValue(mode, out double db) ? (int)Math.Round(db - _tunerDb[radio] + _calDb) : DefaultSquelchDbm;
        }

        public string AudioOutput()
        {
            Reload();
            return _audio;
        }

        /// <summary>Switched off in Oxide's own list of radios: one receiver scanning both bands.</summary>
        public bool IsOff(int radio)
        {
            Reload();
            return _off[radio];
        }

        private void Reload()
        {
            long now = Environment.TickCount64;
            if (now - _checkedAt < ReloadMs) return;
            _checkedAt = now;
            ReloadTuners();
            string memories = Path.Combine(Root, "memories.json"), toml = Path.Combine(Root, "config.toml"), roster = Path.Combine(Root, "radios.json");
            try
            {
                var stamp = RadioDirs.SelectMany(d => new[] { Path.Combine(d, "scanner.json"), Path.Combine(d, "modeprofiles.json") })
                                     .Append(memories).Append(toml).Append(roster).Max(File.GetLastWriteTimeUtc);
                if (stamp == _stamp) return;

                using (var doc = JsonDocument.Parse(File.ReadAllBytes(roster)))
                    foreach (var r in doc.RootElement.GetProperty("radios").EnumerateArray())
                        if (r.GetProperty("id").GetInt32() is var id && id >= 0 && id < _off.Length)
                            _off[id] = r.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.False;

                var mems = new List<Mem>();
                using (var doc = JsonDocument.Parse(File.ReadAllBytes(memories)))
                    foreach (var e in doc.RootElement.EnumerateArray())
                        mems.Add(new Mem(e.GetProperty("id").GetInt32(),
                                         (long)Math.Round(e.GetProperty("freq_hz").GetDouble()),
                                         e.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : -1,
                                         Short(Fold(e.GetProperty("name").GetString() ?? ""))));
                _mems = mems;

                for (int i = 0; i < RadioDirs.Length; i++)
                {
                    _folders[i].Clear();
                    using (var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RadioDirs[i], "scanner.json"))))
                        if (doc.RootElement.TryGetProperty("folders", out var folders))
                            foreach (var e in folders.EnumerateArray())
                                if (e.ValueKind == JsonValueKind.Number) _folders[i].Add(e.GetInt32());

                    _squelch[i].Clear();
                    using (var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RadioDirs[i], "modeprofiles.json"))))
                        if (doc.RootElement.TryGetProperty("modes", out var modes))
                            foreach (var p in modes.EnumerateObject())
                                if (p.Value.TryGetProperty("squelch_db", out var sq)) _squelch[i][p.Name] = sq.GetDouble();
                }

                string text = File.ReadAllText(toml);
                var cal = CalRx().Match(text);
                _calDb = cal.Success ? double.Parse(cal.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                var audio = AudioRx().Match(text);
                _audio = audio.Success ? audio.Groups[1].Value : "";
                _stamp = stamp;
            }
            // Oxide may be saving right now; the next look finds the file whole.
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { }
        }

        /// <summary>On its own: Oxide rewrites this file all the time, and the rest need not be read again for it.</summary>
        private void ReloadTuners()
        {
            for (int i = 0; i < RadioDirs.Length; i++)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RadioDirs[i], "session.json")));
                    if (!doc.RootElement.TryGetProperty("gains", out var gains)) continue;
                    foreach (var g in gains.EnumerateArray())
                        if (g[0].GetString() == "TUNER") _tunerDb[i] = g[1].GetDouble();
                }
                // Oxide may be saving right now; the last value holds until the next look.
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or IndexOutOfRangeException) { }
            }
        }

        [GeneratedRegex(@"(?m)^\s*cal_offset_db\s*=\s*(-?[\d.]+)")]
        private static partial Regex CalRx();

        [GeneratedRegex(@"(?m)^\s*audio_output\s*=\s*""([^""]*)""")]
        private static partial Regex AudioRx();

        /// <summary>
        /// The callsign alone: "SR9XYZ Town" is SR9XYZ. A simplex channel has none
        /// and is known by its frequency: "SX 145.500 Wywolawczy" is SX 145.500.
        /// </summary>
        private static string Short(string name)
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return name;
            return words[0] == "SX" && words.Length > 1 ? $"SX {words[1]}" : words[0];
        }

        /// <summary>The panel's fonts are ASCII, so Polish letters lose their marks.</summary>
        private static string Fold(string s)
        {
            const string From = "ąćęłńóśźżĄĆĘŁŃÓŚŹŻ", To = "ACELNOSZZACELNOSZZ";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                int i = From.IndexOf(c);
                char u = i >= 0 ? To[i] : char.ToUpperInvariant(c);
                sb.Append(u is >= ' ' and <= '~' ? u : '?');
            }
            return sb.ToString();
        }
    }
}
