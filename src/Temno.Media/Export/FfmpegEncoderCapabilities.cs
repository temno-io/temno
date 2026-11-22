using System.Diagnostics;
using System.Text.RegularExpressions;
using Temno.Core.Models;

namespace Temno.Media.Export;

/// <summary>
/// Probes which video encoders the installed FFmpeg build exposes (WP-112 unsupported-codec messaging).
/// </summary>
public static class FfmpegEncoderCapabilities
{
    private static readonly object Gate = new();
    private static HashSet<string>? _encoders;
    private static HashSet<string>? _filters;
    private static string? _ffmpegPath;

    /// <summary>Clear cached probe results (tests).</summary>
    public static void ResetCache()
    {
        lock (Gate)
        {
            _encoders = null;
            _filters = null;
            _ffmpegPath = null;
        }
    }

    public static bool IsEncoderAvailable(string encoderName)
    {
        var set = GetEncoders();
        return set.Contains(encoderName);
    }

    /// <summary>True when the installed FFmpeg exposes <paramref name="filterName"/>.</summary>
    public static bool IsFilterAvailable(string filterName) => GetFilters().Contains(filterName);

    /// <summary>
    /// HDR export needs <c>zscale</c> (libzimg) to convert BT.709 sRGB into BT.2020 HLG. Without it
    /// the only thing the pipeline could do is relabel the pixels, which shifts every hue — better
    /// to say so than to hand back a file whose colours are wrong but whose tags claim otherwise.
    /// </summary>
    public static void RequireHdrColorSupport()
    {
        if (IsFilterAvailable("zscale")) return;
        throw new InvalidOperationException(
            "HEVC 10-bit HDR export needs the 'zscale' filter (FFmpeg built with libzimg), which "
            + $"this build does not provide (ffmpeg={SafeFfmpegPath()}). Install a full FFmpeg build, "
            + "or export H.264 / H.265 instead.");
    }

    /// <summary>
    /// Resolves the FFmpeg encoder name for <paramref name="codec"/>, or throws with an explicit
    /// unsupported message when no suitable encoder is present.
    /// </summary>
    public static string RequireEncoder(VideoCodec codec, bool preferHardware = false)
    {
        var candidates = preferHardware
            ? EncoderCandidatesHardwareFirst(codec)
            : EncoderCandidates(codec);
        foreach (var name in candidates)
        {
            if (IsEncoderAvailable(name))
                return name;
        }

        var ffmpeg = SafeFfmpegPath();
        var tried = string.Join(", ", candidates);
        throw new InvalidOperationException(
            $"Codec '{DisplayName(codec)}' is not available in this FFmpeg build " +
            $"(looked for: {tried}; ffmpeg={ffmpeg}). " +
            "Install a build that includes the encoder, or choose H.264 / H.265.");
    }

    public static string DisplayName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.H265 => "H.265",
        VideoCodec.ProRes => "ProRes",
        VideoCodec.HevcHdr => "HEVC 10-bit HDR",
        _ => codec.ToString(),
    };

    /// <summary>
    /// Software-first: the RGBA pipe is most reliable through libx264. Hardware encoders
    /// are fallbacks. The shipped build is GPL and includes libx264 and libx265 (ADR-005).
    /// </summary>
    public static IReadOnlyList<string> EncoderCandidates(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => ["libx264", "libopenh264", "h264_nvenc", "h264_qsv", "h264_amf"],
        VideoCodec.H265 => ["libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf"],
        VideoCodec.ProRes => ["prores_ks", "prores"],
        VideoCodec.HevcHdr => ["libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf"],
        _ => ["libx264"],
    };

    /// <summary>
    /// Hardware H.264 encoders stop at 4096 px a side. HEVC hardware is higher on some GPUs and
    /// not on others, so a frame past this limit is encoded in software rather than surfaced as
    /// an ffmpeg error. 16:9 4K (3840×2160) stays under it.
    /// </summary>
    public const int HardwareEncoderMaxSide = 4096;

    public static bool ExceedsHardwareEncoderLimit(VideoCodec codec, int width, int height)
    {
        if (codec == VideoCodec.ProRes) return false;
        return width > HardwareEncoderMaxSide || height > HardwareEncoderMaxSide;
    }

    public static string SoftwareEncodeReason(int width, int height)
        => $"software, hardware stops at {HardwareEncoderMaxSide} px ({width}×{height})";

    /// <summary>Hardware-first candidate order (WP-114 PreferHardware encode path).</summary>
    public static IReadOnlyList<string> EncoderCandidatesHardwareFirst(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => ["h264_nvenc", "h264_qsv", "h264_amf", "libx264", "libopenh264"],
        VideoCodec.H265 => ["hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265"],
        VideoCodec.ProRes => ["prores_ks", "prores"],
        VideoCodec.HevcHdr => ["hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265"],
        _ => ["h264_nvenc", "libx264"],
    };

    private static string SafeFfmpegPath()
    {
        try { return FFmpegResolver.GetFFmpegPath(); }
        catch { return "(unresolved)"; }
    }

    private static HashSet<string> GetEncoders()
    {
        lock (Gate)
        {
            var path = SafeFfmpegPath();
            if (_encoders is not null && _ffmpegPath == path)
                return _encoders;

            _ffmpegPath = path;
            _encoders = ProbeEncoders(path);
            _filters = null;
            return _encoders;
        }
    }

    private static HashSet<string> GetFilters()
    {
        lock (Gate)
        {
            var path = SafeFfmpegPath();
            if (_filters is not null && _ffmpegPath == path)
                return _filters;

            _ffmpegPath = path;
            // Lines look like: " .S zscale   V->V   Apply resizing, colorspace and bit depth conversion."
            // The flag column is 3 chars (T/S/C) on older builds and 2 on ffmpeg 8.x, so accept both.
            _filters = ProbeNames(path, "-hide_banner -filters", @"^\s*[TSC\.]{2,3}\s+(\S+)\s+\S*->\S*");
            return _filters;
        }
    }

    // Lines look like: " V..... libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10"
    private static HashSet<string> ProbeEncoders(string ffmpegPath)
        => ProbeNames(ffmpegPath, "-hide_banner -encoders", @"^\s*[A-Z\.]{6}\s+(\S+)");

    private static HashSet<string> ProbeNames(string ffmpegPath, string arguments, string pattern)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(ffmpegPath) || PathIsMissing(ffmpegPath))
            return set;

        try
        {
            using var process = new Process();
            process.StartInfo.FileName = ffmpegPath;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            if (!process.Start()) return set;

            var stdout = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);

            foreach (Match m in Regex.Matches(stdout, pattern, RegexOptions.Multiline))
            {
                if (m.Groups.Count > 1)
                    set.Add(m.Groups[1].Value);
            }
        }
        catch
        {
            // Leave empty — the Require* helpers will still fail explicitly.
        }

        return set;
    }

    private static bool PathIsMissing(string p) => p is "(unresolved)" || !File.Exists(p);
}
