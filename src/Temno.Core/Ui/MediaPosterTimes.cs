namespace Temno.Core.Ui;

/// <summary>
/// Which source frame stands for a video in the media library (WP-144 N6). The library shows the
/// mid-point and, only when that frame is a black slate, the first of ¾ then ¼ that is not. The
/// generation sources list reads the same cached frame, so both show the same picture.
/// </summary>
public static class MediaPosterTimes
{
    /// <summary>
    /// Poster offset: clip mid-point, or 0 for stills / unknown duration.
    /// Old rule was <c>Math.Min(1.0, Duration / 2)</c> — every clip over two seconds was
    /// sampled at exactly 1.0 s, inside the fade-in of most footage.
    /// </summary>
    public static double Primary(double duration) => duration > 0 ? duration / 2 : 0;

    /// <summary>The frames tried, in order, when the frame at <paramref name="used"/> is black.</summary>
    public static IEnumerable<double> Fallbacks(double duration, double used)
    {
        var last = Math.Max(0, duration - 0.04);
        foreach (var raw in new[] { duration * 0.75, duration * 0.25 })
        {
            var clamped = Math.Clamp(raw, 0, last);
            if (Math.Abs(clamped - used) > 0.05)
                yield return clamped;
        }
    }
}
