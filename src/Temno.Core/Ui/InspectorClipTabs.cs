using Temno.Core.Models;

namespace Temno.Core.Ui;

/// <summary>Inspector clip-inspector tabs. Names match the WinUI host; Style is <see cref="Content"/>.</summary>
public enum InspectorClipTab
{
    Video,
    Adjust,
    Audio,
    Content,
    Animation,
    Multicam,
}

/// <summary>
/// WP-174. Which Inspector tabs a clip may show. A nested-sequence carrier is visual (Mac
/// <c>ClipType.isVisual</c> is everything except audio) and the WP-165 mixer honours its
/// volume / fade / denoise, so it is not "neither".
/// </summary>
public static class InspectorClipTabs
{
    /// <summary>
    /// Picture-bearing types that get Video + Adjust. Text is visual and gets Style / Animation /
    /// Video (without Adjust); Lottie is unchanged (out of WP-174 scope).
    /// </summary>
    public static bool IsPictureClip(ClipType type)
        => type is ClipType.video or ClipType.image or ClipType.sequence;

    /// <summary>
    /// Visual clip types that expose the Video tab (Timing, Transform, Playback, Crop, Keyframes)
    /// and picture fades.
    /// </summary>
    public static bool HasVideoTab(ClipType type)
        => IsPictureClip(type) || type == ClipType.text;

    /// <summary>
    /// Types whose clip-level volume the mixer actually uses. Image stays silent; sequence
    /// does not — WP-165 proved the carrier fields.
    /// </summary>
    public static bool HasClipAudioLevels(ClipType type)
        => type is ClipType.audio or ClipType.video or ClipType.sequence;

    public static IReadOnlyList<InspectorClipTab> ForClip(
        ClipType type,
        bool hasTextContent,
        bool includeMulticam)
    {
        var tabs = new List<InspectorClipTab>(4);
        var isText = type == ClipType.text || hasTextContent;

        if (isText && !IsPictureClip(type))
        {
            tabs.Add(InspectorClipTab.Content);
            tabs.Add(InspectorClipTab.Animation);
            tabs.Add(InspectorClipTab.Video);
        }

        if (IsPictureClip(type))
        {
            tabs.Add(InspectorClipTab.Video);
            tabs.Add(InspectorClipTab.Adjust);
        }

        if (HasClipAudioLevels(type))
            tabs.Add(InspectorClipTab.Audio);

        if (includeMulticam)
            tabs.Add(InspectorClipTab.Multicam);

        return tabs;
    }

    /// <summary>
    /// Keep the current tab when it still exists; otherwise the first available tab.
    /// Entering a text-only clip from a non-text clip (or empty selection) lands on Style unless
    /// already on Style / Animation; staying on a text clip keeps whichever tab the user picked
    /// (including Video) across edits and selection changes between text clips.
    /// An empty set returns null — the host must hide every pane, not the previous clip's.
    /// </summary>
    public static InspectorClipTab? ResolvePreferred(
        IReadOnlyList<InspectorClipTab> available,
        InspectorClipTab current,
        ClipType type,
        bool hasTextContent,
        bool wasTextClip = false)
    {
        var isText = type == ClipType.text || hasTextContent;
        if (isText && !IsPictureClip(type)
            && !wasTextClip
            && current is not (InspectorClipTab.Content or InspectorClipTab.Animation)
            && available.Contains(InspectorClipTab.Content))
            return InspectorClipTab.Content;

        if (available.Contains(current))
            return current;

        return available.Count > 0 ? available[0] : null;
    }
}
