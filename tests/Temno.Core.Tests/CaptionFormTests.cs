using Temno.Core.Captions;
using Temno.Core.IO;
using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

public class CaptionFormLogicTests
{
    [Fact]
    public void GapMs_500_IsTheSameGenerateInputAsTheOldHalfSecondSlider()
    {
        var fromMs = CaptionFormLogic.GapMsToSeconds(500);
        var fromSlider = CaptionSpecBuilder.ClampGapSeconds(0.5);
        Assert.Equal(fromSlider, fromMs);
        Assert.Equal(15, CaptionSpecBuilder.GapFrames(fromMs, fps: 30));
        Assert.Equal(15, CaptionSpecBuilder.GapFrames(fromSlider, fps: 30));
    }

    [Fact]
    public void GapMsRoundTripsTheDefaultAndTheBounds()
    {
        Assert.Equal(500, CaptionFormLogic.GapSecondsToMs(0.5));
        Assert.Equal(0, CaptionFormLogic.GapMsToSeconds(0));
        Assert.Equal(2, CaptionFormLogic.GapMsToSeconds(2000));
        Assert.Equal(2, CaptionFormLogic.GapMsToSeconds(9999));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0.4, null)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(50, 50)]
    public void ZeroMeansUnlimitedForWordAndCharacterCaps(double value, int? expected)
        => Assert.Equal(expected, CaptionFormLogic.UnlimitedOrCount(value));

    [Fact]
    public void PositionPixelsMatchTheMacReadoutOnA1080x1920Canvas()
    {
        Assert.Equal(540, CaptionFormLogic.PixelFromNormalized(0.5, 1080));
        Assert.Equal(1728, CaptionFormLogic.PixelFromNormalized(0.9, 1920));
        Assert.Equal(0.5, CaptionFormLogic.NormalizedFromPixel(540, 1080), 6);
        Assert.Equal(0.9, CaptionFormLogic.NormalizedFromPixel(1728, 1920), 6);
    }
}

[Collection("EnvRegistry")]
public sealed class PreferencesSectionStoreTests : IDisposable
{
    private readonly string _file;

    public PreferencesSectionStoreTests()
    {
        _file = Path.Combine(Path.GetTempPath(), "temno-sections-" + Guid.NewGuid().ToString("N") + ".json");
        UserPreferences.SetPathForTests(_file);
    }

    public void Dispose()
    {
        UserPreferences.SetPathForTests(null);
        try { File.Delete(_file); } catch { /* ignore */ }
    }

    [Fact]
    public void ExpandStateSurvivesAFreshStoreReadingTheSameFile()
    {
        var store = new PreferencesSectionStore();
        store.Set("captions.style", false);
        store.Set("captions.animation", true);

        var reloaded = new PreferencesSectionStore();
        Assert.False(reloaded.Get("captions.style"));
        Assert.True(reloaded.Get("captions.animation"));
        Assert.Null(reloaded.Get("captions.missing"));
    }

    [Fact]
    public void UserPreferencesRoundTripsEditorSections()
    {
        var prefs = new UserPreferences();
        prefs.EditorSections["captions.source"] = false;
        UserPreferences.Save(prefs);
        var loaded = UserPreferences.Load();
        Assert.False(loaded.EditorSections["captions.source"]);
    }
}
