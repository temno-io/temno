using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temno.Core.Models;

/// <summary>Root of project.json. Mac: Models/ProjectFile.swift</summary>
public sealed class ProjectFile
{
    [JsonPropertyName("timelines")]
    public List<Timeline> Timelines { get; set; } = [];

    [JsonPropertyName("activeTimelineId")]
    public string? ActiveTimelineId { get; set; }

    [JsonPropertyName("openTimelineIds")]
    public List<string>? OpenTimelineIds { get; set; }

    [JsonPropertyName("viewStates")]
    public Dictionary<string, TimelineViewState>? ViewStates { get; set; }

    [JsonPropertyName("speakers")]
    public JsonElement? Speakers { get; set; }

    /// <summary>Mac: ProjectFile.multicamGroups</summary>
    [JsonPropertyName("multicamGroups")]
    public List<MulticamSource>? MulticamGroups { get; set; }

    public static ProjectFile Decode(ReadOnlySpan<byte> utf8Json)
    {
        var options = JsonOptions.ForProject;

        // Prefer modern ProjectFile when "timelines" array is present.
        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray());
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("timelines", out var timelinesEl)
                && timelinesEl.ValueKind == JsonValueKind.Array)
            {
                var file = JsonSerializer.Deserialize<ProjectFile>(utf8Json, options)
                    ?? throw new JsonException("project.json deserialized to null");
                if (file.Timelines.Count == 0)
                    throw new JsonException("project has no timelines");
                return file;
            }
        }
        catch (JsonException)
        {
            // fall through to legacy
        }

        // Legacy: bare Timeline root
        var legacy = JsonSerializer.Deserialize<Timeline>(utf8Json, options)
            ?? throw new JsonException("failed to decode project.json as ProjectFile or Timeline");
        return new ProjectFile
        {
            Timelines = [legacy],
            ActiveTimelineId = legacy.Id,
            OpenTimelineIds = [legacy.Id],
        };
    }

    public byte[] Encode()
        => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions.ForProject);
}

public sealed class TimelineViewState
{
    [JsonPropertyName("playheadFrame")]
    public int PlayheadFrame { get; set; }

    [JsonPropertyName("zoomScale")]
    public double ZoomScale { get; set; } = 4.0;

    [JsonPropertyName("scrollOffsetX")]
    public double ScrollOffsetX { get; set; }
}
