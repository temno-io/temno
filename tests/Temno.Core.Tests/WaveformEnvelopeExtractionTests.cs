using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-213: streaming envelope extraction. Long sources must generate real waveforms with bounded
/// per-call PCM working memory (no 180-second cutoff, no full-file buffer), and cancellation /
/// failures must surface instead of settling as false completions.
/// </summary>
public sealed class WaveformEnvelopeExtractionTests
{
    /// <summary>Synthesizes audio on demand so "long media" tests never materialize full PCM.</summary>
    private sealed class ScriptedAudioSource(
        double durationSeconds,
        Func<double, float>? sampleAt = null) : IAudioSource
    {
        public const int Rate = WaveformExtractor.SampleRate;

        public int CallCount;
        public double MaxRequestedSeconds;
        private readonly double _duration = durationSeconds;

        public Task<AudioBuffer> ReadAudioAsync(
            string path, int sampleRate, int channelCount,
            double startSeconds = 0, double? durationSeconds = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            MaxRequestedSeconds = Math.Max(MaxRequestedSeconds, durationSeconds ?? double.MaxValue);

            var end = Math.Min(_duration, startSeconds + (durationSeconds ?? (_duration - startSeconds)));
            var frames = Math.Max(0, (int)((end - startSeconds) * sampleRate));
            var samples = new float[frames * channelCount];
            for (var f = 0; f < frames; f++)
            {
                var t = startSeconds + (double)f / sampleRate;
                var v = sampleAt?.Invoke(t) ?? 0f;
                for (var c = 0; c < channelCount; c++)
                    samples[f * channelCount + c] = v;
            }
            return Task.FromResult(new AudioBuffer(samples, sampleRate, channelCount));
        }
    }

    private static string TouchTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "wp213-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    [Fact]
    public async Task LongSource_GeneratesFullEnvelope_WithBoundedChunks()
    {
        // 300 s (past the old 180 s cutoff): 10 chunks of 30 s, spike at 250.5 s preserved.
        var source = new ScriptedAudioSource(
            durationSeconds: 300,
            sampleAt: t => Math.Abs(t - 250.5) < 0.001 ? 0.95f : 0f);
        var path = TouchTempFile();
        try
        {
            var envelope = await new WaveformExtractor(source).ExtractEnvelopeAsync(path);
            Assert.Equal(200, envelope.PeaksPerSecond);
            Assert.Equal(300 * 200, envelope.Peaks.Length);
            Assert.Equal(300.0, envelope.DurationSeconds, 6);
            Assert.Equal(0.95f, envelope.Peaks[(int)(250.5 * 200)], 2);
            Assert.Equal(0f, envelope.Peaks[0]);
            Assert.Equal(0f, envelope.Peaks[^1]);
            // Ten full chunks plus at most one trailing empty probe at end of stream.
            Assert.InRange(source.CallCount, 10, 11);
            Assert.True(source.MaxRequestedSeconds <= WaveformExtractor.EnvelopeChunkSeconds + 0.001);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PastOldFourHourBoundary_DecodesToEndOfStream()
    {
        // The first WP-213 candidate truncated at 4 h; this repo has exercised 8.09 h
        // sources. A spike just past the old boundary must survive to its true bucket.
        const double duration = 4 * 3600 + 20;
        const double spikeAt = 4 * 3600 + 10;
        var source = new ScriptedAudioSource(
            durationSeconds: duration,
            sampleAt: t => Math.Abs(t - spikeAt) < 0.001 ? 0.9f : 0f);
        var path = TouchTempFile();
        try
        {
            var envelope = await new WaveformExtractor(source).ExtractEnvelopeAsync(path);
            Assert.Equal(duration * 200, envelope.Peaks.Length);
            Assert.Equal(duration, envelope.DurationSeconds, 6);
            Assert.Equal(0.9f, envelope.Peaks[(int)(spikeAt * 200)], 2);
            Assert.True(source.MaxRequestedSeconds <= WaveformExtractor.EnvelopeChunkSeconds + 0.001);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ChunkBoundary_TransientAt30sSurvivesCarryOver()
    {
        // A spike exactly on a chunk edge must land in the envelope once, at the right bucket.
        var source = new ScriptedAudioSource(
            durationSeconds: 65,
            sampleAt: t => Math.Abs(t - 30.0) < 0.00006 ? 1.0f : 0f);
        var path = TouchTempFile();
        try
        {
            var envelope = await new WaveformExtractor(source).ExtractEnvelopeAsync(path);
            var hot = envelope.Peaks
                .Select((p, i) => (p, i))
                .Where(x => x.p > 0.5f)
                .Select(x => x.i)
                .ToArray();
            Assert.Single(hot);
            Assert.InRange(hot[0], 5999, 6001); // bucket 6000 ±1
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoudAndSilent_RegionsKeepPeakAndZero()
    {
        var source = new ScriptedAudioSource(
            durationSeconds: 40,
            sampleAt: t => t < 20 ? 0.5f : 0f);
        var path = TouchTempFile();
        try
        {
            var envelope = await new WaveformExtractor(source).ExtractEnvelopeAsync(path);
            Assert.Equal(40 * 200, envelope.Peaks.Length);
            Assert.All(envelope.Peaks.Take(20 * 200), p => Assert.Equal(0.5f, p, 3));
            Assert.All(envelope.Peaks.Skip(20 * 200), p => Assert.Equal(0f, p));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CancelledToken_ThrowsAndReportsNoCompletion()
    {
        var source = new ScriptedAudioSource(durationSeconds: 300, sampleAt: _ => 0.5f);
        var path = TouchTempFile();
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new WaveformExtractor(source).ExtractEnvelopeAsync(path, cancellationToken: cts.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CancelMidStream_AbortsWithoutEnvelope()
    {
        var inner = new ScriptedAudioSource(durationSeconds: 300, sampleAt: _ => 0.5f);
        var cts = new CancellationTokenSource();
        var cancelling = new CancellingAudioSource(inner, cts, cancelAfterCalls: 2);
        var path = TouchTempFile();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new WaveformExtractor(cancelling).ExtractEnvelopeAsync(path, cancellationToken: cts.Token));
            Assert.True(cancelling.CallCount <= 3);
        }
        finally
        {
            File.Delete(path);
            cts.Dispose();
        }
    }

    [Fact]
    public async Task NoAudioStream_PropagatesInvalidOperation()
    {
        var path = TouchTempFile();
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new WaveformExtractor(new ThrowingAudioSource())
                    .ExtractEnvelopeAsync(path));
            Assert.Contains("audio stream", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EmptyAudio_YieldsEmptyEnvelope()
    {
        var source = new ScriptedAudioSource(durationSeconds: 0);
        var path = TouchTempFile();
        try
        {
            var envelope = await new WaveformExtractor(source).ExtractEnvelopeAsync(path);
            Assert.Empty(envelope.Peaks);
            Assert.Equal(0.0, envelope.DurationSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingFile_ThrowsFileNotFound()
    {
        var source = new ScriptedAudioSource(durationSeconds: 10);
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => new WaveformExtractor(source).ExtractEnvelopeAsync(
                Path.Combine(Path.GetTempPath(), "wp213-does-not-exist-" + Guid.NewGuid().ToString("N"))));
        await Assert.ThrowsAsync<ArgumentException>(
            () => new WaveformExtractor(source).ExtractEnvelopeAsync(""));
    }

    private sealed class CancellingAudioSource(IAudioSource inner, CancellationTokenSource cts, int cancelAfterCalls)
        : IAudioSource
    {
        public int CallCount;

        public Task<AudioBuffer> ReadAudioAsync(
            string path, int sampleRate, int channelCount,
            double startSeconds = 0, double? durationSeconds = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount >= cancelAfterCalls)
                cts.Cancel();
            return inner.ReadAudioAsync(path, sampleRate, channelCount, startSeconds, durationSeconds, cancellationToken);
        }
    }

    private sealed class ThrowingAudioSource : IAudioSource
    {
        public Task<AudioBuffer> ReadAudioAsync(
            string path, int sampleRate, int channelCount,
            double startSeconds = 0, double? durationSeconds = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The media file does not contain an audio stream.");
    }
}
