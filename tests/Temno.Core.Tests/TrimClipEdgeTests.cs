using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public sealed class TrimClipEdgeTests
{
    [Fact]
    public void TrimLeft_ShortensAndAdvancesStart()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip
        {
            Id = "c1",
            StartFrame = 10,
            DurationFrames = 40,
            TrimStartFrame = 0,
            MediaType = ClipType.video,
        };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);

        var r = EditCommands.TrimClipEdge(session, "c1", leftEdge: true, edgeFrame: 20);
        Assert.True(r.Ok);
        var updated = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == "c1");
        Assert.Equal(20, updated.StartFrame);
        Assert.Equal(30, updated.DurationFrames);
        Assert.Equal(10, updated.TrimStartFrame);
    }

    [Fact]
    public void TrimRight_ShortensDuration()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip
        {
            Id = "c2",
            StartFrame = 0,
            DurationFrames = 50,
            MediaType = ClipType.video,
        };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);

        var r = EditCommands.TrimClipEdge(session, "c2", leftEdge: false, edgeFrame: 30);
        Assert.True(r.Ok);
        var updated = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == "c2");
        Assert.Equal(0, updated.StartFrame);
        Assert.Equal(30, updated.DurationFrames);
    }
}
