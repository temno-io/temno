using Temno.Core.Models;
using Temno.Core.Editing;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-205: the <see cref="EditCommands"/> marker surface the marker-creation UI path depends on.
/// The naming ("Marker N") and frame ordering live in the App layer beside the key handler, which
/// Core must stay free of; what Core must guarantee is that three adds at three playhead positions
/// are three markers, each its own undo step, renameable, restorable, and round-trippable.
/// </summary>
public sealed class MarkerCreationContractTests
{
    [Fact]
    public void ThreeAdds_ProduceThreeNamedMarkers_InFrameOrder()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddMarker(session, 812, "Marker 1").Ok);
        Assert.True(EditCommands.AddMarker(session, 120, "Marker 2").Ok);
        Assert.True(EditCommands.AddMarker(session, 440, "Marker 3").Ok);

        var frames = session.ActiveTimeline.Markers.OrderBy(m => m.Frame).Select(m => m.Frame).ToList();
        Assert.Equal([120, 440, 812], frames);
    }

    [Fact]
    public void EachAdd_IsItsOwnUndoStep()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddMarker(session, 10, "Marker 1").Ok);
        Assert.True(EditCommands.AddMarker(session, 20, "Marker 2").Ok);

        session.Undo();
        Assert.Single(session.ActiveTimeline.Markers);
        Assert.Equal("Marker 1", session.ActiveTimeline.Markers[0].Name);

        session.Redo();
        Assert.Equal(2, session.ActiveTimeline.Markers.Count);
    }

    [Fact]
    public void Rename_CommitsName_AndCancelLeavesIt()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddMarker(session, 10, "Marker 1").Ok);
        var id = session.ActiveTimeline.Markers[0].Id;

        // The rename dialog commits through UpdateMarker; Cancel never calls it.
        Assert.True(EditCommands.UpdateMarker(session, id, frame: null, name: "Intro", color: null, comment: null).Ok);
        Assert.Equal("Intro", session.ActiveTimeline.Markers[0].Name);

        session.Undo();
        Assert.Equal("Marker 1", session.ActiveTimeline.Markers[0].Name);
        session.Redo();
        Assert.Equal("Intro", session.ActiveTimeline.Markers[0].Name);
    }

    [Fact]
    public void Remove_IsItsOwnUndoStep()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddMarker(session, 10, "Marker 1").Ok);
        var id = session.ActiveTimeline.Markers[0].Id;

        Assert.True(EditCommands.RemoveMarker(session, id).Ok);
        Assert.Empty(session.ActiveTimeline.Markers);

        session.Undo();
        Assert.Single(session.ActiveTimeline.Markers);
    }

    [Fact]
    public void Markers_RoundTrip_ThroughSave()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "temno-markers-" + Guid.NewGuid().ToString("N") + ".temno");
        try
        {
            var session = ProjectSession.CreateNew();
            Assert.True(EditCommands.AddMarker(session, 812, "Marker 1").Ok);
            Assert.True(EditCommands.AddMarker(session, 120, "Marker 2").Ok);
            session.Save(dir);

            var reopened = ProjectSession.Open(dir);
            var names = reopened.ActiveTimeline.Markers.OrderBy(m => m.Frame).Select(m => m.Name).ToList();
            Assert.Equal(["Marker 2", "Marker 1"], names);
            Assert.Equal([120, 812], reopened.ActiveTimeline.Markers.OrderBy(m => m.Frame).Select(m => m.Frame).ToList());
        }
        finally
        {
            if (System.IO.Directory.Exists(dir))
                System.IO.Directory.Delete(dir, recursive: true);
        }
    }
}
