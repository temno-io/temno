using Temno.Core.Models;

namespace Temno.Core.Preview;

public enum PreviewMonitorKind
{
    Timeline,
    MediaAsset,
}

/// <summary>
/// Mac <c>PreviewTab</c>: the program monitor (timeline) plus source tabs opened from the library.
/// </summary>
public sealed record PreviewMonitorTab(
    string Id,
    string Title,
    PreviewMonitorKind Kind,
    string? MediaRef = null,
    ClipType? MediaType = null)
{
    public const string TimelineId = "__timeline__";

    public bool IsCloseable => Kind != PreviewMonitorKind.Timeline;

    public static PreviewMonitorTab Timeline(string name)
        => new(TimelineId, string.IsNullOrWhiteSpace(name) ? "Timeline" : name, PreviewMonitorKind.Timeline);

    public static string MediaTabId(string mediaRef) => "media_" + mediaRef;

    public static PreviewMonitorTab ForMedia(string mediaRef, string name, ClipType type)
        => new(MediaTabId(mediaRef), string.IsNullOrWhiteSpace(name) ? mediaRef : name, PreviewMonitorKind.MediaAsset, mediaRef, type);
}
