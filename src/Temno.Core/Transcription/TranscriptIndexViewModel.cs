using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;

namespace Temno.Core.Transcription;

/// <summary>Whether the Index surface can show a cached transcript without transcribing.</summary>
public enum TranscriptIndexAvailability
{
    NoSpeechMedia,
    UncachedSpeech,
    Cached,
}

/// <summary>A sentence-like transcript row for the editor Index surface.</summary>
public sealed record TranscriptIndexRow(
    int FirstWordIndex,
    int LastWordIndex,
    string Text,
    int StartFrame,
    int EndFrame,
    string? Speaker,
    IReadOnlyList<TimelineWord> Words);

/// <summary>
/// WinUI-free state and edit planning for the Transcript Index tab. The surface supplies cached
/// transcript data through <see cref="LoadAsync"/> and invokes the existing remove_words command
/// for the actual mutation; this class owns seek, active-word, search, and plan-preview math.
/// </summary>
public sealed class TranscriptIndexViewModel
{
    private readonly TimelineTranscript _transcript;
    private readonly IReadOnlyList<TranscriptIndexRow> _rows;
    private IReadOnlyList<TranscriptIndexRow> _visibleRows;
    private string _searchQuery = "";
    private int _searchCursor = -1;

    public TranscriptIndexViewModel(TimelineTranscript transcript, int fps)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        if (fps <= 0)
            throw new ArgumentOutOfRangeException(nameof(fps), "Frame rate must be positive.");

        _transcript = transcript;
        Fps = fps;
        _rows = BuildRows(transcript, fps);
        _visibleRows = _rows;
    }

    public int Fps { get; }

    public IReadOnlyList<TimelineWord> Words => _transcript.Words;

    public IReadOnlyList<TranscriptIndexRow> Rows => _rows;

    public IReadOnlyList<TranscriptIndexRow> VisibleRows => _visibleRows;

    public string SearchQuery => _searchQuery;

    public bool IsEmpty => Words.Count == 0;

    /// <summary>
    /// Builds from the same cache-first timeline transcript path used by get_transcript.
    /// </summary>
    public static async Task<TranscriptIndexViewModel> LoadAsync(
        ProjectSession session,
        ITranscriptionProvider provider,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var fps = session.ActiveTimeline.Fps;
        var transcript = await TimelineTranscriptBuilder
            .BuildAsync(session, provider, language, cancellationToken)
            .ConfigureAwait(false);
        return new TranscriptIndexViewModel(transcript, fps);
    }

    /// <summary>
    /// Loads only <see cref="TranscriptDiskCache"/> hits. Cache misses are skipped; this never
    /// starts a transcription backend — Generate on Captions fills the cache.
    /// </summary>
    public static Task<TranscriptIndexViewModel> LoadCachedAsync(
        ProjectSession session,
        CancellationToken cancellationToken = default)
        => LoadAsync(session, CacheOnlyProvider.Instance, language: null, cancellationToken);

    /// <summary>
    /// Classifies whether the active timeline has speech media and a disk-cache hit.
    /// </summary>
    public static TranscriptIndexAvailability Classify(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var hasSpeech = false;
        var hasCache = false;
        foreach (var path in SpeechMediaPaths(session))
        {
            hasSpeech = true;
            if (TranscriptDiskCache.HasCached(path))
                hasCache = true;
        }

        if (!hasSpeech)
            return TranscriptIndexAvailability.NoSpeechMedia;
        return hasCache
            ? TranscriptIndexAvailability.Cached
            : TranscriptIndexAvailability.UncachedSpeech;
    }

    /// <summary>Returns the exact project frame used when the surface seeks to a word.</summary>
    public int? SeekFrameForWord(int wordIndex)
        => Words.FirstOrDefault(w => w.Index == wordIndex)?.StartFrame;

    /// <summary>Returns the word whose real half-open timing contains the playhead.</summary>
    public TimelineWord? ActiveWordAtFrame(int playheadFrame)
        => Words.FirstOrDefault(w => playheadFrame >= w.StartFrame && playheadFrame < w.EndFrame);

    /// <summary>Filters sentence rows using ordinal, case-insensitive text matching.</summary>
    public void SetSearchQuery(string? query)
    {
        var normalized = query?.Trim() ?? "";
        if (string.Equals(normalized, _searchQuery, StringComparison.Ordinal))
            return;

        _searchQuery = normalized;
        _searchCursor = -1;
        _visibleRows = normalized.Length == 0
            ? _rows
            : _rows
                .Where(r => r.Text.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }

    /// <summary>Moves to the next filtered row, wrapping after the final result.</summary>
    public TranscriptIndexRow? MoveToNextSearchResult()
    {
        if (_searchQuery.Length == 0 || _visibleRows.Count == 0)
            return null;

        _searchCursor = (_searchCursor + 1) % _visibleRows.Count;
        return _visibleRows[_searchCursor];
    }

    /// <summary>
    /// Previews the exact balanced/tight/loose frame ranges that remove_words derives for one
    /// inclusive word-index selection on one timeline track.
    /// </summary>
    public WordCutPlan CreateCutPlan(
        int anchorWordIndex,
        int extentWordIndex,
        CutAggressiveness aggressiveness = CutAggressiveness.balanced)
    {
        var first = Math.Min(anchorWordIndex, extentWordIndex);
        var last = Math.Max(anchorWordIndex, extentWordIndex);
        var selected = Words
            .Where(w => w.Index >= first && w.Index <= last)
            .Select(w => w.Index)
            .ToHashSet();
        if (selected.Count == 0)
            throw new ArgumentOutOfRangeException(
                nameof(anchorWordIndex), "The selection contains no transcript words.");

        var selectedGroups = _transcript.Groups()
            .Where(g => g.Words.Any(w => selected.Contains(w.Index)))
            .ToList();
        var tracks = selectedGroups.Select(g => g.TrackIndex).Distinct().ToList();
        if (tracks.Count != 1)
            throw new InvalidOperationException(
                "A transcript Index cut must stay on one track; use remove_words for linked multi-track validation.");

        var keepGapFrames = WordCutPlanner.KeepGapFrames(aggressiveness, Fps);
        var ranges = new List<FrameRange>();
        foreach (var group in selectedGroups)
        {
            var planWords = group.Words
                .Select(w => new WordCutPlanner.Word(
                    w.StartFrame,
                    w.EndFrame,
                    selected.Contains(w.Index)))
                .ToList();
            ranges.AddRange(WordCutPlanner.CutRanges(
                planWords,
                group.ClipStartFrame,
                group.ClipEndFrame,
                keepGapFrames));
        }

        var merged = RippleEngine.MergeRanges(ranges);
        var splitFrames = new SortedSet<int>();
        foreach (var range in merged)
        {
            foreach (var group in selectedGroups)
            {
                if (range.Start > group.ClipStartFrame && range.Start < group.ClipEndFrame)
                    splitFrames.Add(range.Start);
                if (range.End > group.ClipStartFrame && range.End < group.ClipEndFrame)
                    splitFrames.Add(range.End);
            }
        }

        return new WordCutPlan(tracks[0], merged, splitFrames.ToList());
    }

    private static IReadOnlyList<TranscriptIndexRow> BuildRows(
        TimelineTranscript transcript,
        int fps)
    {
        var rows = new List<TranscriptIndexRow>();
        foreach (var group in transcript.Groups())
        {
            var run = new List<TimelineWord>();
            void Flush()
            {
                if (run.Count == 0)
                    return;

                var words = run.ToArray();
                rows.Add(new TranscriptIndexRow(
                    words[0].Index,
                    words[^1].Index,
                    string.Join(" ", words.Select(w => w.Text)),
                    words[0].StartFrame,
                    words[^1].EndFrame,
                    words[0].Speaker,
                    words));
                run.Clear();
            }

            foreach (var word in group.Words)
            {
                if (run.Count > 0)
                {
                    var previous = run[^1];
                    if (previous.Speaker != word.Speaker
                        || word.StartFrame - previous.EndFrame > fps
                        || run.Count >= 48)
                        Flush();
                }

                run.Add(word);
                if (word.Text.EndsWith(".", StringComparison.Ordinal)
                    || word.Text.EndsWith("!", StringComparison.Ordinal)
                    || word.Text.EndsWith("?", StringComparison.Ordinal))
                    Flush();
            }
            Flush();
        }

        return rows;
    }

    private static IEnumerable<string> SpeechMediaPaths(ProjectSession session)
    {
        var timeline = session.ActiveTimeline;
        var entriesById = session.Manifest.Entries
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var track in timeline.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip.MediaType is not (ClipType.video or ClipType.audio))
                    continue;
                if (!entriesById.TryGetValue(clip.MediaRef, out var entry))
                    continue;
                if (entry.HasAudio == false)
                    continue;
                var path = session.Resolver.ResolvePath(entry);
                if (path is null || !File.Exists(path))
                    continue;
                yield return Path.GetFullPath(path);
            }
        }
    }

    /// <summary>
    /// Throws on every call so <see cref="TimelineTranscriptBuilder"/> can only return cache hits.
    /// </summary>
    private sealed class CacheOnlyProvider : ITranscriptionProvider
    {
        public static readonly CacheOnlyProvider Instance = new();

        public TranscriptionProvider Provider => TranscriptionProvider.local;

        public Task<TranscriptionResult> TranscribeAsync(
            string mediaPath,
            string? language = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "Index reads the transcript cache only. Generate from the Captions tab.");
    }
}
