using System.Runtime.InteropServices;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Media.Hardware;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;

namespace Temno.Media.Effects;

/// <summary>
/// D3D11 compute path for <c>color.exposure</c>, <c>color.contrast</c>, and <c>color.saturation</c>
/// with deterministic fall-through to the CPU oracle for any other stack or device failure (WP-114).
/// </summary>
public sealed class GpuEffectsRenderer : IFrameEffectsRenderer, IDisposable
{
    public static readonly HashSet<string> GpuSupportedTypes = new(StringComparer.Ordinal)
    {
        "color.exposure",
        "color.contrast",
        "color.saturation",
    };

    private readonly CpuEffectsRenderer _cpuOracle;
    private readonly HardwareCapabilities _caps;
    private readonly object _gate = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _shader;
    private bool _initAttempted;
    private bool _gpuReady;
    private string? _lastGpuError;

    public GpuEffectsRenderer(CpuEffectsRenderer? cpuOracle = null, HardwareCapabilities? caps = null)
    {
        _cpuOracle = cpuOracle ?? new CpuEffectsRenderer();
        _caps = caps ?? HardwareCapabilities.Probe();
    }

    public bool IsGpuReady
    {
        get { EnsureDevice(); return _gpuReady; }
    }

    public string? LastGpuError
    {
        get { EnsureDevice(); return _lastGpuError; }
    }

    public static bool CanAccelerate(IReadOnlyList<Effect>? effects)
    {
        if (effects is null || effects.Count == 0) return true;
        foreach (var e in effects)
        {
            if (!e.Enabled) continue;
            if (!GpuSupportedTypes.Contains(e.Type)) return false;
        }
        return true;
    }

    public VideoFrame Apply(VideoFrame source, IReadOnlyList<Effect>? effects, int clipRelativeFrame)
    {
        // An empty or fully-bypassed stack is identity: the oracle returns the source frame
        // without cloning it, so skip device init entirely.
        if (!CpuEffectsRenderer.HasEnabledEffect(effects) || !CanAccelerate(effects))
            return _cpuOracle.Apply(source, effects, clipRelativeFrame);

        EnsureDevice();
        if (!_gpuReady || _device is null || _context is null || _shader is null)
            return _cpuOracle.Apply(source, effects, clipRelativeFrame);

        try
        {
            return ApplyGpu(source, effects, clipRelativeFrame);
        }
        catch (Exception ex)
        {
            _lastGpuError = ex.Message;
            return _cpuOracle.Apply(source, effects, clipRelativeFrame);
        }
    }

    private VideoFrame ApplyGpu(VideoFrame source, IReadOnlyList<Effect> effects, int clipRelativeFrame)
    {
        var format = source.PixelFormat.Trim();
        var isRgba = string.Equals(format, "rgba", StringComparison.OrdinalIgnoreCase);
        var isBgra = string.Equals(format, "bgra", StringComparison.OrdinalIgnoreCase);
        if (!isRgba && !isBgra)
            return _cpuOracle.Apply(source, effects, clipRelativeFrame);

        var pixelCount = checked(source.Width * source.Height);
        var required = checked(pixelCount * 4);
        if (source.Data.Length < required)
            throw new ArgumentException("Frame data too small.", nameof(source));

        var ops = new GpuOp[8];
        var opCount = ResolveGpuOps(effects, clipRelativeFrame, ops);
        if (opCount == 0)
            return _cpuOracle.Apply(source, effects, clipRelativeFrame);

        var output = (byte[])source.Data.Clone();
        var constants = new GpuConstants
        {
            Width = (uint)source.Width,
            Height = (uint)source.Height,
            IsBgra = isBgra ? 1u : 0u,
            OpCount = (uint)opCount,
            Op0 = ops[0],
            Op1 = ops[1],
            Op2 = ops[2],
            Op3 = ops[3],
            Op4 = ops[4],
            Op5 = ops[5],
            Op6 = ops[6],
            Op7 = ops[7],
        };

        lock (_gate)
            Dispatch(output, in constants);

        return new VideoFrame(source.Width, source.Height, output, source.PixelFormat);
    }

    private void Dispatch(byte[] pixels, in GpuConstants constants)
    {
        var device = _device!;
        var context = _context!;
        var pixelCount = (int)(constants.Width * constants.Height);
        var packed = new uint[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            packed[i] = (uint)(pixels[o] | (pixels[o + 1] << 8) | (pixels[o + 2] << 16) | (pixels[o + 3] << 24));
        }

        var bufferDesc = new BufferDescription(
            (uint)(packed.Length * sizeof(uint)),
            BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured,
            structureByteStride: sizeof(uint));

        using var buffer = device.CreateBuffer(packed.AsSpan(), bufferDesc);
        using var uav = device.CreateUnorderedAccessView(buffer, new UnorderedAccessViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView
            {
                FirstElement = 0,
                NumElements = (uint)packed.Length,
            },
        });

        // Constant buffers must be 16-byte multiples.
        var cbBytes = ((Marshal.SizeOf<GpuConstants>() + 15) / 16) * 16;
        var cbDesc = new BufferDescription(
            (uint)cbBytes,
            BindFlags.ConstantBuffer,
            ResourceUsage.Default);
        using var cbuf = device.CreateBuffer(in constants, cbDesc);

        context.CSSetShader(_shader);
        context.CSSetUnorderedAccessView(0, uav);
        context.CSSetConstantBuffer(0, cbuf);

        var groupsX = (constants.Width + 7) / 8;
        var groupsY = (constants.Height + 7) / 8;
        context.Dispatch(groupsX, groupsY, 1);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShader(null);

        var stagingDesc = new BufferDescription(
            (uint)(packed.Length * sizeof(uint)),
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read);
        using var staging = device.CreateBuffer(stagingDesc);
        context.CopyResource(staging, buffer);

        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                var src = (uint*)mapped.DataPointer;
                for (var i = 0; i < packed.Length; i++)
                    packed[i] = src[i];
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }

        for (var i = 0; i < pixelCount; i++)
        {
            var v = packed[i];
            var o = i * 4;
            pixels[o] = (byte)(v & 0xFF);
            pixels[o + 1] = (byte)((v >> 8) & 0xFF);
            pixels[o + 2] = (byte)((v >> 16) & 0xFF);
            pixels[o + 3] = (byte)((v >> 24) & 0xFF);
        }
    }

    private void EnsureDevice()
    {
        if (_initAttempted) return;
        lock (_gate)
        {
            if (_initAttempted) return;
            _initAttempted = true;
            if (!_caps.CanGpuColorEffects)
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

                var bytecode = Compiler.Compile(
                    ColorComputeHlsl,
                    entryPoint: "CSMain",
                    sourceName: "ColorCompute.hlsl",
                    profile: "cs_5_0");
                _shader = _device.CreateComputeShader(bytecode.Span);
                _gpuReady = true;
            }
            catch (Exception ex)
            {
                _lastGpuError = ex.Message;
                DisposeDevice();
            }
        }
    }

    private void DisposeDevice()
    {
        _shader?.Dispose();
        _shader = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _gpuReady = false;
    }

    /// <summary>
    /// WP-269: serialize disposal with in-flight renders. <c>PreviewHostControl.Bind</c> runs on
    /// every <c>RefreshAfterEdit</c> and disposes the outgoing provider's renderer while a preview
    /// render from the previous edit may still be inside <see cref="EnsureDevice"/> or
    /// <see cref="Dispatch"/> on a worker thread; disposing the D3D11 device mid-call crashed the
    /// app twice with an uncatchable <c>AccessViolationException</c> in <c>CreateComputeShader</c>
    /// (WP-269 receipt runs, Application event 1026). Every native call already holds
    /// <c>_gate</c>, so disposing under it cannot interleave one: a render that loses the race
    /// sees nulled fields and falls back to the CPU oracle through the existing null check or the
    /// <c>Apply</c> catch, both catchable. Mirrors the <c>GpuCompositor</c> gate noted in
    /// <c>PreviewHostControl.Bind</c>.
    /// </summary>
    public void Dispose()
    {
        lock (_gate) DisposeDevice();
    }

    private static int ResolveGpuOps(IReadOnlyList<Effect> effects, int frame, GpuOp[] ops)
    {
        var n = 0;
        foreach (var effect in effects)
        {
            if (!effect.Enabled || n >= ops.Length) continue;
            switch (effect.Type)
            {
                case "color.exposure":
                    ops[n++] = new GpuOp { Kind = 1, A = (float)ResolveParam(effect, "ev", frame) };
                    break;
                case "color.contrast":
                    ops[n++] = new GpuOp { Kind = 2, A = (float)ResolveParam(effect, "amount", frame) };
                    break;
                case "color.saturation":
                    ops[n++] = new GpuOp { Kind = 3, A = (float)ResolveParam(effect, "amount", frame) };
                    break;
            }
        }
        return n;
    }

    private static double ResolveParam(Effect effect, string key, int frame)
    {
        var descriptor = EffectRegistry.GetDescriptor(effect.Type)
            ?? throw new InvalidOperationException($"Missing descriptor {effect.Type}");
        var spec = descriptor.Params.Find(p => p.Key == key)
            ?? throw new InvalidOperationException($"Missing param {key}");
        var value = effect.Params is not null && effect.Params.TryGetValue(key, out var parameter)
            ? parameter.Resolved(frame, spec.DefaultValue)
            : spec.DefaultValue;
        return Math.Clamp(value, spec.Min, spec.Max);
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GpuOp
    {
        public uint Kind;
        public float A;
        public float B;
        public float C;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GpuConstants
    {
        public uint Width;
        public uint Height;
        public uint IsBgra;
        public uint OpCount;
        public GpuOp Op0, Op1, Op2, Op3, Op4, Op5, Op6, Op7;
    }

    private const string ColorComputeHlsl = """
RWStructuredBuffer<uint> Pixels : register(u0);

struct Op { uint Kind; float A; float B; float C; };

cbuffer Params : register(b0)
{
    uint Width;
    uint Height;
    uint IsBgra;
    uint OpCount;
    Op Ops[8];
};

float SrgbToLinear(float v)
{
    return (v <= 0.04045) ? (v / 12.92) : pow(abs((v + 0.055) / 1.055), 2.4);
}
float LinearToSrgb(float v)
{
    return (v <= 0.0031308) ? (v * 12.92) : (1.055 * pow(abs(v), 1.0 / 2.4) - 0.055);
}

void ApplyExposure(inout float r, inout float g, inout float b, float ev)
{
    float m = pow(2.0, ev);
    r = saturate(LinearToSrgb(SrgbToLinear(r) * m));
    g = saturate(LinearToSrgb(SrgbToLinear(g) * m));
    b = saturate(LinearToSrgb(SrgbToLinear(b) * m));
}
void ApplyContrast(inout float r, inout float g, inout float b, float amount)
{
    r = saturate((r - 0.5) * amount + 0.5);
    g = saturate((g - 0.5) * amount + 0.5);
    b = saturate((b - 0.5) * amount + 0.5);
}
void ApplySaturation(inout float r, inout float g, inout float b, float amount)
{
    float luma = r * 0.2126 + g * 0.7152 + b * 0.0722;
    r = saturate(luma + (r - luma) * amount);
    g = saturate(luma + (g - luma) * amount);
    b = saturate(luma + (b - luma) * amount);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height) return;
    uint idx = id.y * Width + id.x;
    uint packed = Pixels[idx];
    float r = (packed & 255) / 255.0;
    float g = ((packed >> 8) & 255) / 255.0;
    float b = ((packed >> 16) & 255) / 255.0;
    float a = ((packed >> 24) & 255) / 255.0;
    if (IsBgra != 0) { float t = r; r = b; b = t; }

    [loop]
    for (uint i = 0; i < OpCount && i < 8; i++)
    {
        Op op = Ops[i];
        if (op.Kind == 1) ApplyExposure(r, g, b, op.A);
        else if (op.Kind == 2) ApplyContrast(r, g, b, op.A);
        else if (op.Kind == 3) ApplySaturation(r, g, b, op.A);
    }

    if (IsBgra != 0) { float t = r; r = b; b = t; }
    uint R = (uint)(saturate(r) * 255.0 + 0.5);
    uint G = (uint)(saturate(g) * 255.0 + 0.5);
    uint B = (uint)(saturate(b) * 255.0 + 0.5);
    uint A = (uint)(saturate(a) * 255.0 + 0.5);
    Pixels[idx] = R | (G << 8) | (B << 16) | (A << 24);
}
""";
}
