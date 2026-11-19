using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CydMonitor;

/// <summary>
/// Duplicates every <see cref="Console.WriteLine"/> to a file with a
/// timestamp, so a session that hangs or crashes unattended still leaves a
/// trace of what led up to it, rather than only whatever happened to still be
/// on screen in a terminal that may itself be long gone. Deliberately does
/// NOT duplicate plain <see cref="Console.Write"/>: that's what the frame
/// loop's live "\r ...fps... kB/s..." gauge uses to overwrite itself roughly
/// once a second forever, and logging every tick of it would make the file
/// grow without bound while burying the one-off events — connects,
/// disconnects, exceptions — that are actually worth finding later. The
/// existing code already keeps that distinction (WriteLine for events, Write
/// for the live gauge), so this needed no changes anywhere else.
/// </summary>
internal sealed class FileTeeWriter : System.IO.TextWriter
{
    private readonly System.IO.TextWriter _console;
    private readonly System.IO.StreamWriter _file;

    public FileTeeWriter(System.IO.TextWriter console, System.IO.StreamWriter file)
    {
        _console = console;
        _file = file;
    }

    public override Encoding Encoding => _console.Encoding;

    public override void Write(char value) => _console.Write(value);
    public override void Write(string? value) => _console.Write(value);

    public override void WriteLine(string? value)
    {
        _console.WriteLine(value);
        try { _file.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {value}"); _file.Flush(); }
        catch (System.IO.IOException) { /* disk full, file locked elsewhere — the console line still went out */ }
    }

    public override void WriteLine() => _console.WriteLine();

    /// <summary>
    /// Opens (or rotates) the log file and swaps it in as <see
    /// cref="Console.Out"/>. Rotation is size-based rather than time-based —
    /// simplest thing that stops an idle server left running for weeks from
    /// growing the file without limit — and keeps exactly one previous file,
    /// which is enough to see across the moment something went wrong without
    /// building a real log-rotation scheme for a single-process tool.
    /// </summary>
    public static void Install(string path)
    {
        try
        {
            const long maxBytes = 5 * 1024 * 1024;
            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > maxBytes)
            {
                string old = path + ".old";
                System.IO.File.Delete(old);
                System.IO.File.Move(path, old);
            }
            var stream = new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.Read);
            var writer = new System.IO.StreamWriter(stream) { AutoFlush = false };
            writer.WriteLine($"--- start {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
            writer.Flush();
            Console.SetOut(new FileTeeWriter(Console.Out, writer));
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            // Logging to disk is a diagnostic nice-to-have, not something
            // that should stop the monitor coming up if the folder is
            // read-only or another instance has the file open.
        }
    }
}

internal sealed class Options
{
    public string MonitorSpec { get; set; } = "primary";
    public int    TargetFps   { get; set; } = 30;
    // Tuning knobs, not preferences: take effect for the run they are passed
    // on and are never written to settings.json — otherwise one measurement
    // (a 40 KB frame test) quietly became every later run's behaviour.
    //
    // 6: this link's round trip is not the steady ~90 ms measured earlier —
    // under load it swings as high as 445 ms. At depth 2, one slow round
    // trip stalls the frame loop directly, and it was measured stuck waiting
    // on the panel's ack 88-93% of the time for single-digit fps on a mostly
    // still desktop. Depth 6 rides out that variance; the desktop went to
    // 26-30 fps with 0% of the time spent waiting once it was applied.
    [System.Text.Json.Serialization.JsonIgnore] public int InFlight { get; set; } = 6;
    // 40 KB: what gets acked per round trip is frames-in-flight x this, and
    // the WiFi round trip swings widely (pings of 5-435 ms measured while
    // streaming). At 16 KB a full-screen JPEG film frame (12-18 KB) often
    // split in two and throughput capped near 100-160 KB/s with the server
    // stalled on acks most of the time — 6-17 fps; at 40 KB the same film ran
    // 27-28 fps with no strip ever dropped on the panel.
    [System.Text.Json.Serialization.JsonIgnore] public int FrameKb { get; set; } = 40;
    // 85: measured on a film as clean enough to not read as "JPEG", and still
    // over 25 fps full-screen once JPEG strips stopped being sharpened.
    [System.Text.Json.Serialization.JsonIgnore] public int JpegQuality { get; set; } = 85;
    public int    Port        { get; set; } = Proto.Port;
    public int    Contrast    { get; set; }
    public int    Gamma       { get; set; } = 100;
    /// <summary>0..100 unsharp-mask amount; 0 (default) matches the pre-sharpen picture exactly. See FrameProcessor.Sharpen.</summary>
    public int    Sharpen     { get; set; }
    /// <summary>
    /// Off by default, and the reason is measured rather than aesthetic. Ordered
    /// dithering hides the banding that 16-bit colour produces on gradients, but
    /// the noise it adds is exactly what stops a tile being flat — and flatness
    /// is what the encoder trades on. On a real desktop it moved a full repaint
    /// from 35.8 kB to 113.9 kB, pushing the worst-case frame rate over a
    /// 12 Mbit/s link from 41 fps down to 13. A desktop is mostly flat UI where
    /// there is little banding to hide, so the default favours the frame rate
    /// and the menu can turn it back on for gradient-heavy content.
    /// </summary>
    public bool   Dither      { get; set; }
    public ScaleMode Scale    { get; set; } = ScaleMode.Fit;
    public int    ResIndex    { get; set; } = 1;
    public int    Brightness  { get; set; } = 160;
    public int    CursorSize  { get; set; } = 20;
    /// <summary>Skip Desktop Duplication and use the slower GDI path; for diagnosing a capture problem.</summary>
    public bool   ForceGdi    { get; set; }
    // Not persisted in practice: a measurement mode, set per run on the command line.
    [System.Text.Json.Serialization.JsonIgnore] public bool Synthetic { get; set; }
    /// <summary>Create a Windows virtual monitor and mirror that, instead of an existing screen.</summary>
    public bool   Virtual     { get; set; }

    public const string DataFolder = "cyd-monitor";

    /// <summary>
    /// Carry pixels over UDP (protocol v5) when the panel offers it (see docs/protocol-v5-udp.pl.md).
    /// Per run, never persisted. Default true: this is the production behaviour as of the A/B
    /// measurement on 2026-09-21 (11 min: 2.4 zacieć/min, 2.4% czasu, zero >=1 s, vs the TCP
    /// baseline's 2.95/min, 4.2%, 27 zacięć >=1 s z 177) — v4-only TCP is still available with
    /// --no-udp, and a session on this transport falls back to it on its own if the panel stops
    /// acknowledging for 3 s (see UdpEngine.IsFailed / DeviceSession.FallBackToTcp).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Udp { get; set; } = true;

    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DataFolder, "settings.json");

    public static Options Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<Options>(File.ReadAllText(Path_)) ?? new Options();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file must not stop the monitor
            // coming up; defaults plus whatever the menu sends will rebuild it.
        }
        return new Options();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class Program
{
    private const string Usage = """
        cyd-monitor — wysyla obraz wybranego monitora Windows na panel CYD po WiFi.

          --virtual          utworz wirtualny monitor (Parsec VDD) i pokaz jego zawartosc
          --monitor <spec>   numer (1,2,...), \\.\DISPLAYn, albo fragment nazwy
                             sterownika, np. "virtual". Domyslnie: glowny ekran.
          --fps <n>          docelowa liczba klatek (domyslnie 30)
          --inflight <1..8>  ile klatek naraz czeka na potwierdzenie panelu, gdy piksele
                             ida przez TCP (domyslnie 2)
          --frame-kb <8..128> najwiekszy rozmiar jednej klatki w kB (domyslnie 40)
          --jpeg <0..100>    jakosc JPEG dla filmu/zdjec, 0 = wylaczony (domyslnie 85)
          --jpegtest <dir>   zapisz obraz testowy kodera JPEG do sprawdzenia
          --synthetic        zamiast pulpitu: ruchomy obraz testowy (pomiar wydajnosci panelu)
          --no-udp           piksele tylko przez TCP (protokol v4); domyslnie UDP (v5), gdy
                             panel to oferuje - patrz docs/protocol-v5-udp.pl.md
          --udp              wymus UDP (domyslne zachowanie; flaga na wypadek gdyby --no-udp
                             trafilo do settings.json wczesniejszej wersji)
          --udpsim [s]       symulacja kanalu UDP ze stratami na wirtualnym zegarze
                             (s sekund na scenariusz, domyslnie 30), potem zakoncz
          --udploop [s]      ten sam kanal danych przez prawdziwe gniazda na petli zwrotnej
                             (s sekund ruchu, domyslnie 8), potem zakoncz
          --port <n>         port TCP (domyslnie 7788)
          --contrast <-64..64>
          --gamma <50..300>  100 = bez zmian
          --sharpen <0..100> 0 = bez zmian (wyostrzanie krawedzi po skalowaniu)
          --scale fit|stretch|1:1
          --cursor <px>      rozmiar rysowanego kursora (domyslnie 20)
          --dither           wlacz dithering (gladsze gradienty, ~3x wiecej danych)
          --gdi              wymus stara sciezke GDI (diagnostyka; wolniejsza)
          --list             wypisz monitory i tryby, potem zakoncz
          --selftest [n]     sprawdz kodek na wlasnym pulpicie i zmierz ruch
                             (n klatek, domyslnie 60), potem zakoncz
          --help

        Ustawienia zmienione z menu na panelu zapisuja sie same i sa uzywane
        przy nastepnym starcie; flagi powyzej je nadpisuja.
        """;

    private static int Main(string[] args)
    {
        // Without this, a scaled desktop reports virtualised coordinates and
        // every capture and every injected touch lands off by the DPI factor.
        Native.SetProcessDPIAware();

        // 1 ms scheduler tick for this process, so the frame loop's sleeps
        // land where they are aimed instead of up to ~15.6 ms late. Released
        // automatically when the process exits.
        Native.timeBeginPeriod(1);

        FileTeeWriter.Install(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Options.DataFolder, "log.txt"));

        var opt = Options.Load();
        bool selftest = false;
        int selftestFrames = 60;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--help" or "-h": Console.WriteLine(Usage); return 0;
                case "--list": return ListMonitors();
                case "--vdd-reset":
                    Console.WriteLine("czyszcze wszystkie indeksy wirtualnego monitora (0-15)...");
                    VirtualDisplay.RemoveAllIndices();
                    Console.WriteLine("gotowe.");
                    return 0;
                case "--benchcapture": return CaptureBench.Run(i + 1 < args.Length && int.TryParse(args[i + 1], out int bn) ? bn : 30);
                case "--selftest":
                    // Deferred until after the loop so flags that follow it
                    // (--scale, --contrast) still apply to the run.
                    selftest = true;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int sf)) { i++; selftestFrames = Math.Clamp(sf, 1, 600); }
                    break;
                case "--monitor":
                    opt.MonitorSpec = Next() ?? opt.MonitorSpec;
                    // Naming a real monitor is a clear enough signal on its own:
                    // Options persists across runs, so without this a --virtual
                    // from a PREVIOUS invocation stays silently in effect and
                    // --monitor 1 keeps trying (and, if the driver is in a bad
                    // state, failing) to create the virtual display anyway. A
                    // later --virtual on the same command line still wins,
                    // since flags are applied in the order given.
                    opt.Virtual = false;
                    break;
                case "--fps": if (int.TryParse(Next(), out int f)) opt.TargetFps = Math.Clamp(f, 1, 60); break;
                case "--inflight": if (int.TryParse(Next(), out int inf)) opt.InFlight = Math.Clamp(inf, 1, 8); break;
                case "--frame-kb": if (int.TryParse(Next(), out int fkb)) opt.FrameKb = Math.Clamp(fkb, 8, 128); break;
                case "--jpeg": if (int.TryParse(Next(), out int jq)) opt.JpegQuality = Math.Clamp(jq, 0, 100); break;
                case "--jpegtest": return JpegTest.Run(Next() ?? ".", opt.JpegQuality > 0 ? opt.JpegQuality : 75);
                case "--port": if (int.TryParse(Next(), out int p)) opt.Port = p; break;
                case "--contrast": if (int.TryParse(Next(), out int c)) opt.Contrast = Math.Clamp(c, -64, 64); break;
                case "--gamma": if (int.TryParse(Next(), out int g)) opt.Gamma = Math.Clamp(g, 50, 300); break;
                case "--sharpen": if (int.TryParse(Next(), out int sh)) opt.Sharpen = Math.Clamp(sh, 0, 100); break;
                case "--cursor": if (int.TryParse(Next(), out int cs)) opt.CursorSize = Math.Clamp(cs, 0, 64); break;
                case "--no-dither": opt.Dither = false; break;
                case "--dither": opt.Dither = true; break;
                case "--gdi": opt.ForceGdi = true; break;
                case "--synthetic": opt.Synthetic = true; break;
                case "--udp": opt.Udp = true; break;
                case "--no-udp": opt.Udp = false; break;
                case "--skaner-test":
                    return ScannerBridge.RunConsole(i + 1 < args.Length && int.TryParse(args[i + 1], out int st) ? Math.Clamp(st, 1, 3600) : 10, pageOpen: true);
                case "--skaner-okno":
                    return ScannerBridge.RunConsole(i + 1 < args.Length && int.TryParse(args[i + 1], out int so) ? Math.Clamp(so, 1, 3600) : 60, pageOpen: false);
                case "--skaner-www":
                    // Only the page, next to a server running as usual, until it is stopped.
                    ScannerWeb.Start();
                    Thread.Sleep(i + 1 < args.Length && int.TryParse(args[i + 1], out int sw) ? Math.Clamp(sw, 1, 86400) * 1000 : 60_000);
                    return 0;
                case "--skaner-minimalizuj":
                    OxideUi.MinimizeApp();
                    return 0;
                case "--skaner-napraw":
                    Console.WriteLine(OxideUi.RepairWindowEntry(Next() ?? "") ? "naprawiono" : "bez zmian");
                    return 0;
                case "--skaner-awaria":
                {
                    // What the watchdog would do with the renderer note in this folder; starts nothing.
                    // With a count, every round after the first leaves a fresh note, as a new death would.
                    string dir = Next() ?? "";
                    int rounds = int.TryParse(Next(), out int ar) ? Math.Clamp(ar, 1, 10) : 1;
                    for (int r = 0; r < rounds; r++)
                    {
                        if (r > 0) File.WriteAllText(Path.Combine(dir, "renderer-fallback.txt"), "test\n");
                        Console.WriteLine(OxideUi.PlanRendererRestart(dir, DateTime.UtcNow) ?? "bez restartu");
                    }
                    return 0;
                }
                case "--skaner-klik":
                {
                    int radio = int.TryParse(Next(), out int kr) ? kr : 0;
                    byte cmd = (Next() ?? "").ToLowerInvariant() switch
                    {
                        "start" => Proto.ScanCmdStart,
                        "stop" => Proto.ScanCmdStop,
                        "next" => Proto.ScanCmdNext,
                        _ => (byte)0,
                    };
                    if (cmd == 0) { Console.Error.WriteLine("uzycie: --skaner-klik <0|1> <start|stop|next>"); return 2; }
                    // Without the radio's frequency there is nothing to check a window against,
                    // and its position decides.
                    long hz = i + 1 < args.Length && long.TryParse(args[i + 1], out long kh) ? kh : 0;
                    var problems = OxideUi.Press(cmd, [(radio, hz)], out _);
                    Console.WriteLine(problems.Count == 0 ? "ok" : problems[0].Problem);
                    return 0;
                }
                case "--udpsim":
                    return UdpSim.Run(i + 1 < args.Length && int.TryParse(args[i + 1], out int us) ? Math.Clamp(us, 5, 600) : 30);
                case "--udploop":
                    return UdpSim.RunLoopback(i + 1 < args.Length && int.TryParse(args[i + 1], out int ul) ? Math.Clamp(ul, 3, 120) : 8);
                case "--virtual": opt.Virtual = true; break;
                case "--no-virtual": opt.Virtual = false; break;
                case "--scale":
                    opt.Scale = (Next() ?? "fit").ToLowerInvariant() switch
                    {
                        "stretch" => ScaleMode.Stretch,
                        "1:1" or "one" or "native" => ScaleMode.OneToOne,
                        _ => ScaleMode.Fit,
                    };
                    break;
                default:
                    Console.Error.WriteLine($"nieznany argument: {a}");
                    Console.WriteLine(Usage);
                    return 2;
            }
        }
        opt.Save();

        if (selftest) return SelfTest.Run(opt, selftestFrames);

        OxideUi.StartSplitWatchdog();
        DialBridge.Start();
        ScannerWeb.Start();

        // The virtual monitor is a lease held by this process: the driver
        // unplugs it about a second after the last keepalive, so it exists for
        // exactly as long as the server runs and cannot be left behind by a
        // crash. Held in a using for the same reason.
        using var vdd = new VirtualDisplay();
        MonitorTarget? target;

        if (opt.Virtual)
        {
            if (!vdd.Start(out string vddError))
            {
                Console.Error.WriteLine($"nie moge utworzyc wirtualnego monitora: {vddError}");
                Console.Error.WriteLine(
                    "\nJednorazowa instalacja sterownika (wymaga administratora):\n" +
                    "  powershell -ExecutionPolicy Bypass -File scripts\\install-virtual-display.ps1\n" +
                    "\nAlbo pomin wirtualny ekran i pokaz istniejacy: --monitor 1");
                return 1;
            }
            // Windows needs a moment to attach the new monitor and hand it a
            // device name; asking too early finds nothing. 2 seconds looked
            // enough in early testing but was measured to fail outright on a
            // later run (the process exited here with "sterownik dodal ekran,
            // ale Windows go nie pokazuje" even though the monitor showed up
            // fine a few seconds later) — the settle time genuinely varies
            // run to run, so the budget is wide enough to cover the slow case
            // rather than tuned to the fast one.
            for (int tries = 0; tries < 150 && VirtualDisplay.FindCreatedMonitor() is null; tries++)
                Thread.Sleep(50);

            target = VirtualDisplay.FindCreatedMonitor();
            if (target is null)
            {
                Console.Error.WriteLine("sterownik dodal ekran, ale Windows go nie pokazuje — sprawdz Ustawienia ekranu");
                return 1;
            }

            var want = Proto.ResTable[Math.Clamp(opt.ResIndex, 0, Proto.ResTable.Length - 1)];
            var got = Monitors.SetResolution(target.Device, want.W, want.H);
            target = Monitors.Enumerate().FirstOrDefault(m => m.Device == target.Device) ?? target;
            Console.WriteLine($"wirtualny monitor #{vdd.Index} utworzony: {got.W}x{got.H}");

            // A panel that was already on and reconnecting can hit the TCP
            // listener within a second of the driver reporting the display
            // attached — observed end-to-end to be fast enough that DXGI's own
            // adapter/output enumeration and even a second GDI mode query can
            // still see a topology that has not finished settling (both now
            // retry for exactly this reason, see Monitors.TryEnumCurrentSettings
            // and DuplicationSource.FindOutputWithRetry). Waiting here first
            // means the common case never has to fall back on those retries at
            // all; they stay as the safety net for whatever this fixed pause
            // doesn't cover.
            Thread.Sleep(500);
        }
        else
        {
            target = ResolveTarget(opt.MonitorSpec);
        }

        if (target is null)
        {
            Console.Error.WriteLine($"nie znalazlem monitora \"{opt.MonitorSpec}\".");
            ListMonitors();
            Console.Error.WriteLine(
                "\nJesli mial to byc wirtualny monitor: najpierw zainstaluj sterownik\n" +
                "(scripts/install-virtual-display.ps1, wymaga uprawnien administratora).");
            return 1;
        }

        Console.WriteLine($"zrodlo : {target}");
        Console.WriteLine($"obraz  : {target.Bounds.Width}x{target.Bounds.Height} -> 320x240, " +
                          $"skalowanie {opt.Scale}, {opt.TargetFps} fps docelowo");
        Console.WriteLine($"port   : TCP {opt.Port}, wykrywanie UDP {opt.Port - 1}, piksele: {(opt.Udp ? $"UDP {opt.Port + 1} (spada na TCP po 3 s ciszy)" : "TCP (v4)")}");

        using var discovery = new Discovery(opt.Port - 1);
        var listener = new TcpListener(IPAddress.Any, opt.Port);
        listener.Start();
        Console.WriteLine("czekam na panel...\n");

        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (SocketException e) { Console.Error.WriteLine($"accept: {e.Message}"); break; }

            Console.WriteLine($"\npanel podlaczony: {client.Client.RemoteEndPoint}");

            // One panel at a time, deliberately. A second would need its own
            // frame buffers, its own reference frame and its own encoder, and
            // sharing a capture between two of them at different rates is a
            // different design; see README.md.
            var fresh = Monitors.Enumerate().FirstOrDefault(m => m.Device == target.Device) ?? target;
            using var session = new DeviceSession(client, fresh, opt);
            try
            {
                session.RunAsync().GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is IOException or SocketException or EndOfStreamException)
            {
            }
            Console.WriteLine("\npanel rozlaczony, czekam na kolejny...\n");
        }
        return 0;
    }

    private static MonitorTarget? ResolveTarget(string spec)
    {
        if (spec is "primary" or "glowny" or "1")
        {
            var all = Monitors.Enumerate();
            return all.FirstOrDefault(m => m.Primary) ?? all.FirstOrDefault();
        }
        return Monitors.Resolve(spec);
    }

    private static int ListMonitors()
    {
        var all = Monitors.Enumerate();
        if (all.Count == 0)
        {
            Console.WriteLine("nie znaleziono zadnego monitora");
            return 1;
        }
        Console.WriteLine("monitory:");
        foreach (var m in all)
        {
            Console.WriteLine("  " + m);
            var modes = Monitors.AvailableModes(m.Device);
            // Only the modes worth using here: 4:3 fits 320x240 exactly, so
            // anything else costs black bars.
            var fourThree = modes.Where(x => x.W * 3 == x.H * 4).ToList();
            Console.WriteLine("      tryby 4:3 : " +
                (fourThree.Count > 0
                    ? string.Join("  ", fourThree.Select(x => $"{x.W}x{x.H}"))
                    : "(brak — obraz bedzie mial czarne pasy lub rozciagniety)"));
        }
        return 0;
    }
}
