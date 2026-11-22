using System.Text.Json.Nodes;
using System.Xml.Linq;
using Temno.Core.Export;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-236 E-3: interchange exports never drop markers, transitions or clips silently. What the
/// file does not contain is counted in the returned report; the file itself is checked to agree.
/// </summary>
[Collection("EnvRegistry")]
public sealed class InterchangeExportWarningTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "wp236-" + Guid.NewGuid().ToString("N"));

    public InterchangeExportWarningTests()
    {
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", Path.Combine(_tempDir, "Projects"));
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", Path.Combine(_tempDir, "registry.json"));
    }

    public void Dispose()
    {
        ProjectHub.ActiveSession = null;
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", null);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Xmeml_MarkersAndTransitions_AreReportedAndAbsentFromTheFile()
    {
        var (root, lookup, resolver) = MarkersAndTransitions();

        var (xml, report) = XmemlExporter.RenderWithReport(root, resolver, lookup);

        var markers = Omission(report, InterchangeOmissionKinds.Markers);
        Assert.Equal(3, markers.Count);
        Assert.Contains("Root: 2", markers.Detail);
        Assert.Contains("Child: 1", markers.Detail);
        var transitions = Omission(report, InterchangeOmissionKinds.Transitions);
        Assert.Equal(1, transitions.Count);
        Assert.Contains("Root: 1", transitions.Detail);
        Assert.DoesNotContain(report.Omissions, o => o.Kind == InterchangeOmissionKinds.Clips);
        Assert.Equal("3 markers, 1 transition", report.OmissionSummary);

        var doc = XDocument.Parse(xml);
        Assert.Empty(doc.Descendants("marker"));
        Assert.Empty(doc.Descendants("transitionitem"));
        Assert.Equal(4, doc.Descendants("clipitem").Count()); // a1, b1, carrier, inner
    }

    [Fact]
    public void FcpXml_MarkersAndTransitions_AreReportedAndAbsentFromTheFile()
    {
        var (root, lookup, resolver) = MarkersAndTransitions();

        var (xml, report) = FcpXmlExporter.RenderWithReport(root, resolver, lookup);

        Assert.Equal(3, Omission(report, InterchangeOmissionKinds.Markers).Count);
        Assert.Equal(1, Omission(report, InterchangeOmissionKinds.Transitions).Count);
        Assert.DoesNotContain(report.Omissions, o => o.Kind == InterchangeOmissionKinds.Clips);

        var doc = XDocument.Parse(xml);
        Assert.Empty(doc.Descendants("marker"));
        Assert.Empty(doc.Descendants("transition"));
        Assert.Equal(4, doc.Descendants("asset-clip").Count() + doc.Descendants("ref-clip").Count()); // a1, b1, inner, carrier
    }

    [Fact]
    public void CleanTimeline_HasNoOmissions_ButStillNamesWhatTheFormatNeverCarries()
    {
        var (root, _, resolver) = MarkersAndTransitions();
        root.Markers.Clear();
        root.Transitions.Clear();
        root.Tracks[0].Clips.RemoveAt(2); // the nest carrier

        var xmeml = XmemlExporter.RenderWithReport(root, resolver).Report;
        var fcpxml = FcpXmlExporter.RenderWithReport(root, resolver).Report;

        Assert.Empty(xmeml.Omissions);
        Assert.Null(xmeml.OmissionSummary);
        Assert.Equal("xmeml4", xmeml.Format);
        Assert.Contains("fades", xmeml.NotTransported);
        Assert.Contains("transform (position, scale, rotation)", xmeml.NotTransported);
        Assert.Empty(fcpxml.Omissions);
        Assert.Equal("fcpxml", fcpxml.Format);
        Assert.Contains("effect stacks", fcpxml.NotTransported);
    }

    [Fact]
    public void Xmeml_TextAndUnresolvedNest_AreCountedAsClipsLeftOut()
    {
        var (root, _, resolver) = MarkersAndTransitions();
        root.Markers.Clear();
        root.Transitions.Clear();
        root.Tracks[0].Clips.Add(new Clip
        {
            Id = "title", MediaType = ClipType.text, SourceClipType = ClipType.text,
            TextContent = "Hello", StartFrame = 200, DurationFrames = 30,
        });

        // No lookup: the carrier's child cannot be resolved.
        var (xml, report) = XmemlExporter.RenderWithReport(root, resolver);

        var clips = Omission(report, InterchangeOmissionKinds.Clips);
        Assert.Equal(2, clips.Count);
        Assert.Contains("1 text", clips.Detail);
        Assert.Contains("1 unresolved nested sequence", clips.Detail);
        Assert.Equal(2, XDocument.Parse(xml).Descendants("clipitem").Count());
    }

    [Fact]
    public void FcpXml_LottieAndMissingMedia_AreCountedAsClipsLeftOut_TextIsCarried()
    {
        var (root, lookup, resolver) = MarkersAndTransitions();
        root.Markers.Clear();
        root.Transitions.Clear();
        root.Tracks[0].Clips.AddRange(
        [
            new Clip { Id = "title", MediaType = ClipType.text, SourceClipType = ClipType.text, TextContent = "Hello", StartFrame = 200, DurationFrames = 30 },
            new Clip { Id = "anim", MediaRef = "lottie", MediaType = ClipType.lottie, SourceClipType = ClipType.lottie, StartFrame = 240, DurationFrames = 30 },
            new Clip { Id = "gone", MediaRef = "not-in-manifest", MediaType = ClipType.video, StartFrame = 280, DurationFrames = 30 },
        ]);

        var (xml, report) = FcpXmlExporter.RenderWithReport(root, resolver, lookup);

        var clips = Omission(report, InterchangeOmissionKinds.Clips);
        Assert.Equal(2, clips.Count);
        Assert.Contains("1 Lottie", clips.Detail);
        Assert.Contains("1 missing media", clips.Detail);
        Assert.Single(XDocument.Parse(xml).Descendants("title"));
    }

    [Fact]
    public void Xmeml_VideoOnATextTypedTrack_IsWrittenLikeMac()
    {
        var (root, _, resolver) = MarkersAndTransitions();
        root.Markers.Clear();
        root.Transitions.Clear();
        root.Tracks[0].Clips.RemoveAt(2);
        root.Tracks[0].Type = ClipType.text; // manage_tracks can create one; TrackAccepts lets video in

        var (xml, report) = XmemlExporter.RenderWithReport(root, resolver);

        Assert.Empty(report.Omissions);
        Assert.Equal(2, XDocument.Parse(xml).Descendants("clipitem").Count());
    }

    /// <summary>
    /// WP-236 T1 finding 1: a carrier trimmed past its child's end has nothing to show — a 60-frame
    /// 30 fps child is 48 frames of a 24 fps parent, and this carrier starts reading at 50. Both
    /// formats leave it out and say so; XMEML used to write it with its end before its start.
    /// </summary>
    [Fact]
    public void NestTrimmedPastTheChildsEnd_IsLeftOutAndReported_InBothFormats()
    {
        var child = new Timeline
        {
            Id = "T-CHILD", Name = "Child", Fps = 30, Width = 1920, Height = 1080,
            Tracks = [new Track { Type = ClipType.video, Clips = [new Clip { Id = "inner", MediaRef = "a", MediaType = ClipType.video, DurationFrames = 60 }] }],
        };
        var root = new Timeline
        {
            Id = "T-ROOT", Name = "Root", Fps = 24, Width = 1920, Height = 1080,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips = [new Clip { Id = "carrier", MediaRef = child.Id, MediaType = ClipType.sequence, SourceClipType = ClipType.sequence, StartFrame = 100, TrimStartFrame = 50, DurationFrames = 10 }],
                },
            ],
        };
        var (_, _, resolver) = MarkersAndTransitions();
        Func<string, Timeline?> lookup = id => id == child.Id ? child : null;

        var (xmeml, xmemlReport) = XmemlExporter.RenderWithReport(root, resolver, lookup);
        var (fcpxml, fcpxmlReport) = FcpXmlExporter.RenderWithReport(root, resolver, lookup);

        foreach (var report in new[] { xmemlReport, fcpxmlReport })
        {
            var clips = Omission(report, InterchangeOmissionKinds.Clips);
            Assert.Equal(1, clips.Count);
            Assert.Contains("1 nested sequence trimmed past its end", clips.Detail);
        }
        Assert.Empty(XDocument.Parse(xmeml).Descendants("clipitem"));
        Assert.Empty(XDocument.Parse(fcpxml).Descendants("ref-clip"));
    }

    /// <summary>
    /// WP-236 T1 finding 2: neither format writes speed on a nested carrier (Mac refuses to retime a
    /// nest; Windows lets it happen), so the carrier is written at normal speed and the change is
    /// reported rather than lost.
    /// </summary>
    [Fact]
    public void NestCarrierSpeed_IsWrittenAtNormalSpeedAndReported_InBothFormats()
    {
        var (root, lookup, resolver) = MarkersAndTransitions();
        root.Markers.Clear();
        root.Transitions.Clear();
        root.Tracks[0].Clips[2].Speed = 0.5;

        var (xmeml, xmemlReport) = XmemlExporter.RenderWithReport(root, resolver, lookup);
        var (fcpxml, fcpxmlReport) = FcpXmlExporter.RenderWithReport(root, resolver, lookup);

        foreach (var report in new[] { xmemlReport, fcpxmlReport })
        {
            var speed = Omission(report, InterchangeOmissionKinds.NestedSpeed);
            Assert.Equal(1, speed.Count);
            Assert.Contains("Root: 1", speed.Detail);
            Assert.Equal("1 marker, 1 nested speed change", report.OmissionSummary); // the child keeps its marker
        }
        Assert.Equal(4, XDocument.Parse(xmeml).Descendants("clipitem").Count());
        var fcp = XDocument.Parse(fcpxml);
        Assert.Equal(4, fcp.Descendants("asset-clip").Count() + fcp.Descendants("ref-clip").Count());
    }

    [Theory]
    [InlineData("xml", "xmeml4")]
    [InlineData("fcpxml", null)]
    public void ExportProject_InterchangeResult_CarriesOmissionsAndNotTransported(string mode, string? format)
    {
        var created = ToolExecutor.Execute("new_project", new JsonObject { ["name"] = "Wp236 " + mode, ["fps"] = 30 });
        Assert.False(created.IsError, created.Content?.FirstOrDefault()?.Text);
        var session = ProjectHub.ActiveSession!;
        session.ActiveTimeline.Markers.Add(new Marker { Frame = 15, Name = "hit" });
        session.ActiveTimeline.Markers.Add(new Marker { Frame = 45, Name = "cut" });

        var outputPath = Path.Combine(_tempDir, "out." + mode);
        var result = ToolExecutor.Execute("export_project", new JsonObject { ["mode"] = mode, ["outputPath"] = outputPath });

        Assert.False(result.IsError, result.Content?.FirstOrDefault()?.Text);
        var json = JsonNode.Parse(result.Content![0].Text!)!;
        Assert.Equal("success", json["status"]!.GetValue<string>());
        if (format is not null)
            Assert.Equal(format, json["format"]!.GetValue<string>());
        var omissions = json["omissions"]!.AsArray();
        var markers = Assert.Single(omissions);
        Assert.Equal("markers", markers!["kind"]!.GetValue<string>());
        Assert.Equal(2, markers["count"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(markers["detail"]!.GetValue<string>()));
        Assert.NotEmpty(json["notTransported"]!.AsArray());
        Assert.True(File.Exists(outputPath));
    }

    private static InterchangeOmission Omission(InterchangeExportReport report, string kind)
        => Assert.Single(report.Omissions, o => o.Kind == kind);

    /// <summary>
    /// Root: two markers, a cross-dissolve between adjacent a1 → b1, and a carrier into a child with
    /// one marker. Every clip resolves, so any omission is a marker or a transition.
    /// </summary>
    private static (Timeline Root, Func<string, Timeline?> Lookup, MediaResolver Resolver) MarkersAndTransitions()
    {
        var child = new Timeline
        {
            Id = "T-CHILD", Name = "Child", Fps = 30, Width = 1920, Height = 1080,
            Tracks = [new Track { Type = ClipType.video, Clips = [new Clip { Id = "inner", MediaRef = "a", MediaType = ClipType.video, DurationFrames = 60 }] }],
            Markers = [new Marker { Frame = 10, Name = "child beat" }],
        };
        var root = new Timeline
        {
            Id = "T-ROOT", Name = "Root", Fps = 30, Width = 1920, Height = 1080,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip { Id = "a1", MediaRef = "a", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 60 },
                        new Clip { Id = "b1", MediaRef = "b", MediaType = ClipType.video, StartFrame = 60, DurationFrames = 60 },
                        new Clip { Id = "carrier", MediaRef = child.Id, MediaType = ClipType.sequence, SourceClipType = ClipType.sequence, StartFrame = 120, DurationFrames = 60 },
                    ],
                },
            ],
            Markers = [new Marker { Frame = 0, Name = "open" }, new Marker { Frame = 90, Name = "beat", Note = "cut here" }],
            Transitions = [new Transition { ClipIdA = "a1", ClipIdB = "b1", DurationFrames = 12 }],
        };
        var manifest = new MediaManifest
        {
            Entries =
            [
                Video("a", @"C:\media\a.mp4"),
                Video("b", @"C:\media\b.mp4"),
            ],
        };
        return (root, id => id == child.Id ? child : null, new MediaResolver(() => manifest, () => null));

        static MediaManifestEntry Video(string id, string path) => new()
        {
            Id = id, Name = id, Type = ClipType.video, Duration = 10,
            SourceWidth = 1920, SourceHeight = 1080, SourceFps = 30, HasAudio = false,
            Source = MediaSource.External(path),
        };
    }
}
