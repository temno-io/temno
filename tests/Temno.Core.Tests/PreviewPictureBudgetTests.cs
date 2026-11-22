using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

public sealed class PreviewPictureBudgetTests
{
    [Theory]
    [InlineData(1366, 768, 1, 640, 360)]
    [InlineData(1366, 768, 1.25, 560, 315)]
    [InlineData(1366, 768, 1.5, 480, 270)]
    [InlineData(1920, 1080, 1, 960, 540)]
    [InlineData(1920, 1080, 1.25, 800, 450)]
    [InlineData(1920, 1080, 1.5, 640, 360)]
    public void Default_shell_reserves_the_picture_minimum_at_each_physical_size_and_scale(
        double windowWidth, double windowHeight, double scale, double landscapeWidth, double pictureHeight)
    {
        // Native non-client borders: conservatively reserve 16 physical pixels on each axis.
        var width = (windowWidth - 16) / scale;
        var height = (windowHeight - 16) / scale;
        var available = width - 4 - (width >= 1500 ? 285 : 0);
        var (media, inspector, _) = ResponsiveShell.FitSideColumns(available,
            420, 300, 300, 220, PreviewPictureBudget.MinimumColumnWidth(height));
        var previewWidth = available - media - inspector - 10 - 6;
        var timeline = PreviewPictureBudget.TimelineHeight(height, previewWidth, 324, 80, 220, 100);
        var canvasHeight = height - 164 - timeline - (previewWidth < 480 ? 28 : 0);
        foreach (var aspect in new[] { 16d / 9, 9d / 16, 1 })
        {
            var actualHeight = Math.Min(canvasHeight, previewWidth / aspect) * scale;
            var requiredHeight = aspect > 1 ? landscapeWidth / aspect : pictureHeight;
            Assert.True(actualHeight >= requiredHeight - 1,
                $"{windowWidth}x{windowHeight} @{scale}: aspect={aspect}, picture height={actualHeight}, required={requiredHeight}");
        }
        Assert.True(timeline >= 100);
    }

    [Fact]
    public void Tall_to_short_to_tall_releases_height_and_restores_the_track_stack()
    {
        var tall = PreviewPictureBudget.TimelineHeight(1064, 966, 324, 80, 220, 100);
        var shortHeight = PreviewPictureBudget.TimelineHeight(752, 650, 324, 80, 220, 100);
        Assert.True(shortHeight < tall);
        Assert.Equal(tall, PreviewPictureBudget.TimelineHeight(1064, 966, 324, 80, 220, 100));
        Assert.InRange(tall, 100, 404);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Unmeasured_window_keeps_a_finite_default(double height)
        => Assert.Equal(220, PreviewPictureBudget.TimelineHeight(height, 640, 324, 80, 220, 100));
}
