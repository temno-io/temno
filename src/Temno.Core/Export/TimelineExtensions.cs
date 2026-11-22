using System;
using System.Collections.Generic;
using System.Linq;
using Temno.Core.Models;

namespace Temno.Core.Export;

public static class TimelineExtensions
{
    public static List<Timeline> GetReachableTimelines(
        this Timeline timeline,
        Func<string, Timeline?> resolve,
        int maxDepth = int.MaxValue,
        Func<Timeline, bool>? include = null)
    {
        var found = new List<Timeline>();
        var seen = new HashSet<string> { timeline.Id };
        var queue = new List<(Timeline T, int Depth)> { (timeline, 0) };
        var i = 0;
        while (i < queue.Count)
        {
            var (t, depth) = queue[i];
            i++;
            if (depth >= maxDepth) continue;

            var clips = t.Tracks.SelectMany(track => track.Clips)
                .Where(clip => clip.SourceClipType == ClipType.sequence);

            foreach (var clip in clips)
            {
                if (seen.Add(clip.MediaRef))
                {
                    var child = resolve(clip.MediaRef);
                    if (child != null && (include == null || include(child)))
                    {
                        found.Add(child);
                        queue.Add((child, depth + 1));
                    }
                }
            }
        }
        return found;
    }
}
