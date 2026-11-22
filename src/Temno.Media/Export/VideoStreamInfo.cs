using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Temno.Media.Export;

/// <summary>
/// Probed video stream metadata and keyframe timestamps used for smart-render eligibility and GOP snapping.
/// Times are seconds from the container start, the origin ffmpeg's <c>-ss</c> seeks from.
/// </summary>
public sealed record VideoStreamInfo(
    string CodecName,      // ffprobe codec_name, e.g. "h264", "hevc"
    string? Profile,
    int Width,
    int Height,
    string PixelFormat,    // e.g. "yuv420p"
    double FrameRate,      // from avg_frame_rate, parsed from the "30000/1001" rational
    string? ColorPrimaries,
    string? ColorTransfer,
    string? ColorSpace,
    string? ColorRange,
    IReadOnlyList<double> KeyframeSeconds,    // ascending pts of every keyframe
    IReadOnlyList<VideoPacket>? Packets = null,   // every packet in decode order; null when not probed
    string? ContainerFormat = null,               // ffprobe format_name, e.g. "mov,mp4,m4a,3gp,3g2,mj2", "mpegts"
    bool Reshaped = false);                       // WP-405: stored turned (display matrix) or with non-square pixels

/// <summary>
/// One video packet in decode order. A stream copy of N frames takes the first N packets after a
/// keyframe in this order, so this table — not the keyframe list — decides which display frames
/// such a copy actually contains.
/// </summary>
/// <param name="PtsSeconds">Presentation time from the container start; NaN when the packet has none.</param>
/// <param name="IsKeyframe">The container marks the packet as a random-access point.</param>
/// <param name="IsDiscard">The container hides the packet (an edit-list pre-roll); it is decoded but never shown.</param>
public readonly record struct VideoPacket(double PtsSeconds, bool IsKeyframe, bool IsDiscard = false);

/// <summary>
/// Interface for probing video stream metadata. Allows <see cref="SmartRenderAnalyzer"/> to be tested without media on disk.
/// </summary>
public interface IVideoStreamProbe
{
    Task<VideoStreamInfo> ProbeAsync(string path, CancellationToken cancellationToken = default);
}
