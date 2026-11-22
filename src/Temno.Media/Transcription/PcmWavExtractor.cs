using Temno.Core.Account;
using Temno.Core.IO;
using Temno.Core.Transcription;
using Temno.Media.Audio;

namespace Temno.Media.Transcription;

public sealed class PcmWavExtractor : IPcmWavPreparer
{
    private readonly FfmpegAudioSource _source;

    public PcmWavExtractor(FfmpegAudioSource? source = null)
    {
        _source = source ?? new FfmpegAudioSource();
    }

    public async Task<PreparedPcmWav> PrepareAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
        var buffer = await _source.ReadAudioAsync(
            mediaPath,
            Pcm16Wav.SampleRate,
            channelCount: 1,
            startSeconds: 0,
            durationSeconds: null,
            cancellationToken).ConfigureAwait(false);
        return Pcm16Wav.FromMonoFloats(buffer.Samples, buffer.SampleRate);
    }
}
