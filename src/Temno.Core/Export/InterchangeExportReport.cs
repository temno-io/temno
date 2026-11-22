using Temno.Core.Models;

namespace Temno.Core.Export;

/// <summary>
/// What an interchange export (XMEML, FCPXML) left out. <see cref="Omissions"/> counts timeline
/// content that is absent from the written file; <see cref="NotTransported"/> names the clip
/// properties the format never carries, so no export reads as full fidelity. WP-236.
/// </summary>
public sealed class InterchangeExportReport
{
    public required string Format { get; init; }
    public IReadOnlyList<InterchangeOmission> Omissions { get; init; } = [];
    public IReadOnlyList<string> NotTransported { get; init; } = [];

    /// <summary>"2 markers, 1 transition" — null when nothing was left out.</summary>
    public string? OmissionSummary => Omissions.Count == 0
        ? null
        : string.Join(", ", Omissions.Select(o => $"{o.Count} {(o.Count == 1 ? o.Singular : o.Kind)}"));
}

/// <param name="Kind">One of <see cref="InterchangeOmissionKinds"/>.</param>
/// <param name="Count">How many were left out, across the root and every nested timeline written.</param>
/// <param name="Detail">A sentence naming where they were, for a person or an agent to act on.</param>
public sealed record InterchangeOmission(string Kind, int Count, string Detail)
{
    internal string Singular => Kind.EndsWith('s') ? Kind[..^1] : Kind;
}

public static class InterchangeOmissionKinds
{
    public const string Markers = "markers";
    public const string Transitions = "transitions";
    /// <summary>Nested-sequence carriers written at normal speed because the format's nest carries no retime.</summary>
    public const string NestedSpeed = "nested speed changes";
    public const string Clips = "clips";
}

/// <summary>Tallies omissions per timeline while an exporter walks the timelines it writes.</summary>
internal sealed class InterchangeOmissionTally
{
    private readonly List<(string Timeline, int Count)> _markers = [];
    private readonly List<(string Timeline, int Count)> _transitions = [];
    private readonly List<(string Timeline, int Count)> _nestedSpeed = [];
    private readonly List<(string Timeline, string Reason)> _clips = [];

    /// <summary>Records the markers and rendered transitions of a timeline the file contains.</summary>
    public void TimelineWritten(Timeline timeline)
    {
        if (timeline.Markers.Count > 0)
            _markers.Add((timeline.Name, timeline.Markers.Count));
        var transitions = timeline.ResolvedTransitions.Count;
        if (transitions > 0)
            _transitions.Add((timeline.Name, transitions));
    }

    public void ClipSkipped(Timeline timeline, string reason) => _clips.Add((timeline.Name, reason));

    /// <summary>
    /// Records a nested-sequence carrier the file contains. Neither format retimes a nest (Mac does
    /// not let one be retimed), so a carrier with a speed change is written at normal speed.
    /// </summary>
    public void CarrierWritten(Timeline timeline, Clip carrier)
    {
        if (Math.Abs(carrier.Speed - 1.0) > 0.001)
            _nestedSpeed.Add((timeline.Name, 1));
    }

    public IReadOnlyList<InterchangeOmission> ToOmissions(string formatName)
    {
        var list = new List<InterchangeOmission>();
        if (_markers.Count > 0)
        {
            list.Add(new InterchangeOmission(
                InterchangeOmissionKinds.Markers,
                _markers.Sum(m => m.Count),
                $"{formatName} export does not carry timeline markers ({PerTimeline(_markers)})."));
        }
        if (_transitions.Count > 0)
        {
            list.Add(new InterchangeOmission(
                InterchangeOmissionKinds.Transitions,
                _transitions.Sum(t => t.Count),
                $"{formatName} export does not carry transitions; the clips meet as hard cuts ({PerTimeline(_transitions)})."));
        }
        if (_nestedSpeed.Count > 0)
        {
            list.Add(new InterchangeOmission(
                InterchangeOmissionKinds.NestedSpeed,
                _nestedSpeed.Count,
                $"{formatName} export does not carry speed on nested sequences; each such carrier plays its nest at normal speed from its trim ({PerTimeline(_nestedSpeed)})."));
        }
        if (_clips.Count > 0)
        {
            var reasons = _clips
                .GroupBy(c => c.Reason)
                .Select(g => $"{g.Count()} {g.Key}")
                .ToList();
            var timelines = _clips
                .GroupBy(c => c.Timeline)
                .Select(g => (Timeline: g.Key, Count: g.Count()))
                .ToList();
            list.Add(new InterchangeOmission(
                InterchangeOmissionKinds.Clips,
                _clips.Count,
                $"{formatName} export left out clips: {string.Join(", ", reasons)} ({PerTimeline(timelines)})."));
        }
        return list;
    }

    private static string PerTimeline(IEnumerable<(string Timeline, int Count)> rows)
        => string.Join(", ", rows
            .GroupBy(r => r.Timeline)
            .Select(g => $"{g.Key}: {g.Sum(r => r.Count)}"));
}
