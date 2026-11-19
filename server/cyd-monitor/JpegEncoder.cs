using System.Buffers.Binary;

namespace CydMonitor;

/// <summary>
/// Baseline JPEG encoder, specialised for one job: strips of photographic
/// content (video, photos) that no lossless mode in <see cref="TileEncoder"/>
/// can shrink. Measured on this link, a full-screen video frame costs ~154 KB
/// raw against a ceiling of ~420 KB/s — under three full screens a second —
/// while the same frame as JPEG is on the order of a tenth of that.
///
/// Fixed to 4:2:0 with the ITU T.81 Annex K tables, which is what makes two
/// things possible:
///  * Every strip is exactly one MCU (16 px) tall and a whole number of MCUs
///    wide, because strips are built from 16x16 tiles — no edge padding, ever.
///  * The quantisation and Huffman tables depend on nothing but the quality,
///    so they cross the wire once per session (<see cref="Tables"/>, sent as
///    MSG_JPEGTABLES) and each strip carries only its entropy-coded scan. A
///    self-contained JPEG header is ~590 bytes, which against the few hundred
///    bytes a strip's scan typically takes would nearly double every one of
///    them. The device rebuilds the header in front of each scan.
/// </summary>
internal sealed class JpegEncoder
{
    // ITU T.81 Annex K.1, natural (row-major) order.
    private static readonly byte[] StdLumQ =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly byte[] StdChromQ =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    // ZigZag[k] = natural index of the k-th coefficient in zigzag order.
    private static readonly byte[] ZigZag =
    [
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // ITU T.81 Annex K.3: code-length counts for lengths 1..16, then symbols.
    private static readonly byte[] DcLumBits  = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcChrBits  = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] DcVals     = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] AcLumBits  = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
    private static readonly byte[] AcLumVals  =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    private static readonly byte[] AcChrBits  = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static readonly byte[] AcChrVals  =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa,
    ];

    // AAN scale factors (jfdctflt); folded into the quantisation divisors so
    // the transform itself needs no per-coefficient multiply.
    private static readonly float[] Aan =
        [1.0f, 1.387039845f, 1.306562965f, 1.175875602f, 1.0f, 0.785694958f, 0.541196100f, 0.275899379f];

    private readonly byte[] _qLum = new byte[64];
    private readonly byte[] _qChr = new byte[64];
    private readonly float[] _divLum = new float[64];
    private readonly float[] _divChr = new float[64];

    private readonly ushort[] _dcLumCode = new ushort[256], _dcChrCode = new ushort[256];
    private readonly byte[]   _dcLumLen  = new byte[256],   _dcChrLen  = new byte[256];
    private readonly ushort[] _acLumCode = new ushort[256], _acChrCode = new ushort[256];
    private readonly byte[]   _acLumLen  = new byte[256],   _acChrLen  = new byte[256];

    // Per-MCU working storage: 16x16 of Y, 8x8 each of Cb/Cr, one block to
    // transform, and the quantised block in zigzag order.
    private readonly float[] _y  = new float[256];
    private readonly float[] _cb = new float[64];
    private readonly float[] _cr = new float[64];
    private readonly float[] _blk = new float[64];
    private readonly int[]   _zz  = new int[64];

    public int Quality { get; }

    /// <summary>
    /// The DQT and DHT segments, markers included — everything in a JPEG header
    /// that depends on nothing but the quality. Sent once per session as
    /// MSG_JPEGTABLES.
    /// </summary>
    public byte[] Tables { get; }

    public JpegEncoder(int quality)
    {
        Quality = Math.Clamp(quality, 1, 100);
        // IJG quality scaling, so "quality 75" means what it means to anyone
        // who has ever saved a JPEG.
        int scale = Quality < 50 ? 5000 / Quality : 200 - 2 * Quality;
        for (int i = 0; i < 64; i++)
        {
            _qLum[i] = (byte)Math.Clamp((StdLumQ[i] * scale + 50) / 100, 1, 255);
            _qChr[i] = (byte)Math.Clamp((StdChromQ[i] * scale + 50) / 100, 1, 255);
        }
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
            {
                int i = r * 8 + c;
                _divLum[i] = 1.0f / (_qLum[i] * Aan[r] * Aan[c] * 8.0f);
                _divChr[i] = 1.0f / (_qChr[i] * Aan[r] * Aan[c] * 8.0f);
            }

        BuildCodes(DcLumBits, DcVals, _dcLumCode, _dcLumLen);
        BuildCodes(DcChrBits, DcVals, _dcChrCode, _dcChrLen);
        BuildCodes(AcLumBits, AcLumVals, _acLumCode, _acLumLen);
        BuildCodes(AcChrBits, AcChrVals, _acChrCode, _acChrLen);

        Tables = BuildTables();
    }

    // ITU T.81 Annex C: canonical codes from the length counts.
    private static void BuildCodes(byte[] bits, byte[] vals, ushort[] code, byte[] len)
    {
        int c = 0, k = 0;
        for (int l = 1; l <= 16; l++)
        {
            for (int i = 0; i < bits[l - 1]; i++)
            {
                code[vals[k]] = (ushort)c;
                len[vals[k]] = (byte)l;
                k++;
                c++;
            }
            c <<= 1;
        }
    }

    private byte[] BuildTables()
    {
        var t = new List<byte>(600);
        // DQT: both tables in one segment, in zigzag order.
        t.AddRange([0xFF, 0xDB, 0x00, 132]);
        t.Add(0x00);
        for (int k = 0; k < 64; k++) t.Add(_qLum[ZigZag[k]]);
        t.Add(0x01);
        for (int k = 0; k < 64; k++) t.Add(_qChr[ZigZag[k]]);

        // DHT: all four tables in one segment.
        int dhtLen = 2 + 4 * 17 + DcVals.Length * 2 + AcLumVals.Length + AcChrVals.Length;
        t.AddRange([0xFF, 0xC4, (byte)(dhtLen >> 8), (byte)dhtLen]);
        void Add(byte cls, byte[] bits, byte[] vals) { t.Add(cls); t.AddRange(bits); t.AddRange(vals); }
        Add(0x00, DcLumBits, DcVals);
        Add(0x10, AcLumBits, AcLumVals);
        Add(0x01, DcChrBits, DcVals);
        Add(0x11, AcChrBits, AcChrVals);
        return t.ToArray();
    }

    /// <summary>
    /// A complete, standalone JPEG for <paramref name="scan"/> — the same bytes
    /// the device assembles. Used by the self-test to check the encoder
    /// against an independent decoder.
    /// </summary>
    public byte[] Wrap(int w, int h, ReadOnlySpan<byte> scan)
    {
        var j = new List<byte>(Tables.Length + scan.Length + 64);
        j.AddRange([0xFF, 0xD8]);
        j.AddRange(Tables);
        j.AddRange(
        [
            0xFF, 0xC0, 0x00, 17, 8,
            (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w,
            3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
        ]);
        j.AddRange([0xFF, 0xDA, 0x00, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);
        j.AddRange(scan.ToArray());
        j.AddRange([0xFF, 0xD9]);
        return j.ToArray();
    }

    // ---------------------------------------------------------------- scan

    private byte[] _out = [];
    private int _pos;
    private uint _bitBuf;
    private int _bitCnt;

    /// <summary>
    /// Entropy-coded scan (no markers) for the <paramref name="w"/> x
    /// <paramref name="h"/> region at (<paramref name="x0"/>, <paramref
    /// name="y0"/>) of an 8-bit-per-channel R,G,B image with <paramref
    /// name="stride"/> bytes per row. Both dimensions must be multiples of 16.
    /// Returns the scan length, or -1 if it would not fit in <paramref
    /// name="max"/> bytes (the caller then falls back to a lossless mode, so
    /// there is nothing to recover from).
    ///
    /// Takes full 8-bit colour, not the RGB565 the lossless modes send. Fed
    /// 565, the encoder saw the 5- and 6-bit colour steps as detail and spent
    /// bits reproducing them, which came out as visible grain and ringing in
    /// video — the "JPEG look" — on top of the loss JPEG itself introduces.
    /// </summary>
    public int EncodeScan(ReadOnlySpan<byte> rgb, int stride, int x0, int y0, int w, int h,
                          byte[] dst, int at, int max)
    {
        _out = dst;
        _pos = at;
        _bitBuf = 0;
        _bitCnt = 0;
        int limit = at + max;
        int dcY = 0, dcCb = 0, dcCr = 0;

        for (int my = 0; my < h; my += 16)
        {
            for (int mx = 0; mx < w; mx += 16)
            {
                LoadMcu(rgb, stride, x0 + mx, y0 + my);

                for (int b = 0; b < 4; b++)
                {
                    int ox = (b & 1) * 8, oy = (b >> 1) * 8;
                    for (int r = 0; r < 8; r++)
                        for (int c = 0; c < 8; c++)
                            _blk[r * 8 + c] = _y[(oy + r) * 16 + ox + c];
                    dcY = CodeBlock(_divLum, dcY, _dcLumCode, _dcLumLen, _acLumCode, _acLumLen);
                }
                _cb.CopyTo(_blk, 0);
                dcCb = CodeBlock(_divChr, dcCb, _dcChrCode, _dcChrLen, _acChrCode, _acChrLen);
                _cr.CopyTo(_blk, 0);
                dcCr = CodeBlock(_divChr, dcCr, _dcChrCode, _dcChrLen, _acChrCode, _acChrLen);

                // Checked per MCU rather than per byte: an MCU is at most a
                // few hundred bytes, and the buffer is far larger than max.
                if (_pos > limit) return -1;
            }
        }

        // Pad the final partial byte with 1-bits, as T.81 F.1.2.3 requires.
        if (_bitCnt > 0) PutBits(0x7F, 7);
        return _pos > limit ? -1 : _pos - at;
    }

    private void LoadMcu(ReadOnlySpan<byte> rgb, int stride, int px0, int py0)
    {
        for (int r = 0; r < 16; r++)
        {
            int row = (py0 + r) * stride + px0 * 3;
            for (int c = 0; c < 16; c++)
            {
                int o = row + c * 3;
                float R = rgb[o], G = rgb[o + 1], B = rgb[o + 2];

                _y[r * 16 + c] = 0.299f * R + 0.587f * G + 0.114f * B - 128f;
                int ci = (r >> 1) * 8 + (c >> 1);
                // 4:2:0 — each chroma sample is the mean of its 2x2 block.
                float cb = -0.168736f * R - 0.331264f * G + 0.5f * B;
                float cr = 0.5f * R - 0.418688f * G - 0.081312f * B;
                if ((r & 1) == 0 && (c & 1) == 0) { _cb[ci] = cb * 0.25f; _cr[ci] = cr * 0.25f; }
                else { _cb[ci] += cb * 0.25f; _cr[ci] += cr * 0.25f; }
            }
        }
    }

    private int CodeBlock(float[] div, int prevDc,
                          ushort[] dcCode, byte[] dcLen, ushort[] acCode, byte[] acLen)
    {
        Fdct(_blk);
        for (int k = 0; k < 64; k++)
        {
            float v = _blk[ZigZag[k]] * div[ZigZag[k]];
            _zz[k] = (int)(v < 0 ? v - 0.5f : v + 0.5f);
        }

        int diff = _zz[0] - prevDc;
        int cat = Category(diff);
        PutBits(dcCode[cat], dcLen[cat]);
        if (cat > 0) PutBits((uint)(diff < 0 ? diff - 1 : diff) & ((1u << cat) - 1), cat);

        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int v = _zz[k];
            if (v == 0) { run++; continue; }
            while (run > 15) { PutBits(acCode[0xF0], acLen[0xF0]); run -= 16; }
            int ac = Category(v);
            int sym = (run << 4) | ac;
            PutBits(acCode[sym], acLen[sym]);
            PutBits((uint)(v < 0 ? v - 1 : v) & ((1u << ac) - 1), ac);
            run = 0;
        }
        if (run > 0) PutBits(acCode[0x00], acLen[0x00]);
        return _zz[0];
    }

    private static int Category(int v)
    {
        int a = v < 0 ? -v : v;
        int n = 0;
        while (a != 0) { n++; a >>= 1; }
        return n;
    }

    private void PutBits(uint bits, int n)
    {
        _bitBuf = (_bitBuf << n) | (bits & ((1u << n) - 1));
        _bitCnt += n;
        while (_bitCnt >= 8)
        {
            byte b = (byte)(_bitBuf >> (_bitCnt - 8));
            _bitCnt -= 8;
            if (_pos < _out.Length) _out[_pos] = b;
            _pos++;
            // A literal 0xFF in entropy-coded data must be followed by 0x00,
            // or the decoder reads it as the start of a marker.
            if (b == 0xFF)
            {
                if (_pos < _out.Length) _out[_pos] = 0;
                _pos++;
            }
        }
        _bitBuf &= (1u << _bitCnt) - 1;
    }

    // jfdctflt (AAN), unscaled; the scaling lives in the divisors.
    private static void Fdct(float[] d)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            int step = pass == 0 ? 1 : 8;     // rows, then columns
            int next = pass == 0 ? 8 : 1;
            for (int i = 0; i < 8; i++)
            {
                int o = i * next;
                float d0 = d[o], d1 = d[o + step], d2 = d[o + 2 * step], d3 = d[o + 3 * step];
                float d4 = d[o + 4 * step], d5 = d[o + 5 * step], d6 = d[o + 6 * step], d7 = d[o + 7 * step];

                float tmp0 = d0 + d7, tmp7 = d0 - d7;
                float tmp1 = d1 + d6, tmp6 = d1 - d6;
                float tmp2 = d2 + d5, tmp5 = d2 - d5;
                float tmp3 = d3 + d4, tmp4 = d3 - d4;

                float tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3;
                float tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
                d[o]            = tmp10 + tmp11;
                d[o + 4 * step] = tmp10 - tmp11;
                float z1 = (tmp12 + tmp13) * 0.707106781f;
                d[o + 2 * step] = tmp13 + z1;
                d[o + 6 * step] = tmp13 - z1;

                tmp10 = tmp4 + tmp5;
                tmp11 = tmp5 + tmp6;
                tmp12 = tmp6 + tmp7;
                float z5 = (tmp10 - tmp12) * 0.382683433f;
                float z2 = 0.541196100f * tmp10 + z5;
                float z4 = 1.306562965f * tmp12 + z5;
                float z3 = tmp11 * 0.707106781f;
                float z11 = tmp7 + z3, z13 = tmp7 - z3;
                d[o + 5 * step] = z13 + z2;
                d[o + 3 * step] = z13 - z2;
                d[o + 1 * step] = z11 + z4;
                d[o + 7 * step] = z11 - z4;
            }
        }
    }
}
