using Temno.Core.Editing;
using Xunit;

namespace Temno.Core.Tests;

public sealed class TimelineWheelTests
{
    [Fact]
    public void ControlWheelZooms()
        => Assert.Equal(TimelineWheelAction.Zoom, TimelineWheel.Resolve(control: true, shift: false, alt: false, horizontalWheel: false));

    [Fact]
    public void BareWheelScrollsVertically()
        => Assert.Equal(TimelineWheelAction.VerticalScroll, TimelineWheel.Resolve(false, false, false, false));

    [Fact]
    public void ShiftWheelPansTime()
        => Assert.Equal(TimelineWheelAction.HorizontalPan, TimelineWheel.Resolve(false, shift: true, alt: false, horizontalWheel: false));

    [Fact]
    public void AltWheelPansTime()
        => Assert.Equal(TimelineWheelAction.HorizontalPan, TimelineWheel.Resolve(false, false, alt: true, horizontalWheel: false));

    [Fact]
    public void TiltWheelPansTime()
        => Assert.Equal(TimelineWheelAction.HorizontalPan, TimelineWheel.Resolve(false, false, false, horizontalWheel: true));

    [Fact]
    public void ControlWinsOverShift()
        => Assert.Equal(TimelineWheelAction.Zoom, TimelineWheel.Resolve(control: true, shift: true, alt: false, horizontalWheel: false));

    [Fact]
    public void ZoomKeepsTheContentPointAtTheSameViewportX()
    {
        const double contentX = 100.5;
        const double oldPpf = 3.0;
        const double newPpf = oldPpf * TimelineWheel.WheelZoomFactor;
        const double offset = 40;

        var next = TimelineWheel.ZoomHorizontalOffset(contentX, oldPpf, newPpf, offset);
        var viewportBefore = contentX - offset;
        var viewportAfter = contentX * (newPpf / oldPpf) - next;
        Assert.Equal(viewportBefore, viewportAfter, 8);
    }

    [Fact]
    public void IntegerFrameSnapDoesNotKeepASubFrameCursorPut()
    {
        const double contentX = 100.5;
        const double oldPpf = 3.0;
        const double newPpf = oldPpf * TimelineWheel.WheelZoomFactor;
        const double offset = 40;

        var legacy = TimelineWheel.LegacyIntegerFrameOffset(contentX, oldPpf, newPpf, offset);
        var viewportBefore = contentX - offset;
        var viewportAfter = contentX * (newPpf / oldPpf) - legacy;
        Assert.NotEqual(viewportBefore, viewportAfter, 4);
    }

    [Fact]
    public void ZoomOutMirrorsZoomIn()
    {
        const double contentX = 800;
        const double oldPpf = 4;
        var zoomed = oldPpf * TimelineWheel.WheelZoomFactor;
        const double offset = 120;
        var inOffset = TimelineWheel.ZoomHorizontalOffset(contentX, oldPpf, zoomed, offset);
        var back = TimelineWheel.ZoomHorizontalOffset(
            contentX * (zoomed / oldPpf), zoomed, oldPpf, inOffset);
        Assert.Equal(offset, back, 8);
    }
}
