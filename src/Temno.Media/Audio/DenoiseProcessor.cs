namespace Temno.Media.Audio;

/// <summary>
/// Deterministic Windows denoise reference path. It estimates a broadband noise floor from the
/// lower active 10 ms RMS blocks, applies a block gain to that wet signal, and dry/wet blends it.
/// This is deliberately not a claim to run macOS's optional SpeechEnhancement model.
/// </summary>
public static class DenoiseProcessor
{
    private const float ActiveFloor = 0.0005f;
    // Above this level a lower-percentile block is program material, not a trustworthy noise profile.
    private const float MaximumProfileFloor = 0.04f;

    public static DenoiseProcessResult Apply(
        ReadOnlySpan<float> drySamples,
        int sampleRate,
        int channelCount,
        double strength)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channelCount <= 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (drySamples.Length % channelCount != 0)
            throw new ArgumentException("Interleaved samples do not match channel count.", nameof(drySamples));
        if (!double.IsFinite(strength) || strength < 0 || strength > 1)
            throw new ArgumentOutOfRangeException(nameof(strength), "Strength must be in [0, 1].");

        var output = drySamples.ToArray();
        if (output.Length == 0 || strength == 0)
            return new DenoiseProcessResult(output, Applied: false, "Strength is 0 or audio is empty.");

        var sampleFrames = output.Length / channelCount;
        var blockFrames = Math.Max(1, sampleRate / 100); // 10 ms profile blocks
        var blockCount = (sampleFrames + blockFrames - 1) / blockFrames;
        var rms = new float[blockCount];
        for (var block = 0; block < blockCount; block++)
        {
            var start = block * blockFrames;
            var end = Math.Min(start + blockFrames, sampleFrames);
            double sumSquares = 0;
            for (var frame = start; frame < end; frame++)
            {
                for (var channel = 0; channel < channelCount; channel++)
                {
                    var sample = output[frame * channelCount + channel];
                    sumSquares += sample * sample;
                }
            }

            rms[block] = (float)Math.Sqrt(sumSquares / Math.Max(1, (end - start) * channelCount));
        }

        var active = rms.Where(value => value >= ActiveFloor).OrderBy(value => value).ToArray();
        if (active.Length == 0)
            return new DenoiseProcessResult(output, Applied: true, "Silence profile; no audible samples changed.");

        var noiseFloor = active[(active.Length - 1) / 5];
        if (noiseFloor >= MaximumProfileFloor)
            return new DenoiseProcessResult(output, Applied: true, "No low-level noise profile; audio retained.");

        var wetGains = new float[blockCount];
        for (var block = 0; block < blockCount; block++)
        {
            var level = rms[block];
            wetGains[block] = level <= noiseFloor
                ? 0f
                : Math.Clamp((level - noiseFloor) / Math.Max(level, ActiveFloor), 0f, 1f);
        }

        var dryMix = 1f - (float)strength;
        for (var frame = 0; frame < sampleFrames; frame++)
        {
            var block = Math.Min(frame / blockFrames, wetGains.Length - 1);
            var gain = dryMix + (float)strength * wetGains[block];
            for (var channel = 0; channel < channelCount; channel++)
                output[frame * channelCount + channel] *= gain;
        }

        return new DenoiseProcessResult(output, Applied: true, $"Applied deterministic noise-floor reduction ({noiseFloor:0.####}).");
    }
}

public sealed record DenoiseProcessResult(float[] Samples, bool Applied, string Detail);
