namespace Temno.Core.Generation;

/// <summary>
/// Gaps for the post-checkout credit watch. They start at four seconds and double until a minute,
/// and together they cover ten minutes. While the push socket is open (WP-350) a tickle ends each wait
/// and the watch reads only every <see cref="PushGap"/> on its own.
/// </summary>
public static class CheckoutWatchSchedule
{
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    /// <summary>The safety poll while push is live.</summary>
    public static readonly TimeSpan PushGap = TimeSpan.FromSeconds(60);

    public static IReadOnlyList<TimeSpan> Gaps()
    {
        var gaps = new List<TimeSpan>();
        var elapsed = TimeSpan.Zero;
        var gap = TimeSpan.FromSeconds(4);
        var cap = TimeSpan.FromSeconds(60);
        while (elapsed < Budget)
        {
            var step = gap < cap ? gap : cap;
            var room = Budget - elapsed;
            if (step > room)
                step = room;
            if (step <= TimeSpan.Zero)
                break;
            gaps.Add(step);
            elapsed += step;
            var doubled = TimeSpan.FromTicks(gap.Ticks * 2);
            gap = doubled < cap ? doubled : cap;
        }

        return gaps;
    }
}
