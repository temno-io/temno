using Temno.Core.Editing;
using Xunit;

namespace Temno.Core.Tests;

public class RippleEngineTests
{
    [Fact]
    public void MergeRanges_Overlapping_Merges()
    {
        var merged = RippleEngine.MergeRanges(
        [
            new FrameRange(0, 10),
            new FrameRange(8, 20),
            new FrameRange(30, 40),
        ]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(new FrameRange(0, 20), merged[0]);
        Assert.Equal(new FrameRange(30, 40), merged[1]);
    }

    [Fact]
    public void ComputeRippleShifts_ClosesGapAfterRemoval()
    {
        // clips: A[0,10) B[10,20) C[20,30) — remove B → C should start at 10
        var clips = new List<(string Id, int StartFrame, int EndFrame)>
        {
            ("A", 0, 10),
            ("B", 10, 20),
            ("C", 20, 30),
        };
        var shifts = RippleEngine.ComputeRippleShifts(clips, new HashSet<string> { "B" });
        Assert.Single(shifts);
        Assert.Equal("C", shifts[0].ClipId);
        Assert.Equal(10, shifts[0].NewStartFrame);
    }

    [Fact]
    public void ComputeRipplePush_ShiftsLaterClips()
    {
        var clips = new List<(string Id, int StartFrame)>
        {
            ("A", 0),
            ("B", 10),
            ("C", 20),
        };
        var shifts = RippleEngine.ComputeRipplePush(clips, insertFrame: 10, pushAmount: 5);
        Assert.Equal(2, shifts.Count);
        Assert.Contains(shifts, s => s.ClipId == "B" && s.NewStartFrame == 15);
        Assert.Contains(shifts, s => s.ClipId == "C" && s.NewStartFrame == 25);
    }
}
