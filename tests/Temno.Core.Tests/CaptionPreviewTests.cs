using System.Text.Json;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Playback;
using Temno.Media.Text;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-124: the preview primitive. A caption preview must be produced by the same renderer as the
/// final pixels, or it is decoration rather than a preview.
/// </summary>
public sealed class CaptionPreviewTests
{
    private static readonly TextStyle.RGBA White = new() { R = 1, G = 1, B = 1, A = 1 };

    private static TextStyle Style() => new() { FontSize = 90, Color = White };

    private static TextAnimation Anim(string preset) => new()
    {
        Preset = preset,
        PerWordFrames = 6,
        Highlight = new TextStyle.RGBA { R = 1, G = 0, B = 0, A = 1 },
    };

    // ------------------------------------------------------------------ loop policy

    [Theory]
    [InlineData(null, CaptionPreview.EntranceLoopFrames)]
    [InlineData("none", CaptionPreview.EntranceLoopFrames)]
    [InlineData("fadeIn", CaptionPreview.EntranceLoopFrames)]
    [InlineData("popIn", CaptionPreview.EntranceLoopFrames)]
    [InlineData("slideUp", CaptionPreview.EntranceLoopFrames)]
    [InlineData("typewriter", CaptionPreview.EntranceLoopFrames)]
    [InlineData("wordReveal", CaptionPreview.PerWordLoopFrames)]
    [InlineData("wordSlide", CaptionPreview.PerWordLoopFrames)]
    [InlineData("wordPop", CaptionPreview.PerWordLoopFrames)]
    [InlineData("wordCycle", CaptionPreview.PerWordLoopFrames)]
    [InlineData("highlightPop", CaptionPreview.PerWordLoopFrames)]
    [InlineData("highlightBlock", CaptionPreview.PerWordLoopFrames)]
    public void LoopFramesFollowsTheRenderMode(string? preset, int expected)
        => Assert.Equal(expected, CaptionPreview.LoopFrames(preset));

    [Fact]
    public void MacLoopLengthsArePreserved()
    {
        // Mac CaptionPreviewRender.loopFrames: 28 entrance, 54 per-word.
        Assert.Equal(28, CaptionPreview.EntranceLoopFrames);
        Assert.Equal(54, CaptionPreview.PerWordLoopFrames);
    }

    // ------------------------------------------------------------------ synthetic clip

    [Fact]
    public void BuiltClipLooksLikeAGeneratedCaptionClip()
    {
        var clip = CaptionPreview.BuildClip(
            CaptionPreview.SampleText, Style(), CaptionPreview.GalleryTransform(), Anim("highlightPop"));

        Assert.Equal(ClipType.text, clip.MediaType);
        Assert.Equal(ClipType.text, clip.SourceClipType);
        Assert.Equal("", clip.MediaRef);
        Assert.Equal(0, clip.StartFrame);
        Assert.Equal(CaptionPreview.PerWordLoopFrames, clip.DurationFrames);
        Assert.Equal(CaptionPreview.SampleText, clip.TextContent);

        // Style and animation round-trip through the same JSON a real caption clip stores.
        Assert.NotNull(clip.TextStyle);
        Assert.Equal(90, clip.TextStyle!.Value.GetProperty("fontSize").GetDouble());
        Assert.NotNull(clip.TextAnimation);
        Assert.Equal("highlightPop", clip.TextAnimation!.Value.GetProperty("preset").GetString());

        var style = JsonSerializer.Deserialize<TextStyle>(clip.TextStyle.Value.GetRawText(), JsonOptions.ForProject);
        Assert.NotNull(style);
        Assert.Equal(90, style!.FontSize);
    }

    [Fact]
    public void AnOffPresetStoresNoAnimation()
    {
        foreach (var preset in new string?[] { null, "", "none", "off" })
        {
            var anim = preset is null ? null : new TextAnimation { Preset = preset };
            var clip = CaptionPreview.BuildClip(
                CaptionPreview.GallerySample, Style(), CaptionPreview.GalleryTransform(), anim);

            Assert.Null(clip.TextAnimation);
            Assert.Equal(CaptionPreview.EntranceLoopFrames, clip.DurationFrames);
        }
    }

    [Fact]
    public void SuppliedWordTimingsAreClonedNotAliased()
    {
        List<WordTiming> timings = [new() { Text = "Aa", StartFrame = 0, EndFrame = 10 }];
        var clip = CaptionPreview.BuildClip(
            CaptionPreview.GallerySample, Style(), CaptionPreview.GalleryTransform(),
            Anim("highlightPop"), timings);

        Assert.NotNull(clip.WordTimings);
        timings[0].StartFrame = 999;
        Assert.Equal(0, clip.WordTimings![0].StartFrame);
    }

    [Fact]
    public void GalleryTransformMatchesMacsPreviewClip()
    {
        var t = CaptionPreview.GalleryTransform();
        Assert.Equal(0.5, t.CenterX, 3);
        Assert.Equal(0.5, t.CenterY, 3);
        Assert.Equal(0.92, t.Width, 3);
        Assert.Equal(0.55, t.Height, 3);
    }

    // ------------------------------------------------------------------ the guarantee

    [Fact]
    public async Task APreviewFrameIsByteIdenticalToTheRealRenderer()
    {
        // The whole point: a preview must be produced by the renderer that produces the export, so
        // the two cannot drift. Build the same timeline by hand and compare buffers.
        var clip = CaptionPreview.BuildClip(
            CaptionPreview.SampleText, Style(), CaptionPreview.GalleryTransform(), Anim("highlightPop"));

        using var viaPreview = await CaptionPreview.RenderAsync(clip, frame: 12, 320, 120);
        Assert.NotNull(viaPreview);

        var timeline = new Timeline
        {
            Fps = 30,
            Width = 320,
            Height = 120,
            Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
        };
        using var viaRenderer = await new TimelineFrameRenderer(new NeverFrameSource())
            .RenderAsync(timeline, 12, 320, 120);
        Assert.NotNull(viaRenderer);

        Assert.Equal(viaRenderer!.Width, viaPreview!.Width);
        Assert.Equal(viaRenderer.Height, viaPreview.Height);
        Assert.Equal(viaRenderer.PixelFormat, viaPreview.PixelFormat);
        Assert.Equal(viaRenderer.Data, viaPreview.Data);
    }

    [Fact]
    public async Task APerWordPreviewAdvancesAcrossItsLoop()
    {
        var clip = CaptionPreview.BuildClip(
            "AAA BBB CCC", Style(), CaptionPreview.GalleryTransform(), Anim("highlightPop"));

        using var early = await CaptionPreview.RenderAsync(clip, 4, 320, 120);
        using var late = await CaptionPreview.RenderAsync(clip, 40, 320, 120);

        Assert.NotNull(early);
        Assert.NotNull(late);
        Assert.NotEqual(early!.Data, late!.Data);
    }

    [Fact]
    public async Task ThePosterFrameIsStable()
    {
        // The gallery shows LoopFrames - 1 at rest; rendering it twice must produce the same bytes,
        // otherwise poster-frame caching would flicker.
        var clip = CaptionPreview.BuildClip(
            CaptionPreview.GallerySample, Style(), CaptionPreview.GalleryTransform(), Anim("wordReveal"));
        var poster = CaptionPreview.LoopFrames("wordReveal") - 1;

        using var a = await CaptionPreview.RenderAsync(clip, poster, 168, 96);
        using var b = await CaptionPreview.RenderAsync(clip, poster, 168, 96);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Data, b!.Data);
    }

    [Fact]
    public async Task RenderingHonoursTheRequestedPixelSize()
    {
        // Backing-resolution rendering just means asking for more pixels; prove the size is respected.
        var clip = CaptionPreview.BuildClip(
            CaptionPreview.GallerySample, Style(), CaptionPreview.GalleryTransform(), null);

        using var oneX = await CaptionPreview.RenderAsync(clip, 0, 168, 96);
        using var oneAndAHalfX = await CaptionPreview.RenderAsync(clip, 0, 252, 144);

        Assert.Equal(168, oneX!.Width);
        Assert.Equal(96, oneX.Height);
        Assert.Equal(252, oneAndAHalfX!.Width);
        Assert.Equal(144, oneAndAHalfX.Height);
    }

    [Fact]
    public async Task ThePreviewNeverAsksForMediaFrames()
    {
        // A text-only preview timeline must not touch a frame source. If it ever does, the throwing
        // source turns a silent wrong-pixels bug into a loud one.
        var clip = CaptionPreview.BuildClip(
            CaptionPreview.SampleText, Style(), CaptionPreview.GalleryTransform(), Anim("wordSlide"));

        var frames = CaptionPreview.LoopFrames("wordSlide");
        var rendered = 0;
        for (var f = 0; f < frames; f += 6)
        {
            // Early frames of a per-word preset legitimately draw nothing — every word is still at
            // zero opacity — so the assertion is that no frame source is touched, not that every
            // frame produces pixels.
            using var output = await CaptionPreview.RenderAsync(clip, f, 200, 80);
            if (output is not null) rendered++;
        }
        Assert.True(rendered > 0, "the preview must produce pixels once its words arrive");
    }

    [Fact]
    public async Task RenderRejectsANonPositiveSize()
    {
        var clip = CaptionPreview.BuildClip(
            CaptionPreview.GallerySample, Style(), CaptionPreview.GalleryTransform(), null);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CaptionPreview.RenderAsync(clip, 0, 0, 96));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CaptionPreview.RenderAsync(clip, 0, 168, 0));
    }

    private sealed class NeverFrameSource : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(
            string path, double sourceSeconds, CancellationToken cancellationToken = default)
            => throw new FileNotFoundException(path);
    }
}
