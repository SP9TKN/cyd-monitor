using System.Buffers.Binary;
using System.Numerics;

namespace CydMonitor;

/// <summary>
/// Turns the difference between two frames into strips on the wire.
///
/// This class is the reason the 30 fps target is reachable. A raw 320x240
/// RGB565 frame is 153.6 KB; at 30 fps that is 37 Mbit/s, which an ESP32's WiFi
/// will not sustain. Two reductions bring it inside the link's budget:
///
///  * Only changed 16x16 tiles are sent, coalesced along each tile row into
///    strips so a wide change costs one address-window setup on the panel
///    instead of twenty. On an idle desktop this alone takes a frame to nearly
///    nothing.
///  * Each strip is encoded in whichever of five schemes comes out smallest.
///    This matters precisely in the worst case the first reduction doesn't
///    help with — a full repaint. Desktop pixels are overwhelmingly flat
///    colour: window chrome is one or two colours per tile, text is two, and
///    both collapse to a bitmask over a tiny palette. A full-screen repaint
///    that would be 153.6 KB raw typically leaves here at 25-40 KB, which is
///    what keeps a worst-case frame inside 30 ms rather than 130.
///
/// Photographic content (a full-screen photo, a video) has no such structure
/// and would fall through to ENC_RAW — measured at ~420 KB/s of link against
/// 153.6 KB per full screen, under three screens a second. Those strips go
/// out as JPEG instead (see <see cref="Jpeg"/>), at roughly a tenth of the
/// bytes; the loss is confined to content that is moving, and made good once
/// it stops (see the refine note in Encode).
/// </summary>
/// <summary>One strip of an encoded frame as a UDP rectangle: tile row/column/width, mode, body location.</summary>
internal readonly record struct StripRef(int Tx, int Ty, int Tw, byte Mode, int Offset, int Length);

internal sealed class TileEncoder
{
    private const int MaxStripPx = Proto.W * Proto.TileH;

    private readonly byte[]   _out  = new byte[256 * 1024];
    private readonly ushort[] _tmp  = new ushort[MaxStripPx];
    private readonly ushort[] _pal  = new ushort[17];
    private readonly bool[]   _dirty = new bool[Proto.TilesX];
    private readonly bool[]   _motion = new bool[Proto.TilesX];

    // Per tile: whether what the device shows there came from a JPEG (and so
    // is only approximately the source), and how many consecutive frames the
    // source has held still. Together they decide when a lossy tile gets
    // re-sent exactly — see the refine note in Encode.
    private readonly bool[] _lossy = new bool[Proto.TilesX * Proto.TilesY];
    private readonly byte[] _still = new byte[Proto.TilesX * Proto.TilesY];
    // One bit per recent frame, newest lowest: did this tile change? The
    // basis for telling motion (video, a waterfall) from content that merely
    // updates now and then (a meter, a clock, a readout).
    private readonly byte[] _history = new byte[Proto.TilesX * Proto.TilesY];
    private bool _lastWasJpeg;
    private byte[] _soft = [];   // this frame's pre-sharpen 8-bit pixels, for JPEG

    // ~0.5 s at the usual rate: long enough to be past the gaps between
    // frames of a video running slower than capture, short enough that a
    // paused picture sharpens before anyone looks for the blur.
    private const int RefineAfterFrames = 12;

    // Below this a lossless mode is already cheap, and JPEG's fixed per-block
    // cost plus its loss of fidelity can't pay for themselves.
    private const int JpegMinLossless = 2048;

    // Changed in at least this many of the last 8 frames to count as moving.
    private const int MotionMinFrames = 3;

    private readonly bool[] _changedAll = new bool[Proto.TilesX * Proto.TilesY];

    // UDP data plane (protocol v5). A tile forced here goes out on the next Encode
    // even if it still equals prev: that is how a datagram the device never
    // acknowledged is made good - with the tile's *current* content, not a replay.
    private readonly bool[] _forceTile = new bool[Proto.TilesX * Proto.TilesY];

    /// <summary>
    /// Largest encoded payload one strip may carry, in bytes; 0 = unlimited (TCP).
    /// A strip is one datagram's rectangle on the UDP path, so it has to fit: an
    /// over-long run of tiles is split in halves until each half does.
    /// </summary>
    public int SplitLimit { get; set; }

    /// <summary>Widest strip, in tiles. Bounds the device's decode buffer on the UDP path.</summary>
    public int MaxRunTiles { get; set; } = Proto.TilesX;

    /// <summary>Marks a tile to be sent on the next <see cref="Encode"/> whatever it compares to.</summary>
    public void ForceTile(int t) { if ((uint)t < (uint)_forceTile.Length) _forceTile[t] = true; }

    /// <summary>Marks every tile of one tile row (the periodic sweep that heals any divergence nothing else noticed).</summary>
    public void ForceRow(int ty)
    {
        if ((uint)ty >= Proto.TilesY) return;
        for (int tx = 0; tx < Proto.TilesX; tx++) _forceTile[ty * Proto.TilesX + tx] = true;
    }

    // JPEG is only used at all while a large share of the screen is moving —
    // a film full-screen or in a big window. Below that everything goes
    // lossless and keeps the user's sharpening: an SDR waterfall, spectrum and
    // meters, or an APRS packet list, looked washed out and lost contrast as
    // unsharpened 4:2:0 JPEG, and had nothing to gain from it. Entry and exit
    // thresholds differ, and each switch must hold for a run of frames, so a
    // scene cut or a paused frame doesn't drop a film out of video mode and a
    // burst of scrolling doesn't pull a desktop into it.
    private const int VideoEnterTiles = Proto.TilesX * Proto.TilesY * 40 / 100;
    private const int VideoExitTiles  = Proto.TilesX * Proto.TilesY * 25 / 100;
    private const int VideoEnterFrames = 8;    // ~0.3 s at 25-30 fps
    private const int VideoExitFrames  = 15;   // ~0.5 s
    private bool _videoMode;
    private int  _videoVotes;

    /// <summary>True while JPEG is in use for moving content; for the diagnostics line.</summary>
    public bool VideoMode => _videoMode;

    private void UpdateVideoMode(int movingTiles)
    {
        bool want = _videoMode ? movingTiles >= VideoExitTiles : movingTiles >= VideoEnterTiles;
        if (want == _videoMode) { _videoVotes = 0; return; }
        if (++_videoVotes >= (want ? VideoEnterFrames : VideoExitFrames))
        {
            _videoMode = want;
            _videoVotes = 0;
        }
    }

    /// <summary>Lossy coder for photographic strips; null keeps everything lossless.</summary>
    public JpegEncoder? Jpeg { get; set; }

    /// <summary>JPEG strips in the last <see cref="Encode"/>, for the diagnostics line.</summary>
    public int JpegCount { get; private set; }

    public int StripCount { get; private set; }
    public int PayloadLength { get; private set; }
    public ReadOnlySpan<byte> Payload => _out.AsSpan(0, PayloadLength);

    /// <summary>Counters for the diagnostics line; purely informational.</summary>
    public long TotalStrips { get; private set; }
    public long ModeCounts0 { get; private set; }
    public long ModeCountsRaw { get; private set; }

    /// <summary>
    /// Encodes <paramref name="cur"/> against <paramref name="prev"/> and
    /// updates <paramref name="prev"/> to match for every tile it emitted.
    ///
    /// The caller must not call this for a frame it is not going to send:
    /// <paramref name="prev"/> tracks what the device has been *told*, so
    /// updating it for a frame that gets dropped would leave those tiles
    /// permanently stale. Skipping the call instead is harmless — the next
    /// frame's diff simply covers both changes.
    /// </summary>
    /// <summary>
    /// Most bytes this may put on the wire in one frame. A full repaint is
    /// roughly 36 KB and used to go out as a single burst of ~15 full-width
    /// strips; the device only has four staging buffers (CYD_STRIP_BUFFERS),
    /// so it ran out, dropped the strips it had nowhere to put, and asked for
    /// a full refresh — which is the largest frame there is, so it ran out
    /// again. Measured on hardware as a repaint that never completed: the
    /// dropped strips left permanently unpainted regions, because prev has
    /// already been updated for them and nothing marks them dirty again.
    /// Capping the frame here breaks that loop at the only place that can:
    /// tiles past the cap keep their old prev, stay dirty, and are picked up
    /// by the next frame, so a big repaint arrives over a few frames instead
    /// of failing as one.
    /// </summary>
    public int MaxFrameBytes { get; set; } = 16 * 1024;

    /// <summary>
    /// Tile row a capped full repaint still has to reach, or -1 when no
    /// repaint is outstanding. A forced repaint cannot simply be dropped
    /// when <see cref="MaxFrameBytes"/> cuts the frame short: the rows left
    /// over are only re-sent if they look different from prev, and the whole
    /// point of forcing is that they may not — a black region against a
    /// freshly-zeroed prev compares equal, so it would never be sent and the
    /// device would keep whatever was on those pixels before. Remembering
    /// where the repaint stopped is what makes "force" mean the whole frame
    /// rather than the first 16 KB of it.
    /// </summary>
    private int _forceResumeRow = -1;

    /// <summary>
    /// Tile row an ordinary (unforced) scan should begin at, so a frame
    /// budget exhausted part way down doesn't hand the next frame's budget
    /// straight back to the rows that just used it. See the note in Encode.
    /// </summary>
    private int _scanStartRow;

    /// <summary>
    /// True while a forced repaint has been cut short by <see
    /// cref="MaxFrameBytes"/> and still owes the device the rows below where
    /// it stopped. The frame loop uses this to deliver the remainder without
    /// waiting out its usual send interval between each piece: the cap exists
    /// to protect the device's staging buffers from one oversized frame, not
    /// to spread a repaint over wall-clock time, and pacing the pieces is
    /// what made a fresh connection visibly crawl into view instead of
    /// snapping in.
    /// </summary>
    public bool RepaintPending => _forceResumeRow >= 0;

    /// <param name="softRgb">The same frame before sharpening, as 8-bit R,G,B (see FrameProcessor.SoftRgb); JPEG strips are coded from this.</param>
    public void Encode(ushort[] cur, ushort[] prev, bool forceFull, byte[] softRgb)
    {
        int p = 0;
        StripCount = 0;
        JpegCount = 0;
        _soft = softRgb;

        if (forceFull) { _forceResumeRow = 0; _scanStartRow = 0; }

        // Change detection for every tile up front, rather than row by row as
        // the scan reaches them, so the video decision below sees the whole
        // screen — the byte budget often stops the scan part way down.
        int moving = 0;
        for (int t = 0; t < _changedAll.Length; t++)
        {
            bool changed = TileDiffers(cur, prev, t % Proto.TilesX, (t / Proto.TilesX) * Proto.TileH);
            _changedAll[t] = changed;
            if (changed) _still[t] = 0;
            else if (_still[t] < byte.MaxValue) _still[t]++;
            _history[t] = (byte)((_history[t] << 1) | (changed ? 1 : 0));
            if (BitOperations.PopCount(_history[t]) >= MotionMinFrames) moving++;
        }
        UpdateVideoMode(moving);

        for (int k = 0; k < Proto.TilesY; k++)
        {
            // Ordinary frames resume the scan where the byte budget cut the
            // last one off, wrapping around, instead of restarting at the
            // top every time. With content that dirties the whole screen at
            // once — a video is the obvious case — always starting at row 0
            // meant the upper rows consumed the whole budget, were dirty
            // again by the next frame, and consumed it again: the lower part
            // of the screen could go unsent indefinitely while the top
            // updated, which reads as heavy stutter rather than as the
            // bandwidth limit it actually is. Rotating the starting row
            // spends the same bytes but spreads them over the whole picture.
            // A forced repaint keeps its own strictly top-down order, since
            // _forceResumeRow's contract is "everything below here still
            // owes an unconditional repaint".
            int ty = _forceResumeRow >= 0 ? k : (_scanStartRow + k) % Proto.TilesY;
            int y0 = ty * Proto.TileH;
            bool forceRow = _forceResumeRow >= 0 && ty >= _forceResumeRow;

            for (int tx = 0; tx < Proto.TilesX; tx++)
            {
                int t = ty * Proto.TilesX + tx;
                bool changed = _changedAll[t];
                // A tile last sent as JPEG is only an approximation of what is
                // on screen. Once it has held still long enough that it is no
                // longer part of something moving, send it again losslessly:
                // otherwise a paused video, or a page that was scrolled through
                // and then left, would stay slightly blurred for as long as it
                // stayed put. The wait matters — video often runs slower than
                // the capture rate, so plenty of tiles sit unchanged for a
                // frame or two between video frames, and refining those would
                // send the whole picture raw every other frame.
                bool refine = _lossy[t] && _still[t] >= RefineAfterFrames;
                _dirty[tx] = forceRow || changed || refine || _forceTile[t];
                // Lossy only in video mode, and there for any tile that changed
                // this frame. A stricter per-tile rule (changing in several of
                // the last few frames) was tried to stop an SDR window's meters
                // and readouts flickering between JPEG and lossless — but with
                // the frame-level video mode that desktop never gets JPEG at
                // all, and inside a film the per-tile rule backfired: tiles in
                // dark or still parts of the picture missed it, chopped strips
                // into pieces too narrow for JPEG, and sent them raw — measured
                // as 17-45% raw strips and the frame rate swinging 7-25 fps,
                // against 27-28 fps with this simpler rule.
                _motion[tx] = _videoMode && changed;
            }

            // Runs of dirty tiles, broken wherever the motion mode changes, so
            // a static label beside a waterfall never gets pulled into a JPEG
            // strip and a moving strip never gets forced lossless by a
            // neighbour that isn't.
            int run = 0;
            bool runMotion = false;
            for (int tx = 0; tx <= Proto.TilesX; tx++)
            {
                bool d = tx < Proto.TilesX && _dirty[tx];
                bool m = d && _motion[tx];
                if (d && (run == 0 || m == runMotion))
                {
                    if (run == 0) runMotion = m;
                    run++;
                    continue;
                }
                if (run == 0) continue;

                p = EmitRun(cur, prev, tx - run, run, ty, p, runMotion);

                // This tile ended the run by being dirty in the other mode:
                // it starts the next one rather than being skipped.
                run = d ? 1 : 0;
                runMotion = m;

                // Checked after emitting rather than before: a strip's encoded
                // size isn't known until it has been written, and stopping
                // mid-strip would put a half-written payload on the wire.
                if (p >= MaxFrameBytes)
                {
                    // Resume at this row, not the next one: the row may be
                    // only half emitted, and re-sending the part that already
                    // went out costs one duplicate strip, where skipping the
                    // rest would leave a hole.
                    if (_forceResumeRow >= 0) _forceResumeRow = ty;
                    // Next frame picks up from the row that ran out of
                    // budget, so every band gets its turn at the wire.
                    _scanStartRow = ty;
                    PayloadLength = p;
                    return;
                }
            }
        }

        // Reached the bottom without being cut short, so any outstanding
        // repaint is now complete and every dirty tile went out — where the
        // next scan starts no longer matters.
        _forceResumeRow = -1;
        _scanStartRow = 0;
        PayloadLength = p;
    }

    /// <summary>
    /// Emits <paramref name="n"/> consecutive dirty tiles of row <paramref name="ty"/> as
    /// one strip, or - when <see cref="MaxRunTiles"/> or <see cref="SplitLimit"/> say it is
    /// too wide or too long for one UDP datagram - as several. Owns the per-strip
    /// bookkeeping (lossy flags, forced flags, strip count), so a split stays consistent.
    /// </summary>
    private int EmitRun(ushort[] cur, ushort[] prev, int tx0, int n, int ty, int p, bool allowJpeg)
    {
        int rowBase = ty * Proto.TilesX;
        while (n > MaxRunTiles)
        {
            p = EmitRun(cur, prev, tx0, MaxRunTiles, ty, p, allowJpeg);
            tx0 += MaxRunTiles;
            n -= MaxRunTiles;
        }

        bool wasLossy = false;
        for (int i = 0; i < n; i++) wasLossy |= _lossy[rowBase + tx0 + i];

        int start = p;
        p = EmitStrip(cur, prev, tx0 * Proto.TileW, ty * Proto.TileH, n * Proto.TileW, Proto.TileH, p, allowJpeg, wasLossy);

        if (SplitLimit > 0 && n > 1 && p - start - Proto.StripHeaderBytes > SplitLimit)
        {
            // Too long for one datagram. Discard the attempt (prev already holds these
            // tiles' current content, so emitting the halves is harmless) and go again
            // with two narrower strips. A single tile always fits: 512 bytes raw.
            int left = n / 2;
            p = EmitRun(cur, prev, tx0, left, ty, start, allowJpeg);
            return EmitRun(cur, prev, tx0 + left, n - left, ty, p, allowJpeg);
        }

        for (int i = 0; i < n; i++)
        {
            _lossy[rowBase + tx0 + i] = _lastWasJpeg;
            _forceTile[rowBase + tx0 + i] = false;
        }
        StripCount++;
        return p;
    }

    /// <summary>
    /// Describes the strips of the last <see cref="Encode"/> as UDP rectangles:
    /// tile coordinates, mode, and where the body sits inside <see cref="Payload"/>.
    /// </summary>
    public void GetStrips(List<StripRef> into)
    {
        into.Clear();
        var payload = Payload;
        int p = 0;
        for (int s = 0; s < StripCount; s++)
        {
            int x = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(p, 2));
            int y = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(p + 2, 2));
            int w = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(p + 4, 2));
            byte mode = payload[p + 8];
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(p + 9, 4));
            into.Add(new StripRef(x / Proto.TileW, y / Proto.TileH, w / Proto.TileW, mode, p + Proto.StripHeaderBytes, len));
            p += Proto.StripHeaderBytes + len;
        }
    }

    private static bool TileDiffers(ushort[] cur, ushort[] prev, int tx, int y0)
    {
        int x0 = tx * Proto.TileW;
        for (int y = 0; y < Proto.TileH; y++)
        {
            int i = (y0 + y) * Proto.W + x0;
            // Explicit loop with an early exit rather than SequenceEqual: these
            // spans are 32 bytes, short enough that the vectorised path's setup
            // costs more than it saves, and most tiles differ in their first
            // few pixels or not at all.
            for (int x = 0; x < Proto.TileW; x++)
                if (cur[i + x] != prev[i + x]) return true;
        }
        return false;
    }

    private int EmitStrip(ushort[] cur, ushort[] prev, int x, int y, int w, int h, int p, bool allowJpeg, bool wasLossy)
    {
        int px = w * h;

        // Gather to a contiguous buffer so every encoder below is a flat scan,
        // and copy into prev in the same pass — this strip is going out, so it
        // becomes what the device is known to be showing.
        for (int row = 0; row < h; row++)
        {
            int src = (y + row) * Proto.W + x;
            Array.Copy(cur, src, _tmp, row * w, w);
            Array.Copy(cur, src, prev, src, w);
        }

        var span = _tmp.AsSpan(0, px);
        Analyse(span, out bool allEqual, out int nPal, out int runs);

        int solidSize = allEqual ? 2 : int.MaxValue;
        int pal1Size  = nPal is > 0 and <= 2 ? 4 + ((px + 7) >> 3) : int.MaxValue;
        int pal4Size  = nPal is > 0 and <= 16 ? 1 + nPal * 2 + ((px + 1) >> 1) : int.MaxValue;
        int rleSize   = runs * 3;
        int rawSize   = px * 2;

        int best = Math.Min(Math.Min(solidSize, pal1Size), Math.Min(Math.Min(pal4Size, rleSize), rawSize));

        byte mode;
        int  bodyStart = p + Proto.StripHeaderBytes;
        int  bodyLen;

        // JPEG only for strips of tiles in motion (see the note in Encode),
        // and only where it beats every lossless mode — flat UI, text and
        // runs come out small above even while they move, so a dragged window
        // stays sharp. The bar to switch into JPEG is a clear win; the bar to
        // stay in it, for tiles already going out as JPEG, is only not losing
        // much. Without that gap, content whose sizes sit near each other
        // alternated between the two frame to frame, and the soft/sharp
        // difference between them read as flicker. The cap handed to the
        // encoder also lets it give up part way through instead of finishing
        // a scan that was never going to be chosen.
        int jpegLen = -1;
        if (allowJpeg && Jpeg is not null && best >= JpegMinLossless)
        {
            int cap = wasLossy ? best * 12 / 10 : best * 8 / 10;
            jpegLen = Jpeg.EncodeScan(_soft, Proto.W * 3, x, y, w, h, _out, bodyStart, Math.Min(Proto.JpegMaxScan, cap));
        }
        _lastWasJpeg = jpegLen > 0;

        if (_lastWasJpeg)           { mode = Proto.EncJpeg;  bodyLen = jpegLen; JpegCount++; }
        else if (best == solidSize) { mode = Proto.EncSolid; bodyLen = WriteSolid(span, bodyStart); }
        else if (best == pal1Size)  { mode = Proto.EncPal1;  bodyLen = WritePal1(span, nPal, bodyStart); }
        else if (best == pal4Size)  { mode = Proto.EncPal4;  bodyLen = WritePal4(span, nPal, bodyStart); }
        else if (best == rleSize)   { mode = Proto.EncRle;   bodyLen = WriteRle(span, bodyStart); }
        else                        { mode = Proto.EncRaw;   bodyLen = WriteRaw(span, bodyStart); }

        var hdr = _out.AsSpan(p, Proto.StripHeaderBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr[..2], (ushort)x);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr[2..4], (ushort)y);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr[4..6], (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr[6..8], (ushort)h);
        hdr[8] = mode;
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[9..13], (uint)bodyLen);

        TotalStrips++;
        if (mode == Proto.EncRaw) ModeCountsRaw++;
        if (mode == Proto.EncSolid) ModeCounts0++;

        return bodyStart + bodyLen;
    }

    /// <summary>
    /// One pass that gathers everything the size estimates need: whether the
    /// strip is flat, how many distinct colours it holds (abandoned past 16,
    /// where the palette modes stop being applicable), and how many runs it
    /// would take. Doing this once and writing only the winner avoids encoding
    /// the same strip five times to compare.
    /// </summary>
    private void Analyse(ReadOnlySpan<ushort> px, out bool allEqual, out int nPal, out int runs)
    {
        ushort first = px[0];
        allEqual = true;
        int n = 0;
        runs = 0;

        ushort runColour = first;
        int runLen = 0;

        for (int i = 0; i < px.Length; i++)
        {
            ushort c = px[i];
            if (c != first) allEqual = false;

            if (n >= 0)
            {
                bool found = false;
                for (int k = 0; k < n; k++)
                    if (_pal[k] == c) { found = true; break; }
                if (!found)
                {
                    if (n == 16) n = -1;        // too many; palette modes are out
                    else _pal[n++] = c;
                }
            }

            if (i == 0) { runColour = c; runLen = 1; }
            else if (c == runColour && runLen < 255) runLen++;
            else
            {
                runs++;
                runColour = c;
                runLen = 1;
            }
        }
        if (runLen > 0) runs++;
        nPal = n < 0 ? 0 : n;   // 0 means "not usable", callers test for > 0
    }

    private int WriteSolid(ReadOnlySpan<ushort> px, int at)
    {
        // Stored values are already byte-swapped, so writing them
        // little-endian emits the panel's native order. Same trick throughout.
        BinaryPrimitives.WriteUInt16LittleEndian(_out.AsSpan(at, 2), px[0]);
        return 2;
    }

    private int WriteRaw(ReadOnlySpan<ushort> px, int at)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(px);
        bytes.CopyTo(_out.AsSpan(at, bytes.Length));
        return bytes.Length;
    }

    private int WriteRle(ReadOnlySpan<ushort> px, int at)
    {
        int p = at;
        int i = 0;
        while (i < px.Length)
        {
            ushort c = px[i];
            int n = 1;
            while (i + n < px.Length && px[i + n] == c && n < 255) n++;
            _out[p++] = (byte)n;
            BinaryPrimitives.WriteUInt16LittleEndian(_out.AsSpan(p, 2), c);
            p += 2;
            i += n;
        }
        return p - at;
    }

    private int WritePal1(ReadOnlySpan<ushort> px, int nPal, int at)
    {
        int p = at;
        // Always emit two entries even for a single-colour palette, so the
        // decoder's index is unconditionally valid.
        BinaryPrimitives.WriteUInt16LittleEndian(_out.AsSpan(p, 2), _pal[0]); p += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(_out.AsSpan(p, 2), nPal > 1 ? _pal[1] : _pal[0]); p += 2;

        int bytes = (px.Length + 7) >> 3;
        _out.AsSpan(p, bytes).Clear();
        for (int i = 0; i < px.Length; i++)
            if (nPal > 1 && px[i] == _pal[1])
                _out[p + (i >> 3)] |= (byte)(0x80 >> (i & 7));
        return (p - at) + bytes;
    }

    private int WritePal4(ReadOnlySpan<ushort> px, int nPal, int at)
    {
        int p = at;
        _out[p++] = (byte)nPal;
        for (int k = 0; k < nPal; k++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_out.AsSpan(p, 2), _pal[k]);
            p += 2;
        }

        int bytes = (px.Length + 1) >> 1;
        _out.AsSpan(p, bytes).Clear();
        for (int i = 0; i < px.Length; i++)
        {
            int idx = 0;
            for (int k = 0; k < nPal; k++)
                if (_pal[k] == px[i]) { idx = k; break; }
            if ((i & 1) == 0) _out[p + (i >> 1)] |= (byte)(idx << 4);
            else              _out[p + (i >> 1)] |= (byte)idx;
        }
        return (p - at) + bytes;
    }
}
