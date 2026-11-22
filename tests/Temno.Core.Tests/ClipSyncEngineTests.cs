using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Engine placement tests use a fake audio source so FFmpeg is not required.
/// Correlation is validated separately with known delays.
/// </summary>
public sealed class ClipSyncEngineTests
{
    [Fact]
    public async Task Sync_MovesTargetByCorrelatedLag()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "ref",
            MediaRef = "mRef",
            MediaType = ClipType.video,
            StartFrame = 100,
            DurationFrames = 90,
            Speed = 1,
        }).Ok);
        Assert.True(EditCommands.AddClip(session, 1, new Clip
        {
            Id = "tgt",
            MediaRef = "mTgt",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 90,
            Speed = 1,
        }).Ok);

        // Shared tone; target media is delayed 20 hops = 0.2 s → 6 frames at 30 fps.
        const int delayHops = 20;
        var refPcm = MakeTone(16_000, seconds: 1.0);
        var tgtPcm = new float[refPcm.Length + delayHops * 160]; // pad (hop at 16k = 160 samples)
        Array.Copy(refPcm, 0, tgtPcm, delayHops * 160, refPcm.Length);

        var fake = new DictAudioSource(new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ref.wav"] = refPcm,
            ["tgt.wav"] = tgtPcm,
        });

        string? Resolve(string mediaRef) => mediaRef switch
        {
            "mRef" => "ref.wav",
            "mTgt" => "tgt.wav",
            _ => null,
        };

        var engine = new ClipSyncEngine(Resolve, fake);
        var report = await engine.SyncAsync(session, "ref", ["tgt"], ClipSyncEngine.SyncMode.audio,
            searchWindowSeconds: 5, minConfidence: 0.5);

        Assert.NotEmpty(report.Synced);
        var hit = Assert.Single(report.Synced);
        Assert.Equal("tgt", hit.ClipId);
        // Target delayed in media → start later on timeline relative to ref@100.
        Assert.True(session.ActiveTimeline.Tracks[1].Clips[0].StartFrame > 100
            || hit.OffsetFrames != 0);
        Assert.True(session.CanUndo());
    }

    [Fact]
    public async Task TimecodeMode_FailsHonestly()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "a", MediaRef = "m", MediaType = ClipType.video, DurationFrames = 30,
        }).Ok);
        Assert.True(EditCommands.AddClip(session, 1, new Clip
        {
            Id = "b", MediaRef = "m2", MediaType = ClipType.video, DurationFrames = 30,
        }).Ok);

        var engine = new ClipSyncEngine(_ => "x.wav", new DictAudioSource([]));
        var report = await engine.SyncAsync(session, "a", ["b"], ClipSyncEngine.SyncMode.timecode);
        Assert.Empty(report.Synced);
        Assert.Contains(report.Failures, f => f.ClipId == "b" && f.Reason.Contains("timecode", StringComparison.OrdinalIgnoreCase));
    }

    private static float[] MakeTone(int sampleRate, double seconds)
    {
        var n = (int)(sampleRate * seconds);
        var s = new float[n];
        for (var i = 0; i < n; i++)
            s[i] = (float)Math.Sin(2 * Math.PI * 440 * i / sampleRate);
        return s;
    }

    private sealed class DictAudioSource(Dictionary<string, float[]> files) : IAudioSource
    {
        public Task<AudioBuffer> ReadAudioAsync(
            string path,
            int sampleRate,
            int channelCount,
            double startSeconds = 0,
            double? durationSeconds = null,
            CancellationToken cancellationToken = default)
        {
            if (!files.TryGetValue(path, out var full))
                throw new FileNotFoundException(path);

            var start = (int)Math.Round(startSeconds * sampleRate);
            start = Math.Clamp(start, 0, full.Length);
            var count = durationSeconds is { } d
                ? (int)Math.Round(d * sampleRate)
                : full.Length - start;
            count = Math.Clamp(count, 0, full.Length - start);
            var slice = new float[count];
            Array.Copy(full, start, slice, 0, count);
            return Task.FromResult(new AudioBuffer(slice, sampleRate, 1));
        }
    }
}
