using System.Text.Json.Serialization;

namespace Temno.Core.Audio;

/// <summary>
/// Beat / downbeat analysis for a single media file. Times are SECONDS in source media
/// (same units as Mac BeatAnalysis and transcription timestamps).
/// Mac: Audio/Beats/BeatDetector.swift
/// </summary>
public sealed class BeatAnalysis : IEquatable<BeatAnalysis>
{
    /// <summary>Estimated tempo; 0 when indeterminate.</summary>
    [JsonPropertyName("bpm")]
    public double Bpm { get; set; }

    /// <summary>Beat onset times in source-media seconds.</summary>
    [JsonPropertyName("beats")]
    public double[] Beats { get; set; } = [];

    /// <summary>Downbeat (bar start) times in source-media seconds.</summary>
    [JsonPropertyName("downbeats")]
    public double[] Downbeats { get; set; } = [];

    public static BeatAnalysis Empty { get; } = new()
    {
        Bpm = 0,
        Beats = [],
        Downbeats = [],
    };

    public bool Equals(BeatAnalysis? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Bpm.Equals(other.Bpm)
               && Beats.AsSpan().SequenceEqual(other.Beats)
               && Downbeats.AsSpan().SequenceEqual(other.Downbeats);
    }

    public override bool Equals(object? obj) => obj is BeatAnalysis other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Bpm);
        foreach (var b in Beats)
        {
            hash.Add(b);
        }

        foreach (var d in Downbeats)
        {
            hash.Add(d);
        }

        return hash.ToHashCode();
    }
}
