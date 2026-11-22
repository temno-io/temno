using Temno.Core.Export;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

public sealed class SourceTimecodeTests
{
    [Theory]
    [InlineData("00:00:10:00", 30, 300, false)]
    [InlineData("01:00:00:00", 24, 86400, false)]
    [InlineData("00:00:01;15", 30, 45, true)]
    public void TryParseSmpte_ParsesComponents(string text, int quanta, int expectedFrame, bool drop)
    {
        Assert.True(SourceTimecode.TryParseSmpte(text, quanta, out var tc));
        Assert.Equal(expectedFrame, tc.Frame);
        Assert.Equal(quanta, tc.Quanta);
        Assert.Equal(drop, tc.DropFrame);
        Assert.Equal(expectedFrame / (double)quanta, tc.Seconds, 6);
    }

    [Fact]
    public void TimecodeAlignedStart_MatchesMacFormula()
    {
        var refTc = new SourceTimecode(frame: 0, quanta: 30, dropFrame: false);
        var targetTc = new SourceTimecode(frame: 300, quanta: 30, dropFrame: false); // +10s
        var start = ClipSyncEngine.TimecodeAlignedStart(
            refStartFrame: 100,
            refTrimStartFrame: 0,
            refSpeed: 1,
            refTimecode: refTc,
            targetTrimStartFrame: 0,
            targetTimecode: targetTc,
            fps: 30);
        // target clock is 10s later → lands 100 + 300 = 400
        Assert.Equal(400, start);
    }

    [Fact]
    public async Task ClipSyncEngine_TimecodeMode_MovesTarget()
    {
        var session = ProjectSession.CreateNew();
        session.ActiveTimeline.Fps = 30;
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            Id = "ref",
            MediaRef = "a",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 30,
        });
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            Id = "tgt",
            MediaRef = "b",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 30,
        });

        Task<SourceTimecode?> Read(string path, CancellationToken _)
        {
            SourceTimecode? tc = path switch
            {
                "/a.mp4" => new SourceTimecode(0, 30, false),
                "/b.mp4" => new SourceTimecode(150, 30, false), // +5s
                _ => null,
            };
            return Task.FromResult(tc);
        }

        string? Resolve(string id) => id switch
        {
            "a" => "/a.mp4",
            "b" => "/b.mp4",
            _ => null,
        };

        var engine = new ClipSyncEngine(Resolve, audioSource: null, readTimecode: Read);
        var report = await engine.SyncAsync(
            session, "ref", ["tgt"], ClipSyncEngine.SyncMode.timecode);

        Assert.Empty(report.Failures);
        Assert.Single(report.Synced);
        Assert.Equal(nameof(ClipSyncEngine.SyncMethod.timecode), report.Synced[0].Method);
        var moved = session.ActiveTimeline.Tracks[0].Clips.First(c => c.Id == "tgt");
        Assert.Equal(150, moved.StartFrame);
    }

    [Fact]
    public async Task ClipSyncEngine_TimecodeMode_UnavailableSource_ReportsFailure()
    {
        var session = ProjectSession.CreateNew();
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            Id = "ref", MediaRef = "a", MediaType = ClipType.video, DurationFrames = 10,
        });
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            Id = "tgt", MediaRef = "b", MediaType = ClipType.video, DurationFrames = 10,
        });

        var engine = new ClipSyncEngine(
            id => id == "a" ? "/a.mp4" : "/b.mp4",
            readTimecode: (_, _) => Task.FromResult<SourceTimecode?>(null));
        var report = await engine.SyncAsync(session, "ref", ["tgt"], ClipSyncEngine.SyncMode.timecode);
        Assert.NotEmpty(report.Failures);
        Assert.Contains(report.Failures, f => f.ClipId == "tgt");
    }

    [Fact]
    public async Task MulticamMemberSync_FallsBackToTimecodeWhenNoAudio()
    {
        // Paths that exist but yield no audio — use fake TC reader.
        var root = Path.Combine(Path.GetTempPath(), "temno-tc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var master = Path.Combine(root, "m.mp4");
        var other = Path.Combine(root, "o.mp4");
        await File.WriteAllBytesAsync(master, [0]);
        await File.WriteAllBytesAsync(other, [0]);
        try
        {
            Task<SourceTimecode?> Read(string path, CancellationToken _)
            {
                if (path == master) return Task.FromResult<SourceTimecode?>(new SourceTimecode(0, 30, false));
                if (path == other) return Task.FromResult<SourceTimecode?>(new SourceTimecode(60, 30, false)); // +2s
                return Task.FromResult<SourceTimecode?>(null);
            }

            // Silent audio source forces envelope failure.
            var silent = new SilentAudioSource();
            var outcome = await MulticamMemberSync.SyncAsync(
                "m",
                [("m", null), ("o", null)],
                id => id == "m" ? master : other,
                audioSource: silent,
                readTimecode: Read);

            Assert.True(outcome.Maps.ContainsKey("o"));
            Assert.Equal(2.0, outcome.Maps["o"].OffsetSeconds, 3);
            Assert.Equal(1, outcome.Maps["o"].Confidence);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    private sealed class SilentAudioSource : IAudioSource
    {
        public Task<AudioBuffer> ReadAudioAsync(
            string path,
            int sampleRate,
            int channelCount,
            double startSeconds = 0,
            double? durationSeconds = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AudioBuffer(Array.Empty<float>(), sampleRate, channelCount));
    }
}
