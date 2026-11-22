using System.Text.Json.Serialization;

namespace Temno.Core.Transcription;

/// <summary>Mac: Transcription/Transcription.swift — raw values are the serialized strings.</summary>
public enum TranscriptionProvider
{
    local,
    cloud,
}

/// <summary>Mac: Transcription/Transcription.swift — timestamps are SECONDS in source media.</summary>
public sealed class TranscriptionWord
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("start")]
    public double? Start { get; set; }

    [JsonPropertyName("end")]
    public double? End { get; set; }

    [JsonPropertyName("speaker")]
    public string? Speaker { get; set; }
}

/// <summary>Mac: Transcription/Transcription.swift — timestamps are SECONDS in source media.</summary>
public sealed class TranscriptionSegment
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("speaker")]
    public string? Speaker { get; set; }
}

/// <summary>
/// Mac: Transcription/Transcription.swift — JSON-persisted verbatim (cache files and
/// cloud results decode into this shape). Serialize with JsonOptions.ForProject.
/// </summary>
public sealed class TranscriptionResult
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("words")]
    public List<TranscriptionWord> Words { get; set; } = [];

    [JsonPropertyName("segments")]
    public List<TranscriptionSegment> Segments { get; set; } = [];

    /// <summary>Shifts all timestamps back into source time after transcribing an extracted range.</summary>
    public TranscriptionResult Offsetting(double offset)
    {
        if (offset == 0)
            return this;

        return new TranscriptionResult
        {
            Text = Text,
            Language = Language,
            Words = Words.Select(w => new TranscriptionWord
            {
                Text = w.Text,
                Start = w.Start + offset,
                End = w.End + offset,
                Speaker = w.Speaker,
            }).ToList(),
            Segments = Segments.Select(s => new TranscriptionSegment
            {
                Text = s.Text,
                Start = s.Start + offset,
                End = s.End + offset,
                Speaker = s.Speaker,
            }).ToList(),
        };
    }
}
