namespace CydMonitor;

/// <summary>
/// A scaled 320x240 BGRA view of one monitor.
///
/// Two implementations exist because the obvious one turned out not to be fast
/// enough. <see cref="DuplicationSource"/> is the real path; <see
/// cref="ScreenSource"/> is the GDI fallback for sessions where Desktop
/// Duplication is unavailable, and is measurably capped near 30 fps with no
/// budget left over — see the note in CydMonitor.csproj.
/// </summary>
internal interface IFrameSource : IDisposable
{
    /// <summary>Pixel size of the pointer drawn into the frame; 0 disables it.</summary>
    int CursorSize { get; set; }

    /// <summary>
    /// How long a capture may wait for the compositor to produce a frame. The
    /// live loop paces itself and so passes 0 — a miss just becomes a skipped
    /// frame. Measurement code raises it, because an idle desktop legitimately
    /// produces nothing and a benchmark that recorded that as a failure would
    /// be measuring its own impatience.
    /// </summary>
    int AcquireTimeoutMs { get; set; }

    /// <summary>
    /// Milliseconds the last capture spent doing work, excluding any time spent
    /// waiting for the compositor to produce a frame. Only this counts against
    /// the frame budget — waiting happens while the desktop is idle, when there
    /// is by definition nothing to send. For the GDI path the two are
    /// inseparable, because the block happens inside BitBlt itself, so it
    /// reports the whole call.
    /// </summary>
    double LastWorkMs { get; }

    /// <summary>Milliseconds the last capture spent purely waiting; 0 for the GDI path.</summary>
    double LastWaitMs { get; }

    /// <summary>Human-readable name of the capture path, for the startup banner.</summary>
    string Kind { get; }

    /// <summary>
    /// Produces the next frame. Returns false when there is nothing new to send
    /// — for Desktop Duplication that is the common case on an idle desktop and
    /// is not an error.
    /// </summary>
    bool Capture(Native.RECT monitor, ScaleMode mode);

    /// <summary>BGRA, 320x240, top row first. Valid after a successful capture.</summary>
    unsafe uint* Pixels { get; }

    /// <summary>Desktop rectangle the last frame was taken from.</summary>
    Native.RECT LastSrc { get; }

    /// <summary>Where in the 320x240 panel it landed; smaller than the panel when letterboxed.</summary>
    Native.RECT LastDst { get; }
}

internal static class FrameSourceFactory
{
    /// <summary>
    /// Prefers Desktop Duplication and falls back to GDI if it cannot be set
    /// up. The fallback is reported rather than taken silently, because the two
    /// paths differ by roughly a factor of two in achievable frame rate and a
    /// user chasing missing frames needs to know which one is running.
    /// </summary>
    public static IFrameSource Create(string deviceName, bool forceGdi)
    {
        if (!forceGdi)
        {
            try
            {
                return new DuplicationSource(deviceName);
            }
            catch (Exception e)
            {
                Console.WriteLine($"  Desktop Duplication niedostepne ({e.GetType().Name}: {e.Message})");
                Console.WriteLine("  przechodzę na GDI — spodziewaj sie ~25-30 fps zamiast 30+");
            }
        }
        return new ScreenSource();
    }
}
