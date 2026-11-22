using Temno.Core.Models;
using Temno.Media.Compositing;
using Temno.Media.Playback;
using Temno.Media.Text;
using Temno.Media;
using Xunit;

namespace Temno.Core.Tests;

public sealed class CpuTextRasterizerTests
{
    [Fact]
    public void Rasterize_ProducesNonEmptyPixels()
    {
        var rast = new CpuTextRasterizer();
        var style = new TextStyle
        {
            FontSize = 96,
            Color = new TextStyle.RGBA { R = 1, G = 1, B = 1, A = 1 },
            IsBold = true,
        };
        var result = rast.Rasterize("HI", style, 320, 180);
        Assert.Equal(320, result.Width);
        Assert.Equal(180, result.Height);
        Assert.Equal(320 * 180 * 4, result.Bgra.Length);
        Assert.Contains(result.Bgra, b => b > 0);
    }

    [Fact]
    public void Rasterize_WindowsUsesAntiAliasedSystemFont()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var rast = new CpuTextRasterizer();
        var style = new TextStyle
        {
            FontName = "Helvetica-Bold",
            FontSize = 96,
            Color = new TextStyle.RGBA { R = 1, G = 1, B = 1, A = 1 },
            IsBold = true,
        };
        var result = rast.Rasterize("Aa", style, 320, 180);
        var alphas = new HashSet<byte>();
        for (var i = 3; i < result.Bgra.Length; i += 4)
            alphas.Add(result.Bgra[i]);
        Assert.True(alphas.Count > 4, $"expected DirectWrite grayscale coverage, unique alphas={alphas.Count}");
        Assert.Contains(SystemFontCatalog.FamilyNames(), n => n.Contains("Segoe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TimelineFrameRenderer_IncludesTextClip()
    {
        var textClip = new Clip
        {
            Id = "t1",
            MediaType = ClipType.text,
            TextContent = "AB",
            StartFrame = 0,
            DurationFrames = 30,
            Opacity = 1,
            Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 1, Height = 1 },
            TextStyle = System.Text.Json.JsonSerializer.SerializeToElement(new TextStyle
            {
                FontSize = 120,
                Color = new TextStyle.RGBA { R = 1, G = 1, B = 1, A = 1 },
            }, JsonOptions.ForProject),
        };
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 64,
            Height = 64,
            Tracks = [new Track { Type = ClipType.video, Clips = [textClip] }],
        };
        var renderer = new TimelineFrameRenderer(new NeverFrameSource());
        using var frame = await renderer.RenderAsync(timeline, 0, 64, 64);
        Assert.NotNull(frame);
        // Black canvas + white text → some non-black opaque pixels.
        var bright = 0;
        for (var i = 0; i < frame!.Data.Length; i += 4)
        {
            if (frame.Data[i] > 40 || frame.Data[i + 1] > 40 || frame.Data[i + 2] > 40)
                bright++;
        }
        Assert.True(bright > 10, $"expected text pixels, bright={bright}");
    }

    [Fact]
    public async Task TimelineFrameRenderer_TextTransform_MovesScalesRotatesFlipsCropsAndFades()
    {
        static Clip MakeText(string content, Transform transform, TextStyle? style = null, Crop? crop = null, int fadeIn = 0)
            => new()
            {
                Id = "t1",
                MediaType = ClipType.text,
                SourceClipType = ClipType.text,
                TextContent = content,
                StartFrame = 0,
                DurationFrames = 60,
                Opacity = 1,
                FadeInFrames = fadeIn,
                Transform = transform,
                Crop = crop ?? new Crop(),
                TextStyle = System.Text.Json.JsonSerializer.SerializeToElement(
                    style ?? new TextStyle
                    {
                        FontSize = 180,
                        Color = new TextStyle.RGBA { R = 1, G = 1, B = 1, A = 1 },
                        Shadow = new TextStyle.ShadowStyle { Enabled = false },
                    },
                    JsonOptions.ForProject),
            };

        static (int MinX, int MaxX, int MinY, int MaxY, int Count) BrightBounds(VideoFrame frame)
        {
            int minX = frame.Width, minY = frame.Height, maxX = -1, maxY = -1, count = 0;
            for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var i = (y * frame.Width + x) * 4;
                if (frame.Data[i] > 40 || frame.Data[i + 1] > 40 || frame.Data[i + 2] > 40)
                {
                    count++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return (minX, maxX, minY, maxY, count);
        }

        async Task<VideoFrame?> Render(Clip clip, int frame = 0, int w = 320, int h = 180)
        {
            var timeline = new Timeline
            {
                Fps = 30,
                Width = w,
                Height = h,
                Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
            };
            using var renderer = new TimelineFrameRenderer(new NeverFrameSource());
            return await renderer.RenderAsync(timeline, frame, w, h);
        }

        // 1. Position move (left vs right, plus fully off-canvas).
        var leftClip = MakeText("HI", new Transform { CenterX = 0.25, CenterY = 0.25, Width = 0.5, Height = 0.3 });
        var rightClip = MakeText("HI", new Transform { CenterX = 0.75, CenterY = 0.75, Width = 0.5, Height = 0.3 });
        var offClip = MakeText("HI", new Transform { CenterX = 2.0, CenterY = 0.5, Width = 0.5, Height = 0.3 });
        using var leftFrame = await Render(leftClip);
        using var rightFrame = await Render(rightClip);
        using var offFrame = await Render(offClip);
        var lb = BrightBounds(leftFrame!);
        var rb = BrightBounds(rightFrame!);
        var ob = BrightBounds(offFrame!);
        Assert.True(lb.Count > 20 && rb.Count > 20);
        Assert.True((lb.MinX + lb.MaxX) / 2.0 < 120, $"left text center expected < 120, got {lb}");
        Assert.True((lb.MinY + lb.MaxY) / 2.0 < 75, $"top text center expected < 75, got {lb}");
        Assert.True((rb.MinX + rb.MaxX) / 2.0 > 200, $"right text center expected > 200, got {rb}");
        Assert.True((rb.MinY + rb.MaxY) / 2.0 > 105, $"bottom text center expected > 105, got {rb}");
        Assert.Equal(0, ob.Count);

        // 2. Rotation (90 deg turns wide text into tall text around its own center).
        var flatClip = MakeText("WIDE", new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.8, Height = 0.3, Rotation = 0 });
        var rotClip = MakeText("WIDE", new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.8, Height = 0.3, Rotation = 90 });
        using var flatFrame = await Render(flatClip);
        using var rotFrame = await Render(rotClip);
        var fb = BrightBounds(flatFrame!);
        var rtb = BrightBounds(rotFrame!);
        Assert.True((fb.MaxX - fb.MinX) > (fb.MaxY - fb.MinY), $"unrotated WIDE should be wider than tall: {fb}");
        Assert.True((rtb.MaxY - rtb.MinY) > (rtb.MaxX - rtb.MinX), $"90-deg WIDE should be taller than wide: {rtb}");

        // 3. FlipHorizontal flips an asymmetric glyph in place at an off-center position.
        var normalL = MakeText("L", new Transform { CenterX = 0.3, CenterY = 0.5, Width = 0.4, Height = 0.4 });
        var flippedL = MakeText("L", new Transform { CenterX = 0.3, CenterY = 0.5, Width = 0.4, Height = 0.4, FlipHorizontal = true });
        using var normalLFrame = await Render(normalL);
        using var flippedLFrame = await Render(flippedL);
        var nb = BrightBounds(normalLFrame!);
        var flb = BrightBounds(flippedLFrame!);
        Assert.True(Math.Abs((nb.MinX + nb.MaxX) / 2.0 - (flb.MinX + flb.MaxX) / 2.0) < 12,
            $"flipped text should stay in place near CenterX=0.3: normal={nb}, flipped={flb}");
        Assert.NotEqual(normalLFrame!.Data, flippedLFrame!.Data);

        // 4. FontScale and ScaleTrack keyframes both scale the rendered text.
        var baseScale = MakeText("HI", new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.5, Height = 0.3 },
            new TextStyle { FontSize = 140, FontScale = 1.0, Color = new TextStyle.RGBA { R = 1, G = 1, B = 1, A = 1 }, Shadow = new TextStyle.ShadowStyle { Enabled = false } });
        var doubleScale = MakeText("HI", new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.8, Height = 0.5 },
            new TextStyle { FontSize = 140, FontScale = 2.0, Color = new TextStyle.RGBA { R = 1, G = 1, B = 1, A = 1 }, Shadow = new TextStyle.ShadowStyle { Enabled = false } });
        using var baseScaleFrame = await Render(baseScale);
        using var doubleScaleFrame = await Render(doubleScale);
        Assert.True(BrightBounds(doubleScaleFrame!).Count > BrightBounds(baseScaleFrame!).Count * 1.5);

        baseScale.ScaleTrack = new KeyframeTrack<AnimPair>
        {
            Keyframes =
            [
                new Keyframe<AnimPair> { Frame = 0, Value = new AnimPair(0.5, 0.3) },
                new Keyframe<AnimPair> { Frame = 30, Value = new AnimPair(1.0, 0.6) },
            ],
        };
        using var kfStart = await Render(baseScale, frame: 0);
        using var kfEnd = await Render(baseScale, frame: 30);
        Assert.True(BrightBounds(kfEnd!).Count > BrightBounds(kfStart!).Count * 1.5);

        // 5. Crop trims visible text bounds; full crop hides it.
        var uncropped = MakeText("WIDE", new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.8, Height = 0.3 });
        var halfCropped = MakeText("WIDE", new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.8, Height = 0.3 },
            crop: new Crop { Left = 0.5 });
        var fullyCropped = MakeText("WIDE", new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.8, Height = 0.3 },
            crop: new Crop { Left = 0.5, Right = 0.5 });
        using var uncroppedFrame = await Render(uncropped);
        using var halfCroppedFrame = await Render(halfCropped);
        using var fullyCroppedFrame = await Render(fullyCropped);
        var ub = BrightBounds(uncroppedFrame!);
        var hb = BrightBounds(halfCroppedFrame!);
        Assert.True(hb.MinX > ub.MinX + (ub.MaxX - ub.MinX) / 3, $"left 50% crop must trim left pixels: uncropped={ub}, cropped={hb}");
        Assert.True(hb.Count > 10 && hb.Count < ub.Count);
        Assert.Equal(0, BrightBounds(fullyCroppedFrame!).Count);

        // 6. Fade-in starts transparent on frame 0 and renders after fade completes.
        var faded = MakeText("HI", new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.5, Height = 0.3 }, fadeIn: 15);
        using var fadeStart = await Render(faded, frame: 0);
        using var fadeDone = await Render(faded, frame: 20);
        Assert.Equal(0, BrightBounds(fadeStart!).Count);
        Assert.True(BrightBounds(fadeDone!).Count > 20);
    }

    private sealed class NeverFrameSource : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
            => throw new FileNotFoundException(path);
    }
}
