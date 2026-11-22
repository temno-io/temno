using System.Globalization;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-336: a clip's sound plays at its true time and speed whatever the length or start time of the
/// file's audio stream. Audio that ends early leaves silence instead of being stretched, and audio
/// that starts after the container start gets leading silence. Source seconds are on the container
/// clock, the picture's; audio that starts before a later picture is not skipped (owner, 2026-10-05).
/// </summary>
public sealed class AudioStaysOnItsPictureTests
{
    private const int Rate = 48_000;

    // ---- The read plan: where the decode starts, on the container clock ----

    [Fact]
    public void AudioAtTheContainerStart_SeeksExactlyAsBefore()
    {
        var atZero = FfmpegAudioSource.PlanRead(0, 0, 5, Rate);
        Assert.Equal(new AudioReadPlan(0, 0, 5), atZero);

        var trimmed = FfmpegAudioSource.PlanRead(0, 7 / 60.0, 2, Rate);
        Assert.Equal(new AudioReadPlan(0, 7 / 60.0, 2), trimmed);

        var unknown = FfmpegAudioSource.PlanRead(null, 1.25, null, Rate);
        Assert.Equal(new AudioReadPlan(0, 1.25, null), unknown);
    }

    [Fact]
    public void LateAudio_GetsLeadingSilenceAndSeeksToItsFirstSample()
    {
        // WP-274's late-audio fixture: the audio stream starts 0.178 s after the container (and the picture).
        var plan = FfmpegAudioSource.PlanRead(0.178, 0, 121, Rate);

        Assert.Equal(8_544, plan.LeadFrames);
        Assert.Equal(0.178, plan.SeekSeconds, 9);
        Assert.Equal(121 - 0.178, plan.DecodeSeconds!.Value, 9);
    }

    [Fact]
    public void LateAudio_WindowPastTheGap_SeeksOnTheContainerClock()
    {
        var plan = FfmpegAudioSource.PlanRead(0.178, 60, 2, Rate);

        Assert.Equal(new AudioReadPlan(0, 60, 2), plan);
    }

    [Fact]
    public void LateAudio_WindowInsideTheGap_DecodesNothing()
    {
        var plan = FfmpegAudioSource.PlanRead(0.25, 0.05, 0.1, Rate);

        Assert.Equal(9_600, plan.LeadFrames);
        Assert.Equal(0.0, plan.DecodeSeconds!.Value);
    }

    [Fact]
    public void OffsetUnderHalfASample_IsNoOffset()
    {
        var plan = FfmpegAudioSource.PlanRead(0.4 / Rate, 0, 1, Rate);

        Assert.Equal(new AudioReadPlan(0, 0.4 / Rate, 1), plan);
    }

    // ---- Fit: pad or cut to the expected source length, never stretch ----

    [Fact]
    public void Fit_PadsWithSilenceCutsTheTailAndLeavesAnExactBufferAlone()
    {
        var buffer = new AudioBuffer([1, 2, 3, 4, 5, 6], 10, 2);

        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6, 0, 0 }, TimelineAudioDecoder.FitToSampleFrames(buffer, 4).Samples);
        Assert.Equal(new float[] { 1, 2 }, TimelineAudioDecoder.FitToSampleFrames(buffer, 1).Samples);
        Assert.Same(buffer, TimelineAudioDecoder.FitToSampleFrames(buffer, 3));
    }

    // ---- The decoder against a stream whose audio is shorter than the clip ----

    [Fact]
    public async Task ShortAudio_KeepsItsSamplesAndLeavesSilence()
    {
        // 10 s clip at 30 fps; the stream holds 8 s.
        var stream = Ramp(8 * Rate);
        var segment = await DecodeOneClipAsync(stream, durationFrames: 300);

        Assert.Equal(10 * Rate, segment.Length);
        Assert.Equal(stream, segment[..stream.Length]);
        Assert.All(segment[stream.Length..], sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public async Task StreamsOneFrameApart_GiveTheDecodedSamplesThenTheTailPad()
    {
        // A camera file: the audio stream ends one 30 fps frame (1600 samples) before the picture.
        var stream = Ramp(10 * Rate - 1_600);
        var segment = await DecodeOneClipAsync(stream, durationFrames: 300);

        Assert.Equal(stream, segment[..stream.Length]);
        Assert.All(segment[stream.Length..], sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public async Task StreamsOfEqualLength_GiveExactlyTheDecodedSamples()
    {
        var stream = Ramp(10 * Rate);
        var segment = await DecodeOneClipAsync(stream, durationFrames: 300);

        Assert.Equal(stream, segment);
    }

    [Fact]
    public async Task TrimmedPastTheEndOfTheStream_IsSilent()
    {
        var segment = await DecodeOneClipAsync(Ramp(4 * Rate), durationFrames: 60, trimStartFrame: 150);

        Assert.Equal(2 * Rate, segment.Length);
        Assert.All(segment, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public async Task RetimedShortAudio_KeepsItsSpeedAndLeavesSilence()
    {
        // 2x over a 4 s clip asks for 8 s of source; the stream holds 6 s. Source second s must
        // land on clip second s / 2, and the last second of the clip is silent.
        var stream = Ramp(6 * Rate);
        var segment = await DecodeOneClipAsync(stream, durationFrames: 120, speed: 2);

        Assert.Equal(4 * Rate, segment.Length);
        foreach (var second in new[] { 0.5, 1.0, 2.0, 2.9 })
        {
            var outputIndex = (int)(second * Rate);
            Assert.InRange(segment[outputIndex], stream[outputIndex * 2] - 3, stream[outputIndex * 2] + 3);
        }

        Assert.All(segment[(3 * Rate + 2)..], sample => Assert.Equal(0f, sample));
    }

    // ---- Real files through ffmpeg: clicks on whole source seconds ----

    [Fact]
    public async Task Probe_ReportsEachStreamsStartOnTheSeekClock()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var dir = FfmpegFixture.CreateTempDirectory();
        try
        {
            var late = await WriteClickFileAsync(dir, "late.mov", videoOffset: 0, audioOffset: 0.25, audioSeconds: 6);
            var early = await WriteClickFileAsync(dir, "early.mov", videoOffset: 0.1, audioOffset: 0, audioSeconds: 6);
            var probe = new FfmpegMediaProbe();

            Assert.Equal(0.25, (await probe.ProbeAsync(late)).AudioStartSeconds!.Value, 3);
            Assert.Equal(0, (await probe.ProbeAsync(early)).AudioStartSeconds!.Value, 3);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(dir);
        }
    }

    [Fact]
    public async Task LateAudio_PlaysAtItsTimeNotEarly()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var dir = FfmpegFixture.CreateTempDirectory();
        try
        {
            var late = await WriteClickFileAsync(dir, "late.mov", videoOffset: 0, audioOffset: 0.25, audioSeconds: 6);
            var source = new FfmpegAudioSource();

            var fromStart = await source.ReadAudioAsync(late, Rate, 1, 0, 3);
            Assert.Equal(3 * Rate, fromStart.SampleFrameCount);
            AssertClicksAt(fromStart.Samples, [0.25, 1.25, 2.25]);

            var pastTheGap = await source.ReadAudioAsync(late, Rate, 1, 1.5, 1);
            Assert.Equal(Rate, pastTheGap.SampleFrameCount);
            AssertClicksAt(pastTheGap.Samples, [0.75]);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(dir);
        }
    }

    [Fact]
    public async Task AudioBeforeALaterPicture_IsNotSkipped()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var dir = FfmpegFixture.CreateTempDirectory();
        try
        {
            // The picture starts 0.1 s in. Source seconds are the container's, so nothing is skipped
            // and no silence is added: the audio's click at its second 1 is source second 1.
            var early = await WriteClickFileAsync(dir, "early.mov", videoOffset: 0.1, audioOffset: 0, audioSeconds: 6);
            var source = new FfmpegAudioSource();

            var fromStart = await source.ReadAudioAsync(early, Rate, 1, 0, 2);
            Assert.Equal(2 * Rate, fromStart.SampleFrameCount);
            AssertClicksAt(fromStart.Samples, [0, 1]);

            var trimmed = await source.ReadAudioAsync(early, Rate, 1, 1.5, 2);
            AssertClicksAt(trimmed.Samples, [0.5, 1.5]);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(dir);
        }
    }

    [Fact]
    public async Task Decoder_LateAndShortAudio_StayOnTheirPicture()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var dir = FfmpegFixture.CreateTempDirectory();
        try
        {
            // 6 s of picture; audio starts 0.25 s late and ends at 4.25 s.
            var file = await WriteClickFileAsync(dir, "late-short.mov", videoOffset: 0, audioOffset: 0.25, audioSeconds: 4);
            var clip = new Clip
            {
                Id = "clip",
                MediaRef = "clip",
                MediaType = ClipType.audio,
                SourceClipType = ClipType.audio,
                StartFrame = 0,
                DurationFrames = 180,
            };
            var timeline = new Timeline { Fps = 30, Tracks = [new Track { Type = ClipType.audio, Clips = [clip] }] };

            var result = await new TimelineAudioDecoder().DecodeAsync(timeline, _ => file, Rate, 1);

            var samples = Assert.Single(result.Segments).Samples;
            Assert.Equal(6 * Rate, samples.Length);
            AssertClicksAt(samples, [0.25, 1.25, 2.25, 3.25]);
            Assert.All(samples[(int)(4.3 * Rate)..], sample => Assert.Equal(0f, sample));
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(dir);
        }
    }

    private static async Task<float[]> DecodeOneClipAsync(
        float[] stream, int durationFrames, int trimStartFrame = 0, double speed = 1)
    {
        var clip = new Clip
        {
            Id = "clip",
            MediaRef = "clip",
            MediaType = ClipType.audio,
            SourceClipType = ClipType.audio,
            StartFrame = 0,
            DurationFrames = durationFrames,
            TrimStartFrame = trimStartFrame,
            Speed = speed,
        };
        var timeline = new Timeline { Fps = 30, Tracks = [new Track { Type = ClipType.audio, Clips = [clip] }] };
        var path = Path.GetTempFileName();
        try
        {
            var result = await new TimelineAudioDecoder(new StreamAudioSource(stream))
                .DecodeAsync(timeline, _ => path, Rate, 1);
            return Assert.Single(result.Segments).Samples;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static float[] Ramp(int length)
        => Enumerable.Range(0, length).Select(index => (float)index).ToArray();

    /// <summary>
    /// A 64x36 picture and 1 kHz clicks, 10 ms long, on every whole second of the audio stream's own
    /// clock. PCM in QuickTime, so the stream starts exactly where it is put: no encoder priming.
    /// </summary>
    private static async Task<string> WriteClickFileAsync(
        string dir, string name, double videoOffset, double audioOffset, double audioSeconds)
    {
        var path = Path.Combine(dir, name);
        string F(double value) => value.ToString(CultureInfo.InvariantCulture);
        await FfmpegFixture.RunAsync(
            "-hide_banner", "-loglevel", "error", "-y",
            "-itsoffset", F(videoOffset), "-f", "lavfi", "-i", "nullsrc=s=64x36:r=30:d=6,format=yuv420p",
            "-itsoffset", F(audioOffset), "-f", "lavfi", "-i",
            $"aevalsrc='0.8*sin(2*PI*1000*t)*lt(mod(t,1),0.01)':s={Rate}:c=mono:d={F(audioSeconds)}",
            "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-preset", "ultrafast",
            "-c:a", "pcm_s16le", path);
        return path;
    }

    /// <summary>Every click onset, in seconds of the buffer, is within 1 ms of the expected time.</summary>
    private static void AssertClicksAt(float[] mono, double[] expectedSeconds)
    {
        var onsets = new List<double>();
        for (var i = 0; i < mono.Length; i++)
        {
            if (Math.Abs(mono[i]) <= 0.16f) continue;
            onsets.Add(i / (double)Rate);
            i += Rate / 2;
        }

        Assert.Equal(expectedSeconds.Length, onsets.Count);
        for (var k = 0; k < onsets.Count; k++)
            Assert.InRange(onsets[k] - expectedSeconds[k], -0.001, 0.001);
    }

    /// <summary>A mono stream read the way ffmpeg reads it: whatever exists in the window, never more.</summary>
    private sealed class StreamAudioSource(float[] stream) : IAudioSource
    {
        public Task<AudioBuffer> ReadAudioAsync(
            string path,
            int sampleRate,
            int channelCount,
            double startSeconds = 0,
            double? durationSeconds = null,
            CancellationToken cancellationToken = default)
        {
            var start = (int)Math.Min(stream.Length, Math.Round(startSeconds * sampleRate));
            var end = durationSeconds is { } duration
                ? (int)Math.Min(stream.Length, start + Math.Round(duration * sampleRate))
                : stream.Length;
            return Task.FromResult(new AudioBuffer(stream[start..end], sampleRate, channelCount));
        }
    }
}
