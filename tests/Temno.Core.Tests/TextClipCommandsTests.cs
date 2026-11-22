using System.Text.Json;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public sealed class TextClipCommandsTests
{
    [Fact]
    public void AddTexts_AutoCreatesTopTrack_AppliesStyleAndAnimation()
    {
        var session = ProjectSession.CreateNew(fps: 30, width: 1920, height: 1080);
        var existingTracks = session.ActiveTimeline.Tracks.Count;

        var style = new TextStyle();
        new TextStylePatch
        {
            FontSize = 48,
            IsBold = false,
            Color = TextClipCommands.ParseHexColor("#ff0000"),
            BackgroundColor = TextClipCommands.ParseHexColor("#00000080"),
        }.ApplyTo(style);

        var result = TextClipCommands.AddTexts(session,
        [
            new TextClipSpec(null, 0, 60, "Hello", style, null,
                new TextAnimation { Preset = "fadeIn" }),
            new TextClipSpec(null, 60, 120, "World", new TextStyle(), null, null),
        ], out var ids);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, ids.Count);
        Assert.Equal(existingTracks + 1, session.ActiveTimeline.Tracks.Count);

        var track = session.ActiveTimeline.Tracks[0];
        Assert.Equal(ClipType.video, track.Type);
        Assert.Equal(2, track.Clips.Count);

        var hello = track.Clips[0];
        Assert.Equal(ClipType.text, hello.MediaType);
        Assert.Equal("Hello", hello.TextContent);
        Assert.Equal(60, hello.DurationFrames);

        var styleJson = hello.TextStyle!.Value;
        Assert.Equal(48, styleJson.GetProperty("fontSize").GetDouble());
        Assert.False(styleJson.GetProperty("isBold").GetBoolean());
        Assert.Equal(1.0, styleJson.GetProperty("color").GetProperty("r").GetDouble(), 3);
        Assert.True(styleJson.GetProperty("background").GetProperty("enabled").GetBoolean());
        Assert.Equal(0x80 / 255.0, styleJson.GetProperty("background").GetProperty("color").GetProperty("a").GetDouble(), 3);

        Assert.Equal("fadeIn", hello.TextAnimation!.Value.GetProperty("preset").GetString());
        Assert.Null(track.Clips[1].TextAnimation);

        // Auto-fit gives a sane, sub-canvas box centered at (0.5, 0.5).
        Assert.Equal(0.5, hello.Transform.CenterX, 3);
        Assert.InRange(hello.Transform.Width, 0.01, 0.9);
        Assert.InRange(hello.Transform.Height, 0.01, 1.0);
    }

    [Fact]
    public void AddTexts_ValidatesFramesTracksMixingAndOverlap()
    {
        var session = SessionWithVideoTrackAndAudioTrack();

        Assert.False(TextClipCommands.AddTexts(session,
            [Spec(null, -1, 10, "x")], out _).Ok);
        Assert.False(TextClipCommands.AddTexts(session,
            [Spec(null, 10, 10, "x")], out _).Ok);
        Assert.False(TextClipCommands.AddTexts(session,
            [Spec(5, 0, 10, "x")], out _).Ok); // out of range
        Assert.False(TextClipCommands.AddTexts(session,
            [Spec(1, 0, 10, "x")], out _).Ok); // audio track

        // Mixed explicit/omitted trackIndex is rejected all-or-none.
        var mixed = TextClipCommands.AddTexts(session,
            [Spec(0, 0, 10, "a"), Spec(null, 10, 20, "b")], out _);
        Assert.False(mixed.Ok);
        Assert.Contains("Mixed trackIndex", mixed.Error);

        // Batch overlap on the same track is rejected and nothing is committed.
        var tracksBefore = session.ActiveTimeline.Tracks.Count;
        var overlap = TextClipCommands.AddTexts(session,
            [Spec(0, 0, 30, "a"), Spec(0, 20, 40, "b")], out _);
        Assert.False(overlap.Ok);
        Assert.Equal(tracksBefore, session.ActiveTimeline.Tracks.Count);
        Assert.Empty(session.ActiveTimeline.Tracks[0].Clips);

        // Overlap with an existing clip is rejected.
        Assert.True(TextClipCommands.AddTexts(session, [Spec(0, 0, 30, "a")], out _).Ok);
        Assert.False(TextClipCommands.AddTexts(session, [Spec(0, 15, 45, "b")], out _).Ok);
    }

    [Fact]
    public void AddTexts_UndoRemovesClipsAndAutoCreatedTrack()
    {
        var session = ProjectSession.CreateNew();
        var tracksBefore = session.ActiveTimeline.Tracks.Count;

        Assert.True(TextClipCommands.AddTexts(session, [Spec(null, 0, 30, "t")], out _).Ok);
        Assert.Equal(tracksBefore + 1, session.ActiveTimeline.Tracks.Count);

        session.Undo();
        Assert.Equal(tracksBefore, session.ActiveTimeline.Tracks.Count);

        session.Redo();
        Assert.Equal(tracksBefore + 1, session.ActiveTimeline.Tracks.Count);
        Assert.Equal("t", session.ActiveTimeline.Tracks[0].Clips.Single().TextContent);
    }

    [Fact]
    public void UpdateText_ContentClearsWordTimings_StyleMergesAndTransformPreservesRotation()
    {
        var session = ProjectSession.CreateNew(width: 1920, height: 1080);
        Assert.True(TextClipCommands.AddTexts(session, [Spec(null, 0, 60, "before")], out var ids).Ok);
        var clip = FindClip(session, ids[0]);
        clip.WordTimings = [new WordTiming { Text = "before", StartFrame = 0, EndFrame = 60 }];
        clip.Transform.Rotation = 15;

        var result = TextClipCommands.UpdateText(session, ids, null, new TextPatch
        {
            HasContent = true,
            Content = "after",
            Style = new TextStylePatch { IsItalic = true, BorderColor = TextClipCommands.ParseHexColor("#00ff00") },
            CenterX = 0.3,
            Width = 0.4,
        }, out var touched, out var notes);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(ids, touched);
        Assert.Single(notes); // word-timings-cleared note

        clip = FindClip(session, ids[0]);
        Assert.Equal("after", clip.TextContent);
        Assert.Null(clip.WordTimings);

        var style = clip.TextStyle!.Value;
        Assert.True(style.GetProperty("isItalic").GetBoolean());
        Assert.True(style.GetProperty("border").GetProperty("enabled").GetBoolean());
        Assert.Equal(1.0, style.GetProperty("border").GetProperty("color").GetProperty("g").GetDouble(), 3);
        // Untouched fields keep their previous values.
        Assert.Equal("Helvetica-Bold", style.GetProperty("fontName").GetString());

        Assert.Equal(0.3, clip.Transform.CenterX, 3);
        Assert.Equal(0.4, clip.Transform.Width, 3);
        Assert.Equal(0.5, clip.Transform.CenterY, 3); // partial transform keeps prior value
        Assert.Equal(15, clip.Transform.Rotation, 3); // rotation survives transform edits
    }

    [Fact]
    public void UpdateText_AnimationSetClearAndHighlightOnly()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(TextClipCommands.AddTexts(session, [Spec(null, 0, 60, "t")], out var ids).Ok);

        Assert.True(TextClipCommands.UpdateText(session, ids, null, new TextPatch
        {
            SetAnimation = true,
            Animation = new TextAnimation { Preset = "wordPop", Highlight = TextClipCommands.ParseHexColor("#ffd900") },
        }, out _, out _).Ok);
        var anim = FindClip(session, ids[0]).TextAnimation!.Value;
        Assert.Equal("wordPop", anim.GetProperty("preset").GetString());
        Assert.Equal(0xD9 / 255.0, anim.GetProperty("highlight").GetProperty("g").GetDouble(), 3);
        Assert.Equal(0.0, anim.GetProperty("highlight").GetProperty("b").GetDouble(), 3);

        // highlightColor alone updates the highlight without touching the preset.
        Assert.True(TextClipCommands.UpdateText(session, ids, null, new TextPatch
        {
            HighlightOnly = TextClipCommands.ParseHexColor("#0000ff"),
        }, out _, out _).Ok);
        anim = FindClip(session, ids[0]).TextAnimation!.Value;
        Assert.Equal("wordPop", anim.GetProperty("preset").GetString());
        Assert.Equal(1.0, anim.GetProperty("highlight").GetProperty("b").GetDouble(), 3);

        // "off" clears.
        Assert.True(TextClipCommands.UpdateText(session, ids, null, new TextPatch
        {
            SetAnimation = true,
            Animation = null,
        }, out _, out _).Ok);
        Assert.Null(FindClip(session, ids[0]).TextAnimation);
    }

    [Fact]
    public void UpdateText_CaptionGroupResolutionAndValidation()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(TextClipCommands.AddTexts(session,
            [Spec(null, 0, 30, "c1"), Spec(null, 30, 60, "c2")], out var ids).Ok);
        foreach (var id in ids) FindClip(session, id).CaptionGroupId = "grp1";

        Assert.True(TextClipCommands.UpdateText(session, [], "grp1", new TextPatch
        {
            Style = new TextStylePatch { FontSize = 30 },
        }, out var touched, out _).Ok);
        Assert.Equal(2, touched.Count);
        Assert.All(ids, id => Assert.Equal(30, FindClip(session, id).TextStyle!.Value.GetProperty("fontSize").GetDouble()));

        Assert.False(TextClipCommands.UpdateText(session, [], "missing", new TextPatch
        {
            Style = new TextStylePatch { FontSize = 30 },
        }, out _, out _).Ok);

        Assert.False(TextClipCommands.UpdateText(session, ids, null, new TextPatch(), out _, out _).Ok);

        // Non-text clips are rejected.
        var videoTrack = new Track { Type = ClipType.video };
        videoTrack.Clips.Add(new Clip { Id = "vid1", MediaRef = "M1", MediaType = ClipType.video, DurationFrames = 30 });
        session.ActiveTimeline.Tracks.Add(videoTrack);
        Assert.False(TextClipCommands.UpdateText(session, ["vid1"], null, new TextPatch
        {
            HasContent = true, Content = "x",
        }, out _, out _).Ok);
    }

    [Fact]
    public void TextClips_RoundTripThroughProjectEncodeDecode()
    {
        var session = ProjectSession.CreateNew();
        var style = new TextStyle();
        new TextStylePatch { FontSize = 64, Color = TextClipCommands.ParseHexColor("#123456") }.ApplyTo(style);
        Assert.True(TextClipCommands.AddTexts(session,
            [new TextClipSpec(null, 0, 45, "persist me", style, null, new TextAnimation { Preset = "typewriter" })],
            out var ids).Ok);

        var bytes = session.ProjectFile.Encode();
        var decoded = ProjectFile.Decode(bytes);
        var clip = decoded.Timelines[0].Tracks[0].Clips.Single(c => c.Id == ids[0]);

        Assert.Equal(ClipType.text, clip.MediaType);
        Assert.Equal("persist me", clip.TextContent);
        Assert.Equal(64, clip.TextStyle!.Value.GetProperty("fontSize").GetDouble());
        Assert.Equal("typewriter", clip.TextAnimation!.Value.GetProperty("preset").GetString());
        Assert.Equal(0x12 / 255.0, clip.TextStyle.Value.GetProperty("color").GetProperty("r").GetDouble(), 3);
    }

    [Fact]
    public void ParseHexColor_HandlesShortLongAlphaAndRejectsGarbage()
    {
        var rgb = TextClipCommands.ParseHexColor("#abc")!;
        Assert.Equal(0xAA / 255.0, rgb.R, 6);
        Assert.Equal(0xBB / 255.0, rgb.G, 6);
        Assert.Equal(0xCC / 255.0, rgb.B, 6);
        Assert.Equal(1.0, rgb.A, 6);

        var rgba = TextClipCommands.ParseHexColor("80ff0040")!;
        Assert.Equal(0x80 / 255.0, rgba.R, 6);
        Assert.Equal(0x40 / 255.0, rgba.A, 6);

        Assert.Null(TextClipCommands.ParseHexColor(null));
        Assert.Null(TextClipCommands.ParseHexColor("  "));
        Assert.Throws<FormatException>(() => TextClipCommands.ParseHexColor("#12"));
        Assert.Throws<FormatException>(() => TextClipCommands.ParseHexColor("#zzzzzz"));
    }

    private static TextClipSpec Spec(int? track, int start, int end, string content)
        => new(track, start, end, content, new TextStyle(), null, null);

    private static ProjectSession SessionWithVideoTrackAndAudioTrack()
    {
        var session = ProjectSession.CreateNew();
        // Tracks[0] video (default), ensure an audio track exists at index 1.
        if (session.ActiveTimeline.Tracks.Count < 2 || session.ActiveTimeline.Tracks[1].Type != ClipType.audio)
            session.ActiveTimeline.Tracks.Insert(1, new Track { Type = ClipType.audio });
        return session;
    }

    private static Clip FindClip(ProjectSession session, string id)
        => session.ActiveTimeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == id);
}
