using System.Linq;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-157. Mac <c>add_clips</c>: "When a video asset with audio is placed on a video track, a linked
/// audio clip is automatically created on an audio track (an existing one if available, otherwise a
/// new one)" (<c>ToolDefinitions.swift:287</c>); <c>insert_clips</c> applies the same push to that
/// audio track (<c>:311</c>). Windows shipped the behaviour on the drag-drop path only, so an agent
/// driving over MCP could not put a video's audio on an audio track at all.
/// </summary>
public class LinkedAudioOnAddClipsTests
{
    private const string MediaRef = "VOD";

    private static ProjectSession SessionWithMedia(bool hasAudio)
    {
        var session = ProjectSession.CreateNew();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = MediaRef,
            Name = "source VOD",
            Type = ClipType.video,
            Duration = 120,
            HasAudio = hasAudio,
        });
        return session;
    }

    private static Clip VideoClip(int start = 0, int duration = 60, int trimStart = 0) => new()
    {
        MediaRef = MediaRef,
        MediaType = ClipType.video,
        SourceClipType = ClipType.video,
        StartFrame = start,
        DurationFrames = duration,
        TrimStartFrame = trimStart,
    };

    private static Track FirstAudioTrack(ProjectSession session)
        => session.ActiveTimeline.Tracks.First(t => t.Type == ClipType.audio);

    [Fact]
    public void AddClips_VideoWithAudio_CreatesLinkedPartnerOnAnAudioTrack()
    {
        var session = SessionWithMedia(hasAudio: true);
        var video = VideoClip(start: 10, duration: 50, trimStart: 7);

        Assert.True(EditCommands.AddClips(session, [(0, video)]).Ok);

        var placedVideo = session.ActiveTimeline.Tracks[0].Clips.Single();
        var audio = FirstAudioTrack(session).Clips.Single();
        Assert.Equal(ClipType.audio, audio.MediaType);
        Assert.Equal(ClipType.video, audio.SourceClipType);
        Assert.Equal(placedVideo.MediaRef, audio.MediaRef);
        Assert.Equal(placedVideo.StartFrame, audio.StartFrame);
        Assert.Equal(placedVideo.DurationFrames, audio.DurationFrames);
        Assert.Equal(placedVideo.TrimStartFrame, audio.TrimStartFrame);
        Assert.False(string.IsNullOrEmpty(placedVideo.LinkGroupId));
        Assert.Equal(placedVideo.LinkGroupId, audio.LinkGroupId);
    }

    [Fact]
    public void AddClips_VideoWithoutAudio_CreatesNoPartner()
    {
        var session = SessionWithMedia(hasAudio: false);

        Assert.True(EditCommands.AddClips(session, [(0, VideoClip())]).Ok);

        Assert.Empty(FirstAudioTrack(session).Clips);
        Assert.Empty(session.ActiveTimeline.Tracks[0].Clips.Single().LinkGroupId ?? "");
    }

    [Fact]
    public void AddClips_UnknownMediaRef_CreatesNoPartner()
    {
        // HasAudio is unknown, not false. Guessing "yes" would put a silent clip on the timeline.
        var session = ProjectSession.CreateNew();

        Assert.True(EditCommands.AddClips(session, [(0, VideoClip())]).Ok);

        Assert.Empty(FirstAudioTrack(session).Clips);
    }

    [Fact]
    public void AddClips_ImageOnAVideoTrack_CreatesNoPartner()
    {
        var session = SessionWithMedia(hasAudio: true);
        var still = new Clip
        {
            MediaRef = MediaRef,
            MediaType = ClipType.image,
            SourceClipType = ClipType.image,
            StartFrame = 0,
            DurationFrames = 30,
        };

        Assert.True(EditCommands.AddClips(session, [(0, still)]).Ok);

        Assert.Empty(FirstAudioTrack(session).Clips);
    }

    [Fact]
    public void AddClips_ReusesTheExistingAudioTrackRatherThanAddingOne()
    {
        var session = SessionWithMedia(hasAudio: true);
        var trackCountBefore = session.ActiveTimeline.Tracks.Count;

        Assert.True(EditCommands.AddClips(session, [(0, VideoClip())]).Ok);

        Assert.Equal(trackCountBefore, session.ActiveTimeline.Tracks.Count);
    }

    [Fact]
    public void AddClips_CreatesAnAudioTrackWhenTheTimelineHasNone()
    {
        var session = SessionWithMedia(hasAudio: true);
        session.ActiveTimeline.Tracks.RemoveAll(t => t.Type == ClipType.audio);
        Assert.DoesNotContain(session.ActiveTimeline.Tracks, t => t.Type == ClipType.audio);

        Assert.True(EditCommands.AddClips(session, [(0, VideoClip())]).Ok);

        Assert.Single(FirstAudioTrack(session).Clips);
    }

    [Fact]
    public void AddClips_PairIsOneUndoStep()
    {
        var session = SessionWithMedia(hasAudio: true);

        Assert.True(EditCommands.AddClips(session, [(0, VideoClip())]).Ok);
        Assert.Single(session.ActiveTimeline.Tracks[0].Clips);
        Assert.Single(FirstAudioTrack(session).Clips);

        Assert.True(session.CanUndo());
        session.Undo();

        Assert.Empty(session.ActiveTimeline.Tracks[0].Clips);
        Assert.Empty(FirstAudioTrack(session).Clips);
    }

    [Fact]
    public void AddClips_BatchGivesEveryEntryItsOwnLinkGroup()
    {
        var session = SessionWithMedia(hasAudio: true);

        Assert.True(EditCommands.AddClips(
            session,
            [(0, VideoClip(start: 0, duration: 30)), (0, VideoClip(start: 30, duration: 30))]).Ok);

        var videos = session.ActiveTimeline.Tracks[0].Clips;
        var audios = FirstAudioTrack(session).Clips;
        Assert.Equal(2, videos.Count);
        Assert.Equal(2, audios.Count);
        Assert.Equal(2, videos.Select(c => c.LinkGroupId).Distinct().Count());
        Assert.Equal(
            videos.Select(c => c.LinkGroupId).OrderBy(x => x),
            audios.Select(c => c.LinkGroupId).OrderBy(x => x));
    }

    [Fact]
    public void AddClips_AudioClipOnAnAudioTrackIsUnaffected()
    {
        var session = SessionWithMedia(hasAudio: true);
        var audioTrackIndex = session.ActiveTimeline.Tracks.FindIndex(t => t.Type == ClipType.audio);
        var music = new Clip
        {
            MediaRef = MediaRef,
            MediaType = ClipType.audio,
            SourceClipType = ClipType.audio,
            StartFrame = 0,
            DurationFrames = 40,
        };

        Assert.True(EditCommands.AddClips(session, [(audioTrackIndex, music)]).Ok);

        Assert.Single(session.ActiveTimeline.Tracks[audioTrackIndex].Clips);
        Assert.Empty(session.ActiveTimeline.Tracks[audioTrackIndex].Clips.Single().LinkGroupId ?? "");
    }

    // --- WP-157 T1 spot-check finding (Grok 4.6 xhigh, 2026-09-04) ---------------------
    // ResolveLinkedAudioTrackIndex was occupancy-blind and MCP add_clips places with overwrite:true.
    // On the default V/V/A/A layout both video tracks resolved to Audio 1, so a partner could delete
    // whatever was already there. Mac availableAudioTrackIndex picks a track with no conflict, else
    // appends one, and never overwrites.

    [Fact]
    public void AddClips_PartnerNeverOverwritesAudioTheUserAlreadyPlaced()
    {
        var session = SessionWithMedia(hasAudio: true);
        var audioIndex = session.ActiveTimeline.Tracks.FindIndex(t => t.Type == ClipType.audio);
        var music = new Clip
        {
            MediaRef = MediaRef,
            MediaType = ClipType.audio,
            SourceClipType = ClipType.audio,
            StartFrame = 0,
            DurationFrames = 100,
        };
        session.ActiveTimeline.Tracks[audioIndex].Clips.Add(music);

        // Overwrite on, exactly as ToolExecutor.AddClips calls it.
        Assert.True(EditCommands.AddClips(
            session, [(0, VideoClip(start: 0, duration: 50))], overwrite: true).Ok);

        Assert.Contains(session.ActiveTimeline.Tracks[audioIndex].Clips, c => c.Id == music.Id);
        Assert.Equal(100, session.ActiveTimeline.Tracks[audioIndex].Clips.Single(c => c.Id == music.Id).DurationFrames);
        var partners = session.ActiveTimeline.Tracks
            .Where(t => t.Type == ClipType.audio)
            .SelectMany(t => t.Clips)
            .Where(c => c.Id != music.Id)
            .ToList();
        Assert.Single(partners);
    }

    [Fact]
    public void AddClips_TwoOverlappingVideosInOneBatchKeepBothPartners()
    {
        // The reported case: default V/V/A/A, two hasAudio videos at the same frames on V1 and V2.
        var session = SessionWithMedia(hasAudio: true);

        Assert.True(EditCommands.AddClips(
            session,
            [(0, VideoClip(start: 0, duration: 50)), (1, VideoClip(start: 0, duration: 50))],
            overwrite: true).Ok);

        var videos = session.ActiveTimeline.Tracks
            .Where(t => t.Type == ClipType.video).SelectMany(t => t.Clips).ToList();
        var audios = session.ActiveTimeline.Tracks
            .Where(t => t.Type == ClipType.audio).SelectMany(t => t.Clips).ToList();
        Assert.Equal(2, videos.Count);
        Assert.Equal(2, audios.Count);
        // Every video keeps a partner that still exists.
        foreach (var v in videos)
            Assert.Contains(audios, a => a.LinkGroupId == v.LinkGroupId);
        // And they are on different tracks, because one track cannot hold both at the same frames.
        var occupied = session.ActiveTimeline.Tracks
            .Where(t => t.Type == ClipType.audio && t.Clips.Count > 0).ToList();
        Assert.Equal(2, occupied.Count);
    }

    [Fact]
    public void AddClips_AppendsAnAudioTrackWhenEveryExistingOneIsBusy()
    {
        var session = SessionWithMedia(hasAudio: true);
        foreach (var t in session.ActiveTimeline.Tracks.Where(t => t.Type == ClipType.audio))
        {
            t.Clips.Add(new Clip
            {
                MediaRef = MediaRef,
                MediaType = ClipType.audio,
                SourceClipType = ClipType.audio,
                StartFrame = 0,
                DurationFrames = 100,
            });
        }
        var before = session.ActiveTimeline.Tracks.Count;

        Assert.True(EditCommands.AddClips(
            session, [(0, VideoClip(start: 0, duration: 50))], overwrite: true).Ok);

        Assert.Equal(before + 1, session.ActiveTimeline.Tracks.Count);
        Assert.Single(session.ActiveTimeline.Tracks[^1].Clips);
    }

    [Fact]
    public void InsertClips_PushesTheAudioTrackSoThePairStaysAligned()
    {
        var session = SessionWithMedia(hasAudio: true);
        // A pair already on the timeline at frame 0.
        Assert.True(EditCommands.AddClips(session, [(0, VideoClip(start: 0, duration: 60))]).Ok);
        var audioTrack = FirstAudioTrack(session);
        Assert.Single(audioTrack.Clips);

        // Splice 25 frames in ahead of it.
        Assert.True(EditCommands.InsertClips(session, 0, 0, [VideoClip(start: 0, duration: 25)]).Ok);

        var videos = session.ActiveTimeline.Tracks[0].Clips.OrderBy(c => c.StartFrame).ToList();
        var audios = audioTrack.Clips.OrderBy(c => c.StartFrame).ToList();
        Assert.Equal(2, videos.Count);
        Assert.Equal(2, audios.Count);
        // Every video clip has an audio clip at the same frame — the alignment the Mac contract names.
        Assert.Equal(videos.Select(c => c.StartFrame), audios.Select(c => c.StartFrame));
        Assert.Equal(25, videos[1].StartFrame);
    }

    [Fact]
    public void InsertClips_SilentVideoDoesNotPushTheAudioTrack()
    {
        var session = SessionWithMedia(hasAudio: false);
        var audioTrackIndex = session.ActiveTimeline.Tracks.FindIndex(t => t.Type == ClipType.audio);
        var music = new Clip
        {
            MediaRef = MediaRef,
            MediaType = ClipType.audio,
            SourceClipType = ClipType.audio,
            StartFrame = 0,
            DurationFrames = 40,
        };
        Assert.True(EditCommands.AddClips(session, [(audioTrackIndex, music)]).Ok);

        Assert.True(EditCommands.InsertClips(session, 0, 0, [VideoClip(start: 0, duration: 25)]).Ok);

        // Nothing was linked, so nothing on the audio track may move.
        Assert.Equal(0, session.ActiveTimeline.Tracks[audioTrackIndex].Clips.Single().StartFrame);
    }

    [Fact]
    public void InsertClips_PairIsOneUndoStep()
    {
        var session = SessionWithMedia(hasAudio: true);

        Assert.True(EditCommands.InsertClips(session, 0, 0, [VideoClip(start: 0, duration: 25)]).Ok);
        Assert.Single(session.ActiveTimeline.Tracks[0].Clips);
        Assert.Single(FirstAudioTrack(session).Clips);

        Assert.True(session.CanUndo());
        session.Undo();

        Assert.Empty(session.ActiveTimeline.Tracks[0].Clips);
        Assert.Empty(FirstAudioTrack(session).Clips);
    }
}
