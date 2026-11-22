using System.Text;
using System.Text.Json.Nodes;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

// WP-337: remove_words and remove_silence share ripple_delete_ranges' rule. A partner track that holds an unrelated
// clip inside the cut is refused (D-1), where remove_silence used to leave that track out and cut the rest.
[Collection("EnvRegistry")]
public sealed class LinkedCutToolTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "temno-wp337-" + Guid.NewGuid().ToString("N"));

    public LinkedCutToolTests()
    {
        Directory.CreateDirectory(_temp);
        TranscriptDiskCache.SetRootForTests(Path.Combine(_temp, "cache"));
    }

    public void Dispose()
    {
        ProjectHub.ActiveSession = null;
        ToolExecutor.TranscriptionProviderOverride = null;
        TranscriptDiskCache.SetRootForTests(null);
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    // Picture entries carry no sound, so only the audio clip is transcribed or scanned for dead air.
    private ProjectSession Session(string mediaFile)
    {
        var session = ProjectSession.CreateNew(fps: 30);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "Vmedia", Name = "picture", Type = ClipType.video, HasAudio = false,
            Source = MediaSource.External(mediaFile), Duration = 10,
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "Amedia", Name = "speech", Type = ClipType.audio, HasAudio = true,
            Source = MediaSource.External(mediaFile), Duration = 10,
        });
        return session;
    }

    private static Clip Picture(string id, int start, int duration) =>
        new() { Id = id, MediaRef = "Vmedia", MediaType = ClipType.video, StartFrame = start, DurationFrames = duration };

    private static Clip Sound(string id, int start, int duration) =>
        new() { Id = id, MediaRef = "Amedia", MediaType = ClipType.audio, StartFrame = start, DurationFrames = duration };

    private static List<(int Start, int Duration, int Trim)> On(ProjectSession session, int track) =>
        session.ActiveTimeline.Tracks[track].Clips.OrderBy(c => c.StartFrame)
            .Select(c => (c.StartFrame, c.DurationFrames, c.TrimStartFrame)).ToList();

    // The audio clip on track 2 is linked to a short picture on track 0, and an unrelated picture follows it there,
    // inside the range the sound loses.
    private ProjectSession PartnerTrackWithAnUnrelatedClip(string mediaFile, int soundFrames)
    {
        var session = Session(mediaFile);
        Assert.True(EditCommands.AddClips(session,
            [(0, Picture("V", 0, 10)), (0, Picture("Other", 10, soundFrames - 10)), (2, Sound("A", 0, soundFrames))]).Ok);
        Assert.True(EditCommands.LinkClips(session, ["V", "A"]).Ok);
        ProjectHub.ActiveSession = session;
        return session;
    }

    [Fact]
    public void Remove_words_refuses_a_partner_track_holding_an_unrelated_clip_inside_the_cut()
    {
        var file = Path.Combine(_temp, "speech.wav");
        File.WriteAllText(file, "fake audio");
        var session = PartnerTrackWithAnUnrelatedClip(file, 60);
        ToolExecutor.TranscriptionProviderOverride = new FakeTranscriptionProvider(
            FakeTranscriptionProvider.MakeTranscript(["alpha", "bravo", "charlie"]));

        var result = ToolExecutor.Execute("remove_words", new JsonObject { ["matches"] = new JsonArray("bravo") });
        Assert.True(result.IsError);
        Assert.Contains("Nothing was cut: track 0 (V2) holds clip Other (\"picture\") inside the cut", result.Content[0].Text);
        Assert.Equal([(0, 10, 0), (10, 50, 0)], On(session, 0));
        Assert.Equal([(0, 60, 0)], On(session, 2));
    }

    [Fact]
    public void Remove_silence_cuts_a_linked_pair_by_the_same_frames()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var file = WriteSpeechPauseSpeech(Path.Combine(_temp, "pause.wav"));
        var session = Session(file);
        Assert.True(EditCommands.AddClips(session, [(0, Picture("V", 0, 120)), (2, Sound("A", 0, 120))]).Ok);
        Assert.True(EditCommands.LinkClips(session, ["V", "A"]).Ok);
        ProjectHub.ActiveSession = session;

        var result = ToolExecutor.Execute("remove_silence", new JsonObject());
        Assert.False(result.IsError, result.Content[0].Text);
        var removed = JsonNode.Parse(result.Content[0].Text!)!["removedFrames"]!.GetValue<int>();
        Assert.InRange(removed, 30, 60);
        Assert.Equal(120 - removed, On(session, 2).Sum(p => p.Duration));
        Assert.Equal(On(session, 2), On(session, 0));
    }

    [Fact]
    public void Remove_silence_refuses_a_partner_track_holding_an_unrelated_clip_inside_the_cut()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var file = WriteSpeechPauseSpeech(Path.Combine(_temp, "pause.wav"));
        var session = PartnerTrackWithAnUnrelatedClip(file, 120);

        var result = ToolExecutor.Execute("remove_silence", new JsonObject());
        Assert.True(result.IsError);
        Assert.Contains("Nothing was cut: track 0 (V2) holds clip Other (\"picture\") inside the cut", result.Content[0].Text);
        Assert.Equal([(0, 10, 0), (10, 110, 0)], On(session, 0));
        Assert.Equal([(0, 120, 0)], On(session, 2));
    }

    // A multicam camera whose scratch audio is silent, and the group's mic: the camera's track has the most dead air,
    // but a cut there also cuts the mic, so only the mic's own pauses may go (the WP-337 T1 finding, GPT-6 Astra).
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 30)]
    public void Remove_silence_never_cuts_a_multicam_mic_where_it_is_audible(bool micPauses, int minimumRemoved)
    {
        if (!FfmpegFixture.IsAvailable) return;
        var camera = WriteWav(Path.Combine(_temp, "camera.wav"), _ => false);
        var mic = micPauses
            ? WriteSpeechPauseSpeech(Path.Combine(_temp, "mic.wav"))
            : WriteWav(Path.Combine(_temp, "mic.wav"), _ => true);
        var session = ProjectSession.CreateNew(fps: 30);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "Cmedia", Name = "camera", Type = ClipType.video, HasAudio = true, Source = MediaSource.External(camera), Duration = 4,
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "Mmedia", Name = "mic", Type = ClipType.audio, HasAudio = true, Source = MediaSource.External(mic), Duration = 4,
        });
        // Placed as manage_multicam places them: grouped, and with no linked audio partner.
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip { Id = "Camera", MediaRef = "Cmedia", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 120, MulticamGroupId = "G" });
        session.ActiveTimeline.Tracks[2].Clips.Add(new Clip { Id = "Mic", MediaRef = "Mmedia", MediaType = ClipType.audio, StartFrame = 0, DurationFrames = 120, MulticamGroupId = "G" });
        ProjectHub.ActiveSession = session;

        var result = ToolExecutor.Execute("remove_silence", new JsonObject());
        if (!micPauses)
        {
            Assert.True(result.IsError);
            Assert.Contains("No dead air to remove: every quiet section is audible in another clip the cut would reach", result.Content[0].Text);
            Assert.Equal([(0, 120, 0)], On(session, 0));
            Assert.Equal([(0, 120, 0)], On(session, 2));
            return;
        }
        Assert.False(result.IsError, result.Content[0].Text);
        var removed = JsonNode.Parse(result.Content[0].Text!)!["removedFrames"]!.GetValue<int>();
        Assert.InRange(removed, minimumRemoved, 60);
        Assert.Equal(On(session, 0), On(session, 2));
    }

    // One second of tone, two of silence, one of tone: 16-bit mono PCM at 16 kHz.
    private static string WriteSpeechPauseSpeech(string path) => WriteWav(path, i => i < 16_000 || i >= 16_000 * 3);

    private static string WriteWav(string path, Func<int, bool> loud)
    {
        const int rate = 16_000;
        var samples = new short[rate * 4];
        for (var i = 0; i < samples.Length; i++)
        {
            if (!loud(i)) continue;
            samples[i] = (short)(16_000 * Math.Sin(2 * Math.PI * 440 * i / rate));
        }
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var s in samples)
            writer.Write(s);
        return path;
    }
}
