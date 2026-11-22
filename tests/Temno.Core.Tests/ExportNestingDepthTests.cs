using Temno.Core.Export;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Audio;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

public sealed class ExportNestingDepthTests
{
    [Theory]
    [InlineData(ClipType.video, -1, true)]
    [InlineData(ClipType.video, 0, true)]
    [InlineData(ClipType.video, 1, false)]
    [InlineData(ClipType.audio, -1, true)]
    [InlineData(ClipType.audio, 0, true)]
    [InlineData(ClipType.audio, 1, false)]
    public void OfflineMedia_IsReportedThroughTheLastReachableLevel(
        ClipType leafType, int offsetFromLimit, bool shouldReport)
    {
        using var project = new NestedProject(TimelineFrameRenderer.MaxSequenceNestingDepth + offsetFromLimit);
        project.Leaf.Tracks.RemoveAll(track => track.Type != leafType);
        File.Delete(project.MediaPath);

        var report = ExportMediaPreflight.Inspect(
            project.Root, project.Manifest, project.Resolver, project.Lookup);

        Assert.Equal(shouldReport, report.HasProblems);
        if (shouldReport)
        {
            var problem = Assert.Single(report.Problems);
            Assert.Equal("leaf-media", problem.Reference);
            Assert.Equal(ExportMediaState.Missing, problem.State);
        }
        else
        {
            Assert.Empty(report.Items);
        }
    }

    public static IEnumerable<object[]> NestingDepths()
    {
        for (var depth = 0; depth <= TimelineFrameRenderer.MaxSequenceNestingDepth + 2; depth++)
            yield return [depth];
    }

    [Theory]
    [MemberData(nameof(NestingDepths))]
    public async Task PreflightDecoderAndRenderer_ReachTheSameMediaAtEveryDepth(int depth)
    {
        Assert.Equal(TimelineFrameRenderer.MaxSequenceNestingDepth, ExportMediaPreflight.MaxSequenceNestingDepth);
        using var project = new NestedProject(depth);
        var source = new RecordingMediaSource();
        using var renderer = new TimelineFrameRenderer(source, project.Resolver, timelineLookup: project.Lookup);

        var report = ExportMediaPreflight.Inspect(
            project.Root, project.Manifest, project.Resolver, project.Lookup);
        using var frame = await renderer.RenderAsync(project.Root, 0, 1, 1);
        var audio = await new TimelineAudioDecoder(source).DecodeAsync(
            project.Root, project.Resolver.ResolvePath, sampleRate: 10, channelCount: 1,
            timelineLookup: project.Lookup);

        // Observe real traversal and source requests: equal constants alone miss off-by-one guards.
        var expected = depth <= TimelineFrameRenderer.MaxSequenceNestingDepth
            ? new[] { "leaf-media" }
            : Array.Empty<string>();
        var preflightRefs = report.Items.Select(item => item.Reference).ToArray();
        Assert.Equal(expected, preflightRefs);
        var preflightPaths = report.Items.Select(item => project.Resolver.ResolvePath(item.Reference)).ToArray();
        Assert.Equal(preflightPaths, source.FrameRequests);
        Assert.Equal(preflightPaths, source.AudioRequests);
        Assert.Equal(expected.Length != 0, audio.Segments.Count != 0);
        Assert.NotNull(frame);
        Assert.Equal(expected.Length != 0 ? (byte)180 : (byte)40, frame.Data[1]);
        Assert.False(report.HasProblems);
    }

    private sealed class NestedProject : IDisposable
    {
        private readonly Dictionary<string, Timeline> _timelines = new(StringComparer.Ordinal);
        public string MediaPath { get; } = Path.GetTempFileName();
        public Timeline Root { get; }
        public Timeline Leaf { get; }
        public MediaManifest Manifest { get; }
        public MediaResolver Resolver { get; }

        public NestedProject(int depth)
        {
            Manifest = new MediaManifest
            {
                Entries = [new MediaManifestEntry
                {
                    Id = "leaf-media",
                    Name = "Boundary media",
                    Source = new MediaSource.ExternalSource(MediaPath),
                }],
            };
            Resolver = new MediaResolver(() => Manifest, () => null);
            Leaf = NewTimeline("leaf",
                new Track { Type = ClipType.video, Clips = [MediaClip(ClipType.video)] },
                new Track { Type = ClipType.audio, Clips = [MediaClip(ClipType.audio)] });
            var child = Leaf;
            for (var level = depth - 1; level >= 0; level--)
            {
                child = NewTimeline($"level-{level}", new Track
                {
                    Type = ClipType.video,
                    Clips = [new Clip
                    {
                        MediaRef = child.Id,
                        MediaType = ClipType.sequence,
                        SourceClipType = ClipType.sequence,
                        DurationFrames = 10,
                    }],
                });
            }
            Root = child;
        }

        public Timeline? Lookup(string id) => _timelines.GetValueOrDefault(id);

        private Timeline NewTimeline(string id, params Track[] tracks)
        {
            var timeline = new Timeline { Id = id, Fps = 10, Width = 1, Height = 1, Tracks = [.. tracks] };
            _timelines.Add(id, timeline);
            return timeline;
        }

        private static Clip MediaClip(ClipType type) => new()
        {
            MediaRef = "leaf-media",
            MediaType = type,
            SourceClipType = type,
            DurationFrames = 10,
        };

        public void Dispose() => File.Delete(MediaPath);
    }

    private sealed class RecordingMediaSource : IFrameSource, IAudioSource
    {
        public List<string> FrameRequests { get; } = [];
        public List<string> AudioRequests { get; } = [];

        public Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
        {
            FrameRequests.Add(path);
            return Task.FromResult(new VideoFrame(1, 1, [20, 180, 40, 255], "rgba"));
        }

        public Task<AudioBuffer> ReadAudioAsync(
            string path, int sampleRate, int channelCount, double startSeconds = 0,
            double? durationSeconds = null, CancellationToken cancellationToken = default)
        {
            AudioRequests.Add(path);
            return Task.FromResult(new AudioBuffer(Enumerable.Repeat(0.25f, 10).ToArray(), sampleRate, channelCount));
        }
    }
}
