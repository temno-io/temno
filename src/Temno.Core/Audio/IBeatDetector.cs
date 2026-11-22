namespace Temno.Core.Audio;

/// <summary>
/// Detects beat and downbeat onsets for a media file. Mac: Audio/Beats/BeatDetector.swift
/// (Core ML). Windows uses an FFmpeg + energy-flux path (EnergyFluxBeatDetector).
/// Returned times are SECONDS in source media.
/// </summary>
public interface IBeatDetector
{
    /// <param name="mediaPath">Absolute path to a media file with an audio stream.</param>
    Task<BeatAnalysis> DetectAsync(string mediaPath, CancellationToken cancellationToken = default);
}
