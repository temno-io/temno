using Temno.Core.Models;

namespace Temno.Media.Audio;

/// <summary>
/// One clip's decoded PCM ready for timeline mixing. Sample-frame 0 of
/// <see cref="Samples"/> aligns with timeline frame <c>Clip.StartFrame</c>;
/// the decoder is responsible for having already applied source trim and speed.
/// Samples must be interleaved at the mixer's sample rate and channel count.
/// </summary>
public sealed class AudioClipSegment
{
    /// <summary>Placement, volume, fades and volume keyframes (dB) come from the clip.</summary>
    public Clip Clip { get; }

    public float[] Samples { get; }

    public AudioClipSegment(Clip clip, float[] samples)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(samples);
        Clip = clip;
        Samples = samples;
    }
}
