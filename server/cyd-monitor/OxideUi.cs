using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace CydMonitor;

/// <summary>
/// Presses SDR Oxide's scanner buttons. None of its protocols or bindable actions
/// reach the scanner, but the app exposes its UI to Windows UI Automation, which
/// needs neither the mouse nor the foreground.
///
/// The buttons have no accessible names, so they are found by their place in the
/// Scanner window and checked against their known size. SKIP sits right next to
/// NEXT and changes the saved skip list, so a layout that doesn't check out is
/// refused rather than guessed at. A collapsed window has no buttons at all: it is
/// expanded for the press and collapsed again, unless the user had it expanded. A closed
/// one is opened for the press and closed again, so the user never needs it on screen;
/// a scan carries on without its window. A minimized app draws nothing and so has no
/// buttons either: it is restored without taking the focus for as long as the press
/// takes, which shows as a short flash.
///
/// A radio with no pane has no buttons at all, and Oxide forgets its split view at every
/// start. The watchdog here brings the second pane back, once per run of Oxide, so a pane
/// the user closes on purpose stays closed until a button on the panel needs it. Having
/// set the program up it minimizes it, because the desktop belongs to the APRS station.
///
/// The panel can also start the program and close it, which is what START and STOP
/// come to mean once there is no scanner left to start or stop.
/// </summary>
internal static partial class OxideUi
{
    /// <summary>For <see cref="Press"/>: START or STOP, whichever the scanner is not doing. Never on the wire.</summary>
    internal const byte ScanToggle = 0xFF;

    private const string WindowName = "Scanner";
    private const int SettleMs = 700;
    // Long enough for the app to draw a frame, which is when it acts on a press.
    private const int FrameMs = 80;
    private const int WalkTries = 5;
    private const int PressTries = 5;
    private const int RestoreMs = 1500;
    private const int SwShowNoActivate = 4, SwMinimize = 6, SwShowMinNoActivate = 7, SwRestore = 9;
    // Two radios on 2 m and 70 cm are further apart than any one scan list is wide.
    private const long SameRadioHz = 100_000_000;
    private const int SplitCheckMs = 5000;
    // Oxide opens its radios before it settles; a split asked for earlier could be lost.
    private const int SplitSettleMs = 8000;
    private const int SplitTries = 3;
    private const int WmClose = 0x0010, WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmMouseWheel = 0x020A;
    private const int WheelNotch = 120;
    // Finding where to zoom walks the whole tree, too slow for every tick of a turn.
    private const int ZoomKeepMs = 2000;
    // A restored window needs a frame or two before it can be saved as a real window.
    private const int CloseSettleMs = 300;

    private static readonly string Config = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sdroxide", "sdroxide", "config");
    private static readonly string AppState = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sdroxide", "data", "app.ron");
    private const string SaneWindow =
        "\"window\": \"(inner_position_pixels:Some((x:0.0,y:23.0)),outer_position_pixels:Some((x:0.0,y:0.0))," +
        "fullscreen:false,maximized:true,inner_size_points:Some((x:1600.0,y:900.0)))\"";
    private static DateTime _appStateChecked;
    // Left by Oxide's panic hook when its graphics device dies; a start with it renders through OpenGL.
    private const string RendererNote = "renderer-fallback.txt";
    private const string RendererNoteAside = "renderer-fallback.txt.awaria-";
    // A note older than this was not left by the run that just ended.
    private static readonly TimeSpan CrashFresh = TimeSpan.FromMinutes(2);
    // Time for the dongles to be let go and the driver to come back.
    private const int CrashRestartDelayMs = 5000;
    private const int CrashRestartsPerHour = 3;
    private static readonly Queue<DateTime> _crashRestarts = new();
    private static DateTime _crashNoteSeen;
    // The watchdog and a press both restore and minimize the same window.
    private static readonly object UiLock = new();
    // Started because somebody asked to see it, so the watchdog must not minimize it.
    private static volatile bool _summoned;
    private static System.Windows.Point _zoomAt;
    private static long _zoomHz, _zoomUntil;

    private static readonly TreeWalker Walker = TreeWalker.RawViewWalker;

    [GeneratedRegex(@"^(scanning|holding)\b.*?(\d+\.\d+) MHz")]
    private static partial Regex StatusRx();

    [GeneratedRegex(@"^(\d+\.\d+) MHz\b")]
    private static partial Regex ChannelRx();

    [GeneratedRegex("\"window\": \"\\([^\"]*\\)\"")]
    private static partial Regex SavedWindowRx();

    /// <summary>Whether any button really went down, a radio already in the state asked for needs none, and what a toggle came to.</summary>
    private sealed class Outcome { public bool Pressed; public byte? Toggled; }

    public static bool IsRunning()
    {
        var all = Process.GetProcessesByName("sdroxide");
        foreach (var p in all) p.Dispose();
        return all.Length > 0;
    }

    private const string OwnBuild = @"D:\oxide-build\bin\sdroxide.exe";

    /// <summary>
    /// Starts Oxide with no console: a click into its console window stalls the program.
    /// Through cmd for the redirection, because a pipe from this process would break when the
    /// server restarts, and Oxide would die writing to it. The file also keeps what Oxide
    /// says as it goes down: a Rust panic leaves nothing in the Windows event log.
    /// With <paramref name="scan"/> the scanners start with it, as they were before a crash.
    /// </summary>
    public static void StartApp(bool scan = false)
    {
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "sdroxide", "sdroxide.exe");
        // The portable package keeps Oxide in "oxide" next to the server's own folder.
        string packaged = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "oxide", "sdroxide.exe"));
        // The patched build, when there is one, is the one in use.
        string exe = File.Exists(OwnBuild) ? OwnBuild : File.Exists(packaged) ? packaged : installed;
        string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Options.DataFolder, "oxide.log");
        RepairSavedWindow();
        try { File.Move(log, log + ".old", true); }
        catch (FileNotFoundException) { }
        var start = new ProcessStartInfo("cmd.exe", $"/c \"\"{exe}\" > \"{log}\" 2>&1\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        if (scan) start.Environment["SDROXIDE_AUTOSCAN"] = "1";
        Process.Start(start)?.Dispose();
    }

    /// <summary>
    /// Oxide does not outlive its graphics device: a driver reset (Windows event 4101, seen on
    /// 2026-10-30) ends the process, and its panic hook leaves the renderer note behind as it
    /// goes. So Oxide gone with a note written in the last two minutes is such a death, never a
    /// close by the user or the panel, which write none, and it is started again as it was closed:
    /// no scan is switched on (SP9TKN, 2026-11-10 - an automatic scan also sent the IC-7100 through
    /// every memory, and a receiver parked on a satellite was taken off it).
    /// </summary>
    private static void RestartAfterRendererCrash()
    {
        string? renderer = PlanRendererRestart(Config, DateTime.UtcNow);
        if (renderer == null) return;
        Thread.Sleep(CrashRestartDelayMs);
        if (IsRunning()) return;
        Console.WriteLine($"[skaner] Oxide padl w sterowniku grafiki, uruchamiam go ponownie ({renderer})");
        StartApp();
    }

    /// <summary>
    /// The renderer the restart after a fresh note will get, or null when there is nothing to
    /// restart for. The first death in a day keeps the default renderer, D3D12, because a reset
    /// is a passing thing: the note is moved aside, where it also counts as that day's first.
    /// A second within the day leaves it, and the start renders through OpenGL until someone
    /// removes it. At most three an hour, so a driver that dies at every start is not hammered.
    /// </summary>
    internal static string? PlanRendererRestart(string config, DateTime now)
    {
        string note = Path.Combine(config, RendererNote);
        DateTime written;
        try
        {
            if (!File.Exists(note)) return null;
            written = File.GetLastWriteTimeUtc(note);
        }
        catch (IOException) { return null; }
        if (written == _crashNoteSeen || now - written > CrashFresh) return null;
        _crashNoteSeen = written;
        while (_crashRestarts.Count > 0 && now - _crashRestarts.Peek() > TimeSpan.FromHours(1)) _crashRestarts.Dequeue();
        if (_crashRestarts.Count >= CrashRestartsPerHour)
        {
            Console.WriteLine($"[skaner] Oxide padl w sterowniku grafiki po {CrashRestartsPerHour} restartach w ciagu godziny, nie uruchamiam go ponownie");
            return null;
        }
        _crashRestarts.Enqueue(now);
        // A move keeps the time the note was written, which is the time of that death.
        bool earlier = Directory.EnumerateFiles(config, RendererNoteAside + "*")
            .Any(f => now - File.GetLastWriteTimeUtc(f) < TimeSpan.FromDays(1));
        if (earlier) return "OpenGL, drugi raz w ciagu doby";
        File.Move(note, Path.Combine(config, RendererNoteAside + written.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)), true);
        return "D3D12";
    }

    /// <summary>
    /// The window's own close, so Oxide saves its session as it does for the user. Never while
    /// minimized: see <see cref="RepairSavedWindow"/>.
    /// </summary>
    public static void CloseApp()
    {
        var hwnd = MainWindow();
        if (hwnd == IntPtr.Zero) return;
        lock (UiLock)
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SwShowNoActivate);
                Thread.Sleep(CloseSettleMs);
            }
            PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Oxide closed while minimized saves its window at -32000 with a size of 0, and from then
    /// on panics during the first frame of every start. Nothing in the program can undo that,
    /// so it is put right here, in the one place the state is kept, while Oxide is not running.
    /// </summary>
    private static void RepairSavedWindow()
    {
        lock (UiLock)
        {
            var stamp = File.GetLastWriteTimeUtc(AppState);
            if (stamp == _appStateChecked) return;
            _appStateChecked = stamp;
            if (RepairWindowEntry(AppState)) Console.WriteLine("[skaner] Oxide: naprawiony stan okna zapisany przy minimalizacji");
        }
    }

    /// <summary>Puts a sane window in this state file if it holds a minimized one; true when it did.</summary>
    internal static bool RepairWindowEntry(string path)
    {
        string text = File.ReadAllText(path);
        var saved = SavedWindowRx().Match(text);
        if (!saved.Success || !saved.Value.Contains("x:-32000.0")) return false;
        File.Copy(path, path + ".bak-okno", true);
        File.WriteAllText(path, text.Replace(saved.Value, SaneWindow), new UTF8Encoding(false));
        return true;
    }

    /// <summary>
    /// Takes the program off the desktop without taking the focus from whatever has it. The
    /// console too, which only a start by hand leaves on show: a hidden one must stay hidden,
    /// and minimizing would bring it to the taskbar.
    /// </summary>
    internal static void MinimizeApp()
    {
        foreach (var hwnd in AppWindows("Window Class", "ConsoleWindowClass"))
            if (IsWindowVisible(hwnd)) ShowWindow(hwnd, SwShowMinNoActivate);
    }

    /// <summary>
    /// Puts the program in front of the user. One that is not running is started, and comes
    /// up the same way once the watchdog has its second pane.
    /// </summary>
    public static void Summon(long radioHz)
    {
        var hwnd = MainWindow();
        if (hwnd == IntPtr.Zero)
        {
            _summoned = true;
            if (!IsRunning()) StartApp();
            return;
        }
        BringToFront(hwnd);
        SelectTab(radioHz);
    }

    /// <summary>
    /// Windows gives the foreground only to the process that saw the last input, which a
    /// server never is. So it makes one first: a mouse event that moves and presses nothing.
    /// </summary>
    private static void BringToFront(IntPtr hwnd)
    {
        lock (UiLock)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            var nothing = new EmptyInput();
            SendInput(1, ref nothing, Marshal.SizeOf<EmptyInput>());
            if (!SetForegroundWindow(hwnd)) Console.WriteLine("[skaner] Oxide: Windows nie oddal pierwszego planu");
        }
    }

    /// <summary>
    /// Moves Oxide's accent outline, and the keyboard with it, to this radio's pane. To UI
    /// Automation a tab is plain text, so that takes a click, and a posted one moves neither
    /// the pointer nor the focus. It lands on the radio's own open tab, where a click does
    /// nothing else, or on its tab in the only pane, which brings the radio up. A minimized
    /// window shows no outline, so this waits for <see cref="Summon"/>.
    /// </summary>
    public static void SelectTab(long radioHz)
    {
        var hwnd = MainWindow();
        if (hwnd == IntPtr.Zero || radioHz == 0 || IsIconic(hwnd)) return;
        OnAppWindow(hwnd, (main, _) =>
        {
            var kids = Children(main);
            string name = Radios().MinBy(r => Math.Abs(r.Hz - radioHz)).Name;
            if (OwnTab(kids, name, radioHz) is { } tab) PostClick(hwnd, Box(tab));
        });
    }

    /// <summary>
    /// Every pane's strip names every radio, and a radio up in one pane is greyed out in the
    /// others, so the tab to click is the one in the pane whose dial is on that band.
    /// </summary>
    private static AutomationElement? OwnTab(List<AutomationElement> kids, string name, long radioHz)
    {
        AutomationElement? any = null;
        int from = 0;
        for (int i = 0; i < kids.Count; i++)
        {
            if (!IsPaneEnd(kids[i])) continue;
            var tab = kids.Skip(from).Take(i - from).FirstOrDefault(k => Name(k) == name);
            if (Math.Abs(DialHz(kids, from, i) - radioHz) <= SameRadioHz) return tab;
            any ??= tab;
            from = i;
        }
        return any;
    }

    private static void PostClick(IntPtr hwnd, System.Windows.Rect box)
    {
        var lParam = ClientParam(hwnd, box.X + box.Width / 2, box.Y + box.Height / 2);
        PostMessage(hwnd, WmMouseMove, IntPtr.Zero, lParam);
        PostMessage(hwnd, WmLButtonDown, 1, lParam);
        Thread.Sleep(FrameMs);
        PostMessage(hwnd, WmLButtonUp, IntPtr.Zero, lParam);
    }

    private static IntPtr ClientParam(IntPtr hwnd, double screenX, double screenY)
    {
        var at = new ClientPoint { X = (int)screenX, Y = (int)screenY };
        ScreenToClient(hwnd, ref at);
        return (IntPtr)((at.Y << 16) | (at.X & 0xFFFF));
    }

    /// <summary>
    /// Zooms the radio's panadapter, by the mouse wheel, which is the only way in: a posted
    /// notch per tick, over the middle of the pane. Low down, on the waterfall, because a Scanner
    /// window left open covers the top of it and would take the wheel. It works only with the
    /// real pointer over the window, as it is with Oxide on top and maximized: anywhere else
    /// Windows reports the pointer gone at once and the wheel goes nowhere.
    /// </summary>
    public static void Zoom(long radioHz, int ticks)
    {
        var hwnd = MainWindow();
        if (hwnd == IntPtr.Zero || radioHz == 0) return;
        long now = Environment.TickCount64;
        if (now >= _zoomUntil || Math.Abs(radioHz - _zoomHz) > SameRadioHz)
        {
            if (OwnDisplay(Children(AutomationElement.FromHandle(hwnd)), radioHz) is not { } display) return;
            var box = Box(display);
            _zoomAt = new(box.X + box.Width / 2, box.Y + box.Height * 3 / 4);
            _zoomHz = radioHz;
        }
        _zoomUntil = now + ZoomKeepMs;
        PostMessage(hwnd, WmMouseMove, IntPtr.Zero, ClientParam(hwnd, _zoomAt.X, _zoomAt.Y));
        Thread.Sleep(FrameMs / 4);
        PostMessage(hwnd, WmMouseWheel, (IntPtr)(ticks * WheelNotch << 16), (IntPtr)(((int)_zoomAt.Y << 16) | ((int)_zoomAt.X & 0xFFFF)));
    }

    /// <summary>The panadapter and waterfall of the pane on this radio's band.</summary>
    private static AutomationElement? OwnDisplay(List<AutomationElement> kids, long radioHz)
    {
        int from = 0;
        for (int i = 0; i < kids.Count; i++)
        {
            if (!IsPaneEnd(kids[i])) continue;
            if (Math.Abs(DialHz(kids, from, i) - radioHz) <= SameRadioHz) return kids[i];
            from = i;
        }
        return null;
    }

    /// <summary>
    /// Whether the user is in Oxide right now. The program's console counts, it is no other program's.
    /// A minimized Oxide does not: Windows can leave the foreground on it when nothing but the
    /// desktop is left (and SW_SHOWMINNOACTIVE keeps it there on purpose), and the dial then went
    /// on tuning Oxide with the user looking at the desktop.
    /// </summary>
    public static bool IsForeground()
    {
        IntPtr front = GetForegroundWindow();
        if (front == IntPtr.Zero || IsIconic(front)) return false;
        GetWindowThreadProcessId(front, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName == "sdroxide";
        }
        // Gone between the two calls, or nothing has the foreground at all.
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Sends the program down at the user's request: unlike <see cref="MinimizeApp"/> this hands
    /// the foreground to the window underneath.
    /// </summary>
    public static void Hide()
    {
        foreach (var hwnd in AppWindows("Window Class", "ConsoleWindowClass"))
            if (IsWindowVisible(hwnd)) ShowWindow(hwnd, SwMinimize);
    }

    /// <summary>Presses one button for each radio given; returns what could not be done, as short messages for the panel.</summary>
    public static List<(int Radio, string Problem)> Press(byte cmd, IReadOnlyList<(int Radio, long Hz)> targets, out bool pressed)
    {
        var problems = new List<(int, string)>();
        var outcome = new Outcome();
        pressed = false;
        var hwnd = MainWindow();
        if (hwnd == IntPtr.Zero)
        {
            foreach (var t in targets) problems.Add((t.Radio, "OXIDE WYLACZONY"));
            return problems;
        }
        OnAppWindow(hwnd, (main, restored) =>
        {
            // Only a help to the press: without it the press still does what it can.
            try { EnsureTwoPanes(main, out _); }
            catch (ElementNotAvailableException) { }
            foreach (var t in targets)
                if (PressSettled(main, t.Radio, cmd, t.Hz, restored, outcome) is { } problem) problems.Add((t.Radio, problem));
        });
        pressed = outcome.Pressed;
        return problems;
    }

    /// <summary>Runs <paramref name="work"/> with the app's tree in reach, restoring a minimized window for just that long.</summary>
    private static void OnAppWindow(IntPtr hwnd, Action<AutomationElement, bool> work)
    {
        lock (UiLock)
        {
            bool minimized = IsIconic(hwnd);
            if (minimized) ShowWindow(hwnd, SwShowNoActivate);
            try
            {
                var main = AutomationElement.FromHandle(hwnd);
                var until = Environment.TickCount64 + RestoreMs;
                while (Children(main).Count < 20 && Environment.TickCount64 < until) Thread.Sleep(30);
                work(main, minimized);
            }
            finally
            {
                if (minimized) ShowWindow(hwnd, SwShowMinNoActivate);
            }
        }
    }

    /// <summary>
    /// Oxide above the PC's other work, however it was started. With the CPU at 100% (a
    /// compile) the 2 m dongle's stream went to pieces and the whole waterfall with it.
    /// </summary>
    private static void RaisePriority()
    {
        foreach (var p in Process.GetProcessesByName("sdroxide"))
            using (p)
            {
                try
                {
                    if (p.PriorityClass == ProcessPriorityClass.Normal) p.PriorityClass = ProcessPriorityClass.AboveNormal;
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
    }

    /// <summary>
    /// Minimised, Oxide keeps the whole picture of its window in memory, some 400 MB that
    /// nothing touches. Once per minimising its working set is emptied: what the radios use
    /// comes straight back, the window's part only when it is opened again. The memory stays
    /// Oxide's; the RAM goes to whatever else runs.
    /// </summary>
    private static void TrimWhenMinimized(IntPtr hwnd)
    {
        if (!IsIconic(hwnd))
        {
            _trimmed = false;
            return;
        }
        if (_trimmed) return;
        _trimmed = true;
        foreach (var p in Process.GetProcessesByName("sdroxide"))
            using (p)
            {
                try
                {
                    long before = p.WorkingSet64;
                    if (!EmptyWorkingSet(p.Handle)) continue;
                    p.Refresh();
                    Console.WriteLine($"[skaner] Oxide zminimalizowany: oddal Windows {(before - p.WorkingSet64) >> 20} MB RAM");
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
    }

    private static bool _trimmed;

    /// <summary>Starts the split view watchdog; it lives as long as the server.</summary>
    public static void StartSplitWatchdog() =>
        new Thread(SplitLoop) { IsBackground = true, Name = "oxide-split" }.Start();

    private static void SplitLoop()
    {
        var done = IntPtr.Zero;   // the window already seen to, so one run of Oxide gets one go
        while (true)
        {
            Thread.Sleep(SplitCheckMs);
            try
            {
                var hwnd = MainWindow();
                if (hwnd == IntPtr.Zero)
                {
                    done = IntPtr.Zero;
                    if (!IsRunning())
                    {
                        RepairSavedWindow();
                        RestartAfterRendererCrash();
                    }
                    continue;
                }
                RaisePriority();
                TrimWhenMinimized(hwnd);
                if (hwnd == done || !OxideSettled()) continue;
                // Oxide lays out its own panes now, as they were at the last close (SP9TKN,
                // 2026-11-10): nothing is clicked here and the window is not minimised. Pressing
                // the split button from here guessed the wrong radio once a third one was added.
                done = hwnd;
                if (_summoned)
                {
                    _summoned = false;
                    BringToFront(hwnd);
                }
            }
            // Oxide may close at any moment; the next look starts over.
            catch (Exception e) { Console.WriteLine($"[skaner] watchdog panelu: {e.GetType().Name}: {e.Message}"); }
        }
    }

    private static bool OxideSettled()
    {
        foreach (var p in Process.GetProcessesByName("sdroxide"))
            using (p)
                if ((DateTime.Now - p.StartTime).TotalMilliseconds >= SplitSettleMs) return true;
        return false;
    }

    /// <summary>
    /// True when the window shows two panes, after pressing ⊞ on the hidden radio's tab if
    /// it did not. A radio's frequency tells which one the only pane has: Oxide keeps each
    /// radio's last one in its session file, and that is close enough for the band.
    /// </summary>
    private static bool EnsureTwoPanes(AutomationElement main, out bool split)
    {
        split = false;
        var kids = Children(main);
        if (PaneCount(kids) >= 2) return true;
        var radios = Radios();
        if (radios.Count < 2) return true;
        long dial = DialHz(kids, 0, kids.Count);
        var hidden = radios.OrderByDescending(r => Math.Abs(r.Hz - dial)).First();

        // On a tab: the name, then LINK, the speaker and ⊞.
        int tab = kids.FindIndex(k => Name(k) == hidden.Name);
        if (tab < 0 || tab + 3 >= kids.Count || !IsSize(kids[tab + 3], 24, 23)) return false;
        Console.WriteLine($"[skaner] Oxide: wlaczam drugi panel ({hidden.Name})");
        Invoke(kids[tab + 3], "split");
        var until = Environment.TickCount64 + RestoreMs;
        while (Environment.TickCount64 < until)
        {
            Thread.Sleep(50);
            if (PaneCount(Children(main)) >= 2)
            {
                split = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>SETTINGS is the one button of its size, and every pane has its own.</summary>
    private static int PaneCount(List<AutomationElement> kids) => kids.Count(IsPaneEnd);

    /// <summary>The panadapter and waterfall, which close each pane. A tool window is as big, but it has a name.</summary>
    private static bool IsPaneEnd(AutomationElement el) =>
        Box(el) is { Width: > 300, Height: > 300 } && Name(el).Length == 0;

    /// <summary>The enabled radios, by the name their tab shows, with the frequency Oxide last saved.</summary>
    private static List<(string Name, long Hz)> Radios()
    {
        var list = new List<(string, long)>();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Config, "radios.json")));
        foreach (var r in doc.RootElement.GetProperty("radios").EnumerateArray())
        {
            if (!r.GetProperty("enabled").GetBoolean()) continue;
            int id = r.GetProperty("id").GetInt32();
            string dir = id == 0 ? Config : Path.Combine(Config, $"radio-{id}");
            // A tab that plays the I/Q buffer back is not a receiver: never split it in.
            if (PlaysBuffer(dir)) continue;
            using var session = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "session.json")));
            list.Add((r.GetProperty("name").GetString() ?? "", (long)session.RootElement.GetProperty("freq_hz").GetDouble()));
        }
        return list;
    }

    /// <summary>Whether the radio in this config folder plays another radio's I/Q buffer back.</summary>
    private static bool PlaysBuffer(string dir)
    {
        try
        {
            using var radio = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "radio.json")));
            return radio.RootElement.TryGetProperty("backend", out var b) && b.GetString() == "TimeShift";
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The app rebuilds its tree as it redraws, every hop of a scan included, so an element can
    /// vanish between being found and being used. Everything that can fail that way comes
    /// before the button goes down, and a press looks at the scanner's state first, so going
    /// round again can neither press twice nor switch anything back.
    /// </summary>
    private static string? PressSettled(AutomationElement main, int radio, byte cmd, long radioHz, bool restored, Outcome outcome)
    {
        for (int tries = 1; ; tries++)
        {
            try { return PressOne(main, radio, cmd, radioHz, restored, outcome); }
            catch (Exception e) when (tries < PressTries && IsTreeChange(e)) { Thread.Sleep(FrameMs); }
        }
    }

    private static string? PressOne(AutomationElement main, int radio, byte cmd, long radioHz, bool restored, Outcome outcome)
    {
        var windows = ScannerWindows(main);
        // Just restored, the app is still drawing, and floating windows come last. SCAN
        // toggles, so it must not be pressed on a window that is merely not there yet.
        for (int t = 0; restored && t < 10 && windows.Count < 2; t++)
        {
            Thread.Sleep(50);
            windows = ScannerWindows(main);
        }

        // Left to right is only the likeliest order: windows float.
        for (int i = 0; i < windows.Count; i++)
            if (OnWindow(windows[(i + radio) % windows.Count], cmd, radioHz, outcome, out var problem)) return problem;

        // Not open, so it comes up for this press and goes away again.
        if (!OpenWindow(main, radio, radioHz)) return "BRAK OKNA SKANERA";
        try
        {
            var until = Environment.TickCount64 + SettleMs;
            List<AutomationElement> now;
            while ((now = ScannerWindows(main)).Count <= windows.Count && Environment.TickCount64 < until) Thread.Sleep(30);
            foreach (var w in now)
                if (OnWindow(w, cmd, radioHz, outcome, out var problem)) return problem;
            return "BRAK OKNA SKANERA";
        }
        finally
        {
            CloseNew(main, windows);
        }
    }

    /// <summary>
    /// Closes whatever was not open before. Here and not next to the press, because the
    /// tree can change under a press too, and a retry would take a window left behind
    /// for one the user opened and leave it on screen.
    /// </summary>
    private static void CloseNew(AutomationElement main, List<AutomationElement> before)
    {
        var until = Environment.TickCount64 + SettleMs;
        while (Environment.TickCount64 < until)
        {
            Thread.Sleep(FrameMs);
            try
            {
                var extra = ScannerWindows(main).Where(w => !before.Any(o => Automation.Compare(o, w))).ToList();
                if (extra.Count == 0) return;
                extra.ForEach(Close);
            }
            catch (ElementNotAvailableException) { }
        }
    }

    /// <summary>Presses in this window if it is the radio's; false leaves another radio's window as it was.</summary>
    private static bool OnWindow(AutomationElement window, byte cmd, long radioHz, Outcome outcome, out string? problem)
    {
        problem = null;
        bool expanded = Expand(window, out var el);
        try
        {
            var kids = Children(el);
            int status = kids.FindIndex(k => StatusRx().IsMatch(Name(k)));
            if (radioHz != 0 && Math.Abs(WindowHz(kids, status) - radioHz) > SameRadioHz) return false;

            bool running = status >= 0;
            // Settled at the first look, so that going round again can't switch it back.
            if (cmd == ScanToggle) outcome.Toggled ??= running ? Proto.ScanCmdStop : Proto.ScanCmdStart;
            byte want = outcome.Toggled ?? cmd;
            AutomationElement? button = null;
            if ((want == Proto.ScanCmdStart && !running) || (want == Proto.ScanCmdStop && running))
                button = StartStop(kids);
            else if (cmd == Proto.ScanCmdNext && running)
                button = Next(kids, status);
            else
                return true;   // already in the state asked for

            // Not found where it belongs: the list was moving. The next look finds it.
            if (button is null) throw new ElementNotAvailableException();
            problem = Invoke(button, "START/STOP/NEXT");
            outcome.Pressed |= problem is null;
            return true;
        }
        finally
        {
            if (expanded)
            {
                Thread.Sleep(FrameMs);
                Collapse(el);
                Thread.Sleep(FrameMs);
            }
        }
    }

    /// <summary>
    /// The app's own window. Not Process.MainWindowHandle: Oxide is a console program,
    /// so that is its console.
    /// </summary>
    private static IntPtr MainWindow() => AppWindows("Window Class").FirstOrDefault();

    private static List<IntPtr> AppWindows(params string[] classes)
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("sdroxide"))
            using (p) pids.Add((uint)p.Id);
        var found = new List<IntPtr>();
        var cls = new StringBuilder(32);
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            GetClassName(h, cls, cls.Capacity);
            if (classes.Contains(cls.ToString())) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static List<AutomationElement> ScannerWindows(AutomationElement main) =>
        Children(main).Where(k => Name(k) == WindowName)
                      .OrderBy(k => Box(k).X).ToList();

    /// <summary>
    /// SCAN is five to the left of HELP, and HELP ends the last row of buttons before the
    /// display, whatever notices stand between them. The pane is the radio's if its dial is
    /// on the same band. False when no pane shows this radio.
    /// </summary>
    private static bool OpenWindow(AutomationElement main, int radio, long radioHz)
    {
        var kids = Children(main);
        int pane = 0, from = 0;
        for (int i = 0; i < kids.Count; i++)
        {
            if (!IsPaneEnd(kids[i])) continue;
            bool mine = radioHz != 0 ? Math.Abs(DialHz(kids, from, i) - radioHz) <= SameRadioHz : pane == radio;
            if (mine)
            {
                int help = i - 1;
                while (help > from && Math.Abs(Box(kids[help]).Height - 26) >= 2) help--;
                bool sameRow = help - 5 >= from && Math.Abs(Box(kids[help - 5]).Y - Box(kids[help]).Y) < 4;
                return sameRow && Invoke(kids[help - 5], "SCAN") is null;
            }
            pane++;
            from = i;
        }
        return false;
    }

    /// <summary>The large digits of a pane's dial, one element each.</summary>
    private static long DialHz(List<AutomationElement> kids, int from, int to)
    {
        var digits = new StringBuilder();
        for (int i = from; i < to; i++)
            if (IsSize(kids[i], 22, 45)) digits.Append(Name(kids[i]).Where(char.IsDigit).ToArray());
        return long.TryParse(digits.ToString(), out long hz) ? hz : 0;
    }

    /// <summary>
    /// Shows the window, which can be newly opened and still filling. Returns true when
    /// this call expanded it; <paramref name="live"/> is its element afterwards.
    /// </summary>
    private static bool Expand(AutomationElement window, out AutomationElement live)
    {
        live = window;
        var show = Find(window, "Show");
        if (show is not null) Invoke(show, "Show");
        var until = Environment.TickCount64 + SettleMs;
        while (Environment.TickCount64 < until &&
               !(Find(window, "Hide") is not null && Children(window).Count > 20))
            Thread.Sleep(30);
        return show is not null;
    }

    private static void Collapse(AutomationElement window) => PressByName(window, "Hide");

    private static void Close(AutomationElement window) => PressByName(window, "Close window");

    /// <summary>Tidying up after a press that has already happened: it must not fail the press.</summary>
    private static void PressByName(AutomationElement window, string name)
    {
        for (int tries = 0; tries < WalkTries; tries++)
        {
            try
            {
                var button = Find(window, name);
                if (button is not null) Invoke(button, name);
                return;
            }
            catch (Exception e) when (IsTreeChange(e)) { Thread.Sleep(FrameMs / 2); }
        }
    }

    private static bool IsTreeChange(Exception e) =>
        e is ElementNotAvailableException or InvalidOperationException or TimeoutException or COMException;

    private static string Name(AutomationElement el)
    {
        try { return el.Current.Name; }
        catch (ElementNotAvailableException) { return ""; }
    }

    private static System.Windows.Rect Box(AutomationElement el)
    {
        try { return el.Current.BoundingRectangle; }
        catch (ElementNotAvailableException) { return System.Windows.Rect.Empty; }
    }

    /// <summary>The third button of the top row, after MEM and RANGE, at the far right.</summary>
    private static AutomationElement? StartStop(List<AutomationElement> kids)
    {
        if (kids.Count < 6) return null;
        var (mem, range, b) = (Box(kids[3]), Box(kids[4]), Box(kids[5]));
        bool sameRow = Math.Abs(mem.Y - b.Y) < 4 && Math.Abs(range.Y - b.Y) < 4;
        return sameRow && b.X > range.Right + 100 && b.Width is > 40 and < 80 ? kids[5] : null;
    }

    /// <summary>The status line is followed by SKIP and NEXT, in that order, and NEXT is the wider one on the left.</summary>
    private static AutomationElement? Next(List<AutomationElement> kids, int status)
    {
        if (status + 2 >= kids.Count) return null;
        var (skip, next) = (kids[status + 1], kids[status + 2]);
        var (s, n) = (Box(skip), Box(next));
        return Math.Abs(s.Width - 47) < 2 && Math.Abs(n.Width - 51) < 2 && n.Right <= s.X && Math.Abs(n.Y - s.Y) < 4 ? next : null;
    }

    /// <summary>What says whose window this is: the frequency it is on, or with the scanner off, the first channel on its list.</summary>
    private static long WindowHz(List<AutomationElement> kids, int status)
    {
        string? mhz = status >= 0
            ? StatusRx().Match(Name(kids[status])).Groups[2].Value
            : kids.Select(k => ChannelRx().Match(Name(k))).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        return mhz is null ? 0 : (long)(double.Parse(mhz, CultureInfo.InvariantCulture) * 1e6);
    }

    private static string? Invoke(AutomationElement? el, string what)
    {
        if (el is null) return "ZMIENIL SIE UKLAD OKNA";
        ((InvokePattern)el.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        return null;
    }

    private static bool IsSize(AutomationElement el, double w, double h)
    {
        var r = Box(el);
        return Math.Abs(r.Width - w) < 2 && Math.Abs(r.Height - h) < 2;
    }

    private static AutomationElement? Find(AutomationElement parent, string name) =>
        Children(parent).FirstOrDefault(k => Name(k) == name);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientPoint { public int X, Y; }

    // A 64-bit INPUT left all zero, which makes it a mouse event with no movement and no buttons.
    [StructLayout(LayoutKind.Sequential, Size = 40)]
    private struct EmptyInput { public int Type; }

    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref ClientPoint point);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, ref EmptyInput input, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>A walk that loses its place because the tree changed under it starts again.</summary>
    private static List<AutomationElement> Children(AutomationElement parent)
    {
        for (int tries = 1; ; tries++)
        {
            try
            {
                var list = new List<AutomationElement>();
                for (var k = Walker.GetFirstChild(parent); k is not null; k = Walker.GetNextSibling(k))
                    list.Add(k);
                return list;
            }
            catch (ElementNotAvailableException) when (tries < WalkTries) { Thread.Sleep(FrameMs / 4); }
        }
    }
}
