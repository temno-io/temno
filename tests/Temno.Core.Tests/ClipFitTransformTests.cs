using System.Linq;
using System.Text.Json.Nodes;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Mac <c>EditorViewModel.placeClip</c> gives every placed clip <c>fitTransform(for: asset)</c>: the
/// whole source at its own aspect. Windows placed clips with the default full-canvas transform, so a
/// 1152x2048 portrait still dropped on a 1920x1080 timeline was stretched to 16:9.
/// </summary>
[Collection("EnvRegistry")]
public sealed class ClipFitTransformTests : IDisposable
{
    private const int Hd = 10;

    public void Dispose() => ProjectHub.ActiveSession = null;

    private static void AssertFills(Transform t)
    {
        Assert.Equal(0.5, t.CenterX, Hd);
        Assert.Equal(0.5, t.CenterY, Hd);
        Assert.Equal(1.0, t.Width, Hd);
        Assert.Equal(1.0, t.Height, Hd);
    }

    [Fact]
    public void Portrait_OnLandscapeCanvas_PillarboxesAtFullHeight()
    {
        var t = LayoutEngine.FitTransform(1152, 2048, 1920, 1080);

        // (1152/2048) / (1920/1080) = 0.31640625: a third of the canvas wide, full height.
        Assert.Equal(0.31640625, t.Width, Hd);
        Assert.Equal(1.0, t.Height, Hd);
        Assert.Equal(0.5, t.CenterX, Hd);
        Assert.Equal(0.5, t.CenterY, Hd);
        Assert.Equal(1152.0 / 2048.0, t.Width * 1920 / (t.Height * 1080), Hd);
    }

    [Fact]
    public void Ultrawide_OnLandscapeCanvas_LetterboxesAtFullWidth()
    {
        var t = LayoutEngine.FitTransform(3840, 1080, 1920, 1080);

        Assert.Equal(1.0, t.Width, Hd);
        Assert.Equal(0.5, t.Height, Hd);
    }

    [Fact]
    public void Landscape_OnPortraitCanvas_LetterboxesAtFullWidth()
    {
        var t = LayoutEngine.FitTransform(1920, 1080, 1080, 1920);

        Assert.Equal(1.0, t.Width, Hd);
        Assert.Equal(0.31640625, t.Height, Hd);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1088)] // Mac aspectTolerance 0.02: a codec-padded 16:9 still fills
    public void CanvasAspectSource_FillsTheCanvas(int w, int h)
        => AssertFills(LayoutEngine.FitTransform(w, h, 1920, 1080));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1152, 0)]
    public void UnknownDimensions_FillTheCanvas(int w, int h)
        => AssertFills(LayoutEngine.FitTransform(w, h, 1920, 1080));

    [Fact]
    public void EntryWithoutProbedDimensions_FillsTheCanvas()
    {
        var timeline = new Timeline { Width = 1920, Height = 1080 };

        AssertFills(LayoutEngine.FitTransform(new MediaManifestEntry { Id = "A", Type = ClipType.audio }, timeline));
        AssertFills(LayoutEngine.FitTransform(null, timeline));
        Assert.Null(LayoutEngine.MediaCanvasAspect(null, 1920, 1080));
    }

    private static ProjectSession SessionWithPortraitStill()
    {
        var session = ProjectSession.CreateNew(fps: 30, width: 1920, height: 1080);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "P1",
            Name = "presenter",
            Type = ClipType.image,
            Duration = 5,
            SourceWidth = 1152,
            SourceHeight = 2048,
        });
        ProjectHub.ActiveSession = session;
        return session;
    }

    [Fact]
    public void AddClips_PlacesAPortraitStillUnstretched()
    {
        var session = SessionWithPortraitStill();

        var result = ToolExecutor.Execute("add_clips", new JsonObject
        {
            ["entries"] = new JsonArray { new JsonObject { ["mediaRef"] = "P1", ["startFrame"] = 0 } },
        });

        Assert.False(result.IsError, result.Content[0].Text);
        var clip = session.ActiveTimeline.Tracks.SelectMany(t => t.Clips).Single();
        Assert.Equal(0.31640625, clip.Transform.Width, Hd);
        Assert.Equal(1.0, clip.Transform.Height, Hd);
    }

    [Fact]
    public void InsertClips_PlacesAPortraitStillUnstretched()
    {
        var session = SessionWithPortraitStill();
        var videoTrack = session.ActiveTimeline.Tracks.FindIndex(t => t.Type == ClipType.video);

        var result = ToolExecutor.Execute("insert_clips", new JsonObject
        {
            ["trackIndex"] = videoTrack,
            ["atFrame"] = 0,
            ["entries"] = new JsonArray { new JsonObject { ["mediaRef"] = "P1" } },
        });

        Assert.False(result.IsError, result.Content[0].Text);
        var clip = session.ActiveTimeline.Tracks[videoTrack].Clips.Single();
        Assert.Equal(0.31640625, clip.Transform.Width, Hd);
        Assert.Equal(1.0, clip.Transform.Height, Hd);
    }
}
