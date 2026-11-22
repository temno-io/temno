using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public class TimelineCommandsTests
{
    [Fact]
    public void AddTimeline_CreatesUniqueIdAndActivates()
    {
        var session = ProjectSession.CreateNew();
        var firstId = session.ActiveTimeline.Id;

        Assert.True(TimelineCommands.AddTimeline(session, "B-roll", null, out var secondId).Ok);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, session.ProjectFile.Timelines.Count);
        Assert.Equal(secondId, session.ProjectFile.ActiveTimelineId);
        Assert.Equal("B-roll", session.ActiveTimeline.Name);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void AddTimeline_UndoRemovesCreatedTimeline()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(TimelineCommands.AddTimeline(session, "Temp", null, out _).Ok);
        Assert.Equal(2, session.ProjectFile.Timelines.Count);
        session.Undo();
        Assert.Single(session.ProjectFile.Timelines);
    }

    [Fact]
    public void SetActive_SwitchesWithoutUndoStackEntry()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(TimelineCommands.AddTimeline(session, "Two", null, out var two).Ok);
        var undoBefore = session.CanUndo();
        Assert.True(TimelineCommands.SetActiveTimeline(session, session.ProjectFile.Timelines[0].Id).Ok);
        Assert.Equal(session.ProjectFile.Timelines[0].Id, session.ProjectFile.ActiveTimelineId);
        // SetActive does not push undo (only Add does).
        Assert.Equal(undoBefore, session.CanUndo());
        Assert.True(TimelineCommands.SetActiveTimeline(session, two).Ok);
        Assert.Equal(two, session.ActiveTimeline.Id);
    }

    [Fact]
    public void Rename_PersistsAndIsUndoable()
    {
        var session = ProjectSession.CreateNew();
        var id = session.ActiveTimeline.Id;
        Assert.True(TimelineCommands.RenameTimeline(session, id, "Main Cut").Ok);
        Assert.Equal("Main Cut", session.ActiveTimeline.Name);
        session.Undo();
        Assert.Equal("Timeline 1", session.ActiveTimeline.Name);
    }

    [Fact]
    public void Switch_PreservesIndependentClipContents()
    {
        var session = ProjectSession.CreateNew();
        var a = session.ActiveTimeline;
        a.Tracks[0].Clips.Add(new Clip
        {
            MediaRef = "m1",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 30,
        });
        Assert.True(TimelineCommands.AddTimeline(session, "Empty", null, out var bId).Ok);
        Assert.Empty(session.ActiveTimeline.Tracks.SelectMany(t => t.Clips));
        Assert.True(TimelineCommands.SetActiveTimeline(session, a.Id).Ok);
        Assert.Single(session.ActiveTimeline.Tracks.SelectMany(t => t.Clips));
        Assert.True(TimelineCommands.SetActiveTimeline(session, bId).Ok);
        Assert.Empty(session.ActiveTimeline.Tracks.SelectMany(t => t.Clips));
    }

    [Fact]
    public void Rename_RejectsEmptyAndMissing()
    {
        var session = ProjectSession.CreateNew();
        Assert.False(TimelineCommands.RenameTimeline(session, session.ActiveTimeline.Id, "  ").Ok);
        Assert.False(TimelineCommands.RenameTimeline(session, "missing", "X").Ok);
    }

    [Fact]
    public void DeleteTimeline_RemovesAndSwitchesActive_UndoRestores()
    {
        var session = ProjectSession.CreateNew();
        var first = session.ActiveTimeline.Id;
        Assert.True(TimelineCommands.AddTimeline(session, "Doomed", null, out var doomed).Ok);
        Assert.Equal(2, session.ProjectFile.Timelines.Count);
        Assert.Equal(doomed, session.ProjectFile.ActiveTimelineId);

        Assert.True(TimelineCommands.DeleteTimeline(session, doomed).Ok);
        Assert.Single(session.ProjectFile.Timelines);
        Assert.Equal(first, session.ProjectFile.ActiveTimelineId);

        session.Undo();
        Assert.Equal(2, session.ProjectFile.Timelines.Count);
        Assert.Contains(session.ProjectFile.Timelines, t => t.Id == doomed);
    }

    [Fact]
    public void DeleteTimeline_RejectsLastTimeline()
    {
        var session = ProjectSession.CreateNew();
        Assert.False(TimelineCommands.DeleteTimeline(session, session.ActiveTimeline.Id).Ok);
    }

    [Fact]
    public void CloseTimelineTab_HidesWithoutDeleting()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(TimelineCommands.AddTimeline(session, "Two", null, out var two).Ok);
        TimelineCommands.EnsureOpenTimelineIds(session);
        Assert.Equal(2, session.ProjectFile.OpenTimelineIds!.Count);
        Assert.True(TimelineCommands.CloseTimelineTab(session, two).Ok);
        Assert.Single(session.ProjectFile.OpenTimelineIds);
        Assert.Equal(2, session.ProjectFile.Timelines.Count);
    }

    [Fact]
    public void CloseOtherTabs_LeavesOne()
    {
        var session = ProjectSession.CreateNew();
        var first = session.ActiveTimeline.Id;
        Assert.True(TimelineCommands.AddTimeline(session, "B", null, out _).Ok);
        Assert.True(TimelineCommands.AddTimeline(session, "C", null, out _).Ok);
        TimelineCommands.EnsureOpenTimelineIds(session);
        Assert.True(TimelineCommands.CloseOtherTimelineTabs(session, first).Ok);
        Assert.Single(session.ProjectFile.OpenTimelineIds!);
        Assert.Equal(first, session.ProjectFile.OpenTimelineIds![0]);
        Assert.Equal(3, session.ProjectFile.Timelines.Count);
    }
}
