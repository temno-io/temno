using System.Linq;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-162. Repair path for timelines built before WP-157: <c>add_clips</c> spawns the linked audio
/// partner going forward, but clips already on a timeline stay permanently silent with no way out
/// short of re-adding them. <c>RestoreLinkedAudio</c> manufactures the missing audio half.
/// </summary>
public class RestoreLinkedAudioTests
{
    private const string VodRef = "VOD";
    private const string SilentRef = "SILENT";

    private static ProjectSession SessionWithMedia()
    {
        var session = ProjectSession.CreateNew();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = VodRef,
            Name = "source VOD",
            Type = ClipType.video,
            Duration = 120,
            HasAudio = true,
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = SilentRef,
            Name = "silent clip",
            Type = ClipType.video,
            Duration = 120,
            HasAudio = false,
        });
        return session;
    }

    private static Clip VideoClip(string mediaRef, int start = 0, int duration = 60) => new()
    {
        MediaRef = mediaRef,
        MediaType = ClipType.video,
        SourceClipType = ClipType.video,
        StartFrame = start,
        DurationFrames = duration,
    };

    private static void PlaceOnVideo(ProjectSession session, Clip clip)
    {
        session.ActiveTimeline.Tracks[0].Clips.Add(clip);
        session.ActiveTimeline.Tracks[0].Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
    }

    private static Track FirstAudioTrack(ProjectSession session)
        => session.ActiveTimeline.Tracks.First(t => t.Type == ClipType.audio);

    [Fact]
    public void Restore_PreWp157Video_CreatesLinkedPartner()
    {
        var session = SessionWithMedia();
        var video = VideoClip(VodRef, start: 10, duration: 50);
        PlaceOnVideo(session, video);

        var (restored, skipped) = EditCommands.RestoreLinkedAudio(session, [video.Id]);

        var single = Assert.Single(restored);
        Assert.Equal(video.Id, single.ClipId);
        var audio = FirstAudioTrack(session).Clips.Single();
        Assert.Equal(single.AudioClipId, audio.Id);
        Assert.Equal(ClipType.audio, audio.MediaType);
        Assert.Equal(VodRef, audio.MediaRef);
        Assert.Equal(video.StartFrame, audio.StartFrame);
        Assert.Equal(video.DurationFrames, audio.DurationFrames);
        Assert.Equal(video.LinkGroupId, audio.LinkGroupId);
        Assert.False(string.IsNullOrEmpty(video.LinkGroupId));
        Assert.Empty(skipped);
    }

    [Fact]
    public void Restore_AlreadyLinked_IsSkippedNotDuplicated()
    {
        var session = SessionWithMedia();
        var video = VideoClip(VodRef);
        Assert.True(EditCommands.AddClips(session, [(0, video)]).Ok);
        var before = FirstAudioTrack(session).Clips.Count;

        var (restored, skipped) = EditCommands.RestoreLinkedAudio(session, [video.Id]);

        Assert.Empty(restored);
        Assert.Equal(before, FirstAudioTrack(session).Clips.Count);
        Assert.Contains(skipped, s => s.ClipId == video.Id);
    }

    [Fact]
    public void Restore_SilentVideo_IsSkipped()
    {
        var session = SessionWithMedia();
        var video = VideoClip(SilentRef);
        PlaceOnVideo(session, video);

        var (restored, skipped) = EditCommands.RestoreLinkedAudio(session, [video.Id]);

        Assert.Empty(restored);
        Assert.Empty(FirstAudioTrack(session).Clips);
        Assert.Contains(skipped, s => s.ClipId == video.Id);
    }

    [Fact]
    public void Restore_UnknownMediaRef_IsSkipped()
    {
        var session = SessionWithMedia();
        var video = VideoClip("NOT-IN-MANIFEST");
        PlaceOnVideo(session, video);

        var (restored, skipped) = EditCommands.RestoreLinkedAudio(session, [video.Id]);

        Assert.Empty(restored);
        Assert.Contains(skipped, s => s.ClipId == video.Id);
    }

    [Fact]
    public void Restore_UnknownClipId_IsSkipped()
    {
        var session = SessionWithMedia();

        var (restored, skipped) = EditCommands.RestoreLinkedAudio(session, ["NOPE-NOPE"]);

        Assert.Empty(restored);
        var single = Assert.Single(skipped);
        Assert.Equal("NOPE-NOPE", single.ClipId);
    }

    [Fact]
    public void Restore_AudioClip_IsSkipped()
    {
        var session = SessionWithMedia();
        var video = VideoClip(VodRef);
        PlaceOnVideo(session, video);
        var (first, _) = EditCommands.RestoreLinkedAudio(session, [video.Id]);
        Assert.Single(first);
        var audioId = FirstAudioTrack(session).Clips.Single().Id;

        var (restored, skipped) = EditCommands.RestoreLinkedAudio(session, [audioId]);

        Assert.Empty(restored);
        Assert.Contains(skipped, s => s.ClipId == audioId);
    }

    [Fact]
    public void Restore_DoesNotOverwriteOccupiedAudio()
    {
        var session = SessionWithMedia();
        // Music already on Audio 1 over the video's range: the partner must go elsewhere.
        var music = new Clip
        {
            MediaRef = VodRef,
            MediaType = ClipType.audio,
            StartFrame = 0,
            DurationFrames = 600,
        };
        FirstAudioTrack(session).Clips.Add(music);
        var video = VideoClip(VodRef, start: 10, duration: 50);
        PlaceOnVideo(session, video);

        var (restored, _) = EditCommands.RestoreLinkedAudio(session, [video.Id]);

        var single = Assert.Single(restored);
        Assert.NotEqual(
            session.ActiveTimeline.Tracks.IndexOf(FirstAudioTrack(session)),
            single.AudioTrackIndex);
        Assert.Equal(music.StartFrame, FirstAudioTrack(session).Clips.Single(c => c.Id == music.Id).StartFrame);
    }

    [Fact]
    public void Restore_PartialBatch_RepairsOneSkipsOne()
    {
        var session = SessionWithMedia();
        var broken = VideoClip(VodRef, start: 0, duration: 60);
        var silent = VideoClip(SilentRef, start: 100, duration: 60);
        PlaceOnVideo(session, broken);
        PlaceOnVideo(session, silent);

        var (restored, skipped) = EditCommands.RestoreLinkedAudio(session, [broken.Id, silent.Id]);

        Assert.Equal([broken.Id], restored.Select(r => r.ClipId).ToList());
        Assert.Equal([silent.Id], skipped.Select(s => s.ClipId).ToList());
    }

    [Fact]
    public void Restore_EmptyInput_ChangesNothing()
    {
        var session = SessionWithMedia();
        Assert.False(session.CanUndo());

        var (restored, skipped) = EditCommands.RestoreLinkedAudio(session, []);

        Assert.Empty(restored);
        Assert.Empty(skipped);
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void Restore_AllSkipped_LeavesNoUndoStep()
    {
        var session = SessionWithMedia();
        var video = VideoClip(SilentRef);
        PlaceOnVideo(session, video);
        Assert.False(session.CanUndo());

        var (restored, _) = EditCommands.RestoreLinkedAudio(session, [video.Id]);

        Assert.Empty(restored);
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void Restore_Success_IsOneUndoStep()
    {
        var session = SessionWithMedia();
        var first = VideoClip(VodRef, start: 0, duration: 60);
        var second = VideoClip(VodRef, start: 100, duration: 60);
        PlaceOnVideo(session, first);
        PlaceOnVideo(session, second);
        Assert.False(session.CanUndo());

        var (restored, _) = EditCommands.RestoreLinkedAudio(session, [first.Id, second.Id]);

        Assert.Equal(2, restored.Count);
        Assert.True(session.CanUndo());
        session.Undo();
        Assert.DoesNotContain(
            session.ActiveTimeline.Tracks.SelectMany(t => t.Clips),
            c => c.MediaType == ClipType.audio);
        Assert.False(session.CanUndo());
    }
}
