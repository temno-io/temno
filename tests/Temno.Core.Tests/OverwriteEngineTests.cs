using Temno.Core.Editing;
using Xunit;

namespace Temno.Core.Tests;

public class OverwriteEngineTests
{
    [Fact]
    public void FullyCovered_Removes()
    {
        var clips = new[]
        {
            new OverwriteEngine.ClipSpan("A", 0, 20, 0, 1.0),
        };
        var actions = OverwriteEngine.ComputeOverwrite(clips, 0, 20);
        Assert.Single(actions);
        Assert.IsType<OverwriteEngine.Action.Remove>(actions[0]);
    }

    [Fact]
    public void LeftOverlap_TrimsEnd()
    {
        var clips = new[]
        {
            new OverwriteEngine.ClipSpan("A", 0, 20, 0, 1.0),
        };
        var actions = OverwriteEngine.ComputeOverwrite(clips, 10, 30);
        var trim = Assert.IsType<OverwriteEngine.Action.TrimEnd>(actions[0]);
        Assert.Equal(10, trim.NewDuration);
    }

    [Fact]
    public void RightOverlap_TrimsStart()
    {
        var clips = new[]
        {
            new OverwriteEngine.ClipSpan("A", 10, 30, 0, 1.0),
        };
        var actions = OverwriteEngine.ComputeOverwrite(clips, 0, 20);
        var trim = Assert.IsType<OverwriteEngine.Action.TrimStart>(actions[0]);
        Assert.Equal(20, trim.NewStartFrame);
        Assert.Equal(10, trim.NewTrimStart);
        Assert.Equal(10, trim.NewDuration);
    }

    [Fact]
    public void MiddleOverlap_Splits()
    {
        var clips = new[]
        {
            new OverwriteEngine.ClipSpan("A", 0, 30, 0, 1.0),
        };
        var actions = OverwriteEngine.ComputeOverwrite(clips, 10, 20);
        var split = Assert.IsType<OverwriteEngine.Action.Split>(actions[0]);
        Assert.Equal(10, split.LeftDuration);
        Assert.Equal(20, split.RightStartFrame);
        Assert.Equal(10, split.RightDuration);
        // Mac: trimStart + round((regionEnd - clipStart) * speed) = 0 + 20
        Assert.Equal(20, split.RightTrimStart);
    }
}
