using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Temno.Core.Models;
using Temno.Core.IO;
using Temno.Core.Export;
using Xunit;

namespace Temno.Core.Tests;

public class FcpXmlExporterTests
{
    private class ManifestBuilder
    {
        public MediaManifest Manifest { get; } = new();

        public void AddVideo(string mediaRef, string absolutePath, double duration = 10.0, int w = 1920, int h = 1080, double fps = 30.0, bool hasAudio = true)
        {
            Manifest.Entries.Add(new MediaManifestEntry
            {
                Id = mediaRef,
                Name = Path.GetFileName(absolutePath),
                Type = ClipType.video,
                Source = MediaSource.External(absolutePath),
                Duration = duration,
                SourceWidth = w,
                SourceHeight = h,
                SourceFps = fps,
                HasAudio = hasAudio
            });
        }

        public void AddAudio(string mediaRef, string absolutePath, double duration = 15.0)
        {
            Manifest.Entries.Add(new MediaManifestEntry
            {
                Id = mediaRef,
                Name = Path.GetFileName(absolutePath),
                Type = ClipType.audio,
                Source = MediaSource.External(absolutePath),
                Duration = duration,
                HasAudio = true,
                SourceFps = 30.0
            });
        }
    }

    [Fact]
    public void Render_EmptyTimeline_GeneratesValidXmlStructure()
    {
        // Arrange
        var timeline = new Timeline
        {
            Id = "t-1",
            Name = "Empty Timeline",
            Fps = 30,
            Width = 1920,
            Height = 1080,
            Tracks = new List<Track>()
        };
        var builder = new ManifestBuilder();
        var resolver = new MediaResolver(() => builder.Manifest, () => "");

        // Act
        var xmlString = FcpXmlExporter.Render(timeline, resolver);

        // Assert
        Assert.NotNull(xmlString);
        var doc = XDocument.Parse(xmlString);
        var root = Assert.IsType<XElement>(doc.Root);
        Assert.Equal("fcpxml", root.Name.LocalName);
        Assert.Equal("1.10", root.Attribute("version")?.Value);

        var resources = root.Element("resources");
        Assert.NotNull(resources);

        // Default format resource should be present
        var format = resources.Element("format");
        Assert.NotNull(format);
        Assert.Equal("r1", format.Attribute("id")?.Value);
        Assert.Equal("1920", format.Attribute("width")?.Value);
        Assert.Equal("1080", format.Attribute("height")?.Value);

        var library = root.Element("library");
        Assert.NotNull(library);
        var eventNode = library.Element("event");
        Assert.NotNull(eventNode);
        var project = eventNode.Element("project");
        Assert.NotNull(project);
        Assert.Equal("Empty Timeline", project.Attribute("name")?.Value);

        var sequence = project.Element("sequence");
        Assert.NotNull(sequence);
        Assert.Equal("r1", sequence.Attribute("format")?.Value);
        Assert.Equal("0s", sequence.Attribute("duration")?.Value);

        var spine = sequence.Element("spine");
        Assert.NotNull(spine);
        // An empty timeline spine is completely empty (no gap)
        Assert.Empty(spine.Elements());
    }

    [Fact]
    public void Render_SingleVideoClip_EmitsAssetAndSpinePlacement()
    {
        // Arrange
        var timeline = new Timeline
        {
            Id = "t-1",
            Name = "Single Video Timeline",
            Fps = 30,
            Width = 1920,
            Height = 1080
        };

        var track = new Track { Type = ClipType.video, Muted = false };
        var clip = new Clip
        {
            Id = "c-1",
            MediaType = ClipType.video,
            MediaRef = "video-1",
            StartFrame = 0,
            DurationFrames = 150,
            TrimStartFrame = 30,
            TrimEndFrame = 0,
            Speed = 1.0,
            Volume = 0.5,
            Transform = new Transform { Width = 1.2, Height = 1.2, CenterX = 0.6, CenterY = 0.4 }
        };
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);

        var builder = new ManifestBuilder();
        builder.AddVideo("video-1", "/assets/clip1.mp4", duration: 10.0, w: 1920, h: 1080, fps: 30.0, hasAudio: false);
        var resolver = new MediaResolver(() => builder.Manifest, () => "");

        // Act
        var xmlString = FcpXmlExporter.Render(timeline, resolver);

        // Assert
        var doc = XDocument.Parse(xmlString);
        var root = Assert.IsType<XElement>(doc.Root);

        // Check assets
        var resources = root.Element("resources");
        var asset = resources!.Elements("asset").FirstOrDefault(a => a.Attribute("id")?.Value == "asset1");
        Assert.NotNull(asset);
        Assert.Equal("clip1.mp4", asset.Attribute("name")?.Value);
        Assert.Equal("1", asset.Attribute("hasVideo")?.Value);
        Assert.Null(asset.Attribute("hasAudio")); // Should be omitted since hasAudio was false

        // Check spine placement (emittable clips are inside a gap in the spine)
        var spine = root.Element("library")!.Element("event")!.Element("project")!.Element("sequence")!.Element("spine");
        var gap = spine!.Element("gap");
        Assert.NotNull(gap);
        var assetClip = gap.Element("asset-clip");
        Assert.NotNull(assetClip);
        Assert.Equal("asset1", assetClip.Attribute("ref")?.Value);
        Assert.Equal("0s", assetClip.Attribute("offset")?.Value);
        Assert.Equal("5s", assetClip.Attribute("duration")?.Value);
        Assert.Equal("1s", assetClip.Attribute("start")?.Value);

        // Check Transform parameters
        var adjustTransform = assetClip.Element("adjust-transform");
        Assert.NotNull(adjustTransform);
        Assert.Equal("1.2 1.2", adjustTransform.Attribute("scale")?.Value);
        // unit = seqHeight/100.0 = 10.8. CenterX = 0.6 -> (0.6 - 0.5)*1920 / 10.8 = 17.7778
        // CenterY = 0.4 -> (0.5 - 0.4)*1080 / 10.8 = 10
        Assert.Contains("17.7778", adjustTransform.Attribute("position")?.Value);
        Assert.Contains("10", adjustTransform.Attribute("position")?.Value);
    }

    [Fact]
    public void Render_RetimeSpeedMap_EmitsTimeMap()
    {
        // Arrange
        var timeline = new Timeline
        {
            Id = "t-1",
            Name = "Speed Timeline",
            Fps = 30,
            Width = 1920,
            Height = 1080
        };

        var track = new Track { Type = ClipType.video };
        var clip = new Clip
        {
            Id = "c-1",
            MediaType = ClipType.video,
            MediaRef = "video-1",
            StartFrame = 0,
            DurationFrames = 60, // Played duration (e.g. 60 frames)
            TrimStartFrame = 0,
            TrimEndFrame = 0,
            Speed = 2.0
        };
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);

        var builder = new ManifestBuilder();
        builder.AddVideo("video-1", "/assets/speedy.mp4", duration: 10.0, fps: 30.0, hasAudio: false);
        var resolver = new MediaResolver(() => builder.Manifest, () => "");

        // Act
        var xmlString = FcpXmlExporter.Render(timeline, resolver);

        // Assert
        var doc = XDocument.Parse(xmlString);
        var gap = doc.Root!.Element("library")!.Element("event")!.Element("project")!.Element("sequence")!.Element("spine")!.Element("gap");
        Assert.NotNull(gap);
        var assetClip = gap.Element("asset-clip");
        Assert.NotNull(assetClip);

        var timeMap = assetClip.Element("timeMap");
        Assert.NotNull(timeMap);
        var timepts = timeMap.Elements("timept").ToList();
        Assert.Equal(2, timepts.Count);

        // Point 1: time=0s, value=0s
        Assert.Equal("0s", timepts[0].Attribute("time")?.Value);
        Assert.Equal("0s", timepts[0].Attribute("value")?.Value);

        // Point 2: duration of the entire source asset is 10.0s (300 frames). At 2x speed, time is 5s.
        Assert.Equal("5s", timepts[1].Attribute("time")?.Value);
        Assert.Equal("10s", timepts[1].Attribute("value")?.Value);
    }

    [Fact]
    public void Render_TextTitleClip_EmitsTitleAndTextStyleDef()
    {
        // Arrange
        var timeline = new Timeline
        {
            Id = "t-1",
            Name = "Title Timeline",
            Fps = 30,
            Width = 1920,
            Height = 1080
        };

        var styleObj = new TextStyle
        {
            FontName = "Helvetica-Bold",
            FontSize = 64,
            FontScale = 1.0,
            IsBold = true,
            IsItalic = false,
            Alignment = TextAlignment.Center,
            Color = new TextStyle.RGBA { R = 1, G = 0, B = 0, A = 1 }
        };
        var styleJson = JsonSerializer.Serialize(styleObj);
        var styleDoc = JsonDocument.Parse(styleJson);

        var track = new Track { Type = ClipType.video };
        var clip = new Clip
        {
            Id = "c-1",
            MediaType = ClipType.text,
            TextContent = "Hello Temno",
            StartFrame = 30,
            DurationFrames = 90,
            TextStyle = styleDoc.RootElement
        };
        track.Clips.Add(clip);
        timeline.Tracks.Add(track);

        var builder = new ManifestBuilder();
        var resolver = new MediaResolver(() => builder.Manifest, () => "");

        // Act
        var xmlString = FcpXmlExporter.Render(timeline, resolver);

        // Assert
        var doc = XDocument.Parse(xmlString);
        var root = Assert.IsType<XElement>(doc.Root);

        // Check text style definition in resources
        var resources = root.Element("resources");
        var effect = resources!.Element("effect");
        Assert.NotNull(effect);
        Assert.Equal("titleBasic", effect.Attribute("id")?.Value);

        var spine = root.Element("library")!.Element("event")!.Element("project")!.Element("sequence")!.Element("spine");
        var gap = spine!.Element("gap");
        Assert.NotNull(gap);
        var title = gap.Element("title");
        Assert.NotNull(title);
        Assert.Equal("titleBasic", title.Attribute("ref")?.Value);
        Assert.Equal("1s", title.Attribute("offset")?.Value);
        Assert.Equal("3s", title.Attribute("duration")?.Value);

        // Text style def should match styling
        var textStyleDef = title.Element("text-style-def");
        Assert.NotNull(textStyleDef);
        var textStyle = textStyleDef.Element("text-style");
        Assert.NotNull(textStyle);
        Assert.Equal("Helvetica", textStyle.Attribute("font")?.Value);
        Assert.Equal("Bold", textStyle.Attribute("fontFace")?.Value);
        Assert.Equal("64", textStyle.Attribute("fontSize")?.Value);
        Assert.Equal("1 0 0 1", textStyle.Attribute("fontColor")?.Value);
        Assert.Equal("center", textStyle.Attribute("alignment")?.Value);
    }

    [Fact]
    public void Render_SyncedAudioVideoClips_ConsolidatesToSingleAssetClip()
    {
        // Arrange
        var timeline = new Timeline
        {
            Id = "t-1",
            Name = "Sync Timeline",
            Fps = 30,
            Width = 1920,
            Height = 1080
        };

        var videoTrack = new Track { Type = ClipType.video };
        var videoClip = new Clip
        {
            Id = "v-clip",
            MediaType = ClipType.video,
            MediaRef = "av-sync",
            StartFrame = 0,
            DurationFrames = 150,
            TrimStartFrame = 0,
            TrimEndFrame = 0,
            LinkGroupId = "group-123"
        };
        videoTrack.Clips.Add(videoClip);

        var audioTrack = new Track { Type = ClipType.audio };
        var audioClip = new Clip
        {
            Id = "a-clip",
            MediaType = ClipType.audio,
            MediaRef = "av-sync",
            StartFrame = 0,
            DurationFrames = 150,
            TrimStartFrame = 0,
            TrimEndFrame = 0,
            LinkGroupId = "group-123",
            Volume = 0.8 // Volume should be applied to consolidated clip
        };
        audioTrack.Clips.Add(audioClip);

        timeline.Tracks.Add(videoTrack);
        timeline.Tracks.Add(audioTrack);

        var builder = new ManifestBuilder();
        builder.AddVideo("av-sync", "/assets/merged.mp4", duration: 15.0, w: 1920, h: 1080, fps: 30.0, hasAudio: true);
        var resolver = new MediaResolver(() => builder.Manifest, () => "");

        // Act
        var xmlString = FcpXmlExporter.Render(timeline, resolver);

        // Assert
        var doc = XDocument.Parse(xmlString);
        var spine = doc.Root!.Element("library")!.Element("event")!.Element("project")!.Element("sequence")!.Element("spine");
        var gap = spine!.Element("gap");
        Assert.NotNull(gap);

        // There should be only ONE asset-clip in the spine representing both tracks
        var assetClips = gap.Elements("asset-clip").ToList();
        Assert.Single(assetClips);

        var consolidatedClip = assetClips[0];
        Assert.Equal("asset1", consolidatedClip.Attribute("ref")?.Value);

        // The audio volume adjust-volume should be emitted inside the asset-clip
        var adjustVolume = consolidatedClip.Element("adjust-volume");
        Assert.NotNull(adjustVolume);
        // Decibel conversion for 0.8: 20 * log10(0.8) = -1.9382
        Assert.Contains("-1.938", adjustVolume.Attribute("amount")?.Value);
    }

    [Fact]
    public void Render_NestedTimeline_EmitsRefClip()
    {
        // Arrange
        var mainTimeline = new Timeline
        {
            Id = "main",
            Name = "Main Timeline",
            Fps = 30,
            Width = 1920,
            Height = 1080
        };

        var track = new Track { Type = ClipType.video };
        var refClip = new Clip
        {
            Id = "nested-clip",
            MediaType = ClipType.video,
            SourceClipType = ClipType.sequence,
            MediaRef = "nested-timeline-id", // Points to the nested timeline
            StartFrame = 30,
            DurationFrames = 90,
            TrimStartFrame = 0,
            TrimEndFrame = 0
        };
        track.Clips.Add(refClip);
        mainTimeline.Tracks.Add(track);

        var nestedTimeline = new Timeline
        {
            Id = "nested-timeline-id",
            Name = "Nested Timeline",
            Fps = 30,
            Width = 1920,
            Height = 1080
        };
        var nestedTrack = new Track { Type = ClipType.video };
        var videoClip = new Clip
        {
            Id = "nested-v",
            MediaType = ClipType.video,
            MediaRef = "video-1",
            StartFrame = 0,
            DurationFrames = 90,
            TrimStartFrame = 0,
            TrimEndFrame = 0
        };
        nestedTrack.Clips.Add(videoClip);
        nestedTimeline.Tracks.Add(nestedTrack);

        var builder = new ManifestBuilder();
        builder.AddVideo("video-1", "/assets/clip.mp4", duration: 10.0, fps: 30.0);
        var resolver = new MediaResolver(() => builder.Manifest, () => "");

        Timeline? ResolveTimeline(string id)
        {
            return id == "nested-timeline-id" ? nestedTimeline : null;
        }

        // Act
        var xmlString = FcpXmlExporter.Render(mainTimeline, resolver, ResolveTimeline);

        // Assert
        var doc = XDocument.Parse(xmlString);
        var root = Assert.IsType<XElement>(doc.Root);

        // Verify ref-clip in resources
        var resources = root.Element("resources");
        var media = resources!.Elements("media").FirstOrDefault(m => m.Attribute("id")?.Value == "nest1");
        Assert.NotNull(media);
        Assert.Equal("Nested Timeline", media.Attribute("name")?.Value);

        // Verify spine has a ref-clip
        var spine = root.Element("library")!.Element("event")!.Element("project")!.Element("sequence")!.Element("spine");
        var gap = spine!.Element("gap");
        Assert.NotNull(gap);
        var xmlRefClip = gap.Element("ref-clip");
        Assert.NotNull(xmlRefClip);
        Assert.Equal("nest1", xmlRefClip.Attribute("ref")?.Value);
        Assert.Equal("1s", xmlRefClip.Attribute("offset")?.Value);
        Assert.Equal("3s", xmlRefClip.Attribute("duration")?.Value);
    }

    // WP-236 E-2: the nest keeps its own frame rate; the carrier converts placement once.
    [Theory]
    [InlineData(30, 24)]
    [InlineData(24, 30)]
    public void Render_MixedRateNest_PreservesSecondsTrimsFormatAndOrigin(int rootFps, int childFps)
    {
        var (root, lookup, resolver) = XmemlExporterTests.MixedRateFixture(rootFps, childFps);
        var startTimecodes = new Dictionary<string, SourceTimecode>
        {
            ["cam"] = new SourceTimecode(3600 * childFps, childFps, dropFrame: false),
        };

        var doc = XDocument.Parse(FcpXmlExporter.Render(root, resolver, lookup, startTimecodes: startTimecodes));
        var resources = doc.Root!.Element("resources")!;
        var formats = resources.Elements("format").ToDictionary(f => f.Attribute("id")!.Value);

        var project = doc.Root.Element("library")!.Element("event")!.Element("project")!.Element("sequence")!;
        var rootFormat = formats[project.Attribute("format")!.Value];
        AssertTime((1, rootFps), rootFormat.Attribute("frameDuration")!.Value);
        AssertTime((3, 1), project.Attribute("duration")!.Value);

        var nest = resources.Elements("media").Single(m => m.Attribute("name")?.Value == "Child");
        var nestSequence = nest.Element("sequence")!;
        var nestFormat = formats[nestSequence.Attribute("format")!.Value];
        Assert.NotSame(rootFormat, nestFormat);
        AssertTime((1, childFps), nestFormat.Attribute("frameDuration")!.Value);
        Assert.Equal("1920", nestFormat.Attribute("width")!.Value);
        Assert.Equal("1080", nestFormat.Attribute("height")!.Value);
        AssertTime((3, 1), nestSequence.Attribute("duration")!.Value);

        var asset = resources.Elements("asset").Single();
        AssertTime((3600, 1), asset.Attribute("start")!.Value);
        AssertTime((10, 1), asset.Attribute("duration")!.Value);

        var inner = nestSequence.Descendants("asset-clip").Single();
        AssertTime((1, 1), inner.Attribute("offset")!.Value);
        AssertTime((7201, 2), inner.Attribute("start")!.Value); // origin 3600 s + 0.5 s trim
        AssertTime((2, 1), inner.Attribute("duration")!.Value);

        var carrier = project.Descendants("ref-clip").Single();
        Assert.Equal(nest.Attribute("id")!.Value, carrier.Attribute("ref")!.Value);
        AssertTime((1, 1), carrier.Attribute("offset")!.Value);
        AssertTime((1, 2), carrier.Attribute("start")!.Value);
        AssertTime((2, 1), carrier.Attribute("duration")!.Value);
    }

    [Fact]
    public void Render_SameRateSameSizeNest_SharesTheSequenceFormat()
    {
        var (root, lookup, resolver) = XmemlExporterTests.MixedRateFixture(30, 30);
        var doc = XDocument.Parse(FcpXmlExporter.Render(root, resolver, lookup));
        var nest = doc.Root!.Element("resources")!.Elements("media").Single(m => m.Attribute("name")?.Value == "Child");
        Assert.Equal("r1", nest.Element("sequence")!.Attribute("format")!.Value);
    }

    // WP-236 E-4: a retimed clip keeps its source origin in the timeMap values rather than in
    // `start`. Whichever way the exporter spells it, the source second at the clip's first frame
    // must be the embedded timecode origin plus the trim.
    [Theory]
    [InlineData(30, 1.0, false)]
    [InlineData(30, 2.0, false)]
    [InlineData(30, 0.5, false)]
    [InlineData(24, 2.0, false)]
    [InlineData(30, 1.0, true)]
    [InlineData(30, 2.0, true)]
    public void Render_SourceOriginAtFirstFrame_IsOriginPlusTrim(int fps, double speed, bool withAudio)
    {
        var timeline = new Timeline { Id = "t", Name = "T", Fps = fps, Width = 1920, Height = 1080 };
        var track = new Track { Type = ClipType.video };
        track.Clips.Add(new Clip
        {
            Id = "c",
            MediaType = ClipType.video,
            MediaRef = "cam",
            StartFrame = 0,
            DurationFrames = 2 * fps,
            TrimStartFrame = fps, // 1 s into the source
            Speed = speed,
        });
        timeline.Tracks.Add(track);
        var builder = new ManifestBuilder();
        builder.AddVideo("cam", @"C:\media\cam.mp4", duration: 10.0, fps: fps, hasAudio: withAudio);
        var resolver = new MediaResolver(() => builder.Manifest, () => "");
        var startTimecodes = new Dictionary<string, SourceTimecode>
        {
            ["cam"] = new SourceTimecode(3600 * fps, fps, dropFrame: false),
        };

        var doc = XDocument.Parse(FcpXmlExporter.Render(timeline, resolver, startTimecodes: startTimecodes));
        var resources = doc.Root!.Element("resources")!;
        var asset = resources.Elements("asset").Single();
        AssertTime((3600, 1), asset.Attribute("start")!.Value);

        var gap = doc.Root.Element("library")!.Element("event")!.Element("project")!.Element("sequence")!
            .Element("spine")!.Element("gap")!;
        var placed = gap.Elements().Single(e => e.Name.LocalName is "asset-clip" or "ref-clip");

        // A compound ref-clip reads the asset from its own 0-based spine, whose inner clip starts
        // at the asset origin; a flat asset-clip reads the asset's time directly.
        var baseSeconds = Seconds("0s");
        if (placed.Name.LocalName == "ref-clip")
        {
            var compound = resources.Elements("media").Single(m => m.Attribute("id")!.Value == placed.Attribute("ref")!.Value);
            baseSeconds = Seconds(compound.Descendants("asset-clip").Single().Attribute("start")!.Value);
        }

        var local = Seconds(placed.Attribute("start")!.Value);
        var sourceAtFirstFrame = placed.Element("timeMap") is { } map
            ? Interpolate(map, local)
            : local;
        Assert.Equal(3601m, baseSeconds + sourceAtFirstFrame);
    }

    /// <summary>
    /// WP-236 T1 finding 3: an asset is as long as its source, counted on the frame grid its own
    /// <c>format</c> declares, so a 10.1 s 30 fps source is <c>101/10s</c> in a 24 fps sequence as in a
    /// 30 fps one — and so is the timeMap endpoint that scales it. Before: 121/12s at 24.
    /// </summary>
    [Theory]
    [InlineData(24, 1.0)]
    [InlineData(30, 1.0)]
    [InlineData(24, 2.0)]
    [InlineData(30, 2.0)]
    public void Render_AssetDuration_DoesNotDependOnTheSequenceRate(int fps, double speed)
    {
        var timeline = new Timeline { Id = "t", Name = "T", Fps = fps, Width = 1920, Height = 1080 };
        timeline.Tracks.Add(new Track
        {
            Type = ClipType.video,
            Clips = [new Clip { Id = "c", MediaType = ClipType.video, MediaRef = "cam", DurationFrames = fps, Speed = speed }],
        });
        var builder = new ManifestBuilder();
        builder.AddVideo("cam", @"C:\media\cam.mp4", duration: 10.1, fps: 30, hasAudio: false);
        var resolver = new MediaResolver(() => builder.Manifest, () => "");

        var doc = XDocument.Parse(FcpXmlExporter.Render(timeline, resolver));

        AssertTime((101, 10), doc.Root!.Element("resources")!.Elements("asset").Single().Attribute("duration")!.Value);
        if (speed != 1.0)
            AssertTime((101, 20), doc.Descendants("timept").Last().Attribute("time")!.Value);
    }

    private static decimal Interpolate(XElement timeMap, decimal localTime)
    {
        var pts = timeMap.Elements("timept")
            .Select(p => (T: Seconds(p.Attribute("time")!.Value), V: Seconds(p.Attribute("value")!.Value)))
            .ToList();
        Assert.Equal(2, pts.Count);
        return pts[0].V + (localTime - pts[0].T) * (pts[1].V - pts[0].V) / (pts[1].T - pts[0].T);
    }

    private static (long N, long D) ParseTime(string value)
    {
        Assert.EndsWith("s", value);
        var body = value[..^1];
        var slash = body.IndexOf('/');
        return slash < 0
            ? (long.Parse(body), 1)
            : (long.Parse(body[..slash]), long.Parse(body[(slash + 1)..]));
    }

    private static decimal Seconds(string value)
    {
        var (n, d) = ParseTime(value);
        return (decimal)n / d;
    }

    private static void AssertTime((long N, long D) expected, string actual)
    {
        var (n, d) = ParseTime(actual);
        Assert.True(n * expected.D == expected.N * d, $"expected {expected.N}/{expected.D}s, got {actual}");
    }
}
