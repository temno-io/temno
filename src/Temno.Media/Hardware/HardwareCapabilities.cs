using System.Runtime.InteropServices;
using System.Text;
using Temno.Core.Models;
using Temno.Media.Export;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Temno.Media.Hardware;

/// <summary>
/// Detected GPU / encoder capabilities for WP-114. Probe is cached; never throws for missing HW.
/// </summary>
public sealed class HardwareCapabilities
{
    private static readonly object Gate = new();
    private static HardwareCapabilities? _cached;

    public bool IsWindows { get; init; }
    public bool D3D11Available { get; init; }
    public string? D3D11AdapterDescription { get; init; }
    public string? D3D11Error { get; init; }

    public bool NvencH264 { get; init; }
    public bool NvencHevc { get; init; }
    public bool LibX264 { get; init; }
    public bool LibX265 { get; init; }
    public bool ProRes { get; init; }

    public IReadOnlyList<string> AvailableEncoders { get; init; } = [];

    public bool CanHardwareEncode(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => NvencH264,
        VideoCodec.H265 or VideoCodec.HevcHdr => NvencHevc,
        _ => false,
    };

    public bool CanGpuColorEffects => D3D11Available;

    public string Summary
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append("os=").Append(IsWindows ? "windows" : "other");
            sb.Append("; d3d11=").Append(D3D11Available ? "yes" : "no");
            if (D3D11AdapterDescription is { Length: > 0 } desc)
                sb.Append(" (").Append(desc).Append(')');
            if (D3D11Error is { Length: > 0 })
                sb.Append(" [").Append(D3D11Error).Append(']');
            sb.Append("; nvenc_h264=").Append(NvencH264 ? "yes" : "no");
            sb.Append("; nvenc_hevc=").Append(NvencHevc ? "yes" : "no");
            sb.Append("; libx264=").Append(LibX264 ? "yes" : "no");
            sb.Append("; libx265=").Append(LibX265 ? "yes" : "no");
            return sb.ToString();
        }
    }

    public static HardwareCapabilities Probe(bool forceRefresh = false)
    {
        lock (Gate)
        {
            if (!forceRefresh && _cached is not null)
                return _cached;
            _cached = ProbeCore();
            return _cached;
        }
    }

    public static void ResetCache()
    {
        lock (Gate) _cached = null;
        FfmpegEncoderCapabilities.ResetCache();
    }

    private static HardwareCapabilities ProbeCore()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var d3d = false;
        string? adapter = null;
        string? d3dError = null;

        if (isWindows)
        {
            try
            {
                var result = D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0, FeatureLevel.Level_10_0],
                    out ID3D11Device? device,
                    out ID3D11DeviceContext? context);
                if (result.Success && device is not null)
                {
                    d3d = true;
                    try
                    {
                        using var dxgi = device.QueryInterface<IDXGIDevice>();
                        using var adapterObj = dxgi.GetAdapter();
                        adapter = adapterObj.Description.Description?.Trim();
                    }
                    catch
                    {
                        adapter = "D3D11 device";
                    }
                    context?.Dispose();
                    device.Dispose();
                }
                else
                {
                    d3dError = result.ToString();
                }
            }
            catch (Exception ex)
            {
                d3dError = ex.GetType().Name + ": " + ex.Message;
            }
        }
        else
        {
            d3dError = "non-windows";
        }

        // Ensure encoder set is populated.
        _ = FfmpegEncoderCapabilities.IsEncoderAvailable("libx264");

        return new HardwareCapabilities
        {
            IsWindows = isWindows,
            D3D11Available = d3d,
            D3D11AdapterDescription = adapter,
            D3D11Error = d3dError,
            NvencH264 = FfmpegEncoderCapabilities.IsEncoderAvailable("h264_nvenc"),
            NvencHevc = FfmpegEncoderCapabilities.IsEncoderAvailable("hevc_nvenc"),
            LibX264 = FfmpegEncoderCapabilities.IsEncoderAvailable("libx264"),
            LibX265 = FfmpegEncoderCapabilities.IsEncoderAvailable("libx265"),
            ProRes = FfmpegEncoderCapabilities.IsEncoderAvailable("prores_ks")
                     || FfmpegEncoderCapabilities.IsEncoderAvailable("prores"),
            AvailableEncoders = CollectEncoders(),
        };
    }

    private static List<string> CollectEncoders()
    {
        var names = new[]
        {
            "libx264", "libopenh264", "h264_nvenc", "h264_qsv", "h264_amf",
            "libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf", "prores_ks", "prores",
        };
        return names.Where(FfmpegEncoderCapabilities.IsEncoderAvailable).ToList();
    }
}

/// <summary>How export/preview should choose between software and hardware paths.</summary>
public enum HardwareAccelerationMode
{
    /// <summary>CPU effects + software-first encoders (default, deterministic).</summary>
    Software,

    /// <summary>Use D3D11 color effects when available; fall back to CPU. Prefer NVENC when present.</summary>
    PreferHardware,

    /// <summary>Require hardware where possible; still fall back to CPU for unsupported effect types.</summary>
    ForceHardwareWhereAvailable,
}
