namespace Temno.Core.Transcription;

/// <summary>
/// Explicit test double for tests/dev — deterministic, never touches the file system.
/// Not a production provider; real backends implement ITranscriptionProvider separately.
/// </summary>
public sealed class FakeTranscriptionProvider : ITranscriptionProvider
{
    private readonly TranscriptionResult? _result;

    public FakeTranscriptionProvider(
        TranscriptionResult? result = null,
        TranscriptionProvider provider = TranscriptionProvider.local)
    {
        _result = result;
        Provider = provider;
    }

    public TranscriptionProvider Provider { get; }

    public string? LastMediaPath { get; private set; }

    public string? LastLanguage { get; private set; }

    public int TranscribeCallCount { get; private set; }

    public Task<TranscriptionResult> TranscribeAsync(
        string mediaPath,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(mediaPath))
            throw new ArgumentException("Path cannot be null or empty.", nameof(mediaPath));

        LastMediaPath = mediaPath;
        LastLanguage = language;
        TranscribeCallCount++;

        return Task.FromResult(_result ?? MakeTranscript(
            ["the", "quick", "brown", "fox", "jumps", "over", "the", "lazy", "dog"],
            language: language ?? "en-US"));
    }

    /// <summary>Builds a deterministic transcript: evenly spaced words plus one covering segment.</summary>
    public static TranscriptionResult MakeTranscript(
        IReadOnlyList<string> words,
        double start = 0.0,
        double wordDurationSeconds = 0.4,
        double gapSeconds = 0.1,
        string? language = "en-US")
    {
        var transcriptWords = new List<TranscriptionWord>(words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            var wordStart = start + i * (wordDurationSeconds + gapSeconds);
            transcriptWords.Add(new TranscriptionWord
            {
                Text = words[i],
                Start = wordStart,
                End = wordStart + wordDurationSeconds,
            });
        }

        var text = string.Join(" ", words);
        var segments = new List<TranscriptionSegment>();
        if (transcriptWords.Count > 0)
        {
            segments.Add(new TranscriptionSegment
            {
                Text = text,
                Start = transcriptWords[0].Start!.Value,
                End = transcriptWords[^1].End!.Value,
            });
        }

        return new TranscriptionResult
        {
            Text = text,
            Language = language,
            Words = transcriptWords,
            Segments = segments,
        };
    }
}
