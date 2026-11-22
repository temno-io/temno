using System.Globalization;
using Temno.Core.Export;
using Temno.Core.Models;

namespace Temno.Media.Export;

/// <summary>
/// FFmpeg argv for timeline export. Mac analog is AVAssetExportSession (even canvas, High/HEVC/ProRes presets);
/// Windows must set player-safe H.264/HEVC flags itself.
/// </summary>
public static class ExportEncodeArguments
{
    public static (int Width, int Height) EvenSize(int width, int height)
        => ExportOutputSize.EvenSize(width, height);

    public static (int Width, int Height) ResolveOutputSize(Timeline timeline, ExportPreset preset)
        => ExportOutputSize.Resolve(timeline, preset);

    public static double DefaultBitrateMbps(int width, int height, VideoCodec codec)
    {
        var pixels = (double)(width * height);
        return codec switch
        {
            VideoCodec.H265 => pixels / 1_000_000 * 4,
            VideoCodec.HevcHdr => pixels / 1_000_000 * 6,
            VideoCodec.ProRes => pixels / 1_000_000 * 40,
            _ => pixels / 1_000_000 * 6,
        };
    }

    public static string BuildVideoPassArguments(
        string encoder,
        int width,
        int height,
        int fps,
        VideoCodec codec,
        double bitrateMbps,
        int crf,
        bool explicitBitrate,
        bool writeFaststart,
        string outputPath,
        int codedWidth = 0,
        int codedHeight = 0,
        double? outputFps = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (width < 2 || height < 2)
            throw new ArgumentOutOfRangeException(nameof(width), "Export size must be at least 2×2.");
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps));
        if (outputFps is { } requestedFps && !ExportFrameRate.TryValidate(requestedFps, out var fpsError))
            throw new ArgumentOutOfRangeException(nameof(outputFps), fpsError);
        if (codedWidth <= 0) codedWidth = width;
        if (codedHeight <= 0) codedHeight = height;
        if (codedWidth < width || codedHeight < height)
            throw new ArgumentOutOfRangeException(nameof(codedWidth), "The coded frame cannot be smaller than the rendered picture.");

        var inv = CultureInfo.InvariantCulture;
        var resampledFps = outputFps is { } target && !ExportFrameRate.MatchesTimeline(target, fps) ? target : (double?)null;
        var effectiveFps = resampledFps ?? fps;
        var isHdr = codec == VideoCodec.HevcHdr;
        var isNvenc = encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase);
        var isAmf = encoder.Contains("_amf", StringComparison.OrdinalIgnoreCase);
        var isQsv = encoder.Contains("_qsv", StringComparison.OrdinalIgnoreCase);
        var isOpenh264 = encoder.Equals("libopenh264", StringComparison.OrdinalIgnoreCase);
        var isX264 = encoder.Equals("libx264", StringComparison.OrdinalIgnoreCase);
        var isX265 = encoder.Equals("libx265", StringComparison.OrdinalIgnoreCase);
        var isHardware = isNvenc || isAmf || isQsv;

        string pixFmt;
        if (codec == VideoCodec.ProRes)
            pixFmt = "yuv422p10le";
        else if (isHdr)
            pixFmt = isNvenc ? "p010le" : "yuv420p10le";
        else if (isHardware)
            pixFmt = "nv12";
        else
            pixFmt = "yuv420p";

        var presetFlag = encoder switch
        {
            _ when isX264 || isX265 => " -preset medium",
            _ when isNvenc || isQsv => " -preset medium",
            _ => "",
        };

        var rate = RateControl(
            codec, bitrateMbps, crf, explicitBitrate,
            isNvenc, isAmf, isQsv, isOpenh264, isX264, isX265);

        var profile = codec switch
        {
            VideoCodec.ProRes => " -profile:v 3",
            VideoCodec.H264 when !isOpenh264 => " -profile:v high",
            _ => "",
        };

        var gop = codec == VideoCodec.ProRes ? "" : $" -g {Math.Max(1, (int)Math.Round(effectiveFps * 2)).ToString(inv)}";

        // Output-side -color_* flags alone do NOT reach the bitstream on most encoders: since
        // FFmpeg 7 the encoder takes its colour description from the AVFrame the filtergraph
        // produces, and a rawvideo frame carries none. Verified against ffmpeg 8.1 — every encoder
        // except libx265 (which gets the values a second time via -x265-params) wrote
        // color_primaries=unknown / color_transfer=unknown. The filter chain below stamps the
        // description onto the frames themselves, which every encoder does forward.
        var afterSourceParts = new List<string>(2);
        if (resampledFps is { } outRate)
            afterSourceParts.Add($"fps={ExportFrameRate.ToFfmpegExpression(outRate)}");
        if (codedWidth != width || codedHeight != height)
        {
            var x = (codedWidth - width) / 2;
            var y = (codedHeight - height) / 2;
            // Pad in RGBA, before the YUV conversion, so the bars are black in the picture
            // rather than a YUV black that depends on the matrix.
            afterSourceParts.Add($"pad={codedWidth.ToString(inv)}:{codedHeight.ToString(inv)}:{x.ToString(inv)}:{y.ToString(inv)}:color=black");
        }
        var afterSource = afterSourceParts.Count > 0 ? string.Join(',', afterSourceParts) : null;

        var filter = BuildColorFilter(codec, pixFmt, afterSource);
        string color;
        if (isHdr)
        {
            color = isX265
                ? " -color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc -color_range tv -x265-params \"colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc\""
                : " -color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc -color_range tv";
        }
        else
        {
            color = " -color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range tv";
        }

        var tag = codec switch
        {
            VideoCodec.H264 => " -tag:v avc1",
            VideoCodec.H265 or VideoCodec.HevcHdr => " -tag:v hvc1",
            _ => "",
        };

        var faststart = writeFaststart ? " -movflags +faststart" : "";
        var size = $"{width.ToString(inv)}x{height.ToString(inv)}";
        var fpsText = fps.ToString(inv);

        // -hide_banner/-nostats keep stderr to actual errors, so the diagnostic surfaced when a pass
        // fails is the reason rather than 40 lines of build configuration and progress counters.
        return $"-hide_banner -nostats -loglevel error -y "
             + $"-f rawvideo -pix_fmt rgba -s {size} -r {fpsText} -i pipe:0 "
             + $"-vf \"{filter}\" "
             + $"-c:v {encoder} -pix_fmt {pixFmt}{presetFlag} {rate}{profile}{gop}{color}{tag}{faststart} "
             + $"-an \"{outputPath}\"";
    }

    /// <summary>Nominal peak luminance (cd/m²) assumed when mapping SDR content into HLG.</summary>
    public const int HdrNominalPeakLuminance = 1000;

    /// <summary>
    /// The <c>-vf</c> chain that converts the compositor's 8-bit full-range sRGB RGBA canvas into
    /// the encoder's target colour space AND tags the frames so the description survives into the
    /// bitstream.
    /// <para>
    /// For HDR this is not cosmetic. The canvas is BT.709 primaries with the sRGB transfer; simply
    /// declaring it BT.2020/HLG re-interprets the same numbers against a much wider gamut, which
    /// shifts every hue (a pure red reads as orange/yellow). The zscale chain linearises, converts
    /// primaries BT.709 → BT.2020, then applies the HLG transfer, so the colours are actually
    /// carried across rather than relabelled.
    /// </para>
    /// </summary>
    public static string BuildColorFilter(VideoCodec codec, string pixelFormat, string? afterSource = null)
    {
        // The pipe is full-range sRGB RGB; say so explicitly before any conversion.
        const string tagSource = "setparams=color_primaries=bt709:color_trc=iec61966-2-1:colorspace=gbr:range=full";
        var mid = string.IsNullOrEmpty(afterSource) ? "" : "," + afterSource;

        if (codec == VideoCodec.HevcHdr)
        {
            return tagSource
                 + mid
                 + $",zscale=t=linear:npl={HdrNominalPeakLuminance.ToString(CultureInfo.InvariantCulture)}"
                 + ",zscale=p=bt2020"
                 + ",zscale=t=arib-std-b67:m=bt2020nc:r=tv"
                 + $",format={pixelFormat}"
                 + ",setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc:range=tv";
        }

        // SDR: swscale already performs the RGB→YUV matrix; naming it keeps the choice explicit and
        // stops the tag from disagreeing with the pixels.
        return tagSource
             + mid
             + ",scale=out_color_matrix=bt709:out_range=tv"
             + $",format={pixelFormat}"
             + ",setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv";
    }

    public static string BuildAudioMuxArguments(
        string videoPath,
        string outputPath,
        int sampleRate = 48000,
        int channels = 2,
        bool hdr = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var inv = CultureInfo.InvariantCulture;
        var container = outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? " -f mp4"
            : "";
        return $"-hide_banner -nostats -loglevel error -y -i \"{videoPath}\" "
             + $"-f f32le -ar {sampleRate.ToString(inv)} -ac {channels.ToString(inv)} -i pipe:0 "
             // -c:v copy carries the encoded stream through unchanged, but NOT its colour
             // description on every container/muxer pairing, so restate it on the copy.
             + "-c:v copy -c:a aac -b:a 256k -map 0:v:0 -map 1:a:0 "
             + $"{CopyColorFlags(hdr)}-movflags +faststart{container} "
             + $"\"{outputPath}\"";
    }

    private static string CopyColorFlags(bool hdr) => hdr
        ? "-color_primaries bt2020 -color_trc arib-std-b67 -colorspace bt2020nc -color_range tv "
        : "-color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range tv ";

    private static string RateControl(
        VideoCodec codec,
        double bitrateMbps,
        int crf,
        bool explicitBitrate,
        bool isNvenc,
        bool isAmf,
        bool isQsv,
        bool isOpenh264,
        bool isX264,
        bool isX265)
    {
        var b = Mbps(bitrateMbps);
        var max = Mbps(bitrateMbps * 1.5);
        var buf = Mbps(bitrateMbps * 2);

        if (codec == VideoCodec.ProRes || isOpenh264)
            return $"-b:v {b}M";
        if (isNvenc)
            return $"-b:v {b}M -maxrate {max}M -bufsize {buf}M -rc vbr";
        if (isAmf)
            return $"-b:v {b}M -maxrate {max}M -rc vbr_peak";
        if (isQsv)
            return $"-b:v {b}M -maxrate {max}M";
        if ((isX264 || isX265) && !explicitBitrate)
            return $"-crf {crf.ToString(CultureInfo.InvariantCulture)} -maxrate {b}M -bufsize {buf}M";
        return $"-b:v {b}M -maxrate {b}M -bufsize {buf}M";
    }

    private static string Mbps(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
