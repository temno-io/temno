namespace Temno.Media.Audio;

/// <summary>
/// User-facing timing policy for energy-based silence detection. Values are source seconds;
/// the Windows Audio tab displays them in milliseconds.
/// </summary>
public sealed record SilenceDetectionOptions
{
    public const double DefaultMinimumPauseSeconds = 0.5;
    public const double DefaultSpeechPaddingSeconds = 0.15;

    public double ThresholdDb { get; init; } = SilenceDetector.DefaultThresholdDb;

    /// <summary>Quiet runs shorter than this are preserved.</summary>
    public double MinimumPauseSeconds { get; init; } = DefaultMinimumPauseSeconds;

    /// <summary>Speech-side material retained at each interior edge of a detected quiet run.</summary>
    public double SpeechPaddingSeconds { get; init; } = DefaultSpeechPaddingSeconds;

    public void Validate()
    {
        if (!double.IsFinite(ThresholdDb))
            throw new ArgumentOutOfRangeException(nameof(ThresholdDb), "Threshold must be finite.");
        if (!double.IsFinite(MinimumPauseSeconds) || MinimumPauseSeconds < 0)
            throw new ArgumentOutOfRangeException(
                nameof(MinimumPauseSeconds), "Minimum pause must be finite and non-negative.");
        if (!double.IsFinite(SpeechPaddingSeconds) || SpeechPaddingSeconds < 0)
            throw new ArgumentOutOfRangeException(
                nameof(SpeechPaddingSeconds), "Speech padding must be finite and non-negative.");
    }
}
