using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Xunit;

namespace Temno.Core.Tests;

// Two tests here set the static TranscriptDiskCache seam; the collection keeps them serial with
// every other seam user so a parallel Dispose cannot reset the root under a running test (WP-194).
[Collection("EnvRegistry")]
public sealed class TranscriptIndexViewModelTests
{
    [Fact]
    public void SeekAndActiveWord_UseAbsoluteWordTiming()
    {
        var vm = ViewModel(
            Word(0, "twelve", 372, 381),
            Word(1, "forty", 390, 399));

        Assert.Equal(372, vm.SeekFrameForWord(0)); // 12.40 s at 30 fps
        Assert.Equal(0, vm.ActiveWordAtFrame(372)?.Index);
        Assert.Equal(0, vm.ActiveWordAtFrame(380)?.Index);
        Assert.Null(vm.ActiveWordAtFrame(381));
        Assert.Null(vm.ActiveWordAtFrame(389));
        Assert.Equal(1, vm.ActiveWordAtFrame(390)?.Index);
    }

    [Fact]
    public void Search_FiltersSentenceRows_AndNextWraps()
    {
        var vm = ViewModel(
            Word(0, "Opening", 0, 6),
            Word(1, "line.", 7, 12),
            Word(2, "Budget", 30, 36),
            Word(3, "review.", 37, 45),
            Word(4, "Another", 60, 66),
            Word(5, "budget.", 67, 75));

        Assert.Equal(3, vm.Rows.Count);
        vm.SetSearchQuery("budget");

        Assert.Equal(2, vm.VisibleRows.Count);
        Assert.Equal(30, vm.MoveToNextSearchResult()?.StartFrame);
        Assert.Equal(60, vm.MoveToNextSearchResult()?.StartFrame);
        Assert.Equal(30, vm.MoveToNextSearchResult()?.StartFrame);

        vm.SetSearchQuery("missing");
        Assert.Empty(vm.VisibleRows);
        Assert.Null(vm.MoveToNextSearchResult());
    }

    [Fact]
    public void CutPlan_EqualsRemoveWordsPlanner_AndAppliesAsOneUndo()
    {
        var words = new[]
        {
            Word(0, "keep", 10, 20, clipEnd: 100),
            Word(1, "um", 30, 40, clipEnd: 100),
            Word(2, "keep", 50, 60, clipEnd: 100),
        };
        var vm = ViewModel(words);

        var plan = vm.CreateCutPlan(1, 1);
        var expected = WordCutPlanner.CutRanges(
            words.Select(w => new WordCutPlanner.Word(w.StartFrame, w.EndFrame, w.Index == 1)).ToList(),
            clipStart: 0,
            clipEnd: 100,
            WordCutPlanner.KeepGapFrames(CutAggressiveness.balanced, 30));

        Assert.Equal(expected, plan.Ranges);
        var range = Assert.Single(plan.Ranges);
        Assert.Equal(new FrameRange(22, 48), range);

        var session = ProjectSession.CreateNew(fps: 30);
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            Id = "clip",
            MediaRef = "media",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 100,
        });

        Assert.True(plan.ApplyTo(session).Ok);
        Assert.True(session.CanUndo());
        session.Undo();
        Assert.False(session.CanUndo());
        var restored = Assert.Single(session.ActiveTimeline.Tracks[0].Clips);
        Assert.Equal(0, restored.StartFrame);
        Assert.Equal(100, restored.DurationFrames);
    }

    [Fact]
    public async Task LoadAsync_UsesTranscriptDiskCacheWithoutRetranscribing()
    {
        var root = Path.Combine(Path.GetTempPath(), "temno-index-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var mediaPath = Path.Combine(root, "speech.mp4");
        File.WriteAllText(mediaPath, "cached transcript fixture");
        TranscriptDiskCache.SetRootForTests(Path.Combine(root, "cache"));
        try
        {
            TranscriptDiskCache.Store(mediaPath, new TranscriptionResult
            {
                Text = "cached",
                Words =
                [
                    new TranscriptionWord { Text = "cached", Start = 0.25, End = 0.5 },
                ],
            });

            var session = ProjectSession.CreateNew(fps: 30);
            session.Manifest.Entries.Add(new MediaManifestEntry
            {
                Id = "media",
                Name = "speech",
                Type = ClipType.video,
                HasAudio = true,
                Source = MediaSource.External(mediaPath),
                Duration = 2,
            });
            session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
            {
                Id = "clip",
                MediaRef = "media",
                MediaType = ClipType.video,
                StartFrame = 0,
                DurationFrames = 60,
            });
            var provider = new CountingThrowingProvider();

            var vm = await TranscriptIndexViewModel.LoadAsync(session, provider);

            Assert.Equal(0, provider.CallCount);
            Assert.Equal("cached", Assert.Single(vm.Words).Text);
            Assert.Equal(TranscriptIndexAvailability.Cached, TranscriptIndexViewModel.Classify(session));
        }
        finally
        {
            TranscriptDiskCache.SetRootForTests(null);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Classify_NoSpeechMedia_WhenTimelineHasNoAudio()
    {
        var session = ProjectSession.CreateNew(fps: 30);
        Assert.Equal(TranscriptIndexAvailability.NoSpeechMedia, TranscriptIndexViewModel.Classify(session));
    }

    [Fact]
    public async Task LoadCachedAsync_DoesNotTranscribeUncachedSpeech()
    {
        var root = Path.Combine(Path.GetTempPath(), "temno-index-uncached-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var mediaPath = Path.Combine(root, "speech.mp4");
        File.WriteAllText(mediaPath, "uncached speech fixture");
        TranscriptDiskCache.SetRootForTests(Path.Combine(root, "cache"));
        try
        {
            var session = SpeechSession(mediaPath);
            Assert.Equal(
                TranscriptIndexAvailability.UncachedSpeech,
                TranscriptIndexViewModel.Classify(session));

            var vm = await TranscriptIndexViewModel.LoadCachedAsync(session);

            Assert.Empty(vm.Words);
            Assert.True(vm.IsEmpty);
        }
        finally
        {
            TranscriptDiskCache.SetRootForTests(null);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static ProjectSession SpeechSession(string mediaPath)
    {
        var session = ProjectSession.CreateNew(fps: 30);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "media",
            Name = "speech",
            Type = ClipType.video,
            HasAudio = true,
            Source = MediaSource.External(mediaPath),
            Duration = 2,
        });
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            Id = "clip",
            MediaRef = "media",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 60,
        });
        return session;
    }

    private static TranscriptIndexViewModel ViewModel(params TimelineWord[] words)
        => new(new TimelineTranscript(words, [], "test"), fps: 30);

    private static TimelineWord Word(
        int index,
        string text,
        int start,
        int end,
        int clipEnd = 600)
        => new(
            Index: index,
            ClipId: "clip",
            TrackIndex: 0,
            ClipStartFrame: 0,
            ClipEndFrame: clipEnd,
            Text: text,
            StartFrame: start,
            EndFrame: end,
            SourceStart: start / 30.0,
            SourceEnd: end / 30.0,
            Speaker: null);

    private sealed class CountingThrowingProvider : ITranscriptionProvider
    {
        public int CallCount { get; private set; }

        public TranscriptionProvider Provider => TranscriptionProvider.local;

        public Task<TranscriptionResult> TranscribeAsync(
            string mediaPath,
            string? language = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("cache miss");
        }
    }
}
