using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace CydMonitor;

/// <summary>
/// The Ulanzi Dial as a front panel for SDR Oxide. Ulanzi Studio owns the dial; a plugin of
/// ours (ulanzi-plugin in this repository) sits in it and passes every event on to this
/// loopback port as a plain GET, because a plugin page has no sockets of its own and all
/// that can drive Oxide already lives in this server.
///
/// The controls are Oxide's while Oxide has the foreground and keep their old meaning (system
/// volume, media keys, copy, undo) while it hasn't. That is decided here, event by event, and
/// not with a Studio preset tied to the program: Studio 3.4.8 locks up for good switching to
/// such a preset, and with its switch reminder turned off it does not switch at all.
///
/// In Oxide the two outer buttons above the knob pick a radio, and the rest acts on that one
/// alone. The knob and the memory buttons go over rigctld. A frequency or channel set that way also
/// drops the radio out of its scan, which is what a hand on a VFO should do, so the middle button is
/// what brings the scan back. That button goes over rigctld too where Oxide knows the scanner
/// commands (latka_skan.py), and otherwise, like the window, needs <see cref="OxideUi"/>, where a
/// press can take a while, so they wait in a queue of their own and the knob never waits for
/// them. The step is for all radios alike and is shown nowhere: the dial has no screen, and
/// neither has Oxide for a step that is not its own, so the next tick is what shows it.
/// </summary>
internal static partial class DialBridge
{
    private const int Port = Proto.Port + 4;
    // The channel raster on 2 m and 70 cm here comes first, a click moves on to the next.
    private static readonly long[] StepsHz = [12_500, 25_000, 100_000, 1_000];
    private const byte VkControl = 0x11, VkC = 0x43, VkY = 0x59, VkZ = 0x5A, VkF13 = 0x7C, VkF14 = 0x7D, VkVolumeMute = 0xAD,
                       VkVolumeDown = 0xAE, VkVolumeUp = 0xAF, VkMediaNext = 0xB0, VkMediaPrev = 0xB1, VkPlayPause = 0xB3;
    private const uint KeyUp = 2;
    // Presses landing in one frame of Oxide's would count as one.
    private const int KeyGapMs = 20;
    // A shortcut goes to the pane with the outline; a click with the mouse can have moved it.
    private const int OutlineKeepMs = 2000;
    // Longer than a sweep stays on a channel: settling, a 100 ms listen and Brown's 250 ms
    // second look at a peak. A stop lasts seconds, the resume time at the least.
    private const int HoldMs = 700, HoldPollMs = 50;

    private static readonly ScannerBridge.RigLink[] Rigs =
        ScannerBridge.RigPorts.Select(p => new ScannerBridge.RigLink(p)).ToArray();
    private static readonly Channel<Action> UiWork = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropWrite });
    private static readonly AutoResetEvent KnobMoved = new(false);
    // Only the knob's thread looks here.
    private static readonly ScannerBridge.OxideConfig Channels = new();
    private static int _radio, _step, _tuneTicks, _memoryTicks, _zoomTicks, _filterTicks;
    private static long _outlineUntil;

    /// <summary>Starts listening; everything here lives as long as the server.</summary>
    public static void Start()
    {
        new Thread(AcceptLoop) { IsBackground = true, Name = "dial-listen" }.Start();
        new Thread(KnobLoop) { IsBackground = true, Name = "dial-knob" }.Start();
        _ = Task.Run(UiLoop);
    }

    /// <summary>The server being replaced can still hold the port for a moment, and a second
    /// copy for as long as it runs, so keep asking: nothing else would bring the dial back.</summary>
    private static TcpListener Listen()
    {
        for (bool told = false; ; Thread.Sleep(2000))
        {
            var listener = new TcpListener(IPAddress.Loopback, Port);
            try
            {
                listener.Start();
                if (told) Console.WriteLine($"[dial] port {Port} odzyskany");
                return listener;
            }
            catch (SocketException e)
            {
                listener.Stop();
                if (!told) Console.WriteLine($"[dial] port {Port} niedostepny, ponawiam: {e.Message}");
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
            catch (SocketException e) { Console.WriteLine($"[dial] accept: {e.Message}"); }
        }
    }

    private static void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 2000;
                client.SendTimeout = 2000;
                var stream = client.GetStream();
                using var input = new StreamReader(stream, Encoding.ASCII);
                // "GET /tune/1 HTTP/1.1", then headers nobody here needs.
                string? request = input.ReadLine();
                while (!string.IsNullOrEmpty(input.ReadLine())) { }
                stream.Write("HTTP/1.1 204 No Content\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n"u8);
                if (request?.Split(' ') is [_, var path, ..]) Handle(path);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { }
        }
    }

    private static void Handle(string path)
    {
        bool oxide = OxideUi.IsForeground();
        switch (path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            case ["tune", var n] when int.TryParse(n, out int ticks):
                if (oxide) Turn(ref _tuneTicks, ticks);
                else Tap(ticks > 0 ? VkVolumeUp : VkVolumeDown);
                break;
            case ["hold", var n] when int.TryParse(n, out int ticks):
                if (oxide) Turn(ref _zoomTicks, ticks);
                else Tap(ticks > 0 ? VkVolumeUp : VkVolumeDown);
                break;
            case ["filter", var n] when int.TryParse(n, out int ticks):
                if (oxide) Turn(ref _filterTicks, ticks);
                else Tap(ticks > 0 ? VkVolumeUp : VkVolumeDown);
                break;
            case ["press"]:
                if (oxide) NextStep();
                else Tap(VkVolumeMute);
                break;
            // The plugin's names for the two right buttons, from when they were the volume. The upper
            // goes up the memory list, the lower down.
            case ["vol", "1"]:
                if (oxide) Turn(ref _memoryTicks, 1);
                else Tap(VkControl, VkY);
                break;
            case ["vol", "-1"]:
                if (oxide) Turn(ref _memoryTicks, -1);
                else Tap(VkControl, VkZ);
                break;
            case ["radio", "0"]:
                if (oxide) Pick(path, 0);
                else Tap(VkMediaPrev);
                break;
            case ["radio", "1"]:
                if (oxide) Pick(path, 1);
                else Tap(VkMediaNext);
                break;
            case ["scan"]:
                if (oxide) Queue(path, ToggleScan);
                else Tap(VkPlayPause);
                break;
            case ["hide"]:
                if (oxide) Queue(path, OxideUi.Hide);
                else Tap(VkControl, VkC);
                break;
            case ["show"]:
                Queue(path, () => OxideUi.Summon(Rigs[Volatile.Read(ref _radio)].ReadHz()));
                break;
            default:
                Console.WriteLine($"[dial] nieznane: {path}");
                break;
        }
    }

    private static void Turn(ref int ticks, int by)
    {
        Interlocked.Add(ref ticks, by);
        KnobMoved.Set();
    }

    private static void NextStep()
    {
        int step = (Volatile.Read(ref _step) + 1) % StepsHz.Length;
        Volatile.Write(ref _step, step);
        Console.WriteLine($"[dial] krok {StepsHz[step]} Hz");
    }

    private static void Pick(string path, int radio)
    {
        Volatile.Write(ref _radio, radio);
        Queue(path, () => OxideUi.SelectTab(Rigs[radio].ReadHz()));
    }

    private static void Queue(string path, Action work)
    {
        Console.WriteLine($"[dial] {path}");
        UiWork.Writer.TryWrite(work);
    }

    /// <summary>Presses the keys in order and lets them go in reverse, so a modifier holds for the key after it.</summary>
    private static void Tap(params byte[] keys)
    {
        foreach (byte k in keys) KeyEvent(k, 0, 0, 0);
        foreach (byte k in keys.Reverse()) KeyEvent(k, 0, KeyUp, 0);
    }

    /// <summary>A knob turned faster than rigctld answers adds up instead of queueing.</summary>
    private static void KnobLoop()
    {
        while (true)
        {
            KnobMoved.WaitOne();
            int radio = Volatile.Read(ref _radio);
            var rig = Rigs[radio];
            int tune = Interlocked.Exchange(ref _tuneTicks, 0), memory = Interlocked.Exchange(ref _memoryTicks, 0),
                zoom = Interlocked.Exchange(ref _zoomTicks, 0), filter = Interlocked.Exchange(ref _filterTicks, 0);
            // The mirror must outlive anything that goes wrong here, and the next tick may work.
            try
            {
                if (tune != 0) rig.Tune(tune, StepsHz[Volatile.Read(ref _step)]);
                if (memory != 0 && Channels.MemoryFrom(radio, rig.ReadMemory(), rig.ReadHz(), memory) is > 0 and var id) rig.Recall(id);
                if (zoom != 0) OxideUi.Zoom(rig.ReadHz(), zoom);
                if (filter != 0) Widen(rig.ReadHz(), filter);
            }
            catch (Exception e) { Console.WriteLine($"[dial] {e.GetType().Name}: {e.Message}"); }
        }
    }

    /// <summary>
    /// rigctld takes a passband, answers that it did and changes nothing, so the filter goes
    /// through two shortcuts bound to Oxide's own FilterWidth in its input.json
    /// (ulanzi-plugin\set-oxide-filter-keys.py). Oxide is in front whenever this runs.
    /// </summary>
    private static void Widen(long radioHz, int ticks)
    {
        if (Environment.TickCount64 >= _outlineUntil) OxideUi.SelectTab(radioHz);
        _outlineUntil = Environment.TickCount64 + OutlineKeepMs;
        for (int i = 0; i < Math.Abs(ticks); i++)
        {
            Tap(ticks > 0 ? VkF13 : VkF14);
            Thread.Sleep(KeyGapMs);
        }
    }

    private static async Task UiLoop()
    {
        await foreach (var work in UiWork.Reader.ReadAllAsync())
        {
            // The mirror must outlive anything that goes wrong here, and the next press may work.
            try { work(); }
            catch (Exception e) { Console.WriteLine($"[dial] {e.GetType().Name}: {e.Message}"); }
        }
    }

    /// <summary>
    /// The middle button: NEXT while the scan stands on a station, START or STOP otherwise.
    /// Only an Oxide that says whether it scans gets NEXT, since a scanner that is off stands
    /// still as well.
    /// </summary>
    private static void ToggleScan()
    {
        int radio = Volatile.Read(ref _radio);
        var rig = Rigs[radio];
        long hz = rig.ReadHz();
        byte cmd = rig.ReadScanning() == true && Holding(rig, hz) ? Proto.ScanCmdNext : OxideUi.ScanToggle;
        // Straight to the radio's scanner where Oxide knows how (latka_skan.py); the GUI otherwise.
        bool pressed = false;
        var left = ScannerBridge.PressByRig(cmd, [(radio, hz)], ref pressed);
        if (left.Count == 0) return;
        foreach (var (r, problem) in OxideUi.Press(cmd, left, out _))
            Console.WriteLine($"[dial] radio {r}: {problem}");
    }

    /// <summary>
    /// Whether a running scan has stopped on a station. Neither rigctld nor the window says so,
    /// but a sweep leaves every channel within <see cref="HoldMs"/>, so a frequency that stays put
    /// that long is a stop. The first move answers at once.
    /// </summary>
    private static bool Holding(ScannerBridge.RigLink rig, long hz)
    {
        for (long until = Environment.TickCount64 + HoldMs; Environment.TickCount64 < until; Thread.Sleep(HoldPollMs))
            if (rig.ReadHz() != hz) return false;
        return hz != 0;
    }

    [LibraryImport("user32.dll", EntryPoint = "keybd_event")]
    private static partial void KeyEvent(byte vk, byte scan, uint flags, nuint extra);
}
