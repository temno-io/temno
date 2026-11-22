using Temno.Core.Models;
using Temno.Core.Session;

namespace Temno.Core.Transcription;

/// <summary>One transcript word placed on the timeline. Mac: TimelineWord (ToolExecutor+Transcription.swift).</summary>
public sealed record TimelineWord(
    int Index,
    string ClipId,
    int TrackIndex,
    int ClipStartFrame,
    int ClipEndFrame,
    string Text,
    int StartFrame,
    int EndFrame,
    double SourceStart,
    double SourceEnd,
    string? Speaker);

/// <summary>A clip that could not contribute transcript words, with the honest reason.</summary>
public sealed record TranscriptSkip(string ClipId, string MediaRef, string Reason);

/// <summary>A contiguous run of words belonging to one clip, in global word order.</summary>
public sealed record TimelineTranscriptGroup(
    string ClipId,
    int TrackIndex,
    int ClipStartFrame,
    int ClipEndFrame,
    IReadOnlyList<TimelineWord> Words);

/// <summary>
/// The spoken transcript of a timeline in project frames.
/// Mac: TimelineTranscript (ToolExecutor+Transcription.swift).
/// </summary>
public sealed class TimelineTranscript
{
    public TimelineTranscript(IReadOnlyList<TimelineWord> words, IReadOnlyList<TranscriptSkip> skipped, string source)
    {
        Words = words;
        Skipped = skipped;
        Source = source;
    }

    public IReadOnlyList<TimelineWord> Words { get; }

    public IReadOnlyList<TranscriptSkip> Skipped { get; }

    /// <summary>Provider name, e.g. "cloud" — surfaced as transcriptionSource.</summary>
    public string Source { get; }

    public bool IncludesSpeakers => Words.Any(w => w.Speaker != null);

    /// <summary>Contiguous per-clip runs in word order, optionally filtered to one clip.</summary>
    public List<TimelineTranscriptGroup> Groups(string? clipIdFilter = null)
    {
        var groups = new List<TimelineTranscriptGroup>();
        var i = 0;
        while (i < Words.Count)
        {
            var clipId = Words[i].ClipId;
            var j = i + 1;
            while (j < Words.Count && Words[j].ClipId == clipId) j++;
            if (clipIdFilter is null || clipIdFilter == clipId)
            {
                var slice = new List<TimelineWord>(j - i);
                for (var k = i; k < j; k++) slice.Add(Words[k]);
                groups.Add(new TimelineTranscriptGroup(
                    clipId, Words[i].TrackIndex, Words[i].ClipStartFrame, Words[i].ClipEndFrame, slice));
            }
            i = j;
        }
        return groups;
    }
}

/// <summary>
/// Walks the active timeline's speech-capable clips, transcribes each unique media file once, and
/// maps the words onto project frames through each clip's placement/trim/speed.
/// Mac: ToolExecutor.timelineWords — minus speaker identity alignment, which needs the voice
/// fingerprint models (see IMPLEMENTATION_PLAN, WP-95-era work).
/// </summary>
public static class TimelineTranscriptBuilder
{
    public static async Task<TimelineTranscript> BuildAsync(
        ProjectSession session,
        ITranscriptionProvider provider,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(provider);

        var timeline = session.ActiveTimeline;
        var entriesById = session.Manifest.Entries
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var skipped = new List<TranscriptSkip>();
        var fragments = new List<(Clip Clip, int TrackIndex, string Path)>();
        var transfer = session.Resolver.ForTransfer();
        for (var trackIndex = 0; trackIndex < timeline.Tracks.Count; trackIndex++)
        {
            foreach (var clip in timeline.Tracks[trackIndex].Clips)
            {
                if (clip.MediaType is not (ClipType.video or ClipType.audio)) continue;
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
                // WP-399: the transcript goes back to the caller (an agent's model, a hosted provider), so an
                // external file must be one the user allowed and must still lead where it did then.
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
                fragments.Add((clip, trackIndex, Path.GetFullPath(path)));
            }
        }

        // Transcribe each unique file once; a failure skips every clip using that file.
        var transcripts = new Dictionary<string, TranscriptionResult>(StringComparer.OrdinalIgnoreCase);
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in fragments.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Prefer disk cache (WP-95 spoken search + avoid re-transcribe).
                if (TranscriptDiskCache.TryLoad(path, out var cached) && cached is not null)
                {
                    transcripts[path] = cached;
                    continue;
                }

                var result = await provider
                    .TranscribeAsync(path, language, cancellationToken)
                    .ConfigureAwait(false);
                transcripts[path] = result;
                try { TranscriptDiskCache.Store(path, result); }
                catch { /* cache write is best-effort */ }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures[path] = ex.Message;
            }
        }

        var words = new List<TimelineWord>();
        foreach (var frag in fragments.OrderBy(f => f.Clip.StartFrame).ThenBy(f => f.TrackIndex))
        {
            if (failures.TryGetValue(frag.Path, out var reason))
            {
                skipped.Add(new TranscriptSkip(frag.Clip.Id, frag.Clip.MediaRef, $"transcription failed: {reason}"));
                continue;
            }
            if (!transcripts.TryGetValue(frag.Path, out var transcript)) continue;

            foreach (var mapped in WordCutPlanner.MapToTimeline(frag.Clip, timeline.Fps, transcript.Words))
            {
                words.Add(new TimelineWord(
                    words.Count,
                    frag.Clip.Id,
                    frag.TrackIndex,
                    frag.Clip.StartFrame,
                    frag.Clip.EndFrame,
                    mapped.Text,
                    mapped.StartFrame,
                    mapped.EndFrame,
                    mapped.SourceStart,
                    mapped.SourceEnd,
                    mapped.Speaker));
            }
        }

        return new TimelineTranscript(words, skipped, provider.Provider.ToString());
    }
}
