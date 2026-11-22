using System;
using System.Collections.Generic;

namespace Temno.Core.Ui;

/// <summary>
/// WP-171: which already-cached filmstrip cell to show when the exact cell was never extracted.
/// While the transport plays, extraction requests are deferred (WP-163 step d) and playhead-follow
/// scroll slides the visible range onto cells with nothing cached; drawing nothing leaves the clip
/// body flat track-colour for the whole run. The fix draws the nearest already-cached cell for the
/// same media — stale but continuous — without spawning any ffmpeg work.
/// WinUI-free so <c>Temno.Core.Tests</c> can reach it.
/// </summary>
public static class StripCellFallback
{
    /// <summary>
    /// Nearest cached source second to <paramref name="wantedSeconds"/>, or null when nothing is
    /// cached for the media. Ties go to the earlier second, independent of input order, so the
    /// choice is deterministic no matter how the caller gathered the keys.
    /// </summary>
    public static double? Nearest(double wantedSeconds, IReadOnlyList<double> cachedSeconds)
    {
        if (cachedSeconds.Count == 0) return null;
        var best = cachedSeconds[0];
        var bestGap = Math.Abs(best - wantedSeconds);
        for (var i = 1; i < cachedSeconds.Count; i++)
        {
            var gap = Math.Abs(cachedSeconds[i] - wantedSeconds);
            if (gap < bestGap || (gap == bestGap && cachedSeconds[i] < best))
            {
                best = cachedSeconds[i];
                bestGap = gap;
            }
        }
        return best;
    }
}
