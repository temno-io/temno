using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-115: inspect_media transcribes spoken media through the same provider and disk cache as
/// get_transcript, and reads cues from subtitle assets. Before this package it answered with
/// "unsupported: transcription is not wired on Windows yet."
/// </summary>
[Collection("EnvRegistry")]
public sealed class CaptionInspectMediaTests : IDisposable
{
    private const string StaleUnsupportedCopy = "transcription is not wired on Windows yet";

    private readonly string _dir;
    private readonly string _audio;

    public CaptionInspectMediaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "temno-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        TranscriptDiskCache.SetRootForTests(Path.Combine(_dir, "cache"));
        _audio = Path.Combine(_dir, "voice.wav");
        File.WriteAllText(_audio, "not really audio");
    }

    public void Dispose()
    {
        ToolExecutor.TranscriptionProviderOverride = null;
        ProjectHub.ActiveSession = null;
        TranscriptDiskCache.SetRootForTests(null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ProjectSession SessionWithAudio(bool hasAudio = true, params Clip[] clips)
    {
        var session = ProjectSession.CreateNew(fps: 30, width: 1920, height: 1080);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "A1",
            Name = "voice.wav",
            Type = ClipType.audio,
            Source = MediaSource.External(_audio),
            Duration = 10,
            HasAudio = hasAudio,
        });
        foreach (var clip in clips)
            session.ActiveTimeline.Tracks[0].Clips.Add(clip);
        ProjectHub.ActiveSession = session;
        return session;
    }

    private static JsonElement Meta(ToolCallResult result)
    {
        Assert.False(result.IsError, result.Content[0].Text);
        return JsonDocument.Parse(result.Content[0].Text!).RootElement;
    }

    [Fact]
    public void ReturnsSegmentsForSpokenMedia()
    {
        SessionWithAudio();
        ToolExecutor.TranscriptionProviderOverride = new FakeTranscriptionProvider(
            FakeTranscriptionProvider.MakeTranscript(["hello", "brave", "new", "world"]));

        var result = ToolExecutor.Execute("inspect_media", new JsonObject { ["mediaRef"] = "A1" });
        var meta = Meta(result);

        Assert.DoesNotContain(StaleUnsupportedCopy, result.Content[0].Text, StringComparison.Ordinal);
        var transcription = meta.GetProperty("transcription");
        Assert.Equal("source seconds", transcription.GetProperty("timing").GetString());
        var segments = transcription.GetProperty("segments");
        Assert.True(segments.GetArrayLength() > 0);
        // Each row is [text, start, end].
        var row = segments[0];
        Assert.Equal(3, row.GetArrayLength());
        Assert.Contains("hello", row[0].GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.False(transcription.TryGetProperty("words", out _));
    }

    [Fact]
    public void WordTimestampsAddWordRows()
    {
        SessionWithAudio();
        ToolExecutor.TranscriptionProviderOverride = new FakeTranscriptionProvider(
            FakeTranscriptionProvider.MakeTranscript(["hello", "brave", "new", "world"]));

        var result = ToolExecutor.Execute("inspect_media", new JsonObject
        {
            ["mediaRef"] = "A1",
            ["wordTimestamps"] = true,
        });
        var meta = Meta(result);

        Assert.DoesNotContain(StaleUnsupportedCopy, result.Content[0].Text, StringComparison.Ordinal);
        var words = meta.GetProperty("transcription").GetProperty("words");
        Assert.Equal(4, words.GetArrayLength());
        Assert.Equal("hello", words[0][0].GetString());
    }

    [Fact]
    public void SecondCallIsServedFromTheSharedTranscriptCache()
    {
        SessionWithAudio();
        var counting = new CountingTranscriptionProvider(
            FakeTranscriptionProvider.MakeTranscript(["cached", "words"]));
        ToolExecutor.TranscriptionProviderOverride = counting;

        _ = Meta(ToolExecutor.Execute("inspect_media", new JsonObject { ["mediaRef"] = "A1" }));
        Assert.Equal(1, counting.Calls);

        var again = Meta(ToolExecutor.Execute("inspect_media", new JsonObject { ["mediaRef"] = "A1" }));
        Assert.Equal(1, counting.Calls);
        Assert.Equal("cache", again.GetProperty("transcription").GetProperty("source").GetString());
    }

    [Fact]
    public void ClipScopedRequestsReportProjectFrames()
    {
        SessionWithAudio(hasAudio: true, new Clip
        {
            Id = "c1",
            MediaRef = "A1",
            MediaType = ClipType.audio,
            StartFrame = 300,
            DurationFrames = 300,
            TrimStartFrame = 0,
        });
        ToolExecutor.TranscriptionProviderOverride = new FakeTranscriptionProvider(
            FakeTranscriptionProvider.MakeTranscript(["one", "two"]));

        var meta = Meta(ToolExecutor.Execute("inspect_media", new JsonObject
        {
            ["mediaRef"] = "A1",
            ["clipId"] = "c1",
        }));

        var transcription = meta.GetProperty("transcription");
        Assert.Equal("project frames", transcription.GetProperty("timing").GetString());
        var start = transcription.GetProperty("segments")[0][1].GetInt32();
        Assert.InRange(start, 300, 600);
    }

    [Fact]
    public void SilentMediaSaysSoInsteadOfTranscribing()
    {
        SessionWithAudio(hasAudio: false);
        ToolExecutor.TranscriptionProviderOverride = new BlockedTranscriptionProvider();

        var meta = Meta(ToolExecutor.Execute("inspect_media", new JsonObject { ["mediaRef"] = "A1" }));

        Assert.Contains(
            "no audio",
            meta.GetProperty("transcription").GetProperty("error").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranscriptionFailureIsReportedWithoutFailingTheTool()
    {
        SessionWithAudio();
        ToolExecutor.TranscriptionProviderOverride = new BlockedTranscriptionProvider();

        var result = ToolExecutor.Execute("inspect_media", new JsonObject { ["mediaRef"] = "A1" });
        var meta = Meta(result);

        Assert.True(meta.GetProperty("transcription").TryGetProperty("error", out _));
        Assert.DoesNotContain(StaleUnsupportedCopy, result.Content[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SubtitleAssetsReturnTheirCues()
    {
        var srt = Path.Combine(_dir, "cues.srt");
        File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:01,000\nFirst cue\n\n");

        var session = ProjectSession.CreateNew(fps: 30, width: 1920, height: 1080);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "S1", Name = "cues.srt", Type = ClipType.subtitle,
            Source = MediaSource.External(srt), Duration = 1,
        });
        ProjectHub.ActiveSession = session;

        var meta = Meta(ToolExecutor.Execute("inspect_media", new JsonObject { ["mediaRef"] = "S1" }));

        Assert.Equal("subtitle", meta.GetProperty("type").GetString());
        var transcription = meta.GetProperty("transcription");
        Assert.Equal("subtitle file", transcription.GetProperty("source").GetString());
        Assert.Equal("First cue", transcription.GetProperty("cues")[0][0].GetString());
    }

    // ---------------------------------------------------------------- get_transcript trackIndex

    [Fact]
    public void GetTranscriptScopesToOneTrack()
    {
        var second = Path.Combine(_dir, "other.wav");
        File.WriteAllText(second, "also not audio");

        var session = ProjectSession.CreateNew(fps: 30, width: 1920, height: 1080);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "A1", Name = "voice.wav", Type = ClipType.audio,
            Source = MediaSource.External(_audio), Duration = 10, HasAudio = true,
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "A2", Name = "other.wav", Type = ClipType.audio,
            Source = MediaSource.External(second), Duration = 10, HasAudio = true,
        });
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            Id = "c0", MediaRef = "A1", MediaType = ClipType.audio, StartFrame = 0, DurationFrames = 120,
        });
        session.ActiveTimeline.Tracks[1].Clips.Add(new Clip
        {
            Id = "c1", MediaRef = "A2", MediaType = ClipType.audio, StartFrame = 0, DurationFrames = 120,
        });
        ProjectHub.ActiveSession = session;
        ToolExecutor.TranscriptionProviderOverride = new RoutingTranscriptionProvider(
            new Dictionary<string, TranscriptionResult>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(_audio)] = FakeTranscriptionProvider.MakeTranscript(["alpha", "alpha"]),
                [Path.GetFullPath(second)] = FakeTranscriptionProvider.MakeTranscript(["bravo", "bravo"]),
            });

        var all = Meta(ToolExecutor.Execute("get_transcript", new JsonObject()));
        Assert.Equal(2, all.GetProperty("clips").GetArrayLength());

        var scoped = Meta(ToolExecutor.Execute("get_transcript", new JsonObject { ["trackIndex"] = 1 }));
        var clips = scoped.GetProperty("clips");
        Assert.Equal(1, clips.GetArrayLength());
        Assert.Equal(1, clips[0].GetProperty("trackIndex").GetInt32());
        Assert.Equal("c1", clips[0].GetProperty("clipId").GetString());
    }

    [Fact]
    public void GetTranscriptRejectsAnOutOfRangeTrackIndex()
    {
        SessionWithAudio();
        ToolExecutor.TranscriptionProviderOverride = new FakeTranscriptionProvider();

        var result = ToolExecutor.Execute("get_transcript", new JsonObject { ["trackIndex"] = 99 });

        Assert.True(result.IsError);
        Assert.Contains("trackIndex", result.Content[0].Text, StringComparison.Ordinal);
    }

    private sealed class CountingTranscriptionProvider(TranscriptionResult result) : ITranscriptionProvider
    {
        public int Calls { get; private set; }

        public TranscriptionProvider Provider => TranscriptionProvider.local;

        public Task<TranscriptionResult> TranscribeAsync(
            string mediaPath, string? language = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class BlockedTranscriptionProvider : ITranscriptionProvider
    {
        public TranscriptionProvider Provider => TranscriptionProvider.local;

        public Task<TranscriptionResult> TranscribeAsync(
            string mediaPath, string? language = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("provider must not be called");
    }

    private sealed class RoutingTranscriptionProvider(IReadOnlyDictionary<string, TranscriptionResult> byPath)
        : ITranscriptionProvider
    {
        public TranscriptionProvider Provider => TranscriptionProvider.local;

        public Task<TranscriptionResult> TranscribeAsync(
            string mediaPath, string? language = null, CancellationToken cancellationToken = default)
            => Task.FromResult(byPath[Path.GetFullPath(mediaPath)]);
    }
}
