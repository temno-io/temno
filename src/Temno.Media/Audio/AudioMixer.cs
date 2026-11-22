using Temno.Core.Models;

namespace Temno.Media.Audio;

/// <summary>
/// Pure timeline audio mixer (no ffmpeg). Mac: Preview/CompositionBuilder.swift
/// audio half + Models/Timeline.swift volume methods.
///
/// Semantics matched to the Mac engine:
/// - Effective gain at a timeline frame = Clip.VolumeAt(frame) =
///   volume (linear) × linearFromDb(volumeTrack sample, dB) × fadeMultiplier
///   (linear or smoothstep per edge, envelope = min(in, out)).
/// - The Mac emits piecewise-LINEAR gain ramps between envelope breakpoints
///   (AVAudioMix volume ramps are linear in gain). This mixer samples the gain
///   at every timeline frame boundary and lerps linearly across the samples of
///   each frame block — a strictly finer subdivision than the Mac's 8-way
///   smooth-segment split, exact at frame boundaries.
/// - Overlapping segments SUM linearly (float accumulation, as AVFoundation
///   does), then the mix is hard-clamped to [-1, 1] — matching the clipping
///   AVFoundation applies at the fixed-point output stage. No soft limiter.
/// - WP-165: that clamp belongs to the OUTPUT stage only. A nested sequence is
///   mixed once to build its submix and then gained again by its carrier clip,
///   so clamping the submix would flat-top it before the carrier's fader ever
///   ran — something no top-level clip suffers. Callers building an
///   intermediate bus pass clampOutput: false and let the final mix clamp.
/// - Callers must apply the per-track overlap rule (clips starting before the
///   previous clip's end on the SAME track are skipped) and exclude muted
///   tracks before building segments; segments given here always sum.
/// </summary>
public static class AudioMixer
{
    /// <summary>
    /// Absolute PCM sample-frame offset of a timeline frame: round(frame / fps × sampleRate),
    /// midpoint away from zero. Rounding absolute positions (not block widths) avoids drift
    /// when sampleRate is not divisible by fps.
    /// </summary>
    public static long SampleOffsetForFrame(int frame, int fps, int sampleRate)
    {
        if (fps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), "Fps must be positive.");
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        }

        return (long)Math.Round(frame * (double)sampleRate / fps, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Mixes all segments over the timeline frame range [startFrame, startFrame + frameCount).
    /// Returns interleaved float32 PCM of exactly
    /// (SampleOffsetForFrame(startFrame + frameCount) − SampleOffsetForFrame(startFrame)) × channelCount
    /// samples; ranges with no audible segment content are silence (zeros).
    /// </summary>
    /// <param name="clampOutput">
    /// False for an intermediate bus (a nested-sequence submix) whose result is gained again
    /// downstream. The final, audible mix always clamps.
    /// </param>
    public static float[] Mix(
        IReadOnlyList<AudioClipSegment> segments,
        int startFrame,
        int frameCount,
        int fps,
        int sampleRate,
        int channelCount,
        bool clampOutput = true)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount), "Channel count must be positive.");
        }

        if (frameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), "Frame count cannot be negative.");
        }

        var rangeStart = SampleOffsetForFrame(startFrame, fps, sampleRate);
        var rangeEnd = SampleOffsetForFrame(startFrame + frameCount, fps, sampleRate);
        var output = new float[(rangeEnd - rangeStart) * channelCount];
        if (output.Length == 0)
        {
            return output;
        }

        foreach (var segment in segments)
        {
            MixSegment(segment, output, rangeStart, rangeEnd, fps, sampleRate, channelCount);
        }

        if (clampOutput)
        {
            for (var i = 0; i < output.Length; i++)
            {
                output[i] = Math.Clamp(output[i], -1f, 1f);
            }
        }

        return output;
    }

    private static void MixSegment(
        AudioClipSegment segment,
        float[] output,
        long rangeStart,
        long rangeEnd,
        int fps,
        int sampleRate,
        int channelCount)
    {
        var clip = segment.Clip;
        if (clip.DurationFrames <= 0)
        {
            return;
        }

        var clipStart = SampleOffsetForFrame(clip.StartFrame, fps, sampleRate);
        var clipEnd = SampleOffsetForFrame(clip.EndFrame, fps, sampleRate);
        // A segment shorter than the clip's placement contributes silence past its data.
        var availableSampleFrames = segment.Samples.Length / channelCount;
        clipEnd = Math.Min(clipEnd, clipStart + availableSampleFrames);

        var mixStart = Math.Max(clipStart, rangeStart);
        var mixEnd = Math.Min(clipEnd, rangeEnd);
        if (mixEnd <= mixStart)
        {
            return;
        }

        // Advance to the timeline frame whose sample block contains mixStart.
        var frame = clip.StartFrame;
        var blockStart = clipStart;
        while (true)
        {
            var next = SampleOffsetForFrame(frame + 1, fps, sampleRate);
            if (next > mixStart)
            {
                break;
            }

            frame++;
            blockStart = next;
        }

        while (blockStart < mixEnd)
        {
            var blockEnd = SampleOffsetForFrame(frame + 1, fps, sampleRate);
            // frame + 1 tops out at clip.EndFrame where VolumeAt still yields the
            // ramp target (0 for a fade-out) — the exact endpoint of the Mac envelope.
            var gain0 = clip.VolumeAt(frame);
            var gain1 = clip.VolumeAt(frame + 1);
            var blockLength = (double)(blockEnd - blockStart);

            var first = Math.Max(blockStart, mixStart);
            var last = Math.Min(blockEnd, mixEnd);
            for (var s = first; s < last; s++)
            {
                var t = blockLength > 0 ? (s - blockStart) / blockLength : 0.0;
                var gain = (float)(gain0 + (gain1 - gain0) * t);
                var srcBase = (s - clipStart) * channelCount;
                var dstBase = (s - rangeStart) * channelCount;
                for (var ch = 0; ch < channelCount; ch++)
                {
                    output[dstBase + ch] += segment.Samples[srcBase + ch] * gain;
                }
            }

            frame++;
            blockStart = blockEnd;
        }
    }
}
