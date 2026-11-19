using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CydMonitor;

/// <summary>
/// The I/Q buffer on the scanner's web page. Oxide keeps every receiver's signal on disk and plays
/// it back on two radios of their own: the third plays the 2 m receiver's buffer, the fourth the
/// 70 cm one's. Those two are steered here over their own rigctld with the `g TS` commands of our
/// Oxide build (where the playback is, seek, pause, keep going) and heard through their own TCI.
///
/// A playback runs only while the page listens: while a page holds its sound open, Oxide is told
/// every 5 s to keep going for 15 s, and "stop" the moment the last listener goes, whether the tab
/// was closed or the connection simply dropped. Should this server die meanwhile, the 15 s run out
/// in Oxide by themselves.
/// </summary>
internal static class BufferWeb
{
    private static readonly int[] RigPorts = [4534, 4535];
    private static readonly int[] TciPorts = [50003, 50004];
    private const int ListenSecs = 15, RenewMs = 5000, Decimate = 3, MarksShown = 80;
    // A TCI binary frame starts with 16 words of header; words[5] is the count, words[6] the type (1 = audio).
    private const int TciHeaderBytes = 64;
    private static readonly List<TcpClient>[] Listeners = [[], []];
    private static readonly bool[] Running = new bool[2];
    private static readonly object Gate = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"0" is the 2 m receiver's buffer, "1" the 70 cm one's.</summary>
    public static int? Buffer(string? s) => s switch { "0" => 0, "1" => 1, _ => null };

    // ---- Oxide's rigctld on the playback radio, one connection a question -------------------

    private static string? Rig(int b, string command)
    {
        try
        {
            using var c = new TcpClient();
            if (!c.ConnectAsync("127.0.0.1", RigPorts[b]).Wait(1000)) return null;
            c.ReceiveTimeout = 2000;
            c.SendTimeout = 2000;
            var s = c.GetStream();
            s.Write(Encoding.ASCII.GetBytes(command + "\n"));
            using var input = new StreamReader(s, Encoding.ASCII);
            return input.ReadLine();
        }
        catch (Exception e) when (e is IOException or SocketException or AggregateException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    private readonly record struct Play(long Oldest, double Pos, double Total, bool Paused, double Hz, bool Listening)
    {
        public double At => Oldest + Pos;
    }

    private static Play? Status(int b)
    {
        if (Rig(b, "g TS")?.Split(' ') is not [var o, var p, var t, var pa, var hz, var l]) return null;
        if (!long.TryParse(o, NumberStyles.Integer, Inv, out long oldest)
            || !double.TryParse(p, NumberStyles.Float, Inv, out double pos)
            || !double.TryParse(t, NumberStyles.Float, Inv, out double total)
            || !double.TryParse(hz, NumberStyles.Float, Inv, out double dial)) return null;
        return new Play(oldest, pos, total, pa == "1", dial, l == "1");
    }

    // ---- the buffer's list of transmissions (transmissions.tsv, written by Oxide) ------------

    private sealed record Mark(double Start, double End, double Hz, string Name);
    private static readonly (long At, List<Mark> Marks)[] MarksRead = [(0, []), (0, [])];

    /// <summary>
    /// The receiver's buffer folder (its timeshift.json's folder, then radio0 or radio1), and whether
    /// its settings leave the digital transmissions with no speech ("hide_voiceless") and the analog ones
    /// with no voice ("hide_quiet") off the list.
    /// </summary>
    private static (string Dir, bool HideVoiceless, bool HideQuiet)? BufferDir(int b)
    {
        try
        {
            string cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sdroxide", "sdroxide", "config");
            string file = b == 0 ? Path.Combine(cfg, "timeshift.json") : Path.Combine(cfg, $"radio-{b}", "timeshift.json");
            var j = JsonNode.Parse(File.ReadAllText(file));
            var dir = j?["dir"]?.GetValue<string>();
            bool hide = j?["hide_voiceless"]?.GetValue<bool>() ?? false;
            bool quiet = j?["hide_quiet"]?.GetValue<bool>() ?? false;
            return string.IsNullOrEmpty(dir) ? null : (Path.Combine(dir, $"radio{b}"), hide, quiet);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }

    private static List<Mark> Marks(int b)
    {
        lock (MarksRead)
        {
            if (Environment.TickCount64 - MarksRead[b].At < 3000) return MarksRead[b].Marks;
        }
        var marks = new List<Mark>();
        try
        {
            if (BufferDir(b) is { } where)
            {
                using var f = new FileStream(Path.Combine(where.Dir, "transmissions.tsv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(f, Encoding.UTF8);
                for (string? line = r.ReadLine(); line is not null; line = r.ReadLine())
                {
                    // start ms, end ms, dial Hz, name, and what it was: digital "voice" or "data" (no speech
                    // heard), analog "speech" or "quiet" (no voice heard).
                    var p = line.Split('\t', 5);
                    string kind = p.Length > 4 ? p[4].Trim() : "";
                    if ((where.HideVoiceless && kind == "data") || (where.HideQuiet && kind == "quiet")) continue;
                    if (p.Length >= 3 && long.TryParse(p[0], Inv, out long s) && long.TryParse(p[1], Inv, out long e)
                        && double.TryParse(p[2], NumberStyles.Float, Inv, out double hz))
                        marks.Add(new Mark(s / 1000.0, e / 1000.0, hz, p.Length > 3 ? p[3].Trim() : ""));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        lock (MarksRead) MarksRead[b] = (Environment.TickCount64, marks);
        return marks;
    }

    // ---- what the page asks and presses -----------------------------------------------------

    /// <summary>
    /// The whole buffer for the page's timeline: its span and every transmission in it, with the
    /// channel's name and frequency. Names are listed once and referred to by number, since a day
    /// holds thousands of transmissions on a few dozen channels.
    /// </summary>
    public static string TimelineJson(int b)
    {
        var o = new JsonObject { ["b"] = b };
        var st = Status(b);
        o["ok"] = st is not null;
        if (st is not Play p) return o.ToJsonString();
        o["oldest"] = p.Oldest;
        o["total"] = Math.Round(p.Total, 1);
        var names = new List<string>();
        var index = new Dictionary<string, int>();
        var rows = new JsonArray();
        foreach (var m in Marks(b))
        {
            if (m.End < p.Oldest) continue;
            if (!index.TryGetValue(m.Name, out int n))
            {
                n = names.Count;
                names.Add(m.Name);
                index[m.Name] = n;
            }
            rows.Add(new JsonArray(Math.Round(m.Start, 1), Math.Round(m.End, 1), m.Hz, n));
        }
        o["names"] = new JsonArray(names.Select(x => (JsonNode)x).ToArray());
        o["m"] = rows;
        return o.ToJsonString();
    }

    public static string StatusJson(int b)
    {
        var o = new JsonObject { ["b"] = b };
        var st = Status(b);
        o["ok"] = st is not null;
        if (st is Play p)
        {
            o["oldest"] = p.Oldest;
            o["at"] = Math.Round(p.At, 1);
            o["behind"] = Math.Round(Math.Max(0, p.Total - p.Pos), 1);
            o["total"] = Math.Round(p.Total, 1);
            o["paused"] = p.Paused;
            o["hz"] = p.Hz;
            o["listening"] = p.Listening;
            var marks = Marks(b).Where(m => m.Start >= p.Oldest).ToList();
            o["marks"] = new JsonArray(marks.Skip(Math.Max(0, marks.Count - MarksShown))
                .Select(m => (JsonNode)new JsonObject { ["s"] = m.Start, ["e"] = m.End, ["hz"] = m.Hz, ["n"] = m.Name }).ToArray());
        }
        return o.ToJsonString();
    }

    /// <summary>
    /// A button of the buffer tab: prev / next transmission (the way Oxide's own strip does it:
    /// back goes to the start of the one playing once it is more than 1.5 s in), back / fwd 10 s,
    /// pause / play, live, or a transmission's start (Unix s) picked from the list.
    /// </summary>
    public static bool Command(int b, string what, string? arg)
    {
        if (Status(b) is not Play p) return false;
        double? seek = null;
        bool? pause = null;
        switch (what)
        {
            case "pause": pause = true; break;
            case "play": pause = false; break;
            case "back": seek = p.Pos - 10; break;
            case "fwd": seek = p.Pos + 10; break;
            case "live": seek = p.Total; pause = false; break;
            case "prev" or "next":
                var marks = Marks(b);
                var m = what == "next"
                    ? marks.FirstOrDefault(x => x.Start > p.At + 0.5)
                    : marks.LastOrDefault(x => x.Start < p.At - 1.5);
                if (m is null) return false;
                seek = m.Start - p.Oldest - 0.5;
                pause = false;
                break;
            case "seek" when double.TryParse(arg, NumberStyles.Float, Inv, out double unix):
                seek = unix - p.Oldest - 0.5;
                pause = false;
                break;
            default: return false;
        }
        bool ok = true;
        if (seek is double s) ok &= Rig(b, FormattableString.Invariant($"g TS SEEK {Math.Clamp(s, 0, p.Total):0.00}")) == "RPRT 0";
        if (pause is bool pa && pa != p.Paused) ok &= Rig(b, $"g TS PAUSE {(pa ? 1 : 0)}") == "RPRT 0";
        return ok;
    }

    // ---- the sound -----------------------------------------------------------------------------

    /// <summary>A page that plays this buffer: 16-bit mono PCM at 16 kHz, as long as it listens.</summary>
    public static void AddListener(int b, TcpClient client)
    {
        lock (Gate)
        {
            Listeners[b].Add(client);
            if (Running[b]) return;
            Running[b] = true;
        }
        Console.WriteLine($"[bufor] {(b == 0 ? "2 m" : "70 cm")}: strona slucha, odtwarzanie wlaczone");
        new Thread(() => Run(b)) { IsBackground = true, Name = $"www-bufor{b}" }.Start();
    }

    /// <summary>Where a mute this server put on a playback is noted, so a restart can take it off.</summary>
    private static string MuteNote(int b) => Path.Combine(AppContext.BaseDirectory, $"bufor{b}-wyciszony-przez-www.txt");

    /// <summary>
    /// Oxide plays the buffer on the station's speakers as well, and heard through a remote desktop
    /// the page's sound and Oxide's come one after the other: twice. While the page listens only the
    /// page plays — the page's sound is taken before Oxide's volume and mute, so it is not touched.
    /// A playback the operator muted himself is left as it is.
    /// </summary>
    private static bool MuteSpeakers(int b)
    {
        if (Rig(b, "u MUTE") != "0" || Rig(b, "U MUTE 1") != "RPRT 0") return false;
        try { File.WriteAllText(MuteNote(b), DateTime.Now.ToString("s")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Console.WriteLine($"[bufor] {(b == 0 ? "2 m" : "70 cm")}: glosniki Oxide wyciszone na czas sluchania przez strone");
        return true;
    }

    private static void UnmuteSpeakers(int b)
    {
        if (Rig(b, "U MUTE 0") != "RPRT 0") return;
        try { File.Delete(MuteNote(b)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Run(int b)
    {
        // Switched on here, so switched off here too. A playback the operator had already
        // switched on in Oxide is left on: that is his, not this page's.
        bool ours = false, muted = false;
        while (true)
        {
            if (Status(b) is null && Rig(b, "g TS ON") == "RPRT 0")
            {
                ours = true;
                Console.WriteLine($"[bufor] {(b == 0 ? "2 m" : "70 cm")}: wlaczam odtwarzacz bufora w Oxide");
                // Its buffer opens in a moment.
                for (int i = 0; i < 40 && Status(b) is null; i++) Thread.Sleep(250);
            }
            // A mute left behind by a server that stopped mid-listen comes off first.
            if (!muted && File.Exists(MuteNote(b))) UnmuteSpeakers(b);
            if (!muted) muted = MuteSpeakers(b);
            // Not disposed: the feed thread may still be on its way out when this starts over.
            var stop = new CancellationTokenSource();
            new Thread(() => Feed(b, stop.Token)) { IsBackground = true, Name = $"www-bufor{b}-tci", Priority = ThreadPriority.AboveNormal }.Start();
            // The buffer's own waterfall, for the receiver's card while its buffer strip is open.
            new Thread(() => ScannerWeb.BufferSpectrum(b, stop.Token)) { IsBackground = true, Name = $"www-bufor{b}-widmo" }.Start();
            long renewed = long.MinValue / 2;
            while (true)
            {
                DropGone(b);
                int n;
                lock (Gate) n = Listeners[b].Count;
                if (n == 0) break;
                if (Environment.TickCount64 - renewed >= RenewMs)
                {
                    Rig(b, $"g TS LISTEN {ListenSecs}");
                    renewed = Environment.TickCount64;
                }
                Thread.Sleep(300);
            }
            stop.Cancel();
            Rig(b, "g TS LISTEN 0");
            // The speakers as they were, before the playback may be switched off (its rigctld
            // goes with it).
            lock (Gate)
            {
                if (Listeners[b].Count > 0) continue;
            }
            if (muted)
            {
                UnmuteSpeakers(b);
                muted = false;
            }
            // Switched off before this thread lets go, so a page that comes back right after
            // finds it off and switches it on again, rather than losing it to this switch.
            bool was = ours;
            if (ours)
            {
                lock (Gate)
                {
                    if (Listeners[b].Count > 0) continue;
                }
                Rig(b, "g TS OFF");
                ours = false;
            }
            lock (Gate)
            {
                // Someone came back while the playback was being let go: start over.
                if (Listeners[b].Count > 0) continue;
                Running[b] = false;
            }
            Console.WriteLine($"[bufor] {(b == 0 ? "2 m" : "70 cm")}: nikt nie slucha, odtwarzacz {(was ? "wylaczony" : "wstrzymany (wlaczony w Oxide)")}");
            return;
        }
    }

    private static void Feed(int b, CancellationToken stop)
    {
        var buffer = new byte[64 * 1024];
        float sum = 0;
        int summed = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.ConnectAsync(new Uri($"ws://127.0.0.1:{TciPorts[b]}"), stop).GetAwaiter().GetResult();
                bool streaming = false;
                var outgoing = new List<byte>(8192);
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
                            var start = Encoding.ASCII.GetBytes("audio_samplerate:48000;audio_heard:true;audio_start:0;");
                            ws.SendAsync(start, WebSocketMessageType.Text, true, stop).GetAwaiter().GetResult();
                        }
                        continue;
                    }
                    if (length < TciHeaderBytes) continue;
                    var words = MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, TciHeaderBytes));
                    if (words[6] != 1) continue;
                    var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(TciHeaderBytes, length - TciHeaderBytes));
                    int count = Math.Min((int)words[5], samples.Length);
                    outgoing.Clear();
                    // Stereo, both sides alike; three averaged take 48 kHz down to what speech needs.
                    for (int i = 0; i + 1 < count; i += 2)
                    {
                        sum += samples[i];
                        if (++summed < Decimate) continue;
                        short v = (short)(Math.Clamp(sum / Decimate, -1f, 1f) * short.MaxValue);
                        outgoing.Add((byte)v);
                        outgoing.Add((byte)(v >> 8));
                        sum = 0;
                        summed = 0;
                    }
                    if (outgoing.Count == 0) continue;
                    var bytes = outgoing.ToArray();
                    TcpClient[] now;
                    lock (Gate) now = [.. Listeners[b]];
                    foreach (var c in now)
                    {
                        try { c.GetStream().Write(bytes); }
                        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or SocketException)
                        {
                            lock (Gate) Listeners[b].Remove(c);
                            c.Dispose();
                        }
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or HttpRequestException or ObjectDisposedException) { }
            stop.WaitHandle.WaitOne(2000);
        }
    }

    /// <summary>
    /// A page that went away without a word leaves its socket open until a write to it fails, and
    /// with the playback paused nothing is written. This drops such sockets: a closed connection
    /// reads as zero bytes at once.
    /// </summary>
    private static void DropGone(int b)
    {
        lock (Gate)
        {
            foreach (var c in Listeners[b].ToArray())
            {
                bool gone;
                try { gone = c.Client.Poll(0, SelectMode.SelectRead) && c.Client.Available == 0; }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { gone = true; }
                if (!gone) continue;
                Listeners[b].Remove(c);
                c.Dispose();
            }
        }
    }
}
