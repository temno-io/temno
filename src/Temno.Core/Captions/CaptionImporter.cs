using System.Text.Json;
using Temno.Core.Models;
using Temno.Core.Session;

namespace Temno.Core.Captions;

/// <summary>Clips built from caption cues plus notes about trimmed/dropped cues.</summary>
public sealed class CaptionImportResult
{
    public List<Clip> Clips { get; } = [];

    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Converts caption cues (SECONDS, file domain) to timeline text clips (FRAMES)
/// and back. Rounding policy: frame = round(seconds * fps) half-away-from-zero,
/// matching Swift round(); cue duration floors at 1 frame after rounding.
/// Mac: Editor/ViewModel/EditorViewModel+Captions.swift (placeCaptionTrack) for
/// clip shape and track placement; subtitle cue import is Mac add_captions
/// subtitleMediaRef, which forces gap closing off (see
/// <see cref="CaptionSpecBuilder.SubtitleImportGapSeconds"/>).
/// </summary>
public static class CaptionImporter
{
    public static int FrameFromSeconds(double seconds, int fps)
    {
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps), "fps must be positive");
        return (int)Math.Round(seconds * fps, MidpointRounding.AwayFromZero);
    }

    public static double SecondsFromFrame(int frame, int fps)
    {
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps), "fps must be positive");
        return frame / (double)fps;
    }

    /// <summary>
    /// Builds one text clip per cue (sorted by start time). Timing policy is
    /// <see cref="CaptionSpecBuilder"/>: overlap ownership first, then gap closing (off by default
    /// for subtitle import, matching Mac). A cue a later cue fully covers is dropped, and one that
    /// loses frames to its neighbour is reported in <see cref="CaptionImportResult.Warnings"/>.
    /// Negative starts clamp to frame 0. All clips share one captionGroupId (fresh unless
    /// supplied), mirroring the Mac caption-generation pipeline. When style is null the TextStyle
    /// defaults are serialized explicitly so the clip round-trips like a Mac text clip.
    /// </summary>
    /// <param name="transformFor">
    /// Per-cue transform factory (Mac CaptionBuilder.specs transformFor). Takes precedence over
    /// <paramref name="transform"/>, which is cloned for every clip when no factory is given.
    /// </param>
    public static CaptionImportResult BuildTextClips(
        IReadOnlyList<CaptionEntry> entries,
        int fps,
        TextStyle? style = null,
        Transform? transform = null,
        string? captionGroupId = null,
        double maximumGapSeconds = CaptionSpecBuilder.SubtitleImportGapSeconds,
        Func<string, Transform?>? transformFor = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps), "fps must be positive");

        var result = new CaptionImportResult();
        var ordered = entries.OrderBy(e => e.StartSeconds).ToList();
        var groupId = captionGroupId ?? Timeline.NewId();
        var styleElement = JsonSerializer.SerializeToElement(
            style ?? new TextStyle(), JsonOptions.ForProject);

        var built = new List<Clip>(ordered.Count);
        var cues = new List<(Clip Clip, int Index, int StartFrame, int DurationFrames)>(ordered.Count);

        foreach (var entry in ordered)
        {
            var startFrame = Math.Max(0, FrameFromSeconds(entry.StartSeconds, fps));
            var endFrame = FrameFromSeconds(entry.EndSeconds, fps);
            var duration = Math.Max(1, endFrame - startFrame);

            var clip = new Clip
            {
                MediaRef = "",
                MediaType = ClipType.text,
                SourceClipType = ClipType.text,
                StartFrame = startFrame,
                DurationFrames = duration,
                CaptionGroupId = groupId,
                TextContent = entry.Text,
                TextStyle = styleElement,
            };
            var fitted = transformFor?.Invoke(entry.Text);
            if (fitted is not null)
                clip.Transform = fitted;
            else if (transform is not null)
                clip.Transform = CloneTransform(transform);

            built.Add(clip);
            cues.Add((clip, entry.Index, startFrame, duration));
        }

        CaptionSpecBuilder.Apply(built, fps, maximumGapSeconds);

        foreach (var cue in cues)
        {
            if (!built.Any(c => ReferenceEquals(c, cue.Clip)))
            {
                result.Warnings.Add(
                    $"Cue {cue.Index}: dropped; a later cue starts on or before its start frame.");
            }
            else if (cue.Clip.DurationFrames < cue.DurationFrames || cue.Clip.StartFrame != cue.StartFrame)
            {
                // Compare the start too: a cue that lost its first frame to the previous cue and was
                // then extended forward by gap closing can end up longer than it started.
                result.Warnings.Add(
                    $"Cue {cue.Index}: trimmed to the next cue's start to avoid overlap.");
            }
        }

        result.Clips.AddRange(built);
        return result;
    }

    /// <summary>
    /// Imports cues into the active timeline as one undoable edit. With a null
    /// trackIndex a new video track is inserted at index 0 (topmost — Mac
    /// placeCaptionTrack behavior); otherwise the chosen track must be a video
    /// track and the new clips must not overlap its existing clips.
    /// </summary>
    public static EditResult ImportCaptions(
        ProjectSession session,
        IReadOnlyList<CaptionEntry> entries,
        int? trackIndex = null,
        TextStyle? style = null,
        Transform? transform = null,
        double maximumGapSeconds = CaptionSpecBuilder.SubtitleImportGapSeconds,
        Func<string, Transform?>? transformFor = null,
        string? captionGroupId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return EditResult.Fail("no caption entries to import");

        session.RecordBeforeEdit();
        var timeline = session.ActiveTimeline;
        var build = BuildTextClips(
            entries, timeline.Fps, style, transform, captionGroupId, maximumGapSeconds, transformFor);
        if (build.Clips.Count == 0)
        {
            session.RollbackLastRecord();
            return EditResult.Fail("no importable caption cues");
        }

        Track track;
        if (trackIndex is int index)
        {
            if (index < 0 || index >= timeline.Tracks.Count)
            {
                session.RollbackLastRecord();
                return EditResult.Fail($"trackIndex {index} out of range");
            }
            track = timeline.Tracks[index];
            if (track.Type != ClipType.video)
            {
                session.RollbackLastRecord();
                return EditResult.Fail("captions require a video track");
            }
            foreach (var clip in build.Clips)
            {
                var overlaps = track.Clips.Any(
                    c => c.StartFrame < clip.EndFrame && clip.StartFrame < c.EndFrame);
                if (overlaps)
                {
                    session.RollbackLastRecord();
                    return EditResult.Fail(
                        $"caption clips would overlap existing clips on track {index}");
                }
            }
        }
        else
        {
            track = new Track { Type = ClipType.video };
            timeline.Tracks.Insert(0, track);
        }

        track.Clips.AddRange(build.Clips);
        track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
        session.MarkDirty();
        return EditResult.Success();
    }

    /// <summary>
    /// Reverse mapping: text clips → cues. Non-text clips and clips with no
    /// text content are ignored; cues are ordered by start frame and numbered
    /// sequentially from 1.
    /// </summary>
    public static List<CaptionEntry> ToEntries(IEnumerable<Clip> clips, int fps)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps), "fps must be positive");

        return clips
            .Where(c => c.MediaType == ClipType.text && !string.IsNullOrWhiteSpace(c.TextContent))
            .OrderBy(c => c.StartFrame)
            .Select((c, i) => new CaptionEntry
            {
                Index = i + 1,
                StartSeconds = SecondsFromFrame(c.StartFrame, fps),
                EndSeconds = SecondsFromFrame(c.EndFrame, fps),
                Text = c.TextContent!,
            })
            .ToList();
    }

    public static string ExportSrt(IEnumerable<Clip> clips, int fps)
        => SrtParser.Serialize(ToEntries(clips, fps));

    public static string ExportVtt(IEnumerable<Clip> clips, int fps)
        => VttParser.Serialize(ToEntries(clips, fps));

    private static Transform CloneTransform(Transform t) => new()
    {
        CenterX = t.CenterX,
        CenterY = t.CenterY,
        Width = t.Width,
        Height = t.Height,
        Rotation = t.Rotation,
        FlipHorizontal = t.FlipHorizontal,
        FlipVertical = t.FlipVertical,
    };
}
