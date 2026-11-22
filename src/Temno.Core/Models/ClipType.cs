namespace Temno.Core.Models;

/// <summary>Mac: Models/ClipType.swift — keep raw string values stable for JSON.</summary>
public enum ClipType
{
    video,
    audio,
    image,
    text,
    lottie,
    sequence,

    /// <summary>
    /// SRT/WebVTT caption file. A library-only asset: it never becomes a timeline clip and never
    /// becomes a track type. Cues are placed as text clips via add_captions subtitleMediaRef.
    /// </summary>
    subtitle,
}

public static class ClipTypeExtensions
{
    /// <summary>
    /// True for library assets that carry no picture or sound and must never be placed on the
    /// timeline as an A/V clip.
    /// </summary>
    public static bool IsTimelinePlaceable(this ClipType type) => type != ClipType.subtitle;
}
