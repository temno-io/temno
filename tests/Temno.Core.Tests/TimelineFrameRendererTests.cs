using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

public sealed class TimelineFrameRendererTests
{
    [Fact]
    public async Task Renderer_AppliesEffectsBeforeCompositingAndCarriesClipRelativeFrame()
    {
        var clip = new Clip
        {
            Id = "clip",
            MediaRef = "asset",
            MediaType = ClipType.video,
            StartFrame = 10,
            DurationFrames = 30,
            Effects =
            [
                new Effect
                {
                    Type = "color.exposure",
                    Params = new() { ["ev"] = new EffectParam { Value = 1 } },
                },
            ],
        };
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 1,
            Height = 1,
            Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
        };
        var resolver = Resolver(("asset", "/media/asset.mp4"));
        var source = new StaticFrameSource(new VideoFrame(1, 1, [128, 0, 0, 255], "rgba"));
        var renderer = new TimelineFrameRenderer(source, resolver);

        var resolved = Assert.Single(TimelineFrameResolver.Resolve(timeline, 15, resolver));
        Assert.Same(clip.Effects, resolved.Effects);
        Assert.Equal(5, resolved.ClipRelativeFrame);

        using var output = await renderer.RenderAsync(timeline, 15, 1, 1);

        Assert.NotNull(output);
        Assert.Equal(176, output.Data[0]);
        Assert.Equal(0, output.Data[1]);
        Assert.Equal(0, output.Data[2]);
        Assert.Equal(255, output.Data[3]);
    }

    [Fact]
    public async Task PreviewProvider_UsesTheSharedCompositedPath()
    {
        var top = new Clip
        {
            MediaRef = "top",
            MediaType = ClipType.video,
            DurationFrames = 30,
            Effects =
            [
                new Effect
                {
                    Type = "color.saturation",
                    Params = new() { ["amount"] = new EffectParam { Value = 0 } },
                },
            ],
        };
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 1,
            Height = 1,
            Tracks = [new Track { Type = ClipType.video, Clips = [top] }],
        };
        var resolver = Resolver(("top", "/media/top.mp4"));
        var provider = new PreviewFrameProvider(
            new StaticFrameSource(new VideoFrame(1, 1, [255, 0, 0, 255], "rgba")),
            resolver);

        using var output = await provider.GetCompositedFrameAsync(timeline, 0);

        Assert.NotNull(output);
        Assert.Equal(new byte[] { 54, 54, 54, 255 }, output.Data);
    }

    [Fact]
    public async Task RendererAndPreview_PropagateClipBlendModeThroughTheSharedPath()
    {
        var top = new Clip
        {
            Id = "top",
            MediaRef = "top",
            MediaType = ClipType.video,
            DurationFrames = 30,
            BlendMode = "multiply",
        };
        var bottom = new Clip
        {
            Id = "bottom",
            MediaRef = "bottom",
            MediaType = ClipType.video,
            DurationFrames = 30,
        };
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 1,
            Height = 1,
            Tracks =
            [
                new Track { Type = ClipType.video, Clips = [top] },
                new Track { Type = ClipType.video, Clips = [bottom] },
            ],
        };
        var resolver = Resolver(("top", "/media/top.mp4"), ("bottom", "/media/bottom.mp4"));
        var source = new PathFrameSource(
            ("/media/top.mp4", [204, 102, 51, 255]),
            ("/media/bottom.mp4", [64, 128, 191, 255]));

        var resolved = TimelineFrameResolver.Resolve(timeline, 0, resolver);
        Assert.Equal("multiply", resolved.Last().BlendMode);

        var renderer = new TimelineFrameRenderer(source, resolver);
        using var rendered = await renderer.RenderAsync(timeline, 0, 1, 1);
        Assert.NotNull(rendered);
        Assert.Equal(new byte[] { 51, 51, 38, 255 }, rendered.Data);

        var preview = new PreviewFrameProvider(source, resolver);
        using var previewFrame = await preview.GetCompositedFrameAsync(timeline, 0);
        Assert.NotNull(previewFrame);
        Assert.Equal(rendered.Data, previewFrame.Data);
    }

    [Fact]
    public async Task Renderer_DecodesVisibleLayersConcurrently_KeepingOrderAndPixels()
    {
        // WP-163 (step b): the two layer decodes must overlap in time, and the composited
        // bytes must be identical to the sequential path's (bottom red, top green multiply).
        var top = new Clip
        {
            Id = "top",
            MediaRef = "top",
            MediaType = ClipType.video,
            DurationFrames = 30,
            BlendMode = "multiply",
        };
        var bottom = new Clip
        {
            Id = "bottom",
            MediaRef = "bottom",
            MediaType = ClipType.video,
            DurationFrames = 30,
        };
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 1,
            Height = 1,
            Tracks =
            [
                new Track { Type = ClipType.video, Clips = [top] },
                new Track { Type = ClipType.video, Clips = [bottom] },
            ],
        };
        var resolver = Resolver(("top", "/media/top.mp4"), ("bottom", "/media/bottom.mp4"));
        var source = new OverlappedFrameSource(new Dictionary<string, byte[]>
        {
            ["/media/top.mp4"] = [204, 102, 51, 255],
            ["/media/bottom.mp4"] = [64, 128, 191, 255],
        });

        var renderer = new TimelineFrameRenderer(source, resolver);
        using var rendered = await renderer.RenderAsync(timeline, 0, 1, 1);

        Assert.NotNull(rendered);
        Assert.Equal(new byte[] { 51, 51, 38, 255 }, rendered.Data);
        Assert.Equal(2, source.MaxInFlight);
    }

    [Fact]
    public async Task Renderer_FadeInAtFirstFrame_IsOpaqueBlack_AndAGapStaysAbsent()
    {
        var clip = new Clip
        {
            MediaRef = "asset",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 90,
            FadeInFrames = 30,
            Opacity = 1,
        };
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 1,
            Height = 1,
            Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
        };
        var resolver = Resolver(("asset", "/media/asset.mp4"));
        var source = new StaticFrameSource(new VideoFrame(1, 1, [255, 0, 0, 255], "rgba"));
        var renderer = new TimelineFrameRenderer(source, resolver);

        Assert.Empty(TimelineFrameResolver.Resolve(timeline, 0, resolver));

        using var faded = await renderer.RenderAsync(timeline, 0, 1, 1);
        Assert.NotNull(faded);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, faded.Data);

        using var rising = await renderer.RenderAsync(timeline, 15, 1, 1);
        Assert.NotNull(rising);
        Assert.True(rising.Data[0] > 0);
        Assert.Equal(255, rising.Data[3]);

        using var gap = await renderer.RenderAsync(timeline, 200, 1, 1);
        Assert.Null(gap);
    }

    [Fact]
    public async Task Renderer_UnresolvedOpaqueClip_StaysAbsent()
    {
        var clip = new Clip
        {
            MediaRef = "missing",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 30,
            Opacity = 1,
        };
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 1,
            Height = 1,
            Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
        };
        var renderer = new TimelineFrameRenderer(new StaticFrameSource(new VideoFrame(1, 1, [255, 0, 0, 255], "rgba")));

        using var output = await renderer.RenderAsync(timeline, 0, 1, 1);
        Assert.Null(output);
    }

    private static MediaResolver Resolver(params (string Id, string Path)[] entries)
    {
        var manifest = new MediaManifest
        {
            Entries = entries.Select(entry => new MediaManifestEntry
            {
                Id = entry.Id,
                Source = MediaSource.External(entry.Path),
            }).ToList(),
        };
        return new MediaResolver(() => manifest, () => null);
    }

    private sealed class StaticFrameSource(VideoFrame frame) : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
            => Task.FromResult(new VideoFrame(frame.Width, frame.Height, (byte[])frame.Data.Clone(), frame.PixelFormat));
    }

    private sealed class PathFrameSource(params (string Path, byte[] Rgba)[] frames) : IFrameSource
    {
        private readonly Dictionary<string, byte[]> _frames = frames.ToDictionary(entry => entry.Path, entry => entry.Rgba);

        public Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
        {
            var rgba = _frames[path];
            return Task.FromResult(new VideoFrame(1, 1, (byte[])rgba.Clone(), "rgba"));
        }
    }

    /// <summary>
    /// WP-163: blocks the first decode until the second arrives, so a sequential renderer
    /// deadlocks (xunit cancels it) while a fan-out renderer completes with MaxInFlight 2.
    /// </summary>
    private sealed class OverlappedFrameSource(Dictionary<string, byte[]> frames) : IFrameSource
    {
        private int _inFlight;
        private int _entered;

        public int MaxInFlight { get; private set; }

        public async Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (this)
            {
                MaxInFlight = Math.Max(MaxInFlight, now);
            }
            try
            {
                // Wait until both layers are inside before answering either.
                if (Interlocked.Increment(ref _entered) == 1)
                {
                    var spin = 0;
                    while (Volatile.Read(ref _entered) < 2 && spin++ < 10_000)
                        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    Assert.Equal(2, Volatile.Read(ref _entered));
                }
                var rgba = frames[path];
                return new VideoFrame(1, 1, (byte[])rgba.Clone(), "rgba");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
