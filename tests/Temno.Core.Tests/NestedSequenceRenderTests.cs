using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

public sealed class NestedSequenceRenderTests
{
    [Fact]
    public async Task NestedSequence_RendersChildTimelineContent()
    {
        var nested = new Timeline
        {
            Id = "T-NEST",
            Fps = 30,
            Width = 4,
            Height = 4,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            Id = "nested-clip",
                            MediaRef = "asset",
                            MediaType = ClipType.video,
                            StartFrame = 0,
                            DurationFrames = 30,
                        },
                    ],
                },
            ],
        };
        var main = new Timeline
        {
            Id = "T-MAIN",
            Fps = 30,
            Width = 4,
            Height = 4,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            Id = "seq-clip",
                            MediaRef = "T-NEST",
                            MediaType = ClipType.sequence,
                            SourceClipType = ClipType.sequence,
                            StartFrame = 0,
                            DurationFrames = 30,
                        },
                    ],
                },
            ],
        };

        var source = new StaticFrameSource(new VideoFrame(4, 4, SolidRgba(4, 4, 20, 180, 40, 255), "rgba"));
        var resolver = Resolver(("asset", "/media/asset.mp4"));

        Timeline? Lookup(string id) => id == nested.Id ? nested : null;
        var renderer = new TimelineFrameRenderer(source, resolver, timelineLookup: Lookup);

        using var frame = await renderer.RenderAsync(main, 5, 4, 4);
        Assert.NotNull(frame);
        // Nested content green channel should show through
        Assert.Equal(180, frame!.Data[1]);
    }

    [Fact]
    public async Task NestedSequence_MissingLookup_RendersEmpty()
    {
        var main = new Timeline
        {
            Id = "T-MAIN",
            Fps = 30,
            Width = 2,
            Height = 2,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            MediaRef = "missing-timeline",
                            MediaType = ClipType.sequence,
                            DurationFrames = 10,
                        },
                    ],
                },
            ],
        };
        var source = new StaticFrameSource(new VideoFrame(1, 1, [0, 0, 0, 255], "rgba"));
        var renderer = new TimelineFrameRenderer(source, mediaResolver: null, timelineLookup: _ => null);
        using var frame = await renderer.RenderAsync(main, 0, 2, 2);
        Assert.Null(frame);
    }

    [Fact]
    public async Task NestedSequence_ExceedsDepth_UsesPlaceholder()
    {
        // Self-referential nest to blow the depth budget.
        var nested = new Timeline
        {
            Id = "T-LOOP",
            Fps = 30,
            Width = 2,
            Height = 2,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            MediaRef = "T-LOOP",
                            MediaType = ClipType.sequence,
                            DurationFrames = 5,
                        },
                    ],
                },
            ],
        };
        Timeline? Lookup(string id) => id == nested.Id ? nested : null;
        var source = new StaticFrameSource(new VideoFrame(1, 1, [255, 0, 0, 255], "rgba"));
        var renderer = new TimelineFrameRenderer(source, timelineLookup: Lookup);
        using var frame = await renderer.RenderAsync(nested, 0, 2, 2);
        Assert.NotNull(frame);
        // Depth-cap placeholder is dark gray, not the red static source.
        Assert.True(frame!.Data[0] < 50);
        Assert.True(frame.Data[1] < 50);
        Assert.True(frame.Data[2] < 50);
    }

    [Fact]
    public async Task LottieWithoutFlattenedImage_UsesPlaceholder()
    {
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 8,
            Height = 8,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            MediaRef = "anim",
                            MediaType = ClipType.lottie,
                            SourceClipType = ClipType.lottie,
                            DurationFrames = 10,
                        },
                    ],
                },
            ],
        };
        var source = new StaticFrameSource(new VideoFrame(1, 1, [0, 0, 0, 255], "rgba"));
        var renderer = new TimelineFrameRenderer(source, mediaResolver: null);
        using var frame = await renderer.RenderAsync(timeline, 0, 8, 8);
        Assert.NotNull(frame);
        // Magenta-ish placeholder
        Assert.True(frame!.Data[0] > 100);
        Assert.True(frame.Data[2] > 100);
    }

    [Fact]
    public void SemiTransparentImage_AlphaPreservedThroughColorEffect()
    {
        // Compositor canvas is opaque black (Mac parity); alpha preservation is at the effect stage.
        var data = SolidRgba(2, 2, 100, 50, 25, 128);
        using var input = new VideoFrame(2, 2, data, "rgba");
        var effects = new List<Effect>
        {
            new()
            {
                Type = "color.exposure",
                Params = new() { ["ev"] = new EffectParam { Value = 0 } },
            },
        };
        var renderer = new Temno.Media.Effects.CpuEffectsRenderer();
        using var output = renderer.Apply(input, effects, clipRelativeFrame: 0);
        Assert.Equal(128, output.Data[3]);
        Assert.Equal(100, output.Data[0]);
    }

    private static byte[] SolidRgba(int w, int h, byte r, byte g, byte b, byte a)
    {
        var d = new byte[w * h * 4];
        for (var i = 0; i < d.Length; i += 4)
        {
            d[i] = r;
            d[i + 1] = g;
            d[i + 2] = b;
            d[i + 3] = a;
        }
        return d;
    }

    private static MediaResolver Resolver(params (string Id, string Path)[] entries)
    {
        var manifest = new MediaManifest
        {
            Entries = entries.Select(e => new MediaManifestEntry
            {
                Id = e.Id,
                Type = ClipType.video,
                Source = MediaSource.External(e.Path),
            }).ToList(),
        };
        return new MediaResolver(() => manifest, () => null);
    }

    private sealed class StaticFrameSource : IFrameSource
    {
        private readonly VideoFrame _frame;
        public StaticFrameSource(VideoFrame frame) => _frame = frame;
        public Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
        {
            var copy = new byte[_frame.Data.Length];
            Buffer.BlockCopy(_frame.Data, 0, copy, 0, copy.Length);
            return Task.FromResult(new VideoFrame(_frame.Width, _frame.Height, copy, _frame.PixelFormat));
        }
    }
}
