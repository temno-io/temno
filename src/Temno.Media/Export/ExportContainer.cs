using Temno.Core.Models;

namespace Temno.Media.Export;

/// <summary>
/// Codec ↔ container rules for export destinations.
/// <para>
/// ProRes and HEVC 10-bit HDR are QuickTime deliverables: ffmpeg's mp4 muxer refuses a ProRes
/// stream outright ("Could not write header (incorrect codec parameters ?)"). The export dialog
/// already forces <c>.mov</c> for those codecs, but the agent/MCP path derives the extension from
/// the export MODE alone and always produced <c>.mp4</c>, so <c>export_project codec:"ProRes"</c>
/// rendered every frame and only then failed at the mux.
/// </para>
/// </summary>
public static class ExportContainer
{
    /// <summary>Container extension a codec must be written into, including the leading dot.</summary>
    public static string RequiredExtension(VideoCodec codec) => codec switch
    {
        VideoCodec.ProRes => ".mov",
        VideoCodec.HevcHdr => ".mov",
        _ => ".mp4",
    };

    /// <summary>Extensions a codec can legally be written into.</summary>
    public static IReadOnlyList<string> AllowedExtensions(VideoCodec codec) => codec switch
    {
        VideoCodec.ProRes => [".mov"],
        VideoCodec.HevcHdr => [".mov", ".mp4"],
        VideoCodec.H265 => [".mp4", ".mov", ".mkv"],
        _ => [".mp4", ".mov", ".mkv"],
    };

    public static bool IsSupported(VideoCodec codec, string outputPath)
    {
        var ext = Path.GetExtension(outputPath);
        if (string.IsNullOrEmpty(ext)) return false;
        foreach (var allowed in AllowedExtensions(codec))
        {
            if (string.Equals(ext, allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Rewrites <paramref name="outputPath"/> to a container the codec can actually be written
    /// into, leaving an already-valid path untouched. Use when picking a default destination.
    /// </summary>
    public static string CoerceExtension(VideoCodec codec, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return outputPath;
        return IsSupported(codec, outputPath)
            ? outputPath
            : Path.ChangeExtension(outputPath, RequiredExtension(codec));
    }

    /// <summary>
    /// Throws before any frame is rendered when the destination container cannot hold the codec.
    /// Failing here costs a second; failing at the mux costs the whole render.
    /// </summary>
    public static void EnsureCodecMatchesContainer(VideoCodec codec, string outputPath)
    {
        if (IsSupported(codec, outputPath)) return;

        var ext = Path.GetExtension(outputPath);
        var shown = string.IsNullOrEmpty(ext) ? "(no extension)" : ext;
        var allowed = string.Join(" / ", AllowedExtensions(codec));
        throw new InvalidOperationException(
            $"{FfmpegEncoderCapabilities.DisplayName(codec)} cannot be written into a {shown} file. "
            + $"Use {allowed} for this codec (suggested: "
            + $"{Path.GetFileName(CoerceExtension(codec, outputPath))}).");
    }
}
