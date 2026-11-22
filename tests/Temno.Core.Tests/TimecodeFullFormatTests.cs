using Temno.Core.Editing;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-184. The transport prints HH:MM:SS:FF always; the older formatter keeps dropping the hours so
/// the ruler and every existing caller are unchanged.
/// </summary>
public sealed class TimecodeFullFormatTests
{
    [Theory]
    [InlineData(0, 30, "00:00:00:00")]
    [InlineData(10492, 30, "00:05:49:22")]
    [InlineData(108000, 30, "01:00:00:00")]
    [InlineData(3599, 60, "00:00:59:59")]
    public void Full_shape_always_carries_hours(int frame, int fps, string expected)
        => Assert.Equal(expected, TimelineGeometry.FormatTimecodeFull(frame, fps));

    [Fact]
    public void Existing_formatter_is_unchanged_below_an_hour()
        => Assert.Equal("05:49:22", TimelineGeometry.FormatTimecode(10492, 30));

    [Fact]
    public void Negative_frames_and_bad_fps_are_clamped_like_the_existing_formatter()
        => Assert.Equal("00:00:00:00", TimelineGeometry.FormatTimecodeFull(-5, 0));
}
