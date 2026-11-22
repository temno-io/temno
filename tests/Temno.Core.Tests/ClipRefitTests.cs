using System.Xml.Linq;
using Temno.Core.Editing;
using Temno.Core.Export;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-405: clips keep their shape when the canvas changes (Mac <c>applyTimelineSettings</c>), take their
/// fit when a size arrives after placement, and export the shape Temno draws.
/// </summary>
public sealed class ClipRefitTests
{
    private const int Hd = 10;

    private static MediaManifestEntry Entry(string id, ClipType type, int? width, int? height) => new()
    {
        Id = id,
        Name = id,
        Type = type,
        Source = MediaSource.External($"C:/media/{id}"),
        SourceWidth = width,
        SourceHeight = height,
    };

    private static Clip ClipOf(string mediaRef, Transform transform, ClipType type = ClipType.video) => new()
    {
        MediaRef = mediaRef,
        MediaType = type,
        SourceClipType = type,
        StartFrame = 0,
        DurationFrames = 30,
        Transform = transform,
    };

    private static (ProjectSession Session, Clip Portrait, Clip Landscape, Clip Legacy, Clip Scaled, Clip Animated, Clip Text) Project()
    {
        var manifest = new MediaManifest
        {
            Entries =
            [
                Entry("portrait", ClipType.image, 1152, 2048),
                Entry("landscape", ClipType.video, 1920, 1080),
            ],
        };
        var portrait = ClipOf("portrait", LayoutEngine.FitTransform(1152, 2048, 1920, 1080), ClipType.image);
        var landscape = ClipOf("landscape", new Transform());
        // Saved by WP-242's harness: a portrait still at full-canvas size.
        var legacy = ClipOf("portrait", new Transform(), ClipType.image);
        var scaled = ClipOf("landscape", new Transform { Width = 0.5, Height = 0.5, CenterX = 0.3 });
        var animated = ClipOf("landscape", new Transform());
        animated.ScaleTrack = new KeyframeTrack<AnimPair>
        {
            Keyframes =
            [
                new Keyframe<AnimPair> { Frame = 0, Value = new AnimPair(1, 1) },
                new Keyframe<AnimPair> { Frame = 20, Value = new AnimPair(0.5, 0.5) },
            ],
        };
        var text = new Clip { MediaType = ClipType.text, TextContent = "Title", StartFrame = 0, DurationFrames = 30, Transform = new Transform { Width = 0.6, Height = 0.2 } };
        var timeline = new Timeline
        {
            Id = "main",
            Fps = 30,
            Width = 1920,
            Height = 1080,
            Tracks =
            [
                new Track { Type = ClipType.video, Clips = [portrait] },
                new Track { Type = ClipType.video, Clips = [landscape] },
                new Track { Type = ClipType.video, Clips = [legacy] },
                new Track { Type = ClipType.video, Clips = [scaled] },
                new Track { Type = ClipType.video, Clips = [animated] },
                new Track { Type = ClipType.text, Clips = [text] },
            ],
        };
        var session = new ProjectSession(new ProjectFile { Timelines = [timeline], ActiveTimelineId = "main" }, manifest);
        return (session, portrait, landscape, legacy, scaled, animated, text);
    }

    private static double DrawnAspect(Clip clip, int sourceWidth, int sourceHeight, int canvasWidth, int canvasHeight)
    {
        var drawn = ClipPlacement.FitInBox(clip.Transform, sourceWidth, sourceHeight, canvasWidth, canvasHeight);
        return drawn.Width * canvasWidth / (drawn.Height * canvasHeight);
    }

    [Fact]
    public void LandscapeToPortraitCanvas_FittedClipsTakeTheNewFit()
    {
        var (session, portrait, landscape, legacy, _, _, _) = Project();

        Assert.True(EditCommands.SetProjectSettings(session, null, null, null, "9:16", null).Ok);

        Assert.Equal((1080, 1920), (session.ActiveTimeline.Width, session.ActiveTimeline.Height));
        // The portrait still now fills the vertical canvas.
        Assert.Equal(1.0, portrait.Transform.Width, Hd);
        Assert.Equal(1.0, portrait.Transform.Height, Hd);
        // The 16:9 video fits across it: full width, 0.316 of the height.
        Assert.Equal(1.0, landscape.Transform.Width, Hd);
        Assert.Equal(0.31640625, landscape.Transform.Height, Hd);
        // The legacy full-canvas box drew the portrait fit, so it re-fits like a fitted clip.
        Assert.Equal(1.0, legacy.Transform.Width, Hd);
        Assert.Equal(1.0, legacy.Transform.Height, Hd);
    }

    [Fact]
    public void RoundTrip_KeepsEveryDrawnShapeWithinOnePercent()
    {
        var (session, portrait, landscape, legacy, scaled, _, _) = Project();
        var before = new[]
        {
            DrawnAspect(portrait, 1152, 2048, 1920, 1080), DrawnAspect(landscape, 1920, 1080, 1920, 1080),
            DrawnAspect(legacy, 1152, 2048, 1920, 1080), DrawnAspect(scaled, 1920, 1080, 1920, 1080),
        };

        Assert.True(EditCommands.SetProjectSettings(session, null, null, null, "9:16", null).Ok);
        var vertical = new[]
        {
            DrawnAspect(portrait, 1152, 2048, 1080, 1920), DrawnAspect(landscape, 1920, 1080, 1080, 1920),
            DrawnAspect(legacy, 1152, 2048, 1080, 1920), DrawnAspect(scaled, 1920, 1080, 1080, 1920),
        };
        Assert.True(EditCommands.SetProjectSettings(session, null, null, null, "16:9", null).Ok);
        var after = new[]
        {
            DrawnAspect(portrait, 1152, 2048, 1920, 1080), DrawnAspect(landscape, 1920, 1080, 1920, 1080),
            DrawnAspect(legacy, 1152, 2048, 1920, 1080), DrawnAspect(scaled, 1920, 1080, 1920, 1080),
        };

        for (var i = 0; i < before.Length; i++)
        {
            Assert.InRange(vertical[i] / before[i], 0.99, 1.01);
            Assert.InRange(after[i] / before[i], 0.99, 1.01);
        }
        // The user's half-size box keeps its width and position.
        Assert.Equal(0.5, scaled.Transform.Width, Hd);
        Assert.Equal(0.3, scaled.Transform.CenterX, Hd);
    }

    [Fact]
    public void AnimatedScale_ScalesItsHeightsLikeTheMac()
    {
        var (session, _, _, _, _, animated, text) = Project();

        Assert.True(EditCommands.SetProjectSettings(session, null, null, null, "9:16", null).Ok);

        // oldAspect / newAspect = (9/16) / (16/9) = 0.31640625.
        Assert.Equal(1.0, animated.Transform.Width, Hd);
        Assert.Equal(0.31640625, animated.Transform.Height, Hd);
        Assert.Equal(0.31640625, animated.ScaleTrack!.Keyframes[0].Value.B, Hd);
        Assert.Equal(0.158203125, animated.ScaleTrack.Keyframes[1].Value.B, Hd);
        Assert.Equal(0.5, animated.ScaleTrack.Keyframes[1].Value.A, Hd);
        // Text has no source size and is left alone, as on the Mac.
        Assert.Equal(0.2, text.Transform.Height, Hd);
    }

    [Fact]
    public void Undo_RestoresEveryTransform()
    {
        var (session, portrait, landscape, _, _, _, _) = Project();

        Assert.True(EditCommands.SetProjectSettings(session, null, null, null, "9:16", null).Ok);
        session.Undo();

        var restored = session.ActiveTimeline;
        Assert.Equal((1920, 1080), (restored.Width, restored.Height));
        var p = restored.Tracks[0].Clips[0];
        var l = restored.Tracks[1].Clips[0];
        Assert.Equal(portrait.MediaRef, p.MediaRef);
        Assert.Equal(0.31640625, p.Transform.Width, Hd);
        Assert.Equal(1.0, p.Transform.Height, Hd);
        Assert.Equal(1.0, l.Transform.Width, Hd);
        Assert.Equal(1.0, l.Transform.Height, Hd);
        Assert.Equal(landscape.MediaRef, l.MediaRef);
    }

    [Fact]
    public void SizeArrivingAfterPlacement_RefitsOnlyUntouchedClips()
    {
        var entry = Entry("generated", ClipType.image, null, null);
        var placedWhileGenerating = ClipOf("generated", LayoutEngine.FitTransform(entry, new Timeline { Width = 1920, Height = 1080 }), ClipType.image);
        var userScaled = ClipOf("generated", new Transform { Width = 0.4, Height = 0.4 }, ClipType.image);
        var other = ClipOf("someone-else", new Transform(), ClipType.image);
        var timeline = new Timeline
        {
            Width = 1920,
            Height = 1080,
            Tracks = [new Track { Type = ClipType.video, Clips = [placedWhileGenerating, userScaled, other] }],
        };
        Assert.Equal(1.0, placedWhileGenerating.Transform.Width, Hd);

        entry.SourceWidth = 1152;
        entry.SourceHeight = 2048;
        Assert.Equal(1, ClipRefit.AfterSourceSizeKnown([timeline], entry));

        Assert.Equal(0.31640625, placedWhileGenerating.Transform.Width, Hd);
        Assert.Equal(1.0, placedWhileGenerating.Transform.Height, Hd);
        Assert.Equal(0.4, userScaled.Transform.Width, Hd);
        Assert.Equal(1.0, other.Transform.Width, Hd);
    }

    [Fact]
    public void FcpXml_ExportsTheDrawnShapeAsOneUniformScale()
    {
        var manifest = new MediaManifest { Entries = [Entry("portrait", ClipType.video, 1080, 1920)] };
        var resolver = new MediaResolver(() => manifest, () => "");
        var clip = ClipOf("portrait", new Transform { Width = 0.5, Height = 0.5 });
        clip.Id = "c-1";
        var timeline = new Timeline
        {
            Id = "t-1",
            Name = "Shape",
            Fps = 30,
            Width = 1920,
            Height = 1080,
            Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
        };

        var doc = XDocument.Parse(FcpXmlExporter.Render(timeline, resolver));
        var scale = doc.Descendants("adjust-transform").Single().Attribute("scale")!.Value.Split(' ');

        // A half-canvas box holding a portrait draws half its fit, on both axes; before WP-405 this read "1.58 0.5".
        Assert.Equal(scale[0], scale[1]);
        Assert.Equal("0.5", scale[0]);
    }

    [Fact]
    public void UnprobedImport_HasNoGuessedSize()
    {
        var root = Path.Combine(Path.GetTempPath(), "wp405-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(root, "p.temno");
            var session = ProjectSession.CreateNew("Timeline 1", fps: 30, width: 1920, height: 1080);
            Directory.CreateDirectory(package);
            session.Save(package);
            var source = Path.Combine(root, "still.png");
            File.WriteAllBytes(source, [137, 80, 78, 71]);

            Assert.True(MediaLibraryCommands.ImportFile(session, source, null, null, out var entry).Ok);
            Assert.Null(entry!.SourceWidth);
            Assert.Null(entry.SourceHeight);
            // Placement then draws the picture at its decoded shape (ClipPlacement.FitInBox).
            AssertFull(LayoutEngine.FitTransform(entry, session.ActiveTimeline));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertFull(Transform t)
    {
        Assert.Equal(1.0, t.Width, Hd);
        Assert.Equal(1.0, t.Height, Hd);
    }
}
