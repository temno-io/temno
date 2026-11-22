using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class TimelineTranscriptTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _mediaFile;

    public TimelineTranscriptTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "temno-transcript-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // BuildAsync stores every transcript through TranscriptDiskCache; without this seam the
        // fixture lands in the user's %LocalAppData%\Temno\transcripts (WP-194).
        TranscriptDiskCache.SetRootForTests(Path.Combine(_tempDir, "cache"));
        _mediaFile = Path.Combine(_tempDir, "speech.mp4");
        File.WriteAllText(_mediaFile, "fake media bytes");
    }

    public void Dispose()
    {
        TranscriptDiskCache.SetRootForTests(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Build_MapsWordsAcrossClips_WithGlobalIndices_AndTranscribesOncePerFile()
    {
        // 4s of speech: 8 words, 0.5s cadence (0.4s word + 0.1s gap).
        var result = FakeTranscriptionProvider.MakeTranscript(
            ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"],
            wordDurationSeconds: 0.4, gapSeconds: 0.1);
        var provider = new FakeTranscriptionProvider(result);

        var session = SessionWithClips(
            // Clip A shows source 0..2s at frames 0..60.
            new Clip { Id = "a", MediaRef = "M1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 60, TrimStartFrame = 0 },
            // Clip B shows source 2..4s at frames 60..120.
            new Clip { Id = "b", MediaRef = "M1", MediaType = ClipType.video, StartFrame = 60, DurationFrames = 60, TrimStartFrame = 60 });

        var transcript = await TimelineTranscriptBuilder.BuildAsync(session, provider);

        Assert.Equal(1, provider.TranscribeCallCount); // one file, one transcription
        Assert.Empty(transcript.Skipped);
        Assert.Equal(8, transcript.Words.Count);
        Assert.Equal(Enumerable.Range(0, 8), transcript.Words.Select(w => w.Index));

        // Words 0-3 (source 0..2s) belong to clip A; 4-7 to clip B.
        Assert.All(transcript.Words.Take(4), w => Assert.Equal("a", w.ClipId));
        Assert.All(transcript.Words.Skip(4), w => Assert.Equal("b", w.ClipId));

        // First word: source 0.0-0.4s → frames 0-12 on clip A.
        Assert.Equal(0, transcript.Words[0].StartFrame);
        Assert.Equal(12, transcript.Words[0].EndFrame);
        // Fifth word: source 2.0-2.4s → clip B starts at frame 60, trim 2s → frames 60-72.
        Assert.Equal("echo", transcript.Words[4].Text);
        Assert.Equal(60, transcript.Words[4].StartFrame);
        Assert.Equal(72, transcript.Words[4].EndFrame);

        var groups = transcript.Groups();
        Assert.Equal(2, groups.Count);
        Assert.Equal(["a", "b"], groups.Select(g => g.ClipId));
        Assert.Single(transcript.Groups("b"));
    }

    [Fact]
    public async Task Build_SkipsOfflineNoAudioMissingEntryAndFailures_Honestly()
    {
        var provider = new FakeTranscriptionProvider();
        var session = SessionWithClips(
            new Clip { Id = "ok", MediaRef = "M1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 30 },
            new Clip { Id = "silent", MediaRef = "M2", MediaType = ClipType.video, StartFrame = 30, DurationFrames = 30 },
            new Clip { Id = "offline", MediaRef = "M3", MediaType = ClipType.video, StartFrame = 60, DurationFrames = 30 },
            new Clip { Id = "orphan", MediaRef = "M4", MediaType = ClipType.video, StartFrame = 90, DurationFrames = 30 },
            new Clip { Id = "img", MediaRef = "M1", MediaType = ClipType.image, StartFrame = 120, DurationFrames = 30 });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M2", Name = "silent", Type = ClipType.video, HasAudio = false,
            Source = MediaSource.External(_mediaFile), Duration = 10,
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M3", Name = "gone", Type = ClipType.video, HasAudio = true,
            Source = MediaSource.External(Path.Combine(_tempDir, "missing.mp4")), Duration = 10,
        });

        var transcript = await TimelineTranscriptBuilder.BuildAsync(session, provider);

        Assert.All(transcript.Words, w => Assert.Equal("ok", w.ClipId));
        Assert.Equal(3, transcript.Skipped.Count); // silent, offline, orphan — image clips are not speech targets
        Assert.Contains(transcript.Skipped, s => s.ClipId == "silent" && s.Reason.Contains("no audio"));
        Assert.Contains(transcript.Skipped, s => s.ClipId == "offline" && s.Reason.Contains("offline"));
        Assert.Contains(transcript.Skipped, s => s.ClipId == "orphan" && s.Reason.Contains("not found"));
    }

    [Fact]
    public async Task Build_ReportsProviderFailuresPerClip()
    {
        var provider = new ThrowingProvider();
        var session = SessionWithClips(
            new Clip { Id = "a", MediaRef = "M1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 30 },
            new Clip { Id = "b", MediaRef = "M1", MediaType = ClipType.video, StartFrame = 30, DurationFrames = 30 });

        var transcript = await TimelineTranscriptBuilder.BuildAsync(session, provider);

        Assert.Empty(transcript.Words);
        Assert.Equal(2, transcript.Skipped.Count);
        Assert.All(transcript.Skipped, s => Assert.Contains("backend unavailable", s.Reason));
    }

    private ProjectSession SessionWithClips(params Clip[] clips)
    {
        var session = ProjectSession.CreateNew(fps: 30);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M1", Name = "speech", Type = ClipType.video, HasAudio = true,
            Source = MediaSource.External(_mediaFile), Duration = 10,
        });
        foreach (var clip in clips)
            session.ActiveTimeline.Tracks[0].Clips.Add(clip);
        return session;
    }

    private sealed class ThrowingProvider : ITranscriptionProvider
    {
        public TranscriptionProvider Provider => TranscriptionProvider.cloud;

        public Task<TranscriptionResult> TranscribeAsync(string mediaPath, string? language = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("backend unavailable");
    }
}
