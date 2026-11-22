using Temno.Core.Editing;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public sealed class TimelineRulerLayoutTests
{
    [Fact]
    public void Custom_ruler_keeps_track_positions_and_hit_testing_aligned()
    {
        var timeline = new Timeline
        {
            Tracks = [new Track { DisplayHeight = 80 }, new Track { DisplayHeight = 50 }],
        };
        var geometry = TimelineGeometry.FromTimeline(timeline, 2, headerWidth: 0, rulerHeight: 36);
        Assert.Equal(36, geometry.TrackYAt(0));
        Assert.Equal(116, geometry.TrackYAt(1));
        Assert.Equal(0, geometry.TrackAtY(37));
        Assert.Equal(1, geometry.TrackAtY(117));
        Assert.Equal(166, geometry.ContentHeight);
    }
}
