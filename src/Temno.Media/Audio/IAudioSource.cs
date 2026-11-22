namespace Temno.Media.Audio;

/// <summary>
/// Decodes a media file's audio to float32 interleaved PCM at a caller-chosen
/// sample rate and channel count. Times are SOURCE media seconds.
/// Mac: Audio/AudioTrackReader.swift.
/// </summary>
public interface IAudioSource
{
    /// <summary>
    /// Reads audio starting at <paramref name="startSeconds"/> for
    /// <paramref name="durationSeconds"/> (null = to end of stream). Reading past
    /// the end of the stream returns fewer samples than requested, possibly zero.
    /// </summary>
    Task<AudioBuffer> ReadAudioAsync(
        string path,
        int sampleRate,
        int channelCount,
        double startSeconds = 0,
        double? durationSeconds = null,
        CancellationToken cancellationToken = default);
}
