using System.Runtime.InteropServices;
using ClipPlacement = Temno.Core.Editing.ClipPlacement;
using Temno.Media.Hardware;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Temno.Media.Compositing;

/// <summary>
/// D3D11 compute compositor (WP-215). Same contract as <see cref="CpuCompositor"/>, which stays the
/// numerical reference: the parity rules and the per-case tolerance are in
/// <c>docs/verification/wp-215/DECISION.md</c>.
/// <para>
/// The two properties that make the tolerance small enough to ship are both deliberate. First, this
/// does <b>not</b> use a <c>Texture2D</c> sampler: the fixed-function bilinear filter is specified
/// to 8 bits of subtexel precision, so it cannot match WP-214's 16-bit fixed-point weights. Instead
/// the per-column and per-row weight tables are computed <b>host-side, in <c>double</c>, by the same
/// arithmetic as <see cref="CpuCompositor"/></b> and uploaded, and the shader lerps in exact 32-bit
/// integers — which makes the axis-aligned sample bit-identical to the CPU path. Second,
/// <c>opacity &gt;= 1</c> and the axis-aligned in-bounds test are decided host-side too, so both
/// implementations take the same branch on the same pixels rather than on their own rounding.
/// </para>
/// <para>
/// What is left in <c>float</c> is the blend tail and the rotated mapping, which is where the
/// tolerance lives. Every failure — no device, a lost device, a shader compile failure, an
/// unexpected exception mid-frame — renders the frame on <see cref="CpuCompositor"/> instead and
/// reports it through <see cref="CompositorDiagnostics"/>; a fallback is never silent.
/// </para>
/// </summary>
public sealed class GpuCompositor : ICompositor, IDisposable
{
    /// <summary>Matches <see cref="CpuCompositor"/>'s <c>BilinearWeightScale</c>. Do not diverge.</summary>
    private const int BilinearWeightScale = 65536;

    // Caption tiles construct short-lived compositors; shader source is process-constant,
    // while the native shader/device objects must remain owned by each instance.
    private static readonly Lazy<byte[]> CompositeBytecode = new(() =>
        Compiler.Compile(CompositeHlsl, "CSComposite", "Composite.hlsl", "cs_5_0").ToArray());
    private static readonly Lazy<byte[]> ClearBytecode = new(() =>
        Compiler.Compile(CompositeHlsl, "CSClear", "Composite.hlsl", "cs_5_0").ToArray());

    private readonly CpuCompositor _reference;
    private readonly HardwareCapabilities _caps;
    private readonly object _gate = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _compositeShader;
    private ID3D11ComputeShader? _clearShader;
    private ID3D11Buffer? _constantBuffer;

    private ID3D11Buffer? _canvasBuffer;
    private ID3D11UnorderedAccessView? _canvasUav;
    private ID3D11Buffer? _canvasStaging;
    private int _canvasPixels;

    private ID3D11Buffer? _sourceBuffer;
    private ID3D11ShaderResourceView? _sourceSrv;
    private int _sourceCapacityPixels;

    private ID3D11Buffer? _tableBuffer;
    private ID3D11ShaderResourceView? _tableSrv;
    private int _tableCapacity;

    private bool _initAttempted;
    private bool _gpuReady;
    private string? _lastGpuError;
    private string? _adapterDescription;
    private bool _disposed;

    public GpuCompositor(CpuCompositor? reference = null, HardwareCapabilities? caps = null)
    {
        _reference = reference ?? new CpuCompositor();
        _caps = caps ?? HardwareCapabilities.Probe();
    }

    /// <summary>
    /// WP-248. When set, a GPU-composited frame is read back into a buffer rented from this pool,
    /// and is a pooled frame its receiver must dispose. Only the preview provider sets it, on its
    /// own instance before its first render; export and the CPU fallback allocate as before. A
    /// property rather than a <see cref="CompositorFactory.Create"/> argument, so the factory's
    /// choice of compositor still cannot depend on who asks (WP-215 R1).
    /// </summary>
    public FrameBufferPool? OutputPool { get; set; }

    /// <summary>True once the device and both shaders exist. Probes on first read.</summary>
    public bool IsGpuReady
    {
        get { EnsureDevice(); return _gpuReady; }
    }

    /// <summary>Why <see cref="IsGpuReady"/> is false, or the reason for the last fallback.</summary>
    public string? LastGpuError
    {
        get { EnsureDevice(); return _lastGpuError; }
    }

    /// <summary>Adapter the composite actually runs on, once the device exists.</summary>
    public string? AdapterDescription
    {
        get { EnsureDevice(); return _adapterDescription; }
    }

    public VideoFrame Render(IReadOnlyList<CompositorLayer> layers, int outputWidth, int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (outputWidth <= 0) throw new ArgumentOutOfRangeException(nameof(outputWidth));
        if (outputHeight <= 0) throw new ArgumentOutOfRangeException(nameof(outputHeight));

        EnsureDevice();
        if (!_gpuReady)
            return RenderOnReference(layers, outputWidth, outputHeight, fallbackReason: null);

        try
        {
            lock (_gate)
            {
                var frame = RenderGpu(layers, outputWidth, outputHeight);
                CompositorDiagnostics.RecordFrame(CompositorPath.Gpu, _adapterDescription);
                return frame;
            }
        }
        catch (Exception ex)
        {
            // A device removed mid-export, a driver reset, an allocation refusal: the frame still has
            // to be produced, and the reason still has to be visible. Do not swallow it.
            var reason = ex.GetType().Name + ": " + ex.Message;
            _lastGpuError = reason;
            lock (_gate) InvalidateAfterFailure();
            return RenderOnReference(layers, outputWidth, outputHeight, reason);
        }
    }

    private VideoFrame RenderOnReference(
        IReadOnlyList<CompositorLayer> layers, int outputWidth, int outputHeight, string? fallbackReason)
    {
        var detail = fallbackReason is null
            ? _lastGpuError is { Length: > 0 } err ? "D3D11 unavailable: " + err : "D3D11 unavailable"
            : "fell back from GPU";
        var frame = _reference.Render(layers, outputWidth, outputHeight);
        CompositorDiagnostics.RecordFrame(CompositorPath.Cpu, detail, fallbackReason);
        return frame;
    }

    // ---------------------------------------------------------------------------------------
    // Host-side geometry. Every value below is computed by the same arithmetic, in the same order
    // and in the same type (double), as CpuCompositor.BlendLayer. That is what makes the sample
    // bit-identical rather than merely close, so any "simplification" here is a parity change.
    // ---------------------------------------------------------------------------------------

    private readonly struct LayerPlan
    {
        public required int MinX { get; init; }
        public required int MinY { get; init; }
        public required int MaxX { get; init; }
        public required int MaxY { get; init; }
        public required bool HasRotation { get; init; }
        public required double DstCx { get; init; }
        public required double DstCy { get; init; }
        public required double DstW { get; init; }
        public required double DstH { get; init; }
        public required double CosR { get; init; }
        public required double SinR { get; init; }
        public required bool OneToOne { get; init; }
        public required int SourceOffsetX { get; init; }
        public required int SourceOffsetY { get; init; }
        public required double Opacity { get; init; }
        public required CpuBlendModes.Kind BlendMode { get; init; }
        public required bool Bgra { get; init; }
    }

    private static LayerPlan? PlanLayer(CompositorLayer layer, int canvasW, int canvasH)
    {
        var src = layer.Frame;
        if (src.Width <= 0 || src.Height <= 0 || src.Data.Length < src.Width * src.Height * 4) return null;

        // WP-405: the picture keeps its own aspect inside the transform box; it is never stretched.
        var t = layer.PlacementIsFinal
            ? layer.Transform
            : ClipPlacement.FitInBox(layer.Transform, src.Width, src.Height, canvasW, canvasH);
        var opacity = Math.Clamp(layer.Opacity, 0, 1);

        var dstW = t.Width * canvasW;
        var dstH = t.Height * canvasH;
        var dstCx = t.CenterX * canvasW;
        var dstCy = t.CenterY * canvasH;

        var hasRotation = Math.Abs(t.Rotation) > 0.001;
        var cosR = Math.Cos(-t.Rotation * Math.PI / 180.0);
        var sinR = Math.Sin(-t.Rotation * Math.PI / 180.0);

        int minX, minY, maxX, maxY;
        if (hasRotation)
        {
            var hw = dstW / 2; var hh = dstH / 2;
            var bound = Math.Abs(hw * cosR) + Math.Abs(hh * sinR);
            var boundY = Math.Abs(hw * sinR) + Math.Abs(hh * cosR);
            minX = Math.Max(0, (int)(dstCx - bound));
            maxX = Math.Min(canvasW - 1, (int)(dstCx + bound));
            minY = Math.Max(0, (int)(dstCy - boundY));
            maxY = Math.Min(canvasH - 1, (int)(dstCy + boundY));
        }
        else
        {
            minX = Math.Max(0, (int)(dstCx - dstW / 2));
            maxX = Math.Min(canvasW - 1, (int)(dstCx + dstW / 2 - 0.001));
            minY = Math.Max(0, (int)(dstCy - dstH / 2));
            maxY = Math.Min(canvasH - 1, (int)(dstCy + dstH / 2 - 0.001));
        }

        if (minX > maxX || minY > maxY) return null;

        var offsetX = dstCx - dstW / 2;
        var offsetY = dstCy - dstH / 2;
        var oneToOne =
            !hasRotation && !t.FlipHorizontal && !t.FlipVertical
            && Math.Abs(dstW - src.Width) < 1e-6 && Math.Abs(dstH - src.Height) < 1e-6
            && Math.Abs(offsetX - Math.Round(offsetX)) < 1e-6
            && Math.Abs(offsetY - Math.Round(offsetY)) < 1e-6;

        return new LayerPlan
        {
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            HasRotation = hasRotation,
            DstCx = dstCx,
            DstCy = dstCy,
            DstW = dstW,
            DstH = dstH,
            CosR = cosR,
            SinR = sinR,
            OneToOne = oneToOne,
            SourceOffsetX = (int)Math.Round(offsetX),
            SourceOffsetY = (int)Math.Round(offsetY),
            Opacity = opacity,
            BlendMode = CpuBlendModes.Parse(layer.BlendMode),
            Bgra = string.Equals(src.PixelFormat, "bgra", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Per-axis sample plan for one layer: the two source indices and the 16-bit weight between
    /// them, or <c>X0 = -1</c> for a destination position that falls outside the source. Mirrors the
    /// <c>colX0/colX1/colW</c> and <c>rowY0/rowY1/rowW</c> arrays in
    /// <c>CpuCompositor.BlendLayerResampleFast</c> — including the order of the bounds test and the
    /// flip, which is why the flip is applied after the test and not before.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AxisEntry
    {
        public int X0;
        public int X1;
        public int W;
        public int Pad;
    }

    private static void BuildAxis(
        Span<AxisEntry> entries, int min, int count, double center, double extent,
        bool flip, int sourceExtent, bool oneToOne, int sourceOffset)
    {
        for (var i = 0; i < count; i++)
        {
            var rel = (min + i) + 0.5 - center;
            var norm = rel / extent + 0.5;
            if (norm < 0 || norm >= 1)
            {
                entries[i] = new AxisEntry { X0 = -1 };
                continue;
            }

            if (oneToOne)
            {
                // The 1:1 branch in CpuCompositor indexes the source directly instead of sampling:
                // dstW is only within 1e-6 of the source width, so re-deriving the position through
                // the resample formula would not land on an exact weight of zero.
                var exact = (min + i) - sourceOffset;
                entries[i] = new AxisEntry { X0 = exact, X1 = exact, W = 0 };
                continue;
            }

            if (flip) norm = 1.0 - norm;
            var f = norm * sourceExtent - 0.5;
            var raw = (int)Math.Floor(f);
            var x0 = Math.Clamp(raw, 0, sourceExtent - 1);
            entries[i] = new AxisEntry
            {
                X0 = x0,
                X1 = Math.Min(x0 + 1, sourceExtent - 1),
                W = QuantizeWeight(f - raw),
            };
        }
    }

    private static int QuantizeWeight(double frac)
    {
        var w = (int)(frac * BilinearWeightScale + 0.5);
        return w < 0 ? 0 : w > BilinearWeightScale ? BilinearWeightScale : w;
    }

    private static void BuildAreaAxis(List<AxisEntry> tables, int headerOffset,
        int min, int count, double center, double extent, bool flip, int sourceExtent)
    {
        for (var i = 0; i < count; i++)
        {
            var norm = ((min + i) + 0.5 - center) / extent + 0.5;
            if (norm < 0 || norm >= 1)
            {
                tables[headerOffset + i] = new AxisEntry { X0 = -1 };
                continue;
            }
            if (flip) norm = 1 - norm;
            var kernel = Minification.Axis(norm * sourceExtent, sourceExtent / extent, sourceExtent);
            var start = tables.Count;
            for (var source = kernel.First; source <= kernel.Last; source++)
            {
                var weight = kernel.Weight(source);
                if (weight != 0) tables.Add(new AxisEntry { X0 = source, W = weight });
            }
            tables[headerOffset + i] = new AxisEntry { X0 = start, X1 = tables.Count - start };
        }
    }

    // ---------------------------------------------------------------------------------------
    // GPU render
    // ---------------------------------------------------------------------------------------

    private unsafe VideoFrame RenderGpu(IReadOnlyList<CompositorLayer> layers, int canvasW, int canvasH)
    {
        var context = _context!;
        var pixels = checked(canvasW * canvasH);
        EnsureCanvas(pixels);

        // Opaque black base canvas, cleared on the device so a 4K frame costs a dispatch instead of
        // a 33 MB upload.
        context.CSSetShader(_clearShader);
        context.CSSetUnorderedAccessView(0, _canvasUav);
        context.CSSetConstantBuffer(0, _constantBuffer);
        WriteConstants(new Constants { CanvasW = (uint)canvasW, CanvasH = (uint)canvasH });
        context.Dispatch(((uint)canvasW + 7) / 8, ((uint)canvasH + 7) / 8, 1);

        context.CSSetShader(_compositeShader);
        foreach (var layer in layers)
        {
            if (layer.Opacity <= 0) continue;
            var plan = PlanLayer(layer, canvasW, canvasH);
            if (plan is not { } p) continue;

            var src = layer.Frame;
            var cols = p.MaxX - p.MinX + 1;
            var rows = p.MaxY - p.MinY + 1;

            UploadSource(src);

            var area = !p.OneToOne && (Minification.NeedsArea(src.Width, p.DstW, p.DstCx - p.DstW / 2)
                || Minification.NeedsArea(src.Height, p.DstH, p.DstCy - p.DstH / 2));
            if (!p.HasRotation && area)
            {
                var tables = new List<AxisEntry>(new AxisEntry[cols + rows]);
                BuildAreaAxis(tables, 0, p.MinX, cols, p.DstCx, p.DstW, layer.Transform.FlipHorizontal, src.Width);
                BuildAreaAxis(tables, cols, p.MinY, rows, p.DstCy, p.DstH, layer.Transform.FlipVertical, src.Height);
                UploadTables(CollectionsMarshal.AsSpan(tables));
            }
            else if (!p.HasRotation)
            {
                var tables = new AxisEntry[cols + rows];
                BuildAxis(
                    tables.AsSpan(0, cols), p.MinX, cols, p.DstCx, p.DstW,
                    layer.Transform.FlipHorizontal, src.Width, p.OneToOne, p.SourceOffsetX);
                BuildAxis(
                    tables.AsSpan(cols, rows), p.MinY, rows, p.DstCy, p.DstH,
                    layer.Transform.FlipVertical, src.Height, p.OneToOne, p.SourceOffsetY);
                UploadTables(tables);
            }
            else
            {
                // The rotated mapping is not separable, so there is nothing to tabulate. The buffer
                // still has to be bound; one element is enough.
                UploadTables([new AxisEntry { X0 = -1 }]);
            }

            WriteConstants(new Constants
            {
                CanvasW = (uint)canvasW,
                CanvasH = (uint)canvasH,
                MinX = (uint)p.MinX,
                MinY = (uint)p.MinY,
                Cols = (uint)cols,
                Rows = (uint)rows,
                RowOffset = p.HasRotation ? 0u : (uint)cols,
                SrcW = (uint)src.Width,
                SrcH = (uint)src.Height,
                IsBgra = p.Bgra ? 1u : 0u,
                BlendMode = (uint)p.BlendMode,
                OpacityIsOne = p.Opacity >= 1.0 ? 1u : 0u,
                Opacity = (float)p.Opacity,
                UseTables = p.HasRotation ? 0u : area ? 2u : 1u,
                DstCx = (float)p.DstCx,
                DstCy = (float)p.DstCy,
                DstW = (float)p.DstW,
                DstH = (float)p.DstH,
                CosR = (float)p.CosR,
                SinR = (float)p.SinR,
                FlipH = layer.Transform.FlipHorizontal ? 1u : 0u,
                FlipV = layer.Transform.FlipVertical ? 1u : 0u,
                FootprintX = p.HasRotation && Minification.NeedsRotatedArea(src.Width, src.Height, p.DstW, p.DstH)
                    ? (float)(src.Width / p.DstW * (Math.Abs(p.CosR) + Math.Abs(p.SinR))) : 0,
                FootprintY = p.HasRotation && Minification.NeedsRotatedArea(src.Width, src.Height, p.DstW, p.DstH)
                    ? (float)(src.Height / p.DstH * (Math.Abs(p.CosR) + Math.Abs(p.SinR))) : 0,
            });

            context.CSSetShaderResource(0, _sourceSrv);
            context.CSSetShaderResource(1, _tableSrv);
            context.Dispatch(((uint)cols + 7) / 8, ((uint)rows + 7) / 8, 1);
        }

        context.CSSetShaderResource(0, null);
        context.CSSetShaderResource(1, null);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShader(null);

        // Scope 5: the frame still leaves the GPU into managed memory in this package. Removing this
        // readback (and the per-layer upload above it) is WP-216.
        var output = OutputPool?.Rent(canvasW, canvasH, "rgba")
                     ?? new VideoFrame(canvasW, canvasH, new byte[pixels * 4], "rgba");
        try
        {
            context.CopyResource(_canvasStaging!, _canvasBuffer!);
            var mapped = context.Map(_canvasStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                new ReadOnlySpan<byte>((void*)mapped.DataPointer, pixels * 4).CopyTo(output.Data);
            }
            finally
            {
                context.Unmap(_canvasStaging!, 0);
            }
        }
        catch
        {
            output.Dispose();
            throw;
        }

        return output;
    }

    private unsafe void UploadSource(VideoFrame src)
    {
        var pixels = checked(src.Width * src.Height);
        if (_sourceBuffer is null || _sourceCapacityPixels < pixels)
        {
            _sourceSrv?.Dispose();
            _sourceBuffer?.Dispose();
            _sourceCapacityPixels = pixels;
            _sourceBuffer = _device!.CreateBuffer(new BufferDescription(
                (uint)(pixels * sizeof(uint)),
                BindFlags.ShaderResource,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write,
                ResourceOptionFlags.BufferStructured,
                structureByteStride: sizeof(uint)));
            _sourceSrv = CreateStructuredSrv(_sourceBuffer, pixels);
        }

        // The frame is already packed r,g,b,a little-endian, which is exactly the uint layout the
        // shader unpacks — so this is one memcpy, not a per-pixel repack.
        var bytes = MemoryMarshal.Cast<byte, uint>(src.Data.AsSpan(0, pixels * 4));
        var mapped = _context!.Map(_sourceBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            bytes.CopyTo(new Span<uint>((void*)mapped.DataPointer, pixels));
        }
        finally
        {
            _context.Unmap(_sourceBuffer!, 0);
        }
    }

    private unsafe void UploadTables(ReadOnlySpan<AxisEntry> entries)
    {
        if (_tableBuffer is null || _tableCapacity < entries.Length)
        {
            _tableSrv?.Dispose();
            _tableBuffer?.Dispose();
            _tableCapacity = entries.Length;
            _tableBuffer = _device!.CreateBuffer(new BufferDescription(
                (uint)(entries.Length * sizeof(AxisEntry)),
                BindFlags.ShaderResource,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write,
                ResourceOptionFlags.BufferStructured,
                structureByteStride: (uint)sizeof(AxisEntry)));
            _tableSrv = CreateStructuredSrv(_tableBuffer, entries.Length);
        }

        var mapped = _context!.Map(_tableBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            entries.CopyTo(new Span<AxisEntry>((void*)mapped.DataPointer, entries.Length));
        }
        finally
        {
            _context.Unmap(_tableBuffer!, 0);
        }
    }

    private unsafe void WriteConstants(in Constants constants)
    {
        var mapped = _context!.Map(_constantBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            *(Constants*)mapped.DataPointer = constants;
        }
        finally
        {
            _context.Unmap(_constantBuffer!, 0);
        }
    }

    /// <summary>
    /// Canvas buffer, UAV and staging are published as a set or not at all. A partially built canvas
    /// must never be reachable: the cache test below is <c>_canvasBuffer is not null</c>, so if the
    /// buffer were published before the UAV and a later allocation threw, the next frame would take
    /// the cache hit, bind a disposed view and read back a buffer nothing had written — a garbage
    /// frame, recorded as a GPU frame. Build into locals, publish once, and on failure leave the
    /// fields null so the next frame rebuilds from scratch or falls back honestly.
    /// </summary>
    private void EnsureCanvas(int pixels)
    {
        if (_canvasBuffer is not null && _canvasPixels == pixels) return;

        DisposeCanvas();

        ID3D11Buffer? buffer = null;
        ID3D11UnorderedAccessView? uav = null;
        ID3D11Buffer? staging = null;
        try
        {
            buffer = _device!.CreateBuffer(new BufferDescription(
                (uint)(pixels * sizeof(uint)),
                BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.BufferStructured,
                structureByteStride: sizeof(uint)));
            uav = _device.CreateUnorderedAccessView(buffer, new UnorderedAccessViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)pixels },
            });
            staging = _device.CreateBuffer(new BufferDescription(
                (uint)(pixels * sizeof(uint)),
                BindFlags.None,
                ResourceUsage.Staging,
                CpuAccessFlags.Read));
        }
        catch
        {
            staging?.Dispose();
            uav?.Dispose();
            buffer?.Dispose();
            throw;
        }

        _canvasBuffer = buffer;
        _canvasUav = uav;
        _canvasStaging = staging;
        _canvasPixels = pixels;
    }

    private void DisposeCanvas()
    {
        _canvasStaging?.Dispose(); _canvasStaging = null;
        _canvasUav?.Dispose(); _canvasUav = null;
        _canvasBuffer?.Dispose(); _canvasBuffer = null;
        _canvasPixels = 0;
    }

    private ID3D11ShaderResourceView CreateStructuredSrv(ID3D11Buffer buffer, int elements)
        => _device!.CreateShaderResourceView(buffer, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)elements },
        });

    private void EnsureDevice()
    {
        if (_initAttempted) return;
        lock (_gate)
        {
            if (_initAttempted) return;
            _initAttempted = true;

            // T2 round 2 (P1): guard on _disposed, not only on _initAttempted. Dispose is one-shot,
            // so a device built after it has returned would never be released — and that is a
            // reachable order, not a theoretical one: PreviewHostControl.Bind disposes the outgoing
            // provider while a decode may still be in BuildMediaLayersAsync, i.e. before this
            // compositor's first frame, with _initAttempted still false. The frame is still produced,
            // on the reference path, and the reason says why.
            if (_disposed)
            {
                _lastGpuError = "compositor disposed before its first frame";
                return;
            }

            var forced = Environment.GetEnvironmentVariable("TEMNO_COMPOSITOR");
            if (string.Equals(forced, "cpu", StringComparison.OrdinalIgnoreCase))
            {
                _lastGpuError = "disabled by TEMNO_COMPOSITOR=cpu";
                return;
            }

            if (!_caps.D3D11Available)
            {
                _lastGpuError = _caps.D3D11Error ?? "D3D11 unavailable";
                return;
            }

            try
            {
                var hr = D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0],
                    out _device,
                    out _context);
                if (!hr.Success || _device is null || _context is null)
                {
                    _lastGpuError = "D3D11CreateDevice failed: " + hr;
                    return;
                }

                _compositeShader = CompileShader("CSComposite");
                _clearShader = CompileShader("CSClear");
                _constantBuffer = _device.CreateBuffer(new BufferDescription(
                    (uint)Marshal.SizeOf<Constants>(),
                    BindFlags.ConstantBuffer,
                    ResourceUsage.Dynamic,
                    CpuAccessFlags.Write));

                try
                {
                    using var dxgi = _device.QueryInterface<IDXGIDevice>();
                    using var adapter = dxgi.GetAdapter();
                    _adapterDescription = adapter.Description.Description?.Trim();
                }
                catch
                {
                    _adapterDescription = "D3D11 device";
                }

                _gpuReady = true;
            }
            catch (Exception ex)
            {
                _lastGpuError = ex.GetType().Name + ": " + ex.Message;
                DisposeDevice();
            }
        }
    }

    private ID3D11ComputeShader CompileShader(string entryPoint)
    {
        var bytecode = entryPoint == "CSComposite" ? CompositeBytecode.Value : ClearBytecode.Value;
        return _device!.CreateComputeShader(bytecode.AsSpan());
    }

    /// <summary>
    /// Called after a dispatch threw. Two separate problems, and conflating them would be wrong in
    /// both directions. (a) Whatever was cached may be half-built or stale, so drop it — the next
    /// frame rebuilds. (b) If the <em>device</em> is gone, rebuilding cannot succeed and retrying it
    /// every frame buys nothing but the cost of failing; tear it down so <c>_gpuReady</c> is false
    /// and every later frame takes the reference path directly. A transient allocation refusal is
    /// not a removed device and keeps its retry.
    /// </summary>
    private void InvalidateAfterFailure()
    {
        if (_disposed) return;

        var deviceLost = false;
        try { deviceLost = _device is not null && _device.DeviceRemovedReason.Failure; }
        catch { deviceLost = true; }

        if (deviceLost)
        {
            // _initAttempted stays true, so EnsureDevice will not rebuild: _gpuReady is false for
            // the rest of this compositor's life and every later frame goes straight to reference.
            DisposeDevice();
            return;
        }

        DisposeCanvas();
        _sourceSrv?.Dispose(); _sourceSrv = null;
        _sourceBuffer?.Dispose(); _sourceBuffer = null;
        _sourceCapacityPixels = 0;
        _tableSrv?.Dispose(); _tableSrv = null;
        _tableBuffer?.Dispose(); _tableBuffer = null;
        _tableCapacity = 0;
    }

    private void DisposeDevice()
    {
        DisposeCanvas();
        _sourceSrv?.Dispose(); _sourceSrv = null;
        _sourceBuffer?.Dispose(); _sourceBuffer = null;
        _sourceCapacityPixels = 0;
        _tableSrv?.Dispose(); _tableSrv = null;
        _tableBuffer?.Dispose(); _tableBuffer = null;
        _tableCapacity = 0;
        _constantBuffer?.Dispose(); _constantBuffer = null;
        _compositeShader?.Dispose(); _compositeShader = null;
        _clearShader?.Dispose(); _clearShader = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
        _gpuReady = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) DisposeDevice();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public uint CanvasW;
        public uint CanvasH;
        public uint MinX;
        public uint MinY;

        public uint Cols;
        public uint Rows;
        public uint RowOffset;
        public uint SrcW;

        public uint SrcH;
        public uint IsBgra;
        public uint BlendMode;
        public uint OpacityIsOne;

        public float Opacity;
        public uint UseTables;
        public float DstCx;
        public float DstCy;

        public float DstW;
        public float DstH;
        public float CosR;
        public float SinR;

        public uint FlipH;
        public uint FlipV;
        public float FootprintX;
        public float FootprintY;
    }

    /// <summary>
    /// Mirrors <see cref="CpuCompositor"/> and <see cref="CpuBlendModes"/> statement for statement.
    /// The blend-mode numbering is <see cref="CpuBlendModes.Kind"/>'s declaration order and is cast
    /// directly from it, so reordering that enum silently repaints every non-normal layer.
    /// </summary>
    private const string CompositeHlsl = """
RWStructuredBuffer<uint> Canvas : register(u0);
StructuredBuffer<uint> Source   : register(t0);
struct AxisEntry { int X0; int X1; int W; int Pad; };
StructuredBuffer<AxisEntry> Tables : register(t1);

cbuffer Params : register(b0)
{
    uint CanvasW; uint CanvasH; uint MinX; uint MinY;
    uint Cols; uint Rows; uint RowOffset; uint SrcW;
    uint SrcH; uint IsBgra; uint BlendMode; uint OpacityIsOne;
    float Opacity; uint UseTables; float DstCx; float DstCy;
    float DstW; float DstH; float CosR; float SinR;
    uint FlipH; uint FlipV; float FootprintX; float FootprintY;
};

static const int WeightScale = 65536;

[numthreads(8, 8, 1)]
void CSClear(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= CanvasW || id.y >= CanvasH) return;
    Canvas[id.y * CanvasW + id.x] = 0xFF000000;
}

// ReadRgba: the frame is packed little-endian, so byte 0 is Data[i], byte 3 is Data[i+3].
int4 ReadRgba(uint x, uint y)
{
    uint p = Source[y * SrcW + x];
    int b0 = (int)(p & 255);
    int b1 = (int)((p >> 8) & 255);
    int b2 = (int)((p >> 16) & 255);
    int b3 = (int)((p >> 24) & 255);
    return (IsBgra != 0) ? int4(b2, b1, b0, b3) : int4(b0, b1, b2, b3);
}

// Exact 32-bit integer lerp: identical to CpuCompositor.LerpFixed, including the truncating shift.
int LerpFixed(int a, int b, int w) { return (a * (WeightScale - w) + b * w) >> 16; }

// Truncating double-lerp of CpuCompositor.LerpByte, in float. Used by the rotated path only.
int LerpByte(int a, int b, float t) { return (int)clamp(a + (b - a) * t, 0.0, 255.0); }

int AreaQuantize(float value) { return (int)clamp(value * WeightScale + 0.5, 0.0, (float)WeightScale); }
void AreaAxis(float center, float width, int extent, out int first, out int last, out float left, out int bw)
{
    bw = -1;
    left = center - width / 2;
    if (width <= 1)
    {
        int raw = (int)floor(center - 0.5);
        first = clamp(raw, 0, extent - 1);
        last = min(first + 1, extent - 1);
        bw = AreaQuantize(center - 0.5 - raw);
    }
    else
    {
        first = (int)clamp(floor(left), 0.0, (float)(extent - 1));
        last = (int)clamp(ceil(center + width / 2) - 1, 0.0, (float)(extent - 1));
    }
}
int AreaWeight(int index, int first, int last, float left, float width, int bw)
{
    if (bw >= 0) return first == last ? WeightScale : index == first ? WeightScale - bw : bw;
    if (isinf(width)) return first == last ? WeightScale : index == first || index == last ? WeightScale / 2 : 0;
    int previous = index == first ? 0 : AreaQuantize((index - left) / width);
    int next = index == last ? WeightScale : AreaQuantize((index + 1 - left) / width);
    return next - previous;
}
int4 SampleArea(float centerX, float centerY)
{
    int x0, x1, y0, y1, bx, by;
    float left, top;
    AreaAxis(centerX, FootprintX, (int)SrcW, x0, x1, left, bx);
    AreaAxis(centerY, FootprintY, (int)SrcH, y0, y1, top, by);
    int4 sum = 0;
    [loop] for (int y = y0; y <= y1; y++)
    {
        int wy = AreaWeight(y, y0, y1, top, FootprintY, by);
        int4 row = 0;
        [loop] for (int x = x0; x <= x1; x++)
            row += ReadRgba((uint)x, (uint)y) * AreaWeight(x, x0, x1, left, FootprintX, bx);
        sum += (row >> 16) * wy;
    }
    return sum >> 16;
}

float Screen1(float b, float s)      { return 1 - (1 - b) * (1 - s); }
float ColorDodge1(float b, float s)  { return s >= 1 ? 1 : min(1, b / (1 - s)); }
float ColorBurn1(float b, float s)   { return s <= 0 ? 0 : 1 - min(1, (1 - b) / s); }
float Overlay1(float b, float s)     { return b <= 0.5 ? 2 * b * s : 1 - 2 * (1 - b) * (1 - s); }
float Exclusion1(float b, float s)   { return b + s - 2 * b * s; }
float SoftLight1(float b, float s)
{
    if (s <= 0.5) return b - (1 - 2 * s) * b * (1 - b);
    float d = (b <= 0.25) ? (((16 * b - 12) * b + 4) * b) : sqrt(b);
    return b + (2 * s - 1) * (d - b);
}

float Lum3(float3 c) { return 0.3 * c.r + 0.59 * c.g + 0.11 * c.b; }
float Sat3(float3 c) { return max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b)); }

float3 ClipColor(float3 c)
{
    // lum, mn and mx are read from the ORIGINAL colour for both clamps, as in CpuBlendModes.
    float l = Lum3(c);
    float mn = min(c.r, min(c.g, c.b));
    float mx = max(c.r, max(c.g, c.b));
    if (mn < 0) c = l + ((c - l) * l) / (l - mn);
    if (mx > 1) c = l + ((c - l) * (1 - l)) / (mx - l);
    return c;
}

float3 SetLum(float3 c, float l) { return ClipColor(c + (l - Lum3(c))); }

float3 SetSat(float3 color, float saturation)
{
    float v[3] = { color.r, color.g, color.b };
    int minIndex = 0;
    int maxIndex = 0;
    for (int i = 1; i < 3; i++)
    {
        if (v[i] < v[minIndex]) minIndex = i;
        if (v[i] > v[maxIndex]) maxIndex = i;
    }
    // R == G == B has no distinct mid channel; the CPU path used to throw here.
    if (minIndex == maxIndex) return float3(0, 0, 0);

    int midIndex = 3 - minIndex - maxIndex;
    float mn = v[minIndex];
    float mx = v[maxIndex];
    if (mx > mn)
    {
        v[midIndex] = ((v[midIndex] - mn) * saturation) / (mx - mn);
        v[maxIndex] = saturation;
    }
    else
    {
        v[midIndex] = 0;
        v[maxIndex] = 0;
    }
    v[minIndex] = 0;
    return float3(v[0], v[1], v[2]);
}

float3 ApplyBlend(uint mode, float3 bg, float3 s)
{
    if (mode == 0)  return s;                                        // normal
    if (mode == 1)  return min(bg, s);                               // darken
    if (mode == 2)  return bg * s;                                   // multiply
    if (mode == 3)  return float3(ColorBurn1(bg.r, s.r), ColorBurn1(bg.g, s.g), ColorBurn1(bg.b, s.b));
    if (mode == 4)  return max(bg, s);                               // lighten
    if (mode == 5)  return float3(Screen1(bg.r, s.r), Screen1(bg.g, s.g), Screen1(bg.b, s.b));
    if (mode == 6)  return float3(ColorDodge1(bg.r, s.r), ColorDodge1(bg.g, s.g), ColorDodge1(bg.b, s.b));
    if (mode == 7)  return float3(Overlay1(bg.r, s.r), Overlay1(bg.g, s.g), Overlay1(bg.b, s.b));
    if (mode == 8)  return float3(SoftLight1(bg.r, s.r), SoftLight1(bg.g, s.g), SoftLight1(bg.b, s.b));
    if (mode == 9)  return float3(Overlay1(s.r, bg.r), Overlay1(s.g, bg.g), Overlay1(s.b, bg.b)); // hardLight
    if (mode == 10) return abs(bg - s);                              // difference
    if (mode == 11) return float3(Exclusion1(bg.r, s.r), Exclusion1(bg.g, s.g), Exclusion1(bg.b, s.b));
    if (mode == 12) return SetLum(SetSat(s, Sat3(bg)), Lum3(bg));    // hue
    if (mode == 13) return SetLum(SetSat(bg, Sat3(s)), Lum3(bg));    // saturation
    if (mode == 14) return SetLum(s, Lum3(bg));                      // color
    if (mode == 15) return SetLum(bg, Lum3(s));                      // luminosity
    if (mode == 16) return min(1.0.xxx, bg + s);                     // add
    return s;
}

[numthreads(8, 8, 1)]
void CSComposite(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Cols || id.y >= Rows) return;
    uint cx = MinX + id.x;
    uint cy = MinY + id.y;

    int4 s;
    if (UseTables == 2)
    {
        AxisEntry col = Tables[id.x];
        AxisEntry row = Tables[RowOffset + id.y];
        if (col.X0 < 0 || row.X0 < 0) return;
        int4 sum = 0;
        [loop] for (int y = 0; y < row.X1; y++)
        {
            AxisEntry ty = Tables[row.X0 + y];
            int4 horizontal = 0;
            [loop] for (int x = 0; x < col.X1; x++)
            {
                AxisEntry tx = Tables[col.X0 + x];
                horizontal += ReadRgba((uint)tx.X0, (uint)ty.X0) * tx.W;
            }
            sum += (horizontal >> 16) * ty.W;
        }
        s = sum >> 16;
    }
    else if (UseTables != 0)
    {
        AxisEntry col = Tables[id.x];
        if (col.X0 < 0) return;
        AxisEntry row = Tables[RowOffset + id.y];
        if (row.X0 < 0) return;

        int4 p00 = ReadRgba((uint)col.X0, (uint)row.X0);
        int4 p10 = ReadRgba((uint)col.X1, (uint)row.X0);
        int4 p01 = ReadRgba((uint)col.X0, (uint)row.X1);
        int4 p11 = ReadRgba((uint)col.X1, (uint)row.X1);

        int4 t0, t1;
        t0.x = LerpFixed(p00.x, p10.x, col.W); t1.x = LerpFixed(p01.x, p11.x, col.W);
        t0.y = LerpFixed(p00.y, p10.y, col.W); t1.y = LerpFixed(p01.y, p11.y, col.W);
        t0.z = LerpFixed(p00.z, p10.z, col.W); t1.z = LerpFixed(p01.z, p11.z, col.W);
        t0.w = LerpFixed(p00.w, p10.w, col.W); t1.w = LerpFixed(p01.w, p11.w, col.W);

        s.x = LerpFixed(t0.x, t1.x, row.W);
        s.y = LerpFixed(t0.y, t1.y, row.W);
        s.z = LerpFixed(t0.z, t1.z, row.W);
        s.w = LerpFixed(t0.w, t1.w, row.W);
    }
    else
    {
        float relX = cx + 0.5 - DstCx;
        float relY = cy + 0.5 - DstCy;
        float rx = relX * CosR - relY * SinR;
        float ry = relX * SinR + relY * CosR;
        float normX = rx / DstW + 0.5;
        float normY = ry / DstH + 0.5;
        if (normX < 0 || normX >= 1 || normY < 0 || normY >= 1) return;
        if (FlipH != 0) normX = 1.0 - normX;
        if (FlipV != 0) normY = 1.0 - normY;

        if (FootprintX > 0)
            s = SampleArea(normX * SrcW, normY * SrcH);
        else
        {
        float sx = normX * SrcW - 0.5;
        float sy = normY * SrcH - 0.5;
        int x0raw = (int)floor(sx);
        int y0raw = (int)floor(sy);
        float fx = sx - x0raw;
        float fy = sy - y0raw;
        int x0 = clamp(x0raw, 0, (int)SrcW - 1);
        int y0 = clamp(y0raw, 0, (int)SrcH - 1);
        int x1 = min(x0 + 1, (int)SrcW - 1);
        int y1 = min(y0 + 1, (int)SrcH - 1);

        int4 p00 = ReadRgba((uint)x0, (uint)y0);
        int4 p10 = ReadRgba((uint)x1, (uint)y0);
        int4 p01 = ReadRgba((uint)x0, (uint)y1);
        int4 p11 = ReadRgba((uint)x1, (uint)y1);

        s.x = LerpByte(LerpByte(p00.x, p10.x, fx), LerpByte(p01.x, p11.x, fx), fy);
        s.y = LerpByte(LerpByte(p00.y, p10.y, fx), LerpByte(p01.y, p11.y, fx), fy);
        s.z = LerpByte(LerpByte(p00.z, p10.z, fx), LerpByte(p01.z, p11.z, fx), fy);
        s.w = LerpByte(LerpByte(p00.w, p10.w, fx), LerpByte(p01.w, p11.w, fx), fy);
        }
    }

    // Shared per-pixel tail, statement for statement with CpuCompositor.
    float alpha = (s.w / 255.0) * Opacity;
    if (alpha <= 0) return;

    uint idx = cy * CanvasW + cx;

    // Decided host-side in double (OpacityIsOne) so both paths take this branch on the same pixels.
    if (OpacityIsOne != 0 && s.w == 255 && BlendMode == 0)
    {
        Canvas[idx] = (uint)s.x | ((uint)s.y << 8) | ((uint)s.z << 16) | (255u << 24);
        return;
    }

    uint packed = Canvas[idx];
    float3 bg = float3((packed & 255) / 255.0, ((packed >> 8) & 255) / 255.0, ((packed >> 16) & 255) / 255.0);
    float3 blended = ApplyBlend(BlendMode, bg, float3(s.x / 255.0, s.y / 255.0, s.z / 255.0));

    float invAlpha = 1.0 - alpha;
    float3 outColor = blended * alpha + bg * invAlpha;
    uint R = (uint)clamp(outColor.r * 255, 0.0, 255.0);
    uint G = (uint)clamp(outColor.g * 255, 0.0, 255.0);
    uint B = (uint)clamp(outColor.b * 255, 0.0, 255.0);
    Canvas[idx] = R | (G << 8) | (B << 16) | (255u << 24);
}
""";
}
