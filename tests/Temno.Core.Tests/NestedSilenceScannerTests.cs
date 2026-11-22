using Temno.Core.Models;
using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-172: nested carriers expose their inner audio to silence detection.</summary>
public sealed class NestedSilenceScannerTests
{
    private const int Fps = 30;

    [Fact]
    public void Plan_NestedOnlyTimeline_ExposesInnerFileWithOneHop()
    {
        var nested = NestedTimeline("T-NEST", Fps, InnerAudioClip("inner", 0, 300));
        var parent = ParentWithCarrier("carrier", nested.Id, startFrame: 10, durationFrames: 300);

        var plan = NestedSilenceScanner.Plan(
            parent,
            id => id == nested.Id ? nested : null,
            _ => "/audio/nested.wav");

        var source = Assert.Single(plan);
        Assert.Equal("/audio/nested.wav", source.Path);
        Assert.Equal(0, source.ParentTrackIndex);
        Assert.Equal("carrier", source.CarrierClipId);
        Assert.Equal(Fps, source.NestedFps);
        var hop = Assert.Single(source.Hops);
        Assert.Equal(Fps, hop.ChildFps);
        Assert.Equal(10, hop.StartFrame);
        Assert.Equal(310, hop.EndFrame);
        Assert.Equal(Fps, hop.ParentFps);
    }

    [Fact]
    public void Plan_DirectClip_IsNotPlanned_NorIsAManifestMiss()
    {
        var nested = NestedTimeline("T-NEST", Fps, InnerAudioClip("inner", 0, 300));
        var parent = new Timeline
        {
            Id = "T-MAIN",
            Fps = Fps,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.audio,
                    Clips = [DirectAudioClip("direct", 0, 100)],
                },
                new Track
                {
                    Type = ClipType.video,
                    Clips = [SequenceCarrier("carrier", nested.Id, 0, 300)],
                },
            ],
        };

        // Direct clips stay the caller's own loop; a null resolve (manifest miss / HasAudio
        // false / offline) plans nothing.
        var withResolve = NestedSilenceScanner.Plan(
            parent, id => id == nested.Id ? nested : null, _ => "/audio/nested.wav");
        Assert.Single(withResolve);

        var withoutResolve = NestedSilenceScanner.Plan(
            parent, id => id == nested.Id ? nested : null, _ => null);
        Assert.Empty(withoutResolve);
    }

    [Fact]
    public void Plan_MutedNestedTrack_PlansNothing()
    {
        var nested = NestedTimeline("T-NEST", Fps, InnerAudioClip("inner", 0, 300));
        nested.Tracks[0].Muted = true;
        var parent = ParentWithCarrier("carrier", nested.Id, startFrame: 0, durationFrames: 300);

        var plan = NestedSilenceScanner.Plan(
            parent, id => id == nested.Id ? nested : null, _ => "/audio/nested.wav");

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_StackedNesting_AccumulatesHopsInnermostFirst()
    {
        var inner2 = NestedTimeline("T-INNER2", Fps, InnerAudioClip("deep", 0, 300));
        var inner1 = new Timeline
        {
            Id = "T-INNER1",
            Fps = Fps,
            Tracks = [new Track
            {
                Type = ClipType.video,
                Clips = [SequenceCarrier("c2", inner2.Id, 0, 300)],
            }],
        };
        var parent = ParentWithCarrier("c1", inner1.Id, startFrame: 0, durationFrames: 300);

        Timeline? Lookup(string id) =>
            id == inner1.Id ? inner1 : id == inner2.Id ? inner2 : null;

        var plan = NestedSilenceScanner.Plan(parent, Lookup, _ => "/audio/deep.wav");

        var source = Assert.Single(plan);
        Assert.Equal(2, source.Hops.Count);
        Assert.Equal("c1", source.CarrierClipId);
        // Innermost hop first: frames address T-INNER2, then T-INNER1, then the parent.
        Assert.Equal(Fps, source.Hops[0].ChildFps);
        Assert.Equal(Fps, source.Hops[0].ParentFps);
        Assert.Equal(Fps, source.Hops[1].ChildFps);
        Assert.Equal(Fps, source.Hops[1].ParentFps);
    }

    [Fact]
    public void MapNestedRangeToCarrier_TrimsAndRetimeLikeTheDecoderWindow()
    {
        // Carrier at parent 10..310 (fps 30), trim 60 parent frames = 2 s, speed 1:
        // nested frame 60 (= 2 s in) lands on parent frame 10; nested frame 90 lands on 40.
        var hop = new NestedSilenceScanner.CarrierHop(
            ChildFps: 30, StartFrame: 10, EndFrame: 310,
            TrimStartFrame: 60, Speed: 1.0, ParentFps: 30);

        var mapped = NestedSilenceScanner.MapNestedRangeToCarrier(60, 90, 30, hop, 30);

        Assert.NotNull(mapped);
        Assert.Equal(10, mapped.Value.Start);
        Assert.Equal(40, mapped.Value.End);
    }

    [Fact]
    public void MapNestedRangeToCarrier_SpeedStretchesTheWindow()
    {
        // Speed 2 consumes nested seconds twice as fast: nested frames 0..60 (2 s) cover one
        // parent second = 30 frames.
        var hop = new NestedSilenceScanner.CarrierHop(
            ChildFps: 30, StartFrame: 0, EndFrame: 300,
            TrimStartFrame: 0, Speed: 2.0, ParentFps: 30);

        var mapped = NestedSilenceScanner.MapNestedRangeToCarrier(0, 60, 30, hop, 30);

        Assert.NotNull(mapped);
        Assert.Equal(0, mapped.Value.Start);
        Assert.Equal(30, mapped.Value.End);
    }

    [Fact]
    public void MapNestedRangeToCarrier_ClampsToCarrierPlacement()
    {
        var hop = new NestedSilenceScanner.CarrierHop(
            ChildFps: 30, StartFrame: 10, EndFrame: 40,
            TrimStartFrame: 0, Speed: 1.0, ParentFps: 30);

        // Nested range far past the carrier end clamps into the placement, and a range fully
        // outside it maps to nothing instead of leaking onto neighbouring clips.
        var clamped = NestedSilenceScanner.MapNestedRangeToCarrier(0, 900, 30, hop, 30);
        Assert.NotNull(clamped);
        Assert.Equal(10, clamped.Value.Start);
        Assert.Equal(40, clamped.Value.End);

        Assert.Null(NestedSilenceScanner.MapNestedRangeToCarrier(60, 30, 30, hop, 30));
    }

    [Fact]
    public void MapNestedRangeToCarrier_MismatchedFps_MapsInSeconds_NotFrames()
    {
        // Nested 24 fps inside a 30 fps parent: nested frame 24 = 1 s lands on parent frame 30.
        var hop = new NestedSilenceScanner.CarrierHop(
            ChildFps: 24, StartFrame: 0, EndFrame: 300,
            TrimStartFrame: 0, Speed: 1.0, ParentFps: 30);

        var mapped = NestedSilenceScanner.MapNestedRangeToCarrier(24, 48, 24, hop, 30);

        Assert.NotNull(mapped);
        Assert.Equal(30, mapped.Value.Start);
        Assert.Equal(60, mapped.Value.End);
    }

    [Fact]
    public async Task ScanAsync_MapsSilenceThroughCarrierToParentFrames()
    {
        // 4 s at 16 kHz: 1 s tone, 2 s silence, 1 s tone. Minimum pause 0.5 s keeps the
        // middle; padding nudges the edges. Carrier starts at parent frame 30 (fps 30),
        // so the silence must surface around parent frames 60..120, not nested frames.
        const int rate = SilenceDetector.SampleRate;
        var samples = Concat(Tone(1.0, 0.5f), new float[rate * 2], Tone(1.0, 0.5f));
        var mediaPath = Path.Combine(Path.GetTempPath(), $"temno_wp172_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(mediaPath, new byte[16]);
            var nested = NestedTimeline("T-NEST", Fps, InnerAudioClip("inner", 0, 120));
            var parent = ParentWithCarrier("carrier", nested.Id, startFrame: 30, durationFrames: 120);

            var plan = NestedSilenceScanner.Plan(
                parent, id => id == nested.Id ? nested : null, _ => mediaPath);
            Assert.Single(plan);

            var hits = await new NestedSilenceScanner(new ConstantAudioSource(samples))
                .ScanAsync(plan);

            var hit = Assert.Single(hits);
            Assert.Equal(0, hit.ParentTrackIndex);
            Assert.Equal("carrier", hit.CarrierClipId);
            // Silence spans nested seconds ~1..3 = frames 30..90; +30 carrier offset.
            Assert.InRange(hit.ParentRange.Start, 55, 65);
            Assert.InRange(hit.ParentRange.End, 115, 125);
        }
        finally
        {
            try { if (File.Exists(mediaPath)) File.Delete(mediaPath); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_UnreadableFile_SkipsInsteadOfThrowing()
    {
        var nested = NestedTimeline("T-NEST", Fps, InnerAudioClip("inner", 0, 120));
        var parent = ParentWithCarrier("carrier", nested.Id, startFrame: 0, durationFrames: 120);
        // Resolve names a path whose file is gone before the scan runs. DetectAsync throws
        // FileNotFoundException (not InvalidOperationException); ScanAsync must skip it.
        var plan = NestedSilenceScanner.Plan(
            parent, id => id == nested.Id ? nested : null,
            _ => Path.Combine(Path.GetTempPath(), $"gone_{Guid.NewGuid():N}.wav"));

        var hits = await new NestedSilenceScanner().ScanAsync([Assert.Single(plan)]);

        Assert.Empty(hits);
    }

    private static Timeline NestedTimeline(string id, int fps, Clip inner) => new()
    {
        Id = id,
        Fps = fps,
        Tracks = [new Track { Type = ClipType.audio, Clips = [inner] }],
    };

    private static Timeline ParentWithCarrier(string carrierId, string nestedId, int startFrame, int durationFrames) => new()
    {
        Id = "T-MAIN",
        Fps = Fps,
        Tracks = [new Track
        {
            Type = ClipType.video,
            Clips = [SequenceCarrier(carrierId, nestedId, startFrame, durationFrames)],
        }],
    };

    private static Clip InnerAudioClip(string id, int startFrame, int durationFrames) => new()
    {
        Id = id,
        MediaRef = id,
        MediaType = ClipType.audio,
        SourceClipType = ClipType.audio,
        StartFrame = startFrame,
        DurationFrames = durationFrames,
    };

    private static Clip DirectAudioClip(string id, int startFrame, int durationFrames) => new()
    {
        Id = id,
        MediaRef = id,
        MediaType = ClipType.audio,
        SourceClipType = ClipType.audio,
        StartFrame = startFrame,
        DurationFrames = durationFrames,
    };

    private static Clip SequenceCarrier(string id, string nestedId, int startFrame, int durationFrames) => new()
    {
        Id = id,
        MediaRef = nestedId,
        MediaType = ClipType.sequence,
        SourceClipType = ClipType.sequence,
        StartFrame = startFrame,
        DurationFrames = durationFrames,
    };

    private static float[] Tone(double seconds, float amplitude)
    {
        const int rate = SilenceDetector.SampleRate;
        var samples = new float[(int)Math.Round(seconds * rate)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = amplitude * (float)Math.Sin(2 * Math.PI * 440.0 * i / rate);
        return samples;
    }

    private static float[] Concat(params float[][] parts)
    {
        var result = new float[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }

    private sealed class ConstantAudioSource(float[] samples) : IAudioSource
    {
        // Mono samples on every requested channel, so their length in seconds is the same at any
        // channel count (WP-336: the decoder no longer stretches a short read across the clip).
        public Task<AudioBuffer> ReadAudioAsync(
            string path,
            int sampleRate,
            int channelCount,
            double startSeconds = 0,
            double? durationSeconds = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AudioBuffer(
                samples.SelectMany(sample => Enumerable.Repeat(sample, channelCount)).ToArray(),
                sampleRate,
                channelCount));
    }
}
