using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-435: the editor agent read <c>"currentFrame": 0</c> from <c>get_timeline</c> whatever the playhead was,
/// rendered frame 0 when <c>inspect_timeline</c> got no frame, and called the header's V1 "Video 2". The
/// playhead is the one <c>TimelineControl.PersistPlayhead</c> saves into the project's view state.
/// </summary>
[Collection("EnvRegistry")]
public sealed class AgentPlayheadTests : IDisposable
{
    public void Dispose() => ProjectHub.ActiveSession = null;

    /// <summary>The owner's layout: V2 empty, V1 and A1 holding a 600-frame clip pair, A2 empty.</summary>
    private static ProjectSession Session(int? playhead)
    {
        var session = ProjectSession.CreateNew(fps: 30, width: 1920, height: 1080);
        session.ActiveTimeline.Tracks[2].Clips.Add(new Clip
        {
            Id = "a0", MediaRef = "missing-audio", MediaType = ClipType.audio, StartFrame = 0, DurationFrames = 600,
        });
        if (playhead is { } frame)
        {
            session.ProjectFile.ViewStates = new Dictionary<string, TimelineViewState>
            {
                [session.ActiveTimeline.Id] = new() { PlayheadFrame = frame },
            };
        }
        ProjectHub.ActiveSession = session;
        return session;
    }

    private static JsonElement Json(ToolCallResult result, int block = 0)
    {
        Assert.False(result.IsError, result.Content[0].Text);
        return JsonDocument.Parse(result.Content[block].Text!).RootElement;
    }

    [Fact]
    public void GetTimeline_ReportsTheSavedPlayhead()
    {
        Session(playhead: 524); // 00:00:17:14 at 30 fps
        var timeline = Json(ToolExecutor.Execute("get_timeline", []));
        Assert.Equal(524, timeline.GetProperty("currentFrame").GetInt32());
    }

    [Fact]
    public void GetTimeline_WithoutViewState_ReportsZero()
    {
        Session(playhead: null);
        Assert.Equal(0, Json(ToolExecutor.Execute("get_timeline", [])).GetProperty("currentFrame").GetInt32());
    }

    [Theory]
    [InlineData(5000, 600)]
    [InlineData(-3, 0)]
    public void GetTimeline_ClampsThePlayheadAsTheTimelineDoes(int saved, int expected)
    {
        Session(playhead: saved);
        Assert.Equal(expected, Json(ToolExecutor.Execute("get_timeline", [])).GetProperty("currentFrame").GetInt32());
    }

    [Fact]
    public void GetTimeline_ReadsTheActiveTimelinesViewStateOnly()
    {
        var session = Session(playhead: null);
        session.ProjectFile.ViewStates = new Dictionary<string, TimelineViewState>
        {
            ["some-other-timeline"] = new() { PlayheadFrame = 300 },
        };
        Assert.Equal(0, Json(ToolExecutor.Execute("get_timeline", [])).GetProperty("currentFrame").GetInt32());
    }

    [Fact]
    public void GetTimeline_LabelsTracksAsTheHeaderDoes()
    {
        var session = Session(playhead: 0);
        var tracks = Json(ToolExecutor.Execute("get_timeline", [])).GetProperty("tracks");
        var labels = tracks.EnumerateArray().Select(t => t.GetProperty("label").GetString()!).ToArray();

        Assert.Equal(["V2", "V1", "A1", "A2"], labels);
        var types = session.ActiveTimeline.Tracks.Select(t => t.Type).ToList();
        for (var i = 0; i < labels.Length; i++)
            Assert.Equal(TrackLabels.LabelFor(types, i), labels[i]);
    }

    [Fact]
    public void ManageTracks_LabelsTracksAsTheHeaderDoes()
    {
        Session(playhead: 0);
        var result = Json(ToolExecutor.Execute("manage_tracks", new JsonObject
        {
            ["set"] = new JsonArray { new JsonObject { ["index"] = 3, ["muted"] = true } },
        }));
        var labels = result.GetProperty("tracks").EnumerateArray().Select(t => t.GetProperty("label").GetString()!);
        Assert.Equal(["V2", "V1", "A1", "A2"], labels);
    }

    [Fact]
    public void CreatedTracks_AreLabelledAsTheHeaderDoes()
    {
        var session = Session(playhead: 0);
        var result = Json(ToolExecutor.Execute("manage_tracks", new JsonObject { ["action"] = "add", ["type"] = "video" }));

        var created = Assert.Single(result.GetProperty("createdTracks").EnumerateArray());
        var index = created.GetProperty("index").GetInt32();
        var types = session.ActiveTimeline.Tracks.Select(t => t.Type).ToList();
        Assert.Equal(TrackLabels.LabelFor(types, index), created.GetProperty("label").GetString());
        Assert.StartsWith("V", created.GetProperty("label").GetString());
    }

    private static void MuteTopAudioTrack()
        => Json(ToolExecutor.Execute("manage_tracks", new JsonObject
        {
            ["set"] = new JsonArray { new JsonObject { ["index"] = 3, ["muted"] = true } },
        }));

    private static int CurrentFrame() => Json(ToolExecutor.Execute("get_timeline", [])).GetProperty("currentFrame").GetInt32();

    /// <summary>
    /// T1 finding: undo restored the whole project file, the playhead saved before the edit included, while the
    /// timeline kept showing the live one. The Mac's undo never moves the playhead.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndoAndRedo_KeepTheLivePlayhead(bool redo)
    {
        var session = Session(playhead: 100);
        MuteTopAudioTrack();
        // The user seeks after the edit: TimelineControl.PersistPlayhead updates the view state in place.
        session.ProjectFile.ViewStates![session.ActiveTimeline.Id].PlayheadFrame = 524;

        Json(ToolExecutor.Execute("undo", []));
        Assert.False(session.ActiveTimeline.Tracks[3].Muted);
        Assert.Equal(524, CurrentFrame());

        if (redo)
        {
            Json(ToolExecutor.Execute("redo", []));
            Assert.True(session.ActiveTimeline.Tracks[3].Muted);
            Assert.Equal(524, CurrentFrame());
        }
    }

    [Fact]
    public void RollbackLastRecord_KeepsTheLivePlayhead()
    {
        var session = Session(playhead: 100);
        session.RecordBeforeEdit();
        session.ProjectFile.ViewStates![session.ActiveTimeline.Id].PlayheadFrame = 524;
        session.RollbackLastRecord();
        Assert.Equal(524, CurrentFrame());
    }

    [Fact]
    public void Undo_OfATimelineDelete_RestoresThatTimelinesViewState()
    {
        var session = Session(playhead: 100);
        var first = session.ActiveTimeline.Id;
        Json(ToolExecutor.Execute("create_timeline", new JsonObject { ["name"] = "Second" }));
        var second = session.ActiveTimeline.Id;
        session.ProjectFile.ViewStates![second] = new TimelineViewState { PlayheadFrame = 0 };
        Assert.True(TimelineCommands.DeleteTimeline(session, second).Ok);
        Assert.False(session.ProjectFile.ViewStates!.ContainsKey(second));

        session.Undo();
        Assert.True(session.ProjectFile.ViewStates!.ContainsKey(second));
        Assert.Equal(100, session.ProjectFile.ViewStates[first].PlayheadFrame);
    }

    /// <summary>Carrying the live view state through undo leaves the unsaved-changes flag as it was before WP-435.</summary>
    [Fact]
    public void Undo_BackToTheSavedState_IsStillClean()
    {
        var session = Session(playhead: 100);
        var root = Path.Combine(Path.GetTempPath(), "temno-wp435-" + Guid.NewGuid().ToString("N"));
        try
        {
            session.Save(Path.Combine(root, "p.temno"));
            MuteTopAudioTrack();
            Assert.True(session.IsDirty);
            session.ProjectFile.ViewStates![session.ActiveTimeline.Id].PlayheadFrame = 524;

            Json(ToolExecutor.Execute("undo", []));
            Assert.False(session.IsDirty);
            Assert.Equal(524, CurrentFrame());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void InspectTimeline_WithNoFrame_RendersThePlayhead()
    {
        Session(playhead: 524);
        var meta = Json(ToolExecutor.Execute("inspect_timeline", []));
        Assert.Equal([524], meta.GetProperty("frames").EnumerateArray().Select(f => f.GetInt32()));
    }

    [Fact]
    public void InspectTimeline_ExplicitFrames_StillWin()
    {
        Session(playhead: 524);
        var single = Json(ToolExecutor.Execute("inspect_timeline", new JsonObject { ["startFrame"] = 12 }));
        Assert.Equal([12], single.GetProperty("frames").EnumerateArray().Select(f => f.GetInt32()));

        // A range given only by its end still starts at 0, as before WP-435.
        var range = Json(ToolExecutor.Execute("inspect_timeline", new JsonObject { ["endFrame"] = 100, ["maxFrames"] = 2 }));
        Assert.Equal([0, 100], range.GetProperty("frames").EnumerateArray().Select(f => f.GetInt32()));
    }
}
