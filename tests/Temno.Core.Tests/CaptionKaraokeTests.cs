using System.Text.Json;
using Temno.Core.Captions;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Playback;
using Temno.Media.Text;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-123 per-word compositing, and the WP-115 timing behaviour it builds on. Every vector here is
/// transcribed from Mac <c>Compositing/TextAnimator.swift</c> and <c>TextFrameRenderer.swift</c>
/// @ cfe9c18 — unlike the gap policy in WP-115, this part of Mac is present and readable.
/// </summary>
public sealed class CaptionKaraokeTests
{
    private const string Line = "hello brave new world";

    private static List<WordTiming> Timings() =>
    [
        new() { Text = "hello", StartFrame = 0, EndFrame = 10 },
        new() { Text = "brave", StartFrame = 10, EndFrame = 20 },
        new() { Text = "new", StartFrame = 20, EndFrame = 30 },
        new() { Text = "world", StartFrame = 30, EndFrame = 40 },
    ];

    private static TextAnimation Anim(string preset) => new()
    {
        Preset = preset,
        PerWordFrames = 6,
        Highlight = new TextStyle.RGBA { R = 1, G = 0, B = 0, A = 1 },
    };

    private static readonly TextStyle.RGBA White = new() { R = 1, G = 1, B = 1, A = 1 };

    /// <summary>The word whose rendered text matches, or null when the animator dropped it.</summary>
    private static TextAnimator.WordRender? Word(TextAnimator.ClipState state, string text, string word)
    {
        if (state.Words is null) return null;
        foreach (var w in state.Words)
            if (text.Substring(w.Start, w.Length) == word) return w;
        return null;
    }

    // ------------------------------------------------------------------ easing (Mac parity)

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.25, 0.15625)]
    public void SmoothstepMatchesSwift(double t, double expected)
        => Assert.Equal(expected, TextAnimator.Smoothstep(t), 6);

    [Fact]
    public void OvershootPassesOneBeforeSettling()
    {
        // Mac back-ease with s = 1.70158: 0 at t=0, exactly 1 at t=1, above 1 in between.
        Assert.Equal(0, TextAnimator.Overshoot(0), 6);
        Assert.Equal(1, TextAnimator.Overshoot(1), 6);
        Assert.True(TextAnimator.Overshoot(0.75) > 1, "the back-ease must overshoot before settling");
    }

    [Fact]
    public void LinearRampClampsAtBothEnds()
    {
        Assert.Equal(0, TextAnimator.Linear(0, 0, 6), 6);
        Assert.Equal(0, TextAnimator.Linear(-5, 0, 6), 6);
        Assert.Equal(0.5, TextAnimator.Linear(3, 0, 6), 6);
        Assert.Equal(1, TextAnimator.Linear(6, 0, 6), 6);
        Assert.Equal(1, TextAnimator.Linear(99, 0, 6), 6);
    }

    [Fact]
    public void ActiveRampIsZeroOutsideTheWordAndPeaksInside()
    {
        var word = new WordTiming { Text = "w", StartFrame = 10, EndFrame = 30 };

        Assert.Equal(0, TextAnimator.ActiveRamp(9, word, 4), 6);
        Assert.Equal(0, TextAnimator.ActiveRamp(30, word, 4), 6);   // end is exclusive
        Assert.Equal(0, TextAnimator.ActiveRamp(10, word, 4), 6);   // the ramp starts at 0
        Assert.Equal(1, TextAnimator.ActiveRamp(20, word, 4), 6);   // fully on mid-word
        Assert.True(TextAnimator.ActiveRamp(11, word, 4) is > 0 and < 1, "ramps in");
        Assert.True(TextAnimator.ActiveRamp(29, word, 4) is > 0 and < 1, "ramps out");
    }

    [Fact]
    public void ActiveRampShortensSoAFastWordStillReachesOne()
    {
        // A 2-frame word: the ramp is capped at span/2 so it still reaches full strength.
        var quick = new WordTiming { Text = "a", StartFrame = 0, EndFrame = 2 };
        Assert.Equal(1, TextAnimator.ActiveRamp(1, quick, 6), 6);

        // A 1-frame word is simply on for its single frame.
        var single = new WordTiming { Text = "a", StartFrame = 5, EndFrame = 6 };
        Assert.Equal(1, TextAnimator.ActiveRamp(5, single, 6), 6);
    }

    // ------------------------------------------------------------------ entrance presets

    [Fact]
    public void EntrancePresetsAreTimedOffPerWordFrames()
    {
        // Mac clipEntry uses max(1, perWordFrames) — not the clip duration.
        var anim = new TextAnimation { Preset = "fadeIn", PerWordFrames = 6 };
        Assert.Equal(0, TextAnimator.Evaluate(anim, Line, 0, 300).Opacity, 6);
        Assert.Equal(1, TextAnimator.Evaluate(anim, Line, 6, 300).Opacity, 6);
        Assert.True(TextAnimator.Evaluate(anim, Line, 3, 300).Opacity is > 0 and < 1);
    }

    [Fact]
    public void PopInDoesNotOvershoot()
    {
        // Mac popIn is 0.6 + 0.4 * t with no back-ease; only wordPop overshoots.
        var anim = new TextAnimation { Preset = "popIn", PerWordFrames = 6 };
        for (var rel = 0; rel <= 6; rel++)
            Assert.InRange(TextAnimator.Evaluate(anim, Line, rel, 300).Scale, 0.6, 1.0);
        Assert.Equal(1.0, TextAnimator.Evaluate(anim, Line, 6, 300).Scale, 6);
    }

    [Fact]
    public void SlideUpUsesTheMacOffset()
    {
        var anim = new TextAnimation { Preset = "slideUp", PerWordFrames = 6 };
        Assert.Equal(0.05, TextAnimator.Evaluate(anim, Line, 0, 300).OffsetY, 6);
        Assert.Equal(0, TextAnimator.Evaluate(anim, Line, 6, 300).OffsetY, 6);
    }

    [Fact]
    public void EntranceAndTypewriterPresetsProduceNoWordStates()
    {
        Assert.Null(TextAnimator.Evaluate(null, Line, 5, 40, Timings()).Words);
        Assert.Null(TextAnimator.Evaluate(Anim("fadeIn"), Line, 5, 40, Timings()).Words);
        Assert.Null(TextAnimator.Evaluate(Anim("typewriter"), Line, 5, 40, Timings()).Words);
        Assert.Equal(
            "hel",
            TextAnimator.Evaluate(
                new TextAnimation { Preset = "typewriter", PerWordFrames = 1 }, Line, 3, 40).VisibleText);
    }

    // ------------------------------------------------------------------ per-word states

    [Theory]
    [InlineData(5, "hello")]
    [InlineData(15, "brave")]
    [InlineData(25, "new")]
    [InlineData(35, "world")]
    public void HighlightPopTintsAndScalesTheSpokenWord(int rel, string spoken)
    {
        var state = TextAnimator.Evaluate(Anim("highlightPop"), Line, rel, 40, Timings(), White);

        Assert.Equal(Line, state.VisibleText);
        var word = Word(state, Line, spoken);
        Assert.NotNull(word);
        // Mid-word the ramp is fully on: tinted to the highlight and scaled by 1.15.
        Assert.Equal(1.15, word!.Value.Scale, 3);
        Assert.Equal(1, word.Value.Color.R, 3);
        Assert.Equal(0, word.Value.Color.G, 3);
        Assert.Null(word.Value.Background);

        // Every other word stays white and unscaled.
        foreach (var other in state.Words!)
        {
            if (other.Index == word.Value.Index) continue;
            Assert.Equal(1, other.Scale, 3);
            Assert.Equal(1, other.Color.G, 3);
        }
    }

    [Fact]
    public void HighlightBlockKeepsTheWordColourAndFillsBehindIt()
    {
        var state = TextAnimator.Evaluate(Anim("highlightBlock"), Line, 25, 40, Timings(), White);

        var word = Word(state, Line, "new");
        Assert.NotNull(word);
        // Mac: the word stays base colour and the block carries the highlight.
        Assert.Equal(1, word!.Value.Color.G, 3);
        Assert.Equal(1, word.Value.Scale, 3);
        Assert.NotNull(word.Value.Background);
        Assert.Equal(1, word.Value.Background!.R, 3);
        Assert.Equal(0, word.Value.Background.G, 3);
        Assert.Equal(1, word.Value.Background.A, 3);

        // A word that is not being spoken still carries a block, at zero alpha — Mac sets bgColor
        // unconditionally and guards on alpha at draw time, which both rasterizer paths mirror.
        Assert.Equal(0, Word(state, Line, "hello")!.Value.Background!.A, 3);
    }

    [Fact]
    public void HighlightBlockRampsItsBlockAlpha()
    {
        var timings = Timings();
        var edge = TextAnimator.Evaluate(Anim("highlightBlock"), Line, 21, 40, timings, White);
        var mid = TextAnimator.Evaluate(Anim("highlightBlock"), Line, 25, 40, timings, White);

        var edgeAlpha = Word(edge, Line, "new")!.Value.Background!.A;
        var midAlpha = Word(mid, Line, "new")!.Value.Background!.A;
        Assert.True(edgeAlpha < midAlpha, $"block alpha must ramp in: {edgeAlpha} -> {midAlpha}");
    }

    [Fact]
    public void WordRevealFadesWordsInsteadOfTruncatingTheLine()
    {
        var anim = new TextAnimation { Preset = "wordReveal", PerWordFrames = 6 };
        var state = TextAnimator.Evaluate(anim, Line, 22, 40, Timings(), White);

        // The whole line is still the rendered string — nothing is chopped off.
        Assert.Equal(Line, state.VisibleText);
        Assert.NotNull(state.Words);

        // "new" starts at frame 20, so two frames in it is partway through its fade.
        var partial = Word(state, Line, "new");
        Assert.NotNull(partial);
        Assert.InRange(partial!.Value.Opacity, 0.01, 0.99);

        // Words already spoken are fully on; words not yet reached were dropped (opacity 0).
        Assert.Equal(1, Word(state, Line, "hello")!.Value.Opacity, 3);
        Assert.Null(Word(state, Line, "world"));
    }

    [Fact]
    public void WordSlideOffsetsTheArrivingWord()
    {
        var anim = new TextAnimation { Preset = "wordSlide", PerWordFrames = 6 };
        var state = TextAnimator.Evaluate(anim, Line, 22, 40, Timings(), White);

        Assert.True(Word(state, Line, "new")!.Value.OffsetY > 0, "an arriving word slides into place");
        Assert.Equal(0, Word(state, Line, "hello")!.Value.OffsetY, 3);
    }

    [Fact]
    public void WordPopOvershootsTheArrivingWord()
    {
        var anim = new TextAnimation { Preset = "wordPop", PerWordFrames = 6 };
        var scales = new List<double>();
        for (var rel = 20; rel <= 26; rel++)
        {
            if (Word(TextAnimator.Evaluate(anim, Line, rel, 40, Timings(), White), Line, "new") is { } w)
                scales.Add(w.Scale);
        }

        Assert.NotEmpty(scales);
        Assert.True(scales.Max() > 1.0, "wordPop must overshoot past its settled size");
    }

    [Fact]
    public void WordCycleOnlyLightsTheCurrentWord()
    {
        var anim = new TextAnimation { Preset = "wordCycle", PerWordFrames = 6 };
        var state = TextAnimator.Evaluate(anim, Line, 25, 40, Timings(), White);

        Assert.Equal(1, Word(state, Line, "new")!.Value.Opacity, 3);
        // Words outside their span ramp to zero and are dropped entirely.
        Assert.Null(Word(state, Line, "hello"));
        Assert.Null(Word(state, Line, "world"));
    }

    // ------------------------------------------------------------------ token / timing alignment

    [Fact]
    public void TimingsZipWhenTheCountsAgree()
    {
        var timings = TextAnimator.TokenTimings(Line, TextAnimator.WordSpans(Line), Timings(), 40);

        Assert.Equal(4, timings.Length);
        Assert.Equal(20, timings[2].StartFrame);
        Assert.Equal("new", timings[2].Text);
    }

    [Fact]
    public void ASplitTranscriptRunStillLandsOnTheRightWord()
    {
        // The ASR split "don't" into two runs. Mac's grouping reconciles them onto one token.
        const string text = "don't stop";
        List<WordTiming> words =
        [
            new() { Text = "don", StartFrame = 0, EndFrame = 6 },
            new() { Text = "t", StartFrame = 6, EndFrame = 10 },
            new() { Text = "stop", StartFrame = 10, EndFrame = 20 },
        ];

        var timings = TextAnimator.TokenTimings(text, TextAnimator.WordSpans(text), words, 20);

        Assert.Equal(2, timings.Length);
        Assert.Equal(0, timings[0].StartFrame);
        Assert.Equal(10, timings[0].EndFrame);   // the split run's whole span
        Assert.Equal(10, timings[1].StartFrame);
        Assert.Equal(20, timings[1].EndFrame);
    }

    [Fact]
    public void CasedAndPunctuatedTextStillAlignsToRawTimings()
    {
        const string cased = "HELLO, BRAVE NEW WORLD!";
        List<WordTiming> words =
        [
            new() { Text = "hello", StartFrame = 0, EndFrame = 10 },
            new() { Text = "brave", StartFrame = 10, EndFrame = 20 },
            new() { Text = "new", StartFrame = 20, EndFrame = 30 },
            new() { Text = "world", StartFrame = 30, EndFrame = 40 },
        ];

        var state = TextAnimator.Evaluate(Anim("highlightPop"), cased, 35, 40, words, White);
        Assert.Equal(1.15, Word(state, cased, "WORLD!")!.Value.Scale, 3);
    }

    [Fact]
    public void NoTimingsFallsBackToAnEvenSplit()
    {
        var state = TextAnimator.Evaluate(Anim("highlightPop"), Line, 25, 40, wordTimings: null, White);

        // 4 words across 40 frames: frame 25 sits in the third.
        Assert.Equal(1.15, Word(state, Line, "new")!.Value.Scale, 3);
    }

    [Fact]
    public void AnIrreconcilableTimingListDegradesToAnEvenSplit()
    {
        // A dropped timing cannot be reconciled by Mac's grouping, so it falls back to an even split
        // rather than shifting every later word onto the wrong token.
        List<WordTiming> sparse =
        [
            new() { Text = "hello", StartFrame = 0, EndFrame = 10 },
            new() { Text = "new", StartFrame = 20, EndFrame = 30 },
            new() { Text = "world", StartFrame = 30, EndFrame = 40 },
        ];

        var state = TextAnimator.Evaluate(Anim("highlightPop"), Line, 25, 40, sparse, White);
        Assert.Equal(1.15, Word(state, Line, "new")!.Value.Scale, 3);
    }

    [Fact]
    public void WordSpansMapBackOntoTheSourceString()
    {
        var spans = TextAnimator.WordSpans("  a  bb   ccc ");
        Assert.Equal(3, spans.Count);
        Assert.Equal((2, 1), spans[0]);
        Assert.Equal((5, 2), spans[1]);
        Assert.Equal((10, 3), spans[2]);
    }

    // ------------------------------------------------------------------ pixels

    private const string SampleLine = "AAA BBB CCC";

    private static List<WordTiming> SampleTimings() =>
    [
        new() { Text = "AAA", StartFrame = 0, EndFrame = 20 },
        new() { Text = "BBB", StartFrame = 20, EndFrame = 40 },
        new() { Text = "CCC", StartFrame = 40, EndFrame = 60 },
    ];

    private static TextRasterization Raster(string preset, int rel)
    {
        var style = new TextStyle { FontSize = 90, Color = White };
        var state = TextAnimator.Evaluate(Anim(preset), SampleLine, rel, 60, SampleTimings(), White);
        var runs = state.Words?
            .Select(w => new TextWordRun(w.Start, w.Length, w.Opacity, w.Scale, w.OffsetY, w.Color, w.Background))
            .ToList();

        return new CpuTextRasterizer().Rasterize(SampleLine, style, 480, 160, 0.5, 0.5, 0.95, 1, runs);
    }

    private static int CountRed(TextRasterization r)
    {
        var n = 0;
        for (var i = 0; i + 3 < r.Bgra.Length; i += 4)
            if (r.Bgra[i + 2] > 120 && r.Bgra[i + 2] - r.Bgra[i + 1] > 90 && r.Bgra[i + 2] - r.Bgra[i] > 90) n++;
        return n;
    }

    private static int CountWhite(TextRasterization r)
    {
        var n = 0;
        for (var i = 0; i + 3 < r.Bgra.Length; i += 4)
            if (r.Bgra[i] > 150 && r.Bgra[i + 1] > 150 && r.Bgra[i + 2] > 150) n++;
        return n;
    }

    [Fact]
    public void HighlightBlockAndHighlightPopNoLongerRenderIdentically()
    {
        var pop = Raster("highlightPop", 30);
        var block = Raster("highlightBlock", 30);

        Assert.NotEqual(pop.Bgra, block.Bgra);

        // A filled block covers far more area than tinted glyph strokes do.
        Assert.True(
            CountRed(block) > CountRed(pop) * 2,
            $"block should fill behind the word: block={CountRed(block)} pop={CountRed(pop)}");

        // ...and the word itself stays readable in its base colour on top of the block.
        Assert.True(CountWhite(block) > 0, "highlightBlock must leave the word in base colour");
    }

    [Fact]
    public void HighlightPopTintsGlyphsWithoutFillingABlock()
    {
        var plain = Raster("none", 30);
        var pop = Raster("highlightPop", 30);

        Assert.Equal(0, CountRed(plain));
        Assert.True(CountRed(pop) > 0, "the spoken word must be tinted");
        Assert.True(CountWhite(pop) > 0, "the other words must stay white");
    }

    [Fact]
    public void PerWordDrawingReproducesTheWholeLineLayout()
    {
        // Each word is drawn as its own layout at the rect the line layout gave it. If that per-word
        // layout re-applies the line's centring, every word shifts sideways and the result stops
        // matching the plain line. At full opacity and unit scale the two must agree to within
        // antialiasing.
        var style = new TextStyle { FontSize = 90, Color = White };
        var rasterizer = new CpuTextRasterizer();

        var plain = rasterizer.Rasterize(SampleLine, style, 480, 160, 0.5, 0.5, 0.95, 1);

        // wordCycle mid-word leaves one word on; use wordReveal at the end of its loop, where every
        // word has fully arrived, so the only difference from the plain line is how it was drawn.
        var anim = new TextAnimation { Preset = "wordReveal", PerWordFrames = 6 };
        var state = TextAnimator.Evaluate(anim, SampleLine, 59, 60, SampleTimings(), White);
        var runs = state.Words!
            .Select(w => new TextWordRun(w.Start, w.Length, w.Opacity, w.Scale, w.OffsetY, w.Color, w.Background))
            .ToList();
        Assert.Equal(3, runs.Count);
        Assert.All(runs, r => Assert.Equal(1, r.Opacity, 3));

        var perWord = rasterizer.Rasterize(SampleLine, style, 480, 160, 0.5, 0.5, 0.95, 1, runs);

        var plainLit = CountLit(plain);
        var perWordLit = CountLit(perWord);
        Assert.True(plainLit > 0);
        var drift = Math.Abs(perWordLit - plainLit) / (double)plainLit;
        Assert.True(
            drift < 0.05,
            $"per-word drawing drifted from the line layout: plain={plainLit} perWord={perWordLit} ({drift:P1})");
    }

    [Fact]
    public void APerWordPresetWithNothingVisibleDrawsNothing()
    {
        // At frame 0 of wordReveal every word is still at zero opacity. An empty run list must not
        // fall through to the plain whole-line draw.
        var anim = new TextAnimation { Preset = "wordReveal", PerWordFrames = 6 };
        var state = TextAnimator.Evaluate(anim, SampleLine, 0, 60, SampleTimings(), White);

        Assert.NotNull(state.Words);
        Assert.Empty(state.Words!);
    }

    [Fact]
    public async Task NothingIsRenderedWhileEveryWordIsStillHidden()
    {
        var clip = new Clip
        {
            MediaRef = "",
            MediaType = ClipType.text,
            SourceClipType = ClipType.text,
            StartFrame = 0,
            DurationFrames = 60,
            Opacity = 1,
            TextContent = SampleLine,
            Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.95, Height = 1 },
            TextStyle = JsonSerializer.SerializeToElement(
                new TextStyle { FontSize = 90, Color = White }, JsonOptions.ForProject),
            TextAnimation = JsonSerializer.SerializeToElement(
                new TextAnimation { Preset = "wordReveal", PerWordFrames = 6 }, JsonOptions.ForProject),
            WordTimings = SampleTimings(),
        };
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 480,
            Height = 160,
            Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
        };
        var renderer = new TimelineFrameRenderer(new NeverFrameSource());

        using var hidden = await renderer.RenderAsync(timeline, 0, 480, 160);
        using var shown = await renderer.RenderAsync(timeline, 59, 480, 160);

        Assert.Null(hidden);            // no visible layer at all
        Assert.NotNull(shown);
    }

    private static int CountLit(TextRasterization r)
    {
        var n = 0;
        for (var i = 0; i + 3 < r.Bgra.Length; i += 4)
            if (r.Bgra[i + 3] > 40) n++;
        return n;
    }

    [Fact]
    public async Task RenderedFramesEmphasizeDifferentWordsOverTime()
    {
        var clip = new Clip
        {
            MediaRef = "",
            MediaType = ClipType.text,
            SourceClipType = ClipType.text,
            StartFrame = 0,
            DurationFrames = 60,
            Opacity = 1,
            TextContent = SampleLine,
            Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.95, Height = 1 },
            TextStyle = JsonSerializer.SerializeToElement(
                new TextStyle { FontSize = 90, Color = White }, JsonOptions.ForProject),
            TextAnimation = JsonSerializer.SerializeToElement(Anim("highlightPop"), JsonOptions.ForProject),
            WordTimings = SampleTimings(),
        };
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 480,
            Height = 160,
            Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
        };
        var renderer = new TimelineFrameRenderer(new NeverFrameSource());

        using var early = await renderer.RenderAsync(timeline, 10, 480, 160);
        using var late = await renderer.RenderAsync(timeline, 50, 480, 160);
        Assert.NotNull(early);
        Assert.NotNull(late);

        var earlyColumns = TintedColumns(early!.Data, early.Width, early.PixelFormat);
        var lateColumns = TintedColumns(late!.Data, late.Width, late.PixelFormat);
        Assert.NotEmpty(earlyColumns);
        Assert.NotEmpty(lateColumns);
        Assert.True(
            earlyColumns.Max() < lateColumns.Min(),
            $"the emphasized word must move right: early={earlyColumns.Min()}..{earlyColumns.Max()}, "
            + $"late={lateColumns.Min()}..{lateColumns.Max()}");
    }

    private static List<int> TintedColumns(byte[] pixels, int width, string pixelFormat)
    {
        // The rasterizer emits BGRA; the compositor emits RGBA.
        var redOffset = pixelFormat.Equals("bgra", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        var blueOffset = 2 - redOffset;
        var columns = new List<int>();
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var r = pixels[i + redOffset];
            var g = pixels[i + 1];
            var b = pixels[i + blueOffset];
            if (r > 120 && r - g > 90 && r - b > 90) columns.Add(i / 4 % width);
        }
        return columns;
    }

    private sealed class NeverFrameSource : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(
            string path, double sourceSeconds, CancellationToken cancellationToken = default)
            => throw new FileNotFoundException(path);
    }
}
