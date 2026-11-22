using Temno.Core.Editing;
using Temno.Core.Models;

namespace Temno.Core.Session;

/// <summary>
/// WP-250's Inspector fade rows. The changing edge clamps to <c>[0, room]</c>; the opposite edge
/// is left alone. <see cref="Clip.ClampFadesToDuration"/> is not used here because it spends the
/// leftover room by shrinking the fade-out. The timeline's blocks go through
/// <see cref="TransitionCommands"/> (WP-252).
/// </summary>
public static class FadeCommands
{
    /// <summary>
    /// Sets one fade edge. The room is the clip's length less whatever the other edge's block
    /// uses (<see cref="TransitionBlocks.MaxFade"/>), so a fade never runs into a cut transition.
    /// </summary>
    public static void ApplyEdge(TransitionIndex index, Clip clip, FadeEdge edge, int frames)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(clip);
        var clamped = Math.Clamp(frames, 0, TransitionBlocks.MaxFade(index, clip, edge));
        if (edge == FadeEdge.In) clip.FadeInFrames = clamped;
        else clip.FadeOutFrames = clamped;
    }
}
