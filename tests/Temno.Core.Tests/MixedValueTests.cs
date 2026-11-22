using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-166. Mac <c>sharedClipValue</c>: one shared value, or mixed.</summary>
public sealed class MixedValueTests
{
    [Fact]
    public void Marker_IsEmDash()
        => Assert.Equal("\u2014", MixedValue.Marker);

    [Fact]
    public void Empty_IsNotShared()
    {
        Assert.False(MixedValue.TryShared(Array.Empty<int>(), out int _));
        Assert.False(MixedValue.TrySharedRounded([], 0, out _));
    }

    [Fact]
    public void OneValue_IsThatValue()
    {
        Assert.True(MixedValue.TryShared([50], out int n));
        Assert.Equal(50, n);
    }

    [Fact]
    public void EqualValues_AreShared()
    {
        Assert.True(MixedValue.TryShared(["normal", "normal"], out var mode));
        Assert.Equal("normal", mode);
    }

    [Fact]
    public void Disagreement_IsMixed()
    {
        Assert.False(MixedValue.TryShared([100.0, 50.0], out double _));
        Assert.False(MixedValue.TryShared(["normal", "multiply"], StringComparer.Ordinal, out string? _));
    }

    [Fact]
    public void RoundedDoubles_ThatPaintTheSame_AreShared()
    {
        Assert.True(MixedValue.TrySharedRounded([1.001, 1.004], decimals: 2, out var shared));
        Assert.Equal(1.00, shared);
    }

    [Fact]
    public void RoundedDoubles_ThatPaintDifferently_AreMixed()
    {
        Assert.False(MixedValue.TrySharedRounded([50.0, 51.0], decimals: 0, out _));
    }
}
