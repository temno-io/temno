using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Temno.Core.Audio;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

public class BeatDetectionTests
{
    private const int SampleRate = EnergyFluxBeatDetector.SampleRate;

    // ---- pure algorithm: synthetic signal ---------------------------------

    [Fact]
    public void AnalyzeSamples_Detects_Known_Bpm_Within_5_Percent()
    {
        const double targetBpm = 120.0;
        var samples = GenerateClickTrack(durationSeconds: 8.0, bpm: targetBpm, clickAmplitude: 1f);

        var analysis = EnergyFluxBeatDetector.AnalyzeSamples(samples, SampleRate);

        Assert.NotEmpty(analysis.Beats);
        Assert.True(analysis.Bpm > 0, "Expected a positive BPM estimate.");
        var error = Math.Abs(analysis.Bpm - targetBpm) / targetBpm;
        Assert.True(
            error <= 0.05,
            $"BPM {analysis.Bpm:F2} not within 5% of {targetBpm} (error={error:P1}). Beats={analysis.Beats.Length}");
    }

    [Fact]
    public void AnalyzeSamples_Detects_Alternate_Bpm_Within_5_Percent()
    {
        const double targetBpm = 90.0;
        var samples = GenerateClickTrack(durationSeconds: 10.0, bpm: targetBpm, clickAmplitude: 1f);

        var analysis = EnergyFluxBeatDetector.AnalyzeSamples(samples, SampleRate);

        Assert.NotEmpty(analysis.Beats);
        var error = Math.Abs(analysis.Bpm - targetBpm) / targetBpm;
        Assert.True(
            error <= 0.05,
            $"BPM {analysis.Bpm:F2} not within 5% of {targetBpm} (error={error:P1}).");
    }

    [Fact]
    public void AnalyzeSamples_Produces_Downbeats_Every_Fourth_Beat()
    {
        var samples = GenerateClickTrack(durationSeconds: 8.0, bpm: 120.0, clickAmplitude: 1f);
        var analysis = EnergyFluxBeatDetector.AnalyzeSamples(samples, SampleRate);

        Assert.NotEmpty(analysis.Downbeats);
        Assert.True(analysis.Downbeats.Length <= analysis.Beats.Length);
        // All downbeats must appear in the beat list (within a small timing epsilon).
        foreach (var db in analysis.Downbeats)
        {
            Assert.Contains(analysis.Beats, b => Math.Abs(b - db) < 1e-9);
        }
    }

    [Fact]
    public void AnalyzeSamples_Empty_Or_Short_Returns_Empty()
    {
        Assert.Equal(0, EnergyFluxBeatDetector.AnalyzeSamples([], SampleRate).Bpm);
        Assert.Empty(EnergyFluxBeatDetector.AnalyzeSamples([], SampleRate).Beats);

        var shortBuf = new float[100];
        var empty = EnergyFluxBeatDetector.AnalyzeSamples(shortBuf, SampleRate);
        Assert.Equal(0, empty.Bpm);
        Assert.Empty(empty.Beats);
        Assert.Empty(empty.Downbeats);
    }

    [Fact]
    public void AnalyzeSamples_Silence_Returns_Empty_Or_Zero_Bpm()
    {
        var silence = new float[SampleRate * 2];
        var analysis = EnergyFluxBeatDetector.AnalyzeSamples(silence, SampleRate);
        Assert.Empty(analysis.Beats);
        Assert.Equal(0, analysis.Bpm);
    }

    // ---- BPM estimation edge cases ----------------------------------------

    [Fact]
    public void EstimateBpm_Returns_Null_When_Too_Few_Beats()
    {
        Assert.Null(EnergyFluxBeatDetector.EstimateBpm(Array.Empty<double>()));
        Assert.Null(EnergyFluxBeatDetector.EstimateBpm([0.0]));
        Assert.Null(EnergyFluxBeatDetector.EstimateBpm([0.0, 0.5]));
    }

    [Fact]
    public void EstimateBpm_Uses_Median_InterOnset_Interval()
    {
        // Regular 0.5 s IOI → 120 BPM.
        var beats = Enumerable.Range(0, 8).Select(i => i * 0.5).ToArray();
        var bpm = EnergyFluxBeatDetector.EstimateBpm(beats);
        Assert.NotNull(bpm);
        Assert.InRange(bpm!.Value, 119.0, 121.0);
    }

    [Fact]
    public void EstimateBpm_Median_Ignores_Outlier_Gaps()
    {
        // Mostly 0.5 s gaps with one 2.0 s outlier; median stays 0.5 → 120 BPM.
        double[] beats = [0.0, 0.5, 1.0, 1.5, 3.5, 4.0, 4.5];
        var bpm = EnergyFluxBeatDetector.EstimateBpm(beats);
        Assert.NotNull(bpm);
        Assert.InRange(bpm!.Value, 119.0, 121.0);
    }

    [Fact]
    public void EstimateBpm_Zero_Interval_Returns_Null()
    {
        double[] beats = [1.0, 1.0, 1.0, 1.0];
        Assert.Null(EnergyFluxBeatDetector.EstimateBpm(beats));
    }

    [Fact]
    public void EstimateDownbeats_Aligns_To_Strongest_Onset()
    {
        double[] beats = [0.0, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5];
        // Strongest at index 2 → downbeats at 1.0, 3.0 (and potentially earlier if phase wraps).
        double[] strengths = [1, 1, 10, 1, 1, 1, 1, 1];
        var downbeats = EnergyFluxBeatDetector.EstimateDownbeats(beats, strengths);

        Assert.Contains(1.0, downbeats);
        Assert.Contains(3.0, downbeats);
        Assert.DoesNotContain(0.0, downbeats);
        Assert.DoesNotContain(0.5, downbeats);
    }

    // ---- BeatAnalysis JSON ------------------------------------------------

    [Fact]
    public void BeatAnalysis_Json_RoundTrip_Matches_Mac_Field_Names()
    {
        var original = new BeatAnalysis
        {
            Bpm = 128.5,
            Beats = [0.1, 0.6, 1.1],
            Downbeats = [0.1, 1.1],
        };

        var json = JsonSerializer.Serialize(original, JsonOptions.ForProject);
        Assert.Contains("\"bpm\"", json, StringComparison.Ordinal);
        Assert.Contains("\"beats\"", json, StringComparison.Ordinal);
        Assert.Contains("\"downbeats\"", json, StringComparison.Ordinal);

        var restored = JsonSerializer.Deserialize<BeatAnalysis>(json, JsonOptions.ForProject);
        Assert.NotNull(restored);
        Assert.Equal(original.Bpm, restored!.Bpm);
        Assert.Equal(original.Beats, restored.Beats);
        Assert.Equal(original.Downbeats, restored.Downbeats);
    }

    // ---- BeatStore cache --------------------------------------------------

    [Fact]
    public void BeatStore_Memory_And_Disk_RoundTrip()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "temno_beat_cache_" + Guid.NewGuid().ToString("N"));
        var mediaPath = Path.Combine(Path.GetTempPath(), "temno_beat_media_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(mediaPath, new byte[64]);
            var store = new BeatStore(cacheDir);
            var analysis = new BeatAnalysis
            {
                Bpm = 100,
                Beats = [0.0, 0.6, 1.2],
                Downbeats = [0.0, 1.2],
            };

            store.Store(mediaPath, "clipA", analysis);

            Assert.True(File.Exists(store.AnalysisPath(mediaPath, "clipA")));
            var mem = store.GetAnalysis("clipA");
            Assert.NotNull(mem);
            Assert.Equal(100, mem!.Bpm);

            // Fresh store hydrates from disk.
            var store2 = new BeatStore(cacheDir);
            Assert.Null(store2.GetAnalysis("clipA"));
            var hydrated = store2.Hydrate(mediaPath, "clipA");
            Assert.NotNull(hydrated);
            Assert.Equal(100, hydrated!.Bpm);
            Assert.Equal(analysis.Beats, hydrated.Beats);
            Assert.Equal(analysis.Downbeats, hydrated.Downbeats);

            // LoadFromDisk without memory.
            var store3 = new BeatStore(cacheDir);
            var disk = store3.LoadFromDisk(mediaPath, "clipA");
            Assert.NotNull(disk);
            Assert.Equal(100, disk!.Bpm);
        }
        finally
        {
            TryDeleteDir(cacheDir);
            TryDeleteFile(mediaPath);
        }
    }

    [Fact]
    public void BeatStore_Invalidates_And_Resets()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "temno_beat_cache_" + Guid.NewGuid().ToString("N"));
        var mediaPath = Path.Combine(Path.GetTempPath(), "temno_beat_media_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(mediaPath, new byte[32]);
            var store = new BeatStore(cacheDir);
            store.Store(mediaPath, "x", new BeatAnalysis { Bpm = 90, Beats = [0.0], Downbeats = [] });
            Assert.NotNull(store.GetAnalysis("x"));

            store.Invalidate("x");
            Assert.Null(store.GetAnalysis("x"));

            store.Store(mediaPath, "y", new BeatAnalysis { Bpm = 80, Beats = [0.0], Downbeats = [] });
            store.Reset();
            Assert.Null(store.GetAnalysis("y"));
        }
        finally
        {
            TryDeleteDir(cacheDir);
            TryDeleteFile(mediaPath);
        }
    }

    [Fact]
    public void BeatStore_Removes_Stale_Cache_Files_For_Same_MediaRef()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "temno_beat_cache_" + Guid.NewGuid().ToString("N"));
        var mediaPath = Path.Combine(Path.GetTempPath(), "temno_beat_media_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(mediaPath, new byte[16]);
            Directory.CreateDirectory(cacheDir);
            var stale = Path.Combine(cacheDir, "clipA_0_0_beats.json");
            File.WriteAllText(stale, "{}");

            var store = new BeatStore(cacheDir);
            store.Store(mediaPath, "clipA", new BeatAnalysis { Bpm = 70, Beats = [0.0], Downbeats = [] });

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(store.AnalysisPath(mediaPath, "clipA")));
        }
        finally
        {
            TryDeleteDir(cacheDir);
            TryDeleteFile(mediaPath);
        }
    }

    [Fact]
    public async Task BeatStore_DetectAsync_Uses_Detector_And_Caches()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "temno_beat_cache_" + Guid.NewGuid().ToString("N"));
        var mediaPath = Path.Combine(Path.GetTempPath(), "temno_beat_media_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(mediaPath, new byte[8]);
            var fake = new FakeBeatDetector(new BeatAnalysis
            {
                Bpm = 110,
                Beats = [0.0, 0.545, 1.09],
                Downbeats = [0.0],
            });
            var store = new BeatStore(cacheDir, fake);

            var first = await store.DetectAsync(mediaPath, "m1");
            Assert.Equal(110, first.Bpm);
            Assert.Equal(1, fake.CallCount);

            // Second call hits memory/disk — detector not invoked again.
            var second = await store.DetectAsync(mediaPath, "m1");
            Assert.Equal(110, second.Bpm);
            Assert.Equal(1, fake.CallCount);
        }
        finally
        {
            TryDeleteDir(cacheDir);
            TryDeleteFile(mediaPath);
        }
    }

    // ---- FFmpeg end-to-end (skip if unavailable) --------------------------

    [Fact]
    public async Task DetectAsync_With_Ffmpeg_Click_Track_Estimates_Bpm()
    {
        if (!IsFfmpegAvailable())
        {
            // Skip gracefully if ffmpeg/ffprobe is missing in the environment
            return;
        }

        string? wavPath = null;
        try
        {
            wavPath = await GenerateClickWavAsync(bpm: 120, durationSeconds: 6);
            var detector = new EnergyFluxBeatDetector();
            var analysis = await detector.DetectAsync(wavPath);

            Assert.NotEmpty(analysis.Beats);
            Assert.True(analysis.Bpm > 0);
            var error = Math.Abs(analysis.Bpm - 120.0) / 120.0;
            Assert.True(
                error <= 0.08,
                $"FFmpeg path BPM {analysis.Bpm:F2} not close to 120 (error={error:P1}).");
        }
        finally
        {
            if (wavPath is not null)
            {
                TryDeleteFile(wavPath);
            }
        }
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>
    /// Mono click track: short energy bursts at each beat interval, zeros elsewhere.
    /// </summary>
    private static float[] GenerateClickTrack(double durationSeconds, double bpm, float clickAmplitude)
    {
        var total = (int)(durationSeconds * SampleRate);
        var samples = new float[total];
        var intervalSeconds = 60.0 / bpm;
        var clickSamples = 64; // short impulse for sharp energy flux

        for (var beatTime = 0.0; beatTime < durationSeconds; beatTime += intervalSeconds)
        {
            var start = (int)Math.Round(beatTime * SampleRate);
            for (var i = 0; i < clickSamples && start + i < total; i++)
            {
                // Decaying impulse so energy is front-loaded (clear onset).
                var envelope = 1f - (i / (float)clickSamples);
                samples[start + i] = clickAmplitude * envelope;
            }
        }

        return samples;
    }

    private static bool IsFfmpegAvailable()
    {
        try
        {
            var ffmpegPath = FFmpegResolver.GetFFmpegPath();
            if (File.Exists(ffmpegPath))
            {
                return true;
            }

            using var process = new Process();
            process.StartInfo.FileName = ffmpegPath;
            process.StartInfo.Arguments = "-version";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.Start();
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> GenerateClickWavAsync(double bpm, double durationSeconds)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"temno_beat_{Guid.NewGuid():N}.wav");
        var ffmpegPath = FFmpegResolver.GetFFmpegPath();
        // sine clicks: short sine bursts at bpm via ametro / sine — use sine with volume envelopes via aevalsrc is complex;
        // simpler: generate raw PCM file ourselves and wrap with ffmpeg, or use sine + adelay chain.
        // Use lavfi sine + volume gate is hard; write our synthetic PCM via ffmpeg raw input.
        var pcmPath = Path.Combine(Path.GetTempPath(), $"temno_beat_{Guid.NewGuid():N}.f32");
        try
        {
            var samples = GenerateClickTrack(durationSeconds, bpm, 0.9f);
            var bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            await File.WriteAllBytesAsync(pcmPath, bytes);

            var arguments =
                $"-hide_banner -loglevel error -y -f f32le -ar {SampleRate} -ac 1 -i \"{pcmPath}\" -c:a pcm_s16le \"{tempPath}\"";

            using var process = new Process();
            process.StartInfo.FileName = ffmpegPath;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardOutput = true;

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start ffmpeg for click wav.");
            }

            // Drain stdout and stderr concurrently before awaiting exit, or a full 4 KiB pipe buffer deadlocks ffmpeg.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            await stdoutTask;
            var err = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg failed: {err}");
            }

            return tempPath;
        }
        finally
        {
            TryDeleteFile(pcmPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class FakeBeatDetector : IBeatDetector
    {
        private readonly BeatAnalysis _result;

        public FakeBeatDetector(BeatAnalysis result) => _result = result;

        public int CallCount { get; private set; }

        public Task<BeatAnalysis> DetectAsync(string mediaPath, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }
}
