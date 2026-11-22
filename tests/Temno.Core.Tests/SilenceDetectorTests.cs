using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Temno.Media.Audio;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

public class SilenceDetectorTests
{
    private const int SampleRate = SilenceDetector.SampleRate;
    private const double Cell = SilenceDetector.CellDurationSeconds; // 0.032 s
    private const double Pad = SilenceDetectionOptions.DefaultSpeechPaddingSeconds;

    // Durations are multiples of the 32 ms cell so run boundaries are deterministic.
    private const double Block = 32 * Cell; // 1.024 s

    // ---- pure silence -------------------------------------------------------

    [Fact]
    public void AnalyzeSamples_PureSilence_Returns_One_FullSpan_Range()
    {
        var samples = new float[SampleRate * 2]; // 2 s of zeros

        var ranges = SilenceDetector.AnalyzeSamples(samples, SampleRate);

        var range = Assert.Single(ranges);
        Assert.Equal(0.0, range.Start, 9);
        Assert.Equal(2.0, range.End, 9);
    }

    // ---- speech / silence / speech ------------------------------------------

    [Fact]
    public void AnalyzeSamples_SpeechSilenceSpeech_Returns_Gap_Minus_Padding()
    {
        var samples = Concat(Tone(Block, 0.5f), Zeros(Block), Tone(Block, 0.5f));

        var ranges = SilenceDetector.AnalyzeSamples(samples, SampleRate);

        var range = Assert.Single(ranges);
        Assert.Equal(Block + Pad, range.Start, 9);
        Assert.Equal(2 * Block - Pad, range.End, 9);
    }

    [Fact]
    public void AnalyzeSamples_LeadingSilence_Pads_Only_The_Speech_Edge()
    {
        var samples = Concat(Zeros(Block), Tone(Block, 0.5f));

        var ranges = SilenceDetector.AnalyzeSamples(samples, SampleRate);

        var range = Assert.Single(ranges);
        Assert.Equal(0.0, range.Start, 9);
        Assert.Equal(Block - Pad, range.End, 9);
    }

    [Fact]
    public void AnalyzeSamples_TrailingSilence_Pads_Only_The_Speech_Edge()
    {
        var samples = Concat(Tone(Block, 0.5f), Zeros(Block));

        var ranges = SilenceDetector.AnalyzeSamples(samples, SampleRate);

        var range = Assert.Single(ranges);
        Assert.Equal(Block + Pad, range.Start, 9);
        Assert.Equal(2 * Block, range.End, 9);
    }

    [Fact]
    public void AnalyzeSamples_Multiple_Gaps_Are_Ordered()
    {
        var samples = Concat(
            Tone(Block, 0.5f), Zeros(Block),
            Tone(Block, 0.5f), Zeros(Block),
            Tone(Block, 0.5f));

        var ranges = SilenceDetector.AnalyzeSamples(samples, SampleRate);

        Assert.Equal(2, ranges.Count);
        Assert.True(ranges[0].End < ranges[1].Start);
        Assert.Equal(Block + Pad, ranges[0].Start, 9);
        Assert.Equal(3 * Block + Pad, ranges[1].Start, 9);
    }

    // ---- minimum duration ----------------------------------------------------

    [Fact]
    public void AnalyzeSamples_Short_Gap_Below_Minimum_Not_Flagged()
    {
        // 4 cells = 0.128 s < 0.256 s minimum.
        var samples = Concat(Tone(Block, 0.5f), Zeros(4 * Cell), Tone(Block, 0.5f));

        var ranges = SilenceDetector.AnalyzeSamples(samples, SampleRate);

        Assert.Empty(ranges);
    }

    [Fact]
    public void AnalyzeSamples_Gap_Exactly_At_Minimum_Is_Flagged()
    {
        // 8 cells = 0.256 s; use an explicit policy to hit the boundary exactly.
        var samples = Concat(Tone(Block, 0.5f), Zeros(8 * Cell), Tone(Block, 0.5f));

        var ranges = SilenceDetector.AnalyzeSamples(samples, SampleRate, new SilenceDetectionOptions
        {
            MinimumPauseSeconds = 8 * Cell,
            SpeechPaddingSeconds = 0,
        });

        var range = Assert.Single(ranges);
        Assert.Equal(Block, range.Start, 9);
        Assert.Equal(Block + 8 * Cell, range.End, 9);
    }

    [Fact]
    public void AnalyzeSamples_Gap_One_Cell_Under_Minimum_Not_Flagged()
    {
        var samples = Concat(Tone(Block, 0.5f), Zeros(7 * Cell), Tone(Block, 0.5f));

        var ranges = SilenceDetector.AnalyzeSamples(samples, SampleRate, new SilenceDetectionOptions
        {
            MinimumPauseSeconds = 8 * Cell,
            SpeechPaddingSeconds = 0,
        });

        Assert.Empty(ranges);
    }

    [Fact]
    public void AnalyzeSamples_MinimumPause_Is_Evaluated_Before_Padding()
    {
        // Raw gap 9 cells = 0.288 s qualifies; 30 ms padding then yields 0.228 s.
        var samples = Concat(Tone(Block, 0.5f), Zeros(9 * Cell), Tone(Block, 0.5f));
        var options = new SilenceDetectionOptions
        {
            MinimumPauseSeconds = 8 * Cell,
            SpeechPaddingSeconds = 0.03,
        };

        var padded = SilenceDetector.AnalyzeSamples(samples, SampleRate, options);

        var range = Assert.Single(padded);
        Assert.Equal(9 * Cell - 0.06, range.Duration, 9);
    }

    [Fact]
    public void ApplyTimingPolicy_MinimumPause_DefaultAndOverride_AreAbsolute()
    {
        var raw = new[] { new SilenceRange(1.0, 1.4) };

        Assert.Empty(SilenceDetector.ApplyTimingPolicy(raw, 3.0));

        var lowered = SilenceDetector.ApplyTimingPolicy(raw, 3.0, new SilenceDetectionOptions
        {
            MinimumPauseSeconds = 0.3,
            SpeechPaddingSeconds = 0.15,
        });
        var range = Assert.Single(lowered);
        const int fps = 30;
        var startFrame = (int)Math.Round(range.Start * fps, MidpointRounding.AwayFromZero);
        var endFrame = (int)Math.Round(range.End * fps, MidpointRounding.AwayFromZero);
        Assert.Equal(35, startFrame);
        Assert.Equal(38, endFrame);
        Assert.Equal(3, endFrame - startFrame); // 0.1 s after 150 ms padding at both edges
    }

    [Fact]
    public void ApplyTimingPolicy_OneSecondPause_DefaultPadding_CutsExactlyPointSevenSecondsAt30Fps()
    {
        const int fps = 30;
        var range = Assert.Single(SilenceDetector.ApplyTimingPolicy(
            [new SilenceRange(2.0, 3.0)],
            mediaDurationSeconds: 5.0));

        var startFrame = (int)Math.Round(range.Start * fps, MidpointRounding.AwayFromZero);
        var endFrame = (int)Math.Round(range.End * fps, MidpointRounding.AwayFromZero);

        Assert.Equal(65, startFrame);
        Assert.Equal(86, endFrame);
        Assert.Equal(21, endFrame - startFrame); // 0.7 s at 30 fps
    }

    [Fact]
    public void RemoveSilence_McpSchema_RemainsArgumentFreeLikeMac()
    {
        var schema = McpToolRegistry.Tools.Single(t => t.Name == "remove_silence").InputSchema;

        Assert.Null(schema["properties"]);
        Assert.Null(schema["required"]);
    }

    // ---- threshold ------------------------------------------------------------

    [Fact]
    public void AnalyzeSamples_Hum_Below_Threshold_Counts_As_Silence()
    {
        // -40 dB hum is below the -28 dB default threshold.
        var samples = Concat(Tone(Block, 0.5f), Tone(Block, 0.01f), Tone(Block, 0.5f));

        var ranges = SilenceDetector.AnalyzeSamples(samples, SampleRate);

        var range = Assert.Single(ranges);
        Assert.Equal(Block + Pad, range.Start, 9);
        Assert.Equal(2 * Block - Pad, range.End, 9);
    }

    [Fact]
    public void AnalyzeSamples_Hum_Above_Threshold_Not_Silence()
    {
        // -20 dB hum is above the -28 dB default threshold.
        var samples = Concat(Tone(Block, 0.5f), Tone(Block, 0.1f), Tone(Block, 0.5f));

        var ranges = SilenceDetector.AnalyzeSamples(samples, SampleRate);

        Assert.Empty(ranges);
    }

    [Fact]
    public void AnalyzeSamples_Custom_Threshold_Flags_Louder_Hum()
    {
        // Same -20 dB hum becomes silence once the threshold is raised to -15 dB.
        var samples = Concat(Tone(Block, 0.5f), Tone(Block, 0.1f), Tone(Block, 0.5f));

        var ranges = SilenceDetector.AnalyzeSamples(samples, SampleRate, new SilenceDetectionOptions
        {
            ThresholdDb = -15.0,
        });

        Assert.Single(ranges);
    }

    // ---- empty / invalid input -------------------------------------------------

    [Fact]
    public void AnalyzeSamples_Empty_Input_Returns_Empty()
    {
        Assert.Empty(SilenceDetector.AnalyzeSamples([], SampleRate));
    }

    [Fact]
    public void AnalyzeSamples_Invalid_Arguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => SilenceDetector.AnalyzeSamples(null!, SampleRate));
        Assert.Throws<ArgumentOutOfRangeException>(() => SilenceDetector.AnalyzeSamples([0f], 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SilenceDetector.AnalyzeSamples([0f], SampleRate, new SilenceDetectionOptions
            {
                MinimumPauseSeconds = -1,
            }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SilenceDetector.AnalyzeSamples([0f], SampleRate, new SilenceDetectionOptions
            {
                SpeechPaddingSeconds = -0.1,
            }));
    }

    [Fact]
    public void AnalyzeSamples_Works_At_Other_Sample_Rates()
    {
        const int rate = 48_000;
        var tone = ToneAt(rate, Block, 0.5f);
        var gap = new float[(int)Math.Round(Block * rate)];
        var samples = Concat(tone, gap, tone);

        var ranges = SilenceDetector.AnalyzeSamples(samples, rate);

        var range = Assert.Single(ranges);
        Assert.InRange(range.Start, Block, Block + Pad + Cell);
        Assert.InRange(range.End, 2 * Block - Pad - Cell, 2 * Block);
    }

    // ---- DetectAsync (fake audio source, no ffmpeg) ------------------------------

    [Fact]
    public async Task DetectAsync_Uses_AudioSource_And_Matches_Pure_Analysis()
    {
        var samples = Concat(Tone(Block, 0.5f), Zeros(Block), Tone(Block, 0.5f));
        var mediaPath = Path.Combine(Path.GetTempPath(), $"temno_silence_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(mediaPath, new byte[16]);
            var detector = new SilenceDetector(new FakeAudioSource(samples, SampleRate));

            var ranges = await detector.DetectAsync(mediaPath);

            var expected = SilenceDetector.AnalyzeSamples(samples, SampleRate);
            Assert.Equal(expected.Count, ranges.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Start, ranges[i].Start, 9);
                Assert.Equal(expected[i].End, ranges[i].End, 9);
            }
        }
        finally
        {
            TryDeleteFile(mediaPath);
        }
    }

    [Fact]
    public async Task DetectAsync_Empty_Stream_Returns_Empty()
    {
        var mediaPath = Path.Combine(Path.GetTempPath(), $"temno_silence_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(mediaPath, new byte[16]);
            var detector = new SilenceDetector(new FakeAudioSource([], SampleRate));

            Assert.Empty(await detector.DetectAsync(mediaPath));
        }
        finally
        {
            TryDeleteFile(mediaPath);
        }
    }

    [Fact]
    public async Task DetectAsync_Missing_File_Throws()
    {
        var detector = new SilenceDetector(new FakeAudioSource([], SampleRate));
        await Assert.ThrowsAsync<ArgumentException>(() => detector.DetectAsync(""));
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => detector.DetectAsync(Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.mp4")));
    }

    // ---- helpers -------------------------------------------------------------

    private static float[] Tone(double seconds, float amplitude) => ToneAt(SampleRate, seconds, amplitude);

    private static float[] ToneAt(int sampleRate, double seconds, float amplitude)
    {
        var samples = new float[(int)Math.Round(seconds * sampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = amplitude * (float)Math.Sin(2 * Math.PI * 440.0 * i / sampleRate);
        }

        return samples;
    }

    private static float[] Zeros(double seconds) => new float[(int)Math.Round(seconds * SampleRate)];

    private static float[] Concat(params float[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new float[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
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

    private sealed class FakeAudioSource : IAudioSource
    {
        private readonly float[] _samples;
        private readonly int _sampleRate;

        public FakeAudioSource(float[] samples, int sampleRate)
        {
            _samples = samples;
            _sampleRate = sampleRate;
        }

        public Task<AudioBuffer> ReadAudioAsync(
            string path,
            int sampleRate,
            int channelCount,
            double startSeconds = 0,
            double? durationSeconds = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AudioBuffer(_samples, _sampleRate, channelCount: 1));
    }
}
