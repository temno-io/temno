using Temno.Core.Models;
using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-213: visible waveform columns must sample source audio time, not stretch the whole-file
/// envelope across the clipped viewport rectangle. Each test pins a mapping the old
/// DrawLanes/AddClipWaveform equations got wrong (see
/// docs/verification/waveform-accuracy-20260910/EVIDENCE.md).
/// </summary>
public sealed class WaveformSamplerTests
{
    private const int Fps = 30;
    private const int Rate = 200; // WaveformEnvelope.DefaultPeaksPerSecond

    private static WaveformEnvelope SpikeEnvelope(double durationSeconds, double spikeSeconds, float spike = 0.879f)
    {
        var peaks = new float[(int)(durationSeconds * Rate)];
        var bucket = (int)(spikeSeconds * Rate);
        peaks[bucket] = spike;
        return new WaveformEnvelope(peaks, Rate);
    }

    private static Clip AudioClip(int startFrame, int durationFrames, int trimStartFrames = 0, double speed = 1.0)
        => new()
        {
            MediaRef = "m1",
            MediaType = ClipType.audio,
            StartFrame = startFrame,
            DurationFrames = durationFrames,
            TrimStartFrame = trimStartFrames,
            Speed = speed,
        };

    [Fact]
    public void SourceSeconds_UsesTrimAndSpeed()
    {
        var clip = AudioClip(startFrame: 90, durationFrames: 300, trimStartFrames: 60, speed: 2.0);
        // Frame 90 (clip start) -> source frame 60 -> 2 s; each timeline frame advances 2 source frames.
        Assert.Equal(2.0, WaveformSampler.SourceSecondsForTimelineFrame(clip, 90, Fps), 9);
        Assert.Equal(2.0 + 30 * 2.0 / Fps, WaveformSampler.SourceSecondsForTimelineFrame(clip, 120, Fps), 9);
    }

    [Fact]
    public void SourceSeconds_RoundTripsThroughCanonicalInverse()
    {
        // The sampler must agree with Clip.TimelineFrameFromSourceSeconds (Mac Clip.timelineFrame)
        // and the TimelineAudioDecoder convention (TrimStartFrame/fps start, DurationFrames*Speed consumed).
        var clips = new[]
        {
            AudioClip(0, 360),
            AudioClip(90, 180, trimStartFrames: 45),
            AudioClip(30, 240, trimStartFrames: 15, speed: 2.0),
            AudioClip(0, 120, speed: 0.5),
        };
        foreach (var clip in clips)
        {
            for (var f = clip.StartFrame; f < clip.EndFrame; f += 7)
            {
                var sourceSeconds = WaveformSampler.SourceSecondsForTimelineFrame(clip, f, Fps);
                var back = clip.TimelineFrameFromSourceSeconds(sourceSeconds, Fps);
                Assert.NotNull(back);
                Assert.InRange(back.Value, f - 1, f + 1);
            }
        }
    }

    [Fact]
    public void EvidenceCase_Viewport608sDoesNotDraw48sPeakAt8068s()
    {
        // Offline reproduction: 12 s clip, 100 px/s, viewport 6–12 s. The old renderer drew the
        // 4.888 s peak (0.879) at timeline 8.068 s, where the audio is silent.
        var envelope = SpikeEnvelope(durationSeconds: 12, spikeSeconds: 4.888);
        var clip = AudioClip(0, 12 * Fps);
        var pixelsPerFrame = 100.0 / Fps;
        var peaks = WaveformSampler.SampleVisibleColumns(
            clip, Fps, envelope, pixelsPerFrame,
            visibleStartContentX: 6 * 100, visibleEndContentX: 12 * 100, columnCount: 300);

        // Timeline 8.068 s sits in column (8.068-6)/6*300 ≈ 103; every nearby column is silent.
        var column = (int)((8.068 - 6.0) / 6.0 * 300);
        for (var i = column - 2; i <= column + 2; i++)
            Assert.Equal(0f, peaks[i]);
        // And no column in the 6–12 s view shows the 4.888 s spike at all.
        Assert.All(peaks, p => Assert.Equal(0f, p));
    }

    [Fact]
    public void PartialViewport_MatchesSameColumnsOfFullView()
    {
        // Partial-viewport invariance: stitching the left and right halves must reproduce the
        // full view column for column. The old stretch-everything formula fails this by design.
        var peaks = new float[12 * Rate];
        var random = new Random(213);
        for (var i = 0; i < peaks.Length; i++) peaks[i] = (float)random.NextDouble();
        var envelope = new WaveformEnvelope(peaks, Rate);
        var clip = AudioClip(0, 12 * Fps);
        var pixelsPerFrame = 100.0 / Fps;

        var full = WaveformSampler.SampleVisibleColumns(
            clip, Fps, envelope, pixelsPerFrame, 0, 12 * 100, 600);
        var left = WaveformSampler.SampleVisibleColumns(
            clip, Fps, envelope, pixelsPerFrame, 0, 6 * 100, 300);
        var right = WaveformSampler.SampleVisibleColumns(
            clip, Fps, envelope, pixelsPerFrame, 6 * 100, 12 * 100, 300);

        Assert.Equal(full.Take(300).ToArray(), left);
        Assert.Equal(full.Skip(300).ToArray(), right);
    }

    [Fact]
    public void ClipOffset_SourceWindowStartsAtClipStart()
    {
        // A 12 s source with a spike at 10 s; the clip plays source 0–6 s starting at timeline 3 s.
        // The 10 s spike must never appear. The old code stretched the whole file into the rect.
        var envelope = SpikeEnvelope(durationSeconds: 12, spikeSeconds: 10.0);
        var clip = AudioClip(startFrame: 3 * Fps, durationFrames: 6 * Fps);
        var pixelsPerFrame = 4.0;
        var peaks = WaveformSampler.SampleVisibleColumns(
            clip, Fps, envelope, pixelsPerFrame,
            visibleStartContentX: 3 * Fps * pixelsPerFrame,
            visibleEndContentX: 9 * Fps * pixelsPerFrame,
            columnCount: 200);
        Assert.All(peaks, p => Assert.Equal(0f, p));
    }

    [Fact]
    public void TrimStart_ShiftsTransientToEarlierTimelinePosition()
    {
        // 2 s trim: source 5 s appears at timeline 3 s, and timeline 5 s is silent.
        var envelope = SpikeEnvelope(durationSeconds: 12, spikeSeconds: 5.0, spike: 1.0f);
        var clip = AudioClip(startFrame: 0, durationFrames: 10 * Fps, trimStartFrames: 2 * Fps);
        var pixelsPerFrame = 10.0; // 300 px/s
        var peaks = WaveformSampler.SampleVisibleColumns(
            clip, Fps, envelope, pixelsPerFrame, 0, 10 * 300, 500);

        var atTimeline3s = peaks[(int)(3.0 / 10.0 * 500)];
        var atTimeline5s = peaks[(int)(5.0 / 10.0 * 500)];
        Assert.Equal(1.0f, atTimeline3s);
        Assert.Equal(0f, atTimeline5s);
    }

    [Theory]
    [InlineData(2.0, 4.0)] // source 8 s -> timeline 4 s
    [InlineData(0.5, 8.0)] // source 4 s -> timeline 8 s
    public void Speed_MapsTransientThroughRetiming(double speed, double expectedTimelineSeconds)
    {
        var sourceSpike = expectedTimelineSeconds * speed;
        var envelope = SpikeEnvelope(durationSeconds: 12, spikeSeconds: sourceSpike, spike: 1.0f);
        var clip = AudioClip(startFrame: 0, durationFrames: 10 * Fps, speed: speed);
        var pixelsPerFrame = 10.0;
        var peaks = WaveformSampler.SampleVisibleColumns(
            clip, Fps, envelope, pixelsPerFrame, 0, 10 * 300, 500);

        var column = (int)(expectedTimelineSeconds / 10.0 * 500);
        Assert.Equal(1.0f, peaks[column]);
        // Neighbouring seconds stay silent: the spike is narrow, not smeared.
        Assert.Equal(0f, peaks[(int)((expectedTimelineSeconds - 2.0) / 10.0 * 500)]);
    }

    [Fact]
    public void ZoomAndScroll_SameInstantSamplesSamePeak()
    {
        var envelope = SpikeEnvelope(durationSeconds: 12, spikeSeconds: 7.25, spike: 0.64f);
        var clip = AudioClip(0, 12 * Fps);
        // Wide view: whole clip at 4 px/frame. Zoomed view: 40 px/frame scrolled to 6–9 s.
        var wide = WaveformSampler.SampleVisibleColumns(clip, Fps, envelope, 4.0, 0, 360 * 4.0, 360);
        var zoomed = WaveformSampler.SampleVisibleColumns(
            clip, Fps, envelope, 40.0, 6 * Fps * 40.0, 9 * Fps * 40.0, 360);
        // Timeline 7.25 s: column 217 of the wide view, column 150 of the zoomed view.
        Assert.Equal(0.64f, wide[(int)(7.25 / 12.0 * 360)]);
        Assert.Equal(0.64f, zoomed[(int)((7.25 - 6.0) / 3.0 * 360)]);
    }

    [Fact]
    public void NarrowTransient_SurvivesWideColumnAggregation()
    {
        // One hot bucket inside a column spanning hundreds of buckets must still read loud:
        // max-aggregation, not point sampling.
        var envelope = SpikeEnvelope(durationSeconds: 60, spikeSeconds: 30.0, spike: 0.9f);
        var clip = AudioClip(0, 60 * Fps);
        var peaks = WaveformSampler.SampleVisibleColumns(clip, Fps, envelope, 2.0, 0, 60 * Fps * 2.0, 60);
        Assert.Equal(0.9f, peaks[30]);
        Assert.Equal(0f, peaks[29]);
        Assert.Equal(0f, peaks[31]);
    }

    [Fact]
    public void ColumnsOutsideClipBody_ReadSilence()
    {
        var peaks = new float[12 * Rate];
        Array.Fill(peaks, 0.7f);
        var envelope = new WaveformEnvelope(peaks, Rate);
        var clip = AudioClip(startFrame: 2 * Fps, durationFrames: 4 * Fps);
        var pixelsPerFrame = 10.0;
        // Viewport 0–8 s: columns before 2 s and after 6 s fall outside the clip body.
        var sampled = WaveformSampler.SampleVisibleColumns(
            clip, Fps, envelope, pixelsPerFrame, 0, 8 * 300, 800);
        Assert.Equal(0f, sampled[0]);
        Assert.Equal(0f, sampled[799]);
        Assert.Equal(0.7f, sampled[400]); // timeline 4 s: inside the clip
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void InvalidSpeed_ReadsSilence(double speed)
    {
        // The audio decoder skips invalid-speed clips (silence); the waveform agrees.
        var peaks = new float[12 * Rate];
        Array.Fill(peaks, 0.7f);
        var envelope = new WaveformEnvelope(peaks, Rate);
        var clip = AudioClip(0, 12 * Fps, speed: speed);
        var sampled = WaveformSampler.SampleVisibleColumns(clip, Fps, envelope, 4.0, 0, 360 * 4.0, 100);
        Assert.All(sampled, p => Assert.Equal(0f, p));
        Assert.True(double.IsNaN(WaveformSampler.SourceSecondsForTimelineFrame(clip, 10, Fps)));
    }

    [Fact]
    public void DegenerateInputs_ReadSilence()
    {
        var envelope = SpikeEnvelope(12, 1.0);
        var clip = AudioClip(0, 12 * Fps);
        Assert.Empty(WaveformSampler.SampleVisibleColumns(clip, Fps, envelope, 4.0, 0, 100, 0));
        Assert.All(WaveformSampler.SampleVisibleColumns(clip, 0, envelope, 4.0, 0, 100, 8), p => Assert.Equal(0f, p));
        Assert.All(WaveformSampler.SampleVisibleColumns(clip, Fps, envelope, 0, 0, 100, 8), p => Assert.Equal(0f, p));
        var zero = AudioClip(5, 0);
        Assert.All(WaveformSampler.SampleVisibleColumns(zero, Fps, envelope, 4.0, 0, 100, 8), p => Assert.Equal(0f, p));
    }
}
