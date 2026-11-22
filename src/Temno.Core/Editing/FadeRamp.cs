using Temno.Core.Models;

namespace Temno.Core.Editing;

public enum FadeEdge
{
    In,
    Out,
}

/// <summary>
/// One sampled point on a fade ramp. <see cref="Multiplier"/> is
/// <see cref="Clip.FadeMultiplier"/> at <see cref="TimelineFrame"/>, so the drawn curve
/// is the curve the mixer and the picture renderer already use.
/// </summary>
public readonly record struct FadeRampSample(double T, int TimelineFrame, double Multiplier);

/// <summary>
/// WP-250, reduced by WP-252: the fade curve the timeline draws inside a fade block.
/// </summary>
public static class FadeRamp
{
    public const int SmoothSteps = 12;

    /// <summary>The ramp from silent (<c>T = 0</c>) to full (<c>T = 1</c>), sampled from the clip's own multiplier.</summary>
    public static IReadOnlyList<FadeRampSample> Samples(Clip clip, FadeEdge edge)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var length = edge == FadeEdge.In ? clip.FadeInFrames : clip.FadeOutFrames;
        if (length <= 0 || clip.DurationFrames <= 0) return [];

        var interpolation = edge == FadeEdge.In ? clip.FadeInInterpolation : clip.FadeOutInterpolation;
        var steps = interpolation == Interpolation.smooth ? Math.Min(SmoothSteps, length) : 1;
        if (steps < 1) steps = 1;

        var samples = new FadeRampSample[steps + 1];
        for (var s = 0; s <= steps; s++)
        {
            var t = (double)s / steps;
            var along = (int)Math.Round(t * length, MidpointRounding.AwayFromZero);
            var rel = edge == FadeEdge.In ? along : clip.DurationFrames - along;
            rel = Math.Clamp(rel, 0, clip.DurationFrames);
            var frame = clip.StartFrame + rel;
            samples[s] = new FadeRampSample(t, frame, clip.FadeMultiplier(frame));
        }
        return samples;
    }
}
