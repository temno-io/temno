using Temno.Core.Models;

namespace Temno.Core.Ui;

/// <summary>
/// WP-188. Track header labels, numbered the way the Mac numbers them
/// (<c>EditorViewModel+Tracks.swift</c>, <c>timelineTrackDisplayLabel(at:)</c>): video tracks count
/// upward from the bottom of the video stack, so the top row carries the highest V number and V1 sits
/// directly above A1; audio tracks count downward from the top of the audio stack.
/// Only the label changes. Display order is already the Mac's (index 0 is the top row on both
/// platforms — the Mac timeline views are flipped), and track ids, file order and compositing order
/// are untouched.
/// </summary>
public static class TrackLabels
{
    public static string LabelFor(IReadOnlyList<ClipType> trackTypes, int index)
    {
        if (trackTypes is null || index < 0 || index >= trackTypes.Count)
            return "";

        var type = trackTypes[index];
        var n = 0;
        if (type == ClipType.audio)
        {
            for (var i = 0; i <= index; i++)
                if (trackTypes[i] == type) n++;
        }
        else
        {
            var end = Math.Max(index + 1, FirstAudioIndex(trackTypes));
            for (var i = index; i < end; i++)
                if (trackTypes[i] == type) n++;
        }
        return Prefix(type) + n;
    }

    /// <summary>Index of the first audio track, or the track count when there is none.</summary>
    public static int FirstAudioIndex(IReadOnlyList<ClipType> trackTypes)
    {
        for (var i = 0; i < trackTypes.Count; i++)
            if (trackTypes[i] == ClipType.audio) return i;
        return trackTypes.Count;
    }

    public static string Prefix(ClipType type) => type switch
    {
        ClipType.video or ClipType.sequence => "V",
        ClipType.audio => "A",
        ClipType.image => "I",
        ClipType.text => "T",
        ClipType.lottie => "L",
        _ => "V",
    };
}
