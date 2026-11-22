using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temno.Core.Session;

/// <summary>
/// Named editor shell layouts. Mac: Utilities/Constants.swift LayoutPreset
/// (default / media / vertical). Preview is an extra Windows seed (not Mac).
/// </summary>
public enum EditorLayoutPreset
{
    Default,
    Media,
    Vertical,
    Preview,
}

/// <summary>
/// User-global editor layout (dividers + panel visibility + last preset).
/// Not stored in project packages. Mac: UserDefaults keys for layoutPreset
/// and NSSplitView autosave frames.
/// </summary>
public sealed class EditorLayoutPreferences
{
    public const double DefaultAgentWidth = 344;
    /// <summary>
    /// R3 media dock; the larger caption preset gallery lives in its defaults dialog.
    /// Matches <c>Layout.MediaPanelDefault</c> on the app side.
    /// </summary>
    public const double DefaultMediaWidth = 344;
    public const double DefaultInspectorWidth = 344;
    public const double DefaultTimelineHeight = 220;

    public const double MinAgentWidth = 240;
    public const double MaxAgentWidth = 640;
    public const double MinMediaWidth = 200;
    public const double MaxMediaWidth = 700;
    public const double MinInspectorWidth = 150;
    public const double MaxInspectorWidth = 480;
    public const double MinTimelineHeight = 100;
    public const double MaxTimelineHeight = 700;

    /// <summary>Below this absolute size a column is treated as collapsed (not persisted).</summary>
    public const double CollapsedEpsilon = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("preset")]
    public string Preset { get; set; } = "default";

    [JsonPropertyName("agentWidth")]
    public double AgentWidth { get; set; } = DefaultAgentWidth;

    [JsonPropertyName("mediaWidth")]
    public double MediaWidth { get; set; } = DefaultMediaWidth;

    [JsonPropertyName("inspectorWidth")]
    public double InspectorWidth { get; set; } = DefaultInspectorWidth;

    [JsonPropertyName("timelineHeight")]
    public double TimelineHeight { get; set; } = DefaultTimelineHeight;

    /// <summary>
    /// WP-185. True until the user drags the timeline divider: while true the editor sizes the
    /// timeline to the open project's tracks on load (bounded by a share of the window) so the
    /// audio tracks are visible without scrolling. A dragged divider is an explicit choice and
    /// turns this off for good.
    /// </summary>
    [JsonPropertyName("timelineHeightAuto")]
    public bool TimelineHeightAuto { get; set; } = true;

    [JsonPropertyName("agentVisible")]
    public bool AgentVisible { get; set; } = false;

    [JsonPropertyName("mediaVisible")]
    public bool MediaVisible { get; set; } = true;

    [JsonPropertyName("inspectorVisible")]
    public bool InspectorVisible { get; set; } = true;

    public EditorLayoutPreset ParsedPreset
    {
        get => ParsePreset(Preset);
        set => Preset = ToWire(value);
    }

    public static string ToWire(EditorLayoutPreset preset) => preset switch
    {
        EditorLayoutPreset.Default => "default",
        EditorLayoutPreset.Media => "media",
        EditorLayoutPreset.Vertical => "vertical",
        EditorLayoutPreset.Preview => "preview",
        _ => "default",
    };

    public static EditorLayoutPreset ParsePreset(string? raw) =>
        (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "media" => EditorLayoutPreset.Media,
            "vertical" => EditorLayoutPreset.Vertical,
            "preview" => EditorLayoutPreset.Preview,
            _ => EditorLayoutPreset.Default,
        };

    /// <summary>Seed dimensions/visibility for a named preset (Mac ratios approximated).</summary>
    public static EditorLayoutPreferences ForPreset(EditorLayoutPreset preset)
    {
        return preset switch
        {
            EditorLayoutPreset.Media => Clamp(new EditorLayoutPreferences
            {
                Preset = ToWire(EditorLayoutPreset.Media),
                AgentVisible = false,
                MediaVisible = true,
                InspectorVisible = true,
                AgentWidth = DefaultAgentWidth,
                MediaWidth = DefaultMediaWidth,
                InspectorWidth = DefaultInspectorWidth,
                TimelineHeight = 200,
            }),
            EditorLayoutPreset.Vertical => Clamp(new EditorLayoutPreferences
            {
                // Mac vertical: [Media|Inspector]/Timeline | Preview (side).
                Preset = ToWire(EditorLayoutPreset.Vertical),
                AgentVisible = false,
                MediaVisible = true,
                InspectorVisible = true,
                AgentWidth = DefaultAgentWidth,
                MediaWidth = 360,
                InspectorWidth = DefaultInspectorWidth,
                TimelineHeight = 280,
            }),
            EditorLayoutPreset.Preview => Clamp(new EditorLayoutPreferences
            {
                Preset = ToWire(EditorLayoutPreset.Preview),
                AgentVisible = false,
                MediaVisible = false,
                InspectorVisible = false,
                AgentWidth = DefaultAgentWidth,
                MediaWidth = DefaultMediaWidth,
                InspectorWidth = DefaultInspectorWidth,
                TimelineHeight = 180,
            }),
            _ => Clamp(new EditorLayoutPreferences
            {
                // Match Mac empty-project default silhouette: agent collapsed, media + inspector on.
                Preset = ToWire(EditorLayoutPreset.Default),
                AgentVisible = false,
                MediaVisible = true,
                InspectorVisible = true,
                AgentWidth = DefaultAgentWidth,
                MediaWidth = DefaultMediaWidth,
                InspectorWidth = DefaultInspectorWidth,
                TimelineHeight = 240,
            }),
        };
    }

    /// <summary>
    /// Clamps sizes into safe ranges. Rejects non-finite or collapsed-looking
    /// widths so maximize/star/zero never overwrite persisted user values.
    /// </summary>
    public static EditorLayoutPreferences Clamp(EditorLayoutPreferences raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var p = new EditorLayoutPreferences
        {
            Version = raw.Version <= 0 ? 1 : raw.Version,
            Preset = ToWire(ParsePreset(raw.Preset)),
            AgentVisible = raw.AgentVisible,
            MediaVisible = raw.MediaVisible,
            InspectorVisible = raw.InspectorVisible,
            AgentWidth = ClampWidth(raw.AgentWidth, DefaultAgentWidth, MinAgentWidth, MaxAgentWidth),
            MediaWidth = ClampWidth(raw.MediaWidth, DefaultMediaWidth, MinMediaWidth, MaxMediaWidth),
            InspectorWidth = ClampWidth(raw.InspectorWidth, DefaultInspectorWidth, MinInspectorWidth, MaxInspectorWidth),
            TimelineHeight = ClampWidth(raw.TimelineHeight, DefaultTimelineHeight, MinTimelineHeight, MaxTimelineHeight),
            TimelineHeightAuto = raw.TimelineHeightAuto,
        };
        return p;
    }

    /// <summary>
    /// Merges live divider sizes into prefs only when values are absolute and
    /// above <see cref="CollapsedEpsilon"/>. Star/zero/NaN do not overwrite.
    /// </summary>
    public static EditorLayoutPreferences MergeLiveSizes(
        EditorLayoutPreferences current,
        double? agentWidth,
        double? mediaWidth,
        double? inspectorWidth,
        double? timelineHeight,
        bool? agentVisible = null,
        bool? mediaVisible = null,
        bool? inspectorVisible = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        var next = Clone(current);
        if (agentVisible is { } av) next.AgentVisible = av;
        if (mediaVisible is { } mv) next.MediaVisible = mv;
        if (inspectorVisible is { } iv) next.InspectorVisible = iv;

        if (IsPersistableSize(agentWidth, MinAgentWidth, MaxAgentWidth))
            next.AgentWidth = agentWidth!.Value;
        if (IsPersistableSize(mediaWidth, MinMediaWidth, MaxMediaWidth))
            next.MediaWidth = mediaWidth!.Value;
        if (IsPersistableSize(inspectorWidth, MinInspectorWidth, MaxInspectorWidth))
            next.InspectorWidth = inspectorWidth!.Value;
        if (IsPersistableSize(timelineHeight, MinTimelineHeight, MaxTimelineHeight))
            next.TimelineHeight = timelineHeight!.Value;

        return Clamp(next);
    }

    public static bool IsPersistableSize(double? value, double min, double max)
    {
        if (value is null) return false;
        if (double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return false;
        // Reject collapsed / star-as-1 masquerading as a pixel size.
        if (value.Value < CollapsedEpsilon) return false;
        if (value.Value < min * 0.5) return false; // far below min → not a real divider
        if (value.Value > max * 1.5) return false;
        return true;
    }

    private static double ClampWidth(double value, double fallback, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < CollapsedEpsilon)
            return fallback;
        return Math.Clamp(value, min, max);
    }

    public static EditorLayoutPreferences Clone(EditorLayoutPreferences src)
    {
        ArgumentNullException.ThrowIfNull(src);
        return new EditorLayoutPreferences
        {
            Version = src.Version,
            Preset = src.Preset,
            AgentWidth = src.AgentWidth,
            MediaWidth = src.MediaWidth,
            InspectorWidth = src.InspectorWidth,
            TimelineHeight = src.TimelineHeight,
            TimelineHeightAuto = src.TimelineHeightAuto,
            AgentVisible = src.AgentVisible,
            MediaVisible = src.MediaVisible,
            InspectorVisible = src.InspectorVisible,
        };
    }

    public string ToJson() => JsonSerializer.Serialize(Clamp(this), JsonOptions);

    public static EditorLayoutPreferences FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ForPreset(EditorLayoutPreset.Default);
        try
        {
            var parsed = JsonSerializer.Deserialize<EditorLayoutPreferences>(json, JsonOptions);
            return parsed is null ? ForPreset(EditorLayoutPreset.Default) : Clamp(parsed);
        }
        catch (JsonException)
        {
            return ForPreset(EditorLayoutPreset.Default);
        }
    }
}
