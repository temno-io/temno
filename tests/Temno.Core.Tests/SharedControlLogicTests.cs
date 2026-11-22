using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-127 shared editor controls — the logic the WinUI shells bind to. The controls themselves need
/// a GUI to verify; everything that can be decided by arithmetic is decided here.
/// </summary>
public class SharedControlLogicTests
{
    /// <summary>Mac's Close gaps field: 500 ms, 0–2000, step 10 (docs/reference/mac-ui/06).</summary>
    private static UnitFieldSpec MillisecondSpec => new()
    {
        Unit = "ms",
        Minimum = 0,
        Maximum = 2000,
        Step = 10,
        UnitsPerPixel = 5,
        Default = 500,
    };

    /// <summary>Mac's caption size field: 48 pt, 12–300 (docs/reference/mac-ui/11).</summary>
    private static UnitFieldSpec PointSpec => new()
    {
        Unit = "pt",
        Minimum = 12,
        Maximum = 300,
        Step = 1,
        UnitsPerPixel = 1,
        Default = 48,
    };

    // ---------------------------------------------------------------- formatting

    [Fact]
    public void TheMacFieldsRenderExactlyAsTheScreenshotsShowThem()
    {
        Assert.Equal("500 ms", MillisecondSpec.Format(500));
        Assert.Equal("48 pt", PointSpec.Format(48));
        Assert.Equal("100%", new UnitFieldSpec { Unit = "%" }.Format(100));
        Assert.Equal("0 px", new UnitFieldSpec { Unit = "px" }.Format(0));
    }

    [Fact]
    public void PercentHasNoSpaceButEveryOtherUnitDoes()
    {
        Assert.Equal("60%", new UnitFieldSpec { Unit = "%" }.Format(60));
        Assert.Equal("6.0 pt", new UnitFieldSpec { Unit = "pt", Decimals = 1 }.Format(6));
    }

    [Fact]
    public void ABareNumberFormatsWithoutASeparator()
        => Assert.Equal("4", new UnitFieldSpec().Format(4));

    [Fact]
    public void DecimalsAreHonouredAndPadded()
        => Assert.Equal("-2.0 pt", new UnitFieldSpec { Unit = "pt", Decimals = 1 }.Format(-2));

    // ---------------------------------------------------------------- parsing

    [Theory]
    [InlineData("500 ms", 500)]
    [InlineData("500ms", 500)]
    [InlineData("500", 500)]
    [InlineData("  620  ", 620)]
    [InlineData("2500", 2000)]   // clamped to Maximum
    [InlineData("-40", 0)]       // clamped to Minimum
    [InlineData("503", 503)]     // typing is explicit: clamped, but not snapped to the step grid
    public void WhatTheFieldPrintsIsWhatItCanReadBack(string text, double expected)
    {
        Assert.True(MillisecondSpec.TryParse(text, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void TypingIsNotSnappedToTheStepGridButDraggingIs()
    {
        // The step grid is a gesture affordance, not a constraint on the value. Snapping typed
        // input would make fine-drag results impossible to re-enter by keyboard.
        var spec = MillisecondSpec with { UnitsPerPixel = 1 };
        Assert.True(spec.TryParse("503", out var typed));
        Assert.Equal(503, typed);
        Assert.Equal(510, spec.Scrub(500, 7));
    }

    [Fact]
    public void AFineDragReachesValuesBetweenTheCoarseStops()
    {
        // Regression: fine gestures were being re-snapped onto the coarse grid, which silently
        // defeated fine mode entirely.
        var spec = MillisecondSpec;
        Assert.Equal(501, spec.Nudge(500, 1, fine: true));
        Assert.Equal(502, spec.Scrub(500, 4, fine: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("ms")]
    [InlineData("1,2,3")]
    public void GarbageIsRejectedRatherThanGuessedAt(string text)
        => Assert.False(MillisecondSpec.TryParse(text, out _));

    [Fact]
    public void ParsingIsCultureInvariantSoADecimalCommaCannotSilentlyChangeTheValue()
    {
        var spec = new UnitFieldSpec { Unit = "pt", Decimals = 1, Step = 0.1, Minimum = 0, Maximum = 100 };
        Assert.True(spec.TryParse("2.5 pt", out var dot));
        Assert.Equal(2.5, dot, 3);
        Assert.False(spec.TryParse("2,5 pt", out _));
    }

    // ---------------------------------------------------------------- scrub math

    [Fact]
    public void A300PixelDragOnTheMillisecondFieldLandsOnAPredictedValue()
    {
        var spec = MillisecondSpec;

        // 5 units/px * 300 px = +1500 from 500 => 2000, which is exactly Maximum.
        Assert.Equal(2000, spec.Scrub(start: 500, deltaPixels: 300));

        // The same drag leftwards runs into Minimum.
        Assert.Equal(0, spec.Scrub(start: 500, deltaPixels: -300));

        // A drag that does not saturate is plain arithmetic: 500 + 5*20 = 600.
        Assert.Equal(600, spec.Scrub(start: 500, deltaPixels: 20));
    }

    [Fact]
    public void FineDraggingMovesATenthAsFar()
    {
        var spec = MillisecondSpec;
        Assert.Equal(600, spec.Scrub(500, 20));
        Assert.Equal(510, spec.Scrub(500, 20, fine: true));
    }

    [Fact]
    public void ScrubbingSnapsToTheStepGridSoADragCannotProduceAnUntypeableValue()
    {
        var spec = MillisecondSpec with { UnitsPerPixel = 1 };
        var value = spec.Scrub(500, 7);        // 507 raw
        Assert.Equal(510, value);              // snapped to the 10 grid
        Assert.True(spec.TryParse(spec.Format(value), out var reparsed));
        Assert.Equal(value, reparsed);
    }

    [Fact]
    public void TheGridIsMeasuredFromTheMinimumNotFromZero()
    {
        var spec = new UnitFieldSpec { Minimum = 5, Maximum = 105, Step = 10 };
        Assert.Equal(15, spec.Quantize(14));
        Assert.Equal(15, spec.Quantize(16));
        Assert.Equal(5, spec.Quantize(6));
    }

    [Fact]
    public void RightwardIsAlwaysIncreasingAndDragIsSymmetric()
    {
        var spec = PointSpec;
        Assert.Equal(68, spec.Scrub(48, 20));
        Assert.Equal(28, spec.Scrub(48, -20));
    }

    [Fact]
    public void KeyboardAndWheelNudgeByOneStepAndShareTheScrubClamping()
    {
        var spec = MillisecondSpec;
        Assert.Equal(510, spec.Nudge(500, 1));
        Assert.Equal(490, spec.Nudge(500, -1));
        Assert.Equal(2000, spec.Nudge(1995, 1));
        Assert.Equal(0, spec.Nudge(5, -1));
        Assert.Equal(501, spec.Nudge(500, 1, fine: true));
    }

    // ---------------------------------------------------------------- reset semantics

    [Fact]
    public void ResetOffersItselfOnlyWhenTheValueActuallyDiffersFromTheDefault()
    {
        var spec = MillisecondSpec;
        Assert.False(spec.DiffersFromDefault(500));
        Assert.True(spec.DiffersFromDefault(510));
    }

    [Fact]
    public void ADifferenceTooSmallToSeeDoesNotOfferAReset()
    {
        // Whole-number field: 48.4 displays as "48", the default. Offering a reset for a difference
        // the user cannot see is noise.
        var spec = new UnitFieldSpec { Unit = "pt", Decimals = 0, Default = 48 };
        Assert.False(spec.DiffersFromDefault(48.4));
        Assert.True(spec.DiffersFromDefault(48.6));

        // One decimal shown: now 48.4 is visibly different and does offer one.
        var precise = spec with { Decimals = 1 };
        Assert.True(precise.DiffersFromDefault(48.4));
    }

    // ---------------------------------------------------------------- colour round-trip

    [Theory]
    [InlineData("#3F7ACC")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#F29933")]
    public void AnOpaqueColourRoundTripsToTheIdenticalString(string hex)
    {
        var parsed = TextClipCommands.ParseHexColor(hex);
        Assert.NotNull(parsed);
        Assert.Equal(hex, TextClipCommands.ToHexColor(parsed));
    }

    [Theory]
    [InlineData("#3F7ACC80")]
    [InlineData("#00000000")]
    public void AnAlphaColourKeepsItsAlphaChannelThroughTheRoundTrip(string hex)
    {
        var parsed = TextClipCommands.ParseHexColor(hex);
        Assert.NotNull(parsed);
        Assert.Equal(hex, TextClipCommands.ToHexColor(parsed));
    }

    [Fact]
    public void FullyOpaqueEncodesAsSixDigitsSoUnchangedProjectJsonStaysByteIdentical()
    {
        var opaque = TextClipCommands.ParseHexColor("#3F7ACCFF");
        Assert.Equal("#3F7ACC", TextClipCommands.ToHexColor(opaque));
    }

    [Fact]
    public void ShorthandExpandsTheWayTheParserAlreadyDefines()
    {
        var parsed = TextClipCommands.ParseHexColor("#F93");
        Assert.Equal("#FF9933", TextClipCommands.ToHexColor(parsed));
    }

    [Fact]
    public void TheFormatterMatchesTheDefaultHighlightTheModelsAlreadyStore()
    {
        // TextAnimation.DefaultHighlight is Mac's yellow; it must survive a well round-trip.
        var yellow = TextAnimation.DefaultHighlight;
        var hex = TextClipCommands.ToHexColor(yellow);
        var back = TextClipCommands.ParseHexColor(hex);
        Assert.NotNull(back);
        Assert.Equal(hex, TextClipCommands.ToHexColor(back));
    }

    [Fact]
    public void ANullColourFormatsToEmptyRatherThanThrowing()
        => Assert.Equal("", TextClipCommands.ToHexColor(null));

    [Fact]
    public void ChannelsOutsideTheUnitRangeAreClampedNotWrapped()
    {
        var wild = new TextStyle.RGBA { R = 1.4, G = -0.2, B = 0.5, A = 1 };
        Assert.Equal("#FF0080", TextClipCommands.ToHexColor(wild));
    }

    // ---------------------------------------------------------------- section expand state

    [Fact]
    public void AnUntouchedSectionUsesTheFallbackAndRemembersNothing()
    {
        SectionExpandState.Reset();
        Assert.True(SectionExpandState.IsExpanded("captions.style", fallback: true));
        Assert.False(SectionExpandState.IsExpanded("captions.style", fallback: false));
    }

    [Fact]
    public void ExpandStateIsRememberedPerSectionId()
    {
        SectionExpandState.Reset();
        SectionExpandState.SetExpanded("captions.style", false);
        SectionExpandState.SetExpanded("captions.outline", true);

        Assert.False(SectionExpandState.IsExpanded("captions.style"));
        Assert.True(SectionExpandState.IsExpanded("captions.outline"));
        Assert.True(SectionExpandState.IsExpanded("captions.shadow", fallback: true));
    }

    [Fact]
    public void ASectionWithNoIdIsNeverRememberedSoItCannotCollideWithANamedOne()
    {
        SectionExpandState.Reset();
        SectionExpandState.SetExpanded("", false);
        SectionExpandState.SetExpanded(null, false);
        Assert.True(SectionExpandState.IsExpanded("", fallback: true));
        Assert.True(SectionExpandState.IsExpanded(null, fallback: true));
    }

    [Fact]
    public void AHostCanSwapInADurableStore()
    {
        SectionExpandState.Reset();
        var backing = new Dictionary<string, bool>();
        SectionExpandState.Store = new DictionaryStore(backing);
        try
        {
            SectionExpandState.SetExpanded("captions.source", false);
            Assert.False(SectionExpandState.IsExpanded("captions.source"));
            Assert.False(backing["captions.source"]);
        }
        finally
        {
            SectionExpandState.Reset();
        }
    }

    private sealed class DictionaryStore(Dictionary<string, bool> values) : SectionExpandState.IStore
    {
        public bool? Get(string sectionId) => values.TryGetValue(sectionId, out var v) ? v : null;

        public void Set(string sectionId, bool expanded) => values[sectionId] = expanded;
    }
}
