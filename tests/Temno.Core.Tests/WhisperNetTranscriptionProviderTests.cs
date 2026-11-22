using Temno.Core.Transcription;
using Temno.Media;
using Temno.Media.Audio;
using Temno.Media.Transcription;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class WhisperNetTranscriptionProviderTests : IDisposable
{
    private readonly string? _prevFake;

    public WhisperNetTranscriptionProviderTests()
    {
        _prevFake = Environment.GetEnvironmentVariable("TEMNO_USE_FAKE_TRANSCRIPTION");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_TRANSCRIPTION", _prevFake);
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("auto", "en")]
    [InlineData("en-US", "en")]
    [InlineData("es_MX", "es")]
    [InlineData("FR", "fr")]
    public void ResolveLanguage_NormalizesBcp47(string? hint, string expected)
        => Assert.Equal(expected, WhisperNetTranscriptionProvider.ResolveLanguage(hint));

    [Fact]
    public async Task TranscribeAsync_NoAudio_ThrowsBeforeModelLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-whisper-silent-" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllText(path, "x");
        var modelTouched = false;
        try
        {
            var provider = new WhisperNetTranscriptionProvider(
                audio: new UnusedAudio(),
                probe: new StubProbe(hasAudio: false, durationSeconds: 1),
                ensureModel: _ =>
                {
                    modelTouched = true;
                    return Task.FromResult("unused.bin");
                });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranscribeAsync(path));
            Assert.Contains("audio stream", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(modelTouched);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task TranscribeAsync_UnpinnedModel_IsRefusedBeforeTheNativeLoader()
    {
        var media = Path.Combine(Path.GetTempPath(), "temno-whisper-media-" + Guid.NewGuid().ToString("N") + ".wav");
        var model = Path.Combine(Path.GetTempPath(), "temno-whisper-model-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(media, "x");
        File.WriteAllBytes(model, [0x6c, 0x6d, 0x67, 0x67, 0, 0, 0, 0]);
        try
        {
            var provider = new WhisperNetTranscriptionProvider(
                audio: new UnusedAudio(),
                probe: new StubProbe(hasAudio: true, durationSeconds: 1),
                ensureModel: _ => Task.FromResult(model));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranscribeAsync(media));
            Assert.IsType<InvalidDataException>(ex.InnerException);
            Assert.Contains("loads only the pinned model", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(media); } catch { }
            try { File.Delete(model); } catch { }
        }
    }

    [Fact]
    public async Task TranscribeAsync_MissingFile_Throws()
    {
        var provider = new WhisperNetTranscriptionProvider(
            audio: new UnusedAudio(),
            probe: new StubProbe(hasAudio: true, durationSeconds: 1),
            ensureModel: _ => Task.FromResult("unused.bin"));
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.TranscribeAsync(Path.Combine(Path.GetTempPath(), "no-such-temno-media.wav")));
    }

    [Fact]
    public void TranscriptionServices_FakeEnv_IsOptIn()
    {
        Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_TRANSCRIPTION", null);
        Assert.False(TranscriptionServices.UseFakeProvider);
        Assert.IsType<WhisperNetTranscriptionProvider>(TranscriptionServices.Resolve());

        Environment.SetEnvironmentVariable("TEMNO_USE_FAKE_TRANSCRIPTION", "true");
        Assert.True(TranscriptionServices.UseFakeProvider);
        Assert.IsType<FakeTranscriptionProvider>(TranscriptionServices.Resolve());
    }

    private sealed class StubProbe(bool hasAudio, double durationSeconds) : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(new MediaProbeResult(durationSeconds, null, null, null, hasAudio, false));
    }

    private sealed class UnusedAudio : IAudioSource
    {
        public Task<AudioBuffer> ReadAudioAsync(
            string path,
            int sampleRate,
            int channelCount,
            double startSeconds = 0,
            double? durationSeconds = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("audio should not be read");
    }
}
