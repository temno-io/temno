using System.Text.Json;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Temno.Core.Ui;

namespace Temno.Core.Captions;

/// <summary>Mac: EditorViewModel.CaptionCase.</summary>
public enum CaptionCase
{
    auto,
    upper,
    lower,
}

/// <summary>Options for agent caption generation. Mac: EditorViewModel.CaptionRequest.</summary>
public sealed class CaptionRequest
{
    /// <summary>Mac AppTheme.Caption.defaultFontSize = 48.</summary>
    public TextStyle Style { get; set; } = new() { FontSize = 48 };

    /// <summary>Mac AppTheme.Caption.defaultCenter = (0.5, 0.9).</summary>
    public double CenterX { get; set; } = 0.5;

    public double CenterY { get; set; } = 0.9;

    public CaptionCase TextCase { get; set; } = CaptionCase.auto;

    public int? MaxWords { get; set; }

    /// <summary>Mac add_captions maxCharacters: hard character cap per caption line.</summary>
    public int? MaxCharacters { get; set; }

    /// <summary>
    /// Mac CaptionGapSettings: captions are extended across gaps at or under this many seconds so
    /// they do not flicker off between phrases. Range 0…2; 0 leaves gaps as transcribed.
    /// </summary>
    public double MaximumGapSeconds { get; set; } = CaptionSpecBuilder.DefaultMaximumGapSeconds;

    /// <summary>
    /// Mac add_captions trackIndex: caption only this track instead of auto-detecting the track
    /// carrying the most speech.
    /// </summary>
    public int? TrackIndex { get; set; }

    /// <summary>Applied when non-null (Mac: request.animation.isActive).</summary>
    public TextAnimation? Animation { get; set; }

    public string? Language { get; set; }
}

public sealed class CaptionGenerationResult
{
    public List<string> CreatedClipIds { get; } = [];
    public List<TranscriptSkip> Skipped { get; } = [];
    public string TranscriptionSource { get; set; } = "";
}

public sealed record CaptionSourceTrack(
    string TrackId,
    int TrackIndex,
    string Label,
    ClipType TrackType,
    int ClipCount)
{
    public string DisplayName => $"{Label} · {ClipCount} {(ClipCount == 1 ? "clip" : "clips")}";
}

/// <summary>
/// Transcribes the timeline's spoken audio and creates styled caption text clips on a new top
/// track. Mac: EditorViewModel.generateCaptions + CaptionTranscriptMapper + CaptionBuilder.specs.
/// </summary>
public static class CaptionGenerator
{
    /// <summary>
    /// Mac CaptionBuilder.specs uses minDurationFrames: 1 — the visible floor is one frame, applied
    /// in the frame domain by <see cref="BuildCaptionClip"/>. There is no seconds-domain phrase
    /// floor (WP-115 removed the Windows-only 0.7 s constant).
    /// </summary>
    private const double NoPhraseDurationFloorSeconds = 0;

    /// <summary>Mac AppTheme.ComponentSize.captionPreviewMaxTextWidthRatio.</summary>
    private const double MaxTextWidthRatio = 0.9;

    public static async Task<CaptionGenerationResult> GenerateAsync(
        ProjectSession session,
        ITranscriptionProvider provider,
        CaptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);

        var result = new CaptionGenerationResult { TranscriptionSource = provider.Provider.ToString() };
        var timeline = session.ActiveTimeline;
        var fps = Math.Max(1, timeline.Fps);

        var targets = ResolveTargets(session, result.Skipped);
        if (request.TrackIndex is { } wanted)
            targets.RemoveAll(t => t.TrackIndex != wanted);
        if (targets.Count == 0) return result;

        // Transcribe each unique file once.
        var transcripts = new Dictionary<string, TranscriptionResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in targets.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (TranscriptDiskCache.TryLoad(path, out var cached) && cached is not null)
                {
                    transcripts[path] = cached;
                    continue;
                }

                var transcript = await provider
                    .TranscribeAsync(path, request.Language, cancellationToken)
                    .ConfigureAwait(false);
                transcripts[path] = transcript;
                try { TranscriptDiskCache.Store(path, transcript); }
                catch { /* cache write is best-effort */ }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                foreach (var t in targets.Where(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)))
                    result.Skipped.Add(new TranscriptSkip(t.Clip.Id, t.Clip.MediaRef, $"transcription failed: {ex.Message}"));
            }
        }
        targets.RemoveAll(t => !transcripts.ContainsKey(t.Path));
        if (targets.Count == 0) return result;

        // Explicit track already narrowed targets above; otherwise caption the track with the most
        // spoken words (Mac dominantSpeechTrack).
        if (request.TrackIndex is null)
        {
            var wordsByTrack = new Dictionary<int, int>();
            foreach (var t in targets)
            {
                var count = SpokenWordCount(t.Clip, transcripts[t.Path], fps);
                wordsByTrack[t.TrackIndex] = wordsByTrack.GetValueOrDefault(t.TrackIndex) + count;
            }
            var spoken = wordsByTrack.Where(kv => kv.Value > 0).ToList();
            if (spoken.Count == 0) return result;
            var dominantTrack = spoken.MaxBy(kv => kv.Value).Key;
            targets.RemoveAll(t => t.TrackIndex != dominantTrack);
        }

        // Build caption clip specs.
        var canvasW = Math.Max(1, timeline.Width);
        var canvasH = Math.Max(1, timeline.Height);
        bool Fits(string line) => EstimateLineWidth(line, request.Style, canvasH) <= canvasW * MaxTextWidthRatio;

        var groupId = Timeline.NewId();
        var styleElement = JsonSerializer.SerializeToElement(request.Style, JsonOptions.ForProject);
        var animationElement = request.Animation is { } anim && anim.Preset != "none"
            ? JsonSerializer.SerializeToElement(anim, JsonOptions.ForProject)
            : (JsonElement?)null;

        var clips = new List<Clip>();
        int? captionedEnd = null;
        foreach (var target in targets.OrderBy(t => t.Clip.StartFrame))
        {
            var clipsBefore = clips.Count;
            var phrases = PhrasesForClip(
                target.Clip, transcripts[target.Path], fps, request.MaxWords, Fits, request.MaxCharacters);
            foreach (var phrase in phrases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TranscriptionSpecials.IsSpecialToken(phrase.Text))
                    continue;
                var cased = ApplyCase(request.TextCase, phrase.Text);
                var clip = BuildCaptionClip(
                    target.Clip, phrase, cased, fps,
                    styleElement, animationElement, groupId,
                    request, canvasW, canvasH);
                if (clip is not null) clips.Add(clip);
            }

            // Only a clip that actually produced captions may extend the captioned span. A trailing
            // silent clip must not let the last caption be held out over footage nobody transcribed.
            if (clips.Count > clipsBefore)
                captionedEnd = Math.Max(captionedEnd ?? target.Clip.EndFrame, target.Clip.EndFrame);
        }
        if (clips.Count == 0) return result;

        // Mac CaptionSpecBuilder: settle overlap ownership, then close short gaps and hold the last
        // caption to the end of the captioned span.
        CaptionSpecBuilder.Apply(clips, fps, request.MaximumGapSeconds, captionedEnd);
        if (clips.Count == 0) return result;

        // Place on a new top video track as one undoable edit (Mac placeCaptionTrack).
        session.RecordBeforeEdit();
        var track = new Track { Type = ClipType.video };
        track.Clips.AddRange(clips.OrderBy(c => c.StartFrame));
        timeline.Tracks.Insert(0, track);
        session.MarkDirty();

        result.CreatedClipIds.AddRange(clips.Select(c => c.Id));
        return result;
    }

    /// <summary>
    /// Lists tracks carrying speech-capable clips using the same link-group and audio rules as
    /// <see cref="ResolveTargets"/>, labeled with timeline track names (<c>A1</c>, <c>V1</c>, etc.).
    /// Matches Mac <c>CaptionTab.captionTrackIndices</c>.
    /// </summary>
    public static IReadOnlyList<CaptionSourceTrack> ListSourceTracks(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var timeline = session.ActiveTimeline;
        var entriesById = session.Manifest.Entries
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var linkGroupsWithAudio = timeline.Tracks
            .SelectMany(t => t.Clips)
            .Where(c => c.MediaType == ClipType.audio && !string.IsNullOrEmpty(c.LinkGroupId))
            .Select(c => c.LinkGroupId!)
            .ToHashSet(StringComparer.Ordinal);
        var trackTypes = timeline.Tracks.Select(t => t.Type).ToList();

        var result = new List<CaptionSourceTrack>();
        for (var i = 0; i < timeline.Tracks.Count; i++)
        {
            var track = timeline.Tracks[i];
            var count = 0;
            foreach (var clip in track.Clips)
            {
                if (clip.MediaType is not (ClipType.video or ClipType.audio)) continue;
                if (string.IsNullOrEmpty(clip.MediaRef) || !string.IsNullOrEmpty(clip.TextContent)) continue;
                if (clip.MediaType == ClipType.video
                    && !string.IsNullOrEmpty(clip.LinkGroupId)
                    && linkGroupsWithAudio.Contains(clip.LinkGroupId!))
                    continue;
                if (entriesById.TryGetValue(clip.MediaRef, out var entry) && entry.HasAudio == false)
                    continue;
                count++;
            }
            if (count > 0)
            {
                result.Add(new CaptionSourceTrack(
                    track.Id,
                    i,
                    TrackLabels.LabelFor(trackTypes, i),
                    track.Type,
                    count));
            }
        }
        return result;
    }

    private sealed record CaptionTarget(Clip Clip, int TrackIndex, string Path);

    /// <summary>
    /// Speech-capable clips; when a link group contains audio, the audio clip carries the speech
    /// and its linked video partners are excluded (Mac captionTargets link-group rule).
    /// </summary>
    private static List<CaptionTarget> ResolveTargets(ProjectSession session, List<TranscriptSkip> skipped)
    {
        var transfer = session.Resolver.ForTransfer();
        var timeline = session.ActiveTimeline;
        var entriesById = session.Manifest.Entries
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var linkGroupsWithAudio = timeline.Tracks
            .SelectMany(t => t.Clips)
            .Where(c => c.MediaType == ClipType.audio && !string.IsNullOrEmpty(c.LinkGroupId))
            .Select(c => c.LinkGroupId!)
            .ToHashSet(StringComparer.Ordinal);

        var targets = new List<CaptionTarget>();
        for (var trackIndex = 0; trackIndex < timeline.Tracks.Count; trackIndex++)
        {
            foreach (var clip in timeline.Tracks[trackIndex].Clips)
            {
                if (clip.MediaType is not (ClipType.video or ClipType.audio)) continue;
                if (clip.MediaType == ClipType.video
                    && !string.IsNullOrEmpty(clip.LinkGroupId)
                    && linkGroupsWithAudio.Contains(clip.LinkGroupId!))
                    continue;
                if (!entriesById.TryGetValue(clip.MediaRef, out var entry))
                {
                    skipped.Add(new TranscriptSkip(clip.Id, clip.MediaRef, "media entry not found"));
                    continue;
                }
                if (entry.HasAudio == false)
                {
                    skipped.Add(new TranscriptSkip(clip.Id, clip.MediaRef, "media has no audio stream"));
                    continue;
                }
                // WP-399: the audio may go to a hosted transcription provider, so an external file must be
                // one the user allowed and must still lead where it did then.
                if (transfer.IsBlocked(entry))
                {
                    skipped.Add(new TranscriptSkip(clip.Id, clip.MediaRef, "media is outside the project and not allowed"));
                    continue;
                }
                var path = transfer.ResolvePath(entry);
                if (path is null && entry.Source is MediaSource.ExternalSource)
                {
                    skipped.Add(new TranscriptSkip(clip.Id, clip.MediaRef,
                        "media is outside the project and now leads somewhere other than where it did when it was allowed"));
                    continue;
                }
                if (path is null || !File.Exists(path))
                {
                    skipped.Add(new TranscriptSkip(clip.Id, clip.MediaRef, "media is offline"));
                    continue;
                }
                targets.Add(new CaptionTarget(clip, trackIndex, System.IO.Path.GetFullPath(path)));
            }
        }
        return targets;
    }

    /// <summary>Mac CaptionTranscriptMapper.sourceSpan (source frames).</summary>
    private static (double Start, double End) SourceSpan(Clip clip)
    {
        var start = (double)clip.TrimStartFrame;
        return (start, start + clip.DurationFrames * Math.Max(clip.Speed, 0.0001));
    }

    /// <summary>Mac CaptionTranscriptMapper.spokenWordCount.</summary>
    internal static int SpokenWordCount(Clip clip, TranscriptionResult transcription, int fps)
    {
        var visible = SourceSpan(clip);
        var count = 0;
        foreach (var word in transcription.Words)
        {
            if (word.Start is not { } s || word.End is not { } e) continue;
            if (TranscriptionSpecials.IsSpecialToken(word.Text)) continue;
            var midFrame = (s + e) / 2 * fps;
            if (midFrame >= visible.Start && midFrame < visible.End) count++;
        }
        return count;
    }

    /// <summary>Mac CaptionTranscriptMapper.phrases: word-timing path preferred, segment fallback.</summary>
    internal static List<CaptionPhraseBuilder.Phrase> PhrasesForClip(
        Clip clip, TranscriptionResult transcription, int fps, int? maxWords, Func<string, bool> fits,
        int? maxCharacters = null)
    {
        var source = SourceSpan(clip);
        var visibleStart = source.Start / fps;
        var visibleEnd = source.End / fps;
        if (visibleEnd <= visibleStart) return [];

        var hasWordTimings = transcription.Words.Any(w => w.Start is not null && w.End is not null);
        if (hasWordTimings)
            return PhrasesWithWordTimings(visibleStart, visibleEnd, transcription, maxWords, fits, maxCharacters);

        var phrases = new List<CaptionPhraseBuilder.Phrase>();
        foreach (var segment in transcription.Segments)
        {
            var start = Math.Max(segment.Start, visibleStart);
            var end = Math.Min(segment.End, visibleEnd);
            if (end <= start) continue;
            var spoken = TranscriptionSpecials.StripSpecialTokens(segment.Text);
            if (spoken.Length == 0) continue;
            var clipped = new TranscriptionSegment { Text = spoken, Start = start, End = end, Speaker = segment.Speaker };
            phrases.AddRange(CaptionPhraseBuilder.Phrases(
                clipped, [], fits, maxWords, NoPhraseDurationFloorSeconds, maxCharacters));
        }
        return phrases;
    }

    private static List<CaptionPhraseBuilder.Phrase> PhrasesWithWordTimings(
        double visibleStart, double visibleEnd, TranscriptionResult transcription, int? maxWords,
        Func<string, bool> fits, int? maxCharacters)
    {
        var segments = transcription.Segments.Count > 0
            ? transcription.Segments
            : [FallbackSegment(transcription)];

        var phrases = new List<CaptionPhraseBuilder.Phrase>();
        var wordIndex = 0;
        var words = transcription.Words;
        foreach (var segment in segments)
        {
            while (wordIndex < words.Count)
            {
                if (words[wordIndex].Start is not { } s || words[wordIndex].End is not { } e) { wordIndex++; continue; }
                if ((s + e) / 2 < segment.Start) { wordIndex++; continue; }
                break;
            }

            var i = wordIndex;
            var segmentWords = new List<TranscriptionWord>();
            while (i < words.Count)
            {
                var word = words[i];
                if (word.Start is not { } s || word.End is not { } e) { i++; continue; }
                if (TranscriptionSpecials.IsSpecialToken(word.Text)) { i++; continue; }
                var mid = (s + e) / 2;
                if (mid >= segment.End) break;
                if (mid >= segment.Start && mid >= visibleStart && mid < visibleEnd)
                    segmentWords.Add(word);
                i++;
            }

            if (segmentWords.Count == 0) continue;
            phrases.AddRange(CaptionPhraseBuilder.PhrasesFromTimedWords(
                segmentWords, fits, maxWords, NoPhraseDurationFloorSeconds, maxCharacters));
        }
        return phrases;
    }

    private static TranscriptionSegment FallbackSegment(TranscriptionResult transcription)
    {
        var timed = transcription.Words
            .Where(w => w.Start is not null && w.End is not null)
            .Select(w => (Start: w.Start!.Value, End: w.End!.Value))
            .ToList();
        var start = timed.Count > 0 ? timed.Min(w => w.Start) : 0;
        var end = timed.Count > 0 ? timed.Max(w => w.End) : start;
        return new TranscriptionSegment { Text = transcription.Text, Start = start, End = end };
    }

    /// <summary>Mac CaptionBuilder.specs for one phrase → a caption text clip.</summary>
    private static Clip? BuildCaptionClip(
        Clip sourceClip,
        CaptionPhraseBuilder.Phrase phrase,
        string casedText,
        int fps,
        JsonElement styleElement,
        JsonElement? animationElement,
        string groupId,
        CaptionRequest request,
        int canvasW,
        int canvasH)
    {
        var speed = Math.Max(sourceClip.Speed, 0.0001);
        var visibleStartSource = (double)sourceClip.TrimStartFrame;
        var visibleEndSource = visibleStartSource + sourceClip.DurationFrames * speed;
        var phraseStartSource = phrase.Start * fps;
        var phraseEndSource = phrase.End * fps;
        if (phraseEndSource <= visibleStartSource || phraseStartSource >= visibleEndSource) return null;

        int ClampedTimelineFrame(double sourceSeconds)
        {
            var sourceFrame = sourceSeconds * fps;
            var frame = (int)Math.Round(
                sourceClip.StartFrame + (sourceFrame - visibleStartSource) / speed,
                MidpointRounding.AwayFromZero);
            return Math.Clamp(frame, sourceClip.StartFrame, sourceClip.EndFrame);
        }

        var start = ClampedTimelineFrame(phrase.Start);
        var end = ClampedTimelineFrame(phrase.End);
        var duration = Math.Max(1, end - start);

        var wordTimings = new List<WordTiming>();
        foreach (var w in phrase.Words)
        {
            var wordStartSource = w.Start * fps;
            var wordEndSource = w.End * fps;
            if (wordEndSource <= visibleStartSource || wordStartSource >= visibleEndSource) continue;
            var ws = ClampedTimelineFrame(w.Start);
            var we = ClampedTimelineFrame(w.End);
            var rs = Math.Min(Math.Max(0, ws - start), duration);
            var re = Math.Min(Math.Max(rs, we - start), duration);
            if (re <= rs) continue;
            wordTimings.Add(new WordTiming { Text = w.Text, StartFrame = rs, EndFrame = re });
        }

        var transform = TextClipCommands.FitTransform(
            casedText, request.Style, canvasW, canvasH, request.CenterX, request.CenterY);

        return new Clip
        {
            MediaRef = "",
            MediaType = ClipType.text,
            SourceClipType = ClipType.text,
            StartFrame = start,
            DurationFrames = duration,
            TextContent = casedText,
            TextStyle = styleElement,
            TextAnimation = animationElement,
            CaptionGroupId = groupId,
            WordTimings = wordTimings.Count > 0 ? wordTimings : null,
            Transform = transform,
        };
    }

    internal static string ApplyCase(CaptionCase textCase, string s) => textCase switch
    {
        CaptionCase.upper => s.ToUpperInvariant(),
        CaptionCase.lower => s.ToLowerInvariant(),
        _ => s,
    };

    /// <summary>
    /// Average-advance width estimate standing in for Mac TextLayout.naturalSize until a Windows
    /// text rasterizer lands (same approximation as TextClipCommands.FitTransform).
    /// </summary>
    private static double EstimateLineWidth(string line, TextStyle style, int canvasH)
    {
        var scaleY = canvasH > 0 ? canvasH / 1080.0 : 1.0;
        var fontSize = Math.Max(1, style.FontSize * (style.FontScale <= 0 ? 1 : style.FontScale) * scaleY);
        var advance = fontSize * (style.IsBold ? 0.56 : 0.52);
        return line.Length * advance;
    }
}
