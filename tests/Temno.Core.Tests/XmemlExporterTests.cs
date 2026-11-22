using System.Xml.Linq;
using Temno.Core.Export;
using Temno.Core.IO;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public sealed class XmemlExporterTests
{
    [Fact]
    public void Render_EmitsXmemlShellWithClipPlacement()
    {
        var timeline = new Timeline
        {
            Id = "T1",
            Name = "Main",
            Fps = 30,
            Width = 1920,
            Height = 1080,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            Id = "c1",
                            MediaRef = "asset",
                            MediaType = ClipType.video,
                            StartFrame = 10,
                            DurationFrames = 60,
                            TrimStartFrame = 5,
                        },
                    ],
                },
            ],
        };
        var manifest = new MediaManifest
        {
            Entries =
            [
                new MediaManifestEntry
                {
                    Id = "asset",
                    Name = "Cam A",
                    Type = ClipType.video,
                    Duration = 10,
                    SourceWidth = 1920,
                    SourceHeight = 1080,
                    Source = MediaSource.External(@"C:\media\a.mp4"),
                },
            ],
        };
        var resolver = new MediaResolver(() => manifest, () => null);
        var xml = XmemlExporter.Render(timeline, resolver);
        Assert.Contains("<!DOCTYPE xmeml>", xml);
        Assert.Contains("version=\"4\"", xml);

        var doc = XDocument.Parse(xml);
        Assert.Equal("xmeml", doc.Root!.Name.LocalName);
        var clipitem = doc.Descendants("clipitem").First();
        Assert.Equal("10", clipitem.Element("start")?.Value);
        Assert.Equal("70", clipitem.Element("end")?.Value);
        Assert.Equal("5", clipitem.Element("in")?.Value);
        Assert.Equal("65", clipitem.Element("out")?.Value); // 5 + 60*1
        Assert.Contains("file://localhost//", clipitem.Descendants("pathurl").First().Value);
    }

    [Fact]
    public void Render_NestedSequence_EmbedsChildSequence()
    {
        var nested = new Timeline
        {
            Id = "T-NEST",
            Name = "Nested",
            Fps = 30,
            Width = 1280,
            Height = 720,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            MediaRef = "inner",
                            MediaType = ClipType.video,
                            DurationFrames = 30,
                        },
                    ],
                },
            ],
        };
        var main = new Timeline
        {
            Id = "T-MAIN",
            Name = "Main",
            Fps = 30,
            Width = 1920,
            Height = 1080,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            MediaRef = "T-NEST",
                            MediaType = ClipType.sequence,
                            SourceClipType = ClipType.sequence,
                            DurationFrames = 30,
                        },
                    ],
                },
            ],
        };
        var manifest = new MediaManifest
        {
            Entries =
            [
                new MediaManifestEntry
                {
                    Id = "inner",
                    Type = ClipType.video,
                    Duration = 5,
                    Source = MediaSource.External("/x.mp4"),
                },
            ],
        };
        var resolver = new MediaResolver(() => manifest, () => null);
        Timeline? Lookup(string id) => id == nested.Id ? nested : null;
        var xml = XmemlExporter.Render(main, resolver, Lookup);
        var doc = XDocument.Parse(xml);
        var sequences = doc.Descendants("sequence").ToList();
        Assert.True(sequences.Count >= 2);
        Assert.Contains(sequences, s => s.Element("name")?.Value == "Nested");
    }

    [Fact]
    public void FormatTimecode_NonDrop()
    {
        Assert.Equal("00:00:01:00", XmemlExporter.FormatTimecode(30, 30, dropFrame: false));
        Assert.Equal("01:00:00:00", XmemlExporter.FormatTimecode(30 * 3600, 30, dropFrame: false));
    }

    // WP-236 E-1. Expected labels come from the SMPTE ST 12-1 count formula,
    // count = nominal − D × (minutes − minutes / 10), worked by hand — not from the formatter.
    [Theory]
    [InlineData(0, 30, "00;00;00;00")]
    [InlineData(2, 30, "00;00;00;02")]
    [InlineData(1799, 30, "00;00;59;29")]
    [InlineData(1800, 30, "00;01;00;02")]
    [InlineData(1801, 30, "00;01;00;03")]
    [InlineData(3597, 30, "00;01;59;29")]
    [InlineData(3598, 30, "00;02;00;02")]
    [InlineData(17981, 30, "00;09;59;29")]
    [InlineData(17982, 30, "00;10;00;00")]
    [InlineData(17983, 30, "00;10;00;01")]
    [InlineData(19781, 30, "00;10;59;29")]
    [InlineData(19782, 30, "00;11;00;02")]
    [InlineData(107891, 30, "00;59;59;29")]
    [InlineData(107892, 30, "01;00;00;00")]
    [InlineData(109691, 30, "01;00;59;29")]
    [InlineData(109692, 30, "01;01;00;02")]
    [InlineData(2589407, 30, "23;59;59;29")]
    [InlineData(3599, 60, "00;00;59;59")]
    [InlineData(3600, 60, "00;01;00;04")]
    [InlineData(35963, 60, "00;09;59;59")]
    [InlineData(35964, 60, "00;10;00;00")]
    [InlineData(215783, 60, "00;59;59;59")]
    [InlineData(215784, 60, "01;00;00;00")]
    public void FormatTimecode_DropFrame_MatchesSmpteVectors(int frame, int fps, string expected)
    {
        Assert.Equal(expected, XmemlExporter.FormatTimecode(frame, fps, dropFrame: true));
    }

    [Theory]
    [InlineData(30, 2)]
    [InlineData(60, 4)]
    public void FormatTimecode_DropFrame_RoundTripsEveryFrameForTwoHours(int fps, int dropped)
    {
        // Inverse of the label, derived independently: every minute not divisible by ten skips
        // `dropped` labels at the top of the minute.
        static int CountOf(string label, int fps, int dropped)
        {
            var p = label.Split(';').Select(int.Parse).ToArray();
            var minutes = p[0] * 60 + p[1];
            return ((p[0] * 3600) + (p[1] * 60) + p[2]) * fps + p[3] - dropped * (minutes - minutes / 10);
        }

        var twoHours = CountOf("02;00;00;00", fps, dropped);
        for (var frame = 0; frame < twoHours; frame++)
        {
            var label = XmemlExporter.FormatTimecode(frame, fps, dropFrame: true);
            var p = label.Split(';').Select(int.Parse).ToArray();
            var skippedLabel = p[2] == 0 && p[3] < dropped && p[1] % 10 != 0;
            Assert.False(skippedLabel, $"frame {frame} produced dropped label {label}");
            Assert.Equal(frame, CountOf(label, fps, dropped));
        }
    }

    [Fact]
    public void FormatTimecode_NonDropMinuteBoundaryIsNominal()
    {
        Assert.Equal("00:01:00:00", XmemlExporter.FormatTimecode(1800, 30, dropFrame: false));
        Assert.Equal("00:00:59:23", XmemlExporter.FormatTimecode(1439, 24, dropFrame: false));
    }

    [Fact]
    public void TimecodeTags_DropFlagOnNonThirtyFamilyBase_FormatsNonDrop()
    {
        // Drop-frame is defined only for the 30 family (29.97, 59.94). A ';' tag on a 24/23.976
        // source must not invent dropped labels.
        var tags = XmemlExporter.TimecodeTags(new SourceTimecode(1440, 24, dropFrame: true), 24, videoNtsc: true);
        Assert.False(tags.DropFrame);
        Assert.Equal("00:01:00:00", tags.String);
        Assert.Equal(1440, tags.Frame);
    }

    [Fact]
    public void TimecodeTags_DropFrameSource_EmitsCountAndMatchingLabel()
    {
        var tags = XmemlExporter.TimecodeTags(new SourceTimecode(1800, 30, dropFrame: true), 30, videoNtsc: true);
        Assert.True(tags.DropFrame);
        Assert.True(tags.Ntsc);
        Assert.Equal(1800, tags.Frame);
        Assert.Equal("00;01;00;02", tags.String);
    }

    // WP-236 E-2: a nested timeline keeps its own timebase, and the carrier converts placement.
    [Theory]
    [InlineData(30, 24)]
    [InlineData(24, 30)]
    public void Render_MixedRateNest_PreservesSecondsTrimsAndFormat(int rootFps, int childFps)
    {
        var (root, lookup, resolver) = MixedRateFixture(rootFps, childFps);
        var startTimecodes = new Dictionary<string, SourceTimecode>
        {
            ["cam"] = new SourceTimecode(3600 * childFps, childFps, dropFrame: false),
        };

        var doc = XDocument.Parse(XmemlExporter.Render(root, resolver, lookup, startTimecodes));

        var sequences = doc.Descendants("sequence").Where(s => s.Element("name") is not null).ToList();
        var rootSeq = sequences.Single(s => s.Element("name")!.Value == "Root");
        var childSeq = sequences.Single(s => s.Element("name")!.Value == "Child");

        Assert.Equal(rootFps, Timebase(rootSeq));
        Assert.Equal(childFps, Timebase(childSeq));
        Assert.Equal(childFps, Timebase(childSeq.Element("timecode")!));
        var childFormat = childSeq.Element("media")!.Element("video")!.Element("format")!.Element("samplecharacteristics")!;
        Assert.Equal(childFps, Timebase(childFormat));
        Assert.Equal("1920", childFormat.Element("width")!.Value);
        Assert.Equal(3.0, Seconds(childSeq, "duration"));

        var inner = doc.Descendants("clipitem").Single(c => (string?)c.Attribute("id") == "clipitem-inner");
        Assert.Equal(childFps, Timebase(inner));
        Assert.Equal(1.0, Seconds(inner, "start"));
        Assert.Equal(3.0, Seconds(inner, "end"));
        Assert.Equal(0.5, Seconds(inner, "in"));
        Assert.Equal(2.5, Seconds(inner, "out"));
        Assert.Equal(10.0, Seconds(inner, "duration"));

        var file = inner.Element("file")!;
        Assert.Equal(childFps, Timebase(file));
        Assert.Equal(10.0, Seconds(file, "duration"));
        var timecode = file.Element("timecode")!;
        Assert.Equal(childFps, Timebase(timecode));
        Assert.Equal((3600 * childFps).ToString(), timecode.Element("frame")!.Value);
        Assert.Equal("01:00:00:00", timecode.Element("string")!.Value);

        var carrier = doc.Descendants("clipitem").Single(c => (string?)c.Attribute("id") == "clipitem-carrier");
        Assert.Equal(rootFps, Timebase(carrier));
        Assert.Equal(1.0, Seconds(carrier, "start"));
        Assert.Equal(3.0, Seconds(carrier, "end"));
        Assert.Equal(0.5, Seconds(carrier, "in"));
        Assert.Equal(2.5, Seconds(carrier, "out"));
        Assert.Equal(3.0, Seconds(carrier, "duration"));
        Assert.Same(childSeq, carrier.Element("sequence"));
    }

    [Fact]
    public void Render_MixedRateMediaFile_DurationUsesTheFilesOwnRate()
    {
        var (root, lookup, resolver) = MixedRateFixture(30, 24);
        var doc = XDocument.Parse(XmemlExporter.Render(root, resolver, lookup));
        var file = doc.Descendants("file").Single(f => f.Element("pathurl") is not null);
        Assert.Equal(24, Timebase(file));
        Assert.Equal("240", file.Element("duration")!.Value);
    }

    private static int Timebase(XElement owner)
        => int.Parse(owner.Element("rate")!.Element("timebase")!.Value);

    private static double Seconds(XElement owner, string name)
        => int.Parse(owner.Element(name)!.Value) / (double)Timebase(owner);

    /// <summary>
    /// Root at <paramref name="rootFps"/> carries a child at <paramref name="childFps"/>. Both place
    /// their clip at 1 s for 2 s with a 0.5 s trim, each in its own frames, so every expectation is
    /// a rate-free number of seconds. The carrier's trim is in root frames — the renderer's rule.
    /// </summary>
    internal static (Timeline Root, Func<string, Timeline?> Lookup, MediaResolver Resolver) MixedRateFixture(
        int rootFps, int childFps)
    {
        var child = new Timeline
        {
            Id = "T-CHILD",
            Name = "Child",
            Fps = childFps,
            Width = 1920,
            Height = 1080,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            Id = "inner",
                            MediaRef = "cam",
                            MediaType = ClipType.video,
                            StartFrame = childFps,
                            DurationFrames = 2 * childFps,
                            TrimStartFrame = childFps / 2,
                        },
                    ],
                },
            ],
        };
        var root = new Timeline
        {
            Id = "T-ROOT",
            Name = "Root",
            Fps = rootFps,
            Width = 1920,
            Height = 1080,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            Id = "carrier",
                            MediaRef = child.Id,
                            MediaType = ClipType.sequence,
                            SourceClipType = ClipType.sequence,
                            StartFrame = rootFps,
                            DurationFrames = 2 * rootFps,
                            TrimStartFrame = rootFps / 2,
                        },
                    ],
                },
            ],
        };
        var manifest = new MediaManifest
        {
            Entries =
            [
                new MediaManifestEntry
                {
                    Id = "cam",
                    Name = "Cam",
                    Type = ClipType.video,
                    Duration = 10,
                    SourceWidth = 1920,
                    SourceHeight = 1080,
                    SourceFps = childFps,
                    HasAudio = false,
                    Source = MediaSource.External(@"C:\media\cam.mp4"),
                },
            ],
        };
        return (root, id => id == child.Id ? child : null, new MediaResolver(() => manifest, () => null));
    }

    [Fact]
    public void Export_WritesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "xmeml-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var timeline = new Timeline
            {
                Fps = 24,
                Width = 640,
                Height = 360,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips = [new Clip { MediaRef = "a", MediaType = ClipType.video, DurationFrames = 24 }],
                    },
                ],
            };
            var resolver = new MediaResolver(
                () => new MediaManifest
                {
                    Entries =
                    [
                        new MediaManifestEntry
                        {
                            Id = "a",
                            Duration = 1,
                            Source = MediaSource.External("/a.mp4"),
                        },
                    ],
                },
                () => null);
            XmemlExporter.Export(timeline, resolver, path);
            Assert.True(File.Exists(path));
            Assert.Contains("xmeml", File.ReadAllText(path));
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
