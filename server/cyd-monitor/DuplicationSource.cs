using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace CydMonitor;

/// <summary>
/// Captures a monitor with DXGI Desktop Duplication and scales it on the GPU.
///
/// This exists because GDI could not meet the frame-rate target. Screen reads
/// through a display DC are synchronised to the monitor's refresh: measured
/// with --benchcapture, even a 1x1 BitBlt from the screen costs 16.66 ms (one
/// interval at 60 Hz) and a full-screen read costs 33.3 ms — the whole 30 fps
/// budget, with nothing left for colour conversion, encoding or the network.
/// That cost is a wait rather than work, so no stretch mode or staging buffer
/// could reduce it.
///
/// Desktop Duplication instead hands over the frame the compositor just
/// produced, as a GPU texture, and only when there is one. Three things follow
/// from that which matter here:
///
///  * Scaling is free. The texture is already on the GPU, so the downscale is
///    one textured draw, and area-averaging comes from the hardware's own
///    mip selection rather than a filter someone has to write.
///  * Only the 320x240 result crosses back into system memory — 150 KB a frame
///    instead of the 8 MB a full-resolution readback would cost.
///  * An idle desktop produces no frames at all, so the server stops working
///    instead of busily rediscovering that nothing changed.
/// </summary>
internal sealed unsafe class DuplicationSource : IFrameSource
{
    private readonly string _deviceName;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _ctx;
    private IDXGIOutput1? _output1;
    private IDXGIOutputDuplication? _dupl;

    private ID3D11Texture2D? _mip;          // full-size copy carrying a mip chain
    private ID3D11ShaderResourceView? _mipSrv;
    private ID3D11Texture2D? _rt;           // 320x240 scaled result
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _staging;      // CPU-readable copy of _rt
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11SamplerState? _sampler;

    private int _srcW, _srcH;
    private readonly IntPtr _screenDc;
    private Dib _out;
    private readonly uint[] _base;          // scaled desktop without the pointer

    private bool _haveBase;
    private bool _haveDesktop;   // at least one real desktop image has reached _mip

    // Guards against a Duplication interface that has gone stale without ever
    // throwing: observed in practice as AcquireNextFrame returning
    // WaitTimeout forever - not one new frame, not even a cursor move - on a
    // desktop that was neither locked nor actually idle (the session's own
    // Windows input was live). Nothing here distinguishes that from a
    // genuinely idle desktop, which produces exactly the same silence and is
    // the common, deliberately-cheap case (see the class comment) - so rather
    // than trying to tell the two apart, a Duplication interface that has
    // gone this long without delivering *anything*, real frame or pointer
    // move, is recreated unconditionally. On a healthy interface this branch
    // is never reached (every real desktop keeps producing something well
    // inside the interval); on an idle one it costs one extra DuplicateOutput
    // call and, at most, one repeated frame the encoder discards as
    // unchanged; on a stuck one it is the only thing that ever resyncs it.
    private const long RecreateIntervalMs = 20_000;
    private long _lastActivityMs = Environment.TickCount64;
    private int _quietRecreates;   // cheap rebuilds in a row that brought no activity back; see Capture

    // When there is no duplication at all (a rebuild failed), Capture keeps trying, but no
    // more often than this: a locked screen, or a session parked on another display adapter
    // (Remote Desktop), then costs one probe every couple of seconds instead of a spin, and
    // the picture returns by itself as soon as the output exists again.
    private const long RebuildRetryMs = 2_000;
    private long _lastRebuildTryMs;
    private bool _broken;   // the last full rebuild failed; only so the log says so once, not every retry

    public int CursorSize { get; set; } = 20;
    public string Kind => "DXGI Desktop Duplication (GPU)";
    public int AcquireTimeoutMs { get; set; }
    public Native.RECT LastSrc { get; private set; }
    public Native.RECT LastDst { get; private set; }
    public uint* Pixels => _out.Bits;

    /// <summary>
    /// Throws if duplication cannot be set up, which is a normal outcome in
    /// some sessions rather than a bug — the caller falls back to GDI.
    /// </summary>
    public DuplicationSource(string deviceName)
    {
        _deviceName = deviceName;
        _screenDc = Native.GetDC(IntPtr.Zero);
        _out = new Dib(_screenDc, Proto.W, Proto.H);
        _base = new uint[Proto.W * Proto.H];
        Initialise();
    }

    private void Initialise()
    {
        var (chosenAdapter, chosenOutput) = FindOutputWithRetry();
        if (chosenAdapter is null || chosenOutput is null)
            throw new InvalidOperationException($"DXGI nie zna wyjscia {_deviceName}");

        try
        {
            // The device has to be created on the adapter that actually owns
            // the output; duplicating an output from a different adapter's
            // device fails, which is exactly what happens on a laptop with
            // switchable graphics if this is skipped.
            var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            D3D11.D3D11CreateDevice(chosenAdapter, DriverType.Unknown,
                                    DeviceCreationFlags.BgraSupport, levels,
                                    out _device, out _ctx).CheckError();

            var desc = chosenOutput.Description.DesktopCoordinates;
            _srcW = desc.Right - desc.Left;
            _srcH = desc.Bottom - desc.Top;

            _output1 = chosenOutput.QueryInterface<IDXGIOutput1>();
            _dupl = _output1.DuplicateOutput(_device!);

            CreatePipeline();
        }
        finally
        {
            chosenOutput.Dispose();
            chosenAdapter.Dispose();
        }
    }

    /// <summary>
    /// Looks up the adapter/output pair for <see cref="_deviceName"/>, retrying
    /// a few times a beat apart before giving up.
    ///
    /// Needed for the same reason <c>Monitors.TryEnumCurrentSettings</c>
    /// retries: a virtual monitor created moments earlier can be invisible to
    /// this specific lookup on the very first attempt even though Win32's own
    /// GDI enumeration already reports it — observed end-to-end (create
    /// display, connect a panel within a second or two) rather than assumed.
    /// The DXGI factory is recreated on every attempt rather than reused,
    /// because a factory that was created before the new output attached is
    /// exactly the stale-topology snapshot documented to need a fresh
    /// <c>CreateDXGIFactory1</c> call to see a topology change at all.
    /// </summary>
    private (IDXGIAdapter1?, IDXGIOutput?) FindOutputWithRetry()
    {
        for (int attempt = 0; ; attempt++)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                {
                    if (output.Description.DeviceName.TrimEnd('\0') == _deviceName.TrimEnd('\0'))
                        return (adapter, output);
                    output.Dispose();
                }
                adapter.Dispose();
            }

            if (attempt >= 4) return (null, null);
            Thread.Sleep(150);
        }
    }

    private void CreatePipeline()
    {
        var dev = _device!;

        // MipLevels = 0 asks for the complete chain. The chain is the filter:
        // sampling it with trilinear filtering from a 320x240 viewport makes the
        // hardware pick the mip whose pixels are the right size and blend
        // between levels, which is an area average done by fixed-function
        // silicon instead of a loop.
        _mip = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_srcW,
            Height = (uint)_srcH,
            MipLevels = 0,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _mipSrv = dev.CreateShaderResourceView(_mip);

        _rt = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = Proto.W,
            Height = Proto.H,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
        });
        _rtv = dev.CreateRenderTargetView(_rt);

        _staging = dev.CreateTexture2D(new Texture2DDescription
        {
            Width = Proto.W,
            Height = Proto.H,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        // A single triangle covering the viewport, generated from the vertex id.
        // No vertex buffer, no input layout, nothing to bind.
        const string hlsl = """
            Texture2D    tex  : register(t0);
            SamplerState samp : register(s0);

            struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

            VSOut VSMain(uint id : SV_VertexID)
            {
                float2 p = float2((id << 1) & 2, id & 2);
                VSOut o;
                o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
                o.uv  = p;
                return o;
            }

            float4 PSMain(VSOut i) : SV_Target
            {
                return tex.Sample(samp, i.uv);
            }
            """;

        // Compile returns plain memory, not a COM blob, so there is nothing to dispose.
        var vsBlob = Vortice.D3DCompiler.Compiler.Compile(hlsl, "VSMain", "cyd.hlsl", "vs_4_0");
        var psBlob = Vortice.D3DCompiler.Compiler.Compile(hlsl, "PSMain", "cyd.hlsl", "ps_4_0");
        _vs = dev.CreateVertexShader(vsBlob.Span);
        _ps = dev.CreatePixelShader(psBlob.Span);

        _sampler = dev.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxAnisotropy = 1,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
    }

    public double LastWorkMs { get; private set; }
    public double LastWaitMs { get; private set; }

    /// <summary>Desktop presents reported by DXGI so far; diagnostics only.</summary>
    public int PresentsSeen { get; private set; }

    public bool Capture(Native.RECT monitor, ScaleMode mode)
    {
        // No duplication means a rebuild failed. This used to return false here for good
        // ("the next Capture call tries again", said the comment in Recreate) - but nothing
        // below is reached with _dupl null, so one failed rebuild meant a picture that never
        // came back for the rest of the session, with the panel still getting keepalives.
        // It is exactly what a switch between the console and a Remote Desktop session does
        // to the old device and output objects.
        if (_dupl is null && !TryRebuild()) return false;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool newDesktop;
        try
        {
            newDesktop = PullFrame();
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Access lost: a mode change, a full-screen app taking exclusive
            // ownership, or a session switch. Rebuilding the duplication is the
            // documented recovery, and it has to happen here rather than
            // bubbling up, or every resolution change from the panel's menu
            // would end the session.
            Recreate();
            return false;
        }

        // See the note on RecreateIntervalMs: a Duplication interface can go
        // quiet without ever throwing. The cheap rebuild (same device and output)
        // goes first; if that did not bring any activity back by the next interval,
        // the objects under it are stale as well, so start over from a fresh factory.
        if (Environment.TickCount64 - _lastActivityMs > RecreateIntervalMs)
        {
            if (_quietRecreates++ == 0) Recreate();
            else Reinitialise();
        }

        // Nothing to show until the compositor has handed over at least one
        // desktop image. Before that the mip texture is empty, and drawing it
        // anyway — which the !_haveBase branch below used to do on the very
        // first call of every session — produced an all-black frame. Being
        // the first frame of a session it went out as a forced full repaint,
        // delivered top row band first, so every connect painted a black bar
        // down the whole panel before the real picture followed it.
        if (newDesktop) _haveDesktop = true;
        if (!_haveDesktop) return false;

        ComputeGeometry(monitor, mode, out var src, out var dst);

        if (newDesktop || !_haveBase)
        {
            if (mode == ScaleMode.OneToOne) BlitOneToOne(src, dst);
            else ScaleToRenderTarget(dst);
            ReadBack();
            _haveBase = true;
        }
        else if (LastDstChanged(dst))
        {
            // Geometry moved without a new desktop frame (the pointer crossed
            // into a different 1:1 window). The scaled content has to be
            // rebuilt even though the desktop itself did not change.
            if (mode == ScaleMode.OneToOne) BlitOneToOne(src, dst);
            else ScaleToRenderTarget(dst);
            ReadBack();
        }

        LastSrc = src;
        LastDst = dst;

        // Always recomposite: a pointer-only update is a real change as far as
        // the panel is concerned, and it costs one icon draw over a memcpy.
        fixed (uint* b = _base)
        {
            Buffer.MemoryCopy(b, _out.Bits, Proto.W * Proto.H * 4L, Proto.W * Proto.H * 4L);
        }
        CursorOverlay.Draw(_out, src, dst, CursorSize);
        // Everything since the acquire returned is real work; the wait recorded
        // inside PullFrame is not, and folding the two together would make an
        // idle desktop look like an expensive one.
        LastWorkMs = clock.Elapsed.TotalMilliseconds - LastWaitMs;
        return true;
    }

    /// <summary>
    /// Takes whatever the compositor has ready. Returns true when a new desktop
    /// image arrived, false when the call only reported a pointer move.
    /// </summary>
    private bool PullFrame()
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var result = _dupl!.AcquireNextFrame((uint)AcquireTimeoutMs, out OutduplFrameInfo info, out IDXGIResource? resource);
        LastWaitMs = waited.Elapsed.TotalMilliseconds;
        if (result.Failure || resource is null)
        {
            // WaitTimeout with a 0 ms timeout is the ordinary "nothing new"
            // answer on a static desktop, not an error.
            if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return false;
            result.CheckError();
            return false;
        }

        try
        {
            // How many times the desktop was actually redrawn since the last
            // acquire — the source's true frame rate, independent of how often
            // we happen to look. Above 1 means frames were composited that no
            // capture ever saw.
            PresentsSeen += (int)info.AccumulatedFrames;
            // Any successful acquire - even a pointer-only one - is proof the
            // interface is alive; see the note on RecreateIntervalMs.
            _lastActivityMs = Environment.TickCount64;
            _quietRecreates = 0;
            if (info.LastPresentTime == 0) return false;   // pointer moved only
            using var tex = resource.QueryInterface<ID3D11Texture2D>();
            _ctx!.CopySubresourceRegion(_mip!, 0, 0, 0, 0, tex, 0);
            _ctx.GenerateMips(_mipSrv!);
            return true;
        }
        finally
        {
            resource.Dispose();
            _dupl!.ReleaseFrame();
        }
    }

    private void ScaleToRenderTarget(Native.RECT dst)
    {
        var ctx = _ctx!;
        ctx.OMSetRenderTargets(_rtv!);
        // Clear unconditionally: in Fit mode the letterbox bars are not covered
        // by the draw, and leaving them undefined would send noise.
        ctx.ClearRenderTargetView(_rtv!, new Color4(0f, 0f, 0f, 1f));
        ctx.RSSetViewport(dst.Left, dst.Top, dst.Width, dst.Height);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetShaderResource(0, _mipSrv!);
        ctx.PSSetSampler(0, _sampler);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.Draw(3, 0);
    }

    private void BlitOneToOne(Native.RECT src, Native.RECT dst)
    {
        // True scale: copy the window straight across with no filtering at all,
        // which is the whole point of the mode.
        var box = new Box(src.Left, src.Top, 0, src.Right, src.Bottom, 1);
        _ctx!.CopySubresourceRegion(_rt!, 0, (uint)dst.Left, (uint)dst.Top, 0, _mip!, 0, box);
    }

    private void ReadBack()
    {
        var ctx = _ctx!;
        ctx.CopyResource(_staging!, _rt!);
        var map = ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* srcBase = (byte*)map.DataPointer;
            fixed (uint* dstBase = _base)
            {
                for (int y = 0; y < Proto.H; y++)
                {
                    // RowPitch is whatever the driver chose and is routinely
                    // wider than the image, so rows have to be copied
                    // individually rather than the surface in one block.
                    Buffer.MemoryCopy(srcBase + (long)y * map.RowPitch,
                                      dstBase + y * Proto.W,
                                      Proto.W * 4L, Proto.W * 4L);
                }
            }
        }
        finally
        {
            ctx.Unmap(_staging!, 0);
        }
    }

    private void ComputeGeometry(Native.RECT monitor, ScaleMode mode,
                                out Native.RECT src, out Native.RECT dst)
    {
        if (mode == ScaleMode.OneToOne)
        {
            var ci = new Native.CURSORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.CURSORINFO>(),
            };
            int cx = monitor.Width / 2, cy = monitor.Height / 2;
            if (Native.GetCursorInfo(ref ci) &&
                ci.ptScreenPos.X >= monitor.Left && ci.ptScreenPos.X < monitor.Right &&
                ci.ptScreenPos.Y >= monitor.Top && ci.ptScreenPos.Y < monitor.Bottom)
            {
                cx = ci.ptScreenPos.X - monitor.Left;
                cy = ci.ptScreenPos.Y - monitor.Top;
            }
            // Texture-local coordinates: the duplication texture starts at the
            // monitor's own origin, not the virtual desktop's.
            int left = Math.Clamp(cx - Proto.W / 2, 0, Math.Max(0, _srcW - Proto.W));
            int top  = Math.Clamp(cy - Proto.H / 2, 0, Math.Max(0, _srcH - Proto.H));
            int w = Math.Min(Proto.W, _srcW), h = Math.Min(Proto.H, _srcH);
            src = new Native.RECT
            {
                Left = monitor.Left + left, Top = monitor.Top + top,
                Right = monitor.Left + left + w, Bottom = monitor.Top + top + h,
            };
            dst = new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
            return;
        }

        src = monitor;
        if (mode == ScaleMode.Stretch || monitor.Width * Proto.H == monitor.Height * Proto.W)
        {
            // See the note on Proto.StatsMarginPx for why this row band is
            // carved out unconditionally rather than only when the overlay
            // is actually on.
            dst = new Native.RECT { Left = 0, Top = 0, Right = Proto.W, Bottom = Proto.H - Proto.StatsMarginPx };
            return;
        }

        int dw, dh;
        if (monitor.Width * Proto.H > monitor.Height * Proto.W)
        {
            dw = Proto.W;
            dh = Math.Max(1, (int)Math.Round(Proto.W * (double)monitor.Height / monitor.Width));
        }
        else
        {
            dh = Proto.H;
            dw = Math.Max(1, (int)Math.Round(Proto.H * (double)monitor.Width / monitor.Height));
        }
        int ox = (Proto.W - dw) / 2, oy = (Proto.H - dh) / 2;
        dst = new Native.RECT { Left = ox, Top = oy, Right = ox + dw, Bottom = oy + dh };
    }

    private bool LastDstChanged(Native.RECT dst) =>
        dst.Left != LastDst.Left || dst.Top != LastDst.Top ||
        dst.Right != LastDst.Right || dst.Bottom != LastDst.Bottom;

    private void Recreate()
    {
        _dupl?.Dispose();
        _dupl = null;
        _haveBase = false;
        _haveDesktop = false;
        // Fresh grace period for the new interface, whether this recreate was
        // triggered by an exception or by staleness - otherwise a recreate
        // that itself starts out quiet (plausible on a genuinely idle
        // desktop) would immediately look overdue again on the next call.
        _lastActivityMs = Environment.TickCount64;
        try
        {
            // The output object survives an access loss, so only the duplication
            // needs rebuilding — but the monitor may have changed size, in which
            // case the whole GPU pipeline is the wrong shape.
            var desc = _output1!.Description.DesktopCoordinates;
            int w = desc.Right - desc.Left, h = desc.Bottom - desc.Top;
            if (w != _srcW || h != _srcH)
            {
                _srcW = w; _srcH = h;
                DisposePipeline();
                CreatePipeline();
            }
            _dupl = _output1.DuplicateOutput(_device!);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // Still unavailable, or the device and output are stale: _dupl stays null and
            // Capture goes on to TryRebuild, which starts over from a fresh factory.
        }
    }

    /// <summary>
    /// Called by Capture while there is no duplication. Rebuilds from scratch, but at most
    /// every <see cref="RebuildRetryMs"/>. Returns whether there is a duplication now.
    /// </summary>
    private bool TryRebuild()
    {
        long now = Environment.TickCount64;
        if (now - _lastRebuildTryMs < RebuildRetryMs) return false;
        _lastRebuildTryMs = now;
        Reinitialise();
        return _dupl is not null;
    }

    /// <summary>
    /// The heavy recovery: drops the D3D device and every DXGI object and builds them again
    /// the way the constructor does, from a fresh factory. <see cref="Recreate"/> reuses the
    /// old device and output and cannot help once those are stale, which is what a session
    /// moving between the console and Remote Desktop does to them (the desktop is drawn on
    /// another display adapter meanwhile and comes back on a new one).
    /// </summary>
    private void Reinitialise()
    {
        _dupl?.Dispose(); _dupl = null;
        DisposePipeline();
        _output1?.Dispose(); _output1 = null;
        _ctx?.Dispose(); _ctx = null;
        _device?.Dispose(); _device = null;
        _haveBase = false;
        _haveDesktop = false;
        _lastActivityMs = Environment.TickCount64;
        try
        {
            Initialise();
            if (_broken)
            {
                _broken = false;
                Console.WriteLine("\n  Desktop Duplication odtworzone od zera");
            }
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or InvalidOperationException)
        {
            // The output is not there (yet): the session is on another adapter, or the
            // display is still coming back. Capture tries again after RebuildRetryMs.
            if (!_broken)
            {
                _broken = true;
                Console.WriteLine($"\n  Desktop Duplication niedostepne, ponawiam co {RebuildRetryMs / 1000} s " +
                                  $"({e.GetType().Name}: {e.Message})");
            }
        }
    }

    private void DisposePipeline()
    {
        _sampler?.Dispose(); _sampler = null;
        _ps?.Dispose(); _ps = null;
        _vs?.Dispose(); _vs = null;
        _staging?.Dispose(); _staging = null;
        _rtv?.Dispose(); _rtv = null;
        _rt?.Dispose(); _rt = null;
        _mipSrv?.Dispose(); _mipSrv = null;
        _mip?.Dispose(); _mip = null;
    }

    public void Dispose()
    {
        _dupl?.Dispose();
        _output1?.Dispose();
        DisposePipeline();
        _ctx?.Dispose();
        _device?.Dispose();
        _out.Dispose();
        if (_screenDc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
