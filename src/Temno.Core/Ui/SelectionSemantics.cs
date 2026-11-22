using Temno.Core.Models;

namespace Temno.Core.Ui;

/// <summary>
/// WP-183. What a timeline selection means to the Inspector. A video clip selected together with
/// its linked audio clip is the most common selection in the editor and it is one logical clip,
/// not a batch: the Inspector used to call it "2 clips · batch", warn that edits apply to all, and
/// take timing, keyframe and effect editing away. Two or more independent clips are still a batch.
/// </summary>
public static class SelectionSemantics
{
    public enum Kind { None, Single, LinkedPair, Batch }

    /// <param name="PrimaryId">The clip the Inspector shows: the video half of a pair, the focused clip of a batch.</param>
    /// <param name="AudioPartnerId">The linked audio clip of a pair; null otherwise.</param>
    public sealed record Classification(Kind Kind, string? PrimaryId, string? AudioPartnerId, int Count);

    public static Classification Classify(
        IReadOnlyList<(string Id, ClipType Type, string? LinkGroupId)> selected,
        string? focusedId)
    {
        if (selected.Count == 0)
            return new Classification(Kind.None, null, null, 0);
        if (selected.Count == 1)
            return new Classification(Kind.Single, selected[0].Id, null, 1);

        if (selected.Count == 2
            && !string.IsNullOrEmpty(selected[0].LinkGroupId)
            && selected[0].LinkGroupId == selected[1].LinkGroupId)
        {
            var video = selected.FirstOrDefault(c => c.Type == ClipType.video);
            var audio = selected.FirstOrDefault(c => c.Type == ClipType.audio);
            if (video.Id is not null && audio.Id is not null)
                return new Classification(Kind.LinkedPair, video.Id, audio.Id, 2);
        }

        var primary = focusedId is not null && selected.Any(c => c.Id == focusedId) ? focusedId : selected[0].Id;
        return new Classification(Kind.Batch, primary, null, selected.Count);
    }
}
