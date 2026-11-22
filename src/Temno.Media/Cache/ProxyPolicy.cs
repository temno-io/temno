using System;
using System.Collections.Generic;

namespace Temno.Media.Cache;

/// <summary>
/// Decides when a scrub proxy is worth generating for a source file. Pure heuristics:
/// large frames (short side above direct-playback height) always warrant one; decode-expensive
/// codecs warrant one whenever the proxy would actually be smaller than the source.
/// The Mac app has no scrub proxies (its proxies normalize stills/lottie/alpha); this policy is the
/// Windows analogue for smooth timeline scrubbing per WP-25.
/// </summary>
public sealed class ProxyPolicy
{
    public static ProxyPolicy Default { get; } = new();

    /// <summary>Largest short-side that plays back directly without a proxy.</summary>
    public int MaxDirectPlaybackHeight { get; init; } = 1080;

    /// <summary>Target short-side of generated proxies.</summary>
    public int ProxyHeight { get; init; } = 720;

    /// <summary>ffprobe codec_name values considered expensive to decode while scrubbing.</summary>
    public IReadOnlySet<string> ExpensiveCodecs { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hevc", "h265", "h.265", "av1", "vp9" };

    /// <summary>
    /// Still-image codecs never warrant an H.264 <c>.mp4</c> scrub proxy: the decoder holds the
    /// single decoded frame in memory, and transcoding a still to <c>yuv420p</c> strips alpha.
    /// </summary>
    public IReadOnlySet<string> StillImageCodecs { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mjpeg", "png", "webp", "bmp", "tiff", "gif", "targa", "qoi", "exr", "psd", "hdr", "jpeg2000", "jp2"
        };

    /// <summary>
    /// Still-image file extensions (including HEIC/HEIF/AVIF whose codec reports as <c>hevc</c>/<c>av1</c>).
    /// Checked alongside <see cref="StillImageCodecs"/> so high-res stills never get transcoded into video proxies.
    /// </summary>
    public static IReadOnlySet<string> StillImageExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif",
            ".tga", ".qoi", ".exr", ".psd", ".hdr", ".jp2"
        };

    public static bool IsStillImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = System.IO.Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && StillImageExtensions.Contains(ext);
    }

    /// <summary>
    /// WP-164. True when decoding a proxy would be cheaper than the original *for preview*.
    /// <see cref="ShouldProxy"/> answers WP-25's general question ("is this file heavy?"); preview
    /// asks a narrower one, because it already decodes at <paramref name="previewMaxDimension"/>:
    /// anything whose long edge exceeds that is paying for pixels it throws away, and an expensive
    /// codec is worth re-wrapping as H.264 even at the same size.
    /// </summary>
    public bool ShouldProxyForPreview(int? width, int? height, string? codecName, int previewMaxDimension)
    {
        if (previewMaxDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(previewMaxDimension));
        if (!string.IsNullOrEmpty(codecName) && StillImageCodecs.Contains(codecName))
            return false;

        int? longEdge = (width, height) switch
        {
            (int w, int h) => Math.Max(w, h),
            (int w, null) => w,
            (null, int h) => h,
            _ => null,
        };

        if (longEdge is int edge && edge > previewMaxDimension)
            return true;

        // Same-size but expensive to decode (HEVC/AV1/VP9): an H.264 rewrap still pays off.
        // Unknown dimensions plus an expensive codec cannot be ruled out, so proxy those too.
        if (!string.IsNullOrEmpty(codecName) && ExpensiveCodecs.Contains(codecName))
            return true;

        return false;
    }

    /// <summary>
    /// WP-164. Proxy height for a source, chosen so the proxy lands on the exact raster preview
    /// already decodes at. <see cref="FfmpegVideoFilter.Convert"/> then emits no <c>scale</c> at
    /// all for the proxy — the decode is a format conversion and nothing more. Sizing by
    /// <see cref="ProxyHeight"/> instead would under-serve portrait video, where scaling *height*
    /// to 720 leaves a 405-wide frame that preview would have to upscale.
    /// </summary>
    public static int TargetHeightFor(int sourceWidth, int sourceHeight, int previewMaxDimension)
        => PreviewRaster.Fit(sourceWidth, sourceHeight, previewMaxDimension).Height;

    /// <summary>
    /// True when a proxy should be generated. Uses the frame's short side so vertical video is
    /// judged by the same effective resolution. Unknown dimensions with an expensive codec still
    /// proxy (cannot rule out benefit); fully unknown media does not.
    /// </summary>
    public bool ShouldProxy(int? width, int? height, string? codecName)
    {
        if (!string.IsNullOrEmpty(codecName) && StillImageCodecs.Contains(codecName))
            return false;

        int? shortSide = (width, height) switch
        {
            (int w, int h) => Math.Min(w, h),
            (int w, null) => w,
            (null, int h) => h,
            _ => null,
        };

        if (shortSide is int side && side > MaxDirectPlaybackHeight)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(codecName) && ExpensiveCodecs.Contains(codecName))
        {
            return shortSide is null || shortSide > ProxyHeight;
        }

        return false;
    }
}
