using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

public class AudioMixTests
{
    // fps 30 at 3000 Hz => exactly 100 samples per timeline frame, so gain
    // values at frame-block starts are sample-exact.
    private const int Fps = 30;
    private const int SampleRate = 3000;
    private const int SamplesPerFrame = SampleRate / Fps;

    private static Clip MakeAudioClip(int startFrame, int durationFrames, double volume = 1.0)
        => new()
        {
            MediaType = ClipType.audio,
            SourceClipType = ClipType.audio,
            StartFrame = startFrame,
            DurationFrames = durationFrames,
            Volume = volume,
        };

    private static float[] ConstantSignal(int durationFrames, float value, int channelCount = 1)
    {
        var samples = new float[durationFrames * SamplesPerFrame * channelCount];
        Array.Fill(samples, value);
        return samples;
    }

    [Fact]
    public void SampleOffsetForFrame_Converts_Frames_To_Samples()
    {
        Assert.Equal(0L, AudioMixer.SampleOffsetForFrame(0, 30, 48000));
        Assert.Equal(48000L, AudioMixer.SampleOffsetForFrame(30, 30, 48000));
        Assert.Equal(1600L, AudioMixer.SampleOffsetForFrame(1, 30, 48000));
        // Fractional samples-per-frame: 1 * 44100 / 24 = 1837.5 -> away-from-zero -> 1838.
        Assert.Equal(1838L, AudioMixer.SampleOffsetForFrame(1, 24, 44100));
        Assert.Equal(3675L, AudioMixer.SampleOffsetForFrame(2, 24, 44100));
    }

    [Fact]
    public void SampleOffsetForFrame_Rejects_Invalid_Rates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioMixer.SampleOffsetForFrame(0, 0, 48000));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioMixer.SampleOffsetForFrame(0, 30, 0));
    }

    [Fact]
    public void Mix_EmptyRange_Returns_Empty_Buffer()
    {
        var clip = MakeAudioClip(0, 10);
        var segment = new AudioClipSegment(clip, ConstantSignal(10, 1f));

        var mixed = AudioMixer.Mix([segment], 0, 0, Fps, SampleRate, 1);

        Assert.Empty(mixed);
    }

    [Fact]
    public void Mix_NoSegments_Returns_Silence_Of_Exact_Length()
    {
        var mixed = AudioMixer.Mix([], 0, 30, Fps, SampleRate, 2);

        Assert.Equal(30 * SamplesPerFrame * 2, mixed.Length);
        Assert.All(mixed, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Mix_Output_Length_Uses_Rounded_Absolute_Offsets()
    {
        // 24 fps at 44100 Hz: [10, 30) frames => offset(30) - offset(10) samples.
        var mixed = AudioMixer.Mix([], 10, 20, 24, 44100, 1);

        var expected = AudioMixer.SampleOffsetForFrame(30, 24, 44100) - AudioMixer.SampleOffsetForFrame(10, 24, 44100);
        Assert.Equal(expected, (long)mixed.Length);
    }

    [Fact]
    public void Mix_VolumeScaling_Is_Exact()
    {
        var clip = MakeAudioClip(0, 10, volume: 0.5);
        var segment = new AudioClipSegment(clip, ConstantSignal(10, 1f));

        var mixed = AudioMixer.Mix([segment], 0, 10, Fps, SampleRate, 1);

        Assert.Equal(10 * SamplesPerFrame, mixed.Length);
        Assert.All(mixed, s => Assert.Equal(0.5f, s));
    }

    [Fact]
    public void Mix_TwoOverlappingConstantClips_Sum()
    {
        // A: [0, 10) at 0.5; B: [5, 15) at 0.25 — overlap [5, 10) sums to 0.75.
        var a = new AudioClipSegment(MakeAudioClip(0, 10, 0.5), ConstantSignal(10, 1f));
        var b = new AudioClipSegment(MakeAudioClip(5, 10, 0.25), ConstantSignal(10, 1f));

        var mixed = AudioMixer.Mix([a, b], 0, 15, Fps, SampleRate, 1);

        Assert.Equal(0.5f, mixed[0]);
        Assert.Equal(0.5f, mixed[5 * SamplesPerFrame - 1]);
        Assert.Equal(0.75f, mixed[5 * SamplesPerFrame]);
        Assert.Equal(0.75f, mixed[10 * SamplesPerFrame - 1]);
        Assert.Equal(0.25f, mixed[10 * SamplesPerFrame]);
        Assert.Equal(0.25f, mixed[15 * SamplesPerFrame - 1]);
    }

    [Fact]
    public void Mix_Sum_Is_Clamped_To_Unit_Range()
    {
        var a = new AudioClipSegment(MakeAudioClip(0, 10, 0.8), ConstantSignal(10, 1f));
        var b = new AudioClipSegment(MakeAudioClip(0, 10, 0.8), ConstantSignal(10, 1f));

        var mixed = AudioMixer.Mix([a, b], 0, 10, Fps, SampleRate, 1);

        Assert.All(mixed, s => Assert.Equal(1f, s));
    }

    [Fact]
    public void Mix_Linear_FadeIn_Ramp_Endpoints()
    {
        var clip = MakeAudioClip(0, 20);
        clip.FadeInFrames = 10;
        var segment = new AudioClipSegment(clip, ConstantSignal(20, 1f));

        var mixed = AudioMixer.Mix([segment], 0, 20, Fps, SampleRate, 1);

        // Gain at frame-block starts equals Clip.VolumeAt(frame) exactly.
        Assert.Equal(0f, mixed[0]);
        Assert.Equal(0.5f, mixed[5 * SamplesPerFrame]);
        Assert.Equal(1f, mixed[10 * SamplesPerFrame]);
        Assert.Equal(1f, mixed[19 * SamplesPerFrame]);
    }

    [Fact]
    public void Mix_Linear_FadeOut_Ramp_Endpoints_And_Terminal_Zero()
    {
        var clip = MakeAudioClip(0, 20);
        clip.FadeOutFrames = 10;
        var segment = new AudioClipSegment(clip, ConstantSignal(20, 1f));

        var mixed = AudioMixer.Mix([segment], 0, 20, Fps, SampleRate, 1);

        Assert.Equal(1f, mixed[0]);
        Assert.Equal(1f, mixed[10 * SamplesPerFrame]); // fade starts here (outRem = 10)
        Assert.Equal(0.5f, mixed[15 * SamplesPerFrame]);
        // Last block [19, 20) ramps 0.1 -> 0: final sample is one lerp step above zero.
        var last = mixed[20 * SamplesPerFrame - 1];
        Assert.InRange(last, 0.0005f, 0.0015f);
    }

    [Fact]
    public void Mix_Smooth_FadeIn_Uses_Smoothstep()
    {
        var clip = MakeAudioClip(0, 20);
        clip.FadeInFrames = 4;
        clip.FadeInInterpolation = Interpolation.smooth;
        var segment = new AudioClipSegment(clip, ConstantSignal(20, 1f));

        var mixed = AudioMixer.Mix([segment], 0, 20, Fps, SampleRate, 1);

        // smoothstep(0.25) = 0.15625, exactly representable in float.
        Assert.Equal(0.15625f, mixed[1 * SamplesPerFrame]);
        Assert.Equal(0.5f, mixed[2 * SamplesPerFrame]);
    }

    [Fact]
    public void Mix_VolumeTrack_Keyframes_Are_Db()
    {
        var clip = MakeAudioClip(0, 20);
        clip.VolumeTrack = new KeyframeTrack<double>();
        clip.VolumeTrack.Upsert(new Keyframe<double> { Frame = 0, Value = 0, InterpolationOut = Interpolation.linear });
        clip.VolumeTrack.Upsert(new Keyframe<double> { Frame = 10, Value = -20, InterpolationOut = Interpolation.linear });
        var segment = new AudioClipSegment(clip, ConstantSignal(20, 1f));

        var mixed = AudioMixer.Mix([segment], 0, 20, Fps, SampleRate, 1);

        Assert.Equal(1f, mixed[0]);
        // Keyframes interpolate in dB: frame 5 => -10 dB => 10^(-0.5).
        Assert.Equal(Math.Pow(10, -0.5), mixed[5 * SamplesPerFrame], 4);
        Assert.Equal(0.1, mixed[10 * SamplesPerFrame], 5);
        Assert.Equal(0.1, mixed[19 * SamplesPerFrame], 5); // clamped past last keyframe
    }

    [Fact]
    public void Mix_Arbitrary_Range_Extracts_Correct_Window()
    {
        var clip = MakeAudioClip(0, 20);
        var segment = new AudioClipSegment(clip, ConstantSignal(20, 1f));

        // Range [15, 25): frames 15-19 audible, 20-24 silent.
        var mixed = AudioMixer.Mix([segment], 15, 10, Fps, SampleRate, 1);

        Assert.Equal(10 * SamplesPerFrame, mixed.Length);
        Assert.All(mixed.Take(5 * SamplesPerFrame), s => Assert.Equal(1f, s));
        Assert.All(mixed.Skip(5 * SamplesPerFrame), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Mix_Segment_Shorter_Than_Clip_ZeroFills_Remainder()
    {
        var clip = MakeAudioClip(0, 20);
        var segment = new AudioClipSegment(clip, ConstantSignal(10, 1f)); // only half the placement

        var mixed = AudioMixer.Mix([segment], 0, 20, Fps, SampleRate, 1);

        Assert.All(mixed.Take(10 * SamplesPerFrame), s => Assert.Equal(1f, s));
        Assert.All(mixed.Skip(10 * SamplesPerFrame), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Mix_Stereo_Preserves_Channel_Interleaving()
    {
        var clip = MakeAudioClip(0, 10, volume: 0.5);
        var samples = new float[10 * SamplesPerFrame * 2];
        for (var i = 0; i < samples.Length; i += 2)
        {
            samples[i] = 1f;      // left
            samples[i + 1] = -0.5f; // right
        }

        var segment = new AudioClipSegment(clip, samples);
        var mixed = AudioMixer.Mix([segment], 0, 10, Fps, SampleRate, 2);

        for (var i = 0; i < mixed.Length; i += 2)
        {
            Assert.Equal(0.5f, mixed[i]);
            Assert.Equal(-0.25f, mixed[i + 1]);
        }
    }

    // --- FfmpegAudioSource (skips gracefully when ffmpeg is absent) ---

    private bool IsFfmpegAvailable()
    {
        try
        {
            var ffmpegPath = FFmpegResolver.GetFFmpegPath();
            var ffprobePath = FFmpegResolver.GetFFprobePath();

            if (File.Exists(ffmpegPath) && File.Exists(ffprobePath))
            {
                return true;
            }

            using var process = new Process();
            process.StartInfo.FileName = ffmpegPath;
            process.StartInfo.Arguments = "-version";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            process.WaitForExit();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> GenerateTestToneAsync()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"temno_test_{Guid.NewGuid():N}.wav");
        var ffmpegPath = FFmpegResolver.GetFFmpegPath();

        // 1 second 440 Hz sine at 48 kHz mono
        var arguments = $"-y -f lavfi -i sine=frequency=440:duration=1:sample_rate=48000 -c:a pcm_s16le \"{tempPath}\"";

        using var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg to generate test tone.");
        }

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed to generate test tone. Exit code: {process.ExitCode}");
        }

        return tempPath;
    }

    [Fact]
    public async Task Test_AudioSource_Decodes_And_Resamples()
    {
        if (!IsFfmpegAvailable())
        {
            // Skip gracefully if ffmpeg/ffprobe is missing in the environment
            return;
        }

        string? tempTonePath = null;
        try
        {
            tempTonePath = await GenerateTestToneAsync();
            var source = new FfmpegAudioSource();

            // Native rate, mono
            var native = await source.ReadAudioAsync(tempTonePath, 48000, 1);
            Assert.Equal(48000, native.SampleRate);
            Assert.Equal(1, native.ChannelCount);
            Assert.InRange(native.SampleFrameCount, 47000, 49000);
            Assert.True(native.Samples.Max(MathF.Abs) > 0.01f, "Decoded tone should not be silent.");
            Assert.True(native.Samples.Max(MathF.Abs) <= 1.0f, "Float PCM should stay within unit range.");

            // Resampled to 22050 Hz stereo
            var resampled = await source.ReadAudioAsync(tempTonePath, 22050, 2);
            Assert.Equal(2, resampled.ChannelCount);
            Assert.InRange(resampled.SampleFrameCount, 21500, 22600);

            // Windowed read: 0.25 s from 0.5 s in
            var window = await source.ReadAudioAsync(tempTonePath, 48000, 1, startSeconds: 0.5, durationSeconds: 0.25);
            Assert.InRange(window.SampleFrameCount, 11000, 13000);
        }
        finally
        {
            if (tempTonePath != null && File.Exists(tempTonePath))
            {
                try
                {
                    File.Delete(tempTonePath);
                }
                catch
                {
                    // Ignore cleanup exceptions
                }
            }
        }
    }

    [Fact]
    public async Task Test_AudioSource_Throws_On_MissingFile()
    {
        var source = new FfmpegAudioSource();
        var missingPath = Path.Combine(Path.GetTempPath(), $"temno_missing_{Guid.NewGuid():N}.wav");

        await Assert.ThrowsAsync<FileNotFoundException>(() => source.ReadAudioAsync(missingPath, 48000, 2));
    }

    [Fact]
    public async Task Test_AudioSource_Throws_On_EmptyPath()
    {
        var source = new FfmpegAudioSource();
        await Assert.ThrowsAsync<ArgumentException>(() => source.ReadAudioAsync("", 48000, 2));
    }
}
