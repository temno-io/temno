using System.Text.Json.Nodes;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

// WP-331: ripple_delete_ranges follows the Mac tool. Units default to project frames, seconds are the clip's source
// seconds, values may be fractional, and exactly one of clipId and trackIndex names the target.
[Collection("EnvRegistry")]
public sealed class RippleDeleteRangesToolTests : IDisposable
{
    public void Dispose() => ProjectHub.ActiveSession = null;

    private static ProjectSession Session(Clip clip)
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);
        ProjectHub.ActiveSession = session;
        return session;
    }

    private static List<Clip> Clips(ProjectSession session) =>
        session.ActiveTimeline.Tracks[0].Clips.OrderBy(c => c.StartFrame).ToList();

    private static ToolCallResult Run(string json) =>
        ToolExecutor.Execute("ripple_delete_ranges", JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void Seconds_map_through_the_clips_trim_speed_and_position()
    {
        var session = Session(new Clip
        {
            Id = "C1", MediaRef = "M", MediaType = ClipType.video, StartFrame = 30, DurationFrames = 120, Speed = 2,
        });
        var fps = session.ActiveTimeline.Fps;
        session.ActiveTimeline.Tracks[0].Clips[0].TrimStartFrame = 2 * fps;
        // Source seconds 3..4 sit one and two seconds past the trim, at double speed: project frames 30+fps/2 .. 30+fps.
        var result = Run("""{"clipId":"C1","units":"seconds","ranges":[[3,4]]}""");
        Assert.False(result.IsError, result.Content[0].Text);
        var clips = Clips(session);
        Assert.Equal(2, clips.Count);
        Assert.Equal(fps / 2, clips[0].DurationFrames);
        Assert.Equal(30 + fps / 2, clips[1].StartFrame);
        Assert.Equal(120 - fps / 2, clips.Sum(c => c.DurationFrames));
    }

    [Fact]
    public void Frames_are_the_default_and_fractional_ends_round_half_away_from_zero()
    {
        var session = Session(new Clip { Id = "C1", MediaRef = "M", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 100 });
        var result = Run("""{"clipId":"C1","ranges":[[10.5,20.4]]}""");
        Assert.False(result.IsError, result.Content[0].Text);
        var clips = Clips(session);
        Assert.Equal(11, clips[0].DurationFrames);
        Assert.Equal(91, clips.Sum(c => c.DurationFrames));
    }

    [Fact]
    public void Ranges_outside_the_span_are_counted_not_applied()
    {
        var session = Session(new Clip { Id = "C1", MediaRef = "M", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 100 });
        // Integer-backed nodes, as an in-process caller builds them.
        var args = new JsonObject
        {
            ["trackIndex"] = 0,
            ["ranges"] = new JsonArray(new JsonArray(10, 20), new JsonArray(500, 600)),
        };
        var result = ToolExecutor.Execute("ripple_delete_ranges", args);
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal(1, JsonNode.Parse(result.Content[0].Text!)!["rangesIgnored"]!.GetValue<int>());
        Assert.Equal(90, Clips(session).Sum(c => c.DurationFrames));

        var none = Run("""{"clipId":"C1","ranges":[[200,300]]}""");
        Assert.True(none.IsError);
        Assert.Contains("No ranges fall within clip C1", none.Content[0].Text);
    }

    [Theory]
    [InlineData("""[[10,20],[10,20]]""", 90)]
    [InlineData("""[[15,30],[10,20]]""", 80)]
    [InlineData("""[[10,20],[20,30]]""", 80)]
    public void Overlapping_ranges_merge_instead_of_cutting_twice(string ranges, int remaining)
    {
        var session = Session(new Clip { Id = "C1", MediaRef = "M", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 100 });
        var result = Run($$"""{"trackIndex":0,"ranges":{{ranges}}}""");
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal(remaining, Clips(session).Sum(c => c.DurationFrames));
        Assert.Equal(10, Clips(session)[0].DurationFrames);
    }

    // --- WP-337: linked partners move with the cut ---

    private static Clip Picture(string id, int start, int duration) =>
        new() { Id = id, MediaRef = "M-" + id, MediaType = ClipType.video, StartFrame = start, DurationFrames = duration };

    private static Clip Sound(string id, int start, int duration) =>
        new() { Id = id, MediaRef = "M-" + id, MediaType = ClipType.audio, StartFrame = start, DurationFrames = duration };

    // Default layout: tracks 0 and 1 are video, 2 and 3 audio.
    private static ProjectSession Layout(params (int Track, Clip Clip)[] clips)
    {
        var session = ProjectSession.CreateNew(fps: 30);
        Assert.True(EditCommands.AddClips(session, clips).Ok);
        ProjectHub.ActiveSession = session;
        return session;
    }

    private static void Link(ProjectSession session, params string[] ids) =>
        Assert.True(EditCommands.LinkClips(session, ids).Ok);

    private static List<(int Start, int Duration, int Trim)> On(ProjectSession session, int track) =>
        session.ActiveTimeline.Tracks[track].Clips.OrderBy(c => c.StartFrame)
            .Select(c => (c.StartFrame, c.DurationFrames, c.TrimStartFrame)).ToList();

    [Fact]
    public void A_cut_on_a_linked_video_clip_cuts_its_audio_by_the_same_frames_in_one_undo_step()
    {
        var session = Layout((0, Picture("V", 0, 900)), (2, Sound("A", 0, 900)));
        Link(session, "V", "A");

        // "Cut 0:10 to 0:15", as an agent sends it: the clip's source seconds.
        var result = Run("""{"clipId":"V","units":"seconds","ranges":[[10,15]]}""");
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal(150, JsonNode.Parse(result.Content[0].Text!)!["removedFrames"]!.GetValue<int>());
        Assert.Equal([(0, 300, 0), (300, 450, 450)], On(session, 0));
        Assert.Equal(On(session, 0), On(session, 2));

        var undo = ToolExecutor.Execute("undo", new JsonObject());
        Assert.False(undo.IsError, undo.Content[0].Text);
        Assert.Equal([(0, 900, 0)], On(session, 0));
        Assert.Equal([(0, 900, 0)], On(session, 2));
    }

    [Fact]
    public void A_partner_track_that_moves_takes_the_partners_of_its_later_clips_with_it()
    {
        // The default V/V/A/A layout puts both video tracks' sound on audio track 2 when it is free there.
        var session = Layout(
            (0, Picture("X", 0, 300)), (2, Sound("XA", 0, 300)),
            (1, Picture("Y", 400, 100)), (2, Sound("YA", 400, 100)));
        Link(session, "X", "XA");
        Link(session, "Y", "YA");

        var result = Run("""{"trackIndex":0,"ranges":[[100,150]]}""");
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal(50, JsonNode.Parse(result.Content[0].Text!)!["removedFrames"]!.GetValue<int>());
        Assert.Equal([(0, 100, 0), (100, 150, 150)], On(session, 0));
        Assert.Equal([(0, 100, 0), (100, 150, 150), (350, 100, 0)], On(session, 2));
        Assert.Equal([(350, 100, 0)], On(session, 1));
    }

    [Fact]
    public void A_partner_track_holding_an_unrelated_clip_inside_the_cut_refuses_and_changes_nothing()
    {
        var session = Layout((0, Picture("X", 0, 300)), (2, Sound("XA", 0, 100)), (2, Sound("Music", 100, 200)));
        Link(session, "X", "XA");

        var result = Run("""{"clipId":"X","ranges":[[150,200]]}""");
        Assert.True(result.IsError);
        Assert.Contains("Nothing was cut: track 2 (A1) holds clip Music inside the cut, and it is not linked to the clips being cut.", result.Content[0].Text);
        Assert.Equal([(0, 300, 0)], On(session, 0));
        Assert.Equal([(0, 100, 0), (100, 200, 0)], On(session, 2));
    }

    // A J-cut: the sound starts before its picture. Cutting the picture track's empty lead-in moves the picture, so the
    // sound loses the same frames; it is the picture's partner, not an unrelated clip (found by the T1 spot-check).
    [Fact]
    public void Cutting_a_j_cut_lead_in_on_the_picture_track_cuts_the_leading_sound()
    {
        var session = Layout((0, Picture("X", 100, 300)), (2, Sound("XA", 50, 350)));
        Link(session, "X", "XA");

        var result = Run("""{"trackIndex":0,"ranges":[[60,90]]}""");
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal([(70, 300, 0)], On(session, 0));
        Assert.Equal([(50, 10, 0), (60, 310, 40)], On(session, 2));
    }

    // Multicam clips carry a group, not a link: the mic and the other angles are cut with the angle the agent names.
    [Fact]
    public void A_cut_on_a_multicam_angle_cuts_the_mic_and_the_other_angles()
    {
        Clip Member(Clip clip) { clip.MulticamGroupId = "G"; return clip; }
        var session = Layout(
            (0, Member(Picture("Wide", 0, 200))), (0, Member(Picture("Close", 200, 100))),
            (1, Member(Picture("Alt", 0, 300))), (2, Member(Sound("Mic", 0, 300))));

        var result = Run("""{"clipId":"Wide","ranges":[[100,150]]}""");
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal([(0, 100, 0), (100, 50, 150), (150, 100, 0)], On(session, 0));
        Assert.Equal([(0, 100, 0), (100, 150, 150)], On(session, 1));
        Assert.Equal(On(session, 1), On(session, 2));
    }

    [Fact]
    public void An_unlinked_clip_is_cut_alone()
    {
        var session = Layout((0, Picture("X", 0, 300)), (2, Sound("Music", 0, 300)));
        var result = Run("""{"clipId":"X","ranges":[[100,150]]}""");
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal([(0, 100, 0), (100, 150, 150)], On(session, 0));
        Assert.Equal([(0, 300, 0)], On(session, 2));
    }

    [Fact]
    public void IgnoreSyncLockedTracks_is_accepted_and_changes_nothing()
    {
        var session = Layout((0, Picture("V", 0, 300)), (2, Sound("A", 0, 300)));
        Link(session, "V", "A");
        var result = Run("""{"clipId":"V","ranges":[[100,150]],"ignoreSyncLockedTracks":[2]}""");
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal(On(session, 0), On(session, 2));
        var schema = McpToolRegistry.Tools.Single(t => t.Name == "ripple_delete_ranges").InputSchema;
        Assert.Contains("Windows has no sync lock", schema["properties"]!["ignoreSyncLockedTracks"]!["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"clipId":"C1","trackIndex":0,"ranges":[[1,2]]}""", "exactly one of 'clipId'")]
    [InlineData("""{"ranges":[[1,2]]}""", "exactly one of 'clipId'")]
    [InlineData("""{"trackIndex":0,"units":"seconds","ranges":[[1,2]]}""", "requires a clipId")]
    [InlineData("""{"trackIndex":9,"ranges":[[1,2]]}""", "Track index out of range: 9")]
    [InlineData("""{"clipId":"C1","ranges":[[5,5]]}""", "ranges[0]: end (5) must be greater than start (5)")]
    [InlineData("""{"clipId":"C1","ranges":[[1,2,3]]}""", "ranges[0]: expected [start, end]")]
    [InlineData("""{"clipId":"C1","ranges":[["a","b"]]}""", "ranges[0]: start and end must be numbers")]
    [InlineData("""{"clipId":"C1","ranges":[]}""", "Missing or empty 'ranges' array")]
    [InlineData("""{"clipId":"C1","units":"beats","ranges":[[1,2]]}""", "units must be 'seconds' or 'frames'")]
    public void Invalid_calls_are_refused_and_change_nothing(string json, string message)
    {
        var session = Session(new Clip { Id = "C1", MediaRef = "M", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 100 });
        var result = Run(json);
        Assert.True(result.IsError);
        Assert.Contains(message, result.Content[0].Text);
        Assert.Equal(100, Assert.Single(Clips(session)).DurationFrames);
    }
}
