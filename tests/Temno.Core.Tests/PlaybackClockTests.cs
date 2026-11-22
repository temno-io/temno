using Temno.Core.Editing;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

public class PlaybackClockTests
{
    private sealed class FakeTimeSource : ITimeSource
    {
        public double ElapsedSeconds { get; set; }

        public void Advance(double seconds) => ElapsedSeconds += seconds;
    }

    private static (PlaybackClock Clock, FakeTimeSource Time) MakeClock(int fps = 30, int durationFrames = 300)
    {
        var time = new FakeTimeSource();
        return (new PlaybackClock(time, fps, durationFrames), time);
    }

    [Fact]
    public void Play_Advances_CurrentFrame_With_Elapsed_Time()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 300);
        clock.Play();
        Assert.True(clock.IsPlaying);
        Assert.Equal(0, clock.CurrentFrame);

        time.Advance(1.0);
        Assert.Equal(30, clock.CurrentFrame);

        time.Advance(0.5);
        Assert.Equal(45, clock.Tick());
    }

    [Fact]
    public void Pause_Freezes_Frame()
    {
        var (clock, time) = MakeClock();
        clock.Play();
        time.Advance(1.0);
        clock.Pause();

        Assert.False(clock.IsPlaying);
        Assert.Equal(30, clock.CurrentFrame);

        time.Advance(5.0);
        Assert.Equal(30, clock.CurrentFrame);
        Assert.Equal(30, clock.Tick());
    }

    [Fact]
    public void TogglePlay_Toggles_IsPlaying()
    {
        var (clock, _) = MakeClock();
        clock.TogglePlay();
        Assert.True(clock.IsPlaying);
        clock.TogglePlay();
        Assert.False(clock.IsPlaying);
    }

    [Fact]
    public void Seek_Clamps_To_Timeline_Bounds()
    {
        var (clock, _) = MakeClock(durationFrames: 100);

        clock.SeekToFrame(-5);
        Assert.Equal(0, clock.CurrentFrame);

        clock.SeekToFrame(400);
        Assert.Equal(100, clock.CurrentFrame);

        clock.SeekToFrame(42);
        Assert.Equal(42, clock.CurrentFrame);
    }

    [Fact]
    public void Seek_While_Playing_Continues_From_New_Frame()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 300);
        clock.Play();
        time.Advance(1.0);
        clock.SeekToFrame(100);
        time.Advance(1.0);
        Assert.Equal(130, clock.CurrentFrame);
        Assert.True(clock.IsPlaying);
    }

    [Fact]
    public void Tick_AutoPauses_At_End_And_Clamps()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 60);
        clock.Play();
        time.Advance(10.0); // raw frame 300, far past the 60-frame duration

        Assert.Equal(60, clock.Tick());
        Assert.False(clock.IsPlaying);
        Assert.Equal(60, clock.CurrentFrame);
    }

    [Fact]
    public void Play_At_End_Restarts_From_Zero()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 60);
        clock.SeekToFrame(60);
        clock.Play();

        Assert.Equal(0, clock.CurrentFrame);
        time.Advance(0.5);
        Assert.Equal(15, clock.CurrentFrame);
    }

    [Fact]
    public void Rate_2x_Doubles_Advance()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 300);
        clock.Rate = 2.0;
        clock.Play();
        time.Advance(1.0);
        Assert.Equal(60, clock.CurrentFrame);
    }

    [Fact]
    public void Rate_Change_MidPlay_Does_Not_Jump()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 300);
        clock.Play();
        time.Advance(1.0); // frame 30
        clock.Rate = -2.0; // JKL reverse shuttle
        Assert.Equal(30, clock.CurrentFrame);

        time.Advance(0.25); // -15 frames
        Assert.Equal(15, clock.CurrentFrame);
    }

    [Fact]
    public void Negative_Rate_AutoPauses_At_Zero()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 300);
        clock.SeekToFrame(30);
        clock.Rate = -1.0;
        clock.Play();
        time.Advance(5.0);

        Assert.Equal(0, clock.Tick());
        Assert.False(clock.IsPlaying);
    }

    [Fact]
    public void Play_Backward_From_Zero_Restarts_From_End()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 60);
        clock.Rate = -1.0;
        clock.Play();

        Assert.Equal(60, clock.CurrentFrame);
        time.Advance(1.0);
        Assert.Equal(30, clock.CurrentFrame);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_Rate_Throws(double rate)
    {
        var (clock, _) = MakeClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Rate = rate);
    }

    [Fact]
    public void LoopRange_Wraps_Forward()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 300);
        clock.LoopRange = new FrameRange(10, 20);
        clock.SeekToFrame(10);
        clock.Play();

        time.Advance(0.5); // raw frame 25 → wraps to 15
        Assert.Equal(15, clock.Tick());
        Assert.True(clock.IsPlaying);
    }

    [Fact]
    public void LoopRange_Wraps_Backward()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 300);
        clock.LoopRange = new FrameRange(10, 20);
        clock.SeekToFrame(15);
        clock.Rate = -1.0;
        clock.Play();

        time.Advance(8.0 / 30.0); // raw frame 7 → wraps to 17
        Assert.Equal(17, clock.Tick());
        Assert.True(clock.IsPlaying);
    }

    [Fact]
    public void Invalid_LoopRange_Throws()
    {
        var (clock, _) = MakeClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.LoopRange = new FrameRange(20, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.LoopRange = new FrameRange(-1, 10));
    }

    [Fact]
    public void FrameChanged_Raised_On_Tick_And_Seek()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 300);
        var frames = new List<int>();
        clock.FrameChanged += frames.Add;

        clock.SeekToFrame(10);
        clock.Play();
        time.Advance(1.0 / 30.0);
        clock.Tick();
        clock.Tick(); // same frame → no duplicate event

        Assert.Equal(new[] { 10, 11 }, frames);
    }

    [Fact]
    public void IsPlayingChanged_Raised_On_Transport_And_AutoPause()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 30);
        var states = new List<bool>();
        clock.IsPlayingChanged += states.Add;

        clock.Play();
        time.Advance(2.0);
        clock.Tick(); // auto-pause at end
        clock.Play();
        clock.Pause();

        Assert.Equal(new[] { true, false, true, false }, states);
    }

    [Fact]
    public void Stop_Resets_To_Zero_And_Pauses()
    {
        var (clock, time) = MakeClock();
        clock.Play();
        time.Advance(1.0);
        clock.Stop();

        Assert.False(clock.IsPlaying);
        Assert.Equal(0, clock.CurrentFrame);
    }

    [Fact]
    public void DurationFrames_Shrink_Clamps_Playhead()
    {
        var (clock, _) = MakeClock(durationFrames: 300);
        clock.SeekToFrame(200);
        clock.DurationFrames = 100;
        Assert.Equal(100, clock.CurrentFrame);
    }

    [Fact]
    public void CurrentFrame_Rounds_Half_Away_From_Zero()
    {
        var (clock, time) = MakeClock(fps: 30, durationFrames: 300);
        clock.Play();
        time.Advance(0.05); // raw 1.5 frames → 2, not banker's 1
        Assert.Equal(2, clock.CurrentFrame);
    }

    [Fact]
    public void Constructor_Validates_Arguments()
    {
        var time = new FakeTimeSource();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackClock(time, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackClock(time, 30, -1));
    }
}
