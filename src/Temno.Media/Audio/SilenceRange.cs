namespace Temno.Media.Audio;

/// <summary>
/// A silent (dead-air) span in SOURCE media seconds.
/// Mac: the non-speech runs SpeechMaskStore.buildDeadAirMask marks dead.
/// </summary>
public sealed record SilenceRange(double Start, double End)
{
    public double Duration => End - Start;
}
