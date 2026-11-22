using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-171: while the transport plays, a filmstrip cell with nothing cached must fall back to the
/// nearest already-cached cell for the same media (stale but continuous) instead of drawing
/// nothing (flat track-colour). The rule is pure so the fallback choice is pinned here; the
/// no-new-ffmpeg half is structural (the control only reads its cache on this path) and is
/// covered by the playback-trace strip counts in docs/verification/wp-171/sept10.
/// </summary>
public class StripCellFallbackTests
{
    [Fact]
    public void NoCachedCells_ReturnsNull()
    {
        Assert.Null(StripCellFallback.Nearest(12.0, []));
    }

    [Fact]
    public void ExactHit_ReturnsItself()
    {
        Assert.Equal(8.0, StripCellFallback.Nearest(8.0, [2.0, 8.0, 14.0]));
    }

    [Fact]
    public void PicksNearestEarlier()
    {
        Assert.Equal(7.5, StripCellFallback.Nearest(9.0, [2.0, 7.5, 20.0]));
    }

    [Fact]
    public void PicksNearestLater()
    {
        Assert.Equal(12.0, StripCellFallback.Nearest(9.0, [2.0, 12.0, 20.0]));
    }

    [Fact]
    public void Tie_GoesEarlier()
    {
        Assert.Equal(3.0, StripCellFallback.Nearest(5.0, [3.0, 7.0]));
    }

    [Fact]
    public void Tie_GoesEarlierRegardlessOfOrder()
    {
        Assert.Equal(3.0, StripCellFallback.Nearest(5.0, [7.0, 3.0]));
    }

    [Fact]
    public void SingleCachedCell_AlwaysWins()
    {
        Assert.Equal(40.0, StripCellFallback.Nearest(1.0, [40.0]));
    }

    [Fact]
    public void ColdStartBeforeFirstSample_FallsBackToFirstSample()
    {
        Assert.Equal(4.0, StripCellFallback.Nearest(0.0, [4.0, 9.0]));
    }
}
