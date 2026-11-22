namespace Temno.Media;

/// <summary>Implemented in WP-20 (FFmpeg). Stub interface for early architecture.</summary>
public interface IMediaProbe
{
    Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

/// <param name="VideoCodecName">
/// ffprobe <c>codec_name</c> of the video stream, or null when unknown. WP-164: the proxy policy
/// needs it to tell an expensive decode from a cheap one. Trailing and optional so the many
/// positional constructions in tests and fakes keep compiling.
/// </param>
/// <param name="AudioStartSeconds">
/// WP-336: where the first audio stream, the one the audio source decodes, starts in seconds after
/// the container start (ffprobe stream <c>start_time</c> minus format <c>start_time</c>), which is the
/// clock <c>-ss</c> seeks on. Null when there is no audio stream or ffprobe reports no start time.
/// </param>
/// <param name="Width">
/// WP-405: the picture as it is shown, square pixels and upright: the coded width after the stream's
/// rotation and its sample aspect ratio. <c>Height</c> likewise. This is the size a clip is fitted by.
/// </param>
/// <param name="DecodedWidth">
/// WP-405: what ffmpeg emits before any scale filter: the coded size, turned by its default autorotate.
/// Differs from <c>Width</c> only for non-square pixels. Null means the same as <c>Width</c>.
/// </param>
/// <param name="Rotation">WP-405: the display rotation in degrees, 0, 90, 180 or 270.</param>
/// <param name="SampleAspect">WP-405: the pixel aspect ratio (ffprobe <c>sample_aspect_ratio</c>), 1 for square.</param>
public sealed record MediaProbeResult(
    double DurationSeconds,
    int? Width,
    int? Height,
    double? Fps,
    bool HasAudio,
    bool HasVideo,
    string? VideoCodecName = null,
    double? AudioStartSeconds = null,
    int? DecodedWidth = null,
    int? DecodedHeight = null,
    int Rotation = 0,
    double SampleAspect = 1.0)
{
    /// <summary>The size ffmpeg decodes to before scaling; the decode paths scale it to <c>Width</c>x<c>Height</c>.</summary>
    public (int Width, int Height)? DecodedSize =>
        (DecodedWidth ?? Width, DecodedHeight ?? Height) is (int w, int h) ? (w, h) : null;

    /// <summary>True when the stored picture is turned or has non-square pixels, so a stream copy would not match a decode.</summary>
    public bool IsReshaped => Rotation != 0 || Math.Abs(SampleAspect - 1) > 1e-6;
}
