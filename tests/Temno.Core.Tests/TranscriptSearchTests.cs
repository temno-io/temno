using Temno.Core.Transcription;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class TranscriptSearchTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "TemnoTranscriptSearch_" + Guid.NewGuid().ToString("N"));

    public TranscriptSearchTests()
    {
        Directory.CreateDirectory(_tempRoot);
        TranscriptDiskCache.SetRootForTests(Path.Combine(_tempRoot, "cache"));
    }

    public void Dispose()
    {
        TranscriptDiskCache.SetRootForTests(null);
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch { /* ignore */ }
    }

    [Fact]
    public void Terms_StripPunctuation()
    {
        var terms = TranscriptSearch.Terms("budget, cuts!");
        Assert.Equal(new[] { "budget", "cuts" }, terms);
    }

    [Fact]
    public void Search_RequiresAllTermsInSegment()
    {
        var a = FakeTranscriptionProvider.MakeTranscript(["hello", "world", "today"]);
        a.Segments =
        [
            new TranscriptionSegment { Text = "hello world today", Start = 0, End = 1.5 },
        ];
        var b = FakeTranscriptionProvider.MakeTranscript(["hello", "there"]);
        b.Segments =
        [
            new TranscriptionSegment { Text = "hello there", Start = 0, End = 1 },
        ];

        var hits = TranscriptSearch.Search("hello world",
        [
            ("a", a),
            ("b", b),
        ]);

        Assert.Single(hits);
        Assert.Equal("a", hits[0].MediaRef);
        Assert.Contains("world", hits[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiskCache_RoundTrip()
    {
        var media = Path.Combine(_tempRoot, "clip.wav");
        File.WriteAllText(media, "audio-bytes");
        var tr = FakeTranscriptionProvider.MakeTranscript(["quick", "brown", "fox"]);
        TranscriptDiskCache.Store(media, tr);

        Assert.True(TranscriptDiskCache.HasCached(media));
        Assert.True(TranscriptDiskCache.TryLoad(media, out var loaded));
        Assert.NotNull(loaded);
        Assert.Equal(tr.Text, loaded!.Text);
        Assert.Equal(tr.Segments.Count, loaded.Segments.Count);
    }

    [Fact]
    public void Search_UsesCachedTranscriptsOnly()
    {
        var media = Path.Combine(_tempRoot, "speech.wav");
        File.WriteAllText(media, "wav");
        var tr = FakeTranscriptionProvider.MakeTranscript(["meeting", "budget", "review"]);
        tr.Segments =
        [
            new TranscriptionSegment { Text = "meeting budget review", Start = 1, End = 3 },
        ];
        TranscriptDiskCache.Store(media, tr);

        Assert.True(TranscriptDiskCache.TryLoad(media, out var cached));
        var hits = TranscriptSearch.Search("budget", [("speech", cached!)]);
        Assert.Single(hits);
        Assert.Equal(1, hits[0].StartSeconds);
    }
}
