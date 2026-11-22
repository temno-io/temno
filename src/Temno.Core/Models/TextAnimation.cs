using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temno.Core.Models;

/// <summary>
/// Text-clip animation settings. Mac: Models/TextAnimation.swift — the preset is stored as the
/// Swift enum's raw string, so a plain string keeps interchange exact and future presets readable.
/// </summary>
public sealed class TextAnimation
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonPropertyName("preset")]
    public string Preset { get; set; } = "none";

    [JsonPropertyName("perWordFrames")]
    public int PerWordFrames { get; set; } = 6;

    [JsonPropertyName("highlight")]
    public TextStyle.RGBA? Highlight { get; set; }

    /// <summary>Mac TextAnimation.Preset cases other than none.</summary>
    public static readonly string[] Presets =
    [
        "fadeIn", "popIn", "slideUp", "typewriter",
        "wordReveal", "wordSlide", "wordPop", "wordCycle", "highlightPop", "highlightBlock",
    ];

    /// <summary>Values agents may pass ("off" clears the animation). Mac: Preset.agentValues.</summary>
    public static readonly string[] AgentValues = ["off", .. Presets];

    /// <summary>Mac TextAnimation.defaultHighlight (yellow).</summary>
    public static TextStyle.RGBA DefaultHighlight => new() { R = 1, G = 0.85, B = 0, A = 1 };

    /// <summary>Mac TextAnimation.Preset.RenderMode — how the compositor has to draw the clip.</summary>
    public enum RenderMode
    {
        /// <summary>Whole clip animates in as one unit.</summary>
        Entrance,

        /// <summary>Each word carries its own opacity/scale/offset/colour.</summary>
        PerWord,

        /// <summary>Characters revealed over time.</summary>
        Typewriter,
    }

    /// <summary>Mac Preset.perLine — full JSON/agent vocabulary. Do not use as the gallery.</summary>
    public static readonly string[] PerLine = ["fadeIn", "popIn", "slideUp", "typewriter"];

    /// <summary>Mac Preset.perWord — full JSON/agent vocabulary. Do not use as the gallery.</summary>
    public static readonly string[] PerWord =
        ["wordReveal", "wordSlide", "wordPop", "wordCycle", "highlightPop", "highlightBlock"];

    /// <summary>
    /// Screenshot-09 gallery, Per line row, including Off. <see cref="PerLine"/> still lists
    /// <c>fadeIn</c> for JSON/agents; that preset is not surfaced.
    /// </summary>
    public static readonly string[] GalleryPerLine = ["none", "popIn", "slideUp", "typewriter"];

    /// <summary>
    /// Screenshot-09 gallery, Per word row. <see cref="PerWord"/> still lists <c>wordPop</c> and
    /// <c>wordCycle</c> for JSON/agents; those presets are not surfaced.
    /// </summary>
    public static readonly string[] GalleryPerWord =
        ["wordReveal", "wordSlide", "highlightPop", "highlightBlock"];

    public static RenderMode ModeOf(string? preset) => preset switch
    {
        "typewriter" => RenderMode.Typewriter,
        "wordReveal" or "wordSlide" or "wordPop" or "wordCycle"
            or "highlightPop" or "highlightBlock" => RenderMode.PerWord,
        _ => RenderMode.Entrance,
    };

    /// <summary>Mac Preset.usesHighlight — only per-word presets read the highlight colour.</summary>
    public static bool UsesHighlight(string? preset) => ModeOf(preset) == RenderMode.PerWord;

    /// <summary>Mac Preset.displayName — user-facing label for the preset gallery (WP-125).</summary>
    public static string DisplayName(string? preset) => preset switch
    {
        "fadeIn" => "Fade In",
        "popIn" => "Pop In",
        "slideUp" => "Slide Up",
        "typewriter" => "Typewriter",
        "wordReveal" => "Word Reveal",
        "wordSlide" => "Word Slide",
        "wordPop" => "Word Pop",
        "wordCycle" => "Word Cycle",
        "highlightPop" => "Highlight",
        "highlightBlock" => "Highlight Block",
        _ => "Off",
    };

    /// <summary><c>off</c>, empty, and null are the Off cell (<c>none</c>).</summary>
    public static string CanonicalPreset(string? preset) => preset switch
    {
        null or "" or "off" => "none",
        _ => preset,
    };

    public static bool IsOff(string? preset)
        => CanonicalPreset(preset) == "none";

    /// <summary>True when this value is one of the eight gallery cells (Off included).</summary>
    public static bool IsGalleryPreset(string? preset)
    {
        var key = CanonicalPreset(preset);
        return GalleryPerLine.Contains(key) || GalleryPerWord.Contains(key);
    }

    /// <summary>
    /// Cell to highlight for a stored preset, or <c>null</c> when the project uses a hidden
    /// preset (<c>fadeIn</c> / <c>wordPop</c> / <c>wordCycle</c>). Null means "select nothing" —
    /// never coerce those values to Off.
    /// </summary>
    public static string? GallerySelection(string? storedPreset)
    {
        var key = CanonicalPreset(storedPreset);
        return IsGalleryPreset(key) ? key : null;
    }

    /// <summary>
    /// Mac animates a cell when it is hovered, selected, or (WinUI) focused. Off never animates.
    /// A hidden panel or deactivated window must pass <paramref name="live"/> = false.
    /// </summary>
    public static bool GalleryCellAnimates(
        string? preset, bool live, bool selected, bool pointerOver, bool focused)
        => live && !IsOff(preset) && (selected || pointerOver || focused);

    /// <summary>Animation handed to the preview control for one gallery cell.</summary>
    public static TextAnimation? GalleryPreviewAnimation(string? preset, TextStyle.RGBA? highlight)
    {
        var key = CanonicalPreset(preset);
        if (IsOff(key)) return null;
        var animation = new TextAnimation { Preset = key };
        if (UsesHighlight(key))
            animation.Highlight = highlight ?? DefaultHighlight;
        return animation;
    }
}
