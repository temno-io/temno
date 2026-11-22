using System.Text.Json;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-235: multicam splits must preserve styled clip state. The oracle is the
/// pre-split clip itself: sampled opacity, transform channels, crop and volume
/// at every timeline frame must match before vs after, on both sides of each cut.
/// </summary>
public sealed class MulticamSplitPreservationTests
{
    private sealed class StyledFixture
    {
        public ProjectSession Session { get; init; } = null!;
        public string GroupId { get; init; } = "";
        public string AudioClipId { get; init; } = "";
        public Dictionary<string, double> Durations { get; init; } = new();
    }

    private sealed class SampleRow
    {
        public double Opacity;
        public double RawOpacity;
        public double Rotation;
        public (double X, double Y) Size;
        public (double X, double Y) TopLeft;
        public Crop Crop = new();
        public double Volume;
        public double Fade;
        public double EvParam;
    }

    private static StyledFixture BuildStyled(double speed = 1.0, int trimStart = 0)
    {
        var session = ProjectSession.CreateNew();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "camA", Type = ClipType.video, Duration = 10,
            Source = MediaSource.External("/a.mp4"),
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "camB", Type = ClipType.video, Duration = 10,
            Source = MediaSource.External("/b.mp4"),
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "mic1", Name = "Mic", Type = ClipType.audio, Duration = 10, HasAudio = true,
            Source = MediaSource.External("/m.wav"),
        });
        var maps = new Dictionary<string, MulticamSyncMap>
        {
            ["camA"] = new() { OffsetSeconds = 0, Confidence = 1 },
            ["camB"] = new() { OffsetSeconds = 0, Confidence = 1 },
            ["mic1"] = new() { OffsetSeconds = 0, Confidence = 1, Locked = true },
        };
        var durations = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Duration);
        var types = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Type);
        Assert.True(MulticamCommands.CreateGroup(
            session,
            [
                new MulticamCommands.MemberSpec { MediaRef = "camA", Kind = MulticamMemberKind.both, AngleLabel = "a" },
                new MulticamCommands.MemberSpec { MediaRef = "camB", Kind = MulticamMemberKind.angle, AngleLabel = "b" },
                new MulticamCommands.MemberSpec { MediaRef = "mic1", Kind = MulticamMemberKind.mic, AngleLabel = "mic" },
            ],
            maps, "camA", durations, types, "MC", 0, out var created).Ok);

        var program = ProgramVideos(session, created.GroupId).Single();
        Assert.Equal(0, program.StartFrame);
        Assert.Equal(300, program.EndFrame);

        program.Speed = speed;
        program.TrimStartFrame = trimStart;
        program.BlendMode = "multiply";
        program.FadeInFrames = 7;
        program.FadeOutFrames = 7;
        program.Effects =
        [
            new Effect
            {
                Type = "color.exposure",
                Params = new Dictionary<string, EffectParam>(StringComparer.Ordinal)
                {
                    ["ev"] = new EffectParam
                    {
                        Value = 1.5,
                        Track = new KeyframeTrack<double>
                        {
                            Keyframes =
                            [
                                new Keyframe<double> { Frame = 0, Value = -1.0, InterpolationOut = Interpolation.linear },
                                new Keyframe<double> { Frame = 299, Value = 2.0, InterpolationOut = Interpolation.linear },
                            ],
                        },
                    },
                },
            },
        ];
        program.OpacityTrack = new KeyframeTrack<double>
        {
            Keyframes =
            [
                new Keyframe<double> { Frame = 0, Value = 0.2, InterpolationOut = Interpolation.linear },
                new Keyframe<double> { Frame = 150, Value = 0.9, InterpolationOut = Interpolation.linear },
                new Keyframe<double> { Frame = 299, Value = 0.4, InterpolationOut = Interpolation.linear },
            ],
        };
        program.PositionTrack = new KeyframeTrack<AnimPair>
        {
            Keyframes =
            [
                new Keyframe<AnimPair> { Frame = 0, Value = new AnimPair(0, 0), InterpolationOut = Interpolation.linear },
                new Keyframe<AnimPair> { Frame = 299, Value = new AnimPair(100, 50), InterpolationOut = Interpolation.linear },
            ],
        };
        program.ScaleTrack = new KeyframeTrack<AnimPair>
        {
            Keyframes =
            [
                new Keyframe<AnimPair> { Frame = 0, Value = new AnimPair(1, 1), InterpolationOut = Interpolation.linear },
                new Keyframe<AnimPair> { Frame = 150, Value = new AnimPair(2, 1.5), InterpolationOut = Interpolation.linear },
            ],
        };
        program.RotationTrack = new KeyframeTrack<double>
        {
            Keyframes =
            [
                new Keyframe<double> { Frame = 0, Value = 0, InterpolationOut = Interpolation.linear },
                new Keyframe<double> { Frame = 299, Value = 90, InterpolationOut = Interpolation.linear },
            ],
        };
        program.CropTrack = new KeyframeTrack<Crop>
        {
            Keyframes =
            [
                new Keyframe<Crop> { Frame = 0, Value = new Crop(), InterpolationOut = Interpolation.linear },
                new Keyframe<Crop> { Frame = 299, Value = new Crop { Left = 0.1, Top = 0.05, Right = 0.1, Bottom = 0.05 }, InterpolationOut = Interpolation.linear },
            ],
        };
        program.VolumeTrack = new KeyframeTrack<double>
        {
            Keyframes =
            [
                new Keyframe<double> { Frame = 0, Value = -6.0, InterpolationOut = Interpolation.linear },
                new Keyframe<double> { Frame = 299, Value = 3.0, InterpolationOut = Interpolation.linear },
            ],
        };
        program.WordTimings =
        [
            new WordTiming { Text = "alpha", StartFrame = 0, EndFrame = 30 },
            new WordTiming { Text = "beta", StartFrame = 30, EndFrame = 60 },
            new WordTiming { Text = "gamma", StartFrame = 200, EndFrame = 260 },
        ];

        var audio = session.ActiveTimeline.Tracks.SelectMany(t => t.Clips)
            .First(c => c.MulticamGroupId == created.GroupId && c.MediaType == ClipType.audio);
        audio.Volume = 0.8;
        audio.VolumeTrack = new KeyframeTrack<double>
        {
            Keyframes =
            [
                new Keyframe<double> { Frame = 0, Value = 0.0, InterpolationOut = Interpolation.linear },
                new Keyframe<double> { Frame = 299, Value = -3.0, InterpolationOut = Interpolation.linear },
            ],
        };
        audio.Effects = [new Effect { Type = Clip.DenoiseEffectType }];

        return new StyledFixture
        {
            Session = session,
            GroupId = created.GroupId,
            AudioClipId = audio.Id,
            Durations = durations,
        };
    }

    private static List<Clip> ProgramVideos(ProjectSession session, string groupId)
        => session.ActiveTimeline.Tracks.SelectMany(t => t.Clips)
            .Where(c => c.MulticamGroupId == groupId && c.MediaType != ClipType.audio)
            .OrderBy(c => c.StartFrame)
            .ToList();

    private static Clip ClipAt(IReadOnlyList<Clip> clips, int frame)
        => clips.First(c => c.StartFrame <= frame && frame < c.EndFrame);

    private static SampleRow Sample(Clip clip, int timelineFrame)
    {
        var ev = clip.Effects!.First(e => e.Type == "color.exposure").Params["ev"];
        return new SampleRow
        {
            Opacity = clip.OpacityAt(timelineFrame),
            RawOpacity = clip.RawOpacityAt(timelineFrame),
            Rotation = clip.RotationAt(timelineFrame),
            Size = clip.SizeAt(timelineFrame),
            TopLeft = clip.TopLeftAt(timelineFrame),
            Crop = clip.CropAt(timelineFrame),
            Volume = clip.VolumeAt(timelineFrame),
            Fade = clip.FadeMultiplier(timelineFrame),
            EvParam = ev.Resolved(timelineFrame - clip.StartFrame, 0),
        };
    }

    private static Dictionary<int, SampleRow> Snapshot(Clip clip)
    {
        var rows = new Dictionary<int, SampleRow>();
        for (var f = clip.StartFrame; f < clip.EndFrame; f++)
            rows[f] = Sample(clip, f);
        return rows;
    }

    private static void AssertOracle(
        Dictionary<int, SampleRow> before, IReadOnlyList<Clip> after, string context)
    {
        foreach (var (frame, want) in before)
        {
            var clip = ClipAt(after, frame);
            var got = Sample(clip, frame);
            Assert.Equal(want.Opacity, got.Opacity, 1e-9);
            Assert.Equal(want.RawOpacity, got.RawOpacity, 1e-9);
            Assert.Equal(want.Rotation, got.Rotation, 1e-9);
            Assert.Equal(want.Size.X, got.Size.X, 1e-9);
            Assert.Equal(want.Size.Y, got.Size.Y, 1e-9);
            Assert.Equal(want.TopLeft.X, got.TopLeft.X, 1e-9);
            Assert.Equal(want.TopLeft.Y, got.TopLeft.Y, 1e-9);
            Assert.Equal(want.Crop.Left, got.Crop.Left, 1e-9);
            Assert.Equal(want.Crop.Top, got.Crop.Top, 1e-9);
            Assert.Equal(want.Crop.Right, got.Crop.Right, 1e-9);
            Assert.Equal(want.Crop.Bottom, got.Crop.Bottom, 1e-9);
            Assert.Equal(want.Volume, got.Volume, 1e-9);
            Assert.Equal(want.Fade, got.Fade, 1e-9);
            Assert.Equal(want.EvParam, got.EvParam, 1e-9);
        }
        Assert.All(after, c => Assert.Equal("multiply", c.BlendMode));
        Assert.All(after, c =>
            Assert.Equal(1.5, c.Effects?.First(e => e.Type == "color.exposure")
                .Params["ev"].Value));
    }

    [Fact]
    public void ChangeCam_30_60_PreservesStyledStateOnAllFragments()
    {
        var fx = BuildStyled();
        var before = Snapshot(ProgramVideos(fx.Session, fx.GroupId).Single());

        Assert.True(MulticamCommands.ChangeCam(
            fx.Session, fx.GroupId, 30, 60, "b", fx.Durations, out var outcome).Ok);
        Assert.True(outcome.Switched >= 1);

        var after = ProgramVideos(fx.Session, fx.GroupId);
        Assert.Equal(3, after.Count);
        Assert.Equal(new[] { (0, 30), (30, 60), (60, 300) },
            after.Select(c => (c.StartFrame, c.EndFrame)));
        Assert.Equal("camA", after[0].MediaRef);
        Assert.Equal("camB", after[1].MediaRef);
        Assert.Equal("camA", after[2].MediaRef);

        // Fade edges: head keeps fade-in only, interior clears both, tail keeps fade-out only.
        Assert.Equal((7, 0), (after[0].FadeInFrames, after[0].FadeOutFrames));
        Assert.Equal((0, 0), (after[1].FadeInFrames, after[1].FadeOutFrames));
        Assert.Equal((0, 7), (after[2].FadeInFrames, after[2].FadeOutFrames));

        AssertOracle(before, after, "30-60");

        // Switched fragment retargets source trim to the new angle (group-frame continuity).
        Assert.Equal(30, after[1].TrimStartFrame);
        Assert.Equal(60, after[2].TrimStartFrame);
    }

    [Fact]
    public void ChangeCam_NonUnitSpeedAndTrim_PreservesSampledState()
    {
        var fx = BuildStyled(speed: 1.5, trimStart: 45);
        var before = Snapshot(ProgramVideos(fx.Session, fx.GroupId).Single());

        Assert.True(MulticamCommands.ChangeCam(
            fx.Session, fx.GroupId, 30, 60, "b", fx.Durations, out _).Ok);

        var after = ProgramVideos(fx.Session, fx.GroupId);
        Assert.Equal(3, after.Count);
        AssertOracle(before, after, "speed-1.5");

        // Trim advances by Round(leftDuration * Speed), not by the keyframe shift.
        Assert.Equal(45 + (int)Math.Round(30 * 1.5), after[1].TrimStartFrame);
        Assert.Equal(45 + (int)Math.Round(60 * 1.5), after[2].TrimStartFrame);
    }

    [Fact]
    public void ChangeCam_SameAngleRejoin_RestoresEdgeFadeAndSamples()
    {
        var fx = BuildStyled();
        var before = Snapshot(ProgramVideos(fx.Session, fx.GroupId).Single());

        Assert.True(MulticamCommands.ChangeCam(
            fx.Session, fx.GroupId, 30, 60, "a", fx.Durations, out var outcome).Ok);
        Assert.True(outcome.Merged >= 1);

        var after = ProgramVideos(fx.Session, fx.GroupId);
        var clip = Assert.Single(after);
        Assert.Equal((0, 300), (clip.StartFrame, clip.EndFrame));
        Assert.Equal("camA", clip.MediaRef);
        Assert.Equal((7, 7), (clip.FadeInFrames, clip.FadeOutFrames));
        AssertOracle(before, after, "same-angle-rejoin");
    }

    [Fact]
    public void ChangeCam_BeginningEndFullRange_PreservesState()
    {
        foreach (var (s, e, spans) in new (int, int, (int, int)[])[]
                 {
                     (0, 30, [(0, 30), (30, 300)]),
                     (270, 300, [(0, 270), (270, 300)]),
                     (0, 300, [(0, 300)]),
                 })
        {
            var fx = BuildStyled();
            var before = Snapshot(ProgramVideos(fx.Session, fx.GroupId).Single());

            Assert.True(MulticamCommands.ChangeCam(
                fx.Session, fx.GroupId, s, e, "b", fx.Durations, out _).Ok);

            var after = ProgramVideos(fx.Session, fx.GroupId);
            Assert.Equal(spans, after.Select(c => (c.StartFrame, c.EndFrame)));
            AssertOracle(before, after, $"{s}-{e}");
            if (spans.Length == 1)
                Assert.Equal("camB", after[0].MediaRef);
        }
    }

    [Fact]
    public void ChangeCam_MultipleSwitchesAndLinkedAudio_PreservesState()
    {
        var fx = BuildStyled();
        var before = Snapshot(ProgramVideos(fx.Session, fx.GroupId).Single());
        var audioBefore = AudioSnapshot(fx.Session, fx.AudioClipId);

        Assert.True(MulticamCommands.ChangeCam(
            fx.Session, fx.GroupId, 30, 60, "b", fx.Durations, out _).Ok);
        Assert.True(MulticamCommands.ChangeCam(
            fx.Session, fx.GroupId, 100, 130, "b", fx.Durations, out _).Ok);

        var after = ProgramVideos(fx.Session, fx.GroupId);
        Assert.Equal(new[] { (0, 30), (30, 60), (60, 100), (100, 130), (130, 300) },
            after.Select(c => (c.StartFrame, c.EndFrame)));
        AssertOracle(before, after, "multi");

        // Interior untouched-angle fragment clears both fades.
        Assert.Equal((0, 0), (after[2].FadeInFrames, after[2].FadeOutFrames));

        // Linked audio in the group is not split or restyled.
        var audioAfter = AudioSnapshot(fx.Session, fx.AudioClipId);
        Assert.Equal(audioBefore.Count, audioAfter.Count);
        foreach (var (frame, want) in audioBefore)
            Assert.Equal(want, audioAfter[frame], 1e-9);
        var audio = fx.Session.ActiveTimeline.Tracks.SelectMany(t => t.Clips)
            .First(c => c.Id == fx.AudioClipId);
        Assert.Equal(0.8, audio.Volume, 1e-9);
        Assert.Single(audio.Effects ?? []);
    }

    private static Dictionary<int, double> AudioSnapshot(ProjectSession session, string audioClipId)
    {
        var clip = session.ActiveTimeline.Tracks.SelectMany(t => t.Clips)
            .First(c => c.Id == audioClipId);
        var rows = new Dictionary<int, double>();
        for (var f = clip.StartFrame; f < clip.EndFrame; f++)
            rows[f] = clip.VolumeAt(f);
        return rows;
    }

    [Fact]
    public void SplitFragments_ShareNoMutableState()
    {
        var fx = BuildStyled();
        Assert.True(MulticamCommands.ChangeCam(
            fx.Session, fx.GroupId, 30, 60, "b", fx.Durations, out _).Ok);
        var after = ProgramVideos(fx.Session, fx.GroupId);
        var head = after[0];
        var mid = after[1];

        Assert.False(ReferenceEquals(head.Effects, mid.Effects));
        Assert.False(ReferenceEquals(head.OpacityTrack, mid.OpacityTrack));
        Assert.False(ReferenceEquals(head.Transform, mid.Transform));
        Assert.False(ReferenceEquals(head.Crop, mid.Crop));
        Assert.False(ReferenceEquals(head.WordTimings, mid.WordTimings));

        head.Effects!.First(e => e.Type == "color.exposure").Params["ev"].Value = 99;
        Assert.Equal(1.5, mid.Effects!.First(e => e.Type == "color.exposure").Params["ev"].Value);

        mid.OpacityTrack!.Keyframes[0].Value = -42;
        Assert.Equal(0.2, head.OpacityTrack!.Keyframes[0].Value, 1e-9);

        head.Transform.CenterX = -1;
        Assert.Equal(0.5, mid.Transform.CenterX, 1e-9);

        mid.WordTimings![0].Text = "MUTATED";
        Assert.Equal("alpha", head.WordTimings![0].Text);
    }

    [Fact]
    public void ChangeCam_UndoRedo_SaveReopen_PreservesProject()
    {
        var fx = BuildStyled();
        var program = ProgramVideos(fx.Session, fx.GroupId).Single();
        var before = Snapshot(program);
        var keysBefore = ClipJsonKeys(fx.Session);

        Assert.True(MulticamCommands.ChangeCam(
            fx.Session, fx.GroupId, 30, 60, "b", fx.Durations, out _).Ok);
        var switched = ProgramVideos(fx.Session, fx.GroupId);
        Assert.Equal(3, switched.Count);

        fx.Session.Undo();
        var undone = ProgramVideos(fx.Session, fx.GroupId);
        Assert.Single(undone);
        AssertOracle(before, undone, "undo");

        fx.Session.Redo();
        var redone = ProgramVideos(fx.Session, fx.GroupId);
        Assert.Equal(3, redone.Count);
        AssertOracle(before, redone, "redo");

        var dir = Path.Combine(Path.GetTempPath(), "temno-wp235-" + Guid.NewGuid().ToString("N") + ".palmier");
        try
        {
            fx.Session.Save(dir);
            var reopened = ProjectSession.Open(dir);
            var clips = ProgramVideos(reopened, fx.GroupId);
            Assert.Equal(new[] { (0, 30), (30, 60), (60, 300) },
                clips.Select(c => (c.StartFrame, c.EndFrame)));
            Assert.Equal("camB", clips[1].MediaRef);
            AssertOracle(before, clips, "reopen");
            Assert.Equal(keysBefore, ClipJsonKeys(reopened));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static SortedSet<string> ClipJsonKeys(ProjectSession session)
    {
        var raw = JsonSerializer.Serialize(session.ProjectFile);
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(raw);
        foreach (var timeline in doc.RootElement.GetProperty("timelines").EnumerateArray())
            foreach (var track in timeline.GetProperty("tracks").EnumerateArray())
                foreach (var clip in track.GetProperty("clips").EnumerateArray())
                    foreach (var prop in clip.EnumerateObject())
                        keys.Add(prop.Name);
        return keys;
    }
}
