using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Hardware;
using Temno.Media.Playback;
using Xunit;
using Xunit.Abstractions;

namespace Temno.Core.Tests;

/// <summary>
/// WP-405, drawn: a 1152x2048 still in a full-canvas box on 1920x1080 covers 607.5 px of width at
/// full height, centred, on both compositors; and clip crop draws only the visible region, where it
/// sat in the whole picture (Mac <c>FrameRenderer.applyClipPipeline</c>).
/// </summary>
public sealed class ClipShapeRenderTests(ITestOutputHelper output)
{
    private static VideoFrame Solid(int w, int h, byte r, byte g, byte b)
    {
        var data = new byte[w * h * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = r;
            data[i + 1] = g;
            data[i + 2] = b;
            data[i + 3] = 255;
        }
        return new VideoFrame(w, h, data, "rgba");
    }

    private static VideoFrame Gradient(int w, int h)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            data[i] = (byte)(40 + x * 3);
            data[i + 1] = (byte)(30 + y * 5);
            data[i + 2] = (byte)(200 - x);
            data[i + 3] = 255;
        }
        return new VideoFrame(w, h, data, "rgba");
    }

    private static (int First, int Last, int Count) RedColumns(VideoFrame frame, int row)
    {
        int first = -1, last = -1, count = 0;
        for (var x = 0; x < frame.Width; x++)
        {
            var i = (row * frame.Width + x) * 4;
            if (frame.Data[i] <= 127) continue;
            if (first < 0) first = x;
            last = x;
            count++;
        }
        return (first, last, count);
    }

    private static void AssertPillarboxed(VideoFrame result)
    {
        foreach (var row in new[] { 0, 540, 1079 })
        {
            var (first, last, count) = RedColumns(result, row);
            // 607.5 px wide from x = 656.25: 608 pixel centres fall inside it.
            Assert.InRange(count, 607, 609);
            Assert.InRange(first, 655, 657);
            Assert.InRange(last, 1262, 1264);
        }
        var left = (540 * 1920 + 100) * 4;
        var right = (540 * 1920 + 1800) * 4;
        Assert.Equal(0, result.Data[left]);
        Assert.Equal(0, result.Data[right]);
    }

    [Fact]
    public void Cpu_PortraitInFullCanvasBox_IsPillarboxedNotStretched()
    {
        using var picture = Solid(1152, 2048, 255, 0, 0);
        using var result = new CpuCompositor().Render([new CompositorLayer(picture)], 1920, 1080);
        AssertPillarboxed(result);
    }

    [Fact]
    public void Gpu_PortraitInFullCanvasBox_IsPillarboxedNotStretched()
    {
        var caps = HardwareCapabilities.Probe();
        using var gpu = new GpuCompositor(new CpuCompositor(), caps);
        if (!caps.D3D11Available || !gpu.IsGpuReady)
        {
            output.WriteLine($"no GPU path here: d3d11={caps.D3D11Available}, error={gpu.LastGpuError}");
            return;
        }

        using var picture = Solid(1152, 2048, 255, 0, 0);
        using var result = gpu.Render([new CompositorLayer(picture)], 1920, 1080);
        output.WriteLine($"gpu={gpu.IsGpuReady}; adapter={caps.D3D11AdapterDescription}");
        AssertPillarboxed(result);
    }

    [Fact]
    public void MatchingBox_RendersExactlyAsBefore()
    {
        // The fitted box FitTransform gives the still draws the same pixels as stretching to it did.
        using var picture = Gradient(72, 128);
        var box = Temno.Core.Editing.LayoutEngine.FitTransform(72, 128, 160, 90);
        using var fitted = new CpuCompositor().Render([new CompositorLayer(picture, transform: box)], 160, 90);
        using var again = new CpuCompositor().Render([new CompositorLayer(picture, transform: box.Clone())], 160, 90);
        Assert.Equal(fitted.Data, again.Data);
        Assert.Equal(0, fitted.Data[(45 * 160 + 10) * 4]);
    }

    [Fact]
    public async Task Crop_DrawsOnlyTheVisibleRegion_WhereItSat()
    {
        using var project = new OneClipProject(64, 36);
        using var uncropped = await project.RenderAsync(crop: null);
        using var cropped = await project.RenderAsync(new Crop { Left = 0.5 });

        for (var y = 0; y < 36; y++)
        for (var x = 0; x < 64; x++)
        {
            var i = (y * 64 + x) * 4;
            if (x < 32)
            {
                Assert.Equal(0, cropped.Data[i]);
                Assert.Equal(0, cropped.Data[i + 1]);
                Assert.Equal(0, cropped.Data[i + 2]);
            }
            else
            {
                Assert.Equal(uncropped.Data[i], cropped.Data[i]);
                Assert.Equal(uncropped.Data[i + 1], cropped.Data[i + 1]);
                Assert.Equal(uncropped.Data[i + 2], cropped.Data[i + 2]);
            }
        }
    }

    [Fact]
    public async Task CropOfAPictureWithinTolerance_IsNotFittedASecondTime()
    {
        // WP-405 T1 finding: 480x272 in a full box on 480x270 is 0.013 off, so it draws as the box; its
        // bottom half (480x136 in a 480x135 region) is 0.026 off. Fitting that region again would narrow
        // it and open side bars. The region must cover every column, as the uncropped picture does.
        using var project = new OneClipProject(480, 272, red: true);
        using var cropped = await project.RenderAsync(new Crop { Top = 0.5 }, canvasWidth: 480, canvasHeight: 270);

        var (first, last, count) = RedColumns(cropped, 200);
        Assert.Equal((0, 479, 480), (first, last, count));
        Assert.Equal((-1, -1, 0), RedColumns(cropped, 60));
    }

    [Fact]
    public async Task CropOfEverything_DrawsNothing()
    {
        using var project = new OneClipProject(64, 36);
        using var cropped = await project.RenderAsync(new Crop { Left = 0.5, Right = 0.5 });
        Assert.All(Enumerable.Range(0, 64 * 36), p => Assert.Equal(0, cropped.Data[p * 4]));
    }

    [Fact]
    public async Task PortraitStillOnTimeline_RendersPillarboxed()
    {
        using var project = new OneClipProject(1152, 2048, red: true);
        using var frame = await project.RenderAsync(crop: null, canvasWidth: 1920, canvasHeight: 1080);
        AssertPillarboxed(frame);
    }

    private sealed class OneClipProject : IDisposable
    {
        private readonly string _path = Path.GetTempFileName();
        private readonly int _width;
        private readonly int _height;
        private readonly bool _red;

        public OneClipProject(int width, int height, bool red = false)
        {
            _width = width;
            _height = height;
            _red = red;
        }

        public async Task<VideoFrame> RenderAsync(Crop? crop, int canvasWidth = 64, int canvasHeight = 36)
        {
            var manifest = new MediaManifest
            {
                Entries = [new MediaManifestEntry
                {
                    Id = "still",
                    Name = "Still",
                    Type = ClipType.image,
                    Source = new MediaSource.ExternalSource(_path),
                    SourceWidth = _width,
                    SourceHeight = _height,
                }],
            };
            var clip = new Clip
            {
                MediaRef = "still",
                MediaType = ClipType.image,
                SourceClipType = ClipType.image,
                StartFrame = 0,
                DurationFrames = 10,
                Crop = crop ?? new Crop(),
            };
            var timeline = new Timeline
            {
                Fps = 10,
                Width = canvasWidth,
                Height = canvasHeight,
                Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
            };
            var source = new StillSource(() => _red ? Solid(_width, _height, 255, 0, 0) : Gradient(_width, _height));
            using var renderer = new TimelineFrameRenderer(
                source, new MediaResolver(() => manifest, () => null), new CpuCompositor());
            var frame = await renderer.RenderAsync(timeline, 0, canvasWidth, canvasHeight);
            Assert.NotNull(frame);
            return frame!;
        }

        public void Dispose() => File.Delete(_path);
    }

    private sealed class StillSource(Func<VideoFrame> make) : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
            => Task.FromResult(make());
    }
}
