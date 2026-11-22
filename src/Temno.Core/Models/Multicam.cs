using System.Text.Json.Serialization;

namespace Temno.Core.Models;

/// <summary>Mac: Models/MulticamSource.swift</summary>
public sealed class MulticamSource
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Timeline.NewId();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("members")]
    public List<MulticamMember> Members { get; set; } = [];

    [JsonPropertyName("masterMemberId")]
    public string MasterMemberId { get; set; } = "";

    [JsonIgnore]
    public MulticamMember? Master => Members.FirstOrDefault(m => m.Id == MasterMemberId);

    [JsonIgnore]
    public IEnumerable<MulticamMember> Angles => Members.Where(m => m.ProvidesVideo && m.Usable);

    [JsonIgnore]
    public IEnumerable<MulticamMember> Mics => Members.Where(m => m.ProvidesAudio && m.Usable);

    public MulticamMember? MemberLabeled(string label)
        => Members.FirstOrDefault(m => string.Equals(m.AngleLabel, label, StringComparison.OrdinalIgnoreCase));

    public MulticamMember? MemberByMediaRef(string mediaRef)
        => Members.FirstOrDefault(m => m.MediaRef == mediaRef);
}

public sealed class MulticamMember
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Timeline.NewId();

    [JsonPropertyName("mediaRef")]
    public string MediaRef { get; set; } = "";

    [JsonPropertyName("kind")]
    public MulticamMemberKind Kind { get; set; } = MulticamMemberKind.angle;

    [JsonPropertyName("angleLabel")]
    public string AngleLabel { get; set; } = "";

    [JsonPropertyName("sync")]
    public MulticamSyncMap Sync { get; set; } = new();

    [JsonIgnore]
    public bool ProvidesVideo => Kind != MulticamMemberKind.mic;

    [JsonIgnore]
    public bool ProvidesAudio => Kind != MulticamMemberKind.angle;

    [JsonIgnore]
    public bool Usable => Sync.Confidence > 0 || Sync.Locked;

    public int OffsetFrames(int fps)
        => (int)Math.Round(Sync.OffsetSeconds * Math.Max(1, fps));

    /// <summary>
    /// Timeline frame of the group origin implied by this member's clip (Mac MulticamSource.Member.anchorFrame).
    /// </summary>
    public int AnchorFrame(Clip clip, int fps)
        => clip.StartFrame - clip.TrimStartFrame - OffsetFrames(fps);

    /// <summary>Group-timeline frame range this member covers given source duration (seconds).</summary>
    public (int Start, int End) Coverage(double sourceDurationSeconds, int fps)
    {
        fps = Math.Max(1, fps);
        var start = (int)Math.Round(Sync.OffsetSeconds * fps);
        var end = (int)Math.Round((Sync.OffsetSeconds + sourceDurationSeconds) * fps);
        return (start, Math.Max(start, end));
    }

    public int TrimFrameAtGroupFrame(int groupFrame, int fps)
    {
        fps = Math.Max(1, fps);
        return (int)Math.Round(groupFrame - Sync.OffsetSeconds * fps);
    }
}

public sealed class MulticamSyncMap
{
    [JsonPropertyName("offsetSeconds")]
    public double OffsetSeconds { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MulticamMemberKind
{
    angle,
    mic,
    both,
}
