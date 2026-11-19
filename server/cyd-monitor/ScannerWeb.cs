using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CydMonitor;

/// <summary>
/// The scanner as a web page, for any browser on the network: what each radio is doing, its
/// channels, spectrum and waterfall, and the controls. It lives here and not on the panel's
/// own page because the ESP32 has neither the memory nor the time for it while it draws, so
/// that page only links here.
///
/// All of this costs nothing while nobody looks: rigctld is polled and the IQ streams are open
/// only with a viewer connected. rigctld is what <see cref="ScannerBridge"/> reads as well, on
/// connections of its own. The spectrum comes from Oxide's TCI servers, the one thing they
/// have that rigctld lacks, and is computed here, a bounded amount of it per frame. A client
/// of TCI only ever asks for IQ, so the transmitter stays out of reach of this page.
///
/// There is no sign-in, as on the panel's page, so the port is for the local network.
/// </summary>
internal static class ScannerWeb
{
    private const int Port = Proto.Port + 5;
    private static readonly int[] TciPorts = [50001, 50002];
    // One reading of both radios every this many milliseconds, however long the reading took:
    // the scanner moves on every ~100 ms, and a page that read every 100 ms plus the reading's own
    // time (some 150 ms all told) missed a third of the channels and looked slower than Oxide.
    private const int PollMs = 50;
    // The same reading of the numbers as in ScannerBridge: see the notes there.
    private const int HoldMs = 400;
    private const int TxDropMs = 3000;
    private const int S9Dbm = -73;
    private const int MuteEveryMs = 500;
    private const int StampEveryMs = 5000;
    // A page being reloaded comes straight back, so the polling need not stop for it.
    private const int ViewerGraceMs = 5000;
    private const int WriteTimeoutMs = 1500;
    private const int IqRetryMs = 3000;
    private const int IqRate = 250_000;
    private const int AudioRate = 16_000, AudioDecimate = 3, AudioTickMs = 40;
    private const int AudioHeld = AudioRate / 10, AudioMaxHeld = AudioRate / 2;
    // What Oxide itself tells a TCI client it can tune.
    private const long MinHz = 100_000, MaxHz = 1_766_000_000;

    private sealed class Viewer(TcpClient client, bool spectrum)
    {
        public readonly TcpClient Client = client;
        public readonly bool Spectrum = spectrum;
    }

    private sealed class RadioView
    {
        public long Hz, SinceMs, AboveMs;
        public bool Held, Online, Transmitting, SoundOff, Open, Heard;
        public int Dbm, Memory, Bandwidth;
        public double Volume;
        public double? Squelch;
        public string Mode = "";
    }

    private static readonly List<Viewer> Viewers = [];
    // Those listening over the network; guarded by the same lock.
    private static readonly List<TcpClient> Listeners = [];
    private static readonly AudioRing[] Audio = [new(), new()];
    private static bool _polling;
    private static readonly RadioView[] Radios = [new(), new()];
    private static readonly ScannerBridge.RigLink[] Rigs =
        ScannerBridge.RigPorts.Select(p => new ScannerBridge.RigLink(p)).ToArray();
    private static readonly ScannerBridge.OxideConfig Config = new();
    private static readonly SoundCard Card = new();
    private static readonly Channel<Action> UiWork = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropWrite });
    private static readonly HttpClient PanelHttp = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly string OxideConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sdroxide", "sdroxide", "config");

    private static readonly object PanelLock = new();
    private static object? _panelOwner;
    private static IPAddress? _panelIp;
    private static volatile bool _panelScanner;
    private static volatile bool _cardMuted, _oxideMuted;
    private const string OxideProcess = "sdroxide";

    /// <summary>Starts listening; this lives as long as the server.</summary>
    public static void Start()
    {
        new Thread(AcceptLoop) { IsBackground = true, Name = "www-listen" }.Start();
        _ = Task.Run(UiLoop);
    }

    /// <summary>A panel session began; <paramref name="owner"/> is what may end it, so a late goodbye from an old session changes nothing.</summary>
    public static void PanelUp(object owner, IPAddress ip)
    {
        lock (PanelLock) { _panelOwner = owner; _panelIp = ip; }
        _panelScanner = false;
    }

    public static void PanelDown(object owner)
    {
        lock (PanelLock)
            if (ReferenceEquals(_panelOwner, owner)) { _panelOwner = null; _panelIp = null; }
    }

    public static void PanelPage(bool scanner) => _panelScanner = scanner;

    private static IPAddress? PanelIp() { lock (PanelLock) return _panelIp; }

    // ---------------------------------------------------------------- HTTP

    /// <summary>As with the dial: the server being replaced can hold the port for a moment.</summary>
    private static TcpListener Listen()
    {
        for (bool told = false; ; Thread.Sleep(2000))
        {
            var listener = new TcpListener(IPAddress.Any, Port);
            try
            {
                listener.Start();
                if (told) Console.WriteLine($"[www] port {Port} odzyskany");
                return listener;
            }
            catch (SocketException e)
            {
                listener.Stop();
                if (!told) Console.WriteLine($"[www] port {Port} niedostepny, ponawiam: {e.Message}");
                told = true;
            }
        }
    }

    private static void AcceptLoop()
    {
        var listener = Listen();
        while (true)
        {
            try
            {
                var client = listener.AcceptTcpClient();
                // On its own: a browser may well connect first and speak later.
                _ = Task.Run(() => Serve(client));
            }
            catch (SocketException e) { Console.WriteLine($"[www] accept: {e.Message}"); }
        }
    }

    private static void Serve(TcpClient client)
    {
        bool keep = false;
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 2000;
            client.SendTimeout = WriteTimeoutMs;
            var stream = client.GetStream();
            var input = new StreamReader(stream, Encoding.ASCII);
            // "GET /api/kanaly HTTP/1.1", then the headers.
            string? request = input.ReadLine();
            string origin = "", host = "";
            for (string? line = input.ReadLine(); !string.IsNullOrEmpty(line); line = input.ReadLine())
            {
                if (line.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) origin = line[7..].Trim();
                else if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = line[5..].Trim();
            }
            if (request?.Split(' ') is not [var method, var target, ..]) return;
            string path = target.Split('?')[0];

            if (method == "GET" && path == "/")
            {
                Reply(stream, "200 OK", "text/html; charset=utf-8", Page.Value);
            }
            else if (method == "GET" && path == "/api/kanaly")
            {
                try { Reply(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(ChannelsJson())); }
                // Oxide may be saving right now; the page asks again.
                catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
                {
                    Reply(stream, "503 Service Unavailable", "text/plain", []);
                }
            }
            else if (method == "GET" && path == "/api/zdarzenia")
            {
                stream.Write("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: keep-alive\r\n\r\nretry: 2000\n\n"u8);
                AddViewer(new Viewer(client, target.Contains("widmo=1")));
                keep = true;
            }
            else if (method == "GET" && path == "/api/dzwiek.wav")
            {
                // The same stream behind a WAV header with no end, for the page's <audio>: a
                // phone keeps that playing in the background, where it puts Web Audio to sleep.
                stream.Write("HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"u8);
                stream.Write(WavHeader());
                AddListener(client);
                keep = true;
            }
            else if (method == "GET" && path == "/api/dzwiek")
            {
                stream.Write("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"u8);
                AddListener(client);
                keep = true;
            }
            // The I/Q buffer tab: where its playback is, and its sound (see BufferWeb).
            else if (method == "GET" && path == "/api/bufor/os" && BufferWeb.Buffer(Query(target, "b")) is int osB)
            {
                Reply(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(BufferWeb.TimelineJson(osB)));
            }
            else if (method == "GET" && path == "/api/bufor/stan" && BufferWeb.Buffer(Query(target, "b")) is int stanB)
            {
                Reply(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(BufferWeb.StatusJson(stanB)));
            }
            else if (method == "GET" && path == "/api/bufor/dzwiek" && BufferWeb.Buffer(Query(target, "b")) is int dzwiekB)
            {
                stream.Write("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"u8);
                BufferWeb.AddListener(dzwiekB, client);
                keep = true;
            }
            else if (method == "POST" && path.StartsWith("/api/"))
            {
                // No browser sends a page's clicks from another site, so no other site may press these.
                if (origin.Length > 0 && Uri.TryCreate(origin, UriKind.Absolute, out var from) && from.Authority != host)
                {
                    Reply(stream, "403 Forbidden", "text/plain", []);
                    return;
                }
                Reply(stream, Handle(path) ? "204 No Content" : "404 Not Found", "text/plain", []);
            }
            else
            {
                Reply(stream, "404 Not Found", "text/plain", []);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
        finally
        {
            if (!keep) client.Dispose();
        }
    }

    private static void Reply(Stream stream, string status, string type, byte[] body)
    {
        stream.Write(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"));
        stream.Write(body);
    }

    private static readonly Lazy<byte[]> Page = new(() =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("skaner.html")!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    });

    /// <summary>Quick ones are done here; whatever needs Oxide's window waits its turn, a press can take a while.</summary>
    /// <summary>One parameter of a request's query string, or null.</summary>
    private static string? Query(string target, string name)
    {
        int q = target.IndexOf('?');
        if (q < 0) return null;
        foreach (var pair in target[(q + 1)..].Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv[0] == name) return kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return null;
    }

    private static bool Handle(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (parts)
        {
            case ["api", "bufor", var bb, var bwhat, ..] when BufferWeb.Buffer(bb) is int buf:
                return BufferWeb.Command(buf, bwhat, parts.Length > 4 ? parts[4] : null);
            case ["api", "skan", var r, var what] when Radio(r) is int radio:
                byte? cmd = what switch { "start" => Proto.ScanCmdStart, "stop" => Proto.ScanCmdStop, "next" => Proto.ScanCmdNext, _ => null };
                if (cmd is not byte c) return false;
                UiWork.Writer.TryWrite(() => PressScan(radio, c));
                return true;

            case ["api", "glos", var r, var v] when Radio(r) is int radio &&
                    double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double volume):
                Rigs[radio].SetVolume(Math.Clamp(volume, 0, 1));
                return true;

            case ["api", "czest", var r, var v] when Radio(r) is int radio && long.TryParse(v, out long hz) && hz is >= MinHz and <= MaxHz:
                Rigs[radio].SetFrequency(hz);
                return true;

            case ["api", "krok", var r, var n, var s] when Radio(r) is int radio && int.TryParse(n, out int ticks) &&
                    long.TryParse(s, out long step) && step is >= 100 and <= 10_000_000:
                Rigs[radio].Tune(Math.Clamp(ticks, -100, 100), step);
                return true;

            case ["api", "sql", var r, var v] when Radio(r) is int radio &&
                    double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double squelch):
                Rigs[radio].SetSquelch(Math.Clamp(squelch, -150, 0));
                return true;

            case ["api", "bw", var r, var v] when Radio(r) is int radio && int.TryParse(v, out int width) && width > 0 && Radios[radio].Mode.Length > 0:
                Rigs[radio].SetBandwidth(Radios[radio].Mode, width);
                return true;

            case ["api", "wycisz", var r] when Radio(r) is int radio:
                Rigs[radio].SetMute(!Radios[radio].SoundOff);
                return true;

            case ["api", "karta"]:
                string card = Config.AudioOutput();
                if (Card.Toggle(card)) _cardMuted = Card.IsMuted(card) == true;
                else Console.WriteLine($"[www] nie ma karty dzwiekowej '{card}'");
                return true;

            case ["api", "karta", var on] when on is "0" or "1":
                string named = Config.AudioOutput();
                if (Card.IsMuted(named) != (on == "1") && Card.Toggle(named)) _cardMuted = on == "1";
                return true;

            // Cichy dom: only Oxide quiet in the house, by its own slider in the volume mixer.
            // The card, the PC's other sound and the page's own sound stay as they are.
            case ["api", "cichy"]:
                string output = Config.AudioOutput();
                bool quiet = Card.IsAppMuted(output, OxideProcess) != true;
                if (Card.SetAppMute(output, OxideProcess, quiet)) _oxideMuted = quiet;
                else Console.WriteLine($"[www] Oxide nie gra na '{output}', nie ma czego wyciszyc");
                return true;

            case ["api", "kanal", var r, var i] when Radio(r) is int radio && int.TryParse(i, out int id):
                Recall(radio, id);
                return true;

            // A channel clicked on the page: in or out of the memory scan, and the radio onto it.
            case ["api", "pomin", var r, var i] when Radio(r) is int radio && int.TryParse(i, out int id):
                bool skip = !Skipped(radio, id);
                if (!Rigs[radio].TryScanSkip(id, skip))
                    Console.WriteLine($"[www] radio {radio}: Oxide nie zmienil skanowania kanalu {id} (brak latka_pomin.py?)");
                Recall(radio, id);
                return true;

            case ["api", "ekran", var which] when which is "lustro" or "skaner":
                _ = SetPanelPage(which == "skaner");
                return true;

            case ["api", "oxide", "pokaz", var r] when Radio(r) is int radio:
                UiWork.Writer.TryWrite(() => OxideUi.Summon(Rigs[radio].ReadHz()));
                return true;

            case ["api", "oxide", "schowaj"]:
                UiWork.Writer.TryWrite(OxideUi.Hide);
                return true;

            case ["api", "oxide", "uruchom"]:
                UiWork.Writer.TryWrite(() => { if (!OxideUi.IsRunning()) OxideUi.StartApp(); });
                return true;

            case ["api", "oxide", "zamknij"]:
                UiWork.Writer.TryWrite(OxideUi.CloseApp);
                return true;

            default:
                return false;
        }
    }

    private static int? Radio(string s) => int.TryParse(s, out int r) && r >= 0 && r < Radios.Length ? r : null;

    private static async Task UiLoop()
    {
        await foreach (var work in UiWork.Reader.ReadAllAsync())
        {
            // The mirror must outlive anything that goes wrong here, and the next press may work.
            try { work(); }
            catch (Exception e) { Console.WriteLine($"[www] {e.GetType().Name}: {e.Message}"); }
        }
    }

    private static void PressScan(int radio, byte cmd)
    {
        long hz = Rigs[radio].ReadHz();
        if (hz == 0) return;
        bool pressed = false;
        var left = ScannerBridge.PressByRig(cmd, [(radio, hz)], ref pressed);
        if (left.Count == 0) return;
        foreach (var (r, problem) in OxideUi.Press(cmd, left, out _))
            Console.WriteLine($"[www] radio {r}, komenda {cmd}: {problem}");
    }

    /// <summary>
    /// With all a channel stores where Oxide can, and otherwise its frequency and mode: only
    /// the patched Oxide knows how to recall. Either way it ends a scan on this radio.
    /// </summary>
    private static void Recall(int radio, int id)
    {
        if (Rigs[radio].TryRecall(id)) return;
        foreach (var c in Json(Path.Combine(OxideConfigDir, "memories.json")).AsArray())
            if (c!["id"]!.GetValue<int>() == id)
            {
                string mode = c["mode"]?.GetValue<string>() switch
                {
                    "Am" => "AM", "Sam" => "AMS", "Wfm" => "WFM", "Usb" => "USB", "Lsb" => "LSB", "Cw" => "CW", _ => "FM",
                };
                Rigs[radio].TuneTo((long)c["freq_hz"]!.GetValue<double>(), mode);
                return;
            }
    }

    /// <summary>The panel's own page does this already, and only the panel can draw its screen.</summary>
    private static async Task SetPanelPage(bool scanner)
    {
        if (PanelIp() is not { } ip) return;
        try
        {
            using var form = new FormUrlEncodedContent([new("key", "scan"), new("value", scanner ? "1" : "0")]);
            using var answer = await PanelHttp.PostAsync($"http://{ip}/api/set", form);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"[www] panel nie odpowiada: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- channels

    private static JsonNode Json(string path) =>
        JsonNode.Parse(File.ReadAllBytes(path)) ?? throw new JsonException(path);

    /// <summary>Oxide's own lists, as they stand: the page does the rest.</summary>
    private static string ChannelsJson()
    {
        var radios = new JsonArray();
        foreach (var r in Json(Path.Combine(OxideConfigDir, "radios.json"))["radios"]!.AsArray())
        {
            int id = r!["id"]!.GetValue<int>();
            // A radio switched off in Oxide (one receiver for both bands) has no card on the page.
            if (id >= Radios.Length || r["enabled"]?.GetValue<bool>() == false) continue;
            var scanner = Json(Path.Combine(id == 0 ? OxideConfigDir : Path.Combine(OxideConfigDir, $"radio-{id}"), "scanner.json"));
            radios.Add(new JsonObject
            {
                ["id"] = id,
                ["nazwa"] = r["name"]!.GetValue<string>(),
                ["foldery"] = scanner["folders"]?.DeepClone() ?? new JsonArray(),
                ["pomijane"] = scanner["skip"]?.DeepClone() ?? new JsonArray(),
            });
        }
        var channels = new JsonArray();
        foreach (var c in Json(Path.Combine(OxideConfigDir, "memories.json")).AsArray())
            channels.Add(new JsonObject
            {
                ["id"] = c!["id"]!.GetValue<int>(),
                ["nazwa"] = c["name"]!.GetValue<string>(),
                ["hz"] = (long)Math.Round(c["freq_hz"]!.GetValue<double>()),
                ["tryb"] = c["mode"]?.GetValue<string>() ?? "",
                ["folder"] = c["folder"]?.DeepClone(),
            });
        return new JsonObject
        {
            ["radia"] = radios,
            ["foldery"] = Json(Path.Combine(OxideConfigDir, "memory_folders.json")),
            ["kanaly"] = channels,
        }.ToJsonString();
    }

    /// <summary>16-bit mono PCM at <see cref="AudioRate"/>, its lengths at the maximum: a stream with no end.</summary>
    private static byte[] WavHeader()
    {
        var h = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
        BitConverter.GetBytes(-1).CopyTo(h, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(h, 8);
        BitConverter.GetBytes(16).CopyTo(h, 16);
        BitConverter.GetBytes((short)1).CopyTo(h, 20);
        BitConverter.GetBytes((short)1).CopyTo(h, 22);
        BitConverter.GetBytes(AudioRate).CopyTo(h, 24);
        BitConverter.GetBytes(AudioRate * 2).CopyTo(h, 28);
        BitConverter.GetBytes((short)2).CopyTo(h, 32);
        BitConverter.GetBytes((short)16).CopyTo(h, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
        BitConverter.GetBytes(-1).CopyTo(h, 40);
        return h;
    }

    /// <summary>Whether memory <paramref name="id"/> is on this radio's skip list, as Oxide last saved it.</summary>
    private static bool Skipped(int radio, int id)
    {
        var scanner = Json(Path.Combine(radio == 0 ? OxideConfigDir : Path.Combine(OxideConfigDir, $"radio-{radio}"), "scanner.json"));
        return scanner["skip"] is JsonArray skip && skip.Any(n => n?.GetValue<int>() == id);
    }

    private static long ChannelsStamp()
    {
        try
        {
            string[] files =
            [
                Path.Combine(OxideConfigDir, "memories.json"), Path.Combine(OxideConfigDir, "memory_folders.json"),
                Path.Combine(OxideConfigDir, "scanner.json"), Path.Combine(OxideConfigDir, "radio-1", "scanner.json"),
                Path.Combine(OxideConfigDir, "radios.json"),
            ];
            return files.Max(f => File.GetLastWriteTimeUtc(f).Ticks);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    // ---------------------------------------------------------------- viewers

    private static void AddViewer(Viewer viewer)
    {
        bool start;
        lock (Viewers)
        {
            Viewers.Add(viewer);
            start = !_polling;
            _polling = true;
        }
        if (start) new Thread(PollLoop) { IsBackground = true, Name = "www-poll" }.Start();
    }

    private static void AddListener(TcpClient client)
    {
        bool start;
        lock (Viewers)
        {
            Listeners.Add(client);
            start = !_polling;
            _polling = true;
        }
        if (start) new Thread(PollLoop) { IsBackground = true, Name = "www-poll" }.Start();
    }

    private static void Broadcast(string kind, string json, bool spectrum)
    {
        byte[] message = Encoding.UTF8.GetBytes($"event: {kind}\ndata: {json}\n\n");
        Viewer[] now;
        lock (Viewers) now = Viewers.ToArray();
        foreach (var v in now)
        {
            if (spectrum && !v.Spectrum) continue;
            try
            {
                // Both the poll and the IQ threads end up here.
                lock (v) v.Client.GetStream().Write(message);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or SocketException)
            {
                lock (Viewers) Viewers.Remove(v);
                v.Client.Dispose();
            }
        }
    }

    private static void PollLoop()
    {
        CancellationTokenSource? feeds = null, sound = null;
        long lastViewer = Environment.TickCount64, lastMute = 0, lastStamp = 0, stamp = 0;
        try
        {
            while (true)
            {
                long now = Environment.TickCount64;
                bool any, spectrum, listening;
                lock (Viewers)
                {
                    listening = Listeners.Count > 0;
                    any = Viewers.Count > 0;
                    spectrum = Viewers.Any(v => v.Spectrum);
                    if (any || listening) lastViewer = now;
                    else if (now - lastViewer >= ViewerGraceMs)
                    {
                        _polling = false;
                        return;
                    }
                }

                if (spectrum && feeds is null)
                {
                    feeds = new CancellationTokenSource();
                    for (int i = 0; i < TciPorts.Length; i++)
                    {
                        int radio = i;
                        var token = feeds.Token;
                        new Thread(() => IqFeed(radio, token)) { IsBackground = true, Name = $"www-iq{radio}" }.Start();
                    }
                }
                else if (!spectrum && feeds is not null)
                {
                    feeds.Cancel();
                    feeds = null;
                }

                if (listening && sound is null)
                {
                    sound = new CancellationTokenSource();
                    var token = sound.Token;
                    for (int i = 0; i < TciPorts.Length; i++)
                    {
                        int radio = i;
                        new Thread(() => AudioFeed(radio, token)) { IsBackground = true, Name = $"www-audio{radio}" }.Start();
                    }
                    new Thread(() => AudioMix(token)) { IsBackground = true, Name = "www-mix", Priority = ThreadPriority.AboveNormal }.Start();
                }
                else if (!listening && sound is not null)
                {
                    sound.Cancel();
                    sound = null;
                }

                // Both radios at once: each has its own rigctld and its own state.
                Parallel.For(0, Radios.Length, i => Update(i, now));
                if (now - lastMute >= MuteEveryMs)
                {
                    lastMute = now;
                    _cardMuted = Card.IsMuted(Config.AudioOutput()) == true;
                    _oxideMuted = Card.IsAppMuted(Config.AudioOutput(), OxideProcess) == true;
                }
                if (now - lastStamp >= StampEveryMs)
                {
                    lastStamp = now;
                    stamp = ChannelsStamp();
                }
                if (any) Broadcast("stan", StateJson(stamp), false);
                Thread.Sleep((int)Math.Max(5, PollMs - (Environment.TickCount64 - now)));
            }
        }
        catch (Exception e)
        {
            // The mirror must outlive anything that goes wrong here; the next viewer starts it again.
            Console.WriteLine($"[www] petla odczytu zakonczona: {e.GetType().Name}: {e.Message}");
            lock (Viewers) _polling = false;
        }
        finally
        {
            feeds?.Cancel();
            sound?.Cancel();
        }
    }

    private static void Update(int i, long now)
    {
        var r = Radios[i];
        if (!Rigs[i].TryReadAll(out long hz, out string mode, out int strength, out bool soundOff, out double volume, out int memory, out int bandwidth, out double? squelch))
        {
            r.Online = r.Held = r.Transmitting = r.Open = false;
            r.Hz = 0;
            return;
        }
        r.Online = true;
        r.Dbm = S9Dbm + strength;
        r.SoundOff = soundOff;
        r.Volume = volume;
        r.Memory = memory;
        r.Mode = mode;
        r.Bandwidth = bandwidth;
        r.Squelch = squelch;
        if (hz != r.Hz)
        {
            r.Hz = hz;
            r.SinceMs = now;
            r.Held = r.Transmitting = false;
        }
        if (!r.Held && now - r.SinceMs >= HoldMs) r.Held = true;
        r.Open = r.Held && r.Dbm >= Config.SquelchDbm(i, mode);
        if (!r.Held) return;
        if (r.Open)
        {
            r.AboveMs = now;
            r.Transmitting = true;
        }
        else if (now - r.AboveMs >= TxDropMs)
        {
            r.Transmitting = false;
        }
    }

    private static string StateJson(long stamp)
    {
        var radios = new JsonArray();
        foreach (var r in Radios)
            radios.Add(new JsonObject
            {
                ["stan"] = !r.Online ? "off" : r.Transmitting ? "tx" : r.Held ? "stop" : "skan",
                ["hz"] = r.Hz,
                ["tryb"] = r.Mode,
                ["dbm"] = r.Dbm,
                ["mem"] = r.Memory,
                ["glos"] = r.Volume,
                ["cisza"] = r.SoundOff,
                ["bw"] = r.Bandwidth,
                ["sql"] = r.Squelch,
            });
        return new JsonObject
        {
            ["oxide"] = OxideUi.IsRunning(),
            ["karta"] = _cardMuted,
            ["cichy"] = _oxideMuted,
            ["ekran"] = PanelIp() is null ? null : _panelScanner ? "skaner" : "lustro",
            ["kanaly"] = stamp,
            ["radia"] = radios,
        }.ToJsonString();
    }

    // ---------------------------------------------------------------- sound

    /// <summary>
    /// One radio's demodulated audio, which never reaches the sound card: that is what lets the
    /// house stay quiet. Plain TCI hands over the decoders' tap, from before the squelch, so the
    /// Oxide built here is asked for what the squelch let through instead, and its echo says so.
    /// Either way it comes before the volume and the mute, and <see cref="AudioMix"/> applies those.
    /// </summary>
    private static void AudioFeed(int radio, CancellationToken stop)
    {
        var buffer = new byte[64 * 1024];
        float sum = 0;
        int summed = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.ConnectAsync(new Uri($"ws://127.0.0.1:{TciPorts[radio]}"), stop).GetAwaiter().GetResult();
                bool streaming = false;
                while (!stop.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    int length = 0;
                    WebSocketReceiveResult got;
                    do
                    {
                        got = ws.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), stop).GetAwaiter().GetResult();
                        length += got.Count;
                    }
                    while (!got.EndOfMessage && length < buffer.Length);

                    if (got.MessageType == WebSocketMessageType.Close) break;
                    if (got.MessageType == WebSocketMessageType.Text)
                    {
                        if (!streaming && Encoding.ASCII.GetString(buffer, 0, length).Contains("ready;"))
                        {
                            streaming = true;
                            Say(ws, "audio_samplerate:48000;audio_heard:true;audio_start:0;", stop);
                        }
                        if (Encoding.ASCII.GetString(buffer, 0, length).Contains("audio_heard:true")) Radios[radio].Heard = true;
                        continue;
                    }
                    if (length < SpectrumFrame.HeaderBytes) continue;
                    var words = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, SpectrumFrame.HeaderBytes));
                    if (words[6] != 1) continue;
                    var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(SpectrumFrame.HeaderBytes, length - SpectrumFrame.HeaderBytes));
                    int count = Math.Min((int)words[5], samples.Length);
                    // Stereo with both sides alike; averaging three takes 48 kHz down to what speech needs.
                    for (int i = 0; i + 1 < count; i += 2)
                    {
                        sum += samples[i];
                        if (++summed < AudioDecimate) continue;
                        Audio[radio].Write(sum / AudioDecimate);
                        sum = 0;
                        summed = 0;
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or HttpRequestException or ObjectDisposedException) { }
            Radios[radio].Heard = false;
            stop.WaitHandle.WaitOne(IqRetryMs);
        }
        Audio[radio].Clear();
    }

    /// <summary>Both radios into one stream of 16-bit little-endian samples at <see cref="AudioRate"/>, silence included, so a player's clock never runs dry.</summary>
    private static void AudioMix(CancellationToken stop)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long sent = 0;
        var mix = new float[AudioRate];
        while (!stop.WaitHandle.WaitOne(AudioTickMs))
        {
            int need = (int)Math.Min(clock.ElapsedMilliseconds * AudioRate / 1000 - sent, mix.Length);
            if (need <= 0) continue;
            sent += need;
            Array.Clear(mix, 0, need);
            for (int r = 0; r < Audio.Length; r++)
            {
                var radio = Radios[r];
                // Without Oxide's own squelch in the audio, the level stands in for it, a poll late.
                Audio[r].MixInto(mix, need, radio.SoundOff || !(radio.Heard || radio.Open) ? 0 : (float)radio.Volume);
            }

            var bytes = new byte[need * 2];
            for (int i = 0; i < need; i++)
            {
                short s = (short)(Math.Clamp(mix[i], -1f, 1f) * short.MaxValue);
                bytes[i * 2] = (byte)s;
                bytes[i * 2 + 1] = (byte)(s >> 8);
            }
            TcpClient[] now;
            lock (Viewers) now = Listeners.ToArray();
            foreach (var c in now)
            {
                try { c.GetStream().Write(bytes); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or SocketException)
                {
                    lock (Viewers) Listeners.Remove(c);
                    c.Dispose();
                }
            }
        }
    }

    /// <summary>What has arrived and not been played. Oxide delivers in bursts, so a little is held back first.</summary>
    private sealed class AudioRing
    {
        private readonly float[] _ring = new float[AudioRate * 2];
        private int _read, _count;
        private bool _playing;

        public void Write(float sample)
        {
            lock (_ring)
            {
                _ring[(_read + _count) % _ring.Length] = sample;
                if (_count < _ring.Length) _count++;
                else _read = (_read + 1) % _ring.Length;
            }
        }

        public void Clear()
        {
            lock (_ring) { _read = _count = 0; _playing = false; }
        }

        public void MixInto(float[] mix, int need, float gain)
        {
            lock (_ring)
            {
                // Two clocks drift apart: past this much the delay is dropped rather than kept.
                if (_count > AudioMaxHeld)
                {
                    _read = (_read + _count - AudioHeld) % _ring.Length;
                    _count = AudioHeld;
                }
                if (!_playing && _count < AudioHeld) return;
                _playing = true;
                int take = Math.Min(need, _count);
                for (int i = 0; i < take; i++) mix[i] += _ring[(_read + i) % _ring.Length] * gain;
                _read = (_read + take) % _ring.Length;
                _count -= take;
                if (_count == 0) _playing = false;
            }
        }
    }

    // ---------------------------------------------------------------- spectrum

    /// <summary>
    /// The spectrum of an I/Q buffer's playback (see <see cref="BufferWeb"/>), from its own TCI,
    /// sent as radio 2 + <paramref name="b"/>: the page draws it in that receiver's card while
    /// the card's buffer strip is open, the way Oxide's playback tab shows the buffer's waterfall.
    /// </summary>
    internal static void BufferSpectrum(int b, CancellationToken stop) => IqFeed(2 + b, stop, 50003 + b);

    /// <summary>One radio's IQ stream for as long as anyone wants a spectrum. Oxide can be closed and reopened at will.</summary>
    private static void IqFeed(int radio, CancellationToken stop, int port = 0)
    {
        if (port == 0) port = TciPorts[radio];
        var buffer = new byte[256 * 1024];
        var spectrum = new SpectrumFrame();
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), stop).GetAwaiter().GetResult();
                long center = 0, vfo = 0;
                bool streaming = false;
                while (!stop.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    int length = 0;
                    WebSocketReceiveResult got;
                    do
                    {
                        got = ws.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), stop).GetAwaiter().GetResult();
                        length += got.Count;
                    }
                    while (!got.EndOfMessage && length < buffer.Length);

                    if (got.MessageType == WebSocketMessageType.Close) break;
                    if (got.MessageType == WebSocketMessageType.Text)
                    {
                        foreach (var line in Encoding.ASCII.GetString(buffer, 0, length).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (line == "ready" && !streaming)
                            {
                                streaming = true;
                                Say(ws, $"iq_samplerate:{IqRate};iq_start:0;", stop);
                            }
                            else if (line.StartsWith("dds:0,") && long.TryParse(line[6..], out long d))
                            {
                                if (d != center) spectrum.Reset();
                                center = d;
                            }
                            else if (line.StartsWith("vfo:0,0,") && long.TryParse(line[8..], out long f))
                            {
                                vfo = f;
                            }
                        }
                        continue;
                    }

                    // 16 little-endian u32 and then the samples: rate in [1], float count in [5], type in [6], 0 for IQ.
                    if (length < SpectrumFrame.HeaderBytes) continue;
                    var words = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, SpectrumFrame.HeaderBytes));
                    if (words[6] != 0) continue;
                    var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(SpectrumFrame.HeaderBytes, length - SpectrumFrame.HeaderBytes));
                    int count = Math.Min((int)words[5], samples.Length);
                    if (spectrum.Add(samples[..count]) is { } frame)
                        Broadcast("widmo", $"{{\"r\":{radio},\"c\":{center},\"v\":{vfo},\"s\":{words[1]},\"d\":\"{Convert.ToBase64String(frame)}\"}}", true);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or HttpRequestException or ObjectDisposedException) { }
            stop.WaitHandle.WaitOne(IqRetryMs);
        }
    }

    private static void Say(ClientWebSocket ws, string line, CancellationToken stop)
    {
        foreach (var command in line.Split(';', StringSplitOptions.RemoveEmptyEntries))
            ws.SendAsync(Encoding.ASCII.GetBytes(command + ";"), WebSocketMessageType.Text, true, stop).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Averages a few transforms into one row for the page. The rest of each frame's samples are
    /// passed over, so the work stays the same at any sample rate. One byte a bin, 0 to 255
    /// for <see cref="FloorDb"/> to 0 dB of full scale.
    /// </summary>
    private sealed class SpectrumFrame
    {
        public const int HeaderBytes = 64;
        private const int Size = 2048, Bins = 512, MaxPerFrame = 4, FrameMs = 100;
        private const double FloorDb = -150;

        private readonly Complex[] _in = new Complex[Size];
        private readonly Complex[] _twiddle = new Complex[Size / 2];
        private readonly double[] _window = new double[Size];
        private readonly double[] _power = new double[Size];
        private readonly double _scale;
        private int _fill, _done;
        private long _frameAt = Environment.TickCount64;

        public SpectrumFrame()
        {
            double sum = 0;
            for (int i = 0; i < Size; i++)
            {
                _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Size);
                sum += _window[i];
            }
            for (int i = 0; i < Size / 2; i++) _twiddle[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / Size);
            _scale = 1 / (sum * sum);
        }

        /// <summary>The old window's samples say nothing about the new one.</summary>
        public void Reset()
        {
            _fill = _done = 0;
            Array.Clear(_power);
        }

        /// <summary>Interleaved I and Q. Returns a finished row when one is due.</summary>
        public byte[]? Add(ReadOnlySpan<float> iq)
        {
            for (int i = 0; i + 1 < iq.Length && _done < MaxPerFrame; i += 2)
            {
                _in[_fill] = new Complex(iq[i] * _window[_fill], iq[i + 1] * _window[_fill]);
                if (++_fill < Size) continue;
                _fill = 0;
                Transform();
                for (int k = 0; k < Size; k++) _power[k] += _in[k].Real * _in[k].Real + _in[k].Imaginary * _in[k].Imaginary;
                _done++;
            }
            if (_done == 0 || Environment.TickCount64 - _frameAt < FrameMs) return null;

            var row = new byte[Bins];
            int per = Size / Bins;
            for (int b = 0; b < Bins; b++)
            {
                double peak = 0;
                // Negative frequencies first, so the centre of the window is the middle of the row.
                for (int k = 0; k < per; k++) peak = Math.Max(peak, _power[(b * per + k + Size / 2) % Size]);
                double db = 10 * Math.Log10(peak * _scale / _done + 1e-20);
                row[b] = (byte)Math.Clamp((db - FloorDb) * 255 / -FloorDb, 0, 255);
            }
            Array.Clear(_power);
            _done = 0;
            _frameAt = Environment.TickCount64;
            return row;
        }

        private void Transform()
        {
            for (int i = 1, j = 0; i < Size; i++)
            {
                int bit = Size >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (_in[i], _in[j]) = (_in[j], _in[i]);
            }
            for (int len = 2; len <= Size; len <<= 1)
            {
                int half = len / 2, step = Size / len;
                for (int start = 0; start < Size; start += len)
                    for (int k = 0; k < half; k++)
                    {
                        var odd = _in[start + k + half] * _twiddle[k * step];
                        _in[start + k + half] = _in[start + k] - odd;
                        _in[start + k] += odd;
                    }
            }
        }
    }
}
