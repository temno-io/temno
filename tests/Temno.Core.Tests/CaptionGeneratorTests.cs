using Temno.Core.Captions;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class CaptionGeneratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _mediaA;
    private readonly string _mediaB;

    public CaptionGeneratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "temno-caption-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        TranscriptDiskCache.SetRootForTests(Path.Combine(_tempDir, "cache"));
        _mediaA = Path.Combine(_tempDir, "a.mp4");
        _mediaB = Path.Combine(_tempDir, "b.mp4");
        File.WriteAllText(_mediaA, "a");
        File.WriteAllText(_mediaB, "b");
    }

    public void Dispose()
    {
        TranscriptDiskCache.SetRootForTests(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void PhraseBuilder_SplitsAtSentencesAndCapsWords()
    {
        var segment = new TranscriptionSegment { Text = "Hello world. This is a much longer sentence here", Start = 0, End = 8 };
        var phrases = CaptionPhraseBuilder.Phrases(segment, [], fits: s => s.Length <= 30, maxWords: null, minDuration: 0);

        Assert.True(phrases.Count >= 2);
        Assert.Equal("Hello world.", phrases[0].Text);
        Assert.All(phrases, p => Assert.True(p.Text.Length <= 30 || !p.Text.Contains(' ')));

        // Word cap forces further splitting even when the line visually fits.
        var capped = CaptionPhraseBuilder.Phrases(segment, [], fits: _ => true, maxWords: 2, minDuration: 0);
        Assert.All(capped, p => Assert.True(
            p.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2, p.Text));
    }

    [Fact]
    public void PhraseBuilder_TimesFromWordsAndEnforcesMinDuration()
    {
        var words = FakeTranscriptionProvider.MakeTranscript(
            ["one", "two.", "three"], wordDurationSeconds: 0.2, gapSeconds: 0.0).Words;
        var segment = new TranscriptionSegment { Text = "one two. three", Start = 0, End = 0.6 };

        var phrases = CaptionPhraseBuilder.Phrases(segment, words, fits: s => s.Length <= 8, maxWords: null, minDuration: 0.7);
        Assert.Equal(2, phrases.Count);
        Assert.Equal("one two.", phrases[0].Text);
        Assert.Equal(0.0, phrases[0].Start, 3);
        // Min duration extends the first phrase only up to the next phrase's first word.
        Assert.Equal(phrases[1].Start, phrases[0].End, 3);
        // Last phrase gets the full floor duration.
        Assert.Equal(phrases[1].Start + 0.7, phrases[1].End, 3);
        // Word spans preserved for karaoke timing.
        Assert.Equal(2, phrases[0].Words.Count);
    }

    [Fact]
    public async Task Generate_CreatesGroupedCasedCaptionsOnNewTopTrack_WithClipRelativeWordTimings()
    {
        // 4 words over 2s at the head of the clip's visible span.
        var provider = new FakeTranscriptionProvider(FakeTranscriptionProvider.MakeTranscript(
            ["hello", "brave", "new", "world"], wordDurationSeconds: 0.4, gapSeconds: 0.1));
        var session = SessionWith(
            new Clip { Id = "src", MediaRef = "M1", MediaType = ClipType.video, StartFrame = 30, DurationFrames = 60, TrimStartFrame = 0 });
        var tracksBefore = session.ActiveTimeline.Tracks.Count;

        var result = await CaptionGenerator.GenerateAsync(session, provider, new CaptionRequest
        {
            TextCase = CaptionCase.upper,
            Animation = new TextAnimation { Preset = "wordPop" },
        });

        Assert.NotEmpty(result.CreatedClipIds);
        Assert.Equal(tracksBefore + 1, session.ActiveTimeline.Tracks.Count);

        var captionTrack = session.ActiveTimeline.Tracks[0];
        Assert.Equal(result.CreatedClipIds.Count, captionTrack.Clips.Count);
        var first = captionTrack.Clips[0];
        Assert.Equal(ClipType.text, first.MediaType);
        Assert.Equal("HELLO BRAVE NEW WORLD", first.TextContent);
        // Source 0s maps to the clip's placement at frame 30.
        Assert.Equal(30, first.StartFrame);
        Assert.NotNull(first.CaptionGroupId);
        Assert.All(captionTrack.Clips, c => Assert.Equal(first.CaptionGroupId, c.CaptionGroupId));
        Assert.Equal("wordPop", first.TextAnimation!.Value.GetProperty("preset").GetString());
        // Caption style default (Mac AppTheme.Caption.defaultFontSize).
        Assert.Equal(48, first.TextStyle!.Value.GetProperty("fontSize").GetDouble());
        // Clip-relative karaoke timings: first word starts at 0 within the caption clip.
        Assert.NotNull(first.WordTimings);
        Assert.Equal(0, first.WordTimings![0].StartFrame);
        Assert.Equal("hello", first.WordTimings[0].Text);

        // Undo removes the caption track in one step.
        session.Undo();
        Assert.Equal(tracksBefore, session.ActiveTimeline.Tracks.Count);
    }

    [Fact]
    public async Task Generate_PicksDominantSpeechTrack_AndSkipsSilentMedia()
    {
        // Track 0 clip: media A with 2 words; track 1 clip: media B with 6 words → B's track wins.
        var providerResults = new Dictionary<string, TranscriptionResult>(StringComparer.OrdinalIgnoreCase)
        {
            [_mediaA] = FakeTranscriptionProvider.MakeTranscript(["only", "two"]),
            [_mediaB] = FakeTranscriptionProvider.MakeTranscript(["six", "words", "spoken", "on", "this", "track"]),
        };
        var provider = new RoutingProvider(providerResults);

        var session = SessionWith(
            new Clip { Id = "a", MediaRef = "M1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 120 });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M2", Name = "b", Type = ClipType.video, HasAudio = true,
            Source = MediaSource.External(_mediaB), Duration = 10,
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M3", Name = "silent", Type = ClipType.video, HasAudio = false,
            Source = MediaSource.External(_mediaA), Duration = 10,
        });
        session.ActiveTimeline.Tracks[1].Type = ClipType.video;
        session.ActiveTimeline.Tracks[1].Clips.Add(
            new Clip { Id = "b", MediaRef = "M2", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 120 });
        session.ActiveTimeline.Tracks[1].Clips.Add(
            new Clip { Id = "mute", MediaRef = "M3", MediaType = ClipType.video, StartFrame = 120, DurationFrames = 30 });

        var result = await CaptionGenerator.GenerateAsync(session, provider, new CaptionRequest());

        Assert.NotEmpty(result.CreatedClipIds);
        // All captions come from clip "b" (dominant track), whose text is the six-word transcript.
        var texts = session.ActiveTimeline.Tracks[0].Clips.Select(c => c.TextContent!).ToList();
        Assert.Contains(texts, t => t.Contains("six"));
        Assert.DoesNotContain(texts, t => t.Contains("only"));
        Assert.Contains(result.Skipped, s => s.ClipId == "mute" && s.Reason.Contains("no audio"));
    }

    [Fact]
    public async Task Generate_ReusesDiskCache_WithoutCallingProvider()
    {
        var first = new CountingProvider(FakeTranscriptionProvider.MakeTranscript(["cached", "words"]));
        var session = SessionWith(new Clip
        {
            Id = "c1", MediaRef = "M1", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 120,
        });

        var created = await CaptionGenerator.GenerateAsync(session, first, new CaptionRequest());
        Assert.NotEmpty(created.CreatedClipIds);
        Assert.Equal(1, first.Calls);

        session.Undo();

        var blocked = new BlockedProvider();
        var again = await CaptionGenerator.GenerateAsync(session, blocked, new CaptionRequest());
        Assert.NotEmpty(again.CreatedClipIds);
        Assert.Contains(session.ActiveTimeline.Tracks[0].Clips, c => c.TextContent?.Contains("cached") == true);
    }

    [Fact]
    public async Task Generate_SkipsBlankAudioTokens()
    {
        var provider = new FakeTranscriptionProvider(FakeTranscriptionProvider.MakeTranscript(
            ["[BLANK_AUDIO]", "[BLANK_AUDIO]"]));
        var session = SessionWith(new Clip
        {
            Id = "src",
            MediaRef = "M1",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 120,
        });

        var result = await CaptionGenerator.GenerateAsync(session, provider, new CaptionRequest { MaxWords = 4 });
        Assert.Empty(result.CreatedClipIds);
        Assert.DoesNotContain(
            session.ActiveTimeline.Tracks.SelectMany(t => t.Clips),
            c => c.TextContent?.Contains("BLANK", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ------------------------------------------------------- WP-115 timing contract

    [Fact]
    public async Task Generate_AppliesNoSecondsPhraseFloor_OnlyAOneFrameMinimum()
    {
        // Four one-word captions of 0.1 s each at 30 fps = 3 frames apiece. The removed Windows
        // 0.7 s floor would have stretched the LAST caption to 21 frames (Mac uses 1 frame).
        var provider = new FakeTranscriptionProvider(FakeTranscriptionProvider.MakeTranscript(
            ["a", "b", "c", "d"], wordDurationSeconds: 0.1, gapSeconds: 0.0));
        var session = SessionWith(new Clip
        {
            Id = "src", MediaRef = "M1", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 300, TrimStartFrame = 0,
        });

        var result = await CaptionGenerator.GenerateAsync(session, provider, new CaptionRequest
        {
            MaxWords = 1,
            MaximumGapSeconds = 0,
        });

        Assert.Equal(4, result.CreatedClipIds.Count);
        var captions = session.ActiveTimeline.Tracks[0].Clips;
        Assert.All(captions, c => Assert.Equal(3, c.DurationFrames));
        Assert.Equal(3, captions[^1].DurationFrames);
    }

    [Fact]
    public async Task Generate_ClosesShortGapsWithTheMacDefault()
    {
        // 0.3 s hole between captions = 9 frames at 30 fps, inside the 0.5 s (15-frame) default.
        var transcript = FakeTranscriptionProvider.MakeTranscript(
            ["a", "b"], wordDurationSeconds: 0.1, gapSeconds: 0.3);

        var closed = SessionWith(new Clip
        {
            Id = "src", MediaRef = "M1", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 300, TrimStartFrame = 0,
        });
        await CaptionGenerator.GenerateAsync(
            closed, new FakeTranscriptionProvider(transcript), new CaptionRequest { MaxWords = 1 });
        var closedClips = closed.ActiveTimeline.Tracks[0].Clips;
        Assert.Equal(2, closedClips.Count);
        Assert.Equal(closedClips[1].StartFrame, closedClips[0].EndFrame);
        Assert.Equal(12, closedClips[0].DurationFrames);

        var open = SessionWith(new Clip
        {
            Id = "src", MediaRef = "M1", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 300, TrimStartFrame = 0,
        });
        await CaptionGenerator.GenerateAsync(
            open,
            new FakeTranscriptionProvider(transcript),
            new CaptionRequest { MaxWords = 1, MaximumGapSeconds = 0 });
        var openClips = open.ActiveTimeline.Tracks[0].Clips;
        Assert.Equal(3, openClips[0].DurationFrames);
        Assert.True(openClips[1].StartFrame > openClips[0].EndFrame, "the hole must survive with gap = 0");
    }

    [Fact]
    public void PhraseBuilder_HonoursMaxCharacters()
    {
        var segment = new TranscriptionSegment
        {
            Text = "Hello world this is a much longer sentence here",
            Start = 0,
            End = 8,
        };

        var capped = CaptionPhraseBuilder.Phrases(
            segment, [], fits: _ => true, maxWords: null, minDuration: 0, maxCharacters: 12);

        Assert.True(capped.Count > 1);
        Assert.All(capped, p => Assert.True(
            p.Text.Length <= 12 || !p.Text.Contains(' '),
            $"'{p.Text}' exceeds the 12-character cap"));

        // No cap keeps the line whole.
        var uncapped = CaptionPhraseBuilder.Phrases(
            segment, [], fits: _ => true, maxWords: null, minDuration: 0);
        Assert.Single(uncapped);
    }

    [Fact]
    public async Task Generate_HonoursMaxCharacters()
    {
        var provider = new FakeTranscriptionProvider(FakeTranscriptionProvider.MakeTranscript(
            ["alpha", "bravo", "charlie", "delta"]));
        var session = SessionWith(new Clip
        {
            Id = "src", MediaRef = "M1", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 300, TrimStartFrame = 0,
        });

        await CaptionGenerator.GenerateAsync(
            session, provider, new CaptionRequest { MaxCharacters = 14 });

        var texts = session.ActiveTimeline.Tracks[0].Clips.Select(c => c.TextContent!).ToList();
        Assert.True(texts.Count > 1, "a 14-character cap must split the line");
        Assert.All(texts, t => Assert.True(t.Length <= 14 || !t.Contains(' '), t));
    }

    [Fact]
    public async Task Generate_HonoursExplicitTrackIndex()
    {
        // Track 1 carries six words and would win auto-detection; trackIndex 0 must override that.
        var providerResults = new Dictionary<string, TranscriptionResult>(StringComparer.OrdinalIgnoreCase)
        {
            [_mediaA] = FakeTranscriptionProvider.MakeTranscript(["only", "two"]),
            [_mediaB] = FakeTranscriptionProvider.MakeTranscript(["six", "words", "spoken", "on", "this", "track"]),
        };
        var provider = new RoutingProvider(providerResults);

        var session = SessionWith(
            new Clip { Id = "a", MediaRef = "M1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 120 });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M2", Name = "b", Type = ClipType.video, HasAudio = true,
            Source = MediaSource.External(_mediaB), Duration = 10,
        });
        session.ActiveTimeline.Tracks[1].Type = ClipType.video;
        session.ActiveTimeline.Tracks[1].Clips.Add(
            new Clip { Id = "b", MediaRef = "M2", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 120 });

        var result = await CaptionGenerator.GenerateAsync(
            session, provider, new CaptionRequest { TrackIndex = 0 });

        Assert.NotEmpty(result.CreatedClipIds);
        var texts = session.ActiveTimeline.Tracks[0].Clips.Select(c => c.TextContent!).ToList();
        Assert.Contains(texts, t => t.Contains("only"));
        Assert.DoesNotContain(texts, t => t.Contains("six"));
    }

    [Fact]
    public async Task Generate_DoesNotHoldTheLastCaptionOverAClipThatSaidNothing()
    {
        // Clip A speaks and ends at frame 90; clip B has audio but no speech and ends at 100. Holding
        // the last caption to the end of the captioned span must stop at 90, not run over B.
        var providerResults = new Dictionary<string, TranscriptionResult>(StringComparer.OrdinalIgnoreCase)
        {
            [_mediaA] = FakeTranscriptionProvider.MakeTranscript(
                ["late"], start: 2.7, wordDurationSeconds: 0.15, gapSeconds: 0),
            [_mediaB] = new TranscriptionResult(),
        };
        var provider = new RoutingProvider(providerResults);

        var session = SessionWith(new Clip
        {
            Id = "speaks", MediaRef = "M1", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 90, TrimStartFrame = 0,
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M2", Name = "silent", Type = ClipType.video, HasAudio = true,
            Source = MediaSource.External(_mediaB), Duration = 10,
        });
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            Id = "silent", MediaRef = "M2", MediaType = ClipType.video,
            StartFrame = 90, DurationFrames = 10, TrimStartFrame = 0,
        });

        var result = await CaptionGenerator.GenerateAsync(session, provider, new CaptionRequest());

        Assert.NotEmpty(result.CreatedClipIds);
        var last = session.ActiveTimeline.Tracks[0].Clips[^1];
        Assert.Equal(90, last.EndFrame);
    }

    [Fact]
    public void ListSourceTracks_UsesTimelineLabelsAndExcludesEmptyAndLinkedVideoTracks()
    {
        // Standard 4-track timeline: [V2 (0), V1 (1), A1 (2), A2 (3)]
        var session = ProjectSession.CreateNew(fps: 30, width: 1920, height: 1080);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M1", Name = "gameplay", Type = ClipType.video, HasAudio = true,
            Source = MediaSource.External(_mediaA), Duration = 10,
        });
        session.ActiveTimeline.Tracks[1].Clips.Add(new Clip
        {
            Id = "v1", MediaRef = "M1", MediaType = ClipType.video, LinkGroupId = "lg1",
            StartFrame = 0, DurationFrames = 120,
        });
        session.ActiveTimeline.Tracks[2].Clips.Add(new Clip
        {
            Id = "a1", MediaRef = "M1", MediaType = ClipType.audio, LinkGroupId = "lg1",
            StartFrame = 0, DurationFrames = 120,
        });

        var sources = CaptionGenerator.ListSourceTracks(session);

        var only = Assert.Single(sources);
        Assert.Equal("A1", only.Label);
        Assert.Equal(2, only.TrackIndex);
        Assert.Equal(session.ActiveTimeline.Tracks[2].Id, only.TrackId);
        Assert.Equal(1, only.ClipCount);
        Assert.Equal("A1 · 1 clip", only.DisplayName);
    }

    [Fact]
    public void ListSourceTracks_IncludesUnlinkedAudioVideoAndSkipsSilentAndImageMedia()
    {
        // [V2 (0), V1 (1), A1 (2), A2 (3)]
        var session = ProjectSession.CreateNew(fps: 30, width: 1920, height: 1080);
        session.Manifest.Entries.AddRange([
            new MediaManifestEntry { Id = "M_VID", Name = "cam", Type = ClipType.video, HasAudio = true, Source = MediaSource.External(_mediaA), Duration = 10 },
            new MediaManifestEntry { Id = "M_SILENT", Name = "broll", Type = ClipType.video, HasAudio = false, Source = MediaSource.External(_mediaB), Duration = 10 },
            new MediaManifestEntry { Id = "M_IMG", Name = "logo", Type = ClipType.image, HasAudio = false, Source = MediaSource.External(_mediaA), Duration = 0 },
            new MediaManifestEntry { Id = "M_VO", Name = "vo", Type = ClipType.audio, HasAudio = true, Source = MediaSource.External(_mediaB), Duration = 10 },
        ]);
        // V2: one unlinked video with audio + one still image
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip { Id = "v2-cam", MediaRef = "M_VID", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 60 });
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip { Id = "v2-img", MediaRef = "M_IMG", MediaType = ClipType.image, StartFrame = 60, DurationFrames = 60 });
        // V1: silent video only
        session.ActiveTimeline.Tracks[1].Clips.Add(new Clip { Id = "v1-silent", MediaRef = "M_SILENT", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 60 });
        // A1: empty
        // A2: two voiceover clips
        session.ActiveTimeline.Tracks[3].Clips.Add(new Clip { Id = "a2-1", MediaRef = "M_VO", MediaType = ClipType.audio, StartFrame = 0, DurationFrames = 60 });
        session.ActiveTimeline.Tracks[3].Clips.Add(new Clip { Id = "a2-2", MediaRef = "M_VO", MediaType = ClipType.audio, StartFrame = 60, DurationFrames = 60 });

        var sources = CaptionGenerator.ListSourceTracks(session);

        Assert.Equal(2, sources.Count);
        Assert.Equal("V2 · 1 clip", sources[0].DisplayName);
        Assert.Equal(0, sources[0].TrackIndex);
        Assert.Equal("A2 · 2 clips", sources[1].DisplayName);
        Assert.Equal(3, sources[1].TrackIndex);
    }

    [Fact]
    public async Task Generate_ExplicitTrackIndex_DoesNotTranscribeOtherTracks()
    {
        var transcribed = new List<string>();
        var provider = new RecordingProvider(
            new Dictionary<string, TranscriptionResult>(StringComparer.OrdinalIgnoreCase)
            {
                [_mediaA] = FakeTranscriptionProvider.MakeTranscript(["hello", "world"]),
                [_mediaB] = FakeTranscriptionProvider.MakeTranscript(["should", "not", "run"]),
            },
            transcribed);

        var session = SessionWith(
            new Clip { Id = "a", MediaRef = "M1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 120 });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M2", Name = "b", Type = ClipType.audio, HasAudio = true,
            Source = MediaSource.External(_mediaB), Duration = 10,
        });
        session.ActiveTimeline.Tracks[2].Clips.Add(
            new Clip { Id = "b", MediaRef = "M2", MediaType = ClipType.audio, StartFrame = 0, DurationFrames = 120 });

        var result = await CaptionGenerator.GenerateAsync(
            session, provider, new CaptionRequest { TrackIndex = 2 });

        Assert.NotEmpty(result.CreatedClipIds);
        var onlyPath = Assert.Single(transcribed);
        Assert.Equal(Path.GetFullPath(_mediaB), Path.GetFullPath(onlyPath), StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RecordingProvider(
        IReadOnlyDictionary<string, TranscriptionResult> byPath,
        List<string> transcribedPaths) : ITranscriptionProvider
    {
        public TranscriptionProvider Provider => TranscriptionProvider.local;

        public Task<TranscriptionResult> TranscribeAsync(string mediaPath, string? language = null, CancellationToken cancellationToken = default)
        {
            transcribedPaths.Add(mediaPath);
            return byPath.TryGetValue(mediaPath, out var result)
                ? Task.FromResult(result)
                : throw new InvalidOperationException("no transcript for " + mediaPath);
        }
    }

    private ProjectSession SessionWith(params Clip[] track0Clips)
    {
        var session = ProjectSession.CreateNew(fps: 30, width: 1920, height: 1080);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M1", Name = "a", Type = ClipType.video, HasAudio = true,
            Source = MediaSource.External(_mediaA), Duration = 10,
        });
        foreach (var clip in track0Clips)
            session.ActiveTimeline.Tracks[0].Clips.Add(clip);
        return session;
    }

    private sealed class RoutingProvider(IReadOnlyDictionary<string, TranscriptionResult> byPath) : ITranscriptionProvider
    {
        public TranscriptionProvider Provider => TranscriptionProvider.local;

        public Task<TranscriptionResult> TranscribeAsync(string mediaPath, string? language = null, CancellationToken cancellationToken = default)
            => byPath.TryGetValue(mediaPath, out var result)
                ? Task.FromResult(result)
                : throw new InvalidOperationException("no transcript for " + mediaPath);
    }

    private sealed class CountingProvider(TranscriptionResult result) : ITranscriptionProvider
    {
        public TranscriptionProvider Provider => TranscriptionProvider.local;
        public int Calls { get; private set; }

        public Task<TranscriptionResult> TranscribeAsync(string mediaPath, string? language = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class BlockedProvider : ITranscriptionProvider
    {
        public TranscriptionProvider Provider => TranscriptionProvider.local;

        public Task<TranscriptionResult> TranscribeAsync(string mediaPath, string? language = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("should have used TranscriptDiskCache");
    }
}
