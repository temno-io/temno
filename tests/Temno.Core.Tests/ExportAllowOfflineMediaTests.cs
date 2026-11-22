using System.Diagnostics;
using System.Text.Json.Nodes;
using Temno.Core.Export;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Hardware;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-170. WP-154 made <c>export_project</c> refuse a video export that would be missing picture, and
/// only the editor had a way through: its warning names the offline media and "Export anyway" lets
/// the render go ahead. <c>allowOfflineMedia</c> is that way through for an agent. The default is
/// unchanged; the opt-in renders what the editor renders and names what was offline and where.
/// </summary>
[Collection("EnvRegistry")]
public sealed class ExportAllowOfflineMediaTests : IDisposable
{
    private const int Fps = 30;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "temno-wp170-" + Guid.NewGuid().ToString("N"));

    public ExportAllowOfflineMediaTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", Path.Combine(_root, "Projects"));
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", Path.Combine(_root, "registry.json"));
    }

    public void Dispose()
    {
        ProjectHub.ActiveSession = null;
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", null);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", null);
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    // ---- the wire, no render ---------------------------------------------------------------------

    [Fact]
    public void Schema_AdvertisesAnOptionalBoolean()
    {
        var schema = McpToolRegistry.Tools.Single(t => t.Name == "export_project").InputSchema;
        var property = schema["properties"]!["allowOfflineMedia"]!;
        Assert.Equal("boolean", property["type"]!.GetValue<string>());
        Assert.Contains("Default false", property["description"]!.GetValue<string>());
        Assert.DoesNotContain("allowOfflineMedia",
            schema["required"]?.AsArray().Select(n => n!.GetValue<string>()) ?? []);
    }

    [Fact]
    public void OtherModes_RefuseTheOptIn_AndAcceptFalse()
    {
        NewProject("Wp170 modes");
        foreach (var mode in new[] { "xml", "fcpxml", "palmier" })
        {
            var error = Fail("export_project", new JsonObject
            {
                ["mode"] = mode,
                ["outputPath"] = Path.Combine(_root, "refused." + mode),
                ["allowOfflineMedia"] = true,
            });
            Assert.Contains("allowOfflineMedia applies only to video mode", error);
        }

        var written = Call("export_project", new JsonObject
        {
            ["mode"] = "fcpxml",
            ["outputPath"] = Path.Combine(_root, "accepted.fcpxml"),
            ["allowOfflineMedia"] = false,
        });
        Assert.Equal("success", written["status"]!.GetValue<string>());
        Assert.Null(written["offlineMedia"]);
    }

    // ---- where the offline media is used ---------------------------------------------------------

    [Fact]
    public void Ranges_TopLevelClips_AreTheirExtents_Merged()
    {
        var timeline = Root(
            VideoTrack(At("c1", "a", 0, 30), At("c2", "b", 30, 30), At("c3", "b", 60, 15), At("c4", "b", 90, 10)));

        Assert.Equal(Spans((30, 75), (90, 100)), ExportMediaRanges.Where(timeline, "b"));
        Assert.Empty(ExportMediaRanges.Where(timeline, "nobody"));
    }

    [Fact]
    public void Ranges_FollowTheSameReachAsThePreflight()
    {
        var hidden = VideoTrack(At("h", "m", 0, 10));
        hidden.Hidden = true;
        var mutedVideo = VideoTrack(At("v", "m", 10, 10));
        mutedVideo.Muted = true;
        var mutedAudio = AudioTrack(At("s", "m", 20, 10, ClipType.audio));
        mutedAudio.Muted = true;
        var audio = AudioTrack(At("a", "m", 30, 10, ClipType.audio));
        var timeline = Root(hidden, mutedVideo, mutedAudio, audio);

        // A hidden track is not exported; a muted video track still shows its picture; a muted audio
        // track reaches nothing; an audio clip on an unmuted track is heard.
        Assert.Equal(Spans((10, 20), (30, 40)), ExportMediaRanges.Where(timeline, "m"));
    }

    /// <summary>
    /// The preflight decides what is offline and the ranges say where it is used, so they must agree on
    /// what the export reaches: a reference has ranges exactly when the preflight collects it.
    /// </summary>
    [Fact]
    public void Ranges_ExistExactlyForWhatThePreflightCollects()
    {
        var nested = new Timeline { Id = "NEST", Fps = 24, Tracks = { VideoTrack(At("inner", "nested-media", 0, 24)) } };
        var hidden = VideoTrack(At("h", "on-hidden", 0, 10));
        hidden.Hidden = true;
        var mutedVideo = VideoTrack(
            At("mv", "muted-video", 0, 10),
            At("mva", "audio-on-muted-video", 10, 10, ClipType.audio),
            At("mvt", "text-on-muted-video", 20, 10, ClipType.text));
        mutedVideo.Muted = true;
        var mutedAudio = AudioTrack(At("ma", "muted-audio", 0, 10, ClipType.audio));
        mutedAudio.Muted = true;
        var carrier = At("carrier", "NEST", 30, 30, ClipType.sequence);
        var video = VideoTrack(
            At("v", "video", 0, 10),
            At("t", "text-with-a-ref", 10, 10, ClipType.text),
            carrier,
            At("gone", "GONE", 60, 10, ClipType.sequence));
        var audio = AudioTrack(At("a", "audio", 0, 10, ClipType.audio));
        var captions = new Track { Type = ClipType.text, Clips = { At("cap", "on-a-text-track", 0, 10, ClipType.text) } };
        var root = Root(hidden, mutedVideo, mutedAudio, video, audio, captions);
        Timeline? Lookup(string id) => id == nested.Id ? nested : null;

        var collected = ExportMediaPreflight.Collect(root, new MediaManifest(), Lookup).Candidates
            .Select(c => c.Reference).Order().ToList();
        var referenced = root.Tracks.Concat(nested.Tracks).SelectMany(t => t.Clips).Select(c => c.MediaRef)
            .Where(r => r != "NEST").Distinct().ToList();
        var withRanges = referenced.Where(r => ExportMediaRanges.Where(root, r, Lookup).Count > 0).Order().ToList();

        Assert.Equal(collected, withRanges);
        Assert.Contains("nested-media", withRanges);
        Assert.DoesNotContain("on-hidden", withRanges);
    }

    [Fact]
    public void Ranges_ATransitionBesideAnOfflineClip_CountsItsWholeWindow()
    {
        var timeline = Root(VideoTrack(At("a1", "a", 0, 30), At("b1", "b", 30, 30)));
        timeline.Transitions.Add(new Transition { ClipIdA = "a1", ClipIdB = "b1", DurationFrames = 10 });

        // The window is centred on the cut, 25..35; the renderer blends from black on the offline side.
        Assert.Equal(Spans((25, 60)), ExportMediaRanges.Where(timeline, "b"));
        Assert.Equal(Spans((0, 35)), ExportMediaRanges.Where(timeline, "a"));
    }

    [Fact]
    public void Ranges_ThroughANestedSequence_UseTheCarriersTrimSpeedAndFrameRate()
    {
        var nested = new Timeline { Id = "NEST", Name = "Nest", Fps = 24, Tracks = { VideoTrack(At("inner", "m", 24, 24)) } };
        var carrier = At("carrier", "NEST", 60, 90, ClipType.sequence);
        carrier.SourceClipType = ClipType.sequence;
        carrier.TrimStartFrame = 11;
        carrier.Speed = 0.75;
        var root = Root(VideoTrack(carrier));

        // Parent frame 60 + k shows nested frame round((11 + 0.75k) × 24 / 30) = round(8.8 + 0.6k). That
        // is in 24..47 from k = 25 (23.8 → 24) to k = 64 (47.2 → 47); k = 24 gives 23 and k = 65 gives
        // 48. Rounding matters at both ends: floor would give 86..126, ceiling 84..124.
        Assert.Equal(Spans((85, 125)), ExportMediaRanges.Where(root, "m", id => id == nested.Id ? nested : null));
    }

    [Fact]
    public void Ranges_TheSameNestedSequenceTwice_IsMappedThroughEachCarrier()
    {
        var nested = new Timeline { Id = "NEST", Fps = Fps, Tracks = { VideoTrack(At("inner", "m", 10, 10)) } };
        var first = At("first", "NEST", 0, 30, ClipType.sequence);
        var second = At("second", "NEST", 100, 30, ClipType.sequence);
        second.TrimStartFrame = 5;
        var root = Root(VideoTrack(first, second));

        Assert.Equal(Spans((10, 20), (105, 115)),
            ExportMediaRanges.Where(root, "m", id => id == nested.Id ? nested : null));
    }

    [Fact]
    public void Ranges_ANestedSequenceThatDoesNotResolve_IsItsCarriersExtent()
    {
        var root = Root(VideoTrack(At("carrier", "GONE", 40, 20, ClipType.sequence)));
        Assert.Equal(Spans((40, 60)), ExportMediaRanges.Where(root, "GONE", _ => null));
    }

    [Fact]
    public void Ranges_StopAtTheNestingCap_AndTerminateOnACycle()
    {
        // Root is depth 0 and the render reaches media at depth 8 (ExportMediaPreflight's rule).
        Timeline Chain(int depth)
        {
            var timelines = new List<Timeline>();
            for (var i = 0; i <= depth; i++)
                timelines.Add(new Timeline { Id = "T" + i, Fps = Fps });
            for (var i = 0; i < depth; i++)
                timelines[i].Tracks.Add(VideoTrack(At("c" + i, "T" + (i + 1), 0, 30, ClipType.sequence)));
            timelines[depth].Tracks.Add(VideoTrack(At("leaf", "m", 0, 30)));
            _chain = timelines.ToDictionary(t => t.Id);
            return timelines[0];
        }

        Assert.Equal(Spans((0, 30)), ExportMediaRanges.Where(Chain(8), "m", id => _chain.GetValueOrDefault(id)));
        Assert.Empty(ExportMediaRanges.Where(Chain(9), "m", id => _chain.GetValueOrDefault(id)));

        var a = new Timeline { Id = "A", Fps = Fps };
        var b = new Timeline { Id = "B", Fps = Fps };
        a.Tracks.Add(VideoTrack(At("ab", "B", 0, 30, ClipType.sequence), At("am", "m", 30, 10)));
        b.Tracks.Add(VideoTrack(At("ba", "A", 0, 30, ClipType.sequence)));
        Assert.Equal(Spans((30, 40)), ExportMediaRanges.Where(a, "m", id => id == "A" ? a : id == "B" ? b : null));
    }

    /// <summary>
    /// WP-170 T1 finding 1. The renderer bounds nesting by depth, not by revisiting a timeline, so a
    /// trimmed cycle reaches media the first pass skipped: A 0..9 shows B 0..9, which shows A 30..39.
    /// </summary>
    [Fact]
    public void Ranges_ACycleIsFollowedAsTheRendererFollowsIt_ToTheDepthCap()
    {
        var a = new Timeline { Id = "A", Fps = Fps };
        var b = new Timeline { Id = "B", Fps = Fps };
        a.Tracks.Add(VideoTrack(
            At("ab", "B", 0, 30, ClipType.sequence), At("am", "m", 30, 10), At("av", "v", 40, 20)));
        var back = At("ba", "A", 0, 30, ClipType.sequence);
        back.TrimStartFrame = 30;
        b.Tracks.Add(VideoTrack(back));

        Assert.Equal(Spans((0, 10), (30, 40)),
            ExportMediaRanges.Where(a, "m", id => id == "A" ? a : id == "B" ? b : null));
    }

    /// <summary>
    /// A forward-playing carrier is searched, not scanned (WP-170 T1 lead). The search must find exactly
    /// the frames a frame-by-frame pass with the renderer's mapping finds, whatever the trim, speed and
    /// frame rates.
    /// </summary>
    [Fact]
    public void Ranges_ThroughAForwardCarrier_MatchAFrameByFramePass()
    {
        var random = new Random(170);
        var fpsChoices = new[] { 24, 25, 30, 50, 60 };
        for (var trial = 0; trial < 300; trial++)
        {
            var nestedFps = fpsChoices[random.Next(fpsChoices.Length)];
            var parentFps = fpsChoices[random.Next(fpsChoices.Length)];
            var innerStart = random.Next(0, 200);
            var nested = new Timeline { Id = "NEST", Fps = nestedFps, Tracks = { VideoTrack(At("inner", "m", innerStart, random.Next(1, 120))) } };
            var carrier = At("carrier", "NEST", random.Next(0, 50), random.Next(1, 400), ClipType.sequence);
            carrier.TrimStartFrame = random.Next(0, 120);
            carrier.Speed = Math.Round(0.1 + random.NextDouble() * 3.9, 3);
            var root = Root(VideoTrack(carrier));

            var inner = nested.Tracks[0].Clips[0];
            var expected = new List<ExportFrameRange>();
            for (var frame = carrier.StartFrame; frame < carrier.EndFrame; frame++)
            {
                var nestedFrame = Math.Max(0, (int)Math.Round(
                    Temno.Media.Playback.TimelineFrameResolver.SourceSecondsAt(carrier, frame, parentFps) * nestedFps));
                if (nestedFrame < inner.StartFrame || nestedFrame >= inner.EndFrame) continue;
                if (expected.Count > 0 && expected[^1].EndFrame == frame) expected[^1] = expected[^1] with { EndFrame = frame + 1 };
                else expected.Add(new ExportFrameRange(frame, frame + 1));
            }

            root.Fps = parentFps;
            Assert.Equal(expected, ExportMediaRanges.Where(root, "m", id => id == nested.Id ? nested : null));
        }
    }

    [Fact]
    public void Ranges_ThroughAStillCarrier_AreItsWholeExtentOrNothing()
    {
        var nested = new Timeline { Id = "NEST", Fps = Fps, Tracks = { VideoTrack(At("inner", "m", 24, 24)) } };
        var carrier = At("carrier", "NEST", 10, 20, ClipType.sequence);
        carrier.Speed = 0;
        var root = Root(VideoTrack(carrier));
        Timeline? Lookup(string id) => id == nested.Id ? nested : null;

        carrier.TrimStartFrame = 30;
        Assert.Equal(Spans((10, 30)), ExportMediaRanges.Where(root, "m", Lookup));
        carrier.TrimStartFrame = 10;
        Assert.Empty(ExportMediaRanges.Where(root, "m", Lookup));
    }

    private Dictionary<string, Timeline> _chain = [];

    // ---- the render --------------------------------------------------------------------------------

    [Fact]
    public async Task Default_RefusesAPartlyOfflineTimeline_AsBefore()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var (_, _, green, _) = await RedThenGreenAsync();
        File.Delete(green);

        foreach (var args in new[] { VideoArgs("refused.mp4"), VideoArgs("refused-false.mp4", allowOfflineMedia: false) })
        {
            var error = Fail("export_project", args);
            Assert.Contains("Export would be missing picture", error);
            Assert.Contains("green clip", error);
            Assert.False(File.Exists(args["outputPath"]!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task OptIn_RendersTheEditorsExportAnyway_AndNamesWhatWasOfflineAndWhere()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var (session, greenRef, green, _) = await RedThenGreenAsync();
        File.Delete(green);

        var result = Call("export_project", VideoArgs("anyway.mp4", allowOfflineMedia: true));
        var output = Path.Combine(_root, "anyway.mp4");
        Assert.Equal("success", result["status"]!.GetValue<string>());
        Assert.True(File.Exists(output));

        var offline = Assert.Single(result["offlineMedia"]!.AsArray())!;
        Assert.Equal(greenRef, offline["mediaRef"]!.GetValue<string>());
        Assert.Equal("green clip", offline["name"]!.GetValue<string>());
        Assert.Equal("missing", offline["state"]!.GetValue<string>());
        Assert.Equal("missing from disk", offline["reason"]!.GetValue<string>());
        var range = Assert.Single(offline["ranges"]!.AsArray())!;
        Assert.Equal(Fps, range["startFrame"]!.GetValue<int>());
        Assert.Equal(2 * Fps, range["endFrame"]!.GetValue<int>());

        var dropped = Assert.Single(result["missingPicture"]!.AsArray())!;
        Assert.Equal(greenRef, dropped["mediaRef"]!.GetValue<string>());
        Assert.Equal("green clip", dropped["name"]!.GetValue<string>());
        Assert.Equal(
            new[]
            {
                "1 media file this timeline uses will not appear in the export: “green clip” (missing from disk).",
                "1 clip is missing from the picture: “green clip”.",
            },
            result["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));

        // The gap is where the ranges say, and the red half is untouched.
        AssertColour(await MeanRgbAtAsync(output, 0.5), red: true);
        AssertColour(await MeanRgbAtAsync(output, 1.5), red: false);

        // Same gaps as the editor: its Export anyway builds this exporter and sets AllowPartialPicture.
        var editorOutput = Path.Combine(_root, "editor-anyway.mp4");
        using (var editor = new TimelineExporter(
                   CompositorFactory.Create(),
                   new ExportFrameSource(),
                   session.Resolver,
                   timelineLookup: SessionTimelineLookup.For(session),
                   acceleration: HardwareAccelerationMode.Software,
                   ownsFrameSource: true) { AllowPartialPicture = true })
        {
            await editor.ExportAsync(
                session.ActiveTimeline, new ExportPreset("Export", VideoCodec.H264, ExportResolution.MatchTimeline), editorOutput);
        }
        var mcpFrames = await FrameHashesAsync(output);
        Assert.Equal(2 * Fps, mcpFrames.Count);
        Assert.Equal(await FrameHashesAsync(editorOutput), mcpFrames);
    }

    [Fact]
    public async Task OptIn_InTheBackground_NamesTheOfflineMediaAtOnce_AndTheDropsWhenDone()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var (_, greenRef, green, _) = await RedThenGreenAsync();
        File.Delete(green);

        var args = VideoArgs("background.mp4", allowOfflineMedia: true);
        args["background"] = true;
        var queued = Call("export_project", args);
        Assert.Equal("queued", queued["status"]!.GetValue<string>());
        var offline = Assert.Single(queued["offlineMedia"]!.AsArray())!;
        Assert.Equal(greenRef, offline["mediaRef"]!.GetValue<string>());
        Assert.Single(queued["warnings"]!.AsArray());
        Assert.Null(queued["missingPicture"]);

        var jobId = queued["jobId"]!.GetValue<string>();
        var row = WaitForRow(jobId);
        Assert.Equal("completed", row["status"]!.GetValue<string>());
        Assert.Equal(2, row["warnings"]!.AsArray().Count);
        var dropped = Assert.Single(row["missingPicture"]!.AsArray())!;
        Assert.Equal("green clip", dropped["name"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_root, "background.mp4")));
    }

    [Fact]
    public async Task OptIn_StillRefusesATimelineWithNoPictureLeft()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var (_, _, green, red) = await RedThenGreenAsync();
        File.Delete(green);
        File.Delete(red);

        var error = Fail("export_project", VideoArgs("black.mp4", allowOfflineMedia: true));
        Assert.Contains("Export produced no picture", error);
        Assert.False(File.Exists(Path.Combine(_root, "black.mp4")));
    }

    /// <summary>
    /// Consent is to a partial picture, as in the editor. An audio track with nothing left to hear
    /// still fails the export, and the preflight's warning is what an agent would have been told.
    /// </summary>
    [Fact]
    public async Task OptIn_StillRefusesAnAudioTrackWithNothingLeftToHear()
    {
        if (!FfmpegFixture.IsAvailable) return;
        await RedThenGreenAsync();
        var tone = Path.Combine(_root, $"tone-{Guid.NewGuid():N}.wav");
        await FfmpegFixture.RunAsync($"-hide_banner -loglevel error -y -f lavfi -i sine=frequency=440:duration=1 \"{tone}\"");
        var imported = Call("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = tone, ["reference"] = true },
            ["name"] = "tone",
        });
        Call("add_clips", new JsonObject
        {
            ["entries"] = new JsonArray
            {
                new JsonObject { ["mediaRef"] = imported["mediaRef"]!.GetValue<string>(), ["startFrame"] = 0, ["endFrame"] = Fps },
            },
        });
        File.Delete(tone);

        var error = Fail("export_project", VideoArgs("silent.mp4", allowOfflineMedia: true));
        Assert.Contains("no usable source audio", error);
    }

    [Fact]
    public async Task OptIn_OnAnOnlineTimeline_OnlyAddsEmptyLists()
    {
        if (!FfmpegFixture.IsAvailable) return;
        await RedThenGreenAsync();

        var plain = Call("export_project", VideoArgs("plain.mp4"));
        var optedIn = Call("export_project", VideoArgs("opted-in.mp4", allowOfflineMedia: true));

        Assert.Null(plain["offlineMedia"]);
        Assert.Null(plain["missingPicture"]);
        Assert.Empty(optedIn["offlineMedia"]!.AsArray());
        Assert.Empty(optedIn["missingPicture"]!.AsArray());
        Assert.Null(optedIn["warnings"]);
        Assert.Equal(
            plain.Select(p => p.Key).Concat(new[] { "missingPicture", "offlineMedia" }).Order(),
            optedIn.Select(p => p.Key).Order());
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>
    /// A project with a red clip at 0..30 and a green clip at 30..60, both imported by reference so
    /// deleting the source takes the asset offline. Returns the green asset's id as the wire shows it.
    /// </summary>
    private async Task<(ProjectSession Session, string GreenRef, string GreenPath, string RedPath)> RedThenGreenAsync()
    {
        var session = NewProject("Wp170 " + Guid.NewGuid().ToString("N")[..6]);
        Call("set_project_settings", new JsonObject { ["width"] = 320, ["height"] = 180 });
        var refs = new List<string>();
        var paths = new List<string>();
        foreach (var colour in new[] { "red", "green" })
        {
            var path = Path.Combine(_root, $"{colour}-{Guid.NewGuid():N}.mp4");
            await FfmpegFixture.RunAsync(
                $"-hide_banner -loglevel error -y -f lavfi -i color=c={colour}:s=320x180:d=1:r={Fps} "
                + $"-c:v libx264 -pix_fmt yuv420p \"{path}\"");
            var imported = Call("import_media", new JsonObject
            {
                ["source"] = new JsonObject { ["path"] = path, ["reference"] = true },
                ["name"] = colour + " clip",
            });
            refs.Add(imported["mediaRef"]!.GetValue<string>());
            paths.Add(path);
        }

        Call("add_clips", new JsonObject
        {
            ["entries"] = new JsonArray
            {
                new JsonObject { ["mediaRef"] = refs[0], ["trackIndex"] = 0, ["startFrame"] = 0, ["endFrame"] = Fps },
                new JsonObject { ["mediaRef"] = refs[1], ["trackIndex"] = 0, ["startFrame"] = Fps, ["endFrame"] = 2 * Fps },
            },
        });
        Assert.Equal(2 * Fps, session.ActiveTimeline.TotalFrames);
        return (session, refs[1], paths[1], paths[0]);
    }

    private JsonObject VideoArgs(string file, bool? allowOfflineMedia = null)
    {
        var args = new JsonObject
        {
            ["mode"] = "video",
            ["codec"] = "H.264",
            ["resolution"] = "Match Timeline",
            ["outputPath"] = Path.Combine(_root, file),
            ["preferHardware"] = false,
        };
        if (allowOfflineMedia is { } allow) args["allowOfflineMedia"] = allow;
        return args;
    }

    private static JsonObject WaitForRow(string jobId)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            var row = Call("manage_exports", new JsonObject { ["action"] = "list" })["exports"]!.AsArray()
                .Single(r => r!["jobId"]!.GetValue<string>() == jobId)!.AsObject();
            if (row["status"]!.GetValue<string>() is "completed" or "failed" or "canceled") return row;
            Thread.Sleep(50);
        }
        throw new TimeoutException($"export {jobId} did not finish");
    }

    private static ProjectSession NewProject(string name)
    {
        Call("new_project", new JsonObject { ["name"] = name, ["fps"] = Fps });
        return ProjectHub.ActiveSession!;
    }

    private static JsonObject Call(string tool, JsonObject args)
    {
        var result = ToolExecutor.Execute(tool, args);
        Assert.False(result.IsError, result.Content[0].Text);
        return JsonNode.Parse(result.Content[0].Text!)!.AsObject();
    }

    private static string Fail(string tool, JsonObject args)
    {
        var result = ToolExecutor.Execute(tool, args);
        Assert.True(result.IsError, result.Content[0].Text);
        return result.Content[0].Text!;
    }

    private static Timeline Root(params Track[] tracks)
    {
        var timeline = new Timeline { Id = "ROOT", Name = "Root", Fps = Fps };
        timeline.Tracks.AddRange(tracks);
        return timeline;
    }

    private static Track VideoTrack(params Clip[] clips) => MakeTrack(ClipType.video, clips);

    private static Track AudioTrack(params Clip[] clips) => MakeTrack(ClipType.audio, clips);

    private static Track MakeTrack(ClipType type, Clip[] clips)
    {
        var track = new Track { Type = type };
        track.Clips.AddRange(clips);
        return track;
    }

    private static ExportFrameRange[] Spans(params (int Start, int End)[] spans)
        => spans.Select(s => new ExportFrameRange(s.Start, s.End)).ToArray();

    private static Clip At(string id, string mediaRef, int start, int duration, ClipType type = ClipType.video)
        => new() { Id = id, MediaRef = mediaRef, MediaType = type, StartFrame = start, DurationFrames = duration };

    private static void AssertColour((int R, int G, int B) rgb, bool red)
    {
        if (red)
            Assert.True(rgb.R > 200 && rgb.G < 40 && rgb.B < 40, $"expected red, measured {rgb}");
        else
            Assert.True(rgb.R < 20 && rgb.G < 20 && rgb.B < 20, $"expected a blank picture, measured {rgb}");
    }

    /// <summary>The frame at <paramref name="seconds"/>, averaged to one pixel.</summary>
    private static async Task<(int R, int G, int B)> MeanRgbAtAsync(string path, double seconds)
    {
        var bytes = await FfmpegStdoutAsync(
            $"-hide_banner -loglevel error -ss {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i \"{path}\" "
            + "-frames:v 1 -vf scale=1:1:flags=area -f rawvideo -pix_fmt rgb24 pipe:1");
        Assert.Equal(3, bytes.Length);
        return (bytes[0], bytes[1], bytes[2]);
    }

    /// <summary>One MD5 per decoded frame: equal lists mean an identical picture, frame for frame.</summary>
    private static async Task<List<string>> FrameHashesAsync(string path)
    {
        var text = System.Text.Encoding.ASCII.GetString(await FfmpegStdoutAsync(
            $"-hide_banner -loglevel error -i \"{path}\" -map 0:v -f framemd5 pipe:1"));
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(line => line.Split(',').Last().Trim())
            .ToList();
    }

    private static async Task<byte[]> FfmpegStdoutAsync(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(FFmpegResolver.GetFFmpegPath(), arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        using var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await copy;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed: {await stderr}");
        return stdout.ToArray();
    }
}
