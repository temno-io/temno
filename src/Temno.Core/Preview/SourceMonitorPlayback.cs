using Temno.Core.Models;

namespace Temno.Core.Preview;

/// <summary>
/// WP-173. Whether a source-monitor tab may start transport and mix audio.
/// Nested sequences are timelines, not media-library assets: the package contract lists
/// <see cref="ClipType.sequence"/> on a clip only.
/// </summary>
public static class SourceMonitorPlayback
{
    /// <summary>
    /// Placeholder when a source tab is typed as a sequence. Matches the Lottie honesty
    /// pattern: the surface says where that content actually plays.
    /// </summary>
    public const string SequencePlaceholder = "Sequence — nested timelines play on the Timeline tab";

    /// <summary>Transport audio line when a sequence tab is active. Never silent.</summary>
    public const string SequenceAudioStatus = "Nested sequences play on the Timeline tab.";

    public static bool IsSequence(ClipType? type) => type is ClipType.sequence;

    /// <summary>Types that may start Play on a source tab.</summary>
    public static bool CanPlay(ClipType? type) => type is ClipType.video or ClipType.audio;

    /// <summary>
    /// Types that get a synthetic one-clip mix timeline in the source monitor.
    /// Sequence is excluded: a nested timeline id is not a media file.
    /// </summary>
    public static bool BuildsSourceAudioTimeline(ClipType type)
        => type is ClipType.video or ClipType.audio;
}
